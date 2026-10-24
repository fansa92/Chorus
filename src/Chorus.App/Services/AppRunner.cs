using System.Text.Json;
using System.Text.RegularExpressions;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Configuration;
using Chorus.Core.Models;

namespace Chorus.App.Services;

/// <summary>启动/停止编排：生成配置 → 校验 → 拉起核心 → 流量订阅 → 系统代理。</summary>
public sealed partial class AppRunner
{
    [GeneratedRegex(@"^\s*(?<level>INFO|WARN(?:ING)?|ERROR|DEBUG|FATAL)", RegexOptions.IgnoreCase)]
    private static partial Regex LevelRegex();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _trafficCts;
    private bool _coreHandlersAttached;
    private bool _stopRequested;

    public bool IsRunning { get; private set; }

    public event Action<bool>? StatusChanged;
    public event Action<string>? StatusTextChanged;
    public event Action<TrafficSample>? TrafficUpdated;
    public event Action<LogEntry>? LogReceived;

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) return;
            _stopRequested = false;
            StatusTextChanged?.Invoke("正在启动…");

            var settings = AppServices.Settings;
            var corePath = AppServices.ResolveCorePath();
            if (!File.Exists(corePath))
                throw new FileNotFoundException(
                    $"未找到核心：{corePath}。请下载 sing-box.exe（Windows amd64 版）放到该目录，详见 README。");

            var configPath = Path.Combine(AppServices.DataDir, "config.json");
            var workDir = Path.Combine(AppServices.DataDir, "data");
            Directory.CreateDirectory(workDir);

            var config = ConfigGenerator.Build(settings);
            await File.WriteAllTextAsync(
                configPath,
                config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Log("INFO", "正在校验配置…");
            AppServices.Core.CorePath = corePath;
            await AppServices.Core.CheckAsync(configPath);

            AttachCoreHandlers();
            AppServices.Core.Start(configPath, workDir);
            AppServices.Clash.Configure("127.0.0.1", settings.ClashApiPort, settings.ClashApiSecret);

            _trafficCts = new CancellationTokenSource();
            _ = PumpTrafficAsync(_trafficCts.Token);

            if (settings.EnableSystemProxy && !settings.EnableTun)
                SafeDisableProxyOnExit(() => SystemProxyManager.Enable(settings.MixedPort));

            IsRunning = true;
            StatusChanged?.Invoke(true);
            StatusTextChanged?.Invoke("运行中");
            Log("INFO", "核心已启动。");
        }
        catch (Exception ex)
        {
            Log("ERROR", "启动失败：" + ex.Message);
            StatusTextChanged?.Invoke("启动失败");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning) return;
            _stopRequested = true;
            StatusTextChanged?.Invoke("正在停止…");

            var settings = AppServices.Settings;
            if (settings.EnableSystemProxy && !settings.EnableTun)
                SafeDisableProxyOnExit(SystemProxyManager.Disable);

            _trafficCts?.Cancel();
            _trafficCts = null;
            AppServices.Core.Stop();
            DetachCoreHandlers();

            IsRunning = false;
            StatusChanged?.Invoke(false);
            StatusTextChanged?.Invoke("已停止");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PumpTrafficAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var sample in AppServices.Clash.StreamTrafficAsync(ct))
                    TrafficUpdated?.Invoke(sample);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("WARN", "流量连接断开，2 秒后重连：" + ex.Message);
            }

            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void AttachCoreHandlers()
    {
        if (_coreHandlersAttached) return;
        AppServices.Core.LogReceived += ForwardCoreLog;
        AppServices.Core.Exited += OnCoreExited;
        _coreHandlersAttached = true;
    }

    private void DetachCoreHandlers()
    {
        if (!_coreHandlersAttached) return;
        AppServices.Core.LogReceived -= ForwardCoreLog;
        AppServices.Core.Exited -= OnCoreExited;
        _coreHandlersAttached = false;
    }

    private void ForwardCoreLog(string line)
    {
        var level = LevelRegex().Match(line) is { Success: true } match
            ? match.Groups["level"].Value.ToUpperInvariant()
            : "LOG";
        LogReceived?.Invoke(new LogEntry(DateTimeOffset.Now, level, line));
    }

    private void OnCoreExited(int? code)
    {
        if (_stopRequested)
        {
            _stopRequested = false;
            return;
        }

        _trafficCts?.Cancel();
        _trafficCts = null;
        if (AppServices.Settings.EnableSystemProxy && !AppServices.Settings.EnableTun)
            SafeDisableProxyOnExit(SystemProxyManager.Disable);
        DetachCoreHandlers();

        IsRunning = false;
        StatusChanged?.Invoke(false);
        StatusTextChanged?.Invoke($"核心已退出（代码 {code ?? -1}）");
        Log("WARN", $"核心进程异常退出，退出代码 {code ?? -1}");
    }

    private void Log(string level, string message) =>
        LogReceived?.Invoke(new LogEntry(DateTimeOffset.Now, level, message));

    private void SafeDisableProxyOnExit(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log("WARN", "系统代理操作失败：" + ex.Message);
        }
    }
}

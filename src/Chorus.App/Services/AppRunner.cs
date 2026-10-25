using System.Text.Json;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Chorus.Core.Parsing;

namespace Chorus.App.Services;

/// <summary>启动/停止编排：生成配置 → 校验 → 拉起核心 → 流量订阅 → 系统代理。</summary>
public sealed partial class AppRunner
{
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

            var (config, routing) = ConfigGenerator.BuildWithReport(settings);
            await File.WriteAllTextAsync(
                configPath,
                config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Log("INFO", routing.Applied
                ? $"已合并订阅「{routing.SourceName}」的分流规则：{routing.Describe()}"
                : $"分流规则：{routing.Describe()}");

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
        // 核心输出带 ANSI 颜色与时间戳前缀，先规整再算级别
        var parsed = CoreLogParser.Parse(line);
        var level = parsed.Level.Length > 0 ? parsed.Level : "LOG";
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
        LogReceived?.Invoke(new LogEntry(DateTimeOffset.Now, level, message, LogSource.App));

    private void SafeDisableProxyOnExit(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log("WARN", "系统代理操作失败：" + ex.Message);
        }
    }
}

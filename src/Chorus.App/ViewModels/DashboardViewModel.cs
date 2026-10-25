using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Utils;
using Microsoft.UI.Dispatching;

namespace Chorus.App.ViewModels;

/// <summary>
/// 仪表盘（参考 Bettbox 首页小组件）：运行开关、实时网速、累计流量、
/// 出站模式、活动连接、快捷开关。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly DispatcherQueueTimer _timer;
    private int _tick;
    private DateTimeOffset _startedAt;
    private long _totalUp;
    private long _totalDown;
    private bool _toggling;

    // 最近 60 个采样点，供仪表盘绘制迷你流量曲线
    private readonly Queue<double> _upHistory = new(61);
    private readonly Queue<double> _downHistory = new(61);

    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _statusText = "未启动";
    [ObservableProperty] private string _runTimeText = "00:00:00";
    [ObservableProperty] private string _coreInfo = "";

    [ObservableProperty] private string _upSpeedText = "0 B/s";
    [ObservableProperty] private string _downSpeedText = "0 B/s";
    [ObservableProperty] private double _upBarRatio;
    [ObservableProperty] private double _downBarRatio;

    [ObservableProperty] private string _totalUpText = "0 B";
    [ObservableProperty] private string _totalDownText = "0 B";

    [ObservableProperty] private string _modeText = "规则";
    [ObservableProperty] private bool _ruleMode = true;
    [ObservableProperty] private bool _globalMode;
    [ObservableProperty] private bool _directMode;

    [ObservableProperty] private int _connectionCount;

    [ObservableProperty] private bool _systemProxyOn;
    [ObservableProperty] private bool _tunOn;
    [ObservableProperty] private bool _allowLanOn;

    [ObservableProperty] private string _hintText = "";
    [ObservableProperty] private bool _hasHint;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _hasError;

    /// <summary>每次收到流量采样时触发，参数为最近 60 个采样点（字节/秒）。</summary>
    public event Action<(double[] Up, double[] Down)>? SpeedSampled;

    /// <summary>开关卡底部的操作提示。</summary>
    public string StartHint => Running ? "点击卡片停止核心" : "点击卡片启动核心";

    public DashboardViewModel()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        var settings = AppServices.Settings;
        _systemProxyOn = settings.EnableSystemProxy;
        _tunOn = settings.EnableTun;
        _allowLanOn = settings.AllowLan;

        var corePath = AppServices.ResolveCorePath();
        CoreInfo = File.Exists(corePath) ? corePath : $"未找到核心：{corePath}";

        AppServices.Runner.StatusChanged += running => Ui.Post(() =>
        {
            Running = running;
            if (running)
            {
                HasHint = false; // 启动成功后清掉「核心未运行」之类的过期提示
                _startedAt = DateTimeOffset.Now;
                _totalUp = 0;
                _totalDown = 0;
                TotalUpText = "0 B";
                TotalDownText = "0 B";
                _upHistory.Clear();
                _downHistory.Clear();
                _ = RefreshModeAsync();
            }
            else
            {
                RunTimeText = "00:00:00";
                UpSpeedText = "0 B/s";
                DownSpeedText = "0 B/s";
                UpBarRatio = 0;
                DownBarRatio = 0;
                ConnectionCount = 0;
                ApplyMode("rule");
            }
        });
        AppServices.Runner.StatusTextChanged += text => Ui.Post(() => StatusText = text);
        AppServices.Runner.TrafficUpdated += sample => Ui.Post(() => OnTraffic(sample));
    }

    public async Task ToggleAsync(bool turnOn)
    {
        if (_toggling) return;
        _toggling = true;
        HasError = false;
        try
        {
            if (turnOn)
                await AppServices.Runner.StartAsync();
            else
                await AppServices.Runner.StopAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            _toggling = false;
        }
    }

    /// <summary>重启核心（停止后立即启动）。</summary>
    public async Task RestartAsync()
    {
        if (_toggling) return;
        _toggling = true;
        HasError = false;
        try
        {
            if (AppServices.Runner.IsRunning)
                await AppServices.Runner.StopAsync();
            await AppServices.Runner.StartAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            _toggling = false;
        }
    }

    private void OnTraffic(TrafficSample sample)
    {
        UpSpeedText = BytesHumanize.Format(sample.Up) + "/s";
        DownSpeedText = BytesHumanize.Format(sample.Down) + "/s";
        UpBarRatio = ToRatio(sample.Up);
        DownBarRatio = ToRatio(sample.Down);

        _totalUp += sample.Up;
        _totalDown += sample.Down;
        TotalUpText = BytesHumanize.Format(_totalUp);
        TotalDownText = BytesHumanize.Format(_totalDown);

        _upHistory.Enqueue(sample.Up);
        _downHistory.Enqueue(sample.Down);
        if (_upHistory.Count > 60) _upHistory.Dequeue();
        if (_downHistory.Count > 60) _downHistory.Dequeue();
        SpeedSampled?.Invoke((_upHistory.ToArray(), _downHistory.ToArray()));
    }

    private void OnTick()
    {
        if (Running)
        {
            var t = DateTimeOffset.Now - _startedAt;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            RunTimeText = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        _tick++;
        if (!Running) return;
        if (_tick % 2 == 0) _ = RefreshConnectionsAsync();
        if (_tick % 3 == 0) _ = RefreshModeAsync();
    }

    // ---- 出站模式（Clash API PATCH /configs） ----

    public async Task RefreshModeAsync()
    {
        try
        {
            var mode = await AppServices.Clash.GetModeAsync();
            if (mode is null) return;
            Ui.Post(() => ApplyMode(mode));
        }
        catch
        {
            // 核心刚启动或已停止时拉取失败，忽略
        }
    }

    public async Task SetModeAsync(string mode)
    {
        if (!Running)
        {
            ShowHint("核心未运行，先启动后再切换模式");
            return;
        }
        try
        {
            await AppServices.Clash.SetModeAsync(mode);
            Ui.Post(() => ApplyMode(mode));
        }
        catch (Exception ex)
        {
            Ui.Post(() => ShowHint("切换模式失败：" + ex.Message));
        }
    }

    private void ApplyMode(string mode)
    {
        ModeText = mode switch
        {
            "global" => "全局",
            "direct" => "直连",
            _ => "规则",
        };
        RuleMode = mode == "rule";
        GlobalMode = mode == "global";
        DirectMode = mode == "direct";
    }

    // ---- 活动连接 ----

    public async Task RefreshConnectionsAsync()
    {
        try
        {
            var (count, _, _) = await AppServices.Clash.GetConnectionsSummaryAsync();
            Ui.Post(() => ConnectionCount = count);
        }
        catch
        {
            // 轮询失败不打扰用户
        }
    }

    public async Task CloseConnectionsAsync()
    {
        if (!Running) return;
        try
        {
            await AppServices.Clash.CloseConnectionsAsync();
            Ui.Post(() =>
            {
                ConnectionCount = 0;
                ShowHint("已断开全部连接");
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => ShowHint("断开连接失败：" + ex.Message));
        }
    }

    // ---- 快捷开关 ----

    public async Task ToggleSystemProxyAsync(bool on)
    {
        var settings = AppServices.Settings;
        SystemProxyOn = on; // 先跟随 UI，异常时再回弹，避免开关与真实状态不一致
        try
        {
            if (AppServices.Runner.IsRunning)
            {
                if (on) SystemProxyManager.Enable(settings.MixedPort);
                else SystemProxyManager.Disable();
            }

            settings.EnableSystemProxy = on;
            AppServices.Store.Save(settings);

            if (on && !AppServices.Runner.IsRunning)
                ShowHint("核心未运行：系统代理将在核心启动后自动开启");
        }
        catch (Exception ex)
        {
            SystemProxyOn = !on; // 回弹开关，别让界面假装已经生效
            ShowHint("系统代理切换失败：" + ex.Message);
        }
        await Task.CompletedTask;
    }

    public void ToggleTun(bool on)
    {
        var settings = AppServices.Settings;
        settings.EnableTun = on;
        AppServices.Store.Save(settings);
        TunOn = on;
        if (AppServices.Runner.IsRunning)
            ShowHint("TUN 设置已保存，重启核心后生效");
    }

    public void ToggleAllowLan(bool on)
    {
        var settings = AppServices.Settings;
        settings.AllowLan = on;
        AppServices.Store.Save(settings);
        AllowLanOn = on;
        if (AppServices.Runner.IsRunning)
            ShowHint("局域网访问设置已保存，重启核心后生效");
    }

    public void ShowHint(string text)
    {
        HintText = text;
        HasHint = true;
    }

    private static double ToRatio(long bytesPerSec) => Math.Min(1.0, Math.Log10(bytesPerSec + 1) / 7.0);

    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(StartHint));
}

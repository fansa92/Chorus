using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Net;
using Chorus.Core.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Chorus.App.ViewModels;

/// <summary>
/// 仪表盘（排布与逻辑参考 Bettbox 首页的小组件）：
/// 运行状态、流量统计（实时速度 + 累计流量 + 走势曲线）、内核内存、
/// 网络检测（出口 IP / 地区 / 运营商 / 延迟）、出站模式、活动连接、快捷开关。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private static readonly SolidColorBrush OkBrush = new(Color.FromArgb(0xFF, 0x3B, 0xA5, 0x5D));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromArgb(0xFF, 0xD1, 0x8A, 0x2B));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x8C));

    /// <summary>流量曲线：60 个 1 秒采样。</summary>
    private const int SpeedHistoryLength = 60;

    /// <summary>内存曲线：60 个 2 秒采样（约 2 分钟）。</summary>
    private const int MemoryHistoryLength = 60;

    private readonly DispatcherQueueTimer _timer;
    private readonly AutoHide _hintHide;
    private readonly AutoHide _errorHide;

    private int _tick;
    private DateTimeOffset _startedAt;
    private long _totalUp;
    private long _totalDown;
    private long _memoryPeak;
    private bool _toggling;

    private readonly Queue<double> _upHistory = new(SpeedHistoryLength + 1);
    private readonly Queue<double> _downHistory = new(SpeedHistoryLength + 1);
    private readonly Queue<double> _memoryHistory = new(MemoryHistoryLength + 1);

    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _statusText = "未启动";
    [ObservableProperty] private string _runTimeText = "00:00:00";
    [ObservableProperty] private string _coreInfo = "";

    // ---- 流量统计：实时速度 + 累计流量 ----
    [ObservableProperty] private string _upSpeedText = "0 B/s";
    [ObservableProperty] private string _downSpeedText = "0 B/s";
    [ObservableProperty] private string _totalUpText = "0 B";
    [ObservableProperty] private string _totalDownText = "0 B";

    // ---- 内核内存 ----
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _memoryDetailText = "核心未运行";

    // ---- 网络检测 ----
    [ObservableProperty] private string _ipText = "—";
    [ObservableProperty] private string _locationText = "尚未检测";
    [ObservableProperty] private string _ispText = "";
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private SolidColorBrush _latencyBrush = MutedBrush;
    [ObservableProperty] private string _lastDetectText = "—";
    [ObservableProperty] private string _detectStatusText = "点「重新检测」查看出口 IP 与延迟";
    [ObservableProperty] private bool _canDetect = true;

    // ---- 出站模式 / 连接 / 快捷开关 ----
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

    /// <summary>每次采样到内核内存时触发，参数为最近 60 个采样点（字节）。</summary>
    public event Action<double[]>? MemorySampled;

    /// <summary>开关卡底部的操作提示。</summary>
    public string StartHint => Running ? "点击卡片停止核心" : "点击卡片启动核心";

    /// <summary>流量统计卡底部：累计流量（实时速度在上面的两栏里）。</summary>
    public string TotalTrafficText => $"累计上传 {TotalUpText} · 累计下载 {TotalDownText}";

    public DashboardViewModel()
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        _hintHide = new AutoHide(() => HasHint = false);
        _errorHide = new AutoHide(() => HasError = false);

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
                _hintHide.Cancel();
                _startedAt = DateTimeOffset.Now;
                ResetCounters();
                _ = RefreshModeAsync();
                _ = DetectLaterAsync(); // 核心刚起来时 Clash API 还没就绪，等一会儿再测
            }
            else
            {
                RunTimeText = "00:00:00";
                UpSpeedText = "0 B/s";
                DownSpeedText = "0 B/s";
                ResetCounters();
                ConnectionCount = 0;
                MemoryText = "—";
                MemoryDetailText = "核心未运行";
                _memoryHistory.Clear();
                MemorySampled?.Invoke([]);
                ApplyMode("rule");
            }
        });
        AppServices.Runner.StatusTextChanged += text => Ui.Post(() => StatusText = text);
        AppServices.Runner.TrafficUpdated += sample => Ui.Post(() => OnTraffic(sample));
    }

    private void ResetCounters()
    {
        _totalUp = 0;
        _totalDown = 0;
        TotalUpText = "0 B";
        TotalDownText = "0 B";
        _upHistory.Clear();
        _downHistory.Clear();
    }

    public async Task ToggleAsync(bool turnOn)
    {
        if (_toggling) return;
        _toggling = true;
        HasError = false;
        _errorHide.Cancel();
        try
        {
            if (turnOn)
                await AppServices.Runner.StartAsync();
            else
                await AppServices.Runner.StopAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
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
        _errorHide.Cancel();
        try
        {
            if (AppServices.Runner.IsRunning)
                await AppServices.Runner.StopAsync();
            await AppServices.Runner.StartAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
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

        _totalUp += sample.Up;
        _totalDown += sample.Down;
        TotalUpText = BytesHumanize.Format(_totalUp);
        TotalDownText = BytesHumanize.Format(_totalDown);

        _upHistory.Enqueue(sample.Up);
        _downHistory.Enqueue(sample.Down);
        if (_upHistory.Count > SpeedHistoryLength) _upHistory.Dequeue();
        if (_downHistory.Count > SpeedHistoryLength) _downHistory.Dequeue();
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
        if (_tick % 2 == 0)
        {
            _ = RefreshConnectionsAsync();
            UpdateMemory();
        }
        if (_tick % 3 == 0) _ = RefreshModeAsync();
    }

    // ---- 内核内存 ----

    private void UpdateMemory()
    {
        var bytes = AppServices.Core.GetMemoryUsage();
        if (bytes <= 0)
        {
            MemoryDetailText = "等待核心数据…";
            return;
        }

        MemoryText = BytesHumanize.Format(bytes);
        if (bytes > _memoryPeak) _memoryPeak = bytes;

        _memoryHistory.Enqueue(bytes);
        while (_memoryHistory.Count > MemoryHistoryLength) _memoryHistory.Dequeue();

        var average = (long)_memoryHistory.Average();
        MemoryDetailText = $"平均 {BytesHumanize.Format(average)} · 峰值 {BytesHumanize.Format(_memoryPeak)}";
        MemorySampled?.Invoke(_memoryHistory.ToArray());
    }

    // ---- 网络检测 ----

    private async Task DetectLaterAsync()
    {
        // 核心刚起来时入站监听还没就绪，等一会儿再测，否则第一轮必然失败
        await Task.Delay(3000);
        await DetectAsync();
    }

    /// <summary>
    /// 网络检测：量一次 HTTP 往返延迟 + 查出口 IP / 地区 / 运营商。
    /// 核心在跑就走混合端口（看到的是代理出口），否则直连；代理路径整个没响应时再直连兜底一次。
    /// </summary>
    public async Task DetectAsync()
    {
        if (!CanDetect) return;
        CanDetect = false;
        DetectStatusText = "正在检测…";

        var proxy = AppServices.Runner.IsRunning && AppServices.Settings.MixedPort > 0
            ? $"http://127.0.0.1:{AppServices.Settings.MixedPort}"
            : null;

        var result = await ProbeAsync(proxy);
        var via = proxy is null ? "直连" : "经代理";

        if (proxy is not null && result.Info is null && result.Latency is null)
        {
            // 节点没起来 / 代理链路整个不通时，再直连测一次：
            // 至少能说清「是网络本身不通，还是只是代理不通」
            var direct = await ProbeAsync(null);
            if (direct.Info is not null || direct.Latency is not null)
            {
                result = direct;
                via = "直连回退";
            }
        }

        var (latency, info, error) = result;
        Ui.Post(() =>
        {
            if (info is not null)
            {
                IpText = info.Ip;
                LocationText = info.LocationText;
                IspText = info.Isp;
            }
            else
            {
                IpText = "—";
                LocationText = "出口 IP 查询失败";
                IspText = "";
            }

            LatencyText = latency is null ? "—" : $"{latency} ms";
            LatencyBrush = latency switch
            {
                null => MutedBrush,
                < 200 => OkBrush,
                < 500 => WarnBrush,
                _ => ErrorBrush,
            };
            LastDetectText = DateTime.Now.ToString("HH:mm:ss");
            DetectStatusText = error.Length > 0
                ? $"{via}检测出错：{error}"
                : via == "直连回退"
                    ? "代理路径未响应，以上为直连结果 · 点右上「重新检测」"
                    : $"{via}检测 · 点右上「重新检测」刷新";
            CanDetect = true;
        });
    }

    /// <summary>跑一轮探测：延迟与 IP 并行，各自带超时（某个接口被墙不会拖死另一个）。</summary>
    private static async Task<(int? Latency, IpInfo? Info, string Error)> ProbeAsync(string? proxy)
    {
        try
        {
            using var http = CreateHttp(proxy);
            var result = await new NetworkProbe(http).RunAsync();
            return (result.LatencyMs, result.Info, "");
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    private static HttpClient CreateHttp(string? proxy)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
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

    // ---- 提示 ----

    public void ShowHint(string text)
    {
        HintText = text;
        HasHint = true;
        _hintHide.Schedule(AutoHide.ShortDelay);
    }

    public void DismissHint()
    {
        HasHint = false;
        _hintHide.Cancel();
    }

    private void ShowError(string text)
    {
        ErrorMessage = text;
        HasError = true;
        _errorHide.Schedule(AutoHide.LongDelay);
    }

    public void DismissError()
    {
        HasError = false;
        _errorHide.Cancel();
    }

    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(StartHint));
    partial void OnTotalUpTextChanged(string value) => OnPropertyChanged(nameof(TotalTrafficText));
    partial void OnTotalDownTextChanged(string value) => OnPropertyChanged(nameof(TotalTrafficText));
}

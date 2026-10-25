using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Net;
using Chorus.Core.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
    private readonly DispatcherQueue _ui;
    private readonly AutoHide _hintHide;
    private readonly AutoHide _errorHide;

    private int _tick;
    private DateTimeOffset _startedAt;
    private long _totalUp;
    private long _totalDown;
    private long _memoryPeak;
    private bool _toggling;

    /// <summary>检测轮次：切换节点/模式后旧请求的结果可能后到，靠它作废过期结果。</summary>
    private int _detectGeneration;

    /// <summary>检测进行中又来了新的刷新请求（比如连续切换节点），当前这轮结束后补一次。</summary>
    private bool _detectQueued;

    /// <summary>延迟触发的检测（切节点后给核心一点时间让新出站生效）。</summary>
    private CancellationTokenSource? _detectDelayCts;

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

    /// <summary>出口 IP 的来源（ip-api.com / ipwho.is / 本地 GeoIP 库…）。</summary>
    [ObservableProperty] private string _ipSourceText = "";

    // ---- 内网信息（本机网卡地址）----
    [ObservableProperty] private string _localIpv4Text = "—";
    [ObservableProperty] private string _localIpv6Text = "—";
    [ObservableProperty] private string _localDetailText = "";

    /// <summary>有没有拿到 IPv6 地址（没有时隐藏那一行）。</summary>
    public Visibility HasLocalIpv6 => LocalIpv6Text.Length > 0 && LocalIpv6Text != "—"
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>本机 IPv6 是否可用（与 IPv6 开关联动，关掉时提示用户）。</summary>
    [ObservableProperty] private bool _ipv6Disabled;

    /// <summary>出口国家/地区的国旗（查不到归属时为 null，界面显示地球图标）。</summary>
    [ObservableProperty] private ImageSource? _flagImage;

    [ObservableProperty] private bool _hasFlag;

    /// <summary>供 XAML 反向绑定用：没有国旗时显示地球图标。</summary>
    public bool HasNoFlag => !HasFlag;

    // ---- 出站模式 / 连接 / 快捷开关 ----
    [ObservableProperty] private string _modeText = "规则";
    [ObservableProperty] private bool _ruleMode = true;
    [ObservableProperty] private bool _globalMode;
    [ObservableProperty] private bool _directMode;
    [ObservableProperty] private int _connectionCount;

    /// <summary>核心当前所处的出站模式。</summary>
    private string _mode = "rule";

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
        _ui = DispatcherQueue.GetForCurrentThread();
        _timer = _ui.CreateTimer();
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
                DetectLaterAsync(); // 核心刚起来时 Clash API 还没就绪，等一会儿再测
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
                // 核心停了也继续展示网络信息：出口 IP 走直连测，内网信息始终有效
                RequestDetect(0);
            }
        });
        AppServices.Runner.StatusTextChanged += text => Ui.Post(() => StatusText = text);
        AppServices.Runner.TrafficUpdated += sample => Ui.Post(() => OnTraffic(sample));

        RefreshLocalAddresses();

        // 核心没启动也展示网络信息：开屏先直连测一次出口 IP 与延迟。
        // （用户要求：未开启内核时不提示「未开启内核」，直接给出网络信息）
        RequestDetect(0);
    }

    /// <summary>
    /// 采集本机网卡地址（内网信息卡片）。不依赖核心，开关界面就有。
    /// </summary>
    private void RefreshLocalAddresses()
    {
        try
        {
            var addresses = LocalAddressProvider.Collect();
            var ipv4 = LocalAddressProvider.PrimaryIpv4(addresses);
            var ipv6 = LocalAddressProvider.PrimaryIpv6(addresses);

            LocalIpv4Text = ipv4.Length > 0 ? ipv4 : "未检测到";
            LocalIpv6Text = ipv6.Length > 0 ? ipv6 : "未检测到";

            // 网卡数量 + 虚拟网卡提示
            var virtualCount = addresses.Count(a => a.IsVirtual);
            var parts = new List<string> { $"{addresses.Count} 块网卡" };
            if (virtualCount > 0) parts.Add($"{virtualCount} 块为虚拟网卡");
            LocalDetailText = string.Join(" · ", parts);

            OnPropertyChanged(nameof(HasLocalIpv6));
        }
        catch
        {
            LocalIpv4Text = "读取失败";
            LocalIpv6Text = "读取失败";
            LocalDetailText = "";
            OnPropertyChanged(nameof(HasLocalIpv6));
        }
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

    /// <summary>核心刚起来时入站监听还没就绪，等一会儿再测，否则第一轮必然失败。</summary>
    private void DetectLaterAsync() => RequestDetect(3000);

    /// <summary>
    /// 在策略组页切换节点 / 切换出站模式后调用：出口换了，网络检测里的 IP、地区、延迟
    /// 都必须是新节点的。Bettbox 换节点后也是立刻重查一次出口 IP（closeConnections + checkIp）。
    /// </summary>
    public void RefreshAfterNodeSwitch(string? nodeName = null)
    {
        DetectStatusText = nodeName is null
            ? "出口已切换，正在刷新网络信息…"
            : $"已切换到「{nodeName}」，正在刷新网络信息…";
        // 给核心几百毫秒让 selector 的新出站生效，否则测到的可能还是旧链路
        RequestDetect(700);
    }

    /// <summary>延迟发起一轮检测（会作废之前尚未返回的结果）。</summary>
    public void RequestDetect(int delayMs)
    {
        var generation = ++_detectGeneration;
        _detectDelayCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detectDelayCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (cts.IsCancellationRequested || generation != _detectGeneration) return;
            // 回到 UI 线程再改可观察属性：x:Bind 的 OneWay 更新必须在 UI 线程上发生
            _ui.TryEnqueue(() => _ = DetectAsync(generation));
        });
    }

    /// <summary>立即检测（「重新检测」按钮、核心启动后自动调用）。</summary>
    public Task DetectAsync() => DetectAsync(++_detectGeneration);

    /// <summary>
    /// 网络检测：量一次 HTTP 往返延迟 + 查出口 IP / 地区 / 运营商。
    /// 核心在跑就走混合端口（看到的是代理出口），否则直连；代理路径整个没响应时再直连兜底一次。
    /// </summary>
    private async Task DetectAsync(int generation)
    {
        if (!CanDetect)
        {
            // 已经在测：记下来，等这一轮结束再补一轮，别把用户刚切节点后的刷新吞掉
            _detectQueued = true;
            return;
        }
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
            CanDetect = true;

            // 期间又切换了节点：这一轮测的是上个出口，直接丢掉，改用最新一轮的结果
            if (generation != _detectGeneration)
            {
                RunQueuedDetect();
                return;
            }

            if (info is not null)
            {
                IpText = info.Ip;
                LocationText = info.LocationText;
                IspText = info.Isp;
                IpSourceText = info.SourceText;
                FlagImage = Flags.Get(info.FlagCode);
                HasFlag = FlagImage is not null;
            }
            else
            {
                IpText = "—";
                LocationText = "出口 IP 查询失败";
                IspText = "";
                IpSourceText = "";
                FlagImage = null;
                HasFlag = false;
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

            RunQueuedDetect();
        });
    }

    private void RunQueuedDetect()
    {
        if (!_detectQueued) return;
        _detectQueued = false;
        RequestDetect(200);
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
            Ui.Post(() =>
            {
                ApplyMode(mode);
                // 直连 / 全局模式下出口完全变了，网络检测里的 IP 与延迟要跟着刷新
                DetectStatusText = $"已切换到「{ModeText}」模式，正在刷新网络信息…";
                RequestDetect(600);
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => ShowHint("切换模式失败：" + ex.Message));
        }
    }

    private void ApplyMode(string mode)
    {
        _mode = mode;
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

    /// <summary>
    /// 当前出站模式（rule / global / direct）。
    /// RadioButton 的 Checked 事件在「IsChecked 由 VM 回填」时也会触发，
    /// 界面要用它把那种回填和用户真实点击区分开。
    /// </summary>
    public bool IsMode(string mode) => _mode == mode;

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
    partial void OnHasFlagChanged(bool value) => OnPropertyChanged(nameof(HasNoFlag));
    partial void OnTotalUpTextChanged(string value) => OnPropertyChanged(nameof(TotalTrafficText));
    partial void OnTotalDownTextChanged(string value) => OnPropertyChanged(nameof(TotalTrafficText));
}

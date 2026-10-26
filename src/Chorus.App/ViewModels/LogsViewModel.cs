using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Chorus.App.Services;
using Chorus.Core.ClashApi;

namespace Chorus.App.ViewModels;

/// <summary>日志严重级别（用于左侧色条与筛选分组）。</summary>
public enum LogSeverity
{
    Info,
    Warning,
    Error,
    Debug,
}

/// <summary>
/// 日志页（参考 Bettbox 的 logs 页）：内核/应用双来源、级别 + 关键词 + 模块筛选、
/// 错误/警告计数、单条展开与复制、自动滚动与「N 条新日志」浮标。
///
/// 每条连接日志还带一个「链路」标记（Bettbox 请求页的 chain）：策略组 → 节点，
/// 由 <c>/connections</c> 按目标地址配对而来，配对不上时退回日志里的出站 tag。
///
/// 关键点是**入站节流**：核心日志每秒可能几十条，逐条 Ui.Post + Add 会把 UI 线程压住，
/// 所以先进待处理队列，由 250ms 的定时器合批落到集合里。
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 2000;
    private const int FlushIntervalMs = 250;

    /// <summary>活动连接轮询间隔：日志只显示最终出站，「走的哪个策略组」只有 Clash API 的 chains 知道。</summary>
    private const int RoutePollMs = 2000;

    /// <summary>还没匹配上链路的日志行上限（连接早就结束了的行会一直留在这里，靠上限兜住）。</summary>
    private const int MaxPendingRoutes = 400;

    private readonly List<LogItem> _all = [];
    private readonly List<LogItem> _pending = [];
    private readonly List<LogItem> _pendingRoutes = [];
    private readonly object _gate = new();
    private DispatcherQueueTimer? _timer;
    private DispatcherQueueTimer? _routeTimer;
    private Dictionary<string, ConnectionInfo> _routes = new(StringComparer.OrdinalIgnoreCase);
    private bool _routeBusy;
    private long _seq;
    private bool _attached;

    /// <summary>
    /// 当前展示的日志（已应用筛选，时间升序，最后一条最新）。
    /// 只在这个页面挂载期间被修改：页面不在时日志继续进 _all，但不动这里，
    /// 否则已卸载页面的 ItemsRepeater 收到集合通知会抛 E_UNEXPECTED（表现为闪退）。
    /// </summary>
    public ObservableCollection<LogItem> Lines { get; } = [];

    public string[] SourceOptions { get; } = ["全部来源", "内核", "应用"];

    // ---------- 筛选状态 ----------
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showDebug = true;
    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private string _moduleFilter = "";
    [ObservableProperty] private Visibility _moduleFilterVisibility = Visibility.Collapsed;

    /// <summary>有新日志时是否自动滚到底部（默认开；用户往上翻后自动暂停）。</summary>
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>列表当前是否停在底部（由页面在 ViewChanged 里回填）。</summary>
    [ObservableProperty] private bool _isAtBottom = true;

    // ---------- 状态 ----------
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _newCount;
    [ObservableProperty] private string _statusText = "共 0 条";
    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Visible;
    [ObservableProperty] private string _emptyText = "还没有日志，启动核心后这里会实时滚动。";
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private Visibility _toastVisibility = Visibility.Collapsed;

    public string ErrorLabel => ErrorCount > 0 ? $"错误 {ErrorCount}" : "错误";
    public string WarningLabel => WarningCount > 0 ? $"警告 {WarningCount}" : "警告";

    /// <summary>标题右侧的摘要：只有存在错误/警告时才显示，避免和总数重复。</summary>
    public string LedgerText => ErrorCount > 0 || WarningCount > 0
        ? $"· {ErrorCount} 错误 · {WarningCount} 警告"
        : "";
    public Visibility LedgerVisibility => LedgerText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ModuleFilterText => ModuleFilter.Length > 0 ? $"模块：{ModuleFilter}" : "";
    public Visibility NewVisibility => NewCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string NewText => $"↓ {NewCount} 条新日志";

    /// <summary>只有「自动滚动 + 停在底部」时才跟随新日志，否则累计到浮标上。</summary>
    public bool ShouldFollow => AutoScroll && IsAtBottom;

    public LogsViewModel()
    {
        AppServices.Runner.LogReceived += Enqueue;
    }

    // ==================== 日志入站（可跨线程调用） ====================

    private void Enqueue(LogEntry entry)
    {
        lock (_gate)
            _pending.Add(LogItem.From(entry, ++_seq));
        EnsureTimer();
    }

    private void EnsureTimer()
    {
        if (_timer is not null) return;
        Ui.Post(() =>
        {
            if (_timer is not null) return;
            var queue = Ui.Queue;
            if (queue is null) { Flush(); return; }
            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(FlushIntervalMs);
            _timer.Tick += (_, _) => Flush();
            _timer.Start();
        });
    }

    /// <summary>把待处理队列里的日志合批落到集合（UI 线程）。</summary>
    private void Flush()
    {
        List<LogItem> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        var appended = 0;
        foreach (var item in batch)
        {
            _all.Add(item);
            if (item.RouteTarget.Length > 0 || item.Outbound.Length > 0) ApplyRoute(item);
            if (!_attached || !Passes(item)) continue;
            Lines.Add(item);
            appended++;
        }
        if (appended > 0) EmptyVisibility = Visibility.Collapsed;

        TrimToLimit();
        UpdateCounts();
        UpdateStatus();
    }

    /// <summary>页面挂载：之后才允许动展示列表，并把期间累积的日志补上。</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        Rebuild();
        StartRoutePolling();
    }

    /// <summary>页面卸载：停止维护展示列表（旧 repeater 可能还挂着集合通知）。</summary>
    public void Detach()
    {
        _attached = false;
        _routeTimer?.Stop();
        _routeTimer = null;
        _pendingRoutes.Clear();
    }

    // ==================== 链路（走的是哪个策略组的哪个节点） ====================

    /// <summary>
    /// sing-box 的日志只写到最终出站（<c>outbound/vless[HK 01]</c>），不知道它属于哪个策略组、
    /// 命中了哪条规则；这些在 Clash API 的 <c>/connections</c> 的 chains / rule 里。
    /// 页面开着时每 2 秒取一次活动连接，按目标地址把两者对上。
    /// </summary>
    private void StartRoutePolling()
    {
        if (_routeTimer is not null) return;
        var queue = Ui.Queue;
        if (queue is null) return;
        _routeTimer = queue.CreateTimer();
        _routeTimer.Interval = TimeSpan.FromMilliseconds(RoutePollMs);
        _routeTimer.Tick += async (_, _) => await RefreshRoutesAsync();
        _routeTimer.Start();
    }

    private async Task RefreshRoutesAsync()
    {
        if (_routeBusy || !_attached) return;
        if (!AppServices.Runner.IsRunning)
        {
            _routes = new Dictionary<string, ConnectionInfo>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        _routeBusy = true;
        try
        {
            var map = new Dictionary<string, ConnectionInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var conn in await AppServices.Clash.GetConnectionsAsync())
                foreach (var key in conn.Targets)
                    map.TryAdd(key, conn); // 同一目标多条连接时留第一条
            _routes = map;

            for (var i = _pendingRoutes.Count - 1; i >= 0; i--)
            {
                var item = _pendingRoutes[i];
                if (item.HasExactRoute || !_routes.TryGetValue(item.RouteTarget, out var conn)) continue;
                ApplyExact(item, conn);
                _pendingRoutes.RemoveAt(i);
            }
        }
        catch
        {
            // 核心刚停或 API 还没就绪：保持现状，下一轮再试
        }
        finally
        {
            _routeBusy = false;
        }
    }

    /// <summary>
    /// 给一条日志定链路：先按目标地址查活动连接（能拿到完整的「策略组 → 节点」和命中规则）；
    /// 查不到（连接刚开始或已经结束）就退回日志里的那个出站 tag，标成近似链路。
    /// </summary>
    private void ApplyRoute(LogItem item)
    {
        if (item.RouteTarget.Length > 0 && _routes.TryGetValue(item.RouteTarget, out var conn))
        {
            ApplyExact(item, conn);
            return;
        }

        if (item.Outbound.Length > 0) item.SetRoute(item.Outbound, "", false);
        if (item.RouteTarget.Length > 0 && _pendingRoutes.Count < MaxPendingRoutes) _pendingRoutes.Add(item);
    }

    /// <summary>用活动连接补上精确链路；链路上没有出站（异常载荷）时退回规则文本。</summary>
    private static void ApplyExact(LogItem item, ConnectionInfo conn) =>
        item.SetRoute(conn.ChainText.Length > 0 ? conn.ChainText : conn.Rule, conn.Rule, true);

    private void TrimToLimit()
    {
        if (_all.Count <= MaxLines) return;
        var overflow = _all.Count - MaxLines;
        var removed = _all.GetRange(0, overflow);
        _all.RemoveRange(0, overflow);
        if (!_attached || Lines.Count == 0) return;
        // 展示列表是 _all 的有序子集，被丢弃的最旧几条若在列表里，同步去掉前缀
        var drop = 0;
        while (drop < Lines.Count && drop < removed.Count && ReferenceEquals(Lines[drop], removed[drop]))
            drop++;
        for (var i = 0; i < drop; i++)
            Lines.RemoveAt(0);
    }

    // ==================== 筛选 ====================

    private bool Passes(LogItem item) =>
        PassesLevel(item) && InScope(item);

    private bool PassesLevel(LogItem item) => item.Severity switch
    {
        LogSeverity.Error => ShowError,
        LogSeverity.Warning => ShowWarning,
        LogSeverity.Debug => ShowDebug,
        _ => ShowInfo,
    };

    /// <summary>关键词 / 来源 / 模块三个条件（不含级别，用于计数）。</summary>
    private bool InScope(LogItem item)
    {
        if (SourceIndex == 1 && item.Source != LogSource.Core) return false;
        if (SourceIndex == 2 && item.Source != LogSource.App) return false;
        if (ModuleFilter.Length > 0
            && !item.Module.Equals(ModuleFilter, StringComparison.OrdinalIgnoreCase)) return false;

        var search = SearchText.Trim();
        if (search.Length == 0) return true;
        foreach (var token in search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!item.SearchText.Contains(token, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private void Rebuild()
    {
        // 页面不在时只更新计数，不动展示列表（见 Lines 的注释）
        if (!_attached)
        {
            UpdateCounts();
            UpdateStatus();
            return;
        }

        Lines.Clear();
        foreach (var item in _all)
            if (Passes(item))
                Lines.Add(item);

        UpdateCounts();
        UpdateStatus();
        ClearNew();
        EmptyVisibility = Lines.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText = _all.Count == 0
            ? "还没有日志，启动核心后这里会实时滚动。"
            : "没有符合当前筛选条件的日志。";
        if (ShouldFollow) ScrollToEndRequested?.Invoke();
    }

    private void UpdateCounts()
    {
        int errors = 0, warnings = 0, total = 0;
        foreach (var item in _all)
        {
            if (!InScope(item)) continue;
            total++;
            if (item.Severity == LogSeverity.Error) errors++;
            else if (item.Severity == LogSeverity.Warning) warnings++;
        }
        ErrorCount = errors;
        WarningCount = warnings;
        TotalCount = total;
    }

    private void UpdateStatus()
    {
        var shown = Lines.Count;
        StatusText = shown == _all.Count
            ? $"共 {_all.Count} 条"
            : $"显示 {shown} / {_all.Count} 条";
        OnPropertyChanged(nameof(ErrorLabel));
        OnPropertyChanged(nameof(WarningLabel));
        OnPropertyChanged(nameof(LedgerText));
        OnPropertyChanged(nameof(LedgerVisibility));
    }

    // 筛选属性变化 → 重建列表
    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnShowErrorChanged(bool value) => Rebuild();
    partial void OnShowWarningChanged(bool value) => Rebuild();
    partial void OnShowInfoChanged(bool value) => Rebuild();
    partial void OnShowDebugChanged(bool value) => Rebuild();
    partial void OnSourceIndexChanged(int value) => Rebuild();

    partial void OnModuleFilterChanged(string value)
    {
        ModuleFilterVisibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(ModuleFilterText));
        Rebuild();
    }

    partial void OnNewCountChanged(int value)
    {
        OnPropertyChanged(nameof(NewVisibility));
        OnPropertyChanged(nameof(NewText));
    }

    // ==================== 供页面调用 ====================

    /// <summary>页面在列表滚动时回填是否停在底部。</summary>
    public void MarkAtBottom(bool atBottom)
    {
        IsAtBottom = atBottom;
        if (atBottom) ClearNew();
    }

    /// <summary>新增了 n 条日志但用户正翻在上面，累计到浮标。</summary>
    public void ReportNew(int count)
    {
        if (count <= 0) return;
        NewCount += count;
    }

    public void ClearNew() => NewCount = 0;

    /// <summary>页面请求滚到底部（筛选变化 / 点浮标时）。</summary>
    public event Action? ScrollToEndRequested;

    public void RaiseScrollToEnd() => ScrollToEndRequested?.Invoke();

    public void ApplyModuleFilter(string module)
    {
        if (module.Length == 0) return;
        ModuleFilter = module;
    }

    public void ClearModuleFilter() => ModuleFilter = "";

    /// <summary>当前筛选结果的纯文本快照（复制 / 导出用）。</summary>
    public string Snapshot()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in Lines)
            sb.AppendLine(item.ToPlainText());
        return sb.ToString();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _all.Clear();
        }
        if (_attached) Lines.Clear();
        _pendingRoutes.Clear();
        ClearNew();
        UpdateCounts();
        UpdateStatus();
        EmptyVisibility = Visibility.Visible;
        EmptyText = "还没有日志，启动核心后这里会实时滚动。";
        ShowToast("日志已清空");
    }

    public void ShowToast(string text)
    {
        ToastText = text;
        ToastVisibility = Visibility.Visible;
        var queue = Ui.Queue;
        if (queue is null) return;
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2.5);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { ToastVisibility = Visibility.Collapsed; timer.Stop(); };
        timer.Start();
    }
}

/// <summary>
/// 一条日志的展示模型：解析出级别 / 模块 / 正文 / 出站与目标地址，
/// 预先算好配色与展开态；链路（策略组 → 节点）由页面在拿到活动连接后补上。
/// </summary>
public sealed partial class LogItem : ObservableObject
{
    private static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD9, 0x3A, 0x3F));
    private static readonly SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xC7, 0x7E, 0x12));
    private static readonly SolidColorBrush DebugBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush InfoBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0x8D, 0xF6));
    private static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0x00, 0, 0, 0));
    private static readonly SolidColorBrush ErrorBgBrush = new(Windows.UI.Color.FromArgb(0x16, 0xD9, 0x3A, 0x3F));
    private static readonly SolidColorBrush WarningBgBrush = new(Windows.UI.Color.FromArgb(0x16, 0xC7, 0x7E, 0x12));
    private static readonly SolidColorBrush ErrorBadgeBrush = new(Windows.UI.Color.FromArgb(0x24, 0xD9, 0x3A, 0x3F));
    private static readonly SolidColorBrush WarningBadgeBrush = new(Windows.UI.Color.FromArgb(0x24, 0xC7, 0x7E, 0x12));
    private static readonly SolidColorBrush NeutralBadgeBrush = new(Windows.UI.Color.FromArgb(0x1A, 0x8A, 0x8A, 0x8A));

    private static readonly SolidColorBrush RouteBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6));
    private static readonly SolidColorBrush RouteBgBrush = new(Windows.UI.Color.FromArgb(0x22, 0x8B, 0x5C, 0xF6));

    public DateTimeOffset Time { get; }
    public string Level { get; }
    public string Module { get; }
    public string Message { get; }
    public LogSource Source { get; }
    public long Seq { get; }
    public LogSeverity Severity { get; }

    /// <summary>日志里提到的出站 tag（<c>outbound/vless[HK 01]</c> 的 HK 01），查不到链路时的兜底。</summary>
    public string Outbound { get; }

    /// <summary>这一行的目标地址（host:port / ip:port），用来匹配活动连接；没有则为空。</summary>
    public string RouteTarget { get; }

    /// <summary>检索串（含链路与规则，所以搜「HK 01」也能捞出这批连接的全部日志）。</summary>
    public string SearchText => $"{Level} {Module} {Message} {SourceText} {Route} {Rule}";

    public string TimeText => Time.LocalDateTime.ToString("HH:mm:ss");
    public string FullTimeText => Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string SourceText => Source == LogSource.App ? "应用" : "内核";
    public string ModuleText => Module.Length > 0 ? Module : SourceText;
    public string MetaText => $"{FullTimeText} · {SourceText} · {Level}"
        + (Module.Length > 0 ? $" · {Module}" : "")
        + (Route.Length > 0 ? $" · 链路：{Route}{(HasExactRoute ? "" : "（按日志里的出站）")}" : "")
        + (Rule.Length > 0 ? $" · 规则：{Rule}" : "");

    public bool HasModule => Module.Length > 0;

    public Brush BarBrush => Severity switch
    {
        LogSeverity.Error => ErrorBrush,
        LogSeverity.Warning => WarningBrush,
        _ => TransparentBrush,
    };

    public Brush RowBackground => Severity switch
    {
        LogSeverity.Error => ErrorBgBrush,
        LogSeverity.Warning => WarningBgBrush,
        _ => TransparentBrush,
    };

    public Brush LevelBrush => Severity switch
    {
        LogSeverity.Error => ErrorBrush,
        LogSeverity.Warning => WarningBrush,
        LogSeverity.Debug => DebugBrush,
        _ => InfoBrush,
    };

    public Brush LevelBadgeBackground => Severity switch
    {
        LogSeverity.Error => ErrorBadgeBrush,
        LogSeverity.Warning => WarningBadgeBrush,
        _ => NeutralBadgeBrush,
    };

    public Brush ModuleBrush => MutedBrush;
    public Brush ModuleBackground => NeutralBadgeBrush;

    /// <summary>这一条连接走的链路：「策略组 → 节点」（来自 Clash API），或退化成日志里的出站 tag。</summary>
    [ObservableProperty] private string _route = "";

    /// <summary>命中的路由规则（只有匹配到活动连接时才有）。</summary>
    [ObservableProperty] private string _rule = "";

    /// <summary>链路是否来自活动连接（true），还是只从日志里的出站 tag 推出来的。</summary>
    public bool HasExactRoute { get; private set; }

    public Visibility RouteVisibility => Route.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string RouteTooltip => HasExactRoute
        ? Rule.Length > 0 ? $"这条连接的链路：{Route}\n命中规则：{Rule}" : $"这条连接的链路：{Route}"
        : $"日志里的出站：{Route}（连接已结束，查不到属于哪个策略组）";
    public Brush RouteForeground => HasExactRoute ? RouteBrush : MutedBrush;
    public Brush RouteBackground => HasExactRoute ? RouteBgBrush : NeutralBadgeBrush;

    public void SetRoute(string route, string rule, bool exact)
    {
        if (exact) HasExactRoute = true;
        else if (HasExactRoute) return; // 已经有精确链路了，别用日志里的出站把它盖掉
        if (route.Length == 0 && rule.Length == 0) return;
        Rule = rule;
        Route = route;
    }

    [ObservableProperty] private bool _isExpanded;

    public TextWrapping MessageWrapping => IsExpanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public int MessageMaxLines => IsExpanded ? 0 : 1;
    public Visibility DetailVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    private LogItem(
        DateTimeOffset time, string level, string module, string message, LogSource source, long seq,
        string outbound, string target)
    {
        Time = time;
        Level = level;
        Module = module;
        Message = message;
        Source = source;
        Seq = seq;
        Outbound = outbound;
        RouteTarget = target;
        Severity = Classify(level);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(MessageWrapping));
        OnPropertyChanged(nameof(MessageMaxLines));
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(ExpandGlyph));
    }

    partial void OnRouteChanged(string value)
    {
        OnPropertyChanged(nameof(RouteVisibility));
        OnPropertyChanged(nameof(RouteTooltip));
        OnPropertyChanged(nameof(RouteForeground));
        OnPropertyChanged(nameof(RouteBackground));
        OnPropertyChanged(nameof(MetaText));
    }

    partial void OnRuleChanged(string value) => OnPropertyChanged(nameof(MetaText));

    public string ToPlainText() => $"{FullTimeText} {Level,-5} {ModuleText,-10} {Message}"
        + (Route.Length > 0 ? $" [{Route}{(Rule.Length > 0 ? $" / {Rule}" : "")}]" : "");

    public static LogItem From(LogEntry entry, long seq)
    {
        var parsed = Chorus.Core.Parsing.CoreLogParser.Parse(entry.Message);
        var level = parsed.Level.Length > 0
            ? parsed.Level
            : (string.IsNullOrWhiteSpace(entry.Level) ? "LOG" : entry.Level.ToUpperInvariant());
        return new LogItem(entry.Time, level, parsed.Module, parsed.Message, entry.Source, seq,
            parsed.Outbound, parsed.Target);
    }

    private static LogSeverity Classify(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" or "FATAL" or "PANIC" => LogSeverity.Error,
        "WARN" or "WARNING" => LogSeverity.Warning,
        "DEBUG" or "TRACE" => LogSeverity.Debug,
        _ => LogSeverity.Info,
    };
}

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
/// 关键点是**入站节流**：核心日志每秒可能几十条，逐条 Ui.Post + Add 会把 UI 线程压住，
/// 所以先进待处理队列，由 250ms 的定时器合批落到集合里。
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 2000;
    private const int FlushIntervalMs = 250;

    private readonly List<LogItem> _all = [];
    private readonly List<LogItem> _pending = [];
    private readonly object _gate = new();
    private DispatcherQueueTimer? _timer;
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
    }

    /// <summary>页面卸载：停止维护展示列表（旧 repeater 可能还挂着集合通知）。</summary>
    public void Detach() => _attached = false;

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

/// <summary>一条日志的展示模型：解析出级别 / 模块 / 正文，并预先算好配色与展开态。</summary>
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

    public DateTimeOffset Time { get; }
    public string Level { get; }
    public string Module { get; }
    public string Message { get; }
    public LogSource Source { get; }
    public long Seq { get; }
    public LogSeverity Severity { get; }

    /// <summary>预先拼好的小写检索串，筛选时避免逐次拼接。</summary>
    public string SearchText { get; }

    public string TimeText => Time.LocalDateTime.ToString("HH:mm:ss");
    public string FullTimeText => Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string SourceText => Source == LogSource.App ? "应用" : "内核";
    public string ModuleText => Module.Length > 0 ? Module : SourceText;
    public string MetaText => $"{FullTimeText} · {SourceText} · {Level}"
        + (Module.Length > 0 ? $" · {Module}" : "");

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

    [ObservableProperty] private bool _isExpanded;

    public TextWrapping MessageWrapping => IsExpanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public int MessageMaxLines => IsExpanded ? 0 : 1;
    public Visibility DetailVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    private LogItem(DateTimeOffset time, string level, string module, string message, LogSource source, long seq)
    {
        Time = time;
        Level = level;
        Module = module;
        Message = message;
        Source = source;
        Seq = seq;
        Severity = Classify(level);
        SearchText = $"{level} {module} {message} {source}";
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(MessageWrapping));
        OnPropertyChanged(nameof(MessageMaxLines));
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(ExpandGlyph));
    }

    public string ToPlainText() => $"{FullTimeText} {Level,-5} {ModuleText,-10} {Message}";

    public static LogItem From(LogEntry entry, long seq)
    {
        var parsed = Chorus.Core.Parsing.CoreLogParser.Parse(entry.Message);
        var level = parsed.Level.Length > 0
            ? parsed.Level
            : (string.IsNullOrWhiteSpace(entry.Level) ? "LOG" : entry.Level.ToUpperInvariant());
        return new LogItem(entry.Time, level, parsed.Module, parsed.Message, entry.Source, seq);
    }

    private static LogSeverity Classify(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" or "FATAL" or "PANIC" => LogSeverity.Error,
        "WARN" or "WARNING" => LogSeverity.Warning,
        "DEBUG" or "TRACE" => LogSeverity.Debug,
        _ => LogSeverity.Info,
    };
}

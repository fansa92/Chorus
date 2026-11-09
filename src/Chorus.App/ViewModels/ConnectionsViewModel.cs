using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Chorus.App.Services;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Utils;

namespace Chorus.App.ViewModels;

/// <summary>连接列表里的一行。字段全是可绑定文本，避免 XAML 里做转换。</summary>
public sealed partial class ConnectionRowViewModel : ObservableObject
{
    public ConnectionRowViewModel(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _processText = "";
    [ObservableProperty] private string _ruleText = "";
    [ObservableProperty] private string _chainText = "";
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _networkText = "";
    [ObservableProperty] private string _uploadText = "";
    [ObservableProperty] private string _downloadText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _durationText = "";

    public void Update(ConnectionInfo c, long upPerSec, long downPerSec, DateTimeOffset now, string[] labels)
    {
        var target = c.Host.Length > 0 ? c.Host : c.DestinationIp;
        TargetText = target.Length > 0 ? $"{target}:{c.DestinationPort}" : c.DestinationPort;
        DetailText = string.IsNullOrEmpty(c.DestinationIp) || c.DestinationIp == c.Host
            ? $"{c.SourceIp}:{c.SourcePort}"
            : $"{c.DestinationIp}:{c.DestinationPort} · {c.SourceIp}:{c.SourcePort}";
        ProcessText = c.ProcessName;
        RuleText = c.RulePayload.Length > 0 ? $"{c.Rule} · {c.RulePayload}" : c.Rule;
        ChainText = c.ChainText;
        KindText = ConnectionQuery.KindOf(c) switch
        {
            ConnectionKind.Proxy => labels[0],
            ConnectionKind.Direct => labels[1],
            ConnectionKind.Blocked => labels[2],
            _ => labels[3],
        };
        NetworkText = c.Network.ToUpperInvariant();
        UploadText = BytesHumanize.Format(c.Upload);
        DownloadText = BytesHumanize.Format(c.Download);
        SpeedText = $"↑ {BytesHumanize.Format(upPerSec)}/s · ↓ {BytesHumanize.Format(downPerSec)}/s";
        DurationText = ConnectionQuery.FormatDuration(c.Start, now);
    }
}

/// <summary>
/// 连接管理页：轮询 Clash API /connections，做筛选、排序、汇总与逐条断开。
/// <para>
/// 列表按 Id 增量更新而不是每轮 Clear 重建 —— 否则 2 秒一次的刷新会让
/// 滚动位置和鼠标焦点一直跳，断开按钮也点不准。
/// </para>
/// </summary>
public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly record struct Sample(long Up, long Down, DateTimeOffset At);

    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<string, Sample> _previous = new(StringComparer.Ordinal);
    private readonly AutoHide _hintHide;
    private bool _active;
    private bool _refreshing;

    public ConnectionsViewModel()
    {
        _hintHide = new AutoHide(() => HasHint = false);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        // 「自动刷新」只管定时轮询；手动刷新和改筛选条件时照常拉一次
        _timer.Tick += (_, _) => { if (AutoRefresh) _ = RefreshAsync(); };

        Loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(RefreshLabel));
            OnPropertyChanged(nameof(CloseAllLabel));
            OnPropertyChanged(nameof(AutoRefreshLabel));
            OnPropertyChanged(nameof(KindOptions));
            OnPropertyChanged(nameof(NetworkOptions));
            OnPropertyChanged(nameof(SortOptions));
            OnPropertyChanged(nameof(EmptyText));
        };
    }

    /// <summary>页面可见时才轮询；页面切走后立刻停，别在后台白刷 Clash API。</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value)
            {
                _timer.Start();
                _ = RefreshAsync();
            }
            else
            {
                _timer.Stop();
                _previous.Clear();
            }
        }
    }

    public ObservableCollection<ConnectionRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private int _networkIndex;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _hintText = "";
    [ObservableProperty] private bool _hasHint;

    public Visibility EmptyVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(EmptyVisibility));

    public string TitleText => Loc.T("conn.title");
    public string SearchPlaceholder => Loc.T("conn.search");
    public string RefreshLabel => Loc.T("common.refresh");
    public string CloseAllLabel => Loc.T("conn.closeAll");
    public string AutoRefreshLabel => Loc.T("conn.autoRefresh");
    public string EmptyText => Loc.T("conn.empty");
    public string SummaryTitle => Loc.T("conn.summary");

    public string[] KindOptions =>
    [
        Loc.T("conn.filter.all"), Loc.T("conn.filter.proxy"),
        Loc.T("conn.filter.direct"), Loc.T("conn.filter.blocked"),
    ];

    public string[] NetworkOptions =>
    [
        Loc.T("conn.filter.all"), "TCP", "UDP",
    ];

    public string[] SortOptions =>
    [
        Loc.T("conn.sort.recent"), Loc.T("conn.sort.download"),
        Loc.T("conn.sort.upload"), Loc.T("conn.sort.process"),
    ];

    // 筛选/排序变化后立刻重排，不用等下一个 2 秒轮询
    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();
    partial void OnKindIndexChanged(int value) => _ = RefreshAsync();
    partial void OnNetworkIndexChanged(int value) => _ = RefreshAsync();
    partial void OnSortIndexChanged(int value) => _ = RefreshAsync();

    private ConnectionKind SelectedKind => KindIndex switch
    {
        1 => ConnectionKind.Proxy,
        2 => ConnectionKind.Direct,
        3 => ConnectionKind.Blocked,
        _ => ConnectionKind.All,
    };

    private string? SelectedNetwork => NetworkIndex switch
    {
        1 => "tcp",
        2 => "udp",
        _ => null,
    };

    private ConnectionSort SelectedSort => SortIndex switch
    {
        1 => ConnectionSort.Download,
        2 => ConnectionSort.Upload,
        3 => ConnectionSort.Process,
        _ => ConnectionSort.Recent,
    };

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_active || _refreshing) return;
        if (!AppServices.Runner.IsRunning)
        {
            Rows.Clear();
            _previous.Clear();
            StatusText = Loc.T("conn.coreStopped");
            SummaryText = "";
            IsEmpty = true;
            return;
        }

        _refreshing = true;
        try
        {
            var connections = await AppServices.Clash.GetConnectionsAsync();
            var now = DateTimeOffset.Now;
            var summary = ConnectionQuery.Summarize(connections);
            var visible = ConnectionQuery.Apply(
                connections, SearchText, SelectedKind, SelectedNetwork, SelectedSort);

            Ui.Post(() =>
            {
                ApplyRows(visible, now);
                StatusText = Loc.T("conn.status", visible.Count, connections.Count);
                SummaryText = Loc.T("conn.summaryText",
                    BytesHumanize.Format(summary.Upload), BytesHumanize.Format(summary.Download),
                    summary.TopProcess.Length > 0 ? summary.TopProcess : Loc.T("common.none"));
                IsEmpty = Rows.Count == 0;
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = Loc.T("conn.failed", ex.Message));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>按 Id 就地更新 / 移动 / 增删，保持滚动位置与行对象身份。</summary>
    private void ApplyRows(List<ConnectionInfo> visible, DateTimeOffset now)
    {
        var labels = new[]
        {
            Loc.T("conn.kind.proxy"), Loc.T("conn.kind.direct"),
            Loc.T("conn.kind.blocked"), Loc.T("conn.kind.unknown"),
        };
        var desired = new HashSet<string>(visible.Select(c => c.Id), StringComparer.Ordinal);

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Rows[i].Id)) Rows.RemoveAt(i);
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var c = visible[i];
            var (upPerSec, downPerSec) = ComputeSpeed(c, now);

            var existingIndex = -1;
            for (var j = i; j < Rows.Count; j++)
            {
                if (Rows[j].Id == c.Id) { existingIndex = j; break; }
            }

            if (existingIndex < 0)
            {
                var row = new ConnectionRowViewModel(c.Id);
                row.Update(c, upPerSec, downPerSec, now, labels);
                Rows.Insert(Math.Min(i, Rows.Count), row);
            }
            else
            {
                if (existingIndex != i) Rows.Move(existingIndex, i);
                Rows[i].Update(c, upPerSec, downPerSec, now, labels);
            }
        }

        // 清掉已经断开连接的速率基线，避免 Id 复用时算出负数/突刺
        foreach (var key in _previous.Keys.Where(k => !desired.Contains(k)).ToList())
            _previous.Remove(key);
    }

    private (long Up, long Down) ComputeSpeed(ConnectionInfo c, DateTimeOffset now)
    {
        if (!_previous.TryGetValue(c.Id, out var prev))
        {
            _previous[c.Id] = new Sample(c.Upload, c.Download, now);
            return (0, 0);
        }
        var seconds = (now - prev.At).TotalSeconds;
        var up = seconds > 0 && c.Upload >= prev.Up ? (long)((c.Upload - prev.Up) / seconds) : 0;
        var down = seconds > 0 && c.Download >= prev.Down ? (long)((c.Download - prev.Down) / seconds) : 0;
        _previous[c.Id] = new Sample(c.Upload, c.Download, now);
        return (up, down);
    }

    /// <summary>断开单条连接，立刻把行从列表里拿掉，不等下轮轮询。</summary>
    public async Task CloseAsync(ConnectionRowViewModel row)
    {
        if (row is null) return;
        try
        {
            await AppServices.Clash.CloseConnectionAsync(row.Id);
            Ui.Post(() =>
            {
                for (var i = Rows.Count - 1; i >= 0; i--)
                    if (Rows[i].Id == row.Id) Rows.RemoveAt(i);
                _previous.Remove(row.Id);
                IsEmpty = Rows.Count == 0;
                ShowHint(Loc.T("conn.closed"));
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => ShowHint(Loc.T("conn.closeFailed", ex.Message)));
        }
    }

    [RelayCommand]
    private async Task CloseAllAsync()
    {
        if (!AppServices.Runner.IsRunning) return;
        try
        {
            await AppServices.Clash.CloseConnectionsAsync();
            Ui.Post(() =>
            {
                Rows.Clear();
                _previous.Clear();
                IsEmpty = true;
                ShowHint(Loc.T("conn.closedAll"));
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => ShowHint(Loc.T("conn.closeFailed", ex.Message)));
        }
    }

    private void ShowHint(string text)
    {
        HintText = text;
        HasHint = true;
        _hintHide.Schedule(AutoHide.ShortDelay);
    }
}

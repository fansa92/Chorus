using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Chorus.App.Services;
using Chorus.Core;
using Chorus.Core.Models;

namespace Chorus.App.ViewModels;

/// <summary>
/// 订阅页的 ViewModel（布局与处理逻辑参考 Bettbox 的「配置」列表）：
/// 每个订阅是一张卡片，卡片上直接给出「上次更新时间 / 节点数 / 策略组数 / 失败原因」，
/// 单条更新与全部更新都走同一套流程——拉取 → 解析 → 落盘 → 核心在跑就按新配置重启。
/// </summary>
public partial class SubscriptionsViewModel : ObservableObject
{
    /// <summary>订阅卡片列表（整体替换的快照，避免共享 ObservableCollection 在页面卸载后被改动）。</summary>
    [ObservableProperty] private IReadOnlyList<SubscriptionItemViewModel> _items = [];

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newUrl = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _manualNodeCount;
    [ObservableProperty] private int _subscriptionNodeCount;
    [ObservableProperty] private int _totalNodeCount;
    [ObservableProperty] private int _groupCount;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _messageTitle = "";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    /// <summary>正在更新的订阅个数（用于顶部「全部更新」按钮的进度与禁用）。</summary>
    [ObservableProperty] private int _updatingCount;

    public SubscriptionsViewModel() => Reload();

    // ---------- 顶部摘要 ----------
    public string SummaryText
    {
        get
        {
            var active = AppServices.Settings.ActiveSubscription();
            var source = active is null
                ? (Items.Count == 0 ? "还没有订阅" : "未选择配置（全部订阅合并生效）")
                : $"当前使用「{active.Name}」";
            return $"{source} · 生效 {TotalNodeCount} 个节点（手动 {ManualNodeCount}）· {GroupCount} 个策略组"
                 + $" · 共 {Items.Count} 份配置";
        }
    }

    /// <summary>只有订阅多于一份时，「点击卡片切换」才有意义。</summary>
    public string ActiveHintText => Items.Count > 1
        ? "点卡片中间的空白处即可切换到该配置（同一时刻只有一份配置生效，手动节点始终参与）。"
        : "同一时刻只有一份配置生效：点卡片右侧「更新」刷新，点卡片可把它设为当前使用的配置。";

    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListVisibility => Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string UpdateAllText => IsBusy ? "更新中…" : "全部更新";
    public bool CanUpdateAll => !IsBusy && Items.Count > 0;

    /// <summary>顶部按钮里的进度环是否可见。</summary>
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(CanUpdateAll));
        OnPropertyChanged(nameof(BusyVisibility));
    }

    partial void OnTotalNodeCountChanged(int value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnSubscriptionNodeCountChanged(int value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnManualNodeCountChanged(int value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnGroupCountChanged(int value) => OnPropertyChanged(nameof(SummaryText));

    partial void OnUpdatingCountChanged(int value) => UpdateAllButtonState();

    partial void OnItemsChanged(IReadOnlyList<SubscriptionItemViewModel> value)
    {
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(SummaryText));
        UpdateAllButtonState();
    }

    /// <summary>从设置重建列表（增删改后调用）。</summary>
    public void Reload()
    {
        Ui.Post(() =>
        {
            // 切换/删除订阅后保证「当前使用」仍指向存在的条目
            AppServices.Settings.EnsureActiveSubscription();

            Items = AppServices.Settings.Subscriptions
                .Select(entry => new SubscriptionItemViewModel(entry, IsActive(entry)))
                .ToList();
            ManualNodeCount = AppServices.Settings.Profile.Nodes.Count;
            SubscriptionNodeCount = AppServices.Settings.EffectiveSubscriptions().Sum(s => s.Nodes.Count);
            TotalNodeCount = AppServices.Settings.EffectiveNodes().Count;
            GroupCount = AppServices.Settings.EffectiveGroups().Count;
            OnPropertyChanged(nameof(EmptyVisibility));
            OnPropertyChanged(nameof(ListVisibility));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(ActiveHintText));
            UpdateAllButtonState();
        });
    }

    private static bool IsActive(SubscriptionEntry entry) =>
        string.Equals(AppServices.Settings.ActiveSubscription()?.Url, entry.Url, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把某份订阅设为「当前使用的配置」（Bettbox 里点 profile 卡片就是这件事）。
    /// 只有它解析出的节点/策略组/路由规则参与生成配置；核心在跑就立刻按新配置重启。
    /// </summary>
    public async Task SetActiveAsync(SubscriptionItemViewModel item)
    {
        if (IsBusy || item.IsActive) return;

        AppServices.Settings.ActiveSubscriptionUrl = item.Entry.Url;
        AppServices.Store.Save(AppServices.Settings);
        Reload();

        IsBusy = true;
        try
        {
            var (_, note) = await ApplyAsync();
            var count = item.Entry.Nodes.Count == 0
                ? "（还没有导入节点，点「更新」拉一次）"
                : $"：{item.Entry.Nodes.Count} 个节点、{item.Entry.Groups.Count} 个策略组、"
                  + $"{(string.IsNullOrWhiteSpace(item.Entry.RouteJson) ? "无订阅路由" : "含订阅路由规则")}";
            Show("已切换配置", $"当前使用「{item.Name}」{count}。{note}", InfoBarSeverity.Success);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    private void UpdateAllButtonState()
    {
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(CanUpdateAll));
    }

    public async Task AddAsync()
    {
        if (IsBusy) return;
        var url = NewUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Show("错误", "订阅链接必须以 http:// 或 https:// 开头", InfoBarSeverity.Error);
            return;
        }
        if (AppServices.Settings.Subscriptions.Any(s =>
                string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            Show("错误", "该订阅链接已存在", InfoBarSeverity.Error);
            return;
        }

        var name = NewName.Trim();
        var entry = new SubscriptionEntry
        {
            Name = name.Length > 0 ? name : SubscriptionEntryConverter.DefaultName(url),
            Url = url,
        };
        AppServices.Settings.Subscriptions.Add(entry);

        IsBusy = true;
        try
        {
            var (ok, message) = await UpdateEntryAsync(entry);
            // 刚添加的订阅直接设为当前使用的配置（Bettbox 添加配置后也是立刻切过去）
            AppServices.Settings.ActiveSubscriptionUrl = entry.Url;
            AppServices.Store.Save(AppServices.Settings);
            NewName = "";
            NewUrl = "";
            var (applyOk, note) = ok ? await ApplyAsync() : (true, "");
            Show(
                ok ? "添加成功" : "添加成功但更新失败",
                ok ? $"「{entry.Name}」已添加并设为当前使用的配置，解析到 {entry.Nodes.Count} 个节点、{entry.Groups.Count} 个策略组。{Join(message, note)}"
                   : $"「{entry.Name}」已添加，但更新失败：{message}",
                ok ? (applyOk ? InfoBarSeverity.Success : InfoBarSeverity.Warning) : InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    /// <summary>弹窗面板保存后修改订阅（名称/链接），并立即重新拉取。</summary>
    public async Task EditAsync(SubscriptionItemViewModel item, string name, string url)
    {
        if (IsBusy) return;
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Show("错误", "订阅链接必须以 http:// 或 https:// 开头", InfoBarSeverity.Error);
            return;
        }
        if (AppServices.Settings.Subscriptions.Any(s =>
                !ReferenceEquals(s, item.Entry)
                && string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            Show("错误", "该订阅链接已存在", InfoBarSeverity.Error);
            return;
        }

        var trimmed = name.Trim();
        item.Entry.Name = trimmed.Length > 0 ? trimmed : SubscriptionEntryConverter.DefaultName(url);
        // 当前使用的订阅是用 Url 认的，改了链接要跟着改指向
        var wasActive = item.IsActive;
        item.Entry.Url = url;
        if (wasActive) AppServices.Settings.ActiveSubscriptionUrl = url;
        Reload();

        IsBusy = true;
        try
        {
            var (ok, message) = await UpdateEntryAsync(item.Entry);
            AppServices.Store.Save(AppServices.Settings);
            var (applyOk, note) = !ok ? (true, "")
                : wasActive ? await ApplyAsync()
                : (true, "该订阅当前未使用，保存不影响正在运行的配置。");
            Show(
                ok ? "保存成功" : "保存成功但更新失败",
                ok ? $"「{item.Entry.Name}」：{item.Entry.Nodes.Count} 个节点、{item.Entry.Groups.Count} 个策略组。{Join(message, note)}"
                   : $"「{item.Entry.Name}」已保存，但更新失败：{message}",
                ok ? (applyOk ? InfoBarSeverity.Success : InfoBarSeverity.Warning) : InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    public async Task UpdateAsync(SubscriptionItemViewModel item)
    {
        // 同一个订阅不并发更新（Bettbox 里也是靠 _updatingProfileIds 去重）
        if (item.IsUpdating) return;
        item.IsUpdating = true;
        UpdatingCount++;
        try
        {
            var (ok, message) = await UpdateEntryAsync(item.Entry);
            AppServices.Store.Save(AppServices.Settings);
            if (!ok)
            {
                Show("更新失败", $"「{item.Entry.Name}」更新失败：{message}", InfoBarSeverity.Error);
            }
            else
            {
                // 成功不再弹提示条：卡片上的「x 分钟前更新 · N 个节点 · M 个策略组」就是结果，
                // 每次都弹一条绿条只会打断操作。只有「核心重启失败」这种真问题才提示。
                ClearMessage();
                // 只有更新的是「当前使用」的那份才需要重启核心；更新别人不该打断正在跑的代理
                if (item.IsActive)
                {
                    var (applyOk, note) = await ApplyAsync();
                    if (!applyOk) Show("已更新，但重启核心失败", note, InfoBarSeverity.Warning);
                }
            }
        }
        finally
        {
            item.IsUpdating = false;
            UpdatingCount--;
            Reload();
        }
    }

    public async Task UpdateAllAsync()
    {
        if (IsBusy) return;
        var items = Items.ToList();
        if (items.Count == 0)
        {
            Show("提示", "还没有添加任何订阅", InfoBarSeverity.Informational);
            return;
        }

        IsBusy = true;
        try
        {
            var okCount = 0;
            var activeUpdated = false;
            var failures = new List<string>();
            foreach (var item in items)
            {
                item.IsUpdating = true;
                UpdatingCount++;
                try
                {
                    var (ok, message) = await UpdateEntryAsync(item.Entry);
                    if (ok)
                    {
                        okCount++;
                        // 只有「当前使用」的那份变化才需要重启核心
                        if (item.IsActive) activeUpdated = true;
                    }
                    else
                    {
                        failures.Add($"{item.Entry.Name}：{message}");
                    }
                }
                finally
                {
                    item.IsUpdating = false;
                    UpdatingCount--;
                }
            }
            AppServices.Store.Save(AppServices.Settings);
            if (failures.Count > 0)
            {
                // 全军覆没时别叫「部分更新失败」，直接把原因说清楚
                var title = okCount == 0 ? "更新失败" : "部分更新失败";
                var text = okCount == 0
                    ? $"{failures.Count} 个订阅都没更新成功。首个错误：{failures[0]}"
                    : $"成功 {okCount} 个，失败 {failures.Count} 个。首个错误：{failures[0]}"
                      + (activeUpdated ? "（当前使用的配置已刷新）" : "（当前使用的配置没有变化）");
                Show(title, text, InfoBarSeverity.Warning);
            }
            else
            {
                // 全部成功也不弹提示条（每张卡的更新时间已经变了）
                ClearMessage();
                if (activeUpdated)
                {
                    var (applyOk, note) = await ApplyAsync();
                    if (!applyOk) Show("已全部更新，但重启核心失败", note, InfoBarSeverity.Warning);
                }
            }
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    /// <summary>
    /// 删除订阅。删掉的正好是「当前使用」的那份时，自动切到剩下的第一份并让核心按新配置重启，
    /// 否则策略组页会一直显示已经删掉的配置。
    /// </summary>
    public async Task RemoveAsync(SubscriptionItemViewModel item)
    {
        if (IsBusy) return;
        var wasActive = item.IsActive;
        AppServices.Settings.Subscriptions.Remove(item.Entry);
        AppServices.Store.Save(AppServices.Settings);
        Reload();

        var next = AppServices.Settings.ActiveSubscription();
        if (!wasActive)
        {
            Show("已删除", $"订阅「{item.Name}」已删除。核心正在运行时点「全部更新」或手动重启核心后生效。",
                InfoBarSeverity.Informational);
            return;
        }

        IsBusy = true;
        try
        {
            var (_, note) = next is null
                ? (true, "现在没有任何订阅，只剩手动节点参与生成配置。")
                : (true, $"现在使用「{next.Name}」。{(await ApplyAsync()).Note}");
            Show("已删除", $"「{item.Name}」是当前使用的配置，已删除。{note}", InfoBarSeverity.Informational);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    /// <summary>下载并解析单个订阅，写回 Entry。返回 (是否成功, 提示信息)。</summary>
    private static async Task<(bool Ok, string Message)> UpdateEntryAsync(SubscriptionEntry entry)
    {
        try
        {
            var (nodes, groups, routing, errors) = await AppServices.Subscriptions.ImportFullAsync(entry.Url);
            entry.Nodes = nodes;
            entry.Groups = groups;
            // 订阅自带的 route/dns 要跟着存下来：它们既决定规则页显示什么，
            // 也决定生成配置时能不能把规则集合并进去
            entry.RouteJson = routing?.RouteJson;
            entry.DnsJson = routing?.DnsJson;
            entry.LastUpdated = DateTimeOffset.Now;
            entry.LastError = errors.Count > 0 ? errors[0] : null;
            var detail = AppServices.Subscriptions.LastFetchDetail;
            var routingNote = routing?.HasAny == true ? "（含订阅路由规则）" : "";
            return (true, detail is null ? routingNote : $"{detail}{routingNote}");
        }
        catch (Exception ex)
        {
            entry.LastError = ex.Message;
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 订阅数据变化后让新配置真正生效：核心在运行时按新配置重启（否则策略组页读到的
    /// 仍是旧配置，看起来就像「策略组没加载」）；核心未运行时提示启动后生效。
    /// </summary>
    private static async Task<(bool Ok, string Note)> ApplyAsync()
    {
        if (!AppServices.Runner.IsRunning) return (true, "启动核心后生效。");
        try
        {
            await AppServices.Runner.StopAsync();
            await AppServices.Runner.StartAsync();
            _ = AppServices.ProxiesVm.RefreshAsync();
            return (true, "核心已按新配置重启。");
        }
        catch (Exception ex)
        {
            return (false, $"自动重启核心失败（{ex.Message}），请手动重启核心。");
        }
    }

    /// <summary>把若干条提示拼接成一句（跳过空项），避免文案里出现多余空格或空括号。</summary>
    private static string Join(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private void Show(string title, string text, InfoBarSeverity severity)
    {
        MessageTitle = title;
        MessageText = text;
        MessageSeverity = severity;
        HasMessage = true;
    }

    /// <summary>收起提示条。更新成功不再提示，顺手把上一次残留的成功/信息条也收掉。</summary>
    private void ClearMessage()
    {
        if (!HasMessage) return;
        HasMessage = false;
        MessageTitle = "";
        MessageText = "";
    }
}

/// <summary>列表中单个订阅的卡片数据（Bettbox 配置卡片的信息密度：状态一行说清）。</summary>
public partial class SubscriptionItemViewModel : ObservableObject
{
    private static readonly SolidColorBrush OkBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0xA5, 0x5D));
    private static readonly SolidColorBrush OkBgBrush = new(Windows.UI.Color.FromArgb(0x1F, 0x3B, 0xA5, 0x5D));
    private static readonly SolidColorBrush WarnBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x8A, 0x2B));
    private static readonly SolidColorBrush WarnBgBrush = new(Windows.UI.Color.FromArgb(0x1F, 0xD1, 0x8A, 0x2B));
    private static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));
    private static readonly SolidColorBrush ErrorBgBrush = new(Windows.UI.Color.FromArgb(0x1F, 0xD1, 0x34, 0x38));

    public SubscriptionEntry Entry { get; }

    [ObservableProperty] private bool _isUpdating;

    /// <summary>是否是「当前使用的配置」（同一时刻只有一份生效）。</summary>
    [ObservableProperty] private bool _isActive;

    public string Name => Entry.Name;
    public string Url => Entry.Url;

    /// <summary>订阅名首字符，用作卡片图标。</summary>
    public string IconLetter => Name.Length == 0 ? "?" : Name[..1];

    /// <summary>只显示主机名，卡片上比整条链接更易读。</summary>
    public string Host
    {
        get
        {
            try
            {
                var uri = new Uri(Entry.Url);
                return uri.Host + (uri.AbsolutePath is "/" or "" ? "" : uri.AbsolutePath);
            }
            catch
            {
                return Entry.Url;
            }
        }
    }

    public string CountText => $"{Entry.Nodes.Count} 个节点 · {Entry.Groups.Count} 个策略组";

    public bool HasError => Entry.LastError is { Length: > 0 };

    /// <summary>状态徽标：更新失败 / 已更新 / 未更新。</summary>
    public string BadgeText => HasError ? "失败" : Entry.LastUpdated is null ? "未更新" : "已导入";

    public Brush BadgeBrush => HasError ? ErrorBrush : Entry.LastUpdated is null ? WarnBrush : OkBrush;
    public Brush BadgeBackground => HasError ? ErrorBgBrush : Entry.LastUpdated is null ? WarnBgBrush : OkBgBrush;

    /// <summary>上次更新时间（相对描述，Bettbox 卡片上也是这种「x 分钟前」）。</summary>
    public string UpdatedText
    {
        get
        {
            if (Entry.LastUpdated is not { } updated) return "尚未更新";
            var local = updated.LocalDateTime;
            var delta = DateTimeOffset.Now - updated;
            var relative = delta.TotalMinutes switch
            {
                < 1 => "刚刚",
                < 60 => $"{(int)delta.TotalMinutes} 分钟前",
                < 60 * 24 => $"{(int)delta.TotalHours} 小时前",
                < 60 * 24 * 30 => $"{(int)delta.TotalDays} 天前",
                _ => local.ToString("yyyy-MM-dd"),
            };
            return $"{relative}更新（{local:MM-dd HH:mm}）";
        }
    }

    /// <summary>错误详情（有失败时显示在卡片下方）。</summary>
    public string ErrorText => HasError ? $"上次更新失败：{Entry.LastError}" : "";
    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>更新中时按钮换成进度环。</summary>
    public Visibility UpdateButtonVisibility => IsUpdating ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BusyVisibility => IsUpdating ? Visibility.Visible : Visibility.Collapsed;
    public bool ButtonsEnabled => !IsUpdating;

    /// <summary>列表显示的状态：更新中优先。</summary>
    public string DisplayStatus => IsUpdating ? "正在更新…" : $"{UpdatedText} · {CountText}";

    // ---------- 选中态（Bettbox 的配置卡片：选中的那份有强调色描边与「使用中」标记） ----------
    public Visibility ActiveBadgeVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Brush CardStroke => IsActive ? ThemeBrushes.Accent : ThemeBrushes.CardStroke;
    public Thickness CardStrokeThickness => IsActive ? new Thickness(2) : new Thickness(1);
    public string ActiveTooltip => IsActive ? "当前使用的配置" : "点击设为当前使用的配置";

    public SubscriptionItemViewModel(SubscriptionEntry entry, bool isActive)
    {
        Entry = entry;
        _isActive = isActive;
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ActiveBadgeVisibility));
        OnPropertyChanged(nameof(CardStroke));
        OnPropertyChanged(nameof(CardStrokeThickness));
        OnPropertyChanged(nameof(ActiveTooltip));
    }

    partial void OnIsUpdatingChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(UpdateButtonVisibility));
        OnPropertyChanged(nameof(BusyVisibility));
        OnPropertyChanged(nameof(ButtonsEnabled));
    }
}

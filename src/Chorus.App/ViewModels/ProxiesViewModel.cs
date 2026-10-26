using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.ClashApi;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Chorus.Core.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Chorus.App.ViewModels;

/// <summary>
/// 策略组页（参考 Bettbox / FlClash 的标签页布局）：顶部是策略组切换标签，
/// 点标签切换到另一个策略组；下方只渲染当前组，节点以卡片阵列（自适应多列）排布。
/// </summary>
/// <summary>
/// 策略组显示设置面板里的一个可选项：值写进 settings.json，
/// 标题与副标题走 <see cref="Loc"/> 本地化（key 形式，随语言切换刷新）。
/// </summary>
/// <param name="Value">存进 settings.json 的值（如 "tab" / "delay"）。</param>
/// <param name="LabelKey">标题的本地化 key。</param>
/// <param name="SubtitleKey">副标题的本地化 key。</param>
public sealed record StyleOption(string Value, string LabelKey, string SubtitleKey)
{
    public string Label => Loc.T(LabelKey);
    public string Subtitle => Loc.T(SubtitleKey);
}

public partial class ProxiesViewModel : ObservableObject

{
    // 所有策略组（含被搜索过滤隐藏的），用于保留测速结果
    private readonly List<ProxyGroupViewModel> _allGroups = [];
    private bool _refreshing;
    private int _lastProxyCount;
    private string _lastConfigLabel = "全部订阅合并";

    /// <summary>当前展示的策略组标签（过滤后的快照，整体替换而不是逐条增删）。</summary>
    [ObservableProperty] private IReadOnlyList<ProxyGroupViewModel> _groups = [];

    /// <summary>当前展示的策略组（切换标签时变化）。</summary>
    [ObservableProperty] private ProxyGroupViewModel? _selectedGroup;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "";

    /// <summary>列表为空时的提示文字（核心未运行 / 无匹配）。</summary>
    [ObservableProperty] private string _emptyText = "核心未运行，启动后点刷新";

    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Collapsed;

    /// <summary>正在从核心拉取策略组（页面显示「正在加载」而不是一片空白）。</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>
    /// 当前展示的是否为「上次读取的快照」（核心未运行）。
    /// Bettbox 的核心随应用启动、配置常驻内存，所以没启动代理也能列组；
    /// Chorus 的核心是独立进程，未运行时用落盘快照兜底，行为上对齐。
    /// </summary>
    [ObservableProperty] private bool _fromCache;

    /// <summary>当前组内容（阵列）是否可见。</summary>
    public Visibility ContentVisibility
        => SelectedGroup is not null && !IsLoading ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>加载中的提示是否可见。</summary>
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「核心未运行，以下是上次读取的快照」提示条的可见性。</summary>
    public Visibility CacheNoteVisibility => FromCache ? Visibility.Visible : Visibility.Collapsed;

    public string CacheNoteText => "核心未运行，下面显示的是上次读取的策略组快照（只读）。启动核心后即可切换节点与测速。";

    // ---- 自定义面板：策略组开关（参考 Bettbox 的 groupSwitches）----

    /// <summary>可开关的策略组清单（含开关状态）。</summary>
    [ObservableProperty] private IReadOnlyList<GroupSwitchViewModel> _groupSwitches = [];

    /// <summary>自定义面板的说明文字。</summary>
    public string CustomHintText => "关掉的策略组不会写进配置，引用它的路由规则也会被剔除。改动需要重启核心后生效。";

    /// <summary>开关状态：某个组是否被禁用（禁用 = 不写进配置）。</summary>
    public bool IsGroupDisabled(string name) =>
        AppServices.Settings.DisabledGroups?.Contains(name) == true;

    /// <summary>
    /// 切换某个策略组的启用状态。立即写盘；配置本身要重启核心才生效，
    /// 所以这里提示用户，而不是偷偷重启（重启会断掉正在跑的连接）。
    /// </summary>
    public void ToggleGroupSwitch(GroupSwitchViewModel item, bool enabled)
    {
        var settings = AppServices.Settings;
        settings.DisabledGroups ??= [];
        if (enabled) settings.DisabledGroups.Remove(item.Name);
        else if (!settings.DisabledGroups.Contains(item.Name)) settings.DisabledGroups.Add(item.Name);
        AppServices.Store.Save(settings);

        item.IsEnabled = enabled;
        StatusText = enabled
            ? $"已启用「{item.Name}」，重启核心后生效"
            : $"已禁用「{item.Name}」，重启核心后生效";
    }

    /// <summary>
    /// 重建自定义面板的清单：所有策略组（来自当前配置，能读到就以核心里的为准，
    /// 读不到就用设置里算出来的），默认全部启用。
    /// </summary>
    public void BuildGroupSwitches()
    {
        var names = new List<string>();

        // 优先用核心里实际存在的组（这是用户看到的真实清单）
        if (_allGroups.Count > 0)
        {
            names.AddRange(_allGroups.Select(g => g.Name));
        }
        else
        {
            // 核心没跑：按设置推算
            try
            {
                names.AddRange(AppServices.Settings.EffectiveGroups().Select(g => g.Name));
            }
            catch
            {
                // 设置损坏时给出空清单
            }
        }

        GroupSwitches = names
            .Distinct(StringComparer.Ordinal)
            .Select(name => new GroupSwitchViewModel(name, !IsGroupDisabled(name)))
            .ToList();
    }

    partial void OnFromCacheChanged(bool value) => OnPropertyChanged(nameof(CacheNoteVisibility));

    // ---- 显示设置面板（参考 Bettbox 代理页 ProxiesSetting）----

    /// <summary>是否为列表视图（所有策略组纵向堆叠）。</summary>
    public bool IsListView => ProxiesStyleMetrics.IsListView;

    /// <summary>是否为标签页视图（一次只显示一个策略组）。</summary>
    public bool IsTabView => ProxiesStyleMetrics.IsTabView;

    /// <summary>列表视图容器可见性。</summary>
    public Visibility ListViewVisibility => IsListView ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>标签页视图容器可见性（含顶部标签条与单组内容）。</summary>
    public Visibility TabViewVisibility => IsTabView ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>顶部策略组标签条可见性：只有标签页视图才需要。</summary>
    public Visibility GroupTabsVisibility => IsTabView ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「组头自动吸顶」开关是否可调（仅列表视图有意义）。</summary>
    public Visibility StickyOptionVisibility => IsListView ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「图标样式」分组是否可调（Bettbox 里也只有列表模式才显示这一组）。</summary>
    public Visibility IconStyleSectionVisibility => IsListView ? Visibility.Visible : Visibility.Collapsed;

    // 设置面板的选项表：value 存进 settings.json，label 走本地化
    public IReadOnlyList<StyleOption> ViewTypeOptions { get; } =
    [
        new("tab", "proxies.style.tab", "proxies.style.tab.sub"),
        new("list", "proxies.style.list", "proxies.style.list.sub"),
    ];

    public IReadOnlyList<StyleOption> SortTypeOptions { get; } =
    [
        new("none", "proxies.sort.none", "proxies.sort.none.sub"),
        new("delay", "proxies.sort.delay", "proxies.sort.delay.sub"),
        new("name", "proxies.sort.name", "proxies.sort.name.sub"),
    ];

    public IReadOnlyList<StyleOption> LayoutOptions { get; } =
    [
        new("tight", "proxies.layout.tight", "proxies.layout.tight.sub"),
        new("standard", "proxies.layout.standard", "proxies.layout.standard.sub"),
        new("loose", "proxies.layout.loose", "proxies.layout.loose.sub"),
    ];

    public IReadOnlyList<StyleOption> CardTypeOptions { get; } =
    [
        new("expand", "proxies.size.expand", "proxies.size.expand.sub"),
        new("shrink", "proxies.size.shrink", "proxies.size.shrink.sub"),
        new("min", "proxies.size.min", "proxies.size.min.sub"),
    ];

    public IReadOnlyList<StyleOption> IconStyleOptions { get; } =
    [
        new("standard", "proxies.icon.standard", "proxies.icon.standard.sub"),
        new("none", "proxies.icon.none", "proxies.icon.none.sub"),
        new("icon", "proxies.icon.iconOnly", "proxies.icon.iconOnly.sub"),
    ];

    /// <summary>当前选中的值（供设置面板高亮）。</summary>
    public string CurrentViewType => ProxiesStyleMetrics.ViewType;
    public string CurrentSortType => ProxiesStyleMetrics.SortType;
    public string CurrentLayout => ProxiesStyleMetrics.Layout;
    public string CurrentCardType => ProxiesStyleMetrics.CardType;
    public string CurrentIconStyle => ProxiesStyleMetrics.IconStyle;
    public bool AutoStickyHeader
    {
        get => ProxiesStyleMetrics.AutoStickyHeader;
        set => SaveStyle(s => s.ProxiesAutoStickyHeader = value);
    }

    public bool ShowHiddenItems
    {
        get => ProxiesStyleMetrics.ShowHiddenItems;
        set => SaveStyle(s => s.ProxiesShowHiddenItems = value);
    }

    /// <summary>设置面板的说明文字。</summary>
    public string StyleHintText => Loc.T("proxies.settings.hint");

    /// <summary>
    /// 改一项显示设置：写盘 → 刷新整页（重排节点、切换视图容器、重算卡片几何）。
    /// 只影响展示，不碰 sing-box 配置，所以不需要重启核心。
    /// </summary>
    public void ApplyStyleOption(Action<AppSettings, string> setter, Func<AppSettings, string> getter, string value)
    {
        var settings = AppServices.Settings;
        // 值没变就别重复落盘（设置面板会因动画等原因重复触发）
        if (string.Equals(getter(settings), value, StringComparison.Ordinal)) return;
        setter(settings, value);
        AppServices.Store.Save(settings);
        RefreshStyleSettings();
    }

    /// <summary>布尔型显示设置（吸顶 / 显示隐藏项）。</summary>
    private void SaveStyle(Action<AppSettings> mutate)
    {
        mutate(AppServices.Settings);
        AppServices.Store.Save(AppServices.Settings);
        RefreshStyleSettings();
    }

    /// <summary>设置变化后刷新所有与展示相关的绑定。</summary>
    public void RefreshStyleSettings()
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsTabView));
        OnPropertyChanged(nameof(ListViewVisibility));
        OnPropertyChanged(nameof(TabViewVisibility));
        OnPropertyChanged(nameof(GroupTabsVisibility));
        OnPropertyChanged(nameof(StickyOptionVisibility));
        OnPropertyChanged(nameof(IconStyleSectionVisibility));
        OnPropertyChanged(nameof(CurrentViewType));
        OnPropertyChanged(nameof(CurrentSortType));
        OnPropertyChanged(nameof(CurrentLayout));
        OnPropertyChanged(nameof(CurrentCardType));
        OnPropertyChanged(nameof(CurrentIconStyle));
        OnPropertyChanged(nameof(AutoStickyHeader));
        OnPropertyChanged(nameof(ShowHiddenItems));
        // 重排节点 + 重算卡片几何 + 按「显示隐藏项」重新过滤
        ApplyFilter();
    }

    public ProxiesViewModel()
    {
        // 核心停止后立刻回落到快照，避免策略组页变成空白；核心启动后再拉真实数据
        AppServices.Runner.StatusChanged += running => Ui.Post(() =>
        {
            if (running)
            {
                FromCache = false;
                _ = RefreshAsync();
            }
            else
            {
                RestoreSnapshot();
            }
        });
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedGroupChanged(ProxyGroupViewModel? value)
        => OnPropertyChanged(nameof(ContentVisibility));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ContentVisibility));
        OnPropertyChanged(nameof(LoadingVisibility));
    }

    /// <summary>切换到指定策略组（标签页切换）。</summary>
    public void SelectGroup(ProxyGroupViewModel? group)
    {
        foreach (var g in _allGroups)
            g.IsActive = ReferenceEquals(g, group);
        SelectedGroup = group;
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return; // 连点刷新 / 页面重复进入时只跑一次
        if (!AppServices.Runner.IsRunning)
        {
            Ui.Post(RestoreSnapshot);
            return;
        }

        _refreshing = true;
        Ui.Post(() =>
        {
            IsLoading = _allGroups.Count == 0;
            if (IsLoading) EmptyVisibility = Visibility.Collapsed;
        });
        try
        {
            // 刚启动核心时 /proxies 可能还没就绪：inbound 与 Clash API 是在路由
            // （含远程规则集下载）初始化完成后才起来的，慢的时候要几十秒。
            // 这里边等边试，避免「启动后策略组页一片空白」。
            for (var attempt = 0; attempt < 90; attempt++)
            {
                if (!AppServices.Runner.IsRunning)
                {
                    Ui.Post(() =>
                    {
                        IsLoading = false;
                        StatusText = "核心已停止";
                        UpdateEmptyVisibility();
                    });
                    return;
                }
                if (await TryLoadAsync()) return;

                Ui.Post(() =>
                {
                    if (_allGroups.Count == 0)
                    {
                        IsLoading = true;
                        StatusText = "正在等待核心就绪…（规则集下载完成后即可读取）";
                    }
                });
                await Task.Delay(attempt < 5 ? 500 : 1000);
            }

            Ui.Post(() =>
            {
                IsLoading = false;
                StatusText = "读取策略组超时，点刷新重试";
                UpdateEmptyVisibility();
            });
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>拉一次 /proxies 并重建标签；返回是否拿到了策略组。</summary>
    private async Task<bool> TryLoadAsync()
    {
        Ui.Post(() =>
        {
            if (_allGroups.Count == 0) IsLoading = true;
        });

        Dictionary<string, ProxyInfo> proxies;
        try
        {
            proxies = await AppServices.Clash.GetProxiesAsync();
        }
        catch (Exception ex)
        {
            Ui.Post(() =>
            {
                IsLoading = false;
                StatusText = "刷新失败：" + ex.Message;
                UpdateEmptyVisibility();
            });
            return false;
        }

        var groupInfos = proxies.Values
            .Where(p => (p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase)
                      || p.Type.Equals("URLTest", StringComparison.OrdinalIgnoreCase)
                      || p.Type.Equals("Fallback", StringComparison.OrdinalIgnoreCase))
                      // GLOBAL 是 sing-box 为兼容 Clash 面板伪造的聚合项（源码注释
                      // "fix clash dashboard"），不在出站管理器里，选中必 404，故不展示
                      && !p.Name.Equals("GLOBAL", StringComparison.Ordinal))
            .OrderBy(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        if (groupInfos.Count == 0) return false;

        Ui.Post(() =>
        {
            // 记住刷新前的测速结果，刷新后按 (组名, 节点名) 恢复
            var oldDelays = _allGroups
                .SelectMany(g => g.AllMembers)
                .Where(m => m.DelayMs.HasValue)
                .GroupBy(m => (Group: m.Group.Name, m.Name))
                .ToDictionary(g => g.Key, g => g.First().DelayMs);

            // 记住刷新前停留在哪个策略组标签，重建后按组名回到同一页
            var previousGroupName = SelectedGroup?.Name;

            _allGroups.Clear();
            foreach (var info in groupInfos)
            {
                // 内层自动组（◈ 自动·xxx）是 Chorus 给自动测速组加的包装层里层，
                // 不单独展示为标签页——它在 Clash API 里会自己冒出来。
                if (info.Name.StartsWith(ConfigGenerator.AutoInnerPrefix, StringComparison.Ordinal)) continue;

                // 自动组：外层 selector 选中的是内层 urltest 组名，真正的节点在内层组的 now 里
                var innerTag = ConfigGenerator.AutoInnerPrefix + info.Name;
                var isAutoGroup = info.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase)
                                  && info.All.Contains(innerTag, StringComparer.Ordinal);
                var autoNow = isAutoGroup && proxies.TryGetValue(innerTag, out var inner)
                    ? inner.Now ?? ""
                    : "";

                var gvm = new ProxyGroupViewModel(info.Name, info.Type)
                {
                    SelectedName = info.Now ?? "",
                };
                gvm.SetAutoInfo(isAutoGroup, innerTag, autoNow);

                var members = info.All.Select(name =>
                {
                    var member = new ProxyMemberViewModel(gvm, name)
                    {
                        IsSelected = name == info.Now,
                        // 每个代理在 Clash API 里都带自己的 type，即协议类型
                        ProtocolType = proxies.TryGetValue(name, out var pi) ? pi.Type : "",
                    };
                    if (oldDelays.TryGetValue((info.Name, name), out var delay))
                        member.DelayMs = delay;
                    else if (proxies.TryGetValue(name, out var p2) && p2.LastDelay is > 0)
                        member.DelayMs = p2.LastDelay; // 核心内存中的历史延迟
                    return member;
                }).ToList();
                gvm.SetMembers(members);
                _allGroups.Add(gvm);
            }

            IsLoading = false;
            FromCache = false;
            SelectGroup(previousGroupName is null
                ? _allGroups.FirstOrDefault()
                : _allGroups.FirstOrDefault(g => g.Name == previousGroupName) ?? _allGroups.FirstOrDefault());
            ApplyFilter();
            // 顺带说明当前用的是哪份配置：多订阅时组名可能一样，看这句才知道换没换
            var active = AppServices.Settings.ActiveSubscription();
            var configLabel = active is null ? "全部订阅合并" : $"配置「{active.Name}」";
            StatusText = $"已加载 {_allGroups.Count} 个策略组 · {proxies.Count} 个出站 · {configLabel}";
            _lastProxyCount = proxies.Count;
            _lastConfigLabel = configLabel;
            PersistSnapshot();
        });
        return true;
    }

    /// <summary>
    /// 轻量刷新：只同步自动组里「内核当前挑中的节点」，不重建列表、不动测速结果。
    /// 内层 urltest 每个测速间隔都会重新选节点，页面开着时定时调用保持显示最新。
    /// </summary>
    public async Task RefreshAutoSelectionAsync()
    {
        if (_refreshing || _allGroups.Count == 0 || !AppServices.Runner.IsRunning) return;

        Dictionary<string, ProxyInfo> proxies;
        try
        {
            proxies = await AppServices.Clash.GetProxiesAsync();
        }
        catch
        {
            return; // 核心刚停或 API 还没就绪：保持现状，下次再试
        }

        Ui.Post(() =>
        {
            var changed = false;
            foreach (var group in _allGroups)
            {
                if (!group.IsAutoGroup) continue;
                if (!proxies.TryGetValue(group.AutoInnerTag, out var inner)) continue;
                var now = inner.Now ?? "";
                if (now == group.AutoNow) continue;
                group.AutoNow = now;
                changed = true;
            }
            if (changed) PersistSnapshot();
        });
    }

    /// <summary>核心未运行时：把上次成功读取的策略组快照铺回页面（只读）。</summary>
    private void RestoreSnapshot()
    {
        var snapshot = AppServices.ProxySnapshots.Load();
        if (snapshot is null)
        {
            FromCache = false;
            _allGroups.Clear();
            Groups = [];
            IsLoading = false;
            StatusText = "核心未运行";
            EmptyText = "核心未运行，启动后点刷新";
            UpdateEmptyVisibility();
            return;
        }

        var previousGroupName = SelectedGroup?.Name;
        _allGroups.Clear();
        foreach (var group in snapshot.Groups)
        {
            // 旧快照没有 IsAuto/AutoNow：选中项是「◈ 自动·xxx」就说明这是自动组
            var innerTag = ConfigGenerator.AutoInnerPrefix + group.Name;
            var isAuto = group.IsAuto
                         || group.Now.StartsWith(ConfigGenerator.AutoInnerPrefix, StringComparison.Ordinal);
            var gvm = new ProxyGroupViewModel(group.Name, group.Type) { SelectedName = group.Now };
            gvm.SetAutoInfo(isAuto, innerTag, isAuto ? group.AutoNow : "");
            var members = group.Members.Select(m => new ProxyMemberViewModel(gvm, m.Name)
            {
                ProtocolType = m.Type,
                IsSelected = m.Selected,
                DelayMs = m.DelayMs,
            }).ToList();
            gvm.SetMembers(members);
            _allGroups.Add(gvm);
        }

        FromCache = true;
        IsLoading = false;
        SelectGroup(previousGroupName is null
            ? _allGroups.FirstOrDefault()
            : _allGroups.FirstOrDefault(g => g.Name == previousGroupName) ?? _allGroups.FirstOrDefault());
        ApplyFilter();
        StatusText = $"核心未运行 · 上次加载 {_allGroups.Count} 组（{snapshot.SavedAt.ToLocalTime():MM-dd HH:mm}）· 只读";
    }

    /// <summary>把当前组与节点（含选中项、测速结果）写成快照，供核心停止后展示。</summary>
    private void PersistSnapshot()
    {
        if (_allGroups.Count == 0) return;
        var snapshot = new ProxySnapshot
        {
            SavedAt = DateTimeOffset.Now,
            Source = _lastConfigLabel,
            ProxyCount = _lastProxyCount,
            Groups = _allGroups.Select(group => new ProxySnapshotGroup
            {
                Name = group.Name,
                Type = group.RawType,
                Now = group.SelectedName,
                IsAuto = group.IsAutoGroup,
                AutoNow = group.AutoNow,
                Members = group.AllMembers.Select(member => new ProxySnapshotMember
                {
                    Name = member.Name,
                    Type = member.ProtocolType,
                    DelayMs = member.DelayMs,
                    Selected = member.IsSelected,
                }).ToList(),
            }).ToList(),
        };
        // 落盘放后台：磁盘再快也不该卡住点击后的重绘
        _ = Task.Run(() => AppServices.ProxySnapshots.Save(snapshot));
    }

    /// <summary>按搜索词过滤组与组内节点。</summary>
    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var visible = new List<ProxyGroupViewModel>(_allGroups.Count);
        foreach (var group in _allGroups)
        {
            // 「显示已关闭的策略组」关着时，把「自定义」里禁用的组从列表里摘掉
            if (!ProxiesStyleMetrics.ShowHiddenItems && IsGroupDisabled(group.Name)) continue;

            group.UpdateFilter(search);
            if (search.Length > 0
                && group.Members.Count == 0
                && !group.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue; // 搜索时隐藏名称与节点都不匹配的组
            visible.Add(group);
        }
        Groups = visible;

        // 当前标签被搜索过滤掉时，自动切到列表里的第一个组
        if (SelectedGroup is null || !visible.Contains(SelectedGroup))
            SelectGroup(visible.FirstOrDefault());

        UpdateEmptyVisibility();
    }

    private void UpdateEmptyVisibility()
    {
        if (IsLoading)
        {
            EmptyVisibility = Visibility.Collapsed;
            return;
        }
        if (Groups.Count == 0)
        {
            EmptyVisibility = Visibility.Visible;
            EmptyText = string.IsNullOrWhiteSpace(SearchText)
                ? (AppServices.Runner.IsRunning ? "没有加载到策略组，试试刷新" : "核心未运行，启动后点刷新")
                : "没有匹配的节点或策略组";
        }
        else
        {
            EmptyVisibility = Visibility.Collapsed;
        }
    }

    /// <summary>在策略组里选中一个成员（节点或嵌套的策略组）。</summary>
    public async Task SelectAsync(ProxyGroupViewModel group, ProxyMemberViewModel member)
    {
        if (group.SelectedName == member.Name) return;
        if (!AppServices.Runner.IsRunning)
        {
            Ui.Post(() => StatusText = "核心未运行，启动后才能切换节点（当前显示的是上次读取的快照）");
            return;
        }
        try
        {
            await AppServices.Clash.SelectAsync(group.Name, member.Name);
            Ui.Post(() =>
            {
                group.SelectedName = member.Name;
                foreach (var m in group.AllMembers)
                    m.IsSelected = m.Name == member.Name;
                StatusText = $"{group.Name} → {member.Name}";
                PersistSnapshot();
                // 自动组被手动钉住后记住这个选择：重启核心后仍然生效
                if (group.IsAutoGroup)
                    RememberAutoPick(group.Name, member.Name);
                // 出口换了，仪表盘的网络检测必须跟着刷新（Bettbox 换节点后也会重查出口 IP）
                AppServices.DashboardVm.RefreshAfterNodeSwitch(member.Name);
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = "切换失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把自动组交还内核：选中内层 urltest 组，核心恢复「自动挑最快」。
    /// 用户点一下已选中的节点就回到自动模式（Bettbox 的 url-test 组也是这么切的）。
    /// </summary>
    public async Task ResetAutoGroupAsync(ProxyGroupViewModel group)
    {
        if (!group.IsAutoGroup || group.AutoInnerTag.Length == 0) return;
        if (!AppServices.Runner.IsRunning)
        {
            Ui.Post(() => StatusText = "核心未运行，启动后才能切换节点（当前显示的是上次读取的快照）");
            return;
        }
        try
        {
            await AppServices.Clash.SelectAsync(group.Name, group.AutoInnerTag);
            Ui.Post(() =>
            {
                group.SelectedName = group.AutoInnerTag;
                foreach (var m in group.AllMembers)
                    m.IsSelected = false;
                StatusText = $"{group.Name} → 由内核自动选择";
                PersistSnapshot();
                RememberAutoPick(group.Name, "");
                AppServices.DashboardVm.RefreshAfterNodeSwitch();
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = "切换失败：" + ex.Message);
        }
    }

    /// <summary>记住（或清除）自动组的手动固定，写进设置以便重启核心后仍生效。</summary>
    private static void RememberAutoPick(string groupName, string nodeName)
    {
        var settings = AppServices.Settings;
        settings.AutoGroupPicks ??= new Dictionary<string, string>();
        if (nodeName.Length == 0) settings.AutoGroupPicks.Remove(groupName);
        else settings.AutoGroupPicks[groupName] = nodeName;
        AppServices.Store.Save(settings);
    }

    public async Task TestGroupDelayAsync(ProxyGroupViewModel group)
    {
        if (group.Members.Count == 0) return;
        if (!AppServices.Runner.IsRunning)
        {
            Ui.Post(() => StatusText = "核心未运行，测速不可用（当前显示的是上次读取的快照）");
            return;
        }
        Ui.Post(() => StatusText = $"正在测速：{group.Name}…");
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = group.Members.Select(async member =>
        {
            await gate.WaitAsync();
            try
            {
                var delay = await AppServices.Clash.GetDelayAsync(member.Name);
                Ui.Post(() => member.DelayMs = delay);
            }
            catch
            {
                Ui.Post(() => member.DelayMs = -1); // -1 表示失败/超时
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        Ui.Post(() =>
        {
            StatusText = $"{group.Name} 测速完成";
            PersistSnapshot(); // 测速结果也记进快照，核心停了还能看到上次的延迟
        });
    }

    public async Task TestAllGroupsAsync()
    {
        foreach (var group in _allGroups.ToList())
            await TestGroupDelayAsync(group);
    }
}

/// <summary>
/// 自定义面板里的一个策略组开关（对应 Bettbox 的 groupSwitches）。
/// </summary>
public partial class GroupSwitchViewModel : ObservableObject
{
    public string Name { get; }

    /// <summary>该组是否启用（true = 会写进配置）。</summary>
    [ObservableProperty] private bool _isEnabled = true;

    public GroupSwitchViewModel(string name, bool isEnabled)
    {
        Name = name;
        _isEnabled = isEnabled;
    }
}

public partial class ProxyGroupViewModel : ObservableObject
{
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush AccentBgBrush = new(Windows.UI.Color.FromArgb(0x1F, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush ActiveChipBgBrush = new(Windows.UI.Color.FromArgb(0x33, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush AutoBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6));
    private static readonly SolidColorBrush AutoBgBrush = new(Windows.UI.Color.FromArgb(0x1F, 0x8B, 0x5C, 0xF6));

    private readonly List<ProxyMemberViewModel> _source = [];

    public string Name { get; }
    public string RawType { get; }
    public string TypeText => IsAutoGroup
        ? "自动测速"
        : RawType switch
        {
            "Selector" or "selector" => "手动选择",
            "URLTest" or "urltest" => "自动测速",
            "Fallback" or "fallback" => "故障转移",
            _ => RawType,
        };

    /// <summary>是否支持手动切换节点。sing-box 的 Clash API 仅对 Selector 组开放
    /// PUT /proxies/{name}；URLTest/Fallback 组会返回 400，伪造的 GLOBAL 项返回 404。</summary>
    public bool IsSelectable => RawType.Equals("Selector", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否是「自动测速组」。Chorus 给自动组加了 selector 包装层（内核本身不给 URLTest 组开选择接口），
    /// 包装层里含一个内层 urltest 组——据此就能认出它并允许用户手动钉住某个节点。
    /// </summary>
    public bool IsAutoGroup { get; private set; }

    /// <summary>内层自动组的 tag（点「交还内核自动选择」时选的就是它）。</summary>
    public string AutoInnerTag { get; private set; } = "";

    /// <summary>
    /// 内层 urltest 当前实际挑中的节点（内核自动选择的结果）。
    /// 空串 = 内核还没测完或刚启动，暂时未知。
    /// </summary>
    [ObservableProperty] private string _autoNow = "";

    partial void OnAutoNowChanged(string value)
    {
        OnPropertyChanged(nameof(SubtitleText));
        UpdateAutoChosen();
    }

    /// <summary>设置自动组信息（从核心读到的组才知道自己是不是自动组）。</summary>
    public void SetAutoInfo(bool isAutoGroup, string autoInnerTag, string autoNow = "")
    {
        IsAutoGroup = isAutoGroup;
        AutoInnerTag = autoInnerTag;
        AutoNow = autoNow;
        OnPropertyChanged(nameof(IsAutoGroup));
        OnPropertyChanged(nameof(AutoInnerTag));
        OnPropertyChanged(nameof(IsAutoSelected));
        OnPropertyChanged(nameof(AutoResetVisibility));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(TypeBadgeText));
        OnPropertyChanged(nameof(TypeBadgeBrush));
        OnPropertyChanged(nameof(TypeBadgeBackground));
        OnPropertyChanged(nameof(SubtitleText));
        UpdateAutoChosen();
    }

    /// <summary>
    /// 当前是否由内核自动挑节点（内层 urltest 被选中）。
    /// 自动组被用户钉住某个节点后为 false。
    /// </summary>
    public bool IsAutoSelected => !IsAutoGroup
                                  || string.Equals(SelectedName, AutoInnerTag, StringComparison.Ordinal);

    /// <summary>「交还内核自动选择」按钮的可见性：自动组且当前不是自动模式时才显示。</summary>
    public Visibility AutoResetVisibility => IsAutoGroup && !IsAutoSelected
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>组头右侧的类型徽标（手动 / 自动）。</summary>
    public string TypeBadgeText => IsAutoGroup ? "自动" : (IsSelectable ? "手动" : "自动");

    public Brush TypeBadgeBrush => IsAutoGroup ? AutoBrush : (IsSelectable ? AccentBrush : AutoBrush);
    public Brush TypeBadgeBackground => IsAutoGroup ? AutoBgBrush : (IsSelectable ? AccentBgBrush : AutoBgBrush);

    /// <summary>是否为当前标签页选中的策略组（驱动顶部标签的选中态）。</summary>
    [ObservableProperty] private bool _isActive;

    public Brush ChipBackground => IsActive ? ActiveChipBgBrush : ThemeBrushes.CardFill;
    public Brush ChipBorderBrush => IsActive ? AccentBrush : ThemeBrushes.CardStroke;
    public Brush ChipForeground => IsActive ? AccentBrush : ThemeBrushes.TextPrimary;
    public Thickness ChipBorderThickness => new(IsActive ? 1.5 : 1);

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipBorderBrush));
        OnPropertyChanged(nameof(ChipForeground));
        OnPropertyChanged(nameof(ChipBorderThickness));
    }

    [ObservableProperty] private string _selectedName = "";

    /// <summary>组头下方的说明：类型 + 当前生效节点。</summary>
    public string SubtitleText
    {
        get
        {
            if (SelectedName.Length == 0) return TypeText;
            // 自动组选中的是内层 urltest 的 tag：实际用的是内核挑中的那个节点
            if (IsAutoGroup && string.Equals(SelectedName, AutoInnerTag, StringComparison.Ordinal))
                return AutoNow.Length > 0
                    ? $"{TypeText} · 由内核自动选择（当前：{AutoNow}）"
                    : $"{TypeText} · 由内核自动选择";
            return $"{TypeText} · {SelectedName}";
        }
    }

    /// <summary>全部成员（未过滤，顺序即配置顺序）。</summary>
    public IReadOnlyList<ProxyMemberViewModel> AllMembers => _source;

    /// <summary>当前展示的成员（搜索过滤后的快照）。</summary>
    [ObservableProperty] private IReadOnlyList<ProxyMemberViewModel> _members = [];

    public string MemberCountText => $"{Members.Count}";
    public string MemberCountLabel => $"{Members.Count} 个节点";

    partial void OnMembersChanged(IReadOnlyList<ProxyMemberViewModel> value)
    {
        OnPropertyChanged(nameof(MemberCountText));
        OnPropertyChanged(nameof(MemberCountLabel));
    }

    public ProxyGroupViewModel(string name, string type)
    {
        Name = name;
        RawType = type;
    }

    public void SetMembers(IEnumerable<ProxyMemberViewModel> members)
    {
        _source.Clear();
        // 内层自动组（◈ 自动·xxx）是包装层的里层，不作为节点展示给用户——
        // 它由「交还内核自动选择」按钮代替。
        _source.AddRange(members.Where(m => !string.Equals(m.Name, AutoInnerTag, StringComparison.Ordinal)));
        UpdateAutoChosen();
        UpdateFilter("");
    }

    /// <summary>
    /// 把「内核自动挑中的节点」标到对应卡片上。只在自动模式且结果已知时生效；
    /// 用户手动钉住节点后由 <see cref="ProxyMemberViewModel.IsSelected"/> 表示选中，两者互斥。
    /// </summary>
    private void UpdateAutoChosen()
    {
        var active = IsAutoGroup && IsAutoSelected && AutoNow.Length > 0;
        foreach (var member in _source)
            member.IsAutoChosen = active && string.Equals(member.Name, AutoNow, StringComparison.Ordinal);
    }

    public void UpdateFilter(string search)
    {
        var matched = search.Length == 0
            ? [.. _source]
            : _source.Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        // 排序放在过滤之后：先按搜索词收窄，再按当前排序方式重排（ Bettbox 的
        // proxiesStyleSetting.sortType 语义，未测速的节点排在已测速之后）
        Members = [.. ProxiesStyleOptions.Sort(
            matched, ProxiesStyleMetrics.SortType, m => m.Name, m => m.DelayMs)];
        RefreshStyle();
    }

    /// <summary>重新计算卡片几何（设置里的密度/尺寸变了之后调用）。</summary>
    public void RefreshStyle()
    {
        OnPropertyChanged(nameof(ItemMinWidth));
        OnPropertyChanged(nameof(ItemMinHeight));
        OnPropertyChanged(nameof(ItemSpacing));
        OnPropertyChanged(nameof(CardPadding));
        OnPropertyChanged(nameof(CardCornerRadius));
        foreach (var m in AllMembers) m.RefreshStyle();
    }

    // ---- 卡片几何（随设置里的密度/尺寸变化，UI 直接绑定）----

    public double ItemMinWidth => ProxiesStyleMetrics.ItemMinWidth;
    public double ItemMinHeight => ProxiesStyleMetrics.ItemMinHeight;
    public double ItemSpacing => ProxiesStyleMetrics.ItemSpacing;
    public Thickness CardPadding => ProxiesStyleMetrics.CardPaddingThickness;
    public CornerRadius CardCornerRadius => ProxiesStyleMetrics.CardCorner;

    partial void OnSelectedNameChanged(string value)
    {
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(AutoResetVisibility));
        OnPropertyChanged(nameof(IsAutoSelected));
        UpdateAutoChosen();
    }
}

public partial class ProxyMemberViewModel : ObservableObject
{
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush SelectedBgBrush = new(Windows.UI.Color.FromArgb(0x26, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush SelectedBorderBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    // 自动组里由内核挑中的节点用紫色，和用户手动钉住的蓝色区分开
    private static readonly SolidColorBrush AutoChosenBorderBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6));
    private static readonly SolidColorBrush AutoChosenBgBrush = new(Windows.UI.Color.FromArgb(0x26, 0x8B, 0x5C, 0xF6));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush GreenBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0xA5, 0x5D));
    private static readonly SolidColorBrush OrangeBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x8A, 0x2B));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));

    public ProxyGroupViewModel Group { get; }
    public string Name { get; }

    /// <summary>Clash API 返回的协议类型（如 VMess / Hysteria2 / Shadowsocks / VLESS / AnyTLS…）。</summary>
    public string ProtocolType { get; init; } = "";

    /// <summary>协议类型徽标文字。</summary>
    public string ProtocolText => ProxyProtocols.Friendly(ProtocolType);

    public Brush ProtocolBrush => ProxyProtocols.Brush(ProtocolType);

    public Brush ProtocolBadgeBackground => ProxyProtocols.BadgeBackground(ProtocolType);

    [ObservableProperty] private bool _isSelected;

    /// <summary>自动测速组里，内核当前挑中的节点（不是用户手动钉住的）。</summary>
    [ObservableProperty] private bool _isAutoChosen;

    /// <summary>最近一次测速延迟（毫秒）；null = 未测速，-1 = 失败/超时。</summary>
    [ObservableProperty] private int? _delayMs;

    public Visibility SelectedVisibility => IsSelected || IsAutoChosen ? Visibility.Visible : Visibility.Collapsed;
    public Brush CheckBrush => IsAutoChosen ? AutoChosenBorderBrush : AccentBrush;
    public Brush CardBackground => IsSelected ? SelectedBgBrush
        : IsAutoChosen ? AutoChosenBgBrush
        : ThemeBrushes.CardFill;
    public Brush CardBorderBrush => IsSelected ? SelectedBorderBrush
        : IsAutoChosen ? AutoChosenBorderBrush
        : ThemeBrushes.CardStroke;
    public Thickness CardBorderThickness => new(IsSelected || IsAutoChosen ? 1.5 : 1);

    public string DelayBadgeText => DelayMs switch
    {
        null => "",
        <= 0 => "超时",
        _ => $"{DelayMs} ms",
    };

    public Brush DelayBrush => DelayMs switch
    {
        null => TransparentBrush,
        <= 0 => RedBrush,
        < 300 => GreenBrush,
        < 800 => OrangeBrush,
        _ => RedBrush,
    };

    public ProxyMemberViewModel(ProxyGroupViewModel group, string name)
    {
        Group = group;
        Name = name;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedVisibility));
        OnPropertyChanged(nameof(CheckBrush));
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardBorderThickness));
    }

    partial void OnIsAutoChosenChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedVisibility));
        OnPropertyChanged(nameof(CheckBrush));
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardBorderThickness));
    }

    partial void OnDelayMsChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayBadgeText));
        OnPropertyChanged(nameof(DelayBrush));
    }

    // ---- 卡片显隐（随设置里的图标样式/尺寸变化）----

    /// <summary>协议徽标是否可见（「仅图标」模式下让位）。</summary>
    public Visibility ProtocolBadgeVisibility => ProxiesStyleMetrics.ShowProtocolBadge
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>延迟数字是否可见。</summary>
    public Visibility DelayBadgeVisibility => ProxiesStyleMetrics.ShowDelayBadge
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>节点名称是否可见（「仅图标」模式下名称收进 ToolTip）。</summary>
    public Visibility NodeNameVisibility => ProxiesStyleMetrics.ShowNodeName
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>展开尺寸下第三行附加信息是否可见。</summary>
    public Visibility ExtraRowVisibility => ProxiesStyleMetrics.ShowExtraRow
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Thickness CardPadding => ProxiesStyleMetrics.CardPaddingThickness;

    public CornerRadius CardCornerRadius => ProxiesStyleMetrics.CardCorner;

    /// <summary>卡片内容水平对齐：「仅图标」模式居中，其余左对齐。</summary>
    public HorizontalAlignment ContentAlignment => ProxiesStyleMetrics.ShowNodeName
        ? HorizontalAlignment.Stretch
        : HorizontalAlignment.Center;

    /// <summary>
    /// 「仅图标」模式下节点名称收进悬浮提示；名称本来就显示着时返回空串——
    /// 空串不会弹出 ToolTip，也就不会在 UIA 树里多出一个同名文本节点
    /// （那会让自动化点击点到提示气泡而不是卡片）。
    /// </summary>
    public string ToolTipName => ProxiesStyleMetrics.ShowNodeName ? "" : Name;

    /// <summary>设置里的密度/尺寸/图标样式变化后刷新卡片显隐。</summary>
    public void RefreshStyle()
    {
        OnPropertyChanged(nameof(ProtocolBadgeVisibility));
        OnPropertyChanged(nameof(DelayBadgeVisibility));
        OnPropertyChanged(nameof(NodeNameVisibility));
        OnPropertyChanged(nameof(ExtraRowVisibility));
        OnPropertyChanged(nameof(CardPadding));
        OnPropertyChanged(nameof(CardCornerRadius));
        OnPropertyChanged(nameof(ContentAlignment));
        OnPropertyChanged(nameof(ToolTipName));
    }
}

/// <summary>
/// 主题资源懒取（Bettbox CommonCard 的 outlineVariant 风格）：未选中卡片与标签底色
/// 沿用主题的卡片底色/描边/主文字色，随深浅主题自动协调。
/// </summary>
internal static class ThemeBrushes
{
    private static readonly SolidColorBrush Transparent = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush DefaultText = new(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F));

    private static Brush? _cardFill;
    private static Brush? _cardStroke;
    private static Brush? _textPrimary;
    private static Brush? _accent;

    public static Brush CardFill
        => _cardFill ??= Lookup("CardBackgroundFillColorDefaultBrush", Transparent);

    public static Brush CardStroke
        => _cardStroke ??= Lookup("CardStrokeColorDefaultBrush", Transparent);

    public static Brush TextPrimary
        => _textPrimary ??= Lookup("TextFillColorPrimaryBrush", DefaultText);

    /// <summary>强调色（选中态边框 / 徽标）。取不到时退回系统蓝。</summary>
    public static Brush Accent
        => _accent ??= Lookup("AccentFillColorDefaultBrush",
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x67, 0xC0)));

    private static Brush Lookup(string key, Brush fallback)
        => Application.Current is { } app
           && app.Resources.ContainsKey(key)
           && app.Resources[key] is Brush brush
            ? brush
            : fallback;
}

/// <summary>节点协议类型的展示名与配色。</summary>
public static class ProxyProtocols
{
    public static string Friendly(string type) => type.ToLowerInvariant() switch
    {
        "shadowsocks" or "ss" => "SS",
        "vmess" => "VMess",
        "vless" => "VLESS",
        "trojan" => "Trojan",
        "hysteria2" or "hy2" => "Hy2",
        "hysteria" => "Hy1",
        "anytls" => "AnyTLS",
        "tuic" => "TUIC",
        "socks" => "Socks",
        "http" => "HTTP",
        "ssh" => "SSH",
        "wireguard" => "WG",
        "selector" or "urltest" or "fallback" => "策略组",
        "direct" => "直连",
        "reject" or "block" => "拦截",
        "dns" => "DNS",
        "" => "未知",
        _ => type,
    };

    private static (byte R, byte G, byte B) ColorOf(string type) => type.ToLowerInvariant() switch
    {
        "vmess" => (0x3E, 0x8F, 0xE0),
        "vless" => (0x8B, 0x5C, 0xF6),
        "trojan" => (0xD1, 0x34, 0x38),
        "shadowsocks" or "ss" => (0x0D, 0x94, 0x88),
        "hysteria2" or "hy2" => (0xEA, 0x58, 0x0C),
        "hysteria" => (0xD9, 0x77, 0x06),
        "tuic" => (0xDB, 0x27, 0x77),
        "anytls" => (0xCA, 0x8A, 0x04),
        "socks" or "http" or "ssh" or "wireguard" or "dns" => (0x64, 0x74, 0x8B),
        "selector" or "urltest" or "fallback" => (0x3E, 0x8F, 0xE0),
        "direct" => (0x16, 0xA3, 0x4A),
        "reject" or "block" => (0xD1, 0x34, 0x38),
        _ => (0x6B, 0x72, 0x80),
    };

    public static SolidColorBrush Brush(string type)
    {
        var (r, g, b) = ColorOf(type);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
    }

    public static SolidColorBrush BadgeBackground(string type)
    {
        var (r, g, b) = ColorOf(type);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, r, g, b));
    }
}

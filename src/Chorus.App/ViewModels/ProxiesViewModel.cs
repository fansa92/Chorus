using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.ClashApi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Chorus.App.ViewModels;

/// <summary>
/// 策略组页（参考 Bettbox / FlClash 的标签页布局）：顶部是策略组切换标签，
/// 点标签切换到另一个策略组；下方只渲染当前组，节点以卡片阵列（自适应多列）排布。
/// </summary>
public partial class ProxiesViewModel : ObservableObject
{
    // 所有策略组（含被搜索过滤隐藏的），用于保留测速结果
    private readonly List<ProxyGroupViewModel> _allGroups = [];
    private bool _refreshing;

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

    /// <summary>当前组内容（阵列）是否可见。</summary>
    public Visibility ContentVisibility
        => SelectedGroup is not null && !IsLoading ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>加载中的提示是否可见。</summary>
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

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
            Ui.Post(() =>
            {
                StatusText = "核心未运行";
                _allGroups.Clear();
                Groups = [];
                IsLoading = false;
                EmptyText = "核心未运行，启动后点刷新";
                UpdateEmptyVisibility();
            });
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
                var gvm = new ProxyGroupViewModel(info.Name, info.Type)
                {
                    SelectedName = info.Now ?? "",
                };
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
            SelectGroup(previousGroupName is null
                ? _allGroups.FirstOrDefault()
                : _allGroups.FirstOrDefault(g => g.Name == previousGroupName) ?? _allGroups.FirstOrDefault());
            ApplyFilter();
            // 顺带说明当前用的是哪份配置：多订阅时组名可能一样，看这句才知道换没换
            var active = AppServices.Settings.ActiveSubscription();
            var configLabel = active is null ? "全部订阅合并" : $"配置「{active.Name}」";
            StatusText = $"已加载 {_allGroups.Count} 个策略组 · {proxies.Count} 个出站 · {configLabel}";
        });
        return true;
    }

    /// <summary>按搜索词过滤组与组内节点。</summary>
    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var visible = new List<ProxyGroupViewModel>(_allGroups.Count);
        foreach (var group in _allGroups)
        {
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
        try
        {
            await AppServices.Clash.SelectAsync(group.Name, member.Name);
            Ui.Post(() =>
            {
                group.SelectedName = member.Name;
                foreach (var m in group.AllMembers)
                    m.IsSelected = m.Name == member.Name;
                StatusText = $"{group.Name} → {member.Name}";
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = "切换失败：" + ex.Message);
        }
    }

    public async Task TestGroupDelayAsync(ProxyGroupViewModel group)
    {
        if (group.Members.Count == 0) return;
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
        Ui.Post(() => StatusText = $"{group.Name} 测速完成");
    }

    public async Task TestAllGroupsAsync()
    {
        foreach (var group in _allGroups.ToList())
            await TestGroupDelayAsync(group);
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
    public string TypeText { get; }

    /// <summary>是否支持手动切换节点。sing-box 的 Clash API 仅对 Selector 组开放
    /// PUT /proxies/{name}；URLTest/Fallback 组会返回 400，伪造的 GLOBAL 项返回 404。</summary>
    public bool IsSelectable => RawType.Equals("Selector", StringComparison.OrdinalIgnoreCase);

    /// <summary>组头右侧的类型徽标（手动 / 自动）。</summary>
    public string TypeBadgeText => IsSelectable ? "手动" : "自动";
    public Brush TypeBadgeBrush => IsSelectable ? AccentBrush : AutoBrush;
    public Brush TypeBadgeBackground => IsSelectable ? AccentBgBrush : AutoBgBrush;

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
    public string SubtitleText => SelectedName.Length == 0
        ? TypeText
        : $"{TypeText} · {SelectedName}";

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
        TypeText = type switch
        {
            "Selector" or "selector" => "手动选择",
            "URLTest" or "urltest" => "自动测速",
            "Fallback" or "fallback" => "故障转移",
            _ => type,
        };
    }

    public void SetMembers(IEnumerable<ProxyMemberViewModel> members)
    {
        _source.Clear();
        _source.AddRange(members);
        UpdateFilter("");
    }

    public void UpdateFilter(string search)
    {
        Members = search.Length == 0
            ? [.. _source]
            : _source.Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    partial void OnSelectedNameChanged(string value) => OnPropertyChanged(nameof(SubtitleText));
}

public partial class ProxyMemberViewModel : ObservableObject
{
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush SelectedBgBrush = new(Windows.UI.Color.FromArgb(0x26, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush SelectedBorderBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
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

    /// <summary>最近一次测速延迟（毫秒）；null = 未测速，-1 = 失败/超时。</summary>
    [ObservableProperty] private int? _delayMs;

    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public Brush CheckBrush => AccentBrush;
    public Brush CardBackground => IsSelected ? SelectedBgBrush : ThemeBrushes.CardFill;
    public Brush CardBorderBrush => IsSelected ? SelectedBorderBrush : ThemeBrushes.CardStroke;
    public Thickness CardBorderThickness => new(IsSelected ? 1.5 : 1);

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
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardBorderThickness));
    }

    partial void OnDelayMsChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayBadgeText));
        OnPropertyChanged(nameof(DelayBrush));
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

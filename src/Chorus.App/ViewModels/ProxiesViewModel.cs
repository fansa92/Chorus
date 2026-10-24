using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.ClashApi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Chorus.App.ViewModels;

/// <summary>
/// 策略组页（参考 Bettbox / FlClash Proxies 页）：顶部横向策略组选择器，
/// 下方展示所选组的节点列表；每行显示选中标记、协议类型徽标、名称与延迟。
/// </summary>
public partial class ProxiesViewModel : ObservableObject
{
    // 所有策略组（含被搜索过滤隐藏的），用于保留选中状态与延迟结果
    private readonly List<ProxyGroupViewModel> _allGroups = [];

    public ObservableCollection<ProxyGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private ProxyGroupViewModel? _selectedGroup;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "";

    public ProxiesViewModel()
    {
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedGroupChanged(ProxyGroupViewModel? value)
    {
        // 同步横向选择器里各组的选中态视觉
        foreach (var g in _allGroups)
            g.IsSelected = ReferenceEquals(g, value);
        OnPropertyChanged(nameof(SelectedGroupNodes));
        OnPropertyChanged(nameof(NodesEmptyVisibility));
        OnPropertyChanged(nameof(HasSelectedGroup));
    }

    /// <summary>当前选中组展示的节点（搜索过滤后）。</summary>
    public ObservableCollection<ProxyMemberViewModel>? SelectedGroupNodes => SelectedGroup?.Members;

    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>节点列表空态提示的可见性。</summary>
    public Visibility NodesEmptyVisibility =>
        SelectedGroup is null || SelectedGroup.Members.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public async Task RefreshAsync()
    {
        if (!AppServices.Runner.IsRunning)
        {
            Ui.Post(() =>
            {
                StatusText = "核心未运行";
                _allGroups.Clear();
                Groups.Clear();
                SelectedGroup = null;
            });
            return;
        }

        try
        {
            var proxies = await AppServices.Clash.GetProxiesAsync();
            var groupInfos = proxies.Values
                .Where(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase)
                         || p.Type.Equals("URLTest", StringComparison.OrdinalIgnoreCase)
                         || p.Type.Equals("Fallback", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            Ui.Post(() =>
            {
                // 记住刷新前的选中组与各组测速结果，刷新后按组名恢复
                var previousName = SelectedGroup?.Name;
                var oldDelays = _allGroups
                    .SelectMany(g => g.AllMembers)
                    .Where(m => m.DelayMs.HasValue)
                    .GroupBy(m => (Group: m.Group.Name, m.Name))
                    .ToDictionary(g => g.Key, g => g.First().DelayMs);

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

                // 先恢复选中组，再应用搜索过滤
                SelectedGroup = _allGroups.FirstOrDefault(g => g.Name == previousName);
                ApplyFilter();
                if (SelectedGroup is null)
                    SelectedGroup = Groups.FirstOrDefault();
                StatusText = $"已加载 {_allGroups.Count} 个策略组";
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = "刷新失败：" + ex.Message);
        }
    }

    /// <summary>按搜索词过滤节点与组；无搜索词时展示全部组。</summary>
    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        Groups.Clear();
        foreach (var group in _allGroups)
        {
            group.UpdateFilter(search);
            if (search.Length > 0
                && group.Members.Count == 0
                && !group.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue; // 搜索时隐藏名称与节点都不匹配的组
            Groups.Add(group);
        }

        // 选中逻辑：搜索时优先停留在仍有匹配的组；否则保持当前选中（若仍可见），否则选第一个
        ProxyGroupViewModel? target;
        if (search.Length > 0)
        {
            target = SelectedGroup is not null && SelectedGroup.Members.Count > 0 && Groups.Contains(SelectedGroup)
                ? SelectedGroup
                : Groups.FirstOrDefault(g => g.Members.Count > 0);
        }
        else
        {
            target = SelectedGroup is not null && Groups.Contains(SelectedGroup)
                ? SelectedGroup
                : Groups.FirstOrDefault();
        }
        if (!ReferenceEquals(target, SelectedGroup))
            SelectedGroup = target;
        else
            OnPropertyChanged(nameof(NodesEmptyVisibility)); // 过滤可能改变空态
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
        // 遍历全部组（不受当前搜索过滤影响）
        foreach (var group in _allGroups.ToList())
            await TestGroupDelayAsync(group);
    }
}

public partial class ProxyGroupViewModel : ObservableObject
{
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush SelectedBgBrush = new(Windows.UI.Color.FromArgb(0x1A, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush SelectedBorderBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush GrayBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x9B, 0x9B, 0x9B));

    private readonly List<ProxyMemberViewModel> _source = [];

    public string Name { get; }
    public string RawType { get; }
    public string TypeText { get; }

    /// <summary>
    /// 是否支持手动切换节点。sing-box 的 Clash API 仅对 Selector 组开放
    /// PUT /proxies/{name}；URLTest/Fallback 组会返回 400，伪造的 GLOBAL 项返回 404。
    /// </summary>
    public bool IsSelectable => RawType.Equals("Selector", StringComparison.OrdinalIgnoreCase);

    /// <summary>该组是否为横向选择器中当前选中的组。</summary>
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _selectedName = "";

    /// <summary>全部成员（未过滤）。</summary>
    public ObservableCollection<ProxyMemberViewModel> AllMembers { get; } = [];

    /// <summary>当前展示的成员（搜索过滤后）。</summary>
    public ObservableCollection<ProxyMemberViewModel> Members { get; } = [];

    public string MemberCountText => $"{Members.Count} 节点";

    public string SubtitleText => SelectedName.Length == 0
        ? TypeText
        : $"{TypeText} · {SelectedName}";

    // ---- 横向选择器 chip 的选中态视觉 ----
    public Brush ChipBackground => IsSelected ? SelectedBgBrush : TransparentBrush;
    public Brush ChipBorderBrush => IsSelected ? SelectedBorderBrush : TransparentBrush;
    public Brush ChipDotBrush => IsSelected ? AccentBrush : GrayBrush;

    public ProxyGroupViewModel(string name, string type)
    {
        Name = name;
        RawType = type;
        TypeText = type switch
        {
            "Selector" or "selector" => "手动选择",
            "URLTest" or "urltest" => "自动测速",
            "Fallback" => "故障转移",
            _ => type,
        };
    }

    public void SetMembers(IEnumerable<ProxyMemberViewModel> members)
    {
        _source.Clear();
        _source.AddRange(members);
        AllMembers.Clear();
        foreach (var m in _source) AllMembers.Add(m);
        UpdateFilter("");
    }

    public void UpdateFilter(string search)
    {
        Members.Clear();
        if (search.Length == 0)
        {
            foreach (var m in _source) Members.Add(m);
        }
        else
        {
            foreach (var m in _source)
                if (m.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                    Members.Add(m);
        }
        OnPropertyChanged(nameof(MemberCountText));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipBorderBrush));
        OnPropertyChanged(nameof(ChipDotBrush));
    }

    partial void OnSelectedNameChanged(string value) => OnPropertyChanged(nameof(SubtitleText));
}

public partial class ProxyMemberViewModel : ObservableObject
{
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush WhiteBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SelectedBgBrush = new(Windows.UI.Color.FromArgb(0x26, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush SelectedBorderBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush GreenBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0xA5, 0x5D));
    private static readonly SolidColorBrush OrangeBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x8A, 0x2B));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));

    // 未选中卡片沿用主题的卡片底色/描边（Bettbox CommonCard 的 outlineVariant 风格），
    // 首次访问时从主题资源取，随深浅主题自动协调。
    private static Brush? _idleCardBrush;
    private static Brush? _idleBorderBrush;

    private static Brush IdleCardBrush
    {
        get
        {
            if (_idleCardBrush is null
                && Application.Current.Resources.ContainsKey("CardBackgroundFillColorDefaultBrush")
                && Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] is Brush b)
                _idleCardBrush = b;
            return _idleCardBrush ?? TransparentBrush;
        }
    }

    private static Brush IdleBorderBrush
    {
        get
        {
            if (_idleBorderBrush is null
                && Application.Current.Resources.ContainsKey("CardStrokeColorDefaultBrush")
                && Application.Current.Resources["CardStrokeColorDefaultBrush"] is Brush b)
                _idleBorderBrush = b;
            return _idleBorderBrush ?? TransparentBrush;
        }
    }

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
    public Brush CardBackground => IsSelected ? SelectedBgBrush : IdleCardBrush;
    public Brush CardBorderBrush => IsSelected ? SelectedBorderBrush : IdleBorderBrush;
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

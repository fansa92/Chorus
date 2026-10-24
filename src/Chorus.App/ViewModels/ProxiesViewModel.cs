using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.ClashApi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Chorus.App.ViewModels;

/// <summary>
/// 策略组页（参考 Bettbox Proxies 页）：组卡片 + 展开的节点卡片，
/// 支持按名称搜索、整组测速、点击节点切换。
/// </summary>
public partial class ProxiesViewModel : ObservableObject
{
    // 所有策略组（含被搜索过滤隐藏的），用于保留展开状态与延迟结果
    private readonly List<ProxyGroupViewModel> _allGroups = [];

    public ObservableCollection<ProxyGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "";

    public ProxiesViewModel()
    {
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task RefreshAsync()
    {
        if (!AppServices.Runner.IsRunning)
        {
            Ui.Post(() => StatusText = "核心未运行");
            return;
        }

        try
        {
            var proxies = await AppServices.Clash.GetProxiesAsync();
            var groups = proxies.Values
                .Where(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase)
                         || p.Type.Equals("URLTest", StringComparison.OrdinalIgnoreCase)
                         || p.Type.Equals("Fallback", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            Ui.Post(() =>
            {
                // 保留同名旧组的展开状态
                var expanded = _allGroups.Where(g => g.IsExpanded).Select(g => g.Name).ToHashSet();

                _allGroups.Clear();
                foreach (var group in groups)
                {
                    var gvm = new ProxyGroupViewModel(group.Name, group.Type)
                    {
                        SelectedName = group.Now ?? "",
                        IsExpanded = expanded.Contains(group.Name),
                    };
                    var members = group.All.Select(name => new ProxyMemberViewModel(gvm, name)
                    {
                        IsSelected = name == group.Now,
                    }).ToList();
                    gvm.SetMembers(members);
                    _allGroups.Add(gvm);
                }
                ApplyFilter();
                StatusText = $"已加载 {_allGroups.Count} 个策略组";
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = "刷新失败：" + ex.Message);
        }
    }

    /// <summary>按搜索词过滤节点；无搜索词时展示全部组，有匹配时自动展开。</summary>
    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        Groups.Clear();
        foreach (var group in _allGroups)
        {
            group.UpdateFilter(search);
            if (search.Length > 0)
            {
                if (group.Members.Count == 0) continue; // 搜索时隐藏无匹配的组
                group.IsExpanded = true; // 搜索时自动展开有匹配的组
            }
            Groups.Add(group);
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
                foreach (var m in group.Members)
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
        foreach (var group in Groups.ToList())
            await TestGroupDelayAsync(group);
    }
}

public partial class ProxyGroupViewModel : ObservableObject
{
    private readonly List<ProxyMemberViewModel> _source = [];

    public string Name { get; }
    public string TypeText { get; }

    /// <summary>组名首字符（通常是 emoji），用作组图标。</summary>
    public string IconLetter => Name.Length == 0 ? "?" : Name[..1];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _selectedName = "";

    /// <summary>全部成员（未过滤）。</summary>
    public ObservableCollection<ProxyMemberViewModel> AllMembers { get; } = [];

    /// <summary>当前展示的成员（搜索过滤后）。</summary>
    public ObservableCollection<ProxyMemberViewModel> Members { get; } = [];

    public Visibility NodesVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>展开/收起箭头（&#xE70E; 向上 / &#xE70D; 向下）。</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    public string SubtitleText => SelectedName.Length == 0
        ? TypeText
        : $"{TypeText} · {SelectedName}";

    public ProxyGroupViewModel(string name, string type)
    {
        Name = name;
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
            return;
        }
        foreach (var m in _source)
            if (m.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                Members.Add(m);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(NodesVisibility));
        OnPropertyChanged(nameof(ExpandGlyph));
    }
    partial void OnSelectedNameChanged(string value) => OnPropertyChanged(nameof(SubtitleText));
}

public partial class ProxyMemberViewModel : ObservableObject
{
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush SelectedBgBrush = new(Windows.UI.Color.FromArgb(0x1A, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush SelectedBorderBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush GreenBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0xA5, 0x5D));
    private static readonly SolidColorBrush OrangeBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x8A, 0x2B));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));
    private static readonly SolidColorBrush GrayBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x9B, 0x9B, 0x9B));

    public ProxyGroupViewModel Group { get; }
    public string Name { get; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>最近一次测速延迟（毫秒）；null = 未测速，-1 = 失败/超时。</summary>
    [ObservableProperty] private int? _delayMs;

    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public Brush CheckBrush => AccentBrush;
    public Brush CardBackground => IsSelected ? SelectedBgBrush : TransparentBrush;
    public Brush CardBorderBrush => IsSelected ? SelectedBorderBrush : TransparentBrush;

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
    }

    partial void OnDelayMsChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayBadgeText));
        OnPropertyChanged(nameof(DelayBrush));
    }
}

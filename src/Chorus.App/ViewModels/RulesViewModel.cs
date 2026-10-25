using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Chorus.App.ViewModels;

/// <summary>
/// 规则页：展示真正交给核心的那份配置里的分流规则。
/// 三个标签分别是 路由规则 / 规则集 / DNS，数据来自 config.json（未启动核心时用当前设置现场生成）。
/// </summary>
public partial class RulesViewModel : ObservableObject
{
    // 注意：这里的列表是「整体替换的快照」，不是会被逐条增删的 ObservableCollection。
    // 页面（ItemsRepeater）重新进入时同一个 ViewModel 会被再次绑定，如果沿用
    // ObservableCollection，就要求集合通知的订阅者（旧页面的 repeater）安全地活着；
    // 而 WinUI 的 ItemsRepeater 在页面卸载后仍可能挂着订阅，下一次 Clear()/Add()
    // 就会抛 COMException 0x8000FFFF（E_UNEXPECTED），表现为「规则页白屏 / 闪退」。
    // 换成快照属性后，一次刷新只发一次 PropertyChanged，由绑定去换 ItemsSource。
    [ObservableProperty] private IReadOnlyList<RuleRowViewModel> _rules = [];
    [ObservableProperty] private IReadOnlyList<RuleSetViewModel> _ruleSets = [];
    [ObservableProperty] private IReadOnlyList<DnsServerViewModel> _dnsServers = [];
    [ObservableProperty] private IReadOnlyList<RuleRowViewModel> _dnsRules = [];
    [ObservableProperty] private IReadOnlyList<RulesTabViewModel> _tabs = [];

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _finalText = "—";
    [ObservableProperty] private string _dnsFinalText = "—";
    [ObservableProperty] private string _ruleSetHintText = "";
    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Collapsed;
    [ObservableProperty] private bool _hasContent = true;
    [ObservableProperty] private string _emptyText = "";

    public RulesViewModel()
    {
        Tabs =
        [
            new RulesTabViewModel(0, "路由规则"),
            new RulesTabViewModel(1, "规则集"),
            new RulesTabViewModel(2, "DNS"),
        ];
        SelectTab(0);
        Refresh();
    }

    /// <summary>有可用配置时才显示规则列表（避免空态里还挂着一行「以上都不匹配」）。</summary>
    public Visibility ContentVisibility => HasContent ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RouteVisibility => TabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuleSetVisibility => TabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DnsVisibility => TabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnHasContentChanged(bool value) => OnPropertyChanged(nameof(ContentVisibility));

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RouteVisibility));
        OnPropertyChanged(nameof(RuleSetVisibility));
        OnPropertyChanged(nameof(DnsVisibility));
    }

    /// <summary>切换标签页。</summary>
    public void SelectTab(int index)
    {
        if (index is < 0 or > 2) index = 0;
        foreach (var tab in Tabs) tab.IsActive = tab.Index == index;
        TabIndex = index;
    }

    /// <summary>重新读取配置并重建三个标签的内容。</summary>
    public void Refresh()
    {
        JsonObject? config;
        string source;
        try
        {
            var configPath = Path.Combine(AppServices.DataDir, "config.json");
            if (AppServices.Runner.IsRunning && File.Exists(configPath))
            {
                config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                source = "正在运行的配置";
            }
            else
            {
                // 核心没跑时磁盘上的 config.json 可能是上一次运行的旧配置，
                // 按当前设置现场生成一份，页面看到的才是「下次启动会生效的规则」
                config = ConfigGenerator.Build(AppServices.Settings);
                source = "当前设置（尚未启动核心）";
            }
        }
        catch (Exception ex)
        {
            Rules = [];
            RuleSets = [];
            DnsServers = [];
            DnsRules = [];
            SourceText = "";
            StatusText = "无法生成配置：" + ex.Message;
            EmptyText = "没有可用节点，先在订阅页导入节点后回到这里";
            EmptyVisibility = Visibility.Visible;
            HasContent = false;
            UpdateTabCounts();
            return;
        }

        var model = ConfigInspector.Inspect(config);

        var rules = new List<RuleRowViewModel>(model.Rules.Count);
        foreach (var rule in model.Rules)
            rules.Add(new RuleRowViewModel(rule.Index, rule.Conditions, rule.ActionText, rule.Outbound, rule.RawJson,
                isReject: rule.ActionKind.Equals("reject", StringComparison.OrdinalIgnoreCase)));

        var ruleSets = new List<RuleSetViewModel>(model.RuleSets.Count);
        foreach (var set in model.RuleSets)
        {
            // 规则集卡片需要显示「被哪几条规则用到」，反查一次
            var usedBy = model.Rules.Count(r => r.RuleSetRefs.Contains(set.Tag, StringComparer.Ordinal));
            ruleSets.Add(new RuleSetViewModel(set, usedBy));
        }

        var dnsServers = new List<DnsServerViewModel>(model.DnsServers.Count);
        foreach (var server in model.DnsServers)
            dnsServers.Add(new DnsServerViewModel(server));

        var dnsRules = new List<RuleRowViewModel>(model.DnsRules.Count);
        foreach (var rule in model.DnsRules)
            dnsRules.Add(new RuleRowViewModel(rule.Index, rule.Conditions, rule.ActionText, null, rule.RawJson,
                isReject: false));

        Rules = rules;
        RuleSets = ruleSets;
        DnsServers = dnsServers;
        DnsRules = dnsRules;

        FinalText = model.Final ?? "（未设置，使用出站列表第一个）";
        DnsFinalText = model.DnsFinal ?? "（未设置）";

        var sub = AppServices.Settings.EffectiveSubscriptions()
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.RouteJson) || !string.IsNullOrWhiteSpace(s.DnsJson));
        SourceText = sub is null
            ? $"{source} · 未使用订阅规则"
            : $"{source} · 规则来自订阅「{sub.Name}」";

        var ruleSetBytes = AppServices.Settings.EffectiveSubscriptions()
            .Any(s => !string.IsNullOrWhiteSpace(s.RouteJson));
        RuleSetHintText = RuleSets.Count == 0
            ? "当前配置没有引用规则集。订阅里带 rule_set 时，此处会列出可更新的 .srs；也可以到设置页打开「使用订阅自带的分流规则」。"
            : $"共 {RuleSets.Count} 个规则集，远程规则集由核心在启动时下载并缓存{(ruleSetBytes ? "" : "（当前没有来自订阅的规则集）")}。";

        HasContent = Rules.Count > 0;
        EmptyVisibility = Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText = Rules.Count == 0 ? "配置里没有路由规则" : "";
        StatusText = $"路由 {Rules.Count} 条 · 规则集 {RuleSets.Count} 个 · DNS {DnsServers.Count} 台";
        UpdateTabCounts();
    }

    private void UpdateTabCounts()
    {
        Tabs[0].CountLabel = Rules.Count.ToString();
        Tabs[1].CountLabel = RuleSets.Count.ToString();
        Tabs[2].CountLabel = DnsServers.Count.ToString();
    }

    /// <summary>
    /// 重新下载规则集。sing-box 没有「重新拉取规则集」的接口，只能清掉缓存再重启核心，
    /// 核心启动时会重新下载所有 remote rule_set。返回给界面用于提示。
    /// </summary>
    public async Task<string> RefreshRuleSetsAsync()
    {
        if (RuleSets.Count == 0) return "当前配置没有引用任何规则集。";
        if (!AppServices.Runner.IsRunning) return "核心未运行，下次启动时会自动下载规则集。";

        var cacheDir = Path.Combine(AppServices.DataDir, "data");
        try
        {
            await AppServices.Runner.StopAsync();
            foreach (var file in new[] { "cache.db", "cache.db-shm", "cache.db-wal" })
            {
                var path = Path.Combine(cacheDir, file);
                if (File.Exists(path)) File.Delete(path);
            }
            await AppServices.Runner.StartAsync();
            Refresh();
            return "已清理规则集缓存并重启核心，核心正在重新下载规则集（详见日志页）。";
        }
        catch (Exception ex)
        {
            return "重新下载规则集失败：" + ex.Message;
        }
    }
}

/// <summary>顶部标签。</summary>
public partial class RulesTabViewModel : ObservableObject
{
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush ActiveBgBrush = new(Windows.UI.Color.FromArgb(0x33, 0x3E, 0x8F, 0xE0));

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _countLabel = "0";

    public RulesTabViewModel(int index, string title)
    {
        Index = index;
        Title = title;
    }

    public int Index { get; }
    public string Title { get; }

    public Brush ChipBackground => IsActive ? ActiveBgBrush : ThemeBrushes.CardFill;
    public Brush ChipBorderBrush => IsActive ? AccentBrush : ThemeBrushes.CardStroke;
    public Brush ChipForeground => IsActive ? AccentBrush : ThemeBrushes.TextPrimary;

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipBorderBrush));
        OnPropertyChanged(nameof(ChipForeground));
    }
}

/// <summary>路由规则 / DNS 规则的一行（点开看原始 JSON）。</summary>
public partial class RuleRowViewModel : ObservableObject
{
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush RejectBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38));
    private static readonly SolidColorBrush ViolettBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6));

    [ObservableProperty] private bool _isExpanded;

    public RuleRowViewModel(
        int index, IReadOnlyList<string> conditions, string actionText, string? outbound, string rawJson, bool isReject)
    {
        Index = index;
        Conditions = conditions.Count == 0 ? new List<string> { "（无匹配条件）" } : conditions;
        ActionText = actionText;
        Outbound = outbound;
        RawJson = PrettyJson(rawJson);
        IsReject = isReject;
    }

    public int Index { get; }
    public IReadOnlyList<string> Conditions { get; }
    public string ActionText { get; }
    public string? Outbound { get; }
    public string RawJson { get; }
    public bool IsReject { get; }

    public string IndexText => Index.ToString("00");
    public string ConditionsText => string.Join("  ·  ", Conditions);

    /// <summary>动作徽标颜色：拒绝用红、劫持/嗅探用紫、其它用 accent。</summary>
    public Brush ActionBrush => IsReject
        ? RejectBrush
        : ActionText.StartsWith("路由", StringComparison.Ordinal) ? AccentBrush : ViolettBrush;

    public Brush ActionBackground => IsReject
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0xD1, 0x34, 0x38))
        : ActionText.StartsWith("路由", StringComparison.Ordinal)
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x3E, 0x8F, 0xE0))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x8B, 0x5C, 0xF6));

    public Visibility DetailVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(ExpandGlyph));
    }

    private static readonly System.Text.Json.JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        // 规则里常有中文/emoji 的节点名，默认编码器会转成 \uXXXX，展开后很难认
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string PrettyJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(PrettyOptions) ?? json;
        }
        catch
        {
            return json;
        }
    }
}

/// <summary>规则集卡片。</summary>
public partial class RuleSetViewModel : ObservableObject
{
    private static readonly SolidColorBrush RemoteBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush LocalBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0xA5, 0x5D));

    public RuleSetViewModel(RuleSetInfo info, int usedBy)
    {
        Info = info;
        UsedBy = usedBy;
    }

    public RuleSetInfo Info { get; }
    public int UsedBy { get; }

    public string Tag => Info.Tag;
    public string MetaText => Info.MetaText;
    public string PathText => Info.PathText;
    public string UrlText => Info.UrlText;
    public string HostText => Info.HostText;
    public Brush TypeBrush => Info.IsRemote ? RemoteBrush : LocalBrush;
    public Brush TypeBackground => Info.IsRemote
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x3E, 0x8F, 0xE0))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x3B, 0xA5, 0x5D));
    public string UsedByText => UsedBy == 0 ? "未被规则引用" : $"被 {UsedBy} 条规则引用";
}

/// <summary>DNS 服务器卡片。</summary>
public partial class DnsServerViewModel : ObservableObject
{
    private static readonly SolidColorBrush ProxyBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x8F, 0xE0));
    private static readonly SolidColorBrush DirectBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0xA5, 0x5D));
    private static readonly SolidColorBrush FakeIpBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6));

    public DnsServerViewModel(DnsServerInfo info) => Info = info;

    public DnsServerInfo Info { get; }

    public string Tag => Info.Tag;
    public string TypeText => Info.Type.ToUpperInvariant();
    public string AddressText => Info.AddressText;
    public string MetaText => Info.MetaText;

    public Brush AccentBrush => Info.IsFakeIp ? FakeIpBrush : Info.Detour is { Length: > 0 } ? ProxyBrush : DirectBrush;

    public Brush BadgeBackground => Info.IsFakeIp
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x8B, 0x5C, 0xF6))
        : Info.Detour is { Length: > 0 }
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x3E, 0x8F, 0xE0))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0x1F, 0x3B, 0xA5, 0x5D));
}

namespace Chorus.Core.Models;

/// <summary>一个出站节点（来自订阅或手动添加），由 ConfigGenerator 映射为 sing-box outbound。</summary>
public sealed class NodeEntry
{
    public string Protocol { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Server { get; set; } = "";
    public int ServerPort { get; set; }
    public string? Uuid { get; set; }
    public string? Password { get; set; }
    public string? Method { get; set; }
    public int AlterId { get; set; }
    public string? Security { get; set; }
    public string? Flow { get; set; }
    public string? Transport { get; set; }
    public string? Host { get; set; }
    public string? Path { get; set; }
    public string? ServiceName { get; set; }
    public bool Tls { get; set; }
    public string? Sni { get; set; }
    public bool Insecure { get; set; }
    public string? Alpn { get; set; }
    public string? Fingerprint { get; set; }
    public string? RealityPublicKey { get; set; }
    public string? RealityShortId { get; set; }
    public string? ObfsPassword { get; set; }
}

/// <summary>用户自定义策略组。Members 支持：精确节点名、精确组名（嵌套）、"*"（全部节点）、通配符（如 *香港*）。</summary>
public sealed class ProxyGroup
{
    public string Name { get; set; } = "";
    /// <summary>"selector"（手动选择）或 "urltest"（自动测速）。</summary>
    public string Type { get; set; } = "selector";
    public List<string> Members { get; set; } = [];
    /// <summary>selector 的默认选中项（订阅配置里的 default 字段）。</summary>
    public string? Default { get; set; }
    /// <summary>urltest 的测速地址（订阅配置里的 url 字段）。</summary>
    public string? Url { get; set; }
    /// <summary>urltest 的测速间隔（订阅配置里的 interval 字段，如 "3m"）。</summary>
    public string? Interval { get; set; }
}

public sealed class Profile
{
    /// <summary>手动添加的节点（设置页粘贴分享链接导入）。</summary>
    public List<NodeEntry> Nodes { get; set; } = [];
    public List<ProxyGroup> Groups { get; set; } = [];
}

/// <summary>一个订阅源：URL + 其解析出的节点/策略组 + 更新状态。多个订阅的节点与手动节点合并后生效。</summary>
public sealed class SubscriptionEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public List<NodeEntry> Nodes { get; set; } = [];
    /// <summary>订阅配置里的策略组（selector/urltest），与节点一样随订阅更新。</summary>
    public List<ProxyGroup> Groups { get; set; } = [];

    /// <summary>
    /// sing-box 原生订阅里自带的 route 片段（rules / rule_set / final 的原始 JSON）。
    /// 生成配置时会被合并进去，规则页也直接展示这些内容。
    /// </summary>
    public string? RouteJson { get; set; }

    /// <summary>订阅自带的 dns 段原始 JSON。</summary>
    public string? DnsJson { get; set; }

    public DateTimeOffset? LastUpdated { get; set; }
    public string? LastError { get; set; }

    /// <summary>最近一次配置检查是否通过（sing-box check）。null = 还没检查过。</summary>
    public bool? LastCheckOk { get; set; }

    /// <summary>检查失败时的原因（已去掉 sing-box 的日志前缀与临时文件路径）。</summary>
    public string? LastCheckError { get; set; }

    /// <summary>最近一次配置检查的时间。</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>检查通过时的规模摘要，如「63 个节点 · 16 个策略组 · 14 条路由规则 · 12 个规则集」。</summary>
    public string? LastCheckSummary { get; set; }
}

public sealed class AppSettings
{
    public int MixedPort { get; set; } = 7897;
    public int ClashApiPort { get; set; } = 9090;
    public string ClashApiSecret { get; set; } = "";
    public bool AllowLan { get; set; }
    public bool EnableTun { get; set; }
    public bool EnableSystemProxy { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string LogLevel { get; set; } = "info";

    /// <summary>
    /// 拉取订阅时上报的 User-Agent。订阅转换服务按 UA 决定返回格式：
    /// 报 sing-box 得到原生 JSON 配置（含 selector/urltest 策略组），报 Clash 客户端得到 YAML。
    /// </summary>
    public string SubscriptionUserAgent { get; set; } = "sing-box";

    /// <summary>
    /// 更新订阅时只走直连、不经核心代理。
    /// 默认关闭（有代理时优先经核心，订阅站在墙外也能更新）；订阅站可直连、
    /// 或者当前配置的代理走不通时打开它，更新就不再依赖正在运行的配置。
    /// </summary>
    public bool SubscriptionDirectUpdate { get; set; }

    public List<SubscriptionEntry> Subscriptions { get; set; } = [];
    public Profile Profile { get; set; } = new();

    /// <summary>
    /// 当前选中的订阅（用 Url 唯一标识）。与 Bettbox 一样同一次只有一份配置生效：
    /// 只有选中的订阅里的节点/策略组/路由规则会参与生成配置，手动节点始终附加在后面。
    /// 为空表示还没选过——此时退回「全部订阅合并生效」的老行为。
    /// </summary>
    public string? ActiveSubscriptionUrl { get; set; }

    /// <summary>
    /// 是否采用订阅自带的 route/dns（规则集分流）。关闭后只用 Chorus 内置的两条模式规则。
    /// 默认开启：订阅转换服务给的规则集才是真正的分流依据。
    /// </summary>
    public bool ApplySubscriptionRouting { get; set; } = true;

    /// <summary>
    /// 是否启用 IPv6。对应 sing-box 的 dns.strategy：
    /// 开启用 prefer_ipv6（能走 IPv6 就走），关闭用 ipv4_only（只解析 A 记录）。
    /// 改这个值需要重启核心。
    /// </summary>
    public bool EnableIPv6 { get; set; } = true;

    /// <summary>
    /// 界面主题："system"（跟随系统）/ "light" / "dark"。
    /// </summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 界面语言："system"（跟随系统）/ "zh-Hans" / "en-US"。
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// 被「自定义」面板关闭的策略组名（参考 Bettbox 的 groupSwitches）。
    /// 关闭的组不会写进生成的配置，路由里引用它的规则也会被剔除。
    /// </summary>
    public List<string> DisabledGroups { get; set; } = [];

    /// <summary>
    /// 自动测速（urltest）组的手动固定：组名 → 节点名。
    /// sing-box 的 urltest 组不能被 Clash API 直接选中，所以 Chorus 为每个自动组
    /// 生成一个同名 selector 包装层，点节点就是在这个包装层上选；
    /// 值为空字符串表示「交还内核自动选择」。
    /// </summary>
    public Dictionary<string, string> AutoGroupPicks { get; set; } = [];

    // ---- 策略组页显示设置（参考 Bettbox 代理页的 ProxiesSetting）----
    // 这一组只影响「怎么展示」，不写进 sing-box 配置，改完立刻生效、无需重启核心。

    /// <summary>
    /// 策略组页布局风格："tab"（标签页，一次显示一个组）/ "list"（列表，所有组纵向堆叠）。
    /// </summary>
    public string ProxiesViewType { get; set; } = "tab";

    /// <summary>策略组内节点排序："none"（按核心返回顺序）/ "delay"（延迟升序）/ "name"（名称）。</summary>
    public string ProxiesSortType { get; set; } = "none";

    /// <summary>卡片密度："tight"（紧凑）/ "standard"（标准）/ "loose"（宽松）。</summary>
    public string ProxiesLayout { get; set; } = "standard";

    /// <summary>卡片尺寸："expand"（展开，信息最全）/ "shrink"（标准）/ "min"（最小，单行）。</summary>
    public string ProxiesCardType { get; set; } = "shrink";

    /// <summary>节点卡片图标样式："standard"（协议徽标 + 延迟）/ "none"（只文字）/ "icon"（只图标）。</summary>
    public string ProxiesIconStyle { get; set; } = "standard";

    /// <summary>列表视图下，滚动时组头是否自动吸顶（只影响列表模式）。</summary>
    public bool ProxiesAutoStickyHeader { get; set; } = true;

    /// <summary>是否显示被「自定义」面板关掉的策略组（关闭时列表里不出现这些组）。</summary>
    public bool ProxiesShowHiddenItems { get; set; }

    /// <summary>当前选中的订阅；未选中（或选中的已被删除）时返回 null。</summary>
    public SubscriptionEntry? ActiveSubscription() =>
        string.IsNullOrWhiteSpace(ActiveSubscriptionUrl)
            ? null
            : Subscriptions.FirstOrDefault(s =>
                string.Equals(s.Url, ActiveSubscriptionUrl, StringComparison.OrdinalIgnoreCase));

    /// <summary>真正参与生成配置的订阅集合：选中了一个就只用它，否则用全部（旧行为）。</summary>
    public List<SubscriptionEntry> EffectiveSubscriptions()
    {
        var active = ActiveSubscription();
        return active is null ? Subscriptions : [active];
    }

    /// <summary>
    /// 保证「当前使用的订阅」指向一个真实存在的条目：首次使用、删除了当前订阅之后调用。
    /// 没有订阅时清空。
    /// </summary>
    public void EnsureActiveSubscription()
    {
        if (Subscriptions.Count == 0)
        {
            ActiveSubscriptionUrl = null;
            return;
        }
        if (ActiveSubscription() is null)
            ActiveSubscriptionUrl = Subscriptions[0].Url;
    }

    /// <summary>生效的节点全集：手动节点 + 生效订阅的节点（按此顺序合并）。</summary>
    public List<NodeEntry> EffectiveNodes()
    {
        var all = new List<NodeEntry>(Profile.Nodes);
        foreach (var sub in EffectiveSubscriptions())
            all.AddRange(sub.Nodes);
        return all;
    }

    /// <summary>生效的策略组全集：手动组 + 生效订阅的组（按此顺序合并，同名去重保留第一个）。</summary>
    public List<ProxyGroup> EffectiveGroups()
    {
        var all = new List<ProxyGroup>(Profile.Groups);
        foreach (var sub in EffectiveSubscriptions())
            all.AddRange(sub.Groups);
        return all
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(g => g.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }
}

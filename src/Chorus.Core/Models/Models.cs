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

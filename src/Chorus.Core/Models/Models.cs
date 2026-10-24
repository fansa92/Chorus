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
    public DateTimeOffset? LastUpdated { get; set; }
    public string? LastError { get; set; }
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
    public List<SubscriptionEntry> Subscriptions { get; set; } = [];
    public Profile Profile { get; set; } = new();

    /// <summary>生效的节点全集：手动节点 + 所有订阅的节点（按此顺序合并）。</summary>
    public List<NodeEntry> EffectiveNodes()
    {
        var all = new List<NodeEntry>(Profile.Nodes);
        foreach (var sub in Subscriptions)
            all.AddRange(sub.Nodes);
        return all;
    }

    /// <summary>生效的策略组全集：手动组 + 所有订阅的组（按此顺序合并，同名去重保留第一个）。</summary>
    public List<ProxyGroup> EffectiveGroups()
    {
        var all = new List<ProxyGroup>(Profile.Groups);
        foreach (var sub in Subscriptions)
            all.AddRange(sub.Groups);
        return all
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(g => g.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }
}

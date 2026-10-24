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
}

public sealed class Profile
{
    public List<NodeEntry> Nodes { get; set; } = [];
    public List<ProxyGroup> Groups { get; set; } = [];
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
    public List<string> Subscriptions { get; set; } = [];
    public Profile Profile { get; set; } = new();
}

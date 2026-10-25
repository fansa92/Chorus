using Chorus.Core.Models;
using YamlDotNet.RepresentationModel;

namespace Chorus.Core.Parsing;

/// <summary>
/// Clash / Clash Meta 订阅解析：proxies 段转节点，proxy-groups 段转策略组。
/// 只收录 ConfigGenerator 能渲染的协议，其余节点跳过并记录原因。
/// </summary>
public static class ClashYamlParser
{
    private static readonly string[] SupportedTypes =
    [
        "vmess", "vless", "trojan", "ss", "shadowsocks", "hysteria2", "hy2", "anytls",
    ];

    /// <summary>内容是否为 Clash 风格的 YAML 配置（含 proxies / proxy-groups 顶层键）。</summary>
    public static bool LooksLikeClash(string content)
    {
        var index = 0;
        foreach (var raw in content.Split('\n'))
        {
            if (++index > 400) break; // 顶层键一定在前面，避免全量扫描大文件
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("proxies:", StringComparison.Ordinal)
                || line.StartsWith("proxy-groups:", StringComparison.Ordinal)
                || line.StartsWith("proxy-providers:", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static (List<NodeEntry> Nodes, List<ProxyGroup> Groups, List<string> Errors) Parse(string content)
    {
        var nodes = new List<NodeEntry>();
        var groups = new List<ProxyGroup>();
        var errors = new List<string>();

        YamlMappingNode? root;
        try
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(content);
            yaml.Load(reader);
            root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (Exception ex)
        {
            errors.Add("解析 Clash YAML 失败：" + ex.Message);
            return (nodes, groups, errors);
        }

        if (root is null)
        {
            errors.Add("Clash 配置格式不正确（根节点不是映射）");
            return (nodes, groups, errors);
        }

        if (Get(root, "proxies") is YamlSequenceNode proxies)
        {
            foreach (var item in proxies.Children)
            {
                if (item is not YamlMappingNode proxy) continue;
                var node = ToNode(proxy, out var error);
                if (node is not null) nodes.Add(node);
                else if (error is not null) errors.Add(error);
            }
        }

        if (Get(root, "proxy-groups") is YamlSequenceNode groupNodes)
        {
            foreach (var item in groupNodes.Children)
            {
                if (item is not YamlMappingNode groupNode) continue;
                var group = ToGroup(groupNode, out var error);
                if (group is not null) groups.Add(group);
                else if (error is not null) errors.Add(error);
            }
        }

        if (nodes.Count == 0)
            errors.Add("Clash 配置里没有可用的节点");
        return (nodes, groups, errors);
    }

    // ---- 节点 ----

    private static NodeEntry? ToNode(YamlMappingNode p, out string? error)
    {
        error = null;
        var name = Str(p, "name")?.Trim();
        var type = Str(p, "type")?.Trim().ToLowerInvariant();
        var server = Str(p, "server")?.Trim();
        var portText = Str(p, "port")?.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)
            || string.IsNullOrWhiteSpace(server)
            || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            error = $"Clash 节点信息不完整：{name ?? "未命名"}";
            return null;
        }

        if (!SupportedTypes.Contains(type))
        {
            error = $"不支持的节点类型：{type}（{name}，已跳过）";
            return null;
        }

        var node = new NodeEntry
        {
            Protocol = type == "shadowsocks" ? "ss" : type,
            Tag = name,
            Server = server,
            ServerPort = port,
            Uuid = Str(p, "uuid"),
            Password = Str(p, "password"),
            Flow = Str(p, "flow"),
        };

        // cipher / alterId 两个字段在不同协议的语义不同
        switch (node.Protocol)
        {
            case "ss":
                node.Method = Str(p, "cipher");
                break;
            case "vmess":
                node.Security = Str(p, "cipher");
                break;
        }

        if (int.TryParse(Str(p, "alterId") ?? Str(p, "alter-id"), out var alterId))
            node.AlterId = alterId;

        // TLS：Clash 用 tls / servername(sni) / skip-cert-verify
        node.Tls = GetBool(p, "tls")
                   || node.Protocol is "trojan" or "hysteria2";
        node.Sni = Str(p, "servername") ?? Str(p, "sni");
        node.Insecure = GetBool(p, "skip-cert-verify");
        node.Fingerprint = Str(p, "client-fingerprint");
        if (Get(p, "alpn") is YamlSequenceNode alpn)
            node.Alpn = string.Join(",", alpn.Children.OfType<YamlScalarNode>()
                .Select(s => s.Value).Where(s => !string.IsNullOrWhiteSpace(s)));

        if (Get(p, "reality-opts") is YamlMappingNode reality)
        {
            node.RealityPublicKey = Str(reality, "public-key");
            node.RealityShortId = Str(reality, "short-id");
        }

        if (node.Protocol is "hysteria2" or "hy2")
        {
            var obfs = Str(p, "obfs");
            if (!string.IsNullOrWhiteSpace(obfs) && !obfs.Equals("none", StringComparison.OrdinalIgnoreCase))
                node.ObfsPassword = Str(p, "obfs-password");
        }

        ApplyTransport(node, p);
        return node;
    }

    private static void ApplyTransport(NodeEntry node, YamlMappingNode p)
    {
        var network = Str(p, "network")?.Trim().ToLowerInvariant();
        switch (network)
        {
            case "ws":
            {
                node.Transport = "ws";
                if (Get(p, "ws-opts") is YamlMappingNode ws)
                {
                    node.Path = Str(ws, "path");
                    if (Get(ws, "headers") is YamlMappingNode headers)
                        node.Host = Str(headers, "Host") ?? Str(headers, "host");
                }
                break;
            }
            case "grpc":
            {
                node.Transport = "grpc";
                if (Get(p, "grpc-opts") is YamlMappingNode grpc)
                    node.ServiceName = Str(grpc, "grpc-service-name");
                break;
            }
            case "h2" or "http":
            {
                node.Transport = "h2";
                if (Get(p, "h2-opts") is YamlMappingNode h2)
                {
                    node.Path = Str(h2, "path");
                    if (Get(h2, "host") is YamlSequenceNode hosts)
                        node.Host = string.Join(",", hosts.Children.OfType<YamlScalarNode>()
                            .Select(s => s.Value).Where(s => !string.IsNullOrWhiteSpace(s)));
                }
                break;
            }
        }
    }

    // ---- 策略组 ----

    private static ProxyGroup? ToGroup(YamlMappingNode g, out string? error)
    {
        error = null;
        var name = Str(g, "name")?.Trim();
        var type = Str(g, "type")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Clash 策略组缺少 name";
            return null;
        }

        // 本项目只有 selector / urltest 两种渲染方式：
        // fallback、load-balance 在 sing-box 侧都按 urltest（自动挑选可用节点）处理
        var mapped = type switch
        {
            "select" => "selector",
            "url-test" or "urltest" => "urltest",
            "fallback" or "load-balance" => "urltest",
            _ => null,
        };
        if (mapped is null)
        {
            error = $"不支持的策略组类型：{type ?? "?"}（{name}，已跳过）";
            return null;
        }

        var members = new List<string>();
        if (Get(g, "proxies") is YamlSequenceNode list)
        {
            foreach (var child in list.Children)
            {
                if (child is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value)) continue;
                members.Add(NormalizeMember(scalar.Value.Trim()));
            }
        }

        return new ProxyGroup
        {
            Name = name,
            Type = mapped,
            Members = members,
            Url = Str(g, "url"),
            Interval = ToGoDuration(Str(g, "interval")),
        };
    }

    /// <summary>Clash 内置出站名映射到 sing-box 的内置出站。</summary>
    private static string NormalizeMember(string name) => name.ToUpperInvariant() switch
    {
        "DIRECT" => "direct",
        "REJECT" or "REJECT-DROP" => "block",
        _ => name,
    };

    /// <summary>Clash 的 interval 是秒数（如 300），sing-box 要 Go 时长（300s / 5m）。</summary>
    private static string? ToGoDuration(string? interval)
    {
        if (string.IsNullOrWhiteSpace(interval)) return null;
        var text = interval.Trim();
        return int.TryParse(text, out var seconds) && seconds > 0 ? $"{seconds}s" : text;
    }

    // ---- YAML 取值 ----

    private static YamlNode? Get(YamlMappingNode map, string key)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }

    private static string? Str(YamlMappingNode map, string key) =>
        Get(map, key) is YamlScalarNode scalar ? scalar.Value : null;

    private static bool GetBool(YamlMappingNode map, string key)
    {
        var text = Str(map, key);
        return text is not null
               && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1");
    }
}

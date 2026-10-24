using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chorus.Core.Models;

namespace Chorus.Core.Configuration;

/// <summary>把 AppSettings（节点 + 策略组 + 全局偏好）渲染为 sing-box config.json。</summary>
public static partial class ConfigGenerator
{
    public const string AutoGroupTag = "♻️ 自动选择";
    public const string SelectGroupTag = "🚀 节点选择";

    public static JsonObject Build(AppSettings settings)
    {
        var nodes = DedupeTags(settings.EffectiveNodes());
        if (nodes.Count == 0)
            throw new InvalidOperationException("没有可用节点，请先在设置页导入订阅或手动添加节点。");

        var userGroups = settings.Profile.Groups ?? [];
        foreach (var group in userGroups)
            if (group.Name is AutoGroupTag or SelectGroupTag)
                throw new InvalidOperationException($"策略组名称“{group.Name}”是保留名称，请修改。");

        var groupNames = userGroups.Select(g => g.Name).ToList();
        var nodeTags = nodes.Select(n => n.Tag).ToList();
        // 组内可引用的名字：内置自动组 + 用户组（内置选择组会造成循环引用，排除）
        var resolvableGroupNames = new List<string> { AutoGroupTag };
        resolvableGroupNames.AddRange(groupNames);

        var outbounds = new JsonArray();

        foreach (var group in userGroups)
        {
            var members = ResolveGroupMembers(group, resolvableGroupNames, nodeTags);
            if (members.Count == 0)
                throw new InvalidOperationException($"策略组“{group.Name}”没有匹配到任何成员，请检查其 members 配置。");
            outbounds.Add(group.Type == "urltest"
                ? BuildUrlTest(group.Name, members)
                : BuildSelector(group.Name, members));
        }

        outbounds.Add(BuildUrlTest(AutoGroupTag, nodeTags));

        var selectorMembers = new List<string> { AutoGroupTag };
        selectorMembers.AddRange(groupNames);
        selectorMembers.AddRange(nodeTags);
        outbounds.Add(BuildSelector(SelectGroupTag, selectorMembers));

        foreach (var node in nodes)
            outbounds.Add(BuildOutbound(node));

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = settings.AllowLan ? "0.0.0.0" : "127.0.0.1",
                ["listen_port"] = settings.MixedPort,
            },
        };
        if (settings.EnableTun)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["auto_route"] = true,
                ["strict_route"] = true,
            });
        }

        return new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = string.IsNullOrWhiteSpace(settings.LogLevel) ? "info" : settings.LogLevel,
                ["timestamp"] = true,
            },
            ["dns"] = new JsonObject
            {
                // sing-box 1.12+ 新 DNS 服务器格式（type + server）；1.14 已移除旧 address 写法
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "https", ["tag"] = "dns-proxy", ["server"] = "8.8.8.8", ["detour"] = SelectGroupTag },
                    new JsonObject { ["type"] = "local", ["tag"] = "dns-direct", ["detour"] = "direct" },
                },
                ["final"] = "dns-proxy",
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                // sing-box 1.12+ 要求：出站 dial 字段必须指定域名解析器（1.14 起缺失即 FATAL）
                ["default_domain_resolver"] = "dns-direct",
                ["final"] = SelectGroupTag,
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{settings.ClashApiPort}",
                    ["secret"] = settings.ClashApiSecret,
                    ["default_mode"] = "rule",
                },
                ["cache_file"] = new JsonObject { ["enabled"] = true },
            },
        };
    }

    /// <summary>展开策略组成员：支持精确节点名、精确组名、"*"（全部节点）与通配符（匹配节点名）。</summary>
    public static List<string> ResolveGroupMembers(ProxyGroup group, IReadOnlyList<string> groupNames, IReadOnlyList<string> nodeTags)
    {
        var resolved = new List<string>();
        foreach (var token in group.Members)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            var t = token.Trim();
            if (t == "*")
            {
                resolved.AddRange(nodeTags);
                continue;
            }
            if (t != group.Name && groupNames.Contains(t, StringComparer.Ordinal))
            {
                resolved.Add(t); // 嵌套引用其他策略组
                continue;
            }
            if (nodeTags.Contains(t, StringComparer.Ordinal))
            {
                resolved.Add(t);
                continue;
            }
            if (t.Contains('*') || t.Contains('?'))
            {
                var pattern = "^" + Regex.Escape(t).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                resolved.AddRange(nodeTags.Where(n => Regex.IsMatch(n, pattern)));
            }
            // 未匹配的 token 忽略
        }
        return resolved.Distinct().ToList();
    }

    internal static List<NodeEntry> DedupeTags(IEnumerable<NodeEntry> nodes)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<NodeEntry>();
        foreach (var node in nodes)
        {
            var tag = string.IsNullOrWhiteSpace(node.Tag) ? $"{node.Server}:{node.ServerPort}" : node.Tag.Trim();
            var baseTag = tag;
            var i = 2;
            while (!used.Add(tag)) tag = $"{baseTag} #{i++}";
            node.Tag = tag;
            result.Add(node);
        }
        return result;
    }

    internal static JsonObject BuildOutbound(NodeEntry n)
    {
        var o = new JsonObject
        {
            ["tag"] = n.Tag,
            ["server"] = n.Server,
            ["server_port"] = n.ServerPort,
        };
        switch (n.Protocol?.ToLowerInvariant())
        {
            case "vmess":
                o["type"] = "vmess";
                o["uuid"] = n.Uuid;
                o["security"] = string.IsNullOrWhiteSpace(n.Security) ? "auto" : n.Security;
                o["alter_id"] = n.AlterId;
                AddTransport(o, n);
                AddTls(o, n, tlsForced: false);
                break;

            case "vless":
                o["type"] = "vless";
                o["uuid"] = n.Uuid;
                if (!string.IsNullOrWhiteSpace(n.Flow)) o["flow"] = n.Flow;
                AddTransport(o, n);
                AddTls(o, n, tlsForced: false);
                break;

            case "trojan":
                o["type"] = "trojan";
                o["password"] = n.Password;
                AddTls(o, n, tlsForced: true);
                break;

            case "ss" or "shadowsocks":
                o["type"] = "shadowsocks";
                o["method"] = n.Method;
                o["password"] = n.Password;
                break;

            case "hysteria2":
            case "hy2":
                o["type"] = "hysteria2";
                o["password"] = n.Password;
                AddTls(o, n, tlsForced: true);
                if (!string.IsNullOrWhiteSpace(n.ObfsPassword))
                    o["obfs"] = new JsonObject { ["enabled"] = true, ["type"] = "salamander", ["password"] = n.ObfsPassword };
                break;

            case "anytls":
                o["type"] = "anytls";
                o["password"] = n.Password;
                AddTls(o, n, tlsForced: true);
                break;

            default:
                throw new NotSupportedException($"不支持的节点协议：{n.Protocol}");
        }
        return o;
    }

    private static void AddTls(JsonObject o, NodeEntry n, bool tlsForced)
    {
        if (!tlsForced && !n.Tls) return;
        var tls = new JsonObject { ["enabled"] = true };
        if (!string.IsNullOrWhiteSpace(n.Sni)) tls["server_name"] = n.Sni;
        if (n.Insecure) tls["insecure"] = true;
        if (!string.IsNullOrWhiteSpace(n.Alpn))
        {
            var alpn = n.Alpn.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
                .Select(s => JsonValue.Create(s)).ToArray();
            if (alpn.Length > 0) tls["alpn"] = new JsonArray(alpn);
        }
        if (!string.IsNullOrWhiteSpace(n.Fingerprint))
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = n.Fingerprint };
        if (!string.IsNullOrWhiteSpace(n.RealityPublicKey))
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = n.RealityPublicKey,
                ["short_id"] = n.RealityShortId ?? "",
            };
        o["tls"] = tls;
    }

    private static void AddTransport(JsonObject o, NodeEntry n)
    {
        JsonObject? transport = n.Transport?.ToLowerInvariant() switch
        {
            "ws" => BuildWs(n),
            "grpc" => new JsonObject { ["type"] = "grpc", ["service_name"] = n.ServiceName ?? n.Path ?? "" },
            "http" or "h2" => BuildHttp(n),
            _ => null,
        };
        if (transport is not null) o["transport"] = transport;
    }

    private static JsonObject BuildWs(NodeEntry n)
    {
        var ws = new JsonObject
        {
            ["type"] = "ws",
            ["path"] = string.IsNullOrWhiteSpace(n.Path) ? "/" : n.Path,
        };
        if (!string.IsNullOrWhiteSpace(n.Host))
            ws["headers"] = new JsonObject { ["Host"] = n.Host };
        return ws;
    }

    private static JsonObject BuildHttp(NodeEntry n)
    {
        var http = new JsonObject
        {
            ["type"] = "http",
            ["path"] = string.IsNullOrWhiteSpace(n.Path) ? "/" : n.Path,
        };
        if (!string.IsNullOrWhiteSpace(n.Host))
            http["host"] = new JsonArray(n.Host.Split(',').Select(s => JsonValue.Create(s.Trim())).ToArray());
        return http;
    }

    private static JsonObject BuildSelector(string tag, IReadOnlyList<string> members) => new()
    {
        ["type"] = "selector",
        ["tag"] = tag,
        ["outbounds"] = ToJsonArray(members),
        ["interrupt_exist_connections"] = false,
    };

    private static JsonObject BuildUrlTest(string tag, IReadOnlyList<string> members) => new()
    {
        ["type"] = "urltest",
        ["tag"] = tag,
        ["outbounds"] = ToJsonArray(members),
        ["url"] = "https://www.gstatic.com/generate_204",
        ["interval"] = "3m",
        ["tolerance"] = 50,
    };

    internal static JsonArray ToJsonArray(IReadOnlyList<string> items) =>
        new(items.Select(s => JsonValue.Create(s)).ToArray());
}

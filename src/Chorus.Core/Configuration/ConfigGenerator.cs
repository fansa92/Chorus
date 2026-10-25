using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chorus.Core.Models;

namespace Chorus.Core.Configuration;

/// <summary>把 AppSettings（节点 + 策略组 + 全局偏好）渲染为 sing-box config.json。</summary>
public static partial class ConfigGenerator
{
    public const string AutoGroupTag = "♻️ 自动选择";
    public const string SelectGroupTag = "🚀 节点选择";

    /// <summary>
    /// 自动测速组的「内层 urltest」tag 前缀。
    /// sing-box 的 Clash API 只给 selector 组开 PUT 接口，urltest 组点节点会返回
    /// 400 "Must be a Selector"。所以自动组生成两层：内层 urltest 负责测速，
    /// 外层 selector 负责让用户手动钉住节点。内层 tag 必须和外层不同，否则自引用成环。
    /// </summary>
    public const string AutoInnerPrefix = "◈ 自动·";

    public static JsonObject Build(AppSettings settings) => BuildWithReport(settings).Config;

    /// <summary>生成配置，并返回订阅路由合并结果（便于在日志里说明规则有没有真正生效）。</summary>
    public static (JsonObject Config, RoutingMergeReport Routing) BuildWithReport(AppSettings settings)
    {
        var nodes = DedupeTags(settings.EffectiveNodes());
        if (nodes.Count == 0)
            throw new InvalidOperationException("没有可用节点，请先在设置页导入订阅或手动添加节点。");

        // 策略组分两个来源：
        // - 手动组（settings.json 手工编辑）：严格校验，空组/保留名直接报错；
        // - 订阅组（外部配置导入）：容错处理——空组跳过、保留名冲突时采用订阅组。
        var manualGroups = DedupeByName(settings.Profile.Groups ?? []);
        foreach (var group in manualGroups)
            if (group.Name is AutoGroupTag or SelectGroupTag)
                throw new InvalidOperationException($"策略组名称“{group.Name}”是保留名称，请修改。");

        var manualNames = manualGroups.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        // 只取生效的订阅（订阅页里选中的那一份），与 Bettbox 一样同一时刻只用一份配置
        var subGroups = DedupeByName(
            settings.EffectiveSubscriptions().SelectMany(s => s.Groups ?? []));
        // 与手动组同名的订阅组被手动组优先（EffectiveGroups 已去重，这里再按来源过滤一次）
        var allGroups = new List<ProxyGroup>(manualGroups);
        allGroups.AddRange(subGroups.Where(g => !manualNames.Contains(g.Name)));

        // 「自定义」面板里被关掉的组：直接不生成这个出站。
        // 路由里引用它的规则随后会被 RoutingMerger 按 outboundTags 校验剔除。
        var disabled = new HashSet<string>(settings.DisabledGroups ?? [], StringComparer.Ordinal);
        if (disabled.Count > 0)
            allGroups = allGroups.Where(g => !disabled.Contains(g.Name)).ToList();

        var hasAutoGroup = allGroups.Any(g => g.Name == AutoGroupTag);
        var hasSelectGroup = allGroups.Any(g => g.Name == SelectGroupTag);

        var groupNames = allGroups.Select(g => g.Name).ToList();
        var nodeTags = nodes.Select(n => n.Tag).ToList();
        // 组内可引用的名字：内置自动组 + 所有组名（内置选择组会造成循环引用，排除）
        var resolvableGroupNames = new List<string> { AutoGroupTag };
        resolvableGroupNames.AddRange(groupNames);

        var outbounds = new JsonArray();

        // 自动测速组（urltest）不能被 Clash API 直接选中——内核只给 selector 组开 PUT 接口。
        // 所以每个自动组额外包一层同名 selector：里层保留 urltest 做真正的测速，
        // 外层 selector 让用户能手动钉住某个节点；再点一次同一个节点就交还内核自动选。
        // 内层组用独立 tag（AutoInnerPrefix），避免与外层同名造成循环引用。
        // 路由规则引用的是外层组名（与原组名一致），所以分流不受影响。
        foreach (var group in allGroups)
        {
            var members = ResolveGroupMembers(group, resolvableGroupNames, nodeTags);
            if (members.Count == 0)
            {
                // 订阅组的成员可能全部失效（引用了不支持的协议或已下线的出口），
                // 跳过该组而不是让整个配置起不来
                if (manualNames.Contains(group.Name))
                    throw new InvalidOperationException($"策略组“{group.Name}”没有匹配到任何成员，请检查其 members 配置。");
                continue;
            }

            if (group.Type == "urltest")
            {
                var innerTag = AutoInnerPrefix + group.Name;
                outbounds.Add(BuildUrlTest(innerTag, members, group));
                // 外层 selector 的成员：内层自动组 + 原组成员（节点与嵌套组都保留）。
                // 排除自己，避免自引用成环。
                var wrapperMembers = new List<string> { innerTag };
                wrapperMembers.AddRange(members.Where(m => !string.Equals(m, group.Name, StringComparison.Ordinal)));
                outbounds.Add(BuildAutoWrapper(group.Name, wrapperMembers, settings.AutoGroupPicks, nodeTags));
            }
            else
            {
                outbounds.Add(BuildSelector(group.Name, members, group));
            }
        }

        // 订阅/手动配置没有定义内置同名组时，才生成内置的自动测速组和总选择组
        if (!hasAutoGroup)
        {
            var innerTag = AutoInnerPrefix + AutoGroupTag;
            outbounds.Add(BuildUrlTest(innerTag, nodeTags, null));
            var wrapperMembers = new List<string> { innerTag };
            wrapperMembers.AddRange(nodeTags);
            outbounds.Add(BuildAutoWrapper(AutoGroupTag, wrapperMembers, settings.AutoGroupPicks, nodeTags));
        }

        if (!hasSelectGroup)
        {
            var selectorMembers = new List<string> { AutoGroupTag };
            selectorMembers.AddRange(groupNames);
            selectorMembers.AddRange(nodeTags);
            outbounds.Add(BuildSelector(SelectGroupTag, selectorMembers, null));
        }

        foreach (var node in nodes)
            outbounds.Add(BuildOutbound(node));

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = "block" });

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

        var config = new JsonObject
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
                    // 本地 DNS 不能带 detour："detour to an empty direct outbound makes no sense"
                    // 会让 sing-box 在解析节点域名时直接失败
                    new JsonObject { ["type"] = "local", ["tag"] = "dns-direct" },
                },
                // IPv6 开关：关闭时只解析 A 记录（ipv4_only），避免拿到 AAAA 后连不通。
                // 开启时 prefer_ipv6：有 IPv6 走 IPv6，没有就回落 IPv4。
                ["strategy"] = settings.EnableIPv6 ? "prefer_ipv6" : "ipv4_only",
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
                // 模式切换规则：clash_mode 与当前 Clash 模式不匹配时整条跳过。
                // 这两条规则同时会让 Clash API 的 mode-list 出现 Global / Direct，仪表盘的
                // 出站模式切换（PATCH /configs {"mode": ...}）才真正生效。
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["clash_mode"] = "Direct", ["action"] = "route", ["outbound"] = "direct" },
                    new JsonObject { ["clash_mode"] = "Global", ["action"] = "route", ["outbound"] = SelectGroupTag },
                },
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

        // 订阅自带的分流规则（规则集 / dns）在这里并入；引用不到的条目会被剔除
        var report = RoutingMerger.Apply(config, settings);
        return (config, report);
    }

    /// <summary>展开策略组成员：支持精确节点名、精确组名、"*"（全部节点）、通配符（匹配节点名）与内置出站（direct/block）。</summary>
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
            // Clash 配置里的内置成员是大写（DIRECT / REJECT / REJECT-DROP），
            // 统一映射到 sing-box 的 direct / block 出站
            var builtin = t.ToLowerInvariant() switch
            {
                "direct" => "direct",
                "reject" or "reject-drop" or "block" => "block",
                _ => null,
            };
            if (builtin is not null)
            {
                resolved.Add(builtin);
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

    /// <summary>按名称去重（保留第一个），用于合并多个来源的策略组。</summary>
    internal static List<ProxyGroup> DedupeByName(IEnumerable<ProxyGroup> groups)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ProxyGroup>();
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name) || !used.Add(group.Name)) continue;
            result.Add(group);
        }
        return result;
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
        if (!string.IsNullOrWhiteSpace(n.RealityPublicKey))
        {
            // sing-box 1.11+ 要求 reality 客户端必须启用 uTLS，缺失即 FATAL；
            // 源配置未指定指纹时默认用 chrome
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = string.IsNullOrWhiteSpace(n.Fingerprint) ? "chrome" : n.Fingerprint,
            };
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = n.RealityPublicKey,
                ["short_id"] = n.RealityShortId ?? "",
            };
        }
        else if (!string.IsNullOrWhiteSpace(n.Fingerprint))
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = n.Fingerprint };
        }
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

    private static JsonObject BuildSelector(string tag, IReadOnlyList<string> members, ProxyGroup? group)
    {
        var selector = new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = tag,
            ["outbounds"] = ToJsonArray(members),
            ["interrupt_exist_connections"] = false,
        };
        // 订阅配置里的默认选中项（仅当引用的是有效成员时才采用）
        if (group?.Default is { Length: > 0 } def && members.Contains(def, StringComparer.Ordinal))
            selector["default"] = def;
        return selector;
    }

    private static JsonObject BuildUrlTest(string tag, IReadOnlyList<string> members, ProxyGroup? group)
    {
        // 订阅配置里的测速地址/间隔需通过校验，否则回退默认值（避免非法值让整个配置起不来）
        var url = group?.Url is { Length: > 0 } u
            && (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? u : "https://www.gstatic.com/generate_204";
        var interval = group?.Interval is { Length: > 0 } iv && GoDurationRegex().IsMatch(iv)
            ? iv : "3m";
        return new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = tag,
            ["outbounds"] = ToJsonArray(members),
            ["url"] = url,
            ["interval"] = interval,
            ["tolerance"] = 50,
        };
    }

    /// <summary>
    /// 自动组的「外层包装 selector」：默认选中内层 urltest（= 内核自动挑最快的），
    /// 用户手动钉住某个节点后，下次生成配置时把 default 改成那个节点。
    /// </summary>
    private static JsonObject BuildAutoWrapper(
        string tag,
        IReadOnlyList<string> members,
        IReadOnlyDictionary<string, string>? picks,
        IReadOnlyList<string> nodeTags)
    {
        var selector = new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = tag,
            ["outbounds"] = ToJsonArray(members),
            ["interrupt_exist_connections"] = false,
        };

        // 用户钉过节点就用它当默认（必须是有效成员，且不能是内层自动组本身）
        var picked = "";
        if (picks is not null && picks.TryGetValue(tag, out var value))
            picked = value;

        if (picked.Length > 0
            && members.Contains(picked, StringComparer.Ordinal)
            && nodeTags.Contains(picked, StringComparer.Ordinal))
        {
            selector["default"] = picked;
            return selector;
        }

        // 没钉过（或钉的节点已失效）：显式指向内层自动组 = 交给内核自动选。
        // 显式写出来而不是依赖「第一个成员」，行为更确定，也方便 UI 读出当前状态。
        var innerTag = members.Count > 0 ? members[0] : "";
        if (innerTag.StartsWith(AutoInnerPrefix, StringComparison.Ordinal))
            selector["default"] = innerTag;

        return selector;
    }

    /// <summary>Go 时长格式（如 3m / 1h30m / 500ms），用于校验订阅里的 interval。</summary>
    [GeneratedRegex(@"^(\d+(\.\d+)?(ns|us|µs|ms|s|m|h))+$")]
    private static partial Regex GoDurationRegex();

    internal static JsonArray ToJsonArray(IReadOnlyList<string> items) =>
        new(items.Select(s => JsonValue.Create(s)).ToArray());
}

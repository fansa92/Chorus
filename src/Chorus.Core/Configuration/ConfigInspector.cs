using System.Text.Json.Nodes;

namespace Chorus.Core.Configuration;

/// <summary>一条路由规则/规则集的展示模型（只承载界面需要的字段，原文另存）。</summary>
public sealed record RouteRuleInfo(
    int Index,
    IReadOnlyList<string> Conditions,
    string ActionText,
    string ActionKind,
    string? Outbound,
    IReadOnlyList<string> RuleSetRefs,
    string RawJson);

/// <summary>一个规则集（remote 的 .srs / 本地文件 / 内联）。</summary>
public sealed record RuleSetInfo(
    string Tag,
    string Type,
    string? Format,
    string? Url,
    string? DownloadDetour)
{
    public bool IsRemote => Type.Equals("remote", StringComparison.OrdinalIgnoreCase);

    public string TypeText => IsRemote ? "远程" : "本地";

    /// <summary>完整下载地址（卡片 tooltip 用）。</summary>
    public string UrlText => string.IsNullOrEmpty(Url) ? "（本地文件）" : Url;

    /// <summary>规则集文件名（只留末两段，避免卡片里被截成一串无意义的目录名）。</summary>
    public string PathText
    {
        get
        {
            if (string.IsNullOrEmpty(Url)) return "—";
            var path = Url;
            var marker = path.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) path = path[(marker + "github.com/".Length)..];
            else if (Uri.TryCreate(Url, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;

            var query = path.IndexOf('?');
            if (query >= 0) path = path[..query];

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length <= 2 ? path : "…/" + string.Join("/", segments[^2..]);
        }
    }

    /// <summary>实际下载地址的主机名（gh-proxy 之类的加速前缀也能一眼看出来）。</summary>
    public string HostText
    {
        get
        {
            if (string.IsNullOrEmpty(Url)) return "本地";
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return Url;
            // gh-proxy 形式：https://gh-proxy.com/https://github.com/... 真正的源在第二段
            var inner = Url.IndexOf("https://", 5, StringComparison.OrdinalIgnoreCase);
            if (inner > 0 && Uri.TryCreate(Url[inner..], UriKind.Absolute, out var innerUri))
                return $"{uri.Host} → {innerUri.Host}";
            return uri.Host;
        }
    }

    public string MetaText
    {
        get
        {
            var parts = new List<string> { TypeText };
            if (!string.IsNullOrEmpty(Format)) parts.Add(Format);
            if (!string.IsNullOrEmpty(DownloadDetour)) parts.Add("下载走 " + DownloadDetour);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>DNS 服务器条目。</summary>
public sealed record DnsServerInfo(string Tag, string Type, string AddressText, string? Detour)
{
    public bool IsFakeIp => Type.Equals("fakeip", StringComparison.OrdinalIgnoreCase);

    public string MetaText
    {
        get
        {
            if (IsFakeIp) return "伪造 IP";
            return string.IsNullOrEmpty(Detour) ? "直连解析" : "经 " + Detour;
        }
    }
}

/// <summary>一条 DNS 规则。</summary>
public sealed record DnsRuleInfo(
    int Index,
    IReadOnlyList<string> Conditions,
    string ActionText,
    string? Server,
    string RawJson);

/// <summary>从生成的 config.json 里抽出来的规则视图。</summary>
public sealed record RouteConfigModel(
    IReadOnlyList<RouteRuleInfo> Rules,
    string? Final,
    IReadOnlyList<RuleSetInfo> RuleSets,
    IReadOnlyList<DnsServerInfo> DnsServers,
    IReadOnlyList<DnsRuleInfo> DnsRules,
    string? DnsFinal);

/// <summary>
/// 把生成的 sing-box 配置解析成「规则页」需要的数据。
/// 解析对象是 <c>%LOCALAPPDATA%\Chorus\config.json</c>（真正交给核心的配置），
/// 而不是订阅原文——订阅 + 用户覆盖 + 内置注入合并之后才是实际生效的规则。
/// </summary>
public static class ConfigInspector
{
    /// <summary>条件字段 → 中文标签。</summary>
    private static readonly Dictionary<string, string> ConditionLabels = new(StringComparer.Ordinal)
    {
        ["domain"] = "域名",
        ["domain_suffix"] = "后缀",
        ["domain_keyword"] = "关键词",
        ["domain_regex"] = "正则",
        ["domain_keyword_regex"] = "关键词正则",
        ["ip_cidr"] = "IP",
        ["ip_is_private"] = "私有 IP",
        ["source_ip_cidr"] = "来源 IP",
        ["port"] = "端口",
        ["port_range"] = "端口范围",
        ["source_port"] = "来源端口",
        ["source_port_range"] = "来源端口范围",
        ["protocol"] = "协议",
        ["network"] = "网络",
        ["inbound"] = "入站",
        ["process_name"] = "进程",
        ["process_path"] = "进程路径",
        ["process_path_regex"] = "进程正则",
        ["clash_mode"] = "模式",
        ["rule_set"] = "规则集",
        ["rule_set_ip_cidr"] = "规则集 IP",
        ["query_type"] = "查询类型",
        ["geoip"] = "GeoIP",
        ["geosite"] = "GeoSite",
        ["package_name"] = "包名",
        ["user"] = "用户",
        ["wifi_ssid"] = "Wi-Fi",
        ["interface_address"] = "网卡地址",
    };

    /// <summary>动作字段 → 中文标签。</summary>
    private static readonly Dictionary<string, string> ActionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["route"] = "路由",
        ["reject"] = "拒绝",
        ["hijack-dns"] = "劫持 DNS",
        ["sniff"] = "嗅探",
        ["resolve"] = "解析",
        ["route-options"] = "选项",
        ["predefined"] = "预定义",
    };

    public static RouteConfigModel Inspect(JsonNode? root)
    {
        if (root is not JsonObject config)
            return new RouteConfigModel([], null, [], [], [], null);

        var route = config["route"] as JsonObject;
        var dns = config["dns"] as JsonObject;

        var ruleSets = ParseRuleSets(route?["rule_set"] as JsonArray);
        var rules = ParseRules(route?["rules"] as JsonArray);
        var dnsServers = ParseDnsServers(dns?["servers"] as JsonArray);
        var dnsRules = ParseDnsRules(dns?["rules"] as JsonArray);

        return new RouteConfigModel(
            rules,
            GetString(route, "final"),
            ruleSets,
            dnsServers,
            dnsRules,
            GetString(dns, "final"));
    }

    /// <summary>规则集列表。</summary>
    private static List<RuleSetInfo> ParseRuleSets(JsonArray? array)
    {
        var result = new List<RuleSetInfo>();
        if (array is null) return result;
        foreach (var item in array)
        {
            if (item is not JsonObject o) continue;
            var tag = GetString(o, "tag");
            if (string.IsNullOrWhiteSpace(tag)) continue;
            result.Add(new RuleSetInfo(
                tag,
                GetString(o, "type") ?? "remote",
                GetString(o, "format"),
                GetString(o, "url") ?? GetString(o, "path"),
                GetString(o, "download_detour")));
        }
        return result;
    }

    private static List<RouteRuleInfo> ParseRules(JsonArray? array)
    {
        var result = new List<RouteRuleInfo>();
        if (array is null) return result;

        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject rule) continue;
            var action = GetString(rule, "action") ?? "route";
            var outbound = GetString(rule, "outbound");
            var refs = ReadStringList(rule["rule_set"]);

            result.Add(new RouteRuleInfo(
                i + 1,
                BuildConditions(rule),
                ActionText(action, outbound),
                action,
                outbound,
                refs,
                rule.ToJsonString()));
        }
        return result;
    }

    /// <summary>把规则里的其它字段翻译成人类可读的条件芯片。</summary>
    private static List<string> BuildConditions(JsonObject rule)
    {
        var chips = new List<string>();
        foreach (var (key, value) in rule)
        {
            if (key is "action" or "outbound" or "invert" or "rcode") continue;
            if (value is null) continue;

            var label = ConditionLabels.TryGetValue(key, out var text) ? text : key;
            switch (key)
            {
                case "ip_is_private":
                    if (IsTrue(value)) chips.Add("私有 IP");
                    continue;
                case "rule_set":
                {
                    var list = ReadStringList(value);
                    if (list.Count > 0) chips.Add("规则集 " + Shorten(string.Join("、", list), 28));
                    continue;
                }
                default:
                    chips.Add($"{label} {Shorten(RenderValue(value), 26)}");
                    continue;
            }
        }
        if (IsTrue(rule["invert"])) chips.Add("取反");
        return chips;
    }

    private static string ActionText(string action, string? outbound)
    {
        if (action.Equals("route", StringComparison.OrdinalIgnoreCase) && outbound is { Length: > 0 })
            return "路由 → " + outbound;
        if (action.Equals("route", StringComparison.OrdinalIgnoreCase))
            return "路由 → 默认";
        return ActionLabels.TryGetValue(action, out var text) ? text : action;
    }

    private static List<DnsServerInfo> ParseDnsServers(JsonArray? array)
    {
        var result = new List<DnsServerInfo>();
        if (array is null) return result;
        foreach (var item in array)
        {
            if (item is not JsonObject o) continue;
            var tag = GetString(o, "tag");
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var type = GetString(o, "type") ?? "legacy";
            var address = GetString(o, "server")
                          ?? GetString(o, "address")
                          ?? GetString(o, "path")
                          ?? GetString(o, "inet4_range")
                          ?? "—";

            result.Add(new DnsServerInfo(tag, type, address, GetString(o, "detour")));
        }
        return result;
    }

    private static List<DnsRuleInfo> ParseDnsRules(JsonArray? array)
    {
        var result = new List<DnsRuleInfo>();
        if (array is null) return result;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject rule) continue;
            var server = GetString(rule, "server");
            var action = GetString(rule, "action") ?? (server is not null ? "route" : "predefined");
            var actionText = server is not null
                ? "查询 " + server
                : ActionLabels.TryGetValue(action, out var text)
                    ? text + (GetString(rule, "rcode") is { Length: > 0 } code ? " " + code : "")
                    : action;

            result.Add(new DnsRuleInfo(i + 1, BuildConditions(rule), actionText, server, rule.ToJsonString()));
        }
        return result;
    }

    private static string RenderValue(JsonNode? value) => value switch
    {
        JsonArray array => string.Join("、", array
            .Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToJsonString())
            .Where(s => !string.IsNullOrEmpty(s))),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value?.ToJsonString() ?? "",
    };

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    private static List<string> ReadStringList(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [s],
        JsonArray a => a.Select(x => x is JsonValue value && value.TryGetValue<string>(out var s) ? s : null)
                        .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList(),
        _ => [],
    };

    private static string? GetString(JsonObject? node, string property) =>
        node?[property] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}

using System.Text.Json.Nodes;
using Chorus.Core.Models;

namespace Chorus.Core.Configuration;

/// <summary>路由合并结果，用于界面/日志说明「订阅规则有没有真正生效」。</summary>
public sealed record RoutingMergeReport(
    bool Applied,
    string? SourceName,
    int Rules,
    int Dropped,
    int RuleSets,
    int DnsServers,
    int DnsRules,
    string? Note)
{
    public static RoutingMergeReport None(string? note = null) =>
        new(false, null, 0, 0, 0, 0, 0, note);

    public string Describe() => Applied
        ? $"{Rules} 条规则 / {RuleSets} 个规则集 / {DnsServers} 个 DNS 服务器"
        : Note ?? "未使用订阅规则";
}

/// <summary>
/// 把订阅自带的 route.rules / route.rule_set / dns 合并进 Chorus 生成的配置。
/// <para>
/// 为什么不原样塞进去：订阅里的出站名与 Chorus 生成的出站名不一定都能对上
/// （组可能因为成员全部失效被跳过、内置出站叫 direct 而订阅写的是 DIRECT），
/// 悬空引用会让 sing-box 在 check 阶段直接失败。这里逐条校验，把引用不到的都剔掉，
/// 并保证 dns 的 final / route 的 default_domain_resolver 仍指向存在的服务器。
/// </para>
/// </summary>
public static class RoutingMerger
{
    public static RoutingMergeReport Apply(JsonObject config, AppSettings settings)
    {
        if (!settings.ApplySubscriptionRouting)
            return RoutingMergeReport.None("设置里已关闭「使用订阅自带的路由规则」");

        // 只合并「当前使用」的那份订阅的路由，否则切换配置后规则还是别人家的
        var sub = settings.EffectiveSubscriptions().FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(s.RouteJson) || !string.IsNullOrWhiteSpace(s.DnsJson));
        if (sub is null)
            return RoutingMergeReport.None("订阅里没有可用的路由配置");

        try
        {
            var outboundTags = CollectOutboundTags(config);
            var route = config["route"] as JsonObject ?? new JsonObject();
            config["route"] = route;

            var (ruleSets, ruleSetTags) = MergeRuleSets(route, sub.RouteJson, outboundTags);
            var (rules, dropped) = MergeRules(route, sub.RouteJson, outboundTags, ruleSetTags);
            MergeFinal(route, sub.RouteJson, outboundTags);

            var (dnsServers, dnsRules) = MergeDns(config, route, sub.DnsJson, outboundTags);

            return new RoutingMergeReport(
                Applied: true,
                SourceName: sub.Name,
                Rules: rules,
                Dropped: dropped,
                RuleSets: ruleSets,
                DnsServers: dnsServers,
                DnsRules: dnsRules,
                Note: null);
        }
        catch (Exception ex)
        {
            // 订阅规则再重要也不能让配置生成失败：退回内置规则
            return RoutingMergeReport.None("订阅路由解析失败，已退回内置规则：" + ex.Message);
        }
    }

    private static HashSet<string> CollectOutboundTags(JsonObject config)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (config["outbounds"] is JsonArray outbounds)
        {
            foreach (var item in outbounds)
            {
                if (item is JsonObject o && GetString(o, "tag") is { Length: > 0 } tag)
                    tags.Add(tag);
            }
        }
        return tags;
    }

    /// <summary>规则集：只保留 tag 唯一、remote 有 url 的条目，download_detour 换成存在的出站。</summary>
    private static (int Count, HashSet<string> Tags) MergeRuleSets(
        JsonObject route, string? routeJson, HashSet<string> outboundTags)
    {
        var kept = new List<JsonObject>();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (Parse(routeJson)?["rule_set"] is JsonArray source)
        {
            foreach (var item in source)
            {
                if (item is not JsonObject o) continue;
                var tag = GetString(o, "tag");
                if (string.IsNullOrWhiteSpace(tag) || !tags.Add(tag)) continue;

                var copy = (JsonObject)o.DeepClone();
                var type = GetString(copy, "type") ?? "remote";
                if (type.Equals("remote", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(GetString(copy, "url")))
                    continue;

                if (GetString(copy, "download_detour") is { Length: > 0 } detour)
                {
                    var normalized = NormalizeOutbound(detour);
                    if (outboundTags.Contains(normalized)) copy["download_detour"] = normalized;
                    else copy.Remove("download_detour"); // 指不到就交给默认出站，不让配置失败
                }
                kept.Add(copy);
            }
        }

        if (kept.Count > 0)
            route["rule_set"] = new JsonArray([.. kept.Select(x => (JsonNode)x)]);
        return (kept.Count, tags);
    }

    /// <summary>
    /// 路由规则：内置的模式规则排在最前（保证 Direct/Global 模式切换仍然生效），
    /// 之后接订阅规则；引用不到出站或规则集的条件直接剔除。
    /// </summary>
    private static (int Count, int Dropped) MergeRules(
        JsonObject route, string? routeJson, HashSet<string> outboundTags, HashSet<string> ruleSetTags)
    {
        var result = new List<JsonNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;

        if (route["rules"] is JsonArray builtin)
        {
            foreach (var rule in builtin)
            {
                if (rule is null) continue;
                result.Add(rule.DeepClone());
                seen.Add(rule.ToJsonString());
            }
        }

        if (Parse(routeJson)?["rules"] is JsonArray source)
        {
            foreach (var item in source)
            {
                if (item is not JsonObject o) continue;
                // 模式规则由 Chorus 自己生成（上面的 builtin），订阅里的同款不再重复
                if (GetString(o, "clash_mode") is { Length: > 0 }) continue;

                var sanitized = SanitizeRule(o, outboundTags, ruleSetTags);
                if (sanitized is null)
                {
                    dropped++;
                    continue;
                }
                if (seen.Add(sanitized.ToJsonString()))
                    result.Add(sanitized);
            }
        }

        if (result.Count > 0)
            route["rules"] = new JsonArray([.. result]);
        return (result.Count, dropped);
    }

    private static JsonObject? SanitizeRule(
        JsonObject rule, HashSet<string> outboundTags, HashSet<string> ruleSetTags)
    {
        var copy = (JsonObject)rule.DeepClone();
        var action = GetString(copy, "action") ?? "route";

        if (action.Equals("route", StringComparison.OrdinalIgnoreCase))
        {
            var outbound = GetString(copy, "outbound");
            if (string.IsNullOrWhiteSpace(outbound)) return null;
            var normalized = NormalizeOutbound(outbound);
            if (!outboundTags.Contains(normalized)) return null;
            copy["outbound"] = normalized;
        }

        // rule_set 可以写成字符串或数组，统一成数组并剔除未定义/已丢弃的规则集
        var references = ReadStringList(copy["rule_set"]);
        if (references.Count > 0)
        {
            var valid = references.Where(ruleSetTags.Contains).ToList();
            if (valid.Count == 0) return null;
            copy["rule_set"] = new JsonArray([.. valid.Select(x => (JsonNode)JsonValue.Create(x)!)]);
        }

        return copy;
    }

    private static void MergeFinal(JsonObject route, string? routeJson, HashSet<string> outboundTags)
    {
        var final = GetString(Parse(routeJson) ?? new JsonObject(), "final");
        if (string.IsNullOrWhiteSpace(final)) return;
        var normalized = NormalizeOutbound(final);
        if (outboundTags.Contains(normalized)) route["final"] = normalized;
    }

    /// <summary>合并 dns：服务器按 detour/domain_resolver 引用校验，规则按 server 引用校验。</summary>
    private static (int Servers, int Rules) MergeDns(
        JsonObject config, JsonObject route, string? dnsJson, HashSet<string> outboundTags)
    {
        var dns = Parse(dnsJson);
        if (dns is null) return (0, 0);

        var serverTags = new HashSet<string>(StringComparer.Ordinal);
        var keptServers = new List<JsonObject>();
        if (dns["servers"] is JsonArray servers)
        {
            // 先收集 tag，才能校验 domain_resolver 这类「服务器引用服务器」的字段
            foreach (var item in servers)
            {
                if (item is JsonObject o && GetString(o, "tag") is { Length: > 0 } tag)
                    serverTags.Add(tag);
            }
            foreach (var item in servers)
            {
                if (item is not JsonObject o) continue;
                var tag = GetString(o, "tag");
                if (string.IsNullOrWhiteSpace(tag)) continue;

                var copy = (JsonObject)o.DeepClone();
                if (GetString(copy, "detour") is { Length: > 0 } detour)
                {
                    var normalized = NormalizeOutbound(detour);
                    if (outboundTags.Contains(normalized)) copy["detour"] = normalized;
                    else copy.Remove("detour");
                }
                if (GetString(copy, "domain_resolver") is { Length: > 0 } resolver
                    && !serverTags.Contains(resolver))
                    copy.Remove("domain_resolver");

                keptServers.Add(copy);
            }
        }
        if (keptServers.Count == 0) return (0, 0);

        var keptRules = new List<JsonObject>();
        var droppedDnsRules = 0;
        if (dns["rules"] is JsonArray rules)
        {
            foreach (var item in rules)
            {
                if (item is not JsonObject o) continue;
                var copy = (JsonObject)o.DeepClone();
                var server = GetString(copy, "server");
                if (server is not null && !serverTags.Contains(server))
                {
                    droppedDnsRules++;
                    continue;
                }
                var references = ReadStringList(copy["rule_set"]);
                if (references.Count > 0)
                {
                    var defined = route["rule_set"] is JsonArray sets
                        ? sets.OfType<JsonObject>().Select(x => GetString(x, "tag")).ToHashSet(StringComparer.Ordinal)
                        : new HashSet<string?>();
                    if (!references.Any(r => defined.Contains(r)))
                    {
                        droppedDnsRules++;
                        continue;
                    }
                }
                keptRules.Add(copy);
            }
        }

        var final = GetString(dns, "final");
        if (string.IsNullOrWhiteSpace(final) || !serverTags.Contains(final))
            final = keptServers.FirstOrDefault(s => GetString(s, "type") is not "fakeip") is { } first
                ? GetString(first, "tag")
                : GetString(keptServers[0], "tag");

        // 重建 dns 段时必须把内置生成的 dns.strategy 带回来（设置页的 IPv6 开关写在这里）：
        // 只保留 servers/rules/final 的话，一旦合并了订阅分流，IPv6 开关就静默失效。
        // 内置生成时 strategy 一定存在；订阅自带 strategy 时以内置的为准（用户显式设置优先）。
        var strategy = config["dns"] is JsonObject prevDns ? GetString(prevDns, "strategy") : null;
        if (string.IsNullOrWhiteSpace(strategy)) strategy = GetString(dns, "strategy");

        var mergedDns = new JsonObject
        {
            ["servers"] = new JsonArray([.. keptServers.Select(x => (JsonNode)x)]),
            ["rules"] = new JsonArray([.. keptRules.Select(x => (JsonNode)x)]),
            ["final"] = final,
        };
        if (!string.IsNullOrWhiteSpace(strategy)) mergedDns["strategy"] = strategy;
        config["dns"] = mergedDns;

        // 出站解析域名用的解析器也必须存在，否则 sing-box 1.14 直接 FATAL；
        // 优先挑一个不走代理的服务器（走代理的解析器会引出「解析节点域名又要用节点」的循环）
        var current = GetString(route, "default_domain_resolver");
        if (string.IsNullOrWhiteSpace(current) || !serverTags.Contains(current))
        {
            var fallback = keptServers.FirstOrDefault(s =>
                    GetString(s, "type") is "local" or "udp" or "tcp"
                    && GetString(s, "detour") is null)
                ?? keptServers.FirstOrDefault(s => GetString(s, "type") is not "fakeip")
                ?? keptServers[0];
            if (GetString(fallback, "tag") is { Length: > 0 } fallbackTag)
                route["default_domain_resolver"] = fallbackTag;
        }

        return (keptServers.Count, keptRules.Count);
    }

    /// <summary>把 Clash 风格的内置出站名映射为 Chorus 生成的标签。</summary>
    private static string NormalizeOutbound(string tag) => tag.Trim().ToLowerInvariant() switch
    {
        "direct" => "direct",
        "reject" or "reject-drop" or "block" => "block",
        _ => tag.Trim(),
    };

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch { return null; }
    }

    private static List<string> ReadStringList(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [s],
        JsonArray a => a.Select(x => x is JsonValue value && value.TryGetValue<string>(out var s) ? s : null)
                        .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList(),
        _ => [],
    };

    private static string? GetString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}

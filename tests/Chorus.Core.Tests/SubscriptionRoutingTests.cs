using System.Text.Json;
using System.Text.Json.Nodes;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// 订阅自带 route/dns 的合并逻辑。核心诉求：订阅里的规则集要真正进入生成配置，
/// 但引用了不存在的出站/规则集的条目必须被剔除，否则 sing-box check 会直接失败。
/// </summary>
public class SubscriptionRoutingTests
{
    private const string RouteJson = """
    {
      "rules": [
        { "action": "sniff" },
        { "protocol": "dns", "action": "hijack-dns" },
        { "clash_mode": "direct", "outbound": "DIRECT" },
        { "rule_set": ["youtube"], "outbound": "📹 油管视频" },
        { "rule_set": ["ghost"], "outbound": "📹 油管视频" },
        { "rule_set": ["cn"], "outbound": "不存在的组" },
        { "outbound": "DIRECT" }
      ],
      "rule_set": [
        { "tag": "youtube", "type": "remote", "format": "binary",
          "url": "https://gh-proxy.com/https://github.com/MetaCubeX/meta-rules-dat/raw/refs/heads/sing/geo/geosite/youtube.srs",
          "download_detour": "DIRECT" },
        { "tag": "cn", "type": "remote", "format": "binary",
          "url": "https://example.com/cn.srs", "download_detour": "🚀 节点选择" },
        { "tag": "broken", "type": "remote", "download_detour": "DIRECT" }
      ],
      "final": "🐟 漏网之鱼"
    }
    """;

    private const string DnsJson = """
    {
      "servers": [
        { "type": "tcp", "tag": "dns_proxy", "server": "1.1.1.1", "detour": "🚀 节点选择", "domain_resolver": "dns_resolver" },
        { "type": "https", "tag": "dns_direct", "server": "dns.alidns.com", "domain_resolver": "dns_resolver" },
        { "type": "udp", "tag": "dns_resolver", "server": "223.5.5.5" },
        { "type": "fakeip", "tag": "dns_fakeip", "inet4_range": "198.18.0.0/15" },
        { "type": "tcp", "tag": "dns_ghost", "server": "9.9.9.9", "detour": "不存在的组", "domain_resolver": "nope" }
      ],
      "rules": [
        { "clash_mode": "direct", "query_type": ["A"], "server": "dns_direct" },
        { "rule_set": ["cn"], "query_type": ["A"], "server": "dns_direct" },
        { "query_type": ["A"], "server": "dns_missing" },
        { "query_type": ["A"], "invert": true, "action": "predefined", "rcode": "REFUSED" }
      ],
      "final": "dns_direct"
    }
    """;

    private static AppSettings SettingsWithRouting(bool applyRouting = true)
    {
        var node = new NodeEntry
        {
            Protocol = "vless",
            Tag = "US-1",
            Server = "1.2.3.4",
            ServerPort = 443,
            Uuid = "uuid-1",
        };
        return new AppSettings
        {
            ClashApiSecret = "test-secret",
            ApplySubscriptionRouting = applyRouting,
            // 节点只放在订阅里：同一实例若同时出现在两处，去重改名会让组引用失效
            Profile = new Profile(),
            Subscriptions =
            [
                new SubscriptionEntry
                {
                    Name = "测试订阅",
                    Url = "https://example.com/sub",
                    Nodes = [node],
                    // 订阅组里允许出现内置组的名字（手动组不允许，会被当成保留名拦截）
                    Groups =
                    [
                        new ProxyGroup { Name = "🚀 节点选择", Type = "selector", Members = ["US-1"] },
                        new ProxyGroup { Name = "📹 油管视频", Type = "selector", Members = ["US-1", "DIRECT"] },
                        new ProxyGroup { Name = "🐟 漏网之鱼", Type = "selector", Members = ["🚀 节点选择", "DIRECT"] },
                    ],
                    RouteJson = RouteJson,
                    DnsJson = DnsJson,
                },
            ],
        };
    }

    private static JsonObject Build(bool applyRouting = true) =>
        ConfigGenerator.Build(SettingsWithRouting(applyRouting));


    [Fact]
    public void Merges_RuleSets_And_Normalizes_DownloadDetour()
    {
        var config = Build();
        var sets = config["route"]?["rule_set"] as JsonArray ?? new JsonArray();

        // broken 没有 url 被丢弃
        Assert.Equal(new[] { "youtube", "cn" }, sets.Select(x => x?["tag"]?.GetValue<string>()).ToArray());

        var youtube = sets.OfType<JsonObject>().First(x => x["tag"]!.GetValue<string>() == "youtube");
        // 订阅里写的是大写 DIRECT，Chorus 生成的出站叫 direct
        Assert.Equal("direct", youtube["download_detour"]?.GetValue<string>());

        var cn = sets.OfType<JsonObject>().First(x => x["tag"]!.GetValue<string>() == "cn");
        Assert.Equal("🚀 节点选择", cn["download_detour"]?.GetValue<string>());
    }

    [Fact]
    public void Drops_Rules_With_Dangling_References()
    {
        var config = Build();
        var rules = config["route"]?["rules"] as JsonArray ?? new JsonArray();

        var outbounds = (config["outbounds"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(o => o["tag"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        // 内置两条模式规则 + sniff + hijack-dns + youtube 规则 + DIRECT 规则 = 6
        Assert.Equal(6, rules.Count);

        // 所有 route 规则的出站都必须真实存在
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (rule["outbound"] is JsonValue value)
                Assert.Contains(value.GetValue<string>(), outbounds);
        }

        // 引用不存在的规则集 / 出站的规则都被剔除
        var raw = rules.ToJsonString();
        Assert.DoesNotContain("ghost", raw);
        Assert.DoesNotContain("不存在的组", raw);
    }

    [Fact]
    public void Merges_Dns_And_Repairs_Resolver_References()
    {
        var config = Build();
        var dns = config["dns"]!;

        var serverTags = dns["servers"]!.AsArray()
            .OfType<JsonObject>().Select(x => x["tag"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "dns_proxy", "dns_direct", "dns_resolver", "dns_fakeip", "dns_ghost" }, serverTags);

        // 指向不存在出站/服务器的字段被清掉，但服务器本身保留
        var ghost = dns["servers"]!.AsArray().OfType<JsonObject>().First(x => x["tag"]!.GetValue<string>() == "dns_ghost");
        Assert.Null(ghost["detour"]);
        Assert.Null(ghost["domain_resolver"]);

        // server 引用错误的 DNS 规则被剔除
        Assert.Equal(3, dns["rules"]!.AsArray().Count);
        Assert.Equal("dns_direct", dns["final"]?.GetValue<string>());

        // 出站解析域名用的解析器必须落在现有服务器上
        Assert.Equal("dns_resolver", config["route"]?["default_domain_resolver"]?.GetValue<string>());
    }

    [Fact]
    public void Uses_Subscription_Final_When_It_Exists()
    {
        var config = Build();
        Assert.Equal("🐟 漏网之鱼", config["route"]?["final"]?.GetValue<string>());
    }

    [Fact]
    public void Toggle_Off_Keeps_Builtin_Rules_Only()
    {
        var config = Build(applyRouting: false);

        Assert.Equal(2, config["route"]?["rules"]!.AsArray().Count);
        Assert.Null(config["route"]?["rule_set"]);
        // dns 保持 Chorus 内置的两个服务器
        Assert.Equal(2, config["dns"]?["servers"]!.AsArray().Count);
        Assert.Equal("dns-direct", config["route"]?["default_domain_resolver"]?.GetValue<string>());
    }

    [Fact]
    public void Inspector_Reads_Generated_Config()
    {
        var model = ConfigInspector.Inspect(Build());

        Assert.Equal(6, model.Rules.Count);
        Assert.Equal(2, model.RuleSets.Count);
        Assert.Equal(5, model.DnsServers.Count);
        Assert.Equal(3, model.DnsRules.Count);
        Assert.Equal("🐟 漏网之鱼", model.Final);
        Assert.Equal("dns_direct", model.DnsFinal);

        // 条件被翻译成中文标签，规则集引用单独保存
        var youtube = model.Rules.First(r => r.RuleSetRefs.Contains("youtube"));
        Assert.Contains("规则集 youtube", youtube.Conditions);
        Assert.Equal("路由 → 📹 油管视频", youtube.ActionText);

        var sniff = model.Rules.First(r => r.ActionKind == "sniff");
        Assert.Equal("嗅探", sniff.ActionText);

        // 规则集卡片只显示末两段（完整地址放在 tooltip 里）
        var set = model.RuleSets.First(s => s.Tag == "youtube");
        Assert.Equal("…/geosite/youtube.srs", set.PathText);
        Assert.Contains("gh-proxy.com", set.HostText);
        Assert.Contains("github.com", set.UrlText);
    }

    [Fact]
    public void Merged_Dns_Keeps_Builtin_Strategy()
    {
        // 设置页的 IPv6 开关写在内置 dns.strategy 里；订阅合并重建 dns 段时必须带回来，
        // 否则一旦订阅带分流规则，IPv6 开关就静默失效（实机验证时发现过这个 bug）。
        var withIpv6 = Build();
        Assert.Equal("prefer_ipv6", withIpv6["dns"]?["strategy"]?.GetValue<string>());

        var settings = SettingsWithRouting();
        settings.EnableIPv6 = false;
        var withoutIpv6 = ConfigGenerator.Build(settings);
        Assert.Equal("ipv4_only", withoutIpv6["dns"]?["strategy"]?.GetValue<string>());
    }

    [Fact]
    public void Subscription_Routing_RoundTrips_Through_Settings_Json()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new SubscriptionEntryConverter() },
        };
        var settings = SettingsWithRouting();

        var json = JsonSerializer.Serialize(settings, options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, options)!;

        var entry = Assert.Single(restored.Subscriptions);
        Assert.NotNull(entry.RouteJson);
        Assert.NotNull(entry.DnsJson);
        Assert.Contains("youtube", entry.RouteJson!);
        Assert.Contains("dns_fakeip", entry.DnsJson!);
        Assert.True(restored.ApplySubscriptionRouting);
    }
}

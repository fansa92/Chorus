using Chorus.Core.Configuration;
using Chorus.Core.Models;
using System.Text.Json.Nodes;
using Xunit;

namespace Chorus.Core.Tests;

public class ConfigGeneratorTests
{
    private static NodeEntry VmessNode(string tag = "节点1") => new()
    {
        Protocol = "vmess",
        Tag = tag,
        Server = "1.2.3.4",
        ServerPort = 443,
        Uuid = "uuid-1",
    };

    private static AppSettings Settings(params NodeEntry[] nodes) => new()
    {
        ClashApiSecret = "test-secret",
        Profile = new Profile { Nodes = [.. nodes], Groups = [] },
    };

    private static JsonObject? FindOutbound(JsonNode config, string tag) =>
        config["outbounds"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(o => o["tag"]?.GetValue<string>() == tag);

    [Fact]
    public void Build_Throws_When_NoNodes()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.Build(Settings()));
    }

    [Fact]
    public void Build_Exposes_ClashApi_And_RouteFinal()
    {
        var config = ConfigGenerator.Build(Settings(VmessNode()));

        Assert.Equal("test-secret", config["experimental"]?["clash_api"]?["secret"]?.GetValue<string>());
        Assert.Equal("127.0.0.1:9090", config["experimental"]?["clash_api"]?["external_controller"]?.GetValue<string>());
        Assert.True(config["experimental"]?["cache_file"]?["enabled"]?.GetValue<bool>());
        Assert.Equal(ConfigGenerator.SelectGroupTag, config["route"]?["final"]?.GetValue<string>());
        Assert.True(config["route"]?["auto_detect_interface"]?.GetValue<bool>());
    }

    [Fact]
    public void DefaultSelector_Contains_AutoGroup_And_AllNodes()
    {
        var config = ConfigGenerator.Build(Settings(VmessNode(), VmessNode("节点2")));

        var selector = FindOutbound(config, ConfigGenerator.SelectGroupTag);
        Assert.Equal("selector", selector?["type"]?.GetValue<string>());
        var members = selector?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains(ConfigGenerator.AutoGroupTag, members);
        Assert.Contains("节点1", members);
        Assert.Contains("节点2", members);

        var auto = FindOutbound(config, ConfigGenerator.AutoGroupTag);
        Assert.Equal("urltest", auto?["type"]?.GetValue<string>());
        var autoMembers = auto?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains("节点1", autoMembers);
    }

    [Fact]
    public void UserGroup_Supports_Wildcard_And_Nested_Group_Reference()
    {
        var settings = Settings(VmessNode("香港 01"), VmessNode("美国 01"));
        settings.Profile.Groups.Add(new ProxyGroup { Name = "🇭🇰 香港", Type = "selector", Members = ["*香港*"] });
        settings.Profile.Groups.Add(new ProxyGroup { Name = "出口", Type = "selector", Members = ["🇭🇰 香港", ConfigGenerator.AutoGroupTag] });

        var config = ConfigGenerator.Build(settings);

        var hk = FindOutbound(config, "🇭🇰 香港");
        var hkMembers = hk?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Single(hkMembers);
        Assert.Contains("香港 01", hkMembers);

        var exit = FindOutbound(config, "出口");
        var exitMembers = exit?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains("🇭🇰 香港", exitMembers);
        Assert.Contains(ConfigGenerator.AutoGroupTag, exitMembers);

        // 默认选择组也应包含用户定义的策略组（可在其中选择“节点组”）
        var selector = FindOutbound(config, ConfigGenerator.SelectGroupTag);
        var selectorMembers = selector?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains("🇭🇰 香港", selectorMembers);
        Assert.Contains("出口", selectorMembers);
    }

    [Fact]
    public void Star_Member_Expands_To_AllNodes()
    {
        var settings = Settings(VmessNode("n1"), VmessNode("n2"));
        settings.Profile.Groups.Add(new ProxyGroup { Name = "全部", Members = ["*"] });

        var config = ConfigGenerator.Build(settings);

        var group = FindOutbound(config, "全部");
        var members = group?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains("n1", members);
        Assert.Contains("n2", members);
    }

    [Fact]
    public void Empty_Group_Throws()
    {
        var settings = Settings(VmessNode());
        settings.Profile.Groups.Add(new ProxyGroup { Name = "空组", Members = ["*不存在的关键词*"] });

        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.Build(settings));
    }

    [Fact]
    public void Reserved_GroupName_Throws()
    {
        var settings = Settings(VmessNode());
        settings.Profile.Groups.Add(new ProxyGroup { Name = ConfigGenerator.SelectGroupTag, Members = ["*"] });

        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.Build(settings));
    }

    [Fact]
    public void Dns_Uses_New_Server_Format_And_DefaultDomainResolver()
    {
        var config = ConfigGenerator.Build(Settings(VmessNode()));

        var servers = config["dns"]?["servers"]?.AsArray().OfType<JsonObject>().ToList();
        Assert.NotNull(servers);
        Assert.Equal(2, servers!.Count);

        var proxy = servers.First(s => s["tag"]?.GetValue<string>() == "dns-proxy");
        Assert.Equal("https", proxy["type"]?.GetValue<string>());
        Assert.Equal("8.8.8.8", proxy["server"]?.GetValue<string>());
        Assert.Null(proxy["address"]); // 旧格式（1.14 已移除）不得出现

        var direct = servers.First(s => s["tag"]?.GetValue<string>() == "dns-direct");
        Assert.Equal("local", direct["type"]?.GetValue<string>());

        // independent_cache 已在 1.14 弃用
        Assert.Null(config["dns"]?["independent_cache"]);

        // 1.14 要求 route.default_domain_resolver，否则启动 FATAL
        Assert.Equal("dns-direct", config["route"]?["default_domain_resolver"]?.GetValue<string>());
    }

    [Fact]
    public void AnyTls_Node_Maps_Password_And_Tls()
    {
        var settings = Settings(new NodeEntry
        {
            Protocol = "anytls",
            Tag = "AT",
            Server = "a.com",
            ServerPort = 443,
            Password = "pw",
            Tls = true,
            Sni = "sni.com",
            Insecure = true,
        });

        var config = ConfigGenerator.Build(settings);

        var node = FindOutbound(config, "AT");
        Assert.Equal("anytls", node?["type"]?.GetValue<string>());
        Assert.Equal("pw", node?["password"]?.GetValue<string>());
        Assert.True(node?["tls"]?["enabled"]?.GetValue<bool>());
        Assert.Equal("sni.com", node?["tls"]?["server_name"]?.GetValue<string>());
        Assert.True(node?["tls"]?["insecure"]?.GetValue<bool>());
    }

    [Fact]
    public void Subscription_Nodes_Are_Merged_Into_Config()
    {
        var settings = Settings(VmessNode("手动节点"));
        settings.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "机场A",
            Url = "https://example.com/sub",
            Nodes = [new NodeEntry { Protocol = "ss", Tag = "订阅节点", Server = "b.com", ServerPort = 8388, Method = "aes-256-gcm", Password = "pw" }],
        });

        var config = ConfigGenerator.Build(settings);

        var selector = FindOutbound(config, ConfigGenerator.SelectGroupTag);
        var members = selector?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains("手动节点", members);
        Assert.Contains("订阅节点", members);
        Assert.NotNull(FindOutbound(config, "订阅节点"));
    }

    [Fact]
    public void VmessNode_Maps_CoreFields()
    {
        var config = ConfigGenerator.Build(Settings(VmessNode()));

        var node = FindOutbound(config, "节点1");
        Assert.Equal("vmess", node?["type"]?.GetValue<string>());
        Assert.Equal("1.2.3.4", node?["server"]?.GetValue<string>());
        Assert.Equal(443, node?["server_port"]?.GetValue<int>());
        Assert.Equal("uuid-1", node?["uuid"]?.GetValue<string>());
        Assert.Equal("auto", node?["security"]?.GetValue<string>());
    }

    [Fact]
    public void VlessRealityNode_Maps_Tls_And_Transport()
    {
        var settings = Settings(new NodeEntry
        {
            Protocol = "vless",
            Tag = "R",
            Server = "s.com",
            ServerPort = 443,
            Uuid = "u",
            Tls = true,
            Sni = "www.apple.com",
            Fingerprint = "chrome",
            RealityPublicKey = "PK",
            RealityShortId = "01",
            Transport = "ws",
            Path = "/ws",
        });

        var config = ConfigGenerator.Build(settings);

        var node = FindOutbound(config, "R");
        Assert.Equal("vless", node?["type"]?.GetValue<string>());
        Assert.True(node?["tls"]?["enabled"]?.GetValue<bool>());
        Assert.Equal("www.apple.com", node?["tls"]?["server_name"]?.GetValue<string>());
        Assert.Equal("PK", node?["tls"]?["reality"]?["public_key"]?.GetValue<string>());
        Assert.Equal("chrome", node?["tls"]?["utls"]?["fingerprint"]?.GetValue<string>());
        Assert.Equal("ws", node?["transport"]?["type"]?.GetValue<string>());
        Assert.Equal("/ws", node?["transport"]?["path"]?.GetValue<string>());
    }

    [Fact]
    public void Hysteria2_Maps_Obfs()
    {
        var settings = Settings(new NodeEntry
        {
            Protocol = "hysteria2",
            Tag = "hy",
            Server = "h.com",
            ServerPort = 36712,
            Password = "pw",
            Tls = true,
            Sni = "h.com",
            ObfsPassword = "ob",
        });

        var config = ConfigGenerator.Build(settings);

        var node = FindOutbound(config, "hy");
        Assert.Equal("hysteria2", node?["type"]?.GetValue<string>());
        Assert.True(node?["tls"]?["enabled"]?.GetValue<bool>());
        Assert.Equal("ob", node?["obfs"]?["password"]?.GetValue<string>());
    }

    [Fact]
    public void Tun_Inbound_When_Enabled()
    {
        var settings = Settings(VmessNode());
        settings.EnableTun = true;

        var config = ConfigGenerator.Build(settings);

        var tun = config["inbounds"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "tun");
        Assert.NotNull(tun);
        Assert.True(tun?["auto_route"]?.GetValue<bool>());
    }

    [Fact]
    public void Mixed_Inbound_Respects_Port_And_Lan()
    {
        var settings = Settings(VmessNode());
        settings.MixedPort = 8080;
        settings.AllowLan = true;

        var config = ConfigGenerator.Build(settings);

        var mixed = config["inbounds"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(o => o["type"]?.GetValue<string>() == "mixed");
        Assert.Equal("0.0.0.0", mixed?["listen"]?.GetValue<string>());
        Assert.Equal(8080, mixed?["listen_port"]?.GetValue<int>());
    }

    [Fact]
    public void Duplicate_Tags_Are_Renamed()
    {
        var config = ConfigGenerator.Build(Settings(VmessNode("A"), VmessNode("A")));

        var selector = FindOutbound(config, ConfigGenerator.SelectGroupTag);
        var members = selector?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains("A", members);
        Assert.Contains("A #2", members);
    }
}

using System.Text.Json.Nodes;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// 本轮新增能力的行为约束：
/// IPv6 开关、被禁用的策略组、自动组的手动固定、多源 IP 解析、本地 GeoIP 兜底。
/// </summary>
public class ConfigFeatureTests
{
    private static AppSettings Settings(params NodeEntry[] nodes) => new()
    {
        Profile = new Profile { Nodes = nodes.ToList() },
    };

    private static NodeEntry Vmess(string tag) => new()
    {
        Protocol = "vmess",
        Tag = tag,
        Server = "example.com",
        ServerPort = 443,
        Uuid = "11111111-1111-1111-1111-111111111111",
    };

    private static JsonObject? Find(JsonObject config, string tag) =>
        config["outbounds"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(o => o["tag"]?.GetValue<string>() == tag);

    // ---- IPv6 ----

    [Fact]
    public void Ipv6_Enabled_Uses_PreferIpv6_Strategy()
    {
        var settings = Settings(Vmess("节点1"));
        settings.EnableIPv6 = true;

        var config = ConfigGenerator.Build(settings);

        Assert.Equal("prefer_ipv6", config["dns"]?["strategy"]?.GetValue<string>());
    }

    [Fact]
    public void Ipv6_Disabled_Uses_Ipv4Only_Strategy()
    {
        var settings = Settings(Vmess("节点1"));
        settings.EnableIPv6 = false;

        var config = ConfigGenerator.Build(settings);

        Assert.Equal("ipv4_only", config["dns"]?["strategy"]?.GetValue<string>());
    }

    // ---- 被禁用的策略组 ----

    [Fact]
    public void Disabled_Group_Is_Not_Generated()
    {
        var settings = Settings(Vmess("节点1"));
        settings.Profile.Groups.Add(new ProxyGroup { Name = "🇭🇰 香港", Type = "selector", Members = ["*"] });
        settings.Profile.Groups.Add(new ProxyGroup { Name = "🇺🇸 美国", Type = "selector", Members = ["*"] });
        settings.DisabledGroups = ["🇺🇸 美国"];

        var config = ConfigGenerator.Build(settings);

        Assert.NotNull(Find(config, "🇭🇰 香港"));
        Assert.Null(Find(config, "🇺🇸 美国"));
    }

    [Fact]
    public void Disabled_Group_Is_Removed_From_Selector_Members()
    {
        var settings = Settings(Vmess("节点1"));
        settings.Profile.Groups.Add(new ProxyGroup { Name = "🇭🇰 香港", Type = "selector", Members = ["*"] });
        settings.Profile.Groups.Add(new ProxyGroup { Name = "🇺🇸 美国", Type = "selector", Members = ["*"] });
        settings.DisabledGroups = ["🇺🇸 美国"];

        var config = ConfigGenerator.Build(settings);

        var selector = Find(config, ConfigGenerator.SelectGroupTag);
        var members = selector?["outbounds"]?.AsArray().Select(x => (string?)x).ToList()!;
        Assert.Contains("🇭🇰 香港", members);
        Assert.DoesNotContain("🇺🇸 美国", members);
    }

    [Fact]
    public void Enabling_Group_Again_Brings_It_Back()
    {
        var settings = Settings(Vmess("节点1"));
        settings.Profile.Groups.Add(new ProxyGroup { Name = "🇭🇰 香港", Type = "selector", Members = ["*"] });
        settings.DisabledGroups = [];

        var config = ConfigGenerator.Build(settings);

        Assert.NotNull(Find(config, "🇭🇰 香港"));
    }

    // ---- 自动组的两层结构 ----

    [Fact]
    public void Auto_Group_Has_Selector_Wrapper_And_Urltest_Inner()
    {
        var settings = Settings(Vmess("节点1"));
        settings.Profile.Groups.Add(new ProxyGroup
        {
            Name = "♻️ 自动",
            Type = "urltest",
            Members = ["*"],
        });

        var config = ConfigGenerator.Build(settings);

        var outer = Find(config, "♻️ 自动");
        Assert.Equal("selector", outer?["type"]?.GetValue<string>());

        var inner = Find(config, ConfigGenerator.AutoInnerPrefix + "♻️ 自动");
        Assert.Equal("urltest", inner?["type"]?.GetValue<string>());

        // 外层默认指向内层 = 交给内核自动选
        Assert.Equal(ConfigGenerator.AutoInnerPrefix + "♻️ 自动", outer?["default"]?.GetValue<string>());
    }

    [Fact]
    public void Auto_Group_Pick_Becomes_Default()
    {
        var settings = Settings(Vmess("节点1"), Vmess("节点2"));
        settings.Profile.Groups.Add(new ProxyGroup
        {
            Name = "♻️ 自动",
            Type = "urltest",
            Members = ["*"],
        });
        settings.AutoGroupPicks = new Dictionary<string, string> { ["♻️ 自动"] = "节点2" };

        var config = ConfigGenerator.Build(settings);

        var outer = Find(config, "♻️ 自动");
        Assert.Equal("节点2", outer?["default"]?.GetValue<string>());
    }

    [Fact]
    public void Auto_Group_Pick_Of_Unknown_Node_Is_Ignored()
    {
        var settings = Settings(Vmess("节点1"));
        settings.Profile.Groups.Add(new ProxyGroup
        {
            Name = "♻️ 自动",
            Type = "urltest",
            Members = ["*"],
        });
        // 钉了一个不存在的节点（节点被删了）→ 退回自动，不能写进配置
        settings.AutoGroupPicks = new Dictionary<string, string> { ["♻️ 自动"] = "已删除的节点" };

        var config = ConfigGenerator.Build(settings);

        var outer = Find(config, "♻️ 自动");
        Assert.Equal(ConfigGenerator.AutoInnerPrefix + "♻️ 自动", outer?["default"]?.GetValue<string>());
    }

    [Fact]
    public void Builtin_Auto_Group_Also_Has_Wrapper()
    {
        var config = ConfigGenerator.Build(Settings(Vmess("节点1")));

        var outer = Find(config, ConfigGenerator.AutoGroupTag);
        Assert.Equal("selector", outer?["type"]?.GetValue<string>());
        Assert.NotNull(Find(config, ConfigGenerator.AutoInnerPrefix + ConfigGenerator.AutoGroupTag));
    }

    [Fact]
    public void Auto_Wrapper_Inner_Tag_Does_Not_Collide_With_Group_Names()
    {
        var settings = Settings(Vmess("节点1"));
        settings.Profile.Groups.Add(new ProxyGroup
        {
            Name = "♻️ 自动",
            Type = "urltest",
            Members = ["*"],
        });

        var config = ConfigGenerator.Build(settings);

        // 内层 tag 与外层不同 → 不会自引用成环
        var outer = Find(config, "♻️ 自动");
        var innerTag = ConfigGenerator.AutoInnerPrefix + "♻️ 自动";
        Assert.NotEqual(outer?["tag"]?.GetValue<string>(), innerTag);
        Assert.DoesNotContain("♻️ 自动", outer?["outbounds"]?.AsArray().Select(x => (string?)x));
    }
}

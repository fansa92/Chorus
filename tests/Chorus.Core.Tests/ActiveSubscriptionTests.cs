using System.Text.Json.Nodes;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// 「当前使用的配置」语义：同一时刻只有一份订阅生效（Bettbox 的 profile 卡片就是这样），
/// 手动节点始终附加；选中的条目被删掉 / 旧配置没有该字段时要能自愈。
/// </summary>
public class ActiveSubscriptionTests
{
    private static NodeEntry Node(string tag) => new()
    {
        Protocol = "ss",
        Tag = tag,
        Server = "127.0.0.1",
        ServerPort = 443,
        Method = "aes-128-gcm",
        Password = "pw",
    };

    private static ProxyGroup Group(string name, params string[] members) => new()
    {
        Name = name,
        Type = "selector",
        Members = [.. members],
    };

    private static SubscriptionEntry Sub(string name, string url, string nodeTag) => new()
    {
        Name = name,
        Url = url,
        Nodes = [Node(nodeTag)],
        Groups = [Group($"{name} 组", nodeTag)],
    };

    private static AppSettings TwoSubs() => new()
    {
        Profile = new Profile
        {
            Nodes = [Node("手动节点")],
            Groups = [Group("手动组", "手动节点")],
        },
        Subscriptions = [Sub("订阅A", "https://a.example/sub", "A-节点"), Sub("订阅B", "https://b.example/sub", "B-节点")],
    };

    [Fact]
    public void 未选中时退回全部订阅合并()
    {
        var settings = TwoSubs();
        Assert.Null(settings.ActiveSubscription());
        Assert.Equal(2, settings.EffectiveSubscriptions().Count);
        Assert.Equal(3, settings.EffectiveNodes().Count); // 手动 + 两份订阅各 1
        Assert.Equal(3, settings.EffectiveGroups().Count);
    }

    [Fact]
    public void 选中后只用那一份订阅加手动节点()
    {
        var settings = TwoSubs();
        settings.ActiveSubscriptionUrl = "https://b.example/sub";

        Assert.Equal("订阅B", settings.ActiveSubscription()?.Name);
        // 手动节点始终排在前面（与合并顺序一致）
        Assert.Equal(["手动节点", "B-节点"], settings.EffectiveNodes().Select(n => n.Tag));
        Assert.Equal(["手动组", "订阅B 组"], settings.EffectiveGroups().Select(g => g.Name));
    }

    [Fact]
    public void URL_不区分大小写()
    {
        var settings = TwoSubs();
        settings.ActiveSubscriptionUrl = "HTTPS://B.EXAMPLE/SUB";
        Assert.Equal("订阅B", settings.ActiveSubscription()?.Name);
    }

    [Fact]
    public void 选中的订阅被删除后退回全部()
    {
        var settings = TwoSubs();
        settings.ActiveSubscriptionUrl = "https://gone.example/sub";

        Assert.Null(settings.ActiveSubscription());
        Assert.Equal(2, settings.EffectiveSubscriptions().Count);
    }

    [Fact]
    public void EnsureActiveSubscription_首次选第一个_删空后清空()
    {
        var settings = TwoSubs();
        settings.EnsureActiveSubscription();
        Assert.Equal("https://a.example/sub", settings.ActiveSubscriptionUrl);

        settings.Subscriptions.Clear();
        settings.EnsureActiveSubscription();
        Assert.Null(settings.ActiveSubscriptionUrl);
    }

    [Fact]
    public void 生成配置只包含当前使用订阅的节点与策略组()
    {
        var settings = TwoSubs();
        settings.ActiveSubscriptionUrl = "https://b.example/sub";

        var tags = ConfigGenerator.Build(settings)["outbounds"]!.AsArray()
            .Select(o => (string?)o!["tag"])
            .ToList();

        Assert.Contains("B-节点", tags);
        Assert.Contains("订阅B 组", tags);
        Assert.Contains("手动节点", tags);
        Assert.DoesNotContain("A-节点", tags);
        Assert.DoesNotContain("订阅A 组", tags);
    }

    [Fact]
    public void 切换配置后规则也换成那一份订阅的()
    {
        var settings = TwoSubs();
        settings.Subscriptions[0].RouteJson = """
        { "rule_set": [ { "tag": "a-set", "type": "remote", "format": "binary", "url": "https://x/a.srs" } ],
          "rules": [ { "rule_set": ["a-set"], "outbound": "DIRECT" } ] }
        """;
        settings.Subscriptions[1].RouteJson = """
        { "rule_set": [ { "tag": "b-set", "type": "remote", "format": "binary", "url": "https://x/b.srs" } ],
          "rules": [ { "rule_set": ["b-set"], "outbound": "DIRECT" } ] }
        """;

        var reportB = ConfigGenerator.BuildWithReport(WithActive(settings, "https://b.example/sub"));
        var setsB = reportB.Config["route"]!["rule_set"]!.AsArray()
            .Select(s => (string?)s!["tag"]).ToList();
        Assert.Equal(["b-set"], setsB);
        Assert.Equal("订阅B", reportB.Routing.SourceName);

        var reportA = ConfigGenerator.BuildWithReport(WithActive(settings, "https://a.example/sub"));
        var setsA = reportA.Config["route"]!["rule_set"]!.AsArray()
            .Select(s => (string?)s!["tag"]).ToList();
        Assert.Equal(["a-set"], setsA);
        Assert.Equal("订阅A", reportA.Routing.SourceName);
    }

    private static AppSettings WithActive(AppSettings settings, string url)
    {
        settings.ActiveSubscriptionUrl = url;
        return settings;
    }
}

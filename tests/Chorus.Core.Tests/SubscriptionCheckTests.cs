using System.Text.Json.Nodes;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// 订阅配置检查：用 sing-box 自带的 check 校验「这份订阅生成的配置」。
/// 关键行为：检查器由外部注入（单测不依赖 sing-box.exe）、失败不能顶掉旧数据、
/// 临时文件只在检查期间存在。
/// </summary>
public class SubscriptionCheckTests
{
    private static NodeEntry Node(string tag) => new()
    {
        Protocol = "vless",
        Tag = tag,
        Server = "1.2.3.4",
        ServerPort = 443,
        Uuid = "uuid-" + tag,
    };

    private static AppSettings Settings()
    {
        var other = new SubscriptionEntry
        {
            Name = "另一份",
            Url = "https://other.example/sub",
            Nodes = [Node("OTHER")],
            Groups = [new ProxyGroup { Name = "另一组", Type = "selector", Members = ["OTHER"] }],
        };
        var active = new SubscriptionEntry
        {
            Name = "当前",
            Url = "https://active.example/sub",
            Nodes = [Node("ACTIVE")],
            Groups = [new ProxyGroup { Name = "手动组", Type = "selector", Members = ["ACTIVE"] }],
        };
        return new AppSettings
        {
            ClashApiSecret = "secret",
            ApplySubscriptionRouting = false,
            Subscriptions = [other, active],
            ActiveSubscriptionUrl = other.Url,
            Profile = new Profile { Nodes = [Node("MANUAL")] },
        };
    }

    private static SubscriptionEntry Candidate(params string[] tags) => new()
    {
        Name = "候选",
        Url = "https://candidate.example/sub",
        Nodes = tags.Select(Node).ToList(),
        Groups = [new ProxyGroup { Name = "候选组", Type = "selector", Members = [.. tags] }],
    };

    [Fact]
    public void SettingsFor_Only_Uses_Candidate_And_Does_Not_Mutate_Source()
    {
        var source = Settings();
        var candidate = Candidate("CAND");

        var isolated = SubscriptionCheck.SettingsFor(source, candidate);

        Assert.Same(candidate, Assert.Single(isolated.Subscriptions));
        Assert.Equal(candidate.Url, isolated.ActiveSubscriptionUrl);
        // 手动节点始终参与生成配置
        Assert.Equal(new[] { "MANUAL", "CAND" }, isolated.EffectiveNodes().Select(n => n.Tag));
        // 原设置一个字段都没被改
        Assert.Equal("https://other.example/sub", source.ActiveSubscriptionUrl);
        Assert.Equal(2, source.Subscriptions.Count);
    }

    [Fact]
    public void BuildJson_Contains_Candidate_Nodes_And_Groups_Only()
    {
        var (json, nodes, groups, rules, ruleSets) = SubscriptionCheck.BuildJson(Settings(), Candidate("CAND"));

        var config = JsonNode.Parse(json)!.AsObject();
        var outboundTags = config["outbounds"]!.AsArray()
            .Select(o => o!["tag"]!.GetValue<string>())
            .ToList();

        Assert.Contains("CAND", outboundTags);
        Assert.Contains("MANUAL", outboundTags);
        Assert.DoesNotContain("OTHER", outboundTags);
        Assert.Contains("候选组", outboundTags);
        Assert.DoesNotContain("另一组", outboundTags);
        Assert.Equal(1, nodes);
        Assert.Equal(1, groups);
        Assert.True(rules >= 2);        // 内置两条 clash_mode 规则
        Assert.Equal(0, ruleSets);      // 没启用订阅路由
    }

    [Fact]
    public async Task RunAsync_Passes_And_Deletes_Temp_File()
    {
        string? seenPath = null;
        var report = await SubscriptionCheck.RunAsync(Settings(), Candidate("CAND"), path =>
        {
            seenPath = path;
            Assert.True(File.Exists(path));
            var parsed = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.NotNull(parsed["outbounds"]);
            return Task.CompletedTask;
        });

        Assert.Equal(ConfigCheckStatus.Passed, report.Status);
        Assert.True(report.Ok);
        Assert.False(File.Exists(seenPath));
        Assert.Contains("1 个节点", report.Summary);
    }

    [Fact]
    public async Task RunAsync_Cleans_SingBox_Error_Message()
    {
        var report = await SubscriptionCheck.RunAsync(Settings(), Candidate("CAND"), _ =>
            throw new InvalidOperationException(
                "配置校验失败：\nFATAL[0000] decode config at C:\\Users\\x\\Temp\\chorus-check-abc.json: "
                + "outbounds[0]: unknown outbound type: bogus"));

        Assert.Equal(ConfigCheckStatus.Failed, report.Status);
        Assert.False(report.Ok);
        Assert.Equal("outbounds[0]: unknown outbound type: bogus", report.Error);
    }

    [Fact]
    public async Task RunAsync_Skips_When_No_Core_Available()
    {
        var report = await SubscriptionCheck.RunAsync(Settings(), Candidate("CAND"), checkConfig: null);

        Assert.Equal(ConfigCheckStatus.Skipped, report.Status);
        Assert.True(report.Ok); // 跳过不应阻止保存
        Assert.Null(report.Error);
    }

    [Fact]
    public async Task RunAsync_Fails_Without_Nodes()
    {
        var settings = Settings();
        settings.Profile.Nodes.Clear(); // 手动节点也算节点，这里要一份彻底没有节点的配置
        var report = await SubscriptionCheck.RunAsync(settings, Candidate(), _ => Task.CompletedTask);

        Assert.Equal(ConfigCheckStatus.Failed, report.Status);
        Assert.Contains("没有可用节点", report.Error!);
    }

    [Theory]
    [InlineData("FATAL[0000] decode config at C:\\a\\b.json: invalid character 'o'",
        "invalid character 'o'")]
    [InlineData("配置校验失败：\nFATAL[0000] decode config at /tmp/x.json: missing route.final",
        "missing route.final")]
    public void Clean_Strips_Prefix_And_Temp_Path(string raw, string expected)
        => Assert.Equal(expected, SubscriptionCheck.Clean(raw));
}

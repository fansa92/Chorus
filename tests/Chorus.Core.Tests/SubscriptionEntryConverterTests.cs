using System.Text.Json;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

public class SubscriptionEntryConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new SubscriptionEntryConverter() },
    };

    [Fact]
    public void Legacy_String_Format_Migrates_To_Entry()
    {
        // 旧版 settings.json："Subscriptions": ["https://a.com/sub"]
        const string json = """["https://a.com/sub"]""";

        var entries = JsonSerializer.Deserialize<List<SubscriptionEntry>>(json, Options);

        Assert.NotNull(entries);
        var entry = Assert.Single(entries!);
        Assert.Equal("https://a.com/sub", entry.Url);
        Assert.Equal("a.com", entry.Name); // 自动取名：主机名
        Assert.Empty(entry.Nodes);
        Assert.Null(entry.LastUpdated);
        Assert.Null(entry.LastError);
    }

    [Fact]
    public void Object_Format_RoundTrips()
    {
        var original = new SubscriptionEntry
        {
            Name = "机场A",
            Url = "https://a.com/sub",
            Nodes = [new NodeEntry { Protocol = "ss", Tag = "n1", Server = "b.com", ServerPort = 8388, Method = "aes-256-gcm", Password = "pw" }],
            LastUpdated = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8)),
            LastError = null,
        };

        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<SubscriptionEntry>(json, Options);

        Assert.NotNull(back);
        Assert.Equal("机场A", back!.Name);
        Assert.Equal("https://a.com/sub", back.Url);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8)), back.LastUpdated);
        var node = Assert.Single(back.Nodes);
        Assert.Equal("ss", node.Protocol);
        Assert.Equal("n1", node.Tag);
        Assert.Equal(8388, node.ServerPort);
    }

    [Fact]
    public void Groups_Survive_Serialize_Roundtrip()
    {
        // 回归：早期转换器只写 nodes，订阅里的 proxy-groups 存盘即丢，
        // 重启应用后策略组页只剩自动生成的默认组。
        var original = new SubscriptionEntry
        {
            Name = "机场A",
            Url = "https://a.com/sub",
            Nodes = [new NodeEntry { Protocol = "vless", Tag = "n1", Server = "b.com", ServerPort = 443 }],
            Groups =
            [
                new ProxyGroup
                {
                    Name = "🚀 节点选择",
                    Type = "selector",
                    Members = ["♻️ 自动选择", "n1"],
                    Default = "♻️ 自动选择",
                },
                new ProxyGroup
                {
                    Name = "♻️ 自动选择",
                    Type = "urltest",
                    Members = ["n1"],
                    Url = "https://www.gstatic.com/generate_204",
                    Interval = "300s",
                },
            ],
        };

        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<SubscriptionEntry>(json, Options);

        Assert.NotNull(back);
        Assert.Equal(2, back!.Groups.Count);
        Assert.Equal("🚀 节点选择", back.Groups[0].Name);
        Assert.Equal("selector", back.Groups[0].Type);
        Assert.Equal(["♻️ 自动选择", "n1"], back.Groups[0].Members);
        Assert.Equal("♻️ 自动选择", back.Groups[0].Default);
        Assert.Equal("urltest", back.Groups[1].Type);
        Assert.Equal("300s", back.Groups[1].Interval);
    }

    [Fact]
    public void Check_Result_Fields_RoundTrip()
    {
        var original = new SubscriptionEntry
        {
            Name = "机场A",
            Url = "https://a.com/sub",
            LastCheckOk = false,
            LastCheckError = "outbounds[0]: unknown outbound type: bogus",
            LastCheckSummary = "1 个节点 · 1 个策略组 · 2 条路由规则 · 0 个规则集",
            LastCheckedAt = new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.FromHours(8)),
        };

        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<SubscriptionEntry>(json, Options);

        Assert.NotNull(back);
        Assert.False(back!.LastCheckOk);
        Assert.Equal("outbounds[0]: unknown outbound type: bogus", back.LastCheckError);
        Assert.Equal("1 个节点 · 1 个策略组 · 2 条路由规则 · 0 个规则集", back.LastCheckSummary);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.FromHours(8)), back.LastCheckedAt);
    }

    [Fact]
    public void Effective_Nodes_Merge_Manual_And_Subscriptions()
    {
        var settings = new AppSettings
        {
            Profile = new Profile
            {
                Nodes = [new NodeEntry { Protocol = "ss", Tag = "m1", Server = "a.com", ServerPort = 1 }],
            },
            Subscriptions =
            [
                new SubscriptionEntry
                {
                    Url = "https://a.com/sub",
                    Nodes = [new NodeEntry { Protocol = "ss", Tag = "s1", Server = "b.com", ServerPort = 2 }],
                },
                new SubscriptionEntry
                {
                    Url = "https://b.com/sub",
                    Nodes = [new NodeEntry { Protocol = "ss", Tag = "s2", Server = "c.com", ServerPort = 3 }],
                },
            ],
        };

        var effective = settings.EffectiveNodes();

        Assert.Equal(3, effective.Count);
        Assert.Equal(["m1", "s1", "s2"], effective.Select(n => n.Tag));
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// 新装（%LOCALAPPDATA% 里没有 settings.json）时的默认值必须是「干净」的：
/// 不带任何订阅、不带节点、不自动开系统代理 / TUN / 局域网。
/// 这几条是发布包的对外承诺，改动默认值会让用户一装上就被代理或看到别人的订阅，故锁死。
/// </summary>
public class SafeDefaultsTests
{
    [Fact]
    public void 新装默认没有任何订阅()
    {
        var settings = new AppSettings();

        Assert.Empty(settings.Subscriptions);
        Assert.Null(settings.ActiveSubscriptionUrl);
        Assert.Null(settings.ActiveSubscription());
        Assert.Empty(settings.EffectiveSubscriptions());
        Assert.Empty(settings.EffectiveNodes());
        Assert.Empty(settings.EffectiveGroups());
    }

    [Fact]
    public void 新装默认不开启系统代理与TUN()
    {
        var settings = new AppSettings();

        Assert.False(settings.EnableSystemProxy);
        Assert.False(settings.EnableTun);
        Assert.False(settings.AllowLan);
    }

    [Fact]
    public void 新装默认没有手动节点与策略组()
    {
        var settings = new AppSettings();

        Assert.Empty(settings.Profile.Nodes);
        Assert.Empty(settings.Profile.Groups);
    }

    [Fact]
    public void 默认设置序列化后不含任何网址()
    {
        var json = JsonSerializer.Serialize(new AppSettings());
        var node = JsonNode.Parse(json)!;

        Assert.DoesNotContain("http://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(node["Subscriptions"]!.AsArray());
    }

    [Fact]
    public void 没有节点时生成配置会明确报错而不是产出空配置()
    {
        var settings = new AppSettings();
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigGenerator.BuildWithReport(settings));

        Assert.Contains("没有可用节点", ex.Message);
    }
}

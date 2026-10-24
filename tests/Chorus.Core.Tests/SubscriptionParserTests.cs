using System.Text;
using Chorus.Core.Parsing;
using Xunit;

namespace Chorus.Core.Tests;

public class SubscriptionParserTests
{
    [Fact]
    public void Parses_Base64_Encoded_Link_List()
    {
        var link1 = "trojan://pass1@1.1.1.1:443?sni=a.com#n1";
        var link2 = "trojan://pass2@2.2.2.2:443?sni=b.com#n2";
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(link1 + "\n" + link2));

        var (nodes, errors) = SubscriptionParser.Parse(content);

        Assert.Empty(errors);
        Assert.Equal(2, nodes.Count);
        Assert.Equal("n1", nodes[0].Tag);
        Assert.Equal("2.2.2.2", nodes[1].Server);
    }

    [Fact]
    public void Parses_Plain_Lines_With_Unknown_Lines_Reported()
    {
        var content = "trojan://pass1@1.1.1.1:443?sni=a.com#n1\n" +
                      "https://not-a-node.example\n" +
                      "vmess://bad_content!!!";

        var (nodes, errors) = SubscriptionParser.Parse(content);

        Assert.Single(nodes);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Parses_SingBox_Json()
    {
        const string content = """
            {
              "outbounds": [
                {
                  "type": "vless",
                  "tag": "s1",
                  "server": "a.com",
                  "server_port": 443,
                  "uuid": "u1",
                  "tls": { "enabled": true, "server_name": "a.com" },
                  "transport": { "type": "ws", "path": "/ws", "headers": { "Host": "cdn.a.com" } }
                },
                { "type": "direct", "tag": "direct" },
                { "type": "selector", "tag": "sel", "outbounds": ["s1"] }
              ]
            }
            """;

        var (nodes, errors) = SubscriptionParser.Parse(content);

        Assert.Empty(errors);
        Assert.Single(nodes);
        Assert.Equal("s1", nodes[0].Tag);
        Assert.Equal("vless", nodes[0].Protocol);
        Assert.Equal(443, nodes[0].ServerPort);
        Assert.True(nodes[0].Tls);
        Assert.Equal("a.com", nodes[0].Sni);
        Assert.Equal("ws", nodes[0].Transport);
        Assert.Equal("/ws", nodes[0].Path);
        Assert.Equal("cdn.a.com", nodes[0].Host);
    }

    [Fact]
    public void SingBox_Json_Shadowsocks_Normalized_To_Ss()
    {
        // sing-box JSON 订阅里 shadowsocks 节点需归一为内部 "ss"，否则配置生成会抛不支持
        const string content = """
            {
              "outbounds": [
                { "type": "shadowsocks", "tag": "hk", "server": "a.com", "server_port": 8388, "method": "aes-256-gcm", "password": "pw" }
              ]
            }
            """;

        var (nodes, errors) = SubscriptionParser.Parse(content);

        Assert.Empty(errors);
        var node = Assert.Single(nodes);
        Assert.Equal("ss", node.Protocol);
        Assert.Equal("aes-256-gcm", node.Method);
        Assert.Equal("pw", node.Password);
    }

    [Fact]
    public void SingBox_Json_Unsupported_Type_Is_Skipped_With_Error()
    {
        const string content = """
            {
              "outbounds": [
                { "type": "tuic", "tag": "weird", "server": "a.com", "server_port": 443, "uuid": "u", "password": "p" },
                { "type": "anytls", "tag": "ok", "server": "b.com", "server_port": 443, "password": "p", "tls": { "enabled": true } }
              ]
            }
            """;

        var (nodes, errors) = SubscriptionParser.Parse(content);

        var node = Assert.Single(nodes);
        Assert.Equal("anytls", node.Protocol);
        Assert.Contains(errors, e => e.Contains("tuic"));
    }

    [Fact]
    public void SingBox_Json_Extracts_Selector_And_UrlTest_Groups()
    {
        // sing-box JSON 订阅里的策略组需提取出来，供策略组页展示
        const string content = """
            {
              "outbounds": [
                { "type": "vless", "tag": "s1", "server": "a.com", "server_port": 443, "uuid": "u1" },
                { "type": "vless", "tag": "s2", "server": "b.com", "server_port": 443, "uuid": "u2" },
                { "type": "direct", "tag": "direct" },
                {
                  "type": "selector",
                  "tag": "🐸 手动切换",
                  "default": "s2",
                  "outbounds": ["s1", "s2", "direct"]
                },
                {
                  "type": "urltest",
                  "tag": "🦈 自动选择",
                  "url": "https://www.gstatic.com/generate_204",
                  "interval": "300s",
                  "outbounds": ["s1", "s2"]
                }
              ]
            }
            """;

        var (nodes, groups, errors) = SubscriptionParser.ParseWithGroups(content);

        Assert.Empty(errors);
        Assert.Equal(2, nodes.Count);
        Assert.Equal(2, groups.Count);

        var selector = groups.Single(g => g.Type == "selector");
        Assert.Equal("🐸 手动切换", selector.Name);
        Assert.Equal(["s1", "s2", "direct"], selector.Members);
        Assert.Equal("s2", selector.Default);

        var auto = groups.Single(g => g.Type == "urltest");
        Assert.Equal("🦈 自动选择", auto.Name);
        Assert.Equal(["s1", "s2"], auto.Members);
        Assert.Equal("https://www.gstatic.com/generate_204", auto.Url);
        Assert.Equal("300s", auto.Interval);
    }

    [Fact]
    public void Empty_Content_Returns_Error()
    {
        var (nodes, errors) = SubscriptionParser.Parse("   \n  ");

        Assert.Empty(nodes);
        Assert.Single(errors);
    }
}

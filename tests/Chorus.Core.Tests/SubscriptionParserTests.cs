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
    public void Empty_Content_Returns_Error()
    {
        var (nodes, errors) = SubscriptionParser.Parse("   \n  ");

        Assert.Empty(nodes);
        Assert.Single(errors);
    }
}

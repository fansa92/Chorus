using System.Text;
using Chorus.Core.Parsing;
using Xunit;

namespace Chorus.Core.Tests;

public class ShareLinkParserTests
{
    [Fact]
    public void Parses_Vmess_Base64Json()
    {
        var json = """
            {"v":"2","ps":"测试节点","add":"a.com","port":"8443","id":"b831381d-6324-4d53-ad4f-8cda48b30811","aid":"0","scy":"auto","net":"ws","host":"cdn.a.com","path":"/ws","tls":"tls","sni":"a.com"}
            """;
        var link = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var node = ShareLinkParser.Parse(link, out var error);

        Assert.Null(error);
        Assert.NotNull(node);
        Assert.Equal("vmess", node!.Protocol);
        Assert.Equal("测试节点", node.Tag);
        Assert.Equal("a.com", node.Server);
        Assert.Equal(8443, node.ServerPort);
        Assert.Equal("b831381d-6324-4d53-ad4f-8cda48b30811", node.Uuid);
        Assert.True(node.Tls);
        Assert.Equal("ws", node.Transport);
        Assert.Equal("/ws", node.Path);
        Assert.Equal("cdn.a.com", node.Host);
        Assert.Equal("a.com", node.Sni);
    }

    [Fact]
    public void Parses_Vless_Reality()
    {
        var link = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@1.2.3.4:443" +
                   "?encryption=none&security=reality&sni=www.apple.com&fp=chrome&pbk=PUBKEY&sid=0123abcd" +
                   "&type=ws&host=h.com&path=%2Fws#R%E8%8A%82%E7%82%B9";

        var node = ShareLinkParser.Parse(link, out var error);

        Assert.Null(error);
        Assert.NotNull(node);
        Assert.Equal("vless", node!.Protocol);
        Assert.Equal("R节点", node.Tag);
        Assert.Equal("1.2.3.4", node.Server);
        Assert.Equal(443, node.ServerPort);
        Assert.Equal("b831381d-6324-4d53-ad4f-8cda48b30811", node.Uuid);
        Assert.True(node.Tls);
        Assert.Equal("PUBKEY", node.RealityPublicKey);
        Assert.Equal("0123abcd", node.RealityShortId);
        Assert.Equal("chrome", node.Fingerprint);
        Assert.Equal("www.apple.com", node.Sni);
        Assert.Equal("ws", node.Transport);
        Assert.Equal("/ws", node.Path);
    }

    [Fact]
    public void Parses_Trojan()
    {
        var link = "trojan://pass123@1.2.3.4:443?sni=example.com&allowInsecure=1#trojan-node";

        var node = ShareLinkParser.Parse(link, out var error);

        Assert.Null(error);
        Assert.NotNull(node);
        Assert.Equal("trojan", node!.Protocol);
        Assert.Equal("pass123", node.Password);
        Assert.Equal("example.com", node.Sni);
        Assert.True(node.Tls);
        Assert.True(node.Insecure);
        Assert.Equal("trojan-node", node.Tag);
    }

    [Fact]
    public void Parses_SS_UserInfoBase64()
    {
        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pass123"));
        var link = $"ss://{userInfo}@1.2.3.4:8388#ss%E8%8A%82%E7%82%B9";

        var node = ShareLinkParser.Parse(link, out var error);

        Assert.Null(error);
        Assert.NotNull(node);
        Assert.Equal("ss", node!.Protocol);
        Assert.Equal("aes-256-gcm", node.Method);
        Assert.Equal("pass123", node.Password);
        Assert.Equal("1.2.3.4", node.Server);
        Assert.Equal(8388, node.ServerPort);
        Assert.Equal("ss节点", node.Tag);
    }

    [Fact]
    public void Parses_SS_WholeBase64()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pw@5.6.7.8:990"));
        var link = $"ss://{payload}#x";

        var node = ShareLinkParser.Parse(link, out var error);

        Assert.Null(error);
        Assert.NotNull(node);
        Assert.Equal("ss", node!.Protocol);
        Assert.Equal("chacha20-ietf-poly1305", node.Method);
        Assert.Equal("pw", node.Password);
        Assert.Equal("5.6.7.8", node.Server);
        Assert.Equal(990, node.ServerPort);
        Assert.Equal("x", node.Tag);
    }

    [Fact]
    public void Parses_Hysteria2_WithObfs()
    {
        var link = "hy2://pass@1.2.3.4:36712?sni=h.com&insecure=1&obfs=salamander&obfs-password=ob123#hy";

        var node = ShareLinkParser.Parse(link, out var error);

        Assert.Null(error);
        Assert.NotNull(node);
        Assert.Equal("hysteria2", node!.Protocol);
        Assert.Equal("pass", node.Password);
        Assert.Equal(36712, node.ServerPort);
        Assert.Equal("h.com", node.Sni);
        Assert.True(node.Tls);
        Assert.True(node.Insecure);
        Assert.Equal("ob123", node.ObfsPassword);
    }

    [Fact]
    public void Unsupported_Scheme_Returns_Error()
    {
        var node = ShareLinkParser.Parse("https://example.com", out var error);

        Assert.Null(node);
        Assert.NotNull(error);
        Assert.Contains("不支持", error);
    }

    [Fact]
    public void Broken_Vmess_Returns_Error()
    {
        var node = ShareLinkParser.Parse("vmess://not_base64!!!", out var error);

        Assert.Null(node);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseHostPort_Handles_IPv6()
    {
        var (server, port) = ShareLinkParser.ParseHostPort("[::1]:8388");

        Assert.Equal("::1", server);
        Assert.Equal(8388, port);
    }
}

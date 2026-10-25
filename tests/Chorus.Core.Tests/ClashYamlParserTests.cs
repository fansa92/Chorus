using Chorus.Core.Parsing;
using Xunit;

namespace Chorus.Core.Tests;

public class ClashYamlParserTests
{
    private const string ClashConfig = """
        mixed-port: 7890
        proxies:
          - name: "🇯🇵 JP-01"
            type: vless
            server: jp.example.com
            port: 443
            uuid: 11111111-2222-3333-4444-555555555555
            tls: true
            servername: www.lamer.com.sg
            flow: xtls-rprx-vision
            client-fingerprint: chrome
            reality-opts:
              public-key: Lk5nwDxC2tr3J7hT8emhUUiMX8cHyKX9TiRj_EnAiik
              short-id: 28f5c1f8f98a
          - name: "🇭🇰 HK-WS"
            type: vless
            server: hk.example.com
            port: 80
            uuid: 66666666-7777-8888-9999-000000000000
            network: ws
            ws-opts:
              path: /ws?ed=2048
              headers:
                Host: hk.example.com
          - name: "🛡 SS-01"
            type: ss
            server: ss.example.com
            port: 8388
            cipher: aes-256-gcm
            password: pass123
          - name: "⚡ HY2-01"
            type: hysteria2
            server: hy.example.com
            port: 8443
            password: hypass
            sni: hy.example.com
            skip-cert-verify: true
          - name: "❌ SSR-01"
            type: ssr
            server: ssr.example.com
            port: 1234
        proxy-groups:
          - name: "🚀 节点选择"
            type: select
            proxies:
              - "♻️ 自动选择"
              - "🇯🇵 JP-01"
              - "🛡 SS-01"
              - DIRECT
          - name: "♻️ 自动选择"
            type: url-test
            proxies:
              - "🇯🇵 JP-01"
              - "🇭🇰 HK-WS"
            url: "http://www.gstatic.com/generate_204"
            interval: 300
          - name: "🔁 故障转移"
            type: fallback
            proxies:
              - "⚡ HY2-01"
              - "🇭🇰 HK-WS"
          - name: "🛑 广告拦截"
            type: select
            proxies:
              - REJECT
              - DIRECT
        """;

    [Fact]
    public void Looks_Like_Clash_Detects_Top_Level_Keys()
    {
        Assert.True(ClashYamlParser.LooksLikeClash(ClashConfig));
        Assert.False(ClashYamlParser.LooksLikeClash("trojan://pass@1.1.1.1:443#n"));
        Assert.False(ClashYamlParser.LooksLikeClash("""{"outbounds":[]}"""));
    }

    [Fact]
    public void Parses_Proxies_And_Skips_Unsupported()
    {
        var (nodes, _, errors) = ClashYamlParser.Parse(ClashConfig);

        Assert.Equal(4, nodes.Count); // ssr 节点被跳过
        Assert.Contains(errors, e => e.Contains("ssr") || e.Contains("SSR-01"));

        var vless = nodes.Single(n => n.Tag == "🇯🇵 JP-01");
        Assert.Equal("vless", vless.Protocol);
        Assert.Equal(443, vless.ServerPort);
        Assert.True(vless.Tls);
        Assert.Equal("www.lamer.com.sg", vless.Sni);
        Assert.Equal("xtls-rprx-vision", vless.Flow);
        Assert.Equal("chrome", vless.Fingerprint);
        Assert.Equal("Lk5nwDxC2tr3J7hT8emhUUiMX8cHyKX9TiRj_EnAiik", vless.RealityPublicKey);
        Assert.Equal("28f5c1f8f98a", vless.RealityShortId);

        var ss = nodes.Single(n => n.Tag == "🛡 SS-01");
        Assert.Equal("ss", ss.Protocol);
        Assert.Equal("aes-256-gcm", ss.Method);
        Assert.Equal("pass123", ss.Password);

        var hy2 = nodes.Single(n => n.Tag == "⚡ HY2-01");
        Assert.Equal("hysteria2", hy2.Protocol);
        Assert.True(hy2.Tls);
        Assert.True(hy2.Insecure);
    }

    [Fact]
    public void Parses_Ws_Transport()
    {
        var (nodes, _, _) = ClashYamlParser.Parse(ClashConfig);

        var ws = nodes.Single(n => n.Tag == "🇭🇰 HK-WS");
        Assert.Equal("ws", ws.Transport);
        Assert.Equal("/ws?ed=2048", ws.Path);
        Assert.Equal("hk.example.com", ws.Host);
        Assert.False(ws.Tls);
    }

    [Fact]
    public void Parses_Groups_With_Type_Mapping_And_Builtin_Members()
    {
        var (_, groups, _) = ClashYamlParser.Parse(ClashConfig);

        Assert.Equal(4, groups.Count);

        var select = groups.Single(g => g.Name == "🚀 节点选择");
        Assert.Equal("selector", select.Type); // select → selector
        Assert.Contains("♻️ 自动选择", select.Members); // 嵌套组引用保留
        Assert.Contains("direct", select.Members); // DIRECT → direct（不区分大小写）

        var auto = groups.Single(g => g.Name == "♻️ 自动选择");
        Assert.Equal("urltest", auto.Type); // url-test → urltest
        Assert.Equal("300s", auto.Interval); // 秒数转 Go 时长
        Assert.Equal("http://www.gstatic.com/generate_204", auto.Url);

        var fallback = groups.Single(g => g.Name == "🔁 故障转移");
        Assert.Equal("urltest", fallback.Type); // fallback 归一到 urltest

        var block = groups.Single(g => g.Name == "🛑 广告拦截");
        Assert.Contains("block", block.Members); // REJECT → block
    }

    [Fact]
    public void Subscription_Parser_Routes_Clash_Yaml_To_Yaml_Parser()
    {
        var (nodes, groups, errors) = SubscriptionParser.ParseWithGroups(ClashConfig);

        Assert.Equal(4, nodes.Count);
        Assert.Equal(4, groups.Count);
        Assert.DoesNotContain(errors, e => e.Contains("Base64"));
    }

    [Fact]
    public void Empty_Proxies_Produces_Error_Not_Exception()
    {
        const string content = """
            proxy-groups:
              - name: G
                type: select
                proxies:
                  - DIRECT
            """;
        var (nodes, groups, errors) = ClashYamlParser.Parse(content);

        Assert.Empty(nodes);
        Assert.Single(groups);
        Assert.Contains(errors, e => e.Contains("没有可用的节点"));
    }
}

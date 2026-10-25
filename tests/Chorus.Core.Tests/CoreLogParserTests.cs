using Chorus.Core.Parsing;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>核心日志行解析：ANSI 颜色 / 时区时间戳 / 连接 id / 模块前缀都要正确剥掉。</summary>
public class CoreLogParserTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void 解析_sing_box_带颜色与时区的行()
    {
        var line = $"+0800 2026-10-25 10:14:34 {Esc}[36mINFO{Esc}[0m outbound/vless[HK 01]: outbound connection to www.gstatic.com:443";

        var parsed = CoreLogParser.Parse(line);

        Assert.Equal("INFO", parsed.Level);
        Assert.Equal("outbound/vless", parsed.Module);
        Assert.Equal("outbound connection to www.gstatic.com:443", parsed.Message);
    }

    [Fact]
    public void 解析带连接id的入站行()
    {
        var line = $"+0800 2026-10-25 10:14:37 {Esc}[36mINFO{Esc}[0m [{Esc}[38;5;30m2177483534{Esc}[0m 0ms] inbound/mixed[mixed-in]: inbound connection from 127.0.0.1:52133";

        var parsed = CoreLogParser.Parse(line);

        Assert.Equal("INFO", parsed.Level);
        Assert.Equal("inbound/mixed", parsed.Module);
        Assert.Equal("inbound connection from 127.0.0.1:52133", parsed.Message);
    }

    [Fact]
    public void 解析错误行并保留正文()
    {
        var line = $"+0800 2026-10-25 10:10:35 {Esc}[31mERROR{Esc}[0m [{Esc}[38;5;30m2654300302{Esc}[0m 5.0s] connection: open connection to www.gstatic.com:443 using outbound/vless[JP]: dial tcp 18.140.198.185:46105: i/o timeout";

        var parsed = CoreLogParser.Parse(line);

        Assert.Equal("ERROR", parsed.Level);
        Assert.Equal("connection", parsed.Module);
        Assert.Contains("i/o timeout", parsed.Message);
        // ANSI 控制字符必须被清掉（逐字符检查，避免 xUnit 对子串重载的歧义）
        Assert.False(parsed.Message.Any(c => c < 32));
    }

    [Fact]
    public void 兼容_clash_风格的方括号模块()
    {
        var parsed = CoreLogParser.Parse("[DNS] resolve example.com");

        Assert.Equal("", parsed.Level);
        Assert.Equal("DNS", parsed.Module);
        Assert.Equal("resolve example.com", parsed.Message);
    }

    [Fact]
    public void 无前缀的日志原样保留()
    {
        var parsed = CoreLogParser.Parse("核心已启动。");

        Assert.Equal("", parsed.Level);
        Assert.Equal("", parsed.Module);
        Assert.Equal("核心已启动。", parsed.Message);
    }

    [Fact]
    public void 中文节点名不会因为正则丢字符()
    {
        var line = $"+0800 2026-10-25 10:14:39 {Esc}[36mINFO{Esc}[0m network: updated default interface 浙江移动, index 15";

        var parsed = CoreLogParser.Parse(line);

        Assert.Equal("network", parsed.Module);
        Assert.Equal("updated default interface 浙江移动, index 15", parsed.Message);
    }
}

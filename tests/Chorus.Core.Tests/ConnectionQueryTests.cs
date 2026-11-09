using Chorus.Core.ClashApi;
using Xunit;

namespace Chorus.Core.Tests;

public class ConnectionQueryTests
{
    private static ConnectionInfo Conn(
        string id, string process = "", string? outbound = "HK 01",
        string network = "tcp", long up = 0, long down = 0,
        DateTimeOffset? start = null, string host = "example.com", string ip = "1.2.3.4") => new()
    {
        Id = id,
        Host = host,
        DestinationIp = ip,
        DestinationPort = "443",
        Network = network,
        Process = process,
        Rule = "rule-set( test ) => proxy",
        RulePayload = "proxy",
        Chain = outbound is null ? [] : [outbound, "🚀 节点选择"],
        Upload = up,
        Download = down,
        Start = start,
    };

    [Theory]
    [InlineData("HK 01", ConnectionKind.Proxy)]
    [InlineData("DIRECT", ConnectionKind.Direct)]
    [InlineData("direct", ConnectionKind.Direct)]
    [InlineData("REJECT", ConnectionKind.Blocked)]
    [InlineData("REJECT-DROP", ConnectionKind.Blocked)]
    public void KindOf_Uses_The_Final_Outbound(string outbound, ConnectionKind expected) =>
        Assert.Equal(expected, ConnectionQuery.KindOf(Conn("a", outbound: outbound)));

    [Fact]
    public void KindOf_No_Chain_Is_Unknown() =>
        Assert.Equal(ConnectionKind.Unknown, ConnectionQuery.KindOf(Conn("a", outbound: null)));

    [Fact]
    public void Matches_Searches_Every_Visible_Field()
    {
        var c = Conn("a", process: @"C:\Program Files\browser\chrome.exe", outbound: "HK 01");

        Assert.True(ConnectionQuery.Matches(c, "EXAMPLE"));       // 域名，大小写不敏感
        Assert.True(ConnectionQuery.Matches(c, "1.2.3.4"));      // 目标 IP
        Assert.True(ConnectionQuery.Matches(c, "chrome"));        // 进程文件名
        Assert.True(ConnectionQuery.Matches(c, "rule-set"));      // 规则
        Assert.True(ConnectionQuery.Matches(c, "HK 01"));         // 链路
        Assert.True(ConnectionQuery.Matches(c, "  tcp  "));       // 协议 + 去空格
        Assert.True(ConnectionQuery.Matches(c, ""));              // 空条件全放行
        Assert.False(ConnectionQuery.Matches(c, "not-there"));
    }

    [Fact]
    public void Apply_Filters_By_Kind_Network_And_Query()
    {
        var all = new[]
        {
            Conn("proxy-tcp", @"C:\a.exe", "HK 01", "tcp"),
            Conn("direct-tcp", @"C:\b.exe", "DIRECT", "tcp"),
            Conn("proxy-udp", @"C:\c.exe", "HK 01", "udp"),
            Conn("blocked", @"C:\d.exe", "REJECT", "tcp"),
        };

        Assert.Equal(4, ConnectionQuery.Apply(all, null).Count);
        Assert.Equal(new[] { "proxy-tcp", "proxy-udp" },
            ConnectionQuery.Apply(all, null, ConnectionKind.Proxy).Select(c => c.Id));
        Assert.Equal(new[] { "proxy-tcp" },
            ConnectionQuery.Apply(all, null, ConnectionKind.Proxy, "tcp").Select(c => c.Id));
        Assert.Equal(new[] { "proxy-udp" },
            ConnectionQuery.Apply(all, "proxy", network: "udp").Select(c => c.Id));
        Assert.Equal(new[] { "blocked" },
            ConnectionQuery.Apply(all, null, ConnectionKind.Blocked).Select(c => c.Id));
    }

    [Fact]
    public void Apply_Sorts_By_Recent_Download_Upload_And_Process()
    {
        var now = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var all = new[]
        {
            Conn("old", @"C:\zeta.exe", up: 10, down: 10, start: now.AddMinutes(-5)),
            Conn("new", @"C:\alpha.exe", up: 1, down: 1, start: now),
            Conn("big", @"C:\mid.exe", up: 999, down: 999, start: now.AddMinutes(-1)),
            Conn("nostart", @"C:\beta.exe", up: 0, down: 0),
        };

        // 最近建立：有时间的在前、新的更前，没时间的垫底
        Assert.Equal(new[] { "new", "big", "old", "nostart" },
            ConnectionQuery.Apply(all, null, sort: ConnectionSort.Recent).Select(c => c.Id));
        Assert.Equal(new[] { "big", "old", "new", "nostart" },
            ConnectionQuery.Apply(all, null, sort: ConnectionSort.Download).Select(c => c.Id));
        Assert.Equal(new[] { "big", "old", "new", "nostart" },
            ConnectionQuery.Apply(all, null, sort: ConnectionSort.Upload).Select(c => c.Id));
        // alpha / beta / mid / zeta 的字母序
        Assert.Equal(new[] { "new", "nostart", "big", "old" },
            ConnectionQuery.Apply(all, null, sort: ConnectionSort.Process).Select(c => c.Id));
    }

    [Fact]
    public void Summarize_Counts_Totals_And_Top_Process()
    {
        var all = new[]
        {
            Conn("a", @"C:\chrome.exe", up: 100, down: 200),
            Conn("b", @"C:\chrome.exe", up: 5, down: 50),
            Conn("c", @"C:\curl.exe", up: 1, down: 1),
        };

        var s = ConnectionQuery.Summarize(all);
        Assert.Equal(3, s.Count);
        Assert.Equal(106, s.Upload);
        Assert.Equal(251, s.Download);
        Assert.Equal("chrome.exe", s.TopProcess);
        Assert.Equal("106 B", s.UploadText);
    }

    [Fact]
    public void FormatDuration_Handles_Seconds_Minutes_Hours_And_Missing_Start()
    {
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("12s", ConnectionQuery.FormatDuration(now.AddSeconds(-12), now));
        Assert.Equal("3m 5s", ConnectionQuery.FormatDuration(now.AddMinutes(-3).AddSeconds(-5), now));
        Assert.Equal("2h 10m", ConnectionQuery.FormatDuration(now.AddHours(-2).AddMinutes(-10), now));
        Assert.Equal("1d 2h", ConnectionQuery.FormatDuration(now.AddDays(-1).AddHours(-2), now));
        Assert.Equal("", ConnectionQuery.FormatDuration(null, now));
        Assert.Equal("0s", ConnectionQuery.FormatDuration(now.AddSeconds(30), now)); // 时钟回拨
    }

    [Fact]
    public void ProcessName_Falls_Back_To_Inbound_When_Path_Missing()
    {
        Assert.Equal("chrome.exe", Conn("a", @"C:\Program Files\chrome.exe").ProcessName);
        var without = Conn("b");
        without.Inbound = "mixed-in";
        Assert.Equal("mixed-in", without.ProcessName);
        without.Inbound = "";
        Assert.Equal("未知进程", without.ProcessName);
    }
}

using Chorus.Core.Utils;
using Xunit;

namespace Chorus.Core.Tests;

public class IpInfoParserTests
{
    [Fact]
    public void IpWhoIs_Parses_Chinese_Country_And_Isp()
    {
        const string json = """
        {"ip":"203.0.113.7","success":true,"type":"IPv4","continent":"亚洲",
         "country":"中国香港","country_code":"HK","region":"香港","city":"香港",
         "connection":{"asn":4760,"org":"HKT Limited","isp":"HKT"}}
        """;

        var info = IpInfoParser.ParseIpWhoIs(json);

        Assert.NotNull(info);
        Assert.Equal("203.0.113.7", info!.Ip);
        Assert.Equal("中国香港", info.Country);
        Assert.Equal("香港", info.City);
        Assert.Equal("HKT", info.Isp);
    }

    [Fact]
    public void IpWhoIs_Falls_Back_To_Org_When_Isp_Missing()
    {
        const string json = """
        {"ip":"198.51.100.9","success":true,"country":"日本","city":"东京",
         "connection":{"asn":2497,"org":"NTT Communications"}}
        """;

        var info = IpInfoParser.ParseIpWhoIs(json);

        Assert.NotNull(info);
        Assert.Equal("NTT Communications", info!.Isp);
    }

    [Fact]
    public void IpWhoIs_Returns_Null_When_Api_Reports_Failure()
    {
        const string json = """{"ip":"1.1.1.1","success":false,"message":"Invalid IP address"}""";

        Assert.Null(IpInfoParser.ParseIpWhoIs(json));
    }

    [Fact]
    public void IpApi_Parses_Success_Payload()
    {
        const string json = """
        {"status":"success","country":"美国","regionName":"加利福尼亚","city":"洛杉矶",
         "isp":"Cloudflare, Inc.","query":"104.16.0.1"}
        """;

        var info = IpInfoParser.ParseIpApi(json);

        Assert.NotNull(info);
        Assert.Equal("104.16.0.1", info!.Ip);
        Assert.Equal("美国", info.Country);
        Assert.Equal("洛杉矶", info.City);
        Assert.Equal("Cloudflare, Inc.", info.Isp);
    }

    [Fact]
    public void IpApi_Returns_Null_On_Fail_Status_Or_Garbage()
    {
        Assert.Null(IpInfoParser.ParseIpApi("""{"status":"fail","message":"private range"}"""));
        Assert.Null(IpInfoParser.ParseIpApi("not json"));
        Assert.Null(IpInfoParser.ParseIpApi(""));
    }

    [Theory]
    [InlineData("中国香港", "香港", "中国香港")]      // 城市已包含在地区名里，不重复
    [InlineData("日本", "东京", "日本 · 东京")]
    [InlineData("日本", "", "日本")]
    [InlineData("", "首尔", "首尔")]
    [InlineData("", "", "未知地区")]
    public void LocationText_Joins_Country_And_City_Without_Duplicates(
        string country, string city, string expected)
    {
        var info = new IpInfo("1.2.3.4", country, city, "");

        Assert.Equal(expected, info.LocationText);
    }
}

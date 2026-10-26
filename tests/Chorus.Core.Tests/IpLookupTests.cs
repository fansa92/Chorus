using Chorus.Core.Net;
using Chorus.Core.Utils;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>多源 IP 归属解析 + 本地 GeoIP 兜底的行为约束。</summary>
[Collection("LocalGeoLookupStatic")]
public class IpLookupTests
{
    // ---- 多源解析 ----

    [Fact]
    public void ParseIpApi_Reads_Code_And_Isp()
    {
        var json = """
        {"status":"success","country":"日本","countryCode":"JP","regionName":"Tokyo",
         "city":"Tokyo","isp":"NTT","query":"203.0.113.5"}
        """;

        var info = IpInfoParser.ParseIpApi(json);

        Assert.NotNull(info);
        Assert.Equal("203.0.113.5", info!.Ip);
        Assert.Equal("JP", info.CountryCode);
        Assert.Equal("NTT", info.Isp);
        Assert.Equal("ip-api", info.Source);
    }

    [Fact]
    public void ParseIpWhoIs_Reads_Connection_Isp()
    {
        var json = """
        {"ip":"1.2.3.4","success":true,"country":"美国","country_code":"US","city":"LA",
         "connection":{"asn":1,"org":"Example","isp":"Example ISP"}}
        """;

        var info = IpInfoParser.ParseIpWhoIs(json);

        Assert.NotNull(info);
        Assert.Equal("Example ISP", info!.Isp);
        Assert.Equal("ipwho.is", info.Source);
    }

    [Fact]
    public void ParseIpInfoIo_Uses_Code_And_Cleans_Org()
    {
        var json = """
        {"ip":"5.6.7.8","city":"Berlin","region":"Berlin","country":"DE","org":"AS3320 Deutsche Telekom AG"}
        """;

        var info = IpInfoParser.ParseIpInfoIo(json);

        Assert.NotNull(info);
        Assert.Equal("DE", info!.CountryCode);
        Assert.Equal("德国", info.Country); // 只有代码时用本地表补出中文名
        Assert.Equal("Deutsche Telekom AG", info.Isp); // 去掉 AS 编号前缀
        Assert.Equal("ipinfo.io", info.Source);
    }

    [Fact]
    public void ParseIpIm_Reads_Isp_Or_AsnOrg()
    {
        var json = """{"ip":"9.9.9.9","country":"德国","country_code":"DE","city":"Berlin","isp":"Deutsche"}""";

        var info = IpInfoParser.ParseIpIm(json);

        Assert.NotNull(info);
        Assert.Equal("Deutsche", info!.Isp);
        Assert.Equal("ip.im", info.Source);
    }

    [Fact]
    public void ParseIplark_Reads_CamelCase_Fields()
    {
        var json = """{"ip":"7.7.7.7","countryCode":"FR","country":"法国","city":"Paris","isp":"Orange"}""";

        var info = IpInfoParser.ParseIplark(json);

        Assert.NotNull(info);
        Assert.Equal("FR", info!.CountryCode);
        Assert.Equal("Paris", info.City);
        Assert.Equal("iplark", info.Source);
    }

    [Fact]
    public void ParseIfconfigCo_Reads_CountryIso_And_AsnOrg()
    {
        var json = """
        {"ip":"6.6.6.6","country":"China","country_iso":"CN","asn_org":"CHINANET IDC"}
        """;

        var info = IpInfoParser.ParseIfconfigCo(json);

        Assert.NotNull(info);
        Assert.Equal("CN", info!.CountryCode);
        Assert.Equal("CHINANET IDC", info.Isp);
        Assert.Equal("ifconfig.co", info.Source);
    }

    [Fact]
    public void Parsers_Return_Null_For_Broken_Json()
    {
        Assert.Null(IpInfoParser.ParseIpApi("not json"));
        Assert.Null(IpInfoParser.ParseIpWhoIs("{"));
        Assert.Null(IpInfoParser.ParseIpInfoIo(""));
        Assert.Null(IpInfoParser.ParseIpIm("<html>"));
        Assert.Null(IpInfoParser.ParseIplark("null"));
        Assert.Null(IpInfoParser.ParseIfconfigCo("{}"));
    }

    [Fact]
    public void ParseIpApi_Fails_When_Status_Is_Fail()
    {
        Assert.Null(IpInfoParser.ParseIpApi("""{"status":"fail","message":"reserved range"}"""));
    }

    [Fact]
    public void ParseIpWhoIs_Fails_When_Success_Is_False()
    {
        Assert.Null(IpInfoParser.ParseIpWhoIs("""{"success":false,"message":"quota"}"""));
    }

    // ---- 本地 GeoIP 兜底 ----

    [Fact]
    public void LocalGeo_Marks_Private_Address_As_Lan()
    {
        var info = LocalGeoLookup.Lookup("192.168.1.10");

        Assert.NotNull(info);
        Assert.Equal("内网地址", info!.Country);
        Assert.Equal("local", info.Source);
    }

    [Fact]
    public void LocalGeo_Finds_China_Segment()
    {
        var info = LocalGeoLookup.Lookup("114.114.114.114");

        Assert.NotNull(info);
        Assert.Equal("CN", info!.CountryCode);
        Assert.Equal("中国", info.Country);
        Assert.Equal("local", info.Source);
    }

    [Fact]
    public void LocalGeo_Returns_Null_For_Unknown_Address()
    {
        Assert.Null(LocalGeoLookup.Lookup("203.0.113.222")); // 文档保留段，库里没有
    }

    [Fact]
    public void LocalGeo_Returns_Null_For_Invalid_Input()
    {
        Assert.Null(LocalGeoLookup.Lookup("不是 IP"));
        Assert.Null(LocalGeoLookup.Lookup(""));
    }

    [Fact]
    public void LocalGeo_Has_NonEmpty_Range_Table()
    {
        Assert.True(LocalGeoLookup.RangeCount > 10);
    }

    [Fact]
    public void LocalGeo_Range_Table_Has_No_Conflicting_Overlaps()
    {
        // 同前缀长度的两个不同国家段 = 查询结果取决于表内顺序 = 会给出错误的国家。
        // 这条测试守住「段表不重叠」这个约束。
        var conflicts = LocalGeoLookup.FindOverlaps();
        Assert.True(conflicts.Count == 0,
            "段表存在重叠：" + string.Join("；", conflicts));
    }

    // ---- 内网地址判定 ----

    [Fact]
    public void IsPrivateAddress_Covers_Rfc1918_And_Cgnat()
    {
        Assert.True(LocalAddressProvider.IsPrivateAddress("10.1.2.3"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("172.16.0.1"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("172.31.255.254"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("192.168.0.1"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("169.254.1.1"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("100.64.0.1"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("127.0.0.1"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("::1"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("fe80::1"));
        Assert.True(LocalAddressProvider.IsPrivateAddress("fd00::1"));

        Assert.False(LocalAddressProvider.IsPrivateAddress("8.8.8.8"));
        Assert.False(LocalAddressProvider.IsPrivateAddress("172.32.0.1")); // 不在 172.16/12 内
        Assert.False(LocalAddressProvider.IsPrivateAddress("11.0.0.1"));
    }

    [Fact]
    public void Collect_Returns_Addresses_Or_Empty()
    {
        // 本机总有网卡（至少回环，但回环被排除），所以可能为空——这里只验证不抛异常
        var addresses = LocalAddressProvider.Collect();
        Assert.NotNull(addresses);
        foreach (var a in addresses)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.InterfaceName));
            // 至少有一个地址
            Assert.True(a.Ipv4.Length > 0 || a.Ipv6.Length > 0);
        }
    }

    // ---- 国家名表 ----

    [Fact]
    public void CountryNames_Resolves_Common_Codes()
    {
        Assert.Equal("中国", CountryNames.Of("CN"));
        Assert.Equal("美国", CountryNames.Of("us"));
        Assert.Equal("日本", CountryNames.Of("JP"));
        // 未收录时返回代码本身
        Assert.Equal("ZZ", CountryNames.Of("ZZ"));
        Assert.Equal("", CountryNames.Of(""));
    }
}

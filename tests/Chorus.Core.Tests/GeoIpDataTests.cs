using System.IO;
using Chorus.Core.Net;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// GeoIP 段表的下载解析、二进制缓存与外部表查询。
/// 与 <see cref="IpLookupTests"/> 同属一个 Collection，避免改动静态段表时互相干扰。
/// </summary>
[Collection("LocalGeoLookupStatic")]
public class GeoIpDataTests
{
    private const string SampleCsv = """
        1.0.0.0,1.0.0.255,AU
        1.0.1.0,1.0.3.255,CN
        1.0.4.0,1.0.7.255,AU
        1.0.8.0,1.0.15.255,CN
        1.0.16.0,1.0.31.255,JP
        """;

    [Fact]
    public void ParseCsv_Reads_Ranges_And_Codes()
    {
        var count = GeoIpDataUpdater.ParseCsv(
            new StringReader(SampleCsv), out var starts, out var ends, out var codes);

        Assert.Equal(5, count);
        Assert.Equal(new uint[] { 0x0100_0000, 0x0100_0100, 0x0100_0400, 0x0100_0800, 0x0100_1000 }, starts);
        Assert.Equal(0x0100_00FFu, ends[0]);
        Assert.Equal("AU", codes[0]);
        Assert.Equal("CN", codes[1]);
        Assert.Equal("JP", codes[4]);
    }

    [Fact]
    public void ParseCsv_Skips_Header_And_Garbage()
    {
        const string csv = """
            start,end,country
            1.0.0.0,1.0.0.255,AU
            not-an-ip,1.0.0.255,CN
            1.0.1.0,1.0.0.0,CN
            1.0.2.0,1.0.3.255,
            """;

        var count = GeoIpDataUpdater.ParseCsv(new StringReader(csv), out _, out _, out var codes);

        Assert.Equal(1, count);
        Assert.Equal("AU", codes[0]);
    }

    [Fact]
    public void ParseCsv_Sorts_Out_Of_Order_Input()
    {
        const string csv = """
            1.0.4.0,1.0.7.255,AU
            1.0.0.0,1.0.0.255,US
            """;

        var count = GeoIpDataUpdater.ParseCsv(new StringReader(csv), out var starts, out _, out var codes);

        Assert.Equal(2, count);
        Assert.Equal(0x0100_0000u, starts[0]);
        Assert.Equal("US", codes[0]);
        Assert.Equal("AU", codes[1]);
    }

    [Fact]
    public void Binary_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"geoip-{Guid.NewGuid():N}.bin");
        try
        {
            uint[] starts = [0x0100_0000, 0x0100_0100, 0x0100_0200];
            uint[] ends = [0x0100_00FF, 0x0100_01FF, 0x0100_02FF];
            string[] codes = ["AU", "CN", "AU"];

            GeoIpDataUpdater.WriteBinary(path, starts, ends, codes);
            var table = GeoIpDataUpdater.ReadBinary(path);

            Assert.NotNull(table);
            Assert.Equal(starts, table!.Value.Starts);
            Assert.Equal(ends, table.Value.Ends);
            Assert.Equal(codes, table.Value.Codes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadBinary_Rejects_Garbage_And_Missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"geoip-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
            Assert.Null(GeoIpDataUpdater.ReadBinary(path));
            Assert.Null(GeoIpDataUpdater.ReadBinary(path + ".missing"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadExternal_Rejects_Mismatched_Arrays()
    {
        try
        {
            LocalGeoLookup.LoadExternal([1u, 2u], [3u], ["CN"]);
            Assert.False(LocalGeoLookup.HasExternalData);

            LocalGeoLookup.LoadExternal([], [], []);
            Assert.False(LocalGeoLookup.HasExternalData);
        }
        finally
        {
            LocalGeoLookup.ClearExternal();
        }
    }

    [Fact]
    public void External_Table_Takes_Precedence_And_Clear_Restores_Builtin()
    {
        try
        {
            // 内置表没有覆盖 1.0.0.0/8，加载外部表后应能查到
            LocalGeoLookup.LoadExternal([0x0100_0000u], [0x0100_FFFFu], ["AU"]);

            Assert.True(LocalGeoLookup.HasExternalData);
            Assert.Equal(1, LocalGeoLookup.RangeCount);
            Assert.Equal("AU", LocalGeoLookup.Lookup("1.0.0.5")?.CountryCode);

            // 外部表没覆盖的段回落到内置表（114DNS 内置标为 CN）
            Assert.Equal("CN", LocalGeoLookup.Lookup("114.114.114.114")?.CountryCode);
        }
        finally
        {
            LocalGeoLookup.ClearExternal();
        }

        Assert.False(LocalGeoLookup.HasExternalData);
        Assert.Null(LocalGeoLookup.Lookup("1.0.0.5")); // 回到内置表后查不到
        Assert.Equal("CN", LocalGeoLookup.Lookup("114.114.114.114")?.CountryCode);
    }
}

using System.Net;
using System.Text.Json;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// 订阅用量信息：来自服务端响应头，不是所有服务商都有。
/// 关键行为：缺失时整体为 null（界面不显示）、0 的含义按字段区分、剩余流量与到期提醒能算对。
/// </summary>
public class SubscriptionUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(8));
    private const long Kb = 1024L;
    private const long Mb = 1024 * Kb;
    private const long Gb = 1024 * Mb;

    private static SubscriptionUsage Usage(long? upload, long? download, long? total, DateTimeOffset? expire) => new()
    {
        Upload = upload,
        Download = download,
        Total = total,
        ExpiresAt = expire,
        FetchedAt = Now,
    };

    /// <summary>还剩 <paramref name="daysLeft"/> 天到期时的「已用量」，让剩余刚好是 <paramref name="remainGb"/> GB。</summary>
    private static SubscriptionUsage WithRemaining(long remainGb, int daysLeft) =>
        Usage(0, (20 - remainGb) * Gb, 20 * Gb, daysLeft == 0 ? null : Now.AddDays(daysLeft));

    [Fact]
    public void Parses_Standard_Userinfo_Header()
    {
        var expires = Now.AddDays(30);
        var usage = SubscriptionUsage.Parse(
            $"upload={Gb}; download={2 * Gb}; total={20 * Gb}; expire={expires.ToUnixTimeSeconds()}", Now)!;

        Assert.Equal(Gb, usage.Upload);
        Assert.Equal(2 * Gb, usage.Download);
        Assert.Equal(20 * Gb, usage.Total);
        Assert.Equal(expires, usage.ExpiresAt);
        Assert.Equal(17 * Gb, usage.RemainingBytes);
        Assert.Contains("剩余 17.00 GB / 20.00 GB", usage.Describe(Now));
        Assert.Contains("30 天后到期（12-06）", usage.Describe(Now));
    }

    [Theory]
    [InlineData("upload=0;download=1;total=2;expire=0")]                 // 分号 + 无空格
    [InlineData("upload=0, download=1, total=2, expire=0")]              // 个别服务端用逗号
    [InlineData("UPLOAD=0; Download=1 ; Total=2 ; Expire=0")]            // 键名大小写混用
    [InlineData("upload=\"0\";download=\"1\";total=\"2\";expire=\"0\"")] // 值带引号
    public void Tolerates_Header_Formatting(string header)
    {
        var usage = SubscriptionUsage.Parse(header, Now)!;
        Assert.Equal(0L, usage.Upload);
        Assert.Equal(1L, usage.Download);
        Assert.Equal(2L, usage.Total);
        Assert.Null(usage.ExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("upload=abc; download=; total=1/2")]
    [InlineData("auth=token; node-info=3")]
    public void Missing_Or_Unparsable_Header_Means_No_Usage(string? header)
        => Assert.Null(SubscriptionUsage.Parse(header, Now));

    [Fact]
    public void Zero_Total_And_Expire_Are_Read_As_Unknown()
    {
        // total=0 是「不限/未提供」，expire=0 是「长期有效」——都不能当成真实的 0
        var usage = SubscriptionUsage.Parse("upload=1;download=1;total=0;expire=0", Now)!;
        Assert.Null(usage.Total);
        Assert.Null(usage.ExpiresAt);
        Assert.Null(usage.RemainingBytes);
        Assert.Equal("已用 2 B", usage.Describe(Now));
    }

    [Fact]
    public void Fresh_Account_Still_Shows_The_Whole_Allowance()
    {
        var usage = SubscriptionUsage.Parse($"upload=0;download=0;total={20 * Gb};expire=0", Now)!;
        Assert.Equal(0L, usage.UsedBytes);
        Assert.Equal(20 * Gb, usage.RemainingBytes);
        Assert.Contains("剩余 20.00 GB", usage.Describe(Now));
        Assert.Equal(SubscriptionUsageLevel.None, usage.Level(Now));
    }

    [Fact]
    public void Millisecond_Timestamps_Still_Land_On_The_Same_Day()
    {
        var usage = SubscriptionUsage.Parse(
            $"upload=0;download=0;total={Gb};expire={Now.AddDays(3).ToUnixTimeMilliseconds()}", Now)!;
        Assert.Equal(Now.AddDays(3), usage.ExpiresAt);
    }

    // 下面三条走真实的 HttpClient 响应对象：字段名大小写、头挂在响应还是内容上，都不能漏
    [Fact]
    public void Reads_Header_From_Response_Regardless_Of_Casing()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("subscription-UserInfo",
            $"upload=1;download=2;total={3 * Gb};expire={Now.AddDays(3).ToUnixTimeSeconds()}");

        var usage = SubscriptionService.ReadUsage(response, Now)!;
        Assert.Equal(3 * Gb - 3, usage.RemainingBytes);
        Assert.Equal(3, usage.DaysUntilExpiry(Now));
    }

    [Fact]
    public void Reads_Header_Placed_On_Content_Headers()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Content.Headers.TryAddWithoutValidation("Subscription-Info", "upload=0;download=0;total=1024;expire=0");

        Assert.Equal(1024L, SubscriptionService.ReadUsage(response, Now)!.Total);
    }

    [Fact]
    public void Header_Without_Usable_Numbers_Is_Treated_As_Absent()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=oops;download=");

        Assert.Null(SubscriptionService.ReadUsage(response, Now));
    }

    [Fact]
    public void Used_Up_Allowance_Clamps_Ratio_And_Alerts()
    {
        var usage = Usage(60 * Gb, 60 * Gb, 100 * Gb, null);
        Assert.Equal(0L, usage.RemainingBytes);
        Assert.Equal(1d, usage.UsedRatio!.Value);
        Assert.Equal("流量已用完", usage.AlertText(Now));
        Assert.Equal(SubscriptionUsageLevel.Critical, usage.Level(Now));
    }

    [Theory]
    [InlineData(1, 30, SubscriptionUsageLevel.Warning)] // 剩 5%：流量紧张
    [InlineData(12, 7, SubscriptionUsageLevel.Warning)] // 7 天内到期
    [InlineData(12, 8, SubscriptionUsageLevel.None)]    // 刚过提醒窗口
    [InlineData(12, -1, SubscriptionUsageLevel.Critical)]
    [InlineData(12, 0, SubscriptionUsageLevel.None)]    // expire=0：长期有效
    public void Levels_And_Alerts(long remainGb, int daysLeft, SubscriptionUsageLevel expected)
    {
        var usage = WithRemaining(remainGb, daysLeft);
        Assert.Equal(expected, usage.Level(Now));
        Assert.Equal(expected == SubscriptionUsageLevel.None, string.IsNullOrEmpty(usage.AlertText(Now)));
    }

    [Fact]
    public void Expiring_Soon_And_Low_Traffic_Both_Reported()
    {
        var alert = WithRemaining(1, 2).AlertText(Now);
        Assert.Contains("将在 2 天后到期", alert);
        Assert.Contains("剩余流量不足", alert);
    }

    [Fact]
    public void Last_Day_Before_Expiry_Still_Counts_As_One_Day()
    {
        // 到期时间就在几小时后：不能因为向上取整之前的负数/零判断而漏掉提醒
        var usage = Usage(0, 0, 20 * Gb, Now.AddHours(3));
        Assert.Equal(1, usage.DaysUntilExpiry(Now));
        Assert.False(usage.IsExpired(Now));
        Assert.Contains("将在 1 天后到期", usage.AlertText(Now));
    }

    [Fact]
    public void Expired_Account_Describes_The_Date_It_Went_Out()
    {
        var usage = Usage(0, 0, 20 * Gb, Now.AddDays(-3));
        Assert.True(usage.IsExpired(Now));
        Assert.Null(usage.DaysUntilExpiry(Now));
        Assert.Contains("已到期（2026-11-03）", usage.Describe(Now));
        Assert.Equal(SubscriptionUsageLevel.Critical, usage.Level(Now));
    }

    [Fact]
    public void Usage_Survives_Settings_Roundtrip()
    {
        var options = new JsonSerializerOptions { Converters = { new SubscriptionEntryConverter() } };
        var entry = new SubscriptionEntry
        {
            Name = "A",
            Url = "https://a.example/sub",
            Usage = Usage(Gb, 2 * Gb, 20 * Gb, Now.AddDays(30)),
        };

        var restored = JsonSerializer.Deserialize<SubscriptionEntry>(JsonSerializer.Serialize(entry, options), options)!;

        Assert.Equal(Gb, restored.Usage!.Upload);
        Assert.Equal(2 * Gb, restored.Usage.Download);
        Assert.Equal(20 * Gb, restored.Usage.Total);
        Assert.Equal(Now.AddDays(30), restored.Usage.ExpiresAt);
        Assert.Equal(17 * Gb, restored.Usage.RemainingBytes);
    }

    [Fact]
    public void Entry_Without_Usage_Writes_No_Usage_Node()
    {
        var options = new JsonSerializerOptions { Converters = { new SubscriptionEntryConverter() } };
        var json = JsonSerializer.Serialize(new SubscriptionEntry { Name = "A", Url = "https://a.example/sub" }, options);
        Assert.DoesNotContain("usage", json);
        Assert.Null(JsonSerializer.Deserialize<SubscriptionEntry>(json, options)!.Usage);
    }

    [Fact]
    public void Usage_That_Has_Nothing_But_A_Timestamp_Is_Not_Stored()
    {
        // 全空的用量对象存进 settings.json 只会让卡片凭空多一行空白
        var options = new JsonSerializerOptions { Converters = { new SubscriptionEntryConverter() } };
        var json = JsonSerializer.Serialize(
            new SubscriptionEntry
            {
                Name = "A",
                Url = "https://a.example/sub",
                Usage = new SubscriptionUsage { FetchedAt = Now },
            },
            options);
        Assert.DoesNotContain("usage", json);
    }
}

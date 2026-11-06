using System.Text.Json;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

public class SubscriptionUpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_Do_Not_Enable_Background_Downloads()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.False(settings.SubscriptionAutoUpdate);
        Assert.Equal(24, settings.SubscriptionUpdateIntervalHours);
        Assert.False(SubscriptionUpdateSchedule.IsDue(settings, new SubscriptionEntry(), Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    public void Updates_Are_Due_At_The_Selected_Interval(int hours)
    {
        var settings = new AppSettings { SubscriptionAutoUpdate = true, SubscriptionUpdateIntervalHours = hours };
        var entry = new SubscriptionEntry { LastUpdated = Now.AddHours(-hours) };
        Assert.False(SubscriptionUpdateSchedule.IsDue(settings, entry, Now.AddTicks(-1)));
        Assert.True(SubscriptionUpdateSchedule.IsDue(settings, entry, Now));
    }

    [Fact]
    public void Never_Updated_Subscription_Is_Due_Immediately()
    {
        var settings = new AppSettings { SubscriptionAutoUpdate = true };
        Assert.True(SubscriptionUpdateSchedule.IsDue(settings, new SubscriptionEntry(), Now));
        Assert.DoesNotContain(settings.Subscriptions, s => SubscriptionUpdateSchedule.IsDue(settings, s, Now));
    }

    [Fact]
    public void Failed_Attempt_Delays_Retry_Without_Changing_Last_Success()
    {
        var entry = new SubscriptionEntry
        {
            LastUpdated = Now.AddDays(-3), LastUpdateAttempt = Now, LastError = "timeout"
        };
        Assert.Equal(Now.AddHours(6), SubscriptionUpdateSchedule.NextAttemptAt(entry, 6, Now));
        Assert.Equal(Now.AddDays(-3), entry.LastUpdated);
    }

    [Fact]
    public void A_More_Recent_Manual_Update_Resets_The_Schedule()
    {
        var entry = new SubscriptionEntry { LastUpdateAttempt = Now.AddHours(-5), LastUpdated = Now };
        Assert.Equal(Now.AddHours(6), SubscriptionUpdateSchedule.NextAttemptAt(entry, 6, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Invalid_Intervals_Fall_Back_To_One_Day(int hours)
    {
        Assert.Equal(24, SubscriptionUpdateSchedule.NormalizeInterval(hours));
        Assert.Equal(Now.AddDays(1), SubscriptionUpdateSchedule.NextAttemptAt(
            new SubscriptionEntry { LastUpdated = Now }, hours, Now));
    }

    [Fact]
    public void Clock_Rollback_Does_Not_Postpone_Updates_Indefinitely()
    {
        var entry = new SubscriptionEntry { LastUpdateAttempt = DateTimeOffset.MaxValue };
        Assert.Equal(Now, SubscriptionUpdateSchedule.NextAttemptAt(entry, 24, Now));
    }

    [Fact]
    public void Schedule_Survives_Settings_Roundtrip()
    {
        var options = new JsonSerializerOptions { Converters = { new SubscriptionEntryConverter() } };
        var settings = new AppSettings
        {
            SubscriptionAutoUpdate = true,
            SubscriptionUpdateIntervalHours = 6,
            Subscriptions = [new SubscriptionEntry { LastUpdateAttempt = Now, LastUpdated = Now.AddDays(-1) }]
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, options), options)!;
        Assert.True(restored.SubscriptionAutoUpdate);
        Assert.Equal(6, restored.SubscriptionUpdateIntervalHours);
        Assert.Equal(Now, restored.Subscriptions[0].LastUpdateAttempt);
        Assert.False(SubscriptionUpdateSchedule.IsDue(restored, restored.Subscriptions[0], Now));
        Assert.True(SubscriptionUpdateSchedule.IsDue(restored, restored.Subscriptions[0], Now.AddHours(6)));
    }

    [Fact]
    public void Legacy_Entry_Uses_Last_Success_Until_First_Attempt()
    {
        var options = new JsonSerializerOptions { Converters = { new SubscriptionEntryConverter() } };
        var entry = JsonSerializer.Deserialize<SubscriptionEntry>(
            """{"name":"legacy","lastUpdated":"2026-11-06T12:00:00Z"}""", options)!;
        Assert.Null(entry.LastUpdateAttempt);
        Assert.Equal(Now.AddDays(1), SubscriptionUpdateSchedule.NextAttemptAt(entry, 24, Now));
    }
}

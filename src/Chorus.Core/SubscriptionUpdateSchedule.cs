using Chorus.Core.Models;

namespace Chorus.Core;

public static class SubscriptionUpdateSchedule
{
    public static IReadOnlyList<int> Intervals { get; } = Array.AsReadOnly(new[] { 1, 6, 12, 24 });

    public static int NormalizeInterval(int hours) => Intervals.Contains(hours) ? hours : 24;

    public static DateTimeOffset NextAttemptAt(SubscriptionEntry entry, int hours, DateTimeOffset now)
    {
        var last = entry.LastUpdateAttempt;
        if (entry.LastUpdated is { } updated && (last is null || updated > last)) last = updated;
        if (last is null) return now;
        var interval = TimeSpan.FromHours(NormalizeInterval(hours));
        // 时钟回拨或手改时间不应让订阅永久停止更新。
        if (last > now) return now;
        return now - last.Value >= interval ? now : last.Value + interval;
    }

    public static bool IsDue(AppSettings settings, SubscriptionEntry entry, DateTimeOffset now) =>
        settings.SubscriptionAutoUpdate && NextAttemptAt(entry, settings.SubscriptionUpdateIntervalHours, now) <= now;
}

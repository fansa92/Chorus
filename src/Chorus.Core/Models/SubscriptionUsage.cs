using Chorus.Core.Utils;

namespace Chorus.Core.Models;

/// <summary>用量提醒的严重程度，决定卡片上这行字的颜色。</summary>
public enum SubscriptionUsageLevel
{
    /// <summary>正常，或服务端没给出可判断的信息。</summary>
    None,

    /// <summary>快到期 / 流量快用完，该提醒用户了。</summary>
    Warning,

    /// <summary>已到期或额度已用尽。</summary>
    Critical,
}

/// <summary>
/// 订阅服务端返回的用量信息，来自响应头 <c>Subscription-Userinfo</c>
/// （形如 <c>upload=0; download=107968; total=1073741824; expire=1780000000</c>）。
/// 服务端不给这些信息时为 null，界面上不显示这一行。
/// </summary>
public sealed class SubscriptionUsage
{
    /// <summary>流量剩余不足这个比例时提醒。</summary>
    public const double LowTrafficRatio = 0.1;

    /// <summary>距到期不足这个天数时提醒。</summary>
    public const int ExpiringSoonDays = 7;

    /// <summary>已上传字节数。</summary>
    public long? Upload { get; init; }

    /// <summary>已下载字节数。</summary>
    public long? Download { get; init; }

    /// <summary>套餐总流量字节数；0 或负数表示服务端未提供（视为不限/未知）。</summary>
    public long? Total { get; init; }

    /// <summary>到期时间；服务端没给或给 0（表示长期有效）时为 null。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>这份用量是哪次拉取时读到的，用于判断信息是否过期。</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>已用量：上行 + 下行；个别服务端只给其中一项。</summary>
    public long? UsedBytes
    {
        get
        {
            if (Upload is { } u && Download is { } d) return u + d;
            if (Upload is { } onlyUp) return onlyUp;
            if (Download is { } onlyDown) return onlyDown;
            return null;
        }
    }

    /// <summary>剩余流量；只有服务端同时给了总量和已用量才算得出来。</summary>
    public long? RemainingBytes =>
        Total is { } total && UsedBytes is { } used ? Math.Max(0, total - used) : null;

    /// <summary>已用比例（0~1），超出总量时按 1 计。</summary>
    public double? UsedRatio =>
        Total is { } total && UsedBytes is { } used && total > 0 ? Math.Min(1d, (double)used / total) : null;

    public static SubscriptionUsage? Parse(string? headerValue, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        long? upload = null, download = null, total = null;
        DateTimeOffset? expires = null;
        // 标准分隔符是分号，个别服务端用逗号；数值里不会出现逗号，所以两种都切开
        foreach (var pair in headerValue.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var key = pair[..idx].Trim().ToLowerInvariant();
            var number = ParseNumber(pair[(idx + 1)..]);
            switch (key)
            {
                case "upload": upload = number; break;
                case "download": download = number; break;
                case "total": total = number; break;
                case "expire": expires = FromUnix(number, fetchedAt); break;
            }
        }

        var usage = new SubscriptionUsage
        {
            // 已用量是计数器，0 就是「没用过」，得留着；只有总量和到期时间的 0 才表示「未提供」
            Upload = NonNegative(upload),
            Download = NonNegative(download),
            // total=0 是「未提供/不限」的约定写法，当成未知处理，否则界面会显示「剩余 0 B」
            Total = Positive(total),
            ExpiresAt = expires,
            FetchedAt = fetchedAt,
        };
        return usage.UsedBytes is null && usage.Total is null && usage.ExpiresAt is null ? null : usage;
    }

    /// <summary>卡片上的一行摘要，例如「剩余 12.30 GB / 20.00 GB（已用 38%）· 23 天后到期」。</summary>
    public string Describe(DateTimeOffset now)
    {
        var parts = new List<string>();
        if (RemainingBytes is { } remaining)
            parts.Add(UsedRatio is { } ratio
                ? $"剩余 {BytesHumanize.Format(remaining)} / {BytesHumanize.Format(Total!.Value)}（已用 {ratio:P0}）"
                : $"剩余 {BytesHumanize.Format(remaining)}");
        else if (UsedBytes is { } used)
            parts.Add($"已用 {BytesHumanize.Format(used)}");

        if (ExpiresAt is { } expires)
            parts.Add(IsExpired(now)
                ? $"已到期（{expires.LocalDateTime:yyyy-MM-dd}）"
                : $"{DaysUntilExpiry(now)} 天后到期（{expires.LocalDateTime:MM-dd}）");
        return string.Join(" · ", parts);
    }

    /// <summary>需要主动提醒的内容（额度紧张/到期）；一切正常时返回空串。</summary>
    public string AlertText(DateTimeOffset now)
    {
        var alerts = new List<string>();
        if (ExpiresAt is not null)
        {
            if (IsExpired(now)) alerts.Add("已到期");
            else if (DaysUntilExpiry(now) is { } days && days <= ExpiringSoonDays)
                alerts.Add($"将在 {days} 天后到期");
        }
        if (RemainingBytes is <= 0) alerts.Add("流量已用完");
        else if (RemainingBytes is { } r && Total is { } t && t > 0 && (double)r / t < LowTrafficRatio)
            alerts.Add($"剩余流量不足 {BytesHumanize.Format(r)}");
        return string.Join("，", alerts);
    }

    /// <summary>正常时返回 <see cref="SubscriptionUsageLevel.None"/>。</summary>
    public SubscriptionUsageLevel Level(DateTimeOffset now)
    {
        if (IsExpired(now) || RemainingBytes is <= 0) return SubscriptionUsageLevel.Critical;
        if (!string.IsNullOrEmpty(AlertText(now))) return SubscriptionUsageLevel.Warning;
        return SubscriptionUsageLevel.None;
    }

    /// <summary>服务端给了到期时间，且时间点已经过去。</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;

    /// <summary>还剩几天到期（向上取整，最少算 1 天）；没有到期时间或已到期时为 null。</summary>
    public int? DaysUntilExpiry(DateTimeOffset now) =>
        ExpiresAt is not { } expires || expires <= now
            ? null
            : (int)Math.Ceiling((expires - now).TotalDays);

    private static long? ParseNumber(string raw)
    {
        var text = raw.Trim().Trim('"', '\'');
        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>expire=0 表示长期有效；单位一般是秒，个别服务端给毫秒。</summary>
    private static DateTimeOffset? FromUnix(long? seconds, DateTimeOffset reference)
    {
        if (seconds is not { } value || value <= 0) return null;
        try
        {
            return value > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToOffset(reference.Offset)
                : DateTimeOffset.FromUnixTimeSeconds(value).ToOffset(reference.Offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long? Positive(long? value) => value is > 0 ? value : null;

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;

    /// <summary>用于 settings.json 读写。</summary>
    public bool HasAny => UsedBytes is not null || Total is not null || ExpiresAt is not null;
}

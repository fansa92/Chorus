using Chorus.Core.Utils;

namespace Chorus.Core.ClashApi;

/// <summary>连接列表的分类：按最终出站（Chain 的第 0 项）判断走没走代理。</summary>
public enum ConnectionKind
{
    All,
    Proxy,
    Direct,
    Blocked,
    Unknown,
}

/// <summary>连接列表的排序方式。</summary>
public enum ConnectionSort
{
    Recent,
    Download,
    Upload,
    Process,
}

/// <summary>连接列表右上角的汇总数字。</summary>
public sealed record ConnectionSummary(int Count, long Upload, long Download, string TopProcess)
{
    public string UploadText => BytesHumanize.Format(Upload);
    public string DownloadText => BytesHumanize.Format(Download);
}

/// <summary>
/// 连接管理页的筛选 / 排序 / 汇总。放成纯函数是为了能在 Core 里单测，
/// 不依赖 WinUI 的集合与 UI 线程。
/// </summary>
public static class ConnectionQuery
{
    /// <summary>Chain 的第 0 项是最终出站：DIRECT 是直连，REJECT/BLOCK 是阻断，其余算代理。</summary>
    public static ConnectionKind KindOf(ConnectionInfo c)
    {
        if (c.Chain.Count == 0) return ConnectionKind.Unknown;
        var outbound = c.Chain[0];
        if (outbound.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
            outbound.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase) ||
            outbound.Equals("BLOCK", StringComparison.OrdinalIgnoreCase))
            return ConnectionKind.Blocked;
        if (outbound.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
            return ConnectionKind.Direct;
        return ConnectionKind.Proxy;
    }

    /// <summary>搜索目标 / IP / 端口 / 进程 / 规则 / 链路，大小写不敏感。</summary>
    public static bool Matches(ConnectionInfo c, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return Contains(c.Host, q)
            || Contains(c.DestinationIp, q)
            || Contains(c.DestinationPort, q)
            || Contains(c.SourceIp, q)
            || Contains(c.Process, q)
            || Contains(c.ProcessName, q)
            || Contains(c.Rule, q)
            || Contains(c.RulePayload, q)
            || Contains(c.Network, q)
            || c.Chain.Any(hop => Contains(hop, q));
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Length > 0 && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 依次套用搜索词、分类、网络协议过滤，再排序。
    /// <paramref name="network"/> 传 "tcp"/"udp" 时只保留该协议，其他值（含 null）不过滤。
    /// </summary>
    public static List<ConnectionInfo> Apply(
        IEnumerable<ConnectionInfo> connections,
        string? query,
        ConnectionKind kind = ConnectionKind.All,
        string? network = null,
        ConnectionSort sort = ConnectionSort.Recent)
    {
        var filtered = connections.Where(c => Matches(c, query));
        if (kind != ConnectionKind.All)
            filtered = filtered.Where(c => KindOf(c) == kind);
        if (network is "tcp" or "udp")
            filtered = filtered.Where(c => c.Network.Equals(network, StringComparison.OrdinalIgnoreCase));

        return sort switch
        {
            ConnectionSort.Download => filtered.OrderByDescending(c => c.Download).ThenBy(c => c.Id, StringComparer.Ordinal).ToList(),
            ConnectionSort.Upload => filtered.OrderByDescending(c => c.Upload).ThenBy(c => c.Id, StringComparer.Ordinal).ToList(),
            ConnectionSort.Process => filtered.OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase).ThenByDescending(c => c.Download).ToList(),
            _ => filtered
                // 没有开始时间的排最后，其余按建立时间倒序（最新的在最上面）
                .OrderBy(c => c.Start.HasValue ? 0 : 1)
                .ThenByDescending(c => c.Start ?? DateTimeOffset.MinValue)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public static ConnectionSummary Summarize(IEnumerable<ConnectionInfo> connections)
    {
        var list = connections as IReadOnlyCollection<ConnectionInfo> ?? connections.ToList();
        var top = list
            .Where(c => c.ProcessName.Length > 0)
            .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";
        return new ConnectionSummary(list.Count, list.Sum(c => c.Upload), list.Sum(c => c.Download), top);
    }

    /// <summary>连接已建立的时长，核心没给 start 时返回空串。</summary>
    public static string FormatDuration(DateTimeOffset? start, DateTimeOffset now)
    {
        if (start is null) return "";
        var span = now - start.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }
}

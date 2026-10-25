using System.Diagnostics;
using Chorus.Core.Utils;

namespace Chorus.Core.Net;

/// <summary>一次网络检测的结果：延迟（毫秒，测不到为 null）+ 出口 IP 归属（查不到为 null）。</summary>
public sealed record ProbeResult(int? LatencyMs, IpInfo? Info);

/// <summary>
/// 「网络检测」的实际动作：量一次 HTTP 往返延迟 + 查出口 IP / 地区 / 运营商。
/// 放在 Core 里是为了能用假的 HttpClient 单测（App 只负责决定走不走本地代理端口）。
/// </summary>
public sealed class NetworkProbe
{
    /// <summary>测延迟用的 204 接口。</summary>
    public const string LatencyUrl = "https://www.gstatic.com/generate_204";

    /// <summary>
    /// IP 归属接口，按顺序尝试：ip-api.com 中文友好、国内可达性好，ipwho.is 作兜底。
    /// 每个接口单独计时，前一个超时不影响后一个。
    /// </summary>
    private static readonly (string Url, Func<string, IpInfo?> Parse)[] IpEndpoints =
    [
        ("http://ip-api.com/json/?lang=zh-CN&fields=status,country,regionName,city,isp,query",
            IpInfoParser.ParseIpApi),
        ("https://ipwho.is/?lang=zh-CN", IpInfoParser.ParseIpWhoIs),
    ];

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public NetworkProbe(HttpClient http, TimeSpan? timeout = null)
    {
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
    }

    /// <summary>延迟与 IP 归属并行测。任何一项失败都是 null，不会让另一项跟着失败。</summary>
    public async Task<ProbeResult> RunAsync(CancellationToken ct = default)
    {
        var latencyTask = MeasureLatencyAsync(ct);
        var infoTask = LookupIpAsync(ct);
        return new ProbeResult(await latencyTask, await infoTask);
    }

    private async Task<int?> MeasureLatencyAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            using var resp = await _http.GetAsync(LatencyUrl, cts.Token);
            watch.Stop();
            return resp.IsSuccessStatusCode ? (int)watch.ElapsedMilliseconds : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>依次问每个接口，拿到第一条能解析出 IP 的结果就返回。</summary>
    public async Task<IpInfo?> LookupIpAsync(CancellationToken ct = default)
    {
        foreach (var (url, parse) in IpEndpoints)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                var json = await _http.GetStringAsync(url, cts.Token);
                if (parse(json) is { } info) return info;
            }
            catch
            {
                // 换下一个接口
            }
        }
        return null;
    }
}

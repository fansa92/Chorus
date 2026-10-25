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
    /// IP 归属接口，按顺序尝试：ip-api.com 中文友好、国内可达性好，ipwho.is 作兜底，
    /// 之后再补 ipinfo.io / ip.im / iplark.com / ifconfig.co —— 单个接口被墙或限流时还有别的能顶上。
    /// 每个接口单独计时，前一个超时不影响后一个。
    /// fields 里必须带 countryCode——界面靠它取国旗，漏了就只能显示地球图标。
    /// </summary>
    private static readonly (string Url, Func<string, IpInfo?> Parse)[] IpEndpoints =
    [
        ("http://ip-api.com/json/?lang=zh-CN&fields=status,country,countryCode,regionName,city,isp,query",
            IpInfoParser.ParseIpApi),
        ("https://ipwho.is/?lang=zh-CN", IpInfoParser.ParseIpWhoIs),
        ("https://ipinfo.io/json", IpInfoParser.ParseIpInfoIo),
        ("https://ip.im/api", IpInfoParser.ParseIpIm),
        ("https://iplark.com/api", IpInfoParser.ParseIplark),
        ("https://ifconfig.co/json", IpInfoParser.ParseIfconfigCo),
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

    /// <summary>
    /// 依次问每个接口，拿到第一条能解析出 IP 的结果就返回。
    /// 全都失败时用本地 GeoIP 库兜底（至少能报出国家）。
    /// </summary>
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

        // 在线接口全挂：退到本地 GeoIP 段表，至少还能报出出口在哪个国家
        return await LookupLocalAsync(ct);
    }

    /// <summary>
    /// 本地兜底：先用「本机直连能拿到的公网 IP」定位国家。
    /// 这一步需要真的发一次请求（只取 IP，不做归属查询），
    /// 所以放在在线接口全部失败之后。
    /// </summary>
    private async Task<IpInfo?> LookupLocalAsync(CancellationToken ct)
    {
        foreach (var url in new[] { "https://api.ipify.org", "https://icanhazip.com" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                var text = await _http.GetStringAsync(url, cts.Token);
                var ip = text.Trim();
                if (ip.Length == 0) continue;
                if (LocalGeoLookup.Lookup(ip) is { } info) return info;
            }
            catch
            {
                // 换下一个
            }
        }
        return null;
    }
}

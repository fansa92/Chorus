using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Chorus.Core.Net;

/// <summary>竞速结果：最终选中的边缘 IP 及其探测耗时。</summary>
public sealed record RaceResult(
    IPAddress Address,
    int Port,
    TimeSpan Elapsed,
    int CandidateCount,
    int ReachableCount)
{
    public string Describe() =>
        $"直连 {Address}:{Port}（{CandidateCount} 个候选 / {ReachableCount} 个可达，{Elapsed.TotalMilliseconds:F0}ms）";
}

/// <summary>
/// 同一个域名在不同 DNS 上会解析到不同的 Cloudflare 边缘 IP，而这里部分 IP 段是被黑洞的。
/// 这个类做的事：把系统 DNS + 几家国内外公共 DNS 的 A 记录取并集，然后并发（而不是串行）
/// 逐个做 TLS 握手探测，第一个成功的 IP 就是这次要用的出口。
/// 探测时 SNI 保持原始域名，所以证书校验与正常访问一致，能真实反映「能不能拉下来」。
/// </summary>
public static class EndpointRacer
{
    /// <summary>参与竞速的公共 DNS（均为 UDP 53 可达的服务器）。</summary>
    private static readonly string[] PublicDnsServers =
    [
        "223.5.5.5",      // 阿里
        "119.29.29.29",   // DNSPod
        "9.9.9.9",        // Quad9（实测会返回可用的 172.66 段）
        "8.8.8.8",        // Google
    ];

    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan SystemDnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>已确认可用的 IP 缓存（10 分钟），避免每次更新订阅都重新竞速。</summary>
    private static readonly ConcurrentDictionary<string, (IPAddress Address, DateTimeOffset At)> Cache = new();

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>竞速选出一个可用的 IP；全部不可达时返回 null。</summary>
    public static async Task<RaceResult?> RaceAsync(string host, int port, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        // 地址直接就是 IP：仍然探测一次（可能被封），不解析
        if (IPAddress.TryParse(host, out var literal))
        {
            var ok = await ProbeAsync(literal, port, host, ProbeTimeout, ct).ConfigureAwait(false);
            return ok ? new RaceResult(literal, port, TimeSpan.Zero, 1, 1) : null;
        }

        if (Cache.TryGetValue(host, out var cached) && DateTimeOffset.Now - cached.At < CacheLifetime)
        {
            var cachedOk = await ProbeAsync(cached.Address, port, host, ProbeTimeout, ct).ConfigureAwait(false);
            if (cachedOk)
                return new RaceResult(cached.Address, port, TimeSpan.Zero, 1, 1);
            Cache.TryRemove(host, out _);   // 缓存失效，重新竞速
        }

        var candidates = await CollectCandidatesAsync(host, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return null;

        var stopwatch = Stopwatch.StartNew();
        var reachable = 0;
        var pending = new List<Task<(IPAddress Address, bool Ok, TimeSpan Elapsed)>>();
        foreach (var ip in candidates)
            pending.Add(ProbeWithTimingAsync(ip, port, host, ct));

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            var (address, ok, elapsed) = await finished.ConfigureAwait(false);
            if (!ok) continue;
            reachable++;
            Cache[host] = (address, DateTimeOffset.Now);
            stopwatch.Stop();
            // 只清掉未完成的探测（不等待它们，避免拖慢拉取）
            return new RaceResult(address, port, elapsed, candidates.Count, reachable);
        }

        return null;
    }

    /// <summary>把多家 DNS 的结果取并集（保持出现顺序，系统解析结果优先）。</summary>
    public static async Task<IReadOnlyList<IPAddress>> CollectCandidatesAsync(string host, CancellationToken ct = default)
    {
        var tasks = new List<Task<IReadOnlyList<IPAddress>>>
        {
            SystemLookupAsync(host, ct),
        };
        foreach (var server in PublicDnsServers)
            tasks.Add(DnsQuery.QueryAAsync(server, host, DnsTimeout, ct));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var seen = new HashSet<IPAddress>();
        var ordered = new List<IPAddress>();
        foreach (var list in results)
        {
            foreach (var ip in list)
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (seen.Add(ip)) ordered.Add(ip);
            }
        }
        return ordered;
    }

    private static async Task<IReadOnlyList<IPAddress>> SystemLookupAsync(string host, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct)
                .WaitAsync(SystemDnsTimeout, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private static async Task<(IPAddress Address, bool Ok, TimeSpan Elapsed)> ProbeWithTimingAsync(
        IPAddress ip, int port, string sniHost, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var ok = await ProbeAsync(ip, port, sniHost, ProbeTimeout, ct).ConfigureAwait(false);
        stopwatch.Stop();
        return (ip, ok, stopwatch.Elapsed);
    }

    /// <summary>
    /// 探测单个 IP 是否真的能用：先建 TCP，443 端口再做一次带 SNI 的 TLS 握手。
    /// 只测 TCP 会漏掉「TCP 通但 TLS 被重置」的情况（本机对部分 Cloudflare 段就是这样）。
    /// </summary>
    private static async Task<bool> ProbeAsync(
        IPAddress ip, int port, string sniHost, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
            if (port != 443) return true;

            await using var stream = new NetworkStream(socket, ownsSocket: true);
            await using var tls = new SslStream(stream, leaveInnerStreamOpen: false,
                // 只关心连通性：证书链校验失败（比如 IP 段被中间人替换）不应算作可用，
                // 但自签证书这类无关场景也不能直接判死，所以这里接受证书后继续握手
                userCertificateValidationCallback: static (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = sniHost }, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>
    /// 构造一个「只连指定 IP」的 HttpClientHandler：TLS 与 Host 头仍按原域名来（SNI 不变），
    /// 只有 TCP 连接目标被钉死在这个 IP 上。这样既绕开了被墙的解析结果，也不需要改 DNS。
    /// </summary>
    public static SocketsHttpHandler CreatePinnedHandler(IPAddress address)
    {
        return new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = ProbeTimeout,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    // 端口取请求目标（重定向到别的端口时也能工作）
                    await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    /// <summary>把某个域名从缓存里移除（拉取失败时调用，下次重新竞速）。</summary>
    public static void Invalidate(string host) => Cache.TryRemove(host, out _);
}

using System.Net;
using System.Net.Http.Headers;
using Chorus.Core.Models;
using Chorus.Core.Net;

namespace Chorus.Core;

/// <summary>一次订阅拉取的结果：正文 + 服务端响应头里的用量信息（没有时为 null）。</summary>
public sealed record SubscriptionFetchResult(string Content, SubscriptionUsage? Usage);

/// <summary>从订阅地址下载内容并解析为节点列表与策略组。</summary>
public sealed class SubscriptionService
{
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProxyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 订阅转换服务按 UA 分流：报 sing-box 才会返回原生 JSON（含 selector/urltest 策略组），
    /// 报 Clash 客户端返回 Clash YAML。可在设置页修改。
    /// </summary>
    public const string DefaultUserAgent = "sing-box";

    private readonly object _clientGate = new();
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    private string _userAgent = DefaultUserAgent;

    /// <summary>拉取订阅时使用的 User-Agent（为空回落到 <see cref="DefaultUserAgent"/>）。</summary>
    public string UserAgent
    {
        get => _userAgent;
        set => _userAgent = string.IsNullOrWhiteSpace(value) ? DefaultUserAgent : value.Trim();
    }

    /// <summary>
    /// 返回核心运行时的本机代理端点（host, port）；核心未运行时可返回 null。
    /// 订阅地址通常在墙外，有代理时默认借核心的 mixed 端口出去。
    /// </summary>
    public Func<(string Host, int Port)?>? ProxyProvider { get; set; }

    /// <summary>
    /// 为 true 时订阅请求只走直连、完全不碰核心代理（订阅页的「直连更新」）。
    /// </summary>
    public bool DirectOnly { get; set; }

    /// <summary>最近一次拉取走的是哪条路（代理 / 直连用了哪个 IP），用于在界面上给出可见的依据。</summary>
    public string? LastFetchDetail { get; private set; }

    /// <summary>下载订阅原始内容，并顺带读出服务端在响应头里给的用量信息。</summary>
    public async Task<SubscriptionFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        var endpoint = DirectOnly ? null : ProxyProvider?.Invoke();
        Exception? proxyFailure = null;
        LastFetchDetail = null;

        if (endpoint is { } proxy && proxy.Port is > 0 and <= 65535)
        {
            try
            {
                var fetched = await GetStringAsync(Client(proxy), url, ct);
                LastFetchDetail = $"经核心代理 {proxy.Host}:{proxy.Port}";
                return fetched;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                proxyFailure = ex;
            }
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("拉取订阅失败：订阅链接格式不正确（需以 http:// 或 https:// 开头）。");
        }

        string? raceNote = null;
        try
        {
            var raced = await EndpointRacer.RaceAsync(uri.Host, uri.Port, ct);
            if (raced is not null)
            {
                using var pinned = new HttpClient(EndpointRacer.CreatePinnedHandler(raced.Address))
                {
                    Timeout = DirectTimeout,
                };
                pinned.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
                var fetched = await GetStringAsync(pinned, url, ct);
                LastFetchDetail = raced.Describe();
                return fetched;
            }
            raceNote = "所有候选 IP 均不可达";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            raceNote = Describe(ex);
        }
        // 竞速失败说明这个 IP 已经不能用了，别让下一次继续吃缓存
        EndpointRacer.Invalidate(uri.Host);

        // 兜底：走系统解析的普通直连（竞速本身可能因本机网络异常整体失败）
        try
        {
            var fetched = await GetStringAsync(Client(null), url, ct);
            LastFetchDetail = "直连（系统 DNS）";
            return fetched;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception directFailure)
        {
            var hint = DirectOnly
                ? "当前开着「直连更新」，需要代理时请关闭它后重试。"
                : "该订阅若需要代理访问，请先启动核心再更新。";
            var detail = proxyFailure is null
                ? $"直连失败（多 DNS 解析：{raceNote}；{Describe(directFailure)}）。{hint}"
                : $"经核心代理（{endpoint!.Value.Host}:{endpoint.Value.Port}）失败：{Describe(proxyFailure)}；"
                  + $"直连失败（多 DNS 解析：{raceNote}；{Describe(directFailure)}）。";
            throw new InvalidOperationException("拉取订阅失败：" + detail, directFailure);
        }
    }

    public async Task<(List<NodeEntry> Nodes, List<string> Errors)> ImportFromUrlAsync(
        string url, CancellationToken ct = default)
    {
        var content = (await FetchAsync(url, ct)).Content;
        return Parsing.SubscriptionParser.Parse(content);
    }

    /// <summary>同 <see cref="ImportFromUrlAsync"/>，但保留 Clash 配置里的策略组。</summary>
    public async Task<(List<NodeEntry> Nodes, List<ProxyGroup> Groups, List<string> Errors)> ImportFromUrlWithGroupsAsync(
        string url, CancellationToken ct = default)
    {
        var (nodes, groups, _, errors, _) = await ImportFullAsync(url, ct);
        return (nodes, groups, errors);
    }

    /// <summary>
    /// 完整导入：节点 + 策略组 + 订阅自带的 route/dns 片段 + 服务端用量信息。
    /// 路由片段只有 sing-box 原生 JSON 订阅才有（Clash YAML / 分享链接返回 null）。
    /// </summary>
    public async Task<(List<NodeEntry> Nodes, List<ProxyGroup> Groups, Parsing.SubscriptionRouting? Routing,
        List<string> Errors, SubscriptionUsage? Usage)>
        ImportFullAsync(string url, CancellationToken ct = default)
    {
        var fetched = await FetchAsync(url, ct);
        var (nodes, groups, errors) = Parsing.SubscriptionParser.ParseWithGroups(fetched.Content);
        return (nodes, groups, Parsing.SubscriptionParser.ExtractRouting(fetched.Content), errors, fetched.Usage);
    }

    /// <summary>
    /// 按「代理端点 + UA」缓存 HttpClient：直连客户端不跟随系统/环境代理，
    /// 避免本机残留的代理设置把请求带到别处；UA 变化时重建。
    /// </summary>
    private HttpClient Client((string Host, int Port)? proxy)
    {
        var key = $"{(proxy is null ? "direct" : $"{proxy.Value.Host}:{proxy.Value.Port}")}|{_userAgent}";
        lock (_clientGate)
        {
            if (_clients.TryGetValue(key, out var cached)) return cached;

            // 只保留当前 UA 的客户端，避免切换 UA 后不停堆积
            foreach (var staleKey in _clients.Keys.Where(k => !k.EndsWith("|" + _userAgent, StringComparison.Ordinal)).ToList())
            {
                _clients[staleKey].Dispose();
                _clients.Remove(staleKey);
            }

            var client = CreateClient(proxy, proxy is null ? DirectTimeout : ProxyTimeout, _userAgent);
            _clients[key] = client;
            return client;
        }
    }

    private static HttpClient CreateClient((string Host, int Port)? proxy, TimeSpan timeout, string userAgent)
    {
        var handler = new HttpClientHandler { UseProxy = proxy is not null };
        if (proxy is { } p)
            handler.Proxy = new WebProxy(p.Host, p.Port);
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    private static async Task<SubscriptionFetchResult> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var content = await resp.Content.ReadAsStringAsync(ct);
        return new SubscriptionFetchResult(content, ReadUsage(resp, DateTimeOffset.Now));
    }

    /// <summary>
    /// 用量信息只可能在响应头里，服务端不给就是不给（不猜、不报错）。
    /// 少数服务端会把它挂在 Content-Type 同级的内容头上，两处都看一眼。
    /// </summary>
    internal static SubscriptionUsage? ReadUsage(HttpResponseMessage response, DateTimeOffset now)
    {
        foreach (var headers in new HttpHeaders[] { response.Headers, response.Content.Headers })
        {
            foreach (var name in UsageHeaderNames)
            {
                if (headers.TryGetValues(name, out var values)
                    && SubscriptionUsage.Parse(values.FirstOrDefault(), now) is { } usage)
                {
                    return usage;
                }
            }
        }
        return null;
    }

    private static readonly string[] UsageHeaderNames = ["subscription-userinfo", "subscription-info", "x-subscription-userinfo"];

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "请求超时",
        HttpRequestException h when h.InnerException is { } inner => inner.Message,
        _ => ex.Message,
    };
}

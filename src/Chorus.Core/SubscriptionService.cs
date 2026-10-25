using System.Net;
using Chorus.Core.Models;

namespace Chorus.Core;

/// <summary>从订阅地址下载内容并解析为节点列表与策略组。</summary>
public sealed class SubscriptionService
{
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProxyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>订阅转换服务按 UA 分流：报 Clash 客户端才会返回带 proxy-groups 的完整配置。</summary>
    private const string UserAgent = "clash-verge/v2.0.0";

    // 直连客户端：不跟随系统/环境代理，避免本机残留的代理配置把请求带到别处
    private static readonly HttpClient DirectHttp = CreateClient(null, DirectTimeout);

    private static readonly object ProxyGate = new();
    private HttpClient? _proxyHttp;
    private string? _proxyKey;

    /// <summary>
    /// 返回核心运行时的本机代理端点（host, port）；核心未运行时可返回 null。
    /// 订阅地址通常在墙外，直连必然超时，必须借核心的 mixed 端口出去。
    /// </summary>
    public Func<(string Host, int Port)?>? ProxyProvider { get; set; }

    /// <summary>下载订阅原始内容。优先经核心代理，失败再回退直连。</summary>
    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        var endpoint = ProxyProvider?.Invoke();
        Exception? proxyFailure = null;

        if (endpoint is { } proxy && proxy.Port is > 0 and <= 65535)
        {
            try
            {
                return await GetStringAsync(ProxyClient(proxy.Host, proxy.Port), url, ct);
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

        try
        {
            return await GetStringAsync(DirectHttp, url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception directFailure)
        {
            var detail = proxyFailure is null
                ? $"{Describe(directFailure)}。该订阅若需要代理访问，请先启动核心再更新。"
                : $"经核心代理（{endpoint!.Value.Host}:{endpoint.Value.Port}）失败：{Describe(proxyFailure)}；"
                  + $"直连失败：{Describe(directFailure)}。";
            throw new InvalidOperationException("拉取订阅失败：" + detail, directFailure);
        }
    }

    public async Task<(List<NodeEntry> Nodes, List<string> Errors)> ImportFromUrlAsync(
        string url, CancellationToken ct = default)
    {
        var content = await FetchAsync(url, ct);
        return Parsing.SubscriptionParser.Parse(content);
    }

    /// <summary>同 <see cref="ImportFromUrlAsync"/>，但保留 Clash 配置里的策略组。</summary>
    public async Task<(List<NodeEntry> Nodes, List<ProxyGroup> Groups, List<string> Errors)> ImportFromUrlWithGroupsAsync(
        string url, CancellationToken ct = default)
    {
        var content = await FetchAsync(url, ct);
        return Parsing.SubscriptionParser.ParseWithGroups(content);
    }

    private static HttpClient CreateClient((string Host, int Port)? proxy, TimeSpan timeout)
    {
        var handler = new HttpClientHandler { UseProxy = proxy is not null };
        if (proxy is { } p)
            handler.Proxy = new WebProxy(p.Host, p.Port);
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>代理 HttpClient 按端口缓存，端口变化时重建。</summary>
    private HttpClient ProxyClient(string host, int port)
    {
        var key = $"{host}:{port}";
        lock (ProxyGate)
        {
            if (_proxyHttp is not null && _proxyKey == key) return _proxyHttp;
            _proxyHttp?.Dispose();
            _proxyHttp = CreateClient((host, port), ProxyTimeout);
            _proxyKey = key;
            return _proxyHttp;
        }
    }

    private static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "请求超时",
        HttpRequestException h when h.InnerException is { } inner => inner.Message,
        _ => ex.Message,
    };
}

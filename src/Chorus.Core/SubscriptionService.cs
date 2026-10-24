using Chorus.Core.Models;

namespace Chorus.Core;

/// <summary>从订阅地址下载内容并解析为节点列表。</summary>
public sealed class SubscriptionService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public async Task<string> FetchAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("chorus/1.0");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<(List<NodeEntry> Nodes, List<string> Errors)> ImportFromUrlAsync(
        string url, CancellationToken ct = default)
    {
        var content = await FetchAsync(url, ct);
        return Parsing.SubscriptionParser.Parse(content);
    }

    /// <summary>同 <see cref="ImportFromUrlAsync"/>，但保留 sing-box JSON 订阅里的策略组。</summary>
    public async Task<(List<NodeEntry> Nodes, List<ProxyGroup> Groups, List<string> Errors)> ImportFromUrlWithGroupsAsync(
        string url, CancellationToken ct = default)
    {
        var content = await FetchAsync(url, ct);
        return Parsing.SubscriptionParser.ParseWithGroups(content);
    }
}

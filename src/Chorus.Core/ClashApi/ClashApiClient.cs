using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chorus.Core.ClashApi;

/// <summary>sing-box 内置 Clash API 的客户端：节点/策略组查询与切换、延迟测速、实时流量。</summary>
public sealed class ClashApiClient
{
    private readonly HttpClient _http;
    private string _baseUrl = "http://127.0.0.1:9090";
    private string _secret = "";

    public ClashApiClient() : this(new HttpClientHandler { UseProxy = false }) { }

    public ClashApiClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void Configure(string host, int port, string? secret)
    {
        _baseUrl = $"http://{host}:{port}";
        _secret = secret ?? "";
    }

    public async Task<Dictionary<string, ProxyInfo>> GetProxiesAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "/proxies");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ClashApiModels.ParseProxies(json);
    }

    /// <summary>在策略组中选择成员（成员可以是节点，也可以是嵌套的策略组）。</summary>
    public async Task SelectAsync(string group, string name, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, "/proxies/" + Uri.EscapeDataString(group));
        req.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = name }),
            Encoding.UTF8,
            "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"切换节点失败（{(int)resp.StatusCode}）：{body.Trim()}");
        }
    }

    /// <summary>读取当前出站模式（rule / global / direct，大小写不敏感）。</summary>
    public async Task<string?> GetModeAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "/configs");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (JsonNode.Parse(json) is JsonObject obj && obj["mode"] is JsonValue mode && mode.TryGetValue<string>(out var m))
            return m.ToLowerInvariant();
        return null;
    }

    /// <summary>切换出站模式：rule（规则）/ global（全局代理）/ direct（全局直连）。</summary>
    public async Task SetModeAsync(string mode, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Patch, "/configs");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["mode"] = mode }),
            Encoding.UTF8,
            "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"切换模式失败（{(int)resp.StatusCode}）：{body.Trim()}");
        }
    }

    /// <summary>连接摘要：活动连接数 + 累计上传/下载字节。</summary>
    public async Task<(int Count, long Up, long Down)> GetConnectionsSummaryAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "/connections");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (JsonNode.Parse(json) is JsonObject obj)
        {
            var count = obj["connections"] is JsonArray conns ? conns.Count : 0;
            return (count, ClashApiModels.GetLong(obj, "uploadTotal"), ClashApiModels.GetLong(obj, "downloadTotal"));
        }
        return (0, 0, 0);
    }

    /// <summary>
    /// 活动连接明细（含每条连接走的链路：策略组 → 节点，以及命中的路由规则）。
    /// sing-box 只列**当前还活着**的连接，连接一结束就从这里消失，所以要和日志里的出站 tag 配合兜底。
    /// </summary>
    public async Task<List<ConnectionInfo>> GetConnectionsAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "/connections");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ClashApiModels.ParseConnections(json);
    }

    /// <summary>断开全部活动连接。</summary>
    public async Task CloseConnectionsAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, "/connections");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<int?> GetDelayAsync(string name, CancellationToken ct = default)
    {
        var path = "/proxies/" + Uri.EscapeDataString(name) +
                   "/delay?timeout=5000&url=" + Uri.EscapeDataString("https://www.gstatic.com/generate_204");
        using var req = CreateRequest(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return null;
        return JsonNode.Parse(json)?["delay"] is JsonValue value && value.TryGetValue<int>(out var delay)
            ? delay
            : null;
    }

    /// <summary>实时流量（字节/秒），通过 Clash API 的 WebSocket 端点推送。</summary>
    public async IAsyncEnumerable<TrafficSample> StreamTrafficAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket
        {
            Options = { KeepAliveInterval = TimeSpan.FromSeconds(30), Proxy = null },
        };
        if (!string.IsNullOrEmpty(_secret))
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _secret);
        var uri = new Uri(_baseUrl.Replace("http://", "ws://") + "/traffic");
        await ws.ConnectAsync(uri, ct);

        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(ws, buffer, ct);
            if (message is null) break;

            TrafficSample? sample = null;
            try
            {
                if (JsonNode.Parse(message) is JsonObject obj)
                    sample = new TrafficSample(ClashApiModels.GetLong(obj, "up"), ClashApiModels.GetLong(obj, "down"));
            }
            catch
            {
                // 单帧解析失败直接跳过
            }
            if (sample is not null) yield return sample;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery)
    {
        var req = new HttpRequestMessage(method, _baseUrl + pathAndQuery);
        if (!string.IsNullOrEmpty(_secret))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        return req;
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch { /* 对端已关闭 */ }
                return null;
            }
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}

public sealed class ProxyInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Now { get; set; }
    public List<string> All { get; set; } = [];
    public int? LastDelay { get; set; }
}

public sealed record TrafficSample(long Up, long Down);

/// <summary>
/// 一条活动连接。Chain 按 sing-box 返回的顺序是「最终出站 → … → 顶层策略组」，
/// 显示时要反过来读成「策略组 → 节点」；Rule 是命中的路由规则（没有规则时为 final）。
/// </summary>
public sealed class ConnectionInfo
{
    public string Host { get; set; } = "";
    public string DestinationIp { get; set; } = "";
    public string DestinationPort { get; set; } = "";
    public string Network { get; set; } = "";
    public string Inbound { get; set; } = "";
    public string Process { get; set; } = "";
    public string Rule { get; set; } = "";
    public List<string> Chain { get; set; } = [];
    public long Upload { get; set; }
    public long Download { get; set; }

    /// <summary>日志里可能写成 host:port，也可能写成 ip:port，两个键都能匹配上这条连接。</summary>
    public IEnumerable<string> Targets
    {
        get
        {
            if (Host.Length > 0 && DestinationPort.Length > 0) yield return $"{Host}:{DestinationPort}";
            if (DestinationIp.Length > 0 && DestinationPort.Length > 0) yield return $"{DestinationIp}:{DestinationPort}";
        }
    }

    /// <summary>「策略组 → 节点」；只有一跳时就是那个出站本身（如 DIRECT）。</summary>
    public string ChainText => Chain.Count switch
    {
        0 => "",
        1 => Chain[0],
        _ => string.Join(" → ", Chain.AsEnumerable().Reverse()),
    };
}

/// <summary>日志来源：核心进程输出 / 应用自身（启动、系统代理、订阅等）。</summary>
public enum LogSource
{
    Core,
    App,
}

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message, LogSource Source = LogSource.Core);

public static class ClashApiModels
{
    public static Dictionary<string, ProxyInfo> ParseProxies(string json)
    {
        var result = new Dictionary<string, ProxyInfo>(StringComparer.Ordinal);
        if (JsonNode.Parse(json) is not JsonObject root || root["proxies"] is not JsonObject proxies)
            return result;

        foreach (var (name, node) in proxies)
        {
            if (node is not JsonObject obj) continue;
            var info = new ProxyInfo
            {
                Name = name,
                Type = obj["type"] is JsonValue type && type.TryGetValue<string>(out var t) ? t : "",
                Now = obj["now"] is JsonValue now && now.TryGetValue<string>(out var n) ? n : null,
            };
            if (obj["all"] is JsonArray all)
                info.All = all.Select(x => x?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();
            if (obj["history"] is JsonArray history && history.Count > 0
                && history[history.Count - 1]?["delay"] is JsonValue delayValue && delayValue.TryGetValue<int>(out var delay))
                info.LastDelay = delay;
            result[name] = info;
        }
        return result;
    }

    /// <summary>解析 /connections 的返回：sing-box 形如 <c>{"connections":[{metadata:{...},chains:[...],rule:...}]}</c>。</summary>
    public static List<ConnectionInfo> ParseConnections(string json)
    {
        var result = new List<ConnectionInfo>();
        if (JsonNode.Parse(json) is not JsonObject root || root["connections"] is not JsonArray array)
            return result;

        foreach (var node in array)
        {
            if (node is not JsonObject conn) continue;
            var meta = conn["metadata"] as JsonObject;
            var info = new ConnectionInfo
            {
                Host = GetText(meta, "host"),
                DestinationIp = GetText(meta, "destinationIP"),
                DestinationPort = GetText(meta, "destinationPort"),
                Network = GetText(meta, "network"),
                Inbound = GetText(meta, "type"),
                Process = GetText(meta, "processPath"),
                Rule = GetText(conn, "rule"),
                Upload = GetLong(conn, "upload"),
                Download = GetLong(conn, "download"),
            };
            if (conn["chains"] is JsonArray chains)
                info.Chain = chains
                    .Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "")
                    .Where(s => s.Length > 0)
                    .ToList();
            result.Add(info);
        }
        return result;
    }

    private static string GetText(JsonObject? obj, string property)
    {
        if (obj?[property] is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var s)) return s;
        // 端口这类字段可能是数字
        if (value.TryGetValue(out long l)) return l.ToString();
        if (value.TryGetValue(out int i)) return i.ToString();
        if (value.TryGetValue(out double d)) return ((long)d).ToString();
        return "";
    }

    public static long GetLong(JsonObject obj, string property)
    {
        if (obj[property] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return (long)d;
        }
        return 0;
    }
}

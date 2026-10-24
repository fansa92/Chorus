using System.Text;
using System.Text.Json.Nodes;
using Chorus.Core.Models;

namespace Chorus.Core.Parsing;

/// <summary>解析 v2rayN / Clash 风格的节点分享链接。</summary>
public static class ShareLinkParser
{
    public static readonly string[] SupportedSchemes =
    [
        "vmess://", "vless://", "trojan://", "ss://", "hysteria2://", "hy2://",
    ];

    public static NodeEntry? Parse(string link, out string? error)
    {
        error = null;
        link = link.Trim();
        try
        {
            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(link);
            if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(link);
            if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(link);
            if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(link);
            if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(link);
            error = "不支持的链接格式：" + Truncate(link);
        }
        catch (Exception ex)
        {
            error = $"解析失败：{Truncate(link)}（{ex.Message}）";
        }
        return null;
    }

    private static NodeEntry ParseVmess(string link)
    {
        var json = DecodeBase64(link["vmess://".Length..]);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("vmess 内容不是有效的 JSON");

        var node = new NodeEntry
        {
            Protocol = "vmess",
            Tag = GetString(root, "ps") ?? "",
            Server = GetString(root, "add") ?? throw new InvalidDataException("vmess 缺少服务器地址"),
            Uuid = GetString(root, "id") ?? throw new InvalidDataException("vmess 缺少 uuid"),
            Security = GetString(root, "scy") is { Length: > 0 } scy ? scy : "auto",
            Transport = GetString(root, "net"),
            Host = GetString(root, "host"),
            Path = GetString(root, "path"),
            Sni = GetString(root, "sni"),
            Fingerprint = GetString(root, "fp"),
            AlterId = GetInt(root, "aid"),
            ServerPort = GetInt(root, "port"),
        };
        var tls = GetString(root, "tls");
        node.Tls = string.Equals(tls, "tls", StringComparison.OrdinalIgnoreCase);
        if (node.ServerPort <= 0) throw new InvalidDataException("vmess 端口无效");
        if (string.IsNullOrWhiteSpace(node.Tag)) node.Tag = $"{node.Server}:{node.ServerPort}";
        ValidateServer(node);
        return node;
    }

    private static NodeEntry ParseVless(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri.Query);
        var node = new NodeEntry
        {
            Protocol = "vless",
            Tag = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Server = uri.Host,
            ServerPort = uri.Port,
            Uuid = Uri.UnescapeDataString(uri.UserInfo),
            Flow = q.GetValueOrDefault("flow"),
            Transport = q.GetValueOrDefault("type"),
            Host = q.GetValueOrDefault("host"),
            Path = q.GetValueOrDefault("path"),
            ServiceName = q.GetValueOrDefault("serviceName"),
            Sni = q.GetValueOrDefault("sni"),
            Fingerprint = q.GetValueOrDefault("fp"),
            RealityPublicKey = q.GetValueOrDefault("pbk"),
            RealityShortId = q.GetValueOrDefault("sid"),
            Insecure = IsTrue(q.GetValueOrDefault("allowInsecure")),
        };
        var security = q.GetValueOrDefault("security");
        node.Tls = security is "tls" or "reality";
        if (node.ServerPort <= 0) node.ServerPort = 443;
        if (string.IsNullOrWhiteSpace(node.Tag)) node.Tag = $"{node.Server}:{node.ServerPort}";
        ValidateServer(node);
        return node;
    }

    private static NodeEntry ParseTrojan(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri.Query);
        var node = new NodeEntry
        {
            Protocol = "trojan",
            Tag = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Server = uri.Host,
            ServerPort = uri.Port > 0 ? uri.Port : 443,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Sni = q.GetValueOrDefault("sni") ?? q.GetValueOrDefault("peer"),
            Fingerprint = q.GetValueOrDefault("fp"),
            Transport = q.GetValueOrDefault("type"),
            Host = q.GetValueOrDefault("host"),
            Path = q.GetValueOrDefault("path"),
            Insecure = IsTrue(q.GetValueOrDefault("allowInsecure")),
            Tls = true,
        };
        if (string.IsNullOrWhiteSpace(node.Tag)) node.Tag = $"{node.Server}:{node.ServerPort}";
        ValidateServer(node);
        return node;
    }

    private static NodeEntry ParseHysteria2(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri.Query);
        var node = new NodeEntry
        {
            Protocol = "hysteria2",
            Tag = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Server = uri.Host,
            ServerPort = uri.Port > 0 ? uri.Port : 443,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Sni = q.GetValueOrDefault("sni"),
            Insecure = IsTrue(q.GetValueOrDefault("insecure")),
            ObfsPassword = q.GetValueOrDefault("obfs-password") ?? q.GetValueOrDefault("obfsParam"),
            Tls = true,
        };
        if (string.IsNullOrWhiteSpace(node.Tag)) node.Tag = $"{node.Server}:{node.ServerPort}";
        ValidateServer(node);
        return node;
    }

    private static NodeEntry ParseShadowsocks(string link)
    {
        var payload = link["ss://".Length..];
        var tag = "";
        var hashIndex = payload.IndexOf('#');
        if (hashIndex >= 0)
        {
            tag = Uri.UnescapeDataString(payload[(hashIndex + 1)..]);
            payload = payload[..hashIndex];
        }
        var queryIndex = payload.IndexOf('?');
        if (queryIndex >= 0) payload = payload[..queryIndex];

        string method, password, hostPort;
        var atIndex = payload.LastIndexOf('@');
        if (atIndex >= 0)
        {
            // SIP002：ss://base64(method:password)@host:port#tag
            var userInfo = payload[..atIndex];
            hostPort = payload[(atIndex + 1)..];
            var credential = TryDecodeBase64(userInfo) ?? Uri.UnescapeDataString(userInfo);
            (method, password) = SplitCredential(credential);
        }
        else
        {
            // 旧格式：ss://base64(method:password@host:port)#tag
            var decoded = DecodeBase64(payload);
            var innerAtIndex = decoded.LastIndexOf('@');
            if (innerAtIndex < 0) throw new InvalidDataException("ss 链接格式无效");
            var credential = decoded[..innerAtIndex];
            hostPort = decoded[(innerAtIndex + 1)..];
            (method, password) = SplitCredential(credential);
        }

        var (server, port) = ParseHostPort(hostPort);
        return new NodeEntry
        {
            Protocol = "ss",
            Tag = string.IsNullOrWhiteSpace(tag) ? $"{server}:{port}" : tag,
            Server = server,
            ServerPort = port,
            Method = method,
            Password = password,
        };
    }

    private static (string, string) SplitCredential(string credential)
    {
        var colon = credential.IndexOf(':');
        if (colon <= 0) throw new InvalidDataException("ss 用户信息格式无效");
        return (credential[..colon], credential[(colon + 1)..]);
    }

    internal static (string Server, int Port) ParseHostPort(string hostPort)
    {
        hostPort = hostPort.Trim().TrimEnd('/');
        if (hostPort.StartsWith('['))
        {
            var end = hostPort.IndexOf(']');
            if (end < 0) throw new InvalidDataException("IPv6 地址格式无效");
            var server = hostPort[1..end];
            var rest = hostPort[(end + 1)..];
            if (!rest.StartsWith(':') || !int.TryParse(rest[1..], out var v6Port))
                throw new InvalidDataException("IPv6 地址缺少有效端口");
            return (server, v6Port);
        }
        var index = hostPort.LastIndexOf(':');
        if (index < 0 || !int.TryParse(hostPort[(index + 1)..], out var port))
            throw new InvalidDataException("地址缺少有效端口");
        return (hostPort[..index], port);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var key = index < 0 ? pair : pair[..index];
            var value = index < 0 ? "" : pair[(index + 1)..];
            result[Uri.UnescapeDataString(key).Trim()] = Uri.UnescapeDataString(value);
        }
        return result;
    }

    private static bool IsTrue(string? value) => value is "1" or "true";

    internal static string DecodeBase64(string input)
    {
        input = input.Trim().Replace('-', '+').Replace('_', '/');
        switch (input.Length % 4)
        {
            case 2: input += "=="; break;
            case 3: input += "="; break;
            case 1: input = input[..^1]; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(input));
    }

    internal static string? TryDecodeBase64(string input)
    {
        try { return DecodeBase64(input); }
        catch { return null; }
    }

    private static string? GetString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static int GetInt(JsonObject node, string property)
    {
        if (node[property] is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        }
        return 0;
    }

    private static void ValidateServer(NodeEntry node)
    {
        if (string.IsNullOrWhiteSpace(node.Server)) throw new InvalidDataException("缺少服务器地址");
        if (node.ServerPort is < 1 or > 65535) throw new InvalidDataException($"端口无效：{node.ServerPort}");
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
}

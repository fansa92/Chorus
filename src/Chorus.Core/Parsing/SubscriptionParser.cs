using System.Text.Json.Nodes;
using Chorus.Core.Models;

namespace Chorus.Core.Parsing;

/// <summary>解析订阅内容：支持分享链接列表、Base64 编码的链接列表、sing-box 原生 JSON 配置。</summary>
public static class SubscriptionParser
{
    public static (List<NodeEntry> Nodes, List<string> Errors) Parse(string content)
    {
        var (nodes, _, errors) = ParseWithGroups(content);
        return (nodes, errors);
    }

    /// <summary>解析订阅内容，同时提取 sing-box JSON 订阅里的策略组（selector/urltest）。</summary>
    public static (List<NodeEntry> Nodes, List<ProxyGroup> Groups, List<string> Errors) ParseWithGroups(string content)
    {
        var nodes = new List<NodeEntry>();
        var groups = new List<ProxyGroup>();
        var errors = new List<string>();

        content = content?.Trim() ?? "";
        if (content.Length == 0)
        {
            errors.Add("订阅内容为空");
            return (nodes, groups, errors);
        }

        if (content.StartsWith('{') || content.StartsWith('['))
            return ParseSingBoxJsonWithGroups(content);

        var lines = SplitLines(content);
        if (!lines.Any(IsShareLink))
        {
            try { lines = SplitLines(ShareLinkParser.DecodeBase64(content)); }
            catch
            {
                errors.Add("内容既不是分享链接，也不是有效的 Base64");
                return (nodes, groups, errors);
            }
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) continue;
            if (!IsShareLink(line))
            {
                errors.Add("无法识别：" + Truncate(line));
                continue;
            }
            if (ShareLinkParser.Parse(line, out var error) is { } node)
                nodes.Add(node);
            else if (error is not null)
                errors.Add(error);
        }
        return (nodes, groups, errors);
    }

    public static (List<NodeEntry> Nodes, List<string> Errors) ParseSingBoxJson(string content)
    {
        var (nodes, _, errors) = ParseSingBoxJsonWithGroups(content);
        return (nodes, errors);
    }

    /// <summary>解析 sing-box JSON 订阅：提取节点与策略组。功能性出站（direct/block/dns）忽略。</summary>
    public static (List<NodeEntry> Nodes, List<ProxyGroup> Groups, List<string> Errors) ParseSingBoxJsonWithGroups(string content)
    {
        var nodes = new List<NodeEntry>();
        var groups = new List<ProxyGroup>();
        var errors = new List<string>();
        try
        {
            var root = JsonNode.Parse(content);
            var outbounds = root switch
            {
                JsonObject obj => obj["outbounds"] as JsonArray,
                JsonArray arr => arr,
                _ => null,
            };
            if (outbounds is null)
            {
                errors.Add("JSON 中没有 outbounds 数组");
                return (nodes, groups, errors);
            }
            foreach (var item in outbounds)
            {
                if (item is not JsonObject outbound) continue;
                var type = GetString(outbound, "type")?.ToLowerInvariant();
                // 功能性出站（直连/拦截/DNS）忽略；selector/urltest 提取为策略组
                if (type is "direct" or "block" or "dns") continue;
                if (type is "selector" or "urltest")
                {
                    if (FromGroup(outbound, type) is { } group)
                        groups.Add(group);
                    continue;
                }
                if (!IsSupportedProxyType(type))
                {
                    errors.Add($"不支持的出站类型：{type ?? "?"}（{GetString(outbound, "tag") ?? "未命名"}，已跳过）");
                    continue;
                }
                if (FromOutbound(outbound) is { } node) nodes.Add(node);
            }
        }
        catch (Exception ex)
        {
            errors.Add("解析 sing-box JSON 失败：" + ex.Message);
        }
        return (nodes, groups, errors);
    }

    /// <summary>从 selector/urltest 出站提取策略组。成员列表原样保留，由 ConfigGenerator 负责解析引用。</summary>
    private static ProxyGroup? FromGroup(JsonObject o, string type)
    {
        var name = GetString(o, "tag");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var members = new List<string>();
        if (o["outbounds"] is JsonArray list)
            members.AddRange(list.Select(x => x?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s))!);
        return new ProxyGroup
        {
            Name = name,
            Type = type,
            Members = members,
            Default = GetString(o, "default"),
            Url = GetString(o, "url"),
            Interval = GetString(o, "interval"),
        };
    }

    private static NodeEntry? FromOutbound(JsonObject o)
    {
        var type = GetString(o, "type")?.ToLowerInvariant();
        var tag = GetString(o, "tag") ?? "";
        if (type is null or "direct" or "block" or "dns" or "selector" or "urltest") return null;
        var server = GetString(o, "server");
        var port = 0;
        if (o["server_port"] is JsonValue pv) pv.TryGetValue<int>(out port);
        if (string.IsNullOrWhiteSpace(server) || port is < 1 or > 65535) return null;

        var node = new NodeEntry
        {
            // shadowsocks 归一为内部通用的 "ss"
            Protocol = type == "shadowsocks" ? "ss" : type,
            Tag = tag,
            Server = server,
            ServerPort = port,
            Uuid = GetString(o, "uuid"),
            Password = GetString(o, "password"),
            Method = GetString(o, "method"),
            Security = GetString(o, "security"),
            Flow = GetString(o, "flow"),
        };
        if (o["alter_id"] is JsonValue av)
        {
            var alterId = 0;
            if (av.TryGetValue<int>(out alterId)) node.AlterId = alterId;
        }

        if (o["tls"] is JsonObject tls)
        {
            node.Tls = type is "trojan" or "hysteria2" || GetBool(tls, "enabled");
            node.Sni = GetString(tls, "server_name");
            node.Insecure = GetBool(tls, "insecure");
            if (tls["alpn"] is JsonArray alpn)
                node.Alpn = string.Join(",", alpn.Select(x => x?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)));
            if (tls["utls"] is JsonObject utls && GetBool(utls, "enabled"))
                node.Fingerprint = GetString(utls, "fingerprint");
            if (tls["reality"] is JsonObject reality && GetBool(reality, "enabled"))
            {
                node.RealityPublicKey = GetString(reality, "public_key");
                node.RealityShortId = GetString(reality, "short_id");
            }
        }
        else if (type is "trojan" or "hysteria2")
        {
            node.Tls = true;
        }

        if (o["transport"] is JsonObject transport)
        {
            node.Transport = GetString(transport, "type");
            node.Path = GetString(transport, "path");
            node.ServiceName = GetString(transport, "service_name");
            if (transport["headers"] is JsonObject headers)
                node.Host = GetString(headers, "Host") ?? GetString(headers, "host");
            if (transport["host"] is JsonArray hostArray)
                node.Host = string.Join(",", hostArray.Select(x => x?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)));
        }

        if (o["obfs"] is JsonObject obfs && GetBool(obfs, "enabled"))
            node.ObfsPassword = GetString(obfs, "password");

        if (string.IsNullOrWhiteSpace(node.Tag)) node.Tag = $"{node.Server}:{node.ServerPort}";
        return node;
    }

    private static bool IsShareLink(string line) =>
        ShareLinkParser.SupportedSchemes.Any(s => line.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>ConfigGenerator 支持的代理出站类型（含 sing-box JSON 与分享链接里的别名）。</summary>
    private static readonly string[] SupportedProxyTypes =
    [
        "vmess", "vless", "trojan", "ss", "shadowsocks", "hysteria2", "hy2", "anytls",
    ];

    private static bool IsSupportedProxyType(string? type) =>
        type is not null && SupportedProxyTypes.Contains(type);

    private static string[] SplitLines(string content) =>
        content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string? GetString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static bool GetBool(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
}

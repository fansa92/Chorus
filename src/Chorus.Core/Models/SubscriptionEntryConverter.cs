using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chorus.Core.Models;

/// <summary>
/// SubscriptionEntry 的 JSON 转换器：兼容旧版 settings.json 中
/// "Subscriptions": ["https://..."]（纯字符串数组）的格式，自动迁移为完整对象。
/// </summary>
public sealed class SubscriptionEntryConverter : JsonConverter<SubscriptionEntry>
{
    public override SubscriptionEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var url = reader.GetString() ?? "";
                return new SubscriptionEntry { Url = url, Name = DefaultName(url) };
            }
            case JsonTokenType.StartObject:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;
                var entry = new SubscriptionEntry
                {
                    Name = GetString(root, "name") ?? "",
                    Url = GetString(root, "url") ?? "",
                    LastError = GetString(root, "lastError"),
                };
                if (TryGetProperty(root, "nodes", out var nodesElement)
                    && nodesElement.ValueKind == JsonValueKind.Array)
                {
                    entry.Nodes = JsonSerializer.Deserialize<List<NodeEntry>>(
                        nodesElement.GetRawText(), options) ?? [];
                }
                // 策略组必须一起读回：早期版本漏了这一段，导致订阅里的 proxy-groups
                // 保存后丢失，重启应用就只剩自动生成的默认组
                if (TryGetProperty(root, "groups", out var groupsElement)
                    && groupsElement.ValueKind == JsonValueKind.Array)
                {
                    entry.Groups = JsonSerializer.Deserialize<List<ProxyGroup>>(
                        groupsElement.GetRawText(), options) ?? [];
                }
                if (TryGetProperty(root, "lastUpdated", out var updatedElement)
                    && updatedElement.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(updatedElement.GetString(), out var updated))
                {
                    entry.LastUpdated = updated;
                }
                // 订阅自带的 route/dns 片段同样必须读回：漏掉就会在存盘后丢失，
                // 规则页与生成配置都会退回「只有内置两条模式规则」
                entry.RouteJson = GetString(root, "routeJson");
                entry.DnsJson = GetString(root, "dnsJson");
                if (string.IsNullOrWhiteSpace(entry.Name) && entry.Url.Length > 0)
                    entry.Name = DefaultName(entry.Url);
                return entry;
            }
            default:
                throw new JsonException($"无法解析订阅条目（token: {reader.TokenType}）");
        }
    }

    public override void Write(Utf8JsonWriter writer, SubscriptionEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("url", value.Url);
        writer.WriteStartArray("nodes");
        foreach (var node in value.Nodes)
            JsonSerializer.Serialize(writer, node, options);
        writer.WriteEndArray();
        if (value.Groups.Count > 0)
        {
            writer.WriteStartArray("groups");
            foreach (var group in value.Groups)
                JsonSerializer.Serialize(writer, group, options);
            writer.WriteEndArray();
        }
        if (value.LastUpdated is { } updated)
            writer.WriteString("lastUpdated", updated);
        if (value.RouteJson is { Length: > 0 } routeJson)
            writer.WriteString("routeJson", routeJson);
        if (value.DnsJson is { Length: > 0 } dnsJson)
            writer.WriteString("dnsJson", dnsJson);
        if (value.LastError is { } error)
            writer.WriteString("lastError", error);
        writer.WriteEndObject();
    }

    /// <summary>从 URL 推导默认名称：取主机名。</summary>
    public static string DefaultName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;
        return "未命名订阅";
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

namespace Chorus.Core.Parsing;

/// <summary>
/// sing-box 原生 JSON 订阅里与「路由 / DNS」相关的原始片段（序列化后的 JSON 文本）。
/// 保存下来有两个用途：规则页展示订阅自带的规则集，以及生成配置时把规则合并进去
/// （否则订阅里的规则会在解析阶段被丢弃，页面看起来就是「规则没加载」）。
/// </summary>
public sealed record SubscriptionRouting(string? RouteJson, string? DnsJson)
{
    public bool HasAny => !string.IsNullOrWhiteSpace(RouteJson) || !string.IsNullOrWhiteSpace(DnsJson);
}

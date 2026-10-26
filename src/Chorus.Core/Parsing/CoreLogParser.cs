using System.Text.RegularExpressions;

namespace Chorus.Core.Parsing;

/// <summary>
/// 一行日志拆解结果。Outbound 是这一行提到的出站 tag（<c>outbound/vless[HK 01]</c> 里的 <c>HK 01</c>），
/// Target 是这一行的目标地址（host:port / ip:port），用来和 Clash API 的活动连接对上。
/// </summary>
public readonly record struct ParsedLogLine(
    string Level, string Module, string Message, string Outbound, string Target);

/// <summary>
/// 解析核心日志行。sing-box 的默认输出形如：
/// <code>
/// +0800 2026-10-25 10:14:34 [36mINFO[0m outbound/vless[HK 01]: outbound connection to www.gstatic.com:443
/// +0800 2026-10-25 10:14:37 [36mINFO[0m [[38;5;30m2177483534[0m 0ms] inbound/mixed[mixed-in]: inbound connection from 127.0.0.1:52133
/// </code>
/// 即「时区 + 时间戳 + ANSI 颜色 + 级别 + 连接 id + 模块: 正文」，需要逐段剥掉。
/// Clash 风格 <c>[DNS] resolve ...</c> 也兼容。
/// </summary>
public static partial class CoreLogParser
{
    // ANSI 颜色控制符：\x1b[36m / \x1b[0m / \x1b[38;5;30m
    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    // 时区前缀：+0800
    [GeneratedRegex(@"^[+-]\d{4}\s+")]
    private static partial Regex ZoneRegex();

    // 时间戳：2026-10-25 10:14:34.123 / 2026-10-25T10:14:34+08:00
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?\s*")]
    private static partial Regex TimestampRegex();

    // 级别：INFO / WARN / ERROR / DEBUG / FATAL，兼容 logrus 的 level=info
    [GeneratedRegex(@"^(?:level=(?<a>[A-Za-z]+)\s*)?(?<b>TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b\s*(?:\[\d+(?:\.\d+)?\])?\s*",
        RegexOptions.IgnoreCase)]
    private static partial Regex LevelRegex();

    // sing-box 的连接 id 与耗时：[2177483534 0ms]
    [GeneratedRegex(@"^\[\d+\s+[0-9.]+(?:ns|us|µs|ms|s)\]\s*")]
    private static partial Regex ConnectionIdRegex();

    // Bettbox / Clash 风格：[DNS] resolve ...
    [GeneratedRegex(@"^\[(?<m>[^\]\r\n]{1,32})\]\s*")]
    private static partial Regex BracketModuleRegex();

    // 模块前缀：router: / network: / inbound/mixed[tun-in]: / clash-api:（必须带冒号，避免误吃正文）
    [GeneratedRegex(@"^(?<m>[a-z][a-z0-9_-]*(?:/[a-z0-9_-]+)?)(?:\[[^\]\r\n]{0,64}\])?\s*[:：]\s")]
    private static partial Regex NamedModuleRegex();

    // 出站及其 tag：outbound/vless[HK 01]（既可能出现在模块前缀，也可能出现在正文的 using 后面）
    [GeneratedRegex(@"outbound/[a-z0-9_-]+\[(?<tag>[^\]\r\n]{1,64})\]")]
    private static partial Regex OutboundRegex();

    // 目标地址：outbound connection to www.gstatic.com:443 / dial tcp 18.140.198.185:46105
    [GeneratedRegex(@"(?:connection to|dial (?:tcp|udp))\s+(?<d>(?:\[[0-9A-Fa-f:.]+\]|[^\s,:]+):\d{1,5})")]
    private static partial Regex TargetRegex();

    public static ParsedLogLine Parse(string? raw)
    {
        var original = raw ?? "";
        var text = AnsiRegex().Replace(original, "");

        // 出站 tag 与目标地址在整行里找：模块前缀会被剥掉，正文里的 using 也要照顾到
        var outbound = OutboundRegex().Match(text) is { Success: true } ob ? ob.Groups["tag"].Value : "";
        var target = TargetRegex().Match(text) is { Success: true } tg
            ? tg.Groups["d"].Value.Replace("[", "").Replace("]", "") // IPv6 写作 [::1]:443，与 API 的 ::1:443 对齐
            : "";

        foreach (var prefix in new[] { ZoneRegex(), TimestampRegex() })
        {
            var m = prefix.Match(text);
            if (m.Success) text = text[m.Length..];
        }

        var level = "";
        var levelMatch = LevelRegex().Match(text);
        if (levelMatch.Success)
        {
            level = (levelMatch.Groups["a"].Success ? levelMatch.Groups["a"].Value
                                                    : levelMatch.Groups["b"].Value).ToUpperInvariant();
            if (level == "WARNING") level = "WARN";
            text = text[levelMatch.Length..];
        }

        var connId = ConnectionIdRegex().Match(text);
        if (connId.Success) text = text[connId.Length..];

        var module = "";
        var bracket = BracketModuleRegex().Match(text);
        if (bracket.Success)
        {
            module = bracket.Groups["m"].Value.Trim();
            text = text[bracket.Length..];
        }
        else
        {
            var named = NamedModuleRegex().Match(text);
            if (named.Success)
            {
                module = named.Groups["m"].Value;
                text = text[named.Length..];
            }
        }

        text = text.TrimEnd();
        if (text.Length == 0) text = original.Trim();
        return new ParsedLogLine(level, module, text, outbound, target);
    }
}

using System.Text.Json.Nodes;

namespace Chorus.Core.Utils;

/// <summary>出口 IP 的归属信息（仪表盘「网络检测」用）。</summary>
public sealed record IpInfo(string Ip, string Country, string City, string Isp, string CountryCode = "")
{
    /// <summary>「国家/地区 · 城市」；城市已经包含在地区名里时只显示地区（例如「中国香港」+「香港」）。</summary>
    public string LocationText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Country)) return string.IsNullOrWhiteSpace(City) ? "未知地区" : City;
            if (string.IsNullOrWhiteSpace(City) || Country.Contains(City, StringComparison.Ordinal)) return Country;
            return $"{Country} · {City}";
        }
    }

    /// <summary>
    /// 国旗资源名（Assets/flags/{FlagCode}.png，小写 ISO 3166-1 alpha-2）。
    /// 台湾用中国国旗；港澳用各自的特别行政区区旗；没有代码或代码不规范时返回空串（界面退回地球图标）。
    /// </summary>
    public string FlagCode
    {
        get
        {
            var code = CountryCode.Trim().ToLowerInvariant();
            if (code.Length != 2 || !code.All(char.IsAsciiLetter)) return "";
            return code == "tw" ? "cn" : code;
        }
    }
}

/// <summary>
/// 解析各家 IP 归属查询接口的返回体：主接口 ipwho.is，备用接口 ip-api.com。
/// 只做纯解析，便于单测——网络请求在 App 层。
/// </summary>
public static class IpInfoParser
{
    /// <summary>解析 https://ipwho.is/?lang=zh-CN 的返回。</summary>
    public static IpInfo? ParseIpWhoIs(string json)
    {
        if (Parse(json) is not JsonObject root) return null;
        if (root["success"] is JsonValue flag && flag.TryGetValue<bool>(out var success) && !success) return null;

        var ip = Str(root, "ip");
        if (ip.Length == 0) return null;

        var code = Str(root, "country_code");
        var country = Str(root, "country");
        if (country.Length == 0) country = code;
        var isp = root["connection"] is JsonObject connection ? Str(connection, "isp") : "";
        if (isp.Length == 0 && root["connection"] is JsonObject conn2) isp = Str(conn2, "org");

        return new IpInfo(ip, NormalizeCountry(country, code), Str(root, "city"), isp, code);
    }

    /// <summary>解析 http://ip-api.com/json 的返回。</summary>
    public static IpInfo? ParseIpApi(string json)
    {
        if (Parse(json) is not JsonObject root) return null;
        if (Str(root, "status") is "fail") return null;

        var ip = Str(root, "query");
        if (ip.Length == 0) ip = Str(root, "ip");
        if (ip.Length == 0) return null;

        var code = Str(root, "countryCode");
        var country = Str(root, "country");
        if (country.Length == 0) country = code;
        var city = Str(root, "city");
        if (city.Length == 0) city = Str(root, "regionName");

        return new IpInfo(ip, NormalizeCountry(country, code), city, Str(root, "isp"), code);
    }

    /// <summary>
    /// 统一台港澳的地区名：接口有时给「中国台湾」，有时只给「台湾」/「Taiwan」，
    /// 这里一律按中国的地区写法定名，避免界面上出现不一致的叫法。
    /// </summary>
    private static string NormalizeCountry(string country, string code) => code.ToUpperInvariant() switch
    {
        "TW" => "中国台湾",
        "HK" => "中国香港",
        "MO" => "中国澳门",
        "CN" when country.Length == 0 => "中国",
        _ => country,
    };

    private static JsonNode? Parse(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonObject obj, string property) =>
        obj[property] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
}

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

    /// <summary>这条结果是从哪来的（界面底部的来源标注）。</summary>
    public string Source { get; init; } = "";

    /// <summary>来源的中文短名。</summary>
    public string SourceText => Source switch
    {
        "ip-api" => "ip-api.com",
        "ipwho.is" => "ipwho.is",
        "ipinfo.io" => "ipinfo.io",
        "ip.im" => "ip.im",
        "iplark" => "iplark.com",
        "ifconfig.co" => "ifconfig.co",
        "local" => "本地 GeoIP 库",
        _ => "",
    };
}

/// <summary>
/// 解析各家 IP 归属查询接口的返回体。
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

        return new IpInfo(ip, NormalizeCountry(country, code), Str(root, "city"), isp, code)
        {
            Source = "ipwho.is",
        };
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

        return new IpInfo(ip, NormalizeCountry(country, code), city, Str(root, "isp"), code)
        {
            Source = "ip-api",
        };
    }

    /// <summary>
    /// 解析 https://ipinfo.io/json 的返回。
    /// 它没有国家全名，只有两位代码与 org（运营商），城市在 city 字段。
    /// </summary>
    public static IpInfo? ParseIpInfoIo(string json)
    {
        if (Parse(json) is not JsonObject root) return null;

        var ip = Str(root, "ip");
        if (ip.Length == 0) return null;

        var code = Str(root, "country");
        var country = CountryNames.Of(code);
        var isp = Str(root, "org");
        // org 形如 "AS15169 Google LLC"，只留后面的名字更干净
        if (isp.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            var space = isp.IndexOf(' ');
            if (space > 0 && space < isp.Length - 1) isp = isp[(space + 1)..];
        }

        return new IpInfo(ip, NormalizeCountry(country, code), Str(root, "city"), isp, code)
        {
            Source = "ipinfo.io",
        };
    }

    /// <summary>
    /// 解析 https://ip.im/api 的返回（字段名与 ipwho.is 类似但有差异）。
    /// </summary>
    public static IpInfo? ParseIpIm(string json)
    {
        if (Parse(json) is not JsonObject root) return null;

        var ip = Str(root, "ip");
        if (ip.Length == 0) return null;

        var code = Str(root, "country_code");
        var country = Str(root, "country");
        if (country.Length == 0) country = code;
        var isp = Str(root, "isp");
        if (isp.Length == 0) isp = Str(root, "asn_org");

        return new IpInfo(ip, NormalizeCountry(country, code), Str(root, "city"), isp, code)
        {
            Source = "ip.im",
        };
    }

    /// <summary>
    /// 解析 https://iplark.com/api 的返回。
    /// 它用驼峰字段名，与 ip-api 类似但用 countryCode 之外还有 code。
    /// </summary>
    public static IpInfo? ParseIplark(string json)
    {
        if (Parse(json) is not JsonObject root) return null;

        var ip = Str(root, "ip");
        if (ip.Length == 0) return null;

        var code = Str(root, "countryCode");
        if (code.Length == 0) code = Str(root, "code");
        var country = Str(root, "country");
        if (country.Length == 0) country = Str(root, "countryName");
        if (country.Length == 0) country = CountryNames.Of(code);
        var city = Str(root, "city");
        if (city.Length == 0) city = Str(root, "regionName");
        var isp = Str(root, "isp");
        if (isp.Length == 0) isp = Str(root, "asnOrg");

        return new IpInfo(ip, NormalizeCountry(country, code), city, isp, code)
        {
            Source = "iplark",
        };
    }

    /// <summary>
    /// 解析 https://ifconfig.co/json 的返回。
    /// 它用 country_iso 给两位代码、asn_org 给运营商，但没有城市。
    /// </summary>
    public static IpInfo? ParseIfconfigCo(string json)
    {
        if (Parse(json) is not JsonObject root) return null;

        var ip = Str(root, "ip");
        if (ip.Length == 0) return null;

        var code = Str(root, "country_iso");
        var country = Str(root, "country");
        if (country.Length == 0) country = CountryNames.Of(code);
        var isp = Str(root, "asn_org");
        if (isp.Length == 0) isp = Str(root, "asn");

        return new IpInfo(ip, NormalizeCountry(country, code), Str(root, "city"), isp, code)
        {
            Source = "ifconfig.co",
        };
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

/// <summary>
/// 两位国家代码 → 中文国家名。
/// 只有 ipinfo.io 这种只给代码不给名字的接口才用得上；
/// 表不求全，覆盖常见的出口地即可，缺了就退回代码本身。
/// </summary>
public static class CountryNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CN"] = "中国", ["US"] = "美国", ["JP"] = "日本", ["KR"] = "韩国", ["SG"] = "新加坡",
        ["TW"] = "中国台湾", ["HK"] = "中国香港", ["MO"] = "中国澳门",
        ["GB"] = "英国", ["DE"] = "德国", ["FR"] = "法国", ["CA"] = "加拿大", ["AU"] = "澳大利亚",
        ["RU"] = "俄罗斯", ["IN"] = "印度", ["BR"] = "巴西", ["NL"] = "荷兰", ["IT"] = "意大利",
        ["ES"] = "西班牙", ["SE"] = "瑞典", ["CH"] = "瑞士", ["NO"] = "挪威", ["FI"] = "芬兰",
        ["DK"] = "丹麦", ["PL"] = "波兰", ["TR"] = "土耳其", ["TH"] = "泰国", ["VN"] = "越南",
        ["MY"] = "马来西亚", ["ID"] = "印度尼西亚", ["PH"] = "菲律宾", ["NZ"] = "新西兰",
        ["IE"] = "爱尔兰", ["AT"] = "奥地利", ["BE"] = "比利时", ["CZ"] = "捷克", ["PT"] = "葡萄牙",
        ["GR"] = "希腊", ["HU"] = "匈牙利", ["UA"] = "乌克兰", ["IL"] = "以色列", ["AE"] = "阿联酋",
        ["SA"] = "沙特阿拉伯", ["ZA"] = "南非", ["AR"] = "阿根廷", ["CL"] = "智利", ["MX"] = "墨西哥",
        ["RO"] = "罗马尼亚", ["BG"] = "保加利亚", ["IS"] = "冰岛", ["IE2"] = "爱尔兰",
        ["KZ"] = "哈萨克斯坦", ["MN"] = "蒙古", ["PK"] = "巴基斯坦", ["BD"] = "孟加拉国",
        ["NP"] = "尼泊尔", ["LK"] = "斯里兰卡", ["KH"] = "柬埔寨", ["LA"] = "老挝", ["MM"] = "缅甸",
    };

    /// <summary>取中文名；没有收录就返回代码本身（界面显示代码也不算错）。</summary>
    public static string Of(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        return Map.TryGetValue(code.Trim(), out var name) ? name : code.Trim().ToUpperInvariant();
    }
}

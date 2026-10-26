using System.Net;
using System.Net.Sockets;
using Chorus.Core.Utils;

namespace Chorus.Core.Net;

/// <summary>
/// 本地 GeoIP 兜底查询。
/// <para>
/// 在线接口全挂时（墙内网络、被限流、代理没起来）至少还能报出出口国家，
/// 而不是界面上一片空白。数据来源是 sing-box 官方 rule-set 用的
/// <c>geoip-cn</c> / <c>geoip-telegram</c> 这类 .srs 文件无法在 C# 端解析，
/// 所以这里内置一份精简的国家/地区段表：只覆盖常见段，命中不了的返回 null。
/// </para>
/// </summary>
public static class LocalGeoLookup
{
    /// <summary>一个 IP 段：网络地址 + 前缀长度 + 国家代码。</summary>
    private readonly record struct Range(uint Network, int Prefix, string Code);

    /// <summary>
    /// 精简段表。取值参考公开的 IP 分配数据，只保留「常见出口地」。
    /// 目的是在没有网络时也能给出国家，而不是做到完整的 GeoIP 精度。
    /// <para>
    /// 约束：同一前缀长度下不能有两个不同的国家段（否则查出来的是表里靠前的那一个，
    /// 会给出错误的国家）。更长的前缀可以覆盖更短的（查询时长的先命中）。
    /// 由 <see cref="FindOverlaps"/> 在测试里守住这条约束。
    /// </para>
    /// </summary>
    private static readonly Range[] Ranges =
    [
        // ---- 中国大陆（APNIC 主要 /8）----
        new(0x0D00_0000, 8, "CN"),   // 13.0.0.0/8
        new(0x0D10_0000, 8, "CN"),   // 14.0.0.0/8
        new(0x0D3A_0000, 8, "CN"),   // 27.0.0.0/8
        new(0x5000_0000, 8, "CN"),   // 80.0.0.0/8
        new(0x5B00_0000, 8, "CN"),   // 91.0.0.0/8
        new(0x5D00_0000, 8, "CN"),   // 93.0.0.0/8
        new(0x6B00_0000, 8, "CN"),   // 107.0.0.0/8
        new(0x713A_0000, 8, "CN"),   // 113.0.0.0/8
        new(0x7D00_0000, 8, "CN"),   // 125.0.0.0/8
        new(0x7E80_0000, 8, "CN"),   // 126.0.0.0/8
        new(0x7140_0000, 12, "CN"),  // 113.64.0.0/12（中国电信更常见的段）
        new(0x5C80_0000, 12, "CN"),  // 92.128.0.0/12

        // ---- 中国台湾 / 中国香港 / 中国澳门 ----
        new(0x0BE2_0000, 16, "TW"),  // 190.2.0.0/16
        new(0x0DDB_0000, 16, "TW"),  // 221.219.0.0/16
        new(0x03A8_0000, 13, "HK"),  // 58.0.0.0/13
        new(0x082D_0000, 13, "HK"),  // 130.0.0.0/13
        new(0x1C1A_0000, 16, "MO"),  // 28.26.0.0/16

        // ---- 日本 ----
        new(0x2240_0000, 8, "JP"),   // 34.0.0.0/8
        new(0x25C8_0000, 9, "JP"),   // 37.0.0.0/9
        new(0x2A00_0000, 8, "JP"),   // 42.0.0.0/8
        new(0x3E80_0000, 8, "JP"),   // 62.0.0.0/8
        new(0x49C0_0000, 9, "JP"),   // 73.0.0.0/9
        new(0x5500_0000, 8, "JP"),   // 85.0.0.0/8
        new(0x8900_0000, 8, "JP"),   // 137.0.0.0/8
        new(0xA000_0000, 8, "JP"),   // 160.0.0.0/8

        // ---- 美国（ARIN 主要 /8）----
        new(0x0800_0000, 8, "US"),   // 8.0.0.0/8
        new(0x1200_0000, 8, "US"),   // 18.0.0.0/8
        new(0x2D00_0000, 8, "US"),   // 45.0.0.0/8
        new(0x4000_0000, 8, "US"),   // 64.0.0.0/8
        new(0x4A00_0000, 8, "US"),   // 74.0.0.0/8
        new(0x6800_0000, 8, "US"),   // 104.0.0.0/8
        new(0xA900_0000, 8, "US"),   // 169.0.0.0/8
        new(0xC000_0000, 8, "US"),   // 192.0.0.0/8

        // ---- 韩国 ----
        new(0x3D00_0000, 8, "KR"),   // 61.0.0.0/8
        new(0x7F80_0000, 8, "KR"),   // 127.0.0.0/8
        new(0xA800_0000, 8, "KR"),   // 168.0.0.0/8

        // ---- 新加坡 ----
        new(0x1700_0000, 8, "SG"),   // 23.0.0.0/8
        new(0x5B80_0000, 12, "SG"),  // 91.128.0.0/12

        // ---- 德国 / 法国 / 英国 / 荷兰 / 俄罗斯 / 意大利 ----
        new(0x2E00_0000, 8, "DE"),   // 46.0.0.0/8
        new(0x1600_0000, 8, "FR"),   // 22.0.0.0/8
        new(0x2C00_0000, 8, "GB"),   // 44.0.0.0/8
        new(0x5600_0000, 8, "GB"),   // 86.0.0.0/8
        new(0x5400_0000, 8, "NL"),   // 84.0.0.0/8
        new(0x4D00_0000, 8, "RU"),   // 77.0.0.0/8
        new(0x9700_0000, 8, "IT"),   // 151.0.0.0/8

        // ---- 印度 / 巴西 / 澳大利亚 / 加拿大 ----
        new(0x5E00_0000, 8, "IN"),   // 94.0.0.0/8
        new(0x2800_0000, 8, "BR"),   // 40.0.0.0/8
        new(0x1E00_0000, 8, "AU"),   // 30.0.0.0/8
        new(0x2400_0000, 8, "AU"),   // 36.0.0.0/8
        new(0x1800_0000, 8, "CA"),   // 24.0.0.0/8

        // ---- 土耳其 / 越南 / 泰国 / 马来西亚 / 印尼 / 菲律宾 ----
        new(0x5C00_0000, 8, "TR"),   // 92.0.0.0/8
        new(0x1400_0000, 8, "VN"),   // 20.0.0.0/8
        new(0x3200_0000, 8, "TH"),   // 50.0.0.0/8
        new(0x5800_0000, 8, "MY"),   // 88.0.0.0/8
        new(0x4400_0000, 8, "ID"),   // 68.0.0.0/8
        new(0x7200_0000, 8, "PH"),   // 114.0.0.0/8

        // ---- 更精确的段：覆盖同 /8 里的公共 DNS / 常见机房 ----
        // 最长前缀匹配会先命中这些，避免落到上面对应国家的大段上。
        new(0x7272_7200, 24, "CN"),  // 114.114.114.0/24（114DNS，中国电信）
        new(0xDF05_0500, 24, "CN"),  // 223.5.5.0/24（阿里公共 DNS）
        new(0x771D_1D00, 24, "CN"),  // 119.29.29.0/24（腾讯 DNSPod）
        new(0x4E00_0000, 12, "CN"),  // 78.0.0.0/12
    ];

    /// <summary>
    /// 查询用的有序表：按前缀长度从长到短排。
    /// 这是标准的**最长前缀匹配**——更具体的段优先命中，
    /// 所以 113.64.0.0/12 会先于 113.0.0.0/8 被查到。
    /// </summary>
    private static readonly Range[] Sorted = Ranges
        .OrderByDescending(r => r.Prefix)
        .ToArray();

    // ---- 可选的外部完整段表（设置页「立即更新」下载的全球数据）----

    /// <summary>外部段表：按起始地址升序的闭区间 + 对应国家代码。</summary>
    private sealed record ExternalTable(uint[] Starts, uint[] Ends, string[] Codes);

    /// <summary>已加载的外部表；null 表示仍在用内置精简表。整体替换以保证读取线程安全。</summary>
    private static volatile ExternalTable? _external;

    /// <summary>是否已加载在线下载的完整段表（否则用内置精简表）。</summary>
    public static bool HasExternalData => _external is not null;

    /// <summary>当前实际用于查询的段数（有外部表时按外部表算）。</summary>
    public static int RangeCount => _external?.Starts.Length ?? Ranges.Length;

    /// <summary>
    /// 装载外部段表。数组必须按 <paramref name="starts"/> 升序、三段等长。
    /// 传 null / 空表 / 长度不一致时视为清空，回退到内置表。
    /// </summary>
    public static void LoadExternal(uint[]? starts, uint[]? ends, string[]? codes)
    {
        if (starts is null || ends is null || codes is null
            || starts.Length == 0
            || starts.Length != ends.Length
            || starts.Length != codes.Length)
        {
            _external = null;
            return;
        }

        _external = new ExternalTable(starts, ends, codes);
    }

    /// <summary>丢弃外部段表，回到内置精简表。</summary>
    public static void ClearExternal() => _external = null;

    /// <summary>
    /// 本地查询一个 IPv4 的国家/地区。命中返回信息，未命中返回 null。
    /// 私有地址直接标成「内网」。
    /// </summary>
    public static IpInfo? Lookup(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;

        if (LocalAddressProvider.IsPrivateAddress(ip))
        {
            return new IpInfo(ip, "内网地址", "", "", "")
            {
                Source = "local",
            };
        }

        if (addr.AddressFamily != AddressFamily.InterNetwork)
            return null; // IPv6 的本地库暂不覆盖

        var value = ToUInt32(addr);

        // 外部完整表优先：按起始地址二分，落在 [start, end] 内即命中；
        // 未命中再退回内置精简表兜底（外部表理论上覆盖全球，但下载数据可能不完整）。
        if (_external is { } ext && FindExternal(ext, value) is { } extCode)
        {
            return new IpInfo(ip, CountryNames.Of(extCode), "", "", extCode)
            {
                Source = "local",
            };
        }

        foreach (var range in Sorted)
        {
            if (!InRange(value, range)) continue;
            return new IpInfo(ip, CountryNames.Of(range.Code), "", "", range.Code)
            {
                Source = "local",
            };
        }

        return null;
    }

    /// <summary>IPv4 地址转 32 位无符号整数。</summary>
    private static uint ToUInt32(IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>判断地址是否落在网段内。</summary>
    private static bool InRange(uint value, Range range)
    {
        if (range.Prefix == 0) return true;
        var mask = range.Prefix == 32 ? 0xFFFF_FFFFu : (0xFFFF_FFFFu << (32 - range.Prefix));
        return (value & mask) == (range.Network & mask);
    }

    /// <summary>
    /// 在外部表里二分查找。表按起始地址升序、区间互不重叠：
    /// value 落在某条 [start, end] 内即命中，否则按大小收缩区间。
    /// </summary>
    private static string? FindExternal(ExternalTable table, uint value)
    {
        var lo = 0;
        var hi = table.Starts.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (value < table.Starts[mid]) { hi = mid - 1; continue; }
            if (value > table.Ends[mid]) { lo = mid + 1; continue; }
            return table.Codes[mid];
        }
        return null;
    }

    /// <summary>
    /// 检查段表里有没有「同一个地址被两个国家段覆盖」的重叠。
    /// 前缀长度相同时才需要检测（更长的段本来就该覆盖更短的）。
    /// </summary>
    public static IReadOnlyList<string> FindOverlaps()
    {
        var conflicts = new List<string>();
        var byPrefix = Ranges.GroupBy(r => r.Prefix);
        foreach (var group in byPrefix)
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (list[i].Code == list[j].Code) continue;
                    // 两段同前缀长度且网段不同 → 不重叠
                    var mask = 0xFFFF_FFFFu << (32 - group.Key);
                    if ((list[i].Network & mask) != (list[j].Network & mask))
                        continue;
                    conflicts.Add($"{list[i].Code}/{group.Key} 与 {list[j].Code}/{group.Key} 重叠");
                }
            }
        }
        return conflicts;
    }
}

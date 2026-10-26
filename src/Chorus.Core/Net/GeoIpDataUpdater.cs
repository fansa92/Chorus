using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Chorus.Core.Net;

/// <summary>
/// 全球 IPv4 → 国家/地区段表的下载与本地缓存。
/// <para>
/// 数据源是 sapics/ip-location-db 的 <c>dbip-country-ipv4.csv</c>（DB-IP Lite，CC BY 4.0），
/// 每行 <c>起址,止址,国家码</c>（点分十进制、无表头，约 20 万行）。下载后转成紧凑二进制
/// （10 字节/条）存到 <c>%LOCALAPPDATA%\Chorus\geoip-country-v4.bin</c>，之后启动直接读取，
/// 不必每次解析几 MB 的 CSV。
/// </para>
/// <para>
/// 它只服务于「出口 IP 归属地」的本地兜底（<see cref="LocalGeoLookup"/>）；
/// sing-box 的路由分流走远程 .srs 规则集，与这里无关。
/// </para>
/// </summary>
public static class GeoIpDataUpdater
{
    /// <summary>本地缓存文件（二进制）。</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Chorus", "geoip-country-v4.bin");

    /// <summary>
    /// 下载源，按顺序尝试：gh-proxy（国内实测可用）→ jsDelivr（CDN）→ GitHub 直连。
    /// 每个源单独限时，避免一个源卡死把整个更新拖到超时。
    /// </summary>
    public static readonly string[] Sources =
    [
        "https://gh-proxy.com/https://raw.githubusercontent.com/sapics/ip-location-db/main/dbip-country/dbip-country-ipv4.csv",
        "https://cdn.jsdelivr.net/gh/sapics/ip-location-db@main/dbip-country/dbip-country-ipv4.csv",
        "https://raw.githubusercontent.com/sapics/ip-location-db/main/dbip-country/dbip-country-ipv4.csv",
    ];

    /// <summary>一次成功更新的结果。</summary>
    public sealed record UpdateResult(int Count, string Source, DateTimeOffset UpdatedAt);

    private const byte FormatVersion = 1;
    private const int EntrySize = 10; // uint32 start + uint32 end + uint16 codeIndex
    private const int MinSaneEntries = 10_000; // 少于这个数说明下到的是错误页/被截断

    /// <summary>
    /// 依次尝试所有下载源，解析并落盘，最后装载进 <see cref="LocalGeoLookup"/>。
    /// 全部失败时抛出 <see cref="InvalidOperationException"/>（内层是最后一次的错误）。
    /// </summary>
    public static async Task<UpdateResult> DownloadAndSaveAsync(
        HttpClient http,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        path ??= DefaultPath;
        Exception? last = null;

        foreach (var url in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 每个源单独限时，避免某个源连不上时把整体拖到 HttpClient.Timeout
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(25));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                if (!response.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var count = ParseCsv(reader, out var starts, out var ends, out var codes);
                if (count < MinSaneEntries)
                {
                    last = new InvalidDataException($"只解析到 {count} 条，数据不完整，放弃该源");
                    continue;
                }

                WriteBinary(path, starts, ends, codes);
                LocalGeoLookup.LoadExternal(starts, ends, codes);
                return new UpdateResult(count, url, DateTimeOffset.Now);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new TimeoutException($"下载超时：{url}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("所有 GeoIP 数据源都下载失败", last);
    }

    /// <summary>
    /// 解析 <c>起址,止址,国家码</c> 的 CSV。跳过表头/注释/非法行。
    /// 输出按起始地址升序（源文件通常已有序，乱序时才重排）。
    /// </summary>
    public static int ParseCsv(
        TextReader reader,
        out uint[] starts,
        out uint[] ends,
        out string[] codes)
    {
        var startList = new List<uint>(250_000);
        var endList = new List<uint>(250_000);
        var codeList = new List<string>(250_000);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty) continue;

            var firstComma = span.IndexOf(',');
            if (firstComma <= 0) continue;
            var secondComma = span[(firstComma + 1)..].IndexOf(',');
            if (secondComma <= 0) continue;

            var startText = span[..firstComma];
            var endText = span.Slice(firstComma + 1, secondComma);
            var codeText = span[(firstComma + 1 + secondComma + 1)..].Trim();

            if (!TryParseIpv4(startText, out var start)) continue;
            if (!TryParseIpv4(endText, out var end)) continue;
            if (end < start) continue;
            if (codeText.IsEmpty || codeText.Length > 3) continue;

            startList.Add(start);
            endList.Add(end);
            codeList.Add(codeText.ToString().ToUpperInvariant());
        }

        var count = startList.Count;
        var startsArr = startList.ToArray();
        var endsArr = endList.ToArray();
        var codesArr = codeList.ToArray();

        // 二分查找要求按起始地址升序；只在发现乱序时才付排序成本
        for (var i = 1; i < count; i++)
        {
            if (startsArr[i] >= startsArr[i - 1]) continue;

            var order = Enumerable.Range(0, count).OrderBy(x => startsArr[x]).ToArray();
            var sortedStarts = new uint[count];
            var sortedEnds = new uint[count];
            var sortedCodes = new string[count];
            for (var k = 0; k < count; k++)
            {
                var j = order[k];
                sortedStarts[k] = startsArr[j];
                sortedEnds[k] = endsArr[j];
                sortedCodes[k] = codesArr[j];
            }
            startsArr = sortedStarts;
            endsArr = sortedEnds;
            codesArr = sortedCodes;
            break;
        }

        starts = startsArr;
        ends = endsArr;
        codes = codesArr;
        return count;
    }

    /// <summary>把段表写成紧凑二进制（先写临时文件再原子替换，写坏了不会留下半个文件）。</summary>
    public static void WriteBinary(string path, uint[] starts, uint[] ends, string[] codes)
    {
        if (starts.Length != ends.Length || starts.Length != codes.Length)
            throw new ArgumentException("段表三个数组长度不一致");

        var (codeTable, codeIndex) = BuildCodeTable(codes);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write([(byte)'C', (byte)'G', (byte)'E', (byte)'O']);
            writer.Write(FormatVersion);
            writer.Write((uint)starts.Length);
            writer.Write((ushort)codeTable.Count);

            foreach (var code in codeTable)
            {
                var pair = new byte[2];
                var raw = Encoding.ASCII.GetBytes(code);
                Array.Copy(raw, pair, Math.Min(2, raw.Length));
                writer.Write(pair);
            }

            for (var i = 0; i < starts.Length; i++)
            {
                writer.Write(starts[i]);
                writer.Write(ends[i]);
                writer.Write(codeIndex[i]);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>读回二进制段表；文件不存在或格式不对返回 null。</summary>
    public static (uint[] Starts, uint[] Ends, string[] Codes)? ReadBinary(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var magic = reader.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'C' || magic[1] != 'G' || magic[2] != 'E' || magic[3] != 'O')
                return null;
            if (reader.ReadByte() != FormatVersion) return null;

            var count = reader.ReadUInt32();
            var codeCount = reader.ReadUInt16();
            if (count == 0 || count > 5_000_000 || codeCount == 0) return null;

            var expected = 4 + 1 + 4 + 2 + codeCount * 2L + count * EntrySize;
            if (stream.Length < expected) return null;

            var codeTable = new string[codeCount];
            for (var i = 0; i < codeCount; i++)
            {
                var pair = reader.ReadBytes(2);
                if (pair.Length != 2) return null;
                codeTable[i] = Encoding.ASCII.GetString(pair).TrimEnd('\0');
            }

            var starts = new uint[count];
            var ends = new uint[count];
            var codes = new string[count];
            for (var i = 0; i < count; i++)
            {
                starts[i] = reader.ReadUInt32();
                ends[i] = reader.ReadUInt32();
                var codeIndex = reader.ReadUInt16();
                if (codeIndex >= codeCount) return null;
                codes[i] = codeTable[codeIndex];
            }

            return (starts, ends, codes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把本地缓存的段表装载进 <see cref="LocalGeoLookup"/>；返回装载条数（0 表示没有可用缓存）。
    /// 启动时调用，失败静默回退到内置精简表。
    /// </summary>
    public static int TryLoadIntoLookup(string? path = null)
    {
        try
        {
            if (ReadBinary(path ?? DefaultPath) is not var (starts, ends, codes) || starts.Length == 0)
                return 0;

            LocalGeoLookup.LoadExternal(starts, ends, codes);
            return starts.Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>删掉本地缓存并回到内置精简表。</summary>
    public static void RemoveCached()
    {
        try
        {
            var path = DefaultPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉不影响内存里的状态
        }
        LocalGeoLookup.ClearExternal();
    }

    /// <summary>把国家代码去重成小表，并返回每条对应的下标。</summary>
    private static (List<string> Table, ushort[] Index) BuildCodeTable(string[] codes)
    {
        var map = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var table = new List<string>();
        var index = new ushort[codes.Length];

        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            if (!map.TryGetValue(code, out var slot))
            {
                if (table.Count >= ushort.MaxValue)
                {
                    slot = 0; // 超出 65535 个国家代码不可能发生，兜底落到第一条
                }
                else
                {
                    slot = (ushort)table.Count;
                    map[code] = slot;
                    table.Add(code);
                }
            }
            index[i] = slot;
        }

        if (table.Count == 0) table.Add("ZZ");
        return (table, index);
    }

    private static bool TryParseIpv4(ReadOnlySpan<char> text, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(text, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }
}

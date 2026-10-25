using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Chorus.Core.Net;

/// <summary>
/// 最小可用的 DNS 客户端：只查 A 记录，走 UDP 53。
/// <para>
/// 为什么自己查：系统 DNS（以及阿里/DNSPod 这些国内递归）会把订阅域名解析到 Cloudflare
/// 的 104.17/104.18 段，而这段在本机被整体黑洞（TCP 443 全超时）；Quad9 反而会给出
/// 172.66 段（实测直连 173ms 可用）。所以拉订阅时要把多家 DNS 的结果取并集再逐个试。
/// </para>
/// </summary>
public static class DnsQuery
{
    /// <summary>向指定 DNS 服务器查询 A 记录。失败/超时/被截断时返回空列表（不抛异常）。</summary>
    public static async Task<IReadOnlyList<IPAddress>> QueryAAsync(
        string server, string host, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(server, out var serverIp)
            || serverIp.AddressFamily != AddressFamily.InterNetwork)
            return [];
        if (string.IsNullOrWhiteSpace(host)) return [];

        var query = BuildQuery(host, out var id);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(query, new IPEndPoint(serverIp, 53), cts.Token);
            var reply = await udp.ReceiveAsync(cts.Token);
            return ParseAnswers(reply.Buffer, id);
        }
        catch
        {
            // DNS 服务器不可达 / 超时 / 报文异常：交给调用方与其它服务器取并集
            return [];
        }
    }

    /// <summary>构造一条标准的 A 记录查询（递归期望，随机 ID）。</summary>
    private static byte[] BuildQuery(string host, out int id)
    {
        id = Random.Shared.Next(0, 65536);
        var buffer = new List<byte>(64);
        void U16(int value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value & 0xFF));
        }

        U16(id);      // ID
        U16(0x0100);  // flags: 标准查询 + 期望递归
        U16(1);       // QDCOUNT
        U16(0);       // ANCOUNT
        U16(0);       // NSCOUNT
        U16(0);       // ARCOUNT

        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) continue;
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);   // 根标签结尾
        U16(1);          // QTYPE = A
        U16(1);          // QCLASS = IN
        return [.. buffer];
    }

    /// <summary>从响应报文里取出 A 记录地址（忽略 CNAME / AAAA）。</summary>
    private static IReadOnlyList<IPAddress> ParseAnswers(byte[] data, int expectedId)
    {
        if (data.Length < 12) return [];
        if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)) != expectedId) return [];

        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        var rcode = flags & 0x000F;
        if (rcode != 0) return [];                    // NXDOMAIN / SERVFAIL / REFUSED
        if ((flags & 0x0200) != 0) return [];         // TC=1：截断，UDP 拿不到完整结果

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));

        var offset = 12;
        for (var i = 0; i < qdCount; i++)
        {
            if (!TrySkipName(data, ref offset)) return [];
            offset += 4;                              // QTYPE + QCLASS
        }

        var result = new List<IPAddress>();
        for (var i = 0; i < anCount; i++)
        {
            if (!TrySkipName(data, ref offset)) break;
            if (offset + 10 > data.Length) break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8, 2));
            offset += 10;
            if (offset + length > data.Length) break;
            if (type == 1 && length == 4)
                result.Add(new IPAddress(data.AsSpan(offset, 4)));
            offset += length;
        }
        return result.Distinct().ToList();
    }

    /// <summary>跳过域名（支持 0xC0 压缩指针）。</summary>
    private static bool TrySkipName(byte[] data, ref int offset)
    {
        while (offset < data.Length)
        {
            var length = data[offset];
            if (length == 0)
            {
                offset += 1;
                return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;                          // 压缩指针占两个字节，且指向的内容无需读取
                return true;
            }
            offset += 1 + length;
        }
        return false;
    }
}

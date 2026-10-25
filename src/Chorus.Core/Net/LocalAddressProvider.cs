using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Chorus.Core.Net;

/// <summary>本机的一块网卡地址信息（内网信息卡片用）。</summary>
/// <param name="InterfaceName">网卡名称。</param>
/// <param name="Description">网卡描述。</param>
/// <param name="Ipv4">IPv4 地址（没有则为空串）。</param>
/// <param name="Ipv6">IPv6 地址（没有则为空串）。</param>
/// <param name="IsVirtual">是否为虚拟网卡（VPN / 虚拟交换机 / 隧道）。</param>
public sealed record LocalAddress(
    string InterfaceName,
    string Description,
    string Ipv4,
    string Ipv6,
    bool IsVirtual);

/// <summary>
/// 读取本机网卡地址（内网信息）。
/// <para>
/// 只取「有实际地址」的网卡：回环（127.0.0.1 / ::1）不算内网地址，
/// 虚拟网卡（VPN/虚拟交换机）会另外标注，方便用户分辨哪个是真实内网 IP。
/// </para>
/// </summary>
public static class LocalAddressProvider
{
    /// <summary>枚举本机所有网卡的 IPv4 / IPv6 地址。</summary>
    public static IReadOnlyList<LocalAddress> Collect()
    {
        var result = new List<LocalAddress>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return result;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ipv4 = "";
            var ipv6 = "";

            try
            {
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        // 跳过 169.254.x.x（APIPA，自动私有地址）与 127.x
                        var text = addr.Address.ToString();
                        if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        if (text.StartsWith("127.", StringComparison.Ordinal)) continue;
                        if (ipv4.Length == 0) ipv4 = text;
                    }
                    else if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        var text = addr.Address.ToString();
                        // 回环与链路本地（fe80::）不算可用地址
                        if (text.StartsWith("::1", StringComparison.Ordinal)) continue;
                        if (text.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)) continue;
                        if (ipv6.Length == 0) ipv6 = text;
                    }
                }
            }
            catch
            {
                // 某些网卡在枚举地址时会抛（网卡被禁用/驱动异常），跳过即可
            }

            if (ipv4.Length == 0 && ipv6.Length == 0) continue;

            result.Add(new LocalAddress(
                nic.Name,
                nic.Description,
                ipv4,
                ipv6,
                IsVirtual(nic)));
        }

        // 真实网卡排前面：内网 IP 通常是用户关心的那个
        return result
            .OrderBy(a => a.IsVirtual)
            .ThenBy(a => a.Ipv4.Length == 0 ? 1 : 0)
            .ToList();
    }

    /// <summary>判断是不是虚拟网卡（VPN / 虚拟交换机 / 隧道）。</summary>
    private static bool IsVirtual(NetworkInterface nic)
    {
        var text = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        return text.Contains("virtual", StringComparison.Ordinal)
            || text.Contains("vpn", StringComparison.Ordinal)
            || text.Contains("tun", StringComparison.Ordinal)
            || text.Contains("tap", StringComparison.Ordinal)
            || text.Contains("hyper-v", StringComparison.Ordinal)
            || text.Contains("wintun", StringComparison.Ordinal)
            || text.Contains("loopback", StringComparison.Ordinal);
    }

    /// <summary>
    /// 取「主内网地址」：优先真实网卡的 IPv4，没有就退到 IPv6，再没有就空串。
    /// </summary>
    public static string PrimaryIpv4(IReadOnlyList<LocalAddress>? addresses = null)
        => (addresses ?? Collect()).FirstOrDefault(a => a.Ipv4.Length > 0 && !a.IsVirtual)?.Ipv4
           ?? (addresses ?? Collect()).FirstOrDefault(a => a.Ipv4.Length > 0)?.Ipv4
           ?? "";

    /// <summary>取「主 IPv6 地址」：优先真实网卡的，没有就空串。</summary>
    public static string PrimaryIpv6(IReadOnlyList<LocalAddress>? addresses = null)
        => (addresses ?? Collect()).FirstOrDefault(a => a.Ipv6.Length > 0 && !a.IsVirtual)?.Ipv6
           ?? (addresses ?? Collect()).FirstOrDefault(a => a.Ipv6.Length > 0)?.Ipv6
           ?? "";

    /// <summary>
    /// 判断一个 IP 是不是私有/内网地址（RFC1918、链路本地、回环、CGNAT）。
    /// 用来在界面上标注「这是内网地址，不是出口 IP」。
    /// </summary>
    public static bool IsPrivateAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        if (IPAddress.IsLoopback(addr)) return true;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                  // 10.0.0.0/8
                172 => bytes[1] >= 16 && bytes[1] <= 31,      // 172.16.0.0/12
                192 => bytes[1] == 168,                        // 192.168.0.0/16
                169 => bytes[1] == 254,                        // 169.254.0.0/16
                100 => bytes[1] >= 64 && bytes[1] <= 127,      // 100.64.0.0/10 (CGNAT)
                _ => false,
            };
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal) return true;
            if (addr.IsIPv6SiteLocal) return true;
            // fc00::/7 唯一本地地址
            var b = addr.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}

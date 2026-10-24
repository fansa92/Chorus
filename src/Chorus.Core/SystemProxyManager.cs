using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Chorus.Core;

/// <summary>Windows 系统代理（IE/WinINET 代理设置）开关。</summary>
[SupportedOSPlatform("windows")]
public static class SystemProxyManager
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static void Enable(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表项");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{port}");
        key.SetValue("ProxyOverride",
            "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;" +
            "172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>");
        NotifyShell();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true);
        key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyShell();
    }

    /// <summary>广播设置变更，让系统代理立即生效（否则要等应用重新读注册表）。</summary>
    public static void NotifyShell()
    {
        NativeInternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        NativeInternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool NativeInternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);
}

using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Chorus.Core.State;

namespace Chorus.Core;

/// <summary>
/// Windows 系统代理（IE/WinINET 代理设置）开关。
/// <para>
/// 开启前会先快照用户原有的设置，关闭时按快照恢复 —— 只把 ProxyEnable 置 0
/// 会留下被改过的 ProxyServer，用户原本的公司代理 / PAC 就再也回不来了。
/// 恢复前会核对当前 ProxyServer 是否仍指向本应用端口，避免用户中途自己改过代理时被覆盖。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemProxyManager
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private const string ProxyOverrideValue =
        "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;" +
        "172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    /// <summary>快照默认落盘位置，和应用设置同一个目录。</summary>
    public static string DefaultBackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Chorus", "system-proxy-backup.json");

    /// <summary>开启代理，并先把原设置存成快照。</summary>
    public static void Enable(int port) => Enable(port, DefaultBackupPath);

    public static void Enable(int port, string backupPath)
    {
        var store = new SystemProxyBackupStore(backupPath);
        var expected = $"127.0.0.1:{port}";
        // 已经由本应用开启时别重复快照，否则会把「我们自己的设置」当成用户原值存下来
        if (CurrentServer != expected || store.Load() is null)
        {
            try
            {
                store.Save(new SystemProxyBackup
                {
                    CapturedAt = DateTimeOffset.Now,
                    Port = port,
                    ProxyEnable = ReadInt("ProxyEnable"),
                    ProxyServer = ReadString("ProxyServer"),
                    ProxyOverride = ReadString("ProxyOverride"),
                    AutoConfigUrl = ReadString("AutoConfigURL"),
                });
            }
            catch
            {
                // 快照写失败不阻止开启代理，退出时退化成只关开关
            }
        }

        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表项");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", expected);
        key.SetValue("ProxyOverride", ProxyOverrideValue, RegistryValueKind.String);
        // PAC 脚本优先级高于固定代理，残留的 AutoConfigURL 会让系统不认我们的设置
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        NotifyShell();
    }

    /// <summary>
    /// 关闭代理并恢复用户原设置。只有当前代理仍指向本应用端口时才动注册表，
    /// 用户中途自己改过的话一句都不碰，只是把过期快照清掉。
    /// </summary>
    public static bool DisableWithRestore() => DisableWithRestore(DefaultBackupPath);

    public static bool DisableWithRestore(string backupPath)
    {
        var store = new SystemProxyBackupStore(backupPath);
        var backup = store.Load();
        try
        {
            if (backup is not null &&
                string.Equals(CurrentServer, backup.OwnServer, StringComparison.OrdinalIgnoreCase))
            {
                Restore(backup);
                return true;
            }
            // 没有快照（比如老版本留下的）：沿用旧行为，只把开关置 0
            if (backup is null)
                Disable();
            return false;
        }
        finally
        {
            store.Clear();
        }
    }

    /// <summary>兜底路径：无条件把代理开关置 0（不恢复原值），并清掉快照。</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true);
        key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyShell();
        try { new SystemProxyBackupStore(DefaultBackupPath).Clear(); }
        catch { /* 清理失败不影响关闭 */ }
    }

    private static void Restore(SystemProxyBackup backup)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表项");
        key.SetValue("ProxyEnable", backup.ProxyEnable ?? 0, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", backup.ProxyServer);
        SetOrDelete(key, "ProxyOverride", backup.ProxyOverride);
        SetOrDelete(key, "AutoConfigURL", backup.AutoConfigUrl);
        NotifyShell();
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value, RegistryValueKind.String);
    }

    private static int? ReadInt(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath);
        return key?.GetValue(name) is int v ? v : null;
    }

    private static string? ReadString(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath);
        return key?.GetValue(name) as string;
    }

    /// <summary>系统代理当前是否开启（读注册表中的真实状态）。</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath);
            return key?.GetValue("ProxyEnable") is int v && v != 0;
        }
    }

    /// <summary>系统代理当前指向的地址（未开启时也返回写入过的值，便于排查）。</summary>
    public static string? CurrentServer
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath);
            return key?.GetValue("ProxyServer") as string;
        }
    }

    /// <summary>
    /// 广播设置变更，让系统代理立即生效（否则要等应用重新读注册表）。
    /// wininet 导出的函数名是 InternetSetOptionW/A，必须显式指定 EntryPoint，
    /// 否则会抛 EntryPointNotFoundException —— 这正是此前「系统代理切换失败」的根因。
    /// </summary>
    public static void NotifyShell()
    {
        NativeInternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        NativeInternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeInternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);
}

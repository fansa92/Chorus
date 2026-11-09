using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Chorus.Core.Interop;

/// <summary>自启项命令行的构造与识别（纯字符串逻辑，可跨平台单测）。</summary>
public static class AutoStartCommand
{
    /// <summary>Run 项的值：带引号的可执行路径 + --start（启动即拉起核心）。</summary>
    public static string Build(string exePath)
    {
        var path = (exePath ?? "").Trim().Trim('"');
        return path.Length == 0 ? "" : $"\"{path}\" --start";
    }

    /// <summary>
    /// 判断一个 Run 项是不是本应用写的。故意放宽匹配（不比对路径），
    /// 这样用户换了解压目录后旧项依然能被识别、被正确清理，不会变成孤儿。
    /// </summary>
    public static bool IsChorusEntry(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("--start", StringComparison.OrdinalIgnoreCase)
        && value.Contains("Chorus.App.exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 开机自启：HKCU 的 Run 项。用 HKCU 而不是 HKLM，不需要管理员权限。
/// 读写函数可以注入，单测就能在不碰真实注册表的情况下覆盖开/关/冲突三种分支。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Chorus";

    private readonly string _exePath;
    private readonly Func<string, string?> _read;
    private readonly Action<string, string?> _write;

    public AutoStartManager(
        string? exePath = null,
        Func<string, string?>? read = null,
        Action<string, string?>? write = null)
    {
        _exePath = exePath ?? Environment.ProcessPath ?? "";
        _read = read ?? ReadRegistry;
        _write = write ?? WriteRegistry;
    }

    /// <summary>本进程对应的自启命令；exePath 为空时返回空串（不写无效项）。</summary>
    public string Command => AutoStartCommand.Build(_exePath);

    public bool IsEnabled() => AutoStartCommand.IsChorusEntry(_read(ValueName));

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var command = Command;
            if (command.Length == 0)
                throw new InvalidOperationException("无法确定应用路径，开机自启设置失败");
            _write(ValueName, command);
        }
        else
        {
            // 只删自己写的项：别人占用了同一个名字时绝不能顺手删掉
            if (AutoStartCommand.IsChorusEntry(_read(ValueName)))
                _write(ValueName, null);
        }
    }

    private static string? ReadRegistry(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(name) as string;
    }

    private static void WriteRegistry(string name, string? value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("无法打开开机自启注册表项");
        if (value is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value, RegistryValueKind.String);
    }
}

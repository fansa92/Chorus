using System.Reflection;

namespace Chorus.App.Services;

/// <summary>
/// 应用版本号。发布时由 CI 用 git tag（v0.1.4）注入 -p:Version，本地构建取 csproj 里的 Version，
/// 界面上只显示纯版本号（剥掉 .NET 追加的「+提交号」后缀）。
/// </summary>
public static class AppInfo
{
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(AppInfo).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        return asm.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";
    }
}

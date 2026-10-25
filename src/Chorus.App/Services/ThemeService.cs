using Chorus.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Chorus.App.Services;

/// <summary>
/// 界面主题：浅色 / 深色 / 跟随系统。
/// <para>
/// WinUI 3 里 <see cref="Application.RequestedTheme"/> 的类型是不可空的
/// <c>ApplicationTheme</c>（只有 Light / Dark），没有「跟随系统」这一档——
/// 它的默认值就是跟随系统，但一旦显式赋过值就再也回不去了。
/// <para>
/// 所以这里改用 <see cref="FrameworkElement.RequestedTheme"/>：
/// 它的类型是 <c>ElementTheme</c>，有 <c>Default</c>（= 跟随系统）这一档。
/// 把它设在窗口的根元素上，整棵内容树都会跟着换 ThemeResource。
/// </para>
/// </summary>
public static class ThemeService
{
    /// <summary>可选值：system / light / dark。</summary>
    public static readonly string[] Options = ["system", "light", "dark"];

    /// <summary>把设置里的字符串翻译成界面上的中文名。</summary>
    public static string Label(string value) => value switch
    {
        "light" => "浅色",
        "dark" => "深色",
        _ => "跟随系统",
    };

    /// <summary>设置值 → ElementTheme（Default 表示跟随系统）。</summary>
    private static ElementTheme ToElementTheme(string value) => value switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>当前已知的窗口（WinUI 3 的 Application 没有 Windows 集合，窗口自己登记）。</summary>
    private static readonly List<Window> KnownWindows = [];

    /// <summary>窗口登记（MainWindow 创建时调用）。</summary>
    public static void Register(Window window)
    {
        if (!KnownWindows.Contains(window)) KnownWindows.Add(window);
    }

    /// <summary>把设置值应用到当前所有窗口。</summary>
    /// <remarks>
    /// 注意：<see cref="Application.RequestedTheme"/> 只能在 App 构造函数里设置一次，
    /// 窗口创建后再赋值直接抛 COMException（crash.log 里 0x80131515 就是这么来的），
    /// 所以这里完全不碰应用级主题——根元素 <see cref="FrameworkElement.RequestedTheme"/>
    /// 是 ElementTheme（含 Default=跟随系统），优先级高于应用级，单靠它就够了。
    /// </remarks>
    public static void Apply(string value)
    {
        var theme = ToElementTheme(value);
        foreach (var window in KnownWindows.ToList())
        {
            if (window.Content is FrameworkElement root)
                root.RequestedTheme = theme;
        }
    }

    /// <summary>窗口创建后把主题套到它的根元素上（根元素才是「跟随系统」能生效的地方）。</summary>
    public static void ApplyToWindow(Window window, string value)
    {
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = ToElementTheme(value);
    }

    /// <summary>应用启动时调用（读设置里的主题）。</summary>
    public static void ApplyFromSettings(AppSettings settings) => Apply(settings.Theme);
}

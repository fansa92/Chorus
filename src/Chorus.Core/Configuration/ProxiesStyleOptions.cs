using Chorus.Core.Models;

namespace Chorus.Core.Configuration;

/// <summary>
/// 策略组页显示设置的选项表与取值归一化（参考 Bettbox 代理页的 ProxiesSetting）。
/// <para>
/// 这一组设置只影响「怎么展示」，不写进 sing-box 配置：改完立刻生效，不需要重启核心。
/// 取值统一用小写 ASCII 字符串，兼容手改 settings.json 时的大小写混用。
/// </para>
/// </summary>
public static class ProxiesStyleOptions
{
    // ---- 可选值（与 Bettbox 的枚举一一对应）----

    /// <summary>风格：tab（标签页）/ list（列表）。</summary>
    public static readonly string[] ViewTypes = ["tab", "list"];

    /// <summary>排序：none（默认）/ delay（延迟）/ name（名称）。</summary>
    public static readonly string[] SortTypes = ["none", "delay", "name"];

    /// <summary>布局密度：tight（紧凑）/ standard（标准）/ loose（宽松）。</summary>
    public static readonly string[] Layouts = ["tight", "standard", "loose"];

    /// <summary>卡片尺寸：expand（展开）/ shrink（标准）/ min（最小）。</summary>
    public static readonly string[] CardTypes = ["expand", "shrink", "min"];

    /// <summary>图标样式：standard（标准）/ none（无图标）/ icon（仅图标）。</summary>
    public static readonly string[] IconStyles = ["standard", "none", "icon"];

    /// <summary>默认值（也是设置缺失时的兜底）。</summary>
    public const string DefaultViewType = "tab";
    public const string DefaultSortType = "none";
    public const string DefaultLayout = "standard";
    public const string DefaultCardType = "shrink";
    public const string DefaultIconStyle = "standard";

    /// <summary>把任意输入归一化成合法值：无法识别时回落到默认值。</summary>
    public static string Normalize(string? value, string[] allowed, string fallback)
    {
        var v = value?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(allowed, v) >= 0 ? v : fallback;
    }

    /// <summary>按排序方式重排节点：延迟升序（未测速的排最后）、名称升序，其余保持原顺序。</summary>
    /// <param name="delayOf">取某个节点的延迟（毫秒）；null = 未测速。</param>
    public static IEnumerable<T> Sort<T>(IEnumerable<T> items, string sortType, Func<T, string> nameOf, Func<T, int?> delayOf)
    {
        return Normalize(sortType, SortTypes, DefaultSortType) switch
        {
            "delay" => items
                .OrderBy(x => delayOf(x) is int d && d >= 0 ? d : int.MaxValue)
                .ThenBy(nameOf, StringComparer.OrdinalIgnoreCase),
            "name" => items.OrderBy(nameOf, StringComparer.OrdinalIgnoreCase),
            _ => items,
        };
    }

    /// <summary>把设置里的展示字段全部归一到合法值（读设置时调用，防止手改出非法值）。</summary>
    public static void Sanitize(AppSettings settings)
    {
        settings.ProxiesViewType = Normalize(settings.ProxiesViewType, ViewTypes, DefaultViewType);
        settings.ProxiesSortType = Normalize(settings.ProxiesSortType, SortTypes, DefaultSortType);
        settings.ProxiesLayout = Normalize(settings.ProxiesLayout, Layouts, DefaultLayout);
        settings.ProxiesCardType = Normalize(settings.ProxiesCardType, CardTypes, DefaultCardType);
        settings.ProxiesIconStyle = Normalize(settings.ProxiesIconStyle, IconStyles, DefaultIconStyle);
    }

    // ---- 布局度量：把「风格/密度/尺寸」换算成卡片几何，UI 直接拿来用 ----
    // 都返回 double（Core 层不引用 WinUI 类型），由 App 层转成 Thickness / GridLength。

    /// <summary>网格单列最小宽度。</summary>
    public static double ItemMinWidth(string layout) => layout switch
    {
        "tight" => 210,
        "loose" => 300,
        _ => 250,
    };

    /// <summary>卡片最小高度：尺寸决定信息行数，密度再叠加一档。</summary>
    public static double ItemMinHeight(string layout, string cardType)
    {
        var card = cardType switch
        {
            "min" => 46.0,     // 单行：名称 + 延迟
            "expand" => 96.0,  // 三行：名称 / 协议+延迟 / 附加信息
            _ => 74.0,         // 标准：两行
        };
        var density = layout switch
        {
            "tight" => -6.0,
            "loose" => 10.0,
            _ => 0.0,
        };
        return Math.Max(36.0, card + density);
    }

    /// <summary>网格间距（列间距与行间距共用）。</summary>
    public static double ItemSpacing(string layout) => layout switch
    {
        "tight" => 6.0,
        "loose" => 14.0,
        _ => 10.0,
    };

    /// <summary>卡片内边距。</summary>
    public static double CardPadding(string layout) => layout switch
    {
        "tight" => 7.0,
        "loose" => 16.0,
        _ => 12.0,
    };

    /// <summary>卡片圆角。</summary>
    public static double CardCornerRadius => 12.0;

}

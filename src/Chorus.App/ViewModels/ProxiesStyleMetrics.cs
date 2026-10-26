using Chorus.App.Services;
using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Microsoft.UI.Xaml;

namespace Chorus.App.ViewModels;

/// <summary>
/// 策略组页布局度量：把设置里的「风格/密度/尺寸/图标样式」换算成 XAML 能绑定的几何值。
/// 策略组与节点卡片都从这里取，保证同一套设置驱动整页。
/// </summary>
internal static class ProxiesStyleMetrics
{
    private static AppSettings Settings => AppServices.Settings;

    public static string ViewType => ProxiesStyleOptions.Normalize(Settings.ProxiesViewType, ProxiesStyleOptions.ViewTypes, ProxiesStyleOptions.DefaultViewType);
    public static string SortType => ProxiesStyleOptions.Normalize(Settings.ProxiesSortType, ProxiesStyleOptions.SortTypes, ProxiesStyleOptions.DefaultSortType);
    public static string Layout => ProxiesStyleOptions.Normalize(Settings.ProxiesLayout, ProxiesStyleOptions.Layouts, ProxiesStyleOptions.DefaultLayout);
    public static string CardType => ProxiesStyleOptions.Normalize(Settings.ProxiesCardType, ProxiesStyleOptions.CardTypes, ProxiesStyleOptions.DefaultCardType);
    public static string IconStyle => ProxiesStyleOptions.Normalize(Settings.ProxiesIconStyle, ProxiesStyleOptions.IconStyles, ProxiesStyleOptions.DefaultIconStyle);

    public static bool IsListView => ViewType == "list";
    public static bool IsTabView => ViewType == "tab";
    public static bool AutoStickyHeader => Settings.ProxiesAutoStickyHeader;
    public static bool ShowHiddenItems => Settings.ProxiesShowHiddenItems;

    // ---- 卡片几何 ----

    public static double ItemMinWidth => ProxiesStyleOptions.ItemMinWidth(Layout);
    public static double ItemMinHeight => ProxiesStyleOptions.ItemMinHeight(Layout, CardType);
    public static double ItemSpacing => ProxiesStyleOptions.ItemSpacing(Layout);
    public static double CardPadding => ProxiesStyleOptions.CardPadding(Layout);
    public static double CardCornerRadius => ProxiesStyleOptions.CardCornerRadius;

    public static Thickness CardPaddingThickness => new(CardPadding);
    public static CornerRadius CardCorner => new(CardCornerRadius);

    // ---- 图标样式：决定卡片里显示哪些元素 ----

    /// <summary>是否显示协议徽标（「仅图标」模式下徽标让位给国旗/协议点）。</summary>
    public static bool ShowProtocolBadge => IconStyle != "icon";

    /// <summary>是否显示延迟数字。</summary>
    public static bool ShowDelayBadge => IconStyle == "standard";

    /// <summary>是否显示节点名称文字。</summary>
    public static bool ShowNodeName => IconStyle != "icon";

    /// <summary>展开尺寸下多出来的第三行（协议 + 延迟 + 分组提示）是否可见。</summary>
    public static bool ShowExtraRow => CardType == "expand";
}

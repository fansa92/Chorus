using Chorus.Core.Configuration;
using Chorus.Core.Models;
using Xunit;

namespace Chorus.Core.Tests;

/// <summary>
/// 策略组页显示设置（参考 Bettbox 代理页 ProxiesSetting）：
/// 取值归一化、排序、布局度量。这些只影响展示，不写进 sing-box 配置。
/// </summary>
public class ProxiesStyleTests
{
    private static (string Name, int? Delay) N(string name, int? delay) => (name, delay);

    [Fact]
    public void Normalize_Accepts_Known_Values_CaseInsensitively()
    {
        Assert.Equal("list", ProxiesStyleOptions.Normalize("LIST", ProxiesStyleOptions.ViewTypes, "tab"));
        Assert.Equal("delay", ProxiesStyleOptions.Normalize(" Delay ", ProxiesStyleOptions.SortTypes, "none"));
        Assert.Equal("loose", ProxiesStyleOptions.Normalize("LOOSE", ProxiesStyleOptions.Layouts, "standard"));
        Assert.Equal("min", ProxiesStyleOptions.Normalize("Min", ProxiesStyleOptions.CardTypes, "shrink"));
        Assert.Equal("icon", ProxiesStyleOptions.Normalize("ICON", ProxiesStyleOptions.IconStyles, "standard"));
    }

    [Fact]
    public void Normalize_Falls_Back_To_Default_For_Unknown_Or_Empty()
    {
        // 手改 settings.json 写出拼错的值时，必须回落到默认值而不是把 UI 选中态打空
        Assert.Equal("tab", ProxiesStyleOptions.Normalize("BOGUS", ProxiesStyleOptions.ViewTypes, "tab"));
        Assert.Equal("none", ProxiesStyleOptions.Normalize("", ProxiesStyleOptions.SortTypes, "none"));
        Assert.Equal("standard", ProxiesStyleOptions.Normalize(null, ProxiesStyleOptions.Layouts, "standard"));
        Assert.Equal("shrink", ProxiesStyleOptions.Normalize("huge", ProxiesStyleOptions.CardTypes, "shrink"));
    }

    [Fact]
    public void Sanitize_Rewrites_Illegal_Values_In_Settings()
    {
        var settings = new AppSettings
        {
            ProxiesViewType = "grid",
            ProxiesSortType = "SIZE",
            ProxiesLayout = "cozy",
            ProxiesCardType = "big",
            ProxiesIconStyle = "emoji",
        };

        ProxiesStyleOptions.Sanitize(settings);

        Assert.Equal("tab", settings.ProxiesViewType);
        Assert.Equal("none", settings.ProxiesSortType);
        Assert.Equal("standard", settings.ProxiesLayout);
        Assert.Equal("shrink", settings.ProxiesCardType);
        Assert.Equal("standard", settings.ProxiesIconStyle);
    }

    [Fact]
    public void Sanitize_Keeps_Valid_Values_Untouched()
    {
        var settings = new AppSettings
        {
            ProxiesViewType = "list",
            ProxiesSortType = "delay",
            ProxiesLayout = "tight",
            ProxiesCardType = "min",
            ProxiesIconStyle = "icon",
        };

        ProxiesStyleOptions.Sanitize(settings);

        Assert.Equal("list", settings.ProxiesViewType);
        Assert.Equal("delay", settings.ProxiesSortType);
        Assert.Equal("tight", settings.ProxiesLayout);
        Assert.Equal("min", settings.ProxiesCardType);
        Assert.Equal("icon", settings.ProxiesIconStyle);
    }

    [Fact]
    public void Default_Settings_Use_Tab_View_And_Standard_Density()
    {
        // 干净安装的默认值：标签页视图 + 标准密度 + 标准尺寸
        var s = new AppSettings();
        Assert.Equal("tab", s.ProxiesViewType);
        Assert.Equal("none", s.ProxiesSortType);
        Assert.Equal("standard", s.ProxiesLayout);
        Assert.Equal("shrink", s.ProxiesCardType);
        Assert.Equal("standard", s.ProxiesIconStyle);
    }

    [Fact]
    public void Sort_By_Delay_Puts_Tested_First_And_Untested_Last()
    {
        var items = new[]
        {
            N("untested", null),
            N("slow", 900),
            N("fast", 120),
            N("failed", -1),
            N("medium", 400),
        };

        var sorted = ProxiesStyleOptions.Sort(items, "delay", x => x.Name, x => x.Delay).ToList();

        // 有延迟值的升序在前；未测速(null)与失败(-1)都排最后
        Assert.Equal(["fast", "medium", "slow"], sorted.Take(3).Select(x => x.Name));
        Assert.Contains("untested", sorted.Skip(3).Select(x => x.Name));
        Assert.Contains("failed", sorted.Skip(3).Select(x => x.Name));
    }

    [Fact]
    public void Sort_By_Name_Is_Case_Insensitive_Alphabetical()
    {
        var items = new[] { N("beta", 10), N("Alpha", 20), N("gamma", 30) };

        var sorted = ProxiesStyleOptions.Sort(items, "name", x => x.Name, x => x.Delay).ToList();

        Assert.Equal(["Alpha", "beta", "gamma"], sorted.Select(x => x.Name));
    }

    [Fact]
    public void Sort_None_Keeps_Original_Order()
    {
        var items = new[] { N("zeta", 10), N("alpha", 900), N("mid", 400) };

        var sorted = ProxiesStyleOptions.Sort(items, "none", x => x.Name, x => x.Delay).ToList();

        Assert.Equal(["zeta", "alpha", "mid"], sorted.Select(x => x.Name));
    }

    [Fact]
    public void Sort_Unknown_Type_Falls_Back_To_Original_Order()
    {
        var items = new[] { N("zeta", 10), N("alpha", 900) };

        var sorted = ProxiesStyleOptions.Sort(items, "garbage", x => x.Name, x => x.Delay).ToList();

        Assert.Equal(["zeta", "alpha"], sorted.Select(x => x.Name));
    }

    [Theory]
    [InlineData("tight", 210, 68, 6)]
    [InlineData("standard", 250, 74, 10)]
    [InlineData("loose", 300, 84, 14)]
    public void Layout_Metrics_Scale_With_Density(string layout, double width, double height, double spacing)
    {
        Assert.Equal(width, ProxiesStyleOptions.ItemMinWidth(layout));
        Assert.Equal(height, ProxiesStyleOptions.ItemMinHeight(layout, "shrink"));
        Assert.Equal(spacing, ProxiesStyleOptions.ItemSpacing(layout));
    }

    [Theory]
    [InlineData("expand", 96)]
    [InlineData("shrink", 74)]
    [InlineData("min", 46)]
    public void Card_Size_Drives_Card_Height(string cardType, double height)
    {
        Assert.Equal(height, ProxiesStyleOptions.ItemMinHeight("standard", cardType));
    }

    [Fact]
    public void Card_Height_Never_Drops_Below_Readable_Minimum()
    {
        // 紧凑 + 最小 两个方向叠加时也不能负/过小，否则文字会被裁掉
        var h = ProxiesStyleOptions.ItemMinHeight("tight", "min");
        Assert.True(h >= 36, $"卡片高度 {h} 低于可读下限");
    }

    [Fact]
    public void Unknown_Layout_Falls_Back_To_Standard_Metrics()
    {
        Assert.Equal(ProxiesStyleOptions.ItemMinWidth("standard"), ProxiesStyleOptions.ItemMinWidth("weird"));
        Assert.Equal(ProxiesStyleOptions.ItemSpacing("standard"), ProxiesStyleOptions.ItemSpacing(""));
    }
}

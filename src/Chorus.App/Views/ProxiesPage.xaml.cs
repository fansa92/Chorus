using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Chorus.App.Services;
using Chorus.App.ViewModels;
using Chorus.Core.Models;

namespace Chorus.App.Views;

public sealed partial class ProxiesPage : Page
{
    public ProxiesViewModel Vm { get; } = AppServices.ProxiesVm;

    public ProxiesPage()
    {
        InitializeComponent();
        // 刷新放到 Loaded 之后：ViewModel 是单例，若在 OnNavigatedTo 里重建列表，
        // 上一个页面实例还没卸载的 ItemsRepeater 可能仍在监听集合，容易抛 E_UNEXPECTED。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // 核心在跑就读实时数据；没在跑时 RefreshAsync 会铺上次的快照（不再是空白页）
        _ = Vm.RefreshAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = Vm.RefreshAsync();
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        await Vm.TestAllGroupsAsync();
    }

    /// <summary>点击顶部标签：切换到对应的策略组（一次只展示一个组）。</summary>
    private void GroupChip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProxyGroupViewModel group })
            Vm.SelectGroup(group);
    }

    /// <summary>只测当前展示的这一组。</summary>
    private async void TestCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedGroup is { } group)
            await Vm.TestGroupDelayAsync(group);
    }

    /// <summary>
    /// 点击节点行：把该组切换为使用此节点。同样经 Tag 取数据项。
    /// 仅「手动选择」（Selector）组支持切换——sing-box 的 Clash API 只对
    /// Selector 组开放 PUT /proxies/{name}，URLTest/Fallback 组会 400，
    /// 列表里伪造的 GLOBAL 项会 404，因此其余组只给出提示、不发请求。
    /// </summary>
    private async void Member_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProxyMemberViewModel member })
            return;

        // 自动测速组：内核本身不给 URLTest 组开选择接口，Chorus 给它加了 selector 包装层，
        // 所以这里能点。已钉住某节点时再点同一个 = 交还内核自动选择；其余点击都是钉住/换钉。
        if (member.Group.IsAutoGroup)
        {
            var pinned = !member.Group.IsAutoSelected
                         && string.Equals(member.Group.SelectedName, member.Name, StringComparison.Ordinal);
            if (pinned)
                await Vm.ResetAutoGroupAsync(member.Group);
            else
                await Vm.SelectAsync(member.Group, member);
            return;
        }

        if (member.Group.IsSelectable)
            await Vm.SelectAsync(member.Group, member);
        else
            Vm.StatusText =
                $"{member.Group.Name}（{member.Group.TypeText}）由核心自动选择，仅「手动选择」组可手动切换节点";
    }

    /// <summary>「交还内核自动选择」按钮：把自动组恢复成内核自动挑节点。</summary>
    private async void ResetAuto_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedGroup is { } group)
            await Vm.ResetAutoGroupAsync(group);
    }

    /// <summary>列表视图里，某一组头部的「测速本组」按钮。</summary>
    private async void TestGroupFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProxyGroupViewModel group })
            await Vm.TestGroupDelayAsync(group);
    }

    /// <summary>当前打开着的显示设置面板（点选项后要就地刷新选中态）。</summary>
    private ContentDialog? _styleDialog;

    /// <summary>
    /// 「设置」按钮：打开策略组显示设置面板。
    /// <para>
    /// 参考 Bettbox 代理页 more_vert 菜单里的「设置」（<c>ProxiesSetting</c>）：
    /// 风格 / 排序 / 布局 / 尺寸 / 图标样式五组，外加组头吸顶与显示隐藏项两个开关。
    /// 面板本身用 <see cref="ContentDialog"/> 承载，选项卡片对齐 Bettbox 的
    /// <c>SettingInfoCard</c> / <c>SettingTextCard</c>（选中态描边 + 强调色）。
    /// </para>
    /// </summary>
    private async void StyleSettings_Click(object sender, RoutedEventArgs e)
    {
        // 已经开着就别再叠一个（设置面板常驻，点选项是就地刷新）
        if (_styleDialog is not null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("proxies.settings.title"),
            Content = BuildStyleSettingsPanel(),
            CloseButtonText = Loc.T("common.close"),
        };
        _styleDialog = dialog;
        // Closed 比 await ShowAsync() 更早触发（后者要等隐藏动画跑完），
        // 这里及时清掉引用，否则用户关掉面板后立刻再点「设置」会被守卫挡住
        dialog.Closed += (_, _) => _styleDialog = null;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _styleDialog = null;
        }
    }

    /// <summary>设置面板内容：标题说明 + 五组选项 + 两个开关。</summary>
    private UIElement BuildStyleSettingsPanel()
    {
        var root = new StackPanel { Spacing = 10, MinWidth = 460, MaxWidth = 520 };

        root.Children.Add(new TextBlock
        {
            Text = Vm.StyleHintText,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });

        root.Children.Add(BuildOptionSection(
            Loc.T("proxies.settings.style"),
            Vm.ViewTypeOptions,
            Vm.CurrentViewType,
            (s, v) => s.ProxiesViewType = v,
            s => s.ProxiesViewType));

        root.Children.Add(BuildOptionSection(
            Loc.T("proxies.settings.sort"),
            Vm.SortTypeOptions,
            Vm.CurrentSortType,
            (s, v) => s.ProxiesSortType = v,
            s => s.ProxiesSortType));

        root.Children.Add(BuildOptionSection(
            Loc.T("proxies.settings.layout"),
            Vm.LayoutOptions,
            Vm.CurrentLayout,
            (s, v) => s.ProxiesLayout = v,
            s => s.ProxiesLayout));

        root.Children.Add(BuildOptionSection(
            Loc.T("proxies.settings.size"),
            Vm.CardTypeOptions,
            Vm.CurrentCardType,
            (s, v) => s.ProxiesCardType = v,
            s => s.ProxiesCardType));

        // 图标样式：Bettbox 里只有列表模式才显示这一组
        var iconSection = BuildOptionSection(
            Loc.T("proxies.settings.iconStyle"),
            Vm.IconStyleOptions,
            Vm.CurrentIconStyle,
            (s, v) => s.ProxiesIconStyle = v,
            s => s.ProxiesIconStyle);
        iconSection.Visibility = Vm.IconStyleSectionVisibility;
        root.Children.Add(iconSection);

        root.Children.Add(BuildToggleRow(
            Loc.T("proxies.sticky"), Loc.T("proxies.sticky.sub"),
            Vm.AutoStickyHeader, Vm.StickyOptionVisibility,
            on => Vm.AutoStickyHeader = on));

        root.Children.Add(BuildToggleRow(
            Loc.T("proxies.hidden"), Loc.T("proxies.hidden.sub"),
            Vm.ShowHiddenItems, Visibility.Visible,
            on => Vm.ShowHiddenItems = on));

        // 五组选项 + 两个开关在列表模式下会超出窗口高度，套一层滚动，
        // 否则下面的开关被裁掉、点不到（Bettbox 的面板本身也是可滚动的）
        var scroll = new ScrollViewer
        {
            MaxHeight = 600,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = root,
        };
        return scroll;
    }

    /// <summary>一组选项：横向排列的可点卡片，选中项描边强调色（Bettbox 的选中态）。</summary>
    private UIElement BuildOptionSection(
        string title,
        IReadOnlyList<StyleOption> options,
        string current,
        Action<AppSettings, string> setter,
        Func<AppSettings, string> getter)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var option in options)
        {
            var isSelected = string.Equals(option.Value, current, StringComparison.OrdinalIgnoreCase);
            var card = new Border
            {
                Width = 132,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = isSelected
                    ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                    : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                Tag = option,
            };
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = option.Label,
                FontSize = 13,
                FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = option.Subtitle,
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });
            card.Child = text;
            card.Tapped += (_, _) =>
            {
                Vm.ApplyStyleOption(setter, getter, option.Value);
                // 就地刷新面板：重绘内容即可反映新的选中态，不关对话框
                if (_styleDialog is not null)
                    _styleDialog.Content = BuildStyleSettingsPanel();
            };
            row.Children.Add(card);
        }
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>一行开关：标题 + 副标题 + ToggleSwitch。</summary>
    private UIElement BuildToggleRow(
        string title, string subtitle, bool isOn, Visibility visibility, Action<bool> onToggled)
    {
        var grid = new Grid { ColumnSpacing = 12, Visibility = visibility };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13 });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        grid.Children.Add(text);

        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = isOn,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Toggled += (_, _) => onToggled(toggle.IsOn);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>
    /// 「自定义」按钮：弹出策略组开关面板。
    /// <para>
    /// 参考 Bettbox 代理页的「自定义」入口（Icons.tune 图标 → showGroupSwitchOptions）：
    /// 逐个策略组开关，关闭的组不写进配置。Bettbox 用的是 Flutter 的
    /// <c>AdaptiveSheetScaffold</c> 自适应底部弹层，WinUI 3 没有对应控件，
    /// 这里用 <see cref="ContentDialog"/> 承载同样的「卡片 + 开关」列表。
    /// </para>
    /// </summary>
    private async void Custom_Click(object sender, RoutedEventArgs e)
    {
        Vm.BuildGroupSwitches();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "策略组自定义",
            Content = BuildCustomPanel(),
            CloseButtonText = "完成",
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 自定义面板的内容：一个可滚动的列表，每行一个策略组 + 开关。
    /// <para>
    /// 不用 ListView + DataTemplate：WinUI 3 的 DataTemplate 没法用纯代码装载内容
    /// （LoadContent 不是 virtual，SetContentPresenter 不存在）。
    /// 这里直接手工生成行——策略组数量有限（通常几十个），一次生成完全够用。
    /// </para>
    /// </summary>
    private UIElement BuildCustomPanel()
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };

        panel.Children.Add(new TextBlock
        {
            Text = Vm.CustomHintText,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });

        var scroll = new ScrollViewer
        {
            Height = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var list = new StackPanel { Spacing = 8 };
        scroll.Content = list;

        foreach (var item in Vm.GroupSwitches)
        {
            list.Children.Add(BuildGroupSwitchRow(item));
        }

        panel.Children.Add(scroll);
        return panel;
    }

    /// <summary>一行策略组开关（对齐 Bettbox 的 CommonCard + ListTile + Switch）。</summary>
    private static Border BuildGroupSwitchRow(GroupSwitchViewModel item)
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = item.Name,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
        };

        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = item.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 开关拨动后立刻写盘
        toggle.Toggled += (sender, _) =>
        {
            var sw = sender as ToggleSwitch;
            if (sw is not null)
                AppServices.ProxiesVm.ToggleGroupSwitch(item, sw.IsOn);
        };

        Grid.SetColumn(name, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(name);
        grid.Children.Add(toggle);
        root.Child = grid;

        return root;
    }
}

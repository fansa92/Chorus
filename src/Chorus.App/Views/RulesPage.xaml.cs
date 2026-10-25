using Chorus.App.Services;
using Chorus.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Chorus.App.Views;

public sealed partial class RulesPage : Page
{
    public RulesViewModel Vm { get; } = AppServices.RulesVm;

    public RulesPage()
    {
        InitializeComponent();
        // 不要在 OnNavigatedTo 里改数据：那时页面还没 Loaded，旧页面（如果是同一个
        // 单例 ViewModel）的 ItemsRepeater 可能还挂着集合通知，改动会抛 E_UNEXPECTED。
        // 放到 Loaded 之后，绑定就位、旧页面的 repeater 也已经卸载。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // 每次进页面都重读配置：用户可能刚改过订阅或设置
        Vm.Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm.Refresh();

    /// <summary>点击顶部标签切换 路由规则 / 规则集 / DNS。</summary>
    private void Tab_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RulesTabViewModel tab })
            Vm.SelectTab(tab.Index);
    }

    /// <summary>点规则行展开原始 JSON（再点收起）。</summary>
    private void Rule_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleRowViewModel row })
            row.IsExpanded = !row.IsExpanded;
    }

    /// <summary>规则集没有独立的下载接口，只能清缓存 + 重启核心，所以先确认再执行。</summary>
    private async void RefreshRuleSets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重新下载规则集",
            Content = "sing-box 没有单独刷新规则集的接口，需要清空规则集缓存并重启核心，"
                      + "核心启动时会重新下载所有远程 .srs（走 download_detour 指定的出站）。继续吗？",
            PrimaryButtonText = "清缓存并重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var message = await Vm.RefreshRuleSetsAsync();
        var result = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重新下载规则集",
            Content = message,
            CloseButtonText = "知道了",
        };
        await result.ShowAsync();
    }
}

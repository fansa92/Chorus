using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Chorus.App.Services;
using Chorus.App.ViewModels;

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
        if (sender is FrameworkElement { Tag: ProxyMemberViewModel member })
        {
            if (member.Group.IsSelectable)
                await Vm.SelectAsync(member.Group, member);
            else
                Vm.StatusText =
                    $"{member.Group.Name}（{member.Group.TypeText}）由核心自动选择，仅「手动选择」组可手动切换节点";
        }
    }
}

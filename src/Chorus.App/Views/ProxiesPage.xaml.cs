using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

public sealed partial class ProxiesPage : Page
{
    public ProxiesViewModel Vm { get; } = AppServices.ProxiesVm;

    public ProxiesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (AppServices.Runner.IsRunning)
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

    private async void TestSelectedGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedGroup is not null)
            await Vm.TestGroupDelayAsync(Vm.SelectedGroup);
    }

    /// <summary>
    /// 点击横向选择器里的策略组 chip。
    /// 注意：ItemTemplate 使用 x:Bind 编译期绑定，ItemsRepeater 不会给模板元素
    /// 设置 DataContext（整条祖先链均为 null），因此不能依赖 DataContext 取数据项；
    /// 改为在 XAML 里用 Tag="{x:Bind}" 把数据项本身绑到 Border.Tag 上取用。
    /// </summary>
    private void Group_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProxyGroupViewModel group }
            && !ReferenceEquals(group, Vm.SelectedGroup))
        {
            Vm.SelectedGroup = group;
        }
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

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

    private async void TestGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProxyGroupViewModel group)
            await Vm.TestGroupDelayAsync(group);
    }

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProxyGroupViewModel group)
            group.IsExpanded = !group.IsExpanded;
    }

    private async void Member_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProxyMemberViewModel member)
            await Vm.SelectAsync(member.Group, member);
    }
}

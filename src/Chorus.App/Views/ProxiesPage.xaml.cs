using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private async void Member_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: ProxyMemberViewModel member }
            && member.Group.SelectedName != member.Name)
        {
            await Vm.SelectAsync(member.Group, member);
        }
    }
}

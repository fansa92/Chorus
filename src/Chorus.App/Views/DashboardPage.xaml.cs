using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel Vm { get; } = AppServices.DashboardVm;

    public DashboardPage()
    {
        InitializeComponent();
    }

    private async void ToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggleSwitch) return;
        if (toggleSwitch.IsOn == Vm.Running) return;
        await Vm.ToggleAsync(toggleSwitch.IsOn);
    }

    private void GoProxies_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ProxiesPage));
    }
}

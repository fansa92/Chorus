using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; } = AppServices.SettingsVm;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        await Vm.ImportAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Vm.Save();
    }
}

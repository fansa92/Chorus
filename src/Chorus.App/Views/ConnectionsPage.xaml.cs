using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

/// <summary>
/// 连接管理页。ViewModel 是全局单例，页面切走时关掉轮询，
/// 免得用户在别的页面上还每 2 秒打一次 Clash API。
/// </summary>
public sealed partial class ConnectionsPage : Page
{
    public ConnectionsViewModel Vm { get; } = AppServices.ConnectionsVm;

    public ConnectionsPage()
    {
        InitializeComponent();
        SearchBox.Text = Vm.SearchText;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Vm.Active = true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Vm.Active = false;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Vm.SearchText = SearchBox.Text;

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConnectionRowViewModel row })
            await Vm.CloseAsync(row);
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e) =>
        Vm.CloseAllCommand.Execute(null);
}

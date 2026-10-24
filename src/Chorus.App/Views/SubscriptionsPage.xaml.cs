using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

public sealed partial class SubscriptionsPage : Page
{
    public SubscriptionsViewModel Vm { get; } = AppServices.SubscriptionsVm;

    public SubscriptionsPage()
    {
        InitializeComponent();
    }

    private async void Add_Click(object sender, RoutedEventArgs e) => await Vm.AddAsync();

    private async void UpdateAll_Click(object sender, RoutedEventArgs e) => await Vm.UpdateAllAsync();

    private async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SubscriptionItemViewModel item)
            await Vm.UpdateAsync(item);
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SubscriptionItemViewModel item)
            Vm.Remove(item);
    }
}

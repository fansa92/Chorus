using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;

namespace Chorus.App.Views;

public sealed partial class LogsPage : Page
{
    public LogsViewModel? Vm => AppServices.LogsVm;

    public LogsPage()
    {
        InitializeComponent();
        Vm.Lines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 })
                LogList.ScrollIntoView(e.NewItems[^1]);
        };
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Vm.Clear();
    }
}

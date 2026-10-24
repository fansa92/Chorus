using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Chorus.App.Services;
using Chorus.App.Views;

namespace Chorus.App;

public sealed partial class MainWindow : Window
{
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Chorus";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            TrayIcon.IconSource = new BitmapImage(new Uri(iconPath));
        }
        catch
        {
            // 图标加载失败不影响主功能
        }

        ContentFrame.Navigate(typeof(DashboardPage));
        NavView.SelectedItem = NavView.MenuItems[0];

        AppWindow.Closing += (s, e) =>
        {
            if (!_exiting && AppServices.Settings.CloseToTray)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        var pageType = tag switch
        {
            "proxies" => typeof(ProxiesPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType);
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Show();
        Activate();
    }

    private async void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        try
        {
            await AppServices.Runner.StopAsync();
        }
        catch
        {
            // 退出时的清理失败不阻止退出
        }
        TrayIcon.Dispose();
        Application.Current.Exit();
    }
}

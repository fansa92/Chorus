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
            "subscriptions" => typeof(SubscriptionsPage),
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
        if (_exiting) return;
        _exiting = true;
        try
        {
            // 停止核心可能卡住（如核心无响应），加超时兜底，避免退出流程被挂起
            await AppServices.Runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 退出时的清理失败不阻止退出
        }
        try { AppServices.Core.Stop(); }
        catch
        {
            // 核心可能已退出
        }
        try { TrayIcon.Dispose(); }
        catch
        {
            // 托盘图标释放失败不影响退出
        }
        try { Close(); }
        catch
        {
            // 窗口可能已关闭
        }
        Application.Current.Exit();
        // 兜底：确保进程真正退出（WinUI 的 Exit() 只是请求关闭，遇到挂起的后台任务时可能不终止）
        Environment.Exit(0);
    }
}

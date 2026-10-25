using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
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

        // 托盘交互：
        // - 左键 / 双击托盘图标显示主窗口（NoLeftClickDelay=True 让左键立即响应，不等双击判定）
        // - 菜单项必须用 Command：H.NotifyIcon 默认 PopupMenu 模式下，
        //   原生菜单点击只调用 MenuFlyoutItem.Command，Click 事件不会触发
        var showCommand = new RelayCommand(ShowWindow);
        var exitCommand = new RelayCommand(ExitApp);
        TrayIcon.LeftClickCommand = showCommand;
        TrayIcon.DoubleClickCommand = showCommand;
        TrayShowItem.Command = showCommand;
        TrayExitItem.Command = exitCommand;

        ContentFrame.Navigate(typeof(DashboardPage));
        NavList.SelectedIndex = 0;

        // 支持 --start 启动即拉起核心（开机自启 / 脚本化启动用；不加参数时行为不变）
        if (Environment.GetCommandLineArgs().Skip(1)
                .Any(a => a.Equals("--start", StringComparison.OrdinalIgnoreCase)))
            _ = StartCoreOnLaunchAsync();

        AppWindow.Closing += (s, e) =>
        {
            if (!_exiting && AppServices.Settings.CloseToTray)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    /// <summary>--start：窗口起来后自动启动核心（等界面完成首帧，避免和布局抢 UI 线程）。</summary>
    private async Task StartCoreOnLaunchAsync()
    {
        try
        {
            await Task.Delay(800);
            await AppServices.DashboardVm.ToggleAsync(true);
        }
        catch (Exception ex)
        {
            AppServices.DashboardVm.StatusText = "自动启动失败：" + ex.Message;
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)    {
        if (NavList.SelectedItem is not ListViewItem item || item.Tag is not string tag) return;
        var pageType = tag switch
        {
            "subscriptions" => typeof(SubscriptionsPage),
            "proxies" => typeof(ProxiesPage),
            "rules" => typeof(RulesPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType);
    }

    /// <summary>从托盘恢复主窗口：显示、激活并尽量带到最前。</summary>
    private void ShowWindow()
    {
        // 托盘命令可能在非 UI 线程触发，统一 Post 回 UI 线程操作窗口对象
        Ui.Post(() =>
        {
            try
            {
                AppWindow.Show();
                Activate();
                // 窗口被隐藏过再显示时，Activate 不一定能抢到前台，用经典技巧兜底
                ForceForegroundWindow(GetWindowHandle(this));
            }
            catch
            {
                // 恢复窗口失败不崩溃（窗口可能正在关闭）
            }
        });
    }

    /// <summary>托盘菜单「退出」。</summary>
    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        // 统一回 UI 线程执行：TrayIcon.Dispose()/Close() 等需要 UI 线程
        Ui.Post(() => _ = ExitAsync());
    }

    private async Task ExitAsync()
    {
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

    private static nint GetWindowHandle(Window window) =>
        WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>
    /// 经典的强制前台窗口技巧：临时把线程输入挂到当前前台窗口所在线程，
    /// 绕过 Windows 的前台锁限制，保证从托盘恢复时窗口能跳到最前。
    /// </summary>
    private static void ForceForegroundWindow(nint hwnd)
    {
        if (hwnd == 0) return;
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return;
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread != currentThread)
        {
            AttachThreadInput(currentThread, foregroundThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(currentThread, foregroundThread, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}

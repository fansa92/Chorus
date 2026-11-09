using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Chorus.App.Services;
using Chorus.App.ViewModels;
using Chorus.App.Views;
using Chorus.Core;

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

        // 主题套到根元素上：应用级没有「跟随系统」这一档，只有根元素的
        // ElementTheme.Default 才是真正的跟随系统。
        ThemeService.Register(this);
        ThemeService.ApplyToWindow(this, AppServices.Settings.Theme);

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
        // - 菜单项在 XAML 里静态定义：PopupMenu 模式生成的是原生菜单，
        //   Opening 事件不触发，运行时增删 Items 会让菜单变空。这里只绑 Command、
        //   之后按需刷新文本。
        var showCommand = new RelayCommand(ShowWindow);
        TrayIcon.LeftClickCommand = showCommand;
        TrayIcon.DoubleClickCommand = showCommand;
        WireTrayCommands();
        RefreshTrayMenu();

        ContentFrame.Navigate(typeof(DashboardPage));
        NavList.SelectedIndex = 0;

        // 支持 --start 启动即拉起核心（开机自启 / 脚本化启动用；不加参数时行为不变）
        if (Environment.GetCommandLineArgs().Skip(1)
                .Any(a => a.Equals("--start", StringComparison.OrdinalIgnoreCase)))
            _ = StartCoreOnLaunchAsync();

        // 上次是被强杀的：启动阶段已经把残留系统代理恢复成用户原设置，这里告知一声
        if (AppServices.StaleProxyCleaned)
            Ui.Post(() => AppServices.DashboardVm.ShowHint(Loc.T("tray.proxyRestored")));

        Closed += (_, _) => AppServices.SubscriptionsVm.StopAutomaticUpdates();
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
            "connections" => typeof(ConnectionsPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType);
    }

    /// <summary>
    /// 给静态定义的托盘菜单项绑 Command。命令里读的是**实时状态**，
    /// 不在创建时捕获，这样即使菜单文本没及时刷新也不会点错。
    /// </summary>
    private void WireTrayCommands()
    {
        var vm = AppServices.DashboardVm;
        TrayToggleItem.Command = new RelayCommand(() => _ = vm.ToggleAsync(!AppServices.Runner.IsRunning));
        TrayRuleItem.Command = new RelayCommand(() => _ = vm.SetModeAsync("rule"));
        TrayGlobalItem.Command = new RelayCommand(() => _ = vm.SetModeAsync("global"));
        TrayDirectItem.Command = new RelayCommand(() => _ = vm.SetModeAsync("direct"));
        TraySystemProxyItem.Command = new RelayCommand(() => _ = vm.ToggleSystemProxyAsync(!vm.SystemProxyOn));
        TrayTunItem.Command = new RelayCommand(() => vm.ToggleTun(!vm.TunOn));
        TrayAllowLanItem.Command = new RelayCommand(() => vm.ToggleAllowLan(!vm.AllowLanOn));
        TrayShowItem.Command = new RelayCommand(ShowWindow);
        TrayConnectionsItem.Command = new RelayCommand(() => NavigateTo("connections"));
        TrayProxiesItem.Command = new RelayCommand(() => NavigateTo("proxies"));
        TraySettingsItem.Command = new RelayCommand(() => NavigateTo("settings"));
        TrayExitItem.Command = new RelayCommand(ExitApp);

        // PopupMenu 模式下 Opening 不一定触发，不能只靠它刷文案
        AppServices.Runner.StatusChanged += _ => Ui.Post(RefreshTrayMenu);
        Loc.LanguageChanged += () => Ui.Post(RefreshTrayMenu);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DashboardViewModel.SystemProxyOn)
                or nameof(DashboardViewModel.TunOn)
                or nameof(DashboardViewModel.AllowLanOn)
                or nameof(DashboardViewModel.ConnectionCount)
                or nameof(DashboardViewModel.RuleMode)
                or nameof(DashboardViewModel.GlobalMode)
                or nameof(DashboardViewModel.DirectMode))
                Ui.Post(RefreshTrayMenu);
        };
    }

    private void TrayMenu_Opening(object sender, object e) => RefreshTrayMenu();

    /// <summary>
    /// 只改文本与可用状态，绝不增删 Items —— H.NotifyIcon 的原生菜单是按
    /// 初始化时的 Items 生成的，运行时增删会让菜单整个消失。
    /// </summary>
    private void RefreshTrayMenu()
    {
        var vm = AppServices.DashboardVm;
        var running = AppServices.Runner.IsRunning;

        TrayStatusItem.Text = running
            ? Loc.T("tray.status.running", vm.ConnectionCount)
            : Loc.T("tray.status.stopped");
        TrayToggleItem.Text = running ? Loc.T("tray.stop") : Loc.T("tray.start");
        TrayModeHeader.Text = Loc.T("tray.mode");

        TrayRuleItem.Text = Mark(vm.IsMode("rule")) + Loc.T("tray.mode.rule");
        TrayGlobalItem.Text = Mark(vm.IsMode("global")) + Loc.T("tray.mode.global");
        TrayDirectItem.Text = Mark(vm.IsMode("direct")) + Loc.T("tray.mode.direct");
        TrayRuleItem.IsEnabled = running;
        TrayGlobalItem.IsEnabled = running;
        TrayDirectItem.IsEnabled = running;

        TraySystemProxyItem.Text = Mark(vm.SystemProxyOn) + Loc.T("tray.systemProxy");
        TrayTunItem.Text = Mark(vm.TunOn) + Loc.T("tray.tun");
        TrayAllowLanItem.Text = Mark(vm.AllowLanOn) + Loc.T("tray.allowLan");

        TrayShowItem.Text = Loc.T("tray.show");
        TrayConnectionsItem.Text = Loc.T("nav.connections");
        TrayProxiesItem.Text = Loc.T("nav.proxies");
        TraySettingsItem.Text = Loc.T("nav.settings");
        TrayExitItem.Text = Loc.T("tray.exit");
    }

    private static string Mark(bool on) => on ? "● " : "○ ";

    /// <summary>托盘里跳到某个页面：走导航列表的选中态，保证侧栏高亮同步。</summary>
    private void NavigateTo(string tag)
    {
        Ui.Post(() =>
        {
            foreach (var entry in NavList.Items)
            {
                if (entry is ListViewItem { Tag: string t } && t == tag)
                {
                    NavList.SelectedItem = entry;
                    break;
                }
            }
            ShowWindow();
        });
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
        AppServices.SubscriptionsVm.StopAutomaticUpdates();
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
        // 兜底：StopAsync 超时或失败时，只要系统代理还指向本应用端口，
        // 就按快照恢复用户原本的设置，别让整机断网留到下次开机。
        try
        {
            var expected = $"127.0.0.1:{AppServices.Settings.MixedPort}";
            if (string.Equals(SystemProxyManager.CurrentServer, expected, StringComparison.OrdinalIgnoreCase))
                SystemProxyManager.DisableWithRestore();
        }
        catch
        {
            // 退出阶段的清理失败不阻止退出
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

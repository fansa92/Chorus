using System.Security.Cryptography;
using Chorus.App.ViewModels;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Models;
using Chorus.Core.State;

namespace Chorus.App.Services;

/// <summary>组合根：服务与 ViewModel 的单例容器。</summary>
public static class AppServices
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chorus");

    /// <summary>便携模式：exe 旁边的 core\ 目录优先。</summary>
    public static string PortableCoreDir => Path.Combine(AppContext.BaseDirectory, "core");

    public static string LocalCoreDir => Path.Combine(DataDir, "core");

    public static AppSettings Settings { get; private set; } = null!;
    public static SettingsStore Store { get; } = new();
    public static SubscriptionService Subscriptions { get; } = new();

    /// <summary>上一次读到的策略组快照：核心未运行时策略组页用它兜底（Bettbox 靠常驻核心，Chorus 靠快照）。</summary>
    public static ProxySnapshotStore ProxySnapshots { get; } =
        new(Path.Combine(DataDir, "proxies-cache.json"));

    public static ClashApiClient Clash { get; private set; } = null!;
    public static SingBoxCoreManager Core { get; private set; } = null!;
    public static AppRunner Runner { get; private set; } = null!;

    public static DashboardViewModel DashboardVm { get; private set; } = null!;
    public static SubscriptionsViewModel SubscriptionsVm { get; private set; } = null!;
    public static ProxiesViewModel ProxiesVm { get; private set; } = null!;
    public static RulesViewModel RulesVm { get; private set; } = null!;
    public static LogsViewModel LogsVm { get; private set; } = null!;
    public static SettingsViewModel SettingsVm { get; private set; } = null!;

    public static void Initialize()
    {
        Settings = Store.Load();
        if (string.IsNullOrWhiteSpace(Settings.ClashApiSecret))
        {
            Settings.ClashApiSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            Store.Save(Settings);
        }

        // 老版本没有「当前使用的订阅」这个概念：有订阅但没选中时默认选第一个，
        // 之后订阅页就能像 Bettbox 一样点击卡片切换配置
        if (Settings.ActiveSubscription() is null && Settings.Subscriptions.Count > 0)
        {
            Settings.EnsureActiveSubscription();
            Store.Save(Settings);
        }

        CleanupStaleSystemProxy();

        Clash = new ClashApiClient();
        Clash.Configure("127.0.0.1", Settings.ClashApiPort, Settings.ClashApiSecret);
        Core = new SingBoxCoreManager();
        Runner = new AppRunner();

        // 订阅地址基本都在墙外：核心在运行时经 mixed 端口拉取，未运行时才直连
        Subscriptions.UserAgent = Settings.SubscriptionUserAgent;
        Subscriptions.ProxyProvider = () =>
            Runner.IsRunning && Settings.MixedPort > 0
                ? ("127.0.0.1", Settings.MixedPort)
                : null;

        DashboardVm = new DashboardViewModel();
        SubscriptionsVm = new SubscriptionsViewModel();
        ProxiesVm = new ProxiesViewModel();
        RulesVm = new RulesViewModel();
        LogsVm = new LogsViewModel();
        SettingsVm = new SettingsViewModel();
    }

    /// <summary>
    /// 上次进程被强杀或崩溃时，系统代理会残留并指向已经停止的核心（整机断网）。
    /// 启动阶段把「指向本应用核心」的残留代理关掉，核心起来后会按设置重新开启。
    /// </summary>
    private static void CleanupStaleSystemProxy()
    {
        try
        {
            // 已有另一个实例在跑时，那可能是它在用的代理，绝不能动
            var others = System.Diagnostics.Process.GetProcessesByName("Chorus.App")
                .Any(p => p.Id != Environment.ProcessId);
            if (others) return;

            var expected = $"127.0.0.1:{Settings.MixedPort}";
            if (SystemProxyManager.IsEnabled && SystemProxyManager.CurrentServer == expected)
                SystemProxyManager.Disable();
        }
        catch
        {
            // 清理失败不影响启动
        }
    }

    public static string ResolveCorePath()
    {
        var portable = Path.Combine(PortableCoreDir, "sing-box.exe");
        if (File.Exists(portable)) return portable;
        return Path.Combine(LocalCoreDir, "sing-box.exe");
    }
}

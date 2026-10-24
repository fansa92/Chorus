using System.Security.Cryptography;
using Chorus.App.ViewModels;
using Chorus.Core;
using Chorus.Core.ClashApi;
using Chorus.Core.Models;

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
    public static ClashApiClient Clash { get; private set; } = null!;
    public static SingBoxCoreManager Core { get; private set; } = null!;
    public static AppRunner Runner { get; private set; } = null!;

    public static DashboardViewModel DashboardVm { get; private set; } = null!;
    public static SubscriptionsViewModel SubscriptionsVm { get; private set; } = null!;
    public static ProxiesViewModel ProxiesVm { get; private set; } = null!;
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

        Clash = new ClashApiClient();
        Clash.Configure("127.0.0.1", Settings.ClashApiPort, Settings.ClashApiSecret);
        Core = new SingBoxCoreManager();
        Runner = new AppRunner();

        DashboardVm = new DashboardViewModel();
        SubscriptionsVm = new SubscriptionsViewModel();
        ProxiesVm = new ProxiesViewModel();
        LogsVm = new LogsViewModel();
        SettingsVm = new SettingsViewModel();
    }

    public static string ResolveCorePath()
    {
        var portable = Path.Combine(PortableCoreDir, "sing-box.exe");
        if (File.Exists(portable)) return portable;
        return Path.Combine(LocalCoreDir, "sing-box.exe");
    }
}

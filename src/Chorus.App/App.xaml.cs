using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Chorus.App.Services;

namespace Chorus.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private Window? _window;

    public App()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain", e.ExceptionObject as Exception);
        UnhandledException += (_, e) =>
        {
            WriteCrashLog("Xaml", e.Exception);
            e.Handled = false;
        };

        _singleInstanceMutex = new Mutex(true, @"Local\Chorus.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            if (_singleInstanceMutex is null)
            {
                Exit();
                return;
            }

            Ui.Initialize(DispatcherQueue.GetForCurrentThread());
            AppServices.Initialize();

            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnLaunched", ex);
            throw;
        }
    }

    internal static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chorus");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 崩溃日志写入失败时不做任何事
        }
    }
}

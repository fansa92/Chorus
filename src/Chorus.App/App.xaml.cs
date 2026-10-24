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
        _singleInstanceMutex = new Mutex(true, @"Local\Chorus.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
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
}

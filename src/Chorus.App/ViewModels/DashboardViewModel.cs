using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.Utils;

namespace Chorus.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _statusText = "未启动";
    [ObservableProperty] private string _uploadText = "0 B";
    [ObservableProperty] private string _downloadText = "0 B";
    [ObservableProperty] private string _coreInfo = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _hasError;

    private bool _toggling;

    public DashboardViewModel()
    {
        var corePath = AppServices.ResolveCorePath();
        CoreInfo = File.Exists(corePath)
            ? $"核心：{corePath}"
            : $"未找到核心：{corePath}\n请下载 sing-box.exe（Windows amd64 版）放到该目录，然后重启应用。";

        AppServices.Runner.StatusChanged += running => Ui.Post(() =>
        {
            Running = running;
            if (!running)
            {
                UploadText = "0 B";
                DownloadText = "0 B";
            }
        });
        AppServices.Runner.StatusTextChanged += text => Ui.Post(() => StatusText = text);
        AppServices.Runner.TrafficUpdated += sample => Ui.Post(() =>
        {
            UploadText = BytesHumanize.Format(sample.Up);
            DownloadText = BytesHumanize.Format(sample.Down);
        });
    }

    public async Task ToggleAsync(bool turnOn)
    {
        if (_toggling) return;
        _toggling = true;
        HasError = false;
        try
        {
            if (turnOn)
                await AppServices.Runner.StartAsync();
            else
                await AppServices.Runner.StopAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
        }
        finally
        {
            _toggling = false;
        }
    }
}

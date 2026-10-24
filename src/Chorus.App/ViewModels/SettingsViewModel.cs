using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;
using Chorus.Core.Models;
using Chorus.Core.Parsing;

namespace Chorus.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _mixedPortText = "7897";
    [ObservableProperty] private string _clashApiPortText = "9090";
    [ObservableProperty] private bool _enableTun;
    [ObservableProperty] private bool _enableSystemProxy;
    [ObservableProperty] private bool _allowLan;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private string _logLevel = "info";
    [ObservableProperty] private string _subscriptionUrl = "";
    [ObservableProperty] private string _shareLinks = "";
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _messageTitle = "";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    public string[] LogLevels { get; } = ["debug", "info", "warn", "error"];

    public SettingsViewModel() => LoadFrom(AppServices.Settings);

    private void LoadFrom(AppSettings settings)
    {
        _mixedPortText = settings.MixedPort.ToString();
        _clashApiPortText = settings.ClashApiPort.ToString();
        _enableTun = settings.EnableTun;
        _enableSystemProxy = settings.EnableSystemProxy;
        _allowLan = settings.AllowLan;
        _closeToTray = settings.CloseToTray;
        _logLevel = settings.LogLevel;
        _subscriptionUrl = settings.Subscriptions.FirstOrDefault() ?? "";
        OnPropertyChanged(string.Empty);
    }

    public bool Save()
    {
        if (!int.TryParse(MixedPortText.Trim(), out var mixedPort) || mixedPort is < 1 or > 65535)
        {
            Show("错误", "混合端口无效（1-65535）", InfoBarSeverity.Error);
            return false;
        }
        if (!int.TryParse(ClashApiPortText.Trim(), out var apiPort) || apiPort is < 1 or > 65535)
        {
            Show("错误", "Clash API 端口无效（1-65535）", InfoBarSeverity.Error);
            return false;
        }

        var settings = AppServices.Settings;
        settings.MixedPort = mixedPort;
        settings.ClashApiPort = apiPort;
        settings.EnableTun = EnableTun;
        settings.EnableSystemProxy = EnableSystemProxy;
        settings.AllowLan = AllowLan;
        settings.CloseToTray = CloseToTray;
        settings.LogLevel = LogLevel;
        var url = SubscriptionUrl.Trim();
        settings.Subscriptions = url.Length == 0 ? [] : [url];
        AppServices.Store.Save(settings);

        Show("已保存", "设置已保存。若核心正在运行，重启核心后生效。", InfoBarSeverity.Success);
        return true;
    }

    public async Task ImportAsync()
    {
        var nodes = new List<NodeEntry>();
        var errors = new List<string>();
        try
        {
            var url = SubscriptionUrl.Trim();
            if (url.Length > 0)
            {
                var (fetched, fetchErrors) = await AppServices.Subscriptions.ImportFromUrlAsync(url);
                nodes.AddRange(fetched);
                errors.AddRange(fetchErrors);
            }
            if (ShareLinks.Trim().Length > 0)
            {
                var (parsed, parseErrors) = SubscriptionParser.Parse(ShareLinks);
                nodes.AddRange(parsed);
                errors.AddRange(parseErrors);
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        if (nodes.Count > 0)
        {
            var settings = AppServices.Settings;
            settings.Profile.Nodes = nodes;
            AppServices.Store.Save(settings);
        }

        var summary = $"导入 {nodes.Count} 个节点，失败 {errors.Count} 条。";
        Show(
            nodes.Count > 0 ? "导入完成" : "导入失败",
            errors.Count > 0 ? summary + " 首个错误：" + errors[0] : summary,
            nodes.Count > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void Show(string title, string text, InfoBarSeverity severity)
    {
        MessageTitle = title;
        MessageText = text;
        MessageSeverity = severity;
        HasMessage = true;
    }
}

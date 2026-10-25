using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;
using Chorus.Core;
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
    [ObservableProperty] private string _subscriptionUserAgent = SubscriptionService.DefaultUserAgent;
    [ObservableProperty] private bool _applySubscriptionRouting = true;
    [ObservableProperty] private string _shareLinks = "";
    [ObservableProperty] private int _manualNodeCount;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _messageTitle = "";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    public string[] LogLevels { get; } = ["debug", "info", "warn", "error"];

    /// <summary>常见订阅 UA：转换服务按 UA 决定返回 sing-box JSON 还是 Clash YAML。</summary>
    public string[] UserAgents { get; } =
    [
        SubscriptionService.DefaultUserAgent,
        "clash-verge/v2.0.0",
        "clash.meta",
        "ClashforWindows/0.20.39",
        "v2rayN/6.31",
        "Shadowrocket/2.2.9",
    ];

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
        _subscriptionUserAgent = settings.SubscriptionUserAgent;
        _applySubscriptionRouting = settings.ApplySubscriptionRouting;
        _manualNodeCount = settings.Profile.Nodes.Count;
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
        var agent = SubscriptionUserAgent.Trim();
        settings.SubscriptionUserAgent = agent.Length > 0 ? agent : SubscriptionService.DefaultUserAgent;
        AppServices.Subscriptions.UserAgent = settings.SubscriptionUserAgent;
        settings.ApplySubscriptionRouting = ApplySubscriptionRouting;
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
            // 手动节点追加到现有列表（不再覆盖订阅带来的节点）
            var settings = AppServices.Settings;
            settings.Profile.Nodes.AddRange(nodes);
            AppServices.Store.Save(settings);
            ManualNodeCount = settings.Profile.Nodes.Count;
        }

        var summary = $"导入 {nodes.Count} 个节点，失败 {errors.Count} 条。当前手动节点共 {ManualNodeCount} 个。";
        Show(
            nodes.Count > 0 ? "导入完成" : "导入失败",
            errors.Count > 0 ? summary + " 首个错误：" + errors[0] : summary,
            nodes.Count > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    /// <summary>清空手动添加的节点（不影响订阅节点）。</summary>
    public void ClearManualNodes()
    {
        var settings = AppServices.Settings;
        if (settings.Profile.Nodes.Count == 0)
        {
            Show("提示", "当前没有手动节点", InfoBarSeverity.Informational);
            return;
        }
        settings.Profile.Nodes.Clear();
        AppServices.Store.Save(settings);
        ManualNodeCount = 0;
        Show("已清空", "手动节点已清空（订阅节点不受影响）。若核心正在运行，重启核心后生效。", InfoBarSeverity.Informational);
    }

    private void Show(string title, string text, InfoBarSeverity severity)
    {
        MessageTitle = title;
        MessageText = text;
        MessageSeverity = severity;
        HasMessage = true;
    }
}

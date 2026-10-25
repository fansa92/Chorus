using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;
using Chorus.Core;
using Chorus.Core.Models;
using Chorus.Core.Parsing;

namespace Chorus.App.ViewModels;

/// <summary>
/// 设置页的 ViewModel（排布与交互参考 Bettbox 的设置页）：
/// 分组卡片 + 行式条目，**每项改动立即落盘**，所以没有「保存设置」按钮；
/// 端口 / User-Agent 这类文本项点整行弹对话框输入，校验通过才写盘。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private int _mixedPort = 7897;
    [ObservableProperty] private int _clashApiPort = 9090;
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

    /// <summary>回填设置或失败回弹时压住 setter 里的写盘/生效逻辑。</summary>
    private bool _suppress;

    private readonly AutoHide _messageHide;

    /// <summary>页面底部的版本号：应用取发布时注入的版本（git tag），核心取 sing-box 自报版本。</summary>
    [ObservableProperty] private string _appVersion = AppInfo.Version;
    [ObservableProperty] private string _coreVersionText = "";

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

    public string MixedPortLabel => MixedPort.ToString();
    public string ClashApiPortLabel => ClashApiPort.ToString();
    public string ManualNodeSummary => ManualNodeCount == 0 ? "未添加" : $"{ManualNodeCount} 个";
    public string UserAgentLabel => string.IsNullOrWhiteSpace(SubscriptionUserAgent)
        ? SubscriptionService.DefaultUserAgent
        : SubscriptionUserAgent;

    /// <summary>底部版本行：Chorus 版本（来自发布 tag）+ 核心版本（核心在磁盘上才读得到）。</summary>
    public string VersionLine => string.IsNullOrWhiteSpace(CoreVersionText)
        ? $"Chorus {AppVersion}"
        : $"Chorus {AppVersion} · sing-box {CoreVersionText}";

    public SettingsViewModel()
    {
        _messageHide = new AutoHide(ClearMessage);
        LoadFrom(AppServices.Settings);
        _ = LoadCoreVersionAsync();
    }

    /// <summary>问一下 sing-box.exe 自己的版本号（`sing-box version` 的第一行）。</summary>
    private async Task LoadCoreVersionAsync()
    {
        var path = AppServices.ResolveCorePath();
        if (!File.Exists(path))
        {
            CoreVersionText = "未找到核心";
            return;
        }

        try
        {
            AppServices.Core.CorePath = path;
            var text = await AppServices.Core.GetVersionAsync();
            var match = Regex.Match(text, @"\d+\.\d+\.\d+");
            CoreVersionText = match.Success ? match.Value : "";
        }
        catch
        {
            CoreVersionText = "";
        }
    }

    private void LoadFrom(AppSettings settings)
    {
        _suppress = true;
        MixedPort = settings.MixedPort;
        ClashApiPort = settings.ClashApiPort;
        EnableTun = settings.EnableTun;
        EnableSystemProxy = settings.EnableSystemProxy;
        AllowLan = settings.AllowLan;
        CloseToTray = settings.CloseToTray;
        LogLevel = settings.LogLevel;
        SubscriptionUserAgent = settings.SubscriptionUserAgent;
        ApplySubscriptionRouting = settings.ApplySubscriptionRouting;
        ManualNodeCount = settings.Profile.Nodes.Count;
        _suppress = false;
    }

    partial void OnMixedPortChanged(int value) => OnPropertyChanged(nameof(MixedPortLabel));
    partial void OnClashApiPortChanged(int value) => OnPropertyChanged(nameof(ClashApiPortLabel));
    partial void OnManualNodeCountChanged(int value) => OnPropertyChanged(nameof(ManualNodeSummary));
    partial void OnSubscriptionUserAgentChanged(string value) => OnPropertyChanged(nameof(UserAgentLabel));
    partial void OnAppVersionChanged(string value) => OnPropertyChanged(nameof(VersionLine));
    partial void OnCoreVersionTextChanged(string value) => OnPropertyChanged(nameof(VersionLine));

    // ---- 即时生效的开关：和仪表盘的快捷开关走同一条路径，避免两处逻辑不一致 ----

    partial void OnEnableSystemProxyChanged(bool value)
    {
        if (_suppress) return;
        _ = ApplySystemProxyAsync(value);
    }

    private async Task ApplySystemProxyAsync(bool value)
    {
        await AppServices.DashboardVm.ToggleSystemProxyAsync(value);
        // 仪表盘那边切换失败会自己回弹并提示；这里跟着回弹，别让设置页显示一个假状态
        if (AppServices.Settings.EnableSystemProxy != value)
        {
            _suppress = true;
            EnableSystemProxy = !value;
            _suppress = false;
            Show("系统代理切换失败", "没能修改系统代理设置，开关已回弹。", InfoBarSeverity.Error);
        }
    }

    partial void OnEnableTunChanged(bool value)
    {
        if (_suppress) return;
        AppServices.DashboardVm.ToggleTun(value);
    }

    partial void OnAllowLanChanged(bool value)
    {
        if (_suppress) return;
        AppServices.DashboardVm.ToggleAllowLan(value);
    }

    partial void OnApplySubscriptionRoutingChanged(bool value)
    {
        if (_suppress) return;
        Apply(settings => settings.ApplySubscriptionRouting = value);
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_suppress) return;
        Apply(settings => settings.CloseToTray = value);
    }

    partial void OnLogLevelChanged(string value)
    {
        // ComboBox 在 ItemsSource/SelectedItem 初始化时可能先给个空值，别当成用户操作写盘
        if (_suppress || string.IsNullOrWhiteSpace(value)) return;
        Apply(settings => settings.LogLevel = value);
    }

    /// <summary>端口由对话框输入后调用；返回 false 表示数值超出 1-65535。</summary>
    public bool TrySetPort(int port, bool mixed)
    {
        if (port is < 1 or > 65535) return false;
        var settings = AppServices.Settings;
        if (mixed) settings.MixedPort = port;
        else settings.ClashApiPort = port;
        AppServices.Store.Save(settings);
        _suppress = true;
        if (mixed) MixedPort = port;
        else ClashApiPort = port;
        _suppress = false;
        return true;
    }

    /// <summary>User-Agent 由对话框输入后调用；留空则回落到默认值。</summary>
    public void SetUserAgent(string? userAgent)
    {
        var agent = (userAgent ?? "").Trim();
        if (agent.Length == 0) agent = SubscriptionService.DefaultUserAgent;
        var settings = AppServices.Settings;
        settings.SubscriptionUserAgent = agent;
        AppServices.Subscriptions.UserAgent = agent;
        AppServices.Store.Save(settings);
        _suppress = true;
        SubscriptionUserAgent = agent;
        _suppress = false;
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
            ShareLinks = "";
        }

        if (nodes.Count == 0 && errors.Count == 0)
        {
            Show("没有可导入的内容", "先粘贴至少一条分享链接（每行一条）。", InfoBarSeverity.Informational);
            return;
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
            Show("提示", "当前没有手动节点。", InfoBarSeverity.Informational);
            return;
        }
        settings.Profile.Nodes.Clear();
        AppServices.Store.Save(settings);
        ManualNodeCount = 0;
        Show("已清空", "手动节点已清空（订阅节点不受影响）。若核心正在运行，重启核心后生效。",
            InfoBarSeverity.Informational);
    }

    /// <summary>写入设置并保存（所有即时生效项的统一出口）。</summary>
    private static void Apply(Action<AppSettings> mutate)
    {
        var settings = AppServices.Settings;
        mutate(settings);
        AppServices.Store.Save(settings);
    }

    private void Show(string title, string text, InfoBarSeverity severity)
    {
        MessageTitle = title;
        MessageText = text;
        MessageSeverity = severity;
        HasMessage = true;
        _messageHide.Schedule(severity switch
        {
            InfoBarSeverity.Error => AutoHide.LongDelay,
            InfoBarSeverity.Warning => AutoHide.WarnDelay,
            _ => AutoHide.ShortDelay,
        });
    }

    /// <summary>手动关闭提示条（也算取消倒计时）。</summary>
    public void DismissMessage() => ClearMessage();

    private void ClearMessage()
    {
        _messageHide.Cancel();
        if (!HasMessage) return;
        HasMessage = false;
        MessageTitle = "";
        MessageText = "";
    }
}

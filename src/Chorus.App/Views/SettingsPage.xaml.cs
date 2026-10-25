using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Chorus.App.Services;
using Chorus.App.ViewModels;
using Chorus.Core;

namespace Chorus.App.Views;

/// <summary>
/// 设置页（排布与交互参考 Bettbox）：整行可点，开关类点一下即翻转并立刻写盘；
/// 端口 / UA / 手动节点这类需要输入的行，点击弹对话框，校验通过才写盘。
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; } = AppServices.SettingsVm;

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>行点击 = 翻转该行右侧的开关（开关本身 IsHitTestVisible=False，避免点两次）。</summary>
    private void ToggleRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || row.Tag is not string key) return;
        var toggle = key switch
        {
            "allowLan" => AllowLanToggle,
            "systemProxy" => SystemProxyToggle,
            "tun" => TunToggle,
            "routing" => RoutingToggle,
            "closeToTray" => CloseToTrayToggle,
            _ => (ToggleSwitch?)null,
        };
        if (toggle is not null) toggle.IsOn = !toggle.IsOn;
    }

    private async void MixedPortRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var port = await PromptPortAsync("混合代理端口", "HTTP 与 SOCKS 共用同一个端口。核心正在运行时，重启核心后生效。", Vm.MixedPort);
        if (port is int value) Vm.TrySetPort(value, mixed: true);
    }

    private async void ClashApiPortRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var port = await PromptPortAsync("Clash API 端口", "策略组、规则与延迟测速都走这个接口。核心正在运行时，重启核心后生效。", Vm.ClashApiPort);
        if (port is int value) Vm.TrySetPort(value, mixed: false);
    }

    private async void UserAgentRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var box = new TextBox
        {
            Text = Vm.SubscriptionUserAgent,
            PlaceholderText = SubscriptionService.DefaultUserAgent,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(Hint("转换服务按 UA 决定返回格式。常见取值：sing-box、clash-verge/v2.0.0、clash.meta、v2rayN/6.31；留空则用 sing-box。"));
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "拉取订阅时的 User-Agent",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Vm.SetUserAgent(box.Text);
    }

    private async void ManualRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 150,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "ss://… / vmess://… / vless://… 每行一条",
            Text = Vm.ShareLinks,
        };
        box.TextChanged += (_, _) => Vm.ShareLinks = box.Text;

        var count = new TextBlock { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        void RefreshCount() => count.Text = Vm.ManualNodeCount == 0
            ? "当前没有手动节点"
            : $"当前手动节点：{Vm.ManualNodeCount} 个";
        RefreshCount();

        var clear = new Button { Content = "清空手动节点", FontSize = 12 };
        clear.Click += (_, _) =>
        {
            Vm.ClearManualNodes();
            RefreshCount();
        };

        var bottom = new Grid { ColumnSpacing = 8 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(count);
        Grid.SetColumn(clear, 1);
        bottom.Children.Add(clear);

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(Hint("支持 ss / vmess / vless / trojan / hysteria2 / anytls。导入的节点追加到现有手动节点，与订阅节点合并生效。"));
        panel.Children.Add(box);
        panel.Children.Add(bottom);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "手动节点",
            Content = panel,
            PrimaryButtonText = "导入",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Vm.ImportAsync();
    }

    /// <summary>端口输入框：校验不过就不关对话框，把错误写在输入框下面。</summary>
    private async Task<int?> PromptPortAsync(string title, string hint, int current)
    {
        var box = new TextBox { Text = current.ToString(), Header = "端口号（1-65535）" };
        var error = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Crimson),
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(Hint(hint));
        panel.Children.Add(box);
        panel.Children.Add(error);

        int? result = null;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!int.TryParse(box.Text.Trim(), out var port) || port is < 1 or > 65535)
            {
                args.Cancel = true;
                error.Text = "端口号必须是 1-65535 之间的整数。";
                error.Visibility = Visibility.Visible;
                return;
            }
            result = port;
        };
        await dialog.ShowAsync();
        return result;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
    };
}

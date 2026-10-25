using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

public sealed partial class SubscriptionsPage : Page
{
    public SubscriptionsViewModel Vm { get; } = AppServices.SubscriptionsVm;

    public SubscriptionsPage()
    {
        InitializeComponent();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var (name, url) = await ShowSubscriptionDialogAsync("添加订阅", "添加并更新", "", "");
        if (url is null) return;
        Vm.NewName = name;
        Vm.NewUrl = url;
        await Vm.AddAsync();
    }

    private async void UpdateAll_Click(object sender, RoutedEventArgs e) => await Vm.UpdateAllAsync();

    /// <summary>
    /// 点卡片空白处 = 把这份订阅设为当前使用的配置（Bettbox 里点 profile 卡片的行为）。
    /// 按钮自己会吃掉指针事件，所以点「更新/编辑/删除」不会误触发这里的切换。
    /// </summary>
    private async void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || item.IsActive) return;
        await Vm.SetActiveAsync(item);
    }

    private async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await Vm.UpdateAsync(item);
    }

    private async void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var (name, url) = await ShowSubscriptionDialogAsync("编辑订阅", "保存并更新", item.Name, item.Url);
        if (url is null) return;
        await Vm.EditAsync(item, name, url);
    }

    /// <summary>删除前确认（Bettbox 的配置删除同样有一次确认，避免误删整份订阅）。</summary>
    private async void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除订阅",
            Content = $"确定删除「{item.Name}」吗？该订阅解析出的 {item.Entry.Nodes.Count} 个节点与 "
                      + $"{item.Entry.Groups.Count} 个策略组会一并移除（手动节点不受影响）。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Vm.RemoveAsync(item);
    }

    private static SubscriptionItemViewModel? ItemOf(object sender)
        => (sender as FrameworkElement)?.Tag as SubscriptionItemViewModel;

    /// <summary>弹出订阅编辑面板（添加/修改共用）。返回 (名称, 链接)；用户取消时链接为 null。</summary>
    private async Task<(string Name, string? Url)> ShowSubscriptionDialogAsync(
        string title, string primaryButtonText, string name, string url)
    {
        var nameBox = new TextBox
        {
            Header = "名称（可选）",
            PlaceholderText = "例如：机场A（留空则自动取名）",
            Text = name,
        };
        var urlBox = new TextBox
        {
            Header = "订阅链接",
            PlaceholderText = "https://example.com/sub",
            Text = url,
        };
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 69, 69)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 12, Width = 460 };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            Content = panel,
        };

        dialog.PrimaryButtonClick += (s, e) =>
        {
            var input = urlBox.Text.Trim();
            if (input.Length == 0)
            {
                errorText.Text = "请输入订阅链接";
                errorText.Visibility = Visibility.Visible;
                e.Cancel = true;
                return;
            }
            if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                errorText.Text = "订阅链接必须以 http:// 或 https:// 开头";
                errorText.Visibility = Visibility.Visible;
                e.Cancel = true;
                return;
            }
            // 与其他订阅重复才拦截；改回自己的链接不算重复
            var duplicate = AppServices.Settings.Subscriptions.Any(s =>
                string.Equals(s.Url, input, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                errorText.Text = "该订阅链接已存在";
                errorText.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? (nameBox.Text, urlBox.Text.Trim()) : ("", null);
    }

    private void Message_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Vm.DismissMessage();
}

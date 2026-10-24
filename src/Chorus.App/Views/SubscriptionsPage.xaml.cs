using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SubscriptionItemViewModel item)
            await Vm.UpdateAsync(item);
    }

    private async void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not SubscriptionItemViewModel item) return;
        var (name, url) = await ShowSubscriptionDialogAsync("编辑订阅", "保存并更新", item.Name, item.Url);
        if (url is null) return;
        await Vm.EditAsync(item, name, url);
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is SubscriptionItemViewModel item)
            Vm.Remove(item);
    }

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
}

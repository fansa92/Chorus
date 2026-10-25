using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

/// <summary>
/// 日志页：负责滚动跟随 / 「N 条新日志」浮标 / 复制导出等交互，
/// 筛选与合批逻辑都在 <see cref="LogsViewModel"/> 里。
/// </summary>
public sealed partial class LogsPage : Page
{
    private ScrollViewer? _scroll;
    private bool _stickToEnd = true;
    private bool _programmatic;

    public LogsViewModel? Vm => AppServices.LogsVm;

    public LogsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 先让 VM 进入「挂载」状态，它会把这期间累积的日志补进展示列表
        Vm.Attach();
        Vm.Lines.CollectionChanged += OnLinesChanged;
        Vm.ScrollToEndRequested += ScrollToEnd;
        _scroll = FindScrollViewer(LogList);
        if (_scroll is not null)
            _scroll.ViewChanged += OnViewChanged;
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Vm.Lines.CollectionChanged -= OnLinesChanged;
        Vm.ScrollToEndRequested -= ScrollToEnd;
        // 页面离开后 VM 不再改展示列表，避免已卸载的列表控件收到集合通知而崩溃
        Vm.Detach();
        if (_scroll is not null)
            _scroll.ViewChanged -= OnViewChanged;
        _scroll = null;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            if (Vm.ShouldFollow) ScrollToEnd();
            return;
        }
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not { Count: > 0 } added)
            return;

        if (Vm.ShouldFollow)
            ScrollToEnd();
        else
            Vm.ReportNew(added.Count);
    }

    /// <summary>滚到底部：先把最后一条滚进视口，再校正一次（变高行需要二次布局）。</summary>
    private void ScrollToEnd()
    {
        if (Vm.Lines.Count == 0) return;
        Vm.ClearNew();
        _programmatic = true;
        var last = Vm.Lines[^1];
        LogList.ScrollIntoView(last, ScrollIntoViewAlignment.Default);
        DispatcherQueue?.TryEnqueue(() =>
        {
            _scroll?.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true);
            _stickToEnd = true;
            Vm.MarkAtBottom(true);
            DispatcherQueue?.TryEnqueue(() => _programmatic = false);
        });
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroll is null || _programmatic) return;
        var atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 24;
        if (atBottom == _stickToEnd) return;
        _stickToEnd = atBottom;
        Vm.MarkAtBottom(atBottom);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    // ==================== 交互 ====================

    private void Row_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LogItem item })
        {
            item.IsExpanded = !item.IsExpanded;
            e.Handled = true;
        }
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LogItem item })
            item.IsExpanded = !item.IsExpanded;
    }

    private void Module_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LogItem item })
            Vm.ApplyModuleFilter(item.Module);
        ScrollToEnd();
    }

    private void Module_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LogItem item } && item.HasModule)
        {
            Vm.ApplyModuleFilter(item.Module);
            e.Handled = true;
            ScrollToEnd();
        }
    }

    private void ClearModule_Click(object sender, RoutedEventArgs e) => Vm.ClearModuleFilter();

    private void Clear_Click(object sender, RoutedEventArgs e) => Vm.Clear();

    private void JumpToEnd_Click(object sender, RoutedEventArgs e)
    {
        Vm.MarkAtBottom(true);
        ScrollToEnd();
    }

    private void CopyOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LogItem item }) return;
        CopyToClipboard(item.ToPlainText());
        Vm.ShowToast("已复制这一条");
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var text = Vm.Snapshot();
        if (text.Length == 0)
        {
            Vm.ShowToast("当前没有可复制的日志");
            return;
        }
        CopyToClipboard(text);
        Vm.ShowToast($"已复制 {Vm.Lines.Count} 条日志");
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AppServices.DataDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"chorus-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await File.WriteAllTextAsync(path, Vm.Snapshot());
            Vm.ShowToast($"已导出到 {path}");
        }
        catch (Exception ex)
        {
            Vm.ShowToast("导出失败：" + ex.Message);
        }
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Chorus.App.Services;
using Chorus.App.ViewModels;

namespace Chorus.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel Vm { get; } = AppServices.DashboardVm;

    public DashboardPage()
    {
        InitializeComponent();
        Vm.SpeedSampled += OnSpeedSampled;
        Vm.MemorySampled += OnMemorySampled;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 页面卸载后别再往已销毁的 Canvas 上画线（第二次进页面会崩）
        Vm.SpeedSampled -= OnSpeedSampled;
        Vm.MemorySampled -= OnMemorySampled;
    }

    private async void StartCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        await Vm.ToggleAsync(!Vm.Running);
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string mode } radio) return;
        // IsChecked 由 VM 回填时也会触发 Checked（切回仪表盘页重新绑定就会来一次）。
        // 回填时核心的模式已经等于这个值，不是用户的操作：不能再打一次 API，
        // 更不能覆盖「已切换到某节点，正在刷新网络信息…」的提示。
        if (radio.IsChecked != true || Vm.IsMode(mode)) return;
        await Vm.SetModeAsync(mode);
    }

    private async void CloseConnections_Click(object sender, RoutedEventArgs e)
    {
        await Vm.CloseConnectionsAsync();
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        await Vm.DetectAsync();
    }

    private async void SystemProxy_Toggled(object sender, RoutedEventArgs e)
    {
        // IsOn 由 VM 驱动变化时也会触发 Toggled，这里只处理用户的实际操作，
        // 否则「失败回弹」会被当成一次新的切换再写一遍注册表。
        if (sender is ToggleSwitch toggle && toggle.IsOn != Vm.SystemProxyOn)
            await Vm.ToggleSystemProxyAsync(toggle.IsOn);
    }

    private void Tun_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
            Vm.ToggleTun(toggle.IsOn);
    }

    private void AllowLan_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
            Vm.ToggleAllowLan(toggle.IsOn);
    }

    private void Hint_Closed(object sender, InfoBarClosedEventArgs e)
    {
        Vm.DismissHint();
    }

    private void Error_Closed(object sender, InfoBarClosedEventArgs e)
    {
        Vm.DismissError();
    }

    /// <summary>把最近的流量采样画成两条迷你曲线（对数刻度）。</summary>
    private void OnSpeedSampled((double[] Up, double[] Down) data)
    {
        DrawLine(UpSpeedLine, SpeedCanvas, data.Up, SpeedCapacity, logarithmic: true);
        DrawLine(DownSpeedLine, SpeedCanvas, data.Down, SpeedCapacity, logarithmic: true);
    }

    /// <summary>内存走势：线性刻度（工作集不会像流量那样跨几个数量级）。</summary>
    private void OnMemorySampled(double[] values) =>
        DrawLine(MemoryLine, MemoryCanvas, values, MemoryCapacity, logarithmic: false);

    /// <summary>采样点上限，与 ViewModel 的历史长度一致（曲线按固定步长从右往左长）。</summary>
    private const int SpeedCapacity = 60;
    private const int MemoryCapacity = 60;

    private static void DrawLine(Polyline line, Canvas canvas, double[] values, int capacity, bool logarithmic)
    {
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 0 || height <= 0 || values.Length < 2)
        {
            line.Points.Clear();
            return;
        }

        // 采样点不满时贴着右边画，而不是把两三个点拉满整张卡片
        var step = capacity > 1 ? width / (capacity - 1) : width;
        var startX = Math.Max(0, width - step * (values.Length - 1));

        if (logarithmic)
        {
            var max = 1.0;
            foreach (var value in values)
                if (value > max) max = value;

            line.Points.Clear();
            for (var i = 0; i < values.Length; i++)
            {
                var x = startX + step * i;
                var ratio = Math.Log10(values[i] + 1) / Math.Log10(max + 1);
                line.Points.Add(new Point(x, height - ratio * height));
            }
            return;
        }

        // 线性：从最低点画到最高点，别让一条水平的 45 MB 直线贴着顶部或底部
        var min = double.MaxValue;
        var top = 0.0;
        foreach (var value in values)
        {
            if (value < min) min = value;
            if (value > top) top = value;
        }
        var span = Math.Max(top - min, top * 0.15); // 全平时给一点起伏，看起来才是条线
        if (span <= 0) span = 1;

        line.Points.Clear();
        for (var i = 0; i < values.Length; i++)
        {
            var x = startX + step * i;
            var ratio = (values[i] - min) / span;
            line.Points.Add(new Point(x, height - ratio * height));
        }
    }
}

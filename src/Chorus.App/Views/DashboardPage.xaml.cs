using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Vm.SpeedSampled -= OnSpeedSampled;
    }

    private async void StartCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        await Vm.ToggleAsync(!Vm.Running);
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode })
            await Vm.SetModeAsync(mode);
    }

    private async void CloseConnections_Click(object sender, RoutedEventArgs e)
    {
        await Vm.CloseConnectionsAsync();
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
        Vm.HasHint = false;
    }

    /// <summary>把最近的流量采样画成迷你曲线（对数刻度）。</summary>
    private void OnSpeedSampled((double[] Up, double[] Down) data)
    {
        DrawSparkline(UpSpeedLine, data.Up);
        DrawSparkline(DownSpeedLine, data.Down);
    }

    private void DrawSparkline(Polyline line, double[] values)
    {
        var width = SpeedCanvas.ActualWidth;
        var height = SpeedCanvas.ActualHeight;
        if (width <= 0 || height <= 0 || values.Length < 2) return;

        var max = 1.0;
        foreach (var value in values)
            if (value > max) max = value;

        line.Points.Clear();
        for (var i = 0; i < values.Length; i++)
        {
            var x = width * i / (values.Length - 1);
            var ratio = Math.Log10(values[i] + 1) / Math.Log10(max + 1);
            var y = height - ratio * height;
            line.Points.Add(new Point(x, y));
        }
    }
}

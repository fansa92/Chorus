using Microsoft.UI.Dispatching;

namespace Chorus.App.Services;

/// <summary>
/// 提示条自动消失（bettbox 的 toast 也是几秒后自己收）。
/// 每次显示都重置倒计时；同一个信息再次出现不会叠加多个计时器。
/// </summary>
public sealed class AutoHide
{
    /// <summary>成功 / 普通信息：4 秒。</summary>
    public static readonly TimeSpan ShortDelay = TimeSpan.FromSeconds(4);

    /// <summary>警告：6 秒。</summary>
    public static readonly TimeSpan WarnDelay = TimeSpan.FromSeconds(6);

    /// <summary>错误：留久一点，够看清原因。</summary>
    public static readonly TimeSpan LongDelay = TimeSpan.FromSeconds(8);

    private readonly DispatcherQueueTimer _timer;

    public AutoHide(Action hide)
    {
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => hide();
    }

    /// <summary>（重新）开始倒计时。</summary>
    public void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    /// <summary>取消倒计时（手动关闭提示条、或提示被别的原因收掉时）。</summary>
    public void Cancel() => _timer.Stop();
}

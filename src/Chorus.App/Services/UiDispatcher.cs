using Microsoft.UI.Dispatching;

namespace Chorus.App.Services;

/// <summary>后台线程到 UI 线程的调度。</summary>
public static class Ui
{
    public static DispatcherQueue? Queue { get; private set; }

    public static void Initialize(DispatcherQueue queue) => Queue = queue;

    public static void Post(Action action)
    {
        var queue = Queue;
        if (queue is null)
        {
            action();
            return;
        }
        queue.TryEnqueue(_ => action());
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LLMTray.App;

/// <summary>
/// One LLMTray per user session: a second launch (Start menu, a pinned
/// tile) asks the running one to show its window and exits.
/// </summary>
public static class Program
{
    internal const string ShowEventName = @"Local\LLMTray.Windows.Show";
    const string InstanceMutexName = @"Local\LLMTray.Windows.Instance";

    [STAThread]
    static int Main()
    {
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                using (show) show.Set();
            }
            return 0;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        GC.KeepAlive(instance);
        return 0;
    }
}

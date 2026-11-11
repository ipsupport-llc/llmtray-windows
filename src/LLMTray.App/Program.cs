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

    /// <summary>Set by a second launch; the running app shows its window.</summary>
    internal static EventWaitHandle ShowEvent { get; private set; } = null!;

    [STAThread]
    static int Main()
    {
        // The project files reader (adr/0021): a child of the app, no window, no single-instance check.
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 4 && args[1] == LLMTray.Core.ProjectFiles.ExtractProtocol.ArgumentName)
        {
            return LLMTray.Core.ProjectFiles.ExtractProtocol.RunChild(args[2], args[3], Console.In, Console.OpenStandardOutput());
        }
        // The project pictures' decoder (adr/0024): a child too, Windows' own image codecs in it.
        if (args.Length == 5 && args[1] == Interop.PictureDecoding.ArgumentName)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            return Interop.PictureDecoding.RunChild(args[2], args[3], args[4]);
        }
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var first);
        if (!first)
        {
            // llmtray starts the app with --background: one already running stays as it is.
            if (Environment.GetCommandLineArgs().Contains("--background")) return 0;
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                using (show) show.Set();
            }
            return 0;
        }
        // Created before anything else, so a launch during startup isn't lost:
        // the auto-reset event stays set until the app first waits on it.
        ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        GC.KeepAlive(instance);
        return 0;
    }
}

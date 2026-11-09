using Microsoft.Win32;
using Windows.ApplicationModel;

namespace LLMTray.App;

/// <summary>
/// "Start LLMTray when you sign in" (the Mac app's Launch at login). The
/// package uses its startup task; the portable copy the user's Run key.
/// Either way it starts in the notification area, without the window.
/// </summary>
static class SignInStartup
{
    const string TaskId = "LLMTrayStartup";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "LLMTray";
    public const string BackgroundArgument = "--background";

    static bool Packaged
    {
        get
        {
            try
            {
                _ = Package.Current.Id;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static async Task<bool> IsOnAsync()
    {
        if (Packaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string value && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Null when done; otherwise why it couldn't be.</summary>
    public static async Task<string?> SetAsync(bool on)
    {
        if (Packaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (!on)
            {
                task.Disable();
                return null;
            }
            var state = await task.RequestEnableAsync();
            return state switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
                StartupTaskState.DisabledByUser => "Turned off in Windows Settings › Apps › Startup: turn LLMTray on there.",
                StartupTaskState.DisabledByPolicy => "Your organization's policy doesn't allow it.",
                _ => "Windows didn't allow it.",
            };
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" {BackgroundArgument}");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return e.Message;
        }
    }

    /// <summary>Started by sign-in (or with --background): no window at first.</summary>
    public static bool StartedInBackground()
    {
        if (Environment.GetCommandLineArgs().Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase)) return true;
        try
        {
            return Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind
                == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

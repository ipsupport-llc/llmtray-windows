using System.Diagnostics;
using System.Runtime.InteropServices;

// LLMTray.exe at the top of the folder (adr/0007): starts app\LLMTray.exe
// with the same arguments (--background from sign-in start, among others)
// and exits.
var app = Path.Combine(AppContext.BaseDirectory, "app", "LLMTray.exe");
if (!File.Exists(app))
{
    Native.MessageBoxW(IntPtr.Zero, $"LLMTray's app folder is missing:\n{app}\n\nUnpack or install LLMTray again.", "LLMTray", 0x10);
    return 2;
}
var info = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
foreach (var arg in Environment.GetCommandLineArgs().Skip(1)) info.ArgumentList.Add(arg);
try
{
    Process.Start(info);
}
catch (Exception e)
{
    Native.MessageBoxW(IntPtr.Zero, $"LLMTray couldn't start:\n{app}\n\n{e.Message}\n\nUnpack or install LLMTray again.", "LLMTray", 0x10);
    return 3;
}
return 0;

static partial class Native
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}

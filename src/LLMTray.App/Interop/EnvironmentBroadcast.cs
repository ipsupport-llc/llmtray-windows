using System.Runtime.InteropServices;

namespace LLMTray.App.Interop;

/// <summary>Tells running programs (Explorer, new terminals) that the user's environment changed.</summary>
static class EnvironmentBroadcast
{
    public static void Changed() =>
        SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LLMTray.App.Interop;

/// <summary>One line of the tray menu; <c>null</c> text is a separator.</summary>
sealed record TrayMenuItem(string? Text, Action? Action = null, bool Checked = false, bool Enabled = true)
{
    public static readonly TrayMenuItem Separator = new(Text: null);
}

/// <summary>
/// The notification-area icon (Shell_NotifyIcon), owned by a hidden window
/// on the UI thread. Left click raises <see cref="Activated"/>; right click
/// shows the menu <see cref="Menu"/> builds.
/// </summary>
sealed class TrayIcon : IDisposable
{
    const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    const uint IconId = 1;

    readonly Native.WndProc wndProc;
    readonly IntPtr window;
    readonly IntPtr icon;
    readonly uint taskbarCreated;
    string tooltip;
    bool disposed;

    public TrayIcon(string iconPath, string tooltip)
    {
        this.tooltip = tooltip;
        wndProc = WindowProc;
        var instance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = instance,
            lpszClassName = "LLMTray.TrayWindow",
        };
        if (Native.RegisterClassExW(ref wc) == 0) throw new Win32Exception();
        // A hidden top-level window, not a message-only one: only top-level
        // windows hear TaskbarCreated (Explorer restarted).
        window = Native.CreateWindowExW(0, wc.lpszClassName, "LLMTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception();
        taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        icon = Native.LoadImageW(IntPtr.Zero, iconPath, Native.IMAGE_ICON,
            Native.GetSystemMetrics(Native.SM_CXSMICON), Native.GetSystemMetrics(Native.SM_CYSMICON), Native.LR_LOADFROMFILE);
        Add();
    }

    public event Action? Activated;

    /// <summary>Builds the right-click menu each time it opens.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    public void SetTooltip(string text)
    {
        tooltip = text.Length > 127 ? text[..127] : text;
        var data = Data(Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
    }

    void Add()
    {
        var data = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Shell_NotifyIconW(Native.NIM_ADD, ref data);
        data.uTimeoutOrVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref data);
    }

    Native.NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
        hWnd = window,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tooltip,
    };

    IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the event in LOWORD(lParam), the anchor point in wParam.
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case Native.WM_LBUTTONUP:
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    Activated?.Invoke();
                    break;
                case Native.WM_CONTEXTMENU:
                    var w = wParam.ToInt64();
                    ShowMenu((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF));
                    break;
            }
            return IntPtr.Zero;
        }
        if (message == taskbarCreated && taskbarCreated != 0)
        {
            Add();
            return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    void ShowMenu(int x, int y)
    {
        var items = Menu?.Invoke() ?? [];
        if (items.Count == 0) return;
        var menu = Native.CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Text is null)
                {
                    Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                var flags = Native.MF_STRING | (item.Checked ? Native.MF_CHECKED : 0) | (item.Enabled ? 0 : Native.MF_GRAYED);
                // & is the mnemonic marker in menus: shown literally.
                Native.AppendMenuW(menu, flags, (UIntPtr)(i + 1), item.Text.Replace("&", "&&"));
            }
            // Without this the menu doesn't close when the user clicks elsewhere.
            Native.SetForegroundWindow(window);
            var chosen = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, x, y, window, IntPtr.Zero);
            Native.PostMessageW(window, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= items.Count) items[chosen - 1].Action?.Invoke();
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var data = Data(0);
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
        Native.DestroyWindow(window);
        if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
    }

    static class Native
    {
        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
        public const uint NOTIFYICON_VERSION_4 = 4;
        public const uint WM_LBUTTONUP = 0x0202, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
        public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
        public const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
        public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
        public const int SM_CXSMICON = 49, SM_CYSMICON = 50;

        public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID, uFlags, uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string? name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}

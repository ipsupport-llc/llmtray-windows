using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LLMTray.App.Interop;

/// <summary>The browser tab in front: its browser, title, address and page text (null when none could be read).</summary>
sealed record CurrentTab(string Browser, string Title, string Address, string? Text);

/// <summary>Why no tab came: shown in the composer.</summary>
sealed class BrowserTabException(string message) : Exception(message);

/// <summary>
/// Attach Current Tab (adr/0022, Mac adr/0030): the browser window that was in front before LLMTray
/// (Edge, Chrome, Brave, Vivaldi, Opera, Firefox), read through UI Automation, the accessibility
/// interface the browser gives screen readers: the address bar's value and the page's text. Nothing
/// runs in the page and nothing is sent to the browser; the user's click is the consent.
/// </summary>
static partial class BrowserTab
{
    static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi",
        ["opera"] = "Opera",
        ["firefox"] = "Firefox",
    };

    /// <summary>The page's text read at most (the shared item keeps 6,000 characters of it).</summary>
    const int MaxRead = 200_000;

    /// <summary>
    /// The tab of the browser in front of every other app's window (LLMTray's own aside). Off the UI thread:
    /// UI Automation calls into the browser and can take a moment.
    /// </summary>
    public static Task<CurrentTab> ReadAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var window = FrontWindow() ?? throw new BrowserTabException("No browser window is open in front.");
        var (process, browser) = window.Browser
            ?? throw new BrowserTabException("The window in front isn't a browser. Bring the browser tab to the front, then attach it.");
        var title = CleanTitle(window.Title, browser);
        string address = "";
        string? text = null;
        try
        {
            var automation = (IUIAutomation)new CUIAutomation();
            if (automation.ElementFromHandle(window.Handle, out var root) != 0 || root is null) throw new BrowserTabException("The browser can't be read.");
            address = Address(automation, root) ?? "";
            cancellationToken.ThrowIfCancellationRequested();
            text = PageText(automation, root);
            // A Chromium browser builds its accessibility tree on the first ask: a moment later the page is there.
            if (string.IsNullOrWhiteSpace(text))
            {
                Thread.Sleep(700);
                cancellationToken.ThrowIfCancellationRequested();
                text = PageText(automation, root);
            }
        }
        catch (COMException)
        {
            // The browser went, or refuses: the tab still goes, title and address as far as known.
        }
        catch (InvalidCastException)
        {
        }
        GC.KeepAlive(process);
        return new CurrentTab(browser, title, address, string.IsNullOrWhiteSpace(text) ? null : text);
    }, cancellationToken);

    sealed record Front(IntPtr Handle, string Title, (string Process, string Name)? Browser);

    /// <summary>The first app window in the z-order that isn't LLMTray's: the one that was in front before it.</summary>
    static Front? FrontWindow()
    {
        var own = (uint)Environment.ProcessId;
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GwHwndNext))
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0) continue;
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) continue;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own) continue;
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            string? name = null;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
            }
            return new Front(hwnd, title.ToString(), name is not null && Browsers.TryGetValue(name, out var browser) ? (name, browser) : null);
        }
        return null;
    }

    /// <summary>"Page - Profile 1 - Microsoft​ Edge" → "Page".</summary>
    static string CleanTitle(string title, string browser)
    {
        var cleaned = BrowserSuffix().Replace(title.Replace("​", ""), "");
        // Edge and Chrome put the profile before the browser's name.
        cleaned = Regex.Replace(cleaned, @" [-—] (Personal|Work|Profile \d+|Person \d+)$", "");
        return cleaned.Trim();
    }

    [GeneratedRegex(@" [-—] (Microsoft ?Edge|Google Chrome|Brave|Vivaldi|Opera|Mozilla Firefox|Firefox)$")]
    private static partial Regex BrowserSuffix();

    /// <summary>The address bar's value: the browser's first edit box (the omnibox).</summary>
    static string? Address(IUIAutomation automation, IUIAutomationElement root)
    {
        if (automation.CreatePropertyCondition(ControlTypePropertyId, EditControlTypeId, out var condition) != 0) return null;
        IUIAutomationElement? edit;
        try
        {
            if (root.FindFirst(TreeScopeDescendants, condition, out edit) != 0 || edit is null) return null;
        }
        finally
        {
            Marshal.Release(condition);
        }
        if (edit.GetCurrentPattern(ValuePatternId, out var raw) != 0 || raw is not IUIAutomationValuePattern value) return null;
        if (value.get_CurrentValue(out var text) != 0) return null;
        // The bar shows "example.com/path" without the scheme; a typed-in search isn't an address either way.
        return text?.Trim();
    }

    /// <summary>The page's text: the first document in the window (the web content), through its text pattern.</summary>
    static string? PageText(IUIAutomation automation, IUIAutomationElement root)
    {
        if (automation.CreatePropertyCondition(ControlTypePropertyId, DocumentControlTypeId, out var condition) != 0) return null;
        IUIAutomationElement? document;
        try
        {
            if (root.FindFirst(TreeScopeDescendants, condition, out document) != 0 || document is null) return null;
        }
        finally
        {
            Marshal.Release(condition);
        }
        if (document.GetCurrentPattern(TextPatternId, out var raw) != 0 || raw is not IUIAutomationTextPattern pattern) return null;
        if (pattern.get_DocumentRange(out var range) != 0 || range is null) return null;
        return range.GetText(MaxRead, out var text) == 0 ? text : null;
    }

    // ---- UI Automation, the few calls used (UIAutomationClient.h; slots before them kept in order) ----

    const int TreeScopeDescendants = 4;
    const int ControlTypePropertyId = 30003;
    const int EditControlTypeId = 50004;
    const int DocumentControlTypeId = 50030;
    const int ValuePatternId = 10002;
    const int TextPatternId = 10014;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    class CUIAutomation;

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void get_ControlViewWalker();
        void get_ContentViewWalker();
        void get_RawViewWalker();
        void get_RawViewCondition();
        void get_ControlViewCondition();
        void get_ContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IntPtr condition);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object pattern);
    }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationValuePattern
    {
        void SetValue();
        [PreserveSig] int get_CurrentValue([MarshalAs(UnmanagedType.BStr)] out string value);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextPattern
    {
        void RangeFromPoint();
        void RangeFromChild();
        void GetSelection();
        void GetVisibleRanges();
        [PreserveSig] int get_DocumentRange(out IUIAutomationTextRange range);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextRange
    {
        void Clone();
        void Compare();
        void CompareEndpoints();
        void ExpandToEnclosingUnit();
        void FindAttribute();
        void FindText();
        void GetAttributeValue();
        void GetBoundingRectangles();
        void GetEnclosingElement();
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
    }

    // ---- Windows ----

    const uint GwHwndNext = 2;
    const uint GwOwner = 4;
    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x80;

    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

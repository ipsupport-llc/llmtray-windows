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
        var (_, browser) = window.Browser
            ?? throw new BrowserTabException("The window in front isn't a browser. Bring the browser tab to the front, then attach it.");
        var title = CleanTitle(window.Title, browser);
        string address = "";
        string? text = null;
        IUIAutomation automation;
        IUIAutomationElement? root;
        try
        {
            automation = (IUIAutomation)new CUIAutomation();
            if (automation.ElementFromHandle(window.Handle, out root) != 0 || root is null) throw new BrowserTabException("The browser can't be read.");
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            throw new BrowserTabException("The browser can't be read.");
        }
        // Each part on its own: a flaky address bar doesn't cost the page's text.
        try
        {
            address = Address(automation, root) ?? "";
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
        }
        for (var attempt = 0; attempt < 2 && text is null; attempt++)
        {
            // A Chromium browser builds its accessibility tree on the first ask: a moment later the page is there.
            if (attempt > 0) Thread.Sleep(700);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (name, read) = PageText(automation, root, title);
                if (!string.IsNullOrWhiteSpace(read)) text = read;
                // The page's own title (the document's name) over the window's, which may carry a profile's name.
                if (!string.IsNullOrWhiteSpace(name)) title = name.Trim();
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
            }
        }
        return new CurrentTab(browser, title, address, text);
    }, cancellationToken);

    /// <summary>
    /// The last window of another app that was in the foreground, noted as it happened (a foreground hook on the
    /// UI thread): the window the user was in before LLMTray, whatever the z-order says now.
    /// </summary>
    static IntPtr lastForeign;
    static IntPtr hook;
    static WinEventDelegate? onForeground;

    /// <summary>Starts noting the foreground window; on the UI thread (the hook's messages come through its loop).</summary>
    public static void TrackForeground()
    {
        if (hook != IntPtr.Zero) return;
        onForeground = (_, _, hwnd, _, _, _, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (hwnd != IntPtr.Zero && pid != (uint)Environment.ProcessId) lastForeign = hwnd;
        };
        hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, onForeground, 0, 0, WinEventOutOfContext);
    }

    sealed record Front(IntPtr Handle, string Title, (string Process, string Name)? Browser);

    /// <summary>The first app window in the z-order that isn't LLMTray's: the one that was in front before it.</summary>
    static Front? FrontWindow()
    {
        var own = (uint)Environment.ProcessId;
        // The window noted as last in front, while it's still an ordinary visible one; else the z-order.
        var noted = lastForeign;
        if (noted != IntPtr.Zero && IsWindow(noted))
        {
            var top = GetAncestor(noted, GaRootOwner);
            if (top == IntPtr.Zero) top = noted;
            if (Candidate(top, own)) return Describe(top);
        }
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GwHwndNext))
        {
            // Always-on-top windows aren't where the user was working.
            if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0) continue;
            if (Candidate(hwnd, own)) return Describe(hwnd);
        }
        return null;
    }

    /// <summary>A window the user can see and works in: visible, not cloaked (another desktop, a suspended app), not a tool window or owned, with a title, not LLMTray's.</summary>
    static bool Candidate(IntPtr hwnd, uint own)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if ((GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return false;
        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid != own;
    }

    static Front Describe(IntPtr hwnd)
    {
            var length = GetWindowTextLength(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);
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

    /// <summary>
    /// The window's title without this browser's own name ("Page - Microsoft Edge" to "Page"); a page titled
    /// "… - Brave" in Firefox keeps it. The page's own title from its document replaces this when it can be read.
    /// </summary>
    static string CleanTitle(string title, string browser)
    {
        var name = browser switch
        {
            "Microsoft Edge" => "Microsoft ?Edge",
            "Firefox" => "(Mozilla )?Firefox",
            _ => Regex.Escape(browser),
        };
        return Regex.Replace(title.Replace("\u200b", ""), $" [-—] {name}$", "").Trim();
    }

    /// <summary>
    /// The address bar's value: the browser's own bar by its class (Chromium's omnibox) or id (Firefox's), never any
    /// other edit box (an app window's composer). Text with spaces in it is a search being typed, not an address.
    /// </summary>
    static string? Address(IUIAutomation automation, IUIAutomationElement root)
    {
        var edit = Find(automation, root, ClassNamePropertyId, "OmniboxViewViews") ?? Find(automation, root, AutomationIdPropertyId, "urlbar-input");
        if (edit is null) return null;
        if (edit.GetCurrentPattern(ValuePatternId, out var raw) != 0 || raw is not IUIAutomationValuePattern value) return null;
        if (value.get_CurrentValue(out var text) != 0 || text is null) return null;
        text = text.Trim();
        return text.Length == 0 || text.Any(char.IsWhiteSpace) ? null : text;
    }

    static IUIAutomationElement? Find(IUIAutomation automation, IUIAutomationElement root, int property, object value)
    {
        if (automation.CreatePropertyCondition(property, value, out var condition) != 0) return null;
        try
        {
            return root.FindFirst(TreeScopeDescendants, condition, out var found) == 0 ? found : null;
        }
        finally
        {
            Marshal.Release(condition);
        }
    }

    /// <summary>
    /// The page: the document named as the window's title says (the tab's web content, not a sidebar's or tools'),
    /// else the first document. Its name (the page's title) and its text through its text pattern.
    /// </summary>
    static (string? Name, string? Text) PageText(IUIAutomation automation, IUIAutomationElement root, string title)
    {
        IUIAutomationElement? document = null;
        if (title.Length > 0 && automation.CreatePropertyCondition(ControlTypePropertyId, DocumentControlTypeId, out var isDocument) == 0)
        {
            try
            {
                if (automation.CreatePropertyCondition(NamePropertyId, title, out var named) == 0)
                {
                    try
                    {
                        if (automation.CreateAndCondition(isDocument, named, out var both) == 0)
                        {
                            try
                            {
                                if (root.FindFirst(TreeScopeDescendants, both, out var found) == 0) document = found;
                            }
                            finally
                            {
                                Marshal.Release(both);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(named);
                    }
                }
            }
            finally
            {
                Marshal.Release(isDocument);
            }
        }
        document ??= Find(automation, root, ControlTypePropertyId, DocumentControlTypeId);
        if (document is null) return (null, null);
        var name = document.get_CurrentName(out var n) == 0 ? n : null;
        if (document.GetCurrentPattern(TextPatternId, out var raw) != 0 || raw is not IUIAutomationTextPattern pattern) return (name, null);
        if (pattern.get_DocumentRange(out var range) != 0 || range is null) return (name, null);
        return (name, range.GetText(MaxRead, out var text) == 0 ? text : null);
    }

    // ---- UI Automation, the few calls used (UIAutomationClient.h; slots before them kept in order) ----

    const int TreeScopeDescendants = 4;
    const int ControlTypePropertyId = 30003;
    const int NamePropertyId = 30005;
    const int AutomationIdPropertyId = 30011;
    const int ClassNamePropertyId = 30012;
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
        void CreatePropertyConditionEx();
        [PreserveSig] int CreateAndCondition(IntPtr first, IntPtr second, out IntPtr condition);
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
        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        void get_CurrentProcessId();
        void get_CurrentControlType();
        void get_CurrentLocalizedControlType();
        [PreserveSig] int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string name);
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
    const long WsExTopmost = 0x8;
    const uint GaRootOwner = 3;
    const int DwmwaCloaked = 14;
    const uint EventSystemForeground = 3;
    const uint WinEventOutOfContext = 0;

    delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

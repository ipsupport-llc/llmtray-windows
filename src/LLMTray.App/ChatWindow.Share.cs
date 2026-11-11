using LLMTray.App.Interop;
using LLMTray.Core.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// Showing the model a window or the screen (adr/0022, Mac adr/0030): from
/// the attach button's menu, through the system picker. Each capture keeps
/// its picture and the text read from it; at send time, a model that sees
/// gets the picture (within the picture limit) and one that doesn't gets
/// the text. Shown as a chip until sent; saved with the message.
/// </summary>
sealed partial class ChatWindow
{
    /// <summary>What's shown with the next message: what it was and its picture (apart from the attached pictures).</summary>
    readonly List<(SharedContext Shared, byte[]? Png)> pendingShared = [];

    /// <summary>The attach button: a menu when windows may be shown, else the picture picker.</summary>
    async Task AttachClickedAsync()
    {
        if (!App.Current.Settings.ShowWindows)
        {
            await PickPicturesAsync();
            return;
        }
        var menu = new MenuFlyout();
        var pictures = new MenuFlyoutItem { Text = L.T("Attach Pictures…"), Icon = new FontIcon { Glyph = "" } };
        pictures.Click += async (_, _) => await PickPicturesAsync();
        var window = new MenuFlyoutItem { Text = L.T("Show a Window or the Screen…"), Icon = new FontIcon { Glyph = "" } };
        window.Click += async (_, _) => await ShowWindowAsync();
        // The browser tab that was in front before LLMTray (Mac adr/0030): its title, address and text.
        var tab = new MenuFlyoutItem { Text = L.T("Attach Current Tab"), Icon = new FontIcon { Glyph = "\uE774" } };
        tab.Click += async (_, _) => await AttachTabAsync();
        menu.Items.Add(pictures);
        if (ScreenCapture.IsSupported) menu.Items.Add(window);
        menu.Items.Add(tab);
        menu.ShowAt(attach);
    }

    async Task ShowWindowAsync()
    {
        if (pending.Count + pendingShared.Count(s => s.Png is not null) >= MaxPictures)
        {
            ShowAttachProblem(L.F("At most {0} pictures and windows a message.", MaxPictures));
            return;
        }
        var epoch = chat.Epoch;
        Capture? capture;
        try
        {
            capture = await ScreenCapture.PickAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), MaxSide);
        }
        catch (Exception e)
        {
            ShowAttachProblem(L.F("The window couldn't be captured: {0}", e.Message));
            return;
        }
        // Cancelled, or another chat is on screen now.
        if (capture is null || epoch != chat.Epoch) return;
        pendingShared.Add((SharedContext.Make(SharedKind.Window, capture.Name, "", capture.Text, hasPicture: false), capture.Png));
        RenderAttachments();
        input.Focus(FocusState.Programmatic);
    }

    /// <summary>Tabs one message may carry: each is up to 6,000 characters of text (they take no picture's place).</summary>
    const int MaxTabs = 4;

    /// <summary>The chat (its epoch) a tab is being read for: Send there waits (the message would go without it); null when none is.</summary>
    int? attachingTabFor;

    bool attachingTab => attachingTabFor == chat.Epoch;

    /// <summary>A browser that doesn't answer (hung, a modal up): the read is given up, the composer free again.</summary>
    static readonly TimeSpan TabReadTime = TimeSpan.FromSeconds(10);

    async Task<SharedContext?> AttachTabAsync()
    {
        if (attachingTabFor is not null) return null;
        if (pendingShared.Count(s => s.Png is null) >= MaxTabs)
        {
            ShowAttachProblem(L.F("At most {0} browser tabs a message.", MaxTabs));
            return null;
        }
        var epoch = chat.Epoch;
        CurrentTab tab;
        attachingTabFor = epoch;
        attachNote.Text = L.T("Reading the tab…");
        attachNote.Foreground = Ui.Secondary;
        attachNote.Visibility = Visibility.Visible;
        using var timeout = new CancellationTokenSource(TabReadTime);
        try
        {
            tab = await BrowserTab.ReadAsync(timeout.Token).WaitAsync(TabReadTime);
            attachNote.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            if (epoch == chat.Epoch) ShowAttachProblem(L.T("The browser didn't answer in time. Try again in a moment."));
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Said only in the chat it was asked in.
            if (epoch == chat.Epoch) ShowAttachProblem(e is BrowserTabException ? L.T(e.Message) : L.F("The tab couldn't be read: {0}", e.Message));
            return null;
        }
        finally
        {
            attachingTabFor = null;
        }
        if (epoch != chat.Epoch || pendingShared.Count(s => s.Png is null) >= MaxTabs) return null;
        var shared = SharedContext.Make(SharedKind.BrowserTab, tab.Title, tab.Address, tab.Text, hasPicture: false);
        if (tab.Text is null) shared = shared with { TitleOnly = true };
        pendingShared.Add((shared, null));
        RenderAttachments();
        if (tab.Text is null)
        {
            // Without its text the tab still goes, title and address; the chip says so too.
            ShowAttachProblem(L.F("{0} didn't give the page's text, only its title and address. Reload the page and attach it again.", tab.Browser));
        }
        input.Focus(FocusState.Programmatic);
        return shared;
    }

    void ShowAttachProblem(string text)
    {
        attachNote.Text = text;
        attachNote.Foreground = Ui.Orange;
        attachNote.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// At send time, for the model the message goes to: a model that sees
    /// gets each capture's picture, after the attached ones (so the captures
    /// with pictures are the message's last pictures); one that doesn't gets
    /// the text. Returns the shown items as sent.
    /// </summary>
    List<SharedContext> TakeShared(List<byte[]> images)
    {
        var app = App.Current;
        var picked = (model.SelectedItem as ComboBoxItem)?.Tag as string;
        var entry = picked is null ? app.Host.State.Model : app.Library.Current.Resolve(picked);
        var sees = entry is { ProjectorPath: not null };
        var sent = new List<SharedContext>();
        foreach (var (shared, png) in pendingShared)
        {
            // A browser tab has no picture: its text goes to every model.
            var withPicture = png is not null && sees && images.Count < MaxPictures;
            if (withPicture) images.Add(png!);
            sent.Add(shared with { HasPicture = withPicture });
        }
        pendingShared.Clear();
        return sent;
    }

    /// <summary>The chips of what's shown, beside the pictures.</summary>
    void RenderSharedChips()
    {
        foreach (var item in pendingShared.ToList())
        {
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 10 }, Padding = new Thickness(4) };
            ToolTipService.SetToolTip(remove, L.T("Remove"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, L.F("Remove {0}", item.Shared.Heading));
            remove.Click += (_, _) =>
            {
                pendingShared.Remove(item);
                RenderAttachments();
            };
            UIElement thumb;
            if (item.Png is { } png)
            {
                var image = new Image { Width = 40, Height = 40, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
                _ = SetSourceAsync(image, png, 40);
                thumb = image;
            }
            else
            {
                thumb = new FontIcon { Glyph = "\uE774", FontSize = 20, Width = 40 };
            }
            var label = new TextBlock { Text = item.Shared.TitleOnly ? L.F("{0} (title and address only)", item.Shared.Heading) : item.Shared.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 };
            ToolTipService.SetToolTip(label, item.Shared.Text is { } text ? (text.Length > 400 ? text[..400] + "…" : text) : item.Shared.Heading);
            attachments.Children.Add(new Border
            {
                Child = Ui.Horizontal(thumb, label, remove),
                BorderBrush = Ui.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    /// <summary>
    /// For <c>--screenshots</c> only: a page opened in Edge on the runner, then attached as the current tab
    /// through UI Automation; what came out written beside the pictures, and the composer photographed.
    /// </summary>
    internal async Task TabScreenshotsAsync(string folder, Func<UIElement, string, Task> save)
    {
        string report;
        try
        {
            var page = Path.Combine(Path.GetTempPath(), "llmtray-tab-demo.html");
            await File.WriteAllTextAsync(page, "<!doctype html><html><head><title>Router settings</title></head><body><h1>Wi-Fi</h1>"
                + "<p>Network name: HomeNet. Security: WPA3. Channel: auto.</p><p>Guest network is off.</p></body></html>");
            var edge = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("no Edge on this machine");
            var start = new System.Diagnostics.ProcessStartInfo(edge) { UseShellExecute = false };
            foreach (var a in new[] { "--new-window", "--no-first-run", "--no-default-browser-check", new Uri(page).AbsoluteUri }) start.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(start)?.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(8));
            // The product's own path, the composer's chip included.
            var shared = await AttachTabAsync() ?? throw new InvalidOperationException("nothing attached: " + attachNote.Text);
            report = $"title: {shared.Title}\naddress: {shared.Source}\ntitle only: {shared.TitleOnly}\ntext: {shared.Text ?? "(none)"}";
            await Task.Delay(800);
            if (Content is UIElement content) await save(content, Path.Combine(folder, "chat-tab.png"));
            pendingShared.Clear();
            RenderAttachments();
        }
        catch (Exception e)
        {
            report = "failed: " + e;
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "tab.txt"), report + Environment.NewLine);
    }
}

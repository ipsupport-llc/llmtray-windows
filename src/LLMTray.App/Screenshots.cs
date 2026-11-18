using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LLMTray.App;

/// <summary>
/// <c>--screenshots &lt;folder&gt;</c>: every window photographed by the app
/// itself, for a look at the layout in CI (adr/0014): the chat, each tab of
/// Settings, About. The windows' own pixels, whatever covers them.
/// </summary>
static class Screenshots
{
    public static async Task TakeAsync(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            // After the first check for updates, the model list and the setup wizard settle.
            await Task.Delay(TimeSpan.FromSeconds(12));
            var app = App.Current;
            app.ShowChat();
            await Task.Delay(1500);
            if (app.Chat is { } chat)
            {
                await SaveAsync(chat, Path.Combine(folder, "chat.png"));
                // A demo project with files (adr/0021): the sidebar, the Files dialog, and the reader process run for real.
                await chat.ProjectScreenshotsAsync(folder, SaveElementAsync);
                // Spoken messages (adr/0023): the composer's microphone, a clip, the recording bar.
                await chat.VoiceScreenshotsAsync(folder, SaveElementAsync);
                // Documents (adr/0024): a report printed to PDF by the real printer.
                await chat.DocumentScreenshotsAsync(folder);
                // The current browser tab (adr/0022): Edge on the runner, read through UI Automation.
                await chat.TabScreenshotsAsync(folder, SaveElementAsync);
                // A short chat: the answers, Regenerate and the composer.
                await chat.MessagesScreenshotAsync(folder, SaveElementAsync);
            }
            app.ShowSettings();
            foreach (var tab in new[] { "general", "models", "profiles", "memory", "tools", "server", "updates" })
            {
                app.SettingsWindow?.ShowPage(tab);
                await Task.Delay(1200);
                if (app.SettingsWindow is { } settings) await SaveAsync(settings, Path.Combine(folder, $"settings-{tab}.png"));
            }
            // For the Microsoft Store listing (adr/0025): the windows filling a large screen. About comes last: CI waits for it.
            await StoreAsync(app, Path.Combine(folder, "store"));
            app.ShowAbout();
            await Task.Delay(2000);
            if (app.About is { } about) await SaveAsync(about, Path.Combine(folder, "about.png"));
            app.ShowChat();
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(folder, "screenshots-failed.txt"), e.ToString());
        }
    }

    /// <summary>
    /// The chat with a short exchange and Settings' main tabs, each window's
    /// content at 1440 × 810 DIPs (the Store takes 1366 × 768 or more), when
    /// its screen has room for that. CI only: the windows are put back
    /// roughly (size, place, maximized), not to the pixel.
    /// </summary>
    static async Task StoreAsync(App app, string folder)
    {
        app.ShowChat();
        await Task.Delay(800);
        if (app.Chat is not { } chat) return;
        Directory.CreateDirectory(folder);
        var chatWas = Where(chat);
        try
        {
            if (!Fit(chat, folder)) return;
            await Task.Delay(1500);
            await chat.StoreScreenshotAsync(folder, SaveElementAsync);
        }
        finally
        {
            Put(chat, chatWas);
        }
        app.ShowSettings();
        await Task.Delay(800);
        if (app.SettingsWindow is not { } settings) return;
        var settingsWas = Where(settings);
        try
        {
            if (!Fit(settings, folder)) return;
            foreach (var tab in new[] { "models", "server", "tools", "memory" })
            {
                settings.ShowPage(tab);
                await Task.Delay(1500);
                await SaveAsync(settings, Path.Combine(folder, $"settings-{tab}.png"));
            }
        }
        finally
        {
            Put(settings, settingsWas);
        }
    }

    const int StoreWidth = 1440, StoreHeight = 810;

    /// <summary>Where a window is: its normal bounds (taken after un-maximizing it) and whether it was maximized.</summary>
    static (Windows.Graphics.RectInt32 Bounds, bool Maximized) Where(Window window)
    {
        var presenter = window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        var maximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        if (maximized) presenter!.Restore();
        return (new Windows.Graphics.RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y, window.AppWindow.Size.Width, window.AppWindow.Size.Height), maximized);
    }

    /// <summary>
    /// The window's content at the Store size in DIPs (as the shot renders
    /// it), centered on its own screen at its own scale; false when that
    /// screen hasn't the room.
    /// </summary>
    static bool Fit(Window window, string folder)
    {
        var scale = Ui.Scale(window);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(folder)!, "store.txt"), $"{window.GetType().Name}: work area {area.Width}x{area.Height}, scale {scale}\n");
        // Room for the content, the frame and the title bar.
        if (area.Width < (StoreWidth + 40) * scale || area.Height < (StoreHeight + 80) * scale) return false;
        window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)Math.Ceiling(StoreWidth * scale), (int)Math.Ceiling(StoreHeight * scale)));
        var size = window.AppWindow.Size;
        window.AppWindow.Move(new Windows.Graphics.PointInt32(area.X + Math.Max(0, (area.Width - size.Width) / 2), area.Y + Math.Max(0, (area.Height - size.Height) / 2)));
        return true;
    }

    static void Put(Window window, (Windows.Graphics.RectInt32 Bounds, bool Maximized) was)
    {
        window.AppWindow.MoveAndResize(was.Bounds);
        if (was.Maximized && window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
    }

    static async Task SaveAsync(Window window, string path)
    {
        if (window.Content is UIElement content) await SaveElementAsync(content, path);
    }

    static async Task SaveElementAsync(UIElement content, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }
}

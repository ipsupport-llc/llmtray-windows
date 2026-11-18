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
    /// The chat with a short exchange and Settings' main tabs, each window
    /// maximized, when the screen is at least the Store's 1366 × 768.
    /// </summary>
    static async Task StoreAsync(App app, string folder)
    {
        var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(folder)!, "store.txt"), $"work area {area.Width}x{area.Height}\n");
        if (area.Width < 1366 || area.Height < 768) return;
        Directory.CreateDirectory(folder);
        app.ShowChat();
        await Task.Delay(800);
        if (app.Chat is { } chat)
        {
            Maximize(chat);
            await Task.Delay(1500);
            await chat.StoreScreenshotAsync(folder, SaveElementAsync);
            Restore(chat);
        }
        app.ShowSettings();
        await Task.Delay(800);
        if (app.SettingsWindow is { } settings)
        {
            Maximize(settings);
            foreach (var tab in new[] { "models", "server", "tools", "memory" })
            {
                settings.ShowPage(tab);
                await Task.Delay(1500);
                await SaveAsync(settings, Path.Combine(folder, $"settings-{tab}.png"));
            }
            Restore(settings);
        }
    }

    static void Maximize(Window window)
    {
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
    }

    static void Restore(Window window)
    {
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Restore();
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

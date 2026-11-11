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
    readonly List<(SharedContext Shared, byte[] Png)> pendingShared = [];

    /// <summary>The attach button: a menu when windows may be shown, else the picture picker.</summary>
    async Task AttachClickedAsync()
    {
        if (!App.Current.Settings.ShowWindows || !ScreenCapture.IsSupported)
        {
            await PickPicturesAsync();
            return;
        }
        var menu = new MenuFlyout();
        var pictures = new MenuFlyoutItem { Text = L.T("Attach Pictures…"), Icon = new FontIcon { Glyph = "" } };
        pictures.Click += async (_, _) => await PickPicturesAsync();
        var window = new MenuFlyoutItem { Text = L.T("Show a Window or the Screen…"), Icon = new FontIcon { Glyph = "" } };
        window.Click += async (_, _) => await ShowWindowAsync();
        menu.Items.Add(pictures);
        menu.Items.Add(window);
        menu.ShowAt(attach);
    }

    async Task ShowWindowAsync()
    {
        if (pending.Count + pendingShared.Count >= MaxPictures)
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
            var withPicture = sees && images.Count < MaxPictures;
            if (withPicture) images.Add(png);
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
            var thumb = new Image { Width = 40, Height = 40, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            _ = SetSourceAsync(thumb, item.Png, 40);
            var label = new TextBlock { Text = item.Shared.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 };
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
}

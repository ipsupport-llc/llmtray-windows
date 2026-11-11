using LLMTray.App.Interop;
using LLMTray.Core.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLMTray.App;

/// <summary>
/// Showing the model a window or the screen (adr/0022, Mac adr/0030): from
/// the attach button's menu, through the system picker. A model that sees
/// gets the picture and a line naming it; one that doesn't gets the text
/// recognized in it. Shown as a chip until sent; saved with the message.
/// </summary>
sealed partial class ChatWindow
{
    /// <summary>What's shown with the next message, and its picture among <see cref="pending"/> (null: the model doesn't see).</summary>
    readonly List<(SharedContext Shared, byte[]? Png)> pendingShared = [];

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
        var epoch = chat.Epoch;
        Capture? capture;
        try
        {
            capture = await ScreenCapture.PickAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), MaxSide);
        }
        catch (Exception e)
        {
            attachNote.Text = L.F("The window couldn't be captured: {0}", e.Message);
            attachNote.Foreground = Ui.Orange;
            attachNote.Visibility = Visibility.Visible;
            return;
        }
        // Cancelled, or another chat is on screen now.
        if (capture is null || epoch != chat.Epoch) return;
        var app = App.Current;
        var picked = (model.SelectedItem as ComboBoxItem)?.Tag as string;
        var entry = picked is null ? app.Host.State.Model : app.Library.Current.Resolve(picked);
        // A model that sees gets the picture; its text goes too, for the model to fall back on if the picture is removed.
        var sees = entry is { ProjectorPath: not null };
        var shared = SharedContext.Make(SharedKind.Window, capture.Name, "", capture.Text, hasPicture: sees);
        if (sees && pending.Count < MaxPictures) pending.Add(capture.Png);
        pendingShared.Add((shared, sees ? capture.Png : null));
        RenderAttachments();
        input.Focus(FocusState.Programmatic);
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
                if (item.Png is { } png) pending.Remove(png);
                RenderAttachments();
            };
            var label = new TextBlock { Text = item.Shared.Heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
            ToolTipService.SetToolTip(label, item.Shared.Text is { } text ? (text.Length > 400 ? text[..400] + "…" : text) : item.Shared.Heading);
            attachments.Children.Add(new Border
            {
                Child = Ui.Horizontal(new FontIcon { Glyph = "", FontSize = 12 }, label, remove),
                BorderBrush = Ui.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }
}

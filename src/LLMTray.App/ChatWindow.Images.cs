using System.Runtime.InteropServices.WindowsRuntime;
using LLMTray.Core.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LLMTray.App;

/// <summary>
/// Pictures in the chat (Mac: attach, paste, drop): converted to PNG with
/// the long side at most 2048 px (the server refuses huge ones, and a
/// vision encoder sees no more), shown in the composer until sent.
/// </summary>
sealed partial class ChatWindow
{
    /// <summary>What the server takes and the vision encoders use (Mac ImageAttachment.maxSide).</summary>
    const uint MaxSide = 2048;
    const long MaxFileBytes = 64L << 20;
    /// <summary>Pictures one message may carry: each costs the model hundreds of tokens.</summary>
    const int MaxPictures = 8;
    static readonly string[] PictureTypes = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic"];

    readonly List<byte[]> pending = [];
    readonly StackPanel attachments = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    readonly TextBlock attachNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly Button attach = new();

    void SetUpPictures(UIElement dropTarget)
    {
        attach.Content = new FontIcon { Glyph = "", FontSize = 14 };
        attach.VerticalAlignment = VerticalAlignment.Bottom;
        ToolTipService.SetToolTip(attach, L.T("Attach a picture"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(attach, L.T("Attach a picture"));
        attach.Click += async (_, _) => await AttachClickedAsync();

        // Ctrl+V of a picture (or of copied picture files) into the message box.
        input.Paste += async (_, e) =>
        {
            DataPackageView content;
            try
            {
                content = Clipboard.GetContent();
                // Text pastes as text; only pictures and picture files are taken here.
                if (!content.Contains(StandardDataFormats.Bitmap) && !content.Contains(StandardDataFormats.StorageItems)) return;
            }
            catch (Exception)
            {
                // The clipboard is busy (another app holds it): the box pastes as usual.
                return;
            }
            e.Handled = true;
            await AddFromDataAsync(content);
        };

        dropTarget.AllowDrop = true;
        dropTarget.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = L.T("Attach a picture");
            }
        };
        dropTarget.Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                await AddFromDataAsync(e.DataView);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    async Task PickPicturesAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail };
            foreach (var type in PictureTypes) picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            var epoch = chat.Epoch;
            foreach (var file in files) await AddFileAsync(file, epoch);
        }
        catch (Exception)
        {
            // The picker can't open (policy): nothing attached.
        }
        input.Focus(FocusState.Programmatic);
    }

    async Task AddFromDataAsync(DataPackageView data)
    {
        var epoch = chat.Epoch;
        try
        {
            var files = data.Contains(StandardDataFormats.StorageItems)
                ? (await data.GetStorageItemsAsync()).OfType<StorageFile>().ToList()
                : [];
            foreach (var file in files) await AddFileAsync(file, epoch);
            if (files.Count > 0) return;
            if (data.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await data.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                Add(await ToPngAsync(stream), epoch);
                return;
            }
            ShowAttachNote(L.T("Only pictures can be attached."));
        }
        catch (Exception)
        {
            ShowAttachNote(L.T("That picture couldn't be read."));
        }
    }

    async Task AddFileAsync(StorageFile file, int epoch)
    {
        if (!PictureTypes.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))
        {
            ShowAttachNote(L.F("{0} isn't a picture.", file.Name));
            return;
        }
        try
        {
            var size = (await file.GetBasicPropertiesAsync()).Size;
            if ((long)size > MaxFileBytes)
            {
                ShowAttachNote(L.F("{0} is too large.", file.Name));
                return;
            }
            using var stream = await file.OpenReadAsync();
            Add(await ToPngAsync(stream), epoch);
        }
        catch (Exception)
        {
            ShowAttachNote(L.F("{0} couldn't be read.", file.Name));
        }
    }

    /// <param name="epoch">The chat it was attached in: one that finished converting after another chat was opened is dropped.</param>
    void Add(byte[]? png, int epoch)
    {
        if (epoch != chat.Epoch) return;
        if (png is null)
        {
            ShowAttachNote(L.T("That picture couldn't be read."));
            return;
        }
        if (pending.Count >= MaxPictures)
        {
            ShowAttachNote(L.F("At most {0} pictures in one message.", MaxPictures));
            return;
        }
        pending.Add(png);
        RenderAttachments();
    }

    /// <summary>Another chat is shown: what was waiting to be sent belonged to the one before.</summary>
    void ClearAttachments()
    {
        if (pending.Count == 0 && pendingShared.Count == 0 && attachNote.Visibility == Visibility.Collapsed) return;
        pending.Clear();
        // What was shown belongs to the chat it was shown in (a temporary one's never reaches another).
        pendingShared.Clear();
        RenderAttachments();
        attachNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>Any picture Windows can read, as PNG with the long side at most 2048 px, upright, in sRGB.</summary>
    static async Task<byte[]?> ToPngAsync(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        var (width, height) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        if (width == 0 || height == 0) return null;
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (scale < 1)
        {
            // The transform scales before it rotates: the decoder's own (unrotated) sides.
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
        return bytes;
    }

    void ShowAttachNote(string text)
    {
        attachNote.Text = text;
        attachNote.Foreground = Ui.Red;
        attachNote.Visibility = Visibility.Visible;
    }

    void RenderAttachments()
    {
        attachments.Children.Clear();
        foreach (var png in pending.ToList())
        {
            var image = new Image { Width = 56, Height = 56, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            _ = SetSourceAsync(image, png, 56);
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 10 },
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            ToolTipService.SetToolTip(remove, L.T("Remove"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, L.T("Remove"));
            remove.Click += (_, _) =>
            {
                pending.Remove(png);
                RenderAttachments();
            };
            var tile = new Grid { Width = 56, Height = 56, CornerRadius = new CornerRadius(6) };
            tile.Children.Add(image);
            tile.Children.Add(remove);
            attachments.Children.Add(tile);
        }
        RenderSharedChips();
        attachments.Visibility = pending.Count > 0 || pendingShared.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderAttachNote();
    }

    /// <summary>Says so when the chosen model can't see what's attached.</summary>
    void RenderAttachNote()
    {
        if (pending.Count == 0)
        {
            attachNote.Visibility = Visibility.Collapsed;
            return;
        }
        var app = App.Current;
        var picked = (model.SelectedItem as ComboBoxItem)?.Tag as string;
        var entry = picked is null ? app.Host.State.Model : app.Library.Current.Resolve(picked);
        if (entry is { ProjectorPath: null })
        {
            attachNote.Text = L.F("{0} doesn't see pictures. Choose a model that does (marked \"sees images\" in Models).", entry.Name);
            attachNote.Foreground = Ui.Orange;
            attachNote.Visibility = Visibility.Visible;
        }
        else
        {
            attachNote.Visibility = Visibility.Collapsed;
        }
    }

    /// <param name="width">Decoded at about this width (logical pixels), not at full size: a tile holds no more.</param>
    static async Task SetSourceAsync(Image image, byte[] png, int width)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = width, DecodePixelType = DecodePixelType.Logical };
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception)
        {
            // Not a picture after all: the tile stays empty.
        }
    }

    /// <summary>A message's pictures, side by side.</summary>
    static UIElement? PicturesView(ChatMessage message, HorizontalAlignment alignment)
    {
        if (message.Images.Count == 0) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = alignment };
        foreach (var png in message.Images)
        {
            var image = new Image { MaxWidth = 260, MaxHeight = 260, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
            _ = SetSourceAsync(image, png, 260);
            row.Children.Add(new Border { Child = image, CornerRadius = new CornerRadius(8) });
        }
        return new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalAlignment = alignment,
        };
    }
}

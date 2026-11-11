using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LLMTray.App.Interop;

/// <summary>
/// Scanned PDF pages read in the reader process (adr/0021, Mac adr/0012 tier 2): Windows' own PDF
/// renderer draws the page at the size the recognizer takes, and Windows.Media.Ocr reads it in the
/// user's languages (as it reads a shown window, adr/0022). Nothing is downloaded; with no
/// recognition language installed, a scan reads as no text, as before.
/// </summary>
static class PageOcr
{
    static string? openPath;
    static PdfDocument? open;
    static OcrEngine? engine;
    static bool noEngine;

    /// <summary>The <see cref="LLMTray.Core.ProjectFiles.PageRecognizer"/> the reader child runs with.</summary>
    public static string? Recognize(string path, int page)
    {
        try
        {
            return RecognizeAsync(path, page).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A page Windows can't draw or read: no text for it, the rest of the document goes on.
            return null;
        }
    }

    static async Task<string?> RecognizeAsync(string path, int page)
    {
        if (noEngine) return null;
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            noEngine = true;
            return null;
        }
        if (open is null || openPath != path)
        {
            open = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
            openPath = path;
        }
        if (page < 1 || page > open.PageCount) return null;
        using var pdfPage = open.GetPage((uint)(page - 1));
        // As large as the recognizer takes on the long side: small print survives.
        var limit = OcrEngine.MaxImageDimension;
        var size = pdfPage.Size;
        var scale = Math.Min(limit / Math.Max(1.0, size.Width), limit / Math.Max(1.0, size.Height));
        using var stream = new InMemoryRandomAccessStream();
        await pdfPage.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Floor(size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Floor(size.Height * scale)),
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
        });
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        var text = string.Join("\n", result.Lines.Select(l => l.Text));
        return text.Trim().Length > 0 ? text : null;
    }
}

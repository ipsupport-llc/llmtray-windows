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
    /// <summary>A PDF Windows couldn't open: not tried again for every page.</summary>
    static string? failedPath;

    /// <summary>A page's draw and read at most: one that hangs is given up, the rest go on.</summary>
    static readonly TimeSpan PageTime = TimeSpan.FromSeconds(30);

    /// <summary>The long side a page is drawn at: about 300 dpi for A4 or Letter, what text recognition needs, never the 10,000 px it could take.</summary>
    const double RenderSide = 3300;
    static OcrEngine? engine;
    static bool noEngine;
    /// <summary>A page was given up while Windows still worked on it: no more pages are started beside it (they'd pile up).</summary>
    static bool stalled;

    /// <summary>The <see cref="LLMTray.Core.ProjectFiles.PageRecognizer"/> the reader child runs with.</summary>
    public static string? Recognize(string path, int page)
    {
        if (stalled) return null;
        try
        {
            return RecognizeAsync(path, page).WaitAsync(PageTime).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            // The rest of this document's scans read as no text: the stuck page's work can't be stopped, only not joined.
            stalled = true;
            return null;
        }
        catch (Exception)
        {
            // A page Windows can't draw or read in time (or in memory): no text for it, the rest of the document goes on.
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
        if (failedPath == path) return null;
        if (open is null || openPath != path)
        {
            try
            {
                open = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
                openPath = path;
            }
            catch (Exception)
            {
                failedPath = path;
                throw;
            }
        }
        if (page < 1 || page > open.PageCount) return null;
        using var pdfPage = open.GetPage((uint)(page - 1));
        var size = pdfPage.Size;
        // A page with no sane size reads as no text, before anything is drawn.
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width < 1 || size.Height < 1 || Math.Max(size.Width, size.Height) / Math.Min(size.Width, size.Height) > 100)
        {
            return null;
        }
        var side = Math.Min(RenderSide, OcrEngine.MaxImageDimension);
        var scale = side / Math.Max(size.Width, size.Height);
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

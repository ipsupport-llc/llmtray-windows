using System.Runtime.InteropServices.WindowsRuntime;
using LLMTray.Core.Documents;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LLMTray.App.Interop;

/// <summary>
/// The app's <see cref="IDocumentPrinter"/>: prints on the UI thread (WebView2 lives there), hosted by the chat window.
/// </summary>
sealed class WindowDocumentPrinter(Microsoft.UI.Xaml.Window window, string dataFolder) : IDocumentPrinter
{
    public string BaseUrl => DocumentPrinter.Base;
    public DocumentPaper Paper => DocumentPrinter.UsesLetter ? DocumentPaper.Letter : DocumentPaper.A4;

    public Task<PrintedDocument> PrintAsync(DocumentPage page, string title, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<PrintedDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paper = Paper == DocumentPaper.Letter ? (8.5, 11.0) : (8.27, 11.69);
        var resources = page.Resources.ToDictionary(r => r.Key, r => (r.Value.Bytes, r.Value.ContentType));
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                done.TrySetResult(await DocumentPrinter.PrintAsync(WinRT.Interop.WindowNative.GetWindowHandle(window), dataFolder, page.Html,
                    resources, title, paper, cancellationToken));
            }
            catch (Exception e)
            {
                done.TrySetException(e is DocumentPrintException or OperationCanceledException ? e : new DocumentPrintException(e.Message));
            }
        }))
        {
            done.TrySetException(new DocumentPrintException("the app is closing"));
        }
        return done.Task;
    }
}

/// <summary>
/// Prints a document's page to PDF (adr/0024, Mac adr/0020) with WebView2,
/// Chromium's print: it repeats a table's header on every page and lays out
/// MathML. The page is built by Core's converter, which emits nothing that
/// loads or runs; behind it, this web view runs no script, opens nothing,
/// and serves only the page's own resources (its stylesheet, chart SVGs,
/// pictures) from a fixed table under one address — every other request
/// is refused before it leaves. It is never shown. Chromium renders in its
/// own sandboxed processes; the output limits are checked here.
/// </summary>
static class DocumentPrinter
{
    /// <summary>The address the page's resources live under (nothing is fetched from it: requests are answered from the table).</summary>
    public const string Base = "https://document.llmtray/";
    const string PageName = "index.html";
    public const int MaxPages = 200;
    public const long MaxPdfBytes = 50L << 20;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    static CoreWebView2Environment? environment;
    /// <summary>One print at a time (a turn's and a Make again's): one environment, made once.</summary>
    static readonly SemaphoreSlim gate = new(1, 1);

    /// <param name="parent">A window to host the (hidden) view; nothing is shown in it.</param>
    /// <param name="resources">The page's resources by path under <see cref="Base"/>: bytes and content type.</param>
    /// <param name="paperInches">The paper's width and height.</param>
    public static async Task<PrintedDocument> PrintAsync(IntPtr parent, string dataFolder, string html,
        IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> resources, string title, (double Width, double Height) paperInches,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // The whole of it bounded (WebView2 starting, Windows reading the PDF): every wait inside gives up at the deadline,
            // so the print has ended, its view closed, before the next one may start.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout + TimeSpan.FromSeconds(30));
            try
            {
                return await PrintLockedAsync(parent, dataFolder, html, resources, title, paperInches, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DocumentPrintException("it took over a minute and a half");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    static async Task<PrintedDocument> PrintLockedAsync(IntPtr parent, string dataFolder, string html,
        IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> resources, string title, (double Width, double Height) paperInches,
        CancellationToken cancellationToken)
    {
        // The page itself is served from the table too: NavigateToString takes at most 2 MB, a page may be 8.
        // Built first (before there's a view to close): the paths are exact, as the page names them.
        var served = new Dictionary<string, (byte[] Bytes, string ContentType)>(resources, StringComparer.Ordinal)
        {
            [PageName] = (System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8"),
        };
        CoreWebView2Controller controller;
        // This print's own environment, used throughout (its handlers too): never a field another print may change.
        CoreWebView2Environment env;
        Task<CoreWebView2Controller>? creating = null;
        try
        {
            env = environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(dataFolder, "webview"), new CoreWebView2EnvironmentOptions())
                .AsTask().WaitAsync(cancellationToken);
            creating = env.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)parent)).AsTask();
            controller = await creating.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A view that turns up after the deadline is closed then, on this thread, as one in time would be.
            var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = creating?.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) ui?.TryEnqueue(() => t.Result.Close());
            }, TaskScheduler.Default);
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // No runtime (an old Windows 10 without Edge's WebView2), or it wouldn't start: its own words say which.
            throw new DocumentPrintException($"Microsoft Edge WebView2, which lays out the pages, couldn't start ({e.Message})");
        }
        var temp = Path.Combine(Path.GetTempPath(), "llmtray-document-" + Guid.NewGuid().ToString("N"));
        Task<(int Pages, byte[]? Thumbnail)>? inspecting = null;
        try
        {
            controller.IsVisible = false;
            var view = controller.CoreWebView2;
            var settings = view.Settings;
            settings.IsScriptEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            // Every request: the table's own resources, else refused (a remote image, file:, a CSS url() elsewhere).
            view.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            view.WebResourceRequested += (_, e) =>
            {
                var uri = e.Request.Uri;
                if (uri.StartsWith(Base, StringComparison.Ordinal) && served.TryGetValue(uri[Base.Length..], out var resource))
                {
                    var stream = new MemoryStream(resource.Bytes, writable: false).AsRandomAccessStream();
                    e.Response = env.CreateWebResourceResponse(stream, 200, "OK",
                        $"Content-Type: {resource.ContentType}\r\nAccess-Control-Allow-Origin: *");
                    return;
                }
                e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            // The page itself is the only navigation; a refresh, a link or a new window goes nowhere.
            var loaded = false;
            view.NavigationStarting += (_, e) =>
            {
                if (loaded || !string.Equals(e.Uri, Base + PageName, StringComparison.Ordinal))
                {
                    e.Cancel = true;
                    return;
                }
                loaded = true;
            };
            view.NewWindowRequested += (_, e) => e.Handled = true;
            view.DownloadStarting += (_, e) => e.Cancel = true;
            view.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, e) => done.TrySetResult(e.IsSuccess);
            view.Navigate(Base + PageName);
            using (timeout.Token.Register(() => done.TrySetCanceled()))
            {
                try
                {
                    if (!await done.Task) throw new DocumentPrintException("the page didn't load");
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DocumentPrintException("it took over a minute");
                }
            }

            Directory.CreateDirectory(temp);
            var path = Path.Combine(temp, "document.pdf");
            var print = env.CreatePrintSettings();
            print.Orientation = CoreWebView2PrintOrientation.Portrait;
            print.PageWidth = paperInches.Width;
            print.PageHeight = paperInches.Height;
            print.MarginTop = print.MarginBottom = 0.6;
            print.MarginLeft = print.MarginRight = 0.7;
            print.ShouldPrintBackgrounds = true;
            // Chromium's header and footer: the title, and the page's number of all; the address left blank.
            print.ShouldPrintHeaderAndFooter = true;
            print.HeaderTitle = title;
            print.FooterUri = " ";
            bool printed;
            try
            {
                printed = await view.PrintToPdfAsync(path, print).AsTask().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DocumentPrintException("it took over a minute");
            }
            if (!printed) throw new DocumentPrintException("printing to PDF failed");
            var size = new FileInfo(path).Length;
            if (size > MaxPdfBytes) throw new DocumentPrintException("the PDF would be over 50 MB; make it shorter");
            var pdf = await File.ReadAllBytesAsync(path, cancellationToken);
            inspecting = InspectAsync(path);
            var (pages, thumbnail) = await inspecting.WaitAsync(cancellationToken);
            if (pages == 0) throw new DocumentPrintException("no pages came out");
            if (pages > MaxPages) throw new DocumentPrintException($"it would be {pages} pages, over {MaxPages}; make it shorter");
            return new PrintedDocument(pdf, pages, thumbnail);
        }
        finally
        {
            controller.Close();
            // Windows still reading the PDF (given up on at the deadline): the folder goes when it's done with it.
            if (inspecting is { IsCompleted: false }) _ = inspecting.ContinueWith(_ => DeleteFolder(temp), TaskScheduler.Default);
            else DeleteFolder(temp);
        }
    }

    static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The page count and a 400 px thumbnail of the first page (Windows' own PDF reader).</summary>
    static async Task<(int Pages, byte[]? Thumbnail)> InspectAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        byte[]? thumbnail = null;
        try
        {
            using var page = document.GetPage(0);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 400, BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId });
            thumbnail = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(thumbnail.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        }
        catch (Exception)
        {
            // No thumbnail: the card shows an icon.
            thumbnail = null;
        }
        return ((int)document.PageCount, thumbnail);
    }

    /// <summary>Letter where it's the paper in use (the Americas mostly), else A4.</summary>
    public static bool UsesLetter =>
        System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE" or "PR" or "GT" or "CR" or "SV" or "DO" or "PA" or "BZ";
}

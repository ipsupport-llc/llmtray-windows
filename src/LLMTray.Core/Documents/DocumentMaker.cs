namespace LLMTray.Core.Documents;

/// <summary>A printed document: the PDF, its page count and a thumbnail of the first page (PNG), if one was made.</summary>
public sealed record PrintedDocument(byte[] Pdf, int Pages, byte[]? Thumbnail);

/// <summary>The page couldn't be printed; the reason goes to the model, which tells the user.</summary>
public sealed class DocumentPrintException(string reason) : Exception(reason);

/// <summary>
/// The app's printer for <c>make_document</c> (adr/0024): it serves the page's
/// resources under <see cref="BaseUrl"/> and prints the page to PDF (WebView2 on
/// Windows). Core builds the page; this only prints it.
/// </summary>
public interface IDocumentPrinter
{
    /// <summary>The address the page's resources are served under.</summary>
    string BaseUrl { get; }

    /// <summary>The paper of this PC's region.</summary>
    DocumentPaper Paper { get; }

    /// <exception cref="DocumentPrintException">Not printed (a limit, the time, no printer engine).</exception>
    Task<PrintedDocument> PrintAsync(DocumentPage page, string title, CancellationToken cancellationToken);
}

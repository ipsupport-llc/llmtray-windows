using System.Text.Json.Nodes;

namespace LLMTray.Core.Documents;

/// <summary>
/// What a call comes to before anything is printed: the checked spec, the
/// whole call (changes applied) to save with the document, and the earlier
/// document it changed; or the answer when nothing is to be made.
/// </summary>
public sealed record DocumentRequest(
    DocumentSpec? Spec,
    DocumentCall? Call,
    string? FromTitle,
    string? Refusal,
    DocumentProblem Problem = DocumentProblem.None);

/// <summary>
/// <c>make_document</c> (Mac adr/0020): the declaration the model sees, the
/// path from a call's arguments to a spec, and the answers. Printing is the
/// app's (WebView2), so the made answer takes the page count from there.
/// One document per turn: after one is made, a second call is refused.
/// </summary>
public static class DocumentTool
{
    public const string Name = DocumentCall.ToolName;

    /// <summary>After every answer that made nothing: a small model read "use one of them" as done and told the user the PDF was ready.</summary>
    public const string NotMade = " Nothing was made -- don't tell the user it was made; call make_document again with this fixed.";

    /// <summary>The output's limits (Mac adr/0020, Hardening 3): over one, no document, and the answer says which.</summary>
    public static class Limits
    {
        public const int Pages = 200;
        public const long PdfBytes = 50L * 1024 * 1024;
        public static readonly TimeSpan Time = TimeSpan.FromSeconds(60);
    }

    const string Description =
        "Make a PDF document from Markdown the user asked for (a report, letter, notes, invoice). The app lays it out and draws charts "
        + "from their numbers or formula in charts (never image data); the user sees it in the chat.";

    /// <summary>The request's <c>tools</c> entry.</summary>
    public static JsonObject Definition()
    {
        var properties = new JsonObject();
        foreach (var f in DocumentCall.Fields) properties[f.Name] = f.JsonSchema();
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(DocumentCall.Fields.Where(f => f.Required).Select(f => (JsonNode)JsonValue.Create(f.Name)).ToArray()),
        };
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = Name, ["description"] = Description, ["parameters"] = parameters },
        };
    }

    /// <summary>
    /// A call's arguments to what to print: read, changes applied to the
    /// earlier document they name (<paramref name="documents"/>: the chat's
    /// made documents, oldest first), then checked.
    /// </summary>
    public static DocumentRequest Prepare(string argumentsJson, IReadOnlyList<EarlierDocument> documents, bool madeThisTurn = false)
    {
        if (madeThisTurn) return new(null, null, null, AlreadyMade);
        var parsed = DocumentCall.Parse(argumentsJson);
        if (parsed.Call is null) return new(null, null, null, parsed.Refusal, DocumentProblem.BadArguments);
        var edit = DocumentEdit.Resolve(parsed.Call, documents);
        if (edit.Call is null) return new(null, null, null, edit.Refusal, DocumentProblem.Changes);
        var read = DocumentSpec.Read(edit.Call, edit.FromContent);
        if (read.Spec is null) return new(null, null, null, read.Refusal, read.Problem);
        var spec = read.Spec with { Adjusted = [.. edit.Adjusted, .. read.Spec.Adjusted] };
        return new(spec, edit.Call, edit.FromTitle, null);
    }

    /// <summary>A second call in the turn.</summary>
    public const string AlreadyMade = Name + ": a document was already made in this turn. Do not call it again; say if it should change.";

    public const string TurnedOff = Name + " isn't available: making documents is turned off in Settings. Answer without it.";

    public const string TemporaryChat = Name + " isn't available in a temporary chat: it writes nothing. The user can make documents in a saved chat.";

    /// <summary>The page passed its size limit (<see cref="DocumentPage.TooLarge"/>).</summary>
    public const string TooLarge = Name + ": the document is too large to lay out (over 8 MB of page). Shorten it." + NotMade;

    /// <summary>Printing or saving failed: telling the user is the fix.</summary>
    public static string Failed(string why) => $"{Name}: the document couldn't be made ({why}). Tell the user. Nothing was made -- don't tell the user it was made.";

    /// <summary>
    /// The answer for a made document: its title and pages, that the user
    /// sees it in the chat; what was drawn other than asked and what was left
    /// out -- the tool is spent for the turn, so the user must hear it.
    /// </summary>
    public static string Made(DocumentSpec spec, int pages, string? fromTitle = null, IEnumerable<string>? pageNotes = null)
    {
        var text = $"Made the PDF \"{spec.Title}\" ({pages} page{(pages == 1 ? "" : "s")})"
            + (fromTitle is null ? "" : $" from \"{fromTitle}\" with the changes") + ". "
            + "The user sees it in the chat and can open, save or share it. Don't repeat its content unless asked.";
        var notes = spec.Notes.Concat(pageNotes ?? []).ToList();
        if (spec.Adjusted.Count > 0) text += " Drawn with changes: " + string.Join(" ", spec.Adjusted);
        if (notes.Count > 0)
            text += " Not in the PDF: " + string.Join(" ", notes) + " Tell the user what is missing and why; don't say everything worked.";
        return text;
    }
}

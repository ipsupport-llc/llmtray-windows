namespace LLMTray.Core.Documents;

/// <summary>
/// What a call with changes came to: the whole call to make, notes for the
/// answer, and the earlier document it started from (its title and content:
/// what it already had isn't held against the change). <see cref="Refusal"/>
/// when nothing is to be made.
/// </summary>
public sealed record DocumentEditResult(
    DocumentCall? Call,
    IReadOnlyList<string> Adjusted,
    string? FromTitle = null,
    string? FromContent = null,
    string? Refusal = null);

/// <summary>An earlier document of the chat: its title and the saved call (<see cref="DocumentCall.ToJson"/>).</summary>
public sealed record EarlierDocument(string Title, string Arguments);

/// <summary>
/// <c>make_document</c> with <c>changes</c> (Mac adr/0020, "Changing a
/// document"): an earlier document of the chat made again with only the
/// sections the model sent, the rest taken from that document's call. A small
/// model rewriting a long document drops or changes parts it wasn't asked
/// to; sending one section is faster and leaves the others as they were.
/// </summary>
public static class DocumentEdit
{
    /// <summary>
    /// A call with changes as the whole call it means: the earlier document it
    /// changes (the one of its title, else the latest; <paramref name="documents"/>
    /// oldest first) with those sections. A call with content too is that
    /// content; one without changes is as it came.
    /// </summary>
    public static DocumentEditResult Resolve(DocumentCall call, IReadOnlyList<EarlierDocument> documents)
    {
        if (call.Changes.Count == 0) return new(call, []);
        if (!string.IsNullOrWhiteSpace(call.Content))
            return new(call with { Changes = [] }, ["The changes were set aside: the whole content was given."]);
        if (Pick(call.Title, documents.Select(d => d.Title).ToList()) is not int k)
            return Refused("make_document: there's no earlier document in this chat to change. Write the whole content.");
        var earlier = documents[k];
        var baseCall = DocumentCall.Parse(earlier.Arguments).Call ?? new DocumentCall(earlier.Title, "", [], null, []);
        var applied = Apply(call, baseCall);
        return applied with { FromTitle = applied.Refusal is null ? earlier.Title : null, FromContent = applied.Refusal is null ? baseCall.Content ?? "" : null };
    }

    /// <summary>The earlier document a call changes: the latest one whose title is the call's, else the latest. <paramref name="titles"/> oldest first.</summary>
    public static int? Pick(string? title, IReadOnlyList<string> titles)
    {
        if (titles.Count == 0) return null;
        if (title is not null && Normalized(title) is { Length: > 0 } wanted)
        {
            for (var i = titles.Count - 1; i >= 0; i--)
                if (Normalized(titles[i]) == wanted) return i;
        }
        return titles.Count - 1;
    }

    /// <summary>
    /// The whole call to make: <paramref name="baseCall"/> with <paramref name="call"/>'s
    /// changes, title, style and charts. The result has no changes: it is what
    /// Make again and a later change start from. A section to replace that
    /// isn't there is a refusal listing the headings; one to add after that
    /// isn't there puts the new part at the end, said.
    /// </summary>
    public static DocumentEditResult Apply(DocumentCall call, DocumentCall baseCall)
    {
        var adjusted = new List<string>();
        var baseTitle = baseCall.Title;
        var title = string.IsNullOrWhiteSpace(call.Title) ? baseCall.Title : call.Title;
        var style = call.Style ?? baseCall.Style;
        var charts = baseCall.Charts.ToList();
        // A chart of the same id is replaced, a new one added.
        foreach (var chart in call.Charts)
        {
            var i = charts.FindIndex(c => c.Id == chart.Id);
            if (i >= 0) charts[i] = chart;
            else charts.Add(chart);
        }
        var baseContent = baseCall.Content ?? "";
        var lines = DocumentHtml.Lines(baseContent);
        if (call.Changes.Count > DocumentSpec.Limits.Changes)
            return Refused($"make_document: at most {DocumentSpec.Limits.Changes} changes at once; write the whole content instead.");
        foreach (var change in call.Changes)
        {
            var text = change.Content.Trim('\n', '\r');
            var added = string.IsNullOrWhiteSpace(text) ? [] : DocumentHtml.Lines(text);
            var sections = Sections(lines);
            if (NonEmpty(change.Section) is { } name)
            {
                var found = Find(name, sections);
                if (found.Count == 0) return Refused(Missing(name, baseTitle, sections));
                if (found.Count > 1) return Refused(Ambiguous(name, found));
                var s = found[0];
                var filled = added.Where(l => l.Trim().Length > 0).ToList();
                if (added.Count == 0)
                {
                    lines.RemoveRange(s.Start, s.End - s.Start);
                }
                else if (filled.Count == 1 && DocumentHtml.Converter.Heading(filled[0].Trim()) is { Text.Length: > 0 })
                {
                    // A heading alone is a new name: the heading line only, its
                    // text and subsections kept (a title's section is the whole document).
                    lines[s.Start] = filled[0];
                }
                else if (filled.Count > 0 && DocumentHtml.Converter.Heading(filled[0].Trim()) is not null)
                {
                    lines.RemoveRange(s.Start, s.End - s.Start);
                    lines.InsertRange(s.Start, [.. added, ""]);
                }
                else
                {
                    // The body only: the section keeps its heading.
                    var heading = lines[s.Start];
                    lines.RemoveRange(s.Start, s.End - s.Start);
                    lines.InsertRange(s.Start, [heading, "", .. added, ""]);
                }
            }
            else if (NonEmpty(change.After) is { } after)
            {
                var found = Find(after, sections);
                if (found.Count > 1) return Refused(Ambiguous(after, found));
                if (found.Count == 0)
                {
                    // After a section that isn't there: at the end, said -- nothing
                    // is lost or doubled, unlike a replace's guess.
                    if (added.Count == 0) continue;
                    lines.AddRange(["", .. added]);
                    adjusted.Add($"There's no section \"{after}\", so the new part was added at the end.");
                    continue;
                }
                if (added.Count == 0) continue;
                lines.InsertRange(found[0].End, ["", .. added, ""]);
            }
            else if (added.Count > 0)
            {
                lines.AddRange(["", .. added]);
            }
        }
        var content = string.Join("\n", lines);
        // Most of the document gone in one call: said, never silent.
        var before = DocumentHtml.Lines(baseContent).Count(l => l.Trim().Length > 0);
        var left = lines.Count(l => l.Trim().Length > 0);
        if (before >= 6 && left > 0 && left * 2 < before)
            adjusted.Add($"The changes removed most of the document ({left} of {before} lines are left). Tell the user, "
                + "and if that wasn't meant, change it again with the sections that should stay.");
        // A chart whose place went with a removed or rewritten section goes too
        // (else it would turn up at the end); one this call sent stays.
        var dropped = DocumentHtml.PlacedCharts(baseContent);
        dropped.ExceptWith(DocumentHtml.PlacedCharts(content));
        var sent = call.Charts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (dropped.Count > 0) charts = charts.Where(c => !dropped.Contains(c.Id) || sent.Contains(c.Id)).ToList();
        if (string.IsNullOrWhiteSpace(content)) adjusted.Add("Every section was removed.");
        return new(new DocumentCall(title, content, [], style, charts), adjusted);
    }

    static DocumentEditResult Refused(string text) => new(null, [], Refusal: text + DocumentTool.NotMade);

    /// <summary>A section: its heading's line, its level, and the line after it (the next heading of its level or above, or the end).</summary>
    internal sealed record Section(string Title, int Level, int Start, int End);

    /// <summary>The sections of a content; headings in fenced code aren't headings.</summary>
    internal static List<Section> Sections(IReadOnlyList<string> lines)
    {
        var heads = new List<(string Title, int Level, int Line)>();
        string? fence = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (fence is not null)
            {
                if (t.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                continue;
            }
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = t[..3];
                continue;
            }
            if (DocumentHtml.Converter.Heading(t) is { } h) heads.Add((h.Text, h.Level, i));
        }
        return heads.Select((h, n) =>
        {
            var end = heads.Skip(n + 1).FirstOrDefault(x => x.Level <= h.Level);
            return new Section(h.Title, h.Level, h.Line, end.Title is null ? lines.Count : end.Line);
        }).ToList();
    }

    /// <summary>
    /// The sections a name means: the same heading, then the same without its
    /// number ("2. Costs" for "Costs"), then the heading that contains it --
    /// the first step with any match decides, and two matches there are a
    /// question back (more than one result), never the first of them.
    /// </summary>
    internal static List<Section> Find(string name, IReadOnlyList<Section> sections)
    {
        var wanted = Normalized(name);
        if (wanted.Length == 0) return [];
        var bare = Unnumbered(wanted);
        Func<Section, bool>[] steps =
        [
            s => Normalized(s.Title) == wanted,
            s => Unnumbered(Normalized(s.Title)) == bare,
            s => Normalized(s.Title).Contains(bare, StringComparison.Ordinal),
        ];
        foreach (var step in steps)
        {
            var found = sections.Where(step).ToList();
            if (found.Count > 0) return found;
        }
        return [];
    }

    static string Ambiguous(string name, List<Section> sections) =>
        $"make_document: several sections fit \"{name}\": {string.Join(", ", sections.Take(10).Select(s => $"\"{s.Title}\""))}. Give the heading exactly as written.";

    static string Missing(string name, string title, List<Section> sections)
    {
        var list = string.Join(", ", sections.Take(40).Select(s => $"\"{s.Title}\""));
        return $"make_document: there's no section \"{name}\" in \"{title}\"."
            + (list.Length == 0 ? " It has no headings: give the whole content instead."
                                : $" Its sections: {list}. Use one of them, or leave \"section\" out to add one at the end.");
    }

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Without the Markdown around it, its case or its extra spaces.</summary>
    internal static string Normalized(string s)
    {
        var t = s.Trim().TrimStart('#');
        t = DocumentHtml.Converter.Plain(t).Replace("_", "");
        return string.Join(" ", t.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"2. costs", "2) costs", "iv. costs" without the number.</summary>
    internal static string Unnumbered(string s)
    {
        var space = s.IndexOf(' ');
        if (space < 0) return s;
        var head = s[..space];
        var marker = head.EndsWith('.') || head.EndsWith(')') ? head[..^1] : head;
        var numeric = marker.Any(char.IsDigit) && marker.All(c => char.IsDigit(c) || c == '.');
        var roman = marker.Length > 0 && marker.Length < head.Length && marker.All(c => "ivxlc".Contains(c));
        return numeric || roman ? s[(space + 1)..] : s;
    }
}

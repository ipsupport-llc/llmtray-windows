using LLMTray.Core.Documents;

namespace LLMTray.Core.Tests;

public sealed class DocumentEditTests
{
    const string BaseContent = """
        # Plan

        Intro.

        ## 1. Costs

        Old costs.

        ![c](chart:costs)

        ### Detail

        Old detail.

        ## Risks

        ```
        ## not a heading
        ```

        Old risks.
        """;

    static DocumentCall Call(string json)
    {
        var parsed = DocumentCall.Parse(json);
        Assert.True(parsed.Call is not null, parsed.Refusal);
        return parsed.Call!;
    }

    static readonly DocumentCall Base = new("Plan", BaseContent, [], "notes",
    [
        new ChartArgs("costs", "bar", null, null, [new ChartSeriesArgs(null, [1, 2], null, null)], null, null),
        new ChartArgs("end", "pie", null, null, [new ChartSeriesArgs(null, [1], null, null)], null, null),
    ]);

    static DocumentEditResult Apply(string json, DocumentCall? baseCall = null) => DocumentEdit.Apply(Call(json), baseCall ?? Base);

    static DocumentEditResult Applied(string json, DocumentCall? baseCall = null)
    {
        var result = Apply(json, baseCall);
        Assert.True(result.Call is not null, result.Refusal);
        return result;
    }

    [Fact]
    public void ReplacesASectionsBodyKeepingItsHeading()
    {
        var output = Applied("""{"title": "Plan", "changes": [{"section": "Costs", "content": "New costs."}]}""");
        var text = output.Call!.Content!;
        Assert.Contains("## 1. Costs\n\nNew costs.", text);
        Assert.DoesNotContain("Old costs", text);
        Assert.DoesNotContain("Old detail", text);   // the subsection went with it
        Assert.Contains("Intro.", text);
        Assert.Contains("Old risks.", text);
        Assert.Equal("notes", output.Call.Style);
        Assert.Empty(output.Call.Changes);
        // Its chart's place went with it: the chart too, not at the end.
        Assert.Equal(["end"], output.Call.Charts.Select(c => c.Id));
    }

    [Fact]
    public void ReplacesWithAHeadingRemovesAddsAndAppends()
    {
        // After "Intro", no heading: at the end, said.
        var appended = Applied("""
            {"title": "Plan v2", "changes": [
              {"section": "## Risks", "content": "## Dangers\n\nNew risks."},
              {"section": "detail", "content": ""},
              {"after": "Intro", "content": "## Notes\n\nA note."},
              {"after": "Plan", "content": ""},
              {"content": "## Ending\n\nBye."}
            ], "charts": [{"id": "costs", "type": "line", "series": [{"values": [3, 4]}]}]}
            """);
        Assert.EndsWith("## Ending\n\nBye.", appended.Call!.Content);
        Assert.Contains("## Notes\n\nA note.", appended.Call.Content);
        Assert.Contains(appended.Adjusted, a => a.Contains("no section \"Intro\""));
        var ok = Applied("""
            {"title": "Plan v2", "changes": [
              {"section": "## Risks", "content": "## Dangers\n\nNew risks."},
              {"section": "detail", "content": ""},
              {"after": "1. Costs", "content": "## Budget\n\nB."},
              {"content": "## Ending\n\nBye."}
            ], "charts": [{"id": "costs", "type": "line", "series": [{"values": [3, 4]}]}]}
            """);
        var text = ok.Call!.Content!;
        Assert.Contains("## Dangers\n\nNew risks.", text);
        Assert.DoesNotContain("Old risks", text);
        Assert.DoesNotContain("## Risks", text);
        Assert.DoesNotContain("Detail", text);
        Assert.EndsWith("## Ending\n\nBye.", text);
        Assert.True(text.IndexOf("Old costs", StringComparison.Ordinal) < text.IndexOf("## Budget", StringComparison.Ordinal));
        Assert.True(text.IndexOf("## Budget", StringComparison.Ordinal) < text.IndexOf("## Dangers", StringComparison.Ordinal));
        Assert.DoesNotContain("not a heading", text);   // a heading in code is part of its section, replaced with it
        Assert.Equal("Plan v2", ok.Call.Title);
        Assert.Equal(["costs", "end"], ok.Call.Charts.Select(c => c.Id));
        Assert.Equal("line", ok.Call.Charts[0].Type);   // replaced by id
    }

    [Fact]
    public void FindsSectionsByNumberCaseAndPart()
    {
        var sections = DocumentEdit.Sections(DocumentHtml.Lines(BaseContent));
        Assert.Equal(["Plan", "1. Costs", "Detail", "Risks"], sections.Select(s => s.Title));
        string? Title(string name, List<DocumentEdit.Section>? list = null) =>
            DocumentEdit.Find(name, list ?? sections) is [var one] ? one.Title : null;
        Assert.Equal("1. Costs", Title("costs"));
        Assert.Equal("1. Costs", Title("2. Costs"));
        Assert.Equal("Risks", Title("**RISKS**"));
        Assert.Equal("1. Costs", Title("Cost"));   // the one heading containing it
        Assert.Null(Title("Budget"));
        Assert.Equal("costs", DocumentEdit.Unnumbered("iv. costs"));
        Assert.Equal("vivid costs", DocumentEdit.Unnumbered("vivid costs"));
        // Two that fit are a question back, never the first of them.
        var twice = DocumentEdit.Sections(DocumentHtml.Lines("# T\n## 1. Costs\nA\n## 2. Costs\nB\n## Summary\n## Summary"));
        Assert.Equal("2. Costs", Title("2. Costs", twice));
        Assert.Equal(2, DocumentEdit.Find("Costs", twice).Count);
        Assert.Equal(2, DocumentEdit.Find("Summary", twice).Count);
        var output = Apply("""{"title": "x", "changes": [{"section": "Costs", "content": "y"}]}""",
            new DocumentCall("T", "# T\n## 1. Costs\nA\n## 2. Costs\nB", [], null, []));
        Assert.Contains("\"1. Costs\", \"2. Costs\"", output.Refusal);
        Assert.EndsWith(DocumentTool.NotMade, output.Refusal);
    }

    /// <summary>A heading alone renames the section; the document stays.</summary>
    [Fact]
    public void AHeadingAloneRenames()
    {
        var output = Applied("""{"title": "Plan", "changes": [{"section": "Plan", "content": "# New plan"}]}""");
        Assert.StartsWith("# New plan\n", output.Call!.Content);
        Assert.Contains("Old costs.", output.Call.Content);
        Assert.Contains("Old risks.", output.Call.Content);
        Assert.Empty(output.Adjusted);
        var sub = Applied("""{"title": "Plan", "changes": [{"section": "Costs", "content": "\n## 1. Budget\n"}]}""");
        Assert.Contains("## 1. Budget\n\nOld costs.", sub.Call!.Content);
    }

    /// <summary>Most of the document removed: the answer says so; everything removed too.</summary>
    [Fact]
    public void RemovingMostIsSaid()
    {
        var output = Applied("""{"title": "Plan", "changes": [{"section": "Plan", "content": "# Plan\n\nShort."}]}""");
        Assert.Contains("removed most of the document", Assert.Single(output.Adjusted));
        var all = Applied("""{"title": "Plan", "changes": [{"section": "Plan", "content": ""}]}""");
        Assert.Contains("Every section was removed.", all.Adjusted);
        Assert.Equal(DocumentProblem.NoContent, DocumentSpec.Read(all.Call!).Problem);
    }

    [Fact]
    public void ChangesAreBounded()
    {
        var many = string.Join(",", Enumerable.Range(0, DocumentSpec.Limits.Changes + 1).Select(_ => """{"content": "x"}"""));
        Assert.Contains("at most 50 changes", Apply($$"""{"title": "x", "changes": [{{many}}]}""").Refusal);
    }

    [Fact]
    public void AMissingSectionListsTheHeadings()
    {
        var refusal = Apply("""{"title": "x", "changes": [{"section": "Budget", "content": "y"}]}""").Refusal!;
        Assert.Contains("\"Budget\"", refusal);
        Assert.Contains("\"Plan\", \"1. Costs\", \"Detail\", \"Risks\"", refusal);
        Assert.Contains("It has no headings", Apply("""{"title": "x", "changes": [{"section": "B", "content": "y"}]}""", new DocumentCall("x", "plain", [], null, [])).Refusal);
    }

    [Fact]
    public void ResolvePicksTheDocumentAndPrefersContent()
    {
        EarlierDocument[] docs = [new("Plan", Base.ToJson()), new("Other", new DocumentCall("Other", "# Other\n\n## Costs\n\nOther costs.", [], null, []).ToJson())];
        // Its title picks it; else the latest.
        var byTitle = DocumentEdit.Resolve(Call("""{"title": "plan", "changes": [{"section": "Costs", "content": "X"}]}"""), docs);
        Assert.Contains("Intro.", byTitle.Call!.Content);
        Assert.Equal("Plan", byTitle.FromTitle);
        Assert.Equal(BaseContent, byTitle.FromContent);
        var latest = DocumentEdit.Resolve(Call("""{"title": "New", "changes": [{"section": "Costs", "content": "X"}]}"""), docs);
        Assert.Contains("# Other", latest.Call!.Content);
        // The whole content wins over changes; no changes, as it came.
        var whole = DocumentEdit.Resolve(Call("""{"title": "N", "content": "All new.", "changes": [{"section": "Costs", "content": "X"}]}"""), docs);
        Assert.Equal("All new.", whole.Call!.Content);
        Assert.Single(whole.Adjusted);
        Assert.Empty(whole.Call.Changes);
        var plain = DocumentEdit.Resolve(Call("""{"title": "N", "content": "Hi"}"""), []);
        Assert.Equal("Hi", plain.Call!.Content);
        Assert.Null(plain.FromTitle);
        // Nothing to change.
        Assert.Null(DocumentEdit.Resolve(Call("""{"title": "N", "changes": [{"content": "X"}]}"""), []).Call);
        // The result reads as a document.
        Assert.NotNull(DocumentSpec.Read(byTitle.Call).Spec);
    }

    [Fact]
    public void Pick()
    {
        Assert.Null(DocumentEdit.Pick("x", []));
        Assert.Equal(2, DocumentEdit.Pick("A", ["A", "B", "a"]));   // the latest of that title
        Assert.Equal(1, DocumentEdit.Pick("Z", ["A", "B"]));
        Assert.Equal(1, DocumentEdit.Pick(null, ["A", "B"]));
    }
}

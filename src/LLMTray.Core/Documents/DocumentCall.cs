using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Documents;

/// <summary>One of <c>changes</c>: replace <see cref="Section"/>, or add after <see cref="After"/>, or (neither) at the end.</summary>
public sealed record DocumentChange(string? Section, string? After, string Content);

/// <summary>A chart's series as the model sent it, before it is checked.</summary>
public sealed record ChartSeriesArgs(string? Name, IReadOnlyList<double>? Values, IReadOnlyList<double>? X, string? Formula);

/// <summary>A chart as the model sent it, before it is checked (<see cref="DocumentSpec"/> does that).</summary>
public sealed record ChartArgs(
    string Id,
    string? Type,
    string? Title,
    IReadOnlyList<string>? Labels,
    IReadOnlyList<ChartSeriesArgs> Series,
    IReadOnlyList<string>? Range,
    string? Unit);

/// <summary>The outcome of <see cref="DocumentCall.Parse"/>: the call, or the answer telling the model what to fix.</summary>
public sealed record DocumentCallParse(DocumentCall? Call, string? Refusal);

/// <summary>
/// A <c>make_document</c> call read leniently but not yet checked: what a
/// change merges into and what is saved with a made document, so Make again
/// and a later change start from the whole call (Mac <c>DocumentSource</c>).
/// </summary>
public sealed record DocumentCall(
    string Title,
    string? Content,
    IReadOnlyList<DocumentChange> Changes,
    string? Style,
    IReadOnlyList<ChartArgs> Charts)
{
    public const string ToolName = "make_document";

    public static readonly string[] Styles = ["report", "letter", "notes", "invoice"];
    public static readonly string[] ChartTypes = ["bar", "stacked", "line", "pie", "scatter"];

    static readonly string[] ContentAliases = ["markdown", "text", "body"];

    /// <summary>The declaration's fields (Mac <c>DocumentSpec.schema</c>), which also drive the reading.</summary>
    internal static readonly DocumentField[] Fields =
    [
        new("title", FieldKind.String, Required: true, Aliases: ["name"]),
        new("content", FieldKind.String, "Markdown; math as LaTeX in $...$ or $$...$$. Place a chart with ![caption](chart:<id>).",
            Aliases: ContentAliases),
        new("changes", FieldKind.Objects,
            "To change the last document made in this chat: only the sections that change, instead of content. The rest stays as it was.",
            Aliases: ["edits", "sections"],
            Fields:
            [
                new("section", FieldKind.String, "The heading of the section to replace.", Aliases: ["heading"]),
                new("after", FieldKind.String, "Or: the heading to add a new section after (neither: at the end)."),
                new("content", FieldKind.String, "The section's new Markdown; a heading alone renames it; empty removes it.", Required: true,
                    Aliases: ContentAliases),
            ]),
        new("style", FieldKind.OneOf, Allowed: Styles,
            ValueAliases: new Dictionary<string, string> { ["memo"] = "notes", ["bill"] = "invoice", ["cover letter"] = "letter" }),
        new("charts", FieldKind.Objects, Fields:
        [
            new("id", FieldKind.String, Required: true),
            // Not required: a formula is a line whatever it's called.
            new("type", FieldKind.OneOf, Allowed: ChartTypes, Aliases: ["kind"],
                ValueAliases: new Dictionary<string, string>
                {
                    ["column"] = "bar", ["bars"] = "bar", ["lines"] = "line", ["donut"] = "pie",
                    ["stacked bar"] = "stacked", ["stacked_bar"] = "stacked", ["stacked column"] = "stacked",
                    ["points"] = "scatter", ["dots"] = "scatter", ["xy"] = "scatter", ["scatter plot"] = "scatter",
                }),
            new("title", FieldKind.String),
            new("labels", FieldKind.Strings, Aliases: ["categories", "x"]),
            new("series", FieldKind.Objects, Required: true, Aliases: ["data"], Fields:
            [
                new("name", FieldKind.String),
                new("values", FieldKind.Numbers, Aliases: ["data", "y"]),
                new("x", FieldKind.Numbers, "A scatter's x of each value.", Aliases: ["xs", "x_values"]),
                new("formula", FieldKind.String, "A math function of x for a line, e.g. sin(x) or x^2: the app computes it. Not for data -- give values.",
                    Aliases: ["function", "expression"]),
            ]),
            new("range", FieldKind.Strings, "x from and to for a formula, e.g. [\"0\", \"2*pi\"].", Aliases: ["x_range", "domain"]),
            new("unit", FieldKind.String),
        ]),
    ];

    static readonly string[] WrapperKeys = ["arguments", "parameters", "args", "input", "params", ToolName];

    /// <summary>The call's arguments text, read leniently (<see cref="LenientJson"/>).</summary>
    /// <summary>
    /// The longest arguments read: the content's 200,000 characters escaped (\uXXXX at worst) and the
    /// charts' numbers fit well within; anything longer is refused before it's parsed.
    /// </summary>
    public const int MaxArgumentChars = 2_600_000;

    public static DocumentCallParse Parse(string argumentsJson)
    {
        if (argumentsJson.Length > MaxArgumentChars) return new(null, Problem(["the arguments are far too long (the content may be 200,000 characters at most)"]));
        JsonNode? node;
        try
        {
            node = LenientJson.Parse(argumentsJson);
        }
        catch (LenientJson.Failure e)
        {
            return new(null, Problem([$"arguments aren't a JSON object ({e.Message})"]));
        }
        // {"arguments": {...}} and the like: the object inside.
        for (var depth = 0; depth < 4 && node is JsonObject { Count: 1 } one; depth++)
        {
            var (key, inner) = one.First();
            if (!WrapperKeys.Contains(key.ToLowerInvariant())) break;
            if (inner is JsonValue v && v.TryGetValue<string>(out var text))
            {
                try { inner = LenientJson.Parse(text); }
                catch (LenientJson.Failure) { inner = null; }
            }
            if (inner is not JsonObject) break;
            node = inner;
        }
        if (node is not JsonObject obj) return new(null, Problem(["arguments aren't a JSON object"]));
        var problems = new List<string>();
        var values = FieldReader.Read(obj, Fields, "", problems);
        if (problems.Count > 0) return new(null, Problem(problems));
        return new(From(values), null);
    }

    static string Problem(List<string> problems) =>
        $"{ToolName}: {string.Join("; ", problems.Take(3))}." + DocumentTool.NotMade;

    static DocumentCall From(Dictionary<string, object> v)
    {
        static string? S(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var o) ? o as string : null;
        static IReadOnlyList<Dictionary<string, object>> Objects(Dictionary<string, object> d, string k) =>
            d.TryGetValue(k, out var o) && o is List<Dictionary<string, object>> l ? l : [];
        var changes = Objects(v, "changes").Select(c => new DocumentChange(S(c, "section"), S(c, "after"), S(c, "content") ?? "")).ToList();
        var charts = Objects(v, "charts").Select(c => new ChartArgs(
            S(c, "id") ?? "",
            S(c, "type"),
            S(c, "title"),
            c.TryGetValue("labels", out var labels) ? (List<string>)labels : null,
            Objects(c, "series").Select(s => new ChartSeriesArgs(
                S(s, "name"),
                s.TryGetValue("values", out var values) ? (List<double>)values : null,
                s.TryGetValue("x", out var x) ? (List<double>)x : null,
                S(s, "formula"))).ToList(),
            c.TryGetValue("range", out var range) ? (List<string>)range : null,
            S(c, "unit"))).ToList();
        return new DocumentCall(S(v, "title") ?? "", S(v, "content"), changes, S(v, "style"), charts);
    }

    /// <summary>The call as JSON (sorted keys): what is saved with a made document and read back by <see cref="Parse"/>.</summary>
    public string ToJson()
    {
        var o = new SortedJson { ["title"] = Title };
        if (Content is not null) o["content"] = Content;
        if (Style is not null) o["style"] = Style;
        if (Changes.Count > 0)
        {
            o["changes"] = new JsonArray(Changes.Select(c =>
            {
                var j = new SortedJson { ["content"] = c.Content };
                if (c.Section is not null) j["section"] = c.Section;
                if (c.After is not null) j["after"] = c.After;
                return (JsonNode)j.ToNode();
            }).ToArray());
        }
        if (Charts.Count > 0) o["charts"] = new JsonArray(Charts.Select(c => (JsonNode)ChartJson(c)).ToArray());
        return o.ToNode().ToJsonString(JsonOptions);
    }

    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static JsonObject ChartJson(ChartArgs c)
    {
        var o = new SortedJson { ["id"] = c.Id };
        if (c.Type is not null) o["type"] = c.Type;
        if (c.Title is not null) o["title"] = c.Title;
        if (c.Unit is not null) o["unit"] = c.Unit;
        if (c.Labels is not null) o["labels"] = new JsonArray(c.Labels.Select(l => (JsonNode)JsonValue.Create(l)).ToArray());
        if (c.Range is not null) o["range"] = new JsonArray(c.Range.Select(r => (JsonNode)JsonValue.Create(r)).ToArray());
        o["series"] = new JsonArray(c.Series.Select(s =>
        {
            var j = new SortedJson();
            if (s.Name is not null) j["name"] = s.Name;
            if (s.Formula is not null) j["formula"] = s.Formula;
            if (s.Values is not null) j["values"] = Numbers(s.Values);
            if (s.X is not null) j["x"] = Numbers(s.X);
            return (JsonNode)j.ToNode();
        }).ToArray());
        return o.ToNode();
    }

    static JsonArray Numbers(IReadOnlyList<double> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

    /// <summary>Keys in order, so the same call is the same text.</summary>
    sealed class SortedJson : SortedDictionary<string, JsonNode?>
    {
        public SortedJson() : base(StringComparer.Ordinal) { }

        public JsonObject ToNode()
        {
            var o = new JsonObject();
            foreach (var (k, v) in this) o[k] = v;
            return o;
        }
    }
}

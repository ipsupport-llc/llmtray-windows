using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMTray.Core.Tools;

namespace LLMTray.Core.Documents;

/// <summary>
/// The kinds a <c>make_document</c> field can have. <see cref="ToolSchema"/>
/// reads scalars only; the document's call has lists of objects (charts,
/// series, changes) and lists of values, so it has its own small schema.
/// </summary>
enum FieldKind { String, Number, OneOf, Objects, Strings, Numbers }

/// <summary>One field of the call: declared once, read and described from the same data (Mac adr/0007).</summary>
sealed record DocumentField(
    string Name,
    FieldKind Kind,
    string? Description = null,
    bool Required = false,
    string[]? Aliases = null,
    string[]? Allowed = null,
    IReadOnlyDictionary<string, string>? ValueAliases = null,
    DocumentField[]? Fields = null)
{
    public string TypeName => Kind switch
    {
        FieldKind.Number => "number",
        FieldKind.OneOf => string.Join("|", Allowed ?? []),
        FieldKind.Objects => "list of objects",
        FieldKind.Strings => "list of strings",
        FieldKind.Numbers => "list of numbers",
        _ => "string",
    };

    public JsonObject JsonSchema()
    {
        JsonObject o;
        switch (Kind)
        {
            case FieldKind.Number:
                o = new JsonObject { ["type"] = "number" };
                break;
            case FieldKind.OneOf:
                o = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray((Allowed ?? []).Select(a => (JsonNode)JsonValue.Create(a)).ToArray()) };
                break;
            case FieldKind.Objects:
                {
                    var properties = new JsonObject();
                    foreach (var f in Fields ?? []) properties[f.Name] = f.JsonSchema();
                    var item = new JsonObject { ["type"] = "object", ["properties"] = properties };
                    var required = (Fields ?? []).Where(f => f.Required).Select(f => (JsonNode)JsonValue.Create(f.Name)).ToArray();
                    if (required.Length > 0) item["required"] = new JsonArray(required);
                    o = new JsonObject { ["type"] = "array", ["items"] = item };
                    break;
                }
            case FieldKind.Strings:
                o = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
                break;
            case FieldKind.Numbers:
                o = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } };
                break;
            default:
                o = new JsonObject { ["type"] = "string" };
                break;
        }
        if (Description is not null) o["description"] = Description;
        return o;
    }
}

/// <summary>
/// Reads an object against fields, leniently as the Mac's parser does:
/// names and aliases in any case or with _ and -, a single value for a
/// one-item list, "5" for 5, a number for a string, a list sent as a JSON
/// string. What can't be read is a problem named by its path
/// (<c>charts[0].type</c>), and the call is answered, not guessed.
/// </summary>
static class FieldReader
{
    /// <summary>The object's values under their declared names: string, double, List of those, or List of dictionaries.</summary>
    public static Dictionary<string, object> Read(JsonObject obj, IReadOnlyList<DocumentField> fields, string prefix, List<string> problems)
    {
        var sent = new Dictionary<string, List<(JsonNode? Value, bool Exact)>>();
        foreach (var (key, value) in obj)
        {
            var exact = fields.FirstOrDefault(f => f.Name == key);
            var field = exact;
            if (field is null)
            {
                var n = ToolArguments.NormalizedKey(key);
                var matches = fields.Where(f => ToolArguments.NormalizedKey(f.Name) == n || (f.Aliases ?? []).Any(a => ToolArguments.NormalizedKey(a) == n)).ToList();
                if (matches.Count != 1) continue;
                field = matches[0];
            }
            if (!sent.TryGetValue(field.Name, out var list)) sent[field.Name] = list = [];
            list.Add((value, exact is not null));
        }
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var path = prefix + field.Name;
            if (!sent.TryGetValue(field.Name, out var given))
            {
                if (field.Required) Problem(problems, $"\"{path}\" is required ({field.TypeName})");
                continue;
            }
            given = given.OrderByDescending(g => g.Exact).ToList();
            if (given.Count > 1 && given.Skip(1).Any(g => !JsonNode.DeepEquals(g.Value, given[0].Value)))
            {
                Problem(problems, $"\"{path}\" was given twice under different names with different values");
                continue;
            }
            if (given[0].Value is not { } node)
            {
                if (field.Required) Problem(problems, $"\"{path}\" is required ({field.TypeName})");
                continue;
            }
            if (Coerce(node, field, path, problems) is { } value) values[field.Name] = value;
        }
        return values;
    }

    /// <summary>
    /// A list longer than any limit could take (charts, series, points, labels, changes all cap far below):
    /// refused whole before its items are read, so a million-item array costs nothing but the parse.
    /// </summary>
    public const int MaxListItems = 2_000;

    /// <summary>Problems kept at most: the model reads the first few anyway.</summary>
    public const int MaxProblems = 50;

    static void Problem(List<string> problems, string text)
    {
        if (problems.Count < MaxProblems) problems.Add(text);
    }

    static object? Coerce(JsonNode node, DocumentField field, string path, List<string> problems)
    {
        switch (field.Kind)
        {
            case FieldKind.Objects:
                {
                    node = Unstring(node, allowObject: true);
                    var items = node switch
                    {
                        JsonArray a => a.ToList(),
                        JsonObject => [node],
                        _ => null,
                    };
                    if (items is { Count: > MaxListItems })
                    {
                        Problem(problems, $"\"{path}\" has {items.Count} items, far over the limit");
                        return null;
                    }
                    if (items is null || items.Any(i => i is not JsonObject))
                    {
                        Problem(problems, $"\"{path}\" must be a {field.TypeName}, not {ShortText(node)}");
                        return null;
                    }
                    var list = new List<Dictionary<string, object>>();
                    for (var i = 0; i < items.Count && problems.Count < MaxProblems; i++) list.Add(Read((JsonObject)items[i]!, field.Fields ?? [], $"{path}[{i}].", problems));
                    return list;
                }
            case FieldKind.Strings or FieldKind.Numbers:
                {
                    node = Unstring(node, allowObject: false);
                    if (node is JsonObject)
                    {
                        Problem(problems, $"\"{path}\" must be a {field.TypeName}, not an object");
                        return null;
                    }
                    var items = node is JsonArray a ? a.ToList() : [node];
                    if (items.Count > MaxListItems)
                    {
                        Problem(problems, $"\"{path}\" has {items.Count} items, far over the limit");
                        return null;
                    }
                    var element = field with { Kind = field.Kind == FieldKind.Strings ? FieldKind.String : FieldKind.Number };
                    if (field.Kind == FieldKind.Strings)
                    {
                        var strings = new List<string>();
                        for (var i = 0; i < items.Count && problems.Count < MaxProblems; i++)
                        {
                            // A null in a list of labels is an empty label.
                            if (items[i] is null) { strings.Add(""); continue; }
                            if (Coerce(items[i]!, element, $"{path}[{i}]", problems) is string s) strings.Add(s);
                        }
                        return strings;
                    }
                    var numbers = new List<double>();
                    for (var i = 0; i < items.Count && problems.Count < MaxProblems; i++)
                    {
                        if (items[i] is null)
                        {
                            Problem(problems, $"\"{path}[{i}]\" must be a number, not null");
                            continue;
                        }
                        if (Coerce(items[i]!, element, $"{path}[{i}]", problems) is double d) numbers.Add(d);
                    }
                    return numbers;
                }
        }
        var kind = node.GetValueKind();
        var text = kind == JsonValueKind.String ? node.GetValue<string>() : null;
        double? number = kind == JsonValueKind.Number ? NumberOf(node) : null;
        switch (field.Kind)
        {
            case FieldKind.String:
                if (text is not null) return text.Trim();
                if (number is double n) return FormatNumber(n);
                break;
            case FieldKind.Number:
                if (number is double d && double.IsFinite(d)) return d;
                // "2.5" yes; "1,5" or "1,000" could be either: no.
                if (text is not null && !text.Contains(',')
                    && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var td) && double.IsFinite(td))
                    return td;
                break;
            case FieldKind.OneOf:
                {
                    if (text is null) break;
                    var allowed = field.Allowed ?? [];
                    var folded = text.Trim().ToLowerInvariant();
                    if (allowed.FirstOrDefault(a => a.ToLowerInvariant() == folded) is { } match) return match;
                    if (field.ValueAliases?.FirstOrDefault(a => a.Key.ToLowerInvariant() == folded) is { Value: { } target } && allowed.Contains(target))
                        return target;
                    Problem(problems, $"\"{path}\" must be one of {string.Join(", ", allowed)}, not {ShortText(node)}");
                    return null;
                }
        }
        Problem(problems, $"\"{path}\" must be a {field.TypeName}, not {ShortText(node)}");
        return null;
    }

    /// <summary>A list (or object) sent as a JSON string: the value it holds.</summary>
    static JsonNode Unstring(JsonNode node, bool allowObject)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var text)) return node;
        var body = text.Trim();
        if (!(body.StartsWith('[') || (allowObject && body.StartsWith('{')))) return node;
        try
        {
            return LenientJson.Parse(body) ?? node;
        }
        catch (LenientJson.Failure)
        {
            return node;
        }
    }

    public static string FormatNumber(double n) =>
        n == Math.Round(n) && Math.Abs(n) < 1e15 ? ((long)n).ToString(CultureInfo.InvariantCulture) : n.ToString("R", CultureInfo.InvariantCulture);

    static double? NumberOf(JsonNode node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        return null;
    }

    static string ShortText(JsonNode node)
    {
        var text = node.GetValueKind() switch
        {
            JsonValueKind.String => $"\"{node.GetValue<string>()}\"",
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => node.ToJsonString(),
        };
        return text.Length > 40 ? text[..39] + "…\"" : text;
    }
}

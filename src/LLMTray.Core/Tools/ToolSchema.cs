using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Tools;

public enum ParamKind { String, Integer, Number, Boolean, OneOf }

/// <summary>One field of a tool's arguments.</summary>
/// <param name="Description">Only where the name doesn't say it.</param>
/// <param name="Aliases">Other names models use for it (q for query).</param>
/// <param name="ValueAliases">Other words for an allowed value (fahrenheit → imperial).</param>
public sealed record ToolParam(
    string Name,
    ParamKind Kind,
    string? Description = null,
    bool Required = false,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Allowed = null,
    IReadOnlyDictionary<string, string>? ValueAliases = null)
{
    public IReadOnlyList<string> AllAliases => Aliases ?? [];

    public string TypeName => Kind switch
    {
        ParamKind.Integer => "integer",
        ParamKind.Number => "number",
        ParamKind.Boolean => "boolean",
        ParamKind.OneOf => string.Join("|", Allowed ?? []),
        _ => "string",
    };

    public JsonObject JsonSchema()
    {
        var o = new JsonObject
        {
            ["type"] = Kind switch
            {
                ParamKind.Integer => "integer",
                ParamKind.Number => "number",
                ParamKind.Boolean => "boolean",
                _ => "string",
            },
        };
        if (Kind == ParamKind.OneOf) o["enum"] = new JsonArray((Allowed ?? []).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        if (Description is not null) o["description"] = Description;
        return o;
    }

    /// <summary>A stand-in for the value in a retry example.</summary>
    public string Placeholder => Kind switch
    {
        ParamKind.Integer => "<integer>",
        ParamKind.Number => "<number>",
        ParamKind.Boolean => "<true|false>",
        ParamKind.OneOf => string.Join("|", Allowed ?? []),
        _ => $"<{Name}>",
    };
}

/// <summary>
/// A tool's declaration as data (Mac adr/0007): the OpenAI function JSON is
/// built from it, and the same fields read the model's arguments leniently
/// and word the error that tells it how to retry.
/// </summary>
public sealed record ToolSchema(string Name, string Description, IReadOnlyList<ToolParam> Params)
{
    public ToolParam? Param(string name) => Params.FirstOrDefault(p => p.Name == name);

    /// <summary>The request's <c>tools</c> entry.</summary>
    public JsonObject Definition()
    {
        var properties = new JsonObject();
        foreach (var p in Params) properties[p.Name] = p.JsonSchema();
        var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = Params.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray();
        if (required.Length > 0) parameters["required"] = new JsonArray(required);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = Name, ["description"] = Description, ["parameters"] = parameters },
        };
    }
}

/// <summary>What a call's arguments came to: values under their declared names (string, long, double, bool).</summary>
public sealed class ToolArguments
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
    public List<string> Repairs { get; } = [];
    public List<string> Problems { get; } = [];
    public bool IsValid => Problems.Count == 0;

    public string? String(string name) => Values.TryGetValue(name, out var v) ? v as string : null;
    public long? Integer(string name) => Values.TryGetValue(name, out var v) && v is long l ? l : null;
    public double? Number(string name) => Values.TryGetValue(name, out var v) ? v switch { double d => d, long l => l, _ => null } : null;
    public bool Has(string name) => Values.ContainsKey(name);

    static readonly string[] WrapperKeys = ["arguments", "parameters", "args", "input", "params"];

    /// <summary>"country_code", "countryCode" and "Country-Code" read alike.</summary>
    public static string NormalizedKey(string key) => new(key.ToLowerInvariant().Where(c => c is not ('_' or '-' or ' ')).ToArray());

    /// <summary><paramref name="raw"/>, the call's arguments text, read against <paramref name="schema"/>.</summary>
    public static ToolArguments Parse(string raw, ToolSchema schema)
    {
        var result = new ToolArguments();
        JsonObject obj;
        try
        {
            if (LenientJson.Parse(raw, result.Repairs) is not JsonObject parsed)
            {
                result.Problems.Add("arguments aren't a JSON object (expected a JSON object)");
                return result;
            }
            obj = parsed;
        }
        catch (LenientJson.Failure e)
        {
            result.Problems.Add($"arguments aren't a JSON object ({e.Message})");
            return result;
        }
        if (Unwrap(obj, schema, result) is not { } args) return result;
        Normalize(args, schema, result);
        return result;
    }

    /// <summary><c>{"arguments": {...}}</c>, <c>{"name": "x", "arguments": "{...}"}</c>: the inner object.</summary>
    static JsonObject? Unwrap(JsonObject obj, ToolSchema schema, ToolArguments result)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            var own = schema.Params.SelectMany(p => p.AllAliases.Append(p.Name)).Select(NormalizedKey).ToHashSet();
            var wrappers = obj.Select(kv => kv.Key)
                .Where(k => !own.Contains(NormalizedKey(k)) && (WrapperKeys.Contains(k.ToLowerInvariant()) || k == schema.Name))
                .ToList();
            if (wrappers.Count == 0) return obj;
            bool DescribesCall(string key) =>
                key.ToLowerInvariant() is "name" or "type"
                && (!own.Contains(NormalizedKey(key)) || obj[key]?.GetValueKind() == JsonValueKind.String
                    && (obj[key]!.GetValue<string>().Trim().Equals("function", StringComparison.OrdinalIgnoreCase)
                        || NormalizedKey(obj[key]!.GetValue<string>()) == NormalizedKey(schema.Name)));
            var beside = obj.Select(kv => kv.Key).Where(k => !wrappers.Contains(k) && !DescribesCall(k)).ToList();
            if (beside.Count > 0)
            {
                var holding = wrappers.FirstOrDefault(w => obj[w] is JsonObject { Count: > 0 });
                if (holding is null) return obj;
                result.Problems.Add($"arguments aren't a JSON object (\"{holding}\" beside other fields ({string.Join(", ", beside.Select(b => $"\"{b}\""))}))");
                return null;
            }
            if (wrappers.Count > 1)
            {
                result.Problems.Add($"arguments aren't a JSON object (arguments wrapped twice: {string.Join(" and ", wrappers.Select(w => $"\"{w}\""))})");
                return null;
            }
            var inner = obj[wrappers[0]];
            if (inner is JsonValue v && v.TryGetValue<string>(out var text) && text.Trim() is { Length: > 0 } body && body != "null")
            {
                try
                {
                    inner = LenientJson.Parse(body, result.Repairs);
                }
                catch (LenientJson.Failure)
                {
                    inner = null;
                }
            }
            if (inner is not JsonObject innerObj)
            {
                result.Problems.Add($"arguments aren't a JSON object (\"{wrappers[0]}\" isn't a JSON object)");
                return null;
            }
            result.Repairs.Add("nested_arguments");
            obj = innerObj;
        }
        return obj;
    }

    static void Normalize(JsonObject obj, ToolSchema schema, ToolArguments result)
    {
        var byParam = new Dictionary<string, List<(string Key, JsonNode? Value, bool Exact)>>();
        foreach (var (key, value) in obj)
        {
            if (schema.Param(key) is not null)
            {
                Add(key, key, value, true);
                continue;
            }
            var n = NormalizedKey(key);
            var matches = schema.Params.Where(p => NormalizedKey(p.Name) == n || p.AllAliases.Any(a => NormalizedKey(a) == n)).ToList();
            if (matches.Count == 1) Add(matches[0].Name, key, value, false);
            else result.Repairs.Add("unknown_field");
        }
        void Add(string param, string key, JsonNode? value, bool exact)
        {
            if (!byParam.TryGetValue(param, out var list)) byParam[param] = list = [];
            list.Add((key, value, exact));
        }

        foreach (var param in schema.Params)
        {
            if (!byParam.TryGetValue(param.Name, out var sent))
            {
                if (param.Required) result.Problems.Add($"\"{param.Name}\" is required ({param.TypeName})");
                continue;
            }
            sent = sent.OrderByDescending(s => s.Exact).ToList();
            if (sent.Count > 1 && !sent[0].Exact && sent.Skip(1).Any(s => !JsonNode.DeepEquals(s.Value, sent[0].Value)))
            {
                result.Problems.Add($"\"{param.Name}\" was given twice under different names with different values");
                continue;
            }
            var chosen = sent[0];
            if (!chosen.Exact) result.Repairs.Add("field_alias");
            if (chosen.Value is null)
            {
                result.Repairs.Add("null_dropped");
                if (param.Required) result.Problems.Add($"\"{param.Name}\" is required ({param.TypeName})");
                continue;
            }
            if (Coerce(chosen.Value, param, result.Repairs, out var value, out var problem)) result.Values[param.Name] = value!;
            else result.Problems.Add(problem!);
        }
    }

    static bool Coerce(JsonNode node, ToolParam param, List<string> repairs, out object? value, out string? problem)
    {
        value = null;
        problem = null;
        var kind = node.GetValueKind();
        string? text = kind == JsonValueKind.String ? node.GetValue<string>() : null;
        double? number = kind == JsonValueKind.Number ? NumberOf(node) : null;
        bool? flag = kind is JsonValueKind.True or JsonValueKind.False ? kind == JsonValueKind.True : null;
        var sent = ShortText(node);
        string Article(string type) => type.Contains('|') ? "one of " + type.Replace("|", ", ") : ("aeiou".Contains(type[0]) ? "an " : "a ") + type;

        switch (param.Kind)
        {
            case ParamKind.String:
                if (text is not null)
                {
                    var trimmed = text.Trim();
                    if (trimmed != text) repairs.Add("whitespace");
                    value = trimmed;
                    return true;
                }
                if (number is double n)
                {
                    value = n == Math.Round(n) && Math.Abs(n) < 1e15 ? ((long)n).ToString(CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
                    repairs.Add("type_coerced");
                    return true;
                }
                break;
            case ParamKind.Integer:
                if (number is double i && i == Math.Round(i) && Math.Abs(i) < 1e15)
                {
                    if (!IsIntegerLiteral(node)) repairs.Add("type_coerced");
                    value = (long)i;
                    return true;
                }
                if (text is not null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ti)
                    && ti == Math.Round(ti) && Math.Abs(ti) < 1e15 && !text.Contains(','))
                {
                    repairs.Add("type_coerced");
                    value = (long)ti;
                    return true;
                }
                break;
            case ParamKind.Number:
                if (number is double d && double.IsFinite(d))
                {
                    value = d;
                    return true;
                }
                // "2.5" yes; "1,5" or "1,000" could be either: no.
                if (text is not null && !text.Contains(',') && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var td) && double.IsFinite(td))
                {
                    repairs.Add("type_coerced");
                    value = td;
                    return true;
                }
                break;
            case ParamKind.Boolean:
                if (flag is bool b)
                {
                    value = b;
                    return true;
                }
                if (text?.Trim().ToLowerInvariant() is "true" or "false")
                {
                    repairs.Add("type_coerced");
                    value = text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                    return true;
                }
                break;
            case ParamKind.OneOf:
                if (text is null) break;
                var allowed = param.Allowed ?? [];
                if (allowed.Contains(text))
                {
                    value = text;
                    return true;
                }
                var folded = text.Trim().ToLowerInvariant();
                if (allowed.FirstOrDefault(a => a.ToLowerInvariant() == folded) is { } match)
                {
                    repairs.Add("enum_value");
                    value = match;
                    return true;
                }
                if (param.ValueAliases?.FirstOrDefault(a => a.Key.ToLowerInvariant() == folded) is { Value: { } target } && allowed.Contains(target))
                {
                    repairs.Add("enum_value");
                    value = target;
                    return true;
                }
                problem = $"\"{param.Name}\" must be one of {string.Join(", ", allowed)}, not {sent}";
                return false;
        }
        problem = $"\"{param.Name}\" must be {Article(param.TypeName)}, not {sent}";
        return false;
    }

    static double? NumberOf(JsonNode node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        return null;
    }

    static bool IsIntegerLiteral(JsonNode node) =>
        node is JsonValue v && (v.TryGetValue<long>(out _) || v.TryGetValue<int>(out _)
            || (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _)));

    static JsonNode? Node(object value) => value switch
    {
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        _ => null,
    };

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

    /// <summary>
    /// What the model reads when its call couldn't be understood: the field
    /// and what it must be, and the call to send instead.
    /// </summary>
    public string? ErrorMessage(ToolSchema schema)
    {
        if (IsValid) return null;
        var example = new JsonObject();
        foreach (var p in schema.Params)
        {
            if (Values.TryGetValue(p.Name, out var v)) example[p.Name] = Node(v);
        }
        foreach (var p in schema.Params)
        {
            var named = Problems.Any(x => x.StartsWith($"\"{p.Name}\"", StringComparison.Ordinal));
            var bad = Problems.Any(x => x.StartsWith("arguments", StringComparison.Ordinal));
            if (named || (bad && p.Required)) example[p.Name] = p.Placeholder;
        }
        var json = example.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("\"<integer>\"", "<integer>").Replace("\"<number>\"", "<number>").Replace("\"<true|false>\"", "<true|false>");
        return $"{schema.Name}: {string.Join("; ", Problems.Take(3))}. Retry: {schema.Name}({json})";
    }
}

/// <summary>Which declared tool a call's name means: exact, another case, a "functions." prefix, or a former name.</summary>
public static class ToolNameResolver
{
    public sealed record Resolved(string Name, IReadOnlyDictionary<string, string> ImpliedArguments, bool Repaired);

    public static Resolved? Resolve(string called, IReadOnlyCollection<string> known, IReadOnlyDictionary<string, (string Tool, Dictionary<string, string> Arguments)>? former = null)
    {
        if (known.Contains(called)) return new Resolved(called, new Dictionary<string, string>(), false);
        var bare = called.Trim();
        foreach (var prefix in new[] { "functions.", "function.", "tools.", "tool." })
        {
            if (bare.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) bare = bare[prefix.Length..];
        }
        var folded = ToolArguments.NormalizedKey(bare);
        var matches = known.Where(k => ToolArguments.NormalizedKey(k) == folded).ToList();
        if (matches.Count == 1) return new Resolved(matches[0], new Dictionary<string, string>(), true);
        if (former is not null)
        {
            var formerMatches = former.Where(f => ToolArguments.NormalizedKey(f.Key) == folded && known.Contains(f.Value.Tool)).ToList();
            if (formerMatches.Count == 1) return new Resolved(formerMatches[0].Value.Tool, formerMatches[0].Value.Arguments, true);
        }
        return null;
    }
}

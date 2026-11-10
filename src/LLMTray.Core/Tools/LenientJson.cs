using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Tools;

/// <summary>
/// Tool-call arguments as small local models write them: JSON, or nearly
/// JSON (Mac adr/0007). Only what reads one way is fixed: a ``` fence,
/// prose around the object, single quotes, trailing commas, Python's
/// True/False/None, bare keys, raw newlines in strings, an object sent as
/// a JSON string. Anything else stays unparsed, with the reason.
/// </summary>
public static class LenientJson
{
    public sealed class Failure(string reason) : Exception(reason);

    /// <summary>The value; empty input or "null" is <c>{}</c>.</summary>
    public static JsonNode Parse(string text, List<string>? repairs = null)
    {
        repairs ??= [];
        var body = text.Trim();
        if (body.Length == 0 || body == "null") return new JsonObject();
        if (body.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = body.IndexOf('\n');
            var close = body.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine > 0 && close > firstLine)
            {
                body = body[(firstLine + 1)..close].Trim();
                repairs.Add("fenced");
            }
        }
        var start = body.IndexOfAny(['{', '[', '"', '\'']);
        if (start < 0) throw new Failure("no JSON object");
        if (start > 0 && body[start] is '{' or '[')
        {
            repairs.Add("surrounding_text");
        }
        else if (start > 0)
        {
            // Prose with a quote in it before the object.
            var brace = body.IndexOf('{');
            if (brace < 0) throw new Failure("no JSON object");
            start = brace;
            repairs.Add("surrounding_text");
        }
        var reader = new Reader(body, start, repairs);
        var value = reader.Value(0);
        reader.SkipSpace();
        if (reader.Position < body.Length)
        {
            // Text after the value: prose, fine; a second object is a guess.
            var rest = body[reader.Position..].TrimStart();
            if (rest.StartsWith('{')) throw new Failure("two JSON objects");
            repairs.Add("surrounding_text");
        }
        // The object sent as a string: '"{\"q\": 1}"'.
        if (value is JsonValue v && v.TryGetValue<string>(out var inner) && inner.TrimStart().StartsWith('{'))
        {
            repairs.Add("double_encoded");
            return Parse(inner, repairs);
        }
        return value ?? new JsonObject();
    }

    sealed class Reader(string text, int position, List<string> repairs)
    {
        public int Position = position;

        public void SkipSpace()
        {
            while (Position < text.Length && char.IsWhiteSpace(text[Position])) Position++;
        }

        char Peek => Position < text.Length ? text[Position] : '\0';

        Failure Fail(string what) => new($"{what} at {Position}");

        public JsonNode? Value(int depth)
        {
            if (depth > 64) throw Fail("nested too deeply");
            SkipSpace();
            switch (Peek)
            {
                case '{': return Object(depth);
                case '[': return Array(depth);
                case '"':
                case '\'':
                    return JsonValue.Create(String());
                case '\0': throw Fail("unexpected end");
            }
            if (Peek == '-' || Peek == '+' || char.IsAsciiDigit(Peek) || Peek == '.') return Number();
            var word = Word();
            switch (word)
            {
                case "true": return JsonValue.Create(true);
                case "false": return JsonValue.Create(false);
                case "null": return null;
                case "True":
                    repairs.Add("python_literal");
                    return JsonValue.Create(true);
                case "False":
                    repairs.Add("python_literal");
                    return JsonValue.Create(false);
                case "None":
                    repairs.Add("python_literal");
                    return null;
            }
            throw Fail(word.Length == 0 ? $"unexpected '{Peek}'" : $"unexpected '{word}'");
        }

        string Word()
        {
            var begin = Position;
            while (Position < text.Length && (char.IsLetterOrDigit(text[Position]) || text[Position] is '_' or '-' or '$')) Position++;
            return text[begin..Position];
        }

        JsonObject Object(int depth)
        {
            Position++;
            var obj = new JsonObject();
            while (true)
            {
                SkipSpace();
                if (Peek == '}')
                {
                    Position++;
                    return obj;
                }
                string key;
                if (Peek is '"' or '\'')
                {
                    key = String();
                }
                else
                {
                    key = Word();
                    if (key.Length == 0) throw Fail("expected a key");
                    repairs.Add("unquoted_key");
                }
                SkipSpace();
                if (Peek != ':') throw Fail("expected ':'");
                Position++;
                var value = Value(depth + 1);
                // The last of a repeated key wins, as in JSON readers.
                obj[key] = value;
                SkipSpace();
                if (Peek == ',')
                {
                    Position++;
                    SkipSpace();
                    if (Peek == '}') repairs.Add("trailing_comma");
                    continue;
                }
                if (Peek == '}') continue;
                throw Fail("expected ',' or '}'");
            }
        }

        JsonArray Array(int depth)
        {
            Position++;
            var array = new JsonArray();
            while (true)
            {
                SkipSpace();
                if (Peek == ']')
                {
                    Position++;
                    return array;
                }
                array.Add(Value(depth + 1));
                SkipSpace();
                if (Peek == ',')
                {
                    Position++;
                    SkipSpace();
                    if (Peek == ']') repairs.Add("trailing_comma");
                    continue;
                }
                if (Peek == ']') continue;
                throw Fail("expected ',' or ']'");
            }
        }

        string String()
        {
            var quote = text[Position++];
            if (quote == '\'') repairs.Add("single_quotes");
            var sb = new StringBuilder();
            while (true)
            {
                if (Position >= text.Length) throw Fail("unterminated string");
                var c = text[Position++];
                if (c == quote) return sb.ToString();
                if (c == '\\')
                {
                    if (Position >= text.Length) throw Fail("unterminated string");
                    var e = text[Position++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (Position + 4 > text.Length || !int.TryParse(text.AsSpan(Position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw Fail("bad \\u escape");
                            sb.Append((char)code);
                            Position += 4;
                            break;
                        case '"' or '\'' or '\\' or '/': sb.Append(e); break;
                        // Not an escape ("C:\Windows"): the backslash stays, nothing is guessed away.
                        default: sb.Append('\\').Append(e); break;
                    }
                    continue;
                }
                if (c is '\n' or '\r' or '\t') repairs.Add("raw_control_character");
                sb.Append(c);
            }
        }

        JsonNode Number()
        {
            var begin = Position;
            if (Peek is '-' or '+') Position++;
            while (Position < text.Length && (char.IsAsciiDigit(text[Position]) || text[Position] is '.' or 'e' or 'E'
                || (text[Position] is '+' or '-' && text[Position - 1] is 'e' or 'E'))) Position++;
            var s = text[begin..Position].TrimStart('+');
            var integer = !s.Contains('.') && !s.Contains('e') && !s.Contains('E');
            if (integer && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            {
                // Too big to keep exactly: kept as text so nothing ambiguous is guessed.
                if (integer) return JsonValue.Create(s);
                return JsonValue.Create(d);
            }
            throw Fail($"bad number '{s}'");
        }
    }
}

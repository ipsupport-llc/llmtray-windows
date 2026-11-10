using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Tools;

/// <summary>What went wrong in an expression, in words the model can act on.</summary>
public sealed class CalculatorException(CalculatorException.Kind kind, string detail) : Exception(Describe(kind, detail))
{
    public enum Kind { Syntax, Unknown, Domain }

    public Kind Failure { get; } = kind;
    public string Detail { get; } = detail;

    static string Describe(Kind kind, string detail) => kind switch
    {
        Kind.Syntax => $"syntax error: {detail}",
        // The declaration names only some functions: the rest are here.
        Kind.Unknown => $"unknown name: {detail}. Functions: {string.Join(", ", Calculator.FunctionNames)}; constants: pi, e, tau",
        _ => $"math error: {detail}",
    };
}

/// <summary>
/// Evaluates arithmetic for the chat's <c>calculate</c> tool with a small
/// recursive-descent parser: nothing but numbers, operators and a fixed list
/// of math functions, never code.
///
/// Grammar: <c>+ - * / // % ^ **</c>, unary minus, parentheses, the constants
/// <c>pi e tau</c>, and functions like <c>sqrt log(x[, base]) round(x[, digits])
/// min max hypot factorial gcd</c>.
/// </summary>
public static partial class Calculator
{
    /// <summary>
    /// <paramref name="variables"/> (lowercase names, like <c>x</c> in a chart's
    /// formula) are read like the constants and win over them.
    /// </summary>
    public static double Evaluate(string expression, IReadOnlyDictionary<string, double>? variables = null)
    {
        var parser = new Parser(Tokenize(PercentOf(expression)), variables ?? new Dictionary<string, double>());
        var value = parser.Expression();
        if (!parser.AtEnd) throw Syntax($"unexpected '{parser.PeekText}'");
        if (!double.IsFinite(value)) throw new CalculatorException(CalculatorException.Kind.Domain, "result is not finite");
        return value;
    }

    /// <summary>The result as the tool reports it: integers without ".0", otherwise up to 12 significant digits.</summary>
    public static string Format(double value)
    {
        if (value == Math.Round(value) && Math.Abs(value) < 1e15) return ((long)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("G12", CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    static CalculatorException Syntax(string detail) => new(CalculatorException.Kind.Syntax, detail);
    static CalculatorException Domain(string detail) => new(CalculatorException.Kind.Domain, detail);

    // Lexer

    enum TokenKind { Number, Name, Op, LParen, RParen, Comma }

    readonly record struct Token(TokenKind Kind, double Number = 0, string Text = "")
    {
        public override string ToString() => Kind switch
        {
            TokenKind.Number => $"number({Format(Number)})",
            TokenKind.Name => $"name({Text})",
            TokenKind.Op => $"op({Text})",
            TokenKind.LParen => "(",
            TokenKind.RParen => ")",
            _ => ",",
        };
    }

    static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == '_')
            {
                i++;
                continue;
            }
            if (char.IsDigit(c) || c == '.')
            {
                var j = i;
                while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == '_')) j++;
                // Exponent: 1e10, 2.5E-3.
                if (j < s.Length && (s[j] == 'e' || s[j] == 'E'))
                {
                    var k = j + 1;
                    if (k < s.Length && (s[k] == '+' || s[k] == '-')) k++;
                    if (k < s.Length && char.IsDigit(s[k]))
                    {
                        j = k;
                        while (j < s.Length && char.IsDigit(s[j])) j++;
                    }
                }
                var text = s[i..j].Replace("_", "");
                if (!double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value)
                    || text.Count(ch => ch == '.') > 1)
                {
                    throw Syntax($"bad number '{text}'");
                }
                tokens.Add(new Token(TokenKind.Number, value));
                i = j;
                continue;
            }
            if (char.IsLetter(c))
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                var name = s[i..j].ToLowerInvariant();
                // "math.sqrt(2)": a Python-style prefix, dropped.
                if (name == "math" && j < s.Length && s[j] == '.')
                {
                    i = j + 1;
                    continue;
                }
                tokens.Add(new Token(TokenKind.Name, Text: name));
                i = j;
                continue;
            }
            if (i + 1 < s.Length && (s.AsSpan(i, 2).SequenceEqual("**") || s.AsSpan(i, 2).SequenceEqual("//")))
            {
                tokens.Add(new Token(TokenKind.Op, Text: s.Substring(i, 2)));
                i += 2;
                continue;
            }
            switch (c)
            {
                case '+' or '-' or '*' or '/' or '%' or '^': tokens.Add(new Token(TokenKind.Op, Text: c.ToString())); break;
                case '×': tokens.Add(new Token(TokenKind.Op, Text: "*")); break;
                case '÷': tokens.Add(new Token(TokenKind.Op, Text: "/")); break;
                case '(': tokens.Add(new Token(TokenKind.LParen)); break;
                case ')': tokens.Add(new Token(TokenKind.RParen)); break;
                case ',': tokens.Add(new Token(TokenKind.Comma)); break;
                default: throw Syntax($"unexpected character '{c}'");
            }
            i++;
        }
        return tokens;
    }

    // Parser

    sealed class Parser(List<Token> tokens, IReadOnlyDictionary<string, double> variables)
    {
        int position;
        int depth;

        public bool AtEnd => position >= tokens.Count;
        public string PeekText => AtEnd ? "end" : tokens[position].ToString();

        Token? Next() => AtEnd ? null : tokens[position++];
        Token? Peek() => AtEnd ? null : tokens[position];

        bool PeekOp(params string[] ops) => Peek() is { Kind: TokenKind.Op } t && ops.Contains(t.Text);

        void Enter()
        {
            depth++;
            if (depth >= 200) throw Syntax("expression nested too deeply");
        }

        // expression := term (('+' | '-') term)*
        public double Expression()
        {
            Enter();
            try
            {
                var value = Term();
                while (PeekOp("+", "-"))
                {
                    var op = tokens[position++].Text;
                    var rhs = Term();
                    value = op == "+" ? value + rhs : value - rhs;
                }
                return value;
            }
            finally
            {
                depth--;
            }
        }

        // term := unary (('*' | '/' | '//' | '%') unary)*
        double Term()
        {
            var value = Unary();
            while (PeekOp("*", "/", "//", "%"))
            {
                var op = tokens[position++].Text;
                var rhs = Unary();
                switch (op)
                {
                    case "*": value *= rhs; break;
                    case "/":
                        if (rhs == 0) throw Domain("division by zero");
                        value /= rhs;
                        break;
                    case "//":
                        if (rhs == 0) throw Domain("division by zero");
                        value = Math.Floor(value / rhs);
                        break;
                    default:
                        if (rhs == 0) throw Domain("modulo by zero");
                        value -= rhs * Math.Floor(value / rhs);   // Python's sign convention
                        break;
                }
            }
            return value;
        }

        // unary := ('-' | '+') unary | power   -- so -2^2 = -4, as in Python
        double Unary()
        {
            Enter();
            try
            {
                if (PeekOp("-", "+"))
                {
                    var op = tokens[position++].Text;
                    var v = Unary();
                    return op == "-" ? -v : v;
                }
                return Power();
            }
            finally
            {
                depth--;
            }
        }

        // power := primary (('^' | '**') unary)?   -- right-associative
        double Power()
        {
            var b = Primary();
            if (PeekOp("^", "**"))
            {
                position++;
                var exponent = Unary();
                var result = Math.Pow(b, exponent);
                if (double.IsNaN(result)) throw Domain($"{Format(b)} ^ {Format(exponent)} is undefined");
                return result;
            }
            return b;
        }

        double Primary()
        {
            var token = Next();
            switch (token)
            {
                case { Kind: TokenKind.Number } t:
                    return t.Number;
                case { Kind: TokenKind.LParen }:
                    {
                        var v = Expression();
                        if (Next() is not { Kind: TokenKind.RParen }) throw Syntax("missing ')'");
                        return v;
                    }
                case { Kind: TokenKind.Name } t:
                    {
                        var name = t.Text;
                        if (Peek() is { Kind: TokenKind.LParen })
                        {
                            position++;
                            var args = new List<double>();
                            if (Peek() is { Kind: TokenKind.RParen })
                            {
                                position++;
                            }
                            else
                            {
                                while (true)
                                {
                                    args.Add(Expression());
                                    var after = Next();
                                    if (after is { Kind: TokenKind.Comma }) continue;
                                    if (after is { Kind: TokenKind.RParen }) break;
                                    throw Syntax($"missing ')' after arguments of {name}");
                                }
                            }
                            return Call(name, args);
                        }
                        if (variables.TryGetValue(name, out var variable)) return variable;
                        if (Constants.TryGetValue(name, out var constant)) return constant;
                        throw new CalculatorException(CalculatorException.Kind.Unknown, name);
                    }
                case null:
                    throw Syntax("unexpected end");
                default:
                    throw Syntax($"unexpected {token}");
            }
        }
    }

    /// <summary>"15% of 2450", as small models write it: <c>(15 / 100) * 2450</c>. Only with "of": a bare % stays modulo.</summary>
    static string PercentOf(string expression) => PercentOfPattern().Replace(expression, "($1 / 100) * ");

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%\s*of\s+", RegexOptions.IgnoreCase)]
    private static partial Regex PercentOfPattern();

    static readonly Dictionary<string, double> Constants = new() { ["pi"] = Math.PI, ["e"] = Math.E, ["tau"] = 2 * Math.PI };

    /// <summary>What <see cref="Call"/> knows (for the error naming an unknown one).</summary>
    public static readonly IReadOnlyList<string> FunctionNames =
    [
        "sqrt", "cbrt", "log", "ln", "log2", "log10", "exp", "sin", "cos", "tan", "asin", "acos",
        "atan", "atan2", "sinh", "cosh", "tanh", "floor", "ceil", "trunc", "abs", "degrees", "radians",
        "round", "min", "max", "sum", "pow", "hypot", "factorial", "gcd", "lcm",
    ];

    static double Call(string name, List<double> a)
    {
        void Arity(int low, int high)
        {
            if (a.Count < low || a.Count > high)
                throw Syntax($"{name} takes {(low == high ? $"{low}" : $"{low}-{high}")} argument(s)");
        }
        void Check(bool ok, string why)
        {
            if (!ok) throw Domain(why);
        }
        switch (name)
        {
            case "sqrt": Arity(1, 1); Check(a[0] >= 0, "sqrt of a negative number"); return Math.Sqrt(a[0]);
            case "cbrt": Arity(1, 1); return Math.Cbrt(a[0]);
            case "log" or "ln":
                Arity(1, 2);
                Check(a[0] > 0, "log of a non-positive number");
                if (a.Count == 2)
                {
                    Check(a[1] > 0 && a[1] != 1, "bad log base");
                    return Math.Log(a[0]) / Math.Log(a[1]);
                }
                return Math.Log(a[0]);
            case "log2": Arity(1, 1); Check(a[0] > 0, "log of a non-positive number"); return Math.Log2(a[0]);
            case "log10" or "lg": Arity(1, 1); Check(a[0] > 0, "log of a non-positive number"); return Math.Log10(a[0]);
            case "exp": Arity(1, 1); return Math.Exp(a[0]);
            case "sin": Arity(1, 1); return Math.Sin(a[0]);
            case "cos": Arity(1, 1); return Math.Cos(a[0]);
            case "tan": Arity(1, 1); return Math.Tan(a[0]);
            case "asin": Arity(1, 1); Check(Math.Abs(a[0]) <= 1, "asin outside [-1, 1]"); return Math.Asin(a[0]);
            case "acos": Arity(1, 1); Check(Math.Abs(a[0]) <= 1, "acos outside [-1, 1]"); return Math.Acos(a[0]);
            case "atan": Arity(1, 1); return Math.Atan(a[0]);
            case "atan2": Arity(2, 2); return Math.Atan2(a[0], a[1]);
            case "sinh": Arity(1, 1); return Math.Sinh(a[0]);
            case "cosh": Arity(1, 1); return Math.Cosh(a[0]);
            case "tanh": Arity(1, 1); return Math.Tanh(a[0]);
            case "floor": Arity(1, 1); return Math.Floor(a[0]);
            case "ceil": Arity(1, 1); return Math.Ceiling(a[0]);
            case "trunc": Arity(1, 1); return Math.Truncate(a[0]);
            case "abs" or "fabs": Arity(1, 1); return Math.Abs(a[0]);
            case "degrees": Arity(1, 1); return a[0] * 180 / Math.PI;
            case "radians": Arity(1, 1); return a[0] * Math.PI / 180;
            case "round":
                {
                    Arity(1, 2);
                    var digits = a.Count == 2 ? a[1] : 0;
                    var scale = Math.Pow(10, digits);
                    return Math.Round(a[0] * scale, MidpointRounding.ToEven) / scale;
                }
            case "min": Arity(1, 64); return a.Min();
            case "max": Arity(1, 64); return a.Max();
            case "sum": Arity(1, 64); return a.Sum();
            case "pow": Arity(2, 2); return Math.Pow(a[0], a[1]);
            case "hypot": Arity(2, 2); return Math.Sqrt(a[0] * a[0] + a[1] * a[1]);
            case "factorial":
                {
                    Arity(1, 1);
                    Check(a[0] >= 0 && a[0] == Math.Round(a[0]) && a[0] <= 170, "factorial needs a whole number 0...170");
                    var result = 1.0;
                    for (var k = 1; k <= (int)a[0]; k++) result *= k;
                    return result;
                }
            case "gcd" or "lcm":
                {
                    Arity(2, 2);
                    Check(a.All(v => v == Math.Round(v) && Math.Abs(v) < 9e15), $"{name} needs whole numbers");
                    long x = (long)Math.Abs(a[0]), y = (long)Math.Abs(a[1]);
                    long ox = x, oy = y;
                    while (y != 0) (x, y) = (y, x % y);
                    if (name == "gcd") return x;
                    return x == 0 ? 0 : (double)(ox / x) * oy;   // no long overflow
                }
            default:
                throw new CalculatorException(CalculatorException.Kind.Unknown, name);
        }
    }
}

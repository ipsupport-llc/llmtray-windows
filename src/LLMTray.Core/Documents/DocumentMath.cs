using System.Text;

namespace LLMTray.Core.Documents;

/// <summary>
/// Math in a document's text (Mac adr/0020, "Math"): the LaTeX a model writes
/// between <c>$…$</c> / <c>$$…$$</c> (or <c>\(…\)</c> / <c>\[…\]</c>) as
/// MathML, which Chromium lays out and prints (MathML Core). Our own reader
/// of the common part of LaTeX -- fractions, roots, scripts, sums and
/// integrals, Greek, operators, accents, matrices and cases -- no script and
/// no package. Like the rest of the converter it emits only its own tags
/// (<see cref="Tags"/>), every text escaped; an unknown command shows as written.
/// </summary>
public static class DocumentMath
{
    /// <summary>What it can emit (part of <see cref="DocumentHtml.AllowedTags"/>).</summary>
    public static readonly IReadOnlySet<string> Tags = new HashSet<string>
    {
        "math", "mrow", "mi", "mn", "mo", "mtext", "msup", "msub", "msubsup", "mfrac", "msqrt", "mroot",
        "mover", "munder", "munderover", "mtable", "mtr", "mtd",
    };

    /// <summary>How long a formula may be (scalars), and how deep its groups nest.</summary>
    public const int MaxScalars = 4_000;
    const int MaxDepth = 24;

    /// <summary>MathML for <paramref name="tex"/>, or null when it can't be read (unbalanced braces, too long, too deep): the text then stays as written.</summary>
    public static string? MathML(string tex, bool display)
    {
        if (DocumentSpec.Scalars(tex) > MaxScalars || string.IsNullOrWhiteSpace(tex)) return null;
        var reader = new Reader(tex.EnumerateRunes().Select(r => r.Value).ToArray());
        try
        {
            var row = reader.Expression([]);
            if (!reader.AtEnd) return null;
            return (display ? "<math display=\"block\">" : "<math>") + Mrow(row) + "</math>";
        }
        catch (Failure)
        {
            return null;
        }
    }

    sealed class Failure : Exception;

    static string Str(int scalar) => char.ConvertFromUtf32(scalar);
    static bool IsAsciiLetter(int c) => c < 128 && char.IsAsciiLetter((char)c);
    static bool IsAsciiDigit(int c) => c is >= '0' and <= '9';
    static bool IsLetter(int c) => Rune.IsLetter(new Rune(c));
    static bool IsSpace(int c) => Rune.IsWhiteSpace(new Rune(c));

    sealed class Reader(int[] s)
    {
        int i;
        int depth;
        /// <summary>The atom just read was a function name (sin, log): "applied to" and a thin space go after its scripts.</summary>
        bool function;

        public bool AtEnd => i >= s.Length;
        int? Peek(int k = 0) => i + k < s.Length ? s[i + k] : null;

        void SkipSpace()
        {
            while (Peek() is int c && IsSpace(c)) i++;
        }

        /// <summary>Atoms with their scripts until one of <paramref name="stops"/> (such as "\end", "&amp;", "\\", "}" or "\right") or the end.</summary>
        public List<string> Expression(HashSet<string> stops)
        {
            depth++;
            try
            {
                if (depth > MaxDepth) throw new Failure();
                var output = new List<string>();
                while (true)
                {
                    SkipSpace();
                    if (Peek() is not int c) break;
                    if (c == '}')
                    {
                        if (stops.Contains("}")) break;
                        throw new Failure();
                    }
                    if (c == '&' && stops.Contains("&")) break;
                    if (c == ']' && stops.Contains("]")) break;
                    if (c == '\\' && CommandName() is { } name && stops.Contains("\\" + name)) break;
                    if (c is '^' or '_')
                    {
                        // A script with nothing before it: on an empty base.
                        output.Add(Scripts("<mrow></mrow>", false));
                        continue;
                    }
                    function = false;
                    if (Atom() is not { } atom) continue;
                    var applied = function;
                    var limits = atom.Limits;
                    SkipSpace();
                    // \sum\limits_…: the operator's scripts, placed as asked.
                    while (CommandName() is "limits" or "nolimits")
                    {
                        limits = CommandName() == "limits";
                        ReadCommand();
                        SkipSpace();
                    }
                    if (Peek() is '^' or '_' or '\'')
                    {
                        // Nothing to put them on (a space, \displaystyle): an empty base.
                        output.Add(Scripts(atom.Html.Length == 0 ? "<mrow></mrow>" : atom.Html, limits));
                    }
                    else
                    {
                        output.Add(atom.Html);
                    }
                    if (applied)
                    {
                        SkipSpace();
                        // sin x, but sin(x).
                        var paren = Peek() == '(' || (Peek() == '\\' && CommandName() is "left" or "bigl" or "Bigl" or "big" or "Big");
                        output.Add("<mo>⁡</mo>" + (paren ? "" : "<mtext> </mtext>"));
                    }
                }
                return output;
            }
            finally
            {
                depth--;
                // A group is never a function name, whatever it ends with.
                function = false;
            }
        }

        /// <summary>The name of the command at i (a \ there), not consumed.</summary>
        string? CommandName()
        {
            if (Peek() != '\\' || Peek(1) is not int first) return null;
            if (!IsAsciiLetter(first)) return Str(first);
            var k = i + 1;
            var name = new StringBuilder();
            while (k < s.Length && IsAsciiLetter(s[k])) name.Append((char)s[k++]);
            return name.ToString();
        }

        string ReadCommand()
        {
            var name = CommandName() ?? "";
            i += 1 + name.EnumerateRunes().Count();
            return name;
        }

        /// <summary>^, _ and primes after a base: msub, msup, msubsup, or under and over for a sum's limits.</summary>
        string Scripts(string @base, bool limits)
        {
            string? sub = null, sup = null;
            var primes = "";
            while (true)
            {
                SkipSpace();
                if (Peek() is not int c) break;
                if (c == '\'') { primes += "′"; i++; continue; }
                if (c == '_' && sub is null) { i++; sub = Argument(); continue; }
                if (c == '^' && sup is null) { i++; sup = Argument(); continue; }
                break;
            }
            if (primes.Length > 0) sup = sup is null ? $"<mo>{primes}</mo>" : $"<mrow><mo>{primes}</mo>{sup}</mrow>";
            return (sub, sup) switch
            {
                ({ } b, { } p) => limits ? $"<munderover>{@base}{b}{p}</munderover>" : $"<msubsup>{@base}{b}{p}</msubsup>",
                ({ } b, null) => limits ? $"<munder>{@base}{b}</munder>" : $"<msub>{@base}{b}</msub>",
                (null, { } p) => limits ? $"<mover>{@base}{p}</mover>" : $"<msup>{@base}{p}</msup>",
                _ => @base,
            };
        }

        /// <summary>A command's argument or a script: {…} as one row, else one atom (x^2, \frac12).</summary>
        string Argument()
        {
            SkipSpace();
            if (Peek() is not int c) throw new Failure();
            if (c == '{')
            {
                i++;
                var row = Expression(["}"]);
                if (Peek() != '}') throw new Failure();
                i++;
                return Mrow(row);
            }
            if (IsAsciiDigit(c))
            {
                // One digit: \frac12 is 1 over 2.
                i++;
                return $"<mn>{(char)c}</mn>";
            }
            return Atom()?.Html ?? throw new Failure();
        }

        /// <summary>The text between braces as written (\text{…}).</summary>
        string RawArgument()
        {
            SkipSpace();
            if (Peek() != '{')
            {
                if (Peek() is not int one) throw new Failure();
                i++;
                return Str(one);
            }
            i++;
            var level = 1;
            var output = new StringBuilder();
            while (Peek() is int c)
            {
                i++;
                // An escaped brace (or anything escaped) is text, not a group.
                if (c == '\\' && Peek() is int next)
                {
                    output.Append('\\').Append(Str(next));
                    i++;
                    continue;
                }
                if (c == '{') level++;
                if (c == '}' && --level == 0) return output.ToString();
                output.Append(Str(c));
            }
            throw new Failure();
        }

        /// <summary>[…] if there: \sqrt's index.</summary>
        string? Optional()
        {
            SkipSpace();
            if (Peek() != '[') return null;
            i++;
            var row = Expression(["]"]);
            if (Peek() != ']') throw new Failure();
            i++;
            return Mrow(row);
        }

        /// <summary>One thing: a number, a letter, an operator, a group or a command. Limits: its scripts go under and over (∑, lim).</summary>
        (string Html, bool Limits)? Atom()
        {
            // \sqrt\sqrt\sqrt… nests without braces: counted here too.
            depth++;
            try
            {
                if (depth > MaxDepth * 2) throw new Failure();
                SkipSpace();
                if (Peek() is not int c) return null;
                if (c == '{') return (Argument(), false);
                if (c == '\\') return Command();
                if (IsAsciiDigit(c))
                {
                    var number = new StringBuilder();
                    while (Peek() is int d && (IsAsciiDigit(d) || (d == '.' && Peek(1) is int e && IsAsciiDigit(e))))
                    {
                        number.Append((char)d);
                        i++;
                    }
                    return ($"<mn>{number}</mn>", false);
                }
                i++;
                if (IsLetter(c)) return ($"<mi>{DocumentHtml.Escape(Str(c))}</mi>", false);
                switch (c)
                {
                    case '-': return ("<mo>−</mo>", false);
                    case '~': return ("<mtext> </mtext>", false);
                    case '&': return ("<mo>&amp;</mo>", false);   // outside a table: as written
                    case '*': return ("<mo>∗</mo>", false);
                    case '(' or ')' or '[' or ']' or '|':
                        // As written, not stretched: \left and \right stretch.
                        return ($"<mo stretchy=\"false\">{(char)c}</mo>", false);
                }
                if (BigOperators.TryGetValue(Str(c), out var big)) return ($"<mo>{big}</mo>", true);
                return ($"<mo>{DocumentHtml.Escape(Str(c))}</mo>", false);
            }
            finally
            {
                depth--;
            }
        }

        (string Html, bool Limits)? Command()
        {
            var name = ReadCommand();
            if (name.Length == 0) return ("<mo>\\</mo>", false);
            if (Spaces.TryGetValue(name, out var space)) return (space.Length == 0 ? "" : $"<mtext>{space}</mtext>", false);
            if (Identifiers.TryGetValue(name, out var symbol)) return ($"<mi>{DocumentHtml.Escape(symbol)}</mi>", false);
            if (Upright.TryGetValue(name, out var upright)) return ($"<mi mathvariant=\"normal\">{DocumentHtml.Escape(upright)}</mi>", false);
            if (Fences.TryGetValue(name, out var fence)) return ($"<mo stretchy=\"false\">{DocumentHtml.Escape(fence)}</mo>", false);
            if (Operators.TryGetValue(name, out var op)) return ($"<mo>{DocumentHtml.Escape(op)}</mo>", false);
            if (BigOperators.TryGetValue(name, out var big)) return ($"<mo>{big}</mo>", !Integrals.Contains(name));
            if (Functions.Contains(name))
            {
                // An upright name, then an invisible "applied to".
                function = true;
                return ($"<mi>{name}</mi>", LimitFunctions.Contains(name));
            }
            if (Accents.TryGetValue(name, out var accent)) return ($"<mover accent=\"true\">{Argument()}<mo>{accent}</mo></mover>", false);
            switch (name)
            {
                case "frac" or "dfrac" or "tfrac" or "cfrac":
                    {
                        var top = Argument();
                        return ($"<mfrac>{top}{Argument()}</mfrac>", false);
                    }
                case "binom" or "dbinom" or "tbinom":
                    {
                        var top = Argument();
                        return ($"<mrow><mo>(</mo><mfrac linethickness=\"0\">{top}{Argument()}</mfrac><mo>)</mo></mrow>", false);
                    }
                case "left":
                    {
                        // \left( … \right): the content between, the fences stretched to it.
                        var open = Delimiter(true);
                        var inner = Expression(["\\right"]);
                        if (CommandName() != "right") throw new Failure();
                        ReadCommand();
                        return ($"<mrow>{open}{Mrow(inner)}{Delimiter(true)}</mrow>", false);
                    }
                case "right" or "middle":
                    return (Delimiter(true), false);
                case "bigl" or "bigr" or "Bigl" or "Bigr" or "big" or "Big" or "bigg" or "Bigg" or "biggl" or "biggr" or "Biggl" or "Biggr":
                    return (Delimiter(false), false);
                case "sqrt":
                    if (Optional() is { } index) return ($"<mroot>{Argument()}{index}</mroot>", false);
                    return ($"<msqrt>{Argument()}</msqrt>", false);
                case "underline":
                    return ($"<munder>{Argument()}<mo>_</mo></munder>", false);
                case "text" or "textrm" or "textit" or "textbf" or "mbox" or "textnormal":
                    return ($"<mtext>{DocumentHtml.Escape(TextEscapes(RawArgument()))}</mtext>", false);
                case "operatorname":
                    {
                        // A name as written (foo-bar too), applied like \sin.
                        var op2 = TextEscapes(RawArgument(), dropSpaces: true);
                        function = true;
                        return ($"<mi mathvariant=\"normal\">{DocumentHtml.Escape(op2)}</mi>", false);
                    }
                case "mathrm" or "mathsf" or "mathtt":
                    {
                        // Read as math (m^2 is m squared), its letters upright; a plain word stays one name (\mathrm{kg}).
                        var start = i;
                        var text = RawArgument();
                        if (text.All(ch => char.IsLetterOrDigit(ch) || ch == ' '))
                            return ($"<mi mathvariant=\"normal\">{DocumentHtml.Escape(new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray()))}</mi>", false);
                        i = start;
                        return (Argument().Replace("<mi>", "<mi mathvariant=\"normal\">"), false);
                    }
                case "mathbf" or "boldsymbol" or "bm" or "mathit" or "mathcal" or "mathscr" or "mathfrak":
                    return (Argument(), false);
                case "mathbb":
                    {
                        var text = new string(RawArgument().Where(ch => !char.IsWhiteSpace(ch)).ToArray());
                        return ($"<mi>{DocumentHtml.Escape(string.Concat(text.Select(DoubleStruck)))}</mi>", false);
                    }
                case "begin":
                    return (Environment(), false);
                case "displaystyle" or "textstyle" or "scriptstyle" or "limits" or "nolimits" or "nonumber" or "notag":
                    return ("", false);
                default:
                    // Not ours: shown as written.
                    return ($"<mtext>\\{DocumentHtml.Escape(name)}</mtext>", false);
            }
        }

        /// <summary>The delimiter after \left, \right or \big: a character or a command (\langle, \{), "." for none.</summary>
        string Delimiter(bool stretchy)
        {
            SkipSpace();
            if (Peek() is not int c) throw new Failure();
            string text;
            if (c == '.')
            {
                i++;
                return "";
            }
            if (c == '\\')
            {
                var name = ReadCommand();
                if (!Fences.TryGetValue(name, out var d) && !Operators.TryGetValue(name, out d)) throw new Failure();
                text = d;
            }
            else
            {
                i++;
                text = Str(c);
            }
            text = DocumentHtml.Escape(text);
            return stretchy ? $"<mo>{text}</mo>" : $"<mo stretchy=\"false\">{text}</mo>";
        }

        /// <summary>\begin{matrix} … \end{matrix}: rows by \\, cells by &amp;, with the environment's brackets.</summary>
        string Environment()
        {
            var name = RawArgument().Trim();
            // An array's column spec isn't drawn.
            if (name == "array") RawArgument();
            var rows = new List<List<string>> { new() };
            while (true)
            {
                var cell = Expression(["&", "\\\\", "\\end"]);
                rows[^1].Add(Mrow(cell));
                if (Peek() is null) throw new Failure();
                if (Peek() == '&')
                {
                    i++;
                    continue;
                }
                var command = ReadCommand();
                if (command == "\\")
                {
                    rows.Add([]);
                    continue;
                }
                if (command != "end") throw new Failure();
                RawArgument();
                break;
            }
            // A last \\ leaves an empty row.
            if (rows.Count > 1 && rows[^1] is [ "<mrow></mrow>" ]) rows.RemoveAt(rows.Count - 1);
            if (rows.Count > 100 || rows.Any(r => r.Count > 50)) throw new Failure();
            var table = "<mtable>" + string.Concat(rows.Select(r => "<mtr>" + string.Concat(r.Select(c => $"<mtd>{c}</mtd>")) + "</mtr>")) + "</mtable>";
            (string Open, string Close)? fences = name switch
            {
                "pmatrix" => ("(", ")"),
                "bmatrix" => ("[", "]"),
                "Bmatrix" => ("{", "}"),
                "vmatrix" => ("|", "|"),
                "Vmatrix" => ("‖", "‖"),
                "cases" => ("{", ""),
                _ => null,
            };
            if (fences is not { } f) return $"<mrow>{table}</mrow>";
            return $"<mrow><mo>{f.Open}</mo>{table}" + (f.Close.Length == 0 ? "" : $"<mo>{f.Close}</mo>") + "</mrow>";
        }
    }

    static string Mrow(List<string> parts) => "<mrow>" + string.Concat(parts) + "</mrow>";

    /// <summary>\text{…}'s escapes as the characters they stand for (5\% is 5%). dropSpaces: plain spaces go (a name), escaped ones stay.</summary>
    static string TextEscapes(string text, bool dropSpaces = false)
    {
        var output = new StringBuilder();
        var escaped = false;
        foreach (var ch in text)
        {
            if (escaped)
            {
                // \\ is a line break in LaTeX: a space on one line.
                if ("%$&#_{} ".Contains(ch)) output.Append(ch);
                else if (ch == '\\') output.Append(' ');
                else output.Append('\\').Append(ch);
                escaped = false;
            }
            else if (ch == '\\')
            {
                escaped = true;
            }
            else if (ch is ' ' or '\t' or '\n')
            {
                if (!dropSpaces) output.Append(ch);
            }
            else
            {
                output.Append(ch == '~' ? ' ' : ch);
            }
        }
        if (escaped) output.Append('\\');
        return output.ToString();
    }

    // Tables

    static readonly Dictionary<string, string> Spaces = new()
    {
        [","] = " ", [":"] = " ", [">"] = " ", [";"] = " ", [" "] = " ", ["!"] = "",
        ["quad"] = " ", ["qquad"] = "  ", ["enspace"] = " ", ["thinspace"] = " ",
    };

    /// <summary>Letters and symbols set as identifiers (italic Greek lower case).</summary>
    static readonly Dictionary<string, string> Identifiers = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ",
        ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν",
        ["xi"] = "ξ", ["omicron"] = "ο", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς",
        ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["ell"] = "ℓ", ["hbar"] = "ℏ", ["imath"] = "ı", ["jmath"] = "ȷ", ["wp"] = "℘",
    };

    /// <summary>Upright identifiers: capital Greek, ∞, ∂ and the like.</summary>
    static readonly Dictionary<string, string> Upright = new()
    {
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ",
        ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇", ["emptyset"] = "∅", ["varnothing"] = "∅", ["aleph"] = "ℵ",
        ["Re"] = "ℜ", ["Im"] = "ℑ", ["degree"] = "°", ["angle"] = "∠", ["triangle"] = "△", ["top"] = "⊤", ["bot"] = "⊥",
        ["$"] = "$", ["%"] = "%", ["#"] = "#", ["&"] = "&", ["_"] = "_",
    };

    static readonly Dictionary<string, string> Operators = new()
    {
        ["cdot"] = "⋅", ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓", ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘",
        ["bullet"] = "∙", ["oplus"] = "⊕", ["ominus"] = "⊖", ["otimes"] = "⊗", ["odot"] = "⊙",
        ["le"] = "≤", ["leq"] = "≤", ["leqslant"] = "⩽", ["ge"] = "≥", ["geq"] = "≥", ["geqslant"] = "⩾", ["ne"] = "≠", ["neq"] = "≠",
        ["lt"] = "<", ["gt"] = ">", ["approx"] = "≈", ["equiv"] = "≡", ["sim"] = "∼", ["simeq"] = "≃", ["cong"] = "≅", ["propto"] = "∝",
        ["ll"] = "≪", ["gg"] = "≫", ["doteq"] = "≐", ["prec"] = "≺", ["succ"] = "≻", ["perp"] = "⊥", ["parallel"] = "∥", ["mid"] = "∣",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["gets"] = "←", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐",
        ["leftrightarrow"] = "↔", ["Leftrightarrow"] = "⇔", ["iff"] = "⟺", ["implies"] = "⟹", ["impliedby"] = "⟸",
        ["mapsto"] = "↦", ["longrightarrow"] = "⟶", ["longleftarrow"] = "⟵", ["Longrightarrow"] = "⟹", ["uparrow"] = "↑",
        ["downarrow"] = "↓", ["nearrow"] = "↗", ["searrow"] = "↘",
        ["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂", ["subseteq"] = "⊆", ["supset"] = "⊃", ["supseteq"] = "⊇",
        ["subsetneq"] = "⊊", ["cup"] = "∪", ["cap"] = "∩", ["setminus"] = "∖", ["wedge"] = "∧", ["land"] = "∧", ["vee"] = "∨",
        ["lor"] = "∨", ["neg"] = "¬", ["lnot"] = "¬", ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄",
        ["cdots"] = "⋯", ["ldots"] = "…", ["dots"] = "…", ["dotsc"] = "…", ["dotsb"] = "⋯", ["vdots"] = "⋮", ["ddots"] = "⋱",
        ["backslash"] = "∖", ["colon"] = ":", ["prime"] = "′",
        ["therefore"] = "∴", ["because"] = "∵", ["models"] = "⊨", ["vdash"] = "⊢",
    };

    /// <summary>Brackets, as written unless \left and \right stretch them.</summary>
    static readonly Dictionary<string, string> Fences = new()
    {
        ["langle"] = "⟨", ["rangle"] = "⟩", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈", ["rceil"] = "⌉",
        ["vert"] = "|", ["lvert"] = "|", ["rvert"] = "|", ["Vert"] = "‖", ["lVert"] = "‖", ["rVert"] = "‖", ["|"] = "‖",
        ["{"] = "{", ["}"] = "}", ["lbrace"] = "{", ["rbrace"] = "}",
    };

    /// <summary>Operators whose scripts are limits (under and over), and integrals, whose scripts stay scripts.</summary>
    static readonly Dictionary<string, string> BigOperators = new()
    {
        ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["bigcup"] = "⋃", ["bigcap"] = "⋂", ["bigoplus"] = "⨁", ["bigotimes"] = "⨂",
        ["bigvee"] = "⋁", ["bigwedge"] = "⋀", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮",
        ["∑"] = "∑", ["∏"] = "∏",
    };

    static readonly HashSet<string> Integrals = ["int", "iint", "iiint", "oint"];

    static readonly HashSet<string> Functions =
    [
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth",
        "log", "ln", "lg", "exp", "det", "dim", "gcd", "deg", "ker", "hom", "arg", "Pr",
        "lim", "liminf", "limsup", "max", "min", "sup", "inf", "argmax", "argmin",
    ];

    static readonly HashSet<string> LimitFunctions = ["lim", "liminf", "limsup", "max", "min", "sup", "inf", "argmax", "argmin", "det", "gcd", "Pr"];

    static readonly Dictionary<string, string> Accents = new()
    {
        ["hat"] = "ˆ", ["widehat"] = "ˆ", ["bar"] = "¯", ["overline"] = "¯", ["vec"] = "→", ["overrightarrow"] = "→",
        ["dot"] = "˙", ["ddot"] = "¨", ["tilde"] = "˜", ["widetilde"] = "˜", ["check"] = "ˇ", ["breve"] = "˘", ["acute"] = "´", ["grave"] = "`",
    };

    /// <summary>ℝ for R and the like.</summary>
    static string DoubleStruck(char c)
    {
        switch (c)
        {
            case 'C': return "ℂ";
            case 'H': return "ℍ";
            case 'N': return "ℕ";
            case 'P': return "ℙ";
            case 'Q': return "ℚ";
            case 'R': return "ℝ";
            case 'Z': return "ℤ";
        }
        return c switch
        {
            >= 'A' and <= 'Z' => char.ConvertFromUtf32(0x1D538 + (c - 'A')),
            >= 'a' and <= 'z' => char.ConvertFromUtf32(0x1D552 + (c - 'a')),
            >= '0' and <= '9' => char.ConvertFromUtf32(0x1D7D8 + (c - '0')),
            _ => c.ToString(),
        };
    }
}

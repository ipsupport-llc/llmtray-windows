namespace LLMTray.Core.Usage;

/// <summary>
/// Who sent a request, in a word, for the usage statistics (adr/0026, Mac
/// UsageClient): LLMTray's own chat, else the client its User-Agent names.
/// </summary>
public static class UsageClient
{
    public const string App = "LLMTray";
    public const string AppBackground = "LLMTray (background)";

    /// <summary>Known clients by a part of their User-Agent, first match wins.</summary>
    static readonly (string Match, string Name)[] Known =
    [
        ("codex", "Codex"),
        ("claude-cli", "Claude Code"),
        ("claude-code", "Claude Code"),
        ("opencode", "OpenCode"),
        ("cline", "Cline"),
        ("kilo-code", "Kilo Code"),
        ("roo-code", "Roo Code"),
        ("continue", "Continue"),
        ("aider", "Aider"),
        ("zed", "Zed"),
        ("cursor", "Cursor"),
        ("jetbrains", "JetBrains"),
        ("xcode", "Xcode"),
        ("openwebui", "Open WebUI"),
        ("open-webui", "Open WebUI"),
        ("librechat", "LibreChat"),
        ("anthropic/python", "Anthropic SDK (Python)"),
        ("anthropic/js", "Anthropic SDK (JS)"),
        ("openai/python", "OpenAI SDK (Python)"),
        ("openai/js", "OpenAI SDK (JS)"),
        ("ipsupport-code", "ipsupport-code"),
        ("curl/", "curl"),
        ("wget/", "wget"),
        ("postman", "Postman"),
        ("insomnia", "Insomnia"),
        ("mozilla/", "Browser"),
    ];

    /// <summary>
    /// An HTTP library's own User-Agent (its first product, exactly): a
    /// program that didn't name itself. The language, for "OpenAI API (Go)".
    /// </summary>
    static readonly Dictionary<string, string> Libraries = new(StringComparer.Ordinal)
    {
        ["go-http-client"] = "Go",
        ["python-requests"] = "Python", ["python-httpx"] = "Python", ["python-urllib"] = "Python", ["aiohttp"] = "Python",
        ["node-fetch"] = "Node", ["undici"] = "Node", ["axios"] = "Node", ["node"] = "Node",
        ["okhttp"] = "Java", ["java"] = "Java", ["java-http-client"] = "Java", ["dart"] = "Dart", ["reqwest"] = "Rust", ["hyper"] = "Rust",
        ["cfnetwork"] = "Swift",
    };

    /// <summary>Which API a route is: what an unnamed client is called by.</summary>
    public static string Api(string route)
    {
        var path = route.Split('?')[0];
        if (path.EndsWith("/messages", StringComparison.Ordinal) || path.EndsWith("/messages/count_tokens", StringComparison.Ordinal)) return "Anthropic API";
        if (path.EndsWith("/responses", StringComparison.Ordinal)) return "Responses API";
        if (path.EndsWith("/completions", StringComparison.Ordinal) || path.EndsWith("/embeddings", StringComparison.Ordinal)) return "OpenAI API";
        return "API";
    }

    static readonly char[] Separators = ['/', ' ', '('];

    /// <summary>
    /// The name for a request: the app's own by whether it's background work;
    /// a known client's name; for a program that didn't name itself (no
    /// User-Agent, or an HTTP library's) the API it calls and its language;
    /// else the agent's first product name ("Foo/1.2 (…)" → "Foo"), ASCII
    /// letters, digits, " ._-" only, at most 24 characters.
    /// </summary>
    public static string Name(string? userAgent, bool fromApp, bool background = false, string route = "")
    {
        if (fromApp) return background ? AppBackground : App;
        var api = Api(route);
        var agent = userAgent?.Trim() ?? "";
        if (agent.Length == 0) return api;
        var lower = agent.ToLowerInvariant();
        foreach (var (match, name) in Known)
        {
            if (StartsWord(lower, match)) return name;
        }
        var first = lower.Split(Separators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (Libraries.TryGetValue(first, out var language)) return $"{api} ({language})";
        var product = agent.Split(Separators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var clean = new string(product.Where(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || " ._-".Contains(c))).Take(24).ToArray());
        return clean.Length == 0 ? api : clean;
    }

    /// <summary><paramref name="word"/> in <paramref name="text"/> where no letter comes right before it ("zed" isn't in "customized").</summary>
    static bool StartsWord(string text, string word)
    {
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            if (i == 0 || !char.IsLetter(text[i - 1])) return true;
        }
        return false;
    }
}

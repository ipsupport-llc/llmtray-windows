using System.Globalization;
using System.Text.Json;

namespace LLMTray.App;

/// <summary>
/// UI text in the user's language (adr/0006), as in the Mac app: the key is
/// the English text, translations live in Strings/&lt;lang&gt;.json beside
/// the app, and a string a language lacks shows in English. Placeholders
/// are .NET's {0}, {1}.
/// </summary>
static class L
{
    static Dictionary<string, string> table = [];

    /// <summary>The languages the app has text for (the Strings folder), English first.</summary>
    public static IReadOnlyList<string> Available { get; private set; } = ["en"];

    /// <summary>The language in use ("en", "de", "pt-BR", "zh-Hans", …).</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>Reads the table for a language; null: Windows' display language.</summary>
    public static void Load(string? language)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Strings");
        try
        {
            Available = Directory.EnumerateFiles(folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .OrderBy(c => c == "en" ? "" : c, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Available = ["en"];
        }
        Current = Pick(language ?? CultureInfo.CurrentUICulture.Name, Available);
        table = [];
        if (Current == "en") return;
        try
        {
            using var stream = File.OpenRead(Path.Combine(folder, Current + ".json"));
            table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            table = [];
        }
    }

    /// <summary>The best of the available languages for a culture name: exact, script (zh-Hans/zh-Hant), then the language alone.</summary>
    public static string Pick(string culture, IReadOnlyCollection<string> available)
    {
        if (available.Contains(culture)) return culture;
        var lower = culture.ToLowerInvariant();
        if (lower.StartsWith("zh", StringComparison.Ordinal))
        {
            // An explicit script wins; the region decides only without one.
            var traditional = lower.Contains("hant") || (!lower.Contains("hans") && (lower.EndsWith("-tw") || lower.EndsWith("-hk") || lower.EndsWith("-mo")));
            var zh = traditional ? "zh-Hant" : "zh-Hans";
            if (available.Contains(zh)) return zh;
        }
        if (lower.StartsWith("pt", StringComparison.Ordinal) && available.Contains("pt-BR")) return "pt-BR";
        var language = culture.Split('-')[0];
        return available.FirstOrDefault(a => string.Equals(a, language, StringComparison.OrdinalIgnoreCase)) ?? "en";
    }

    /// <summary>A text in the current language.</summary>
    public static string T(string english) =>
        table.TryGetValue(english, out var text) && text.Length > 0 ? text : english;

    /// <summary>A text with {0}, {1} … filled in; an unusable translation falls back to English.</summary>
    public static string F(string english, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(english), args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, english, args);
        }
    }

    /// <summary>A language's own name ("Deutsch", "日本語"), for the picker.</summary>
    public static string NativeName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            var name = culture.NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}

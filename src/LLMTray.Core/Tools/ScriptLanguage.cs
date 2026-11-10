namespace LLMTray.Core.Tools;

/// <summary>
/// Which Wikipedias a query is likely written for, from its script: a
/// Cyrillic, Japanese or Arabic title 404s on en.wiki (or finds a thinner
/// article), so its own language is tried first, English last.
/// </summary>
public static class ScriptLanguage
{
    public static IReadOnlyList<string> WikipediaCandidates(string text)
    {
        var langs = new List<string>();
        var scalars = text.EnumerateRunes().Select(r => r.Value).ToList();
        bool Has(int low, int high) => scalars.Any(v => v >= low && v <= high);
        bool HasAny(string chars) => text.IndexOfAny(chars.ToCharArray()) >= 0;

        if (Has(0x0400, 0x04FF))
        {
            if (HasAny("ўЎ")) langs.AddRange(["be", "ru"]);            // Belarusian also has і: ў first
            else if (HasAny("іїєґІЇЄҐ")) langs.AddRange(["uk", "ru"]);
            else if (HasAny("ёыэъЁЫЭЪ")) langs.AddRange(["ru", "uk"]);
            else langs.AddRange(["ru", "uk", "bg", "sr"]);
        }
        if (Has(0x3040, 0x30FF)) langs.Add("ja");                            // kana
        if (Has(0xAC00, 0xD7AF) || Has(0x1100, 0x11FF)) langs.Add("ko");    // hangul
        if (Has(0x4E00, 0x9FFF)) langs.AddRange(["zh", "ja"]);              // han
        if (Has(0x0600, 0x06FF)) langs.Add("ar");
        if (Has(0x0590, 0x05FF)) langs.Add("he");
        if (Has(0x0370, 0x03FF)) langs.Add("el");
        if (Has(0x0900, 0x097F)) langs.Add("hi");
        if (Has(0x0E00, 0x0E7F)) langs.Add("th");
        langs.Add("en");
        return langs.Distinct().ToList();
    }
}

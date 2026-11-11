using System.Globalization;
using System.Text;

namespace LLMTray.Core.ProjectFiles;

/// <summary>
/// The files added to a chat's project since its last message, told to the
/// model with the user's next one (Mac adr/0012, "New files are said"): how
/// many and of which kinds, never their names (a name is file text, read
/// only through project_files); the names are for the user's caption.
/// </summary>
public static class ProjectFilesNote
{
    public const int ShownNames = 10;
    const int NameChars = 80;

    /// <summary>The names in <paramref name="current"/> that <paramref name="seen"/> didn't have, in order (a name twice counts twice).</summary>
    public static List<string> Added(IReadOnlyList<string> current, IReadOnlyList<string> seen)
    {
        var left = seen.GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var name in current)
        {
            if (left.TryGetValue(name, out var n) && n > 0) left[name] = n - 1;
            else added.Add(name);
        }
        return added;
    }

    /// <summary>The line put before the user's message for the model; null for none.</summary>
    public static string? Text(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return null;
        var kinds = new List<string>();
        foreach (var name in names)
        {
            var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            var kind = ext.Length is > 0 and <= 5 && ext.All(char.IsAsciiLetterOrDigit) ? ext : "file";
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }
        var count = names.Count == 1 ? "1 new file" : $"{names.Count} new files";
        return $"[App note, not the user's words: {count} ({string.Join(", ", kinds.Take(6))}) added to this chat's project "
            + "since the last message. If the message is about them: project_files with no arguments lists the files with their ids, "
            + "then project_files(doc: <id>) reads one and project_files(query: \"...\") searches them.]";
    }

    /// <summary>A name for the caption: one line, no control or format characters (bidi overrides), at most 80.</summary>
    public static string Clean(string name)
    {
        var sb = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse) continue;
            sb.Append(rune.ToString());
        }
        var s = sb.ToString();
        return s.Length > NameChars ? s[..(NameChars - 1)] + "…" : s;
    }
}

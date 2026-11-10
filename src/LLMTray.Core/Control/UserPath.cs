namespace LLMTray.Core.Control;

/// <summary>
/// This user's PATH as written in the registry (adr/0013): one folder added
/// or taken out, everything else left exactly as it was, <c>%VAR%</c>
/// references included.
/// </summary>
public static class UserPath
{
    static string Plain(string entry) => entry.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    public static bool Contains(string raw, string folder) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(p => string.Equals(Plain(p), Plain(folder), StringComparison.OrdinalIgnoreCase));

    /// <summary>The PATH with <paramref name="folder"/> on it (at the end) or off it; null when nothing changes.</summary>
    public static string? Edit(string raw, string folder, bool on)
    {
        if (folder.Contains(';')) throw new ArgumentException("a folder with ; in its name can't go on PATH", nameof(folder));
        if (Contains(raw, folder) == on) return null;
        if (on) return raw.Length == 0 || raw.EndsWith(';') ? raw + folder : raw + ";" + folder;
        // Only the matching entries go; everything else, empty entries too, stays as written.
        return string.Join(";", raw.Split(';').Where(p => p.Trim().Length == 0 || !string.Equals(Plain(p), Plain(folder), StringComparison.OrdinalIgnoreCase)));
    }
}

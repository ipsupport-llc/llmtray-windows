using System.Globalization;
using System.Text;

namespace LLMTray.Core.Documents;

/// <summary>
/// The PDF's file name from the document's title (Mac adr/0020, Hardening 4):
/// one sanitizer, never a path. Windows adds its own rules to the Mac's:
/// the characters it can't hold in a name, no trailing dot or space, and
/// not a device name (CON, NUL, COM1…).
/// </summary>
public static class DocumentFileName
{
    const int MaxScalars = 80;

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>The name without its extension: NFC, separators, control and Windows-invalid characters removed, at most 80 characters, "Document" when nothing is left.</summary>
    public static string Sanitize(string title)
    {
        var s = title.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            var v = r.Value;
            if (v < 0x20 || v == 0x7F || (v >= 0x80 && v <= 0x9F) || v is 0x2028 or 0x2029 or 0x85) continue;
            if (v is '/' or ':' or '\\' or '<' or '>' or '"' or '|' or '?' or '*') continue;
            sb.Append(r.ToString());
        }
        s = sb.ToString().Trim();
        s = s.TrimStart('.').Trim();
        if (DocumentSpec.Scalars(s) > MaxScalars) s = DocumentSpec.Clip(s, MaxScalars);
        // Windows drops a trailing dot or space from a name: never ends with one.
        s = s.TrimEnd('.', ' ').Trim();
        if (s.Length == 0) return "Document";
        // CON, NUL.txt, COM1 … are devices, with or without an extension.
        var stem = s.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem)) s = "_" + s;
        return s;
    }

    /// <summary>The n-th name to try: "Title.pdf", "Title 2.pdf", "Title 3.pdf"…</summary>
    public static string Candidate(string baseName, int attempt) =>
        attempt <= 1 ? $"{baseName}.pdf" : $"{baseName} {attempt.ToString(CultureInfo.InvariantCulture)}.pdf";
}

namespace LLMTray.Core.Folders;

/// <summary>
/// Where the folder tools may look (adr/0016, Mac adr/0014): paths as the
/// model writes them, made whole; what is never granted or never shown;
/// files whose contents are never read.
/// </summary>
/// <param name="home">The user's profile folder (<c>~</c>): never a grant itself.</param>
/// <param name="known">Folders by their usual names (Downloads, Documents, Desktop…), for paths written from them.</param>
/// <param name="deniedTrees">Folders never granted and never entered, nor anything in them (Windows, Program Files, AppData, the app's own data).</param>
/// <param name="comparison">How names compare (Windows: ignoring case).</param>
public sealed class FolderPlaces(string home, IReadOnlyDictionary<string, string> known, IReadOnlyList<string> deniedTrees,
    StringComparison comparison = StringComparison.OrdinalIgnoreCase)
{
    public string Home { get; } = Trim(Path.GetFullPath(home));
    public StringComparison Comparison { get; } = comparison;
    public IReadOnlyDictionary<string, string> Known { get; } = known;
    readonly IReadOnlyList<string> denied = deniedTrees.Select(d => Trim(Path.GetFullPath(d))).ToList();

    /// <summary>Folders and files never shown inside any grant: keys, credentials, other apps' data.</summary>
    static readonly string[] HiddenNames =
    [
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".config", ".password-store", ".terraform.d", ".gem", ".cargo",
        "AppData", "$Recycle.Bin", "System Volume Information", ".git-credentials", ".netrc", "NTUSER.DAT", "ntuser.dat.LOG1", "ntuser.dat.LOG2",
    ];

    /// <summary>A path without a trailing separator (but a drive root's).</summary>
    public static string Trim(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }

    public bool Same(string a, string b) => string.Equals(Trim(a), Trim(b), Comparison);

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public bool Inside(string path, string root)
    {
        path = Trim(path);
        root = Trim(root);
        if (string.Equals(path, root, Comparison)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }

    public bool IsHiddenName(string name) => HiddenNames.Any(h => string.Equals(h, name, Comparison));

    /// <summary>
    /// Never looked at: inside a denied tree, or with a hidden name on the way
    /// (<c>.ssh</c>, <c>AppData</c>) below the user's profile or a drive.
    /// </summary>
    public bool IsDenied(string full)
    {
        full = Trim(full);
        if (denied.Any(d => Inside(full, d))) return true;
        var root = Path.GetPathRoot(full) ?? "";
        var rest = full[root.Length..];
        return rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(IsHiddenName);
    }

    /// <summary>
    /// May be granted: not denied, and not a drive's root, the profile folder
    /// itself or the folder of all profiles (too much at once; a folder in them is fine).
    /// </summary>
    public bool CanGrant(string full)
    {
        full = Trim(full);
        if (IsDenied(full)) return false;
        if (string.Equals(full, Trim(Path.GetPathRoot(full) ?? ""), Comparison)) return false;
        if (Same(full, Home)) return false;
        if (Path.GetDirectoryName(Home) is { } profiles && Same(full, profiles)) return false;
        return true;
    }

    /// <summary>
    /// A file whose contents are never read (keys, secrets): listed, and may
    /// be moved, but no head, no hash, no duplicate check (Mac Hardening 17).
    /// </summary>
    public static bool IsSecretName(string name, string? parentName = null)
    {
        var n = name.ToLowerInvariant();
        if (n is ".env" or ".envrc" or ".npmrc" or ".pypirc" or ".netrc" or ".git-credentials" or "credentials") return true;
        if (n.StartsWith(".env.", StringComparison.Ordinal)) return true;
        if (n.StartsWith("id_rsa", StringComparison.Ordinal) || n.StartsWith("id_dsa", StringComparison.Ordinal)
            || n.StartsWith("id_ecdsa", StringComparison.Ordinal) || n.StartsWith("id_ed25519", StringComparison.Ordinal)) return true;
        var ext = Path.GetExtension(n);
        if (ext is ".pem" or ".key" or ".p12" or ".pfx" or ".kdbx" or ".ppk" or ".jks" or ".keystore") return true;
        return n == "config" && string.Equals(parentName, ".git", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A path as the model wrote it, made whole: <c>~</c>, <c>%USERPROFILE%</c>,
    /// a known folder's name ("Downloads\x"), a granted folder's name, or a
    /// full path. Null when it can't be read.
    /// </summary>
    public string? Resolve(string written, IEnumerable<string> grantRoots)
    {
        var p = written.Trim().Trim('"', '\'', '`').Trim();
        if (p.Length == 0) return null;
        p = p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var sep = Path.DirectorySeparatorChar.ToString();
        if (p == "~" || p.StartsWith("~" + sep, StringComparison.Ordinal)) p = Home + p[1..];
        else if (p.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase)) p = Home + p["%USERPROFILE%".Length..];
        else if (p.StartsWith("%HOMEPATH%", StringComparison.OrdinalIgnoreCase)) p = Home + p["%HOMEPATH%".Length..];
        if (!Path.IsPathFullyQualified(p))
        {
            var first = p.Split(Path.DirectorySeparatorChar, 2);
            var restOf = first.Length > 1 ? first[1] : "";
            var roots = grantRoots.ToList();
            // A granted folder named so first (the chat's own folders), then a known folder.
            var granted = roots.Where(r => string.Equals(Path.GetFileName(Trim(r)), first[0], Comparison)).ToList();
            if (granted.Count == 1) p = Path.Combine(granted[0], restOf);
            else if (Known.FirstOrDefault(k => string.Equals(k.Key, first[0], Comparison)) is { Value: { } folder }) p = Path.Combine(folder, restOf);
            else if (roots.Count == 1) p = Path.Combine(roots[0], p);
            else return null;
        }
        try
        {
            return Trim(Path.GetFullPath(p));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>A path as shown to the model and the user: under the profile written with <c>~</c>.</summary>
    public string Show(string full)
    {
        if (Inside(full, Home) && !Same(full, Home)) return "~" + full[Trim(Home).Length..];
        return full;
    }
}

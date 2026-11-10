namespace LLMTray.Core.Folders;

/// <summary>
/// Where the folder tools may look (adr/0016, Mac adr/0014): paths as the
/// model writes them, made whole; what is never granted or never shown;
/// files whose contents are never read.
/// </summary>
/// <param name="home">The user's profile folder (<c>~</c>): never a grant itself.</param>
/// <param name="known">Folders by their usual names (Downloads, Documents, Desktop…), for paths written from them.</param>
/// <param name="deniedTrees">Folders never granted and never entered, nor anything in them (Windows, Program Files, AppData, the app's own data).</param>
/// <param name="comparison">
/// How paths compare once made real (<see cref="Real"/>): exactly, since the
/// real names are the folders' own; a case-sensitive folder (WSL) can hold
/// "Public" and "public" side by side.
/// </param>
public sealed class FolderPlaces
{
    public FolderPlaces(string home, IReadOnlyDictionary<string, string> known, IReadOnlyList<string> deniedTrees,
        StringComparison comparison = StringComparison.Ordinal)
    {
        Comparison = comparison;
        Home = Real(Trim(Path.GetFullPath(home))) ?? Trim(Path.GetFullPath(home));
        Known = known.ToDictionary(k => k.Key, k => Real(Trim(Path.GetFullPath(k.Value))) ?? k.Value, StringComparer.OrdinalIgnoreCase);
        // Each as written and as it really is (a junction to it counts too).
        denied = deniedTrees.Where(d => d.Length > 0).Select(d => Trim(Path.GetFullPath(d)))
            .SelectMany(d => Real(d) is { } r && r != d ? new[] { d, r } : [d]).ToList();
    }

    public string Home { get; }
    public StringComparison Comparison { get; }
    public IReadOnlyDictionary<string, string> Known { get; }
    readonly IReadOnlyList<string> denied;

    /// <summary>Folders and files never shown inside any grant: keys, credentials, other apps' data.</summary>
    static readonly string[] HiddenNames =
    [
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".config", ".password-store", ".terraform.d", ".gem", ".cargo", ".vault-token",
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

    public bool IsHiddenName(string name) => HiddenNames.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

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
        if (n is ".npmrc" or ".pypirc" or ".netrc" or ".git-credentials" or ".htpasswd" or ".pgpass") return true;
        if (n.StartsWith(".env", StringComparison.Ordinal) || n.EndsWith(".env", StringComparison.Ordinal)) return true;
        foreach (var word in new[] { "credential", "password", "passwd", "secret", "token", "apikey", "api_key", "api-key", "private_key", "privatekey" })
        {
            if (n.Contains(word, StringComparison.Ordinal)) return true;
        }
        if (n.StartsWith("id_rsa", StringComparison.Ordinal) || n.StartsWith("id_dsa", StringComparison.Ordinal)
            || n.StartsWith("id_ecdsa", StringComparison.Ordinal) || n.StartsWith("id_ed25519", StringComparison.Ordinal)) return true;
        var ext = Path.GetExtension(n);
        if (ext is ".pem" or ".key" or ".p12" or ".pfx" or ".kdbx" or ".ppk" or ".jks" or ".keystore" or ".ovpn" or ".asc" or ".gpg") return true;
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
        // Device and raw forms (\\?\, \\.\), network paths, streams (a:b) and wildcards aren't paths the tools take.
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return null;
        if (p.IndexOfAny(['*', '?', '<', '>', '|', '\0']) >= 0) return null;
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
            var granted = roots.Where(r => string.Equals(Path.GetFileName(Trim(r)), first[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (granted.Count == 1) p = Path.Combine(granted[0], restOf);
            else if (Known.TryGetValue(first[0], out var folder)) p = Path.Combine(folder, restOf);
            else if (roots.Count == 1) p = Path.Combine(roots[0], p);
            else return null;
        }
        string full;
        try
        {
            full = Trim(Path.GetFullPath(p));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return Plain(full) ? Real(full) : null;
    }

    static readonly string[] Devices = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Every name is an ordinary one: no stream (<c>a.txt:s</c>), no device
    /// (<c>NUL</c>, <c>CON.txt</c>), no trailing dot or space (Windows would
    /// open another name than the one checked).
    /// </summary>
    public static bool Plain(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        if (root.Length == 0) return false;
        foreach (var name in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (name.Contains(':') || name.EndsWith('.') || name.EndsWith(' ')) return false;
            var stem = name.Split('.')[0].TrimEnd(' ');
            if (Devices.Any(d => string.Equals(d, stem, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    static readonly EnumerationOptions Lookup = new()
    {
        IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32, MatchCasing = MatchCasing.PlatformDefault,
    };

    /// <summary>
    /// The path as it really is: each existing name as the folder holds it
    /// (a short 8.3 alias becomes its long name, the case its own), and each
    /// link on the way (a junction, a symbolic link) replaced by where it
    /// leads; the part that doesn't exist yet as written. Null when a link
    /// can't be followed or loops.
    /// </summary>
    public string? Real(string full, int depth = 0)
    {
        if (depth > 8) return null;
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return null;
        // The drive's letter as Windows writes it.
        var current = root.Length >= 2 && root[1] == ':' ? char.ToUpperInvariant(root[0]) + root[1..] : root;
        var names = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < names.Length; i++)
        {
            FileSystemInfo? found = null;
            try
            {
                var matches = new DirectoryInfo(current).EnumerateFileSystemInfos(names[i], Lookup).Take(4).ToList();
                found = matches.FirstOrDefault(m => string.Equals(m.Name, names[i], StringComparison.Ordinal))
                    ?? (matches.Count == 1 ? matches[0] : matches.FirstOrDefault(m => string.Equals(m.Name, names[i], StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
            if (found is null)
            {
                // Not there (yet): the rest as written.
                return Trim(Path.Combine([current, .. names[i..]]));
            }
            if (found.Attributes.HasFlag(FileAttributes.ReparsePoint) && i < names.Length - 1)
            {
                // A link on the way: where it leads, then the rest.
                FileSystemInfo? target;
                try
                {
                    target = found.ResolveLinkTarget(returnFinalTarget: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
                if (target is null) return null;
                return Real(Trim(Path.Combine([Trim(Path.GetFullPath(target.FullName)), .. names[(i + 1)..]])), depth + 1);
            }
            current = Path.Combine(current, found.Name);
        }
        return Trim(current);
    }

    /// <summary>A path as shown to the model and the user: under the profile written with <c>~</c>.</summary>
    public string Show(string full)
    {
        if (Inside(full, Home) && !Same(full, Home)) return "~" + full[Trim(Home).Length..];
        return full;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMTray.Core;

/// <summary>
/// A release tag, <c>vX.Y.Z</c> (stable) or <c>vX.Y.Z-beta.N</c> (beta), and
/// the four-part MSIX package version it maps to (adr/0002).
/// </summary>
public sealed partial record ReleaseTag(int Major, int Minor, int Patch, int? Beta)
{
    /// <summary>Highest patch that fits: patch × 1000 + 999 must stay ≤ 65535.</summary>
    public const int MaxPatch = 64;

    /// <summary>Highest beta number; 999 marks the stable build of a patch.</summary>
    public const int MaxBeta = 998;

    const int StableSlot = 999;

    public bool IsPrerelease => Beta is not null;

    /// <summary>
    /// <c>Major.Minor.(Patch × 1000 + Beta).0</c>, with 999 in place of the
    /// beta number for stable. Every beta of a version sorts below its stable
    /// build, and the stable build below the next patch's first beta. The
    /// last part stays 0 because the Store requires it.
    /// </summary>
    public string PackageVersion =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch * 1000 + (Beta ?? StableSlot)}.0");

    /// <summary>The version shown to people: the tag without the leading v.</summary>
    public string DisplayVersion =>
        Beta is int n
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-beta.{n}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The tag a package version came from (the inverse of <see cref="PackageVersion"/>).</summary>
    public static ReleaseTag? FromPackageVersion(Version version)
    {
        if (version.Major < 0 || version.Minor < 0 || version.Build < 0) return null;
        var patch = version.Build / 1000;
        var slot = version.Build % 1000;
        if (patch > MaxPatch || slot == 0) return null;
        return new ReleaseTag(version.Major, version.Minor, patch, slot == StableSlot ? null : slot);
    }

    /// <summary>Release order (the package version's order).</summary>
    public Version SortKey => Version.Parse(PackageVersion);

    public static ReleaseTag Parse(string tag) =>
        TryParse(tag, out var result, out var error)
            ? result
            : throw new FormatException(error);

    public static bool TryParse(string tag, out ReleaseTag result, out string error)
    {
        result = null!;
        var m = TagPattern().Match(tag);
        if (!m.Success)
        {
            error = $"'{tag}' is not a release tag (vX.Y.Z or vX.Y.Z-beta.N)";
            return false;
        }
        if (!TryPart(m.Groups["major"].Value, 65535, out var major) ||
            !TryPart(m.Groups["minor"].Value, 65535, out var minor) ||
            !TryPart(m.Groups["patch"].Value, MaxPatch, out var patch))
        {
            error = $"'{tag}': major and minor must be ≤ 65535 and patch ≤ {MaxPatch}";
            return false;
        }
        int? beta = null;
        if (m.Groups["beta"].Success)
        {
            if (!TryPart(m.Groups["beta"].Value, MaxBeta, out var n) || n < 1)
            {
                error = $"'{tag}': the beta number must be 1–{MaxBeta}";
                return false;
            }
            beta = n;
        }
        result = new ReleaseTag(major, minor, patch, beta);
        error = "";
        return true;
    }

    static bool TryPart(string text, int max, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= max;

    [GeneratedRegex(@"\Av(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-beta\.(?<beta>[1-9][0-9]*))?\z")]
    private static partial Regex TagPattern();
}

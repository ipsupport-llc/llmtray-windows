namespace LLMTray.Core.Settings;

/// <summary>
/// Where a window was and how big, in the screen's pixels, to open it there
/// again (the Mac's windows keep their frame). Kept per window in the settings.
/// </summary>
public sealed record WindowPlace(int X, int Y, int Width, int Height, bool Maximized)
{
    /// <summary>A screen's work area, in the same pixels.</summary>
    public readonly record struct Area(int X, int Y, int Width, int Height);

    /// <summary>
    /// The place to open at, on the screens there are now: the saved one when
    /// enough of its title bar is on one of them (a screen unplugged since
    /// leaves it nowhere), cut to that screen if it grew too big; null otherwise.
    /// </summary>
    public WindowPlace? On(IReadOnlyList<Area> areas, int minWidth, int minHeight)
    {
        if (Width < minWidth || Height < minHeight) return null;
        // The screen holding most of it, among those where its title bar is: at least 120 pixels of its top 40.
        var best = areas
            .Select(a => (Area: a, Visible: Math.Min(X + Width, a.X + a.Width) - Math.Max(X, a.X), Top: Y >= a.Y - 8 && Y + 40 <= a.Y + a.Height,
                Overlap: (long)Math.Max(0, Math.Min(X + Width, a.X + a.Width) - Math.Max(X, a.X)) * Math.Max(0, Math.Min(Y + Height, a.Y + a.Height) - Math.Max(Y, a.Y))))
            .Where(c => c.Visible >= 120 && c.Top)
            .OrderByDescending(c => c.Overlap)
            .Select(c => (Area?)c.Area)
            .FirstOrDefault();
        if (best is not { } a) return null;
        var width = Math.Min(Width, a.Width);
        var height = Math.Min(Height, a.Height);
        var x = Math.Clamp(X, a.X - 8, a.X + a.Width - width + 8);
        var y = Math.Clamp(Y, a.Y, a.Y + a.Height - height);
        return this with { X = x, Y = y, Width = width, Height = height };
    }
}

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
        foreach (var a in areas)
        {
            // The top 40 pixels, where the title bar is, at least 120 of them on this screen.
            var visible = Math.Min(X + Width, a.X + a.Width) - Math.Max(X, a.X);
            var top = Y >= a.Y - 8 && Y + 40 <= a.Y + a.Height;
            if (visible < 120 || !top) continue;
            var width = Math.Min(Width, a.Width);
            var height = Math.Min(Height, a.Height);
            var x = Math.Clamp(X, a.X - 8, a.X + a.Width - width + 8);
            var y = Math.Clamp(Y, a.Y, a.Y + a.Height - height);
            return this with { X = x, Y = y, Width = width, Height = height };
        }
        return null;
    }
}

using LLMTray.Core.Downloads;
using LLMTray.Core.Settings;

namespace LLMTray.Core.Setup;

/// <summary>
/// The first-run wizard's logic (Mac adr/0013, adr/0010 here): when it
/// opens, and which chat models to offer on this PC.
/// </summary>
public static class SetupWizard
{
    /// <summary>The wizard's version: a bigger one could open it again for a new step (not done yet).</summary>
    public const int Version = 1;

    /// <summary>A fresh install: never finished or closed, and no model loaded yet.</summary>
    public static bool ShouldOpen(AppSettings settings) => settings.SetupCompleted is null && settings.LastModel is null;

    /// <summary>
    /// The PC's tier by its memory, as on the Mac (its RAM there; here the
    /// larger of system memory and the GPU's own): under 6 GB → 4, under
    /// 12 → 8, under 24 → 16, else 32.
    /// </summary>
    public static int MemoryTier(long memoryBytes)
    {
        var gb = memoryBytes / (double)(1L << 30);
        return gb < 6 ? 4 : gb < 12 ? 8 : gb < 24 ? 16 : 32;
    }

    public enum Fit { Recommended, Lighter, Bigger }

    public sealed record Pick(FeaturedModel Model, Fit Fit);

    /// <summary>
    /// The ladder (Mac ModelRecommendations.picks): every featured model the
    /// PC has the memory for, on the chosen source, the tier's pick first,
    /// then from the biggest down, each labelled against the pick (lighter:
    /// faster, simpler answers; bigger: better answers, slower).
    /// </summary>
    public static IReadOnlyList<Pick> Picks(IEnumerable<FeaturedModel> models, long memoryBytes, DownloadSource source)
    {
        var tier = MemoryTier(memoryBytes);
        var gb = memoryBytes / (double)(1L << 30);
        var fitting = models.Where(m => m.IsOn(source) && m.MinMemoryGB <= gb + 0.5).ToList();
        if (fitting.Count == 0) return [];
        var recommended = fitting.FirstOrDefault(m => m.RecommendedFor?.Contains(tier) == true)
            // No pick for this tier on this source: the biggest that fits.
            ?? fitting.OrderByDescending(m => m.TotalSize).First();
        var rest = fitting.Where(m => m != recommended).OrderByDescending(m => m.TotalSize)
            .Select(m => new Pick(m, m.TotalSize > recommended.TotalSize ? Fit.Bigger : Fit.Lighter));
        return [new Pick(recommended, Fit.Recommended), .. rest];
    }

    /// <summary>A featured model already in the models folder, file and projector complete.</summary>
    public static bool IsDownloaded(FeaturedModel model, string root)
    {
        try
        {
            foreach (var f in model.Projector is { } p ? new[] { model.File, p } : [model.File])
            {
                var path = FeaturedModel.Inside(root, Path.Combine(model.Folder, f.Name));
                if (!File.Exists(path) || new FileInfo(path).Length != f.Size) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DownloadException or ArgumentException)
        {
            return false;
        }
    }
}

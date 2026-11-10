using LLMTray.Core.Downloads;

namespace LLMTray.Core.Control;

/// <summary>
/// What <c>llmtray pull org/repo</c> downloads from a repository of GGUF
/// files: one model file, the first quantization found in a preference
/// order that keeps quality and size in balance, plus the smallest vision
/// projector if there is one.
/// </summary>
public static class PullPlan
{
    static readonly string[] Preference = ["Q4_K_M", "Q4_K_S", "Q4_0", "IQ4_XS", "Q5_K_M", "Q5_K_S", "Q6_K", "Q8_0", "Q3_K_M", "BF16", "F16"];

    /// <summary>The files, or why none: a split model is left to the window, where its parts are picked together.</summary>
    public static (IReadOnlyList<RemoteFile>? Files, string? Problem) Choose(IReadOnlyList<RemoteFile> files, string? wanted = null)
    {
        var models = files.Where(f => f.IsGguf && !f.IsProjector).ToList();
        if (models.Count == 0) return (null, "the repository has no GGUF model");
        var whole = models.Where(f => !System.Text.RegularExpressions.Regex.IsMatch(f.Name, @"-\d{5}-of-\d{5}\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        RemoteFile? pick;
        if (wanted is { Length: > 0 })
        {
            pick = models.FirstOrDefault(f => f.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (pick is null) return (null, $"no file with {wanted} in its name");
        }
        else
        {
            pick = Preference.Select(q => whole.FirstOrDefault(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f is not null)
                ?? whole.OrderBy(f => f.Size).FirstOrDefault();
        }
        if (pick is null) return (null, "only split models here: choose one in the Get models window");
        if (System.Text.RegularExpressions.Regex.IsMatch(pick.Name, @"-\d{5}-of-\d{5}\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return (null, "a split model: choose it in the Get models window");
        }
        var projector = files.Where(f => f.IsGguf && f.IsProjector).OrderBy(f => f.Size).FirstOrDefault();
        return (projector is null ? [pick] : [pick, projector], null);
    }
}

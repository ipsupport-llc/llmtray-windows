using System.Text;
using LLMTray.Core.Models;

namespace LLMTray.Core.Licenses;

/// <summary>
/// A downloaded model's license, as the Mac app shows it: the publisher's,
/// from the GGUF's own metadata (general.license) or the model card
/// (README.md front matter) beside it.
/// </summary>
public static class ModelLicenses
{
    public static LicenseEntry Of(ModelEntry model)
    {
        var meta = GgufMetadata.ReadStrings(model.FullPath, ["general.license", "general.license.name", "general.license.link", "general.name", "general.organization"]);
        var folder = Path.GetDirectoryName(model.FullPath) ?? "";
        var readme = Path.Combine(folder, "README.md");
        string? card = null;
        try
        {
            if (File.Exists(readme)) card = File.ReadAllText(readme);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable card: the GGUF's own metadata still counts.
        }
        var license = meta.GetValueOrDefault("general.license.name") ?? meta.GetValueOrDefault("general.license")
            ?? (card is null ? null : FromCard(card)) ?? "not stated";
        var link = meta.GetValueOrDefault("general.license.link");
        var text = new StringBuilder($"License: {license}\n");
        if (link is not null) text.Append($"License text: {link}\n");
        if (meta.GetValueOrDefault("general.name") is { } name) text.Append($"Model: {name}\n");
        if (meta.GetValueOrDefault("general.organization") is { } org) text.Append($"From: {org}\n");
        text.Append("\nA model's license is set by its publisher: read it on the model card before using the model, especially commercially.\n");
        if (card is not null) text.Append($"\nModel card: {readme}\n");
        text.Append($"File: {model.FullPath}\n");
        return new LicenseEntry(model.Folder.Length > 0 ? model.Folder : model.Name, null, license, link, text.ToString());
    }

    /// <summary>
    /// The license in a Hugging Face card's YAML front matter:
    /// "license: apache-2.0", or a list ("license:" then "- mit" lines).
    /// </summary>
    public static string? FromCard(string card)
    {
        if (!card.StartsWith("---", StringComparison.Ordinal)) return null;
        var end = card.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var lines = card[..end].Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("license:", StringComparison.Ordinal)) continue;
            var value = lines[i]["license:".Length..].Trim().Trim('"', '\'');
            if (value.StartsWith('[')) return string.Join(", ", value.Trim('[', ']').Split(',').Select(v => v.Trim().Trim('"', '\'')));
            if (value.Length > 0) return value;
            var items = lines.Skip(i + 1).TakeWhile(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                .Select(l => l.TrimStart()[2..].Trim().Trim('"', '\'')).ToList();
            return items.Count > 0 ? string.Join(", ", items) : null;
        }
        return null;
    }
}

using System.Text;
using System.Text.RegularExpressions;
using LLMTray.Core.Models;

namespace LLMTray.Core.Licenses;

/// <summary>
/// A downloaded model's license, as the Mac app shows it: the publisher's,
/// from the GGUF's own metadata (general.license) or the model card
/// (README.md front matter) beside it.
/// </summary>
public static partial class ModelLicenses
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
        catch (IOException)
        {
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

    /// <summary>"license: apache-2.0" in a Hugging Face card's YAML front matter.</summary>
    public static string? FromCard(string card)
    {
        if (!card.StartsWith("---", StringComparison.Ordinal)) return null;
        var end = card.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var m = CardLicense().Match(card[..end]);
        return m.Success ? m.Groups["license"].Value.Trim().Trim('"', '\'') : null;
    }

    [GeneratedRegex(@"^license:\s*(?<license>\S.*)$", RegexOptions.Multiline)]
    private static partial Regex CardLicense();
}

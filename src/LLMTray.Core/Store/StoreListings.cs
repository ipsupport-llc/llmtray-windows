using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Store;

/// <summary>One language's Store listing text, as kept in <c>store/listings/&lt;language&gt;.json</c> (adr/0025).</summary>
public sealed record StoreText(
    string Title,
    string ShortDescription,
    string Description,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> SearchTerms,
    string Copyright);

/// <summary>
/// The Store listing texts and screenshots a release submits, and how they go
/// into a submission of the Microsoft Store submission API: the package
/// replaced, each language's text set, the screenshots replaced. What the
/// files don't hold (website, privacy policy, pricing, the other languages)
/// stays as the last published submission had it.
/// </summary>
public static class StoreListings
{
    /// <summary>The Store's limits on a listing's fields.</summary>
    public const int MaxScreenshots = 10, MaxTitle = 256, MaxShortDescription = 1000, MaxDescription = 10_000, MaxFeatures = 20, MaxFeature = 200, MaxSearchTerms = 7, MaxSearchTerm = 30;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Every <c>&lt;language&gt;.json</c> in a folder, checked against the Store's limits.</summary>
    public static SortedDictionary<string, StoreText> Load(string folder)
    {
        var all = new SortedDictionary<string, StoreText>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var language = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var text = JsonSerializer.Deserialize<StoreText>(File.ReadAllText(file), Json) ?? throw new InvalidDataException($"{file}: empty");
            Check(language, text);
            all[language] = text;
        }
        if (all.Count == 0) throw new InvalidDataException($"no listings in {folder}");
        return all;
    }

    /// <summary>Throws when a field is missing or over the Store's limit, naming the language and the field.</summary>
    public static void Check(string language, StoreText text)
    {
        void Need(bool ok, string what)
        {
            if (!ok) throw new InvalidDataException($"{language}: {what}");
        }
        Need(!string.IsNullOrWhiteSpace(text.Title) && text.Title.Length <= MaxTitle, "title missing or too long");
        Need(text.ShortDescription is { Length: <= MaxShortDescription }, "short_description too long");
        Need(!string.IsNullOrWhiteSpace(text.Description) && text.Description.Length <= MaxDescription, "description missing or too long");
        Need(text.Features is { Count: <= MaxFeatures } && text.Features.All(f => !string.IsNullOrWhiteSpace(f) && f.Length <= MaxFeature), $"features: at most {MaxFeatures}, each 1-{MaxFeature} characters");
        Need(text.SearchTerms is { Count: <= MaxSearchTerms } && text.SearchTerms.All(t => !string.IsNullOrWhiteSpace(t) && t.Length <= MaxSearchTerm), $"search_terms: at most {MaxSearchTerms}, each 1-{MaxSearchTerm} characters");
        Need(text.Copyright is not null, "copyright missing");
    }

    /// <summary>The screenshots in the order they're shown: the chat first, then Settings' tabs as listed, then the rest by name.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> names)
    {
        string[] first = ["chat.png", "settings-models.png", "settings-server.png", "settings-tools.png", "settings-memory.png"];
        var list = names.Select(Path.GetFileName).OfType<string>().Where(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> ordered = [.. first.Where(list.Contains), .. list.Where(n => !first.Contains(n)).Order(StringComparer.Ordinal)];
        return ordered.Count <= MaxScreenshots ? ordered : throw new InvalidDataException($"{ordered.Count} screenshots: the Store takes at most {MaxScreenshots}");
    }

    /// <summary>
    /// A new submission (the API's copy of the last published one), changed in
    /// place: <paramref name="package"/> (a file name in the upload) replaces
    /// the packages, each language in <paramref name="texts"/> gets its text,
    /// and, when <paramref name="screenshots"/> (file names in the upload) has
    /// any, they replace every language's screenshots. A language the
    /// submission doesn't have yet is added only with screenshots (a listing
    /// without any isn't complete); returns the languages left out.
    /// </summary>
    public static IReadOnlyList<string> Apply(JsonObject submission, string package, IReadOnlyDictionary<string, StoreText> texts, IReadOnlyList<string> screenshots)
    {
        var skipped = new List<string>();
        var packages = submission["applicationPackages"] as JsonArray ?? [];
        foreach (var old in packages.OfType<JsonObject>()) old["fileStatus"] = "PendingDelete";
        packages.Add(new JsonObject
        {
            ["fileName"] = package,
            ["fileStatus"] = "PendingUpload",
            ["minimumDirectXVersion"] = "None",
            ["minimumSystemRam"] = "None",
        });
        submission["applicationPackages"] = packages;

        var listings = submission["listings"] as JsonObject ?? [];
        submission["listings"] = listings;
        foreach (var (language, text) in texts)
        {
            if (listings[language] is not JsonObject listing)
            {
                if (screenshots.Count == 0)
                {
                    skipped.Add(language);
                    continue;
                }
                listing = new JsonObject { ["platformOverrides"] = new JsonObject() };
                listings[language] = listing;
            }
            if (listing["baseListing"] is not JsonObject base_)
            {
                base_ = [];
                listing["baseListing"] = base_;
            }
            base_["title"] = text.Title;
            base_["shortDescription"] = text.ShortDescription;
            base_["description"] = text.Description;
            base_["features"] = new JsonArray([.. text.Features.Select(f => (JsonNode?)f)]);
            base_["keywords"] = new JsonArray([.. text.SearchTerms.Select(t => (JsonNode?)t)]);
            base_["copyrightAndTrademarkInfo"] = text.Copyright;
        }
        if (screenshots.Count == 0) return skipped;
        foreach (var listing in listings.Select(l => l.Value).OfType<JsonObject>())
        {
            if (listing["baseListing"] is not JsonObject base_) continue;
            var images = base_["images"] as JsonArray ?? [];
            foreach (var image in images.OfType<JsonObject>())
            {
                if ((string?)image["imageType"] == "Screenshot") image["fileStatus"] = "PendingDelete";
            }
            foreach (var shot in screenshots)
            {
                images.Add(new JsonObject
                {
                    ["fileName"] = shot,
                    ["fileStatus"] = "PendingUpload",
                    ["imageType"] = "Screenshot",
                    ["description"] = "",
                });
            }
            base_["images"] = images;
        }
        return skipped;
    }
}

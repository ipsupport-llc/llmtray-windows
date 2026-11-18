using System.Text.Json.Nodes;
using LLMTray.Core.Store;

namespace LLMTray.Core.Tests;

public sealed class StoreListingsTests
{
    static StoreText Text(string title = "LLMTray") => new(title, "Short.", "Long text.", ["One", "Two"], ["local AI", "LLM"], "© 2026 IPSupport LLC");

    /// <summary>The listings committed in the repository are what a release submits: they must load and fit the Store.</summary>
    [Fact]
    public void TheRepositoryListingsLoad()
    {
        var folder = Path.Combine(Repo(), "store", "listings");
        var all = StoreListings.Load(folder);
        Assert.Contains("en-us", all.Keys);
        Assert.Contains("ru-ru", all.Keys);
        Assert.Equal(19, all.Count);
        Assert.All(all.Values, t => Assert.Equal("LLMTray", t.Title));
    }

    [Fact]
    public void LimitsAreChecked()
    {
        Assert.Throws<InvalidDataException>(() => StoreListings.Check("en-us", Text("")));
        Assert.Throws<InvalidDataException>(() => StoreListings.Check("en-us", Text() with { Description = new string('x', 10_001) }));
        Assert.Throws<InvalidDataException>(() => StoreListings.Check("en-us", Text() with { Features = [new string('x', 201)] }));
        Assert.Throws<InvalidDataException>(() => StoreListings.Check("en-us", Text() with { SearchTerms = ["1", "2", "3", "4", "5", "6", "7", "8"] }));
        Assert.Throws<InvalidDataException>(() => StoreListings.Check("en-us", Text() with { SearchTerms = [new string('x', 31)] }));
        StoreListings.Check("en-us", Text());
    }

    [Fact]
    public void ScreenshotsKeepTheirOrder()
    {
        Assert.Equal(
            ["chat.png", "settings-models.png", "settings-server.png", "a-extra.png"],
            StoreListings.Order(["x/settings-server.png", "x/a-extra.png", "x/chat.png", "x/notes.txt", "x/settings-models.png"]));
    }

    [Fact]
    public void ApplyReplacesThePackageTextsAndScreenshots()
    {
        var submission = JsonNode.Parse("""
        {
          "applicationPackages": [{ "fileName": "old.msixupload", "fileStatus": "Uploaded", "id": "1" }],
          "listings": {
            "en-us": { "baseListing": {
              "title": "Old", "privacyPolicy": "https://example.com/privacy", "websiteUrl": "https://example.com",
              "images": [
                { "fileName": "old.png", "fileStatus": "Uploaded", "imageType": "Screenshot", "id": "7" },
                { "fileName": "logo.png", "fileStatus": "Uploaded", "imageType": "StoreLogo300x300", "id": "8" }
              ] }, "platformOverrides": {} },
            "fr-fr": { "baseListing": { "title": "Ancien" }, "platformOverrides": {} }
          }
        }
        """)!.AsObject();
        StoreListings.Apply(submission, "LLMTray_0.1.7000.0.msixupload", new Dictionary<string, StoreText> { ["en-us"] = Text(), ["ru-ru"] = Text() with { Description = "Текст." } }, ["screenshots/chat.png"]);

        var packages = submission["applicationPackages"]!.AsArray();
        Assert.Equal("PendingDelete", (string?)packages[0]!["fileStatus"]);
        Assert.Equal("LLMTray_0.1.7000.0.msixupload", (string?)packages[1]!["fileName"]);
        Assert.Equal("PendingUpload", (string?)packages[1]!["fileStatus"]);

        var en = submission["listings"]!["en-us"]!["baseListing"]!;
        Assert.Equal("LLMTray", (string?)en["title"]);
        Assert.Equal(["local AI", "LLM"], en["keywords"]!.AsArray().Select(k => (string?)k));
        Assert.Equal(["One", "Two"], en["features"]!.AsArray().Select(k => (string?)k));
        // What the files don't hold stays.
        Assert.Equal("https://example.com/privacy", (string?)en["privacyPolicy"]);
        var images = en["images"]!.AsArray();
        Assert.Equal("PendingDelete", (string?)images[0]!["fileStatus"]);
        Assert.Equal("Uploaded", (string?)images[1]!["fileStatus"]);
        Assert.Equal("screenshots/chat.png", (string?)images[2]!["fileName"]);
        Assert.Equal("PendingUpload", (string?)images[2]!["fileStatus"]);

        // A new language is added; one the files don't have keeps its text but gets the new screenshots too.
        Assert.Equal("Текст.", (string?)submission["listings"]!["ru-ru"]!["baseListing"]!["description"]);
        var fr = submission["listings"]!["fr-fr"]!["baseListing"]!;
        Assert.Equal("Ancien", (string?)fr["title"]);
        Assert.Equal("screenshots/chat.png", (string?)fr["images"]![0]!["fileName"]);
    }

    [Fact]
    public void WithoutScreenshotsThePublishedOnesStay()
    {
        var submission = JsonNode.Parse("""{ "listings": { "en-us": { "baseListing": { "images": [ { "fileName": "a.png", "fileStatus": "Uploaded", "imageType": "Screenshot" } ] } } } }""")!.AsObject();
        StoreListings.Apply(submission, "p.msixupload", new Dictionary<string, StoreText> { ["en-us"] = Text() }, []);
        var images = submission["listings"]!["en-us"]!["baseListing"]!["images"]!.AsArray();
        Assert.Single(images);
        Assert.Equal("Uploaded", (string?)images[0]!["fileStatus"]);
    }

    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LLMTray.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("the repository wasn't found above the tests");
    }
}

using System.Text.Json.Nodes;
using LLMTray.Core.Chat;

namespace LLMTray.Core.Tests;

public sealed class LinkNoteTests
{
    static JsonObject Tool(params string[] properties) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "web_search",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create(p, (JsonNode?)new JsonObject { ["type"] = "string" }))),
            },
        },
    };

    [Fact]
    public void LinksAreWrittenWithTheirScheme()
    {
        Assert.Equal(["https://chatgpt.com/s/t_6afb"], LinkNote.Links("https://chatgpt.com/s/t_6afb о чем это"));
        // Bare names aren't links; trailing punctuation isn't part of one; each once.
        Assert.Equal(["https://a.org/x", "http://b.io/?q=1"], LinkNote.Links("see example.com and https://a.org/x, (https://a.org/x). http://b.io/?q=1"));
        Assert.Empty(LinkNote.Links("Please fix src/main.rs; let value = user.id"));
        Assert.Empty(LinkNote.Links("mail me at a@b.com, ftp://x.org/f"));
        // Local addresses aren't read anyway.
        Assert.Empty(LinkNote.Links("curl http://127.0.0.1:8765/v1/models and http://localhost:3000 and http://[::1]:80/"));
        Assert.Empty(LinkNote.Links("http://localhost./ http://a.localhost/ http://printer.local/ http://192.168.0.1/ http://[fe80::1]/ http://[::ffff:127.0.0.1]/ http://0.0.0.0/ https://user:pass@example.org/"));
        // A name that starts like one is still a name; brackets the link opened stay; another case is the same link.
        Assert.Equal(["https://127.example.org/"], LinkNote.Links("https://127.example.org/"));
        Assert.Equal(["https://[2606:4700:4700::1111]", "https://en.wikipedia.org/wiki/Foo_(bar)"],
            LinkNote.Links("https://[2606:4700:4700::1111] and (see https://en.wikipedia.org/wiki/Foo_(bar))."));
        Assert.Single(LinkNote.Links("HTTPS://EXAMPLE.ORG/A and https://example.org/A"));
        Assert.Empty(LinkNote.Links("no links here"));
    }

    [Fact]
    public void TheNoteCountsTheLinksOnlyWithThePageTool()
    {
        Assert.Equal("[App note, not the user's words: the message has a link. To read it, call web_search with source=page and url set to the link.]",
            LinkNote.Text("https://chatgpt.com/s/t_6afb разберем", PageReading.SourceAndUrl));
        Assert.Equal("[App note, not the user's words: the message has 2 links. To read one, call web_search with source=page and url set to that link.]",
            LinkNote.Text("compare https://a.org/x and https://b.org/y", PageReading.SourceAndUrl));
        // Only url declared: the note doesn't name a source.
        Assert.Equal("[App note, not the user's words: the message has a link. To read it, call web_search with url set to the link.]",
            LinkNote.Text("https://a.org/x", PageReading.Url));
        Assert.Null(LinkNote.Text("https://chatgpt.com/s/t_6afb", PageReading.None));
        Assert.Null(LinkNote.Text("привет", PageReading.SourceAndUrl));
    }

    [Fact]
    public void ThePageToolIsWebSearchWithAUrl()
    {
        Assert.Equal(PageReading.SourceAndUrl, LinkNote.Declared([Tool("query"), Tool("query", "source", "url")]));
        Assert.Equal(PageReading.Url, LinkNote.Declared([Tool("url", "page")]));
        Assert.Equal(PageReading.None, LinkNote.Declared([Tool("query")]));
        Assert.Equal(PageReading.None, LinkNote.Declared([]));
        Assert.Equal(PageReading.None, LinkNote.Declared(null));
    }
}

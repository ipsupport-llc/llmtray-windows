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
        Assert.Empty(LinkNote.Links("no links here"));
    }

    [Fact]
    public void TheNoteCountsTheLinksOnlyWithThePageTool()
    {
        Assert.Equal("[App note, not the user's words: the message has a link. To read it, call web_search with source=page and url set to the link.]",
            LinkNote.Text("https://chatgpt.com/s/t_6afb разберем", canReadPages: true));
        Assert.Equal("[App note, not the user's words: the message has 2 links. To read one, call web_search with source=page and url set to that link.]",
            LinkNote.Text("compare https://a.org/x and https://b.org/y", canReadPages: true));
        Assert.Null(LinkNote.Text("https://chatgpt.com/s/t_6afb", canReadPages: false));
        Assert.Null(LinkNote.Text("привет", canReadPages: true));
    }

    [Fact]
    public void ThePageToolIsWebSearchWithAUrl()
    {
        Assert.True(LinkNote.DeclaresPageReading([Tool("query"), Tool("query", "url")]));
        Assert.False(LinkNote.DeclaresPageReading([Tool("query")]));
        Assert.False(LinkNote.DeclaresPageReading([]));
        Assert.False(LinkNote.DeclaresPageReading(null));
    }
}

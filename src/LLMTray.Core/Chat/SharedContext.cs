using System.Text;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Chat;

/// <summary>
/// A window or the screen the user showed the model with a message (Mac
/// adr/0030, the Mac's <c>shared</c>): what it was, and the text recognized
/// in it for a model that doesn't see. Saved with the message.
/// </summary>
public sealed record SharedContext
{
    [JsonConverter(typeof(JsonStringEnumConverter<SharedKind>))]
    public SharedKind Kind { get; init; }
    [JsonConverter(typeof(UpperGuidConverter))]
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>The window's title.</summary>
    public string Title { get; init; } = "";
    /// <summary>The app, for a window; the address, for a browser tab.</summary>
    public string Source { get; init; } = "";
    /// <summary>The text recognized in it; null: none.</summary>
    public string? Text { get; init; }
    public bool Truncated { get; init; }
    public bool TitleOnly { get; init; }
    /// <summary>The picture goes with the message: its text is for a model that doesn't see.</summary>
    public bool HasPicture { get; init; }

    /// <summary>Fields a newer Mac wrote: kept when this app saves the chat.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Other { get; set; }

    /// <summary>The most text one shared item adds to a message.</summary>
    public const int MaxText = 6000;

    public static SharedContext Make(SharedKind kind, string title, string source, string? text, bool hasPicture)
    {
        // A page's text keeps its layout (code, a config in a <pre>); recognized text is mostly layout whitespace.
        var clean = text is null ? null : kind == SharedKind.BrowserTab ? Tidy(text) : Clean(text);
        var truncated = clean is { Length: > MaxText };
        if (truncated && clean is { } whole) clean = whole[..(char.IsHighSurrogate(whole[MaxText - 1]) ? MaxText - 1 : MaxText)];
        return new SharedContext
        {
            Kind = kind, Title = title.Trim(), Source = source.Trim(), Text = clean is { Length: > 0 } ? clean : null,
            Truncated = truncated, HasPicture = hasPicture,
        };
    }

    /// <summary>A page's text as it reads: line ends made one kind, trailing spaces and runs of blank lines gone, indentation kept.</summary>
    public static string Tidy(string text)
    {
        var lines = new List<string>();
        var blank = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n', '\r'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                blank = lines.Count > 0;
                continue;
            }
            if (blank) lines.Add("");
            blank = false;
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    /// <summary>Runs of spaces and of blank lines collapsed, lines trimmed: recognized text is mostly layout whitespace.</summary>
    public static string Clean(string text)
    {
        var lines = new List<string>();
        var blank = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n', '\r'))
        {
            var line = string.Join(' ', raw.Split([' ', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0)
            {
                blank = lines.Count > 0;
                continue;
            }
            if (blank) lines.Add("");
            blank = false;
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    /// <summary>The line naming it, for the model and the chip.</summary>
    public string Heading => Kind switch
    {
        SharedKind.Window => string.Join(" — ", new[] { Source, Title }.Where(s => s.Length > 0)) is { Length: > 0 } name ? "Window: " + name : "A window on the user's screen",
        SharedKind.Screen => "The user's screen",
        _ => "Browser tab: " + (Title.Length == 0 ? Source : $"{Title} ({Source})"),
    };

    /// <summary>
    /// The block the model gets before the message's text, marked as what the
    /// user showed, so its words don't read as the user's own. The text goes
    /// only to a model that doesn't see the picture.
    /// </summary>
    public string NoteForModel(bool seesPicture)
    {
        var sb = new StringBuilder("[The user is showing you this. ").Append(Heading).Append("]\n");
        if (Text is { } text && !(HasPicture && seesPicture))
        {
            sb.Append(Kind == SharedKind.BrowserTab ? "The page's text:" : "Text recognized in it (icons, colors and states of controls aren't in it):").Append('\n');
            sb.Append(text).Append('\n');
            if (Truncated) sb.Append("(cut here: the rest isn't shown)\n");
        }
        else if (!HasPicture || !seesPicture)
        {
            sb.Append("(No text could be read from it.)\n");
        }
        return sb.Append("[End of what the user is showing]").ToString();
    }
}

/// <summary>The Mac's raw values: window, screen, browserTab.</summary>
public enum SharedKind
{
    [JsonStringEnumMemberName("window")] Window,
    [JsonStringEnumMemberName("screen")] Screen,
    [JsonStringEnumMemberName("browserTab")] BrowserTab,
}

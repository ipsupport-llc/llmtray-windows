namespace LLMTray.Core.Control;

/// <summary>
/// What <c>llmtray</c> was asked (Mac CommandLineArguments): the command,
/// its words and its options. Pure, so it's tested in Core; the CLI links
/// this file as source.
/// </summary>
public sealed record CliArguments(string Command, IReadOnlyList<string> Words, IReadOnlyDictionary<string, string?> Options)
{
    public const string Usage = """
        llmtray: LLMTray from the command line.

          llmtray status [--json]          what's running, and the API address
          llmtray models [--json]          the models in the models folder
          llmtray start [model]            load a model (the last one if none is named)
          llmtray stop                     unload the model
          llmtray pull <model>             download one of LLMTray's models, or org/repo from the chosen source
          llmtray api                      the API address, for OPENAI_BASE_URL
          llmtray chat "prompt" [--model M] [--system S] [--show-thinking] [--json]
                                           ask a model; "-" or a pipe reads the prompt from stdin
          llmtray --version
          llmtray help

        The app is started in the background when it isn't running.
        """;

    static readonly HashSet<string> Flags = ["--json", "--show-thinking", "--version", "-h", "--help"];
    static readonly HashSet<string> Valued = ["--model", "-m", "--system", "-s"];
    static readonly HashSet<string> Commands = ["status", "models", "start", "stop", "pull", "api", "chat", "help", "version"];

    public bool Has(string option) => Options.ContainsKey(option);
    public string? Option(string name, string? alias = null) =>
        Options.TryGetValue(name, out var v) ? v : alias is not null && Options.TryGetValue(alias, out var a) ? a : null;

    /// <summary>The arguments read; a FormatException says what's wrong, for the usage line.</summary>
    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var words = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                words.AddRange(args.Skip(i + 1));
                break;
            }
            if (Flags.Contains(a))
            {
                options[a] = null;
                continue;
            }
            if (Valued.Contains(a))
            {
                // An option after it isn't its value (--model --json).
                if (i + 1 >= args.Count || Flags.Contains(args[i + 1]) || Valued.Contains(args[i + 1])) throw new FormatException($"{a} needs a value");
                options[a] = args[++i];
                continue;
            }
            if (a.StartsWith('-') && a.Contains('='))
            {
                var eq = a.IndexOf('=');
                var name = a[..eq];
                if (!Valued.Contains(name)) throw new FormatException($"unknown option {name}");
                options[name] = a[(eq + 1)..];
                continue;
            }
            if (a.Length > 1 && a.StartsWith('-') && a != "-") throw new FormatException($"unknown option {a}");
            words.Add(a);
        }
        if (options.ContainsKey("--version")) return new CliArguments("version", words, options);
        if (options.ContainsKey("-h") || options.ContainsKey("--help") || words.Count == 0) return new CliArguments("help", words, options);
        var command = words[0].ToLowerInvariant();
        if (!Commands.Contains(command)) throw new FormatException($"unknown command {words[0]}");
        var rest = words.Skip(1).ToList();
        switch (command)
        {
            case "pull" when rest.Count != 1:
                throw new FormatException("pull needs one model");
            case "start" when rest.Count > 1:
                throw new FormatException("start takes at most one model");
            case "status" or "models" or "stop" or "api" when rest.Count > 0:
                throw new FormatException($"{command} takes no words");
        }
        return new CliArguments(command, rest, options);
    }
}

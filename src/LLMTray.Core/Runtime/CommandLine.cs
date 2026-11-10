using System.Text;

namespace LLMTray.Core.Runtime;

/// <summary>
/// Joins arguments into one Windows command line that
/// <c>CommandLineToArgvW</c> and the C runtime split back into the same
/// arguments.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Splits what a user typed into arguments: spaces separate them, double
    /// quotes keep spaces inside one (<c>--alias "my model"</c>).
    /// </summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return args;
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    public static string Join(string executable, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        Append(sb, executable);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            Append(sb, arg);
        }
        return sb.ToString();
    }

    public static void Append(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                // Backslashes before a quote are doubled, and the quote escaped.
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }
            backslashes = 0;
            sb.Append(c);
        }
        // Backslashes before the closing quote are doubled.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}

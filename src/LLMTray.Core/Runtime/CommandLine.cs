using System.Text;

namespace LLMTray.Core.Runtime;

/// <summary>
/// Joins arguments into one Windows command line that
/// <c>CommandLineToArgvW</c> and the C runtime split back into the same
/// arguments.
/// </summary>
public static class CommandLine
{
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

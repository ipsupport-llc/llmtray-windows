using System.Globalization;
using LLMTray.Core.Models;

namespace LLMTray.Core.Runtime;

/// <summary>Options the user sets for llama-server: the model's profile (adr/0008).</summary>
/// <param name="ContextSize">0 lets llama-server pick: the model's own, cut to fit memory (<c>--fit</c>).</param>
/// <param name="KvCacheType">f16, q8_0 or q4_0 for the K and V caches.</param>
/// <param name="Parallel">Requests answered at once; 0: llama-server's own choice.</param>
/// <param name="ExtraArguments">More flags as the user wrote them, last.</param>
public sealed record ServerOptions(int ContextSize = 0, string KvCacheType = "f16", int Parallel = 0, string ExtraArguments = "");

/// <summary>The llama-server command line for one model (adr/0001).</summary>
public static class LlamaServerLaunch
{
    /// <summary>The environment variable llama-server reads its API key from.</summary>
    public const string ApiKeyVariable = "LLAMA_API_KEY";

    public static LaunchPlan Build(string serverExecutable, ModelEntry model, int port, string apiKey, string? logFile, ServerOptions options)
    {
        var args = new List<string>
        {
            "--model", model.FullPath,
            "--alias", model.Name,
            // Loopback only, on a port chosen at launch; the proxy is the way in.
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            // The proxy serves the API; llama-server's own web page isn't offered.
            "--no-webui",
        };
        if (model.ProjectorPath is not null)
        {
            args.AddRange(["--mmproj", model.ProjectorPath]);
        }
        if (options.ContextSize > 0)
        {
            args.AddRange(["--ctx-size", options.ContextSize.ToString(CultureInfo.InvariantCulture)]);
        }
        if (options.KvCacheType is "q8_0" or "q4_0")
        {
            args.AddRange(["--cache-type-k", options.KvCacheType, "--cache-type-v", options.KvCacheType]);
        }
        if (options.Parallel > 0)
        {
            args.AddRange(["--parallel", options.Parallel.ToString(CultureInfo.InvariantCulture)]);
        }
        if (logFile is not null)
        {
            args.AddRange(["--log-file", logFile]);
        }
        // Last, as written: a conflicting flag there is the user's call.
        args.AddRange(CommandLine.Split(options.ExtraArguments));
        // The key goes in the environment, not on the command line, where
        // any local process could read it.
        var env = new Dictionary<string, string> { [ApiKeyVariable] = apiKey };
        return new LaunchPlan(serverExecutable, args, env, logFile);
    }
}

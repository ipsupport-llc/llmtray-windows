namespace LLMTray.Core.Runtime;

/// <summary>One llama.cpp build the app carries: a backend and its server.</summary>
public sealed record LlamaBackend(string Name, string ServerPath);

/// <summary>
/// The llama.cpp backends in the app's <c>runtime/llama</c> folder, in the
/// order they're tried: Vulkan (GPU), then CPU (adr/0001).
/// </summary>
public static class LlamaRuntime
{
    public static readonly string[] Order = ["vulkan", "cpu"];

    public static IReadOnlyList<LlamaBackend> Find(string runtimeRoot, string executableName = "llama-server.exe") =>
        Order.Select(name => new LlamaBackend(name, Path.Combine(runtimeRoot, name, executableName)))
            .Where(b => File.Exists(b.ServerPath))
            .ToList();
}

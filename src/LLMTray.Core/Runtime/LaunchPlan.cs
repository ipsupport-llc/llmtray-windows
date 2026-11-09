namespace LLMTray.Core.Runtime;

/// <summary>A process to start: what, with which arguments and extra environment.</summary>
public sealed record LaunchPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? LogFile);

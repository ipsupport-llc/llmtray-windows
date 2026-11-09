using System.Diagnostics;

namespace LLMTray.Core.Runtime;

/// <summary>A started child process.</summary>
public interface IChildProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    Task<int> Exited { get; }

    /// <summary>Starts ending the process; <see cref="Exited"/> completes when it's gone.</summary>
    void Kill();
}

/// <summary>Starts child processes. The Windows app's launcher ties them to the app's lifetime.</summary>
public interface IProcessLauncher
{
    IChildProcess Start(LaunchPlan plan);
}

/// <summary>
/// <see cref="Process"/> based: for tests and other platforms. The Windows
/// app uses its Job Object launcher instead.
/// </summary>
public sealed class DefaultProcessLauncher : IProcessLauncher
{
    public IChildProcess Start(LaunchPlan plan)
    {
        var info = new ProcessStartInfo(plan.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(plan.Executable) ?? "",
        };
        foreach (var arg in plan.Arguments) info.ArgumentList.Add(arg);
        foreach (var (key, value) in plan.Environment) info.Environment[key] = value;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);
        process.Start();
        if (process.HasExited) exited.TrySetResult(process.ExitCode);
        return new Child(process, exited.Task);
    }

    sealed class Child(Process process, Task<int> exited) : IChildProcess
    {
        public int Id => process.Id;
        public Task<int> Exited => exited;

        public void Kill()
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        public void Dispose() => process.Dispose();
    }
}

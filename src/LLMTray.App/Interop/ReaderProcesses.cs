using System.Diagnostics;
using LLMTray.Core.ProjectFiles;

namespace LLMTray.App.Interop;

/// <summary>
/// Project files are read by the app's own exe in <c>--extract</c> mode
/// (adr/0021): a separate process, confined to a job (2 GB, no children,
/// killed with the app), that only starts parsing once it's in the job
/// (it waits for "go").
/// </summary>
static class ReaderProcesses
{
    public const long MemoryLimit = 2L << 30;

    public static ReaderProcess Start(string path, DocumentFormat format)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("the app's path is unknown");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        info.ArgumentList.Add(ExtractProtocol.ArgumentName);
        info.ArgumentList.Add(path);
        info.ArgumentList.Add(format.ToString());
        var process = Process.Start(info) ?? throw new InvalidOperationException("the reader didn't start");
        IDisposable job;
        try
        {
            job = JobObjectLauncher.Confine(process, MemoryLimit);
        }
        catch
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
        process.EnableRaisingEvents = true;
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(SafeExitCode(process));
        if (process.HasExited) exited.TrySetResult(SafeExitCode(process));
        return new ReaderProcess(process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            () =>
            {
                try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            },
            exited.Task, new Both(job, process));
    }

    static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    sealed class Both(IDisposable job, Process process) : IDisposable
    {
        public void Dispose()
        {
            // Closing the job kills the reader if it's still there.
            job.Dispose();
            process.Dispose();
        }
    }
}

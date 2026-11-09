using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LLMTray.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace LLMTray.App.Interop;

/// <summary>
/// Starts child processes inside a Job Object that kills them when the app
/// exits, however it exits (adr/0001). Each child is created suspended,
/// assigned to the job, then resumed, so it can't run outside the job.
/// </summary>
sealed class JobObjectLauncher : IProcessLauncher, IDisposable
{
    readonly SafeFileHandle job;

    public JobObjectLauncher()
    {
        job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new Native.JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
        {
            throw new Win32Exception();
        }
    }

    public IChildProcess Start(LaunchPlan plan)
    {
        var commandLine = new StringBuilder(CommandLine.Join(plan.Executable, plan.Arguments));
        // Copied whole, embedded NULs included: the block is NUL-separated.
        var environment = Marshal.StringToHGlobalUni(EnvironmentBlock(plan.Environment));
        var startup = new Native.STARTUPINFOW { cb = Marshal.SizeOf<Native.STARTUPINFOW>() };
        const uint flags = Native.CREATE_SUSPENDED | Native.CREATE_NO_WINDOW | Native.CREATE_UNICODE_ENVIRONMENT;
        Native.PROCESS_INFORMATION info;
        try
        {
            if (!Native.CreateProcessW(plan.Executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    environment, Path.GetDirectoryName(plan.Executable), ref startup, out info))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
        using var thread = new SafeFileHandle(info.hThread, ownsHandle: true);
        var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
        if (!Native.AssignProcessToJobObject(job, process))
        {
            var error = new Win32Exception();
            Native.TerminateProcess(process, 1);
            process.Dispose();
            throw error;
        }
        Native.ResumeThread(thread);
        return new Child(process, info.dwProcessId);
    }

    /// <summary>The app's environment plus the plan's, sorted as CreateProcess wants it.</summary>
    static string EnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            all[(string)e.Key] = (string?)e.Value ?? "";
        }
        foreach (var (key, value) in extra) all[key] = value;
        var sb = new StringBuilder();
        foreach (var (key, value) in all) sb.Append(key).Append('=').Append(value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    public void Dispose() => job.Dispose();

    sealed class Child : IChildProcess
    {
        readonly SafeProcessHandle handle;
        readonly ManualResetEvent waitable;
        readonly RegisteredWaitHandle registration;
        readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Child(SafeProcessHandle handle, int id)
        {
            this.handle = handle;
            Id = id;
            waitable = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false) };
            registration = ThreadPool.RegisterWaitForSingleObject(waitable, (_, _) =>
            {
                Native.GetExitCodeProcess(handle, out var code);
                exited.TrySetResult(unchecked((int)code));
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public int Id { get; }
        public Task<int> Exited => exited.Task;

        public void Kill()
        {
            if (!exited.Task.IsCompleted) Native.TerminateProcess(handle, 1);
        }

        public void Dispose()
        {
            registration.Unregister(null);
            waitable.Dispose();
            handle.Dispose();
        }
    }

    static class Native
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint CREATE_SUSPENDED = 0x4;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
        public const uint CREATE_NO_WINDOW = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFOW
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(SafeFileHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    }
}

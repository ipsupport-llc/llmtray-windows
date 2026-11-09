using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Runtime;

/// <summary>A GPU a llama.cpp backend sees.</summary>
public sealed record GpuDevice(string Id, string Description, long TotalMiB, long FreeMiB);

/// <summary>
/// Asks a llama.cpp build which GPUs it can use (<c>llama-server
/// --list-devices</c>). A Vulkan build on a PC without a usable GPU lists
/// none: then the CPU build is the better choice.
/// </summary>
public static partial class LlamaDevices
{
    /// <summary>The devices in <c>--list-devices</c> output; CPUs aren't listed there.</summary>
    public static IReadOnlyList<GpuDevice> Parse(string output)
    {
        var devices = new List<GpuDevice>();
        foreach (Match m in DeviceLine().Matches(output))
        {
            devices.Add(new GpuDevice(
                m.Groups["id"].Value,
                m.Groups["description"].Value,
                long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups["free"].Value, CultureInfo.InvariantCulture)));
        }
        return devices;
    }

    /// <summary>The backend's GPUs, or null when the probe itself failed (unknown).</summary>
    public static async Task<IReadOnlyList<GpuDevice>?> ProbeAsync(LlamaBackend backend)
    {
        try
        {
            var info = new ProcessStartInfo(backend.ServerPath, "--list-devices")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(backend.ServerPath) ?? "",
            };
            using var process = Process.Start(info);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return output.Contains("Available devices:", StringComparison.Ordinal) ? Parse(output) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s+(?<id>[^\s:]+): (?<description>.+) \((?<total>\d+) MiB, (?<free>\d+) MiB free\)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();
}

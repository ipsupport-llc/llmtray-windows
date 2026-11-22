namespace LLMTray.Core.Runtime;

/// <summary>
/// The DLLs a Windows program needs, read from its import tables, and which
/// of them a folder lacks. llama-server is built against the Visual C++
/// runtime, which isn't part of Windows: without it beside the server (or
/// installed), Windows refuses to start it (exit code 0xC0000135) before it
/// writes a line. CI checks the packaged runtime with this; the app names
/// the missing DLL when it happens anyway.
/// </summary>
public static class PeImports
{
    /// <summary>STATUS_DLL_NOT_FOUND, as a process's exit code.</summary>
    public const int DllNotFound = unchecked((int)0xC0000135);

    /// <summary>
    /// DLLs every Windows 10 and 11 has, and the Vulkan loader, which comes
    /// with the graphics driver (without it the Vulkan build fails and the
    /// CPU build is next). The API sets (api-ms-win-…, ext-ms-…) are Windows' too.
    /// </summary>
    static readonly HashSet<string> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32.dll", "kernelbase.dll", "ntdll.dll", "user32.dll", "gdi32.dll", "advapi32.dll", "shell32.dll", "shlwapi.dll",
        "ole32.dll", "oleaut32.dll", "ws2_32.dll", "crypt32.dll", "psapi.dll", "bcrypt.dll", "dbghelp.dll", "version.dll",
        "setupapi.dll", "cfgmgr32.dll", "dxgi.dll", "d3d12.dll", "winmm.dll", "comctl32.dll", "iphlpapi.dll", "secur32.dll",
        "userenv.dll", "wintrust.dll", "imm32.dll", "rpcrt4.dll", "ucrtbase.dll", "powrprof.dll", "mswsock.dll", "dnsapi.dll",
        "ncrypt.dll", "normaliz.dll", "wldap32.dll",
    };

    /// <summary>The Vulkan loader: installed with a graphics driver, not with Windows. Without it the Vulkan build can't start and the CPU build is next.</summary>
    public const string VulkanLoader = "vulkan-1.dll";

    /// <summary>The DLLs a program or DLL imports, delay-loaded ones too, as named in the file.</summary>
    /// <exception cref="InvalidDataException">Not a PE file, or one this can't read.</exception>
    public static IReadOnlyList<string> Of(string path) => Of(File.ReadAllBytes(path));

    public static IReadOnlyList<string> Of(byte[] b)
    {
        try
        {
            if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') throw new InvalidDataException("not a PE file");
            var pe = I32(b, 0x3c);
            if (I32(b, pe) != 0x00004550) throw new InvalidDataException("no PE signature");
            var sections = U16(b, pe + 6);
            var optionalSize = U16(b, pe + 20);
            var optional = pe + 24;
            // PE32+ (64-bit) or PE32; the data directories follow the fields up to NumberOfRvaAndSizes.
            var (countAt, directories) = U16(b, optional) switch
            {
                0x20b => (optional + 108, optional + 112),
                0x10b => (optional + 92, optional + 96),
                var magic => throw new InvalidDataException($"unknown optional header magic {magic:x}"),
            };
            var count = I32(b, countAt);
            if (directories + count * 8 > optional + optionalSize) throw new InvalidDataException("the data directories run past the optional header");
            var table = new List<(int Va, int Size, int Raw, int RawSize)>();
            for (var i = 0; i < sections; i++)
            {
                var s = optional + optionalSize + i * 40;
                table.Add((I32(b, s + 12), Math.Max(I32(b, s + 8), I32(b, s + 16)), I32(b, s + 20), I32(b, s + 16)));
            }
            // A file offset for an RVA, inside the bytes its section has in the file.
            int Offset(int rva, int length)
            {
                foreach (var (va, size, raw, rawSize) in table)
                {
                    if (rva < va || rva >= va + size) continue;
                    var at = rva - va;
                    if (at + length > rawSize || raw + at + length > b.Length) throw new InvalidDataException($"RVA {rva:x} runs past its section");
                    return raw + at;
                }
                throw new InvalidDataException($"RVA {rva:x} is in no section");
            }
            string Name(int rva)
            {
                var start = Offset(rva, 1);
                var end = Array.IndexOf(b, (byte)0, start);
                if (end < 0 || end - start > 260) throw new InvalidDataException("a DLL name without its end");
                return System.Text.Encoding.ASCII.GetString(b, start, end - start);
            }
            var names = new List<string>();
            // The import directory (2nd): 20-byte descriptors, the name's RVA at +12, ended by a zero one.
            if (count > 1 && I32(b, directories + 8) is var imports and > 0)
            {
                for (var o = Offset(imports, 20); I32(b, o + 12) is var name and > 0; o = Offset(imports += 20, 20)) names.Add(Name(name));
            }
            // The delay-load directory (14th): 32-byte descriptors, the name's RVA at +4.
            if (count > 13 && I32(b, directories + 13 * 8) is var delayed and > 0)
            {
                for (var o = Offset(delayed, 32); I32(b, o + 4) is var name and > 0; o = Offset(delayed += 32, 32)) names.Add(Name(name));
            }
            return names;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            throw new InvalidDataException("a damaged PE file", e);
        }
    }

    /// <summary>
    /// The DLLs that llama-server.exe and the DLLs beside it import but
    /// that are neither in <paramref name="folder"/> nor part of Windows: what
    /// stops it from starting on a PC that has nothing else installed.
    /// </summary>
    /// <param name="systemFolder">Also counted as there when found in this folder (the PC's System32); null for a clean Windows.</param>
    /// <param name="vulkanIsThere">The Vulkan loader counts as there (a build's check: it comes with the driver); false on the PC itself.</param>
    /// <exception cref="InvalidDataException">llama-server.exe or a DLL beside it can't be read: nothing can be said of what it needs.</exception>
    public static IReadOnlyList<string> Missing(string folder, string? systemFolder = null, bool vulkanIsThere = true)
    {
        var here = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(folder, "*.dll").Prepend(Path.Combine(folder, "llama-server.exe")).Where(File.Exists);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            IReadOnlyList<string> imports;
            try
            {
                imports = Of(file);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{Path.GetFileName(file)}: {e.Message}", e);
            }
            foreach (var name in imports)
            {
                if (here.Contains(name) || IsWindows(name)) continue;
                if (vulkanIsThere && name.Equals(VulkanLoader, StringComparison.OrdinalIgnoreCase)) continue;
                if (systemFolder is not null && File.Exists(Path.Combine(systemFolder, name))) continue;
                missing.Add(name);
            }
        }
        return [.. missing];
    }

    static bool IsWindows(string name) =>
        Windows.Contains(name) || name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);

    static int I32(byte[] b, int at) => BitConverter.ToInt32(b, at);

    static int U16(byte[] b, int at) => BitConverter.ToUInt16(b, at);
}

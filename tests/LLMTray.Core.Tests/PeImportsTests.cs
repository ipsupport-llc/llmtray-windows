using System.Text;
using LLMTray.Core.Runtime;

namespace LLMTray.Core.Tests;

public sealed class PeImportsTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("llmtray-pe-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A minimal 64-bit PE: one section holding an import and a delay-load directory.</summary>
    static byte[] Pe(string[] imports, string[] delayed)
    {
        var b = new byte[0x800];
        void I32(int at, int v) => BitConverter.GetBytes(v).CopyTo(b, at);
        void U16(int at, int v) => BitConverter.GetBytes((ushort)v).CopyTo(b, at);
        b[0] = (byte)'M';
        b[1] = (byte)'Z';
        const int pe = 0x80, optional = pe + 24, section = optional + 240;
        const int va = 0x1000, raw = 0x400;
        I32(0x3c, pe);
        I32(pe, 0x00004550);
        U16(pe + 6, 1);
        U16(pe + 20, 240);
        U16(optional, 0x20b);
        I32(section + 8, 0x400);
        I32(section + 12, va);
        I32(section + 16, 0x400);
        I32(section + 20, raw);
        var names = va + 0x200;
        int Name(string name)
        {
            var at = names;
            Encoding.ASCII.GetBytes(name).CopyTo(b, at - va + raw);
            names += name.Length + 1;
            return at;
        }
        I32(optional + 112 + 8, va);
        for (var i = 0; i < imports.Length; i++) I32(raw + i * 20 + 12, Name(imports[i]));
        if (delayed.Length > 0)
        {
            I32(optional + 112 + 13 * 8, va + 0x100);
            for (var i = 0; i < delayed.Length; i++) I32(raw + 0x100 + i * 32 + 4, Name(delayed[i]));
        }
        return b;
    }

    [Fact]
    public void ReadsImportsAndDelayLoads()
    {
        var names = PeImports.Of(Pe(["llama.dll", "VCRUNTIME140.dll", "KERNEL32.dll"], ["libomp.dll"]));
        Assert.Equal(["llama.dll", "VCRUNTIME140.dll", "KERNEL32.dll", "libomp.dll"], names);
    }

    [Fact]
    public void ANonPeFileIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => PeImports.Of(Encoding.ASCII.GetBytes("not a program at all, just some text to read")));
        var cut = Pe(["a.dll"], []).AsSpan(0, 0x90).ToArray();
        Assert.Throws<InvalidDataException>(() => PeImports.Of(cut));
    }

    [Fact]
    public void NamesWhatTheFolderLacks()
    {
        File.WriteAllBytes(Path.Combine(root, "llama-server.exe"), Pe(["llama.dll", "VCRUNTIME140.dll", "KERNEL32.dll", "api-ms-win-crt-runtime-l1-1-0.dll"], ["libomp.dll"]));
        File.WriteAllBytes(Path.Combine(root, "llama.dll"), Pe(["MSVCP140.dll", "vulkan-1.dll"], []));
        // Not a PE: skipped, but there.
        File.WriteAllText(Path.Combine(root, "libomp.dll"), "stand-in");
        Assert.Equal(["MSVCP140.dll", "VCRUNTIME140.dll"], PeImports.Missing(root));

        // Beside the server (any case), or on this PC: not missing.
        File.WriteAllText(Path.Combine(root, "vcruntime140.dll"), "stand-in");
        var system = Directory.CreateDirectory(Path.Combine(root, "system32")).FullName;
        File.WriteAllText(Path.Combine(system, "MSVCP140.dll"), "stand-in");
        Assert.Equal(["MSVCP140.dll"], PeImports.Missing(root));
        Assert.Empty(PeImports.Missing(root, system));
    }

    [Fact]
    public void TheExitCodeIsWindows()
    {
        Assert.Equal(-1073741515, PeImports.DllNotFound);
    }
}

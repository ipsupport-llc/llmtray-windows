using System.Runtime.InteropServices;
using System.Text;
using LLMTray.Core.Mcp;

namespace LLMTray.App.Interop;

/// <summary>
/// Secrets (sign-in tokens, adr/0015) as files only this Windows user can
/// read back: each encrypted with DPAPI for the current user, written
/// atomically, in a folder of their own under the app's data.
/// </summary>
sealed class ProtectedSecrets(string folder) : IMcpSecrets
{
    readonly object sync = new();
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LLMTray secrets v1");

    string FileOf(string name)
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        return Path.Combine(folder, safe + ".bin");
    }

    public string? Get(string name)
    {
        lock (sync)
        {
            try
            {
                var data = Unprotect(File.ReadAllBytes(FileOf(name)));
                return data is null ? null : Encoding.UTF8.GetString(data);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Set(string name, string? value)
    {
        lock (sync)
        {
            var file = FileOf(name);
            if (value is null)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
                return;
            }
            Directory.CreateDirectory(folder);
            var data = Protect(Encoding.UTF8.GetBytes(value)) ?? throw new IOException("Windows couldn't protect the secret");
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, file, overwrite: true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    const int UiForbidden = 0x1;

    static byte[]? Protect(byte[] plain) => Run(plain, protect: true);

    static byte[]? Unprotect(byte[] cipher) => Run(cipher, protect: false);

    static byte[]? Run(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var blob = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref blob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}

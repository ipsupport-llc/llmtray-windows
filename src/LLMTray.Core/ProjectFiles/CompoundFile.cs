using System.Buffers.Binary;
using System.Text;

namespace LLMTray.Core.ProjectFiles;

/// <summary>
/// Just enough of OLE2 / Compound File Binary ([MS-CFB]) to read the root
/// storage's streams: what the old Word, Excel and PowerPoint files are made
/// of. Every chain walk is bounded by the sector count (a FAT loop is a
/// damaged file, not a hang) and every stream by the file's size. Runs in
/// the reader process only (adr/0021).
/// </summary>
public sealed class CompoundFile
{
    public static ReadOnlySpan<byte> Magic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    const uint MaxRegular = 0xFFFFFFFA;
    const uint NoStream = 0xFFFFFFFF;

    readonly byte[] data;
    readonly int size;
    readonly int sectorCount;
    readonly List<uint> fatSectors = [];
    readonly uint fatSectorClaim;
    readonly uint miniCutoff;
    readonly Dictionary<string, (uint Start, long Size)> streams = new(StringComparer.OrdinalIgnoreCase);
    byte[]? miniStream;
    byte[]? miniFat;
    readonly uint miniFatStart;
    readonly (uint Start, long Size) root;

    CompoundFile(byte[] data)
    {
        this.data = data;
        if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(Magic)) throw Damaged();
        var shift = U16(data, 0x1E);
        if (shift is not (9 or 12)) throw Damaged();
        size = 1 << shift;
        sectorCount = (int)((data.LongLength - size + size - 1) / size);
        if (sectorCount <= 0) throw Damaged();
        miniCutoff = U32(data, 0x38);
        miniFatStart = U32(data, 0x3C);

        // The FAT's own sectors: 109 in the header, the rest in the DIFAT chain.
        fatSectorClaim = U32(data, 0x2C);
        if (fatSectorClaim > (uint)sectorCount) throw Damaged();
        for (var i = 0; i < 109 && fatSectors.Count < fatSectorClaim; i++)
        {
            var s = U32(data, 0x4C + i * 4);
            if (s < MaxRegular) fatSectors.Add(s);
        }
        var next = U32(data, 0x44);
        var per = size / 4 - 1;
        for (var steps = 0; next < MaxRegular && fatSectors.Count < fatSectorClaim; steps++)
        {
            if (steps > sectorCount) throw Damaged();
            var at = Offset(next);
            for (var i = 0; i < per && fatSectors.Count < fatSectorClaim; i++)
            {
                var s = U32(data, at + i * 4);
                if (s < MaxRegular) fatSectors.Add(s);
            }
            next = U32(data, at + per * 4);
        }

        // The directory: 128-byte entries; the root's children are a tree of left and right siblings.
        var directory = Chain(U32(data, 0x30), long.MaxValue, 16 << 20);
        var count = directory.Length / 128;
        if (count == 0 || directory[66] != 5) throw Damaged();
        root = (U32(directory, 116), Length(directory, 0));
        var stack = new Stack<uint>([U32(directory, 76)]);
        var seen = new HashSet<uint>();
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (i >= count || !seen.Add(i)) continue;
            var o = (int)i * 128;
            stack.Push(U32(directory, o + 68));
            stack.Push(U32(directory, o + 72));
            if (directory[o + 66] != 2) continue;
            var length = Math.Min(64, (int)U16(directory, o + 64));
            var name = Encoding.Unicode.GetString(directory, o, Math.Max(0, length - 2));
            streams.TryAdd(name, (U32(directory, o + 116), Length(directory, o)));
        }
    }

    /// <summary>Opens the file's bytes: a file that isn't one, or is damaged, throws an <see cref="ExtractException"/>.</summary>
    public static CompoundFile Open(byte[] data) => new(data);

    public bool Has(string name) => streams.ContainsKey(name);

    /// <summary>A stream at the root, by name (any case); null when there's none.</summary>
    public byte[]? Stream(string name)
    {
        if (!streams.TryGetValue(name, out var entry)) return null;
        if (entry.Size > data.Length) throw Damaged();
        if (entry.Size >= miniCutoff) return Chain(entry.Start, entry.Size, entry.Size);
        // Small streams live in the mini stream, in 64-byte sectors of their own chain.
        miniStream ??= Chain(root.Start, root.Size, Math.Min(root.Size, data.Length));
        miniFat ??= Chain(miniFatStart, long.MaxValue, data.Length);
        var output = new byte[entry.Size];
        var s = entry.Start;
        var seen = new HashSet<uint>();
        for (long done = 0; done < entry.Size; )
        {
            // A chain that comes back to a sector is damage, not more data.
            if (s >= MaxRegular || !seen.Add(s)) throw Damaged();
            var at = (long)s * 64;
            if (at + 64 > miniStream.Length) throw Damaged();
            var n = (int)Math.Min(64, entry.Size - done);
            Array.Copy(miniStream, at, output, done, n);
            done += n;
            if ((long)s * 4 + 4 > miniFat.Length) throw Damaged();
            s = U32(miniFat, (int)s * 4);
        }
        return output;
    }

    /// <summary>A chain of regular sectors, cut to <paramref name="length"/>; longer than <paramref name="cap"/> is damage.</summary>
    byte[] Chain(uint start, long length, long cap)
    {
        using var output = new MemoryStream();
        var s = start;
        var seen = new HashSet<uint>();
        while (s < MaxRegular && output.Length < length)
        {
            // A chain that comes back to a sector is damage, not more data.
            if (!seen.Add(s) || output.Length + size > cap + size) throw Damaged();
            var at = Offset(s);
            // A last sector cut short is fine only as far as nothing past the file's end is needed from it.
            var needed = (int)Math.Min(size, length - output.Length);
            if (at + (long)needed > data.Length) throw Damaged();
            output.Write(data, at, needed);
            s = Fat(s);
        }
        if (length != long.MaxValue)
        {
            if (output.Length < length) throw Damaged();
            output.SetLength(length);
        }
        return output.ToArray();
    }

    /// <summary>The next sector, looked up in place: a header can claim a FAT far larger than the file.</summary>
    uint Fat(uint s)
    {
        var per = (uint)(size / 4);
        var i = s / per;
        if (i >= fatSectors.Count) throw Damaged();
        return U32(data, Offset(fatSectors[(int)i]) + (int)(s % per) * 4);
    }

    int Offset(uint sector)
    {
        if (sector >= sectorCount) throw Damaged();
        return (int)((sector + 1) * (long)size);
    }

    /// <summary>A directory entry's stream size; a version 3 file's high half is junk by the spec.</summary>
    long Length(byte[] directory, int o) => size == 512 ? U32(directory, o + 120) : (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(o + 120)), long.MaxValue);

    internal static ushort U16(byte[] d, int o) => o >= 0 && (long)o + 2 <= d.Length ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) : (ushort)0;

    internal static uint U32(byte[] d, int o) => o >= 0 && (long)o + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)) : NoStream;

    static ExtractException Damaged() => new("the file is damaged");
}

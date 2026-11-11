using System.Text;

namespace LLMTray.Core.Licenses;

/// <summary>
/// Reads string values from a GGUF file's header metadata. Only the header
/// is read (the tokenizer's large arrays are skipped over, not read), and
/// reading stops once every key asked for is found.
/// </summary>
public static class GgufMetadata
{
    /// <summary>Like <see cref="ReadStrings"/>, but null when the file couldn't be read (as opposed to not having the keys).</summary>
    public static IReadOnlyDictionary<string, string>? TryReadStrings(string path, IReadOnlyCollection<string> keys)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
            {
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return ReadStrings(path, keys);
    }

    public static IReadOnlyDictionary<string, string> ReadStrings(string path, IReadOnlyCollection<string> keys)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != 0x46554747) return found; // "GGUF"
            var version = reader.ReadUInt32();
            if (version < 2) return found;
            reader.ReadUInt64(); // tensors
            var count = reader.ReadUInt64();
            for (ulong i = 0; i < count && found.Count < keys.Count; i++)
            {
                var key = ReadString(reader);
                var type = reader.ReadUInt32();
                if (type == 8)
                {
                    var value = ReadString(reader);
                    if (keys.Contains(key)) found[key] = value;
                }
                else
                {
                    Skip(reader, type);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException)
        {
        }
        return found;
    }

    /// <summary>The bool values among <paramref name="keys"/> in a GGUF header (type 7); the ones missing or unreadable are absent.</summary>
    public static IReadOnlyDictionary<string, bool> ReadBools(string path, IReadOnlyCollection<string> keys)
    {
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != 0x46554747) return found; // "GGUF"
            if (reader.ReadUInt32() < 2) return found;
            reader.ReadUInt64(); // tensors
            var count = reader.ReadUInt64();
            for (ulong i = 0; i < count && found.Count < keys.Count; i++)
            {
                var key = ReadString(reader);
                var type = reader.ReadUInt32();
                if (type == 7 && keys.Contains(key))
                {
                    found[key] = reader.ReadByte() != 0;
                }
                else
                {
                    Skip(reader, type);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException)
        {
        }
        return found;
    }

    static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > 1 << 20) throw new InvalidDataException("string too long for a header");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    static void Skip(BinaryReader reader, uint type)
    {
        switch (type)
        {
            case 0: case 1: case 7: reader.BaseStream.Seek(1, SeekOrigin.Current); break;
            case 2: case 3: reader.BaseStream.Seek(2, SeekOrigin.Current); break;
            case 4: case 5: case 6: reader.BaseStream.Seek(4, SeekOrigin.Current); break;
            case 10: case 11: case 12: reader.BaseStream.Seek(8, SeekOrigin.Current); break;
            case 8: reader.BaseStream.Seek((long)reader.ReadUInt64(), SeekOrigin.Current); break;
            case 9:
                var elementType = reader.ReadUInt32();
                var n = reader.ReadUInt64();
                for (ulong i = 0; i < n; i++) Skip(reader, elementType);
                break;
            default: throw new InvalidDataException($"unknown GGUF value type {type}");
        }
    }
}

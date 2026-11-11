using System.Buffers.Binary;

namespace LLMTray.Core.Chat;

/// <summary>
/// Spoken messages (adr/0023, Mac adr/0023): a recording is 16 kHz mono
/// 16-bit PCM wrapped as a WAV in memory, at most 30 seconds, at most eight
/// to a request.
/// </summary>
public static class Wav
{
    public const int SampleRate = 16_000;
    public const int BytesPerSecond = SampleRate * 2;
    /// <summary>The longest clip (Gemma 4's extractor cuts longer ones).</summary>
    public const int MaxSeconds = 30;
    /// <summary>Clips a request carries, and a message holds.</summary>
    public const int MaxClips = 8;
    const int HeaderBytes = 44;

    /// <summary>A WAV of 16 kHz mono PCM16 samples (little-endian bytes), cut to <see cref="MaxSeconds"/>.</summary>
    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm)
    {
        var length = Math.Min(pcm.Length & ~1, MaxSeconds * BytesPerSecond);
        var wav = new byte[HeaderBytes + length];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + length);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], length);
        pcm[..length].CopyTo(s[HeaderBytes..]);
        return wav;
    }

    /// <summary>A RIFF/WAVE file (what goes to the model as input_audio; the Mac's music is .m4a and doesn't).</summary>
    public static bool IsWav(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8);

    /// <summary>A WAV's length in seconds from its fmt and data chunks; null when it isn't one that says.</summary>
    public static double? Seconds(ReadOnlySpan<byte> wav)
    {
        if (!IsWav(wav)) return null;
        var rate = 0;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(pos + 4)..]);
            if (size < 0) return null;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && pos + 24 <= wav.Length)
            {
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav[(pos + 16)..]); // bytes a second
            }
            else if (id.SequenceEqual("data"u8))
            {
                var bytes = Math.Min(size, wav.Length - pos - 8);
                return rate > 0 ? (double)bytes / rate : null;
            }
            pos += 8 + size + (size & 1);
        }
        return null;
    }

    /// <summary>A recording's prompt tokens, as Gemma 4 counts audio: one soft token per 40 ms, and its begin and end.</summary>
    public static int Tokens(int wavBytes) => (int)Math.Ceiling(Math.Max(0, wavBytes - HeaderBytes) / (double)BytesPerSecond * 25) + 2;

    /// <summary>"0:07".</summary>
    public static string Clock(double seconds)
    {
        var s = (int)Math.Max(0, Math.Round(seconds));
        return $"{s / 60}:{s % 60:00}";
    }
}

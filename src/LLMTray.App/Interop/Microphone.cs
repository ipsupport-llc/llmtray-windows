using System.Runtime.InteropServices;
using LLMTray.Core.Chat;

namespace LLMTray.App.Interop;

/// <summary>The microphone couldn't be opened (none, or access is off in Windows' privacy settings).</summary>
sealed class MicrophoneException(string message) : Exception(message);

/// <summary>
/// A spoken message being recorded (adr/0023): the default microphone at
/// 16 kHz mono 16-bit (Windows converts), collected in memory and wrapped
/// as a WAV in memory; nothing touches the disk. It stops itself at
/// <see cref="Wav.MaxSeconds"/>. waveIn, polled on a thread of its own:
/// it works the same packaged or portable, with no COM and no callbacks
/// into managed code from the audio driver.
/// </summary>
sealed class Microphone : IDisposable
{
    const int Buffers = 4;
    const int BufferBytes = Wav.BytesPerSecond / 10; // 100 ms
    const int MaxBytes = Wav.MaxSeconds * Wav.BytesPerSecond;

    readonly object sync = new();
    readonly MemoryStream pcm = new();
    readonly IntPtr handle;
    readonly IntPtr[] headers = new IntPtr[Buffers];
    readonly Thread thread;
    volatile bool stopping;
    bool closed;
    float level;

    /// <summary>Raised once, on the recording's thread, when it reached the longest clip (or the driver failed) and stopped taking sound.</summary>
    public event Action? Full;

    Microphone(IntPtr handle)
    {
        this.handle = handle;
        thread = new Thread(Loop) { IsBackground = true, Name = "Microphone" };
    }

    /// <summary>Opens the default microphone and starts recording.</summary>
    public static Microphone Start()
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSec = Wav.SampleRate,
            AvgBytesPerSec = Wav.BytesPerSecond,
            BlockAlign = 2,
            BitsPerSample = 16,
            Size = 0,
        };
        var result = waveInOpen(out var handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            throw new MicrophoneException(result == BadDeviceId || result == NoDriver
                ? L.T("No microphone was found.")
                : L.T("The microphone couldn't be opened. Check that LLMTray may use it in Windows Settings › Privacy & security › Microphone."));
        }
        var mic = new Microphone(handle);
        try
        {
            for (var i = 0; i < Buffers; i++)
            {
                var data = Marshal.AllocHGlobal(BufferBytes);
                var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = BufferBytes }, header, false);
                mic.headers[i] = header;
                Check(waveInPrepareHeader(handle, header, Marshal.SizeOf<WaveHeader>()));
                Check(waveInAddBuffer(handle, header, Marshal.SizeOf<WaveHeader>()));
            }
            Check(waveInStart(handle));
        }
        catch
        {
            mic.Close();
            throw;
        }
        mic.thread.Start();
        return mic;
    }

    static void Check(int result)
    {
        if (result != 0) throw new MicrophoneException(L.T("The microphone couldn't be opened. Check that LLMTray may use it in Windows Settings › Privacy & security › Microphone."));
    }

    /// <summary>How long it has recorded.</summary>
    public double Seconds
    {
        get { lock (sync) return (double)pcm.Length / Wav.BytesPerSecond; }
    }

    /// <summary>The loudness of the last tenth of a second, 0–1.</summary>
    public float Level
    {
        get { lock (sync) return level; }
    }

    static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
    static readonly int RecordedOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BytesRecorded));
    /// <summary>The buffer the driver fills next: they come back in the order they were queued.</summary>
    int next;
    readonly byte[] chunk = new byte[BufferBytes];

    /// <summary>
    /// Takes the buffers that are done, in queue order, under <c>sync</c>; each is handed back for
    /// more sound when <paramref name="requeue"/>. True when any came.
    /// </summary>
    bool DrainLocked(bool requeue)
    {
        var any = false;
        for (var n = 0; n < Buffers && !full; n++)
        {
            var header = headers[next];
            if ((Marshal.ReadInt32(header, FlagsOffset) & Done) == 0) break;
            any = true;
            var bytes = Math.Min(Marshal.ReadInt32(header, RecordedOffset), BufferBytes) & ~1;
            Marshal.Copy(Marshal.ReadIntPtr(header, 0), chunk, 0, bytes);
            pcm.Write(chunk, 0, (int)Math.Min(bytes, MaxBytes - pcm.Length));
            var peak = 0;
            for (var i = 0; i + 1 < bytes; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(chunk, i)));
            level = Math.Min(1f, peak / 32768f);
            full = pcm.Length >= MaxBytes;
            // Clear the flag first: a buffer not handed back isn't read twice.
            Marshal.WriteInt32(header, FlagsOffset, Marshal.ReadInt32(header, FlagsOffset) & ~Done);
            // A buffer the driver won't take back ends the recording here (the queue order needs every one).
            if (requeue && !full && waveInAddBuffer(handle, header, Marshal.SizeOf<WaveHeader>()) != 0) full = true;
            next = (next + 1) % Buffers;
        }
        return any;
    }

    bool full;

    void Loop()
    {
        while (!stopping)
        {
            bool any, reachedEnd;
            lock (sync)
            {
                // Closed meanwhile (a stop that didn't wait long enough): the buffers are gone.
                if (closed) return;
                any = DrainLocked(requeue: true);
                reachedEnd = full;
                // The microphone goes quiet now, not when the window gets round to the clip.
                if (full) waveInStop(handle);
            }
            if (reachedEnd)
            {
                Full?.Invoke();
                return;
            }
            if (!any) Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Stops and returns the recording as a WAV (empty sound when nothing came). The driver hands
    /// back what it holds first, the part-filled buffer with what it got: the last syllable stays.
    /// </summary>
    public byte[] Stop()
    {
        stopping = true;
        if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(2));
        lock (sync)
        {
            if (!closed)
            {
                waveInReset(handle);
                DrainLocked(requeue: false);
            }
            var wav = Wav.FromPcm16(pcm.GetBuffer().AsSpan(0, (int)pcm.Length));
            Close();
            return wav;
        }
    }

    /// <summary>Drops the recording.</summary>
    public void Dispose()
    {
        stopping = true;
        if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(2));
        lock (sync) Close();
    }

    void Close()
    {
        if (closed) return;
        closed = true;
        waveInReset(handle);
        foreach (var header in headers.Where(h => h != IntPtr.Zero))
        {
            waveInUnprepareHeader(handle, header, Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(header, 0));
            Marshal.FreeHGlobal(header);
        }
        waveInClose(handle);
        pcm.SetLength(0);
    }

    const uint WaveMapper = 0xFFFFFFFF;
    const int Done = 0x1;
    const int BadDeviceId = 2;
    const int NoDriver = 6;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    static extern int waveInOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")]
    static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")]
    static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")]
    static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")]
    static extern int waveInStart(IntPtr handle);
    [DllImport("winmm.dll")]
    static extern int waveInReset(IntPtr handle);
    [DllImport("winmm.dll")]
    static extern int waveInStop(IntPtr handle);
    [DllImport("winmm.dll")]
    static extern int waveInClose(IntPtr handle);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMTray.Core.Usage;

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UsageLedger))]
internal sealed partial class UsageJson : JsonSerializerContext;

/// <summary>
/// The usage statistics on disk (adr/0026): fed by the proxy while the user
/// has them on, never sent anywhere. Written at most every few seconds, and
/// when the app quits. An unreadable file is set aside, not written over.
/// </summary>
public sealed class UsageStore : IDisposable
{
    readonly object sync = new();
    // One writer at a time: the timer's and the one at quit.
    readonly object writing = new();
    readonly string file;
    readonly Timer timer;
    UsageLedger ledger;
    bool dirty;
    bool scheduled;

    public UsageStore(string file)
    {
        this.file = file;
        timer = new Timer(_ => Flush());
        ledger = Load(file);
    }

    public event Action? Changed;

    static UsageLedger Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return new UsageLedger();
            return JsonSerializer.Deserialize(File.ReadAllText(file), UsageJson.Default.UsageLedger) ?? new UsageLedger();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                File.Move(file, file + ".unreadable", overwrite: true);
            }
            catch (Exception m) when (m is IOException or UnauthorizedAccessException)
            {
                // Left where it is: still not written over before it moves.
            }
            return new UsageLedger();
        }
    }

    /// <summary>A look at the ledger under its lock.</summary>
    public T Read<T>(Func<UsageLedger, T> read)
    {
        lock (sync) return read(ledger);
    }

    public void Record(RequestStats stats, string model, DateTime local)
    {
        lock (sync)
        {
            ledger.Record(stats, model, local);
            dirty = true;
            if (!scheduled)
            {
                scheduled = true;
                timer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (sync)
        {
            ledger.Clear();
            dirty = true;
        }
        Flush();
        Changed?.Invoke();
    }

    /// <summary>Written now, if anything changed (at quit, or when the timer fires).</summary>
    public void Flush()
    {
        string text;
        lock (sync)
        {
            scheduled = false;
            if (!dirty) return;
            text = JsonSerializer.Serialize(ledger, UsageJson.Default.UsageLedger);
            dirty = false;
        }
        try
        {
            lock (writing)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + ".tmp";
                File.WriteAllText(temp, text);
                File.Move(temp, file, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tried again in a while.
            lock (sync)
            {
                dirty = true;
                if (!scheduled)
                {
                    scheduled = true;
                    timer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        Flush();
    }
}

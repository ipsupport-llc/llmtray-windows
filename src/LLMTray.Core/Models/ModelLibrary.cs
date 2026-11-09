namespace LLMTray.Core.Models;

/// <summary>
/// The current catalog of the models folder, shared by the window, the
/// tray menu and the proxy. A request for a name the catalog lacks rescans
/// (a model copied in by hand), at most every few seconds, so a client that
/// always asks for some other name doesn't rescan on every request.
/// </summary>
public sealed class ModelLibrary(Func<string> root)
{
    static readonly TimeSpan MissRescanInterval = TimeSpan.FromSeconds(5);

    readonly object sync = new();
    ModelCatalog current = ModelCatalog.Empty(root());
    DateTime lastMissRescan = DateTime.MinValue;

    /// <summary>Raised on the thread that rescanned.</summary>
    public event Action<ModelCatalog>? Changed;

    public ModelCatalog Current
    {
        get { lock (sync) return current; }
    }

    public ModelCatalog Rescan()
    {
        var scanned = ModelCatalog.Scan(root());
        lock (sync) current = scanned;
        Changed?.Invoke(scanned);
        return scanned;
    }

    public ModelEntry? Resolve(string name)
    {
        var hit = Current.Resolve(name);
        // A hit whose file is gone (deleted or moved): rescan, so a stale
        // entry never stops the working model for nothing.
        if (hit is not null && File.Exists(hit.FullPath)) return hit;
        if (hit is null)
        {
            lock (sync)
            {
                if (DateTime.UtcNow - lastMissRescan < MissRescanInterval) return null;
                lastMissRescan = DateTime.UtcNow;
            }
        }
        return Rescan().Resolve(name);
    }
}

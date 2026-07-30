namespace VortexArena.Net;

/// <summary>Owns the one rule that matters about the map catalog: it is computed off the simulation
/// thread, once, and cached (map-catalog-v1 §10).
///
/// Hashing a few hundred packages is seconds of disk work. Doing it on the sim thread would stall the
/// world for that long, and doing it inside an announce would put it in the same failure class — the
/// announce runs on a worker, but it runs every 180 seconds, and re-reading the entire content tree at
/// that cadence is a disk load nobody asked for. So the scan happens here: at server start, and
/// afterwards only when an operator says <c>sv_master_catalog_refresh</c>.
///
/// <para>Everything crossing into the worker is a value the caller sampled on its own thread — the
/// package paths and the download base URL — for the same reason
/// <see cref="MasterAnnounce.AnnounceSnapshot"/> is: the worker must never reach into live server
/// state.</para>
///
/// <para>A rebuild does not clear the cache. The previous snapshot stays announceable until the new one
/// is finished, so a refresh costs nothing and a slow disk does not delist a server's pool.</para></summary>
public sealed class MapCatalogCache : IDisposable
{
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private volatile MapCatalogSnapshot? _current;
    private Task? _build;

    public MapCatalogCache(Action<string> log) => _log = log;

    /// <summary>The pool as of the last completed scan, or null when none has finished yet. Read from
    /// the announce worker.
    ///
    /// Null is why <see cref="MasterAnnounce"/> omits <c>map_catalog_hash</c> on the first announce of a
    /// cold server rather than waiting for the scan: announcing a hash whose index cannot be uploaded
    /// yet would be a claim the server cannot back, and the next announce carries it anyway.</summary>
    public MapCatalogSnapshot? Current => _current;

    /// <summary>True while a scan is running. Only for the console reply — the caller must not branch on
    /// it, since it can change the moment after it is read.</summary>
    public bool Building
    {
        get { lock (_gate) return _build is { IsCompleted: false }; }
    }

    /// <summary>Scan once, at server start, if nothing has been scanned yet. Idempotent and cheap to
    /// call repeatedly: this is also the path that picks a catalog up when an operator turns
    /// <c>sv_master_catalog</c> on partway through a session, which is why it is called from the announce
    /// tick as well as from startup.</summary>
    public void EnsureBuilt(IReadOnlyList<string> packagePaths, string? downloadBaseUrl)
    {
        if (_current is not null)
            return;
        Start(packagePaths, downloadBaseUrl, "building map catalog");
    }

    /// <summary>The <c>sv_master_catalog_refresh</c> path: rescan even though a pool is already cached,
    /// because the operator has just changed the data directory. False when a scan is already running,
    /// which is a fact the console reply should report rather than a failure.</summary>
    public bool Refresh(IReadOnlyList<string> packagePaths, string? downloadBaseUrl) =>
        Start(packagePaths, downloadBaseUrl, "rebuilding map catalog");

    private bool Start(IReadOnlyList<string> packagePaths, string? downloadBaseUrl, string what)
    {
        lock (_gate)
        {
            // One scan at a time. A second one would read the same several hundred megabytes
            // concurrently with the first and finish with the same answer.
            if (_build is { IsCompleted: false })
                return false;

            _log($"{what} ({packagePaths.Count} packages)");
            _build = Task.Run(() => Build(packagePaths, downloadBaseUrl), _shutdown.Token);
            return true;
        }
    }

    private void Build(IReadOnlyList<string> packagePaths, string? downloadBaseUrl)
    {
        try
        {
            var snapshot = MapCatalogScan.Scan(packagePaths, _log, downloadBaseUrl, _shutdown.Token);
            _current = snapshot;
            _log($"map catalog ready: {snapshot.Entries.Count} packages, hash {snapshot.CatalogHash}");
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-scan. Nothing to say and nothing to keep.
        }
        catch (Exception ex)
        {
            // A catalog that cannot be built must never take the server or the announce lane with it.
            // Any previously cached snapshot survives and keeps being announced.
            _log($"map catalog scan failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        // Bounded, like the announce worker's: a scan can be in the middle of a half-gigabyte read and
        // shutdown must not wait it out.
        try { _build?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _shutdown.Dispose();
    }
}

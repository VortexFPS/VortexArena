using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Conductor.Protocol;

namespace VortexArena.Net;

/// <summary>What <see cref="MasterAnnounce.RefreshCatalog"/> did, for the console reply.</summary>
public enum CatalogRefresh
{
    /// <summary>A rescan is running on a worker. It will be announced when it finishes.</summary>
    Started,

    /// <summary>A scan was already running; a second one would read the same content tree concurrently
    /// and reach the same answer.</summary>
    AlreadyRunning,

    /// <summary><c>sv_master_catalog</c> is 0, so there is nothing to report a catalog to.</summary>
    Disabled,
}

/// <summary>DS-7: the modern announce lane.
///
/// Additive. <see cref="MasterServerLink"/> keeps speaking classic dpmaster for LAN discovery and
/// legacy tooling, and the getinfo responder is untouched, which is what lets the master verify this
/// server with a UDP challenge without any new listener here.
///
/// Everything network happens on a worker. <see cref="Tick"/> is called from the simulation loop and
/// does nothing but compare a clock and set a flag: an HttpClient call on that thread would put a
/// network round trip inside the frame budget, and the failure mode is a hitch that only shows up
/// when the master is slow.
///
/// The map catalog (map-catalog-v1) rides the same rule and takes it further: reading and hashing a few
/// hundred packages is seconds of DISK work, so it happens neither on the sim thread nor on an announce
/// but once, on its own worker, cached in <see cref="MapCatalogCache"/>. What the announce carries is
/// 64 bytes of hash, and the pool itself moves only when the master asks for it.</summary>
public sealed class MasterAnnounce : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<AnnounceSnapshot> _snapshot;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly MapCatalogCache _catalog;

    private Task? _worker;
    private DateTime _nextAnnounce = DateTime.MinValue;
    private volatile bool _announceNow;
    private string? _serverId;

    /// <summary>Everything the announce needs, sampled on the sim thread and handed across. The
    /// worker never reaches into live server state.</summary>
    public readonly record struct AnnounceSnapshot(
        string MasterUrl,
        int Port,
        string Hostname,
        string Map,
        string Gametype,
        int Players,
        int Bots,
        int MaxPlayers,
        string GameVersion,
        int NetProtocol,
        IReadOnlyList<string> Mutators,
        int SvPublic,
        bool PasswordProtected,
        bool AvailableForControl,
        string? ControlKeyFingerprint,
        bool ReportCatalog,
        IReadOnlyList<string> PackagePaths,
        string? CatalogDownloadUrl);

    public MasterAnnounce(Func<AnnounceSnapshot> snapshot, Action<string> log)
    {
        _snapshot = snapshot;
        _log = log;
        _catalog = new MapCatalogCache(log);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VortexArena-Server");
    }

    /// <summary>Call from the server tick. Cheap by construction.</summary>
    public void Tick()
    {
        if (DateTime.UtcNow < _nextAnnounce && !_announceNow)
            return;

        _nextAnnounce = DateTime.UtcNow.AddSeconds(AnnounceProtocol.AnnounceIntervalSeconds);

        // Cleared before the sv_public gate, not after. Left set, a map change on a PRIVATE server pins
        // this method past its early return forever, and the caller drives it from the per-frame master
        // pump — so every frame would build a snapshot (and its mutator list) to throw away.
        _announceNow = false;

        var snapshot = _snapshot();

        // A private server does not announce at all. Not "announces and asks not to be listed": the
        // announce is itself the disclosure, and a server that sends one has already told the master
        // it exists, where, and what it is running.
        if (snapshot.SvPublic != 1)
            return;

        // The startup scan (map catalog §10). This is the first Tick on a server that just came up,
        // because EnableMasterAnnounce arms an immediate announce — and it is also what picks a catalog
        // up when an operator turns sv_master_catalog on partway through a session, at the cost of one
        // announce interval. Idempotent after the first scan completes.
        if (snapshot.ReportCatalog)
            _catalog.EnsureBuilt(snapshot.PackagePaths, snapshot.CatalogDownloadUrl);

        if (_worker is { IsCompleted: false })
            return; // previous announce still in flight; skip rather than pile up

        _worker = Task.Run(() => AnnounceAsync(snapshot, _shutdown.Token));
    }

    /// <summary>Re-announce immediately on a map change, per the protocol's freshness contract.</summary>
    public void OnMapChanged() => _announceNow = true;

    /// <summary>The <c>sv_master_catalog_refresh</c> console command (map catalog §10). Call from the sim
    /// thread: it samples the snapshot there, exactly like <see cref="Tick"/>, and the scan it starts runs
    /// on a worker. The currently cached pool stays announceable until the new one lands.</summary>
    public CatalogRefresh RefreshCatalog()
    {
        var snapshot = _snapshot();
        if (!snapshot.ReportCatalog)
            return CatalogRefresh.Disabled;
        return _catalog.Refresh(snapshot.PackagePaths, snapshot.CatalogDownloadUrl)
            ? CatalogRefresh.Started
            : CatalogRefresh.AlreadyRunning;
    }

    /// <summary>The pool as of the last completed scan, or null when none has finished. For status
    /// output; the announce path uses the snapshot it captured, not this.</summary>
    public MapCatalogSnapshot? Catalog => _catalog.Current;

    private async Task AnnounceAsync(AnnounceSnapshot snapshot, CancellationToken ct)
    {
        // Captured once, and the same reference is what any upload below describes. A refresh landing
        // between this announce and the master's catalog_request would otherwise have us upload an index
        // that does not hash to the value we just announced, which the master rejects by design (§4).
        var catalog = snapshot.ReportCatalog ? _catalog.Current : null;

        var request = new AnnounceRequest
        {
            Port = snapshot.Port,
            Hostname = snapshot.Hostname,
            Map = snapshot.Map,
            Gametype = snapshot.Gametype,
            Players = snapshot.Players,
            Bots = snapshot.Bots,
            MaxPlayers = snapshot.MaxPlayers,
            GameVersion = snapshot.GameVersion,
            NetProtocol = snapshot.NetProtocol,
            Mutators = snapshot.Mutators.Count == 0 ? null : snapshot.Mutators,
            SvPublic = snapshot.SvPublic,
            PasswordProtected = snapshot.PasswordProtected,
            AvailableForControl = snapshot.AvailableForControl,
            ControlKeyFingerprint = snapshot.ControlKeyFingerprint,
            // Omitted entirely when sv_master_catalog is 0 (§3) — and also while the first scan is still
            // running, because a hash whose index we could not upload yet is a claim this server cannot
            // back. The next announce carries it: a fresh server's pool shows up one interval in, and
            // that is on purpose, because the announce interval is also the protocol's floor and firing
            // an extra announce the moment a scan finished would be sending faster than it allows.
            MapCatalogHash = catalog?.CatalogHash,
        };

        // Validate locally first. The same rules run on the master, and catching a misconfiguration
        // here names the offending field in this server's own log instead of surfacing as an opaque
        // 400 from a remote host.
        if (AnnounceValidation.Validate(request) is { } invalid)
        {
            _log($"master announce skipped: {invalid.Error.Field} {invalid.Error.Message}");
            return;
        }

        try
        {
            var url = snapshot.MasterUrl.TrimEnd('/') + AnnounceProtocol.AnnouncePath;
            using var response = await _http.PostAsJsonAsync(
                url, request, AnnounceProtocol.Json, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Delta
                            ?? TimeSpan.FromSeconds(AnnounceProtocol.AnnounceIntervalSeconds);
                _nextAnnounce = DateTime.UtcNow.Add(retry);
                return;
            }

            var body = await response.Content.ReadFromJsonAsync<AnnounceResponse>(
                AnnounceProtocol.Json, ct);
            if (body is null)
                return;

            _serverId = body.ServerId;

            switch (body.State)
            {
                case ListingState.Listed:
                    break;

                case ListingState.PendingChallenge:
                    // Nothing to do. The master challenges the getinfo responder and this becomes
                    // Listed on its own, with no further request from here.
                    break;

                case ListingState.Rejected:
                    _log($"master refused to list this server: {body.Detail}");
                    // Back off hard. A rejection is a decision, not a transient failure, and
                    // re-announcing on the normal cadence would just repeat it every three minutes.
                    _nextAnnounce = DateTime.UtcNow.AddHours(1);
                    return;
            }

            // Map catalog §3/§4. Absent is the steady state and means "send nothing, keep announcing",
            // which is the whole reason a catalog costs 64 bytes on a request that already exists.
            if (body.CatalogRequest is { } ask && catalog is not null)
                await UploadCatalogAsync(snapshot.MasterUrl, ask, catalog, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A master that is down must never affect a running game. Log and carry on: players
            // already connected do not care, and the dpmaster lane is unaffected.
            _log($"master announce failed: {ex.Message}");
        }
    }

    /// <summary>The two-phase upload from map catalog §4, on the announce worker.
    ///
    /// Phase 1 is the cheap half — hashes, names and sizes — and on a mature master the exchange ends
    /// there, because every package this server carries has already been described by somebody. Phase 2
    /// carries metadata and thumbnails for the globally new ones only, batched at the §8 cap.
    ///
    /// Never throws. A catalog upload is the least important thing this server does, and a master that
    /// mishandles one must not cost it its listing, let alone its match.</summary>
    private async Task UploadCatalogAsync(
        string masterUrl, CatalogRequest ask, MapCatalogSnapshot catalog, CancellationToken ct)
    {
        var index = new CatalogIndexRequest
        {
            CatalogHash = catalog.CatalogHash,
            Entries = catalog.Entries,
        };

        // The master recomputes this and rejects a mismatch, so a local failure here is a bug in the
        // scan that would otherwise show up as an opaque 409 from a remote host.
        if (MapCatalogValidation.ValidateIndex(index) is { } invalid)
        {
            _log($"map catalog upload skipped: {invalid.Error.Field} {invalid.Error.Message}");
            return;
        }

        var baseUrl = masterUrl.TrimEnd('/');
        try
        {
            _log($"master asked for the map catalog ({ask.Reason}); "
                 + $"sending an index of {catalog.Entries.Count} package(s)");

            using var indexResponse = await PostAsync(
                baseUrl + MapCatalogProtocol.IndexPath, ask.UploadToken, index, ct);
            if (!indexResponse.IsSuccessStatusCode)
            {
                _log($"map catalog index rejected: {await DescribeAsync(indexResponse, ct)}");
                return;
            }

            var unknown = await indexResponse.Content.ReadFromJsonAsync<CatalogIndexResponse>(
                AnnounceProtocol.Json, ct);
            if (unknown is null || unknown.UnknownPackages.Count == 0)
            {
                _log("map catalog accepted; the master already knew every package");
                return;
            }

            var described = 0;
            foreach (var batch in catalog.BatchDetails(unknown.UnknownPackages))
            {
                ct.ThrowIfCancellationRequested();

                using var batchResponse = await PostAsync(
                    baseUrl + MapCatalogProtocol.PackagesPath, ask.UploadToken, batch, ct);
                if (!batchResponse.IsSuccessStatusCode)
                {
                    // Stop rather than push the rest at a master that just refused a batch: the token is
                    // single-use per §8, so a rejection usually means the remaining batches have nothing
                    // to authenticate with either.
                    _log($"map catalog details rejected after {described} package(s): "
                         + $"{await DescribeAsync(batchResponse, ct)}");
                    return;
                }

                described += batch.Packages.Count;
            }

            _log($"map catalog uploaded: described {described} package(s) the master had not seen");
        }
        // JsonException is in here because a master answering 200 with something that is not a
        // CatalogIndexResponse would otherwise escape onto a Task.Run worker, where an unobserved
        // exception is not an error anybody sees — it is silence.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                      or JsonException or NotSupportedException)
        {
            _log($"map catalog upload failed: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> PostAsync<T>(
        string url, string uploadToken, T body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: AnnounceProtocol.Json),
        };
        // Bearer for both phases, single use and bound to this server_id, so it is worth nothing to
        // anyone else (§8).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
        // Awaited here so the request (and the megabytes of thumbnails in its content) is disposed as
        // soon as it has been sent. The default completion option buffers the whole response first, so
        // the returned message is fully readable after this returns.
        return await _http.SendAsync(request, ct);
    }

    /// <summary>Name the master's own error code in this server's log when there is one. An operator
    /// reading "HTTP 409" learns nothing; "catalog_hash_mismatch" tells them the pool changed under the
    /// upload.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ProtocolError>(
                AnnounceProtocol.Json, ct);
            if (error is not null)
                return $"HTTP {(int)response.StatusCode} {error.Error.Code}: {error.Error.Message}";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException
                                      or HttpRequestException or TaskCanceledException)
        {
            // Not a protocol error body — a proxy's HTML page, or nothing at all. The status code is
            // still worth logging.
        }

        return $"HTTP {(int)response.StatusCode}";
    }

    public string? ServerId => _serverId;

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _catalog.Dispose();
        _http.Dispose();
        _shutdown.Dispose();
    }
}

using System.Net.Http.Json;
using Conductor.Protocol;

namespace VortexArena.Net;

/// <summary>DS-7: the modern announce lane.
///
/// Additive. <see cref="MasterServerLink"/> keeps speaking classic dpmaster for LAN discovery and
/// legacy tooling, and the getinfo responder is untouched, which is what lets the master verify this
/// server with a UDP challenge without any new listener here.
///
/// Everything network happens on a worker. <see cref="Tick"/> is called from the simulation loop and
/// does nothing but compare a clock and set a flag: an HttpClient call on that thread would put a
/// network round trip inside the frame budget, and the failure mode is a hitch that only shows up
/// when the master is slow.</summary>
public sealed class MasterAnnounce : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<AnnounceSnapshot> _snapshot;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _shutdown = new();

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
        string? ControlKeyFingerprint);

    public MasterAnnounce(Func<AnnounceSnapshot> snapshot, Action<string> log)
    {
        _snapshot = snapshot;
        _log = log;
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

        if (_worker is { IsCompleted: false })
            return; // previous announce still in flight; skip rather than pile up

        _worker = Task.Run(() => AnnounceAsync(snapshot, _shutdown.Token));
    }

    /// <summary>Re-announce immediately on a map change, per the protocol's freshness contract.</summary>
    public void OnMapChanged() => _announceNow = true;

    private async Task AnnounceAsync(AnnounceSnapshot snapshot, CancellationToken ct)
    {
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
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A master that is down must never affect a running game. Log and carry on: players
            // already connected do not care, and the dpmaster lane is unaffected.
            _log($"master announce failed: {ex.Message}");
        }
    }

    public string? ServerId => _serverId;

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _http.Dispose();
        _shutdown.Dispose();
    }
}

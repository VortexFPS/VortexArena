namespace VortexArena.Modding;

public enum ModClientState
{
    /// <summary>No offer, or the last one was withdrawn. Stock presentation.</summary>
    Idle,
    /// <summary>An offer is waiting for the player's answer. Nothing has been downloaded or requested.</summary>
    AwaitingConsent,
    Downloading,
    /// <summary>Every file is in the cache and verified; the host should load <see cref="ModOfferClient.Plan"/> and report back.</summary>
    ReadyToLoad,
    /// <summary>The mod is running and the server has been told.</summary>
    Active,
    /// <summary>This client will not run the offered mod; see <see cref="ModOfferClient.DeclineReason"/>. The game goes on without it.</summary>
    Declined,
}

/// <summary>The client's side of the policy. Everything here is the player's ceiling; nothing a server sends can raise it.</summary>
public sealed record ModClientOptions
{
    /// <summary><c>cl_allow_mods</c>. False - the default - declines every offer without reading it.</summary>
    public bool AllowMods { get; init; }
    /// <summary>False where there is no WebAssembly runtime; every offer is then declined as unsupported.</summary>
    public bool SandboxAvailable { get; init; } = true;
    /// <summary>The server's address as the player connected to it; what remembered consent is keyed by.</summary>
    public string ServerKey { get; init; } = "";
    /// <summary>This connection's base protocol hash. A manifest made for another one is declined. Null skips the check.</summary>
    public uint? BaseProtocol { get; init; }
    public ModLimits LimitCeiling { get; init; } = ModLimits.Default;
    /// <summary>Most the client will download for one offer, whatever the manifest's own caps allow.</summary>
    public long MaxDownloadBytes { get; init; } = 256L << 20;
    /// <summary>A download that has not finished this long after it started is abandoned.</summary>
    public double DownloadTimeoutSeconds { get; init; } = 600;
    /// <summary>A download that receives nothing for this long is abandoned.</summary>
    public double StallTimeoutSeconds { get; init; } = 30;

    /// <summary>Mod-to-server messages: sustained rate, burst, and the same for bytes.</summary>
    public double ToServerPerSecond { get; init; } = 20;
    public double ToServerBurst { get; init; } = 40;
    public double ToServerBytesPerSecond { get; init; } = 8 * 1024;
    public double ToServerBurstBytes { get; init; } = 16 * 1024;

    /// <summary>Server-to-mod messages the client will hand to the mod: sustained rate and burst. The rest are dropped.</summary>
    public double ToClientPerSecond { get; init; } = 60;
    public double ToClientBurst { get; init; } = 120;
    public int MaxQueuedEvents { get; init; } = 64;
    public int MaxQueuedEventBytes { get; init; } = 256 * 1024;

    /// <summary>How often a server may make a new offer (a level change makes one). Extra offers are declined unread.</summary>
    public double OffersPerSecond { get; init; } = 0.5;
    public double OfferBurst { get; init; } = 4;
}

/// <summary>What the player is shown before anything is downloaded.</summary>
public sealed record ModConsentRequest
{
    public required ModManifest Manifest { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string ServerKey { get; init; }
    /// <summary>Bytes that would be downloaded; zero when every file is already cached.</summary>
    public long DownloadBytes { get; init; }
    public int DownloadFiles { get; init; }
    /// <summary>The limits the mod would actually run under (the server's requests, clamped to the client's ceiling).</summary>
    public required ModLimits Limits { get; init; }
}

/// <summary>A verified mod, ready to be loaded: where its files are in the cache and what it may use.</summary>
public sealed record ModLoadPlan
{
    public required ModManifest Manifest { get; init; }
    public required string ManifestSha256 { get; init; }
    /// <summary>The module's path in the cache, or null for an assets-only mod.</summary>
    public string? ModulePath { get; init; }
    /// <summary>The asset packs' cache paths, in mount order, paired with their manifest entries.</summary>
    public IReadOnlyList<(ModArtifact Artifact, string Path)> Packs { get; init; } = Array.Empty<(ModArtifact, string)>();
    public required ModLimits Limits { get; init; }
}

/// <summary>
/// The client's half of the mod-offer protocol (planning/specs/modding.md, section 9): what happens
/// between a server saying "I have a mod" and that mod running, with no Godot and no sandbox in it.
/// The caller feeds it the frames that arrive, the clock, and the player's answer, and sends what it
/// queues; when it reaches <see cref="ModClientState.ReadyToLoad"/> the caller loads
/// <see cref="Plan"/> and reports the result. <see cref="ModClientSession"/> is that caller.
///
/// The server is a stranger's. So: nothing is parsed while mods are off; nothing is requested before
/// the player (or a remembered answer bound to this exact manifest) allows it; a file is written only
/// in order, only up to its declared size, and enters the cache only if it hashes to the manifest's
/// value; every queue has a cap; and no input throws. Whatever goes wrong, the outcome is
/// <see cref="ModClientState.Declined"/> and a game that carries on without the mod - whether the
/// player may stay is the server's decision (<see cref="ModManifest.Required"/>), not this class's.
///
/// Single-threaded: call everything from the thread that owns the connection.
/// </summary>
public sealed class ModOfferClient : IDisposable
{
    private const int MaxOutboundFrames = 128;

    private readonly ModClientOptions _options;
    private readonly ModCache _cache;
    private readonly ModConsentStore _consent;
    private readonly Queue<byte[]> _outbound = new();
    private readonly Queue<(int Id, byte[] Payload)> _events = new();
    private readonly HashSet<string> _allowedThisConnection = new(StringComparer.Ordinal);
    private ModRateLimiter _offerRate, _toServerRate, _toClientRate;
    private bool _deniedThisConnection;
    private bool _allowMods;
    private int _queuedEventBytes;
    private byte _sequence;

    // The download in progress.
    private IReadOnlyList<ModArtifact> _needed = Array.Empty<ModArtifact>();
    private int _slot;
    private ModCacheWriter? _writer;
    private double _downloadStarted, _lastProgress;
    private bool _clockSet;

    public ModOfferClient(ModClientOptions options, ModCache cache, ModConsentStore consent)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _allowMods = options.AllowMods;
        _offerRate = NewOfferRate();
        _toServerRate = NewToServerRate();
        _toClientRate = NewToClientRate();
    }

    public ModClientState State { get; private set; } = ModClientState.Idle;
    /// <summary>The manifest of the current (or last declined) offer, once it has parsed.</summary>
    public ModManifest? Manifest { get; private set; }
    public string? ManifestSha256 { get; private set; }
    /// <summary>Set while <see cref="State"/> is <see cref="ModClientState.AwaitingConsent"/>.</summary>
    public ModConsentRequest? PendingConsent { get; private set; }
    /// <summary>
    /// Set from <see cref="ModClientState.ReadyToLoad"/> on and cleared - or replaced by a new object -
    /// whenever the mod it describes must stop running. A host that loaded a plan unloads it as soon as
    /// this is no longer that same object.
    /// </summary>
    public ModLoadPlan? Plan { get; private set; }
    public ModDeclineReason DeclineReason { get; private set; }
    public string DeclineText { get; private set; } = "";
    public long DownloadTotalBytes { get; private set; }
    public long DownloadedBytes { get; private set; }

    /// <summary>Frames that were malformed, not for a client, or not for the current offer. For diagnostics.</summary>
    public int IgnoredFrames { get; private set; }
    /// <summary>Server-to-mod messages dropped by the rate limit or the queue cap.</summary>
    public int DroppedEvents { get; private set; }

    /// <summary>
    /// <c>cl_allow_mods</c>, live. Switching it off stops the current mod and declines the offer;
    /// switching it on does not revive an offer that was declined unread - the next one is considered.
    /// </summary>
    public bool AllowMods
    {
        get => _allowMods;
        set
        {
            if (_allowMods == value) return;
            _allowMods = value;
            if (!value && State is not (ModClientState.Idle or ModClientState.Declined))
                Decline(ModDeclineReason.ModsDisabled, "mods were switched off");
        }
    }

    // ---- input from the network --------------------------------------------------------------------

    /// <summary>Handles one frame from the server. Never throws, whatever the bytes are.</summary>
    public void HandleFrame(ReadOnlySpan<byte> frame, double now)
    {
        if (!ModWire.TryParse(frame, out ModFrame parsed))
        {
            IgnoredFrames++;
            return;
        }

        try
        {
            switch (parsed.Kind)
            {
                case ModFrameKind.Offer: HandleOffer(parsed, now); break;
                case ModFrameKind.Chunk: HandleChunk(parsed, now); break;
                case ModFrameKind.Abort: HandleAbort(parsed); break;
                case ModFrameKind.ToClient: HandleToClient(parsed, now); break;
                default: IgnoredFrames++; break; // a client-to-server kind, sent the wrong way
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ModManifestException)
        {
            // The cache directory could not be read or written. Same outcome as any other failure.
            Decline(ModDeclineReason.DownloadFailed, "the mod cache is not usable");
        }
    }

    private void HandleOffer(in ModFrame frame, double now)
    {
        // Whatever was offered before is over: a new offer replaces it (a level change sends one).
        StopCurrent();
        _sequence = frame.Sequence;
        Manifest = null;
        ManifestSha256 = null;
        State = ModClientState.Idle;

        // The order below is the order of how much of the server's input each step looks at.
        if (!_allowMods) { Decline(ModDeclineReason.ModsDisabled, null); return; }
        if (!_options.SandboxAvailable) { Decline(ModDeclineReason.Unsupported, null); return; }
        if (!_offerRate.TryConsume(0, now)) { Decline(ModDeclineReason.Busy, null); return; }
        if (frame.Version != ModWire.TransferVersion)
        {
            Decline(ModDeclineReason.UnsupportedTransferVersion, $"this client speaks transfer version {ModWire.TransferVersion}");
            return;
        }

        ModManifest manifest;
        try { manifest = ModManifest.Parse(frame.Body, _options.LimitCeiling); }
        catch (ModManifestException e)
        {
            Decline(e.UnsupportedAbi ? ModDeclineReason.UnsupportedAbi : ModDeclineReason.BadManifest,
                e.UnsupportedAbi ? $"this client provides {ModAbi.ImportModule}" : null);
            return;
        }

        Manifest = manifest;
        ManifestSha256 = ModManifest.HashOf(frame.Body);

        if (_options.BaseProtocol is { } protocol && manifest.BaseProtocol != protocol)
        {
            Decline(ModDeclineReason.ProtocolMismatch, null);
            return;
        }
        if (_deniedThisConnection) { Decline(ModDeclineReason.ConsentDenied, null); return; }

        IReadOnlyList<ModArtifact> missing = _cache.Missing(manifest);
        long missingBytes = 0;
        foreach (ModArtifact artifact in missing) missingBytes += artifact.SizeBytes;
        if (missingBytes > _options.MaxDownloadBytes)
        {
            Decline(ModDeclineReason.TooLarge, $"the download is {missingBytes} bytes; this client allows {_options.MaxDownloadBytes}");
            return;
        }

        if (_allowedThisConnection.Contains(ManifestSha256)) { Proceed(now); return; }
        switch (_consent.Lookup(_options.ServerKey, ManifestSha256))
        {
            case ModConsentAnswer.Deny:
                Decline(ModDeclineReason.ConsentDenied, null);
                break;
            case ModConsentAnswer.Allow:
                Proceed(now);
                break;
            default:
                PendingConsent = new ModConsentRequest
                {
                    Manifest = manifest,
                    ManifestSha256 = ManifestSha256,
                    ServerKey = ModConsentStore.NormalizeServer(_options.ServerKey),
                    DownloadBytes = missingBytes,
                    DownloadFiles = missing.Count,
                    Limits = manifest.EffectiveLimits(_options.LimitCeiling),
                };
                State = ModClientState.AwaitingConsent;
                break;
        }
    }

    private void HandleChunk(in ModFrame frame, double now)
    {
        if (State != ModClientState.Downloading || frame.Sequence != _sequence)
        {
            IgnoredFrames++;
            return;
        }

        // The channel is reliable and ordered, so the only honest chunk is the next piece of the file
        // being received. Anything else - another file, a gap, a repeat - ends the download.
        ModArtifact artifact = _needed[_slot];
        _writer ??= _cache.BeginStore(artifact);
        if (frame.Slot != _slot || frame.Offset != _writer.BytesWritten)
        {
            Decline(ModDeclineReason.DownloadFailed, "a file arrived out of order");
            return;
        }
        if (frame.Body.Length > artifact.SizeBytes - _writer.BytesWritten)
        {
            Decline(ModDeclineReason.DownloadFailed, "a file is larger than its manifest declares");
            return;
        }

        _writer.Append(frame.Body);
        DownloadedBytes += frame.Body.Length;
        _lastProgress = now;
        if (!_writer.IsComplete) return;

        ModCacheWriter finished = _writer;
        _writer = null;
        try { finished.Commit(); }
        catch (ModManifestException)
        {
            Decline(ModDeclineReason.DownloadFailed, "a file does not match its SHA-256");
            return;
        }
        finally { finished.Dispose(); }

        if (++_slot < _needed.Count) return;

        // Everything arrived. Make room if the cache is over its budget, never at this mod's expense.
        _cache.Evict(Manifest!.Artifacts.Select(a => a.Sha256));
        FinishPlan();
    }

    private void HandleAbort(in ModFrame frame)
    {
        if (frame.Sequence != _sequence || State is ModClientState.Idle or ModClientState.Declined)
        {
            IgnoredFrames++;
            return;
        }
        StopCurrent();
        State = ModClientState.Idle;
        DeclineText = ModWire.SafeText(frame.Body);
    }

    private void HandleToClient(in ModFrame frame, double now)
    {
        // Negative ids are the host's own events (console commands); a server may not forge one.
        if (State != ModClientState.Active || frame.Sequence != _sequence || frame.EventId < 0
            || Manifest is null || !Manifest.HasCapability(ModCapabilities.Net))
        {
            IgnoredFrames++;
            return;
        }
        // The queue is checked first so that a message with nowhere to go does not also spend the budget.
        if (_events.Count >= _options.MaxQueuedEvents
            || _queuedEventBytes + frame.Body.Length > _options.MaxQueuedEventBytes
            || !_toClientRate.TryConsume(frame.Body.Length, now))
        {
            DroppedEvents++;
            return;
        }
        _events.Enqueue((frame.EventId, frame.Body.ToArray()));
        _queuedEventBytes += frame.Body.Length;
    }

    // ---- input from the player and the host --------------------------------------------------------

    /// <summary>The player's answer to <see cref="PendingConsent"/>. Ignored when nothing is pending.</summary>
    public void ResolveConsent(ModConsentDecision decision, double now)
    {
        if (State != ModClientState.AwaitingConsent || PendingConsent is null || ManifestSha256 is null) return;
        _consent.Record(_options.ServerKey, ManifestSha256, decision, Manifest?.ModId);

        if (decision is ModConsentDecision.AllowOnce or ModConsentDecision.AllowOnThisServer or ModConsentDecision.AllowEverywhere)
        {
            _allowedThisConnection.Add(ManifestSha256);
            try { Proceed(now); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ModManifestException)
            {
                Decline(ModDeclineReason.DownloadFailed, "the mod cache is not usable");
            }
        }
        else
        {
            // Any refusal covers the rest of this connection, so re-offering cannot be used to nag.
            _deniedThisConnection = true;
            Decline(ModDeclineReason.ConsentDenied, null);
        }
    }

    /// <summary>Abandons a consent prompt or a download in progress (the player pressed cancel).</summary>
    public void Cancel()
    {
        if (State is ModClientState.AwaitingConsent or ModClientState.Downloading)
            Decline(ModDeclineReason.Cancelled, null);
    }

    /// <summary>
    /// The player wants this server's mod gone: stops it, tells the server, and declines whatever else
    /// the server offers until the connection ends. Nothing is remembered beyond that.
    /// </summary>
    public void StopForConnection()
    {
        _deniedThisConnection = true;
        if (State is not (ModClientState.Idle or ModClientState.Declined))
            Decline(ModDeclineReason.Cancelled, "stopped by the player");
    }

    /// <summary>The host loaded and started <see cref="Plan"/>. Tells the server the mod is running.</summary>
    public void ReportLoaded()
    {
        if (State != ModClientState.ReadyToLoad) return;
        State = ModClientState.Active;
        _toServerRate = NewToServerRate();
        _toClientRate = NewToClientRate();
        _outbound.Enqueue(ModWire.Ready(_sequence));
    }

    /// <summary>
    /// The host could not load the plan, or the running mod was disabled. <paramref name="text"/> comes
    /// from the sandbox and may quote the guest, so it is cut to a short printable line before it is sent.
    /// </summary>
    public void ReportFailure(ModDeclineReason reason, string? text)
    {
        if (State is not (ModClientState.ReadyToLoad or ModClientState.Active)) return;
        Decline(reason is ModDeclineReason.LoadFailed or ModDeclineReason.Faulted ? reason : ModDeclineReason.Faulted, text);
    }

    /// <summary>Checks the download against its time caps. Call regularly with the same clock as <see cref="HandleFrame"/>.</summary>
    public void Update(double now)
    {
        if (State != ModClientState.Downloading || double.IsNaN(now)) return;
        if (!_clockSet)
        {
            _downloadStarted = _lastProgress = now;
            _clockSet = true;
            return;
        }
        if (now - _downloadStarted > _options.DownloadTimeoutSeconds)
            Decline(ModDeclineReason.Timeout, "the download took too long");
        else if (now - _lastProgress > _options.StallTimeoutSeconds)
            Decline(ModDeclineReason.Timeout, "the download stalled");
    }

    /// <summary>
    /// The level changed: the running mod stops, as a fresh one is started per match. Answers given
    /// during this connection are kept, so the server's next offer of the same mod does not ask again.
    /// </summary>
    public void LevelChanged()
    {
        StopCurrent();
        State = ModClientState.Idle;
    }

    /// <summary>The connection ended. Everything about it is forgotten, including "allow once".</summary>
    public void Disconnected()
    {
        StopCurrent();
        State = ModClientState.Idle;
        Manifest = null;
        ManifestSha256 = null;
        DeclineReason = ModDeclineReason.None;
        DeclineText = "";
        _outbound.Clear();
        _allowedThisConnection.Clear();
        _deniedThisConnection = false;
        _offerRate = NewOfferRate();
        IgnoredFrames = DroppedEvents = 0;
    }

    public void Dispose() => StopCurrent();

    // ---- the mod channel ---------------------------------------------------------------------------

    /// <summary>
    /// Queues a message from the mod to the server. False - and nothing queued - unless the mod is
    /// running, declared the <see cref="ModCapabilities.Net"/> capability, and is inside its size and
    /// rate limits. This is what <see cref="IModHost.SendToServer"/> ends in.
    /// </summary>
    public bool TrySendToServer(ReadOnlySpan<byte> payload, double now)
    {
        if (State != ModClientState.Active || Manifest is null || !Manifest.HasCapability(ModCapabilities.Net)) return false;
        if (payload.Length > ModWire.MaxToServerBytes || _outbound.Count >= MaxOutboundFrames) return false;
        if (!_toServerRate.TryConsume(payload.Length, now)) return false;
        _outbound.Enqueue(ModWire.ToServer(_sequence, payload));
        return true;
    }

    /// <summary>The next server-to-mod message waiting to be delivered to the running mod.</summary>
    public bool TryDequeueEvent(out int eventId, out byte[] payload)
    {
        if (State == ModClientState.Active && _events.TryDequeue(out (int Id, byte[] Payload) next))
        {
            _queuedEventBytes -= next.Payload.Length;
            (eventId, payload) = next;
            return true;
        }
        eventId = 0;
        payload = Array.Empty<byte>();
        return false;
    }

    /// <summary>The next frame to send to the server on the reliable channel.</summary>
    public bool TryDequeueOutbound(out byte[] frame) => _outbound.TryDequeue(out frame!);

    // ------------------------------------------------------------------------------------------------

    private void Proceed(double now)
    {
        PendingConsent = null;
        // Looked up again rather than reused from the offer: the player may have taken a while.
        _needed = _cache.Missing(Manifest!);
        if (_needed.Count == 0)
        {
            FinishPlan();
            return;
        }

        DownloadTotalBytes = 0;
        foreach (ModArtifact artifact in _needed) DownloadTotalBytes += artifact.SizeBytes;
        if (DownloadTotalBytes > _options.MaxDownloadBytes)
        {
            Decline(ModDeclineReason.TooLarge, null);
            return;
        }
        DownloadedBytes = 0;
        _slot = 0;
        _downloadStarted = _lastProgress = now;
        _clockSet = !double.IsNaN(now);
        State = ModClientState.Downloading;

        List<string> hashes = new(_needed.Count);
        foreach (ModArtifact artifact in _needed) hashes.Add(artifact.Sha256);
        _outbound.Enqueue(ModWire.Need(_sequence, hashes));
    }

    private void FinishPlan()
    {
        ModManifest manifest = Manifest!;
        string? modulePath = null;
        if (manifest.ClientModule is not null && !_cache.TryGetPath(manifest.ClientModule, out modulePath))
        {
            Decline(ModDeclineReason.DownloadFailed, "a file is missing from the mod cache");
            return;
        }
        List<(ModArtifact, string)> packs = new(manifest.AssetPacks.Count);
        foreach (ModArtifact pack in manifest.AssetPacks)
        {
            if (!_cache.TryGetPath(pack, out string path))
            {
                Decline(ModDeclineReason.DownloadFailed, "a file is missing from the mod cache");
                return;
            }
            packs.Add((pack, path));
        }

        Plan = new ModLoadPlan
        {
            Manifest = manifest,
            ManifestSha256 = ManifestSha256!,
            ModulePath = modulePath,
            Packs = packs,
            Limits = manifest.EffectiveLimits(_options.LimitCeiling),
        };
        State = ModClientState.ReadyToLoad;
    }

    private void Decline(ModDeclineReason reason, string? text)
    {
        StopCurrent();
        State = ModClientState.Declined;
        DeclineReason = reason;
        DeclineText = ModWire.SafeText(System.Text.Encoding.UTF8.GetBytes(text ?? ""));
        // One reply per offer at most, so a caller that drains the queue never sees it grow; the cap is
        // for a caller that does not.
        if (_outbound.Count < MaxOutboundFrames * 2) _outbound.Enqueue(ModWire.Decline(_sequence, reason, DeclineText));
    }

    /// <summary>Stops whatever the current offer had got to: prompt, download or running mod. Sends nothing.</summary>
    private void StopCurrent()
    {
        PendingConsent = null;
        Plan = null;
        _writer?.Dispose();
        _writer = null;
        _needed = Array.Empty<ModArtifact>();
        _slot = 0;
        _events.Clear();
        _queuedEventBytes = 0;
        DownloadedBytes = DownloadTotalBytes = 0;
        DeclineReason = ModDeclineReason.None;
        DeclineText = "";
    }

    private ModRateLimiter NewOfferRate() => new(_options.OffersPerSecond, _options.OfferBurst);
    private ModRateLimiter NewToServerRate() => new(_options.ToServerPerSecond, _options.ToServerBurst, _options.ToServerBytesPerSecond, _options.ToServerBurstBytes);
    private ModRateLimiter NewToClientRate() => new(_options.ToClientPerSecond, _options.ToClientBurst);
}

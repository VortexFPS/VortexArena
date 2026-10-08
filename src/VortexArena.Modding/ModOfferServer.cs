using System.Security.Cryptography;

namespace VortexArena.Modding;

/// <summary>What a server operator states about the mod; the file names, sizes and hashes are worked out from the files.</summary>
public sealed record ModOfferDescription
{
    public string ModId { get; init; } = "";
    public string ModVersion { get; init; } = "";
    public uint BaseProtocol { get; init; }
    public ModConsent Consent { get; init; } = new();
    public ModLimitRequest Limits { get; init; } = new();
    /// <summary>True to close the connection of any client that does not end up running the mod.</summary>
    public bool Required { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A server's mod, prepared once and offered to every client: the manifest exactly as it will be sent,
/// its hash, and where each file's bytes come from. Immutable and safe to share between connections.
/// </summary>
public sealed class ModOffer
{
    private readonly Dictionary<string, (ModArtifact Artifact, Func<Stream> Open)> _sources;

    public ModManifest Manifest { get; }
    public byte[] ManifestJson { get; }
    public string ManifestSha256 { get; }

    private ModOffer(ModManifest manifest, byte[] json, Dictionary<string, (ModArtifact, Func<Stream>)> sources)
    {
        Manifest = manifest;
        ManifestJson = json;
        ManifestSha256 = ModManifest.HashOf(json);
        _sources = sources;
    }

    /// <summary>
    /// Builds an offer from files on the server's disk: hashes each one and writes the manifest. The
    /// result is checked with the same strict parser clients use, so a mod no client would accept is
    /// reported here, at start-up, rather than by every player who connects.
    /// </summary>
    /// <exception cref="ModManifestException">The description or a file breaks a manifest rule.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static ModOffer FromFiles(ModOfferDescription description, string? modulePath, IReadOnlyList<string>? packPaths = null)
    {
        List<(string, Func<Stream>)> packs = new();
        foreach (string path in packPaths ?? Array.Empty<string>())
        {
            string full = Path.GetFullPath(path);
            packs.Add((Path.GetFileName(full), () => OpenRead(full)));
        }
        string? moduleFull = modulePath is null ? null : Path.GetFullPath(modulePath);
        return Build(description, moduleFull is null ? null : (Path.GetFileName(moduleFull), () => OpenRead(moduleFull)), packs);

        static Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>Builds an offer from bytes already in memory (tests, generated content).</summary>
    public static ModOffer FromMemory(ModOfferDescription description, (string Name, byte[] Bytes)? module, IReadOnlyList<(string Name, byte[] Bytes)>? packs = null)
    {
        List<(string, Func<Stream>)> packSources = new();
        foreach ((string name, byte[] bytes) in packs ?? Array.Empty<(string, byte[])>())
            packSources.Add((name, () => new MemoryStream(bytes, writable: false)));
        (string, Func<Stream>)? moduleSource = module is { } m ? (m.Name, () => new MemoryStream(m.Bytes, writable: false)) : null;
        return Build(description, moduleSource, packSources);
    }

    private static ModOffer Build(ModOfferDescription description, (string Name, Func<Stream> Open)? module, List<(string Name, Func<Stream> Open)> packs)
    {
        Dictionary<string, (ModArtifact, Func<Stream>)> sources = new(StringComparer.Ordinal);

        ModArtifact Describe(string name, Func<Stream> open)
        {
            using Stream stream = open();
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                size += read;
            }
            ModArtifact artifact = new() { Name = name, SizeBytes = size, Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() };
            sources.TryAdd(artifact.Sha256, (artifact, open));
            return artifact;
        }

        ModManifest manifest = new()
        {
            ModId = description.ModId,
            ModVersion = description.ModVersion,
            BaseProtocol = description.BaseProtocol,
            Consent = description.Consent,
            Limits = description.Limits,
            Required = description.Required,
            Capabilities = description.Capabilities,
            ClientModule = module is { } m ? Describe(m.Name, m.Open) : null,
            AssetPacks = packs.Select(p => Describe(p.Name, p.Open)).ToArray(),
        };
        byte[] json = manifest.ToJson();
        return new ModOffer(ModManifest.Parse(json), json, sources);
    }

    internal bool TryGetSource(string sha256, out ModArtifact artifact, out Func<Stream> open)
    {
        if (_sources.TryGetValue(sha256, out (ModArtifact Artifact, Func<Stream> Open) source))
        {
            (artifact, open) = source;
            return true;
        }
        artifact = null!;
        open = null!;
        return false;
    }
}

public enum ModPeerState
{
    /// <summary>The offer was sent; the client has not answered (it may be asking its player).</summary>
    Offered,
    /// <summary>The client asked for files and they are being sent.</summary>
    Sending,
    /// <summary>Every requested file was sent; waiting for the client to verify, load and report.</summary>
    AwaitingReady,
    /// <summary>The client is running the mod. The mod channel is open.</summary>
    Ready,
    /// <summary>The client said it will not run the mod. It plays with stock presentation unless the mod is required.</summary>
    Declined,
    /// <summary>The exchange broke: a protocol violation, a timeout, or a file the server could not read.</summary>
    Failed,
}

public sealed record ModPeerOptions
{
    /// <summary>How long a client may take to answer an offer. Long, because a person may be reading a prompt.</summary>
    public double AnswerTimeoutSeconds { get; init; } = 180;
    /// <summary>How long a client may take from its first answer to running the mod.</summary>
    public double CompleteTimeoutSeconds { get; init; } = 900;
    /// <summary>Upload rate to one client, so a download cannot crowd gameplay off the connection.</summary>
    public double UploadBytesPerSecond { get; init; } = 512 * 1024;
    public double UploadBurstBytes { get; init; } = 128 * 1024;
    /// <summary>Mod-to-server messages accepted from one client; twice the client's own default limit, so an honest client is never cut.</summary>
    public double ToServerPerSecond { get; init; } = 40;
    public double ToServerBurst { get; init; } = 80;
    public double ToServerBytesPerSecond { get; init; } = 16 * 1024;
    public double ToServerBurstBytes { get; init; } = 32 * 1024;
    public int MaxQueuedMessages { get; init; } = 64;
    public int MaxQueuedOutbound { get; init; } = 256;
}

/// <summary>
/// The server's half of the mod-offer protocol for ONE connected client. The server creates it when a
/// client has passed the handshake, sends what it queues on the reliable channel, feeds it the mod
/// frames that client sends, and asks it two questions: <see cref="MayPlay"/> and
/// <see cref="ShouldDisconnect"/>.
///
/// A client is as untrusted here as a server is on the other side. It can only ever be sent files the
/// manifest lists, each at most once per offer, at a capped rate; anything it sends that the protocol
/// does not allow at that moment is a violation and ends with <see cref="ShouldDisconnect"/>; its
/// messages to the mod's server half are size- and rate-limited before the server half sees them.
///
/// A client that declines, or never answers, costs the server nothing and keeps playing with stock
/// presentation - unless the offer is marked required.
/// </summary>
public sealed class ModOfferPeer : IDisposable
{
    private readonly ModOffer _offer;
    private readonly ModPeerOptions _options;
    private readonly Queue<byte[]> _control = new();
    private readonly Queue<byte[]> _messages = new();
    private readonly byte[] _chunk = new byte[ModWire.MaxChunkBytes];
    private readonly List<(ModArtifact Artifact, Func<Stream> Open)> _requested = new();
    private ModRateLimiter _upload, _toServerRate;
    private Stream? _stream;
    private int _slot;
    private long _offset;
    private byte _sequence;
    private double _phaseStarted;

    public ModOfferPeer(ModOffer offer, double now, ModPeerOptions? options = null)
    {
        _offer = offer ?? throw new ArgumentNullException(nameof(offer));
        _options = options ?? new ModPeerOptions();
        _upload = NewUploadRate();
        _toServerRate = NewToServerRate();
        Reoffer(now);
    }

    public ModPeerState State { get; private set; }
    public ModDeclineReason DeclineReason { get; private set; }
    /// <summary>The client's own explanation, already made safe to print.</summary>
    public string DeclineText { get; private set; } = "";
    /// <summary>True when the server should close this connection; <see cref="DisconnectReason"/> says why.</summary>
    public bool ShouldDisconnect { get; private set; }
    public string? DisconnectReason { get; private set; }
    public long BytesSent { get; private set; }
    /// <summary>Mod-to-server messages dropped by the rate limit or the queue cap.</summary>
    public int DroppedMessages { get; private set; }

    /// <summary>
    /// Whether this client may take part in the game as far as the mod is concerned: always, for an
    /// optional mod; only once it is running the mod, for a required one.
    /// </summary>
    public bool MayPlay => !ShouldDisconnect && (!_offer.Manifest.Required || State == ModPeerState.Ready);

    /// <summary>
    /// Sends the offer again with a new sequence number, abandoning whatever the previous one had got
    /// to. Called on a level change: clients stop their mod there and start a fresh one from this.
    /// </summary>
    public void Reoffer(double now)
    {
        if (ShouldDisconnect) return;
        CloseStream();
        _requested.Clear();
        _messages.Clear();
        _control.Clear();
        _sequence = _sequence == byte.MaxValue ? (byte)1 : (byte)(_sequence + 1);
        State = ModPeerState.Offered;
        DeclineReason = ModDeclineReason.None;
        DeclineText = "";
        _phaseStarted = now;
        _upload = NewUploadRate();
        _toServerRate = NewToServerRate();
        _control.Enqueue(ModWire.Offer(_sequence, _offer.ManifestJson));
    }

    /// <summary>Handles one mod frame from this client. Never throws, whatever the bytes are.</summary>
    public void HandleFrame(ReadOnlySpan<byte> frame, double now)
    {
        if (ShouldDisconnect) return;
        if (!ModWire.TryParse(frame, out ModFrame parsed))
        {
            Violation("a malformed frame");
            return;
        }
        // A frame about an earlier offer was in flight when the new one went out. Not the client's fault.
        if (parsed.Sequence != _sequence) return;
        // Likewise once this offer is over - declined, lapsed or withdrawn - a late answer to it (the
        // player finally pressed a button) is not an offence. Only its kind is still checked.
        if (State is ModPeerState.Declined or ModPeerState.Failed)
        {
            if (parsed.Kind is not (ModFrameKind.Need or ModFrameKind.Ready or ModFrameKind.Decline or ModFrameKind.ToServer))
                Violation("a frame only a server may send");
            return;
        }

        switch (parsed.Kind)
        {
            case ModFrameKind.Need: HandleNeed(parsed, now); break;
            case ModFrameKind.Ready: HandleReady(); break;
            case ModFrameKind.Decline: HandleDecline(parsed); break;
            case ModFrameKind.ToServer: HandleToServer(parsed, now); break;
            default: Violation("a frame only a server may send"); break;
        }
    }

    private void HandleNeed(in ModFrame frame, double now)
    {
        if (State != ModPeerState.Offered)
        {
            Violation("a second request for files");
            return;
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < frame.HashCount; i++)
        {
            string sha = ModWire.HashHex(frame.Hash(i));
            if (!seen.Add(sha) || !_offer.TryGetSource(sha, out ModArtifact artifact, out Func<Stream> open))
            {
                _requested.Clear();
                Violation("a request for a file the mod does not contain");
                return;
            }
            _requested.Add((artifact, open));
        }
        _slot = 0;
        _offset = 0;
        _phaseStarted = now;
        State = ModPeerState.Sending;
    }

    private void HandleReady()
    {
        if (State is not (ModPeerState.Offered or ModPeerState.AwaitingReady))
        {
            Violation("a ready report at the wrong time");
            return;
        }
        State = ModPeerState.Ready;
    }

    private void HandleDecline(in ModFrame frame)
    {
        if (State is ModPeerState.Declined or ModPeerState.Failed) return;
        CloseStream();
        _requested.Clear();
        _messages.Clear();
        State = ModPeerState.Declined;
        DeclineReason = frame.Reason;
        DeclineText = ModWire.SafeText(frame.Body);
        if (_offer.Manifest.Required)
            Disconnect($"this server requires the mod '{_offer.Manifest.ModId}', which your client is not running ({DescribeDecline()})");
    }

    private void HandleToServer(in ModFrame frame, double now)
    {
        if (State != ModPeerState.Ready || !_offer.Manifest.HasCapability(ModCapabilities.Net))
        {
            Violation("a mod message without a running mod that may send one");
            return;
        }
        // The queue is checked first so that a message with nowhere to go does not also spend the budget.
        if (_messages.Count >= _options.MaxQueuedMessages || !_toServerRate.TryConsume(frame.Body.Length, now))
        {
            DroppedMessages++;
            return;
        }
        _messages.Enqueue(frame.Body.ToArray());
    }

    /// <summary>Applies the timeouts. Call regularly with the same clock as <see cref="HandleFrame"/>.</summary>
    public void Update(double now)
    {
        if (ShouldDisconnect || double.IsNaN(now)) return;
        double limit = State switch
        {
            ModPeerState.Offered => _options.AnswerTimeoutSeconds,
            ModPeerState.Sending or ModPeerState.AwaitingReady => _options.CompleteTimeoutSeconds,
            _ => double.PositiveInfinity,
        };
        if (now - _phaseStarted <= limit) return;

        // An optional mod's offer simply lapses. Only a required one makes silence a reason to go.
        CloseStream();
        _requested.Clear();
        State = ModPeerState.Failed;
        _control.Enqueue(ModWire.Abort(_sequence, "timed out"));
        if (_offer.Manifest.Required)
            Disconnect($"this server requires the mod '{_offer.Manifest.ModId}', and your client did not start it in time");
    }

    /// <summary>
    /// The next frame to send to this client on the reliable channel, or false when there is nothing to
    /// send right now. File pieces are produced here, one per call, as the upload rate allows - so the
    /// caller's loop is also the pacing.
    /// </summary>
    public bool TryDequeueOutbound(double now, out byte[] frame)
    {
        if (_control.TryDequeue(out frame!)) return true;
        if (State != ModPeerState.Sending || ShouldDisconnect) return false;

        (ModArtifact artifact, Func<Stream> open) = _requested[_slot];
        int want = (int)Math.Min(ModWire.MaxChunkBytes, artifact.SizeBytes - _offset);
        if (_upload.AvailableBytes(now) < want) return false;

        int read;
        try
        {
            _stream ??= open();
            read = _stream.ReadAtLeast(_chunk.AsSpan(0, want), want, throwOnEndOfStream: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            read = -1;
        }
        if (read != want)
        {
            // The file is not the one that was hashed at start-up. Sending the rest would only make the
            // client download something it must then throw away.
            CloseStream();
            _requested.Clear();
            State = ModPeerState.Failed;
            frame = ModWire.Abort(_sequence, "the server could not read one of the mod's files");
            if (_offer.Manifest.Required) Disconnect($"this server requires the mod '{_offer.Manifest.ModId}' but could not send it");
            return true;
        }

        _upload.TryConsume(want, now);
        frame = ModWire.Chunk(_sequence, (byte)_slot, (uint)_offset, _chunk.AsSpan(0, want));
        _offset += want;
        BytesSent += want;
        if (_offset == artifact.SizeBytes)
        {
            CloseStream();
            _offset = 0;
            if (++_slot == _requested.Count)
            {
                _requested.Clear();
                State = ModPeerState.AwaitingReady;
            }
        }
        return true;
    }

    /// <summary>
    /// Queues a message from the mod's server half to this client's mod. False unless that client is
    /// running the mod, the mod declared the <see cref="ModCapabilities.Net"/> capability, the id is
    /// not negative (those are reserved for the client's own events) and the payload fits.
    /// </summary>
    public bool TrySendToClient(int eventId, ReadOnlySpan<byte> payload)
    {
        if (State != ModPeerState.Ready || ShouldDisconnect || eventId < 0 || payload.Length > ModWire.MaxToClientBytes) return false;
        if (!_offer.Manifest.HasCapability(ModCapabilities.Net) || _control.Count >= _options.MaxQueuedOutbound) return false;
        _control.Enqueue(ModWire.ToClient(_sequence, eventId, payload));
        return true;
    }

    /// <summary>The next message this client's mod sent to the server half of the mod.</summary>
    public bool TryDequeueMessage(out byte[] payload) => _messages.TryDequeue(out payload!);

    public void Dispose() => CloseStream();

    // ------------------------------------------------------------------------------------------------

    private string DescribeDecline() => DeclineText.Length > 0 ? $"{DeclineReason}: {DeclineText}" : DeclineReason.ToString();

    private void Violation(string what)
    {
        CloseStream();
        _requested.Clear();
        _messages.Clear();
        State = ModPeerState.Failed;
        Disconnect($"mod protocol violation: {what}");
    }

    private void Disconnect(string reason)
    {
        if (ShouldDisconnect) return;
        ShouldDisconnect = true;
        DisconnectReason = reason;
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private ModRateLimiter NewUploadRate() =>
        new(1e9, 1e9, Math.Max(1, _options.UploadBytesPerSecond), Math.Max(ModWire.MaxChunkBytes, _options.UploadBurstBytes));

    private ModRateLimiter NewToServerRate() =>
        new(_options.ToServerPerSecond, _options.ToServerBurst, _options.ToServerBytesPerSecond, _options.ToServerBurstBytes);
}

/// <summary>
/// Every connected client's <see cref="ModOfferPeer"/>, keyed by the server's own peer id. This is the
/// whole of what a game server has to call: a peer joined, a peer left, a peer sent a mod frame, the
/// level changed, and - once a tick - <see cref="Pump"/>, which sends what is due and names the peers
/// to disconnect.
/// </summary>
public sealed class ModOfferHub : IDisposable
{
    private readonly Dictionary<int, ModOfferPeer> _peers = new();
    private readonly List<int> _scratch = new();
    private readonly ModPeerOptions _options;

    public ModOfferHub(ModOffer offer, ModPeerOptions? options = null)
    {
        Offer = offer ?? throw new ArgumentNullException(nameof(offer));
        _options = options ?? new ModPeerOptions();
    }

    public ModOffer Offer { get; }
    public int PeerCount => _peers.Count;

    /// <summary>Most frames sent to one peer in one <see cref="Pump"/>, so one download cannot stall a tick.</summary>
    public int MaxFramesPerPeerPerPump { get; init; } = 16;

    /// <summary>A client passed the handshake: offer it the mod. A second call for the same id starts over.</summary>
    public void PeerJoined(int peerId, double now)
    {
        PeerLeft(peerId);
        _peers[peerId] = new ModOfferPeer(Offer, now, _options);
    }

    public void PeerLeft(int peerId)
    {
        if (_peers.Remove(peerId, out ModOfferPeer? peer)) peer.Dispose();
    }

    /// <summary>A mod frame arrived from <paramref name="peerId"/>. Frames from an unknown peer are dropped.</summary>
    public void HandleFrame(int peerId, ReadOnlySpan<byte> frame, double now)
    {
        if (_peers.TryGetValue(peerId, out ModOfferPeer? peer)) peer.HandleFrame(frame, now);
    }

    /// <summary>The level changed: every client stops its mod, so every client is offered it again.</summary>
    public void LevelChanged(double now)
    {
        foreach (ModOfferPeer peer in _peers.Values) peer.Reoffer(now);
    }

    public ModOfferPeer? Peer(int peerId) => _peers.GetValueOrDefault(peerId);

    /// <summary>True when the peer may take part in the game as far as the mod is concerned. Unknown peers may not.</summary>
    public bool MayPlay(int peerId) => _peers.TryGetValue(peerId, out ModOfferPeer? peer) && peer.MayPlay;

    /// <summary>
    /// Applies timeouts, sends what is due through <paramref name="sendReliable"/>, and reports through
    /// <paramref name="disconnect"/> each peer that should be dropped (which is then forgotten here).
    /// </summary>
    public void Pump(double now, Action<int, byte[]> sendReliable, Action<int, string> disconnect)
    {
        _scratch.Clear();
        foreach ((int id, ModOfferPeer peer) in _peers)
        {
            peer.Update(now);
            for (int sent = 0; sent < MaxFramesPerPeerPerPump && peer.TryDequeueOutbound(now, out byte[] frame); sent++)
                sendReliable(id, frame);
            if (peer.ShouldDisconnect) _scratch.Add(id);
        }
        foreach (int id in _scratch)
        {
            string reason = _peers[id].DisconnectReason ?? "mod";
            PeerLeft(id);
            disconnect(id, reason);
        }
    }

    public void Dispose()
    {
        foreach (ModOfferPeer peer in _peers.Values) peer.Dispose();
        _peers.Clear();
    }
}

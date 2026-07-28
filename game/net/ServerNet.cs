using System;
using System.Collections.Generic;
using System.Net;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;
using XonoticGodot.Server;
using NVec3 = System.Numerics.Vector3;
using BitWriter = XonoticGodot.Net.BitWriter;
using BitReader = XonoticGodot.Net.BitReader;
// [W14b LI1] alias the animdecide unit — the `Player` class in XonoticGodot.Common.Gameplay (imported above) shadows
// the `...Gameplay.Player` namespace, so `Player.AnimDecide` would bind to the class. The alias resolves the unit.
using AnimDecideUnit = XonoticGodot.Common.Gameplay.AnimDecide;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The authoritative server's network driver — the glue that turns a headless <see cref="GameWorld"/> into a
/// networked match. It owns a <see cref="NetTransport.Server"/>, maps each connected ENet peer to a
/// <see cref="Player"/> on the world's <see cref="ClientManager"/>, feeds received <see cref="InputCommand"/>s
/// into the world's per-player movement, advances the simulation, and broadcasts a per-tick snapshot of the
/// relevant entity state back to every client. It also installs the <see cref="EffectEmitter"/> /
/// <see cref="NotificationSystem"/> NET sinks so server-emitted effects and notifications are serialized and
/// broadcast (resolving the "wire to netcode" TODOs in those systems).
///
/// Drive it by calling <see cref="Tick"/> once per host frame with the real elapsed time:
/// <list type="number">
///   <item>poll the transport (dispatches handshakes + queued input packets);</item>
///   <item>advance the <see cref="GameWorld"/> by the elapsed time (the world pulls each client's queued
///         input through <see cref="GameWorld.InputProvider"/>, runs authoritative movement, fires
///         effect/notification emissions which our sinks capture);</item>
///   <item>broadcast a snapshot to each client (owner-replicated full-precision state for prediction + the
///         quantized state of every other entity for interpolation), then flush the captured event bundles.</item>
/// </list>
///
/// Lag-compensation rewind (antilag.qc) is wired: each frame this records the position history of every player,
/// monster and nade (<see cref="BuildEntitySet"/> / <see cref="RecordAntilagEntities"/>), and at hitscan/projectile
/// fire it rewinds them all to the shooter's MEASURED view-time and restores them after the trace
/// (<see cref="BeginLagComp"/>/<see cref="EndLagComp"/>). Delta-compression of the snapshot is also in place
/// (<see cref="ServerSnapshotHistory"/>).
/// </summary>
public sealed class ServerNet : IDisposable
{
    private readonly NetTransport.Server _transport;
    private readonly GameWorld _world;
    private readonly string _serverName;

    // S5 (sv_threaded, DEFAULT ON since 2026-07-11): the cross-thread serialisation gate for the listen/host worker-thread path.
    // NULL by default (single-threaded) — when null Tick runs with NO lock, byte-for-byte as today. The host
    // installs a shared lock object here AND takes the SAME object around the main thread's prediction span in
    // NetGame._Process; the ServerThread worker's Tick then locks it, so the sim and the main-thread prediction
    // never touch the shared world concurrently. Set ONCE at StartListenServer when sv_threaded is on.
    private object? _simGate;

    /// <summary>S5: install (or clear) the cross-thread serialisation gate. Set by the host when sv_threaded is on,
    /// BEFORE the ServerThread starts; the SAME object is taken by the main thread around its prediction span.</summary>
    public object? SimGate { get => _simGate; set => _simGate = value; }

    // S5: main→server one-way command handoff. When threaded, console/menu sinks (chat / bot_add / bot_remove /
    // changelevel) fire on the MAIN thread but must run on the worker that owns the world — they're enqueued here
    // and drained at the top of Tick (under the gate). When NOT threaded (_simGate == null) Enqueue runs the
    // action inline immediately, so the single-threaded path keeps its exact synchronous behavior.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _inbound = new();

    /// <summary>S5: run <paramref name="action"/> on the sim thread. Threaded: enqueue for the worker to drain at the
    /// next tick top (under the gate). Single-threaded: invoke inline now (unchanged synchronous behavior).</summary>
    public void RunOnSimThread(Action action)
    {
        if (_simGate is null) action();
        else _inbound.Enqueue(action);
    }

    /// <summary>The transport's connection cap — the browser's slots fallback when g_maxplayers is unset.</summary>
    private int _maxClients = 32;

    // Reused writers (the networking spec's allocation discipline: one writer per send path, reset+refilled).
    private readonly BitWriter _snapshotWriter = new(2048);
    private readonly BitWriter _eventWriter = new(1024);
    private readonly BitWriter _reliableWriter = new(1024);
    private readonly BitWriter _scratchWriter = new(512);

    /// <summary>Reusable scratch for the per-peer personalized GametypeStatusBlock (T53) — serialized first so
    /// it can be hash-gated before deciding to copy it into the snapshot.</summary>
    private readonly BitWriter _modeStatusScratch = new(256);

    private readonly Dictionary<int, PeerState> _peers = new();      // by Godot peer id
    private readonly Dictionary<Player, PeerState> _byPlayer = new(ReferenceEqualityComparer.Instance);

    /// <summary>Number of connected ENet peers (incl. not-yet-handshaked ones). On a listen server the local
    /// host is one peer, so <c>&gt; 1</c> means at least one REMOTE client is connected. Backed by
    /// <see cref="_livePeerIds"/>, which only the transport-polling thread (always MAIN) touches — the
    /// <c>_peers</c> table this used to count is mutated by the sim WORKER since the WS1 stage-2 inbound
    /// marshaling, so counting it here was a cross-thread read of a plain Dictionary.</summary>
    public int ConnectedPeerCount => _livePeerIds.Count;

    /// <summary>Fixed sim ticks the last <c>PumpTransport</c> ran (0 when rendering faster than 72 Hz, up to
    /// MaxTicksPerFrame on catch-up). Read by the <c>cl_motion_trace</c> diagnostic (r16 rubberband hunt).</summary>
    public int LastTicksRan { get; private set; }

    // --- snapshot delta-compression + movevar replication + teleport detection ---
    private ushort _snapshotSeq;                                     // global snapshot sequence (clients ack it)
    private readonly Dictionary<int, NetEntityState> _entityScratch = new(); // reused per-tick entity set
    // Set during BuildEntitySet when any player carries owner-private Feedback-block state; forces the
    // per-recipient pass in RelevantEntitiesFor to run so that state is stripped from other players' copies.
    private bool _scratchHasOwnerPrivate;
    // (§12.8) DP-faithful per-client PVS entity culling (sv_cullentities_pvs). _entityBounds holds each networked
    // entity's world-space bounds (filled in BuildEntitySet); _relevantScratch is the per-recipient filtered view
    // of _entityScratch fed to EncodeSnapshot — the delta encoder then turns "no longer relevant" into a removal
    // and "relevant again" into a spawn against THIS client's baseline, exactly like DP's per-client entity frame.
    private readonly Dictionary<int, (NVec3 Min, NVec3 Max)> _entityBounds = new();
    private readonly Dictionary<int, NetEntityState> _relevantScratch = new();

    // Per-recipient viewpoint clusters for the PVS entity cull: the viewer's own cluster plus the exit cluster
    // of every linked warpzone whose window the viewer can see (see RelevantEntitiesFor's warpzone union).
    private readonly List<int> _viewClustersScratch = new();
    // Memoized "<classname> <netname>" catalog keys for model-less projectiles so BuildEntitySet doesn't
    // allocate a fresh interpolated string per projectile per tick-frame (3.2-4). Keyed by (classname, netname)
    // — both stable per projectile — so the set of distinct entries is tiny (one per projectile type).
    private readonly Dictionary<(string, string), string> _projKeyCache = new();

    // Small stable per-player net ids (humans AND bots), decoupled from the large/random ENet peer id and kept
    // below EntityNetBase so the player and non-player id spaces never collide.
    // WS1 stage 3: ConcurrentDictionary — NetGame.LocalServerPlayer resolves PlayerByNetId from the RENDER
    // thread while the sim worker assigns/removes ids on join/leave; a plain Dictionary enumerated mid-resize
    // is undefined. Single logical writer (the sim thread); render-side readers are enumeration/TryGetValue.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Player, int> _playerNetIds = new(ReferenceEqualityComparer.Instance);
    private int _nextPlayerNetId = 1;
    private float[] _moveVars = System.Array.Empty<float>();         // this tick's movement cvars
    private uint _moveVarsHash;

    // this tick's scoreboard snapshot (built once, broadcast to all; per-client gated by _scoreVersion).
    private readonly List<XonoticGodot.Net.ScoreRowWire> _scoreRows = new();
    // #22: last roster/status signature — a change bumps the score version so the row set re-sends on join/leave.
    private long _lastRosterSig = long.MinValue;
    private readonly List<(int team, int score)> _scoreTeams = new();
    // QC the race/CTS rankings table (Scoreboard_Rankings_Draw): best-first (time, holder) for this map,
    // captured from the persistent RaceRecords DB; empty in non-race modes.
    private readonly List<(int timeEncoded, string holder)> _scoreRankings = new();
    // QC the CTS/race speed award (race_send_speedaward / _best): the round-best + persisted all-time best planar
    // speed (qu/s, rounded) + holder names, captured from the live Cts gametype; zeroed in non-CTS modes.
    private (int speed, string holder, int best, string bestHolder) _scoreSpeedAward;
    private int _scoreVersion;
    // this tick's score-LAYOUT generation (the active label/flag set + gametype/teamplay): the ScoreInfo block
    // (QC ENT_CLIENT_SCORES_INFO) is sent per-client only when this changed since they last got it (a mode
    // switch). Distinct from _scoreVersion, which gates the per-VALUE scoreboard block.
    private int _scoreInfoGen;
    private uint _scoreInfoHash;
    private readonly Dictionary<Player, NVec3> _lastSnapOrigin = new(ReferenceEqualityComparer.Instance);

    // [A5 #3/#7] ENT_CLIENT_STATUSEFFECTS: the last full status-effect bitmap encoded for each player (or null when
    // the player has no networked effects). StatusEffectsCatalog.Flush(p) re-encodes a player ONLY on the tick its
    // effects change (QC SendFlags); this side-table holds the CURRENT blob between those ticks so every snapshot
    // carries the authoritative state — without it a late joiner (or a client deltaing against an old baseline)
    // would never receive an effect applied before it connected. Cleared on disconnect (mirrors _lastSnapOrigin).
    private readonly Dictionary<Player, byte[]?> _statusBlob = new(ReferenceEqualityComparer.Instance);

    // --- lag compensation: a per-player position history rewound at hitscan/projectile fire (antilag.qc) ---
    private readonly Dictionary<Player, AntilagBuffer> _antilag = new(ReferenceEqualityComparer.Instance);

    // The antilag breadth beyond players (antilag.qc antilag_takeback_all/restore_all also rewind IL_EACH
    // g_monsters PLUS IL_EACH g_projectiles where classname=="nade"): a parallel per-Entity history for every
    // monster + nade. Keyed by the entity itself (the QC stores the ring on the entity — antilag_record(it, it,
    // altime)). Pruned each frame as entities free; cleared on (re)spawn via SetOrigin teleport detection.
    private readonly Dictionary<Entity, AntilagBuffer> _antilagEntities = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entity> _antilagPruneScratch = new();   // reused: freed/gone entities to drop

    // antilag enable + record nudge, refreshed once per frame from the cvar store (xonotic-server.cfg:
    // g_antilag default 2 = "server side hit scan in the past"; g_antilag_nudge default 0). Only mode 2 takes
    // the shooter back on the server (antilag.qc traceline_antilag: autocvar_g_antilag != 2 → lag = 0); mode 1 is
    // client-verified hitscan and mode 0 is off — both apply zero server-side takeback.
    private int _antilagMode = 2;
    private float _antilagNudge;

    // --- master-server registration (server browser): periodic heartbeat + answer getinfo probes ---
    private MasterServerLink? _master;
    private readonly List<IPEndPoint> _masters = new();
    private float _heartbeatAccum;
    private const float HeartbeatInterval = 180f; // DP re-registers every ~3 minutes
    private float _masterPollAccum; // wall time since the last master pump (WS4: pumped on tick frames + a 0.25s pause fallback)

    /// <summary>An origin jump beyond this in a single tick is a teleport/respawn, not movement → set the teleport bit
    /// so the client snaps instead of lerping (normal play moves &lt;~20u/tick at 72 Hz).</summary>
    private const float TeleportTickDistance = 150f;

    /// <summary>Low-pass factor for the measured RTT (per input frame): smaller = smoother but slower to track a
    /// latency change. ~0.25 reaches ~95% of a step in ~10 input frames, damping the per-packet jitter that would
    /// otherwise make the rewind depth (and thus hit registration) flicker.</summary>
    private const float RttSmoothing = 0.25f;

    /// <summary>Per-frame captured fire-and-forget effects (broadcast unreliable in the event bundle).</summary>
    private readonly List<CapturedEffect> _effectQueue = new();

    /// <summary>Per-frame captured notifications (broadcast reliable in the reliable bundle).</summary>
    private readonly List<CapturedNotification> _notifyQueue = new();

    /// <summary>Per-frame captured positional sounds (broadcast unreliable in the sound bundle, DP SV_StartSound).</summary>
    private readonly List<CapturedSound> _soundQueue = new();

    /// <summary>One connected client: its peer id, its player, the input ring + ack cursor.</summary>
    private sealed class PeerState
    {
        public int PeerId;
        public int NetId;               // the small stable entity id sent on the wire (not the raw ENet peer id)
        public Player? Player;          // null until the handshake completes
        public bool Accepted;
        public uint LastProcessedSeq;   // servercommandframe: the highest input seq applied
        public uint HighestQueuedSeq;   // highest seq currently queued (dedup of the redundant tail)
        public readonly Queue<InputCommand> Pending = new();
        public InputCommand Last;       // last applied input (repeated if the queue starves — held keys)
        public bool HasLast;

        /// <summary>The last <see cref="NetControl.ItemsTime"/> payload key sent to this peer (tier + send-gate +
        /// the encoded table) — so the reliable message is resent only when THIS peer's view changes (the table,
        /// the tier, or its own send-gate flipping when it joins/observes — QC's ClientConnect /
        /// MakePlayerObserver / PlayerSpawn full-sync vs reset). "" = never sent. </summary>
        public string ItemsTimeKey = "";

        // Legacy gentle-drain state (fix for the 3–7-deep "dead zone" the hard Trim leaves untouched). A client
        // HITCH can leave the queue sitting a few commands deep FOREVER — producer + consumer both pace at 72 Hz,
        // so 1-in-1-out preserves whatever depth a burst settled at, below the hard-trim trigger = permanent input
        // latency. We detect a PERSISTENT floor (the queue's recent MINIMUM depth never reaches the jitter floor)
        // over a trailing window and bleed one extra command per tick until it does. A healthy LOW-FPS client
        // (whose bursts fully drain between packets) periodically empties toward the floor, so its recent-min stays
        // low and it is NEVER drained — only a stuck post-hitch backlog is. (Per-frame mode drains fully, so unused.)
        public int LegacyMinDepth = int.MaxValue;   // min Pending.Count seen in the current window
        public int LegacyDepthSamples;              // ticks elapsed in the current window
        public int LegacyPersistentFloor;           // last window's measured min (> residual ⇒ a standing backlog)
        // Per-frame (variable-dt) input mode for this client (set from the input-frame header): when true,
        // ProvideInput drains ALL pending commands this tick and OnClientMove runs movement per command (each with
        // its own DeltaTime) — DP's process-queued-client-moves; false = the legacy one-command-per-tick path.
        public bool PerFrameInput;

        // The input resolved for the current sim tick + the sim time it was resolved at. GameWorld pulls
        // InputProvider TWICE per tick (movement + the W_WeaponFrame driver); we dequeue/ack only on the first
        // call of a tick and return this cached command on the second, so the queue isn't drained 2x and
        // LastProcessedSeq doesn't over-ack an input whose movement never ran.
        public IMovementInput? TickInput;
        public float TickInputTime = float.NaN;
        // Per-frame mode: the per-command movement inputs drained for THIS tick (one per client render frame),
        // iterated by OnClientMove so each integrates with its own dt. Reused (cleared) each tick; keyed live by
        // TickInputTime == world time. Empty/stale → OnClientMove falls back to the single merged TickInput.
        public readonly List<IMovementInput> TickBatch = new();

        /// <summary>QC PlayerUseKey is edge-driven (DP fires it once per +use press, not per tick): the
        /// RELEASED-&gt;PRESSED rising edge of the +use button (BIT(5)) triggers vehicle board/exit. Track the
        /// previous tick's +use state per player so a HELD key doesn't board-&gt;exit-&gt;board flicker.</summary>
        public bool UsePrevDown;

        /// <summary>Per-client delta-compression baseline ring (the entity stream this client interpolates).</summary>
        public readonly ServerSnapshotHistory SnapHistory = new();

        /// <summary>The movevars hash last sent to this client (re-send the block only when the physics changes).</summary>
        public uint LastMoveVarsHash;

        /// <summary>The hash of the preset-RESOLVED movevar vector last sent to this client (v7, T54 —
        /// g_physics_clientselect). 0 = no per-client override outstanding; when a peer's resolved physics stops
        /// deviating from the global block, a count-0 "clear" block is sent once and this resets to 0.</summary>
        public uint LastResolvedVarsHash;

        /// <summary>The score-table version last sent (re-send the scoreboard block only when a score changed). -1 = never.</summary>
        public int LastScoreVersion = -1;

        /// <summary>The score-LAYOUT hash last sent (re-send the ScoreInfo label/flag block only on a mode switch).
        /// 0 = never sent (so the first snapshot always carries the layout, even if it equals the default hash).</summary>
        public uint LastScoreInfoHash;
        public bool SentScoreInfo;

        /// <summary>FNV hash of the last GametypeStatusBlock sent to this client (0 = never) — the per-mode
        /// round/objective HUD stats (T53) are personalized (the KH "31 = self" slot, the SURV hunter
        /// visibility), so each peer gates on the hash of ITS OWN serialized block, resending only when the
        /// bytes change.</summary>
        public uint LastModeStatusHash;

        /// <summary>Hash of the item-pickup tally last sent to this peer inside the scoreboard block (QC's
        /// Inventory entity is per-viewer, so the tally can change without any score changing).</summary>
        public uint LastItemStatsHash;

        /// <summary>The accuracy-change generation last sent to this client (T57, QC ENT_CLIENT_ACCURACY's
        /// per-weapon SendFlags change detection): the owner's per-weapon accuracy byte array is re-sent only
        /// when <see cref="Scores.AccuracyGeneration"/> moves. -1 = never sent (so the first snapshot to a client
        /// always carries the array, even when it's all zeros).</summary>
        public int LastAccuracyGen = -1;

        /// <summary>Measured round-trip latency in seconds (DP <c>host_client-&gt;ping = cmd.receivetime - cmd.time</c>,
        /// sv_user.c:847) — the server-receive time minus the server-time the client echoed from the snapshot it was
        /// responding to. Exponentially smoothed to damp per-packet jitter; this is the value <c>ANTILAG_LATENCY</c>
        /// (antilag.qh: <c>CS(e).ping * 0.001</c>) consumes to rewind. 0 on a LAN/listen/bot client (fully caught up).</summary>
        public float MeasuredRtt;
        public bool HasRtt;

        /// <summary>[T47] QC <c>CS(this).cmd_floodtime</c> (server/command/cmd.qc): the per-client command-flood
        /// bucket cursor — the absolute sim time the client's command budget is "used up to". A new command is
        /// rejected when <c>frameStart + count*time &lt; CmdFloodTime</c>; otherwise the cursor advances by
        /// <c>antispam_time</c> from <c>max(frameStart, CmdFloodTime)</c> (exponential backoff under sustained
        /// spam). Initialized to 0; the count*time headroom means a fresh client never trips on its first burst.</summary>
        public float CmdFloodTime;

        /// <summary>[T47] QC <c>.ban_checked</c> (server/ipban.qc <c>Ban_MaybeEnforceBanOnce</c>): the ban gate runs
        /// at most once per client. Once set, the per-command ban check short-circuits to "not banned" (the QC
        /// once-guard) — a banned client is dropped on its first command, so re-checking is pointless.</summary>
        public bool BanChecked;

        /// <summary>The session challenge issued to this client and whether its identity is verified (SessionAuth).</summary>
        public byte[]? AuthChallenge;
        public byte[]? PendingPublicKey;   // the client's identity key, held until it signs the challenge
        public string PendingName = "";    // the requested name, applied on successful auth
        public string IdentityFingerprint = "";
    }

    public ServerNet(NetTransport.Server transport, GameWorld world, string serverName = "XonoticGodot Server")
    {
        _transport = transport;
        _world = world;
        _serverName = serverName;

        _transport.PeerConnected += OnPeerConnected;
        _transport.PeerDisconnected += OnPeerDisconnected;
        _transport.PacketReceived += OnPacket;

        // The world asks us for each client's movement input every tick; serve it from that client's queue.
        _world.InputProvider = ProvideInput;
        _world.TickMovementBatch = TickMovementBatch;

        // Wire the NET sinks so server-emitted effects/notifications are serialized + broadcast (the
        // EffectEmitter/NotificationSystem "wire to netcode" TODOs). They capture into our per-frame queues;
        // Tick() flushes them after the snapshot.
        EffectEmitter.Sink = new EffectNetSink(this);
        NotificationSystem.Sink = new NotificationNetSink(this);

        // Positional sounds (DP SV_StartSound): the sim emits via Api.Sound.Play → SoundService.Broadcast. On a
        // listen server _world.Services.SoundImpl is the SAME instance Api.Sound resolves to, so capturing here
        // grabs exactly what the sim emits; flushed each frame in FlushSounds() like the effect bundle. A NAMED
        // method (not a lambda) so Dispose() can unsubscribe and a re-host on a reused world won't double-subscribe.
        _world.Services.SoundImpl.Broadcast += OnSoundEmitted;

        // Install the lag-compensation hook so hitscan weapon traces rewind other players to the shooter's view
        // time (antilag.qc). Weapons call LagComp.Begin/End ambiently; this routes it to our rewind/restore.
        LagComp.Provider = new LagCompProvider(this);

        // Feed the anticheat's snap-aim suppression window the real per-client ping (QC ANTILAG_LATENCY = the same
        // CS(e).ping the lag-comp rewind uses). FixAngle takes ping in MILLISECONDS (it multiplies by 0.001 like
        // QC), but EstimatedPing returns the smoothed round trip in SECONDS, so scale up. 0 for a LAN/bot client.
        _world.AntiCheat.PingProvider = p => EstimatedPing(p) * 1000f;

        // Per-player physics resolution (T54, QC Physics_UpdateStats → Physics_ClientOption): the shared sim
        // asks this for each player's preset-resolved movevar vector. Returns null when g_physics_clientselect
        // is off (the stock default) — the sim then reads the global cvars exactly as before.
        _presetProvider = ResolvePresetVector;
        MovementParameters.PresetProvider = _presetProvider;
    }

    // --- per-player preset physics (T54): the provider + a per-sim-tick resolve cache ---
    private readonly System.Func<Entity, float[]?> _presetProvider;
    private readonly Dictionary<Player, float[]?> _presetCache = new(ReferenceEqualityComparer.Instance);
    private float _presetCacheTime = float.NaN;
    private float[]? _presetCacheGlobals;

    /// <summary>
    /// QC <c>Physics_ClientOption</c> over the whole movevar vector for one player. Null when client physics
    /// selection is off, or when the player's resolution doesn't deviate from the globals (so the sim takes the
    /// plain FromCvars path). Cached per sim tick (keyed on <c>_world.Time</c>, which is constant within a tick)
    /// because the sim asks once per move and the snapshot writer asks again at broadcast.
    /// A bot/unknown player resolves with cl_physics "" — chain 2 skipped, but the g_physics_clientselect_default
    /// chain still applies, faithful to player.qc:34-40 running for ANY entity.
    /// </summary>
    private float[]? ResolvePresetVector(Entity e)
    {
        if (e is not Player p)
            return null;
        var cvars = _world.Services.Cvars;
        if (cvars.GetFloat("g_physics_clientselect") == 0f)
            return null;

        float now = _world.Time;
        if (_presetCacheTime != now)
        {
            _presetCache.Clear();
            _presetCacheGlobals = MoveVarsBlock.Capture(cvars);
            _presetCacheTime = now;
        }
        if (_presetCache.TryGetValue(p, out float[]? cached))
            return cached;

        string clPhysics = _byPlayer.ContainsKey(p) ? _world.Commands.GetClientCvar(p, "cl_physics", "") : "";
        float[] globals = _presetCacheGlobals!;
        float[] resolved = MoveVarsBlock.CaptureResolved(cvars, clPhysics, globals, _world.Services.CvarsImpl.Has);
        // No deviation from the global vector → null (the cheap shared path; also what keys the wire "clear").
        float[]? result = MoveVarsBlock.Hash(resolved) == MoveVarsBlock.Hash(globals) ? null : resolved;
        _presetCache[p] = result;
        return result;
    }

    /// <summary>Convenience: start a server on <paramref name="port"/> and drive <paramref name="world"/>.</summary>
    public static ServerNet? Start(GameWorld world, int port, int maxClients = 32, string serverName = "XonoticGodot Server")
    {
        NetTransport.Server? t = NetTransport.Server.Start(port, maxClients);
        return t is null ? null : new ServerNet(t, world, serverName) { _maxClients = maxClients };
    }

    // =====================================================================================
    //  Per-frame drive
    // =====================================================================================

    /// <summary>
    /// Advance one host frame: pump the transport, step the authoritative world by <paramref name="realDelta"/>
    /// seconds, then broadcast a snapshot to each client and flush the captured effect/notification bundles.
    /// </summary>
    public void Tick(float realDelta)
    {
        // Single-threaded path (sv_threaded 0, the default): everything runs inline on the caller's thread,
        // exactly as before — receive, sim, send. _simGate is null so no lock and the inbound queue is empty.
        TransportReceive(realDelta);
        int ticksRan = StepWorld(realDelta);
        TransportSend(realDelta, ticksRan > 0);
    }

    // =====================================================================================
    //  (§13.5) S5 thread split — the ROOT-CAUSE fix for the threaded prediction desync.
    //
    //  The first S5 design ran the WHOLE Tick (transport + sim) on the worker. But the Godot ENet transport
    //  (ENetMultiplayerPeer / PacketPeerUDP, created on the MAIN thread in ServerNet.Start) is main-thread-
    //  affine — servicing it from the worker mis-delivered/garbled snapshots, so the client reconciled against
    //  corrupt authoritative state and prediction ran away (camera through walls, projectiles from the server's
    //  stale position). The per-trace ConcurrencyGate fixed the sim-side crash storm but could never fix a
    //  CORRUPT-SNAPSHOT problem — the transport was the real culprit.
    //
    //  Fix: the worker runs ONLY the pure-C# sim (GameWorld.Frame — the heavy 4-12 ms cost S5 exists to move
    //  off the render thread); ALL Godot transport (receive/send/flush) stays on the MAIN thread. Every
    //  shared-world access on both sides is still serialised by _simGate, so it's race-free; we only changed
    //  WHICH thread touches the Godot objects. This is strictly more faithful to S5's stated intent.
    // =====================================================================================

    /// <summary>WORKER thread: advance the authoritative sim under the gate, then ENCODE the fresh state into
    /// the outbox (WS1 stage 1 — the world-reading encode belongs to the world-owning thread; pre-Stage-1 it
    /// ran on main under the gate, double-loading tick frames). NO Godot transport here.</summary>
    public void StepSimThreaded(float realDelta)
    {
        if (_simGate is null) { StepWorld(realDelta); return; } // defensive; threaded path always has the gate
        // WS1 per-tick gating: the gate is held in SHORT units (inbound drain / each tick via
        // SimulationLoop.TickGate / encode) instead of one hold across the whole step — a render-thread
        // per-trace reader now waits at most one unit, never a multi-tick catch-up + encode.
        lock (_simGate)
        {
            // WS1 stage 2: process the transport events main marshaled over (connect → handshake → input, in
            // arrival order), THEN the main→server command actions (chat / bot_add / changelevel), so this
            // thread is the only one that ever touches peers/world.
            while (_inboundNet.TryDequeue(out (InboundKind Kind, int Peer, int Channel, byte[]? Data) ev))
            {
                switch (ev.Kind)
                {
                    case InboundKind.Packet: OnPacketCore(ev.Peer, ev.Channel, ev.Data!); break;
                    case InboundKind.Connected: OnPeerConnectedCore(ev.Peer); break;
                    case InboundKind.Disconnected: OnPeerDisconnectedCore(ev.Peer); break;
                }
            }
            while (_inbound.TryDequeue(out Action? a)) a();
        }
        int ticksRan = StepWorld(realDelta); // each tick locks the gate itself (SimulationLoop.TickGate)
        if (ticksRan > 0)
        {
            lock (_simGate)
                using (Prof.Sample("net.send")) EncodeGameState(); // → SendPacket stages (OnSimWorker)
            System.Threading.Interlocked.Exchange(ref _pendingBroadcastFlag, 1); // flush/master cadence signal
        }
    }

    /// <summary>MAIN thread (threaded path): pure Godot transport work, NO sim gate anywhere on the common
    /// path (WS1 stages 2+3) — Poll's handlers only enqueue (the worker processes them), staged packets drain
    /// to the ENet peer lock-free, and only the rare master-server pump (probe answers read world info) takes
    /// the gate, on the WS4 cadence. The render thread never waits on a mid-flight sim tick here.</summary>
    public void PumpTransportThreaded(float realDelta)
    {
        if (_simGate is null) return;
        TransportReceive(realDelta); // handlers marshal to _inboundNet — no world access on this thread
        bool advanced = System.Threading.Interlocked.Exchange(ref _pendingBroadcastFlag, 0) != 0;

        _masterPollAccum += realDelta;
        if (advanced || _masterPollAccum >= 0.25f)
        {
            // Gate ONLY when a master link exists (probe answers read world info via BuildServerInfo). An
            // unregistered LAN listen server (_master null — the common case) must never block the render
            // thread behind a worker tick for what would be a no-op pump.
            if (_master is not null)
                lock (_simGate)
                    PumpMasterServer(_masterPollAccum);
            _masterPollAccum = 0f;
        }

        bool sent = DrainOutbox();
        if (sent)
            using (Prof.Sample("net.flush")) _transport.Flush();
    }

    private int _pendingBroadcastFlag; // worker sets when it encoded fresh state (Interlocked; flush/master cadence)

    // ---- WS1 stage 1 (variance program 2026-07-11): worker-side snapshot ENCODE -----------------------
    // The per-peer encode (BroadcastSnapshots + the Send* world-readers + event bundles) reads world state,
    // so it belongs on the thread that OWNS the world. Pre-Stage-1 it ran on MAIN under the gate — every
    // tick-frame paid sim (worker) + encode (main) serialised back-to-back, and a long tick stalled the
    // render thread at the gate before it could even send. Now the WORKER encodes right after its ticks
    // (already under its gate hold, world consistent), staging finished packets here; the MAIN thread hands
    // them to the Godot ENet peer (main-thread-affine) with NO gate. Pooled copies: the encode writers are
    // reused per peer, so the staged bytes must own their memory.
    /// <summary>Set on the sim worker (ServerThread.Run): sends stage into the outbox instead of touching
    /// the main-thread-affine Godot transport. Main-thread sends (handshake replies, the whole unthreaded
    /// path) pass straight through.</summary>
    [ThreadStatic] internal static bool OnSimWorker;

    /// <summary>What a staged outbound op does when the main thread drains it. One ordered queue (not one per
    /// kind) so a reject packet and the disconnect that follows it keep their encode order on the wire.</summary>
    private enum OutKind : byte { Packet, Broadcast, Disconnect }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(OutKind Kind, int Peer, byte[]? Buf, int Len, bool Reliable)> _outbox = new();

    // Cap-and-shed for the outbox (review 2026-07-27): the worker stages one snapshot set per peer per tick
    // at 72 Hz whether or not main ever drains — a Windows modal main-loop stall (title-bar drag, resize)
    // parks _Process for seconds while the queue grows without bound (memory + rented buffers), then bursts
    // seconds of STALE snapshots on resume. Shed policy: once the staged-but-undrained UNRELIABLE count hits
    // the cap, new unreliable ops are dropped at the staging site — exactly the packets a stalled main could
    // not have sent anyway, and each is superseded by the next tick's fresh encode. RELIABLE ops and
    // Disconnects are never shed (event-driven, bounded, and must arrive); they bypass the counter entirely.
    private const int OutboxUnreliableCap = 256;
    private int _outboxUnreliable;   // Interlocked: ++ on worker stage, -- on main drain
    private bool _outboxShedding;    // worker-only, logs one line per shed episode

    /// <summary>Every ServerNet unicast send funnels here (the 17 former direct <c>_transport.Send</c> sites).</summary>
    private void SendPacket(int peerId, ReadOnlySpan<byte> payload, bool reliable)
    {
        if (!OnSimWorker)
        {
            _transport.Send(peerId, payload, reliable);
            return;
        }
        StageOutbound(OutKind.Packet, peerId, payload, reliable);
    }

    /// <summary>
    /// Broadcast counterpart of <see cref="SendPacket"/> — the funnel the shared bundles use (sound + the
    /// non-excluded effect bundle). These ran <c>_transport.Broadcast</c> INLINE from the worker until
    /// 2026-07-27: the WS1 stage-1 migration moved the 17 <c>_transport.Send</c> sites into the outbox but
    /// grepped for <c>Send</c>, so the two <c>Broadcast</c> sites kept calling straight into the Godot ENet
    /// peer from the sim worker while the main thread was polling/sending on the same object. That is a plain
    /// cross-thread call into a non-thread-safe Godot object and it crashed the listen server intermittently
    /// mid-combat (0xC0000005 inside <c>PacketPeer.PutPacket</c>, ~1 run in 3 with bots fighting).
    /// </summary>
    private void BroadcastPacket(ReadOnlySpan<byte> payload, bool reliable)
    {
        if (!OnSimWorker)
        {
            _transport.Broadcast(payload, reliable);
            return;
        }
        StageOutbound(OutKind.Broadcast, 0, payload, reliable);
    }

    /// <summary>
    /// Drop a peer (handshake reject). Same story as <see cref="BroadcastPacket"/>: <see cref="Reject"/> runs
    /// inside <c>OnPacketCore</c>, which on the threaded path is drained by the WORKER at its step top — so
    /// <c>ENetMultiplayerPeer.DisconnectPeer</c> was being called off-thread too. Staged in the SAME queue as
    /// the reject packet that precedes it, so the client still receives the reason before the link closes.
    /// </summary>
    private void DisconnectPeer(int peerId)
    {
        if (!OnSimWorker)
        {
            _transport.Disconnect(peerId);
            return;
        }
        _outbox.Enqueue((OutKind.Disconnect, peerId, null, 0, false));
    }

    /// <summary>Copy <paramref name="payload"/> into a pooled buffer and stage it — the encode writers are
    /// reused per peer, so staged bytes must own their memory.</summary>
    private void StageOutbound(OutKind kind, int peerId, ReadOnlySpan<byte> payload, bool reliable)
    {
        if (!reliable)
        {
            int inFlight = System.Threading.Volatile.Read(ref _outboxUnreliable);
            if (inFlight >= OutboxUnreliableCap)
            {
                if (!_outboxShedding)
                {
                    _outboxShedding = true;
                    GD.Print($"[ServerNet] outbox at cap ({OutboxUnreliableCap} unreliable ops staged, main thread stalled?) — shedding unreliable sends until it drains");
                }
                return; // superseded by the next tick's encode — nothing to burst on resume
            }
            if (_outboxShedding && inFlight < OutboxUnreliableCap / 2)
                _outboxShedding = false;
            System.Threading.Interlocked.Increment(ref _outboxUnreliable);
        }
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buf);
        _outbox.Enqueue((kind, peerId, buf, payload.Length, reliable));
    }

    /// <summary>MAIN thread: hand worker-staged ops to the Godot transport. Lock-free vs the worker
    /// (ConcurrentQueue); FIFO, so per-peer order and packet-before-disconnect both hold. Returns true when
    /// anything was actually put on the wire (the caller then flushes).</summary>
    private bool DrainOutbox()
    {
        bool any = false;
        while (_outbox.TryDequeue(out (OutKind Kind, int Peer, byte[]? Buf, int Len, bool Reliable) p))
        {
            switch (p.Kind)
            {
                case OutKind.Packet:
                    // Drop packets staged for a peer that disconnected in the marshaling window (ENet spams a
                    // native "Invalid target peer" error otherwise). _livePeerIds is main-thread-owned, same
                    // thread as here.
                    if (_livePeerIds.Contains(p.Peer))
                    {
                        _transport.Send(p.Peer, new ReadOnlySpan<byte>(p.Buf!, 0, p.Len), p.Reliable);
                        any = true;
                    }
                    break;

                case OutKind.Broadcast:
                    // No per-peer filter to apply — ENet fans a broadcast out to whoever is live at send time.
                    // Skip it entirely when nobody is connected so we don't flag a flush for a no-op.
                    if (_livePeerIds.Count > 0)
                    {
                        _transport.Broadcast(new ReadOnlySpan<byte>(p.Buf!, 0, p.Len), p.Reliable);
                        any = true;
                    }
                    break;

                case OutKind.Disconnect:
                    // Graceful (now: false) — ENet closes after the queued reject packet above has gone out.
                    if (_livePeerIds.Contains(p.Peer))
                        _transport.Disconnect(p.Peer);
                    break;
            }
            // Disconnect ops carry Reliable=false but never touched the counter (enqueued directly,
            // not via StageOutbound) — only counted unreliable Packet/Broadcast entries decrement.
            if (p.Kind != OutKind.Disconnect && !p.Reliable)
                System.Threading.Interlocked.Decrement(ref _outboxUnreliable);
            if (p.Buf is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(p.Buf);
        }
        return any;
    }

    /// <summary>Receive handshakes + input frames (fills each peer's input queue). GODOT TRANSPORT — main thread
    /// only on the threaded path.</summary>
    private void TransportReceive(float realDelta)
    {
        using (Prof.Sample("net.poll")) _transport.Poll();
        SamplePeerStats(realDelta);
    }

    // Per-peer ENet statistics (ping + packet loss), sampled on the TRANSPORT-owning thread and read by the
    // scoreboard encode. BuildScoreboard used to call _transport.RoundTripMs/PacketLoss directly, but on the
    // threaded path it runs inside the WORKER's encode — and those go through ENetMultiplayerPeer.GetPeer() +
    // ENetPacketPeer.GetStatistic(), i.e. Godot objects the main thread is concurrently polling and sending on.
    // Same cross-thread class as the FlushSounds crash, just on the read side. Sampled here instead, published
    // through a ConcurrentDictionary the encode reads. ENet's RTT/loss are its own smoothed, seconds-scale
    // estimates, so the 4 Hz sample carries the same information the per-tick live read did.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int PingMs, float Loss)> _peerNetStats = new();
    private const float PeerStatsIntervalSeconds = 0.25f;
    private float _peerStatsAccum = PeerStatsIntervalSeconds; // due immediately on the first poll

    /// <summary>MAIN thread (transport owner): refresh <see cref="_peerNetStats"/> on a 4 Hz cadence.</summary>
    private void SamplePeerStats(float realDelta)
    {
        _peerStatsAccum += realDelta;
        if (_peerStatsAccum < PeerStatsIntervalSeconds)
            return;
        _peerStatsAccum = 0f;

        // _livePeerIds is this thread's own view of the connected set (maintained in the connect/disconnect
        // handlers, which fire from the Poll above).
        foreach (int id in _livePeerIds)
            _peerNetStats[id] = (_transport.RoundTripMs(id), _transport.PacketLoss(id));

        if (_peerNetStats.Count != _livePeerIds.Count)
            foreach (int id in _peerNetStats.Keys)          // snapshot; only walked after a disconnect
                if (!_livePeerIds.Contains(id))
                    _peerNetStats.TryRemove(id, out _);
    }

    /// <summary>Step the authoritative world by <paramref name="realDelta"/> + drive observer joins. PURE C# —
    /// safe on the worker. Returns the number of fixed sim ticks that ran.</summary>
    private int StepWorld(float realDelta)
    {
        // slowmo / host_timescale (DP): scale the whole sim's real→sim time mapping (default 1). Read live so it
        // toggles in-session; clamp negatives to 0 (paused). The client scales its input cadence by the SAME
        // (replicated) value so prediction stays in lockstep — the time-mapping that makes movement fps-independent.
        _world.Simulation.TimeScale = ResolveSlowmo(_world.Services.Cvars);

        // DP overload semantics (frametime parity audit 2026-07-11), read live for in-session A/B:
        //   sv_overload_timedrop 1 (default) — cap the owed-tick backlog at DP's 0.1 s and SHED the excess
        //     (sv_main.c:2604 / perf_acc_lost): sustained overload becomes brief uniform slow-motion instead of
        //     burst catch-up ticks spiking the already-slow frames (the r16 cost-wave oscillator). 0 = legacy
        //     preserve-and-burst (spiral guard only).
        //   sv_catchup_wallbudget_ms (default 0 = off) — stop starting catch-up ticks once a frame has spent this
        //     much wall time ticking (DP aborttime, sv_main.c:2676; the first owed tick always runs). Kept as an
        //     opt-in knob: the 2026-07-11 playtest found no felt benefit and timedrop + the soft cap already
        //     bound catch-up cost.
        var cv = _world.Services.Cvars;
        // The engine keeps wall-clock APIs out of the sim source (DeterminismTests) — the host injects the clock.
        _world.Simulation.WallClock ??= static () =>
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        _world.Simulation.BacklogDropSeconds =
            (string.IsNullOrWhiteSpace(cv.GetString("sv_overload_timedrop")) || cv.GetFloat("sv_overload_timedrop") != 0f)
                ? 0.1f : 0f;
        string budgetMs = cv.GetString("sv_catchup_wallbudget_ms");
        _world.Simulation.CatchupWallBudgetSeconds =
            (string.IsNullOrWhiteSpace(budgetMs) ? 0f : MathF.Max(0f, cv.GetFloat("sv_catchup_wallbudget_ms"))) / 1000f;

        // The world runs its fixed ticks, pulling each client's queued input via ProvideInput and firing
        // effect/notification emissions into our sinks. ticksRan == 0 when the host renders faster than 72 Hz.
        int ticksRan = _world.Frame(realDelta);
        LastTicksRan = ticksRan; // exposed for the cl_motion_trace diagnostic (r16 rubberband hunt)
        Prof.Mark("ticks", ticksRan); // >1 ⇒ a server.tick spike is catch-up amortizing a prior stall, not new work

        // Observer/join lifecycle: a human connects as an OBSERVER (ClientConnect → TRANSMUTE(Observer)) and only
        // enters the match via Join — on +jump/+attack or the delayed autojoin. The headless world doesn't drive
        // this on the real-client path, so we run it here per accepted peer from its last input. (Bots autojoin.)
        // WS1 per-tick gating: this MUTATES the world (join → spawn), and StepWorld no longer runs under one big
        // gate hold — take a short hold of its own when threaded (null gate = unthreaded, lock-free as before).
        if (_simGate is { } g)
            lock (g) DriveObserverJoins();
        else
            DriveObserverJoins();
        return ticksRan;
    }

    /// <summary>Read the live <c>slowmo</c> time scale (DP host_timescale), default 1 when unset, clamped to &gt;= 0
    /// (0 = paused). Shared by the server tick (<see cref="StepWorld"/>) and exposed so the client scales its input
    /// cadence by the same value.</summary>
    internal static float ResolveSlowmo(XonoticGodot.Common.Services.ICvarService cv)
    {
        string s = cv.GetString("slowmo");
        if (string.IsNullOrWhiteSpace(s)) return 1f;
        float v = cv.GetFloat("slowmo");
        return v < 0f ? 0f : v;
    }

    /// <summary>Encode + queue one game-state update per peer: the snapshot, the per-peer side channels, and
    /// the event bundles. READS WORLD STATE — call only on the thread that owns the world at the time (the
    /// main thread on the unthreaded path; the WORKER, under its gate hold, on the threaded path — WS1 stage 1).
    /// All sends route through <see cref="SendPacket"/>, which stages to the outbox on the worker.</summary>
    private void EncodeGameState()
    {
        BroadcastSnapshots();
        SendMinigameState(); // [T38] push per-peer minigame session snapshots (reliable channel)
        SendMatchState();    // global match clock (GAMESTARTTIME/TIMELIMIT/warmup) → TIMER panel
        SendMapVote();       // per-peer end-of-match map/gametype ballot (QC ENT_CLIENT_MAPVOTE) → MapVotePanel
        SendWaypoints(); // per-peer waypoint sprites (CTF flags + player pings…) → 3D markers + radar icons
        SendRadarLinks(); // Onslaught control-point/generator connection lines (QC ENT_CLIENT_RADARLINK) → radar
        SendItemsTime(); // per-peer item respawn-time table (QC itemstime IT_Write) → ItemsTimePanel countdowns
        FlushEventBundles();
    }

    /// <summary>Broadcast the snapshot (when the world advanced) + master-server pump + flush. GODOT TRANSPORT —
    /// unthreaded path only (the threaded path splits this: worker encodes in StepSimThreaded, main sends in
    /// PumpTransportThreaded). Reads world state.</summary>
    private void TransportSend(float realDelta, bool worldAdvanced)
    {
        // Send one snapshot per client + the shared event bundles, but ONLY when the world actually advanced.
        // The sim is a fixed 72 Hz accumulator, so when the render rate outruns it many frames run 0 ticks and
        // the world is identical to the last broadcast: re-encoding a duplicate snapshot per peer is pure waste.
        // Sending at the sim rate (DP networks at sys_ticrate, not the render rate) also lets a 0-tick frame
        // after a hitch recover a frame sooner. Events/minigame state are only produced inside a tick.
        using (Prof.Sample("net.send"))
        if (worldAdvanced)
            EncodeGameState();

        // Master-server registration: answer browser probes + re-heartbeat periodically. Variance program WS4:
        // not every render frame — at 144+ fps ~2/3 of frames run 0 ticks and polling the master socket on each
        // was pure per-frame overhead. Pump on tick frames, with a 0.25s wall fallback so probes still get
        // answered while the sim is paused (autopause/slowmo 0). The ACCUMULATED delta is passed through so the
        // ~180s heartbeat keeps wall time across skipped frames.
        _masterPollAccum += realDelta;
        if (worldAdvanced || _masterPollAccum >= 0.25f)
        {
            PumpMasterServer(_masterPollAccum);
            _masterPollAccum = 0f;
        }

        // Flush: push the snapshots/bundles queued above onto the wire NOW (send-only) instead of letting them
        // wait for the next Poll(). On the in-process listen loop the client's Poll() later this SAME frame then
        // receives the snapshot, so the fire/knockback the server just simulated reconciles a render-frame sooner.
        // Variance program WS4: only on tick frames — snapshots/bundles are only queued when the world advanced,
        // so a 0-tick flush had nothing to push; the rare 0-tick queued reply (a handshake accept from this
        // frame's TransportReceive) departs on the NEXT frame's Poll (enet service sends too) — ≤1 render frame.
        if (worldAdvanced)
            using (Prof.Sample("net.flush")) _transport.Flush();
    }

    /// <summary>
    /// Drive the observer→join lifecycle for each accepted human peer (server/client.qc ObserverOrSpectatorThink
    /// + the PlayerPreThink delayed autojoin). Reads the peer's last applied input for the +jump/+attack join
    /// edge and runs <see cref="ClientManager.ObserverOrSpectatorThink"/> (which also handles the ~1s delayed
    /// autojoin). A bot never reaches here (it joins at connect).
    ///
    /// T44 (spectator free-flight): an observer is no longer frozen here. In QC the per-client move tick
    /// (<c>sys_phys_update</c>) runs for EVERY IS_CLIENT — players AND spectators — and a spectator has
    /// MOVETYPE_NOCLIP/FLY so it FLIES (the fly branch, scaled by STAT(SPECTATORSPEED)). Zeroing the velocity
    /// each frame would defeat that. The observer reaches <c>Movement.Move</c> via the OnClientMove observer
    /// gate (see T44's gameWorldWiring seam) and its speed is driven by the spectator-speed ladder in
    /// <c>PlayerPhysics.SpectatorControl</c>.
    /// </summary>
    private void DriveObserverJoins()
    {
        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is not { IsObserver: true } observer)
                continue;

            bool jump = false, attack = false, attack2 = false;
            if (st.HasLast)
            {
                InputButtons b = st.Last.TypedButtons;
                jump = (b & InputButtons.Jump) != 0;
                attack = (b & InputButtons.Attack) != 0;
                attack2 = (b & InputButtons.Attack2) != 0;
            }
            _world.Clients.ObserverOrSpectatorThink(observer, jump, attack, attack2);
        }
    }

    // =====================================================================================
    //  Master server (server browser): heartbeat to the master + answer getinfo probes
    // =====================================================================================

    /// <summary>
    /// Register this server with the given master(s) (DP <c>sv_masterextra*</c>) so it appears in the internet
    /// server browser. Resolves each <c>host:port</c> once, sends an immediate heartbeat, and answers clients'
    /// <c>getinfo</c> probes with this server's infostring. Call after <see cref="Start"/>; silently skips an
    /// address that can't be resolved.
    /// </summary>
    /// <summary>
    /// Make this server discoverable on the LAN: bind the out-of-band <c>getinfo</c> answerer to a well-known
    /// port NEXT TO the game port so browser broadcasts reach it. DP answers OOB probes on the game socket
    /// itself, but our game socket is ENet (it drops raw OOB datagrams), so the discovery socket lives on the
    /// first free port in <c>[gamePort+1 .. gamePort+8]</c> — the browser sweeps that small range and the
    /// <c>infoResponse</c> carries the real game <c>port</c> to connect to (<see cref="BuildServerInfo"/>).
    /// Safe to call when every candidate port is taken (discovery is then simply off, logged).
    /// </summary>
    public void EnableLanDiscovery(int gamePort)
    {
        if (_master is not null)
            return; // already discoverable (EnableMasterServer bound a link)
        for (int candidate = gamePort + 1; candidate <= gamePort + 8; candidate++)
        {
            try
            {
                _master = new MasterServerLink(candidate);
                _master.GetInfoRequested += (from, _) => _master!.SendInfoResponse(from, BuildServerInfo(gamePort));
                GD.Print($"[ServerNet] LAN discovery: answering getinfo on UDP {candidate} (game port {gamePort}).");
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // port taken (another local server's discovery socket) — try the next one
            }
        }
        GD.Print($"[ServerNet] LAN discovery unavailable: UDP {gamePort + 1}..{gamePort + 8} all in use.");
    }

    public void EnableMasterServer(IEnumerable<string> masters, int port)
    {
        _master ??= new MasterServerLink();
        _master.GetInfoRequested += (from, _) => _master!.SendInfoResponse(from, BuildServerInfo(port));
        foreach (string addr in masters)
        {
            if (TryResolve(addr, out IPEndPoint? ep))
                _masters.Add(ep!);
        }
        _heartbeatAccum = HeartbeatInterval; // heartbeat on the next Tick
        GD.Print($"[ServerNet] master-server registration enabled for {_masters.Count} master(s).");
    }

    private void PumpMasterServer(float realDelta)
    {
        if (_master is null)
            return;
        _master.Poll(); // answer any getinfo probes (the event handler replies)

        _heartbeatAccum += realDelta;
        if (_heartbeatAccum >= HeartbeatInterval)
        {
            _heartbeatAccum = 0f;
            foreach (IPEndPoint m in _masters)
                _master.SendHeartbeat(m);
        }
    }

    /// <summary>The DP infostring a browser sees: hostname, map, gametype, player counts, protocol parity.</summary>
    private Dictionary<string, string> BuildServerInfo(int port)
    {
        // QC server/client.qc:1107 MUTATOR_CALLHOOK(BuildMutatorsPrettyString, ""): accumulate each active
        // mutator's ", Token" pretty label, then strip the leading ", " (QC substring(s, 2, strlen(s)-2)).
        // Appended as the "modifications" key so the server browser / welcome screen can surface it.
        string prettyMutators = MutatorActivation.BuildMutatorsPrettyString("");
        if (prettyMutators.StartsWith(", ", StringComparison.Ordinal))
            prettyMutators = prettyMutators.Substring(2);

        var info = new Dictionary<string, string>
        {
            ["hostname"] = _serverName,
            ["mapname"] = _world.Services.Cvars.GetString("mapname"),
            ["gametype"] = _world.GameType?.RegistryName ?? "dm",
            ["clients"] = _byPlayer.Count.ToString(),
            // g_maxplayers 0/unset means "no gameplay cap" — report the transport's connection cap so the
            // browser's players column shows real slots instead of "/0".
            ["sv_maxclients"] = _world.Services.Cvars.GetFloat("g_maxplayers") > 0
                ? _world.Services.Cvars.GetString("g_maxplayers")
                : _maxClients.ToString(),
            ["protocol"] = NetProtocol.BuildParity().ToString(),
            ["port"] = port.ToString(),
            ["gamename"] = "Xonotic",
        };
        // QC WriteString(msg_type, modifications) — only include the field when there are active mutators.
        if (prettyMutators.Length > 0)
            info["modifications"] = prettyMutators;
        return info;
    }

    private static bool TryResolve(string hostPort, out IPEndPoint? ep)
    {
        ep = null;
        int colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], out int port))
            return false;
        try
        {
            IPAddress[] addrs = Dns.GetHostAddresses(hostPort[..colon]);
            if (addrs.Length == 0) return false;
            ep = new IPEndPoint(addrs[0], port);
            return true;
        }
        catch (Exception ex) { GD.PrintErr($"[ServerNet] master resolve '{hostPort}' failed: {ex.Message}"); return false; }
    }

    // =====================================================================================
    //  Connection lifecycle
    // =====================================================================================

    // ---- WS1 stage 2: inbound marshaling (threaded path) -----------------------------------------------
    // Transport events fire on MAIN (Godot ENet is polled there), but their handlers mutate peers/world — the
    // reason main used to take the sim gate to receive, and so could stall behind a mid-flight worker tick.
    // Now, when threaded, the events just enqueue here (the payload byte[] is already an owned copy from
    // GetPacket) and the WORKER processes them at the top of its next step, in arrival order (one queue keeps
    // connect → handshake → input ordering). Replies those handlers send route through SendPacket, which is
    // already worker-aware (stage 1). Unthreaded: handlers run inline exactly as before.
    private enum InboundKind : byte { Packet, Connected, Disconnected }
    private readonly System.Collections.Concurrent.ConcurrentQueue<(InboundKind Kind, int Peer, int Channel, byte[]? Data)> _inboundNet = new();

    // MAIN-thread-owned view of which ENet peer ids are currently live — maintained in the connect/disconnect
    // handlers (which always fire on main, BEFORE marshaling), consumed by DrainOutbox to drop worker-staged
    // packets addressed to a peer that disconnected in the marshaling window (ENet rejects the target with a
    // native "Invalid target peer" error spam otherwise — caught by the 2026-07-11 two-instance test).
    private readonly HashSet<int> _livePeerIds = new();

    private bool MarshalInbound => _simGate is not null && !OnSimWorker;

    private void OnPeerConnected(int peerId)
    {
        _livePeerIds.Add(peerId); // main-thread view for the outbox drain filter
        if (MarshalInbound) { _inboundNet.Enqueue((InboundKind.Connected, peerId, 0, null)); return; }
        OnPeerConnectedCore(peerId);
    }

    private void OnPeerConnectedCore(int peerId)
    {
        // ENet-level connect only — we wait for the client's HandshakeRequest (build-parity) before admitting
        // it as a player. Track the slot so we can attach the player on accept.
        _peers[peerId] = new PeerState { PeerId = peerId };
        GD.Print($"[ServerNet] peer {peerId} connected (awaiting handshake).");
    }

    private void OnPeerDisconnected(int peerId)
    {
        _livePeerIds.Remove(peerId); // main-thread view for the outbox drain filter
        if (MarshalInbound) { _inboundNet.Enqueue((InboundKind.Disconnected, peerId, 0, null)); return; }
        OnPeerDisconnectedCore(peerId);
    }

    private void OnPeerDisconnectedCore(int peerId)
    {
        if (_peers.Remove(peerId, out PeerState? st) && st.Player is not null)
        {
            _byPlayer.Remove(st.Player);
            _antilag.Remove(st.Player);
            _lastSnapOrigin.Remove(st.Player);
            _statusBlob.Remove(st.Player);
            _playerNetIds.TryRemove(st.Player, out _);
            _world.Commands.ForgetPlayer(st.Player); // drop the per-client replicated-cvar/autoswitch tables
            _world.Clients.ClientDisconnect(st.Player);
            GD.Print($"[ServerNet] peer {peerId} disconnected ({st.Player.NetName}).");
        }
    }

    private void OnPacket(int from, int channel, byte[] data)
    {
        if (MarshalInbound) { _inboundNet.Enqueue((InboundKind.Packet, from, channel, data)); return; }
        OnPacketCore(from, channel, data);
    }

    private void OnPacketCore(int from, int channel, byte[] data)
    {
        var r = new BitReader(data);
        var control = (NetControl)r.ReadByte();
        switch (control)
        {
            case NetControl.HandshakeRequest:
                HandleHandshake(from, ref r);
                break;
            case NetControl.HandshakeAuth:
                HandleAuth(from, ref r);
                break;
            case NetControl.InputFrame:
                HandleInputFrame(from, ref r);
                break;
            case NetControl.ClientCommand:
                HandleClientCommand(from, ref r);
                break;
            default:
                // unknown / not-server-bound control byte — ignore (a malformed or out-of-phase packet).
                break;
        }
    }

    /// <summary>The <see cref="Player"/> assigned the small stable net id <paramref name="netId"/> (humans and
    /// bots), or null if none — the reverse of the wire id (lets the host map <c>ClientNet.LocalNetId</c> back to
    /// the local player for console client-commands).</summary>
    public Player? PlayerByNetId(int netId)
    {
        foreach (var kv in _playerNetIds)
            if (kv.Value == netId)
                return kv.Key;
        return null;
    }

    /// <summary>
    /// DP <c>clc_stringcmd</c>: run a console command line on behalf of the sending peer's player. Dispatched as
    /// a CLIENT command (<c>isServerConsole: false</c>, <c>caller</c> = the peer's player) so caller-gated
    /// commands (kill/say/team/…) act on the right player; the command's output is returned to that peer as a
    /// <see cref="NetControl.ServerPrint"/>. Ignored from an unauthed/observer peer.
    /// </summary>
    private void HandleClientCommand(int peerId, ref BitReader r)
    {
        string line = r.ReadString();
        if (r.BadRead)
            return;
        if (!_peers.TryGetValue(peerId, out PeerState? st) || !st.Accepted || st.Player is null
            || string.IsNullOrWhiteSpace(line))
            return;

        // [T47] SV_ParseClientCommand's three pre-dispatch gates (server/command/cmd.qc:1170-1251), run in QC
        // order BEFORE any command executes. Any gate that returns drops the command silently (QC `return`):
        //
        // GATE 1 — UTF-8 round-trip validation (cmd.qc:1172-1178): QC re-encodes the command char-by-char through
        // chr2str(str2chr(...)) and drops it if it doesn't round-trip. Malformed input never reaches the parser.
        if (!ClientCommandRegistry.IsValidUtf8Command(line))
            return;

        // GATE 2 — Ban_MaybeEnforceBanOnce (cmd.qc:1180-1181 → ipban.qc): if the client is banned, tell+drop them
        // and don't parse the command. Runs at most once per client (the QC .ban_checked once-guard).
        if (!st.BanChecked)
        {
            st.BanChecked = true;
            if (_world.Bans.MaybeEnforceBan(st.Player))
                return; // banned → dropped by the wired Bans.DropClient pipeline; command discarded
        }

        // GATE 3 — per-client command flood bucket (cmd.qc:1191-1251): exponential-backoff anti-spam on the
        // common command stream, exempting the engine/server/chat-handled verbs (begin/download/prespawn/spawn/
        // sentcvar/pause/say/say_team/tell/minigame-non-common/…). A flooded command is rejected with a notice.
        // The exemption test + budget math live in ClientCommandRegistry (the testable Server assembly); this
        // method supplies the per-client cursor, the sim frame-start time, and the antispam cvar reads.
        string verb = FirstToken(line);
        if (!ClientCommandRegistry.IsCommandFloodExempt(verb, SecondToken(line)))
        {
            float antispamTime = Cvars.FloatOr("sv_clientcommand_antispam_time", 1f);
            float antispamCount = Cvars.FloatOr("sv_clientcommand_antispam_count", 8f);
            // QC uses gettime(GETTIME_FRAMESTART); _world.Time (the sim clock, constant within a tick) is the
            // faithful equivalent of that frame-start time. Capture the pre-update cursor for the wait notice.
            float floodBefore = st.CmdFloodTime;
            if (!ClientCommandRegistry.TryPassFlood(ref st.CmdFloodTime, _world.Time, antispamCount, antispamTime))
            {
                float wait = floodBefore - (_world.Time + antispamCount * antispamTime); // QC: cmd_floodtime - mod_time
                SendPrint(peerId, $"^3CMD FLOOD CONTROL: wait ^1{wait:0.######}^3 seconds, command was: {line}");
                return;
            }
        }

        CommandContext ctx = _world.Commands.Execute(line, isServerConsole: false, caller: st.Player);
        string output = ctx.Output;
        if (string.IsNullOrEmpty(output))
            return;
        SendPrint(peerId, output.TrimEnd('\n', '\r'));
    }

    /// <summary>The lowercased first whitespace-delimited token of <paramref name="line"/> (QC <c>strtolower(argv(0))</c>).</summary>
    private static string FirstToken(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        int start = i;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        return line.Substring(start, i - start).ToLowerInvariant();
    }

    /// <summary>The second whitespace-delimited token of <paramref name="line"/> (QC <c>argv(1)</c>), "" if absent.</summary>
    private static string SecondToken(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;          // skip argv(0)
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        int start = i;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        return start < line.Length ? line.Substring(start, i - start) : "";
    }

    /// <summary>Send one console line to a single peer (DP <c>svc_print</c>).</summary>
    private void SendPrint(int peerId, string text)
    {
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.ServerPrint);
        _scratchWriter.WriteString(text);
        SendPacket(peerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    /// <summary>
    /// Broadcast one console line to EVERY accepted client (DP <c>bprint</c> over <c>svc_print</c>) — the host
    /// wires <see cref="Commands.ChatBroadcast"/> (vote/ban/team notices) and <see cref="Commands.ChatHandler"/>
    /// (player chat) to this so they reach all clients' consoles, the listen-server host included.
    /// </summary>
    public void BroadcastPrint(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.ServerPrint);
        _scratchWriter.WriteString(text);
        foreach (PeerState st in _peers.Values)
            if (st.Accepted)
                SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    // =============================================================================================
    // [T46] chat delivery — per-player sprint + team/private routing with ignore filtering. The chat engine
    // (Chat.Say) does the routing/ignore/flood logic over ClientManager.Players; these are the net send paths.
    // A host wires Commands.ChatToPlayer to SendChatToPlayer so each routed recipient gets a reliable svc_print.
    // =============================================================================================

    /// <summary>QC <c>sprint(client, text)</c>: send one chat line to a single player's console (DP svc_print over
    /// the reliable channel). No-op if the player has no accepted peer (a bot, or a not-yet-handshaked client).</summary>
    public void SendChatToPlayer(Player player, string text)
    {
        if (player is null || string.IsNullOrEmpty(text))
            return;
        if (!_byPlayer.TryGetValue(player, out PeerState? st) || !st.Accepted)
            return;
        SendPrint(st.PeerId, text);
    }

    /// <summary>
    /// QC the say_team FOREACH_CLIENT branch (chat.qc:328): deliver <paramref name="text"/> to every real client
    /// on <paramref name="team"/> who has not ignored <paramref name="sender"/>. Append-only convenience for a
    /// host that prefers ServerNet to own team routing; <see cref="Chat.Say"/> already routes per-player through
    /// <see cref="SendChatToPlayer"/>, so this is an alternative entry, not on the default path.
    /// </summary>
    public void SendTeamChat(Player? sender, int team, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (Player p in _world.Clients.Players)
        {
            if (p.IsBot || p.IsObserver || (int)p.Team != team)
                continue;
            if (ReferenceEquals(p, sender))
                continue;
            if (sender is not null && IgnorePlayerInList(p, sender))
                continue;
            SendChatToPlayer(p, text);
        }
    }

    /// <summary>
    /// QC the private (tell) delivery (chat.qc:295-307): deliver <paramref name="text"/> to <paramref name="target"/>
    /// only, suppressed if the target has ignored <paramref name="sender"/>. Append-only convenience entry.
    /// </summary>
    public void SendPrivateChat(Player? sender, Player target, string text)
    {
        if (target is null || string.IsNullOrEmpty(text))
            return;
        if (sender is not null && IgnorePlayerInList(target, sender))
            return; // sender is ignored by target — drop it
        SendChatToPlayer(target, text);
    }

    /// <summary>QC <c>ignore_playerinlist(this, other)</c>: is <paramref name="other"/> on <paramref name="self"/>'s
    /// ignore list? Delegates to the chat engine's PersistentId-keyed check.</summary>
    public static bool IgnorePlayerInList(Player self, Player other) => Chat.IgnorePlayerInList(self, other);

    /// <summary>
    /// QC <c>soundto(MSG_ONE, emitter, channel, sample, volume, atten, 0)</c>: send one positional sound to a
    /// single client only (the <c>target_speaker ACTIVATOR</c> BIT3 per-client sound). Encodes a one-record
    /// <see cref="NetControl.SoundBundle"/> using the same <see cref="XonoticGodot.Net.SoundWire"/> codec as
    /// <see cref="FlushSounds"/> and delivers it ONLY to <paramref name="client"/>'s peer (reliable so it
    /// arrives even on a laggy connection — matching QC's reliable SOUND channel for MSG_ONE). No-op if the
    /// player has no accepted peer (bot, unconnected, or non-remote entity).
    /// </summary>
    public void SendSoundToPlayer(
        Player client, Entity emitter, SoundChannel channel, string sample, float volume, float attenuation)
    {
        if (client is null || string.IsNullOrEmpty(sample)) return;
        if (!_byPlayer.TryGetValue(client, out PeerState? st) || !st.Accepted) return;

        // Build a one-record SoundBundle using the shared SoundWire codec (same layout as FlushSounds).
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.SoundBundle);
        int countPos = ReserveCount(_scratchWriter);
        var rec = new XonoticGodot.Net.SoundWire
        {
            Sample = sample,
            Origin = emitter.Origin + (emitter.Mins + emitter.Maxs) * 0.5f,
            Volume = volume,
            Attenuation = attenuation,
            Channel = (int)channel,
            SourceNetId = NetIdForEntity(emitter),
            Loop = false,
            Stop = false,
            Pitch = 1f,
        };
        rec.Write(_scratchWriter);
        PatchCount(_scratchWriter, countPos, 1);
        // Use reliable delivery — QC's soundto MSG_ONE goes on the reliable channel so the cue arrives
        // even when unreliable packets are lost. Mirrors SendChatToPlayer's reliable send pattern.
        SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    /// <summary>[T38] Push each changed minigame session's snapshot to its participating peers (QC
    /// <c>minigame_resend</c> + <c>minigame_CheckSend</c>), each carrying that peer's own team; and an empty
    /// envelope to anyone who just left / whose session ended (QC the per-entity removal → CSQC
    /// <c>deactivate_minigame</c>). Reliable channel — the board state must arrive.</summary>
    private void SendMinigameState()
    {
        MinigameSessionManager mg = _world.Minigames;

        // Changed sessions → per-participant envelope (carries that participant's own team).
        foreach (MinigameSession s in mg.DrainDirty())
        {
            foreach ((Player player, int team) in mg.Participants(s))
            {
                if (!_byPlayer.TryGetValue(player, out PeerState? st) || !st.Accepted)
                    continue;
                _scratchWriter.Reset();
                _scratchWriter.WriteByte((byte)NetControl.MinigameState);
                MinigameNetState.EncodeEnvelope(_scratchWriter, s, team);
                SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
            }
        }

        // Departed players → an empty envelope so the client hides the board.
        foreach (Player player in mg.DrainDeparted())
        {
            if (!_byPlayer.TryGetValue(player, out PeerState? st) || !st.Accepted)
                continue;
            _scratchWriter.Reset();
            _scratchWriter.WriteByte((byte)NetControl.MinigameState);
            MinigameNetState.EncodeEnvelope(_scratchWriter, null, 0);
            SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
        }
    }

    private float _nextMatchStateSend;
    private string _lastMatchStateKey = "";
    private readonly System.Text.StringBuilder _itemsTimeSb = new(); // reused fold buffer for SendItemsTime dedup

    /// <summary>Broadcast the GLOBAL match-clock state (QC STAT(GAMESTARTTIME)/TIMELIMIT/WARMUP) to every
    /// accepted peer so the TIMER panel can count up/down on the play path. Sent immediately when a value
    /// changes (warmup→live, overtime, timelimit edit) and otherwise ~1×/s so a late joiner gets it within a
    /// second. Reliable + tiny (~18 bytes). Same field order as <see cref="ClientNet.HandleMatchState"/>.</summary>
    private void SendMatchState()
    {
        float gameStart = _world.GameStartTime;
        float timeLimitSec = _world.Services.Cvars.GetFloat("timelimit") * 60f;
        bool warmup = _world.Warmup.WarmupStage;
        float warmupLimit = _world.Warmup.WarmupLimit;
        bool intermission = _world.Intermission.Running;
        // QC Set_NextMap / Send_NextMap_To_Player: the upcoming map name → the scoreboard "Next map:" line.
        string nextMap = _world.NextMapBroadcast ?? "";
        // QC STAT(OVERTIMES) (common/stats.qh): 0 = none, N = "Overtime #N", OVERTIME_SUDDENDEATH = "Sudden Death".
        // Drives the TIMER panel's persistent overtime/sudden-death subtext (the one-shot center notifications are
        // sent separately by the overtime cascade).
        int overtimes = _world.OverTime.Overtimes;
        // [W14a] QC randomseed: the networked deterministic-RNG seed (re-rolled every 5s). Rides this packet (the
        // smallest diff that reaches every accepted peer); the changed value is part of the dedup key below so a
        // re-roll forces an immediate re-send instead of waiting for the ~1×/s heartbeat.
        int randomSeed = _world.RandomSeed.Current;

        string key = $"{gameStart:F2}|{timeLimitSec:F1}|{(warmup ? 1 : 0)}|{warmupLimit:F1}|{(intermission ? 1 : 0)}|{nextMap}|{overtimes}|{randomSeed}";
        if (key == _lastMatchStateKey && _world.Time < _nextMatchStateSend)
            return;
        _lastMatchStateKey = key;
        _nextMatchStateSend = _world.Time + 1f;

        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.MatchState);
        _scratchWriter.WriteFloat(gameStart);
        _scratchWriter.WriteFloat(timeLimitSec);
        _scratchWriter.WriteBool(warmup);
        _scratchWriter.WriteFloat(warmupLimit);
        _scratchWriter.WriteBool(intermission);
        _scratchWriter.WriteString(nextMap);
        _scratchWriter.WriteLong(overtimes); // 32-bit: OVERTIME_SUDDENDEATH (1<<24) exceeds the 24-bit range
        // [W14a] QC randomseed.cnt (0..65535) — APPENDED at the END (an old client stops reading before this and is
        // unaffected; ClientNet.HandleMatchState reads it in this same trailing position). Reserve a comment for any
        // future trailing match-state field: it MUST go AFTER this and be read in the same order.
        _scratchWriter.WriteUShort(randomSeed);

        foreach (PeerState st in _peers.Values)   // real network peers (matches BroadcastSnapshots; excludes bots)
            if (st.Accepted && st.Player is not null)
                SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    private float _nextMapVoteSend;
    private bool _lastMapVoteShowing;

    /// <summary>QC the <c>mapvote</c> net entity (server/mapvoting.qc) sent to every client: push the live
    /// end-of-match ballot so a pure remote client's <c>MapVotePanel</c> (QC client/mapvoting.qc MapVote_Draw)
    /// lights up. Sent PER-PEER because the own-vote field (QC <c>mv_ownvote</c>) differs per client — the rest of
    /// the ballot is identical, but stamping each peer's <see cref="MapVoting.SelectionOf"/> is cheap. Throttled to
    /// ~2×/s while the vote runs (live counts), plus an immediate edge send on the running→idle transition so the
    /// panel hides promptly. Field order mirrors <c>ClientNet.HandleMapVote</c>. Reliable channel.</summary>
    private void SendMapVote()
    {
        MapVoting vote = _world.MapVote;
        // QC mv_active: the panel draws while the vote runs or while the finished winner is being revealed.
        bool showing = vote.Running || (vote.Finished && !string.IsNullOrEmpty(vote.WinningMap));
        if (!showing)
        {
            // Edge: send one final "not showing" frame so a remote panel clears, then go quiet.
            if (!_lastMapVoteShowing)
                return;
            _lastMapVoteShowing = false;
        }
        else
        {
            // Throttle live updates to ~2×/s except on the first showing frame (push the ballot identity at once).
            if (_lastMapVoteShowing && _world.Time < _nextMapVoteSend)
                return;
            _lastMapVoteShowing = true;
            _nextMapVoteSend = _world.Time + 0.5f;
        }

        IReadOnlyList<MapVoteCandidate> cands = vote.Candidates;
        // QC mv_timeout: send SECONDS-REMAINING (not absolute server time) so the client converts it against its own
        // sim clock — the listen/remote clocks differ, and the panel counts down vs Api.Clock.Time.
        float remaining = vote.Timeout > 0f ? System.Math.Max(0f, vote.Timeout - _world.Time) : -1f;
        // QC mv_winner (1-based, 0 = not yet): the ballot index of the chosen map among the non-abstain options.
        int winner1Based = 0;
        if (vote.Finished && !string.IsNullOrEmpty(vote.WinningMap))
        {
            int slot = 0;
            foreach (MapVoteCandidate c in cands)
            {
                if (c.IsAbstain) continue;
                slot++;
                if (string.Equals(c.MapName, vote.WinningMap, System.StringComparison.OrdinalIgnoreCase)) { winner1Based = slot; break; }
            }
        }

        foreach (PeerState st in _peers.Values)   // real network peers (matches SendMatchState; excludes bots)
        {
            if (!st.Accepted || st.Player is null)
                continue;

            _scratchWriter.Reset();
            _scratchWriter.WriteByte((byte)NetControl.MapVote);
            _scratchWriter.WriteBool(showing);
            _scratchWriter.WriteBool(vote.Finished);
            _scratchWriter.WriteBool(vote.IsGametypeVote);
            _scratchWriter.WriteBool(_world.Services.Cvars.GetFloat("sv_vote_gametype_detail") != 0f); // QC mv_detail
            _scratchWriter.WriteFloat(remaining);
            _scratchWriter.WriteShort(winner1Based);
            // QC mv_ownvote: this peer's own selection (-1 = none / abstain), so the panel highlights the right cell.
            int own = vote.SelectionOf(st.Player);
            bool ownAbstain = own >= 0 && own < cands.Count && cands[own].IsAbstain;
            _scratchWriter.WriteShort(ownAbstain ? -1 : own);
            // QC mv_abstain: the appended "Don't care" slot is present on the ballot.
            bool abstain = false;
            foreach (MapVoteCandidate c in cands) if (c.IsAbstain) { abstain = true; break; }
            _scratchWriter.WriteBool(abstain);
            // Candidate list (excluding the abstain slot — the client renders it from the abstain flag). The
            // client computes the pic/pretty-name/description from the name itself (GameTypes registry / map pic),
            // exactly like the listen-server feed in NetGame.UpdateMapVotePanel, so only the raw fields ride here.
            int n = 0;
            foreach (MapVoteCandidate c in cands) if (!c.IsAbstain) n++;
            _scratchWriter.WriteByte((byte)n);
            foreach (MapVoteCandidate c in cands)
            {
                if (c.IsAbstain) continue;
                _scratchWriter.WriteString(c.MapName);
                _scratchWriter.WriteShort(c.Available ? c.Votes : -1); // QC mv_votes: -1 = unavailable/hidden
                _scratchWriter.WriteBool(c.Available);
                _scratchWriter.WriteString(c.Suggester ?? "");
            }

            SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
        }
    }

    /// <summary>
    /// [W14a-QW4] QC <c>ClientInit_misc</c> (server/client.qc:907) — the one-shot per-server gameplay-constant bundle
    /// sent once to a joining client right after <see cref="NetControl.HandshakeAccept"/> (the welcome/accept
    /// handshake leg). Carries the constants a pure remote client needs at session init: the hook muzzle shot origin,
    /// <c>g_trueaim_minrange</c>, <c>g_balance_damagepush_speedfactor</c>, <c>g_balance_armor_blockpercent</c>, the
    /// networked <c>serverflags</c>, the map fog string, and <c>g_nexball_meter_period</c>. Field order mirrors
    /// <see cref="ClientNet.HandleClientInit"/>.
    ///
    /// Port divergence vs Base: QC sends FOUR hook + four arc shot origins (one per cl_gunalign side) compressed via
    /// compressShotOrigin; the port models the hook muzzle as a single offset (<c>Hook.HookShotOrigin</c>) and computes
    /// the arc muzzle inline (no stored arc_shotorigin), so only the single hook origin is networked here as a raw
    /// vector. The per-align arc array is RESERVED for a later wave if/when the port models it server-side.
    /// </summary>
    private void SendClientInit(int peerId)
    {
        var cvars = _world.Services.Cvars;
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.ClientInit);
        // QC hook_shotorigin[s] (a single port offset; see the divergence note above) — raw float vector.
        _scratchWriter.WriteVector(XonoticGodot.Common.Gameplay.Hook.HookShotOrigin, NetPrecision.Float);
        // QC autocvar_g_trueaim_minrange (WriteCoord) — the min range below which true-aim is skipped.
        _scratchWriter.WriteFloat(cvars.GetFloat("g_trueaim_minrange"));
        // QC this.cnt * 255 → g_balance_damagepush_speedfactor; this.count * 255 → g_balance_armor_blockpercent. The
        // port sends them as raw floats (no /255 byte quantization) — a faithful value with more precision than Base.
        _scratchWriter.WriteFloat(cvars.GetFloat("g_balance_damagepush_speedfactor"));
        _scratchWriter.WriteFloat(cvars.GetFloat("g_balance_armor_blockpercent"));
        // QC serverflags (the networked SERVERFLAG_* bitmap — fullbright/forbid-pickuptimer/teamplay).
        _scratchWriter.WriteLong(XonoticGodot.Common.Gameplay.ServerFlags.Value);
        // QC: if (autocvar_sv_foginterval && world.fog != "") WriteString(world.fog) else WriteString("").
        string fog = cvars.GetFloat("sv_foginterval") != 0f ? cvars.GetString("sv_fog") : "";
        _scratchWriter.WriteString(fog ?? "");
        // QC g_nexball_meter_period (the nexball powershot meter cycle period).
        _scratchWriter.WriteFloat(cvars.GetFloat("g_nexball_meter_period"));
        SendPacket(peerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    /// <summary>Push the item respawn-time table to each peer — the C# port of QC's CSQC <c>itemstime</c> net
    /// message + <c>Item_ItemsTime_SetTimesForPlayer</c> / <c>SetTimesForAllPlayers</c> (itemstime.qc). The table
    /// is the <see cref="XonoticGodot.Common.Gameplay.ItemstimeMutator"/>'s <c>CurrentTimes</c> (absolute respawn
    /// times with the negative "another copy available now" encoding) plus the live <c>STAT(ITEMSTIME)</c> tier
    /// (= <c>sv_itemstime</c>). QC's <c>SetTimesForAllPlayers</c> only sends to a peer when
    /// <c>warmup_stage || !IS_PLAYER(it) || sv_itemstime == 2</c> — i.e. only spectators get times in a live
    /// non-warmup round unless <c>sv_itemstime==2</c>; a live player whose send-gate is closed gets the RESET
    /// table (Item_ItemsTime_ResetTimesForPlayer: -1 for absent item types, else 0) so its panel hides the
    /// countdowns. Reliable + small; resent to a peer only when ITS view changes (the table, the tier, or its
    /// own send-gate flipping on join/observe/spawn — covers QC's ClientConnect / MakePlayerObserver / PlayerSpawn
    /// hooks). The client decode mirrors this field order (<see cref="ClientNet.HandleItemsTime"/>).</summary>
    private void SendItemsTime()
    {
        if (XonoticGodot.Common.Gameplay.Mutators.ByName("itemstime")
            is not XonoticGodot.Common.Gameplay.ItemstimeMutator itm || !itm.IsEnabled)
            return;

        int tier = itm.Tier; // QC STAT(ITEMSTIME) = autocvar_sv_itemstime (0/1/2)
        bool warmup = _world.Warmup.WarmupStage;
        System.Collections.Generic.IReadOnlyDictionary<string, float> times = itm.CurrentTimes;

        // Fold the (peer-independent) table once; the per-peer dedup key is just tier|sendFull prepended.
        _itemsTimeSb.Clear();
        foreach (System.Collections.Generic.KeyValuePair<string, float> kv in times)
            _itemsTimeSb.Append(kv.Key).Append('=').Append(kv.Value.ToString("F2")).Append(';');
        string tableFold = _itemsTimeSb.ToString();

        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is null)
                continue;
            // QC IS_PLAYER(it): a live in-game client gets the table only in warmup or sv_itemstime==2; a
            // spectator/observer always gets it (Item_ItemsTime_SetTimesForAllPlayers send gate).
            bool sendFull = warmup || st.Player.IsObserver || tier == 2;

            // Per-peer dedup: resend the reliable message only when this peer's resolved view changed (the table,
            // the tier, or its own send-gate flipping on join/observe/spawn).
            string key = $"{tier}|{(sendFull ? 1 : 0)}|{tableFold}";
            if (key == st.ItemsTimeKey)
                continue;
            st.ItemsTimeKey = key;

            _scratchWriter.Reset();
            _scratchWriter.WriteByte((byte)NetControl.ItemsTime);
            _scratchWriter.WriteByte((byte)tier);
            _scratchWriter.WriteByte((byte)System.Math.Min(times.Count, 255));
            foreach (System.Collections.Generic.KeyValuePair<string, float> kv in times)
            {
                _scratchWriter.WriteString(kv.Key);
                // QC SetTimesForPlayer sends the real time; a gated-out live player gets the reset value. The
                // port's table only ever holds present items, so the reset value is the panel's "hide" sentinel -1.
                _scratchWriter.WriteFloat(sendFull ? kv.Value : -1f);
            }
            SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
        }
    }

    private readonly System.Collections.Generic.List<XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite> _wpScratch = new();
    private readonly System.Collections.Generic.List<XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite> _wpVisible = new();
    private bool _wpWasNonEmpty;

    /// <summary>Push the live waypoint sprites (QC the networked ENT_CLIENT_WAYPOINT entities) to each peer,
    /// filtered to what that peer may see (team/rule/predicate). The set is the persistent
    /// <see cref="XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites"/> manager (player pings, deployed
    /// markers) merged with the gametype's per-tick objective rebuild (flags/points/keys via
    /// <c>GameType.CollectWaypoints</c>). Unreliable + small (positions move with carriers). Skipped for
    /// waypoint-less modes except the single clearing message when a mode that HAD waypoints drops to none.</summary>
    private void SendWaypoints()
    {
        var mgr = XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites.Active;
        _wpScratch.Clear();
        _world.GameType?.CollectWaypoints(_wpScratch);

        int total = mgr.Count + _wpScratch.Count;
        if (total == 0 && !_wpWasNonEmpty)
            return; // waypoint-less mode: never spam empty messages
        _wpWasNonEmpty = total > 0;

        float now = _world.Time;
        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is null)
                continue;
            Player peer = st.Player;

            // Gather the peer-visible subset (manager persistents + gametype objectives), capped to a byte count.
            _wpVisible.Clear();
            foreach (var wp in mgr) if (WaypointVisible(wp, peer) && _wpVisible.Count < 255) _wpVisible.Add(wp);
            foreach (var wp in _wpScratch) if (WaypointVisible(wp, peer) && _wpVisible.Count < 255) _wpVisible.Add(wp);

            _scratchWriter.Reset();
            _scratchWriter.WriteByte((byte)NetControl.Waypoints);
            _scratchWriter.WriteByte((byte)_wpVisible.Count);
            foreach (var wp in _wpVisible)
            {
                System.Numerics.Vector3 o = wp.Origin;
                _scratchWriter.WriteLong(wp.Id);
                _scratchWriter.WriteFloat(o.X);
                _scratchWriter.WriteFloat(o.Y);
                _scratchWriter.WriteFloat(o.Z);
                _scratchWriter.WriteByte((byte)(wp.Team & 0xFF));
                // QC Draw_WaypointSprite SPRITERULE_TEAMPLAY (waypointsprites.qc:514): a teamplay waypoint shows a
                // DIFFERENT image to own-team / enemy / spectator (model2 / model1 / model3). The port resolves it
                // here, per peer, instead of networking the triple. Non-teamplay rules return SpriteName unchanged.
                string spriteFor = wp.SpriteFor((int)peer.Team, peer.IsObserver);
                _scratchWriter.WriteString(spriteFor);
                // QC packs the radar-icon byte: low 7 bits = m_radaricon (0/1), bit 7 = "ping now" (cnt|BIT(7)),
                // which the client turns into an expanding gfx/teamradar_ping ring. waypointsprites.qc:187-192.
                int radarByte = (wp.RadarIcon & 0x7F);
                if (XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites.IsPinging(wp))
                    radarByte |= 0x80;
                _scratchWriter.WriteByte((byte)radarByte);
                _scratchWriter.WriteByte(Clamp255(wp.Color.X));
                _scratchWriter.WriteByte(Clamp255(wp.Color.Y));
                _scratchWriter.WriteByte(Clamp255(wp.Color.Z));
                _scratchWriter.WriteFloat(wp.Health);
                _scratchWriter.WriteFloat(XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites.FadeAlpha(wp));
                _scratchWriter.WriteByte((byte)(wp.HelpmeUntil > now ? 1 : 0));
                _scratchWriter.WriteFloat(wp.MaxDistance);
                _scratchWriter.WriteBool(wp.Hideable);
            }
            SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: false);
        }
    }

    private readonly System.Collections.Generic.List<XonoticGodot.Common.Gameplay.Onslaught.RadarLinkSegment> _radarLinkScratch = new();
    private bool _radarLinksWasNonEmpty;

    /// <summary>Push the live Onslaught radar links (QC the networked ENT_CLIENT_RADARLINK entities) to every
    /// accepted peer so the team radar can draw the control-point/generator connection lines (QC
    /// draw_teamradar_link). Only Onslaught produces links; other modes never send (and send one clearing message
    /// when a mode that HAD links drops to none, mirroring SendWaypoints). The set is identical for every peer
    /// (links are not team-filtered — QC draws all of them), so it is encoded once and broadcast. Unreliable
    /// (positions/ownership change as points are captured).</summary>
    private void SendRadarLinks()
    {
        _radarLinkScratch.Clear();
        if (_world.GameType is XonoticGodot.Common.Gameplay.Onslaught ons)
            ons.CollectRadarLinks(_radarLinkScratch);

        if (_radarLinkScratch.Count == 0 && !_radarLinksWasNonEmpty)
            return; // link-less mode: never spam empty messages
        _radarLinksWasNonEmpty = _radarLinkScratch.Count > 0;

        int count = System.Math.Min(_radarLinkScratch.Count, 255);
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.RadarLinks);
        _scratchWriter.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
        {
            XonoticGodot.Common.Gameplay.Onslaught.RadarLinkSegment seg = _radarLinkScratch[i];
            _scratchWriter.WriteFloat(seg.A.X);
            _scratchWriter.WriteFloat(seg.A.Y);
            _scratchWriter.WriteByte((byte)(seg.TeamA & 0xFF));
            _scratchWriter.WriteFloat(seg.B.X);
            _scratchWriter.WriteFloat(seg.B.Y);
            _scratchWriter.WriteByte((byte)(seg.TeamB & 0xFF));
        }

        foreach (PeerState st in _peers.Values)
            if (st.Accepted && st.Player is not null)
                SendPacket(st.PeerId, _scratchWriter.WrittenSpan, reliable: false);
    }

    private static byte Clamp255(float c) => (byte)System.Math.Clamp((int)(c * 255f), 0, 255);

    /// <summary>QC WaypointSprite_visible_for_player (waypointsprites.qc:982): a waypoint's team/rule/predicate
    /// gate for one viewer. Mirrors the QC control flow: personal-waypoint predicate first, then the SPECTATOR
    /// (sv_itemstime-gated) and the team-restricted DEFAULT branches.</summary>
    private bool WaypointVisible(XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite wp, Player peer)
    {
        // QC: personal waypoints (enemy set) — visible only to that one viewer.
        if (wp.VisibleForPlayer is not null) return wp.VisibleForPlayer(peer);

        // QC IS_PLAYER(view): an in-game client (not an observer/spectator).
        bool isPlayer = !peer.IsObserver;

        // MUTATOR_HOOKFUNCTION(powerups, CustomizeWaypoint) (sv_powerups.qc:61): a flag/objective carrier holding
        // the Invisibility powerup is hidden from ENEMY radar — their waypoint sprite is restricted to their own
        // team. QC: IS_CLIENT(wp.owner) && (viewentity == viewer) && DIFF_TEAM(wp.owner, viewer)
        //          && StatusEffects_active(STATUSEFFECT_Invisibility, wp.owner). Only applies to real players
        // (a spectator is not "the view-entity", so they still see it — matches the QC e==player guard).
        if (wp.Owner is XonoticGodot.Common.Gameplay.Player carrier && isPlayer
            && (int)carrier.Team != (int)peer.Team
            && XonoticGodot.Common.Gameplay.StatusEffectsCatalog.ByName("invisibility") is { } invisDef
            && XonoticGodot.Common.Gameplay.StatusEffectsCatalog.Has(carrier, invisDef))
        {
            return false;
        }

        switch (wp.Rule)
        {
            case XonoticGodot.Common.Gameplay.Waypoints.SpriteRule.Spectator:
            {
                // QC: hidden entirely when sv_itemstime == 0; for live players hidden unless warmup or sv_itemstime == 2.
                float itemstime = _world.Services.Cvars.GetFloat("sv_itemstime");
                if (itemstime == 0f)
                    return false;
                if (!_world.Warmup.WarmupStage && isPlayer && itemstime != 2f)
                    return false;
                return true;
            }
            case XonoticGodot.Common.Gameplay.Waypoints.SpriteRule.Teamplay:
                // QC Draw_WaypointSprite SPRITERULE_TEAMPLAY (waypointsprites.qc:514) is visible to EVERYONE — it
                // never hides on team; it only swaps the IMAGE (own/enemy/spectator, resolved in SendWaypoints via
                // SpriteFor). The only "hide" is when the chosen image is "" (QC: if(spriteimage=="") return). So
                // skip this viewer only when SpriteFor yields an empty image (e.g. a producer left model2 unset).
                return !string.IsNullOrEmpty(wp.SpriteFor((int)peer.Team, !isPlayer));
            default: // SPRITERULE_DEFAULT
                // QC: a team-set Default waypoint is visible only to the same team AND only to live players.
                if (wp.Team != 0)
                {
                    if (wp.Team != (int)peer.Team)
                        return false;
                    if (!isPlayer)
                        return false;
                }
                return true;
        }
    }

    /// <summary>
    /// Drop a player from the server's per-player networking state — the bot-removal counterpart to
    /// <see cref="OnPeerDisconnected"/> (which only fires for real ENet peers). A removed bot leaves
    /// <see cref="GameWorld"/>'s client list (so the next snapshot delta despawns it on clients); this clears the
    /// id/antilag/baseline maps so they don't retain the dead player. Safe to call for any player (no-op if absent).
    /// </summary>
    public void ForgetPlayer(Player p)
    {
        _playerNetIds.TryRemove(p, out _);
        _byPlayer.Remove(p);
        _antilag.Remove(p);
        _lastSnapOrigin.Remove(p);
        _statusBlob.Remove(p);
        _world.Commands.ForgetPlayer(p); // per-client replicated-cvar/autoswitch tables
    }

    /// <summary>Send a reject reason and drop the peer (build mismatch / failed auth / full).</summary>
    private void Reject(int peerId, string reason)
    {
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.HandshakeReject);
        _scratchWriter.WriteString(reason);
        SendPacket(peerId, _scratchWriter.WrittenSpan, reliable: true);
        DisconnectPeer(peerId);
    }

    private void HandleHandshake(int peerId, ref BitReader r)
    {
        if (!_peers.TryGetValue(peerId, out PeerState? st) || st.Accepted)
            return;

        uint clientParity = r.ReadULong();
        st.PendingName = r.ReadString();
        var publicKey = r.ReadBytes(r.ReadUShort()).ToArray(); // the client's identity public key (SPKI)
        uint serverParity = NetProtocol.BuildParity();

        if (clientParity != serverParity)
        {
            // build-parity gate: mismatched content/protocol — reject with a reason and drop.
            Reject(peerId, $"build mismatch (server 0x{serverParity:X8}, client 0x{clientParity:X8})");
            GD.Print($"[ServerNet] peer {peerId} REJECTED: parity {clientParity:X8} != {serverParity:X8}.");
            return;
        }

        // build OK — issue a session-auth challenge (SessionAuth). We admit the player only once it proves
        // ownership of its public identity key by signing this challenge (replaces d0_blind_id, ADR-0011).
        st.PendingPublicKey = publicKey;
        st.AuthChallenge = ServerChallenge.NewChallenge();
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.HandshakeChallenge);
        _scratchWriter.WriteUShort(st.AuthChallenge.Length);
        _scratchWriter.WriteBytes(st.AuthChallenge);
        SendPacket(peerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    private void HandleAuth(int peerId, ref BitReader r)
    {
        if (!_peers.TryGetValue(peerId, out PeerState? st) || st.Accepted || st.AuthChallenge is null || st.PendingPublicKey is null)
            return;

        var signature = r.ReadBytes(r.ReadUShort()).ToArray();
        if (r.BadRead || !ServerChallenge.Verify(st.PendingPublicKey, st.AuthChallenge, signature))
        {
            Reject(peerId, "identity verification failed");
            GD.Print($"[ServerNet] peer {peerId} REJECTED: bad auth signature.");
            return;
        }
        st.IdentityFingerprint = PlayerIdentity.ComputeFingerprint(st.PendingPublicKey);
        st.AuthChallenge = null;

        // admit: create the player on the world's client roster (spawns it via PutClientInServer).
        ClientManager.ClientInfo info = _world.Clients.ClientConnect(isBot: false, netName: string.IsNullOrEmpty(st.PendingName) ? null : st.PendingName);
        st.Player = info.Player;
        // QC .crypto_idfp: the stable identity used to key race/CTS records (anonymous bots keep "").
        info.Player.PersistentId = st.IdentityFingerprint;
        st.NetId = NetIdFor(info.Player);
        st.Accepted = true;
        _byPlayer[info.Player] = st;

        // the client's stable network entity id is a small allocated id (NOT the large ENet peer id). The owner
        // uses it to pick + exclude its own state from the snapshot.
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.HandshakeAccept);
        _scratchWriter.WriteUShort(st.NetId);                     // your net entity id
        _scratchWriter.WriteFloat(1f / SimulationLoopTicRate);     // server tick RATE in Hz (1/dt) for client timing
        _scratchWriter.WriteString(_serverName);                   // server display name (for the client UI)
        // The current map + gametype so a pure remote client can load the BSP itself — rendering the world and
        // building its own prediction collision (NetGame.LoadClientMapFromServer). ClientNet.HandleAccept reads
        // these two strings in this exact order, right after the server name.
        _scratchWriter.WriteString(_world.Services.Cvars.GetString("mapname"));
        _scratchWriter.WriteString(_world.GameType?.RegistryName ?? "dm");
        SendPacket(peerId, _scratchWriter.WrittenSpan, reliable: true);

        // [W14a-QW4] QC ClientInit_misc (server/client.qc:907): right after the accept, send the one-shot per-server
        // gameplay-constant bundle (the welcome/accept handshake leg) so a pure remote client has the constants the
        // hook/arc/trueaim/damagepush/armor/fog paths read.
        SendClientInit(peerId);

        GD.Print($"[ServerNet] peer {peerId} accepted as '{info.Player.NetName}' (netId {peerId}, id {st.IdentityFingerprint[..8]}).");
    }

    private void HandleInputFrame(int peerId, ref BitReader r)
    {
        if (!_peers.TryGetValue(peerId, out PeerState? st) || !st.Accepted || st.Player is null)
            return;

        // The client leads its input frame with the newest snapshot it decoded; that closes the delta loop so
        // the server deltas the next snapshot against a baseline this client provably holds.
        st.SnapHistory.Ack((ushort)r.ReadUShort());

        // ...then the server-time stamp of the snapshot the client is responding to (its LatestServerTime).
        // Measured RTT = receive-time − that stamp — the faithful port of DP's
        // host_client->ping = cmd.receivetime - cmd.time (sv_user.c:847), in the SERVER clock domain on both
        // ends (the snapshot left the server at echoedTime and this input came back now), so there is no
        // client/server clock-offset error. Exponentially smoothed (DP networks a similarly-filtered ping).
        float echoedServerTime = r.ReadFloat();
        if (!r.BadRead && echoedServerTime > 0f)
        {
            float rtt = _world.Time - echoedServerTime;
            if (rtt < 0f) rtt = 0f;                 // clock/order skew → no rewind (antilag_getlag lag<0.001 → 0)
            if (!st.HasRtt) { st.MeasuredRtt = rtt; st.HasRtt = true; }
            else st.MeasuredRtt += (rtt - st.MeasuredRtt) * RttSmoothing; // 1st-order low-pass
        }

        // Per-frame (variable-dt) input mode for this client (see PeerState.PerFrameInput). Client-authoritative
        // for its own input cadence; the server still clamps each command's dt + caps the per-tick budget when it
        // drains. Position on the wire matches ClientNet.SendInput: snapAck, echoedServerTime, THIS flag, count.
        st.PerFrameInput = r.ReadByte() != 0;

        int count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            InputCommand cmd = InputCommand.Deserialize(ref r);
            if (r.BadRead)
                break;
            DbgRecv++;            // net_input_trace diagnostic
            // dedup by seq: the client sends a redundant tail; only enqueue commands we haven't processed and
            // haven't already queued (keep the queue monotonic in seq).
            if (cmd.Seq > st.LastProcessedSeq && cmd.Seq > st.HighestQueuedSeq)
            {
                st.Pending.Enqueue(cmd);
                st.HighestQueuedSeq = cmd.Seq;
                DbgEnqueued++;     // net_input_trace diagnostic
            }
        }
    }

    // =====================================================================================
    //  Input feed (GameWorld.InputProvider)
    // =====================================================================================

    /// <summary>
    /// Supply the next authoritative movement input for <paramref name="p"/> this tick (QC the received move
    /// command). Pops the oldest un-applied <see cref="InputCommand"/> from the client's queue and converts it
    /// to a <see cref="MovementInput"/>; if the queue has starved (packet loss / client hitch) it repeats the
    /// last input so held movement keys keep the player moving rather than stuttering to a stop.
    /// </summary>
    private IMovementInput ProvideInput(Player p)
    {
        if (!_byPlayer.TryGetValue(p, out PeerState? st))
            return ZeroInput;

        // QC applies exactly ONE usercmd per player per server frame; movement (SV_PlayerPhysics) and the weapon
        // driver (W_WeaponFrame) both read that same command. GameWorld calls InputProvider twice per tick — once
        // for movement, once for the weapon frame — so resolve/dequeue only on the FIRST call of a sim tick and
        // return the cached command on the second. (_world.Time is constant within a sim tick and advances each
        // tick, so it keys the per-tick cache.) Otherwise the queue drains at 2x and LastProcessedSeq over-acks
        // inputs whose movement never ran, corrupting predict/reconcile + antilag under input-queue jitter.
        float now = _world.Time;
        if (st.TickInput is not null && st.TickInputTime == now)
            return st.TickInput;

        // Per-frame (variable-dt) client: drain ALL pending commands this tick into the per-command movement batch
        // + a merged once-per-tick input, instead of the legacy one-command-per-tick below.
        if (st.PerFrameInput)
            return ProvideInputPerFrame(st, p, now);

        // Persistent-backlog gentle drain (complements the hard Trim below — it closes the 3–7-deep "dead zone"
        // the trim's >7 trigger leaves untouched). Sample the queue depth's recent MINIMUM over a trailing window;
        // if even the minimum stays above the jitter floor, a client hitch left a STANDING backlog (1-in-1-out
        // preserves it forever = permanent input latency). Bleed ONE command per tick until the floor is reached. A
        // healthy low-fps client drains its bursts to ~empty between packets, so its recent-min is low and it is
        // NEVER bled — only a stuck post-hitch backlog is. A bled command acks + dispatches its impulse unsimulated,
        // exactly the Trim contract (both ends agree the move never ran — a brief reconcile warp, like DP).
        const int backlogWindowTicks = 36; // ~0.5 s at 72 Hz — long enough that a healthy client hits its low min
        st.LegacyMinDepth = Math.Min(st.LegacyMinDepth, st.Pending.Count);
        if (++st.LegacyDepthSamples >= backlogWindowTicks)
        {
            st.LegacyPersistentFloor = st.LegacyMinDepth == int.MaxValue ? 0 : st.LegacyMinDepth;
            st.LegacyMinDepth = int.MaxValue;
            st.LegacyDepthSamples = 0;
        }
        if (st.LegacyPersistentFloor > InputQueuePolicy.LegacyTrimResidual
            && st.Pending.Count > InputQueuePolicy.LegacyTrimResidual)
        {
            InputCommand bleed = st.Pending.Dequeue();
            if (bleed.Seq > st.LastProcessedSeq)
                st.LastProcessedSeq = bleed.Seq;       // ack unsimulated → client stops replaying it
            if (bleed.Impulse != 0 && !p.IsObserver)
                _world.Commands.Execute($"impulse {bleed.Impulse}", isServerConsole: false, caller: p);
            st.LegacyPersistentFloor--;                // account for the bleed so we drain ~1 command/tick to the floor
            if (Log.WillTrace)
                Log.Trace($"[ServerNet] standing input backlog bled for '{p.NetName}': now {st.Pending.Count} (post-hitch latency drain)");
        }

        // LEGACY queue bound (DP sv_clmovement_inputtimeout). Producer and consumer both pace at exactly
        // 72 Hz, so the queue length never shrinks on its own — a client hitch's burst (up to 18 commands
        // per long frame, while the sim's B3 soft-cap + spiral guard run/drop FEWER catch-up ticks) would
        // otherwise become PERMANENT input latency (fire/jump executing queue-length ticks late for the
        // rest of the match, compounding with every further hitch — the felt "projectile leaves 0.5 s after
        // the click"). Past the abnormal-depth trigger, discard the OLDEST commands down to a small jitter
        // floor: dropped seqs still ack (client prediction stops replaying them — both ends agree the
        // movement never happened, the same brief reconcile warp DP shows), and their one-shot impulses
        // still dispatch so a hitch can't eat a weapon switch.
        if (st.Pending.Count > InputQueuePolicy.MaxLegacyQueuedCommands)
        {
            _droppedImpulseScratch.Clear();
            int before = st.Pending.Count;
            uint highestDropped = InputQueuePolicy.Trim(st.Pending,
                InputQueuePolicy.MaxLegacyQueuedCommands, InputQueuePolicy.LegacyTrimResidual,
                _droppedImpulseScratch);
            if (highestDropped > st.LastProcessedSeq)
                st.LastProcessedSeq = highestDropped;
            if (!p.IsObserver)
                foreach (int imp in _droppedImpulseScratch)
                    _world.Commands.Execute($"impulse {imp}", isServerConsole: false, caller: p);
            if (Log.WillTrace)
                Log.Trace($"[ServerNet] input backlog trimmed for '{p.NetName}': {before} -> {st.Pending.Count} " +
                          "(client hitch burst; surplus consumed unsimulated)");
            Prof.Event($"net: input backlog trimmed {before} -> {st.Pending.Count} ({p.NetName})");
        }

        InputCommand cmd;
        if (st.Pending.Count > 0)
        {
            cmd = st.Pending.Dequeue();
            st.Last = cmd;
            st.HasLast = true;
            st.LastProcessedSeq = cmd.Seq;     // ack cursor: highest input we've applied

            // C2S impulse (QC usercmd.impulse → ImpulseCommands): a fresh command may carry a one-shot weapon
            // switch/reload number. Dispatch it ONCE, here on dequeue (this is the FIRST ProvideInput call of the
            // tick — the second call returns the cached TickInput above, so it never re-runs), through the GATED
            // impulse command path (Commands.CmdImpulse → DispatchImpulse keeps the game_stopped / timeout /
            // round-not-started guards faithful) → WeaponImpulses.Handle. Then ZERO it on st.Last so the
            // starve-repeat below (which reuses st.Last when the queue drains) doesn't re-fire it every starved
            // tick — exactly QC's CS(this).impulse = 0 (impulse.qc:377). The Seq-dedup already drops the
            // redundant input tail, so this processes the impulse exactly once.
            //
            // T44: for an OBSERVER (spectator free-flight) the impulse is NOT a weapon command — the speed-step
            // impulses (1-19/200-209/220-229) drive the spectator-speed ladder in PlayerPhysics.SpectatorControl,
            // which reads it off the MovementInput. So skip the weapon dispatch for observers and let the impulse
            // ride into ToMovementInput(cmd) below (cmd keeps it; only st.Last is cleared). In QC the spectator
            // branch handles the impulse BEFORE weapon impulses for non-players (physics.qc:58-62), and weapon
            // impulses don't apply to a spectator anyway. We still ZERO st.Last so the starve-repeat doesn't
            // re-step the ladder every starved tick (QC's CS(this).impulse = 0 after spectator_control consumes it).
            if (cmd.Impulse != 0)
            {
                if (!p.IsObserver)
                    _world.Commands.Execute($"impulse {cmd.Impulse}", isServerConsole: false, caller: p);
                InputCommand last = st.Last;
                last.Impulse = 0;
                st.Last = last;
            }
        }
        else if (st.HasLast)
        {
            cmd = st.Last;                      // starved: repeat the last input (held keys keep moving)
        }
        else
        {
            st.TickInput = ZeroInput;
            st.TickInputTime = now;
            return ZeroInput;
        }

        IMovementInput resolved = ToMovementInput(cmd);

        // +use rising edge (QC PlayerUseKey, client.qc): board/exit a vehicle on the RELEASED->PRESSED edge of
        // the +use button. PlayerUseKey is edge-driven in DP (fired once per press), so detecting the edge here
        // — once per sim tick, since this is the FIRST ProvideInput call of the tick (the cached-return above
        // guards the W_WeaponFrame second call) — keeps a HELD +use from boarding then immediately exiting then
        // re-boarding. Routed to the server-authoritative VehicleBoarding.UseKey (Common-side, headless).
        bool useDown = resolved.ButtonUse;
        if (useDown && !st.UsePrevDown)
            UseKeyEdge(p);
        st.UsePrevDown = useDown;

        st.TickInput = resolved;
        st.TickInputTime = now;
        return resolved;
    }

    // Per-frame (variable-dt) guardrails. The client already clamps each command's dt to the SAME bounds before
    // stamping it (NetGame.MinInputDt/MaxInputDt) so re-clamping here is a no-op for an honest client and keeps
    // client prediction in lockstep; it only tames a buggy/cheating client. The per-tick budget caps how much
    // simulated time one server tick may drain (drain the rest next tick) so a flooded queue can't fast-forward.
    private const float PerFrameMinCommandDt = 0.0005f;
    private const float PerFrameMaxCommandDt = 0.05f;
    // Diagnostic counters surfaced by net_input_trace (see NetGame [nettrace]). Cumulative, all peers: DbgRecv =
    // input commands deserialized off the wire; DbgEnqueued = passed the seq-dedup into a per-peer queue; DbgBatch =
    // drained into a movement batch. recv≫enq ⇒ heavy redundancy or dup; enq≫batch ⇒ queue not draining. Single
    // increments on the hot path — negligible; left always-on so the trace reads true the instant it's enabled.
    public int DbgRecv, DbgEnqueued, DbgBatch;

    private const float PerFrameTickBudget = 0.25f;

    /// <summary>
    /// Per-frame (variable-dt) input drain — DP's process-queued-client-moves. Dequeues ALL pending commands for
    /// this tick (up to <see cref="PerFrameTickBudget"/> simulated seconds), building <see cref="PeerState.TickBatch"/>
    /// (one movement input per command, integrated with its own clamped dt by OnClientMove) AND the MERGED
    /// once-per-tick input the single-call readers use: the LATEST command's angles/wish-move with the OR of every
    /// drained command's buttons (so a fire pressed in ANY sub-frame reaches this tick's weapon driver). Impulses
    /// dispatch per command (rapid weapon switches); the +use edge + starve-repeat mirror the legacy path.
    /// </summary>
    private IMovementInput ProvideInputPerFrame(PeerState st, Player p, float now)
    {
        st.TickBatch.Clear();
        InputButtons mergedButtons = InputButtons.None;
        InputCommand last = default;
        bool any = false;
        float consumed = 0f;

        while (st.Pending.Count > 0 && consumed < PerFrameTickBudget)
        {
            InputCommand cmd = st.Pending.Dequeue();
            // Clamp this command's dt server-side (untrusted client); the client clamps to the same bounds, so an
            // honest command is unchanged → prediction stays in lockstep.
            float dt = cmd.DeltaTime;
            dt = dt < PerFrameMinCommandDt ? PerFrameMinCommandDt : (dt > PerFrameMaxCommandDt ? PerFrameMaxCommandDt : dt);
            cmd.DeltaTime = dt;
            consumed += dt;

            st.Last = cmd;
            st.HasLast = true;
            st.LastProcessedSeq = cmd.Seq;

            // Impulse: dispatch once per command (the enqueue Seq-dedup already drops the redundant tail), then zero
            // it on st.Last so the starve-repeat can't re-fire it — exactly the legacy path, per command. Observers
            // keep the impulse on the dequeued cmd (it rides the batch entry into SpectatorControl's speed ladder).
            if (cmd.Impulse != 0)
            {
                if (!p.IsObserver)
                    _world.Commands.Execute($"impulse {cmd.Impulse}", isServerConsole: false, caller: p);
                InputCommand z = st.Last; z.Impulse = 0; st.Last = z;
            }

            st.TickBatch.Add(ToMovementInput(cmd));
            DbgBatch++; // net_input_trace diagnostic
            mergedButtons |= cmd.TypedButtons;
            last = cmd;
            any = true;
        }

        if (!any)
        {
            // No queued command this world tick. This is EITHER a soft-capped EXTRA tick within a render frame (the
            // frame's real commands already drained on its first tick) OR a genuinely starved frame (client hitch /
            // packet loss). In BOTH cases the player must NOT advance on a FABRICATED command. The old "starve-
            // repeat" stepped the player on these ticks, advancing it MORE than the client predicted — and below the
            // sim rate that happens on every render frame, desyncing client vs server every frame (the catharsis
            // rubberband; see ListenServerDiagnosisTests). Path B = COMMAND-DRIVEN movement: leave the batch EMPTY
            // so OnClientMove runs ZERO moves; the player advances only by real commands, in lockstep with the
            // client (which also only predicted real commands). The held state still drives the once-per-tick MERGED
            // input below (view angles + weapon frame continue) — only the MOVEMENT is withheld.
            if (st.HasLast)
            {
                last = st.Last;
                mergedButtons = st.Last.TypedButtons;
                // Intentionally NOT adding to st.TickBatch — no fabricated movement step (the Path B fix).
            }
            else
            {
                st.TickInput = ZeroInput;
                st.TickInputTime = now;
                return ZeroInput; // never had input → nothing to drive the weapon frame either
            }
        }

        // MERGED once-per-tick input: the latest command's angles/wish-move (freshest aim + intent) carrying the OR
        // of every drained command's buttons, at the tick frametime (the weapon driver/regen run once per tick).
        // Drives p.Angles, the dead-respawn buttons, the vehicle input and W_WeaponFrame.
        InputCommand mergedCmd = last;
        mergedCmd.Buttons = (int)mergedButtons;
        mergedCmd.Impulse = 0;
        mergedCmd.DeltaTime = SimulationLoopTicRate;
        IMovementInput merged = ToMovementInput(mergedCmd);

        // +use rising edge from the merged state (once per tick, like the legacy path).
        bool useDown = merged.ButtonUse;
        if (useDown && !st.UsePrevDown)
            UseKeyEdge(p);
        st.UsePrevDown = useDown;

        st.TickInput = merged;
        st.TickInputTime = now;
        return merged;
    }

    /// <summary>
    /// The +use rising-edge dispatcher (QC PlayerUseKey, client.qc): the engine fires PlayerUseKey once per press
    /// and every interested mutator/gametype hooks it. We run the vehicle board/exit hook first (matching the
    /// shipped order), then — if no vehicle consumed the key — the active gametype's +use hook. CTF's hook
    /// (Ctf.HandleUseKey) throws the carried flag (g_ctf_throw) or requests a pass from the nearest carrier
    /// (g_ctf_pass_request); the roster is the live player list it scans to find a carrier to request from.
    /// </summary>
    private void UseKeyEdge(Player p)
    {
        VehicleBoarding.UseKey(p);
        if (_world.GameType is Ctf ctf)
            ctf.HandleUseKey(p, _world.Clients.Players);
    }

    /// <summary>
    /// The per-command movement batch drained for <paramref name="p"/> THIS tick (per-frame mode) — one entry per
    /// real client command, each integrated with its own dt by OnClientMove. In per-frame mode this is returned
    /// even when EMPTY (a soft-capped extra tick / a starved frame): an empty batch means "the player runs ZERO
    /// moves this tick" (Path B command-driven movement — no fabricated starve-repeat), which is distinct from the
    /// LEGACY/no-net case (null → OnClientMove runs the single merged InputProvider command). Built by
    /// <see cref="ProvideInputPerFrame"/>, which OnClientMove triggers via its first InputProvider call.
    /// </summary>
    private IReadOnlyList<IMovementInput>? TickMovementBatch(Player p)
    {
        if (!_byPlayer.TryGetValue(p, out PeerState? st))
            return null;
        if (!st.PerFrameInput || st.TickInputTime != _world.Time)
            return null; // legacy / not drained this tick → caller runs the single merged command
        return st.TickBatch; // per-frame: the real commands (possibly empty → zero moves, never fabricated)
    }

    // Boxed once: typed as the interface so the hot ProvideInput paths return/store the same object instead of
    // re-boxing the struct on every assignment to an IMovementInput-typed field/return.
    private static readonly IMovementInput ZeroInput = new MovementInput { FrameTime = SimulationLoopTicRate };

    // Scratch for the legacy input-queue trim (one-shot impulses carried by dropped commands); ProvideInput
    // runs on the sim thread only, so one shared buffer is safe.
    private readonly List<int> _droppedImpulseScratch = new(4);

    private static MovementInput ToMovementInput(in InputCommand c)
    {
        InputButtons b = c.TypedButtons;
        return new MovementInput
        {
            ViewAngles = c.ViewAngles,
            // Rescale the normalized (±1) wish-move to wish-velocity units via the fixspeed value a live Base
            // client runs with — max(sv_maxspeed, sv_maxairspeed) = 360 stock (sys_phys_fixspeed stuffcmd; see
            // WishMoveScaling). MUST use the SAME scaling the client predictor (EntityMovementStep) uses or
            // prediction and authority disagree.
            MoveValues = WishMoveScaling.Scale(c.Forward, c.Side, c.Up),
            FrameTime = c.DeltaTime > 0f ? c.DeltaTime : SimulationLoopTicRate,
            ButtonJump = (b & InputButtons.Jump) != 0,
            ButtonCrouch = (b & InputButtons.Crouch) != 0,
            ButtonAttack1 = (b & InputButtons.Attack) != 0,
            ButtonAttack2 = (b & InputButtons.Attack2) != 0,
            ButtonUse = (b & InputButtons.Use) != 0,
            // PHYS_INPUT_BUTTON_HOOK — the +hook / offhand-fire button, driving the offhand-weapon think
            // (grapple hook, offhand blaster, nade prime/throw) in WeaponFireDriver.Frame.
            ButtonHook = (b & InputButtons.Hook) != 0,
            // PHYS_INPUT_BUTTON_ZOOM — the +zoom bind. The rifle reads it (via the slot's ButtonZoom mirror)
            // to re-aim a scoped shot straight from the eye (rifle.qc:16-20). The wire bit is already sent by
            // the client (NetGame.cs InputButtons.Zoom); this is the missing server-side decode.
            ButtonZoom = (b & InputButtons.Zoom) != 0,
            // PHYS_INPUT_BUTTON_CHAT — the client is typing (chat prompt / console open). The client sends this
            // bit even while it has otherwise gone input-inactive (movement zeroed), so the typing flag actually
            // reaches the server. PlayerPhysics writes it onto player.ButtonChat, which the camp-check typecheck
            // gate, the type-frag classifier, spawn-near-teammate and monster-typefrag all consult.
            Typing = (b & InputButtons.Chat) != 0,
            // Carry the one-shot impulse (QC CS(this).impulse) so the spectator free-flight speed ladder
            // (PlayerPhysics.SpectatorControl) sees it. For a live PLAYER the impulse was already dispatched as
            // a weapon command in ProvideInput; PlayerPhysics' spectator branch is gated on IsObserver, so this
            // value is simply ignored by the player movement path (no double-processing). For an OBSERVER
            // ProvideInput skips that weapon dispatch, so the speed-step impulse (1-19/200-229) survives here.
            Impulse = c.Impulse,
        };
    }

    // =====================================================================================
    //  Snapshot broadcast
    // =====================================================================================

    /// <summary>
    /// Send one snapshot per accepted client. Each snapshot carries: the server time, the recipient's acked
    /// input seq, their own full-precision authoritative state (the reconcile seed) + the movevars block when the
    /// physics changed, then a <b>delta-compressed</b> entity section (only spawned/changed/removed entities
    /// since this client's last-acked snapshot — <see cref="ServerSnapshotHistory"/>). The owner's own entity is
    /// excluded (the client predicts it). The entity set is built once per tick + the teleport bit detected.
    /// </summary>
    private void BroadcastSnapshots()
    {
        float now = _world.Time;

        // antilag config for this frame (antilag.qc / world.qc EndFrame). g_antilag default 2 (xonotic-server.cfg)
        // — when the cvar is unset GetString is empty, so we keep the stock-enabled default; an explicit 0
        // disables server-side rewind (antilag_getlag). g_antilag_nudge (default 0) tunes the record timestamp.
        string antilag = _world.Services.Cvars.GetString("g_antilag");
        _antilagMode = string.IsNullOrEmpty(antilag) ? 2 : (int)_world.Services.Cvars.GetFloat("g_antilag");
        _antilagNudge = _world.Services.Cvars.GetFloat("g_antilag_nudge");

        BuildEntitySet(now);

        // movevars (the prediction-relevant sv_* set): recomputed once; sent per client only when its hash changes.
        _moveVars = MoveVarsBlock.Capture(_world.Services.Cvars);
        _moveVarsHash = MoveVarsBlock.Hash(_moveVars);

        // scoreboard: mirror the authoritative team totals into the score table so its Version reflects team
        // changes too, then snapshot every player's networked columns + the team totals once (same to all).
        BuildScoreboard();

        _snapshotSeq++;
        if (_snapshotSeq == 0) _snapshotSeq = 1; // 0 is the "no baseline" sentinel

        // (§12.8) DP sv_cullentities_pvs: read once per broadcast — the per-recipient PVS filter is applied below.
        bool cullEntitiesPvs = _world.Services.Cvars.GetFloat("sv_cullentities_pvs") != 0f;

        // ent_cs enemy-privacy mask (common/ent_cs.qc:_entcs_send ENTCS_PUBLICMASK gating). Read the two server-side
        // globals the gate depends on ONCE per broadcast: `radar_showenemies` (a server global Race forces true and
        // Nexball mirrors from g_nexball_radar_showallplayers — when set, every player's private fields go to
        // everyone) and `teamplay` (SAME_TEAM is `teamplay ? a.team==b.team : a==b`). When neither holds, a live
        // player's private ent_cs fields (origin/angles/health/armor) are stripped for enemy recipients below.
        bool radarShowEnemies = _world.Services.Cvars.GetFloat("radar_showenemies") != 0f;
        bool teamplay = _world.GameType?.TeamGame ?? false;

        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is null)
                continue;

            Player owner = st.Player;
            _snapshotWriter.Reset();
            _snapshotWriter.WriteByte((byte)NetControl.Snapshot);
            _snapshotWriter.WriteFloat(now);
            _snapshotWriter.WriteULong(st.LastProcessedSeq);       // ack: the last input we ran for this client

            // owner-replicated state (full precision — aim/position fidelity matters for prediction).
            _snapshotWriter.WriteUShort(st.NetId);                 // owner net id (echo, lets the client confirm)
            WriteOwnerState(_snapshotWriter, owner, st);

            // movevars: send the block only when this client's physics is stale (steady-state = one bool).
            bool sendMoveVars = st.LastMoveVarsHash != _moveVarsHash;
            _snapshotWriter.WriteBool(sendMoveVars);
            if (sendMoveVars)
            {
                MoveVarsBlock.Serialize(_snapshotWriter, _moveVars);
                st.LastMoveVarsHash = _moveVarsHash;
            }

            // [v7] per-peer preset-RESOLVED physics (T54 — QC Physics_ClientOption, player.qc:18-42): when this
            // client's g_physics_clientselect resolution deviates from the global block, send the resolved
            // vector (hash-gated like the movevars block); when the deviation ENDS (preset back to default /
            // clientselect turned off) send ONE count-0 block so the client clears its PredictionOverride.
            // Steady state either way is a single false bool. The same vector drives the server sim via
            // MovementParameters.PresetProvider (ResolvePresetVector), so authority and prediction agree.
            {
                float[]? resolved = ResolvePresetVector(owner);
                uint resolvedHash = resolved is null ? 0u : MoveVarsBlock.Hash(resolved);
                bool sendResolved = st.LastResolvedVarsHash != resolvedHash;
                _snapshotWriter.WriteBool(sendResolved);
                if (sendResolved)
                {
                    MoveVarsBlock.Serialize(_snapshotWriter, resolved ?? System.Array.Empty<float>());
                    st.LastResolvedVarsHash = resolvedHash;
                    if (_world.Services.Cvars.GetFloat("developer") != 0f)
                        GD.Print($"[ServerNet] resolved physics block → {owner.NetName}: "
                                 + (resolved is null ? "cleared (back to global)" : $"hash 0x{resolvedHash:X8}"));
                }
            }

            // ScoreInfo (the per-mode label/flag layout + gametype/teamplay): send the block only when the layout
            // changed since this client last got it — a gametype/mode switch (else one bool). MUST precede the
            // scoreboard block so the client applies the labels BEFORE deserializing the per-player columns, or the
            // first post-switch scoreboard frame would be dropped on a layout-hash mismatch (QC sends ScoreInfo on
            // its own linked entity; here both ride the snapshot, so order is the guarantee). Always sent on the
            // first snapshot to a client (SentScoreInfo=false) so a remote --connect client gets the layout even if
            // it equals the default hash.
            bool sendScoreInfo = !st.SentScoreInfo || st.LastScoreInfoHash != _scoreInfoHash;
            _snapshotWriter.WriteBool(sendScoreInfo);
            if (sendScoreInfo)
            {
                XonoticGodot.Net.ScoreInfoBlock.Serialize(_snapshotWriter);
                st.LastScoreInfoHash = _scoreInfoHash;
                st.SentScoreInfo = true;
            }

            // scoreboard: send the block only when a score changed since this client last got it (one bool
            // otherwise). The block also carries THIS viewer's item-pickup tally (QC's per-viewer Inventory
            // entity), which changes independently of any score — so the gate covers both.
            CollectItemStats(owner, _itemStatsScratch, out uint itemStatsHash);
            bool sendScores = st.LastScoreVersion != _scoreVersion || st.LastItemStatsHash != itemStatsHash;
            _snapshotWriter.WriteBool(sendScores);
            if (sendScores)
            {
                XonoticGodot.Net.ScoreboardBlock.Serialize(_snapshotWriter, _scoreRows, _scoreTeams, _scoreRankings,
                    _scoreSpeedAward, _itemStatsScratch);
                st.LastScoreVersion = _scoreVersion;
                st.LastItemStatsHash = itemStatsHash;
            }

            // Gametype status (T53): the per-mode round/objective HUD stats — QC STAT(REDALIVE..PINKALIVE)
            // (CA/FT), STAT(OBJECTIVE_STATUS) (the KH key pack), eliminatedPlayers (scoreboard grey-out) and
            // survivalStatuses (role + hunter disclosure). The block is PERSONALIZED per recipient (the KH
            // "31 = self" slot, the SURV hunter visibility), so serialize per peer into a scratch and hash-gate
            // (steady-state cost = one bool; QC's equivalent is stat delta-compression + linked-entity SendFlags).
            _modeStatusScratch.Reset();
            bool haveModeStatus = XonoticGodot.Net.GametypeStatusBlock.Capture(
                _modeStatusScratch, _world.GameType, owner, _world.Clients.Players, NetIdFor,
                roundStarted: _world.Rounds is { IsRoundStarted: true });
            uint modeStatusHash = haveModeStatus ? XonoticGodot.Net.GametypeStatusBlock.Hash(_modeStatusScratch.WrittenSpan) : 0u;
            bool sendModeStatus = haveModeStatus && st.LastModeStatusHash != modeStatusHash;
            _snapshotWriter.WriteBool(sendModeStatus);
            if (sendModeStatus)
            {
                _snapshotWriter.WriteBytes(_modeStatusScratch.WrittenSpan);
                st.LastModeStatusHash = modeStatusHash;
            }

            // [T57 accuracy] — the owner's per-weapon accuracy byte array (QC ENT_CLIENT_ACCURACY, owner-only)
            // is a PER-OWNER payload, so it is written/read at the END of the owner block (WriteOwnerState's
            // append-at-END slot), NOT here in the broadcast entity-shared region.

            // delta-compressed entity section (everyone but the recipient's own entity). DP-faithful per-client
            // PVS cull (sv_cullentities_pvs): out-of-PVS entities are dropped from THIS client's set and the
            // delta encoder removes them against its baseline (re-spawning them when they return into view).
            IReadOnlyDictionary<int, NetEntityState> sendSet =
                RelevantEntitiesFor(st, owner, st.NetId, cullEntitiesPvs, radarShowEnemies, teamplay);
            // v14: the recipient's OWN entity is included (it used to be excluded via excludeEntNum). The client
            // never interpolates it — ClientNet.HandleSnapshot diverts it into LocalState, the decoded own-entity
            // slice every "watched == self" consumer reads (viewmodel anim frame/colors, vortex glow, powerup
            // status timers, hitsound damage diff, the own name tag). Matches Base, where CSQC receives the local
            // player's own entity too.
            st.SnapHistory.EncodeSnapshot(_snapshotWriter, sendSet, _snapshotSeq);

            // Snapshots are normally unreliable (latest-wins, loss-tolerant), but the FIRST frame to a client is a
            // full baseline of every networked entity (all of the map's static items at once) and exceeds the
            // unreliable MTU until the client's first ack lets the server delta-compress against it. An oversized
            // unreliable packet is lossy (fragmented, any lost fragment drops the whole frame) — so a dropped
            // baseline would leave the client missing static items until they next change (rare). Promote any
            // over-MTU snapshot to the reliable channel (ENet fragments it losslessly); the client dispatches by
            // the leading control byte regardless of channel, so this is transparent. Steady-state deltas stay
            // unreliable, so this adds no head-of-line blocking once the first ack shrinks the frame.
            ReadOnlySpan<byte> snapshot = _snapshotWriter.WrittenSpan;
            SendPacket(st.PeerId, snapshot, reliable: snapshot.Length > NetProtocol.MaxUnreliablePayload);
        }
    }

    /// <summary>The memoized "&lt;classname&gt; &lt;netname&gt;" catalog key a model-less projectile networks in its
    /// (otherwise unused) model field so the client's <c>ProjectileCatalog</c> picks the right type. Cached per
    /// (classname, netname) so the per-tick entity build allocates no interpolated string (3.2-4).</summary>
    private string ProjectileCatalogKey(string className, string netName)
    {
        if (string.IsNullOrEmpty(netName))
            return className;
        var key = (className, netName);
        if (!_projKeyCache.TryGetValue(key, out string? combined))
        {
            combined = $"{className} {netName}";
            _projKeyCache[key] = combined;
        }
        return combined;
    }

    /// <summary>
    /// Build this tick's networked entity set (keyed by net id) from the player roster, stamping the teleport
    /// bit on any entity whose one-tick origin jump exceeds <see cref="TeleportTickDistance"/> (a teleport or
    /// respawn, which the client must snap to rather than lerp across). Reused dictionary — no per-tick alloc.
    /// </summary>
    private void BuildEntitySet(float now)
    {
        _entityScratch.Clear();
        _entityBounds.Clear();
        _scratchHasOwnerPrivate = false;

        // QC world.qc:EndFrame records antilag history at `altime` (time + frametime*(1+g_antilag_nudge)), NOT at
        // the bare current time — so the ring aligns with the time the client will see this frame. Compute it once.
        float altime = LagCompensation.RecordTime(now, SimulationLoopTicRate, _antilagNudge);

        IReadOnlyList<Player> players = _world.Clients.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];

            // An OBSERVER (connected but not yet joined) has no live body — it's never spawned, so it would
            // otherwise be networked as a ghost player at the world origin. QC only puts live players in the
            // CSQCModel player stream; skip observers here so no client sees a phantom at (0,0,0).
            if (p.IsObserver)
                continue;

            int netId = NetIdFor(p); // humans AND bots get a stable id (bots aren't in _byPlayer)

            // Detect a teleport/respawn (a one-tick origin jump past the threshold) ONCE, up front — it both flags
            // the snapshot (so the client snaps instead of lerping) and, faithful to PutPlayerInServer's
            // antilag_clear(this, CS(this)) (client.qc:858), wipes this player's lag-comp ring so the next shot
            // can't rewind a freshly-spawned/teleported player back to their corpse for ~0.4s.
            bool teleported = _lastSnapOrigin.TryGetValue(p, out NVec3 prev)
                              && (p.Origin - prev).Length() > TeleportTickDistance;

            // [sv-antilag.clear.on_spawn] Explicit antilag_clear request from the spawn/teleport/vehicle-board
            // lifecycle (PutPlayerInServer sets Entity.AntilagNeedsClear, port of PutClientInServer's
            // antilag_clear, client.qc:858). This catches a (re)spawn/teleport that lands WITHIN the
            // TeleportTickDistance heuristic's threshold of the previous origin — which the jump test alone
            // would miss, leaving stale history that could rewind a fresh player toward its corpse. One-shot.
            bool explicitClear = p.AntilagNeedsClear;
            p.AntilagNeedsClear = false;

            // record the position history for lag compensation (antilag_record(it, CS(it), altime)).
            if (!_antilag.TryGetValue(p, out AntilagBuffer? hist))
            {
                hist = new AntilagBuffer();
                _antilag[p] = hist;
            }
            if (teleported || explicitClear)
            {
                hist.Clear();                 // antilag_clear: drop pre-teleport history before recording the new pos
                _lastSnapOrigin.Remove(p);    // forget the pre-jump origin so the post-jump baseline is this frame's
            }
            hist.Store(altime, p.Origin);

            // [W14b LI1/LI3] resolve + expire the upper-body animdecide action for the wire. QC's
            // animdecide_getupperanim runs per frame on the CLIENT; the port decides server-side (the set-site is the
            // weapon-fire commit) and networks the EXPIRY-RESOLVED action so the client renders a stable contract: an
            // ACTIVE action while its window (start + numframes/framerate, e.g. SHOOT = 0.2s) hasn't elapsed, else
            // None. AnimActionTime is the action's start time (the death_time-class raw-float client-observed divergence).
            var (resolvedAction, _) = AnimDecideUnit.GetUpperAnim(
                p.AnimUpperAction, p.AnimActionStart, now);

            var s = new NetEntityState
            {
                EntNum = netId,
                Kind = NetEntityKind.Player,
                ModelIndex = p.ModelIndex,
                Frame = (int)p.Frame,
                Skin = (int)p.Skin,
                Origin = p.Origin,
                Angles = p.Angles,
                // The remote player's velocity drives the client-side locomotion pick (LocomotionBlend.SelectLegs
                // → run/walk/idle) for the skeletal PlayerModel — exactly as CSQC's animdecide derives walk/run
                // from the networked velocity. Without it every remote velocity reads 0 and (combined with the
                // OnGround flag below) the bot is frozen in a single pose. (QC csqcmodel networks .velocity.)
                Velocity = p.Velocity,
                Health = (int)p.Health,
                Armor = (int)p.ArmorValue, // [T68] networked entcs armor → teammate shownames armor sub-bar
                Alpha = QuantizeAlpha(p.Alpha), // [W1-alpha-net] Cloaked/RunningGuns/Invisibility player transparency
                // Networked colormap is the player's TEAM only — never the Survival role. Survival's green-prey/
                // red-hunter override is applied CLIENT-side (ClientWorld.ResolveForcedColormap, from the disclosed
                // SurvivalHunterIds set) so it honors the per-recipient hunter-disclosure gate; stamping the role
                // into this shared snapshot byte would leak hunter identity to prey mid-round (cl_survival.qc derives
                // e.colormap from the disclosure-gated survival_status, not server-side).
                Colormap = (int)p.Team,
                // [r15 #43] QC entcs clientcolors: the packed 16*shirt+pants palette colors — FFA profile color
                // (the `color` client command / _cl_color) or the team-forced 17*teamcode (SetPlayerColors).
                // Drives the _shirt/_pants mask tint + glowmod on the player body AND their weapon models.
                Colors = p.ClientColors & 0xFF,
                // QC wepent: the remote third-person held weapon model follows the EXTERIOR weapon entity's
                // weaponname (CL_ExteriorWeaponentity_Think → w_ent.weaponname), which is the weapon slot's
                // ACTIVE/RAISING weapon (m_weapon) — set only at the WS_CLEAR→WS_RAISE transition in the switch
                // state machine, NOT the switch target. The port's p.ActiveWeaponId is the immediate switch
                // mirror (Inventory.SwitchWeapon sets it at once), so during the drop window it would jump the
                // remote's held model to the new weapon before the old one finished lowering. Network the slot-0
                // CurrentWeaponId (the weapon actually held this tick, post-lower) so the remote model snaps to
                // the new weapon at the same moment Base's exterior model does — the held weapon raise/lower
                // transition is reproduced. Falls back to ActiveWeaponId before the slot is first driven (a fresh
                // spawn / a non-weapon-sim entity whose CurrentWeaponId is still -1).
                Weapon = HeldWeaponId(p),
                Model = p.Model,           // QC .model (playermodel) — the client loads the skeletal IQM by name
                Flags = (p.OnGround ? NetEntityFlags.OnGround : 0)
                      | (p.IsDead ? NetEntityFlags.Dead : 0)
                      | (p.IsDucked ? NetEntityFlags.Crouched : 0) // QC FL_DUCKED → remote crouch anim/hull
                        // QC common/physics/player.qc:878 — csqcmodel_modelflags |= MF_ROCKET when
                        // (IT_USING_JETPACK && !IS_DEAD && !intermission). Networked so the client re-derives
                        // MF_ROCKET (rocket trail + looping jetpack-fly sound) for this player.
                      | (p.UsingJetpack && !p.IsDead && !_world.Intermission.Running ? NetEntityFlags.UsingJetpack : 0),
                // [T41/T46] the Feedback stats the client diffs/draws (view.qc HitSound + objective rings):
                //   - HitDamageDealtTotal: cumulative damage dealt — the client diffs it to fire the hit sound.
                //   - NadeTimer: 0..1 held-nade charge — drives the nade objective ring.
                //   - ReviveProgress: 0..1 Freeze-Tag thaw — drives the thaw ring (mirrored from FrozenState).
                // CaptureProgress is intentionally left 0: the stat is networked + rendered but no gametype sets
                // it (matches Base QC — an unfinished CTF/Assault ring producer; see NetEntity.cs).
                HitDamageDealtTotal = p.HitsoundDamageDealtTotal,
                NadeTimer = p.NadeTimer,
                ReviveProgress = p.ReviveProgress,
                // [W-nadeclient] owner-only nade STATs, carried in the SAME EntityField.Feedback block (appended after
                // the four ring floats in NetEntity.cs). NadeDarknessTime = QC STAT(NADE_DARKNESS_TIME) (absolute
                // expiry — the client computes remaining = NadeDarknessTime - now to fade the darkness overlay);
                // NadeBonus/NadeBonusType/NadeBonusScore = the banked-bonus count/type-id/progress driving the
                // ammo-panel nade-bonus counter + bonus-progress bar. All 0 on a fresh/no-nade player → the Feedback
                // bit stays clear; the privacy gate already strips this whole block for non-owner viewers (it's owner-
                // only state, never networked to remotes — see the WepentView/owner-block precedent).
                NadeDarknessTime = p.NadeDarknessTime,
                NadeBonus = p.NadeBonus,
                NadeBonusType = p.NadeBonusType,
                NadeBonusScore = p.NadeBonusScore,
                // [hitsound] QC STAT(HIT_TIME)/STAT(TYPEHIT_TIME)/STAT(KILL_TIME): the EndFrame-flushed hit-feedback
                // times (exactly one advances per frame, typehit > kill > hit). The client HitSound plays the
                // matching sound per advance; HitDamageDealtTotal above carries the pitch-driving damage diff.
                HitTime = p.HitTime,
                TypeHitTime = p.TypeHitTime,
                KillTime = p.KillTime,
                // [W14b LI1] the animdecide upper-body action overlay (SHOOT this wave) + its start time. The server
                // latches the action at the weapon-fire commit (WeaponFireGate) and networks the expiry-resolved id
                // (resolvedAction above): the client plays it as a torso overlay over the velocity-derived legs (LI3).
                // None (idle) keeps the EntityField.AnimAction bit clear (Diff) so a not-shooting player costs nothing.
                UpperAction = (byte)resolvedAction,
                AnimActionTime = p.AnimActionStart,
                // [W14a-wepent] the exterior-weapon block (QC common/wepent.qh) populated from the live slot-0 weapon
                // state machine — the remote third-person held weapon's switch/transparency/skin.
                SwitchWeapon = p.WeaponState(new WeaponSlot(0)).SwitchWeaponId,
                SwitchingWeapon = p.WeaponState(new WeaponSlot(0)).SwitchingWeaponId,
                // QC the slot's WS_* .state compressed to the render-relevant phase: WS_RAISE → 1, WS_DROP → 2, else 0.
                WepPhase = p.WeaponState(new WeaponSlot(0)).State switch
                {
                    WeaponFireState.Raise => (byte)1,
                    WeaponFireState.Drop => (byte)2,
                    _ => (byte)0,
                },
                // QC the exterior weapon entity's .alpha — quantized exactly like the body alpha (0 = opaque, not
                // networked; 255 = hidden). Running Guns hides the body (body Alpha = -1 hidden) but keeps the gun
                // (DefaultWeaponAlpha = +1), and Cloaked fades both; the exterior-weapon alpha is the SetDefaultAlpha
                // hook's DefaultWeaponAlpha, networked independently of the body Alpha above so the two can differ.
                WepAlpha = (byte)QuantizeAlpha(XonoticGodot.Common.Gameplay.MutatorHooks.DefaultWeaponAlpha),
                // [W-wepent-view] the per-player wepent HUD view-state — the spectatee/third-person counterpart of
                // the owner-block OwnerWeaponRings; every client viewing this player gets its charge/clip/heat/beam.
                // Resolved by the SAME WepentResolver the owner block uses (ResolveOwnerWeaponRings now delegates to
                // it), so the owner's own rings and other players' views of this player stay in lockstep. An idle /
                // no-charge player resolves to WepentViewState.None, which NetEntityState.Diff compares field-by-field
                // against the baseline → the EntityField.WepentView bit stays clear and that player costs nothing here.
                WepentView = XonoticGodot.Common.Gameplay.WepentResolver.Resolve(p, CountLiveMines, _world.Time),
                // [W-vehicleview] the per-player vehicle HUD view-state — the seated-pilot health/shield/energy/ammo/
                // reload bars, the vehicle hud id, weapon-2 mode, and lock strength/flags. Resolved off the
                // authoritative player exactly like WepentView above; an on-foot / observing player resolves to
                // VehicleViewState.None (VehKind 0), which NetEntityState.Diff compares field-by-field against the
                // baseline → the EntityField.VehicleView bit stays clear and that player costs nothing here.
                VehicleView = XonoticGodot.Common.Gameplay.VehicleViewResolver.Resolve(p),
                // ViewmodelSkin / GunAlign have NO server-side producer in the port (the viewmodel skin variant and
                // cl_gunalign side are client-local concepts; QC networks them off the wepent entity, which the port
                // doesn't yet model server-side). Left at their defaults (0) — the wire is RESERVED for a later wave
                // once a server-side exterior-weapon entity carries them; defaulting keeps them from desyncing.
            };
            // QC PutPlayerInServer sets effects |= EF_TELEPORT_BIT on every (re)spawn (client.qc:630), which tells
            // the engine to CANCEL position interpolation for that update so the model snaps to its spawn spot
            // instead of sliding across the map. The port carries that as NetEntityFlags.Teleported. The distance
            // heuristic (`teleported`) catches the common far jump, but a respawn that lands WITHIN
            // TeleportTickDistance of the death spot would still lerp — so also raise the flag on the explicit
            // spawn/teleport/board signal (Entity.AntilagNeedsClear → `explicitClear`), the same one-shot
            // PutPlayerInServer raises. This is the no-interp half of EF_TELEPORT_BIT, faithful regardless of jump
            // distance (EF_RESTARTANIM_BIT — anim restart — is a no-op for the port's stateless procedural
            // LocomotionBlend, which re-derives the pose every frame from live velocity/flags).
            if (teleported || explicitClear)
                s.Flags |= NetEntityFlags.Teleported;
            _lastSnapOrigin[p] = p.Origin;

            // [A5 #3/#7] ENT_CLIENT_STATUSEFFECTS: consume this player's status-effect dirty mark (QC SendFlags) —
            // Flush re-encodes the full bitmap only on a tick its effects changed, else returns null. Cache the
            // current blob (null = no effects, the "all clear" majorBits-only byte normalized away) so EVERY
            // snapshot — including a late joiner's full delta — carries the authoritative state; the delta then
            // re-sends the bytes only when they actually change (NetEntityState.Diff compares them by content).
            byte[]? flushed = StatusEffectsCatalog.Flush(p);
            if (flushed is not null)
                _statusBlob[p] = NormalizeStatusBlob(flushed);
            s.StatusEffects = _statusBlob.TryGetValue(p, out byte[]? cachedBlob) ? cachedBlob : null;

            // Does this player carry OWNER-PRIVATE Feedback-block state? If any player does, the per-recipient
            // pass below MUST run to strip it from everyone else's copy — so the "no filtering needed" fast
            // paths in RelevantEntitiesFor have to know. (Cheap: one bool per snapshot, not per recipient.)
            if (s.HitTime != 0f || s.TypeHitTime != 0f || s.KillTime != 0f || s.HitDamageDealtTotal != 0f
                || s.NadeDarknessTime != 0f || s.NadeBonus != 0f || s.NadeBonusScore != 0f)
            {
                _scratchHasOwnerPrivate = true;
            }

            _entityScratch[netId] = s;
            _entityBounds[netId] = RelevanceBounds(p.Origin, p.Mins, p.Maxs);
        }

        // non-player networked entities: projectiles, items, gibs, monsters/turrets/vehicles — everything with a
        // model the client must render (CSQCProjectile + the general CSQC entity stream). Keyed in a high net-id
        // range so they never collide with the small ENet peer ids; inline brush models ("*N") are skipped (the
        // client renders those from the map). Delta-compression makes a static item nearly free after its spawn.
        IReadOnlyList<Entity> all = _world.Services.EntityTable.All;
        for (int i = 0; i < all.Count; i++)
        {
            Entity e = all[i];
            if (e is null || e.IsFreed || e is Player)
                continue;
            if (!string.IsNullOrEmpty(e.Model) && e.Model.StartsWith('*'))
                continue;                          // inline brush model — drawn by the map, not networked
            if (e.PhysNetSuppressed)
                continue;                          // QC setSendEntity(func_null): physical_items hid this item
                                                   // behind its ghost — the real edict no longer networks
            NetEntityKind kind = Classify(e);
            if (kind == NetEntityKind.None)
                continue;
            // Projectiles carry NO server-side .model (QC sets the model client-side via CSQCProjectile); the
            // client renders them procedurally (ProjectileRenderer + ProjectileCatalog). So a projectile networks
            // even with an empty model — only NON-projectiles are dropped for having nothing to draw.
            // [W-nadeclient] a NadeOrb (like a Projectile) carries NO server-side model — the client renders it
            // procedurally from the NadeOrbState block (OrbType/OrbExpire/OrbRadius). So exempt it from the
            // empty-model drop alongside Projectile; only OTHER kinds are dropped for having nothing to draw.
            if (kind != NetEntityKind.Projectile && kind != NetEntityKind.NadeOrb
                && e.ModelIndex <= 0 && string.IsNullOrEmpty(e.Model))
                continue;                          // nothing to render

            // Give a model-less projectile a catalog key so the client picks the right type (rocket/grenade/
            // electro/…) instead of the Generic fallback: send its classname + netname in the (otherwise unused)
            // model field. ProjectileRenderer ignores the model geometry, so this only feeds ProjectileCatalog.
            string netModel = e.Model;
            if (kind == NetEntityKind.Projectile && string.IsNullOrEmpty(netModel))
            {
                netModel = ProjectileCatalogKey(e.ClassName, e.NetName);
                // QC CSQCProjectile sub-variant: a bouncing grenade (mortar type 1) networks its own model token so
                // the client picks PROJECTILE_GRENADE_BOUNCING (sideways tumble) instead of PROJECTILE_GRENADE.
                if (e.CsqcProjectileBouncing)
                    netModel += " bouncing";
                // [W-nadeclient] a thrown nade encodes its TYPE into the same catalog key so the client classifier
                // picks the right CSQCProjectile model/trail (napalm/ice/heal/…) instead of the generic nade. The
                // registry id 0 is the "random/null" sentinel (no specific type) — only append a concrete netname.
                if (e.ClassName == "nade")
                {
                    var def = XonoticGodot.Common.Gameplay.Nades.NadeRegistry.ById(e.NadeBonusType);
                    if (def is not null && def.Id != 0)
                        netModel += " " + def.NetName;
                }
            }

            int netId = EntityNetBase + e.Index;
            if (netId > ushort.MaxValue)
                continue;
            _entityScratch[netId] = new NetEntityState
            {
                EntNum = netId,
                Kind = kind,
                ModelIndex = e.ModelIndex,
                // [W14a-wepent] non-player entities carry no exterior-weapon block; default the switch ids to the
                // -1 "none" sentinel so they MATCH the NetEntityState.Empty baseline and the EntityField.Wepent bit
                // stays clear (a non-player entity costs nothing for the wepent group on the wire).
                SwitchWeapon = -1,
                SwitchingWeapon = -1,
                // A weapon PICKUP's explicit identity: the weapon RegistryId + 1 (0 = not a weapon pickup, the
                // baseline default — costs nothing for other items). The client paints/classifies the pickup
                // from this id instead of guessing from the model filename (which a mapper can override).
                Weapon = e.Pickup is XonoticGodot.Common.Gameplay.WeaponPickup wpk ? wpk.Weapon.RegistryId + 1 : 0,
                Model = netModel,                  // QC .model (or, for a projectile, its catalog key) — resolved by name
                Frame = (int)e.Frame,
                Skin = (int)e.Skin,
                Origin = e.Origin,
                Angles = e.Angles,
                Velocity = e.Velocity,             // static entities keep 0 → the delta never sends it
                Effects = e.Effects,
                Colormap = (int)e.Team,
                // RENDER_COLORMAPPED entities (a thrown/death-dropped weapon inheriting the thrower's packed
                // shirt/pants — WeaponThrowing.ThrowerColormap — plus colormapped props/monsters): ship the
                // FULL authoritative colormap in its own delta field (protocol v17), so the client repaints
                // the loot in the DROPPER's colors (frozen at throw time — the server never rewrites a loot
                // colormap after spawn) and both QC encodings (packed 1024+ vs a sub-1024 slot reference)
                // survive the wire. 0 on the overwhelming majority of entities → the delta bit stays clear.
                ColorMapOverride = e.ColorMapOverride & 0xFFFF,
                Health = (int)e.Health,
                Alpha = QuantizeAlpha(e.Alpha), // [W1-alpha-net] per-entity render transparency (e.g. Cloaked items)
                Owner = (e.Owner is Player op && _byPlayer.TryGetValue(op, out PeerState? ops)) ? ops.PeerId : 0,
                // QC ItemStatus bits (items.qc). ITS_EXPIRING: a loot item in its despawn-fx window — set the
                // frame it flips so the client starts the despawn animation. ITS_ANIMATE1/2: the item's static
                // bob+spin class (set once at spawn from the def) so the client renders the float/rotation and
                // lifts the model clear of the floor. Other non-player kinds leave these clear → Flags stays None.
                // ITS_AVAILABLE: a picked-up item awaiting respawn keeps its model (Item_Show(e,0) only clears
                // availability + solidity, it does NOT null the model) — networked as ItemGhost so the client
                // renders it faded instead of opaque. Item-only: non-items never clear ItemAvailable (default true).
                Flags = (e.ItemExpiringFx ? NetEntityFlags.ItemExpiring : NetEntityFlags.None)
                        | (e.ItemAnimate == 1 ? NetEntityFlags.ItemAnimate1 : NetEntityFlags.None)
                        | (e.ItemAnimate == 2 ? NetEntityFlags.ItemAnimate2 : NetEntityFlags.None)
                        | (kind == NetEntityKind.Item && !e.ItemAvailable ? NetEntityFlags.ItemGhost : NetEntityFlags.None),
            };

            // [objstream] Fold the turret-head + objective-state extras into the SAME snapshot record (no second
            // loop): both groups stay default on every non-turret/non-objective entity, so NetEntityState.Diff
            // leaves EntityField.TurretHead / EntityField.ObjState clear and a normal item/projectile costs nothing
            // for them. Read the struct back, set the extras, write it back (NetEntityState is a value type).
            var s = _entityScratch[netId];
            // TURRET head aim/active (QC cl_turrets tur_head.angles/.avelocity + .active/DEAD_DEAD): TryGetState is the
            // non-allocating probe (State(e) would alloc for every non-turret entity each tick), so only a real turret
            // populates the head block; everything else leaves it default.
            if (XonoticGodot.Common.Gameplay.TurretAI.TryGetState(e, out var tst))
            {
                s.TurHeadPitch = tst.HeadAngles.X;
                s.TurHeadYaw = tst.HeadAngles.Y;
                s.TurHeadAVelYaw = tst.HeadAVelocity.Y;
                byte tf = 0;
                if (tst.Active) tf |= 1;             // bit0 = QC .active (awake / team-owned)
                if (e.DeadState != DeadFlag.No) tf |= 2; // bit1 = QC DEAD_DEAD (destroyed pose)
                s.TurFlags = tf;
            }
            // OBJECTIVE health fraction + build/own state (QC generator/CP-pad/icon STATUS hp/255 + build-bar): only
            // an entity that carries objective health (GtObjMaxHealth > 0) sets these — a normal entity has 0 max and
            // leaves the block default so the EntityField.ObjState bit stays clear.
            if (e.GtObjMaxHealth > 0f)
            {
                float frac = e.GtObjHealth / e.GtObjMaxHealth;
                s.ObjHealthByte = (byte)System.Math.Clamp((int)MathF.Round(frac * 255f), 0, 255);
                s.ObjState = ResolveObjState(e);
            }
            // [W-nadeclient] the nade_orb STATE block (QC the orb's networked id/expiry/radius — carried under
            // EntityField.NadeOrbState, present ONLY on a NadeOrb entity). OrbType = the Nades registry id (selects the
            // orb model + flash color); OrbExpire = the absolute expiry the client derives the scale-up/fade phase from
            // (QC .ltime, stored on e.NadeOrbExpire by the boom); OrbRadius = the hull half-width doubled into the full
            // qu diameter that drives the in-orb color-flash containment test + 3D model scale. The Origin rides the
            // normal Origin field. Default on every other kind so the NadeOrbState bit stays clear.
            if (kind == NetEntityKind.NadeOrb)
            {
                s.OrbType = (byte)e.NadeBonusType;
                s.OrbExpire = e.NadeOrbExpire;
                s.OrbRadius = (int)(e.Maxs.X * 2f);
            }
            _entityScratch[netId] = s;
            _entityBounds[netId] = RelevanceBounds(e.Origin, e.Mins, e.Maxs);
        }

        // Antilag breadth (world.qc:EndFrame): besides players, record the position history of every MONSTER and
        // every NADE so they can also be rewound at fire time (antilag_takeback_all/restore_all walk g_monsters +
        // g_projectiles[classname=="nade"] too). Done as its own pass over `all` because antilag-eligibility is
        // independent of render-eligibility — a monster/nade is recorded whether or not it made the snapshot set.
        RecordAntilagEntities(all, altime);
    }

    /// <summary>
    /// (§12.8) The world-space box used to PVS-test a networked entity for relevance: the entity's own hull
    /// (origin + Mins/Maxs) expanded to a conservative minimum half-extent so point entities (projectiles) and
    /// fast movers aren't culled right at a cluster boundary — wrongly hiding a visible entity is far worse than
    /// under-culling one solidly behind a wall. (DP caches each edict's clusters at link time; we box-test live
    /// per recipient, which is cheap at these entity counts.)
    /// </summary>
    private static (NVec3 Min, NVec3 Max) RelevanceBounds(NVec3 origin, NVec3 mins, NVec3 maxs)
    {
        const float minHalf = 48f;
        NVec3 lo = origin + mins, hi = origin + maxs;
        return (
            new NVec3(MathF.Min(lo.X, origin.X - minHalf), MathF.Min(lo.Y, origin.Y - minHalf), MathF.Min(lo.Z, origin.Z - minHalf)),
            new NVec3(MathF.Max(hi.X, origin.X + minHalf), MathF.Max(hi.Y, origin.Y + minHalf), MathF.Max(hi.Z, origin.Z + minHalf)));
    }

    /// <summary>
    /// [objstream] The objective entity's networked state byte (QC the cpicon build-bar vs generator healthbar
    /// selector): 3 = destroyed (no health left); for a control-point build icon (classname
    /// <c>onslaught_controlpoint_icon</c>) 1 = building / 2 = built; otherwise (a generator / Domination pad) 2 when
    /// the objective is team-owned, else 0 = neutral/idle. Mirrors the client's decoded <c>Entity.ObjState</c>.
    /// </summary>
    private static byte ResolveObjState(Entity e)
    {
        if (e.GtObjHealth <= 0f)
            return 3; // destroyed
        if (e.ClassName == "onslaught_controlpoint_icon")
            return e.GtIconBuilt ? (byte)2 : (byte)1; // mid-build vs captured
        return e.Team != 0f && e.Team != Teams.None ? (byte)2 : (byte)0; // owned vs neutral
    }

    /// <summary>
    /// The weapon a player is ACTUALLY holding (raising/in-use) this tick — the QC <c>wepent</c> exterior weapon's
    /// <c>m_weapon</c> (CL_ExteriorWeaponentity_Think reads <c>w_ent.weaponname</c>, which the switch state machine
    /// sets to the new weapon only at the WS_CLEAR→WS_RAISE transition, server/weapons/weaponsystem.qc:547). The
    /// port drives that in <see cref="XonoticGodot.Common.Gameplay.WeaponFireDriver"/> via slot-0
    /// <c>CurrentWeaponId</c>, which (unlike the immediately-mirrored <see cref="Player.ActiveWeaponId"/>) still
    /// holds the OLD weapon through the drop window — so the remote third-person held model lowers the old weapon
    /// and only snaps to the new one when the raise begins, exactly like Base's exterior weapon entity. Falls back
    /// to <see cref="Player.ActiveWeaponId"/> before the slot has been driven (CurrentWeaponId still -1 on a fresh
    /// spawn) so a just-spawned player still shows its weapon immediately.
    /// </summary>
    private static int HeldWeaponId(Player p)
    {
        int held = p.WeaponState(new WeaponSlot(0)).CurrentWeaponId;
        return held >= 0 ? held : p.ActiveWeaponId;
    }

    /// <summary>
    /// (§12.8) Build the per-recipient entity set fed to the delta encoder. Two per-recipient filters compose here:
    /// <list type="bullet">
    /// <item>DP's <c>SV_MarkWriteEntityStateToClient</c> PVS cull (<c>sv_cullentities_pvs</c>): drop entities whose
    /// bounds aren't in the recipient's PVS (the delta encoder turns those into removals against this client's
    /// baseline and re-spawns them when they return). Disabled when culling is off / the map is unvised / the
    /// recipient is dead-or-spectating / the eye is in solid — all conservative "send everything" fallbacks.</item>
    /// <item>The ent_cs enemy-privacy mask (common/ent_cs.qc:_entcs_send, ENTCS_PUBLICMASK): for a live remote
    /// PLAYER whose recipient is an enemy active player (not <paramref name="radarShowEnemies"/>, not SAME_TEAM),
    /// strip the private ent_cs HEALTH/ARMOR resource slice so a modified client can't read every enemy's exact
    /// hit-points. Stripped fields are zeroed on the wire (a constant, so the delta sends one zero on first sight
    /// then nothing — equivalent to QC never including the bit in this recipient's SendFlags; the client's enemy
    /// value stays the default 0, and the shownames status bar is team-gated client-side anyway).</item>
    /// </list>
    /// NB on ORIGIN/ANGLES: QC also marks those ent_cs props private, but in QC the VISIBLE player MODEL is a
    /// SEPARATE csqcmodel channel that is sent (PVS-gated) regardless of the ent_cs mask — the mask only hides the
    /// out-of-PVS radar GPS. The port folds the model + ent_cs into one state whose origin IS the render position,
    /// and already PVS-culls the whole entity when out of view (the bullet above), so an enemy's origin is governed
    /// by PVS exactly as QC's csqcmodel origin is. Freezing origin here would wrongly stall a VISIBLE enemy's model,
    /// so only HEALTH/ARMOR (which have no csqcmodel counterpart and are otherwise leaked even for visible enemies)
    /// are stripped. The residual out-of-PVS GPS degrade is its own tracked gap (csqcmodel.force_updates).
    ///
    /// Returns <see cref="_entityScratch"/> unchanged only when NEITHER filter applies (a fast no-op path).
    /// </summary>
    private IReadOnlyDictionary<int, NetEntityState> RelevantEntitiesFor(
        PeerState st, Player owner, int ownNetId, bool cullPvs, bool radarShowEnemies, bool teamplay)
    {
        // ent_cs privacy applies only when the recipient is an enemy that could read another player's private
        // fields: a live, active player (IS_PLAYER(to) || INGAME(to) → !IsObserver) in a game where enemies aren't
        // globally revealed. A spectator/observer recipient gets everyone's private fields (QC: the
        // `!(IS_PLAYER(to) || INGAME(to))` branch keeps them) — free spectating sees all.
        bool applyPrivacy = !radarShowEnemies && !owner.IsObserver;

        bool havePvsCull = cullPvs && _world.Pvs is { HasVis: true } && !owner.IsDead && !owner.IsObserver;

        // Fast path: neither filter narrows the set → send the shared scratch as-is.
        if (!havePvsCull && !applyPrivacy && !_scratchHasOwnerPrivate)
            return _entityScratch;

        int viewerCluster = -1;
        XonoticGodot.Formats.Bsp.BspPvs? pvs = null;
        if (havePvsCull)
        {
            pvs = _world.Pvs;
            NVec3 eye = owner.Origin + owner.ViewOfs;
            viewerCluster = pvs!.LeafCluster(pvs.FindLeaf(eye));
            if (viewerCluster < 0)
                havePvsCull = false; // eye in solid / outside the tree → conservatively send all (no PVS cull)
        }

        // [warpzone] Cluster UNION through visible portals: if the recipient can see a linked warpzone's window,
        // everything at its EXIT is on-screen through the portal — Base networks csqc items with NO PVS gate
        // (ItemSend) and DP's culling never blocks the warpzone view, so a per-client PVS cull that ignores
        // portals makes far-side items/players/projectiles VANISH from the through-portal view (the live
        // "can't see items through the warpzone" report). Zone windows are static; two FindLeaf calls per
        // linked zone per recipient per snapshot are negligible.
        _viewClustersScratch.Clear();
        if (havePvsCull)
        {
            _viewClustersScratch.Add(viewerCluster);
            IReadOnlyList<XonoticGodot.Common.Gameplay.Warpzone> zones = _world.Warpzones.Zones;
            for (int zi = 0; zi < zones.Count; zi++)
            {
                XonoticGodot.Common.Gameplay.Warpzone wz = zones[zi];
                if (!wz.Linked) continue;
                NVec3 inP = wz.Transform.InOrigin + wz.Transform.InForward * 8f;
                int inC = pvs!.LeafCluster(pvs.FindLeaf(inP));
                if (inC < 0 || !pvs.ClustersVisible(viewerCluster, inC)) continue;
                NVec3 outP = wz.Transform.OutOrigin + wz.Transform.OutForward * 8f;
                int outC = pvs.LeafCluster(pvs.FindLeaf(outP));
                if (outC >= 0 && !_viewClustersScratch.Contains(outC)) _viewClustersScratch.Add(outC);
            }
        }

        // Privacy still applies even with PVS culling off, so if neither now narrows the set, take the fast path.
        if (!havePvsCull && !applyPrivacy && !_scratchHasOwnerPrivate)
            return _entityScratch;

        int ownerTeam = (int)owner.Team;
        // QC EndFrame stamps a follow-spectator's feedback stats from `it.enemy` (the spectatee), so a
        // spectator legitimately receives the watched player's hit/kill feedback — it becomes their own STAT
        // in Base. An observer has no networked entity of their own here, so the client reads it off the
        // spectatee's entity instead; keep the block on that ONE entity for this recipient only.
        int spectateeNetId = owner.Spectatee is { } sp ? NetIdFor(sp) : 0;

        _relevantScratch.Clear();
        foreach (KeyValuePair<int, NetEntityState> kv in _entityScratch)
        {
            bool isOwn = kv.Key == ownNetId;

            // PVS cull: never cull the recipient's own entity (it ships as the client's LocalState slice); keep anything
            // whose bounds touch a cluster the recipient can see — DIRECTLY or THROUGH a visible portal (the
            // warpzone cluster union above).
            if (havePvsCull && !isOwn
                && _entityBounds.TryGetValue(kv.Key, out (NVec3 Min, NVec3 Max) b))
            {
                bool anyVisible = false;
                for (int vc = 0; vc < _viewClustersScratch.Count && !anyVisible; vc++)
                    anyVisible = pvs!.BoxAnyClusterVisibleFrom(_viewClustersScratch[vc], b.Min, b.Max);
                if (!anyVisible)
                    continue; // out of PVS (incl. through portals) → drop (delta turns it into a removal)
            }

            NetEntityState state = kv.Value;

            // ent_cs enemy-privacy mask: a live remote PLAYER (the entcs owner IS_PLAYER) whose recipient is an
            // enemy → strip the private HEALTH/ARMOR slice. SAME_TEAM = teamplay ? sameTeamColor : (to == player);
            // a distinct player is never the same entity, so in a non-team game EVERY other player is an enemy (you
            // never learn anyone else's HP — FFA). The own entity ships unmasked (it IS this recipient — the
            // client keeps it as its LocalState slice, never in the remote table).
            if (applyPrivacy && !isOwn && state.Kind == NetEntityKind.Player)
            {
                bool sameTeam = teamplay && ownerTeam != 0 && state.Colormap == ownerTeam;
                if (!sameTeam)
                {
                    // Zero the private resource fields (constant → one zero on first sight, then the delta is silent;
                    // matches QC ANDing HEALTH/ARMOR out of this recipient's SendFlags so its value stays default 0).
                    state.Health = 0;
                    state.Armor = 0;
                }
            }

            // [W-vehicleview] the cockpit block is owner-private: the host path reads only LocalServerPlayer.VehicleView,
            // so a remote client must see its OWN seated HUD and never another pilot's bars. Reset every non-own player's
            // VehicleView to None so EntityField.VehicleView stays clear for remotes (independent of the enemy-privacy
            // gate above, which only fires for enemy recipients — vehicle state is private from teammates too).
            if (applyPrivacy && !isOwn && state.Kind == NetEntityKind.Player
                && !state.VehicleView.Equals(XonoticGodot.Net.VehicleViewState.None))
            {
                state.VehicleView = XonoticGodot.Net.VehicleViewState.None;
            }

            // OWNER-PRIVATE Feedback block. In QC these are per-client STATs (STAT(HIT_TIME) etc.) — they are
            // not entity data at all, so no recipient but the owner ever sees them. The port carries them on
            // the player entity delta, which means they must be stripped here or every client learns the exact
            // frame any visible enemy landed a hit or scored a frag, and pays the delta bytes for it.
            // Deliberately NOT gated on applyPrivacy: that flag is off for spectators and in
            // radar-show-enemies modes, neither of which should hand out another player's private stats.
            if (!isOwn && kv.Key != spectateeNetId && state.Kind == NetEntityKind.Player)
            {
                state.HitTime = 0f;
                state.TypeHitTime = 0f;
                state.KillTime = 0f;
                state.HitDamageDealtTotal = 0f;
                state.NadeDarknessTime = 0f;
                state.NadeBonus = 0;
                state.NadeBonusType = 0;
                state.NadeBonusScore = 0f;
            }

            _relevantScratch[kv.Key] = state;
        }
        return _relevantScratch;
    }

    /// <summary>
    /// Normalize a freshly-flushed status-effect bitmap to the wire representation of "no effects": an entity with
    /// no active effects encodes to a single <c>majorBits = 0</c> byte (length 1) — collapse that (and any
    /// empty/null) to <c>null</c> so the delta treats it identically to a never-touched entity (the mask bit stays
    /// clear instead of shipping a 1-byte "nothing here" blob every snapshot). Any real effect set is ≥ 7 bytes.
    /// </summary>
    private static byte[]? NormalizeStatusBlob(byte[]? blob)
        => (blob is null || blob.Length <= 1) ? null : blob;

    /// <summary>
    /// Port of the monster + nade half of the <c>world.qc:EndFrame</c> antilag record loop
    /// (<c>IL_EACH(g_monsters, …) { antilag_record(it, it, altime); }</c> and the <c>g_projectiles</c> pass
    /// gated on <c>it.classname == "nade"</c>). Stores each live monster/nade's origin at <paramref name="altime"/>
    /// into its own per-entity <see cref="AntilagBuffer"/>, allocating one on first sight; entities that have since
    /// freed are pruned so their rings don't leak. We have no intrusive lists (IL_EACH) yet, so we filter the dense
    /// entity table: a monster carries <see cref="EntFlags.Monster"/> (set by <c>MonsterAI.Setup</c>) — the same
    /// test Invasion uses — and a nade is <c>ClassName == "nade"</c> (the thrown-grenade projectile; the nades
    /// subsystem that spawns it is T11, so this is currently a no-op until those entities exist, but the rewind is
    /// in place for when they do).
    /// </summary>
    private void RecordAntilagEntities(IReadOnlyList<Entity> all, float altime)
    {
        for (int i = 0; i < all.Count; i++)
        {
            Entity e = all[i];
            if (e is null || e.IsFreed || e is Player)
                continue;
            if (!IsAntilagged(e))
                continue;

            if (!_antilagEntities.TryGetValue(e, out AntilagBuffer? hist))
            {
                hist = new AntilagBuffer();
                _antilagEntities[e] = hist;
            }
            // A monster/nade/vehicle that teleported or (re)spawned in place gets its ring wiped first, mirroring
            // the player-side antilag_clear, so a shot can't rewind it to a pre-jump position. altime is strictly
            // newer than every stored stamp, so SampleAt(altime) returns the head (last recorded) origin to
            // diff against without a new API. The explicit one-shot AntilagNeedsClear flag is the QC
            // antilag_clear lifecycle (vehicle enter/exit sets it on the body, sv_vehicles.qc) — it catches a
            // board/unboard that doesn't move the body far enough to trip the jump heuristic.
            bool explicitClear = e.AntilagNeedsClear;
            e.AntilagNeedsClear = false;
            if (explicitClear
                || (hist.HasData && (e.Origin - hist.SampleAt(altime)).Length() > TeleportTickDistance))
                hist.Clear();
            hist.Store(altime, e.Origin);
        }

        // prune rings for monsters/nades that died/expired since last frame (freed, or no longer antilagged) so the
        // dictionary tracks only live entities — the QC equivalent is the entity simply leaving its intrusive list
        // on remove. Cheap over a small dict, so done every frame rather than gated on a (churn-blind) count check.
        if (_antilagEntities.Count > 0)
        {
            _antilagPruneScratch.Clear();
            foreach (Entity tracked in _antilagEntities.Keys)
                if (tracked.IsFreed || !IsAntilagged(tracked))
                    _antilagPruneScratch.Add(tracked);
            for (int i = 0; i < _antilagPruneScratch.Count; i++)
                _antilagEntities.Remove(_antilagPruneScratch[i]);
        }
    }

    /// <summary>Is this non-player entity one the antilag engine rewinds — a monster (FL_MONSTER, the same flag test
    /// <c>Invasion</c> uses), a thrown nade (<c>classname == "nade"</c>), or a MANNED vehicle body
    /// (antilag.qc antilag_takeback_all set)? Base records/rewinds a vehicle through the piloting player's
    /// <c>antilag_record</c>/<c>antilag_takeback</c> recursion into <c>e.vehicle</c> (skipping a VHF_PLAYERSLOT
    /// gunner slot). The port instead tracks the occupied vehicle directly as an antilag entity — equivalent,
    /// since the vehicle keeps its own ring and is rewound when other players' shots take back. A vehicle is
    /// "manned" while it has a piloting <see cref="Entity.Owner"/>; an unoccupied vehicle has no shooter to
    /// compensate for and Base never records it (no player recurses into it), so it drops out of the set.</summary>
    private static bool IsAntilagged(Entity e)
        => (e.Flags & EntFlags.Monster) != 0
           || e.ClassName == "nade"
           || IsMannedVehicle(e);

    /// <summary>A non-PLAYERSLOT vehicle body with a piloting owner — the entity Base's <c>antilag_record</c>
    /// recurses into via the player's <c>.vehicle</c> link (the <c>vehicle_flags != VHF_PLAYERSLOT</c> guard
    /// skips a multislot gunner SLOT, whose movement is its parent vehicle's, not its own).</summary>
    private static bool IsMannedVehicle(Entity e)
        => (e.VehicleFlags & VehicleFlags.IsVehicle) != 0
           && (e.VehicleFlags & VehicleFlags.PlayerSlot) == 0
           && e.Owner is Player;

    /// <summary>
    /// Snapshot the score table for this tick (QC PlayerScore_SendEntity / TeamScore_SendEntity). Mirrors the
    /// authoritative per-team totals (which the gametype owns) into the shared score table so its
    /// <see cref="XonoticGodot.Common.Gameplay.Scoring.GameScores.Version"/> reflects team changes, then captures
    /// every player's networked columns + the active team totals once. The version stamp gates the per-client send.
    /// </summary>
    private void BuildScoreboard()
    {
        // mirror the active gametype/teamplay into the score table so the ScoreInfo block (QC
        // ScoreInfo_SendEntity's WriteRegistered(Gametypes) + teamplay) carries the live mode for the client's
        // column filter. Idempotent; writing the same NetName doesn't bump the layout generation (only label/flag
        // changes do), so it doesn't spuriously force a ScoreInfo resend.
        XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype = _world.GameType?.RegistryName ?? "dm";
        XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay = _world.Teamplay is { IsTeamGame: true };

        // mirror gametype-owned team totals into the score table (bumps Version on a team-score change).
        if (_world.Teamplay is { IsTeamGame: true })
            foreach (int t in XonoticGodot.Common.Gameplay.Teams.Active(_world.Teamplay.TeamCount))
                XonoticGodot.Common.Gameplay.Scoring.GameScores.SetTeamScore(t, _world.Scores.TeamScore(t));

        IReadOnlyList<Player> players = _world.Clients.Players;

        // #22: fold a cheap ROSTER signature (count + each player's net id / spectator flag / team) into the score
        // version so a roster/status change with no score change — you or the bots JOINING (observer→player), a team
        // switch, a disconnect — forces a re-send. Without this the row set is rebuilt below but the per-client send
        // stays gated on the score-only Version, so the client's board sat at the pre-join state ("you're a
        // spectator, no bots") until the first frag bumped it.
        long rosterSig = players.Count;
        for (int i = 0; i < players.Count; i++)
        {
            Player rp = players[i];
            int status = (rp.IsObserver || rp.FragsStatus == Player.FragsSpectator) ? 1 : 0;
            rosterSig = unchecked(rosterSig * 31 + NetIdFor(rp));
            rosterSig = unchecked(rosterSig * 31 + status);
            rosterSig = unchecked(rosterSig * 31 + (int)rp.Team);
        }
        if (rosterSig != _lastRosterSig)
        {
            _lastRosterSig = rosterSig;
            XonoticGodot.Common.Gameplay.Scoring.GameScores.MarkDirty();
        }

        _scoreVersion = XonoticGodot.Common.Gameplay.Scoring.GameScores.Version;
        _scoreInfoGen = XonoticGodot.Common.Gameplay.Scoring.GameScores.LayoutGeneration;
        _scoreInfoHash = XonoticGodot.Net.ScoreInfoBlock.Hash();

        _scoreRows.Clear();
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            // QC ping_packetloss (server/world.qc:74): a connected human's measured ENet packet loss, quantized
            // to a 0..255 byte (min(ceil(loss*255),255)); bots and unmapped players send 0.
            int plByte = 0;
            // QC SP_PING (scoreboard.qc:1041): a connected human's measured ENet round-trip time (ms); bots and
            // unmapped players send -1 (unknown → the client renders the neutral '-' rather than a fake number).
            int pingMs = -1;
            // Read the main-thread sample (SamplePeerStats), NOT the live ENet peer: this encode runs on the sim
            // worker when threaded and ENetMultiplayerPeer is main-thread-affine. A peer that connected inside the
            // current 0.25s sample window simply reads as unmapped for one window (ping -1 → the neutral '-'),
            // which is what it already rendered before its first RTT estimate existed.
            if (_byPlayer.TryGetValue(p, out PeerState? plSt)
                && _peerNetStats.TryGetValue(plSt.PeerId, out (int PingMs, float Loss) ns))
            {
                plByte = System.Math.Min((int)System.MathF.Ceiling(ns.Loss * 255f), 255);
                pingMs = ns.PingMs;
            }
            // carry the entcs name/team slice so the client can label/group the row without an entcs stream
            // (the port has no entcs name source; the scoreboard would otherwise have an opaque net id).
            _scoreRows.Add(new XonoticGodot.Net.ScoreRowWire(NetIdFor(p),
                XonoticGodot.Common.Gameplay.Scoring.GameScores.CaptureColumns(p), p.NetName, (int)p.Team,
                // QC pl.team == NUM_SPECTATOR: an observer is listed in the scoreboard spectator block, not the
                // score table. The port keeps the observer's last team color, so flag it explicitly here.
                isSpectator: p.IsObserver || p.FragsStatus == Player.FragsSpectator,
                packetLossByte: plByte,
                pingMs: pingMs)); // QC SP_PING: the human's ENet RTT (-1 for bots / unmapped)
        }

        _scoreTeams.Clear();
        if (_world.Teamplay is { IsTeamGame: true })
            foreach (int t in XonoticGodot.Common.Gameplay.Teams.Active(_world.Teamplay.TeamCount))
                _scoreTeams.Add((t, _world.Scores.TeamScore(t)));

        // QC the race/CTS rankings table (Scoreboard_Rankings_Draw): in race modes, snapshot the persistent
        // RaceRecords DB's best-first ranked times for this map so the client scoreboard's rankings block has
        // data (the C# stand-in for RACE_NET_SERVER_RANKINGS). Non-race modes leave it empty (one byte on the wire).
        _scoreRankings.Clear();
        _scoreSpeedAward = default;
        string gt = _world.GameType?.NetName ?? "";
        if (gt == "rc" || gt == "cts")
        {
            string map = _world.Services.Cvars.GetString("mapname");
            string recordType = gt == "cts"
                ? XonoticGodot.Common.Gameplay.RaceRecords.CtsRecord
                : XonoticGodot.Common.Gameplay.RaceRecords.RaceRecord;
            // QC race_send_rankings_cnt: m = min(RANKINGS_CNT, autocvar_g_cts_send_rankings_cnt) — the number of
            // ranked records networked for display. Base default g_cts_send_rankings_cnt = 15 (gametypes-server.cfg),
            // hard-capped at RANKINGS_CNT (99). Read the cvar live so a server config edit takes effect.
            int displayCnt = 15;
            string rcStr = _world.Services.Cvars.GetString("g_cts_send_rankings_cnt");
            if (!string.IsNullOrEmpty(rcStr))
            {
                float rc = _world.Services.Cvars.GetFloat("g_cts_send_rankings_cnt");
                if (float.IsFinite(rc) && rc >= 0f) displayCnt = (int)rc;
            }
            displayCnt = System.Math.Clamp(displayCnt, 0, XonoticGodot.Common.Gameplay.RaceRecords.RankingsCnt);
            for (int pos = 1; pos <= displayCnt; pos++)
            {
                float t = XonoticGodot.Common.Gameplay.RaceRecords.ReadTime(map, recordType, pos);
                if (t == 0f) break; // ranked table is dense from rank 1; first empty slot ends it
                _scoreRankings.Add((
                    XonoticGodot.Common.Gameplay.Scoring.GameScores.TimeEncode(t),
                    XonoticGodot.Common.Gameplay.RaceRecords.ReadName(map, recordType, pos)));
            }

            // QC race_send_speedaward / _best (server/race.qc:267): Race and CTS both track the round-best +
            // persisted all-time best planar speed; rounded to an int (QC floor(speed + 0.5)). Now that Race owns
            // SpeedAwardFrame too, feed both gametypes.
            int SpeedRound(float s) => (int)System.MathF.Floor(s + 0.5f);
            if (_world.GameType is XonoticGodot.Common.Gameplay.Cts cts)
            {
                _scoreSpeedAward = (SpeedRound(cts.SpeedAwardSpeed), cts.SpeedAwardHolder,
                                    SpeedRound(cts.SpeedAwardBest), cts.SpeedAwardBestHolder);
            }
            else if (_world.GameType is XonoticGodot.Common.Gameplay.Race raceGt)
            {
                _scoreSpeedAward = (SpeedRound(raceGt.SpeedAwardSpeed), raceGt.SpeedAwardHolder,
                                    SpeedRound(raceGt.SpeedAwardBest), raceGt.SpeedAwardBestHolder);
            }
        }
    }

    /// <summary>Net-id base for non-player entities, above the small ENet peer ids (so the two id spaces can't collide).</summary>
    private const int EntityNetBase = 16384;

    /// <summary>
    /// [W1-alpha-net] Quantize an entity's render alpha (QC csqcmodel m_alpha) to the wire byte: a fully-opaque
    /// entity (alpha &gt;= 1) sends 0 so the <see cref="EntityField.Alpha"/> bit stays clear and costs nothing;
    /// a transparent entity sends 1..254 (clamped so a real fade never rounds to the 0 = opaque sentinel and a
    /// near-opaque value doesn't disappear); the QC <c>-1</c> "do not render" sentinel (Running Guns hides the
    /// player model; gibbing) sends 255 as a DISTINCT hidden marker — it is NOT opaque. The client maps 0 →
    /// opaque, 255 → hidden (-1), else <c>byte/255</c>.
    /// </summary>
    private static int QuantizeAlpha(float alpha)
    {
        if (alpha < 0f)
            return 255; // QC -1 "don't render" sentinel — hidden, distinct from opaque (Running Guns player)
        if (alpha >= 1f)
            return 0;   // opaque — not networked
        return System.Math.Clamp((int)MathF.Round(alpha * 255f), 1, 254);
    }

    private static bool IsProjectileMoveType(MoveType mt)
        => mt is MoveType.Fly or MoveType.Toss or MoveType.FlyMissile or MoveType.Bounce or MoveType.BounceMissile;

    /// <summary>Classify a networked non-player entity into its CSQC render kind (None = not networkable).</summary>
    private static NetEntityKind Classify(Entity e)
    {
        string cn = e.ClassName.ToLowerInvariant();

        // Pickups (map items, weapon/ammo boxes, dropped loot) rest with MOVETYPE_TOSS but are NOT projectiles —
        // they must render their item MODEL via an EntityNode, not the procedural projectile visual. Match them by
        // their item classnames FIRST, because the projectile test below also keys on the shared FL_ITEM marker
        // (the port reuses EntFlags.Item as the FL_PROJECTILE stand-in) and would otherwise steal every Toss item.
        if (cn == "item" || cn.StartsWith("item_") || cn.StartsWith("weapon_") || cn.StartsWith("ammo_"))
            return NetEntityKind.Item;
        if (cn.Contains("gib") || cn.Contains("debris"))
            return NetEntityKind.Gib;

        // A live projectile: the port marks every projectile with EntFlags.Item (its FL_PROJECTILE stand-in — see
        // every weapon's spawn), so a projectile is that marker PLUS an owner PLUS a projectile movetype
        // (rocket/grenade/bolt/nade/mine/…). Same test as TurretAI.IsMissile; excludes ownerless map geometry.
        if ((e.Flags & EntFlags.Item) != 0 && e.Owner is not null && IsProjectileMoveType(e.MoveType))
            return NetEntityKind.Projectile;

        // [W-nadeclient] the nade_orb effect entity (heal/ammo/entrap/veil/ice/darkness orb) is SOLID_TRIGGER, so the
        // generic Trigger→Item branch below would mis-classify it as an Item AND then drop it for carrying no server
        // model (orbs render procedurally, like projectiles). Classify it explicitly BEFORE that branch.
        if (cn == "nade_orb")
            return NetEntityKind.NadeOrb;

        if (cn.Contains("_item") || e.Solid == Solid.Trigger)
            return NetEntityKind.Item;
        return NetEntityKind.Generic; // monsters, turrets, vehicles, mapobjects with alias models
    }

    /// <summary>Get (or allocate) a player's small stable net id — works for bots (which have no ENet peer).</summary>
    private int NetIdFor(Player p)
    {
        if (!_playerNetIds.TryGetValue(p, out int id))
        {
            id = _nextPlayerNetId++;
            _playerNetIds[p] = id;
        }
        return id;
    }

    /// <summary>
    /// [W-wepent-charges] Resolve the owner-only weapon-HUD ring values (QC the networked wepent.* fields:
    /// vortex_charge / vortex_chargepool_ammo / clip_load / clip_size / hagar_load / minelayer_mines /
    /// arc_heat_percent) for the local viewmodel slot, off the AUTHORITATIVE player. This is the server-side
    /// twin of NetGame.UpdateCrosshairWeaponRings (which reads the same slot-0 scratch state on a listen host);
    /// the resolved scalars ride the owner block (WriteOwnerState) so a PURE remote / dedicated-server client
    /// gets the same crosshair rings the host already shows. Each value uses the panel's -1 'no data' sentinel
    /// when the active weapon doesn't own that ring (so only the held weapon's ring draws), exactly like the host
    /// feeder. Caps (HagarLoadMax / MineLimit) are sent resolved from the descriptor too, because a pure client's
    /// g_balance_* cvars aren't guaranteed to match the server's.
    /// </summary>
    private void ResolveOwnerWeaponRings(
        Player p,
        out float charge, out float chargePool,
        out float clipLoad, out float clipSize,
        out float hagarLoad, out float hagarLoadMax,
        out float mineCount, out float mineLimit,
        out float arcHeat)
    {
        // The owner block and the per-player WepentView field are produced by the SAME resolver
        // (WepentResolver.Resolve) so they can never drift. The resolver returns the unified WepentViewState
        // (charges/clip/load/heat) off the authoritative player; this method maps that struct into the owner
        // block's existing out-params (translating the struct's 0-means-inactive representation into the
        // CrosshairPanel -1 "no ring" sentinel the owner wire expects) and resolves the two owner-only CAPS
        // (HagarLoadMax / MineLimit) locally — those caps are NOT on the per-player WepentView wire (the design
        // keeps resolved caps owner-only; the client clamps the per-player view against the panel defaults).
        WepentViewState v = XonoticGodot.Common.Gameplay.WepentResolver.Resolve(p, CountLiveMines, _world.Time);

        // [0,1] charge/pool/heat: the struct carries 0 when the active weapon doesn't own that ring; the owner
        // wire hides a ring with -1, so translate 0 → -1 (a held charge weapon at exactly 0 reads as hidden for
        // that frame, which matches "no ring to draw"). Clip uses matching sentinels already (-1 load / 0 size).
        charge = NoRingOr(v.VortexCharge);
        chargePool = NoRingOr(v.VortexChargePool);
        clipLoad = v.ClipLoad;                       // -1 = needs-reload sentinel preserved straight
        clipSize = v.ClipSize;                       // 0 = weapon has no clip → ring hidden
        hagarLoad = v.HagarLoad <= 0 ? -1f : v.HagarLoad;
        mineCount = v.MinelayerMines <= 0 ? -1f : v.MinelayerMines;
        arcHeat = NoRingOr(v.ArcHeat);

        // Resolved caps (owner-only): default to the CrosshairPanel defaults, overridden from the live weapon
        // descriptor when the matching ring is active so a pure client's g_balance_* needn't match the server.
        hagarLoadMax = 4f;
        mineLimit = 3f;
        Weapon? active = Inventory.CurrentWeapon(p);
        if (active is null || p.IsDead || p.IsObserver)
            return;
        if (active is Hagar hg && hg.Secondary.LoadMax > 0f)
            hagarLoadMax = hg.Secondary.LoadMax;
        if (active is Minelayer ml && ml.Cvars.Limit > 0)
            mineLimit = ml.Cvars.Limit;

        // Local helper: map the resolver's 0-means-inactive [0,1] value to the owner wire's -1 "no ring" sentinel.
        static float NoRingOr(float value) => value > 0f ? value : -1f;
    }

    /// <summary>
    /// [W-wepent-view] Count this player's live mines (QC the W_MineLayer_Count <c>g_mines</c> scan) — the
    /// Mine Layer count ring's current value, which isn't cached on the weapon slot. Extracted from the old
    /// ResolveOwnerWeaponRings so BOTH the owner block and the per-player WepentView (which pass this as the
    /// <c>mineCounter</c> delegate to <see cref="XonoticGodot.Common.Gameplay.WepentResolver.Resolve"/>)
    /// reuse the same authoritative count.
    /// </summary>
    /// <summary>Scratch for the per-viewer item-pickup tally written into the scoreboard block.</summary>
    private readonly List<(string icon, int count)> _itemStatsScratch = new();

    /// <summary>
    /// QC the per-viewer <c>Inventory</c> entity (common/items/inventory.qh <c>Inventory_Send</c>, customized to
    /// its owner + spectators): collect <paramref name="viewer"/>'s item-pickup tally into
    /// <paramref name="into"/> and hash it so the snapshot can gate the resend. A spectator inherits the tally of
    /// whoever they are following (QC <c>Inventory_customize</c> "sends to spectators too").
    /// </summary>
    private static void CollectItemStats(Player? viewer, List<(string icon, int count)> into, out uint hash)
    {
        into.Clear();
        hash = 2166136261u;
        Entity? src = viewer;
        if (viewer?.Spectatee is { } tgt && !tgt.IsObserver) src = tgt;
        if (src?.ItemPickupCounts is not { Count: > 0 } counts) return;

        foreach (var kv in counts)
        {
            if (kv.Value <= 0 || string.IsNullOrEmpty(kv.Key)) continue;
            into.Add((kv.Key, kv.Value));
        }
        // Stable order so the hash (and the drawn grid) don't churn with dictionary iteration order.
        into.Sort(static (a, b) => string.CompareOrdinal(a.icon, b.icon));
        foreach ((string icon, int count) in into)
        {
            foreach (char c in icon) hash = (hash ^ c) * 16777619u;
            hash = (hash ^ (uint)count) * 16777619u;
        }
    }

    private int CountLiveMines(Player p)
    {
        int n = 0;
        foreach (Entity e in _world.Services.Entities.FindByClass("mine"))
            if (ReferenceEquals(e.Owner, p) && !e.IsFreed) ++n;
        return n;
    }

    /// <summary>Owner-replicated authoritative state: origin + velocity (full precision) + onground + HUD stats +
    /// the active weapon id (the local first-person viewmodel selector — QC wepent m_weapon, which the owner is
    /// excluded from the entity stream for). Fixed-layout: keep these in lockstep with
    /// <c>ClientNet.HandleSnapshot</c>'s owner read, appending new fields at the END.</summary>
    private void WriteOwnerState(BitWriter w, Player p, PeerState st)
    {
        w.WriteVector(p.Origin, XonoticGodot.Net.NetPrecision.Float);
        w.WriteVector(p.Velocity, XonoticGodot.Net.NetPrecision.Float);
        w.WriteBool(p.OnGround);
        // Health/armor ride a SIGNED 16-bit field, and WriteShort casts (silently truncating) — so a value
        // outside ±32767 wraps and can flip sign. A player's health goes hugely negative on death: QC
        // ClientKill_Now suicides with Damage(…, 100000, DEATH_KILL, …), whose overkill excess is passed on to
        // PlayerCorpseDamage, which subtracts it from the corpse UNCLAMPED (Base does the same — but Base
        // networks health as a full 32-bit stat). At ~-65000 the truncation produced a POSITIVE health on the
        // client, so `hidden` in EquipNetworkedWeapon (Health <= 0) stayed false and the first-person weapon
        // model kept drawing through the whole death — but only after a /kill, since a normal frag leaves health
        // only slightly negative and round-trips fine. Clamp (preserving sign) instead of truncating.
        w.WriteShort(System.Math.Clamp((int)p.Health, short.MinValue, short.MaxValue));
        w.WriteShort(System.Math.Clamp((int)p.ArmorValue, short.MinValue, short.MaxValue));
        w.WriteShort(p.ActiveWeaponId); // QC wepent m_weapon — drives the local first-person viewmodel

        // QC STAT(RESPAWN_TIME) (server/client.qc:2419-2436): the dead-player respawn countdown / "press fire"
        // prompt. Absolute sim time, negated while DEAD_RESPAWNING, 0 while alive/silent. Float (a sim time can
        // exceed a short over a long match).
        w.WriteFloat(p.RespawnTimeStat);

        // QC spectatee_status (server/client.qc:1904, networked in ClientData BIT(1)): 0 = this owner is a live
        // player; its own net id = observing (free-fly; the client maps it to -1); another player's net id = it
        // is spectating that player (the client renders from their eyes). Net ids are assigned here, so the
        // Player-ref spectatee link is resolved to a wire id at send time.
        int spec;
        if (p.Spectatee is { } tgt && !tgt.IsDead && !tgt.IsObserver)
            spec = NetIdFor(tgt);
        else if (p.IsObserver)
            spec = NetIdFor(p);          // observing (not following anyone)
        else
            spec = 0;                    // a live player
        p.SpectateeStatus = spec;
        w.WriteShort(spec);

        // QC STAT(PRESSED_KEYS) (server/client.qc GetPressedKeys :2840 / SpectateCopy :1820): the owner's held-key
        // bitset (KEY_FORWARD/BACK/LEFT/RIGHT/JUMP/CROUCH/ATCK/ATCK2, bits 0..7 — the layout PressedKeysPanel reads).
        // GetPressedKeys keeps p.PressedKeys current each server frame for a live owner, the observer branch clears
        // it, and SpectateCopy copies the spectatee's bits onto a following observer — so this single owner-block
        // field already carries the RIGHT keys whether the owner is playing or spectating someone, and the client
        // HUD's pressedkeys/strafe cluster can show a SPECTATED player's keys (closes the frame.pressed_keys net gap).
        w.WriteShort(p.PressedKeys);

        // QC view punch (PM_check_punch): the weapon-recoil view kick, decayed server-side and applied to the
        // owner's rendered view angles client-side. Owner-only (only the local player sees their own kick), so
        // it rides the owner block, not the entity stream. Small + cheap; sent unconditionally for lockstep.
        w.WriteVector(p.PunchAngle, XonoticGodot.Net.NetPrecision.Float);

        // QC view punch ORIGIN kick (PM_check_punch punchvector, decayed 30u/s server-side): added to the rendered
        // view ORIGIN (vieworg += view_punchvector, cl_player.qc:570) — owner-only, same block as PunchAngle. Stock
        // content never sets it non-zero, but the seam is now wired end-to-end so any future origin kick is faithful.
        w.WriteVector(p.PunchVector, XonoticGodot.Net.NetPrecision.Float);

        // QC STAT(MOVEVARS_HIGHSPEED) per-player (Physics_UpdateStats): the RESOLVED top-speed multiplier this tick
        // — the Speed powerup ×, the speed/disability buffs ×, the entrap nade × all folded into player.SpeedMultiplier
        // by the PlayerPhysics hooks and applied in PlayerPhysics.Move. The client-prediction carrier has none of
        // those status effects, so it can't recompute this; replicate it (owner-only) so the predicted leg
        // (PlayerPhysics: input.Predicted → adopt SpeedMultiplierPredicted) scales identically to authority. Sent
        // unconditionally for lockstep (steady state 1.0); cheap.
        w.WriteFloat(p.SpeedMultiplier);

        // [T57 accuracy] — the owner's own per-weapon accuracy bytes (QC ENT_CLIENT_ACCURACY, owner-only:
        // a.drawonlytoclient = e, accuracy.qc:54). One QC accuracy_byte per Registry<Weapon> id (0 = never fired,
        // 1..101 = pct+1, 255 = >100%). Sent at the END of the owner block, in lockstep with ClientNet's matching
        // read. Change-gated like QC's accuracy_send SendFlags (accuracy.qc:38: "zero sendflags can never be
        // sent... so we can use that to say that we send no accuracy"): write the change generation, then a bool
        // "changed since last sent to THIS peer" (tracked in PeerState.LastAccuracyGen, the T53 hash pattern);
        // only when changed do the length + bytes ride along. Steady state = one int + one false bool.
        int accGen = _world.Scores.AccuracyGeneration(p);
        w.WriteLong(accGen);
        bool sendAccuracy = st.LastAccuracyGen != accGen;
        w.WriteBool(sendAccuracy);
        if (sendAccuracy)
        {
            byte[] bytes = _world.Scores.AccuracyBytes(p);
            int n = bytes.Length > 255 ? 255 : bytes.Length; // length byte — the per-weapon array is small
            w.WriteByte(n);
            w.WriteBytes(bytes.AsSpan(0, n));
            st.LastAccuracyGen = accGen;
        }

        // [W-wepent-charges] the owner-only weapon-HUD ring values (QC networked wepent vortex_charge /
        // vortex_chargepool_ammo / clip_load / clip_size / hagar_load / minelayer_mines / arc_heat_percent + the
        // resolved caps). Resolved server-side off the authoritative player so a pure remote / dedicated-server
        // client renders the same crosshair charge/clip/load/heat rings the listen host already does (previously
        // host-only). Written at the END of the owner block, read in lockstep by ClientNet.HandleSnapshot.
        ResolveOwnerWeaponRings(p,
            out float wsCharge, out float wsChargePool,
            out float wsClipLoad, out float wsClipSize,
            out float wsHagarLoad, out float wsHagarLoadMax,
            out float wsMineCount, out float wsMineLimit,
            out float wsArcHeat);
        new XonoticGodot.Net.OwnerWeaponRings
        {
            VortexCharge = wsCharge, VortexChargePool = wsChargePool,
            ClipLoad = wsClipLoad, ClipSize = wsClipSize,
            HagarLoad = wsHagarLoad, HagarLoadMax = wsHagarLoadMax,
            MineCount = wsMineCount, MineLimit = wsMineLimit,
            ArcHeat = wsArcHeat,
        }.Write(w); // ClientNet.HandleSnapshot reads this via OwnerWeaponRings.Read, in lockstep

        // Owner inventory (QC the ammo/items STATs, stats.qh RES_* + STAT_WEAPONS + the STAT_ITEMS flag bits):
        // what a PURE remote client's full HUD (ammo panel, weapons panel, powerups item rows, crosshair ring
        // color) needs; a listen host reads the same values straight off LocalServerPlayer. Appended at the END
        // of the owner block; ClientNet.HandleSnapshot reads it via OwnerInventory.Read, in lockstep — the
        // struct's single Write/Read pair owns the wire layout (see OwnerWeaponRings for why).
        new XonoticGodot.Net.OwnerInventory
        {
            Shells = p.GetResource(ResourceType.Shells),
            Bullets = p.GetResource(ResourceType.Bullets),
            Rockets = p.GetResource(ResourceType.Rockets),
            Cells = p.GetResource(ResourceType.Cells),
            Fuel = p.GetResource(ResourceType.Fuel),
            WeaponBits = p.OwnedWeaponSet.Bits,
            UnlimitedAmmo = p.UnlimitedAmmo,
            ItemFlags = p.Items,
        }.Write(w);
    }

    // =====================================================================================
    //  Lag compensation (antilag.qc) — rewind other players to the shooter's view time at fire
    // =====================================================================================

    private readonly Dictionary<Player, NVec3> _antilagRestore = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, NVec3> _antilagEntityRestore = new(ReferenceEqualityComparer.Instance);
    private bool _lagCompActive;

    /// <summary>
    /// <paramref name="shooter"/>'s MEASURED round-trip latency, in seconds — the value
    /// <c>ANTILAG_LATENCY(e) = min(0.4, CS(e).ping * 0.001)</c> (antilag.qh) consumes to pick the rewind depth.
    /// This is the smoothed <c>cmd.receivetime − cmd.time</c> the server computed from the snapshot timestamp the
    /// client echoes in each input frame (DP sv_user.c:847), NOT an assumption: 0 for a LAN/listen/bot client
    /// (fully caught up) and the genuine round trip otherwise. Replaces the earlier seq-gap heuristic.
    /// </summary>
    public float EstimatedPing(Player shooter)
    {
        if (!_byPlayer.TryGetValue(shooter, out PeerState? st) || !st.HasRtt)
            return 0f;
        return st.MeasuredRtt;
    }

    /// <summary>
    /// <see cref="ILagCompensation.Begin"/>: rewind every OTHER antilagged entity — players PLUS monsters PLUS
    /// nades — to where <paramref name="shooter"/> saw them at fire time (<c>antilag_takeback_all</c>), sampled
    /// from each recorded <see cref="AntilagBuffer"/> at the shooter's measured view-time
    /// (<c>time - ANTILAG_LATENCY</c>, <see cref="LagCompensation.ComputeTakebackTime"/>). No-op unless server-side
    /// rewind is active (<c>autocvar_g_antilag == 2</c>; mode 1 is client-verified hitscan with no takeback), for a
    /// non-client shooter (bot/monster: no remote latency to compensate), or when already active (a multi-pellet
    /// shot won't nest). The shooter itself is the <c>ignore</c> entity.
    /// </summary>
    public void BeginLagComp(Entity shooter)
    {
        // QC's hitscan path only rewinds at g_antilag == 2 (traceline_antilag: autocvar_g_antilag != 2 → lag = 0);
        // g_antilag == 1 is client-verified hitscan with NO server takeback. The cvar-unset fallback is 2 (stock).
        if (_lagCompActive || _antilagMode != 2 || shooter is not Player sp || !_byPlayer.ContainsKey(sp))
            return;
        // Per-shooter opt-out (T54): QC forces lag = 0 for a shooter whose replicated cl_noantilag is set
        // (antilag.qc:154,205-230 — antilag_takeback_all's `if (noantilag(ignore)) lag = 0`; same gate in
        // tracing.qc:115 for fireBullet). Zero lag == no rewind, so skipping Begin entirely is equivalent
        // (EndLagComp no-ops via _lagCompActive). The restore path is untouched.
        if (_world.Commands.GetClientCvarBool(sp, "cl_noantilag"))
            return;
        _lagCompActive = true;
        // Rewind depth is the shooter's measured latency alone: ANTILAG_LATENCY(e) = min(0.4, ping) (antilag.qh),
        // with NO added ticrate (the "add one ticrate?" there is a commented-out musing). The single frame-of-lag
        // compensation QC applies is on the RECORD side (altime = time + frametime*(1+nudge), see RecordTime).
        float t = LagCompensation.ComputeTakebackTime(_world.Time, EstimatedPing(sp), 0f);

        // players (FOREACH_CLIENT IS_PLAYER && it != ignore → antilag_takeback(it, CS(it), time - lag)).
        _antilagRestore.Clear();
        foreach (KeyValuePair<Player, AntilagBuffer> kv in _antilag)
        {
            if (ReferenceEquals(kv.Key, sp) || !kv.Value.HasData)
                continue;
            _antilagRestore[kv.Key] = kv.Key.Origin;
            _world.Services.Entities.SetOrigin(kv.Key, kv.Value.SampleAt(t));
        }

        // monsters + nades + manned vehicles (IL_EACH g_monsters / IL_EACH g_projectiles[classname=="nade"] →
        // antilag_takeback(it, it, ...); the vehicle case is Base's recursion into the player's `.vehicle`).
        // The shooter's OWN vehicle is excluded too: Base reaches a vehicle via its pilot's antilag_takeback,
        // and the player loop skips `it == ignore`, so a shooter piloting a vehicle never rewinds their own body.
        Entity? shooterVehicle = sp.Vehicle;
        _antilagEntityRestore.Clear();
        foreach (KeyValuePair<Entity, AntilagBuffer> kv in _antilagEntities)
        {
            if (ReferenceEquals(kv.Key, shooter) || ReferenceEquals(kv.Key, shooterVehicle)
                || kv.Key.IsFreed || !kv.Value.HasData)
                continue;
            _antilagEntityRestore[kv.Key] = kv.Key.Origin;
            _world.Services.Entities.SetOrigin(kv.Key, kv.Value.SampleAt(t));
        }
    }

    /// <summary><see cref="ILagCompensation.End"/>: restore every rewound entity — players, monsters, nades, and
    /// manned vehicle bodies — to its authoritative present position (<c>antilag_restore_all</c>).</summary>
    public void EndLagComp()
    {
        if (!_lagCompActive)
            return;
        foreach (KeyValuePair<Player, NVec3> kv in _antilagRestore)
            _world.Services.Entities.SetOrigin(kv.Key, kv.Value);
        _antilagRestore.Clear();
        foreach (KeyValuePair<Entity, NVec3> kv in _antilagEntityRestore)
            if (!kv.Key.IsFreed)
                _world.Services.Entities.SetOrigin(kv.Key, kv.Value);
        _antilagEntityRestore.Clear();
        _lagCompActive = false;
    }

    /// <summary>The ambient <see cref="LagComp"/> provider weapon code calls; routes to <see cref="BeginLagComp"/>/<see cref="EndLagComp"/>.</summary>
    private sealed class LagCompProvider : ILagCompensation
    {
        private readonly ServerNet _net;
        public LagCompProvider(ServerNet net) => _net = net;
        public void Begin(Entity shooter) => _net.BeginLagComp(shooter);
        public void End() => _net.EndLagComp();
    }

    // Snapshot entity count is patched after the loop (we don't know it up front without a pre-scan).
    private static int ReserveCount(BitWriter w) { int at = w.Length; w.WriteUShort(0); return at; }

    private static void PatchCount(BitWriter w, int at, int count)
        => w.PatchUShortAt(at, count);

    // =====================================================================================
    //  Effect + notification NET sinks (resolves the EffectEmitter / NotificationSystem TODOs)
    // =====================================================================================

    private readonly struct CapturedEffect
    {
        public readonly EffectRequest Request;
        public readonly Entity? Except;
        public CapturedEffect(in EffectRequest req) { Request = req; Except = req.Except; }
    }

    private readonly struct CapturedNotification
    {
        public readonly NotificationDispatch Dispatch;
        public CapturedNotification(in NotificationDispatch d) { Dispatch = d; }
    }

    private readonly struct CapturedSound
    {
        public readonly XonoticGodot.Engine.Simulation.SoundEvent Event;
        public CapturedSound(in XonoticGodot.Engine.Simulation.SoundEvent e) { Event = e; }
    }

    internal void CaptureEffect(in EffectRequest request) => _effectQueue.Add(new CapturedEffect(request));
    internal void CaptureNotification(in NotificationDispatch d) => _notifyQueue.Add(new CapturedNotification(d));

    /// <summary>SoundService.Broadcast handler — queue a server-emitted sound for this frame's sound bundle.</summary>
    private void OnSoundEmitted(XonoticGodot.Engine.Simulation.SoundEvent e) => _soundQueue.Add(new CapturedSound(e));

    /// <summary>
    /// Flush the per-frame captured effects + notifications to clients. Effects go in an unreliable
    /// <see cref="NetControl.EventBundle"/> (fire-and-forget, like the QC temp-entities), notifications in a
    /// reliable <see cref="NetControl.ReliableBundle"/> (kill-feed / scores must arrive). Both honor the
    /// per-request exclusion/target so a shooter doesn't get told about their own muzzleflash twice, etc.
    /// </summary>
    private void FlushEventBundles()
    {
        FlushEffects();
        FlushSounds();
        FlushNotifications();
    }

    private void FlushEffects()
    {
        if (_effectQueue.Count == 0)
            return;

        // Build one bundle per recipient only when an exclusion is in play; otherwise broadcast a shared one.
        // Simple + correct: broadcast the bundle to all, but encode each effect once; the rare "except" case
        // is handled by sending that effect only to the non-excluded peers (a per-peer pass).
        _eventWriter.Reset();
        _eventWriter.WriteByte((byte)NetControl.EventBundle);
        int countPos = ReserveCount(_eventWriter);
        int n = 0;
        bool anyExcept = false;
        for (int i = 0; i < _effectQueue.Count; i++)
        {
            CapturedEffect ce = _effectQueue[i];
            if (ce.Except is not null) { anyExcept = true; continue; } // handled per-peer below
            if (WriteEffect(_eventWriter, ce.Request)) n++;
        }
        PatchCount(_eventWriter, countPos, n);
        if (n > 0)
            BroadcastPacket(_eventWriter.WrittenSpan, reliable: false);

        // per-peer pass for excluded effects (each goes to everyone but the excluded player).
        if (anyExcept)
        {
            foreach (PeerState st in _peers.Values)
            {
                if (!st.Accepted || st.Player is null) continue;
                _eventWriter.Reset();
                _eventWriter.WriteByte((byte)NetControl.EventBundle);
                int cpos = ReserveCount(_eventWriter);
                int m = 0;
                for (int i = 0; i < _effectQueue.Count; i++)
                {
                    CapturedEffect ce = _effectQueue[i];
                    if (ce.Except is null) continue;
                    if (ReferenceEquals(ce.Except, st.Player)) continue; // excluded recipient
                    if (WriteEffect(_eventWriter, ce.Request)) m++;
                }
                PatchCount(_eventWriter, cpos, m);
                if (m > 0)
                    SendPacket(st.PeerId, _eventWriter.WrittenSpan, reliable: false);
            }
        }

        _effectQueue.Clear();
    }

    /// <summary>
    /// Encode one effect into the bundle: the registered effect id (ushort) then the EFF_NET_* body from
    /// <see cref="EffectNetProtocol.Encode"/> (the faithful QC Net_Write_Effect layout). Returns false for a
    /// non-networkable request (no registered effect / count-0 point effect) so the count stays accurate.
    /// </summary>
    private bool WriteEffect(BitWriter w, in EffectRequest r)
    {
        // Engine-fallback by-name path (QC Send_Effect_ → __pointparticles(_particleeffectnum(name))): no registered
        // Effect carries the name, so write the ByNameSentinelId + the effectinfo NAME and let the client resolve it
        // through its own effectinfo.txt catalog (DP's _particleeffectnum). This makes unregistered effectinfo names
        // (arbitrary cheat args, non-canonical impact_<material>) reach REMOTE clients, not just the listen-server
        // in-process mirror.
        if (r.Effect is null)
        {
            byte[]? nameBody = EffectNetProtocol.EncodeByName(r);
            if (nameBody is null)
                return false;
            w.WriteUShort(EffectNetProtocol.ByNameSentinelId);
            // body = length-prefixed name string, then the EFF_NET payload (framed together so the reader knows the
            // record extent from the single bodyLen, like the registered branch).
            int namePos = ReserveCount(w); // reuse the ushort-length reservation helper for the body length prefix
            int start = w.Length;
            w.WriteString(r.EffectName);
            w.WriteBytes(nameBody);
            PatchCount(w, namePos, w.Length - start);
            return true;
        }

        byte[]? regBody = EffectNetProtocol.Encode(r);
        if (regBody is null)
            return false;
        w.WriteUShort(r.Effect.RegistryId);
        w.WriteUShort(regBody.Length);
        w.WriteBytes(regBody);
        return true;
    }

    /// <summary>
    /// Flush this frame's captured positional sounds to all clients in one unreliable
    /// <see cref="NetControl.SoundBundle"/>. DP <c>sound()</c> is a pure broadcast — no per-recipient exclusion
    /// (unlike effects) — so this is a single shared bundle. One record per emission; repeats are kept
    /// (shotgun pellets, footsteps). PHS/occlusion culling is a follow-up.
    /// </summary>
    private void FlushSounds()
    {
        if (_soundQueue.Count == 0)
            return;

        _eventWriter.Reset();
        _eventWriter.WriteByte((byte)NetControl.SoundBundle);
        int countPos = ReserveCount(_eventWriter);
        int n = 0;
        for (int i = 0; i < _soundQueue.Count; i++)
            if (WriteSound(_eventWriter, _soundQueue[i].Event)) n++;
        PatchCount(_eventWriter, countPos, n);
        if (n > 0)
            BroadcastPacket(_eventWriter.WrittenSpan, reliable: false);

        _soundQueue.Clear();
    }

    /// <summary>
    /// Encode one sound record via the shared <see cref="XonoticGodot.Net.SoundWire"/> codec: sample, origin (raw
    /// floats, matching <see cref="WriteEffect"/>'s origin — NOT the quantized WriteVector path), volume,
    /// attenuation, channel, the SOURCE entity's net id, and the loop/stop flags. The net id + flags let the
    /// client key a looping AudioStreamPlayer3D by (entity, channel) and replace/stop it as the emitter moves
    /// (Arc beam owner, vehicles). Returns false for a non-stop record with an empty sample (nothing to play) so
    /// the bundle count stays accurate; a STOP record is always written though its sample is empty. Inverse of
    /// <c>ClientNet.HandleSoundBundle</c>.
    /// </summary>
    private bool WriteSound(BitWriter w, in XonoticGodot.Engine.Simulation.SoundEvent e)
    {
        if (!e.Stop && string.IsNullOrEmpty(e.Sample))
            return false;
        var rec = new XonoticGodot.Net.SoundWire
        {
            Sample = e.Sample,
            Origin = e.Origin,
            Volume = e.Volume,
            Attenuation = e.Attenuation,
            Channel = (int)e.Channel,
            SourceNetId = NetIdForEntity(e.Source),
            Loop = e.Loop,
            Stop = e.Stop,
            Pitch = e.Pitch,
        };
        rec.Write(w);
        return true;
    }

    /// <summary>
    /// The network id of a sound's SOURCE entity — the SAME id space the snapshot uses (<see cref="BuildEntitySet"/>),
    /// so the client can match a looping sound to the player/vehicle entity it follows. Players get their small
    /// stable id (<see cref="NetIdFor"/>); any other entity gets the <see cref="EntityNetBase"/>+index id. 0 when
    /// the entity is null or the id would overflow the ushort wire field — the loop then simply doesn't follow
    /// (it still plays at its emit origin).
    /// </summary>
    private int NetIdForEntity(Entity? e)
    {
        if (e is null) return 0;
        if (e is Player p) return NetIdFor(p);
        int id = EntityNetBase + e.Index;
        return id <= ushort.MaxValue ? id : 0;
    }

    private void FlushNotifications()
    {
        if (_notifyQueue.Count == 0)
            return;

        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is null) continue;

            _reliableWriter.Reset();
            _reliableWriter.WriteByte((byte)NetControl.ReliableBundle);
            int cpos = ReserveCount(_reliableWriter);
            int n = 0;
            for (int i = 0; i < _notifyQueue.Count; i++)
            {
                NotificationDispatch d = _notifyQueue[i].Dispatch;
                if (!NotificationReaches(d, st.Player)) continue;
                WriteNotification(_reliableWriter, d);
                n++;
            }
            PatchCount(_reliableWriter, cpos, n);
            if (n > 0)
                SendPacket(st.PeerId, _reliableWriter.WrittenSpan, reliable: true);
        }

        _notifyQueue.Clear();
    }

    /// <summary>
    /// Does a notification's broadcast/target reach this player? (the QC NOTIF_ONE/ALL/EXCEPT routing).
    /// Includes spectator-follow routing: IS_SPEC(to) && to.enemy == other for NOTIF_ONE/ALL_EXCEPT;
    /// IS_SPEC(to) && to.enemy.team == other.team for NOTIF_TEAM[_EXCEPT].
    /// </summary>
    private static bool NotificationReaches(in NotificationDispatch d, Player p)
    {
        switch (d.Broadcast)
        {
            case NotifBroadcast.All:
                return true;
            case NotifBroadcast.AllExcept:
                // Exclude the target and spectators following the target.
                return !ReferenceEquals(d.Target, p)
                    && !(p.IsObserver && p.Spectatee == d.Target);
            case NotifBroadcast.One:
                // Reach the target and spectators following the target.
                return ReferenceEquals(d.Target, p)
                    || (p.IsObserver && p.Spectatee == d.Target);
            case NotifBroadcast.OneOnly:
                // Reach only the target, not spectators.
                return ReferenceEquals(d.Target, p);
            case NotifBroadcast.Team:
                // Reach the target's team and spectators following the target's team.
                if (d.Target is not Player t)
                    return false;
                return t.Team == p.Team
                    || (p.IsObserver && p.Spectatee?.Team == t.Team);
            case NotifBroadcast.TeamExcept:
                // Reach the target's team except the target, and spectators following the target's team
                // (but not the target themselves).
                if (d.Target is not Player te)
                    return false;
                // QC factors `to_client != other_client` over BOTH branches (all.qc:78-82): never reach the target.
                return !ReferenceEquals(d.Target, p)
                    && (te.Team == p.Team
                        || (p.IsObserver && p.Spectatee != d.Target && p.Spectatee?.Team == te.Team));
            default:
                return true;
        }
    }

    /// <summary>
    /// Encode a notification: its registry id (ushort), the resolved text, and the raw string/float args (so
    /// the client can re-localize/re-format if it wants). Compact + reliable.
    /// </summary>
    private void WriteNotification(BitWriter w, in NotificationDispatch d)
    {
        w.WriteUShort(d.Notification.RegistryId);
        w.WriteByte((byte)d.WireType); // normally d.Notification.Type; a CenterKill retraction overrides it
        w.WriteString(d.Text);
        w.WriteByte(d.StringArgs.Length);
        for (int i = 0; i < d.StringArgs.Length; i++) w.WriteString(d.StringArgs[i]);
        w.WriteByte(d.FloatArgs.Length);
        for (int i = 0; i < d.FloatArgs.Length; i++) w.WriteFloat(d.FloatArgs[i]);
    }

    // The world's fixed tick dt (SimulationLoop.TicRate is the per-tick seconds, 1/72). Mirrored from the
    // engine constant so the input dt fallback and the advertised tick rate stay in lockstep with the sim.
    private const float SimulationLoopTicRate = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate;

    private sealed class EffectNetSink : IEffectSink
    {
        private readonly ServerNet _net;
        public EffectNetSink(ServerNet net) => _net = net;
        public void Emit(in EffectRequest request) => _net.CaptureEffect(request);
    }

    private sealed class NotificationNetSink : INotificationSink
    {
        private readonly ServerNet _net;
        public NotificationNetSink(ServerNet net) => _net = net;
        public void Dispatch(in NotificationDispatch dispatch) => _net.CaptureNotification(dispatch);
    }

    public void Dispose()
    {
        // restore the default recording sinks so a torn-down server doesn't leave dangling net sinks.
        if (EffectEmitter.Sink is EffectNetSink) EffectEmitter.Sink = EffectEmitter.Recorder;
        if (NotificationSystem.Sink is NotificationNetSink) NotificationSystem.Sink = NotificationSystem.Recorder;
        _world.Services.SoundImpl.Broadcast -= OnSoundEmitted; // drop the sound capture (avoid a re-host double-subscribe)
        if (LagComp.Provider is LagCompProvider) LagComp.Provider = null;
        // drop the per-player physics resolver only if it is still OURS (a re-host may have installed a new one).
        if (ReferenceEquals(MovementParameters.PresetProvider, _presetProvider))
            MovementParameters.PresetProvider = null;
        _master?.Dispose();
        _transport.Dispose();
    }
}

using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The client's network driver — connects to a <see cref="ServerNet"/>, streams the local player's
/// <see cref="InputCommand"/>s up, and applies the authoritative snapshots that come back: the local player
/// is <b>predicted + reconciled</b> (via <see cref="PredictionBuffer"/> + <see cref="Reconciler"/>) and every
/// remote entity is <b>interpolated</b> (via a per-entity <see cref="InterpolationBuffer"/>). Effect,
/// temp-entity and notification messages are decoded and surfaced as C# events for the renderer to consume.
///
/// Per host frame the host calls, in order:
/// <list type="number">
///   <item><see cref="Poll"/> — pump the transport (handshake + snapshots + event bundles arrive here);</item>
///   <item><see cref="SendInput"/> — sample the local input, push it to the ring buffer, predict forward, and
///         send the redundant input tail to the server.</item>
/// </list>
/// then reads <see cref="PredictedOrigin"/> (+ <see cref="PredictedStairOffset"/>) for the local camera and
/// <see cref="SampleRemote"/> for each remote entity's render pose.
///
/// The movement maths is injected as an <see cref="IMovementStep"/> (the host wraps the shared deterministic
/// <see cref="Movement"/> sim over a scratch entity — see <see cref="EntityMovementStep"/>) so this driver
/// stays movement-agnostic and the SAME sim runs client-side prediction and server-side authority.
///
/// The client also echoes the latest snapshot's server-time in each input frame so the server can MEASURE its
/// round-trip latency for lag compensation (antilag.qc), and refreshes its predictor movement-vars from the
/// server's replicated <see cref="MoveVarsBlock"/> so prediction reads authoritative physics, not constants.
/// TODO(net): a client-side interpolation delay / jitter buffer tuned to RTT (we interpolate at the latest
/// received server time, which is correct but minimally buffered). The predict/reconcile/interpolate core is complete.
/// </summary>
public sealed class ClientNet : IDisposable
{
    private NetTransport.Client _transport;
    private readonly PredictionBuffer _inputBuffer = new();
    private readonly Reconciler _reconciler;
    private readonly Func<InputCommand> _sampleInput;

    // reconnect: remembered target + loss detection so a dropped link can be re-established.
    private string _host = "";
    private int _port;
    private bool _wasConnected;

    // rate tuning: minimum seconds between input-frame transmits (0 = every tick). The redundant tail makes a
    // lower send rate loss-tolerant; raising this trades a little input latency for bandwidth (DP cl_netrate).
    private float _lastInputSend;

    private readonly BitWriter _writer = new(512);

    // delta-decompression baseline ring (reconstructs each snapshot from the server-named baseline).
    private readonly ClientSnapshotHistory _snapHistory = new();
    private readonly List<int> _staleScratch = new();

    // remote entities, keyed by their server net id.
    private readonly Dictionary<int, RemoteEntity> _remotes = new();

    private sealed class RemoteEntity
    {
        public readonly InterpolationBuffer Interp = new();
        public float LastServerTime;     // serverprevtime for the next Note()
        public NetEntityState State;     // latest decoded properties (model/frame/health/team) for the renderer
    }

    /// <summary>The client's own network entity id (assigned by the server at handshake). 0 until accepted.</summary>
    public int LocalNetId { get; private set; }

    /// <summary>True once the server has accepted the build-parity handshake.</summary>
    public bool Accepted { get; private set; }

    /// <summary>The server tick rate (Hz) advertised at handshake (drives interpolation/decay timing).</summary>
    public float ServerTickRate { get; private set; } = 72f;

    /// <summary>The latest server time carried by a snapshot (the clock we interpolate remote entities at).</summary>
    public float LatestServerTime { get; private set; }

    /// <summary>The round-trip latency to the server in milliseconds (ENet's smoothed RTT estimate), or -1 when
    /// not connected — the value the HUD ping readout shows. ~0 on a loopback listen server, the real ping on a
    /// remote one. Distinct from the server-side antilag RTT (which the server measures from the snapshot echo).</summary>
    public int PingMs => _transport.RoundTripMs();

    /// <summary>The current predicted local origin (Quake space) — what the camera follows.</summary>
    // ---- DP cl_movement / cl_nolerp (r16 A/B switches) -------------------------------------------------
    // UseClientMovement=false (DP `cl_movement 0`): the VIEW rides the server-authoritative owner state —
    // extrapolated by the owner velocity over the sub-snapshot remainder — instead of the local prediction.
    // Commands still go to the server (movement stays server-authoritative); only the rendered origin
    // switches. The DP-faithful no-prediction mode, and the clean discriminator for prediction-path bugs.
    // NoLerp (DP `cl_nolerp 1`): remote entities render their raw newest snapshot state (no two-snapshot
    // interpolation). NetGame pushes both flags + RenderNow (the interp-domain clock) each frame.
    public bool UseClientMovement = true;
    public bool NoLerp;
    public float RenderNow;
    private NVec3 _ownerOrigin, _ownerVelocity;
    private float _ownerTime = -1f;

    public NVec3 PredictedOrigin => UseClientMovement || _ownerTime < 0f
        ? _reconciler.Predicted.Origin
        : _ownerOrigin + _ownerVelocity * Math.Clamp(RenderNow - _ownerTime, 0f, 0.1f);

    /// <summary>The local player's own latest decoded entity state (kind/model/health/team/flags), captured from
    /// the snapshot before it is skipped for interpolation (we predict our own origin, not interpolate it). Lets
    /// the own name tag (chase_active + hud_shownames_self) read the same entcs slice QC iterates for self. Null
    /// until the first snapshot carrying the local entity arrives. Origin/angles here are the last SERVER values —
    /// the camera/own-tag should pair this with <see cref="PredictedOrigin"/> for the live position.</summary>
    public NetEntityState? LocalState { get; private set; }

    /// <summary>The current predicted local velocity (Quake space).</summary>
    public NVec3 PredictedVelocity => UseClientMovement || _ownerTime < 0f
        ? _reconciler.Predicted.Velocity
        : _ownerVelocity;

    /// <summary>Diagnostic (camera-trace): the magnitude (qu) of the last reconcile's prediction error — ~0 when
    /// client predict and server authority agree (the command-driven, fps-independent target); a steady nonzero
    /// value is the rubberband; a one-shot spike is a teleport/respawn.</summary>
    public float LastReconcileError => _reconciler.LastReconcileError;

    /// <summary>Owner HUD stats from the last snapshot (health/armor), for the client HUD.</summary>
    public int Health { get; private set; }
    public int Armor { get; private set; }

    /// <summary>Owner inventory stats from the last snapshot (QC the ammo RES_* STATs + STAT_WEAPONS +
    /// IT_UNLIMITED_AMMO + the STAT_ITEMS flag bits) — feeds the full HUD's ammo/weapons/powerups panels on a
    /// pure remote client (NetGame's HUD mirror player). One struct, read in lockstep with the server's
    /// <c>OwnerInventory.Write</c>.</summary>
    public XonoticGodot.Net.OwnerInventory LocalInventory { get; private set; } = XonoticGodot.Net.OwnerInventory.None;

    /// <summary>Whether the PREDICTED local player is on the ground this frame (the reconciler's live predicted
    /// state, seeded from the owner block's onground bool) — feeds the HUD mirror's OnGround flag so the
    /// physics/strafehud panels take the same ground branches on a pure client as on the listen host.</summary>
    public bool PredictedOnGround => _reconciler.Predicted.OnGround;

    /// <summary>QC view punch (<c>punchangle</c>): the weapon-recoil view kick the server applies on firing and
    /// decays (PM_check_punch). Added to the rendered view angles ONLY (not the aim), so the gun kicks the
    /// camera without skewing where shots go. Owner-replicated; zero while not recently fired.</summary>
    public NVec3 PunchAngle { get; private set; }

    /// <summary>QC view punch ORIGIN kick (<c>punchvector</c>, PM_check_punch, decayed 30u/s): the weapon-recoil
    /// origin kick added to the rendered view ORIGIN ONLY (<c>vieworg += view_punchvector</c>, cl_player.qc:570),
    /// not the aim. Owner-replicated; zero while not recently fired (stock content never drives it non-zero).</summary>
    public NVec3 PunchVector { get; private set; }

    /// <summary>
    /// QC <c>STAT(RESPAWN_TIME)</c>: the absolute sim time the local player becomes/became respawnable while
    /// dead, NEGATED while a respawn is imminent (DEAD_RESPAWNING) and 0 while alive. The HUD shows the
    /// remaining countdown (<c>|RespawnTimeStat| − LatestServerTime</c>) / "Press fire to respawn".
    /// </summary>
    public float RespawnTimeStat { get; private set; }

    /// <summary>
    /// QC <c>spectatee_status</c>: 0 = the local client is a live player; <see cref="LocalNetId"/> = observing
    /// (free-fly, not following anyone); another player's net id (&gt;0) = spectating that player (the camera +
    /// HUD should track that entity). See <see cref="IsObserving"/> / <see cref="SpectatingNetId"/>.
    /// </summary>
    public int SpectateeStatus { get; private set; }

    /// <summary>QC <c>spectatee_status_changed_time</c>: the client clock time at which <see cref="SpectateeStatus"/>
    /// last changed. Drives the 1-second shownames grace window in which the freshly-followed player's own name tag
    /// stays visible even with <c>hud_shownames_self</c> off (<c>Draw_ShowNames</c>).</summary>
    public float SpectateeStatusChangedTime { get; private set; }

    /// <summary>QC STAT(MOVEVARS_HIGHSPEED) per-player: the server's resolved top-speed multiplier for the local
    /// player (Speed powerup / speed·disability buffs / entrap nade folded in). 1 while none are active. The host
    /// mirrors this onto the prediction carrier each frame so client prediction scales speed like authority.</summary>
    public float LocalSpeedMultiplier { get; private set; } = 1f;

    /// <summary>True when the local client is observing (free-fly) and not following any specific player.</summary>
    public bool IsObserving => SpectateeStatus != 0 && SpectateeStatus == LocalNetId;

    /// <summary>The net id of the player the local client is spectating (following), or 0 if not following one.</summary>
    public int SpectatingNetId => (SpectateeStatus != 0 && SpectateeStatus != LocalNetId) ? SpectateeStatus : 0;

    // Spawn/respawn settle-snap state: a freshly spawned player teleports to the spawn point and falls to the
    // floor; the client predicts the whole fall instantly while the server lands tick-by-tick, so the reconcile
    // would fold that prediction LEAD into the view smoother. We snap (clear smoothing) until the server lands.
    private bool _wasAliveLastSnap;
    private bool _settlingAfterSpawn;

    /// <summary>The local player's active weapon id (registry id; -1 = none/holstered), from the owner block —
    /// the first-person viewmodel selector (QC wepent m_weapon → viewmodels[slot].activeweapon).</summary>
    public int ActiveWeaponId { get; private set; } = -1;

    /// <summary>The last networked scoreboard (per-player columns + team totals), for the client scoreboard HUD. Null until first received.</summary>
    public XonoticGodot.Net.ScoreboardWire? LatestScoreboard { get; private set; }

    /// <summary>The last received score LAYOUT (QC ENT_CLIENT_SCORES_INFO): the active gametype/teamplay + the
    /// applied per-mode label/flag set, for the client scoreboard HUD's per-mode columns. Null until first received.
    /// Already applied to the shared <see cref="XonoticGodot.Common.Gameplay.Scoring.GameScores"/> when set.</summary>
    public XonoticGodot.Net.ScoreInfoBlock.Decoded? LatestScoreInfo { get; private set; }

    /// <summary>The latest per-mode round/objective HUD status (T53): the CA/FT alive counts + eliminated ids,
    /// the KH OBJECTIVE_STATUS key pack, the Survival role + disclosed hunter ids. Null until a tracked mode
    /// sends one. Consumers: NetGame.UpdateModIcons (ModIconsPanel) + UpdateScoreboard (eliminated grey-out).</summary>
    public XonoticGodot.Net.GametypeStatusBlock.Decoded? LatestModeStatus { get; private set; }

    /// <summary>[T57] The local player's own per-weapon accuracy bytes (QC ENT_CLIENT_ACCURACY, owner-only):
    /// one QC accuracy_byte per Registry&lt;Weapon&gt; id (0 = never fired, 1..101 = pct+1, 255 = &gt;100%).
    /// Empty until the first owner block carrying the array arrives; updated only when the server's
    /// <see cref="LocalAccuracyGeneration"/> moves. NetGame polls the generation and decodes these into the
    /// scoreboard/weapons-panel accuracy grids.</summary>
    public byte[] LocalAccuracyBytes { get; private set; } = System.Array.Empty<byte>();

    /// <summary>[T57] The owner accuracy change counter the server replicated (QC the accuracy SendFlags
    /// generation). NetGame compares this against its last-fed value to rebuild the accuracy dictionaries only
    /// on a change.</summary>
    public int LocalAccuracyGeneration { get; private set; }

    /// <summary>[W-wepent-charges] The owner-only weapon-HUD ring scalars (QC the networked wepent.* fields:
    /// vortex charge/chargepool, clip load/size, hagar load/max, mine count/limit, arc heat), replicated on the
    /// owner block. <see cref="NetGame.UpdateCrosshairWeaponRings"/> feeds these to the crosshair on a pure
    /// remote / dedicated-server client (a listen host reads the same values off its LocalServerPlayer instead).
    /// Defaults to the panel's "no data" sentinels until the first snapshot.</summary>
    public XonoticGodot.Net.OwnerWeaponRings LocalWeaponRings { get; private set; } = XonoticGodot.Net.OwnerWeaponRings.None;

    /// <summary>QC STAT(PRESSED_KEYS): the view-player's held-key bitset (KEY_FORWARD..KEY_ATCK2, bits 0..7),
    /// replicated on the owner block — the local player's own keys while playing, the spectatee's keys while
    /// following. Feeds <see cref="NetGame"/>'s pressedkeys/strafe HUD cluster on a remote client (a listen host
    /// can read its own input locally).</summary>
    public int OwnerPressedKeys { get; private set; }

    /// <summary>The stair-smoothing Z offset to subtract from <see cref="PredictedOrigin"/> Z when placing the
    /// camera (so it glides over steps). Advanced by the REAL frame delta <paramref name="frameDt"/> (clamped),
    /// NOT the server-synced render clock — the rebasing clock's quantized jumps made the view jitter up/down.</summary>
    public float PredictedStairOffset(float frameDt) => _reconciler.GetStairSmoothOffset(frameDt);

    /// <summary>
    /// Push the live stair-smoothing tunables into the reconciler before reading <see cref="PredictedStairOffset"/>.
    /// <paramref name="smoothSpeed"/> is <c>cl_stairsmoothspeed</c> (&lt;= 0 turns smoothing OFF, matching the
    /// reference's single-cvar semantics); <paramref name="stepHeight"/> is the live <c>sv_stepheight</c> (the lag
    /// clamp); the two port-extension knobs (<c>cl_stairsmooth_snapspeed</c> / <c>cl_stairsmooth_catchuptime</c>)
    /// gate the airborne snap on vertical speed and scale the catch-up so a fast climb doesn't yank the camera.
    /// Cheap (a few field writes); call once per render frame so live cvar edits take effect immediately.
    /// </summary>
    /// <summary>DP <c>CL_RotateMoves</c> (builtin #638): rotate the view angles of every UNACKED pending input
    /// command by a warpzone crossing's transform, so post-warp reconcile replays use post-warp view angles
    /// (see <see cref="PredictionBuffer.RotatePendingViewAngles"/>). Called by the host the frame the predicted
    /// crossing happens, together with the client-side view rotation.</summary>
    public void RotatePendingMoves(System.Func<System.Numerics.Vector3, System.Numerics.Vector3> rotate)
        => _inputBuffer.RotatePendingViewAngles(rotate);

    public void ConfigureStairSmoothing(float smoothSpeed, float stepHeight, float snapSpeed, float catchupTime)
    {
        if (smoothSpeed <= 0f)
        {
            _reconciler.StairSmoothTime = 0f;       // cl_stairsmoothspeed <= 0 => off (reference semantics)
        }
        else
        {
            _reconciler.StairSmoothTime = 1f;
            _reconciler.StairSmoothSpeed = smoothSpeed;
        }
        if (stepHeight > 0f) _reconciler.StairStepHeight = stepHeight;
        if (snapSpeed > 0f) _reconciler.StairSnapVerticalSpeed = snapSpeed;
        _reconciler.StairCatchupTime = catchupTime >= 0f ? catchupTime : 0f;
    }

    /// <summary>The prediction-error smoothing offset to add to the rendered origin (decays to zero).</summary>
    public NVec3 PredictionErrorOffset(float now) => _reconciler.GetPredictionErrorOrigin(now);

    /// <summary>The prediction-error smoothing offset to add to the rendered VELOCITY (decays to zero) — QC
    /// <c>this.velocity += CSQCPlayer_GetPredictionErrorV()</c> (cl_player.qc). Feeds the view-effect velocity
    /// (bob/sway + sub-tic eye extrapolation) so a smoothed correction — notably a damage-knockback shove
    /// (<see cref="Reconciler.ForceSmoothWindow"/>) — ramps the rendered velocity instead of snapping it.</summary>
    public NVec3 PredictionVelocityErrorOffset(float now) => _reconciler.GetPredictionErrorVelocity(now);

    /// <summary>Push the live prediction-error smoothing knobs into the reconciler: <paramref name="errorComp"/>
    /// (<c>cl_movement_errorcompensation</c>) — the smoothing strength, 0 = snap to truth (no view smoothing) — and
    /// <paramref name="forceWindow"/> (<c>cl_movement_errorcompensation_force_time</c>) — the longer knockback-shove
    /// glide window. Cheap (two field writes); call once per render frame so a live cvar edit (or the settings-menu
    /// checkbox) takes effect immediately, alongside <see cref="ConfigureStairSmoothing"/>.</summary>
    public void ConfigureErrorSmoothing(float errorComp, float forceWindow)
    {
        _reconciler.ErrorCompensation = errorComp;
        _reconciler.ForceSmoothWindow = forceWindow;
    }

    // ---- renderer-facing events ----

    /// <summary>Raised for each received effect/temp-entity (the client renderer spawns the particle/sound).</summary>
    public event Action<EffectEvent>? EffectReceived;

    /// <summary>Raised for each received positional sound (the client renderer plays it spatially). DP SV_StartSound.</summary>
    public event Action<SoundEvent>? SoundReceived;

    /// <summary>Raised for each received notification (the client HUD shows the kill-feed / centerprint / announce).</summary>
    public event Action<NotificationEvent>? NotificationReceived;

    /// <summary>A decoded effect event (the client-side counterpart of <see cref="EffectRequest"/>).</summary>
    public readonly struct EffectEvent
    {
        public readonly Effect? Effect;       // resolved from the registry id; null if unknown / by-name fallback
        public readonly string EffectName;    // effectinfo name carried on the by-name engine-fallback path (Effect null)
        public readonly NVec3 Origin;
        public readonly NVec3 Velocity;
        public readonly int Count;
        public readonly NVec3 ColorMin;
        public readonly NVec3 ColorMax;
        public EffectEvent(Effect? effect, NVec3 origin, NVec3 velocity, int count, NVec3 colorMin, NVec3 colorMax)
            : this(effect, effect?.Name ?? "", origin, velocity, count, colorMin, colorMax) { }
        public EffectEvent(Effect? effect, string effectName, NVec3 origin, NVec3 velocity, int count, NVec3 colorMin, NVec3 colorMax)
        { Effect = effect; EffectName = effectName; Origin = origin; Velocity = velocity; Count = count; ColorMin = colorMin; ColorMax = colorMax; }
    }

    /// <summary>A decoded positional sound (the client-side counterpart of the engine's <c>SoundEvent</c>). Kept
    /// distinct from <c>XonoticGodot.Engine.Simulation.SoundEvent</c> so the client net layer doesn't depend on the
    /// engine record (mirrors <see cref="EffectEvent"/> vs <c>EffectRequest</c>). <see cref="SourceNetId"/> +
    /// <see cref="Loop"/>/<see cref="Stop"/> carry DP's entity+channel looping-sound model.</summary>
    public readonly struct SoundEvent
    {
        public readonly string Sample;        // bare sample path or a registered GameSound name
        public readonly NVec3 Origin;
        public readonly float Volume;
        public readonly float Attenuation;
        public readonly int Channel;          // SoundChannel, carried as a byte on the wire
        public readonly int SourceNetId;      // emitter entity net id — keys a looping sound by (entity, channel)
        public readonly bool Loop;            // a persistent looping sound (QC loopsound)
        public readonly bool Stop;            // stop the (entity, channel) sound (DP sound(e, ch, SND_Null))
        public readonly float Pitch;          // pitch scale (1.0 = normal, DP percentage encoding)
        public SoundEvent(string sample, NVec3 origin, float volume, float attenuation, int channel,
            int sourceNetId, bool loop, bool stop, float pitch = 1f)
        {
            Sample = sample; Origin = origin; Volume = volume; Attenuation = attenuation; Channel = channel;
            SourceNetId = sourceNetId; Loop = loop; Stop = stop; Pitch = pitch;
        }
    }

    /// <summary>A decoded notification event for the HUD.</summary>
    public readonly struct NotificationEvent
    {
        public readonly Notification? Notification;  // resolved from the registry id; null if unknown
        public readonly MsgType Type;
        public readonly string Text;                 // pre-formatted message / announcer sound name
        public readonly string[] StringArgs;
        public readonly float[] FloatArgs;
        public NotificationEvent(Notification? n, MsgType type, string text, string[] s, float[] f)
        { Notification = n; Type = type; Text = text; StringArgs = s; FloatArgs = f; }
    }

    public ClientNet(NetTransport.Client transport, IMovementStep movement, Func<InputCommand> sampleInput)
    {
        _transport = transport;
        _sampleInput = sampleInput;
        _reconciler = new Reconciler(_inputBuffer, movement)
        {
            // smoothing on by default so corrections/steps are visually gradual (cl_movement_errorcompensation).
            ErrorCompensation = 1f,
            TickRate = 72f,
        };

        _transport.PacketReceived += OnPacket;
    }

    /// <summary>Convenience: connect to <paramref name="host"/>:<paramref name="port"/> and build the driver.</summary>
    public static ClientNet? Connect(string host, int port, IMovementStep movement, Func<InputCommand> sampleInput)
    {
        NetTransport.Client? t = NetTransport.Client.Connect(host, port);
        if (t is null) return null;
        var cn = new ClientNet(t, movement, sampleInput) { _host = host, _port = port };
        return cn;
    }

    /// <summary>Minimum seconds between input-frame transmits (0 = every tick). DP <c>cl_netfps</c>-style pacing.</summary>
    public float InputSendInterval { get; set; }

    /// <summary>DP <c>cl_netimmediatebuttons</c>: when set, a command whose button bits CHANGED (or that carries an
    /// impulse) is transmitted immediately, bypassing the <see cref="InputSendInterval"/> rate gate — so fire/jump/
    /// weapon-switch reach the server with minimal latency at high fps while steady movement stays rate-limited.</summary>
    public bool ImmediateButtons { get; set; } = true;
    private int _lastSentButtons; // last transmitted button bits (for the ImmediateButtons change-detect)

    /// <summary>(Fix B) Set by the host each frame when the last render frame was a HITCH (dt &gt; threshold) — i.e. the
    /// shared listen-server thread stalled (GC / heavy map streaming). Arms the post-hitch stall hold in
    /// <see cref="HandleSnapshot"/>: a moderate (non-teleport) correction right after a stall is the server being
    /// transiently behind, not a real desync, so we keep predicting forward (bounded) instead of snapping back.</summary>
    public bool RecentHitch { get; set; }
    // Diagnostics surfaced by net_input_trace (NetGame [nettrace]): DbgPush = input commands generated; DbgSend =
    // datagrams transmitted; DbgRedundancy = commands per datagram. DbgEnet = the server peer's ENet throttle/loss/RTT.
    public int DbgPush, DbgSend, DbgRedundancy;
    public (double Throttle, double ThrottleLimit, double Loss, double Rtt) DbgEnet => _transport.DbgEnetStats();
    private bool _hitchHoldActive;
    private int _hitchSkips;
    private const int MaxHitchSkips = 8; // bound: a real desync still corrects after this many held snapshots

    /// <summary>True when the link was established and has since dropped — the host can prompt/auto <see cref="Reconnect"/>.</summary>
    public bool ConnectionLost => _wasConnected && !_transport.IsActive;

    /// <summary>
    /// Re-establish the link to the original server after a drop: rebuild the ENet transport and reset the
    /// handshake + delta baseline + remote-entity state so the fresh session starts clean. Returns false if the
    /// socket couldn't be created.
    /// </summary>
    public bool Reconnect()
    {
        NetTransport.Client? t = NetTransport.Client.Connect(_host, _port);
        if (t is null)
            return false;

        _transport.PacketReceived -= OnPacket;
        _transport.Dispose();
        _transport = t;
        _transport.PacketReceived += OnPacket;

        _handshakeSent = false;
        _wasConnected = false;
        Accepted = false;
        _remotes.Clear();
        LocalState = null; // drop the captured local entcs slice across reconnect/map change
        // session-scoped replication state: a fresh session re-sends all cvars and starts override-free.
        MovementParameters.PredictionOverride = null;
        _replicatedLastSent.Clear();
        _replicateSendAll = false;
        _radarPingTimes.Clear(); // drop stale per-waypoint radar ping stamps across reconnect/map change
        _radarLinks.Clear();     // drop stale Onslaught radar links across reconnect/map change
        // Reset the echoed snapshot clock so the first post-reconnect input frame doesn't carry a stale large
        // server-time the server would read as a near-zero/negative RTT (it self-heals via the rtt<0 clamp, but
        // this avoids the brief under-report). 0 = "no snapshot yet", which the server treats as "measure nothing".
        LatestServerTime = 0f;
        return true;
    }

    // =====================================================================================
    //  Per-frame drive
    // =====================================================================================

    /// <summary>
    /// Pump the transport: completes the ENet connection, sends the build-parity handshake once connected,
    /// and dispatches any received snapshots / event bundles. Call once per host frame before reading state.
    /// </summary>
    public void Poll()
    {
        _transport.Poll();

        if (_transport.IsConnected)
            _wasConnected = true; // arm loss detection (ConnectionLost / Reconnect)

        // Once the ENet link is up but we haven't sent our handshake, send it (the server replies accept/reject).
        if (_transport.IsConnected && !_handshakeSent)
        {
            SendHandshake();
            _handshakeSent = true;
        }
    }

    private bool _handshakeSent;

    /// <summary>This client's cryptographic identity (its public-key fingerprint is the stable player id). Defaults
    /// to a fresh ephemeral key; the host may assign a persisted <see cref="PlayerIdentity"/> before connecting.</summary>
    public PlayerIdentity Identity { get; set; } = PlayerIdentity.Generate();

    private void SendHandshake()
    {
        _writer.Reset();
        _writer.WriteByte((byte)NetControl.HandshakeRequest);
        _writer.WriteULong(NetProtocol.BuildParity());
        _writer.WriteString(LocalPlayerName);
        byte[] pub = Identity.PublicKey;
        _writer.WriteUShort(pub.Length);
        _writer.WriteBytes(pub);
        _transport.SendToServer(_writer.WrittenSpan, reliable: true);
    }

    /// <summary>Sign the server's session challenge and return the proof (SessionAuth). Admits us once verified.</summary>
    private void HandleChallenge(ref BitReader r)
    {
        ReadOnlySpan<byte> challenge = r.ReadBytes(r.ReadUShort());
        if (r.BadRead) return;
        byte[] sig = Identity.Sign(challenge);
        _writer.Reset();
        _writer.WriteByte((byte)NetControl.HandshakeAuth);
        _writer.WriteUShort(sig.Length);
        _writer.WriteBytes(sig);
        _transport.SendToServer(_writer.WrittenSpan, reliable: true);
    }

    /// <summary>The name advertised to the server at handshake. Host may set this before connecting.</summary>
    public string LocalPlayerName { get; set; } = "player";

    /// <summary>
    /// When true (cl_movement_perframe, driven by the host), the input frame is flagged so the server drains ALL
    /// pending commands that tick and runs movement per command with each command's DeltaTime (DP-style variable
    /// dt); false = the legacy one-command-per-tick consumption. Carried on the wire so client + server agree
    /// without a shared cvar store. The host is responsible for stamping the matching per-frame DeltaTime.
    /// </summary>
    public bool PerFrameInput { get; set; }

    /// <summary>
    /// Sample the local input this tick, push it to the input ring buffer, predict forward (replay from the
    /// last acked authoritative state), and send the redundant input tail to the server. Returns the seq
    /// stamped on this tick's command. No-op until the handshake is accepted.
    ///
    /// <para>The sampled <see cref="InputCommand"/> may carry a one-shot <see cref="InputCommand.Impulse"/> (QC
    /// usercmd.impulse — a weapon switch/reload, stamped by the host's sampler). It rides the command transparently
    /// through <see cref="InputCommand.Serialize"/>; because the impulse is part of THIS command's Seq, the
    /// redundant tail re-sends the same numbered command, and the server's Seq-dedup processes it exactly once
    /// (then zeroes it on its cached command so a starved tick can't re-fire it). No separate channel is needed —
    /// this matches QC, where the impulse is part of the move command.</para>
    /// </summary>
    public uint SendInput(float now)
    {
        if (!Accepted)
            return 0;

        // Push changed replicated cl_* cvars on the QC ReplicateVars cadence (piggybacks the input clock).
        PumpReplicatedCvars(now);

        InputCommand cmd = _sampleInput();
        uint seq = _inputBuffer.Push(cmd);
        DbgPush++; // net_input_trace diagnostic

        // Re-predict from the last authoritative state with the freshly-extended input history so the local
        // view reflects this tick immediately (client-side prediction). We reconcile against the last server
        // state we have; if none yet, the reconciler simply replays from the current predicted state.
        Predict(now);

        // Send the redundant tail (unreliable): a single dropped datagram won't strand an input. Lead with the
        // newest snapshot we decoded so the server can delta the next snapshot against a baseline we hold. The
        // transmit is rate-gated to cl_netfps (InputSendInterval); the redundancy widens to still cover every tick.
        //
        // cl_netimmediatebuttons (DP cl_input.c): an IMPORTANT command — a weapon switch/reload (impulse) or any
        // button-state CHANGE (fire/jump/crouch press or release) — is sent RIGHT NOW, bypassing the rate gate, so
        // fire/aim/jump reach the server with minimal latency at high fps; steady movement-only input stays gated to
        // cl_netfps. Button changes are infrequent (you don't toggle fire 144×/s), so this doesn't flood the link —
        // it just removes the up-to-one-interval (~13.9 ms) wait a fire would otherwise sit through.
        bool important = cmd.Impulse != 0 || (ImmediateButtons && cmd.Buttons != _lastSentButtons);
        if (important || InputSendInterval <= 0f || now - _lastInputSend >= InputSendInterval)
        {
            int redundancy = InputSendInterval > 0f ? (int)(InputSendInterval * ServerTickRate) + 3 : 3;
            _writer.Reset();
            _writer.WriteByte((byte)NetControl.InputFrame);
            _writer.WriteUShort(_snapHistory.LastDecodedSeq);
            // Echo the server-time of the latest snapshot we hold so the server can MEASURE our round-trip latency
            // (receive-time − this echoed time = RTT), the faithful port of DP's cmd.receivetime − cmd.time
            // (sv_user.c) that feeds ANTILAG_LATENCY. 0 before the first snapshot (the server then measures nothing).
            _writer.WriteFloat(LatestServerTime);
            // Per-frame (variable-dt) mode flag: tells the server to drain ALL pending commands this tick and run
            // movement per command with each command's DeltaTime, instead of one-per-tick. Rides the wire so the
            // two ends agree without a shared cvar store (and works for remote, not just the in-process listen path).
            _writer.WriteByte(PerFrameInput ? (byte)1 : (byte)0);
            _inputBuffer.WriteRedundant(_writer, redundancy);
            _transport.SendToServer(_writer.WrittenSpan, reliable: false);
            DbgSend++; DbgRedundancy = redundancy; // net_input_trace diagnostic
            // Flush this input onto the wire NOW (send-only) instead of letting it sit until the next Poll(): on
            // the in-process listen loop the server's next Tick then consumes it THIS frame rather than a render-
            // frame later, cutting the input→fire latency that makes firing (and Blaster trick-jump timing) feel
            // behind the keypress. Harmless on a remote client (the datagram just leaves a hair sooner).
            _transport.Flush();
            _lastInputSend = now;
            _lastSentButtons = cmd.Buttons; // for the cl_netimmediatebuttons change-detect above
        }
        return seq;
    }

    // The last authoritative state + acked seq we reconcile from (updated on each snapshot).
    private PredictedState _serverState;
    private uint _serverAckedSeq;
    private PlayerState _vars = DefaultVars();
    private PredictedState _previousPredictionAtAck;

    /// <summary>Replay unacked inputs on top of the last server state to refresh <see cref="PredictedOrigin"/>
    /// (replay-only — the prediction error is measured/armed in <see cref="HandleSnapshot"/> when a new ack
    /// lands, not on every input tick, so smoothing decays cleanly between snapshots).</summary>
    private void Predict(float now)
        => _reconciler.Predict(_serverState, _serverAckedSeq, _vars, now);

    // =====================================================================================
    //  Receive
    // =====================================================================================

    private void OnPacket(int from, int channel, byte[] data)
    {
        var r = new BitReader(data);
        var control = (NetControl)r.ReadByte();
        switch (control)
        {
            case NetControl.HandshakeChallenge: HandleChallenge(ref r); break;
            case NetControl.HandshakeAccept: HandleAccept(ref r); break;
            case NetControl.HandshakeReject: HandleReject(ref r); break;
            case NetControl.Snapshot: HandleSnapshot(ref r); break;
            case NetControl.EventBundle: HandleEventBundle(ref r); break;
            case NetControl.SoundBundle: HandleSoundBundle(ref r); break;
            case NetControl.ReliableBundle: HandleReliableBundle(ref r); break;
            case NetControl.ServerPrint: HandleServerPrint(ref r); break;
            case NetControl.MinigameState: HandleMinigameState(ref r); break;
            case NetControl.MatchState: HandleMatchState(ref r); break;
            case NetControl.Waypoints: HandleWaypoints(ref r); break;
            case NetControl.ItemsTime: HandleItemsTime(ref r); break;
            case NetControl.RadarLinks: HandleRadarLinks(ref r); break;
            case NetControl.ClientInit: HandleClientInit(ref r); break;
            case NetControl.MapVote: HandleMapVote(ref r); break;
            default: break;
        }
    }

    /// <summary>Raised with a decoded minigame-session envelope (the S2C <see cref="NetControl.MinigameState"/>
    /// push) — the host drives the minigame board overlay + menu from it. A null
    /// <see cref="XonoticGodot.Game.Net.MinigameNetState.Envelope.Session"/> means the local player left / has no
    /// active minigame. The C# stand-in for CSQC <c>activate_minigame</c>/<c>deactivate_minigame</c>.</summary>
    public event Action<XonoticGodot.Game.Net.MinigameNetState.Envelope>? MinigameStateReceived;

    private void HandleMinigameState(ref BitReader r)
    {
        XonoticGodot.Game.Net.MinigameNetState.Envelope env = XonoticGodot.Game.Net.MinigameNetState.DecodeEnvelope(ref r);
        if (r.BadRead)
            return;
        MinigameStateReceived?.Invoke(env);
    }

    // ---- match clock (S2C NetControl.MatchState; QC STAT(GAMESTARTTIME)/TIMELIMIT/WARMUP) ----

    /// <summary>True once the first <see cref="NetControl.MatchState"/> has arrived (the TIMER panel stays hidden
    /// until then). The fields below are on the SAME clock as <see cref="LatestServerTime"/> (the server sim time).</summary>
    public bool HasMatchState { get; private set; }

    /// <summary>QC GAMESTARTTIME — absolute server time the match started (warmup-end). Elapsed = LatestServerTime − this.</summary>
    public float MatchStartTime { get; private set; }

    /// <summary>QC TIMELIMIT × 60 — the match time limit in seconds (0 = no limit).</summary>
    public float MatchTimeLimit { get; private set; }

    /// <summary>QC warmup_stage — true during the pre-match warmup phase.</summary>
    public bool MatchWarmup { get; private set; }

    /// <summary>QC warmup_limit — warmup duration in seconds (≤ 0 = until ready / no limit).</summary>
    public float MatchWarmupLimit { get; private set; }

    /// <summary>End-of-match intermission/scoreboard-freeze flag.</summary>
    public bool MatchIntermission { get; private set; }

    /// <summary>QC GET_NEXTMAP / the <c>_nextmap</c> the server broadcasts (Send_NextMap_To_Player) — the upcoming
    /// map name shown on the scoreboard's "Next map:" line. Empty until the server has a queued/rotation pick.</summary>
    public string NextMap { get; private set; } = "";

    /// <summary>QC STAT(OVERTIMES) (common/stats.qh): 0 = none, N = the Nth overtime, OVERTIME_SUDDENDEATH (1&lt;&lt;24)
    /// = sudden death. Drives the TIMER panel's persistent "Overtime #N" / "Sudden Death" subtext.</summary>
    public int MatchOvertimes { get; private set; }

    /// <summary>[W14a] QC <c>randomseed.cnt</c> (server/world.qc): the server's networked deterministic-RNG seed
    /// (0..65535), re-rolled every 5s and delivered on the match-state channel. The client reseeds
    /// <see cref="RandomRng"/> from it so a future predicting effect can reproduce the server's random stream. There
    /// is no client effect consuming it yet (the seam is shipped additive); 0 until the first match-state arrives.</summary>
    public int RandomSeed { get; private set; }

    /// <summary>[W14a] The client's per-instance deterministic RNG (QC psrandom), reseeded from the networked
    /// <see cref="RandomSeed"/>. Per-instance (NOT the static <see cref="XonoticGodot.Common.Math.Prandom"/> default)
    /// so on a listen server the client's draws never corrupt the server world's stream. RESERVED — no consumer yet.</summary>
    public XonoticGodot.Common.Math.PrandomContext RandomRng { get; } = new();

    private void HandleMatchState(ref BitReader r)
    {
        float gameStart = r.ReadFloat();
        float timeLimit = r.ReadFloat();
        bool warmup = r.ReadBool();
        float warmupLimit = r.ReadFloat();
        bool intermission = r.ReadBool();
        string nextMap = r.ReadString();
        int overtimes = r.ReadLong();
        // [W14a] QC randomseed.cnt — read in the SAME trailing position ServerNet.SendMatchState appended it (after
        // overtimes). A truncated old-server packet leaves BadRead set; we guard below so a missing seed is harmless.
        int randomSeed = r.ReadUShort();
        if (r.BadRead)
            return;
        MatchStartTime = gameStart;
        MatchTimeLimit = timeLimit;
        MatchWarmup = warmup;
        MatchWarmupLimit = warmupLimit;
        MatchIntermission = intermission;
        NextMap = nextMap ?? "";
        MatchOvertimes = overtimes;
        // [W14a] reseed the per-instance deterministic RNG only when the seed actually changes (every 5s server-side),
        // so a steady ~1×/s match-state heartbeat doesn't keep resetting the stream mid-window.
        if (randomSeed != RandomSeed)
        {
            RandomSeed = randomSeed;
            RandomRng.Seed((uint)randomSeed);
        }
        HasMatchState = true;
    }

    // ---- client init constants (S2C NetControl.ClientInit; QC ENT_CLIENT_INIT / ClientInit_misc) ----

    /// <summary>[W14a-QW4] True once the one-shot <see cref="NetControl.ClientInit"/> constant bundle has arrived.</summary>
    public bool HasClientInit { get; private set; }

    /// <summary>QC <c>hook_shotorigin</c> — the grappling-hook muzzle offset (the port models a single offset, not the
    /// Base per-cl_gunalign array; see ServerNet.SendClientInit). Used to position the hook reel/beam visual.</summary>
    public NVec3 HookShotOrigin { get; private set; }

    /// <summary>QC <c>autocvar_g_trueaim_minrange</c> — the min range below which true-aim crosshair correction is
    /// skipped (the client crosshair true-aim trace reads it).</summary>
    public float TrueAimMinRange { get; private set; }

    /// <summary>QC <c>g_balance_damagepush_speedfactor</c> — the knockback velocity scale (decoded for the client-side
    /// damage-push prediction; RESERVED until a client consumer reads it).</summary>
    public float DamagePushSpeedFactor { get; private set; }

    /// <summary>QC <c>g_balance_armor_blockpercent</c> (Base <c>armorblockpercent</c>) — the fraction of damage armor
    /// absorbs; the client damage/HUD math reads it.</summary>
    public float ArmorBlockPercent { get; private set; }

    /// <summary>QC networked <c>serverflags</c> — the SERVERFLAG_* bitmap (fullbright / forbid-pickuptimer / teamplay).
    /// The client gates fullbright player rendering + the HUD pickup timer off these bits.</summary>
    public int ServerFlags { get; private set; }

    /// <summary>QC <c>world.fog</c> (when <c>sv_foginterval</c>) — the server's fog string, or "" when none.</summary>
    public string ServerFog { get; private set; } = "";

    /// <summary>QC <c>g_nexball_meter_period</c> — the nexball powershot meter cycle period.</summary>
    public float NexballMeterPeriod { get; private set; }

    private void HandleClientInit(ref BitReader r)
    {
        // Same field order as ServerNet.SendClientInit.
        NVec3 hookOrigin = r.ReadVector(NetPrecision.Float);
        float trueAimMin = r.ReadFloat();
        float damagePush = r.ReadFloat();
        float armorBlock = r.ReadFloat();
        int serverFlags = r.ReadLong();
        string fog = r.ReadString();
        float nexballMeter = r.ReadFloat();
        if (r.BadRead)
            return;
        HookShotOrigin = hookOrigin;
        TrueAimMinRange = trueAimMin;
        DamagePushSpeedFactor = damagePush;
        ArmorBlockPercent = armorBlock;
        ServerFlags = serverFlags;
        ServerFog = fog ?? "";
        NexballMeterPeriod = nexballMeter;
        HasClientInit = true;
    }

    // ---- end-of-match map / gametype vote ballot (S2C NetControl.MapVote) ----

    /// <summary>The networked map/gametype-vote ballot for this client (QC the ReadMapVote net feed), or null when
    /// no ballot is live. The client-side stand-in for the listen host's in-process <c>MapVoting</c>: on a pure
    /// remote (--connect) client there is no server world, so NetGame.UpdateMapVotePanel renders the ballot from
    /// this instead. Replaced wholesale each <see cref="NetControl.MapVote"/> message.</summary>
    public NetMapVote? MapVote { get; private set; }

    /// <summary>
    /// Decode the end-of-match map/gametype-vote ballot (S2C <see cref="NetControl.MapVote"/>). Field order mirrors
    /// the server writer (ServerNet map-vote send) exactly. Populates <see cref="MapVote"/>. Each control message is
    /// its own datagram, so a short/bad read just drops this ballot (no stream desync).
    /// </summary>
    private void HandleMapVote(ref BitReader r)
    {
        bool showing = r.ReadBool();
        bool finished = r.ReadBool();
        bool isGametypeVote = r.ReadBool();
        bool detail = r.ReadBool();
        float remaining = r.ReadFloat();
        int winner1Based = r.ReadShort();
        int own = r.ReadShort();
        bool abstain = r.ReadBool();
        int n = r.ReadByte();
        var cands = new List<NetMapVote.Cand>(n);
        for (int i = 0; i < n; i++)
        {
            string mapName = r.ReadString();
            int votes = r.ReadShort();
            bool available = r.ReadBool();
            string suggester = r.ReadString();
            cands.Add(new NetMapVote.Cand
            {
                MapName = mapName ?? "",
                Votes = votes,
                Available = available,
                Suggester = suggester ?? "",
            });
        }
        if (r.BadRead)
            return;
        var ballot = new NetMapVote
        {
            Showing = showing,
            Finished = finished,
            IsGametypeVote = isGametypeVote,
            Detail = detail,
            Remaining = remaining,
            Winner1Based = winner1Based,
            Own = own,
            AbstainPresent = abstain,
        };
        ballot.Candidates.AddRange(cands);
        MapVote = ballot;
    }

    // ---- waypoint sprites (S2C NetControl.Waypoints; QC the networked ENT_CLIENT_WAYPOINT entities) ----

    private readonly List<XonoticGodot.Common.Gameplay.Waypoints.WaypointNet> _waypoints = new();

    /// <summary>The live waypoint sprites for this client (gametype objectives + player pings), already
    /// team/rule-filtered by the server. Drives the 3D in-world sprite layer + the radar icons. Replaced
    /// wholesale each <see cref="NetControl.Waypoints"/> message; empty in waypoint-less modes (plain DM).</summary>
    public IReadOnlyList<XonoticGodot.Common.Gameplay.Waypoints.WaypointNet> Waypoints => _waypoints;

    /// <summary>Per-waypoint-id radar ping timestamps (server time of the last ping) — the C# analogue of QC's
    /// per-icon <c>teamradar_times</c> array. The radar draws an expanding gfx/teamradar_ping ring for ~1s after
    /// each stamp. Stamped on decode when bit 7 of the radar-icon byte is set; read by <see cref="Game.Hud.RadarPanel"/>.</summary>
    private readonly Dictionary<int, float> _radarPingTimes = new();

    /// <summary>The server time of the most recent radar ping for <paramref name="waypointId"/>, or 0 if none.</summary>
    public float RadarPingTime(int waypointId)
        => _radarPingTimes.TryGetValue(waypointId, out float t) ? t : 0f;

    // ---- onslaught radar links (S2C NetControl.RadarLinks; QC the networked ENT_CLIENT_RADARLINK entities) ----

    /// <summary>One Onslaught radar-link segment for the radar (QC draw_teamradar_link): the two endpoint world
    /// XY positions plus each end's team color code (0 = neutral/white). Z is irrelevant to the top-down radar.</summary>
    public readonly record struct RadarLink(NVec3 A, int TeamA, NVec3 B, int TeamB);

    private readonly List<RadarLink> _radarLinks = new();

    /// <summary>The live Onslaught control-point/generator connection lines (QC g_radarlinks). Drives the radar's
    /// draw_teamradar_link pass. Replaced wholesale each <see cref="NetControl.RadarLinks"/> message; empty in any
    /// non-Onslaught mode. Read by <see cref="Game.Hud.RadarPanel"/>.</summary>
    public IReadOnlyList<RadarLink> RadarLinks => _radarLinks;

    private void HandleRadarLinks(ref BitReader r)
    {
        int count = r.ReadByte();
        // Decode into a temp so a truncated packet never leaves a half-updated list on screen.
        var tmp = new List<RadarLink>(count);
        for (int i = 0; i < count; i++)
        {
            float ax = r.ReadFloat(), ay = r.ReadFloat();
            int ta = r.ReadByte();
            float bx = r.ReadFloat(), by = r.ReadFloat();
            int tb = r.ReadByte();
            tmp.Add(new RadarLink(new NVec3(ax, ay, 0f), ta, new NVec3(bx, by, 0f), tb));
        }
        if (r.BadRead)
            return;
        _radarLinks.Clear();
        _radarLinks.AddRange(tmp);
    }

    private void HandleWaypoints(ref BitReader r)
    {
        int count = r.ReadByte();
        // Decode into a temp so a truncated packet never leaves a half-updated list on screen.
        var tmp = new List<XonoticGodot.Common.Gameplay.Waypoints.WaypointNet>(count);
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadLong();
            float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
            int team = r.ReadByte();
            string sprite = r.ReadString();
            // QC: the radar-icon byte packs m_radaricon in the low 7 bits and a ping pulse in bit 7
            // (waypointsprites.qc:187-192). Stamp a per-id ping time on a fresh ping so the radar can draw an
            // expanding gfx/teamradar_ping ring; de-dupe within the 0.3s ping window (one ring per ping event).
            int radarByte = r.ReadByte();
            int radarIcon = radarByte & 0x7F;
            if ((radarByte & 0x80) != 0)
            {
                float servTime = LatestServerTime;
                if (!_radarPingTimes.TryGetValue(id, out float prev) || servTime - prev >= 0.3f)
                    _radarPingTimes[id] = servTime;
            }
            float cr = r.ReadByte() / 255f, cg = r.ReadByte() / 255f, cb = r.ReadByte() / 255f;
            float health = r.ReadFloat();
            float fade = r.ReadFloat();
            float helpme = r.ReadByte();
            float maxDist = r.ReadFloat();
            bool hideable = r.ReadBool();
            tmp.Add(new XonoticGodot.Common.Gameplay.Waypoints.WaypointNet(
                id, new NVec3(x, y, z), team, sprite, radarIcon, new NVec3(cr, cg, cb),
                health, fade, helpme, maxDist, hideable));
        }
        if (r.BadRead)
            return;
        _waypoints.Clear();
        _waypoints.AddRange(tmp);
    }

    // ---- items-time (S2C NetControl.ItemsTime; QC the CSQC itemstime net-temp message) ----

    private readonly Dictionary<string, float> _itemTimes = new(StringComparer.Ordinal);

    /// <summary>The item respawn-time table for this client (QC <c>ItemsTime_time[]</c>), already send-gated by
    /// the server — keyed by the HUD panel's item names with the negative "available now" encoding. Fed to
    /// <see cref="Game.Hud.ItemsTimePanel.SetItemTimes"/> on the remote-client path. Replaced wholesale per
    /// message.</summary>
    public IReadOnlyDictionary<string, float> ItemTimes => _itemTimes;

    /// <summary>True once an <see cref="NetControl.ItemsTime"/> message has arrived (the panel feed waits for it
    /// on the remote path, like <see cref="HasMatchState"/>).</summary>
    public bool HasItemsTime { get; private set; }

    /// <summary>The live <c>STAT(ITEMSTIME)</c> tier (= server <c>sv_itemstime</c>, 0/1/2), networked alongside the
    /// table — drives the panel enable gate (mode 2 "also alive players when ==2") and the spectator waypoint
    /// gate on a remote client.</summary>
    public int ItemsTimeTier { get; private set; }

    private void HandleItemsTime(ref BitReader r)
    {
        int tier = r.ReadByte();
        int count = r.ReadByte();
        var tmp = new List<KeyValuePair<string, float>>(count);
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            float time = r.ReadFloat();
            tmp.Add(new KeyValuePair<string, float>(name, time));
        }
        if (r.BadRead)
            return;
        ItemsTimeTier = tier;
        _itemTimes.Clear();
        foreach (KeyValuePair<string, float> kv in tmp)
            if (!string.IsNullOrEmpty(kv.Key) && float.IsFinite(kv.Value) && kv.Value != -1f)
                _itemTimes[kv.Key] = kv.Value; // -1 = item type not present / reset → omit so the panel hides it
        HasItemsTime = true;
    }

    /// <summary>Raised with a line of server console output (a reply to <see cref="SendStringCommand"/> or a
    /// server notice) — the host appends it to the in-game console. DP <c>svc_print</c>.</summary>
    public event Action<string>? PrintReceived;

    /// <summary>
    /// Send a console command line to run on the server on our behalf (DP <c>clc_stringcmd</c>) — how the
    /// in-game console reaches a REMOTE server's gameplay commands (kill/say/team/…). Reliable; the reply (if
    /// any) arrives as a <see cref="NetControl.ServerPrint"/> raised on <see cref="PrintReceived"/>. No-op until
    /// the handshake is accepted (the server only honours commands from an authed peer).
    /// </summary>
    public void SendStringCommand(string line)
    {
        if (!Accepted || string.IsNullOrWhiteSpace(line))
            return;
        _writer.Reset();
        _writer.WriteByte((byte)NetControl.ClientCommand);
        _writer.WriteString(line);
        _transport.SendToServer(_writer.WrittenSpan, reliable: true);
    }

    private void HandleServerPrint(ref BitReader r)
    {
        string text = r.ReadString();
        if (r.BadRead)
            return;
        PrintReceived?.Invoke(text);
    }

    private void HandleAccept(ref BitReader r)
    {
        LocalNetId = r.ReadUShort();
        ServerTickRate = r.ReadFloat();
        if (ServerTickRate <= 0f) ServerTickRate = 72f;
        ServerName = r.ReadString();
        // The current map + gametype (appended to the accept): a pure client loads this BSP for the world render
        // and its prediction collision. Read in the same order ServerNet.HandleAuth writes them.
        ServerMapName = r.ReadString();
        ServerGametype = r.ReadString();
        _reconciler.TickRate = ServerTickRate;
        Accepted = true;
        // ReplicateVars_Start (lib/replicate.qh:50-57): push ALL replicated cvars once at session init, then the
        // periodic watcher (PumpReplicatedCvars) only sends changes.
        _replicateSendAll = true;
        _nextReplicateTime = 0f;
        GD.Print($"[ClientNet] handshake accepted by '{ServerName}': netId {LocalNetId}, tickrate {ServerTickRate} Hz.");
    }

    // =====================================================================================
    //  Replicated client cvars — the CSQC ReplicateVars watcher (lib/replicate.qh)
    // =====================================================================================

    /// <summary>
    /// The client cvars automatically replicated to the server via <c>cmd sentcvar</c> — the full QC REPLICATE
    /// set (common/replicate.qh) plus two port-specific entries. The server-side gate (Commands.SentCvarAllowlist)
    /// decides which it actually honours; a cvar unset locally reads "" and is skipped (see PushReplicatedSet), so
    /// listing one the server doesn't yet consume is harmless. The set mirrors Base in field-for-field order:
    /// <list type="bullet">
    ///   <item><c>cl_autoswitch</c> — auto-switch to a better weapon on pickup;</item>
    ///   <item><c>cl_autoscreenshot</c> — server-forced end-of-match screenshot opt-in (sv-intermission);</item>
    ///   <item><c>cl_clippedspectating</c> — keep the spectator camera inside walls;</item>
    ///   <item><c>cl_autoswitch_cts</c> — CTS weapon auto-switch;</item>
    ///   <item><c>cl_handicap</c> + <c>cl_handicap_damage_given</c>/<c>_taken</c> — self-imposed damage handicap;</item>
    ///   <item><c>cl_noantilag</c> — lag-compensation opt-out;</item>
    ///   <item><c>g_xonoticversion</c> — the client's build version (server compat check).</item>
    /// </list>
    /// Two port additions follow Base's list: <c>cl_weaponpriority</c> (weapon selection order, replicated + live
    /// in the port) and <c>cl_physics</c> (the T54 per-player physics-preset select cvar — not a Base REPLICATE
    /// entry). The per-choice <c>notification_CHOICE_*</c> cvars are pushed separately (see ReplicatedChoiceCvars).
    /// </summary>
    private static readonly string[] ReplicatedCvars =
    {
        "cl_autoswitch",
        "cl_autoscreenshot",
        "cl_clippedspectating",
        "cl_autoswitch_cts",
        "cl_handicap",
        "cl_handicap_damage_given",
        "cl_handicap_damage_taken",
        "cl_noantilag",
        "g_xonoticversion",
        // --- port-specific (not in Base REPLICATE) ---
        "cl_weaponpriority",
        "cl_physics",
    };

    /// <summary>QC <c>notification_CHOICE_*</c> replicated cvars (notifications/all.qh:884 ReplicateVars): one per
    /// registered MSG_CHOICE notification (team variants collapse to a single shared cvar). Built LAZILY from the
    /// notification registry (Notifications.RegisterAll runs at GameInit, before the first replicate pump) so the
    /// watcher pushes exactly the choice cvars the server's gate accepts. Without this the client's verbose/terse
    /// kill-message preference never reaches the server.</summary>
    private string[]? _replicatedChoiceCvars;
    private string[] ReplicatedChoiceCvars
    {
        get
        {
            if (_replicatedChoiceCvars is null)
            {
                var s = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in Notifications.All)
                    if (n.Type == MsgType.Choice)
                        s.Add("notification_" + NotificationChoiceState.ChoiceCvarName(n));
                var arr = new string[s.Count];
                s.CopyTo(arr);
                _replicatedChoiceCvars = arr;
            }
            return _replicatedChoiceCvars;
        }
    }

    /// <summary>
    /// Where the watcher reads the replicated <c>cl_*</c> values — the host wires this to the SHARED menu/console
    /// cvar store (where cl_* live; on a listen host <c>Api.Cvars</c> is the server world's PRIVATE store, which
    /// doesn't carry them). Falls back to <c>Api.Cvars</c> when unset (headless/loopback rigs).
    /// </summary>
    public Func<string, string>? ReplicatedCvarSource { get; set; }

    private readonly Dictionary<string, string> _replicatedLastSent = new(StringComparer.Ordinal);
    private bool _replicateSendAll;
    private float _nextReplicateTime;
    private readonly Random _replicateJitter = new();

    private string ReadReplicatedCvar(string name)
        => ReplicatedCvarSource is not null
            ? (ReplicatedCvarSource(name) ?? "")
            : (Api.Services is not null ? Api.Cvars.GetString(name) : "");

    /// <summary>
    /// The CSQC ReplicateVars poll (replicate.qh:43-57): on session start send every replicated cvar once
    /// (REPLICATEVARS_SEND_ALL), then every 0.8 + random()*0.4 s compare against the last-sent value and push
    /// only the changed ones (value != last → "cl_cmd sendcvar" → "cmd sentcvar <name> <value>"; here we send
    /// the sentcvar line directly). An UNSET cvar reads "" and is skipped — the server then keeps its own
    /// defaults (QC always has cfg-seeded values; pushing "" would zero them server-side).
    /// </summary>
    private void PumpReplicatedCvars(float now)
    {
        if (!Accepted)
            return;
        if (!_replicateSendAll && now < _nextReplicateTime)
            return;
        bool sendAll = _replicateSendAll;
        _replicateSendAll = false;
        _nextReplicateTime = now + 0.8f + (float)_replicateJitter.NextDouble() * 0.4f; // replicate.qh:48

        PushReplicatedSet(ReplicatedCvars, sendAll);
        // QC ReplicateVars also pushes the per-choice notification_CHOICE_* cvars (the client's verbose/terse
        // kill-message preference) so the server can pick option A/B per recipient.
        PushReplicatedSet(ReplicatedChoiceCvars, sendAll);
    }

    /// <summary>Push the changed (or, on send-all, every set) cvar in <paramref name="names"/> via sentcvar.</summary>
    private void PushReplicatedSet(string[] names, bool sendAll)
    {
        foreach (string name in names)
        {
            string value = ReadReplicatedCvar(name);
            if (value.Length == 0)
                continue; // unset locally → don't clobber the server's default
            if (!sendAll && _replicatedLastSent.TryGetValue(name, out string? last) && last == value)
                continue;
            _replicatedLastSent[name] = value;
            SendStringCommand($"sentcvar {name} \"{value}\"");
        }
    }

    /// <summary>The server's display name, learned at handshake (for the client UI / server browser).</summary>
    public string ServerName { get; private set; } = "";

    /// <summary>The map the server is currently running, learned at handshake. A pure <c>--connect</c> client
    /// loads this BSP to render the world + build its prediction collision (NetGame.LoadClientMapFromServer);
    /// "" leaves the client on its flat prediction floor.</summary>
    public string ServerMapName { get; private set; } = "";

    /// <summary>The server's gametype short name, learned at handshake — the client drops the SAME gametype-
    /// conditional map submodels the server did, so its rendered world + prediction collision match authority.</summary>
    public string ServerGametype { get; private set; } = "";

    private void HandleReject(ref BitReader r)
    {
        string reason = r.ReadString();
        GD.PrintErr($"[ClientNet] handshake REJECTED: {reason}");
        Accepted = false;
    }

    private void HandleSnapshot(ref BitReader r)
    {
        // [r16 #5] cn.snapshot: the snapshot decode+apply is the heavy slice of ng.poll (4-5ms spikes at
        // 150+ entities were landing in ng.process "(other)"); scoped so the census names it.
        using var _snapScope = XonoticGodot.Common.Diagnostics.Prof.Sample("cn.snapshot");
        float serverTime = r.ReadFloat();
        uint ackedSeq = r.ReadULong();

        int ownerNetId = r.ReadUShort();
        // owner authoritative state (full precision) — the reconcile seed.
        NVec3 origin = r.ReadVector(NetPrecision.Float);
        NVec3 velocity = r.ReadVector(NetPrecision.Float);
        // DP cl_movement 0 rides these directly (see UseClientMovement above).
        _ownerOrigin = origin;
        _ownerVelocity = velocity;
        _ownerTime = serverTime;
        bool onGround = r.ReadBool();
        Health = r.ReadShort();
        Armor = r.ReadShort();
        ActiveWeaponId = r.ReadShort(); // owner block — same order as ServerNet.WriteOwnerState (viewmodel selector)
        RespawnTimeStat = r.ReadFloat(); // QC STAT(RESPAWN_TIME): dead respawn countdown / "press fire" prompt
        int newSpectatee = r.ReadShort(); // QC spectatee_status: 0 playing, own id observing, other id spectating
        if (newSpectatee != SpectateeStatus)
            // QC client/main.qc: spectatee_status_changed_time is stamped whenever spectatee_status changes (the
            // 1s shownames grace window after switching who you follow).
            SpectateeStatusChangedTime = Api.Services is not null ? Api.Clock.Time : SpectateeStatusChangedTime;
        SpectateeStatus = newSpectatee;
        // QC STAT(PRESSED_KEYS) (server GetPressedKeys / SpectateCopy): the view-player's held-key bitset — the
        // OWN keys while playing, the SPECTATEE's keys while following (the server copies them). Written by
        // ServerNet.WriteOwnerState right after spectatee; MUST be read here to keep the owner block aligned for
        // PunchAngle and everything after. Feeds the HUD pressedkeys/strafe cluster on a remote client.
        OwnerPressedKeys = r.ReadShort();
        PunchAngle = r.ReadVector(NetPrecision.Float); // QC view punch (recoil kick) — added to the view angles
        PunchVector = r.ReadVector(NetPrecision.Float); // QC view punch ORIGIN kick — added to the rendered origin
        LocalSpeedMultiplier = r.ReadFloat(); // QC STAT(MOVEVARS_HIGHSPEED) — mirrored onto the prediction carrier

        // [T57 accuracy] — the owner's own per-weapon accuracy bytes (QC ENT_CLIENT_ACCURACY, owner-only), read
        // in lockstep with ServerNet.WriteOwnerState's append: the change generation, then a bool gate, and —
        // only when set — a length byte + that many bytes (one QC accuracy_byte per weapon registry id). ALWAYS
        // consume whatever is present to keep the stream aligned. NetGame polls LocalAccuracyGeneration and
        // decodes the bytes into the scoreboard + weapons-panel accuracy grids.
        int accGen = r.ReadLong();
        if (r.ReadBool())
        {
            int n = r.ReadByte();
            ReadOnlySpan<byte> accBytes = r.ReadBytes(n);
            if (!r.BadRead)
            {
                LocalAccuracyBytes = accBytes.ToArray();
                LocalAccuracyGeneration = accGen;
            }
        }
        else if (!r.BadRead)
        {
            LocalAccuracyGeneration = accGen; // no change this frame; keep the generation current
        }

        // [W-wepent-charges] the owner-only weapon-HUD ring scalars (QC the networked wepent vortex_charge /
        // vortex_chargepool_ammo / clip_load / clip_size / hagar_load (+max) / minelayer_mines (+limit) /
        // arc_heat_percent), read in lockstep with ServerNet.WriteOwnerState's OwnerWeaponRings tail. Feeds the
        // crosshair charge/clip/load/heat rings on a PURE remote / dedicated-server client (the listen host reads
        // the same values straight off LocalServerPlayer). ALWAYS consumes the 9 floats so the owner block stays
        // aligned for the movevars/scores/entity sections that follow — the server writes them unconditionally.
        LocalWeaponRings = XonoticGodot.Net.OwnerWeaponRings.Read(ref r);

        // Owner inventory (appended after the ring floats — the struct's Write/Read pair owns the layout): ammo
        // pools + owned-weapon bitset + unlimited-ammo + the STAT_ITEMS flag bits. Feeds the full HUD's
        // ammo/weapons/powerups panels on a pure remote client via NetGame's HUD mirror player. ALWAYS consumes
        // the fixed layout so the owner block stays aligned for the movevars/scores/entity sections that follow.
        LocalInventory = XonoticGodot.Net.OwnerInventory.Read(ref r);

        // movevars: when the server's physics changed, stamp the replicated values into our cvar store so the
        // predictor's MovementParameters.FromCvars() matches authority (mid-match physics/mutator changes), then
        // refresh the PlayerState the reconciler carries from those same live cvars — so prediction reads the
        // REPLICATED movevars (the server's sv_maxspeed/accel/gravity/…) rather than the hardcoded Defaults the
        // session seeded with. (PlayerPhysics.Move reads FromCvars() directly each tick, so the cvar Apply is the
        // load-bearing half; refreshing _vars keeps the carried stat set honest for any consumer of it too.)
        if (r.ReadBool())
        {
            float[] mv = MoveVarsBlock.Deserialize(ref r);
            if (!r.BadRead && Api.Services is not null)
            {
                MoveVarsBlock.Apply(Api.Cvars, mv);
                _vars = VarsFromCvars();
            }
        }

        // [v7] per-peer preset-RESOLVED physics (T54): when the server replicates OUR preset-resolved movevar
        // vector (g_physics_clientselect — `cmd physics cpma` etc.), park it as the predictor's
        // MovementParameters.PredictionOverride so the prediction replays integrate with the SAME per-player
        // physics the server simulates us with. A count-0 block clears the override (preset deactivated).
        // NEVER stamped into the cvar store: on a listen host the shared→server cvar bridge (NetGame) would
        // leak the local player's preset into the authoritative store and change everyone's physics.
        // ALWAYS consume the bytes (stream alignment), even with no engine wired.
        if (r.ReadBool())
        {
            float[] resolved = MoveVarsBlock.Deserialize(ref r);
            if (!r.BadRead)
            {
                if (resolved.Length == 0)
                {
                    MovementParameters.PredictionOverride = null;
                    if (Api.Services is not null)
                        _vars = VarsFromCvars(); // back to the replicated global movevars
                }
                else
                {
                    MovementParameters mp = MovementParameters.FromValues(resolved);
                    MovementParameters.PredictionOverride = mp;
                    _vars = ToPlayerVars(mp); // keep the carried stat subset honest for its consumers
                }
            }
        }

        // ScoreInfo (QC ENT_CLIENT_SCORES_INFO): when the per-mode label/flag layout changed, parse + APPLY it
        // BEFORE the scoreboard block below. This is THE fix for the dropped-scoreboard bug: a remote --connect
        // client only ran GameScores.RegisterAll (default labels), so its networked-column set differed from the
        // server's mode-specific set and ScoreboardBlock.Deserialize dropped the whole block on a layout-hash
        // mismatch. Applying the received labels (GameScores.SetLabel per field) makes the client's NetworkedFields
        // — and thus the layout hash — match the server, so the scoreboard read below now succeeds. Apply is
        // idempotent: on a listen server the co-located server already set these on the SHARED GameScores, so this
        // re-writes the same values (a no-op) and never desyncs the in-process path.
        if (r.ReadBool())
        {
            // ALWAYS deserialize to consume the bytes (keeps the stream aligned for the scoreboard + entity
            // sections below) even when there's no engine to apply into — mirrors the movevars block above.
            XonoticGodot.Net.ScoreInfoBlock.Decoded? si = XonoticGodot.Net.ScoreInfoBlock.Deserialize(ref r);
            if (si is not null && Api.Services is not null)
            {
                XonoticGodot.Net.ScoreInfoBlock.Apply(si);
                LatestScoreInfo = si;
            }
        }

        // scoreboard: when a score changed, parse the networked per-player columns + team totals (the HUD reads
        // LatestScoreboard). Sent after the ScoreInfo block (so the layout already matches) and before the entity
        // section, so it must be read here to stay frame-aligned.
        if (r.ReadBool())
        {
            XonoticGodot.Net.ScoreboardWire? sb = XonoticGodot.Net.ScoreboardBlock.Deserialize(ref r);
            if (sb is not null)
                LatestScoreboard = sb;
        }

        // Gametype status (T53): read in lockstep with ServerNet's write order (owner → movevars → scoreinfo →
        // scoreboard → THIS → entity section). ALWAYS consume the bytes to keep the stream aligned, even with
        // no consumer wired (the ScoreInfo comment above documents the alignment contract).
        if (r.ReadBool())
        {
            XonoticGodot.Net.GametypeStatusBlock.Decoded? ms = XonoticGodot.Net.GametypeStatusBlock.Deserialize(ref r);
            if (ms is not null && !r.BadRead)
                LatestModeStatus = ms;
        }

        // [T57 accuracy] — the owner's accuracy array is a PER-OWNER payload; it is read at the END of the owner
        // block above (next to SpectateeStatus), NOT here in the broadcast region.

        if (r.BadRead)
            return;

        LatestServerTime = serverTime;

        // Fix B — POST-HITCH STALL HOLD (defense-in-depth for the spawn-rubberband; Fix A removes the pre-match-freeze
        // case, this covers any other shared-thread stall: mid-match GC, a heavier map). After a frame hitch the
        // listen server is transiently BEHIND — it hasn't simulated our queued input yet — so this snapshot's
        // authoritative origin sits well behind our prediction. Snapping to it teleports the camera backward even
        // though our prediction is the correct future the server is about to reproduce. So when a hitch armed the
        // hold and the pending correction is MODERATE (a stall lag: between the teleport-snap threshold and a genuine
        // teleport/respawn jump), HOLD: keep the old authoritative baseline and keep predicting forward, WITHOUT
        // snapping. Bounded to MaxHitchSkips snapshots so a genuine desync still corrects; it releases the moment the
        // server catches up (pending error drops back under the snap threshold) or the player teleports for real.
        float pendingErr = (_reconciler.Predicted.Origin - origin).Length();
        if (RecentHitch) _hitchHoldActive = true;
        bool holdPlayer = _hitchHoldActive && _hitchSkips < MaxHitchSkips
            && pendingErr > Reconciler.MaxErrorOrigin && pendingErr < Reconciler.MaxForceOrigin;
        if (holdPlayer)
        {
            _hitchSkips++; // keep _serverState/_serverAckedSeq from before the stall → keep predicting forward
        }
        else
        {
            _hitchSkips = 0;
            _hitchHoldActive = false;
            // Stash the previous prediction at the (newly) acked frame for the error measurement, then update the
            // authoritative state the reconciler replays from.
            _previousPredictionAtAck = _reconciler.Predicted;
            _serverState = new PredictedState { Origin = origin, Velocity = velocity, OnGround = onGround };
            _serverAckedSeq = ackedSeq;

            // Reconcile immediately so prediction error is measured + smoothing armed the moment the ack lands.
            _reconciler.Reconcile(_serverState, _serverAckedSeq, _vars, serverTime, _previousPredictionAtAck);
        }

        // Spawn/respawn settle-snap: a freshly spawned player teleports to its spawn point and FALLS to the floor.
        // The client predicts that whole fall in one replay (it lands instantly) while the server descends one tick
        // per frame — so the reconcile measures a big positive prediction LEAD every fall snapshot and folds it
        // into the view smoother, floating the camera ~150u up for seconds. Treat the spawn+settle as a teleport
        // (QC csqcmodel_teleported: a respawn snaps the view, never smooths): keep the smoother cleared until the
        // server reports the player has landed (on-ground). After that the player is stable (oErr≈0) so normal
        // smoothing resumes cleanly. The mid-game teleporter/respawn-in-place cases are still caught by the
        // teleport-sized origin reset in Reconciler.SetPredictionError.
        if (Health > 0 && !_wasAliveLastSnap)
            _settlingAfterSpawn = true;
        _wasAliveLastSnap = Health > 0;
        if (_settlingAfterSpawn)
        {
            _reconciler.ResetError();
            if (_serverState.OnGround)
                _settlingAfterSpawn = false; // landed: prediction has converged, resume smoothing
        }

        // PRE-MATCH FREEZE: keep the view smoother CLEARED for the whole pre-match countdown (server holds the
        // player at spawn, canMove=false, while serverTime < MatchStartTime and we're not in freely-playable
        // warmup). The predictor is also frozen (EntityMovementStep.Frozen) so the error should be ~0, but any
        // residual from inputs buffered before the first MatchState arrived (the MatchStartTime-race) would
        // otherwise be smoothed + re-armed each snapshot and take a freeze-length to bleed off — the vibrate the
        // user saw lingering ~5s into live play. Clearing it every frozen snapshot makes go-live instant + clean.
        if (!MatchWarmup && MatchStartTime > 0f && serverTime < MatchStartTime)
            _reconciler.ResetError();

        // delta-decompress the entity section against the baseline the server named.
        IReadOnlyDictionary<int, NetEntityState>? entities = _snapHistory.DecodeSnapshot(ref r);
        if (entities is null)
            return; // bad read or missing baseline — wait for a full snapshot

        foreach (KeyValuePair<int, NetEntityState> kv in entities)
        {
            int netId = kv.Key;
            if (netId == LocalNetId)
            {
                // Never interpolate our own entity (we predict it) — but keep its decoded slice so the own name tag
                // (QC Draw_ShowNames self branch: chase_active + hud_shownames_self) can read kind/health/team/flags.
                LocalState = kv.Value;
                continue;
            }

            NetEntityState s = kv.Value;
            RemoteEntity re = GetOrCreateRemote(netId);
            re.State = s;
            bool teleported = (s.Flags & NetEntityFlags.Teleported) != 0;
            var snap = new Snapshot { Time = serverTime, Origin = s.Origin, Angles = s.Angles };
            re.Interp.Note(snap, teleported, re.LastServerTime);
            re.LastServerTime = serverTime;
        }

        // drop remotes the server removed this frame (no longer in the reconstructed set).
        _staleScratch.Clear();
        foreach (int id in _remotes.Keys)
            if (id != LocalNetId && !entities.ContainsKey(id))
                _staleScratch.Add(id);
        for (int i = 0; i < _staleScratch.Count; i++)
            _remotes.Remove(_staleScratch[i]);

        // The own entity left the set (we became an observer — the server's entity set skips observers): drop
        // the captured slice so the "watched == self" consumers see NO state rather than the last alive frame
        // (e.g. a fire viewmodel frame latched at the moment of death).
        if (LocalNetId != 0 && !entities.ContainsKey(LocalNetId))
            LocalState = null;
    }

    private RemoteEntity GetOrCreateRemote(int netId)
    {
        if (!_remotes.TryGetValue(netId, out RemoteEntity? re))
        {
            re = new RemoteEntity();
            _remotes[netId] = re;
        }
        return re;
    }

    /// <summary>The net ids of all known remote entities (the renderer iterates these to place nodes).</summary>
    public IReadOnlyCollection<int> RemoteIds => _remotes.Keys;

    /// <summary>
    /// The interpolated render pose of remote entity <paramref name="netId"/> at client time
    /// <paramref name="now"/> (origin + seam-safe blended angles). Returns false if the entity is unknown.
    /// Interpolate slightly behind the latest server time for smoothness; here <paramref name="now"/> is
    /// typically <see cref="LatestServerTime"/> (a one-snapshot delay falls out of the two-snapshot lerp).
    /// </summary>
    public bool SampleRemote(int netId, float now, out NVec3 origin, out NVec3 angles)
    {
        if (NoLerp)
        {
            // DP cl_nolerp 1: no two-snapshot interpolation — callers fall back to the raw newest state.
            origin = default;
            angles = default;
            return false;
        }
        if (_remotes.TryGetValue(netId, out RemoteEntity? re) && re.Interp.HasData)
        {
            Snapshot s = re.Interp.Sample(now);
            origin = s.Origin;
            angles = s.Angles;
            return true;
        }
        origin = default;
        angles = default;
        return false;
    }

    /// <summary>The latest decoded properties (kind/model/frame/health/team/flags) of a remote entity, for the
    /// renderer to pick the right model + nameplate. Returns false if the entity is unknown.</summary>
    public bool TryGetRemoteState(int netId, out NetEntityState state)
    {
        if (_remotes.TryGetValue(netId, out RemoteEntity? re)) { state = re.State; return true; }
        state = default;
        return false;
    }

    /// <summary>
    /// The decoded entity slice of the player this client is LOOKING THROUGH — works for the OWN entity too.
    /// The own entity never enters the remote table (<see cref="HandleSnapshot"/> diverts it to
    /// <see cref="LocalState"/> so it's never interpolated), so a <see cref="TryGetRemoteState"/> on
    /// <see cref="LocalNetId"/> always MISSES — the silent breaker of every pure-client "watched == self"
    /// consumer (viewmodel anim frame, viewmodel colors, vortex glow). Use THIS for view-through reads.
    /// (v14: the server actually SENDS the own entity — before that the divert branch was dead and LocalState
    /// stayed null for the whole session, which is why the #49/#51/#58 fixes didn't land on the wire.)
    /// </summary>
    public bool TryGetViewedState(int netId, out NetEntityState state)
    {
        if (netId != 0 && netId == LocalNetId && LocalState is { } ls) { state = ls; return true; }
        return TryGetRemoteState(netId, out state);
    }

    /// <summary>Drop a remote entity (the renderer calls this when the server stops sending it / it's removed).</summary>
    public void ForgetRemote(int netId) => _remotes.Remove(netId);

    // ---- event bundles ----

    private void HandleEventBundle(ref BitReader r)
    {
        int count = r.ReadUShort();
        for (int i = 0; i < count; i++)
        {
            int effectId = r.ReadUShort();
            int bodyLen = r.ReadUShort();
            ReadOnlySpan<byte> body = r.ReadBytes(bodyLen);
            if (r.BadRead)
                return;
            if (effectId == EffectNetProtocol.ByNameSentinelId)
                DecodeEffectByName(body);
            else
                DecodeEffect(effectId, body);
        }
    }

    /// <summary>
    /// Decode an engine-fallback by-name effect record (the QC <c>Send_Effect_</c> →
    /// <c>__pointparticles(_particleeffectnum(name))</c> path): the body is a length-prefixed effectinfo NAME
    /// followed by the usual EFF_NET payload for a POINT effect. We resolve nothing through the registry; the name
    /// is carried verbatim so the renderer can look it up in the effectinfo.txt catalog (DP's _particleeffectnum
    /// equivalent), exactly as the listen-server in-process mirror already does.
    /// </summary>
    private void DecodeEffectByName(ReadOnlySpan<byte> body)
    {
        var br = new BitReader(body);
        string name = br.ReadString();

        NVec3 origin = new(br.ReadFloat(), br.ReadFloat(), br.ReadFloat());
        int flags = br.ReadByte();

        NVec3 velocity = default, colorMin = default, colorMax = default;
        if ((flags & EffectNetProtocol.NetVelocity) != 0)
            velocity = new NVec3(br.ReadFloat(), br.ReadFloat(), br.ReadFloat());
        if ((flags & EffectNetProtocol.NetColorMin) != 0)
            colorMin = ReadColor(ref br);
        if ((flags & EffectNetProtocol.NetColorSame) != 0)
            colorMax = colorMin;
        else if ((flags & EffectNetProtocol.NetColorMax) != 0)
            colorMax = ReadColor(ref br);

        int count = br.ReadByte(); // __pointparticles is always a point effect → count present
        if (br.BadRead || string.IsNullOrEmpty(name))
            return;

        EffectReceived?.Invoke(new EffectEvent(null, name, origin, velocity, count, colorMin, colorMax));
    }

    /// <summary>
    /// Decode one EFF_NET_* effect body (the inverse of <see cref="EffectNetProtocol.Encode"/>) and raise
    /// <see cref="EffectReceived"/>. The body is: origin vector (3 floats), an extraflags byte, then — gated
    /// by the flags — velocity (3 floats), color-min triple, color-max triple, and (point effects only) a
    /// count byte. Trail effects carry no count.
    /// </summary>
    private void DecodeEffect(int effectId, ReadOnlySpan<byte> body)
    {
        Effect? effect = (effectId >= 0 && effectId < Effects.Count) ? Effects.ById(effectId) : null;
        var br = new BitReader(body);

        NVec3 origin = new(br.ReadFloat(), br.ReadFloat(), br.ReadFloat());
        int flags = br.ReadByte();

        NVec3 velocity = default, colorMin = default, colorMax = default;
        if ((flags & EffectNetProtocol.NetVelocity) != 0)
            velocity = new NVec3(br.ReadFloat(), br.ReadFloat(), br.ReadFloat());
        if ((flags & EffectNetProtocol.NetColorMin) != 0)
            colorMin = ReadColor(ref br);
        // EFF_NET_COLOR_SAME means min == max; only color-max is sent when distinct.
        if ((flags & EffectNetProtocol.NetColorSame) != 0)
            colorMax = colorMin;
        else if ((flags & EffectNetProtocol.NetColorMax) != 0)
            colorMax = ReadColor(ref br);

        bool trail = effect is { IsTrail: true };
        int count = trail ? 0 : br.ReadByte();
        if (br.BadRead)
            return;

        EffectReceived?.Invoke(new EffectEvent(effect, origin, velocity, count, colorMin, colorMax));
    }

    // EFF_NET color components are quantized as rint(bound(0,16*c,255)); decode back to the [0,16) float range.
    private static NVec3 ReadColor(ref BitReader r)
    {
        float x = r.ReadByte() / 16f;
        float y = r.ReadByte() / 16f;
        float z = r.ReadByte() / 16f;
        return new NVec3(x, y, z);
    }

    /// <summary>
    /// Decode a <see cref="NetControl.SoundBundle"/> via the shared <see cref="XonoticGodot.Net.SoundWire"/> codec
    /// (the inverse of <c>ServerNet.WriteSound</c>) and raise <see cref="SoundReceived"/> per record — sample,
    /// origin, volume, attenuation, channel, the source entity net id, and the loop/stop flags.
    /// <see cref="BitReader.BadRead"/> guards a truncated/oversized record — bail like <see cref="DecodeEffect"/>.
    /// </summary>
    private void HandleSoundBundle(ref BitReader r)
    {
        int count = r.ReadUShort();
        for (int i = 0; i < count; i++)
        {
            XonoticGodot.Net.SoundWire rec = XonoticGodot.Net.SoundWire.Read(ref r);
            if (r.BadRead)
                return;
            SoundReceived?.Invoke(new SoundEvent(rec.Sample, rec.Origin, rec.Volume, rec.Attenuation,
                rec.Channel, rec.SourceNetId, rec.Loop, rec.Stop, rec.Pitch));
        }
    }

    private void HandleReliableBundle(ref BitReader r)
    {
        int count = r.ReadUShort();
        for (int i = 0; i < count; i++)
        {
            int notifId = r.ReadUShort();
            var type = (MsgType)r.ReadByte();
            string text = r.ReadString();
            int strN = r.ReadByte();
            var strs = strN > 0 ? new string[strN] : Array.Empty<string>();
            for (int s = 0; s < strN; s++) strs[s] = r.ReadString();
            int fltN = r.ReadByte();
            var flts = fltN > 0 ? new float[fltN] : Array.Empty<float>();
            for (int f = 0; f < fltN; f++) flts[f] = r.ReadFloat();
            if (r.BadRead)
                return;

            Notification? notif = (notifId >= 0 && notifId < Notifications.Count) ? Notifications.ById(notifId) : null;
            NotificationReceived?.Invoke(new NotificationEvent(notif, type, text, strs, flts));
        }
    }

    // =====================================================================================
    //  Helpers
    // =====================================================================================

    private static PlayerState DefaultVars()
    {
        // The movement-var seed the predictor carries before the first movevar block arrives. Built from the
        // shared deterministic defaults (NOT a lone hardcoded constant) so it already agrees with the server's
        // stock physics; once a MoveVarsBlock lands, HandleSnapshot refreshes this from the REPLICATED cvars via
        // VarsFromCvars() so a mid-match physics/mutator change is reflected in prediction.
        return ToPlayerVars(MovementParameters.Defaults);
    }

    /// <summary>
    /// Rebuild the predictor's <see cref="PlayerState"/> movement-var set from the live cvar store after the
    /// server's replicated <see cref="MoveVarsBlock"/> was applied — i.e. read the SERVER's
    /// sv_maxspeed/accelerate/airaccelerate/friction/stopspeed/jumpvelocity/gravity/stepheight rather than the
    /// hardcoded defaults. <c>MovementParameters.FromCvars()</c> consults <c>Api.Cvars</c>, which the
    /// just-run <c>MoveVarsBlock.Apply</c> stamped, so this picks up the authoritative physics.
    /// </summary>
    private static PlayerState VarsFromCvars() => ToPlayerVars(MovementParameters.FromCvars());

    /// <summary>Project the movement-var subset of a <see cref="MovementParameters"/> onto the owner-replicated
    /// <see cref="PlayerState"/> the reconciler carries (the MOVEVARS_* the predictor depends on). Local mapping so
    /// this stays in the net layer without touching the shared physics/stat types.</summary>
    private static PlayerState ToPlayerVars(in MovementParameters mp) => new()
    {
        MaxSpeed = mp.MaxSpeed,
        Accelerate = mp.Accelerate,
        AirAccelerate = mp.AirAccelerate,
        Friction = mp.Friction,
        StopSpeed = mp.StopSpeed,
        JumpVelocity = mp.JumpVelocity,
        Gravity = mp.Gravity,
        StepHeight = mp.StepHeight,
    };

    public void Dispose()
    {
        // Drop the per-player physics override this session may have installed — it's session state, and a
        // later session (or a local GameDemo) must not predict with a stale preset.
        MovementParameters.PredictionOverride = null;
        _transport.Dispose();
    }
}

/// <summary>
/// The end-of-match map/gametype-vote ballot as decoded on a remote client (<see cref="ClientNet.HandleMapVote"/>)
/// — the client-side stand-in for the listen host's in-process <c>MapVoting</c>. NetGame.UpdateMapVotePanel renders
/// the vote panel from this when there is no server world (a pure --connect client). All indices are the same
/// abstain-stripped cell indices the server computed, so the panel highlights line up 1:1 with the candidate cells.
/// </summary>
public sealed class NetMapVote
{
    /// <summary>Whether the ballot should be shown (QC the server's showing gate: vote running, or finished with a winner).</summary>
    public bool Showing;
    /// <summary>The vote has ended (a winner is latched).</summary>
    public bool Finished;
    /// <summary>This is a gametype vote (cells are gametypes, not maps) rather than a map vote.</summary>
    public bool IsGametypeVote;
    /// <summary>QC mv_detail (sv_vote_gametype_detail): show the extended per-gametype description panel.</summary>
    public bool Detail;
    /// <summary>QC mv_abstain: the appended "Don't care" abstain slot is present on the ballot.</summary>
    public bool AbstainPresent;
    /// <summary>Seconds remaining on the vote countdown (the panel's timeout clock).</summary>
    public float Remaining;
    /// <summary>1-based winning cell (abstain-stripped), 0 = none decided yet.</summary>
    public int Winner1Based;
    /// <summary>This client's own vote as an abstain-stripped cell index, or -1 for none / abstain.</summary>
    public int Own;
    /// <summary>The ballot cells (abstain excluded — see <see cref="AbstainPresent"/>).</summary>
    public readonly List<Cand> Candidates = new();

    /// <summary>One ballot cell as it rides the wire.</summary>
    public struct Cand
    {
        public string MapName;
        public int Votes;
        public bool Available;
        public string Suggester;
    }
}

using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Common;            // GameInit
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;   // Player (LocalServerPlayer)
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Console;
using XonoticGodot.Game.Loaders;
using XonoticGodot.Game.Client;
using XonoticGodot.Game.Console;
using XonoticGodot.Game.Hud;
using XonoticGodot.Game.Menu;
using XonoticGodot.Net;
using XonoticGodot.Server;
using EngineServices = XonoticGodot.Engine.Simulation.EngineServices;
using GVec3 = Godot.Vector3;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The networked-match node — the thing the menu (or a CLI flag) spawns to actually <b>join a server and
/// play</b>. It composes the real §2 netcode pieces the way <see cref="NetLoopback"/> does, but driven by
/// genuine player input + a first-person camera that follows the PREDICTED local player, so it is a playable
/// client rather than a headless wire exerciser:
///
/// <list type="bullet">
///   <item>a real <see cref="ClientNet"/> (build-parity + ECDSA handshake, delta snapshots, predict/reconcile
///         the local player, interpolate remotes) connected over ENet;</item>
///   <item>a <see cref="EntityMovementStep"/> driving the SAME deterministic <see cref="Movement"/> sim the
///         server runs over a carrier entity, so prediction stays in lockstep with authority;</item>
///   <item>a <see cref="ClientWorld"/> render layer + <see cref="ClientEntityView"/> net→render bridge so
///         remote players/projectiles/items render from the over-the-wire entity stream;</item>
///   <item>a first-person <see cref="Camera3D"/> placed each frame at the predicted eye, oriented with the
///         exact basis math <see cref="PlayerController.UpdateCamera"/> uses (copied here — we never touch
///         that class);</item>
///   <item>a minimal HUD (<see cref="NetHud"/> crosshair + health/armor) and the <see cref="RadarPanel"/>
///         ent_cs radar.</item>
/// </list>
///
/// <para><b>Two modes.</b> A <see cref="ConfigureClient"/> instance is a pure client: it connects to a remote
/// <c>host:port</c> and boots a minimal engine facade locally (a flat floor) so prediction has something to
/// trace against — replicating the server's real map collision on a remote client is a later layer (see the
/// note on <see cref="BootClientFacade"/>). A <see cref="ConfigureListenServer"/> instance is a host/listen
/// server: it boots a <see cref="GameWorld"/> + <see cref="ServerNet"/> in-process (optionally with bots),
/// then self-connects a local <see cref="ClientNet"/> to <c>127.0.0.1</c> — the listen-server case where the
/// client and server share the one ambient <see cref="Api.Services"/> collision world, so prediction is exact.</para>
///
/// <para>This is the bring-up the <c>OnConnect</c>/<c>StartMap</c> stubs deferred: the menu's server browser
/// (or <c>--connect &lt;addr&gt;</c>) builds a client NetGame; a host (Create Game / <c>--host [map]</c>, and
/// <c>--map</c> for a quick 0-bot local match) builds a listen-server NetGame. The no-net GameDemo demo is gone —
/// this is the single play path now; the <c>--net-loopback</c> and menu flows are unchanged.</para>
/// </summary>
public sealed partial class NetGame : Node3D
{
    /// <summary>The default XonoticGodot/Xonotic game port (DP <c>port</c> 26000).</summary>
    public const int DefaultPort = 26000;

    // --- configuration (set via the factory helpers before the node enters the tree) ---
    private bool _isListenServer;
    private string _host = "127.0.0.1";
    private int _port = DefaultPort;
    private string _map = "";
    private string _gametype = "dm";
    private int _botCount;
    private int _botSkill = -1;                 // -1 = unspecified: never write the `skill` cvar (MatchConfig.BotSkill)
    private string _campaignName = "";          // non-empty → host this listen server as a campaign level
    private int _campaignIndex;
    private string _serverName = "XonoticGodot Listen Server";
    private string _playerName = "player";
    private VirtualFileSystem? _vfs;            // shared asset VFS (from the menu shell), for models/sounds/maps
    private XonoticGodot.Engine.Simulation.CvarService? _sharedCvars;
    private System.Action<string>? _sharedCvarBridge;   // mirrors console/menu cvar writes into the server's store

    // --- live pieces ---
    private GameWorld? _serverWorld;            // listen server only
    private ServerNet? _server;                 // listen server only
    // S5 (sv_threaded, default OFF): the dedicated server-sim worker thread + the shared serialisation gate it
    // and the main-thread prediction span lock against. BOTH null unless sv_threaded was 1 at StartListenServer on
    // a non-headless host. When null, _Process drives _server.Tick directly on the main thread exactly as today —
    // no thread, no lock — so the default path is byte-for-byte unchanged.
    private ServerThread? _serverThread;        // listen server only, sv_threaded 1 only
    private object? _simGate;                    // the shared lock; non-null iff _serverThread is non-null
    private ClientNet? _client;
    private ClientWorld _render = null!;
    private DevHarness? _devHarness;            // dev capture (--fx-demo), inert unless a dev flag was passed
    // The background parse/build queue (S1): the idle player-model warm AND the live on-demand player-model
    // path (perf §9.4 Wave 1 — first sight of N bots streams one model build per frame instead of all at once).
    private XonoticGodot.Game.Client.BackgroundAssetStreamer? _streamer;
    // Player-model names whose async resolve settled as NOT skeletal (non-IQM / no skeleton): the resolver
    // returns null for these so ClientWorld's MD3/static fall-through owns them — exactly the old synchronous
    // path's outcome. Per session; model files can't change mid-run.
    private readonly System.Collections.Generic.HashSet<string> _nonSkeletalPlayerModels =
        new(System.StringComparer.OrdinalIgnoreCase);
    // FORCED player-model names+skins whose async resolve settled as NOT a poseable skeleton (non-IQM forced model
    // → render via the cached LoadModel node, FORCEMODEL parity) OR a true VFS miss (LoadModel also null → fall
    // through to the entity's own model). Keyed "model#skin" because a forced model is name+skin-specific. Checking
    // this BEFORE re-streaming is what makes the RebuildEntityModel-driven re-attach terminate instead of looping.
    private readonly System.Collections.Generic.HashSet<string> _unresolvableForcedModels =
        new(System.StringComparer.OrdinalIgnoreCase);
    private ClientEntityView _entityView = null!;
    private Camera3D _camera = null!;

    // (E3/E5) Map editor: the interaction controller, its line-overlay renderer and the orthographic view.
    // Created with the render tree and inert until the editor gametype puts this client into free-fly.
    private Vmap.EditorController? _editor;
    private Vmap.EditorGizmos? _editorGizmos;
    private Vmap.EditorOrthoView? _editorOrtho;
    private bool _orthoPanning;

    // The editor renders the world from the EDITABLE document, not the compiled BSP. Without this the textured
    // geometry never reflects an edit: the overlay outline moves and the wall stays put.
    private Node3D? _editorMapRoot;
    private int _editorMapVersion = -1;

    /// <summary>The editor's live light rig (design doc §10.1 rung 1); null when lighting is off.</summary>
    private Vmap.EditorLighting? _editorLights;

    /// <summary>Wall clock at bake start, for the timing line (ms since engine start).</summary>
    private ulong _bakeClock;

    /// <summary>
    /// The document imported at load in the editor gametype — the single truth the session edits, the render
    /// regenerates from, and collision is built from. Null outside the editor gametype.
    /// </summary>
    private XonoticGodot.Formats.Vmap.VmapDocument? _preloadedEditorDoc;

    /// <summary>GeometryVersion the live collision was built from; -1 forces the first playtest rebuild check.</summary>
    private int _editorCollisionVersion = -1;

    private float _bakeUniformScale = float.NaN, _bakeUniformAmbient = float.NaN, _bakeUniformGamma = float.NaN;
    private float _bakeUniformRange = float.NaN, _bakeUniformDeluxe = float.NaN;


    private static float CvarOr(XonoticGodot.Engine.Simulation.CvarService cvars, string name, float fallback)
    {
        string v = cvars.GetString(name);
        return string.IsNullOrEmpty(v) ? fallback : cvars.GetFloat(name);
    }
    private RadarPanel _radar = null!;
    private NetHud _hud = null!;                 // crosshair + health/armor readout (the always-on lightweight HUD)
    // The full CSQC HUD panel set (weapon bar / ammo / kill-feed / centerprint / timer) on the net play path —
    // the same Hud GameDemo uses (T34 SEAM). It hosts the networked panels fed from the net stream; its own
    // Scoreboard panel is left UNUSED (T9's standalone _scoreboard owns the networked scoreboard). On a listen
    // server its gameplay panels (health/ammo/weapons) read the local server Player; a pure client gets the
    // player-agnostic panels (centerprint/killfeed) + the NetHud crosshair/health.
    private XonoticGodot.Game.Hud.Hud _fullHud = null!;
    private XonoticGodot.Game.Client.DamageTextLayer? _damageText; // [T51] floating damage numbers (cl_damagetext)
    private XonoticGodot.Game.Client.WaypointSpriteLayer? _waypointLayer; // 3D in-world waypoint/objective markers
    private Node3D? _mapRoot;                                              // the built map scene (holds the "Portals" child)
    private Godot.Environment? _worldEnv;                                  // the WorldEnvironment's env — kept so a pure client can swap in the real map sky/fog/tint once its BSP loads
    private bool _clientMapLoaded;                                         // a pure --connect client has loaded the server's map (render + prediction collision) — one-shot
    private XonoticGodot.Game.Client.PortalRenderer? _portalRenderer;      // see-through warpzone portal render (listen host)
    private XonoticGodot.Game.Client.PortalDiscRenderer? _portalDiscRenderer; // warpzone portal DISC (skinned model) — listen host, reads the same AmbientManager zones
    private XonoticGodot.Game.Client.NadeOrbRenderer? _orbRenderer;        // 3D nade orb effect models (heal/ammo/entrap/veil/darkness) — fed by the entity stream, read for the in-orb color flash
    private XonoticGodot.Game.Client.ShowNamesLayer? _shownamesLayer; // [T68] floating player name + health/armor tags
    private XonoticGodot.Game.Client.HitSound? _hitSound;          // client-side hit-confirmation beep (cl_hitsound modes 0-3)
    private XonoticGodot.Game.Hud.ScoreboardPanel _scoreboard = null!; // the networked scoreboard (held while +showscores)
    private XonoticGodot.Game.Hud.HudNotifications? _notifications; // notification router (centerprint/killfeed/announcer) on the net path
    private MinigameClient? _minigame;          // client-side minigame coordinator (board overlay + menu + cmd forwarding)
    private ViewEffects _viewEffects = null!;   // SEAM: T4's reusable screen-effects layer, on the net play path
    private AssetLoader? _assets;
    private AssetLoader? _injectedAssets;        // shared process-lifetime loader from MenuState — persists model/sound/material/texture/shader caches across maps & servers; null (or cl_persist_asset_cache 0) → build a fresh per-match loader
    private ViewModel _viewModel = null!;       // first-person weapon view-model (CSQC viewmodel / wepent)
    private int _equippedWeaponId = int.MinValue; // weapon id currently in the viewmodel; rebuild only on a change
    private string _equippedVmOverride = "";       // per-weapon wr_viewmodel override (Tuba note model); rebuild on change
    private bool _viewmodelReloading;              // last-frame reload state (clip_load==-1) — play the reload anim on the rising edge
    private bool _weaponsPrecached;             // PrecacheWeaponModels ran once (warm the per-weapon asset caches)
    private bool _readyComplete;                // _Ready finished — _Process can run its full body (before this, fields are half-built)
    private bool _captureMarked;                // one-shot guard: CaptureGate.MarkReady() fired at first spawn (--screenshot)
    private HandshakeStage _handshakeStage;     // last sub-stage announced to the LoadingScreen (so we only BeginStage on a transition)

    /// <summary>Sub-stages of the post-_Ready handshake/spawn phase, used by <see cref="_Process"/> to drive
    /// the loading bar to BeginStage on each transition (not every frame).</summary>
    private enum HandshakeStage { None, Connecting, WaitingForServer, Joining, Spawned }
    private MusicPlayer? _musicPlayer;          // client-side map music (cdtrack / target_music / trigger_music)

    // The SHARED first-person view subsystem (zoom + FOV + chase/death cam + eye-contents) — the SAME component
    // PlayerController uses, ported once from qcsrc/client/view.qc (T34 SEAM). Fed a ClientNet-predicted
    // ViewState each frame; we read back EyeContents / ZoomFraction / SensitivityScale / ChaseActive.
    private readonly Client.FirstPersonView _view = new();

    // The FAITHFUL Base view smoothing (CSQCPlayer_ApplySmoothing: stairsmoothz glide + viewheightavg eye-height
    // blend), used by UpdateCamera when cl_movement_smoothing_faithful is on (the default) so the rendered eye
    // matches stock Xonotic exactly. The port's adaptive stair offset + error-comp glide is the alternative
    // (faithful 0). Reset on respawn/teleport via the same path that clears the reconciler smoothing.
    private readonly XonoticGodot.Net.FaithfulViewSmoothing _faithfulSmoothing = new();

    // The zoom scope reticle overlay (QC crosshair.qc DrawReticle), fed each frame in _Process from the networked
    // active weapon (ClientNet.ActiveWeaponId) + the zoom/button state — the net-path twin of PlayerController's.
    private Client.ReticleOverlay _reticle = null!;

    // The listen server's parsed map + its gametype-filtered dropped submodels, kept so SetupRender can build the
    // world render mesh from the SAME BSP + filter the collision was built from (the client renders the worldmodel
    // locally — DP VF_DRAWWORLD; the server ships no geometry). Null on a pure --connect client (no BSP yet).
    private XonoticGodot.Formats.Bsp.BspData? _bsp;
    private System.Collections.Generic.IReadOnlySet<int>? _droppedSubmodels;

    // The carrier entity the prediction sim drives (the client's local player hull). Spawned into the ambient
    // engine world (the listen server's GameWorld, or the minimal client facade) so PlayerPhysics has a real
    // edict with a hull to slide-and-step. Mirrors PlayerController's spawned entity.
    private Entity? _carrier;
    private EntityMovementStep? _movementStep; // the client predictor adapter (kept so the pre-match freeze mirrors onto it)
    private IEngineServices? _world; // the facade the carrier lives in (captured so teardown removes it correctly)

    // Accumulated view angles in DEGREES, Quake convention (X = pitch down-positive, Y = yaw, Z = roll) — the
    // same convention PlayerController keeps, sampled into each InputCommand and used to orient the camera.
    private NVec3 _viewAngles;
    private bool _attackHeld;                     // per-frame previous +attack state (local fire-feedback edge)
    private bool _attack2Held;                    // per-frame previous +attack2 state
    // Sub-tick fire latch: set on the per-frame press edge (UpdateLocalFireFeedback), OR'd into the next sampled
    // InputCommand and cleared, so a tap shorter than one 1/72 s input tick still reaches the server.
    private bool _attackLatch;
    private bool _attack2Latch;

    // Local fire prediction (cl_predictfire, default on): a client-side refire clock that fires the view-model
    // muzzle flash + a local fire SOUND on every shot the moment the player fires (not just the first), so
    // sustained fire feels instant; the local player's own networked fire-sound/muzzle-flash is then suppressed
    // so it isn't doubled. _fireClock is a monotonic frame accumulator. _weaponReadyTime is a PERSISTENT
    // per-weapon "next allowed shot" clock — a client mirror of the server's per-slot ATTACK_FINISHED
    // (WeaponFireGate.st.AttackFinished). It survives button release, so tapping / wheel-firing can't out-run the
    // weapon's refire rate (the spam this closes). Keyed by weapon id (Base INDEPENDENT_ATTACK_FINISHED) and
    // SHARED by both fire modes (Base indexes ATTACK_FINISHED by [weapon][slot], not by primary/secondary).
    private bool _predictFire = true;
    private float _fireClock;
    private readonly Dictionary<int, float> _weaponReadyTime = new();
    private bool _loggedAccept;
    private bool _hostJoinDone;                  // [#44] the listen host's early_join_requested `join` actually spawned it
    private float _hostJoinNextRetryMsec;        // [#44] next allowed `join` attempt (bounded retry — a blocked spawn no-ops)
    private bool _cameraReady;                   // C5: false until the first snapshot seeds the predicted eye
    private int _prevHealth = -1;               // previous networked local health, for the damage red-flash edge
    private bool _inputActive;                   // tracks the active→inactive edge to release held buttons (pause/console)
    private bool _nadeDarknessActive;            // tracks the 0→positive darkness-remaining edge so SND_BLIND plays once per blind onset

    // C2S impulse (QC usercmd.impulse): a one-shot weapon-switch/reload number a weapon bind set this frame.
    // Edge-triggered — SampleInput stamps it onto the next InputCommand then clears it, so it is sent (and
    // processed server-side) exactly once. 0 = nothing pending.
    private int _pendingImpulse;

    // Spawn-zoom edge (QC cl_spawnzoom): the net path has no Teleport, so derive the (re)spawn from a Health
    // 0→>0 transition and arm the shared view's spawn-zoom on that edge. -1 until the first health snapshot.
    private int _prevAliveHealth = -1;

    // True once the local player has been alive at least once. Gates the death-cam so the PRE-spawn Health 0
    // (observer window, before the first spawn) isn't read as a death (which would engage the event-chase at the
    // world origin under the connect overlay). After the first spawn, Health<=0 is a real death.
    private bool _everAlive;

    /// <summary>Runs a console command line for one-shot binds (weapon_next/reload/kill/…). Wired by
    /// <see cref="Shell"/> to the shared interpreter's <c>ExecuteLine</c> so a bound key routes through the same
    /// command path as the console.</summary>
    public Action<string>? RunCommand { get; set; }

    /// <summary>Raised with a line of server console output (a <c>clc_stringcmd</c> reply on a remote client) —
    /// <see cref="Shell"/> forwards it to the in-game console.</summary>
    public event Action<string>? ConsolePrint;

    /// <summary>The in-process listen-server world (host mode), or null on a pure <c>--connect</c> client. The
    /// console routes gameplay commands here directly when present (else over the remote string-command channel).</summary>
    public GameWorld? ServerWorld => _serverWorld;

    /// <summary>The local human <see cref="Player"/> on the listen-server world (the <c>caller</c> for console
    /// client-commands), resolved from the client's assigned net id with a single-human fallback. Null on a pure client.</summary>
    public Player? LocalServerPlayer
    {
        get
        {
            if (_serverWorld is null)
                return null;
            if (_server is not null && _client is not null)
            {
                Player? byId = _server.PlayerByNetId(_client.LocalNetId);
                if (byId is not null)
                    return byId;
            }
            // WS1 stage 3: enumerate the COW roster snapshot — this property is read from the render thread
            // (HUD mirrors, feeds) while the sim worker may be mutating the live List on a join/leave.
            foreach (Player p in _serverWorld.Clients.PlayersSnapshot)
                if (!p.IsBot)
                    return p;
            return null;
        }
    }

    /// <summary>Send a console command line to the connected server (DP <c>clc_stringcmd</c>) — used by the
    /// console on a pure client where there is no in-process world. No-op if not connected.</summary>
    public void SendStringCommand(string line) => _client?.SendStringCommand(line);

    /// <summary>
    /// Execute a host-console gameplay command against the in-process server world, returning its output —
    /// or null when there is no local world (pure client). WS1: this runs on the MAIN thread (console input)
    /// while the sim worker owns the world when sv_threaded is on, so it takes the sim gate for the duration —
    /// the same serialisation the pre-stage-3 whole-frame gate provided for console commands, restored for
    /// just this path (RunOnSimThread can't be used here: the console needs the command's synchronous output).
    /// Every world-mutating verb the console can type (kill/kick/give/endmatch/vote/…) must come through here,
    /// not through a bare Commands.Execute — that was the mid-combat crash class d991956 closed for transport.
    /// </summary>
    public string? ExecuteHostConsoleCommand(string line)
    {
        GameWorld? world = _serverWorld;
        if (world is null)
            return null;
        object? gate = _server?.SimGate;
        if (gate is null)
            return world.Commands.Execute(line, isServerConsole: true, caller: LocalServerPlayer).Output;
        lock (gate)
            return world.Commands.Execute(line, isServerConsole: true, caller: LocalServerPlayer).Output;
    }

    /// <summary>The loading screen (DP <c>SCR_DrawLoadingScreen</c>) shown during map load + handshake,
    /// set by <see cref="Shell"/> before the node enters the tree. Null when no loading screen is active
    /// (e.g. a bare CLI host).</summary>
    public LoadingScreen? LoadingScreen { get; set; }

    /// <summary>Callback to dismiss the loading screen layer (owned by <see cref="Shell"/>). Called when
    /// the player spawns (camera ready + health &gt; 0).</summary>
    public Action? DismissLoadingScreen { get; set; }

    /// <summary>True when this node is hosting a listen server (vs a pure remote <c>--connect</c> client).</summary>
    public bool IsListenServer => _isListenServer;

    /// <summary>True when a listen server has at least one REMOTE client connected (a peer beyond the local
    /// host). Drives the local-only auto-pause gate in <see cref="Shell"/>: a solo local game may freeze on
    /// menu/console/focus-loss, but a server with remote players must keep running. False for a remote client
    /// (no server) and for a listen host that is alone (with or without bots — bots aren't peers).</summary>
    public bool HasRemoteClients => _isListenServer && (_server?.ConnectedPeerCount ?? 0) > 1;

    /// <summary>
    /// Raised when this listen server must change level (DP <c>changelevel</c>) — carries (map, gametype,
    /// botCount, botSkill, campaignId, campaignIndex) captured from the live match so the host can reboot the
    /// listen server on the new map preserving the mode + bot fill, and — for a campaign auto-advance on win —
    /// staying in campaign mode at the next level (campaignId empty for a normal map change). <see cref="Shell"/>
    /// subscribes and reboots. Emitted deferred (idle) so the teardown happens OUTSIDE the server tick.
    /// </summary>
    public event Action<string, string, int, int, string, int>? MapChangeRequested;

    private string? _pendingMap; // a requested changelevel, emitted at the end of _Process (then deferred)

    /// <summary>
    /// Request a changelevel to <paramref name="map"/> (DP <c>map</c>/<c>changelevel</c> + the end-of-match
    /// rotation/gotomap). Wired as <c>Commands.ChangeLevelHandler</c> and called by the console's <c>map</c>
    /// builtin; only records the target — it's emitted (deferred) from <see cref="_Process"/> so a request made
    /// mid-tick (a clc_stringcmd <c>map</c>, or the intermission rotation) can't tear the server down under itself.
    /// </summary>
    public void RequestMapChange(string map) => RequestMapChange(map, null);

    /// <param name="gametype">
    /// Gametype for the rebooted server, or null to keep the current one. This is what lets
    /// <c>editor</c> drop into the map you are already playing: the changelevel machinery always carried a
    /// gametype through to the restart, but nothing could ever ASK for a different one.
    /// </param>
    public void RequestMapChange(string map, string? gametype)
    {
        if (string.IsNullOrWhiteSpace(map))
            return;
        _pendingMap = map;
        _pendingGametype = string.IsNullOrWhiteSpace(gametype) ? null : gametype;
    }

    /// <summary>Gametype override for the pending changelevel; null keeps the current one.</summary>
    private string? _pendingGametype;

    /// <summary>The map currently being served, for commands that re-host it.</summary>
    public string CurrentMap => _map;

    // Render clock used to drive the prediction (Predict/SendInput) AND read the decaying stair/error offsets.
    // The reconciler arms those decays stamped with the SERVER time (in HandleSnapshot), so the clock we read
    // them with must track server time — but advance smoothly BETWEEN snapshots (a snapshot only lands every
    // few frames). So we advance it by real delta ONCE per frame (the `_renderClock += dt` in _Process) and
    // rebase to LatestServerTime on each new snapshot. The input cadence (per-frame / legacy drain) reads this
    // clock but MUST NOT advance it again — doing so ran it at ~2x wall time between rebases, which (once the
    // error-decay window was corrected to one tick) made the prediction-error offset read as already-expired
    // every frame, silently disabling the smoothing. Single advance point keeps the decay clock truthful.
    private float _renderClock;
    private float _lastSeenServerTime = -1f;

    // Fixed-timestep input accumulator: real time banked toward whole 1/72 s input ticks (so prediction speed
    // is frame-rate independent). Capped so a frame hitch can't queue a huge input burst (spiral-of-death).
    private float _inputAccum;
    private const float MaxInputBacklog = 0.25f; // ≤ ~18 catch-up input ticks per frame
    // GRACEFUL HITCH RECOVERY: cap how many fixed input ticks one render frame may drain. A frame hitch (bot
    // pathfinding spike / GC / OS preempt) leaves a backlog in _inputAccum; draining it ALL in the recovery frame
    // sends a burst of commands the server applies in one go → a one-frame movement lunge + a reconcile snap. With
    // command-driven movement the server advances the player by exactly the commands we send, so capping the drain
    // makes a hitch's backlog spread over the next few frames IN LOCKSTEP with the server (no lunge, no snap). Set
    // to the server's interactive soft cap (SimulationLoop.MaxTicksPerFrame = 4 above): high enough not to clip
    // sustained low-fps play (handles down to ~18 fps), low enough to smear a real hitch over several frames.
    // (Used only by the LEGACY fixed-tick path / perframe 0; Path A's bounded single step subsumes it.)
    private const int MaxClientCatchupTicksPerFrame = 4;

    // PATH A (Base-faithful variable-dt local prediction) bounds, mirroring Darkplaces:
    //   MaxInputFrameDt — hard spiral-guard cap on one render frame's simulated time (DP: bound(cl_timer, .., 0.1));
    //                     a multi-100ms hitch simulates at most this, the rest is dropped (the graceful-recovery
    //                     equivalent, now inherent to the bounded step instead of a tick-count cap).
    //   MaxSubStepDt    — split a long frame into <= this so one huge physics step can't tunnel collisions
    //                     (DP CL_ClientMovement_PlayerMove_Frame splits moves > 0.05 s).
    private const float MaxInputFrameDt = 0.1f;
    private const float MaxSubStepDt = 0.05f;

    // PATH A render-clock RATE SLEW (r16 rubberband fix — supersedes the per-snapshot ClockSyncFactor STEP):
    // the render clock converges on server time by modulating its playback RATE within a bounded,
    // imperceptible band instead of stepping a fraction of the error on each snapshot arrival. Every step
    // visibly sped/slowed ALL interpolated entities for a frame; under combat load the listen server's tick
    // catch-up makes LatestServerTime elastic vs wall time, so the steps oscillated in ~300-600ms waves
    // (session-20260710-182544: frame-time lag-1 autocorr +0.61) — the felt "movement rubberbanding".
    //   ClockSlewGain — rate deviation per second of clock error (err 33ms → the 5% cap);
    //   ClockSlewMax  — playback-rate deviation cap (±5%: never perceptibly fast/slow);
    //   MaxClockResync — discontinuity threshold beyond which we hard-snap (respawn / level change / a
    //                    huge hitch). DP's cl_nettimesyncboundmode family, done as a rate, not a step.
    private const float ClockSlewGain = 1.5f;
    private const float ClockSlewMax = 0.05f;
    private const float MaxClockResync = 0.25f;
    // Slew-target offset behind the newest snapshot (pairs with the hard upper bound at LatestServerTime in
    // _Process). Frametime parity audit 2026-07-11: Base (cl_nettimesyncboundmode 5, cl_parse.c:3354) targets
    // cl.mtime[1] — the PREVIOUS snapshot's time, a full MEASURED interval behind newest, so the cushion grows
    // adaptively exactly when snapshot production runs late (the loaded listen server) and interpolation never
    // rides the f=1 clamp edge. cl_interp_cushion 1 (default) = that DP parity via _snapInterval; 0 = the r16
    // half-tick constant bias below (legacy, ~7ms from the edge).
    private const float InterpBias = 0.5f / 72f;
    // Measured spacing between the last two snapshots (DP cl.mtime[0] - cl.mtime[1]), clamped to [1 tick, 0.1s];
    // the cl_interp_cushion slew/step target offset. Seeded at one tick until two snapshots have arrived.
    private float _snapInterval = 1f / 72f;

    /// <summary>How far behind the newest snapshot the render clock aims (see <see cref="InterpBias"/>):
    /// the measured snapshot interval (DP boundmode-5 parity, default) or the legacy half-tick bias.</summary>
    private float InterpCushion() => _interpCushionCv ? _snapInterval : InterpBias;

    // Fix B: a render frame longer than this (a stall on the shared listen-server thread — GC / heavy map streaming,
    // ~3.6+ ticks) arms the reconciler's post-hitch hold so a transient server-behind correction glides/holds rather
    // than snapping the camera back to spawn.
    private const float HitchFrameSeconds = 0.05f;

    // Fix C: seconds to defer the background idle-warmer past the spawn / pre-match-countdown / streaming-settle
    // window (5s countdown + a couple seconds margin) so its stock-model builds don't hitch the opening seconds.
    private const float IdleWarmupDelaySeconds = 7f;

    // Per-frame (variable-dt) input mode — cl_movement_perframe (DP-style: one command per rendered frame stamped
    // with the real frame dt, the server drains all pending commands that tick). DEFAULT 1 (on, user-approved per
    // planning/PERFORMANCE_REPORT.md B2); `set cl_movement_perframe 0` = the legacy fixed 1/72 s cadence above (kept for
    // parity testing). Read live from the shared cvar store each frame so it A/B-toggles in-session.
    private bool _perFrameInput;
    // The DeltaTime stamped onto the next sampled InputCommand. Always the fixed TicRate now — BOTH input modes
    // integrate movement in fixed 1/72 s ticks (the bunnyhop-consistency fix); per-frame mode differs only in
    // sending a command per render frame (snappy aim/fire + faster backlog catch-up via the server batch path),
    // not in the movement dt.
    private float _inputDeltaTime = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate;

    // FPS eye height (Xonotic PL_VIEW_OFS '0 0 35'). Mouse-look reads the live `sensitivity` × m_yaw/m_pitch
    // cvars (DP cl_input.c formula), so the input-settings dialog drives it and tuned m_yaw/m_pitch work.
    private const float EyeHeight = 35f;

    // Standing player hull (Xonotic sv_player_mins/maxs), Quake units — same as PlayerController.HullMins/Maxs.
    private static readonly NVec3 HullMins = new(-16f, -16f, -24f);
    private static readonly NVec3 HullMaxs = new(16f, 16f, 45f);

    // Observer hull — QC PL_CROUCH_MIN/MAX, the size PutObserverInServer gives a free-flying spectator. Must
    // match the server's ApplyObserverHull or prediction and authority integrate different collisions.
    private static readonly NVec3 ObserverHullMins = new(-16f, -16f, -24f);
    private static readonly NVec3 ObserverHullMaxs = new(16f, 16f, 25f);

    // =====================================================================================
    //  Factory configuration (call before AddChild)
    // =====================================================================================

    /// <summary>
    /// Configure this node as a PURE CLIENT that connects to <paramref name="address"/> (<c>host[:port]</c>,
    /// default port <see cref="DefaultPort"/>). The optional <paramref name="vfs"/>/<paramref name="cvars"/>
    /// are the menu shell's shared asset VFS + cvar store (so models/sounds/crosshairs resolve and the
    /// predictor reads the user's physics cvars); pass null for a standalone/CLI client.
    /// </summary>
    public void ConfigureClient(string address, string playerName = "player",
        VirtualFileSystem? vfs = null, XonoticGodot.Engine.Simulation.CvarService? cvars = null,
        AssetLoader? sharedAssets = null)
    {
        _isListenServer = false;
        (_host, _port) = ParseAddress(address, DefaultPort);
        _playerName = string.IsNullOrWhiteSpace(playerName) ? "player" : playerName;
        _vfs = vfs;
        _sharedCvars = cvars;
        _injectedAssets = sharedAssets;
    }

    /// <summary>
    /// Configure this node as a LISTEN SERVER (host): boot a <see cref="GameWorld"/> on <paramref name="map"/>
    /// + a <see cref="ServerNet"/> on <paramref name="port"/> (optionally filled with <paramref name="botCount"/>
    /// bots), then self-connect a local client to it. The shared <paramref name="vfs"/>/<paramref name="cvars"/>
    /// drive both the server's config + the client's rendering; pass null for a bare CLI host on a test floor.
    /// </summary>
    public void ConfigureListenServer(string map, string gametype = "dm", int botCount = 0, int botSkill = -1,
        int port = DefaultPort, string playerName = "player", string serverName = "XonoticGodot Listen Server",
        VirtualFileSystem? vfs = null, XonoticGodot.Engine.Simulation.CvarService? cvars = null,
        string campaignName = "", int campaignIndex = 0, AssetLoader? sharedAssets = null)
    {
        _isListenServer = true;
        _host = "127.0.0.1";
        _port = port;
        _map = map ?? "";
        _gametype = string.IsNullOrWhiteSpace(gametype) ? "dm" : gametype;
        _botCount = Math.Max(0, botCount);
        _botSkill = botSkill;
        _campaignName = campaignName ?? "";
        _campaignIndex = campaignIndex;
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "XonoticGodot Listen Server" : serverName;
        _playerName = string.IsNullOrWhiteSpace(playerName) ? "player" : playerName;
        _vfs = vfs;
        _sharedCvars = cvars;
        _injectedAssets = sharedAssets;
    }

    // =====================================================================================
    //  Lifecycle
    // =====================================================================================

    public override async void _Ready()
    {
        // The asset loader (models/sounds/maps) over the shared VFS, when the menu mounted one.
        if (_vfs is not null)
        {
            // Reuse MenuState's PROCESS-LIFETIME loader when one was injected (cl_persist_asset_cache 1, default):
            // its model/sound/material/texture/shader caches then persist across map changes AND server switches,
            // so the second map load finds the stock assets already parsed + GPU-uploaded instead of rebuilding
            // them. Map-coupled resources (the lightmap atlas + its surface materials) are NOT in these caches —
            // they live in the map render tree and are freed with the match — so sharing is safe. A fresh loader
            // is built only when persistence is off or nothing was injected (bare CLI / tests / model viewer).
            bool persist = (_sharedCvars?.GetFloat("cl_persist_asset_cache") ?? 1f) != 0f;
            _assets = (persist && _injectedAssets is not null) ? _injectedAssets : new AssetLoader(_vfs);
            // Wire the per-model player-sound resolver (QC LoadPlayerSounds): parses .sounds manifests so the
            // jump grunt + pain/death voices resolve to real samples instead of the bogus default.sounds/<id>.
            PlayerSoundResolver.Install(_vfs);
        }

        // The load sequence runs as a coroutine: each BeginStage sets the bar's target and per-stage expected
        // time (the LoadingScreen animates asymptotically from where it is now toward that target), then we
        // yield one frame so the bar can repaint with the new status text BEFORE the synchronous work begins.
        // After the work, we yield again so the bar can catch up to its now-elapsed-time-based position before
        // the next stage takes over. Net effect: the bar advances visibly through every sub-step and slows down
        // gracefully when a stage runs longer than expected, instead of one "Loading…" → snap-to-1 jump.
        // (Async void so a TeardownGame mid-load just leaves this coroutine to fall off the tree-check below.)

        if (_isListenServer)
        {
            LoadingScreen?.BeginStage("Loading map…", 0.30f, 4.0f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;

            StartListenServer();

            LoadingScreen?.BeginStage("Booting world…", 0.42f, 0.5f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;
        }
        else
        {
            LoadingScreen?.BeginStage("Preparing client…", 0.30f, 0.5f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;

            BootClientFacade();

            LoadingScreen?.BeginStage("Resolving server…", 0.42f, 0.5f);
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;
        }

        // The client connects to the chosen endpoint (a listen server is on 127.0.0.1; a pure client on _host).
        StartClient();

        LoadingScreen?.BeginStage("Setting up renderer…", 0.55f, 1.0f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;

        // Render layer + the net→render bridge + a basic HUD/camera. Built regardless of mode so a connect that
        // hasn't completed yet still has somewhere to draw once snapshots flow.
        SetupRender();
        SetupCameraAndHud();

        // Dev capture: `--fx-demo [effect]` rides on the live ClientWorld to burst a named effect in front of the
        // player each frame for an effect-parity --screenshot (the survivor of GameDemo's inline dev flags; see
        // DevHarness). Inert unless the flag is present, so it's cheap to wire on every boot.
        {
            string[] cmd = OS.GetCmdlineArgs();
            int fi = System.Array.IndexOf(cmd, "--fx-demo");
            if (fi >= 0)
            {
                string fx = (fi + 1 < cmd.Length && !cmd[fi + 1].StartsWith("--")) ? cmd[fi + 1] : "rocket_explode";
                _devHarness = new DevHarness { Name = "DevHarness", Render = _render, FxDemoEffect = fx };
                AddChild(_devHarness);
                GD.Print($"[NetGame] --fx-demo: bursting '{fx}' in front of the player for capture.");
            }
        }

        LoadingScreen?.BeginStage("Starting music…", 0.62f, 0.3f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;

        // Map music: read the cdtrack from the mapinfo file (VFS) and wire a MusicPlayer into the render tree.
        SetupMusic();

        LoadingScreen?.BeginStage("Precaching weapon models…", 0.82f, 3.0f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;

        // Warm every weapon's view model + hand rig into the asset caches now, under the loading screen, so the
        // first switch/pickup of a weapon in combat doesn't hitch reading+decoding the model and its textures on
        // the main thread (DP precaches weapon models at map load). Runs after _assets is built above.
        // The async variant yields every few weapons so the bar visibly ticks through this stage.
        await PrecacheWeaponModelsAsync();
        if (!IsInsideTree()) return;

        // Warm the combat-sound decode cache + common player models (A3) so the first shot/explosion and the
        // first player render don't stall on an OGG decode / skeletal-model texture build mid-combat.
        LoadingScreen?.BeginStage("Precaching sounds…", 0.87f, 1.5f);
        await YieldForLoadingFrame();
        if (!IsInsideTree()) return;
        await PrecacheCombatSoundsAndModelsAsync();
        if (!IsInsideTree()) return;

        // S3: a low-priority idle warmer mops up the long tail (announcer/pickup voices, the OTHER stock player
        // models) so the whole asset set goes hot within the first minute. (Fix C) DEFER its start past the critical
        // opening seconds: its 5 stock-model builds + GPU-warms otherwise land during the first ~5-7s — exactly the
        // pre-match countdown + streaming-settle window the player spawns into — adding frame hitches right then (a
        // contributor to the spawn-rubberband: Fix A/B stop it TELEPORTing, this keeps it SMOOTH). By go-live the
        // local model + map are already hot and the player is moving; any model that appears sooner still resolves
        // on-demand at High priority. cl_idle_warmup 0 still disables it entirely (checked in StartIdleWarmup).
        GetTree().CreateTimer(IdleWarmupDelaySeconds).Timeout += () => { if (IsInsideTree()) StartIdleWarmup(); };

        LoadingScreen?.BeginStage("Connecting…", 0.90f, 0.5f);

        // Keep the cursor FREE during load: the per-frame reassert in _Process grabs it for mouse-look the frame
        // the local player spawns (gated on LoadingScreen going null), so there's nothing to capture here — and
        // capturing now would trap the pointer in a windowed game for the whole load (DP keeps it free on the
        // load screen so you can mouse out of the window).
        MouseCapture.SetWantCapture(false);

        GD.Print(_isListenServer
            ? $"[NetGame] listen server on 127.0.0.1:{_port} (map '{_map}', {_gametype}, {_botCount} bots) — self-connecting."
            : $"[NetGame] connecting to {_host}:{_port}.");

        // _Process can safely run its full body now (camera/HUD/render/client all built above).
        _readyComplete = true;
    }

    /// <summary>Yield one process_frame so the LoadingScreen's _Process can repaint with whatever stage
    /// is currently in flight. No-op (synchronously completes) if the node is no longer in the tree —
    /// guards against ToSignal throwing on a torn-down NetGame mid-load.</summary>
    private async System.Threading.Tasks.Task YieldForLoadingFrame()
    {
        if (!IsInsideTree()) return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>
    /// Boot the listen server: build the map (or a test floor), stand up a <see cref="GameWorld"/> on it,
    /// activate the gametype, fill it with bots, and start a <see cref="ServerNet"/>. The world's collision
    /// becomes the ambient <see cref="Api.Services"/> (via <see cref="GameWorld.Boot"/>), which the client's
    /// prediction then traces against — the listen-server property that makes local prediction exact.
    /// </summary>
    private void StartListenServer()
    {
        // --- collision: load the map's BSP collision if we can resolve it, else a flat test floor. ---
        CollisionWorld collision;
        XonoticGodot.Formats.Bsp.BspData? bsp = TryLoadMapBsp(_map);
        BspCollisionBuilder.Result? built = null;
        if (bsp is not null)
        {
            // Keep the BSP + the gametype-filtered dropped submodels so SetupRender can render the worldmodel
            // (the client draws the world it loaded locally) AND so collision drops the SAME conditional "*N"
            // brushes the render does — exactly like GameDemo (render + collision MUST agree, GameDemo.cs:134/181).
            _bsp = bsp;
            _droppedSubmodels = GameMapView.ComputeDroppedSubmodels(bsp, _gametype);
            if (IsEditorGametype)
            {
                // The editor plays the DOCUMENT, not the compiled map. Import once, here, and this instance
                // becomes the single truth for the whole session: the client edits it, the render regenerates
                // from it, and — the part that makes PLAYTEST honest — collision is built from it too, so the
                // level you run and jump through is the level you edited, not the .bsp it started from.
                _preloadedEditorDoc = XonoticGodot.Formats.Vmap.BspToVmap.Import(
                    bsp, _map, $"maps/{_map}.bsp", sourceHash: "", droppedSubmodels: _droppedSubmodels);
                built = Vmap.EditorWorldCollision.Build(_preloadedEditorDoc, _droppedSubmodels);
                collision = built.World;
                XonoticGodot.Common.Diagnostics.Log.Info(
                    $"editor: map is the imported document ({_preloadedEditorDoc.Brushes.Count} brushes, "
                    + $"{_preloadedEditorDoc.Patches.Count} patches; collision from the document)");
            }
            else
            {
                built = BspCollisionBuilder.Build(bsp, _droppedSubmodels);
                collision = built.World;
            }
        }
        else
        {
            collision = BuildTestFloor();
            if (!string.IsNullOrWhiteSpace(_map))
                GD.PrintErr($"[NetGame] map '{_map}' not found in the VFS — listen server runs on a flat floor.");
        }

        // --- the headless world + gametype. Pass the map's parsed entity lump so the server spawns the real
        //     spawn points + gameplay entities (jump-pads/items/doors) — without these a player would spawn at
        //     the world origin with no items. Reuse the menu's shared cvar store via the config reader so the
        //     server reads authentic balance/physics; a bare CLI host (no preloaded store) loads them too. ---
        System.Collections.Generic.IReadOnlyList<EntityDict>? mapEntities = bsp is not null ? BuildEntityDicts(bsp) : null;

        // Resolve the threading decision NOW — before the world is built — because it selects the CVAR-STORE MODEL,
        // and the worker/gate setup further below must agree with that choice (so it's decided once, here).
        //   • Default (sv_threaded 0): UNIFY — the world runs directly on the shared menu/console/client store
        //     (DP's one engine cvar table). A host's console `set g_balance_…`/physics tweak is then live in the
        //     match with no bridge, and client prediction (also on the shared store) and server authority read
        //     identical values. The menu already loaded the cfg tree into it, so GameWorld.Boot skips the reload.
        //   • sv_threaded 1 (the DEFAULT since 2026-07-11, non-headless only): keep a PRIVATE per-world store,
        //     isolated from the main-thread menu/console writes and fed one-way by the bridge below — the sim
        //     runs on a worker thread, so a shared mutable store would race. sv_threaded is read from the shared
        //     store (where the menu/console writes it; unset → ON); with no shared store (the degenerate bare
        //     host) we stay single-threaded + private.
        bool wantThreaded =
            _sharedCvars is not null
            && (!_sharedCvars.Has("sv_threaded") || _sharedCvars.GetFloat("sv_threaded") != 0f)
            && DisplayServer.GetName() != "headless";
        bool unifyStore = _sharedCvars is not null && !wantThreaded;

        _serverWorld = new GameWorld(collision, mapEntities,
            sharedCvars: unifyStore ? _sharedCvars : null) { MapName = _map };
        // B3: on the interactive client-hosted path (a windowed listen server), clamp the per-render-frame
        // catch-up so a hitch isn't followed by a frame running 16× the sim — that second spike would miss
        // another vblank. The small backlog drains over the next few frames instead. A headless dedicated host
        // (no vblank) keeps the full cap and catches up fast.
        // (hitch-fix 2026-06-14) Lowered 4 -> 3. A recovery frame after a hitch runs MaxTicksPerFrame catch-up
        // ticks, and every per-tick × per-player multiplier (WeaponThink, the bot strategy-token goal-rating)
        // fires that many times in one frame — the measured `mp.weapon ×28` / `bot.think` spikes on catch-up
        // frames. 3 trims that multiplier ~25% (the backlog just drains over one extra frame — brief, invisible
        // slow-motion); tick SEMANTICS are bit-identical (same ticks run, only their per-render-frame distribution
        // changes), so it is parity-safe. The primary cause of these backlogs (the bot-spawn model-build storm) is
        // fixed by the AnimationLibrary cache, so this is a secondary smoother. Headless keeps the full cap.
        if (DisplayServer.GetName() != "headless")
            _serverWorld.Simulation.MaxTicksPerFrame = 3;
        if (built is not null)
            _serverWorld.BrushModels = built.Submodels; // moving SOLID_BSP brushes (doors/plats) clip correctly
        if (bsp is not null)
        {
            _serverWorld.MapBsp = bsp;                   // inline-model render surfaces for the getsurface* builtins
            _serverWorld.Pvs = new XonoticGodot.Formats.Bsp.BspPvs(bsp); // checkpvs culling (bot LOS / sound / net)
        }
        if (_vfs is not null)
            _serverWorld.ConfigReader = path => _vfs.Exists(path) ? _vfs.ReadText(path) : null;

        // Campaign: the menu picked a campaign level. Hand the id + index to the world BEFORE Boot so its
        // CampaignPreInit (run inside Boot) resolves this level's gametype/bots/skill/limits/mutators from the
        // campaign file — QC CampaignSetup set the same cvars before the map change. (Must be a pre-Boot
        // property, not a cvar Set here: the world's cvar store is private and is created during Boot.)
        if (!string.IsNullOrEmpty(_campaignName))
        {
            _serverWorld.CampaignName = _campaignName;
            _serverWorld.CampaignIndex = _campaignIndex;
        }

        // Boot the world: publishes Api.Services, activates the gametype, loads the cfg tree, spawns the map's
        // entities (spawn points + jump-pads/items/doors), registers brush models + surfaces + PVS.
        _serverWorld.Boot(_gametype);

        // Expose the map name as a cvar so the server-browser infostring (ServerNet.BuildServerInfo) and the
        // pure-client map-name handshake (follow-up) have the real value; GameWorld.MapName is set, but the cvar
        // is independent (DP keeps mapname as an engine cvar available to serverinfo).
        _serverWorld.Services.Cvars.Set("mapname", _map);

        // S5: register sv_threaded (default 0) on the server store and resolve the host-side threading decision
        // ONCE, here at boot (changing it mid-match is out of scope — a rehost/map-change re-reads it). The actual
        // worker + gate are created further below, after ServerNet starts and the command sinks are wired. A
        // console/menu override in the SHARED store wins over the world default (the menu writes there). Threading
        // is GATED to non-headless hosts: a headless dedicated server already keeps the full catch-up cap and has
        // no render frame to unblock, so it stays on the stock single-threaded drive even with sv_threaded 1.
        // (§13.5 → WS1) DEFAULT ON since 2026-07-11 (user decision, after the WS1 rework): the worker owns
        // tick+ENCODE, the render thread never blocks on the gate on the common path (per-tick holds; the
        // sv.gatewait_ms counter measures the residue), and the full validation battery is clean (soaks,
        // mid-match remote joins, 2989 tests). `sv_threaded 0` restores the single-threaded drive (and the
        // UNIFIED cvar store — see the store-model comment above).
        _serverWorld.Services.Cvars.Register("sv_threaded", "1");
        // wantThreaded + unifyStore were resolved above (before the world was built) since they pick the
        // cvar-store model; the worker/gate setup below reuses that one decision so the two never disagree.

        // [#44] sv_spectate stays at Base's shipped `1` (spectating allowed). The old port set it to 0 here to
        // make the host auto-spawn — which as a side effect made spectating impossible for EVERYONE (and armed
        // the sv_spectate=0 spectator-kick). The host's "Create Game and play" UX is now the Base-faithful
        // `early_join_requested` equivalent instead: the [#44] block in the accept handler (search _hostJoinDone)
        // runs `join` for the LOCAL player once its observer attaches, retrying until it spawns — remote clients
        // get the real observer flow (+jump joins, +attack spectates, exactly QC ObserverOrSpectatorThink).
        // cl_bench_spectate (demo/benchmark captures) rides this cleanly: the host must stay an OBSERVER camera,
        // so BenchSpectateActive gates the [#44] host-join off and BenchSpectateThink glues the observer to a bot.
        //
        // [#47] Port-default divergence: no idle auto-spectate by default. This now lives in the shipped cfg tree
        // (xonotic-server.cfg sets sv_maxidle_playertospectator 0, was 60) instead of a post-load code Set — so an
        // operator's config.cfg/console value actually wins and survives map changes, rather than being clobbered
        // on every StartListenServer. The Base-faithful machinery in PlayerFrameIdleAll is intact either way.

        // A single-player campaign is "host AND play" too: g_campaign arms its own spectator-hold, so opt the
        // local host out (CampaignHostAutojoin) AND let the [#44] host-join block above spawn it — at the shipped
        // sv_spectate 1 the passive delayed-autojoin can't fire, so the campaign host rides the same `join` path
        // (CmdJoin succeeds on stock campaign maps).
        if (!string.IsNullOrEmpty(_campaignName))
        {
            _serverWorld.Clients.CampaignHostAutojoin = true;

            // Campaign progress lives in the SHARED (menu) cvar store as a seta cvar; bridge it across the
            // private/shared store split (QC keeps progress in one engine store the menu + server share):
            //  - SEED the world's frontier from the menu so PreIntermission only advances at the frontier level
            //    (and a replay of an earlier level can't regress it — Level == g_campaign<id>_index gate).
            //  - MIRROR each saved progress cvar back to the menu store (+ archive it) so the campaign list sees
            //    the new frontier immediately and SaveUserConfig persists it (QC CampaignSaveCvar's seta side).
            string indexVar = $"g_campaign{_campaignName}_index";
            if (_sharedCvars is not null)
            {
                _serverWorld.Services.Cvars.Set(indexVar,
                    ((int)_sharedCvars.GetFloat(indexVar)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _serverWorld.Campaign.OnProgressSaved = (name, value) =>
                {
                    _sharedCvars.Set(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _sharedCvars.MarkArchived(name);
                };
            }
        }

        // Carry the chosen match limits onto the hosted world's own cvar store. ONLY needed on the threaded
        // (private-store) path: there GameWorld keeps a separate store, so the values the menu wrote into
        // MenuState.Cvars don't reach it automatically. When unified the world IS running on the shared store the
        // menu populated (Shell already wrote timelimit/fraglimit there), so there's nothing to copy. In campaign
        // mode the limits are authored by the file (CampaignPostInit set them during Boot), so the menu's leftover
        // timelimit/fraglimit must NOT clobber them.
        if (_sharedCvars is not null && !unifyStore && string.IsNullOrEmpty(_campaignName))
        {
            CopyCvarIfSet(_sharedCvars, _serverWorld.Services.Cvars, "timelimit");
            CopyCvarIfSet(_sharedCvars, _serverWorld.Services.Cvars, "fraglimit");
        }

        // THREADED PATH ONLY: bridge runtime cvar changes from the shared (console/menu) store into the listen
        // server's PRIVATE store. Under sv_threaded the world keeps its own store (isolated from the main thread),
        // so without this a console `set g_balance_…` — or any server-cvar tweak — never reaches the live match.
        // The DEFAULT (unified) path doesn't need this at all: the world runs ON the shared store, so a console
        // write IS the server's value (and its own Changed hook re-derives weapon balance). Mirror only cvars the
        // server already knows (Has); one-way + idempotent so the campaign progress mirror above can't loop.
        if (_sharedCvars is not null && !unifyStore)
        {
            // INITIAL BACKFILL (private-store only): a new map boots a fresh GameWorld whose private store reloads
            // from the cfg tree, so a sv_*/g_* the user changed in the console (shared store) would revert to its
            // cfg default. Re-apply the user's CURRENT overrides now (the bridge only forwards FUTURE changes).
            // Skip in campaign mode (the level file authors the cvars). BackfillModified only touches cvars the
            // user actually changed AND the server Has, so it never clobbers a map/ruleset value they didn't set.
            // (The unified path needs none of this — its store is never reloaded out from under the user.)
            if (string.IsNullOrEmpty(_campaignName))
                XonoticGodot.Engine.Simulation.CvarService.BackfillModified(
                    _sharedCvars, _serverWorld.Services.CvarsImpl, BootAuthoredCvars);

            _sharedCvarBridge = name =>
            {
                var server = _serverWorld?.Services.CvarsImpl;   // re-read the field so a map change is followed
                if (server is not null && server.Has(name) && _sharedCvars is not null)
                    server.Set(name, _sharedCvars.GetString(name));
            };
            _sharedCvars.Changed += _sharedCvarBridge;
        }

        // --- bots so a solo host has opponents to see/play (QC bot_number / minplayers fill). ---
        // [T39] Seed the SERVER store's bot_number/skill instead of direct ClientConnect calls: the live
        // BotPopulation fixcount (GameWorld.OnStartFrame → Bots.ServerFrame, QC bot_fixcount) fills one bot per
        // frame from time 2.5 — and, critically, a faithful fixcount REMOVES bots above the bot_number target
        // at its first recount, so host-added bots must raise the floor, not bypass it (QC bot.qc:682-683).
        if (_botCount > 0)
        {
            _serverWorld.Services.Cvars.Set("bot_number",
                _botCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Seed `skill` only when the caller actually chose one (menu slider / campaign level). A bare CLI
            // `--host --bots N` leaves it -1 = unspecified — writing the old implicit default here silently
            // stomped the user's/stock skill (a perf run left every bot at skill 0). Both cvars are plain-`set`
            // in Base (never archived), so these writes affect the live match only, never config.cfg.
            if (_botSkill >= 0)
                _serverWorld.Services.Cvars.Set("skill",
                    _botSkill.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // any bot leaving (fixcount trim / removebots / intermission teardown) must clear ServerNet's
        // per-player id/antilag maps, exactly like the old explicit remove handler did.
        _serverWorld.Bots.BotRemoved += p => _server?.ForgetPlayer(p);
        _serverWorld.Bots.MaxClients = 16; // QC maxclients (mirrors ServerNet.Start's maxClients below)

        ServerNet? server = ServerNet.Start(_serverWorld, _port, maxClients: 16, serverName: _serverName);
        if (server is null)
        {
            GD.PrintErr($"[NetGame] could not start the listen server on UDP {_port} (port in use?).");
            return;
        }
        _server = server;
        // Answer server-browser getinfo probes so this host shows up in the LAN list (no master heartbeat —
        // the port's transport is ENet, so registering with the public DP masters would only mislead DP clients).
        server.EnableLanDiscovery(_port);

        // --- wire the server-command host sinks (QC the say bus / bprint + bot_cmd add/remove). Without these,
        //     console/clc_stringcmd `say`/`bot_add`/`setbots` no-op; with them, chat reaches every client's console
        //     and bots are added/removed live. Bots auto-join + spawn on connect (ClientManager.ClientConnect) and
        //     the per-tick snapshot delta networks/despawns them (NetIdFor is lazy), so no extra net plumbing is
        //     needed beyond cleaning ServerNet's per-player maps on removal (ForgetPlayer). ---
        Commands cmd = _serverWorld.Commands;

        if (!wantThreaded)
        {
            // STOCK single-threaded sinks — byte-for-byte the pre-S5 wiring (exact synchronous return values, which
            // the console feedback + the removebots loop depend on). This branch is what the default path uses.

            // player chat (say) → broadcast to everyone incl. the sender; server announcements (vote/ban/team) → same.
            cmd.ChatHandler = (caller, msg, teamOnly) =>
                _server?.BroadcastPrint($"{(teamOnly ? "(team) " : "")}{caller?.NetName ?? "server"}^7: {msg}");
            cmd.ChatBroadcast = msg => _server?.BroadcastPrint(msg);

            // [T46] per-player chat delivery: Chat.Say routes team/private/spectator/public lines through sprint()
            //       → Commands.ChatToPlayer; wire it to the reliable per-peer svc_print so each routed recipient
            //       (and only that recipient) gets the line. Without this every routed sprint() silently no-ops.
            cmd.ChatToPlayer = (player, text) => _server?.SendChatToPlayer(player, text);
            // [T46] QC dedicated_print(): echo every delivered chat line to the SERVER's own console, independent
            //       of client delivery (a baseline feature for the host operator). Routes to stdout via GD.Print.
            cmd.ChatConsole = text => GD.Print(text);

            // bot_add / setbots: spawn a brained bot through the live population (raises bot_number so the faithful
            // fixcount keeps it); the snapshot loop networks it next tick. [T39]
            cmd.AddBotHandler = (name, skill) => _serverWorld?.Bots.AddBot(name, skill) is not null;

            // bot_remove / removebots: drop the newest matching bot via the population (lowers bot_number so
            // fixcount doesn't re-add); the disconnect chain fires Bots.BotRemoved → ForgetPlayer clears ServerNet's
            // id/antilag maps, and the next snapshot delta despawns it on clients. [T39]
            cmd.RemoveBotHandler = name => _serverWorld?.Bots.RemoveBot(name) ?? false;

            // map / gotomap / nextmap / map-vote / rotation / samelevel all funnel here (QC changelevel): record the
            // target; the deferred emit in _Process reboots the listen server on it (preserving gametype + bots).
            cmd.ChangeLevelHandler = RequestMapChange;

            // QC DoNextMapOverride host exits: all three fire inside the server sim tick (DriveEndOfMatchMapFlow),
            // so each must be DEFERRED (Callable.From...CallDeferred) exactly like MapChangeRequested — never tear
            // down the game tree from inside the tick that drives it. The handlers route through MenuCommand so they
            // behave identically to a player typing "quit" / "connect" / "disconnect" in the console.

            // QC quit_when_empty → localcmd("quit"): shut the process when the last human leaves at match end.
            cmd.QuitHandler = () => Callable.From(() => GetTree()?.Quit()).CallDeferred();

            // QC quit_and_redirect → redirection_target: reconnect every client (here: the local player) to the
            // target server. Routes through MenuCommand.Connect so Shell tears down the current game then connects.
            cmd.RedirectHandler = addr => Callable.From(() => MenuCommand.Connect?.Invoke(addr)).CallDeferred();

            // QC lastlevel → localcmd("set lastlevel 0\ntogglemenu 1\n"): after the final campaign/last-level map
            // show the main menu instead of rotating. lastlevel is already cleared by the server before invoking this.
            // MenuCommand.Disconnect (wired to Shell.ReturnToMainMenu) tears down the match and returns to the menu.
            cmd.LastLevelHandler = () => Callable.From(() => MenuCommand.Disconnect?.Invoke()).CallDeferred();
        }
        else
        {
            // S5 THREADED sinks: these fire on the MAIN thread (console/menu input) but touch _server/_serverWorld,
            // which the worker thread owns — route each through ServerNet.RunOnSimThread so it executes on the sim
            // thread under the gate at the next tick top. The bot handlers can no longer return a synchronous
            // success (the work runs later on the worker), so they report true and the result surfaces in the next
            // snapshot — faithful to how a remote add/remove would behave. This branch is reached ONLY when
            // sv_threaded 1 on a non-headless host, so it never affects the default path.
            ServerNet s = _server!; // non-null here (the null-check above returned early)

            cmd.ChatHandler = (caller, msg, teamOnly) =>
                s.RunOnSimThread(() => _server?.BroadcastPrint($"{(teamOnly ? "(team) " : "")}{caller?.NetName ?? "server"}^7: {msg}"));
            cmd.ChatBroadcast = msg => s.RunOnSimThread(() => _server?.BroadcastPrint(msg));
            // [T46] per-player chat + server-console echo on the threaded path: the send touches _server (worker-owned),
            //       so route the per-peer delivery through the sim thread; the console echo is GD.Print-only (thread-safe).
            cmd.ChatToPlayer = (player, text) => s.RunOnSimThread(() => _server?.SendChatToPlayer(player, text));
            cmd.ChatConsole = text => GD.Print(text);
            cmd.AddBotHandler = (name, skill) => { s.RunOnSimThread(() => _serverWorld?.Bots.AddBot(name, skill)); return true; };
            cmd.RemoveBotHandler = name => { s.RunOnSimThread(() => _serverWorld?.Bots.RemoveBot(name)); return true; };
            cmd.ChangeLevelHandler = map => s.RunOnSimThread(() => RequestMapChange(map));

            // QC DoNextMapOverride host exits (threaded path): the handlers fire on the SIM thread (inside the
            // server tick), so each uses CallDeferred to post the Godot tree operation to the main thread — the
            // same pattern as the non-threaded path (no RunOnSimThread wrapper needed; the callers are already on
            // the sim thread and just need to cross to the main thread, not the sim thread).
            cmd.QuitHandler = () => Callable.From(() => GetTree()?.Quit()).CallDeferred();
            cmd.RedirectHandler = addr => Callable.From(() => MenuCommand.Connect?.Invoke(addr)).CallDeferred();
            cmd.LastLevelHandler = () => Callable.From(() => MenuCommand.Disconnect?.Invoke()).CallDeferred();
        }

        // QC localcmd("\nsv_vote_gametype_hook_all\n") + localcmd("\nsv_vote_gametype_hook_", name, "\n") from
        // GameTypeVote_SetGametype: fires on the sim thread (ApplyGametypeSwitch → DriveEndOfMatchMapFlow), so the
        // handler runs synchronously on that thread — no RunOnSimThread wrapper needed on either path.
        // These hooks are ALIASES (gametypes-server.cfg defines `sv_vote_gametype_hook_<name>` for every gametype,
        // empty by default), NOT registered server commands — so they live in the ConfigInterpreter's alias table
        // (GameWorld.LoadedConfig), not in Commands' verb dictionary. Routing through Commands.Execute would just
        // print "Unknown command" and never run the alias body; we must dispatch through LoadedConfig.ExecuteLine,
        // which resolves the alias (DP localcmd → engine console → alias expansion). Stock empty aliases are a
        // no-op (matching Base); a server.cfg that redefines one (e.g. `alias sv_vote_gametype_hook_dm
        // "g_maxplayers 8"`) now runs against the server's cvar store. LoadedConfig is null only on a bare
        // unit-test world (no cfg load) — the `?.` then makes this a harmless no-op, same as no alias defined.
        cmd.GameTypeVoteHookHandler = name =>
            _serverWorld?.LoadedConfig?.ExecuteLine($"sv_vote_gametype_hook_{name}");

        // QC localcmd("\nsv_hook_gameend\n") (NextLevel): the once-per-match end-of-match admin-script alias. Same
        // alias-resolution path as the gametype hooks above — dispatch through LoadedConfig.ExecuteLine so the
        // engine console expands the `sv_hook_gameend` alias. Stock config leaves it empty (a no-op, matching
        // Base); a server.cfg that redefines the alias runs against the server's cvar store. LoadedConfig is null
        // only on a bare unit-test world (no cfg load) — the `?.` then makes this a harmless no-op.
        cmd.GameEndHookHandler = () =>
            _serverWorld?.LoadedConfig?.ExecuteLine("sv_hook_gameend");

        // sandbox build mode (g_sandbox): wire the SandboxMutator's host seams (per-player print, crosshair trace,
        // owner-UID/name/view-yaw, real-client roster, per-map file storage). The `g_sandbox` command routes to
        // SandboxMutator.HandleCommand (Commands.CmdSandbox); without these the handler runs but every action is
        // inert (spawns at origin, nothing selectable, no persistence). Runs on the sim thread (HandleCommand is
        // dispatched there); the only main/worker-shared touch is the client print, routed through the sim thread
        // when threaded. Safe even with g_sandbox 0 (the seams only fire while the mutator is added).
        WireSandbox();

        // superspec spectator commands (g_superspectate): wire the SuperSpecMutator's host seams (per-player sprint
        // print + the live client roster used by the follow*/followkiller scans). The superspec/autospec/follow*
        // verbs route to SuperSpecMutator.HandleCommand (Commands.CmdSuperspec); without these the option flags can
        // be SET but the follow* switches have no roster and messages print nothing. Safe with g_superspectate 0
        // (the seams only fire while the mutator is added). Same sim-thread ownership as WireSandbox.
        WireSuperspec();

        // target_speaker ACTIVATOR (BIT3) — QC soundto(MSG_ONE, …): play a one-shot sound to the TRIGGERING
        // client ONLY (the `target_speaker_use_activator` path in speaker.qc). The Common layer exposes a
        // PlayToClientHandler seam (TargetSpeaker.cs); without it the live path falls back to a broadcast play
        // and every connected client hears the sound. Wire it here (after _server is live) using the new
        // ServerNet.SendSoundToPlayer method, which encodes a per-peer SoundBundle via the same SoundWire codec
        // as FlushSounds so the per-client sound rides an existing channel without protocol changes.
        // The seam is re-wired on every server start (every NetGame.SetupListenServer call), so a map restart
        // or gametype change never leaves it stale. On sv_threaded the seam fires on the sim thread (the
        // GameWorld.Use → TargetSpeaker.SpeakerUseActivator call chain), which owns ServerNet writes — safe.
        {
            ServerNet sv = _server!;
            XonoticGodot.Common.Gameplay.TargetSpeaker.PlayToClientHandler =
                (client, emitter, ch, sample, vol, atten) =>
                {
                    // QC IS_REAL_CLIENT guard is already applied before this seam fires (SpeakerUseActivator).
                    // Only route for a real Player; a bare Entity activator has no peer to target.
                    if (client is Player p)
                        sv.SendSoundToPlayer(p, emitter, ch, sample, vol, atten);
                };
        }

        // S5: spin up the dedicated server-sim worker now that ServerNet + the world + the sinks are all wired.
        // Install the shared gate on ServerNet (its Tick locks it) and hand the SAME object to the main thread
        // (_simGate) so _Process serialises its prediction span against the worker. Start LAST so no tick runs
        // before the sinks/gate are in place. Headless hosts never reach here (wantThreaded gated it off).
        if (wantThreaded)
        {
            _simGate = new object();
            _server!.SimGate = _simGate;
            // The music player's _Process scans the live server entity list (wired in SetupMusic, called earlier)
            // on the main thread — hand it the SAME gate so that scan can't race the worker's spawn/relink. Stays
            // null on the non-threaded default path, so its scan remains lock-free.
            if (_musicPlayer is not null) _musicPlayer.SimGate = _simGate;
            // The trace service keeps shared mutable scratch and reads the live areagrid — MAIN-thread traces
            // (faithful-particle bounces, crosshair true-aim, projectile prediction; none run inside this
            // class's gated _Process span) raced the worker's sim ticks and corrupted both ends (the first
            // threaded soak's NRE storm). Gate every trace entry point on the SAME object; the worker holds it
            // around its whole tick (Monitor is reentrant), the single-threaded default path stays lock-free.
            _serverWorld!.Services.TraceImpl.ConcurrencyGate = _simGate;
            // WS1 stage 3: the main thread's display/feed reads of the PRIVATE server store (music cdtrack,
            // endscreen cvars, slowmo autopause…) run ungated against the worker's writes — swap the store to
            // its thread-safe backing BEFORE the worker exists. The unthreaded default path never pays this
            // (the plain-Dictionary backing measured 32% faster per tick at 4 players).
            _serverWorld!.Services.CvarsImpl.EnableConcurrentReads();
            // WS1 per-tick gating: the worker holds the gate around EACH tick (not the whole step), so a
            // render-thread per-trace wait is bounded by one tick.
            _serverWorld!.Simulation.TickGate = _simGate;
            _serverThread = new ServerThread(
                _serverWorld!, _server!,
                static () => XonoticGodot.Engine.Simulation.SimulationLoop.TicRate);
            _serverThread.Start();
            GD.Print("[NetGame] sv_threaded 1 — server simulation running on a dedicated worker thread (XG-ServerSim).");
        }
    }

    /// <summary>
    /// Wire the <see cref="SandboxMutator"/>'s host seams — the C# successor to the cross-file calls the QC
    /// sandbox mutator makes into the engine/server (crypto_idfp/netname/v_angle, FOREACH_CLIENT, the crosshair
    /// trace, print_to, and the <c>sandbox/storage_*.txt</c> file IO). Without these the command surface (now
    /// routed via <c>g_sandbox</c> → Commands.CmdSandbox → HandleCommand) executes but every effect is inert.
    /// Idempotent and harmless when g_sandbox 0 (the delegates are only invoked while the mutator is added).
    ///
    /// <para>Threading: the sandbox command + the autosave/think tick both run on the sim thread (HandleCommand is
    /// dispatched from there; OnStartFrame fires inside the server tick), which is the thread that owns
    /// <see cref="_server"/> / <see cref="_serverWorld"/> on the threaded path — so the print/trace touch the
    /// right owner directly, no <c>RunOnSimThread</c> hop needed.</para>
    /// </summary>
    private void WireSandbox()
    {
        if (_serverWorld is null) return;
        if (Mutators.ByName("sandbox") is not SandboxMutator sandbox) return;

        ITraceService trace = _serverWorld.Services.TraceImpl;

        // QC print_to(player, msg) → sprint(player, ..): one console line to the issuing player.
        sandbox.PrintTo = (e, msg) => { if (e is Player p) _server?.SendChatToPlayer(p, msg); };

        // QC .crypto_idfp / .netname / view yaw. PersistentId is the port's crypto_idfp (RaceRecords/SuperSpec
        // use the same field); a bot's is "" so it can't own objects, faithful to "bots cannot own objects".
        sandbox.CryptoIdfpProvider = e => e is Player p ? p.PersistentId : "";
        sandbox.NetNameProvider = e => e?.NetName ?? "";
        sandbox.ViewYawProvider = e => e?.Angles.Y ?? 0f; // angles.y is the view yaw (object_spawn angles_y = v_angle.y)

        // QC FOREACH_CLIENT(IS_PLAYER && IS_REAL_CLIENT): the live real (non-bot) roster, pulled each think for the
        // owner-UID resync (clear when the owner disconnects). Matches the Warmup/Voting/Bans .Roster seams.
        sandbox.RealClientsProvider = () =>
        {
            var list = new List<Entity>();
            foreach (Player p in _serverWorld.Clients.Players)
                if (!p.IsBot) list.Add(p);
            return list;
        };

        // QC sandbox_ObjectEdit_Get / object_spawn forward trace (crosshair_trace / WarpZone_TraceLine forward
        // distance). Trace a point line from the player's eye along the view forward; the endpoint is the spawn
        // origin, and a hit object is resolved by matching the trace entity to a live sandbox object's edict.
        sandbox.Trace = new SandboxTraceProvider(sandbox, trace);

        // QC sandbox_Database_Save/_Load: the per-map storage file is sandbox/storage_<name>_<map>.txt under the
        // writable user dir (DP writes to the gamedir; the port's writable root is ~/XonData via UserPaths). This
        // makes save/autosave/autoload genuinely persist. The object_duplicate copy clipboard (QC stuffcmd `set
        // cl_sandbox_clipboard …`) is now live for the listen-server local host: SetClipboard sets the cvar
        // directly on the shared store the local console reads. A remote client over a dedicated server still
        // can't be reached (no svc_stufftext channel), and is silently skipped (the documented residual).
        sandbox.Store = new SandboxObjectStore(SetSandboxClipboard);

        // QC fexists(argv(2)): the asset VFS is the port's filesystem-existence check (DP's fexists). Wiring it
        // makes object_spawn reject a model path that doesn't resolve in any mount, exactly like Base.
        if (_vfs is not null)
            sandbox.ModelExistsProvider = path => _vfs.Exists(path);

        // QC FOR_EACH_TAG(e) for `object_info mesh`: enumerate the object model's tag (bone) names from the
        // engine model-tag table (ModelService.ModelDef.Tags). Empty list when the model has no registered tags.
        // _serverWorld.Services.ModelsImpl is the concrete ModelService (non-null), same accessor TraceImpl uses.
        XonoticGodot.Engine.Simulation.ModelService models = _serverWorld.Services.ModelsImpl;
        sandbox.MeshTagNamesProvider = model => models.TryGetModel(model, out var def)
            ? new List<string>(def.Tags.Keys)
            : (IReadOnlyList<string>)System.Array.Empty<string>();
    }

    /// <summary>
    /// QC <c>stuffcmd(player, strcat("set ", cvar, " \"", value, "\""))</c> for the <c>object_duplicate copy</c>
    /// clipboard. The only console the port can reach in-process is the listen-server local host's, which reads
    /// the SAME <see cref="_sharedCvars"/> store the menu/client use — so for that recipient we set the cvar
    /// directly (the local equivalent of the svc_stufftext the QC sends). A pure remote client over a dedicated
    /// server has no in-process cvar store here and no stufftext channel, so it is skipped (documented residual).
    /// </summary>
    private void SetSandboxClipboard(Entity player, string cvar, string value)
    {
        if (string.IsNullOrEmpty(cvar) || _sharedCvars is null)
            return;
        if (!ReferenceEquals(player, LocalServerPlayer))
            return; // only the local host's console shares this cvar store
        // HandleCommand escapes the serialized object (\" ) for the QC stuffcmd `set cvar "…"` console parse;
        // the engine console UN-escapes it back to real quotes before storing the cvar. We set the cvar value
        // directly (no console reparse), so undo that escaping here to store the same value paste/ObjectPortLoad
        // (which tokenizes "-quoted tokens) expects.
        // WS1: this is dispatched on the SIM worker (HandleClientCommand), but the SHARED store is main-owned
        // (plain Dictionary, read/written by console/menu every frame — only the server's PRIVATE store is
        // concurrent-enabled). Cross to the main thread for the write; a one-frame clipboard delay is harmless.
        string unescaped = value.Replace("\\\"", "\"");
        Callable.From(() =>
        {
            _sharedCvars?.Set(cvar, unescaped);
            _sharedCvars?.MarkArchived(cvar); // cl_sandbox_clipboard is an archived client cvar
        }).CallDeferred();
    }

    /// <summary>
    /// Wire the <see cref="SuperSpecMutator"/>'s host seams — the C# successor to the engine calls the QC superspec
    /// mutator makes from its SV_ParseClientCommand / PlayerDies hooks: <c>sprint(to, ..)</c> for the option/state
    /// messages, and <c>FOREACH_CLIENT</c> for the follow* / followkiller player scans. Without these the option
    /// flags can be set (the command surface routes via <c>superspec</c>/<c>autospec</c>/… → Commands.CmdSuperspec
    /// → HandleCommand) but the manual follow* switches have no roster to scan and every message prints nothing.
    /// Idempotent and harmless when g_superspectate is off (the delegates only fire while the mutator is added).
    /// Same sim-thread ownership as <see cref="WireSandbox"/> (HandleCommand is dispatched there; PlayerDies fires
    /// inside the server tick), so the print/roster touch the right owner directly.
    /// </summary>
    private void WireSuperspec()
    {
        if (_serverWorld is null) return;
        if (Mutators.ByName("superspec") is not SuperSpecMutator superspec) return;

        // QC sprint(to, strcat(con_title, msg)): one console line to the spectator. (The centered centerprint form
        // goes through the notification CenterRaw channel directly inside SuperSpecMutator.Msg — no host seam.)
        superspec.PrintTo = (p, msg) => _server?.SendChatToPlayer(p, msg);

        // QC FOREACH_CLIENT: the live client roster the follow*/followkiller/ItemTouch scans iterate.
        superspec.RosterProvider = () =>
        {
            var list = new List<Player>();
            foreach (Player p in _serverWorld.Clients.Players) list.Add(p);
            return list;
        };

        // QC .crypto_idfp — the player's auth UID, used to key the remote options file. PersistentId is the
        // port's crypto_idfp (same field RaceRecords/Sandbox use); a bot's is "" (so it never persists).
        superspec.CryptoIdfpProvider = p => p.PersistentId;

        // QC the per-client options-file IO (fopen/fputs/fgets in superspec_save_client_conf / ClientConnect).
        // Files live under the writable user dir (~/XonData/superspec/<filename>, DP's writable-gamedir analogue).
        superspec.Store = new SuperspecOptionsStore();
    }

    /// <summary>The per-client options-file persistence seam for the superspec mutator (QC fopen/fputs/fgets in
    /// superspec_save_client_conf / ClientConnect). Files live under the writable user dir
    /// (<c>~/XonData/superspec/&lt;filename&gt;</c>), one line per field, exactly as the QC reader/writer expects.</summary>
    private sealed class SuperspecOptionsStore : SuperSpecMutator.IOptionsStore
    {
        private static string PathFor(string filename) => UserPaths.Resolve($"superspec/{filename}");

        public System.Collections.Generic.IReadOnlyList<string>? Read(string filename)
        {
            try
            {
                string p = PathFor(filename);
                return System.IO.File.Exists(p) ? System.IO.File.ReadAllLines(p) : null;
            }
            catch { return null; }
        }

        public void Write(string filename, System.Collections.Generic.IReadOnlyList<string> lines)
        {
            try { System.IO.File.WriteAllLines(PathFor(filename), lines); } catch { /* read-only host */ }
        }
    }

    /// <summary>The crosshair/forward trace seam for the sandbox mutator (QC crosshair_trace / WarpZone_TraceLine).
    /// Resolves a hit sandbox object by matching the traced entity to a live object's edict.</summary>
    private sealed class SandboxTraceProvider : SandboxMutator.ITraceProvider
    {
        private readonly SandboxMutator _sandbox;
        private readonly ITraceService _trace;
        public SandboxTraceProvider(SandboxMutator sandbox, ITraceService trace) { _sandbox = sandbox; _trace = trace; }

        public void TraceForward(Entity player, float distance, out NVec3 endpos, out SandboxMutator.SandboxObject? hit)
        {
            NVec3 eye = player.Origin + player.ViewOfs;
            NVec3 fwd = QMath.Forward(player.Angles);
            NVec3 end = eye + fwd * distance;
            TraceResult tr = _trace.Trace(eye, NVec3.Zero, NVec3.Zero, end, MoveFilter.Normal, player);
            endpos = tr.EndPos;
            hit = null;
            if (tr.Ent is not null)
                foreach (var o in _sandbox.Objects)
                    if (ReferenceEquals(o.Edict, tr.Ent)) { hit = o; break; }
        }
    }

    /// <summary>The per-map text persistence seam for the sandbox mutator (QC sandbox_Database_Save/_Load file IO).
    /// File lives under the writable user dir (<c>~/XonData/sandbox/storage_&lt;name&gt;_&lt;map&gt;.txt</c>, DP's
    /// writable-gamedir analogue). The object_duplicate copy clipboard is left inert (no svc_stufftext transport
    /// to set a remote client's cvar — see <see cref="SetClipboard"/>).</summary>
    private sealed class SandboxObjectStore : SandboxMutator.IObjectStore
    {
        private readonly Action<Entity, string, string> _setClipboard;
        public SandboxObjectStore(Action<Entity, string, string> setClipboard) => _setClipboard = setClipboard;

        private static string PathFor(string storageName, string mapName)
            => UserPaths.Resolve($"sandbox/storage_{storageName}_{mapName}.txt");

        public string? Read(string storageName, string mapName)
        {
            try { string p = PathFor(storageName, mapName); return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : null; }
            catch { return null; }
        }

        public void Write(string storageName, string mapName, string contents)
        {
            try { System.IO.File.WriteAllText(PathFor(storageName, mapName), contents); } catch { /* read-only host */ }
        }

        // QC stuffcmd(player, strcat("set ", cvar, " \"", value, "\"")) — pushes the serialized object into the
        // player's clipboard cvar. The host callback handles the only transport the port has: setting the cvar
        // directly on the SHARED cvar store when the target is the listen-server local host (whose console reads
        // the same store the menu/client read). A remote client over a dedicated server still can't be reached
        // (no svc_stufftext channel) — that recipient is silently skipped, matching the documented residual.
        public void SetClipboard(Entity player, string cvar, string value) => _setClipboard(player, cvar, value);
    }

    /// <summary>
    /// Boot a minimal ambient engine facade for a PURE CLIENT so the prediction sim has a world to trace
    /// against before/while connected. We use a flat floor: a remote client does not yet replicate the
    /// server's real map collision (that needs the map name in the handshake + a client-side BSP load — a
    /// follow-up). Prediction, the camera, input and remote-entity rendering all work over this floor; only
    /// the local player's collision against real geometry is approximate until that lands.
    /// </summary>
    private void BootClientFacade()
    {
        var services = new EngineServices(BuildTestFloor(), _sharedCvars);
        GameInit.Boot(services);                 // Api.Services = services; + movement/registries
        // Seed weapon balance from whatever cvars are loaded (the menu preloaded the tree into _sharedCvars).
        XonoticGodot.Common.Gameplay.Weapons.ConfigureAll();
        // QC: REGISTER_MUTATOR(walljump, true) on CSQC — Base registers movement mutators (walljump,
        // doublejump, dodging, …) on the CLIENT unconditionally so their PlayerJump/PMPhysics hooks run
        // inside client prediction. The port's MutatorHooks.PlayerJump chain is static and shared with the
        // server, so prediction already works in a listen-server build (server Apply() populates the chain
        // before the client sim runs). But on a DEDICATED-SERVER client the server runs in a separate
        // process: the static chain is empty on the client, so every wall jump / dodge / doublejump is
        // applied by the server only and the client mispredicts (rubberband). Calling Apply() here mirrors
        // Base's CSQC unconditional registration: it subscribes every currently-enabled movement mutator's
        // hooks onto the client process's static chain so prediction stays in lockstep.
        // Apply() is idempotent (MutatorBase.Added guard) — safe to call even if called again later.
        MutatorActivation.Apply();
    }

    /// <summary>
    /// Build the predicted <see cref="ClientNet"/> and connect. The local player is predicted by driving the
    /// shared <see cref="Movement"/> sim over a carrier entity (<see cref="EntityMovementStep"/>) — the same
    /// deterministic sim the server runs — and the input is sampled live each tick from WASD/mouse.
    /// </summary>
    private void StartClient()
    {
        // The carrier the predictor integrates: a real player edict in the ambient world (the listen server's,
        // or the client facade's), set up exactly like PlayerController's player entity so PlayerPhysics has a
        // proper hull + client flags to slide-and-step.
        _carrier = SpawnCarrier();

        _movementStep = _carrier is not null ? new EntityMovementStep(_carrier) : null;
        IMovementStep step = (IMovementStep?)_movementStep ?? new NullMovementStep();

        ClientNet? client = ClientNet.Connect(_host, _port, step, SampleInput);
        if (client is null)
        {
            GD.PrintErr($"[NetGame] could not create the client socket for {_host}:{_port}.");
            return;
        }
        _client = client;
        _client.LocalPlayerName = _playerName;
        // Relay server console output (clc_stringcmd replies / notices) to the in-game console via the Shell.
        _client.PrintReceived += s => ConsolePrint?.Invoke(s);
    }

    /// <summary>Spawn the carrier player entity (the local hull the predictor moves), mirroring PlayerController._Ready.</summary>
    private Entity? SpawnCarrier()
    {
        if (Api.Services is null)
            return null;
        _world = Api.Services; // remember the facade so teardown removes the carrier from the right table
        // S5: SpawnCarrier runs AFTER StartListenServer started the worker (boot order), so Api.Entities.Spawn()
        // mutates the SAME entity table the worker ticks — take the gate around the spawn when threaded so it can't
        // race a concurrent server-tick spawn/relink. No-op (no lock) when not threaded. The field-sets below act
        // on the freshly-returned edict only; doing them under the gate too is harmless and keeps it simple.
        object? simGate = _simGate;
        bool taken = false;
        try
        {
            if (simGate is not null)
                System.Threading.Monitor.Enter(simGate, ref taken);
        Entity e = Api.Entities.Spawn();
        e.ClassName = "client_predict";
        e.MoveType = MoveType.Walk;
        // IMPORTANT: the carrier is the client-side PREDICTION copy, not an authoritative actor. Keep it
        // SOLID_NOT so it never enters the trace/solid set — on a listen server the real authoritative Player
        // lives in this same entity table, and a solid prediction ghost would let the server collide with it.
        // The movement sim sweeps the carrier's hull against the world regardless of its own Solid value, so
        // prediction still slides-and-steps correctly.
        e.Solid = Solid.Not;
        // The carrier is kept SOLID_NOT (above) so the listen-server authority never collides with the
        // prediction ghost — but for MOVEMENT it IS a SOLID_SLIDEBOX player and must clip the SAME content mask
        // the authoritative server Player does (SpawnSystem sets the real player SOLID_SLIDEBOX → Solid|Body|
        // PlayerClip). Without this, GenericHitMask derives the SOLID_NOT default (Solid|Body|CORPSE), so the
        // carrier collides with our OWN projectiles: PROJECTILE_MAKETRIGGER makes them SOLID_CORPSE precisely so
        // a player's PlayerClip move passes through them, but the predicted carrier is a DISTINCT entity from the
        // projectile's server-side .Owner, so the owner trace-exception can't protect it — and the CORPSE bit in
        // the default mask let the rocket/orb block (and detonate on) the firer anyway. Match the server player's
        // mask via the DP dphitcontentsmask override so prediction and authority stay in lockstep. (See
        // Projectiles.MakeTrigger / TraceService.GenericHitMask.)
        e.DpHitContentsMask = SuperContents.Solid | SuperContents.Body | SuperContents.PlayerClip;
        e.Flags |= EntFlags.Client | EntFlags.JumpReleased;
        e.Mins = HullMins;
        e.Maxs = HullMaxs;
        e.ViewOfs = new NVec3(0f, 0f, EyeHeight);
        // Fresh carrier = fresh input-sequence space (seqs restart per connection): reset the seq-keyed
        // predicted-warp pulse or a stale high seq from the previous match would gate it shut forever.
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictionSeq = 0;
        XonoticGodot.Engine.Simulation.TriggerTouch.LastPredictedWarpSeq = 0;
        _consumedWarpSeq = 0;
        return e;
        }
        finally
        {
            if (taken)
                System.Threading.Monitor.Exit(simGate!);
        }
    }

    // cl_bench_spectate: next server-time the bench camera re-evaluates (0.5 s cadence — cheap, and the
    // re-glue after a spectatee death only needs sub-second latency, not per-frame work).
    private float _benchSpectateNextThink;

    /// <summary>True while <c>cl_bench_spectate</c> holds the host as an observer camera — read where the
    /// normal host-join flow must stand down (the [#44] early-join block; BenchSpectateThink owns the camera).</summary>
    private bool BenchSpectateActive => (_sharedCvars?.GetFloat("cl_bench_spectate") ?? 0f) != 0f;

    /// <summary>
    /// <c>cl_bench_spectate</c> (demo/benchmark captures): keep the local listen-server host OBSERVING a
    /// living bot, so the capture camera experiences real gameplay — map traversal, combat, and first-person
    /// gunplay via the follow-cam (<see cref="UpdateCamera"/>) + the spectated viewmodel — instead of idling
    /// at a spawn point. Host-side twin of the manual <c>spectate</c> client command (same ClientManager
    /// calls). Re-glues after the spectatee dies (the per-tick glue drops a follower whose target died or
    /// re-observed — ClientManager's SpectateCopy gate) or leaves; if the host somehow joined the match (a
    /// stray +jump), it is pulled back to observer — bench mode owns the camera by design. Runs inside the
    /// _Process sim-gate span, so the server-state mutations are safe under sv_threaded too.
    /// </summary>
    private void BenchSpectateThink()
    {
        if (_serverWorld is null || !_isListenServer)
            return;
        float now = _serverWorld.Time;
        if (now < _benchSpectateNextThink)
            return;
        _benchSpectateNextThink = now + 0.5f;
        // NOTE: no cl_bench_spectate read here — on the threaded path this runs on the SIM worker and the
        // shared cvar store is main-owned. The dispatch site (_Process) gates on BenchSpectateActive on main.
        if (LocalServerPlayer is not { } host)
            return;
        var clients = _serverWorld.Clients;
        if (!host.IsObserver)
        {
            // The real observer transition (model hidden, weapons stripped, non-solid, autojoin disarmed) —
            // exactly what the spectate command runs. Follow on the next think, once the state settled a tick.
            clients.PutObserverInServer(host);
            return;
        }
        if (host.Spectatee is not null)
            return; // glued — SpectateCopy tracks the bot each tick
        foreach (var p in clients.Players)
        {
            // First living bot in roster order = a deterministic pick for captures. After a spectatee death
            // (which drops the follower to free-fly) this re-attaches — usually to the same bot once its
            // forced respawn lands, else to the next living one.
            if (p.IsBot && !p.IsObserver && !p.IsDead)
            {
                clients.Spectate(host, p);
                XonoticGodot.Common.Diagnostics.Log.Info($"[bench] spectating bot '{p.NetName}' (slot {p.Index})");
                break;
            }
        }
    }

    private void SetupRender()
    {
        _render = new ClientWorld { Name = "Render" };
        // Resolve models/sounds straight from the mounted content VFS so remote players render as real skeletal
        // IQM models + positional sounds resolve (else the placeholder box + the res:// fallback).
        if (_assets is not null)
        {
            _render.AudioLoader = _assets.LoadSound;
            _render.Assets = _assets.Assets;   // material facade → textured world/vehicle models (ModelResolver path)
            // Remote players render as skeletal IQM models (models/player/*.iqm); non-player networked entities
            // (items/gibs/monsters/props) render their real model by name — MD3 via the frame-driven
            // ModelResolver, any other format (IQM/DPM) via the EntityModelFactory fallback.
            _render.PlayerModelResolver = ResolvePlayerModel;
            _render.ModelResolver = ResolveEntityModel;
            _render.EntityModelFactory = BuildEntityModel;
            // FORCEMODEL: resolve a forced player-model name+skin to a render node (the cl_forcemyplayermodel /
            // cl_forceplayermodels swap). Streams the skeletal forced model through the same async placeholder path
            // as the live player resolve (a forced-model snapshot never re-introduces the sync build stall); a
            // non-IQM forced model still wins via the cached LoadModel node (FORCEMODEL parity), and only a genuine
            // VFS miss returns null (QC fexists-miss → the entity keeps its own model). Receives the Entity so the
            // async delivery can validate staleness + re-attach via RebuildEntityModel.
            _render.ForcedModelResolver = BuildForcedPlayerModel;
        }
        AddChild(_render);

        // The background asset streamer (S1) — created unconditionally (not just for the idle warm) because the
        // LIVE player-model resolve streams through it: parse on the thread pool, Godot build under the per-frame
        // budget. One node per NetGame; freed with the scene.
        _streamer = new XonoticGodot.Game.Client.BackgroundAssetStreamer { Name = "AssetStreamer" };
        AddChild(_streamer);

        // Networked projectiles draw their REAL model (rocket.md3 with its additive RocketThrust flame cone,
        // grenademodel.md3) through the same VFS loader the world/weapon models use. Set after AddChild since
        // ClientWorld._Ready (which built _render.Projectiles) ran synchronously on it.
        if (_assets is not null)
        {
            _render.Projectiles.ModelFactory = m => _assets.LoadModel(m);
            // (engine-perf 2026-06-16) Wire the SAME loader into the gib + casing systems so they render their
            // REAL MD3/IQM limb + brass models (faithful to DP gibs.qc/casings.qc) instead of the generated
            // placeholder box/cylinder — the ModelLoader Func they support was never connected, so that path was
            // dead. The GpuWarmPass below warms these models' pipelines, so the restored MD3s cost no mid-match
            // SURFACE compile. (EffectSystem.ModelLoader propagates to its Gibs/Casings, both live post-AddChild.)
            _render.Effects.ModelLoader = m => _assets.LoadModel(m);

            // The cosmetic add-on layer (freezetag ice block / buff-carrier glow) reads its model files through
            // the same AssetLoader (cached): LoadMd3 for the .md3 morph path, LoadModel for IQM/DPM.
            _render.CosmeticMd3Loader = m => _assets.LoadMd3(m);
            _render.CosmeticModelLoader = (m, skin) => _assets.LoadModel(m, skin);
        }

        // Pre-warm the effect catalog + particlefont atlas now (map-load), so the FIRST weapon shot doesn't hitch
        // parsing effectinfo.txt + decoding the atlas on its render frame (DP precaches these at client init,
        // cl_particles.c). The Assets setter above already wired the texture/text loaders via WireEffectAssets,
        // and _render.Effects is live (ClientWorld._Ready ran synchronously on AddChild). Idempotent + invisible.
        _render.Effects.Warmup();
        // Likewise pre-build the shared per-type projectile-trail Resources so the first rocket/plasma/grenade
        // doesn't construct its trail material on its render frame (see ProjectileRenderer.WarmupTrails).
        _render.Projectiles.WarmupTrails();
        // A2: render one hidden instance of every effect/projectile material family in a tiny offscreen viewport
        // so the GPU compiles their shader pipelines NOW (during load) — the first real explosion/rocket/gib in
        // play then hits a warm pipeline instead of stalling the frame. Self-frees after a few frames.
        // The map-item / pickup MD3 models render through the entity feed (PVS-culled until first-seen), so warm
        // them here too — built from the item registry + the same AssetLoader the live entity build uses.
        XonoticGodot.Game.Client.GpuWarmPass.Run(_render, _render.Effects, _render.Projectiles, BuildItemWarmupInstances());

        // CSQC appearance context (FORCEMODEL/FORCECOLORS need the local player + gametype): read live each frame.
        _render.AppearanceProvider = BuildAppearanceContext;

        // [W14b LI3] the server clock for the remote torso-action overlay — the networked Entity.AnimActionTime is
        // stamped on this clock, so the client derives the action play phase as LatestServerTime − start.
        _render.ServerTimeProvider = () => _client?.LatestServerTime ?? 0f;

        // Render the world geometry + PVS entity-cull + client particle/decal collision from the loaded BSP.
        // On a listen server _bsp is already set (StartListenServer); a pure --connect client has no BSP here and
        // attaches this later — once the server's map name arrives — via LoadClientMapFromServer. No-op until then.
        AttachWorldRender();

        if (_client is not null)
        {
            _entityView = new ClientEntityView(_client, _render);
            // Third-person carried weapons: build each remote player's held weapon from the asset pipeline
            // (RC6) instead of the gray placeholder box — same v_ model the first-person viewmodel uses.
            _entityView.WeaponModelFactory = BuildWeaponWorldModel;
            AddChild(_entityView);

            // Surface decoded effects/sounds to the renderer as packets arrive: effects → particles, sounds →
            // spatial AudioStreamPlayer3D (DP SV_StartSound). Announcer voices are wired separately in
            // SetupCameraAndHud (they need a HUD host); other notification text still goes to the console.
            _client.EffectReceived += OnEffectReceived;
            _client.SoundReceived += OnSoundReceived;

            // Looping positional sounds (Arc beam, vehicle engines) follow their emitter each frame: resolve a
            // net id → its live origin (the local player from prediction, remotes from their interpolated pose).
            _render.EntityOriginResolver = ResolveEntityOrigin;
        }

        // The editor's world-space alignment grid (E2). Added unconditionally but hidden until
        // cl_editor_grid is set: it is a full-screen pass that costs nothing while invisible, and having it
        // resident means the `editor_grid` bind works instantly instead of building a shader on first toggle
        // (which would be a pipeline-compile hitch at exactly the wrong moment).
        var editorGrid = new Vmap.EditorGrid();
        AddChild(editorGrid);

        // (E3/E5) Editor nodes. Always present but inert outside the editor gametype, so toggling into it is
        // instant rather than paying node + shader construction at the moment the mapper asks for it.
        _editor = new Vmap.EditorController();
        _editor.Attach(_camera);
        AddChild(_editor);

        _editorGizmos = new Vmap.EditorGizmos();
        _editorGizmos.Attach(_editor);
        AddChild(_editorGizmos);

        _editorOrtho = new Vmap.EditorOrthoView { Name = "EditorOrtho" };
        _editorOrtho.Attach(_camera, _editor, _editorGizmos);
        AddChild(_editorOrtho);
        _editor.Ortho = _editorOrtho;
        editorGrid.Attach(_editor);

        AddLight();
    }

    /// <summary>
    /// Build + attach the map's render geometry (worldmodel), the PVS entity-cull, and the client-side
    /// particle/decal collision from the loaded <see cref="_bsp"/>. Shared by <see cref="SetupRender"/> (listen
    /// server — <see cref="_bsp"/> is loaded in <see cref="StartListenServer"/>) and
    /// <see cref="LoadClientMapFromServer"/> (a pure <c>--connect</c> client, once the server's map name arrives).
    /// No-op without a BSP / render world / assets, so it is safe to call before the map is known.
    /// </summary>
    private void AttachWorldRender(CollisionWorld? sharedEffectsWorld = null)
    {
        if (_bsp is null || _render is null || _assets?.Assets is null)
            return;

        // The client draws the worldmodel it loaded locally (DP VF_DRAWWORLD=1 + renderscene(); the server ships
        // no geometry). Reuses the SAME BSP + gametype submodel filter the collision was built from — render and
        // collision MUST agree (GameDemo.cs:134/181). The map name resolves external lm_NNNN lightmaps.
        _mapRoot = MapLoader.BuildMap(_bsp, _assets.Assets, _map, _droppedSubmodels);
        AddChild(_mapRoot);

        // (§12.8) The render world's PVS so it DP-faithfully culls remote entities behind walls (r_pvs_cull_entities).
        _render.Pvs = new XonoticGodot.Formats.Bsp.BspPvs(_bsp);

        // Client-side collision for the particle systems: decal splats conform to the real brush faces (DP
        // R_DecalSystem — else marks fall back to flat quads). A pure client already built the world for its
        // prediction collision (LoadClientMapFromServer) and passes it in, so we don't build the same brush world a
        // SECOND time on the connect frame — both the prediction trace and these effect traces are main-thread, so
        // sharing one instance is safe. The listen path builds a fresh one (GameWorld owns the server collision).
        CollisionWorld effectsWorld = sharedEffectsWorld ?? MapLoader.BuildCollision(_bsp, _assets.Assets);
        _render.Effects.SetCollisionWorld(effectsWorld);
        // Splats clip against the RENDER triangles (DP's actual target) — marks roll over visible trim/patch edges.
        _render.Effects.SetDecalGeometry(_bsp);

        // Chunked-SDF collision field for modern particles (planning/particles-dual-system.md §A). Built only in a
        // modern-collision mode (cl_particles_modern 1/2) — mode 0 (the faithful default) needs no SDF, so the
        // default connect pays nothing here. The SDF generator queries the world on a background Task, and
        // CollisionWorld.Query mutates a per-instance mark epoch — so it must NOT share the instance the
        // main-thread prediction traces use. When we shared the prediction world into effects above, hand the SDF
        // lane its OWN world (a dedicated build), else reuse the fresh effects world (listen path, as before).
        if (_vfs is not null &&
            XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat(XonoticGodot.Engine.Particles.ParticleCvars.Modern) != 0f)
        {
            string bspVpath = $"maps/{_map}.bsp";
            byte[]? bspBytes = _vfs.Exists(bspVpath) ? _vfs.ReadBytes(bspVpath) : null;
            if (bspBytes is not null)
            {
                CollisionWorld sdfWorld = sharedEffectsWorld is null
                    ? effectsWorld
                    : MapLoader.BuildCollision(_bsp, _assets.Assets);
                _render.Effects.BuildSdfForMap(_map, bspBytes, sdfWorld, _vfs);
            }
        }
    }

    /// <summary>
    /// Read the map's cdtrack from its .mapinfo file (or worldspawn fallback) and instantiate the client-side
    /// <see cref="MusicPlayer"/> that drives background music for the match. The MusicPlayer evaluates the
    /// priority stack (trigger_music > target_music > cdtrack) each frame and crossfades between tracks.
    /// </summary>
    private void SetupMusic()
    {
        // Resolve the cdtrack: first from the mapinfo file, then from worldspawn (already parsed by GameWorld).
        string cdTrack = "";
        if (_vfs is not null && !string.IsNullOrEmpty(_map))
        {
            // Try reading maps/<map>.mapinfo from the VFS (the pk3-mounted search path).
            string mapinfoPath = $"maps/{_map}.mapinfo";
            if (_vfs.Exists(mapinfoPath))
            {
                try
                {
                    string text = _vfs.ReadText(mapinfoPath);
                    cdTrack = ParseMapinfoCdTrack(text);
                }
                catch { /* mapinfo read failed — fall through to worldspawn fallback */ }
            }
        }

        // Fallback: GameWorld may have read "music" or "noise" from the worldspawn entity.
        if (string.IsNullOrEmpty(cdTrack) && _serverWorld is not null && !string.IsNullOrEmpty(_serverWorld.CdTrack))
            cdTrack = _serverWorld.CdTrack;

        // Resolve the raw cdtrack value to a VFS sample path (handles numbers, bare names, full paths).
        string resolvedTrack = MusicPlayer.ResolveMusicPath(cdTrack);

        // Store it on the server world too (so other code can query it).
        if (_serverWorld is not null && !string.IsNullOrEmpty(resolvedTrack))
            _serverWorld.CdTrack = resolvedTrack;

        // Create and configure the music player node.
        _musicPlayer = new MusicPlayer
        {
            Name = "MusicPlayer",
            CdTrack = resolvedTrack,
            AudioLoader = _assets is not null ? _assets.LoadSound : null,
            BgmVolume = _sharedCvars?.GetFloat("bgmvolume") is float bv and > 0f ? bv : 0.7f,
        };

        // On a listen server, give the music player direct access to the entity list so it can scan
        // target_music / trigger_music state each frame without networking.
        if (_serverWorld is not null)
        {
            _musicPlayer.EntityList = _serverWorld.Services.EntityTable.All;
            // The server time is needed for trigger_music touch freshness.
            // Updated each frame in _Process via the music player's ServerTime property.
        }

        AddChild(_musicPlayer);

        if (!string.IsNullOrEmpty(resolvedTrack))
            GD.Print($"[NetGame] map music: '{resolvedTrack}' (from {(string.IsNullOrEmpty(cdTrack) ? "none" : "mapinfo/worldspawn")})");
    }

    /// <summary>Parse the cdtrack line from a .mapinfo file's text content.</summary>
    private static string ParseMapinfoCdTrack(string mapinfoText)
    {
        if (string.IsNullOrEmpty(mapinfoText))
            return "";

        // The mapinfo format is line-based: "cdtrack <value>"
        foreach (string rawLine in mapinfoText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("cdtrack ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("cdtrack\t", StringComparison.OrdinalIgnoreCase))
            {
                string value = line.Substring(8).Trim();
                // Strip quotes if present (QC cvar_value_issafe check — we just take the bare value).
                if (value.Length > 1 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                return value;
            }
        }
        return "";
    }

    private void SetupCameraAndHud()
    {
        // Seed the zoom / eventchase cvars so the shared FirstPersonView reads authentic values even when a
        // particular boot path didn't load the full client cfg tree (Register is idempotent — it never clobbers a
        // loaded value). Mirrors PlayerController._Ready's seeding so the net path zooms identically.
        SeedViewCvars();

        // The shared first-person view (T34 SEAM): the SAME zoom/FOV/death-cam component PlayerController drives.
        _view.EyeHeight = EyeHeight;
        _view.BaseFov = 100f; // Xonotic `fov 100`; the `fov` cvar overrides it live (FirstPersonView reads it).

        // Seed the camera fov from the view's *0.75 frustum (fov 100 → ~83.6° vertical), then UpdateView keeps it
        // live (incl. the zoom). Xonotic `fov` is HORIZONTAL at a 4:3 reference; Godot Camera3D.Fov is VERTICAL.
        float vFov = Client.FirstPersonView.ComputeVerticalFov(_view.BaseFov, 1f);
        _camera = new Camera3D { Name = "Camera3D", Fov = vFov, Near = 1f, Current = true };
        AddChild(_camera);

        // (E3/E5) The editor nodes are constructed in SetupRender, which runs BEFORE this method — so the
        // Attach calls there handed them a null camera, and both silently stood down every frame: the
        // controller early-returns without a camera (no picking, so no hover highlight and no drag) and the
        // ortho view refuses to open. Bind them to the real camera here, where it exists.
        _editor?.Attach(_camera);
        if (_editor is not null && _editorGizmos is not null)
            _editorOrtho?.Attach(_camera, _editor, _editorGizmos);

        // First-person weapon view-model hung off the camera (CSQC viewmodel rendered at the view origin). Built
        // here because SetupRender ran first, so _render.Effects is live (ClientWorld._Ready set it synchronously
        // on AddChild). The weapon model is installed/swapped from the networked active weapon each frame in
        // EquipNetworkedWeapon(); the ViewStateProvider feeds the follow/lean/bob sway from the predicted view.
        _viewModel = new ViewModel { Name = "ViewModel", Effects = _render.Effects };
        _viewModel.ViewStateProvider = BuildViewState;
        // QC W_MuzzleFlash attaches the weapon's m_muzzlemodel (flash.md3 for the Devastator/MineLayer,
        // uziflash.md3 for the Machinegun/Shotgun; MDL_Null = no model flash for every other weapon) to the
        // exterior weapon entity at the shot tag. The port equivalent: a path-keyed loader the ViewModel calls on
        // each Fire() to spawn a fresh flash mesh node at the muzzle socket, gated by the per-weapon MuzzleModelPath
        // set at equip (MuzzleModelFor). LoadModel returns null when the file is missing, degrading to no flash.
        if (_assets is not null)
        {
            // Flash-model factory: for multi-frame MD3 flash files (flash.md3=30 frames, uziflash.md3=14 frames)
            // return a ModelAnimator so SpawnMuzzleFlashModel can drive frame+=2 each 0.05 s tick
            // (QC W_MuzzleFlash_Model_Think). Static/missing models fall back to plain LoadModel.
            _viewModel.FlashModelFactory = path =>
            {
                XonoticGodot.Formats.Md3.Md3Data? md3 = _assets.LoadMd3(path);
                if (md3 is not null && md3.FrameCount > 1)
                    return ModelAnimator.Create(md3);
                return _assets.LoadModel(path);
            };
        }
        _camera.AddChild(_viewModel);
        _render.ViewModel = _viewModel;

        // Porto_Draw aim-trajectory preview: feed it the local player's view angles, active weapon id, and the
        // alive/spectating/intermission gate (QC Porto_Draw early-return). The preview node itself reads the
        // camera position for the eye + draws only when a porto is held in the non-default combined-shot mode.
        if (_render.PortoPreview is not null)
        {
            _render.PortoPreview.ViewAnglesProvider = () => _viewAngles;
            _render.PortoPreview.ActiveWeaponProvider = () => _client?.ActiveWeaponId ?? -1;
            _render.PortoPreview.SuppressedProvider = () =>
                _client is not { Accepted: true } || _client.Health <= 0
                || _view.ChaseActive || _client.MatchIntermission;
        }

        // Bridge the HUD's texture cache to the mounted game data so weapon icons / crosshairs / kill-notify
        // icons draw the REAL Xonotic art instead of colored-box fallbacks (mirrors GameDemo.SetupHud).
        if (_assets is not null)
        {
            XonoticGodot.Game.Hud.TextureCache.VfsResolver = _assets.LoadTexture;
            // Xolonium HUD font (the menu skin font), so HUD text matches Xonotic instead of Godot's fallback.
            XonoticGodot.Game.Hud.HudPanel.HudFont = _assets.GetFont("xolonium");
            XonoticGodot.Game.Hud.HudSkin.BoldFont = _assets.GetFont("xolonium-bold");
        }

        // The full CSQC HUD panel set on the net path (T34): weapon bar / ammo / kill-feed / centerprint / timer,
        // the SAME Hud GameDemo uses. It is its OWN CanvasLayer (Hud : CanvasLayer), added directly to the node
        // like GameDemo does — on a layer BELOW the lightweight hudLayer so the crosshair/radar/scoreboard draw on
        // top. Its gameplay panels read a local Player; on a listen server that is the local server Player
        // (resolved each frame in _Process as the spawn lands). Its OWN Scoreboard panel is left unfed — T9's
        // standalone _scoreboard owns the networked scoreboard, so we don't double it up.
        _fullHud = new XonoticGodot.Game.Hud.Hud { Name = "FullHud", Layer = 4 };
        AddChild(_fullHud);
        // The full HUD's skinned Crosshair + HealthArmor panels now render on the play path (goal 1). On a listen
        // server (--host) UpdateFullHudPlayer feeds them the local server Player so they show the skinned art and
        // suppresses NetHud's duplicate crosshair/health (NetHud.SuppressCrosshairAndHealth). On a pure --connect
        // client (no local Player) UpdateFullHudPlayer hides the skinned Crosshair (it would otherwise draw a
        // reticle even without a Player) and leaves NetHud un-suppressed as the fallback — so there's never a DOUBLE
        // crosshair/health. HealthArmorPanel self-blanks with no Player (reads its resources), so it's left visible.
        // Crosshair visibility is driven by UpdateFullHudPlayer (shown only when a local Player resolves). Start it
        // HIDDEN so the pure-client baseline (no Player ever resolves → UpdateFullHudPlayer's null==null early-out
        // never runs its body) leaves NetHud's crosshair as the sole reticle. The listen-server null→Player edge
        // then shows it.
        _fullHud.Crosshair.Visible = false;
        //
        // The full HUD's Timer panel IS now fed: the server pushes the global match clock (NetControl.MatchState →
        // ClientNet.HasMatchState/MatchStartTime/MatchTimeLimit/MatchWarmup), drained into the panel each frame by
        // UpdateMatchClock(). It self-blanks (TimerPanel draws nothing until fed) until the first MatchState lands.

        // Ping readout (next to the FPS counter): feed it the live ENet round-trip estimate to the server. The
        // panel self-gates on cl_showping/showping and stays hidden until the cvar is set; the provider returns
        // -1 before the link is up (the panel then draws nothing). On a loopback listen server this reads ~0.
        _fullHud.Ping.PingProvider = () => _client?.PingMs ?? -1;

        // Position readout (stacked above the FPS/ping counters): feed it the local player's predicted Quake-space
        // origin — the same X Y Z space map entities use. The panel self-gates on cl_showposition/showposition
        // (debug-default-on, like FPS) and returns null until we have a live local body (handshake assigns a
        // non-zero LocalNetId), so it draws nothing in the menu / pre-spawn.
        // Both readouts come from the ACTIVE RENDERING CAMERA — the same transform the frame was drawn with.
        //
        // Anything else is a near-miss that looks authoritative. Reporting the player origin is an eye height
        // out; reporting _viewAngles is the INPUT state, which a pinned camera (--observe) or any scripted
        // override does not drive at all — so a screenshot could read "yaw 135 pitch 0" while the picture was
        // taken at yaw 128 pitch -58. A view readout whose only job is to reproduce a view has to be measured
        // from the thing that produced it.
        _fullHud.Position.PositionProvider = () => ActiveViewCamera() is { } cam
            ? Coords.ToQuake(cam.GlobalTransform.Origin)
            : _client is { LocalNetId: not 0 } c ? c.PredictedOrigin : (NVec3?)null;
        _fullHud.Position.AnglesProvider = () => ActiveViewCamera() is { } cam
            ? CameraQuakeAngles(cam)
            : (NVec3?)_viewAngles;

        // [T51] Floating damage-number layer (QC cl_damagetext). Full-rect overlay; fed each frame in _Process
        // from the server-side DamagetextMutator's drained events, projected via the first-person _camera.
        _damageText = new XonoticGodot.Game.Client.DamageTextLayer { Name = "DamageText" };
        _damageText.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _fullHud.AddChild(_damageText);

        // Hit/typehit/kill feedback sounds (QC HitSound): non-positional, SFX bus. misc/hit is the pitched
        // damage-confirm beep (cl_hitsound modes 0-3); misc/typehit the team/chat-hit dink; misc/kill the
        // kill confirm (which the server flush prioritizes over the beep). Fed per frame from the owner's
        // feedback stats in UpdateCrosshairFeedback.
        _hitSound = new XonoticGodot.Game.Client.HitSound(_sharedCvars);
        if (_assets is not null)
            _hitSound.AudioLoader = _assets.LoadSound;
        _hitSound.Attach(_fullHud);

        // In-vehicle low-health/shield alarm (QC vehicle_alarm, cl_vehicles.qc): feed the VehicleHud the VFS sound
        // loader so it can play SND_VEH_ALARM / SND_VEH_ALARM_SHIELD (gated by cl_vehicles_alarm, default 0).
        if (_assets is not null)
            _fullHud.Vehicle.AudioLoader = _assets.LoadSound;

        // The lightweight crosshair + health/armor readout + radar + networked scoreboard, on a layer ABOVE the
        // full HUD so the crosshair sits on top.
        var hudLayer = new CanvasLayer { Name = "Hud", Layer = 5 };
        AddChild(hudLayer);

        // The crosshair + health/armor readout. Kept because it needs no local Player (it reads the networked
        // ClientNet stats directly), so the crosshair + health show even on a pure --connect client where the full
        // HUD's player-bound panels have no actor.
        _hud = new NetHud { Name = "NetHud", Net = _client };
        _hud.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hudLayer.AddChild(_hud);

        // Notifications (QC Local_Notification): centerprint + kill-feed + announcer voice on the net path, routed
        // into the full HUD's panels. Previously only MSG_ANNCE was handled (via a hidden host Hud); now the full
        // HUD lets HudNotifications render center/info text too. OnNotificationReceived forwards EVERY type.
        _notifications = new XonoticGodot.Game.Hud.HudNotifications(_fullHud);
        // QC client/announcer.qh autocvar_cl_announcer (voice pack dir, "default") + autocvar_cl_announcer_antispam
        // (2s same-sound dedup window). Read from the client cvar store so the user setting actually takes effect
        // (HudNotifications consumes AnnouncerVoice in the sound/announcer/<voice>/ resolve and AntiSpamInterval in
        // the QueueRun dedup). Defaults match xonotic-client.cfg:357-358 when the cvars are unset.
        // Then run the resolved voice through MUTATOR_CALLHOOK(AnnouncerOption) (QC AnnouncerFilename -> AnnouncerOption):
        // a mutator MAY rewrite the voice pack via this hook. No stock Base mutator registers it (overkill included),
        // so this is a no-op pass-through out of the box, exactly like Base.
        if (_sharedCvars is not null)
        {
            string voice = _sharedCvars.GetString("cl_announcer");
            if (!string.IsNullOrWhiteSpace(voice))
                _notifications.AnnouncerVoice = voice;
            // Fire the AnnouncerOption hook to allow mutators to override the voice (QC announcer.qc:14-20)
            _notifications.AnnouncerVoice = MutatorHooks.FireAnnouncerOption(_notifications.AnnouncerVoice);
            if (_sharedCvars.Has("cl_announcer_antispam"))
                _notifications.AntiSpamInterval = _sharedCvars.GetFloat("cl_announcer_antispam");
        }
        if (_assets is not null)
        {
            _notifications.AudioLoader = _assets.LoadSound; // sound/announcer/<voice>/<snd>.ogg from the mounted VFS
            string fallbackVoice = _notifications.AnnouncerVoice;
            _notifications.AnnouncerResolver = name =>
                string.IsNullOrWhiteSpace(name) ? null : $"res://sound/announcer/{fallbackVoice}/{name}.ogg";
        }
        if (_client is not null)
            _client.NotificationReceived += OnNotificationReceived;

        // Minigames (QC cl_minigames.qc): the client-side coordinator drives the minigame board overlay + the
        // in-game menu off the S2C session snapshot and forwards board clicks / menu actions back as
        // `cmd minigame …` lines (the QC minigame_cmd localcmd). All wiring is in-process — the menu/renderer
        // live on _fullHud (the same Hud GameDemo uses), and the command + snapshot ride the existing
        // ClientCommand / MinigameState net channels.
        if (_client is not null)
        {
            _minigame = new MinigameClient(_fullHud, _fullHud.MinigameMenu, SendStringCommand);
            _client.MinigameStateReceived += OnMinigameStateReceived;
            // The list-sessions reply prints as ServerPrint lines; feed them to the Join menu's session list.
            _client.PrintReceived += OnServerPrintForMinigame;
            // Pong's paddle drive (QC pong_hud_board: minigame_cmd("move "+bits) on an arrow-key change). The
            // line is the SERVER command ("minigame move <bits>") — SendStringCommand IS the clc_stringcmd
            // forward channel, so it carries no extra "cmd " prefix (that's the DP local-console convention).
            _fullHud.PongMoveSink = bits => SendStringCommand($"minigame move {bits}");

            // Chat (QC HUD panel #12): the port has no dedicated chat net channel — server chat is broadcast via
            // ServerNet.BroadcastPrint and arrives on the SAME PrintReceived stream the minigame session-list
            // parser reads. Forward every print line to the ChatPanel's scrollback (it self-blanks until fed); the
            // panel honours con_chattime/con_chatsize and fades lines out. (Console output is also relayed to the
            // in-game console via ConsolePrint elsewhere, so chat shows in both — matching DP, where chat lands in
            // the console AND the chat HUD area.)
            _client.PrintReceived += OnServerPrintForChat;
        }

        // QuickMenu (QC HUD panel #QUICKMENU): point its command sink at the shared interpreter (RunCommand, wired
        // by Shell) — the analogue of QC localcmd, the SAME single channel a bound key uses (RunBoundCommand). A
        // picked toggle row (`toggle cl_hitsound`) hits the shared cvar store; a say/say_team/vcall row is forwarded
        // to the server by the interpreter's own forward path, exactly like typing it in the console. The panel is
        // toggled by the `quickmenu` bind (intercepted in RunBoundCommand → Toggle()); it self-blanks until opened.
        // It grabs keyboard focus on open so the 1-9/0/Esc number-key navigation works; mouse-click navigation
        // needs the cursor made visible (the gameplay path keeps it captured) — see the report's goal-8 note.
        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.QuickMenuPanel>() is { } quick)
            quick.CommandSink = line => RunCommand?.Invoke(line);

        _radar = new RadarPanel
        {
            Name = "Radar",
            Net = _client,
            Size = new Vector2(256, 256),
            Position = new Vector2(24, 24),
        };
        // Maximized-radar wiring (QC HUD_Radar / +hud_panel_radar_maximized): the click-to-spawn channel is the same
        // clc_stringcmd path a console command takes — emit `cmd ons_spawn <x> <y> <z>` (Onslaught.HandleOnsSpawnCommand
        // parses argv 1..3 as world x/y/z). IsOnslaught gates the clickable spawn-point picking to Onslaught; it tracks
        // the SAME gametype string the HUD/scoreboard read (the networked ScoreInfo block sets GameScores.Gametype,
        // "ons" for Onslaught). Refreshed per-frame in _Process so a mid-match gametype change re-gates it.
        _radar.SendServerCommand = line => _client.SendStringCommand(line);
        _radar.IsOnslaught = XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype == "ons";
        // Feed the real minimap: the map name (resolves gfx/<map>_mini.jpg inside the map pk3 via the VFS) + the
        // map's world XY bounds (QC mi_min/mi_max = the BSP worldspawn model mins/maxs) so the image + blips align.
        _radar.MapName = _map;
        if (_bsp is { Models.Length: > 0 })
        {
            System.Numerics.Vector3 mins = _bsp.Models[0].Mins, maxs = _bsp.Models[0].Maxs;
            _radar.MapMinXY = new Vector2(mins.X, mins.Y);
            _radar.MapMaxXY = new Vector2(maxs.X, maxs.Y);
        }
        hudLayer.AddChild(_radar);

        // 3D in-world waypoint sprites (QC Draw_WaypointSprite): floating objective/ping markers projected through
        // the first-person camera, on their OWN layer BELOW the HUD panels (Layer 3 < the HUD's 4/5) so the
        // crosshair + panels draw over them. Fed the live waypoint list straight from ClientNet.
        var waypointLayer = new CanvasLayer { Name = "Waypoints", Layer = 3 };
        AddChild(waypointLayer);
        _waypointLayer = new XonoticGodot.Game.Client.WaypointSpriteLayer { Name = "WaypointSprites", Camera = _camera };
        _waypointLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        if (_bsp is { Models.Length: > 0 })
        {
            // QC WaypointSprite_Load: waypointsprite_fadedistance = vlen(mi_scale) where mi_scale = mi_max - mi_min
            // (the BSP worldspawn model extent). Drives the distance-fade ramp distancefadedistance.
            System.Numerics.Vector3 mn = _bsp.Models[0].Mins, mx = _bsp.Models[0].Maxs;
            _waypointLayer.MapSize = new System.Numerics.Vector3(mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z).Length();
        }
        if (_client is not null)
            _waypointLayer.Source = () => _client.Waypoints;
        waypointLayer.AddChild(_waypointLayer);

        // Warpzone PORTAL render (the C# stand-in for DP's engine r_water portal pass): turn the warpzone "window"
        // meshes MapLoader extracted into live SubViewport renders of the linked exit. Listen-host only (reads the
        // shared WarpzoneTrace.AmbientManager zone transforms); a pure remote client / a map with no warpzones / a
        // surface that matches no zone all fall back to the dark-mirror placeholder. Gated by cl_portal_render.
        if (_mapRoot is not null)
        {
            _portalRenderer = new XonoticGodot.Game.Client.PortalRenderer { Name = "PortalRenderer" };
            AddChild(_portalRenderer);
            _portalRenderer.Setup(_mapRoot, _camera);

            // Warpzone portal DISC (the cosmetic skinned-model ring DP draws on a warpzone face, separate from the
            // see-through SubViewport portal above). Client-only — it reads the SAME un-networked
            // WarpzoneTrace.AmbientManager zone transforms PortalRenderer/WarpzoneFixView use, so it is LIVE on a
            // listen host. The node self-drives via its own _Process once in the tree (same as _portalRenderer);
            // we only construct + wire its skinned-model factory here. AssetLoader.LoadModel already supports the
            // `_N.skin` variant via its (vpath, skinIndex=0) overload; tolerate a null _assets the same way the
            // ProjectileRenderer.ModelFactory wiring (above) does.
            _portalDiscRenderer = new XonoticGodot.Game.Client.PortalDiscRenderer { Name = "PortalDiscRenderer" };
            AddChild(_portalDiscRenderer);
            _portalDiscRenderer.Setup(_camera, (path, skin) => _assets?.LoadModel(path, skin));
        }

        // [T68] Floating player name + health/armor tags (QC client/shownames.qc Draw_ShowNames_All): a 3D
        // in-world overlay projected through the first-person camera, on the SAME low layer as the waypoint
        // sprites (below the HUD panels). Fed each frame in _Process from ClientNet's remote player slice + the
        // scoreboard name slice. The display NAME comes from the networked scoreboard rows (the port's faithful
        // entcs_GetName stand-in — there is no separate entcs name stream).
        _shownamesLayer = new XonoticGodot.Game.Client.ShowNamesLayer { Name = "ShowNames", Camera = _camera, Net = _client };
        _shownamesLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _shownamesLayer.NameResolver = ResolveScoreboardName;
        waypointLayer.AddChild(_shownamesLayer);

        // Networked scoreboard (QC HUD panel #25 + the +showscores toggle). The real play path is NetGame, whose
        // NetHud has no scoreboard — so the per-player columns + team totals that ClientNet decodes
        // (LatestScoreboard) had nowhere to go. Add the panel here, sized to a centered slab, hidden until the
        // scoreboard key is held; _Process feeds it from LatestScoreboard each frame the data changes.
        // MouseFilter.Ignore: this panel is added straight to hudLayer, bypassing HudManager.RegisterPanel (which
        // sets Ignore) — without it the Control defaults to Stop and its (centered, screen-sized) rect EATS the
        // captured mouse-look motion before it reaches _UnhandledInput, so the scoreboard "steals the mouse" while
        // it's up (QC hud_cursormode off — the HUD never eats input).
        _scoreboard = new XonoticGodot.Game.Hud.ScoreboardPanel
        {
            Name = "Scoreboard", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hudLayer.AddChild(_scoreboard);
        // QC the interactive scoreboard's `localcmd(...)` sink (Scoreboard_InputEvent): join/spectate/tell/vcall
        // are CLIENT commands the server must run, so a "cmd " prefix routes to the server the same way a typed
        // `cmd join red` does; everything else (toggle, scoreboard_columns_set, commandmode) is a local console
        // verb. On a listen host the client path still reaches the in-process server through the same seam.
        _scoreboard.CommandSink = line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("cmd ", System.StringComparison.Ordinal))
            {
                string verb = line[4..];
                if (_serverWorld is not null) ExecuteHostConsoleCommand(verb);
                else SendStringCommand(verb);
                return;
            }
            MenuState.Interp?.ExecuteLine(line);
        };
        // DP commandmode: Shell owns the prompt; the direct hook preserves the prefill's quoting verbatim.
        _scoreboard.OpenCommandPrompt = prefill => XonoticGodot.Game.Menu.MenuCommand.OpenCommandPrompt?.Invoke(prefill);
        // Escape is owned by Shell's in-match chain (it runs in _UnhandledKeyInput, ahead of our _UnhandledInput,
        // and marks both edges handled) — claim it from there so TAB+Escape can open the UI and Escape close it.
        XonoticGodot.Game.Menu.MenuCommand.ScoreboardEscape = () =>
            _scoreboard is not null && GodotObject.IsInstanceValid(_scoreboard) && _scoreboard.HandleEscape();
        LayoutScoreboard();

        // Screen-effects layer (damage red-flash + liquid tint) — the SAME reusable ViewEffects node T4 built for
        // the local match, so the networked "play from menu" path also reacts to damage + underwater. It is its
        // own CanvasLayer (below the HUD), fed each frame in _Process from the networked stats + predicted eye.
        _viewEffects = new ViewEffects { Name = "ViewEffects" };
        AddChild(_viewEffects);

        // Nade orb effect models (heal/ammo/entrap/veil/darkness orbs — the static field entities a nade spawns).
        // Built next to the ProjectileRenderer and fed off the SAME entity stream: ClientEntityView routes a
        // NetEntityKind.NadeOrb spawn/update/remove to _render.NadeOrbs, so the renderer NetGame creates here is
        // also the one ClientWorld exposes — one shared instance. Its ModelFactory mirrors
        // ProjectileRenderer.ModelFactory (AssetLoader.LoadModel, null-tolerant). Held in _orbRenderer so the
        // per-frame view-effects feed can read ActiveOrbs() for the in-orb color flash. The node self-advances
        // via its own _Process once in the tree (same as the ProjectileRenderer).
        _orbRenderer = new XonoticGodot.Game.Client.NadeOrbRenderer { Name = "NadeOrbRenderer" };
        AddChild(_orbRenderer);
        if (_assets is not null)
            _orbRenderer.ModelFactory = m => _assets.LoadModel(m);
        if (_render is not null)
            _render.NadeOrbs = _orbRenderer;

        // Screen-space vignette (cl_vignette_*): a soft darkened gradient framing the view edges, on its own
        // CanvasLayer above the world/ViewEffects tint but below the HUD. Self-contained — it registers its own
        // cvars, reads them live, and self-drives; no per-frame feeding needed here.
        AddChild(new XonoticGodot.Game.Client.VignetteOverlay { Name = "Vignette" });

        // Zoom scope reticle (QC crosshair.qc DrawReticle): a CanvasLayer below the HUD, fed each frame in
        // _Process from the networked active weapon + zoom state (see the zoom block there).
        _reticle = new XonoticGodot.Game.Client.ReticleOverlay { Name = "Reticle" };
        AddChild(_reticle);

        // The loading screen (Shell's CanvasLayer 100) covers the viewport during the handshake and the
        // connect-as-observer window — the DP gfx/loading.tga + progress bar, replacing the old plain black
        // overlay. It is owned by Shell (above this node) and dismissed via DismissLoadingScreen once the camera
        // is seeded AND the local player is live (health > 0). If no loading screen was set (bare CLI), create
        // a minimal black fallback so the from-origin flash is still hidden.
        if (LoadingScreen is null)
        {
            var fallback = new CanvasLayer { Name = "ConnectOverlay", Layer = 100 };
            var cover = new ColorRect { Name = "Cover", Color = Colors.Black };
            cover.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            fallback.AddChild(cover);
            AddChild(fallback);
            _fallbackOverlay = fallback;
        }
    }
    private CanvasLayer? _fallbackOverlay;

    /// <summary>The view state the first-person <see cref="ViewModel"/> sway reads (QC viewmodel_animate inputs):
    /// the predicted local velocity + the live view angles + the predicted onground flag.</summary>
    private ViewModel.ViewState BuildViewState() => new()
    {
        VelocityQuake = _client?.PredictedVelocity ?? default,
        ViewAnglesQuake = _viewAngles,
        OnGround = _carrier?.OnGround ?? false,
    };

    /// <summary>Seed the zoom / spawn-zoom / eventchase cvars (xonotic-client.cfg defaults) so the shared
    /// <see cref="Client.FirstPersonView"/> reads authentic values even when a boot path didn't load the full
    /// client cfg tree (a bare CLI host / pure client). Register is idempotent — it never clobbers a value the
    /// loaded cfg/menu store already set. Mirrors <c>PlayerController._Ready</c>'s seeding.</summary>
    private static void SeedViewCvars()
    {
        if (Api.Services is null)
            return;
        Api.Cvars.Register("cl_zoomfactor", "5");      // xonotic-client.cfg:59
        Api.Cvars.Register("cl_zoomspeed", "8");       // xonotic-client.cfg:60
        Api.Cvars.Register("cl_zoomsensitivity", "0");
        Api.Cvars.Register("cl_spawnzoom", "1");       // xonotic-client.cfg:56-58 (spawn-zoom default ON)
        Api.Cvars.Register("cl_spawnzoom_speed", "1");
        Api.Cvars.Register("cl_spawnzoom_factor", "2");
        Api.Cvars.Register("fov", "100");
        Api.Cvars.Register("cl_eventchase_death", "2");
        Api.Cvars.Register("cl_eventchase_distance", "140");
        Api.Cvars.Register("cl_eventchase_speed", "1.3");

        // Port benchmark/demo knob (NOT a Base cvar; never archived): 1 = on a listen host, stay an observer
        // and follow a living bot (BenchSpectateThink) so a capture camera experiences real gameplay. The
        // perf-run demo scenario sets it together with g_weaponarena/g_forced_respawn/bot_ai_weapon_rotate.
        Api.Cvars.Register("cl_bench_spectate", "0");
        // cl_motion_trace (r16 rubberband diagnostic): per-frame CSV of the motion-timeline signals — render
        // clock error/slew, server ticks per frame, camera/predicted/remote-entity rendered speeds, prediction
        // error magnitude — so a felt motion wobble can be attributed to its actual carrier. Port-only debug.
        Api.Cvars.Register("cl_motion_trace", "0");
        // Spectator follow-cam angle smoothing half-life in seconds (port quality knob, NOT a Base cvar —
        // Base renders the spectatee's raw stepped angles). 0 = raw. See SmoothSpectateAngles.
        Api.Cvars.Register("cl_spectate_smoothangles", "0.05");
        // QC Scoreboard_WouldDraw death-scoreboard gate (_cl_main.qc / scoreboard.qc:1793): force the scoreboard up
        // a short delay after the local player dies, even without holding +showscores.
        Api.Cvars.Register("cl_deathscoreboard", "1");
        Api.Cvars.Register("cl_deathscoreboard_delay", "1");
    }

    /// <summary>
    /// Install or swap the first-person weapon model to match the networked active weapon — QC view.qc:305-332
    /// picks the viewmodel's model from the active weapon's v_ model and rebuilds only on a weapon change. The
    /// owner's active weapon arrives in the snapshot owner block (<see cref="ClientNet.ActiveWeaponId"/>).
    /// Holstered / dead (id &lt; 0 or health &le; 0) hides the gun, mirroring view.qc masking it when dead.
    /// </summary>
    /// <summary>
    /// Seconds left of the OUTGOING weapon's <c>switchdelay_drop</c> during a staged first-person weapon swap
    /// (the holster slide window <see cref="EquipNetworkedWeapon"/> holds the old model through). Negative = no
    /// swap staged. Also read by the fire-feedback gate: the server slot is in the Drop state for this window,
    /// so predicted muzzle flash/sound must not fire through it.
    /// </summary>
    private float _switchDropLeft = -1f;

    /// <summary>
    /// Seconds left of the INCOMING weapon's <c>switchdelay_raise</c> after a first-person weapon install
    /// (set beside <c>PlayRaise</c>). The server slot is in the Raise state (can't fire) for this window, so the
    /// fire-feedback gate holds prediction through it — else a held trigger through a switch predicted the flash
    /// up to the raise time EARLY. Decays in <see cref="UpdateLocalFireFeedback"/>; negative = idle.
    /// </summary>
    private float _switchRaiseLeft = -1f;

    /// <summary>
    /// Client mirror of QC <c>wepent.rl_release</c> (armed on release/equip, cleared on each predicted shot) —
    /// the release-gated-weapon (Devastator) fire condition. See <see cref="UpdateLocalFireFeedback"/>.
    /// </summary>
    private bool _rlReleaseMirror = true;
    private int _rlMirrorWeaponId = -1;

    private void EquipNetworkedWeapon()
    {
        if (_viewModel is null || !GodotObject.IsInstanceValid(_viewModel) || _client is not { Accepted: true })
            return;

        int id = _client.ActiveWeaponId;
        // Hide the gun when holstered (id<0), dead (Health<=0), the event/death chase camera is active — QC
        // view.qc viewmodel_draw masks the viewmodel when STAT(HEALTH)<=0 || chase_active (the shared view tracks
        // ChaseActive after UpdateCamera ran this frame), so the third-person death-cam doesn't show a floating gun.
        // ALSO hidden at intermission: QC FixIntermissionClient sets each weapon entity's effects = EF_NODRAW so the
        // viewmodel disappears once the match ends and the scoreboard takes over.
        bool hidden = id < 0 || id >= XonoticGodot.Common.Gameplay.Weapons.Count
            || _client.Health <= 0 || _view.ChaseActive || _client.MatchIntermission;

        // [#43] Push the local profile color (_cl_color) to the server: listen host applies it directly
        // (QC SV_ChangeTeam — FFA-only recolor), a pure client sends the `color` command. Change-gated.
        PushLocalPlayerColor();

        // Player colormap + per-weapon glowmod (Base viewmodel_draw 302-321): tint the held gun to the local
        // player's packed shirt/pants colors — the FFA profile color, or the team color in teamplay (the server
        // forces clientcolors there) — and glow with the pants palette / wr_glow. Applied every frame (cheap —
        // SetPlayerColors early-returns when the color is unchanged) so a mid-match team swap / recolor / charge
        // change repaints.
        UpdateViewModelColors();

        // Reload animation (Base wframe WFRAME_RELOAD → anim_reload). The port does not network the wframe temp
        // entity, but the reload state IS observable from the already-networked clip counter: clip_load == -1 is the
        // QC "scheduled for reload" sentinel (set for the duration of the reload think). Play the viewmodel reload
        // clip once on the rising edge so the gun visibly cycles through a reload, dropping back to idle when done.
        UpdateViewModelReloadAnim();

        // Networked viewmodel anim-frame (Base wframe NET_HANDLE selector): on a listen host the reload anim above is
        // already host-derived, but a pure client / a spectatee has no LocalServerPlayer slot — so drive the local
        // viewmodel's fire/raise/drop/reload clip from the networked WepentView.ViewmodelFrame selector instead.
        UpdateViewModelAnimFromNet();

        // (#41) Lightgrid model light: DP lights every model from the BSP lightgrid at the entity origin
        // (Mod_Q3BSP_LightPoint) — the port lit everything with one global sun + flat ambient, so guns read
        // dull/gray indoors. Sample the grid at the camera and modulate the viewmodel's skin-shader tint.
        UpdateViewModelLightgrid();

        // Per-weapon wr_viewmodel override (Base viewmodel_draw 324-327: newname = wep.wr_viewmodel(wep, this)).
        // Only Tuba overrides it, swapping its v_ model by the currently played instrument (tuba/akordeon/
        // kleinbottle). Resolved live so a reload that cycles the instrument rebuilds the gun model. Empty = no
        // override (every other weapon uses its static WorldModel).
        string vmOverride = WeaponViewModelOverride(id);

        if (id == _equippedWeaponId && vmOverride == _equippedVmOverride)
        {
            _switchDropLeft = -1f;        // a staged switch back to the current weapon dissolves
            _viewModel.Visible = !hidden; // no weapon/model change; just track the dead/holstered visibility edge
            return;
        }

        // Weapon CHANGE while a gun is up: hold the OLD model on screen for the outgoing weapon's
        // switchdelay_drop while the holster slide plays, and only then install the new model + raise — QC
        // weapon_thinkf(WFRAME_DONTCHANGE, oldwep.switchdelay_drop, w_clear) installs the new weapon at w_clear.
        // The port's Inventory.SwitchWeapon mirrors ActiveWeaponId IMMEDIATELY (the fire driver alone carries the
        // drop/raise timing), so without this hold the swap looked instantaneous — worst on the out-of-ammo
        // auto-switch, which has no keypress holster to mask it. Re-targeting mid-drop (rapid re-switch) keeps
        // the running drop clock, like QC re-entering w_clear with a new m_switchweapon. First equip
        // (_equippedWeaponId < 0) and the hidden/dead path stay immediate.
        // (id check: a same-weapon wr_viewmodel override change — the Tuba instrument swap — rebuilds immediately.)
        if (!hidden && _equippedWeaponId >= 0 && id >= 0 && id != _equippedWeaponId)
        {
            if (_switchDropLeft < 0f)
            {
                XonoticGodot.Common.Gameplay.Weapon outgoing = XonoticGodot.Common.Gameplay.Weapons.ById(_equippedWeaponId);
                _switchDropLeft = outgoing?.SwitchDelayDrop() ?? 0f;
                _viewModel.PlayHolster(); // no-op refresh if the manual keypress already started the slide

                // Out-of-ammo FORCED switch with the trigger still down: Base (cl_unpress_attack_on_weapon_switch
                // 0 default) lets the switched-to weapon open fire the moment it raises — surprising, since the
                // player never chose it. Release the attack buttons (DP -fire/-fire2, exactly what Base's opt-in
                // cvar localcmds) until a physical re-press — but ONLY when the outgoing weapon is genuinely dry
                // (the forced-switch signature; ClientHasWeapon(andAmmo) is the server's own W_SwitchToOtherWeapon
                // dry test, so the Devastator's ammo>=4 floor counts as dry). A held trigger through a MANUAL
                // switch (old weapon still usable) keeps firing, and a non-dry forced cycle (NIX) is untouched.
                if ((BindTable.AttackHeld || BindTable.Attack2Held) && outgoing is not null
                    && MenuState.Cvars.GetString("cl_unpress_attack_on_empty_switch") != "0"
                    && (LocalServerPlayer ?? _hudMirror) is { } fireActor
                    && !XonoticGodot.Common.Gameplay.Inventory.ClientHasWeapon(fireActor, outgoing, andAmmo: true, complain: false))
                {
                    BindTable.ReleaseAttack();
                    _attackLatch = _attack2Latch = false; // pending sub-tick taps die with the release
                }
            }
            _switchDropLeft -= (float)GetProcessDeltaTime();
            if (_switchDropLeft > 0f)
                return; // old model stays up while it lowers
        }
        _switchDropLeft = -1f;
        _equippedWeaponId = id;
        _equippedVmOverride = vmOverride;

        if (hidden)
        {
            _viewModel.Visible = false;
            return;
        }

        // Map the active weapon → its first-person v_ model: W_Model = "models/weapons/" + v_*.md3 (all.qc:233/367).
        // Weapon.WorldModel is the bare "v_laser.md3", so prefix the directory; a missing model → placeholder bar.
        // Base-faithful selection (CL_WeaponEntity_SetModel): full-model DPM rigs (rl/gl/crylink/electro/hagar/
        // ok_*) render the h_ HAND RIG itself; invisible-hand IQM rigs render the v_ model attached to the rig's
        // "weapon" bone. ViewModelEquip.Build is the single source of truth for first-person weapon construction.
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(id);
        // wr_viewmodel override replaces the model file (vmOverride = "v_<instrument>.md3"); else the static WorldModel.
        string vModel = string.IsNullOrEmpty(vmOverride) ? WeaponVModelPath(w) : "models/weapons/" + vmOverride;
        ViewModelEquip eq = ViewModelEquip.Build(_assets, vModel);
        _viewModel.SetWeaponModel(eq.Model, MuzzleEffectFor(w), "tag_shot", eq.Attach, MuzzleModelFor(w));
        // QC wepent.movedir (CL_WeaponEntity_SetModel): the weapon's registered model-local tag_shot offset —
        // the SAME value the server's SetupShot fires from — so the first-person flash spawns at the real
        // muzzle point (Base movedir_aligned), not wherever the compressed-view-space render tag lands.
        System.Numerics.Vector3 mdv = XonoticGodot.Common.Gameplay.WeaponFiring.TryGetMuzzleOffset(id, out System.Numerics.Vector3 mo)
            ? mo : XonoticGodot.Common.Gameplay.WeaponFiring.DefaultMuzzleOffset;
        _viewModel.MuzzleMovedir = new Vector3(mdv.X, mdv.Y, mdv.Z);
        _viewModel.Visible = true;
        // Raise the new gun into view instead of popping the model in (Xonotic viewmodel_draw raise; pairs with
        // the keypress holster in RunBoundCommand). Confirmed switch → cancels any pending holster auto-recovery.
        _viewModel.PlayRaise();
        // The server slot now runs the incoming weapon's raise think (can't fire until it lands) — hold fire
        // prediction for the same window so a held trigger doesn't pop the flash while the gun is still rising.
        _switchRaiseLeft = w?.SwitchDelayRaise() ?? 0f;
    }

    /// <summary>
    /// Resolve + push the held view-model's team colormap tint + glowmod (Base <c>viewmodel_draw</c> 302-321 →
    /// <c>weaponentity_glowmod</c>, all.qh:402). The glow is the per-weapon <c>wr_glow</c> override (Vortex /
    /// Overkill-Nex charge glow) when the weapon supplies one, else the pants palette color
    /// (<c>colormapPaletteColor(c &amp; 0x0F, true)</c>). [#43] The colors are the watched player's packed
    /// <c>clientcolors</c> — Base <c>entcs_GetClientColors(current_player)</c>: the FFA profile color, or the
    /// team color in teamplay (the server forces clientcolors to <c>17*teamcode</c> there, so team display falls
    /// out with no special case). Colorless (never networked / no player) leaves the gun untinted with its
    /// native glow. Cheap to call every frame — <see cref="ViewModel.SetPlayerColors"/> early-returns when
    /// nothing changed.
    /// </summary>
    private void UpdateViewModelColors()
    {
        if (_viewModel is null || !GodotObject.IsInstanceValid(_viewModel))
            return;

        int colors = LocalViewedClientColors();
        if (colors == 0)
        {
            _viewModel.SetPlayerColors(XonoticGodot.Game.Client.ModelTint.White,
                XonoticGodot.Game.Client.ModelTint.Black, XonoticGodot.Game.Client.ModelTint.Black, false);
            return;
        }

        float paletteTime = XonoticGodot.Common.Services.Api.Services?.Clock?.Time ?? 0f;
        (float sr, float sg, float sb) = XonoticGodot.Engine.Simulation.CsqcModelAppearance.ColormapPaletteColor(
            (colors >> 4) & 0x0F, isPants: false, paletteTime);
        (float pr, float pg, float pb) = XonoticGodot.Engine.Simulation.CsqcModelAppearance.ColormapPaletteColor(
            colors & 0x0F, isPants: true, paletteTime);
        var shirt = new Color(sr, sg, sb);
        var pants = new Color(pr, pg, pb);

        // Per-weapon wr_glow override: Vortex (and Overkill-Nex) glow with the charge level, fading from the
        // player's colors (vortex_glowcolor: f*colors*0.3 below animlimit, +f*colors*0.7 above). Default weapons
        // return '0 0 0' → fall back to the pants palette color (weaponentity_glowmod's `if (!g) g = palette`).
        Color glow = pants;
        int wid = _client?.ActiveWeaponId ?? -1;
        if (wid >= 0 && wid < XonoticGodot.Common.Gameplay.Weapons.Count)
        {
            XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(wid);
            string net = w?.NetName ?? "";
            if (net is "vortex" or "vaporizer" || net.Contains("nex"))
            {
                // Charge source: a listen host with a live local slot reads it directly; a pure client (no
                // LocalServerPlayer) or a followed spectatee instead reads the watched player's networked
                // WepentView.VortexCharge off the entity stream, so the first-person glow also lights on a remote
                // client / for a spectated charge. Same vortex_glowcolor ramp either way.
                int specNet = _client?.SpectatingNetId ?? 0;
                float? charge = null;
                if (LocalServerPlayer is { } p && specNet == 0)
                {
                    if (!p.IsDead && !p.IsObserver)
                        charge = p.WeaponState(new WeaponSlot(0)).VortexCharge;
                }
                else
                {
                    // TryGetViewedState: the own entity lives in LocalState, never the remote table (see #51/#58).
                    int watched = specNet != 0 ? specNet : (_client?.LocalNetId ?? 0);
                    if (watched != 0 && _client != null && _client.TryGetViewedState(watched, out var rs)
                        && rs.WepentView.VortexCharge > 0f)
                        charge = rs.WepentView.VortexCharge;
                }

                if (charge is { } c)
                {
                    glow = VortexGlowColor(pants, Mathf.Max(0.25f, c));
                    if (glow.R + glow.G + glow.B <= 0.001f)
                        glow = pants; // charge disabled → fall back to the pants palette color
                }
            }
        }

        _viewModel.SetPlayerColors(glow, shirt, pants, true);
    }

    /// <summary>
    /// [#43] The packed <c>clientcolors</c> of the player this client is LOOKING THROUGH (Base
    /// <c>entcs_GetClientColors(current_player)</c>): the listen host reads its live slot, a pure client /
    /// a followed spectatee reads the watched player's networked <see cref="XonoticGodot.Net.NetEntityState.Colors"/>.
    /// 0 = no colors resolved.
    /// </summary>
    private int LocalViewedClientColors()
    {
        int specNet = _client?.SpectatingNetId ?? 0;
        if (LocalServerPlayer is { } p && specNet == 0)
            return p.ClientColors & 0xFF;
        int watched = specNet != 0 ? specNet : (_client?.LocalNetId ?? 0);
        // TryGetViewedState (NOT TryGetRemoteState): the own entity is kept in ClientNet.LocalState, never the
        // remote table — the old lookup MISSED for a pure client's own colors, so the first-person weapon never
        // took the profile/team tint on a remote client (playtest #58).
        if (watched != 0 && _client != null && _client.TryGetViewedState(watched, out var rs))
            return rs.Colors & 0xFF;
        return 0;
    }

    /// <summary>
    /// [#43] Push the local profile color (<c>_cl_color</c>, packed <c>16*shirt+pants</c> — what the
    /// multiplayer-profile palette grids edit) to the server, Base's engine-side color userinfo: the listen
    /// host applies it directly through <c>SV_ChangeTeam</c> (<see cref="XonoticGodot.Server.Teamplay.ChangeTeam"/>
    /// — a NO-OP in teamplay, where the team owns the colors), a pure client sends the <c>color</c> client
    /// command (same server sink). Change-gated so it costs one cvar read per frame; an unset/0 cvar pushes
    /// nothing (keeps the colorless default look rather than forcing white/white).
    /// </summary>
    private void PushLocalPlayerColor()
    {
        string s = MenuState.Cvars.GetString("_cl_color");
        int want = string.IsNullOrWhiteSpace(s) ? 0 : (int)MenuState.Cvars.GetFloat("_cl_color") & 0xFF;
        if (want == _lastPushedClColor)
            return;
        if (want == 0 && _lastPushedClColor == int.MinValue)
        {
            _lastPushedClColor = 0; // never chosen — leave the server default alone
            return;
        }

        if (_serverWorld is { } w && LocalServerPlayer is { } p)
        {
            w.Teamplay.ChangeTeam(p, want); // QC SV_ChangeTeam: recolors in FFA, ignored in teamplay
            _lastPushedClColor = want;
        }
        else if (_client is { Accepted: true } c)
        {
            c.SendStringCommand($"color {want}");
            _lastPushedClColor = want;
        }
        // Neither ready yet (connecting) → try again next frame.
    }

    private int _lastPushedClColor = int.MinValue;

    /// <summary>
    /// Drive the first-person reload animation from the networked clip state — the port's stand-in for Base's
    /// networked <c>WFRAME_RELOAD</c> (all.qc <c>NET_HANDLE(wframe)</c> plays <c>anim_reload</c> when the server
    /// reports a reload). The port doesn't network the <c>wframe</c> temp entity, but a reload is fully observable
    /// from the already-networked magazine counter: <c>ClipLoad == -1</c> is the QC "scheduled for reload" sentinel
    /// (<see cref="WeaponSlotState.ClipLoad"/>), held for the duration of the reload think. Play the viewmodel
    /// reload clip ONCE on the rising edge (so it doesn't restart every frame); the viewmodel returns to its idle
    /// clip when the one-shot reload clip finishes. Listen-server only reads the local <see cref="LocalServerPlayer"/>
    /// slot state, same source the crosshair reload ring uses.
    /// </summary>
    private void UpdateViewModelReloadAnim()
    {
        if (_viewModel is null || !GodotObject.IsInstanceValid(_viewModel))
            return;
        bool reloading = false;
        if (LocalServerPlayer is { } p && !p.IsDead && !p.IsObserver)
        {
            WeaponSlotState st = p.WeaponState(new WeaponSlot(0));
            // clip_load == -1 = QC reload-in-progress sentinel; clip_size>0 guards non-reloadable weapons (clip 0).
            reloading = st.ClipSize > 0 && st.ClipLoad < 0;

            // (playtest r8 #3) FIRE clip, listen-host path: the networked ViewmodelFrame selector is deliberately
            // NOT consumed on a listen host (UpdateViewModelAnimFromNet returns early — "the host path owns it"),
            // but this host path only ever derived RELOAD, so the local player's fire animation NEVER triggered
            // (the muzzle flash is the separate predicted path). Base restarts the clip per shot
            // (weapon_thinkf(WFRAME_FIRE1) on every attack) — the faithful per-shot edge on the live slot is the
            // ATTACK_FINISHED bump: every shot pushes it forward. An int-frame rising edge can't re-trigger
            // during sustained fire; the AttackFinished VALUE change can.
            float af = st.AttackFinished;
            if (!reloading && af > _viewmodelLastAttackFinished && _serverWorld is { } sw && af > (float)sw.Time)
                _viewModel.PlayFireClip();
            else if (_serverWorld is { } sw2 && af <= (float)sw2.Time)
                // Attack window closed (refire expired, no new shot): re-assert idle like Base's weapon state
                // machine does explicitly (w_ready → weapon_thinkf(WFRAME_IDLE)). Needed because SOME fire
                // clips are authored LOOPING (h_hagar fire loop=1) — the end-of-clip idle recovery never
                // triggers on those and the gun pumped forever after the last rocket (playtest r12).
                _viewModel.StopLoopingFire();
            _viewmodelLastAttackFinished = af;
        }
        if (reloading && !_viewmodelReloading)
            _viewModel.PlayReload();
        _viewmodelReloading = reloading;
    }

    /// <summary>Last seen slot-0 ATTACK_FINISHED — the per-shot fire-anim edge for the listen host (see
    /// <see cref="UpdateViewModelReloadAnim"/>). Reset on equip is unnecessary: a stale higher value only
    /// suppresses until the first shot pushes past it.</summary>
    private float _viewmodelLastAttackFinished;

    /// <summary>
    /// (#41, upgraded r14 B/C) Sample the map's baked lightgrid at the camera and drive the viewmodel's
    /// DP-style grid shading — Mod_Q3BSP_LightPoint samples per entity; the viewmodel is what the player
    /// stares at, so it goes first (players/items are the documented follow-up). The full sample is pushed
    /// (ambient RGB + directed RGB + the baked light DIRECTION) and the skin shader reproduces DP's
    /// MODE_LIGHTDIRECTION formula per pixel — position-varying colored shading structure instead of the
    /// r13 flat modulate. Scale is DP's ABSOLUTE 1/128 (Mod_Q3BSP_LightPoint <c>stylescale</c>: grid byte
    /// 128 = 1.0, above overbrightens — with the r_model_light_gamma response this reads like DP, where
    /// the r13 self-calibrating normalization was a crutch for the linear pipeline compressing it), tunable
    /// via <c>r_model_light_scale</c>. No grid on the map → the PBR + fill-light fallback.
    /// </summary>
    private void UpdateViewModelLightgrid()
    {
        if (_viewModel is null || !GodotObject.IsInstanceValid(_viewModel))
            return;
        XonoticGodot.Formats.Bsp.LightGridData? grid = _bsp?.LightGrid;
        if (grid is null || _camera is null || !GodotObject.IsInstanceValid(_camera))
        {
            _viewModel.SetGridLight(false, Vector3.One, Vector3.Zero, Vector3.Up);
            return;
        }
        System.Numerics.Vector3 eye = Coords.ToQuake(_camera.GlobalPosition);
        grid.Sample(eye, out System.Numerics.Vector3 amb, out System.Numerics.Vector3 dir, out System.Numerics.Vector3 dirn);
        string scaleCvar = MenuState.Cvars.GetString("r_model_light_scale");
        float userScale = string.IsNullOrWhiteSpace(scaleCvar) ? 1f : MenuState.Cvars.GetFloat("r_model_light_scale");
        float scale = (userScale <= 0f ? 1f : userScale) / 128f;
        var ambient = new Vector3(amb.X, amb.Y, amb.Z) * scale;
        var diffuse = new Vector3(dir.X, dir.Y, dir.Z) * scale;
        // The sample direction is QUAKE axes; the shader wants GODOT world axes (it view-transforms per pixel).
        Vector3 dirG = dirn.LengthSquared() > 1e-6f ? Coords.ToGodot(dirn).Normalized() : Vector3.Up;
        _viewModel.SetGridLight(true, ambient, diffuse, dirG);
    }

    /// <summary>
    /// Drive the local view-model's anim frame from the networked <see cref="XonoticGodot.Net.WepentViewState.ViewmodelFrame"/>
    /// selector (Base <c>wframe</c> NET_HANDLE: 0 idle, 1 fire, 2 reload, 3 raise, 4 drop). This is the pure-client /
    /// spectatee path: a listen host derives the reload anim from <see cref="LocalServerPlayer"/> (see
    /// <see cref="UpdateViewModelReloadAnim"/>) and needs nothing here, but a remote client / a followed spectatee has
    /// no local slot — so resolve the WATCHED player's per-player WepentView slice off the entity stream and let
    /// <see cref="ViewModel.SetNetAnimFrame"/> play the matching clip once on a rising edge (idempotent per frame).
    /// When following a player use <see cref="ClientNet.SpectatingNetId"/>; otherwise (no spectatee) only feed it when
    /// there is no <see cref="LocalServerPlayer"/> (pure client) — on a listen host the host path already owns it.
    /// </summary>
    private void UpdateViewModelAnimFromNet()
    {
        if (_viewModel is null || !GodotObject.IsInstanceValid(_viewModel) || _client is null)
            return;

        int watched;
        if (_client.SpectatingNetId != 0)
            watched = _client.SpectatingNetId;            // following another player → their networked anim frame
        else if (LocalServerPlayer is null)
            watched = _client.LocalNetId;                 // pure client (no local slot) → our own networked anim frame
        else
            return;                                        // listen host: the host-derived reload anim owns the frame

        // TryGetViewedState (NOT TryGetRemoteState): the OWN entity is diverted to ClientNet.LocalState and never
        // enters the remote table, so the pure-client "watched == self" lookup used to MISS every frame — the
        // viewmodel then never received the server's idle edge and a LOOPING fire clip (h_hagar) pumped forever
        // (playtest #51, the remote twin of the r12 host fix).
        if (watched != 0 && _client.TryGetViewedState(watched, out var rs))
            _viewModel.SetNetAnimFrame(rs.WepentView.ViewmodelFrame);
    }

    /// <summary>Port of <c>vortex_glowcolor</c> (weapon/vortex.qc:7): a charge-scaled blend of the player's team
    /// color, ramping 0→0.3× up to the anim limit then 0.3→1.0× above it. Uses the stock balance defaults
    /// (charge_animlimit 0.5); <paramref name="charge"/> is the [0,1] charge fraction.</summary>
    private static Color VortexGlowColor(Color teamColor, float charge)
    {
        const float animlimit = 0.5f; // g_balance_vortex_charge_animlimit default
        float f = Mathf.Min(1f, charge / animlimit);
        Color g = teamColor * (f * 0.3f);
        if (charge > animlimit)
        {
            f = (charge - animlimit) / (1f - animlimit);
            g += teamColor * (f * 0.7f);
        }
        return g;
    }

    /// <summary>
    /// The first-/third-person <c>v_*</c> model vpath for a weapon (<c>"models/weapons/" + WorldModel</c>), or
    /// <c>""</c> when the weapon has no world model. The single source of truth shared by
    /// <see cref="EquipNetworkedWeapon"/>, <see cref="BuildWeaponWorldModel"/>, and
    /// <see cref="PrecacheWeaponModels"/> so they all hit the SAME asset-cache key.
    /// </summary>
    internal static string WeaponVModelPath(XonoticGodot.Common.Gameplay.Weapon w)
        => string.IsNullOrEmpty(w.WorldModel) ? "" : "models/weapons/" + w.WorldModel;

    /// <summary>
    /// Port of the per-weapon <c>wr_viewmodel</c> override (Base weapon.qh:160 default = string_null; only Tuba
    /// overrides — tuba.qc:423). Returns the <c>v_*.md3</c> filename to substitute for the active weapon's static
    /// view-model, or <c>""</c> for no override. Tuba swaps its model by the currently played instrument
    /// (<c>tuba_instrument</c>): 0 → <c>v_tuba.md3</c>, 1 → <c>v_akordeon.md3</c>, 2 → <c>v_kleinbottle.md3</c>.
    /// The instrument is read from the local server player's slot-0 weapon state (host path); a pure remote client
    /// has no slot state here so it keeps the default Tuba model — the networked exterior model still updates.
    /// </summary>
    private string WeaponViewModelOverride(int weaponId)
    {
        if (weaponId < 0 || weaponId >= XonoticGodot.Common.Gameplay.Weapons.Count)
            return "";
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(weaponId);
        if (w?.NetName != "tuba" || LocalServerPlayer is not { } p)
            return "";
        int instrument = p.WeaponState(new WeaponSlot(0)).TubaInstrument;
        return instrument switch
        {
            1 => "v_akordeon.md3",
            2 => "v_kleinbottle.md3",
            _ => "v_tuba.md3",
        };
    }

    /// <summary>
    /// Warm every registered weapon's view model (and its sibling <c>h_*</c> hand rig) into the asset caches
    /// once, up-front. <see cref="EquipNetworkedWeapon"/> / <see cref="BuildWeaponWorldModel"/> otherwise read
    /// + decode the model and ALL its textures synchronously on the main thread the first time each weapon is
    /// switched to or picked up — the brief stutter on a never-seen-before weapon. Building each model once here
    /// (under the connect overlay, where a load pause is expected) populates <see cref="AssetLoader"/>'s parse
    /// cache and <see cref="AssetSystem"/>'s material/texture caches; the throwaway build node is freed
    /// immediately (it only exists to drive the cache-filling side effects). The later real load rebuilds a
    /// cheap mesh from the cached parse and reuses the cached materials, so the in-combat swap no longer hitches.
    ///
    /// <para>All weapons are warmed (not just this match's loadout): the cost is small and one-time, and it
    /// covers anything the player later picks up, switches to, or sees another player carry — faithful to
    /// DarkPlaces precaching weapon models at map load. Runs once (guarded by <see cref="_weaponsPrecached"/>).</para>
    /// </summary>
    private async System.Threading.Tasks.Task PrecacheWeaponModelsAsync()
    {
        if (_assets is null || _weaponsPrecached)
            return;
        _weaponsPrecached = true;

        // Smart-precache: only warm the v_ model + hand rig for weapons we EXPECT to see this match (the
        // map's weapon_<netname> spawns, plus whatever the active gametype/mutators force). Anything else
        // is left to AssetLoader's lazy cache — first encounter takes a one-frame hitch and then it's
        // cached. The cost saving is real: a dm map averages ~9 distinct weapons; warming 24 is ~2.5× more
        // mesh/material/texture work than this match will ever use. Mutators that REPLACE the loadout
        // (instagib/overkill) shrink this further. Always register muzzle offsets, though — that uses a
        // header-only parser (LoadMuzzleModel), is cheap, and the in-process listen server needs them for
        // any weapon a player might fire (including the lazy ones).
        System.Collections.Generic.HashSet<string> expected = ComputeExpectedWeapons();

        // A3: warm EVERY weapon's v_ model by default (cl_precache_all_weapons 1), not just this match's
        // expected loadout — the extra load cost is hidden by the loading screen and it removes the 30–300 ms
        // stall the first time the player switches to / picks up / sees an unanticipated weapon. The smart
        // expected-only path is still available (set cl_precache_all_weapons 0) for memory-constrained machines.
        bool warmAll = (_sharedCvars?.GetFloat("cl_precache_all_weapons") ?? 1f) != 0f;

        // Spread the per-weapon load across frames so the loading bar visibly ticks instead of freezing on
        // this one stage. Yield every few weapons (not every one — the yield costs a frame each).
        const int YieldEveryNWeapons = 4;

        int warmed = 0, skipped = 0, muzzles = 0;
        int i = 0;
        // (hitch-fix 2026-06-15) Collect the built v_ models to RENDER them once at the end (see below) instead of
        // freeing them unrendered — building+freeing warmed only the caches, leaving each weapon's material
        // pipeline UNcompiled, so the first time a weapon was drawn (1st-person view model on switch, or another
        // player's 3rd-person carried weapon) it paid a mid-match PIPELINE-COMPILE hitch. Same warm-by-render fix
        // as the player-model roster (PrecacheCombatSoundsAndModelsAsync) and the idle warmer (§12.6-2).
        var weaponWarmRoots = new System.Collections.Generic.List<Node3D>();
        foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
        {
            string vModel = WeaponVModelPath(w);
            if (string.IsNullOrEmpty(vModel))
                continue;

            // Per-weapon shot-origin (QC CL_WeaponEntity_SetModel: movedir, all.qc:367-424). Computed via the
            // header-only MuzzleTag parser — cheap enough to do for EVERY weapon so server-side projectile
            // spawns stay correct even for weapons we didn't warm. Base prefers the v_ model's own shot tag
            // (transformed through the h_ rig's weapon-attach bone), falling back to the h_ rig's shot tag.
            string hModel = vModel.Replace("/v_", "/h_").Replace(".md3", ".iqm");
            System.Numerics.Vector3? shot = _assets.LoadMuzzleOffset(vModel, hModel != vModel ? hModel : null);
            if (shot is { } so)
            {
                XonoticGodot.Common.Gameplay.WeaponFiring.RegisterMuzzleOffset(w.RegistryId, so);
                muzzles++;
                // `set developer 1` to confirm the per-weapon shot origin (QC movedir) actually applied: forward
                // (x), +left (y), up (z) from the eye. A weapon that fires from screen-CENTER would show ~(0,0,0)
                // or be ABSENT here (fell back to the generic offset). Devastator should read ~(40.9,-9,-17).
                XonoticGodot.Common.Diagnostics.Log.Trace(
                    $"[muzzle] {w.NetName}: movedir=({so.X:0.0},{so.Y:0.0},{so.Z:0.0}) fwd/left/up from eye");
            }

            // The heavy bit: full mesh + material/texture build of the v_ model, plus the hand rig. Warmed for
            // every weapon by default (warmAll); the smart path restricts it to this match's expected loadout.
            // An unanticipated pickup under the smart path just lazy-loads (one-frame hitch, then cached).
            if (warmAll || expected.Contains(w.NetName))
            {
                // Build once to fill the parse + material/texture caches; keep the node to render it for pipeline
                // warm below (instead of freeing it unrendered). A miss just caches the failure.
                if (_assets.LoadModel(vModel) is { } vRoot)
                    weaponWarmRoots.Add(vRoot);
                // The hand rig is loaded by WeaponAttachTransform on each switch; warm it too (it builds +
                // frees its own throwaway node internally and caches the attach transform's model).
                ViewModelEquip.WeaponAttachTransform(_assets, vModel);
                warmed++;
            }
            else
            {
                skipped++;
            }

            if (++i % YieldEveryNWeapons == 0)
            {
                await YieldForLoadingFrame();
                if (!IsInsideTree()) return;
            }
        }
        // Render the warmed v_ models offscreen for a few frames so their material pipelines COMPILE now (under
        // the loading screen) — Godot compiles a pipeline on first DRAW, so an un-drawn warm model left it
        // uncompiled. Then free them; the texture/material caches persist. Mirrors the player-roster warm.
        if (weaponWarmRoots.Count > 0)
            XonoticGodot.Game.Client.GpuWarmPass.WarmNodes(this, weaponWarmRoots, () =>
            {
                foreach (Node3D r in weaponWarmRoots)
                    if (GodotObject.IsInstanceValid(r))
                        r.QueueFree();
            });
        GD.Print($"[NetGame] precached {warmed} weapon models, skipped {skipped} (lazy), {muzzles} muzzle tags.");
    }

    /// <summary>
    /// Warm the combat-sound decode cache + the common player models at map load (A3) so the first weapon fire /
    /// impact / explosion doesn't stall the frame decoding its OGG (the report's 5–50 ms first-play cost), and
    /// the first sight of a player doesn't stall building its skeletal IQM + textures (20–150 ms). Precaches
    /// every registered sound under <c>sound/weapons/</c> (the per-weapon fire/impact/reload lists) and the local
    /// + stock-default player models. Everything else (announcer voices, item-pickup cues, other players' model
    /// picks) stays lazy — the idle-warmup queue (S3) mops up the long tail. Yields so the loading bar ticks.
    /// </summary>
    private async System.Threading.Tasks.Task PrecacheCombatSoundsAndModelsAsync()
    {
        if (_assets is null)
            return;

        int sounds = 0, i = 0;
        foreach (XonoticGodot.Common.Gameplay.GameSound s in XonoticGodot.Common.Gameplay.Sounds.All)
        {
            // Combat sounds live under sound/weapons/ (rocket_fire, rocket_impact, …) — the report's per-weapon
            // fire/impact lists. LoadSound fills _soundCache (and caches misses, so no re-probe on real play).
            if (string.IsNullOrEmpty(s.Sample)
                || !s.Sample.StartsWith("weapons/", System.StringComparison.OrdinalIgnoreCase))
                continue;
            _assets.LoadSound(s.Sample);
            sounds++;
            if (++i % 16 == 0)
            {
                await YieldForLoadingFrame();
                if (!IsInsideTree()) return;
            }
        }

        // Player models: warm the local player's chosen model (_cl_playermodel) + the stock default (erebus) so
        // the first player render doesn't stall building the skeletal IQM's textures/materials (cached in
        // _assets). The throwaway build node is freed — only the material/texture caches are the goal. Other
        // players' picks stay lazy; precaching each connected client's model at join is a follow-up.
        var models = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "models/player/erebus.iqm",
        };
        string local = _sharedCvars?.GetString("_cl_playermodel") ?? string.Empty;
        if (!string.IsNullOrEmpty(local))
            models.Add(local);

        // (perf §9.4) Hosting → warm the PLAYER-MODEL roster NOW, under the loading screen, instead of letting it
        // stream in during play. The set isn't bot-specific: a bot added at RUNTIME (bot_number raised mid-match —
        // the 2026-06-14 release profile started bots=0 and added them later, so every model cold-loaded and the
        // `iqm.anims`/`iqm.mesh` + texture-decode storm hit while playing) OR a human joining picks from this same
        // stock roster, so warm it regardless of the start bot count. Each cold IQM otherwise decodes + GPU-uploads
        // its textures one-per-frame on first render; warming fills the parse/texture/material caches so the in-play
        // skeletal build (StreamSkeletalInto) is a cache hit. erebus/local are already in `models` (HashSet dedups).
        // NOTE: a human bringing a NON-roster custom model still cold-loads on first sight — a warm-on-first-seen
        // hook would close that, but the stock roster covers bots and the overwhelming majority of player picks.
        if (_isListenServer && _serverWorld is not null)
            foreach (string bm in _serverWorld.Bots.CandidateModelPaths())
                models.Add(bm);

        int modelsWarmed = 0;
        var warmRoots = new System.Collections.Generic.List<Node3D>();
        foreach (string m in models)
        {
            AssetLoader.SkeletalModelParts? parts = _assets.LoadSkeletalModel(m, 0);
            if (parts is not null)
            {
                // (hitch-fix §3, 2026-06-15) Keep the built node to RENDER it below instead of freeing it
                // unrendered. Building-then-freeing warmed only the texture/material CACHES — Godot compiles a
                // material's pipeline on first DRAW, so an un-drawn warm model left its player-shader pipeline
                // UNcompiled, and the first bot wearing it paid a mid-match PIPELINE-COMPILE hitch at join
                // (measured t=23-27s, rest-dominated, "1 ubershader"). This is the exact bug §12.6-2 fixed for the
                // idle warmer (which renders via WarmNodes); the load-time roster warm had been missed.
                warmRoots.Add(parts.Root);
                modelsWarmed++;
            }
            // Yield per model so the loading bar animates and one cold IQM's decode+upload doesn't monopolize the
            // frame — the load-screen analogue of the streamer's per-frame budget on the live path.
            await YieldForLoadingFrame();
            if (!IsInsideTree()) return;
        }
        // Render the roster offscreen for a few frames so every player-shader pipeline variant compiles NOW (under
        // the loading screen), then free them — the textures/materials stay cached. Mirrors StartIdleWarmup's
        // WarmNodes use. Textures are shared-cached, so holding the roster's scene nodes briefly is a small,
        // bounded peak. No-op/immediate-free headless.
        if (warmRoots.Count > 0)
            XonoticGodot.Game.Client.GpuWarmPass.WarmNodes(this, warmRoots, () =>
            {
                foreach (Node3D r in warmRoots)
                    if (GodotObject.IsInstanceValid(r))
                        r.QueueFree();
            });
        GD.Print($"[NetGame] precached {sounds} combat sounds, {modelsWarmed} player models (rendered for pipeline warm).");
    }

    /// <summary>The stock player models the idle warmer warms after load (the local + erebus default are already
    /// eagerly precached by A3). Warming these in the background means the first sight of another player's chosen
    /// model doesn't stall building its skeletal IQM + textures.</summary>
    private static readonly string[] IdleWarmPlayerModels =
    {
        "models/player/megaerebus.iqm", "models/player/nyx.iqm",
        "models/player/pyria.iqm", "models/player/seraphina.iqm", "models/player/umbra.iqm",
    };

    /// <summary>Spin up the idle-time asset warmer (S3): queue the long tail of sounds + the other stock player
    /// models for background warming on a small per-frame budget, so the whole asset set is hot within the first
    /// minute of play. The loaders cache, so anything the eager precache already warmed is a cheap no-op here.</summary>
    private void StartIdleWarmup()
    {
        if (_assets is null)
            return;
        // `set cl_idle_warmup 0` disables the background warm (A/B switch — the concurrent player-model parse was
        // implicated in an early gen2 GC stall). Unset/unregistered → on (only an explicit "0" disables).
        if (_sharedCvars is not null && _sharedCvars.GetString("cl_idle_warmup") == "0")
            return;
        var warmer = new XonoticGodot.Game.Client.IdleWarmer { Name = "IdleWarmer" };
        AddChild(warmer);

        // Every registered sound (announcer/pickup/voice + the already-warm combat samples). LoadSound caches.
        foreach (XonoticGodot.Common.Gameplay.GameSound s in XonoticGodot.Common.Gameplay.Sounds.All)
        {
            string sample = s.Sample;
            if (!string.IsNullOrEmpty(sample))
                warmer.Enqueue(() => _assets.LoadSound(sample));
        }

        // The other stock player models — streamed (S1): the IQM parse + sidecar reads run OFF the main thread
        // (BackgroundAssetStreamer.Request), and only the Godot build (Skeleton3D + skinned mesh + materials) runs
        // on the main thread, budgeted. Build then free the throwaway node (the texture/material caches persist).
        // Low priority: a LIVE on-demand player-model resolve (ResolvePlayerModel) shares this streamer at High
        // priority, so a visible player's model always builds before warm-only work.
        if (_streamer is null)
            return;
        AssetLoader loader = _assets;
        foreach (string model in IdleWarmPlayerModels)
        {
            string m = model;
            _streamer.Request(
                () => loader.ParseSkeletalModel(m, 0),                 // off-thread: IQM + sidecars + anims
                // (§12.3-1) Stage the texture pipeline like the live path — the idle warm previously paid the
                // same multi-hundred-ms monolithic build; now its decode runs on the worker and each upload is
                // its own budgeted job. (§12.6) The built model then RENDERS offscreen for a few frames before
                // it's freed: a built-but-never-drawn model never compiled its material variants' pipelines,
                // so the first player wearing it on screen paid the compile (`pipe +N` mid-fight).
                parse => EnqueueStagedSkeletalBuild(loader, parse,
                    XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.Low, $"idle-warm {m}",
                    () =>
                    {
                        if (loader.BuildSkeletalModel(parse)?.Root is { } warmRoot)
                            XonoticGodot.Game.Client.GpuWarmPass.WarmNodes(this,
                                new List<Node3D> { warmRoot }, () => warmRoot.QueueFree());
                    }),
                XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.Low,
                label: $"idle-warm {m} (parse)");
        }
    }

    /// <summary>
    /// Decide which weapons this match is likely to use, so <see cref="PrecacheWeaponModelsAsync"/> can warm
    /// just those instead of all 24. Combines (a) the map's <c>weapon_&lt;netname&gt;</c> spawn entities,
    /// (b) the universal blaster (granted on spawn in nearly every standard gametype), and (c) mutator/arena
    /// overrides that REPLACE the loadout (instagib → vaporizer only, overkill → ok* set, nix → all normal
    /// non-mutatorblocked weapons it rotates through, <c>g_weaponarena</c> → the arena list). Falls back to
    /// "all weapons" for the pure-client path where we have no map/cvar info yet; lazy <see cref="AssetLoader"/>
    /// caching catches anything we missed (one-frame hitch on first use, then cached).
    /// </summary>
    private System.Collections.Generic.HashSet<string> ComputeExpectedWeapons()
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Pure client (we don't see the map's entities, mutators, or cvars locally yet) — fall back to
        // warming everything. Slightly wasteful, but keeps the first-use hitch off the connecting client.
        if (!_isListenServer || _serverWorld is null)
        {
            foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
                if (!string.IsNullOrEmpty(w.NetName))
                    set.Add(w.NetName);
            return set;
        }

        XonoticGodot.Common.Services.ICvarService cv = _serverWorld.Services.Cvars;

        // --- Replacement mutators: these define the loadout outright, so the map's weapon_ pickups are
        //     gameplay-irrelevant (instagib/overkill replace the player's inventory each spawn; nix forces
        //     a cycling weapon). Return EARLY — don't union with map entities. ---

        if (cv.GetFloat("g_instagib") != 0f)
        {
            set.Add("vaporizer");
            return set;
        }

        if (cv.GetFloat("g_overkill") != 0f)
        {
            set.Add("okmachinegun");
            set.Add("oknex");
            set.Add("okshotgun");
            if (cv.GetFloat("g_weapon_overkill_rpc_weaponstart") != 0f) set.Add("okrpc");
            if (cv.GetFloat("g_weapon_overkill_hmg_weaponstart") != 0f) set.Add("okhmg");
            return set;
        }

        if (cv.GetFloat("g_nix") != 0f)
        {
            // NIX cycles through every "normal" non-mutator-blocked weapon — see NixMutator.CanChooseWeapon.
            foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
            {
                if ((w.SpawnFlags & XonoticGodot.Common.Gameplay.WeaponFlags.MutatorBlocked) != 0) continue;
                if ((w.SpawnFlags & XonoticGodot.Common.Gameplay.WeaponFlags.Normal) == 0) continue;
                if (!string.IsNullOrEmpty(w.NetName))
                    set.Add(w.NetName);
            }
            if (cv.GetFloat("g_nix_with_blaster") != 0f) set.Add("blaster");
            return set;
        }

        // g_weaponarena: empty / "0" / "off" = disabled (use normal map pickups). Otherwise space-separated
        // list of netnames, or "all"/"most" shorthand. Arena REPLACES spawn loadout (map pickups still
        // physically there, but the carried loadout is the arena set — that's what we definitely render).
        string arena = cv.GetString("g_weaponarena");
        if (!string.IsNullOrWhiteSpace(arena) && arena != "0" && !arena.Equals("off", System.StringComparison.OrdinalIgnoreCase))
        {
            if (arena.Equals("all", System.StringComparison.OrdinalIgnoreCase)
             || arena.Equals("most", System.StringComparison.OrdinalIgnoreCase))
            {
                foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
                    if ((w.SpawnFlags & XonoticGodot.Common.Gameplay.WeaponFlags.MutatorBlocked) == 0
                     && !string.IsNullOrEmpty(w.NetName))
                        set.Add(w.NetName);
            }
            else
            {
                foreach (string tok in arena.Split(new[] { ' ', '\t', ',' }, System.StringSplitOptions.RemoveEmptyEntries))
                    set.Add(tok.Trim());
            }
            // Arena weapons fully define the loadout — don't union with map pickups, but DO keep the blaster
            // since g_weaponarena_with_blaster (and stock modes) usually still grant it.
            set.Add("blaster");
            return set;
        }

        // --- Normal path: union of map weapon_ entity classnames + universal starter weapons. ---
        if (_bsp is not null)
        {
            foreach (System.Collections.Generic.IReadOnlyDictionary<string, string> dict in _bsp.Entities)
            {
                if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                    continue;
                const string prefix = "weapon_";
                if (cls.StartsWith(prefix, System.StringComparison.Ordinal))
                    set.Add(cls.Substring(prefix.Length));
            }
        }

        // The blaster is granted on spawn in essentially every stock gametype — always include it.
        set.Add("blaster");

        return set;
    }

    /// <summary>The muzzle-flash effect name for a weapon — the QC <c>m_muzzleeffect</c> attrib of each
    /// weapon's .qh (e.g. electro.qh:29 EFFECT_ELECTRO_MUZZLEFLASH, devastator.qh:28 EFFECT_ROCKET_MUZZLEFLASH,
    /// minelayer.qh:30 EFFECT_ROCKET_MUZZLEFLASH, vaporizer.qh:26 EFFECT_VORTEX_MUZZLEFLASH). Defaults to the
    /// blaster flash (an unregistered name simply yields no flash, never an error).</summary>
    private static string MuzzleEffectFor(XonoticGodot.Common.Gameplay.Weapon w) => w.NetName switch
    {
        "vortex" or "vaporizer" => "VORTEX_MUZZLEFLASH",
        "devastator" or "minelayer" => "ROCKET_MUZZLEFLASH",
        "mortar" => "GRENADE_MUZZLEFLASH",
        "machinegun" => "MACHINEGUN_MUZZLEFLASH",
        "electro" => "ELECTRO_MUZZLEFLASH",
        "crylink" => "CRYLINK_MUZZLEFLASH",
        "shotgun" => "SHOTGUN_MUZZLEFLASH",
        "hagar" => "HAGAR_MUZZLEFLASH",
        "arc" => "ARC_MUZZLEFLASH",
        "rifle" => "RIFLE_MUZZLEFLASH",
        "seeker" => "SEEKER_MUZZLEFLASH",
        "hook" => "HOOK_MUZZLEFLASH",
        "hlac" => "GREEN_HLAC_MUZZLEFLASH",
        "fireball" => "FIREBALL_MUZZLEFLASH",
        _ => "BLASTER_MUZZLEFLASH",
    };

    /// <summary>
    /// The muzzle-flash MODEL vpath for a weapon — the QC <c>m_muzzlemodel</c> attrib (each weapon's
    /// <c>*.qh</c> ATTRIB). Only the weapons that set a non-null <c>m_muzzlemodel</c> attach a model flash:
    /// Devastator + MineLayer + Overkill RPC (<c>flash.md3</c>) and Machinegun + Shotgun + Overkill HMG/MachineGun
    /// (<c>uziflash.md3</c>). NOTE: Vortex/Vaporizer DEFINE a <c>VORTEX_MUZZLEFLASH</c>/<c>VAPORIZER_MUZZLEFLASH</c>
    /// model (nexflash.md3) but their <c>m_muzzlemodel</c> attrib is <c>MDL_Null</c> — they attach NO model flash,
    /// so they (and the Arc, also <c>MDL_Null</c>) are correctly absent here. Every other weapon is <c>MDL_Null</c>
    /// and attaches NO model flash (only the particle effect), so this returns <c>""</c> for them (the ViewModel
    /// skips the model spawn).
    /// </summary>
    private static string MuzzleModelFor(XonoticGodot.Common.Gameplay.Weapon w) => w.NetName switch
    {
        "devastator" or "minelayer" or "okrpc" => "models/flash.md3",
        "machinegun" or "shotgun" or "okhmg" or "okmachinegun" => "models/uziflash.md3",
        _ => "",
    };

    /// <summary>
    /// Build the third-person world model for a carried weapon id (RC6 — wired into
    /// <see cref="ClientEntityView.WeaponModelFactory"/>). Uses the same <c>v_*</c> model + asset pipeline as
    /// the first-person viewmodel so other players' weapons render textured instead of a placeholder box.
    /// Returns null (→ placeholder) for an out-of-range id, no asset loader, or a model that won't load.
    /// </summary>
    private Node3D? BuildWeaponWorldModel(int weaponId)
    {
        if (_assets is null || weaponId < 0 || weaponId >= XonoticGodot.Common.Gameplay.Weapons.Count)
            return null;
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(weaponId);
        string vModel = WeaponVModelPath(w);
        return string.IsNullOrEmpty(vModel) ? null : _assets.LoadModel(vModel);
    }

    /// <summary>
    /// Resolve a non-player networked entity to its parsed MD3 for <see cref="ClientWorld.ModelResolver"/>.
    /// The server networks each entity's <c>.model</c> name (ServerNet), copied to <see cref="Entity.Model"/>
    /// on the client; world items/gibs/monsters then load+render it (textured, via the MD3 morph/snapshot
    /// path). Returns null for an empty / inline (<c>*N</c> brush) model or a non-MD3 model (IQM/DPM handled
    /// elsewhere) → the entity falls back to the placeholder box.
    /// </summary>
    private XonoticGodot.Formats.Md3.Md3Data? ResolveEntityModel(Entity e)
    {
        if (_assets is null || string.IsNullOrEmpty(e.Model) || e.Model.StartsWith('*'))
            return null;
        return _assets.LoadMd3(e.Model);
    }

    /// <summary>
    /// Format-agnostic fallback for <see cref="ClientWorld.EntityModelFactory"/>: build a fully-textured,
    /// animated node for a non-player world entity of ANY model format (IQM/DPM/MD3) via the asset pipeline
    /// (<see cref="AssetLoader.LoadModel"/>). Used when <see cref="ResolveEntityModel"/> returns null (non-MD3),
    /// so IQM/DPM props/items/monsters render their real model instead of the placeholder box. Null → placeholder.
    /// </summary>
    private Node3D? BuildEntityModel(Entity e)
    {
        if (_assets is null || string.IsNullOrEmpty(e.Model) || e.Model.StartsWith('*'))
            return null;
        return _assets.LoadModel(e.Model);
    }

    /// <summary>
    /// (engine-perf 2026-06-16) Build one hidden instance per DISTINCT map-item / pickup model for the offscreen
    /// GPU pipeline warm pass. Item world-models render through the entity feed and are PVS-culled until first
    /// seen, so their (mesh,material) pipeline first-compiles mid-match the moment the player rounds a corner onto
    /// a pickup — a synchronous SURFACE compile (the residual a RenderDoc capture pinned to the MD3-entity class).
    /// Loads each distinct model the SAME way <see cref="BuildEntityModel"/> does (<c>_assets.LoadModel</c> of the
    /// registry def's <see cref="StartItem.ResolveModelPath"/>), so the warmed pipeline is byte-identical to the
    /// live draw. The returned nodes are unparented (the warm pass owns + frees them); the cached parses/materials
    /// live on for the real spawns. Missing / unsupported (Quake1 .mdl) models simply aren't warmed.
    /// </summary>
    private List<Node3D> BuildItemWarmupInstances()
    {
        var list = new List<Node3D>();
        if (_assets is null || DisplayServer.GetName() == "headless")
            return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Pickup def in Registry<Pickup>.All)
        {
            string? path = StartItem.ResolveModelPath(def);
            if (string.IsNullOrEmpty(path) || !seen.Add(path))
                continue;
            try
            {
                if (_assets.LoadModel(path) is { } node)
                    list.Add(node);
            }
            catch { /* a missing / unsupported item model simply isn't warmed */ }
        }
        return list;
    }

    public override void _ExitTree()
    {
        Vmap.EditorLightBake.Cancel();   // never tear the scene down with rays still in flight
        Shutdown();
    }

    private bool _shutDown;

    // #19 auto-pause: the `slowmo` value captured while the solo-local pause holds slowmo at 0. Null = not held.
    private string? _localPauseSlowmo;

    /// <summary>#19 auto-pause realized the Xonotic way — via the <c>slowmo</c> cvar (DP host_timescale; the exact
    /// lever the timeout pause uses, GameWorld.cs:1274). <see cref="Shell"/>'s SyncAutoPause owns
    /// <c>GetTree().Paused</c> for a solo-local game; mirror it onto the server's slowmo so
    /// <c>ServerNet.ResolveSlowmo</c> time-scales the WHOLE sim to 0 (bots/projectiles/timers freeze) AND the
    /// client — which scales its input cadence by the REPLICATED slowmo — freezes prediction in lockstep (so
    /// there's no unpause input-burst). Capture/restore the prior value so a server-set slowmo survives; also
    /// restored in <see cref="Shutdown"/> so a paused teardown can't leave the next map stuck frozen.</summary>
    private void SyncLocalPauseSlowmo()
    {
        if (_serverWorld is null) return; // pure client: no authoritative sim to scale
        bool pause = GetTree().Paused;
        if (pause && _localPauseSlowmo is null)
        {
            _localPauseSlowmo = _serverWorld.Services.Cvars.GetString("slowmo");
            _serverWorld.Services.Cvars.Set("slowmo", "0");
        }
        else if (!pause && _localPauseSlowmo is not null)
        {
            _serverWorld.Services.Cvars.Set("slowmo", _localPauseSlowmo);
            _localPauseSlowmo = null;
        }
    }

    /// <summary>
    /// Dispose the client + (listen) server transports and free the prediction carrier — idempotent. Call this
    /// SYNCHRONOUSLY before <see cref="Node.QueueFree"/> when tearing a session down, so the server's UDP port is
    /// released immediately: a deferred <see cref="_ExitTree"/> would otherwise still hold the port when the next
    /// listen server (re-host) tries to bind it in the same frame. Also runs from <see cref="_ExitTree"/> as a
    /// safety net.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown)
            return;
        _shutDown = true;

        // Motion-trace close: the toggle-off path in MotionTrace never runs on a quit/map-change, and the
        // writer buffers 128 lines — without this a short capture (or any session tail) is silently lost.
        if (_motionTrace is not null)
        {
            try { _motionTrace.Flush(); _motionTrace.Dispose(); } catch { /* best-effort on teardown */ }
            _motionTrace = null;
            _mtHave = false;
        }

        // #19: if we tore down while the solo-local auto-pause was holding slowmo at 0, restore it so the engine
        // doesn't stay frozen for the next map (mirrors TimeoutController.ResetSlowmoOnShutdown).
        if (_localPauseSlowmo is not null && _serverWorld is not null)
        {
            _serverWorld.Services.Cvars.Set("slowmo", _localPauseSlowmo);
            _localPauseSlowmo = null;
        }
        // #30: same guard for the published client render-time scale — a teardown mid-pause/slowmo must not
        // leave the menu / the next match's visuals frozen at the old factor.
        XonoticGodot.Game.Client.ClientRenderTime.Scale = 1f;

        if (_client is not null)
        {
            _client.EffectReceived -= OnEffectReceived;
            _client.SoundReceived -= OnSoundReceived;
            _client.NotificationReceived -= OnNotificationReceived;
            _client.MinigameStateReceived -= OnMinigameStateReceived;
            _client.PrintReceived -= OnServerPrintForMinigame;
            _client.PrintReceived -= OnServerPrintForChat;
        }
        if (_sharedCvarBridge is not null && _sharedCvars is not null)
        {
            _sharedCvars.Changed -= _sharedCvarBridge;   // shared store outlives this match; don't leak the hook
            _sharedCvarBridge = null;
        }
        if (_processCvarsHooked && _sharedCvars is not null)
            _sharedCvars.Changed -= OnProcessCvarChanged;   // same store-outlives-match reasoning (§11 R11)
        _minigame?.Dispose();
        // Drop any unconsumed predecoded texture images (a model whose staged build never completed —
        // entity died mid-stream / map change) so decoded pixels don't outlive the session.
        _assets?.Assets.ClearPredecodedImages();
        _client?.Dispose();
        // S5: join the server-sim worker BEFORE disposing ServerNet/the world, so the socket + world teardown is
        // single-threaded and no in-flight tick races the Dispose (the worker holds the gate around its whole
        // tick, so Join guarantees it's not mid-tick when we tear down). Clear the gate after the join so any
        // late main-thread _Process this frame takes no lock. No-op when not threaded (both null).
        _serverThread?.Dispose();
        _serverThread = null;
        if (_server is not null)
            _server.SimGate = null;
        if (_musicPlayer is not null && Godot.GodotObject.IsInstanceValid(_musicPlayer))
            _musicPlayer.SimGate = null;   // gate released; music scan reverts to lock-free
        if (_serverWorld is not null)
        {
            // Detach the world's cvar hook BEFORE dropping the world. Critical on the unified path, where that hook
            // lives on the SHARED store which OUTLIVES this match (the next map builds a fresh world on it): leave
            // it attached and every retired world would keep re-deriving weapon balance on every future cvar change.
            _serverWorld.Shutdown();
            _serverWorld.Services.TraceImpl.ConcurrencyGate = null;   // traces revert to lock-free
            _serverWorld.Simulation.TickGate = null;                  // per-tick gating off with the worker gone
        }
        _simGate = null;
        _server?.Dispose();
        // Free the carrier so a torn-down session doesn't leave a stray player edict in its facade. Use the
        // captured world (Api.Services may already have been swapped to the menu's empty facade by teardown).
        if (_carrier is not null && _world is not null && !_carrier.IsFreed)
            _world.Entities.Remove(_carrier);
    }

    // =====================================================================================
    //  Per-frame drive (the heart: server tick → client poll → send input → camera)
    // =====================================================================================

    // (§11 R11) The three cvars _Process consults every frame, cached so the hot path does zero dictionary
    // lookups: seeded on first frame, refreshed only when the shared store raises Changed for one of them.
    // With no shared store (bare CLI client) the seeds match the old per-frame defaults.
    private bool _processCvarsHooked;
    private float _bgmVolumeCv = 0.7f;
    private bool _predictFireCv = true;
    private bool _perFrameInputCv;
    // Path A send model. cl_netfps (DP-faithful, default 72) = datagram send rate; cl_movement_send_all (default 0):
    // 0 = BASE-FAITHFUL — transmit gated at 1/cl_netfps with bounded redundancy (intermediate frames coalesce above
    // ~cl_netfps×redundancy fps, exactly like DP), 1 = EXACT — transmit every predicted frame so the server replays
    // the identical command sequence and reconcile stays ~0 (more bandwidth; great on a listen server).
    private float _netFpsCv = 72f;
    private bool _sendAllCv;
    private bool _netClockSmoothCv = true; // cl_netclock_smooth: gradual render-clock creep vs hard rebase (#2)
    private bool _netInputTraceCv;         // net_input_trace: dormant net input→movement pipeline diagnostic (see _Process)
    private int _netTraceTick;             // throttle counter for the net_input_trace log
    private bool _hitchHoldCv = true;      // cl_movement_hitch_hold: Fix B post-hitch stall-aware reconcile (see docs/TROUBLESHOOTING.md)
    private bool _smoothDtCv;              // cl_smoothdt: conditioned client-motion dt — default OFF, see ConditionDt
    private bool _smoothDtCapCv = true;    // cl_smoothdt_driftcap: bounded drift ledger + wide hitch gate (2026-07-26 fix)
    private bool _mouseSmoothDtCv = true;  // m_smoothdt: yaw-channel dt conditioning (2026-07-26; see FlushMouseLook)
    private bool _interpCushionCv = true;  // cl_interp_cushion: full measured-interval slew target (DP boundmode-5 parity; see InterpBias)
    private bool _netClockDp5Cv;           // cl_netclock_dp5: DP boundmode-5 stepped correction law instead of the rate slew (A/B)
    private bool _frameGovernorCv = false; // cl_frame_governor: adaptive frame pacing (r16; default OFF — opt-in experiment)
    private bool _clMovementCv = true;     // DP cl_movement: 0 = no client prediction (view rides the server owner state)
    private bool _noLerpCv;                // DP cl_nolerp: 1 = remote entities render raw newest snapshots
    private bool _immediateButtonsCv = true; // cl_netimmediatebuttons: send fire/jump/impulse immediately past the rate gate (DP)
    // DP mouse pipeline (cl_input.c IN_Move → CL_Input): raw deltas accumulate per frame in _mouseDx/Dy
    // (_UnhandledInput), then FlushMouseLook applies the m_accelerate/m_filter block ONCE per render frame
    // (the accel math needs realframetime) and scales into the view angles. Params cached R11-style.
    private readonly XonoticGodot.Common.Input.MouseAccel _mouseAccel = new();
    private XonoticGodot.Common.Input.MouseAccelParams _mouseAccelCv = XonoticGodot.Common.Input.MouseAccelParams.DpDefaults;
    private float _pitchMinCv = -90f;      // in_pitch_min (DP -90; quake used -70)
    private float _pitchMaxCv = 90f;       // in_pitch_max (DP 90; quake used 80)
    private float _mouseDx, _mouseDy;

    private void EnsureProcessCvarCache()
    {
        if (_processCvarsHooked)
            return;
        _processCvarsHooked = true;
        RefreshProcessCvars();
        if (_sharedCvars is not null)
            _sharedCvars.Changed += OnProcessCvarChanged;
    }

    private void OnProcessCvarChanged(string name)
    {
        if (name.Equals("bgmvolume", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_predictfire", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_movement_perframe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_netfps", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_movement_send_all", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_netclock_smooth", StringComparison.OrdinalIgnoreCase)
            || name.Equals("net_input_trace", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_movement_hitch_hold", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cl_netimmediatebuttons", StringComparison.OrdinalIgnoreCase)
            // the DP mouse family: m_filter + the whole m_accelerate_* set, and the pitch clamp pair
            || name.StartsWith("m_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("in_pitch_", StringComparison.OrdinalIgnoreCase))
            RefreshProcessCvars();
    }

    /// <summary>Read a float cvar with an unset-aware default (GetFloat can't tell unset from 0 — several
    /// DP mouse-accel defaults are non-zero: minspeed 5000, power 2, in_pitch ±90).</summary>
    private float CvOr(string name, float def)
    {
        string? s = _sharedCvars?.GetString(name);
        return !string.IsNullOrEmpty(s)
            && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)
            ? v : def;
    }

    private void RefreshProcessCvars()
    {
        _bgmVolumeCv = _sharedCvars?.GetFloat("bgmvolume") ?? 0.7f;
        // cl_predictfire defaults ON: unset GetString reads "" → treat anything but "0" as on.
        _predictFireCv = (_sharedCvars?.GetString("cl_predictfire") ?? "") != "0";
        _perFrameInputCv = _sharedCvars?.GetFloat("cl_movement_perframe") is float pf && pf != 0f;
        _netFpsCv = _sharedCvars?.GetFloat("cl_netfps") is float nf && nf > 0f ? nf : 72f;
        _sendAllCv = _sharedCvars?.GetFloat("cl_movement_send_all") is float sa && sa != 0f;
        // cl_netclock_smooth defaults ON (unset → on): treat anything but "0" as enabled.
        _netClockSmoothCv = (_sharedCvars?.GetString("cl_netclock_smooth") ?? "") != "0";
        _netInputTraceCv = (_sharedCvars?.GetString("net_input_trace") ?? "") == "1";
        // cl_movement_hitch_hold defaults ON (unset → on): treat anything but "0" as enabled.
        _hitchHoldCv = (_sharedCvars?.GetString("cl_movement_hitch_hold") ?? "") != "0";
        // cl_smoothdt defaults OFF (unset → off, note the "0" fallback): raw measured delta, Base-faithful.
        // It was default-ON for the r16 variance fix; that justification died with the engine-clamp root cause
        // (see ConditionDt) and the playtest on the honest clock found no felt difference. 1 = the filter (A/B).
        _smoothDtCv = (_sharedCvars?.GetString("cl_smoothdt") ?? "0") != "0";
        // cl_smoothdt_driftcap defaults ON (unset → on): bounded drift ledger + wide hitch gate
        // (the 2026-07-26 oscillator fix); 0 = legacy r16 unbounded ledger (A/B).
        _smoothDtCapCv = (_sharedCvars?.GetString("cl_smoothdt_driftcap") ?? "") != "0";
        // cl_interp_cushion defaults ON (unset → on): DP boundmode-5 full-interval slew target; 0 = half-tick bias.
        _interpCushionCv = (_sharedCvars?.GetString("cl_interp_cushion") ?? "") != "0";
        // cl_netclock_dp5 defaults OFF: opt-in A/B of Base's stepped correction law vs the r16 rate slew.
        _netClockDp5Cv = (_sharedCvars?.GetString("cl_netclock_dp5") ?? "") == "1";
        // cl_frame_governor defaults ON (unset → on): adaptive frame pacing; 0 = off (A/B).
        _frameGovernorCv = (_sharedCvars?.GetString("cl_frame_governor") ?? "0") == "1"; // default OFF (r16: a workaround, not the fix — the variance program is)
        // DP cl_movement (default 1 = client prediction) / cl_nolerp (default 0 = interpolate) — r16 A/B.
        _clMovementCv = (_sharedCvars?.GetString("cl_movement") ?? "") != "0";
        _noLerpCv = (_sharedCvars?.GetString("cl_nolerp") ?? "") == "1";
        // cl_netimmediatebuttons defaults ON (unset → on): treat anything but "0" as enabled.
        _immediateButtonsCv = (_sharedCvars?.GetString("cl_netimmediatebuttons") ?? "") != "0";
        // The DP mouse pipeline params (cl_input.c:401-412 registration defaults for anything unset).
        var dp = XonoticGodot.Common.Input.MouseAccelParams.DpDefaults;
        _mouseAccelCv = new XonoticGodot.Common.Input.MouseAccelParams
        {
            MFilter             = CvOr("m_filter", 0f) != 0f,
            Accelerate          = CvOr("m_accelerate", dp.Accelerate),
            AccelerateMinSpeed  = CvOr("m_accelerate_minspeed", dp.AccelerateMinSpeed),
            AccelerateMaxSpeed  = CvOr("m_accelerate_maxspeed", dp.AccelerateMaxSpeed),
            AccelerateFilter    = CvOr("m_accelerate_filter", dp.AccelerateFilter),
            PowerOffset         = CvOr("m_accelerate_power_offset", dp.PowerOffset),
            Power               = CvOr("m_accelerate_power", dp.Power),
            PowerSensCap        = CvOr("m_accelerate_power_senscap", dp.PowerSensCap),
            PowerStrength       = CvOr("m_accelerate_power_strength", dp.PowerStrength),
            NaturalStrength     = CvOr("m_accelerate_natural_strength", dp.NaturalStrength),
            NaturalAccelSensCap = CvOr("m_accelerate_natural_accelsenscap", dp.NaturalAccelSensCap),
            NaturalOffset       = CvOr("m_accelerate_natural_offset", dp.NaturalOffset),
        };
        _pitchMinCv = CvOr("in_pitch_min", -90f);
        _pitchMaxCv = CvOr("in_pitch_max", 90f);
        // m_smoothdt defaults ON (unset → on): yaw-channel dt conditioning; 0 = legacy raw application (A/B).
        _mouseSmoothDtCv = (_sharedCvars?.GetString("m_smoothdt") ?? "") != "0";
    }

    public override void _Process(double delta)
    {
        using var _ngScope = XonoticGodot.Game.Client.FrameProfiler.Scope("ng.process"); // [profiling] whole-method cost
        // _Ready runs as a coroutine (async void) so the loading screen can animate between sub-stages —
        // until it sets _readyComplete, many of the fields touched below (the HUD, the camera, _client,
        // _server) are partly built. Just sit out _Process during this window; the LoadingScreen drives
        // its own animation, and the handshake state machine doesn't matter yet (no _client).
        if (!_readyComplete)
            return;

        EnsureProcessCvarCache();   // (§11 R11) hot-path cvar values are cached; refreshed on Changed

        // (motion trace v3) Wall clock at the TOP of the frame. Godot's `delta` is a start-to-start interval,
        // so only a start-of-frame stamp differences into the same quantity — the v2 column was sampled inside
        // MotionTrace (late in the frame), which added a work-time phase term to every per-frame comparison.
        // This is the reference clock the two-clock wobble detector (tools/wobble-detect.py) measures the
        // engine's reported delta against; see MotionTrace's header for what that comparison decides.
        _mtQpcTop = System.Diagnostics.Stopwatch.GetTimestamp()
            / (double)System.Diagnostics.Stopwatch.Frequency;

        float rawDt = (float)delta;
        // [r16 rubberband FIX — the conviction after the full elimination matrix] Godot's delta is the
        // PREVIOUS frame's duration: each frame advances the world by how long the LAST frame took, then is
        // displayed for however long THIS frame takes. With uniform frame times the error is zero (why
        // vsync-on and a hard-engaged cap feel smooth); with the port's frame-time variance every frame
        // carries a motion error proportional to that variance — the felt wobble, at ANY average fps,
        // surviving present-path/GC/movement-mode changes because none of them touch the variance.
        // ConditionDt feeds the client's MOTION path a rolling-median PREDICTED dt (a better estimate of
        // this frame's true display duration than last frame's measurement), passes real hitches through
        // raw, and drift-corrects so accumulated time stays exactly wall-true. The server tick pump keeps
        // RAW dt — its accumulator integrates variance harmlessly. cl_smoothdt 0 = raw dt for A/B.
        float dt = ConditionDt(rawDt);
        FrameGovernor(rawDt); // adaptive frame pacing (r16) — see the method's rationale block

        // WS1 instrumentation: how long the RENDER thread spent waiting on the sim gate last frame (per-trace
        // ConcurrencyGate acquisitions — prediction/particle/crosshair traces). Read+reset here so the counter
        // covers a whole frame; attributed one frame late like the other lagged counters. ~0 when unthreaded.
        if (_serverThread is not null)
        {
            double gateWaitMs = XonoticGodot.Engine.Collision.TraceService.GateWaitTicks
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            XonoticGodot.Engine.Collision.TraceService.GateWaitTicks = 0;
            XonoticGodot.Common.Diagnostics.Prof.Mark("sv.gatewait_ms", gateWaitMs);
        }

        // DP CL_Input: apply the frame's accumulated mouse-look FIRST, so everything below (input sampling,
        // camera, radar) sees this frame's view angles. rawDt = cl.realframetime for the accel math (the
        // audited contract); dt = the conditioned display-interval estimate for m_smoothdt's yaw-channel
        // conditioning (see FlushMouseLook).
        FlushMouseLook(rawDt, dt);

        // slowmo / host_timescale: the CLIENT-side time accumulators (input cadence + render clock) scale by the
        // SAME factor the server applies to its sim (ServerNet.StepWorld → SimulationLoop.TimeScale), so the player's
        // command rate and the server's tick rate slow/speed together and prediction stays in lockstep. On a listen
        // server both ends read the same shared cvar; a remote client reads the replicated value. 1 = real time.
        // NOTE: the server is still driven by the RAW dt below (it scales internally via TimeScale) — only the
        // client accumulators are pre-scaled here.
        float slowmo = ResolveTimeScale();
        // #30: publish the factor for every client-side visual animation driver (viewmodel sway/clips, casings,
        // gibs, entity/player animators, ambient emitters, faithful particles) — the port of DP scaling cl.time
        // (which ALL CSQC animation reads) by movevars_timescale, so a slowmo/paused sim slows/freezes the
        // client visuals in lockstep instead of leaving them running on wall clock.
        XonoticGodot.Game.Client.ClientRenderTime.Scale = slowmo;

        // Arm the predicted-warpzone budget: ONE predicted crossing per render frame, across every reconcile
        // replay chain this frame runs (see TriggerTouch.PredictedWarpBudget — kills the replay round-trip
        // through both paired zones that stamped a bogus entry-facing view snap).
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictedWarpBudget = 1;

        // Drive the listen server (if any) by real elapsed time — it runs its fixed ticks, pulls each client's
        // queued input, simulates, and broadcasts snapshots.
        // S5: when threaded, the dedicated worker (XG-ServerSim) owns ServerNet.Tick — the main thread must NOT
        // call it (that would double-drive the world AND race the worker). The "server.tick" main-thread scope
        // then measures ~0, which is exactly the A/B metric this item targets. When NOT threaded, drive it here
        // exactly as today.
        // #19 auto-pause: mirror Shell's solo-local pause onto the SIM the Xonotic way — the `slowmo` cvar (see
        // SyncLocalPauseSlowmo). ResolveSlowmo reads it into Simulation.TimeScale each StepWorld, so a plain
        // Tick(dt) below then advances ZERO sim time while paused; the transport still pumps (keepalive + a
        // joining remote still surfaces → Shell auto-releases). Runs BEFORE the tick so the value is live this frame.
        SyncLocalPauseSlowmo();

        if (_serverThread is null)
            using (XonoticGodot.Game.Client.FrameProfiler.Scope("server.tick"))
                _server?.Tick(rawDt); // RAW wall time: the tick accumulator integrates variance harmlessly

        // WS1 stage 3 (2026-07-11): the S5 whole-remainder gate span is GONE. It serialised the worker's ticks
        // against the entire rest of _Process — a long tick stalled the render thread at the Enter, and a long
        // frame stalled the sim (why sv_threaded never actually flattened frames). What replaced it:
        //   • transport: stage-2 inbound marshaling (main only Polls; the WORKER processes packets/connects at
        //     its step top) + the stage-1 worker-encode outbox — PumpTransportThreaded takes no gate on the
        //     common path;
        //   • world MUTATIONS from main (bench spectate, host auto-join, map-vote impulse, chat/console sinks)
        //     ride ServerNet.RunOnSimThread;
        //   • crash-capable read races closed structurally: CvarService → ConcurrentDictionary,
        //     ClientManager.PlayersSnapshot (COW roster) for cross-thread enumeration, _playerNetIds →
        //     ConcurrentDictionary;
        //   • prediction/particle traces serialise per-trace via TraceService.ConcurrencyGate (waits are
        //     bounded by the WS-BOT tick budgets, p99 ~2.5ms);
        //   • the remaining display reads of live Player/world fields (HUD mirrors, feeds, carrier mirroring)
        //     are tolerated stale/torn-per-field — the SAME class of ungated read the standalone HUD panels
        //     (scoreboard/radar/NetHud) have always done on the threaded path. Wrong-for-a-frame display data
        //     self-corrects; the reconcile absorbs a rare odd prediction seed.

        // S5 (§13.5): when threaded, the worker runs ONLY the pure-C# sim — the MAIN thread services the Godot
        // ENet transport here (receive the client input the worker's next step consumes; send the snapshot of
        // the worker's latest sim state). Done under the gate, before the world reads below. The client's own
        // transport (_client.Poll, further down) then receives that snapshot the SAME frame.
        if (_serverThread is not null)
            using (XonoticGodot.Game.Client.FrameProfiler.Scope("server.tick"))
                _server?.PumpTransportThreaded(rawDt); // RAW wall time (see the server Tick note above)

        // Demo/benchmark camera: keep the host observing a living bot (no-op unless cl_bench_spectate is set;
        // throttled to 2 Hz). WS1 stage 3: it MUTATES server-side spectator state — run it on the sim thread
        // when threaded (RunOnSimThread is inline when not). Its 2 Hz throttle field is then written only by
        // whichever thread owns the mode — never both. The cl_bench_spectate read happens HERE on main for
        // both paths: the shared store is a plain main-owned Dictionary, so the worker must never touch it
        // (BenchSpectateThink itself no longer re-reads the cvar).
        if (BenchSpectateActive)
        {
            if (_serverThread is null)
                BenchSpectateThink();
            else
                _server?.RunOnSimThread(BenchSpectateThink);
        }

        // (perf 2.0) ng.feeds: the music/announcer/host-mutator HUD-feed run — sub-scoped so the ng.process
        // residual shrinks to genuinely-unattributed work (frame-budget decomposition, perf-campaign doc).
        using (XonoticGodot.Game.Client.FrameProfiler.Scope("ng.feeds"))
        {
        // QC target_music_kill() at NextLevel: stop the map music at intermission (works on both the listen-server
        // and pure-client paths — the flag is networked via ClientNet.MatchIntermission). QC FixIntermissionClient
        // additionally loops a random sv_intermission_cdtrack word over the scoreboard; feed that value from the
        // server cvar (listen-server only — the cvar isn't networked, so it stays empty/kill for a pure client,
        // matching the stock empty default where no switch occurs anyway).
        if (_musicPlayer is not null && _client is not null)
        {
            _musicPlayer.Intermission = _client.MatchIntermission;
            if (_serverWorld is not null)
                _musicPlayer.IntermissionCdTrack = _serverWorld.Services.Cvars.GetString("sv_intermission_cdtrack") ?? "";
        }

        // QC IntermissionThink autoscreenshot dance: the server forces clients with cl_autoscreenshot to capture the
        // end-of-match scoreboard (and cl_autoscreenshot 2 always captures). Driven here off the networked
        // intermission flag — armed with a short delay (QC `autoscreenshot = time + 0.1`) so the frame grabbed is the
        // settled scoreboard, fired exactly once per match.
        UpdateIntermissionAutoscreenshot();

        // Feed the music player the current server time so trigger_music touch freshness works.
        if (_musicPlayer is not null && _serverWorld is not null)
        {
            _musicPlayer.ServerTime = _serverWorld.Time;
            // Keep bgmvolume in sync (cached; the Changed hook refreshes it on a menu/console set). Only the
            // no-shared-store path still reads the server store live (rare: bare CLI host).
            float bgm = _sharedCvars is not null ? _bgmVolumeCv : _serverWorld.Services.Cvars.GetFloat("bgmvolume");
            if (bgm > 0f) _musicPlayer.BgmVolume = bgm;
        }

        // [T51] HUD feeds produced by the server-side mutators (host path only — a pure remote client has no
        // local server mutators). damagetext: drain the floating-number events to the client draw layer,
        // projecting the victim's Quake-space origin into Godot via Coords.ToGodot and the first-person camera.
        // itemstime: push the item-respawn-time table (with the negative "available now" encoding the panel
        // already handles) to the existing ItemsTimePanel.
        if (_server is not null)
        {
            if (_damageText is not null && Mutators.ByName("damagetext") is DamagetextMutator dtm)
            {
                _damageText.Camera = _camera;
                // QC spectatee_status != -1 (playing or following a player → a meaningful view origin, so the
                // close-range / out-of-view 2D heuristics may apply); a free-fly observer (IsObserving) always
                // gets a world number.
                bool canUse2d = _client is not null && !_client.IsObserving;
                // QC write_damagetext per-viewer visibility (sv_damagetext.qc:32-37): the queue carries EVERY
                // hit on the server; the local viewer only sees the ones the sv_damagetext tier grants them —
                // at the shipped default 2 that's THEIR OWN hits only (an observer sees all at tier >= 1).
                // Without this gate every bot-vs-bot hit on the map drew a number, and the off-screen ones got
                // pinned near the crosshair by the 2D out-of-view fallback.
                float dtTier = _serverWorld?.Services.Cvars.GetFloat("sv_damagetext") ?? 2f;
                Player? dtViewer = LocalServerPlayer;
                bool dtObserver = _client?.IsObserving ?? false;
                foreach (DamageTextEvent ev in dtm.DrainPending())
                {
                    if (!DamagetextMutator.ShouldShowTo(dtTier,
                            viewerIsAttacker: dtViewer is not null && ReferenceEquals(ev.Attacker, dtViewer),
                            viewerIsObserver: dtObserver))
                        continue;
                    string wn = XonoticGodot.Common.Gameplay.Damage.DeathTypes.WeaponNetNameOf(ev.DeathType);
                    int colorKey = Weapons.ByName(wn) is { } w ? w.RegistryId : -1;
                    _damageText.Add(ev, Coords.ToGodot(ev.Target.Origin), colorKey, canUse2d);
                }
                // (The hitsound + crosshair hit flash no longer ride these damagetext events — QC drives them
                // from the feedback STATS, which UpdateCrosshairFeedback now does on both paths. That decouples
                // them from sv_damagetext, skips damagetext's own gates, stops burn-tick DoT beep trails, and
                // lets the kill sound take its EndFrame priority over the hit beep.)
            }
            if (Mutators.ByName("itemstime") is ItemstimeMutator itm && itm.IsEnabled)
            {
                // QC HUD_ItemsTime enable gate (itemstime.qc:293-296): with stock cvars (hud_panel_itemstime=2,
                // sv_itemstime=1) an ALIVE player in a normal round sees NOTHING — only spectators (mode 1/2) and,
                // in mode 2, also everyone during warmup or when sv_itemstime==2. STAT(ITEMSTIME) is modeled on
                // the host as the live sv_itemstime tier (itm.Tier); spectatee_status / warmup_stage come off the
                // local client. (Previously the panel was force-shown whenever the mutator was enabled.)
                int panelMode = Mathf.RoundToInt(
                    XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat("hud_panel_itemstime"));
                int spectatee = _client?.SpectateeStatus ?? 0;
                bool warmup = _client?.MatchWarmup ?? false;
                bool show = ItemsTimePanel.ShouldDraw(panelMode, spectatee, warmup, itm.Tier);
                _fullHud.ItemsTime.Visible = show;
                if (show)
                    _fullHud.ItemsTime.SetItemTimes(itm.CurrentTimes);
            }

            // [T41] objective-ring + hit-indication feed moved to UpdateCrosshairFeedback() (called
            // unconditionally below, like UpdateCrosshairWeaponRings) so it also runs on a pure remote client
            // off the networked LocalState slice. That same call now owns the hit/typehit/kill feedback
            // sounds + the crosshair hit flash on BOTH paths (host reads the live Player stats, remote the
            // networked Feedback block) — QC's stat-driven HitSound, no damagetext coupling.

            // (weapon-ring + vehicle-HUD feeders hoisted out of the host-only block — see the unconditional calls
            // before ProcessAnnouncerQueue below — so they also run for a pure remote client; each now picks its
            // source: LocalServerPlayer on a host, the networked ClientNet slice on a client.)
        }
        else if (_client is not null && _client.HasItemsTime)
        {
            // REMOTE-CLIENT items-time feed (no local server): the server pushed the per-peer item respawn-time
            // table + the STAT(ITEMSTIME) tier over NetControl.ItemsTime (QC the CSQC itemstime net message). The
            // server already applied the SetTimesForAllPlayers send gate (a gated-out live player received the
            // reset/cleared table), so here we only run the same client-side HUD_ItemsTime enable gate the host
            // path uses (spectatee / warmup / STAT(ITEMSTIME)==2). The tier comes off the network, not a local
            // mutator. This is what makes the panel work for a pure remote/dedicated-server client.
            int panelMode = Mathf.RoundToInt(
                XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat("hud_panel_itemstime"));
            bool show = ItemsTimePanel.ShouldDraw(
                panelMode, _client.SpectateeStatus, _client.MatchWarmup, _client.ItemsTimeTier);
            _fullHud.ItemsTime.Visible = show;
            if (show)
                _fullHud.ItemsTime.SetItemTimes(_client.ItemTimes);
        }

        // Weapon-stat crosshair rings (QC client/hud/crosshair.qc 476-557): runs on EVERY path now (the feeder was
        // hoisted out of the host-only block). On a listen host it reads the live slot state off LocalServerPlayer;
        // a pure remote / dedicated-server client mirrors the networked owner-block rings; and when FOLLOWING another
        // player (spectator) it now reads that player's per-player WepentView slice off the entity stream — the
        // cross-client wepent-prop networking that was the prior follow-up is wired (smoke consumer in
        // UpdateCrosshairWeaponRings; the full viewmodel/beam consumers land in Phase 2).
        UpdateCrosshairWeaponRings();

        // [Wave5 + vehicleview] in-vehicle HUD + aux lock crosshair (QC cl_vehicles.qc Vehicles_drawHUD /
        // drawCrosshair + the TE_CSQC_VEHICLESETUP dispatch). Runs on EVERY path now (hoisted out of the host-only
        // block, like the weapon rings): a listen host reads the live local Player's vehicle link + 0..100 stat
        // mirror; a pure remote pilot reads the networked VehicleViewState off ClientNet.LocalState; a spectator
        // reads the followed entity's VehicleView slice. The camera's cockpit/chase pull-back keys off the panel's
        // resolved InVehicle (see UpdateCamera). Skipped only when there is no full HUD yet.
        if (_fullHud is not null)
            UpdateVehicleHud();

        // [T41] objective rings (NADE_TIMER > CAPTURE_PROGRESS > REVIVE_PROGRESS) + the remote-client hit-
        // indication flash. Runs on EVERY path (hoisted out of the host-only block): a listen host reads the
        // live LocalServerPlayer stats; a pure remote client reads the networked LocalState slice the server
        // already ships (ClientNet.LocalState), so the nade/thaw/capture rings and the hit flash now show for a
        // remote client too (QC view.qc UpdateDamage + the HUD_Draw objective rings, both client-side).
        UpdateCrosshairFeedback();

        // Advance the announcer queue (play the next queued voice if the current one finished).
        _notifications?.ProcessAnnouncerQueue();
        }   // ng.feeds

        // Pump the client transport (handshake + snapshots + event bundles) before reading predicted state.
        if (_client is null)
            return;
        // Fix B (cl_movement_hitch_hold, default on): flag a frame HITCH (the shared listen-server thread stalled —
        // GC / heavy map streaming) so the reconciler HOLDS a moderate post-stall correction instead of snapping the
        // camera back (see ClientNet.HandleSnapshot). Gated by a cvar because it's defensive, not the cause of any
        // observed bug (the spawn-stutter was the ENet throttle) — `set cl_movement_hitch_hold 0` disables it.
        // Rationale + risks: docs/TROUBLESHOOTING.md.
        _client.RecentHitch = _hitchHoldCv && rawDt > HitchFrameSeconds; // hitch detection must see the REAL frame
        using (XonoticGodot.Game.Client.FrameProfiler.Scope("ng.poll"))
            _client.Poll();

        // Advance the render clock: free-run by this frame's elapsed time with a BOUNDED RATE SLEW toward
        // server time (r16 rubberband fix). The previous design free-ran then corrected 20% of the error
        // as a STEP on each fresh snapshot — every step jolted the interpolation sampling clock, visibly
        // speeding/slowing all remote entities for a frame; with the listen server's tick catch-up making
        // LatestServerTime elastic under combat load, the steps oscillated in ~300-600ms waves — the felt
        // rubberbanding. Now the clock's PLAYBACK RATE is modulated continuously (±ClockSlewMax): the same
        // error converges over a few hundred ms while entity motion never deviates perceptibly from real
        // time — frame rate and sim tick batching fully decouple from interpolation speed. Snap only on
        // the FIRST snapshot or a discontinuity beyond MaxClockResync (respawn / level change / a huge
        // hitch). cl_netclock_smooth 0 = the old hard per-snapshot rebase, for A/B.
        // cl_netclock_dp5 (parity-audit A/B): 1 = Base's exact boundmode-5 STEPPED law, applied per snapshot
        // arrival below (snap >0.5s / 50% step >0.1s / ±1..2ms creep), free-running at wall rate between
        // snapshots like DP's cl.time += clframetime. 0 (default) = the r16 continuous rate slew here.
        bool dp5 = _netClockDp5Cv && _netClockSmoothCv;
        float slew = 0f;
        if (_lastSeenServerTime >= 0f && _netClockSmoothCv && !dp5)
            slew = Math.Clamp((_client.LatestServerTime - InterpCushion() - _renderClock) * ClockSlewGain,
                -ClockSlewMax, ClockSlewMax);
        _renderClock += dt * slowmo * (1f + slew); // slowmo: stay aligned with the (time-scaled) server clock
        // DP cl_nettimesyncboundmode invariant (r16 trace finding): the clock must NEVER run past the newest
        // snapshot — sampling beyond the interp window clamps the two-snapshot lerp at f=1, rendering every
        // remote entity at its raw newest pose (uninterpolated tick-lumps: the felt combat rubberbanding; the
        // motion trace measured the clock ~9ms PAST latest on average). Hard upper bound here; the slew/step
        // above targets InterpCushion() behind (default: a full measured snapshot interval, DP mode-5 parity —
        // the DP-equivalent bound, since Base's target cl.mtime[1] also sits one interval below its window edge
        // cl.mtime[0]), so this clamp only engages a full cushion past target. Falling far behind still
        // hard-snaps via MaxClockResync below.
        if (_lastSeenServerTime >= 0f && _renderClock > _client.LatestServerTime)
            _renderClock = _client.LatestServerTime;
        if (_client.LatestServerTime != _lastSeenServerTime)
        {
            bool firstSnapshot = _lastSeenServerTime < 0f;
            // Measured snapshot spacing (DP cl.mtime[0] - cl.mtime[1]): feeds the cl_interp_cushion full-interval
            // target, which is what lets the cushion GROW when the loaded listen server produces snapshots late.
            if (!firstSnapshot)
                _snapInterval = Math.Clamp(_client.LatestServerTime - _lastSeenServerTime, 1f / 72f, 0.1f);
            _lastSeenServerTime = _client.LatestServerTime;
            float err = _client.LatestServerTime - _renderClock;
            if (firstSnapshot || !_netClockSmoothCv)
                _renderClock = _client.LatestServerTime;
            else if (dp5)
            {
                // Base cl_nettimesyncboundmode 5, verbatim (cl_parse.c:3354-3361), against the cushioned target
                // (DP's cl.mtime[1]): snap on a discontinuity, halve big errors per snapshot, else creep at
                // -2ms/+1ms per snapshot (asymmetric: falls behind faster than it catches up — never overshoots).
                float target = _client.LatestServerTime - InterpCushion();
                float terr = _renderClock - target;
                if (MathF.Abs(terr) > 0.5f)
                    _renderClock = target;
                else if (MathF.Abs(terr) > 0.1f)
                    _renderClock += 0.5f * (target - _renderClock);
                else
                    _renderClock = Math.Clamp(target, _renderClock - 0.002f * slowmo, _renderClock + 0.001f * slowmo);
            }
            else if (MathF.Abs(err) > MaxClockResync)
                _renderClock = _client.LatestServerTime;
            // (no per-snapshot step otherwise — the rate slew above owns convergence)
            if (firstSnapshot)
            {
                // Seed the carrier from the server's authoritative owner state so the predictor's first replay
                // starts at the real spawn.
                SeedCarrierFromServer();
                // C5: the camera now has an authoritative owner state to sit at — start placing it (the per-frame
                // UpdateCamera below is gated on _cameraReady until this runs).
                _cameraReady = true;
                UpdateCamera();
            }
        }

        if (_client.Accepted)
        {
            if (!_loggedAccept)
            {
                _loggedAccept = true;
                GD.Print($"[NetGame] handshake accepted by '{_client.ServerName}': netId {_client.LocalNetId}.");
                // A pure --connect client now knows the server's map — load its BSP so the remote player renders
                // and collides with the REAL world instead of the flat prediction floor (no-op on a listen server).
                if (!_isListenServer)
                    LoadClientMapFromServer(_client.ServerMapName, _client.ServerGametype);
            }

            // [#44] The listen host's "Create Game and play" join — Base's `early_join_requested` arm of the
            // PlayerPreThink autojoin (client.qc:2715): the host explicitly ASKED to start a match, so its own
            // client requests `join` the moment its observer attaches. With sv_spectate back at Base's 1 the
            // passive delayed-autojoin no longer exists (faithful — it only armed when sv_spectate was 0), so
            // without this the host — and every scripted flow that expects the host to spawn (--map smoke,
            // --camera-trace, perf runs, --screenshot's CaptureGate) — would sit at the observer prompt forever.
            // The `join` can NO-OP (a gated/blocked spawnpoint on assault/onslaught, a team-balance queue), so we
            // must NOT burn the request on the first attempt: retry on a bounded cadence until the host actually
            // spawns (IsObserver flips false), then latch done. Remote clients are NOT auto-joined — they get the
            // real Base observer flow (+jump joins, +attack spectates). A campaign host that CAN'T passive-autojoin
            // at sv_spectate 1 relies on this same path (its CmdJoin succeeds on stock campaign maps).
            // Two independent "the host is a CAMERA, not a player" gates, both of which must hold off the join:
            // --observe stays an OBSERVER by design (no body/viewmodel in the shot; nothing perturbs the
            // world) — skip the host auto-join entirely while it is armed.
            // cl_bench_spectate: same story — stand down so BenchSpectateThink can glue it to a bot
            // (without the gate the join would land once, then bench pulls it back: a one-spawn flicker on capture).
            // The editor gametype is excluded for the same reason as the observer camera: EDIT *is* the
            // observer state, so auto-joining the host would drop the mapper into a body at a spawn point a
            // second after the map loads. This command path bypasses the gametype join gate (it calls
            // ClientManager.Join directly), so the gate alone does not stop it — it has to be refused here.
            if (_isListenServer && !_hostJoinDone && !ObserverCamera.Active && !BenchSpectateActive
                && !IsEditorGametype
                && _serverWorld is { } joinWorld)
            {
                if (LocalServerPlayer is not { IsObserver: true })
                {
                    _hostJoinDone = true; // spawned (or no host player yet) — nothing to auto-join
                }
                else
                {
                    float nowMsec = Godot.Time.GetTicksMsec();
                    if (nowMsec >= _hostJoinNextRetryMsec)
                    {
                        _hostJoinNextRetryMsec = nowMsec + 1000f; // bounded: a blocked spawn no-ops, so don't spam
                        // WS1 stage 3: the join MUTATES the world — run it on the sim thread (inline when
                        // unthreaded). The retry-until-spawned loop already tolerates the deferred execution:
                        // IsObserver stays true until the worker runs it, so we just retry/latch next frame.
                        Player? joinCaller = LocalServerPlayer;
                        if (_server is { } joinSrv)
                            joinSrv.RunOnSimThread(() =>
                                joinWorld.Commands.Execute("join", isServerConsole: false, caller: joinCaller));
                        else
                            joinWorld.Commands.Execute("join", isServerConsole: false, caller: joinCaller);
                        // If the join spawned us this frame (unthreaded path), IsObserver is now false → latch.
                        if (LocalServerPlayer is not { IsObserver: true })
                            _hostJoinDone = true;
                    }
                }
            }

            // LISTEN-SERVER prediction parity: the predicted carrier and the authoritative host Player are TWO
            // distinct, co-located entities in the SAME world. The host Player is a SOLID_SLIDEBOX body in the
            // trace set, and the carrier's slide-move (PlayerPhysics.PushEntity, MoveFilter.Normal) clips Body —
            // so the carrier's sweep collides with the host player's own body, which the server player (ignore=
            // self) never does. That intermittent block makes the predicted origin drift sideways from authority,
            // and the prediction-error smoother drags the camera to the side then crawls it back. Link the carrier
            // to the host Player so TraceService.ClipToEntities skips it (it excludes ignore.Owner==touch); the
            // host player isn't known at SpawnCarrier time, so (re)assign here each frame once the spawn resolves.
            // Only the host body is skipped — projectiles/world still clip — so the projectile-passthrough fix
            // (carrier.DpHitContentsMask=PlayerClip) is unaffected.
            if (_carrier is not null && LocalServerPlayer is { } hostSelf && !ReferenceEquals(_carrier.Owner, hostSelf))
                _carrier.Owner = hostSelf;

            // F02 dead-movement gate: the carrier is a DISTINCT prediction entity that never learns it died, so
            // PlayerPhysics.Move's dead-gate would never fire for the local player and a dead host would keep
            // sliding under WASD. Mirror the authoritative dead state onto the carrier each frame: a listen
            // server reads the host Player; a pure client derives it from the networked health (only after the
            // first spawn, matching the IsDead gate used for the death-cam below). PlayerPhysics.Move then bails.
            if (_carrier is not null)
            {
                // An OBSERVER has zero health, which both of these tests read as dead — and a dead carrier makes
                // PlayerPhysics.Move bail, so the client predicted NO movement at all while the server flew the
                // observer around. That mismatch reconciled every single tick, which is what the free-fly camera
                // jitter actually was. Observing is not dying.
                bool localDead = !IsLocalObserving
                    && (LocalServerPlayer is { } self ? self.IsDead : (_everAlive && _client.Health <= 0));
                _carrier.DeadState = localDead ? DeadFlag.Dead : DeadFlag.No;

                // Match the server's observer state so the predicted leg runs the SAME branch as authority:
                // the fly branch is selected by movetype, and the hull/eye must agree or the two integrate
                // different collisions. QC PutObserverInServer: crouch hull, MOVETYPE_NOCLIP for free-fly.
                if (IsLocalObserving)
                {
                    _carrier.MoveType = MoveType.Noclip;
                    _carrier.Mins = ObserverHullMins;
                    _carrier.Maxs = ObserverHullMaxs;
                }
                else if (_carrier.MoveType == MoveType.Noclip)
                {
                    _carrier.MoveType = MoveType.Walk;
                    _carrier.Mins = HullMins;
                    _carrier.Maxs = HullMaxs;
                }

                // Mirror the server-resolved per-player speed multiplier (Speed powerup / speed·disability buffs /
                // entrap nade) onto the prediction carrier so PlayerPhysics' predicted leg scales the top speed like
                // authority (the carrier has none of those status effects to recompute it). 1 while none active.
                _carrier.SpeedMultiplierPredicted = _client.LocalSpeedMultiplier;

                // PRE-MATCH FREEZE mirror (the real spawn-rubberband fix): the server pins the player at spawn while
                // `time < game_starttime` (GameWorld.preMatchFreeze, canMove=false). Mirror that onto the PREDICTOR so
                // it runs no movement during the countdown — otherwise it predicts gravity/creep the frozen server
                // never applies, and the per-snapshot sub-32u error gets smoothed+re-armed every frame (the vibrate,
                // which then takes a freeze-length to bleed off → looked like movement freed ~5s into live). Gate on
                // HasMatchState (not MatchStartTime>0) so it engages from the very first post-spawn frame, on the
                // server clock (LatestServerTime) so it releases exactly at go-live. Warmup is freely playable.
                if (_movementStep is not null)
                    _movementStep.Frozen = _client.HasMatchState && !_client.MatchWarmup
                        && _client.LatestServerTime < _client.MatchStartTime;
            }

            // NET-INPUT DIAGNOSTIC (dormant; `set net_input_trace 1`). Logs the full input→movement pipeline every
            // ~0.25 s so a future movement/networking regression can be SEEN end-to-end instead of guessed at (this
            // is exactly what found the ENet packet-throttle spawn-stutter). Columns: push/send = client commands
            // generated / transmitted; recv/enq/batch = server commands received off the wire / past the seq-dedup /
            // drained into a movement batch; enet throttle (0..32 — gates UNRELIABLE sends, a low value silently
            // drops input) / loss / rtt; pred vs srvOrg = predicted vs authoritative origin (a widening gap = the
            // predictor running ahead of a starved/lagging server); recon = reconcile error (a steady nonzero is a
            // rubberband). Reading guide + failure signatures: docs/NET-DEBUGGING.md. Fully off when the cvar is 0
            // (the counters it reads are single increments on the hot path — negligible — so they stay always-on).
            if (_netInputTraceCv && (_netTraceTick++ & 15) == 0)
            {
                var st = _client.DbgEnet;
                var sp = LocalServerPlayer;
                GD.Print($"[nettrace] dt={dt * 1000f:F0}ms push={_client.DbgPush} send={_client.DbgSend} " +
                         $"recv={_server?.DbgRecv} enq={_server?.DbgEnqueued} batch={_server?.DbgBatch} " +
                         $"| enet throttle={st.Throttle:F0}/{st.ThrottleLimit:F0} loss={st.Loss:F0} rtt={st.Rtt:F0}ms " +
                         $"| pred={_client.PredictedOrigin} srvOrg={(sp is null ? "-" : sp.Origin.ToString())} recon={_client.LastReconcileError:F2}");
            }


            // F03 (re)spawn view snap (QC PutPlayerInServer: self.fixangle = true; self.angles = spot.angles). The
            // server latches the spawn-spot facing in the host Player's FixAngle/FixAngleAngles channel (the same
            // QC .fixangle reused for teleporters). The client owns its view angles, so snap _viewAngles to the
            // latched facing here — BEFORE the input loop below samples/sends the view — then clear (one-shot). A
            // listen server reads it in-process; a pure client would need it networked (follow-up). This also
            // snaps the view out of any server-side (multi-destination) teleport that set the host Player's flag.
            if (LocalServerPlayer is { FixAngle: true } fixSelf)
            {
                // Warpzone/teleporter crossings are usually snapped FIRST by the predictor
                // (ConsumePredictedFixAngle) — the server's stamp for the SAME crossing lands here 1-3 frames
                // later (its tick runs behind the replay). Re-applying it would throw away every mouse delta
                // in between (a "view fights me" snap-back on each crossing), so recognise it — same facing,
                // recent — and only clear the flag. Spawn snaps and unpredicted (multi-dest) teleports, which
                // the predictor never saw, still apply. Base sidesteps this by NOT using fixangle for player
                // warpzone crossings at all (server.qc:150-170 networks the transform; the client rotates its
                // own view ONCE) — this is the listen-host equivalent of that single-apply.
                NVec3 fa = fixSelf.FixAngleAngles;
                // The AUTHORITATIVE stamp drives warpzone view snaps (the predicted apply is disabled on the
                // listen host — see the wz_predict_apply block above). Skip only when a predicted path (the
                // teleporter snap) already applied this same facing recently — the round-3 known-good rule.
                float yawDiff = Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(
                    Mathf.DegToRad(fa.Y), Mathf.DegToRad(_lastPredictedFixAngles.Y))));
                bool predictedSame = Time.GetTicksMsec() * 0.001f - _lastPredictedFixTime < 1f
                    && yawDiff < 2f
                    && Mathf.Abs(Mathf.Clamp(fa.X, _pitchMinCv, _pitchMaxCv) - _lastPredictedFixAngles.X) < 2f;
                if (!predictedSame)
                {
                    _viewAngles.X = Mathf.Clamp(fa.X, _pitchMinCv, _pitchMaxCv);
                    _viewAngles.Y = fa.Y;
                    _viewAngles.Z = 0f;
                    _lastFixApplyTime = Time.GetTicksMsec() * 0.001f; // arm the predicted replay-echo discard window
                    // Re-seed the view-model sway to the new facing (warpzone crossings now snap ONLY through
                    // this authoritative path — the predicted warp no longer stamps a view snap).
                    if (_viewModel is not null && GodotObject.IsInstanceValid(_viewModel))
                        _viewModel.NotifyTeleported();
                }
                if (MenuState.Cvars.GetFloat("sv_warpzone_trace") != 0f)
                    GD.Print($"[wzview] authoritative {(predictedSame ? "skip (predicted already applied)" : "snap")} -> {fa}");
                fixSelf.FixAngle = false;
            }

            // Local fire feedback runs EVERY render frame (before the input drain below), so the muzzle flash,
            // recoil, sound and HUD pulse pop the same frame as the click rather than waiting for the next 1/72 s
            // input tick — the felt "snap" on high-refresh displays. It also latches the press for the next sampled
            // command so a sub-tick tap can't be dropped, and (cl_predictfire) drives the predicted refire clock.
            // cl_predictfire defaults ON (cached; refreshed via the Changed hook).
            _predictFire = _predictFireCv;
            // Tell the in-process effect mirror to drop the host's own muzzle flash (predicted locally) when on.
            if (_render is not null && GodotObject.IsInstanceValid(_render))
            {
                _render.SuppressOwnFireEffects = _predictFire;
                _render.LocalHostPlayer = LocalServerPlayer;
            }
            UpdateLocalFireFeedback(dt);

            // Input cadence. The client tells the server which mode it's in (PerFrameInput rides the input-frame
            // header) so the two ends agree without a shared cvar store. Cached value still A/B-toggles
            // in-session (the Changed hook refreshes it the frame the cvar is set).
            _perFrameInput = _perFrameInputCv;
            _client.PerFrameInput = _perFrameInput;
            // DP cl_movement / cl_nolerp (r16 A/B switches) + the interp-domain clock for the no-prediction
            // extrapolation. Pushed per frame so both live-toggle in-session.
            _client.UseClientMovement = _clMovementCv;
            _client.NoLerp = _noLerpCv;
            _client.RenderNow = _renderClock;
            // Send model (Path A): EXACT = transmit every predicted frame (InputSendInterval 0 → server replays the
            // identical sequence → reconcile ~0); BASE-FAITHFUL (default) = gate transmits to cl_netfps/s, the
            // bounded redundancy then coalescing intermediate frames above ~cl_netfps×redundancy fps exactly as DP
            // does. Only meaningful in per-frame (Path A) mode; the legacy fixed-tick path already sends one/tick.
            _client.InputSendInterval = _perFrameInput && !_sendAllCv ? 1f / MathF.Max(10f, _netFpsCv) : 0f;
            // cl_netimmediatebuttons (DP): fire/jump/weapon-switch bypass the rate gate above so they reach the
            // server immediately at high fps, while steady movement stays gated to cl_netfps.
            _client.ImmediateButtons = _immediateButtonsCv;
            // (perf 2.0) ng.input: the per-frame prediction + input transmit — the using's embedded
            // statement is the whole if/else, so both cadence paths are covered by one scope.
            using (XonoticGodot.Game.Client.FrameProfiler.Scope("ng.input"))
            if (_perFrameInput)
            {
                // PATH A — BASE-FAITHFUL variable-dt local prediction (Xonotic Base's default, Movetype_Physics_
                // NoMatchTicrate): step the LOCAL player ONCE per RENDER frame at the real (clamped) frame dt, so the
                // predicted origin lands at the EXACT render time. It then advances smoothly at ANY fps with no fixed
                // 1/72 tick boundary to alias against the display rate — that aliasing was the bhop "lurch" the
                // earlier fixed-tick attempt introduced (Base never had it; cf. cl_input.c CL_ClientMovement /
                // cl.cmd.frametime, and the bhop-candidates-vs-base analysis). The physics is frametime-independent
                // (half-step Verlet gravity → dt-invariant apex; verified by MovementTimingTests Phase1), so variable
                // dt does not distort the arc; only the landing tick carries the tiny ±dt quantization Base accepts.
                // A long frame is hard-capped (spiral guard) and split into <= MaxSubStepDt chunks so one huge step
                // can't tunnel collisions (DP splits moves > 0.05 s). Transmit is rate-gated in ClientNet
                // (InputSendInterval ~ 1/72 = DP cl_netfps) so a high-fps client predicts every frame WITHOUT
                // flooding the server. _inputAccum is unused here (no tick draining) — zeroed so the legacy sub-tic
                // extrapolation term is inert (the eye is already at exact render time, nothing to extrapolate).
                _inputAccum = 0f;
                float frameDt = dt * slowmo;
                if (frameDt > MaxInputFrameDt) frameDt = MaxInputFrameDt; // spiral guard: cap a multi-100ms hitch
                while (frameDt > 1e-5f)
                {
                    float sub = frameDt > MaxSubStepDt ? MaxSubStepDt : frameDt; // collision-stable sub-step split
                    _inputDeltaTime = sub;
                    _client.SendInput(_renderClock);
                    ConsumePredictedFixAngle();
                    frameDt -= sub;
                }
            }
            else
            {
                // LEGACY fixed-timestep: emit exactly one InputCommand per 1/72 s of REAL time, independent of the
                // display frame rate (DeltaTime = TicRate), so the server advances this player at true wall-clock
                // speed. Accumulate real delta and drain it in fixed quanta; cap the backlog so a hitch can't
                // trigger a spiral-of-death.
                const float tic = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate;
                _inputDeltaTime = tic;
                _inputAccum += dt * slowmo; // slowmo scales the client's command cadence to match the server's tick rate
                if (_inputAccum > MaxInputBacklog) _inputAccum = MaxInputBacklog;
                int drained = 0;
                while (_inputAccum >= tic && drained < MaxClientCatchupTicksPerFrame) // cap: graceful hitch recovery
                {
                    _inputAccum -= tic;
                    drained++;
                    // Sample WASD/mouse → InputCommand → push to the ring, predict forward, send the redundant tail.
                    // Send at the current render clock (already advanced by real dt once at the top of _Process); the
                    // drained tics share that timestamp, which the redundant-send + cvar-pump pacing tolerate. Do NOT
                    // advance _renderClock per drained tic here — that was the ~2x double-advance (see _renderClock).
                    _client.SendInput(_renderClock);
                    ConsumePredictedFixAngle(); // teleporter view-snap, inside the loop so a 2nd tick samples it
                }
            }
        }

        // "Client counts as spawned" — shared by the CaptureGate mark below and the loading-screen stage pick:
        // a real spawn (Health > 0), or an armed --observe camera (which never spawns — Health stays 0).
        // An editor client never gains health — EDIT is the observer state — so gating "loaded" on a live body
        // would leave the loading screen up forever. A ready camera is the real readiness signal there, the
        // same reasoning as the fixed observer camera beside it.
        bool clientSpawned = _cameraReady && (_client.Health > 0 || ObserverCamera.Active || IsEditorGametype);

        // Capture readiness: a windowed --screenshot waits on this so it lands on the spawned world, not the
        // loading screen or a from-origin pre-spawn frame (CaptureGate). One-shot at first spawn, independent of
        // the loading-screen block below (which a bare CLI host may not have).
        if (!_captureMarked && clientSpawned)
        {
            _captureMarked = true;
            CaptureGate.MarkReady();
        }

        // Loading screen progress during the async handshake + spawn phase (DP's progress bar filling up
        // while connecting/joining). The loading screen is owned by Shell; we BeginStage on each transition
        // (NOT every frame — that would reset the asymptote and stall the bar) and dismiss it via
        // DismissLoadingScreen when the player spawns. Final dismiss uses UpdateProgress to snap to 1.
        if (LoadingScreen is not null || _fallbackOverlay is not null)
        {
            HandshakeStage stage =
                clientSpawned          ? HandshakeStage.Spawned
                : !_client.Accepted    ? HandshakeStage.Connecting
                : !_cameraReady        ? HandshakeStage.WaitingForServer
                : HandshakeStage.Joining;

            if (stage != _handshakeStage)
            {
                _handshakeStage = stage;
                switch (stage)
                {
                    case HandshakeStage.Connecting:
                        LoadingScreen?.BeginStage("Connecting to server…", 0.94f, 1.0f);
                        break;
                    case HandshakeStage.WaitingForServer:
                        LoadingScreen?.BeginStage("Waiting for first snapshot…", 0.97f, 0.5f);
                        break;
                    case HandshakeStage.Joining:
                        LoadingScreen?.BeginStage("Joining game…", 0.99f, 0.5f);
                        break;
                    case HandshakeStage.Spawned:
                        LoadingScreen?.UpdateProgress(1f, "");
                        DismissLoadingScreen?.Invoke();
                        LoadingScreen = null;
                        DismissLoadingScreen = null;

                        if (_fallbackOverlay is not null && GodotObject.IsInstanceValid(_fallbackOverlay))
                            _fallbackOverlay.QueueFree();
                        _fallbackOverlay = null;
                        break;
                }
            }
        }

        // Spawn-zoom edge (QC cl_spawnzoom): the net path has no Teleport, so derive (re)spawn from a Health 0→>0
        // transition and arm the shared view's spawn-zoom on that edge (cl_spawnzoom eases the fov back out).
        if (_client.Accepted)
        {
            int h = _client.Health;
            if (_prevAliveHealth <= 0 && h > 0)
            {
                _view.TriggerSpawnZoom();
                _everAlive = true; // first/again alive: now Health<=0 means a real death (enables the death-cam)
            }
            _prevAliveHealth = h;
        }

        // QC button_zoom on the net path: held while the +zoom bind's key is down (BindTable). PLUS the active
        // weapon's secondary-fire zoom (QC view.qc IsZooming folds in each weapon's wr_zoomdir): the Vortex zooms
        // while ATTACK2 is held and g_balance_vortex_secondary is 0 (the stock default — the networked active
        // weapon arrives as ClientNet.ActiveWeaponId). Suppressed when dead (QC cl_unpress_zoom_on_death — the
        // view zooms out on death), paused, or the console is open. Fed to the shared view, which lerps
        // current_viewzoom toward the target in UpdateView.
        bool zoomActive = !GetTree().Paused && !ConsoleState.IsOpen && _client.Accepted && _client.Health > 0;
        XonoticGodot.Common.Gameplay.Weapon? activeWep =
            _client.ActiveWeaponId >= 0 ? XonoticGodot.Common.Gameplay.Weapons.ById(_client.ActiveWeaponId) : null;
        bool weaponZoom = activeWep is not null && activeWep.ZoomOnSecondary && BindTable.Attack2Held;
        _view.ZoomHeld = zoomActive && (BindTable.ZoomHeld || weaponZoom);

        // QC cl_player.qc CalcRefdef: `if(autocvar_chase_active) vieworg = CSQCPlayer_ApplyChase(...)` — the classic
        // user third-person camera (the menu Perspective radio binds chase_active 0/1, DialogSettingsGame:457,487).
        // Engage the shared view's classic chase mode when chase_active != 0; the death/frozen event-chase still
        // takes precedence inside FirstPersonView. (Negative chase_active is a DP debug split-screen; treat !=0 as on.)
        // An observer is never in third person: chase_active is a PLAYER preference, and while free-flying the
        // camera IS the viewpoint. Without this, returning from PLAYTEST to EDIT left the mapper looking at
        // their own back.
        _view.CameraMode = !IsLocalObserving && CvarOr(Api.Cvars, "chase_active", 0f) != 0f
            ? Client.FirstPersonView.ChaseMode.Chase
            : Client.FirstPersonView.ChaseMode.None;

        // Eye height follows the observer/player state rather than the constant, so the free-fly camera sits at
        // the entity origin and the client's prediction agrees with the server's observer hull.
        _view.EyeHeight = LocalEyeHeight;
        if (_carrier is not null)
            _carrier.ViewOfs = new NVec3(0f, 0f, LocalEyeHeight);

        // Place the first-person camera at the predicted eye each frame (smooth even between snapshots, since
        // SendInput re-predicts every tick). C5: held until the first snapshot seeds the carrier — before that
        // the predicted origin is (0,0,0) and the camera would render a from-world-origin frame during the
        // handshake. The camera is first-placed in the firstSnapshot branch above. Drives the shared view (zoom
        // lerp + camera placement + eventchase + eye-contents), so it must run BEFORE the ViewEffects feed below
        // (which reads SampleEyeContents = _view.EyeContents).
        // The orthographic editor view OWNS the camera while it is open (it parks it on an axis and switches the
        // projection). UpdateCamera would re-place it at the first-person eye every frame, which is why toggling
        // the ortho view looked like it did nothing at all: it opened, and was overwritten before the frame drew.
        if (_editorOrtho is { IsOpen: true })
        {
            _editorOrtho.Reapply();
        }
        else if (_cameraReady)
        {
            using (XonoticGodot.Game.Client.FrameProfiler.Scope("ng.camera"))
                UpdateCamera(dt);
        }

        // Camera-trace capture (apparatus A2): once spawned, record the rendered camera origin + predicted state
        // per frame; finish + quit when the scripted input is exhausted. Inert unless --camera-trace was passed.
        if (CameraTrace.Active && _cameraReady && _carrier is not null && _client is not null)
        {
            CameraTrace.RecordFrame(
                time: _renderClock, dt: dt,
                physicsOrigin: _client.PredictedOrigin,
                viewOriginGodot: _camera.GlobalPosition,
                velocity: _client.PredictedVelocity,
                onGround: _carrier.OnGround,
                viewOfsZ: _carrier.ViewOfs.Z,
                reconcileError: _client.LastReconcileError);
            if (CameraTrace.Done)
                CameraTrace.Finish(GetTree());
        }

        // Dev capture (--fx-demo): burst the configured effect from the predicted eye along the aim. Inert (a
        // single null check) unless --fx-demo was passed; only runs once the eye is seeded (_cameraReady).
        if (_devHarness is { Active: true } && _cameraReady)
        {
            NVec3 eye = _client.PredictedOrigin + new NVec3(0f, 0f, EyeHeight);
            _devHarness.Drive(dt, eye, QMath.Forward(_viewAngles));
        }

        // Zoom scope reticle (QC DrawReticle): the generic +zoom reticle, or the active weapon's scope (the
        // Vortex's gfx/reticle_nex) while zooming with it. Fed after UpdateCamera so ZoomFraction is this frame's
        // value; reuses the active weapon resolved for the zoom above. Suppressed while dead / spectating / chase.
        _reticle?.UpdateReticle(activeWep, BindTable.ZoomHeld, BindTable.Attack2Held,
            _view.ZoomFraction, LocalDeadNow(), _client.SpectatingNetId != 0, _view.ChaseActive,
            _view.ZoomScriptCaught);

        // Feed the full HUD's player-bound panels (health/ammo/weapons/crosshair) the local server Player on a
        // listen server, or the networked-stats mirror on a pure client (refreshed just before, so the panels
        // read this frame's snapshot values) — the SAME skinned panel set either way. SetPlayer no-ops when same.
        using (XonoticGodot.Game.Client.FrameProfiler.Scope("ng.hud"))
        {
            UpdateHudMirror();
            UpdateFullHudPlayer();
            UpdateCrosshairGate();
            UpdateInfoMessages();
        }

        // [r16 #5] ng.viewfx: the equip + screen-effects + radar + shownames feed block — previously unscoped,
        // one of the stretches the profiler's "proc:other dominated / add Prof scopes" debt pointed at.
        // Explicitly disposed just before the second ng.hud block (a `using var` would swallow it + the tail).
        XonoticGodot.Common.Diagnostics.Prof.ScopeToken _viewfxScope =
            XonoticGodot.Game.Client.FrameProfiler.Scope("ng.viewfx");

        // Install / swap the first-person weapon model when the networked active weapon changes (CSQC view.qc:305
        // picks the v_ model from the active weapon, rebuilding only on a swap).
        EquipNetworkedWeapon();

        // Screen reactions (QC HUD_Damage red-flash + HUD_Contents liquid tint) on the live client: feed the
        // reusable view-effects layer from the networked local health (flash on a drop) + the predicted eye
        // contents. Gated on Accepted so the pre-spawn default (health 0) doesn't read as death.
        if (_viewEffects is not null && _client is { Accepted: true })
        {
            int health = _client.Health;
            if (_prevHealth >= 0 && health < _prevHealth)
                _viewEffects.ReportDamage(_prevHealth - health);
            _prevHealth = health;
            // Observing/spectating (spectatee_status != 0) suppresses the death-fade like the pre-spawn window:
            // a free-fly observer has Health 0 (would otherwise ramp the death fade), and a follower's copied
            // health drives the HUD without a death overlay (QC spectatee_status guards the screen effects).
            bool observing = !_everAlive || _client.SpectateeStatus != 0;
            // "observing" = not yet spawned (the pre-spawn / connecting window right after Create) OR actively
            // observing/spectating: mirror QC's spectatee_status guard so health 0 here doesn't ramp the death
            // fade onto the screen. _everAlive flips true on the first spawn (line ~1212), so a genuine in-match
            // death (health<=0 after spawning) still shows the death fade. Matches the IsDead gate used below.
            _viewEffects.UpdateEffects(dt, health, SampleEyeContents(), observing);

            // Freeze-Tag icy full-screen overlay (QC cl_freezetag.qc HUD_Draw_overlay): tint the screen blue while
            // the local player is frozen, fading out as the thaw ring fills. Driven off the host-side local Player's
            // Frozen status effect + the mirrored revive progress (the same source as the crosshair thaw ring); a
            // pure remote client (no LocalServerPlayer) shows no overlay (frozen reads false).
            Player? localFrozen = LocalServerPlayer;
            bool isFrozen = localFrozen is { } lf && StatusEffectsCatalog.Frozen is { } frozenDef
                && StatusEffectsCatalog.Has(lf, frozenDef);
            _viewEffects.UpdateFrozenOverlay(isFrozen, localFrozen?.ReviveProgress ?? 0f);

            // Darkness-nade blind overlay (QC nade/darkness.qc → STAT(NADE_DARKNESS_TIME) → the CSQC full-screen
            // fade). The stat is the ABSOLUTE server time the blind expires; the client renders the remaining
            // window = NadeDarknessTime − now. The host reads the live local Player; a pure remote client reads
            // the networked own-entity slice (the Feedback block ships NadeDarknessTime). _renderClock is the
            // server-synced clock the stat is stamped against.
            float clientNow = _renderClock;
            float darknessRemaining = (LocalServerPlayer is { } lp
                ? lp.NadeDarknessTime
                : _client?.LocalState?.NadeDarknessTime ?? 0f) - clientNow;
            darknessRemaining = Mathf.Max(0f, darknessRemaining);
            _viewEffects.UpdateDarknessOverlay(darknessRemaining);
            // SND_BLIND ("misc/blind"): a one-shot 2D cue on the 0→positive onset edge (QC plays it as the blind
            // lands, not every frame). Edge-tracked so it fires once per darkness field, not while it lingers.
            bool darknessNow = darknessRemaining > 0f;
            if (darknessNow && !_nadeDarknessActive)
                PlayLocal2DSound("misc/blind");
            _nadeDarknessActive = darknessNow;

            // In-orb color flash (QC hud_colorflash): tint the screen toward an orb's color while the predicted eye
            // is inside that orb's radius. The orb set + their flash colors/alphas come from the NadeOrbRenderer
            // (fed off the entity stream); the eye is the SAME final render origin the contents sample uses.
            if (_orbRenderer is not null)
                _viewEffects.UpdateOrbColorFlash(_view.RenderedEyeQuake, _orbRenderer.ActiveOrbs());
        }

        // Keep the radar oriented to the player's facing, and feed the live +zoom fraction so the radar's
        // zoommode 0/1 follow the player's zoom the way QC's current_zoomfraction does.
        if (_radar is not null)
        {
            _radar.LocalYawDegrees = _viewAngles.Y;
            _radar.ZoomFraction = _view.ZoomFraction;
            // Keep the Onslaught gate live so the maximized radar's click-to-spawn only arms in Onslaught — the same
            // networked gametype string the HUD/scoreboard read (ScoreInfo block → GameScores.Gametype, "ons" = ONS).
            _radar.IsOnslaught = XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype == "ons";
        }

        // [T68] Shownames overlay (QC Draw_ShowNames_All): feed the per-frame view context — the local client's
        // team (the sameteam gate), whether we're in chase (the own-name gate), and our net id. The team is the
        // local server Player's on a listen server, else the local scoreboard row's team (the port's entcs team
        // slice). The Camera is set once at SetupCameraAndHud; the layer reads ClientNet directly for the rest.
        if (_shownamesLayer is not null)
        {
            _shownamesLayer.LocalNetId = _client.LocalNetId;
            // QC current_player + 1: the entnum of the player we are VIEWING — the followed spectatee when
            // spectating (a remote in RemoteIds, so its self/spectatee tag branch runs live), else the local player.
            _shownamesLayer.CurrentViewedNetId = _client.SpectatingNetId != 0 ? _client.SpectatingNetId : _client.LocalNetId;
            _shownamesLayer.SpectateeStatus = _client.SpectateeStatus;
            _shownamesLayer.SpectateeStatusChangedTime = _client.SpectateeStatusChangedTime;
            // QC `_entcs_send`'s `!(IS_PLAYER(to) || INGAME(to))` arm: an observing/spectating recipient gets every
            // player's PRIVATE entcs slice, so the shownames m_entcs_private flag is forced true for all of them.
            // spectatee_status != 0 ⇔ the local client is not a live in-game player (free-fly observe OR follow).
            _shownamesLayer.LocalIsSpectating = _client.SpectateeStatus != 0;
            _shownamesLayer.ChaseActive = _view.ChaseActive;
            _shownamesLayer.LocalTeam = LocalShownamesTeam();
            // [#55] The REAL teamplay flag (networked ScoreInfo → GameScores.Teamplay; the in-process server
            // shares the same static on a listen host). The layer previously derived teamplay from
            // LocalTeam != None — the #27 pants-team trap: FFA players carry a pants-derived team, so DM
            // players sharing profile colors classified as teammates (LOS skipped, names always visible).
            _shownamesLayer.Teamplay = XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay;
        }
        _viewfxScope.Dispose(); // close ng.viewfx — the scoreboard/panels below are ng.hud's

        // Scoreboard (QC +showscores): show while the scoreboard key is held, and feed it the networked rows +
        // team totals whenever a fresh LatestScoreboard arrives (the panel only repaints on data/toggle, so this
        // is cheap). BindTable.ShowScores is the held-button state set from the +showscores bind.
        using (XonoticGodot.Game.Client.FrameProfiler.Scope("ng.hud"))
        {
            UpdateScoreboard();
            UpdateScore();
            UpdateMatchClock();
            UpdatePickupFeed();
            UpdateModIcons();
            UpdateAccuracy();
            UpdateVotePanel();
            UpdateMapVotePanel();
            UpdateRacePanels();
            UpdateHudDynamicFollow();
        }

        // Minigame cursor (QC hud_cursormode): while a minigame board/menu (or quickmenu / maximized radar) owns
        // input, show the cursor so the player can click TTT/C4 tiles + the menu; recapture for play once it's
        // dismissed. Skip while the pause menu/console own the cursor (the Shell drives those). We RE-ASSERT the
        // desired state every frame — SetWantCapture is idempotent (it only touches Input.MouseMode on a real
        // change) — rather than edge-latching on a remembered flag. The old latch desynced after a pause-menu
        // round-trip OVER an open minigame board: the block is skipped while paused, resume force-captured while the
        // board was still up, and no edge re-fired, so the cursor stayed stuck captured until the board was re-toggled.
        //
        // ...and NOT while the loading screen is still up: through map load + the connect/join handshake the cursor
        // stays FREE (DP behaviour — you can alt-tab / mouse out of a windowed game while it loads). LoadingScreen
        // goes null the frame the local player spawns (the handshake block above), and this reassert runs later in
        // the same _Process, so the pointer is grabbed on that exact frame; after the first spawn it stays null and
        // this behaves as before.
        if (LoadingScreen is null && !GetTree().Paused && !ConsoleState.IsOpen)
            MouseCapture.SetWantCapture(!UiOwnsCursor);

        // A changelevel was requested this frame (map / gotomap / nextmap / rotation / vote / samelevel): emit it
        // DEFERRED so the actual teardown+reboot (Shell) runs at idle, never inside this server tick. Capture the
        // live gametype + bot fill so the new map comes up in the same mode with the same bots (DP changelevel).
        if (_pendingMap is not null)
        {
            string map = _pendingMap;
            _pendingMap = null;
            string gametype = _pendingGametype ?? CurrentGametype();
            _pendingGametype = null;
            int bots = _serverWorld?.Clients.BotCount ?? _botCount;
            int skill = _botSkill;
            // Campaign auto-advance (win → next level): the world store carries g_campaign + the NEXT
            // _campaign_index (Campaign.Setup set them just before this transition). Pass them so the rebooted
            // listen server stays in campaign mode at the next level. Empty for a normal map/gotomap change.
            string campId = "";
            int campIdx = 0;
            if (_serverWorld is not null && _serverWorld.Services.Cvars.GetFloat("g_campaign") != 0f)
            {
                campId = _serverWorld.Services.Cvars.GetString("_campaign_name");
                campIdx = (int)_serverWorld.Services.Cvars.GetFloat("_campaign_index");
            }
            Callable.From(() => MapChangeRequested?.Invoke(map, gametype, bots, skill, campId, campIdx)).CallDeferred();
        }
        // r16 rubberband diagnostic — last so it records this frame's FINAL camera/prediction state.
        MotionTrace(dt, rawDt, slew);

    }

    // ---- cl_frame_governor (r16): adaptive frame pacing — "reliable frame time while the rate varies" ---
    // The two conditions that felt smooth all night (vsync ON, a hard-engaged cap) share one property: the
    // frame DELIVERY cadence is uniform. The port's frame production is structurally lumpy (tick frames /
    // snapshot-decode frames / effect bursts alternate with cheap frames: p10 3.7ms vs p90 8.3ms), so with
    // no pacing the panel receives frames of wildly varying age — the felt wobble, at any average fps.
    // The governor paces frame starts at a rolling target just under what the scene currently sustains:
    // every second it sets Engine.MaxFps to ~0.98/p75(recent raw dts) — the limiter is then ENGAGED on
    // most frames (its sleep pads the cheap ones to match the expensive ones) while the rate still adapts
    // to load over seconds. AIMD dynamics (fall fast on load, climb <=5%/s) prevent self-oscillation; the
    // user's explicit cl_maxfps stays an upper bound; floor 60. cl_frame_governor 0 = off (restores the
    // ClientSettings-applied cap).
    private readonly float[] _govRing = new float[256];
    private readonly float[] _govSort = new float[256];
    private int _govI, _govN;
    private float _govAccum;
    private int _govApplied;      // last MaxFps the governor applied (0 = none yet)
    private int _govSaved = -1;   // Engine.MaxFps before the governor took over (-1 = not saved)

    private void FrameGovernor(float rawDt)
    {
        if (!_frameGovernorCv)
        {
            if (_govSaved >= 0)
            {
                Godot.Engine.MaxFps = _govSaved;
                _govSaved = -1; _govApplied = 0; _govN = 0; _govI = 0; _govAccum = 0f;
                XonoticGodot.Common.Diagnostics.Log.Info("[governor] off — restored engine cap");
            }
            return;
        }
        if (rawDt <= 0f) return;
        _govRing[_govI] = rawDt;
        _govI = (_govI + 1) % _govRing.Length;
        if (_govN < _govRing.Length) _govN++;
        _govAccum += rawDt;
        if (_govAccum < 1.0f || _govN < 32)
            return;
        _govAccum = 0f;

        // The measured dt INCLUDES the limiter's own sleep once a cap is applied, so a percentile target
        // chases itself downward (the first build spiraled 137->83). React instead to the OVER-CAP TAIL —
        // frames whose dt exceeded the budget did real work past it; sleep can't fake those:
        //   busy > 10%  -> the cap sits too close to the workload edge: drop 6%. (r16 field result: pacing
        //                  only smooths when DEEPLY engaged — at the edge, cheap frames sleep but expensive
        //                  ones still overrun, leaving residual delivery jitter. Converge near the slow
        //                  tail with margin, like the hard-100 test that felt smooth.)
        //   busy <  1%  -> real headroom: probe up 2% (the cap follows the workload back up, slowly).
        //   else        -> hold (deeply paced and sustainable — the desired steady state).
        int target;
        if (_govApplied == 0)
        {
            // First engage: seed from the uncapped distribution (no sleep in it yet).
            System.Array.Copy(_govRing, _govSort, _govN);
            System.Array.Sort(_govSort, 0, _govN);
            float p75 = MathF.Max(_govSort[(int)(_govN * 0.75f)], 0.0005f);
            target = (int)(0.98f / p75);
        }
        else
        {
            float budget = 1f / _govApplied;
            int busy = 0;
            for (int i = 0; i < _govN; i++)
                if (_govRing[i] > budget * 1.10f) busy++;
            float busyFrac = (float)busy / _govN;
            if (busyFrac > 0.10f)      target = Math.Max((int)(_govApplied * 0.94f), 60);
            else if (busyFrac < 0.01f) target = _govApplied + Math.Max(2, _govApplied * 2 / 100);
            else                       target = _govApplied;
        }

        target = Math.Max(target, 60); // floor: never governor below 60
        int userCap = (int)(_sharedCvars?.GetFloat("cl_maxfps") ?? 0f);
        if (userCap > 0 && userCap != 256)
            target = Math.Min(target, userCap); // an explicit player cap stays the upper bound

        if (_govSaved < 0) _govSaved = Godot.Engine.MaxFps;
        if (target != _govApplied)
        {
            Godot.Engine.MaxFps = target;
            if (_govApplied != 0) // don't log the first engage at boot
                XonoticGodot.Common.Diagnostics.Log.Info($"[governor] MaxFps -> {target}");
            _govApplied = target;
        }
    }

    // ---- cl_smoothdt: conditioned client-motion dt — DEFAULT OFF since 2026-07-26 ----------------------
    // WHY IT IS OFF NOW. This filter was default-ON as the r16 "variance fix", justified by an A/B in which
    // `cl_smoothdt 0` felt worse than 1. That A/B was invalid: Godot's MainTimerSync was rewriting the delta
    // onto a physics_step/N grid with a ±50 ms repayment ledger (planning/wobble-independent-audit-2026-07-26.md
    // §3f — the actual wobble), so the "raw" leg was never raw. It compared median-filtered-mangled against
    // mangled. With `physics_jitter_fix 0` restoring the measured delta, the release-export playtest found NO
    // felt difference between the legs — so the filter is carrying cost for no measured benefit:
    //   • it is the only remaining dt-side contributor to felt-band speed error (measured by the two-clock
    //     detector on a 311 s release capture: engine clock 0.01% RMS, after ConditionDt 0.46%);
    //   • it is AUTHORITATIVE, not cosmetic — the conditioned dt ships in InputCommand.DeltaTime, so the
    //     server integrates it: filter error is real movement-speed error (strafe timing, jump distance);
    //   • the driftcap sheds real wall time by design (that capture: 244 ms over 311 s, +0.078% slow);
    //   • Base has no dt filter at all, and Godot's previous-frame delta IS DP's cl.realframetime semantics
    //     (frametime-parity audit) — there is no lag here to correct that DP does not also have.
    // KEEP IT AS AN A/B, and note what a future "1 feels better" result would MEAN: that the display interval
    // genuinely is not the wall interval (DWM re-quantizes presents to vblank), in which case the right filter
    // is not a median-of-9 but a DISPLAY-CADENCE estimator — snap to the measured refresh period with drift
    // repayment (the seam doc's S2, done with a correct refresh number; Godot misreads 60 Hz in borderless).
    //
    // What it does when enabled (1): replaces the motion path's dt — the CLIENT MOTION PATH only — with an
    // estimate of the CURRENT frame's duration rather than last frame's measurement:
    //   estimate = median of the last 9 raw deltas   (robust to one-off spikes — the classic frame pacer),
    //   hitches pass through RAW                      (a real stall must advance real time — no slow-mo),
    //   drift correction                              (a bounded ±4% nudge keeps Σ(smooth) == Σ(raw), so
    //                                                  total time is exactly wall-true and nothing desyncs
    //                                                  from the server's raw-dt accumulator).
    private readonly float[] _dtRing = new float[9];
    private int _dtRingN;                // filled entries (grows to ring size)
    private int _dtRingI;                // next write slot
    private float _dtDrift;             // Σ(raw) - Σ(smooth): positive = smooth is BEHIND wall time
    private readonly float[] _dtSort = new float[9];

    private float ConditionDt(float rawDt)
    {
        if (!_smoothDtCv || rawDt <= 0f)
            return rawDt;

        // Ring update.
        _dtRing[_dtRingI] = rawDt;
        _dtRingI = (_dtRingI + 1) % _dtRing.Length;
        if (_dtRingN < _dtRing.Length) _dtRingN++;
        if (_dtRingN < 3)
            return rawDt; // not enough history yet

        // Median of the recent window.
        System.Array.Copy(_dtRing, _dtSort, _dtRingN);
        System.Array.Sort(_dtSort, 0, _dtRingN);
        float median = _dtSort[_dtRingN / 2];

        // A real hitch (or a big cadence change) passes through raw — and resets nothing: the ring absorbs
        // it and the median follows if the new cadence persists.
        // [driftcap] The legacy 1.8×/0.5× gate MISSES the common cap engage/disengage transitions:
        // 144↔250 fps is ratio 1.736/0.576, so 4-5 frames of the old cadence each dumped ~3 ms into the
        // ledger — a deterministic ~300 ms saturated-repayment episode per transition (2026-07-26 audit).
        // 1.6×/0.6× brackets those ratios; the cost is that 1.6-1.8× jitter outliers now pass raw (rare).
        float gateHi = _smoothDtCapCv ? 1.6f : 1.8f;
        float gateLo = _smoothDtCapCv ? 0.6f : 0.5f;
        if (rawDt > median * gateHi || rawDt < median * gateLo)
        {
            // keep the drift ledger honest for the raw frame too (smooth == raw here, so no drift change)
            return rawDt;
        }

        // Drift-corrected estimate: fold a bounded fraction of the accumulated (raw - smooth) error into
        // this frame so total conditioned time tracks wall time exactly; ±4% is imperceptible per frame.
        float bound = median * 0.04f;
        float nudge = Math.Clamp(_dtDrift * 0.25f, -bound, bound);
        float smooth = median + nudge;
        _dtDrift += rawDt - smooth;
        // [driftcap] Bound the ledger to 16 frames of full-rail repayment (0.64×median ≈ 4.4 ms @144fps).
        // Unbounded, a big excursion rides the ±4% clamp until fully repaid — a SUSTAINED speed error the
        // eye reads as the wobble (measured worst case: 1.5 s). Capped, no saturated episode can outlast
        // ~110 ms @144fps (below the felt band); the excess wall-time debt is shed, the same
        // accuracy-for-smoothness trade DP makes when it drops overload time (sv.perf_acc_lost).
        if (_smoothDtCapCv)
        {
            float driftCap = median * 0.64f;
            _dtDrift = Math.Clamp(_dtDrift, -driftCap, driftCap);
        }
        return smooth;
    }

    // ---- cl_motion_trace (r16 rubberband diagnostic) --------------------------------------------------
    // Per-frame CSV of the motion-timeline signals, written while the cvar is on (opened lazily, closed on
    // toggle-off). Columns answer "which signal carries the felt wave": clock_err/slew (the interp clock),
    // ticks (server catch-up bursts), cam/pred speeds (the OWN view's rendered velocity — waves here =
    // own-movement rubberband), pred_err (reconcile corrections), remote_speed (one tracked remote entity's
    // rendered velocity — waves here with a steady cam = entity-interp rubberband).
    //
    // v2 (presentation-seam falsification): cam_speed divides displacement by the SAME dt that advanced the
    // camera, so it is flat by construction and blind to dt-vs-display divergence. The v2 columns carry the
    // un-normalized signals instead — cam_step/yaw_step (what one displayed frame embodies), raw_dt (before
    // ConditionDt), and qpc_s (QueryPerformanceCounter seconds, the SAME clock PresentMon --qpc_time stamps
    // presents with), so tools/wobble-report.py can join this trace against a PresentMon capture and measure
    // displayed motion on the DISPLAY timeline. Filename is timestamped: an A/B can no longer overwrite the
    // previous leg (the r16 smoothdt-0 segment was lost exactly that way).
    //
    // v3 (engine-delta forensics): the wobble turned out to be measurable WITHOUT a display-side clock at all —
    // by comparing the delta the ENGINE reports against wall time over the same frames. Godot's
    // MainTimerSync::advance_checked rewrites process_step (clamp band from physics_step, ±jitter_fix ledger),
    // so `raw_dt_ms` is not raw: it is what the engine CLAIMED. Motion integrates that claim while the display
    // runs on wall time, so the divergence is a displayed-speed error. Two columns make that comparison exact:
    //   qpc_top_s — QPC sampled at the TOP of _Process, so diff() is the same start-to-start interval Godot's
    //               `delta` measures (the v2 qpc_s stayed, sampled here, late in the frame: a work-time phase
    //               term that telescopes over a window but not per frame).
    //   frame     — Engine.GetFramesDrawn(), so a SKIPPED row is detectable. Rows are skipped whenever dt <= 0,
    //               which the engine really does produce (main.cpp:4857 subtracts physics_step per dropped
    //               physics step); without this column a gap reads as one enormous frame time.
    // Analysis: tools/wobble-detect.py.
    private System.IO.StreamWriter? _motionTrace;
    private NVec3 _mtPrevCam, _mtPrevPred, _mtPrevRemote;
    private float _mtPrevYaw;
    private int _mtRemoteId = -1;
    private int _mtLines;
    private bool _mtHave;
    private double _mtQpcTop;           // QPC seconds at the top of this frame's _Process (v3)

    private void MotionTrace(float dt, float rawDt, float slew)
    {
        bool on = (_sharedCvars?.GetFloat("cl_motion_trace") ?? 0f) != 0f;
        if (!on)
        {
            if (_motionTrace is not null)
            {
                _motionTrace.Flush();
                _motionTrace.Dispose();
                _motionTrace = null;
                _mtHave = false;
                XonoticGodot.Common.Diagnostics.Log.Info("[motiontrace] closed");
            }
            return;
        }
        if (!_cameraReady || _client is null || dt <= 0f)
            return;

        if (_motionTrace is null)
        {
            try
            {
                string path = UserPaths.Resolve(
                    $"motion_trace_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _motionTrace = new System.IO.StreamWriter(path, append: false) { AutoFlush = false };
                _motionTrace.WriteLine(
                    "t,qpc_s,qpc_top_s,frame,dt_ms,raw_dt_ms,clock_err_ms,slew_pct,ticks," +
                    "cam_speed,cam_step,cam_step_xy,yaw_step,pred_speed,pred_err,remote_speed,maxfps,drift_ms");
                XonoticGodot.Common.Diagnostics.Log.Info($"[motiontrace] recording -> {path}");
            }
            catch (System.Exception ex)
            {
                XonoticGodot.Common.Diagnostics.Log.Info($"[motiontrace] open failed: {ex.Message}");
                _sharedCvars?.Set("cl_motion_trace", "0");
                return;
            }
        }

        NVec3 cam = Coords.ToQuake(_camera.GlobalPosition);
        NVec3 pred = _client.PredictedOrigin;
        float errMs = (_client.LatestServerTime - _renderClock) * 1000f;
        float predErr = _client.PredictionErrorOffset(_renderClock).Length();
        int ticks = _server?.LastTicksRan ?? -1;

        // One tracked remote entity's rendered pose (the lowest live id; re-picked when it vanishes).
        float remoteSpeed = -1f;
        NVec3 remote = default;
        bool haveRemote = _mtRemoteId >= 0 && _client.SampleRemote(_mtRemoteId, _renderClock, out remote, out _);
        bool remotePrevValid = haveRemote; // prev sample was the SAME target -> a remote speed is derivable
        if (!haveRemote)
        {
            // Prefer a PLAYER entity (a bot in motion) — the first trace tracked the lowest id, which was a
            // static world item and produced a useless all-zero column.
            _mtRemoteId = -1;
            foreach (int id in _client.RemoteIds)
                if (_client.TryGetRemoteState(id, out NetEntityState rs) && rs.Kind == NetEntityKind.Player
                    && (_mtRemoteId < 0 || id < _mtRemoteId))
                    _mtRemoteId = id;
            haveRemote = _mtRemoteId >= 0 && _client.SampleRemote(_mtRemoteId, _renderClock, out remote, out _);
            // NOTE (v2 fix): this used to reset _mtHave — which, with NO remote player in the session
            // (0-bot capture), re-ran every frame and suppressed EVERY row: the whole trace came out
            // empty. Only the remote-speed column is invalid across a target switch, so only it is gated.
        }

        if (_mtHave)
        {
            float camSpeed = (cam - _mtPrevCam).Length() / dt;
            float predSpeed = (pred - _mtPrevPred).Length() / dt;
            if (remotePrevValid)
                remoteSpeed = (remote - _mtPrevRemote).Length() / dt;
            // v2: raw per-frame steps (NOT divided by dt — see the header note) + the PresentMon join clock.
            // cam_step_xy: horizontal-only step — during a laser-jump/bhop flight arc the horizontal speed is
            // near-constant (air, no friction) while the ballistic Z sweep puts REAL energy into the felt
            // band, so the XY step is the clean wobble signal at exactly the high speeds where it's felt.
            double qpcS = System.Diagnostics.Stopwatch.GetTimestamp()
                / (double)System.Diagnostics.Stopwatch.Frequency;
            NVec3 camDelta = cam - _mtPrevCam;
            float camStep = camDelta.Length();
            float camStepXy = new NVec3(camDelta.X, camDelta.Y, 0f).Length(); // quake coords: Z = up
            float yawStep = Mathf.Wrap(_viewAngles.Y - _mtPrevYaw, -180f, 180f);
            _motionTrace.WriteLine(
                $"{_renderClock:F4},{qpcS:F6},{_mtQpcTop:F6},{Godot.Engine.GetFramesDrawn()}," +
                $"{dt * 1000f:F3},{rawDt * 1000f:F3},{errMs:F2},{slew * 100f:F2},{ticks}," +
                $"{camSpeed:F1},{camStep:F3},{camStepXy:F3},{yawStep:F4},{predSpeed:F1},{predErr:F2},{remoteSpeed:F1}," +
                $"{Godot.Engine.MaxFps},{_dtDrift * 1000f:F3}");
            if (++_mtLines % 128 == 0)
                _motionTrace.Flush(); // survive a quit without the toggle-off close
        }

        _mtPrevCam = cam;
        _mtPrevPred = pred;
        _mtPrevYaw = _viewAngles.Y;
        if (haveRemote) _mtPrevRemote = remote;
        _mtHave = true;
    }

    /// <summary>The active gametype short code for a changelevel: the live <c>gametype</c> cvar if a mid-match
    /// <c>gametype</c> command set it, else the mode this match booted with.</summary>
    private string CurrentGametype()
    {
        string gt = _serverWorld?.Services.Cvars.GetString("gametype") ?? "";
        return string.IsNullOrWhiteSpace(gt) ? _gametype : gt;
    }

    // ---- crosshair objective rings + hit/typehit/kill feedback ----
    // QC view.qc UpdateDamage/HitSound run off the owner's feedback STATS (HIT/TYPEHIT/KILL_TIME + the
    // cumulative damage total). Both paths feed the same HitSound state machine: a listen host reads the
    // live LocalServerPlayer fields (flushed by the server's EndFrame under the sim gate), a pure client the
    // networked LocalState slice (the EntityField.Feedback block, protocol v18).
    private int _arcWeaponId = int.MinValue; // lazily-resolved Arc registry id (the QC have_arc check)

    /// <summary>
    /// Feed the crosshair panel the local player's objective-ring stats (QC view.qc HUD_Draw 1006-1022:
    /// NADE_TIMER &gt; CAPTURE_PROGRESS &gt; REVIVE_PROGRESS) and drive the hit/typehit/kill feedback sounds +
    /// the crosshair hit-indication flash (QC view.qc UpdateDamage/HitSound + crosshair.qc:387). Runs on
    /// every path: a listen host reads the live <see cref="LocalServerPlayer"/>; a pure remote client reads
    /// the networked <see cref="ClientNet.LocalState"/> slice the server ships. CAPTURE has no server
    /// producer yet, so it stays 0 and the panel hides that ring either way.
    /// </summary>
    private void UpdateCrosshairFeedback()
    {
        if (_fullHud is null)
            return;
        CrosshairPanel x = _fullHud.Crosshair;

        // QC have_arc (view.qc:918-925): a viewmodel slot holding the Arc bypasses the beep antispam window
        // at cl_hitsound >= 2 (the beam's pitch shift must sound continuous). _equippedWeaponId mirrors
        // viewmodels[slot].activeweapon.
        if (_arcWeaponId == int.MinValue)
            _arcWeaponId = Weapons.ByName("arc") is { } arc ? arc.RegistryId : -1;
        bool haveArc = _arcWeaponId >= 0 && _equippedWeaponId == _arcWeaponId;

        if (LocalServerPlayer is { } host)
        {
            // Host / listen-server: the live local Player carries the stats.
            x.NadeTimer = host.NadeTimer;
            x.ReviveProgress = host.ReviveProgress;
            x.CaptureProgress = 0f; // no host producer yet (QC STAT(CAPTURE_PROGRESS) is gametype-set)

            // Bonus-nade readout for the ammo panel (QC STAT(NADE_BONUS)/NADE_BONUS_TYPE/NADE_BONUS_SCORE): banked
            // count, the Nades registry id selecting the icon, and the 0..1 fraction toward the next bonus. Live off
            // the local Player on the host.
            _fullHud.Ammo.NadeBonusCount = host.NadeBonus;
            _fullHud.Ammo.NadeBonusTypeId = host.NadeBonusType;
            _fullHud.Ammo.NadeBonusScoreFrac = host.NadeBonusScore;

            // QC UpdateDamage/HitSound off the live stats. The host CAN follow-spectate (cl_bench_spectate, the
            // `spectate` command), and the server-side EndFrame stamp already mirrors the spectatee's feedback
            // onto this player — so pass the real spectatee id through, or view.qc:907's "drop accumulated
            // damage on a spectatee switch" never fires here.
            if (_hitSound is not null
                && _hitSound.Update(haveArc, _client?.SpectatingNetId ?? 0, host.HitTime,
                                    host.HitsoundDamageDealtTotal, host.TypeHitTime, host.KillTime))
                x.HitFlash = 1f;
            return;
        }

        // Pure remote client: read the networked own-entity slice. No slice yet (pre-spawn) → hide the rings
        // and drop the feedback baselines so the next slice re-seeds silently.
        if (_client is null || _client.LocalState is not { } ls)
        {
            x.NadeTimer = 0f; x.CaptureProgress = 0f; x.ReviveProgress = 0f;
            _fullHud.Ammo.NadeBonusCount = 0; _fullHud.Ammo.NadeBonusTypeId = 0; _fullHud.Ammo.NadeBonusScoreFrac = 0f;

            // FOLLOW-SPECTATOR: QC stamps the spectator's own feedback stats from their spectatee
            // (world.qc:2508 `IS_SPEC(it) ? it.enemy : it`), so watching someone frag sounds like playing.
            // An observer has no own entity in the stream, so read the block off the WATCHED entity — the
            // server keeps it unstripped for exactly this recipient (ServerNet.RelevantEntitiesFor).
            int watched = _client?.SpectatingNetId ?? 0;
            if (_hitSound is not null && watched != 0 && _client is not null
                && _client.TryGetRemoteState(watched, out XonoticGodot.Net.NetEntityState spec))
            {
                if (_hitSound.Update(haveArc, watched, spec.HitTime, spec.HitDamageDealtTotal,
                                     spec.TypeHitTime, spec.KillTime))
                    x.HitFlash = 1f;
                return;
            }

            _hitSound?.Reset();
            return;
        }

        x.NadeTimer = ls.NadeTimer;
        x.CaptureProgress = ls.CaptureProgress;
        x.ReviveProgress = ls.ReviveProgress;

        // Bonus-nade readout from the networked own-entity slice (the Feedback block ships NadeBonus/Type/Score).
        _fullHud.Ammo.NadeBonusCount = ls.NadeBonus;
        _fullHud.Ammo.NadeBonusTypeId = ls.NadeBonusType;
        _fullHud.Ammo.NadeBonusScoreFrac = ls.NadeBonusScore;

        // QC UpdateDamage/HitSound off the networked stats; the spectatee id drops accumulated damage on a
        // spectatee switch (view.qc:907). The first slice seeds baselines silently (mid-match join).
        if (_hitSound is not null
            && _hitSound.Update(haveArc, _client.SpectateeStatus, ls.HitTime, ls.HitDamageDealtTotal,
                                ls.TypeHitTime, ls.KillTime))
            x.HitFlash = 1f;
    }

    // ---- pickup feed (QC HUD_Pickup / STAT(LAST_PICKUP)) ----
    // The port has no networked LAST_PICKUP stat, so on the listen server we detect pickups client-side off the
    // local Player: a NEW weapon in the owned set is an unambiguous pickup, and a per-frame resource JUMP above a
    // threshold regen never reaches is an item pickup. Baselines are (re)seeded silently on (re)spawn so the spawn
    // grant isn't reported. (Pure --connect clients have no local Player → the feed stays empty; a faithful
    // LAST_PICKUP stat would be the cross-client follow-up.)
    private bool _pickupInit;
    private XonoticGodot.Common.Gameplay.WepSet _pickupLastOwned;
    private float _pickupHealth, _pickupArmor, _pickupShells, _pickupBullets, _pickupRockets, _pickupCells;

    private void UpdatePickupFeed()
    {
        if (_fullHud is null)
            return;
        Player? p = LocalServerPlayer;
        if (p is null || p.IsDead || p.IsObserver)
        {
            _pickupInit = false; // re-baseline on (re)spawn so the spawn loadout isn't shown as a pickup
            return;
        }

        XonoticGodot.Common.Gameplay.WepSet owned = p.OwnedWeaponSet;
        float health = p.GetResource(ResourceType.Health);
        float armor = p.GetResource(ResourceType.Armor);
        float shells = p.GetResource(ResourceType.Shells);
        float bullets = p.GetResource(ResourceType.Bullets);
        float rockets = p.GetResource(ResourceType.Rockets);
        float cells = p.GetResource(ResourceType.Cells);

        if (_pickupInit)
        {
            XonoticGodot.Game.Hud.PickupPanel? feed = _fullHud.GetPanel<XonoticGodot.Game.Hud.PickupPanel>();
            // Leading-timer feed (QC HUD_Pickup_Time reads the timer panel's clock): Push samples this ONCE per
            // pickup and freezes the string, so the readout is the match time the pickup HAPPENED — it used to
            // fall back to the panel's running "time since" counter, which visibly ticked up. Wired lazily
            // (idempotent) — the timer panel returns null until its net feed arrives, keeping the fallback.
            if (feed is not null && feed.MatchTimeProvider is null
                && _fullHud.GetPanel<XonoticGodot.Game.Hud.TimerPanel>() is { } timerPanel)
                feed.MatchTimeProvider = timerPanel.PickupTimeString;
            // (QC STAT(LAST_PICKUP) advance → pickup_crosshair_size = 1, crosshair.qc:362-379): a pickup this
            // frame also bumps the crosshair. We derive the same trigger from the client-side pickup detection
            // below (any new weapon or resource jump) and pulse the crosshair once.
            bool pickedUp = false;
            {
                // New weapons — always a genuine pickup (never regen/spawn here, since spawn re-baselines).
                foreach (XonoticGodot.Common.Gameplay.Weapon w in XonoticGodot.Common.Gameplay.Weapons.All)
                    if (owned.Has(w) && !_pickupLastOwned.Has(w))
                    {
                        feed?.Push(string.IsNullOrEmpty(w.DisplayName) ? w.NetName : w.DisplayName,
                                   XonoticGodot.Game.Hud.WeaponHud.IconName(w.NetName));
                        pickedUp = true;
                    }

                // Resource pickups — a per-frame jump above a threshold regen never reaches in one frame
                // (health/armor regen is gradual; the 4 main ammo pools never regen).
                if (health - _pickupHealth >= 3f) { feed?.Push("Health", "health"); pickedUp = true; }
                if (armor - _pickupArmor >= 3f) { feed?.Push("Armor", "armor"); pickedUp = true; }
                if (shells - _pickupShells >= 1f) { feed?.Push("Shells", "ammo_shells"); pickedUp = true; }
                if (bullets - _pickupBullets >= 1f) { feed?.Push("Bullets", "ammo_bullets"); pickedUp = true; }
                if (rockets - _pickupRockets >= 1f) { feed?.Push("Rockets", "ammo_rockets"); pickedUp = true; }
                if (cells - _pickupCells >= 1f) { feed?.Push("Cells", "ammo_cells"); pickedUp = true; }
            }
            if (pickedUp)
                _fullHud.Crosshair.PulsePickup(); // QC crosshair_pickup bump
        }

        _pickupInit = true;
        _pickupLastOwned = owned;
        _pickupHealth = health; _pickupArmor = armor;
        _pickupShells = shells; _pickupBullets = bullets; _pickupRockets = rockets; _pickupCells = cells;
    }

    /// <summary>
    /// Feed the crosshair panel the active weapon slot's live ring stats (QC client/hud/crosshair.qc 471-557).
    /// Reads the host-side <see cref="LocalServerPlayer"/>'s active weapon-slot scratch state — the C# home of
    /// QC's networked <c>wepent.*</c> fields — and maps it onto the panel's ring setters: Vortex charge
    /// (<c>vortex_charge</c> → <see cref="CrosshairPanel.ChargeFraction"/>) + chargepool (<c>vortex_chargepool_ammo</c>
    /// → <see cref="CrosshairPanel.ChargePool"/>); the reload/ammo ring (<c>clip_load</c>/<c>clip_size</c>); the
    /// Hagar load (<c>hagar_load</c>) and Mine Layer count (<c>minelayer_mines</c>) rings; and the Arc overheat
    /// ring (<c>arc_heat_percent</c>). Each stat is reset to its "no data" sentinel when the active weapon isn't
    /// the one that owns it, so the panel only draws the ring for the weapon currently held — exactly like the QC
    /// per-weapon if/else chain. Called unconditionally each frame (the feeder was hoisted out of the host-only
    /// block): on a listen host it reads the live slot state off <see cref="LocalServerPlayer"/>; on a pure remote
    /// / dedicated-server client there is no LocalServerPlayer, so it mirrors the networked owner-block ring
    /// scalars instead (<see cref="ClientNet.LocalWeaponRings"/>, resolved server-side by
    /// <c>ServerNet.ResolveOwnerWeaponRings</c>) — so a remote client draws the same rings the host does.
    /// </summary>
    private void UpdateCrosshairWeaponRings()
    {
        CrosshairPanel x = _fullHud.Crosshair;
        Player? p = LocalServerPlayer;

        // [W-wepent-view smoke consumer] when following another player, read the watched player's networked wepent
        // view-state (charge/clip/heat) off the entity stream and drive the same crosshair rings — previously a
        // spectator saw no rings because the owner-block rings only resolve off the local owner. The owner block
        // (OwnerWeaponRings) is delta-excluded from the entity feed, so for a SPECTATEE we instead pull the
        // per-player WepentView slice that ServerNet networks on every entity; each field maps to the panel's
        // -1/0 'no data' sentinel exactly like the pure-remote owner branch below.
        int specNet = _client?.SpectatingNetId ?? 0;
        if (specNet != 0 && _client != null && _client.TryGetRemoteState(specNet, out var rs))
        {
            var v = rs.WepentView;
            x.ChargeFraction = v.VortexCharge > 0 ? v.VortexCharge : -1f;
            x.ChargePool = v.VortexChargePool > 0 ? v.VortexChargePool : -1f;
            x.ClipLoad = v.ClipSize > 0 ? v.ClipLoad : -1f;
            x.ClipSize = v.ClipSize;
            x.HagarLoad = v.HagarLoad > 0 ? v.HagarLoad : -1f;
            x.HagarLoadMax = 4f;
            x.MineCount = v.MinelayerMines > 0 ? v.MinelayerMines : -1f;
            x.MineLimit = 3f;
            x.ArcHeat = v.ArcHeat > 0 ? v.ArcHeat : -1f;
            return;
        }

        // Pure remote / dedicated-server client: no LocalServerPlayer, so feed the rings from the networked
        // owner-block scalars (ServerNet.ResolveOwnerWeaponRings → OwnerWeaponRings, read by ClientNet). The
        // server already resolved each ring's -1/0 'no data' sentinel for the held weapon, so just mirror them —
        // the same crosshair charge/clip/load/heat rings the listen host shows, now on a remote client too.
        if (p is null)
        {
            XonoticGodot.Net.OwnerWeaponRings rings = _client?.LocalWeaponRings ?? XonoticGodot.Net.OwnerWeaponRings.None;
            x.ChargeFraction = rings.VortexCharge; x.ChargePool = rings.VortexChargePool;
            x.ClipLoad = rings.ClipLoad; x.ClipSize = rings.ClipSize;
            x.HagarLoad = rings.HagarLoad; x.HagarLoadMax = rings.HagarLoadMax;
            x.MineCount = rings.MineCount; x.MineLimit = rings.MineLimit;
            x.ArcHeat = rings.ArcHeat;
            return;
        }

        Weapon? active = Inventory.CurrentWeapon(p);
        if (active is null || p.IsDead || p.IsObserver)
        {
            // Live host owner but no live weapon (dead / observing) → hide every ring (sentinels match the
            // panel's "no data" defaults).
            x.ChargeFraction = -1f; x.ChargePool = -1f;
            x.ClipLoad = -1f; x.ClipSize = 0f;
            x.HagarLoad = -1f; x.MineCount = -1f; x.ArcHeat = -1f;
            return;
        }

        WeaponSlotState st = p.WeaponState(new WeaponSlot(0));
        string net = active.NetName ?? string.Empty;

        // Vortex / Overkill-Nex charge ring (QC 482-496). vortex_charge is already a [0,1] fraction; the inner
        // chargepool ring uses vortex_chargepool_ammo (also [0,1]). Fed only while the charge weapon is active.
        if (net is "vortex" or "vaporizer" || net.Contains("nex"))
        {
            x.ChargeFraction = st.VortexCharge;
            x.ChargePool = st.VortexChargePoolAmmo;
        }
        else
        {
            x.ChargeFraction = -1f;
            x.ChargePool = -1f;
        }

        // Reload / ammo ring (QC 536-548): clip_load / clip_size, drawn for any weapon with a clip.
        if (st.ClipSize > 0)
        {
            x.ClipLoad = st.ClipLoad;
            x.ClipSize = st.ClipSize;
        }
        else
        {
            x.ClipLoad = -1f;
            x.ClipSize = 0f;
        }

        // Hagar burst-load ring (QC 529-535): hagar_load / load_max.
        if (net == "hagar" && active is Hagar hg)
        {
            x.HagarLoad = st.HagarLoad;
            x.HagarLoadMax = hg.Secondary.LoadMax > 0f ? hg.Secondary.LoadMax : x.HagarLoadMax;
        }
        else
        {
            x.HagarLoad = -1f;
        }

        // Mine Layer count ring (QC 522-528): minelayer_mines / limit. Count this player's live mines (the same
        // g_mines scan QC's W_MineLayer_Count does), since the count isn't cached on the slot.
        if (net == "minelayer" && active is Minelayer ml)
        {
            int mines = 0;
            foreach (Entity e in Api.Entities.FindByClass("mine"))
                if (ReferenceEquals(e.Owner, p) && !e.IsFreed) ++mines;
            x.MineCount = mines;
            x.MineLimit = ml.Cvars.Limit > 0 ? ml.Cvars.Limit : x.MineLimit;
        }
        else
        {
            x.MineCount = -1f;
        }

        // Arc overheat ring (QC 474, 550-556 + Arc_GetHeat_Percent arc.qc:55-68). While firing the heat fraction
        // is beam_heat / overheat_max; after release the overheat-jam latch holds the ring until it cools.
        if (net == "arc" && active is Arc arc && arc.Beam.OverheatMax > 0f)
        {
            float now = Api.Clock.Time;
            // QC Arc_GetHeat_Percent (arc.qc:62-68): while a beam is live the ring is beam_heat/overheat_max; after
            // it ends the latched arc_overheat timestamp decays the ring, SCALED by arc_cooldown (the cooldown_speed
            // captured when the beam stopped) so a hot release fades the ring proportionally to its bleed rate.
            float pct = st.BeamHeat > 0f
                ? st.BeamHeat / arc.Beam.OverheatMax
                : (st.ArcOverheat > now ? (st.ArcOverheat - now) / arc.Beam.OverheatMax * st.ArcCooldown : 0f);
            x.ArcHeat = Godot.Mathf.Clamp(pct, 0f, 1f);
        }
        else
        {
            x.ArcHeat = -1f;
        }
    }

    /// <summary>
    /// Feed the in-vehicle HUD (QC <c>Vehicles_drawHUD</c> / <c>Vehicles_drawCrosshair</c> + the
    /// <c>TE_CSQC_VEHICLESETUP</c> dispatch, common/vehicles/cl_vehicles.qc). On the host path the local
    /// <see cref="Player"/> carries the live <c>.vehicle</c> link and the 0..100 stat mirror each descriptor
    /// Think writes onto the pilot (<c>vehicle_health/shield/energy</c>), so this is the client-side feeder
    /// the panel needs: pick the vehicle art set (ConfigureForVehicle), mirror the percentages (scaled to
    /// 0..1, the QC <c>0.01 * STAT(...)</c>), and project the homing-lock target into an auxiliary lock
    /// crosshair (SetAuxiliaryXhairLock). The cross-client VEHICLESTAT_* networking that would feed a pure
    /// remote client is the follow-up; here the host-authoritative pilot state drives the panel directly.
    /// </summary>
    private void UpdateVehicleHud()
    {
        XonoticGodot.Game.Hud.VehicleHud hud = _fullHud.Vehicle;
        Player? p = LocalServerPlayer;
        Entity? veh = p?.Vehicle;

        // No host-side local Player (a pure remote / dedicated-server client, OR a spectator following a pilot):
        // source the panel from the networked VehicleViewState instead — the remote pilot's own-entity slice, or
        // the followed spectatee's entity slice. UpdateVehicleHudRemote drives + returns; only fall through to the
        // host path below when LocalServerPlayer is live.
        if (p is null)
        {
            UpdateVehicleHudRemote(hud);
            return;
        }

        // Not piloting (on foot / observing / dead) → the hud_id == HUD_NORMAL exit case (hides + clears aux).
        if (veh is null || p.IsDead || p.IsObserver)
        {
            if (hud.InVehicle)
                hud.Exit();
            return;
        }

        // A bumblebee GUNNER seats in a gun SLOT entity (ClassName "vehicle_playerslot", VehSlotIndex 1/2), whose
        // owner is the body. QC CSQCVehicleSetup hands the gunner the HUD_BUMBLEBEE_GUN art (CSQC_BUMBLE_GUN_HUD),
        // and bumblebee_gunner_frame mirrors the body health/shield + the gun's cannon ammo onto the gunner.
        bool isGunner = veh.VehSlotIndex != 0 && veh.VehSlotOwner is not null;

        // TE_CSQC_VEHICLESETUP: select the art set from the descriptor NetName (re-shows the panel each time).
        hud.ConfigureForVehicle(isGunner
            ? XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.BumblebeeGun
            : (veh.VehicleDef?.NetName) switch
            {
                "raptor"    => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Raptor,
                "spiderbot" => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Spiderbot,
                "bumblebee" => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Bumblebee,
                _           => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Racer,
            });

        // Mirror the pilot/gunner-side 0..100 percentages to the panel's [0,1] (QC 0.01 * STAT(VEHICLESTAT_*)).
        hud.Health = Godot.Mathf.Clamp(p.VehicleHealth * 0.01f, 0f, 1f);
        hud.Shield = Godot.Mathf.Clamp(p.VehicleShield * 0.01f, 0f, 1f);
        hud.Energy = Godot.Mathf.Clamp(p.VehicleEnergy * 0.01f, 0f, 1f);
        hud.Ammo1  = Godot.Mathf.Clamp(p.VehicleAmmo1 * 0.01f, 0f, 1f);
        hud.Ammo2  = Godot.Mathf.Clamp(p.VehicleAmmo2 * 0.01f, 0f, 1f);
        // Mirror BOTH reload bars (QC 0.01 * STAT(VEHICLESTAT_RELOAD1/2)) — the NE bar falls back to Reload1 when
        // Ammo1 is empty (DrawClippedBar: Ammo1 > 0 ? Ammo1 : Reload1), so leaving Reload1 stale on the host path
        // made the host pilot's empty-reload bar diverge from the remote pilot's (which mirrors both). Keep them in
        // lockstep with the resolver / UpdateVehicleHudRemote so host and pure-remote pilots draw identically.
        hud.Reload1 = Godot.Mathf.Clamp(p.VehicleReload1 * 0.01f, 0f, 1f);
        hud.Reload2 = Godot.Mathf.Clamp(p.VehicleReload2 * 0.01f, 0f, 1f);

        if (isGunner)
        {
            // GUNNER aux crosshairs (QC bumblebee_gunner_frame UpdateAuxiliaryXhair): the magenta '1 0 1' LEAD
            // marker (aux slot 1) at the predicted impact, and the reload-colored READY marker (aux slot 0) at
            // the cannon's straight hit. The gun slot carries the live world points the per-frame controller wrote.
            // QC bumblebee vr_setup: aux slot 1 = vCROSS_BURST (gunner), slot 0 = vCROSS_LOCK (raygun-locked, reused
            // here for the gunner's own straight-fire READY marker). Magenta '1 0 1' lead + reload-colored ready.
            if (veh.VehGunnerLeadValid)
                hud.SetAuxiliaryXhair(1, Coords.ToGodot(veh.VehGunnerLeadPoint), new Godot.Color(1f, 0f, 1f),
                    "gfx/vehicles/crosshair_burst");
            else
                hud.ClearAuxiliaryXhair(1);
            if (veh.VehGunnerHitValid)
                hud.SetAuxiliaryXhair(0, Coords.ToGodot(veh.VehGunnerHitPoint), ReloadColor(p.VehicleReload1),
                    "gfx/vehicles/crosshair_lock");
            else
                hud.ClearAuxiliaryXhair(0);
            return;
        }

        // Auxiliary lock-on crosshair (AuxiliaryXhair): the homing-lock target the vehicle is building/holding,
        // projected from its Quake-space origin into Godot and tinted red→yellow→green by VehLockStrength.
        if (veh.VehLockTarget is { } target && !target.IsFreed)
            // QC bumblebee vr_setup: aux slot 0 = vCROSS_LOCK for the raygun heal-lock marker.
            hud.SetAuxiliaryXhairLock(0, Coords.ToGodot(target.Origin), veh.VehLockStrength,
                veh.VehicleDef is Bumblebee ? "gfx/vehicles/crosshair_lock" : "gfx/vehicles/axh-target");
        else
            hud.ClearAuxiliaryXhair(0);

        // PILOT mirror of the two gunners' READY aux crosshairs (QC bumblebee_gunner_frame line 186:
        // UpdateAuxiliaryXhair(vehic.owner, ..., slot 1 for gunner1 / slot 2 for gunner2)). When a side-gun is
        // crewed the pilot sees that gunner's straight-fire marker too.
        FeedPilotGunnerAux(hud, veh.VehGun1, 1);
        FeedPilotGunnerAux(hud, veh.VehGun2, 2);

        // QC bumblebee vr_hud (bumblebee.qc:977-987): the pilot's blinking "No right/left gunner!" prompts, shown
        // while a side-gun seat is unmanned (the QC test is `!AuxiliaryXhair[1/2].draw2d` — no gunner aux crosshair
        // this frame, i.e. the gun has no seated player). Only the bumblebee draws these.
        bool bumble = veh.VehicleDef is Bumblebee;
        hud.ShowNoRightGunner = bumble && veh.VehGun1?.VehSlotPlayer is null;
        hud.ShowNoLeftGunner  = bumble && veh.VehGun2?.VehSlotPlayer is null;

        // Centered main reticle + bomb dropmark (QC vr_crosshair). The raptor draws a per-secondary-mode reticle
        // (RSM_BOMB → vCROSS_BURST, RSM_FLARE → vCROSS_RAIN) plus, in bomb mode, a tracetoss-predicted bomb-impact
        // dropmark; the spiderbot draws a per-rocket-mode reticle (SBRM_VOLLY → vCROSS_BURST, SBRM_GUIDE →
        // vCROSS_GUIDE, SBRM_ARTILLERY → vCROSS_RAIN, spiderbot.qc vr_crosshair). Pure presentation; the mode is
        // server-authoritative (veh.VehW2Mode). FeedRaptorReticle clears the centered reticle for non-raptors, so
        // FeedSpiderbotReticle runs after it and sets the spiderbot's (and is a no-op for the other vehicles).
        FeedRaptorReticle(hud, veh, p);
        FeedSpiderbotReticle(hud, veh);
    }

    /// <summary>
    /// Remote/spectator twin of <see cref="UpdateVehicleHud"/>: drives the vehicle HUD from the networked
    /// <see cref="VehicleViewState"/> rather than a host-side <see cref="Player"/>. Source: the followed
    /// spectatee's entity slice (<c>SpectatingNetId</c> → <c>TryGetRemoteState(...).VehicleView</c>, mirroring the
    /// wepent spectatee branch in <see cref="UpdateCrosshairWeaponRings"/>) when spectating, else the local
    /// client's own-entity slice (<c>ClientNet.LocalState.VehicleView</c>) for a pure remote pilot. When the block
    /// is inactive (VehKind 0 = on foot / observing) the panel exits. The lock-target world position is NOT
    /// networked, so the aux lock crosshair is cleared (host-only nicety); the reticle + bars + strength still draw.
    /// </summary>
    private void UpdateVehicleHudRemote(XonoticGodot.Game.Hud.VehicleHud hud)
    {
        // Pick the source VehicleViewState: the followed spectatee's (entity slice) while spectating, else the
        // local client's own-entity slice (the remote pilot). Default to None when neither is available.
        XonoticGodot.Net.VehicleViewState v = XonoticGodot.Net.VehicleViewState.None;
        int specNet = _client?.SpectatingNetId ?? 0;
        if (specNet != 0 && _client != null && _client.TryGetRemoteState(specNet, out var rs))
            v = rs.VehicleView;
        else if (_client?.LocalState is { } ls)
            v = ls.VehicleView;

        // Inactive (on foot / observing) → the HUD_NORMAL exit case.
        if (!v.IsActive)
        {
            if (hud.InVehicle)
                hud.Exit();
            return;
        }

        // TE_CSQC_VEHICLESETUP: select the art set from the networked vehicle id (re-shows the panel). VehKind
        // mirrors the QC hud id: 2 raptor, 3 spiderbot, 4 bumblebee, 5 bumblebee-gun (gunner); 1/other = racer.
        hud.ConfigureForVehicle(v.VehKind switch
        {
            2 => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Raptor,
            3 => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Spiderbot,
            4 => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Bumblebee,
            5 => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.BumblebeeGun,
            _ => XonoticGodot.Game.Hud.VehicleHud.VehicleHudKind.Racer,
        });

        // The wire block already carries the [0,1] bars (VehicleViewState fields are pre-scaled), so mirror them
        // straight onto the panel — no 0.01 * STAT(...) scaling like the host path.
        hud.Health  = Godot.Mathf.Clamp(v.Health, 0f, 1f);
        hud.Shield  = Godot.Mathf.Clamp(v.Shield, 0f, 1f);
        hud.Energy  = Godot.Mathf.Clamp(v.Energy, 0f, 1f);
        hud.Ammo1   = Godot.Mathf.Clamp(v.Ammo1, 0f, 1f);
        hud.Ammo2   = Godot.Mathf.Clamp(v.Ammo2, 0f, 1f);
        hud.Reload1 = Godot.Mathf.Clamp(v.Reload1, 0f, 1f);
        hud.Reload2 = Godot.Mathf.Clamp(v.Reload2, 0f, 1f);

        // The lock-target identity/world position is not on the wire (only LockTargetValid + LockStrength), so the
        // precise aux lock crosshair can't be projected on the remote path — clear it (host-only nicety). The
        // gunner side-aux markers are likewise host-only; the remote view keeps the reticle, bars and strength.
        hud.ClearAuxiliaryXhair(0);
        hud.ClearAuxiliaryXhair(1);
        hud.ClearAuxiliaryXhair(2);
        hud.ShowNoRightGunner = false;
        hud.ShowNoLeftGunner = false;

        // Centered per-mode reticle from the networked vehicle id + weapon-2 sub-mode (no host Entity, so the live
        // tracetoss bomb dropmark is suppressed — the remote path draws the reticle only, never the green live mark).
        FeedRemoteReticle(hud, v);
    }

    /// <summary>Drive the centered vehicle reticle on the REMOTE/spectator path from the networked
    /// <see cref="VehicleViewState"/> (no host Entity available). Mirrors the per-mode reticle selection of
    /// <see cref="FeedRaptorReticle"/>/<see cref="FeedSpiderbotReticle"/> off <c>VehKind</c> + <c>W2Mode</c>, but
    /// the live bomb-dropmark prediction (which needs the vehicle entity for tracetoss) is suppressed — only the
    /// reticle draws. <c>DropmarkPredictReady</c> is the networked "bombs ready" flag, kept for parity of intent.</summary>
    private static void FeedRemoteReticle(XonoticGodot.Game.Hud.VehicleHud hud, in XonoticGodot.Net.VehicleViewState v)
    {
        // The remote path never draws the live green dropmark (tracetoss needs the server-side vehicle entity).
        hud.DropmarkActive = false;
        hud.DropmarkLive = false;

        switch (v.VehKind)
        {
            case 2: // raptor — QC vr_crosshair: RSM_FLARE (2) → vCROSS_RAIN; RSM_BOMB (1)/default → vCROSS_BURST.
                hud.MainReticle = v.W2Mode == (int)RaptorMode.Flare
                    ? "gfx/vehicles/crosshair_rain"
                    : "gfx/vehicles/crosshair_burst";
                break;
            case 3: // spiderbot — SBRM_VOLLY → vCROSS_BURST; SBRM_ARTILLERY → vCROSS_RAIN; SBRM_GUIDE (default) → vCROSS_GUIDE.
                hud.MainReticle = v.W2Mode switch
                {
                    (int)SpiderbotRocketMode.Volley    => "gfx/vehicles/crosshair_burst",
                    (int)SpiderbotRocketMode.Artillery => "gfx/vehicles/crosshair_rain",
                    _                                  => "gfx/vehicles/crosshair_guide",
                };
                break;
            case 4: // bumblebee pilot — the centered vCROSS_HEAL heal-gun pointer (QC bumblebee vr_crosshair).
                hud.MainReticle = "gfx/vehicles/crosshair_heal";
                break;
            default: // racer / bumblebee-gun gunner — no centered per-mode reticle.
                hud.MainReticle = "";
                break;
        }
    }

    /// <summary>
    /// Port of the spiderbot <c>vr_crosshair</c> client crosshair (common/vehicles/vehicle/spiderbot.qc): pick the
    /// centered reticle by the active rocket mode — SBRM_VOLLY → vCROSS_BURST, SBRM_GUIDE → vCROSS_GUIDE,
    /// SBRM_ARTILLERY → vCROSS_RAIN. No-op for the other vehicles (their reticle is left as the raptor/empty
    /// feeder set it). The spiderbot has no bomb dropmark, so this only drives the centered reticle.
    /// </summary>
    private void FeedSpiderbotReticle(XonoticGodot.Game.Hud.VehicleHud hud, Entity veh)
    {
        if (veh.VehicleDef is not Spiderbot)
            return;

        // QC vr_crosshair: SBRM_VOLLY (1) → vCROSS_BURST; SBRM_GUIDE (2) → vCROSS_GUIDE; SBRM_ARTILLERY (3) →
        // vCROSS_RAIN. veh.VehW2Mode is the server-authoritative SpiderbotRocketMode (default SBRM_GUIDE on enter).
        hud.MainReticle = veh.VehW2Mode switch
        {
            (int)SpiderbotRocketMode.Volley    => "gfx/vehicles/crosshair_burst",
            (int)SpiderbotRocketMode.Artillery => "gfx/vehicles/crosshair_rain",
            _                                  => "gfx/vehicles/crosshair_guide", // SBRM_GUIDE (default)
        };
        hud.DropmarkActive = false; // the spiderbot has no bomb dropmark
    }

    /// <summary>
    /// Port of the raptor <c>vr_crosshair</c> client crosshair (raptor.qc): pick the centered reticle by the
    /// secondary fire mode and, in bomb mode, project the tracetoss bomb-impact dropmark. For other vehicles it
    /// sets the bumblebee pilot's vCROSS_HEAL pointer and clears the reticle for the rest (the spiderbot's
    /// per-mode reticle is set by the FeedSpiderbotReticle pass the caller runs afterward).
    /// </summary>
    private void FeedRaptorReticle(XonoticGodot.Game.Hud.VehicleHud hud, Entity veh, Player p)
    {
        if (veh.VehicleDef is not Raptor)
        {
            // QC bumblebee vr_crosshair (bumblebee.qc:989): the PILOT draws a centered vCROSS_HEAL pointer
            // (gfx/vehicles/crosshair_heal) — the heal-gun aiming reticle. It is shown for the bumblebee body
            // pilot only (a seated gunner uses its own slot HUD, handled by the isGunner branch above and never
            // reaches here). Damage-mode (g_vehicle_bumblebee_raygun 1, non-default) keeps the same reticle in
            // Base — only the BEAM colour changes — so the pointer is unconditional. The shared
            // Vehicles_drawCrosshair colorize path (cl_vehicles_crosshair_colorize) tints it like every vehicle
            // reticle, matching QC. No bomb dropmark for the bumblebee. (The spiderbot's per-mode reticle is set
            // by FeedSpiderbotReticle, which the caller runs after this clears it.)
            hud.MainReticle = veh.VehicleDef is Bumblebee ? "gfx/vehicles/crosshair_heal" : "";
            hud.DropmarkActive = false;
            return;
        }

        // QC vr_crosshair: RSM_FLARE (2) → vCROSS_RAIN; RSM_BOMB (1)/default → vCROSS_BURST.
        bool flareMode = veh.VehW2Mode == (int)RaptorMode.Flare;
        hud.MainReticle = flareMode ? "gfx/vehicles/crosshair_rain" : "gfx/vehicles/crosshair_burst";

        // QC: the dropmark predictor runs only in bomb mode (weapon2mode != RSM_FLARE) and not while spectating.
        bool spectating = (_client?.SpectateeStatus ?? 0) != 0;
        if (flareMode || spectating)
        {
            hud.DropmarkActive = false;
            return;
        }

        // QC dropmark: when reload2 == 1 (bombs ready) run the live tracetoss prediction (green); otherwise hold
        // the last predicted impact (red, larger) while dropmark.cnt > time (the 5s linger window after a drop).
        float reload2 = Godot.Mathf.Clamp(p.VehicleReload2 * 0.01f, 0f, 1f);
        if (reload2 >= 1f)
        {
            _raptorDropmarkLast = VehiclePhysics.BombDropPredict(veh); // Quake space
            hud.DropmarkWorld = Coords.ToGodot(_raptorDropmarkLast);
            hud.DropmarkLive = true;
            hud.DropmarkActive = true;
            _raptorDropmarkLinger = _renderClock + 5f; // QC dropmark.cnt = time + 5
        }
        else if (_renderClock < _raptorDropmarkLinger)
        {
            // The last live prediction lingers as the red "where the dropped bombs are headed" marker.
            hud.DropmarkWorld = Coords.ToGodot(_raptorDropmarkLast);
            hud.DropmarkLive = false;
            hud.DropmarkActive = true;
        }
        else
        {
            hud.DropmarkActive = false;
        }
    }

    // Raptor bomb dropmark linger (QC dropmark.cnt = time + 5): the render-clock time the red post-drop marker
    // expires, and the last live impact point it freezes on.
    private float _raptorDropmarkLinger;
    private System.Numerics.Vector3 _raptorDropmarkLast;

    /// <summary>Project a crewed side-gun's READY aux crosshair onto the PILOT's HUD (QC bumblebee.qc:186); clears
    /// the slot when the gun is empty/idle.</summary>
    private void FeedPilotGunnerAux(XonoticGodot.Game.Hud.VehicleHud hud, Entity? gun, int slot)
    {
        // QC bumblebee vr_setup: the pilot's aux slots 1/2 are vCROSS_BURST (gunner1/gunner2).
        if (gun is { VehGunnerHitValid: true, VehSlotPlayer: not null })
            hud.SetAuxiliaryXhair(slot, Coords.ToGodot(gun.VehGunnerHitPoint),
                ReloadColor(gun.VehSlotPlayer.VehicleReload1), "gfx/vehicles/crosshair_burst");
        else
            hud.ClearAuxiliaryXhair(slot);
    }

    /// <summary>QC bumblebee_gunner_frame reload tint: <c>'1 0 0' * reload1 + '0 1 0' * (1 - reload1)</c> — red while
    /// reloading, green when ready to fire.</summary>
    private static Godot.Color ReloadColor(float reload1)
    {
        float r = Godot.Mathf.Clamp(reload1, 0f, 1f);
        return new Godot.Color(r, 1f - r, 0f);
    }

    // QC IntermissionThink autoscreenshot state: the +0.1s arm timer and the once-per-match latch (QC's
    // `this.autoscreenshot` field, set to -1 after firing). Reset when intermission ends so the next match re-arms.
    private float _autoscreenshotAt = -1f;
    private bool _autoscreenshotTaken;

    /// <summary>
    /// QC IntermissionThink autoscreenshot: when the match enters intermission and the player opted in
    /// (<c>cl_autoscreenshot</c> with the server's <c>sv_autoscreenshot</c>, or <c>cl_autoscreenshot 2</c>
    /// unconditionally), capture the end-of-match scoreboard once. Base arms a +0.1s timer in FixIntermissionClient
    /// and fires the <c>screenshot</c> in IntermissionThink; reproduced here off the networked intermission flag,
    /// routed through the same <c>screenshot</c> command the F12 bind uses (<see cref="RunCommand"/>).
    /// </summary>
    private void UpdateIntermissionAutoscreenshot()
    {
        if (_client is null)
            return;

        if (!_client.MatchIntermission)
        {
            // Match is live (or returned to play): disarm so the NEXT intermission re-rolls the one-shot.
            _autoscreenshotAt = -1f;
            _autoscreenshotTaken = false;
            return;
        }

        if (_autoscreenshotTaken)
            return;

        // Arm on the first intermission frame (QC autoscreenshot = time + 0.1) using the render clock.
        if (_autoscreenshotAt < 0f)
        {
            _autoscreenshotAt = _renderClock + 0.1f;
            return;
        }
        if (_renderClock < _autoscreenshotAt)
            return;

        _autoscreenshotTaken = true; // QC sets this.autoscreenshot = -1 (fire once)

        // Opt-in gate (QC server_screenshot || client_screenshot). cl_autoscreenshot is the local client cvar;
        // sv_autoscreenshot is the server cvar (readable directly on a listen server — it isn't networked, so a
        // pure remote client only honours the cl_autoscreenshot==2 unconditional branch, matching the stock 0 default).
        XonoticGodot.Common.Services.ICvarService cv = Api.Cvars;
        float cl = CvarOr(cv, "cl_autoscreenshot", 0f);
        float sv = _serverWorld is not null ? _serverWorld.Services.Cvars.GetFloat("sv_autoscreenshot") : 0f;
        bool serverShot = sv != 0f && cl != 0f;
        bool clientShot = cl == 2f;
        if (!serverShot && !clientShot)
            return;

        // QC stuffcmd("screenshot screenshots/autoscreenshot/<map>-<matchid>.jpg"). matchid isn't networked here,
        // so disambiguate with a timestamp instead; routes through the shared `screenshot` command (RunCommand).
        string stamp = System.DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string map = string.IsNullOrEmpty(_map) ? "map" : _map;
        RunCommand?.Invoke($"screenshot screenshots/autoscreenshot/{map}-{stamp}.jpg");
    }

    /// <summary>Drain the networked match clock (NetControl.MatchState → ClientNet) into the TIMER panel each
    /// frame: GAMESTARTTIME / TIMELIMIT*60 / warmup, with "now" on the same server clock (LatestServerTime).
    /// The panel self-blanks until the first MatchState arrives, so this is safe to call every frame.</summary>
    private void UpdateMatchClock()
    {
        if (_fullHud is null || _client is null || !_client.HasMatchState)
            return;
        TimerPanel t = _fullHud.Timer;
        t.Now = _client.LatestServerTime;
        t.MatchStartTime = _client.MatchStartTime;
        t.TimeLimitSeconds = _client.MatchTimeLimit;
        t.WarmupStage = _client.MatchWarmup;
        t.WarmupTimeLimitSeconds = _client.MatchWarmupLimit;
        // QC STAT(OVERTIMES): the persistent "Overtime #N" / "Sudden Death" subtext + count-up past the limit.
        // Networked via the MatchState packet (ClientNet.MatchOvertimes); the one-shot center notifications are
        // delivered separately by the overtime cascade.
        t.Overtimes = _client.MatchOvertimes;
        // [T69] feed the dynamic HUD shake's intermission gate: Base suppresses the low-health screen shake on the
        // end-of-match (intermission) screen. Mirror the live flag each frame (clears back to false when the next
        // match leaves intermission). Without this the shake still works in normal play but keeps shaking on the
        // scoreboard screen.
        _fullHud.Intermission = _client.MatchIntermission;
        // Free-fly observer gate for the physics/strafehud "even observing" show-modes (hud_panel_<id> 1/3 hide
        // while observing, 2/4 keep showing). QC spectatee_status == -1 → IsObserving (not following anyone).
        _fullHud.Observing = _client.IsObserving;
        if (_client.MatchIntermission)
            t.IntermissionTime = _client.LatestServerTime;
    }

    /// <summary>Feed the HUD's <c>hud_dynamic_follow</c> effect (QC <c>Hud_Dynamic_Frame</c> follow block) the
    /// live viewmodel follow offset (QC <c>cl_followmodel_ofs</c>). The HUD self-gates on the cvar (off by
    /// default), so this is safe to call every frame; when the viewmodel is gone we feed zero (no sway).</summary>
    private void UpdateHudDynamicFollow()
    {
        if (_fullHud is null)
            return;
        Vector3 ofs = _viewModel is not null && GodotObject.IsInstanceValid(_viewModel)
            ? _viewModel.LastFollowOffset
            : Vector3.Zero;
        _fullHud.FollowModelOffset = new System.Numerics.Vector3(ofs.X, ofs.Y, ofs.Z);
    }

    /// <summary>
    /// Feed the VOTE panel (QC HUD_Vote) the live call-vote state each frame. On a listen server the in-process
    /// <see cref="VoteController"/> (GameWorld.Voting) carries it directly — Active / Display / yes-no tallies /
    /// the threshold / the local player's own ballot (the already-voted dim + side highlight) / the timeout. The
    /// panel self-blanks when no vote is active, so this is safe to call every frame. On a pure remote client
    /// there is no in-process world (no vote net channel yet) → the panel stays blank (an honest gap).
    /// </summary>
    private void UpdateVotePanel()
    {
        if (_fullHud is null)
            return;
        XonoticGodot.Game.Hud.VotePanel v = _fullHud.Vote;
        VoteController? vote = _serverWorld?.Voting;
        if (vote is null || !vote.Active)
        {
            if (v.Active) v.Active = false;        // arm the fade-out when the vote just ended
            // Hold the panel Visible for one fade window (0.5s) after the vote clears so the 0.5s fade-out plays,
            // then hide it (the panel self-blanks at alpha 0 in the meantime).
            if (v.Visible)
            {
                _voteHideAt = _voteHideAt < 0.0 ? _client?.LatestServerTime + 0.6 ?? -1.0 : _voteHideAt;
                if (_voteHideAt >= 0.0 && (_client?.LatestServerTime ?? 0.0) >= _voteHideAt)
                { v.Visible = false; _voteHideAt = -1.0; }
            }
            return;
        }
        _voteHideAt = -1.0; // a live vote cancels any pending hide

        v.CalledVote = vote.Display;
        v.YesCount = vote.YesCount;
        v.NoCount = vote.NoCount;
        v.Needed = vote.NeededOverall;
        v.EndTime = vote.EndTime;
        // QC vote_highlighted: the local player's ballot (+1 yes / -1 no / 0 not-voted; abstain reads as not-voted
        // for the yes/no highlight). Drives the already-voted dim + the chosen-side highlight.
        Player? me = LocalServerPlayer;
        int sel = me is not null ? vote.SelectionOf(me) : VoteController.SelectNull;
        v.Highlighted = sel == VoteController.SelectAccept ? 1 : sel == VoteController.SelectReject ? -1 : 0;
        v.Active = true; // set last so the fade-in only arms on the true→false→true transition
        if (!v.Visible) v.Visible = true;
    }

    /// <summary>
    /// Feed the MAP-VOTE panel (QC mapvoting.qc MapVote_Draw) the end-of-match map ballot each frame. On a listen
    /// server the in-process <see cref="MapVoting"/> (GameWorld.MapVote) drives it: the candidate maps + live
    /// vote counts + the local player's own vote + the running countdown, and the winner reveal once decided. The
    /// panel is in <c>StartHiddenIds</c>, so its Visible is owned here (shown while the vote runs/reveals).
    /// </summary>
    private void UpdateMapVotePanel()
    {
        if (_fullHud is null)
            return;
        XonoticGodot.Game.Hud.MapVotePanel mv = _fullHud.MapVote;
        // Normalize the ballot source so the SAME render runs for a listen host and a remote client. Listen host:
        // the in-process MapVoting. Remote client (no server world): the networked ballot decoded by
        // ClientNet.HandleMapVote — without this a --connect client saw no ballot at all (the panel was fed only
        // from _serverWorld.MapVote, which is null remotely).
        MapVoting? vote = _serverWorld?.MapVote;
        NetMapVote? net = vote is null ? _client?.MapVote : null;

        var cands = new System.Collections.Generic.List<MvCand>();
        bool showing, isGametypeVote, detail, abstainPresent;
        float timeout;
        int ownVote, winnerSlot;                    // winnerSlot: 1-based winning cell (abstain-stripped), 0 = none

        if (vote is not null)
        {
            showing = vote.Running || (vote.Finished && !string.IsNullOrEmpty(vote.WinningMap));
            isGametypeVote = vote.IsGametypeVote;
            detail = Cvars.Bool("sv_vote_gametype_detail");
            timeout = vote.Timeout;
            abstainPresent = false;
            foreach (MapVoteCandidate c in vote.Candidates)
            {
                if (c.IsAbstain) abstainPresent = true;
                cands.Add(new MvCand(c.MapName, c.IsAbstain, c.Votes, c.Available, c.Suggester));
            }
            int own = vote.SelectionOf(LocalServerPlayer);
            ownVote = (own >= 0 && own < vote.Candidates.Count && !vote.Candidates[own].IsAbstain) ? own : -1;
            winnerSlot = 0;
            if (vote.Finished && !string.IsNullOrEmpty(vote.WinningMap))
            {
                int slot = 0;
                foreach (MapVoteCandidate c in vote.Candidates)
                {
                    if (c.IsAbstain) continue;
                    slot++;
                    if (string.Equals(c.MapName, vote.WinningMap, System.StringComparison.OrdinalIgnoreCase)) { winnerSlot = slot; break; }
                }
            }
        }
        else if (net is not null)
        {
            showing = net.Showing;
            isGametypeVote = net.IsGametypeVote;
            detail = net.Detail;
            timeout = net.Remaining;
            abstainPresent = net.AbstainPresent;
            foreach (NetMapVote.Cand c in net.Candidates)
                cands.Add(new MvCand(c.MapName, false, c.Votes, c.Available, c.Suggester));
            ownVote = net.Own;                       // already an abstain-stripped cell index (-1 = none/abstain)
            winnerSlot = net.Finished ? net.Winner1Based : 0;
        }
        else
        {
            if (mv.Visible) { mv.Visible = false; mv.Active = false; }
            return;
        }

        if (!showing)
        {
            if (mv.Visible) { mv.Visible = false; mv.Active = false; }
            return;
        }

        // (Re)build the candidate list only when the ballot identity changes (count / first map / running flag),
        // then update the live counts in place each frame so SetVote's per-cell fade state isn't reset every frame.
        string sig = $"{cands.Count}|{(cands.Count > 0 ? cands[0].MapName : "")}|{timeout:0.0}|{isGametypeVote}";
        if (sig != _mapVoteSig)
        {
            _mapVoteSig = sig;
            var list = new System.Collections.Generic.List<XonoticGodot.Game.Hud.MapVotePanel.Candidate>(cands.Count);
            for (int i = 0; i < cands.Count; i++)
            {
                MvCand c = cands[i];
                if (c.IsAbstain) continue; // abstain is rendered as its own row, not a cell
                string pic;
                if (isGametypeVote)
                    // QC GameTypeVote_DrawGameTypeItem: the icon is gfx/menu/<menu_skin>/gametype_<name>. There is
                    // no "default" skin dir in the shipped assets (skins are luma/luminos/wickedx/xaw) — using it
                    // would always miss and fall back to the nopreview placeholder. Track the live skin (luma by
                    // default, which carries the icons), mirroring Base's per-skin path.
                    pic = $"gfx/menu/{XonoticGodot.Game.Hud.HudSkin.SkinName}/gametype_{c.MapName}";
                else
                    pic = string.IsNullOrEmpty(c.MapName) ? "" : $"maps/{c.MapName}";
                // QC ReadGameTypeVote (client/mapvoting.qc:762-780): the gametype-vote title + description split
                // on the GTV_CUSTOM flag. A REAL (built-in) gametype shows MapInfo_Type_ToText (the gametype's
                // pretty .message, e.g. "Deathmatch") + MapInfo_Type_Description (its built-in description); only a
                // CUSTOM gametype (an alias that doesn't resolve to a built-in type) reads the per-name
                // sv_vote_gametype_<name>_name / _description cvars (falling back to the ballot entry name when the
                // name cvar is empty, exactly like Base). The previous port read the _name/_description cvars for
                // EVERY option, so stock ballots (dm/tdm/ca/ctf — which set neither cvar) showed the raw "dm"
                // entry name and an empty description instead of "Deathmatch" + its blurb.
                string data;
                string desc;
                if (isGametypeVote)
                {
                    XonoticGodot.Common.Gameplay.GameType? gt =
                        XonoticGodot.Common.Gameplay.GameTypes.ByName(c.MapName);
                    if (gt is not null)
                    {
                        // QC MapInfo_Type_Description (client/mapvoting.qc:767): for a real built-in gametype the
                        // picker shows its MenuDescription prose (e.g. Deathmatch's 3-paragraph guide text from
                        // deathmatch.qc:describe). A gametype that has not yet ported its describe() returns null,
                        // which falls back to an empty string matching the pre-port behavior.
                        data = string.IsNullOrEmpty(gt.DisplayName) ? c.MapName : gt.DisplayName;
                        desc = gt.MenuDescription ?? "";
                    }
                    else
                    {
                        // Custom alias: per-name cvars, name falling back to the ballot entry (QC custom branch).
                        string gtName = Cvars.String($"sv_vote_gametype_{c.MapName}_name");
                        data = string.IsNullOrEmpty(gtName) ? c.MapName : gtName;
                        desc = Cvars.String($"sv_vote_gametype_{c.MapName}_description");
                    }
                }
                else
                {
                    data = c.MapName;
                    desc = "";
                }
                list.Add(new XonoticGodot.Game.Hud.MapVotePanel.Candidate(
                    c.MapName, c.Votes, pic, data, desc, c.Available, c.Suggester));
            }
            mv.SetVote(list, timeout, gametypeVote: isGametypeVote, abstain: abstainPresent);
            // QC sv_vote_gametype_detail: honour the gametype-vote detail flag (from the wire on a remote client).
            if (isGametypeVote)
                mv.Detail = detail;
        }

        // Live counts + availability each frame (QC MapVote_UpdateVotes), excluding the abstain slot.
        // Build the availability list FIRST then call SetAvailability before SetVotes, so the panel's
        // _top2Time reduce-fade clock is stamped when an option first becomes unavailable (QC mv_top2_alpha).
        var avail = new System.Collections.Generic.List<bool>(cands.Count);
        var counts = new System.Collections.Generic.List<int>(cands.Count);
        foreach (MvCand c in cands)
        {
            if (!c.IsAbstain)
            {
                avail.Add(c.Available);
                counts.Add(c.Available ? c.Votes : -1);
            }
        }
        mv.SetAvailability(avail); // stamps _top2Time on first reduce (before SetVotes overwrites availability)
        mv.SetVotes(counts);

        // The local player's own vote (QC mv_ownvote) + the decided winner slot — both normalized above from the
        // listen host's MapVoting or the networked ballot (abstain-stripped cell indices, so they line up 1:1 with
        // the panel cells; -1 / 0 = none).
        mv.OwnVote = ownVote;
        if (winnerSlot > 0 && mv.Winner != winnerSlot) mv.SetWinner(winnerSlot);

        mv.Active = true;
        if (!mv.Visible) mv.Visible = true;
    }
    /// <summary>A normalized map/gametype-vote ballot cell — the shared shape the map-vote panel renders, built
    /// from either the listen host's <see cref="MapVoteCandidate"/> or a remote client's <see cref="NetMapVote.Cand"/>.
    /// Member names match <see cref="MapVoteCandidate"/> so the render body reads identically for both sources.</summary>
    private readonly record struct MvCand(string MapName, bool IsAbstain, int Votes, bool Available, string Suggester);
    private string _mapVoteSig = "";
    private double _voteHideAt = -1.0;

    /// <summary>
    /// Feed the RACE-TIMER panel (QC racetimer.qc HUD_RaceTimer) + the Checkpoints panel (checkpoints.qc) the
    /// live race state each frame in Race/CTS. On a listen server the active <see cref="Race"/> gametype carries
    /// the local racer's <c>RaceState</c> and the per-checkpoint record store (QC race_checkpoint_records[]): the
    /// lap count-up clock, the next checkpoint + its record (the anticipation delta), the most-recent checkpoint
    /// split + speed (the frozen "Checkpoint N (+/-delta)" line + the split list), and the penalty accumulator.
    /// This is the host analogue of the TE_CSQC_RACE net feed (QC race_SendTime) — the records are now kept
    /// server-side (Race.RecordCheckpointSplit), so the panels render live instead of staying blank. The panel
    /// self-blanks outside a lap.
    /// </summary>
    private void UpdateRacePanels()
    {
        if (_fullHud is null)
            return;
        XonoticGodot.Game.Hud.RaceTimerPanel rt = _fullHud.RaceTimer;
        XonoticGodot.Game.Hud.CheckpointsPanel cps = _fullHud.Checkpoints;
        Player? me = LocalServerPlayer;
        XonoticGodot.Common.Gameplay.GameType? gt = _serverWorld?.GameType;
        var race = gt as XonoticGodot.Common.Gameplay.Race;
        var cts = gt as XonoticGodot.Common.Gameplay.Cts;
        bool active = (race is not null || cts is not null) && me is not null && !_client!.IsObserving;
        if (!active)
        {
            if (rt.Visible) rt.Visible = false;
            if (cps.Visible) cps.Visible = false;
            _lastFedCheckpoint = -2;
            return;
        }

        rt.Now = _client!.LatestServerTime;
        rt.Observing = false;
        if (race is not null)
        {
            XonoticGodot.Common.Gameplay.Race.RaceState st = race.GetState(me!);
            rt.RaceLapTime = st.LapStartTime;              // QC race_laptime: the lap-start baseline (count-up clock)
            rt.RaceNextCheckpoint = st.NextCheckpoint < 0 ? 0 : st.NextCheckpoint;
            rt.RacePenaltyAccumulator = st.PenaltyAccumulator * 10f; // panel reads tenths; RaceState keeps seconds

            // QC RACE_NET_PENALTY_RACE / _QUALIFYING → racetimer.qc penalty line: show "PENALTY: Ns (reason)" for the
            // ~2 s fade window after a penalty was imposed (panel reads tenths). Fades out on its own past the window.
            if (st.LastPenaltyEventTime > 0f && (rt.Now - st.LastPenaltyEventTime) < 2.0)
            {
                rt.RacePenaltyTime = st.LastPenaltySeconds * 10f; // panel reads tenths; RaceState keeps seconds
                rt.RacePenaltyEventTime = st.LastPenaltyEventTime;
                rt.RacePenaltyReason = st.LastPenaltyReason;
            }
            else
            {
                rt.RacePenaltyTime = 0f;
            }
            // RaceCheckpoint (the frozen-split index) is set below from the last CROSSED checkpoint.

            // QC race_SendTime → the racetimer split feed: the most-recent checkpoint crossing (race_checkpoint /
            // race_time / race_checkpointtime) drives the frozen "Checkpoint N (+/-delta)" line. The delta is vs
            // the per-checkpoint record (race_previousbesttime) + personal best (race_mypreviousbesttime). Times
            // are kept in plain seconds (the port's RaceTimerPanel works in seconds, not TIME_ENCODE'd ints).
            int lastCp = st.LastCrossedCheckpoint;
            // The panel selects FROZEN-split vs ANTICIPATION/clock on RaceCheckpointTime > 0; feed the crossing
            // stamp only within the 2s freeze window (QC racetimer.qc: a = bound(0, 2-(time-race_checkpointtime)))
            // so after the split fades the panel reverts to the running clock + next-checkpoint anticipation.
            double nowT = rt.Now;
            bool frozen = st.LastCheckpointTime > 0f && (nowT - st.LastCheckpointTime) < 2.0;
            rt.RaceCheckpointTime = frozen ? st.LastCheckpointTime : 0.0;
            rt.RaceCheckpoint = lastCp < 0 ? 254 : lastCp;
            rt.RaceTime = st.LastSplit;
            rt.RaceCheckpointSpeed = st.LastCheckpointSpeed;
            rt.RacePreviousBestTime = lastCp >= 0 ? race.CheckpointRecord(lastCp) : 0f;
            rt.RacePreviousBestName = lastCp >= 0 ? race.CheckpointRecordHolder(lastCp) : "";
            rt.RaceCheckpointBestSpeed = lastCp >= 0 ? race.CheckpointRecordSpeed(lastCp) : 0f;
            rt.RaceMyPreviousBestTime = lastCp >= 0 && st.PersonalCheckpointRecords.TryGetValue(lastCp, out float mb) ? mb : 0f;

            // QC the anticipation feed: the record split at the NEXT checkpoint (race_nextbesttime) + your own PB
            // (race_mybesttime), so the panel can show the live delta while heading toward it.
            int nextCp = st.NextCheckpoint < 0 ? 0 : st.NextCheckpoint;
            rt.RaceNextBestTime = race.CheckpointRecord(nextCp);
            rt.RaceNextBestName = race.CheckpointRecordHolder(nextCp);
            rt.RaceMyBestTime = st.PersonalCheckpointRecords.TryGetValue(nextCp, out float nmb) ? nmb : 0f;

            // QC StoreCheckpointSplits (racetimer.qc:276): on a NEW crossing, stash the split line for the
            // Checkpoints panel (the persistent list). Fire once per crossing (track the last fed checkpoint +
            // its stamp so a still-frozen split isn't re-stored every frame). A lap restart (cp back to start)
            // clears the list (QC ClearCheckpointSplits on race_time == 0).
            if (lastCp >= 0 && st.LastCheckpointTime > 0f
                && (lastCp != _lastFedCheckpoint || st.LastCheckpointTime != _lastFedCheckpointTime))
            {
                _lastFedCheckpoint = lastCp;
                _lastFedCheckpointTime = st.LastCheckpointTime;
                float rec = race.CheckpointRecord(lastCp);
                string label = lastCp == 0 ? "Finish line" : $"Checkpoint {lastCp}";
                // delta vs the match record at this checkpoint (negative = ahead). With no record yet, show the
                // bare label (StoreSplit with a 0 delta renders "+0.0").
                float delta = rec > 0f ? st.LastSplit - rec : 0f;
                cps.StoreSplit(lastCp, label, delta);
            }
            else if (st.LapStartTime > 0f && lastCp < 0)
            {
                cps.ClearSplits();   // QC ClearCheckpointSplits on a fresh lap (no crossing yet)
                _lastFedCheckpoint = -2;
            }
            if (!cps.Visible) cps.Visible = true;

            // QC race_SendStatus → cl_race.qc HUD_Mod_Race medal flash: when the LOCAL racer files a new record
            // (Race.RecordFinishTime stamps LastRecordTime), raise the medal once. The status maps QC's
            // race_SendStatus argument: 0 fail, 1 new time (PB, improved own rank), 2 new rank, 3 server record.
            if (ReferenceEquals(race.LastRecordPlayer, me) && race.LastRecordTime > 0f
                && race.LastRecordTime != _lastFedRecordTime)
            {
                _lastFedRecordTime = race.LastRecordTime;
                XonoticGodot.Common.Gameplay.RaceRecordResult r = race.LastRecord;
                int status = r.Kind switch
                {
                    XonoticGodot.Common.Gameplay.RaceRecordKind.Fail => 0,
                    _ when r.IsServerRecord => 3,                                            // newpos == 1
                    XonoticGodot.Common.Gameplay.RaceRecordKind.NewImproved => 1,            // improved own rank → new time
                    _ => 2,                                                                  // NewSet / NewBroken → new rank
                };
                rt.RaceStatus = status;
                rt.RaceStatusName = me!.NetName;
                rt.RaceStatusRank = status > 0 && r.NewPos > 0 ? CountOrdinal(r.NewPos) : "";
                rt.RaceStatusRankIsMine = true;                                              // the local racer set it
                rt.RaceStatusTime = race.LastRecordTime + 5.0;                              // QC race_status_time = time + 5
            }
            else if (rt.RaceStatus >= 0 && rt.RaceStatusTime <= rt.Now)
            {
                rt.RaceStatus = -1; // QC: the flash expires after its 5s window
            }
        }
        else
        {
            // CTS: a single timed run (RunStartTime is the count-up baseline); no per-checkpoint laps.
            XonoticGodot.Common.Gameplay.Cts.CtsState st = cts!.GetState(me!);
            rt.RaceLapTime = st.RunStartTime;
            rt.RaceCheckpoint = 254;
            rt.RaceNextCheckpoint = 254;                   // 254 = none (CTS has no next-checkpoint anticipation)
            rt.RacePenaltyAccumulator = 0f;
            rt.RaceCheckpointTime = 0.0;
            if (cps.Visible) cps.Visible = false;          // CTS has no per-checkpoint split list

            // QC race_SendStatus → cl_race.qc HUD_Mod_Race medal flash: when the LOCAL runner files a new record
            // (Cts.FinishStage stamps LastRecordTime), raise the medal once — identical mapping to the Race branch
            // (CTS forces g_race_qualifying=1, so its finish runs the same QC race_setTime → race_SendStatus path).
            if (ReferenceEquals(cts.LastRecordPlayer, me) && cts.LastRecordTime > 0f
                && cts.LastRecordTime != _lastFedRecordTime)
            {
                _lastFedRecordTime = cts.LastRecordTime;
                XonoticGodot.Common.Gameplay.RaceRecordResult r = cts.LastRecord;
                int status = r.Kind switch
                {
                    XonoticGodot.Common.Gameplay.RaceRecordKind.Fail => 0,
                    _ when r.IsServerRecord => 3,                                            // newpos == 1
                    XonoticGodot.Common.Gameplay.RaceRecordKind.NewImproved => 1,            // improved own rank → new time
                    _ => 2,                                                                  // NewSet / NewBroken → new rank
                };
                rt.RaceStatus = status;
                rt.RaceStatusName = me!.NetName;
                rt.RaceStatusRank = status > 0 && r.NewPos > 0 ? CountOrdinal(r.NewPos) : "";
                rt.RaceStatusRankIsMine = true;                                              // the local runner set it
                rt.RaceStatusTime = cts.LastRecordTime + 5.0;                               // QC race_status_time = time + 5
            }
            else if (rt.RaceStatus >= 0 && rt.RaceStatusTime <= rt.Now)
            {
                rt.RaceStatus = -1; // QC: the flash expires after its 5s window
            }
        }
        if (!rt.Visible) rt.Visible = true;
    }

    // Per-frame StoreSplit de-dup: the last checkpoint index + its crossing stamp we already fed to the
    // Checkpoints panel, so a still-frozen split (held for ~2s) isn't re-stored every render frame.
    private int _lastFedCheckpoint = -2;
    private float _lastFedCheckpointTime = -1f;
    // The last record-attempt stamp we raised the medal flash for (so a frozen flash isn't re-triggered).
    private float _lastFedRecordTime = -1f;
    // Same de-dup for the mod-icon medal flash (separate from the split-timer flash so each panel fires once).
    private float _lastFedModIconRecordTime = -1f;

    /// <summary>QC <c>count_ordinal</c> (common/util.qc): 1 → "1st", 2 → "2nd", 3 → "3rd", 11..13 → "Nth".</summary>
    private static string CountOrdinal(int n)
    {
        int tens = n % 100;
        if (tens >= 11 && tens <= 13) return n + "th";
        return (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };
    }

    /// <summary>
    /// Point the full HUD's player-bound panels (health/ammo/weapons/crosshair/speedo) at the local actor: on a
    /// listen server that is <see cref="LocalServerPlayer"/> (resolved once the spawn lands); on a pure client
    /// there is no local Player so it stays null (the player-agnostic panels — centerprint/killfeed — still draw,
    /// and the NetHud crosshair/health covers the rest). Only re-applies on a change so the per-frame call is cheap.
    /// </summary>
    /// <summary>
    /// The pure-client HUD stats mirror: a bare <see cref="Player"/> (never spawned into any world/entity table)
    /// whose fields are refreshed each frame from the NETWORKED owner stats, so the full skinned HUD panel set
    /// (health/armor, ammo, weapons, powerups, crosshair) renders on a pure <c>--connect</c> client exactly like
    /// the listen host — the CSQC model, where the HUD draws from replicated STATs, not server objects. Null on
    /// a listen server (the real <see cref="LocalServerPlayer"/> feeds the panels there).
    /// </summary>
    private Player? _hudMirror;

    /// <summary>
    /// Refresh <see cref="_hudMirror"/> from the networked owner stats (pure client only): health/armor + the
    /// inventory block (ammo pools, owned-weapon bitset, unlimited-ammo, STAT_ITEMS flags) + active weapon from
    /// the owner block; team + packed colors from the networked own-entity slice; position/velocity/onground from
    /// prediction (the physics/strafe panels); status effects decoded from the owner's own entity slice
    /// (<see cref="ClientNet.LocalState"/> — the powerups panel timers). Raw field writes, NEVER the hooked
    /// SetResource — the mirror is a render-side stats holder, not a sim actor, and must not fire mutator hooks.
    /// </summary>
    private void UpdateHudMirror()
    {
        if (_isListenServer || _client is not { Accepted: true })
            return;
        _hudMirror ??= new Player { NetName = _playerName };
        Player m = _hudMirror;
        XonoticGodot.Net.OwnerInventory inv = _client.LocalInventory;

        m.Health = _client.Health;
        m.ArmorValue = _client.Armor;
        m.AmmoShells = inv.Shells;
        m.AmmoBullets = inv.Bullets;
        m.AmmoRockets = inv.Rockets;
        m.AmmoCells = inv.Cells;
        m.AmmoFuel = inv.Fuel;
        m.OwnedWeaponSet = new WepSet { Bits = inv.WeaponBits };
        m.UnlimitedAmmo = inv.UnlimitedAmmo;
        m.Items = inv.ItemFlags; // STAT_ITEMS: the powerups panel's jetpack / fuel-regen / superweapon rows
        m.ActiveWeaponId = _client.ActiveWeaponId;
        m.SwitchWeaponId = _client.ActiveWeaponId;

        // Team + packed clientcolors for the team/shirt/pants-colored HUD elements (dock tint, panel bg_color_team):
        // the team from the networked scoreboard row, the colors from our own entity slice (now that the server
        // sends it). Without these the dock/shirt/pants resolve palette-0 white instead of the profile/team color.
        m.Team = LocalShownamesTeam();
        m.ClientColors = _client.LocalState?.Colors ?? m.ClientColors;

        // Predicted pose for the physics/strafe panels + the crosshair's Player.Origin/Angles reads.
        m.Origin = _client.PredictedOrigin;
        m.Velocity = _client.PredictedVelocity;
        m.Angles = _viewAngles;
        m.DeadState = _client.Health <= 0 && _everAlive ? DeadFlag.Dead : DeadFlag.No;

        // Predicted ground state for the physics/strafehud panels' slick / accel-max branches (else they read a
        // permanently-airborne mirror). The reconciler seeds OnGround from the owner block's onground bool.
        if (_client.PredictedOnGround)
            m.Flags |= XonoticGodot.Common.Framework.EntFlags.OnGround;
        else
            m.Flags &= ~XonoticGodot.Common.Framework.EntFlags.OnGround;

        // Powerup timers (strength/shield/…): the server networks every player's status-effect bitmap in the
        // entity stream — including our own entity now (v14), whose decoded slice ClientNet keeps as LocalState.
        ClientEntityView.ApplyStatusEffects(m, _client.LocalState?.StatusEffects);
    }

    private void UpdateFullHudPlayer()
    {
        if (_fullHud is null)
            return;
        // The local server Player on a listen host; the networked-stats mirror on a pure client (fed each frame
        // by UpdateHudMirror — null until the handshake accepts). Either way the full skinned HUD renders.
        Player? p = LocalServerPlayer ?? _hudMirror;
        if (ReferenceEquals(p, _lastHudPlayer))
            return;
        _lastHudPlayer = p;
        _fullHud.SetPlayer(p);

        // With a local Player present the skinned Crosshair + HealthArmor panels render, so suppress NetHud's
        // duplicate vector crosshair + textual health/armor (goal 1: no double crosshair/health). On a pure client
        // (p == null) NetHud stays the always-on fallback.
        bool havePlayer = p is not null;
        if (_hud is not null && GodotObject.IsInstanceValid(_hud))
            // The editor picks along the crosshair, so a free-flying mapper needs one. NetHud's lightweight
            // vector crosshair is the right one here: the skinned HUD's is player-bound and an observer has no
            // player, which is why aiming in the editor had nothing to aim WITH.
            _hud.SuppressCrosshairAndHealth = havePlayer && !IsEditorFreeFly;

        // HealthArmorPanel reads the Player's resources, so it self-blanks with no Player (safe to leave visible).
        // CrosshairPanel, however, draws its reticle even without a Player (it doesn't gate on Player) — so on a
        // pure client it would DOUBLE NetHud's crosshair. Show the skinned crosshair only when a local Player is
        // present; otherwise NetHud (un-suppressed above) owns the reticle. No double crosshair in either case.
        // The live per-frame suppression (dead/intermission/scoreboard/observing) is applied in UpdateCrosshairGate
        // below — this only sets the player-presence base state on a player change.
        if (_fullHud.Crosshair.Visible != havePlayer)
            _fullHud.Crosshair.Visible = havePlayer;

        // StrafeHud (goal 3): point it at the local server Player (its velocity + view angles + onground drive the
        // strafe bar). It self-blanks without a Player, so on a pure client this leaves it null = no draw. The
        // optional WishDir/JumpHeld/OnSlick are not fed (the HUD degrades to the non-local W+A path), which keeps
        // it useful without exposing the carrier's per-tick move-values to the client HUD layer.
        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.StrafeHudPanel>() is { } strafe)
            strafe.Player = p;

        // Editor status panel (E2). It stays blank in every non-editor session, so gate it on the gametype and
        // let it read the local observer state: EDIT *is* the observer state, so free-flying means editing.

        // QC HUD_PressedKeys spectatee gate: a free-fly observer never shows the cluster, and while merely
        // playing it shows only at enable 2. Feed the live spectatee_status (translated into the QC convention:
        // ClientNet encodes free-fly as SpectateeStatus==LocalNetId, QC uses -1; following a player is >0).
        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.PressedKeysPanel>() is { } pressed)
        {
            int qcSpectatee = 0;
            if (_client is not null)
            {
                if (_client.IsObserving) qcSpectatee = -1;            // free-fly observer
                else if (_client.SpectatingNetId > 0) qcSpectatee = _client.SpectatingNetId; // following a player
            }
            pressed.SpectateeStatus = qcSpectatee;
            // When FOLLOWING another player, show THEIR keys (the networked STAT(PRESSED_KEYS), which the server
            // copies from the spectatee); when playing/observing yourself, leave the override null so the panel
            // reads your own live input (lower latency, and on a listen host LocalServerPlayer carries it anyway).
            pressed.PressedKeysOverride = (_client is not null && _client.SpectatingNetId > 0)
                ? _client.OwnerPressedKeys
                : null;
        }
    }
    private Player? _lastHudPlayer;

    /// <summary>
    /// Per-frame crosshair master-gating (QC client/hud/crosshair.qc HUD_Crosshair 226-241). Base skips drawing the
    /// crosshair entirely — and resets its smoothing state — whenever the player is not in a live first-person
    /// playing view: scoreboard active, intermission==2, GAME_STOPPED, lockview, <c>spectatee_status == -1</c>
    /// (free-fly observer), <c>STAT(HEALTH) &lt;= 0</c> (dead), the dead-spectator CAMERA_SPECTATOR==2 mode, or a
    /// non-FREEAIM viewloc. The previous port only gated on player-presence (havePlayer), so the skinned crosshair
    /// kept drawing dead-centre while dead / at intermission / over the scoreboard. Reproduce the reachable subset
    /// of the suppress set from state the port already tracks client-side (works on the host path where the skinned
    /// panel draws; a pure remote client's reticle is owned by NetHud, gated separately). The crosshair only shows
    /// when a local Player is present AND none of the suppress conditions hold.
    /// </summary>
    private void UpdateCrosshairGate()
    {
        if (_fullHud is null || _client is null)
            return;

        // Base: the crosshair draws only with a local first-person player (havePlayer mirrors QC's view player).
        bool havePlayer = _lastHudPlayer is not null;

        // QC HUD_Crosshair early-skips (the subset the port tracks client-side):
        //   STAT(HEALTH) <= 0           → dead (LocalDeadNow covers host + remote)
        //   intermission == 2 / GAME_STOPPED → MatchIntermission (the match-over scoreboard takes the screen)
        //   scoreboard_active           → the scoreboard panel is up (+showscores / death / intermission)
        //   spectatee_status == -1      → free-fly observer (IsObserving) — no own first-person aim to draw on
        bool suppressed =
            LocalDeadNow()
            || _client.MatchIntermission
            || (_scoreboard is not null && _scoreboard.Active)
            || _client.IsObserving;

        bool show = havePlayer && !suppressed;
        if (_fullHud.Crosshair.Visible != show)
            _fullHud.Crosshair.Visible = show;

        // [viewprim] Feed the authoritative rendered aim ray + chase state into the crosshair so the shared
        // CrosshairTrace primitive traces from the real render eye (closing the dead AimForward/AimOrigin feed).
        // UpdateCamera → FirstPersonView.UpdateView has already run this frame, so RenderedEyeQuake/
        // RenderedForwardQuake carry the final rendered eye/forward (derived from the already-predicted local pose —
        // no new networking). This replaces CrosshairPanel's dead reconstruction fallback (TryGetAimRay was never
        // fed) with the real eye/forward, and lights up the crosshair_chase origin trace whenever chase is active
        // (own-player + spectate both set CameraMode=Chase, surfaced as _view.ChaseActive).
        CrosshairPanel xh = _fullHud.Crosshair;
        xh.AimOrigin = _view.RenderedEyeQuake;
        xh.AimForward = _view.RenderedForwardQuake;
        xh.ChaseActive = _view.ChaseActive;
        xh.ChaseCamera3D = _camera;

        // [viewconsumers] crosshair-chase body fade (QC crosshair_chase: when the third-person crosshair-chase camera
        // is up, the player's OWN model is drawn at crosshair_chase_playeralpha so it doesn't block the chased aim).
        // Only the local player's own third-person counts — when spectating someone else (SpectatingNetId != 0) the
        // chased body is theirs, not the local model, so leave the local model alone. Drives ClientWorld's per-model
        // alpha ease by local net id; 0 restores full opacity. Read both cvars live (CrosshairPanel registers them).
        bool ownChase = _view.ChaseActive && (_client?.SpectatingNetId ?? 0) == 0
            && CvarOr(Api.Cvars, "crosshair_chase", 0f) != 0f;
        int localNet = _client?.LocalNetId ?? 0;
        _render?.SetLocalBodyAlpha(localNet,
            ownChase ? 1f - CvarOr(Api.Cvars, "crosshair_chase_playeralpha", 0.25f) : 0f);
    }

    /// <summary>
    /// Feed the InfoMessages panel the networked dead/respawn + observing/spectating state each frame (QC the
    /// infomessages panel reads STAT(RESPAWN_TIME) + spectatee_status). Works on a pure remote client (no local
    /// Player) since it sources everything from <see cref="ClientNet"/>: the respawn countdown / "press fire"
    /// prompt from <see cref="ClientNet.RespawnTimeStat"/>, and the "Observing" / "Spectating: name" line from
    /// <see cref="ClientNet.SpectateeStatus"/> (the spectatee's name resolved on a listen server).
    /// </summary>
    private void UpdateInfoMessages()
    {
        if (_fullHud is null || _client is null)
            return;
        // (E2/E3) Editor state + panel feed. This lives on a per-frame path deliberately: the readouts it
        // drives (hover coordinates, drag delta, undo label) change continuously, so the change-latched
        // player-setup path is the wrong home for it.
        bool editorFreeFly = IsEditorGametype && (_client?.IsObserving ?? true);
        if (_editor is not null)
        {
            _editor.Active = editorFreeFly;

            // (E7) Ctrl temporarily INVERTS grid snapping (§11.9). Polled per frame rather than latched on a
            // key event, because it is a held modifier: an edge-triggered read would strand the editor in the
            // inverted state whenever the release event went to a focused control instead of here.
            _editor.SnapInverted = editorFreeFly && Input.IsKeyPressed(Key.Ctrl);

            // Session opens with the GAMETYPE, not with the first free-fly frame: the world swap to the
            // document happens before the player ever sees the compiled BSP, and playtest is in the
            // document from the first spawn.
            if (IsEditorGametype)
                EnsureEditorSession();
            else if (_editorOrtho is { IsOpen: true })
                _editorOrtho.Close();   // dropping into playtest must not leave a flat wireframe camera

            // Entering PLAYTEST with edits pending: rebuild collision from the document so the physics match
            // what is on screen. Keyed on GeometryVersion, so an untouched map never pays the rebuild.
            if (!editorFreeFly && IsEditorGametype && _editor.Document is not null
                && _editorCollisionVersion != _editor.GeometryVersion && _serverWorld is not null)
            {
                _editorCollisionVersion = _editor.GeometryVersion;
                BspCollisionBuilder.Result rebuilt =
                    Vmap.EditorWorldCollision.Build(_editor.Document, _droppedSubmodels);
                _serverWorld.Services.SetCollisionWorld(rebuilt.World);
                _serverWorld.BrushModels = rebuilt.Submodels;
                BspCollisionBuilder.RegisterSubmodels(rebuilt.Submodels, _serverWorld.Services.ModelsImpl);
                XonoticGodot.Common.Diagnostics.Log.Info(
                    $"editor: collision rebuilt from the document (v{_editor.GeometryVersion})");
            }
        }

        // The editor aims along the crosshair, but an observer has no local player so the skinned crosshair is
        // suppressed. Re-enable NetHud's lightweight one here — on the PER-FRAME path, because the player-setup
        // path only runs when the local player REFERENCE changes and would never re-evaluate this.
        if (_hud is not null && GodotObject.IsInstanceValid(_hud) && IsEditorGametype)
            _hud.SuppressCrosshairAndHealth = !IsEditorFreeFly && LocalServerPlayer is not null;

        // Ortho panning on the movement axes. The ortho view owns the cursor, so flying is suspended and those
        // keys are free; this is also far more reliable than depending on the middle mouse button arriving.
        if (_editorOrtho is { IsOpen: true } panOrtho)
            panOrtho.PanByAxes(BindTable.Forward, BindTable.Side, (float)GetProcessDeltaTime());

        RefreshEditorWorld();
        if (_editorLights is not null && GodotObject.IsInstanceValid(_editorLights) && _camera is not null)
        {
            _editorLights.Update(_camera.GlobalPosition, Menu.MenuState.Cvars, (float)Time.GetTicksMsec() / 1000f);
            // Per-frame, not once at build: the environment is owned by the WorldEnvironment NODE (_worldEnv),
            // which may not exist yet when the first world is built, and the GI/ambient cvars are things a
            // mapper changes mid-session. ApplyEnvironment itself no-ops when nothing changed.
            // The background bake publishes into the retained bake; one rebuild resamples it onto the
            // world, which is exact because the geometry has not changed underneath it.
            // Applying the bake is SPREAD over frames: the world already on screen has the right geometry,
            // so only its vertex colours change, a few cell meshes per frame. A full rebuild here was ~880 ms
            // of frozen main thread the instant a bake landed — the one moment the editor should feel free.
            if (Vmap.VmapMapBuilder.RecolorRemaining > 0)
                Vmap.VmapMapBuilder.RecolorStep(3.0);   // ~3 ms a frame: invisible at 144 Hz, done in about a second

            if (Vmap.EditorLightBake.BakeFinished)
            {
                Vmap.EditorLightBake.ClearFinished();
                Vmap.EditorLightBake.End();
                GD.Print($"[EditorLighting] bake: {Vmap.EditorLightBake.RaysTraced:N0} rays in "
                    + $"{Time.GetTicksMsec() - _bakeClock} ms (background)");
                GD.Print($"[EditorLighting] encode range p99={Vmap.EditorLightBake.EncodeRange:F1}, "
                    + $"filled {Vmap.EditorLightBake.FilledSamples:N0} black samples, "
                    + $"{_pendingTraceCount:N0} occluders");
                _bakedShadowsStale = false;
                Vmap.VmapMapBuilder.BeginRecolor();
            }

            if (_worldEnv is not null)
                Vmap.EditorLighting.ApplyEnvironment(_worldEnv, Menu.MenuState.Cvars);

            // The baked-light controls are GLOBAL shader uniforms fed from cvars every frame, changed-gated.
            // Their first incarnation was per-material uniforms, frozen into the material cache at build
            // time — which is why "_scale and _ambient don't appear to do anything" was literally true.
            if (Menu.MenuState.Cvars is { } lc)
            {
                // The editor's baked-light globals live with the other world globals; an editor session can
                // reach this before anything else has caused them to be registered.
                WorldTint.EnsureRegistered();
                // Clamped to the range these knobs MEAN, because their meaning changed: cl_editor_light_ambient
                // used to be a Godot environment energy (where 10 was merely strong) and is now an in-shader
                // floor (where 10 is an opaque white wash). A value saved under the old semantics must not be
                // able to flatten the world — clamp it rather than let a stale config outrank every default.
                float sc2 = Math.Clamp(CvarOr(lc, Vmap.EditorLighting.CvarBakeScale, 0.0043f), 0f, 2f);
                float am2 = Math.Clamp(CvarOr(lc, Vmap.EditorLighting.CvarAmbient, 0.004f), 0f, 1f);
                float gm2 = Math.Clamp(CvarOr(lc, Vmap.EditorLighting.CvarBakeGamma, 1.05f), 0.25f, 4f);
                float dx2 = Math.Clamp(CvarOr(lc, Vmap.EditorLighting.CvarDeluxe, 1f), 0f, 1f);
                if (dx2 != _bakeUniformDeluxe)
                {
                    _bakeUniformDeluxe = dx2;
                    RenderingServer.GlobalShaderParameterSet("editor_deluxe", dx2);

                }
                if (sc2 != _bakeUniformScale || am2 != _bakeUniformAmbient || gm2 != _bakeUniformGamma)
                {
                    _bakeUniformScale = sc2;
                    _bakeUniformAmbient = am2;
                    _bakeUniformGamma = gm2;
                    RenderingServer.GlobalShaderParameterSet("editor_bake_scale", sc2);
                    RenderingServer.GlobalShaderParameterSet("editor_bake_ambient", am2);
                    RenderingServer.GlobalShaderParameterSet("editor_bake_gamma", gm2);
                }
                if (_bakeUniformRange != Vmap.EditorLightBake.EncodeRange)
                {
                    _bakeUniformRange = Vmap.EditorLightBake.EncodeRange;
                    RenderingServer.GlobalShaderParameterSet("editor_bake_range", _bakeUniformRange);
                }
            }
            Vmap.EditorLighting.SuppressSceneSun(this, true);
        }

        // (E7) The editor context menu and the crosshair action readout: same feed pattern as EditorPanel.
        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.EditorMenuPanel>() is { } editorMenu)
        {
            editorMenu.IsEditorSession = IsEditorGametype;
            editorMenu.Controller = _editor;
            editorMenu.OrthoOpen = _editorOrtho is { IsOpen: true };
            editorMenu.FlySpeed = LocalServerPlayer?.SpectatorSpeed ?? 1f;
            // Rows carry command STRINGS, so the menu drives the editor through the same vocabulary the binds
            // and the console use rather than growing a private API of its own.
            editorMenu.CommandSink ??= line => Menu.MenuState.Interp?.ExecuteLine(line);

            // Leaving EDIT (into playtest, or out of the gametype) must not leave a modal menu holding the
            // cursor: there would be no way to close it, because its own keys are gone with the mode.
            if (editorMenu.IsOpen && !(IsEditorGametype && (_client?.IsObserving ?? false)))
                editorMenu.Close();
        }

        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.EditorActionPanel>() is { } editorAction)
        {
            editorAction.IsEditorSession = IsEditorGametype;
            editorAction.IsEditing = _client?.IsObserving ?? true;
            editorAction.Controller = _editor;
        }

        if (_fullHud.GetPanel<XonoticGodot.Game.Hud.EditorPanel>() is { } editorPanel)
        {
            editorPanel.IsEditorSession = IsEditorGametype;
            Hud.HudPanel.EditorSession = IsEditorGametype;
            editorPanel.IsEditing = _client?.IsObserving ?? true;
            editorPanel.FlySpeed = LocalServerPlayer?.SpectatorSpeed ?? 1f;
            editorPanel.Controller = _editor;
            editorPanel.Ortho = _editorOrtho;
            editorPanel.Lights = _editorLights is not null && GodotObject.IsInstanceValid(_editorLights)
                ? _editorLights.TotalLightCount : -1;
            editorPanel.Baked = _editorLights is not null && GodotObject.IsInstanceValid(_editorLights)
                && _editorLights.Baking;
            editorPanel.ShadowsStale = _bakedShadowsStale;
            editorPanel.BakeRunning = Vmap.EditorLightBake.BakeRunning;
            editorPanel.BakePhase = Vmap.EditorLightBake.ProgressPhase;
            editorPanel.ApplyRemaining = Vmap.VmapMapBuilder.RecolorRemaining;
            editorPanel.ApplyTotal = Vmap.VmapMapBuilder.RecolorTotal;
            // The cvar is the same switch key 0 flips, so a scripted capture and a keypress cannot disagree.
            if (Menu.MenuState.Cvars is { } bc3)
            {
                bool want = !string.IsNullOrEmpty(bc3.GetString(Vmap.EditorLighting.CvarShowBsp))
                    && bc3.GetFloat(Vmap.EditorLighting.CvarShowBsp) != 0f;
                if (want != _editorShowBsp)
                {
                    _editorShowBsp = want;
                    ApplyEditorWorldVisibility();
                }
            }
            editorPanel.ShowingBsp = _editorShowBsp;
            editorPanel.BakeProgress = Vmap.EditorLightBake.ProgressTotal > 0
                ? (float)Vmap.EditorLightBake.Progress / Vmap.EditorLightBake.ProgressTotal
                : 0f;
            editorPanel.HasMapSun = _editorLights is not null && GodotObject.IsInstanceValid(_editorLights)
                && _editorLights.HasMapSun;
        }

        InfoMessagesPanel im = _fullHud.InfoMessages;
        // In the editor, jump flies you upward and PLAYTEST is its own toggle, so the stock join hint would be
        // pointing at the wrong key. The editor panel carries the real binding.
        im.SuppressJoinHint = IsEditorGametype;
        im.RespawnStat = _client.RespawnTimeStat;
        im.NetServerTime = _client.LatestServerTime;

        bool spectating = _client.Accepted && _client.SpectateeStatus != 0;
        im.IsSpectating = spectating;
        if (spectating)
        {
            int sid = _client.SpectatingNetId;
            im.SpectatingName = sid != 0 && _server is not null
                ? (_server.PlayerByNetId(sid)?.NetName ?? "")
                : "";
        }

        // QC MUTATOR_HOOKFUNCTION(cl_ca, DrawInfoMessages) (cl_clanarena.qc): in CA, keep the info-message panel
        // drawing for a SPECTATOR who has the scoreboard open (ENTCS_SPEC_IN_SCOREBOARD) — otherwise the panel
        // self-blanks behind the scoreboard. The port knows the scoreboard-open state locally (+showscores) and the
        // spectating state from the net client, so model the CA branch directly: force the panel visible while a CA
        // spectator views the scoreboard. (In this port the manager already keeps infomessages always-on, so this
        // mainly documents intent; it stays a faithful, harmless assertion of the QC behaviour.)
        if (XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype == "ca"
            && _client.Accepted && _client.SpectateeStatus != 0
            && XonoticGodot.Engine.Console.BindTable.ShowScores)
            im.Visible = true;

        // QC cl_lms DrawInfoMessages: in LMS, the local player is "out" once they hold an LMS rank (LMS_RANK > 0,
        // set on elimination). Read it from the local scoreboard row's networked LMS_RANK column.
        im.LmsNoLives = false;
        if (XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype == "lms")
        {
            XonoticGodot.Net.ScoreboardWire? sb = _client.LatestScoreboard;
            var rankField = XonoticGodot.Common.Gameplay.Scoring.GameScores.Field("LMS_RANK");
            if (sb is not null && rankField is not null)
            {
                int localId = _client.LocalNetId;
                foreach (XonoticGodot.Net.ScoreRowWire row in sb.Rows)
                {
                    if (row.NetId != localId) continue;
                    int rid = rankField.RegistryId;
                    if (rid >= 0 && rid < row.Columns.Length && row.Columns[rid] > 0)
                        im.LmsNoLives = true;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Resolve the standalone scoreboard's geometry + skin config against the LIVE viewport — QC
    /// <c>Scoreboard_Draw</c>'s own <c>HUD_Panel_LoadCvars()</c> + the centered-slab override it applies right
    /// after (scoreboard.qc:2466-2483). Must run EVERY FRAME, like Base: the previous one-shot
    /// <c>Configure(...)</c> at NetGame setup froze the rect at whatever the viewport happened to be then, so
    /// after the window resized (or the boot fullscreen transition landed) the board kept a rect computed for
    /// e.g. 1024×768 and rendered jammed into the top-left of the real viewport. Going through
    /// <see cref="XonoticGodot.Game.Hud.HudPanel.LoadConfig"/> ALSO gives the panel a real resolved
    /// <c>Cfg</c> — the standalone instance is not managed by <see cref="XonoticGodot.Game.Hud.HudManager"/>, so
    /// it had been drawing with the constructor-default config (<c>Bg = "0"</c>, fixed alphas/padding), which is
    /// why it never painted the luma skin frame or honoured any <c>hud_panel_scoreboard_*</c> cvar.
    /// </summary>
    private void LayoutScoreboard()
    {
        if (_scoreboard is null || !GodotObject.IsInstanceValid(_scoreboard) || !_scoreboard.Visible) return;
        Vector2 vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        // 1) QC HUD_Panel_LoadCvars — resolve bg/skin/alphas/padding/fontsize from hud_panel_scoreboard_*.
        _scoreboard.LoadConfig(vp, _fullHud?.HudFadeAlpha ?? 1f, _scoreboard.FadeAlpha);
        // 2) QC the centered-slab override Scoreboard_Draw applies right after LoadCvars (the scoreboard is
        //    PANEL_CONFIG_NO — its placement is derived, not taken from the generic pos/size cvars).
        _scoreboard.Configure(_scoreboard.BaseGeometry(vp));
    }

    /// <summary>
    /// Toggle + feed the networked scoreboard. THE consumer of <see cref="ClientNet.LatestScoreboard"/> on the
    /// real play path (it was decoded but never read). Shows the panel while <c>+showscores</c> is held; when a
    /// new scoreboard frame has arrived it hydrates the panel rows from the wire (resolving netId→local via the
    /// client's <see cref="ClientNet.LocalNetId"/>; name/team ride the wire entcs slice) + sets the title and the
    /// fraglimit/timelimit/map header from the live cvars.
    /// </summary>
    private void UpdateScoreboard()
    {
        if (_scoreboard is null || _client is null)
            return;

        // QC Scoreboard_WouldDraw (scoreboard.qc): at intermission==1 the scoreboard force-draws (no +showscores
        // key needed); at intermission==2 — when the MapVote panel owns the screen — it hides. We model
        // intermission==2 as "the MapVote panel is currently showing" (UpdateMapVotePanel owns mv.Visible).
        bool mapVoteShowing = _fullHud is not null && _fullHud.MapVote.Visible;
        bool intermissionShow = _client.MatchIntermission && !mapVoteShowing;
        // QC Scoreboard_WouldDraw death scoreboard (scoreboard.qc:1793): once the local player is dead AND a short
        // delay has elapsed since death, the scoreboard forces up even without +showscores. The port has no
        // networked death_time, so reproduce it client-side: STAT(RESPAWN_TIME) != 0 means dead (set at death,
        // negated while respawning, 0 while alive), and we stamp the server time at which death was first observed
        // to gate the cl_deathscoreboard_delay window. Suppressed at intermission==2 (mapvote) below.
        bool deadNow = _client.RespawnTimeStat != 0f;
        if (deadNow)
        {
            if (_localDeathStamp < 0f) _localDeathStamp = _client.LatestServerTime;
        }
        else _localDeathStamp = -1f;
        var cv = Api.Services?.Cvars;
        // QC cl_cts.qc MUTATOR_HOOKFUNCTION(cl_cts, DrawDeathScoreboard) returns ISGAMETYPE(CTS): CTS never shows the
        // scoreboard automatically while dead (a CTS death is an instant respawn at the start line — there's nothing
        // to read). The manual +showscores hold and the intermission scoreboard are unaffected.
        bool ctsGame = _serverWorld?.GameType is XonoticGodot.Common.Gameplay.Cts
            || XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype == "cts";
        bool deathScoreboard = deadNow && _localDeathStamp >= 0f && !ctsGame
            && (cv is null || cv.GetFloat("cl_deathscoreboard") != 0f)
            && _client.LatestServerTime - _localDeathStamp >= (cv is null ? 1f : cv.GetFloat("cl_deathscoreboard_delay"));
        // QC client/view.qc:1865 — the server asks for a team pick by setting `_scoreboard_team_selection`;
        // the client opens the interactive picker and clears the flag.
        _scoreboard.MatchIntermission = _client.MatchIntermission;
        if (MenuState.Cvars.GetFloat("_scoreboard_team_selection") != 0f)
        {
            _scoreboard.UiEnable(1);
            MenuState.Cvars.Set("_scoreboard_team_selection", "0");
        }

        // QC Scoreboard_WouldDraw's UI branch (scoreboard.qc:1766): while the interactive scoreboard is up it
        // force-draws regardless of +showscores, and a closing one tears its state down once faded out.
        bool uiShow = _scoreboard.UiTick();

        bool show = (uiShow || XonoticGodot.Engine.Console.BindTable.ShowScores || intermissionShow || deathScoreboard)
            && !mapVoteShowing; // QC intermission==2 / clickable-radar suppression: mapvote owns the screen
        // QC scoreboard.qc:2411: drive the cross-fade via scoreboard_active (the Active setter ramps _fadeAlpha
        // in/out in _Process and hides the panel once fully faded out) rather than popping Visible — this is what
        // makes hud_panel_scoreboard_fadeinspeed/fadeoutspeed actually animate on the live path.
        if (_scoreboard.Active != show)
            _scoreboard.Active = show;
        // QC Scoreboard_Draw re-runs HUD_Panel_LoadCvars + its centered-slab geometry EVERY draw — so must we,
        // or the board keeps a rect computed for a stale viewport (see LayoutScoreboard).
        LayoutScoreboard();
        // Drive the manager's non-scoreboard panel cross-fade (QC panel_fade_alpha) from the scoreboard's live fade
        // level. Without this ScoreboardFade sat at 0 forever, so the always-on HUD panels never faded when the
        // scoreboard came up — most visibly the top-left radar, whose bottom-right corner pokes into the centered
        // scoreboard's rect, so its minimap showed THROUGH the scoreboard's Red/Blue + column headers (the "jumble
        // over the radar" on the death scoreboard). FadeAlpha is the read-only ramp ScoreboardPanel exposes exactly
        // for this. Set every frame BEFORE the early returns below so it also ramps back down when the board hides.
        // (_scoreboard is standalone — not _fullHud's discovered panel — so the owner has to bridge the two.)
        if (_fullHud is not null)
            _fullHud.ScoreboardFade = _scoreboard.FadeAlpha;
        // The radar is a STANDALONE panel (NetGame-managed pos/size, never LoadConfig'd), so the manager cross-fade
        // above never reaches it — its LiveFgAlpha is pinned. Fade it directly by the scoreboard level via Modulate
        // so the top-left minimap doesn't bleed through the scoreboard (QC: teamradar is MAINGAME-only). EXCEPT while
        // it's Maximized: the clickable spawn-select radar is itself shown while dead — exactly when the death
        // scoreboard is also up — and must stay fully visible to pick a spawn (force full alpha so a fade that began
        // before it was maximized is undone).
        if (_radar is not null)
        {
            float radarA = _radar.Maximized ? 1f : 1f - _scoreboard.FadeAlpha;
            if (_radar.Modulate.A != radarA)
                _radar.Modulate = new Color(1f, 1f, 1f, radarA);
        }
        // QC GET_NEXTMAP: feed the "Next map:" line from the server-broadcast _nextmap (ClientNet.NextMap).
        if (show && _scoreboard.NextMap != _client.NextMap)
            _scoreboard.NextMap = _client.NextMap;
        // Feed the networked respawn line every frame while shown (QC STAT(RESPAWN_TIME), scoreboard.qc:2764) so
        // the countdown ticks even between score-version changes; this is the live caller for the respawn block.
        if (show)
        {
            _scoreboard.RespawnStat = _client.RespawnTimeStat;
            _scoreboard.RespawnServerTime = _client.LatestServerTime;
            // QC scoreboard.qc:2792 getcommandkey(_("jump"), "+jump"): show the actual key bound to +jump in the
            // "press X to respawn" line, falling back to the literal "jump" when nothing is bound.
            _scoreboard.RespawnJumpKey = XonoticGodot.Engine.Console.BindTable.CommandKey("jump", "+jump");
            // QC Scoreboard_AccuracyStats_WouldDraw (scoreboard.qc:1864): suppress the accuracy block during warmup.
            _scoreboard.MatchWarmup = _client.MatchWarmup;
            // QC Scoreboard_MapStats_Draw reads STAT(MONSTERS_*)/STAT(SECRETS_*) every draw — they tick
            // independently of the score version, so feed the live map-stats counts every frame while shown
            // (not only inside the change-gated row feed below, which made monsters lag and never fed secrets).
            FeedMapStats();
        }
        if (!show)
            return;

        // feed rows only when the data changed (identity by reference — ClientNet replaces the object per
        // decode). A mode-status change (T53) also re-feeds: the eliminated grey-out must track freezes/deaths
        // that don't bump a score version.
        XonoticGodot.Net.ScoreboardWire? sb = _client.LatestScoreboard;
        XonoticGodot.Net.GametypeStatusBlock.Decoded? ms = _client.LatestModeStatus;
        if (sb is not null && (!ReferenceEquals(sb, _lastFedScoreboard) || !ReferenceEquals(ms, _lastFedModeStatus)))
        {
            _lastFedScoreboard = sb;
            _lastFedModeStatus = ms;
            _scoreboard.Title = ScoreboardTitle();
            _scoreboard.SetWireRows(sb, _client.LocalNetId, ms?.EliminatedNetIds); // grey-out (QC eliminatedPlayers)
            // QC Scoreboard_Rankings_Draw: feed the networked race/CTS rankings (best-first (time, holder)) so the
            // scoreboard's rankings block is live in race modes (empty otherwise → DrawRankings hides it). The local
            // name (QC entcs_GetName(player_localnum)) drives the self-row highlight.
            _scoreboard.SetRankings(sb.Rankings);
            _scoreboard.RankingsSelfName = ResolveScoreboardName(_client.LocalNetId);
            // QC the race/CTS speed award (scoreboard.qc:2731): the round-best + all-time best planar speed + holders.
            _scoreboard.SetSpeedAward(sb.SpeedAward, sb.SpeedAwardHolder, sb.SpeedAwardBest, sb.SpeedAwardBestHolder);
            // QC the per-viewer Inventory entity: this client's item-pickup tally, drawn by the scoreboard's
            // Item stats grid (Scoreboard_ItemStats_Draw). Keyed by the item def's HUD icon name.
            _itemStatsById.Clear();
            foreach ((string icon, int count) in sb.ItemStats) _itemStatsById[icon] = count;
            _scoreboard.SetItemStats(_itemStatsById);
            FeedScoreboardHeader();
        }
        else if (sb is null)
        {
            // No networked scoreboard yet (pre-first-snapshot): show an empty titled panel rather than nothing.
            _scoreboard.Title = ScoreboardTitle();
        }
    }
    private XonoticGodot.Net.ScoreboardWire? _lastFedScoreboard;
    private XonoticGodot.Net.GametypeStatusBlock.Decoded? _lastFedModeStatus;
    /// <summary>QC death_time stand-in: the server time at which the local player's death was first observed
    /// (STAT(RESPAWN_TIME) first non-zero), or -1 while alive; gates the cl_deathscoreboard_delay window.</summary>
    private float _localDeathStamp = -1f;

    /// <summary>[T68] Resolve a player net id to its display name — the port's faithful <c>entcs_GetName</c>
    /// stand-in for the shownames overlay. The port has no separate entcs name stream, so the name comes from the
    /// networked scoreboard rows (each carries netId → name; see <see cref="XonoticGodot.Net.ScoreRowWire"/>).
    /// Returns "" when no row is known yet (the tag then shows only the status bar, like QC's blank entcs name).</summary>
    private string ResolveScoreboardName(int netId)
    {
        XonoticGodot.Net.ScoreboardWire? sb = _client?.LatestScoreboard;
        if (sb is null)
            return "";
        foreach (XonoticGodot.Net.ScoreRowWire row in sb.Rows)
            if (row.NetId == netId)
                return row.Name ?? "";
        return "";
    }

    /// <summary>[T68] The local client's team for the shownames <c>sameteam</c> gate — the C# stand-in for the
    /// local <c>entcs</c> team. A listen server reads the local server <see cref="Player"/>'s team directly; a
    /// pure client falls back to the local scoreboard row's team (the networked entcs team slice). 0 (no team /
    /// FFA) means no player is "sameteam", matching QC where a teamless local has only enemy tags.</summary>
    private int LocalShownamesTeam()
    {
        if (LocalServerPlayer is { } self)
            return (int)self.Team;
        XonoticGodot.Net.ScoreboardWire? sb = _client?.LatestScoreboard;
        int localId = _client?.LocalNetId ?? 0;
        if (sb is not null)
            foreach (XonoticGodot.Net.ScoreRowWire row in sb.Rows)
                if (row.NetId == localId)
                    return row.Team;
        return Teams.None;
    }

    // [Score panel] last-fed scoreboard reference, so the in-game Score overlay rebuilds only on a data change
    // (the wire object is replaced per decode — identity by reference, like UpdateScoreboard).
    private XonoticGodot.Net.ScoreboardWire? _lastScorePanelFed;

    /// <summary>
    /// Feed the in-game Score OVERLAY (#7 — the corner standing readout, distinct from the full scoreboard) from
    /// the SAME networked source the scoreboard uses (<see cref="ClientNet.LatestScoreboard"/> +
    /// <see cref="ClientNet.LocalNetId"/>). Best-effort feed (goal 7): own primary score + place, the rankings
    /// leaderboard rows, and (teamplay) per-team scores. The panel self-blanks until fed (<c>HasData</c>), so a
    /// pre-first-snapshot client draws nothing. Rebuilds only when the wire object changes (cheap per frame).
    /// </summary>
    private void UpdateScore()
    {
        if (_fullHud is null || _client is null)
            return;
        XonoticGodot.Game.Hud.ScorePanel? panel = _fullHud.GetPanel<XonoticGodot.Game.Hud.ScorePanel>();
        if (panel is null)
            return;

        XonoticGodot.Net.ScoreboardWire? sb = _client.LatestScoreboard;
        if (sb is null)
            return; // no networked scores yet → panel self-blanks
        if (ReferenceEquals(sb, _lastScorePanelFed))
            return; // unchanged since last feed
        _lastScorePanelFed = sb;

        // Primary score column index within the wire's NetworkedFields order (QC scores_primary). Default 0
        // (SP_SCORE) when no gametype refined it. The wire's Columns array is in NetworkedFields order.
        var fields = XonoticGodot.Common.Gameplay.Scoring.GameScores.NetworkedFields;
        XonoticGodot.Common.Gameplay.Scoring.ScoreField? primary =
            XonoticGodot.Common.Gameplay.Scoring.GameScores.Primary;
        int primaryIdx = 0;
        if (primary is not null)
            for (int i = 0; i < fields.Count; i++)
                if (ReferenceEquals(fields[i], primary)) { primaryIdx = i; break; }
        XonoticGodot.Common.Gameplay.Scoring.ScoreFlags primaryFlags =
            primary?.Flags ?? XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.None;

        bool teamplay = XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay;
        bool spectating = _client.SpectateeStatus != 0 && _client.SpectateeStatus != _client.LocalNetId;

        // ---- build a sorted (primary desc) snapshot of the rows ----
        var sorted = new System.Collections.Generic.List<XonoticGodot.Net.ScoreRowWire>(sb.Rows);
        int PrimaryOf(XonoticGodot.Net.ScoreRowWire r) =>
            (r.Columns is not null && primaryIdx < r.Columns.Length) ? r.Columns[primaryIdx] : 0;
        sorted.Sort((a, b) => PrimaryOf(b).CompareTo(PrimaryOf(a)));

        // ---- own standing (place + primary score) ----
        int localId = _client.LocalNetId;
        int selfScoreVal = 0, selfPlace = 0;
        bool haveSelf = false;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].NetId != localId) continue;
            haveSelf = true;
            selfPlace = i + 1; // 1-based
            selfScoreVal = PrimaryOf(sorted[i]);
            break;
        }
        if (haveSelf)
            panel.SetSelf(
                XonoticGodot.Common.Gameplay.Scoring.GameScores.ScoreString(primaryFlags, selfScoreVal),
                selfPlace);

        // FFA gap to the next player above/below (QC me.scores - pl.scores): the signed gap to the nearest rival.
        if (haveSelf && !teamplay && sorted.Count > 1)
        {
            // Gap to the player immediately ahead (negative = behind), or to the one behind if leading.
            int rivalVal = selfPlace > 1 ? PrimaryOf(sorted[selfPlace - 2]) : PrimaryOf(sorted[selfPlace]);
            panel.SetSelfGap(selfScoreVal - rivalVal, hasGap: true);
        }
        else
        {
            panel.SetSelfGap(0f, hasGap: false);
        }

        // ---- rankings leaderboard rows ----
        var rows = new System.Collections.Generic.List<XonoticGodot.Game.Hud.ScorePanel.RankRow>(sorted.Count);
        foreach (XonoticGodot.Net.ScoreRowWire r in sorted)
        {
            string scoreStr = XonoticGodot.Common.Gameplay.Scoring.GameScores.ScoreString(primaryFlags, PrimaryOf(r));
            rows.Add(new XonoticGodot.Game.Hud.ScorePanel.RankRow(r.Name, scoreStr, r.Team, r.NetId == localId));
        }
        panel.SetRankings(rows);

        // ---- team scores (teamplay) ----
        if (teamplay && sb.Teams.Count > 0)
        {
            int myTeam = LocalServerPlayer is { } lp ? (int)lp.Team : 0;
            var teams = new System.Collections.Generic.List<XonoticGodot.Game.Hud.ScorePanel.TeamScore>(sb.Teams.Count);
            foreach ((int team, int score) in sb.Teams)
                teams.Add(new XonoticGodot.Game.Hud.ScorePanel.TeamScore(
                    team, score.ToString(System.Globalization.CultureInfo.InvariantCulture), team == myTeam));
            panel.SetTeamScores(teams);
            panel.SetMode(XonoticGodot.Game.Hud.ScorePanel.ScoreMode.Team, spectating);
        }
        else
        {
            panel.SetMode(XonoticGodot.Game.Hud.ScorePanel.ScoreMode.FreeForAll, spectating);
        }
    }

    /// <summary>Feed + toggle the mod-icons panel (QC HUD_ModIcons_SetFunc → gametype.m_modicons) from the
    /// networked per-mode status (T53). Cheap: property writes + a Visible toggle; the panel repaints itself.</summary>
    private void UpdateModIcons()
    {
        if (_fullHud is null || _client is null)
            return;
        ModIconsPanel panel = _fullHud.ModIcons;
        XonoticGodot.Net.GametypeStatusBlock.Decoded? ms = _client.LatestModeStatus;
        if (ms is null)
        {
            if (panel.Visible) panel.Visible = false;
            return;
        }
        switch (ms.Mode)
        {
            case XonoticGodot.Net.GametypeStatusBlock.Kind.ClanArena:
                panel.Mode = ModIconsPanel.ModIconsMode.ClanArena;
                panel.SetAliveCounts(ms.Alive[0], ms.Alive[1], ms.Alive[2], ms.Alive[3]);
                break;
            case XonoticGodot.Net.GametypeStatusBlock.Kind.FreezeTag:
                panel.Mode = ModIconsPanel.ModIconsMode.FreezeTag;
                panel.SetAliveCounts(ms.Alive[0], ms.Alive[1], ms.Alive[2], ms.Alive[3]);
                break;
            case XonoticGodot.Net.GametypeStatusBlock.Kind.KeyHunt:
                panel.Mode = ModIconsPanel.ModIconsMode.Keyhunt;
                panel.ObjectiveStatus = unchecked((int)ms.KeyState);
                break;
            case XonoticGodot.Net.GametypeStatusBlock.Kind.Survival:
                panel.Mode = ModIconsPanel.ModIconsMode.Survival;
                panel.SurvivalStatus = ms.MyStatus;
                break;
            // [W1-mod-icons] the Wave-1 objective feeds net-server added to GametypeStatusBlock — establish the
            // client dispatch so Wave-3's per-mode render (already present for CTF/Domination, Keepaway below)
            // is fed live. CTF: the bitpacked OBJECTIVE_STATUS flag pack drives HUD_Mod_CTF.
            case XonoticGodot.Net.GametypeStatusBlock.Kind.Ctf:
                panel.Mode = ModIconsPanel.ModIconsMode.Ctf;
                panel.ObjectiveStatus = unchecked((int)ms.ObjectiveStatus);
                break;
            // Domination: STAT(DOM_TOTAL_PPS / DOM_PPS_*). The wire packs [0]=total, [1..4]=red,blue,yellow,pink.
            case XonoticGodot.Net.GametypeStatusBlock.Kind.Domination:
                panel.Mode = ModIconsPanel.ModIconsMode.Domination;
                panel.SetDominationPps(ms.DominationPps[1], ms.DominationPps[2], ms.DominationPps[3],
                    ms.DominationPps[4], ms.DominationPps[0]);
                break;
            // Keepaway: the KA_CARRYING mod icon. The wire carries the carrier's net id (0 = nobody); the QC stat
            // bit means "the LOCAL player carries it", so resolve it against the local net id here.
            case XonoticGodot.Net.GametypeStatusBlock.Kind.Keepaway:
                panel.Mode = ModIconsPanel.ModIconsMode.Keepaway;
                panel.KeepawayCarrying = ms.CarrierNetId != 0 && ms.CarrierNetId == _client.LocalNetId;
                break;
            // NexBall: the QC nexball_carrying mod-icon. Same shape as Keepaway — the wire carries the carrier's net
            // id (0 = nobody); resolve it against the local net id so the icon shows only while WE hold the ball.
            case XonoticGodot.Net.GametypeStatusBlock.Kind.NexBall:
                panel.Mode = ModIconsPanel.ModIconsMode.NexBall;
                panel.NexBallCarrying = ms.CarrierNetId != 0 && ms.CarrierNetId == _client.LocalNetId;
                // QC HUD_Mod_NexBall keys the power-meter bar off the LOCAL NB_METERSTART — show the triangle-wave
                // charge bar only while the local player is the carrier; otherwise feed -1 (inactive, no bar). The
                // networked meter phase rides the NexBall block (GametypeStatusBlock.Decoded.NexBallMeterPhase).
                panel.NexBallMeterPhase =
                    (ms.CarrierNetId != 0 && ms.CarrierNetId == _client.LocalNetId) ? ms.NexBallMeterPhase : -1;
                break;
            // Team Keepaway: STAT(TKA_BALLSTATUS) — the carrying / per-team-taken / dropped bit pack is already
            // computed per-recipient on the server, so feed it straight to HUD_Mod_TeamKeepaway.
            case XonoticGodot.Net.GametypeStatusBlock.Kind.TeamKeepaway:
                panel.Mode = ModIconsPanel.ModIconsMode.TeamKeepaway;
                panel.TkaBallStatus = ms.TkaBallStatus;
                break;
            // LMS: the recycled REDALIVE/BLUEALIVE/OBJECTIVE_STATUS leader stats drive HUD_Mod_LMS_Draw (the
            // leader-count icon + the colored +N lives lead). The panel hides itself when the leader count is 0.
            case XonoticGodot.Net.GametypeStatusBlock.Kind.Lms:
                panel.Mode = ModIconsPanel.ModIconsMode.Lms;
                panel.LmsLeaderCount = ms.LmsLeaderCount;
                panel.LmsLivesDiff = ms.LmsLivesDiff;
                panel.LmsLeadersVisible = ms.LmsLeadersVisible;
                break;
            default:
                panel.Mode = ModIconsPanel.ModIconsMode.None;
                break;
        }

        // Race/CTS mod-icon (QC HUD_Mod_Race, cl_race.qc:59-164): not sent via GametypeStatusBlock (Race/CTS have
        // no team-objective pack), so fall back to reading the live server gametype directly when on a listen server.
        // Feeds the personal-best (local racer's rank-1 record from RaceRecords), server record, and the latest
        // medal-flash status derived from the same Race.LastRecord that UpdateRacePanels already uses.
        // QC guard: only show when the primary score column is SFL_TIME and it is NOT a team race
        // (QC "if(!(scores_flags(ps_primary) & SFL_TIME) || teamplay)").
        if (panel.Mode == ModIconsPanel.ModIconsMode.None)
        {
            XonoticGodot.Common.Gameplay.GameType? sgt = _serverWorld?.GameType;
            Player? me = LocalServerPlayer;
            var raceGt = sgt as XonoticGodot.Common.Gameplay.Race;
            var ctsGt  = sgt as XonoticGodot.Common.Gameplay.Cts;
            if ((raceGt is not null || ctsGt is not null) && me is not null && !_client!.IsObserving)
            {
                // QC: only show for non-team qualifying/CTS (SFL_TIME primary and !teamplay).
                bool teamRace = raceGt is not null && raceGt.RaceTeams >= 2;
                bool qualifying = raceGt is not null && raceGt.Qualifying;
                bool show2 = qualifying || ctsGt is not null || (raceGt is not null && !teamRace);
                if (show2)
                {
                    panel.Mode = ModIconsPanel.ModIconsMode.Race;

                    // QC crecordtime (ClientProgsDB): the local racer's personal best = their own rank in the
                    // server's per-map top-99, identified by PersistentId (UID). 0 = no personal best yet.
                    string mapName  = raceGt?.MapName ?? ctsGt!.MapName;
                    string recType  = raceGt?.RecordType ?? ctsGt!.RecordType;
                    panel.RaceModIconPb = XonoticGodot.Common.Gameplay.RaceRecords.ReadPersonalBest(mapName, recType, me.PersistentId);

                    // QC race_server_record (RACE_NET_SERVER_RECORD): rank-1 time for this map.
                    panel.RaceModIconServerRecord = raceGt?.ServerRecord ?? ctsGt!.ServerRecord;

                    // QC race_SendStatus → race_status / race_status_name / race_status_time: the medal flash.
                    // Reuse the same stamp+player reference that UpdateRacePanels uses for the split-timer panel so
                    // both panels flash on the same event without double-counting.
                    float lastRecT = raceGt?.LastRecordTime ?? ctsGt!.LastRecordTime;
                    XonoticGodot.Common.Gameplay.Player? lastRecP = raceGt?.LastRecordPlayer ?? ctsGt!.LastRecordPlayer;
                    if (ReferenceEquals(lastRecP, me) && lastRecT > 0f && lastRecT != _lastFedModIconRecordTime)
                    {
                        _lastFedModIconRecordTime = lastRecT;
                        XonoticGodot.Common.Gameplay.RaceRecordResult r = raceGt?.LastRecord ?? ctsGt!.LastRecord;
                        int statusMi = r.Kind switch
                        {
                            XonoticGodot.Common.Gameplay.RaceRecordKind.Fail => 0,
                            _ when r.IsServerRecord => 3,
                            XonoticGodot.Common.Gameplay.RaceRecordKind.NewImproved => 1,
                            _ => 2,
                        };
                        panel.RaceModIconStatus = statusMi;
                        panel.RaceModIconStatusName = me.NetName;
                        panel.RaceModIconStatusRankIsMine = true;
                    }
                }
            }
        }

        panel.MyTeam = ms.MyTeamIndex;
        if (ms.TeamCount > 0) panel.TeamCount = ms.TeamCount;
        bool show = panel.Mode != ModIconsPanel.ModIconsMode.None;
        if (panel.Visible != show)
            panel.Visible = show;
    }

    // [T57] last-fed accuracy generation (rebuild the dictionaries only when the server's changes). -1 = none yet.
    private int _lastFedAccuracyGen = -1;
    private readonly System.Collections.Generic.Dictionary<string, int> _itemStatsById = new();   // item icon name -> pickup count (QC Inventory.inv_items)
    private readonly System.Collections.Generic.Dictionary<int, int> _accuracyById = new();          // weapon registry id → hit% (-1 never fired)
    private readonly System.Collections.Generic.Dictionary<string, float> _accuracyByNetName = new(); // weapon NetName → hit% (0 never fired)

    /// <summary>
    /// [T57] Decode the networked owner accuracy bytes (QC ENT_CLIENT_ACCURACY, owner-only) into the two HUD
    /// accuracy grids when the server's change generation moves: the scoreboard's per-id grid
    /// (<see cref="ScoreboardPanel.SetAccuracy"/>, registry-id → hit% 0..100, -1 never fired) and the weapons
    /// panel's per-NetName grid (<see cref="WeaponsPanel.SetAccuracy"/>, NetName → %). QC accuracy_byte decode:
    /// 0 → never fired (-1 for the scoreboard's sentinel / omitted from the weapons map), 255 → 100% (capped),
    /// else byte−1 = the percentage.
    /// </summary>
    private void UpdateAccuracy()
    {
        if (_fullHud is null || _client is null)
            return;
        if (_client.LocalAccuracyGeneration == _lastFedAccuracyGen)
            return;
        _lastFedAccuracyGen = _client.LocalAccuracyGeneration;

        byte[] bytes = _client.LocalAccuracyBytes;
        _accuracyById.Clear();
        _accuracyByNetName.Clear();
        int count = System.Math.Min(bytes.Length, Registry<Weapon>.Count);
        for (int id = 0; id < count; id++)
        {
            int b = bytes[id];
            int pct = b == 0 ? -1 : (b >= 255 ? 100 : b - 1); // QC accuracy_byte: 0 never fired, 255 >100%, else b-1
            _accuracyById[id] = pct;
            if (b != 0) // only weapons actually fired get a weapons-panel tint (QC skips the unfired ones)
                _accuracyByNetName[Registry<Weapon>.ById(id).NetName] = pct;
        }
        _scoreboard.SetAccuracy(_accuracyById);
        _fullHud.Weapons.SetAccuracy(_accuracyByNetName);
        FeedScoreboardWeaponSets();
    }

    /// <summary>
    /// QC <c>WepSet_GetFromStat()</c> / <c>WepSet_GetFromStat_InMap()</c> (scoreboard.qc:1808-1809): the two sets
    /// that decide which never-fired weapons still earn a cell in the scoreboard's accuracy grid — the weapons
    /// the local player carries, and the weapons that exist on the loaded map. Without them the grid would list
    /// EVERY registered weapon (including the OK/turret/vehicle variants that never appear in a normal match).
    ///
    /// Owned comes from the local player's live WepSet (listen server: the server Player; remote client: the HUD
    /// mirror the owner block feeds). In-map is a listen-server scan of the spawned weapon pickups; a pure
    /// <c>--connect</c> client has no item list of its own, so it falls back to the owned set alone (a strictly
    /// smaller grid, never a wrong one).
    /// </summary>
    private void FeedScoreboardWeaponSets()
    {
        Entity? local = LocalServerPlayer ?? _hudMirror;
        if (local is not null)
        {
            _sbOwnedWeapons.Clear();
            foreach (Weapon w in local.OwnedWeaponSet.Weapons()) _sbOwnedWeapons.Add(w.RegistryId);
            _scoreboard.SetOwnedWeapons(_sbOwnedWeapons);
        }

        // The in-map set only changes on map load; scan once per map.
        if (_serverWorld is not null && _sbWeaponsInMapMap != _map)
        {
            _sbWeaponsInMapMap = _map;
            _sbWeaponsInMap.Clear();
            foreach (Entity e in _serverWorld.Services.Entities.All)
            {
                if (e.IsFreed || e.Pickup is not { IsWeaponPickup: true }) continue;
                foreach (Weapon w in e.OwnedWeaponSet.Weapons()) _sbWeaponsInMap.Add(w.RegistryId);
            }
            _scoreboard.SetWeaponsInMap(_sbWeaponsInMap);
        }
    }

    private readonly System.Collections.Generic.List<int> _sbOwnedWeapons = new();
    private readonly System.Collections.Generic.HashSet<int> _sbWeaponsInMap = new();
    private string _sbWeaponsInMapMap = "";

    /// <summary>The scoreboard title: the active gametype's display name from the networked ScoreInfo (else the
    /// configured gametype). QC the scoreboard header gametype name.</summary>
    private string ScoreboardTitle()
    {
        string gt = XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype;
        return string.IsNullOrEmpty(gt) ? "Scoreboard" : gt.ToUpperInvariant();
    }

    /// <summary>Feed the scoreboard's fraglimit/timelimit/map-name header from the live cvar store (QC
    /// Scoreboard_Fraglimit_Draw reads STAT(FRAGLIMIT)/TIMELIMIT; we read the cvars the server replicated /
    /// the host set). Best-effort: a pure client without those cvars simply shows no header line.</summary>
    private void FeedScoreboardHeader()
    {
        _scoreboard.MapName = _map;
        var cvars = Api.Services?.Cvars;
        if (cvars is null) return;
        _scoreboard.FragLimit = (int)cvars.GetFloat("fraglimit");
        _scoreboard.TimeLimitMinutes = (int)cvars.GetFloat("timelimit");
        // QC scoreboard.qc:2546-2547 STAT(LEADLIMIT)/STAT(LEADLIMIT_AND_FRAGLIMIT): the "^2+N" lead-limit header
        // term and the "& "-vs-"/ " delimiter (both-limits-required). Only DM-family modes set a leadlimit; for
        // the rest the cvar is 0 and BuildLimitsHeader's (ll < fl || fl <= 0) guard drops the term.
        _scoreboard.LeadLimit = (int)cvars.GetFloat("leadlimit");
        _scoreboard.LeadAndFragLimit = cvars.GetFloat("leadlimit_and_fraglimit") != 0f;
        // QC gametype.m_hidelimits (mapinfo.qh:128, GAMETYPE_FLAG_HIDELIMITS; scoreboard.qc:2551): only LMS sets
        // it (lms.qh:11), suppressing the frag/lead limit terms — only the timelimit shows on that line.
        _scoreboard.HideLimits = XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype == "lms";
        // QC global `campaign` (scoreboard.qc:2574): a single-player campaign suppresses the "N/M players" line.
        _scoreboard.Campaign = !string.IsNullOrEmpty(_campaignName);
        FeedMapStats();
    }

    /// <summary>QC <c>Scoreboard_MapStats_Draw</c> feed (STAT(MONSTERS_*) / STAT(SECRETS_*)): the live map-stats
    /// counts. These tick independently of the score version, so the live caller (UpdateScoreboard) feeds them
    /// every frame while the scoreboard is shown — not only when the row data changes.</summary>
    private void FeedMapStats()
    {
        // [T43] Feed the scoreboard map-stats row (QC monsters_setstatus → STAT(MONSTERS_TOTAL/KILLED)).
        // -1 hides the row when there are no monsters (DrawMapStats gates on MonstersTotal > 0).
        // QC sv_invasion.qc MUTATOR_HOOKFUNCTION(inv, SV_StartFrame): for INV_TYPE_ROUND the wave progress is
        // monsters_total=inv_maxspawned / monsters_killed=inv_numkilled — the per-round SPAWNED wave counters, NOT
        // the NATURAL map-placed totals (which MonsterAI tracks and which exclude the spawned wave monsters). So in
        // ROUND Invasion publish the live wave counts; every other mode keeps the natural map-monster totals.
        if (_serverWorld?.GameType is XonoticGodot.Common.Gameplay.Invasion inv
            && inv.Type == XonoticGodot.Common.Gameplay.Invasion.InvasionType.Round)
        {
            _scoreboard.MonstersTotal  = inv.Wave.MaxSpawned > 0 ? inv.Wave.MaxSpawned : -1;
            _scoreboard.MonstersKilled = inv.Wave.Killed;
        }
        else
        {
            _scoreboard.MonstersTotal  = XonoticGodot.Common.Gameplay.MonsterAI.MonstersTotal > 0 ? XonoticGodot.Common.Gameplay.MonsterAI.MonstersTotal : -1;
            _scoreboard.MonstersKilled = XonoticGodot.Common.Gameplay.MonsterAI.MonstersKilled;
        }

        // QC trigger_secret: STAT(SECRETS_TOTAL)/STAT(SECRETS_FOUND), accumulated by trigger_secret spawn/touch
        // (Triggers.SecretSetup/SecretTouch → MapObjectsState). -1 hides the row when the map has no secrets.
        int secretsTotal = XonoticGodot.Common.Gameplay.MapObjectsState.SecretsTotal;
        _scoreboard.SecretsTotal = secretsTotal > 0 ? secretsTotal : -1;
        _scoreboard.SecretsFound = XonoticGodot.Common.Gameplay.MapObjectsState.SecretsFound;
    }

    /// <summary>
    /// Seed the carrier's origin/velocity from the latest authoritative snapshot once the handshake lands, so
    /// the predictor's first replay starts at the server's spawn position. (Without this the carrier would
    /// predict from the world origin until the first reconcile pulled it across — a visible snap.)
    /// </summary>
    private void SeedCarrierFromServer()
    {
        if (_carrier is null || _client is null)
            return;
        _carrier.Origin = _client.PredictedOrigin;
        _carrier.OldOrigin = _carrier.Origin;
        _carrier.Velocity = _client.PredictedVelocity;
    }

    // =====================================================================================
    //  Input sampling (WASD/mouse → InputCommand) — mirrors PlayerController's gather
    // =====================================================================================

    public override void _UnhandledInput(InputEvent @event)
    {
        // HUD configure-mode editor (QC HUD_Panel_InputEvent, main.qc:504): while `_hud_configure 1` the editor
        // intercepts mouse/keyboard for panel drag/resize/keyboard-edit. It self-gates (no-op + returns false
        // when not configuring), so this costs nothing in normal play. When it consumes the event we mark it
        // handled so it doesn't fall through to gameplay binds / mouse-look below. Console-open still wins (the
        // console overlay handles its own keys first; this runs only on events it didn't consume).
        if (!ConsoleState.IsOpen && _fullHud?.ConfigEditor is { } editor && editor.HandleInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        // (E3/E5) Map-editor interaction. Runs only while free-flying in the editor gametype, and consumes the
        // event when it acts so a grab doesn't also register as a weapon impulse or a spectate-cycle.
        if (!ConsoleState.IsOpen && HandleEditorInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        // Minigame menu toggle (QC the +minigamemenu bind — no default key in Base; we bind 'M'). Opens/closes
        // the in-game Create/Join/Current-Game menu. Active even during play (so you can start a game), but not
        // while the console is open. When the menu is open it captures the cursor so the player can click it.
        if (!ConsoleState.IsOpen && _minigame is not null
            && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M })
        {
            _minigame.ToggleMenu();
            // The cursor show/recapture is driven each frame in _Process from MinigameMenuOpen (which now reflects
            // the new menu state), so it stays correct even when a board is still active under a closed menu.
            GetViewport().SetInputAsHandled();
            return;
        }

        // Interactive scoreboard (QC HUD_Scoreboard_InputEvent, main.qc:500). Two entry points:
        //   * TAB held + Escape opens the navigation UI (QC main.qc:545-551 — the ESC branch checks S_TAB);
        //   * while it is up it owns the keyboard: arrows/TAB/Enter/Ctrl-chords all go to the panel.
        // Placed before the radar/mapvote handlers and the gameplay binds so an arrow key navigates instead of
        // switching weapons. Console-open still wins (handled above).
        if (!ConsoleState.IsOpen && _scoreboard is not null && GodotObject.IsInstanceValid(_scoreboard)
            && @event is InputEventKey { Echo: false } sbKey)
        {
            bool shift = sbKey.ShiftPressed, ctrl = sbKey.CtrlPressed;
            // (Escape — open and close — is claimed by Shell's Escape chain via MenuCommand.ScoreboardEscape.)

            if (_scoreboard.UiMode != 0)
            {
                // QC acts on the PRESS and swallows the matching release, so the key never reaches gameplay.
                if (sbKey.Pressed ? _scoreboard.UiHandleKey(sbKey.Keycode, shift, ctrl)
                                  : _scoreboard.UiConsumesRelease(sbKey.Keycode, ctrl))
                {
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }

        // Maximized radar mouse input (QC HUD_Radar_Mouse / hud_panel_radar_maximized): while the radar is maximized
        // it owns the mouse — cursor motion tracks the panel, a left click on a control point in Onslaught issues
        // `cmd ons_spawn <x> <y> <z>` (the server's spawn-point pick), and right-click / Esc closes it. Mirrors the
        // minigame/quickmenu pattern: active only while maximized and the console is closed, and it swallows the
        // events so they never fall through to mouse-look / weapon binds. The cursor is freed in _Process.
        if (_radar is { Maximized: true } && !ConsoleState.IsOpen)
        {
            switch (@event)
            {
                case InputEventMouseMotion:
                    _radar.SetMousePosition(_radar.GetLocalMousePosition());
                    GetViewport().SetInputAsHandled();
                    return;
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                    if (_radar.Clickable
                        && _radar.HandleClick(_radar.GetLocalMousePosition(), out System.Numerics.Vector3 wp))
                        _client?.SendStringCommand($"cmd ons_spawn {wp.X} {wp.Y} {wp.Z}");
                    GetViewport().SetInputAsHandled();
                    return;
                case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                    _radar.SetMaximized(false);
                    GetViewport().SetInputAsHandled();
                    return;
                case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                    _radar.SetMaximized(false);
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }

        // Map-vote panel input (QC client/mapvoting.qc MapVote_InputEvent): while the end-of-match ballot is
        // showing, arrows move the selection and enter/space/digits/click cast it as `impulse N` (routed through
        // the server's impulse path, the port's MapVote_SendChoice). Runs before the gameplay binds so a digit/
        // arrow during the vote casts instead of switching weapons. Console-open still wins (handled above).
        if (!ConsoleState.IsOpen && _fullHud is { } voteHud && voteHud.MapVote.Visible
            && HandleMapVoteInput(voteHud.MapVote, @event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        // Zoom-scroll (QC client/view.qc View_InputEvent / ZoomScroll): while +zoom is held and cl_zoomscroll is on,
        // the mousewheel adjusts the zoom factor instead of switching weapons. View_InputEvent consumes the wheel
        // event in that case (returns true), so feed it to the shared view BEFORE the gameplay binds and swallow the
        // event when it applies. Inert otherwise (returns control to the weapon-next/prev binds below).
        if (!ConsoleState.IsOpen && !GetTree().Paused && !MinigameMenuOpen
            && @event is InputEventMouseButton { Pressed: true } wheel
            && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown)
            && _view.ZoomHeld
            && CvarOr(Api.Cvars, "cl_zoomscroll", 1f) != 0f && CvarOr(Api.Cvars, "cl_zoomscroll_scale", 0.2f) != 0f)
        {
            _view.NotifyZoomScroll(wheel.ButtonIndex == MouseButton.WheelUp);
            GetViewport().SetInputAsHandled();
            return;
        }

        // Bind-driven gameplay input (DP key bindings): a keyboard/mouse-button press/release drives the +/-
        // held-button state and runs one-shot bound commands. Routed through RunBoundCommand, which converts the
        // WEAPON-SWITCH / RELOAD binds into a C2S impulse (the QC usercmd.impulse channel — the real way a human
        // switches weapons on the net path) and forwards everything else (kill/say/team/…) to the shared
        // interpreter. Frozen while the console is open or the match is paused (a key the console didn't consume
        // still reaches here, so the explicit ConsoleState gate is what stops binds firing under the open console),
        // and while the chat prompt is open (DP key_dest = message swallows ALL input, mouse buttons included —
        // the prompt only consumes KEY events, so without this gate a MOUSE bind still flips latches / fires the
        // held +attack the moment the prompt closes).
        if (!ConsoleState.IsOpen && !GetTree().Paused && !MinigameMenuOpen
            && !XonoticGodot.Game.Hud.ChatPrompt.IsOpen
            && @event is InputEventKey or InputEventMouseButton)
            BindInput.HandleEvent(@event, RunBoundCommand);

        // Accumulate mouse-look while the cursor is captured (the Shell owns Escape + the mouse around the
        // in-game menu) — DP's IN_Move: raw deltas sum into in_mouse_x/y over the frame; the application (the
        // m_accelerate/m_filter block + the m_yaw/m_pitch × sensitivity scale) happens ONCE per render frame in
        // FlushMouseLook, because the accel math needs realframetime. Events are dispatched before _Process, so
        // this adds no latency.
        // QC FixIntermissionClient / SVC_INTERMISSION: at intermission the engine freezes the player view at the
        // intermission camera and mouse-look is locked. Mirror it by ignoring look input while the match is over
        // (the angles latched at intermission entry are held), so the scoreboard view doesn't swing with the mouse.
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured
            && !(_client?.MatchIntermission ?? false))
        {
            _mouseDx += motion.Relative.X;
            _mouseDy += motion.Relative.Y;
        }
    }

    /// <summary>
    /// The per-frame half of the DP mouse pipeline (cl_input.c CL_Input:550-694): run the accumulated deltas
    /// through the m_accelerate/m_filter block (<see cref="XonoticGodot.Common.Input.MouseAccel"/>), then apply
    /// — yaw -= m_yaw · dx · sensitivity · sensitivityscale; pitch += m_pitch · dy · sensitivity ·
    /// sensitivityscale (m_pitch SIGNED, negative = invert-Y; SensitivityScale = QC setsensitivityscale; Xonotic
    /// never sets cl.viewzoom, so that DP factor stays 1). Pitch clamps to in_pitch_min/max (DP ±90 — CL_AdjustAngles).
    /// Runs on ZERO-input frames too: averagespeed still decays through the accel lowpass and the m_filter tail
    /// drains, exactly like DP running the block unconditionally each frame.
    /// A hardcoded 0.025 here once replaced m_yaw (DP 0.022) — +13.6% view turn per count vs Base at equal
    /// sensitivity, which in air-strafe turning reads as sharper movement, not just hotter aim.
    /// </summary>
    // m_smoothdt (2026-07-26 wobble hunt): the yaw/pitch channel used to be the ONE motion channel riding
    // the raw frame cadence — counts accumulate over the real previous pump interval but are displayed for
    // the NEXT frame's duration, so displayed rotation rate carried the full frame-time variance while
    // translation rode ConditionDt's smoothed timeline (the felt "stutter when moving the mouse in the
    // air"). Fix: rescale the post-accel deltas by (conditioned display estimate / real interval) so both
    // optical-flow channels share one timeline, with a per-axis carry ledger so the TOTAL rotation of any
    // swipe stays exactly Σ(counts) — 1:1 aim is preserved; only the per-frame distribution shifts by
    // ≤±50% of one frame's counts. The ledger drains fast (50%/frame) when input stops so a swipe's tail
    // completes within ~25 ms and there is no post-stop drift. m_smoothdt 0 = legacy raw application.
    private float _mouseCarryX, _mouseCarryY;

    private void FlushMouseLook(float realFrameTime, float displayDt)
    {
        float dx = _mouseDx, dy = _mouseDy;
        _mouseDx = 0f;
        _mouseDy = 0f;
        (float mx, float my) = _mouseAccel.Apply(dx, dy, realFrameTime, in _mouseAccelCv, LookSensitivity());
        if (_mouseSmoothDtCv && realFrameTime > 0f && displayDt > 0f)
        {
            float scale = Math.Clamp(displayDt / realFrameTime, 0.5f, 2f);
            float outX = mx * scale, outY = my * scale;
            _mouseCarryX += mx - outX;
            _mouseCarryY += my - outY;
            float repay = (dx != 0f || dy != 0f) ? 0.25f : 0.5f;
            outX += _mouseCarryX * repay; _mouseCarryX *= 1f - repay;
            outY += _mouseCarryY * repay; _mouseCarryY *= 1f - repay;
            mx = outX; my = outY;
        }
        else
        {
            _mouseCarryX = 0f; _mouseCarryY = 0f; // toggled off mid-session: drop sub-count residue
        }
        if (mx == 0f && my == 0f)
            return;
        float sens = LookSensitivity() * _view.SensitivityScale;
        _viewAngles.Y -= mx * sens * MYaw();
        _viewAngles.X += my * sens * MPitch();
        _viewAngles.X = Mathf.Clamp(_viewAngles.X, _pitchMinCv, _pitchMaxCv);
    }

    /// <summary>
    /// QC client/mapvoting.qc <c>MapVote_InputEvent</c>: feed a keyboard/mouse event to the showing map-vote
    /// panel. Arrows move the selection; enter/space/digits/left-click cast a vote, which the panel forwards
    /// (via <see cref="MapVotePanel.CastChoice"/>) to <see cref="CastMapVote"/>. Returns true when the panel
    /// consumed the event. The <see cref="MapVotePanel.CastChoice"/> hook is wired lazily here so it always
    /// targets the current local server player.
    /// </summary>
    private bool HandleMapVoteInput(XonoticGodot.Game.Hud.MapVotePanel panel, InputEvent @event)
    {
        panel.CastChoice = CastMapVote; // QC MapVote_SendChoice → localcmd("impulse N")

        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } keyEv:
                return panel.HandleVoteKey(keyEv.Keycode, keyEv.CtrlPressed);
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb:
                return panel.HandleVoteClick(mb.Position - panel.PanelRect.Position);
            case InputEventMouseMotion mm:
                // Hovering moves the selection but doesn't consume the event or cast (QC mv_mouse_selection).
                panel.HandleVoteHover(mm.Position - panel.PanelRect.Position);
                return false;
        }
        return false;
    }

    /// <summary>
    /// QC client <c>MapVote_SendChoice(index)</c> → <c>localcmd("impulse ", index+1)</c>: cast the local player's
    /// vote for the 0-based ballot option. On a listen server this routes through the in-process server's impulse
    /// command (the same path a console <c>impulse N</c> takes — <see cref="Commands.DispatchImpulse"/> →
    /// <see cref="MapVoting.CastVote"/>). On a pure remote client there is no server-side MapVoting object, so the
    /// cast rides the existing C2S impulse byte instead — <see cref="_pendingImpulse"/> is stamped and the next
    /// <see cref="InputCommand"/> carries impulse <c>N</c> to the server's gated impulse path, which forwards it to
    /// <see cref="MapVoting.CastVote"/> while the vote runs (the same call the listen path makes directly).
    /// </summary>
    private void CastMapVote(int index)
    {
        // On a pure remote (--connect) client there is no in-process server: cast the vote by stamping the C2S
        // impulse byte (QC MapVote_SendChoice → localcmd("impulse N")). The next InputCommand carries it to the
        // server, whose gated impulse path (Commands.DispatchImpulse) routes impulse 1..N to MapVoting.CastVote
        // while the vote runs — the same in-process call the listen path makes directly.
        if (_serverWorld is null)
        {
            _pendingImpulse = index + 1; // edge-triggered: SampleInput stamps it onto the next command, then clears it
            return;
        }
        Player? me = LocalServerPlayer;
        if (me is null)
            return;
        // WS1 stage 3: world mutation from a main-thread input event — sim thread when threaded (inline when not).
        GameWorld w = _serverWorld;
        if (_server is { } srv)
            srv.RunOnSimThread(() => w.Commands.Execute($"impulse {index + 1}", isServerConsole: false, caller: me));
        else
            w.Commands.Execute($"impulse {index + 1}", isServerConsole: false, caller: me);
    }

    /// <summary>
    /// Run a one-shot console command issued by a bind on the NET path. Weapon-switch / reload binds are turned
    /// into a C2S impulse number (QC usercmd.impulse — the faithful way the weapon command rides the move
    /// command): the number is stamped into <see cref="_pendingImpulse"/>, which <see cref="SampleInput"/> stamps
    /// onto the next <see cref="InputCommand"/> (edge-triggered, one-shot). The server dispatches it through the
    /// gated <c>impulse</c> path (<c>WeaponImpulses.Handle</c>). Everything else (kill/say/team/+showscores side
    /// effects/…) forwards to the shared interpreter (<see cref="RunCommand"/>, wired by the Shell). Mirrors the
    /// command set in <c>PlayerController.RunBoundCommand</c> + <c>BindInput</c>'s weapon bind strings.
    /// </summary>
    private void RunBoundCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        // QuickMenu (goal 8): the `quickmenu` bind (BindInput maps a key to "quickmenu") toggles the panel — the
        // proper bind-driven hook (QC the quickmenu command). A bare `quickmenu` opens/closes; `quickmenu default
        // <submenu>` would descend (left to the panel's Open overload). The panel grabs cursor/focus while open.
        if (command.Equals("quickmenu", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("quickmenu ", StringComparison.OrdinalIgnoreCase))
        {
            _fullHud?.GetPanel<XonoticGodot.Game.Hud.QuickMenuPanel>()?.Toggle();
            return;
        }

        // Maximized radar (QC the `m` bind → +hud_panel_radar_maximized → cl_cmd hud radar 1): a toggle, like the
        // quickmenu. BindInput emits the press form `+hud_panel_radar_maximized` (and `hud radar 1` if bound directly);
        // both flip the maximized state. The release form (`-hud_panel_radar_maximized`) is a no-op — this is a press-
        // toggle, not a held button, so we swallow it without un-maximizing. The cursor is freed/recaptured in _Process
        // off _radar.Maximized (see the UiOwnsCursor block); _UnhandledInput drives the mouse-move / click-to-spawn.
        if (command.Equals("+hud_panel_radar_maximized", StringComparison.OrdinalIgnoreCase)
            || command.Equals("hud radar 1", StringComparison.OrdinalIgnoreCase))
        {
            _radar?.SetMaximized(!(_radar?.Maximized ?? false));
            return;
        }
        if (command.Equals("-hud_panel_radar_maximized", StringComparison.OrdinalIgnoreCase))
            return; // release of a press-toggle bind — ignored (matches the quickmenu toggle semantics)

        // (E2) The editor state toggle rides F9, which Base binds to the minigame HUD. Deliberately handled
        // BEFORE the free-fly gate below and NOT restricted to EDIT: the weapon binds cannot serve this, because
        // in PLAYTEST they go back to selecting weapons and the mapper would have no way to return to editing.
        if (IsEditorGametype
            && (command.Equals("cl_cmd hud minigame", StringComparison.OrdinalIgnoreCase)
                || command.Equals("editor_toggle", StringComparison.OrdinalIgnoreCase)))
        {
            Menu.MenuState.Interp?.ExecuteLine("editor_playtest");
            return;
        }

        // (E7) The mousewheel still steps the grid while free-flying. Unlike the digits — which the editor now
        // owns outright in HandleEditorInput — the wheel arrives here as a weapon bind, because it is bound to
        // weapnext/weapprev and there is no editor-side keycode to intercept.
        if (IsEditorFreeFly)
        {
            string? wheel = WeaponCommandToImpulse(command) switch
            {
                10 => "editor_grid_size +",
                12 => "editor_grid_size -",
                _ => null,
            };
            if (wheel is not null)
            {
                Menu.MenuState.Interp?.ExecuteLine(wheel);
                return;
            }
        }

        int imp = WeaponCommandToImpulse(command);
        if (imp != 0)
        {
            _pendingImpulse = imp; // edge-triggered: SampleInput consumes it on the next command, then clears it
            // Instant local feedback: begin lowering the gun the moment a weapon-SELECT key is pressed, so the
            // switch starts visibly the same frame instead of waiting for the server round-trip (EquipNetworkedWeapon
            // then raises the new gun when the change confirms). Only weapon-select impulses (group/next/prev/last/
            // best/by-id) — NOT drop (17) or reload (20) — and only when the switch could actually succeed
            // (SwitchCouldSucceedNow, playtest #31): Base plays NO animation on a denied switch, just the
            // "weapons/unavailable" sound. Auto-recovers if the server still denies (pure-client fallback).
            if (IsWeaponSwitchImpulse(imp) && SwitchCouldSucceedNow(imp)
                && _viewModel is not null && GodotObject.IsInstanceValid(_viewModel))
                _viewModel.PlayHolster();
            return;
        }

        // Not a weapon command — forward to the shared interpreter (kill/say/team/…). Unknown commands there are
        // just counted (DP Cmd_ForwardToServer is not wired for the net path), so this is a no-op for them.
        RunCommand?.Invoke(command);
    }

    // =============================================================================================
    //  Map-editor interaction (E3/E5) — the input half; the logic lives in EditorController.
    // =============================================================================================

    /// <summary>
    /// Route input to the editor while free-flying in an editor session. Returns true when the event was
    /// consumed.
    ///
    /// The 3D view is crosshair-driven and keeps the mouse captured (aim, click, grab — the same motion as
    /// aiming a weapon). The orthographic view is the exception: pan/zoom need a pointer, so opening it takes
    /// cursor ownership through the same <c>UiOwnsCursor</c> path the maximized radar uses, which also
    /// suspends movement input so flying doesn't fight panning.
    /// </summary>
    private bool HandleEditorInput(InputEvent @event)
    {
        if (_editor is null || !_editor.Active)
            return false;

        bool orthoOpen = _editorOrtho is { IsOpen: true };

        if (@event is InputEventMouseButton mb)
        {
            if (orthoOpen)
                return HandleOrthoMouse(mb);

            switch (mb.ButtonIndex)
            {
                case MouseButton.Left when mb.Pressed:
                    _editor.BeginDrag(addToSelection: Input.IsKeyPressed(Key.Shift));
                    return true;
                case MouseButton.Left:
                    _editor.EndDrag();
                    return true;
                case MouseButton.Right when mb.Pressed:
                    // Right-click still cancels an in-flight grab: that is the universal editor escape, and it
                    // has to keep working, so the press is CONSUMED and the menu never opens mid-drag.
                    // Otherwise the press only ARMS the menu — it opens on release, so that right-drag stays
                    // available for anything that wants it later without fighting the menu.
                    if (_editor.IsDragging)
                    {
                        _editor.CancelDrag();
                        _rightPressArmedMenu = false;
                    }
                    else
                    {
                        _rightPressArmedMenu = true;
                    }
                    return true;

                case MouseButton.Right:
                    if (_rightPressArmedMenu)
                    {
                        _rightPressArmedMenu = false;
                        OpenEditorMenuAtCrosshair();
                    }
                    return true;
            }
            return false;
        }

        if (@event is InputEventMouseMotion motion)
        {
            if (orthoOpen && _orthoPanning)
            {
                _editorOrtho!.PanByPixels(motion.Relative, GetViewport().GetVisibleRect().Size.Y);
                return true;
            }

            // While a grab is live the pointer moves the GEOMETRY, not the view: consuming the motion here is
            // what freezes the camera, so the mapper judges the placement against a fixed frame.
            if (_editor.IsDragging)
            {
                _editor.ApplyDragMouse(motion.Relative);
                return true;
            }
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.Keycode)
            {
                case Key.Z when key.CtrlPressed && key.ShiftPressed:
                    _editor.Redo();
                    return true;
                case Key.Z when key.CtrlPressed:
                    _editor.Undo();
                    return true;
                case Key.Y when key.CtrlPressed:
                    _editor.Redo();
                    return true;
                case Key.Delete:
                    _editor.DeleteSelection();
                    return true;
                // (E8) The clip tool commits on Enter rather than on the last click: the number of points a
                // cut needs varies by mode, and a gesture that fires the moment you happen to place the last
                // one gives no chance to look at the preview before committing.
                case Key.Enter or Key.KpEnter when _editor.Tool == EditorTool.Clip:
                    _editor.ApplyClip();
                    RefreshEditorWorld();
                    return true;
                case Key.Escape when _editor.Tool == EditorTool.Clip && _editor.ClipPoints.Count > 0:
                    _editor.ClearClipPoints();
                    return true;

                case Key.Escape when _editor.IsDragging:
                    _editor.CancelDrag();
                    return true;
                case Key.Escape when orthoOpen:
                    _editorOrtho!.Close();
                    return true;
            }

            // (E7) The editor owns 0-9 outright while free-flying (§11.9). Consuming them HERE — this method
            // runs ahead of the bind dispatch — is what makes ownership total rather than best-effort: whatever
            // the mapper has bound to a digit stays silent for the duration of the session. The action itself
            // is a console command, so the HUD can still resolve and display its key through the bind table.
            int digit = EditorDigitOf(key.Keycode);
            if (digit >= 0)
            {
                string cmd = Vmap.EditorBinds.CommandForDigit(digit);
                if (cmd.Length > 0)
                    Menu.MenuState.Interp?.ExecuteLine(cmd);
                return true;
            }
        }

        return false;
    }

    /// <summary>Digit 0-9 from a keycode (top row or numpad), or -1 for anything else.</summary>
    private static int EditorDigitOf(Key key) => key switch
    {
        >= Key.Key0 and <= Key.Key9 => (int)(key - Key.Key0),
        >= Key.Kp0 and <= Key.Kp9 => (int)(key - Key.Kp0),
        _ => -1,
    };

    /// <summary>
    /// True between a right-press and its release, when that press did not cancel a drag. The menu opens on
    /// RELEASE (§11.9's "right click and immediate release"), so the flag is what carries the intent across
    /// the two events.
    /// </summary>
    private bool _rightPressArmedMenu;

    /// <summary>
    /// Open the context menu beside the crosshair, or at the pointer when the ortho view owns a real cursor.
    ///
    /// The crosshair is the viewport centre in the 3D view, which is exactly where the mapper is already
    /// looking; anchoring to the mouse there would put the menu wherever the OS cursor happened to be parked
    /// while it was captured, which is nowhere in particular.
    /// </summary>
    private void OpenEditorMenuAtCrosshair()
    {
        if (_fullHud?.GetPanel<XonoticGodot.Game.Hud.EditorMenuPanel>() is not { } menu)
            return;

        Vector2 anchor = _editorOrtho is { IsOpen: true }
            ? GetViewport().GetMousePosition()
            : GetViewport().GetVisibleRect().Size * 0.5f;
        menu.Toggle(anchor);
    }

    /// <summary>Mouse handling specific to the orthographic view: middle-drag pans, the wheel zooms.</summary>
    private bool HandleOrthoMouse(InputEventMouseButton mb)
    {
        switch (mb.ButtonIndex)
        {
            case MouseButton.Middle:
                _orthoPanning = mb.Pressed;
                return true;

            // The menu works in ortho too, anchored at the pointer rather than the screen centre because this
            // view already owns a real cursor.
            case MouseButton.Right when mb.Pressed:
                _rightPressArmedMenu = true;
                return true;
            case MouseButton.Right:
                if (_rightPressArmedMenu)
                {
                    _rightPressArmedMenu = false;
                    OpenEditorMenuAtCrosshair();
                }
                return true;
            case MouseButton.WheelUp when mb.Pressed:
                // ALT moves the floor-filter slab through the map instead of zooming, so stacked floors can be
                // stepped through without leaving the view. This was Ctrl until E7 gave Ctrl a global meaning
                // (invert grid snap, §11.9) — one modifier cannot both suspend snapping and page the slab.
                if (mb.AltPressed)
                    _editorOrtho!.MoveSlab(_editor!.GridSize * 4f);
                else
                    _editorOrtho!.ZoomBy(1f / 1.2f);
                return true;
            case MouseButton.WheelDown when mb.Pressed:
                if (mb.AltPressed)
                    _editorOrtho!.MoveSlab(-_editor!.GridSize * 4f);
                else
                    _editorOrtho!.ZoomBy(1.2f);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Open (or re-open) an editing session for the current map: an already-imported <c>.vmap</c> if one
    /// exists, otherwise imported live from the loaded map so a mapper can start editing any level without a
    /// separate conversion step.
    /// </summary>
    private void EnsureEditorSession()
    {
        if (_editor is null || _editor.Session is not null || _assets is null)
            return;

        try
        {
            string? existing = Vmap.VmapService.FindPackage(_map);
            XonoticGodot.Formats.Vmap.VmapDocument doc;
            if (_preloadedEditorDoc is not null)
            {
                // The listen host already imported the document at load (it is what collision was built
                // from). The session MUST edit that same instance — a copy would let the render and the
                // playtest collision drift apart.
                doc = _preloadedEditorDoc;
            }
            else if (existing is not null)
            {
                doc = XonoticGodot.Formats.Vmap.VmapPackage.Read(existing);
                XonoticGodot.Common.Diagnostics.Log.Info($"editor: opened {existing}");
            }
            else if (_bsp is not null)
            {
                doc = XonoticGodot.Formats.Vmap.BspToVmap.Import(
                    _bsp, _map, $"maps/{_map}.bsp", sourceHash: "", droppedSubmodels: _droppedSubmodels);
                XonoticGodot.Common.Diagnostics.Log.Info(
                    $"editor: imported {_map} from BSP ({doc.Brushes.Count} brushes) — vmap_import to keep it");
                LogEditorFaceHistogram(doc);

            }
            else
            {
                return;
            }
            _editor.OpenSession(doc);
            LoadEntityDefs();
            // The load-time collision was built from this same document, so the live collision is already
            // current — without this seed, the first not-free-fly frame pays a pointless full rebuild.
            _editorCollisionVersion = _editor.GeometryVersion;
            // Default to the mode actually running, so the editor opens showing the map the player sees.
            _editor.SetGametypeFilter(_gametype, _droppedSubmodels);
            Menu.MenuState.Interp?.RegisterCommand("editor_gametype", CmdEditorGametype);
            Menu.MenuState.Interp?.RegisterCommand("editor_rebake", CmdEditorRebake);
            RegisterEditorCommands();
        }
        catch (Exception ex)
        {
            XonoticGodot.Common.Diagnostics.Log.Warn($"editor: could not open a session for '{_map}': {ex.Message}");
        }
    }

    /// <summary>
    /// True when this match is running the map-editor gametype. Several host behaviours key off it because
    /// the editor inverts the usual assumption that observing is a transient state on the way to spawning:
    /// here it is where the mapper LIVES, so auto-join, loading-screen completion and the weapon binds all
    /// have to treat "observer" as a finished, playable state rather than a pending one.
    /// </summary>
    private bool IsEditorGametype => string.Equals(_gametype, "editor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Diagnostic for the "floating brushes" hunt: report how many brush FACES survive the visible-surface
    /// filter, and the material histogram of the survivors. Kept deliberately — it is the measurement that
    /// showed the per-brush tool filter was at the wrong granularity, and it is how we tell whether a shader
    /// family is still slipping through.
    /// </summary>
    /// <summary>
    /// Rebuild the visible world from the EDITED document whenever its geometry version moves.
    ///
    /// The map you fly through is normally built from the compiled BSP, which the editor never touches — so an
    /// edit moved the truth planes and the overlay, while the textured wall stayed exactly where q3map2 left
    /// it. Once a session is open the editor swaps in geometry generated from the document, so what you see is
    /// what you are editing.
    ///
    /// The whole world is rebuilt per edit rather than patched incrementally. That is honest but blunt: it
    /// costs a visible pause on a large map. Rebuilding only the chunks a change touches is the obvious
    /// follow-up, and the cell split in VmapMapBuilder is already the right unit for it.
    /// </summary>
    private void RefreshEditorWorld()
    {
        if (_editor is null || _assets is null)
            return;

        // The regenerated world is shown for the whole EDITOR SESSION, not just while free-flying: PLAYTEST
        // must render the same world it collides with (the document), or the mapper runs through geometry
        // that is not there and bounces off geometry that is.
        bool wantEditorWorld = IsEditorGametype && _editor.Document is not null;

        if (!wantEditorWorld)
        {
            // Left the editor: restore the compiled world.
            if (_editorMapRoot is not null)
            {
                if (GodotObject.IsInstanceValid(_editorMapRoot))
                    _editorMapRoot.QueueFree();
                _editorMapRoot = null;
                _editorMapVersion = -1;
                if (_editorLights is not null && GodotObject.IsInstanceValid(_editorLights))
                    _editorLights.QueueFree();
                _editorLights = null;
                Vmap.EditorLighting.RestoreEnvironment();
                Vmap.EditorLighting.SuppressSceneSun(this, false);
                if (_mapRoot is not null && GodotObject.IsInstanceValid(_mapRoot))
                {
                    _mapRoot.Visible = true;
                    // Hand the warpzone windows back to the compiled world's own portal nodes.
                    if (_portalRenderer is not null && _camera is not null)
                        _portalRenderer.Rebind(_mapRoot, _camera);
                }
            }
            return;
        }

        // The gametype filter and the tool-brush toggle change what is drawn without touching the geometry
        // version, so both have to be part of the key or flipping them would leave the old world on screen.
        bool lit = Vmap.EditorLighting.Enabled(Menu.MenuState.Cvars);
        int viewKey = HashCode.Combine(
            _editor.GeometryVersion, _editor.GametypeFilter, _editor.IncludeToolBrushes,
            _editor.CullOccludedFaces, lit);
        if (_editorMapVersion == viewKey)
            return;

        using var _scope = XonoticGodot.Game.Client.FrameProfiler.Scope("editor.world");

        if (_editorMapRoot is not null && GodotObject.IsInstanceValid(_editorMapRoot))
            _editorMapRoot.QueueFree();

        // Lights FIRST: when they are baked, the mesh builder needs them to compute vertex colours, so the
        // rig has to exist before the world it lights.
        if (_editorLights is not null && GodotObject.IsInstanceValid(_editorLights))
            _editorLights.QueueFree();
        _editorLights = null;
        if (lit)
        {
            _editorLights = Vmap.EditorLighting.Build(_editor.Document!, _assets.Assets, Menu.MenuState.Cvars);
            AddChild(_editorLights);
            if (_editorLights.Baking)
            {
                bool shadowsWanted = Menu.MenuState.Cvars is not { } sc
                    || string.IsNullOrEmpty(sc.GetString(Vmap.EditorLighting.CvarBakeShadows))
                    || sc.GetFloat(Vmap.EditorLighting.CvarBakeShadows) != 0f;
                // A BAKE happens only when asked for — the first build of a session, or editor_rebake. Every
                // other rebuild (every brush you drag) RESAMPLES the retained bake, which costs milliseconds
                // and, more importantly, does not visibly downgrade the lighting mid-edit.
                bool doBake = (_bakeShadowsNextBuild || !Vmap.EditorLightBake.CacheReady)
                    && !Vmap.EditorLightBake.BakeRunning;
                bool tracedShadows = shadowsWanted && doBake;
                _bakeShadowsNextBuild = false;
                _bakedShadowsStale = !doBake;
                if (!doBake)
                {
                    Vmap.EditorLightBake.BeginCached();
                    _editorLights.SuppressBakeLights();
                    goto builtLights;
                }
                var shadowTrace = tracedShadows
                    ? new Vmap.EditorShadowTrace(_editor.Document!,
                        b2 => b2.SubmodelIndex == 0 || !_editor.PickIndex.HiddenSubmodels.Contains(b2.SubmodelIndex))
                    : null;
                Vmap.EditorLightBake.RaysTraced = 0;
                _bakeClock = Time.GetTicksMsec();
                bool bounce = Menu.MenuState.Cvars is not { } bc2
                    || string.IsNullOrEmpty(bc2.GetString(Vmap.EditorLighting.CvarBakeBounce))
                    || bc2.GetFloat(Vmap.EditorLighting.CvarBakeBounce) != 0f;
                Vmap.EditorLightBake.CpuBudget =
                    CvarOr(Menu.MenuState.Cvars!, Vmap.EditorLighting.CvarBakeCpu, 0.75f);
                Vmap.EditorLightBake.SampleSpacing = Math.Clamp(
                    CvarOr(Menu.MenuState.Cvars!, Vmap.EditorLighting.CvarLuxel, 24f), 16f, 256f);
                Vmap.EditorLightBake.DirtStrength =
                    CvarOr(Menu.MenuState.Cvars!, Vmap.EditorLighting.CvarDirt, 0.8f);

                int bounces = 8;
                if (Menu.MenuState.Cvars is { } bn && !string.IsNullOrEmpty(bn.GetString(Vmap.EditorLighting.CvarBakeBounces)))
                    bounces = (int)bn.GetFloat(Vmap.EditorLighting.CvarBakeBounces);
                // Armed WITHOUT the occluder index for the preview pass the build itself does: the preview
                // must cost no rays. RunBackground re-arms with the real trace below.
                Vmap.EditorLightBake.Begin(_editorLights.BakeLights, null, false, bounces);
                _pendingTrace = shadowTrace;
                _pendingBounce = bounce;
                _pendingBounces = bounces;

                // The build only CAPTURES its vertices; the lighting itself runs on a worker afterwards.
                // The world appears immediately with the previous bake resampled onto it, and the editor
                // stays interactive for the minutes a faithful bake can take.
                Vmap.EditorLightBake.Deferred = true;
                Vmap.EditorLightBake.BeginCapture(1 << 20);
                _bakePending = true;
            }
        }

        builtLights:
        _editorMapRoot = Vmap.VmapMapBuilder.BuildMap(_editor.Document!, _assets.Assets, new VmapSurfaceOptions
        {
            // Same filter the picker uses, so what you can click is what you can see.
            HiddenSubmodels = _editor.PickIndex.HiddenSubmodels,
            IncludeToolBrushes = _editor.IncludeToolBrushes,
            // Without this the editor draws the mapper's overlapping solids instead of the level's visible
            // skin, and you end up looking at the inside of the masonry rather than at the room.
            CullOccludedFaces = _editor.CullOccludedFaces,
            PatchSubdivisions = (int)Math.Clamp(
                CvarOr(Menu.MenuState.Cvars!, Vmap.EditorLighting.CvarPatchSubdiv, 8f), 2f, 24f),
        }, lit);
        if (_editorLights is { Baking: true })
            GD.Print($"[EditorLighting] bake: {Vmap.EditorLightBake.RaysTraced:N0} shadow rays in "
                + $"{Time.GetTicksMsec() - _bakeClock} ms");
        if (_bakePending)
        {
            _bakePending = false;
            Vmap.EditorLightBake.Deferred = false;
            _bakeClock = Time.GetTicksMsec();
            // Now arm the REAL bake — traced shadows, dirt and bounce — for the worker.
            _pendingTraceCount = _pendingTrace?.OccluderCount ?? 0;
            Vmap.EditorLightBake.Begin(_editorLights!.BakeLights, _pendingTrace, _pendingBounce, _pendingBounces);
            _pendingTrace = null;
            _ = Vmap.EditorLightBake.RunBackground();   // fire and forget; polled in the editor tick
        }
        else
        {
            Vmap.EditorLightBake.End();
        }
        AddChild(_editorMapRoot);
        _editorMapVersion = viewKey;

        // The regenerated world carries its own "Portals" node, so the portal renderer has to be re-pointed at
        // it — including after every edit, since the rebuild frees the nodes it was holding.
        if (_portalRenderer is not null && _camera is not null)
            _portalRenderer.Rebind(_editorMapRoot, _camera);

        // Hide the compiled world rather than freeing it, so leaving the editor restores it instantly —
        // unless the mapper is holding the BSP-comparison view, which a rebuild must not silently exit.
        ApplyEditorWorldVisibility();
    }

    /// <summary>
    /// <c>editor_rebake</c> (bound to key 9): recompute the lighting — traced shadows, dirt, bounce, the lot.
    ///
    /// A full bake costs seconds (44M rays, ~9 s on stormkeep across every core), which is fine once and
    /// intolerable on every drag of a brush. So NOTHING recomputes lighting automatically: an edit rebuilds
    /// the geometry and RESAMPLES the retained bake onto it, the HUD says the lighting is stale, and the
    /// mapper asks for the real thing when the geometry has settled. Recomputing a cheaper bake per edit
    /// would be both slow and a visible downgrade — the world would flash flatter on every nudge.
    /// </summary>
    private void CmdEditorRebake(IReadOnlyList<string> args)
    {
        _ = args;
        if (_editor is not { Active: true })
        {
            XonoticGodot.Common.Diagnostics.Log.Warn("editor_rebake: no editing session");
            return;
        }
        _bakeShadowsNextBuild = true;
        _editorMapVersion = -1;      // force the next RefreshEditorWorld to rebuild
        XonoticGodot.Common.Diagnostics.Log.Info("editor_rebake: recomputing lighting (traced shadows + dirt + bounce)...");
    }

    /// <summary>True when the next world build should trace shadows (first build, or after editor_rebake).</summary>
    private bool _bakeShadowsNextBuild = true;

    /// <summary>
    /// Load the entity class descriptors from the game's own <c>scripts/entities.ent</c> (design doc §11.9):
    /// 186 classes with typed keys, help text, editor colours and bounding boxes, which is what NetRadiant
    /// drives its entity inspector from.
    ///
    /// A missing or broken file is NOT fatal — the parser returns an empty registry and every entity falls back
    /// to a plain box that still draws, still picks and still has editable keys. An editor that refused to open
    /// a map because a metadata file moved would be a far worse trade.
    /// </summary>
    private void LoadEntityDefs()
    {
        if (_editor is null || _editor.Defs is not null || _assets is null)
            return;

        try
        {
            if (!_assets.Vfs.Exists(XonoticGodot.Formats.Vmap.EntityDefs.VirtualPath))
            {
                XonoticGodot.Common.Diagnostics.Log.Warn(
                    $"editor: {XonoticGodot.Formats.Vmap.EntityDefs.VirtualPath} not found — "
                    + "entities will draw as plain boxes");
                return;
            }

            var defs = XonoticGodot.Formats.Vmap.EntityDefs.Parse(
                _assets.Vfs.ReadText(XonoticGodot.Formats.Vmap.EntityDefs.VirtualPath));
            _editor.Defs = defs;
            XonoticGodot.Common.Diagnostics.Log.Info($"editor: {defs.Count} entity classes loaded");
        }
        catch (Exception ex)
        {
            XonoticGodot.Common.Diagnostics.Log.Warn($"editor: could not read entity definitions: {ex.Message}");
        }
    }

    // =============================================================================================
    //  (E7) The editor command vocabulary — §11.9
    // =============================================================================================

    /// <summary>
    /// Register the <c>editor_*</c> commands. Everything the editor can do goes through this vocabulary: the
    /// context menu's rows, the number keys, and anything typed at the console all take the same path.
    ///
    /// Routing the keys through real COMMANDS rather than a hardcoded keycode switch is what keeps §11.6's
    /// rule alive — the HUD resolves every key it displays through the bind table, so rebinding updates the
    /// readout and an unbound action renders as <c>--</c> instead of the panel confidently lying about which
    /// key does what. It also makes every one of them scriptable, which a switch statement never is.
    ///
    /// Idempotent: <c>EnsureEditorSession</c> can run more than once per session.
    /// </summary>
    private void RegisterEditorCommands()
    {
        if (_editorCommandsRegistered || Menu.MenuState.Interp is not { } interp)
            return;
        _editorCommandsRegistered = true;

        interp.RegisterCommand("editor_tool", CmdEditorTool);
        interp.RegisterCommand("editor_mode", CmdEditorMode);
        interp.RegisterCommand("editor_select", CmdEditorSelect);
        interp.RegisterCommand("editor_menu", CmdEditorMenu);
        interp.RegisterCommand("editor_undo", _ => { if (_editor?.Undo() == true) RefreshEditorWorld(); });
        interp.RegisterCommand("editor_redo", _ => { if (_editor?.Redo() == true) RefreshEditorWorld(); });
        interp.RegisterCommand("editor_ortho", _ => ToggleEditorOrtho());
        interp.RegisterCommand("editor_ortho_axis", _ => _editorOrtho?.CycleAxis());
        interp.RegisterCommand("editor_wire", _ => Vmap.EditorOrthoView.CycleWireAlpha());
        interp.RegisterCommand("editor_show_bsp", _ => ToggleEditorBspCompare());
        interp.RegisterCommand("editor_flyspeed", CmdEditorFlySpeed);
        interp.RegisterCommand("editor_clip", CmdEditorClip);
        interp.RegisterCommand("editor_entity", CmdEditorEntity);
        Vmap.EditorBinds.RegisterCommands(interp);

        // Stubs with an honest message rather than silence. These rows are visible-but-disabled in the menu,
        // and a mapper who reaches one from the console deserves to be told it is E8 rather than to watch
        // nothing happen and assume the editor is broken.
        interp.RegisterCommand("editor_history", _ =>
            XonoticGodot.Common.Diagnostics.Log.Info("editor_history: not built yet (roadmap E8)"));
        interp.RegisterCommand("editor_mapinfo", _ =>
            XonoticGodot.Common.Diagnostics.Log.Info("editor_mapinfo: not built yet (roadmap E8)"));
    }

    private bool _editorCommandsRegistered;

    /// <summary><c>editor_tool &lt;name&gt;</c>, or bare to cycle.</summary>
    private void CmdEditorTool(IReadOnlyList<string> args)
    {
        if (_editor is null)
            return;

        if (args.Count < 2)
        {
            _editor.CycleTool();
            return;
        }

        if (!Enum.TryParse(args[1], ignoreCase: true, out EditorTool tool))
        {
            XonoticGodot.Common.Diagnostics.Log.Help(
                $"editor_tool: unknown tool '{args[1]}' (try: {string.Join(", ", EditorTools.All)})");
            return;
        }

        if (!EditorTools.IsImplemented(tool))
        {
            XonoticGodot.Common.Diagnostics.Log.Info(
                $"editor_tool: {EditorTools.Label(tool)} is not built yet (roadmap E8)");
            return;
        }
        _editor.SetTool(tool);
    }

    /// <summary><c>editor_mode &lt;name&gt;</c>, or bare to cycle within the current tool.</summary>
    private void CmdEditorMode(IReadOnlyList<string> args)
    {
        if (_editor is null)
            return;

        if (args.Count < 2)
        {
            _editor.CycleMode();
            return;
        }

        if (!Enum.TryParse(args[1], ignoreCase: true, out ToolMode mode))
        {
            XonoticGodot.Common.Diagnostics.Log.Help(
                $"editor_mode: unknown mode '{args[1]}' "
                + $"(this tool offers: {string.Join(", ", EditorTools.ModesFor(_editor.Tool))})");
            return;
        }
        _editor.SetMode(mode);
    }

    /// <summary><c>editor_select deselect|copy|paste|delete|invert|all_shader</c> — the selection ACTIONS.</summary>
    private void CmdEditorSelect(IReadOnlyList<string> args)
    {
        if (_editor is not { Session: { } session })
            return;

        string verb = args.Count > 1 ? args[1].ToLowerInvariant() : "";
        switch (verb)
        {
            case "deselect":
                session.Selection.Clear();
                return;

            case "copy":
            {
                int n = _editor.Clipboard.CopyFrom(session.Document, session.Selection);
                XonoticGodot.Common.Diagnostics.Log.Info(n > 0
                    ? $"editor: copied {_editor.Clipboard.Describe()}"
                    : "editor: nothing to copy");
                return;
            }

            case "paste":
                if (_editor.Clipboard.IsEmpty)
                {
                    XonoticGodot.Common.Diagnostics.Log.Info("editor: clipboard is empty");
                    return;
                }
                // Paste is a MODE, not an action: the ghost follows the crosshair and a left-click places it
                // (§11.9). Entering the mode is all this command does.
                _editor.SetMode(ToolMode.Paste);
                XonoticGodot.Common.Diagnostics.Log.Info(
                    $"editor: placing {_editor.Clipboard.Describe()} — click to place, Esc to stop");
                return;

            case "delete":
                if (_editor.DeleteSelection())
                    RefreshEditorWorld();
                return;

            case "invert":
            case "all_shader":
                XonoticGodot.Common.Diagnostics.Log.Info($"editor_select {verb}: not built yet (roadmap E8)");
                return;

            default:
                XonoticGodot.Common.Diagnostics.Log.Help(
                    "usage: editor_select deselect|copy|paste|delete|invert|all_shader");
                return;
        }
    }

    /// <summary>
    /// <c>editor_clip keep|apply|cancel</c>. Bare applies the pending cut, which is what the Enter key does —
    /// registered as a command too so it can be bound and scripted like everything else.
    /// </summary>
    private void CmdEditorClip(IReadOnlyList<string> args)
    {
        if (_editor is null)
            return;

        switch (args.Count > 1 ? args[1].ToLowerInvariant() : "apply")
        {
            case "keep":
                _editor.CycleClipKeep();
                return;
            case "cancel":
                _editor.ClearClipPoints();
                return;
            case "apply":
                if (_editor.ApplyClip())
                    RefreshEditorWorld();
                return;
            default:
                XonoticGodot.Common.Diagnostics.Log.Help("usage: editor_clip keep|apply|cancel");
                return;
        }
    }

    /// <summary>
    /// <c>editor_entity</c> — the entity tool's console surface.
    /// <code>
    ///   editor_entity create &lt;classname&gt;   place one at the crosshair
    ///   editor_entity set &lt;key&gt; &lt;value&gt;    set a key on the selection (empty value clears)
    ///   editor_entity keys                  list the selected entity's keys and what its class allows
    ///   editor_entity list [category]       list placeable classes
    /// </code>
    /// </summary>
    private void CmdEditorEntity(IReadOnlyList<string> args)
    {
        if (_editor is not { Session: { } session })
            return;

        switch (args.Count > 1 ? args[1].ToLowerInvariant() : "")
        {
            case "create":
            {
                if (args.Count < 3)
                {
                    XonoticGodot.Common.Diagnostics.Log.Help("usage: editor_entity create <classname>");
                    return;
                }
                if (!_editor.TryGetPastePoint(out System.Numerics.Vector3 at))
                    return;

                var op = new XonoticGodot.Formats.Vmap.CreateEntityOp(args[2], at);
                if (!session.Apply(op))
                {
                    XonoticGodot.Common.Diagnostics.Log.Warn($"editor: could not create '{args[2]}'");
                    return;
                }

                // Select what was just made, so it can be nudged or edited without hunting for it.
                session.Selection.Clear();
                session.Selection.Add(XonoticGodot.Formats.Vmap.VmapSelection.OfEntity(op.CreatedEntityId));
                _editor.BumpGeometryVersion();
                XonoticGodot.Common.Diagnostics.Log.Info($"editor: placed {args[2]} at {at.X:0} {at.Y:0} {at.Z:0}");
                return;
            }

            case "set":
            {
                if (args.Count < 3)
                {
                    XonoticGodot.Common.Diagnostics.Log.Help("usage: editor_entity set <key> [value]");
                    return;
                }
                List<int> ids = _editor.SelectedEntityIds();
                if (ids.Count == 0)
                {
                    XonoticGodot.Common.Diagnostics.Log.Info("editor: no entity selected");
                    return;
                }

                // Join the tail so values with spaces ("16 32 64", a message string) work without quoting.
                string value = args.Count > 3 ? string.Join(' ', args, 3, args.Count - 3) : "";
                int changed = 0;
                foreach (int id in ids)
                    if (session.Apply(new XonoticGodot.Formats.Vmap.SetEntityKeyOp(id, args[2], value)))
                        changed++;

                XonoticGodot.Common.Diagnostics.Log.Info(changed > 0
                    ? $"editor: set {args[2]} on {changed} entit{(changed == 1 ? "y" : "ies")}"
                    : $"editor: {args[2]} unchanged");
                return;
            }

            case "keys":
                LogEntityKeys();
                return;

            case "list":
                LogEntityClasses(args.Count > 2 ? args[2] : "");
                return;

            default:
                XonoticGodot.Common.Diagnostics.Log.Help(
                    "usage: editor_entity create <classname> | set <key> [value] | keys | list [category]");
                return;
        }
    }

    /// <summary>Print the selected entity's live keys alongside what its class documents.</summary>
    private void LogEntityKeys()
    {
        if (_editor?.Document is not { } doc)
            return;

        List<int> ids = _editor.SelectedEntityIds();
        if (ids.Count == 0)
        {
            XonoticGodot.Common.Diagnostics.Log.Info("editor: no entity selected");
            return;
        }

        foreach (int id in ids)
        {
            if (doc.FindEntity(id) is not { } e)
                continue;

            XonoticGodot.Common.Diagnostics.Log.Info($"--- {e.ClassName} (#{e.Id}) ---");
            foreach (KeyValuePair<string, string> kv in e.Fields)
                XonoticGodot.Common.Diagnostics.Log.Info($"  {kv.Key} = {kv.Value}");

            if (_editor.Defs?.Get(e.ClassName) is not { } def)
                continue;
            if (def.Description.Length > 0)
                XonoticGodot.Common.Diagnostics.Log.Info($"  ({def.Description})");
            foreach (XonoticGodot.Formats.Vmap.EntityKeyDef k in def.Keys)
                if (!e.Fields.ContainsKey(k.Key))
                    XonoticGodot.Common.Diagnostics.Log.Info($"  [unset] {k.Key} ({k.Kind}) — {k.Help}");
        }
    }

    /// <summary>List placeable classes, optionally filtered to one category.</summary>
    private void LogEntityClasses(string category)
    {
        if (_editor?.Defs is not { } defs)
        {
            XonoticGodot.Common.Diagnostics.Log.Info("editor: no entity definitions loaded");
            return;
        }

        if (category.Length == 0)
        {
            XonoticGodot.Common.Diagnostics.Log.Info(
                $"editor: {defs.Count} classes in categories: {string.Join(", ", defs.Categories())}");
            XonoticGodot.Common.Diagnostics.Log.Help("editor_entity list <category>");
            return;
        }

        foreach (XonoticGodot.Formats.Vmap.EntityClassDef d in defs.InCategory(category))
            XonoticGodot.Common.Diagnostics.Log.Info(
                $"  {d.Name}{(d.IsBrushEntity ? "  (brush entity)" : "")}");
    }

    /// <summary><c>editor_menu</c> — open or close the context menu at the crosshair.</summary>
    private void CmdEditorMenu(IReadOnlyList<string> args)
    {
        _ = args;
        OpenEditorMenuAtCrosshair();
    }

    /// <summary>
    /// <c>editor_flyspeed + | -</c>. The speed lives on the SERVER's player entity (QC
    /// <c>sys_phys_spectator_control</c> steps it off impulses 10/12), so this sends the impulse rather than
    /// writing a client value that the next snapshot would overwrite.
    /// </summary>
    private void CmdEditorFlySpeed(IReadOnlyList<string> args)
    {
        string dir = args.Count > 1 ? args[1] : "+";
        _pendingImpulse = dir == "-" ? 12 : 10;
    }

    /// <summary>Open or close the orthographic view, seeded at the current camera position.</summary>
    private void ToggleEditorOrtho()
    {
        if (_editorOrtho is null || _camera is null)
            return;
        _editorOrtho.Toggle(Coords.ToQuake(_camera.GlobalTransform.Origin));
    }

    /// <summary>
    /// Flip to the ORIGINAL compiled BSP and back. The ground truth is one command away, so "is this artifact
    /// ours or the map's?" is answered by looking from the same camera rather than by relaunching into a plain
    /// match and flying back to the spot.
    /// </summary>
    private void ToggleEditorBspCompare()
    {
        _editorShowBsp = !_editorShowBsp;
        ApplyEditorWorldVisibility();
        Menu.MenuState.Cvars?.Set(Vmap.EditorLighting.CvarShowBsp, _editorShowBsp ? "1" : "0");
    }

    /// <summary>
    /// The camera the frame is actually being drawn with. Godot marks exactly one Camera3D current per
    /// viewport, so asking the viewport is the only answer that stays right when something else takes the
    /// view (the observe pin, an ortho pane, a demo).
    /// </summary>
    private Camera3D? ActiveViewCamera()
    {
        Camera3D? cam = GetViewport()?.GetCamera3D();
        return cam is not null && GodotObject.IsInstanceValid(cam) ? cam : null;
    }

    /// <summary>
    /// A camera's Quake view angles (pitch, yaw, roll), derived from its BASIS rather than from any stored
    /// angle state — see the note in the readout wiring for why the stored state is not trustworthy here.
    /// Full-basis conversion, never a yaw-only Euler read: those disagree in handedness.
    /// </summary>
    private static NVec3 CameraQuakeAngles(Camera3D cam)
    {
        Basis b = cam.GlobalTransform.Basis;
        NVec3 forward = Coords.ToQuake(-b.Z);
        NVec3 up = Coords.ToQuake(b.Y);
        NVec3 a = XonoticGodot.Common.Math.QMath.VecToAngles2(forward, up);

        // VecToAngles2 is vectoangles: +pitch means UP. View angles — what --observe takes and what the
        // player's own angles hold — use +pitch means DOWN. Printing one while the flag consumes the other
        // is a readout that reproduces the mirror image of the view it was copied from.
        return new NVec3(-a.X, a.Y, a.Z);
    }

    /// <summary>
    /// Eye offset used by the --observe pin. Any positive value works: it is subtracted from the requested
    /// origin and handed straight back as EyeHeightZ, so it cancels. It must be POSITIVE because the view
    /// treats zero as "use the player's eye height instead", which is the trap this replaced.
    /// </summary>
    private const float PinEyeOfs = 1f;

    /// <summary>Set when the current build is capturing vertices for a background bake.</summary>
    private bool _bakePending;

    /// <summary>True while the editor session is showing the ORIGINAL compiled BSP for comparison (key 0).</summary>
    private bool _editorShowBsp;

    /// <summary>
    /// Show either the regenerated document world or the original compiled BSP, never both: they occupy the
    /// same space, and rendering the pair z-fights everywhere. The editor light rig follows the document
    /// world — the BSP carries its own baked lightmaps and must not receive our sun a second time.
    /// </summary>
    private void ApplyEditorWorldVisibility()
    {
        if (_mapRoot is not null && GodotObject.IsInstanceValid(_mapRoot))
            _mapRoot.Visible = _editorShowBsp;
        if (_editorMapRoot is not null && GodotObject.IsInstanceValid(_editorMapRoot))
            _editorMapRoot.Visible = !_editorShowBsp;
        if (_editorLights is not null && GodotObject.IsInstanceValid(_editorLights))
            _editorLights.Visible = !_editorShowBsp;
    }

    private Vmap.EditorShadowTrace? _pendingTrace;
    private int _pendingTraceCount;
    private bool _pendingBounce;
    private int _pendingBounces = 8;

    /// <summary>True when the live lighting was built WITHOUT tracing, so its shadows are out of date.</summary>
    private bool _bakedShadowsStale;

    /// <c>editor_gametype &lt;name|all&gt;</c>: choose which gametype's geometry the editor shows. Resolves the
    /// hidden inline-model set with the SAME rule the renderer and collision builder use, so the editor shows
    /// exactly the map that mode would produce — and nothing is discarded, only hidden.
    /// </summary>
    private void CmdEditorGametype(System.Collections.Generic.IReadOnlyList<string> argv)
    {
        if (_editor is null || _bsp is null)
        {
            XonoticGodot.Common.Diagnostics.Log.Warn("editor_gametype: no editing session");
            return;
        }
        if (argv.Count < 2)
        {
            XonoticGodot.Common.Diagnostics.Log.Help(
                $"usage: editor_gametype <name|all>   (currently showing: {_editor.GametypeFilterLabel})");
            return;
        }

        string want = argv[1];
        bool showAll = string.Equals(want, "all", StringComparison.OrdinalIgnoreCase);
        System.Collections.Generic.IReadOnlySet<int>? hidden =
            showAll ? null : GameMapView.ComputeDroppedSubmodels(_bsp, want);
        _editor.SetGametypeFilter(want, hidden);
    }

    private static void LogEditorFaceHistogram(XonoticGodot.Formats.Vmap.VmapDocument doc)
    {
        var hist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int pass = 0, total = 0, toolBrushes = 0;
        foreach (XonoticGodot.Formats.Vmap.VmapBrush b in doc.Brushes)
        {
            if (b.IsToolBrush) { toolBrushes++; continue; }
            foreach (XonoticGodot.Formats.Vmap.VmapFace f in b.Faces)
            {
                total++;
                if ((f.SurfaceFlags & 0x0080) != 0 || XonoticGodot.Formats.Vmap.VmapBrush.IsToolMaterial(f.Material))
                    continue;
                pass++;
                string key = string.IsNullOrEmpty(f.Material) ? "(empty)" : f.Material;
                hist[key] = hist.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        var ranked = new List<KeyValuePair<string, int>>(hist);
        ranked.Sort((x, y) => y.Value.CompareTo(x.Value));
        var top = new List<string>();
        for (int i = 0; i < ranked.Count && i < 14; i++)
            top.Add($"{ranked[i].Value}x {ranked[i].Key}");

        XonoticGodot.Common.Diagnostics.Log.Info(
            $"editor: {toolBrushes} tool brushes; visible faces {pass}/{total}; {hist.Count} distinct materials");
        XonoticGodot.Common.Diagnostics.Log.Info($"editor: top materials: {string.Join(" | ", top)}");

    }

    /// <summary>
    /// True when the local client is free-flying as an observer. Several VIEW decisions key off this because an
    /// observer is not a player: it has no body, no eye height above its origin, and — critically — zero health,
    /// which every "are you dead?" test in the client would otherwise answer yes to.
    /// </summary>
    private bool IsLocalObserving => _client?.IsObserving ?? false;

    /// <summary>
    /// Eye offset above the entity origin for the local view. An observer's camera sits AT its origin (QC
    /// PutObserverInServer sets <c>view_ofs = '0 0 0'</c>, so a free-flying camera cannot poke through a ceiling
    /// it is otherwise clear of); a live player uses the standing eye height.
    /// </summary>
    private float LocalEyeHeight => IsLocalObserving ? 0f : EyeHeight;

    /// <summary>
    /// True while this client is free-flying (EDIT) inside an editor session — the only state in which the
    /// weapon binds are repurposed. Playtesting is real play, so weapons behave normally there.
    /// </summary>
    private bool IsEditorFreeFly => IsEditorGametype && (_client?.IsObserving ?? false);

    /// <summary>
    /// Map a bound command STRING to its Xonotic impulse number (common/impulses/all.qh — the same numbers
    /// <see cref="WeaponImpulses"/> dispatches). Covers the weapon binds BindInput emits: <c>weapon_group_N</c>
    /// (1..9 → N, 0 → 14), <c>weapnext</c>/<c>weapprev</c> (10/12), <c>weapon_last</c>/<c>weaplast</c> (11),
    /// <c>weapon_best</c>/<c>weapbest</c> (13), <c>weapon_drop</c>/<c>dropweapon</c> (17),
    /// <c>weapon_reload</c>/<c>reload</c> (20), and <c>weapon_byid_N</c> (230+N). Returns 0 for a non-weapon
    /// command. Case-insensitive.
    /// </summary>
    private static int WeaponCommandToImpulse(string command)
    {
        // weapon_group_N (the 1..9 keys via binds-xonotic.cfg) → impulse N (group 0 → 14).
        if (command.StartsWith("weapon_group_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(command.AsSpan("weapon_group_".Length), out int group) && group is >= 0 and <= 9)
            return group == 0 ? 14 : group;

        // weapon_byid_N → 230+N (direct weapon by unique id).
        if (command.StartsWith("weapon_byid_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(command.AsSpan("weapon_byid_".Length), out int byid) && byid is >= 0 and <= 23)
            return 230 + byid;

        return command.ToLowerInvariant() switch
        {
            "weapnext" => 10,
            "weapprev" => 12,
            "weapon_last" or "weaplast" => 11,
            "weapon_best" or "weapbest" => 13,
            "weapon_drop" or "dropweapon" => 17,
            "weapon_reload" or "reload" => 20,
            _ => 0,
        };
    }

    /// <summary>
    /// True when an impulse number is a weapon-SELECT action (so the viewmodel should lower-then-raise): the
    /// group keys + next/prev/last/best (1..14, matching <see cref="WeaponCommandToImpulse"/>: groups 1..9, 10
    /// next, 11 last, 12 prev, 13 best, 14 group-0) and the direct weapon_byid_N (230+). Excludes drop (17) and
    /// reload (20), which keep the current weapon shown.
    /// </summary>
    private static bool IsWeaponSwitchImpulse(int imp)
        => (imp >= 1 && imp <= 14) || (imp >= 230 && imp <= 253);

    /// <summary>
    /// Whether a weapon-select impulse could actually change the weapon — the gate for the keypress-predicted
    /// holster (playtest #31). Base plays NO switch animation on a denied switch: <c>W_SwitchWeapon</c> leaves
    /// <c>m_switchweapon</c> untouched when <c>client_hasweapon(…, andammo, complain)</c> fails (selection.qc:274),
    /// so the weaponsystem state machine never raises/drops — you only hear the "weapons/unavailable" denial
    /// (which the port's server path already plays). The old predict-always holster lowered the gun on EVERY
    /// select press and recovered on the grace timer — a phantom switch animation when nothing could switch.
    /// Mirrors the server check with the same shared logic (<see cref="Inventory.ClientHasWeapon"/>,
    /// complain: false = silent): group keys test their impulse group, by-id tests the exact weapon (plus its
    /// group when <c>cl_weapon_switch_fallback_to_impulse</c> is on, matching <c>ByIdHandle</c>), next/prev/last/
    /// best test for any other usable weapon ("last"/"best" can rarely still deny with this true — e.g. best ==
    /// current — a small over-predict the holster grace recovers). Pure remote client (no
    /// <see cref="LocalServerPlayer"/>): keep the old predict-always behavior — the same graceful-degradation
    /// policy as the fire-prediction ammo gate (<see cref="HasAmmoNow"/>).
    /// </summary>
    private bool SwitchCouldSucceedNow(int imp)
    {
        if (LocalServerPlayer is not { } p || _client is null)
            return true;
        int currentId = _client.ActiveWeaponId;

        if (imp >= 230) // weapon_byid_N — exact target known
        {
            Weapon? w = WeaponOrder.WeaponByIdIndex(imp - 230);
            if (w is null)
                return false; // out of range → QC WEP_Null no-op, nothing to animate
            if (w.RegistryId != currentId && Inventory.ClientHasWeapon(p, w, andAmmo: true, complain: false))
                return true;
            bool fallback = Api.Services is not null
                && Api.Cvars.GetFloat("cl_weapon_switch_fallback_to_impulse") != 0f;
            return fallback && AnyOtherUsableWeapon(p, currentId, w.Impulse);
        }

        if (imp >= 1 && imp <= 9)
            return AnyOtherUsableWeapon(p, currentId, imp);   // weapon_group_1..9
        if (imp == 14)
            return AnyOtherUsableWeapon(p, currentId, 0);     // weapon_group_0
        return AnyOtherUsableWeapon(p, currentId, group: -1); // next/prev/last/best (10..13)
    }

    /// <summary>A usable (owned + ammo, QC <c>client_hasweapon</c>) weapon other than the current one exists —
    /// optionally restricted to one impulse group (<paramref name="group"/> &lt; 0 = any).</summary>
    private static bool AnyOtherUsableWeapon(Player p, int currentId, int group)
    {
        System.Collections.Generic.IReadOnlyList<Weapon> all = Weapons.All;
        for (int i = 0; i < all.Count; i++)
        {
            Weapon w = all[i];
            if (w.RegistryId == currentId)
                continue;
            if (group >= 0 && w.Impulse != group)
                continue;
            if (Inventory.ClientHasWeapon(p, w, andAmmo: true, complain: false))
                return true;
        }
        return false;
    }

    /// <summary>The live look sensitivity (DP `sensitivity` cvar) folded with the base feel — the value the
    /// input-settings dialog writes. Falls back to the previous hardcoded feel when the cvar is unset.</summary>
    private float LookSensitivity()
    {
        float s = Api.Services is not null ? Api.Cvars.GetFloat("sensitivity") : 0f;
        return s > 0f ? s : 3f;
    }

    /// <summary>DP `m_yaw` (default 0.022 deg/count): the yaw half of the mouse scale. Unset/0 → 0.022.</summary>
    private static float MYaw()
    {
        float v = Api.Services is not null ? Api.Cvars.GetFloat("m_yaw") : 0f;
        return v != 0f ? v : 0.022f;
    }

    /// <summary>DP `m_pitch` (default 0.022 deg/count, SIGNED — negative inverts the Y axis; the input
    /// dialog's "Invert aiming" writes ±|value|). Unset/0 → +0.022. Legacy configs hold ±1 from the old
    /// sign-only invert convention (ClientSettings used to register "1") — any DP-implausible magnitude
    /// (≥0.5) is read as sign × 0.022 so an archived ±1 doesn't give 45× pitch.</summary>
    private static float MPitch()
    {
        float v = Api.Services is not null ? Api.Cvars.GetFloat("m_pitch") : 0f;
        if (v == 0f) return 0.022f;
        if (MathF.Abs(v) >= 0.5f) return v < 0f ? -0.022f : 0.022f; // legacy sign-only ±1
        return v;
    }

    /// <summary>
    /// Sample one tick of live input into an <see cref="InputCommand"/> (the unit the client predicts on and
    /// the server applies). WASD → Quake-space forward/side (normalised to ±1; the move code rescales against
    /// sv_maxspeed), Space → jump, Ctrl → crouch, mouse buttons → attack. The accumulated view angles ride
    /// along so the server applies the exact aim the client predicted with. Called by <see cref="ClientNet"/>
    /// each <see cref="ClientNet.SendInput"/>.
    /// </summary>
    /// <summary>True while the in-game minigame UI owns input — the Create/Join menu is open OR a minigame board
    /// is active (so the player can click TTT/C4 tiles / read the board). The cursor is shown and gameplay input
    /// (move/look/binds) is suppressed, exactly like the pause menu/console. QC: the minigame menu/board run with
    /// hud_cursormode. (Pong's paddle arrow keys are still read directly by HudManager.DrivePongKeys, so Pong
    /// plays even while gameplay input is suppressed.)</summary>
    private bool MinigameMenuOpen
        => _fullHud is not null
        && ((_fullHud.MinigameMenu is { IsOpen: true }) || (_minigame is not null && _minigame.Active is not null));

    /// <summary>The quick-chat menu is showing its list (it owns the cursor like the minigame menu — mouse hover +
    /// click select rows, so the cursor must be freed and gameplay look/fire suspended while open).</summary>
    private bool QuickMenuOpen
        => _fullHud?.GetPanel<XonoticGodot.Game.Hud.QuickMenuPanel>() is { IsOpen: true };

    /// <summary>Any in-world HUD UI that should free the mouse cursor + suspend look/fire (minigame board/menu, the
    /// quick-chat menu, or the maximized radar). The bind channel stays live so the toggling bind can still close the
    /// panel — the maximized radar's `m` toggle + Esc/right-click close all route through it.</summary>
    private bool UiOwnsCursor => MinigameMenuOpen || QuickMenuOpen || (_radar?.Maximized ?? false)
                                 || (_editorOrtho?.IsOpen ?? false) || EditorMenuOpen;

    /// <summary>True while the editor's context menu is up, so it takes the cursor like any other modal HUD UI.</summary>
    private bool EditorMenuOpen
        => _fullHud?.GetPanel<XonoticGodot.Game.Hud.EditorMenuPanel>() is { IsOpen: true };

    /// <summary>Whether gameplay owns keyboard/mouse input this frame — false whenever a UI owner has it (the DP
    /// key_dest states): the pause/auto-pause, the console, the messagemode chat prompt (key_dest = message:
    /// movement/fire suspended while typing), or a cursor-owning HUD UI (minigame/quickmenu/maximized radar). The
    /// single source of truth for the input-active gate (SampleInput + the fire-feedback edge).</summary>
    private bool GameplayInputActive =>
        !GetTree().Paused && !ConsoleState.IsOpen && !XonoticGodot.Game.Hud.ChatPrompt.IsOpen && !UiOwnsCursor;

    /// <summary>
    /// Teleporter view-snap (QC player.fixangle): after a prediction tick re-derives the carrier's .fixangle —
    /// true exactly on the tick the local player is predicted through a single-dest teleporter — snap the
    /// accumulated view to the destination facing (CSQC setproperty(VF_CL_VIEWANGLES)) and consume the flag, so
    /// the camera exits looking the way the mapper aimed; later mouse-look accumulates on top. Multi-dest
    /// teleporters aren't predicted (random exit) and stay server-authoritative.
    /// </summary>
    private void ConsumePredictedFixAngle()
    {
        // PREDICTED WARPZONE CROSSING (the seq-keyed one-shot pulse — see TriggerTouch.LastPredictedWarpSeq):
        // apply the view rotation IMMEDIATELY and RELATIVELY — `view = T(view)`, Base's
        // `setproperty(VF_CL_VIEWANGLES, WarpZone_TransformVAngles(this, getpropertyvec(VF_CL_VIEWANGLES)))`
        // (lib/warpzone/client.qc:141) — and rotate the pending input ring (DP CL_RotateMoves, builtin #638)
        // so post-warp reconcile replays use post-warp view angles.
        //
        // DISABLED BY DEFAULT (wz_predict_apply 0): on the LISTEN HOST this proved counterproductive — the
        // server's sim runs a tick behind the predictor, so the crossing tick's input reaches it carrying the
        // ALREADY-ROTATED view (session-11 trace: every server crossing's entry angles == the post-apply view),
        // double-rotating the server state and fighting the reconcile — worse than the 1-2 frame authoritative
        // latency it was meant to hide. The infrastructure stays for the REMOTE-client path (where Base's
        // CL_RotateMoves actually lives) behind the cvar for future work.
        if (_carrier is not null
            && MenuState.Cvars.GetFloat("wz_predict_apply") != 0f
            && XonoticGodot.Engine.Simulation.TriggerTouch.LastPredictedWarpSeq > _consumedWarpSeq)
        {
            _consumedWarpSeq = XonoticGodot.Engine.Simulation.TriggerTouch.LastPredictedWarpSeq;
            Common.Gameplay.WarpzoneTransform wt = XonoticGodot.Engine.Simulation.TriggerTouch.LastPredictedWarpTransform;
            _viewAngles = wt.TransformAngles(_viewAngles);
            _viewAngles.X = Mathf.Clamp(_viewAngles.X, _pitchMinCv, _pitchMaxCv);
            _client.RotatePendingMoves(a => wt.TransformAngles(a));
            float nowW = Time.GetTicksMsec() * 0.001f;
            // Arm the same guards the (now mostly redundant) fixangle paths use: the server's authoritative
            // stamp for this crossing recognises the applied facing and skips; late replay echoes discard.
            _lastPredictedFixAngles = _viewAngles;
            _lastPredictedFixTime = nowW;
            _lastFixApplyTime = nowW;
            if (MenuState.Cvars.GetFloat("sv_warpzone_trace") != 0f)
                GD.Print($"[wzview] predicted warp apply -> {_viewAngles} (ring rotated)");
            if (_viewModel is not null && GodotObject.IsInstanceValid(_viewModel))
                _viewModel.NotifyTeleported();
        }

        if (_carrier is not null && _carrier.FixAngle)
        {
            float now = Time.GetTicksMsec() * 0.001f;
            // REPLAY-ECHO GUARD: the reconcile replay re-simulates the unacked ticks every frame, and near a
            // warpzone seam a replay from a post-warp base can spuriously re-cross a zone the server never did —
            // stamping a BACK-ROTATED facing (observed live: predicted 172 re-stamped right after the correct
            // authoritative 82, leaving the view at the entry yaw — the "angles still wrong" report). Any stamp
            // arriving within the window after a fixangle APPLY (predicted or authoritative) is such an echo of
            // the same crossing — discard it. A genuine rapid re-crossing inside the window still gets its
            // correct snap from the AUTHORITATIVE stamp, which is empirically always the transformed exit facing
            // and always applies (below in _Process) when it disagrees with the last predicted value.
            if (now - _lastFixApplyTime < 0.4f)
            {
                _carrier.FixAngle = false;
                if (MenuState.Cvars.GetFloat("sv_warpzone_trace") != 0f)
                    GD.Print($"[wzview] predicted echo discarded -> {_carrier.FixAngleAngles}");
                return;
            }
            _viewAngles = _carrier.FixAngleAngles;
            _viewAngles.X = Mathf.Clamp(_viewAngles.X, _pitchMinCv, _pitchMaxCv);
            _carrier.FixAngle = false;
            // Remember what the PREDICTED snap applied: the server's AUTHORITATIVE stamp for the SAME crossing
            // arrives 1-3 frames later (its tick runs behind the replay), and re-applying it would discard every
            // mouse delta in between — a visible "view fights me" snap-back on each crossing at low fps. The
            // authoritative consume skips itself when it matches this (see the FixAngle block in _Process).
            _lastPredictedFixAngles = _viewAngles;
            _lastPredictedFixTime = now;
            _lastFixApplyTime = now;
            if (MenuState.Cvars.GetFloat("sv_warpzone_trace") != 0f)
                GD.Print($"[wzview] predicted snap -> {_viewAngles}");
            // Tell the view-model we teleported so its lean sway re-seeds to the destination facing instead of
            // snapping the gun across the screen (Base csqcmodel_teleported guard in viewmodel_animate). The
            // fixangle edge is exactly the predicted single-dest teleporter / warpzone view-snap.
            if (_viewModel is not null && GodotObject.IsInstanceValid(_viewModel))
                _viewModel.NotifyTeleported();
        }
    }

    // The last PREDICTED fixangle apply (angles + wall-clock seconds), so the authoritative consume can
    // recognise the same crossing's server stamp and skip the double-apply. -1 = none yet.
    private NVec3 _lastPredictedFixAngles;
    private float _lastPredictedFixTime = -1f;

    // The last APPLIED fixangle of either kind (wall-clock seconds) — the replay-echo discard window's anchor.
    private float _lastFixApplyTime = -1f;

    // One-shot consumption cursor for the carrier's LastTeleportTime pulse (the predicted-warpzone teleport
    // signal the view smoothing snaps on — see the faithfulSmoothing block).
    private float _lastSmoothedTeleportTime = -1f;

    // One-shot consumption cursor for the predicted-warp view pulse (TriggerTouch.LastPredictedWarpSeq).
    private uint _consumedWarpSeq;

    /// <summary>
    /// Per-render-frame local fire prediction + feedback, decoupled from the 1/72 s input cadence. With
    /// cl_predictfire (default on) a client-side REFIRE CLOCK fires the view-model muzzle flash + a local fire
    /// SOUND on every shot the instant the player fires (first on the press edge, then every <c>refire</c> s while
    /// held), gated on the active weapon / alive / ammo — so sustained fire feels instant; the local player's own
    /// NETWORKED fire-sound + muzzle-flash are suppressed elsewhere so they aren't doubled. With cl_predictfire 0
    /// it falls back to a single muzzle flash on the press edge (the networked sustained FX play). Both modes gate
    /// every predicted shot on a PERSISTENT per-weapon ready clock (<see cref="_weaponReadyTime"/>, a client mirror
    /// of the server's ATTACK_FINISHED) so spamming / wheel-firing the button can't out-run the weapon's real
    /// refire rate — the FX only replay as fast as the weapon actually fires. Either way each press edge sets a
    /// sub-tick latch (<see cref="_attackLatch"/>) so a tap shorter than one input tick still reaches the server.
    /// Inert while the in-game menu / console / minigame menu owns input.
    /// </summary>
    private void UpdateLocalFireFeedback(float dt)
    {
        _fireClock += dt;

        bool active = GameplayInputActive;
        if (!active)
        {
            // Input is owned elsewhere (in-game menu / console / minigame / quickmenu): drop the edge state and the
            // tap latch so nothing fires on resume (matches SampleInput's in_releaseall edge). The per-weapon ready
            // clock is left intact so a weapon mid-cooldown stays on cooldown across a menu blip.
            _attackHeld = _attack2Held = false;
            _attackLatch = _attack2Latch = false;
            return;
        }

        bool a1 = BindTable.AttackHeld;
        if (a1 && !_attackHeld)
            _attackLatch = true; // sub-tick latch for the server (independent of FX prediction)
        if (!a1)
            _rlReleaseMirror = true; // QC wr_think else-branch: rl_release = 1 any frame fire is up (must run
                                     // even inside the switch drop/raise gate below, or a release fully inside
                                     // the window would be missed and the next held shot never predicts)

        if (_switchRaiseLeft >= 0f)
            _switchRaiseLeft -= dt; // the incoming weapon's raise window decays here (set at install)

        // A staged weapon swap is holstering or raising (EquipNetworkedWeapon holds the old model for
        // switchdelay_drop, then the new gun raises for switchdelay_raise): the server slot is in the Drop/Raise
        // state and cannot fire, so don't predict flash/sound through it. Both press edges still latch for the
        // server, so the input stream is unaffected — only the local FX skip.
        if (_switchDropLeft >= 0f || _switchRaiseLeft >= 0f)
        {
            bool a2Drop = BindTable.Attack2Held;
            if (a2Drop && !_attack2Held)
                _attack2Latch = true;
            _attackHeld = a1;
            _attack2Held = a2Drop;
            return;
        }

        if (_predictFire)
        {
            // Client mirror of QC wepent.rl_release (devastator.qc wr_think: cleared on each rocket, re-armed any
            // frame fire is NOT held; wr_setup arms it on equip). A release-gated weapon fires whenever the button
            // is HELD with the latch armed and ATTACK_FINISHED has expired — NOT only on a fresh press. The old
            // press-edge-only gate (#24) missed the "re-press during the cooldown tail and keep holding" shot:
            // the server fires at cooldown expiry with no press edge in sight, nothing was predicted, and the
            // real fire sound/flash are suppressed as "already predicted" → a fully silent rocket.
            if (_client is { } clw && clw.ActiveWeaponId != _rlMirrorWeaponId)
            {
                _rlMirrorWeaponId = clw.ActiveWeaponId;
                _rlReleaseMirror = true; // QC wr_setup: rl_release = 1 on equip
            }

            // cl_predictfire on: predict the view-model flash + local fire sound on every shot (first on the press
            // edge, then one per `refire` s while held) — but ONLY once the weapon's persistent ready clock has
            // elapsed, so a burst of taps / wheel-fire can't out-run the refire rate. FirePredictReady + the
            // MarkFirePredicted cadence are the client mirror of the server's ATTACK_FINISHED gate.
            if (a1 && !LocalDeadNow() && HasAmmoNow()
                && TryActivePrimaryFire(out int wid, out string fireSound, out float refire) && FirePredictReady(wid)
                && (!PrimaryRefireRequiresRelease(wid) || _rlReleaseMirror)) // #24: release-gated weapons need the armed latch, not a press edge
            {
                PredictFireShot(fireSound);
                MarkFirePredicted(wid, refire, dt);
                _rlReleaseMirror = false; // QC: rl_release = 0 on the shot (inert for non-gated weapons)
            }
        }
        else if (a1 && !_attackHeld && !LocalDeadNow()
                 && TryActiveRefire(XonoticGodot.Common.Gameplay.FireMode.Primary, out int widOff, out float refireOff)
                 && FirePredictReady(widOff))
        {
            // cl_predictfire off: a single muzzle flash on the press edge (Phase 1; the networked sustained FX play),
            // still gated by the ready clock so tap-spam can't repeat it faster than the refire rate.
            _hud?.PulseFire();
            _viewModel?.Fire();
            MarkFirePredicted(widOff, refireOff, dt);
        }
        _attackHeld = a1;

        // Secondary fire: latch the press so its tap reaches the server, and pop a local muzzle flash on the press
        // edge for weapons whose secondary actually fires a shot (Base W_MuzzleFlash is called per shot for either
        // fire mode). Zoom-style secondaries (Vortex/Vaporizer/Rifle secondary = zoom, not a shot) must NOT flash,
        // so we gate on the weapon having a real secondary attack. Shares the SAME per-weapon ready clock as primary
        // (Base ATTACK_FINISHED is per-slot, not per-mode), so a secondary tap inside the refire window is dropped.
        bool a2 = BindTable.Attack2Held;
        if (a2 && !_attack2Held)
        {
            _attack2Latch = true;
            if (SecondaryFiresShot() && !LocalDeadNow()
                && TryActiveRefire(XonoticGodot.Common.Gameplay.FireMode.Secondary, out int wid2, out float refire2)
                && FirePredictReady(wid2))
            {
                _hud?.PulseFire();
                _viewModel?.Fire(); // local muzzle flash for the secondary shot (remote copy stays networked)
                MarkFirePredicted(wid2, refire2, dt);
            }
        }
        _attack2Held = a2;
    }

    /// <summary>
    /// Whether the given weapon's PRIMARY refire requires the fire button to be RELEASED between shots (so a
    /// sustained hold does NOT refire). The Devastator is the stock case: Base <c>W_Devastator</c> <c>wr_think</c>
    /// only fires when <c>rl_release</c> is set (button released since the last shot); holding fire GUIDES the
    /// rocket instead of refiring (devastator.qc:461-472). The simple client refire clock can't model that, so it
    /// predicted phantom muzzle-flash/fire-sound every refire while held (playtest #24) — for these weapons we
    /// predict only on the press edge. <c>guidestop 1</c> disables guiding → continuous fire like a normal weapon,
    /// so honour it (then this returns false and the normal held-fire prediction applies).
    /// </summary>
    private bool PrimaryRefireRequiresRelease(int wid)
    {
        // Netname-only, deliberately NO cvar read: the server-side balance cvar g_balance_devastator_guidestop is
        // NOT reliably reachable from the client here — _sharedCvars (client/menu store) returned a stale non-zero
        // (v1: phantom persisted), and Api.Cvars threw / broke the whole prediction path (v2: no flash at all). The
        // guidestop-1 config (continuous fire, no guiding) is rare and non-default; treating the Devastator as
        // always release-gated only costs a slightly under-predicted FX cadence in that rare case (cosmetic).
        return wid >= 0 && wid < XonoticGodot.Common.Gameplay.Weapons.Count
            && (XonoticGodot.Common.Gameplay.Weapons.ById(wid)?.NetName ?? "") == "devastator";
    }

    /// <summary>
    /// Whether the active weapon's SECONDARY fire emits a projectile/hitscan shot (so it should pop a local
    /// muzzle flash, Base <c>W_MuzzleFlash</c> per shot) rather than a non-shot action (zoom / melee / mode
    /// toggle) that has no flash. Conservative: excludes the stock weapons whose secondary is a zoom (Vortex /
    /// Vaporizer / Rifle) or a melee (Shotgun), and the Blaster/Hook whose secondary isn't a barrel shot; every
    /// other weapon's secondary is a real shot. Listen-server resolves nothing special — pure name gate.
    /// </summary>
    private bool SecondaryFiresShot()
    {
        int wid = _client?.ActiveWeaponId ?? -1;
        if (wid < 0 || wid >= XonoticGodot.Common.Gameplay.Weapons.Count)
            return false;
        string net = XonoticGodot.Common.Gameplay.Weapons.ById(wid)?.NetName ?? "";
        return net switch
        {
            "vortex" or "vaporizer" or "rifle" => false, // secondary = zoom
            "shotgun" => false,                           // secondary = melee (no muzzle)
            "blaster" or "hook" => false,                 // secondary isn't a barrel shot
            // Remote DETONATE, not a shot: Base wr_think fire&2 just flags the live rockets/mines (no
            // W_MuzzleFlash, no weapon_prepareattack — ATTACK_FINISHED is NOT bumped). Treating it as a shot
            // popped a phantom flash AND advanced the shared client ready clock, which then swallowed the
            // prediction of the NEXT primary rocket (the fire→detonate→fire combo went silent).
            "devastator" or "minelayer" => false,
            "" => false,
            _ => true,
        };
    }

    /// <summary>Play one predicted local shot: the view-model muzzle flash + recoil, the HUD crosshair pulse, and
    /// the weapon's primary fire sound at the predicted eye (auto-stack channel so rapid fire doesn't cut off).
    /// Source net-id = the local player so the matching networked copy is dropped in <see cref="OnSoundReceived"/>.</summary>
    private void PredictFireShot(string fireSound)
    {
        _hud?.PulseFire();
        _viewModel?.Fire();
        if (!string.IsNullOrEmpty(fireSound) && _render is not null && _client is not null)
            _render.OnSound(fireSound, _client.PredictedOrigin, 1f, 0.5f,
                (int)XonoticGodot.Common.Services.SoundChannel.WeaponAuto, _client.LocalNetId, 1f);
    }

    /// <summary>The active weapon's id + primary fire sound + refire interval, or false when it can't be predicted
    /// (no active weapon, or a loop/grapple weapon absent from <see cref="WeaponFireSounds"/> → its networked sound
    /// plays normally and is NOT suppressed).</summary>
    private bool TryActivePrimaryFire(out int weaponId, out string fireSound, out float refire)
    {
        weaponId = -1;
        fireSound = "";
        refire = 0.1f;
        if (_client is null || _client.ActiveWeaponId < 0)
            return false;
        weaponId = _client.ActiveWeaponId;
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(weaponId);
        if (w is null)
            return false;
        fireSound = WeaponFireSounds.PrimaryFor(w.NetName);
        if (string.IsNullOrEmpty(fireSound))
            return false;
        refire = w.RefireFor(XonoticGodot.Common.Gameplay.FireMode.Primary);
        if (refire <= 0f) refire = 0.1f;
        return true;
    }

    /// <summary>The active weapon's id + refire interval for <paramref name="mode"/>, or false when there's no
    /// active weapon. Unlike <see cref="TryActivePrimaryFire"/> this needs no fire-sound entry, so it also serves
    /// weapons that aren't sound-predicted (the cl_predictfire-off press-edge flash + the secondary flash) — the
    /// refire only feeds the ready clock. Both modes key the SAME ready slot (Base ATTACK_FINISHED is per-slot).</summary>
    private bool TryActiveRefire(XonoticGodot.Common.Gameplay.FireMode mode, out int weaponId, out float refire)
    {
        weaponId = -1;
        refire = 0.1f;
        if (_client is null || _client.ActiveWeaponId < 0)
            return false;
        weaponId = _client.ActiveWeaponId;
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(weaponId);
        if (w is null)
            return false;
        refire = w.RefireFor(mode);
        if (refire <= 0f) refire = 0.1f;
        return true;
    }

    /// <summary>Whether the persistent predicted-fire clock has elapsed for <paramref name="weaponId"/> (an unseen
    /// weapon is ready). Client mirror of the server's <c>ATTACK_FINISHED &lt;= time</c> gate
    /// (<see cref="XonoticGodot.Common.Gameplay.Weapons.WeaponFireGate"/>) — see <see cref="_weaponReadyTime"/>.</summary>
    private bool FirePredictReady(int weaponId)
        => !_weaponReadyTime.TryGetValue(weaponId, out float ready) || _fireClock >= ready;

    /// <summary>Advance the per-weapon predicted-fire clock after a predicted shot, mirroring the server's
    /// ATTACK_FINISHED cadence (WeaponFireGate): if the weapon wasn't firing continuously (its last scheduled shot
    /// was &gt; ~1.5 frames ago — a fresh tap/burst or a frame hitch) start from now so the opening shot isn't
    /// penalised and no catch-up backlog can bank up; otherwise accumulate so sustained fire holds an exact refire
    /// cadence with no per-frame drift. <paramref name="refire"/> is the base per-mode interval (client prediction
    /// does not apply g_weaponratefactor / haste — a rare, pre-existing approximation, same as before).</summary>
    private void MarkFirePredicted(int weaponId, float refire, float dt)
    {
        float step = Mathf.Max(refire, 0.02f);
        float ready = _weaponReadyTime.TryGetValue(weaponId, out float r) ? r : float.NegativeInfinity;
        if (ready < _fireClock - dt * 1.5f)
            ready = _fireClock;
        _weaponReadyTime[weaponId] = ready + step;
    }

    /// <summary>Whether the local player is dead (stop predicting fire). Listen server reads the host Player; a
    /// pure client derives it from networked health after the first spawn (matching the death-cam gate).</summary>
    private bool LocalDeadNow()
        => LocalServerPlayer is { } self ? self.IsDead : (_everAlive && _client is { } c && c.Health <= 0);

    /// <summary>Whether the active weapon has ammo to fire — else predicting would play phantom shots after the
    /// magazine empties. Uses the listen-server host player's live resources; a pure remote client has no server
    /// player here, so it skips the ammo gate (accepts a shot or two of over-prediction at ammo-out).</summary>
    private bool HasAmmoNow()
    {
        if (LocalServerPlayer is not { } p || _client is null || _client.ActiveWeaponId < 0)
            return true;
        XonoticGodot.Common.Gameplay.Weapon w = XonoticGodot.Common.Gameplay.Weapons.ById(_client.ActiveWeaponId);
        if (w is null || w.AmmoType == XonoticGodot.Common.Gameplay.ResourceType.None)
            return true; // infinite-ammo weapon (e.g. blaster)
        // Unlimited ammo (QC IT_UNLIMITED_AMMO — g_weaponarena / give unlimited_ammo): the numeric count can be 0
        // yet firing is allowed, so gate on the flag first (same check as Inventory.cs HasWeapon's ammo arm).
        // Without this the weapon-arena Devastator had ammo=False, which silently killed ALL fire prediction —
        // including the legitimate press-edge flash (#24 diagnostic: `ammo=False` was the only failing input).
        if (p.UnlimitedAmmo || (p.Items & 1) != 0) // bit 0 = QC IT_UNLIMITED_AMMO (common/items/item.qh)
            return true;
        return XonoticGodot.Common.Gameplay.Resources.GetResource(p, w.AmmoType) > 0f;
    }

#if XG_BOTPLAYER
    private bool _botPlayerAttached;

    /// <summary>
    /// Bind a bot brain to the local player once it exists (idempotent, compile-gated). The bind MUTATES
    /// server-side state that the sim worker owns, so it rides <c>RunOnSimThread</c> like every other
    /// main→sim mutation (inline when unthreaded).
    /// </summary>
    private void MaybeAttachBotPlayer()
    {
        if (_botPlayerAttached || _serverWorld is null || _server is null)
            return;
        if (LocalServerPlayer is not { } me)
            return;
        _botPlayerAttached = true;
        float skill = BotPlayerMode.Skill;
        XonoticGodot.Server.GameWorld world = _serverWorld;
        _server.RunOnSimThread(() => world.Bots.AttachBotPlayer(me, skill));
        GD.Print($"[bot-player] brain attached to the LOCAL player (skill {skill}) — "
                 + "input now synthesised through the real client pipeline.");
    }
#endif

    private InputCommand SampleInput()
    {
#if XG_BOTPLAYER
        // Bot-player harness: synthesise this tick's command from the brain bound to the local player. Sits
        // exactly where the camera-trace scripted input sits — the command below is indistinguishable from a
        // human's, so prediction/encode/reconcile all run for real. Inert unless --bot-player was passed.
        if (BotPlayerMode.Requested)
        {
            MaybeAttachBotPlayer();
            if (_botPlayerAttached && _carrier is not null && _serverWorld is not null)
            {
                XonoticGodot.Server.Bot.BotPopulation.BotPlayerCommand bp =
                    _serverWorld.Bots.BotPlayerCommandLatest;
                InputButtons bpButtons = InputButtons.None;
                if (bp.Attack1) bpButtons |= InputButtons.Attack;
                if (bp.Attack2) bpButtons |= InputButtons.Attack2;
                if (bp.Jump) bpButtons |= InputButtons.Jump;
                if (bp.Crouch) bpButtons |= InputButtons.Crouch;
                if (bp.Hook) bpButtons |= InputButtons.Hook;
                _viewAngles = bp.ViewAngles;   // slave the camera to the brain's aim, like the scripted path
                // Still carry a console/bind-issued impulse (edge-triggered, same as the live path) so an
                // agent can drive weapon switches etc. at a running bot-player session.
                int bpImpulse = _pendingImpulse;
                _pendingImpulse = 0;
                return new InputCommand
                {
                    ViewAngles = bp.ViewAngles,
                    Forward = bp.Forward, Side = bp.Side, Up = bp.Up,
                    Buttons = (int)bpButtons,
                    Impulse = bpImpulse,
                    DeltaTime = _inputDeltaTime,
                };
            }
        }
#endif

        // Camera-trace (apparatus A2): once spawned, feed the deterministic scripted input instead of the live
        // keyboard/mouse, and slave the view angles to it so the captured camera is reproducible. Inert unless
        // --camera-trace was passed; falls through to real input once the script is exhausted.
        if (CameraTrace.Active && _carrier is not null && CameraTrace.TryNextInput(out CameraTrace.InputSpec spec))
        {
            _viewAngles = spec.ViewAngles;
            return new InputCommand
            {
                ViewAngles = spec.ViewAngles,
                Forward = spec.Forward, Side = spec.Side, Up = spec.Up,
                Buttons = spec.Buttons, Impulse = 0,
                DeltaTime = _inputDeltaTime,
            };
        }

        // Re-capture on click if the cursor came free (e.g. after alt-tab); never while the tree is paused (the
        // in-game menu), the console is open, or the minigame menu is up — those own the cursor. Focus handling
        // (MouseCapture) normally recaptures on alt-tab back on its own; this is the same-frame fallback for a
        // click that lands before the focus-in edge, and it still can't grab an unfocused window.
        if (!GetTree().Paused && !ConsoleState.IsOpen && !UiOwnsCursor
            && Input.MouseMode != Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Left))
            MouseCapture.SetWantCapture(true);

        // Gameplay input is inert while the in-game menu is up (paused), the console is open, OR a cursor-owning
        // HUD UI (minigame menu/board or the quick-chat menu) is open. On the edge into inactive, drop all held
        // buttons (DP in_releaseall) so a key held at that moment doesn't stay down once input resumes. The view
        // angles hold their last value, so the camera stays put.
        bool active = GameplayInputActive;
        if (active != _inputActive)
        {
            _inputActive = active;
            if (!active) { BindTable.ReleaseAll(); _attackHeld = false; }
        }

        float forward = 0f, side = 0f, up = 0f;
        InputButtons buttons = InputButtons.None;
        if (active)
        {
            // Bind-driven button state (set from key events by BindInput → BindTable). Move axes are normalised
            // to ±1 here; the movement code rescales against sv_maxspeed. Jump/crouch both drive the up axis
            // (via BindTable.Up) and set their button bit.
            forward = BindTable.Forward;
            side = BindTable.Side;
            up = BindTable.Up;
            if (BindTable.AttackHeld) buttons |= InputButtons.Attack;
            if (BindTable.Attack2Held) buttons |= InputButtons.Attack2;
            if (BindTable.JumpHeld) buttons |= InputButtons.Jump;
            if (BindTable.CrouchHeld) buttons |= InputButtons.Crouch;
            if (BindTable.ZoomHeld) buttons |= InputButtons.Zoom;
            if (BindTable.UseHeld) buttons |= InputButtons.Use;
            // PHYS_INPUT_BUTTON_HOOK (+hook): the offhand-fire button — grapple hook / offhand blaster / nade.
            if (BindTable.HookHeld) buttons |= InputButtons.Hook;

            // Sub-tick fire latch (set per render frame in UpdateLocalFireFeedback): OR a press that landed since
            // the last sampled command into THIS command — so a tap shorter than one input tick still fires —
            // then clear it. The local muzzle flash / recoil / HUD pulse themselves are now driven per-frame in
            // UpdateLocalFireFeedback (CSQC W_MuzzleFlash on the local attack), no longer gated to this 1/72 s sample.
            if (_attackLatch) { buttons |= InputButtons.Attack; _attackLatch = false; }
            if (_attack2Latch) { buttons |= InputButtons.Attack2; _attack2Latch = false; }
        }

        // While FOLLOWING a player (spectatee_status > 0), the server keeps us MOVETYPE_NONE and glues us to the
        // spectatee — so suppress local movement (don't predict our own walk, which would fight the follow-cam),
        // but KEEP the buttons: +attack cycles the spectatee and +attack2 drops to free-fly on the server. (A
        // free-fly observer, spectatee_status == own id, still moves — server + client agree, no drift.)
        if (_client is not null && _client.SpectatingNetId != 0)
            forward = side = up = 0f;

        // PRE-MATCH COUNTDOWN FREEZE (QC PM_Main `time < game_starttime`; server GameWorld.preMatchFreeze). The
        // server pins the player AT SPAWN (canMove=false) during the ~5s start countdown but still ACKs our input
        // each tick (to avoid an un-acked pile-up, GameWorld.cs:1006-1014). If the client PREDICTED movement here,
        // its forward prediction would diverge from the server's frozen-at-spawn authoritative origin, and a
        // map-load hitch's sudden batch-ack collapses the unacked window past the 32u reconcile snap threshold →
        // the camera teleports back to spawn (the "first ~5s rubberbands me to spawn ~5x" report, mode-agnostic so
        // no movement cvar helped). Match the server (and Base, whose CSQC prediction freezes the same way): predict
        // NO movement while frozen — zero the move axes + clear jump/crouch. WARMUP is freely playable, so it is NOT
        // frozen (MatchWarmup). View angles + fire are kept (look around; the weapon think is server-owned).
        if (_client is not null && _client.HasMatchState && !_client.MatchWarmup
            && _client.LatestServerTime < _client.MatchStartTime)
        {
            forward = side = up = 0f;
            buttons &= ~(InputButtons.Jump | InputButtons.Crouch);
        }

        // PHYS_INPUT_BUTTON_CHAT: tag the command as "typing" whenever the player has a text prompt open (the
        // in-game console, where say / messagemode chat is entered). Set OUTSIDE the `active` gate above: opening
        // the console makes input inactive (movement keys are released + zeroed), but the typing FLAG itself must
        // still ride the command so the server can exempt the typist (camp-check g_campcheck_typecheck gate,
        // type-frag classification, etc.). Mirrors QC PHYS_INPUT_BUTTON_CHAT being live while the chat box is up.
        if (ConsoleState.IsOpen || XonoticGodot.Game.Hud.ChatPrompt.IsOpen)
            buttons |= InputButtons.Chat; // messagemode prompt ALSO raises BUTTON_CHAT (the DP key_dest != game rule) -> server chat bubble

        // C2S impulse (QC usercmd.impulse): consume the one-shot weapon-switch/reload number a bind set this
        // frame (RunBoundCommand stamped it into _pendingImpulse, edge-triggered). Stamp it onto THIS command and
        // clear the pending value so it rides exactly one InputCommand — the redundant input tail re-sends the
        // same Seq (carrying the impulse), but the server's Seq-dedup processes it once, and the server zeroes it
        // on the cached command so the starve-repeat can't re-fire it. Suppressed while input is inactive.
        int impulse = active ? _pendingImpulse : 0;
        _pendingImpulse = 0;

        // Seq is stamped by the prediction ring buffer (PredictionBuffer.Push), so we leave it 0 here.
        return new InputCommand
        {
            ViewAngles = _viewAngles,
            Forward = forward,
            Side = side,
            Up = up,
            Buttons = (int)buttons,
            Impulse = impulse,
            // TicRate in legacy mode; the real clamped frame dt in per-frame mode (set before SendInput above).
            DeltaTime = _inputDeltaTime,
        };
    }

    // =====================================================================================
    //  Camera (driven by the SHARED FirstPersonView — same component PlayerController uses)
    // =====================================================================================

    // Spectate-angle smoother state: the currently-smoothed quake angles + the netId they belong to
    // (0 = unseeded; a target change re-seeds so a spectatee switch snaps instead of slewing).
    private NVec3 _specAngSmoothed;
    private int _specAngForId;

    /// <summary>
    /// Exponential shortest-arc smoothing over the followed player's view angles
    /// (<c>cl_spectate_smoothangles</c> = half-life in seconds, 0 = raw). The spectatee's angles arrive in
    /// steps — a bot re-aims only every AI think, a human's angles land at the snapshot rate — and the raw
    /// follow-cam pans in those steps. Smoothing is a spectator-view-only quality deviation from Base (which
    /// renders the raw angles); it never touches gameplay or the local first-person path. Snaps outright on a
    /// spectatee change and on any component jump &gt; 90° (respawn / teleport / warp crossing), so it cannot
    /// slow-slew across a legitimate discontinuity.
    /// </summary>
    private NVec3 SmoothSpectateAngles(NVec3 target, float dt)
    {
        int id = _client?.SpectatingNetId ?? 0;
        float halflife = CvarOr(Api.Cvars, "cl_spectate_smoothangles", 0.05f);
        if (halflife <= 0f || id == 0 || _specAngForId != id)
        {
            _specAngForId = id;
            _specAngSmoothed = target;
            return target;
        }
        float dp = AngleDelta180(target.X - _specAngSmoothed.X);
        float dy = AngleDelta180(target.Y - _specAngSmoothed.Y);
        float dr = AngleDelta180(target.Z - _specAngSmoothed.Z);
        if (System.MathF.Abs(dp) > 90f || System.MathF.Abs(dy) > 90f)
        {
            _specAngSmoothed = target;   // discontinuity — snap, don't slew
            return target;
        }
        float k = 1f - System.MathF.Pow(0.5f, dt / halflife);
        _specAngSmoothed = new NVec3(
            AngleDelta180(_specAngSmoothed.X + dp * k),
            AngleDelta180(_specAngSmoothed.Y + dy * k),
            AngleDelta180(_specAngSmoothed.Z + dr * k));
        return _specAngSmoothed;
    }

    /// <summary>Wrap an angle/delta to the shortest-arc (-180, 180] range.</summary>
    private static float AngleDelta180(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        else if (a < -180f) a += 360f;
        return a;
    }

    /// <summary>
    /// Drive the shared <see cref="Client.FirstPersonView"/> from the PREDICTED local state: the predicted origin
    /// (with the decaying prediction-error + stair-smooth offsets folded in) + predicted velocity + the dead test
    /// (Health&lt;=0 — the net path has no MaxHealth). FirstPersonView places + orients the camera with the proven
    /// AngleVectors → Coords.ToGodot columns → Basis(right, up, -forward) path (a negated-yaw Euler silently flips
    /// handedness), runs the death/event-chase pull-back, lerps the zoom, applies the *0.75 fov, and samples the
    /// eye contents at the FINAL render origin — exactly as on the local path. <paramref name="dt"/> 0 = a
    /// placement-only call (the first-snapshot seed); the per-frame call in <see cref="_Process"/> passes real dt.
    /// </summary>
    private void UpdateCamera(float dt = 0f)
    {
        if (_client is null || _camera is null)
            return;

        // Dev/CI --observe: pin the camera at the fixed Quake-space point/angles (ObserverCamera), ignoring
        // the local player/prediction entirely. Velocity 0 → no bob; not dead → no event-chase.
        //
        // The eye offset is subtracted here and handed back through EyeHeightZ, so the two ALWAYS cancel.
        // Subtracting the static EyeHeight and passing 0 did not: 0 means "fall back to _view.EyeHeight",
        // which is 0 while observing, so the pin landed 35 units BELOW the requested point — every scripted
        // capture silently framed a different place than the one being reported.
        if (ObserverCamera.Active)
        {
            _view.Spectating = false;
            _view.CameraMode = Client.FirstPersonView.ChaseMode.None;
            var ost = new Client.FirstPersonView.ViewState
            {
                OriginQuake = ObserverCamera.OriginQuake - new NVec3(0f, 0f, PinEyeOfs),
                VelocityQuake = NVec3.Zero,
                ViewAnglesQuake = ObserverCamera.AnglesQuake,
                IsDead = false,
                EyeHeightZ = PinEyeOfs,   // exactly what was subtracted above, so the camera lands on the point
            };
            _view.UpdateView(_camera, ost, dt);
            return;
        }

        // Follow-cam: while spectating a specific player (spectatee_status > 0), render from THAT player's
        // interpolated pose + view angles, not the local predicted origin. The server glues us to the spectatee
        // (MOVETYPE_NONE + SpectateCopy) so the owner-state origin already tracks them, but the local predictor
        // would still replay our inputs and walk the camera off them; sourcing the followed entity's snapshot pose
        // directly (and slaving the view angles, QC SpectateCopy fixangle) is the robust fix. Falls through to the
        // predicted path if the entity isn't known yet (then the zeroed spectate input keeps it at the glued origin).
        if (_client.SpectatingNetId != 0
            && _client.SampleRemote(_client.SpectatingNetId, _client.LatestServerTime, out NVec3 specOrg, out NVec3 specAng))
        {
            // QC View_SpectatorCamera (view.qc:655): while following a player (spectatee_status > 0), the user
            // chase_active cvar drives a THIRD-PERSON spectator camera that pulls back from the spectated player
            // (chase_back/up + the chase_front frontal selfie). Tell the shared view it is spectating so the
            // chase_front branch is reachable (Base guards it on spectatee_status), and engage the classic chase
            // mode from chase_active just like the own-player path below — but anchored to the spectatee's pose.
            // chase_active == 0 keeps the faithful first-person follow (see what the spectated player sees).
            _view.Spectating = true;
            _view.CameraMode = CvarOr(Api.Cvars, "chase_active", 0f) != 0f
                ? Client.FirstPersonView.ChaseMode.Chase
                : Client.FirstPersonView.ChaseMode.None;
            // The camera sits inside the followed player in first person — hide that body exactly like one's
            // own (which never even reaches the client's entity stream; the spectatee does). Chase shows it.
            _render?.SetViewedFirstPerson(
                _view.CameraMode == Client.FirstPersonView.ChaseMode.None ? _client.SpectatingNetId : 0);
            var sst = new Client.FirstPersonView.ViewState
            {
                OriginQuake = specOrg,
                VelocityQuake = NVec3.Zero,
                // See what the spectated player sees — through a smoother: a bot only re-aims every AI think
                // and a human's angles arrive at the tick rate, so the raw follow angles pan in visible steps.
                ViewAnglesQuake = SmoothSpectateAngles(specAng, dt),
                IsDead = false,              // following a live player — no death-cam pullback
                EyeHeightZ = EyeHeight,
            };
            _view.UpdateView(_camera, sst, dt);
            return;
        }
        _view.Spectating = false;
        _render?.SetViewedFirstPerson(0);   // not following → every body draws again
        _specAngForId = 0;                  // and the spectate-angle smoother re-seeds on the next follow

        float now = _renderClock; // the clock the reconciler armed the prediction-error decay with (see _Process)
        NVec3 predicted = _client.PredictedOrigin + _client.PredictionErrorOffset(now);

        // A DEAD local player's body is frozen at the death spot (PlayerPhysics.Move bails on IS_DEAD). The server
        // still networks the velocity it died with, so over a void that's a large downward speed — extrapolating
        // the eye by it each render frame, then snapping back when the input tic drains, shakes the death-cam (the
        // "view shakes over a void" report). The corpse isn't predicting movement, so treat its velocity as zero
        // for all render purposes: no sub-tic eye extrapolation and no velocity-driven view effects while dead.
        // (net path has no MaxHealth, so dead = Health<=0 — but ONLY after the first spawn, so the pre-spawn
        // observer 0 doesn't engage the death-cam at the world origin.)
        // At intermission the server stamps RES_HEALTH = -2342 (QC FixIntermissionClient's first-phase sentinel),
        // which also reads as Health<=0; but SVC_INTERMISSION freezes the view at the player's spot rather than
        // engaging the death-cam, so suppress the dead/death-cam path while MatchIntermission is set.
        bool localDead = _everAlive && _client.Health <= 0 && !_client.MatchIntermission;
        // Live: the rendered velocity is the predicted velocity PLUS the decaying velocity-error offset (QC
        // `this.velocity += CSQCPlayer_GetPredictionErrorV()`). It is normally ~0; after a smoothed correction —
        // above all a damage-knockback shove (Reconciler force smoothing) — it ramps the rendered velocity from the
        // old value up to the new over the smoothing window, so the view bob/sway and the sub-tic eye extrapolation
        // below ease into the shove instead of the velocity snapping. Zeroed while dead (frozen corpse has no bob).
        NVec3 viewVelocity = localDead ? NVec3.Zero : _client.PredictedVelocity + _client.PredictionVelocityErrorOffset(now);

        // Sub-tic view smoothing (DP's partial final movement frame): the predicted origin only advances when an
        // input tic is drained, so it moves in discrete 1/72 s (~13.9 ms) steps while the camera renders at the
        // display rate. At any framerate that isn't a multiple of 72 the per-frame step count alternates (0/1, or
        // 1/2), which reads as a faint shimmer/stutter even on a locked-fps machine. DP hides this by simulating a
        // partial final frame each render; we approximate it by extrapolating the eye forward over the as-yet-
        // unsimulated remainder (_inputAccum, always < one tic) along the PREDICTED velocity. That velocity is the
        // post-slide-move value (its into-surface component already clipped by PushEntity), so extrapolating along
        // it slides parallel to walls/floors rather than poking through them, and the next real tic lands within a
        // few units of the extrapolated eye — so the error smoother never engages. Render-only: it does not touch
        // the authoritative predicted state, the input ring, or anything the server/reconciler sees. Suppressed
        // while dead (viewVelocity is zeroed above) so the frozen corpse's death-fall speed can't bob the eye.
        //
        // DIAGNOSTIC / quality toggle cl_movement_subtic_extrapolate (default 1 = current behaviour). This LINEAR
        // extrapolation ignores the sub-tic gravity/accel curve, and `_inputAccum` (the leftover < one tic) varies
        // frame-to-frame at any fps that isn't a multiple of 72 — so the eye gets shoved by velocity*_inputAccum in a
        // pattern that BEATS with the framerate (up to ~5u horizontally at bhop speed), which can read as inconsistent
        // hop rhythm. It is NOT gated by the cl_movement_smoothing_* cvars (those tune POST-extrapolation smoothing).
        // Set 0 to render the eye at the last simulated tic (no extrapolation) — an A/B isolation switch for the
        // bhop-timing jitter; the proper fix (a gravity-correct partial-tic sub-step, like DP) supersedes it.
        if (CvarOr(Api.Cvars, "cl_movement_subtic_extrapolate", 1f) != 0f)
            predicted += viewVelocity * _inputAccum;

        // cl_movement_smoothing_faithful (default 1): route the view smoothing through the FAITHFUL Base algorithm
        // (CSQCPlayer_ApplySmoothing — bound()-based stairsmoothz glide + viewheightavg eye-height blend, error
        // compensation OFF so corrections SNAP), so the rendered eye matches stock Xonotic exactly and the only
        // intentional divergence from Base is the stepheight processing. 0 = the port path (adaptive stair catch-up
        // + error-comp/knockback glide), with the accumulation bug-fix in Reconciler.SetPredictionError.
        XonoticGodot.Common.Services.ICvarService cv = Api.Cvars;
        bool faithfulSmoothing = CvarOr(cv, "cl_movement_smoothing_faithful", 1f) != 0f;

        // Prediction-error view smoothing, read live so the cvars (and the settings-menu checkbox) tune in-session:
        //   cl_movement_errorcompensation       — strength; 0 = snap to truth (smoothing off), the stock toggle.
        //   cl_movement_errorcompensation_force_time — how long an explosion/blaster shove glides (port extension;
        //                                              0 falls back to the one-tick residual window, knockback pops).
        // FAITHFUL forces the Base default (0 = snap): a correction the smoother would smear into a drifting camera
        // lag instead lands as a clean snap to truth, exactly as stock Xonotic renders it.
        _client.ConfigureErrorSmoothing(
            errorComp:   faithfulSmoothing ? 0f : CvarOr(cv, "cl_movement_errorcompensation", 0f),
            forceWindow: faithfulSmoothing ? 0f : CvarOr(cv, "cl_movement_errorcompensation_force_time", 0.12f));

        float eyeOfsZ = _carrier?.ViewOfs.Z ?? EyeHeight;
        if (faithfulSmoothing)
        {
            // Faithful stair + eye-height smoothing on the predicted origin (driven by the real frame dt). The
            // smoothed origin Z feeds OriginQuake.Z and the blended view height feeds EyeHeightZ — the same shape
            // FirstPersonView consumes. The PredictionErrorOffset above is 0 here (errorcomp forced off).
            _faithfulSmoothing.StairSmoothSpeed = CvarOr(cv, "cl_stairsmoothspeed", 200f);
            _faithfulSmoothing.SmoothViewHeight = CvarOr(cv, "cl_smoothviewheight", 0.05f);
            _faithfulSmoothing.StepHeight = CvarOr(cv, "sv_stepheight", 31f);
            bool onground = _carrier?.OnGround ?? true;
            // A teleport this tick snaps the glide instead of smoothing the cross-map jump — QC csqcmodel_teleported
            // does the same. Two signals: the carrier .fixangle (predicted trigger_teleport pass) and the
            // LastTeleportTime pulse (the predicted WARPZONE crossing, which deliberately does not stamp fixangle
            // — see PredictWarpzonesAmbient; without this pulse the stair smoother glides the height difference
            // between the paired windows and every crossing reads as a dip/step).
            bool teleported = _carrier?.FixAngle ?? false;
            if (_carrier is not null && _carrier.LastTeleportTime != _lastSmoothedTeleportTime)
            {
                _lastSmoothedTeleportTime = _carrier.LastTeleportTime;
                if (_carrier.LastTeleportTime > 0f)
                    teleported = true;
            }
            XonoticGodot.Net.FaithfulViewSmoothing.Result r =
                _faithfulSmoothing.Apply(predicted.Z, dt, onground, eyeOfsZ, teleported);
            predicted.Z = r.StairZ;
            eyeOfsZ = r.ViewHeightZ;
        }
        else
        {
            // Port path: the render-only adaptive stair offset (driven by the REAL frame delta, not the server-
            // synced render clock whose 1/72-quantized jumps jittered the catch-up). Refresh tunables live.
            _client.ConfigureStairSmoothing(
                smoothSpeed: CvarOr(cv, "cl_stairsmoothspeed", 200f),
                stepHeight:  CvarOr(cv, "sv_stepheight", 31f),
                snapSpeed:   CvarOr(cv, "cl_stairsmooth_snapspeed", 30f),
                catchupTime: CvarOr(cv, "cl_stairsmooth_catchuptime", 0.1f));
            predicted.Z -= _client.PredictedStairOffset(dt);
        }

        var st = new Client.FirstPersonView.ViewState
        {
            OriginQuake = predicted,
            VelocityQuake = viewVelocity, // zeroed while dead (see localDead above) so a frozen corpse has no bob/sway
            // QC `view_angles += view_punchangle` (cl_player.qc): the recoil kick is added to the rendered VIEW
            // only — _viewAngles (the aim sent to the server) stays unpunched so shots still land on the crosshair.
            ViewAnglesQuake = _viewAngles + _client.PunchAngle,
            // QC `vieworg += view_punchvector` (cl_player.qc:570): the origin recoil kick added to the rendered eye
            // (first-person only — FirstPersonView suppresses it while chase/death-cam is active).
            PunchOriginQuake = _client.PunchVector,
            // An observer has zero health, which LocalDeadNow reads as dead — that engaged the death-chase
            // camera every time the mapper dropped out of PLAYTEST. Observing is not dying.
            IsDead = localDead && !IsLocalObserving,
            // QC STAT(FROZEN) → cl_ft WantEventchase: engage the third-person cam while Freeze-Tag frozen (host
            // path; the local Player carries the Frozen status effect). A pure remote client reads false here.
            IsFrozen = LocalServerPlayer is { } frozenSelf && StatusEffectsCatalog.Frozen is { } frozenDef2
                && StatusEffectsCatalog.Has(frozenSelf, frozenDef2),
            // QC cl_nexball.qc WantEventchase: cl_eventchase_nexball=1 pulls the cam to third person whenever the
            // local player is a nexball participant but is NOT the ball carrier. The NexBall status block (decoded
            // above in UpdateModIcons) already holds the resolved carrying flag; we need the INVERSE of it, gated
            // on the mode being NexBall so non-nexball matches are unaffected.
            IsNexBallNonCarrier = _fullHud.ModIcons.Mode == ModIconsPanel.ModIconsMode.NexBall
                && !_fullHud.ModIcons.NexBallCarrying,
            // Eye drops while crouched: the predicted carrier carries the live view offset (PlayerPhysics.UpdateCrouch
            // sets ViewOfs to the crouch/standing value each predicted tick, QC STAT(PL_CROUCH_VIEW_OFS)/PL_VIEW_OFS).
            // In faithful mode this is the viewheightavg-blended height (smooth crouch); else the raw live offset.
            EyeHeightZ = eyeOfsZ,
            // QC IS_ONGROUND / (input_buttons & BIT(1)): drive the horizontal view-bob smooth ramp (cl_bob2, off
            // by default) and the fall-bob swing trigger (cl_bobfall 0.05 — ON in stock Xonotic: the landing dip).
            OnGround = _carrier?.OnGround ?? false,
            JumpHeld = BindTable.JumpHeld,
        };
        // QC cl_eventchase_vehicle: while seated in a vehicle the shared view engages the cockpit/chase pull-back
        // (FirstPersonView.ApplyVehicle). The HUD already resolved seated-ness this frame (VehicleHud.InVehicle,
        // host or remote), so reuse it as the camera's gate. The chase pivot is the seated origin — st.OriginQuake
        // is already glued to the vehicle origin + '0 0 32' for the seated carrier, so no extra origin plumbing.
        _view.InVehicle = _fullHud.Vehicle.InVehicle;
        _view.UpdateView(_camera, st, dt);
    }

    /// <summary>Read a float cvar, falling back to <paramref name="fallback"/> only when it is UNSET (empty string),
    /// so a deliberately-configured 0 (e.g. <c>cl_stairsmooth_catchuptime 0</c> = adaptive off) is honoured.</summary>
    private static float CvarOr(XonoticGodot.Common.Services.ICvarService c, string name, float fallback)
    {
        string s = c.GetString(name);
        return s.Length == 0 ? fallback : c.GetFloat(name);
    }

    /// <summary>The active slowmo / host_timescale the client scales its time accumulators (input cadence + render
    /// clock) by, so it stays in lockstep with the server's <c>SimulationLoop.TimeScale</c>. Listen server: the
    /// shared <c>slowmo</c> cvar. (Remote-client replication of slowmo is a follow-up.) 1 = real time, clamped &gt;= 0.</summary>
    private static float ResolveTimeScale() => ServerNet.ResolveSlowmo(Api.Cvars);

    /// <summary>SUPERCONTENTS at the FINAL render origin this frame — read back from the shared view after
    /// <see cref="UpdateCamera"/> ran (QC pointcontents(view_origin) at the pulled-back cam during a chase). A
    /// listen server samples the real map collision; a pure client's flat facade reports empty until client-side
    /// map collision lands.</summary>
    private int SampleEyeContents() => _view.EyeContents;

    // =====================================================================================
    //  Effect rendering (decoded EFF_NET_* → particles)
    // =====================================================================================

    private void OnEffectReceived(ClientNet.EffectEvent e)
    {
        if (_render is null || !GodotObject.IsInstanceValid(_render))
            return;
        // Prefer the registered name; fall back to the effectinfo name carried on the by-name engine-fallback path
        // (QC Send_Effect_ → __pointparticles), which the renderer resolves through the effectinfo.txt catalog.
        string name = e.Effect?.Name ?? e.EffectName ?? "";
        if (string.IsNullOrEmpty(name))
            return;
        Color? tint = (e.ColorMin != default || e.ColorMax != default)
            ? new Color((e.ColorMin.X + e.ColorMax.X) * 0.5f, (e.ColorMin.Y + e.ColorMax.Y) * 0.5f, (e.ColorMin.Z + e.ColorMax.Z) * 0.5f)
            : (Color?)null;
        _render.OnEffect(name, e.Origin, e.Velocity, Math.Max(1, e.Count), tint);
    }

    /// <summary>A decoded positional sound (DP SV_StartSound) → play it on the renderer. Routes by the wire flags:
    /// a STOP ends the looping sound on the emitter+channel; a LOOP starts/refreshes a persistent source keyed by
    /// (emitter, channel) that follows the entity (Arc beam, vehicle engines); otherwise a one-shot on the pool.
    /// Calls <see cref="ClientWorld"/> directly with volume+attenuation (NOT the legacy 2-arg SoundHook).</summary>
    private void OnSoundReceived(ClientNet.SoundEvent e)
    {
        if (_render is null || !GodotObject.IsInstanceValid(_render))
            return;

        // Drop the local player's OWN primary fire sound (cl_predictfire): we already played it locally on the
        // refire clock, so the networked echo would double it. Keyed by source net-id + a predicted fire sample so
        // it never suppresses footsteps/pain/reload/secondary (other channels/samples), and remote players are
        // unaffected (they don't match LocalNetId). A non-predicted weapon (loops/grapple) isn't in the set → its
        // networked fire sound plays normally.
        if (_predictFire && _client is not null && e.SourceNetId == _client.LocalNetId
            && WeaponFireSounds.IsPredicted(e.Sample))
            return;

        if (e.Stop)
        {
            // DP sound(e, ch, SND_Null): end the loop on this emitter+channel (the Arc beam on release/overheat).
            _render.OnStopSound(e.SourceNetId, e.Channel);
            return;
        }
        if (string.IsNullOrEmpty(e.Sample))
            return;
        if (e.Loop)
        {
            // A persistent loop keyed by (emitter, channel) that follows the entity (QC loopsound).
            _render.OnLoopingSound(e.SourceNetId, e.Channel, e.Sample, e.Origin, e.Volume, e.Attenuation);
            return;
        }
        _render.OnSound(e.Sample, e.Origin, e.Volume, e.Attenuation, e.Channel, e.SourceNetId, e.Pitch);
    }

    /// <summary>Play a one-shot NON-spatial 2D cue (DP <c>sound(world, …, ATTN_NONE)</c> / a CSQC local play) —
    /// e.g. SND_BLIND on a darkness-nade onset. Loads through the same VFS <see cref="AssetLoader.LoadSound"/> the
    /// HUD/announcer use, plays on the SFX bus, and self-frees when finished (no pooled emitter — these fire
    /// rarely). No-op if the asset loader or the sample is missing.</summary>
    private void PlayLocal2DSound(string sample)
    {
        if (_assets is null || string.IsNullOrEmpty(sample))
            return;
        AudioStream? stream = _assets.LoadSound(sample);
        if (stream is null)
            return;
        var player = new AudioStreamPlayer { Name = "Local2DSound", Bus = "SFX", Stream = stream };
        player.Finished += player.QueueFree; // one-shot: drop the node once the cue ends
        AddChild(player);
        player.Play();
    }

    /// <summary>
    /// The current Quake-space origin of a networked entity by net id (<see cref="ClientWorld.EntityOriginResolver"/>),
    /// so a looping positional sound follows its emitter each frame: the LOCAL player tracks the predicted origin,
    /// a remote entity its interpolated snapshot pose. Null when the id isn't known yet (the loop holds its spot).
    /// </summary>
    private NVec3? ResolveEntityOrigin(int netId)
    {
        if (_client is null)
            return null;
        if (netId == _client.LocalNetId)
            return _client.PredictedOrigin;
        if (_client.SampleRemote(netId, _client.LatestServerTime, out NVec3 origin, out _))
            return origin;
        return null;
    }

    /// <summary>A decoded notification → drive the announcer voice (MSG_ANNCE only) through the hidden HUD host.
    /// Center/info text need the full panel HUD (NetHud lacks it), so they're skipped on the net path for now.</summary>
    /// <summary>A decoded notification → drive the full HUD (T34): MSG_CENTER → centerprint, MSG_INFO →
    /// kill-feed / info line, MSG_ANNCE → announcer voice. Previously only MSG_ANNCE was handled (the net path
    /// had no panel HUD); now the full <see cref="XonoticGodot.Game.Hud.Hud"/> lets <see cref="XonoticGodot.Game.Hud.HudNotifications"/>
    /// render center/info text too. HudNotifications routes by type internally.</summary>
    private void OnNotificationReceived(ClientNet.NotificationEvent e)
    {
        if (_notifications is null)
            return;
        _notifications.OnNotification(e.Notification, e.Type, e.Text, e.StringArgs, e.FloatArgs);
    }

    /// <summary>A decoded minigame session snapshot (QC activate/deactivate_minigame) → drive the board overlay
    /// + menu through the client coordinator, then refresh the MinigameHelp panel's active game (goal 4). The
    /// coordinator sets <see cref="MinigameClient.Active"/> from the envelope first; we read its netname (e.g.
    /// "ttt_3") into <see cref="XonoticGodot.Game.Hud.MinigameHelpPanel.ActiveMinigame"/>, which self-blanks the
    /// help panel when null (no active game). The help panel only enters the draw set under PanelShow.Minigame.</summary>
    private void OnMinigameStateReceived(MinigameNetState.Envelope env)
    {
        _minigame?.OnEnvelope(env);
        if (_fullHud?.GetPanel<XonoticGodot.Game.Hud.MinigameHelpPanel>() is { } help)
            help.ActiveMinigame = _minigame?.Active?.NetName;
    }

    /// <summary>Feed each ServerPrint line to the minigame coordinator's session-list parser (the
    /// <c>cmd minigame list-sessions</c> reply populates the Join menu). Harmless for non-minigame prints — the
    /// coordinator only keeps single-token "&lt;game&gt;_&lt;n&gt;" lines.</summary>
    private void OnServerPrintForMinigame(string line) => _minigame?.NoteSessionListLine(line);

    /// <summary>
    /// Feed each ServerPrint line into the ChatPanel scrollback (QC HUD panel #12). The port has no dedicated chat
    /// net channel: server chat (<c>say</c>/<c>say_team</c>) is broadcast via <c>ServerNet.BroadcastPrint</c> and
    /// arrives here on the SAME print stream. Empty lines are ignored by <see cref="XonoticGodot.Game.Hud.ChatPanel.AddLine"/>;
    /// the panel honours <c>con_chattime</c>/<c>con_chatsize</c> and self-blanks when empty. Drops the single-token
    /// minigame session-list replies ("ttt_3") so they don't clutter the chat area.
    /// </summary>
    private void OnServerPrintForChat(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        // Skip the bare "<game>_<n>" session-list reply tokens (handled by OnServerPrintForMinigame).
        string trimmed = line.Trim();
        if (!trimmed.Contains(' ') && _minigame is not null && trimmed.IndexOf('_') > 0)
            return;
        _fullHud?.GetPanel<XonoticGodot.Game.Hud.ChatPanel>()?.AddLine(line);
    }

    // =====================================================================================
    //  Asset resolution (shared with GameDemo's wiring; built per entity on first sight)
    // =====================================================================================

    private PlayerModel? ResolvePlayerModel(Entity e)
    {
        if (_assets is null || string.IsNullOrEmpty(e.Model)
            || e.Model.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0)
            return null;
        // A model the async resolve already settled as non-skeletal: behave like the old sync path's null
        // return so ClientWorld falls through to the MD3/static-prop attach.
        if (_nonSkeletalPlayerModels.Contains(e.Model))
            return null;

        // Wave 1 (perf report §9.4): do NOT parse+build synchronously here. The first snapshot of a bot match
        // carries EVERY player at once, and building all ~25 MB skeletal models in that one frame was a
        // ~154 MB allocation burst → a 100–150 ms blocking gen2 GC (the worst release-build hitch). Return a
        // placeholder shell immediately; the IQM+sidecar parse runs on the thread pool and the Godot build
        // lands under the streamer's per-frame budget — one model per frame, High priority (ahead of idle warm).
        return StreamSkeletalInto(e, e.Model, (int)e.Skin, $"player#{e.Index}", forced: false);
    }

    /// <summary>
    /// Shared async core for the live player-model path (the on-demand <see cref="ResolvePlayerModel"/> and the
    /// FORCEMODEL <see cref="BuildForcedPlayerModel"/>): return a gray placeholder <see cref="PlayerModel"/> shell
    /// immediately and stream the IQM parse off-thread + the Godot build under the streamer's per-frame budget,
    /// filling the shell in <see cref="DeliverPlayerModel"/>. <paramref name="forced"/> routes the delivery's
    /// fallback branches through the forced-miss memo (FORCEMODEL parity) instead of the own-model memo. When no
    /// streamer/assets are wired (should not happen on the live path) it runs the old synchronous load and returns
    /// the filled shell (or null on miss).
    /// </summary>
    private PlayerModel? StreamSkeletalInto(Entity e, string model, int skin, string shellName, bool forced)
    {
        var pm = new PlayerModel { Name = shellName };
        pm.ShowPlaceholder();
        if (_streamer is null || _assets is not { } assets)
        {
            // No streamer (shouldn't happen on the live path) — fall back to the old synchronous load.
            AssetLoader.SkeletalModelParts? parts = _assets?.LoadSkeletalModel(model, skin);
            if (parts is null) { pm.QueueFree(); return null; }
            pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
            if (!pm.Active) { pm.QueueFree(); return null; }
            return pm;
        }

        _streamer.Request(
            // Off-thread: pure-C# parse (now incl. the animation library, §12.3-1). Boxed in a non-null wrapper
            // because the streamer drops null results silently — but a FAILED parse must still reach the main
            // thread to trigger the fall-back attach.
            () => new SkeletalParseBox(assets.ParseSkeletalModel(model, skin)),
            box =>
            {
                if (box.Parse is null)
                {
                    DeliverPlayerModel(pm, e, model, skin, null, forced);   // miss → fall-back attach, as before
                    return;
                }
                // (§12.3-1) Stage the texture pipeline BEFORE the delivery: one streamer job per material
                // (worker pre-decodes its images, main thread only uploads), then the now-cheap assembly.
                // The old single delivery paid ~395 ms of synchronous decode+upload in one frame.
                EnqueueStagedSkeletalBuild(assets, box.Parse,
                    XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.High, $"player model {model}",
                    () => DeliverPlayerModel(pm, e, model, skin, box.Parse, forced));
            },
            XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority.High,
            label: $"player model {model} (parse)");
        return pm;
    }

    /// <summary>Non-null off-thread wrapper for a (possibly failed) skeletal parse — see ResolvePlayerModel.</summary>
    private sealed record SkeletalParseBox(AssetLoader.SkeletalModelParse? Parse);

    /// <summary>
    /// (§12.3-1/§12.6) Enqueue the staged main-thread half of a skeletal-model build: one streamer job per
    /// TEXTURE the model's materials will probe — its worker phase pre-decodes that image
    /// (<see cref="AssetSystem.PredecodeTexture"/>), its main phase is the GPU upload only — then
    /// <paramref name="onAllMaterialsReady"/> resolves the materials (pure cache hits by then) and runs the
    /// final (cheap) assembly. Per-texture granularity caps a single stage at ONE upload (~5-10 ms) — the
    /// earlier per-MATERIAL jobs still spiked to ~50 ms when one material carried 6 big textures. The
    /// streamer's budget spreads the jobs across frames; the count-down gate fires exactly once, on main.
    /// </summary>
    private void EnqueueStagedSkeletalBuild(AssetLoader loader, AssetLoader.SkeletalModelParse parse,
        XonoticGodot.Game.Client.BackgroundAssetStreamer.Priority priority, string label, Action onAllMaterialsReady)
    {
        List<string> mats = AssetLoader.EffectiveMaterials(parse);
        Action finish = () =>
        {
            // Materials assemble from already-uploaded textures (cache hits) — sub-ms each.
            using (XonoticGodot.Game.Client.FrameProfiler.Scope("iqm.materials"))
                foreach (string m in mats)
                    loader.Assets.ResolveMaterial(m);
            onAllMaterialsReady();
        };

        var textures = new List<string>();
        foreach (string m in mats)
            foreach (string t in loader.Assets.EnumerateMaterialTextureNames(m))
                if (!textures.Contains(t))
                    textures.Add(t);
        if (_streamer is null || textures.Count == 0)
        {
            finish();
            return;
        }

        int remaining = textures.Count;
        foreach (string tex in textures)
        {
            string t = tex;
            _streamer.Request(
                () => { loader.Assets.PredecodeTexture(t); return t; },     // worker: VFS read + decode
                _ =>
                {
                    using (XonoticGodot.Game.Client.FrameProfiler.Scope("iqm.materials"))
                        loader.Assets.LoadTexture(t);                       // main: ONE GPU upload (or a cheap miss)
                    if (--remaining == 0)
                        finish();
                },
                priority,
                label: $"{label} tex {t}");
        }
    }

    /// <summary>The "model#skin" memo key used by <see cref="_unresolvableForcedModels"/> for a forced model.</summary>
    private static string ForcedKey(string model, int skin) => model + "#" + skin;

    /// <summary>
    /// Main-thread delivery of a streamed player-model parse (budgeted by the streamer — one heavy build per
    /// frame): validate the request is still current, build the Godot scene, and fill the placeholder shell via
    /// <see cref="PlayerModel.Setup"/>. A stale/failed resolve re-runs the attach through
    /// <see cref="ClientWorld.RebuildEntityModel"/> so the entity ends up exactly where the old synchronous
    /// path would have put it (the MD3/static fall-through, or a resolve of the changed model).
    /// </summary>
    private void DeliverPlayerModel(PlayerModel pm, Entity e, string model, int skin, AssetLoader.SkeletalModelParse? parse, bool forced)
    {
        if (_assets is null)
            return;
        // The shell was torn down while the parse was in flight (entity removed / world reset / model rebuilt).
        if (!GodotObject.IsInstanceValid(pm) || pm.IsQueuedForDeletion())
            return;
        if (e.IsFreed)
            return; // entity gone — OnEntityRemove tears the node (and shell) down

        // Model/skin changed while parsing: re-attach against the CURRENT model (issues a fresh resolve). This gate
        // keys on the entity's NETWORKED model, which is the right staleness check for the OWN-model resolve but
        // intentionally differs from a FORCED name — so it is skipped for forced deliveries (whose live-ness is the
        // pm-valid + e.IsFreed gates above; a mid-flight cl_forceplayermodels toggle is handled by RebuildEntityModel
        // re-running TryAttachForcedModel, which re-reads the cvars).
        if (!forced && (!string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase) || (int)e.Skin != skin))
        {
            _render.RebuildEntityModel(e);
            return;
        }

        if (parse is null)
        {
            // Not an IQM (or unreadable): remember (so the rebuild's resolve doesn't re-stream), then fall back —
            // own-model path for the live resolve, or the cached non-IQM forced node (FORCEMODEL parity) for a
            // forced one. The memo is populated BEFORE RebuildEntityModel to terminate the re-attach loop.
            ForgetSkeletal(model, skin, forced);
            _render.RebuildEntityModel(e);
            return;
        }

        AssetLoader.SkeletalModelParts? parts = _assets.BuildSkeletalModel(parse);
        if (parts is null)
        {
            ForgetSkeletal(model, skin, forced);
            _render.RebuildEntityModel(e);
            return;
        }

        pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
        if (!pm.Active)
        {
            // Parsed as IQM but no poseable skeleton — match the sync path's fall-through.
            ForgetSkeletal(model, skin, forced);
            _render.RebuildEntityModel(e);
            return;
        }

        // Tint immediately (team colormap / forcecolors) — the per-frame appearance pass keeps it fresh, but
        // seeding now avoids one untinted frame, mirroring TryAttachModel's eager seed.
        _render.SeedAppearance(e);
        XonoticGodot.Common.Diagnostics.Log.Trace(
            $"[stream] {(forced ? "forced " : "")}player model '{model}' (skin {skin}) built for ent {e.Index} — placeholder swapped");
    }

    /// <summary>
    /// Record that a streamed skeletal resolve settled as non-skeletal/failed, in the memo the matching resolver
    /// checks BEFORE re-streaming — so the <see cref="ClientWorld.RebuildEntityModel"/> re-attach terminates. For a
    /// forced model the key is name+skin specific (the forced name differs from the entity's own model); for the
    /// own-model resolve the key is the entity's model name (the existing Wave-1 scheme). Always invoked before
    /// RebuildEntityModel in every forced fallback branch (the loop-prevention invariant).
    /// </summary>
    private void ForgetSkeletal(string model, int skin, bool forced)
    {
        if (forced)
            _unresolvableForcedModels.Add(ForcedKey(model, skin));
        else
            _nonSkeletalPlayerModels.Add(model);
    }

    /// <summary>
    /// Build a FORCED player model (QC <c>cl_forcemyplayermodel</c> / <c>cl_forceplayermodels</c> swap) for
    /// <see cref="ClientWorld.ForcedModelResolver"/>. Mirrors <see cref="ResolvePlayerModel"/>: a skeletal forced
    /// model returns a gray placeholder <see cref="PlayerModel"/> shell immediately and streams the IQM parse+build
    /// through the same High-priority <see cref="BackgroundAssetStreamer"/> path (no synchronous build stall on a
    /// forced-model snapshot). Once the async resolve has settled a model as non-skeletal or a true miss, its
    /// "model#skin" key lives in <see cref="_unresolvableForcedModels"/>: the next attach (driven by
    /// <see cref="ClientWorld.RebuildEntityModel"/>) takes the memo branch and returns the cached non-IQM node via
    /// <see cref="AssetLoader.LoadModel"/> (FORCEMODEL parity — a forced MD3/DPM still wins over the entity's own
    /// model), or null on a genuine VFS miss (QC <c>fexists</c>-miss → the client keeps the entity's own model).
    /// </summary>
    private Node3D? BuildForcedPlayerModel(Entity e, string model, int skin)
    {
        if (_assets is null || string.IsNullOrEmpty(model))
            return null;
        // Already settled as non-skeletal/miss: don't re-stream (that re-attach loop is the highest risk). Return
        // the cached non-IQM forced node (FORCEMODEL parity) or null (true miss → entity's own model). LoadModel is
        // cached (AssetLoader._modelCache), so the rebuild terminates here without rebuilding.
        if (_unresolvableForcedModels.Contains(ForcedKey(model, skin)))
            return _assets.LoadModel(model, skin);
        // Otherwise stream the skeletal forced model exactly like the live player path: placeholder shell now,
        // off-thread parse + budgeted main build, DeliverPlayerModel (forced) fills the shell or records the memo.
        return StreamSkeletalInto(e, model, skin, $"forced_{System.IO.Path.GetFileNameWithoutExtension(model)}", forced: true);
    }

    /// <summary>
    /// Assemble the live CSQC appearance context (QC <c>CSQCPlayer_ModelAppearance_Apply</c> needs the local
    /// player + gametype) for <see cref="ClientWorld.AppearanceProvider"/>. Prefers the listen-server world (it
    /// holds authoritative team/gametype/teamplay + team count); a pure client falls back to the networked
    /// <see cref="ClientNet.LatestScoreInfo"/> (gametype/teamplay) with an unknown local team. Returns null only
    /// before the client is accepted (no force model/colors yet).
    /// </summary>
    private ClientWorld.AppearanceContext? BuildAppearanceContext()
    {
        if (_client is null || !_client.Accepted)
            return null;

        var ctx = new ClientWorld.AppearanceContext { LocalNetId = _client.LocalNetId };

        // QC cl_survival.qc colormap override (ForcePlayercolors_Skip): in Survival, once a status block has been
        // received (the local player has a role → MyStatus != 0, OR the round resolved and disclosed hunters), feed
        // the disclosed hunter set so ResolveForcedColormap repaints every known player green-prey / red-hunter.
        if (_client.LatestModeStatus is { Mode: XonoticGodot.Net.GametypeStatusBlock.Kind.Survival } surv
            && (surv.MyStatus != 0 || surv.HunterNetIds.Count > 0))
        {
            ctx.SurvivalActive = true;
            ctx.SurvivalHunterIds = surv.HunterNetIds;
        }

        // Listen server: authoritative gametype/teamplay/team-count + the local player's team.
        if (_serverWorld is not null)
        {
            ctx.Teamplay = _serverWorld.Teamplay is { IsTeamGame: true };
            ctx.TeamCount = _serverWorld.Teamplay?.TeamCount ?? 0;
            ctx.Is1v1 = string.Equals(_serverWorld.GameType?.NetName, "duel", StringComparison.OrdinalIgnoreCase);
            Player? me = LocalServerPlayer;
            if (me is not null)
                ctx.MyTeam = (int)me.Team;
            return ctx;
        }

        // Pure client: gametype/teamplay from the networked ScoreInfo block (team count + local team unknown here).
        if (_client.LatestScoreInfo is { } si)
        {
            ctx.Teamplay = si.Teamplay;
            ctx.Is1v1 = string.Equals(si.Gametype, "duel", StringComparison.OrdinalIgnoreCase);
            ctx.TeamCount = si.Teamplay ? 2 : 0; // best-effort: the 2-team force-color gate (the common case)
        }
        return ctx;
    }

    // =====================================================================================
    //  Map / world helpers
    // =====================================================================================

    private XonoticGodot.Formats.Bsp.BspData? TryLoadMapBsp(string map)
    {
        if (_assets is null || string.IsNullOrWhiteSpace(map))
            return null;
        foreach (string vpath in MapVPathCandidates(map))
        {
            if (!_assets.Vfs.Exists(vpath))
                continue;
            XonoticGodot.Formats.Bsp.BspData? bsp = _assets.ReadBsp(vpath);
            if (bsp is not null)
                return bsp;
        }
        return null;
    }

    /// <summary>
    /// Convert the BSP entity lump (a list of key/value dicts) into the <see cref="EntityDict"/> list the
    /// <see cref="GameWorld"/> constructor takes, so the server's spawn loop (QC the entity-lump →
    /// spawnfunc_CLASSNAME) mints the map's spawn points, jump-pads, items, doors, etc. Origin/angles are
    /// parsed into the struct's first-class fields (the world reads them there); every key (incl. origin/angles)
    /// is also kept in <see cref="EntityDict.Fields"/> for the generic spawnfunc field reads.
    /// </summary>
    private static System.Collections.Generic.List<EntityDict> BuildEntityDicts(XonoticGodot.Formats.Bsp.BspData bsp)
    {
        var list = new System.Collections.Generic.List<EntityDict>(bsp.Entities.Count);
        foreach (System.Collections.Generic.IReadOnlyDictionary<string, string> dict in bsp.Entities)
        {
            if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls))
                continue;
            var ed = new EntityDict
            {
                ClassName = cls,
                Origin = ParseVec(dict, "origin"),
                Angles = ParseVec(dict, "angles"),
            };
            foreach (System.Collections.Generic.KeyValuePair<string, string> kv in dict)
                ed.Fields[kv.Key] = kv.Value;
            list.Add(ed);
        }
        return list;
    }

    private static NVec3 ParseVec(System.Collections.Generic.IReadOnlyDictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s))
            return NVec3.Zero;
        string[] p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return NVec3.Zero;
        return new NVec3(ParseF(p[0]), ParseF(p[1]), ParseF(p[2]));
    }

    private static float ParseF(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;

    /// <summary>The VFS vpaths a configured map name is probed at (bare → maps/, +.bsp) — mirrors GameDemo.</summary>
    private static System.Collections.Generic.IEnumerable<string> MapVPathCandidates(string mapPath)
    {
        string p = mapPath.Replace('\\', '/').Trim();
        bool hasBsp = p.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase);
        bool underMaps = p.StartsWith("maps/", StringComparison.OrdinalIgnoreCase);
        yield return p;
        if (!hasBsp) yield return p + ".bsp";
        if (!underMaps)
        {
            yield return "maps/" + p;
            if (!hasBsp) yield return "maps/" + p + ".bsp";
        }
    }

    /// <summary>Copy a cvar from <paramref name="from"/> to <paramref name="to"/> only when it holds a non-zero
    /// numeric value (so an unset limit leaves the destination's cfg-loaded default in place).</summary>
    private static void CopyCvarIfSet(XonoticGodot.Engine.Simulation.CvarService from, ICvarService to, string name)
    {
        if (from.Has(name) && from.GetFloat(name) > 0f)
            to.Set(name, from.GetString(name));
    }

    /// <summary>Cvars the listen-server boot sequence (or the map's worldspawn) authors itself — a console override
    /// of these must NOT be backfilled across a map change, or it would clobber the new map's gravity, the chosen
    /// match limits, or the map name. The limits ride the campaign-guarded <see cref="CopyCvarIfSet"/> path above;
    /// sv_gravity is owned by each map's worldspawn. NOTE: sv_spectate is deliberately NOT here anymore — the boot
    /// no longer authors it (#44 kept Base's shipped 1), so excluding it would have silently reverted an operator's
    /// runtime override on every map change.</summary>
    private static readonly System.Collections.Generic.HashSet<string> BootAuthoredCvars = new(System.StringComparer.Ordinal)
    {
        "mapname", "sv_gravity", "timelimit", "fraglimit", "bot_number", "skill",
    };

    private static CollisionWorld BuildTestFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(
            new NVec3(-4096f, -4096f, -64f),
            new NVec3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    private void AddLight()
    {
        AddChild(new DirectionalLight3D { Name = "Sun", RotationDegrees = new GVec3(-50f, -30f, 0f), ShadowEnabled = true });

        // The map's real Xonotic skybox (DP R_LoadSkyBox semantics), falling back to the procedural sky when
        // the map declares none. Same path GameDemo.AddLighting uses; gated on the BSP the client loaded.
        Sky sky = XonoticGodot.Game.Loaders.SkyboxLoader.TryBuild(_bsp, _assets?.Assets)
                  ?? new Sky { SkyMaterial = new ProceduralSkyMaterial() };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.6f,

            // Tonemap stays LINEAR. The world (LightmapShader) and skin shaders output colour hand-tuned to
            // round-trip with Godot's Linear mapper, and the content's dynamic range is low (overbright tops out
            // ~lightmap_scale 2.0). A filmic curve (ACES/Filmic) crushes the mid-tones and shadows here — verified
            // on stormkeep — so it's left off. Switch to ToneMapper.Aces (TonemapWhite ~1.5) for a punchier,
            // less faithful "modern" grade if desired.
            TonemapMode = Godot.Environment.ToneMapper.Linear,

            // Bloom on genuinely-bright pixels only (light fixtures, lava, _glow pages, additive particles,
            // muzzle/explosion flashes). Threshold ~1.0 keeps ordinary lit walls from washing out — this is
            // the equivalent of the r_bloom post pass Darkplaces ships.
            GlowEnabled = true,
            GlowIntensity = 0.8f,
            GlowStrength = 1.0f,
            GlowBloom = 0.05f,
            GlowHdrThreshold = 1.0f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen,
        };

        // Map-declared fog (worldspawn "fog"), restoring the mapper's intended atmosphere + depth cue.
        XonoticGodot.Game.MapLoader.ApplyFog(env, _bsp);

        // Map-declared colour tint baseline (worldspawn "_map_tint"/"_scene_tint"); identity if unset. Live cvars
        // and the C# API can override it at runtime (XonoticGodot.Game.WorldTint).
        XonoticGodot.Game.WorldTint.ApplyWorldspawn(_bsp);

        _worldEnv = env; // kept so a pure client can swap in the real map's sky/fog/tint once its BSP loads (ApplyMapSky)
        AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = env });
    }

    /// <summary>
    /// Re-apply the real map's skybox, fog and colour-tint baseline to the live <see cref="WorldEnvironment"/>,
    /// replacing the procedural sky <see cref="AddLight"/> put up before the map was known. Called by
    /// <see cref="LoadClientMapFromServer"/> once a pure <c>--connect</c> client has loaded the server's BSP.
    /// No-op when the environment or BSP isn't available.
    /// </summary>
    private void ApplyMapSky()
    {
        if (_worldEnv is null || _bsp is null)
            return;
        Sky? sky = XonoticGodot.Game.Loaders.SkyboxLoader.TryBuild(_bsp, _assets?.Assets);
        if (sky is not null)
            _worldEnv.Sky = sky;
        XonoticGodot.Game.MapLoader.ApplyFog(_worldEnv, _bsp);
        XonoticGodot.Game.WorldTint.ApplyWorldspawn(_bsp);
    }

    /// <summary>
    /// A pure <c>--connect</c> client boots on a flat prediction floor (<see cref="BootClientFacade"/>) because it
    /// doesn't yet know the server's map. Once the handshake accept delivers the map name (+ gametype), load that
    /// BSP locally and wire it in three ways so the remote player sees and moves through the REAL world, not a void:
    /// <list type="number">
    ///   <item>swap the ambient trace world's flat floor for the map's collision, so the predicted local player
    ///         clips real geometry (fixes the walk-through-walls / <c>PREDICTION DESYNC</c> on a pure client);</item>
    ///   <item>attach the render worldmodel + PVS cull + particle collision (<see cref="AttachWorldRender"/>);</item>
    ///   <item>swap the procedural sky for the map's real skybox/fog/tint (<see cref="ApplyMapSky"/>).</item>
    /// </list>
    /// One-shot (<see cref="_clientMapLoaded"/>); inert on a listen server (its map is loaded in
    /// <see cref="StartListenServer"/>) and when the map can't be resolved in the VFS (stays on the flat floor).
    /// </summary>
    private void LoadClientMapFromServer(string mapName, string gametype)
    {
        if (_clientMapLoaded || _isListenServer || string.IsNullOrWhiteSpace(mapName))
            return;

        XonoticGodot.Formats.Bsp.BspData? bsp = TryLoadMapBsp(mapName);
        if (bsp is null)
        {
            GD.PrintErr($"[NetGame] client: server map '{mapName}' not found in the VFS — staying on the flat " +
                        "prediction floor (world will not render).");
            return;
        }
        _clientMapLoaded = true;
        _map = mapName;
        if (!string.IsNullOrWhiteSpace(gametype))
            _gametype = gametype;
        _bsp = bsp;
        // Drop the SAME gametype-conditional "*N" submodels the server dropped, so the client's render + collision
        // agree with authority (render and collision MUST match — GameDemo.cs:134/181).
        _droppedSubmodels = GameMapView.ComputeDroppedSubmodels(bsp, _gametype);

        // (1) Prediction/gameplay collision: replace the flat floor the carrier has been tracing against with the
        // real map collision. The ambient EngineServices keeps its entity table (the carrier lives there) + clock +
        // cvars + models; only the static brush world the trace consults changes. Safe between frames (single-thread).
        BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp, _droppedSubmodels);
        if (Api.Services is XonoticGodot.Engine.Simulation.EngineServices es)
        {
            es.SetCollisionWorld(built.World);
            es.Pvs = new XonoticGodot.Formats.Bsp.BspPvs(bsp);
            // Register the inline "*N" brush models into the client model catalog. NOTE: this is FORWARD-LOOKING —
            // no client code currently mirrors snapshot entities into the ambient facade table or calls SetModel
            // on it (the only facade entity is the prediction carrier), so predicted movement still doesn't clip
            // networked doors/plats today (the server reconcile corrects it). The registration is cheap + idempotent
            // and is here so that when a snapshot→facade mover mirror lands, a "*N" setmodel resolves its clip brushes.
            BspCollisionBuilder.RegisterSubmodels(built.Submodels, es.ModelsImpl);
        }

        // (2) Render the worldmodel + PVS entity-cull + particle/decal collision (the same path the listen server
        // runs in SetupRender), now that _bsp is set. Hand it the prediction world we just built so it doesn't
        // build the full brush world a SECOND time on this connect frame (both uses are main-thread — safe to share).
        AttachWorldRender(built.World);

        // (3) The map's real skybox / fog / colour tint, replacing the procedural sky AddLight put up pre-map.
        ApplyMapSky();

        // (4) The map-dependent HUD/ambience feeds SetupCameraAndHud/SetupMusic couldn't wire in _Ready (the map
        // name wasn't known yet on a pure client):
        // — radar minimap image + world bounds (QC mi_min/mi_max), so the image and the blips align;
        if (_radar is not null)
        {
            _radar.MapName = _map;
            if (bsp.Models.Length > 0)
            {
                _radar.MapMinXY = new Vector2(bsp.Models[0].Mins.X, bsp.Models[0].Mins.Y);
                _radar.MapMaxXY = new Vector2(bsp.Models[0].Maxs.X, bsp.Models[0].Maxs.Y);
            }
        }
        // — waypoint-sprite distance-fade ramp (QC waypointsprite_fadedistance = vlen(mi_scale));
        if (_waypointLayer is not null && bsp.Models.Length > 0)
        {
            NVec3 mn = bsp.Models[0].Mins, mx = bsp.Models[0].Maxs;
            _waypointLayer.MapSize = new NVec3(mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z).Length();
        }
        // — map music: SetupMusic ran with no map name, so resolve the cdtrack now. The MusicPlayer re-reads
        //   CdTrack each frame, so a late set starts the track cleanly. (trigger_music/target_music volumes stay
        //   listen-host-only — they are server entities and aren't networked.) Resolve order mirrors SetupMusic:
        //   the mapinfo cdtrack first, then — for legacy/Q3/Nexuiz-compat maps with no mapinfo — the worldspawn
        //   "music"/"noise" key (QC world.qc:970), parsed from the BSP we just loaded. Without the worldspawn
        //   fallback those maps played on the listen host but were silent on a pure client.
        if (_musicPlayer is not null && _vfs is not null)
        {
            string mapinfoPath = $"maps/{_map}.mapinfo";
            string cdTrack = "";
            if (_vfs.Exists(mapinfoPath))
            {
                try { cdTrack = ParseMapinfoCdTrack(_vfs.ReadText(mapinfoPath)); }
                catch { /* unreadable mapinfo — fall through to worldspawn */ }
            }
            if (string.IsNullOrEmpty(cdTrack) && bsp.Entities.Count > 0)
            {
                IReadOnlyDictionary<string, string> worldspawn = bsp.Entities[0];
                if (worldspawn.TryGetValue("music", out string? wsMusic) && !string.IsNullOrEmpty(wsMusic))
                    cdTrack = wsMusic;
                else if (worldspawn.TryGetValue("noise", out string? wsNoise) && !string.IsNullOrEmpty(wsNoise))
                    cdTrack = wsNoise;
            }
            string resolvedTrack = MusicPlayer.ResolveMusicPath(cdTrack);
            if (!string.IsNullOrEmpty(resolvedTrack))
            {
                _musicPlayer.CdTrack = resolvedTrack;
                GD.Print($"[NetGame] map music: '{resolvedTrack}' (from mapinfo, client map load)");
            }
        }
        // — warpzone portal window/disc renderers (SetupCameraAndHud skipped them — no _mapRoot yet). The zone
        //   transforms aren't networked, so on a pure client these degrade to the dark-mirror placeholder rather
        //   than the live see-through render — but the portal FRAMES render instead of nothing. _camera is live
        //   (SetupCameraAndHud ran in _Ready; this fires from _Process on the first accept).
        if (_mapRoot is not null && _camera is not null && _portalRenderer is null)
        {
            _portalRenderer = new XonoticGodot.Game.Client.PortalRenderer { Name = "PortalRenderer" };
            AddChild(_portalRenderer);
            _portalRenderer.Setup(_mapRoot, _camera);

            _portalDiscRenderer = new XonoticGodot.Game.Client.PortalDiscRenderer { Name = "PortalDiscRenderer" };
            AddChild(_portalDiscRenderer);
            _portalDiscRenderer.Setup(_camera, (path, skin) => _assets?.LoadModel(path, skin));
        }

        GD.Print($"[NetGame] client loaded map '{_map}' ({_gametype}) for render + prediction collision.");
    }

    // (the old hardcoded BotName table moved into BotPopulation's bots.txt fallback — T39)

    // =====================================================================================
    //  Address parsing
    // =====================================================================================

    /// <summary>Parse <c>host</c> or <c>host:port</c> into (host, port), defaulting the port. IPv6 in [..] is
    /// left to the transport; we only split on the LAST colon when it's followed by a port number.</summary>
    public static (string Host, int Port) ParseAddress(string address, int defaultPort)
    {
        string a = (address ?? "").Trim();
        if (a.Length == 0)
            return ("127.0.0.1", defaultPort);
        int colon = a.LastIndexOf(':');
        if (colon > 0 && colon < a.Length - 1
            && int.TryParse(a[(colon + 1)..], out int port) && port is > 0 and <= 65535)
            return (a[..colon], port);
        return (a, defaultPort);
    }

    /// <summary>A no-op prediction step used only if the carrier couldn't be spawned (no ambient world).</summary>
    private sealed class NullMovementStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) { }
    }
}

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Md3;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// The client-side rendering hub — the Godot successor to CSQC's <c>CSQC_Ent_Update</c> /
/// <c>CSQC_Event</c> / temp-entity dispatch. It owns the four render subsystems
/// (<see cref="EffectSystem"/>, <see cref="ProjectileRenderer"/>, <see cref="ModelAnimator"/>s, the local
/// <see cref="ViewModel"/>) and exposes a tiny, dependency-free public API that the networking layer
/// (<c>game/net/ClientNet</c>, owned by another agent) calls as packets arrive:
///
///   • <see cref="OnEffect"/>      — a networked particle effect fired (Send_Effect / EFF_NET_* decode).
///   • <see cref="OnEntityUpdate"/>— an entity entered/changed in the client's snapshot (CSQC ent update).
///   • <see cref="OnEntityRemove"/>— an entity left the snapshot / was freed.
///   • <see cref="OnSound"/>       — a positional sound event (QC sound() on an entity/point).
///   • <see cref="OnMuzzleFlash"/> — the local player fired (drives the view-model flash).
///
/// These are plain methods (and matching <c>Action</c> properties for code that prefers delegates), so the
/// net layer can wire them with no shared interface beyond <see cref="Entity"/>/<see cref="Effect"/>, which
/// both sides already reference. The class also installs an <see cref="IEffectSink"/> so that any server-side
/// or local <c>EffectEmitter.Emit</c> in this process is mirrored straight onto the renderer — which is what
/// makes the demo show effects without a real network round-trip.
///
/// Each frame it advances the projectile follow/interp and any model animators it owns; the EffectSystem and
/// per-projectile trails self-advance as Godot particle nodes.
/// </summary>
public partial class ClientWorld : Node3D
{
    /// <summary>Particle effect renderer (EFFECT_* catalog → Godot particles).</summary>
    public EffectSystem Effects { get; private set; } = null!;

    /// <summary>Networked projectile visual renderer.</summary>
    public ProjectileRenderer Projectiles { get; private set; } = null!;

    /// <summary>Networked nade-orb (heal/ammo/entrap/veil/darkness) effect renderer. Created and owned by
    /// <see cref="VortexArena.Game.Net.NetGame"/> (next to the ProjectileRenderer) and assigned here, so the
    /// entity-stream consumer (<c>ClientEntityView</c>) and the per-frame view-effects feed share the one
    /// instance. Null until NetGame wires it.</summary>
    public NadeOrbRenderer NadeOrbs { get; set; } = null!;

    /// <summary>The shared Draw_CylindricLine primitive (cross-ribbon segment pool) injected into every beam/rope/line
    /// renderer (Lasers, PortoPreview, the hook rope) so they all draw through one pooled node.</summary>
    public CylindricLine CylindricLines { get; private set; } = null!;

    /// <summary>Lightning-arc / beam renderer (te_csqc_lightningarc: electro combo, Golem zaps, Tesla arcs).</summary>
    public BeamRenderer Beams { get; private set; } = null!;

    /// <summary>misc_laser beam renderer (T48 — the Draw_Laser successor; ambient-facade scan).</summary>
    public LaserRenderer Lasers { get; private set; } = null!;

    /// <summary>dynlight realtime-light renderer (the DP engine-driven light; ambient-facade scan).</summary>
    public DynamicLightRenderer DynamicLights { get; private set; } = null!;

    /// <summary>func_pointparticles / func_sparks persistent emitters (T48; ambient-facade scan).</summary>
    public MapParticleEmitters MapEmitters { get; private set; } = null!;

    /// <summary>func_rain / func_snow weather volumes (T48; ambient-facade scan).</summary>
    public WeatherSystem Weather { get; private set; } = null!;

    /// <summary>The Port-O-Launch aim-trajectory preview (Porto_Draw red/blue reflecting polyline). Idle unless
    /// the local player holds the porto in the non-default combined-shot mode; the host wires its providers.</summary>
    public PortoTrajectoryPreview PortoPreview { get; private set; } = null!;

    /// <summary>The grappling-hook rope renderer (QC <c>Draw_GrapplingHook</c>): draws the segmented hook beam from
    /// the firing player to the hook anchor, leasing its cross-ribbon segments from the shared
    /// <see cref="CylindricLines"/> pool (it idles on a null <c>Lines</c>, so the host MUST inject the shared node —
    /// mirroring <see cref="LaserRenderer"/>/<see cref="PortoPreview"/>). Self-driving off the ambient entity facade,
    /// so LIVE on a listen host / demo and IDLE on a pure --connect client (the established ambient-renderer seam).</summary>
    public HookRopeRenderer HookRope { get; private set; } = null!;

    /// <summary>The local player's first-person weapon view-model (optional; set by the host).</summary>
    public ViewModel? ViewModel { get; set; }

    /// <summary>The listen-server host's own Player entity (set by NetGame). When <see cref="SuppressOwnFireEffects"/>
    /// is on, the in-process effect mirror drops effects flagged <c>Except = </c> this player (their own muzzle
    /// flash), so it isn't doubled with the locally-predicted one — remotes already get it via the server's
    /// per-peer except pass. Null on a pure client (no in-process emission to mirror).</summary>
    public VortexArena.Common.Gameplay.Player? LocalHostPlayer { get; set; }

    /// <summary>cl_predictfire: when true, suppress the local host player's own in-process fire effects (they are
    /// predicted locally). False = render them (the host sees the networked copy, e.g. cl_predictfire 0).</summary>
    public bool SuppressOwnFireEffects { get; set; }

    /// <summary>Per-entity render nodes for non-projectile networked entities (players/monsters/items).</summary>
    private readonly Dictionary<int, EntityNode> _entityNodes = new();

    /// <summary>(§12.8) The map's compiled PVS, wired by the host after the BSP loads (null on a non-BSP/demo
    /// scene → entity PVS culling is inert). Drives <see cref="DriveEntityNodes"/>: DP-faithful render culling
    /// of remote entities whose bounds fall outside the camera's potentially-visible set. When the cvar is off,
    /// the same loop simply marks every node visible each frame (no separate disable-reset bookkeeping needed).</summary>
    public VortexArena.Formats.Bsp.BspPvs? Pvs { get; set; }

    /// <summary>The client-world tracer for corpse ragdolls (worldspawn brushes, no entities, no gate — the
    /// same pattern as the particle tracer; NEVER the ambient Api.Trace, which on a listen host is the SERVER
    /// world under the tick lock). Wired by the host from the same collision build the particles use.</summary>
    public VortexArena.Engine.Collision.TraceService? RagdollTrace { get; private set; }

    /// <summary>Hand the client-side collision world to the systems that trace against it client-only.</summary>
    public void SetClientCollision(VortexArena.Engine.Collision.CollisionWorld world)
        => RagdollTrace = new VortexArena.Engine.Collision.TraceService(world);

    /// <summary>True on a --headless host (dummy renderer): visual-only CPU work like ragdolls must skip.</summary>
    private static readonly bool HeadlessHost = DisplayServer.GetName() == "headless";

    /// <summary>Per-entity model animators (players/monsters/vehicles with MD3 anim).</summary>
    private readonly Dictionary<int, ModelAnimator> _animators = new();

    /// <summary>Per-player skeletal models (IQM + CPU upper/lower split + view-pitch aim), driven each frame.</summary>
    private readonly Dictionary<int, PlayerModel> _playerModels = new();

    /// <summary>Per-player floating nameplates (ent_cs): a billboarded health/id label above the head.</summary>
    private readonly Dictionary<int, Label3D> _nameplates = new();

    /// <summary>
    /// Crosshair-chase local-body fade targets keyed by local net id (QC <c>crosshair_chase_playeralpha</c>): the
    /// third-person crosshair-chase view fades the LOCAL player's own body so it doesn't block the aim. Maps a net
    /// id to the desired RENDER alpha in (0,1] (1 = opaque; caller passes 1 − crosshair_chase_playeralpha, e.g. the
    /// 0.25 default → 0.75). Populated/cleared by <see cref="SetLocalBodyAlpha"/>; the per-frame model pass eases
    /// the matching <see cref="PlayerModel"/> toward it. Empty on a non-chase frame, so that path pays nothing.
    /// </summary>
    private readonly Dictionary<int, float> _bodyAlphaTarget = new();

    /// <summary>The currently-EASED body-fade render alpha per local net id (1 = opaque), driven toward
    /// <see cref="_bodyAlphaTarget"/> each frame so the fade-in/out is smooth (and so a cleared target eases back to
    /// opaque before the entry is dropped). Removed once the model is opaque again with no live target.</summary>
    private readonly Dictionary<int, float> _bodyAlphaCurrent = new();
    private readonly List<int> _bodyAlphaScratch = new(); // reused: ids to drop after they reach opaque

    /// <summary>Per-entity vehicle visual drivers (rotors/barrels/engine sound/gibs/heal-beam) for vehicle entities.</summary>
    private readonly Dictionary<int, VehicleVisuals> _vehicles = new();

    /// <summary>
    /// Owns the cosmetic add-on models that hang off a networked entity each frame (the QC
    /// <c>csqcmodel_hooks.qc</c> add-on layer): the ice block on a Frozen player, the held <c>buff_*</c> glow, and
    /// the nade spawn-loc marker. It networks nothing new — it reads the already-decoded StatusEffects blob + the
    /// standard entity delta (Origin/Angles/Effects/EF_STARDUST) and attaches/updates/removes the cosmetic child
    /// nodes off the supplied attach root. Driven from the per-frame entity + player-model passes; keyed by
    /// entity index for teardown on removal.
    /// </summary>
    private CosmeticModelLayer _cosmetics = null!;

    /// <summary>Entity indices whose animation frame is networked (CSQCMODEL_AUTOUPDATE): their
    /// <see cref="ModelAnimator"/> follows <see cref="Entity.Frame"/> directly instead of the local
    /// movement-derived clip heuristic. Populated by <see cref="OnEntityUpdate(Entity,bool)"/>.</summary>
    private readonly HashSet<int> _frameDriven = new();

    /// <summary>
    /// Per-entity CSQC-model render state (the C# successor to the per-edict CSQCModel fields the client kept in
    /// <c>csqcmodel_hooks.qc</c>): the persistent effect-light + jetpack-loop state, the last-seen colormap, and
    /// the client-OBSERVED death timestamp (the wire doesn't carry the server <c>death_time</c>, so the
    /// <c>cl_deathglow</c> fade starts from when this client first saw the model dead). Driven each frame in
    /// <see cref="DriveCsqcModelHooks"/>; torn down in <see cref="OnEntityRemove(int,NVec3,string?)"/>.
    /// </summary>
    private sealed class CsqcState
    {
        public readonly CsqcModelEffects.State Effects = new();
        public bool WasDead;          // last frame's dead test (to capture the death instant)
        public float DeathTime;       // client-observed time the model went dead (cl_deathglow fade origin)
        public bool IsPlayerModel;    // a skeletal/MD3 player model (gets the appearance/deathglow pass)
        public ItemDespawnFx? Despawn; // loot despawn animation (lazily created when ITS_EXPIRING first seen)
        public bool ItemFaded;        // a despawn/ghost transparency is currently applied (reset to opaque on the way back)
        public NVec3 TrailTail;       // Quake-space tail of the last emitted CSQC-model trail segment (EF_BRIGHTFIELD / jetpack MF_ROCKET) — csqcmodel_hooks.qc trailparticles continuation
        public bool HasTrailTail;     // false until the first frame a trail is active, so the first segment starts at the model origin (no spurious long streak on spawn)
        public float ItemFadeApplied; // last transparency pushed (§11 R9 change gate; 0 = opaque, the node default)
        public int ItemFadeMeshCount; // mesh-list size at last push (re-push after a cache rebuild)
        // The ONE applied-tint record for the item paint state machine (ApplyItemTint): what spec is currently
        // painted, and on which mesh list (count + first id — a same-count placeholder→real swap must repaint;
        // the ModelTint.TintCache pattern). All three render channels (instance uniforms, surface overrides,
        // whole-mesh MaterialOverride) are derived from the spec by ApplyItemTint alone — no split gates.
        public ItemTintSpec ItemTintSpecApplied;
        public bool ItemTintSpecValid;
        public int ItemTintSpecMeshCount;
        public ulong ItemTintSpecFirstMeshId;
        /// <summary>PER-ENTITY dark-ghost flat (whole-mesh MaterialOverride). Never shared across entities:
        /// CsqcModelEffects' render-flag setters mutate any BaseMaterial3D MaterialOverride in place (additive/
        /// fullbright/nodepthtest), so a shared cached flat would be corrupted for every ghost at once.</summary>
        public StandardMaterial3D? ItemFlatMat;
        // Resolved resting-paint cache: TryGetItemDuotone's output keyed on its raw inputs, so the per-frame
        // resting pass costs two compares instead of a cvar read + registry probe + palette/HSV math per item.
        public bool PaintKeyValid;
        public string PaintKeyModel = "";
        public int PaintKeyCmap;
        public int PaintKeyWeapon = -1;
        public bool PaintHas;
        public ItemPaint PaintCached;
    }
    private readonly Dictionary<int, CsqcState> _csqc = new();

    /// <summary>Pool of positional audio players reused for one-shot sound events.</summary>
    private readonly List<AudioStreamPlayer3D> _audioPool = new();

    /// <summary>
    /// Active one-shot players on SINGLE (positive) channels keyed by (emitter net id, channel). A new sound
    /// on the same key stops the previous player before starting — DP's CH_*_SINGLE replacement semantic.
    /// Auto/negative channels never key here (they always stack via the pool).
    /// </summary>
    private readonly Dictionary<(int netId, int channel), AudioStreamPlayer3D> _singleChannelPlayers = new();

    /// <summary>
    /// Every currently-playing one-shot, driven each frame by <see cref="DriveOneShots"/>: it (a) FOLLOWS its
    /// emitter when <see cref="ActiveOneShot.NetId"/> &gt; 0 (DP attaches a sound to the entity so it tracks the
    /// moving emitter — without this a gunshot fired while running is stranded at the firing point), and (b)
    /// re-applies the DP distance attenuation (<see cref="SetSpatialVolume"/>) relative to the listener, so a
    /// fixed-point impact gets quieter/louder as YOU move toward or away from it. Entries are pruned when the
    /// player stops or is recycled. (Looping sounds carry the same data on <see cref="LoopingSound"/>.)
    /// </summary>
    private readonly List<ActiveOneShot> _activeOneShots = new();

    /// <summary>A live one-shot player + the data needed to re-spatialize it each frame.</summary>
    private struct ActiveOneShot
    {
        public AudioStreamPlayer3D Player;
        public int NetId;          // emitter net id to follow (0 = a fixed world point, e.g. an impact/explosion)
        public float BaseVolume;   // pre-attenuation volume (QC vol); the DP falloff scales this each frame
        public float Attenuation;  // QC ATTEN_* — 0 = heard everywhere (no spatialization)
    }

    /// <summary>
    /// Persistent LOOPING positional sounds keyed by (emitter net id, channel) — DP's entity+channel sound
    /// model (the Arc beam loop, vehicle engines). Distinct from the one-shot <see cref="_audioPool"/>: a loop
    /// follows its emitter each frame (<see cref="EntityOriginResolver"/>), is replaced by a new loop on the
    /// same key, and ends on <see cref="OnStopSound"/> / the emitter leaving the snapshot / a keepalive lapse.
    /// <see cref="OnLoopingSound"/> populates it.
    /// </summary>
    private readonly Dictionary<(int netId, int channel), LoopingSound> _loopingSounds = new();
    private readonly List<(int netId, int channel)> _loopScratch = new(); // reused: keys to drop this frame

    /// <summary>One persistent looping positional source (Arc beam / vehicle engine) keyed in <see cref="_loopingSounds"/>.</summary>
    private sealed class LoopingSound
    {
        public AudioStreamPlayer3D Player = null!;
        public string Sample = "";   // the resolved sample currently looping (idempotency check on re-emit)
        public float SilentTime;     // seconds since the last keepalive refresh (auto-stop past the timeout)
        public bool Alive = true;    // false once stopped, so the Finished re-play handler goes inert
        public float BaseVolume = 1f; // pre-attenuation volume; the DP falloff scales this each frame
        public float Attenuation = 1f; // QC ATTEN_* for the distance falloff
    }

    // Darkplaces distance-attenuation parameters (snd_main.c SND_Spatialize), read LIVE from the snd_* cvars each
    // frame (ClientSettings registers the defaults) so they're tunable at runtime. Godot's own attenuation is
    // disabled; we reproduce DP's curve in SetSpatialVolume. Godot units are 1:1 with Quake units (Coords swap).
    // gain = (exponent==0 ? 1 : (1 - min(1, dist*atten/radius))^exponent) * (decibel==0 ? 1 : 0.1^(0.1*decibel*f)).
    // Defaults below mirror Xonotic's shipped method 1 (radius 2400 / exponent 4) — a steep tail so distant
    // sounds go quiet — vs the Quake default (1200 / exponent 1) which is a too-flat linear ramp.
    private float _sndRadius = 2400f;     // snd_soundradius
    private float _sndExponent = 4f;      // snd_attenuation_exponent
    private float _sndDecibel = 0f;       // snd_attenuation_decibel

    /// <summary>Last known listener (camera) position in Godot space; the fallback when no camera is available
    /// this frame so a transient null doesn't silence or mis-place sounds.</summary>
    private Godot.Vector3 _lastListener;

    /// <summary>A looping sound auto-stops if it isn't refreshed (re-emitted) for this long — the safety net for an
    /// emitter that stops without an explicit stop (e.g. the player switches off the Arc mid-fire, so the server
    /// stops re-emitting the loop but never sends a stop). Generous vs the per-tick/per-frame re-emit cadence.</summary>
    private const float LoopKeepaliveTimeout = 0.5f;

    /// <summary>
    /// Resolves an <see cref="Entity.ModelIndex"/> / model name to a parsed <see cref="Md3Data"/> for the
    /// renderer. The host sets this (it knows the asset/precache table); when null, entities render as a
    /// placeholder box. Keeping it a delegate avoids a hard dependency on the asset pipeline here.
    /// </summary>
    public Func<Entity, Md3Data?>? ModelResolver { get; set; }

    /// <summary>
    /// The material facade (host-set to <c>AssetLoader.Assets</c>) used to texture models built from a
    /// <see cref="ModelResolver"/> result — the <see cref="ModelAnimator"/> morph, the single-frame
    /// <see cref="ModelLoader.BuildModel"/> snapshot, and vehicle bodies. Null → those surfaces render untextured.
    /// Also feeds the <see cref="EffectSystem"/> its particlefont atlas + effectinfo.txt loaders (so explosions,
    /// impacts and decals draw the real Xonotic sprites), wired whether this is set before or after _Ready.
    /// </summary>
    public AssetSystem? Assets
    {
        get => _assets;
        set { _assets = value; WireEffectAssets(); }
    }
    private AssetSystem? _assets;

    /// <summary>Push the asset-backed texture/text loaders into the effect system so it can build the
    /// particlefont atlas and read effectinfo.txt from the mounted VFS. No-op until both Effects and Assets
    /// exist (the host sets Assets before AddChild, so this also runs from _Ready once Effects is created).</summary>
    private void WireEffectAssets()
    {
        if (Effects is null || _assets is null)
            return;
        Effects.TextureLoader = _assets.LoadTexture;
        Effects.VfsTextLoader = v =>
        {
            try { return _assets.Vfs.ReadText(v); }
            catch { return null; }
        };
        // The laser beam texture (particles/laserbeam.tga) resolves through the same asset pipeline; the
        // host may set Assets before OR after _Ready, so wire it from both sites (null until _Ready ran).
        if (Lasers is not null)
            Lasers.TextureLoader = _assets.LoadTexture;
    }

    /// <summary>
    /// Format-agnostic fallback model factory: builds a fully-textured, animated scene node for a non-player
    /// world entity of ANY model format (IQM/DPM/MD3) straight from the asset pipeline
    /// (<c>AssetLoader.LoadModel</c>). Tried after <see cref="ModelResolver"/> (the MD3-only, server-frame-driven
    /// path) returns null, so IQM/DPM props/items/monsters render their real model instead of a placeholder box.
    /// Host-wired; null → those entities fall back to the placeholder.
    /// </summary>
    public Func<Entity, Node3D?>? EntityModelFactory { get; set; }

    /// <summary>
    /// Host-wired raw-MD3 reader for the cosmetic add-on layer (<c>AssetLoader.LoadMd3</c>, cached). Used by the
    /// freezetag ice block / buff-carrier glow (<see cref="AttachedCosmeticModel"/>) to parse their <c>.md3</c>
    /// files. Null in headless/teardown → no cosmetic models resolve.
    /// </summary>
    public Func<string, Md3Data?>? CosmeticMd3Loader { get; set; }

    /// <summary>
    /// Host-wired format-agnostic model reader for the cosmetic add-on layer (<c>AssetLoader.LoadModel</c>,
    /// cached) — the IQM/DPM path for a non-MD3 cosmetic. Null in headless/teardown → those cosmetics don't draw.
    /// </summary>
    public Func<string, int, Node3D?>? CosmeticModelLoader { get; set; }

    /// <summary>
    /// Resolves a player entity to a skeletal <see cref="PlayerModel"/> (an IQM with the CPU upper/lower-body
    /// split + view-pitch aim). Tried before <see cref="ModelResolver"/>: when it returns non-null the entity
    /// renders as a posed skeleton; null falls through to the MD3 morph path / placeholder. The host wires it
    /// to the asset pipeline (<c>AssetLoader.LoadSkeletalModel</c>); null = no skeletal players.
    /// </summary>
    public Func<Entity, PlayerModel?>? PlayerModelResolver { get; set; }

    /// <summary>
    /// Resolves a sound sample path (QC <c>sound_str</c>, e.g. "weapons/rocket_impact") to a loadable Godot
    /// audio resource path (e.g. "res://sound/weapons/rocket_impact.ogg"). Host-provided; defaults to a
    /// res://sound/&lt;sample&gt;.ogg convention. Returning null/blank skips playback.
    /// </summary>
    public Func<string, string?>? SoundResolver { get; set; }

    /// <summary>
    /// Loads a sound sample straight to a decoded <see cref="AudioStream"/> from the mounted asset VFS
    /// (host-set to <c>AssetLoader.LoadSound</c>). This is the primary path — it reads the real Xonotic
    /// <c>sound/</c> tree out of the mounted content packs. When null (or it returns null) the code falls
    /// back to the <see cref="SoundResolver"/> <c>res://</c> path convention. Shared with the projectile
    /// and vehicle renderers so every positional cue resolves the same way.
    /// </summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>
    /// Resolves a networked entity's CURRENT Quake-space origin by its net id, so a LOOPING positional sound
    /// (Arc beam, vehicle engine) follows its emitter each frame (DP channel sounds track the entity). Host-wired
    /// to the net layer (local player → predicted origin; remotes → interpolated pose). Returns null when the id
    /// is unknown — the loop then holds its last position. Only looping sounds use it; one-shots fire at a fixed point.
    /// </summary>
    public Func<int, NVec3?>? EntityOriginResolver { get; set; }

    /// <summary>
    /// The client-side appearance context the CSQC <c>CSQCPlayer_ModelAppearance_Apply</c> FORCEMODEL/FORCECOLORS
    /// branch needs (csqcmodel_hooks.qc:141-327): which net id is the local player, the local player's team +
    /// gametype flags, and the local player number. Host-set (NetGame reads it from <see cref="ClientNet"/>);
    /// when null the force-model/force-color overrides are inert (the entity keeps its own model + networked
    /// colormap, the pre-T58 behavior). The fields mirror the QC globals of the same intent.
    /// </summary>
    public sealed class AppearanceContext
    {
        public int LocalNetId;       // QC: the local player's entnum (islocalplayer = entnum == this)
        public int MyTeam;           // QC myteam (1..4 in team modes; NUM_SPECTATOR=5 spectating; 0 none)
        public bool Teamplay;        // QC teamplay
        public bool Is1v1;           // QC gametype.m_1v1 (Duel)
        public int TeamCount;        // QC team_count (2 for the 2-team force-color gate)
        public int PlayerLocalNum;   // QC player_localnum (the FFA forced colormap = player_localnum+1)

        // QC cl_survival.qc NET_HANDLE(ENT_CLIENT_SURVIVALSTATUSES) + MUTATOR_HOOKFUNCTION(cl_surv,
        // ForcePlayercolors_Skip, CBC_ORDER_LAST): in Survival, once the client has received a status block (round
        // live/over), EVERY known player is recolored to the hardcoded survival palette — green prey / red hunter
        // — overriding scoreboard + player-model colors AND any cl_forceplayercolors (the hook runs LAST).
        public bool SurvivalActive;  // a Survival status block has been received (a round is live or just resolved)
        public System.Collections.Generic.HashSet<int>? SurvivalHunterIds; // net ids the client knows are hunters
    }

    // QC survival.qh: hardcoded survival colormap palette indices (the colormap is 1024 + index).
    private const int SurvColorPrey = 51;   // green
    private const int SurvColorHunter = 68; // red

    /// <summary>Host-set provider for the live <see cref="AppearanceContext"/> (read each frame). Null = no force
    /// model/colors (overrides inert). NetGame wires it; a pure demo leaves it null.</summary>
    public Func<AppearanceContext?>? AppearanceProvider { get; set; }

    /// <summary>[W14b LI3] Host-set accessor for the current SERVER clock time (<see cref="ClientNet.LatestServerTime"/>)
    /// — the clock the networked <c>Entity.AnimActionTime</c> (the upper-body action start) is stamped on, so the
    /// torso-action overlay computes the right play phase (<c>now − start</c>). NetGame wires it; null (a pure demo /
    /// the --skeleton-smoke harness) disables the action overlay (PlayerModel.Pose falls back to the static aim pose),
    /// keeping those paths bit-identical to before this wave.</summary>
    public Func<float>? ServerTimeProvider { get; set; }

    /// <summary>
    /// Resolve a FORCED player model (QC <c>cl_forcemyplayermodel</c> / the <c>cl_forceplayermodels</c> →
    /// <c>_cl_playermodel</c> swap) to a render node for an entity: given the entity, the forced model NAME + skin,
    /// build the skeletal/MD3 node the same way <see cref="PlayerModelResolver"/>/<see cref="ModelResolver"/> would.
    /// The entity is passed so the async (streamed) build can validate staleness and re-attach via
    /// <see cref="RebuildEntityModel"/> when the forced parse settles (a forced skeletal model returns a placeholder
    /// shell immediately, exactly like <see cref="PlayerModelResolver"/>). Host-set (NetGame); null → no force-model
    /// swap (the entity keeps its own networked model). Returns null when the forced model can't be built (QC
    /// <c>fexists</c>-miss → keep own).
    /// </summary>
    public Func<Entity, string, int, Node3D?>? ForcedModelResolver { get; set; }

    // ---- delegate-style hooks (for net code that prefers Action wiring) ----
    public Action<string, NVec3, NVec3, int>? EffectHook;
    public Action<Entity>? EntityUpdateHook;
    public Action<Entity>? EntityRemoveHook;
    public Action<string, NVec3>? SoundHook;

    // =================================================================================================
    //  Setup
    // =================================================================================================

    public override void _Ready()
    {
        Effects = new EffectSystem { Name = "Effects" };
        AddChild(Effects);
        // The host set Assets before AddChild (so before this _Ready), when Effects was still null; now that it
        // exists, push the particlefont/effectinfo loaders into it so the real sprites/decals are used.
        WireEffectAssets();

        // The cosmetic add-on layer (frozen ice block, held buff_* glow, nade spawn-loc marker). It builds its
        // models through the asset pipeline, which the host may set before OR after _Ready — so hand it a late-
        // bound accessor (() => _assets) rather than the current (possibly-null) value.
        _cosmetics = new CosmeticModelLayer(
            () => _assets,
            path => CosmeticMd3Loader?.Invoke(path),
            (path, skin) => CosmeticModelLoader?.Invoke(path, skin));

        // Casing bounce sounds (Base Casing_Touch: brass*/casings* on touch at speed): route them through the
        // positional sound path at VOL_BASE on CH_SHOTS with ATTEN_LARGE (the DP atten value 1 → quieter/closer
        // falloff, since gain = (1 - min(1, dist*atten/radius))^exp). Matches sound(this, CH_SHOTS, s, VOL_BASE,
        // ATTEN_LARGE) in Casing_Touch.
        Effects.Casings.SoundHook = (sample, origin) => OnSound(sample, origin, 0.7f, 1f, /*CH_SHOTS auto*/ -4);

        Projectiles = new ProjectileRenderer { Name = "Projectiles", Effects = Effects };
        // Share the client's sound resolution so projectile fly-loops (rocket/electro/fireball) resolve the
        // same way positional sounds do (QC loopsound on the CSQC projectile).
        Projectiles.SoundResolver = ResolveSound;
        // Share the VFS audio loader so projectile fly-loops resolve from the mounted content packs too
        // (late-bound: the host sets AudioLoader after _Ready; ProjectileRenderer keeps the res:// fallback).
        Projectiles.AudioLoader = s => AudioLoader?.Invoke(s);
        AddChild(Projectiles);

        // The one shared Draw_CylindricLine primitive (cross-ribbon segment pool). Every beam/rope/line renderer
        // leases its segments from this single node, so the host MUST inject it into each consumer below — without
        // it Lasers/PortoPreview/the hook rope idle (they gate on a null Lines). Added first so the consumers can
        // reference it as they're built.
        CylindricLines = new CylindricLine { Name = "CylindricLines" };
        AddChild(CylindricLines);

        Beams = new BeamRenderer { Name = "Beams" };
        AddChild(Beams);
        // Let the effect system route beam-class effects (lightning/arc/laser) to the real beam renderer
        // instead of a particle burst (te_csqc_lightningarc).
        Effects.Beams = Beams;

        // T48 map-entity client renderers: misc_laser beams, func_pointparticles/func_sparks continuous
        // emitters, func_rain/func_snow weather. All self-driving ambient-facade scanners (the
        // TriggerTouch.Predict*Ambient pattern) — live on the listen-server/demo paths; a pure --connect
        // client has no entity facade (or BSP) yet, so they idle there (the established seam).
        Lasers = new LaserRenderer { Name = "Lasers", Effects = Effects, Lines = CylindricLines };
        if (_assets is not null)
            Lasers.TextureLoader = _assets.LoadTexture;
        AddChild(Lasers);
        // dynlight realtime light — the consumer DarkPlaces drives from the engine reading light_lev/color.
        DynamicLights = new DynamicLightRenderer { Name = "DynamicLights" };
        AddChild(DynamicLights);
        MapEmitters = new MapParticleEmitters { Name = "MapEmitters", Effects = Effects };
        AddChild(MapEmitters);

        // Spawn-point idle glow (CSQC Spawn_Draw, gated by cl_spawn_point_particles).
        AddChild(new SpawnPointParticles { Name = "SpawnPointFx", Effects = Effects });
        Weather = new WeatherSystem { Name = "Weather", ViewOriginProvider = () => ViewOrigin() };
        AddChild(Weather);

        // Porto aim-trajectory preview (Porto_Draw). Self-driving; idle until the host wires its providers and
        // the local player holds the porto in the non-default combined-shot mode (g_balance_porto_secondary 0).
        PortoPreview = new PortoTrajectoryPreview { Name = "PortoPreview", Lines = CylindricLines };
        AddChild(PortoPreview);

        // Grappling-hook rope (Draw_GrapplingHook). Inject the SAME shared CylindricLine pool the beam/line
        // renderers lease from — without it HookRopeRenderer gates on the null Lines and idles. Self-driving off
        // the ambient entity facade, exactly like Lasers/PortoPreview above (live on a listen host/demo).
        HookRope = new HookRopeRenderer { Name = "HookRope", Lines = CylindricLines };
        AddChild(HookRope);

        // Mirror in-process effect emissions (server/local gameplay calling EffectEmitter.Emit) onto the
        // renderer. This is what makes a single-process demo show effects with no network layer present.
        EffectEmitter.Sink = new RenderSink(this, EffectEmitter.Sink);

        // Bind the Action hooks to the methods so either calling convention works for the net layer.
        EffectHook = (name, origin, vel, count) => OnEffect(name, origin, vel, count);
        EntityUpdateHook = OnEntityUpdate;
        EntityRemoveHook = OnEntityRemove;
        SoundHook = (s, o) => OnSound(s, o);
    }

    public override void _ExitTree()
    {
        // Detach our sink so a torn-down client doesn't keep mirroring into freed nodes.
        if (EffectEmitter.Sink is RenderSink rs)
            EffectEmitter.Sink = rs.Inner;

        // Free every cosmetic add-on node the layer is still holding (ice blocks / buff glows / spawn-loc markers).
        _cosmetics?.Clear();

        // Drop the static item-tint material caches: their keys are PER-MAP Material instances (AssetSystem
        // shared surface materials), so carrying them across sessions pins every visited map's weapon-item
        // materials + tint/duotone duplicates (and their textures) for the process lifetime. The dropper
        // shirt/pants palette alone is up to 16×16 variants per source material. Rebuilt on demand next map.
        _itemTintVariants.Clear();
        _itemDuotoneVariants.Clear();
        _weaponsByItemModel.Clear();

        // Same deterministic-disposal rule for any live per-entity ghost flat (see ReleaseItemFlat): on teardown
        // the entities are not individually removed, so sweep whatever CsqcState is still held.
        foreach (CsqcState cs in _csqc.Values)
            ReleaseItemFlat(cs);
    }

    // =================================================================================================
    //  Public API — called by the networking layer
    // =================================================================================================

    /// <summary>A networked particle effect fired at a point (Send_Effect). Names are EFFECT_* or effectinfo.</summary>
    public void OnEffect(string name, NVec3 origin, NVec3 velocity = default, int count = 1, Color? color = null)
    {
        Effects.Spawn(name, origin, velocity, count, color);
    }

    /// <summary>Effect fired from a decoded <see cref="EffectRequest"/> (the wire form).</summary>
    public void OnEffect(in EffectRequest request) => Effects.Spawn(request);

    /// <summary>
    /// A networked lightning arc between two Quake-space points (the TE_CSQC_ARC temp-entity: electro combo,
    /// Golem zaps, Tesla turret arcs). Draws the jagged crackling bolt; <paramref name="color"/> tints it.
    /// </summary>
    public void OnArc(NVec3 from, NVec3 to, Color? color = null) => Beams.Arc(from, to, color);

    /// <summary>A straight steady beam between two Quake-space points (rail / lightning trunk).</summary>
    public void OnBeam(NVec3 from, NVec3 to, Color? color = null) => Beams.Beam(from, to, color);

    /// <summary>
    /// An entity entered the client snapshot or changed. Routes projectiles to the
    /// <see cref="ProjectileRenderer"/> and everything else to an <see cref="EntityNode"/> (+ animator).
    /// Safe to call every snapshot for the same entity — it creates on first sight, updates thereafter.
    /// </summary>
    public void OnEntityUpdate(Entity entity) => OnEntityUpdate(entity, frameDriven: false);

    /// <summary>
    /// As <see cref="OnEntityUpdate(Entity)"/>, but when <paramref name="frameDriven"/> is true the entity's
    /// animation is driven by its networked <see cref="Entity.Frame"/> (CSQCMODEL_AUTOUPDATE) rather than the
    /// local movement-derived clip heuristic — used for networked remote players/monsters fed by the entity
    /// stream (see <c>ClientEntityView</c>). The flag takes effect when the model/animator is first attached.
    /// </summary>
    public void OnEntityUpdate(Entity entity, bool frameDriven)
    {
        if (entity is null || entity.IsFreed)
            return;

        if (frameDriven) _frameDriven.Add(entity.Index);
        else _frameDriven.Remove(entity.Index);

        if (IsProjectile(entity))
        {
            if (Projectiles.IsTracking(entity.Index)) Projectiles.OnUpdate(entity);
            else Projectiles.OnSpawn(entity);
            return;
        }

        // Vehicles get the dedicated visual driver (rotors/barrels/engine/gibs/heal-beam) instead of a plain
        // model node (Racer/Raptor/Spiderbot/Bumblebee — the bulk of the client vehicle TODOs).
        if (VehicleCatalog.Classify(entity.ClassName + " " + entity.Model) != VehicleCatalog.VehicleKind.None)
        {
            UpdateVehicle(entity);
            return;
        }

        // Non-projectile: ensure an EntityNode exists and is bound; it syncs origin/yaw each frame itself.
        if (!_entityNodes.TryGetValue(entity.Index, out EntityNode? node))
        {
            node = new EntityNode { Name = $"ent#{entity.Index}_{Safe(entity.ClassName)}" };
            AddChild(node);
            node.SetProcess(false); // R1: ClientWorld drives DriveSync centrally; kill the per-node _Process callback
            node.Bind(entity);
            _entityNodes[entity.Index] = node;

            TryAttachModel(entity, node);
            TryAttachNameplate(entity, node);
        }
        else
        {
            node.Entity = entity;
        }

        // Drive animation from the entity's frame/movement if it has an animator.
        UpdateAnimatorState(entity);
        UpdateNameplate(entity);
    }

    /// <summary>Add a floating health/id nameplate above a remote player (ent_cs). Skipped for non-players.</summary>
    private void TryAttachNameplate(Entity entity, EntityNode node)
    {
        if (!entity.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase))
            return;
        var plate = new Label3D
        {
            Name = "Nameplate",
            Position = new Vector3(0f, 60f, 0f),  // above the head (Godot up)
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            PixelSize = 0.03f,
            FontSize = 48,
            OutlineSize = 8,
            Modulate = Colors.White,
            Text = "",
        };
        node.AddChild(plate);
        _nameplates[entity.Index] = plate;
    }

    /// <summary>Refresh a player's nameplate text (health) each update; hide it when the player is dead.</summary>
    private void UpdateNameplate(Entity entity)
    {
        if (!_nameplates.TryGetValue(entity.Index, out Label3D? plate) || !GodotObject.IsInstanceValid(plate))
            return;
        bool dead = entity.Health <= 0f && entity.MaxHealth > 0f;
        plate.Visible = !dead;
        if (!dead)
            plate.Text = $"♥ {Mathf.Max(0, (int)entity.Health)}";
    }

    /// <summary>An entity left the snapshot / was freed. Tears down its visual(s).</summary>
    public void OnEntityRemove(Entity entity)
    {
        if (entity is null)
            return;
        OnEntityRemove(entity.Index, entity.Origin);
    }

    /// <summary>Remove by index (the net layer may only have the id by the time it's culled).</summary>
    public void OnEntityRemove(int index, NVec3 lastOrigin = default, string? impactEffect = null)
    {
        _frameDriven.Remove(index);
        _graceHidden.Remove(index); // a net id is reusable — a stale grace mark would start the next tenant hidden
        _nameplates.Remove(index); // the Label3D is a child of the node — freed with it below
        StopLoopsForEntity(index); // end any looping sounds this entity emitted (Arc beam, vehicle engine)

        // Tear down the CSQC-model effect state (stop the jetpack loop, free the dynamic light).
        if (_csqc.Remove(index, out CsqcState? cs))
        {
            // Build a throwaway entity carrying the id so the sound service can key the (e, channel) stop.
            CsqcModelEffects.Release(new Entity { Index = index }, cs.Effects, Api.Services?.Sound);
            ReleaseItemFlat(cs);
        }

        if (Projectiles.IsTracking(index))
            Projectiles.OnRemove(index, lastOrigin, impactEffect);

        _cosmetics?.Remove(index); // free any cosmetic add-on (ice block / buff glow / spawn-loc marker) for this entity

        if (_animators.Remove(index, out ModelAnimator? anim) && GodotObject.IsInstanceValid(anim))
            anim.QueueFree();

        if (_playerModels.Remove(index, out PlayerModel? pm) && GodotObject.IsInstanceValid(pm))
        {
            pm.ReleaseSkeleton(); // skel_delete on the CPU poser
            pm.QueueFree();
        }

        if (_vehicles.Remove(index, out VehicleVisuals? veh) && GodotObject.IsInstanceValid(veh))
        {
            // Trigger the death gibs/explosion on removal, then let the driver self-free after they settle.
            veh.Apply(new VehicleVisuals.State { Alive = false }, 0f);
        }

        if (_entityNodes.Remove(index, out EntityNode? node) && GodotObject.IsInstanceValid(node))
            node.QueueFree();
    }

    /// <summary>
    /// [crash fix 2026-07-26] Hide/show an entity's render nodes WITHOUT tearing them down — the
    /// PVS-flicker grace window (ClientEntityView.CullDeparted) parks a briefly-unseen entity here
    /// instead of destroying it: a full teardown+rebuild per PVS boundary crossing was churning
    /// ~5-10 mesh/material RIDs per second into the .NET finalizer thread, whose concurrent
    /// RenderingServer::free calls race the main thread's surface updates (the 0xC0000374 mid-session
    /// heap corruption). No-op for ids with no render state.
    /// </summary>
    public void SetEntityHidden(int index, bool hidden)
    {
        // The grace set is consulted by the per-frame player pass — without it, that pass's
        // first-person visibility toggle force-shows a PVS-hidden player model every frame,
        // making the hide half of this mechanism a no-op for players.
        if (hidden) _graceHidden.Add(index);
        else _graceHidden.Remove(index);
        if (_animators.TryGetValue(index, out ModelAnimator? anim) && GodotObject.IsInstanceValid(anim))
            anim.Visible = !hidden;
        if (_playerModels.TryGetValue(index, out PlayerModel? pm) && GodotObject.IsInstanceValid(pm))
            pm.Visible = !hidden;
        if (_entityNodes.TryGetValue(index, out EntityNode? node) && GodotObject.IsInstanceValid(node))
            node.Visible = !hidden;
    }

    // Ids currently parked hidden by the PVS-flicker grace window (see SetEntityHidden).
    private readonly System.Collections.Generic.HashSet<int> _graceHidden = new();

    /// <summary>
    /// The Godot node a child should attach to in order to track entity <paramref name="ownerIndex"/> at the
    /// named model tag (QC <c>setattachment(weapon, player, "tag_weapon")</c>) — the owner's
    /// <see cref="ModelAnimator"/> tag <see cref="Marker3D"/> when present (so the attachment follows the
    /// animation), else the owner's animator/entity root, else null when the owner isn't rendered yet. Used by
    /// the weapon view-entity renderer to hang a remote player's held weapon off their hand.
    /// </summary>
    public Node3D? GetAttachmentMarker(int ownerIndex, string tagName)
    {
        // QW1: a skeletal player (the primary player path) exposes the resolved weapon bone as a tracked
        // "tag_weapon" marker — return it FIRST so the held weapon attaches to the HAND bone, not the body root.
        // When the weapon bone is unresolved (TagWeaponMarker null) fall through to the old animator/entity-root
        // behavior below.
        if (_playerModels.TryGetValue(ownerIndex, out PlayerModel? pm) && GodotObject.IsInstanceValid(pm))
        {
            Node3D? tag = pm.TagWeaponMarker;
            if (tag is not null && GodotObject.IsInstanceValid(tag))
                return tag;
        }
        if (_animators.TryGetValue(ownerIndex, out ModelAnimator? anim) && GodotObject.IsInstanceValid(anim))
        {
            Marker3D? m = string.IsNullOrEmpty(tagName) ? null : anim.GetTag(tagName);
            return m ?? (Node3D)anim;
        }
        if (_entityNodes.TryGetValue(ownerIndex, out EntityNode? node) && GodotObject.IsInstanceValid(node))
            return node;
        return null;
    }

    /// <summary>
    /// Dispose an entity's per-entity dark-ghost flat material DETERMINISTICALLY. Dropping the CsqcState
    /// without this leaves the material to the .NET finalizer thread, whose RenderingServer::free races the
    /// main thread's renderer work — the 0xC0000374 heap-corruption class documented in SharedMeshCache.
    /// It is a per-ENTITY material (see CsqcState.ItemFlatMat), so every picked-up item that later leaves the
    /// PVS would orphan one: a steady drip on any populated map, not a one-off.
    /// </summary>
    private static void ReleaseItemFlat(CsqcState cs)
    {
        if (cs.ItemFlatMat is null)
            return;
        cs.ItemFlatMat.Dispose();
        cs.ItemFlatMat = null;
    }

    /// <summary>
    /// Tear down the entity's model visual(s) and re-run the attach. Used when an ASYNC player-model resolve
    /// settles on a different outcome than first assumed (the streamed parse found a non-skeletal model → fall
    /// back to the MD3/static path) or the entity's model changed while a resolve was in flight. The nameplate
    /// survives; the freed model children invalidate the cached csqc mesh list on the next effects pass.
    /// </summary>
    public void RebuildEntityModel(Entity entity)
    {
        if (entity is null || entity.IsFreed)
            return;
        if (!_entityNodes.TryGetValue(entity.Index, out EntityNode? node) || !GodotObject.IsInstanceValid(node))
            return;

        // Release the per-entity render state exactly like OnEntityRemove, but keep the node + nameplate.
        if (_csqc.Remove(entity.Index, out CsqcState? cs))
        {
            CsqcModelEffects.Release(entity, cs.Effects, Api.Services?.Sound);
            ReleaseItemFlat(cs);
        }
        _animators.Remove(entity.Index);
        if (_playerModels.Remove(entity.Index, out PlayerModel? pm) && GodotObject.IsInstanceValid(pm))
            pm.ReleaseSkeleton(); // the node itself is swept below

        foreach (Node child in node.GetChildren())
        {
            if (child is Label3D)
                continue; // the floating nameplate isn't part of the model
            node.RemoveChild(child);
            child.QueueFree();
        }

        TryAttachModel(entity, node);
    }

    /// <summary>
    /// Run the appearance pass (colormap/forcecolors tint) for one entity NOW — a model whose meshes were
    /// built after the attach (the streamed player-model path) would otherwise render a frame untinted before
    /// the per-frame <c>DriveCsqcModelHooks</c> pass reaches it.
    /// </summary>
    public void SeedAppearance(Entity entity)
    {
        if (entity is null || entity.IsFreed)
            return;
        if (!_entityNodes.TryGetValue(entity.Index, out EntityNode? node) || !GodotObject.IsInstanceValid(node))
            return;
        ModelTint.ApplyAppearance(node, ResolveForcedColormap(entity), isDead: false, deathTime: 0f, isRespawnGhost: false);
    }

    /// <summary>Resolve a model for a networked entity via the host's <see cref="ModelResolver"/> (or null).</summary>
    public VortexArena.Formats.Md3.Md3Data? ResolveModel(Entity e) => ModelResolver?.Invoke(e);

    /// <summary>The node networked render children (e.g. weapon view-entities) parent under when unattached.</summary>
    public Node3D RenderRoot => this;

    /// <summary>
    /// A positional sound event at a point (QC sound() on an entity, networked to the client). Resolves the
    /// sample to a Godot audio resource and plays it spatially. <paramref name="sample"/> is the bare path
    /// (QC <c>sound_str</c>) or a registered <see cref="GameSound.NetName"/>. When the channel is a positive
    /// SINGLE channel and the emitter has a valid net id, a new play replaces (stops) the previous sound on
    /// that (entity, channel) key — DP's CH_*_SINGLE semantic. Auto/negative channels always stack.
    /// </summary>
    public void OnSound(string sample, NVec3 origin, float volume = 1f, float attenuation = 0.5f,
        int channel = 0, int sourceNetId = 0, float pitch = 1f)
    {
        GameSound? reg = Sounds.ByName(sample);
        string bare = reg?.Sample ?? sample;
        if (reg is not null) { volume = reg.Volume; }

        AudioStream? stream = LoadStream(bare);
        if (stream is null)
            return;

        string bus = BusForChannel(channel);
        bool isSingle = channel > 0 && sourceNetId > 0;

        if (isSingle)
        {
            var key = (sourceNetId, channel);
            if (_singleChannelPlayers.TryGetValue(key, out AudioStreamPlayer3D? prev)
                && GodotObject.IsInstanceValid(prev) && prev.Playing)
                prev.Stop();
            _singleChannelPlayers[key] = StartOneShot(stream, bus, origin, volume, attenuation, pitch, sourceNetId);
        }
        else
        {
            StartOneShot(stream, bus, origin, volume, attenuation, pitch, sourceNetId);
        }
    }

    /// <summary>Rent, configure and play a one-shot positional player and register it in <see cref="_activeOneShots"/>
    /// so it is re-spatialized each frame (<see cref="DriveOneShots"/>): it FOLLOWS its emitter when the record has
    /// a valid net id — DP attaches a sound to the entity so it tracks the moving emitter instead of being stranded
    /// at the firing point — and its DP distance volume is updated as the listener moves.</summary>
    private AudioStreamPlayer3D StartOneShot(AudioStream stream, string bus, NVec3 origin,
        float volume, float attenuation, float pitch, int sourceNetId)
    {
        Godot.Vector3 listener = ListenerPos();
        AudioStreamPlayer3D player = RentAudioPlayer();
        player.Stream = stream;
        player.Bus = bus;
        // ATTEN_NONE: play centered on the listener (DP plays these non-spatialized); else at the emit point.
        player.GlobalPosition = attenuation <= 0f ? listener : Coords.ToGodot(origin);
        player.PitchScale = pitch;
        SetSpatialVolume(player, volume, attenuation, listener); // initial DP-attenuated volume (no 1-frame blast)
        player.Play();

        // A pooled player may still be registered from a previous sound — drop that stale entry before re-adding,
        // so one sound can't be dragged around / re-volumed by a prior emitter's tracking.
        for (int i = _activeOneShots.Count - 1; i >= 0; i--)
            if (_activeOneShots[i].Player == player)
                _activeOneShots.RemoveAt(i);
        _activeOneShots.Add(new ActiveOneShot
        {
            Player = player, NetId = sourceNetId, BaseVolume = volume, Attenuation = attenuation,
        });
        return player;
    }

    // (R18) Cache the active Camera3D per process-frame. ListenerPos + ViewOrigin each resolve it (and ListenerPos
    // is called again per looping sound), and GetViewport().GetCamera3D() is two marshalled crossings each call.
    // Keyed on the frame counter so a camera from a prior frame is never returned stale.
    private ulong _camFrame = ulong.MaxValue;
    private Camera3D? _camThisFrame;

    private Camera3D? FrameCamera()
    {
        ulong f = Godot.Engine.GetProcessFrames();
        if (f != _camFrame)
        {
            Camera3D? cam = GetViewport()?.GetCamera3D();
            _camThisFrame = cam is not null && GodotObject.IsInstanceValid(cam) ? cam : null;
            _camFrame = f;
        }
        return _camThisFrame;
    }

    /// <summary>The current listener position (the active <see cref="Camera3D"/>, which IS Godot's audio listener)
    /// in Godot space; falls back to the last known position when no camera is available this frame.</summary>
    private Godot.Vector3 ListenerPos()
    {
        Camera3D? cam = FrameCamera();
        if (cam is not null)
            _lastListener = cam.GlobalPosition;
        return _lastListener;
    }

    /// <summary>DP distance gain (snd_main.c SND_Spatialize): the exponent term — <c>(1 - min(1, f))^exponent</c>,
    /// f = dist·atten/radius — times the optional decibel term — <c>0.1^(0.1·decibel·f)</c>. With the shipped
    /// exponent 4 the curve has a steep tail so distant sounds go quiet; exponent 1 is the flat Quake linear ramp;
    /// the decibel method (exponent 0, decibel&gt;0) is a pure dB falloff with no hard cutoff. ATTEN_NONE (≤0) is
    /// full everywhere.</summary>
    private float DpDistanceGain(float distance, float attenuation)
    {
        if (attenuation <= 0f)
            return 1f;
        float radius = _sndRadius > 0f ? _sndRadius : 2400f;
        float f = distance * (attenuation / radius); // DP: dist * distfade, distfade = atten/soundradius
        float g = _sndExponent == 0f ? 1f : Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Min(1f, f)), _sndExponent);
        if (_sndDecibel != 0f)
            g *= Mathf.Pow(0.1f, 0.1f * _sndDecibel * f);
        return g;
    }

    /// <summary>Apply the DP distance attenuation to a 3D player relative to <paramref name="listener"/>, scaling
    /// its pre-attenuation <paramref name="baseVolume"/>. Godot's own attenuation model is DISABLED on these
    /// players (see <see cref="RentAudioPlayer"/>/<see cref="CreateLoop"/>), so this is the sole volume curve —
    /// it reproduces DP's falloff instead of Godot's steep inverse-distance one. Panning still comes from the
    /// player's 3D position relative to the listener.</summary>
    private void SetSpatialVolume(AudioStreamPlayer3D player, float baseVolume, float attenuation, Godot.Vector3 listener)
    {
        float dist = player.GlobalPosition.DistanceTo(listener);
        float gain = Mathf.Clamp(baseVolume, 0f, 1f) * DpDistanceGain(dist, attenuation);
        player.VolumeDb = gain <= 0.0008f ? -80f : Mathf.LinearToDb(gain); // -80 dB ≈ silent
    }

    /// <summary>
    /// Map a <see cref="SoundChannel"/> value to the corresponding Godot audio bus name. Provides per-channel
    /// volume control (QC <c>snd_channel&lt;N&gt;volume</c> → dedicated buses created by <c>ClientSettings</c>).
    /// </summary>
    private static string BusForChannel(int channel)
    {
        // SoundChannel enum values:
        //   WeaponSingle=1/WeaponAuto=-1 → Weapon
        //   Voice=2/VoiceAuto=-2          → Voice
        //   Body=4/Player=7/Pain=6        → Player
        //   Item=3/TriggerAuto=-3         → Ambient (SFX fallback)
        //   Auto=0/ShotsAuto=-4           → SFX
        return channel switch
        {
            1 or -1 => "Weapon",                    // WeaponSingle, WeaponAuto
            2 or -2 => "Voice",                     // Voice, VoiceAuto
            4 or 6 or 7 or -6 or -7 => "Player",    // Body, Pain, Player (+ PainAuto, PlayerAuto)
            3 or -3 => "Ambient",                   // Item, TriggerAuto
            _ => "SFX",                             // Auto (0), ShotsAuto (-4), Tuba (5), Bgm (8), etc.
        };
    }

    /// <summary>
    /// Start (or refresh, or replace) a LOOPING positional sound keyed by (<paramref name="netId"/>,
    /// <paramref name="channel"/>) — DP <c>loopsound</c> on an entity+channel (the Arc beam, vehicle engines).
    /// Idempotent on re-emit: if the SAME sample is already looping on that key, this only refreshes its spatial
    /// params + keepalive and does NOT restart it — so a continuous emitter (the Arc) can call it every
    /// weapon-think without audible stacking. A DIFFERENT sample on the key replaces the old loop. The source
    /// then follows its emitter each frame via <see cref="EntityOriginResolver"/>.
    /// </summary>
    public void OnLoopingSound(int netId, int channel, string sample, NVec3 origin, float volume = 1f, float attenuation = 1f)
    {
        if (string.IsNullOrEmpty(sample))
            return;

        // Allow either a registered sound name or a raw sample path (same resolution as the one-shot OnSound).
        GameSound? reg = Sounds.ByName(sample);
        string bare = reg?.Sample ?? sample;
        if (reg is not null) volume = reg.Volume;

        var key = (netId, channel);
        if (_loopingSounds.TryGetValue(key, out LoopingSound? existing) && GodotObject.IsInstanceValid(existing.Player))
        {
            if (existing.Sample == bare)
            {
                // Same loop already playing on this (entity, channel): refresh, don't restart (idempotent re-emit).
                existing.SilentTime = 0f;
                ApplyLoopSpatial(existing, origin, volume, attenuation, ListenerPos());
                return;
            }
            DestroyLoop(existing);          // a different sample on the key → replace
            _loopingSounds.Remove(key);
        }

        AudioStream? stream = LoadStream(bare);
        if (stream is null)
            return;

        LoopingSound ls = CreateLoop(netId, channel, stream, bare);
        ApplyLoopSpatial(ls, origin, volume, attenuation, ListenerPos());
        _loopingSounds[key] = ls;
    }

    /// <summary>
    /// Stop the looping sound on (<paramref name="netId"/>, <paramref name="channel"/>) — DP
    /// <c>sound(e, ch, SND_Null)</c> (the Arc beam on release/overheat). A no-op if nothing loops on that key.
    /// </summary>
    public void OnStopSound(int netId, int channel)
    {
        if (_loopingSounds.Remove((netId, channel), out LoopingSound? ls))
            DestroyLoop(ls);
    }

    /// <summary>Build a dedicated looping <see cref="AudioStreamPlayer3D"/> for a (netId, channel) loop (NOT pooled —
    /// the one-shot pool would cut it off). Ogg/MP3 loop natively; other types re-trigger on <c>Finished</c>.</summary>
    private LoopingSound CreateLoop(int netId, int channel, AudioStream stream, string sample)
    {
        AudioStream looping = AudioLoop.MakeLooping(stream, out bool nativeLoop);
        var player = new AudioStreamPlayer3D
        {
            Name = $"loop_{netId}_{channel}",
            Stream = looping,
            // Godot attenuation off — the DP linear curve is applied each frame in DriveLoopingSounds.
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            MaxDistance = 0f,
            AttenuationFilterCutoffHz = 20500f, // disable Godot's distance low-pass (DP has none)
        };
        var ls = new LoopingSound { Player = player, Sample = sample };
        AddChild(player);
        if (!nativeLoop)
            // WAV / no native loop flag: re-trigger on end while this entry is still the live loop on its key.
            player.Finished += () => { if (ls.Alive && GodotObject.IsInstanceValid(player)) player.Play(); };
        player.Play();
        return ls;
    }

    private static void DestroyLoop(LoopingSound ls)
    {
        ls.Alive = false; // the Finished re-play handler (if any) goes inert
        if (GodotObject.IsInstanceValid(ls.Player))
        {
            ls.Player.Stop();
            ls.Player.QueueFree();
        }
    }

    /// <summary>Place + tune a looping source (mirrors the one-shot spatialization in <see cref="StartOneShot"/>):
    /// store the base volume/attenuation for the per-frame DP falloff and set the initial position + volume.</summary>
    private void ApplyLoopSpatial(LoopingSound ls, NVec3 origin, float volume, float attenuation, Godot.Vector3 listener)
    {
        ls.BaseVolume = volume;
        ls.Attenuation = attenuation;
        ls.Player.GlobalPosition = attenuation <= 0f ? listener : Coords.ToGodot(origin);
        SetSpatialVolume(ls.Player, volume, attenuation, listener);
    }

    /// <summary>Follow each looping source to its emitter's current position and age its keepalive; auto-stop a loop
    /// not refreshed for <see cref="LoopKeepaliveTimeout"/> (its emitter stopped without an explicit stop).</summary>
    private void DriveLoopingSounds(float delta, Godot.Vector3 listener)
    {
        if (_loopingSounds.Count == 0)
            return;

        _loopScratch.Clear();
        foreach (var kv in _loopingSounds)
        {
            LoopingSound ls = kv.Value;
            if (!GodotObject.IsInstanceValid(ls.Player))
            {
                _loopScratch.Add(kv.Key);
                continue;
            }
            ls.SilentTime += delta;
            if (ls.SilentTime > LoopKeepaliveTimeout)
            {
                _loopScratch.Add(kv.Key);
                continue;
            }
            // ATTEN_NONE → centered on the listener; else follow the emitter (local player → predicted eye;
            // remotes → interpolated pose). Then re-apply the DP distance falloff.
            if (ls.Attenuation <= 0f)
                ls.Player.GlobalPosition = listener;
            else if (EntityOriginResolver?.Invoke(kv.Key.netId) is { } pos)
                ls.Player.GlobalPosition = Coords.ToGodot(pos);
            SetSpatialVolume(ls.Player, ls.BaseVolume, ls.Attenuation, listener);
        }
        for (int i = 0; i < _loopScratch.Count; i++)
            if (_loopingSounds.Remove(_loopScratch[i], out LoopingSound? dead))
                DestroyLoop(dead);
    }

    /// <summary>Each frame, for every live one-shot: follow its emitter (so a sound tracks the entity that made it
    /// rather than being stranded where it started) and re-apply the DP distance attenuation relative to the
    /// listener (so a fixed-point impact gets quieter/louder as YOU move toward or away from it — Godot's own
    /// attenuation is disabled). Prune entries whose player has finished or been recycled.</summary>
    private void DriveOneShots(Godot.Vector3 listener)
    {
        for (int i = _activeOneShots.Count - 1; i >= 0; i--)
        {
            ActiveOneShot a = _activeOneShots[i];
            if (!GodotObject.IsInstanceValid(a.Player) || !a.Player.Playing)
            {
                _activeOneShots.RemoveAt(i);
                continue;
            }
            if (a.Attenuation <= 0f)
                a.Player.GlobalPosition = listener;                  // ATTEN_NONE: stay centered
            else if (a.NetId > 0 && EntityOriginResolver?.Invoke(a.NetId) is { } pos)
                a.Player.GlobalPosition = Coords.ToGodot(pos);       // follow the emitter; fixed point otherwise
            SetSpatialVolume(a.Player, a.BaseVolume, a.Attenuation, listener);
        }
    }

    /// <summary>Stop every looping sound emitted by <paramref name="netId"/> (any channel) — the emitter left the
    /// snapshot / was removed, so its loops (Arc beam, vehicle engine) must end even without an explicit stop.</summary>
    private void StopLoopsForEntity(int netId)
    {
        if (_loopingSounds.Count == 0 && _singleChannelPlayers.Count == 0)
            return;

        _loopScratch.Clear();
        foreach (var kv in _loopingSounds)
            if (kv.Key.netId == netId)
                _loopScratch.Add(kv.Key);
        for (int i = 0; i < _loopScratch.Count; i++)
            if (_loopingSounds.Remove(_loopScratch[i], out LoopingSound? ls))
                DestroyLoop(ls);

        _loopScratch.Clear();
        foreach (var kv in _singleChannelPlayers)
            if (kv.Key.netId == netId)
                _loopScratch.Add(kv.Key);
        for (int i = 0; i < _loopScratch.Count; i++)
        {
            if (_singleChannelPlayers.Remove(_loopScratch[i], out AudioStreamPlayer3D? p)
                && GodotObject.IsInstanceValid(p) && p.Playing)
                p.Stop();
        }
    }

    /// <summary>The local player fired — drive the view-model muzzle flash (CSQC W_MuzzleFlash).</summary>
    public void OnMuzzleFlash(string? effectOverride = null) => ViewModel?.Fire(effectOverride);

    /// <summary>
    /// Set / clear the crosshair-chase body fade target for the local player model (QC
    /// <c>crosshair_chase_playeralpha</c>): in the third-person crosshair-chase view the local body is faded so it
    /// doesn't block the aim. <paramref name="alpha"/> is the desired RENDER alpha in (0,1]; the caller passes
    /// <c>1 − crosshair_chase_playeralpha</c> (the 0.25 default → 0.75). Pass <c>alpha &lt;= 0</c> to CLEAR the fade
    /// (the model eases back to opaque). A non-positive <paramref name="localNetId"/> is ignored. The per-frame
    /// model pass eases the matching <see cref="PlayerModel"/> toward this each frame; with no target set it no-ops.
    /// </summary>
    public void SetLocalBodyAlpha(int localNetId, float alpha)
    {
        if (localNetId <= 0)
            return;
        if (alpha <= 0f)
            _bodyAlphaTarget.Remove(localNetId);
        else
            _bodyAlphaTarget[localNetId] = Mathf.Clamp(alpha, 0f, 1f);
    }

    /// <summary>
    /// QC CSQC's view-entity suppression: the net id of the player the camera currently renders from INSIDE
    /// (a first-person followed spectatee) — that body is hidden in the player pass. 0 = none (chase view or
    /// not spectating). Set every frame by NetGame.UpdateCamera; instant on/off by design (no fade).
    /// </summary>
    private int _viewedFirstPersonNetId;

    /// <inheritdoc cref="_viewedFirstPersonNetId"/>
    public void SetViewedFirstPerson(int netId) => _viewedFirstPersonNetId = netId;

    // =================================================================================================
    //  Per-frame drive
    // =================================================================================================

    public override void _Process(double delta)
    {
        using var _cwScope = FrameProfiler.Scope("cw.process"); // [profiling] whole ClientWorld._Process
        // #30 slowmo/pause: every VISUAL animation in this drive advances by the slowmo-scaled delta (the port
        // of DP's cl.time — all CSQC animation freezes/slows with the sim). Audio spatialization below stays on
        // the raw delta (DP doesn't timescale sound envelopes).
        float dtAnim = ClientRenderTime.ScaleDelta((float)delta);
        // The EffectSystem nodes and projectile trails advance themselves (they're Godot particle nodes);
        // ProjectileRenderer and the ModelAnimators also self-advance via their own _Process. We still poll
        // here for entity-bound animators that need their clip re-selected from movement each frame.
        using (FrameProfiler.Scope("cw.anim"))
        foreach (var kv in _animators)
        {
            ModelAnimator anim = kv.Value;
            Entity? e = anim.Entity;
            // Movement-derived clip pick is only for non-networked (demo/local) animators; networked entities
            // play their server frame directly (CSQCMODEL_AUTOUPDATE).
            if (!anim.FollowEntityFrame && e is not null && !e.IsFreed)
                SelectClipFromMovement(e, anim);
            // R1: Advance() relocated here from the (now-disabled) ModelAnimator._Process so the morph still
            // rebuilds once per frame without paying a per-node native->managed callback.
            anim.Advance(dtAnim);
        }

        // Skeletal player models: synthesize the four-pose split + aim each frame and push the CPU bones.
        // 3.3 pose-cull (cl_pose_cull, default OFF): read the gate once per frame, plus the local-player id and
        // the camera position, so off-screen / distant REMOTE models can skip the interop bone push. Coords is
        // 1:1 (no meter scale) so Godot-space distance == Quake-unit distance; cl_pose_cull_distance is squared
        // once here. ListenerPos() never returns null (it falls back to _lastListener), so distSq is NRE-safe.
        // The pose-cull's on-screen test (VisibleOnScreenNotifier3D) only reflects the MAIN camera — a player
        // visible ONLY through a warpzone portal would keep an unpushed (rest/degenerate-AABB) skeleton and
        // never render in the portal view. While any portal is actively rendering, pose everyone (portals are
        // occasional; the cost reverts to the pre-cull baseline only while one is on screen).
        bool poseCull = CvarF("cl_pose_cull", 0f) != 0f
            && VortexArena.Game.Client.PortalRenderer.ActiveExitViewsQuake.Count == 0;
        float cullDist = CvarF("cl_pose_cull_distance", 1500f);
        float cullDistSq = poseCull ? cullDist * cullDist : 0f;
        // Base csqcmodel's clip-switch crossfade window (cl_lerpanim_maxdelta_framegroups, cl_model.qc:29,
        // default 0.1 s; 0 = snap, the pre-crossfade port behavior). Read once per frame, written per model.
        float lerpAnimMax = CvarF("cl_lerpanim_maxdelta_framegroups", 0.1f);
        // Corpse ragdolls (PORT DIVERGENCE, default OFF): only with a client collision world, never on a
        // headless host (the pose path still runs there — a CPU ragdoll would be pure server cost). The
        // cl_ragdoll_max budget counts live ragdolls; over budget, new deaths keep the animated die1/die2.
        bool ragdollOn = RagdollTrace is not null && !HeadlessHost && CvarF("cl_ragdoll", 0f) != 0f;
        int ragdollBudget = 0;
        if (ragdollOn)
        {
            int max = (int)CvarF("cl_ragdoll_max", 6f);
            int active = 0;
            foreach (PlayerModel p in _playerModels.Values)
                if (p.RagdollActive)
                    active++;
            ragdollBudget = max - active;
        }
        int localId = AppearanceProvider?.Invoke()?.LocalNetId ?? -1;
        // [W14b LI3] the server clock the networked anim-action start is stamped on; NaN (no provider) disables the
        // torso-action overlay so a pure demo / the smoke harness keeps the static aim pose unchanged.
        float serverNow = ServerTimeProvider?.Invoke() ?? float.NaN;
        Godot.Vector3 viewG = ListenerPos();
        using (FrameProfiler.Scope("cw.players"))
        foreach (PlayerModel pm in _playerModels.Values)
        {
            Entity? e = pm.Bound;
            if (e is not null && !e.IsFreed && GodotObject.IsInstanceValid(pm))
            {
                bool isLocal = e.Index == localId;
                // QC CSQC's "don't draw the view entity": the camera renders from inside this player (a
                // followed spectatee in first person — one's OWN entity never reaches the client's stream,
                // ServerNet skips the recipient, so only the spectatee case exists). Instant both ways: a
                // fade here would leave body polygons across the eye for several frames on every re-glue.
                bool viewedFp = e.Index == _viewedFirstPersonNetId;
                // A grace-hidden model (PVS flicker window, SetEntityHidden) must STAY hidden — this
                // toggle only reconciles the first-person state for models the grace window isn't holding.
                bool wantVisible = !viewedFp && !_graceHidden.Contains(e.Index);
                if (pm.Visible != wantVisible)
                    pm.Visible = wantVisible;
                float distSq = poseCull ? (pm.GlobalPosition - viewG).LengthSquared() : 0f;
                // QC ENT_CLIENT_STATUSEFFECTS frozen: hold the skeletal pose static while encased (the ice block
                // freezes the animation, not just the tint). Set from the networked StatusEffects bitmap before posing.
                pm.FrozenHold = HasStatusEffect(e, StatusEffectsCatalog.Frozen);
                pm.FadeSeconds = lerpAnimMax;
                pm.RagdollTrace = ragdollOn ? RagdollTrace : null;
                bool wasRagdoll = pm.RagdollActive;
                pm.RagdollAllowNew = ragdollBudget > 0;
                pm.Pose(e, dtAnim, poseCull, isLocal, distSq, cullDistSq, serverNow);
                if (!wasRagdoll && pm.RagdollActive)
                    ragdollBudget--;
                // Cosmetic add-on layer also attaches to the skeletal player node (it follows origin/yaw like the
                // EntityNode path) so the ice block / held buff glow ride the posed body too.
                _cosmetics.Drive(e, pm);
            }
        }

        // Crosshair-chase local-body fade: ease each targeted local model toward its desired alpha (and back to
        // opaque once the target clears). No-op while no chase target is set (the common case pays a single
        // dictionary-count check).
        DriveBodyAlpha(dtAnim);

        // (R1/R2) central entity-node drive — folds the DP-faithful entity PVS cull into the same pass, so this
        // one call replaces the old separate ApplyEntityPvsCull. See DriveEntityNodes.
        using (FrameProfiler.Scope("entitynode")) DriveEntityNodes(localId);
        using (FrameProfiler.Scope("cw.csqc")) DriveCsqcModelHooks(dtAnim);
        using (FrameProfiler.Scope("cw.vehicles")) DriveVehicles(dtAnim);
        // Live-poll the dynamic map/scene tint cvars so a console `set r_map_tint*` re-tints instantly (the
        // testing path); when the strength cvars are 0 this is a couple of cheap reads and leaves the map/code
        // baseline in place. See VortexArena.Game.WorldTint.
        WorldTint.PollCvars();
        // Live-poll the projectile client-side prediction toggle so a console `set cl_projectile_prediction 0`
        // flips back to the old ease (A/B feel-testing) at once. Default on = CSQC-style snap+extrapolate.
        Projectiles.Predict = CvarF("cl_projectile_prediction", 1f) != 0f;
        // Re-spatialize active sounds against the current listener (camera) — DP distance attenuation +
        // emitter-follow — so volume tracks how near/far you are even while you move past a fixed-point impact.
        // Refresh the attenuation cvars live so a runtime `set snd_attenuation_exponent N` takes effect at once.
        _sndRadius = CvarF("snd_soundradius", 2400f);
        _sndExponent = CvarF("snd_attenuation_exponent", 4f);
        _sndDecibel = CvarF("snd_attenuation_decibel", 0f);
        Godot.Vector3 listener = ListenerPos();
        using (FrameProfiler.Scope("cw.audio"))
        {
            DriveLoopingSounds((float)delta, listener);
            DriveOneShots(listener);
        }
    }

    /// <summary>cl_rollkillspeed-style ease per second toward the body-fade target — fast enough to feel responsive
    /// on a chase toggle, slow enough to not pop.</summary>
    private const float BodyAlphaEaseRate = 8f;

    /// <summary>
    /// Ease each targeted local <see cref="PlayerModel"/>'s render alpha toward its crosshair-chase
    /// <see cref="_bodyAlphaTarget"/> (QC <c>crosshair_chase_playeralpha</c>) and push it through
    /// <see cref="PlayerModel.ApplyAlpha"/> (per-instance <c>GeometryInstance3D.Transparency</c> — never a shared
    /// material edit, the same knob the alpha-net seam uses). A cleared target eases the model back to opaque (1)
    /// and is then dropped, so a chase exit restores the body smoothly. Whole-method no-op when no fade is active —
    /// non-chase frames pay only the two empty-dictionary checks.
    /// </summary>
    private void DriveBodyAlpha(float delta)
    {
        if (_bodyAlphaTarget.Count == 0 && _bodyAlphaCurrent.Count == 0)
            return;

        float step = Mathf.Clamp(delta * BodyAlphaEaseRate, 0f, 1f);
        _bodyAlphaScratch.Clear();

        // Walk every model that currently has an eased value OR a fresh target.
        foreach (int netId in _bodyAlphaCurrent.Keys)
            _bodyAlphaScratch.Add(netId);
        foreach (int netId in _bodyAlphaTarget.Keys)
            if (!_bodyAlphaCurrent.ContainsKey(netId))
                _bodyAlphaScratch.Add(netId);

        for (int i = 0; i < _bodyAlphaScratch.Count; i++)
        {
            int netId = _bodyAlphaScratch[i];
            // Target render alpha: the stored fade while a chase is active, else 1 (opaque) so a cleared target
            // eases the body back in.
            float target = _bodyAlphaTarget.TryGetValue(netId, out float t) ? t : 1f;
            float current = _bodyAlphaCurrent.TryGetValue(netId, out float c) ? c : 1f;
            current = Mathf.Lerp(current, target, step);
            // Snap when within a hair so we converge (and can drop a fully-restored entry instead of easing forever).
            if (Mathf.Abs(current - target) < 0.002f)
                current = target;

            if (_playerModels.TryGetValue(netId, out PlayerModel? pm) && GodotObject.IsInstanceValid(pm))
            {
                // PlayerModel.Pose already pushed the networked Entity.Alpha (Cloaked / fades) this frame; COMPOSE the
                // chase fade on top of it (multiply) so a cloaked-in-chase body honors the lower of the two instead
                // of overwriting the networked alpha. Bound is the model's entity; missing → treat as opaque (1).
                float netAlpha = pm.Bound is { IsFreed: false } be ? be.Alpha : 1f;
                if (netAlpha < 0f) netAlpha = 0f; // a hidden/gib alpha clamps like PlayerModel.ApplyAlpha does
                pm.ApplyAlpha(current * Mathf.Min(1f, netAlpha));
            }

            // Once opaque again with no live target, forget the model (back to the model's own networked alpha).
            if (target >= 0.999f && current >= 0.999f && !_bodyAlphaTarget.ContainsKey(netId))
                _bodyAlphaCurrent.Remove(netId);
            else
                _bodyAlphaCurrent[netId] = current;
        }
    }

    /// <summary>
    /// (R1+R2) Central per-frame drive for every entity node — replaces the per-node <see cref="EntityNode"/>
    /// <c>_Process</c> callback (the dominant render-submission / interop tax at ~150 entities × high fps). One
    /// pass over <see cref="_entityNodes"/> that:
    /// <list type="bullet">
    /// <item>(§12.8) computes the DP-faithful PVS visibility — the entity's BOUNDS (origin ±
    ///       <c>r_pvs_cull_entities_margin</c>) vs the camera's visible cluster set — and routes it through
    ///       <see cref="EntityNode.SetPvsVisible"/> so it ANDs with the gameplay ghost/fade flag (neither owner
    ///       clobbers the other). DP draws an entity only when its leaf cluster is in the view's visible set;</item>
    /// <item>(R2) skips the transform sync entirely for an effectively-hidden node — Godot does NOT gate
    ///       processing on <c>Visible</c>, so a culled entity behind a wall would otherwise still pay the full
    ///       marshalled transform write every frame. A hidden→visible regain force-re-syncs (see
    ///       <see cref="EntityNode.DriveSync"/> / <see cref="EntityNode.ForceSync"/>);</item>
    /// <item>(R1) for a visible node, drives the dirty-gated <see cref="EntityNode.DriveSync"/> — which itself
    ///       skips the interop writes when nothing the sync reads (origin/yaw/pitch/roll/scale, or the bob clock
    ///       while <c>ItemAnimate != 0</c>) changed.</item>
    /// </list>
    /// PVS culling is gated behind <c>r_pvs_cull_entities</c> (default 1); when off, every node is visible and
    /// simply syncs. Conservative: the LOCAL player and an unvised map / camera-in-solid keep everything visible
    /// (wrongly culling a visible enemy is far worse than under-culling one solidly behind a wall).
    /// </summary>
    private void DriveEntityNodes(int localId)
    {
        bool pvsEnabled = CvarF("r_pvs_cull_entities", 1f) != 0f && Pvs is { HasVis: true };

        int viewerCluster = -1;
        bool showAll = true;
        NVec3 margin = default;
        if (pvsEnabled)
        {
            NVec3? view = ViewOrigin();
            viewerCluster = view is { } v ? Pvs!.LeafCluster(Pvs.FindLeaf(v)) : -1;
            showAll = viewerCluster < 0; // camera in solid / outside the tree → show everything
            float m = Mathf.Max(CvarF("r_pvs_cull_entities_margin", 64f), 0f);
            margin = new NVec3(m, m, m);
        }

        // ACTIVE portal exit viewpoints (PortalRenderer.ActiveExitViewsQuake): SetPvsVisible(false) hides the
        // node from EVERY viewport sharing the World3D, so an entity standing at a portal's exit must stay
        // visible while that portal renders — union its cluster in, exactly like WorldPvsCuller's cell pass.
        _entityPvsExtraClusters.Clear();
        var portalViews = VortexArena.Game.Client.PortalRenderer.ActiveExitViewsQuake;
        for (int i = 0; i < portalViews.Count; i++)
        {
            int pc = Pvs!.LeafCluster(Pvs.FindLeaf(portalViews[i]));
            if (pc >= 0 && !_entityPvsExtraClusters.Contains(pc))
                _entityPvsExtraClusters.Add(pc);
        }

        foreach (var kv in _entityNodes)
        {
            EntityNode node = kv.Value;
            Entity? e = node.Entity;
            if (e is null || e.IsFreed || !GodotObject.IsInstanceValid(node))
                continue;
            // DP-faithful PVS visibility. Local player + unvised map / camera-in-solid stay visible (wrongly
            // culling a visible enemy is far worse than under-culling one solidly behind a wall). Bounds are a
            // generous symmetric box around the networked origin; an active warpzone exit unions its extra
            // clusters in (main) so an entity standing at a portal's exit isn't hidden from the portal view.
            bool pvsVis = true;
            if (pvsEnabled && !showAll && e.Index != localId)
            {
                NVec3 o = e.Origin;
                pvsVis = Pvs!.BoxAnyClusterVisibleFrom(viewerCluster, o - margin, o + margin);
                for (int i = 0; i < _entityPvsExtraClusters.Count && !pvsVis; i++)
                    pvsVis = Pvs!.BoxAnyClusterVisibleFrom(_entityPvsExtraClusters[i], o - margin, o + margin);
            }
            node.SetPvsVisible(pvsVis);

            // R1/R2: only an effectively-visible node pays the transform sync; a hidden one re-syncs on regain.
            if (node.EffectiveVisible)
                node.DriveSync();
            else
                node.ForceSync();
        }
    }

    // Scratch for ApplyEntityPvsCull's portal-exit cluster union (rebuilt per frame; empty when no portal renders).
    private readonly List<int> _entityPvsExtraClusters = new();

    /// <summary>
    /// Port of the per-frame CSQCModel PreDraw hooks (<c>CSQCModel_Hook_PreDraw</c>, csqcmodel_hooks.qc:674) for
    /// every rendered entity: the appearance/glowmod + death-fade pass on player models (<see cref="ModelTint"/>),
    /// the LOD distance pick (<see cref="CsqcModelLod"/>, best-effort — see note), and the EF_*/MF_* effects pass
    /// (<see cref="CsqcModelEffects"/>). Mirrors the QC order: appearance → LOD → effects.
    /// </summary>
    private void DriveCsqcModelHooks(float delta)
    {
        if (_entityNodes.Count == 0 && _playerModels.Count == 0)
            return;

        float frameTime = Mathf.Clamp(delta, 0f, 0.1f); // QC bound(0, frametime, 0.1) used for the particle bursts
        NVec3? viewOrigin = ViewOrigin();

        // LOD cvars are frame-invariant — read them ONCE here instead of per entity inside ApplyLod (it ran
        // ~4 cvar lookups × every entity × every frame, all for a result the port currently discards). cfg seta
        // overrides the qh inline defaults (768/2304) with 1024/3072 at runtime.
        int lodPlayerDR = (int)CvarF("cl_playerdetailreduction", 4f);
        int lodModelDR = (int)CvarF("cl_modeldetailreduction", 1f);
        float lodDist1 = CvarF("cl_loddistance1", 1024f);
        float lodDist2 = CvarF("cl_loddistance2", 3072f);
        // Weapon-item duotone toggle — frame-invariant, read ONCE (the resting-tint pass runs per item).
        bool weaponItemColors = CvarF("cl_weapon_item_colors", 1f) != 0f;
        // Ghost / weapon-stay colormods, same story: CvarColormod does a GetString + a token split + a float[]
        // per call, and DriveItemGhostFx/DriveItemStayFx run per ITEM per FRAME — ~100-150 gen0 allocations a
        // frame with 20-30 items awaiting respawn, almost all of it discarded by ApplyItemTint's change gate.
        _ghostColorThisFrame = CvarColormod("cl_ghost_items_color", new Godot.Vector3(-1f, -1f, -1f));
        _stayColorThisFrame = CvarColormod("cl_weapon_stay_color", new Godot.Vector3(2f, 0.5f, 0.5f));

        foreach (var kv in _entityNodes)
        {
            EntityNode node = kv.Value;
            Entity? e = node.Entity;
            if (e is null || e.IsFreed || !GodotObject.IsInstanceValid(node))
                continue;

            CsqcState st = CsqcStateFor(e);

            // (1) APPEARANCE (player models only): the FORCECOLORS reassignment + glowmod from the colormap +
            //     the cl_deathglow death fade. MUST run before LOD in QC; here LOD is a no-op swap so order is
            //     moot, but we keep it first.
            if (st.IsPlayerModel)
            {
                bool dead = IsDeadModel(e);
                // Capture the death instant the first frame we observe it dead (death_time isn't networked).
                if (dead && !st.WasDead)
                    st.DeathTime = Now();
                st.WasDead = dead;
                bool ghost = (e.Effects & CsqcModelEffectFlags.CSQCMODEL_EF_RESPAWNGHOST) != 0;
                // FORCECOLORS: the cl_forceplayercolors family can reassign this.colormap (else keep e.Team).
                int colormap = ResolveForcedColormap(e);
                // Tint the CACHED, flattened mesh list (3.2-2) instead of re-walking node.GetChildren() every
                // frame. This is the SAME cache the effects pass uses below (keyed on st.Effects + node), built
                // once and invalidated on model swap / freed mesh (incl. the staggered placeholder→real swap), so
                // the appearance and effects lists can never diverge. Only player models need it, so it's fetched
                // inside this branch — a player model with zero effects never builds it for the effects pass.
                List<MeshInstance3D> meshes = CsqcModelEffects.GetCachedMeshes(st.Effects, node);
                // Change-gated (§11 R8): the uniform pushes only happen when the computed colors or the mesh
                // list changed (colors only move while dead-fading or on a rainbow palette nibble).
                ModelTint.ApplyAppearance(meshes, colormap, dead, st.DeathTime, ghost, ref st.Effects.Tint);

                // QC ENT_CLIENT_STATUSEFFECTS frozen overlay: a frozen player renders icy-blue (the remote analogue
                // of the freeze tint). Multiply the model's colormod toward ice ON TOP of the team appearance just
                // applied, and invalidate the appearance change-gate so the model repaints its real colormod the
                // first frame after it thaws (otherwise the blue would stick, since ApplyAppearance would see its
                // cached White == computed White and skip the push). The status effects are decoded onto the proxy
                // by ClientEntityView from the networked bitmap.
                if (HasStatusEffect(e, StatusEffectsCatalog.Frozen))
                {
                    ModelTint.SetColormod(meshes, FrozenColormod);
                    st.Effects.Tint.Valid = false;
                }
            }
            else if (IsCtfFlagModel(e))
            {
                // playtest-bugs #8: the CTF flag banner (banner.tga + banner_shirt.tga) auto-compiles to the
                // team-colorable PlayerSkinShader with shirt_color defaulting to black (→ the flag reads gray).
                // Drive the team colormap onto it exactly like a player so the banner shirt-tints red/blue/yellow/
                // pink (and the glow picks up the team glowmod). Base: flag.colormap |= RENDER_COLORMAPPED
                // (sv_ctf.qc:1387-1389); the networked team rides Entity.Team (ClientEntityView). NOTE: Entity.Team
                // is the NUM_TEAM_* color CODE (Red=4/Blue=13/…), but ModelTint.TeamColor wants the colormap low
                // nibble (1=red..4=pink) — passing the raw code rendered a red flag PINK (TeamColor(4)=pink).
                ModelTint.ApplyColormap(node, NormalizeTeamColormap((int)e.Team));
            }

            // Cosmetic add-on layer (csqcmodel_hooks.qc add-ons): the Frozen ice block, the held buff_* glow, and
            // the nade spawn-loc marker. Attach off the EntityNode — it already syncs origin/yaw each frame, so the
            // ice/glow inherit the model's pose for free. The blue freeze tint + the ice block both render in Base,
            // so the SetColormod above stays; this only adds the ice geometry.
            _cosmetics.Drive(e, node);

            // (2) LOD: compute the index (faithful math) — see ApplyLod for the swap caveat.
            ApplyLod(e, st, viewOrigin, lodPlayerDR, lodModelDR, lodDist1, lodDist2);

            // (3) EFFECTS: EF_* lights/particles/render-flags from the networked Effects bitfield. MF_* model
            //     flags aren't networked, so 0 here for remote entities (EF_BRIGHTFIELD trail still applies) —
            //     EXCEPT MF_ROCKET, which the client re-derives from the networked IT_USING_JETPACK bit (QC
            //     common/physics/player.qc:879) and passes via ComposeForcedAppearance below to drive the rocket
            //     trail + looping jetpack-fly sound (csqcmodel_hooks.qc:611/645).
            //     Skip the per-frame material reset for the common no-effects prop UNLESS state is still active
            //     (so the frame effects turn off, we still run once to clear the light/render-flags) — QC sets a
            //     cheap int flag every frame; the port's reset walks meshes, so guard it to avoid churn.
            bool ghostBit = (e.Effects & CsqcModelEffectFlags.CSQCMODEL_EF_RESPAWNGHOST) != 0;
            // QC IT_USING_JETPACK → csqcmodel_modelflags |= MF_ROCKET. Re-derived per player on the client (the
            // Wave-3 ComposeForcedAppearance seam) since MF_* isn't networked. Drives the jetpack-fly loop + trail.
            CsqcModelAppearance.ForcedAppearance forced = e.UsingJetpack
                ? CsqcModelAppearance.ComposeForcedAppearance(strength: false, shield: false, jetpackActive: true, forcedGlowmod: (-1f, -1f, -1f))
                : default;
            bool effectsActive = e.Effects != 0 || ghostBit || st.Effects.LoopChannel != 0
                                 || e.UsingJetpack          // start the jetpack loop the frame the bit turns on
                                 || st.Effects.LastEffects != 0; // re-run ONE frame after effects clear so the
                                                                 // render-flags/visibility/light all reset (no stick)
            if (effectsActive)
            {
                // CSQCModel_Effects_Apply returns the trail-effect name for this model (csqcmodel_hooks.qc:611/554):
                // EF_BRIGHTFIELD -> TR_NEXUIZPLASMA, and the client-re-derived jetpack MF_ROCKET -> TR_ROCKET. Entities
                // in _entityNodes are non-projectiles (players/items/monsters) — projectiles own their trail via
                // ProjectileRenderer — but a player MODEL with EF_BRIGHTFIELD (and a jetpacking player's rocket plume)
                // DOES trail in Base, so route the return through the faithful per-segment trail like the projectile path.
                string? tref = CsqcModelEffects.Apply(Effects, node, e, st.Effects, modelFlags: 0, frameTime, Api.Services?.Sound, ghostBit, forced);
                DriveModelTrail(e, st, tref, frameTime);
            }

            // (4) ITEM VISIBILITY FX (item-only — these flags are never set on players/monsters): QC ItemDraw's
            //     alpha pass (client/items/items.qc:144-210). QC sets a base alpha from the DISTANCE fade
            //     (g_items_maxdist/cl_items_fadedist) FIRST, then multiplies the availability (ghost) / weapon-stay /
            //     expiring (despawn) factors on top. The port composes the same way: distFade is the base, the
            //     ghost/despawn fade multiplies onto it. An available, non-expiring item that is also un-faded by
            //     distance resets to opaque once (no per-frame churn).
            if (IsItemEntity(e))
            {
                float distAlpha = ItemDistanceAlpha(e, viewOrigin); // QC lines 144-158 base alpha
                if (e.ItemExpiringFx)
                {
                    DriveItemDespawnFx(e, node, st, distAlpha);
                    st.ItemFaded = true;
                }
                else if (!e.ItemAvailable)
                {
                    DriveItemGhostFx(node, st, distAlpha);
                    st.ItemFaded = true;
                }
                else if (e.ClassName.StartsWith("weapon_", System.StringComparison.OrdinalIgnoreCase)
                         && (e.Effects & CsqcModelEffectFlags.EF_STARDUST) != 0)
                {
                    // QC ItemDraw weapon-stay branch (client/items/items.qc): a still-pickable g_weapon_stay
                    // ghost (Item_Show mode 0 set EF_STARDUST + cleared the live marker server-side; it stays
                    // AVAILABLE so it falls through the ghost branch above) renders translucent at
                    // cl_weapon_stay_alpha (0.75) tinted by the cl_weapon_stay_color ('2 0.5 0.5') colormod
                    // (playtest #29). The stay-marker isn't networked, so we infer it from the weapon_ classname
                    // + the server-set EF_STARDUST that ONLY a weapon-stay ghost carries (Item_Show ll.531).
                    DriveItemStayFx(node, st, distAlpha);
                    st.ItemFaded = true;
                }
                else if (distAlpha < 0.999f)
                {
                    // Available but distance-faded: apply only the distance alpha (QC's available branch keeps the
                    // base alpha, colormod 1, no ghost/stay multiply). Hidden once fully faded out past fade_end.
                    SetTreeTransparency(node, st, 1f - distAlpha);
                    ApplyRestingItemTint(e, node, st, weaponItemColors); // weapon duotone (or colormod 1) — QC available branch
                    node.SetGameplayVisible(distAlpha > 0.001f);
                    st.ItemFaded = true;
                }
                else if (st.ItemFaded)
                {
                    // Back to available + in-range — undo the prior ghost/despawn/distance fade exactly once.
                    SetTreeTransparency(node, st, 0f);
                    ApplyRestingItemTint(e, node, st, weaponItemColors);
                    node.SetGameplayVisible(true);
                    st.ItemFaded = false;
                }
                else
                {
                    // Resting (available, in-range, no fx): keep the weapon duotone painted — the dropper's
                    // shirt/pants on a thrown weapon, the HUD-icon-derived base/highlight on a map pickup.
                    // Change-gated inside, so this is a no-op after the first application.
                    ApplyRestingItemTint(e, node, st, weaponItemColors);
                }
            }

            // (5) STATUS-EFFECT BURNING: the flame visual is now driven by the networked EF_FLAME .effects bit
            //     that BurningTick ORs in server-side (burning.qc:21) and CsqcModelEffects.Apply renders (the
            //     orange light + the EF_FLAME particle burst, step 3 above) — exactly like spawnshield/stunned.
            //     The former per-frame client-side EF_FLAME stand-in here was removed so the burst isn't emitted
            //     twice; the frozen tint still rides the appearance pass above.
        }
    }

    /// <summary>
    /// Render a still-pickable <c>g_weapon_stay</c> ghost as the translucent stay-weapon -- QC <c>ItemDraw</c>'s
    /// weapon-stay branch (client/items/items.qc): <c>alpha *= cl_weapon_stay_alpha</c> (default 0.75) and
    /// <c>colormod = cl_weapon_stay_color</c> ('2 0.5 0.5', the reddish overbright tint) while the item stays
    /// AVAILABLE (a stay weapon is pickable, just gives no ammo). Composed multiplicatively with the distance
    /// fade, exactly like <see cref="DriveItemGhostFx"/> does for the unavailable ghost. (playtest #29)
    /// </summary>
    private void DriveItemStayFx(EntityNode node, CsqcState st, float distAlpha = 1f)
    {
        float stay = Mathf.Clamp(CvarF("cl_weapon_stay_alpha", 0.75f), 0f, 1f); // xonotic-client.cfg default 0.75
        float alpha = stay * Mathf.Clamp(distAlpha, 0f, 1f); // QC: alpha = distFade; then alpha *= cl_weapon_stay_alpha
        SetTreeTransparency(node, st, 1f - alpha);
        ApplyItemTint(node, st, ItemTintSpec.ForColormod(_stayColorThisFrame));
        node.SetGameplayVisible(alpha > 0.001f);
    }

    /// <summary>
    /// Render a picked-up item awaiting respawn as the faded "ghost" — QC ItemDraw's <c>!ITS_AVAILABLE</c> branch
    /// (client/items/items.qc:182-186): <c>alpha *= cl_ghost_items</c> (default 0.45) and
    /// <c>colormod = glowmod = cl_ghost_items_color</c> (default <c>'-1 -1 -1'</c> — a REAL negative colormod:
    /// DP multiplies the surface color and clamps, so the shipped default renders the ghost as a translucent
    /// near-BLACK silhouette; '0 0 0' is the "leave color unchanged" sentinel, per the cfg description).
    /// <c>cl_ghost_items</c> 0 hides it entirely (QC <c>alpha 0 → drawmask 0</c>). Alpha rides the per-instance
    /// transparency, the tint an <see cref="ApplyItemTint"/> colormod spec — neither touches
    /// the shared/cached item materials; the bob+spin keeps running (driven in EntityNode). (playtest #29)
    /// </summary>
    private void DriveItemGhostFx(EntityNode node, CsqcState st, float distAlpha = 1f)
    {
        float ghost = Mathf.Clamp(CvarF("cl_ghost_items", 0.45f), 0f, 1f); // QC autocvar_cl_ghost_items default 0.45
        float alpha = ghost * Mathf.Clamp(distAlpha, 0f, 1f); // QC: alpha = distFade; then alpha *= cl_ghost_items
        SetTreeTransparency(node, st, 1f - alpha);
        ApplyItemTint(node, st, ItemTintSpec.ForColormod(_ghostColorThisFrame));
        node.SetGameplayVisible(alpha > 0.001f);
    }

    /// <summary>
    /// Drive one frame of a loot item's despawn animation (QC <c>ItemDraw</c>'s <c>ITS_EXPIRING</c> branch,
    /// client/items/items.qc:191-210): fade the item node's alpha and emit the accelerating
    /// <c>EFFECT_ITEM_DESPAWN</c> puffs at <c>origin + (0,0,16)</c>. Honors <c>cl_items_animate</c> bit 2 (fade)
    /// and bit 4 (particles); the pure timing/bit logic lives in <see cref="ItemDespawnFx"/> so it's unit-tested.
    /// </summary>
    private void DriveItemDespawnFx(Entity e, EntityNode node, CsqcState st, float distAlpha = 1f)
    {
        ItemDespawnFx fx = st.Despawn ??= new ItemDespawnFx();
        int animate = (int)CvarF("cl_items_animate", 7f); // xonotic-client.cfg default 7 (bob+fade+particles)
        fx.Tick(Now(), animate, out float despawnAlpha, out bool emitPuff);

        // QC: alpha = distFade (base); then alpha *= (wait - time)/IT_DESPAWNFX_TIME — apply as a per-instance
        // transparency on the model (animate bit 2). Bit 2 clear → Tick returns 1, so only the distance fade
        // (if any) applies; both clear → opaque, just particles.
        float alpha = despawnAlpha * Mathf.Clamp(distAlpha, 0f, 1f);
        SetTreeTransparency(node, st, 1f - alpha);
        node.SetGameplayVisible(alpha > 0.001f); // hidden once fully faded (QC drawmask 0); transparency handles the gradient

        // QC: pointparticles(EFFECT_ITEM_DESPAWN, this.origin + '0 0 16', '0 0 0', 1) — same client path the
        // pickup/respawn bursts take (EffectSystem.Spawn); item_despawn resolves from effectinfo.txt.
        if (emitPuff)
            Effects.Spawn("ITEM_DESPAWN", e.Origin + new NVec3(0f, 0f, 16f));
    }

    /// <summary>
    /// QC ItemDraw's distance alpha-fade (client/items/items.qc:144-158): an item past the server's
    /// <c>g_items_maxdist</c> (the per-item networked <c>fade_end</c>; default 4500) is fully hidden, and within the
    /// last <c>cl_items_fadedist</c> (default 500) units before that it linearly fades to zero. The port doesn't
    /// network the per-item <c>fade_end</c>, so we read <c>g_items_maxdist</c> as the effective value (matching the
    /// server's <c>if(!fade_end) fade_end = autocvar_g_items_maxdist</c>, items.qc:1051) — uniform across items, the
    /// stock case. Returns the fade alpha in [0,1] (1 = no fade / disabled), to be composed multiplicatively with
    /// the availability (ghost) and expiring (despawn) fades — exactly as QC sets <c>this.alpha</c> here FIRST and
    /// then multiplies the ghost/stay/despawn factors on top. Returns 1 when the fade is disabled
    /// (<c>fade_end &lt;= 0</c>) or no view origin is available. The bbox-centre offset matches QC's
    /// <c>vlen(org - this.origin - 0.5*(mins+maxs))</c>.
    /// </summary>
    private float ItemDistanceAlpha(Entity e, NVec3? viewOrigin)
    {
        // fade_end: the per-item networked value isn't carried on the port wire, so use g_items_maxdist (the
        // server's fallback when fade_end is 0). 0 / unset → no distance fade (QC: `if(this.fade_end ...)`).
        float fadeEnd = CvarF("g_items_maxdist", 4500f);
        if (fadeEnd <= 0f || viewOrigin is not { } org)
            return 1f;

        // QC vdist(org - this.origin, >, fade_end): a cheap reject on the raw origin distance.
        float originDist = (org - e.Origin).Length();
        if (originDist > fadeEnd)
            return 0f; // fully past the draw distance → hidden (QC alpha = 0)

        float fadeDist = CvarF("cl_items_fadedist", 500f); // xonotic-client.cfg default 500; 0 disables fading
        if (fadeDist <= 0f)
            return 1f;

        float fadeStart = Mathf.Max(500f, fadeEnd - fadeDist); // QC: max(500, fade_end - cl_items_fadedist)
        if (originDist <= fadeStart)
            return 1f; // inside the solid-draw radius

        // QC: bound(0, (fade_end - vlen(org - origin - 0.5*(mins+maxs))) / (fade_end - fade_start), 1).
        NVec3 bboxCentre = e.Origin + 0.5f * (e.Mins + e.Maxs);
        float centreDist = (org - bboxCentre).Length();
        float denom = fadeEnd - fadeStart;
        if (denom <= 0f)
            return 1f; // degenerate band → no gradient (treat as solid; the > fade_end reject already hid the far ones)
        return Mathf.Clamp((fadeEnd - centreDist) / denom, 0f, 1f);
    }

    /// <summary>
    /// Apply a whole-model transparency (0 = opaque, 1 = invisible) to every mesh of an item node — the
    /// render-side analogue of QC's per-entity <c>.alpha</c>. Uses <see cref="GeometryInstance3D.Transparency"/>
    /// (a per-instance property, NOT a material edit) so it never mutates the shared/cached item materials.
    /// (§11 R9) Pushes through the SAME cached flattened mesh list the appearance/effects passes use
    /// (3.2-2) instead of a recursive <c>GetChild</c> walk per fading item per frame, and change-gates on the
    /// last-applied value (the ghost fade is a constant — one push, not one per frame). Child <c>Visible</c> is
    /// left untouched (other systems own it); the caller hides the root node outright once fully faded.
    /// </summary>
    private static void SetTreeTransparency(EntityNode node, CsqcState st, float transparency)
    {
        transparency = Mathf.Clamp(transparency, 0f, 1f);
        List<MeshInstance3D> meshes = CsqcModelEffects.GetCachedMeshes(st.Effects, node);
        if (st.ItemFadeApplied == transparency && st.ItemFadeMeshCount == meshes.Count)
            return;
        for (int i = 0; i < meshes.Count; i++)
            meshes[i].Transparency = transparency;
        st.ItemFadeApplied = transparency;
        st.ItemFadeMeshCount = meshes.Count;
    }

    /// <summary>The kind of paint an item wants this frame — the single input to <see cref="ApplyItemTint"/>.</summary>
    private enum ItemTintKind : byte
    {
        None,     // stock look (shared materials live, identity uniforms)
        Colormod, // QC .colormod/.glowmod multiply (ghost / weapon-stay fx tints)
        Duotone,  // the weapon two-tone paint (dropper colors / HUD-icon colors)
    }

    /// <summary>The desired item look, as one value: compare-for-equality is the whole change gate.</summary>
    private readonly record struct ItemTintSpec(ItemTintKind Kind, Godot.Vector3 Colormod, ItemPaint Paint)
    {
        public static readonly ItemTintSpec None = new(ItemTintKind.None, Godot.Vector3.One, default);

        /// <summary>QC/DP semantics: '0 0 0' means "leave the color unchanged", '1 1 1' is identity → None.</summary>
        public static ItemTintSpec ForColormod(Godot.Vector3 c)
            => c == Godot.Vector3.Zero || c == Godot.Vector3.One ? None : new(ItemTintKind.Colormod, c, default);

        public static ItemTintSpec ForDuotone(in ItemPaint p)
            => new(ItemTintKind.Duotone, Godot.Vector3.One, p);
    }

    /// <summary>
    /// The ONE owner of an item's paint. Callers declare the desired look (<see cref="ItemTintSpec"/>) and this
    /// derives ALL THREE render channels from it — the <see cref="PlayerSkinShader"/> instance uniforms, the
    /// per-surface override variants, and the whole-mesh dark-flat MaterialOverride — behind a single change
    /// gate (spec + mesh-list identity), so no caller can update one channel and forget the others (the
    /// stuck-tint bug class that fired three times while this machinery was split across two methods).
    ///
    /// <para>Channel rules: a DARK colormod (max clamped channel ≤ <see cref="GhostDarkMax"/>, e.g. the shipped
    /// <c>cl_ghost_items_color '-1 -1 -1'</c>) renders as DP does — a flat silhouette — via a PER-ENTITY
    /// unshaded MaterialOverride (per-entity because CsqcModelEffects' render-flag setters mutate any
    /// BaseMaterial3D MaterialOverride in place; properties re-asserted each push to heal that; and
    /// MaterialOverride because it is the one channel that survives ModelAnimator's in-place surface rebuilds).
    /// A BRIGHT colormod multiplies per-surface BaseMaterial3D variants (ShaderMaterial surfaces stay live —
    /// flattening them would erase texture/animation). The duotone pushes the four instance uniforms (the
    /// channel skin-shader weapon materials actually read) plus BaseMaterial3D duotone variants. Foreign
    /// MaterialOverrides (the placeholder box, MdlBuilder skins — meshes whose ONLY material lives there) are
    /// never touched. Uniforms are reset to identity whenever the spec is not Duotone.</para>
    /// </summary>
    private static void ApplyItemTint(EntityNode node, CsqcState st, in ItemTintSpec spec)
    {
        List<MeshInstance3D> meshes = CsqcModelEffects.GetCachedMeshes(st.Effects, node);

        // The flat's SHADING must be re-asserted on EVERY push, before the change gate — not just when the spec
        // changes. CsqcModelEffects.ResetRenderFlags runs each frame while e.Effects != 0 and mutates any
        // BaseMaterial3D MaterialOverride in place, including this one; with g_nodepthtestitems 1 a ghosted item
        // carries EF_NODEPTHTEST forever, so once the gate closed the silhouette was permanently re-lit
        // (SetFullbright(false) → PerPixel) with no path back. Two property writes on one material; the gate
        // below still guards all the per-surface work. NoDepthTest is deliberately NOT re-asserted: that channel
        // reflects the server's g_nodepthtestitems and belongs to CsqcModelEffects, not to the tint.
        if (st.ItemFlatMat is { } liveFlat)
        {
            liveFlat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            liveFlat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
        }

        if (st.ItemTintSpecValid && st.ItemTintSpecApplied == spec
            && st.ItemTintSpecMeshCount == meshes.Count && st.ItemTintSpecFirstMeshId == FirstMeshId(meshes))
            return;

        // Channel 1 — instance uniforms. ONLY touched when a Duotone is being painted or was the previously
        // applied spec (the identity reset that un-paints it). Never write identity onto uniforms we never
        // painted: other systems own that channel on some item-classified entities — the CTF flag's per-frame
        // team ApplyColormap (step 1 of this same loop) would be overwritten to a gray banner on every frame
        // this gate re-opens (spawn, model stream-in).
        bool prevWasDuotone = st.ItemTintSpecValid && st.ItemTintSpecApplied.Kind == ItemTintKind.Duotone;
        if (spec.Kind == ItemTintKind.Duotone)
            ModelTint.Apply(meshes, spec.Paint.Colormod, spec.Paint.Glowmod, spec.Paint.Shirt, spec.Paint.Pants);
        else if (prevWasDuotone)
            ModelTint.Apply(meshes, ModelTint.White, ModelTint.White, ModelTint.Black, ModelTint.Black);

        // Channel 2 — the dark-flat whole-mesh override (DP black-silhouette ghosts).
        float cr = Mathf.Max(spec.Colormod.X, 0f), cg = Mathf.Max(spec.Colormod.Y, 0f), cb = Mathf.Max(spec.Colormod.Z, 0f);
        bool darkFlat = spec.Kind == ItemTintKind.Colormod && MathF.Max(cr, MathF.Max(cg, cb)) <= GhostDarkMax;
        StandardMaterial3D? flat = null;
        if (darkFlat)
        {
            flat = st.ItemFlatMat ??= new StandardMaterial3D();
            flat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            flat.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
            flat.AlbedoColor = new Color(cr, cg, cb, 1f);
        }

        for (int i = 0; i < meshes.Count; i++)
        {
            MeshInstance3D mi = meshes[i];
            if (mi.Mesh is not Mesh mesh)
                continue;
            // Only touch MaterialOverride slots we own (null or our own flat) — some meshes carry their ONLY
            // material there (the unresolved-model placeholder box, MdlBuilder skins); replacing or nulling
            // those would strip them to Godot's default white. Such meshes keep their material (alpha still
            // applies), exactly the pre-duotone behavior.
            if (mi.MaterialOverride is null || ReferenceEquals(mi.MaterialOverride, st.ItemFlatMat))
                mi.MaterialOverride = darkFlat ? flat : null;

            // Channel 3 — per-surface override variants.
            int surfaces = mesh.GetSurfaceCount();
            for (int s = 0; s < surfaces; s++)
            {
                Material? ov = spec.Kind switch
                {
                    ItemTintKind.Duotone => DuotoneVariant(mesh.SurfaceGetMaterial(s),
                        spec.Paint.OverrideBase, spec.Paint.OverrideEmission),
                    ItemTintKind.Colormod when !darkFlat => TintVariant(mesh.SurfaceGetMaterial(s), spec.Colormod),
                    _ => null, // None, or darkFlat (the flat MaterialOverride carries the look)
                };
                mi.SetSurfaceOverrideMaterial(s, ov);
            }
        }

        st.ItemTintSpecApplied = spec;
        st.ItemTintSpecValid = true;
        st.ItemTintSpecMeshCount = meshes.Count;
        st.ItemTintSpecFirstMeshId = FirstMeshId(meshes);
    }

    /// <summary>Mesh-list identity fingerprint for the item tint change gate: first instance id (0 = empty).
    /// Combined with the count it catches a same-count model swap (ModelTint.TintCache pattern).</summary>
    private static ulong FirstMeshId(List<MeshInstance3D> meshes)
        => meshes.Count > 0 ? meshes[0].GetInstanceId() : 0;

    /// <summary>
    /// Paint an item's RESTING tint — what an available, un-fx'd pickup looks like. Weapons get the two-tone
    /// paint (<see cref="TryGetItemDuotone"/>): a dropped weapon keeps its DROPPER's shirt/pants until it
    /// despawns or is picked up (the next owner's drop then carries THEIR colors), a map-spawned weapon wears
    /// its HUD-icon-derived base/highlight. Everything else resets to the untinted shared material. Both
    /// setters are change-gated, so calling this per frame is a no-op after the first paint.
    /// </summary>
    private static void ApplyRestingItemTint(Entity e, EntityNode node, CsqcState st, bool colorsOn)
    {
        ApplyItemTint(node, st, TryGetItemDuotone(e, st, colorsOn, out ItemPaint paint)
            ? ItemTintSpec.ForDuotone(paint)
            : ItemTintSpec.None);
    }

    /// <summary>
    /// The full two-channel weapon-item paint: the four <see cref="PlayerSkinShader"/> instance uniforms
    /// (the channel that reaches skin-shader weapon materials — g_*.md3 textures ship _shirt/_reflect/_glow
    /// siblings, so they compile to <see cref="PlayerSkinShader"/> and IGNORE surface overrides) plus the
    /// base/emission pair for the plain-BaseMaterial3D surface-override fallback. Both are pushed; each
    /// no-ops on the material kind it doesn't apply to.
    /// </summary>
    private readonly record struct ItemPaint(
        Color Colormod, Color Glowmod, Color Shirt, Color Pants, // instance uniforms (skin-shader surfaces)
        Color OverrideBase, Color OverrideEmission);             // BaseMaterial3D override variant (fallback)

    /// <summary>
    /// Resolve the two-tone paint for a weapon pickup, if it gets one (<c>cl_weapon_item_colors</c>, default on).
    /// A DROPPED weapon (RENDER_COLORMAPPED colormap inherited from the thrower — WeaponThrowing) keeps the
    /// dropper's colors: highlight = pants palette color (the same nibble DP feeds glowmod), base = the shirt
    /// color darkened for contrast. A MAP-SPAWNED <c>weapon_*</c> pickup derives its pair from the weapon's
    /// HUD-icon color (<see cref="Weapon.Color"/> — the registry RGB generated from the icon art): highlight =
    /// the icon color itself, base = a darker, slightly desaturated version of it (e.g. devastator orange-yellow
    /// over dark amber, crylink purple over dark plum). Non-weapon items get no duotone.
    /// </summary>
    private static bool TryGetItemDuotone(Entity e, CsqcState st, bool colorsOn, out ItemPaint paint)
    {
        paint = default;
        if (!colorsOn)
        {
            st.PaintKeyValid = false; // cvar back on → recompute
            return false;
        }

        // Per-entity resolved-paint cache: the paint is a pure function of (ItemWeaponId, Model,
        // ColorMapOverride), all of which change rarely — so the per-frame resting pass costs three compares
        // here instead of a registry probe + palette/HSV math + struct build discarded by the gate downstream.
        if (st.PaintKeyValid && st.PaintKeyCmap == e.ColorMapOverride && st.PaintKeyWeapon == e.ItemWeaponId
            && (ReferenceEquals(st.PaintKeyModel, e.Model) || st.PaintKeyModel == e.Model))
        {
            paint = st.PaintCached;
            return st.PaintHas;
        }
        bool has = ResolveItemDuotone(e, out paint);
        st.PaintKeyValid = true;
        st.PaintKeyModel = e.Model;
        st.PaintKeyCmap = e.ColorMapOverride;
        st.PaintKeyWeapon = e.ItemWeaponId;
        st.PaintHas = has;
        st.PaintCached = paint;
        return has;
    }

    private static bool ResolveItemDuotone(Entity e, out ItemPaint paint)
    {
        paint = default;
        // WEAPON pickups only. Preferred identity: the networked weapon registry id (Entity.ItemWeaponId,
        // protocol v17 — explicit, survives a mapper overriding the pickup's model). Fallback: match the item
        // MODEL filename against Weapon.ItemModel (proxies predating the decode, old demos). Ammo/health/
        // armor/powerups — and colormapped map PROPS, which also arrive with the Item kind — keep their
        // stock look. Either way ONE weapon def is resolved and the icon color derives from it in one place.
        Weapon? weapon = e.ItemWeaponId >= 0 && e.ItemWeaponId < VortexArena.Common.Gameplay.Weapons.All.Count
            ? VortexArena.Common.Gameplay.Weapons.ById(e.ItemWeaponId)
            : WeaponForModel(e.Model);
        if (weapon is not { } w)
            return false;
        var icon = new Color(w.Color.X, w.Color.Y, w.Color.Z);

        // Dropped loot carrying the thrower's packed colormap (1024 + (shirt<<4) + pants | BIT(10)) — set by
        // the server at throw time and never rewritten, so the paint stays the DROPPER's until pickup/despawn.
        if ((e.ColorMapOverride & Entity.RenderColormapped) != 0)
        {
            int lo = e.ColorMapOverride & 0x0F;
            int hi = (e.ColorMapOverride >> 4) & 0x0F;
            // Fixed t=0: the animated rainbow palette nibble would otherwise change color every frame,
            // regrowing a paint (and a material variant) per frame — a dropped weapon keeps ONE frozen
            // paint, which also matches "the colors they had when dropped".
            (float sr, float sg, float sb) = CsqcModelAppearance.ColormapPaletteColor(hi, isPants: false, 0f);
            (float pr, float pg, float pb) = CsqcModelAppearance.ColormapPaletteColor(lo, isPants: true, 0f);
            var shirt = new Color(sr, sg, sb);
            var pants = new Color(pr, pg, pb);
            // Skin-shader channel = Base's dropped-loot look exactly (csqcmodel colormap apply): shirt/pants
            // masks tinted with the dropper's palette colors, glowmod = the pants color, no darkening.
            // Plain-material fallback = the duotone contrast pair (dark shirt base, pants emission).
            paint = new ItemPaint(ModelTint.White, pants, shirt, pants,
                DarkenForBase(shirt), pants);
            return true;
        }

        // Map-spawned weapon pickup → HUD-icon-derived duotone: the whole diffuse is multiplied toward the
        // dark desaturated BASE (colormod), while the glow texture + the team-colorable shirt/pants mask
        // accents carry the HIGHLIGHT (the icon color itself).
        // Weapon.Color is a plain field defaulting to Vector3.Zero and not every def sets one (BallStealer
        // ships without it), so guard HERE rather than at the top: a colorless weapon would paint colormod
        // (0,0,0) = pure black, strictly worse than the stock look. The dropped-loot path above is deliberately
        // outside this guard — its colors come from the DROPPER, so it works for a colorless weapon too.
        if (w.Color == System.Numerics.Vector3.Zero)
            return false;
        Color baseCol = DarkenForBase(icon);
        paint = new ItemPaint(baseCol, icon, icon, icon, baseCol, icon);
        return true;
    }

    // Ghost / weapon-stay colormods, hoisted once per frame in the entity-drive pass (see the read site).
    // Only consumed by DriveItemGhostFx/DriveItemStayFx, which run inside that same pass.
    private Godot.Vector3 _ghostColorThisFrame = new(-1f, -1f, -1f);
    private Godot.Vector3 _stayColorThisFrame = new(2f, 0.5f, 0.5f);

    /// <summary>Derive the duotone BASE from a highlight: darker and slightly desaturated, so the highlight
    /// pops against it (the user-spec contrast pair). Applied as a MULTIPLY over already-mid-gray weapon
    /// diffuse textures, so V stays moderate — 0.35 would land near-black in a dim room.</summary>
    private static Color DarkenForBase(Color c)
        => Color.FromHsv(c.H, c.S * 0.8f, c.V * 0.5f);

    /// <summary>Per-model weapon-lookup cache: the item model path (".../g_rl.md3") → the weapon whose
    /// <see cref="Weapon.ItemModel"/> matches by filename (null = not a weapon item model). Keyed on the FULL
    /// networked model path. The FALLBACK identity only — the networked <see cref="Entity.ItemWeaponId"/> is
    /// preferred; the color derivation itself lives with the caller so both paths share it.</summary>
    private static readonly Dictionary<string, Weapon?> _weaponsByItemModel = new();

    private static Weapon? WeaponForModel(string model)
    {
        if (string.IsNullOrEmpty(model))
            return null;
        if (_weaponsByItemModel.TryGetValue(model, out Weapon? cached))
            return cached;
        Weapon? result = null;
        int slash = model.LastIndexOfAny(new[] { '/', '\\' });
        string file = slash >= 0 ? model[(slash + 1)..] : model;
        foreach (Weapon w in VortexArena.Common.Gameplay.Weapons.All)
        {
            if (string.IsNullOrEmpty(w.ItemModel)
                || !string.Equals(w.ItemModel, file, System.StringComparison.OrdinalIgnoreCase))
                continue;
            result = w;
            break;
        }
        _weaponsByItemModel[model] = result;
        return result;
    }

    /// <summary>Cache of duotone BaseMaterial3D variants keyed by (shared source material, base, highlight).
    /// Bounded by the few weapon-item materials × the shipped weapon/player palette; variants hold their
    /// source's textures by reference. Main-thread only (built inside the ClientWorld drive).</summary>
    private static readonly Dictionary<(Material, Color, Color), Material?> _itemDuotoneVariants = new();

    /// <summary>
    /// Get (or build) the duotone duplicate of a plain BaseMaterial3D item surface: albedo multiplied by the
    /// BASE color (gray weapon metal reads as the base hue), emission carrying the HIGHLIGHT — masked by the
    /// surface's own glow texture when it has one (the DP glowmod semantic), otherwise a soft flat accent so
    /// an unlit model still shows the highlight without turning into a slab. Returns null (no override, the
    /// shared material stays live) for ShaderMaterial surfaces — those take the paint through the
    /// <see cref="PlayerSkinShader"/> instance uniforms <see cref="ApplyItemTint"/> pushes alongside.
    /// </summary>
    private static Material? DuotoneVariant(Material? source, Color baseCol, Color highlight)
    {
        if (source is not BaseMaterial3D src)
            return null;
        var key = (source!, baseCol, highlight);
        if (_itemDuotoneVariants.TryGetValue(key, out Material? cached))
            return cached;
        var variant = (BaseMaterial3D)src.Duplicate();
        Color a = src.AlbedoColor;
        variant.AlbedoColor = new Color(a.R * baseCol.R, a.G * baseCol.G, a.B * baseCol.B, a.A);
        if (src.EmissionEnabled)
        {
            variant.Emission = highlight; // the glow texture masks it — the DP glowmod path
        }
        else
        {
            variant.EmissionEnabled = true;
            variant.Emission = highlight;
            variant.EmissionEnergyMultiplier = 0.35f; // flat accent, not a glowing slab
        }
        _itemDuotoneVariants[key] = variant;
        return variant;
    }

    /// <summary>Cache of tinted BaseMaterial3D variants keyed by (shared source material, colormod). Bounded by the
    /// few item materials × the shipped tints; variants hold their source's textures by reference. Main-thread only
    /// (built inside the ClientWorld drive).</summary>
    private static readonly Dictionary<(Material, Godot.Vector3), Material?> _itemTintVariants = new();

    /// <summary>Above this per-channel clamped-multiply value a colormod is NOT a dark ghost — a flat substitute
    /// would erase the shader's texture detail (the stay-weapon '2 0.5 0.5' tint), so we leave the live shader.</summary>
    private const float GhostDarkMax = 0.5f;

    /// <summary>
    /// Get (or build) the tinted duplicate of a shared item surface material. DP applies colormod as a straight
    /// color multiply clamped at 0 — so albedo AND emission (QC sets <c>glowmod</c> to the same vector in the
    /// item branches) are multiplied by <c>max(colormod, 0)</c> per channel; '-1 -1 -1' therefore lands on black.
    /// Returns null (→ no override, shared material stays live) for non-BaseMaterial3D surfaces.
    /// </summary>
    private static Material? TintVariant(Material? source, Godot.Vector3 colormod)
    {
        if (source is not BaseMaterial3D src)
        {
            // [#54] ShaderMaterial (a compiled Q3/glow/skin shader — the POWERUP models are almost entirely
            // these): an arbitrary shader's output can't be colormod-multiplied from here. A DARK tint is now
            // handled BEFORE this by ApplyItemTint's whole-mesh MaterialOverride flat (the DP black-silhouette
            // path), so this only sees BRIGHT tints (the g_weapon_stay 'cl_weapon_stay_color 2 0.5 0.5'
            // still-pickable weapon) — flattening those would erase the model's texture/animation, so leave the
            // live shader (return null = shared material stays live, alpha-only), the least-wrong option for a
            // positive multiply.
            return null;
        }
        var key = (source!, colormod);
        if (_itemTintVariants.TryGetValue(key, out Material? cached))
            return cached;
        float mr = Mathf.Max(colormod.X, 0f), mg = Mathf.Max(colormod.Y, 0f), mb = Mathf.Max(colormod.Z, 0f);
        var variant = (BaseMaterial3D)src.Duplicate();
        Color a = src.AlbedoColor;
        variant.AlbedoColor = new Color(a.R * mr, a.G * mg, a.B * mb, a.A);
        if (variant.EmissionEnabled)
        {
            Color em = src.Emission;
            variant.Emission = new Color(em.R * mr, em.G * mg, em.B * mb, em.A);
        }
        _itemTintVariants[key] = variant;
        return variant;
    }

    /// <summary>
    /// Read an "r g b" vector cvar as a colormod <see cref="Godot.Vector3"/> (QC <c>stov(autocvar_…)</c>) —
    /// negatives preserved (they're meaningful: DP clamps them to black at render time). Falls back to
    /// <paramref name="fallback"/> when unset/unparseable.
    /// </summary>
    private Godot.Vector3 CvarColormod(string name, Godot.Vector3 fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        if (!VortexArena.Common.Framework.VecParse.TryParseFloats(s, min: 3, out float[] v))
            return fallback;
        return new Godot.Vector3(v[0], v[1], v[2]);
    }

    /// <summary>
    /// QC <c>CSQCModel_LOD_Apply</c> index pick for an entity. The port renders a single resolved model node
    /// (not an engine modelindex it can swap by integer), and stock Xonotic ships <c>_lodN</c> variants for only
    /// some models; faithfully resolving + hot-swapping the alternate model file mid-render is out of this task's
    /// in-scope wire/render budget. So this computes the faithful LOD INDEX (so the decision is correct and
    /// testable) but, lacking a resolved alternate model, keeps lod0 — mirroring the QC fexists guard that keeps
    /// the base model when no <c>_lodN</c> file exists. Documented as a known parity gap in the T58 report.
    /// </summary>
    private void ApplyLod(Entity e, CsqcState st, NVec3? viewOrigin, int playerDetailReduction,
        int modelDetailReduction, float dist1, float dist2)
    {
        if (viewOrigin is not { } vo)
            return;
        int detailReduction = st.IsPlayerModel ? playerDetailReduction : modelDetailReduction;
        float distance = (e.Origin - vo).Length();
        // view_quality has no port equivalent (renderer LOD-quality global) → 1; current_viewzoom → 1 here.
        _ = CsqcModelLod.SelectLodIndex(detailReduction, distance, viewZoom: 1f, viewQuality: 1f, dist1, dist2);
        // No alternate LOD model resolved → keep lod0 (the QC fexists-miss path). See doc comment.
    }

    /// <summary>
    /// Emit one frame of a CSQC-model trail (QC <c>CSQCModel_Effects_Apply</c>'s <c>trailparticles</c> continuation,
    /// csqcmodel_hooks.qc:554/611): when the effects pass returned a trail name (EF_BRIGHTFIELD -> TR_NEXUIZPLASMA, or
    /// the client-re-derived jetpack MF_ROCKET -> TR_ROCKET on this player/monster MODEL), spawn a faithful per-segment
    /// trail from the model's previous position to its current one — the same <see cref="EffectSystem.SpawnTrailSegment"/>
    /// path the projectile renderer uses (DP <c>CL_ParticleTrail</c>). The tail anchor is kept per-entity on
    /// <see cref="CsqcState"/> so successive frames join up; it resets the first frame the trail (re)activates so a
    /// spawn or a teleport doesn't draw one long streak. A null/empty trail clears the anchor (the trail stops cleanly).
    /// </summary>
    private void DriveModelTrail(Entity e, CsqcState st, string? tref, float frameTime)
    {
        if (string.IsNullOrEmpty(tref) || frameTime <= 0f)
        {
            st.HasTrailTail = false; // trail off this frame → next activation restarts the tail at the origin
            return;
        }
        NVec3 cur = e.Origin;
        // First active frame (or after a clear): start the segment at the current origin so we don't streak across
        // the whole map from a stale tail (DP seeds trail_pos at the first emission too).
        NVec3 from = st.HasTrailTail ? st.TrailTail : cur;
        Effects.SpawnTrailSegment(tref!, from, cur, e.Velocity);
        st.TrailTail = cur;
        st.HasTrailTail = true;
    }

    /// <summary>The active camera's Quake-space origin (CSQC <c>view_origin</c>) for LOD distance, or null.</summary>
    private NVec3? ViewOrigin()
    {
        Camera3D? cam = FrameCamera();
        if (cam is null)
            return null;
        return Coords.ToQuake(cam.GlobalPosition);
    }

    private CsqcState CsqcStateFor(Entity e)
    {
        if (!_csqc.TryGetValue(e.Index, out CsqcState? st))
        {
            st = new CsqcState { IsPlayerModel = _playerModels.ContainsKey(e.Index) || IsPlayerMd3(e.Index) };
            _csqc[e.Index] = st;
        }
        return st;
    }

    /// <summary>An MD3 animator bound to a player entity (the non-skeletal player path).</summary>
    private bool IsPlayerMd3(int index)
        => _animators.TryGetValue(index, out ModelAnimator? a) && GodotObject.IsInstanceValid(a)
           && a.Entity is { } e && e.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// QC <c>csqcmodel_isdead</c> for the death-fade. Base derives it from <c>IS_DEAD_FRAME(frame)</c> =
    /// <c>(frame==0||frame==1)</c> (csqcmodel_hooks.qc:374/408) — the MD3 death-anim frame convention. The
    /// VortexArena wire does NOT carry that 0/1 death frame for players (the server animates players client-side via
    /// <see cref="PlayerModel"/>/<see cref="SelectClipFromMovement"/>, and <see cref="Entity.Frame"/> is 0 for a
    /// LIVING idle player), so a literal <c>frame==0||frame==1</c> port would false-positive living players as
    /// dead. We instead use the networked DeadState / Health&lt;=0-with-real-MaxHealth, which captures the same
    /// intent faithfully against the port's wire. (Documented T58 parity note — matching Base's frame test here
    /// would reduce fidelity, not raise it.)
    /// </summary>
    private static bool IsDeadModel(Entity e)
        => e.DeadState is DeadFlag.Dying or DeadFlag.Dead || (e.Health <= 0f && e.MaxHealth > 0f);

    /// <summary>
    /// QC FORCECOLORS (csqcmodel_hooks.qc:239-327): the cl_forceplayercolors family can reassign a player's
    /// colormap (force-my-colors / force-enemy-colors / unique-enemy-colors). Returns the colormap to render with
    /// — the forced packed-palette colormap (&gt;=1024) when a force applies, else the entity's own networked team
    /// id (<see cref="Entity.Team"/>). With no <see cref="AppearanceProvider"/> wired (a pure demo) the override is
    /// inert and the entity keeps its own colormap. The pure decision is <see cref="CsqcModelAppearance.ResolveForcedColormap"/>.
    /// </summary>
    /// <summary>
    /// The colormap a player's HELD WEAPON should be tinted with — the same resolved value the player BODY
    /// gets (forcecolors + team normalization), because Base's weaponentity inherits its owner's colormap
    /// verbatim (<c>this.colormap = this.owner.colormap</c>, weaponsystem.qc:180). Public for the weapon
    /// view-entity renderer + the first-person viewmodel (playtest #36: the weapon _shirt/_pants panels sat
    /// at the black no-colormap default forever — desaturated gun panels).
    /// </summary>
    public int PlayerColormap(Entity e) => ResolveForcedColormap(e);

    private int ResolveForcedColormap(Entity e)
    {
        int own = (int)e.Team;
        // [r15 #43] the networked packed clientcolors (16*shirt+pants — FFA profile colors, or the team-forced
        // 17*teamcode). When present, the UNFORCED colormap is the QC-form 1024+colors so ModelTint paints the
        // real shirt/pants palette (Base: .colormap follows .clientcolors); 0 falls back to the team path.
        int packed = e.Colors != 0 ? 1024 + (e.Colors & 0xFF) : 0;
        AppearanceContext? ctx = AppearanceProvider?.Invoke();
        if (ctx is null)
            return packed != 0 ? packed : NormalizeTeamColormap(own);

        // QC cl_survival.qc NET_HANDLE colormap override + ForcePlayercolors_Skip (CBC_ORDER_LAST): once a Survival
        // round is live/resolved, every known player wears the hardcoded survival palette — red if the client knows
        // them to be a hunter (own side mid-round; everyone at round end), green otherwise. This runs LAST in QC so
        // it overrides cl_forceplayercolors; we honor that here by returning BEFORE the force-color resolution.
        if (ctx.SurvivalActive)
        {
            bool hunter = ctx.SurvivalHunterIds is { } ids && ids.Contains(e.Index);
            return 1024 + (hunter ? SurvColorHunter : SurvColorPrey);
        }

        int fpc = (int)CvarF("cl_forceplayercolors", 0f);
        bool enabled = CsqcModelAppearance.ForcePlayerColorsEnabled(fpc, ctx.Is1v1, ctx.Teamplay, ctx.TeamCount, ctx.MyTeam);
        int forceMyColors = (int)CvarF("cl_forcemyplayercolors", 0f);
        int clColor = (int)CvarF("_cl_color", 0f);
        bool forceUnique = CvarF("cl_forceuniqueplayercolors", 0f) != 0f;
        bool isLocal = e.Index == ctx.LocalNetId;
        // QC works in 1024+17*team colormap units; the port networks a team id, so map both sides to that form so
        // the friend test (cm == 1024+17*myteam) reduces to (e.Team == myteam).
        int cm = 1024 + 17 * own;
        int forced = CsqcModelAppearance.ResolveForcedColormap(
            enabled, forceMyColors, clColor, forceUnique, isLocal, ctx.Is1v1, ctx.Teamplay, ctx.MyTeam,
            cm, ctx.PlayerLocalNum, e.Index, ctx.TeamCount);
        // playtest-bugs #8: `own` is Entity.Team = the NUM_TEAM_* color CODE (4/13/12/9), but ModelTint wants the
        // colormap nibble — normalize it (team codes → 1..4) so team PLAYERS tint the right color, not pink. A
        // forced (packed >=1024) colormap is already in colormap form, so it flows through untouched. Unforced,
        // the networked clientcolors (packed) win over the plain team nibble (#43 — FFA profile colors paint).
        return forced != 0 ? forced : (packed != 0 ? packed : NormalizeTeamColormap(own));
    }

    private static float Now() => Api.Services?.Clock?.Time ?? 0f;

    /// <summary>Icy-blue colormod for the frozen overlay (QC ENT_CLIENT_STATUSEFFECTS frozen tint): tilts the whole
    /// model toward cold blue (blue &gt; 1 so it reads bright/frosted, red+green pulled down).</summary>
    private static readonly Color FrozenColormod = new(0.45f, 0.65f, 1.15f);

    /// <summary>True when <paramref name="e"/> currently carries the given networked status effect (decoded onto the
    /// proxy by ClientEntityView). Null-def safe — returns false if the effect isn't registered on this client.</summary>
    private static bool HasStatusEffect(Entity e, StatusEffectDef? def)
        => def is not null && StatusEffectsCatalog.Has(e, def);

    private static float CvarF(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// Position each vehicle driver at its entity and feed it the live presentation state derived from the
    /// networked entity (speed from velocity, leg frame, alive from health). Rotors/barrels/engine/heal-beam
    /// follow from there.
    /// </summary>
    private void DriveVehicles(float delta)
    {
        if (_vehicles.Count == 0)
            return;

        const float speedRef = 700f; // a representative top engine load speed (u/s) for the idle↔move crossfade
        foreach (VehicleVisuals vis in _vehicles.Values)
        {
            Entity? e = vis.Bound;
            if (e is null || e.IsFreed || !GodotObject.IsInstanceValid(vis))
                continue;

            // Follow the entity transform (Quake → Godot), like EntityNode.
            vis.Position = Coords.ToGodot(e.Origin);
            // The raptor flight controller (Raptor.Frame) writes the full nose-down/up PITCH (Angles.X) +
            // bank-on-strafe ROLL (Angles.Z) into the entity Angles, not just yaw — so for a pitched/rolled
            // airframe build the full orientation the SAME way EntityNode does (AngleVectors → Coords.ToGodot
            // columns basis: X=fwd, Y=up, Z=right). Gated to entities that actually carry pitch/roll so the
            // long-standing yaw-only path stays byte-identical for every other vehicle (racer/spiderbot/
            // bumblebee read flat).
            if (e.Angles.X != 0f || e.Angles.Z != 0f)
            {
                VortexArena.Common.Math.QMath.AngleVectors(e.Angles, out NVec3 fwd, out NVec3 right, out NVec3 up);
                vis.Basis = new Basis(Coords.ToGodot(fwd), Coords.ToGodot(up), Coords.ToGodot(right));
            }
            else
            {
                vis.Rotation = new Vector3(0f, -Mathf.DegToRad(e.Angles.Y), 0f);
            }

            float speed01 = Mathf.Clamp(
                Mathf.Sqrt(e.Velocity.X * e.Velocity.X + e.Velocity.Y * e.Velocity.Y) / speedRef, 0f, 1f);
            bool alive = !(e.Health <= 0f && e.MaxHealth > 0f);

            // Firing/boost ride the entity's networked Effects bitfield (the server vehicle frame sets them).
            bool firing = (e.Effects & VehicleEffects.Firing) != 0;
            bool boosting = (e.Effects & VehicleEffects.Boosting) != 0;

            vis.Apply(new VehicleVisuals.State
            {
                Speed01 = speed01,
                Frame = (int)e.Frame,
                Alive = alive,
                Firing = firing,
                Boosting = boosting,
                // Heal-target rides the Bumblebee's networked heal state (see VehicleHealBeam wiring).
            }, delta);
        }
    }

    // =================================================================================================
    //  Model + animation attachment
    // =================================================================================================

    private static bool IsProjectile(Entity e)
    {
        // Compare classname/model case-insensitively WITHOUT allocating a lowercased+concatenated scratch string
        // every frame — this runs per-entity per-frame in the CSQC render pass.
        string cn = e.ClassName;
        // A pickup (item/weapon/ammo box, or dropped-loot "item") is NEVER a projectile — even though its MODEL
        // name carries a weapon stem (a_rockets.md3, g_crylink.md3, g_rl.md3, …) that the substring test below
        // would otherwise match, routing the pickup to the ProjectileRenderer instead of showing its model. The
        // server's NetEntityKind already separated them; this keeps the client render routing consistent.
        if (cn.Equals("item", System.StringComparison.OrdinalIgnoreCase)
            || cn.StartsWith("item_", System.StringComparison.OrdinalIgnoreCase)
            || cn.StartsWith("weapon_", System.StringComparison.OrdinalIgnoreCase)
            || cn.StartsWith("ammo_", System.StringComparison.OrdinalIgnoreCase))
            return false;
        // QC projectiles share these classname/model stems (the server also sends a projectile's classname+netname
        // as its "model" so the catalog can type it). The old code concatenated classname+" "+model and substring-
        // tested; a per-field scan is equivalent (the space separator never let a stem match across the seam) and
        // allocation-free. (movetype FLY/TOSS/BOUNCE w/ owner is the deeper server test.)
        return HasProjectileStem(cn) || HasProjectileStem(e.Model);
    }

    private static readonly string[] ProjectileStems =
        { "projectile", "rocket", "grenade", "nade", "plasma", "electro", "crylink", "hagar", "seeker", "fireball",
          "blaster", "spike", "mine", "hook", "mortar", "devastator", "arc", "porto", "vaporizer" };

    private static bool HasProjectileStem(string s)
    {
        foreach (string stem in ProjectileStems)
            if (s.Contains(stem, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="e"/> is a world pickup (the QC <c>ENT_CLIENT_ITEM</c> the client ItemDraw alpha
    /// pass runs for) — a spawned <c>item</c>/<c>item_*</c>/<c>weapon_*</c>/<c>ammo_*</c> classname (the same set
    /// <see cref="IsProjectile"/> excludes from projectile routing), or any entity already carrying a networked
    /// item render status (animate class / not-available / expiring). Gates the item-only ghost/despawn/distance
    /// fade so it never touches players or monsters (which would wrongly hide a distant enemy under the distance
    /// fade — QC ItemDraw runs only for the item entity class).
    /// </summary>
    private static bool IsItemEntity(Entity e)
    {
        string cn = e.ClassName.ToLowerInvariant();
        if (cn == "item" || cn.StartsWith("item_") || cn.StartsWith("weapon_") || cn.StartsWith("ammo_"))
            return true;
        // A networked item that arrived as the generic proxy classname still carries item render status.
        return e.ItemAnimate != 0 || e.ItemExpiringFx || !e.ItemAvailable;
    }

    // =================================================================================================
    //  Vehicles (dedicated visual driver)
    // =================================================================================================

    /// <summary>
    /// Create (first sight) or re-bind a <see cref="VehicleVisuals"/> driver for a vehicle entity. The driver
    /// owns the body/rotors/barrels/engine-sound/heal-beam; <see cref="DriveVehicles"/> feeds it the live
    /// state and positions it each frame. Dependencies (effects/beams/model/sound) are wired from this client.
    /// </summary>
    private void UpdateVehicle(Entity entity)
    {
        if (!_vehicles.TryGetValue(entity.Index, out VehicleVisuals? vis))
        {
            vis = new VehicleVisuals
            {
                Name = $"vehicle#{entity.Index}",
                Effects = Effects,
                Beams = Beams,
                // Resolve a vehicle model by name through the shared resolver (wrap the name in a temp entity).
                ModelResolver = name => ModelResolver?.Invoke(new Entity { ClassName = "vehicle", Model = name }),
                SoundResolver = ResolveSound,
                AudioLoader = s => this.AudioLoader?.Invoke(s),
                Bound = entity,
            };
            AddChild(vis);
            vis.Assets = Assets;   // texture the vehicle body/guns built via ModelLoader.BuildModel/ModelAnimator
            vis.Build(entity.ClassName + " " + entity.Model);
            _vehicles[entity.Index] = vis;
        }
        else
        {
            vis.Bound = entity;
        }
    }

    /// <summary>
    /// QC FORCEMODEL (csqcmodel_hooks.qc:204-235): when <c>cl_forcemyplayermodel</c> (for a friend) or
    /// <c>cl_forceplayermodels</c> is set and the forced model resolves, build THAT model+skin for the player
    /// entity and attach it, returning true. Returns false (the caller renders the entity's own model) when no
    /// force applies, the entity isn't a player, no <see cref="AppearanceProvider"/>/<see cref="ForcedModelResolver"/>
    /// is wired, or the forced model can't be built (the QC <c>fexists</c>-miss → keep own). The forced model is a
    /// plain render node (it doesn't get the skeletal pose/animation path — a documented force-model parity note).
    /// </summary>
    private bool TryAttachForcedModel(Entity entity, EntityNode node)
    {
        if (ForcedModelResolver is null)
            return false;
        if (!entity.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase))
            return false;
        AppearanceContext? ctx = AppearanceProvider?.Invoke();
        if (ctx is null)
            return false;

        string forceMyModel = Api.Services is null ? "" : Api.Cvars.GetString("cl_forcemyplayermodel");
        bool hasForceMy = !string.IsNullOrEmpty(forceMyModel);
        bool forceAll = CvarF("cl_forceplayermodels", 0f) != 0f;
        if (!hasForceMy && !forceAll)
            return false; // no force-model cvar set → keep own (the common case)

        // isfriend = teamplay ? (this entity's team == myteam) : islocalplayer (csqcmodel_hooks.qc:205-210).
        bool isFriend = ctx.Teamplay ? ((int)entity.Team == ctx.MyTeam) : (entity.Index == ctx.LocalNetId);

        // Try cl_forcemyplayermodel first (friend only), then cl_forceplayermodels (everyone) — mirroring the QC
        // cascade. "good" = the forced resolver actually builds a node (the port's fexists analog).
        if (hasForceMy && isFriend)
        {
            int skin = (int)CvarF("cl_forcemyplayerskin", 0f);
            if (ForcedModelResolver(entity, forceMyModel, skin) is { } myNode)
            {
                AttachForcedNode(entity, node, myNode);
                return true;
            }
        }
        if (forceAll)
        {
            string allModel = Api.Services is null ? "" : Api.Cvars.GetString("_cl_playermodel");
            int allSkin = (int)CvarF("_cl_playerskin", 0f);
            if (!string.IsNullOrEmpty(allModel) && ForcedModelResolver(entity, allModel, allSkin) is { } allNode)
            {
                AttachForcedNode(entity, node, allNode);
                return true;
            }
        }
        return false; // forced model(s) didn't resolve → fall through to the entity's own model
    }

    /// <summary>Parent a forced-model render node under the entity node and run the appearance pass on it (so the
    /// FORCECOLORS/glowmod/deathglow tint applies to the forced model too). A forced <see cref="PlayerModel"/> is
    /// registered for the per-frame skeletal pose so it animates like the entity's own model would.</summary>
    private void AttachForcedNode(Entity entity, EntityNode node, Node3D forcedNode)
    {
        node.AddChild(forcedNode);
        if (forcedNode is PlayerModel pm)
        {
            pm.Bound = entity;
            _playerModels[entity.Index] = pm;
        }
        CsqcStateFor(entity).IsPlayerModel = true;
        ModelTint.ApplyAppearance(node, ResolveForcedColormap(entity), isDead: false, deathTime: 0f, isRespawnGhost: false);
    }

    private void TryAttachModel(Entity entity, EntityNode node)
    {
        // FORCEMODEL (csqcmodel_hooks.qc:204-235): cl_forcemyplayermodel (friend) / cl_forceplayermodels swaps the
        // player's model+skin to the forced one before it's built. When a force applies AND the forced model
        // resolves, render THAT instead of the entity's own model.
        if (TryAttachForcedModel(entity, node))
            return;

        // Players: a skeletal IQM with the CPU upper/lower split + view-pitch aim (driven each frame in _Process).
        if (PlayerModelResolver?.Invoke(entity) is { } playerModel)
        {
            playerModel.Bound = entity;
            node.AddChild(playerModel);
            _playerModels[entity.Index] = playerModel;
            // Mark this entity for the per-frame CSQC appearance/deathglow pass (DriveCsqcModelHooks). The
            // networked colormap rides on Entity.Team (ClientEntityView); FFA/unknown stays untinted, native glow.
            CsqcStateFor(entity).IsPlayerModel = true;
            // Seed the tint immediately so the model isn't a frame untinted (the per-frame pass refreshes it).
            ModelTint.ApplyAppearance(node, ResolveForcedColormap(entity), isDead: false, deathTime: 0f, isRespawnGhost: false);
            return;
        }

        Md3Data? md3 = ModelResolver?.Invoke(entity);
        if (md3 is null)
        {
            // Not an MD3 (the MD3 resolver passed): try the format-agnostic factory (IQM/DPM/MD3 via the asset
            // pipeline → textured + animated). This is what covers non-MD3 world props/items/monsters.
            if (EntityModelFactory?.Invoke(entity) is { } built)
            {
                node.AddChild(built);
                // CSQCMODEL_AUTOUPDATE for skeletal DPM monsters (golem/spider/mage/zombie/wyvern): a networked
                // frame-driven entity plays the frame GROUP its server brain stamped onto Entity.Frame (QC mr_anim
                // → MonsterAI.DriveAnimFrame). DpmBuilder bakes the .framegroups ranges into AnimationPlayer clips
                // in group order; this binds the driver so the model actually plays idle/walk/run/melee/pain/spawn/
                // die clips through their real .dpm fps timeline instead of holding the bind pose. The attach is
                // inert for a DPM that carries no framegroup metadata (a single-clip prop) or a non-frame-driven
                // (local/demo) entity, mirroring the MD3 ModelAnimator.FollowEntityFrame gate just below.
                if (_frameDriven.Contains(entity.Index))
                    DpmFrameDriver.TryAttach(built, entity);
                return;
            }

            // Nothing resolved at all: drop a small placeholder so the entity is still visible.
            node.AddChild(new MeshInstance3D
            {
                Name = "Placeholder",
                Mesh = new BoxMesh { Size = new Vector3(24f, 56f, 24f) },
                Position = new Vector3(0f, 28f, 0f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.6f, 0.65f) },
            });
            return;
        }

        if (md3.FrameCount > 1)
        {
            var anim = ModelAnimator.Create(md3, $"anim#{entity.Index}", Assets);
            anim.Entity = entity;
            // Networked entities play their server-computed frame directly (CSQCMODEL_AUTOUPDATE); local
            // (demo) entities fall to the movement-derived clip heuristic in SelectClipFromMovement.
            anim.FollowEntityFrame = _frameDriven.Contains(entity.Index);
            // Non-local player MD3 models: route the raw frame through the CSQC fallback-frame remap so a model
            // missing the melee/duckwalk anims falls back to a present frame (csqcmodel_hooks.qc:702).
            bool isPlayer = entity.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase);
            anim.UseFallbackFrame = isPlayer && anim.FollowEntityFrame;
            if (isPlayer)
                CsqcStateFor(entity).IsPlayerModel = true;
            node.AddChild(anim);
            anim.SetProcess(false); // R1: ClientWorld's cw.anim loop drives Advance(); kill the per-node callback
            _animators[entity.Index] = anim;
        }
        else
        {
            node.AddChild(ModelLoader.BuildModel(md3, 0, Assets));
        }
    }

    /// <summary>
    /// True when this networked entity is a CTF flag (its resolved model lives under models/ctf/flag*). Its banner
    /// texture (banner.tga) carries a <c>banner_shirt.tga</c> companion, so AssetSystem auto-compiles it to the
    /// team-colorable <see cref="VortexArena.Game.Loaders.PlayerSkinShader"/> — the flag just needs the team
    /// colormap driven onto it like a player (playtest-bugs #8).
    /// </summary>
    private static bool IsCtfFlagModel(Entity e) =>
        e.Model is { } m && m.Contains("ctf/flag", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalize a networked colormap / <see cref="Entity.Team"/> for <see cref="ModelTint"/>: it holds Xonotic's
    /// NUM_TEAM_* color CODE (<see cref="VortexArena.Common.Gameplay.Teams"/>: Red=4/Blue=13/Yellow=12/Pink=9),
    /// but <c>ModelTint.ColormapColors</c>/<c>TeamColor</c> were written for the colormap LOW NIBBLE (1=red..4=pink).
    /// Map the four team codes to their nibble so a team entity (player OR flag) tints the RIGHT team color; leave
    /// everything else unchanged — 0 (FFA/none) and a packed FORCECOLORS value (&gt;=1024) both flow through as-is.
    /// (playtest-bugs #8: a red flag/player rendered PINK because code 4 hit TeamColor(4)=pink.)
    /// </summary>
    /// <summary>Map a NUM_TEAM_* color CODE (Red=4/Blue=13/Yellow=12/Pink=9 — what <c>Entity.Team</c> carries)
    /// onto the colormap low NIBBLE (1..4) <see cref="ModelTint.TeamColor"/> expects; pass-through for values
    /// already in nibble/colormap form. Public: NetGame's viewmodel team tint needs the same mapping
    /// (playtest r9: the raw BLUE code 13 is no valid nibble → the first-person gun never team-tinted).</summary>
    public static int NormalizeTeamColormap(int cm) => cm switch
    {
        VortexArena.Common.Gameplay.Teams.Red => 1,    // 4  → 1 red
        VortexArena.Common.Gameplay.Teams.Blue => 2,   // 13 → 2 blue
        VortexArena.Common.Gameplay.Teams.Yellow => 3, // 12 → 3 yellow
        VortexArena.Common.Gameplay.Teams.Pink => 4,   // 9  → 4 pink
        _ => cm,
    };

    private void UpdateAnimatorState(Entity entity)
    {
        if (_animators.TryGetValue(entity.Index, out ModelAnimator? anim) && !anim.FollowEntityFrame)
            SelectClipFromMovement(entity, anim);
    }

    /// <summary>
    /// Pick a locomotion clip from the entity's state — the client-side animation selection the libs flag
    /// (walk/strafe/idle/jump/death). Dead → death; airborne → jump; moving → run; else idle. If the model
    /// registered no such clips, <see cref="ModelAnimator.PlayIfChanged"/> simply no-ops and the current
    /// (auto-detected) clip keeps playing.
    /// </summary>
    private static void SelectClipFromMovement(Entity e, ModelAnimator anim)
    {
        string clip;
        bool dead = e.DeadState is DeadFlag.Dying or DeadFlag.Dead
                    || (e.Health <= 0f && e.MaxHealth > 0f);
        if (dead)
            clip = "death";
        else if (!e.OnGround && e.MoveType is MoveType.Walk or MoveType.Step or MoveType.Fly)
            clip = "jump";
        else
        {
            float speed2 = e.Velocity.X * e.Velocity.X + e.Velocity.Y * e.Velocity.Y;
            clip = speed2 > 100f ? "run" : "idle";
        }
        anim.PlayIfChanged(clip);
    }

    // =================================================================================================
    //  Helpers
    // =================================================================================================

    private string? ResolveSound(string bareSample)
    {
        if (SoundResolver is not null)
            return SoundResolver(bareSample);
        // Default convention: res://sound/<sample>.ogg (the Xonotic sound/ tree under the project).
        return $"res://sound/{bareSample}.ogg";
    }

    /// <summary>
    /// Resolve a sample to a playable <see cref="AudioStream"/>: the VFS <see cref="AudioLoader"/> first
    /// (the real mounted content packs), then the <see cref="ResolveSound"/> <c>res://</c> fallback.
    /// Returns null to stay silent (missing sample).
    /// </summary>
    private AudioStream? LoadStream(string bareSample)
    {
        AudioStream? stream = AudioLoader?.Invoke(bareSample);
        if (stream is not null)
            return stream;

        string? resPath = ResolveSound(bareSample);
        if (string.IsNullOrEmpty(resPath) || !ResourceLoader.Exists(resPath))
            return null;
        try { return ResourceLoader.Load<AudioStream>(resPath); }
        catch { return null; }
    }

    private AudioStreamPlayer3D RentAudioPlayer()
    {
        foreach (AudioStreamPlayer3D p in _audioPool)
            if (GodotObject.IsInstanceValid(p) && !p.Playing)
                return p;

        // Godot's own distance attenuation is DISABLED (we apply the DP linear curve via SetSpatialVolume each
        // frame); MaxDistance 0 = no Godot cutoff, so our gain alone decides the audible radius.
        var np = new AudioStreamPlayer3D
        {
            Name = $"sfx{_audioPool.Count}",
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            MaxDistance = 0f,
            AttenuationFilterCutoffHz = 20500f, // disable Godot's distance low-pass (DP has none — no far muffling)
        };
        AddChild(np);
        _audioPool.Add(np);
        return np;
    }

    private static string Safe(string s) => string.IsNullOrEmpty(s) ? "entity" : s;

    /// <summary>
    /// An <see cref="IEffectSink"/> that mirrors every in-process <c>EffectEmitter.Emit</c> onto the client
    /// renderer (and still forwards to whatever sink was previously installed, e.g. the recorder/network
    /// sink). This bridges server-side gameplay emission to the client visuals within one process, which is
    /// exactly the demo path; in a real client the network sink stays primary and the decoded effect comes
    /// in via <see cref="OnEffect(in EffectRequest)"/> instead.
    /// </summary>
    private sealed class RenderSink : IEffectSink
    {
        private readonly ClientWorld _world;
        public IEffectSink Inner { get; }

        public RenderSink(ClientWorld world, IEffectSink inner)
        {
            _world = world;
            Inner = inner;
        }

        public void Emit(in EffectRequest request)
        {
            Inner.Emit(request);                 // keep recording / networking behaviour intact
            if (!GodotObject.IsInstanceValid(_world))
                return;

            // Drop the local host player's OWN excepted effect (their muzzle flash) — it's predicted locally on the
            // refire clock (cl_predictfire), so rendering the in-process copy here would double it. The networked
            // path already excludes the shooter (Send_Effect_Except → ServerNet's per-peer pass), so remote players
            // are unaffected; this only fixes the listen-server in-process mirror, which otherwise ignores Except.
            if (_world.SuppressOwnFireEffects && request.Except is not null
                && ReferenceEquals(request.Except, _world.LocalHostPlayer))
                return;

            // Render on the client. Defer onto the node's thread — emission can come from a sim tick, and
            // building Godot nodes must happen on the main/scene thread. Capture by value (struct copy).
            string name = request.Effect?.Name ?? request.EffectName;
            NVec3 origin = request.Origin;
            NVec3 velocity = request.Velocity;
            int count = request.Count;
            Color? tint = null;
            if (request.ColorMin != default || request.ColorMax != default)
            {
                NVec3 mid = (request.ColorMin + request.ColorMax) * 0.5f;
                tint = new Color(mid.X, mid.Y, mid.Z);
            }

            ClientWorld world = _world;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(world))
                    world.Effects.Spawn(name, origin, velocity, count, tint);
            }).CallDeferred();
        }
    }
}

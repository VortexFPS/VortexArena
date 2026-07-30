using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Services;
using VortexArena.Game.Client;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Hud;

/// <summary>
/// Center crosshair — port of the core of Base/.../qcsrc/client/hud/crosshair.qc. The QC crosshair has
/// per-weapon crosshair pics, dynamic scaling on fire, hit-indication color flash, a ring charge indicator
/// (Vortex/Vaporizer/Mortar fuse), true-aim coloring (HITENEMY/HITTEAM/HITWORLD/HITOBSTRUCTION) and a
/// weapon-switch cross-fade between the old and new crosshair image.
///
/// This port wires the per-weapon + ring pieces against the real weapon model. When a <see cref="Player"/>
/// is set the panel reads the active weapon (<see cref="Inventory.CurrentWeapon"/>): it tints the crosshair
/// with that weapon's color (QC <c>crosshair_per_weapon</c>) and, if the weapon supports charging
/// (<see cref="WeaponHud.IsChargeWeapon"/>), draws a charge ring around the center filled to
/// <see cref="ChargeFraction"/> (fed by the weapon/net layer, since per-slot charge state is networked). A
/// per-weapon crosshair texture is used when present (<see cref="TextureCache"/>), else the clean vector
/// crosshair (center gap + four ticks + dot) is drawn. <see cref="FiringRing"/> draws a transient
/// shrinking ring on fire, and <see cref="HitFlash"/> the hit-indication tint.
///
/// <para><b>Weapon-stat rings (QC <c>autocvar_crosshair_ring</c> / <c>_ring_reload</c>).</b> The QC crosshair
/// draws a partial arc around the center for live weapon state using the <c>gfx/crosshair_ring*</c> art:
/// Vortex/Overkill-Nex charge (outer <c>crosshair_ring_nexgun</c> + an inner chargepool/moving-average ring),
/// Hagar burst load, Mine Layer mine count, Arc overheat, and the reload "ammo ring" (clip load / clip size,
/// Rifle gets <c>crosshair_ring_rifle</c>). These are fed by the weapon/net layer via the public ring stat
/// setters (<see cref="ClipLoad"/>/<see cref="ClipSize"/>, <see cref="HagarLoad"/>/<see cref="HagarLoadMax"/>,
/// <see cref="MineCount"/>/<see cref="MineLimit"/>, <see cref="ArcHeat"/>, <see cref="ChargePool"/>) since the
/// per-slot weapon state is networked. The legacy <see cref="ChargeFraction"/>/<see cref="FiringRing"/>
/// drawing is preserved as a fallback when no ring stat is fed.</para>
///
/// <para><b>T21 — true-aim coloring (QC <c>TrueAimCheck</c>/<c>EnemyHitCheck</c>).</b> Each frame the panel
/// runs a forward trace from the eye (<see cref="Api.Trace"/>, the same client trace service the rest of the
/// client uses) and classifies what it would hit — a teammate (<see cref="ShotType.HitTeam"/>), an enemy
/// (<see cref="ShotType.HitEnemy"/>), the world (<see cref="ShotType.HitWorld"/>) or an obstruction between
/// the shot origin and the aim point (<see cref="ShotType.HitObstruction"/>). The crosshair then signals an
/// invalid target the way QC does: a teammate shrinks the crosshair (QC <c>wcross_scale /= crosshair_hittest</c>)
/// and dims it, an obstruction dims it (QC <c>crosshair_hittest_blur_wall</c>), and an enemy/world leaves it at
/// full strength. The aim ray is taken from <see cref="AimOrigin"/>/<see cref="AimForward"/> when the view layer
/// feeds them (the authoritative render eye + look direction); absent that it is reconstructed from the
/// <see cref="Player"/>'s view angles + eye height, so coloring still works on the bare local-player path.</para>
///
/// <para><b>T21 — weapon-switch transition (QC <c>wcross_name_changestarttime</c>/<c>changedonetime</c>).</b>
/// When the active weapon's crosshair image changes the panel starts a short cross-fade over
/// <see cref="EffectTime"/>: the outgoing crosshair fades out while the incoming fades in (alpha only, as in
/// QC), mirroring the QC <c>CROSSHAIR_DRAW</c> of the previous pic over the new one during a switch. A
/// smooth goal-based scale/alpha lerp (QC <c>wcross_changedonetime</c> block) eases size/alpha changes from
/// hit-test / hit-indication / pickup over <see cref="EffectTime"/>.</para>
/// </summary>
public partial class CrosshairPanel : HudPanel
{
    /// <summary>(R8) The crosshair draws with its own <c>crosshair_alpha</c>, independent of the scoreboard
    /// panel-fade, so the HUD manager must keep updating it even while the scoreboard is up.</summary>
    public override bool DrawsWithOwnAlpha => true;

    // Port of crosshair.qc: SHOTTYPE_* — the true-aim hit classification.
    /// <summary>Classification of what the aim ray would hit (QC <c>SHOTTYPE_*</c>).</summary>
    public enum ShotType
    {
        /// <summary>Aim ray would hit a teammate — an invalid target (QC SHOTTYPE_HITTEAM = 1).</summary>
        HitTeam = 1,
        /// <summary>A wall/obstruction sits between the shot origin and the aim point (QC SHOTTYPE_HITOBSTRUCTION = 2).</summary>
        HitObstruction = 2,
        /// <summary>Aim ray hits the world (or nothing relevant) — the default (QC SHOTTYPE_HITWORLD = 3).</summary>
        HitWorld = 3,
        /// <summary>Aim ray would hit an enemy — a valid target (QC SHOTTYPE_HITENEMY = 4).</summary>
        HitEnemy = 4,
    }

    /// <summary>QC <c>autocvar_crosshair_color_special</c>: how the base crosshair color is chosen.</summary>
    public enum ColorMode
    {
        /// <summary>QC default: the static <c>crosshair_color</c>.</summary>
        Fixed = 0,
        /// <summary>QC 1: tint by the active weapon's color (<c>crosshair_per_weapon</c>).</summary>
        Weapon = 1,
        /// <summary>QC 2: tint by health + armor (<c>HUD_Get_Num_Color</c>).</summary>
        Health = 2,
        /// <summary>QC 3: rainbow — a random colour re-rolled every <c>crosshair_color_special_rainbow_delay</c>.</summary>
        Rainbow = 3,
    }

    /// <summary>The local player (set by <see cref="Hud"/>); drives per-weapon crosshair color + charge ring + true-aim.</summary>
    public Player? Player { get; set; }

    /// <summary>Base crosshair color (QC crosshair_color). Alpha is the resting opacity.</summary>
    public Color Color { get; set; } = new(0.4f, 1f, 0.5f, 0.85f);

    /// <summary>QC <c>crosshair_per_weapon</c>: tint the crosshair with the active weapon's color.</summary>
    public bool PerWeaponColor { get; set; } = true;

    /// <summary>
    /// The crosshair shape number (QC <c>crosshair</c> cvar): Xonotic crosshairs are numbered art
    /// (<c>gfx/crosshair&lt;N&gt;</c>), not per-weapon files. 0 disables the textured crosshair (vector fallback).
    /// </summary>
    public int CrosshairNumber { get; set; } = 3;

    /// <summary>Optional per-weapon crosshair number (QC <c>crosshair_per_weapon</c>), keyed by weapon NetName;
    /// overrides <see cref="CrosshairNumber"/> for that weapon when present. Host/config-populated.</summary>
    public System.Collections.Generic.Dictionary<string, int>? PerWeaponNumber { get; set; }

    /// <summary>Empty space (radius) at the very center, in pixels (QC crosshair ring/dot gap).</summary>
    public float GapPixels { get; set; } = 5f;

    /// <summary>Length of each of the four ticks, in pixels (QC crosshair size).</summary>
    public float TickLength { get; set; } = 8f;

    /// <summary>Line thickness, in pixels.</summary>
    public float Thickness { get; set; } = 2f;

    /// <summary>Radius of the center dot, in pixels. 0 disables it.</summary>
    public float DotRadius { get; set; } = 1.5f;

    /// <summary>
    /// Charge fraction in [0,1] for charge weapons (QC <c>wepent.vortex_charge</c>). The weapon/net layer
    /// feeds this each frame; the panel draws a ring filled to this fraction while the active weapon is a
    /// charge weapon. &lt; 0 means "no charge data" (ring hidden).
    /// </summary>
    public float ChargeFraction { get; set; } = -1f;

    /// <summary>Radius of the charge/firing ring, in pixels.</summary>
    public float RingRadius { get; set; } = 16f;

    /// <summary>
    /// Transient hit-indication strength in [0,1] (QC hitindication): briefly tints the crosshair toward
    /// <see cref="HitColor"/>. The owner sets this to 1 on a confirmed hit; it decays each frame.
    /// </summary>
    public float HitFlash { get; set; }

    /// <summary>Color blended in while <see cref="HitFlash"/> is active (QC hit indication red).</summary>
    public Color HitColor { get; set; } = new(1f, 0.2f, 0.2f, 1f);

    /// <summary>Seconds for <see cref="HitFlash"/> to decay from 1 to 0.</summary>
    public float HitDecay { get; set; } = 0.3f;

    /// <summary>
    /// Transient firing-ring strength in [0,1] (QC the fire/cooldown ring): the owner pulses this to 1 on
    /// fire and it decays, drawing a shrinking ring. Independent of the charge ring.
    /// </summary>
    public float FiringRing { get; set; }

    /// <summary>Seconds for <see cref="FiringRing"/> to decay.</summary>
    public float FiringDecay { get; set; } = 0.25f;

    // -------------------------------------------------------------------------------------------------
    //  Weapon-stat rings (QC autocvar_crosshair_ring / _ring_reload) — fed by the weapon/net layer.
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>wepent.clip_load</c>: rounds left in the active weapon's clip (for the reload ammo ring).
    /// &lt; 0 = no reload data.</summary>
    public float ClipLoad { get; set; } = -1f;

    /// <summary>QC <c>wepent.clip_size</c>: clip capacity of the active weapon (the reload ring is only drawn
    /// when this is &gt; 0, exactly like QC <c>weapon_clipsize</c>).</summary>
    public float ClipSize { get; set; }

    /// <summary>QC <c>wepent.hagar_load</c>: queued Hagar burst rounds (drives the Hagar load ring).
    /// &lt; 0 = no data.</summary>
    public float HagarLoad { get; set; } = -1f;

    /// <summary>QC <c>WEP_CVAR_SEC(WEP_HAGAR, load_max)</c>: max queued Hagar rounds (ring denominator).</summary>
    public float HagarLoadMax { get; set; } = 4f;

    /// <summary>QC <c>wepent.minelayer_mines</c>: live mines placed (drives the Mine Layer ring). &lt; 0 = no data.</summary>
    public float MineCount { get; set; } = -1f;

    /// <summary>QC <c>WEP_CVAR(WEP_MINE_LAYER, limit)</c>: max placed mines (ring denominator; 0 = no ring).</summary>
    public float MineLimit { get; set; } = 3f;

    /// <summary>QC <c>wepent.arc_heat_percent</c>: Arc overheat in [0,1] (drives the Arc heat ring). &lt; 0 = no data.</summary>
    public float ArcHeat { get; set; } = -1f;

    /// <summary>QC <c>wepent.vortex_chargepool_ammo</c> / <c>oknex_chargepool_ammo</c>: the inner-ring chargepool
    /// fraction for the rail family. &lt; 0 = no chargepool (inner ring falls back to the charge moving-average).</summary>
    public float ChargePool { get; set; } = -1f;

    // -------------------------------------------------------------------------------------------------
    //  [T41] Objective rings (QC view.qc HUD_Draw 1006-1022) — the NADE_TIMER / CAPTURE_PROGRESS /
    //  REVIVE_PROGRESS [0,1] fill rendered via DrawCircleClippedPic, in that strict priority (only one draws).
    //  Fed by the net layer from the local player's STATs; 0 = inactive (ring hidden). These are independent of
    //  the weapon-stat rings above and draw whatever the active weapon is (they're match-state, not weapon-state).
    // -------------------------------------------------------------------------------------------------

    /// <summary>QC <c>STAT(NADE_TIMER)</c>: 0..1 held-nade charge — the TOP-priority objective ring (life-or-death).
    /// 0 = no held nade (ring hidden). Fed from the local player each frame.</summary>
    public float NadeTimer { get; set; }

    /// <summary>QC <c>STAT(CAPTURE_PROGRESS)</c>: 0..1 objective-capture progress — 2nd-priority ring. 0 = inactive.</summary>
    public float CaptureProgress { get; set; }

    /// <summary>QC <c>STAT(REVIVE_PROGRESS)</c>: 0..1 freeze-tag thaw progress — 3rd-priority ring. 0 = inactive.</summary>
    public float ReviveProgress { get; set; }

    /// <summary>QC <c>autocvar_cl_nade_timer</c> gate for the nade ring (0 = off, 1 = ring only, 2 = ring + label).
    /// Mirrors the property fallback; the live cvar (when present) wins via <c>GlobalF</c>.</summary>
    public int NadeTimerMode { get; set; } = 1;

    /// <summary>Radius of the objective rings, in pixels (QC <c>0.1 * vid_conheight</c> — a large ring around the
    /// crosshair; this port centers them on the crosshair panel rather than at QC's 0.6·height screen anchor).</summary>
    public float ObjectiveRingRadius { get; set; } = 64f;

    // ---- T21: true-aim coloring (QC autocvar_crosshair_hittest + the SHOTTYPE coloring) ----

    /// <summary>
    /// QC <c>autocvar_crosshair_hittest</c>: enable the forward true-aim trace and colour the crosshair by what
    /// it would hit. When false the crosshair is always treated as <see cref="ShotType.HitWorld"/> (no trace),
    /// exactly like QC.
    /// </summary>
    public bool HitTest { get; set; } = true;

    /// <summary>
    /// Legacy property fallback for the teammate-shrink divisor. The live path now divides by the
    /// <c>crosshair_hittest</c> cvar itself (QC <c>wcross_scale /= autocvar_crosshair_hittest</c>), which defaults
    /// to 1 (NO shrink) exactly like Base; this property is retained only as a public API surface and defaults to 1
    /// to match. A value &gt; 1 shrinks the crosshair over a teammate.
    /// </summary>
    public float HitTestTeammateShrink { get; set; } = 1f;

    /// <summary>QC <c>autocvar_crosshair_hittest_blur_wall</c>: dim the crosshair over an obstruction.</summary>
    public bool BlurWall { get; set; } = true;

    /// <summary>QC <c>autocvar_crosshair_hittest_blur_teammate</c>: dim the crosshair over a teammate.</summary>
    public bool BlurTeammate { get; set; }

    /// <summary>
    /// The eye/shot origin for the true-aim trace, in Quake/sim coordinates (QC <c>traceorigin</c>). When this is
    /// fed by the view layer (the authoritative render eye) it is used as-is; otherwise the panel reconstructs it
    /// from the <see cref="Player"/>'s origin + eye height. <c>null</c> = reconstruct.
    /// </summary>
    public NVec3? AimOrigin { get; set; }

    /// <summary>
    /// The look direction for the true-aim trace, in Quake/sim coordinates (QC <c>view_forward</c>). Fed by the
    /// view layer (the render look direction); absent that the panel derives it from the player's view angles.
    /// <c>null</c> = derive.
    /// </summary>
    public NVec3? AimForward { get; set; }

    /// <summary>Eye height above the player origin used to reconstruct the trace origin (Xonotic PL_VIEW_OFS '0 0 35').</summary>
    public float ViewHeight { get; set; } = 35f;

    /// <summary>The latest true-aim classification (QC <c>shottype</c>), recomputed each <see cref="_Process"/>.</summary>
    public ShotType ShotResult { get; private set; } = ShotType.HitWorld;

    // ---- crosshair_chase: third-person crosshair re-projection (QC crosshair.qc HUD_Crosshair chase block) ----

    /// <summary>
    /// QC <c>chase_active</c>: true while the third-person chase camera is live. Fed by the view layer (NetGame).
    /// When set (and <see cref="ChaseCamera3D"/> + <see cref="Player"/> are present and <c>crosshair_chase</c> is on)
    /// the crosshair is drawn at the screen-projection of the chase impact point rather than the geometric panel
    /// centre, so it tracks where the shot would actually land from the offset chase eye.
    /// </summary>
    public bool ChaseActive { get; set; }

    /// <summary>
    /// The live render camera used to re-project the chase impact point to screen space (QC <c>project_3d_to_2d</c>).
    /// Fed by the view layer (NetGame) alongside <see cref="ChaseActive"/>; <c>null</c> = no chase re-projection.
    /// </summary>
    public Camera3D? ChaseCamera3D { get; set; }

    // ---- T21: weapon-switch cross-fade (QC wcross_name_changestarttime / changedonetime) ----

    /// <summary>
    /// QC <c>autocvar_crosshair_effect_time</c>: duration of the weapon-switch crosshair cross-fade, in seconds.
    /// 0 disables the transition (the new crosshair snaps in).
    /// </summary>
    public float EffectTime { get; set; } = 0.4f;

    /// <summary>
    /// Current time used to drive the switch transition. If &lt; 0 (default) the panel uses the sim clock
    /// (<see cref="Api.Clock"/>) when available, else its own per-frame wall clock. The net/demo layer can
    /// slave it to the match clock.
    /// </summary>
    public double Now { get; set; } = -1.0;

    private double _localClock;
    private double _lastDelta = 1.0 / 60.0;

    // (perf 2026-06-14) The two forward world traces in ComputeShotType are a full max_shot_distance (32768 qu)
    // ray + a box trace. The TRUE root-cause fix is in the collision broadphase (Brush.cs: a long ray no longer
    // brute-forces every brush in the map), which made each trace ~negligible. This cache is a cheap, always-
    // correct secondary guard: a static aim ray cannot change the true-aim classification, so we skip the traces
    // entirely while standing still (the user's "slowest at the spawn" case). An optional cl_crosshair_trueaim_rate
    // (default 0 = off) can additionally cap the re-trace rate while turning on pathologically dense content.
    private NVec3 _trueAimLastOrigin, _trueAimLastForward;
    private bool _trueAimHaveCache;
    private double _trueAimNextAt;

    // Weapon-switch cross-fade state — the port of QC's wcross_name_* "goal_prev" persisted globals. We key
    // the transition on the resolved crosshair texture changing (QC keys on wcross_name/resolution changing).
    private Texture2D? _crossPrev;       // the outgoing crosshair texture (QC wcross_name_goal_prev_prev)
    private float _changeStartTime;      // QC wcross_name_changestarttime
    private float _changeDoneTime;       // QC wcross_name_changedonetime
    private Texture2D? _lastResolved;    // what we resolved last frame, to detect a switch (QC wcross_name_goal_prev)

    // ---- pickup / hit-indication animation state (QC pickup_crosshair_size / hitindication_crosshair_size) ----
    private float _pickupSize;           // QC pickup_crosshair_size (counts down from 1; sin() drives the bump)
    private float _hitIndSize;           // QC hitindication_crosshair_size (counts down from 1)

    // ---- goal-based smooth scale/alpha/color (QC wcross_changedonetime block) ----
    private float _scalePrev = 1f, _alphaPrev = 1f;        // value last frame (QC *_prev)
    private float _scaleGoalPrev = 1f, _alphaGoalPrev = 1f; // goal last frame (QC *_goal_prev)
    private Vector3 _colorPrev = Vector3.One;               // RGB last frame (QC wcross_color_prev)
    private Vector3 _colorGoalPrev = Vector3.One;           // RGB goal last frame (QC wcross_color_goal_prev)
    private float _changeDoneTimeGoal;                      // QC wcross_changedonetime
    private bool _smoothSeeded;

    // ---- vortex charge moving average (QC vortex_charge_movingavg) ----
    private float _vortexChargeAvg;
    private bool _useChargePool;          // QC use_vortex_chargepool (latched once a chargepool is observed)

    // ---- weapon-switch ring fade memory (QC wcross_ring_prev) ----
    private bool _ringPrev;

    // ---- rainbow color (QC crosshair_getcolor case 3: rainbow_last_flicker / rainbow_prev_color statics) ----
    private float _rainbowNextFlicker;        // QC rainbow_last_flicker (next re-roll time)
    private Vector3 _rainbowColor;            // QC rainbow_prev_color (the latched random color, may exceed [0,1])
    private bool _rainbowSeeded;

    /// <summary>
    /// Register the panel's behaviour-cvar defaults (the stock <c>crosshair*</c> values from crosshairs.cfg).
    /// Auto-invoked by reflection from <c>HudConfig.RegisterDefaults</c>. These are the engine <c>crosshair_*</c>
    /// cvars (not <c>hud_panel_crosshair_*</c>) so a console/menu <c>set crosshair_*</c> takes effect live.
    /// </summary>
    public static void RegisterDefaults(VortexArena.Engine.Simulation.CvarService c)
    {
        const VortexArena.Common.Services.CvarFlags save = VortexArena.Common.Services.CvarFlags.Save;

        // crosshairs.cfg core
        c.Register("crosshair_enabled", "1", save);
        c.Register("crosshair", "16", save); // stock crosshairs.cfg default (gfx/crosshair16)
        c.Register("crosshair_color", "0.6 0.8 1", save);
        c.Register("crosshair_alpha", "0.8", save);
        c.Register("crosshair_size", "0.4", save);
        c.Register("crosshair_per_weapon", "1", save);
        // crosshairs.cfg:48,51,52 — registered for parity/config faithfulness. crosshair_2d selects the
        // side-scroller (FREEAIM viewloc) crosshair; crosshair_chase/_playeralpha drive the third-person chase
        // crosshair re-projection + body-alpha fade. The side-scroller view-state and chase trace are not yet
        // wired in this port, so these cvars are settable but their special behaviour is inert here.
        c.Register("crosshair_2d", "54", save);
        c.Register("crosshair_chase", "1", save);
        c.Register("crosshair_chase_playeralpha", "0.25", save);

        // dot
        c.Register("crosshair_dot", "0", save);
        c.Register("crosshair_dot_alpha", "1", save);
        c.Register("crosshair_dot_size", "0.6", save);
        c.Register("crosshair_dot_color", "1 0 0", save);
        c.Register("crosshair_dot_color_custom", "1", save);

        // switch transition + scale-fade
        c.Register("crosshair_effect_time", "0.4", save);
        c.Register("crosshair_effect_scalefade", "1", save);

        // pickup pulse
        c.Register("crosshair_pickup", "0.25", save);
        c.Register("crosshair_pickup_speed", "4", save);

        // hit indication
        c.Register("crosshair_hitindication", "0.5", save);
        c.Register("crosshair_hitindication_color", "10 -10 -10", save);
        c.Register("crosshair_hitindication_per_weapon_color", "10 10 10", save);
        c.Register("crosshair_hitindication_speed", "5", save);

        // hit test
        c.Register("crosshair_hittest", "1", save);
        c.Register("crosshair_hittest_blur_teammate", "0", save);
        c.Register("crosshair_hittest_blur_wall", "1", save);
        c.Register("crosshair_hittest_showimpact", "0", save);

        // special color
        c.Register("crosshair_color_special", "1", save);
        c.Register("crosshair_color_special_rainbow_delay", "0.1", save);
        c.Register("crosshair_color_special_rainbow_brightness", "20", save);

        // rings
        c.Register("crosshair_ring", "1", save);
        c.Register("crosshair_ring_inner", "0", save);
        c.Register("crosshair_ring_size", "2", save);
        c.Register("crosshair_ring_alpha", "0.2", save);

        c.Register("crosshair_ring_vortex", "1", save);
        c.Register("crosshair_ring_vortex_alpha", "0.15", save);
        c.Register("crosshair_ring_vortex_inner_alpha", "0.15", save);
        c.Register("crosshair_ring_vortex_inner_color_red", "0.8", save);
        c.Register("crosshair_ring_vortex_inner_color_green", "0", save);
        c.Register("crosshair_ring_vortex_inner_color_blue", "0", save);
        c.Register("crosshair_ring_vortex_currentcharge_scale", "30", save);
        c.Register("crosshair_ring_vortex_currentcharge_movingavg_rate", "0.05", save);

        c.Register("crosshair_ring_minelayer", "1", save);
        c.Register("crosshair_ring_minelayer_alpha", "0.15", save);

        c.Register("crosshair_ring_hagar", "1", save);
        c.Register("crosshair_ring_hagar_alpha", "0.15", save);

        c.Register("crosshair_ring_arc", "1", save);
        c.Register("crosshair_ring_arc_hot_color", "1 0 0", save);
        c.Register("crosshair_ring_arc_cold_alpha", "0.2", save);
        c.Register("crosshair_ring_arc_hot_alpha", "0.5", save);

        c.Register("crosshair_ring_reload", "1", save);
        c.Register("crosshair_ring_reload_size", "2.5", save);
        c.Register("crosshair_ring_reload_alpha", "0.2", save);
    }

    // -------------------------------------------------------------------------------------------------
    //  (§11 R11) Per-frame cvar cache. _Process consulted up to 5 crosshair_* cvars every frame through
    //  GlobalF (2 dictionary lookups each). They're user config — refreshed only when the shared store
    //  raises Changed for a crosshair_* name (console/menu sets are instant; the hot path does zero lookups).
    // -------------------------------------------------------------------------------------------------

    private bool _cvarCacheHooked;
    private Action<string>? _cvarCacheHook;
    private float _cvEnabled = 1f;
    private float _cvEffectTime;
    private float _cvColorSpecial;
    private float _cvHitIndicationSpeed = 5f;
    private float _cvPickupSpeed = 4f;

    private void EnsureCvarCache()
    {
        if (_cvarCacheHooked)
            return;
        _cvarCacheHooked = true;
        RefreshCvarCache();
        _cvarCacheHook = name =>
        {
            if (name.StartsWith("crosshair_", StringComparison.OrdinalIgnoreCase))
                RefreshCvarCache();
        };
        Menu.MenuState.Cvars.Changed += _cvarCacheHook;
    }

    private void RefreshCvarCache()
    {
        _cvEnabled = GlobalF("crosshair_enabled", 1f);
        _cvEffectTime = Mathf.Abs(GlobalF("crosshair_effect_time", EffectTime));
        _cvColorSpecial = GlobalF("crosshair_color_special", PerWeaponColor ? 1f : 0f);
        _cvHitIndicationSpeed = GlobalF("crosshair_hitindication_speed", 5f);
        _cvPickupSpeed = GlobalF("crosshair_pickup_speed", 4f);
    }

    public override void _ExitTree()
    {
        base._ExitTree();   // HudPanel unsubscribes its own config hook there
        // The cvar store is session-static; drop the hook so a freed panel (HUD rebuild) doesn't leak.
        if (_cvarCacheHook is not null)
        {
            Menu.MenuState.Cvars.Changed -= _cvarCacheHook;
            _cvarCacheHook = null;
            _cvarCacheHooked = false;
        }
    }

    public override void _Process(double delta)
    {
        EnsureCvarCache();

        _localClock += delta;
        _lastDelta = delta > 0.0 ? delta : _lastDelta;
        float dt = (float)_lastDelta;

        bool dirty = false;

        // QC hitindication block: unaccounted_damage sets hitindication_crosshair_size=1, then it ramps down by
        // crosshair_hitindication_speed*frametime. We drive the QC-style countdown from HitFlash (the owner pulses
        // HitFlash=1 on a confirmed hit) so the existing HitFlash contract still triggers the indication.
        if (HitFlash > 0f)
        {
            _hitIndSize = 1f;
            HitFlash = Mathf.Max(0f, HitFlash - dt / Mathf.Max(0.001f, HitDecay));
            dirty = true;
        }
        if (_hitIndSize > 0f)
        {
            _hitIndSize = Mathf.Max(0f, _hitIndSize - _cvHitIndicationSpeed * dt);
            dirty = true;
        }

        // QC pickup block: PulsePickup() sets _pickupSize=1, then it ramps down by crosshair_pickup_speed*frametime.
        if (_pickupSize > 0f)
        {
            _pickupSize = Mathf.Max(0f, _pickupSize - _cvPickupSpeed * dt);
            dirty = true;
        }

        if (FiringRing > 0f)
        {
            FiringRing = Mathf.Max(0f, FiringRing - dt / Mathf.Max(0.001f, FiringDecay));
            dirty = true;
        }

        // True-aim classification (QC: run TrueAimCheck every frame while the crosshair is live). Gated on the
        // master toggle so the two forward traces don't fire when the crosshair is disabled (QC runs TrueAimCheck
        // only inside the HUD_Crosshair draw guard, after the crosshair_enabled check).
        ShotType newShot = _cvEnabled != 0f ? ComputeShotType() : ShotType.HitWorld;
        if (newShot != ShotResult)
        {
            ShotResult = newShot;
            dirty = true;
        }

        // Detect a weapon-switch crosshair change and (re)start the cross-fade (QC wcross_name change block).
        Texture2D? resolved = ResolveCrosshair(ActiveWeapon());
        float effectTime = EffectTimeCvar();
        if (!ReferenceEquals(resolved, _lastResolved))
        {
            if (effectTime > 0f && (resolved is not null || _lastResolved is not null))
            {
                _crossPrev = _lastResolved;
                _changeStartTime = CurrentTime();
                _changeDoneTime = _changeStartTime + effectTime;
            }
            _lastResolved = resolved;
            dirty = true;
        }
        // Keep repainting while a switch cross-fade or the goal lerp is in flight.
        if (CurrentTime() < _changeDoneTime || CurrentTime() < _changeDoneTimeGoal)
            dirty = true;

        // The rings follow live weapon state, so keep repainting while a ring weapon is active.
        if (HasAnyRing())
            dirty = true;

        // [T41] objective rings track live match state (held-nade charge / capture / thaw), so keep repainting
        // while any of them is non-zero so the fill animates smoothly as the stat updates.
        if (NadeTimer > 0f || CaptureProgress > 0f || ReviveProgress > 0f)
            dirty = true;

        // Health-based color animates (over-100 flash / low-health pulse), and rainbow re-rolls on a timer, so
        // keep painting in those modes.
        ColorMode cmode = ColorModeCvar();
        if (cmode == ColorMode.Health || cmode == ColorMode.Rainbow)
            dirty = true;

        if (dirty) QueueRedraw();
    }

    // -------------------------------------------------------------------------------------------------
    //  Public animation triggers (the net/match layer calls these)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// QC <c>autocvar_crosshair_pickup</c>: pulse the crosshair on an item pickup. The net layer calls this when
    /// <c>STAT(LAST_PICKUP)</c> advances (QC <c>pickup_crosshair_size = 1</c>); the crosshair then bumps its
    /// scale by <c>sin(pickup_crosshair_size) * crosshair_pickup</c> while the size counts back down.
    /// </summary>
    public void PulsePickup()
    {
        _pickupSize = 1f;
        QueueRedraw();
    }

    private float CurrentTime()
    {
        if (Now >= 0.0) return (float)Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return (float)_localClock;
    }

    private Weapon? ActiveWeapon() => Player is not null ? Inventory.CurrentWeapon(Player) : null;

    // -------------------------------------------------------------------------------------------------
    //  Cvar reads (engine crosshair_* cvars; properties remain the fallback for non-cvar-wired callers)
    // -------------------------------------------------------------------------------------------------

    private float EffectTimeCvar()
    {
        EnsureCvarCache();
        return _cvEffectTime;
    }

    private ColorMode ColorModeCvar()
    {
        EnsureCvarCache();
        int v = (int)_cvColorSpecial;
        return v switch { 1 => ColorMode.Weapon, 2 => ColorMode.Health, 3 => ColorMode.Rainbow, _ => ColorMode.Fixed };
    }

    protected override void DrawPanel()
    {
        Vector2 center = Size2 * 0.5f;

        // crosshair_chase (QC crosshair.qc HUD_Crosshair, the chase_active block + crosshairs.cfg:51): in third
        // person the geometric panel centre no longer matches where the shot lands, so re-project. Trace forward
        // from the player origin (CrosshairTrace.ChaseImpact does the eye-height + max_shot_distance worldonly
        // trace) and project the impact through the live chase camera; draw the crosshair there instead of dead
        // centre. Falls back to the geometric centre when chase is off, the cvar disables it, no camera is fed, or
        // the impact projects off-screen (behind the camera). This closes registry cl-crosshair.core.origin_chase.
        // DEFERRED (Phase 2 third-person wiring): the crosshair_chase_playeralpha 0.25 body-alpha fade while the
        // chase view is obstructed needs a player-model alpha hook that does not exist on this path yet.
        if (GlobalF("crosshair_chase", 1f) != 0f && ChaseActive && ChaseCamera3D is not null && Player is not null
            && TryGetChaseForward(out NVec3 chaseForward))
        {
            NVec3 impact = CrosshairTrace.ChaseImpact(Player.Origin, ViewHeight, chaseForward);
            if (CrosshairTrace.ProjectToScreen(ChaseCamera3D, impact, out Vector2 sp))
                center = sp;
        }

        // [T41] objective rings (QC view.qc HUD_Draw 1006-1022): NADE_TIMER > CAPTURE_PROGRESS > REVIVE_PROGRESS,
        // drawn FIRST so they're independent of the crosshair master toggle below — in QC these live in HUD_Draw,
        // not HUD_Crosshair, so a hidden crosshair (crosshair_enabled 0) still shows the life-or-death nade ring.
        DrawObjectiveRings(center);

        // QC: crosshair_enabled is the master toggle; crosshair "0" / size 0 / alpha 0 hide it.
        if (GlobalF("crosshair_enabled", 1f) == 0f) return;

        Weapon? weapon = ActiveWeapon();

        float baseAlpha = GlobalF("crosshair_alpha", Color.A);
        if (baseAlpha <= 0f) baseAlpha = Color.A;

        // ---- base color (QC crosshair_getcolor: weapon / health / fixed) ----
        ColorMode mode = ColorModeCvar();
        Color c = BaseColor(mode, weapon, baseAlpha);

        // ---- goal scale (QC wcross_scale): start at 1, add pickup + hit-indication bumps, divide on teammate ----
        float scale = 1f;
        Color hitTint = c; // running color the hit-indication tint is folded into (QC wcross_color += sin*col)

        // pickup pulse (QC: wcross_scale += sin(pickup_crosshair_size) * autocvar_crosshair_pickup).
        if (_pickupSize > 0f)
            scale += Mathf.Sin(_pickupSize) * GlobalF("crosshair_pickup", 0.25f);

        // hit indication (QC: scale bump + per-channel color add, weapon mode uses the _per_weapon_color cvar).
        if (_hitIndSize > 0f)
        {
            float ind = GlobalF("crosshair_hitindication", 0.5f);
            scale += Mathf.Sin(_hitIndSize) * ind;

            Vector3 col = mode == ColorMode.Weapon
                ? ParseRgbVec("crosshair_hitindication_per_weapon_color", new Vector3(10f, 10f, 10f))
                : ParseRgbVec("crosshair_hitindication_color", new Vector3(10f, -10f, -10f));
            float s = Mathf.Sin(_hitIndSize);
            hitTint = new Color(
                Mathf.Clamp(hitTint.R + s * col.X, 0f, 1f),
                Mathf.Clamp(hitTint.G + s * col.Y, 0f, 1f),
                Mathf.Clamp(hitTint.B + s * col.Z, 0f, 1f),
                hitTint.A);
        }
        // Preserve the legacy HitFlash lerp on top so the existing HitColor contract still shows even when the
        // _per_weapon_color channels are clamped flat (the two are equivalent cues; QC has only the channel add).
        if (HitFlash > 0f)
            hitTint = hitTint.Lerp(HitColor, Mathf.Clamp(HitFlash, 0f, 1f));
        c = hitTint;

        // ---- T21 true-aim signalling (QC: HITTEAM shrinks; blur dims the alpha) ----
        bool blur = false;
        if (HitTestCvar())
        {
            // QC: wcross_scale /= autocvar_crosshair_hittest — the SAME cvar that gates the hit-test is the shrink
            // divisor. At the stock default (crosshair_hittest 1) this is a no-op (no shrink); a user who sets
            // crosshair_hittest > 1 gets the teammate shrink. (Falls back to 1 = no shrink, like Base, when the
            // cvar store is unavailable.)
            float hittestDiv = GlobalF("crosshair_hittest", 1f);
            if (ShotResult == ShotType.HitTeam && hittestDiv > 0f)
                scale /= hittestDiv;
            if ((ShotResult == ShotType.HitTeam && BlurTeammateCvar())
                || (ShotResult == ShotType.HitObstruction && BlurWallCvar()))
                blur = true;
        }
        float goalAlpha = baseAlpha * (blur ? 0.75f : 1f); // QC wcross_alpha *= 0.75 on a blurred target

        // ---- goal-based smooth scale/alpha/color lerp (QC wcross_changedonetime block) ----
        SmoothGoal(ref scale, ref goalAlpha, ref c);

        c = new Color(c.R, c.G, c.B, goalAlpha);

        // Numbered crosshair art (QC gfx/crosshair<N>), or the active weapon's own pic when crosshair_per_weapon
        // is on (QC wcross_name = e.w_crosshair); else the vector crosshair. The real Xonotic art resolves from
        // the mounted game data via TextureCache's VFS resolver.
        Texture2D? pic = ResolveCrosshair(weapon);

        // Resolution: QC scales the pic by crosshair_size, then by the per-weapon size multiplier in per-weapon
        // mode (QC wcross_resolution *= e.w_crosshair_size). With scalefade the size lives in wcross_scale; we
        // always fold it into the draw scale.
        float resolution = GlobalF("crosshair_size", 0.4f) * BasePicScale() * PerWeaponSizeMult(weapon);
        float drawScale = scale * resolution;
        // QC: skip when scale/alpha collapse. Also reject non-finite values (a NaN drawScale/alpha would slip past
        // a `< 0.001f` test — NaN compares false — and feed NaN-sized rects into the draw calls / off-panel garbage).
        if (!(drawScale >= 0.001f) || !(c.A >= 0.001f)) return;

        // ---- weapon-stat rings (QC crosshair_ring block) — drawn UNDER the crosshair pic, like QC ----
        DrawWeaponRings(center, weapon, c, baseAlpha, drawScale, pic);

        // ---- weapon-switch cross-fade (QC wcross_name_changedonetime block) ----
        float effectTime = EffectTimeCvar();
        float fadeIn = 1f;
        if (effectTime > 0f && CurrentTime() < _changeDoneTime && _changeDoneTime > _changeStartTime)
        {
            // f: 1 at switch start → 0 at switch end (QC: (changedonetime - time)/(changedonetime - changestarttime)).
            float f = (_changeDoneTime - CurrentTime()) / (_changeDoneTime - _changeStartTime);
            f = Mathf.Clamp(f, 0f, 1f);
            fadeIn = 1f - f;                 // the new crosshair's alpha ramps up

            // Draw the outgoing crosshair fading out underneath (QC CROSSHAIR_DRAW of wcross_name_goal_prev_prev).
            if (_crossPrev is not null && !ReferenceEquals(_crossPrev, pic))
            {
                var outc = new Color(c.R, c.G, c.B, c.A * f);
                DrawCrosshairTexture(_crossPrev, center, outc, drawScale, blur);
            }
        }

        Color drawColor = new(c.R, c.G, c.B, c.A * fadeIn);
        if (pic is not null)
            DrawCrosshairTexture(pic, center, drawColor, drawScale, blur);
        else
            DrawVectorCrosshair(center, drawColor, drawScale);

        // ---- center dot (QC autocvar_crosshair_dot) ----
        if (GlobalF("crosshair_dot", 0f) != 0f)
            DrawDot(center, c, drawScale, fadeIn);
    }

    // -------------------------------------------------------------------------------------------------
    //  Color (QC crosshair_getcolor)
    // -------------------------------------------------------------------------------------------------

    private Color BaseColor(ColorMode mode, Weapon? weapon, float alpha)
    {
        switch (mode)
        {
            case ColorMode.Weapon:
                if (weapon is not null)
                    return WeaponHud.ColorOf(weapon, alpha);
                break;
            case ColorMode.Health:
                if (Player is not null)
                {
                    float h = Player.GetResource(ResourceType.Health);
                    float a = Player.GetResource(ResourceType.Armor);
                    return HealthArmorColor(h, a, alpha);
                }
                break;
            case ColorMode.Rainbow:
                return RainbowColor(alpha);
        }
        // QC default: stov(autocvar_crosshair_color), else the property fallback.
        if (TryParseRgb(GlobalStr("crosshair_color"), out Color cc))
            return new Color(cc.R, cc.G, cc.B, alpha);
        return new Color(Color.R, Color.G, Color.B, alpha);
    }

    /// <summary>QC <c>crosshair_color_special == 2</c>: colour by an "ideal max damage" health+armor number
    /// through the QC <c>HUD_Get_Num_Color</c> 5-stop ramp (max 200).</summary>
    private Color HealthArmorColor(float health, float armor, float alpha)
    {
        // healtharmor_maxdamage (common/util.qc): the damage you can still take given health+armor.
        const float armorBlock = 0.7f; // g_balance_armor_blockpercent default
        float healthDamage = (health - 1f) / (1f - armorBlock);
        float armorDamage = armor + (health - 1f);
        float v = Mathf.Min(healthDamage, armorDamage);
        if (v < 0f) v = 0f;
        float hp = Mathf.Floor(v + 1f);
        return NumRampColor(hp, 200f, alpha);
    }

    /// <summary>Port of QC <c>HUD_Get_Num_Color(value, max, false)</c> — the 5-color health ramp (no blink).</summary>
    private static Color NumRampColor(float value, float max, float alpha)
    {
        if (max <= 0f) return new Color(1f, 1f, 1f, alpha);
        var color100 = new Vector3(0f, 1f, 0f);
        var color75 = new Vector3(0.4f, 0.9f, 0f);
        var color50 = new Vector3(1f, 1f, 1f);
        var color25 = new Vector3(1f, 1f, 0.2f);
        var color10 = new Vector3(1f, 0f, 0f);

        float pct = value / max * 100f;
        Vector3 cc;
        if (pct > 100f) cc = color100;
        else if (pct > 75f) cc = Between(color75, color100, pct, 75f, 100f);
        else if (pct > 50f) cc = Between(color50, color75, pct, 50f, 75f);
        else if (pct > 25f) cc = Between(color25, color50, pct, 25f, 50f);
        else if (pct > 10f) cc = Between(color10, color25, pct, 10f, 25f);
        else cc = color10;
        return new Color(Mathf.Clamp(cc.X, 0f, 1f), Mathf.Clamp(cc.Y, 0f, 1f), Mathf.Clamp(cc.Z, 0f, 1f), alpha);
    }

    private static Vector3 Between(Vector3 lo, Vector3 hi, float pct, float min, float max)
        => lo + (hi - lo) * ((pct - min) / (max - min));

    /// <summary>
    /// QC <c>crosshair_color_special == 3</c> (rainbow): a random colour re-rolled every
    /// <c>crosshair_color_special_rainbow_delay</c> seconds (QC <c>randomvec() * ..._rainbow_brightness</c>),
    /// latched between re-rolls. QC <c>randomvec()</c> returns a vector with each component in [-1,1]; scaled by
    /// the brightness and clamped to [0,1] at draw, this gives the bright flickering rainbow crosshair.
    /// </summary>
    private Color RainbowColor(float alpha)
    {
        float now = CurrentTime();
        float brightness = GlobalF("crosshair_color_special_rainbow_brightness", 20f);
        float delay = GlobalF("crosshair_color_special_rainbow_delay", 0.1f);

        // QC: if (time >= rainbow_last_flicker) re-roll and schedule the next flicker. Seed on first use so the
        // first frame already has a colour (QC's statics start at 0, which also re-rolls on the first frame).
        if (!_rainbowSeeded || now >= _rainbowNextFlicker)
        {
            _rainbowColor = RandomVec() * brightness;
            _rainbowNextFlicker = now + delay;
            _rainbowSeeded = true;
        }

        return new Color(
            Mathf.Clamp(_rainbowColor.X, 0f, 1f),
            Mathf.Clamp(_rainbowColor.Y, 0f, 1f),
            Mathf.Clamp(_rainbowColor.Z, 0f, 1f),
            alpha);
    }

    /// <summary>QC <c>randomvec()</c>: a random vector with each component uniform in [-1,1].</summary>
    private static Vector3 RandomVec() => new(
        (float)GD.RandRange(-1.0, 1.0),
        (float)GD.RandRange(-1.0, 1.0),
        (float)GD.RandRange(-1.0, 1.0));

    // -------------------------------------------------------------------------------------------------
    //  Goal-based smooth scale/alpha (QC wcross_changedonetime block)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Port of the QC <c>wcross_changedonetime</c> easing: when the goal scale/alpha/color changes, ease the
    /// displayed value toward it over <see cref="EffectTime"/> (frame-rate-independent exponential approach, QC
    /// <c>f = frametime / (changedonetime - time + frametime)</c>). The colour (RGB) is eased exactly like the
    /// scale and alpha (QC crosshair.qc:444 <c>wcross_color = f*wcross_color + (1-f)*wcross_color_prev</c>); the
    /// raw RGB may exceed [0,1] (rainbow brightness / hit-indication channel adds) — it is eased un-clamped and
    /// clamped only at draw, matching QC. Alpha is carried through <paramref name="alpha"/> (QC wcross_alpha).
    /// </summary>
    private void SmoothGoal(ref float scale, ref float alpha, ref Color color)
    {
        float effectTime = EffectTimeCvar();
        float now = CurrentTime();
        var rgb = new Vector3(color.R, color.G, color.B);

        if (!_smoothSeeded)
        {
            _scalePrev = scale; _alphaPrev = alpha; _colorPrev = rgb;
            _scaleGoalPrev = scale; _alphaGoalPrev = alpha; _colorGoalPrev = rgb;
            _smoothSeeded = true;
        }

        // A changed goal (re)arms the ease window (QC: if scale/alpha/color goal changed, changedonetime = time + f).
        if (scale != _scaleGoalPrev || alpha != _alphaGoalPrev || rgb != _colorGoalPrev)
            _changeDoneTimeGoal = now + effectTime;
        _scaleGoalPrev = scale;
        _alphaGoalPrev = alpha;
        _colorGoalPrev = rgb;

        if (effectTime > 0f && now < _changeDoneTimeGoal)
        {
            float dt = (float)_lastDelta;
            float f = dt / Mathf.Max(0.0001f, _changeDoneTimeGoal - now + dt);
            scale = f * scale + (1f - f) * _scalePrev;
            alpha = f * alpha + (1f - f) * _alphaPrev;
            rgb = f * rgb + (1f - f) * _colorPrev;
        }

        _scalePrev = scale;
        _alphaPrev = alpha;
        _colorPrev = rgb;

        color = new Color(rgb.X, rgb.Y, rgb.Z, color.A);
    }

    // -------------------------------------------------------------------------------------------------
    //  Weapon-stat rings (QC crosshair_ring block)
    // -------------------------------------------------------------------------------------------------

    private bool HasAnyRing()
    {
        Weapon? w = ActiveWeapon();
        if (w is null) return false;
        // legacy charge ring path
        if (ChargeFraction >= 0f && WeaponHud.IsChargeWeapon(w)) return true;
        return (ArcHeat > 0f) || (HagarLoad > 0f) || (MineCount > 0f)
            || (ClipSize > 0f && ClipLoad >= 0f) || (ChargePool >= 0f);
    }

    /// <summary>
    /// Resolve which weapon-stat ring to draw and paint it (QC the big <c>crosshair_ring</c> if/else chain).
    /// Priority matches QC: Vortex/Overkill charge (with an optional inner chargepool ring) → Mine Layer →
    /// Hagar → reload ammo ring → Arc heat. Falls back to the legacy <see cref="ChargeFraction"/> /
    /// <see cref="FiringRing"/> drawing when no networked ring stat is fed.
    /// </summary>
    private void DrawWeaponRings(Vector2 center, Weapon? weapon, Color crossColor, float baseAlpha,
        float drawScale, Texture2D? pic)
    {
        bool ringEnabled = GlobalF("crosshair_ring", 1f) != 0f;
        bool reloadEnabled = GlobalF("crosshair_ring_reload", 1f) != 0f;
        bool drewStatRing = false;

        if ((ringEnabled || reloadEnabled) && weapon is not null)
            drewStatRing = DrawStatRing(center, weapon, crossColor, baseAlpha, drawScale, pic, ringEnabled, reloadEnabled);

        // Legacy fallback rings (kept so existing ChargeFraction/FiringRing feeders still render).
        if (!drewStatRing && weapon is not null && ChargeFraction >= 0f && WeaponHud.IsChargeWeapon(weapon))
            DrawRing(center, RingRadius, Mathf.Clamp(ChargeFraction, 0f, 1f),
                new Color(crossColor.R, crossColor.G, crossColor.B, 0.9f * crossColor.A));

        if (FiringRing > 0f)
        {
            float r = RingRadius * (1f + (1f - FiringRing) * 0.6f);
            DrawRing(center, r, 1f, new Color(crossColor.R, crossColor.G, crossColor.B, FiringRing * crossColor.A),
                segments: 40);
        }
    }

    /// <summary>
    /// [T41] Port of the QC objective-ring block (view.qc HUD_Draw 1006-1022): draw exactly ONE ring around
    /// <paramref name="center"/> — the held-nade charge (NADE_TIMER), else the objective-capture progress
    /// (CAPTURE_PROGRESS), else the freeze-tag thaw (REVIVE_PROGRESS) — using the <c>gfx/crosshair_ring</c> art
    /// clipped to the [0,1] fill (<see cref="DrawRingPic"/> == QC <c>DrawCircleClippedPic</c>). The nade ring's
    /// colour shifts from cyan toward red as it charges (QC <c>'0.25 0.90 1' + vec3(t,-t,-t)</c>); capture/revive
    /// use the flat cyan. The QC <c>autocvar_cl_nade_timer</c> gate hides the nade ring when 0. Alpha is
    /// <c>hud_colorflash_alpha</c> (QC), defaulting to 1.
    /// </summary>
    private void DrawObjectiveRings(Vector2 center)
    {
        float alpha = GlobalF("hud_colorflash_alpha", 1f);
        if (alpha <= 0f) alpha = 1f;
        float radius = ObjectiveRingRadius;

        // NADE_TIMER — top priority (a matter of life and death). Gated on cl_nade_timer like QC.
        float nade = Mathf.Clamp(NadeTimer, 0f, 1f);
        int nadeMode = (int)GlobalF("cl_nade_timer", NadeTimerMode);
        if (nade > 0f && nadeMode != 0)
        {
            // QC: col = '0.25 0.90 1' + vec3(t, -t, -t) — cyan that reddens as the fuse burns down.
            var col = new Color(
                Mathf.Clamp(0.25f + nade, 0f, 1f),
                Mathf.Clamp(0.90f - nade, 0f, 1f),
                Mathf.Clamp(1.00f - nade, 0f, 1f),
                alpha);
            DrawRingPic(center, radius, nade, "gfx/crosshair_ring", col);
            return;
        }

        // CAPTURE_PROGRESS — 2nd priority. Flat cyan.
        float capture = Mathf.Clamp(CaptureProgress, 0f, 1f);
        if (capture > 0f)
        {
            DrawRingPic(center, radius, capture, "gfx/crosshair_ring", new Color(0.25f, 0.90f, 1f, alpha));
            return;
        }

        // REVIVE_PROGRESS — 3rd priority. Flat cyan.
        float revive = Mathf.Clamp(ReviveProgress, 0f, 1f);
        if (revive > 0f)
            DrawRingPic(center, radius, revive, "gfx/crosshair_ring", new Color(0.25f, 0.90f, 1f, alpha));
    }

    private bool DrawStatRing(Vector2 center, Weapon weapon, Color crossColor, float baseAlpha, float drawScale,
        Texture2D? pic, bool ringEnabled, bool reloadEnabled)
    {
        // The ring radius scales with the crosshair pic size like QC (wcross_size.x * resolution * ring_scale).
        // Guard against a not-yet-loaded / placeholder texture reporting a 0 or NaN size (the ring would then
        // collapse to radius 0 and silently vanish even though there's live charge/heat to show).
        float picW = pic?.GetSize().X ?? 0f;
        if (!(picW > 0f)) picW = RingRadius * 2f; // !(>0) also catches NaN
        string netName = weapon.NetName ?? string.Empty;

        float ringValue = 0f, ringAlpha = 0f, ringScale = GlobalF("crosshair_ring_size", 2f);
        string ringImage = "gfx/crosshair_ring";
        Color ringRgb = crossColor;

        float innerValue = 0f, innerAlpha = 0f;
        string innerImage = "gfx/crosshair_ring_inner";
        Color innerRgb = crossColor;
        bool hasInner = false;

        // 1) Vortex / Overkill-Nex charge ring (QC ring_vortex_enabled).
        bool vortexLike = netName is "vortex" or "vaporizer" || netName.Contains("nex");
        float charge = ChargeRingValue(weapon);
        if (ringEnabled && GlobalF("crosshair_ring_vortex", 1f) != 0f && vortexLike && charge > 0f)
        {
            // inner ring: chargepool, else a moving-average of the charge (QC currentcharge block).
            if (ChargePool >= 0f || _useChargePool)
            {
                _useChargePool = true;
                innerValue = Mathf.Clamp(ChargePool, 0f, 1f);
            }
            else
            {
                float rate = GlobalF("crosshair_ring_vortex_currentcharge_movingavg_rate", 0.05f);
                _vortexChargeAvg = (1f - rate) * _vortexChargeAvg + rate * charge;
                float cscale = GlobalF("crosshair_ring_vortex_currentcharge_scale", 30f);
                innerValue = Mathf.Clamp(cscale * (charge - _vortexChargeAvg), 0f, 1f);
            }
            innerAlpha = GlobalF("crosshair_ring_vortex_inner_alpha", 0.15f);
            innerRgb = new Color(
                GlobalF("crosshair_ring_vortex_inner_color_red", 0.8f),
                GlobalF("crosshair_ring_vortex_inner_color_green", 0f),
                GlobalF("crosshair_ring_vortex_inner_color_blue", 0f), 1f);
            hasInner = true;

            ringValue = Mathf.Clamp(charge, 0f, 1f);
            ringAlpha = GlobalF("crosshair_ring_vortex_alpha", 0.15f);
            ringRgb = crossColor;
            ringImage = "gfx/crosshair_ring_nexgun";
        }
        // 2) Mine Layer ring.
        else if (ringEnabled && netName == "minelayer" && MineCount > 0f && MineLimit > 0f
                 && GlobalF("crosshair_ring_minelayer", 1f) != 0f)
        {
            ringValue = Mathf.Clamp(MineCount / MineLimit, 0f, 1f);
            ringAlpha = GlobalF("crosshair_ring_minelayer_alpha", 0.15f);
            ringRgb = crossColor;
            ringImage = "gfx/crosshair_ring";
        }
        // 3) Hagar load ring.
        else if (ringEnabled && netName == "hagar" && HagarLoad > 0f && HagarLoadMax > 0f
                 && GlobalF("crosshair_ring_hagar", 1f) != 0f)
        {
            ringValue = Mathf.Clamp(HagarLoad / HagarLoadMax, 0f, 1f);
            ringAlpha = GlobalF("crosshair_ring_hagar_alpha", 0.15f);
            ringRgb = crossColor;
            ringImage = "gfx/crosshair_ring";
        }
        // 4) Reload ammo ring (QC ring_reload — clip load / clip size).
        else if (reloadEnabled && ClipSize > 0f && ClipLoad >= 0f)
        {
            ringValue = Mathf.Clamp(ClipLoad / ClipSize, 0f, 1f);
            ringScale = GlobalF("crosshair_ring_reload_size", 2.5f);
            ringAlpha = GlobalF("crosshair_ring_reload_alpha", 0.2f);
            ringRgb = crossColor;
            // QC: the Rifle (clip 80) uses its own ring art.
            ringImage = (netName == "rifle" && Mathf.IsEqualApprox(ClipSize, 80f))
                ? "gfx/crosshair_ring_rifle" : "gfx/crosshair_ring";
        }
        // 5) Arc heat ring.
        else if (ringEnabled && netName == "arc" && ArcHeat > 0f && GlobalF("crosshair_ring_arc", 1f) != 0f)
        {
            float heat = Mathf.Clamp(ArcHeat, 0f, 1f);
            ringValue = heat;
            ringAlpha = (1f - heat) * GlobalF("crosshair_ring_arc_cold_alpha", 0.2f)
                      + heat * GlobalF("crosshair_ring_arc_hot_alpha", 0.5f);
            Vector3 hot = ParseRgbVec("crosshair_ring_arc_hot_color", new Vector3(1f, 0f, 0f));
            ringRgb = new Color(
                (1f - heat) * crossColor.R + heat * hot.X,
                (1f - heat) * crossColor.G + heat * hot.Y,
                (1f - heat) * crossColor.B + heat * hot.Z, 1f);
            ringImage = "gfx/crosshair_ring";
        }

        if (ringValue <= 0f && innerValue <= 0f) return false;

        // QC weapon-switch ring fade (fade the ring out then in across the switch window).
        float ringFade = SwitchRingFade(ringValue > 0f);

        // Ring radius in pixels: half of (pic width * ring_scale), scaled by the live draw scale.
        float ringRadius = picW * drawScale * ringScale * 0.5f;
        float innerRadius = picW * drawScale * ringScale * 0.5f;

        if (GlobalF("crosshair_ring_inner", 0f) != 0f && hasInner && innerValue > 0f)
            DrawRingPic(center, innerRadius, innerValue, innerImage,
                new Color(innerRgb.R, innerRgb.G, innerRgb.B, baseAlpha * innerAlpha * ringFade));

        if (ringValue > 0f)
            DrawRingPic(center, ringRadius, ringValue, ringImage,
                new Color(ringRgb.R, ringRgb.G, ringRgb.B, baseAlpha * ringAlpha * ringFade));

        return true;
    }

    /// <summary>The Vortex-family charge fraction: the networked <see cref="ChargeFraction"/> when fed, else 0.</summary>
    private float ChargeRingValue(Weapon weapon)
    {
        if (ChargeFraction >= 0f && WeaponHud.IsChargeWeapon(weapon))
            return Mathf.Clamp(ChargeFraction, 0f, 1f);
        return 0f;
    }

    /// <summary>QC ring weapon-switch fade (wcross_ring_prev): ring alpha fades out then in across the switch.</summary>
    private float SwitchRingFade(bool ringNow)
    {
        float effectTime = EffectTimeCvar();
        if (effectTime <= 0f) return 1f;
        float f = (CurrentTime() - _changeStartTime) / effectTime;
        if (f >= 1f)
        {
            _ringPrev = ringNow;
            return 1f;
        }
        f = Mathf.Clamp(f, 0f, 1f);
        return _ringPrev ? Mathf.Abs(1f - f) : f;
    }

    // -------------------------------------------------------------------------------------------------
    //  T21: true-aim classification — port of crosshair.qc TrueAimCheck() + EnemyHitCheck().
    // -------------------------------------------------------------------------------------------------

    private bool HitTestCvar() => GlobalF("crosshair_hittest", HitTest ? 1f : 0f) != 0f;
    private bool BlurWallCvar() => GlobalF("crosshair_hittest_blur_wall", BlurWall ? 1f : 0f) != 0f;
    private bool BlurTeammateCvar() => GlobalF("crosshair_hittest_blur_teammate", BlurTeammate ? 1f : 0f) != 0f;

    /// <summary>
    /// Forward-trace from the eye and classify what the active weapon would hit (QC <c>TrueAimCheck</c>). Returns
    /// <see cref="ShotType.HitWorld"/> when hit-testing is off, there is no aim ray, or no trace service is wired.
    /// </summary>
    private ShotType ComputeShotType()
    {
        if (!HitTestCvar() || Player is null || Api.Services is null)
            return ShotType.HitWorld;

        Weapon? weapon = ActiveWeapon();
        // QC: WEP_FLAG_NOTRUEAIM weapons (Mortar/Hook/Porto/Tuba) launch straight from the eye — no true-aim.
        if (weapon is not null && (weapon.SpawnFlags & WeaponFlags.NoTrueAim) != 0)
            return ShotType.HitWorld;

        if (!TryGetAimRay(out NVec3 origin, out NVec3 forward))
            return ShotType.HitWorld;

        // A non-finite ray (NaN/Inf origin or forward — e.g. a degenerate view transform mid-frame) would feed
        // NaN endpoints into the trace service. Bail to HitWorld instead of tracing garbage (and the QueueRedraw
        // dirty check stays stable since NaN != NaN would otherwise repaint forever).
        if (!IsFinite(origin) || !IsFinite(forward))
            return ShotType.HitWorld;

        // (perf) Reuse the last classification when the aim ray is effectively unchanged (standing still) or we
        // traced very recently — the two world traces below are ~6 ms on big maps and only drive a cosmetic tint.
        if (TrueAimCanReuse(origin, forward))
            return ShotResult;

        // QC: the rail family (Vortex/Vaporizer; QC also Overkill-Nex, not present in this port) traces against
        // players too (MOVE_NORMAL); everything else uses MOVE_NOMONSTERS for the aim line. Projectile-size
        // weapons additionally get a non-zero trace box.
        bool railLike = weapon is not null && weapon.NetName is "vortex" or "vaporizer";
        MoveFilter aimFilter = railLike ? MoveFilter.Normal : MoveFilter.NoMonsters;
        (NVec3 mins, NVec3 maxs) = ProjectileBox(weapon);

        float range = WeaponFiring.MaxShotDistance; // QC max_shot_distance

        // The two forward traces reach into the live engine trace service. A failure there must NOT escape into
        // _Process (it runs every frame — an unhandled throw would spam the log and stall HUD repaint); degrade
        // to HitWorld (the QC default) so the crosshair stays at full strength.
        // (perf) scope the two world traces so they are never invisible in proc:other again.
        using var _trueAimScope = VortexArena.Game.Client.FrameProfiler.Scope("hud.trueaim");
        try
        {
            // 1) Aim line: where is the player pointing? (QC traceline(traceorigin, ... view_forward * max_shot_distance)).
            // Routed through the shared CrosshairTrace primitive (dedup of the zero-box forward trace) — same eye,
            // forward, range, filter and ignore-self as the direct Api.Trace.Trace it replaces.
            CrosshairTrace.Hit aim = CrosshairTrace.TraceForward(origin, forward, range, aimFilter, Player);
            NVec3 trueAimPoint = aim.PointQuake + forward; // QC nudges the point a little forward for the final box trace

            // QC g_trueaim_minrange: keep the aim point at least a short distance ahead so close-range tracing is stable.
            const float trueAimMinRange = 44f; // Xonotic g_trueaim_minrange default
            if (System.Numerics.Vector3.Distance(trueAimPoint, origin) < trueAimMinRange)
                trueAimPoint = origin + forward * trueAimMinRange;

            // 2) Final box trace from the shot origin to the aim point, classify what it touches (QC the second
            //    tracebox + EnemyHitCheck). For an obstruction (the box stops well short of the aim point on the
            //    world) the crosshair signals a blocked shot.
            TraceResult shot = Api.Trace.Trace(origin, mins, maxs, trueAimPoint, MoveFilter.Normal, Player);
            ShotType shottype = EnemyHitCheck(shot);
            if (shottype != ShotType.HitWorld)
                return shottype;

            return ClassifyObstruction(shot, trueAimPoint, mins, maxs);
        }
        catch (System.Exception)
        {
            return ShotType.HitWorld;
        }
    }

    /// <summary>
    /// (perf) True-aim trace gate. Returns true when the caller should REUSE the last <see cref="ShotResult"/>
    /// instead of running the two expensive world traces: either the aim ray is effectively unchanged since the
    /// last trace (standing still / negligible aim drift — the common case, and exactly the user's "slowest while
    /// standing at the spawn" scenario), or we traced within the last <c>1/cl_crosshair_trueaim_rate</c> seconds
    /// while turning. When it returns false it records the ray + schedules the next allowed trace. Set the cvar to
    /// 0 to disable throttling and trace every frame (the faithful QC cadence, only viable on cheap-trace maps).
    /// </summary>
    private bool TrueAimCanReuse(NVec3 origin, NVec3 forward)
    {
        bool viewSame = _trueAimHaveCache
            && NVec3.DistanceSquared(origin, _trueAimLastOrigin) < 0.25f   // < 0.5 qu of eye movement
            && NVec3.Dot(forward, _trueAimLastForward) > 0.99985f;         // < ~1 degree of aim change
        if (viewSame)
            return true;   // standing still / negligible change → the classification can't have changed

        // Cap the re-trace rate while turning. The true-aim tint is a coarse cosmetic hint that does not need to
        // update every frame, so we hold it to cl_crosshair_trueaim_rate Hz (default 30; 0 = per-frame, which the
        // Brush.cs broadphase fix also made cheap). The view-unchanged cache above is the always-on, always-correct
        // part (a static aim ray can't change the classification), so standing still costs zero traces regardless.
        float rate = GlobalF("cl_crosshair_trueaim_rate", 30f);
        if (rate > 0f && _trueAimHaveCache && _localClock < _trueAimNextAt)
            return true;   // turning, but we traced recently → hold the last result until the next slot

        // We are about to trace: record this ray as the cache key and schedule the next permitted trace.
        _trueAimLastOrigin = origin;
        _trueAimLastForward = forward;
        _trueAimHaveCache = true;
        if (rate > 0f)
            _trueAimNextAt = _localClock + 1.0 / rate;
        return false;
    }

    /// <summary>
    /// HITOBSTRUCTION classification for the completed final box trace (split out so the trace itself can sit in a
    /// guard). See <see cref="ComputeShotType"/> for why this only runs for zero-box (hitscan) weapons.
    /// </summary>
    private static ShotType ClassifyObstruction(in TraceResult shot, NVec3 trueAimPoint, NVec3 mins, NVec3 maxs)
    {
        // HITOBSTRUCTION: a wall sits between the shot origin and the aim point. NOTE: QC's *real* obstruction
        // test (HUD_Crosshair, crosshair.qc:312-319) is screen-space — it reprojects the box-trace endpoint to 2D
        // (EnemyHitCheck's project_3d_to_2d into wcross_origin) and flags HITOBSTRUCTION when that 2D point drifts
        // from the previous frame's by > 0.01 of the screen. The 3D-distance form below is the dead `#if 0` block
        // (crosshair.qc:145-151), disabled in QC because it misfires for the rocket launcher / projectile-box
        // weapons. So we only run it for zero-box (hitscan) weapons, matching the RL/projectile exclusion; a full
        // screen-space port would need the projected-endpoint history the view layer doesn't feed us yet.
        bool projectileBox = mins != NVec3.Zero || maxs != NVec3.Zero;
        if (!projectileBox
            && shot.Fraction < 1f
            && System.Numerics.Vector3.Distance(shot.EndPos, trueAimPoint) > maxs.Length() + mins.Length() + 1f)
            return ShotType.HitObstruction;

        return ShotType.HitWorld;
    }

    /// <summary>
    /// Resolve the look direction used to re-project the chase crosshair (QC <c>view_forward</c>). Prefers the
    /// view-layer-fed <see cref="AimForward"/> (the authoritative render look direction); otherwise derives it from
    /// the <see cref="Player"/>'s view angles (QC <c>AngleVectors(view_angles)</c>). Returns false when neither a
    /// fed forward nor a player is available (the caller then keeps the geometric centre).
    /// </summary>
    private bool TryGetChaseForward(out NVec3 forward)
    {
        if (AimForward is { } f && f != NVec3.Zero)
        {
            forward = System.Numerics.Vector3.Normalize(f);
            return true;
        }
        if (Player is null)
        {
            forward = default;
            return false;
        }
        QMath.AngleVectors(Player.Angles, out NVec3 fq, out _, out _);
        forward = fq;
        return forward != NVec3.Zero;
    }

    /// <summary>
    /// Classify a completed trace by the entity it hit (QC <c>EnemyHitCheck</c>): a same-team player is HITTEAM,
    /// any other player is HITENEMY, anything else (world / no entity / spectator) is HITWORLD.
    /// </summary>
    private ShotType EnemyHitCheck(in TraceResult tr)
    {
        if (tr.Ent is not Player hit || ReferenceEquals(hit, Player))
            return ShotType.HitWorld;

        // QC EnemyHitCheck (crosshair.qc:64-67): a spectator-team player is HITWORLD; otherwise a same-team
        // player (only when teamplay is on) is HITTEAM, any other player is HITENEMY. The port detects "teamplay
        // on" the same way the rest of the client does — both players carry a real (nonzero) team only in a team
        // game — so the `hit.Team != 0` guard doubles as the teamplay gate (in a non-team game everyone is team 0
        // and 0 != 0 is false, i.e. no teammates, matching Base where the `teamplay &&` branch is never taken).
        // A spectator is never a live Player trace-target here, so the QC NUM_SPECTATOR->HITWORLD branch is
        // covered by the `tr.Ent is not Player` early-out above.
        if (Player is not null && hit.Team > 0f && Player.Team > 0f && hit.Team == Player.Team)
            return ShotType.HitTeam;

        return ShotType.HitEnemy;
    }

    /// <summary>
    /// Resolve the eye origin + look direction for the true-aim trace. Prefers the view-layer-fed
    /// <see cref="AimOrigin"/>/<see cref="AimForward"/> (the authoritative render eye); otherwise reconstructs
    /// the origin from the player origin + <see cref="ViewHeight"/> and the forward from the player's view angles
    /// (QC AngleVectors of the view angles). Returns false if neither a ray nor a player is available.
    /// </summary>
    private bool TryGetAimRay(out NVec3 origin, out NVec3 forward)
    {
        if (AimForward is { } f && f != NVec3.Zero)
        {
            forward = System.Numerics.Vector3.Normalize(f);
            origin = AimOrigin ?? (Player!.Origin + new NVec3(0f, 0f, ViewHeight));
            return true;
        }

        if (Player is null)
        {
            origin = default;
            forward = default;
            return false;
        }

        // Derive the look direction from the player's view angles (QC view_forward = AngleVectors(view_angles)).
        QMath.AngleVectors(Player.Angles, out NVec3 fq, out _, out _);
        forward = fq;
        origin = AimOrigin ?? (Player.Origin + new NVec3(0f, 0f, ViewHeight));
        return forward != NVec3.Zero;
    }

    /// <summary>
    /// QC <c>TrueAimCheck</c>: the trace box for projectile-size weapons (Devastator/Fireball/Seeker/Electro).
    /// Hitscan weapons use a zero-size point trace.
    /// </summary>
    private static (NVec3 mins, NVec3 maxs) ProjectileBox(Weapon? weapon) => weapon?.NetName switch
    {
        "devastator" => (new NVec3(-3f, -3f, -3f), new NVec3(3f, 3f, 3f)),
        "fireball" => (new NVec3(-16f, -16f, -16f), new NVec3(16f, 16f, 16f)),
        "seeker" => (new NVec3(-2f, -2f, -2f), new NVec3(2f, 2f, 2f)),
        "electro" => (new NVec3(0f, 0f, -3f), new NVec3(0f, 0f, -3f)),
        _ => (NVec3.Zero, NVec3.Zero),
    };

    // -------------------------------------------------------------------------------------------------
    //  Drawing
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// QC per-weapon crosshair table (the weapon <c>w_crosshair</c> / <c>w_crosshair_size</c> ATTRIBs, mirrored
    /// from common/weapons/weapon/*.qh): NetName → (pic name, size multiplier). When <c>crosshair_per_weapon</c>
    /// is on, the active weapon draws its own named pic at <c>crosshair_size * mult</c> instead of the numbered
    /// <c>gfx/crosshair&lt;N&gt;</c>. Weapons absent from the table (or with a commented-out size in Base) fall
    /// back to a 1.0 multiplier. Keyed by the port's NetName.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, (string pic, float size)> PerWeaponPics = new()
    {
        ["arc"] = ("gfx/crosshairhlac", 0.7f),
        ["blaster"] = ("gfx/crosshairlaser", 0.5f),
        ["crylink"] = ("gfx/crosshaircrylink", 0.5f),
        ["devastator"] = ("gfx/crosshairrocketlauncher", 0.7f),
        ["electro"] = ("gfx/crosshairelectro", 0.6f),
        ["fireball"] = ("gfx/crosshairfireball", 1f),   // Base w_crosshair_size commented out → 1.0
        ["hagar"] = ("gfx/crosshairhagar", 0.8f),
        ["hlac"] = ("gfx/crosshairhlac", 0.6f),
        ["hook"] = ("gfx/crosshairhook", 0.5f),
        ["machinegun"] = ("gfx/crosshairuzi", 0.6f),
        ["minelayer"] = ("gfx/crosshairminelayer", 0.9f),
        ["mortar"] = ("gfx/crosshairgrenadelauncher", 0.7f),
        ["porto"] = ("gfx/crosshairporto", 0.6f),
        ["rifle"] = ("gfx/crosshairrifle", 0.6f),
        ["seeker"] = ("gfx/crosshairseeker", 0.8f),
        ["shotgun"] = ("gfx/crosshairshotgun", 0.65f),
        ["tuba"] = ("gfx/crosshairtuba", 1f),           // Base w_crosshair_size commented out → 1.0
        ["vaporizer"] = ("gfx/crosshairminstanex", 0.6f),
        ["vortex"] = ("gfx/crosshairnex", 0.65f),
    };

    private bool PerWeaponEnabled() => GlobalF("crosshair_per_weapon", PerWeaponColor ? 1f : 0f) != 0f;

    /// <summary>QC <c>wcross_resolution *= e.w_crosshair_size</c>: the per-weapon size multiplier when
    /// <c>crosshair_per_weapon</c> is on, else 1.</summary>
    private float PerWeaponSizeMult(Weapon? weapon)
    {
        if (weapon is not null && PerWeaponEnabled())
        {
            // QC e.w_crosshair_size — prefer the size carried on the weapon itself (its ATTRIB); fall back to the
            // mirrored table for weapons that don't override it.
            if (weapon.Crosshair is not null)
                return weapon.CrosshairSize;
            if (PerWeaponPics.TryGetValue(weapon.NetName, out var e))
                return e.size;
        }
        return 1f;
    }

    /// <summary>
    /// Resolve the crosshair texture. In per-weapon mode (QC <c>crosshair_per_weapon</c>) the active weapon's own
    /// <c>w_crosshair</c> pic wins (QC <c>wcross_name = e.w_crosshair</c>); a per-weapon-number override still
    /// applies if one was injected via <see cref="PerWeaponNumber"/>; otherwise the numbered <c>gfx/crosshair&lt;N&gt;</c>
    /// art. Returns null (→ vector crosshair) when the number is 0.
    /// </summary>
    private Texture2D? ResolveCrosshair(Weapon? weapon)
    {
        int number = (int)GlobalF("crosshair", CrosshairNumber);

        // QC: an explicit per-weapon number override (config-injected) takes precedence over everything.
        if (weapon is not null && PerWeaponNumber is not null
            && PerWeaponNumber.TryGetValue(weapon.NetName, out int n))
        {
            if (n <= 0) return null;
            return TextureCache.GetFirst($"gfx/crosshair{n}", $"res://art/hud/crosshairs/crosshair{n}.png");
        }

        // QC: crosshair_per_weapon — use the active weapon's own named crosshair pic (e.w_crosshair). Prefer the
        // pic carried on the weapon itself (its ATTRIB); fall back to the mirrored table by NetName.
        if (weapon is not null && PerWeaponEnabled())
        {
            string? picName = weapon.Crosshair
                ?? (PerWeaponPics.TryGetValue(weapon.NetName, out var e) ? e.pic : null);
            if (picName is not null)
            {
                Texture2D? wp = TextureCache.GetFirst(picName, $"res://art/hud/crosshairs/{System.IO.Path.GetFileName(picName)}.png");
                if (wp is not null) return wp;
                // Fall through to the numbered art if the per-weapon pic isn't in the mounted data.
            }
        }

        if (number <= 0) return null;
        // VFS art first (gfx/crosshair<N>), then a project override under res://.
        return TextureCache.GetFirst($"gfx/crosshair{number}", $"res://art/hud/crosshairs/crosshair{number}.png");
    }

    /// <summary>The pic-fit scale that maps the crosshair texture onto its on-screen footprint (kept modest so
    /// crosshair_size is the dominant control; QC sizes the pic by its own image size × resolution).</summary>
    private static float BasePicScale() => 1f;

    private void DrawCrosshairTexture(Texture2D pic, Vector2 center, Color c, float scale, bool blur)
    {
        Vector2 s = pic.GetSize() * scale;
        if (s.X <= 0f || s.Y <= 0f) return;

        // QC CROSSHAIR_DO_BLUR: on a blurred (obstruction/teammate) crosshair, draw a 5×5 spread of low-alpha
        // copies so it reads as a soft blur (cheap stand-in for the real per-pixel blur).
        if (blur)
        {
            float spread = Mathf.Max(1f, scale);
            var bc = new Color(c.R, c.G, c.B, c.A * 0.04f);
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                {
                    var off = new Vector2(i * spread, j * spread);
                    DrawTextureRect(pic, new Rect2(center - s * 0.5f + off, s), false, bc);
                }
            return;
        }

        var at = new Rect2(center - s * 0.5f, s);
        DrawTextureRect(pic, at, false, c);
    }

    /// <summary>Draw the center dot (QC autocvar_crosshair_dot, gfx/crosshairdot at crosshair_dot_size).</summary>
    private void DrawDot(Vector2 center, Color crossColor, float drawScale, float fadeIn)
    {
        float dotSize = GlobalF("crosshair_dot_size", 0.6f);
        if (!float.IsFinite(dotSize) || dotSize < 0f) dotSize = 0.6f; // junk cvar → finite default (no NaN-sized dot)
        float dotAlpha = GlobalF("crosshair_dot_alpha", 1f) * fadeIn;
        Color dotColor = crossColor;
        // QC: custom dot color overrides the crosshair color when crosshair_dot_color_custom and not "0".
        if (GlobalF("crosshair_dot_color_custom", 1f) != 0f
            && GlobalStr("crosshair_dot_color") is { } dc && dc != "0" && TryParseRgb(dc, out Color custom))
            dotColor = new Color(custom.R, custom.G, custom.B, crossColor.A);
        dotColor = new Color(dotColor.R, dotColor.G, dotColor.B, dotAlpha);

        Texture2D? dotPic = TextureCache.Get("gfx/crosshairdot");
        if (dotPic is not null)
        {
            Vector2 s = dotPic.GetSize() * drawScale * dotSize;
            if (s.X <= 0f || s.Y <= 0f) return;
            DrawTextureRect(dotPic, new Rect2(center - s * 0.5f, s), false, dotColor);
        }
        else if (DotRadius > 0f)
        {
            DrawCircle(center, DotRadius * drawScale * (dotSize <= 0f ? 1f : dotSize), dotColor);
        }
    }

    private void DrawVectorCrosshair(Vector2 center, Color c, float scale = 1f)
    {
        // Scale the vector crosshair around a sane baseline so crosshair_size (~0.4) doesn't shrink it to nothing.
        float s = Mathf.Max(0.25f, scale * 2.5f);
        float g = GapPixels * s;
        float t = TickLength * s;
        float thick = Mathf.Max(1f, Thickness * s);
        float half = thick * 0.5f;

        // Four ticks (left, right, up, down), leaving GapPixels of empty space at the center.
        DrawRect(new Rect2(center.X - g - t, center.Y - half, t, thick), c); // left
        DrawRect(new Rect2(center.X + g, center.Y - half, t, thick), c);     // right
        DrawRect(new Rect2(center.X - half, center.Y - g - t, thick, t), c); // up
        DrawRect(new Rect2(center.X - half, center.Y + g, thick, t), c);     // down

        if (DotRadius > 0f)
            DrawCircle(center, DotRadius * s, c);
    }

    /// <summary>
    /// Draw a partially-filled ring using the <c>gfx/crosshair_ring*</c> art (QC <c>DrawCircleClippedPic</c>):
    /// the ring pic clipped to a clockwise pie wedge of <paramref name="fraction"/> of the full circle, drawn
    /// additively. Falls back to the procedural <see cref="DrawRing"/> arc when the art is missing.
    /// </summary>
    private void DrawRingPic(Vector2 center, float radius, float fraction, string image, Color color)
    {
        // Reject non-finite radius/fraction first: a NaN slips past `<= 0f` (NaN compares false) and would build a
        // fan with NaN vertices that DrawColoredPolygon can choke on.
        if (!(radius > 0f) || !(fraction > 0f) || color.A <= 0f) return;
        fraction = Mathf.Min(fraction, 1f);

        Texture2D? tex = TextureCache.Get(image);
        if (tex is null)
        {
            // Procedural fallback so the ring is never invisible.
            DrawRing(center, radius, fraction, new Color(color.R, color.G, color.B, Mathf.Min(1f, color.A * 4f)));
            return;
        }

        // Build a textured pie-wedge fan (a center point + arc points). QC sweeps clockwise from 12 o'clock.
        int segments = Mathf.Max(2, (int)(48 * fraction));
        float arc = Mathf.Tau * fraction;
        float start = -Mathf.Pi / 2f;

        var pts = new Vector2[segments + 2];
        var uvs = new Vector2[segments + 2];
        pts[0] = center;
        uvs[0] = new Vector2(0.5f, 0.5f);
        for (int i = 0; i <= segments; i++)
        {
            float a = start + arc * (i / (float)segments);
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            pts[i + 1] = center + dir * radius;
            // map the unit circle (cos,sin) → [0,1] UV (0.5 + 0.5*dir), flipping Y for texture space.
            uvs[i + 1] = new Vector2(0.5f + 0.5f * dir.X, 0.5f + 0.5f * dir.Y);
        }

        // QC draws the ring additively (DRAWFLAG_ADDITIVE); without a per-draw blend material Godot's
        // immediate-mode draw is alpha-blended, which reads fine for the low-alpha ring stats.
        DrawColoredPolygon(pts, color, uvs, tex);
    }

    /// <summary>
    /// Draw an arc ring of <paramref name="fraction"/> of a full circle around <paramref name="center"/>
    /// (QC the charge ring). Approximated as a connected poly-line of short segments. Used as the fallback when
    /// the ring art is missing.
    /// </summary>
    private void DrawRing(Vector2 center, float radius, float fraction, Color color, int segments = 48)
    {
        // Reject non-finite radius/fraction (NaN slips past `<= 0f`, producing NaN line endpoints).
        if (!(radius > 0f) || !(fraction > 0f)) return;
        fraction = Mathf.Min(fraction, 1f);
        int steps = Mathf.Max(1, (int)(segments * fraction));
        float arc = Mathf.Tau * fraction;
        float start = -Mathf.Pi / 2f; // start at 12 o'clock, sweep clockwise
        Vector2 prev = center + new Vector2(Mathf.Cos(start), Mathf.Sin(start)) * radius;
        for (int i = 1; i <= steps; i++)
        {
            float a = start + arc * (i / (float)steps);
            Vector2 p = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            DrawLine(prev, p, color, 2f);
            prev = p;
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  small helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>True when every component of <paramref name="v"/> is finite (no NaN/Inf) — guards the trace ray.</summary>
    private static bool IsFinite(NVec3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Parse a space-separated "r g b" cvar into a <see cref="Vector3"/> (channels may exceed [0,1],
    /// as the hit-indication "10 -10 -10" cvar does), falling back to <paramref name="fallback"/>.</summary>
    private static Vector3 ParseRgbVec(string name, Vector3 fallback)
    {
        string s = GlobalStr(name);
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string[] p = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return fallback;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var st = System.Globalization.NumberStyles.Float;
        if (!float.TryParse(p[0], st, ci, out float r)) return fallback;
        if (!float.TryParse(p[1], st, ci, out float g)) return fallback;
        if (!float.TryParse(p[2], st, ci, out float b)) return fallback;
        return new Vector3(r, g, b);
    }
}

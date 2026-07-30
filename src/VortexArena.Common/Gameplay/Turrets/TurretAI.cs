using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Registry attribute for turrets — the C# stand-in for QC's <c>REGISTER_TURRET</c> (common/turrets/all.qh).
/// Derives from <see cref="GameRegistryAttribute"/> so the existing reflection bootstrap
/// (<c>GameRegistries.Bootstrap</c>) discovers it and routes the instance through
/// <c>case Turret tu: Registry&lt;Turret&gt;.Register(tu)</c>. The shipped Framework/Registry.cs only declares
/// Weapon/Item/Mutator/GameType/Monster attributes (no Turret one), and that file is off-limits here, so the
/// turret attribute is defined locally. Any attribute deriving from the base is honoured identically.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TurretAttribute : GameRegistryAttribute { }

/// <summary>
/// Per-instance runtime state a live turret entity carries that does not exist on the base <see cref="Entity"/>.
/// In QuakeC these are flat <c>.float</c>/<c>.entity</c> fields on the turret edict (sv_turrets.qc,
/// common/turrets/sv_turrets.qh); the entity-model (ADR-0007) keeps them off the shared edict and on a
/// component bag instead. Attached lazily via <see cref="TurretAI.State"/>.
/// </summary>
public sealed class TurretState
{
    /// <summary>QC <c>.attack_finished_single[0]</c> — absolute sim time the next shot is allowed.</summary>
    public float AttackFinished;

    /// <summary>QC <c>.ammo</c> — current power/ammo pool (regenerates toward <see cref="AmmoMax"/>).</summary>
    public float Ammo;

    /// <summary>QC <c>.ammo_max</c>.</summary>
    public float AmmoMax;

    /// <summary>QC <c>.ammo_recharge</c> — ammo regenerated per second.</summary>
    public float AmmoRecharge;

    /// <summary>
    /// QC <c>TFL_AMMO_ENERGY</c> bit of <c>.ammo_flags</c> — whether this turret's ammo pool is "energy".
    /// The fusion reactor only recharges energy-ammo recipients (<c>turret_fusionreactor_firecheck</c>:
    /// <c>targ.ammo_flags &amp; TFL_AMMO_ENERGY</c>), so rocket/bullet turrets are NOT topped up by it.
    /// </summary>
    public bool AmmoIsEnergy = true;

    /// <summary>QC <c>.volly_counter</c> — shots remaining in the current burst (volley) before the long refire.</summary>
    public int VollyCounter;

    /// <summary>QC <c>.target_select_time</c> — last time a full radius target scan ran (rate-limits scanning).</summary>
    public float TargetSelectTime;

    /// <summary>QC <c>.target_validate_time</c> — throttles re-validating the current enemy.</summary>
    public float TargetValidateTime;

    /// <summary>QC <c>.tur_aimpos</c> — the predicted world point the head is aiming at this frame.</summary>
    public Vector3 AimPos;

    /// <summary>QC <c>.tur_shotorg</c> — the muzzle origin shots leave from (turret origin + barrel offset).</summary>
    public Vector3 ShotOrg;

    /// <summary>
    /// QC <c>.tur_head.angles</c> — the head bone's LOCAL angles (relative to the turret body), slewed toward
    /// the firing solution by the track motor. The world aim is <c>turret.Angles + HeadAngles</c>.
    /// </summary>
    public Vector3 HeadAngles;

    /// <summary>QC <c>.tur_head.avelocity</c> — head angular velocity (fluid-inertia / fluid-precise motors).</summary>
    public Vector3 HeadAVelocity;

    /// <summary>QC <c>.tur_head.scale</c> — the head-bone render scale (fusionreactor.qc tr_setup sets <c>0.75</c>;
    /// most turrets leave it at the default 1). Presentation-only — carried so the head identity matches Base for
    /// when the turret client render lands. Defaults to 1 (unscaled).</summary>
    public float HeadScale = 1f;

    /// <summary>QC <c>.idle_aim</c> — the resting head pose (relative to the body) used when idle.</summary>
    public Vector3 IdleAim;

    /// <summary>QC <c>.lip</c> — sim time until which the head keeps aiming at the last firing solution after losing the target.</summary>
    public float AimIdleUntil;

    /// <summary>QC <c>.active</c> — whether the turret is awake and may acquire/track/fire (team-gated).</summary>
    public bool Active = true;

    /// <summary>QC <c>.respawntime</c> — seconds before a destroyed (respawning) turret reactivates.</summary>
    public float RespawnTime = 60f;

    /// <summary>QC TSL_NO_RESPAWN / TFL_DMG_DEATH_NORESPAWN — death removes the turret instead of respawning it.</summary>
    public bool NoRespawn;

    /// <summary>
    /// QC <c>.shot_volly</c> — the full-magazine count this turret was initialized with. <see cref="TurretAI.Respawn"/>
    /// mirrors Base <c>turret_respawn</c> (sv_turrets.qc:282: <c>this.volly_counter = this.shot_volly</c>) by
    /// resetting <see cref="VollyCounter"/> to this value on every respawn. Set by <see cref="TurretSpawn.Init"/>,
    /// which clamps it to a minimum of 1 (single-shot turrets that pass <c>shotVolly 0</c> get 1, never 0), so a
    /// respawn always restores a usable magazine.
    /// </summary>
    public int ShotVollyMax;

    /// <summary>QC TUR_FLAG_MOVE — a mobile turret that damage knockback can shove (.velocity += vforce).</summary>
    public bool Movable;

    /// <summary>QC TFL_DMG_HEADSHAKE (.damage_flags) — a hit jitters the head off-aim by ±damage on pitch+yaw.</summary>
    public bool HeadShake;

    /// <summary>Per-turret death FX + respawn hook (set by the turret so the shared death handler can re-run its setup).</summary>
    public Action<Entity>? OnDeathFx;
    public Action<Entity>? OnRespawn;

    /// <summary>
    /// QC <c>turret_think</c> — the per-frame brain think the spawnfunc wires (re-arms NextThink each tick and
    /// runs the descriptor's combat/locomotion). Recorded here so <see cref="TurretAI.Respawn"/> can re-install
    /// it (QC <c>turret_respawn</c> does <c>setthink(this, turret_think)</c>): once <see cref="TurretAI.Die"/>
    /// swaps <c>Think</c> over to the hide/respawn chain, the only way the resurrected turret resumes thinking is
    /// for respawn to put this driver back. Null on a turret spawned without the spawnfunc (headless tests).
    /// </summary>
    public EntityThink? PerFrameThink;

    /// <summary>QC <c>.tur_dist_enemy</c> — muzzle→enemy distance, refreshed each think (turret_do_updates).</summary>
    public float DistEnemy;

    /// <summary>QC <c>.tur_dist_aimpos</c> — muzzle→aimpos distance, refreshed each think.</summary>
    public float DistAimPos;

    /// <summary>QC <c>.tur_shotdir_updated</c> — the actual muzzle forward (head world-forward) this think.</summary>
    public Vector3 ShotDir;

    /// <summary>QC <c>.tur_impactent</c> — the entity the muzzle's forward tracebox would actually hit this think
    /// (used by the firecheck AFF / target-of-opportunity branches).</summary>
    public Entity? ImpactEnt;

    /// <summary>QC <c>.pathcurrent</c> (ewheel.qc / walker.qc) — the <c>turret_checkpoint</c> a mobile turret is
    /// currently driving toward when it has no enemy (the waypoint chain it roams via the checkpoints'
    /// <c>.enemy</c> links). Null when there is no path to follow (then the turret just brakes idle).</summary>
    public Entity? PathCurrent;

    /// <summary>QC <c>.tur_dist_impact_to_aimpos</c> — how far the predicted impact point sits from the aimpos
    /// (the AIMDIST firecheck: a shot is only taken when this is within aim_firetolerance_dist).</summary>
    public float DistImpactToAimPos;

    /// <summary>QC <c>.tur_impacttime</c> — the shell's predicted travel time to the forward-traced impact point
    /// (<c>vlen(tur_shotorg - trace_endpos) / shot_speed</c>, sv_turrets.qc:523). Refreshed by turret_do_updates;
    /// read by the flak fuse (turret_flac_projectile_think_explode). 0 when there are no engine services (the
    /// firecheck/impact trace is skipped headless).</summary>
    public float ImpactTime;

    /// <summary>
    /// QC <c>.tur_defend</c> (sv_turrets.qc:1238) — the world point this turret guards, resolved from its map
    /// <c>.target</c> key by <c>turret_findtarget</c>. When set, <c>turret_targetscore_generic</c> scores targets
    /// by how close they are to this point (defendmode) instead of by the optimal killzone, so the turret
    /// prioritises threats near what it protects. Null = no defend point (the common case). A per-turret-class
    /// <see cref="TurretParams.DefendPoint"/> takes precedence; this is the map-entity-fed one.
    /// </summary>
    public Vector3? DefendPoint;

    /// <summary>QC <c>.fireflag</c> (phaser.qc) — the phaser head charge/discharge animation state machine flag:
    /// 0 = idle, 1 = charging/firing (head frame cycles 1→10), 2 = discharging (frame advances to 15 then resets to
    /// idle). Drives the <c>tr_think</c> head-frame anim and the <c>turret_phaser_firecheck</c> fire block. Only the
    /// phaser uses it; other turrets advance their head frame directly without a fireflag.</summary>
    public int FireFlag;

    /// <summary>
    /// QC <c>.turret_addtarget</c> (sv_turrets.qh:84) — per-turret external target-reception hook. When non-null
    /// a <c>turret_targettrigger</c> touch (or another turret's designator) calls this to hand the turret a
    /// pre-identified target, bypassing the normal self-scan pipeline. The delegate mirrors the QC signature
    /// <c>bool(entity this, entity e_target, entity e_sender)</c>: it validates the
    /// candidate and, if valid, sets <c>turret.Enemy</c>. Only turrets that set
    /// <see cref="TurretAI.SelectTriggerTarget"/> in their select flags are eligible to receive targets
    /// (checked by the trigger system before dispatching). Null = no external-target hook (most turrets).
    /// </summary>
    public Func<Entity, Entity?, Entity?, bool>? AddTarget;
}

/// <summary>
/// Shared, Godot-free turret AI — the headless core of common/turrets/sv_turrets.qc. This is the
/// acquire → aim → fire pipeline every <see cref="Turret"/> reuses, factored out so the per-turret files only
/// describe identity + balance + their specific weapon. It operates purely on <see cref="Entity"/> and the
/// engine-services facade (<see cref="Api"/>), so it unit-tests without an engine.
///
/// Ported in full here: the radius scan + multi-flag <c>turret_validate_target</c> gate
/// (team/LOS/range/owner/dead/FOV/per-axis aim limits), the bias-weighted <c>turret_targetscore_generic</c>
/// scoring (range_optimal killzone + angular + missile + player biases, defendmode), lead-aim prediction
/// with shot-traveltime + z-gravity compensation (<c>turret_aim_generic</c> TFL_AIM_*), separate head-angle
/// steering with stepmotor / fluid-inertia / fluid-precise motors and per-axis clamps (<c>turret_track</c>),
/// the refire/volley gate (<c>turret_firecheck</c>/<c>turret_fire</c>), ammo reg/spend, and the
/// activation/damage/death/respawn lifecycle (<c>turret_use</c>/<c>turret_damage</c>/<c>turret_die</c>/
/// <c>turret_respawn</c>).
///
/// Deferred (client-render only): CSQC effects, head-bone model attachment, muzzle flashes.
/// </summary>
public static class TurretAI
{
    // ---- turret class spawnflags (QC TUR_FLAG_*, turret.qh) — declarative identity carried per turret class ----
    /// <summary>QC <c>TUR_FLAG_SUPPORT</c> — a support unit (e.g. fusion reactor). In Base this selects
    /// <c>turret_targetscore_support</c> over <c>_generic</c>; the reactor never uses targetscore (its HITALLVALID
    /// sweep gates directly), so the flag is carried as identity data, not as a scoring switch.</summary>
    public const int TurFlagSupport = 1 << 0;
    /// <summary>QC <c>TUR_FLAG_AMMOSOURCE</c> — declarative marker that this turret supplies ammo to others.
    /// Verified to be NEVER read anywhere in Base qcsrc (purely declarative); carried only to match identity.</summary>
    public const int TurFlagAmmoSource = 1 << 1;
    /// <summary>QC <c>TUR_FLAG_PLAYER</c> — this turret selects/attacks player entities (vs. missiles-only or support-only).
    /// Purely declarative in the port; the actual player-targeting gate is <see cref="SelectPlayers"/> in
    /// <see cref="ValidTarget(Entity, Entity?, int, in TurretParams)"/>. Carried to match the class identity in
    /// <c>ewheel.qh</c> + <c>turret.qh</c>.</summary>
    public const int TurFlagPlayer = 1 << 2;
    /// <summary>QC <c>TUR_FLAG_MOVE</c> — a mobile turret that physically moves through the map. Behavior is modeled
    /// as <see cref="TurretState.Movable"/> (damage can shove it) + the turret descriptor's own locomotion
    /// (EWheelTurret/WalkerTurret). Carried here to complete the identity set.</summary>
    public const int TurFlagMove = 1 << 3;
    /// <summary>QC <c>TUR_FLAG_ROAM</c> — an idle mobile turret roams a <c>turret_checkpoint</c> waypoint graph
    /// rather than stopping in place. Behavior is fully ported (EWheelTurret <c>ewheel_move_path</c> /
    /// <c>ewheel_findtarget</c>; the <c>turret_checkpoint</c> spawnfunc). Carried as identity data
    /// so the eWheel's spawnflag identity matches <c>ewheel.qh</c> (TUR_FLAG_PLAYER|TUR_FLAG_MOVE|TUR_FLAG_ROAM).</summary>
    public const int TurFlagRoam = 1 << 4;

    // ---- target-select flags (QC TFL_TARGETSELECT_*, turret.qh) used by ValidTarget gating ----
    public const int SelectLos        = 1 << 0;  ///< require line of sight
    public const int SelectPlayers    = 1 << 1;  ///< may target players
    public const int SelectMissiles   = 1 << 2;  ///< may target projectiles
    public const int SelectMissilesOnly = 1 << 3; ///< ONLY projectiles
    public const int SelectTeamCheck  = 1 << 4;  ///< don't attack same team
    public const int SelectOwnTeam    = 1 << 5;  ///< ONLY same team (support units)
    public const int SelectRangeLimits = 1 << 6; ///< honour min/max range
    public const int SelectNoTurrets  = 1 << 7;  ///< don't attack other turrets
    public const int SelectAngleLimits = 1 << 8; ///< honour per-axis aim_maxpitch/aim_maxrot at acquisition

    /// <summary>QC <c>TFL_TARGETSELECT_VEHICLES</c> (turret.qh BIT(14)) — may target manned vehicles.
    /// When this flag is set an UNMANNED vehicle (no owner/pilot) is rejected, mirroring
    /// <c>turret_validate_target</c> sv_turrets.qc:706-708:
    /// <c>IS_VEHICLE &amp;&amp; VEHICLES &amp;&amp; !owner → -7</c>.
    /// Vehicles with a pilot (owner != null) pass through the gate.
    /// Without this flag vehicles are neither explicitly accepted nor rejected by <see cref="ValidTarget"/>
    /// (they fall through the player/missile checks and are treated as generic solid entities).</summary>
    public const int SelectVehicles = 1 << 9;

    /// <summary>QC <c>TFL_TARGETSELECT_TRIGGERTARGET</c> (turret.qh BIT(5)) — declarative marker: this turret
    /// accepts externally-designated targets from <c>turret_targettrigger</c> touch events and similar relays.
    /// The flag is checked by the trigger system to decide whether to call the turret's
    /// <see cref="TurretState.AddTarget"/> hook; it is NOT consulted inside <see cref="ValidTarget"/> or
    /// <see cref="SelectTarget"/> (external assignment bypasses the normal scan pipeline, as in Base).</summary>
    public const int SelectTriggerTarget = 1 << 10;

    // ---- firecheck flags (QC TFL_FIRECHECK_*, turret.qh) used by the RunCombat fire gate ----
    public const int FireCheckRefire    = 1 << 0;  ///< honour attack_finished refire delay
    public const int FireCheckDead      = 1 << 1;  ///< don't keep firing at a dead enemy
    public const int FireCheckDistances = 1 << 2;  ///< too-close hold (tur_dist_aimpos < range_min)
    public const int FireCheckLos       = 1 << 3;  ///< require a clear muzzle line of sight (rarely used in fire gate)
    public const int FireCheckAimDist   = 1 << 4;  ///< only fire when predicted impact lands within aim_firetolerance_dist
    public const int FireCheckTeamCheck = 1 << 5;  ///< team gate (selection already handles team)
    public const int FireCheckAmmoOwn   = 1 << 6;  ///< require own ammo >= shot_dmg
    public const int FireCheckAff       = 1 << 7;  ///< avoid friendly fire: withhold a shot that would hit a teammate

    /// <summary>QC turret_initialize firecheck_flags default (sv_turrets.qc:1280): DEAD|DISTANCES|LOS|AIMDIST|
    /// TEAMCHECK|AMMO_OWN|REFIRE — notably NO AFF. Turrets that don't pass their own flags inherit this default,
    /// so the fire gate is unchanged for them; the hellion passes its own set (AFF on, AIMDIST off).</summary>
    public const int FireCheckDefault = FireCheckDead | FireCheckDistances | FireCheckLos | FireCheckAimDist
                                      | FireCheckTeamCheck | FireCheckAmmoOwn | FireCheckRefire;

    // ---- track motor types (QC TFL_TRACKTYPE_*, sv_turrets.qh) ----
    public const int TrackStepMotor     = 1; ///< hard angle increments, best accuracy
    public const int TrackFluidPrecise  = 2; ///< smooth absolute movement
    public const int TrackFluidInertia  = 3; ///< simulated inertia ("wobbly")

    /// <summary>QC <c>max_shot_distance</c> default (constants.qh) — the fallback hitscan/aim trace ceiling.</summary>
    public const float MaxShotDistance = 32768f;

    /// <summary>QC per-map <c>max_shot_distance</c> (world.qc:731): the live world-bounds trace ceiling. Mirrors
    /// <see cref="WeaponFiring.CurrentMaxShotDistance"/> so turret traces reach exactly as far as Base on both
    /// oversized and tiny maps.</summary>
    public static float CurrentMaxShotDistance => WeaponFiring.CurrentMaxShotDistance;

    /// <summary>QC autocvar_sv_gravity default — used by the z-predict gravity arc lead.</summary>
    public const float Gravity = 800f;

    private static readonly Dictionary<Entity, TurretState> _states = new();

    /// <summary>Fetch (creating on first use) the per-entity turret runtime state (QC flat turret fields).</summary>
    public static TurretState State(Entity e)
    {
        if (!_states.TryGetValue(e, out var s))
        {
            s = new TurretState();
            _states[e] = s;
        }
        return s;
    }

    /// <summary>Drop a turret's state (call on remove/free). Keeps the table from leaking across a match.</summary>
    public static void Forget(Entity e) => _states.Remove(e);

    /// <summary>
    /// Non-allocating turret probe for the per-tick snapshot producer (ServerNet): is <paramref name="e"/> a
    /// live turret, and if so hand back its existing <see cref="TurretState"/> WITHOUT creating one. Unlike
    /// <see cref="State"/>, this never inserts a default <see cref="TurretState"/>, so the snapshot loop can ask
    /// "is this entity a turret?" for every projectile/item/gib each tick without allocating a state bag for the
    /// (overwhelming) non-turret majority. Returns false (and <paramref name="state"/> = null) for non-turrets.
    /// </summary>
    public static bool TryGetState(Entity e, out TurretState state) => _states.TryGetValue(e, out state!);

    /// <summary>
    /// Read a turret framework cvar (QC <c>autocvar_g_turrets_*</c>), falling back to the turrets.cfg default
    /// when there are no engine services or the cvar isn't registered in the store. The turret cvars aren't
    /// seeded in the port's cvar table, so <c>GetFloat</c> alone would read 0 — which is wrong for the non-zero
    /// defaults (maxdelay 1, mindelay 0.1, aimidle 5); detect "unset" via the empty <c>GetString</c> and supply
    /// the Base default instead.
    /// </summary>
    internal static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        return Api.Cvars.GetString(name).Length == 0 ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// The world-space forward of the head: the turret body angles plus the slewed local head angles. This is
    /// what shots travel along and what the on-target/angle checks use (QC <c>this.angles + this.tur_head.angles</c>).
    /// </summary>
    public static Vector3 HeadWorldAngles(Entity turret)
    {
        TurretState st = State(turret);
        return turret.Angles + st.HeadAngles;
    }

    /// <summary>
    /// The muzzle origin a shot leaves from. QC offsets via the <c>tag_fire</c> head tag
    /// (<c>turret_tag_fire_update</c>); here we use the model tag if the model service exposes one, else fall
    /// back to the QC default barrel offset (<c>tur_shotorg='50 0 50'</c>) rotated by the head world angles.
    /// </summary>
    public static Vector3 ShotOrigin(Entity turret)
    {
        if (Api.Services is not null
            && Api.Models.TryGetTag(turret, "tag_fire", out Vector3 tagOrg, out _, out _, out _))
            return tagOrg;

        // turret_tag_fire_update: real tag_fire muzzle from the tur_head model is a client-render detail; here
        // approximate with the QC default '50 0 50' barrel offset rotated into the head's facing.
        QMath.AngleVectors(HeadWorldAngles(turret), out Vector3 fwd, out _, out Vector3 up);
        return turret.Origin + fwd * 50f + up * 50f;
    }

    /// <summary>
    /// Port of <c>turret_validate_target</c> (sv_turrets.qc): is <paramref name="target"/> a legal thing for
    /// <paramref name="turret"/> to shoot, under <paramref name="selectFlags"/>? Returns true only if every
    /// applicable gate passes (alive, damageable, not self/owner, team rule, range, FOV/aim-angle limits, LOS,
    /// missile rule). Mirrors the QC reject-cascade (which returns a negative reason code on reject, >0 accept).
    /// </summary>
    public static bool ValidTarget(Entity turret, Entity? target, int selectFlags, in TurretParams p)
        => ValidTarget(turret, target, selectFlags, p.RangeMin, p.RangeMax, p.AimMaxPitch, p.AimMaxRot,
            p.FireToleranceDist > 0f ? p.FireToleranceDist : 64f);

    /// <summary>Overload without per-axis angle limits (chain hops, support scans) — angle gating only when SelectAngleLimits is set with sane defaults.</summary>
    public static bool ValidTarget(Entity turret, Entity? target, int selectFlags, float rangeMin, float rangeMax)
        => ValidTarget(turret, target, selectFlags, rangeMin, rangeMax, 90f, 360f);

    public static bool ValidTarget(Entity turret, Entity? target, int selectFlags, float rangeMin, float rangeMax,
        float aimMaxPitch, float aimMaxRot, float fireToleranceDist = 64f)
    {
        if (target is null || target.IsFreed) return false;
        if (ReferenceEquals(target, turret)) return false;
        if (ReferenceEquals(target.Owner, turret)) return false;     // don't shoot own projectiles/parts

        // PVS gate (QC validate_target:688 `if (!checkpvs(e_target.origin, e_turret)) return -1;`) — a target in
        // a BSP cluster not potentially visible from the turret is rejected outright, BEFORE alpha/team/range and
        // independent of the LOS select flag. Engine-only (deterministic tests have no compiled PVS); CheckPvs
        // returns true on an unvised map, so the gate is a no-op there exactly as in DarkPlaces.
        if (Api.Services is not null && !Api.Trace.CheckPvs(turret.Origin, target.Origin)) return false;

        // alpha-cloak: a target faded to <=0.3 alpha is invisible to the turret (QC alpha cull).
        if (target.Alpha != 0f && target.Alpha <= 0.3f) return false;

        // QC turret_validate_target mutator hook (sv_turrets.qc): right after the alpha-cloak cull and before the
        // NO/NOTARGET/dead checks, MUTATOR_CALLHOOK(TurretValidateTarget, e_turret, e_target, validate_flags) can
        // FULLY REPLACE the validity result — `if (hook) return M_ARGV(3, float)`. A non-null override short-circuits
        // the rest of the cascade (true = force valid, false = force reject). No stock mutator registers it.
        // (target is provably non-null here — the null/self/owner guards above already returned.)
        bool? mutatorValid = MutatorHooks.FireTurretValidateTarget(turret, target!, selectFlags);
        if (mutatorValid is bool forced) return forced;

        // Untargetable / dead / not damageable.
        if ((target.Flags & EntFlags.NoTarget) != 0) return false;
        if (target.TakeDamage == DamageMode.No) return false;
        if (target.Health <= 0f) return false;

        bool isClient = IsPlayer(target);
        bool isMissile = IsMissile(target);

        // Vehicle gate (QC turret_validate_target, sv_turrets.qc:706-708): if the target IS a vehicle and the
        // SelectVehicles flag is set, reject UNMANNED vehicles (no owner/pilot). A manned vehicle (owner != null)
        // passes through and is treated as a valid non-player, non-missile target. Without SelectVehicles the
        // flag is absent and vehicles fall through all checks the same way any unrecognised solid entity would.
        bool isVehicle = (target.VehicleFlags & VehicleFlags.IsVehicle) != 0;
        if (isVehicle && (selectFlags & SelectVehicles) != 0 && target.Owner is null) return false;

        // Players require the players flag; projectiles require the missiles flag.
        if (isClient && (selectFlags & SelectPlayers) == 0) return false;
        if ((selectFlags & SelectMissilesOnly) != 0 && !isMissile) return false;
        if (isMissile && (selectFlags & SelectMissiles) == 0 && (selectFlags & SelectMissilesOnly) == 0)
            return false;

        // Don't attack other turrets (support units etc.).
        if ((selectFlags & SelectNoTurrets) != 0 && target.ClassName.StartsWith("turret", StringComparison.Ordinal)
            && DiffTeam(turret, target))
            return false;

        // Team rules. Cover the target itself AND its owner/aiment (portals, projectile owners) like QC.
        if ((selectFlags & SelectTeamCheck) != 0)
        {
            if ((selectFlags & SelectOwnTeam) != 0)
            {
                if (DiffTeam(turret, target)) return false;          // support: same-team only
                if (target.Owner is not null && DiffTeam(turret, target.Owner)) return false;
                if (target.Aiment is not null && DiffTeam(turret, target.Aiment)) return false;
            }
            else
            {
                if (SameTeam(turret, target)) return false;          // combat: never same team
                if (target.Owner is not null && SameTeam(turret, target.Owner)) return false;
                if (target.Aiment is not null && SameTeam(turret, target.Aiment)) return false;
            }
        }

        // Range.
        Vector3 toTarget = TargetCenter(target) - turret.Origin;
        float dist = toTarget.Length();
        if ((selectFlags & SelectRangeLimits) != 0)
        {
            if (dist < rangeMin) return false;
            if (dist > rangeMax) return false;
        }

        // Per-axis aim-angle limits: can the head physically point at it? (QC tvt_tadv vs aim_maxpitch/rot.)
        if ((selectFlags & SelectAngleLimits) != 0)
        {
            Vector3 tadv = TurretMath.ShortAngleVxy(
                TurretMath.AngleOfs(turret.Origin, turret.Angles, TargetCenter(target)), turret.Angles);
            if (System.Math.Abs(tadv.X) > aimMaxPitch) return false;
            if (System.Math.Abs(tadv.Y) > aimMaxRot) return false;
        }

        // grapplinghook is never a turret target (QC explicit reject).
        if (target.ClassName == "grapplinghook") return false;

        // Line of sight.
        if ((selectFlags & SelectLos) != 0 && Api.Services is not null)
        {
            Vector3 eye = turret.Origin + new Vector3(0f, 0f, 16f);
            Vector3 aimAt = TargetCenter(target);
            TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, aimAt, MoveFilter.Normal, turret);
            // Blocked if the trace stopped well short of the target (QC aim_firetolerance_dist test:
            // sv_turrets.qc:791 vdist(v_tmp - trace_endpos, >, aim_firetolerance_dist)).
            if ((aimAt - tr.EndPos).Length() > fireToleranceDist && !ReferenceEquals(tr.Ent, target))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Port of <c>turret_targetscore_generic</c> (sv_turrets.qc): the bias-weighted desirability of
    /// <paramref name="target"/>. Combines a distance score (normalized against the optimal killzone, or the
    /// defend-point distance in defendmode), an angular score (how little the head must swing), and the
    /// missile / player biases. A far-out-of-range target is heavily penalized (QC <c>score *= 0.001</c>).
    /// </summary>
    public static float ScoreTarget(Entity turret, Entity target, in TurretParams p)
    {
        TurretState st = State(turret);
        float tvtDist = (turret.Origin - TargetCenter(target)).Length();

        // Distance score. The per-class param defend point wins; otherwise use the map-entity-fed one resolved by
        // FindTarget from the turret's .target key (QC tur_defend, set by turret_findtarget).
        float dScore;
        Vector3? defendPoint = p.DefendPoint ?? st.DefendPoint;
        if (defendPoint is { } defend)
        {
            float dDist = (TargetCenter(target) - defend).Length();
            dScore = 1f - dDist / p.RangeMax;
        }
        else
        {
            float ikr = p.RangeOptimal > 0f ? p.RangeOptimal : p.RangeMax * 0.5f;
            dScore = System.Math.Min(ikr, tvtDist) / System.Math.Max(ikr, tvtDist);
        }

        // Angular score: head angle diff vs the max rotation (QC tvt_thadf / aim_maxrot).
        Vector3 thadv = TurretMath.AngleOfs(turret.Origin, HeadWorldAngles(turret), TargetCenter(target));
        float thadf = new Vector3(thadv.X, thadv.Y, 0f).Length();
        float aimMaxRot = p.AimMaxRot > 0f ? p.AimMaxRot : 90f;
        float aScore = 1f - thadf / aimMaxRot;

        float mScore = (p.MissileBias > 0f && IsMissile(target)) ? 1f : 0f;
        float pScore = (p.PlayerBias > 0f && IsPlayer(target)) ? 1f : 0f;

        dScore = System.Math.Max(dScore, 0f);
        aScore = System.Math.Max(aScore, 0f);

        float score = dScore * p.RangeBias
                    + aScore * p.AngleBias
                    + mScore * p.MissileBias
                    + pScore * p.PlayerBias;

        if ((st.ShotOrg - TargetCenter(target)).Length() > p.RangeMax)
            score *= 0.001f;

        return score;
    }

    /// <summary>
    /// Port of <c>turret_select_target</c> (sv_turrets.qc): scan everything in range and return the
    /// best-scoring legal target. The current <see cref="Entity.Enemy"/> gets a samebias bump to stay sticky.
    /// </summary>
    public static Entity? SelectTarget(Entity turret, int selectFlags, in TurretParams p)
    {
        if (Api.Services is null) return null;

        Entity? best = null;
        float bestScore = 0f;

        // Sticky: seed with the existing enemy scored * samebias (QC m_score init).
        if (turret.Enemy is not null && turret.Enemy.TakeDamage != DamageMode.No
            && ValidTarget(turret, turret.Enemy, selectFlags, in p))
        {
            best = turret.Enemy;
            bestScore = ScoreTarget(turret, turret.Enemy, in p) * p.SameBias;
        }

        foreach (Entity e in Api.Entities.FindInRadius(turret.Origin, p.RangeMax))
        {
            if (e.TakeDamage == DamageMode.No) continue;             // QC inlines the takedamage check
            if (!ValidTarget(turret, e, selectFlags, in p)) continue;

            float score = ScoreTarget(turret, e, in p);
            if (score > bestScore && score > 0f)
            {
                best = e;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Legacy nearest-valid overload (used by the omnidirectional turrets Tesla/FusionReactor that build their own params-free scan).</summary>
    public static Entity? SelectTarget(Entity turret, int selectFlags, float rangeMin, float rangeMax)
    {
        if (Api.Services is null) return null;
        Entity? best = null;
        float bestDist = float.MaxValue;
        if (turret.Enemy is not null && ValidTarget(turret, turret.Enemy, selectFlags, rangeMin, rangeMax))
        {
            best = turret.Enemy;
            bestDist = (TargetCenter(turret.Enemy) - turret.Origin).Length();
        }
        foreach (Entity e in Api.Entities.FindInRadius(turret.Origin, rangeMax))
        {
            if (e.TakeDamage == DamageMode.No) continue;
            if (!ValidTarget(turret, e, selectFlags, rangeMin, rangeMax)) continue;
            float d = (TargetCenter(e) - turret.Origin).Length();
            if (d < bestDist) { best = e; bestDist = d; }
        }
        return best;
    }

    /// <summary>
    /// Port of <c>turret_aim_generic</c> (sv_turrets.qc): where to aim, given the target's motion. Implements
    /// the full lead pipeline: TFL_AIM_LEAD leads by the refire delay; TFL_AIM_SHOTTIMECOMPENSATE additionally
    /// leads by the shot's traveltime <c>vlen(target-muzzle)/shot_speed</c>; TFL_AIM_ZPREDICT integrates the
    /// gravity arc of an airborne walking target over that traveltime; TFL_AIM_SPLASH traces to the ground at
    /// the target's feet.
    /// </summary>
    public static Vector3 AimPoint(Entity turret, Entity target, in TurretParams p)
    {
        Vector3 prePos = TargetCenter(target);
        if (p.AimSimple) return prePos;

        TurretState st = State(turret);
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        if (p.Lead)
        {
            float minTime = System.Math.Max(st.AttackFinished - now, 0f) + frameTime;

            if (p.ShotTimeCompensate && p.ShotSpeed > 0f)
            {
                Vector3 prep = prePos;
                float impactTime = (prep - st.ShotOrg).Length() / p.ShotSpeed;
                prep += target.Velocity * (impactTime + minTime);

                // Gravity arc: integrate the target's z fall over the shot traveltime when it's airborne and
                // ground-affected (QC TFL_AIM_ZPREDICT for WALK/STEP/TOSS/BOUNCE movetypes).
                if (p.ZPredict && !target.OnGround && IsGroundAffected(target) && frameTime > 0f)
                {
                    prep.Z = prePos.Z;
                    float vz = target.Velocity.Z;
                    for (float t = 0f; t < impactTime; t += frameTime)
                    {
                        vz -= Gravity * frameTime;
                        prep.Z += vz * frameTime;
                    }
                }
                prePos = prep;
            }
            else
            {
                prePos += target.Velocity * minTime;
            }
        }

        if (p.AimSplash && Api.Services is not null)
        {
            // Aim for the ground around the target's feet (QC TFL_AIM_SPLASH worldonly downtrace).
            TraceResult tr = Api.Trace.Trace(prePos + new Vector3(0f, 0f, 32f), Vector3.Zero, Vector3.Zero,
                prePos - new Vector3(0f, 0f, 64f), MoveFilter.WorldOnly, target);
            if (tr.Fraction != 1f) prePos = tr.EndPos;
        }

        return prePos;
    }

    /// <summary>
    /// Port of <c>turret_track</c> (sv_turrets.qc): slew the head's LOCAL angles toward the firing solution,
    /// honouring the track motor model (stepmotor / fluid-precise / fluid-inertia) and clamping per axis to
    /// <c>aim_maxpitch</c> / <c>aim_maxrot</c>. The head angles are stored on the <see cref="TurretState"/>
    /// (there is no separate bone entity); the body <see cref="Entity.Angles"/> is left for the mobile turrets
    /// to steer.
    /// </summary>
    public static void Track(Entity turret, in TurretParams p)
    {
        TurretState st = State(turret);
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // Where do we want to aim (world angles)?
        Vector3 targetAngle;
        if (!st.Active)
            targetAngle = st.IdleAim - new Vector3(p.AimMaxPitch, 0f, 0f) + turret.Angles;
        else if (turret.Enemy is null)
            targetAngle = now > st.AimIdleUntil
                ? st.IdleAim + turret.Angles
                : QMath.VecToAngles(QMath.Normalize(st.AimPos - st.ShotOrg));
        else
            targetAngle = QMath.VecToAngles(QMath.Normalize(st.AimPos - st.ShotOrg));

        st.HeadAngles = new Vector3(TurretMath.AngleMods(st.HeadAngles.X), TurretMath.AngleMods(st.HeadAngles.Y), st.HeadAngles.Z);

        // The desired head-local move (QC: target relative to body, minus current head, shortened).
        Vector3 moveAngle = TurretMath.ShortAngleVxy(targetAngle - turret.Angles - st.HeadAngles, st.HeadAngles);

        float aimMaxPitch = p.AimMaxPitch > 0f ? p.AimMaxPitch : 20f;
        float aimMaxRot = p.AimMaxRot > 0f ? p.AimMaxRot : 90f;
        // QC turret_initialize aim_speed default-fill (sv_turrets.qc): a STEPMOTOR turret that leaves aim_speed unset
        // gets turret_initparams' 36 fallback, but a NON-stepmotor (fluid) turret defaults to 180 instead
        // ("if (track_type != TRACKTYPE_STEPMOTOR) { if (!aim_speed) aim_speed = 180; ... }"). Match per track type.
        // Every shipped turret passes a non-zero AimSpeed, so this only changes the never-hit unset fallback.
        float aimSpeed = p.AimSpeed > 0f ? p.AimSpeed : (p.TrackType == TrackStepMotor ? 36f : 180f);

        switch (p.TrackType)
        {
            case TrackStepMotor:
            {
                float fStep = aimSpeed * frameTime;
                st.HeadAngles.X += QMath.Bound(-fStep, moveAngle.X, fStep);
                st.HeadAngles.X = QMath.Bound(-aimMaxPitch, st.HeadAngles.X, aimMaxPitch);
                st.HeadAngles.Y += QMath.Bound(-fStep, moveAngle.Y, fStep);
                st.HeadAngles.Y = QMath.Bound(-aimMaxRot, st.HeadAngles.Y, aimMaxRot);
                return;
            }

            case TrackFluidInertia:
            {
                float fTmp = aimSpeed * frameTime;
                moveAngle.X = QMath.Bound(-aimSpeed, moveAngle.X * p.TrackAccelPitch * fTmp, aimSpeed);
                moveAngle.Y = QMath.Bound(-aimSpeed, moveAngle.Y * p.TrackAccelRot * fTmp, aimSpeed);
                moveAngle = st.HeadAVelocity * p.TrackBlendRate + moveAngle * (1f - p.TrackBlendRate);
                break;
            }

            case TrackFluidPrecise:
            default:
                moveAngle.X = QMath.Bound(-aimSpeed, moveAngle.X, aimSpeed);
                moveAngle.Y = QMath.Bound(-aimSpeed, moveAngle.Y, aimSpeed);
                break;
        }

        // Fluid motors integrate via head avelocity with the per-axis clamp braking it at the limit.
        st.HeadAVelocity.X = moveAngle.X;
        if (st.HeadAngles.X + st.HeadAVelocity.X * frameTime > aimMaxPitch) { st.HeadAVelocity.X = 0f; st.HeadAngles.X = aimMaxPitch; }
        if (st.HeadAngles.X + st.HeadAVelocity.X * frameTime < -aimMaxPitch) { st.HeadAVelocity.X = 0f; st.HeadAngles.X = -aimMaxPitch; }

        st.HeadAVelocity.Y = moveAngle.Y;
        if (st.HeadAngles.Y + st.HeadAVelocity.Y * frameTime > aimMaxRot) { st.HeadAVelocity.Y = 0f; st.HeadAngles.Y = aimMaxRot; }
        if (st.HeadAngles.Y + st.HeadAVelocity.Y * frameTime < -aimMaxRot) { st.HeadAVelocity.Y = 0f; st.HeadAngles.Y = -aimMaxRot; }

        st.HeadAngles += st.HeadAVelocity * frameTime;
        st.HeadAngles = new Vector3(QMath.Bound(-aimMaxPitch, st.HeadAngles.X, aimMaxPitch),
                                    QMath.Bound(-aimMaxRot, st.HeadAngles.Y, aimMaxRot), st.HeadAngles.Z);
    }

    /// <summary>
    /// The acquire → aim → fire driver shared by every combat turret's <c>Think</c>. Regenerates ammo, rescans
    /// for a target on a delay, leads + tracks it (separate head angle), and invokes <paramref name="fire"/>
    /// once per refire when the muzzle is roughly on target and ammo allows. <paramref name="fire"/> is the
    /// per-turret weapon (the hitscan/projectile body). Returns the current target (or null).
    /// </summary>
    public static Entity? RunCombat(Entity turret, in TurretParams p, Action<Entity, Entity> fire)
    {
        TurretState st = State(turret);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;

        // QC turret_think (sv_turrets.qc) step 1: MUTATOR_CALLHOOK(TurretThink, this) before ammo regen / acquire
        // / aim / fire. A mutator that returns true pauses the rest of the brain for this think (the turret skips
        // its acquire/aim/fire pass). No stock mutator registers it, so this is a no-op fast-path in stock play.
        if (MutatorHooks.FireTurretThink(turret))
            return turret.Enemy;

        // Framework low-HP damage feedback (cl_turrets.qc turret_draw: spark/smoke tiers). Runs every think
        // regardless of active state (QC turret_draw is a per-frame client draw); ewheel/walker self-skip inside.
        DrawFx(turret);

        // Ammo regen (QC turret_think: ammo += ammo_recharge * frametime, capped).
        if (st.Ammo < st.AmmoMax)
            st.Ammo = System.Math.Min(st.Ammo + st.AmmoRecharge * frameTime, st.AmmoMax);

        st.ShotOrg = ShotOrigin(turret);

        // Inactive turrets still slew the head to idle and bail (QC: !active -> turret_track; return).
        if (!st.Active)
        {
            Track(turret, in p);
            return null;
        }

        // (Re)acquire a target. QC: scan when the current one is invalid, on the validate throttle, but never
        // more often than the mindelay. Cvar-backed (QC autocvar_g_turrets_targetscan_*) with the turrets.cfg
        // framework defaults — the maxdelay default is 1 (the port previously hardcoded 0.6, rescanning too often).
        float minDelay = Cvar("g_turrets_targetscan_mindelay", 0.1f);
        float maxDelay = Cvar("g_turrets_targetscan_maxdelay", 1f);
        bool doScan = st.TargetSelectTime + maxDelay < now;

        if (st.TargetValidateTime < now && !ValidTarget(turret, turret.Enemy, p.SelectFlags, in p))
        {
            turret.Enemy = null;
            st.TargetValidateTime = now + 0.5f;
            doScan = true;
        }
        if (st.TargetSelectTime + minDelay > now) doScan = false;

        if (doScan)
        {
            turret.Enemy = SelectTarget(turret, p.SelectFlags, in p);
            st.TargetSelectTime = now;
        }

        Entity? enemy = turret.Enemy;
        if (enemy is null)
        {
            Track(turret, in p);  // keep slewing toward idle
            return null;
        }

        st.AimIdleUntil = now + Cvar("g_turrets_aimidle_delay", 5f);   // hold aim briefly after losing target

        // Aim + track (separate head bone).
        st.AimPos = AimPoint(turret, enemy, in p);
        Track(turret, in p);

        // Refresh the muzzle distances + the predicted impact entity (QC turret_do_updates). A tracebox runs from
        // the muzzle along the ACTUAL head forward to the aimpos distance; trace_ent is the thing the shot would
        // hit (tur_impactent), and tur_dist_impact_to_aimpos is how far that impact lands from the intended aim.
        st.ShotDir = QMath.Forward(HeadWorldAngles(turret));
        st.DistEnemy = (st.ShotOrg - TargetCenter(enemy)).Length();
        st.DistAimPos = (st.ShotOrg - st.AimPos).Length();
        UpdateImpact(turret, enemy, in p);

        // Fire gate (QC turret_firecheck): refire/ammo, then the impact-entity branches, then the aim-tolerance
        // and too-close gates. Default firecheck_flags = DEAD|DISTANCES|LOS|AIMDIST|TEAMCHECK|AMMO_OWN|REFIRE.
        // QC turret_firecheck top: MUTATOR_CALLHOOK(Turret_CheckFire, this) can force the whole result — a true
        // override fires this think (skipping every gate below), a false override holds. No stock mutator registers
        // it, so the fast path falls straight through to the normal gates in stock play.
        bool? checkFire = MutatorHooks.FireTurretCheckFire(turret);
        if (checkFire == true)
        {
            Fire(turret, enemy, in p, fire);
            return enemy;
        }
        if (checkFire == false) return enemy;
        if (st.AttackFinished > now) return enemy;                    // TFL_FIRECHECK_REFIRE
        // Per-unit firecheck override (QC .turret_firecheckfunc, e.g. phaser.qc turret_phaser_firecheck): block fire
        // entirely while a custom fireflag state machine is mid-cycle. The phaser sets FireFlag=1 (beam active) /
        // FireFlag=2 (discharge head animation, ~5 tr_think frames after the beam ends until the head returns to
        // idle) and Base's firecheck returns false for the whole window — including the discharge phase the bare
        // refire hold doesn't cover. No other turret sets FireFlag, so this is inert for them.
        if (st.FireFlag != 0) return enemy;                          // .turret_firecheckfunc (fireflag guard)
        if (st.Ammo < p.ShotDamage) return enemy;                     // TFL_FIRECHECK_AMMO_OWN

        // TFL_SHOOT_VOLLYALWAYS mid-burst with a LIVE enemy (turret_firecheck:889-892): when the burst is already
        // in progress (volly_counter != shot_volly) the Base firecheck early-returns true immediately, skipping the
        // DISTANCES, AFF, and AIMDIST gates. This completes the burst even if the target has stepped inside
        // range_min or the muzzle has swung off the fire-tolerance line mid-volley. The enemy-null mid-burst case
        // is already handled in MlrsTurret.Think before RunCombat is called.
        if (p.VollyAlways && p.ShotVolly > 1 && st.VollyCounter != p.ShotVolly)
        {
            Fire(turret, enemy, in p, fire);
            return enemy;
        }

        // Target of opportunity (QC turret_firecheck, unconditional): if the muzzle is actually lined up on some
        // OTHER valid target, switch to it and fire — a turret never wastes a shot that already hits a foe.
        if (st.ImpactEnt is not null && !ReferenceEquals(st.ImpactEnt, enemy)
            && st.ImpactEnt.TakeDamage != DamageMode.No
            && ValidTarget(turret, st.ImpactEnt, p.SelectFlags, in p))
        {
            turret.Enemy = st.ImpactEnt;
            Fire(turret, st.ImpactEnt, in p, fire);
            return st.ImpactEnt;
        }

        // Too close (TFL_FIRECHECK_DISTANCES): hold unless the impact ent is itself a target of opportunity.
        if (st.DistAimPos < p.RangeMin)
        {
            if (st.ImpactEnt is not null && st.ImpactEnt.TakeDamage != DamageMode.No
                && ValidTarget(turret, st.ImpactEnt, p.SelectFlags, in p))
            {
                Fire(turret, enemy, in p, fire);
                return enemy;
            }
            return enemy;
        }

        // Avoid friendly fire on the predicted impact (TFL_FIRECHECK_AFF, sv_turrets.qc:928): never fire a shot
        // that would land on a same-team entity. Only gated turrets (hellion/hk/plasma in Base) carry AFF; the
        // framework default does NOT, so unflagged turrets keep firing exactly as before.
        if ((p.FireCheckFlags & FireCheckAff) != 0 && st.ImpactEnt is not null && SameTeam(turret, st.ImpactEnt))
            return enemy;

        // Aim<->predicted-impact tolerance (TFL_FIRECHECK_AIMDIST, sv_turrets.qc:933): only shoot when the shot
        // will actually land near the aimpos. The hellion firecheck does NOT set AIMDIST (it fires once range/
        // cool/ammo allow and lets the homing missile find its own way), so this gate is flag-driven. Modelled on
        // the real impact-point distance, falling back to the geometric muzzle-line check with no engine services.
        if ((p.FireCheckFlags & FireCheckAimDist) != 0)
        {
            if (Api.Services is not null)
            {
                if (st.DistImpactToAimPos > p.FireToleranceDist) return enemy;
            }
            else if (!OnTarget(st.ShotOrg, HeadWorldAngles(turret), st.AimPos, p.FireToleranceDist))
            {
                return enemy;
            }
        }

        // Volley-status pre-ammo gate (QC turret_firecheck:937-941): at the START of a fresh burst
        // (volly_counter == shot_volly) refuse to open fire unless there is enough ammo for the WHOLE volley
        // (shot_dmg * shot_volly), so a burst is never started that the ammo pool can't finish.
        if (p.ShotVolly > 1 && st.VollyCounter == p.ShotVolly && st.Ammo < p.ShotDamage * p.ShotVolly)
            return enemy;

        Fire(turret, enemy, in p, fire);
        return enemy;
    }

    /// <summary>
    /// Port of <c>turret_do_updates</c> (sv_turrets.qc): tracebox the muzzle's actual forward out to the aimpos
    /// distance to learn what a shot would really hit (<see cref="TurretState.ImpactEnt"/>) and how far that
    /// impact point lands from the intended aimpos (<see cref="TurretState.DistImpactToAimPos"/>, minus half the
    /// target's bbox span like QC). No-op without engine services (the impact-entity firecheck branches are then
    /// skipped, falling back to the geometric muzzle-line tolerance check).
    /// </summary>
    private static void UpdateImpact(Entity turret, Entity enemy, in TurretParams p)
    {
        TurretState st = State(turret);
        if (Api.Services is null)
        {
            st.ImpactEnt = null;
            st.DistImpactToAimPos = 0f;
            // No trace headless: fall back to the straight muzzle->aimpos travel time (QC trace_endpos == aimpos
            // when the path is clear). Keeps the flak fuse deterministic for tests that drive time by hand.
            st.ImpactTime = p.ShotSpeed > 0f ? st.DistAimPos / p.ShotSpeed : 0f;
            return;
        }

        Vector3 hull = new Vector3(1f, 1f, 1f);
        Vector3 end = st.ShotOrg + st.ShotDir * st.DistAimPos;
        TraceResult tr = Api.Trace.Trace(st.ShotOrg, -hull, hull, end, MoveFilter.Normal, turret);

        // QC subtracts half the enemy's bbox span so a hit anywhere on the target body counts as on-aim.
        Vector3 span = enemy.Maxs - enemy.Mins;
        st.DistImpactToAimPos = (tr.EndPos - st.AimPos).Length() - 0.5f * span.Length();
        if (st.DistImpactToAimPos < 0f) st.DistImpactToAimPos = 0f;
        st.ImpactEnt = tr.Ent;

        // QC sv_turrets.qc:523 tur_impacttime = vlen(tur_shotorg - trace_endpos) / shot_speed. The flak shell's
        // fuse uses this so geometry between the muzzle and aimpos shortens the fuse to where it actually detonates.
        st.ImpactTime = p.ShotSpeed > 0f ? (st.ShotOrg - tr.EndPos).Length() / p.ShotSpeed : 0f;
    }

    /// <summary>
    /// Port of <c>turret_fire</c> (sv_turrets.qc): run the turret's weapon once, then advance the refire clock,
    /// spend ammo, and tick the volley counter (applying the long volley-refire and optional target-clear at
    /// the end of a burst). Exposed so the custom turrets (Tesla/Phaser/FusionReactor) can reuse the bookkeeping.
    /// </summary>
    public static void Fire(Entity turret, Entity enemy, in TurretParams p, Action<Entity, Entity> fire)
    {
        TurretState st = State(turret);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC turret_fire master fire-disable: autocvar_g_turrets_nofire short-circuits the whole fire (no
        // weapon, no refire/ammo/volley bookkeeping) so the turret tracks but never shoots.
        if (Cvar("g_turrets_nofire", 0f) != 0f) return;

        // QC turret_fire mutator gate: `if (MUTATOR_CALLHOOK(TurretFire, this)) return;` — the per-mutator twin of
        // the nofire master gate. A handler returning true suppresses this shot entirely (no weapon, no refire/ammo/
        // volley bookkeeping). No stock mutator registers it, so this is a no-op fast-path in stock play.
        if (MutatorHooks.FireTurretFire(turret)) return;

        fire(turret, enemy);

        st.AttackFinished = now + p.Refire;
        st.Ammo -= p.ShotDamage;

        if (p.ShotVolly > 1)
        {
            st.VollyCounter--;
            if (st.VollyCounter <= 0)
            {
                st.VollyCounter = p.ShotVolly;
                if (p.ClearTarget) turret.Enemy = null;
                st.AttackFinished = now + p.VollyRefire;
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Lifecycle: activation, damage gate, death + respawn (QC turret_use/damage/die/respawn).
    // ----------------------------------------------------------------------------------------------------

    private static bool _deathHooked;

    /// <summary>
    /// Subscribe (once) to the shared <see cref="Combat.Death"/> bus so a turret the generic
    /// <c>DamageSystem</c> kills (health &lt; 1) runs <see cref="Die"/> — the headless pipeline has no
    /// per-entity <c>event_damage</c> hook, so this is how death+respawn fires automatically (the same pattern
    /// Breakable.cs uses). The pre-damage gating lives in <see cref="Damage"/>.
    /// </summary>
    public static void EnsureDeathHook()
    {
        if (_deathHooked) return;
        _deathHooked = true;
        Combat.Death.Add(OnAnyDeath);
    }

    private static bool OnAnyDeath(ref DeathEvent ev)
    {
        Entity v = ev.Victim;
        if (v.ClassName.StartsWith("turret_", StringComparison.Ordinal) && v.DeadState != DeadFlag.Respawning)
            Die(v);
        return false;  // non-exclusive: other death subscribers still run
    }

    /// <summary>
    /// Port of the defend-point half of <c>turret_findtarget</c> (sv_turrets.qc:1218): resolve the turret's
    /// map <c>.target</c> key to the entity it should guard (<see cref="TurretState.DefendPoint"/>) and derive the
    /// resting head pose (<see cref="TurretState.IdleAim"/>) so an idle turret faces what it defends. A target that
    /// resolves to a <c>turret_checkpoint</c> is ignored (QC: "turrets don't defend checkpoints" — that is the
    /// ewheel/walker roam path, wired separately). A missing/empty target is a no-op (no defend point).
    /// The turret_manager auto-spawn + reloadcvars think (the other half of turret_findtarget) is not ported —
    /// the port's balance is C# const, not live-reloadable cvars.
    /// </summary>
    public static void FindTarget(Entity turret)
    {
        if (string.IsNullOrEmpty(turret.Target)) return;

        Entity? targ = MapMover.FindFirstByTargetName(turret.Target);
        if (targ is null) return;                                  // QC: warn + clear target; nothing to defend
        if (targ.ClassName == "turret_checkpoint") return;        // QC: turrets don't defend checkpoints

        TurretState st = State(turret);
        st.DefendPoint = targ.Origin;

        // QC idle_aim = tur_head.angles + angleofs(tur_head, targ). At spawn the head angles are zero (idle), so
        // the resting pose is the angular offset from the head toward the defend point.
        st.IdleAim = st.HeadAngles + TurretMath.AngleOfs(turret.Origin, turret.Angles, targ.Origin);
    }

    /// <summary>
    /// Port of <c>turret_use</c> (sv_turrets.qc): on trigger, the turret adopts the activator's team and goes
    /// active (or inactive + teamless if the activator is teamless). Wire to <see cref="Entity.Use"/>.
    /// </summary>
    public static void Use(Entity turret, Entity? activator)
    {
        turret.Team = activator?.Team ?? 0f;
        State(turret).Active = turret.Team != 0f;
    }

    /// <summary>
    /// Port of <c>turret_damage</c> (sv_turrets.qc): the pre-damage gate a turret applies on a hit. Returns the
    /// (possibly friendly-fire-scaled) damage the caller should actually inflict, or 0 to reject it entirely
    /// (dead/inactive/teammate with friendlyfire off). Also shoves a movable turret. Note: Base does NOT adopt
    /// the attacker as an enemy here (TFL_DMG_RETALIATE is set but never read), so no retaliation occurs. The
    /// server damage router calls this before applying damage; death itself flows through <see cref="OnAnyDeath"/>.
    /// </summary>
    public static float Damage(Entity turret, Entity? attacker, float damage, Vector3 force)
    {
        TurretState st = State(turret);
        if (turret.DeadState == DeadFlag.Dead) return 0f;
        if (!st.Active) return 0f;                                    // inactive turrets take no damage

        // Friendly fire: QC scales by g_friendlyfire (default 0 => no team damage).
        if (attacker is not null && SameTeam(turret, attacker))
        {
            float ff = Api.Services is not null ? Api.Cvars.GetFloat("g_friendlyfire") : 0f;
            if (ff <= 0f) return 0f;
            damage *= ff;
        }

        if (st.Movable) turret.Velocity += force;

        // NOTE: Base turret_damage (sv_turrets.qc:207-251) does NOT adopt the attacker as an enemy. Although the
        // machinegun's inherited default damage_flags set TFL_DMG_RETALIATE (sv_turrets.qc:1274), no Base code ever
        // READS that flag — there is no `this.enemy = attacker` in the damage path — so a Base turret never turns to
        // face whoever shot it. Target acquisition goes exclusively through the scan/score pipeline. Adopting the
        // attacker here was an active fidelity divergence, so it is intentionally omitted.

        return damage;
    }

    /// <summary>QC <c>RES_LIMIT_NONE</c> — the "no explicit cap; fall back to max_health" sentinel a heal source
    /// passes when it wants to top up to the target's own maximum (server/resources.qh).</summary>
    public const float ResLimitNone = -1f;

    /// <summary>
    /// Port of <c>turret_heal</c> (sv_turrets.qc:253) — the turret's installed <c>.event_heal</c> handler, the
    /// <see cref="Entity.GtEventHeal"/> sink wired in <see cref="TurretSpawn.Init"/>. A friendly heal source (the
    /// Arc heal-beam, a heal nade, the mage/bumblebee healgun) routes through <see cref="Combat.Heal"/> →
    /// <c>target.GtEventHeal</c>, so a damaged-but-alive turret can be repaired by its team. Tops health toward
    /// <paramref name="limit"/> (or <c>max_health</c> when limit is <see cref="ResLimitNone"/>), but never a dead
    /// turret (health &lt;= 0) and never past the cap. Returns true if any health was actually added — mirroring
    /// QC <c>bool turret_heal(targ, inflictor, amount, limit)</c> exactly (it sets TNSF_STATUS; the port has no
    /// turret networking, so the net-flag is a no-op here).
    /// </summary>
    public static bool Heal(Entity turret, Entity? inflictor, float amount, float limit)
    {
        _ = inflictor; // QC turret_heal ignores the inflictor (only used for credit elsewhere).
        float trueLimit = limit != ResLimitNone ? limit : turret.MaxHealth;
        float hp = turret.GetResource(ResourceType.Health);
        // QC: a dead turret (<=0) or one already at/over the limit takes no heal.
        if (hp <= 0f || hp >= trueLimit)
            return false;

        turret.GiveResourceWithLimit(ResourceType.Health, amount, trueLimit);
        return true;
    }

    /// <summary>
    /// The turret's installed <c>.event_damage</c> (QC <c>turret_damage</c>, sv_turrets.qc) — the
    /// <see cref="Entity.GtEventDamage"/> shim wired in <see cref="TurretSpawn.Init"/>. The headless
    /// <see cref="Damage.DamageSystem.EventDamage"/> routes every non-player edict with a <c>GtEventDamage</c>
    /// here (and returns), exactly as it does for monsters / Onslaught objectives — so a turret victim runs the
    /// turret pre-damage gate (<see cref="Damage"/>) and its OWN health subtract, instead of being
    /// treated as a player by <c>PlayerDamage</c>.
    ///
    /// This is the seam Wave-2 turrets depend on: it makes <see cref="Damage"/> (which was bit-faithful but had
    /// no live caller) actually run on the live damage path. It applies the gated damage to RES_HEALTH and, when
    /// the turret drops to 0, fires the shared <see cref="Combat.Death"/> bus — which <see cref="OnAnyDeath"/>
    /// already turns into <see cref="Die"/> (blast + respawn schedule). Signature mirrors <c>GtEventDamage</c>:
    /// <c>(self, inflictor, attacker, deathtype, damage, hitloc, force)</c>.
    /// </summary>
    public static void EventDamage(Entity turret, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        _ = inflictor; _ = hitLoc; // (parity slots: QC turret_damage uses attacker/force; hitloc is unused)

        // Pre-damage gate (QC turret_damage): rejects dead/inactive/friendly hits (returns 0), scales friendly
        // fire, and shoves a movable turret. (No retaliation: Base never adopts the attacker as an enemy here.)
        float take = Damage(turret, attacker, damage, force);
        if (take <= 0f)
            return;

        // QC turret_damage: TakeResource(this, RES_HEALTH, damage) then `if (health <= 0) turret_die`. A turret
        // is not a player, so there is no armor split here — the raw gated damage hits health.
        turret.TakeResource(ResourceType.Health, take);
        turret.Health = turret.GetResource(ResourceType.Health);

        // Headshake (QC turret_damage:228-234, TFL_DMG_HEADSHAKE): throw the head slightly off-aim on a hit,
        // ±take on both pitch and yaw of the head-local angles (CSQC is told via TNSF_ANG). The next track tick
        // slews it back, so it reads as a recoil flinch. Uses the post-friendlyfire-scaled `take` like Base.
        TurretState stHit = State(turret);
        if (stHit.HeadShake)
        {
            stHit.HeadAngles.X += (Prandom.Float() - 0.5f) * take;
            stHit.HeadAngles.Y += (Prandom.Float() - 0.5f) * take;
        }

        if (turret.Health <= 0f && turret.DeadState == DeadFlag.No)
        {
            // Fire the shared obituary/death bus (gametypes score, OnAnyDeath -> Die runs the blast + respawn).
            var death = new DeathEvent { Victim = turret, Attacker = attacker, Inflictor = inflictor, DeathType = deathType };
            Combat.Death.Call(ref death);
        }
    }

    /// <summary>
    /// Port of <c>turret_die</c> (sv_turrets.qc): unsolidify, stop taking damage, and either remove
    /// (no-respawn) or schedule a respawn after <see cref="TurretState.RespawnTime"/>. Driven from
    /// <see cref="OnAnyDeath"/>. NOTE: Base <c>turret_die</c> has its ammo-scaled <c>RadiusDamage</c>
    /// death blast COMMENTED OUT (sv_turrets.qc:182), so turrets deal no blast on death — we match that.
    /// </summary>
    public static void Die(Entity turret)
    {
        TurretState st = State(turret);
        turret.DeadState = DeadFlag.Dead;
        turret.Solid = Solid.Not;
        turret.TakeDamage = DamageMode.No;
        turret.Health = 0f;
        turret.SetResourceExplicit(ResourceType.Health, 0f);
        turret.Enemy = null;
        st.Active = false;          // a dead turret runs no combat/movement think until respawn

        // QC turret_die (sv_turrets.qc:176/243): event_heal = func_null — a dead turret cannot be healed back up.
        // (Heal() also guards health<=0, so this is belt-and-braces, but it matches Base's explicit clear and
        // makes a dead turret reject a heal beam even if some future caller bypasses the health gate.)
        turret.GtEventHeal = null;

        // No death blast: Base turret_die has the ammo-scaled RadiusDamage commented out (sv_turrets.qc:182),
        // so a dying turret deals no area damage. (Was a port-added blast; removed to match Base.)

        st.OnDeathFx?.Invoke(turret);

        // Death presentation (cl_turrets.qc:turret_die, run client-side on EVERY death via TNSF_STATUS health==0,
        // not just NORESPAWN): the rocket-explode FX + impact sound, then the per-type debris gib toss. The port
        // has no CSQC turret edict, so the server emits the FX directly at the death site for both the respawning
        // and the permanent-death paths, and spawns the gibs as physics entities that render via the entity feed
        // (the same server-side gib pattern the destroyed vehicles use, VehicleCommon.TossGib).
        if (Api.Services is not null)
        {
            Api.Sound.Play(turret, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav"); // SND_ROCKET_IMPACT
            EffectEmitter.Emit("ROCKET_EXPLODE", turret.Origin);                          // EFFECT_ROCKET_EXPLODE
            TossDeathGibs(turret);                                                        // cl_turrets.qc turret_die gibs
        }

        if (st.NoRespawn)
        {
            // QC turret_die NORESPAWN branch (sv_turrets.qc:185-189): the edict is deleted, so the FX above is the
            // only death feedback before removal.
            if (Api.Services is not null)
                Api.Entities.Remove(turret);
            Forget(turret);
            return;
        }

        // Schedule respawn via the QC two-step (turret_die:200-201 -> turret_hide:159-163): first a 0.2s think
        // that runs turret_hide (which sets EF_NODRAW and schedules turret_respawn at respawntime - 0.2), so the
        // total dead time is exactly respawntime. The 0.2s split exists so the death FX/status update networks
        // before the body is hidden; we mirror the timing rather than respawning directly after RespawnTime.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        turret.DeadState = DeadFlag.Respawning;
        turret.NextThink = now + 0.2f;
        turret.Think = Hide;
    }

    /// <summary>
    /// Port of <c>turret_hide</c> (sv_turrets.qc:159): the second step of the death sequence. Hides the body
    /// (QC <c>effects |= EF_NODRAW</c>) and schedules <see cref="Respawn"/> at <c>respawntime - 0.2</c> so the
    /// dead interval matches <see cref="TurretState.RespawnTime"/> exactly (the 0.2s was already spent by
    /// <see cref="Die"/>). Shaped as an <see cref="EntityThink"/>.
    /// </summary>
    public static void Hide(Entity turret)
    {
        TurretState st = State(turret);
        // QC turret_hide (sv_turrets.qc:161): effects |= EF_NODRAW — hide the dead body for the respawn interval.
        // (The single networked Entity.Effects stands in for the QC tur_head sub-entity, which the port folds into
        // the turret edict; turret_respawn clears it again below.)
        turret.Effects |= EffectFlags.NoDraw;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        turret.NextThink = now + System.Math.Max(st.RespawnTime - 0.2f, 0f);
        turret.Think = Respawn;
    }

    /// <summary>
    /// Port of <c>turret_respawn</c> (sv_turrets.qc): re-solidify, restore full health/ammo/volley, clear the
    /// enemy, reset the head, and resume thinking. <see cref="TurretState.OnRespawn"/> lets a turret re-run its
    /// per-unit setup (model frame, movetype, home pose) on respawn. Shaped as an <see cref="EntityThink"/>.
    /// </summary>
    public static void Respawn(Entity turret)
    {
        TurretState st = State(turret);
        turret.DeadState = DeadFlag.No;
        // QC turret_respawn (sv_turrets.qc:270): effects &= ~EF_NODRAW — un-hide the body the hide-frame set.
        turret.Effects &= ~EffectFlags.NoDraw;
        turret.Solid = Solid.BBox;
        turret.TakeDamage = DamageMode.Aim;
        turret.Health = turret.MaxHealth;
        turret.SetResourceExplicit(ResourceType.Health, turret.MaxHealth);
        // QC turret_respawn (sv_turrets.qc:276): event_heal = turret_heal — re-install the heal sink Die cleared,
        // so a respawned (or round-reset) turret is repairable again.
        turret.GtEventHeal = Heal;
        turret.Enemy = null;
        turret.AVelocity = Vector3.Zero;
        st.HeadAVelocity = Vector3.Zero;
        st.HeadAngles = st.IdleAim;
        // QC turret_respawn (sv_turrets.qc:282): this.volly_counter = this.shot_volly — always reset to the
        // full magazine on respawn, regardless of whether a burst was in-progress when the turret died, so no
        // mid-burst counter survives a respawn. ShotVollyMax is clamped to >=1 by TurretSpawn.Init; the guard
        // covers any headless caller that left it at the default 0.
        st.VollyCounter = st.ShotVollyMax > 0 ? st.ShotVollyMax : 1;
        st.Ammo = st.AmmoMax;
        st.AttackFinished = 0f;
        st.Active = turret.Team != 0f;

        // QC turret_respawn: setthink(this, turret_think); nextthink = time. Re-install the per-frame brain (the
        // spawnfunc recorded it) so the resurrected turret resumes thinking — Die had swapped Think over to the
        // hide/respawn chain, so without this it would never think again. Headless tests with no spawnfunc leave
        // PerFrameThink null (they drive Think by hand), so fall back to clearing the think there.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        turret.Think = st.PerFrameThink;
        turret.NextThink = st.PerFrameThink is not null ? now : 0f;

        st.OnRespawn?.Invoke(turret);
    }

    /// <summary>QC <c>cl_gibs_lifetime</c> default (client.qc) — base seconds a turret debris gib persists.</summary>
    private const float GibLifetime = 14f;
    /// <summary>QC <c>EF_FLAME</c> (dpextensions.qc) — the burning-debris flame an exploding gib trails.</summary>
    private const int EfFlame = 1024;

    /// <summary>
    /// Port of <c>turret_die</c>'s debris toss (cl_turrets.qc:319-348), the generic (non-ewheel/walker/tesla)
    /// branch every standard turret — including the MLRS — uses. A 50% coin flip tosses either the three small
    /// <c>base-gib2/3/4.md3</c> chunks (fade-out) or the single <c>base-gib1.md3</c> (exploding), then always a
    /// <c>head_model</c> head gib (exploding) carrying the head's last angles + spin. The mobile ewheel/walker and
    /// the tesla toss their whole model instead (handled in their own draws — skipped here by classname, as Base
    /// branches on m_id). The port has no CSQC turret edict, so these are spawned server-side as physics entities
    /// that render through the entity feed, exactly as the vehicle wreckage gibs are. No-op headless.
    /// </summary>
    private static void TossDeathGibs(Entity turret)
    {
        if (Api.Services is null) return;
        // QC turret_die branches on m_id: ewheel/walker/tesla toss their own model (their draws own that); every
        // other turret runs the generic base-gib + head_model branch. Mirror by classname so we don't double up.
        if (turret.ClassName is "turret_ewheel" or "turret_walker" or "turret_tesla") return;

        TurretState st = State(turret);
        string? headModel = Turrets.ByName(turret.NetName)?.HeadModel;

        if (Prandom.Float() > 0.5f)
        {
            // QC: three base-gib2/3/4 chunks, vel '0 0 50' + randomvec()*150, '0 0 0' cmod, non-exploding fade-out.
            for (int i = 2; i <= 4; i++)
                TossGib($"models/turrets/base-gib{i}.md3", turret.Origin + new Vector3(0f, 0f, 8f),
                    new Vector3(0f, 0f, 50f) + Prandom.Vec() * 150f, cmod: Vector3.Zero, explode: false);
        }
        else
        {
            // QC: a single base-gib1 chunk, vel '0 0 0', '0 0 0' cmod, exploding.
            TossGib("models/turrets/base-gib1.md3", turret.Origin + new Vector3(0f, 0f, 8f),
                Vector3.Zero, cmod: Vector3.Zero, explode: true);
        }

        // QC: the head gib (head_model) from origin+'0 0 32', vel '0 0 200' + randomvec()*200, '-1 -1 -1' cmod,
        // exploding, and it inherits the head's final angles + a randomized spin (avelocity.y *= 5) with halved gravity.
        Entity? headgib = TossGib(headModel, turret.Origin + new Vector3(0f, 0f, 32f),
            new Vector3(0f, 0f, 200f) + Prandom.Vec() * 200f, cmod: new Vector3(-1f, -1f, -1f), explode: true);
        if (headgib is not null)
        {
            headgib.Angles = turret.Angles + st.HeadAngles;
            Vector3 av = st.HeadAVelocity + Prandom.Vec() * 45f;
            av.Y *= 5f;
            headgib.AVelocity = av;
            headgib.Gravity = 0.5f;
        }
    }

    /// <summary>
    /// Port of <c>turret_gibtoss</c> (cl_turrets.qc:282-313): spawn a bouncing model debris gib at
    /// <paramref name="from"/> and toss it with <paramref name="to"/> as its launch velocity. A traceline from the
    /// turret origin to the spawn point rejects gibs that would start inside solid (QC <c>trace_startsolid</c>).
    /// Exploding gibs trail <c>EF_FLAME</c> and detonate via <see cref="GibBoom"/> after a short random window;
    /// the rest fade out over <see cref="GibLifetime"/>. Pure presentation — a gib never deals damage. Returns the
    /// gib (or null if it could not be placed / no model / headless).
    /// </summary>
    private static Entity? TossGib(string? model, Vector3 from, Vector3 to, Vector3 cmod, bool explode)
    {
        if (Api.Services is null || string.IsNullOrEmpty(model)) return null;

        // QC turret_gibtoss:284: traceline(_from, _to, MOVE_NOMONSTERS, NULL); if (trace_startsolid) return NULL.
        // The reject only consults trace_startsolid (is the SPAWN point inside solid?), so a zero-length trace at
        // `from` captures the exact same gate without reproducing Base's quirk of tracing toward the velocity vector.
        TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, from, MoveFilter.NoMonsters, null);
        if (tr.StartSolid) return null;

        Entity gib = Api.Entities.Spawn();
        gib.ClassName = "turret_gib";
        Api.Entities.SetModel(gib, model);
        Api.Entities.SetSize(gib, new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));  // QC setsize '-1 -1 -1' '1 1 1'
        Api.Entities.SetOrigin(gib, from);
        gib.Solid = Solid.Corpse;                 // QC SOLID_CORPSE
        gib.MoveType = MoveType.Bounce;           // QC MOVETYPE_BOUNCE
        gib.Gravity = 1f;                         // QC gib.gravity = 1
        gib.Velocity = to;                        // QC gib.velocity = _to
        gib.AVelocity = Prandom.Vec() * 32f;      // QC gib.avelocity = prandomvec() * 32
        gib.ColorModKey = cmod;                   // QC gib.colormod = _cmod ('-1 -1 -1' head gib, '0 0 0' base gibs)

        float now = Api.Clock.Time;
        if (explode)
        {
            // QC: nextthink = time + 0.2 * lifetime * (1 + prandom()*0.15); effects = EF_FLAME; then turret_gibboom.
            gib.Effects |= EfFlame;
            gib.NextThink = now + 0.2f * (GibLifetime * (1f + Prandom.Float() * 0.15f));
            gib.Think = GibBoom;
        }
        else
        {
            // QC: nextthink = time + lifetime * (1 + prandom()*0.15); turret_gib_draw fades alpha to nothing.
            gib.Alpha = 1f;
            gib.NextThink = now + GibLifetime * (1f + Prandom.Float() * 0.15f);
            gib.Think = static self =>
            {
                if (Api.Services is not null) Api.Entities.Remove(self);   // fade-out reached: clear the gib
            };
        }

        return gib;
    }

    /// <summary>
    /// Port of <c>turret_gibboom</c> (cl_turrets.qc:273-280): an exploding turret gib detonates — rocket-impact
    /// sound + EFFECT_ROCKET_EXPLODE at its resting point, then four more <c>head-gib1..4.md3</c> sub-chunks fly
    /// off (non-exploding fade-out), then the gib is removed. Pure presentation (no RadiusDamage).
    /// </summary>
    private static void GibBoom(Entity gib)
    {
        if (Api.Services is null) return;
        Api.Sound.Play(gib, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav");  // QC SND_ROCKET_IMPACT
        EffectEmitter.Emit("ROCKET_EXPLODE", gib.Origin);                          // QC EFFECT_ROCKET_EXPLODE

        // QC: for (j = 1; j < 5; ++j) turret_gibtoss("head-gibJ.md3", origin+'0 0 2', velocity + randomvec()*700,
        // '0 0 0', false).
        for (int j = 1; j < 5; j++)
            TossGib($"models/turrets/head-gib{j}.md3", gib.Origin + new Vector3(0f, 0f, 2f),
                gib.Velocity + Prandom.Vec() * 700f, cmod: Vector3.Zero, explode: false);

        Api.Entities.Remove(gib);
    }

    /// <summary>
    /// Port of the low-HP feedback in the framework <c>turret_draw</c> (cl_turrets.qc:39-53) — the all-turret
    /// damage smoke/spark tiers: a turret under 127 hp throws a <c>te_spark</c> (3%/frame), under 85 hp a large
    /// smoke puff (1%/frame), under 32 hp a small smoke puff (1.5%/frame), all from one shared random roll like
    /// Base. <c>turret_draw</c> is the default client draw EVERY turret uses EXCEPT the mobile ewheel/walker,
    /// which install their own <c>ewheel_draw</c>/<c>walker_draw</c> (with their own spark) — so this skips those
    /// two by classname to avoid double-emitting. The port has no CSQC turret edict, so the FX is emitted
    /// server-side (the temp-entities/point-particles network to every viewer identically). Called once per think
    /// from <see cref="RunCombat"/>; no-op headless (needs the effect service).
    /// </summary>
    internal static void DrawFx(Entity turret)
    {
        if (Api.Services is null) return;
        // ewheel/walker replace turret_draw with their own draw (which emits its own spark) — don't double up.
        if (turret.ClassName == "turret_ewheel" || turret.ClassName == "turret_walker") return;

        float hp = turret.GetResource(ResourceType.Health);
        if (hp >= 127f) return;                                   // QC: the whole block is gated on health < 127

        float dt = Prandom.Float();                               // QC: dt = random() (one roll shared by all tiers)
        if (dt < 0.03f)
            EffectEmitter.TeSpark(turret.Origin + new Vector3(0f, 0f, 40f),
                Prandom.Vec() * 256f + new Vector3(0f, 0f, 256f), 16);   // QC te_spark(origin+'0 0 40', .., 16)

        if (hp < 85f && dt < 0.01f)
            EffectEmitter.Emit("SMOKE_LARGE", turret.Origin + Prandom.Vec() * 80f);   // QC EFFECT_SMOKE_LARGE
        if (hp < 32f && dt < 0.015f)
            EffectEmitter.Emit("SMOKE_SMALL", turret.Origin + Prandom.Vec() * 80f);   // QC EFFECT_SMOKE_SMALL
    }

    /// <summary>Is the muzzle, pointing along <paramref name="angles"/>, aimed within <paramref name="tol"/> of <paramref name="aimPos"/>?</summary>
    private static bool OnTarget(Vector3 muzzle, Vector3 angles, Vector3 aimPos, float tol)
    {
        Vector3 toAim = aimPos - muzzle;
        float dist = toAim.Length();
        if (dist <= 0.001f) return true;
        Vector3 fwd = QMath.Forward(angles);
        Vector3 closest = muzzle + fwd * dist;       // where the barrel line is at the target's range
        return (aimPos - closest).Length() <= tol;
    }

    /// <summary>The target's bbox center (QC real_origin + 0.5*(mins+maxs)); origin if it has no size.</summary>
    public static Vector3 TargetCenter(Entity e) => e.Origin + (e.Mins + e.Maxs) * 0.5f;

    /// <summary>QC IS_CLIENT/IS_PLAYER: a real player actor (the <see cref="Player"/> subclass or a client edict).</summary>
    public static bool IsPlayer(Entity e) => e is Player || (e.Flags & EntFlags.Client) != 0;

    /// <summary>QC FL_PROJECTILE stand-in: an owned item-flagged projectile (turret/weapon missile).</summary>
    public static bool IsMissile(Entity e) => (e.Flags & EntFlags.Item) != 0 && e.Owner is not null;

    /// <summary>QC: gravity-affected movetypes whose z arc the lead must integrate (WALK/STEP/TOSS/BOUNCE).</summary>
    private static bool IsGroundAffected(Entity e)
        => e.MoveType is MoveType.Walk or MoveType.Step or MoveType.Toss or MoveType.Bounce;

    /// <summary>QC SAME_TEAM: both on a (nonzero) team and equal. Turrets default team is nonzero when team play is on.</summary>
    public static bool SameTeam(Entity a, Entity b) => a.Team != 0f && a.Team == b.Team;

    /// <summary>QC DIFF_TEAM: on different teams (treating either being teamless as "different").</summary>
    public static bool DiffTeam(Entity a, Entity b) => a.Team != b.Team;
}

/// <summary>
/// The balance + behaviour-flag bundle a turret hands to <see cref="TurretAI.RunCombat"/>. Mirrors the QC
/// per-unit cvars (turrets.cfg <c>g_turrets_unit_*</c>) the AI core reads each think — now carrying the full
/// scoring biases, aim-angle limits, lead flags, shot speed and head track motor. Per-turret extras (homing
/// turn rates, melee, etc.) stay on the individual turret class.
/// </summary>
public readonly struct TurretParams
{
    public readonly int SelectFlags;        // QC target_select_flags
    public readonly float RangeMin;         // QC target_range_min
    public readonly float RangeMax;         // QC target_range
    public readonly float RangeOptimal;     // QC target_range_optimal (killzone for the distance score)
    public readonly float ShotDamage;       // QC shot_dmg (also the per-shot ammo cost)
    public readonly float ShotSpeed;        // QC shot_speed (projectile speed; used by lead compensation)
    public readonly float Refire;           // QC shot_refire
    public readonly float AimSpeed;         // QC aim_speed (deg/sec head slew)
    public readonly float AimMaxPitch;      // QC aim_maxpitch
    public readonly float AimMaxRot;        // QC aim_maxrot
    public readonly float FireToleranceDist;// QC aim_firetolerance_dist (how close the muzzle must be on target)

    // aim flags (QC TFL_AIM_*)
    public readonly bool Lead;              // TFL_AIM_LEAD
    public readonly bool ShotTimeCompensate;// TFL_AIM_SHOTTIMECOMPENSATE
    public readonly bool ZPredict;          // TFL_AIM_ZPREDICT
    public readonly bool AimSplash;         // TFL_AIM_SPLASH
    public readonly bool AimSimple;         // TFL_AIM_SIMPLE (aim at current pos, no lead)

    // volley (QC shot_volly / shot_volly_refire / TFL_SHOOT_CLEARTARGET / TFL_SHOOT_VOLLYALWAYS)
    public readonly int ShotVolly;
    public readonly float VollyRefire;
    public readonly bool ClearTarget;

    /// <summary>
    /// QC <c>TFL_SHOOT_VOLLYALWAYS</c> — once a burst has started it MUST complete even when the enemy is still
    /// present but has slipped inside <see cref="RangeMin"/> or off the muzzle line. Base
    /// <c>turret_firecheck:889</c> early-returns true when <c>volly_counter != shot_volly &amp;&amp; ammo &gt;= shot_dmg</c>,
    /// skipping DISTANCES, AFF, and AIMDIST gates. <see cref="RunCombat"/> mirrors this when this flag is set.
    /// The enemy-null mid-burst case is handled separately by the per-turret <see cref="Turret.Think"/> override.
    /// </summary>
    public readonly bool VollyAlways;

    // target scoring biases (QC target_select_*bias)
    public readonly float RangeBias;
    public readonly float SameBias;
    public readonly float AngleBias;
    public readonly float MissileBias;
    public readonly float PlayerBias;

    // head track motor (QC track_type / track_accel_* / track_blendrate)
    public readonly int TrackType;
    public readonly float TrackAccelPitch;
    public readonly float TrackAccelRot;
    public readonly float TrackBlendRate;

    // defendmode (QC tur_defend.origin) — scores targets by closeness to a point to defend
    public readonly Vector3? DefendPoint;

    // QC firecheck_flags (turret.qh TFL_FIRECHECK_*) — which fire-gate checks the unit's turret_firecheck runs.
    // Defaults to TurretAI.FireCheckDefault (the framework default: AIMDIST on, AFF off), so a turret that does
    // not pass its own flags keeps the previous fire-gate behaviour unchanged.
    public readonly int FireCheckFlags;

    public TurretParams(int selectFlags, float rangeMin, float rangeMax, float shotDamage, float refire,
        float aimSpeed, float fireToleranceDist, bool lead,
        int shotVolly = 0, float vollyRefire = 0f,
        float rangeOptimal = 0f, float shotSpeed = 0f, float aimMaxPitch = 20f, float aimMaxRot = 90f,
        bool shotTimeCompensate = false, bool zPredict = false, bool aimSplash = false, bool aimSimple = false,
        bool clearTarget = false, bool vollyAlways = false,
        float rangeBias = 1f, float sameBias = 1f, float angleBias = 1f, float missileBias = 1f, float playerBias = 1f,
        int trackType = TurretAI.TrackFluidInertia, float trackAccelPitch = 0.5f, float trackAccelRot = 0.5f,
        float trackBlendRate = 0.35f, Vector3? defendPoint = null,
        int fireCheckFlags = TurretAI.FireCheckDefault)
    {
        SelectFlags = selectFlags;
        RangeMin = rangeMin;
        RangeMax = rangeMax;
        RangeOptimal = rangeOptimal;
        ShotDamage = shotDamage;
        ShotSpeed = shotSpeed;
        Refire = refire;
        AimSpeed = aimSpeed;
        AimMaxPitch = aimMaxPitch;
        AimMaxRot = aimMaxRot;
        FireToleranceDist = fireToleranceDist;
        Lead = lead;
        ShotTimeCompensate = shotTimeCompensate;
        ZPredict = zPredict;
        AimSplash = aimSplash;
        AimSimple = aimSimple;
        ShotVolly = shotVolly;
        VollyRefire = vollyRefire;
        ClearTarget = clearTarget;
        VollyAlways = vollyAlways;
        RangeBias = rangeBias;
        SameBias = sameBias;
        AngleBias = angleBias;
        MissileBias = missileBias;
        PlayerBias = playerBias;
        TrackType = trackType;
        TrackAccelPitch = trackAccelPitch;
        TrackAccelRot = trackAccelRot;
        TrackBlendRate = trackBlendRate;
        DefendPoint = defendPoint;
        FireCheckFlags = fireCheckFlags;
    }
}

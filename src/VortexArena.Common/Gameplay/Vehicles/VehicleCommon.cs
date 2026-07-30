// Port: qcsrc/common/vehicles/sv_vehicles.qc (+ sv_vehicles.qh, vehicle.qh, vehicles.qc)
//
// Shared, vehicle-agnostic infrastructure for the C# vehicle port: the [Vehicle] registry attribute,
// the per-entity vehicle fields (promoted from QC's flat .vehicle_* edict fields), the VHF_* flag enum,
// and the Godot-free core of the shared enter/exit/spawn/think/regen/damage routines that every concrete
// vehicle (Racer/Raptor/Spiderbot/Bumblebee) leans on.
//
// NAMESPACE NOTE: everything is in the flat `XonoticGodot.Common.Gameplay` namespace on purpose — a nested
// `.Vehicles` namespace would collide with the `Vehicles` catalog type in EntityClasses.cs. The folder is
// named Vehicles/ but the namespace is not.
//
// This is a NEW file; it only ADDS members to the existing `partial class Entity` (same technique as
// Gameplay/Items/EntityResources.cs) and never edits an existing file.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // ---- QC vehicle linkage (sv_vehicles.qh: .vehicle / .owner) ----

        /// <summary>QC <c>.vehicle</c> — on a player, the vehicle entity they currently pilot (NULL when on foot).</summary>
        public Entity? Vehicle;

        /// <summary>QC <c>.vehicledef</c> — the <see cref="Gameplay.Vehicle"/> descriptor driving this vehicle entity.</summary>
        public Gameplay.Vehicle? VehicleDef;

        /// <summary>QC <c>.tur_head</c> — the turret-head sub-entity (turret/aim model) attached to the vehicle body.</summary>
        public Entity? TurHead;

        // ---- QC vehicle stat fields (sv_vehicles.qh _STAT(VEHICLESTAT_*)) ----
        // On the vehicle entity these hold the REAL values; the per-frame code mirrors a 0..100 percentage
        // onto the owning player. We keep the real values here; the percentage mirroring is a HUD/network
        // concern (NOTE: client/net) handled where the vehicle stats are transmitted.

        /// <summary>QC <c>.vehicle_energy</c> — boost/weapon energy pool (real value on the vehicle).</summary>
        public float VehicleEnergy;

        /// <summary>QC <c>.vehicle_shield</c> — regenerating shield that absorbs damage before health.</summary>
        public float VehicleShield;

        /// <summary>QC <c>.vehicle_ammo1</c> — primary weapon ammo/heat pool (real value on the vehicle).</summary>
        public float VehicleAmmo1;

        /// <summary>QC <c>.vehicle_ammo2</c> — secondary weapon ammo pool.</summary>
        public float VehicleAmmo2;

        /// <summary>QC <c>.vehicle_reload2</c> — secondary-weapon reload PROGRESS (0..100) mirrored onto the owning
        /// player. The raptor drives this from the bomb/flare reload alpha; the in-vehicle HUD reads it for the
        /// reload bar, and the bomb dropmark crosshair only predicts while it reads 100 (bombs ready).</summary>
        public float VehicleReload2;

        // ---- QC vehicle bookkeeping fields ----

        /// <summary>QC <c>.vehicle_flags</c> — the VHF_* capability bitfield (<see cref="Gameplay.VehicleFlags"/>).</summary>
        public Gameplay.VehicleFlags VehicleFlags;

        /// <summary>QC <c>.old_vehicle_flags</c> — backup of the flags while an enemy is "stealing" the vehicle.</summary>
        public Gameplay.VehicleFlags OldVehicleFlags;

        /// <summary>QC <c>.dmg_time</c> — last time the vehicle took damage (gates shield/health regen pause).</summary>
        public float DmgTime;

        /// <summary>QC <c>.vehicle_enter_delay</c> — players cannot enter/leave a vehicle before this time.</summary>
        public float VehicleEnterDelay;

        /// <summary>QC <c>.respawntime</c> — seconds before a destroyed vehicle returns.</summary>
        public float RespawnTime;

        /// <summary>QC <c>.pos1</c> / <c>.pos2</c> — the vehicle's spawn origin and spawn angles (return point).</summary>
        public Vector3 SpawnPos, SpawnAngles;

        /// <summary>QC <c>.pain_frame</c> — debounce for the low-health smoke/jitter cadence (vehicles_painframe).</summary>
        public float PainFrame;

        /// <summary>QC <c>.play_time</c> — debounce for vehicle impact (fall/ram) self-damage (vehicles_impact, 0.25s).</summary>
        public float PlayTime;

        /// <summary>QC <c>.oldvelocity</c> — last-tick velocity; <c>vehicles_impact</c> measures the delta against this.</summary>
        public Vector3 OldVelocity;

        /// <summary>
        /// QC <c>.vehicle_health</c> — the 0..100 health PERCENTAGE mirrored onto the OWNING PLAYER each tick
        /// (vehicles_regen / vehicles_painframe read <c>owner.vehicle_health</c>). On the vehicle entity itself
        /// the real value lives in RES_HEALTH; this field is the owner-side HUD mirror.
        /// </summary>
        public float VehicleHealth;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// Client-render flags a vehicle entity packs into its networked <see cref="Framework.Entity.Effects"/>
    /// bitfield — the transient presentation state the CSQC vehicle visuals need but that isn't a transform
    /// (whether the primary gun is firing, whether boost is engaged). High bits, above the engine's EF_*
    /// range, so they ride the existing delta-compressed Effects field (costing nothing while unchanged) and
    /// the client (<c>VehicleVisuals</c>) reads them back to drive barrel spin / muzzle flash / boost sound.
    /// The server vehicle frame sets/clears them each tick from the pilot's input.
    /// </summary>
    public static class VehicleEffects
    {
        /// <summary>Primary gun firing this tick (spins the barrels + pops the muzzle flash).</summary>
        public const int Firing = 1 << 24;
        /// <summary>Boost/afterburner engaged (the Racer boost engine-sound overlay).</summary>
        public const int Boosting = 1 << 25;
    }

    /// <summary>
    /// Extension of the <see cref="Vehicle"/> descriptor base (declared <c>partial</c> in EntityClasses.cs).
    /// Adds the death hook QC exposes as <c>vr_death</c> (common/vehicles/vehicle.qh) without editing that
    /// file. The base already declares Spawn / Enter(vehicle,player) / Exit / Think.
    /// </summary>
    public abstract partial class Vehicle
    {
        /// <summary>(SERVER) QC <c>vr_death</c> — run when the vehicle is destroyed (death FX, corpse, respawn).</summary>
        public virtual void Death(Entity vehicle) { }

        /// <summary>
        /// (SERVER) QC <c>vr_impact</c> (common/vehicles/vehicle.qh) — run from <c>vehicles_touch</c> when a piloted
        /// vehicle rams/lands; the per-vehicle override calls <see cref="VehicleCommon.Impact"/> with its own
        /// minspeed/speedfac/maxpain thresholds. Default no-op (a vehicle with no impact tuning takes no ram damage).
        /// </summary>
        public virtual void Impact(Entity vehicle) { }

        /// <summary>
        /// (SERVER) Per-vehicle carried-CTF-flag cockpit offset. QC <c>vr_enter</c> parks a boarding flag-carrier's
        /// flag at a fixed offset from the craft so it rides the vehicle instead of the player's back. The shared
        /// default is <c>VEHICLE_FLAG_OFFSET = '0 0 96'</c> (sv_ctf.qh); a vehicle whose vr_enter uses a different
        /// origin (the Racer parks it 190u behind the cockpit, <c>'-190 0 96'</c>) overrides this. Read by
        /// <c>Ctf.Tick</c> each tick when the carrier is seated in a vehicle.
        /// </summary>
        public virtual Vector3 FlagCarryOffset => new(0f, 0f, 96f);
    }

    /// <summary>
    /// Registry attribute for vehicles — the C# successor to QC's <c>REGISTER_VEHICLE</c> (common/vehicles/all.qh).
    /// Derives from <see cref="GameRegistryAttribute"/> (which already carries the AttributeUsage), so the
    /// existing reflection bootstrap in <c>GameRegistries.Bootstrap</c> (Gameplay/Registries.cs) discovers and
    /// enrols each <see cref="Vehicle"/> into <see cref="Vehicles"/> with no edit to Framework/Registry.cs.
    /// Mirrors the existing <c>WeaponAttribute</c>/<c>MutatorAttribute</c> declarations.
    /// </summary>
    public sealed class VehicleAttribute : GameRegistryAttribute { }

    /// <summary>
    /// Vehicle capability flags — port of the VHF_* constants (common/vehicles/vehicle.qh). Stored in
    /// <see cref="Entity.VehicleFlags"/>. The movement-style bits (GROUND/HOVER/FLY) and the damage-shake
    /// cosmetic bits are carried for fidelity even where this headless phase doesn't act on them yet.
    /// </summary>
    [System.Flags]
    public enum VehicleFlags
    {
        None = 0,
        IsVehicle = 1 << 1,    // VHF_ISVEHICLE
        HasShield = 1 << 2,    // VHF_HASSHIELD
        ShieldRegen = 1 << 3,  // VHF_SHIELDREGEN
        HealthRegen = 1 << 4,  // VHF_HEALTHREGEN
        EnergyRegen = 1 << 5,  // VHF_ENERGYREGEN
        DeathEject = 1 << 6,   // VHF_DEATHEJECT
        MoveGround = 1 << 7,   // VHF_MOVE_GROUND
        MoveHover = 1 << 8,    // VHF_MOVE_HOVER
        MoveFly = 1 << 9,      // VHF_MOVE_FLY
        DmgShake = 1 << 10,    // VHF_DMGSHAKE
        DmgRoll = 1 << 11,     // VHF_DMGROLL
        DmgHeadRoll = 1 << 12, // VHF_DMGHEADROLL
        MultiSlot = 1 << 13,   // VHF_MULTISLOT
        PlayerSlot = 1 << 14,  // VHF_PLAYERSLOT
    }

    /// <summary>QC exit flags passed to <c>vehicles_exit</c> / <c>vr_*</c> (sv_vehicles.qh VHEF_*).</summary>
    public enum VehicleExitFlag
    {
        Normal = 0,   // VHEF_NORMAL  — user pressed the exit key
        Eject = 1,    // VHEF_EJECT   — fast triple-tap exit or the vehicle is dying
        Release = 2,  // VHEF_RELEASE — release ownership (disconnect / spectate / kill)
    }

    /// <summary>
    /// Shared, vehicle-agnostic helpers — the Godot-free core of common/vehicles/sv_vehicles.qc. Concrete
    /// vehicles call into these from their <see cref="Vehicle.Spawn"/>/<see cref="Vehicle.Enter"/>/
    /// <see cref="Vehicle.Exit"/>/<see cref="Vehicle.Think"/> overrides so the enter/exit handshake, the
    /// resource regen, the shield-then-health damage split and the shared projectile spawner live in one
    /// place (exactly as QC kept them shared in sv_vehicles.qc).
    /// </summary>
    public static class VehicleCommon
    {
        /// <summary>QC autocvar_g_vehicles_thinkrate (sv_vehicles.qh) — default vehicle think cadence.</summary>
        public const float DefaultThinkRate = 0.1f;

        private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;

        /// <summary>
        /// QC global <c>game_stopped</c> — set true by the match loop while the round is frozen (warmup over /
        /// match ended / intermission), parking every vehicle. The host (MatchController / RoundHandler) drives
        /// this; <see cref="FreezeIfGameStopped"/> reads it. Defaults false (the round is live).
        /// </summary>
        public static bool GameStopped { get; set; }

        // =====================================================================================
        // ENTER  — port of vehicles_enter() (sv_vehicles.qc ~931): attach the player to the vehicle.
        // =====================================================================================

        /// <summary>
        /// The shared half of <c>vehicles_enter()</c>: bind <paramref name="player"/> to <paramref name="vehic"/>,
        /// freeze the player's own physics (MOVETYPE_NONE / SOLID_NOT / no self-damage) and snapshot its team.
        /// The concrete vehicle's <see cref="Vehicle.Enter"/> override calls this first, then applies the
        /// per-vehicle setup (movetype, takeoff, HUD seeding).
        ///
        /// NOTE — cross-boundary (client/net/bot/mutator): CSQCVehicleSetup view-port/HUD networking, weapon-slot
        /// swap to the vehicle's temp weapon entities, jetpack/hook removal, steal-waypoint,
        /// MUTATOR_CALLHOOK(VehicleEnter), and the bot-driver guard.
        /// </summary>
        public static void EnterVehicle(Entity vehic, Entity player)
        {
            // QC: veh.owner = pl; pl.vehicle = veh;
            vehic.Owner = player;
            player.Vehicle = vehic;

            // The vehicle now stops self-thinking; the player's PlayerPhysplug drives it each frame.
            vehic.NextThink = 0f;

            // QC: pl.takedamage = DAMAGE_NO; pl.solid = SOLID_NOT; MOVETYPE_NOCLIP; alpha = -1; view_ofs = 0.
            // The spec asks for the player's movetype set to NONE while seated — the player is now a passenger
            // whose origin is rewritten from the vehicle each frame, so it must not integrate its own physics.
            player.TakeDamage = DamageMode.No;
            player.Solid = Solid.Not;
            player.MoveType = MoveType.None;
            player.Velocity = Vector3.Zero;
            player.ViewOfs = Vector3.Zero;
            player.Flags &= ~EntFlags.OnGround;
            player.Angles = vehic.Angles;

            // QC: veh.team = pl.team; veh.flags -= FL_NOTARGET; (vehicle becomes shootable as the player's team)
            vehic.Team = player.Team;
            vehic.Flags &= ~EntFlags.OnGround;

            // QC seeds the per-weapon ammo/energy mirror to 0 on entry.
            vehic.VehicleAmmo1 = 0f;
            vehic.VehicleAmmo2 = 0f;
            vehic.VehicleEnergy = 0f;

            // NOTE — cross-boundary (other systems/agents): the rest of vehicles_enter is CSQCVehicleSetup
            // viewport/HUD networking (net), the weaponentities[] temp_wepent swap (weapon view-entities), the
            // steal waypoint (CSQC), MUTATOR_CALLHOOK(VehicleEnter) (no VehicleEnter hook in MutatorHooks yet),
            // antilag_clear (antilag system), and the bot-driver gate (bot AI). The server-authoritative core
            // — ownership link, solidity/movetype/damage state, team, ammo-mirror reset — is done above.
        }

        // =====================================================================================
        // EXIT  — port of vehicles_exit() (sv_vehicles.qc ~775): detach + eject the player.
        // =====================================================================================

        /// <summary>
        /// The shared half of <c>vehicles_exit()</c>: restore the ejected <paramref name="player"/> to a normal
        /// walking entity (MOVETYPE_WALK / SOLID_SLIDEBOX / takes damage again), drop the vehicle↔player link,
        /// and re-flag the (living) vehicle as untargetable until it is re-entered. The concrete vehicle's
        /// <see cref="Vehicle.Exit"/> override computes the ejection origin/velocity and then calls this.
        ///
        /// NOTE — cross-boundary (client/net/mutator): SVC_SETVIEWPORT/SETVIEWANGLES + CSQCVehicleSetup
        /// networking, weapon slot restore, MUTATOR_CALLHOOK(VehicleExit), notifications, return-helper/waypoint spawning.
        /// </summary>
        public static void ExitVehicle(Entity vehic, Entity player, VehicleExitFlag eject)
        {
            if (player is not null)
            {
                // QC: player.takedamage = DAMAGE_AIM; solid = SOLID_SLIDEBOX; MOVETYPE_WALK; can be hurt again.
                player.TakeDamage = DamageMode.Aim;
                player.Solid = Solid.SlideBox;
                player.MoveType = MoveType.Walk;
                player.Vehicle = null;
                player.VehicleEnterDelay = Time + 2f; // QC: prevent instant re-entry

                // QC vehicles_exit: restore the on-foot view + hull — view_ofs = STAT(PL_VIEW_OFS); setsize(PL_MIN,
                // PL_MAX). Entering the vehicle ZEROED view_ofs (EnterVehicle), so without this the dismounted
                // player fires from its origin (W_SetupShot eye = origin + view_ofs → shot ~35u LOW) until a crouch
                // cycle re-sets the eye — the SAME eye-not-seeded bug the spawn path had. Reset the duck state +
                // standing hull too so a pre-mount crouch doesn't linger.
                player.IsDucked = false;
                player.ViewOfs = XonoticGodot.Common.Physics.PlayerPhysics.StandViewOfs;
                Vector3 standMins = XonoticGodot.Common.Physics.MovementParameters.Defaults.PlayerMins;
                Vector3 standMaxs = XonoticGodot.Common.Physics.MovementParameters.Defaults.PlayerMaxs;
                if (Api.Services is not null)
                    Api.Entities.SetSize(player, standMins, standMaxs);
                else
                {
                    player.Mins = standMins;
                    player.Maxs = standMaxs;
                    player.Size = standMaxs - standMins;
                }

                // NOTE — still cross-boundary (other systems/agents): SVC_SETVIEWPORT/SETVIEWANGLES + CSQCVehicleSetup
                // (net), the weaponentities[] switch-weapon restore (weapon view-entities), and
                // MUTATOR_CALLHOOK(VehicleExit) (no VehicleExit hook in MutatorHooks yet). The server core —
                // damage/solid/movetype restore, the view_ofs + hull restore above, link drop, re-entry delay — is done here.
            }

            // QC: vehic.flags |= FL_NOTARGET; (no longer a valid target with no pilot). We approximate
            // FL_NOTARGET with NoTarget; living vehicles also stop spinning.
            vehic.Flags |= EntFlags.NoTarget;
            if (!IsDead(vehic))
                vehic.AVelocity = Vector3.Zero;

            vehic.Team = vehic.TurHead?.Team ?? 0f;

            // QC: restore the shield-regen flag that "steal" temporarily stripped.
            if ((vehic.OldVehicleFlags & VehicleFlags.ShieldRegen) != 0)
                vehic.VehicleFlags |= VehicleFlags.ShieldRegen;
            vehic.OldVehicleFlags = VehicleFlags.None;

            vehic.Owner = null;
        }

        /// <summary>
        /// Port of <c>vehicles_findgoodexit()</c> (sv_vehicles.qc ~751): pick a clear spot to drop the pilot.
        /// Tries the preferred spot, then samples points on a ring around the vehicle until a player-sized box
        /// fits. The concrete vehicle's exit code uses this so a pilot never spawns inside a wall.
        /// </summary>
        public static Vector3 FindGoodExit(Entity vehic, Entity player, Vector3 preferSpot)
        {
            Vector3 plMin = player.Mins == Vector3.Zero ? new Vector3(-16f, -16f, -24f) : player.Mins;
            Vector3 plMax = player.Maxs == Vector3.Zero ? new Vector3(16f, 16f, 45f) : player.Maxs;

            if (Api.Services is null)
                return preferSpot;

            TraceResult tr = Api.Trace.Trace(vehic.Origin + new Vector3(0, 0, 32f), plMin, plMax, preferSpot, MoveFilter.Normal, player);
            if (tr.Fraction == 1f && !tr.StartSolid && !tr.AllSolid)
                return preferSpot;

            float mySize = 1.5f * QMath.VLen(vehic.Maxs - vehic.Mins);
            Vector3 center = 0.5f * (vehic.AbsMin + vehic.AbsMax);
            // QC vehicles_findgoodexit: autocvar_g_vehicles_exit_attempts (default 25) tries of
            // `v = normalize(randomvec() with z=0) * mysize` around the vehicle centre, via the deterministic
            // shared PRNG (ADR-0010: server seeds + broadcasts so the prediction reproduces the same draws).
            int attempts = (int)Cvar("g_vehicles_exit_attempts", 25f);
            for (int i = 0; i < attempts; ++i)
            {
                Vector3 v = Prandom.Vec();
                v.Z = 0f;                       // QC: v.z = 0
                v = center + QMath.Normalize(v) * mySize;
                TraceResult t = Api.Trace.Trace(center, plMin, plMax, v, MoveFilter.Normal, player);
                if (t.Fraction == 1f && !t.StartSolid && !t.AllSolid)
                    return v;
            }
            return vehic.Origin;
        }

        // =====================================================================================
        // SPAWN  — port of vehicles_spawn() (sv_vehicles.qc ~1107): (re)place the vehicle in the world.
        // =====================================================================================

        /// <summary>
        /// The shared half of <c>vehicles_spawn()</c>: reset the vehicle entity to its idle, ownerless, shootable
        /// state at its spawn point. Concrete vehicles call this from <see cref="Vehicle.Spawn"/> before applying
        /// their per-vehicle model/movetype/health, then schedule their own think.
        /// </summary>
        public static void SpawnVehicle(Entity vehic, Vehicle info)
        {
            vehic.Owner = null;
            vehic.VehicleDef = info;
            vehic.MoveType = MoveType.Step;     // QC: MOVETYPE_STEP
            vehic.Solid = Solid.SlideBox;       // QC: SOLID_SLIDEBOX
            vehic.TakeDamage = DamageMode.Aim;  // QC: DAMAGE_AIM
            vehic.DeadState = DeadFlag.No;      // QC: DEAD_NO
            vehic.Flags |= EntFlags.NoTarget;   // QC: FL_NOTARGET until entered
            vehic.AVelocity = Vector3.Zero;
            vehic.Velocity = Vector3.Zero;
            vehic.VehicleFlags |= VehicleFlags.IsVehicle;

            // QC vehicle_initialize (sv_vehicles.qc): dphitcontentsmask = DPCONTENTS_BODY | DPCONTENTS_SOLID,
            // plus DPCONTENTS_PLAYERCLIP when autocvar_g_playerclip_collisions (default 1). A vehicle is a moving
            // SOLID_SLIDEBOX edict; without an explicit hit-contents mask its move-trace falls back to the engine
            // default (TraceService.GenericHitMask), so the vehicle would not collide against / settle on func_clip
            // PLAYERCLIP brushes the mapper placed to fence it in. Set the BASE mask here with `=` (BEFORE the
            // descriptor's vr_setup runs — SpawnVehicle is the first call in every descriptor Spawn), so the per-
            // vehicle liquid mask the Raptor/Bumblebee add with `|=` (DPCONTENTS_LIQUIDSMASK) layers on top exactly
            // as QC vr_setup does after vehicle_initialize. Mirrors the same gate Monsters/Nexball port (default 1).
            vehic.DpHitContentsMask = SuperContentsSolid | SuperContentsBody;
            if (Cvar("g_playerclip_collisions", 1f) != 0f) // playerclip.cfg default 1
                vehic.DpHitContentsMask |= SuperContentsPlayerClip;

            // QC vehicles_spawn:1117 — this.event_damage = vehicles_damage. The vehicle carries its OWN
            // .event_damage as a GtEventDamage shim: DamageSystem.EventDamage routes a non-player edict with a
            // GtEventDamage to it and returns, so a shot vehicle runs the per-weapon damagerate + shield-then-
            // health split + knockback + death-eject (DamageVehicle) instead of taking armor-split PLAYER damage.
            // This is the seam Wave-2 vehicles depend on: it makes DamageVehicle (bit-faithful but previously
            // test-only) actually run on the live damage path.
            vehic.GtEventDamage = EventDamage;

            // QC vehicles_spawn:1116 — this.touch = vehicles_touch. Installs the crush / ram-impact / touch-board
            // handler (Touch above) so a moving piloted vehicle runs over soft targets (DEATH_VH_CRUSH) and takes
            // its own landing/ram damage (vr_impact), and an ownerless vehicle boards a toucher when
            // g_vehicles_enter==0. Requires the host collision to dispatch .Touch onto solids (cross-boundary).
            vehic.Touch = Touch;

            // QC vehicles_spawn:1118 — this.event_heal = vehicles_heal. Install the framework heal SINK so the
            // generic Combat.Heal dispatch (DamageContracts.Heal -> target.GtEventHeal) finds it: a func_heal /
            // heal-nade onto a vehicle now tops up its health (limit fallback to max_health, dead/at-limit refusal,
            // owner % mirror) exactly like the Onslaught generator/icon GtEventHeal sinks.
            vehic.GtEventHeal = HealVehicle;

            // QC vehicles_spawn:1118 — this.reset = vehicles_reset. Install the round/match-restart hook so the
            // host's reset sweep (GameWorld.ResetMapObjects → Entity.Reset) ejects any pilot, clears the pending
            // return, and respawns the vehicle on a round boundary (VehiclesReset), exactly like the movers/items/
            // monsters that install their own Entity.Reset at spawn.
            vehic.Reset = VehiclesReset;

            // QC: return to spawn (this.angles = pos2; setorigin(this, pos1)).
            vehic.Angles = vehic.SpawnAngles;
            if (Api.Services is not null)
                Api.Entities.SetOrigin(vehic, vehic.SpawnPos);

            // QC vehicles_spawn (sv_vehicles.qc): Send_Effect(EFFECT_TELEPORT, this.origin, '0 0 0', 1) — the
            // teleport-in flash when a vehicle (re)appears at its spawn point. This is a server-authoritative
            // Send_Effect, the same one-shot the teleporter exit and the Tuba instrument-switch fire, so it goes
            // through the live EffectEmitter seam (NOT a deferred CSQC visual). Closes the spawn flash presentation gap.
            if (Api.Services is not null)
                EffectEmitter.Emit("TELEPORT", vehic.Origin, Vector3.Zero, 1);

            // NOTE — cross-boundary: the bot-target list (bot AI), the hud/viewport model reset +
            // CSQCMODEL_AUTOINIT (client/net) and the lock reset remain deferred. The server spawn state — owner,
            // movetype/solid/damage, FL_NOTARGET, angles, placement at the spawn point — is set above.
        }

        /// <summary>
        /// Port of the tail of <c>vehicle_initialize()</c> (sv_vehicles.qc ~1283): fire the VehicleInit mutator
        /// hook, returning true if a mutator wants to ABORT this vehicle's one-time init
        /// (QC: <c>if (MUTATOR_CALLHOOK(VehicleInit, this)) return false;</c>). The one-time init site
        /// (VehicleSpawnFuncs.Spawn) calls this once after building the vehicle and aborts/deletes on true —
        /// distinct from <see cref="SpawnVehicle"/> which is the per-respawn reset.
        /// </summary>
        public static bool InitVehicle(Entity vehic) => MutatorHooks.FireVehicleInit(vehic);

        // =====================================================================================
        // THINK helpers — regen (vehicles_regen / vehicles_regen_resource, sv_vehicles.qc ~549).
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_regen()</c> (sv_vehicles.qc): top up an arbitrary scalar pool (shield/energy)
        /// toward <paramref name="fieldMax"/> at <paramref name="regen"/> per second, but only once
        /// <paramref name="lastTouchTime"/> + <paramref name="pause"/> has elapsed. When
        /// <paramref name="healthScale"/> is set the rate is scaled by the vehicle's current health fraction.
        /// </summary>
        public static float Regen(Entity vehic, float current, float lastTouchTime, float fieldMax,
            float pause, float regen, float dt, bool healthScale)
        {
            if (current < fieldMax && lastTouchTime + pause < Time)
            {
                if (healthScale && vehic.MaxHealth > 0f)
                    regen *= vehic.GetResource(ResourceType.Health) / vehic.MaxHealth;
                current = MathF.Min(current + regen * dt, fieldMax);

                // QC vehicles_regen: owner.(regen_field) = (this.(regen_field) / field_max) * 100 — but the scalar
                // Regen() has no `.regen_field` identity to mirror generically, so the caller is responsible for the
                // owner mirror of scalar pools (shield/energy) via MirrorOwnerStat after taking the returned value.
            }
            return current;
        }

        /// <summary>
        /// Port of <c>vehicles_regen_resource()</c> (sv_vehicles.qc): the <see cref="Regen"/> variant that tops
        /// up a real <see cref="ResourceType"/> (i.e. RES_HEALTH) on the vehicle instead of a scalar pool.
        /// </summary>
        public static void RegenResource(Entity vehic, float lastTouchTime, float fieldMax,
            float pause, float regen, float dt, bool healthScale, ResourceType resource)
        {
            float amount = vehic.GetResource(resource);
            if (amount < fieldMax && lastTouchTime + pause < Time)
            {
                if (healthScale && vehic.MaxHealth > 0f)
                    regen *= amount / vehic.MaxHealth;
                vehic.SetResource(resource, MathF.Min(amount + regen * dt, fieldMax));

                // QC vehicles_regen_resource: owner.(regen_field) = (GetResource(this, resource) / field_max) * 100.
                // For RES_HEALTH that field is .vehicle_health (the HUD low-health gauge + painframe input).
                if (vehic.Owner is not null && resource == ResourceType.Health)
                    vehic.Owner.VehicleHealth = (vehic.GetResource(resource) / fieldMax) * 100f;
            }
        }

        // =====================================================================================
        // DAMAGE — port of vehicles_damage() (sv_vehicles.qc ~625): shield-then-health, death eject.
        // =====================================================================================

        /// <summary>
        /// The vehicle's installed <c>.event_damage</c> (QC <c>vehicles_damage</c>) — the
        /// <see cref="Framework.Entity.GtEventDamage"/> shim wired in <see cref="SpawnVehicle"/>. The headless
        /// <see cref="Damage.DamageSystem.EventDamage"/> routes every non-player edict with a <c>GtEventDamage</c>
        /// here (and returns), exactly as it does for turrets / monsters / Onslaught objectives — so a vehicle
        /// victim runs the vehicle damage rules (<see cref="DamageVehicle"/>) instead of being treated as a
        /// player. This is a thin adapter: <c>GtEventDamage</c> orders the args
        /// <c>(self, inflictor, attacker, deathtype, damage, hitloc, force)</c> whereas <see cref="DamageVehicle"/>
        /// takes <c>(damage, deathType)</c> — this method just reorders.
        /// </summary>
        public static void EventDamage(Entity vehic, Entity? inflictor, Entity? attacker, string deathType,
            float damage, Vector3 hitLoc, Vector3 force)
            => DamageVehicle(vehic, inflictor, attacker, damage, deathType, hitLoc, force);

        /// <summary>
        /// Port of the core of <c>vehicles_damage()</c> (sv_vehicles.qc): route incoming damage through the
        /// vehicle's shield first (if it has one and any is left), spilling the remainder into health, then
        /// apply knockback (scaled by the vehicle's <c>.damageforcescale</c>) and — on death — eject/release the
        /// pilot and let the descriptor run its death FX + respawn. The per-weapon vehicle damage-rate table is
        /// applied here (<see cref="VehicleDamageRate"/>).
        ///
        /// Deferred vs QC (client-only): the shield-hit cosmetic entity (vehicle_shieldent colormod/alpha flash),
        /// antilag. The SND_ONS_* hit/shield sounds ARE emitted here (spamsound on CH_PAIN, sound_allowed-gated).
        /// </summary>
        public static void DamageVehicle(Entity vehic, Entity? inflictor, Entity? attacker,
            float damage, string deathType, Vector3 hitLoc, Vector3 force)
        {
            if (vehic.TakeDamage == DamageMode.No || IsDead(vehic))
                return;

            vehic.DmgTime = Time;
            vehic.Enemy = attacker;
            vehic.PainFinished = Time; // QC vehicles_damage: this.pain_finished = time

            // Per-weapon vehicle damage-rate (QC vehicles_damage WEAPONTODO): some weapons hit vehicles for a
            // fraction (vortex/machinegun/rifle/vaporizer at <1) and some for a multiple (any other weapon at
            // 2x, the tag/seeker at 5x), so a vehicle "feels" different against each weapon. Keyed off the
            // weapon NetName the deathtype carries (DeathTypes.FromWeapon).
            damage *= VehicleDamageRate(deathType);

            if ((vehic.VehicleFlags & VehicleFlags.HasShield) != 0 && vehic.VehicleShield > 0f)
            {
                vehic.VehicleShield -= damage;
                if (vehic.VehicleShield < 0f)
                {
                    // Shield depleted: spill the overflow into health (QC TakeResource(RES_HEALTH, |shield|)),
                    // and play the heavy hit (SND_ONS_HIT2) at CH_PAIN, VOL_BASE, ATTEN_NORM.
                    vehic.TakeResource(ResourceType.Health, MathF.Abs(vehic.VehicleShield));
                    vehic.VehicleShield = 0f;
                    // QC (sv_vehicles.qc:670-671): spamsound(this, CH_PAIN, SND_ONS_HIT2, VOL_BASE, ATTEN_NORM)
                    // gated by sound_allowed(MSG_BROADCAST, attacker). CH_PAIN (-6) is the stacking auto channel.
                    if (Api.Services is not null && SoundAllowedGate.IsAllowed(attacker))
                        SoundSystem.SpamSoundRaw(vehic, "onslaught/ons_hit2.wav", Time,
                            SoundChannel.PainAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
                    // NOTE (client-render): the vehicle_shieldent colormod+alpha hit flash is a CSQC visual.
                }
                else
                {
                    // Shield absorbed it: QC (sv_vehicles.qc:674-675) spamsound(this, CH_PAIN,
                    // SND_ONS_ELECTRICITY_EXPLODE, VOL_BASE, ATTEN_NORM) gated by sound_allowed(MSG_BROADCAST, attacker).
                    if (Api.Services is not null && SoundAllowedGate.IsAllowed(attacker))
                        SoundSystem.SpamSoundRaw(vehic, "onslaught/electricity_explode.wav", Time,
                            SoundChannel.PainAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
                }
            }
            else
            {
                vehic.TakeResource(ResourceType.Health, damage);
                // QC (sv_vehicles.qc:681-682): spamsound(this, CH_PAIN, SND_ONS_HIT2, VOL_BASE, ATTEN_NORM)
                // gated by sound_allowed(MSG_BROADCAST, attacker). spamsound rate-limits to once per sim step so
                // repeated touch-damage calls don't over-emit. CH_PAIN (-6) is the stacking auto channel.
                if (Api.Services is not null && SoundAllowedGate.IsAllowed(attacker))
                    SoundSystem.SpamSoundRaw(vehic, "onslaught/ons_hit2.wav", Time,
                        SoundChannel.PainAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
            }

            // QC applies knockback scaled by .damageforcescale (set per vehicle in vr_spawn), else raw force.
            float fscale = vehic.DamageForceScale;
            vehic.Velocity += (fscale > 0f && fscale < 1f) ? force * fscale : force;
            _ = hitLoc;

            if (vehic.GetResource(ResourceType.Health) <= 0f)
            {
                // Mark dying BEFORE ejecting so the descriptor's Exit() takes its death-eject branch (QC passes
                // VHEF_EJECT to vehicles_exit before vr_death; the Exit override keys off IsDead here).
                if (vehic.DeadState == DeadFlag.No)
                    vehic.DeadState = DeadFlag.Dying;

                // QC vehicles_damage: if (this.owner) { VHF_DEATHEJECT ? vehicles_exit(VHEF_EJECT) : vehicles_exit(VHEF_RELEASE); }
                Entity? pilot = vehic.Owner;
                if (pilot is not null)
                    vehic.VehicleDef?.Exit(vehic, pilot); // descriptor computes eject vector + calls ExitVehicle

                vehic.VehicleDef?.Death(vehic);

                // QC vehicles_damage tail: vehicles_setreturn(this) — schedule the destroyed-vehicle return so the
                // descriptor Death()'s respawn timer is mirrored by a return-waypoint entity (radar icon while it
                // returns). antilag_clear(this,this) is cross-boundary (antilag system) and deferred.
                SetReturn(vehic);
            }
        }

        /// <summary>
        /// Port of the per-weapon vehicle damage-rate table at the top of <c>vehicles_damage()</c>
        /// (sv_vehicles.qh autocvar_g_vehicles_*_damagerate). Returns the multiplier applied to incoming
        /// damage based on the attacking weapon (carried by <paramref name="deathType"/>). Non-weapon damage
        /// (falls, blasts, drowning) is unscaled (1).
        /// </summary>
        public static float VehicleDamageRate(string deathType)
        {
            string wep = Damage.DeathTypes.WeaponNetNameOf(deathType);
            if (wep.Length == 0)
                return 1f; // non-weapon (environment / vehicle blast) damage is unscaled

            // Defaults mirror sv_vehicles.qh; read the cvar by name so a server config can retune them.
            return wep switch
            {
                "vortex"    => Cvar("g_vehicles_vortex_damagerate", 0.75f),
                "machinegun" => Cvar("g_vehicles_machinegun_damagerate", 0.75f),
                "rifle"     => Cvar("g_vehicles_rifle_damagerate", 0.75f),
                "vaporizer" => Cvar("g_vehicles_vaporizer_damagerate", 0.5f),
                "seeker"    => Cvar("g_vehicles_tag_damagerate", 5f),     // WEP_SEEKER == the tag weapon
                _           => Cvar("g_vehicles_weapon_damagerate", 2f),  // every other weapon hits 2x
            };
        }

        private static float Cvar(string name, float fallback)
        {
            if (Api.Services is null) return fallback;
            float v = Api.Cvars.GetFloat(name);
            return v != 0f ? v : fallback;
        }

        // =====================================================================================
        // PROJECTILE — port of vehicles_projectile() (sv_vehicles.qc ~221): the shared vehicle bolt/rocket.
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_projectile()</c> (sv_vehicles.qc): spawn the generic vehicle projectile used by
        /// every vehicle weapon (racer laser/rocket, raptor/bumblebee cannon, spiderbot rocket). It flies as a
        /// MOVETYPE_FLYMISSILE bbox owned by the vehicle but credited to the pilot, explodes on touch via
        /// <see cref="WeaponSplash.RadiusDamage"/>, and self-removes after its lifetime.
        ///
        /// <paramref name="health"/> &gt; 0 makes the projectile shootable (QC DAMAGE_AIM). The CSQCProjectile
        /// visual networking and muzzle effect are out of scope (NOTE: client/net).
        /// </summary>
        public static Entity SpawnProjectile(Entity owner, Entity pilot, Vector3 origin, Vector3 velocity,
            float damage, float radius, float force, float size, string deathType,
            float health, float lifetime, string? fireSound = null)
        {
            Entity proj = Api.Entities.Spawn();
            proj.ClassName = "vehicles_projectile";
            proj.Owner = owner;       // QC proj.owner = this (the vehicle/turret)
            proj.DmgInflictor = pilot; // QC proj.realowner = the crediting pilot
            proj.MoveType = MoveType.FlyMissile;
            // QC PROJECTILE_MAKETRIGGER (sv_vehicles.qc:230): SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE so
            // the vehicle projectile is transparent to the firing vehicle's bbox — can't collide with / detonate on it.
            Projectiles.MakeTrigger(proj);
            proj.Flags |= EntFlags.Item; // QC FL_PROJECTILE
            proj.Velocity = velocity;
            proj.Angles = QMath.VecToAngles(velocity);

            float s = MathF.Max(size, 1f);
            Api.Entities.SetSize(proj, new Vector3(-s, -s, -s), new Vector3(s, s, s));
            Api.Entities.SetOrigin(proj, origin);

            void Explode(Entity self)
            {
                self.Touch = null;
                self.Think = null;
                self.TakeDamage = DamageMode.No;
                self.GtEventDamage = null; // QC vehicles_projectile_explode: this.event_damage = func_null
                // Carry the per-vehicle special deathtype (vh_*_gun/_rocket/…) through the blast so a kill
                // routes to the vehicle obituary line, not a generic weapon line.
                WeaponSplash.RadiusDamage(self, self.Origin, damage, 0f, radius, self.DmgInflictor, 0, force, deathTag: deathType);
                Api.Entities.Remove(self);
            }

            if (health > 0f)
            {
                proj.TakeDamage = DamageMode.Aim;
                proj.Health = health;
                // QC vehicles_projectile(): _health -> proj.event_damage = vehicles_projectile_damage. The shootable
                // vehicle bolt/rocket can be SHOT DOWN — it takes RES_HEALTH damage, accepts the knockback, and
                // detonates once depleted. DamageSystem.EventDamage routes a non-player victim with a GtEventDamage
                // to it, so installing this here is what makes the projectile destructible on the live path.
                proj.GtEventDamage = (self, inflictor, _attacker, _deathType, dmg, _hitLoc, frc) =>
                {
                    // QC: "Ignore damage from other projectiles from my owner (dont mess up volly's)" — a vehicle's
                    // own salvo (same owning vehicle) passes straight through its in-flight projectiles.
                    if (inflictor is not null && ReferenceEquals(inflictor.Owner, self.Owner))
                        return;

                    self.TakeResource(ResourceType.Health, dmg);
                    self.Velocity += frc;
                    // QC: GetResource(this, RES_HEALTH) < 1 -> detonate (takedamage NO, event_damage null,
                    // setthink(adaptor_think2use) at time, which fires .use -> vehicles_projectile_explode).
                    if (self.GetResource(ResourceType.Health) < 1f)
                        Explode(self);
                };
            }
            else
            {
                proj.Flags |= EntFlags.NoTarget;
            }

            proj.Touch = (self, _) => Explode(self);
            proj.Think = Explode;
            proj.NextThink = Time + (lifetime > 0f ? lifetime : 30f); // QC default nextthink = time + 30

            if (fireSound is not null)
                Api.Sound.Play(owner, SoundChannel.Weapon, fireSound);

            // NOTE — cross-boundary: CSQCProjectile networking + muzzle EFFECT_* (client/net) and the bot-dodge
            // list (bot AI). The server projectile — movetype, ownership, splash, lifetime, fire sound, and the
            // shootable-projectile event_damage (incl. the owner==owner salvo pass-through) — is wired above.
            return proj;
        }

        // =====================================================================================
        // HEAL — port of vehicles_heal() (sv_vehicles.qc ~708): the framework .event_heal SINK.
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_heal()</c> (sv_vehicles.qc): the shared vehicle <c>.event_heal</c> handler — top up
        /// the vehicle's health toward <paramref name="limit"/> (or its <c>max_health</c> when limit is
        /// <see cref="ResLimitNone"/>), but never a dead vehicle and never past the limit. On a successful heal it
        /// mirrors the new 0..100 health percentage onto the owning pilot (<c>owner.vehicle_health</c>), exactly
        /// like the regen path. Returns true if any health was actually given.
        ///
        /// This is the framework heal SINK (func_heal / heal-nade onto a vehicle), distinct from the Bumblebee
        /// healray which is the heal SOURCE. Concrete vehicles install this as their <c>.event_heal</c> in
        /// <see cref="SpawnVehicle"/> so a generic heal dispatch finds it.
        /// </summary>
        public static bool HealVehicle(Entity vehic, Entity? inflictor, float amount, float limit)
        {
            float trueLimit = limit != ResLimitNone ? limit : vehic.MaxHealth;
            float hp = vehic.GetResource(ResourceType.Health);
            // QC: dead (<=0) or already at/over the limit -> no heal.
            if (hp <= 0f || hp >= trueLimit)
                return false;

            vehic.GiveResourceWithLimit(ResourceType.Health, amount, trueLimit);
            if (vehic.Owner is not null && vehic.MaxHealth > 0f)
                vehic.Owner.VehicleHealth = (vehic.GetResource(ResourceType.Health) / vehic.MaxHealth) * 100f;
            _ = inflictor;
            return true;
        }

        /// <summary>QC <c>RES_LIMIT_NONE</c> — sentinel "no explicit limit; fall back to max_health".</summary>
        public const float ResLimitNone = -1f;

        // =====================================================================================
        // PAINFRAME — port of vehicles_painframe() (sv_vehicles.qc ~594): low-health smoke + jitter.
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_painframe()</c> (sv_vehicles.qc): once the vehicle drops to &lt;=50% health, on a
        /// random 0.1..0.6s cadence emit low-health smoke and — per the descriptor's VHF_DMGSHAKE / VHF_DMGROLL /
        /// VHF_DMGHEADROLL flags — jitter the vehicle's velocity and body/head angles so a damaged vehicle visibly
        /// shudders. Driven from each descriptor's <see cref="Vehicle.Think"/> (QC vehicles_frame calls it every
        /// tick). The smoke particle is presentation (NOTE: client/net) — the authoritative velocity/angle jitter
        /// (which a remote pilot feels and which the server transmits) IS applied here.
        /// </summary>
        public static void PainFrame(Entity vehic)
        {
            // QC vehicles_think (sv_vehicles.qc:1084): when owned, mirror VEHICLESTAT_W2MODE onto the pilot every
            // think so the client knows the vehicle's secondary-weapon mode (Raptor bomb/missile, Spiderbot rocket
            // guided/dumb). The descriptors write vehicle.VehW2Mode; this is the per-tick owner mirror. PainFrame is
            // the shared think hub (called from every descriptor's Think tail, like QC vehicles_painframe), so the
            // mirror lives here.
            if (vehic.Owner is not null)
                vehic.Owner.VehW2Mode = vehic.VehW2Mode;

            // QC: myhealth = owner ? owner.vehicle_health : (GetResource(RES_HEALTH)/max_health)*100.
            float myHealth = vehic.Owner is not null
                ? vehic.Owner.VehicleHealth
                : (vehic.MaxHealth > 0f ? (vehic.GetResource(ResourceType.Health) / vehic.MaxHealth) * 100f : 100f);

            if (myHealth <= 50f && vehic.PainFrame < Time)
            {
                float ftmp = myHealth / 50f;
                // QC: pain_frame = time + max(0.1, 0.1 + random()*0.5*_ftmp).
                vehic.PainFrame = Time + MathF.Max(0.1f, 0.1f + Prandom.Float() * 0.5f * ftmp);

                // QC vehicles_painframe (sv_vehicles.qc:605): Send_Effect(EFFECT_SMOKE_SMALL,
                // origin + randomvec()*80, '0 0 0', 1) — the low-health smoke puff. EffectEmitter now exists in
                // the Common layer (used by the descriptor death/muzzle FX + Racer's under-craft trail), so the
                // old "no headless emitter" deferral no longer applies: emit the same server-authoritative
                // Send_Effect the descriptors do.
                if (Api.Services is not null)
                    EffectEmitter.Emit("SMOKE_SMALL", vehic.Origin + Prandom.Vec() * 80f, Vector3.Zero, 1);

                if ((vehic.VehicleFlags & VehicleFlags.DmgShake) != 0)
                    vehic.Velocity += Prandom.Vec() * 30f; // QC: velocity += randomvec()*30

                if ((vehic.VehicleFlags & VehicleFlags.DmgRoll) != 0)
                {
                    // QC: VHF_DMGHEADROLL ? tur_head.angles += randomvec() : angles += randomvec().
                    if ((vehic.VehicleFlags & VehicleFlags.DmgHeadRoll) != 0 && vehic.TurHead is not null)
                        vehic.TurHead.Angles += Prandom.Vec();
                    else
                        vehic.Angles += Prandom.Vec();
                }
            }
        }

        // =====================================================================================
        // TOUCH / CRUSH / IMPACT — port of vehicles_touch + vehicles_crushable + vehicles_impact.
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_crushable()</c> (sv_vehicles.qc): a player past its enter-delay, or any monster,
        /// is a "soft target" a moving vehicle runs over (rather than taking its own impact damage from).
        /// </summary>
        public static bool Crushable(Entity e)
        {
            if ((e.Flags & EntFlags.Client) != 0 && Time >= e.VehicleEnterDelay)
                return true; // QC: IS_PLAYER(e) && time >= e.vehicle_enter_delay
            if ((e.Flags & EntFlags.Monster) != 0)
                return true; // QC: IS_MONSTER(e)
            return false;
        }

        /// <summary>
        /// Port of <c>vehicles_touch()</c> (sv_vehicles.qc ~874): the vehicle's <c>.touch</c> handler. Fires the
        /// VehicleTouch mutator hook (suppress-on-true); while piloted it either CRUSHES a soft target it runs
        /// over (DEATH_VH_CRUSH, gated on speed) or takes its own ram/landing impact damage; ownerless it boards
        /// the toucher when touch-board is enabled (<c>g_vehicles_enter==0</c>). Concrete vehicles install this as
        /// their <c>.touch</c> in <see cref="SpawnVehicle"/> (or the host dual-dispatches solid touches).
        /// </summary>
        public static void Touch(Entity vehic, Entity toucher)
        {
            // QC: if (MUTATOR_CALLHOOK(VehicleTouch, this, toucher)) return;
            if (toucher is not null && MutatorHooks.FireVehicleTouch(vehic, toucher))
                return;

            if (vehic.Owner is not null)
            {
                // QC: toucher above the vehicle top, crushable, and the pilot's weapon isn't locked.
                if (toucher is not null
                    && vehic.Origin.Z + vehic.Maxs.Z > toucher.Origin.Z
                    && Crushable(toucher)
                    && !WeaponLocked(vehic.Owner))
                {
                    float minspeed = Cvar("g_vehicles_crush_minspeed", 100f);
                    if (QMath.VLen(vehic.Velocity) >= minspeed)
                    {
                        Vector3 dir = QMath.Normalize(toucher.Origin - vehic.Origin) * Cvar("g_vehicles_crush_force", 50f);
                        Combat.Damage(toucher, vehic, vehic.Owner, Cvar("g_vehicles_crush_dmg", 70f),
                            DeathTypes.VhCrush, Vector3.Zero, dir);
                    }
                    return; // QC: don't self-damage when hitting a soft target.
                }

                // QC: if (this.play_time < time) info.vr_impact(info, this);
                if (vehic.PlayTime < Time)
                    vehic.VehicleDef?.Impact(vehic);
                return;
            }

            // QC: if (!autocvar_g_vehicles_enter) vehicles_enter(toucher, this).
            if (toucher is not null && Cvar("g_vehicles_enter", 1f) == 0f)
                vehic.VehicleDef?.Enter(vehic, toucher);
        }

        /// <summary>
        /// Port of <c>vehicles_impact()</c> (sv_vehicles.qc ~731): apply DEATH_FALL self-damage when the vehicle's
        /// speed change since last tick exceeds <paramref name="minspeed"/> (a hard landing or ram), capped at
        /// <paramref name="maxpain"/> and scaled by <paramref name="speedfac"/>, gated to once / 0.25s and
        /// skipping NOIMPACT surfaces. The descriptor's <c>vr_impact</c> calls this with its per-vehicle
        /// thresholds; <see cref="Entity.OldVelocity"/> is the last-tick velocity (the descriptor Think snapshots it).
        /// </summary>
        public static void Impact(Entity vehic, float minspeed, float speedfac, float maxpain)
        {
            // QC: if (trace_dphitq3surfaceflags & Q3SURFACEFLAG_NOIMPACT) return. The QC global trace_* side-effect
            // of the touch collision isn't surfaced to this headless touch callback (no per-touch surface-flag in
            // the port collision result), so the NOIMPACT skip is a deferred fidelity detail (NOTE: collision).

            Vector3 delta = vehic.Velocity - vehic.OldVelocity;
            if (vehic.PlayTime < Time && QMath.VLen(delta) > minspeed)
            {
                float dmg = MathF.Min(speedfac * QMath.VLen(delta), maxpain);
                Combat.Damage(vehic, null, null, dmg, DeathTypes.Fall, vehic.Origin, Vector3.Zero);
                vehic.PlayTime = Time + 0.25f; // QC: play_time = time + 0.25
            }
        }

        /// <summary>
        /// QC <c>weaponLocked(it)</c>: the pilot's weapon is locked (mid-switch / pre-round). The headless sim has
        /// no per-frame weapon lock plumbed yet (same faithful subset as SpawnNearTeammateMutator.WeaponLocked),
        /// so this is always false — the crush still fires, which matches the common in-match case.
        /// </summary>
        private static bool WeaponLocked(Entity e) { _ = e; return false; }

        // =====================================================================================
        // RETURN — port of vehicles_setreturn() (sv_vehicles.qc ~501): schedule the destroyed/abandoned return.
        // =====================================================================================

        /// <summary>
        /// Port of <c>vehicles_setreturn()</c> (sv_vehicles.qc): schedule a destroyed (or abandoned-living)
        /// vehicle to return to its spawn point after <see cref="Entity.RespawnTime"/>. A destroyed vehicle uses
        /// the full respawn delay; a still-living one uses respawntime-1 so an idle vehicle drifts home a touch
        /// sooner. The descriptor's Death() already reschedules the respawn Think; this re-arms that Think so the
        /// return is honoured even when SetReturn is reached via the abandoned-living path (vehicles_exit), and is
        /// the single place that wiring would attach the WP_Vehicle return waypoint (presentation, deferred).
        /// </summary>
        public static void SetReturn(Entity vehic)
        {
            if (vehic.RespawnTime <= 0f)
                return;

            // QC vehicles_setreturn (sv_vehicles.qc:511-517): dead -> nextthink = min(time+respawntime,
            // time+respawntime-5) == time + respawntime - 5; alive -> time + respawntime - 1. (Base uses min()
            // with itself-minus-N, which is just the smaller value.) The descriptor's Spawn() is the return
            // target — it re-places the vehicle at pos1/pos2 and re-arms the normal think, exactly as QC's
            // vehicles_return reparents the think back to vehicles_spawn.
            float delay = MathF.Max(0f, vehic.RespawnTime - (IsDead(vehic) ? 5f : 1f));
            vehic.Think = self => self.VehicleDef?.Spawn(self);
            vehic.NextThink = Time + delay;

            // NOTE — cross-boundary (CSQC): the WP_Vehicle return waypoint + radar icon (vehicles_showwp) and the
            // EFFECT_TELEPORT return flash are presentation and unported; the authoritative return SCHEDULE is set
            // above.
        }

        /// <summary>
        /// Port of <c>vehicles_reset()</c> (sv_vehicles.qc:1095) — the round/match-restart <c>.reset</c> hook. On a
        /// round restart the host's reset sweep (GameWorld.ResetMapObjects → <see cref="Entity.Reset"/>) fires this
        /// for every vehicle: release any pilot (QC <c>vehicles_exit(VHEF_RELEASE)</c>), clear any pending
        /// return-to-spawn schedule (QC <c>vehicles_clearreturn</c>), and respawn the vehicle when it is active (QC
        /// <c>if (active != ACTIVE_NOT) vehicles_spawn(this)</c>). The port has no map-scripted ACTIVE state, so —
        /// matching the stock-map common case where every vehicle is ACTIVE_ACTIVE — it always respawns.
        /// Installed onto <see cref="Entity.Reset"/> in <see cref="SpawnVehicle"/>.
        /// </summary>
        public static void VehiclesReset(Entity vehic)
        {
            // QC: if (this.owner) vehicles_exit(this, VHEF_RELEASE) — eject/release any pilot back to a walking
            // player. The descriptor's Exit() computes the eject vector + calls ExitVehicle, exactly like the death
            // path's release (DamageVehicle), so route through it to keep the player-restore identical.
            Entity? pilot = vehic.Owner;
            if (pilot is not null)
                vehic.VehicleDef?.Exit(vehic, pilot);

            // QC: vehicles_clearreturn(this) — remove the "return helper" entity that would otherwise re-run
            // vehicles_spawn on its own schedule. The port has no separate return-helper entity (SetReturn re-arms
            // the vehicle's OWN Think), so clearing the return means cancelling that scheduled Think before the
            // unconditional respawn below re-arms the normal think — otherwise a stale return Think could fire first.
            vehic.Think = null;
            vehic.NextThink = 0f;

            // QC: if (this.active != ACTIVE_NOT) vehicles_spawn(this) — return the vehicle to its idle, ownerless,
            // full-health state at its spawn point (which also re-arms vehicles_think). The descriptor's Spawn folds
            // vehicle_initialize's per-vehicle setup + vehicles_spawn; SetReturn uses it as the return target too.
            vehic.VehicleDef?.Spawn(vehic);
        }

        // =====================================================================================
        // DELAYSPAWN — port of the g_vehicles_delayspawn nextthink branch of vehicle_initialize
        //              (sv_vehicles.qc ~1276-1281): stagger the FIRST activation of a map-placed vehicle.
        // =====================================================================================

        // QC DPCONTENTS_* hit-contents bits (qcsrc reference DPCONTENTS_*; the live values live in
        // XonoticGodot.Engine.Collision.SuperContents which Common cannot reference — Engine depends on Common, not
        // the reverse). Mirrored here EXACTLY as MonsterAI.cs / Nexball.cs / LagComp.cs do; TraceService honors
        // Entity.DpHitContentsMask. Used to stamp the vehicle move-trace mask in SpawnVehicle (vehicle_initialize).
        private const int SuperContentsSolid      = 0x00000001; // QC DPCONTENTS_SOLID
        private const int SuperContentsBody       = 0x02000000; // QC DPCONTENTS_BODY
        private const int SuperContentsPlayerClip = 0x00000100; // QC DPCONTENTS_PLAYERCLIP

        /// <summary>QC EF_NODRAW (dpextensions) — the model is not rendered while a vehicle is parked pre-spawn.</summary>
        private const int EfNoDraw = 16; // EF_NODRAW

        /// <summary>
        /// Port of the <c>autocvar_g_vehicles_delayspawn</c> first-think branch of <c>vehicle_initialize</c>
        /// (sv_vehicles.qc:1278-1280): <c>this.nextthink = time + this.respawntime + random() *
        /// autocvar_g_vehicles_delayspawn_jitter;</c>. Base does NOT run <c>vehicles_spawn</c> at map-placement
        /// under delayspawn — the vehicle stays hidden + inert and its think (which IS <c>vehicles_spawn</c>) only
        /// fires after the staggered delay, so a row of stock-map vehicles activates at randomised offsets instead
        /// of all at once at <c>game_starttime</c>. The port FOLDS <c>vehicles_spawn</c> into the descriptor's
        /// <see cref="Vehicle.Spawn"/> (already run inline so the hull exists for the drop-to-floor trace), so to
        /// reproduce the timing this PARKS the just-built vehicle — EF_NODRAW (hidden), SOLID_NOT + DAMAGE_NO
        /// (un-shootable AND un-boardable: <c>FindBoardableInRadius</c> rejects a <c>DAMAGE_NO</c> vehicle, and
        /// <c>DamageSystem</c> ignores a <c>DAMAGE_NO</c> target) — then re-arms the descriptor's Spawn as the
        /// delayed think. When that think fires, Spawn re-places the vehicle at its (already floor-dropped)
        /// SpawnPos/SpawnAngles and re-establishes SOLID/DAMAGE/model/idle-think, so we only clear EF_NODRAW.
        /// </summary>
        public static void ScheduleDelayedSpawn(Entity vehic, float jitter)
        {
            // QC: nextthink = time + respawntime + random() * jitter. RespawnTime was just set by def.Spawn.
            float delay = vehic.RespawnTime + Prandom.Float() * jitter;

            // Park the vehicle inert + hidden for the delay (QC: EF_NODRAW + the un-spawned vehicle is not yet
            // shootable/boardable). DAMAGE_NO is the gate FindBoardableInRadius (VehicleBoarding.cs) keys off, so
            // a player can't board the staggered vehicle early; DamageSystem skips a DAMAGE_NO target too.
            vehic.Effects |= EfNoDraw;
            vehic.Solid = Solid.Not;
            vehic.TakeDamage = DamageMode.No;
            vehic.MoveType = MoveType.None;
            vehic.Velocity = Vector3.Zero;
            vehic.AVelocity = Vector3.Zero;
            vehic.Owner = null;

            // The delayed think is the descriptor's Spawn (= the FOLDED vehicles_spawn), exactly as SetReturn and
            // VehiclesReset use it as the (re)spawn target. Clear EF_NODRAW first since Spawn does not touch it.
            vehic.Think = self =>
            {
                self.Effects &= ~EfNoDraw;
                self.VehicleDef?.Spawn(self);
            };
            vehic.NextThink = Time + delay;
        }

        // =====================================================================================
        // GIBS — port of vehicle_tossgib + vehicles_gib_explode/touch/think (sv_vehicles.qc ~274-326).
        // =====================================================================================

        /// <summary>QC EF_FLAME (dpextensions.qc:125) — the burning-debris flame the client renders.</summary>
        private const int EfFlame = 1024;
        /// <summary>QC EF_LOWPRECISION (dpextensions) — bandwidth hint for a short-lived debris entity.</summary>
        private const int EfLowPrecision = 4194304;

        /// <summary>
        /// Port of <c>vehicles_gib_explode()</c> (sv_vehicles.qc:274): a tossed debris gib that detonates — play
        /// SND_ROCKET_IMPACT and pop two EFFECT_EXPLOSION_SMALL (one drifting near the gib, one at the dead
        /// vehicle's origin via <c>wp00</c>), then remove the gib. Gibs are pure FX (no RadiusDamage).
        /// </summary>
        private static void GibExplode(Entity gib)
        {
            if (Api.Services is not null)
            {
                // QC: sound(this, CH_SHOTS, SND_ROCKET_IMPACT, VOL_BASE, ATTEN_NORM).
                Api.Sound.Play(gib, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav");
                // QC: Send_Effect(EFFECT_EXPLOSION_SMALL, randomvec()*80 + (origin + '0 0 100'), '0 0 0', 1)
                EffectEmitter.Emit("EXPLOSION_SMALL", Prandom.Vec() * 80f + (gib.Origin + new Vector3(0f, 0f, 100f)), Vector3.Zero, 1);
                // QC: Send_Effect(EFFECT_EXPLOSION_SMALL, this.wp00.origin + '0 0 64', '0 0 0', 1) — at the wreck.
                Vector3 wreckOrigin = gib.GoalEntity is not null ? gib.GoalEntity.Origin : gib.Origin;
                EffectEmitter.Emit("EXPLOSION_SMALL", wreckOrigin + new Vector3(0f, 0f, 64f), Vector3.Zero, 1);
                Api.Entities.Remove(gib);
            }
        }

        /// <summary>
        /// Port of <c>vehicle_tossgib()</c> (sv_vehicles.qc:296): spawn a model debris gib at a tag of the dying
        /// vehicle and toss it with <paramref name="vel"/> + <paramref name="rot"/> spin. Burning gibs trail flame
        /// (EF_FLAME); <paramref name="explode"/> gibs blow up on contact / after a random delay; the rest fade out
        /// over <paramref name="maxtime"/>. The debris is the per-piece wreckage Base scatters from a destroyed
        /// vehicle's <c>vr_death</c> (only the Bumblebee uses it). Pure presentation/physics: a gib never deals
        /// damage. <paramref name="template"/> supplies the model; <paramref name="tag"/> the spawn origin (empty =
        /// the vehicle origin, for the body gib).
        /// </summary>
        public static Entity TossGib(Entity vehic, Entity template, Vector3 vel, string tag,
            bool burn, bool explode, float maxtime, Vector3 rot)
        {
            Entity gib = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
            gib.ClassName = "vehicle_gib";
            gib.GoalEntity = vehic; // QC .wp00 = the dead vehicle (gib_explode pops a blast at its origin)

            // QC: _setmodel(_gib, _template.model). The gun gibs carry their cannon model strings; the body gib
            // reuses the vehicle body model. Fall back to the vehicle model when a sub-gun has no model string.
            string model = !string.IsNullOrEmpty(template.Model) ? template.Model : vehic.Model;
            gib.Model = model;
            if (Api.Services is not null && !string.IsNullOrEmpty(model))
                Api.Entities.SetModel(gib, model);

            // QC: org = gettaginfo(this, gettagindex(this, _tag)); setorigin(_gib, org). Empty tag -> vehicle origin.
            Vector3 org = string.IsNullOrEmpty(tag) ? vehic.Origin : VehiclePhysics.TagOrigin(vehic, tag);
            if (Api.Services is not null)
                Api.Entities.SetOrigin(gib, org);
            else
                gib.Origin = org;

            gib.Velocity = vel;
            gib.MoveType = MoveType.Toss;     // QC: MOVETYPE_TOSS
            gib.Solid = Solid.Corpse;         // QC: SOLID_CORPSE
            gib.ColorModKey = new Vector3(-0.5f, -0.5f, -0.5f); // QC: colormod = '-0.5 -0.5 -0.5' (darkened wreckage)
            gib.Effects = EfLowPrecision;     // QC: effects = EF_LOWPRECISION
            gib.AVelocity = rot;              // QC: avelocity = _rot

            if (burn)
                gib.Effects |= EfFlame;       // QC: if (_burn) effects |= EF_FLAME

            if (explode)
            {
                // QC: setthink(gib_explode); nextthink = time + random()*_explode; settouch(gib_touch).
                // (Base passes the same _maxtime value as _explode for the random detonation window.)
                gib.Think = GibExplode;
                gib.NextThink = Time + Prandom.Float() * maxtime;
                gib.Touch = (self, _) => GibExplode(self); // QC vehicles_gib_touch -> vehicles_gib_explode
            }
            else
            {
                // QC: cnt = time + _maxtime; setthink(gib_think); nextthink = time + _maxtime - 1; alpha = 1.
                float deadline = Time + maxtime;
                gib.Alpha = 1f;
                gib.Think = self =>
                {
                    // QC vehicles_gib_think: alpha -= 0.1; if (cnt >= time) delete; else nextthink = time + 0.1.
                    self.Alpha -= 0.1f;
                    if (deadline <= Time)
                    {
                        if (Api.Services is not null) Api.Entities.Remove(self);
                    }
                    else
                        self.NextThink = Time + 0.1f;
                };
                gib.NextThink = Time + MathF.Max(0f, maxtime - 1f);
            }
            return gib;
        }

        /// <summary>QC IS_DEAD(e).</summary>
        public static bool IsDead(Entity e) => e.DeadState != DeadFlag.No;

        /// <summary>
        /// QC <c>game_stopped</c> freeze used at the top of every <c>*_frame</c>: a stopped round parks the
        /// vehicle (SOLID_NOT / DAMAGE_NO / MOVETYPE_NONE). Concrete <see cref="Vehicle.Think"/> overrides call
        /// this and bail when it returns true.
        /// </summary>
        public static bool FreezeIfGameStopped(Entity vehic)
        {
            // Read the host-driven flag (set by the match loop), falling back to the cvar mirror so the gate
            // works even before a host wires GameStopped (QC keeps game_stopped as a plain global).
            bool stopped = GameStopped || (Api.Services is not null && Api.Cvars.GetFloat("g_game_stopped") != 0f);
            if (!stopped)
                return false;

            // QC racer_frame/raptor_frame/etc.: park the vehicle while the round is frozen.
            vehic.Solid = Solid.Not;
            vehic.TakeDamage = DamageMode.No;
            vehic.MoveType = MoveType.None;
            return true;
        }
    }
}

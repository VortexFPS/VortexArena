// Port: qcsrc/common/vehicles/vehicle/racer.{qh,qc} + racer_weapon.{qh,qc}
//
// The Racer ("wakizashi") — a fast single-seat hovercraft. Primary fire is a rapid energy laser cannon,
// secondary fires a pair of guided/ground-hugging rockets; jump afterburns (drains energy). Balance values
// are the bal/server cfg defaults inlined below (g_vehicle_racer_*).
//
// FULL deep behavior is implemented here: the 4-point engine-spring hover (racer_align4point via
// VehiclePhysics.ForceFromTag), the racer_frame avelocity-free angle controller (yaw/pitch/roll toward the
// pilot's view, friction, downforce, water handling), the afterburner energy drain on the real jump button,
// and the homing/ground-hugging rocket guidance (racer_rocket_tracker / racer_rocket_groundhugger via
// VehiclePhysics.GuideRocket). Server-side Send_Effect emissions (booster puff, under-craft smoke, cannon
// muzzleflash, rocket-launch flash, death explosion) and the death colormod darken are emitted via
// EffectEmitter / ColorModKey. NOTE (client-render): only genuinely client-side items — the CSQCProjectile
// bolt/rocket trails, the aux crosshair lock color, the cockpit hud model, and the HUD gauge that READS the
// already-written % stats — remain.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Racer vehicle (QC <c>CLASS(Racer, Vehicle)</c>). Registered into <see cref="Vehicles"/> via
/// <see cref="VehicleAttribute"/>.
/// </summary>
[Vehicle]
public sealed class Racer : Vehicle
{
    // ---- chassis (racer.qc autocvars) ----
    public float SpeedForward = 650f;     // g_vehicle_racer_speed_forward
    public float SpeedStrafe = 650f;      // g_vehicle_racer_speed_strafe
    public float SpeedAfterburn = 3000f;  // g_vehicle_racer_speed_afterburn
    public float AfterburnCost = 130f;    // g_vehicle_racer_afterburn_cost (energy/sec)
    public float WaterburnCost = 5f;      // g_vehicle_racer_waterburn_cost
    public float WaterburnSpeed = 750f;   // g_vehicle_racer_waterburn_speed
    public float WaterSpeedForward = 600f;// g_vehicle_racer_water_speed_forward
    public float WaterSpeedStrafe = 600f; // g_vehicle_racer_water_speed_strafe
    public float Friction = 0.45f;        // g_vehicle_racer_friction
    public float TurnSpeed = 220f;        // g_vehicle_racer_turnspeed
    public float TurnRoll = 30f;          // g_vehicle_racer_turnroll
    public float PitchSpeed = 125f;       // g_vehicle_racer_pitchspeed
    public float PitchLimit = 30f;        // g_vehicle_racer_pitchlimit
    public float HoverPower = 8000f;      // g_vehicle_racer_hoverpower (x4 engines)
    public float SpringLength = 90f;      // g_vehicle_racer_springlength
    public float UpForceDamper = 2f;      // g_vehicle_racer_upforcedamper
    public float WaterUpForceDamper = 15f;// g_vehicle_racer_water_upforcedamper
    public float AngleStabilizer = 1.75f; // g_vehicle_racer_anglestabilizer
    public float DownForce = 0.01f;       // g_vehicle_racer_downforce
    public float WaterDownForce = 0.03f;  // g_vehicle_racer_water_downforce
    public int HoverType = 0;             // g_vehicle_racer_hovertype (0=hover, !=0=maglev)
    public float ThinkRate = 0.05f;       // g_vehicle_racer_thinkrate
    public float WaterTime = 5f;          // g_vehicle_racer_water_time (submerged air-meter length)
    public float BounceFactor = 0.25f;    // g_vehicle_racer_bouncefactor (engine MOVETYPE_BOUNCE restitution)
    public float BounceStop = 0f;         // g_vehicle_racer_bouncestop (0 -> engine default 60/800)

    // ---- resources ----
    public float MaxEnergy = 100f;            // g_vehicle_racer_energy
    public float EnergyRegen = 90f;           // g_vehicle_racer_energy_regen
    public float EnergyRegenPause = 0.35f;    // g_vehicle_racer_energy_regen_pause
    public float MaxShield = 100f;            // g_vehicle_racer_shield
    public float ShieldRegen = 30f;           // g_vehicle_racer_shield_regen
    public float ShieldRegenPause = 1f;       // g_vehicle_racer_shield_regen_pause
    public float HealthRegen = 0f;            // g_vehicle_racer_health_regen
    public float RespawnTime = 35f;           // g_vehicle_racer_respawntime

    // ---- cannon (racer_weapon.qh) ----
    public float CannonCost = 1.5f;       // g_vehicle_racer_cannon_cost (energy/shot)
    public float CannonDamage = 15f;      // g_vehicle_racer_cannon_damage
    public float CannonRadius = 100f;     // g_vehicle_racer_cannon_radius
    public float CannonForce = 50f;       // g_vehicle_racer_cannon_force
    public float CannonSpeed = 15000f;    // g_vehicle_racer_cannon_speed
    public float CannonSpread = 0.0125f;  // g_vehicle_racer_cannon_spread
    public float CannonRefire = 0.05f;    // g_vehicle_racer_cannon_refire

    // ---- rocket (racer_weapon.qh) ----
    public float RocketDamage = 100f;     // g_vehicle_racer_rocket_damage
    public float RocketRadius = 125f;     // g_vehicle_racer_rocket_radius
    public float RocketForce = 350f;      // g_vehicle_racer_rocket_force
    public float RocketSpeed = 900f;      // g_vehicle_racer_rocket_speed
    public float RocketAccel = 1600f;     // g_vehicle_racer_rocket_accel
    public float RocketTurnRate = 0.2f;   // g_vehicle_racer_rocket_turnrate
    public float RocketRefire = 3f;       // g_vehicle_racer_rocket_refire
    public bool RocketLockTarget = true;  // g_vehicle_racer_rocket_locktarget
    public float RocketLockingTime = 0.35f;      // g_vehicle_racer_rocket_locking_time
    public float RocketLockingReleaseTime = 0.5f;// g_vehicle_racer_rocket_locking_releasetime
    public float RocketLockedTime = 4f;          // g_vehicle_racer_rocket_locked_time

    // ---- death blast (racer.qc) ----
    public float BlowupRadius = 250f;     // g_vehicle_racer_blowup_radius
    public float BlowupCoreDamage = 250f; // g_vehicle_racer_blowup_coredamage
    public float BlowupEdgeDamage = 15f;  // g_vehicle_racer_blowup_edgedamage
    public float BlowupForce = 250f;      // g_vehicle_racer_blowup_forceintensity

    // QC plays the engine loop and the boost on CH_TRIGGER_SINGLE; the port keeps them on two distinct single
    // channels (the boost loop on its own so the SND_Null stop on release doesn't kill the engine idle/move loop).
    private const SoundChannel EngineSoundChannel = SoundChannel.Body;   // racer_move / racer_idle loop
    private const SoundChannel BoostSoundChannel = SoundChannel.Weapon;  // racer_boost (QC on .tur_head)

    public Racer()
    {
        NetName = "racer";
        DisplayName = "Racer";
        Model = "models/vehicles/wakizashi.dpm";
        StartHealth = 200f;               // g_vehicle_racer_health
    }

    /// <summary>
    /// Honour server retune of the <c>g_vehicle_racer_*</c> autocvars (defined in vehicles.cfg). Each tunable is
    /// only overridden when the cvar reads back a sensible (non-zero) value, so an unloaded cvar store (e.g. unit
    /// tests, or a cfg that never ran) keeps the Base-default field values inlined above rather than zeroing them.
    /// Mirrors QC reading <c>autocvar_g_vehicle_racer_*</c> at vr_spawn/racer_frame time.
    /// </summary>
    private void LoadCvars()
    {
        if (Api.Services is null) return;
        // Presence-aware read: GetString returns "" for an unregistered/unset cvar (EngineServices.GetString),
        // so an explicit `set g_vehicle_racer_* 0` is honoured while an unloaded store keeps the Base default.
        float Get(string n, float def) => Api.Cvars.GetString(n).Length != 0 ? Api.Cvars.GetFloat(n) : def;
        bool IsSet(string n) => Api.Cvars.GetString(n).Length != 0;

        SpeedForward = Get("g_vehicle_racer_speed_forward", SpeedForward);
        SpeedStrafe = Get("g_vehicle_racer_speed_strafe", SpeedStrafe);
        SpeedAfterburn = Get("g_vehicle_racer_speed_afterburn", SpeedAfterburn);
        AfterburnCost = Get("g_vehicle_racer_afterburn_cost", AfterburnCost);
        WaterburnCost = Get("g_vehicle_racer_waterburn_cost", WaterburnCost);
        WaterburnSpeed = Get("g_vehicle_racer_waterburn_speed", WaterburnSpeed);
        WaterSpeedForward = Get("g_vehicle_racer_water_speed_forward", WaterSpeedForward);
        WaterSpeedStrafe = Get("g_vehicle_racer_water_speed_strafe", WaterSpeedStrafe);
        Friction = Get("g_vehicle_racer_friction", Friction);
        TurnSpeed = Get("g_vehicle_racer_turnspeed", TurnSpeed);
        TurnRoll = Get("g_vehicle_racer_turnroll", TurnRoll);
        PitchSpeed = Get("g_vehicle_racer_pitchspeed", PitchSpeed);
        PitchLimit = Get("g_vehicle_racer_pitchlimit", PitchLimit);
        HoverPower = Get("g_vehicle_racer_hoverpower", HoverPower);
        SpringLength = Get("g_vehicle_racer_springlength", SpringLength);
        UpForceDamper = Get("g_vehicle_racer_upforcedamper", UpForceDamper);
        WaterUpForceDamper = Get("g_vehicle_racer_water_upforcedamper", WaterUpForceDamper);
        AngleStabilizer = Get("g_vehicle_racer_anglestabilizer", AngleStabilizer);
        DownForce = Get("g_vehicle_racer_downforce", DownForce);
        WaterDownForce = Get("g_vehicle_racer_water_downforce", WaterDownForce);
        HoverType = (int)Get("g_vehicle_racer_hovertype", HoverType);
        ThinkRate = Get("g_vehicle_racer_thinkrate", ThinkRate);
        WaterTime = Get("g_vehicle_racer_water_time", WaterTime);
        BounceFactor = Get("g_vehicle_racer_bouncefactor", BounceFactor);
        BounceStop = Get("g_vehicle_racer_bouncestop", BounceStop); // presence-aware Get now honours an explicit 0

        MaxEnergy = Get("g_vehicle_racer_energy", MaxEnergy);
        EnergyRegen = Get("g_vehicle_racer_energy_regen", EnergyRegen);
        EnergyRegenPause = Get("g_vehicle_racer_energy_regen_pause", EnergyRegenPause);
        MaxShield = Get("g_vehicle_racer_shield", MaxShield);
        ShieldRegen = Get("g_vehicle_racer_shield_regen", ShieldRegen);
        ShieldRegenPause = Get("g_vehicle_racer_shield_regen_pause", ShieldRegenPause);
        StartHealth = Get("g_vehicle_racer_health", StartHealth);
        RespawnTime = Get("g_vehicle_racer_respawntime", RespawnTime);

        CannonCost = Get("g_vehicle_racer_cannon_cost", CannonCost);
        CannonDamage = Get("g_vehicle_racer_cannon_damage", CannonDamage);
        CannonRadius = Get("g_vehicle_racer_cannon_radius", CannonRadius);
        CannonForce = Get("g_vehicle_racer_cannon_force", CannonForce);
        CannonSpeed = Get("g_vehicle_racer_cannon_speed", CannonSpeed);
        CannonSpread = Get("g_vehicle_racer_cannon_spread", CannonSpread);
        CannonRefire = Get("g_vehicle_racer_cannon_refire", CannonRefire);

        RocketDamage = Get("g_vehicle_racer_rocket_damage", RocketDamage);
        RocketRadius = Get("g_vehicle_racer_rocket_radius", RocketRadius);
        RocketForce = Get("g_vehicle_racer_rocket_force", RocketForce);
        RocketSpeed = Get("g_vehicle_racer_rocket_speed", RocketSpeed);
        RocketAccel = Get("g_vehicle_racer_rocket_accel", RocketAccel);
        RocketTurnRate = Get("g_vehicle_racer_rocket_turnrate", RocketTurnRate);
        RocketRefire = Get("g_vehicle_racer_rocket_refire", RocketRefire);
        // RocketLockTarget defaults true (Base). The presence query distinguishes an explicit server override
        // (`g_vehicle_racer_rocket_locktarget 0/1`) from an unloaded store, so an explicit 0 now disables lock-on
        // while an unset store keeps the Base-default true.
        if (IsSet("g_vehicle_racer_rocket_locktarget"))
            RocketLockTarget = Api.Cvars.GetFloat("g_vehicle_racer_rocket_locktarget") != 0f;
        RocketLockingTime = Get("g_vehicle_racer_rocket_locking_time", RocketLockingTime);
        RocketLockingReleaseTime = Get("g_vehicle_racer_rocket_locking_releasetime", RocketLockingReleaseTime);
        RocketLockedTime = Get("g_vehicle_racer_rocket_locked_time", RocketLockedTime);

        BlowupRadius = Get("g_vehicle_racer_blowup_radius", BlowupRadius);
        BlowupCoreDamage = Get("g_vehicle_racer_blowup_coredamage", BlowupCoreDamage);
        BlowupEdgeDamage = Get("g_vehicle_racer_blowup_edgedamage", BlowupEdgeDamage);
        BlowupForce = Get("g_vehicle_racer_blowup_forceintensity", BlowupForce);
    }

    // METHOD(Racer, vr_spawn) — racer.qc
    public override void Spawn(Entity vehicle)
    {
        LoadCvars(); // honour server retune of the g_vehicle_racer_* tunables (else keep Base defaults)
        VehicleCommon.SpawnVehicle(vehicle, this);

        if (Model is not null && Api.Services is not null)
            Api.Entities.SetModel(vehicle, Model);

        vehicle.MaxHealth = StartHealth;
        vehicle.SetResourceExplicit(ResourceType.Health, StartHealth);
        vehicle.VehicleShield = MaxShield;
        vehicle.VehicleEnergy = MaxEnergy;
        vehicle.RespawnTime = RespawnTime;
        vehicle.MoveType = MoveType.Toss;     // QC: MOVETYPE_TOSS on spawn (becomes BOUNCE on enter)
        vehicle.Solid = Solid.SlideBox;
        vehicle.DeadState = DeadFlag.No;
        vehicle.ColorModKey = Vector3.Zero;   // QC vehicles_spawn: clear the vr_death '-0.5 -0.5 -0.5' darken on respawn
        // QC vr_spawn: instance.bouncefactor/bouncestop = g_vehicle_racer_bounce* (0.25 / 0). The engine
        // MOVETYPE_BOUNCE integrator reads these off the edict once the racer is entered (vr_enter -> BOUNCE).
        vehicle.BounceFactor = BounceFactor;
        vehicle.BounceStop = BounceStop;
        vehicle.Mass = 900f;                  // QC vr_spawn: instance.mass = 900 (scales blast knockback)
        vehicle.DamageForceScale = 0.5f;      // QC vr_spawn: instance.damageforcescale = 0.5
        // QC vr_spawn does NOT reset .touch — vehicles_spawn's this.touch = vehicles_touch stands, so the shared
        // crush / ram-impact (vr_impact) dispatch runs. (VehicleCommon.SpawnVehicle already wired vehicle.Touch.)
        vehicle.VehWeaponDelay = Time;
        vehicle.PlayTime = Time;              // QC: clear the impact debounce on (re)spawn

        // Hitbox: '-120 -120 -40' * 0.5 .. '120 120 40' * 0.5 (racer.qh).
        if (Api.Services is not null)
            Api.Entities.SetSize(vehicle, new Vector3(-60f, -60f, -20f), new Vector3(60f, 60f, 20f));

        // QC vr_setup: enable the regen capability flags this vehicle supports.
        vehicle.VehicleFlags |= VehicleFlags.HasShield | VehicleFlags.MoveHover | VehicleFlags.DmgShake | VehicleFlags.DmgRoll;
        if (EnergyRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.EnergyRegen;
        if (ShieldRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.ShieldRegen;
        if (HealthRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.HealthRegen;

        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;

        // TODO(port,client): qcsrc/common/vehicles/vehicle/racer.qc vr_spawn — scale-0.5 model hack,
        //                    hud/viewport tag attachment (cosmetic only; physics uses the real hitbox above).
    }

    // METHOD(Racer, vr_impact) — racer.qc: ram/landing self-damage.
    /// <summary>
    /// Port of <c>vr_impact</c>: on a hard touch the racer takes DEATH_FALL self-damage proportional to its
    /// speed change (bouncepain <c>'200 0.15 150'</c> = minspeed 200, factor 0.15, max 150), debounced 0.25s.
    /// Dispatched from the shared <c>vehicles_touch</c> path (<see cref="VehicleCommon.Touch"/>).
    /// </summary>
    public override void Impact(Entity vehicle)
    {
        VehicleCommon.Impact(vehicle, 200f, 0.15f, 150f);
    }

    // QC vr_enter: setorigin(instance.owner.flagcarried, '-190 0 96') — a boarding flag-carrier's flag parks
    // 190u BEHIND the cockpit (not the shared VEHICLE_FLAG_OFFSET '0 0 96'). Ctf.Tick reads this each tick.
    /// <summary>Racer-specific carried-flag cockpit offset (<c>'-190 0 96'</c>), per <c>vr_enter</c>.</summary>
    public override Vector3 FlagCarryOffset => new(-190f, 0f, 96f);

    // METHOD(Racer, vr_enter) — racer.qc
    public override void Enter(Entity vehicle, Entity player)
    {
        VehicleCommon.EnterVehicle(vehicle, player);
        vehicle.MoveType = MoveType.Bounce; // QC: set_movetype(instance, MOVETYPE_BOUNCE)

        // QC vr_enter: seed the on-foot HUD %-gauges from the vehicle's current resources.
        player.VehicleHealth = vehicle.GetResource(ResourceType.Health) / StartHealth * 100f;
        player.VehicleShield = vehicle.VehicleShield / MaxShield * 100f;
        player.VehicleEnergy = vehicle.VehicleEnergy / MaxEnergy * 100f;

        // QC vr_enter: a boarding CTF flag-carrier's flag is parked at '-190 0 96'. The carried-flag
        // reposition is owned by Ctf.Tick, which now anchors a carrier-in-a-vehicle's flag to the vehicle at
        // that cockpit offset each tick (player.Vehicle != null), so it rides the craft from this Enter on.

        // The Racer is driven by the player's per-frame physics plug (Frame), not its own think, while
        // occupied. Think still runs for regen/glue via the owner branch.
        vehicle.NextThink = Time;
        vehicle.Think = self => Think(self);
    }

    // void racer_exit(entity this, int eject) — racer.qc
    public override void Exit(Entity vehicle, Entity player)
    {
        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out _, out Vector3 up);
        Vector3 spot;

        bool dying = VehicleCommon.IsDead(vehicle);
        if (dying)
        {
            // Eject hard, up and forward (QC eject branch).
            spot = vehicle.Origin + forward * 100f + new Vector3(0, 0, 64f);
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
            player.Velocity = (up + forward * 0.25f) * 750f;
        }
        else
        {
            float maxAir = MaxAirSpeed();
            if (QMath.VLen(vehicle.Velocity) > 2f * maxAir)
            {
                player.Velocity = QMath.Normalize(vehicle.Velocity) * maxAir * 2f;
                player.Velocity += new Vector3(0, 0, 200f);
                spot = vehicle.Origin + forward * 32f + new Vector3(0, 0, 32f);
            }
            else
            {
                player.Velocity = vehicle.Velocity * 0.5f + new Vector3(0, 0, 10f);
                spot = vehicle.Origin - forward * 200f + new Vector3(0, 0, 32f);
            }
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
        }
        // QC racer_exit: owner.oldvelocity = owner.velocity (fall-damage negation on dismount).
        player.OldVelocity = player.Velocity;

        VehicleCommon.ExitVehicle(vehicle, player, dying ? VehicleExitFlag.Eject : VehicleExitFlag.Normal);
        if (Api.Services is not null)
            Api.Entities.SetOrigin(player, spot);

        // QC: the empty racer resumes its idle hover think.
        vehicle.MoveType = MoveType.Bounce;
        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;
    }

    // void racer_think(entity this) — racer.qc: the idle/empty hover. Also the regen + glue dispatcher.
    public override void Think(Entity vehicle)
    {
        float dt = ThinkRate;
        vehicle.NextThink = Time + dt;

        if (VehicleCommon.FreezeIfGameStopped(vehicle))
            return;

        // QC engine .oldvelocity = last-tick velocity; vr_impact (vehicles_impact) measures the touch speed-change
        // against it. Snapshot BEFORE this tick's physics so a touch dispatched this frame sees the real delta.
        vehicle.OldVelocity = vehicle.Velocity;

        Entity? player = vehicle.Owner;

        if (player is null)
        {
            // QC racer_think: a light spring + idle-bob keeps the empty craft hovering near the ground,
            // and the stabilizer eases pitch/roll back toward level.
            HoverIdle(vehicle, dt);
        }
        else if (!VehicleCommon.IsDead(vehicle))
        {
            // Piloted: run the full per-frame controller from the seated player's input.
            Frame(vehicle, player, vehicle.VehInput, dt);
        }

        // --- regen (shared) ---
        if ((vehicle.VehicleFlags & VehicleFlags.ShieldRegen) != 0)
            vehicle.VehicleShield = VehicleCommon.Regen(vehicle, vehicle.VehicleShield, vehicle.DmgTime,
                MaxShield, ShieldRegenPause, ShieldRegen, dt, healthScale: true);
        if ((vehicle.VehicleFlags & VehicleFlags.EnergyRegen) != 0)
            vehicle.VehicleEnergy = VehicleCommon.Regen(vehicle, vehicle.VehicleEnergy, vehicle.VehWait,
                MaxEnergy, EnergyRegenPause, EnergyRegen, dt, healthScale: false);
        if ((vehicle.VehicleFlags & VehicleFlags.HealthRegen) != 0)
            VehicleCommon.RegenResource(vehicle, vehicle.DmgTime, StartHealth, 0f, HealthRegen, dt, false, ResourceType.Health);

        if (player is not null)
        {
            // QC racer_frame VEHICLE_UPDATE_PLAYER: mirror the vehicle's resources onto the pilot as 0..100%
            // stats each tick for the on-foot HUD gauge (the authoritative values stay on the vehicle).
            player.VehicleHealth = vehicle.GetResource(ResourceType.Health) / StartHealth * 100f;
            player.VehicleEnergy = vehicle.VehicleEnergy / MaxEnergy * 100f;
            if ((vehicle.VehicleFlags & VehicleFlags.HasShield) != 0)
                player.VehicleShield = vehicle.VehicleShield / MaxShield * 100f;

            // QC: keep the seated player glued to the vehicle (origin + '0 0 32') and matching its velocity.
            if (Api.Services is not null)
                Api.Entities.SetOrigin(player, vehicle.Origin + new Vector3(0, 0, 32f));
            player.OldOrigin = player.Origin; // negate fall damage
            player.Velocity = vehicle.Velocity;
        }

        // QC vehicles_think: vehicles_painframe(this) runs after vr_think every tick — low-health smoke + jitter.
        VehicleCommon.PainFrame(vehicle);
    }

    /// <summary>
    /// Port of <c>racer_frame</c> (the SVQC controller half): 4-point engine-spring hover + align, yaw/roll/
    /// pitch toward the pilot's view, friction + downforce, afterburn, and both weapons. This is the deep
    /// behavior — invoked from <see cref="Think"/> with the seated player's input each tick.
    /// </summary>
    public void Frame(Entity vehicle, Entity player, in MovementInput input, float dt)
    {
        bool inLiquid = vehicle.WaterLevel > 0 || VehiclePhysics.InLiquid(vehicle.Origin);

        // QC racer_frame water-air timer: out of water clears racer_air_finished; the first tick submerged
        // arms the 5s air meter (time + water_time). Runs even while dead/parked (matches the QC order, before
        // the IS_DEAD early-out).
        if (!inLiquid)
            vehicle.VehAirFinished = 0f;
        else if (vehicle.VehAirFinished == 0f)
            vehicle.VehAirFinished = Time + WaterTime;

        if (VehicleCommon.IsDead(vehicle)) return;

        // --- 4-point engine spring (racer_align4point) -------------------------------------------------
        Align4Point(vehicle, input, dt, inLiquid);

        // --- angle controller: yaw/roll/pitch toward the pilot's aim (racer_frame) ---------------------
        Vector3 vAngle = input.ViewAngles;
        Vector3 ang = vehicle.Angles;
        ang.X = -ang.X; // QC flips pitch sign for the controller, flips back after

        // Yaw toward view yaw.
        float ftmp = TurnSpeed * dt;
        ftmp = QMath.Bound(-ftmp, VehiclePhysics.ShortAngle(vAngle.Y - ang.Y), ftmp);
        ang.Y = VehiclePhysics.AngleMods(ang.Y + ftmp);
        // Roll proportional to the yaw rate (QC turnroll).
        ang.Z += -ftmp * TurnRoll * dt;
        // Pitch toward view pitch, clamped.
        float pf = PitchSpeed * dt;
        pf = QMath.Bound(-pf, VehiclePhysics.ShortAngle(vAngle.X - ang.X), pf);
        ang.X = QMath.Bound(-PitchLimit, VehiclePhysics.AngleMods(ang.X + pf), PitchLimit);

        ang.X = -ang.X;
        vehicle.Angles = ang;

        // --- thrust (friction + wishmove + afterburn + downforce) --------------------------------------
        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 df = vehicle.Velocity * -Friction;

        Vector3 move = input.MoveValues;
        bool hasMove = move.X != 0f || move.Y != 0f;
        if (move.X != 0f)
        {
            float spd = inLiquid ? WaterSpeedForward : SpeedForward;
            df += forward * (move.X > 0f ? spd : -spd);
        }
        if (move.Y != 0f)
        {
            float spd = inLiquid ? WaterSpeedStrafe : SpeedStrafe;
            df += right * (move.Y > 0f ? spd : -spd);
        }

        // Engine-sound state machine (QC racer_frame, gated on .sound_nexttime): the move loop while the pilot
        // is feeding wishmove (sounds=1, 10.92s), else the idle loop (sounds=0, 11.89s). Re-emitting the same
        // loop on its (entity, channel) is idempotent on the facade, so this just swaps loops at the gate.
        if (Api.Services is not null)
        {
            if (hasMove)
            {
                if (vehicle.VehSoundNextTime < Time || vehicle.VehSoundState != 1)
                {
                    vehicle.VehSoundState = 1;
                    vehicle.VehSoundNextTime = Time + 10.922667f; // soundlength("vehicles/racer_move.wav")
                    Api.Sound.Play(vehicle, EngineSoundChannel, "vehicles/racer_move.wav", loop: true);
                }
            }
            else
            {
                if (vehicle.VehSoundNextTime < Time || vehicle.VehSoundState != 0)
                {
                    vehicle.VehSoundState = 0;
                    vehicle.VehSoundNextTime = Time + 11.888604f; // soundlength("vehicles/racer_idle.wav")
                    Api.Sound.Play(vehicle, EngineSoundChannel, "vehicles/racer_idle.wav", loop: true);
                }
            }
        }

        // Afterburn on jump (energy-gated). QC drains afterburn_cost/sec (or waterburn in liquid).
        if (input.ButtonJump && vehicle.VehicleEnergy >= AfterburnCost * dt)
        {
            // QC racer_frame: a booster puff (EFFECT_RACER_BOOSTER) gated on .wait every 0.2s, BEFORE .wait is
            // reset below — emitted behind the craft (origin - fwd*32) thrown forward at the current speed.
            if (Time - vehicle.VehWait > 0.2f)
                EffectEmitter.Emit("RACER_BOOSTER", vehicle.Origin - forward * 32f,
                    forward * QMath.VLen(vehicle.Velocity), 1);
            vehicle.VehWait = Time; // reset the energy regen-pause

            if (inLiquid)
            {
                vehicle.VehicleEnergy -= WaterburnCost * dt;
                df += forward * WaterburnSpeed;
            }
            else
            {
                vehicle.VehicleEnergy -= AfterburnCost * dt;
                df += forward * SpeedAfterburn;
            }

            // QC racer_frame: an under-craft smoke trail (EFFECT_SMOKE_SMALL), gated on .invincible_finished
            // (reused) for a 0.1+rand*0.1s cadence. Trace straight down 256u and puff at the ground hit.
            if (Api.Services is not null && vehicle.VehSmokeTime < Time)
            {
                TraceResult down = Api.Trace.Trace(vehicle.Origin, Vector3.Zero, Vector3.Zero,
                    vehicle.Origin - new Vector3(0, 0, 256f), MoveFilter.Normal, vehicle);
                if (down.Fraction < 1f)
                    EffectEmitter.Emit("SMOKE_SMALL", down.EndPos, Vector3.Zero, 1);
                vehicle.VehSmokeTime = Time + 0.1f + Prandom.Float() * 0.1f;
            }

            // QC: the boost sound is reused with .strength_finished as a 10.92s loop-length delay so it isn't
            // restarted every tick. Plays on the vehicle (no tur_head sub-entity in the port).
            if (Api.Services is not null && vehicle.VehBoostSoundTime < Time)
            {
                vehicle.VehBoostSoundTime = Time + 10.922667f; // soundlength("vehicles/racer_boost.wav")
                Api.Sound.Play(vehicle, BoostSoundChannel, "vehicles/racer_boost.wav");
            }
            vehicle.Effects |= VehicleEffects.Boosting; // networked: the client overlays the boost engine sound
        }
        else
        {
            // QC not-boosting branch: clear the boost-sound delay and issue SND_Null to stop the loop.
            vehicle.VehBoostSoundTime = 0f;
            if (Api.Services is not null)
                Api.Sound.Stop(vehicle, BoostSoundChannel);
            vehicle.Effects &= ~VehicleEffects.Boosting;
        }

        // Networked client-render flag: cannon firing → the client pops the muzzle flash.
        if (input.ButtonAttack1) vehicle.Effects |= VehicleEffects.Firing;
        else vehicle.Effects &= ~VehicleEffects.Firing;

        // QC racer_frame: stamp racer_watertime while in a liquid; the heavy water downforce then persists for
        // 3s AFTER leaving the water (a ramp-out), not just while submerged.
        if (inLiquid)
            vehicle.VehWaterTime = Time;
        float dforce = (Time - vehicle.VehWaterTime <= 3f) ? WaterDownForce : DownForce;
        df -= up * (QMath.VLen(vehicle.Velocity) * dforce);

        vehicle.Velocity += df * dt;

        // --- weapons -----------------------------------------------------------------------------------
        // Primary: rapid energy laser (refire-gated, energy-gated).
        if (input.ButtonAttack1 && vehicle.VehicleEnergy >= CannonCost && Time >= vehicle.VehAttackFinished)
        {
            vehicle.VehAttackFinished = Time + CannonRefire;
            FireCannon(vehicle, player, input.ViewAngles);
        }

        // Rocket lock-on (racer_frame): trace the crosshair and build/decay the lock.
        if (RocketLockTarget)
        {
            TraceResult tr = VehiclePhysics.CrosshairTrace(vehicle, input.ViewAngles, vehicle);
            VehiclePhysics.LockTarget(vehicle, tr.Ent,
                dt / RocketLockingTime, dt / RocketLockingReleaseTime, RocketLockedTime);
            // TODO(port,client): UpdateAuxiliaryXhair lock-color (red/green/blue) by lock_strength.
        }

        // Secondary: a pair of rockets (right then left), with the long refire after the pair.
        if (input.ButtonAttack2 && Time > vehicle.VehWeaponDelay)
        {
            ++vehicle.VehBulletCounter;
            vehicle.VehWeaponDelay = Time + 0.3f;
            bool locked = vehicle.VehLockStrength == 1f && vehicle.VehLockTarget is not null;
            Entity? targ = locked ? vehicle.VehLockTarget : null;

            if (vehicle.VehBulletCounter == 1)
            {
                FireRocket(vehicle, player, "tag_rocket_r", targ);
                player.VehicleAmmo2 = 50f; // QC: half the rocket pair spent
            }
            else if (vehicle.VehBulletCounter >= 2)
            {
                FireRocket(vehicle, player, "tag_rocket_l", targ);
                vehicle.VehLockStrength = 0f;
                vehicle.VehLockTarget = null;
                vehicle.VehBulletCounter = 0;
                vehicle.VehWeaponDelay = Time + RocketRefire;
                vehicle.VehReloadStart = Time; // QC .lip — start of the reload bar
                player.VehicleAmmo2 = 0f; // QC: both rockets spent
            }
        }
        else if (vehicle.VehBulletCounter == 0)
        {
            player.VehicleAmmo2 = 100f; // QC: idle, full secondary ammo
        }

        // QC racer_frame: the reload progress bar runs from .lip (pair-complete) to .delay (next allowed shot).
        // This is the player .vehicle_reload2 stat the on-foot vehicle HUD reads (NetGame -> VehicleHud.Reload2);
        // the same networked stat every other vehicle (Raptor/Bumblebee) drives, NOT the dead VehReload2 scratch.
        float reloadSpan = vehicle.VehWeaponDelay - vehicle.VehReloadStart;
        player.VehicleReload2 = reloadSpan > 0f
            ? QMath.Bound(0f, 100f * (Time - vehicle.VehReloadStart) / reloadSpan, 100f)
            : 100f;
    }

    /// <summary>Port of <c>racer_align4point</c>: four engine springs + the resulting upward push and pitch/roll torque.</summary>
    private void Align4Point(Entity vehicle, in MovementInput input, float dt, bool inLiquid)
    {
        bool maglev = HoverType != 0;
        var (frF, frP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_fr", SpringLength, HoverPower, maglev);
        var (flF, flP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_fl", SpringLength, HoverPower, maglev);
        var (brF, brP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_br", SpringLength, HoverPower, maglev);
        var (blF, blP) = VehiclePhysics.ForceFromTag(vehicle, "tag_engine_bl", SpringLength, HoverPower, maglev);

        Vector3 push = frF + flF + brF + blF;
        vehicle.Velocity += push * dt;

        float uforce = inLiquid ? WaterUpForceDamper : UpForceDamper;
        if (inLiquid)
        {
            // QC racer_align4point: while there's still air left (crouch + time < racer_air_finished) the
            // crouch-dive up-push is the gentle 30; once the 5s air meter has run out it reverts to 200.
            bool diving = input.ButtonCrouch && Time < vehicle.VehAirFinished;
            Vector3 v = vehicle.Velocity;
            v.Z += diving ? 30f : 200f;
            vehicle.Velocity = v;
        }

        // Anti-oscillation damper on the upward velocity.
        if (vehicle.Velocity.Z > 0f)
        {
            Vector3 v = vehicle.Velocity; v.Z *= 1f - uforce * dt; vehicle.Velocity = v;
        }

        // Differential of the four engine powers -> pitch (x) and roll (z) torque (QC * 360).
        float torqueX = ((flP - blP) + (frP - brP)) * 360f;
        float torqueZ = ((frP - flP) + (brP - blP)) * 360f;

        Vector3 ang = vehicle.Angles;
        ang.Z += torqueZ * dt;
        ang.X += torqueX * dt;
        // Stabilizer eases pitch/roll back toward level.
        ang.X *= 1f - AngleStabilizer * dt;
        ang.Z *= 1f - AngleStabilizer * dt;
        vehicle.Angles = ang;
    }

    /// <summary>QC racer_think idle hover spring (sin-bob altitude hold) + stabilizer for the empty craft.</summary>
    private void HoverIdle(Entity vehicle, float dt)
    {
        if (Api.Services is null) return;
        TraceResult tr = Api.Trace.Trace(vehicle.Origin, vehicle.Mins, vehicle.Maxs,
            vehicle.Origin - new Vector3(0, 0, SpringLength), MoveFilter.NoMonsters, vehicle);

        Vector3 df = vehicle.Velocity * -Friction;
        df.Z += (1f - tr.Fraction) * HoverPower + MathF.Sin(Time * 2f) * SpringLength * 2f;

        float forced = UpForceDamper;
        bool inLiquid = VehiclePhysics.InLiquid(vehicle.Origin - new Vector3(0, 0, 64f));
        if (inLiquid)
        {
            forced = WaterUpForceDamper;
            Vector3 vv = vehicle.Velocity; vv.Z += 200f; vehicle.Velocity = vv;
        }

        vehicle.Velocity += df * dt;
        if (vehicle.Velocity.Z > 0f)
        {
            Vector3 vv = vehicle.Velocity; vv.Z *= 1f - forced * dt; vehicle.Velocity = vv;
        }

        Vector3 ang = vehicle.Angles;
        ang.X *= 1f - AngleStabilizer * dt;
        ang.Z *= 1f - AngleStabilizer * dt;
        vehicle.Angles = ang;
    }

    // METHOD(Racer, vr_death) — racer.qc
    public override void Death(Entity vehicle)
    {
        vehicle.SetResourceExplicit(ResourceType.Health, 0f);
        vehicle.TakeDamage = DamageMode.No;
        vehicle.Solid = Solid.Corpse;
        vehicle.DeadState = DeadFlag.Dying;
        vehicle.MoveType = MoveType.Bounce;

        // QC vr_death: a medium explosion at the wreck and a colormod darken on the dying hull.
        EffectEmitter.Emit("EXPLOSION_MEDIUM", vehicle.Origin, Vector3.Zero, 1);
        vehicle.ColorModKey = new Vector3(-0.5f, -0.5f, -0.5f); // QC vr_death: instance.colormod = '-0.5 -0.5 -0.5'

        // QC racer_diethink: tumble (avelocity), then after a random delay racer_blowup detonates.
        float delay = Time + 2f + Prandom.Range(0f, 3f);
        vehicle.VehBulletCounter = 1 + (int)Prandom.Range(0f, 2f); // .cnt = deadtouch detonation countdown
        Vector3 av = vehicle.AVelocity;
        av.Z = Prandom.Float() < 0.5f ? 32f : -32f;
        av.X = -QMath.VLen(vehicle.Velocity) * 0.2f;
        vehicle.AVelocity = av;
        vehicle.Velocity += new Vector3(0, 0, 700f); // QC: pop up before the blast

        // racer_deadtouch: each bounce decrements the counter; hitting 0 detonates early.
        vehicle.Touch = (self, _) =>
        {
            Vector3 a = self.AVelocity; a.X *= 0.7f; self.AVelocity = a;
            if (--self.VehBulletCounter <= 0) Blowup(self);
        };

        vehicle.Think = self =>
        {
            self.NextThink = Time;
            if (Time >= delay) Blowup(self);
        };
        vehicle.NextThink = Time;
        // NOTE(port,net): setSendEntity(func_null) "stop networking the wreck" is a net-layer concern, unmodeled.
    }

    /// <summary>Port of <c>racer_blowup</c>: the delayed radius blast, then schedule respawn at the spawn point.</summary>
    private void Blowup(Entity vehicle)
    {
        vehicle.DeadState = DeadFlag.Dead;
        // The pilot was already ejected when health hit 0 (vehicles_damage). Eject any straggler defensively.
        if (vehicle.Owner is not null)
            VehicleCommon.ExitVehicle(vehicle, vehicle.Owner, VehicleExitFlag.Normal);

        // racer.qc racer_blowup: the death blast is DEATH_VH_WAKI_DEATH (the Racer is QC's "waki").
        WeaponSplash.RadiusDamage(vehicle, vehicle.Origin, BlowupCoreDamage, BlowupEdgeDamage,
            BlowupRadius, vehicle.Enemy, 0, BlowupForce, deathTag: DeathTypes.VhWakiDeath);

        vehicle.MoveType = MoveType.None;
        vehicle.Solid = Solid.Not;
        vehicle.Touch = null;
        vehicle.AVelocity = Vector3.Zero;
        vehicle.Velocity = Vector3.Zero;
        if (Api.Services is not null)
            Api.Entities.SetOrigin(vehicle, vehicle.SpawnPos);

        vehicle.NextThink = Time + RespawnTime;
        vehicle.Think = self => Spawn(self); // QC: setthink(vehicles_spawn) after respawntime
    }

    // ============================ WEAPONS ============================

    // METHOD(RacerAttack, wr_think) fire&1 — racer_weapon.qc: the rapid energy laser cannon.
    /// <summary>
    /// Fire the primary laser cannon (one energy bolt). On the PILOTED path (the only path the port drives)
    /// QC takes the shot from <c>W_SetupShot_Dir(player, v_forward)</c> — the player's aim — and does NOT
    /// alternate the muzzle tags (the <c>tag_fire1</c>/<c>tag_fire2</c> swap via <c>veh.cnt</c> is bot-only).
    /// </summary>
    public void FireCannon(Entity vehicle, Entity player, Vector3 viewAngles)
    {
        if (vehicle.VehicleEnergy < CannonCost) return; // wr_checkammo1
        vehicle.VehicleEnergy -= CannonCost;
        vehicle.VehWait = Time; // reset energy regen-pause

        // QC piloted W_SetupShot_Dir: shot direction is the player's view forward, fired from the cannon muzzle.
        QMath.AngleVectors(viewAngles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 org = VehiclePhysics.TagOrigin(vehicle, "tag_fire1", new Vector3(80f, 0f, 0f));

        // Spread cone around forward (deterministic PRNG, ADR-0010).
        Vector3 vel = Prandom.Spread(forward, right, up, CannonSpread) * CannonSpeed;

        VehicleCommon.SpawnProjectile(vehicle, player, org, vel,
            CannonDamage, CannonRadius, CannonForce, size: 0f,
            DeathTypes.VhWakiGun, health: 0f, lifetime: 0f, // racer_weapon.qc: DEATH_VH_WAKI_GUN
            fireSound: "vehicles/lasergun_fire.wav");
        // QC vehicles_projectile: Send_Effect(_mzlfx, proj.origin, proj.velocity, 1) — the muzzle flash at the
        // bolt's spawn point thrown along its velocity (CSQCProjectile bolt trail remains a client-render concern).
        EffectEmitter.Emit("RACER_MUZZLEFLASH", org, vel, 1);
    }

    // void racer_fire_rocket() — racer_weapon.qc: the secondary rocket (guided when locked, else ground-hugging).
    /// <summary>Fire one secondary rocket from <paramref name="tag"/>; homes on <paramref name="targ"/> when locked, else hugs terrain.</summary>
    public void FireRocket(Entity vehicle, Entity player, string tag, Entity? targ)
    {
        var (org, fwd) = VehiclePhysics.TagOriginForward(vehicle, tag);
        Vector3 vel = fwd * RocketSpeed;

        Entity rocket = VehicleCommon.SpawnProjectile(vehicle, player, org, vel,
            RocketDamage, RocketRadius, RocketForce, size: 3f,
            DeathTypes.VhWakiRocket, health: 20f, lifetime: 15f, // racer_weapon.qc: DEATH_VH_WAKI_ROCKET
            fireSound: "vehicles/rocket_fire.wav");
        rocket.ClassName = "racer_rocket";

        // QC vehicles_projectile: Send_Effect(EFFECT_RACER_ROCKETLAUNCH, proj.origin, proj.velocity, 1) — the
        // launch flash (the PROJECTILE_WAKIROCKET CSQC rocket trail remains a client-render concern).
        EffectEmitter.Emit("RACER_ROCKETLAUNCH", org, vel, 1);

        // Guidance parameters (racer_fire_rocket): per-tick accel, turn rate, lifetime, target.
        rocket.VehProjAccel = RocketAccel * FrameTime;
        rocket.VehProjTurnRate = RocketTurnRate;
        rocket.VehProjExpire = Time + 15f;
        rocket.Enemy = targ;
        rocket.VehGuideMode = (int)(targ is not null
            ? VehiclePhysics.GuideMode.RacerHoming
            : VehiclePhysics.GuideMode.RacerGroundHug);

        // Drive the per-tick guidance off the projectile's own think (replacing the dumb fly-straight think).
        rocket.Think = self =>
        {
            self.NextThink = Time;
            var mode = (VehiclePhysics.GuideMode)self.VehGuideMode;
            if (!VehiclePhysics.GuideRocket(self, mode, FrameTime, crosshair: null))
            {
                // QC racer_rocket_tracker/groundhugger use() -> explode: detonate via the shared projectile
                // blast, tagged DEATH_VH_WAKI_ROCKET so an owner-death / 15s-timeout kill is attributed to the
                // rocket (matches the touch-detonation path's Explode deathtype), not a generic weapon id.
                WeaponSplash.RadiusDamage(self, self.Origin, RocketDamage, 0f, RocketRadius,
                    self.DmgInflictor, RegistryId, RocketForce, deathTag: DeathTypes.VhWakiRocket);
                Api.Entities.Remove(self);
            }
        };
        rocket.NextThink = Time;
    }

    private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;
    private static float FrameTime => Api.Services is not null ? Api.Clock.FrameTime : 0.05f;
    private static float MaxAirSpeed()
    {
        if (Api.Services is null) return 400f;
        float v = Api.Cvars.GetFloat("sv_maxairspeed");
        return v != 0f ? v : 400f;
    }
}

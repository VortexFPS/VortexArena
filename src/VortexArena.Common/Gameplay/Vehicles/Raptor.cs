// Port: qcsrc/common/vehicles/vehicle/raptor.{qh,qc} + raptor_weapons.{qh,qc}
//
// The Raptor — a single-seat VTOL gunship. It takes off vertically (raptor_takeoff), then flies free
// (raptor_frame, an avelocity-based pitch/roll/yaw controller); the pilot operates twin laser cannons
// (primary, alternating gun1/gun2) and switches the secondary between cluster bombs (which burst into N
// independent bomblets) and decoy flares (which seduce incoming guided missiles). Balance values are the
// cfg defaults inlined below (g_vehicle_raptor_*).
//
// FULL deep behavior is implemented: the avelocity flight controller, the takeoff/landing state machines,
// the cannon lock/predict + turret aim of both guns, the bomb cluster split (raptor_bomb_burst spawning
// real bomblets with spread + delayed explosions), and the flare decoys (raptor_flare_think re-targeting
// nearby guided rockets). Only client-only items (muzzle/rotor FX, dropmark crosshair, HUD %) stay TODO.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The raptor's secondary fire mode (QC RSM_* — raptor.qh).</summary>
public enum RaptorMode { Bomb = 1, Flare = 2 }

/// <summary>
/// The Raptor vehicle (QC <c>CLASS(Raptor, Vehicle)</c>). Registered via <see cref="VehicleAttribute"/>.
/// </summary>
[Vehicle]
public sealed class Raptor : Vehicle
{
    // ---- chassis (raptor.qc autocvars) ----
    public int MoveStyle = 1;           // g_vehicle_raptor_movestyle (1 = level flight)
    public float SpeedForward = 1700f;  // g_vehicle_raptor_speed_forward
    public float SpeedStrafe = 2200f;   // g_vehicle_raptor_speed_strafe
    public float SpeedUp = 2300f;       // g_vehicle_raptor_speed_up
    public float SpeedDown = 2000f;     // g_vehicle_raptor_speed_down
    public float Friction = 2f;         // g_vehicle_raptor_friction
    public float TurnSpeed = 200f;      // g_vehicle_raptor_turnspeed
    public float PitchSpeed = 50f;      // g_vehicle_raptor_pitchspeed
    public float PitchLimit = 45f;      // g_vehicle_raptor_pitchlimit
    public float TakeoffTime = 1.5f;    // g_vehicle_raptor_takefftime
    public float ThinkRate = VehicleCommon.DefaultThinkRate;

    // ---- cannon aim/lock (raptor.qc) ----
    public float CannonTurnSpeed = 120f;       // g_vehicle_raptor_cannon_turnspeed
    public float CannonTurnLimit = 20f;        // g_vehicle_raptor_cannon_turnlimit
    public float CannonPitchLimitUp = 12f;     // g_vehicle_raptor_cannon_pitchlimit_up
    public float CannonPitchLimitDown = 32f;   // g_vehicle_raptor_cannon_pitchlimit_down
    public int CannonLockTarget = 1;           // g_vehicle_raptor_cannon_locktarget
    public float CannonLockingTime = 0.2f;     // g_vehicle_raptor_cannon_locking_time
    public float CannonLockingReleaseTime = 0.45f; // g_vehicle_raptor_cannon_locking_releasetime
    public float CannonLockedTime = 1f;        // g_vehicle_raptor_cannon_locked_time
    public bool CannonPredictTarget = true;    // g_vehicle_raptor_cannon_predicttarget

    // ---- resources ----
    public float MaxEnergy = 100f;          // g_vehicle_raptor_energy
    public float EnergyRegen = 25f;         // g_vehicle_raptor_energy_regen
    public float EnergyRegenPause = 0.25f;  // g_vehicle_raptor_energy_regen_pause
    public float MaxShield = 200f;          // g_vehicle_raptor_shield
    public float ShieldRegen = 25f;         // g_vehicle_raptor_shield_regen
    public float ShieldRegenPause = 1.5f;   // g_vehicle_raptor_shield_regen_pause
    public float HealthRegen = 0f;          // g_vehicle_raptor_health_regen
    public float RespawnTime = 40f;         // g_vehicle_raptor_respawntime

    // ---- cannon (raptor_weapons.qh) ----
    public float CannonCost = 1f;        // g_vehicle_raptor_cannon_cost
    public float CannonDamage = 10f;     // g_vehicle_raptor_cannon_damage
    public float CannonRadius = 60f;     // g_vehicle_raptor_cannon_radius
    public float CannonForce = 25f;      // g_vehicle_raptor_cannon_force
    public float CannonSpeed = 24000f;   // g_vehicle_raptor_cannon_speed
    public float CannonSpread = 0.01f;   // g_vehicle_raptor_cannon_spread
    public float CannonRefire = 0.033333f; // g_vehicle_raptor_cannon_refire

    // ---- bombs (raptor_weapons.qh) ----
    public int Bomblets = 8;              // g_vehicle_raptor_bomblets
    public float BombletDamage = 55f;     // g_vehicle_raptor_bomblet_damage
    public float BombletEdgeDamage = 25f; // g_vehicle_raptor_bomblet_edgedamage
    public float BombletRadius = 350f;    // g_vehicle_raptor_bomblet_radius
    public float BombletForce = 150f;     // g_vehicle_raptor_bomblet_force
    public float BombletSpread = 0.4f;    // g_vehicle_raptor_bomblet_spread
    public float BombletExplodeDelay = 0.4f; // g_vehicle_raptor_bomblet_explode_delay
    public float BombletTime = 0.5f;      // g_vehicle_raptor_bomblet_time (fall time before burst)
    public float BombletAlt = 750f;       // g_vehicle_raptor_bomblet_alt (clear-fall early-burst distance)
    public float BombsRefire = 5f;        // g_vehicle_raptor_bombs_refire

    // ---- bounce-missile chassis tuning (raptor.qc autocvars) ----
    public float BounceFactor = 0.2f;     // g_vehicle_raptor_bouncefactor
    public float BounceStop = 0f;         // g_vehicle_raptor_bouncestop
    // g_vehicle_raptor_bouncepain = '1 4 1000' (minspeed, damagemultiplier, maxpain) — applied in Impact.

    // ---- flares (raptor_weapons.qh) ----
    public float FlareRefire = 5f;        // g_vehicle_raptor_flare_refire
    public float FlareLifetime = 10f;     // g_vehicle_raptor_flare_lifetime
    public float FlareChase = 0.9f;       // g_vehicle_raptor_flare_chase
    public float FlareRange = 2000f;      // g_vehicle_raptor_flare_range

    public Raptor()
    {
        NetName = "raptor";
        DisplayName = "Raptor";
        Model = "models/vehicles/raptor.dpm";
        StartHealth = 250f;               // g_vehicle_raptor_health
    }

    // METHOD(Raptor, vr_spawn) — raptor.qc
    public override void Spawn(Entity vehicle)
    {
        VehicleCommon.SpawnVehicle(vehicle, this);

        if (Model is not null && Api.Services is not null)
            Api.Entities.SetModel(vehicle, Model);

        // Sub-entities: the two cannon barrels (gun1/gun2) the aim controller slews. Created once.
        vehicle.VehGun1 ??= NewGun(vehicle);
        vehicle.VehGun2 ??= NewGun(vehicle);

        vehicle.MaxHealth = StartHealth;
        vehicle.SetResourceExplicit(ResourceType.Health, StartHealth);
        vehicle.VehicleShield = MaxShield;
        vehicle.VehicleEnergy = 1f; // QC seeds energy at 1, regens up
        vehicle.RespawnTime = RespawnTime;
        vehicle.MoveType = MoveType.Toss;   // QC: MOVETYPE_TOSS on spawn
        vehicle.Solid = Solid.SlideBox;
        vehicle.DeadState = DeadFlag.No;
        vehicle.DamageForceScale = 0.25f;   // QC vr_spawn: instance.damageforcescale = 0.25
        // QC vr_spawn bounce-missile physics tuning (mass 1 / bouncefactor 0.2 / bouncestop 0): the chassis
        // rebounds softly off geometry under MOVETYPE_BOUNCEMISSILE while in flight.
        vehicle.Mass = 1f;
        vehicle.BounceFactor = BounceFactor;
        vehicle.BounceStop = BounceStop;
        vehicle.ColorModKey = Vector3.Zero; // QC raptor_blowup leaves colormod tinted; a fresh spawn clears it
        // QC: if (!autocvar_g_vehicle_raptor_swim) dphitcontentsmask |= DPCONTENTS_LIQUIDSMASK — so the raptor
        // sinks in water (swim default off) instead of treating liquid as empty space.
        if (Api.Cvars is null || Api.Cvars.GetFloat("g_vehicle_raptor_swim") == 0f)
            vehicle.DpHitContentsMask |= SuperContentsLiquidsMask;
        vehicle.VehAnimFrame = 0f;
        vehicle.VehW2Mode = (int)RaptorMode.Bomb;
        vehicle.VehWeaponDelay = Time;
        vehicle.VehReloadStart = Time;
        vehicle.PlayTime = Time; // QC: clear the impact debounce on (re)spawn (play_time)
        // QC vr_spawn does NOT reset .touch — vehicles_spawn's this.touch = vehicles_touch stands, so the shared
        // crush / ram-impact (vr_impact) dispatch runs. (VehicleCommon.SpawnVehicle already wired vehicle.Touch.)

        // Hitbox: '-80 -80 0' .. '80 80 70' (raptor.qh).
        if (Api.Services is not null)
            Api.Entities.SetSize(vehicle, new Vector3(-80f, -80f, 0f), new Vector3(80f, 80f, 70f));

        vehicle.VehicleFlags |= VehicleFlags.HasShield | VehicleFlags.MoveFly | VehicleFlags.DmgShake | VehicleFlags.DmgRoll;
        if (EnergyRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.EnergyRegen;
        if (ShieldRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.ShieldRegen;
        if (HealthRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.HealthRegen;

        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;

        // TODO(port,client): qcsrc/common/vehicles/vehicle/raptor.qc vr_spawn — rotor spinner entities +
        //                    raptor_rotor_anglefix, raptor_bomb/tail cosmetic models (visual only).
    }

    // METHOD(Raptor, vr_enter) — raptor.qc
    public override void Enter(Entity vehicle, Entity player)
    {
        VehicleCommon.EnterVehicle(vehicle, player);
        vehicle.MoveType = MoveType.BounceMissile; // QC: MOVETYPE_BOUNCEMISSILE
        vehicle.Solid = Solid.SlideBox;
        vehicle.Velocity = new Vector3(0, 0, 1f);  // QC: nudge up so the takeoff sequence can start
        vehicle.VehW2Mode = (int)RaptorMode.Bomb;  // QC: STAT(VEHICLESTAT_W2MODE) = RSM_BOMB
        vehicle.VehAnimFrame = 0f;
        vehicle.VehWeaponDelay = Time + BombsRefire;
        vehicle.VehReloadStart = Time;

        // QC installs raptor_takeoff first (vertical takeoff), then hands off to raptor_frame at frame 25.
        vehicle.VehSoundState = 0; // 0 = in takeoff phase, 1 = flying
        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;
    }

    // void raptor_exit(entity this, int eject) — raptor.qc
    public override void Exit(Entity vehicle, Entity player)
    {
        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out _, out Vector3 up);
        Vector3 spot;
        bool dying = VehicleCommon.IsDead(vehicle);
        if (dying)
        {
            spot = vehicle.Origin + forward * 100f + new Vector3(0, 0, 64f);
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
            player.Velocity = (up + forward * 0.25f) * 750f;
        }
        else
        {
            float maxAir = MaxAirSpeed();
            if (QMath.VLen(vehicle.Velocity) > 2f * maxAir)
            {
                player.Velocity = QMath.Normalize(vehicle.Velocity) * maxAir * 2f + new Vector3(0, 0, 200f);
                spot = vehicle.Origin + forward * 32f + new Vector3(0, 0, 64f);
            }
            else
            {
                player.Velocity = vehicle.Velocity * 0.5f + new Vector3(0, 0, 10f);
                spot = vehicle.Origin - forward * 200f + new Vector3(0, 0, 64f);
            }
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
        }

        VehicleCommon.ExitVehicle(vehicle, player, dying ? VehicleExitFlag.Eject : VehicleExitFlag.Normal);
        if (Api.Services is not null)
            Api.Entities.SetOrigin(player, spot);

        // QC: a living raptor auto-lands (raptor_land think) once the pilot leaves.
        if (!dying)
        {
            vehicle.MoveType = MoveType.Toss;
            vehicle.Think = self => Land(self);
            vehicle.NextThink = Time;
        }
    }

    // raptor_takeoff + raptor_frame dispatcher — raptor.qc.
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

        if (player is not null && !VehicleCommon.IsDead(vehicle))
        {
            if (vehicle.VehSoundState == 0)
                Takeoff(vehicle, player, dt);
            else
                Frame(vehicle, player, vehicle.VehInput, dt);
        }
        // An empty raptor that hasn't landed just coasts under MOVETYPE_TOSS gravity (engine-integrated).

        if ((vehicle.VehicleFlags & VehicleFlags.ShieldRegen) != 0)
            vehicle.VehicleShield = VehicleCommon.Regen(vehicle, vehicle.VehicleShield, vehicle.DmgTime,
                MaxShield, ShieldRegenPause, ShieldRegen, dt, healthScale: true);
        if ((vehicle.VehicleFlags & VehicleFlags.EnergyRegen) != 0)
            vehicle.VehicleEnergy = VehicleCommon.Regen(vehicle, vehicle.VehicleEnergy, vehicle.VehRegenPauseTime,
                MaxEnergy, EnergyRegenPause, EnergyRegen, dt, healthScale: false);
        if ((vehicle.VehicleFlags & VehicleFlags.HealthRegen) != 0)
            VehicleCommon.RegenResource(vehicle, vehicle.DmgTime, StartHealth, 0f, HealthRegen, dt, false, ResourceType.Health);

        if (player is not null)
        {
            if (Api.Services is not null)
                Api.Entities.SetOrigin(player, vehicle.Origin + new Vector3(0, 0, 32f));
            player.OldOrigin = player.Origin;
            player.Velocity = vehicle.Velocity;

            // QC vehicles_regen mirrors owner.(regen_field) = (pool/max)*100 onto the pilot so the in-vehicle
            // HUD shows the live shield/energy gauges (RegenResource already mirrors health). Without these the
            // raptor's HUD shield + energy bars read stale/zero. Mirror every tick (matching the Racer/Bumblebee
            // pilot mirror) so the gauges track even between regen pulses.
            player.VehicleShield = vehicle.VehicleShield / MaxShield * 100f;
            player.VehicleEnergy = vehicle.VehicleEnergy / MaxEnergy * 100f;

            // QC raptor_frame/raptor_takeoff (raptor.qc:401-403, 477-478): the secondary reload bar is the bomb
            // alpha (time - lip) / (delay - lip), where lip = VehReloadStart (reset on fire) and delay =
            // VehWeaponDelay (= lip + refire). It ramps 0->1 as the weapon reloads, reaching 100 when ready;
            // vehicle_ammo2 is 100 only at full reload. The bomb dropmark crosshair only predicts when reload2 == 100.
            float span = vehicle.VehWeaponDelay - vehicle.VehReloadStart;
            float reloadAlpha = span > 0f ? (Time - vehicle.VehReloadStart) / span : 1f;
            player.VehicleReload2 = MathF.Min(MathF.Max(reloadAlpha * 100f, 0f), 100f);
            player.VehicleAmmo2 = player.VehicleReload2 >= 100f ? 100f : 0f;
        }

        // QC vehicles_think: vehicles_painframe(this) runs after vr_think every tick — low-health smoke + jitter.
        VehicleCommon.PainFrame(vehicle);
    }

    /// <summary>Port of <c>raptor_takeoff</c>: rise vertically while the animation frame ramps 0→25, then hand off to flight.</summary>
    private void Takeoff(Entity vehicle, Entity player, float dt)
    {
        // QC raptor_takeoff plays SND_VEH_RAPTOR_SPEED on CH_TRIGGER_SINGLE every 7.955812s, sharing the same
        // vehic.sound_nexttime gate as raptor_frame so the engine note runs continuously through the handoff.
        if (Api.Services is not null && vehicle.VehSoundNextTime < Time)
        {
            vehicle.VehSoundNextTime = Time + 7.955812f; // soundlength("vehicles/raptor_speed.wav")
            Api.Sound.Play(vehicle, SoundChannel.Item, "vehicles/raptor_speed.wav"); // CH_TRIGGER_SINGLE
        }

        if (vehicle.VehAnimFrame < 25f)
        {
            vehicle.VehAnimFrame += 25f * dt / TakeoffTime;
            Vector3 v = vehicle.Velocity;
            v.Z = MathF.Min(v.Z * 1.5f, 256f);
            vehicle.Velocity = v;
        }
        else
        {
            vehicle.VehSoundState = 1; // QC: this.PlayerPhysplug = raptor_frame
        }

        // QC raptor_takeoff zeroes ATCK/ATCK2/CROUCH every takeoff tick so pilot input can't fire/dive while
        // the airframe is still rising — clear them on the cached VehInput the flight handoff tick will read.
        MovementInput vi = vehicle.VehInput;
        vi.ButtonAttack1 = false; vi.ButtonAttack2 = false; vi.ButtonCrouch = false;
        vehicle.VehInput = vi;
    }

    /// <summary>
    /// Port of <c>raptor_frame</c> (the SVQC half): the avelocity-based pitch/yaw flight controller toward
    /// the pilot's aim, roll on strafe, yaw-relative thrust, vertical jump/crouch climb, the twin-cannon
    /// turret aim + lock/predict, and the bomb/flare secondary.
    /// </summary>
    public void Frame(Entity vehicle, Entity player, in MovementInput input, float dt)
    {
        if (VehicleCommon.IsDead(vehicle)) return;

        Vector3 move = input.MoveValues;
        Vector3 vAng = input.ViewAngles;

        // Crosshair aim point (drives both the turret aim and the avelocity controller).
        TraceResult tr = VehiclePhysics.CrosshairTrace(vehicle, vAng, vehicle);
        Vector3 aimPoint = tr.EndPos;

        // Hover-flip guard (QC): a hard-rolled raptor turns jump into descend.
        bool jump = input.ButtonJump, crouch = input.ButtonCrouch;
        if ((vehicle.Angles.Z > 50f || vehicle.Angles.Z < -50f) && jump) { crouch = true; jump = false; }

        // --- avelocity yaw/pitch controller (raptor_frame) ---------------------------------------------
        Vector3 vang = vehicle.Angles; vang.X = -vang.X;
        Vector3 df = QMath.FixedVecToAngles(QMath.Normalize(aimPoint - vehicle.Origin + new Vector3(0, 0, 32f)));
        df = VehiclePhysics.ShortAngles(df);

        // Yaw toward view yaw (smoothed avelocity).
        float yawErr = VehiclePhysics.ShortAngle(vAng.Y - vang.Y);
        Vector3 av = vehicle.AVelocity;
        av.Y = QMath.Bound(-TurnSpeed, yawErr + av.Y * 0.9f, TurnSpeed);

        // Pitch: a small bias from forward/back input plus the aim pitch, clamped to the pitch limit.
        float pbias = 0f;
        if (move.X > 0f && vang.X < PitchLimit) pbias = 5f;
        else if (move.X < 0f && vang.X > -PitchLimit) pbias = -20f;
        float dfx = QMath.Bound(-PitchLimit, df.X, PitchLimit);
        float pitchErr = vang.X - QMath.Bound(-PitchLimit, dfx + pbias, PitchLimit);
        av.X = QMath.Bound(-PitchSpeed, pitchErr + av.X * 0.9f, PitchSpeed);
        vehicle.AVelocity = av;

        Vector3 a = vehicle.Angles;
        a.X = VehiclePhysics.AngleMods(a.X); a.Y = VehiclePhysics.AngleMods(a.Y); a.Z = VehiclePhysics.AngleMods(a.Z);
        vehicle.Angles = a;

        // --- thrust (yaw-relative in movestyle 1) ------------------------------------------------------
        Vector3 basisAng = MoveStyle == 1 ? new Vector3(0f, vehicle.Angles.Y, 0f) : vAng;
        QMath.AngleVectors(basisAng, out Vector3 forward, out Vector3 right, out Vector3 up);

        Vector3 nv = vehicle.Velocity * -Friction;
        if (move.X != 0f) nv += forward * (move.X > 0f ? SpeedForward : -SpeedForward);
        if (move.Y != 0f)
        {
            nv += right * (move.Y > 0f ? SpeedStrafe : -SpeedStrafe);
            a = vehicle.Angles;
            a.Z = QMath.Bound(-30f, a.Z + move.Y / SpeedStrafe, 30f); // roll on strafe
            vehicle.Angles = a;
        }
        else
        {
            a = vehicle.Angles; a.Z *= 0.95f; vehicle.Angles = a;
        }
        if (crouch) nv -= up * SpeedDown;
        else if (jump) nv += up * SpeedUp;

        vehicle.Velocity += nv * dt;

        // --- twin cannon: lock/predict + turret aim of gun1/gun2 ---------------------------------------
        Vector3 aimAt = aimPoint;
        if (CannonLockTarget == 1)
        {
            VehiclePhysics.LockTarget(vehicle, tr.Ent,
                dt / CannonLockingTime, dt / CannonLockingReleaseTime, CannonLockedTime);
            if (vehicle.VehLockTarget is not null && CannonPredictTarget && vehicle.VehLockStrength == 1f)
                aimAt = PredictAim(vehicle, vehicle.VehLockTarget);
        }

        if (vehicle.VehGun1 is not null)
            VehiclePhysics.AimTurret(vehicle, aimAt, vehicle.VehGun1, "fire1",
                -CannonPitchLimitDown, CannonPitchLimitUp, -CannonTurnLimit, CannonTurnLimit, CannonTurnSpeed, dt);
        if (vehicle.VehGun2 is not null)
            VehiclePhysics.AimTurret(vehicle, aimAt, vehicle.VehGun2, "fire1",
                -CannonPitchLimitDown, CannonPitchLimitUp, -CannonTurnLimit, CannonTurnLimit, CannonTurnSpeed, dt);

        // --- primary fire: twin cannon (the 1-1-2-2 cadence) -------------------------------------------
        // QC: refire is doubled at the end of the 4-shot pattern.
        float refire = CannonRefire * (1f + ((vehicle.VehBulletCounter + 1) >= 4 ? 1f : 0f));
        if (input.ButtonAttack1 && vehicle.VehicleEnergy >= CannonCost && Time >= vehicle.VehAttackFinished)
        {
            vehicle.VehAttackFinished = Time + refire;
            FireCannon(vehicle, player);
        }

        // --- secondary fire: bomb or flare -------------------------------------------------------------
        if (input.ButtonAttack2 && Time > vehicle.VehReloadStart + (vehicle.VehW2Mode == (int)RaptorMode.Bomb ? BombsRefire : FlareRefire))
        {
            if (vehicle.VehW2Mode == (int)RaptorMode.Bomb)
                DropBombs(vehicle, player);
            else
                FireFlares(vehicle, player, vAng);
            float rf = vehicle.VehW2Mode == (int)RaptorMode.Bomb ? BombsRefire : FlareRefire;
            vehicle.VehWeaponDelay = Time + rf;
            vehicle.VehReloadStart = Time;
        }

        // --- engine fly sound (raptor_frame): SND_VEH_RAPTOR_SPEED looped on CH_TRIGGER_SINGLE every 7.955812s.
        // QC gates this on vehic.sound_nexttime (the soundlength of vehicles/raptor_speed.wav); same gate the
        // takeoff phase uses, so the engine note continues seamlessly from takeoff into free flight.
        if (Api.Services is not null && vehicle.VehSoundNextTime < Time)
        {
            vehicle.VehSoundNextTime = Time + 7.955812f; // soundlength("vehicles/raptor_speed.wav")
            Api.Sound.Play(vehicle, SoundChannel.Item, "vehicles/raptor_speed.wav"); // CH_TRIGGER_SINGLE
        }

        // --- incoming-missile alarm (raptor_frame): a guided rocket tracking us within range -----------
        // QC gates this on .bomb1.cnt (once per second), DISTINCT from the engine-sound gate (.sound_nexttime).
        if (Api.Services is not null && vehicle.VehAlarmNextTime < Time)
        {
            foreach (Entity proj in Api.Entities.FindByClass("vehicles_projectile"))
            {
                // QC scans g_projectiles for .enemy == vehic, then requires MIF_GUIDED_TRACKING (VehGuideMode>=0
                // models the guided-tracking flag) within 2*flare_range.
                if (proj.Enemy == vehicle && proj.VehGuideMode >= 0
                    && QMath.VLen(vehicle.Origin - proj.Origin) < 2f * FlareRange)
                {
                    // QC: soundto(MSG_ONE, vehic, CH_PAIN_SINGLE, SND(VEH_MISSILE_ALARM), VOL_BASE, ATTEN_NONE).
                    // SND_VEH_MISSILE_ALARM = "vehicles/missile_alarm.wav" (NOT vehicles/alarm.wav = SND_VEH_ALARM).
                    Api.Sound.Play(vehicle, SoundChannel.Pain, "vehicles/missile_alarm.wav", SoundLevels.VolBase, SoundLevels.AttenNone);
                    break;
                }
            }
            vehicle.VehAlarmNextTime = Time + 1f;
        }

        // TODO(port,client): qcsrc/common/vehicles/vehicle/raptor.qc raptor_frame —
        //                    vehicle_ammo2/reload2 HUD %, dropmark aux crosshair, aux lock crosshair color.
    }

    /// <summary>QC cannon predict: iterate the impact-time lead solve toward the locked target.</summary>
    private Vector3 PredictAim(Entity vehicle, Entity target)
    {
        Vector3 vf = target.Origin;
        Vector3 ad = vf;
        for (int i = 0; i < 4; ++i)
        {
            float distance = QMath.VLen(ad - vehicle.Origin);
            float impactTime = distance / CannonSpeed;
            ad = vf + target.Velocity * impactTime;
        }
        return ad;
    }

    // void raptor_land(entity this) — raptor.qc: settle to the ground after the pilot exits.
    private void Land(Entity vehicle)
    {
        if (Api.Services is null) { vehicle.NextThink = 0f; return; }
        float hgt = VehiclePhysics.Altitude(vehicle, 512f);

        vehicle.Velocity = vehicle.Velocity * 0.9f + new Vector3(0, 0, -1800f) * (hgt / 256f) * FrameTime;
        Vector3 a = vehicle.Angles; a.X *= 0.95f; a.Z *= 0.95f; vehicle.Angles = a;

        // QC raptor_land: as the raptor descends below 128u the animation frame ramps with altitude
        // (drives the visible model + the rotor avelocity on the client; tracked server-side here for fidelity).
        if (hgt < 128f && hgt > 0f)
            vehicle.VehAnimFrame = (hgt / 128f) * 25f;

        if (hgt < 16f)
        {
            vehicle.MoveType = MoveType.Toss;
            vehicle.VehAnimFrame = 0f;
            vehicle.Think = self => Think(self); // QC: hand back to vehicles_think
        }
        vehicle.NextThink = Time;
    }

    // METHOD(Raptor, vr_death) — raptor.qc
    public override void Death(Entity vehicle)
    {
        vehicle.SetResourceExplicit(ResourceType.Health, 0f);
        vehicle.TakeDamage = DamageMode.No;
        vehicle.Solid = Solid.Corpse;
        vehicle.DeadState = DeadFlag.Dying;
        vehicle.MoveType = MoveType.Bounce;
        Vector3 v = vehicle.Velocity; v.Z += 600f; vehicle.Velocity = v;
        // QC: a wild tumble.
        vehicle.AVelocity = new Vector3(0f, 0.5f, 1f) * 400f * (Prandom.Float() - Prandom.Float());
        vehicle.ColorModKey = new Vector3(-0.5f, -0.5f, -0.5f); // QC vr_death: darken the dying wreck

        // QC vr_death: Send_Effect(EFFECT_EXPLOSION_MEDIUM, findbetterlocation(origin, 16), '0 0 0', 1).
        // findbetterlocation nudges the FX clear of a nearby surface (cosmetic only — no gameplay difference),
        // so emit at origin directly matching every other vehicle's vr_death port (Racer, Bumblebee).
        EffectEmitter.Emit("EXPLOSION_MEDIUM", vehicle.Origin, Vector3.Zero, 1);

        // raptor_diethink: small explosions for ~5-10s, then raptor_blowup. raptor_blowup also runs on touch.
        float when = Time + 5f + Prandom.Range(0f, 5f);
        vehicle.Touch = (self, _) => Blowup(self);
        vehicle.Think = self =>
        {
            if (Time >= when) { Blowup(self); return; }
            // QC raptor_diethink: 5%/think chance — sound(CH_SHOTS, SND_ROCKET_IMPACT) + Send_Effect(EFFECT_EXPLOSION_SMALL,
            // randomvec()*80 + (origin + '0 0 100'), '0 0 0', 1).
            if (Api.Services is not null && Prandom.Float() < 0.05f)
            {
                Api.Sound.Play(self, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav"); // CH_SHOTS, SND_ROCKET_IMPACT
                EffectEmitter.Emit("EXPLOSION_SMALL", Prandom.Vec() * 80f + (self.Origin + new Vector3(0f, 0f, 100f)), Vector3.Zero, 1);
            }
            self.NextThink = Time;
        };
        vehicle.NextThink = Time;
    }

    /// <summary>Port of <c>raptor_blowup</c>: the death blast, then schedule respawn.</summary>
    private void Blowup(Entity vehicle)
    {
        vehicle.DeadState = DeadFlag.Dead;
        // The pilot was already ejected when health hit 0 (vehicles_damage). Eject any straggler defensively.
        if (vehicle.Owner is not null)
            VehicleCommon.ExitVehicle(vehicle, vehicle.Owner, VehicleExitFlag.Normal);

        // raptor.qc raptor_blowup: the death blast is DEATH_VH_RAPT_DEATH.
        WeaponSplash.RadiusDamage(vehicle, vehicle.Origin, 250f, 15f, 250f, vehicle.Enemy, 0, 250f, deathTag: DeathTypes.VhRaptDeath);

        vehicle.MoveType = MoveType.None;
        vehicle.Solid = Solid.Not;
        vehicle.Touch = null;
        vehicle.AVelocity = Vector3.Zero;
        vehicle.Velocity = Vector3.Zero;
        if (Api.Services is not null)
            Api.Entities.SetOrigin(vehicle, vehicle.SpawnPos);
        vehicle.NextThink = Time + RespawnTime;
        vehicle.Think = self => Spawn(self);
    }

    // ============================ WEAPONS ============================

    // METHOD(RaptorCannon, wr_think) — raptor_weapons.qc: the twin laser cannons (alternating gun1/gun2).
    /// <summary>Fire one cannon bolt from the next barrel in the 1-1-2-2 pattern (energy-gated).</summary>
    public void FireCannon(Entity vehicle, Entity player)
    {
        if (vehicle.VehicleEnergy < CannonCost) return; // wr_checkammo1
        vehicle.VehicleEnergy -= CannonCost;
        vehicle.VehRegenPauseTime = Time; // QC: actor.cnt = time (energy regen pause)

        ++vehicle.VehBulletCounter;
        Entity? gun;
        if (vehicle.VehBulletCounter <= 2) gun = vehicle.VehGun1;
        else { gun = vehicle.VehGun2; if (vehicle.VehBulletCounter >= 4) vehicle.VehBulletCounter = 0; }

        var (org, fwd) = VehiclePhysics.TagOriginForward(gun ?? vehicle, "fire1");
        // QC raptor_weapons.qc RaptorCannon.wr_think: normalize(dir + randomvec() * cannon_spread) * cannon_speed
        // — a unit-ball offset added DIRECTLY to forward (perturbing forward too), NOT the uniform-in-disc
        // right/up cone Prandom.Spread projects. Match the QC distribution exactly for this weapon.
        Vector3 dir = QMath.Normalize(fwd + Prandom.Vec() * CannonSpread);
        Vector3 vel = dir * CannonSpeed;

        Entity bolt = VehicleCommon.SpawnProjectile(vehicle, player, org, vel,
            CannonDamage, CannonRadius, CannonForce, size: 0f,
            DeathTypes.VhRaptCannon, health: 0f, lifetime: 0f, // raptor_weapons.qc: DEATH_VH_RAPT_CANNON
            fireSound: "vehicles/lasergun_fire.wav");
        // QC raptor_weapons.qc RaptorCannon.wr_think: CSQCProjectile(proj, true, PROJECTILE_RAPTORCANNON, true).
        // The shared SpawnProjectile stamps the generic "vehicles_projectile" classname, which the client's
        // ProjectileCatalog can't tell apart. Carry the PROJECTILE_RAPTORCANNON token on the NETNAME (not the
        // classname) so ServerNet's ProjectileCatalogKey networks "vehicles_projectile raptorcannon" in the model
        // field and the client classifier picks ProjectileType.RaptorCannon (the TR_NEXUIZPLASMA in-flight plasma
        // trail + dlight) instead of the trailless Generic fallback. Using NetName — not ClassName — keeps the bolt
        // in the "vehicles_projectile" group so the raptor's own missile-alarm / flare-retarget FindByClass scans
        // (which iterate every vehicle projectile, like QC's g_projectiles) still see it; the bolt simply never
        // matches their proj.Enemy == vehicle homing filter, exactly as in QC.
        bolt.NetName = "raptorcannon";
        // QC vehicles_projectile (sv_vehicles.qc): Send_Effect(_mzlfx, proj.origin, proj.velocity, 1) — the
        // muzzle flash at the bolt's spawn point thrown along its velocity. RaptorCannon passes
        // EFFECT_RAPTOR_MUZZLEFLASH as _mzlfx (raptor_weapons.qc RaptorCannon.wr_think). The shared
        // SpawnProjectile helper plays the fire sound but defers the muzzle effect; emit it here
        // server-authoritative (same as Racer/Spiderbot/the turrets).
        EffectEmitter.Emit("RAPTOR_MUZZLEFLASH", org, vel, 1);
    }

    // raptor_bombdrop -> raptor_bomb_burst — raptor_weapons.qc: drop two cluster bombs that burst into bomblets.
    /// <summary>Drop the two cluster bombs from the bomb mounts; each falls then bursts into <see cref="Bomblets"/> bomblets.</summary>
    public void DropBombs(Entity vehicle, Entity player)
    {
        DropOneBomb(vehicle, player, "bombmount_left");
        DropOneBomb(vehicle, player, "bombmount_right");
    }

    private void DropOneBomb(Entity vehicle, Entity player, string tag)
    {
        if (Api.Services is null) return;
        Vector3 org = VehiclePhysics.TagOrigin(vehicle, tag, new Vector3(0f, 0f, -16f));

        Entity bomb = Api.Entities.Spawn();
        bomb.ClassName = "raptor_bomb";
        bomb.Owner = vehicle;
        bomb.DmgInflictor = player;
        bomb.MoveType = MoveType.Bounce;
        // QC PROJECTILE_MAKETRIGGER (raptor_weapons.qc:208): SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE so
        // the dropped bomb is transparent to the raptor's bbox — can't collide with / detonate on its own vehicle.
        Projectiles.MakeTrigger(bomb);
        bomb.Gravity = 1f;
        bomb.Velocity = vehicle.Velocity; // inherit the raptor's velocity, then fall
        Api.Entities.SetSize(bomb, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(bomb, org);
        // QC raptor_bombdrop: cnt = time + 10 is the clear-fall window in which the bomblet_alt altitude test
        // is allowed to keep deferring the burst while there is open air below.
        float clearFallUntil = Time + 10f;

        void Burst(Entity self)
        {
            // QC raptor_bomb_burst: while still inside the clear-fall window and bomblet_alt is enabled, trace
            // ahead along the fall vector — if there's clear air for bomblet_alt units (or we're already within
            // bomblet_radius of the owner raptor) keep falling instead of bursting now.
            Vector3 normVel = QMath.Normalize(self.Velocity);
            if (clearFallUntil > Time && BombletAlt > 0f)
            {
                TraceResult fall = Api.Trace.Trace(self.Origin, Vector3.Zero, Vector3.Zero,
                    self.Origin + normVel * BombletAlt, MoveFilter.Normal, self);
                if (fall.Fraction == 1f
                    || QMath.VLen(self.Origin - vehicle.Origin) < BombletRadius)
                {
                    self.NextThink = Time;
                    return;
                }
            }

            self.Touch = null;
            self.Think = null;

            // QC raptor_bomb_burst: Damage_DamageInfo(origin, ..., DEATH_VH_RAPT_FRAGMENT) fires the client
            // burst FX — the bomb-casing shell-fragment gibs (RaptorCBShellfragToss, 3 bouncing fragment
            // drawables) + the RAPTOR_BOMB_SPREAD particle puff + a rocket-impact crack. Carry the bomb's
            // velocity so the client can throw the frags along the fall direction; purely cosmetic debris.
            EffectEmitter.Emit("RAPTOR_BOMB_SPREAD", self.Origin, self.Velocity, 1);

            // raptor_bomb_burst: scatter `Bomblets` independent bomblets with spread, each exploding on
            // touch or after a short fuse.
            float speed = QMath.VLen(self.Velocity);
            for (int i = 0; i < Bomblets; ++i)
            {
                Entity bomblet = Api.Entities.Spawn();
                bomblet.ClassName = "raptor_bomblet";
                bomblet.Owner = self.Owner;
                bomblet.DmgInflictor = self.DmgInflictor;
                bomblet.MoveType = MoveType.Toss;
                // QC PROJECTILE_MAKETRIGGER (raptor_weapons.qc:167): SOLID_CORPSE + dphitcontentsmask
                // SOLID|BODY|CORPSE — transparent to the raptor's bbox so the scattered bomblets can't self-collide.
                Projectiles.MakeTrigger(bomblet);
                bomblet.Gravity = 1f;
                bomblet.Velocity = QMath.Normalize(normVel + Prandom.Vec() * BombletSpread) * speed;
                Api.Entities.SetSize(bomblet, Vector3.Zero, Vector3.Zero);
                Api.Entities.SetOrigin(bomblet, self.Origin);

                void Boom(Entity b)
                {
                    b.Touch = null; b.Think = null;
                    // raptor_weapons.qc raptor_bomblet_boom: DEATH_VH_RAPT_BOMB.
                    WeaponSplash.RadiusDamage(b, b.Origin, BombletDamage, BombletEdgeDamage, BombletRadius,
                        b.DmgInflictor, 0, BombletForce, deathTag: DeathTypes.VhRaptBomb);
                    Api.Entities.Remove(b);
                }
                // QC raptor_bomblet_touch: a ground/target hit doesn't detonate instantly — it reschedules the
                // boom for time + random()*bomblet_explode_delay, so the cluster staggers its blasts on impact.
                bomblet.Touch = (b, other) =>
                {
                    if (other == b.Owner) return;
                    b.Touch = null;
                    b.Think = Boom;
                    b.NextThink = Time + Prandom.Float() * BombletExplodeDelay;
                };
                bomblet.Think = Boom;
                bomblet.NextThink = Time + 5f; // QC: 5s safety fuse; touch detonates sooner (+random delay)
            }
            Api.Entities.Remove(self);
        }

        bomb.Touch = (self, _) => Burst(self);
        bomb.Think = Burst;
        // QC raptor_bombdrop: with bomblet_alt enabled (default 750) the bomb thinks immediately so the
        // clear-fall altitude test runs every tick; only with bomblet_alt 0 does it wait bomblet_time first.
        bomb.NextThink = BombletAlt > 0f ? Time : Time + BombletTime;
    }

    // METHOD(RaptorFlare, wr_think) — raptor_weapons.qc: drop three decoy flares that seduce guided missiles.
    /// <summary>Drop a spread of three flares; each re-targets nearby guided rockets onto itself for its lifetime.</summary>
    public void FireFlares(Entity vehicle, Entity player, Vector3 viewAngles)
    {
        if (Api.Services is null) return;
        QMath.AngleVectors(viewAngles, out Vector3 forward, out _, out _);
        for (int i = 0; i < 3; ++i)
        {
            Entity flare = Api.Entities.Spawn();
            flare.ClassName = "raptor_flare";
            flare.Owner = vehicle;
            flare.DmgInflictor = player;
            flare.MoveType = MoveType.Toss;
            flare.Solid = Solid.Corpse;
            flare.Gravity = 0.15f;
            flare.TakeDamage = DamageMode.Yes;
            flare.Health = 20f;
            flare.Velocity = 0.25f * vehicle.Velocity + (forward + Prandom.Vec() * 0.25f) * -500f;
            Api.Entities.SetSize(flare, Vector3.Zero, Vector3.Zero);
            Api.Entities.SetOrigin(flare, vehicle.Origin - new Vector3(0, 0, 16f));

            float expire = Time + FlareLifetime;
            flare.Think = self =>
            {
                self.NextThink = Time + 0.1f;
                // raptor_flare_think: ALL projectiles aimed at us (it.enemy == this.owner) within flare_range get
                // re-pointed at the flare on a random()>flare_chase roll. QC applies NO guide-mode filter here —
                // the MIF_GUIDED_TRACKING gate is only on the incoming-alarm path, not the flare retarget — so we
                // must not add one (a VehGuideMode>=0 filter would under-match seducible rockets).
                foreach (Entity proj in Api.Entities.FindByClass("vehicles_projectile"))
                {
                    if (proj.Enemy == self.Owner
                        && QMath.VLen(self.Origin - proj.Origin) < FlareRange
                        && Prandom.Float() > FlareChase)
                        proj.Enemy = self;
                }
                if (expire < Time) Api.Entities.Remove(self);
            };
            flare.Touch = (self, _) => Api.Entities.Remove(self);
            flare.NextThink = Time;
        }
    }

    // ---- mode switch (raptor_impulse) ----
    /// <summary>Port of <c>raptor_impulse</c> weapon-group / cycle: select or cycle the secondary fire mode.</summary>
    public static void SetMode(Entity vehicle, RaptorMode mode) => vehicle.VehW2Mode = (int)mode;
    public static void CycleMode(Entity vehicle, int dir)
    {
        int m = vehicle.VehW2Mode + dir;
        if (m > (int)RaptorMode.Flare) m = (int)RaptorMode.Bomb;
        if (m < (int)RaptorMode.Bomb) m = (int)RaptorMode.Flare;
        vehicle.VehW2Mode = m;
    }

    // METHOD(Raptor, vr_impact) — raptor.qc: the bounce-missile chassis takes crash damage + jolts the pilot.
    /// <summary>
    /// Port of <c>vr_impact</c>: when the raptor (MOVETYPE_BOUNCEMISSILE) slams into geometry, apply
    /// <c>vehicles_impact(1, 4, 1000)</c> — DEATH_FALL self-damage scaled by the impact speed past minspeed
    /// 1000, multiplied by 4 and capped, debounced to once / 0.25s. Dispatched live from the vehicle touch
    /// path (<see cref="VehicleCommon"/>) which already gates on <c>play_time</c>.
    /// </summary>
    public override void Impact(Entity vehicle)
    {
        // QC: vector autocvar_g_vehicle_raptor_bouncepain = '1 4 1000' (minspeed, damagemultiplier, maxpain).
        VehicleCommon.Impact(vehicle, 1f, 4f, 1000f);
    }

    private Entity NewGun(Entity vehicle)
    {
        Entity g = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
        g.ClassName = "raptor_gun";
        g.Owner = vehicle;
        g.VehSlotOwner = vehicle;
        if (Api.Services is not null) Api.Models.SetAttachment(g, vehicle, "");
        return g;
    }

    // QC DPCONTENTS_LIQUIDSMASK = WATER|SLIME|LAVA (so the raptor sinks instead of treating liquid as empty).
    private const int SuperContentsLiquidsMask = 0x00000010 | 0x00000020 | 0x00000040;

    private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;
    private static float FrameTime => Api.Services is not null ? Api.Clock.FrameTime : 0.05f;
    private static float MaxAirSpeed()
    {
        if (Api.Services is null) return 400f;
        float v = Api.Cvars.GetFloat("sv_maxairspeed");
        return v != 0f ? v : 400f;
    }
}

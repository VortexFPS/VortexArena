// Port: qcsrc/common/vehicles/vehicle/bumblebee.{qh,qc} + bumblebee_weapons.{qh,qc}
//
// The Bumblebee — a large MULTI-SEAT flying gunship seating up to three: a pilot (flies + fires the center
// heal/ray gun) and two side gunners (each a plasma cannon). It is the only multi-slot vehicle.
//
// FULL deep behavior is implemented: the avelocity pilot flight controller, the center raygun in BOTH
// modes (damage beam with energy drain AND heal beam that tops up teammate health/armor and friendly
// vehicle shields), the heal-ray target lock, AND the complete MULTI-SEAT machinery — two gunner slots
// (gun1/gun2) with independent enter/exit/aim/fire (bumblebee_gunner_enter/exit + bumblebee_gunner_frame,
// each gun a turret that locks + leads + fires its plasma cannon), the touch router that sends the 2nd/3rd
// boarder to a gunner seat, and the role check that ejects gunners when the pilot leaves. Only client-only
// items (the networked heal-beam BRG_* entity, muzzle FX, aux crosshairs, HUD %, gib models) stay TODO.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Bumblebee vehicle (QC <c>CLASS(Bumblebee, Vehicle)</c>). Registered via <see cref="VehicleAttribute"/>.
/// </summary>
[Vehicle]
public sealed class Bumblebee : Vehicle
{
    // ---- chassis (bumblebee.qc autocvars) ----
    public float SpeedForward = 350f;   // g_vehicle_bumblebee_speed_forward
    public float SpeedStrafe = 350f;    // g_vehicle_bumblebee_speed_strafe
    public float SpeedUp = 350f;        // g_vehicle_bumblebee_speed_up
    public float SpeedDown = 350f;      // g_vehicle_bumblebee_speed_down
    public float Friction = 0.5f;       // g_vehicle_bumblebee_friction
    public float TurnSpeed = 120f;      // g_vehicle_bumblebee_turnspeed
    public float PitchSpeed = 60f;      // g_vehicle_bumblebee_pitchspeed
    public float PitchLimit = 60f;      // g_vehicle_bumblebee_pitchlimit
    public float ThinkRate = VehicleCommon.DefaultThinkRate;

    // ---- resources ----
    public float MaxEnergy = 500f;          // g_vehicle_bumblebee_energy
    public float EnergyRegen = 50f;         // g_vehicle_bumblebee_energy_regen
    public float EnergyRegenPause = 1f;     // g_vehicle_bumblebee_energy_regen_pause
    public float MaxShield = 400f;          // g_vehicle_bumblebee_shield
    public float ShieldRegen = 150f;        // g_vehicle_bumblebee_shield_regen
    public float ShieldRegenPause = 0.75f;  // g_vehicle_bumblebee_shield_regen_pause
    public float HealthRegen = 65f;         // g_vehicle_bumblebee_health_regen
    public float HealthRegenPause = 10f;    // g_vehicle_bumblebee_health_regen_pause
    public float RespawnTime = 60f;         // g_vehicle_bumblebee_respawntime

    // ---- side-gunner cannon (bumblebee_weapons.qh) ----
    public float CannonCost = 2f;        // g_vehicle_bumblebee_cannon_cost
    public float CannonDamage = 60f;     // g_vehicle_bumblebee_cannon_damage
    public float CannonRadius = 225f;    // g_vehicle_bumblebee_cannon_radius
    public float CannonForce = -35f;     // g_vehicle_bumblebee_cannon_force (negative: pulls victims in)
    public float CannonSpeed = 20000f;   // g_vehicle_bumblebee_cannon_speed
    public float CannonSpread = 0f;      // g_vehicle_bumblebee_cannon_spread
    public float CannonRefire = 0.2f;    // g_vehicle_bumblebee_cannon_refire
    public float CannonAmmoMax = 100f;   // g_vehicle_bumblebee_cannon_ammo
    public float CannonAmmoRegen = 100f; // g_vehicle_bumblebee_cannon_ammo_regen
    public float CannonAmmoRegenPause = 1f; // g_vehicle_bumblebee_cannon_ammo_regen_pause
    public int CannonLock = 1;           // g_vehicle_bumblebee_cannon_lock
    public float CannonTurnSpeed = 260f; // g_vehicle_bumblebee_cannon_turnspeed
    public float CannonPitchLimitDown = 60f; // g_vehicle_bumblebee_cannon_pitchlimit_down
    public float CannonPitchLimitUp = 60f;   // g_vehicle_bumblebee_cannon_pitchlimit_up
    public float CannonTurnLimitIn = 20f;    // g_vehicle_bumblebee_cannon_turnlimit_in
    public float CannonTurnLimitOut = 80f;   // g_vehicle_bumblebee_cannon_turnlimit_out

    // ---- pilot center gun (bumblebee.qc) ----
    public bool RaygunDamage = false;    // g_vehicle_bumblebee_raygun (0 = heal mode, 1 = damage mode)
    public float RaygunRange = 2048f;    // g_vehicle_bumblebee_raygun_range
    public float RaygunDps = 250f;       // g_vehicle_bumblebee_raygun_dps
    public float RaygunAps = 100f;       // g_vehicle_bumblebee_raygun_aps (energy/sec in damage mode)
    public float RaygunFps = 100f;       // g_vehicle_bumblebee_raygun_fps (force/sec)
    public float RaygunTurnSpeed = 180f; // g_vehicle_bumblebee_raygun_turnspeed
    public float RaygunPitchLimitDown = 20f; // g_vehicle_bumblebee_raygun_pitchlimit_down
    public float RaygunPitchLimitUp = 5f;    // g_vehicle_bumblebee_raygun_pitchlimit_up
    public float RaygunTurnLimitSides = 35f; // g_vehicle_bumblebee_raygun_turnlimit_sides
    public float HealgunHps = 150f;      // g_vehicle_bumblebee_healgun_hps (health/sec)
    public float HealgunHmax = 100f;     // g_vehicle_bumblebee_healgun_hmax
    public float HealgunAps = 75f;       // g_vehicle_bumblebee_healgun_aps (armor/sec)
    public float HealgunAmax = 100f;     // g_vehicle_bumblebee_healgun_amax
    public float HealgunSps = 100f;      // g_vehicle_bumblebee_healgun_sps (vehicle shield/sec)
    public float HealgunLockTime = 2.5f; // g_vehicle_bumblebee_healgun_locktime

    public Bumblebee()
    {
        NetName = "bumblebee";
        DisplayName = "Bumblebee";
        Model = "models/vehicles/bumblebee_body.dpm";
        StartHealth = 1000f;              // g_vehicle_bumblebee_health
    }

    // METHOD(Bumblebee, vr_spawn) — bumblebee.qc
    public override void Spawn(Entity vehicle)
    {
        VehicleCommon.SpawnVehicle(vehicle, this);

        if (Model is not null && Api.Services is not null)
            Api.Entities.SetModel(vehicle, Model);

        // Sub-entities: the two side-gun SLOTS (gun1/gun2) and the center raygun (gun3). Created once.
        if (vehicle.VehGun1 is null)
        {
            vehicle.VehGun1 = NewSlot(vehicle, 1, "cannon_right");
            vehicle.VehGun2 = NewSlot(vehicle, 2, "cannon_left");
            vehicle.VehGun3 = NewSub(vehicle, "bumblebee_raygun", "raygun");
            vehicle.VehicleFlags |= VehicleFlags.MultiSlot;

            // QC vr_spawn (bumblebee.qc:878-880): the three cosmetic cannon models — gun1 = CANNON_RIGHT
            // (bumblebee_plasma_right.dpm), gun2 = CANNON_LEFT (bumblebee_plasma_left.dpm), gun3 = CANNON_CENTER
            // (bumblebee_ray.dpm), attached at the cannon_right/cannon_left/raygun tags. So the side cannons and
            // the center raygun render their real plasma/ray models while the airframe is alive (previously these
            // model strings were only assigned at death, leaving the live gunship's guns invisible).
            SetGunModel(vehicle.VehGun1, "models/vehicles/bumblebee_plasma_right.dpm");
            SetGunModel(vehicle.VehGun2, "models/vehicles/bumblebee_plasma_left.dpm");
            SetGunModel(vehicle.VehGun3, "models/vehicles/bumblebee_ray.dpm");
        }

        vehicle.MaxHealth = StartHealth;
        vehicle.SetResourceExplicit(ResourceType.Health, StartHealth);
        vehicle.VehicleShield = MaxShield;
        vehicle.RespawnTime = RespawnTime;
        vehicle.MoveType = MoveType.Toss;   // QC: MOVETYPE_TOSS
        vehicle.Solid = Solid.BBox;
        vehicle.DeadState = DeadFlag.No;
        vehicle.DamageForceScale = 0.025f;  // QC vr_spawn: instance.damageforcescale = 0.025
        // Per-gun ammo pools start full (QC gun1/gun2 vehicle_energy).
        if (vehicle.VehGun1 is not null) vehicle.VehGun1.VehicleEnergy = CannonAmmoMax;
        if (vehicle.VehGun2 is not null) vehicle.VehGun2.VehicleEnergy = CannonAmmoMax;
        vehicle.Touch = (self, toucher) => Touch(self, toucher);

        // Hitbox: '-245 -130 -130' .. '230 130 130' (bumblebee.qh) — a big airframe.
        if (Api.Services is not null)
            Api.Entities.SetSize(vehicle, new Vector3(-245f, -130f, -130f), new Vector3(230f, 130f, 130f));

        // vr_setup flag derivation — QC gates each flag on its OWN cvar(s), not a generic ">0" of the
        // descriptor field: VHF_ENERGYREGEN needs the PAIR (energy && energy_regen); VHF_HASSHIELD on shield;
        // VHF_SHIELDREGEN on shield_regen; VHF_HEALTHREGEN on health_regen. MoveFly|DmgShake stay hardcoded.
        vehicle.VehicleFlags |= VehicleFlags.MoveFly | VehicleFlags.MultiSlot | VehicleFlags.DmgShake;
        if (MaxEnergy != 0f && EnergyRegen != 0f) vehicle.VehicleFlags |= VehicleFlags.EnergyRegen;
        if (MaxShield != 0f) vehicle.VehicleFlags |= VehicleFlags.HasShield;
        if (ShieldRegen != 0f) vehicle.VehicleFlags |= VehicleFlags.ShieldRegen;
        if (HealthRegen != 0f) vehicle.VehicleFlags |= VehicleFlags.HealthRegen;

        // QC vr_spawn: when g_vehicle_bumblebee_swim is FALSE the airframe collides with liquids
        // (dphitcontentsmask |= DPCONTENTS_LIQUIDSMASK). At the swim=true default the mask is left alone.
        if (Cvar("g_vehicle_bumblebee_swim", 1f) == 0f)
            vehicle.DpHitContentsMask |= SuperContentsLiquidsMask;

        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;

        // QC vr_spawn tail: setorigin(instance, instance.origin + '0 0 25') — lift the airframe 25u off its
        // spawn point so it doesn't clip the floor it was placed on.
        if (Api.Services is not null)
            Api.Entities.SetOrigin(vehicle, vehicle.Origin + new Vector3(0f, 0f, 25f));

        // TODO(port,client): qcsrc/common/vehicles/vehicle/bumblebee.qc vr_spawn — networked BRG_* heal-beam
        //                    entity, shield entity, cockpit/viewport offsets, gun cosmetic models, scale 1.5.
    }

    // QC DPCONTENTS_LIQUIDSMASK — the SUPERCONTENTS bits for water/slime/lava (CONTENTS_WATER|SLIME|LAVA).
    private const int SuperContentsLiquidsMask = 0x00000010 | 0x00000020 | 0x00000040;

    // METHOD(Bumblebee, vr_enter) — bumblebee.qc (the PILOT enters here).
    public override void Enter(Entity vehicle, Entity player)
    {
        VehicleCommon.EnterVehicle(vehicle, player);
        vehicle.MoveType = MoveType.BounceMissile; // QC: MOVETYPE_BOUNCEMISSILE
        vehicle.NextThink = Time;
        vehicle.Touch = (self, toucher) => Touch(self, toucher);
        vehicle.Think = self => Think(self);
    }

    // void bumblebee_touch — bumblebee.qc: route the 2nd/3rd boarder to a gunner seat; else normal enter.
    private void Touch(Entity vehicle, Entity toucher)
    {
        if (Api.Services is null) return;
        // [A2-review F4] QC bumblebee_touch opens with `if (autocvar_g_vehicles_enter) return;` — touch-based
        // gunner entry only happens in TOUCH mode (g_vehicles_enter==0). In the shipped default (1, use-key)
        // boarding is +use only; without this gate a moving/landing bumblebee that bumps a same-team teammate
        // would yank them into a gunner seat. Use-key gunner boarding works via VehicleBoarding.Enter's MULTISLOT branch.
        if (Cvar("g_vehicles_enter", 1f) != 0f) return;
        // Both gunner seats taken -> behave like a normal vehicle touch (no more seats).
        if (vehicle.VehGunner1 is not null && vehicle.VehGunner2 is not null)
        {
            // QC: vehicles_touch(this, toucher); return; — fall through to the shared dispatcher
            // (crush / vr_impact bounce-pain / pilot touch-board).
            VehicleCommon.Touch(vehicle, toucher);
            return;
        }

        if (ValidPilot(vehicle, toucher) && Time >= toucher.VehicleEnterDelay
            && (Time >= (vehicle.VehGun1?.VehPhase ?? 0f) || Time >= (vehicle.VehGun2?.VehPhase ?? 0f)))
        {
            if (GunnerEnter(vehicle, toucher))
                return;
        }

        // QC bumblebee_touch tail: vehicles_touch(this, toucher) — the body is free (no pilot) enters as pilot,
        // or (while piloted) the shared dispatcher runs the vr_impact bounce-pain.
        VehicleCommon.Touch(vehicle, toucher);
    }

    // void bumblebee_exit(entity this, int eject) — bumblebee.qc (PILOT exit; gunners exit separately).
    public override void Exit(Entity vehicle, Entity player)
    {
        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out _, out _);
        // QC: forward if moving fast, behind if slow; +up so the pilot drops clear of the airframe.
        bool fast = QMath.VLen(vehicle.Velocity) > SpeedForward * 0.5f;
        Vector3 spot = fast
            ? vehicle.Origin + new Vector3(0, 0, 128f) + forward * 300f
            : vehicle.Origin + new Vector3(0, 0, 128f) - forward * 300f;
        spot = VehicleCommon.FindGoodExit(vehicle, player, spot);

        player.Velocity = 0.75f * vehicle.Velocity + QMath.Normalize(spot - vehicle.Origin) * 200f
            + new Vector3(0, 0, 10f);

        bool dying = VehicleCommon.IsDead(vehicle);

        // Hide the heal beam (client visual).
        // TODO(port,client): set gun3.enemy (the BRG_* beam) EF_NODRAW.

        VehicleCommon.ExitVehicle(vehicle, player, dying ? VehicleExitFlag.Eject : VehicleExitFlag.Normal);
        if (Api.Services is not null)
            Api.Entities.SetOrigin(player, spot);

        // QC: a living bumblebee auto-lands once the pilot leaves.
        if (!dying)
        {
            vehicle.MoveType = MoveType.Toss;
            vehicle.Touch = (self, toucher) => Touch(self, toucher);
            vehicle.Think = self => Land(self);
            vehicle.NextThink = Time;
        }
    }

    // bumblebee_pilot_frame() dispatcher + vr_think gunner role check — bumblebee.qc.
    public override void Think(Entity vehicle)
    {
        float dt = ThinkRate;
        vehicle.NextThink = Time + dt;

        if (VehicleCommon.FreezeIfGameStopped(vehicle))
            return;

        // QC engine .oldvelocity = last-tick velocity; vr_impact (vehicles_impact) measures the touch
        // speed-change against it. Snapshot BEFORE this tick's physics so a touch dispatched this frame
        // sees the real delta (matches racer_think).
        vehicle.OldVelocity = vehicle.Velocity;

        Entity? player = vehicle.Owner;

        // vr_think: ease body roll/pitch; if there's no pilot but a gunner is still aboard, promote a gunner
        // to pilot — QC ejects the gunner (VHEF_EJECT) then re-touches the now-ownerless body, which seats
        // that player as the new pilot. We do it directly: eject the gunner, then Enter() them as pilot.
        Vector3 a = vehicle.Angles; a.Z *= 0.8f; a.X *= 0.8f; vehicle.Angles = a;
        if (player is null)
        {
            Entity? promote = null;
            if (vehicle.VehGunner1 is not null) { promote = vehicle.VehGunner1; GunnerExit(vehicle.VehGun1!, true); }
            else if (vehicle.VehGunner2 is not null) { promote = vehicle.VehGunner2; GunnerExit(vehicle.VehGun2!, true); }

            if (promote is not null && !VehicleCommon.IsDead(vehicle))
            {
                promote.VehicleEnterDelay = 0f; // allow the immediate promotion (bypass the 2s re-entry gate)
                Enter(vehicle, promote);
                player = vehicle.Owner;
            }
        }

        if (player is not null && !VehicleCommon.IsDead(vehicle))
        {
            Frame(vehicle, player, vehicle.VehInput, dt);
        }

        // Drive each seated gunner's turret + fire (their own per-frame controller).
        if (vehicle.VehGunner1 is not null && vehicle.VehGun1 is not null && !VehicleCommon.IsDead(vehicle))
            GunnerFrame(vehicle.VehGun1, vehicle.VehGunner1, dt);
        if (vehicle.VehGunner2 is not null && vehicle.VehGun2 is not null && !VehicleCommon.IsDead(vehicle))
            GunnerFrame(vehicle.VehGun2, vehicle.VehGunner2, dt);

        // Regen (bumblebee_regen): per-gun ammo + the body shield/energy/health.
        Regen(vehicle, dt);

        if (player is not null)
        {
            // QC: pilot rides above-forward of the airframe (origin + up*48 + forward*160).
            QMath.AngleVectors(vehicle.Angles, out Vector3 fwd, out _, out Vector3 up);
            if (Api.Services is not null)
                Api.Entities.SetOrigin(player, vehicle.Origin + up * 48f + fwd * 160f);
            player.OldOrigin = player.Origin;
            player.Velocity = vehicle.Velocity;
        }

        // QC vehicles_think: vehicles_painframe(this) runs after vr_think every tick — low-health smoke + jitter.
        VehicleCommon.PainFrame(vehicle);
    }

    /// <summary>Port of <c>bumblebee_regen</c>: per-gun cannon ammo + the body shield/energy/health.</summary>
    private void Regen(Entity vehicle, float dt)
    {
        if (vehicle.VehGun1 is not null && vehicle.VehGun1.VehWeaponDelay + CannonAmmoRegenPause < Time)
            vehicle.VehGun1.VehicleEnergy = MathF.Min(CannonAmmoMax, vehicle.VehGun1.VehicleEnergy + CannonAmmoRegen * dt);
        if (vehicle.VehGun2 is not null && vehicle.VehGun2.VehWeaponDelay + CannonAmmoRegenPause < Time)
            vehicle.VehGun2.VehicleEnergy = MathF.Min(CannonAmmoMax, vehicle.VehGun2.VehicleEnergy + CannonAmmoRegen * dt);

        if ((vehicle.VehicleFlags & VehicleFlags.ShieldRegen) != 0)
            vehicle.VehicleShield = VehicleCommon.Regen(vehicle, vehicle.VehicleShield, vehicle.DmgTime,
                MaxShield, ShieldRegenPause, ShieldRegen, dt, healthScale: true);
        if ((vehicle.VehicleFlags & VehicleFlags.EnergyRegen) != 0)
            vehicle.VehicleEnergy = VehicleCommon.Regen(vehicle, vehicle.VehicleEnergy, vehicle.VehWait,
                MaxEnergy, EnergyRegenPause, EnergyRegen, dt, healthScale: false);
        if ((vehicle.VehicleFlags & VehicleFlags.HealthRegen) != 0)
            VehicleCommon.RegenResource(vehicle, vehicle.DmgTime, StartHealth, HealthRegenPause, HealthRegen, dt, false, ResourceType.Health);
    }

    /// <summary>
    /// Port of <c>bumblebee_pilot_frame</c>: the avelocity pitch/yaw flight controller, roll on strafe,
    /// yaw-relative thrust, vertical jump/crouch climb, the heal-ray target lock + turret aim, and the
    /// center raygun in damage OR heal mode.
    /// </summary>
    public void Frame(Entity vehicle, Entity player, in MovementInput input, float dt)
    {
        if (VehicleCommon.IsDead(vehicle)) return;
        Vector3 move = input.MoveValues;
        Vector3 vAng = input.ViewAngles;

        TraceResult tr = VehiclePhysics.CrosshairTrace(vehicle, vAng, vehicle);
        Vector3 aimPoint = tr.EndPos;

        // --- avelocity yaw/pitch controller (same shape as the raptor) ---------------------------------
        Vector3 vang = vehicle.Angles; vang.X = -vang.X;
        Vector3 nvAng = QMath.FixedVecToAngles(QMath.Normalize(aimPoint - vehicle.Origin + new Vector3(0, 0, 32f)));
        nvAng = VehiclePhysics.ShortAngles(nvAng);

        float yawErr = VehiclePhysics.ShortAngle(vAng.Y - vang.Y);
        Vector3 av = vehicle.AVelocity;
        av.Y = QMath.Bound(-TurnSpeed, yawErr + av.Y * 0.9f, TurnSpeed);

        float pbias = 0f;
        if (move.X > 0f && vang.X < PitchLimit) pbias = 4f;
        else if (move.X < 0f && vang.X > -PitchLimit) pbias = -8f;
        float nx = QMath.Bound(-PitchLimit, nvAng.X, PitchLimit);
        float pitchErr = vang.X - QMath.Bound(-PitchLimit, nx + pbias, PitchLimit);
        av.X = QMath.Bound(-PitchSpeed, pitchErr + av.X * 0.9f, PitchSpeed);
        vehicle.AVelocity = av;

        Vector3 a = vehicle.Angles;
        a.X = VehiclePhysics.AngleMods(a.X); a.Y = VehiclePhysics.AngleMods(a.Y); a.Z = VehiclePhysics.AngleMods(a.Z);
        vehicle.Angles = a;

        // --- thrust (yaw-relative) ---------------------------------------------------------------------
        QMath.AngleVectors(new Vector3(0f, vehicle.Angles.Y, 0f), out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector3 nv = vehicle.Velocity * -Friction;
        if (move.X != 0f) nv += forward * (move.X > 0f ? SpeedForward : -SpeedForward);
        if (move.Y != 0f)
        {
            nv += right * (move.Y > 0f ? SpeedStrafe : -SpeedStrafe);
            float roll = QMath.Dot(nv, right) * dt * 0.1f;
            a = vehicle.Angles; a.Z = QMath.Bound(-15f, a.Z + roll, 15f); vehicle.Angles = a;
        }
        else { a = vehicle.Angles; a.Z *= 0.95f; vehicle.Angles = a; }
        if (input.ButtonCrouch) nv -= up * SpeedDown;
        else if (input.ButtonJump) nv += up * SpeedUp;
        vehicle.Velocity += nv * dt;

        // --- heal-ray target lock (heal mode prefers a friendly under the crosshair) -------------------
        Vector3 beamAim = aimPoint;
        if (HealgunLockTime > 0f && vehicle.VehGun3 is not null)
        {
            Entity g3 = vehicle.VehGun3;
            // QC bumblebee.qc:519: drop the lock when expired/dead OR the locked teammate is FROZEN (freezetag —
            // a frozen player can't be healed; force a re-lock so the beam stops topping them up).
            if (g3.VehLockTime < Time || (g3.Enemy is not null && (VehicleCommon.IsDead(g3.Enemy) || IsFrozen(g3.Enemy)))) g3.Enemy = null;
            Entity? te = tr.Ent;
            // QC bumblebee.qc:524: skip a FROZEN candidate when acquiring (don't lock onto a frozen teammate).
            if (te is not null && te.MoveType != MoveType.None && te.TakeDamage != DamageMode.No
                && !VehicleCommon.IsDead(te) && !IsFrozen(te))
            {
                bool ok = VehiclePhysics.SameTeam(te, vehicle) || vehicle.Team == 0f;
                if (ok) { g3.Enemy = te; g3.VehLockTime = Time + HealgunLockTime; }
            }
            if (g3.Enemy is not null) beamAim = g3.Enemy.Origin;

            // Auxiliary heal-lock crosshair (QC bumblebee_pilot_frame draws aux xhair '0 0.75 0' over the locked
            // target). The bumblebee's heal lock is binary (locked or not — no build-up ramp), so mirror it onto
            // the shared VehLockTarget/VehLockStrength that the client aux-crosshair feeder already projects.
            vehicle.VehLockTarget = g3.Enemy;
            vehicle.VehLockStrength = g3.Enemy is not null ? 1f : 0f;
        }

        // --- aim the raygun turret (gun3) --------------------------------------------------------------
        if (vehicle.VehGun3 is not null)
            VehiclePhysics.AimTurret(vehicle, beamAim, vehicle.VehGun3, "fire",
                -RaygunPitchLimitDown, RaygunPitchLimitUp, -RaygunTurnLimitSides, RaygunTurnLimitSides, RaygunTurnSpeed, dt);

        // --- fire the center raygun (damage or heal) ---------------------------------------------------
        bool firing = input.ButtonAttack1 || input.ButtonAttack2;
        bool haveEnergy = vehicle.VehicleEnergy > RaygunDps * FrameTime || !RaygunDamage;
        if (firing && haveEnergy)
            FireRay(vehicle, player, beamAim, dt);

        // Mirror the 0..100 vehicle stats onto the pilot for the in-vehicle HUD (QC bumblebee_pilot_frame:
        // vehicle_health/shield/energy + vehicle_ammo1/2 = (gun1/2.vehicle_energy / cannon_ammo) * 100). The
        // client feeder (NetGame.UpdateVehicleHud) reads these off the pilot each frame.
        player.VehicleHealth = vehicle.GetResource(ResourceType.Health) / StartHealth * 100f;
        player.VehicleShield = vehicle.VehicleShield / MaxShield * 100f;
        player.VehicleEnergy = vehicle.VehicleEnergy / MaxEnergy * 100f;
        player.VehicleAmmo1 = vehicle.VehGun1 is not null ? vehicle.VehGun1.VehicleEnergy / CannonAmmoMax * 100f : 0f;
        player.VehicleAmmo2 = vehicle.VehGun2 is not null ? vehicle.VehGun2.VehicleEnergy / CannonAmmoMax * 100f : 0f;

        // TODO(port,client): qcsrc/common/vehicles/vehicle/bumblebee.qc bumblebee_pilot_frame — engine sound,
        //                    networked BRG_* heal-beam visual (remote spectators), the gunner cockpit HUD.
    }

    // METHOD(Bumblebee, vr_impact) — bumblebee.qc: a hard bounce jolts the pilot (vehicles_impact).
    /// <summary>
    /// Port of <c>vr_impact</c>: on a hard collision (the velocity change exceeds the bouncepain minspeed)
    /// deal <c>min(speedfac * speedDelta, maxpain)</c> DEATH_FALL pain to the airframe, debounced 0.25s.
    /// Bouncepain <c>'1 100 200'</c> = minspeed 1, factor 100, max 200. Dispatched from the shared
    /// <c>vehicles_touch</c> path (<see cref="VehicleCommon.Touch"/> -> <see cref="VehicleCommon.Impact"/>);
    /// <see cref="Entity.OldVelocity"/> is the last-tick velocity snapshotted by <see cref="Think"/>.
    /// </summary>
    public override void Impact(Entity vehicle)
    {
        // QC: vector autocvar_g_vehicle_bumblebee_bouncepain = '1 100 200' (minspeed, speedfac, maxpain).
        VehicleCommon.Impact(vehicle, 1f, 100f, 200f);
    }

    // void bumblebee_land(entity this) — bumblebee.qc.
    private void Land(Entity vehicle)
    {
        if (Api.Services is null) { vehicle.NextThink = 0f; return; }
        float hgt = VehiclePhysics.Altitude(vehicle, 512f);
        vehicle.Velocity = vehicle.Velocity * 0.9f + new Vector3(0, 0, -1800f) * (hgt / 256f) * FrameTime;
        Vector3 a = vehicle.Angles; a.X *= 0.95f; a.Z *= 0.95f; vehicle.Angles = a;
        if (hgt < 16f)
            vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;
    }

    // METHOD(Bumblebee, vr_death) — bumblebee.qc
    public override void Death(Entity vehicle)
    {
        vehicle.SetResourceExplicit(ResourceType.Health, 0f);
        vehicle.TakeDamage = DamageMode.No;
        vehicle.DeadState = DeadFlag.Dying;

        // QC: eject both gunners on death.
        if (vehicle.VehGunner1 is not null) GunnerExit(vehicle.VehGun1!, true);
        if (vehicle.VehGunner2 is not null) GunnerExit(vehicle.VehGun2!, true);
        // The pilot was already ejected by vehicles_damage before vr_death.

        vehicle.Solid = Solid.Not;
        vehicle.MoveType = MoveType.None;
        vehicle.Velocity = Vector3.Zero;

        // QC vr_death: Send_Effect(EFFECT_EXPLOSION_MEDIUM, findbetterlocation(origin, 16), '0 0 0', 1) — the
        // medium blast that marks the airframe dying (the port has no findbetterlocation wall-nudge, so emit at
        // the origin directly — the helper only pushes the FX clear of a surface, not a gameplay difference).
        EffectEmitter.Emit("EXPLOSION_MEDIUM", vehicle.Origin, Vector3.Zero, 1);

        // QC vr_death (bumblebee.qc:816-819): fixedmakevectors(angles); toss the three gun gibs off the wreck —
        // each cannon flung outward along the body axes + jitter, spinning, 50/50 burning and 50/50 exploding,
        // over a 6s window. The gun gibs carry their cannon model strings so the debris is the real plasma/ray
        // models. This is the per-piece wreckage the descriptors' single death EXPLOSION couldn't show.
        QMath.AngleVectors(vehicle.Angles, out Vector3 vf, out Vector3 vr, out Vector3 vu);
        Entity? g1 = vehicle.VehGun1, g2 = vehicle.VehGun2, g3 = vehicle.VehGun3;
        if (g1 is not null)
        {
            g1.Model = "models/vehicles/bumblebee_plasma_right.dpm"; // MDL_VEH_BUMBLEBEE_CANNON_RIGHT (gun1)
            VehicleCommon.TossGib(vehicle, g1, vehicle.Velocity + 300f * vr + 100f * vu + 200f * Prandom.Vec(),
                "cannon_right", Prandom.Float() < 0.5f, Prandom.Float() < 0.5f, 6f, Prandom.Vec() * 200f);
        }
        if (g2 is not null)
        {
            g2.Model = "models/vehicles/bumblebee_plasma_left.dpm"; // MDL_VEH_BUMBLEBEE_CANNON_LEFT (gun2)
            VehicleCommon.TossGib(vehicle, g2, vehicle.Velocity + -300f * vr + 100f * vu + 200f * Prandom.Vec(),
                "cannon_left", Prandom.Float() < 0.5f, Prandom.Float() < 0.5f, 6f, Prandom.Vec() * 200f);
        }
        if (g3 is not null)
        {
            g3.Model = "models/vehicles/bumblebee_ray.dpm"; // MDL_VEH_BUMBLEBEE_CANNON_CENTER (gun3/raygun)
            VehicleCommon.TossGib(vehicle, g3, vehicle.Velocity + 300f * vf + -100f * vu + 200f * Prandom.Vec(),
                "raygun", Prandom.Float() < 0.5f, Prandom.Float() < 0.5f, 6f, Prandom.Vec() * 300f);
        }

        // QC vr_death tail: a 50% chance the tossed body blows up early on contact (bumblebee_dead_touch). The
        // port keeps the body in place rather than spawning a separate gib, so model the contact-blowup on the
        // body's own .Touch: a fired/bumped corpse detonates immediately into bumblebee_blowup.
        bool deadTouch = Prandom.Float() > 0.5f;

        // QC bumblebee_blowup: a big radius blast after a short tumble (wait = time + 2 + random*8), then respawn.
        float when = Time + 2f + Prandom.Range(0f, 8f);

        // bumblebee_blowup — the final death detonation (DEATH_VH_BUMB_DEATH) + SND_ROCKET_IMPACT + EXPLOSION_BIG.
        void Blowup(Entity self)
        {
            WeaponSplash.RadiusDamage(self, self.Origin, 500f, 100f, 500f, self.Enemy, 0, 600f, deathTag: DeathTypes.VhBumbDeath);
            // QC: sound(this, CH_SHOTS, SND_ROCKET_IMPACT, ...); Send_Effect(EFFECT_EXPLOSION_BIG, origin + '0 0 100' + randomvec()*80, ...).
            if (Api.Services is not null) Api.Sound.Play(self, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav");
            EffectEmitter.Emit("EXPLOSION_BIG", self.Origin + new Vector3(0f, 0f, 100f) + Prandom.Vec() * 80f, Vector3.Zero, 1);

            self.DeadState = DeadFlag.Dead;
            self.Touch = null;
            if (Api.Services is not null) Api.Entities.SetOrigin(self, self.SpawnPos);
            self.NextThink = Time + RespawnTime;
            self.Think = s => Spawn(s);
        }

        // QC bumblebee_dead_touch: settouch(_body, bumblebee_dead_touch) on the 50% branch — blow up on contact.
        vehicle.Touch = deadTouch ? (self, _) => Blowup(self) : null;

        // bumblebee_diethink — tumble: 10% per 0.1s tick a small explosion + impact sound; at `wait` -> blowup.
        vehicle.Think = self =>
        {
            if (Time >= when)
            {
                Blowup(self);
                return;
            }

            // QC bumblebee_diethink: if (random() < 0.1) { sound SND_ROCKET_IMPACT; Send_Effect(EFFECT_EXPLOSION_SMALL,
            // randomvec()*80 + (origin + '0 0 100'), '0 0 0', 1); }.
            if (Prandom.Float() < 0.1f)
            {
                if (Api.Services is not null) Api.Sound.Play(self, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav");
                EffectEmitter.Emit("EXPLOSION_SMALL", Prandom.Vec() * 80f + (self.Origin + new Vector3(0f, 0f, 100f)), Vector3.Zero, 1);
            }
            self.NextThink = Time + 0.1f;
        };
        vehicle.NextThink = Time;
        // NOTE: the gun1/gun2/gun3 wreckage gibs ARE now tossed (above, via VehicleCommon.TossGib — the
        // vehicle_gib physics-entity subsystem). Base also replaces the BODY with a separate tossgib that runs the
        // diethink; the port keeps the diethink/blowup + respawn on the original body in place (functionally
        // equivalent for the death->blowup->respawn cycle, with one fewer throwaway entity). The heal-ray beam
        // hide (gun3.enemy EF_NODRAW) is a client-only visual and remains deferred.
    }

    // ============================ MULTI-SEAT ============================

    private static bool ValidPilot(Entity vehicle, Entity toucher)
    {
        if (toucher is Player { IsBot: true } && !AllowBots()) return false;
        if (toucher is not Player) return false;
        Player pl = (Player)toucher;
        if (pl.IsDead || pl.Vehicle is not null) return false;
        if (VehiclePhysics.DiffTeam(toucher, vehicle) && vehicle.Team != 0f) return false;
        return true;
    }

    // bool bumblebee_gunner_enter — bumblebee.qc: seat a player in the nearest free side-gun slot.
    /// <summary>Port of <c>bumblebee_gunner_enter</c>: assign the boarder to gun1/gun2 (nearest free) and link them.</summary>
    public bool GunnerEnter(Entity vehicle, Entity player)
    {
        Entity? gun = null;
        Entity g1 = vehicle.VehGun1!, g2 = vehicle.VehGun2!;

        if (vehicle.VehGunner1 is null && vehicle.VehGunner2 is null
            && Time >= g1.VehPhase && Time >= g2.VehPhase)
        {
            // Pick the slot whose cannon mount is nearest the boarder.
            Vector3 v1 = VehiclePhysics.TagOrigin(vehicle, "cannon_right", new Vector3(0f, 128f, -16f));
            Vector3 v2 = VehiclePhysics.TagOrigin(vehicle, "cannon_left", new Vector3(0f, -128f, -16f));
            if (QMath.VLen(player.Origin - v1) < QMath.VLen(player.Origin - v2)) { gun = g1; vehicle.VehGunner1 = player; }
            else { gun = g2; vehicle.VehGunner2 = player; }
        }
        else if (vehicle.VehGunner1 is null && Time >= g1.VehPhase) { gun = g1; vehicle.VehGunner1 = player; }
        else if (vehicle.VehGunner2 is null && Time >= g2.VehPhase) { gun = g2; vehicle.VehGunner2 = player; }
        else return false; // full

        // Bind the player to the gun slot (passenger physics; the body drives it).
        player.Vehicle = gun;
        player.Angles = vehicle.Angles;
        player.TakeDamage = DamageMode.No;
        player.Solid = Solid.Not;
        player.MoveType = MoveType.None;
        player.Velocity = Vector3.Zero;
        player.ViewOfs = Vector3.Zero;
        player.Flags &= ~EntFlags.OnGround;
        // QC bumblebee_gunner_enter:334 — mirror the body's reload state onto the gunner (drives the aux-crosshair
        // reload color). The bumblebee body never sets vehicle_reload1, so this is 0 (always "ready"/green).
        player.VehicleReload1 = vehicle.VehicleReload1;
        gun.VehSlotPlayer = player;
        gun.Owner = vehicle;
        return true;
    }

    // void bumblebee_gunner_exit — bumblebee.qc: drop the gunner clear and free the slot.
    /// <summary>Port of <c>bumblebee_gunner_exit</c>: eject the gunner from a side-gun slot and free it.</summary>
    public void GunnerExit(Entity gun, bool eject)
    {
        Entity? vehic = gun.Owner ?? gun.VehSlotOwner;
        Entity? player = gun.VehSlotPlayer;
        if (vehic is null) return;

        // Free the seat record.
        QMath.AngleVectors(vehic.Angles, out Vector3 forward, out _, out Vector3 up);
        Vector3 right;
        if (player == vehic.VehGunner1) { vehic.VehGunner1 = null; QMath.AngleVectors(vehic.Angles, out _, out right, out _); }
        else { vehic.VehGunner2 = null; QMath.AngleVectors(vehic.Angles, out _, out right, out _); right = -right; }

        gun.VehSlotPlayer = null;
        gun.VehPhase = Time + 5f; // re-entry delay
        gun.VehGunnerLeadValid = false; // stop drawing this gun's aux crosshairs once the seat is empty
        gun.VehGunnerHitValid = false;

        if (player is not null)
        {
            player.TakeDamage = DamageMode.Aim;
            player.Solid = Solid.SlideBox;
            player.MoveType = MoveType.Walk;
            player.Vehicle = null;
            player.VehicleEnterDelay = Time + 2f;

            Vector3 spot = gun.Origin == default ? vehic.Origin : gun.Origin; // QC real_origin(gunner)
            spot += up * 128f + forward * 300f + right * 150f;
            spot = VehicleCommon.FindGoodExit(vehic, player, spot);

            player.Velocity = 0.75f * vehic.Velocity + QMath.Normalize(spot - vehic.Origin) * 200f + new Vector3(0, 0, 10f);
            if (Api.Services is not null) Api.Entities.SetOrigin(player, spot);
        }
        _ = eject;
    }

    /// <summary>
    /// Port of <c>bumblebee_gunner_frame</c>: a seated side-gunner — independently lock/lead a target, aim
    /// their cannon turret toward it (or the crosshair), and fire the plasma cannon from the gun's per-gun
    /// ammo pool. Driven each tick from <see cref="Think"/> while a gunner is aboard.
    /// </summary>
    public void GunnerFrame(Entity gun, Entity gunner, float dt)
    {
        Entity? vehic = gun.Owner ?? gun.VehSlotOwner;
        if (vehic is null) return;
        gun.Velocity = vehic.Velocity;

        // The two slots have mirrored turn limits.
        float inLimit, outLimit;
        if (gun == vehic.VehGun1) { inLimit = CannonTurnLimitIn; outLimit = CannonTurnLimitOut; }
        else { inLimit = CannonTurnLimitOut; outLimit = CannonTurnLimitIn; }

        TraceResult tr = VehiclePhysics.CrosshairTrace(vehic, gunner.VehInput.ViewAngles, gunner);
        Vector3 aimAt = tr.EndPos;

        // Per-gun lock + lead.
        if (CannonLock != 0)
        {
            // QC bumblebee.qc:113: drop the lock when expired/dead OR the locked enemy is FROZEN (freezetag).
            if (gun.VehLockTime < Time || (gun.Enemy is not null && (VehicleCommon.IsDead(gun.Enemy) || IsFrozen(gun.Enemy)))) gun.Enemy = null;
            Entity? te = tr.Ent;
            // QC bumblebee.qc:119: skip a FROZEN candidate when acquiring (don't lock onto a frozen enemy).
            if (te is not null && te.MoveType != MoveType.None && te.TakeDamage != DamageMode.No
                && !VehicleCommon.IsDead(te) && !IsFrozen(te))
            {
                bool diff = !VehiclePhysics.SameTeam(te, gunner) || vehic.Team == 0f;
                if (diff) { gun.Enemy = te; gun.VehLockTime = Time + (vehic.Team != 0f ? 2.5f : 0.5f); }
            }
            if (gun.Enemy is not null)
            {
                float distance = QMath.VLen(gun.Enemy.Origin - gun.Origin);
                float impact = distance / CannonSpeed;
                // QC bumblebee_gunner_frame: a ground (MOVETYPE_WALK) target's vertical velocity is damped *0.1
                // before leading (footstep bob shouldn't throw the aim up/down).
                Vector3 lead = gun.Enemy.Velocity;
                if (gun.Enemy.MoveType == MoveType.Walk) lead.Z *= 0.1f;
                aimAt = gun.Enemy.Origin + lead * impact; // lead

                // QC bumblebee.qc:153 — the magenta '1 0 1' LEAD aux crosshair at the predicted impact point.
                gun.VehGunnerLeadPoint = aimAt;
                gun.VehGunnerLeadValid = true;
            }
            else
            {
                gun.VehGunnerLeadValid = false;
            }
        }
        else
        {
            gun.VehGunnerLeadValid = false;
        }

        VehiclePhysics.AimTurret(vehic, aimAt, gun, "fire",
            -CannonPitchLimitDown, CannonPitchLimitUp, -outLimit, inLimit, CannonTurnSpeed, dt);

        // Fire the plasma cannon (per-gun ammo + refire gated).
        if (gunner.VehInput.ButtonAttack1 && Time > gun.VehAttackFinished && gun.VehicleEnergy >= CannonCost)
        {
            gun.VehicleEnergy -= CannonCost;
            FireCannon(vehic, gun, gunner);
            gun.VehWeaponDelay = Time; // QC: gun.delay = time (ammo regen pause)
            gun.VehAttackFinished = Time + CannonRefire;
        }

        // QC bumblebee_gunner_frame tail (VEHICLE_UPDATE_PLAYER_RESOURCE/VEHICLE_UPDATE_PLAYER): mirror the
        // body's 0..100 health/shield % + the gun's own cannon ammo % onto the GUNNER for their cockpit HUD.
        gunner.VehicleHealth = vehic.GetResource(ResourceType.Health) / StartHealth * 100f;
        if ((vehic.VehicleFlags & VehicleFlags.HasShield) != 0)
            gunner.VehicleShield = vehic.VehicleShield / MaxShield * 100f;
        gunner.VehicleAmmo1 = gun.VehicleEnergy / CannonAmmoMax * 100f; // QC this.vehicle_energy mirror
        gunner.VehicleEnergy = gun.VehicleEnergy / CannonAmmoMax * 100f;

        // QC bumblebee.qc:180-186 — the READY/reload aux crosshair (aux slot 0 on the gunner, slot 1/2 on the
        // pilot) at the cannon's STRAIGHT-LINE hit point, tinted red*reload1 + green*(1-reload1). Trace from the
        // "fire" tag straight ahead and publish the hit so both the gunner HUD and the pilot mirror can draw it.
        var (fireOrg, fireFwd) = VehiclePhysics.TagOriginForward(gun, "fire");
        Vector3 hit = fireOrg + fireFwd * 8192f;
        if (Api.Trace is not null)
        {
            TraceResult ft = Api.Trace.Trace(fireOrg, Vector3.Zero, Vector3.Zero, hit, MoveFilter.Normal, gun);
            hit = ft.EndPos;
        }
        gun.VehGunnerHitPoint = hit;
        gun.VehGunnerHitValid = true;
    }

    /// <summary>QC <c>STAT(FROZEN, e)</c> — true while a player is frozen (freezetag). Frozen targets cannot be
    /// locked, healed, or led; the bumblebee lock guards drop/skip them (bumblebee.qc:113/119/519/524).</summary>
    private static bool IsFrozen(Entity? e)
        => e is not null && (e.FrozenStat != 0
            || (StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(e, fz)));

    // ============================ WEAPONS ============================

    // void bumblebee_fire_cannon() — bumblebee_weapons.qc: a side-gunner plasma cannon shot.
    /// <summary>Fire one side-gunner plasma bolt from the gun's "fire" tag (negative force pulls victims in).</summary>
    public void FireCannon(Entity vehicle, Entity gun, Entity gunner)
    {
        var (org, fwd) = VehiclePhysics.TagOriginForward(gun, "fire");
        QMath.AngleVectors(QMath.VecToAngles(fwd), out Vector3 f, out Vector3 r, out Vector3 u);
        Vector3 vel = Prandom.Spread(f, r, u, CannonSpread) * CannonSpeed;

        // QC vehicles_projectile fires EFFECT_BIGPLASMA_MUZZLEFLASH at the muzzle (the projectile origin),
        // velocity = the bolt's velocity, count 1 — the networked Send_Effect that paints the plasma flash.
        EffectEmitter.Emit("BIGPLASMA_MUZZLEFLASH", org, vel, 1);

        VehicleCommon.SpawnProjectile(vehicle, gunner, org, vel,
            CannonDamage, CannonRadius, CannonForce, size: 0f,
            DeathTypes.VhBumbGun, health: 0f, lifetime: 0f, // bumblebee_weapons.qc: DEATH_VH_BUMB_GUN
            // QC bumblebee_weapons.qc passes SND_VEH_BUMBLEBEE_FIRE = W_Sound("flacexp3") = weapons/flacexp3.
            // The prior literal "vehicles/bumblebee_fire.wav" is a non-existent asset (silent cannon); the
            // real Base sample is weapons/flacexp3 (ships as weapons/flacexp3.ogg).
            fireSound: "weapons/flacexp3.wav");
        // TODO(port,client): the CSQCProjectile (PROJECTILE_BUMBLE_GUN) networked trail/model visual.
    }

    // bumblebee_pilot_frame() center-gun block — bumblebee.qc: the pilot's heal-ray / damage-ray.
    /// <summary>
    /// Fire the pilot's center gun: a hitscan beam to <see cref="RaygunRange"/> from the gun3 "fire" tag
    /// toward <paramref name="aimDir"/>. Damage mode hurts the target (energy-gated); heal mode tops up a
    /// friendly's health/armor and friendly vehicles' shields (teamplay-gated). One tick's effect.
    /// </summary>
    public void FireRay(Entity vehicle, Entity pilot, Vector3 aimPoint, float dt)
    {
        if (Api.Services is null) return;
        Vector3 start = VehiclePhysics.TagOrigin(vehicle.VehGun3 ?? vehicle, "fire", new Vector3(160f, 0f, 0f));
        Vector3 dir = QMath.Normalize(aimPoint - start);
        if (dir == Vector3.Zero) dir = QMath.Forward(vehicle.Angles);
        Vector3 end = start + dir * RaygunRange;
        TraceResult tr = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, vehicle);
        Entity? target = tr.Ent;

        // BRG_* beam: the visible heal/damage ray from gun3 to where it landed. Base's bumble_raygun_draw
        // tints by colormod = (count ? '1 0 0' : '0 1 0') — pure red in damage mode, pure green when healing.
        // Emitted each tick the ray is held; the short overlapping beams read as one continuous ray.
        if (RaygunDamage)
            EffectEmitter.TeBeam("damage_beam", start, tr.EndPos, new Vector3(1f, 0f, 0f));
        else
            EffectEmitter.TeHealBeam(start, tr.EndPos);

        if (target is null) return;

        if (RaygunDamage)
        {
            // Damage beam (QC: continuous Damage at dps*frametime, energy drain by aps).
            Combat.Damage(target, vehicle, pilot, RaygunDps * FrameTime, DeathTypes.Generic,
                tr.EndPos, dir * RaygunFps * FrameTime);
            vehicle.VehicleEnergy -= RaygunAps * FrameTime;
        }
        else if (!VehicleCommon.IsDead(target))
        {
            // Heal beam (teamplay-gated): top up a teammate (or anyone in FFA).
            bool friendly = (vehicle.Team != 0f && VehiclePhysics.SameTeam(target, vehicle)) || vehicle.Team == 0f;
            if (!friendly) return;

            if (HealgunHps > 0f)
            {
                // QC: hplimit = IS_PLAYER ? healgun_hmax : RES_LIMIT_NONE; Heal() = GiveResourceWithLimit(Health),
                // which leaves RES_LIMIT_NONE uncapped — so a non-player (e.g. a friendly vehicle's health pool)
                // tops up unbounded rather than being clamped to its MaxHealth.
                float hpLimit = (target.Flags & EntFlags.Client) != 0 ? HealgunHmax : Resources.LimitNone;
                target.GiveResourceWithLimit(ResourceType.Health, HealgunHps * dt, hpLimit);
            }

            // Friendly vehicle: top up its shield instead of armor.
            if ((target.VehicleFlags & VehicleFlags.IsVehicle) != 0)
            {
                if (HealgunSps > 0f && target.GetResource(ResourceType.Health) <= target.MaxHealth)
                    target.VehicleShield = MathF.Min(target.VehicleShield + HealgunSps * dt, MaxShieldOf(target));
            }
            else if ((target.Flags & EntFlags.Client) != 0)
            {
                // QC bumblebee.qc:598: when instagib is on the armor "lives" cap is g_instagib_extralives
                // (the gunner tops players up to their extra-life count), else the normal healgun amax.
                float maxArmor = InstagibEnabled() ? Cvar("g_instagib_extralives", 0f) : HealgunAmax;
                if (HealgunAps > 0f && target.GetResource(ResourceType.Armor) <= maxArmor)
                    target.GiveResourceWithLimit(ResourceType.Armor, HealgunAps * dt, maxArmor);
            }
        }
    }

    /// <summary>The shield cap of a friendly vehicle being healed (QC tur_head.max_health == its full shield).</summary>
    private static float MaxShieldOf(Entity target)
        => target.VehicleDef is Bumblebee bb ? bb.MaxShield
         : target.VehicleDef is Raptor rp ? rp.MaxShield
         : target.VehicleDef is Spiderbot sp ? sp.MaxShield
         : target.VehicleDef is Racer rc ? rc.MaxShield
         : 200f;

    // ---- slot/sub construction ----
    private Entity NewSlot(Entity vehicle, int index, string tag)
    {
        Entity g = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
        g.ClassName = "vehicle_playerslot";
        g.Owner = vehicle;
        g.VehSlotOwner = vehicle;
        g.VehSlotIndex = index;
        g.VehicleFlags |= VehicleFlags.PlayerSlot;
        g.VehicleEnergy = CannonAmmoMax;
        if (Api.Services is not null) Api.Models.SetAttachment(g, vehicle, tag);
        return g;
    }

    // QC setmodel(gun, MDL_VEH_BUMBLEBEE_CANNON_*) — assign a cosmetic cannon model to a gun sub-entity so it
    // renders attached at its tag. Used at spawn (live guns) and reused as the gib model at death.
    private static void SetGunModel(Entity gun, string model)
    {
        gun.Model = model;
        if (Api.Services is not null) Api.Entities.SetModel(gun, model);
    }

    private Entity NewSub(Entity vehicle, string cls, string tag)
    {
        Entity e = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
        e.ClassName = cls;
        e.Owner = vehicle;
        e.VehSlotOwner = vehicle;
        if (Api.Services is not null) Api.Models.SetAttachment(e, vehicle, tag);
        return e;
    }

    private static bool AllowBots()
        => Api.Services is not null && Api.Cvars.GetFloat("g_vehicles_allow_bots") != 0f;

    /// <summary>QC <c>MUTATOR_IS_ENABLED(mutator_instagib)</c> — resolves to <c>g_instagib != 0</c> (the headline arena cvar).</summary>
    private static bool InstagibEnabled()
        => Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f;

    /// <summary>Read a float cvar through the facade, falling back to <paramref name="fallback"/> when unset / no services.</summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;
    private static float FrameTime => Api.Services is not null ? Api.Clock.FrameTime : 0.05f;
}

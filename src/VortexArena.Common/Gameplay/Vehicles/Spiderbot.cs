// Port: qcsrc/common/vehicles/vehicle/spiderbot.{qh,qc} + spiderbot_weapons.{qh,qc}
//
// The Spiderbot — a single-seat bipedal walker. It walks/strafes on legs (movelib_groundalign4point 4-point
// ground alignment), turns its head turret toward the crosshair, can jump from great heights protecting the
// rider, and mounts twin hitscan miniguns (primary, alternating barrels, heat/ammo belt) plus a 3-mode
// rocket launcher: volley / guided / artillery (secondary). Balance values are the cfg defaults inlined
// below (g_vehicle_spiderbot_*).
//
// FULL deep behavior is implemented: the 4-point leg ground alignment, the head turn/pitch turret aim, the
// directional jump + landing, the minigun (alternating gun1/gun2 with spread + solid penetration + heat),
// and ALL THREE rocket modes with guidance (volley salvo of 9 with reload, crosshair-homing guided rockets
// with guide-release, and the ballistic artillery solve). Only client-only items (muzzle FX, gun-spin
// cosmetics, aux crosshairs, HUD %, gib models) stay TODO.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The spiderbot's secondary rocket modes (QC SBRM_* — spiderbot.qc).</summary>
public enum SpiderbotRocketMode
{
    Volley = 1,     // SBRM_VOLLY
    Guided = 2,     // SBRM_GUIDE
    Artillery = 3,  // SBRM_ARTILLERY
}

/// <summary>
/// The Spiderbot vehicle (QC <c>CLASS(Spiderbot, Vehicle)</c>). Registered via <see cref="VehicleAttribute"/>.
/// </summary>
[Vehicle]
public sealed class Spiderbot : Vehicle
{
    // ---- chassis (spiderbot.qc autocvars) ----
    public float SpeedStop = 50f;      // g_vehicle_spiderbot_speed_stop (brake target)
    public float SpeedStrafe = 400f;   // g_vehicle_spiderbot_speed_strafe
    public float SpeedWalk = 500f;     // g_vehicle_spiderbot_speed_walk
    public float TurnSpeed = 90f;      // g_vehicle_spiderbot_turnspeed
    public float TurnSpeedStrafe = 300f; // g_vehicle_spiderbot_turnspeed_strafe
    public float MovementInertia = 0.15f; // g_vehicle_spiderbot_movement_inertia
    public float SpringLength = 150f;  // g_vehicle_spiderbot_springlength
    public float SpringUp = 20f;       // g_vehicle_spiderbot_springup
    public float SpringBlend = 0.1f;   // g_vehicle_spiderbot_springblend
    public float TiltLimit = 90f;      // g_vehicle_spiderbot_tiltlimit
    public float HeadPitchLimitDown = -20f; // g_vehicle_spiderbot_head_pitchlimit_down
    public float HeadPitchLimitUp = 30f;    // g_vehicle_spiderbot_head_pitchlimit_up
    public float HeadTurnLimit = 90f;       // g_vehicle_spiderbot_head_turnlimit
    public float HeadTurnSpeed = 110f;      // g_vehicle_spiderbot_head_turnspeed
    public float ThinkRate = VehicleCommon.DefaultThinkRate;

    // ---- resources ----
    public float MaxShield = 200f;          // g_vehicle_spiderbot_shield
    public float ShieldRegen = 25f;         // g_vehicle_spiderbot_shield_regen
    public float ShieldRegenPause = 0.35f;  // g_vehicle_spiderbot_shield_regen_pause
    public float HealthRegen = 10f;         // g_vehicle_spiderbot_health_regen
    public float HealthRegenPause = 5f;     // g_vehicle_spiderbot_health_regen_pause
    public float RespawnTime = 45f;         // g_vehicle_spiderbot_respawntime

    // ---- minigun (spiderbot_weapons.qh) ----
    public float MinigunDamage = 16f;       // g_vehicle_spiderbot_minigun_damage
    public float MinigunRefire = 0.06f;     // g_vehicle_spiderbot_minigun_refire
    public float MinigunSpread = 0.012f;    // g_vehicle_spiderbot_minigun_spread
    public float MinigunForce = 9f;         // g_vehicle_spiderbot_minigun_force
    public float MinigunSolidPenetration = 32f; // g_vehicle_spiderbot_minigun_solidpenetration
    public float MinigunAmmoCost = 1f;      // g_vehicle_spiderbot_minigun_ammo_cost
    public float MinigunAmmoMax = 100f;     // g_vehicle_spiderbot_minigun_ammo_max
    public float MinigunAmmoRegen = 40f;    // g_vehicle_spiderbot_minigun_ammo_regen
    public float MinigunAmmoRegenPause = 1f; // g_vehicle_spiderbot_minigun_ammo_regen_pause

    // ---- rocket (spiderbot_weapons.qh) ----
    public float RocketDamage = 50f;     // g_vehicle_spiderbot_rocket_damage
    public float RocketForce = 150f;     // g_vehicle_spiderbot_rocket_force
    public float RocketRadius = 250f;    // g_vehicle_spiderbot_rocket_radius
    public float RocketSpeed = 3500f;    // g_vehicle_spiderbot_rocket_speed
    public float RocketSpread = 0.05f;   // g_vehicle_spiderbot_rocket_spread
    public float RocketRefire = 0.1f;    // g_vehicle_spiderbot_rocket_refire
    public float RocketRefire2 = 0.025f; // g_vehicle_spiderbot_rocket_refire2 (volley)
    public float RocketReload = 4f;      // g_vehicle_spiderbot_rocket_reload
    public float RocketHealth = 100f;    // g_vehicle_spiderbot_rocket_health (shootable)
    public float RocketNoise = 0.2f;     // g_vehicle_spiderbot_rocket_noise
    public float RocketTurnRate = 0.25f; // g_vehicle_spiderbot_rocket_turnrate
    public float RocketLifetime = 20f;   // g_vehicle_spiderbot_rocket_lifetime

    public Spiderbot()
    {
        NetName = "spiderbot";
        DisplayName = "Spiderbot";
        Model = "models/vehicles/spiderbot.dpm";
        StartHealth = 800f;               // g_vehicle_spiderbot_health
    }

    // METHOD(Spiderbot, vr_spawn) — spiderbot.qc
    public override void Spawn(Entity vehicle)
    {
        VehicleCommon.SpawnVehicle(vehicle, this);

        if (Model is not null && Api.Services is not null)
            Api.Entities.SetModel(vehicle, Model);

        // The head turret + two gun barrels (sub-entities the aim/fire code uses). Created once.
        vehicle.TurHead ??= NewSub(vehicle, "spiderbot_top", vehicle);
        vehicle.VehGun1 ??= NewSub(vehicle, "spiderbot_gun", vehicle.TurHead);
        vehicle.VehGun2 ??= NewSub(vehicle, "spiderbot_gun", vehicle.TurHead);

        vehicle.MaxHealth = StartHealth;
        vehicle.SetResourceExplicit(ResourceType.Health, StartHealth);
        vehicle.VehicleShield = MaxShield;
        vehicle.VehicleAmmo1 = MinigunAmmoMax; // start with a full minigun belt
        vehicle.RespawnTime = RespawnTime;
        vehicle.MoveType = MoveType.Step;   // QC: MOVETYPE_STEP (a walker)
        vehicle.Solid = Solid.SlideBox;
        vehicle.Gravity = 2f;               // QC: instance.gravity = 2
        vehicle.DeadState = DeadFlag.No;
        vehicle.DamageForceScale = 0.03f;   // QC vr_spawn: instance.damageforcescale = 0.03
        vehicle.Mass = 5000f;               // QC vr_spawn: instance.mass = 5000 (scales push/knockback forces)
        // NOTE: do NOT reset Touch here. Base spiderbot vr_spawn does not settouch func_null
        // (that appears only in vr_death). SpawnVehicle installs the shared crush/touch handler;
        // nulling it would disable run-over crush on the ground walker most likely to do it.
        if (vehicle.TurHead is not null) vehicle.TurHead.Angles = Vector3.Zero;
        vehicle.VehRocketBelt = 1; // QC: tur_head.frame = 1

        // Hitbox: '-75 -75 10' .. '75 75 125' (spiderbot.qh).
        if (Api.Services is not null)
            Api.Entities.SetSize(vehicle, new Vector3(-75f, -75f, 10f), new Vector3(75f, 75f, 125f));

        vehicle.VehicleFlags |= VehicleFlags.HasShield | VehicleFlags.MoveGround | VehicleFlags.DmgShake;
        if (ShieldRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.ShieldRegen;
        if (HealthRegen > 0f) vehicle.VehicleFlags |= VehicleFlags.HealthRegen;

        // QC vr_setup: instance.pushable = true — the spiderbot can use jumppads. isPushable (triggers.qc)
        // checks .pushable BEFORE the IS_VEHICLE exclusion, so this opts the walker into jumppad/conveyor pushes
        // that vehicles are otherwise denied.
        vehicle.PushableFlag = true;

        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;

        // TODO(port,client): qcsrc/common/vehicles/vehicle/spiderbot.qc vr_spawn — gun barrel hardpoint
        //                    cosmetic attachment, tur_head model frames.
    }

    // METHOD(Spiderbot, vr_impact) — spiderbot.qc
    /// <summary>
    /// Port of <c>vr_impact</c>: on a hard collision (the velocity change exceeds the bouncepain minspeed)
    /// deal DEATH_FALL self-damage to the airframe, scaled by the speed delta and capped at maxpain.
    /// Bouncepain <c>'0 0 0'</c> (default) = minspeed 0, factor 0, max 0 => no-op; when set, applies
    /// <c>min(speedfac * speedDelta, maxpain)</c> damage, debounced 0.25s. Dispatched from the shared
    /// <c>vehicles_touch</c> path (<see cref="VehicleCommon.Touch"/> -> <see cref="VehicleCommon.Impact"/>).
    /// </summary>
    public override void Impact(Entity vehicle)
    {
        // QC vr_impact: `if (autocvar_g_vehicle_spiderbot_bouncepain) vehicles_impact(...)` — the vector cvar
        // guards the call (a QC vector is truthy iff any component is non-zero). The stock default '0 0 0' is
        // falsy, so QC SKIPS vehicles_impact entirely (no debounce stamp, no zero-damage call). Mirror that
        // with the same hardcoded-default convention as the sibling ports (Racer/Bumblebee inline their
        // non-zero defaults; the spiderbot's is '0 0 0', so the guard is always false and this is a no-op).
        Vector3 bouncepain = Vector3.Zero; // g_vehicle_spiderbot_bouncepain default '0 0 0' (minspeed, speedfac, maxpain)
        if (bouncepain != Vector3.Zero)
            VehicleCommon.Impact(vehicle, bouncepain.X, bouncepain.Y, bouncepain.Z);
    }

    // METHOD(Spiderbot, vr_enter) — spiderbot.qc
    public override void Enter(Entity vehicle, Entity player)
    {
        VehicleCommon.EnterVehicle(vehicle, player);
        vehicle.MoveType = MoveType.Step; // QC: MOVETYPE_STEP
        vehicle.VehW2Mode = (int)SpiderbotRocketMode.Guided; // QC default SBRM_GUIDE

        // QC vr_enter (spiderbot.qc:552-556): a pilot carrying a CTF flag has it ride the head turret.
        // (player.GtCarried is the QC .flagcarried back-link — promoted on Entity in EntityGametypeState.cs.)
        Entity? flag = player.GtCarried;
        if (flag is not null && vehicle.TurHead is not null && Api.Services is not null)
        {
            Api.Models.SetAttachment(flag, vehicle.TurHead, "");
            Api.Entities.SetOrigin(flag, new Vector3(-20f, 0f, 120f));
        }

        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;
    }

    // void spiderbot_exit(entity this, int eject) — spiderbot.qc
    public override void Exit(Entity vehicle, Entity player)
    {
        // QC: any in-flight guided rockets owned by this pilot lose their guidance (owner cleared).
        if (Api.Services is not null)
            foreach (Entity proj in Api.Entities.FindByClass("vehicles_projectile"))
                if (proj.ClassName == "spiderbot_rocket" && proj.Owner == vehicle)
                {
                    proj.DmgInflictor = player;
                    proj.Owner = null;
                    if (proj.VehGuideMode == (int)VehiclePhysics.GuideMode.SpiderGuided)
                        proj.VehGuideMode = (int)VehiclePhysics.GuideMode.SpiderUnguided;
                }

        QMath.AngleVectors(vehicle.Angles, out Vector3 forward, out _, out Vector3 up);
        Vector3 spot;
        bool dying = VehicleCommon.IsDead(vehicle);
        if (dying)
        {
            spot = vehicle.Origin + forward * 100f + new Vector3(0, 0, 64f);
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
            player.Velocity = (up + forward * 0.25f) * 750f;
        }
        else if (QMath.VLen(vehicle.Velocity) > SpeedStrafe)
        {
            player.Velocity = QMath.Normalize(vehicle.Velocity) * QMath.VLen(vehicle.Velocity) + new Vector3(0, 0, 200f);
            spot = vehicle.Origin + forward * 128f + new Vector3(0, 0, 64f);
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
        }
        else
        {
            player.Velocity = vehicle.Velocity * 0.5f + new Vector3(0, 0, 10f);
            spot = vehicle.Origin + forward * 256f + new Vector3(0, 0, 64f);
            spot = VehicleCommon.FindGoodExit(vehicle, player, spot);
        }

        VehicleCommon.ExitVehicle(vehicle, player, dying ? VehicleExitFlag.Eject : VehicleExitFlag.Normal);
        if (Api.Services is not null)
            Api.Entities.SetOrigin(player, spot);

        vehicle.MoveType = MoveType.Step;
        vehicle.Think = self => Think(self);
        vehicle.NextThink = Time;
    }

    // spiderbot_frame() + METHOD(Spiderbot, vr_think) dispatcher — spiderbot.qc.
    public override void Think(Entity vehicle)
    {
        float dt = ThinkRate;
        vehicle.NextThink = Time + dt;

        if (VehicleCommon.FreezeIfGameStopped(vehicle))
            return;

        Entity? player = vehicle.Owner;

        if (player is not null && !VehicleCommon.IsDead(vehicle))
        {
            Frame(vehicle, player, vehicle.VehInput, dt);
        }
        else if (vehicle.OnGround)
        {
            // QC vr_think: an idle/empty spiderbot brakes to a stop on the ground.
            VehiclePhysics.BrakeSimple(vehicle, SpeedStop);
        }

        // Minigun belt regen (QC: regen when not firing).
        if (!(player is not null && player.VehInput.ButtonAttack1))
            vehicle.VehicleAmmo1 = VehicleCommon.Regen(vehicle, vehicle.VehicleAmmo1, vehicle.VehRegenPauseTime,
                MinigunAmmoMax, MinigunAmmoRegenPause, MinigunAmmoRegen, dt, healthScale: false);

        if ((vehicle.VehicleFlags & VehicleFlags.ShieldRegen) != 0)
            vehicle.VehicleShield = VehicleCommon.Regen(vehicle, vehicle.VehicleShield, vehicle.DmgTime,
                MaxShield, ShieldRegenPause, ShieldRegen, dt, healthScale: true);
        if ((vehicle.VehicleFlags & VehicleFlags.HealthRegen) != 0)
            VehicleCommon.RegenResource(vehicle, vehicle.DmgTime, StartHealth, HealthRegenPause, HealthRegen, dt, false, ResourceType.Health);

        if (player is not null)
        {
            // QC: glue the pilot to the top of the chassis (origin + maxs.z) and match velocity.
            if (Api.Services is not null)
                Api.Entities.SetOrigin(player, vehicle.Origin + new Vector3(0, 0, vehicle.Maxs.Z));
            player.OldOrigin = player.Origin;
            player.Velocity = vehicle.Velocity;

            // QC vehicles_regen mirrors owner.(regen_field) = (pool/max)*100 onto the pilot so the in-vehicle
            // HUD shows the live shield + minigun-belt gauges (RegenResource already mirrors health). Without
            // these the spiderbot's HUD shield + ammo bars read stale/zero. Mirror every tick (matching the
            // Racer/Bumblebee pilot mirror).
            player.VehicleShield = vehicle.VehicleShield / MaxShield * 100f;
            player.VehicleAmmo1 = vehicle.VehicleAmmo1 / MinigunAmmoMax * 100f;
        }

        // QC vehicles_think: vehicles_painframe(this) runs after vr_think every tick — low-health smoke + jitter.
        VehicleCommon.PainFrame(vehicle);
    }

    /// <summary>
    /// Port of <c>spiderbot_frame</c> (SVQC half): head turret aim, 4-point leg ground alignment, directional
    /// jump + landing, body turn while moving, walk/strafe locomotion with gravity, the minigun, and the
    /// 3-mode rocket launcher.
    /// </summary>
    public void Frame(Entity vehicle, Entity player, in MovementInput input, float dt)
    {
        if (VehicleCommon.IsDead(vehicle) || vehicle.TurHead is null) return;
        Entity head = vehicle.TurHead;
        Vector3 move = input.MoveValues;

        // --- head turret aim toward the crosshair (turn + pitch within limits) -------------------------
        TraceResult tr = VehiclePhysics.CrosshairTrace(vehicle, input.ViewAngles, vehicle);
        // QC averages the two gun hardpoints (tag_hardpoint01 + tag_hardpoint02) for the aim muzzle point.
        Vector3 muzzle = 0.5f * (VehiclePhysics.TagOrigin(head, "tag_hardpoint01", new Vector3(60f, 0f, 20f))
            + VehiclePhysics.TagOrigin(head, "tag_hardpoint02", new Vector3(60f, 0f, 20f)));
        Vector3 wantAng = QMath.VecToAngles(QMath.Normalize(tr.EndPos - muzzle));
        Vector3 delta = VehiclePhysics.ShortAngles(new Vector3(
            wantAng.X - vehicle.Angles.X - head.Angles.X,
            wantAng.Y - vehicle.Angles.Y - head.Angles.Y, 0f));

        // QC steps slew by head_turnspeed * PHYS_INPUT_FRAMETIME EVERY movement frame; the port's Think runs
        // at the 0.1s cadence, so the elapsed time since the last call is `dt` — step by that, NOT FrameTime,
        // or the head tracks ~6x too slow (TicRate/0.1).
        float ftmp = HeadTurnSpeed * dt;
        float dy = QMath.Bound(-ftmp, delta.Y, ftmp);
        Vector3 ha = head.Angles;
        ha.Y = QMath.Bound(-HeadTurnLimit, ha.Y + dy, HeadTurnLimit);
        float dx = QMath.Bound(-ftmp, delta.X, ftmp);
        ha.X = QMath.Bound(HeadPitchLimitDown, ha.X + dx, HeadPitchLimitUp);
        head.Angles = ha;

        // --- 4-point leg ground alignment --------------------------------------------------------------
        QMath.AngleVectors(vehicle.Angles + new Vector3(-2f, 0f, 0f) * vehicle.Angles.X, out Vector3 forward, out Vector3 right, out Vector3 up);
        VehiclePhysics.GroundAlign4Point(vehicle, forward, right, up, SpringLength, SpringUp, SpringBlend, TiltLimit);

        if (vehicle.OnGround) vehicle.VehJumpDelay = Time; // reset so movement can begin

        // --- landing (frame 4 airborne -> 5 idle on touchdown) -----------------------------------------
        // QC: IS_ONGROUND && frame == 4 && tur_head.wait != 0 -> play SND_VEH_SPIDERBOT_LAND, frame = 5.
        // VehLandTime mirrors tur_head.wait (set to time+2 on jump); != 0 guards the fresh-spawn case.
        if (vehicle.OnGround && vehicle.Frame == 4f && vehicle.VehLandTime != 0f)
        {
            // QC CH_TRIGGER_SINGLE — matches the jump sound's channel mapping on this vehicle.
            if (Api.Services is not null) Api.Sound.Play(vehicle, SoundChannel.Auto, "vehicles/spiderbot_land.wav");
            vehicle.Frame = 5f;
        }

        // --- jump (directional launch from the wishmove) -----------------------------------------------
        if (!input.ButtonJump) vehicle.VehJumpLatched = false;

        if (vehicle.OnGround && input.ButtonJump && !vehicle.VehJumpLatched && vehicle.VehLandTime < Time)
        {
            vehicle.VehLandTime = Time + 2f;
            vehicle.VehJumpDelay = Time + 2f;
            vehicle.VehJumpLatched = true;

            Vector3 movefix = new(MathF.Sign(move.X), MathF.Sign(move.Y), 0f);
            Vector3 sd = movefix.X * forward;
            Vector3 rt = movefix.Y * right;
            if (movefix.X == 0f && movefix.Y == 0f) sd = forward; // always jump forward by default

            vehicle.Flags &= ~EntFlags.OnGround;
            vehicle.Velocity = sd * 700f + rt * 600f + up * 600f;
            vehicle.Frame = 4f; // QC: vehic.frame = 4 (airborne) — the land branch resets it to 5 on touchdown.
            if (Api.Services is not null) Api.Sound.Play(vehicle, SoundChannel.Auto, "vehicles/spiderbot_jump.wav");
        }
        else if (Time >= vehicle.VehJumpDelay)
        {
            if (move == Vector3.Zero)
            {
                if (vehicle.OnGround)
                    VehiclePhysics.BrakeSimple(vehicle, SpeedStop);
            }
            else
            {
                // Turn the body toward the head's yaw (faster when strafing). Step by the elapsed `dt`
                // (the 0.1s think cadence), NOT FrameTime — QC steps by PHYS_INPUT_FRAMETIME every frame.
                float turn = (move.X == 0f && move.Y != 0f ? TurnSpeedStrafe : TurnSpeed) * dt;
                turn = QMath.Bound(-turn, head.Angles.Y, turn);
                Vector3 ba = vehicle.Angles; ba.Y = VehiclePhysics.AngleMods(ba.Y + turn); vehicle.Angles = ba;
                ha = head.Angles; ha.Y -= turn; head.Angles = ha;

                float oldZ = vehicle.Velocity.Z;
                if (move.X != 0f)
                {
                    VehiclePhysics.MoveSimple(vehicle, QMath.Normalize(forward * MathF.Sign(move.X)), SpeedWalk, MovementInertia);
                }
                else // move.Y != 0
                {
                    VehiclePhysics.MoveSimple(vehicle, QMath.Normalize(right * MathF.Sign(move.Y)), SpeedStrafe, MovementInertia);
                }
                Vector3 v = vehicle.Velocity; v.Z = oldZ; vehicle.Velocity = v;
                if (vehicle.Velocity.Z <= 20f) // gravity while not jumping
                {
                    // QC: g = (sv_gameplayfix_gravityunaffectedbyticrate ? 0.5 : 1); step by `dt` (see above).
                    float g = GravityTicrateFactor();
                    v = vehicle.Velocity; v.Z -= g * dt * Gravity(); vehicle.Velocity = v;
                }
            }
        }

        // Clamp tilt within limits.
        Vector3 ang = vehicle.Angles;
        ang.X = QMath.Bound(-TiltLimit, ang.X, TiltLimit);
        ang.Z = QMath.Bound(-TiltLimit, ang.Z, TiltLimit);
        vehicle.Angles = ang;

        // --- minigun (alternating barrels, heat/ammo gated) --------------------------------------------
        if (input.ButtonAttack1)
        {
            vehicle.VehRegenPauseTime = Time;
            if (vehicle.VehicleAmmo1 >= MinigunAmmoCost && vehicle.VehAttackFinished <= Time)
            {
                FireMinigun(vehicle, player);
                vehicle.VehicleAmmo1 -= MinigunAmmoCost;
                vehicle.VehAttackFinished = Time + MinigunRefire;
            }
        }
        // Networked client-render flag: minigun firing → the client spins the barrels + pops the muzzle flash.
        if (input.ButtonAttack1) vehicle.Effects |= VehicleEffects.Firing;
        else vehicle.Effects &= ~VehicleEffects.Firing;

        // --- rocket launcher (3 modes + guide-release) -------------------------------------------------
        RocketDo(vehicle, player, input);

        // TODO(port,client): qcsrc/common/vehicles/vehicle/spiderbot.qc spiderbot_frame — walk/strafe/idle
        //                    engine sounds, gun barrel spin, EFFECT_SPIDERBOT_MINIGUN_MUZZLEFLASH, aux
        //                    crosshairs, vehicle_ammo2/reload2 HUD %. (jump/land sounds now played server-side.)
    }

    // METHOD(Spiderbot, vr_death) — spiderbot.qc
    public override void Death(Entity vehicle)
    {
        vehicle.SetResourceExplicit(ResourceType.Health, 0f);
        vehicle.TakeDamage = DamageMode.No;
        vehicle.DeadState = DeadFlag.Dying;
        vehicle.MoveType = MoveType.Toss;
        vehicle.Touch = null;

        // QC spiderbot_blowup: small explosions for ~3.4s, then a 250-dmg radius blast then respawn.
        float when = Time + 3.4f + Prandom.Range(0f, 2f);
        vehicle.Think = self =>
        {
            if (Time >= when)
            {
                // QC spiderbot_blowup (spiderbot.qc:408-458): spawn the 4 physics gib entities (head/2 guns/body)
                // BEFORE the radius blast, then deal damage and hide the originals. The flung head runs
                // spiderbot_headfade, which emits the EFFECT_EXPLOSION_BIG + SND_ROCKET_IMPACT on its own final
                // fade (not at the main blast — so this no longer pops EXPLOSION_BIG here).
                SpawnDeathGibs(self);

                // spiderbot.qc spiderbot_blowup: the final death blast is DEATH_VH_SPID_DEATH.
                WeaponSplash.RadiusDamage(self, self.Origin, 250f, 15f, 250f, self.Enemy, 0, 250f, deathTag: DeathTypes.VhSpidDeath);

                self.DeadState = DeadFlag.Dead;
                self.MoveType = MoveType.None;
                self.Solid = Solid.Not;
                self.Velocity = Vector3.Zero;
                if (Api.Services is not null) Api.Entities.SetOrigin(self, self.SpawnPos);
                self.NextThink = Time + RespawnTime;
                self.Think = s => Spawn(s);
            }
            else
            {
                self.NextThink = Time + 0.1f;
                // QC spiderbot_blowup: ~10% per 0.1s tick, a small explosion + impact sound during the burn.
                // sound(this, CH_SHOTS, SND_ROCKET_IMPACT) + Send_Effect(EFFECT_EXPLOSION_SMALL,
                // randomvec()*80 + (origin + '0 0 100'), '0 0 0', 1).
                if (Prandom.Float() < 0.1f)
                {
                    if (Api.Services is not null) Api.Sound.Play(self, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav");
                    EffectEmitter.Emit("EXPLOSION_SMALL", Prandom.Vec() * 80f + (self.Origin + new Vector3(0f, 0f, 100f)), Vector3.Zero, 1);
                }
            }
        };
        vehicle.NextThink = Time;
        // The 4 head/gun/body physics gib entities (head EF_FLAME bounce + spiderbot_headfade, 2 tossed guns
        // v_forward*700, body frame 11) are now spawned server-side in SpawnDeathGibs below — matching QC
        // spiderbot_blowup (spiderbot.qc:408-458). The per-burn EXPLOSION_SMALL + impact sound (above) and the
        // head's final EXPLOSION_BIG + SND_ROCKET_IMPACT (HeadFade) are authoritative server effects.
        // TODO(port,client): the head's MF_ROCKET modelflag (rocket-smoke-trail render) is a client-only
        //                    modelflag with no port Entity field; EF_FLAME (the burning glow) IS set.
    }

    /// <summary>
    /// Port of the gib-spawn tail of <c>spiderbot_blowup</c> (spiderbot.qc:408-458): scatter 4 physics debris
    /// entities off the dying walker — the body (frame 11, in place), the head (MOVETYPE_BOUNCE, EF_FLAME, flung
    /// up, fades + explodes via its own headfade think), and the two guns (MOVETYPE_TOSS, flung forward). All
    /// darkened (<c>colormod '-2 -2 -2'</c>) and faded out. Pure presentation/physics — a gib deals no damage.
    /// </summary>
    private void SpawnDeathGibs(Entity vehic)
    {
        if (Api.Services is null) return;

        // QC: makevectors is implied via the body angles; the guns are tossed along v_forward, the head up.
        QMath.AngleVectors(vehic.Angles, out Vector3 forward, out _, out Vector3 _up);
        Vector3 dark = new(-2f, -2f, -2f);             // QC: colormod = '-2 -2 -2'
        float fadeGun = MathF.Min(RespawnTime, 10f);   // QC: SUB_SetFade(g, time, min(respawntime, 10))

        // --- body (frame 11, stays at the vehicle origin, fades after 5s) ------------------------------
        Entity b = Api.Entities.Spawn();
        b.ClassName = "spiderbot_body";
        b.Model = "models/vehicles/spiderbot.dpm"; // MDL_VEH_SPIDERBOT_BODY
        Api.Entities.SetModel(b, b.Model);
        Api.Entities.SetOrigin(b, vehic.Origin);
        b.Frame = 11f;                                  // QC: b.frame = 11
        b.Angles = vehic.Angles;
        Api.Entities.SetSize(b, vehic.Mins, vehic.Maxs);
        b.ColorModKey = dark;
        FadeGib(b, Time + 5f, MathF.Min(RespawnTime, 1f)); // QC: SUB_SetFade(b, time+5, min(respawntime,1))

        // --- head (MOVETYPE_BOUNCE, EF_FLAME, flung up + spinning, runs HeadFade) ----------------------
        Entity h = Api.Entities.Spawn();
        h.ClassName = "spiderbot_top";
        h.Model = "models/vehicles/spiderbot_top.dpm"; // MDL_VEH_SPIDERBOT_TOP
        Api.Entities.SetModel(h, h.Model);
        h.Solid = Solid.BBox;                           // QC: SOLID_BBOX (set before setorigin)
        Api.Entities.SetOrigin(h, VehiclePhysics.TagOrigin(vehic, "tag_head"));
        h.MoveType = MoveType.Bounce;                   // QC: MOVETYPE_BOUNCE
        h.Velocity = _up * (500f + Prandom.Float() * 500f) + Prandom.Vec() * 128f;
        h.Effects |= EfFlame;                           // QC: effects = EF_FLAME | EF_LOWPRECISION (MF_ROCKET = client modelflag, not modeled)
        h.AVelocity = Prandom.Vec() * 360f;
        h.Alpha = 1f;
        h.ColorModKey = dark;
        // QC spiderbot_headfade closure state: cnt = time + 3.5*random() (death deadline), fade_rate = 1/min(respawntime,10),
        // fade_time = time. Held in locals (HeadFade is the sole reader and re-arms itself), so no new entity fields.
        float headDeadline = Time + 3.5f * Prandom.Float();
        float headFadeRate = 1f / MathF.Min(RespawnTime, 10f);
        float headFadeTime = Time;
        void HeadFade(Entity self)
        {
            self.NextThink = headFadeTime;
            self.Alpha = 1f - (Time - headFadeTime) * headFadeRate;
            if (headDeadline < Time || self.Alpha < 0.1f)
            {
                if (self.Alpha > 0.1f)
                {
                    // QC: sound(CH_SHOTS, SND_ROCKET_IMPACT); Send_Effect(EFFECT_EXPLOSION_BIG, origin + '0 0 100', ...).
                    Api.Sound.Play(self, SoundChannel.ShotsAuto, "weapons/rocket_impact.wav");
                    EffectEmitter.Emit("EXPLOSION_BIG", self.Origin + new Vector3(0f, 0f, 100f), Vector3.Zero, 1);
                }
                Api.Entities.Remove(self);
            }
        }
        h.Think = HeadFade;
        h.NextThink = Time;

        // --- two guns (MOVETYPE_TOSS, flung forward + spinning, fade) ----------------------------------
        SpawnGunGib("tag_hardpoint01", vehic, forward, dark, fadeGun);
        SpawnGunGib("tag_hardpoint02", vehic, forward, dark, fadeGun);

        // QC: hide the originals (alpha = -1) — the death scene is the gibs from here on.
        vehic.Alpha = -1f;
        if (vehic.TurHead is not null) vehic.TurHead.Alpha = -1f;
        if (vehic.VehGun1 is not null) vehic.VehGun1.Alpha = -1f;
        if (vehic.VehGun2 is not null) vehic.VehGun2.Alpha = -1f;
    }

    private void SpawnGunGib(string tag, Entity vehic, Vector3 forward, Vector3 dark, float fadeTime)
    {
        if (Api.Services is null) return;
        Entity g = Api.Entities.Spawn();
        g.ClassName = "spiderbot_gun";
        g.Model = "models/vehicles/spiderbot_barrels.dpm"; // MDL_VEH_SPIDERBOT_GUN
        Api.Entities.SetModel(g, g.Model);
        g.Solid = Solid.Corpse;                         // QC: SOLID_CORPSE (set before setorigin)
        Api.Entities.SetOrigin(g, VehiclePhysics.TagOrigin(vehic.TurHead ?? vehic, tag));
        g.MoveType = MoveType.Toss;                     // QC: MOVETYPE_TOSS
        g.Velocity = forward * 700f + Prandom.Vec() * 32f; // QC: v_forward*700 + randomvec()*32
        g.AVelocity = Prandom.Vec() * 180f;             // QC: avelocity = randomvec()*180
        g.ColorModKey = dark;
        FadeGib(g, Time + fadeTime, fadeTime);          // QC: SUB_SetFade(g, time, min(respawntime,10))
    }

    /// <summary>QC EF_FLAME (the burning-glow effect bit; the gib's flaming death trail).</summary>
    private const int EfFlame = 1024;

    /// <summary>
    /// Port of QC <c>SUB_SetFade</c> + <c>SUB_SetFade_Think</c> (subs.qc:57-84): wait until
    /// <paramref name="when"/>, then fade <c>alpha -= frametime * (1/fadingTime)</c> each tick (so the gib
    /// fully fades over <paramref name="fadingTime"/> seconds), then remove. Used for the body + gun gibs (the
    /// head runs its own headfade closure instead).
    /// </summary>
    private static void FadeGib(Entity gib, float when, float fadingTime)
    {
        if (Api.Services is null) return;
        if (fadingTime <= 0f) fadingTime = 0.01f; // QC SUB_SetFade clamp
        float fadeRate = 1f / fadingTime;          // QC: ent.fade_rate = 1/fading_time
        gib.Alpha = 1f;
        gib.Think = self =>
        {
            if (Time < when) { self.NextThink = when; return; } // QC: nextthink = vanish_time
            if (self.Alpha == 0f) self.Alpha = 1f;              // QC SUB_SetFade_Think guard
            self.Alpha -= FadeStep * fadeRate;                 // QC: alpha -= frametime * fade_rate
            if (self.Alpha < 0.01f) Api.Entities.Remove(self); // QC: SUB_VanishOrRemove
            else self.NextThink = Time + FadeStep;
        };
        gib.NextThink = when;
    }

    // The gib-fade think re-arms at this cadence; QC steps by `frametime` each frame, the port steps by this dt.
    private const float FadeStep = 0.1f;

    // ============================ WEAPONS ============================

    // spiderbot_frame() minigun block — spiderbot.qc: the twin hitscan miniguns.
    /// <summary>Fire one minigun round (hitscan; alternates barrels; spread + solid penetration).</summary>
    public void FireMinigun(Entity vehicle, Entity player)
    {
        ++vehicle.VehBulletCounter;
        Entity gun = (vehicle.VehBulletCounter % 2) != 0 ? (vehicle.VehGun1 ?? vehicle) : (vehicle.VehGun2 ?? vehicle);

        var (barrel, fwd) = VehiclePhysics.TagOriginForward(gun, "barrels");
        fwd = QMath.Normalize(fwd);                  // QC: v_forward = normalize(v_forward)
        Vector3 origin = barrel + fwd * 50f;
        QMath.AngleVectors(QMath.VecToAngles(fwd), out Vector3 f, out Vector3 r, out Vector3 u);
        Vector3 dir = Prandom.Spread(f, r, u, MinigunSpread);

        // fireBullet with solid penetration: pierce up to a few thin surfaces (QC solidpenetration).
        FireBulletPenetrating(vehicle, origin, dir, MinigunDamage, MinigunSolidPenetration);

        if (Api.Services is not null)
            Api.Sound.Play(gun, SoundChannel.Weapon, "vehicles/spiderbot_minigun_fire.wav");
        // QC spiderbot.qc:276: Send_Effect(EFFECT_SPIDERBOT_MINIGUN_MUZZLEFLASH, v, v_forward * 2500, 1) — the
        // authoritative server-networked muzzle flash at the firing barrel. (The client VehicleVisuals also pops a
        // muzzle flash + spins the barrels off the networked Firing flag; this is the QC-faithful server emit.)
        EffectEmitter.Emit("SPIDERBOT_MINIGUN_MUZZLEFLASH", origin, fwd * 2500f, 1);
    }

    /// <summary>Hitscan with QC solidpenetration: pass through thin walls up to <paramref name="penetration"/> units total.</summary>
    private void FireBulletPenetrating(Entity attacker, Vector3 start, Vector3 dir, float damage, float penetration)
    {
        if (Api.Services is null) return;
        Vector3 from = start;
        Vector3 d = QMath.Normalize(dir);
        for (int pass = 0; pass < 4; ++pass) // bounded pierce count
        {
            Vector3 end = from + d * WeaponFiring.CurrentMaxShotDistance;
            TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, attacker);
            if (tr.Ent is not null && tr.Ent.TakeDamage != DamageMode.No)
            {
                QMath.AngleVectors(QMath.VecToAngles(d), out Vector3 fwd, out _, out _);
                // spiderbot.qc minigun fireBullet: DEATH_VH_SPID_MINIGUN (special "vehicle" deathtype, not a weapon id).
                Combat.Damage(tr.Ent, attacker, attacker, damage, DeathTypes.VhSpidMinigun,
                    tr.EndPos, fwd * MinigunForce);
            }
            if (tr.Fraction == 1f) break;
            // Try to continue through the surface within the penetration budget.
            Vector3 next = tr.EndPos + d * penetration;
            TraceResult back = Api.Trace.Trace(next, Vector3.Zero, Vector3.Zero, tr.EndPos, MoveFilter.WorldOnly, attacker);
            if (back.StartSolid) break; // too thick to penetrate
            from = next;
            penetration -= QMath.VLen(next - tr.EndPos);
            if (penetration <= 0f) break;
        }
    }

    // spiderbot_rocket_do() — spiderbot_weapons.qc: the 3-mode rocket launcher + guide-release.
    /// <summary>Port of <c>spiderbot_rocket_do</c>: belt-driven volley/guided/artillery fire and the guided-release on button-up.</summary>
    public void RocketDo(Entity vehicle, Entity player, in MovementInput input)
    {
        bool atck2 = input.ButtonAttack2;
        int mode = vehicle.VehW2Mode;

        // Guide-release bookkeeping: while holding fire in GUIDE mode, the belt pauses at frame 9/1; on
        // release, any of this pilot's guided rockets convert to unguided aimed at the last crosshair point.
        // VehRocketWaitLatch: 0 = idle, 1 = holding-guide, -10 = volley auto-empty in progress.
        if (vehicle.VehRocketWaitLatch != -10)
        {
            if (atck2 && mode == (int)SpiderbotRocketMode.Guided)
            {
                if (vehicle.VehRocketWaitLatch == 1 && (vehicle.VehRocketBelt == 9 || vehicle.VehRocketBelt == 1))
                {
                    if (vehicle.VehRocketGate < Time && vehicle.VehRocketBelt == 9) vehicle.VehRocketBelt = 1;
                    return;
                }
                vehicle.VehRocketWaitLatch = 1;
            }
            else
            {
                if (vehicle.VehRocketWaitLatch == 1) GuideRelease(vehicle, player, input);
                vehicle.VehRocketWaitLatch = 0;
            }
        }

        if (vehicle.VehRocketGate > Time) return;

        if (vehicle.VehRocketBelt >= 9)
        {
            vehicle.VehRocketBelt = 1;
            vehicle.VehRocketWaitLatch = 0;
        }

        if (vehicle.VehRocketWaitLatch != -10 && !atck2) return;

        Vector3 v = VehiclePhysics.TagOrigin(vehicle.TurHead ?? vehicle, "tag_fire", new Vector3(60f, 0f, 30f));
        QMath.AngleVectors((vehicle.TurHead ?? vehicle).Angles + vehicle.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);

        TraceResult ct = VehiclePhysics.CrosshairTrace(vehicle, input.ViewAngles, vehicle);
        Entity rocket;

        switch (mode)
        {
            case 1: // SBRM_VOLLY — dumb rocket that detonates after a randomized flight time
            {
                Vector3 vel = Prandom.Spread(forward, right, up, RocketSpread) * RocketSpeed;
                rocket = VehicleCommon.SpawnProjectile(vehicle, player, v, vel,
                    RocketDamage, RocketRadius, RocketForce, size: 1f,
                    DeathTypes.VhSpidRocket, health: RocketHealth, lifetime: RocketLifetime,
                    fireSound: "vehicles/spiderbot_rocket_fire.wav");
                float distApprox = Prandom.Range(0f, RocketRadius) + QMath.VLen(v - ct.EndPos) - Prandom.Range(0f, RocketRadius);
                // Detonate at the computed time (QC vehicles_projectile_explode_think).
                rocket.Think = self =>
                {
                    self.Touch = null; self.Think = null; self.TakeDamage = DamageMode.No;
                    WeaponSplash.RadiusDamage(self, self.Origin, RocketDamage, 0f, RocketRadius,
                        self.DmgInflictor, 0, RocketForce, deathTag: DeathTypes.VhSpidRocket);
                    Api.Entities.Remove(self);
                };
                rocket.NextThink = Time + MathF.Max(distApprox / RocketSpeed, 0f);
                if (atck2 && vehicle.VehRocketBelt == 1) vehicle.VehRocketWaitLatch = -10; // hold-to-empty-belt latch
                break;
            }
            case 2: // SBRM_GUIDE — homes toward the pilot's crosshair
                rocket = VehicleCommon.SpawnProjectile(vehicle, player, v, QMath.Normalize(forward) * RocketSpeed,
                    RocketDamage, RocketRadius, RocketForce, size: 1f,
                    DeathTypes.VhSpidRocket, health: RocketHealth, lifetime: RocketLifetime,
                    fireSound: "vehicles/spiderbot_rocket_fire.wav");
                rocket.VehGuideTarget = ct.EndPos;
                rocket.VehGuideMode = (int)VehiclePhysics.GuideMode.SpiderGuided;
                AttachGuidance(rocket, player);
                break;
            default: // SBRM_ARTILLERY — ballistic lob onto the crosshair point
            {
                rocket = VehicleCommon.SpawnProjectile(vehicle, player, v, QMath.Normalize(forward) * RocketSpeed,
                    RocketDamage, RocketRadius, RocketForce, size: 1f,
                    DeathTypes.VhSpidRocket, health: RocketHealth, lifetime: RocketLifetime,
                    fireSound: "vehicles/spiderbot_rocket_fire.wav");
                Vector3 target = ct.EndPos + Prandom.Vec() * (0.75f * RocketRadius);
                target.Z = ct.EndPos.Z;
                rocket.VehGuideTarget = target;
                // Choose a clearance height and solve the ballistic arc.
                float h1 = Api.Services is not null
                    ? 0.75f * QMath.VLen(v - Api.Trace.Trace(v, Vector3.Zero, Vector3.Zero, v + new Vector3(0, 0, WeaponFiring.CurrentMaxShotDistance), MoveFilter.WorldOnly, vehicle).EndPos)
                    : 256f;
                float h2 = Api.Services is not null
                    ? 0.75f * QMath.VLen(target - v)
                    : 256f;
                rocket.Velocity = CalcArtillery(v, target, MathF.Min(h1, h2));
                rocket.MoveType = MoveType.Toss;
                rocket.Gravity = 1f;
                break;
            }
        }
        rocket.ClassName = "spiderbot_rocket";
        rocket.VehProjExpire = Time + RocketLifetime;

        // QC vehicles_projectile (sv_vehicles.qc): Send_Effect(_mzlfx, proj.origin, proj.velocity, 1) with the
        // spiderbot's launch effect EFFECT_SPIDERBOT_ROCKETLAUNCH (spiderbot_weapons.qc:176/191/202). The shared
        // SpawnProjectile helper plays the fire sound but defers the muzzle effect; emit it here (server
        // authoritative) at the rocket's spawn origin + velocity. The in-flight trail/visual stays the client
        // ProjectileCatalog (SpiderRocket) concern.
        EffectEmitter.Emit("SPIDERBOT_ROCKETLAUNCH", rocket.Origin, rocket.Velocity, 1);

        ++vehicle.VehRocketBelt;
        float refire = vehicle.VehRocketBelt == 9 ? RocketReload
            : (mode == 1 ? RocketRefire2 : RocketRefire);
        vehicle.VehAttackFinished = refire;
        vehicle.VehRocketGate = Time + refire;
    }

    /// <summary>Attach the per-tick guided/unguided steering think to a spiderbot rocket.</summary>
    private void AttachGuidance(Entity rocket, Entity pilot)
    {
        rocket.VehProjAccel = 0f;
        rocket.VehProjTurnRate = RocketTurnRate;
        rocket.Think = self =>
        {
            self.NextThink = Time;
            var mode = (VehiclePhysics.GuideMode)self.VehGuideMode;
            Entity? body = self.Owner; // the spiderbot body; body.VehInput carries the pilot's aim

            // QC spiderbot_rocket_guided: stop guiding the moment the pilot leaves the vehicle.
            if (mode == VehiclePhysics.GuideMode.SpiderGuided && (body is null || body.Owner is null))
            {
                mode = VehiclePhysics.GuideMode.SpiderUnguided;
                self.VehGuideMode = (int)mode;
            }

            // Guided: re-trace the live pilot crosshair each tick (from the body's stored input).
            if (mode == VehiclePhysics.GuideMode.SpiderGuided && body is not null)
            {
                TraceResult t = VehiclePhysics.CrosshairTrace(body, body.VehInput.ViewAngles, body);
                self.VehGuideTarget = t.EndPos;
            }

            Vector3 oldDir = QMath.Normalize(self.Velocity);
            Vector3 newDir = QMath.Normalize(self.VehGuideTarget - self.Origin) + Prandom.Vec() * RocketNoise;
            self.Velocity = QMath.Normalize(oldDir + newDir * RocketTurnRate) * RocketSpeed;

            bool detonate = (body is not null && VehicleCommon.IsDead(body)) || self.VehProjExpire < Time
                || (mode == VehiclePhysics.GuideMode.SpiderUnguided && QMath.VLen(self.VehGuideTarget - self.Origin) < 16f);
            if (detonate)
            {
                WeaponSplash.RadiusDamage(self, self.Origin, RocketDamage, 0f, RocketRadius,
                    self.DmgInflictor, 0, RocketForce, deathTag: DeathTypes.VhSpidRocket);
                Api.Entities.Remove(self);
            }
        };
        rocket.NextThink = Time;
    }

    /// <summary>Port of <c>spiderbot_guide_release</c>: convert this pilot's guided rockets to unguided aimed at the crosshair.</summary>
    private void GuideRelease(Entity vehicle, Entity player, in MovementInput input)
    {
        if (Api.Services is null) return;
        Vector3 endpos = VehiclePhysics.CrosshairTrace(vehicle, input.ViewAngles, vehicle).EndPos;
        foreach (Entity proj in Api.Entities.FindByClass("vehicles_projectile"))
            if (proj.ClassName == "spiderbot_rocket" && proj.Owner == vehicle
                && proj.VehGuideMode == (int)VehiclePhysics.GuideMode.SpiderGuided)
            {
                proj.VehGuideTarget = endpos;
                proj.VehGuideMode = (int)VehiclePhysics.GuideMode.SpiderUnguided;
            }
    }

    /// <summary>
    /// Port of <c>spiberbot_calcartillery</c>: solve the launch velocity to lob from <paramref name="org"/>
    /// to <paramref name="tgt"/> reaching at least <paramref name="ht"/> above the higher endpoint.
    /// </summary>
    private static Vector3 CalcArtillery(Vector3 org, Vector3 tgt, float ht)
    {
        float grav = StaticGravity();
        float zdist = tgt.Z - org.Z;
        Vector3 flat = tgt - org - new Vector3(0, 0, zdist);
        float sdist = QMath.VLen(flat);
        Vector3 sdir = QMath.Normalize(flat);

        float jumpheight = MathF.Abs(ht);
        if (zdist > 0f) jumpheight += zdist;

        float vz = MathF.Sqrt(2f * grav * jumpheight);
        if (ht < 0f && zdist < 0f) vz = -vz;

        // Solve 0.5*grav*t^2 - vz*t + zdist = 0 for the flight time (always solvable since jumpheight>=zdist).
        (float r0, float r1, bool two) = SolveQuadratic(0.5f * grav, -vz, zdist);
        float flight;
        if (!two) flight = r0; // single/duplicate root
        else if (zdist < 0f) flight = MathF.Max(r0, r1); // down-jump: take the larger time
        else if (ht < 0f) flight = MathF.Min(r0, r1);    // up + straight-line: smaller time
        else flight = MathF.Max(r0, r1);                 // up + regular arc: larger time
        if (flight <= 0f) flight = MathF.Max(r0, r1) > 0f ? MathF.Max(r0, r1) : 0.0001f;

        float vs = sdist / flight;
        return sdir * vs + new Vector3(0, 0, vz);
    }

    /// <summary>QC solve_quadratic(a,b,c) -> (root0, root1, twoDistinctRoots).</summary>
    private static (float, float, bool) SolveQuadratic(float a, float b, float c)
    {
        if (a == 0f)
        {
            if (b == 0f) return (0f, 0f, false);
            float r = -c / b; return (r, r, false);
        }
        float disc = b * b - 4f * a * c;
        if (disc < 0f) { float r = -b / (2f * a); return (r, r, false); }
        float sq = MathF.Sqrt(disc);
        float x0 = (-b - sq) / (2f * a);
        float x1 = (-b + sq) / (2f * a);
        return (x0, x1, disc > 0f);
    }

    // ---- mode switch (spiderbot_impulse) ----
    public static void SetMode(Entity vehicle, SpiderbotRocketMode mode) => vehicle.VehW2Mode = (int)mode;
    public static void CycleMode(Entity vehicle, int dir)
    {
        int m = vehicle.VehW2Mode + dir;
        if (m > (int)SpiderbotRocketMode.Artillery) m = (int)SpiderbotRocketMode.Volley;
        if (m < (int)SpiderbotRocketMode.Volley) m = (int)SpiderbotRocketMode.Artillery;
        vehicle.VehW2Mode = m;
    }

    private Entity NewSub(Entity vehicle, string cls, Entity? attachParent)
    {
        Entity e = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
        e.ClassName = cls;
        e.Owner = vehicle;
        e.VehSlotOwner = vehicle;
        if (Api.Services is not null && attachParent is not null) Api.Models.SetAttachment(e, attachParent, "");
        return e;
    }

    private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;
    private static float Gravity()
    {
        if (Api.Services is null) return 800f;
        float v = Api.Cvars.GetFloat("sv_gravity");
        return v != 0f ? v : 800f;
    }
    // QC: autocvar_sv_gameplayfix_gravityunaffectedbyticrate (default true) -> 0.5, else 1.
    // The port treats this gameplayfix as hardcoded-true (PlayerPhysics.GravityUnaffectedByTicrate,
    // FlyMove.GravityUnaffectedByTicrate), and the cvar is not registered/seeded, so the factor is
    // a constant 0.5 to stay both Base-default-faithful and consistent with the engine's movetypes.
    private static float GravityTicrateFactor() => 0.5f;
    private static float StaticGravity()
    {
        if (Api.Services is null) return 800f;
        float v = Api.Cvars.GetFloat("sv_gravity");
        return v != 0f ? v : 800f;
    }
}

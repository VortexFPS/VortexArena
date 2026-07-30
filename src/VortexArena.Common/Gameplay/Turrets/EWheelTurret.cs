using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// eWheel Turret — port of common/turrets/turret/ewheel.{qh,qc} (+ ewheel_weapon.qc). A fast, MOBILE wheeled
/// turret (TUR_FLAG_MOVE | TUR_FLAG_ROAM) that rolls around and attacks targets in line of sight with rapid
/// blaster bolts from two cannons. Identity/hitbox from ewheel.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_ewheel_*</c>).
///
/// Ported: the very fast blaster projectiles (shot_speed 9000, effectively near-hitscan) in a volley of 2, and
/// the wheeled locomotion — roll toward the enemy easing in at the optimal range (steerlib_arrive) with the
/// chase speed gated by the body yaw error (fast/slow/slower = Base tur_head.spawnshieldtime &lt;1/&lt;2/else),
/// back off when too close, brake when idle, with the body yawing toward the steer direction at the turnrate
/// (<c>ewheel_move_enemy</c>/<c>ewheel_move_idle</c> + the EWheel tr_think yaw-steer). When idle with a
/// <c>target</c> set it roams the <c>turret_checkpoint</c> waypoint chain (<c>ewheel_move_path</c> /
/// <c>ewheel_findtarget</c>), stamps the locomotion drive frame (0..4) onto the networked <see cref="Entity.Frame"/>
/// (QC <c>turrets_setframe</c>/TNSF_ANIM), and emits the low-HP spark temp-entity (CSQC <c>ewheel_draw</c>
/// te_spark at &lt;127 hp, 5%/frame). On fire it emits the EFFECT_BLASTER_MUZZLEFLASH and nets the bolt as
/// PROJECTILE_BLASTER (the client trail). The pure-client origin/head roll integration in <c>ewheel_draw</c>
/// stays client render (the server already networks origin + velocity), and the two-cannon head-frame
/// alternation needs the unported head sub-entity (see ewheel-gun1.md3 / tag_head).
/// </summary>
[Turret]
public sealed class EWheelTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_ewheel_*) ---
    private const float ShotDamage = 30f;
    private const float ShotRadius = 50f;
    private const float ShotSpeed = 9000f;
    private const float ShotForce = 125f;
    private const float ShotSpread = 0.025f;
    private const float ShotRefire = 0.1f;
    private const int ShotVolly = 2;
    private const float ShotVollyRefire = 1f;
    private const float TargetRange = 5000f;
    private const float TargetRangeMin = 0.1f;
    private const float TargetRangeOptimal = 900f;
    private const float AmmoMax = 4000f;
    private const float AmmoRecharge = 50f;
    private const float AimSpeed = 90f;
    private const float AimMaxPitch = 45f;
    private const float AimMaxRot = 20f;
    private const float FireTolerance = 150f;
    private const float RespawnTime = 30f;

    // --- drive speeds (turrets.cfg g_turrets_unit_ewheel_speed_*) + body turn rate (ewheel_turnrate) ---
    private const float SpeedFast = 500f;
    private const float SpeedSlow = 150f;
    private const float SpeedSlower = 50f;
    private const float SpeedStop = 25f;
    private const float TurnRate = 200f;   // deg/sec body yaw (tur_head.aim_speed in tr_think)

    // --- locomotion animation frames (ewheel.qc:11-15 ewheel_anim_*) driven by turrets_setframe ---
    private const float AnimStop = 0f;
    private const float AnimFwdSlow = 1f;
    private const float AnimFwdFast = 2f;
    private const float AnimBckSlow = 3f;
    private const float AnimBckFast = 4f;

    // ewheel_move_path: proximity at which the path advances to the next checkpoint (turret_closetotarget, 64u).
    private const float PathNodeProximity = 64f;
    // Low-health spark threshold + per-frame chance (CSQC ewheel_draw: health < 127, random() < 0.05).
    private const float SparkHealthThreshold = 127f;
    private const float SparkChance = 0.05f;

    // QC ewheel.qc tr_setup: players, range-limited, team-checked, LOS (no ANGLELIMITS — it turns the body to face).
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectLos;

    public EWheelTurret()
    {
        NetName = "ewheel";
        DisplayName = "eWheel Turret";
        Model = "models/turrets/ewheel-base2.md3";
        StartHealth = 200f;
        // QC ewheel.qh head_model ATTRIB — the separate gun head-bone model (ewheel-gun1.md3) carried on top of the
        // wheeled base; Base alternates its two firing cannons via tur_head.frame (see the NOTE in Attack). Carried as
        // identity data to match Base (the FusionReactor/Flac HeadModel pattern); no client turret render attaches the
        // head at tag_head yet (whole-turret-family presentation gap), and the two-cannon frame cycle still needs that
        // unported head sub-entity.
        HeadModel = "models/turrets/ewheel-gun1.md3";
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
    {
        // Mobile creature: SOLID_SLIDEBOX + MOVETYPE_STEP, and damage can shove it (movable).
        TurretSpawn.Init(this, e, new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 48f),
            AmmoMax, AmmoRecharge, ShotVolly, respawnTime: RespawnTime, movable: true);
        e.Solid = Solid.SlideBox;
        e.MoveType = MoveType.Step;

        // Home pose + re-arm on respawn (QC tr_setup runs on first spawn and respawn).
        TurretState st = TurretAI.State(e);
        st.PathCurrent = null;
        Vector3 home = e.Origin;
        st.OnRespawn = self =>
        {
            self.Velocity = Vector3.Zero;
            self.Enemy = null;
            if (Api.Services is not null) Api.Entities.SetOrigin(self, home);
            self.Solid = Solid.SlideBox;
            self.MoveType = MoveType.Step;
            // QC tr_setup (movetype==STEP branch): re-resolve the initial path waypoint on respawn.
            TurretAI.State(self).PathCurrent = null;
            EWheelFindTarget(self);
        };

        // QC tr_setup first-spawn branch: if a target is set, InitializeEntity(ewheel_findtarget) wires the
        // entry checkpoint. The checkpoint chain is resolved by targetname, so it works regardless of spawn order.
        EWheelFindTarget(e);
    }

    /// <summary>
    /// QC <c>ewheel_findtarget</c> (ewheel.qc:111): resolve the turret's <c>target</c> key to the entry
    /// <c>turret_checkpoint</c> and store it as the current path node. A missing target / non-checkpoint is
    /// tolerated (QC just LOG_TRACEs and leaves pathcurrent null, so the wheel idles in place).
    /// </summary>
    private static void EWheelFindTarget(Entity e)
    {
        if (string.IsNullOrEmpty(e.Target)) return;
        Entity? cp = MapMover.FindFirstByTargetName(e.Target);
        if (cp is null || cp.ClassName != "turret_checkpoint") return;   // QC: warn, but pathcurrent stays null
        TurretAI.State(e).PathCurrent = cp;
    }

    public override void Think(Entity e)
    {
        var p = new TurretParams(Select, TargetRangeMin, TargetRange, ShotDamage, ShotRefire,
            AimSpeed, FireTolerance, lead: true, ShotVolly, ShotVollyRefire,
            rangeOptimal: TargetRangeOptimal, shotSpeed: ShotSpeed, aimMaxPitch: AimMaxPitch, aimMaxRot: AimMaxRot,
            shotTimeCompensate: true, zPredict: true, trackType: TurretAI.TrackStepMotor);
        TurretAI.RunCombat(e, in p, Attack);

        Drive(e);
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // EWheel.tr_think: roll toward / circle the enemy, roam the checkpoint path when idle (or brake if there is
    // none) and yaw the body toward the steer direction at the head turnrate. (ewheel.qc ewheel_move_enemy /
    // ewheel_move_path / ewheel_move_idle + the yaw-steer block + the turrets_setframe drive animation.)
    private void Drive(Entity e)
    {
        TurretState st = TurretAI.State(e);
        if (st.Active == false) return;
        float vz = e.Velocity.Z;

        // QC tr_think: anglemods the body angles, then steer the body yaw toward the steer direction clamped to
        // the turnrate. The body yaw ERROR (fabs of the short-way yaw delta) gates the chase speed below — this
        // is QC's tur_head.spawnshieldtime, computed here BEFORE the yaw step so a well-aligned wheel goes fast.
        e.Angles = new Vector3(TurretMath.AngleMods(e.Angles.X), TurretMath.AngleMods(e.Angles.Y), e.Angles.Z);

        // steerto for this frame. ewheel_move_enemy uses steerlib_arrive(enemy, optimal); ewheel_move_path uses
        // steerlib_attract2(moveto, 0.5, 500, 0.95) toward the current checkpoint. (QC's one-frame steerto lag is
        // elided — the port computes the steer fresh, matching the already-verified enemy branch.)
        Vector3 steerTo = Vector3.Zero;
        if (e.Enemy is not null)
            steerTo = TurretMath.SteerArrive(e, e.Enemy.Origin, TargetRangeOptimal);
        else if (st.PathCurrent is not null)
            steerTo = TurretMath.SteerAttract2(e, st.PathCurrent.Origin, 0.5f, 500f, 0.95f);

        float yawError = 0f;
        if (steerTo != Vector3.Zero)
        {
            float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
            Vector3 wishAngle = QMath.VecToAngles(QMath.Normalize(steerTo));
            // real_angle = wish_angle - angles, then shortangle_vxy toward the head angles (here the body's own).
            float diff = TurretMath.ShortAngle(wishAngle.Y - e.Angles.Y, e.Angles.Y);
            yawError = MathF.Abs(diff);                                   // tur_head.spawnshieldtime
            float step = TurnRate * frameTime;                           // f = aim_speed * frametime
            e.Angles = new Vector3(e.Angles.X, e.Angles.Y + QMath.Bound(-step, diff, step), e.Angles.Z);
        }

        QMath.AngleVectors(new Vector3(0f, e.Angles.Y, 0f), out Vector3 fwd, out _, out _);

        float newFrame = e.Frame;
        if (e.Enemy is not null)
        {
            // ewheel_move_enemy: speed gated by the body yaw error (well-aligned -> fast, off-axis -> slow/slower).
            float dist = (e.Enemy.Origin - e.Origin).Length();
            if (dist > TargetRangeOptimal)
            {
                if (yawError < 1f)
                {
                    newFrame = AnimFwdFast;
                    TurretMath.MoveSimple(e, fwd, SpeedFast, 0.4f);
                }
                else if (yawError < 2f)
                {
                    newFrame = AnimFwdSlow;
                    TurretMath.MoveSimple(e, fwd, SpeedSlow, 0.4f);
                }
                else
                {
                    newFrame = AnimFwdSlow;
                    TurretMath.MoveSimple(e, fwd, SpeedSlower, 0.4f);
                }
            }
            else if (dist < TargetRangeOptimal * 0.5f)
            {
                newFrame = AnimBckSlow;
                TurretMath.MoveSimple(e, -fwd, SpeedSlow, 0.4f);         // back off (kiting)
            }
            else
            {
                newFrame = AnimStop;
                TurretMath.BrakeSimple(e, SpeedStop);                    // hold the optimal range
            }

            SetFrame(e, newFrame);   // QC ewheel_move_enemy tail: turrets_setframe(newframe, false)
        }
        else if (st.PathCurrent is not null)
        {
            // ewheel_move_path: advance the chain when close (turret_closetotarget, 64u box overlap), then roll
            // toward the current node at speed_fast. QC's chain link is the checkpoint's .enemy, which
            // turret_checkpoint_init set to find(targetname, this.target); resolved lazily here by the checkpoint's
            // own target key so spawn order doesn't matter (a looped chain patrols forever; an unterminated one
            // ends with pathcurrent null → the wheel goes Roaming/idle).
            if (CloseToTarget(e, st.PathCurrent.Origin, PathNodeProximity))
                st.PathCurrent = NextCheckpoint(st.PathCurrent);

            if (st.PathCurrent is not null)
                TurretMath.MoveSimple(e, fwd, SpeedFast, 0.4f);
        }
        else
        {
            // ewheel_move_idle: reset to the idle frame, then brake to a stop.
            SetFrame(e, AnimStop);
            if (e.Velocity != Vector3.Zero)
                TurretMath.BrakeSimple(e, SpeedStop);
        }

        e.Velocity = new Vector3(e.Velocity.X, e.Velocity.Y, vz);

        // CSQC ewheel_draw low-health sparks: a damaged wheel (< 127 hp) smokes/sparks at 5%/frame. The QC draw
        // hook runs client-side; the spark is a networked temp-entity, so emit it server-side (it reaches every
        // viewing client identically). The pure-client origin/head roll integration stays client render.
        if (Api.Services is not null
            && e.GetResource(ResourceType.Health) < SparkHealthThreshold
            && Prandom.Float() < SparkChance)
        {
            EffectEmitter.TeSpark(e.Origin + new Vector3(0f, 0f, 40f), Prandom.Vec() * 256f + new Vector3(0f, 0f, 256f), 16);
        }
    }

    /// <summary>
    /// QC <c>turrets_setframe(this, frame, false)</c> (sv_turrets.qc:299): stamp the locomotion frame onto the
    /// networked <see cref="Entity.Frame"/> (which the client model animator plays — the same seam monsters use)
    /// so the wheel shows the correct drive animation (stop=0 / fwd_slow=1 / fwd_fast=2 / bck_slow=3 / bck_fast=4).
    /// In QC this also sets SendFlags |= TNSF_ANIM + anim_start_time; here <see cref="Entity.Frame"/> IS the net
    /// sync, so writing it is the whole job.
    /// </summary>
    private static void SetFrame(Entity e, float frame)
    {
        if (e.Frame != frame) e.Frame = frame;
    }

    /// <summary>
    /// The next node in the checkpoint chain (QC <c>pathcurrent.enemy</c>, set by <c>turret_checkpoint_init</c>'s
    /// <c>this.enemy = find(targetname, this.target)</c>). Resolved lazily by the current checkpoint's
    /// <c>target</c> key so the chain works regardless of checkpoint spawn order; null terminates the chain.
    /// </summary>
    private static Entity? NextCheckpoint(Entity checkpoint)
    {
        if (string.IsNullOrEmpty(checkpoint.Target)) return null;
        Entity? next = MapMover.FindFirstByTargetName(checkpoint.Target);
        return (next is not null && next.ClassName == "turret_checkpoint") ? next : null;
    }

    /// <summary>
    /// QC <c>turret_closetotarget</c> (sv_turrets.qc:1212): true when the point <paramref name="targ"/> (expanded
    /// by <paramref name="range"/> on each axis) overlaps the turret's bbox (also expanded by <paramref name="range"/>).
    /// </summary>
    private static bool CloseToTarget(Entity e, Vector3 targ, float range)
    {
        Vector3 r = new Vector3(range, range, range);
        Vector3 absMin = e.AbsMin != e.AbsMax ? e.AbsMin : e.Origin + e.Mins;
        Vector3 absMax = e.AbsMin != e.AbsMax ? e.AbsMax : e.Origin + e.Maxs;
        Vector3 aMin = targ - r, aMax = targ + r;
        Vector3 bMin = absMin - r, bMax = absMax + r;
        return aMin.X <= bMax.X && aMax.X >= bMin.X
            && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y
            && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    // METHOD(EWheelAttack, wr_think) — ewheel_weapon.qc: a fast blaster bolt (MIF_SPLASH, near-hitscan) with
    // the shot_spread cone, the EFFECT_BLASTER_MUZZLEFLASH, and the PROJECTILE_BLASTER trail. The two-cannon
    // head-frame alternation needs the unported head sub-entity (see the NOTE at the end of this method).
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // ewheel_weapon.qc: turret_projectile(..., PROJECTILE_BLASTER, ...). The client classifies a model-less
        // turret bolt by its networked classname+netname (ProjectileCatalog.Classify keys "blaster"/"laser" onto
        // PROJECTILE_BLASTER); the projType arg gives the bolt the blaster render trail/sprite, just as the in-hand
        // Blaster bolt nets "blaster" (Blaster.Attack). The helper stamps the type itself (QC's _proj_type arg).
        TurretSpawn.Projectile(turret, st.ShotOrg, dir, ShotSpeed, size: 1f, health: 0f,
            ShotDamage, edgeDamage: 0f, ShotRadius, ShotForce, DeathTypes.TurretEwheel, spread: ShotSpread,
            projType: "blaster");

        if (Api.Services is not null)
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/lasergun_fire.wav");

        // ewheel_weapon.qc:28 — Send_Effect(EFFECT_BLASTER_MUZZLEFLASH, tur_shotorg, shotdir*1000, 1). A networked
        // point effect at the muzzle along the shot direction. Base Send_Effect routes through
        // Send_Effect_Except(..., NULL), so it excludes NO viewer — including the firer. (The in-hand Blaster
        // passes except:actor to spare the player's first-person view, but a turret is not a view source, and
        // matching Base means no exclusion here.)
        EffectEmitter.Emit("BLASTER_MUZZLEFLASH", st.ShotOrg, dir * 1000f, 1);

        // NOTE (deferred — needs a head sub-entity): ewheel_weapon.qc:32-35 alternates the two firing cannons via
        // `tur_head.frame += 2; if > 3 -> 0` on the SEPARATE head model entity (ewheel-gun1.md3). The port has no
        // tur_head bone entity, and this turret's own Entity.Frame is already consumed by the locomotion drive
        // animation (0..4, see SetFrame/Drive) — so the cannon-frame cycle cannot be stamped here without
        // corrupting the wheel's roll animation. Belongs on the unported head sub-entity (identity.def gap).
    }
}

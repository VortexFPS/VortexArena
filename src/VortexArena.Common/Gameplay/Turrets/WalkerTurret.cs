using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Walker Turret — port of common/turrets/turret/walker.{qh,qc} (+ walker_weapon.qc). A legged, MOBILE turret
/// (TUR_FLAG_MOVE) that hunts targets: it walks/runs toward them, fires a hitscan minigun, lobs homing rockets
/// at medium range, and melees up close. Identity/hitbox from walker.qh; balance from turrets.cfg
/// (<c>g_turrets_unit_walker_*</c>).
///
/// Ported: the full combat loop (acquire a player in LOS/range, aim, fire the minigun with spread + force), the
/// legged locomotion (move toward the enemy — running when far, walking when near — easing in via
/// steerlib_attract2, yawing the body toward the heading at the per-gait turnrate, braking when idle), the
/// guided ROCKET VOLLY at rocket range (4 homing rockets 0.2s apart then a 10s reload, each steered by
/// <see cref="GuidedProjectile.WalkerRocketThink"/>), and the MELEE swipe (a 100-dmg radius hit in front when
/// the enemy is within melee range). Also ported: the no-enemy IDLE ROAM/WANDER (LOS-probe a new heading + the
/// stop/roam idle cycle, gated by the TSL_ROAM spawnflag), LAST-SEEN PURSUIT (chase the enemy's last position for
/// up to 10s), the turret_checkpoint WAYPOINT PATH-FOLLOWING (walker_findtarget + walker_move_path, incl. the
/// TSF_NO_PATHBREAK "stick to path even with an enemy" flag), the per-gait model FRAME (turrets_setframe onto the
/// networked Entity.Frame), and the waterlevel SWIM gait (pitch-toward + sinusoidal bob). Also ported: the CSQC
/// walker_draw 4-point ground-align terrain pitch/roll (movelib_groundalign4point, folded onto the networked
/// e.Angles pitch/roll on the ground gaits). Only the CSQC head avelocity spin stays client render; the rocket
/// up-and-over loop lives in GuidedProjectile.
/// </summary>
[Turret]
public sealed class WalkerTurret : Turret
{
    // --- minigun balance (turrets.cfg g_turrets_unit_walker_*) ---
    private const float ShotDamage = 5f;
    private const float ShotForce = 10f;
    private const float ShotSpread = 0.025f;
    private const float ShotSpeed = 18000f;       // near-hitscan minigun (turrets.cfg shot_speed)
    private const float ShotRefire = 0.05f;
    private const int ShotVolly = 10;
    private const float ShotVollyRefire = 1f;
    private const float TargetRange = 5000f;
    private const float TargetRangeMin = 0f;
    private const float TargetRangeOptimal = 100f;
    private const float AmmoMax = 4000f;
    private const float AmmoRecharge = 100f;
    private const float AimSpeed = 45f;
    private const float AimMaxPitch = 15f;
    private const float AimMaxRot = 90f;
    private const float FireTolerance = 100f;

    // --- locomotion (turrets.cfg) ---
    private const float SpeedRun = 300f, SpeedWalk = 200f, SpeedStop = 90f;
    private const float SpeedRoam = 100f, SpeedSwim = 200f, SpeedJump = 800f;
    private const float TurnWalk = 15f, TurnRun = 7f, TurnSwim = 10f, TurnStrafe = 5f, Turn = 20f;

    // QC turret spawnflags (turret.qh): TSF_NO_PATHBREAK = don't break the checkpoint path to chase an enemy
    // (still fires at it); TSL_ROAM = roam (random-walk) while idle instead of stopping in place.
    private const int TsfNoPathbreak = 1 << 3;  // BIT(3) = 8
    private const int TslRoam = 1 << 5;         // BIT(5) = 32

    // walker_move_path: proximity at which the path advances to the next checkpoint (turret_closetotarget, 64u).
    private const float PathNodeProximity = 64f;

    // --- rocket volley (turrets.cfg g_turrets_unit_walker_rocket_*) ---
    private const float RocketDamage = 45f;
    private const float RocketRadius = 150f;
    private const float RocketForce = 150f;
    private const float RocketSpeed = 1000f;
    private const float RocketTurnRate = 0.05f;
    private const float RocketRange = 4000f;
    private const float RocketRangeMin = 500f;
    private const float RocketRefire = 10f;

    // --- melee (turrets.cfg g_turrets_unit_walker_melee_*) ---
    private const float MeleeDamage = 100f;
    private const float MeleeForce = 600f;
    private const float MeleeRange = 100f;

    // --- CSQC walker_draw low-hp sparks (walker.qc:627) ---
    private const float SparkHealthThreshold = 127f;
    private const float SparkChance = 0.15f;

    /// <summary>QC walker animflag (walker.qc:28). Drives the per-gait turn rate + movement in tr_think, and is
    /// also the model frame index networked via turrets_setframe.</summary>
    private enum Gait { No = 0, Turn = 1, Walk = 2, Run = 3, StrafeL = 4, StrafeR = 5, Jump = 6, Land = 7,
        Pain = 8, Melee = 9, Swim = 10, Roam = 11 }

    /// <summary>Walker-specific scratch (QC tur_head.shot_volly rocket counter + the separate rocket refire clock + melee lock + idle-roam state).</summary>
    private sealed class WalkerState
    {
        public int RocketVolly;       // QC tur_head.shot_volly: rockets left in the current burst
        public float RocketFinished;  // QC tur_head.attack_finished_single[0]: next rocket/burst time
        public float MeleeUntil;      // sim time the melee swing is locked (no movement/fire mid-swipe)

        public Gait AnimFlag;         // QC .animflag: the active gait (selects turn rate + movement below)
        public Vector3 Moveto;        // QC .moveto: the steering goal point
        public Vector3 Steerto;       // QC .steerto: this think's steering vector (vectoangles -> heading)
        public float IdleTime;        // QC .idletime: next time to re-roll the idle stop/roam decision
        public float HeadIdleTime;    // QC .tur_head.idletime: the -1337 LOS-probe "pick a new wander dir" flag
        public Vector3 EnemyLastLoc;  // QC .enemy_last_loc: last place the enemy was seen
        public float EnemyLastTime;   // QC .enemy_last_time: when (0 = none)
    }
    private static readonly Dictionary<Entity, WalkerState> _w = new();
    private static WalkerState W(Entity e) { if (!_w.TryGetValue(e, out var s)) { s = new(); _w[e] = s; } return s; }

    // QC walker.qc tr_setup: players, range-limited, team-checked, LOS. The walker turns its BODY to face
    // (not angle-gated acquisition), so — unlike fixed turrets — it does NOT set ANGLELIMITS.
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits
                             | TurretAI.SelectTeamCheck | TurretAI.SelectLos;

    public WalkerTurret()
    {
        NetName = "walker";
        DisplayName = "Walker Turret";
        Model = "models/turrets/walker_body.md3";
        StartHealth = 500f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
    {
        Vector3 mins = new Vector3(-70f, -70f, 0f);
        Vector3 maxs = new Vector3(70f, 70f, 95f);

        // QC tr_setup home pose: on the FIRST setup (still non-STEP) ground-snap the map origin to the floor —
        // tracebox from origin+'0 0 128' down 10000, then origin = trace_endpos + '0 0 4' — and store pos1/pos2
        // as the home. Subsequent setups (already MOVETYPE_STEP) just restore that home. We're pre-STEP here, so
        // do the drop now, before Init/MoveType.Step, so the stored home is the floor pose (not the raw map org).
        if (Api.Services is not null)
        {
            Vector3 from = e.Origin + new Vector3(0f, 0f, 128f);
            Vector3 to = e.Origin - new Vector3(0f, 0f, 10000f);
            TraceResult drop = Api.Trace.Trace(from, mins, maxs, to, MoveFilter.Normal, e);
            Api.Entities.SetOrigin(e, drop.EndPos + new Vector3(0f, 0f, 4f));
        }

        // Mobile, creature-like: SOLID_SLIDEBOX + MOVETYPE_STEP; damage shoves it.
        TurretSpawn.Init(this, e, mins, maxs,
            AmmoMax, AmmoRecharge, ShotVolly, respawnTime: 60f, movable: true, energyAmmo: false);
        e.Solid = Solid.SlideBox;
        e.MoveType = MoveType.Step;

        // Home pose + re-arm on respawn (QC tr_setup pos1/pos2 home; runs on first spawn + respawn).
        TurretState st = TurretAI.State(e);
        st.PathCurrent = null;
        Vector3 home = e.Origin;
        Vector3 homeAng = e.Angles;
        st.OnRespawn = self =>
        {
            self.Velocity = Vector3.Zero;
            self.Enemy = null;
            self.Angles = homeAng;
            if (Api.Services is not null) Api.Entities.SetOrigin(self, home);
            self.Solid = Solid.SlideBox;
            self.MoveType = MoveType.Step;
            WalkerState s = W(self);
            s.RocketVolly = 0;
            s.AnimFlag = Gait.No;
            s.IdleTime = 0f;
            s.HeadIdleTime = 0f;
            s.EnemyLastTime = 0f;
            s.MeleeUntil = 0f;
            // QC tr_setup (STEP branch re-spawn): re-resolve the initial checkpoint path head.
            TurretAI.State(self).PathCurrent = null;
            WalkerFindTarget(self);
        };

        // QC tr_setup first-spawn branch: if (target != "") InitializeEntity(walker_findtarget) wires the entry
        // turret_checkpoint. Resolved by targetname, so it works regardless of checkpoint spawn order.
        WalkerFindTarget(e);
    }

    /// <summary>
    /// QC <c>walker_findtarget</c> (walker.qc:326): resolve the walker's <c>target</c> key to the entry
    /// <c>turret_checkpoint</c> and store it as the current path node (the non-FANCYPATHING branch:
    /// <c>pathcurrent = e</c>). A missing target / non-checkpoint is tolerated (QC LOG_TRACEs and leaves
    /// pathcurrent null, so the walker idles/roams in place).
    /// </summary>
    private static void WalkerFindTarget(Entity e)
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

        // Hold fire while mid-melee (QC walker_firecheck: ANIM_MELEE blocks firing).
        WalkerState ws = W(e);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        // NOTE: Walker REPLACES the default turret_draw with walker_draw (walker.qc:637) — its own <127-hp spark is
        // emitted from DoMovement (below), and TurretAI.DrawFx deliberately skips walker, so the framework draw FX
        // is NOT run here. (Base only ewheel/walker override .draw; everything else uses the default turret_draw.)
        if (ws.MeleeUntil > now)
            DoMovement(e, melee: true);
        else
        {
            TurretAI.RunCombat(e, in p, Attack);
            DoMovement(e, melee: false);
        }
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // WalkerTurret.tr_think (walker.qc:356): pick the gait (no-enemy idle roam / last-seen pursuit, or
    // enemy melee / rocket volley / chase), then apply the per-gait move + raw per-think body yaw.
    private void DoMovement(Entity e, bool melee)
    {
        if (TurretAI.State(e).Active == false) return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        WalkerState ws = W(e);
        QMath.AngleVectors(new Vector3(0f, e.Angles.Y, 0f), out Vector3 fwd, out _, out _);

        TurretState ts = TurretAI.State(e);

        // --- gait selection (the QC if/else-if/else over it.enemy) ---
        if (melee)
        {
            // Mid-swing: the swing/damage were already scheduled; just stay in the MELEE gait.
            ws.AnimFlag = Gait.Melee;
        }
        // QC walker.qc:360 — TSF_NO_PATHBREAK + a path: stick to the checkpoint chain even with an enemy in view
        // (it still fires at the enemy through the combat brain; it just doesn't break path to chase).
        else if ((e.SpawnFlags & TsfNoPathbreak) != 0 && ts.PathCurrent is not null)
        {
            WalkerMovePath(e, ws, ts);
        }
        else if (e.Enemy is null)
        {
            // QC walker.qc:364 — no enemy + a path: patrol the checkpoint chain; else roam/last-seen.
            if (ts.PathCurrent is not null)
                WalkerMovePath(e, ws, ts);
            else
                IdleSelect(e, ws, now, fwd);
        }
        else
        {
            Entity enemy = e.Enemy;
            float dist = TurretAI.State(e).DistEnemy;

            // Melee range + roughly facing it -> swing (QC: wish_angle.y < 15 deg, and not while swimming).
            Vector3 wishAngle = TurretMath.AngleOfs(e.Origin, e.Angles, enemy.Origin);
            if (dist < MeleeRange && ws.AnimFlag != Gait.Melee && ws.AnimFlag != Gait.Swim
                && System.Math.Abs(wishAngle.Y) < 15f)
            {
                ws.Moveto = enemy.Origin;
                ws.Steerto = TurretMath.SteerAttract2(e, ws.Moveto, 0.5f, 500f, 0.95f);
                ws.AnimFlag = Gait.Melee;
                ws.MeleeUntil = now + 0.41f;          // QC defers walker_setnoanim by 0.41
                ScheduleMelee(e, now + 0.21f);        // QC defers walker_melee_do_dmg by 0.21
            }
            // Rocket volley clock (QC: separate tur_head.shot_volly / attack_finished_single[0]).
            else if (ws.RocketFinished < now)
            {
                if (ws.RocketVolly > 0)
                {
                    ws.AnimFlag = Gait.No;
                    ws.RocketVolly--;
                    ws.RocketFinished = ws.RocketVolly == 0 ? now + RocketRefire : now + 0.2f;
                    // QC walker.qc:447 — alternate the muzzle tag by the POST-decrement volly parity:
                    // shot_volly > 1 -> tag_rocket01, else tag_rocket02. (For a 4-burst: 3,2 -> 01; 1,0 -> 02.)
                    FireRocket(e, enemy, ws.RocketVolly > 1 ? "tag_rocket01" : "tag_rocket02");
                }
                else if (dist > RocketRangeMin && dist < RocketRange)
                {
                    ws.RocketVolly = 4;               // QC arms a 4-rocket burst
                }
            }
            else if (ws.AnimFlag != Gait.Melee)
            {
                WalkerMoveTo(e, ws, enemy.Origin, dist);  // chase (run/walk/swim by distance+waterlevel)
            }
        }

        // --- per-gait turn + movement (the QC switch(it.animflag) block) ---
        ApplyGait(e, ws, fwd, now);

        // CSQC walker_draw low-health sparks (walker.qc:627): a damaged walker (< 127 hp) sparks at 15%/frame.
        // The QC draw hook runs client-side, but the spark is a networked temp-entity (reaches every viewing
        // client identically), so emit it server-side — same convention as EWheelTurret's ewheel_draw spark. The
        // pure-client head-spin/origin-advance stays client render (the ground-align is folded onto the angles below).
        if (Api.Services is not null
            && e.GetResource(ResourceType.Health) < SparkHealthThreshold
            && Prandom.Float() < SparkChance)
        {
            EffectEmitter.TeSpark(e.Origin + new Vector3(0f, 0f, 40f),
                Prandom.Vec() * 256f + new Vector3(0f, 0f, 256f), 16);
        }

        // CSQC walker_draw 4-point GROUND-ALIGN (walker.qc:615): movelib_groundalign4point(this,300,100,0.25,45)
        // pitches/rolls the chassis to follow the terrain under it. The QC draw hook runs on the client-side render
        // copy, but it writes ONLY pitch (angles.x) and roll (angles.z) — both networked quantities that reach every
        // viewing client identically — and never the yaw (angles.y) that drives locomotion/aim. So, exactly like the
        // low-hp spark and the gait frame above, fold it onto the server entity (the already-ported, Godot-free
        // movelib_groundalign4point in VehiclePhysics). Skip it while swimming: Base's CSQC copy ground-aligns over
        // terrain, while ApplyGait's SWIM case owns the in-water pitch bob — the two never co-occur on one entity, so
        // gate on the ground gaits to avoid fighting the swim pitch (waterlevel < SWIMMING).
        if (Api.Services is not null && ws.AnimFlag != Gait.Swim && e.WaterLevel < 2)
        {
            QMath.AngleVectors(e.Angles, out Vector3 ga_f, out Vector3 ga_r, out Vector3 ga_u);
            VehiclePhysics.GroundAlign4Point(e, ga_f, ga_r, ga_u, 300f, 100f, 0.25f, 45f);
        }

        // QC walker.qc:558 — turrets_setframe(it, it.animflag, false): stamp the active gait onto the networked
        // Entity.Frame (the same client model-animator seam monsters + the ewheel use), so the walker plays its
        // walk/run/melee/swim/idle animation. In QC the gait enum IS the model frame index (ANIM_NO=0 … ANIM_ROAM=11)
        // and this also sets SendFlags |= TNSF_ANIM + anim_start_time; here Entity.Frame is the net sync, so writing
        // it is the whole job. (Only the CSQC walker_draw head avelocity spin now stays client render.)
        SetFrame(e, (float)ws.AnimFlag);

        // NOTE (client-render): QC also sets SendFlags |= TNSF_MOVE here to net origin/velocity to CSQC; the port
        // networks the moved origin through the shared entity feed, and (with the ground-align now folded onto the
        // networked angles above) the CSQC head avelocity spin is the only remaining pure-client piece of walker_draw.
        e.OldOrigin = e.Origin;
    }

    /// <summary>
    /// QC <c>turrets_setframe(this, frame, false)</c> (sv_turrets.qc:299): stamp the locomotion/gait frame onto
    /// the networked <see cref="Entity.Frame"/> (which the client model animator plays). Same seam EWheel uses.
    /// </summary>
    private static void SetFrame(Entity e, float frame)
    {
        if (e.Frame != frame) e.Frame = frame;
    }

    // walker_move_to (walker.qc:249): set the gait from the distance + waterlevel and the steer goal.
    private void WalkerMoveTo(Entity e, WalkerState ws, Vector3 target, float dist)
    {
        switch (e.WaterLevel)
        {
            case 0: // WATERLEVEL_NONE
                ws.AnimFlag = dist > 500f ? Gait.Run : Gait.Walk;
                goto case 1;
            case 1: // WATERLEVEL_WETFEET
            case 2: // WATERLEVEL_SWIMMING
                ws.AnimFlag = ws.AnimFlag == Gait.Swim ? Gait.Swim : Gait.Walk;
                break;
            case 3: // WATERLEVEL_SUBMERGED
                ws.AnimFlag = Gait.Swim;
                break;
        }
        ws.Moveto = target;
        ws.Steerto = TurretMath.SteerAttract2(e, ws.Moveto, 0.5f, 500f, 0.95f);
        if (e.Enemy is not null) { ws.EnemyLastLoc = target; ws.EnemyLastTime = Api.Services is not null ? Api.Clock.Time : 0f; }
    }

    // walker_move_path (walker.qc:279, non-FANCYPATHING branch): advance the turret_checkpoint chain when close
    // to the current node (turret_closetotarget, 64u), then steer toward it via walker_move_to. A null next node
    // ends the chain (the walker stops following and falls back to idle/roam next think).
    private void WalkerMovePath(Entity e, WalkerState ws, TurretState ts)
    {
        if (ts.PathCurrent is null) return;
        if (CloseToTarget(e, ts.PathCurrent.Origin, PathNodeProximity))
            ts.PathCurrent = NextCheckpoint(ts.PathCurrent);

        if (ts.PathCurrent is null)
            return;

        ws.Moveto = ts.PathCurrent.Origin;
        ws.Steerto = TurretMath.SteerAttract2(e, ws.Moveto, 0.5f, 500f, 0.95f);
        WalkerMoveTo(e, ws, ws.Moveto, 0f);
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
    /// QC <c>turret_closetotarget</c> (sv_turrets.qc:1212): true when <paramref name="targ"/> (expanded by
    /// <paramref name="range"/>) overlaps the turret's bbox (also expanded by <paramref name="range"/>).
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

    // The no-enemy branch of tr_think (walker.qc:362): chase the last-seen spot for up to 10s, else LOS-probe
    // a random wander direction while ANIM_WALK, periodically re-rolling between an idle stop and a roam walk.
    private void IdleSelect(Entity e, WalkerState ws, float now, Vector3 fwd)
    {
        if (ws.EnemyLastTime != 0f)
        {
            if ((e.Origin - ws.EnemyLastLoc).Length() < 128f || now - ws.EnemyLastTime > 10f)
                ws.EnemyLastTime = 0f;
            else
                WalkerMoveTo(e, ws, ws.EnemyLastLoc, 0f);
            return;
        }

        if (ws.AnimFlag != Gait.No && Api.Services is not null)
        {
            // Probe ahead + down: if there's a wall ahead or a ledge below, pick a new wander heading.
            Vector3 from = e.Origin + new Vector3(0f, 0f, 64f);
            TraceResult fwdTr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, from + fwd * 128f, MoveFilter.Normal, e);
            if (fwdTr.Fraction != 1f)
                ws.HeadIdleTime = -1337f;
            else
            {
                TraceResult downTr = Api.Trace.Trace(fwdTr.EndPos, Vector3.Zero, Vector3.Zero,
                    fwdTr.EndPos - new Vector3(0f, 0f, 256f), MoveFilter.Normal, e);
                if (downTr.Fraction == 1f) ws.HeadIdleTime = -1337f;
            }

            if (ws.HeadIdleTime == -1337f)
            {
                ws.Moveto = e.Origin + Prandom.Vec() * 256f;
                ws.HeadIdleTime = 0f;
            }

            ws.Moveto = ws.Moveto * 0.9f + ((e.Origin + fwd * 500f) + Prandom.Vec() * 400f) * 0.1f;
            ws.Moveto = new Vector3(ws.Moveto.X, ws.Moveto.Y, e.Origin.Z + 64f);
            WalkerMoveTo(e, ws, ws.Moveto, 0f);
        }

        if (ws.IdleTime < now)
        {
            // QC walker.qc:403 — 50% chance, OR no TSL_ROAM spawnflag at all -> stop and idle 1..6s; else (TSL_ROAM
            // set AND the 50% roll won) roam-walk a random nearby point for 4..6s.
            if (Prandom.Float() < 0.5f || (e.SpawnFlags & TslRoam) == 0)
            {
                ws.IdleTime = now + 1f + Prandom.Float() * 5f;
                ws.Moveto = e.Origin;
                ws.AnimFlag = Gait.No;
            }
            else
            {
                ws.AnimFlag = Gait.Walk;
                ws.IdleTime = now + 4f + Prandom.Float() * 2f;
                ws.Moveto = e.Origin + Prandom.Vec() * 256f;
                ws.HeadIdleTime = 0f;   // QC: it.tur_head.moveto = it.moveto; it.tur_head.idletime = 0
            }
        }
    }

    // The QC switch(it.animflag) tail of tr_think (walker.qc:463): per-gait move + the raw per-think body yaw.
    private void ApplyGait(Entity e, WalkerState ws, Vector3 fwd, float now)
    {
        // real_angle = vectoangles(steerto) - angles; turny/turnx are raw degrees applied ONCE per think
        // (turret_think runs every frame with nextthink=time, so this is NOT frametime-scaled — matching Base).
        Vector3 realAngle = (ws.Steerto != Vector3.Zero ? QMath.VecToAngles(ws.Steerto) : e.Angles) - e.Angles;
        // v_right for the (unreachable, Base-parity) strafe gaits.
        QMath.AngleVectors(new Vector3(0f, e.Angles.Y, 0f), out _, out Vector3 right, out _);
        float vz = e.Velocity.Z;
        float turny = 0f, turnx = 0f;

        switch (ws.AnimFlag)
        {
            case Gait.No:
                TurretMath.BrakeSimple(e, SpeedStop);
                break;
            case Gait.Turn:
                turny = Turn;  // g_turrets_unit_walker_turn
                TurretMath.BrakeSimple(e, SpeedStop);
                break;
            case Gait.Walk:
                turny = TurnWalk;
                TurretMath.MoveSimple(e, fwd, SpeedWalk, 0.6f);
                break;
            case Gait.Run:
                turny = TurnRun;
                TurretMath.MoveSimple(e, fwd, SpeedRun, 0.6f);
                break;
            // STRAFE_L/R + PAIN are never assigned by the brain (Base never drives them from SVQC either — they are
            // leftover model anim states), but the switch cases are reproduced for full structural parity with the
            // Base switch (walker.qc:492) so the constants/move semantics are carried faithfully.
            case Gait.StrafeL:
                turny = TurnStrafe;
                TurretMath.MoveSimple(e, -right, SpeedWalk, 0.8f);
                break;
            case Gait.StrafeR:
                turny = TurnStrafe;
                TurretMath.MoveSimple(e, right, SpeedWalk, 0.8f);
                break;
            case Gait.Jump:
                vz += SpeedJump;
                break;
            case Gait.Land:
                break;
            case Gait.Pain:
                // QC walker.qc:509 — defer walker_setnoanim 0.25 (then nothing this think). Never reached.
                break;
            case Gait.Melee:
                TurretMath.BrakeSimple(e, SpeedStop);
                break;
            case Gait.Swim:
                turny = TurnSwim;
                turnx = TurnSwim;
                e.Angles = new Vector3(
                    e.Angles.X + QMath.Bound(-10f, TurretMath.ShortAngle(realAngle.X, e.Angles.X), 10f),
                    e.Angles.Y, e.Angles.Z);
                TurretMath.MoveSimple(e, fwd, SpeedSwim, 0.3f);
                vz = e.Velocity.Z + MathF.Sin(now * 4f) * 8f;
                break;
            case Gait.Roam:
                turny = TurnWalk;
                TurretMath.MoveSimple(e, fwd, SpeedRoam, 0.5f);
                break;
        }

        if (turny != 0f)
        {
            turny = QMath.Bound(-turny, TurretMath.ShortAngle(realAngle.Y, e.Angles.Y), turny);
            e.Angles = new Vector3(e.Angles.X, e.Angles.Y + turny, e.Angles.Z);
        }
        if (turnx != 0f)
        {
            turnx = QMath.Bound(-turnx, TurretMath.ShortAngle(realAngle.X, e.Angles.X), turnx);
            e.Angles = new Vector3(e.Angles.X + turnx, e.Angles.Y, e.Angles.Z);
        }

        e.Velocity = new Vector3(e.Velocity.X, e.Velocity.Y, vz);
    }

    // walker_melee_do_dmg (walker_weapon.qc): a radius hit 128u in front of the walker.
    private void ScheduleMelee(Entity e, float when)
    {
        // The walker entity's Think is owned by the turret loop; schedule the swipe on a throwaway timer entity.
        if (Api.Services is null) return;
        Entity timer = Api.Entities.Spawn();
        timer.ClassName = "walker_melee_timer";
        timer.Owner = e;
        timer.NextThink = when;
        timer.Think = t =>
        {
            Entity w = t.Owner!;
            if (!w.IsFreed && w.DeadState == DeadFlag.No && Api.Services is not null)
            {
                QMath.AngleVectors(w.Angles, out Vector3 f, out _, out _);
                Vector3 at = w.Origin + f * 128f;
                foreach (Entity victim in Api.Entities.FindInRadius(at, 32f))
                {
                    if (ReferenceEquals(victim, w) || ReferenceEquals(victim.Owner, w)) continue;
                    if (!TurretAI.ValidTarget(w, victim, Select, TargetRangeMin, TargetRange)) continue;
                    // walker.qc: the melee bite is DEATH_TURRET_WALK_MELEE.
                    Combat.Damage(victim, w, w, MeleeDamage, DeathTypes.TurretWalkMelee,
                        victim.Origin, f * MeleeForce);
                }
            }
            Api.Entities.Remove(t);
        };
    }

    // walker_fire_rocket (walker.qc:204): launch a homing rocket from a forward/up direction with jitter. The
    // muzzle org is the alternating tag_rocket01/02 head tag (walker.qc:447); when the model service is headless
    // (no tags), fall back to the shared shot org — same graceful pattern as TurretAI.ShotOrigin/tag_fire.
    private void FireRocket(Entity turret, Entity enemy, string muzzleTag)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 org = st.ShotOrg;
        if (Api.Services is not null
            && Api.Models.TryGetTag(turret, muzzleTag, out Vector3 tagOrg, out _, out _, out _))
            org = tagOrg;

        QMath.AngleVectors(turret.Angles, out Vector3 fwd, out _, out Vector3 up);
        Vector3 dir = QMath.Normalize((fwd + up * 0.5f) + Prandom.Vec() * 0.2f);

        GuidedProjectile.Launch(turret, enemy, org, dir, GuidedProjectile.Mode.WalkerRocket,
            launchSpeed: RocketSpeed, speedMax: RocketSpeed, speedGain: 1f, turnRate: RocketTurnRate,
            size: 6f, health: 25f, RocketDamage, RocketRadius, RocketForce, DeathTypes.TurretWalkRocket, ttl: 9f);

        // QC walker_fire_rocket: te_explosion(org) launch flash at the muzzle (a networked temp-entity, emitted
        // server-side so it reaches all viewing clients identically — same convention as the spark above).
        // (Base emits NO EFFECT_BLASTER_MUZZLEFLASH for rockets — only te_explosion — so none here either.)
        if (Api.Services is not null)
        {
            EffectEmitter.TeExplosion(org);
            // QC: SND_TUR_WALKER_FIRE = W_Sound("hagar_fire") (all.inc:154), NOT the rocket-launch sound.
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/hagar_fire.wav");
        }

        // NOTE: the rocket's 1% up-and-over walker_rocket_loop maneuver (1% on launch + 1%/in-flight think) lives
        // in GuidedProjectile (Launch + WalkerRocketLoop/2/3).
    }

    // METHOD(WalkerTurretAttack, wr_think) — walker_weapon.qc: minigun bullet along the muzzle dir with spread
    // + knockback force, plus the networked EFFECT_BULLET tracer and EFFECT_BLASTER_MUZZLEFLASH (server-emitted).
    private void Attack(Entity turret, Entity enemy)
    {
        TurretState st = TurretAI.State(turret);
        Vector3 dir = QMath.Normalize(st.AimPos - st.ShotOrg);
        if (dir == Vector3.Zero) dir = QMath.Forward(TurretAI.HeadWorldAngles(turret));

        // walker_weapon.qc:22 — fireBullet(... EFFECT_BULLET) with DEATH_TURRET_WALK_GUN. The EFFECT_BULLET
        // tracer is OUTSIDE the `if (isPlayer)` gate, so it is emitted on the turret path (turret-visible bullet
        // trail), via the same EffectEmitter seam the player/machinegun-turret path uses.
        TurretCombat.FireBullet(turret, st.ShotOrg, dir, ShotSpread, ShotDamage, ShotForce,
            DeathTypes.TurretWalkGun, tracerEffect: "BULLET");

        if (Api.Services is not null)
        {
            Api.Sound.Play(turret, SoundChannel.Weapon, "weapons/uzi_fire.wav");

            // walker_weapon.qc:23 — Send_Effect(EFFECT_BLASTER_MUZZLEFLASH, tur_shotorg, tur_shotdir_updated*1000, 1).
            // Also OUTSIDE the `if (isPlayer)` gate. Base attaches it at the head muzzle tag; we have no tur_head
            // sub-entity, so emit the flash at the muzzle org along the shot dir — the same convention as the
            // ewheel/flac/machinegun turrets (a networked temp-effect, viewer-independent).
            EffectEmitter.Emit("BLASTER_MUZZLEFLASH", st.ShotOrg, dir * 1000f, 1);
        }

        // NOTE (client-render, still deferred): the head-bone walk/run/melee anim frames (turrets_setframe +
        // TNSF_MOVE) — no tur_head sub-entity / tag in this server port.
    }
}

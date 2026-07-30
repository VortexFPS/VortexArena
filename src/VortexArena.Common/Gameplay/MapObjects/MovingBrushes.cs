// Port of the continuously-moving brush families:
//   qcsrc/common/mapobjects/func/rotating.qc -> func_rotating  (constant spin)
//   qcsrc/common/mapobjects/func/bobbing.qc  -> func_bobbing   (sine translation)
//   qcsrc/common/mapobjects/func/pendulum.qc -> func_pendulum  (sine roll)
//   qcsrc/common/mapobjects/func/train.qc    -> func_train     (path_corner chain)
//
// These are MOVETYPE_PUSH brushes that never stop:
//  - func_rotating spins at a constant angular velocity about one axis.
//  - func_bobbing / func_pendulum spawn a "controller" sub-entity that re-evaluates a sine wave every 0.1s
//    and drives the parent's velocity / angular velocity so it arrives at the next sample in 0.1s (QC's
//    makevectors-sine technique, ported faithfully).
//  - func_train rides between path_corner waypoints via SUB_CalcMove, waiting `.wait` at each.
//
// Ported in full now: func_rotating target-toggled spin (setactive) WITH its looping .noise ambient on
// CH_AMBIENT_SINGLE (started/stopped in setactive, kicked off at spawn via reset, exactly as rotating.qc),
// the func_train path_corner chain with per-corner speed/wait, TRAIN_TURN orientation toward the next corner,
// TRAIN_CURVE bezier control points (curvetarget), TRAIN_NEEDACTIVATION (use to start), and target_random.
// The looping ambient is a PERSISTENT facade loop (the headless analogue of QC's per-player MSG_ONE
// init_for_player resend — no client roster at this layer). Genuinely out of scope: the CSQC bits.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>func_rotating</c> / <c>func_bobbing</c> / <c>func_pendulum</c> / <c>func_train</c>. Each setup is a spawnfunc.</summary>
public static class MovingBrushes
{
    // ---- rotating.qh ----
    public const int RotatingXAxis = 1 << 2; // FUNC_ROTATING_XAXIS
    public const int RotatingYAxis = 1 << 3; // FUNC_ROTATING_YAXIS
    public const int RotatingStartOff = 1 << 4; // FUNC_ROTATING_STARTOFF

    // ---- bobbing.qh ----
    public const int BobbingXAxis = 1 << 0; // BOBBING_XAXIS
    public const int BobbingYAxis = 1 << 1; // BOBBING_YAXIS

    // ---- train.qh ----
    public const int TrainCurve = 1 << 0;          // TRAIN_CURVE
    public const int TrainTurn = 1 << 1;           // TRAIN_TURN
    public const int TrainNeedActivation = 1 << 2; // TRAIN_NEEDACTIVATION

    // ===================================================================
    //  func_rotating
    // ===================================================================

    /// <summary><c>spawnfunc(func_rotating)</c> — spins in place at a constant angular velocity.</summary>
    public static void RotatingSetup(Entity this_)
    {
        if (this_.Speed == 0f)
            this_.Speed = 100f;

        // axis selection (QC: default Z). avelocity components are (pitch, yaw, roll).
        if ((this_.SpawnFlags & RotatingXAxis) != 0)
            this_.AVelocity = new Vector3(0f, 0f, this_.Speed); // roll
        else if ((this_.SpawnFlags & RotatingYAxis) != 0)
            this_.AVelocity = new Vector3(this_.Speed, 0f, 0f); // pitch
        else
            this_.AVelocity = new Vector3(0f, this_.Speed, 0f); // yaw

        this_.Pos1 = this_.AVelocity; // remembered spin (restored by setactive)

        if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message))
            this_.Message = "was squished";
        if (this_.Dmg != 0f && this_.CrushInterval == 0f)
            this_.CrushInterval = 0.25f;
        this_.CrushNextTime = MapMover.Now();

        this_.ClassName = "func_rotating";
        if (!MapMover.InitMovingBrushTrigger(this_))
            return;

        this_.Blocked = MapMover.GenericPlatBlocked;
        this_.Use = RotatingUse;          // a targeted func_rotating toggles its spin when triggered
        this_.Reset = RotatingReset;      // QC rotating.qc: this.reset = func_rotating_reset (round restart)

        MapMover.IndexRegister(this_);

        // QC rotating.qc:103-104: this.reset = func_rotating_reset; this.reset(this) — the spawn-time reset call
        // is what sets the initial active state AND starts the looping .noise ambient via setactive(ACTIVE_ACTIVE)
        // (STARTOFF -> setactive(ACTIVE_NOT), spun-down + silent). We mirror that here instead of poking .Active
        // directly, so the ambient now starts at spawn exactly as Base does.
        RotatingReset(this_);

        // QC parks a far-future think purely so PushMove keeps simulating it; the engine sim integrates the
        // constant avelocity. We leave Think null and rely on the MOVETYPE_PUSH integrator.
    }

    /// <summary>QC <c>func_rotating</c> <c>.use</c>: a trigger toggles the spin on/off (via setactive TOGGLE).</summary>
    public static void RotatingUse(Entity self, Entity actor)
    {
        RotatingSetActive(self, MapMover.ActiveToggle);
    }

    /// <summary>QC <c>func_rotating_setactive</c>: toggle the spin on/off.</summary>
    public static void RotatingSetActive(Entity this_, int state)
    {
        if (state == MapMover.ActiveToggle)
            this_.Active = this_.Active == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
        else
            this_.Active = state;

        if (this_.Active == MapMover.ActiveNot)
        {
            // QC rotating.qc:16-20: avelocity 0 + stopsound(this, CH_AMBIENT_SINGLE).
            this_.AVelocity = Vector3.Zero;
            MapMover.StopAmbient(this_, SoundChannel.AmbientSingle);
        }
        else
        {
            // QC rotating.qc:21-28: restore the stored spin + (re)start the looping .noise on CH_AMBIENT_SINGLE.
            this_.AVelocity = this_.Pos1;
            MapMover.LoopAmbient(this_, this_.Noise, SoundChannel.AmbientSingle);
        }
    }

    /// <summary>
    /// QC <c>func_rotating_reset</c> (rotating.qc:31): re-apply the spawn active state on a round restart —
    /// STARTOFF rotators stop, all others spin. (QC leaves angles as a TODO, so we do too.)
    /// </summary>
    public static void RotatingReset(Entity this_)
    {
        RotatingSetActive(this_, (this_.SpawnFlags & RotatingStartOff) != 0
            ? MapMover.ActiveNot
            : MapMover.ActiveActive);
    }

    // ===================================================================
    //  func_bobbing
    // ===================================================================

    /// <summary><c>spawnfunc(func_bobbing)</c> — translates back and forth along one axis on a sine wave.</summary>
    public static void BobbingSetup(Entity this_)
    {
        if (this_.Speed == 0f) this_.Speed = 4f;       // seconds per cycle
        if (this_.Height == 0f) this_.Height = 32f;     // travel

        this_.DestVec = this_.Origin;                   // center of motion
        this_.MoverCnt = 360f / this_.Speed;            // degrees/sec timescale (QC stores 360/speed in .cnt)
        this_.Active = MapMover.ActiveActive;

        this_.Blocked = MapMover.GenericPlatBlocked;
        if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message))
            this_.Message = "was squished";
        if (this_.Dmg != 0f && this_.CrushInterval == 0f)
            this_.CrushInterval = 0.25f;
        this_.CrushNextTime = MapMover.Now();

        // travel direction * height
        if ((this_.SpawnFlags & BobbingXAxis) != 0)
            this_.MoveDir = new Vector3(this_.Height, 0f, 0f);
        else if ((this_.SpawnFlags & BobbingYAxis) != 0)
            this_.MoveDir = new Vector3(0f, this_.Height, 0f);
        else
            this_.MoveDir = new Vector3(0f, 0f, this_.Height);

        this_.ClassName = "func_bobbing";
        if (!MapMover.InitMovingBrushTrigger(this_))
            return;

        MapMover.IndexRegister(this_);

        // QC bobbing.qc:35-39: looping ambient soundto(MSG_INIT, this, CH_TRIGGER_SINGLE, .noise, VOL_BASE,
        // ATTEN_IDLE, 0) — a persistent loop on the mover, replayed to every (incl. late-joining) client.
        MapMover.LoopAmbient(this_, this_.Noise);

        // Spawn the controller that drives the sine every 0.1s (QC func_bobbing_controller).
        if (Api.Services is not null)
        {
            Entity controller = Api.Entities.Spawn();
            controller.ClassName = "func_bobbing_controller";
            controller.Owner = this_;
            controller.Think = BobbingControllerThink;
            controller.NextThink = MapMover.Now() + 1f;
        }
    }

    /// <summary>QC <c>func_bobbing_controller_think</c>: sine-drive the parent's velocity to the next sample.</summary>
    private static void BobbingControllerThink(Entity self)
    {
        Entity owner = self.Owner!;
        self.NextThink = MapMover.Now() + 0.1f;

        if (owner.Active != MapMover.ActiveActive)
        {
            owner.Velocity = Vector3.Zero;
            return;
        }

        // makevectors((nextthink * cnt + phase*360) about yaw) -> v_forward.y is the sine term.
        float angDeg = self.NextThink * owner.MoverCnt + owner.Phase * 360f;
        Vector3 fwd = QMath.Forward(new Vector3(0f, angDeg, 0f));
        Vector3 target = owner.DestVec + owner.MoveDir * fwd.Y;
        // *10 so it arrives in 0.1s.
        owner.Velocity = (target - owner.Origin) * 10f;
    }

    // ===================================================================
    //  func_pendulum
    // ===================================================================

    /// <summary><c>spawnfunc(func_pendulum)</c> — swings about the roll axis on a sine wave.</summary>
    public static void PendulumSetup(Entity this_)
    {
        if (this_.Speed == 0f) this_.Speed = 30f;
        this_.Active = MapMover.ActiveActive;

        this_.Blocked = MapMover.GenericPlatBlocked;
        if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message))
            this_.Message = "was squished";
        if (this_.Dmg != 0f && this_.CrushInterval == 0f)
            this_.CrushInterval = 0.25f;
        this_.CrushNextTime = MapMover.Now();

        this_.AVelocity = new Vector3(0f, 0f, 0.0000001f); // kick PushMove
        this_.ClassName = "func_pendulum";
        if (!MapMover.InitMovingBrushTrigger(this_))
            return;

        MapMover.IndexRegister(this_);

        // QC pendulum.qc:28-31: looping ambient soundto(MSG_INIT, this, CH_TRIGGER_SINGLE, .noise, VOL_BASE,
        // ATTEN_IDLE, 0) — persistent loop on the mover (replayed to late joiners via entity sound state).
        MapMover.LoopAmbient(this_, this_.Noise);

        if (this_.Freq == 0f)
        {
            // pendulum length formula (Q3A): freq = 1/(2pi) * sqrt(g / (3*max(8, |mins.z|)))
            float g = Api.Services is null ? 800f : Api.Cvars.GetFloat("sv_gravity");
            if (g == 0f) g = 800f;
            this_.Freq = 1f / (QMath.Pi * 2f) * MathF.Sqrt(g / (3f * MathF.Max(8f, MathF.Abs(this_.Mins.Z))));
        }

        this_.MoverCnt = this_.Angles.Z; // initial/rest roll (QC stores angles_z in .cnt)

        if (Api.Services is not null)
        {
            Entity controller = Api.Entities.Spawn();
            controller.ClassName = "func_pendulum_controller";
            controller.Owner = this_;
            controller.Think = PendulumControllerThink;
            controller.NextThink = MapMover.Now() + 1f;
        }
    }

    /// <summary>QC <c>func_pendulum_controller_think</c>: sine-drive the parent's roll angular velocity.</summary>
    private static void PendulumControllerThink(Entity self)
    {
        Entity owner = self.Owner!;
        self.NextThink = MapMover.Now() + 0.1f;

        if (owner.Active != MapMover.ActiveActive)
        {
            owner.AVelocity = new Vector3(owner.AVelocity.X, owner.AVelocity.Y, 0f);
            return;
        }

        // makevectors((nextthink*freq + phase) about yaw*360) -> v_forward.y sine; target roll = speed*sine + cnt.
        float angDeg = (self.NextThink * owner.Freq + owner.Phase) * 360f;
        Vector3 fwd = QMath.Forward(new Vector3(0f, angDeg, 0f));
        float targetRoll = owner.Speed * fwd.Y + owner.MoverCnt;
        float deltaRoll = Remainder(targetRoll - owner.Angles.Z, 360f);
        owner.AVelocity = new Vector3(owner.AVelocity.X, owner.AVelocity.Y, deltaRoll * 10f);
    }

    /// <summary>QC <c>remainder(a, b)</c>: signed remainder of a/b nearest zero (C fmod-with-round semantics).</summary>
    private static float Remainder(float a, float b)
        => b == 0f ? a : a - b * MathF.Round(a / b);

    // ===================================================================
    //  path_corner (misc/corner.qc) — func_train waypoints
    // ===================================================================

    /// <summary><c>spawnfunc(path_corner)</c> — a waypoint a func_train rides between. Indexed so trains find it.</summary>
    public static void PathCornerSetup(Entity this_)
    {
        this_.ClassName = "path_corner";
        // QC corner.qc: set_platmovetype(this, this.platmovetype) — parse this corner's per-corner ease/turn
        // override string ("start end [force]") into PlatMoveStart/PlatMoveEnd so a riding func_train can pick it
        // up in TrainNext. A corner with no platmovetype key leaves the 0/0 smoothstep default.
        MapMover.SetPlatMoveType(this_, this_.Platmovetype);
        MapMover.SetOrigin(this_, this_.Origin);
        MapMover.IndexRegister(this_);
    }

    // ===================================================================
    //  func_train
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(func_train)</c> — rides a chain of path_corner waypoints, looping. Supports TRAIN_TURN
    /// (orient toward the next corner while waiting), TRAIN_CURVE (bezier through a corner's curvetarget
    /// control point), TRAIN_NEEDACTIVATION (wait for a trigger), per-corner speed/wait, and target_random.
    /// </summary>
    public static void TrainSetup(Entity this_)
    {
        if (string.IsNullOrEmpty(this_.Target))
            return; // QC objerrors; headless: inert
        if (this_.Speed == 0f)
            this_.Speed = 100f;

        this_.ClassName = "func_train";
        if (!MapMover.InitMovingBrushTrigger(this_))
            return;

        if ((this_.SpawnFlags & TrainNeedActivation) != 0)
            this_.Use = TrainUse;

        if ((this_.SpawnFlags & TrainTurn) != 0)
        {
            // a turning train rotates about its own origin (view_ofs 0), not its lower corner.
            this_.PlatMoveTurn = true;
            this_.DestVec = Vector3.Zero; // reuse DestVec as the view_ofs offset
        }
        else
        {
            this_.DestVec = this_.Mins; // QC: view_ofs = mins for a non-turning train
        }

        this_.Blocked = MapMover.GenericPlatBlocked;
        if (this_.Dmg != 0f && string.IsNullOrEmpty(this_.Message))
            this_.Message = "was squished";
        if (this_.Dmg != 0f && this_.CrushInterval == 0f)
            this_.CrushInterval = 0.25f;
        this_.CrushNextTime = MapMover.Now();

        // QC: if(!set_platmovetype(this, this.platmovetype)) return; then stash the parsed start/end as the train's
        // defaults (platmovetype_start_default / _end_default) — restored in TrainNext whenever a corner carries no
        // override. The port re-derives the default by re-parsing this.Platmovetype (the train's own key string).
        if (!MapMover.SetPlatMoveType(this_, this_.Platmovetype))
            return;

        MapMover.IndexRegister(this_);

        // Find the first path_corner and snap onto it, then schedule the first move.
        TrainFind(this_);
    }

    /// <summary>QC <c>train_next_find</c>: the next corner — weighted-random if target_random, else the first.</summary>
    private static Entity? TrainNextFind(Entity this_)
    {
        if (this_.TargetRandom)
        {
            var sel = new MapMover.RandomSelection();
            sel.Reset();
            foreach (Entity t in MapMover.FindByTargetName(this_.Target))
                sel.Add(t, 1f, 0f);
            return sel.Chosen;
        }
        return MapMover.FindFirstByTargetName(this_.Target);
    }

    /// <summary>QC <c>func_train_find</c>: snap to the first corner, stash the lookahead, queue the first leg.</summary>
    private static void TrainFind(Entity this_)
    {
        Entity? targ = TrainNextFind(this_);
        if (targ is null)
            return;
        // advance .target to the corner's target (the next hop), and remember the one after for turning.
        this_.Target = targ.Target;
        this_.TargetRandom = targ.TargetRandom;
        this_.FutureTarget = TrainNextFind(targ);
        MapMover.SetOrigin(this_, targ.Origin - this_.DestVec);

        if ((this_.SpawnFlags & TrainNeedActivation) == 0)
        {
            this_.Think = TrainNext;
            this_.NextThink = this_.LTime + 1f;
        }
        // else: wait for TrainUse() to start.
    }

    /// <summary>
    /// QC <c>train_next</c>: move to the current target corner; on arrival, wait then continue. Exposed as
    /// <c>internal</c> so a path-following dynlight (DynamicLight.FindPath) can reuse the func_train pathing
    /// instead of parking on its first corner.
    /// </summary>
    internal static void TrainNext(Entity this_)
    {
        Entity? targ = this_.FutureTarget;
        if (targ is null)
            return;

        // remember this corner's next hop + dwell for after we arrive.
        this_.Target = targ.Target;
        this_.TargetRandom = targ.TargetRandom;
        this_.FutureTarget = TrainNextFind(targ);
        this_.GoalEntity = targ;
        this_.Wait = targ.Wait != 0f ? targ.Wait : 0.1f;

        // QC train_next: a corner with a platmovetype key overrides this leg's ease curve; otherwise restore the
        // train's own default. PathCornerSetup already parsed the corner's string into its PlatMoveStart/End.
        if (!string.IsNullOrEmpty(targ.Platmovetype))
        {
            this_.PlatMoveStart = targ.PlatMoveStart;
            this_.PlatMoveEnd = targ.PlatMoveEnd;
        }
        else
        {
            // no corner override — re-parse the train's own platmovetype key to restore platmovetype_*_default.
            MapMover.SetPlatMoveType(this_, this_.Platmovetype);
        }

        float speed = targ.Speed != 0f ? targ.Speed : this_.Speed;
        Vector3 dest = targ.Origin - this_.DestVec;

        // TRAIN_CURVE: bezier through the corner's curvetarget control point (if set).
        Entity? cp = null;
        if ((this_.SpawnFlags & TrainCurve) != 0 && !string.IsNullOrEmpty(targ.CurveTarget))
            cp = MapMover.FindFirstByTargetName(targ.CurveTarget);

        if (cp is not null)
            MapMover.CalcMoveBezier(this_, cp.Origin - this_.DestVec, dest, MapMover.SpeedType.Linear, speed, TrainWait);
        else
            MapMover.CalcMove(this_, dest, MapMover.SpeedType.Linear, speed, TrainWait);

        // QC train_next:136-137: _sound(this, CH_TRIGGER_SINGLE, .noise, VOL_BASE, ATTEN_IDLE) — the moving noise.
        if (Api.Services is not null && !string.IsNullOrEmpty(this_.Noise))
            Api.Sound.Play(this_, SoundChannel.Item, this_.Noise, MapMover.VolBase, MapMover.AttenIdle);
    }

    /// <summary>
    /// QC <c>train_wait</c>: fire the arrived corner's targets; if TRAIN_TURN is set, rotate toward the next
    /// corner during the dwell first; then advance to the next leg (after the wait, or immediately for &lt; 0).
    /// </summary>
    private static void TrainWait(Entity this_)
    {
        // a turning train rotates toward the next point while waiting (QC train_wait turn branch).
        if (this_.PlatMoveTurn && !this_.TrainWaitTurning)
        {
            Entity? targ = this_.FutureTarget;
            if (targ is not null)
            {
                Entity? cp = ((this_.SpawnFlags & TrainCurve) != 0 && !string.IsNullOrEmpty(targ.CurveTarget))
                    ? MapMover.FindFirstByTargetName(targ.CurveTarget) : null;
                Vector3 aimAt = cp is not null ? cp.Origin : targ.Origin;
                // QC train_next: ang = vectoangles(...); ang_x = -ang_x ("flip up/down orientation") == fixedvectoangles.
                Vector3 ang = QMath.FixedVecToAngles(aimAt - (this_.Origin - this_.DestVec));

                float turnTime = this_.Wait > 0f ? this_.LTime - MapMover.Now() + this_.Wait : 0.0000001f;
                MapMover.CalcAngleMove(this_, ang, MapMover.SpeedType.Time, turnTime, TrainWait);
                this_.TrainWaitTurning = true;
                return;
            }
        }

        // QC train_wait:40-41: stopsoundto(MSG_BROADCAST, this, CH_TRIGGER_SINGLE) — silence the moving noise on
        // arrival at the corner (it resumes in train_next on the next leg). CH_TRIGGER_SINGLE = SoundChannel.Item.
        if (Api.Services is not null)
            Api.Sound.Stop(this_, SoundChannel.Item);

        // fire the corner we arrived at.
        Entity? corner = this_.GoalEntity;
        if (corner is not null)
            MapMover.UseTargets(corner, null, null);
        this_.GoalEntity = null;

        // QC train_wait: if the NEXT corner is flagged TRAIN_NEEDACTIVATION, pause here and re-arm train_use so a
        // trigger must restart the train at this intermediate corner (multi-segment "wait for activation" trains).
        Entity? tg = this_.FutureTarget;
        if (tg is not null && (tg.SpawnFlags & TrainNeedActivation) != 0)
        {
            this_.TrainWaitTurning = false;
            this_.Use = TrainUse;
            this_.Think = null;
            this_.NextThink = 0f;
        }
        else if (this_.Wait < 0f || this_.TrainWaitTurning) // no waiting, or we already waited while turning
        {
            this_.TrainWaitTurning = false;
            TrainNext(this_);
        }
        else
        {
            this_.Think = TrainNext;
            this_.NextThink = this_.LTime + this_.Wait;
        }
    }

    /// <summary>QC <c>train_use</c>: start a TRAIN_NEEDACTIVATION train moving; a trigger's target2 retargets it.</summary>
    public static void TrainUse(Entity self, Entity actor)
    {
        self.NextThink = self.LTime + 1f;
        self.Think = TrainNext;
        self.Use = null; // one-shot; the next corner can re-arm if needed
        // QC: if(trigger.target2) this.future_target = find(targetname, trigger.target2). The trigger isn't
        // passed to a .use(self, actor) here; a retarget would come through the actor's target2 if present.
        if (!string.IsNullOrEmpty(actor.Target2))
        {
            Entity? retarget = MapMover.FindFirstByTargetName(actor.Target2);
            if (retarget is not null)
                self.FutureTarget = retarget;
        }
    }
}

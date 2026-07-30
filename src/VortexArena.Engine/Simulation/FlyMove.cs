using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// Shared physics context handed to the MOVETYPE integrators — the C# stand-in for the globals
/// (sv.time, sv.frametime, sv_gravity) and the small set of engine routines (SV_PushEntity, SV_Impact,
/// SV_LinkEdict, SV_LinkEdict_TouchAreaGrid, SV_NudgeOutOfSolid) that Darkplaces' sv_phys.c functions
/// call. One instance lives on the SimulationLoop.
/// </summary>
public sealed class PhysicsContext
{
    public ITraceService Trace { get; }

    /// <summary>Relink an entity into the broadphase + recompute AbsMin/AbsMax (DP SV_LinkEdict).</summary>
    public Action<Entity> LinkEdict { get; }

    /// <summary>
    /// The live entity list (DP SV_EntitiesInBox source). Supplies the pusher its candidate riders and the
    /// touch-area-grid its trigger candidates. Defaults to an empty set when the host doesn't wire one.
    /// </summary>
    public Func<IReadOnlyList<Entity>> Entities { get; set; } = static () => System.Array.Empty<Entity>();

    /// <summary>
    /// The entity-area-grid broadphase (D1): fill the supplied list with every entity whose XY footprint overlaps
    /// the box. Wired by the loop to <c>EntityService.EntitiesInBox</c>; when set, <see cref="TouchAreaGrid"/>
    /// queries only the triggers near the mover instead of scanning every entity. Null on a host that didn't wire
    /// it (the touch pass falls back to the flat <see cref="Entities"/> scan — identical result, just slower).
    /// </summary>
    public Action<Vector3, Vector3, System.Collections.Generic.List<Entity>>? EntitiesInBox { get; set; }

    // Re-entrancy-safe candidate-list pool for TouchAreaGrid: a trigger .touch can recursively move another
    // entity (a nested TouchAreaGrid), so each call rents its own list (a distinct one when nested) and returns
    // it on exit — alloc-free in steady state (the common non-nested case reuses one list).
    private readonly System.Collections.Generic.Stack<System.Collections.Generic.List<Entity>> _touchListPool = new();

    /// <summary>
    /// DP <c>PRVM_EDICT_MARK_SETORIGIN_CAUGHT</c> probe: a monotonically-increasing counter the entity
    /// service bumps whenever a QC touch/think calls setorigin. SV_Impact / SV_PushEntity compare it
    /// across a touch call to detect "the move was aborted because a touch teleported the entity".
    /// </summary>
    public Func<int> SetOriginEpoch { get; set; } = static () => 0;

    /// <summary>Current tick length in seconds (sv.frametime, = 1/72).</summary>
    public float FrameTime;

    /// <summary>Current sim time at the start of the tick (sv.time).</summary>
    public float Time;

    /// <summary>World gravity in u/s² (sv_gravity, default 800).</summary>
    public float Gravity = 800f;

    /// <summary>
    /// DP gameplay-fix toggle: when true, gravity is applied as two half-steps straddling the move
    /// (sv_gameplayfix_gravityunaffectedbyticrate) which makes jump height independent of tickrate.
    /// Xonotic runs with this ON. (spec §5 "ticrate-dependent gravity half-step")
    /// </summary>
    public bool GravityUnaffectedByTicrate = true;

    /// <summary>DP sv_gameplayfix_nudgeoutofsolid: try to push a stuck entity out of solid before giving up.</summary>
    public bool NudgeOutOfSolid = true;

    /// <summary>
    /// Emit a server sound (DP SV_StartSound) — used by the movetype integrators for splash / land /
    /// hit-ground events. Wired to the SoundService by the loop; a no-op by default so the engine never
    /// depends on the ambient <see cref="XonoticGodot.Common.Services.Api"/>.
    /// </summary>
    public Action<Entity, SoundChannel, string> PlaySound { get; set; } = static (_, _, _) => { };

    public PhysicsContext(ITraceService trace, Action<Entity> linkEdict)
    {
        Trace = trace;
        LinkEdict = linkEdict;
    }

    /// <summary>SV_Gravity (sv_phys.c:1410): per-entity gravity scale × world gravity × frametime.</summary>
    public float EntGravity(Entity ent)
    {
        float g = ent.Gravity;
        if (g == 0f) g = 1f;
        return g * Gravity * FrameTime;
    }

    /// <summary>
    /// The world edict stand-in (DP entity 0). The port's traces return a null <c>trace.Ent</c> for world-brush
    /// hits (callers read null == world), but SV_Impact still needs a non-null <c>other</c> to dual-dispatch the
    /// moving entity's <c>.touch</c> against — that's what makes a projectile explode on a wall. A single shared,
    /// inert (BSP, non-damageable, touchless) sentinel mirrors DP's worldspawn for that one purpose.
    /// </summary>
    private static readonly Entity WorldSentinel = new() { ClassName = "worldspawn", Solid = Solid.Bsp, TakeDamage = DamageMode.No };

    /// <summary>
    /// SV_Impact (sv_phys.c:1050): two entities touched — run BOTH touch functions, each seeing the
    /// other as <c>other</c>, with the trace globals set appropriately (e2 sees the negated plane).
    /// Returns false if a touch teleported <paramref name="e1"/> via setorigin (DP's
    /// PRVM_EDICT_MARK_SETORIGIN_CAUGHT path), so the slide-move can abort (blocked |= 8).
    /// </summary>
    public bool Impact(Entity e1, in TraceResult trace, Entity? e2)
    {
        if (e2 == null) return true;

        int epoch = SetOriginEpoch();

        if (!e1.IsFreed && !e2.IsFreed && e1.Touch != null && e1.Solid != Solid.Not)
            e1.Touch(e1, e2);

        if (!e1.IsFreed && !e2.IsFreed && e2.Touch != null && e2.Solid != Solid.Not)
            e2.Touch(e2, e1); // e2 is self, e1 is other (DP negates the plane for e2's trace globals)

        // SV_Impact returns false when e1 was teleported by a setorigin inside a touch function: the
        // epoch advanced, so the caller's pre-move origin is stale and the move must abort.
        if (SetOriginEpoch() != epoch)
            return false;
        return true;
    }

    /// <summary>
    /// SV_PushEntity (sv_phys.c:1521): trace the entity's box from its origin along <paramref name="push"/>,
    /// move it to the impact point, relink, and (if <paramref name="doTouch"/>) fire touch dual-dispatch.
    /// Returns false if the move was aborted by a teleport in a touch function (blocked |= 8).
    /// Does NOT modify velocity.
    /// </summary>
    public bool PushEntity(out TraceResult trace, Entity ent, Vector3 push, bool doTouch)
    {
        Vector3 start = ent.Origin;
        Vector3 end = start + push;

        MoveFilter type = MoveFilterFor(ent);
        trace = Trace.Trace(start, ent.Mins, ent.Maxs, end, type, ent);

        // SV_NudgeOutOfSolid (sv_phys.c:1549): if we started fully embedded, try to extricate the entity
        // and re-run the push from the unstuck origin. If it can't escape the WORLD, abort the move
        // (leave the entity where it is) like DP's worldstartsolid early-out. DP passes checkstuck=false
        // for MOVETYPE_FLY so fly traps can move while stuck — honour that by skipping the nudge for FLY.
        if (trace.AllSolid && NudgeOutOfSolid && ent.MoveType != MoveType.Fly)
        {
            if (TryNudgeOutOfSolid(ent))
            {
                start = ent.Origin;
                end = start + push;
                trace = Trace.Trace(start, ent.Mins, ent.Maxs, end, type, ent);
            }
            else if (trace.StartSolid) // couldn't unstick and we're stuck in the world → abort
            {
                return true;
            }
        }

        ent.Origin = trace.EndPos;
        ent.OldOrigin = trace.EndPos;
        LinkEdict(ent);

        if (doTouch)
        {
            // SV_LinkEdict_TouchAreaGrid — fire SOLID_TRIGGER .touch for every trigger the entity now overlaps.
            TouchAreaGrid(ent);

            // The blocking impact (the solid we actually hit), DP order. SV_Impact runs against whatever the sweep
            // hit — another entity (trace.Ent) OR the world brush itself. The port's traces report a null trace.Ent
            // for the world, so substitute the world sentinel on a blocked sweep; without this a projectile slides
            // along / rests on geometry instead of exploding on contact (its .touch never fired vs the world).
            Entity? other = trace.Ent;
            if (other == null && trace.Fraction < 1f)
                other = WorldSentinel;
            if (ent.Solid >= Solid.Trigger && other != null &&
                ((ent.Flags & EntFlags.OnGround) == 0 || ent.GroundEntity != other))
            {
                return Impact(ent, trace, other);
            }
        }

        return true;
    }

    /// <summary>The trace filter DP picks for an entity's own movement (SV_PushEntity / walk.qc type select).</summary>
    private static MoveFilter MoveFilterFor(Entity ent)
    {
        if (ent.MoveType == MoveType.FlyMissile) return MoveFilter.Missile;
        if (ent.MoveType == MoveType.FlyWorldOnly) return MoveFilter.WorldOnly;
        if (ent.Solid == Solid.Trigger || ent.Solid == Solid.Not) return MoveFilter.NoMonsters; // bmodels only
        return MoveFilter.Normal;
    }

    /// <summary>
    /// SV_LinkEdict_TouchAreaGrid (sv_phys.c:727): for every SOLID_TRIGGER whose box overlaps
    /// <paramref name="ent"/>'s box, fire that trigger's <c>.touch</c> with the trigger as self and
    /// <paramref name="ent"/> as other (SV_LinkEdict_TouchAreaGrid_Call sets a clean "just touched" trace).
    /// A SOLID_NOT entity never triggers touches (DP early-out).
    /// </summary>
    public void TouchAreaGrid(Entity ent)
    {
        if (EntitiesInBox is null)
        {
            TriggerTouch.Run(Entities(), ent); // no grid wired (test/host) — flat scan, identical result
            return;
        }
        // Broadphase (D1): only the triggers whose footprint overlaps the mover's box (+ DP's 1-unit expansion).
        // TriggerTouch.Run re-checks each with its own precise BoxesOverlap + Solid==Trigger filter, so the set
        // of fired touches is identical to the flat scan. Rent a candidate list so a nested touch (a trigger that
        // moves another entity) doesn't clobber this one.
        System.Collections.Generic.List<Entity> cands =
            _touchListPool.Count > 0 ? _touchListPool.Pop() : new System.Collections.Generic.List<Entity>(32);
        try
        {
            EntitiesInBox(ent.AbsMin - Vector3.One, ent.AbsMax + Vector3.One, cands);
            TriggerTouch.Run(cands, ent);
        }
        finally
        {
            cands.Clear();
            _touchListPool.Push(cands);
        }
    }

    /// <summary>
    /// SV_NudgeOutOfSolid (sv_phys.c:1430 SV_NudgeOutOfSolid_PivotIsKnownGood): grow a known-good box
    /// outward from the entity's center pivot along each axis, sliding the origin out of any solid we hit,
    /// until the full hull is clear. Returns true (and commits the nudged origin) on success.
    /// </summary>
    public bool TryNudgeOutOfSolid(Entity ent)
    {
        Vector3 stuckOrigin = ent.Origin;
        Vector3 stuckMins = ent.Mins, stuckMaxs = ent.Maxs;
        Vector3 pivot = (ent.Mins + ent.Maxs) * 0.5f;
        Vector3 goodMins = pivot, goodMaxs = pivot;

        for (int bump = 0; bump < 6; bump++)
        {
            int coord = 2 - (bump >> 1); // 2,2,1,1,0,0
            bool dir = (bump & 1) != 0;  // false=mins, true=maxs

            for (int subbump = 0; ; subbump++)
            {
                Vector3 testOrigin = stuckOrigin;
                float delta = dir
                    ? Comp(stuckMaxs, coord) - Comp(goodMaxs, coord)
                    : Comp(stuckMins, coord) - Comp(goodMins, coord);
                testOrigin = WithComp(testOrigin, coord, Comp(testOrigin, coord) + delta);

                TraceResult st = Trace.Trace(stuckOrigin, goodMins, goodMaxs, testOrigin, MoveFilter.NoMonsters, ent);
                if (st.StartSolid)
                    return false; // embedded in a brush model — can't fix that
                if (st.Fraction >= 1f)
                    break;        // this axis is clear
                if (subbump >= 10)
                    return false; // give up

                // move out along the hit plane a hair past contact
                Vector3 move = st.EndPos - testOrigin;
                float nudge = Vector3.Dot(st.PlaneNormal, move) + 0.03125f;
                stuckOrigin += st.PlaneNormal * nudge;
            }

            if (dir) goodMaxs = WithComp(goodMaxs, coord, Comp(stuckMaxs, coord));
            else     goodMins = WithComp(goodMins, coord, Comp(stuckMins, coord));
        }

        ent.Origin = stuckOrigin;
        return true;
    }

    // =============================================================================================
    // SV_PushMove + SV_Physics_Pusher (sv_phys.c:1593 / 1908) — MOVETYPE_PUSH movers + their riders.
    // =============================================================================================

    /// <summary>
    /// SV_Physics_Pusher (sv_phys.c:1908): advance a MOVETYPE_PUSH entity on its LOCAL time
    /// (<see cref="Entity.LTime"/>) up to its next think, moving it + its riders via <see cref="PushMove"/>,
    /// then fire the think when its ltime crosses nextthink. The think runs INLINE (DP executes it directly
    /// at this point, not via SV_RunThink) because a pusher's schedule is keyed to its local time, not
    /// global sv.time — so the SimulationLoop's <c>runThink</c> (which gates on global time) is bypassed for
    /// the actual think call.
    /// </summary>
    public void PhysicsPusher(Entity ent, Func<Entity, bool> runThink)
    {
        float oldLTime = ent.LTime;
        float thinkTime = ent.NextThink;
        float movetime;
        if (thinkTime < ent.LTime + FrameTime)
        {
            movetime = thinkTime - ent.LTime;
            if (movetime < 0f) movetime = 0f;
        }
        else
        {
            movetime = FrameTime;
        }

        if (movetime != 0f)
            PushMove(ent, movetime); // advances ent.LTime unless blocked

        if (thinkTime > oldLTime && thinkTime <= ent.LTime)
        {
            ent.NextThink = 0f;
            ent.Think?.Invoke(ent); // DP fires the pusher think inline here
        }
        _ = runThink; // kept on the signature for call-site symmetry with the other integrators
    }

    /// <summary>
    /// SV_PushMove (sv_phys.c:1593): move a pusher to its final position and carry every solid entity
    /// either standing on it or caught inside its swept volume. SOLID_NOT/SOLID_TRIGGER pushers just
    /// translate (no riders). If a rider can't be pushed clear, the whole move is reverted and the
    /// pusher's <c>.blocked</c> handler runs (door/plat crush).
    /// </summary>
    public void PushMove(Entity pusher, float movetime)
    {
        if (pusher.Velocity == Vector3.Zero && pusher.AVelocity == Vector3.Zero)
        {
            pusher.LTime += movetime;
            return;
        }

        // SOLID_NOT / SOLID_TRIGGER pushers: translate + rotate, no rider carrying.
        if (pusher.Solid == Solid.Not || pusher.Solid == Solid.Trigger)
        {
            pusher.Origin += pusher.Velocity * movetime;
            pusher.Angles += pusher.AVelocity * movetime;
            pusher.Angles = WrapAngles(pusher.Angles);
            pusher.LTime += movetime;
            LinkEdict(pusher);
            return;
        }

        Vector3 move1 = pusher.Velocity * movetime;
        Vector3 moveAngle = pusher.AVelocity * movetime;
        bool rotated = pusher.Angles.LengthSquared() + pusher.AVelocity.LengthSquared() > 0f;

        // swept bounds the pusher will occupy (its box union the move), to find candidate riders.
        Vector3 pusherAbsMin = pusher.AbsMin, pusherAbsMax = pusher.AbsMax;
        Vector3 sweptMin = Vector3.Min(pusherAbsMin, pusherAbsMin + move1) - Vector3.One;
        Vector3 sweptMax = Vector3.Max(pusherAbsMax, pusherAbsMax + move1) + Vector3.One;

        Vector3 pushOrig = pusher.Origin;
        Vector3 pushAng = pusher.Angles;
        float pushLTime = pusher.LTime;

        // move the pusher to its final position.
        pusher.Origin += move1;
        pusher.Angles += moveAngle;
        pusher.LTime += movetime;
        LinkEdict(pusher);

        Solid savesolid = pusher.Solid;

        // gather + move riders.
        var all = Entities();
        var moved = _movedScratch;
        moved.Clear();

        int count = all.Count;
        for (int i = 0; i < count; i++)
        {
            Entity check = all[i];
            if (check.IsFreed || check == pusher) continue;

            switch (check.MoveType)
            {
                case MoveType.None:
                case MoveType.Push:
                case MoveType.Follow:
                case MoveType.Noclip:
                case MoveType.FlyWorldOnly:
                    continue;
            }

            // don't push the pusher's owner or things the pusher owns.
            if (check.Owner == pusher || pusher.Owner == check) continue;

            // a rider standing on the pusher always moves; otherwise only if it sits inside the final box.
            bool standingOnPusher = (check.Flags & EntFlags.OnGround) != 0 && check.GroundEntity == pusher;
            if (!standingOnPusher)
            {
                // not in the swept volume → not affected.
                if (!CollisionWorld.BoxesOverlap(check.AbsMin, check.AbsMax, sweptMin, sweptMax))
                    continue;
                // and not actually overlapping the pusher's final box → skip.
                if (!CollisionWorld.BoxesOverlap(check.AbsMin, check.AbsMax, pusher.AbsMin, pusher.AbsMax))
                    continue;
            }

            // compute this rider's move (rotation carries it around the pusher's pivot).
            Vector3 move;
            if (rotated)
            {
                // rotate the rider's offset by the pusher's angular delta about the pusher origin.
                Vector3 pivot = (check.Mins + check.Maxs) * 0.5f;
                Vector3 org = (check.Origin - pusher.Origin) + pivot;
                XonoticGodot.Common.Math.QMath.AngleVectors(NegateAngles(moveAngle), out Vector3 fwd, out Vector3 right, out Vector3 up);
                // AngleVectorsFLU: forward/left/up — DP uses left = -right.
                Vector3 left = -right;
                Vector3 org2 = new(Vector3.Dot(org, fwd), Vector3.Dot(org, left), Vector3.Dot(org, up));
                move = (org2 - org) + move1;
            }
            else
            {
                move = move1;
            }

            Vector3 movedFrom = check.Origin;
            Vector3 movedFromAngles = check.Angles;
            moved.Add((check, movedFrom, movedFromAngles));

            // MOVETYPE_PHYSICS riders run their own (better) collision solver; the pusher just translates them
            // and relinks, skipping the PushEntity sweep + stuck-check entirely (push.qc:123-129).
            if (check.MoveType == MoveType.Physics)
            {
                check.Origin += move;
                LinkEdict(check);
                continue;
            }

            // push the rider with the pusher temporarily non-solid (so the rider's own trace ignores it).
            pusher.Solid = Solid.Not;
            bool teleported = !PushEntity(out TraceResult trace, check, move, doTouch: true);
            check.Angles = new Vector3(check.Angles.X, check.Angles.Y + trace.Fraction * moveAngle.Y, check.Angles.Z);
            pusher.Solid = savesolid;

            if (teleported)
                continue; // a touch teleported the rider; it's clear of the pusher now

            // riders that lost contact (fell off the back/edge) drop their onground flag.
            if (check.MoveType != MoveType.Walk && (trace.Fraction < 1f || check.GroundEntity != pusher))
                check.Flags &= ~EntFlags.OnGround;

            // if the rider is STILL inside the pusher, the pusher is blocked: revert everything.
            TraceResult re = Trace.Trace(check.Origin, check.Mins, check.Maxs, check.Origin, MoveFilter.NoMonsters, check);
            if (re.StartSolid)
            {
                // try to nudge the rider out of the pusher first. On success, fire a zero-length PushEntity
                // (push.qc:157-158 "hack to invoke all necessary movement triggers") so the relinked rider
                // runs its touch dual-dispatch, then continue.
                if (TryNudgeOutOfSolid(check))
                {
                    PushEntity(out _, check, Vector3.Zero, doTouch: true);
                    continue;
                }

                // still inside the pusher and couldn't be freed.
                // zero-thickness riders just get squashed (skipped) rather than blocking (push.qc:166-167).
                if (check.Mins.X == check.Maxs.X)
                    continue;
                // corpses (SOLID_NOT/SOLID_TRIGGER) get their box flattened to zero and are skipped, not
                // blocked (push.qc:168-173): mins.x = mins.y = 0; maxs = mins.
                if (check.Solid == Solid.Not || check.Solid == Solid.Trigger)
                {
                    check.Mins = new Vector3(0f, 0f, check.Mins.Z);
                    check.Maxs = check.Mins;
                    continue;
                }

                // really blocked: put the pusher back, move every already-moved rider back.
                pusher.Origin = pushOrig;
                pusher.Angles = pushAng;
                pusher.LTime = pushLTime;
                LinkEdict(pusher);

                for (int m = 0; m < moved.Count; m++)
                {
                    var (ed, from, fromAng) = moved[m];
                    ed.Origin = from;
                    ed.Angles = fromAng;
                    LinkEdict(ed);
                }

                // run the pusher's blocked handler (door reverse / plat crush damage).
                pusher.Blocked?.Invoke(pusher, check);
                return;
            }
        }

        pusher.Angles = WrapAngles(pusher.Angles);
    }

    /// <summary>
    /// SV_Physics_Follow (sv_phys.c:2361): rigidly attach a MOVETYPE_FOLLOW entity to its
    /// <see cref="Entity.Aiment"/> using the stored offset (<see cref="Entity.ViewOfs"/>) rotated by the
    /// aiment's orientation relative to the attachment snapshot held in <see cref="Entity.PunchAngle"/>.
    /// </summary>
    public void PhysicsFollow(Entity ent)
    {
        Entity? e = ent.Aiment;
        if (e == null || e.IsFreed)
            return;

        if (e.Angles == ent.PunchAngle)
        {
            // no relative rotation since attach: just offset by view_ofs.
            ent.Origin = e.Origin + ent.ViewOfs;
        }
        else
        {
            // rotate the offset from the attach frame into the aiment's current frame.
            Vector3 attachAng = new(-ent.PunchAngle.X, ent.PunchAngle.Y, ent.PunchAngle.Z);
            XonoticGodot.Common.Math.QMath.AngleVectors(attachAng, out Vector3 vf, out Vector3 vr, out Vector3 vu);
            Vector3 vo = ent.ViewOfs;
            Vector3 v = new(
                vo.X * vf.X + vo.Y * vr.X + vo.Z * vu.X,
                vo.X * vf.Y + vo.Y * vr.Y + vo.Z * vu.Y,
                vo.X * vf.Z + vo.Y * vr.Z + vo.Z * vu.Z);

            Vector3 curAng = new(-e.Angles.X, e.Angles.Y, e.Angles.Z);
            XonoticGodot.Common.Math.QMath.AngleVectors(curAng, out vf, out vr, out vu);
            ent.Origin = new Vector3(
                v.X * vf.X + v.Y * vf.Y + v.Z * vf.Z + e.Origin.X,
                v.X * vr.X + v.Y * vr.Y + v.Z * vr.Z + e.Origin.Y,
                v.X * vu.X + v.Y * vu.Y + v.Z * vu.Z + e.Origin.Z);
        }
        ent.Angles = e.Angles + ent.Angles; // DP adds v_angle; we fold it into the entity angles
        LinkEdict(ent);
    }

    // --- scratch for PushMove riders (per-instance; PushMove is not re-entrant within one context) ---
    private readonly List<(Entity ent, Vector3 from, Vector3 fromAng)> _movedScratch = new();

    private static float Comp(Vector3 v, int i) => i == 0 ? v.X : (i == 1 ? v.Y : v.Z);
    private static Vector3 WithComp(Vector3 v, int i, float val)
        => i == 0 ? new Vector3(val, v.Y, v.Z) : (i == 1 ? new Vector3(v.X, val, v.Z) : new Vector3(v.X, v.Y, val));

    private static Vector3 NegateAngles(Vector3 a) => new(-a.X, -a.Y, -a.Z);

    private static Vector3 WrapAngles(Vector3 a) => new(
        a.X - 360f * MathF.Floor(a.X * (1f / 360f)),
        a.Y - 360f * MathF.Floor(a.Y * (1f / 360f)),
        a.Z - 360f * MathF.Floor(a.Z * (1f / 360f)));
}

/// <summary>
/// SV_FlyMove (Base/darkplaces/sv_phys.c:1150): the core slide-and-step solver. Moves the entity
/// through up to <see cref="MaxClipPlanes"/> collision planes per tick, sliding along each via
/// <see cref="Clip.ClipVelocity"/>, clearing into the crease where two planes meet, detecting ground
/// when a hit plane's normal.Z &gt; 0.7 (sets FL_ONGROUND + GroundEntity). Returns blocked flags:
/// 1 = floor, 2 = wall/step, 4 = dead stop, 8 = teleported by a touch function.
/// </summary>
public static class FlyMove
{
    public const int MaxClipPlanes = 5;
    public const float FloorNormalZ = 0.7f;

    /// <summary>
    /// Run a slide move for <paramref name="time"/> seconds.
    /// <paramref name="applyGravity"/> applies the gravity (half-)step as DP does.
    /// <paramref name="stepHeight"/> &gt; 0 enables in-loop stair stepping (sv_gameplayfix_stepmultipletimes);
    /// pass 0 for the plain slide (players add the explicit up/forward/down step in WalkMove).
    /// <paramref name="stepNormal"/> receives the wall plane normal on a step (blocked&amp;2), for wall friction.
    /// </summary>
    public static int Run(PhysicsContext ctx, Entity ent, float time, bool applyGravity,
        out Vector3 stepNormal, float stepHeight = 0f)
    {
        stepNormal = Vector3.Zero;
        if (time <= 0f) return 0;

        float gravity = 0f;
        Vector3 restoreVelocity = ent.Velocity;

        // gravity: first half-step (or full step) before the move (sv_phys.c:1167)
        if (applyGravity)
        {
            gravity = ctx.EntGravity(ent);
            if ((ent.Flags & EntFlags.OnGround) == 0)
            {
                if (ctx.GravityUnaffectedByTicrate)
                    ent.Velocity.Z -= gravity * 0.5f;
                else
                    ent.Velocity.Z -= gravity;
            }
        }

        int blocked = 0;
        Vector3 originalVelocity = ent.Velocity;
        Vector3 primalVelocity = ent.Velocity;

        Span<Vector3> planes = stackalloc Vector3[MaxClipPlanes];
        int numplanes = 0;
        float timeLeft = time;

        for (int bumpcount = 0; bumpcount < MaxClipPlanes; bumpcount++)
        {
            if (ent.Velocity == Vector3.Zero)
                break;

            Vector3 push = ent.Velocity * timeLeft;
            if (!ctx.PushEntity(out TraceResult trace, ent, push, doTouch: true))
            {
                blocked |= 8; // teleported by touch
                break;
            }

            // stuck in the world and couldn't escape → bail, restore velocity (sv_phys.c:1201)
            if (trace.StartSolid && trace.AllSolid)
            {
                ent.Velocity = restoreVelocity;
                return 3;
            }

            if (trace.Fraction == 1f)
                break; // moved the whole way

            timeLeft *= 1f - trace.Fraction;

            if (trace.PlaneNormal.Z != 0f)
            {
                if (trace.PlaneNormal.Z > FloorNormalZ)
                {
                    // floor
                    blocked |= 1;
                    ent.Flags |= EntFlags.OnGround;
                    ent.GroundEntity = trace.Ent; // null == world
                }
            }
            else if (stepHeight > 0f)
            {
                // in-loop stair step: up stepHeight, forward (remaining push), back down (sv_phys.c:1229)
                Vector3 org = ent.Origin;
                Vector3 fwdPush = ent.Velocity * timeLeft;

                if (!ctx.PushEntity(out _, ent, new Vector3(0, 0, stepHeight), doTouch: false)) { blocked |= 8; break; }
                if (!ctx.PushEntity(out TraceResult fwdTrace, ent, fwdPush, doTouch: false)) { blocked |= 8; break; }
                if (!ctx.PushEntity(out _, ent, new Vector3(0, 0, org.Z - ent.Origin.Z), doTouch: false)) { blocked |= 8; break; }

                // accept if we made horizontal progress
                if (ent.Origin.X != org.X || ent.Origin.Y != org.Y)
                {
                    trace = fwdTrace;
                    timeLeft *= 1f - trace.Fraction;
                    numplanes = 0;
                    continue;
                }
                // no progress: revert to pre-step origin and treat as a wall below
                ent.Origin = org;
                blocked |= 2;
                stepNormal = trace.PlaneNormal;
            }
            else
            {
                // wall / step — report to caller (players do explicit stepping in WalkMove)
                blocked |= 2;
                stepNormal = trace.PlaneNormal;
            }

            // (DP fires SV_Impact here when impactbeforeonground is off; our PushEntity already
            //  dual-dispatched touch on the blocking trace, so we don't repeat it.)

            if (trace.Fraction >= 0.001f)
            {
                // made progress: reset the plane accumulation
                originalVelocity = ent.Velocity;
                numplanes = 0;
            }

            if (numplanes >= MaxClipPlanes)
            {
                // shouldn't happen — bail dead (sv_phys.c:1308)
                ent.Velocity = Vector3.Zero;
                blocked = 3;
                break;
            }

            planes[numplanes++] = trace.PlaneNormal;

            // find a velocity that slides along all accumulated planes (sv_phys.c:1331)
            int i;
            Vector3 newVelocity = Vector3.Zero;
            for (i = 0; i < numplanes; i++)
            {
                newVelocity = Clip.ClipVelocity(originalVelocity, planes[i], 1f);
                int j;
                for (j = 0; j < numplanes; j++)
                {
                    if (j != i && Vector3.Dot(newVelocity, planes[j]) < 0f)
                        break; // still moving into plane j
                }
                if (j == numplanes)
                    break; // found a plane we can slide along cleanly
            }

            if (i != numplanes)
            {
                // slide along the chosen plane
                ent.Velocity = newVelocity;
            }
            else
            {
                // wedged between planes — slide along the crease (intersection of two planes)
                if (numplanes != 2)
                {
                    ent.Velocity = Vector3.Zero;
                    blocked = 7;
                    break;
                }
                Vector3 dir = Vector3.Cross(planes[0], planes[1]);
                dir = NormalizeSafe(dir);
                float d = Vector3.Dot(dir, ent.Velocity);
                ent.Velocity = dir * d;
            }

            // if velocity now opposes the original move, stop dead (avoids oscillation in corners)
            if (Vector3.Dot(ent.Velocity, primalVelocity) <= 0f)
            {
                ent.Velocity = Vector3.Zero;
                break;
            }
        }

        // gravity: second half-step after the move when using the ticrate-independent path (sv_phys.c:1392)
        if (applyGravity && ctx.GravityUnaffectedByTicrate)
        {
            if ((ent.Flags & EntFlags.OnGround) == 0)
                ent.Velocity.Z -= gravity * 0.5f;
        }

        return blocked;
    }

    private static Vector3 NormalizeSafe(Vector3 v)
    {
        float len = v.Length();
        return len > 0f ? v / len : Vector3.Zero;
    }
}

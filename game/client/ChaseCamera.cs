using Godot;
using VortexArena.Common.Framework;   // MoveFilter
using VortexArena.Common.Math;        // QMath, Coords
using VortexArena.Common.Services;    // Api, TraceResult, PointContents
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// The reusable chase / event-chase pull-back trace math, extracted VERBATIM from
/// <see cref="FirstPersonView"/> so Phase 2 (third-person wiring, crosshair-chase) can call the SAME
/// camera-placement primitive that the live first-person view path uses.
///
/// <para>Two primitives:</para>
/// <list type="bullet">
/// <item><see cref="ApplyClassic"/> — the classic user third-person cam (QC cl_player.qc
/// <c>CSQCPlayer_ApplyChase</c>: chase_back/up/front/overhead/pitchangle).</item>
/// <item><see cref="ApplyEvent"/> — the death / event-chase pull-back cam (QC view.qc
/// <c>View_EventChase</c>): pivot lifted by cl_eventchase_viewoffset, ceiling-clamped, box-traced pull-back.</item>
/// </list>
///
/// <para>Both are pure client-side presentation: they trace against the live <see cref="Api.Trace"/>
/// (MOVE_WORLDONLY) and read live cvars via <see cref="FirstPersonView.Cvar"/>. No networked field, stat,
/// struct or entity-feed slice is added or read. Behavior is byte-identical to the original
/// <c>FirstPersonView.ApplyClassicChase</c> / <c>ApplyEventChase</c> — this is the LIVE cl-view chase path.</para>
/// </summary>
public static class ChaseCamera
{
    /// <summary>
    /// The smoothed event-chase state the host carries across frames (QC <c>eventchase_current_distance</c> +
    /// <c>eventchase_running</c>). <see cref="Distance"/> is the current (eased) pull-back distance behind the
    /// pivot; <see cref="Running"/> latches the chase on once death starts so the cl_eventchase_death==2 settle
    /// gate stays engaged until respawn.
    /// </summary>
    public struct EventState
    {
        /// <summary>Current (smoothed) pull-back distance behind the pivot (QC eventchase_current_distance).</summary>
        public float Distance;

        /// <summary>Latches the chase on once death starts (QC eventchase_running).</summary>
        public bool Running;
    }

    /// <summary>
    /// The classic user third-person camera — port of <c>CSQCPlayer_ApplyChase</c> (cl_player.qc:453). Pulls the
    /// camera back along the view by <c>chase_back</c> and up by <c>chase_up</c> (the default branch), or — when
    /// <c>chase_overhead</c> is set — flattens the pitch and drops an overhead cam sampling the lowest of a 5×5
    /// trace grid. <c>chase_front</c> flips it to a frontal selfie view (only while <paramref name="spectating"/>).
    /// Traces the world so the camera stops short of geometry. Mutates <paramref name="viewAnglesQuake"/> for the
    /// overhead/front variants so the caller's basis matches. Operates on the eye position
    /// <paramref name="eyeQuake"/> (= QC vieworg post-smoothing).
    /// </summary>
    public static NVec3 ApplyClassic(NVec3 eyeQuake, ref NVec3 viewAnglesQuake, bool spectating)
    {
        NVec3 v = eyeQuake;
        // DarkPlaces engine defaults: chase_back 48, chase_up 24, chase_front 0, chase_overhead 0, chase_pitchangle 0.
        float chaseBack = FirstPersonView.Cvar("chase_back", 48f);
        float chaseUp = FirstPersonView.Cvar("chase_up", 24f);
        bool chaseFront = FirstPersonView.Cvar("chase_front", 0f) != 0f;
        bool chaseOverhead = FirstPersonView.Cvar("chase_overhead", 0f) != 0f;
        // Spectating-only test (QC CSQCPlayer_ApplyChase: `if (autocvar_chase_front && spectatee_status)`): chase_front
        // is honored ONLY while following a player. For one's own chase cam spectating is false, so chase_front does
        // nothing — matching the QC guard. While spectating with chase_active set it engages the frontal selfie view.

        if (chaseOverhead)
        {
            // QC: flatten pitch, sample a 5×5 grid of overhead trace destinations and keep the LOWEST ceiling hit.
            viewAnglesQuake.X = 0f;
            QMath.AngleVectors(viewAnglesQuake, out NVec3 forward, out _, out NVec3 up);

            NVec3 BackUp(NVec3 ofs) => new(
                v.X - forward.X * chaseBack + up.X * chaseUp + ofs.X,
                v.Y - forward.Y * chaseBack + up.Y * chaseUp + ofs.Y,
                v.Z - forward.Z * chaseBack + up.Z * chaseUp + ofs.Z);

            NVec3 best = TraceEnd(v, BackUp(NVec3.Zero));
            for (float ox = -16f; ox <= 16f; ox += 8f)
                for (float oy = -16f; oy <= 16f; oy += 8f)
                {
                    NVec3 end = TraceEnd(v, BackUp(new NVec3(ox, oy, 0f)));
                    if (best.Z > end.Z) best.Z = end.Z;
                }
            best.Z -= 8f;
            viewAnglesQuake.X = FirstPersonView.Cvar("chase_pitchangle", 0f);
            return best;
        }

        // Default branch: pull back along forward (negated, flipped for chase_front selfie) + lift by chase_up.
        QMath.AngleVectors(viewAnglesQuake, out NVec3 fwd, out _, out _);
        if (chaseFront && spectating)
            fwd = -QMath.Normalize(fwd);

        float cdist = -chaseBack - 8f; // QC trace "a little further" so it hits a surface consistently
        NVec3 chaseDest = new(
            v.X + fwd.X * cdist,
            v.Y + fwd.Y * cdist,
            v.Z + fwd.Z * cdist + chaseUp);

        // QC traceline(v, chase_dest, MOVE_NOMONSTERS, NULL); then back off 8 along forward + 4 along the plane normal.
        NVec3 endPos = chaseDest;
        NVec3 planeNormal = NVec3.Zero;
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(v, NVec3.Zero, NVec3.Zero, chaseDest, MoveFilter.WorldOnly, null);
            endPos = tr.EndPos;
            if (tr.Fraction < 1f) planeNormal = tr.PlaneNormal;
        }
        NVec3 result = new(
            endPos.X + 8f * fwd.X + 4f * planeNormal.X,
            endPos.Y + 8f * fwd.Y + 4f * planeNormal.Y,
            endPos.Z + 8f * fwd.Z + 4f * planeNormal.Z);

        if (chaseFront && spectating)
        {
            // QC: flip the view so the player looks at themselves — inverse pitch, yaw toward the (flipped) forward.
            NVec3 newAng = QMath.VecToAngles(fwd);
            viewAnglesQuake.X = -viewAnglesQuake.X;
            viewAnglesQuake.Y = newAng.Y;
        }
        return result;
    }

    /// <summary>
    /// The death / event-chase camera — port of view.qc <c>View_EventChase</c>. Pull the camera back along
    /// <c>-forward</c> from a pivot at the RAW player origin lifted by <c>cl_eventchase_viewoffset</c> (NOT the
    /// eye — view.qc:807,823-828), growing the distance smoothly (QC <c>eventchase_current_distance</c>, carried in
    /// <paramref name="state"/>) and box-tracing against the world so the camera stops short of geometry rather than
    /// clipping through it.
    /// </summary>
    public static NVec3 ApplyEvent(NVec3 originQuake, NVec3 forwardQuake, float dt, ref EventState state)
    {
        state.Running = true;

        // QC cl_eventchase_mins/maxs "-12 -12 -8"/"12 12 8" (xonotic-client.cfg:217-218). Needed both for the
        // viewoffset ceiling clamp (uses maxs.z) and the pull-back box-trace.
        NVec3 mins = new(-12f, -12f, -8f), maxs = new(12f, 12f, 8f);

        // QC view.qc:807,812,823-828: the pull-back PIVOT is the RAW player origin (csqcplayer.origin /
        // pmove_org), lifted by cl_eventchase_viewoffset "0 0 20" (xonotic-client.cfg:219) — NOT the eye.
        // The lift is ceiling-aware: trace world-up by view_offset + maxs.z; if clear take the full offset,
        // else clamp the rise so the camera box (height maxs.z) stays below the blocking surface.
        NVec3 pivot = originQuake;
        NVec3 viewOffset = new(0f, 0f, 20f);
        if (viewOffset != NVec3.Zero && Api.Services is not null)
        {
            NVec3 ceilTo = pivot + viewOffset + new NVec3(0f, 0f, maxs.Z);
            TraceResult ct = Api.Trace.Trace(pivot, NVec3.Zero, NVec3.Zero, ceilTo, MoveFilter.WorldOnly, null);
            if (ct.Fraction == 1f)
                pivot += viewOffset;
            else
                pivot.Z += Mathf.Max(0f, (ct.EndPos.Z - pivot.Z) - maxs.Z);
        }
        else if (viewOffset != NVec3.Zero)
        {
            pivot += viewOffset;
        }

        float chaseDistance = FirstPersonView.Cvar("cl_eventchase_distance", 140f);
        float chaseSpeed = FirstPersonView.Cvar("cl_eventchase_speed", 1.3f);

        // ease the distance out (slow down the further back we get) — QC eventchase_current_distance integration.
        // A frametime-scaled exponential approach, as in QC.
        float frametime = dt > 0f ? dt : 0.0166667f;
        if (chaseSpeed != 0f && state.Distance < chaseDistance)
            state.Distance += chaseSpeed * (chaseDistance - state.Distance) * frametime;
        else if (!Mathf.IsEqualApprox(state.Distance, chaseDistance))
            state.Distance = chaseDistance;

        NVec3 target = pivot - forwardQuake * state.Distance;

        // Box-trace from the pivot to the target against the world only (QC WarpZone_TraceBox MOVE_WORLDONLY): a
        // small box so the camera keeps a little clearance from walls (QC cl_eventchase_mins/maxs).
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(pivot, mins, maxs, target, MoveFilter.WorldOnly, null);
            if (tr.StartSolid)
            {
                // Camera box started in solid (pivot against a wall): fall back to a line trace (QC behaviour) and
                // stop just short, lifted off the surface by the box extent.
                TraceResult lt = Api.Trace.Trace(pivot, NVec3.Zero, NVec3.Zero, target, MoveFilter.WorldOnly, null);
                return lt.EndPos - forwardQuake * mins.Z;
            }
            return tr.EndPos;
        }
        return target;
    }

    /// <summary>
    /// The vehicle pull-back camera — the realisation of <c>SVC_SETVIEWPORT(vehicle_viewport)</c> +
    /// <c>cl_eventchase_vehicle</c> (Xonotic's seated-vehicle third-person cam; xonotic-client.cfg:221-223).
    /// Structurally identical to <see cref="ApplyEvent"/> — pull the camera back along <c>-forward</c> from a
    /// ceiling-clamped, box-traced pivot, easing the distance out — but the pivot is the RAW vehicle/seated origin
    /// (<paramref name="pivotOriginQuake"/>, NOT lifted to the eye) and it reads the VEHICLE cvars:
    /// <list type="bullet">
    /// <item><c>cl_eventchase_vehicle_viewoffset</c> "0 0 80" (xonotic-client.cfg:222) — the ceiling-clamped pivot
    /// lift, same trace logic as <see cref="ApplyEvent"/>'s "0 0 20".</item>
    /// <item><c>cl_eventchase_vehicle_distance</c> 250 (:223) — the pull-back distance.</item>
    /// <item><c>cl_eventchase_speed</c> 1.3 — the shared ease.</item>
    /// </list>
    /// Same box-trace mins/maxs "-12 -12 -8"/"12 12 8" MOVE_WORLDONLY and the same StartSolid line-trace fallback.
    /// </summary>
    public static NVec3 ApplyVehicle(NVec3 pivotOriginQuake, NVec3 forwardQuake, float dt, ref EventState state)
    {
        state.Running = true;

        // QC cl_eventchase_mins/maxs "-12 -12 -8"/"12 12 8" (xonotic-client.cfg:217-218). Needed both for the
        // viewoffset ceiling clamp (uses maxs.z) and the pull-back box-trace.
        NVec3 mins = new(-12f, -12f, -8f), maxs = new(12f, 12f, 8f);

        // The pull-back PIVOT is the RAW vehicle/seated origin (NOT lifted to the eye), lifted by
        // cl_eventchase_vehicle_viewoffset "0 0 80" (xonotic-client.cfg:222). The lift is ceiling-aware: trace
        // world-up by view_offset + maxs.z; if clear take the full offset, else clamp the rise so the camera box
        // (height maxs.z) stays below the blocking surface — identical logic to ApplyEvent's "0 0 20".
        NVec3 pivot = pivotOriginQuake;
        NVec3 viewOffset = new(0f, 0f, 80f);
        if (viewOffset != NVec3.Zero && Api.Services is not null)
        {
            NVec3 ceilTo = pivot + viewOffset + new NVec3(0f, 0f, maxs.Z);
            TraceResult ct = Api.Trace.Trace(pivot, NVec3.Zero, NVec3.Zero, ceilTo, MoveFilter.WorldOnly, null);
            if (ct.Fraction == 1f)
                pivot += viewOffset;
            else
                pivot.Z += Mathf.Max(0f, (ct.EndPos.Z - pivot.Z) - maxs.Z);
        }
        else if (viewOffset != NVec3.Zero)
        {
            pivot += viewOffset;
        }

        float chaseDistance = FirstPersonView.Cvar("cl_eventchase_vehicle_distance", 250f);
        float chaseSpeed = FirstPersonView.Cvar("cl_eventchase_speed", 1.3f);

        // ease the distance out (slow down the further back we get) — QC eventchase_current_distance integration.
        // A frametime-scaled exponential approach, as in QC.
        float frametime = dt > 0f ? dt : 0.0166667f;
        if (chaseSpeed != 0f && state.Distance < chaseDistance)
            state.Distance += chaseSpeed * (chaseDistance - state.Distance) * frametime;
        else if (!Mathf.IsEqualApprox(state.Distance, chaseDistance))
            state.Distance = chaseDistance;

        NVec3 target = pivot - forwardQuake * state.Distance;

        // Box-trace from the pivot to the target against the world only (QC WarpZone_TraceBox MOVE_WORLDONLY): a
        // small box so the camera keeps a little clearance from walls (QC cl_eventchase_mins/maxs).
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(pivot, mins, maxs, target, MoveFilter.WorldOnly, null);
            if (tr.StartSolid)
            {
                // Camera box started in solid (pivot against a wall): fall back to a line trace (QC behaviour) and
                // stop just short, lifted off the surface by the box extent.
                TraceResult lt = Api.Trace.Trace(pivot, NVec3.Zero, NVec3.Zero, target, MoveFilter.WorldOnly, null);
                return lt.EndPos - forwardQuake * mins.Z;
            }
            return tr.EndPos;
        }
        return target;
    }

    /// <summary>QC <c>traceline(start, end, MOVE_NOMONSTERS, NULL)</c> → trace_endpos; world-only line trace.</summary>
    private static NVec3 TraceEnd(NVec3 start, NVec3 end)
    {
        if (Api.Services is null) return end;
        return Api.Trace.Trace(start, NVec3.Zero, NVec3.Zero, end, MoveFilter.WorldOnly, null).EndPos;
    }
}

// Port of Base/data/xonotic-data.pk3dir/qcsrc/lib/warpzone/common.qc
//   WarpZone_FindRadius / WarpZone_FindRadius_Recurse / WarpZoneLib_NearestPointOnBox / WarpZoneLib_BadEntity
// plus the WarpZone_Accumulator transform chain (common.qc WarpZone_Accumulator_*) it threads through.
//
// SCOPE (T45, combat-traversal half): the radius-damage half of warpzone combat traversal — a blast at one
// portal mouth reaches victims through a chain of seamless portals, with each victim tagged by the transform
// that maps its world position back into the blast's coordinate frame so falloff/knockback are measured in the
// blast frame exactly as QC's WarpZone_FindRadius does. The CLIENT portal SubViewport render is OUT OF SCOPE
// (see TraceServiceWarpzoneExt header).
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

// NOTE: deliberately placed in the existing warpzone namespace (XonoticGodot.Common.Gameplay), NOT a nested
// "...Gameplay.Warpzone" namespace, because the TYPE `Warpzone` already lives there — a `...Gameplay.Warpzone`
// namespace would collide with the class name. The file still lives under the Gameplay/Warpzone/ folder per the
// task scoping; folder layout and namespace are decoupled here on purpose.
namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// An accumulated chain of warpzone transforms — the C# successor to QC's <c>WarpZone_Accumulator</c>
/// (lib/warpzone/common.qc <c>warpzone_transform</c>/<c>warpzone_shift</c> pair, composed via
/// <c>AnglesTransform_Multiply</c> / <c>AnglesTransform_Multiply_GetPostShift</c>). The port's
/// <see cref="WarpzoneTransform"/> uses an explicit forward/right/up basis rather than QC's AnglesTransform
/// vector encoding, so the accumulator is the equivalent affine map <c>p → R·p + shift</c> built by appending
/// each crossed zone's transform. Appending zone <c>wz</c> to the running chain <c>C</c> yields the map
/// "first apply C, then apply wz" (matching QC, where the LATEST-crossed zone is the OUTER multiply).
/// </summary>
public readonly struct WarpzoneTransformChain
{
    // The affine map: world point in the SOURCE frame → point in the accumulated (blast / trace-start) frame
    // is the INVERSE of this; this map carries a SOURCE-frame point forward through the chain. We store the
    // forward map (start→end of the chain) as rows of a 3x3 rotation plus a translation, mirroring how QC's
    // warpzone_transform/_shift carry org through the chain (WarpZone_FindRadius_Recurse: org_new = Transform(org)).
    private readonly Vector3 _r0, _r1, _r2; // rotation rows (R)
    private readonly Vector3 _shift;        // translation
    public readonly bool HasTransform;      // false == identity (no zone crossed)

    private WarpzoneTransformChain(Vector3 r0, Vector3 r1, Vector3 r2, Vector3 shift, bool has)
    {
        _r0 = r0; _r1 = r1; _r2 = r2; _shift = shift; HasTransform = has;
    }

    /// <summary>The identity chain (no warpzone crossed) — QC <c>WarpZone_Accumulator_Clear</c>.</summary>
    public static WarpzoneTransformChain Identity =>
        new(new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), Vector3.Zero, false);

    /// <summary>Apply the accumulated affine map to a point: <c>R·p + shift</c>.</summary>
    public Vector3 TransformPoint(Vector3 p) => new(
        Vector3.Dot(_r0, p) + _shift.X,
        Vector3.Dot(_r1, p) + _shift.Y,
        Vector3.Dot(_r2, p) + _shift.Z) ;

    /// <summary>Apply only the rotation part to a direction/velocity: <c>R·v</c>.</summary>
    public Vector3 TransformDirection(Vector3 v) => new(
        Vector3.Dot(_r0, v),
        Vector3.Dot(_r1, v),
        Vector3.Dot(_r2, v));

    /// <summary>
    /// QC <c>WarpZone_Accumulator_Add</c>: compose the running chain with one more crossed zone. The new map is
    /// "apply this chain, then apply <paramref name="wz"/>" (the freshly-crossed zone is the outer transform),
    /// matching <c>WarpZone_FindRadius_Recurse</c>'s <c>transform_new = AnglesTransform_Multiply(wz.transform, transform)</c>.
    /// </summary>
    public WarpzoneTransformChain Append(in WarpzoneTransform wz)
    {
        // wz maps q → outOrigin + Rwz·(q − inOrigin) = Rwz·q + (outOrigin − Rwz·inOrigin).
        // Build wz's rotation rows + shift from its basis, then compose: result = wz ∘ this.
        wz.GetAffine(out Vector3 wr0, out Vector3 wr1, out Vector3 wr2, out Vector3 wshift);

        // result rotation rows = Wrows · (this rows as columns). Compose row-wise: each output row is a linear
        // combination of THIS chain's rows weighted by the W row's components (R = Rwz · Rthis).
        Vector3 nr0 = wr0.X * _r0 + wr0.Y * _r1 + wr0.Z * _r2;
        Vector3 nr1 = wr1.X * _r0 + wr1.Y * _r1 + wr1.Z * _r2;
        Vector3 nr2 = wr2.X * _r0 + wr2.Y * _r1 + wr2.Z * _r2;

        // result shift = Rwz·thisShift + wshift.
        Vector3 rotThisShift = new(
            Vector3.Dot(wr0, _shift),
            Vector3.Dot(wr1, _shift),
            Vector3.Dot(wr2, _shift));
        Vector3 nshift = rotThisShift + wshift;

        return new WarpzoneTransformChain(nr0, nr1, nr2, nshift, true);
    }
}

/// <summary>The result of a warpzone-aware trace — the plain sweep PLUS the accumulated portal transform.</summary>
public readonly struct WarpzoneTraceResult
{
    /// <summary>The underlying sweep result, with <see cref="TraceResult.EndPos"/> in the FINAL portal frame
    /// (the frame the last segment ran in). For a trace that crossed no portal this equals a plain trace.</summary>
    public readonly TraceResult Trace;

    /// <summary>
    /// The accumulated transform from the trace's START frame to the FINAL frame: apply it to a direction/point
    /// expressed in the start frame to carry it through the same portals the trace crossed. Identity when no
    /// portal was crossed. The C# successor to QC's <c>WarpZone_trace_transform</c> accumulator (common.qc).
    /// </summary>
    public readonly WarpzoneTransformChain Transform;

    /// <summary>How many portals the trace crossed (0 for a plain trace; capped at the 16-zone guard).</summary>
    public readonly int ZonesCrossed;

    /// <summary>The FIRST crossing's entry point (in the trace's START frame) and exit point (in the frame past
    /// that first portal) — valid when <see cref="ZonesCrossed"/> &gt; 0. Lets a caller draw a beam/tracer as
    /// two correct segments (near side up to the portal, far side from the exit) instead of one wrong straight
    /// line whose ends live in different frames.</summary>
    public readonly Vector3 FirstCrossPoint, FirstExitPoint;

    public WarpzoneTraceResult(TraceResult trace, WarpzoneTransformChain transform, int zonesCrossed,
        Vector3 firstCrossPoint = default, Vector3 firstExitPoint = default)
    {
        Trace = trace;
        Transform = transform;
        ZonesCrossed = zonesCrossed;
        FirstCrossPoint = firstCrossPoint;
        FirstExitPoint = firstExitPoint;
    }
}

/// <summary>
/// Warpzone-aware trace recursion — the C# successor to <c>WarpZone_TraceBox</c>/<c>WarpZone_TraceLine</c>
/// (lib/warpzone/common.qc). A trace from <c>org</c> to <c>end</c> runs as a sequence of plain sweeps: when a
/// segment crosses a linked <c>trigger_warpzone</c> the remaining segment is transformed through the portal and
/// the sweep continues on the far side, accumulating the transform, up to the 16-zone chain guard (common.qc
/// <c>i = 16</c>). With no warpzones in the world (or no manager wired) every call collapses to exactly one
/// <see cref="ITraceService.Trace"/>, so non-warpzone maps are byte-for-byte unchanged.
///
/// The port's warpzones are POJO <see cref="Warpzone"/> objects (plane + trigger volume), so where QC does
/// <c>WarpZone_MakeAllSolid</c> + a tracebox that HITS the now-solid zone, the port detects the crossing
/// analytically (the segment crossing the zone's IN plane within its trigger bounds) — the same crossing the
/// teleport touch uses. This keeps the recursion deterministic and headless-testable.
/// </summary>
public static class WarpzoneTrace
{
    /// <summary>QC the chained-zone cap shared by every warpzone trace (common.qc <c>i = 16</c>).</summary>
    public const int MaxZoneDepth = 16;

    /// <summary>
    /// The world's warpzone manager (QC global <c>g_warpzones</c>) — published once per match by the host's
    /// <c>TraceService.SetWarpzoneManager</c> (after the map's zones link), cleared between maps. Null on a
    /// non-warpzone world / a test that never wired one, in which case every warpzone-aware trace is a plain trace.
    /// </summary>
    public static WarpzoneManager? AmbientManager;

    // =============================================================================================
    //  ITraceService extension surface — the call sites in WeaponFiring/WeaponSplash use these.
    // =============================================================================================

    /// <summary>
    /// QC <c>WarpZone_TraceLine(org, end, nomonsters, forent)</c>: a point trace from <paramref name="start"/>
    /// to <paramref name="end"/> that crosses seamless portals. Resolves the world's warpzone manager from
    /// <see cref="AmbientManager"/>; with none wired (a non-warpzone map / a test fake) it returns a plain trace
    /// with an identity transform.
    /// </summary>
    public static WarpzoneTraceResult TraceLineWarpzone(this ITraceService trace, Vector3 start, Vector3 end,
        MoveFilter filter, Entity? ignore)
        => TraceBoxWarpzone(trace, start, Vector3.Zero, Vector3.Zero, end, filter, ignore);

    /// <summary>
    /// QC <c>WarpZone_TraceBox(org, mi, ma, end, nomonsters, forent)</c>: a box sweep from <paramref name="start"/>
    /// to <paramref name="end"/> that crosses seamless portals, accumulating the portal transform. See
    /// <see cref="TraceWarpzone"/>; this overload resolves the ambient manager.
    /// </summary>
    public static WarpzoneTraceResult TraceBoxWarpzone(this ITraceService trace, Vector3 start, Vector3 mins,
        Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
        => TraceWarpzone(trace, AmbientManager, start, mins, maxs, end, filter, ignore);

    // =============================================================================================
    //  The recursion core (manager passed explicitly so it is unit-testable without the host wiring).
    // =============================================================================================

    /// <summary>
    /// Port of <c>WarpZone_TraceBox_ThroughZone</c> (common.qc): sweep <paramref name="start"/>→
    /// <paramref name="end"/> against <paramref name="trace"/>, and each time the swept segment crosses a linked
    /// portal in <paramref name="manager"/>, transform the remaining segment through the portal and continue —
    /// up to the 16-zone guard. Returns the final sweep plus the accumulated start→final transform. A null/empty
    /// manager (no warpzones) collapses to a single plain trace with an identity transform.
    /// </summary>
    public static WarpzoneTraceResult TraceWarpzone(ITraceService trace, WarpzoneManager? manager,
        Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
    {
        // Fast path: no warpzones → exactly one plain trace (QC: the !warpzone_warpzones_exist branch at the top
        // of WarpZone_TraceBox_ThroughZone).
        if (manager is null || manager.Zones.Count == 0)
            return new WarpzoneTraceResult(trace.Trace(start, mins, maxs, end, filter, ignore),
                WarpzoneTransformChain.Identity, 0);

        WarpzoneTransformChain chain = WarpzoneTransformChain.Identity;
        Vector3 segStart = start;
        Vector3 segEnd = end;
        Warpzone? lastZone = null;
        int crossed = 0;
        Vector3 firstCross = default, firstExit = default;

        // QC's `i = 16` loop: each iteration sweeps one segment; on a portal crossing it warps and re-sweeps.
        TraceResult tr = trace.Trace(segStart, mins, maxs, segEnd, filter, ignore);
        for (int i = 0; i < MaxZoneDepth; i++)
        {
            // Does the segment we just swept (segStart → its endpoint) cross a portal plane before it stops? We
            // only look up to the sweep's endpoint so a wall in front of the portal blocks the bullet (QC: the
            // tracebox stops at the wall; the warpzone is only "hit" if nothing solid is closer).
            Vector3 hitPoint = tr.EndPos;
            Warpzone? wz = FindCrossedZone(manager, segStart, hitPoint, lastZone, out Vector3 crossPoint);
            if (wz is null)
                break; // no portal on this segment → the plain sweep result stands

            // Cross the portal: transform the crossing point and the remaining end through it, accumulate the
            // transform, and re-sweep from just past the exit plane (QC: org = TransformOrigin(wz, trace_endpos);
            // end = TransformOrigin(wz, end); then a short step-back trace).
            chain = chain.Append(wz.Transform);
            crossed++;
            lastZone = wz;

            Vector3 exitPoint = wz.Transform.TransformOrigin(crossPoint);
            if (crossed == 1)
            {
                firstCross = crossPoint;   // start-frame entry point (for two-segment beam/tracer draws)
                firstExit = exitPoint;
            }
            segEnd = wz.Transform.TransformOrigin(segEnd);
            // Nudge just past the exit plane so the next sweep doesn't immediately re-detect the same crossing
            // (QC steps back a bit with a 32qu trace; the port nudges along the post-warp direction).
            Vector3 fwd = QNormalize(segEnd - exitPoint);
            segStart = exitPoint + fwd * 0.03125f;

            tr = trace.Trace(segStart, mins, maxs, segEnd, filter, ignore);
        }

        return new WarpzoneTraceResult(tr, chain, crossed, firstCross, firstExit);
    }

    /// <summary>
    /// Find the linked portal whose IN plane the segment <paramref name="from"/>→<paramref name="to"/> crosses
    /// (entering from the front, i.e. moving against the IN forward), within the portal's trigger bounds, nearest
    /// to <paramref name="from"/>. Skips <paramref name="ignoreZone"/> (the one we just exited) so a trace can't
    /// re-enter the zone it just left (QC: the <c>trace_ent == wz</c> guard + the step-back). Returns null when
    /// the segment crosses no portal; <paramref name="crossPoint"/> is the crossing point on the IN plane.
    /// </summary>
    private static Warpzone? FindCrossedZone(WarpzoneManager manager, Vector3 from, Vector3 to,
        Warpzone? ignoreZone, out Vector3 crossPoint)
    {
        crossPoint = to;
        Warpzone? best = null;
        float bestT = 2f;
        Vector3 seg = to - from;

        IReadOnlyList<Warpzone> zones = manager.Zones;
        for (int z = 0; z < zones.Count; z++)
        {
            Warpzone wz = zones[z];
            if (!wz.Linked) continue;
            if (ReferenceEquals(wz, ignoreZone)) continue;

            Vector3 n = wz.Transform.InForward;             // plane normal, faces the approaching side
            float denom = Vector3.Dot(n, seg);
            if (denom >= 0f) continue;                       // not moving INTO the plane (QC: must enter the front)

            float t = Vector3.Dot(n, wz.InOrigin - from) / denom;
            if (t < 0f || t > 1f) continue;                  // crossing is outside this segment
            if (t >= bestT) continue;                        // a nearer portal already wins

            // The crossing must land within the portal's trigger volume, else the ray merely passes the infinite
            // plane outside the actual portal opening (QC: the tracebox only hits the zone within its brush).
            Vector3 p = from + seg * t;
            if (!WithinTriggerBounds(wz, p)) continue;

            best = wz;
            bestT = t;
            crossPoint = p;
        }
        return best;
    }

    /// <summary>True if <paramref name="p"/> is inside the portal's trigger volume (its world AABB, padded 1qu to
    /// match QC's adjacent-trigger touch slack). Falls back to a generous slab around the plane center for a POJO
    /// zone with no trigger entity (headless tests).</summary>
    private static bool WithinTriggerBounds(Warpzone wz, Vector3 p)
    {
        if (wz.Trigger is { } t)
        {
            Vector3 lo = t.Origin + t.Mins - Vector3.One;
            Vector3 hi = t.Origin + t.Maxs + Vector3.One;
            return p.X >= lo.X && p.X <= hi.X
                && p.Y >= lo.Y && p.Y <= hi.Y
                && p.Z >= lo.Z && p.Z <= hi.Z;
        }
        // No trigger entity (pure transform test): accept any crossing near the plane center within a generous
        // 256qu box so the headless-test portals (constructed without a trigger) still drive the recursion.
        Vector3 d = p - wz.InOrigin;
        return MathF.Abs(d.X) <= 256f && MathF.Abs(d.Y) <= 256f && MathF.Abs(d.Z) <= 256f;
    }

    private static Vector3 QNormalize(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : Vector3.Zero;
    }
}

/// <summary>One entity reached by a warpzone-aware radius query, with the transform back to the blast frame.</summary>
public readonly struct WarpzoneRadiusHit
{
    /// <summary>The candidate entity (in its own world frame).</summary>
    public readonly Entity Entity;

    /// <summary>
    /// The transform that maps the entity's world position into the ORIGINAL blast coordinate frame — identity
    /// for an entity in the same room as the blast, or the accumulated portal chain for one reached through
    /// portals. QC stores this on the entity as <c>warpzone_transform</c>/<c>warpzone_shift</c> in
    /// <c>WarpZone_FindRadius_Recurse</c>; the port returns it explicitly so the caller stays stateless.
    /// </summary>
    public readonly WarpzoneTransformChain ToBlastFrame;

    /// <summary>The (possibly portal-shifted) blast origin used for THIS hit, already in the entity's frame.</summary>
    public readonly Vector3 LocalBlastOrigin;

    public WarpzoneRadiusHit(Entity entity, WarpzoneTransformChain toBlastFrame, Vector3 localBlastOrigin)
    {
        Entity = entity;
        ToBlastFrame = toBlastFrame;
        LocalBlastOrigin = localBlastOrigin;
    }
}

/// <summary>
/// Warpzone-aware radius query — the C# successor to <c>WarpZone_FindRadius</c> (lib/warpzone/common.qc).
/// A blast at <c>org</c> with radius <c>rad</c> finds every damageable entity within range INCLUDING those
/// reachable through a chain of seamless portals: at each portal whose mouth lies within the remaining radius
/// the search recurses through the portal with the radius reduced by the travelled distance, accumulating the
/// transform so a victim found on the far side is tagged with the map back to the blast frame.
///
/// The port's warpzones are POJO <see cref="Warpzone"/> objects (each carrying its plane and a trigger volume),
/// so where QC does <c>WarpZone_MakeAllSolid</c> + <c>FOREACH_ENTITY_RADIUS</c> + a per-zone <c>traceline</c>,
/// the port finds the relevant zones from the supplied <see cref="WarpzoneManager"/> and recurses analytically.
/// The 16-deep recursion guard mirrors QC's chained-zone cap (common.qc trace loop). With no warpzones in the
/// world this returns exactly the plain <see cref="IEntityService.FindInRadius"/> set with identity transforms,
/// so non-warpzone maps are byte-for-byte unchanged.
/// </summary>
public static class WarpzoneRadiusQuery
{
    /// <summary>QC the chained-zone cap shared by every warpzone trace/recursion (common.qc <c>i = 16</c>).</summary>
    private const int MaxZoneDepth = 16;

    /// <summary>QC <c>WarpZone_FindRadius_Recurse</c>'s <c>rad - 8</c> clamp — never recurse with the full radius.</summary>
    private const float RadiusRecurseMargin = 8f;

    /// <summary>
    /// Port of <c>WarpZone_FindRadius</c>: collect every entity within <paramref name="radius"/> of
    /// <paramref name="origin"/>, plus entities reachable through the <paramref name="manager"/>'s portals, each
    /// tagged with the transform mapping its world position back to the blast frame. Clears and fills
    /// <paramref name="results"/>. An entity reached through two different portal paths is returned once (the
    /// FIRST/nearest path wins, mirroring QC's <c>WarpZone_findradius_dist</c> nearest-wins bookkeeping).
    /// </summary>
    public static void FindRadiusWarpzone(WarpzoneManager? manager, Vector3 origin, float radius,
        List<WarpzoneRadiusHit> results)
    {
        results.Clear();
        if (Api.Services is null || radius <= 0f) return;

        // No warpzones → the plain findradius with identity transforms (QC: the disabled fast path comment in
        // WarpZone_FindRadius — the port can safely take it because it does not need LOS-through-zone here).
        if (manager is null || manager.Zones.Count == 0)
        {
            var flat = new List<Entity>();
            Api.Entities.FindInRadius(origin, radius, flat);
            for (int i = 0; i < flat.Count; i++)
            {
                // QC WarpZone_FindRadius runs WarpZoneLib_BadEntity on every candidate even with no warpzones
                // present — the blacklist (waypoints, info_/target_ helpers, view models, …) is not gated on the
                // zone count. Apply it here too so the fast path matches the recursive path's filtering.
                Entity e = flat[i];
                if (IsBadEntity(e)) continue;
                if (e.ClassName == "trigger_warpzone") continue;
                results.Add(new WarpzoneRadiusHit(e, WarpzoneTransformChain.Identity, origin));
            }
            return;
        }

        // De-dup set keyed by entity reference; nearest path wins (QC WarpZone_findradius_dist).
        var seen = new HashSet<Entity>();
        Recurse(manager, origin, radius, WarpzoneTransformChain.Identity, seen, results, depth: 0);
    }

    private static void Recurse(WarpzoneManager manager, Vector3 org, float rad, WarpzoneTransformChain toBlast,
        HashSet<Entity> seen, List<WarpzoneRadiusHit> results, int depth)
    {
        if (rad <= 0f) return;
        if (depth >= MaxZoneDepth) return; // QC: "Too many warpzones in sequence, aborting"

        // Gather every entity in this local radius (QC FOREACH_ENTITY_RADIUS), skipping the blacklist.
        var local = new List<Entity>();
        Api.Entities.FindInRadius(org, rad, local);
        for (int i = 0; i < local.Count; i++)
        {
            Entity e = local[i];
            if (IsBadEntity(e)) continue;
            if (e.ClassName == "trigger_warpzone") continue; // zones themselves are handled by the recursion below
            if (!seen.Add(e)) continue;                       // first/nearest path already recorded this victim
            results.Add(new WarpzoneRadiusHit(e, toBlast, org));
        }

        // Recurse through each portal whose mouth lies within the remaining radius (QC: the `for (e = wz; ...)`
        // loop over the warpzones the radius search found). Walking the manager's zones directly is the port's
        // stand-in for QC tagging each found trigger_warpzone in FOREACH_ENTITY_RADIUS.
        IReadOnlyList<Warpzone> zones = manager.Zones;
        for (int z = 0; z < zones.Count; z++)
        {
            Warpzone wz = zones[z];
            if (!wz.Linked) continue;

            // Distance from the blast to the portal's IN plane mouth. QC measures via the trigger box; the port
            // uses the plane origin (the zone's center), within the remaining radius.
            float distToZone = (wz.InOrigin - org).Length();
            if (distToZone >= rad) continue;

            // Only propagate through a portal the blast is on the FRONT of — the side the IN plane's normal faces
            // (dot > 0), i.e. the side from which an entity would cross INTO the seam. A blast BEHIND the plane
            // does not reach the linked OUT room through this seam. (QC recurses through every in-radius zone and
            // leans on the LOS trace + radius reduction to discard the wrong side; the port's explicit front-side
            // gate is the same outcome without the extra trace, and avoids back-warping loops.)
            if (Vector3.Dot(org - wz.InOrigin, wz.Transform.InForward) < 0f)
                continue;

            // Step the blast through the portal (QC org0_new = WarpZone_TransformOrigin(e, org)).
            Vector3 orgNew = wz.Transform.TransformOrigin(org);

            // QC WarpZone_FindRadius_Recurse (common.qc:650): the recursion radius is
            // bound(0, rad - vlen(org_new - org0_new), rad - 8), where org0_new is the blast transformed THROUGH
            // the zone and org_new = trace_endpos of a LOS traceline from the zone's target origin to org0_new.
            // With a clear seam (the analytic port doesn't re-trace solid here) org_new == org0_new, so the
            // travelled term is ~0 and the binding clamp is the rad-8 UPPER bound — Base recurses with very nearly
            // the FULL remaining radius (only shaved by 8qu), NOT reduced by the distance to the portal mouth.
            // (The previous port reduced by distToZone, under-budgeting far-side splash through a portal.)
            float radNew = QClamp(rad, 0f, rad - RadiusRecurseMargin);
            if (radNew <= 0f) continue;

            // Accumulate the transform so far-side victims map back to the blast frame, and recurse.
            WarpzoneTransformChain chainNew = toBlast.Append(wz.Transform);
            Recurse(manager, orgNew, radNew, chainNew, seen, results, depth + 1);
        }
    }

    /// <summary>
    /// Port of <c>WarpZoneLib_BadEntity</c> (common.qc:562): the blacklist of classnames the radius query
    /// ignores (weapon view models, waypoints, info_/target_ helpers, pure/spawnfunc entities). Keeps splash
    /// from "hitting" non-combat helper entities.
    /// </summary>
    private static bool IsBadEntity(Entity e)
    {
        if (e.IsFreed) return true;

        // QC's leading `is_pure(e)` guard = (e.pure_data && e.solid == SOLID_NOT): a bare data object created
        // without a spawnfunc, left non-solid. The port has no `pure_data` flag, but the dominant pure case — a
        // data object with no real classname AND non-solid — is captured by the empty-classname branch below plus
        // this Solid.Not pairing. We require BOTH conditions exactly as is_pure (a non-solid REAL entity, e.g. a
        // non-solid item, still keeps its classname and is NOT treated as pure), so legitimate non-solid victims
        // are not excluded. A faithful full port needs an Entity.PureData flag (see todos).
        if (e.Solid == Solid.Not && string.IsNullOrEmpty(e.ClassName)) return true;

        string s = e.ClassName;
        switch (s)
        {
            case "weaponentity":
            case "exteriorweaponentity":
            case "sprite_waypoint":
            case "waypoint":
            case "spawnfunc":
            case "weaponchild":
            case "chatbubbleentity":
            case "buff_model":
            case "":
                return true;
        }
        if (s.StartsWith("target_", System.StringComparison.Ordinal)) return true;
        if (s.StartsWith("info_", System.StringComparison.Ordinal)) return true;
        return false;
    }

    private static float QClamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}

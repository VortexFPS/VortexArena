using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// The AABB-vs-brush sweep collision service — the C# reimplementation of Darkplaces'
/// <c>traceline</c>/<c>tracebox</c>/<c>pointcontents</c> (Base/darkplaces/sv_phys.c SV_TraceBox /
/// SV_PointSuperContents, collision.c Collision_TraceBrushBrushFloat). This is the fidelity-critical
/// core (planning/specs/determinism-and-physics.md §"The collision/trace service"): NOT Godot physics.
///
/// The sweep models the moving entity as a box brush (the "trace" brush) translating from
/// <c>start</c> to <c>end</c>, tested against each static map brush and each solid entity's bounding
/// box (the "other" brush) using the Separating Axis Theorem / Minkowski-sum enter/leave fraction
/// accumulation. Because neither the box nor an axis-aligned map brush rotates during the sweep, the
/// start-plane and end-plane of every candidate separating axis are identical, which lets us drop
/// DP's start/end plane interpolation while keeping its fraction math exact.
/// </summary>
public sealed class TraceService : ITraceService
{
    private CollisionWorld _world;

    /// <summary>Entity provider for the entity-vs-box sweep. The EntityService implements this.</summary>
    public interface IEntityProvider
    {
        IReadOnlyList<Entity> SolidEntities { get; }

        /// <summary>The entity-area-grid broadphase (D1): fill <paramref name="results"/> with every entity whose
        /// XY footprint overlaps [<paramref name="mins"/>,<paramref name="maxs"/>], de-duplicated. A conservative
        /// superset of the entities the move could touch; the trace applies the precise per-entity test.</summary>
        void EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results);

        /// <summary>
        /// For a SOLID_BSP entity, return its brush model's collision brushes in MODEL-LOCAL space (origin-
        /// relative) and the entity's local→world transform (origin + angles, scale 1). The trace clips the
        /// moving box against these in local space and transforms the impact plane back to world space — DP's
        /// <c>SV_ClipMoveToEntity</c> → <c>Collision_ClipToGenericEntity</c> path. Returns false when the
        /// entity has no brush-model geometry (e.g. a SOLID_BBOX entity, or an inline model whose brushes
        /// aren't loaded and whose AABB is degenerate), in which case the caller uses the AABB sweep.
        /// </summary>
        bool TryGetEntityBrushModel(Entity e, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld);
    }

    private readonly IEntityProvider? _entities;

    public TraceService(CollisionWorld world, IEntityProvider? entities = null)
    {
        _world = world;
        _entities = entities;
    }

    /// <summary>
    /// Swap the static collision world this service traces against. Used by a pure network client that starts on
    /// a flat prediction floor and later loads the server's real map BSP (<c>NetGame.LoadClientMapFromServer</c>):
    /// the swap makes the predicted local player clip real geometry instead of the placeholder floor. Safe to call
    /// between frames — traces are single-threaded and hold no per-world cached state (only the per-call scratch
    /// buffers and the hull-keyed box cache, both world-independent).
    /// </summary>
    public void SetCollisionWorld(CollisionWorld world) => _world = world;

    /// <summary>
    /// [T45] Wire this world's warpzone manager (QC global <c>g_warpzones</c>) so the warpzone-aware trace
    /// extensions (<see cref="XonoticGodot.Common.Gameplay.WarpzoneManager"/> via
    /// <c>ITraceService.TraceLineWarpzone</c>/<c>TraceBoxWarpzone</c>) can recurse hitscan/projectile traces
    /// through linked portals. Call once after the map's zones are linked (GameWorld.Boot, after InitMapZones).
    /// Passing <c>null</c> (a map with no warpzones, or teardown) reverts every warpzone-aware trace to a plain
    /// trace. Forwarded via <see cref="TraceServiceWarpzoneBridge"/> to the Common-side ambient the warpzone
    /// trace extensions (<c>ITraceService.TraceLineWarpzone</c>/<c>TraceBoxWarpzone</c>) resolve.
    /// </summary>
    public void SetWarpzoneManager(XonoticGodot.Common.Gameplay.WarpzoneManager? manager)
        => TraceServiceWarpzoneBridge.Publish(manager);

    /// <summary>
    /// The map's compiled visibility set (DP Mod_Q3BSP vis), backing <see cref="CheckPvs"/>. Set by the host
    /// that loaded the BSP (via <c>new BspPvs(bsp)</c>); null on a non-BSP/test world, where every PVS query is
    /// conservatively visible.
    /// </summary>
    public XonoticGodot.Formats.Bsp.BspPvs? Pvs { get; set; }

    // Scratch buffers reused across calls (the sim is single-threaded per world; a trace fires no callbacks
    // mid-sweep, so these are never re-entered within one Trace/PointContents call).
    private readonly List<Brush> _candidates = new(64);
    private readonly List<Entity> _entCandidates = new(64);   // entity-area-grid broadphase result for the sweep (D1)
    private readonly List<Entity> _pcCandidates = new(16);    // entity-area-grid broadphase result for PointContents (D1)

    // The MOVING trace box is rebuilt every Trace via Brush.FromBox — a Brush + Vector3[8] + BrushPlane[6]
    // allocation (~360 B) each call. But its shape is the mover's hull (mins/maxs), CONSTANT across the many
    // traces a single slide-move tick fires and shared by every entity of a given hull size — and the box is
    // READ-ONLY during the sweep (ClipToBrushModel's rotated case builds a fresh brush, never mutating this one).
    // So cache it per (mins,maxs): a handful of distinct hulls (player standing/crouched, point projectiles)
    // collapse the per-trace box allocation to one-time. This was the dominant sim.move GC churn under bot load.
    private readonly Dictionary<(Vector3 Mins, Vector3 Maxs), Brush> _boxCache = new();

    // A single pooled entity-AABB box brush, refilled in place per solid candidate in ClipToEntities (each
    // candidate is used immediately + never retained), instead of allocating a fresh Brush per entity per trace.
    // The dominant remaining sim.move allocation under bot/player clustering (many candidates per sweep).
    private readonly Brush _entBrush = Brush.FromBox(new Vector3(-1f, -1f, -1f), Vector3.One);

    /// <summary>The cached read-only moving-box brush for a hull (allocated once per distinct mins/maxs).</summary>
    private Brush BoxBrush(Vector3 mins, Vector3 maxs)
    {
        var key = (mins, maxs);
        if (!_boxCache.TryGetValue(key, out Brush? b))
        {
            b = Brush.FromBox(mins, maxs);
            _boxCache[key] = b;
        }
        return b;
    }

    // =============================================================================================
    // ITraceService
    // =============================================================================================

    /// <summary>
    /// (S5 sv_threaded) The host's cross-thread serialisation gate. NULL (the default, and always on the
    /// single-threaded path) = no lock, byte-for-byte today's behaviour. When the listen server runs its sim
    /// on the worker thread, the host installs the SAME object ServerNet.Tick locks — because this service
    /// keeps shared mutable scratch (_candidates/_pcCandidates/_boxCache/_entBrush) and reads the live entity
    /// areagrid, a MAIN-thread trace (faithful-particle bounces, crosshair true-aim, projectile prediction —
    /// none of which run inside NetGame._Process's gated span) racing a worker tick corrupted both ends
    /// (NREs in TraceBrushVsBrush / Movement.Move — caught by the first 180 s threaded soak). Monitor is
    /// reentrant, so the worker (already holding the gate around its whole tick) passes straight through;
    /// main-thread callers serialize against the tick boundary.
    /// </summary>
    public object? ConcurrencyGate;

    /// <summary>
    /// WS1 instrumentation: per-thread Stopwatch ticks spent WAITING to acquire <see cref="ConcurrencyGate"/>
    /// (an uncontended entry adds ~ns; a contended one adds the real wait — at most one worker tick under the
    /// per-tick gating). NetGame reads+resets the MAIN thread's value each frame into the <c>sv.gatewait_ms</c>
    /// profiler counter — the number that says whether prediction traces actually stall behind the sim worker.
    /// Zero-cost on the unthreaded default path (gate null → the clock is never read).
    /// </summary>
    [ThreadStatic] public static long GateWaitTicks;

    public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
    {
        object? gate = ConcurrencyGate;
        if (gate is null)
            return TraceUnlocked(start, mins, maxs, end, filter, ignore);
        // The wait clock is only ever read back into Prof.Mark("sv.gatewait_ms"), which is inert while the
        // profiler is off — so don't pay two QueryPerformanceCounter reads per trace during normal play. A
        // bot-heavy tick runs thousands of traces (the strategy pool alone budgets 96/tick), and sv_threaded
        // is the DEFAULT, so this path is the common one. Numbers are unchanged whenever profiling is on.
        if (!XonoticGodot.Common.Diagnostics.Prof.Enabled)
        {
            lock (gate)
                return TraceUnlocked(start, mins, maxs, end, filter, ignore);
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (gate)
        {
            GateWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return TraceUnlocked(start, mins, maxs, end, filter, ignore);
        }
    }

    private TraceResult TraceUnlocked(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
    {
        // The moving trace box brush (cached per hull — see _boxCache). For a point trace (mins==maxs) it's a point brush.
        Brush box = BoxBrush(mins, maxs);

        // The SUPERCONTENTS this move clips against, derived from the moving entity exactly as DP's
        // SV_GenericHitSuperContentsMask(passedict) does (our 'ignore' IS DP's passedict). A walking player
        // picks up PlayerClip — so common/clip walls block — and drops Corpse; a null mover keeps the generic
        // solid+body+corpse default.
        int hitMask = GenericHitMask(ignore);

        var trace = new SweepState
        {
            Fraction = 1f,
            HitMask = hitMask,
        };

        // --- clip to world brushes ---
        // Broadphase: gather brushes along the SWEPT CORRIDOR of the move (perf 2.1: a long diagonal
        // trace's enclosing AABB used to hand the narrowphase every brush under half the grid — the
        // catharsis shotgun/true-aim melts; QuerySwept marches cell-sized segments instead). The plain
        // rectangle bounds are still computed for the ENTITY broadphase below (few entities — the
        // rectangle is fine there).
        Vector3 sweepMins, sweepMaxs;
        SweptBounds(start, end, mins, maxs, out sweepMins, out sweepMaxs);

        _candidates.Clear();
        _world.QuerySwept(start, end, mins, maxs, _candidates);

        for (int i = 0; i < _candidates.Count; i++)
            TraceBrushVsBrush(ref trace, box, start, end, _candidates[i], worldBrush: true, hitEnt: null);

        bool worldStartSolid = trace.StartSolid;

        // MOVE_WORLDONLY stops at the world.
        if (filter != MoveFilter.WorldOnly && _entities != null)
        {
            // MOVE_MISSILE expands the moving box by ±15 when clipping against monsters (DP).
            Vector3 entMins = mins, entMaxs = maxs;
            if (filter == MoveFilter.Missile)
            {
                entMins -= new Vector3(15f, 15f, 15f);
                entMaxs += new Vector3(15f, 15f, 15f);
            }
            Brush entBox = (entMins == mins && entMaxs == maxs) ? box : BoxBrush(entMins, entMaxs);

            ClipToEntities(ref trace, entBox, start, end, sweepMins, sweepMaxs, filter, ignore, mins, maxs);
        }

        return BuildResult(trace, start, end, worldStartSolid);
    }

    /// <summary>QC <c>checkpvs</c>: delegate to the BSP visibility set, or "visible" when the world is unvised.</summary>
    public bool CheckPvs(Vector3 viewpoint, Vector3 target) => Pvs?.IsInPvs(viewpoint, target) ?? true;

    public int PointContents(Vector3 point)
    {
        object? gate = ConcurrencyGate;
        if (gate is null)
            return PointContentsUnlocked(point);
        if (!XonoticGodot.Common.Diagnostics.Prof.Enabled)   // see Trace: no clock unless it is read back
        {
            lock (gate)
                return PointContentsUnlocked(point);
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (gate)
        {
            GateWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return PointContentsUnlocked(point);
        }
    }

    private int PointContentsUnlocked(Vector3 point)
    {
        int contents = 0;

        // World brushes containing the point.
        _candidates.Clear();
        _world.Query(point, point, _candidates);
        for (int i = 0; i < _candidates.Count; i++)
        {
            var b = _candidates[i];
            if (b.ContainsPoint(point))
                contents |= b.Contents;
        }

        // SV_PointSuperContents (sv_phys.c:611) also ORs each SOLID_BSP entity's brush-model contents at the
        // point — sv_gameplayfix_swiminbmodels, default 1, so you can swim inside a (possibly moving) water
        // bmodel. For each SOLID_BSP entity we transform the point into its local space (inverse matrix) and
        // OR in the contents of any local brush that contains it. (Bounding-box entities don't contribute.)
        if (_entities != null)
        {
            // Broadphase (D1): only entities whose footprint overlaps the point, not every solid entity. The
            // SOLID_BSP + overlap filters below are unchanged, so the OR'd contents are identical.
            _entities.EntitiesInBox(point, point, _pcCandidates);
            var ents = _pcCandidates;
            for (int i = 0; i < ents.Count; i++)
            {
                Entity touch = ents[i];
                if (touch.IsFreed || touch.Solid != Solid.Bsp) continue;
                if (!CollisionWorld.BoxesOverlap(point, point, touch.Origin + touch.Mins, touch.Origin + touch.Maxs))
                    continue;
                if (!_entities.TryGetEntityBrushModel(touch, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld))
                    continue;

                Vector3 local = toWorld.Inverted().TransformPoint(point);
                for (int b = 0; b < localBrushes.Count; b++)
                {
                    Brush mb = localBrushes[b];
                    if (mb != null && mb.ContainsPoint(local))
                        contents |= mb.Contents;
                }
            }
        }

        return contents;
    }

    // =============================================================================================
    // Entity bounding-box sweep (the entity half of SV_TraceBox, sv_phys.c:531)
    // =============================================================================================

    private void ClipToEntities(ref SweepState trace, Brush box, Vector3 start, Vector3 end,
        Vector3 sweepMins, Vector3 sweepMaxs, MoveFilter filter, Entity? ignore, Vector3 pointMins, Vector3 pointMaxs)
    {
        bool pointTrace = pointMins == pointMaxs;
        // Broadphase (D1): only the entities whose footprint overlaps the swept AABB, not every solid entity.
        // The precise BoxesOverlap + Solid/filter tests below are unchanged, so the clipped set is identical.
        _entities!.EntitiesInBox(sweepMins, sweepMaxs, _entCandidates);
        var ents = _entCandidates;
        for (int i = 0; i < ents.Count; i++)
        {
            Entity touch = ents[i];
            if (touch.IsFreed) continue;

            // solid < SOLID_BBOX (Not/Trigger) never block a move.
            if (touch.Solid < Solid.BBox) continue;

            // NOMONSTERS only clips against BSP (brush model) entities.
            if (filter == MoveFilter.NoMonsters && touch.Solid != Solid.Bsp) continue;

            // NOPLAYERS (port extension, sv_player_softcollision): player entities never block this move —
            // the player movement hull slips through other players (the server's post-move PlayerSeparation
            // pass resolves overlaps). Matches GenericHitMask's "player" notion: SOLID_SLIDEBOX carrying
            // FL_CLIENT (monsters are SLIDEBOX + FL_MONSTER and still clip).
            if (filter == MoveFilter.NoPlayers && touch.Solid == Solid.SlideBox &&
                (touch.Flags & EntFlags.Client) != 0) continue;

            if (ignore != null)
            {
                if (touch == ignore) continue;                 // don't clip against self
                if (touch.Owner == ignore) continue;           // owner vs owned
                if (ignore.Owner == touch) continue;           // owned vs owner
            }

            // don't clip points against points (zero-size touch can't collide a point move)
            if (pointTrace && touch.Mins == touch.Maxs &&
                (filter != MoveFilter.Missile || (touch.Flags & EntFlags.Monster) == 0))
                continue;

            // broadphase reject against the move's swept bounds
            Vector3 absMin = touch.Origin + touch.Mins;
            Vector3 absMax = touch.Origin + touch.Maxs;
            if (!CollisionWorld.BoxesOverlap(sweepMins, sweepMaxs, absMin, absMax))
                continue;

            // SOLID_BSP brush-model entities (func_door/plat/breakable, rotating doors, etc.) clip against
            // the model's actual brush planes transformed into the entity's local space — DP's
            // SV_ClipMoveToEntity → Collision_ClipToGenericEntity. We only take this path when the entity
            // really has brush-model geometry; everything else (SOLID_BBOX/CORPSE, alias-model SOLID_BSP
            // without brushes) keeps the AABB sweep below.
            if (touch.Solid == Solid.Bsp &&
                _entities.TryGetEntityBrushModel(touch, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld))
            {
                ClipToBrushModel(ref trace, box, start, end, localBrushes, toWorld, touch);
                continue;
            }

            // The touched entity's bounding box becomes a static brush at its world position — refilled into the
            // pooled box brush (no per-candidate allocation), except a degenerate point entity which needs a
            // point brush (kept as a rare fresh alloc).
            int bodyContents = touch.Solid == Solid.Corpse ? SuperContents.Corpse : SuperContents.Body;
            Brush other;
            if (absMin == absMax)
                other = Brush.FromBox(absMin, absMax, bodyContents);
            else
            {
                Brush.RefillBox(_entBrush, absMin, absMax, bodyContents);
                other = _entBrush;
            }

            TraceBrushVsBrush(ref trace, box, start, end, other, worldBrush: false, hitEnt: touch);
        }
    }

    // =============================================================================================
    // SOLID_BSP brush-model clip — port of Collision_ClipToGenericEntity (collision.c:1791) for the
    // server SV_TraceBox path (scale 1, AABB body fallback already folded into the supplied brushes).
    //
    // DP transforms the moving box into the entity's LOCAL space (by the inverse matrix), sweeps it
    // against the model's local brushes, then transforms the impact plane back to world space. Because
    // the entity transform is rigid (rotation + translation, scale 1), transforming a box that
    // *translates* from start->end yields a box of fixed orientation that still translates in local
    // space — so the single-translation SAT sweep (TraceBrushVsBrush) stays exact: we build the box's
    // local orientation once (rotation-only transform of the centered box) and feed it the local-space
    // start/end positions. The non-rotated case collapses to a plain origin subtraction.
    // =============================================================================================

    private void ClipToBrushModel(ref SweepState trace, Brush box, Vector3 start, Vector3 end,
        IReadOnlyList<Brush> localBrushes, EntityMatrix toWorld, Entity hitEnt)
    {
        EntityMatrix inv = toWorld.Inverted();

        // Move endpoints into the entity's local space (DP: Matrix4x4_Transform(inversematrix, ...)).
        Vector3 localStart = inv.TransformPoint(start);
        Vector3 localEnd = inv.TransformPoint(end);

        // The moving box, centred on the move position, carried into local orientation. For the common
        // no-rotation case the box is unchanged (the AABB fast path stays available); for a rotated brush
        // model the box becomes a fixed-orientation oriented box (rotation only — its position is supplied
        // by localStart/localEnd, matching TraceBrushVsBrush's Points-centred + Dot(axis, boxStart) model).
        Brush localBox = inv.IsTranslationOnly ? box : box.Transform(inv.RotationOnly());

        for (int i = 0; i < localBrushes.Count; i++)
        {
            Brush mb = localBrushes[i];
            if (mb is null) continue;

            // TraceBrushVsBrush only overwrites the impact plane when it records a strictly closer hit, so a
            // drop in Fraction across the call means this brush produced the new closest impact and its
            // (local-space) plane now lives in trace — transform it back to world space (DP transforms the
            // plane by 'matrix' after the local trace).
            float prevFrac = trace.Fraction;

            TraceBrushVsBrush(ref trace, localBox, localStart, localEnd, mb, worldBrush: false, hitEnt: hitEnt);

            if (trace.Fraction < prevFrac)
            {
                (Vector3 wn, float wd) = toWorld.TransformPositivePlane(trace.PlaneNormal, trace.PlaneDist);
                trace.PlaneNormal = wn;
                trace.PlaneDist = wd;
            }
        }
    }

    // =============================================================================================
    // The SAT sweep — port of Collision_TraceBrushBrushFloat (collision.c:559).
    //
    // 'box' is the moving trace brush translating start->end; 'other' is the static brush (a map
    // brush or an entity AABB). We enumerate candidate separating axes:
    //   1. every face plane of 'other'
    //   2. every face plane of the moving box
    //   3. (skipped for AABB-vs-AABB) cross products of box edge dirs with other's edge dirs
    // For each axis we compute how far the moving box's nearest point is in front of other's
    // furthest point at the start and end of the move, and accumulate the [enterfrac, leavefrac]
    // interval during which the projections overlap. If the interval is empty the brushes never
    // touch on this axis (separating axis found) → no collision.
    //
    // For AABB box vs AABB map brush, DP marks the brush hasaabbplanes and skips axes (2) and (3)
    // entirely (the brush's own planes already separate it from any AABB); we honor that fast path.
    // =============================================================================================

    private static void TraceBrushVsBrush(ref SweepState trace, Brush box, Vector3 boxStart, Vector3 boxEnd,
        Brush other, bool worldBrush, Entity? hitEnt)
    {
        // The moving box translates by 'move' over the sweep; its points/planes shift with it.
        // We keep 'other' fixed and translate the box, so a candidate plane's distance to the box
        // points changes linearly with the box translation.
        Vector3 move = boxEnd - boxStart;

        // Decide how many axis groups to test.
        int otherPlanes = other.Sides.Length;
        int boxPlanes = box.Sides.Length;

        // Fast AABB path: box is AABB and other has aabb planes → only test other's planes.
        bool aabbFast = box.IsAabb && other.IsAabb;

        float enterFrac = -1f;
        float leaveFrac = 1f;
        float enterFrac2 = -1f; // nudged fraction actually stored
        Vector3 impactNormal = Vector3.Zero;
        float impactDist = 0f;
        int hitSurfaceFlags = 0;
        string? hitTexture = null;   // DP collision.c:573 const texture_t *hittexture = NULL

        // total candidate axes
        int totalEdgeAxes = aabbFast ? 0 : box.EdgeDirs.Length * other.EdgeDirs.Length * 2;
        int n1 = otherPlanes;
        int n2 = aabbFast ? otherPlanes : otherPlanes + boxPlanes;
        int n3 = n2 + totalEdgeAxes;

        for (int nplane = 0; nplane < n3; nplane++)
        {
            Vector3 axis;
            int axisSurfaceFlags;
            string? axisTexture;       // DP picks the hit texture per axis class (collision.c:676-694)
            bool axisFromOther;

            if (nplane < n1)
            {
                // axis is one of 'other's planes → its per-plane texture (DP collision.c:681).
                axis = other.Sides[nplane].Normal;
                axisSurfaceFlags = other.Sides[nplane].SurfaceFlags;
                axisTexture = other.Sides[nplane].Texture;
                axisFromOther = true;
            }
            else if (nplane < n2)
            {
                // axis is one of the moving box's planes → its per-plane texture (DP collision.c:688).
                // For the SV_TraceBox box this is NULL (DP's box brush has a NULL texture, collision.c:1189
                // with texture=NULL), so a box-plane impact reports no texture — faithful, and never the
                // selected axis on the AABB-vs-AABB fast path (numplanes2 == numplanes1) the caulk consumer
                // exercises. (Recon suggested falling back to other.Texture here; mirroring DP's NULL is
                // strictly more faithful and behaves identically for every live consumer.)
                int bp = nplane - n1;
                axis = box.Sides[bp].Normal;
                axisSurfaceFlags = other.SurfaceFlags;
                axisTexture = box.Sides[bp].Texture;
                axisFromOther = false;
            }
            else
            {
                // edge-cross axis: cross a box edge dir with an other edge dir → brush-wide texture (DP:693).
                int e = nplane - n2;
                int sub = e >> 1;
                int e2 = sub / box.EdgeDirs.Length;
                int e1 = sub - e2 * box.EdgeDirs.Length;
                Vector3 cd = ((e & 1) != 0)
                    ? Vector3.Cross(box.EdgeDirs[e1], other.EdgeDirs[e2])
                    : Vector3.Cross(other.EdgeDirs[e2], box.EdgeDirs[e1]);
                if (cd.LengthSquared() < Collision.EdgeCrossMinLength2)
                    continue; // degenerate
                axis = Vector3.Normalize(cd);
                axisSurfaceFlags = other.SurfaceFlags;
                axisTexture = other.Texture;
                axisFromOther = false;
            }

            // Plane dist of 'other' along this axis = furthest point of other in +axis direction.
            float otherDist = FurthestDist(axis, other.Points);

            // Start/end distance: nearest point of the (translated) box minus otherDist.
            // At t=0 the box is at boxStart; at t=1 it's at boxEnd. Translating a point set by 'd'
            // shifts every projection by Dot(axis, d), so nearest(box + d) = nearest(box) + Dot(axis,d).
            float boxNearestStart = NearestDist(axis, box.Points) + Vector3.Dot(axis, boxStart);
            float boxNearestEnd = boxNearestStart + Vector3.Dot(axis, move);

            float startDist = boxNearestStart - otherDist;
            float endDist = boxNearestEnd - otherDist;

            if (startDist > endDist)
            {
                // approaching the brush along this axis
                if (endDist > 0f)
                    return; // still separated at end of move → never collides
                if (startDist >= 0f)
                {
                    // enter event
                    float imove = 1f / (startDist - endDist);
                    float f = startDist * imove;
                    if (enterFrac < f)
                    {
                        enterFrac = f;
                        if (enterFrac > leaveFrac)
                            return; // interval empty
                        enterFrac2 = (startDist - Collision.ImpactNudge) * imove;
                        if (enterFrac2 >= trace.Fraction)
                            return; // farther than an existing hit
                        // impact plane = this separating axis (start==end plane, so no interp)
                        impactNormal = axis;
                        impactDist = otherDist;
                        hitSurfaceFlags = axisFromOther ? axisSurfaceFlags : other.SurfaceFlags;
                        hitTexture = axisTexture;   // DP collision.c:681/688/693 → trace->hittexture (732)
                    }
                }
            }
            else
            {
                // receding from the brush along this axis
                if (startDist >= 0f)
                    return; // separated at start → no collision on this axis (already outside)
                if (endDist > 0f)
                {
                    // leave event
                    float f = startDist / (startDist - endDist);
                    if (leaveFrac > f)
                    {
                        leaveFrac = f;
                        if (enterFrac > leaveFrac)
                            return;
                    }
                }
            }
        }

        // Survived every axis → the brushes overlap during [enterFrac, leaveFrac].
        if (enterFrac > -1f)
        {
            // started outside and made contact: record the impact if its contents match the mask.
            if ((trace.HitMask & other.Contents) != 0)
            {
                trace.Fraction = Clamp01(enterFrac2);
                // The impact normal points along the separating axis away from 'other'. For a plane
                // belonging to 'other' this is the surface normal the mover slides on. For a plane
                // from the box (or an edge cross) DP keeps the same convention.
                trace.PlaneNormal = impactNormal;
                trace.PlaneDist = impactDist;
                trace.HitContents = other.Contents;
                trace.HitSurfaceFlags = hitSurfaceFlags;
                trace.HitTexture = hitTexture;
                trace.Ent = hitEnt;
                trace.Hit = true;
            }
        }
        else
        {
            // started inside the brush (no enter event): startsolid / allsolid bookkeeping.
            if ((trace.HitMask & other.Contents) != 0)
            {
                trace.StartSolid = true;
                if (leaveFrac < 1f)
                    trace.AllSolid = true;
                // NOTE: deliberately do NOT set trace.HitTexture here. DP stores the startsolid texture in a
                // SEPARATE field (trace->starttexture, collision.c:753), and the QC-visible global
                // trace_dphittexturename reads trace->hittexture ONLY (prvm_cmds.c:5242), which is left NULL on
                // a pure startsolid (no enter event sets it). So a started-inside trace reports DpHitTextureName
                // == null — matching DP exactly. (trace->starttexture has no QC accessor and no port consumer.)
            }
        }
    }

    // =============================================================================================
    // helpers
    // =============================================================================================

    /// <summary>
    /// Port of <c>SV_GenericHitSuperContentsMask</c> (sv_phys.c): the SUPERCONTENTS mask a move clips against,
    /// derived from the moving entity (DP's <c>passedict</c> — our <paramref name="ignore"/>). A walking player
    /// (SOLID_SLIDEBOX, no FL_MONSTER) clips <c>Solid|Body|PlayerClip</c>; a monster <c>Solid|Body|MonsterClip</c>;
    /// a corpse or trigger <c>Solid|Body</c>; everything else (and a null mover) the generic
    /// <c>Solid|Body|Corpse</c> default. DP also honors a per-entity <c>dphitcontentsmask</c> override, which the
    /// port doesn't model yet — so a projectile (SOLID_BBOX) keeps the generic default exactly as before.
    /// </summary>
    private static int GenericHitMask(Entity? ignore)
    {
        if (ignore is null)
            return SuperContents.DefaultHitMask;

        // DP checks the per-entity dphitcontentsmask FIRST: a nonzero value overrides the solid-derived
        // default (sv_phys.c SV_GenericHitSuperContentsMask). A projectile (PROJECTILE_MAKETRIGGER) uses this
        // to keep SOLID|BODY|CORPSE while being SOLID_CORPSE — so it clips corpses but is transparent to a
        // player's PlayerClip-masked movement (the rocket-hits-the-firer fix).
        if (ignore.DpHitContentsMask != 0)
            return ignore.DpHitContentsMask;

        switch (ignore.Solid)
        {
            case Solid.SlideBox:
                return (ignore.Flags & EntFlags.Monster) != 0
                    ? SuperContents.Solid | SuperContents.Body | SuperContents.MonsterClip
                    : SuperContents.Solid | SuperContents.Body | SuperContents.PlayerClip;
            case Solid.Corpse:
            case Solid.Trigger:
                return SuperContents.Solid | SuperContents.Body;
            default:
                return SuperContents.DefaultHitMask; // Solid | Body | Corpse
        }
    }

    /// <summary>nearestplanedist_float (collision.c:124): min projection of points onto axis.</summary>
    private static float NearestDist(Vector3 axis, Vector3[] points)
    {
        if (points.Length == 0) return 0f;
        float best = Vector3.Dot(points[0], axis);
        for (int i = 1; i < points.Length; i++)
        {
            float d = Vector3.Dot(points[i], axis);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>furthestplanedist_float (collision.c:140): max projection of points onto axis.</summary>
    private static float FurthestDist(Vector3 axis, Vector3[] points)
    {
        if (points.Length == 0) return 0f;
        float best = Vector3.Dot(points[0], axis);
        for (int i = 1; i < points.Length; i++)
        {
            float d = Vector3.Dot(points[i], axis);
            if (d > best) best = d;
        }
        return best;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    /// <summary>Swept AABB of a box move (the clip region DP gathers entities/brushes in, sv_phys.c:495).</summary>
    private static void SweptBounds(Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, out Vector3 lo, out Vector3 hi)
    {
        lo = Vector3.Min(start, end) + mins - Vector3.One;
        hi = Vector3.Max(start, end) + maxs + Vector3.One;
    }

    private static TraceResult BuildResult(in SweepState s, Vector3 start, Vector3 end, bool worldStartSolid)
    {
        var r = TraceResult.Miss(end);
        r.Fraction = s.Fraction;
        r.EndPos = start + (end - start) * s.Fraction;
        r.AllSolid = s.AllSolid;
        r.StartSolid = s.StartSolid;
        if (s.Hit)
        {
            r.PlaneNormal = s.PlaneNormal;
            r.PlaneDist = s.PlaneDist;
            r.Ent = s.Ent;
            r.DpHitContents = s.HitContents;
            r.DpHitQ3SurfaceFlags = s.HitSurfaceFlags;
            r.DpHitTextureName = s.HitTexture;
        }
        // InOpen/InWater are classified by start contents; the engine fills waterlevel separately.
        r.InOpen = !s.StartSolid;
        return r;
    }

    /// <summary>Mutable accumulator threaded through the per-brush sweep (DP's trace_t under construction).</summary>
    private struct SweepState
    {
        public float Fraction;
        public bool Hit;
        public bool StartSolid;
        public bool AllSolid;
        public Vector3 PlaneNormal;
        public float PlaneDist;
        public Entity? Ent;
        public int HitContents;
        public int HitSurfaceFlags;
        public string? HitTexture;
        public int HitMask;
    }
}

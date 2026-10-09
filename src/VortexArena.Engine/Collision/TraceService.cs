using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;

namespace VortexArena.Engine.Collision;

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

        /// <summary>
        /// For an entity that <see cref="TryGetEntityBrushModel"/> declined: the triangle mesh of its model
        /// (an MD3 / IQM / OBJ decoration that is SOLID_BSP, or any model when the move is MOVE_HITMODEL) and
        /// the entity's local→world transform, so the trace clips against the model's shape instead of the
        /// entity's box - DP's <c>SV_TraceBox</c>: "if (solid == SOLID_BSP || type == MOVE_HITMODEL) model =
        /// SV_GetModelFromEdict(touch)", then <c>Collision_ClipToGenericEntity</c> → <c>model->TraceBox</c>
        /// (<c>Mod_CollisionBIH_TraceBox</c> on the model's collision BIH). Only asked for SOLID_BSP entities
        /// and under <see cref="MoveFilter.HitModel"/>. The default - and the native game's answer - is
        /// false: such an entity is clipped as its box, as it always was here.
        /// </summary>
        bool TryGetEntityMeshModel(Entity e, MoveFilter filter, out CollisionMesh? mesh, out EntityMatrix toWorld)
        {
            mesh = null;
            toWorld = EntityMatrix.Identity;
            return false;
        }

        /// <summary>
        /// For a SOLID_BSP entity showing a model of a Quake 1 format map ("*N": a door, a platform): the
        /// map's clipping hulls, the model's number in them and the entity's local-to-world transform. Such a
        /// model has no brushes; the trace goes through its hulls (<see cref="Q1HullCollision"/>), as DP's
        /// <c>Collision_ClipToGenericEntity</c> does through <c>Mod_Q1BSP_TraceBox</c>. Asked before
        /// <see cref="TryGetEntityBrushModel"/>. The default - and the native game's answer - is false.
        /// </summary>
        bool TryGetEntityHullModel(Entity e, out Q1HullCollision? hulls, out int model, out EntityMatrix toWorld)
        {
            hulls = null;
            model = 0;
            toWorld = EntityMatrix.Identity;
            return false;
        }
    }

    private readonly IEntityProvider? _entities;

    public TraceService(CollisionWorld world, IEntityProvider? entities = null)
    {
        _world = world;
        _entities = entities;
        _meshTrianglePlanes = _meshTriangle.Sides;
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
    /// extensions (<see cref="VortexArena.Common.Gameplay.WarpzoneManager"/> via
    /// <c>ITraceService.TraceLineWarpzone</c>/<c>TraceBoxWarpzone</c>) can recurse hitscan/projectile traces
    /// through linked portals. Call once after the map's zones are linked (GameWorld.Boot, after InitMapZones).
    /// Passing <c>null</c> (a map with no warpzones, or teardown) reverts every warpzone-aware trace to a plain
    /// trace. Forwarded via <see cref="TraceServiceWarpzoneBridge"/> to the Common-side ambient the warpzone
    /// trace extensions (<c>ITraceService.TraceLineWarpzone</c>/<c>TraceBoxWarpzone</c>) resolve.
    /// </summary>
    public void SetWarpzoneManager(VortexArena.Common.Gameplay.WarpzoneManager? manager)
        => TraceServiceWarpzoneBridge.Publish(manager);

    /// <summary>
    /// The map's compiled visibility set (DP Mod_Q3BSP vis), backing <see cref="CheckPvs"/>. Set by the host
    /// that loaded the BSP (via <c>new BspPvs(bsp)</c>); null on a non-BSP/test world, where every PVS query is
    /// conservatively visible.
    /// </summary>
    public VortexArena.Formats.Bsp.BspPvs? Pvs { get; set; }

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

    // Scratch for a mesh-model entity (ClipToMesh): the leaves of one walk of the model's hierarchy, and the
    // one triangle brush that is refilled for each of them (DP builds it on the stack per triangle).
    private readonly CollisionBih.Walker _meshWalker = new();
    private readonly List<int> _meshLeaves = new(64);
    private readonly Brush _meshTriangle = DarkPlacesPatchCollision.NewScratchTriangle();
    private readonly BrushPlane[] _meshTrianglePlanes;

    /// <summary>
    /// How <see cref="MoveFilter.Missile"/> grows the moving box. DarkPlaces grows it by 15 units a side
    /// ("size when clipping against monsters", sv_phys.c SV_TraceBox: clipmins2 / clipmaxs2) and uses the
    /// grown box only against entities with FL_MONSTER - against everything else, players included, a
    /// MOVE_MISSILE trace is a MOVE_NORMAL one. This port has always grown it against <em>every</em> entity,
    /// which is the default (false) and what the native game runs. True is DarkPlaces' rule, with the
    /// candidates gathered over the grown box as DarkPlaces gathers them.
    ///
    /// For trying DarkPlaces' rule on the whole native game without a code change, a process started with
    /// the environment variable <see cref="MissileTrialVariable"/> set to <c>monsters</c> has it on in
    /// every service it creates.
    /// </summary>
    public bool MissileGrowsOnlyAgainstMonsters { get; set; } = MissileTrial;

    /// <summary>The environment variable read once for the initial value of <see cref="MissileGrowsOnlyAgainstMonsters"/>.</summary>
    public const string MissileTrialVariable = "VORTEX_MISSILE_MARGIN";

    private static readonly bool MissileTrial = ReadMissileTrial();

    private static bool ReadMissileTrial()
    {
        try { return string.Equals(Environment.GetEnvironmentVariable(MissileTrialVariable)?.Trim(), "monsters", StringComparison.OrdinalIgnoreCase); }
        catch (System.Security.SecurityException) { return false; }
    }

    /// <summary>
    /// Sweep a box against a brush or a triangle with DarkPlaces' arithmetic, operation for operation
    /// (<see cref="TraceBrushVsBrushExact"/>), and honour <see cref="Brush.HasAabbPlanes"/>. Off by default.
    ///
    /// The default sweep is the same algorithm with its sums taken in a different order: it projects the
    /// box's corners relative to the box and adds the box's position, where DarkPlaces projects the corners
    /// at their place in the world. In exact arithmetic the two are one number; in single precision, at map
    /// coordinates in the thousands, they differ in the third decimal. That is far below anything a player
    /// feels, and it is enough to put an item a QuakeC program nudges out of a wall on the other side of a
    /// decision: whether a box 1/1000 of a unit from a triangle is touching it. Legacy mode, which must
    /// place things where a DarkPlaces server does, turns this on; the native game has no such obligation.
    /// </summary>
    public bool DarkPlacesArithmetic { get; set; }

    /// <summary>
    /// trace_t.startsupercontents of the last <see cref="Trace"/>: the contents of every brush (and every
    /// entity body) the moving box <em>started inside</em>, whatever the move's hit mask - what QuakeC reads
    /// as <c>trace_dpstartcontents</c>. DP ORs it together in the started-inside branch of
    /// Collision_TraceBrushBrushFloat ("trace->startsupercontents |= other_start->supercontents") and across
    /// entities in Collision_CombineTraces. It is not the contents at the start <em>point</em>: a box with
    /// one edge in a wall starts in SOLID though its origin is in the open, and Xonotic's
    /// _Movetype_TestEntityPosition decides "stuck" on exactly that. (<see cref="TraceResult"/> has no field
    /// for it, so it is kept here; valid until the next trace on this service.)
    /// </summary>
    public int LastStartContents { get; private set; }

    /// <summary>The fraction of the last trace in double precision, as DarkPlaces' <c>trace_t.fraction</c>
    /// holds it. Differs from <see cref="TraceResult.Fraction"/> only on a Quake 1 format map
    /// (<see cref="CollisionWorld.Hulls"/>), whose hull code computes it in doubles.</summary>
    public double LastFraction { get; private set; } = 1;

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
        if (!VortexArena.Common.Diagnostics.Prof.Enabled)
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

    /// <summary>
    /// <see cref="Trace"/> as DarkPlaces runs one: traced <paramref name="extend"/> units further than asked
    /// and cut back (<see cref="TraceExtension"/>; 1 for what QuakeC's traceline / tracebox do, 16 for the
    /// engine's own entity moves). <paramref name="clearedByExtend"/> is true when the only impact lay in the
    /// extra length and was discarded.
    /// </summary>
    public TraceResult TraceExtended(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore, float extend, out bool clearedByExtend)
    {
        TraceExtension extension = TraceExtension.Prepare(start, end, extend);
        TraceResult result = Trace(start, mins, maxs, extension.ExtendEnd, filter, ignore);
        clearedByExtend = extension.Finish(ref result);
        return result;
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

        // A box of no size is a point, and DP does not sweep a point against a collision triangle
        // (Mod_CollisionBIH_TraceLine / TracePoint): see ClipLineToTriangle. No world built by default has one.
        bool pointBox = box.Sides.Length == 0;
        bool exact = DarkPlacesArithmetic;
        if (exact && !pointBox) SetExactBox(box, start, end);
        for (int i = 0; i < _candidates.Count; i++)
        {
            Brush candidate = _candidates[i];
            if (pointBox && candidate.IsTriangle)
                ClipLineToTriangle(ref trace, start + mins, end + mins, candidate, hitEnt: null);
            else if (exact && !pointBox)
                TraceBrushVsBrushExact(ref trace, box, candidate, hitEnt: null);
            else if (exact)
                TraceLineVsBrushExact(ref trace, start + mins, end + mins, candidate, hitEnt: null);
            else
                TraceBrushVsBrush(ref trace, box, start, end, candidate, worldBrush: true, hitEnt: null);
        }

        // A Quake 1 format map: the world is clipping hulls, not brushes (Collision_ClipToWorld, Mod_Q1BSP_TraceBox).
        if (_world.Hulls is { } worldHulls) Q1HullClip.World(ref trace, worldHulls, start, mins, maxs, end);

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

            if (MissileGrowsOnlyAgainstMonsters && filter == MoveFilter.Missile)
            {
                // DP: "create the bounding box of the entire move" from min(hullmins, clipmins2) / max(hullmaxs,
                // clipmaxs2) - the grown box - and choose the box per entity in the loop.
                SweptBounds(start, end, entMins, entMaxs, out sweepMins, out sweepMaxs);
                ClipToEntities(ref trace, box, start, end, sweepMins, sweepMaxs, filter, ignore, mins, maxs, monsterBox: entBox);
            }
            else
                ClipToEntities(ref trace, entBox, start, end, sweepMins, sweepMaxs, filter, ignore, mins, maxs);
        }

        LastStartContents = trace.StartContents;
        LastFraction = trace.HasExactFraction ? trace.ExactFraction : trace.Fraction < 0f ? 0f : trace.Fraction;
        return BuildResult(trace, start, end, worldStartSolid);
    }

    /// <summary>QC <c>checkpvs</c>: delegate to the BSP visibility set, or "visible" when the world is unvised.</summary>
    public bool CheckPvs(Vector3 viewpoint, Vector3 target) => Pvs?.IsInPvs(viewpoint, target) ?? true;

    public int PointContents(Vector3 point)
    {
        object? gate = ConcurrencyGate;
        if (gate is null)
            return PointContentsUnlocked(point);
        if (!VortexArena.Common.Diagnostics.Prof.Enabled)   // see Trace: no clock unless it is read back
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
            // "collision triangle - skipped because they have no volume" (Mod_CollisionBIH_TracePoint)
            if (!b.IsTriangle && b.ContainsPoint(point))
                contents |= b.Contents;
        }

        if (_world.Hulls is { } pointHulls) contents |= pointHulls.PointContents(0, point);

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
                if (_entities.TryGetEntityHullModel(touch, out Q1HullCollision? entHulls, out int hullModel, out EntityMatrix hullToWorld) && entHulls is not null)
                {
                    contents |= entHulls.PointContents(hullModel, hullToWorld.Inverted().TransformPoint(point));
                    continue;
                }
                if (!_entities.TryGetEntityBrushModel(touch, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld))
                    continue;

                Vector3 local = toWorld.Inverted().TransformPoint(point);
                for (int b = 0; b < localBrushes.Count; b++)
                {
                    Brush mb = localBrushes[b];
                    if (mb != null && !mb.IsTriangle && mb.ContainsPoint(local))
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
        Vector3 sweepMins, Vector3 sweepMaxs, MoveFilter filter, Entity? ignore, Vector3 pointMins, Vector3 pointMaxs,
        Brush? monsterBox = null)
    {
        bool pointTrace = pointMins == pointMaxs;
        Brush plainBox = box;
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

            // (MissileGrowsOnlyAgainstMonsters) "if (type == MOVE_MISSILE && (int)flags & FL_MONSTER)" clip with
            // clipmins2 / clipmaxs2, else with clipmins / clipmaxs.
            if (monsterBox is not null)
                box = (touch.Flags & EntFlags.Monster) != 0 ? monsterBox : plainBox;

            // SOLID_BSP brush-model entities (func_door/plat/breakable, rotating doors, etc.) clip against
            // the model's actual brush planes transformed into the entity's local space — DP's
            // SV_ClipMoveToEntity → Collision_ClipToGenericEntity. We only take this path when the entity
            // really has brush-model geometry; everything else (SOLID_BBOX/CORPSE, alias-model SOLID_BSP
            // without brushes) keeps the AABB sweep below.
            // A model of a Quake 1 format map: its clipping hulls (see IEntityProvider.TryGetEntityHullModel).
            if (touch.Solid == Solid.Bsp &&
                _entities.TryGetEntityHullModel(touch, out Q1HullCollision? entHulls, out int hullModel, out EntityMatrix hullToWorld) && entHulls is not null)
            {
                Vector3 hullMins = pointMins, hullMaxs = pointMaxs;
                if (filter == MoveFilter.Missile && (monsterBox is null || (touch.Flags & EntFlags.Monster) != 0))
                {
                    hullMins -= new Vector3(15f, 15f, 15f);
                    hullMaxs += new Vector3(15f, 15f, 15f);
                }
                SweepState own = new() { Fraction = 1f, HitMask = trace.HitMask };
                Q1HullClip.Entity(ref own, entHulls, hullModel, hullToWorld, start, hullMins, hullMaxs, end, touch);
                CombineTraces(ref trace, own, touch);
                continue;
            }

            if (touch.Solid == Solid.Bsp &&
                _entities.TryGetEntityBrushModel(touch, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld))
            {
                if (DarkPlacesArithmetic)
                {
                    SweepState own = new() { Fraction = 1f, HitMask = trace.HitMask };
                    ClipToBrushModel(ref own, box, start, end, localBrushes, toWorld, touch);
                    CombineTraces(ref trace, own, touch);
                }
                else
                    ClipToBrushModel(ref trace, box, start, end, localBrushes, toWorld, touch);
                continue;
            }

            // A model that is not made of brushes, where DP would trace it (a SOLID_BSP decoration; anything
            // under MOVE_HITMODEL): its triangles. Only a provider that opts in answers true.
            if ((touch.Solid == Solid.Bsp || filter == MoveFilter.HitModel) &&
                _entities.TryGetEntityMeshModel(touch, filter, out CollisionMesh? mesh, out EntityMatrix meshToWorld) && mesh is not null)
            {
                // "model->TracePoint ... else Collision_ClipTrace_Point(bodymins, bodymaxs)": see CollisionMesh.PointUsesBodyBox
                if (!(box.Sides.Length == 0 && start == end && mesh.PointUsesBodyBox))
                {
                    if (DarkPlacesArithmetic)
                    {
                        SweepState own = new() { Fraction = 1f, HitMask = trace.HitMask };
                        ClipToMesh(ref own, box, start, end, mesh, meshToWorld, touch);
                        CombineTraces(ref trace, own, touch);
                    }
                    else
                        ClipToMesh(ref trace, box, start, end, mesh, meshToWorld, touch);
                    continue;
                }
            }

            if (DarkPlacesArithmetic)
            {
                SweepState own = new() { Fraction = 1f, HitMask = trace.HitMask };
                ClipToEntityBoxExact(ref own, box, start, end, touch);
                CombineTraces(ref trace, own, touch);
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
        bool exactBox = DarkPlacesArithmetic && box.Sides.Length != 0;
        Brush localBox;
        if (exactBox) localBox = inv.IsTranslationOnly ? SetExactBox(box, localStart, localEnd) : SetExactBoxRotated(box, start, end, inv);
        else localBox = inv.IsTranslationOnly ? box : box.Transform(inv.RotationOnly());

        for (int i = 0; i < localBrushes.Count; i++)
        {
            Brush mb = localBrushes[i];
            if (mb is null) continue;

            // TraceBrushVsBrush only overwrites the impact plane when it records a strictly closer hit, so a
            // drop in Fraction across the call means this brush produced the new closest impact and its
            // (local-space) plane now lives in trace — transform it back to world space (DP transforms the
            // plane by 'matrix' after the local trace).
            float prevFrac = trace.Fraction;

            if (mb.IsTriangle && box.Sides.Length == 0)
            {
                // a point against a curved panel of the brush model: a line from its front, or nothing
                // (the point's place in the model is the entity's inverse of start + its offset)
                ClipLineToTriangle(ref trace, inv.TransformPoint(start + box.Points[0]), inv.TransformPoint(end + box.Points[0]), mb, hitEnt);
            }
            else if (exactBox)
                TraceBrushVsBrushExact(ref trace, localBox, mb, hitEnt);
            else if (DarkPlacesArithmetic)
                TraceLineVsBrushExact(ref trace, inv.TransformPoint(start + box.Points[0]), inv.TransformPoint(end + box.Points[0]), mb, hitEnt);
            else
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
    // Collision triangles and mesh models — port of the BIH_COLLISIONTRIANGLE / BIH_RENDERTRIANGLE cases of
    // Mod_CollisionBIH_TraceLineShared / Mod_CollisionBIH_TraceBrush / Mod_CollisionBIH_TracePoint
    // (model_brush.c) and of Collision_ClipToGenericEntity / Collision_ClipLineToGenericEntity
    // (collision.c) for a model whose TraceBox is Mod_CollisionBIH_TraceBox.
    // =============================================================================================

    /// <summary>
    /// A point moving from <paramref name="lineStart"/> to <paramref name="lineEnd"/> against one collision
    /// triangle (a <see cref="Brush.IsTriangle"/> brush, in the same space as the line):
    /// Collision_TraceLineTriangleFloat, which stops a line that crosses the triangle from its front and lets
    /// one from behind through. A point that does not move meets nothing.
    /// </summary>
    private static void ClipLineToTriangle(ref SweepState trace, Vector3 lineStart, Vector3 lineEnd, Brush triangle, Entity? hitEnt)
    {
        if (lineStart == lineEnd || triangle.Sides.Length == 0) return;
        // "skip if this trace should not be blocked by these contents"
        if ((trace.HitMask & triangle.Contents) == 0) return;
        var line = new DarkPlacesPatchCollision.LineHit { Fraction = trace.Fraction };
        if (!DarkPlacesPatchCollision.TraceLineTriangle(ref line, lineStart, lineEnd, triangle.Points[0], triangle.Points[1], triangle.Points[2]))
            return;
        // DP stores the nudged fraction as it is - it is below zero for a line that starts within the nudge
        // distance of the triangle - and clamps when the trace is finished (BuildResult here).
        trace.Fraction = line.Fraction;
        trace.PlaneNormal = line.PlaneNormal;
        trace.PlaneDist = line.PlaneDist;
        trace.HitContents = triangle.Contents;
        trace.HitSurfaceFlags = triangle.SurfaceFlags;
        trace.HitTexture = triangle.Texture;
        trace.Ent = hitEnt;
        trace.Hit = true;
    }

    /// <summary>
    /// Clip the move against a mesh-model entity. As in <see cref="ClipToBrushModel"/> the move is carried
    /// into the entity's space and the impact plane back out; what is tested there is each triangle the
    /// model's hierarchy puts near the move, in DP's order: for a box, the brush
    /// Collision_TraceBrushTriangleFloat builds from it (points snapped to 1/32); for a moving point, the
    /// triangle itself, unsnapped and from its front only; for a point at rest, nothing.
    /// </summary>
    private void ClipToMesh(ref SweepState trace, Brush box, Vector3 start, Vector3 end, CollisionMesh mesh, EntityMatrix toWorld, Entity hitEnt)
    {
        EntityMatrix inv = toWorld.Inverted();
        _meshLeaves.Clear();

        if (box.Sides.Length == 0)
        {
            // Mod_CollisionBIH_TracePoint_Mesh: a mesh has no volume for a point to be in
            if (start == end) return;
            Vector3 lineStart = inv.TransformPoint(start + box.Points[0]), lineEnd = inv.TransformPoint(end + box.Points[0]);
            mesh.Bih.QuerySwept(_meshWalker, lineStart, lineEnd, Vector3.Zero, Vector3.Zero, _meshLeaves);
            for (int i = 0; i < _meshLeaves.Count; i++)
            {
                CollisionMesh.Surface surface = mesh.Triangle(_meshLeaves[i], out Vector3 v0, out Vector3 v1, out Vector3 v2);
                if ((trace.HitMask & surface.Contents) == 0) continue;
                var line = new DarkPlacesPatchCollision.LineHit { Fraction = trace.Fraction };
                if (!DarkPlacesPatchCollision.TraceLineTriangle(ref line, lineStart, lineEnd, v0, v1, v2)) continue;
                trace.Fraction = line.Fraction;
                // "transform plane" back to the world
                (trace.PlaneNormal, trace.PlaneDist) = toWorld.TransformPositivePlane(line.PlaneNormal, line.PlaneDist);
                trace.HitContents = surface.Contents;
                trace.HitSurfaceFlags = surface.SurfaceFlags;
                trace.HitTexture = surface.Texture;
                trace.Ent = hitEnt;
                trace.Hit = true;
            }
            return;
        }

        Vector3 localStart = inv.TransformPoint(start), localEnd = inv.TransformPoint(end);
        // "we get here if TraceBrush exists, AND we have a rotation component": the box is turned into the
        // model's space and swept as a brush; otherwise it stays the axis-aligned box it was.
        bool exact = DarkPlacesArithmetic;
        Brush localBox;
        if (exact && !inv.IsTranslationOnly)
        {
            localBox = SetExactBoxRotated(box, start, end, inv);
            // Mod_CollisionBIH_TraceBrush, "calculate tracebox-like parameters for efficient culling", from the
            // bounds of the turned box at each end of the move
            Bounds(_exactStart, out Vector3 startMins, out Vector3 startMaxs);
            Bounds(_exactEnd, out Vector3 endMins, out Vector3 endMaxs);
            Vector3 walkStart = (startMins + startMaxs) * 0.5f, walkEnd = (endMins + endMaxs) * 0.5f;
            mesh.Bih.QuerySwept(_meshWalker, walkStart, walkEnd, Vector3.Min(startMins - walkStart, endMins - walkEnd), Vector3.Max(startMaxs - walkStart, endMaxs - walkEnd), _meshLeaves);
        }
        else
        {
            localBox = inv.IsTranslationOnly ? box : box.Transform(inv.RotationOnly());
            if (exact) SetExactBox(box, localStart, localEnd);
            // Mod_CollisionBIH_TraceBrush, "calculate tracebox-like parameters for efficient culling"
            Vector3 centre = (localBox.Mins + localBox.Maxs) * 0.5f;
            mesh.Bih.QuerySwept(_meshWalker, localStart + centre, localEnd + centre, localBox.Mins - centre, localBox.Maxs - centre, _meshLeaves);
        }
        for (int i = 0; i < _meshLeaves.Count; i++)
        {
            CollisionMesh.Surface surface = mesh.Triangle(_meshLeaves[i], out Vector3 v0, out Vector3 v1, out Vector3 v2);
            DarkPlacesPatchCollision.RefillTriangle(_meshTriangle, v0, v1, v2, surface.Contents, surface.SurfaceFlags, surface.Texture, _meshTrianglePlanes);
            float prevFrac = trace.Fraction;
            if (exact)
                TraceBrushVsBrushExact(ref trace, localBox, _meshTriangle, hitEnt);
            else
                TraceBrushVsBrush(ref trace, localBox, localStart, localEnd, _meshTriangle, worldBrush: false, hitEnt: hitEnt);
            if (trace.Fraction < prevFrac)
                (trace.PlaneNormal, trace.PlaneDist) = toWorld.TransformPositivePlane(trace.PlaneNormal, trace.PlaneDist);
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
            // "trace->startsupercontents |= other_start->supercontents": before the mask is asked (LastStartContents)
            trace.StartContents |= other.Contents;
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
    // The same sweep in DarkPlaces' own arithmetic (DarkPlacesArithmetic) — Collision_TraceBrushBrushFloat
    // (collision.c:559) line for line, for the case that neither brush turns during the move (the start and
    // end planes of every axis are then the same plane, as in the sweep above). What differs from the sweep
    // above is only which floating-point operations produce each number:
    //   - the box's points are taken at their place in the world (Collision_BrushForBox of start + mins,
    //     start + maxs), at the start and again at the end (SetExactBox), and each set is projected on the axis;
    //   - a dot product is three products summed left to right;
    //   - an edge cross product is normalised by multiplying with 1/sqrt, and one shorter than
    //     COLLISION_EDGECROSS_MINLENGTH2 (1/4194304) is skipped;
    //   - the impact plane is "startplane * (1 - enterfrac) + endplane * enterfrac";
    //   - a brush with hasaabbplanes is tested on its own planes only;
    //   - a brush the box starts in overwrites the trace's plane with the (zero) plane of no impact, as the
    //     C's "VectorCopy(newimpactplane, trace->plane.normal)" does in its started-inside branch.
    // =============================================================================================

    private const float ExactEdgeCrossMinLength2 = 1.0f / 4194304.0f;

    /// <summary>
    /// Collision_CombineTraces (collision.c:1914): DP clips the move against each entity on its own - a fresh
    /// trace that knows nothing of what the world or another entity stopped it at - and then "take[s] the
    /// 'best' answers from the new trace". Two consequences the shared accumulator of the default sweep does
    /// not have: an entity's impact counts only if its plane is not the zero vector (an impact followed, in
    /// the same entity, by a brush the box starts inside has had its plane overwritten with zeros, and is
    /// dropped); and such a start-inside cannot wipe the plane of an impact found elsewhere.
    /// </summary>
    private static void CombineTraces(ref SweepState clip, in SweepState trace, Entity touch)
    {
        if (trace.AllSolid) clip.AllSolid = true;
        if (trace.StartSolid) clip.StartSolid = true;
        // (Mod_Q1BSP_RecursiveHullCheck is the one clip that sets inwater; inopen is the world's alone)
        if (trace.InWater) clip.InWater = true;
        bool nearer = trace.HasExactFraction || clip.HasExactFraction
            ? (trace.HasExactFraction ? trace.ExactFraction : trace.Fraction) < (clip.HasExactFraction ? clip.ExactFraction : clip.Fraction)
            : trace.Fraction < clip.Fraction;
        if (nearer && DotExact(trace.PlaneNormal, trace.PlaneNormal) > 0)
        {
            clip.HasExactFraction = trace.HasExactFraction;
            clip.ExactFraction = trace.ExactFraction;
            clip.Fraction = trace.Fraction;
            clip.PlaneNormal = trace.PlaneNormal;
            clip.PlaneDist = trace.PlaneDist;
            clip.Ent = touch;
            clip.HitContents = trace.HitContents;
            clip.HitSurfaceFlags = trace.HitSurfaceFlags;
            clip.HitTexture = trace.HitTexture;
            clip.Hit = true;
        }
        clip.StartContents |= trace.StartContents;
    }

    // The six planes of Collision_ClipTrace_Box / Collision_ClipTrace_Point, refilled per entity.
    private readonly Brush _cbox = new(new BrushPlane[6], Array.Empty<Vector3>(), Array.Empty<Vector3>(), 0, 0, isAabb: true, texture: null);

    /// <summary>
    /// A box-shaped entity as DP clips against one: Collision_ClipTrace_Box (and Collision_ClipTrace_Point for
    /// a point at rest), in the entity's own space - the move's ends less the entity's origin. The entity's
    /// box is grown by the moving box ("cbox_planes[0].dist = cmaxs[0] - mins[0]" and so on) and the move's
    /// origin is traced through it as a line; so a body of no size is still the size of the mover, and what
    /// is compared are plane distances near zero rather than map coordinates.
    /// </summary>
    private void ClipToEntityBoxExact(ref SweepState trace, Brush box, Vector3 start, Vector3 end, Entity touch)
    {
        // the moving box's mins / maxs: a box brush keeps them as its bounds, a point brush as its point
        Vector3 mins = box.Sides.Length == 0 ? box.Points[0] : box.Mins, maxs = box.Sides.Length == 0 ? box.Points[0] : box.Maxs;
        bool point = box.Sides.Length == 0;
        // SV_TraceBox hands a point over already shifted by its offset, as a box of no size
        Vector3 offset = point ? mins : Vector3.Zero;
        if (point) mins = maxs = Vector3.Zero;
        Vector3 cmins = touch.Mins, cmaxs = touch.Maxs;
        int contents = touch.Solid == Solid.Corpse ? SuperContents.Corpse : SuperContents.Body;
        BrushPlane[] p = _cbox.Sides;
        p[0] = new BrushPlane(new Vector3(1, 0, 0), cmaxs.X - mins.X, 0, contents, null);
        p[1] = new BrushPlane(new Vector3(-1, 0, 0), maxs.X - cmins.X, 0, contents, null);
        p[2] = new BrushPlane(new Vector3(0, 1, 0), cmaxs.Y - mins.Y, 0, contents, null);
        p[3] = new BrushPlane(new Vector3(0, -1, 0), maxs.Y - cmins.Y, 0, contents, null);
        p[4] = new BrushPlane(new Vector3(0, 0, 1), cmaxs.Z - mins.Z, 0, contents, null);
        p[5] = new BrushPlane(new Vector3(0, 0, -1), maxs.Z - cmins.Z, 0, contents, null);
        _cbox.Contents = contents;
        // Matrix4x4_Transform by the inverse of a translation
        Vector3 localStart = start + offset - touch.Origin, localEnd = end + offset - touch.Origin;
        float prevFrac = trace.Fraction;
        if (point && start == end) TracePointVsBrushExact(ref trace, localStart, _cbox);
        else TraceLineVsBrushExact(ref trace, localStart, localEnd, _cbox, touch);
        // "transform plane": by a translation, the normal stays and the distance moves with the origin
        if (trace.Fraction < prevFrac) trace.PlaneDist += DotExact(trace.PlaneNormal, touch.Origin);
    }

    /// <summary>Collision_TraceLineBrushFloat (collision.c:760): a line against a brush, by its planes alone.</summary>
    private static void TraceLineVsBrushExact(ref SweepState trace, Vector3 lineStart, Vector3 lineEnd, Brush other, Entity? hitEnt)
    {
        if (lineStart == lineEnd)
        {
            // Mod_CollisionBIH_TraceLine: "if (VectorCompare(start, end))" it is Mod_CollisionBIH_TracePoint
            TracePointVsBrushExact(ref trace, lineStart, other);
            return;
        }
        BrushPlane[] planes = other.Sides;
        float enterfrac = -1, leavefrac = 1, enterfrac2 = -1;
        Vector3 newImpactNormal = Vector3.Zero;
        float newImpactDist = 0;
        int hitSurfaceFlags = 0;
        string? hitTexture = null;
        for (int nplane = 0; nplane < planes.Length; nplane++)
        {
            Vector3 plane = planes[nplane].Normal;
            float planeDist = planes[nplane].Dist;
            float startdist = DotExact(lineStart, plane) - planeDist, enddist = DotExact(lineEnd, plane) - planeDist;
            if (startdist > enddist)
            {
                // moving into brush
                if (enddist > 0.0f) return;
                if (startdist > 0)
                {
                    // enter
                    float imove = 1 / (startdist - enddist);
                    float f = startdist * imove;
                    if (enterfrac < f)
                    {
                        enterfrac = f;
                        if (enterfrac > leavefrac) return;
                        enterfrac2 = (startdist - Collision.ImpactNudge) * imove;
                        if (enterfrac2 >= trace.Fraction) return;
                        float ie = 1.0f - enterfrac;
                        newImpactNormal = new Vector3(plane.X * ie + plane.X * enterfrac, plane.Y * ie + plane.Y * enterfrac, plane.Z * ie + plane.Z * enterfrac);
                        newImpactDist = planeDist * ie + planeDist * enterfrac;
                        hitSurfaceFlags = planes[nplane].SurfaceFlags;
                        hitTexture = planes[nplane].Texture;
                    }
                }
            }
            else
            {
                // moving out of brush
                if (startdist > 0) return;
                if (enddist > 0)
                {
                    // leave
                    float f = startdist / (startdist - enddist);
                    if (leavefrac > f)
                    {
                        leavefrac = f;
                        if (enterfrac > leavefrac) return;
                    }
                }
            }
        }
        if (enterfrac > -1)
        {
            // started outside, and overlaps, therefore there is a collision here
            if ((trace.HitMask & other.Contents) != 0)
            {
                trace.HitContents = other.Contents;
                trace.HitSurfaceFlags = hitSurfaceFlags;
                trace.HitTexture = hitTexture;
                trace.Fraction = Clamp01(enterfrac2);
                trace.PlaneNormal = newImpactNormal;
                trace.PlaneDist = newImpactDist;
                trace.Ent = hitEnt;
                trace.Hit = true;
            }
        }
        else
        {
            // started inside, update startsolid and friends
            trace.StartContents |= other.Contents;
            if ((trace.HitMask & other.Contents) != 0)
            {
                trace.StartSolid = true;
                if (leavefrac < 1) trace.AllSolid = true;
                trace.PlaneNormal = newImpactNormal;
                trace.PlaneDist = newImpactDist;
            }
        }
    }

    /// <summary>Collision_TracePointBrushFloat (collision.c:923): a point at rest is inside a brush it is behind
    /// every plane of, and is then both startsolid and allsolid.</summary>
    private static void TracePointVsBrushExact(ref SweepState trace, Vector3 point, Brush other)
    {
        BrushPlane[] planes = other.Sides;
        for (int nplane = 0; nplane < planes.Length; nplane++)
            if (DotExact(point, planes[nplane].Normal) - planes[nplane].Dist > 0) return;
        trace.StartContents |= other.Contents;
        if ((trace.HitMask & other.Contents) != 0)
        {
            trace.StartSolid = true;
            trace.AllSolid = true;
            trace.PlaneNormal = Vector3.Zero;
            trace.PlaneDist = 0;
        }
    }

    private static float DotExact(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    // The moving box's eight points at the start and at the end of the move, in the space the sweep runs in
    // (trace_start->points / trace_end->points). Filled by SetExactBox / SetExactBoxRotated before a run of
    // TraceBrushVsBrushExact calls against the brushes of one space.
    private readonly Vector3[] _exactStart = new Vector3[8], _exactEnd = new Vector3[8];
    private readonly Brush _exactRotatedBox = new(new BrushPlane[6], new Vector3[8], new Vector3[3], 0, 0, isAabb: false, texture: null);

    // Collision_BrushForBox(start + mins, start + maxs) and the same at the end: the box where it is.
    private Brush SetExactBox(Brush box, Vector3 start, Vector3 end)
    {
        Vector3[] points = box.Points;
        for (int i = 0; i < 8; i++)
        {
            _exactStart[i] = points[i] + start;
            _exactEnd[i] = points[i] + end;
        }
        return box;
    }

    /// <summary>
    /// The box against a turned entity, as Collision_ClipToGenericEntity makes it ("we get here if TraceBrush
    /// exists, AND we have a rotation component"): Collision_BrushForBox(mins, maxs), Collision_TranslateBrush
    /// to each end of the move, Collision_TransformBrush by the entity's inverse matrix. The result is not
    /// axis-aligned. Its edge directions are transformed as <em>points</em> - Collision_TransformBrush runs
    /// them through Matrix4x4_Transform, translation and all - so they are not the box's edges at all, and
    /// the edge cross products the sweep goes on to test are not the axes a correct test would use. That is
    /// what a DarkPlaces server computes against every turned crate and door, so it is what this returns.
    /// </summary>
    private Brush SetExactBoxRotated(Brush box, Vector3 start, Vector3 end, in EntityMatrix inv)
    {
        Vector3[] points = box.Points;
        for (int i = 0; i < 8; i++)
        {
            _exactStart[i] = inv.TransformPoint(points[i] + start);
            _exactEnd[i] = inv.TransformPoint(points[i] + end);
        }
        Brush turned = _exactRotatedBox;
        for (int i = 0; i < 6; i++)
        {
            // Matrix4x4_TransformPositivePlane: the normal turned (the distance is never read: the sweep
            // measures the box by its points)
            Vector3 n = box.Sides[i].Normal;
            turned.Sides[i] = new BrushPlane(inv.TransformDirection(n), 0, box.Sides[i].SurfaceFlags, box.Sides[i].Contents, box.Sides[i].Texture);
        }
        for (int i = 0; i < 3; i++) turned.EdgeDirs[i] = inv.TransformPoint(box.EdgeDirs[i]);
        for (int i = 0; i < 8; i++) turned.Points[i] = _exactStart[i];
        Bounds(_exactStart, out turned.Mins, out turned.Maxs);
        turned.IsAabb = false;
        return turned;
    }

    private static void Bounds(Vector3[] points, out Vector3 mins, out Vector3 maxs)
    {
        mins = maxs = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            mins = Vector3.Min(mins, points[i]);
            maxs = Vector3.Max(maxs, points[i]);
        }
    }

    private void TraceBrushVsBrushExact(ref SweepState trace, Brush box, Brush other, Entity? hitEnt)
    {
        Vector3[] startPoints = _exactStart, endPoints = _exactEnd, otherPoints = other.Points;
        if (otherPoints.Length == 0) return;
        int traceEdgeDirs = box.EdgeDirs.Length;
        int numplanes1 = other.Sides.Length;
        int numplanes2 = numplanes1 + box.Sides.Length;
        int numplanes3 = numplanes2 + traceEdgeDirs * other.EdgeDirs.Length * 2;
        float enterfrac = -1, leavefrac = 1, enterfrac2 = -1;
        Vector3 newImpactNormal = Vector3.Zero;
        float newImpactDist = 0;
        int hitSurfaceFlags = 0;
        string? hitTexture = null;

        // fast case for AABB vs compiled brushes
        if (box.IsAabb && (other.IsAabb || other.HasAabbPlanes))
            numplanes3 = numplanes2 = numplanes1;

        for (int nplane = 0; nplane < numplanes3; nplane++)
        {
            Vector3 plane;
            if (nplane < numplanes1) plane = other.Sides[nplane].Normal;
            else if (nplane < numplanes2) plane = box.Sides[nplane - numplanes1].Normal;
            else
            {
                // pick an edgedir from each brush and cross them
                int nplane2 = nplane - numplanes2;
                int nedge1 = nplane2 >> 1;
                int nedge2 = nedge1 / traceEdgeDirs;
                nedge1 -= nedge2 * traceEdgeDirs;
                Vector3 a = (nplane2 & 1) != 0 ? box.EdgeDirs[nedge1] : other.EdgeDirs[nedge2];
                Vector3 b = (nplane2 & 1) != 0 ? other.EdgeDirs[nedge2] : box.EdgeDirs[nedge1];
                // CrossProduct
                plane = new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
                float length2 = DotExact(plane, plane);
                if (length2 < ExactEdgeCrossMinLength2) continue;   // degenerate crossproducts
                // VectorNormalize: "ilength = 1.0f / sqrt(ilength)", the division in double
                float ilength = (float)(1.0 / Math.Sqrt(length2));
                plane = new Vector3(plane.X * ilength, plane.Y * ilength, plane.Z * ilength);
            }

            // furthestplanedist_float(startplane, other_start->points)
            float planeDist = DotExact(otherPoints[0], plane);
            for (int i = 1; i < otherPoints.Length; i++)
            {
                float d = DotExact(otherPoints[i], plane);
                if (planeDist < d) planeDist = d;
            }
            // nearestplanedist_float(startplane, trace_start->points) and (endplane, trace_end->points)
            float startNearest = DotExact(startPoints[0], plane), endNearest = DotExact(endPoints[0], plane);
            for (int i = 1; i < startPoints.Length; i++)
            {
                float ds = DotExact(startPoints[i], plane), de = DotExact(endPoints[i], plane);
                if (startNearest > ds) startNearest = ds;
                if (endNearest > de) endNearest = de;
            }
            float startdist = startNearest - planeDist, enddist = endNearest - planeDist;

            if (startdist > enddist)
            {
                // moving into brush
                if (enddist > 0.0f) return;
                if (startdist >= 0)
                {
                    // enter
                    float imove = 1 / (startdist - enddist);
                    float f = startdist * imove;
                    // check if this will reduce the collision time range
                    if (enterfrac < f)
                    {
                        enterfrac = f;
                        // if the collision time range is now empty, no collision
                        if (enterfrac > leavefrac) return;
                        // calculate the nudged fraction and impact normal we'll need if we accept this collision later
                        enterfrac2 = (startdist - Collision.ImpactNudge) * imove;
                        // if the collision would be further away than the trace's existing collision data, we don't care about this collision
                        if (enterfrac2 >= trace.Fraction) return;
                        float ie = 1.0f - enterfrac;
                        newImpactNormal = new Vector3(plane.X * ie + plane.X * enterfrac, plane.Y * ie + plane.Y * enterfrac, plane.Z * ie + plane.Z * enterfrac);
                        newImpactDist = planeDist * ie + planeDist * enterfrac;
                        if (nplane < numplanes1)
                        {
                            // use the plane from other
                            hitSurfaceFlags = other.Sides[nplane].SurfaceFlags;
                            hitTexture = other.Sides[nplane].Texture;
                        }
                        else if (nplane < numplanes2)
                        {
                            // use the plane from trace
                            hitSurfaceFlags = box.Sides[nplane - numplanes1].SurfaceFlags;
                            hitTexture = box.Sides[nplane - numplanes1].Texture;
                        }
                        else
                        {
                            hitSurfaceFlags = other.SurfaceFlags;
                            hitTexture = other.Texture;
                        }
                    }
                }
            }
            else
            {
                // moving out of brush
                if (startdist >= 0) return;
                if (enddist > 0)
                {
                    // leave
                    float f = startdist / (startdist - enddist);
                    // check if this will reduce the collision time range
                    if (leavefrac > f)
                    {
                        leavefrac = f;
                        // if the collision time range is now empty, no collision
                        if (enterfrac > leavefrac) return;
                    }
                }
            }
        }

        // at this point we know the trace overlaps the brush because it was not rejected at any point in the loop above
        if (enterfrac > -1)
        {
            // started outside, and overlaps, therefore there is a collision here
            if ((trace.HitMask & other.Contents) != 0)
            {
                trace.HitContents = other.Contents;
                trace.HitSurfaceFlags = hitSurfaceFlags;
                trace.HitTexture = hitTexture;
                trace.Fraction = Clamp01(enterfrac2);
                trace.PlaneNormal = newImpactNormal;
                trace.PlaneDist = newImpactDist;
                trace.Ent = hitEnt;
                trace.Hit = true;
            }
        }
        else
        {
            // started inside, update startsolid and friends
            trace.StartContents |= other.Contents;
            if ((trace.HitMask & other.Contents) != 0)
            {
                trace.StartSolid = true;
                if (leavefrac < 1) trace.AllSolid = true;
                trace.PlaneNormal = newImpactNormal;
                trace.PlaneDist = newImpactDist;
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
        // (A line that hit a collision triangle from within the nudge distance carries a fraction below 0
        // to here, as DP's does to Collision_ClipExtendFinish's "clamp things". Every other fraction is
        // already in 0..1, so for a world without triangles this clamp changes no bit.)
        float fraction = s.Fraction < 0f ? 0f : s.Fraction;
        r.Fraction = fraction;
        r.EndPos = start + (end - start) * fraction;
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
        if (s.HullWorld)
        {
            // a Quake 1 format map says both itself, and names what the last leaf held even after a miss
            r.InOpen = s.InOpen;
            r.InWater = s.InWater;
            if (!s.Hit)
            {
                r.DpHitContents = s.HitContents;
                r.DpHitQ3SurfaceFlags = s.HitSurfaceFlags;
                r.DpHitTextureName = s.HitTexture;
            }
        }
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
        public int StartContents;
        // Quake 1 format maps (Q1HullClip): the fraction as the hull code computed it, and what only it reports
        public double ExactFraction;
        public bool HasExactFraction;
        public bool HullWorld, InOpen, InWater;
    }

    /// <summary>The two places a move meets clipping hulls: Collision_ClipToWorld and Collision_ClipToGenericEntity
    /// with a model whose TraceBox is Mod_Q1BSP_TraceBox. (Nested for access to <see cref="SweepState"/>.)</summary>
    private static class Q1HullClip
    {
        public static void World(ref SweepState trace, Q1HullCollision hulls, Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end)
        {
            hulls.TraceBox(0, start, mins, maxs, end, trace.HitMask, out Q1HullTrace hit);
            trace.HullWorld = true;
            trace.InOpen = hit.InOpen;
            trace.InWater = hit.InWater;
            trace.StartContents |= hit.StartContents;
            if (hit.StartSolid) trace.StartSolid = true;
            if (hit.AllSolid) trace.AllSolid = true;
            // the world's answer is the trace the entities are then combined into
            trace.HitContents = hit.HitContents;
            trace.HitSurfaceFlags = hit.HitSurfaceFlags;
            trace.HitTexture = hit.HitTexture;
            if (hit.Fraction < trace.Fraction)
            {
                trace.ExactFraction = hit.Fraction;
                trace.HasExactFraction = true;
                trace.Fraction = (float)hit.Fraction;
                trace.PlaneNormal = hit.PlaneNormal;
                trace.PlaneDist = hit.PlaneDist;
                trace.Hit = true;
                trace.Ent = null;
            }
        }

        public static void Entity(ref SweepState own, Q1HullCollision hulls, int model, EntityMatrix toWorld, Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, Entity touch)
        {
            // "this is only approximate if rotated, quite useless": the move's ends go into the model's space, the box does not turn
            EntityMatrix inv = toWorld.Inverted();
            hulls.TraceBox(model, inv.TransformPoint(start), mins, maxs, inv.TransformPoint(end), own.HitMask, out Q1HullTrace hit);
            own.StartSolid = hit.StartSolid;
            own.AllSolid = hit.AllSolid;
            own.InWater = hit.InWater;
            own.StartContents = hit.StartContents;
            own.ExactFraction = hit.Fraction;
            own.HasExactFraction = true;
            own.Fraction = (float)hit.Fraction;
            own.HitContents = hit.HitContents;
            own.HitSurfaceFlags = hit.HitSurfaceFlags;
            own.HitTexture = hit.HitTexture;
            own.Ent = touch;
            // "transform plane" (a miss leaves the zero normal, which CombineTraces never takes)
            if (hit.PlaneNormal != Vector3.Zero)
            {
                (Vector3 wn, float wd) = toWorld.TransformPositivePlane(hit.PlaneNormal, hit.PlaneDist);
                own.PlaneNormal = wn;
                own.PlaneDist = wd;
                own.Hit = hit.Fraction < 1;
            }
        }
    }
}

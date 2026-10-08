// Port of Base/darkplaces/sv_phys.c SV_TraceBox / SV_TraceLine / SV_TracePoint (the entity loop and
// its pass rules), SV_PointSuperContents, SV_EntitiesInBox; world.c World_SetSize, World_LinkEdict,
// World_UnlinkEdict, World_UnlinkAll, World_EntitiesInBox (the area grid); collision.c
// Collision_CombineTraces (which entity a trace names); model_brush.c Mod_Q3BSP_Load (the yaw and
// rotated bounds of a brush model) and Mod_BSP_GetPVS / Mod_BSP_BoxTouchingPVS.
// The sweep itself (Collision_TraceBrushBrushFloat, Collision_ClipToGenericEntity), what a box, a
// line and a point are each tested against (Mod_CollisionBIH_TraceBox / TraceLine / TracePoint), the
// map's curved surfaces as DarkPlaces' collision triangles, the hierarchy that orders the leaves and
// the lengthened trace (Collision_ClipExtendPrepare / Finish) are VortexArena.Engine.Collision -
// TraceService on a world built with BspCollisionOptions.DarkPlaces - which this drives. It is the
// same library, built the same way, that the client half's BspLegacyWorld drives; the native game
// uses it with its own defaults.
using System.Buffers;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

/// <summary>A server trace (trace_t), in DarkPlaces' SUPERCONTENTS numbering.</summary>
public struct SvTrace
{
    public bool AllSolid, StartSolid;
    /// <summary>The move started inside the world's own brushes / inside any brush model (world or SOLID_BSP entity).</summary>
    public bool WorldStartSolid, BModelStartSolid;
    public float Fraction;
    public QcVector EndPos, PlaneNormal;
    public float PlaneDist;
    /// <summary>The entity hit: -1 for none, 0 for the world. DarkPlaces' trace.ent is NULL or an edict,
    /// and the world edict is not NULL - "hit the world" and "hit nothing" are different answers that
    /// the physics code tells apart (a rocket's touch function runs for the first, not the second).</summary>
    public int Ent;
    public int StartContents, HitContents, HitQ3SurfaceFlags;
    public string? HitTextureName;
}

/// <summary>The three boxes of a model that SV_LinkEdict and SV_PushMove choose between.</summary>
public readonly record struct SvModelBounds(QcVector NormalMins, QcVector NormalMaxs, QcVector YawMins, QcVector YawMaxs, QcVector RotatedMins, QcVector RotatedMaxs)
{
    /// <summary>Mod_Q3BSP_Load / Mod_Alias "model radius computations": the box that holds the model at
    /// any yaw, and the cube that holds it at any orientation.</summary>
    public static SvModelBounds FromNormal(QcVector mins, QcVector maxs)
    {
        float cx = MathF.Max(MathF.Abs(mins.X), MathF.Abs(maxs.X)), cy = MathF.Max(MathF.Abs(mins.Y), MathF.Abs(maxs.Y));
        float cz = MathF.Max(MathF.Abs(mins.Z), MathF.Abs(maxs.Z));
        float yaw = MathF.Sqrt(cx * cx + cy * cy), radius = MathF.Sqrt(cx * cx + cy * cy + cz * cz);
        return new SvModelBounds(mins, maxs, new QcVector(-yaw, -yaw, mins.Z), new QcVector(yaw, yaw, maxs.Z),
            new QcVector(-radius, -radius, -radius), new QcVector(radius, radius, radius));
    }
}

/// <summary>
/// The server's collision world: the level's BSP from the game data, its brushes in the port's
/// collision library, the area grid every solid entity is linked into, and the server program's
/// entities clipped the way SV_TraceBox clips them.
///
/// Everything here is driven by a QuakeC program that is trusted no further than its VM: entity
/// numbers, model indices and vectors are checked or made harmless before use, one trace's
/// candidate list is bounded, and a missing or malformed map leaves an empty world.
/// </summary>
/// <remarks>
/// Knowing deviations from DarkPlaces, all about what is clipped rather than how:
/// <list type="bullet">
/// <item>A SOLID_BSP entity whose model is not a map submodel, and any entity under MOVE_HITMODEL,
/// is clipped against its model's triangles as DarkPlaces clips it - at the model's first frame,
/// whatever frame the entity shows, and only for MD3 and IQM models (<see cref="SvModelCollision"/>);
/// one of another format, or refused by that class's bounds, is clipped as its box.</item>
/// <item>startdepth / startdepthnormal (<see cref="StartDepth"/>) are measured on the faces of each
/// brush and of the box; DarkPlaces also tries the cross products of their edges. Against a rotated
/// brush-model or mesh-model entity no depth is measured.</item>
/// </list>
/// </remarks>
public sealed class SvWorld : TraceService.IEntityProvider
{
    // bspfile.h SUPERCONTENTS_*, as the program sees them.
    public const int ContentsSolid = 0x1, ContentsWater = 0x2, ContentsSlime = 0x4, ContentsLava = 0x8, ContentsSky = 0x10,
        ContentsBody = 0x20, ContentsCorpse = 0x40, ContentsNoDrop = 0x80, ContentsPlayerClip = 0x100,
        ContentsMonsterClip = 0x200, ContentsDoNotEnter = 0x400, ContentsBotClip = 0x800, ContentsOpaque = 0x1000;
    public const int ContentsLiquidsMask = ContentsWater | ContentsSlime | ContentsLava;

    // server.h
    public const int MoveNormal = 0, MoveNoMonsters = 1, MoveMissile = 2, MoveWorldOnly = 3, MoveHitModel = 4;
    private const float SolidBBox = 2, SolidBsp = 4;
    private const int FlMonster = 32;

    /// <summary>The most entities one trace clips against, or one box query returns to the engine's
    /// own loops (triggers touched, entities a pusher moves). A move that sweeps more solid entities
    /// than this is not a real one.</summary>
    public const int MaxTouchedEdicts = 4096;

    private readonly VirtualFileSystem _files;
    private QcVm? _vm;
    private SvFieldOffsets? _f;
    private Func<int, string?>? _modelNameOf;

    // The map as DarkPlaces collides with it: brushes and patch triangles in one world, found through
    // its bounding interval hierarchy. The trace library tests a box against both, a line against the
    // brushes and the front of the triangles, a point against the brushes alone.
    private CollisionWorld? _collision;
    private readonly List<Brush> _depthBrushes = new();
    private TraceService? _worldTrace;    // the world
    private TraceService? _trace;         // the world and the program's entities
    private TraceService? _entityTrace;   // the entities alone, over an empty world
    private readonly SvModelCollision _models;
    private readonly CollisionBih.Walker _depthWalker = new();
    private readonly List<int> _depthLeaves = new();
    private readonly Brush _depthTriangle = DarkPlacesPatchCollision.NewScratchTriangle();
    /// <summary>The static collision world (brushes and patch triangles), for a tool that asks it questions of its own.</summary>
    public CollisionWorld? Collision => _collision;
    /// <summary>Patch collision triangles in the world model (BIH_COLLISIONTRIANGLE leaves).</summary>
    public int PatchTriangles { get; private set; }
    private BspPvs? _pvs;
    private Vector3 _worldMins, _worldMaxs;
    private readonly Dictionary<string, BspCollisionBuilder.Submodel> _submodels = new(StringComparer.Ordinal);

    // The area grid of world.c: AREA_GRID cells a side over the map's XY extent, each listing the
    // entities whose box touches it, and one list for entities too large or too far out to grid.
    private const int Grid = 128;                 // AREA_GRID
    private const int MaxCellsPerEdict = 16 * 16;
    private readonly List<int>?[] _cells = new List<int>?[Grid * Grid];
    private readonly List<int> _outside = new();
    private float _gridBiasX, _gridBiasY, _gridScaleX = 1, _gridScaleY = 1;
    private LinkedEdict[] _links = new LinkedEdict[1024];
    private int[] _marks = new int[1024];
    private int _markNumber;

    private struct LinkedEdict
    {
        public bool Linked, Outside;
        public int X0, Y0, X1, Y1; // cell range, max exclusive
        public Vector3 AreaMins, AreaMaxs;
    }

    // One trace's candidates, mirrored into the collision library's Entity objects for the length of
    // the call; they are reused, never kept.
    private readonly List<Entity> _mirrors = new();
    private readonly Entity _passMirror = new();
    private readonly int[] _touched = new int[MaxTouchedEdicts];
    private int _passEdict, _passOwner, _passClipGroup, _onlyEdict;
    private MonsterFilter _monsters;
    private int _traceDepth;

    private enum MonsterFilter { All, Without, Only }

    public SvWorld(VirtualFileSystem files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _models = new SvModelCollision(files);
    }

    /// <summary>The collision meshes of the level's non-brush models (crates, barrels), loaded as traces meet them.</summary>
    public SvModelCollision ModelCollision => _models;

    /// <summary>The map that is loaded ("maps/x.bsp"), or null.</summary>
    public string? MapName { get; private set; }
    public BspData? Bsp { get; private set; }
    public string? LoadError { get; private set; }
    /// <summary>sv.worldmodel->brush.entities: the map's entity lump as text.</summary>
    public string EntitiesText => Bsp?.EntitiesText ?? "";
    /// <summary>sv.worldmodel->brush.numsubmodels: the world itself plus its "*N" models.</summary>
    public int NumSubmodels => Bsp?.Models.Length ?? 0;
    public long CandidateOverflows { get; private set; }
    public long Traces { get; private set; }
    /// <summary>sv_areagrid_link_SOLID_NOT (DarkPlaces default 1; Xonotic sets 0): whether non-solid
    /// entities are kept in the area grid, and so whether findradius can see them.</summary>
    public bool LinkSolidNot { get; set; } = true;

    /// <summary>SV_SpawnServer's Mod_ForName of the map: reads it and builds its collision. False,
    /// leaving an empty world, if the file is missing or not a map this reader understands.</summary>
    public bool LoadMap(string worldModel)
    {
        Unload();
        if (string.IsNullOrEmpty(worldModel) || !LegacyQcHost.IsSafePath(worldModel) || !_files.Exists(worldModel))
        {
            LoadError = $"map \"{worldModel}\" is not in the game data";
            return false;
        }
        try
        {
            byte[] file = _files.ReadBytes(worldModel);
            BspData bsp = BspReader.Read(file);
            // Brushes, the curved surfaces as DarkPlaces' own collision triangles, and its hierarchy over both.
            BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp, null, BspCollisionOptions.DarkPlaces(file));
            Bsp = bsp;
            foreach (BspCollisionBuilder.Submodel submodel in built.Submodels) _submodels[submodel.Name] = submodel;
            _pvs = new BspPvs(bsp);
            if (built.DarkPlacesWorldBounds is { } worldBounds)
            {
                // normalmins / normalmaxs of the world model: the model lump's box, enlarged to hold what the model draws
                _worldMins = worldBounds.Mins;
                _worldMaxs = worldBounds.Maxs;
            }
            else if (bsp.Models.Length > 0)
            {
                _worldMins = bsp.Models[0].Mins;
                _worldMaxs = bsp.Models[0].Maxs;
            }
            else
            {
                _worldMins = built.World.WorldMins;
                _worldMaxs = built.World.WorldMaxs;
            }
            PatchTriangles = built.PatchTriangles;
            _collision = built.World;
            _worldTrace = new TraceService(built.World) { DarkPlacesArithmetic = true };
            _trace = new TraceService(built.World, this) { DarkPlacesArithmetic = true };
            CollisionWorld empty = new();
            empty.BuildGrid();
            _entityTrace = new TraceService(empty, this) { DarkPlacesArithmetic = true };
            MapName = worldModel;
            SetupGrid();
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Unload();
            LoadError = $"map \"{worldModel}\" could not be loaded: {e.Message}";
            return false;
        }
    }

    private void Unload()
    {
        MapName = null;
        Bsp = null;
        LoadError = null;
        _worldTrace = _trace = _entityTrace = null;
        _collision = null;
        _models.Clear();
        PatchTriangles = 0;
        _pvs = null;
        _submodels.Clear();
        _worldMins = _worldMaxs = default;
        SetupGrid();
    }

    /// <summary>The program whose entities are linked and clipped.</summary>
    /// <param name="modelNameOf">sv.model_precache[modelindex] of an entity, or null: which map
    /// submodel a SOLID_BSP entity is clipped as.</param>
    public void Attach(QcVm vm, SvFieldOffsets fields, Func<int, string?> modelNameOf)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _f = fields ?? throw new ArgumentNullException(nameof(fields));
        _modelNameOf = modelNameOf ?? throw new ArgumentNullException(nameof(modelNameOf));
        SetupGrid();
    }

    /// <summary>sv.world.mins / maxs: the world model's normalmins / normalmaxs.</summary>
    public void Bounds(out QcVector mins, out QcVector maxs)
    {
        mins = Q(_worldMins);
        maxs = Q(_worldMaxs);
    }

    /// <summary>The bounds of map submodel "*N" (N at least 1): its normalmins / normalmaxs, which are the
    /// map's model lump enlarged to hold everything the submodel draws.</summary>
    public bool TryGetSubmodelBounds(string name, out QcVector mins, out QcVector maxs)
    {
        mins = maxs = default;
        if (name.Length < 2 || name[0] != '*' || !_submodels.TryGetValue(name, out BspCollisionBuilder.Submodel submodel)) return false;
        mins = Q(submodel.Mins);
        maxs = Q(submodel.Maxs);
        return true;
    }

    // ---- traces ---------------------------------------------------------------------------------------

    /// <summary>
    /// SV_TraceBox (and, for a box of no size, SV_TraceLine / SV_TracePoint): sweep a box from
    /// <paramref name="start"/> to <paramref name="end"/> through the world and the program's entities.
    /// </summary>
    /// <param name="passEdict">The entity the move belongs to (never hit, nor its owner or what it
    /// owns), or 0.</param>
    /// <param name="hitContentsMask">SUPERCONTENTS_* the move stops on (SV_GenericHitSuperContentsMask
    /// of the moving entity, as a rule).</param>
    /// <param name="extend">
    /// collision_extendtracelinelength / collision_extendtraceboxlength (1, the traceline and
    /// tracebox builtins) or collision_extendmovelength (16, the engine's own moves): the sweep is
    /// run this much further than asked, "to ensure detection of collisions within the
    /// collision_impactnudge distance so that short moves do not degrade across frames (this does
    /// not alter the final trace length)". An impact found only in the extra length is not an
    /// impact; one found a hair before the real end is, where an unextended sweep would have missed
    /// its nudged-back fraction. 0 runs the trace as given.
    /// </param>
    public SvTrace Trace(QcVector start, QcVector mins, QcVector maxs, QcVector end, int type, int passEdict, int hitContentsMask, float extend = 0)
    {
        Traces++;
        SvTrace result = new() { Fraction = 1, EndPos = end, Ent = -1 };
        QcVm? vm = _vm;
        if (_trace is null || _worldTrace is null || _entityTrace is null) return result;

        Vector3 vStart = V(start), vEnd = V(end), vMins = V(mins), vMaxs = V(maxs);
        if (!IsFinite(vStart) || !IsFinite(vEnd) || !IsFinite(vMins) || !IsFinite(vMaxs)) return result;

        // SV_TraceBox: "if (VectorCompare(mins, maxs))" the box is a point - shifted by mins - and the
        // trace is SV_TracePoint when it does not move, SV_TraceLine when it does.
        // (The trace library makes the same distinction for what it tests a point against.)
        TraceService service = _trace, worldService = _worldTrace;

        int engineMask = BspLegacyWorld.ContentsToEngine(hitContentsMask);
        // A mask with none of the known bits can stop on nothing.
        if (engineMask == 0) return result;

        // Collision_ClipExtendPrepare: "make the trace longer according to the extend parameter"
        TraceExtension extension = TraceExtension.Prepare(vStart, vEnd, extend);
        Vector3 extendEnd = extension.ExtendEnd;

        // A trace made from inside another trace's entity loop (it cannot happen today: nothing here
        // calls back into the program) would share the mirrors; refuse rather than corrupt them.
        if (_traceDepth != 0) return result;
        _traceDepth++;
        try
        {
            // "if the passedict is world, make it NULL (to avoid two checks each time)"
            _passEdict = vm is not null && passEdict > 0 && passEdict < vm.NumEdicts && !vm.IsFree(passEdict) ? passEdict : 0;
            _passOwner = _passEdict != 0 ? vm!.FieldInt(_passEdict, _f!.Owner) : 0;
            _passClipGroup = _passEdict != 0 ? QcVm.FloatToInt(vm!.FieldFloat(_passEdict, _f!.ClipGroup)) : 0;
            _onlyEdict = 0;
            // The collision library takes the mask from the entity it is told to pass over.
            _passMirror.Index = _passEdict;
            _passMirror.DpHitContentsMask = engineMask;
            _passMirror.Owner = null;

            TraceResult hit;
            int hitEdict = -1, startContents;
            if (type == MoveWorldOnly || vm is null)
            {
                hit = worldService.Trace(vStart, vMins, vMaxs, extendEnd, MoveFilter.WorldOnly, _passMirror);
                result.WorldStartSolid = result.BModelStartSolid = hit.StartSolid;
                startContents = worldService.LastStartContents;
            }
            else if (type == MoveMissile)
            {
                // "size when clipping against monsters": a MOVE_MISSILE box is 15 units larger on every
                // side, but only against FL_MONSTER entities. Two passes: everything but monsters with
                // the box as given, then monsters alone with the larger box.
                _monsters = MonsterFilter.Without;
                hit = service.Trace(vStart, vMins, vMaxs, extendEnd, MoveFilter.Normal, _passMirror);
                hitEdict = EdictOf(hit);
                startContents = service.LastStartContents;
                _monsters = MonsterFilter.Only;
                Vector3 grow = new(15, 15, 15);
                TraceResult monster = _entityTrace.Trace(vStart, vMins - grow, vMaxs + grow, extendEnd, MoveFilter.Normal, _passMirror);
                startContents |= _entityTrace.LastStartContents;
                bool startSolid = hit.StartSolid || monster.StartSolid, allSolid = hit.AllSolid || monster.AllSolid;
                if (monster.Fraction < hit.Fraction && monster.PlaneNormal != Vector3.Zero)
                {
                    hit = monster;
                    hitEdict = EdictOf(monster);
                }
                hit.StartSolid = startSolid;
                hit.AllSolid = allSolid;
            }
            else
            {
                _monsters = MonsterFilter.All;
                MoveFilter filter = type == MoveNoMonsters ? MoveFilter.NoMonsters : type == MoveHitModel ? MoveFilter.HitModel : MoveFilter.Normal;
                hit = service.Trace(vStart, vMins, vMaxs, extendEnd, filter, _passMirror);
                hitEdict = EdictOf(hit);
                startContents = service.LastStartContents;
            }
            // trace.startsupercontents: everything the box starts inside, of any contents
            result.StartContents = BspLegacyWorld.ContentsFromEngine(startContents);

            // Collision_ClipExtendFinish
            bool clearedByExtend = extension.Finish(ref hit);
            float fraction = hit.Fraction;
            if (clearedByExtend) hitEdict = -1;

            if (hit.StartSolid && type != MoveWorldOnly && vm is not null)
            {
                // Collision_ClipToWorld's "worldstartsolid = bmodelstartsolid = startsolid", and
                // Collision_CombineTraces' "if (touchtrace->startsolid) { if (isbmodel)
                // bmodelstartsolid = true; if (cliptrace->fraction == 1) cliptrace->ent = touch; }".
                // The sweep library reports only that the move started solid, not in what, so the
                // rare stuck case asks again: the world alone, then which entity's box holds the start.
                TraceResult world = worldService.Trace(vStart, vMins, vMaxs, vStart, MoveFilter.WorldOnly, _passMirror);
                result.WorldStartSolid = result.BModelStartSolid = world.StartSolid;
                int stuck = StuckEntity(vm, vStart, vMins, vMaxs, type, out bool isBsp);
                if (isBsp) result.BModelStartSolid = true;
                // "if (cliptrace->fraction == 1) cliptrace->ent = touch": a trace that hit nothing names the
                // ENTITY it started in, also when it started in the world as well - the world's clip comes
                // first and an entity's start overwrites it. A player on the ground always starts in the
                // world (its linked box reaches a unit into the floor), and Xonotic's brush triggers ask
                // exactly this (WarpZoneLib_BoxTouchesBrush: tracebox at rest, "if (trace_ent == e)"):
                // with the world named instead, a door's trigger fired for a player in the air and never
                // for one walking through it.
                if (hitEdict < 0) hitEdict = stuck > 0 && fraction >= 1 ? stuck : world.StartSolid ? 0 : stuck;
            }

            result.Fraction = fraction;
            // "calculate the end position"
            result.EndPos = Q(hit.EndPos);
            result.AllSolid = hit.AllSolid;
            result.StartSolid = hit.StartSolid;
            result.PlaneNormal = Q(hit.PlaneNormal);
            result.PlaneDist = hit.PlaneDist;
            // "if (cliptrace.startsolid || cliptrace.fraction < 1) cliptrace.ent = prog->edicts"
            if (hitEdict < 0 && !clearedByExtend && (fraction < 1 || (hit.StartSolid && (type == MoveWorldOnly || vm is null)))) hitEdict = 0;
            if (hitEdict < 0 && clearedByExtend && hit.StartSolid && (type == MoveWorldOnly || vm is null)) hitEdict = 0;
            result.Ent = hitEdict;
            result.HitContents = BspLegacyWorld.ContentsFromEngine(hit.DpHitContents);
            result.HitQ3SurfaceFlags = hit.DpHitQ3SurfaceFlags;
            result.HitTextureName = hit.DpHitTextureName;
            return result;
        }
        finally
        {
            _passMirror.Owner = null;
            _traceDepth--;
        }
    }

    // The first solid entity whose box holds the start box, under the pass rules of the trace that
    // just ran (the mirrors are refilled, so the same filters apply).
    private int StuckEntity(QcVm vm, Vector3 start, Vector3 mins, Vector3 maxs, int type, out bool isBsp)
    {
        isBsp = false;
        _monsters = MonsterFilter.All;
        List<Entity> candidates = new();
        ((TraceService.IEntityProvider)this).EntitiesInBox(start + mins, start + maxs, candidates);
        // The candidates are the shared mirrors, which the traces below refill: take what is needed
        // from them first.
        List<(int Edict, bool Bsp)> stuck = new(candidates.Count);
        foreach (Entity e in candidates)
        {
            if (type == MoveNoMonsters && e.Solid != Solid.Bsp) continue;
            if (ReferenceEquals(e.Owner, _passMirror) || ReferenceEquals(_passMirror.Owner, e)) continue;
            if (!CollisionWorld.BoxesOverlap(start + mins, start + maxs, e.Origin + e.Mins, e.Origin + e.Maxs)) continue;
            stuck.Add((e.Index, e.Solid == Solid.Bsp));
        }
        int found = -1;
        foreach ((int edict, bool bsp) in stuck)
        {
            // A box entity's box is its volume, but a brush model's is not; and a corpse does not
            // stop a move whose mask leaves corpses out. Ask the sweep about this entity alone.
            _onlyEdict = edict;
            TraceResult only = _entityTrace!.Trace(start, mins, maxs, start, MoveFilter.Normal, _passMirror);
            _onlyEdict = 0;
            if (!only.StartSolid) continue;
            if (bsp) isBsp = true;
            if (found < 0) found = edict;
        }
        return found;
    }

    /// <summary>
    /// Whether a box at rest overlaps one entity's solid volume: Collision_ClipToGenericEntity of a
    /// zero-length move against the pusher alone, which is how SV_PushMove asks "is this entity
    /// inside the pusher now".
    /// </summary>
    public bool BoxInsideEntity(int entity, QcVector origin, QcVector mins, QcVector maxs, int hitContentsMask)
    {
        QcVm? vm = _vm;
        if (vm is null || _entityTrace is null || entity <= 0 || entity >= vm.NumEdicts || vm.IsFree(entity) || _traceDepth != 0) return false;
        Vector3 o = V(origin), lo = V(mins), hi = V(maxs);
        if (!IsFinite(o) || !IsFinite(lo) || !IsFinite(hi)) return false;
        int engineMask = BspLegacyWorld.ContentsToEngine(hitContentsMask);
        if (engineMask == 0) return false;
        _traceDepth++;
        try
        {
            _passEdict = _passOwner = _passClipGroup = 0;
            _passMirror.Index = 0;
            _passMirror.DpHitContentsMask = engineMask;
            _passMirror.Owner = null;
            _monsters = MonsterFilter.All;
            _onlyEdict = entity;
            return _entityTrace.Trace(o, lo, hi, o, MoveFilter.Normal, _passMirror).StartSolid;
        }
        finally
        {
            _onlyEdict = 0;
            _traceDepth--;
        }
    }

    /// <summary>
    /// trace_t startdepth / startdepthnormal / worldstartsolid / bmodelstartsolid for a box at rest
    /// (Collision_TraceBrushBrushFloat's "startdepth" bookkeeping and Collision_CombineTraces): how
    /// deep the box sits in the brush it is deepest in, as a distance at most 0, and the direction
    /// that leads out of that brush soonest. For each brush the depth is the largest (least negative)
    /// separation over the candidate axes - the way out that costs least; over all brushes the
    /// smallest of those - the brush that holds the box most firmly.
    /// </summary>
    /// <param name="worldOnly">MOVE_WORLDONLY; otherwise MOVE_NOMONSTERS: the world and SOLID_BSP entities.</param>
    public void StartDepth(QcVector origin, QcVector mins, QcVector maxs, bool worldOnly, int passEdict, int hitContentsMask,
        out float depth, out QcVector normal, out bool worldStartSolid, out bool bmodelStartSolid)
    {
        depth = 0;
        normal = default;
        worldStartSolid = bmodelStartSolid = false;
        QcVm? vm = _vm;
        if (_collision is null) return;
        Vector3 o = V(origin), lo = V(mins), hi = V(maxs);
        if (!IsFinite(o) || !IsFinite(lo) || !IsFinite(hi)) return;
        int engineMask = BspLegacyWorld.ContentsToEngine(hitContentsMask);
        if (engineMask == 0) return;

        Vector3 bestNormal = default;
        float best = 0;
        _depthBrushes.Clear();
        // The world's hierarchy hands the leaves over in DarkPlaces' order, which matters here: of two
        // leaves the box is equally deep in, the first tested is kept.
        _collision.Query(o + lo, o + hi, _depthBrushes);
        foreach (Brush brush in _depthBrushes)
            if ((brush.Contents & engineMask) != 0 && BrushDepth(brush, o, lo, hi, out float d, out Vector3 n))
            {
                worldStartSolid = bmodelStartSolid = true;
                if (d < best) { best = d; bestNormal = n; }
            }

        if (!worldOnly && vm is not null && _f is { } f && _modelNameOf is not null)
        {
            int count = EdictsInBox(o + lo, o + hi, _touched, SolidBsp);
            int owner = passEdict > 0 && passEdict < vm.NumEdicts ? vm.FieldInt(passEdict, f.Owner) : 0;
            for (int i = 0; i < count; i++)
            {
                int edict = _touched[i];
                if (edict == passEdict || vm.FieldFloat(edict, f.Solid) != SolidBsp) continue;
                if (passEdict != 0 && (edict == owner || vm.FieldInt(edict, f.Owner) == passEdict)) continue;
                string? model = _modelNameOf(edict);
                if (model is null) continue;
                QcVector angles = vm.FieldVector(edict, f.Angles);
                if (angles.X != 0 || angles.Y != 0 || angles.Z != 0) continue;
                Vector3 local = o - V(vm.FieldVector(edict, f.Origin));
                if (!_submodels.TryGetValue(model, out BspCollisionBuilder.Submodel submodel))
                {
                    // A mesh model: the triangles its hierarchy puts at the box, each as the brush
                    // Collision_TraceBrushTriangleFloat makes of it. (One with no area has no planes to
                    // measure a depth on and is passed over.)
                    if (_models.Get(model) is not { } mesh) continue;
                    Vector3 centre = (lo + hi) * 0.5f;
                    _depthLeaves.Clear();
                    mesh.Bih.QuerySwept(_depthWalker, local + centre, local + centre, lo - centre, hi - centre, _depthLeaves);
                    foreach (int leaf in _depthLeaves)
                    {
                        CollisionMesh.Surface surface = mesh.Triangle(leaf, out Vector3 v0, out Vector3 v1, out Vector3 v2);
                        if ((surface.Contents & engineMask) == 0) continue;
                        if (!DarkPlacesPatchCollision.RefillTriangle(_depthTriangle, v0, v1, v2, surface.Contents, surface.SurfaceFlags, surface.Texture)) continue;
                        if (BrushDepth(_depthTriangle, local, lo, hi, out float d, out Vector3 n))
                        {
                            bmodelStartSolid = true;
                            if (d < best) { best = d; bestNormal = n; }
                        }
                    }
                    continue;
                }
                foreach (Brush brush in submodel.Brushes)
                    if (brush is not null && (brush.Contents & engineMask) != 0 && BrushDepth(brush, local, lo, hi, out float d, out Vector3 n))
                    {
                        bmodelStartSolid = true;
                        if (d < best) { best = d; bestNormal = n; }
                    }
            }
        }
        depth = best;
        normal = Q(bestNormal);
    }

    // The separation of an axis-aligned box from a convex brush along the brush's face normals and
    // the box's own six: false if any axis separates them (startdist >= 0), else the largest one.
    private static bool BrushDepth(Brush brush, Vector3 origin, Vector3 mins, Vector3 maxs, out float depth, out Vector3 normal)
    {
        depth = float.NegativeInfinity;
        normal = default;
        Vector3[] points = brush.Points;
        if (points.Length == 0) return false;
        // The box as Collision_BrushForBox makes it: corners at start + mins and start + maxs.
        Vector3 boxMins = origin + mins, boxMaxs = origin + maxs;
        foreach (BrushPlane side in brush.Sides)
        {
            Vector3 axis = side.Normal;
            if (brush.IsTriangle)
            {
                // For a triangle the number is DarkPlaces' own, operation for operation
                // (nearestplanedist_float of the box's eight corners less furthestplanedist_float of
                // the triangle's three points, all in single precision). The depth is what the
                // program moves an item by, at coordinates in the thousands a different order of the
                // same sums differs in the third decimal, and two thousandths decide whether the
                // move that follows clears the next triangle or stops dead against it (courtfun's
                // item_shield, on a patch whose collision triangles do not quite meet).
                float distance = NearestCorner(axis, boxMins, boxMaxs) - Furthest(axis, points);
                if (distance >= 0) return false;
                if (distance > depth) { depth = distance; normal = axis; }
                continue;
            }
            // nearest point of the box along the axis, less the furthest point of the brush
            float nearest = Vector3.Dot(axis, origin) + (axis.X > 0 ? mins.X : maxs.X) * axis.X + (axis.Y > 0 ? mins.Y : maxs.Y) * axis.Y + (axis.Z > 0 ? mins.Z : maxs.Z) * axis.Z;
            // The brush's furthest point along its own face normal lies on that face, so the plane's
            // distance is the same number - and is the map's own, where the brush's corner points
            // are intersections computed in single precision (a floor at -32 has corners at
            // -31.999998). DarkPlaces snaps its points (collision_snapscale); with the unsnapped ones
            // an item standing exactly on a floor would measure as two millionths deep in it.
            float d = nearest - side.Dist;
            if (d >= 0) return false;
            if (d > depth) { depth = d; normal = axis; }
        }
        Vector3 bMins = points[0], bMaxs = points[0];
        foreach (Vector3 p in points) { bMins = Vector3.Min(bMins, p); bMaxs = Vector3.Max(bMaxs, p); }
        if (brush.IsTriangle)
        {
            // A patch triangle is not an AABB brush and has no AABB planes, so DarkPlaces tests it on
            // every axis (the fast case of Collision_TraceBrushBrushFloat does not apply): the box's
            // six planes and the cross products of the edges can each separate the two, but only the
            // triangle's own planes ("nplane < numplanes1") may give the depth.
            if (origin.X + mins.X - bMaxs.X >= 0 || bMins.X - (origin.X + maxs.X) >= 0 || origin.Y + mins.Y - bMaxs.Y >= 0
                || bMins.Y - (origin.Y + maxs.Y) >= 0 || origin.Z + mins.Z - bMaxs.Z >= 0 || bMins.Z - (origin.Z + maxs.Z) >= 0)
                return false;
            foreach (Vector3 edge in brush.EdgeDirs)
                for (int boxEdge = 0; boxEdge < 3; boxEdge++)
                    for (int flip = 0; flip < 2; flip++)
                    {
                        Vector3 boxDir = boxEdge == 0 ? Vector3.UnitX : boxEdge == 1 ? Vector3.UnitY : Vector3.UnitZ;
                        Vector3 axis = flip != 0 ? Vector3.Cross(boxDir, edge) : Vector3.Cross(edge, boxDir);
                        if (axis.LengthSquared() < 1.0f / 4194304.0f) continue;   // COLLISION_EDGECROSS_MINLENGTH2: degenerate crossproducts
                        axis = Vector3.Normalize(axis);
                        if (NearestCorner(axis, boxMins, boxMaxs) - Furthest(axis, points) >= 0) return false;
                    }
            return true;
        }
        // the box's faces, as axes pointing from the brush toward the box
        Span<float> separations = stackalloc float[6]
        {
            origin.X + mins.X - bMaxs.X, bMins.X - (origin.X + maxs.X), origin.Y + mins.Y - bMaxs.Y, bMins.Y - (origin.Y + maxs.Y),
            origin.Z + mins.Z - bMaxs.Z, bMins.Z - (origin.Z + maxs.Z),
        };
        for (int i = 0; i < 6; i++)
        {
            if (separations[i] >= 0) return false;
            if (separations[i] > depth)
            {
                depth = separations[i];
                normal = i switch { 0 => Vector3.UnitX, 1 => -Vector3.UnitX, 2 => Vector3.UnitY, 3 => -Vector3.UnitY, 4 => Vector3.UnitZ, _ => -Vector3.UnitZ };
            }
        }
        return true;
    }

    // DotProduct, as the C writes it: three products summed left to right in single precision.
    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    // nearestplanedist_float over the eight points of Collision_BrushForBox, in its order.
    private static float NearestCorner(Vector3 axis, Vector3 lo, Vector3 hi)
    {
        float nearest = Dot(new Vector3(lo.X, lo.Y, lo.Z), axis);
        nearest = MathF.Min(nearest, Dot(new Vector3(hi.X, lo.Y, lo.Z), axis));
        nearest = MathF.Min(nearest, Dot(new Vector3(lo.X, hi.Y, lo.Z), axis));
        nearest = MathF.Min(nearest, Dot(new Vector3(hi.X, hi.Y, lo.Z), axis));
        nearest = MathF.Min(nearest, Dot(new Vector3(lo.X, lo.Y, hi.Z), axis));
        nearest = MathF.Min(nearest, Dot(new Vector3(hi.X, lo.Y, hi.Z), axis));
        nearest = MathF.Min(nearest, Dot(new Vector3(lo.X, hi.Y, hi.Z), axis));
        return MathF.Min(nearest, Dot(new Vector3(hi.X, hi.Y, hi.Z), axis));
    }

    // furthestplanedist_float
    private static float Furthest(Vector3 axis, Vector3[] points)
    {
        float furthest = Dot(points[0], axis);
        for (int i = 1; i < points.Length; i++) furthest = MathF.Max(furthest, Dot(points[i], axis));
        return furthest;
    }

    /// <summary>
    /// SV_PointSuperContents: the world's contents at a point, plus those of every SOLID_BSP entity
    /// whose brush model holds it (sv_gameplayfix_swiminbmodels, default 1).
    /// </summary>
    public int PointSuperContents(QcVector point)
    {
        Vector3 p = V(point);
        if (_trace is null || !IsFinite(p) || _traceDepth != 0) return 0;
        _traceDepth++;
        try
        {
            _passEdict = _passOwner = _passClipGroup = _onlyEdict = 0;
            _monsters = MonsterFilter.All;
            // SV_PointSuperContents asks the world model's PointSuperContents: brushes, never patch triangles
            return BspLegacyWorld.ContentsFromEngine(_trace.PointContents(p));
        }
        finally { _traceDepth--; }
    }

    /// <summary>VM_SV_checkpvs: 1 if the box is potentially visible from the point, 0 if not, 2 if
    /// the point is in no visibility cluster, 3 if the map has no visibility data.</summary>
    public int CheckPvs(QcVector viewPosition, QcVector mins, QcVector maxs)
    {
        if (_pvs is null) return 3;
        Vector3 view = V(viewPosition), lo = V(mins), hi = V(maxs);
        if (!IsFinite(view) || !IsFinite(lo) || !IsFinite(hi) || !_pvs.HasVis) return 2;
        int cluster = _pvs.LeafCluster(_pvs.FindLeaf(view));
        if (cluster < 0) return 2;
        return _pvs.BoxAnyClusterVisibleFrom(cluster, lo, hi) ? 1 : 0;
    }

    // ---- model_brush.c Mod_BSP_FatPVS / Mod_BSP_FatPVS_RecursiveBSPNode / Mod_BSP_BoxTouchingPVS --------

    /// <summary>model->brush.num_pvsclusterbytes: the size of a visibility set for <see cref="FatPvs"/>, 0 if the map has no visibility data.</summary>
    public int PvsBytes => _pvs is { HasVis: true } && Bsp is { } bsp ? Math.Min(bsp.Vis.BytesPerCluster, bsp.Vis.Data.Length) : 0;

    /// <summary>
    /// Mod_BSP_FatPVS: the union of what can be seen from every visibility cluster within
    /// <paramref name="radius"/> of a point - a viewer is not a point, and one standing in a doorway
    /// sees what both rooms see. With <paramref name="merge"/> the set is added to what the buffer
    /// holds (a second eye); without, it replaces it. A point in no cluster (inside a wall, outside
    /// the map) sees everything: "memset(*pvsbuffer, 0xFF, bytes)".
    /// </summary>
    /// <param name="buffer">At least <see cref="PvsBytes"/> long.</param>
    public void FatPvs(QcVector org, float radius, byte[] buffer, bool merge)
    {
        int bytes = Math.Min(PvsBytes, buffer.Length);
        if (bytes <= 0 || Bsp is not { } bsp || _pvs is null) return;
        Vector3 p = V(org);
        if (!IsFinite(p) || !float.IsFinite(radius) || _pvs.LeafCluster(_pvs.FindLeaf(p)) < 0)
        {
            Array.Fill(buffer, (byte)0xFF, 0, bytes);
            return;
        }
        if (!merge) Array.Clear(buffer, 0, bytes);
        if (bsp.Nodes.Length == 0)
        {
            AccumulateLeaf(bsp, 0, buffer, bytes);
            return;
        }
        FatPvsNode(bsp, p, radius, buffer, bytes, 0, 0);
    }

    private static void FatPvsNode(BspData bsp, Vector3 org, float radius, byte[] buffer, int bytes, int child, int depth)
    {
        // A child below zero is the leaf -(child + 1).
        while (child >= 0)
        {
            if (child >= bsp.Nodes.Length || depth > 4096) return;   // a malformed tree ends here
            BspNode node = bsp.Nodes[child];
            if ((uint)node.PlaneIndex >= (uint)bsp.Planes.Length) return;
            BspPlane plane = bsp.Planes[node.PlaneIndex];
            float d = Vector3.Dot(org, plane.Normal) - plane.Distance;
            if (d > radius) child = node.Child0;
            else if (d < -radius) child = node.Child1;
            else
            {
                // go down both sides
                FatPvsNode(bsp, org, radius, buffer, bytes, node.Child0, depth + 1);
                child = node.Child1;
            }
            depth++;
        }
        AccumulateLeaf(bsp, -(child + 1), buffer, bytes);
    }

    // "if this leaf is in a cluster, accumulate the pvs bits"
    private static void AccumulateLeaf(BspData bsp, int leaf, byte[] buffer, int bytes)
    {
        if ((uint)leaf >= (uint)bsp.Leafs.Length) return;
        int cluster = bsp.Leafs[leaf].Cluster;
        if (cluster < 0 || cluster >= bsp.Vis.ClusterCount) return;
        long row = (long)cluster * bsp.Vis.BytesPerCluster;
        if (row + bytes > bsp.Vis.Data.Length) return;
        ReadOnlySpan<byte> pvs = bsp.Vis.Data.AsSpan((int)row, bytes);
        for (int i = 0; i < bytes; i++) buffer[i] |= pvs[i];
    }

    private int[] _pvsNodeStack = new int[1024];

    /// <summary>
    /// Mod_BSP_BoxTouchingPVS: whether a box touches any leaf whose cluster is in the set. True when
    /// the map has no visibility data ("if (!model->brush.num_pvsclusters) return true").
    /// </summary>
    public bool BoxTouchingPvs(byte[] pvs, QcVector mins, QcVector maxs)
    {
        int bytes = Math.Min(PvsBytes, pvs.Length);
        if (bytes <= 0 || Bsp is not { } bsp) return true;
        Vector3 lo = V(mins), hi = V(maxs);
        if (!IsFinite(lo) || !IsFinite(hi)) return false;   // "ERROR: NAN bounding box!"
        int stack = 0, child = bsp.Nodes.Length == 0 ? -1 : 0, visited = 0;
        while (true)
        {
            if (child >= 0)
            {
                if (child >= bsp.Nodes.Length || ++visited > 1 << 20) return true;   // malformed: cull nothing
                BspNode node = bsp.Nodes[child];
                if ((uint)node.PlaneIndex >= (uint)bsp.Planes.Length) return true;
                BspPlane plane = bsp.Planes[node.PlaneIndex];
                // BoxOnPlaneSide: 1 in front, 2 behind, 3 across
                Vector3 n = plane.Normal;
                float near = (n.X < 0 ? hi.X : lo.X) * n.X + (n.Y < 0 ? hi.Y : lo.Y) * n.Y + (n.Z < 0 ? hi.Z : lo.Z) * n.Z;
                float far = (n.X < 0 ? lo.X : hi.X) * n.X + (n.Y < 0 ? lo.Y : hi.Y) * n.Y + (n.Z < 0 ? lo.Z : hi.Z) * n.Z;
                int sides = (far >= plane.Distance ? 1 : 0) | (near < plane.Distance ? 2 : 0);
                if (sides == 1) child = node.Child0;
                else if (sides == 2) child = node.Child1;
                else
                {
                    // box crosses plane, take one path and remember the other
                    if (stack < _pvsNodeStack.Length) _pvsNodeStack[stack++] = node.Child0;
                    child = node.Child1;
                }
                continue;
            }
            // leaf - check cluster bit
            int leaf = -(child + 1);
            if ((uint)leaf < (uint)bsp.Leafs.Length)
            {
                int cluster = bsp.Leafs[leaf].Cluster;
                if (cluster >= 0 && (cluster >> 3) < bytes && (pvs[cluster >> 3] & (1 << (cluster & 7))) != 0) return true;
            }
            // nothing to see here, try another path we didn't take earlier
            if (stack == 0) break;
            child = _pvsNodeStack[--stack];
        }
        return false;
    }

    /// <summary>
    /// Mod_Q3BSP_TraceLineOfSight as it runs by default (mod_q3bsp_tracelineofsight_brushes 0): a
    /// line through the map's BSP tree, stopped only where it passes from an empty leaf into a solid
    /// one - a leaf in no visibility cluster. Detail brushes, curved surfaces and entities do not
    /// stop it ("enables culling of entities behind detail brushes, curves, etc" is what the cvar
    /// turns on); neither does a line that starts in solid, because that "usually indicate[s] the
    /// eye is in solid and should see the target point anyway". True if the line ends, or is stopped,
    /// inside the box given.
    /// </summary>
    public bool TraceLineOfSight(QcVector start, QcVector end, QcVector acceptMins, QcVector acceptMaxs)
    {
        if (Bsp is not { } bsp || bsp.Nodes.Length == 0) return true;
        Vector3 s = V(start), e = V(end);
        if (!IsFinite(s) || !IsFinite(e)) return false;
        double ex = e.X, ey = e.Y, ez = e.Z;
        LineOfSightNode(bsp, 0, s.X, s.Y, s.Z, e.X, e.Y, e.Z, ref ex, ref ey, ref ez, 0);
        // BoxesOverlap(traceendpos, traceendpos, acceptmins, acceptmaxs)
        return ex >= acceptMins.X && ex <= acceptMaxs.X && ey >= acceptMins.Y && ey <= acceptMaxs.Y && ez >= acceptMins.Z && ez <= acceptMaxs.Z;
    }

    // Mod_Q3BSP_TraceLineOfSight_RecursiveNodeCheck, in doubles as the C is: 0 for an empty leaf, 1
    // for a solid one, "2 if empty is followed by solid (hit something)" with the point it happened at.
    private static int LineOfSightNode(BspData bsp, int child, double p1x, double p1y, double p1z, double p2x, double p2y, double p2z,
        ref double endX, ref double endY, ref double endZ, int depth)
    {
        while (child >= 0)
        {
            if (child >= bsp.Nodes.Length || depth > 4096) return 0;   // a malformed tree blocks nothing
            BspNode node = bsp.Nodes[child];
            if ((uint)node.PlaneIndex >= (uint)bsp.Planes.Length) return 0;
            BspPlane plane = bsp.Planes[node.PlaneIndex];
            // find the point distances
            double t1 = plane.Normal.X * p1x + plane.Normal.Y * p1y + plane.Normal.Z * p1z - plane.Distance;
            double t2 = plane.Normal.X * p2x + plane.Normal.Y * p2y + plane.Normal.Z * p2z - plane.Distance;
            int side;
            if (t1 < 0)
            {
                if (t2 < 0) { child = node.Child1; depth++; continue; }
                side = 1;
            }
            else
            {
                if (t2 >= 0) { child = node.Child0; depth++; continue; }
                side = 0;
            }
            double midf = t1 / (t1 - t2);
            double mx = p1x + midf * (p2x - p1x), my = p1y + midf * (p2y - p1y), mz = p1z + midf * (p2z - p1z);
            // recurse both sides, front side first; do not return 2 if both are solid or both empty,
            // or if start is solid and end is empty
            int ret = LineOfSightNode(bsp, side == 0 ? node.Child0 : node.Child1, p1x, p1y, p1z, mx, my, mz, ref endX, ref endY, ref endZ, depth + 1);
            if (ret != 0) return ret;
            ret = LineOfSightNode(bsp, side == 0 ? node.Child1 : node.Child0, mx, my, mz, p2x, p2y, p2z, ref endX, ref endY, ref endZ, depth + 1);
            if (ret != 1) return ret;
            endX = mx;
            endY = my;
            endZ = mz;
            return 2;
        }
        int leaf = -(child + 1);
        return (uint)leaf < (uint)bsp.Leafs.Length && bsp.Leafs[leaf].Cluster < 0 ? 1 : 0;
    }

    /// <summary>The visibility cluster a point is in, or -1 (outside the map, or no data): the
    /// viewer half of sv.worldmodel->brush.FatPVS, taken once per client per frame.</summary>
    public int ClusterOf(QcVector point)
    {
        Vector3 p = V(point);
        if (_pvs is null || !_pvs.HasVis || !IsFinite(p)) return -1;
        return _pvs.LeafCluster(_pvs.FindLeaf(p));
    }

    /// <summary>BoxTouchingPVS against the cluster <see cref="ClusterOf"/> returned. True (visible)
    /// when there is no visibility data to say otherwise.</summary>
    public bool BoxVisibleFrom(int cluster, QcVector mins, QcVector maxs)
    {
        if (_pvs is null || cluster < 0) return true;
        Vector3 lo = V(mins), hi = V(maxs);
        if (!IsFinite(lo) || !IsFinite(hi)) return true;
        return _pvs.BoxAnyClusterVisibleFrom(cluster, lo, hi);
    }

    // ---- the area grid --------------------------------------------------------------------------------

    // World_SetSize. sv_areagrid_mingridsize is 128.
    private void SetupGrid()
    {
        Array.Clear(_cells);
        _outside.Clear();
        Array.Clear(_links);
        float sizeX = MathF.Max(_worldMaxs.X - _worldMins.X, Grid * 128f);
        float sizeY = MathF.Max(_worldMaxs.Y - _worldMins.Y, Grid * 128f);
        _gridBiasX = -((_worldMins.X + _worldMaxs.X - sizeX) * 0.5f);
        _gridBiasY = -((_worldMins.Y + _worldMaxs.Y - sizeY) * 0.5f);
        _gridScaleX = Grid / sizeX;
        _gridScaleY = Grid / sizeY;
    }

    private bool CellRange(Vector3 mins, Vector3 maxs, out int x0, out int y0, out int x1, out int y1)
    {
        float fx0 = MathF.Floor((mins.X + _gridBiasX) * _gridScaleX), fy0 = MathF.Floor((mins.Y + _gridBiasY) * _gridScaleY);
        float fx1 = MathF.Floor((maxs.X + _gridBiasX) * _gridScaleX) + 1, fy1 = MathF.Floor((maxs.Y + _gridBiasY) * _gridScaleY) + 1;
        x0 = y0 = x1 = y1 = 0;
        // NaN fails every comparison, so it lands here too.
        if (!(fx0 >= 0 && fy0 >= 0 && fx1 <= Grid && fy1 <= Grid && fx1 > fx0 && fy1 > fy0)) return false;
        x0 = (int)fx0; y0 = (int)fy0; x1 = (int)fx1; y1 = (int)fy1;
        return true;
    }

    /// <summary>World_LinkEdict with the box SV_LinkEdict worked out. A SOLID_NOT entity is unlinked
    /// instead unless <see cref="LinkSolidNot"/>.</summary>
    public void LinkEdict(int edict, QcVector areaMins, QcVector areaMaxs, bool solidNot)
    {
        if (edict <= 0 || edict >= DpProtocol.MaxEdicts) return;
        if (edict >= _links.Length)
        {
            int size = Math.Min(DpProtocol.MaxEdicts, Math.Max(edict + 1, _links.Length * 2));
            Array.Resize(ref _links, size);
            Array.Resize(ref _marks, size);
        }
        ref LinkedEdict link = ref _links[edict];
        if (solidNot && !LinkSolidNot)
        {
            if (link.Linked) Unlink(edict, ref link);
            return;
        }
        Vector3 lo = V(areaMins), hi = V(areaMaxs);
        bool inside = CellRange(lo, hi, out int x0, out int y0, out int x1, out int y1) && (x1 - x0) * (y1 - y0) <= MaxCellsPerEdict;
        if (link.Linked && link.Outside == !inside && (!inside || (link.X0 == x0 && link.Y0 == y0 && link.X1 == x1 && link.Y1 == y1)))
        {
            // Same cells as before - the usual case, an entity moving a little: only the box changes.
            link.AreaMins = lo;
            link.AreaMaxs = hi;
            return;
        }
        if (link.Linked) Unlink(edict, ref link);
        link = new LinkedEdict { Linked = true, Outside = !inside, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, AreaMins = lo, AreaMaxs = hi };
        if (!inside)
        {
            _outside.Add(edict);
            return;
        }
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                (_cells[y * Grid + x] ??= new List<int>(4)).Add(edict);
    }

    /// <summary>World_UnlinkEdict.</summary>
    public void UnlinkEdict(int edict)
    {
        if ((uint)edict >= (uint)_links.Length) return;
        ref LinkedEdict link = ref _links[edict];
        if (link.Linked) Unlink(edict, ref link);
    }

    /// <summary>World_UnlinkAll.</summary>
    public void UnlinkAll() => SetupGrid();

    public bool IsLinked(int edict) => (uint)edict < (uint)_links.Length && _links[edict].Linked;

    /// <summary>ent->priv.server->areamins / areamaxs: the box the entity was last linked with.</summary>
    public bool TryGetArea(int edict, out QcVector mins, out QcVector maxs)
    {
        mins = maxs = default;
        if ((uint)edict >= (uint)_links.Length || !_links[edict].Linked) return false;
        mins = Q(_links[edict].AreaMins);
        maxs = Q(_links[edict].AreaMaxs);
        return true;
    }

    private void Unlink(int edict, ref LinkedEdict link)
    {
        if (link.Outside) _outside.Remove(edict);
        else
            for (int y = link.Y0; y < link.Y1; y++)
                for (int x = link.X0; x < link.X1; x++)
                    _cells[y * Grid + x]?.Remove(edict);
        link = default;
    }

    /// <summary>
    /// SV_EntitiesInBox (World_EntitiesInBox): every linked, live entity whose box touches the given
    /// one, each once. Returns how many were written; a list too short for them all is filled and
    /// counted in <see cref="CandidateOverflows"/>.
    /// </summary>
    public int EntitiesInBox(QcVector mins, QcVector maxs, Span<int> list) => EdictsInBox(V(mins), V(maxs), list, 0);

    private int EdictsInBox(Vector3 mins, Vector3 maxs, Span<int> list, float minSolid)
    {
        QcVm? vm = _vm;
        if (vm is null || list.IsEmpty) return 0;
        if (++_markNumber == int.MaxValue)
        {
            Array.Clear(_marks);
            _markNumber = 1;
        }
        int count = 0;
        Visit(vm, _outside, mins, maxs, list, ref count, minSolid);
        // "add 1 unit of padding to the box"; a box off the grid is clamped onto it.
        float fx0 = MathF.Floor((mins.X - 1 + _gridBiasX) * _gridScaleX), fy0 = MathF.Floor((mins.Y - 1 + _gridBiasY) * _gridScaleY);
        float fx1 = MathF.Floor((maxs.X + 1 + _gridBiasX) * _gridScaleX) + 1, fy1 = MathF.Floor((maxs.Y + 1 + _gridBiasY) * _gridScaleY) + 1;
        if (float.IsNaN(fx0) || float.IsNaN(fy0) || float.IsNaN(fx1) || float.IsNaN(fy1)) return count;
        int x0 = (int)Math.Clamp(fx0, 0, Grid), y0 = (int)Math.Clamp(fy0, 0, Grid);
        int x1 = (int)Math.Clamp(fx1, 0, Grid), y1 = (int)Math.Clamp(fy1, 0, Grid);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (_cells[y * Grid + x] is { Count: > 0 } cell)
                    Visit(vm, cell, mins, maxs, list, ref count, minSolid);
        return count;
    }

    private void Visit(QcVm vm, List<int> edicts, Vector3 mins, Vector3 maxs, Span<int> list, ref int count, float minSolid)
    {
        int numEdicts = vm.NumEdicts, solidField = _f!.Solid;
        foreach (int edict in edicts)
        {
            if (_marks[edict] == _markNumber) continue;
            _marks[edict] = _markNumber;
            ref LinkedEdict link = ref _links[edict];
            if (!CollisionWorld.BoxesOverlap(mins, maxs, link.AreaMins, link.AreaMaxs)) continue;
            if (edict >= numEdicts || vm.IsFree(edict)) continue;
            if (minSolid > 0 && !(vm.FieldFloat(edict, solidField) >= minSolid)) continue;
            if (count == list.Length)
            {
                CandidateOverflows++;
                return;
            }
            list[count++] = edict;
        }
    }

    // ---- TraceService.IEntityProvider: the program's entities, as the sweep wants them ----------------

    IReadOnlyList<Entity> TraceService.IEntityProvider.SolidEntities => Array.Empty<Entity>();

    // The broadphase of one trace, and with it every rule of SV_TraceBox's entity loop that the
    // collision library does not apply itself.
    void TraceService.IEntityProvider.EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
    {
        results.Clear();
        if (_vm is not { } vm || _f is not { } f) return;
        int count = EdictsInBox(mins, maxs, _touched, SolidBBox);
        int passOwnerMirror = -1;
        for (int i = 0; i < count; i++)
        {
            int edict = _touched[i];
            if (_onlyEdict != 0 && edict != _onlyEdict) continue;
            // "don't clip against self"
            if (edict == _passEdict && _passEdict != 0) continue;
            float solid = vm.FieldFloat(edict, f.Solid);
            int flags = QcVm.FloatToInt(vm.FieldFloat(edict, f.Flags));
            if ((flags & FlMonster) != 0 ? _monsters == MonsterFilter.Without : _monsters == MonsterFilter.Only) continue;
            // "don't clip against any entities in the same clipgroup (DP_RM_CLIPGROUP)"
            if (_passEdict != 0 && _passClipGroup != 0 && QcVm.FloatToInt(vm.FieldFloat(edict, f.ClipGroup)) == _passClipGroup) continue;

            int slot = results.Count;
            if (slot == _mirrors.Count) _mirrors.Add(new Entity());
            Entity mirror = _mirrors[slot];
            mirror.Index = edict;
            mirror.IsFreed = false;
            mirror.Solid = solid >= 0 && solid < 64 ? (Solid)(int)solid : Solid.BBox;
            mirror.Flags = (EntFlags)flags;
            mirror.Origin = V(vm.FieldVector(edict, f.Origin));
            mirror.Angles = V(vm.FieldVector(edict, f.Angles));
            mirror.Mins = V(vm.FieldVector(edict, f.Mins));
            mirror.Maxs = V(vm.FieldVector(edict, f.Maxs));
            // "don't clip owner against owned entities": the library compares object identity.
            mirror.Owner = _passEdict != 0 && vm.FieldInt(edict, f.Owner) == _passEdict ? _passMirror : null;
            if (edict == _passOwner && _passOwner != 0) passOwnerMirror = slot;
            results.Add(mirror);
        }
        // "don't clip owned entities against owner"
        _passMirror.Owner = passOwnerMirror >= 0 ? results[passOwnerMirror] : null;
    }

    // SV_TraceBox: "if (solid == SOLID_BSP) model = SV_GetModelFromEdict(touch)", and the matrix
    // Matrix4x4_CreateFromQuakeEntity makes of the entity's origin and angles when it has one.
    bool TraceService.IEntityProvider.TryGetEntityBrushModel(Entity e, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld)
    {
        localBrushes = Array.Empty<Brush>();
        toWorld = EntityMatrix.Identity;
        if (_modelNameOf is null || e.Solid != Solid.Bsp) return false;
        string? model = _modelNameOf(e.Index);
        if (model is null || model.Length < 2 || model[0] != '*') return false;
        if (!_submodels.TryGetValue(model, out BspCollisionBuilder.Submodel submodel) || submodel.Brushes.Length == 0) return false;
        localBrushes = submodel.Brushes;
        toWorld = EntityMatrix.FromQuakeEntity(e.Origin, e.Angles);
        return true;
    }

    // SV_TraceBox: "if (solid == SOLID_BSP || type == MOVE_HITMODEL) model = SV_GetModelFromEdict(touch)"
    // for a model that is not a map submodel, and "pitchsign = SV_GetPitchSign(prog, touch)": an alias
    // model's pitch turns the other way.
    bool TraceService.IEntityProvider.TryGetEntityMeshModel(Entity e, MoveFilter filter, out CollisionMesh? mesh, out EntityMatrix toWorld)
    {
        mesh = null;
        toWorld = EntityMatrix.Identity;
        if (_modelNameOf is null || (mesh = _models.Get(_modelNameOf(e.Index))) is null) return false;
        toWorld = EntityMatrix.FromQuakeEntity(e.Origin, new Vector3(-e.Angles.X, e.Angles.Y, e.Angles.Z));
        return true;
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private int EdictOf(in TraceResult hit) => hit.Ent is { } entity && !ReferenceEquals(entity, _passMirror) ? entity.Index : -1;

    internal static Vector3 V(QcVector v) => new(v.X, v.Y, v.Z);
    internal static QcVector Q(Vector3 v) => new(v.X, v.Y, v.Z);
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

// Port of Base/darkplaces/cl_collision.c CL_TraceBox / CL_TraceLine / CL_TracePoint (the entry points
// the client program's traces go through) and cl_collision.h CL_PointSuperContents; world.c
// World_LinkEdict / World_UnlinkEdict / World_EntitiesInBox (the area grid); clvm_cmds.c CL_movestep,
// CL_CheckBottom and the body of VM_CL_checkpvs; model_brush.c Mod_BSP_GetPVS / Mod_BSP_BoxTouchingPVS.
// collision.c Collision_ClipExtendPrepare / Collision_ClipExtendFinish (the lengthened trace).
// The sweep itself (collision.c Collision_TraceBrushBrushFloat and Collision_ClipToGenericEntity) is
// VortexArena.Engine.Collision.TraceService, which this drives. The map's curved surfaces are
// DarkPlaces' own collision triangles (Server/SvPatchCollision.cs), not that library's slabs: the
// client predicts its player against the same shapes the server moves it against, or it is corrected
// by several units every time it crosses a curved floor.
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// <see cref="ILegacyWorld"/> on the real map: the level's BSP from the game data, its brushes in the
/// port's collision library, and the client program's own solid entities clipped the way
/// CL_TraceBox clips them. No renderer is involved, so a Godot presentation uses this same object.
///
/// What a trace sees, in DarkPlaces' order: the world's brushes; then, unless the move is
/// MOVE_WORLDONLY, every entity the program linked (setorigin, setsize, setmodel) whose box the move
/// sweeps through and whose <c>.solid</c> is SOLID_BBOX or more. A SOLID_BSP entity showing a map
/// submodel ("*3", a door or a platform) is clipped against that submodel's brushes, turned and moved
/// with the entity; anything else is clipped as its box.
///
/// The map, the program's entity numbers and every field read here are untrusted. Nothing is indexed
/// without a check, the candidate list of one trace is bounded, and a missing or malformed map leaves
/// an empty world in which every trace runs its full length.
/// </summary>
/// <remarks>
/// Knowing deviations from DarkPlaces, all of them about what is clipped rather than how:
/// <list type="bullet">
/// <item>Engine-networked entities are not clipped (CL_TraceBox's hitnetworkbrushmodels and
/// hitnetworkplayers): <c>trace_networkentity</c> is always 0. Xonotic networks its players and most
/// of its movers through the client program, which links them itself.</item>
/// <item>A SOLID_BSP entity whose model is not a map submodel, and any entity under MOVE_HITMODEL, is
/// clipped as its box. DarkPlaces traces the model's triangles.</item>
/// <item>Every trace is lengthened by one unit (collision_extendtracelinelength and
/// collision_extendtraceboxlength, both 1, are what the program's traceline and tracebox use); a
/// tracetoss, which DarkPlaces lengthens by collision_extendmovelength (16), gets the same one unit.</item>
/// <item>A curved surface that belongs to a brush-model entity stops a line from both sides; the
/// world's stop it from the front only, as DarkPlaces' patch triangles do.</item>
/// <item><c>trace_dpstartcontents</c> is the contents at the start <em>point</em>; DarkPlaces reports
/// the contents of every brush the box starts in. For a line they are the same.</item>
/// <item>A trace that starts inside an entity without ever leaving it names no entity; DarkPlaces
/// (Collision_CombineTraces) names the one it started in.</item>
/// </list>
/// </remarks>
public sealed class BspLegacyWorld : ILegacyWorld, TraceService.IEntityProvider
{
    // bspfile.h SUPERCONTENTS_*, as the program sees them. The collision library keeps the same
    // flags at other bit positions (VortexArena.Engine.Collision.SuperContents), so every mask going
    // in and every contents value coming out is translated.
    public const int ContentsSolid = 0x1, ContentsWater = 0x2, ContentsSlime = 0x4, ContentsLava = 0x8, ContentsSky = 0x10,
        ContentsBody = 0x20, ContentsCorpse = 0x40, ContentsNoDrop = 0x80, ContentsPlayerClip = 0x100,
        ContentsMonsterClip = 0x200, ContentsDoNotEnter = 0x400, ContentsBotClip = 0x800, ContentsOpaque = 0x1000;
    public const int ContentsLiquidsMask = ContentsWater | ContentsSlime | ContentsLava;

    private static readonly (int Dp, int Engine)[] ContentsMap =
    {
        (ContentsSolid, SuperContents.Solid), (ContentsWater, SuperContents.Water), (ContentsSlime, SuperContents.Slime),
        (ContentsLava, SuperContents.Lava), (ContentsSky, SuperContents.Sky), (ContentsBody, SuperContents.Body),
        (ContentsCorpse, SuperContents.Corpse), (ContentsNoDrop, SuperContents.NoDrop), (ContentsPlayerClip, SuperContents.PlayerClip),
        (ContentsMonsterClip, SuperContents.MonsterClip), (ContentsDoNotEnter, SuperContents.DonotEnter),
        (ContentsBotClip, SuperContents.BotClip), (ContentsOpaque, SuperContents.Opaque),
    };

    /// <summary>SUPERCONTENTS_* as DarkPlaces numbers them, to the collision library's bits.</summary>
    public static int ContentsToEngine(int dp)
    {
        int engine = 0;
        foreach ((int d, int e) in ContentsMap)
            if ((dp & d) != 0) engine |= e;
        return engine;
    }

    /// <summary>The collision library's contents bits, to SUPERCONTENTS_* as DarkPlaces numbers them.</summary>
    public static int ContentsFromEngine(int engine)
    {
        int dp = 0;
        foreach ((int d, int e) in ContentsMap)
            if ((engine & e) != 0) dp |= d;
        return dp;
    }

    // server.h
    private const float SolidBBox = 2, SolidBsp = 4;
    private const int FlFly = 1, FlSwim = 2, FlMonster = 32, FlOnGround = 512, FlPartialGround = 1024;
    private const int MoveNormal = 0, MoveNoMonsters = 1, MoveMissile = 2, MoveWorldOnly = 3, MoveHitModel = 4;

    /// <summary>The most entities one trace clips against. World_EntitiesInBox allows MAX_EDICTS; a
    /// move that sweeps more than this many solid entities is not a real one.</summary>
    public const int MaxTouchedEdicts = 1024;

    private readonly VirtualFileSystem _files;
    private readonly Dictionary<string, long>? _calls;
    private CsqcHost? _host;
    private int _ownerField = -1, _clipGroupField = -1, _enemyField = -1;

    // DarkPlaces tests a box against brushes and patch triangles, a line against brushes and (from
    // their front) patch triangles, a point against brushes alone: two views of the map.
    private TraceService? _worldTrace;    // world brushes and patch triangles: box sweeps before a program exists
    private TraceService? _trace;         // the same and the program's entities
    private TraceService? _worldLine;     // world brushes only: pointcontents, lines and points
    private TraceService? _line;          // world brushes and the program's entities
    private CollisionWorld? _full;        // brushes and patch triangles, for a line's triangle candidates
    private int _patchTriangles;
    private readonly List<Brush> _lineBrushes = new();
    private TraceService? _entityTrace;   // the entities alone, over an empty world: the MOVE_MISSILE pass
    private BspPvs? _pvs;
    private Vector3 _worldMins, _worldMaxs;
    private readonly Dictionary<string, BspCollisionBuilder.Submodel> _submodels = new(StringComparer.Ordinal);

    // The area grid of world.c: AREA_GRID cells a side over the map's XY extent, each listing the
    // entities whose box touches it, and one list for entities too large or too far out to grid.
    private const int Grid = 128;                 // AREA_GRID
    private const float MinGridSize = 128;        // sv_areagrid_mingridsize
    private const int MaxCellsPerEdict = 16 * 16; // an entity over more cells than this goes to the outside list
    private readonly List<int>?[] _cells = new List<int>?[Grid * Grid];
    private readonly List<int> _outside = new();
    private float _gridBiasX, _gridBiasY, _gridScaleX = 1, _gridScaleY = 1;
    private LinkedEdict[] _links = new LinkedEdict[512];
    private int[] _marks = new int[512];
    private int _markNumber;

    private struct LinkedEdict
    {
        public bool Linked, Outside;
        public int X0, Y0, X1, Y1; // cell range, max exclusive
        public Vector3 AbsMin, AbsMax;
    }

    // One trace's candidates. The collision library clips against Entity objects, so each candidate
    // edict is mirrored into one of these for the length of the call; they are reused, never kept.
    private readonly List<Entity> _mirrors = new();
    private readonly Entity _passMirror = new();
    private readonly int[] _touched = new int[MaxTouchedEdicts];
    private int _passEdict, _passOwner, _passClipGroup;
    private MonsterFilter _monsters;

    private enum MonsterFilter { All, Without, Only }

    /// <param name="files">The game data. The map is read from here and nowhere else.</param>
    /// <param name="calls">Where to count the calls received, by member name; null for no counting.</param>
    public BspLegacyWorld(VirtualFileSystem files, Dictionary<string, long>? calls = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _calls = calls;
    }

    /// <summary>The map that is loaded ("maps/x.bsp"), or null.</summary>
    public string? MapName { get; private set; }
    /// <summary>The parsed map, for whoever else needs it (surface queries, a renderer). Null without a map.</summary>
    public BspData? Bsp { get; private set; }
    /// <summary>Why the last <see cref="LoadMap"/> failed, or null.</summary>
    public string? LoadError { get; private set; }
    /// <summary>The static collision world, for a caller that traces on its own account.</summary>
    public CollisionWorld? Collision { get; private set; }
    /// <summary>Traces that ran into the <see cref="MaxTouchedEdicts"/> bound.</summary>
    public long CandidateOverflows { get; private set; }

    private void Count(string member)
    {
        if (_calls is not null) _calls[member] = _calls.GetValueOrDefault(member) + 1;
    }

    /// <summary>
    /// Loads the level's map and builds its collision once: what CL_BeginDownloads does with
    /// cl.model_precache[1] before the client program starts. False, leaving an empty world, if the
    /// file is missing or is not a map this reader understands (<see cref="LoadError"/> says which).
    /// </summary>
    /// <param name="worldModel">The name from svc_serverinfo, e.g. "maps/stormkeep.bsp".</param>
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
            BspData bsp = BspReader.Read(_files.ReadBytes(worldModel));
            UseMap(worldModel, bsp, BspCollisionBuilder.Build(bsp));
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A map from a server's download directory is as untrusted as anything else it sends.
            Unload();
            LoadError = $"map \"{worldModel}\" could not be loaded: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Takes a map that is already parsed and built, in place of <see cref="LoadMap"/>: for an owner
    /// that needs the same data for drawing and should not read the file twice.
    /// </summary>
    /// <param name="bsp">The parsed map, for bounds and visibility; null for collision built by hand
    /// (then there is no visibility data and the bounds are the brushes').</param>
    public void UseMap(string worldModel, BspData? bsp, BspCollisionBuilder.Result built)
    {
        ArgumentNullException.ThrowIfNull(built);
        Unload();
        Bsp = bsp;
        CollisionWorld brushWorld = built.World, full = built.World;
        IReadOnlyList<BspCollisionBuilder.Submodel> submodels = built.Submodels;
        if (bsp is not null)
        {
            // What the caller built holds the shared builder's patch slabs, which are not what a
            // DarkPlaces server collides with. Build the brushes again from the map without its
            // faces, and add DarkPlaces' patch triangles. (The file is read once more for the LOD
            // bounds that group patches for seamless tessellation; without it no patches are grouped.)
            byte[] file = !string.IsNullOrEmpty(worldModel) && LegacyQcHost.IsSafePath(worldModel) && _files.Exists(worldModel) ? _files.ReadBytes(worldModel) : Array.Empty<byte>();
            BspData brushesOnly = new() { Version = bsp.Version, Textures = bsp.Textures, Planes = bsp.Planes, Models = bsp.Models, Brushes = bsp.Brushes, BrushSides = bsp.BrushSides };
            BspCollisionBuilder.Result brushes = BspCollisionBuilder.Build(brushesOnly);
            List<Brush>[] patches = Server.SvPatchCollision.Build(bsp, file);
            brushWorld = brushes.World;
            full = new CollisionWorld();
            full.AddBrushes(brushes.World.Brushes);
            full.AddBrushes(patches[0]);
            full.BuildGrid();
            _patchTriangles = patches[0].Count;
            List<BspCollisionBuilder.Submodel> withPatches = new(brushes.Submodels.Count);
            foreach (BspCollisionBuilder.Submodel submodel in brushes.Submodels)
            {
                int index = submodel.Name.Length > 1 && int.TryParse(submodel.Name.AsSpan(1), out int n) ? n : -1;
                withPatches.Add(index > 0 && index < patches.Length && patches[index].Count > 0
                    ? submodel with { Brushes = submodel.Brushes.Concat(patches[index]).ToArray() } : submodel);
            }
            submodels = withPatches;
        }
        Collision = full;
        _full = full;
        foreach (BspCollisionBuilder.Submodel submodel in submodels) _submodels[submodel.Name] = submodel;
        _pvs = bsp is null ? null : new BspPvs(bsp);
        // cl.world.mins / maxs are the world model's normalmins / normalmaxs: model 0 of the map.
        if (bsp is { Models.Length: > 0 })
        {
            _worldMins = bsp.Models[0].Mins;
            _worldMaxs = bsp.Models[0].Maxs;
        }
        else
        {
            _worldMins = built.World.WorldMins;
            _worldMaxs = built.World.WorldMaxs;
        }
        _worldTrace = new TraceService(full);
        _trace = new TraceService(full, this);
        _worldLine = new TraceService(brushWorld);
        _line = new TraceService(brushWorld, this);
        CollisionWorld empty = new();
        empty.BuildGrid();
        _entityTrace = new TraceService(empty, this);
        MapName = worldModel;
        SetupGrid();
    }

    private void Unload()
    {
        MapName = null;
        Bsp = null;
        Collision = null;
        LoadError = null;
        _worldTrace = _trace = _worldLine = _line = _entityTrace = null;
        _full = null;
        _patchTriangles = 0;
        _pvs = null;
        _submodels.Clear();
        _worldMins = _worldMaxs = default;
        SetupGrid();
    }

    /// <summary>The program whose entities are clipped. Its links start empty (a new program is a new world).</summary>
    public void Attach(CsqcHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ownerField = FieldOffset(host, "owner", 1);
        _clipGroupField = FieldOffset(host, "clipgroup", 1);
        _enemyField = FieldOffset(host, "enemy", 1);
        SetupGrid();
    }

    // An engine field's cell, or -1 if the program's definition of it does not fit in an entity.
    private static int FieldOffset(CsqcHost host, string name, int cells) =>
        host.Vm.FindField(name) is { } def && def.Offset >= 0 && def.Offset + cells <= host.Vm.EntityFields ? def.Offset : -1;

    /// <summary>A map submodel ("*N"): its bounds and brushes, in the model's own space.</summary>
    public bool TryGetSubmodel(string name, out Vector3 mins, out Vector3 maxs, out Brush[] brushes)
    {
        if (_submodels.TryGetValue(name, out BspCollisionBuilder.Submodel submodel))
        {
            mins = submodel.Mins;
            maxs = submodel.Maxs;
            brushes = submodel.Brushes;
            return true;
        }
        mins = maxs = default;
        brushes = Array.Empty<Brush>();
        return false;
    }

    // ---- ILegacyWorld: the level ---------------------------------------------------------------------

    public void Bounds(out QcVector mins, out QcVector maxs)
    {
        mins = Q(_worldMins);
        maxs = Q(_worldMaxs);
    }

    /// <summary>VM_CL_droptofloor: "if (cl.worldmodel->brush.isq3bsp) end[2] -= 4096". Only Quake 3
    /// maps are read here; the answer for no map at all is the same, since nothing will be hit.</summary>
    public float DropToFloorDistance => 4096;

    // ---- ILegacyWorld: traces ------------------------------------------------------------------------

    /// <summary>
    /// CL_TraceBox, and through it CL_TraceLine and CL_TracePoint for a box of no size.
    /// </summary>
    public LegacyTrace Trace(QcVector start, QcVector mins, QcVector maxs, QcVector end, int moveType, int ignoreEdict, int hitContentsMask, bool isLine)
    {
        Count(nameof(Trace));

        // Collision_ClipExtendPrepare: "make the trace longer according to the extend parameter" -
        // collision_extendtracelinelength / collision_extendtraceboxlength, one unit - "to ensure
        // detection of collisions within the collision_impactnudge distance (this does not alter
        // the final trace length)".
        Vector3 realStart = V(start), realDelta = V(end) - realStart;
        float realLength = realDelta.Length();
        if (!(realLength > 0) || !float.IsFinite(realLength)) return TraceUnextended(start, mins, maxs, end, moveType, ignoreEdict, hitContentsMask, isLine);
        float scaleToExtend = (realLength + TraceExtend) / realLength;
        LegacyTrace trace = TraceUnextended(start, mins, maxs, Q(realStart + scaleToExtend * realDelta), moveType, ignoreEdict, hitContentsMask, isLine);
        // Collision_ClipExtendFinish
        if (trace.Fraction != 1.0f)
        {
            // undo the extended trace length
            trace.Fraction *= scaleToExtend;
            // "if the extended trace hit something that the unextended trace did not hit (even
            // considering the collision_impactnudge), then we have to clear the hit information"
            if (trace.Fraction > 1.0f)
            {
                trace.Entity = 0;
                trace.HitQ3SurfaceFlags = 0;
                trace.HitContents = 0;
                trace.HitTextureName = null;
                trace.PlaneNormal = default;
                trace.PlaneDist = 0;
            }
        }
        trace.Fraction = Math.Clamp(trace.Fraction, 0, 1);
        trace.EndPos = Q(realStart + trace.Fraction * realDelta);
        return trace;
    }

    private const float TraceExtend = 1;

    private LegacyTrace TraceUnextended(QcVector start, QcVector mins, QcVector maxs, QcVector end, int moveType, int ignoreEdict, int hitContentsMask, bool isLine)
    {

        // A long move is traced as a run of short ones, stopping at the first that hits. The collision
        // library finds its candidates by the bounding box of the whole move, and the client program
        // routinely fires lines thousands of units long (crosshair, shot origin, waypoint visibility):
        // measured on stormkeep, one such line averaged 75 microseconds against 5 for a player-sized
        // box moved a short way, because its box covered most of the map's 29,000 brushes. DarkPlaces
        // does not pay this (it walks a bounding-interval hierarchy along the line). Short moves - all of
        // player movement prediction - take the single-trace path below, bit for bit as before.
        float dx = end.X - start.X, dy = end.Y - start.Y, dz = end.Z - start.Z;
        float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > SegmentLength * 1.5f) || !float.IsFinite(length))
            return TraceSegment(start, mins, maxs, end, moveType, ignoreEdict, hitContentsMask, isLine);

        int segments = Math.Min(MaxSegments, (int)MathF.Ceiling(length / SegmentLength));
        LegacyTrace first = default;
        QcVector from = start;
        for (int i = 0; i < segments; i++)
        {
            float t = (i + 1) / (float)segments;
            QcVector to = i == segments - 1 ? end : new QcVector(start.X + dx * t, start.Y + dy * t, start.Z + dz * t);
            LegacyTrace part = TraceSegment(from, mins, maxs, to, moveType, ignoreEdict, hitContentsMask, isLine);
            if (i == 0) first = part;
            if (part.Fraction < 1 || part.StartSolid)
            {
                // What the move started in is a fact about its first step, wherever it ended.
                part.Fraction = (i + part.Fraction) / segments;
                part.StartContents = first.StartContents;
                part.StartSolid = first.StartSolid;
                part.AllSolid = first.AllSolid && i == 0;
                return part;
            }
            from = to;
        }
        first.Fraction = 1;
        first.EndPos = end;
        return first;
    }

    private const float SegmentLength = 384;
    private const int MaxSegments = 64;

    private LegacyTrace TraceSegment(QcVector start, QcVector mins, QcVector maxs, QcVector end, int moveType, int ignoreEdict, int hitContentsMask, bool isLine)
    {
        // On a Quake 3 map neither inopen nor inwater is ever set: only the Quake 1 hull code
        // (model_brush.c Mod_Q1BSP_RecursiveHullCheck) writes them, and Collision_CombineTraces
        // copies inwater alone. So both stay false, which is what the program sees in DarkPlaces.
        LegacyTrace result = new() { Fraction = 1, EndPos = end };
        if (_trace is null || _worldTrace is null || _line is null || _worldLine is null) return result;

        Vector3 vStart = V(start), vEnd = V(end), vMins = V(mins), vMaxs = V(maxs);
        if (!IsFinite(vStart) || !IsFinite(vEnd) || !IsFinite(vMins) || !IsFinite(vMaxs)) return result;

        // CL_TraceBox: a box of no size is a point (CL_TracePoint) or, if it moves, a line (CL_TraceLine).
        bool pointBox = vMins == vMaxs;
        TraceService service = pointBox ? _line : _trace, worldService = pointBox ? _worldLine : _worldTrace;
        result.StartContents = ContentsFromEngine(_worldLine.PointContents(vStart + (pointBox ? vMins : Vector3.Zero)));
        int engineMask = ContentsToEngine(hitContentsMask);
        // A mask with none of the known bits can stop on nothing.
        if (engineMask == 0) return result;

        // "if the passedict is world, make it NULL (to avoid two checks each time)"
        CsqcHost? host = _host;
        _passEdict = host is not null && ignoreEdict > 0 && ignoreEdict < host.Vm.NumEdicts && !host.Vm.IsFree(ignoreEdict) ? ignoreEdict : 0;
        _passOwner = _passEdict != 0 && _ownerField >= 0 ? host!.Vm.FieldInt(_passEdict, _ownerField) : 0;
        _passClipGroup = _passEdict != 0 && _clipGroupField >= 0 ? QcVm.FloatToInt(host!.Vm.FieldFloat(_passEdict, _clipGroupField)) : 0;
        // The mask is the caller's (CL_GenericHitSuperContentsMask of the ignored entity, or its
        // .dphitcontentsmask); the collision library takes it from the entity it is told to pass over.
        _passMirror.Index = _passEdict;
        _passMirror.DpHitContentsMask = engineMask;
        _passMirror.Owner = null;

        TraceResult hit;
        int hitEdict = 0;
        if (moveType == MoveWorldOnly || host is null)
            hit = worldService.Trace(vStart, vMins, vMaxs, vEnd, MoveFilter.WorldOnly, _passMirror);
        else if (moveType == MoveMissile)
        {
            // "size when clipping against monsters": a MOVE_MISSILE box is 15 units larger on every
            // side, but only against FL_MONSTER entities. So the entities are clipped in two passes:
            // everything but monsters with the box as given, then monsters alone with the larger box.
            _monsters = MonsterFilter.Without;
            hit = service.Trace(vStart, vMins, vMaxs, vEnd, MoveFilter.Normal, _passMirror);
            hitEdict = EdictOf(hit.Ent);
            // (Always run: the first pass looked for candidates along the thin box, so it cannot say
            // whether a monster lies within the margin. Over an empty world this costs one grid query.)
            if (_entityTrace is not null)
            {
                _monsters = MonsterFilter.Only;
                Vector3 grow = new(15, 15, 15);
                // (This pass refills the mirrors, which is why the first pass's entity was noted above.)
                TraceResult monster = _entityTrace.Trace(vStart, vMins - grow, vMaxs + grow, vEnd, MoveFilter.Normal, _passMirror);
                // Collision_CombineTraces: the nearer impact wins; solid starts accumulate.
                bool startSolid = hit.StartSolid || monster.StartSolid, allSolid = hit.AllSolid || monster.AllSolid;
                if (monster.Fraction < hit.Fraction && monster.PlaneNormal != Vector3.Zero)
                {
                    hit = monster;
                    hitEdict = EdictOf(monster.Ent);
                }
                hit.StartSolid = startSolid;
                hit.AllSolid = allSolid;
            }
        }
        else
        {
            _monsters = MonsterFilter.All;
            MoveFilter filter = moveType == MoveNoMonsters ? MoveFilter.NoMonsters : moveType == MoveHitModel ? MoveFilter.HitModel : MoveFilter.Normal;
            hit = service.Trace(vStart, vMins, vMaxs, vEnd, filter, _passMirror);
            hitEdict = EdictOf(hit.Ent);
        }

        // Mod_CollisionBIH_TraceLine's BIH_COLLISIONTRIANGLE case: a line is also stopped by the
        // world's patch triangles, from their front side, if that is nearer than what it has hit.
        if (pointBox && vStart != vEnd && _patchTriangles > 0 && _full is { } full)
        {
            Vector3 lineStart = vStart + vMins, lineEnd = vEnd + vMins;
            Server.SvPatchCollision.LineHit line = new() { Fraction = hit.Fraction };
            _lineBrushes.Clear();
            full.Query(Vector3.Min(lineStart, lineEnd) - Vector3.One, Vector3.Max(lineStart, lineEnd) + Vector3.One, _lineBrushes);
            foreach (Brush brush in _lineBrushes)
                if (Server.SvPatchCollision.IsTriangle(brush) && (brush.Contents & engineMask) != 0)
                    Server.SvPatchCollision.TraceLineTriangle(ref line, lineStart, lineEnd, brush);
            if (line.Triangle is { } triangle)
            {
                hit.Fraction = line.Fraction;
                hit.EndPos = vStart + line.Fraction * (vEnd - vStart);
                hit.PlaneNormal = line.PlaneNormal;
                hit.PlaneDist = line.PlaneDist;
                hit.DpHitContents = triangle.Contents;
                hit.DpHitQ3SurfaceFlags = triangle.SurfaceFlags;
                hit.DpHitTextureName = triangle.Texture;
                hitEdict = 0;
            }
        }

        result.Fraction = hit.Fraction;
        result.EndPos = Q(hit.EndPos);
        result.AllSolid = hit.AllSolid;
        result.StartSolid = hit.StartSolid;
        result.PlaneNormal = Q(hit.PlaneNormal);
        result.PlaneDist = hit.PlaneDist;
        // The world is entity 0, and so is "nothing".
        result.Entity = hitEdict;
        result.HitContents = ContentsFromEngine(hit.DpHitContents);
        result.HitQ3SurfaceFlags = hit.DpHitQ3SurfaceFlags;
        result.HitTextureName = hit.DpHitTextureName;
        // The mirrors are scratch: nothing outside this call may come to depend on them.
        _passMirror.Owner = null;
        return result;
    }

    /// <summary>
    /// CL_PointSuperContents: a point trace with MOVE_NOMONSTERS that does not look at the program's
    /// entities at all (hitcsqcentities is false), only at the world - so a point inside a water brush
    /// that the program owns as an entity is not in water as far as pointcontents is concerned.
    /// </summary>
    public int PointSuperContents(QcVector point)
    {
        Count(nameof(PointSuperContents));
        Vector3 p = V(point);
        if (_worldLine is null || !IsFinite(p)) return 0;
        // brushes, never patch triangles ("skipped because they have no volume")
        return ContentsFromEngine(_worldLine.PointContents(p));
    }

    /// <summary>
    /// VM_CL_checkpvs: the visibility set of the leaf the viewer is in (Mod_BSP_GetPVS), tested
    /// against every leaf the box touches (Mod_BSP_BoxTouchingPVS).
    /// </summary>
    public int CheckPvs(QcVector viewPosition, QcVector mins, QcVector maxs)
    {
        Count(nameof(CheckPvs));
        // "no PVS support on this worldmodel... darn"
        if (_pvs is null) return 3;
        Vector3 view = V(viewPosition), lo = V(mins), hi = V(maxs);
        if (!IsFinite(view) || !IsFinite(lo) || !IsFinite(hi)) return 2;
        // GetPVS answers NULL for a map without visibility data and for a viewer in a leaf with no
        // cluster (inside a wall, outside the map): "viewpos isn't in any PVS... darn"
        if (!_pvs.HasVis) return 2;
        int cluster = _pvs.LeafCluster(_pvs.FindLeaf(view));
        if (cluster < 0) return 2;
        return _pvs.BoxAnyClusterVisibleFrom(cluster, lo, hi) ? 1 : 0;
    }

    // ---- ILegacyWorld: the area grid -------------------------------------------------------------------

    // World_SetSize.
    private void SetupGrid()
    {
        Array.Clear(_cells);
        _outside.Clear();
        Array.Clear(_links);
        float sizeX = MathF.Max(_worldMaxs.X - _worldMins.X, Grid * MinGridSize);
        float sizeY = MathF.Max(_worldMaxs.Y - _worldMins.Y, Grid * MinGridSize);
        _gridBiasX = -((_worldMins.X + _worldMaxs.X - sizeX) * 0.5f);
        _gridBiasY = -((_worldMins.Y + _worldMaxs.Y - sizeY) * 0.5f);
        _gridScaleX = Grid / sizeX;
        _gridScaleY = Grid / sizeY;
    }

    // The cell range of a box, max exclusive; false if it does not lie within the grid.
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

    /// <summary>World_LinkEdict, with the box CL_LinkEdict worked out.</summary>
    public void LinkEdict(int edict, QcVector absMin, QcVector absMax)
    {
        Count(nameof(LinkEdict));
        if (edict <= 0 || edict >= DpProtocol.MaxEdicts) return;
        if (edict >= _links.Length)
        {
            int size = Math.Min(DpProtocol.MaxEdicts, Math.Max(edict + 1, _links.Length * 2));
            Array.Resize(ref _links, size);
            Array.Resize(ref _marks, size);
        }
        Vector3 lo = V(absMin), hi = V(absMax);
        // "an entity which is larger than the grid cells, or outside the grid, goes to the outside list"
        bool inside = CellRange(lo, hi, out int x0, out int y0, out int x1, out int y1) && (x1 - x0) * (y1 - y0) <= MaxCellsPerEdict;
        ref LinkedEdict link = ref _links[edict];
        if (link.Linked && link.Outside == !inside && (!inside || (link.X0 == x0 && link.Y0 == y0 && link.X1 == x1 && link.Y1 == y1)))
        {
            // Same cells as before - the usual case, an entity moving a little: only the box changes.
            link.AbsMin = lo;
            link.AbsMax = hi;
            return;
        }
        if (link.Linked) Unlink(edict, ref link);
        link = new LinkedEdict { Linked = true, Outside = !inside, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, AbsMin = lo, AbsMax = hi };
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
        Count(nameof(UnlinkEdict));
        if ((uint)edict >= (uint)_links.Length) return;
        ref LinkedEdict link = ref _links[edict];
        if (link.Linked) Unlink(edict, ref link);
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

    // World_EntitiesInBox: the linked entities whose box touches the given one, each once - and, of
    // those, only the ones a move can stop on (.solid of SOLID_BBOX or more). A client program links
    // thousands of entities that are not solid; testing that here keeps them out of the bounded list,
    // so the bound is on what a trace actually clips against.
    private int EdictsInBox(QcVm vm, int solidField, Vector3 mins, Vector3 maxs, Span<int> list)
    {
        if (++_markNumber == int.MaxValue)
        {
            Array.Clear(_marks);
            _markNumber = 1;
        }
        int count = 0;
        Visit(vm, solidField, _outside, mins, maxs, list, ref count);
        // "add 1 unit of padding to the box"; a box off the grid is clamped onto it.
        float fx0 = MathF.Floor((mins.X - 1 + _gridBiasX) * _gridScaleX), fy0 = MathF.Floor((mins.Y - 1 + _gridBiasY) * _gridScaleY);
        float fx1 = MathF.Floor((maxs.X + 1 + _gridBiasX) * _gridScaleX) + 1, fy1 = MathF.Floor((maxs.Y + 1 + _gridBiasY) * _gridScaleY) + 1;
        if (float.IsNaN(fx0) || float.IsNaN(fy0) || float.IsNaN(fx1) || float.IsNaN(fy1)) return count;
        int x0 = (int)Math.Clamp(fx0, 0, Grid), y0 = (int)Math.Clamp(fy0, 0, Grid);
        int x1 = (int)Math.Clamp(fx1, 0, Grid), y1 = (int)Math.Clamp(fy1, 0, Grid);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (_cells[y * Grid + x] is { Count: > 0 } cell)
                    Visit(vm, solidField, cell, mins, maxs, list, ref count);
        return count;
    }

    private void Visit(QcVm vm, int solidField, List<int> edicts, Vector3 mins, Vector3 maxs, Span<int> list, ref int count)
    {
        int numEdicts = vm.NumEdicts;
        foreach (int edict in edicts)
        {
            if (_marks[edict] == _markNumber) continue;
            _marks[edict] = _markNumber;
            ref LinkedEdict link = ref _links[edict];
            if (!CollisionWorld.BoxesOverlap(mins, maxs, link.AbsMin, link.AbsMax)) continue;
            if (edict >= numEdicts || vm.IsFree(edict) || !(vm.FieldFloat(edict, solidField) >= SolidBBox)) continue;
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

    // The broadphase of one trace, and with it every rule of CL_TraceBox's entity loop that the
    // collision library does not apply itself: the solid test comes first here so that non-solid
    // entities (most of what a client program links) cost one field read and no mirror.
    void TraceService.IEntityProvider.EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
    {
        results.Clear();
        if (_host is not { } host) return;
        QcVm vm = host.Vm;
        CsqcFieldOffsets f = host.Fields;
        int count = EdictsInBox(vm, f.Solid, mins, maxs, _touched);
        int passOwnerMirror = -1;
        for (int i = 0; i < count; i++)
        {
            int edict = _touched[i];
            float solid = vm.FieldFloat(edict, f.Solid);
            int flags = QcVm.FloatToInt(vm.FieldFloat(edict, f.Flags));
            if ((flags & FlMonster) != 0 ? _monsters == MonsterFilter.Without : _monsters == MonsterFilter.Only) continue;
            // "don't clip against any entities in the same clipgroup (DP_RM_CLIPGROUP)"
            if (_passEdict != 0 && _passClipGroup != 0 && _clipGroupField >= 0
                && QcVm.FloatToInt(vm.FieldFloat(edict, _clipGroupField)) == _passClipGroup)
                continue;

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
            mirror.Owner = _passEdict != 0 && _ownerField >= 0 && vm.FieldInt(edict, _ownerField) == _passEdict ? _passMirror : null;
            if (edict == _passEdict)
            {
                // "don't clip against self". The library would skip it too, given the same object.
                continue;
            }
            if (edict == _passOwner && _passOwner != 0) passOwnerMirror = slot;
            results.Add(mirror);
        }
        // "don't clip owned entities against owner"
        _passMirror.Owner = passOwnerMirror >= 0 ? results[passOwnerMirror] : null;
    }

    // CL_TraceBox: "if (solid == SOLID_BSP) model = CL_GetModelFromEdict(touch)", and the matrix
    // Matrix4x4_CreateFromQuakeEntity makes of the entity's origin and angles when it has one.
    bool TraceService.IEntityProvider.TryGetEntityBrushModel(Entity e, out IReadOnlyList<Brush> localBrushes, out EntityMatrix toWorld)
    {
        localBrushes = Array.Empty<Brush>();
        toWorld = EntityMatrix.Identity;
        if (_host is not { } host || e.Solid != Solid.Bsp) return false;
        string? model = host.ModelNameOf(e.Index);
        if (model is null || model.Length < 2 || model[0] != '*') return false;
        if (!_submodels.TryGetValue(model, out BspCollisionBuilder.Submodel submodel) || submodel.Brushes.Length == 0) return false;
        localBrushes = submodel.Brushes;
        toWorld = EntityMatrix.FromQuakeEntity(e.Origin, e.Angles);
        return true;
    }

    // ---- ILegacyWorld: walkmove -----------------------------------------------------------------------

    /// <summary>
    /// CL_movestep(ent, move, relink = true, noenemy = false, settrace): "The move will be adjusted
    /// for slopes and stairs, but if the move isn't possible, no move is done and false is returned".
    /// </summary>
    public bool MoveStep(int edict, QcVector move, bool setTrace)
    {
        Count(nameof(MoveStep));
        if (_host is not { } host || edict <= 0 || edict >= host.Vm.NumEdicts || host.Vm.IsFree(edict)) return false;
        QcVm vm = host.Vm;
        CsqcFieldOffsets f = host.Fields;
        CsqcBuiltins builtins = host.Builtins;
        QcVector mins = vm.FieldVector(edict, f.Mins), maxs = vm.FieldVector(edict, f.Maxs);
        QcVector oldOrigin = vm.FieldVector(edict, f.Origin);
        QcVector newOrigin = Add(oldOrigin, move);
        int flags = QcVm.FloatToInt(vm.FieldFloat(edict, f.Flags));
        int mask = builtins.HitContentsMask(edict);
        LegacyTrace trace;

        // flying monsters don't step up
        if ((flags & (FlSwim | FlFly)) != 0)
        {
            int enemy = _enemyField >= 0 ? vm.FieldInt(edict, _enemyField) : 0;
            if (enemy < 0 || enemy >= vm.NumEdicts) enemy = 0;
            // try one move with vertical motion, then one without
            for (int i = 0; i < 2; i++)
            {
                QcVector origin = vm.FieldVector(edict, f.Origin);
                newOrigin = Add(origin, move);
                if (i == 0 && enemy != 0)
                {
                    float dz = origin.Z - vm.FieldVector(enemy, f.Origin).Z;
                    if (dz > 40) newOrigin.Z -= 8;
                    if (dz < 30) newOrigin.Z += 8;
                }
                trace = Trace(origin, mins, maxs, newOrigin, MoveNormal, edict, mask, isLine: false);
                if (setTrace) builtins.SetTraceGlobals(trace);
                if (trace.Fraction == 1)
                {
                    // swim monster left water
                    if ((flags & FlSwim) != 0 && (PointSuperContents(trace.EndPos) & ContentsLiquidsMask) == 0) return false;
                    vm.FieldVector(edict, f.Origin) = trace.EndPos;
                    host.LinkEdict(edict);
                    return true;
                }
                if (enemy == 0) break;
            }
            return false;
        }

        // push down from a step height above the wished position
        float stepHeight = host.Services.CvarExists("sv_stepheight") ? host.Services.CvarFloat("sv_stepheight") : 18;
        newOrigin.Z += stepHeight;
        QcVector end = newOrigin;
        end.Z -= stepHeight * 2;
        trace = Trace(newOrigin, mins, maxs, end, MoveNormal, edict, mask, isLine: false);
        if (setTrace) builtins.SetTraceGlobals(trace);
        if (trace.StartSolid)
        {
            newOrigin.Z -= stepHeight;
            trace = Trace(newOrigin, mins, maxs, end, MoveNormal, edict, mask, isLine: false);
            if (setTrace) builtins.SetTraceGlobals(trace);
            if (trace.StartSolid) return false;
        }
        if (trace.Fraction == 1)
        {
            // if monster had the ground pulled out, go ahead and fall
            if ((flags & FlPartialGround) != 0)
            {
                vm.FieldVector(edict, f.Origin) = Add(vm.FieldVector(edict, f.Origin), move);
                host.LinkEdict(edict);
                vm.FieldFloat(edict, f.Flags) = flags & ~FlOnGround;
                return true;
            }
            return false; // walked off an edge
        }

        // check point traces down for dangling corners
        vm.FieldVector(edict, f.Origin) = trace.EndPos;
        if (!CheckBottom(host, edict, stepHeight, mask))
        {
            if ((flags & FlPartialGround) != 0)
            {
                // entity had floor mostly pulled out from underneath it and is trying to correct
                host.LinkEdict(edict);
                return true;
            }
            vm.FieldVector(edict, f.Origin) = oldOrigin;
            return false;
        }
        if ((flags & FlPartialGround) != 0) vm.FieldFloat(edict, f.Flags) = flags & ~FlPartialGround;
        vm.FieldInt(edict, f.GroundEntity) = (uint)trace.Entity < (uint)vm.NumEdicts ? trace.Entity : 0;
        // the move is ok
        host.LinkEdict(edict);
        return true;
    }

    // CL_CheckBottom: "Returns false if any part of the bottom of the entity is off an edge that is
    // not a staircase." (The MOVE_NOMONSTERS variant CL_movestep uses; the builtin has its own.)
    private bool CheckBottom(CsqcHost host, int edict, float stepHeight, int mask)
    {
        QcVm vm = host.Vm;
        CsqcFieldOffsets f = host.Fields;
        QcVector origin = vm.FieldVector(edict, f.Origin);
        QcVector mins = Add(origin, vm.FieldVector(edict, f.Mins)), maxs = Add(origin, vm.FieldVector(edict, f.Maxs));

        // if all of the points under the corners are solid world, don't bother with the tougher checks
        bool easy = true;
        for (int x = 0; x <= 1 && easy; x++)
            for (int y = 0; y <= 1 && easy; y++)
                if ((PointSuperContents(new QcVector(x != 0 ? maxs.X : mins.X, y != 0 ? maxs.Y : mins.Y, mins.Z - 1)) & (ContentsSolid | ContentsBody)) == 0)
                    easy = false;
        if (easy) return true;

        // check it for real: the midpoint must be within 16 of the bottom
        QcVector start = new((mins.X + maxs.X) * 0.5f, (mins.Y + maxs.Y) * 0.5f, mins.Z);
        QcVector stop = new(start.X, start.Y, start.Z - 2 * stepHeight);
        LegacyTrace trace = Trace(start, default, default, stop, MoveNoMonsters, edict, mask, isLine: true);
        if (trace.Fraction == 1.0f) return false;
        float mid = trace.EndPos.Z, bottom = mid;
        // the corners must be within 16 of the midpoint
        for (int x = 0; x <= 1; x++)
            for (int y = 0; y <= 1; y++)
            {
                start.X = stop.X = x != 0 ? maxs.X : mins.X;
                start.Y = stop.Y = y != 0 ? maxs.Y : mins.Y;
                trace = Trace(start, default, default, stop, MoveNoMonsters, edict, mask, isLine: true);
                if (trace.Fraction != 1.0f && trace.EndPos.Z > bottom) bottom = trace.EndPos.Z;
                if (trace.Fraction == 1.0f || mid - trace.EndPos.Z > stepHeight) return false;
            }
        return true;
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private int EdictOf(Entity? entity) => entity is not null && !ReferenceEquals(entity, _passMirror) ? entity.Index : 0;

    private static Vector3 V(QcVector v) => new(v.X, v.Y, v.Z);
    private static QcVector Q(Vector3 v) => new(v.X, v.Y, v.Z);
    private static QcVector Add(QcVector a, QcVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

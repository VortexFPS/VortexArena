// Port of Base/darkplaces/cl_collision.c CL_TraceBox / CL_TraceLine / CL_TracePoint (the entry points
// the client program's traces go through) and cl_collision.h CL_PointSuperContents; world.c
// World_LinkEdict / World_UnlinkEdict / World_EntitiesInBox (the area grid); clvm_cmds.c CL_movestep,
// CL_CheckBottom and the body of VM_CL_checkpvs; model_brush.c Mod_BSP_GetPVS / Mod_BSP_BoxTouchingPVS.
// The sweep itself (collision.c Collision_TraceBrushBrushFloat and Collision_ClipToGenericEntity),
// the lengthened trace (Collision_ClipExtendPrepare / Finish) and the map's collision are
// VortexArena.Engine.Collision - TraceService on a world built with BspCollisionOptions.DarkPlaces,
// exactly as the server half (Server/SvWorld.cs) builds its own: the client predicts its player
// against the same shapes the server moves it against, or it is corrected by several units every
// time it crosses a curved floor.
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
/// clipped against its model's triangles as DarkPlaces clips it, but at the model's first frame
/// whatever the entity shows, and only for MD3 and IQM models within the bounds of
/// <see cref="Server.SvModelCollision"/>; any other is clipped as its box.</item>
/// <item>Every trace is lengthened by one unit (collision_extendtracelinelength and
/// collision_extendtraceboxlength, both 1, are what the program's traceline and tracebox use); a
/// tracetoss, which DarkPlaces lengthens by collision_extendmovelength (16), gets the same one unit.</item>
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
    private int _ownerField = -1, _clipGroupField = -1, _enemyField = -1, _solidMirror;

    // The map as DarkPlaces collides with it - brushes and patch triangles in one world. The trace
    // library tests a box against both, a line against the brushes and the front of the triangles, a
    // point against the brushes alone.
    private TraceService? _worldTrace;    // the world: traces before a program exists, pointcontents
    private TraceService? _trace;         // the world and the program's entities
    private TraceService? _entityTrace;   // the entities alone, over an empty world: the MOVE_MISSILE pass
    private readonly Server.SvModelCollision _models;
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
        _models = new Server.SvModelCollision(files);
    }

    /// <summary>The collision meshes of the level's non-brush models, loaded as traces meet them.</summary>
    public Server.SvModelCollision ModelCollision => _models;

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
            byte[] file = _files.ReadBytes(worldModel);
            BspData bsp = BspReader.Read(file);
            UseMap(worldModel, bsp, BuildCollision(bsp, file));
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
    /// The collision of a map as legacy mode needs it: brushes, DarkPlaces' patch triangles, its
    /// hierarchy. What <see cref="UseMap(string, BspData?, BspCollisionBuilder.Result)"/> expects to be
    /// handed, and what the server half builds for itself.
    /// </summary>
    /// <param name="mapFile">The bytes the map was parsed from (see <see cref="BspCollisionOptions.MapFile"/>).</param>
    public static BspCollisionBuilder.Result BuildCollision(BspData bsp, ReadOnlyMemory<byte> mapFile) =>
        BspCollisionBuilder.Build(bsp, null, BspCollisionOptions.DarkPlaces(mapFile));

    /// <summary>
    /// Takes a map that is already parsed, in place of <see cref="LoadMap"/>, and builds its collision
    /// once: for an owner that needs the parsed map for drawing and should not parse the file twice.
    /// (The file is read once more, unparsed, for the patches' LOD bounds.)
    /// </summary>
    public void UseMap(string worldModel, BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        byte[] file = Array.Empty<byte>();
        try
        {
            if (!string.IsNullOrEmpty(worldModel) && LegacyQcHost.IsSafePath(worldModel) && _files.Exists(worldModel)) file = _files.ReadBytes(worldModel);
        }
        catch (IOException) { /* no LOD bounds: no two patches are grouped */ }
        UseMap(worldModel, bsp, BuildCollision(bsp, file));
    }

    /// <summary>
    /// Takes a map that is already parsed and built, in place of <see cref="LoadMap"/>.
    /// </summary>
    /// <param name="bsp">The parsed map, for bounds and visibility; null for collision built by hand
    /// (then there is no visibility data and the bounds are the brushes').</param>
    /// <param name="built">The collision, from <see cref="BuildCollision"/>. It is used as it is.
    /// The one exception is for a caller that still hands over the shared builder's default
    /// (patch slabs) together with the map it came from: slabs are not what a DarkPlaces server
    /// collides with, so that result is set aside and the collision built again here - the cost
    /// <see cref="UseMap(string, BspData)"/> exists to avoid.</param>
    public void UseMap(string worldModel, BspData? bsp, BspCollisionBuilder.Result built)
    {
        ArgumentNullException.ThrowIfNull(built);
        if (bsp is not null && built.PatchCollision != PatchCollisionMode.DarkPlacesTriangles)
        {
            UseMap(worldModel, bsp);
            return;
        }
        Unload();
        Bsp = bsp;
        CollisionWorld full = built.World;
        IReadOnlyList<BspCollisionBuilder.Submodel> submodels = built.Submodels;
        Collision = full;
        foreach (BspCollisionBuilder.Submodel submodel in submodels) _submodels[submodel.Name] = submodel;
        _pvs = bsp is null ? null : new BspPvs(bsp);
        // cl.world.mins / maxs are the world model's normalmins / normalmaxs: model 0 of the map.
        if (built.DarkPlacesWorldBounds is { } worldBounds)
        {
            // (enlarged, as DarkPlaces does, to hold everything the model draws)
            _worldMins = worldBounds.Mins;
            _worldMaxs = worldBounds.Maxs;
        }
        else if (bsp is { Models.Length: > 0 })
        {
            _worldMins = bsp.Models[0].Mins;
            _worldMaxs = bsp.Models[0].Maxs;
        }
        else
        {
            _worldMins = built.World.WorldMins;
            _worldMaxs = built.World.WorldMaxs;
        }
        _worldTrace = new TraceService(full) { DarkPlacesArithmetic = true };
        _trace = new TraceService(full, this) { DarkPlacesArithmetic = true };
        CollisionWorld empty = new();
        empty.BuildGrid();
        _entityTrace = new TraceService(empty, this) { DarkPlacesArithmetic = true };
        MapName = worldModel;
        SetupGrid();
    }

    private void Unload()
    {
        MapName = null;
        Bsp = null;
        Collision = null;
        LoadError = null;
        _worldTrace = _trace = _entityTrace = null;
        _models.Clear();
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
        _solidMirror = host.Vm.MirrorField(host.Fields.Solid);
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
        Vector3 realStart = V(start), realEnd = V(end);
        float realLength = (realEnd - realStart).Length();
        if (!(realLength > 0) || !float.IsFinite(realLength)) return TraceUnextended(start, mins, maxs, end, moveType, ignoreEdict, hitContentsMask, isLine);
        TraceExtension extension = TraceExtension.Prepare(realStart, realEnd, TraceExtend);
        LegacyTrace trace = TraceUnextended(start, mins, maxs, Q(extension.ExtendEnd), moveType, ignoreEdict, hitContentsMask, isLine);
        // Collision_ClipExtendFinish
        double fraction = extension.FinishFraction(trace.Fraction, out bool cleared);
        if (cleared)
        {
            trace.Entity = 0;
            trace.HitQ3SurfaceFlags = 0;
            trace.HitContents = 0;
            trace.HitTextureName = null;
            trace.PlaneNormal = default;
            trace.PlaneDist = 0;
        }
        trace.Fraction = (float)fraction;
        trace.EndPos = Q(extension.EndPos(fraction));
        return trace;
    }

    private const float TraceExtend = 1;

    // One trace, as asked. (Until the collision world found its candidates the way DarkPlaces does - by
    // walking a hierarchy along the move - a long move was cut into 384-unit pieces here, because the
    // grid broadphase handed a long line most of the map: 75 microseconds a line on stormkeep. With
    // the hierarchy a long line costs 5, less than the pieces did, and the pieces were not the same
    // trace: a line that started in solid ended at the first piece.)
    private LegacyTrace TraceUnextended(QcVector start, QcVector mins, QcVector maxs, QcVector end, int moveType, int ignoreEdict, int hitContentsMask, bool isLine)
    {
        // On a Quake 3 map neither inopen nor inwater is ever set: only the Quake 1 hull code
        // (model_brush.c Mod_Q1BSP_RecursiveHullCheck) writes them, and Collision_CombineTraces
        // copies inwater alone. So both stay false, which is what the program sees in DarkPlaces.
        LegacyTrace result = new() { Fraction = 1, EndPos = end };
        if (_trace is null || _worldTrace is null) return result;

        Vector3 vStart = V(start), vEnd = V(end), vMins = V(mins), vMaxs = V(maxs);
        if (!IsFinite(vStart) || !IsFinite(vEnd) || !IsFinite(vMins) || !IsFinite(vMaxs)) return result;

        // CL_TraceBox: a box of no size is a point (CL_TracePoint) or, if it moves, a line (CL_TraceLine).
        // (The trace library makes the same distinction for what it tests a point against.)
        TraceService service = _trace, worldService = _worldTrace;
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
        int hitEdict = 0, startContents;
        if (moveType == MoveWorldOnly || host is null)
        {
            hit = worldService.Trace(vStart, vMins, vMaxs, vEnd, MoveFilter.WorldOnly, _passMirror);
            startContents = worldService.LastStartContents;
        }
        else if (moveType == MoveMissile)
        {
            // "size when clipping against monsters": a MOVE_MISSILE box is 15 units larger on every
            // side, but only against FL_MONSTER entities. So the entities are clipped in two passes:
            // everything but monsters with the box as given, then monsters alone with the larger box.
            _monsters = MonsterFilter.Without;
            hit = service.Trace(vStart, vMins, vMaxs, vEnd, MoveFilter.Normal, _passMirror);
            hitEdict = EdictOf(hit.Ent);
            startContents = service.LastStartContents;
            // (Always run: the first pass looked for candidates along the thin box, so it cannot say
            // whether a monster lies within the margin. Over an empty world this costs one grid query.)
            if (_entityTrace is not null)
            {
                _monsters = MonsterFilter.Only;
                Vector3 grow = new(15, 15, 15);
                // (This pass refills the mirrors, which is why the first pass's entity was noted above.)
                TraceResult monster = _entityTrace.Trace(vStart, vMins - grow, vMaxs + grow, vEnd, MoveFilter.Normal, _passMirror);
                startContents |= _entityTrace.LastStartContents;
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
            startContents = service.LastStartContents;
        }
        // trace.startsupercontents: the contents of everything the box starts inside, whatever the mask
        result.StartContents = ContentsFromEngine(startContents);

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
        if (_worldTrace is null || !IsFinite(p)) return 0;
        // brushes, never patch triangles ("skipped because they have no volume")
        return ContentsFromEngine(_worldTrace.PointContents(p));
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
    private int EdictsInBox(QcVm vm, Vector3 mins, Vector3 maxs, Span<int> list)
    {
        if (++_markNumber == int.MaxValue)
        {
            Array.Clear(_marks);
            _markNumber = 1;
        }
        int count = 0;
        Visit(vm, _outside, mins, maxs, list, ref count);
        // "add 1 unit of padding to the box"; a box off the grid is clamped onto it.
        float fx0 = MathF.Floor((mins.X - 1 + _gridBiasX) * _gridScaleX), fy0 = MathF.Floor((mins.Y - 1 + _gridBiasY) * _gridScaleY);
        float fx1 = MathF.Floor((maxs.X + 1 + _gridBiasX) * _gridScaleX) + 1, fy1 = MathF.Floor((maxs.Y + 1 + _gridBiasY) * _gridScaleY) + 1;
        if (float.IsNaN(fx0) || float.IsNaN(fy0) || float.IsNaN(fx1) || float.IsNaN(fy1)) return count;
        int x0 = (int)Math.Clamp(fx0, 0, Grid), y0 = (int)Math.Clamp(fy0, 0, Grid);
        int x1 = (int)Math.Clamp(fx1, 0, Grid), y1 = (int)Math.Clamp(fy1, 0, Grid);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (_cells[y * Grid + x] is { Count: > 0 } cell)
                    Visit(vm, cell, mins, maxs, list, ref count);
        return count;
    }

    private void Visit(QcVm vm, List<int> edicts, Vector3 mins, Vector3 maxs, Span<int> list, ref int count)
    {
        int numEdicts = vm.NumEdicts;
        // .solid is read from the VM's mirror of it (QcVm.MirrorField), not from the entity: this loop
        // looks at some forty entities for every one a trace can stop on, and each entity's own copy
        // lies in ten kilobytes of fields nothing else here touches - a cache miss apiece, which was a
        // third of what a trace cost.
        int solidMirror = _solidMirror;
        foreach (int edict in edicts)
        {
            if (_marks[edict] == _markNumber) continue;
            _marks[edict] = _markNumber;
            ref LinkedEdict link = ref _links[edict];
            if (!CollisionWorld.BoxesOverlap(mins, maxs, link.AbsMin, link.AbsMax)) continue;
            if (edict >= numEdicts || vm.IsFree(edict) || !(vm.MirroredFloat(solidMirror, edict) >= SolidBBox)) continue;
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
        int count = EdictsInBox(vm, mins, maxs, _touched);
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

    // CL_TraceBox: "if (solid == SOLID_BSP || type == MOVE_HITMODEL) model = CL_GetModelFromEdict(touch)"
    // for a model that is not a map submodel, with the pitch of an alias model turned the other way
    // (CL_GetPitchSign).
    bool TraceService.IEntityProvider.TryGetEntityMeshModel(Entity e, MoveFilter filter, out CollisionMesh? mesh, out EntityMatrix toWorld)
    {
        mesh = null;
        toWorld = EntityMatrix.Identity;
        if (_host is not { } host || (mesh = _models.Get(host.ModelNameOf(e.Index))) is null) return false;
        toWorld = EntityMatrix.FromQuakeEntity(e.Origin, new Vector3(-e.Angles.X, e.Angles.Y, e.Angles.Z));
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

using System.Numerics;

namespace VortexArena.Engine.Collision;

// Port of the Darkplaces collision world (Base/darkplaces/collision.c, world.c).
//
// The collision world is the BSP brush set (convex planar half-space groups carrying contentflags),
// kept entirely separate from render geometry — see planning/specs/determinism-and-physics.md.
// A brush is the intersection of its plane half-spaces: a point is inside the brush when it lies on
// the back side of (or on) every plane (Dot(normal, p) <= dist for all planes).
//
// The trace algorithm (TraceService) needs three things from each brush to run the Separating Axis
// Theorem sweep that Collision_TraceBrushBrushFloat implements:
//   - planes    (face normals + dists)
//   - points    (the corner vertices — used for furthest/nearest plane-dist along candidate axes)
//   - edgedirs  (unique edge directions — crossed with the box edge dirs to build separating axes)
// For axis-aligned map brushes derived from an AABB the points/edgedirs are trivial; for general
// convex brushes the caller supplies them (the BSP loader will, in a later phase).

/// <summary>
/// SUPERCONTENTS_* bitmask (Base/darkplaces/bspfile.h). Engine traces use these richer masks rather
/// than the legacy CONTENT_* enum; <see cref="VortexArena.Common.Framework.Contents"/> is the QC-facing
/// point-content value. TraceResult.DpHitContents carries the SUPERCONTENTS of the surface hit.
/// </summary>
public static class SuperContents
{
    public const int Solid          = 0x00000001;
    public const int Water          = 0x00000010;
    public const int Slime          = 0x00000020;
    public const int Lava           = 0x00000040;
    public const int Sky            = 0x00000080;
    public const int Body           = 0x02000000;
    public const int PlayerClip     = 0x00000100;
    public const int MonsterClip    = 0x00000200;
    public const int DonotEnter     = 0x00000400;   // bot-only AI hint; never blocks a physical move
    public const int BotClip        = 0x00000800;   // blocks bots/monsters only
    public const int Corpse         = 0x20000000;
    public const int NoDrop         = unchecked((int)0x80000000);
    public const int Opaque         = 0x10000000;

    /// <summary>SUPERCONTENTS_LIQUIDSMASK — water/slime/lava together (used by waterlevel checks).</summary>
    public const int LiquidsMask = Water | Slime | Lava;

    /// <summary>
    /// Default hit mask DP uses when no dphitcontentsmask is set on a generic entity
    /// (SV_GenericHitSuperContentsMask): solid + body + corpse.
    /// </summary>
    public const int DefaultHitMask = Solid | Body | Corpse;

    /// <summary>
    /// Port of Darkplaces <c>Mod_Q3BSP_SuperContentsFromNativeContents</c> (model_brush.c): convert the RAW Q3
    /// BSP native content bits (<see cref="Q3Contents"/>) stored in the texture lump into this engine's
    /// SUPERCONTENTS bitspace. This MUST run on every brush/leaf content value the loader reads — the on-disk
    /// Q3 bits are a <i>different</i> bitspace, and feeding them straight to a trace makes Q3
    /// <see cref="Q3Contents.Translucent"/> (0x20000000) alias <see cref="Corpse"/>, so water/lava/slime,
    /// <c>common/hint</c>, <c>common/donotenter</c> and nodraw decals all gain a hit-mask bit and become
    /// invisible walls.
    ///
    /// Exactly like DP, the partitioning/decoration bits (STRUCTURAL/DETAIL/TRANSLUCENT/TRIGGER/FOG/AREAPORTAL/
    /// TELEPORTER/JUMPPAD/CLUSTERPORTAL/ORIGIN) map to nothing — a hint/trigger/translucent brush carries no
    /// SOLID and never blocks — while OPAQUE is set whenever the brush is NOT translucent (DP's inverted test,
    /// used for vis, not collision).
    /// </summary>
    public static int FromQ3Native(int q3)
    {
        int s = 0;
        if ((q3 & Q3Contents.Solid)       != 0) s |= Solid;
        if ((q3 & Q3Contents.Water)       != 0) s |= Water;
        if ((q3 & Q3Contents.Slime)       != 0) s |= Slime;
        if ((q3 & Q3Contents.Lava)        != 0) s |= Lava;
        if ((q3 & Q3Contents.Body)        != 0) s |= Body;
        if ((q3 & Q3Contents.Corpse)      != 0) s |= Corpse;
        if ((q3 & Q3Contents.NoDrop)      != 0) s |= NoDrop;
        if ((q3 & Q3Contents.PlayerClip)  != 0) s |= PlayerClip;
        if ((q3 & Q3Contents.MonsterClip) != 0) s |= MonsterClip;
        if ((q3 & Q3Contents.DonotEnter)  != 0) s |= DonotEnter;
        if ((q3 & Q3Contents.BotClip)     != 0) s |= BotClip;
        if ((q3 & Q3Contents.Translucent) == 0) s |= Opaque;
        return s;
    }
}

/// <summary>
/// Raw Q3 BSP native content flags (<c>CONTENTSQ3_*</c>, Base/darkplaces/bspfile.h) as stored on disk in the
/// texture lump's <c>contents</c> field and read verbatim by <c>BspReader</c>. These are a DIFFERENT bitspace
/// from <see cref="SuperContents"/> and must be converted via <see cref="SuperContents.FromQ3Native"/> before
/// any trace consumes them.
/// </summary>
public static class Q3Contents
{
    public const int Solid         = 0x00000001;
    public const int Lava          = 0x00000008;
    public const int Slime         = 0x00000010;
    public const int Water         = 0x00000020;
    public const int Fog           = 0x00000040;
    public const int AreaPortal    = 0x00008000;
    public const int PlayerClip    = 0x00010000;
    public const int MonsterClip   = 0x00020000;
    public const int Teleporter    = 0x00040000;
    public const int JumpPad       = 0x00080000;
    public const int ClusterPortal = 0x00100000;
    public const int DonotEnter    = 0x00200000;
    public const int BotClip       = 0x00400000;
    public const int Origin        = 0x01000000;
    public const int Body          = 0x02000000;
    public const int Corpse        = 0x04000000;
    public const int Detail        = 0x08000000;
    public const int Structural    = 0x10000000;
    public const int Translucent   = 0x20000000;
    public const int Trigger       = 0x40000000;
    public const int NoDrop        = unchecked((int)0x80000000);
}

/// <summary>Q3SURFACEFLAG_* (Base/darkplaces — Q3 surface flags). Game logic reads these for slick/ladder/clip etc.</summary>
public static class Q3SurfaceFlags
{
    public const int NoDamage   = 0x0001;
    public const int Slick      = 0x0002;
    public const int Sky        = 0x0004;
    public const int Ladder     = 0x0008;
    public const int NoImpact   = 0x0010;
    public const int NoMarks    = 0x0020;
    public const int Flesh      = 0x0040;
    public const int NoDraw     = 0x0080;
    public const int NoSteps    = 0x2000;
    public const int NonSolid   = 0x4000;
}

/// <summary>
/// One bounding plane of a convex brush (DP <c>colplanef_t</c>). The half-space is
/// <c>Dot(Normal, p) &lt;= Dist</c>. SurfaceFlags/Contents are the Q3 surfaceflags / SUPERCONTENTS of
/// the face this plane came from (used to fill TraceResult.DpHitQ3SurfaceFlags / DpHitContents).
/// <see cref="Texture"/> is the per-plane shader/texture name (DP <c>colplanef_t.texture-&gt;name</c>),
/// used to fill TraceResult.DpHitTextureName — the value <c>world.qc</c> compares against
/// <c>"common/caulk"</c>. Null for a generated box brush (DP's box planes carry a NULL texture).
/// </summary>
public struct BrushPlane
{
    public Vector3 Normal;
    public float Dist;
    public int SurfaceFlags;   // Q3SURFACEFLAG_*
    public int Contents;       // SUPERCONTENTS_* of this face (informational; brush-wide Contents is authoritative)
    public string? Texture;    // shader/texture name of this face (DP texture->name); null on a box brush

    public BrushPlane(Vector3 normal, float dist, int surfaceFlags = 0, int contents = 0, string? texture = null)
    {
        Normal = normal;
        Dist = dist;
        SurfaceFlags = surfaceFlags;
        Contents = contents;
        Texture = texture;
    }
}

/// <summary>
/// A convex brush: the intersection of its <see cref="Sides"/> half-spaces (DP <c>colbrushf_t</c>).
/// Carries a brush-wide <see cref="Contents"/> (SUPERCONTENTS bitmask) tested against the trace's
/// hit mask. <see cref="Points"/> and <see cref="EdgeDirs"/> feed the SAT sweep; <see cref="IsAabb"/>
/// marks the fast path (DP's <c>hasaabbplanes</c>) where edge-cross axes are unnecessary because the
/// brush's own face planes already separate it from any AABB.
/// </summary>
public sealed class Brush
{
    public BrushPlane[] Sides;
    public Vector3[] Points;
    public Vector3[] EdgeDirs;
    public int Contents;
    public int SurfaceFlags;

    /// <summary>Brush-wide shader/texture name (DP <c>colbrushf_t.texture-&gt;name</c>). Used as the
    /// hit-texture for an edge-cross separating axis (DP collision.c:693) and to fill
    /// TraceResult.DpHitTextureName. Null for a generated box brush (the moving trace box).</summary>
    public string? Texture;

    // Cached AABB of the brush (for broadphase + grid linking).
    public Vector3 Mins;
    public Vector3 Maxs;

    /// <summary>True when the brush is axis-aligned and its planes already cover AABB separation (DP hasaabbplanes).</summary>
    public bool IsAabb;

    public Brush(BrushPlane[] sides, Vector3[] points, Vector3[] edgeDirs, int contents, int surfaceFlags = 0, bool isAabb = false, string? texture = null)
    {
        Sides = sides;
        Points = points;
        EdgeDirs = edgeDirs;
        Contents = contents;
        SurfaceFlags = surfaceFlags;
        IsAabb = isAabb;
        Texture = texture;
        ComputeBounds();
    }

    public void ComputeBounds()
    {
        if (Points.Length == 0)
        {
            Mins = Maxs = Vector3.Zero;
            return;
        }
        Vector3 lo = Points[0], hi = Points[0];
        for (int i = 1; i < Points.Length; i++)
        {
            lo = Vector3.Min(lo, Points[i]);
            hi = Vector3.Max(hi, Points[i]);
        }
        Mins = lo;
        Maxs = hi;
    }

    /// <summary>Whether <paramref name="p"/> lies inside (or on the surface of) the brush.</summary>
    public bool ContainsPoint(Vector3 p)
    {
        for (int i = 0; i < Sides.Length; i++)
        {
            // outside if in front of any plane
            if (Vector3.Dot(Sides[i].Normal, p) - Sides[i].Dist > Collision.PlaneDistEpsilon)
                return false;
        }
        return true;
    }

    // -------------------------------------------------------------------------------------------
    // Box brushes (DP Collision_BrushForBox, collision.c:1141). The moving trace box and entity
    // bounding boxes are represented as 6-plane / 8-point / 3-edgedir AABB brushes. A degenerate
    // box (mins==maxs) is a single point (numplanes/edgedirs = 0).
    // -------------------------------------------------------------------------------------------

    public static readonly Vector3[] BoxEdgeDirs =
    {
        new(1, 0, 0),
        new(0, 1, 0),
        new(0, 0, 1),
    };

    /// <summary>
    /// Build an axis-aligned box brush spanning [mins,maxs] (already in world space). Port of DP
    /// <c>Collision_BrushForBox</c> (collision.c:1141). <paramref name="texture"/> is the box's shader name;
    /// the moving trace box passes null exactly like DP's SV_TraceBox box (which has a NULL texture), but an
    /// entity AABB body brush can carry a name so its hit reports a texture.
    /// </summary>
    public static Brush FromBox(Vector3 mins, Vector3 maxs, int contents = 0, int surfaceFlags = 0, string? texture = null)
    {
        if (mins == maxs)
        {
            // point brush
            return new Brush(
                System.Array.Empty<BrushPlane>(),
                new[] { mins },
                System.Array.Empty<Vector3>(),
                contents, surfaceFlags, isAabb: true, texture: texture);
        }

        var points = new Vector3[8];
        points[0] = new Vector3(mins.X, mins.Y, mins.Z);
        points[1] = new Vector3(maxs.X, mins.Y, mins.Z);
        points[2] = new Vector3(mins.X, maxs.Y, mins.Z);
        points[3] = new Vector3(maxs.X, maxs.Y, mins.Z);
        points[4] = new Vector3(mins.X, mins.Y, maxs.Z);
        points[5] = new Vector3(maxs.X, mins.Y, maxs.Z);
        points[6] = new Vector3(mins.X, maxs.Y, maxs.Z);
        points[7] = new Vector3(maxs.X, maxs.Y, maxs.Z);

        // DP plane order/signs (collision.c:1180): -X,+X,-Y,+Y,-Z,+Z with dist = ±mins/maxs.
        // Per-plane texture mirrors DP (collision.c:1189: boxbrush->brush.planes[i].texture = texture).
        var planes = new[]
        {
            new BrushPlane(new Vector3(-1, 0, 0), -mins.X, surfaceFlags, contents, texture),
            new BrushPlane(new Vector3( 1, 0, 0),  maxs.X, surfaceFlags, contents, texture),
            new BrushPlane(new Vector3( 0,-1, 0), -mins.Y, surfaceFlags, contents, texture),
            new BrushPlane(new Vector3( 0, 1, 0),  maxs.Y, surfaceFlags, contents, texture),
            new BrushPlane(new Vector3( 0, 0,-1), -mins.Z, surfaceFlags, contents, texture),
            new BrushPlane(new Vector3( 0, 0, 1),  maxs.Z, surfaceFlags, contents, texture),
        };

        return new Brush(planes, points, BoxEdgeDirs, contents, surfaceFlags, isAabb: true, texture: texture);
    }

    /// <summary>
    /// Refill an existing AABB box brush IN PLACE (no allocation) — the pooled-brush path for the per-candidate
    /// entity box in <c>ClipToEntities</c>, which would otherwise allocate a fresh Brush + Vector3[8] +
    /// BrushPlane[6] per solid entity per trace (the dominant sim.move GC churn under bot load). Requires
    /// <paramref name="b"/> to already be a non-degenerate box brush (8 points, 6 planes — e.g. built once by
    /// <see cref="FromBox"/>); it reuses those arrays. Mirrors FromBox's plane order/signs exactly, with no
    /// surface flags / texture (an entity body box, like the moving trace box).
    /// </summary>
    public static void RefillBox(Brush b, Vector3 mins, Vector3 maxs, int contents)
    {
        Vector3[] p = b.Points;
        p[0] = new Vector3(mins.X, mins.Y, mins.Z);
        p[1] = new Vector3(maxs.X, mins.Y, mins.Z);
        p[2] = new Vector3(mins.X, maxs.Y, mins.Z);
        p[3] = new Vector3(maxs.X, maxs.Y, mins.Z);
        p[4] = new Vector3(mins.X, mins.Y, maxs.Z);
        p[5] = new Vector3(maxs.X, mins.Y, maxs.Z);
        p[6] = new Vector3(mins.X, maxs.Y, maxs.Z);
        p[7] = new Vector3(maxs.X, maxs.Y, maxs.Z);

        BrushPlane[] s = b.Sides;
        s[0] = new BrushPlane(new Vector3(-1, 0, 0), -mins.X, 0, contents, null);
        s[1] = new BrushPlane(new Vector3( 1, 0, 0),  maxs.X, 0, contents, null);
        s[2] = new BrushPlane(new Vector3( 0,-1, 0), -mins.Y, 0, contents, null);
        s[3] = new BrushPlane(new Vector3( 0, 1, 0),  maxs.Y, 0, contents, null);
        s[4] = new BrushPlane(new Vector3( 0, 0,-1), -mins.Z, 0, contents, null);
        s[5] = new BrushPlane(new Vector3( 0, 0, 1),  maxs.Z, 0, contents, null);

        b.Contents = contents;
        b.SurfaceFlags = 0;
        b.Texture = null;
        b.IsAabb = true;
        b.Mins = mins;   // an AABB box's bounds ARE its mins/maxs (skip the ComputeBounds scan)
        b.Maxs = maxs;
    }

    /// <summary>
    /// Port of <c>Collision_TransformBrush</c> (collision.c:1411): return a copy of this brush with every
    /// plane / point / edge direction carried through <paramref name="m"/> (a rigid local→world or
    /// world→local transform). The result is no longer axis-aligned (DP clears <c>isaabb</c>/
    /// <c>hasaabbplanes</c>), so the SAT sweep must test the full plane + edge-cross axis set against it.
    /// Bounds are recomputed from the transformed points. Plane normals are transformed by the rotation and
    /// the plane distance shifted by the translation (<c>Matrix4x4_TransformPositivePlane</c>, scale 1).
    /// </summary>
    internal Brush Transform(in EntityMatrix m)
    {
        var sides = new BrushPlane[Sides.Length];
        for (int i = 0; i < Sides.Length; i++)
        {
            (Vector3 n, float d) = m.TransformPositivePlane(Sides[i].Normal, Sides[i].Dist);
            sides[i] = new BrushPlane(n, d, Sides[i].SurfaceFlags, Sides[i].Contents, Sides[i].Texture);
        }

        var pts = new Vector3[Points.Length];
        for (int i = 0; i < Points.Length; i++)
            pts[i] = m.TransformPoint(Points[i]);

        var edges = new Vector3[EdgeDirs.Length];
        for (int i = 0; i < EdgeDirs.Length; i++)
            edges[i] = m.TransformDirection(EdgeDirs[i]);

        // isAabb: false — rotation breaks the axis-aligned fast path (DP brush->isaabb = false).
        return new Brush(sides, pts, edges, Contents, SurfaceFlags, isAabb: false, texture: Texture);
    }
}

/// <summary>
/// Shared collision constants and the static map geometry container with a broadphase.
///
/// The broadphase is a 2D uniform grid over the XY footprint of the world bounds — a simplified port
/// of DP's area grid (world.c <c>World_LinkEdict_AreaGrid</c> / <c>World_EntitiesInBox</c>): brushes
/// are bucketed into the grid cells their AABB overlaps; a query gathers candidates from the
/// overlapped cells with an epoch counter to dedup brushes that span several cells. Brushes whose
/// footprint exceeds the grid (or fall outside it) go on an "outside" list always scanned.
/// </summary>
public static class Collision
{
    // DP collision.c constants.
    public const float SnapScale = 32.0f;
    public const float PlaneDistEpsilon = 2.0f / SnapScale;     // COLLISION_PLANE_DIST_EPSILON
    public const float ImpactNudge = 0.03125f;                  // collision_impactnudge: back off from impact
    public const float EdgeCrossMinLength2 = 1.0f / (SnapScale * SnapScale * SnapScale); // COLLISION_EDGECROSS_MINLENGTH2 approx
}

/// <summary>The static map collision geometry plus broadphase. One per loaded map.</summary>
public sealed class CollisionWorld
{
    private readonly List<Brush> _brushes = new();

    // Uniform 2D grid broadphase (port of the area-grid idea, world.c).
    private const int GridDim = 128;                 // AREA_GRID (128x128)
    private List<int>[]? _cells;                     // GridDim*GridDim buckets of brush indices
    private readonly List<int> _outside = new();     // brushes too large / outside the grid
    private Vector3 _worldMins, _worldMaxs;
    private float _scaleX, _scaleY;
    private float _biasX, _biasY;
    private bool _gridBuilt;

    // Epoch dedup for queries (DP areagrid_marknumber).
    private int[]? _mark;
    private int _markNumber;

    public IReadOnlyList<Brush> Brushes => _brushes;

    public Vector3 WorldMins => _worldMins;
    public Vector3 WorldMaxs => _worldMaxs;

    public void AddBrush(Brush b)
    {
        _brushes.Add(b);
        _gridBuilt = false;
    }

    public void AddBrushes(IEnumerable<Brush> brushes)
    {
        _brushes.AddRange(brushes);
        _gridBuilt = false;
    }

    public void Clear()
    {
        _brushes.Clear();
        _gridBuilt = false;
    }

    /// <summary>
    /// (Re)build the broadphase grid sized to the world bounds. Call after the brush set is finalized;
    /// <see cref="Query"/> builds it lazily if needed. Mirrors World_SetSize + World_LinkEdict_AreaGrid.
    /// </summary>
    public void BuildGrid()
    {
        if (_brushes.Count == 0)
        {
            _worldMins = _worldMaxs = Vector3.Zero;
            _cells = null;
            _outside.Clear();
            _gridBuilt = true;
            return;
        }

        // world bounds = union of all brush bounds
        Vector3 lo = _brushes[0].Mins, hi = _brushes[0].Maxs;
        for (int i = 1; i < _brushes.Count; i++)
        {
            lo = Vector3.Min(lo, _brushes[i].Mins);
            hi = Vector3.Max(hi, _brushes[i].Maxs);
        }
        _worldMins = lo;
        _worldMaxs = hi;

        // DP: areagrid_size = max(extent, AREA_GRID*mingridsize); scale = AREA_GRID/size; bias = -mins (+half-cell)
        float sizeX = MathF.Max(hi.X - lo.X, 1.0f);
        float sizeY = MathF.Max(hi.Y - lo.Y, 1.0f);
        _scaleX = GridDim / sizeX;
        _scaleY = GridDim / sizeY;
        _biasX = -lo.X;
        _biasY = -lo.Y;

        _cells = new List<int>[GridDim * GridDim];
        _outside.Clear();
        _mark = new int[_brushes.Count];
        _markNumber = 0;

        for (int bi = 0; bi < _brushes.Count; bi++)
            LinkBrush(bi, _brushes[bi]);

        _gridBuilt = true;
    }

    private void LinkBrush(int index, Brush b)
    {
        GridRange(b.Mins, b.Maxs, out int x0, out int y0, out int x1, out int y1, out bool outside);
        if (outside)
        {
            _outside.Add(index);
            return;
        }
        for (int y = y0; y < y1; y++)
        {
            int row = y * GridDim;
            for (int x = x0; x < x1; x++)
            {
                int cell = row + x;
                (_cells![cell] ??= new List<int>()).Add(index);
            }
        }
    }

    // Convert an AABB to inclusive-exclusive grid cell range; report 'outside' if it spills the grid.
    private void GridRange(Vector3 mins, Vector3 maxs, out int x0, out int y0, out int x1, out int y1, out bool outside)
    {
        x0 = (int)MathF.Floor((mins.X + _biasX) * _scaleX);
        y0 = (int)MathF.Floor((mins.Y + _biasY) * _scaleY);
        x1 = (int)MathF.Floor((maxs.X + _biasX) * _scaleX) + 1;
        y1 = (int)MathF.Floor((maxs.Y + _biasY) * _scaleY) + 1;

        outside = x0 < 0 || y0 < 0 || x1 > GridDim || y1 > GridDim;
        // Always clamp to the grid — callers rely on the clamped range even when 'outside' is reported. LinkBrush
        // sends a spilling brush to _outside; Query scans the clamped cells PLUS _outside, which is candidate-
        // identical to a full scan because every in-grid brush is linked to all cells it overlaps (see Query).
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > GridDim) x1 = GridDim;
        if (y1 > GridDim) y1 = GridDim;
    }

    /// <summary>
    /// Gather candidate brushes whose AABB overlaps [mins,maxs], appending into <paramref name="result"/>.
    /// Uses the grid when built, otherwise brute-forces (still correct, just slower).
    /// </summary>
    public void Query(Vector3 mins, Vector3 maxs, List<Brush> result)
    {
        if (!_gridBuilt) BuildGrid();

        if (_cells == null)
        {
            // brute force (no grid — e.g. empty or degenerate world)
            for (int i = 0; i < _brushes.Count; i++)
            {
                var b = _brushes[i];
                if (BoxesOverlap(mins, maxs, b.Mins, b.Maxs))
                    result.Add(b);
            }
            return;
        }

        _markNumber++;
        int mark = _markNumber;

        // always-scan outside list
        for (int i = 0; i < _outside.Count; i++)
        {
            int idx = _outside[i];
            if (_mark![idx] == mark) continue;
            _mark[idx] = mark;
            var b = _brushes[idx];
            if (BoxesOverlap(mins, maxs, b.Mins, b.Maxs))
                result.Add(b);
        }

        // (perf 2026-06-14) A query box that SPILLS the grid — any long ray: weapon hitscan, AI line-of-sight,
        // and the 32768-qu crosshair true-aim ray — used to brute-force EVERY brush in the map here, making the
        // trace O(total brushes) and dominating frame time on brush-dense maps (catharsis: ~3 ms per such ray).
        // It suffices to scan the CLAMPED grid range plus _outside (already scanned just above): every brush is
        // either in _outside (it spilled the grid at link time) or linked to ALL in-grid cells it overlaps, so the
        // clamped scan yields the IDENTICAL candidate set the old full scan did — verified by the BspCollision /
        // differential trace suites. GridRange now always returns the clamped range.
        GridRange(mins, maxs, out int x0, out int y0, out int x1, out int y1, out _);

        for (int y = y0; y < y1; y++)
        {
            int row = y * GridDim;
            for (int x = x0; x < x1; x++)
            {
                var bucket = _cells[row + x];
                if (bucket == null) continue;
                for (int k = 0; k < bucket.Count; k++)
                {
                    int idx = bucket[k];
                    if (_mark![idx] == mark) continue;
                    _mark[idx] = mark;
                    var b = _brushes[idx];
                    if (BoxesOverlap(mins, maxs, b.Mins, b.Maxs))
                        result.Add(b);
                }
            }
        }
    }

    /// <summary>AABB overlap test (DP BoxesOverlap).</summary>
    public static bool BoxesOverlap(Vector3 amins, Vector3 amaxs, Vector3 bmins, Vector3 bmaxs)
        => amins.X <= bmaxs.X && amaxs.X >= bmins.X
        && amins.Y <= bmaxs.Y && amaxs.Y >= bmins.Y
        && amins.Z <= bmaxs.Z && amaxs.Z >= bmins.Z;

    /// <summary>
    /// Gather candidates for a SWEPT box move [start→end] expanded by [boxMins,boxMaxs] — the long-trace
    /// counterpart of <see cref="Query"/>. A long DIAGONAL trace's enclosing AABB covers O(length²) grid
    /// cells: the 2026-06-14 clamp bounded the scan, but a cross-map shot still visited half the grid and
    /// handed every brush under that rectangle to the narrowphase — the catharsis melts (a long shotgun
    /// blast = 14 penetrating pellet traces = 70–305 ms in <c>wf.shotgun</c>; the <c>hud.trueaim</c> box
    /// trace, bot line-of-sight — all the same rectangle). DP never sees this: its world trace recurses
    /// the BSP tree and only touches the ray corridor. Here we march the sweep in cell-sized segments and
    /// union each segment's small rectangle query. Correct by construction: the swept volume is contained
    /// in the union of the per-segment AABBs, so every brush the sweep can touch overlaps at least one
    /// segment's query box (the epoch mark dedups brushes shared by adjacent segments); the narrowphase
    /// then clips exactly as before. Cost: O(cells along the line), not O(width × height).
    /// Short sweeps (≤ ~3 cells of XY travel — every normal movement step) take the plain rectangle path.
    /// </summary>
    public void QuerySwept(Vector3 start, Vector3 end, Vector3 boxMins, Vector3 boxMaxs, List<Brush> result)
    {
        if (!_gridBuilt) BuildGrid();

        // DP's world gather pads the sweep box by ±1 (see TraceService.SweptBounds, which the entity
        // broadphase still uses). Fold the pad in here so every path below keeps that slop; it also
        // covers the float drift the segment march accumulates (≤ ~0.5 over a max-range trace).
        boxMins -= Vector3.One;
        boxMaxs += Vector3.One;

        Vector3 delta = end - start;
        float cellX = _scaleX > 0f ? 1f / _scaleX : 0f;   // world units per grid cell
        float cellY = _scaleY > 0f ? 1f / _scaleY : 0f;
        float step = MathF.Max(cellX, cellY);
        float xyTravel = MathF.Max(MathF.Abs(delta.X), MathF.Abs(delta.Y));

        if (_cells == null || step <= 0f || xyTravel <= step * 3f)
        {
            // Short (or gridless) — the rectangle is already tight; keep the proven path.
            Vector3 qmins = new(
                MathF.Min(start.X, end.X) + boxMins.X,
                MathF.Min(start.Y, end.Y) + boxMins.Y,
                MathF.Min(start.Z, end.Z) + boxMins.Z);
            Vector3 qmaxs = new(
                MathF.Max(start.X, end.X) + boxMaxs.X,
                MathF.Max(start.Y, end.Y) + boxMaxs.Y,
                MathF.Max(start.Z, end.Z) + boxMaxs.Z);
            Query(qmins, qmaxs, result);
            return;
        }

        _markNumber++;
        int mark = _markNumber;

        // MARK SEMANTICS (fixed 2026-07-27): the mark means "already IN result", NOT "already looked at".
        // Query can mark on sight because it has ONE test box — every brush is tested against it exactly once.
        // QuerySwept has one box PER SEGMENT, so marking on sight pins each brush to the verdict of the first
        // segment that happens to reach its cell: a brush that fails THAT segment's box but overlaps a LATER
        // one was never re-tested and silently vanished from the candidate set, so the narrowphase never clipped
        // it and the trace passed through solid geometry. Cells are XY buckets spanning all Z, and a segment's
        // box covers up to 4 cells, so an early segment routinely reaches a cell it is still far from in Z —
        // e.g. sweeping (0,0,0)→(200,0,2000) past a brush at x[100,128] z[1200,1300]: segment 1 reaches its cell
        // and fails on Z, segment 2 genuinely hits it and skipped it. Long traces with real Z travel (hitscan up
        // or down a height change, bot LOS, crosshair true-aim) are exactly the case this broadphase exists for.
        // Re-testing an unadded brush per overlapping segment is a handful of AABB compares — pay it.

        // The always-scan outside list, tested against the FULL sweep AABB (same as Query would). A brush that
        // fails THIS box cannot overlap any segment box (every segment box is contained in the full one), so
        // leaving it unmarked costs at most a repeat compare below and keeps one meaning for the mark.
        Vector3 fullMins = new(
            MathF.Min(start.X, end.X) + boxMins.X,
            MathF.Min(start.Y, end.Y) + boxMins.Y,
            MathF.Min(start.Z, end.Z) + boxMins.Z);
        Vector3 fullMaxs = new(
            MathF.Max(start.X, end.X) + boxMaxs.X,
            MathF.Max(start.Y, end.Y) + boxMaxs.Y,
            MathF.Max(start.Z, end.Z) + boxMaxs.Z);
        for (int i = 0; i < _outside.Count; i++)
        {
            int idx = _outside[i];
            if (_mark![idx] == mark) continue;
            var b = _brushes[idx];
            if (BoxesOverlap(fullMins, fullMaxs, b.Mins, b.Maxs))
            {
                _mark[idx] = mark;
                result.Add(b);
            }
        }

        // March cell-sized segments along the sweep; each segment queries its own small rectangle.
        int segs = (int)(xyTravel / step) + 1;
        Vector3 segDelta = delta / segs;
        Vector3 p = start;
        for (int s = 0; s < segs; s++)
        {
            Vector3 q = s == segs - 1 ? end : p + segDelta;   // land exactly on end — segDelta drift must not shorten the corridor
            Vector3 smins = new(
                MathF.Min(p.X, q.X) + boxMins.X,
                MathF.Min(p.Y, q.Y) + boxMins.Y,
                MathF.Min(p.Z, q.Z) + boxMins.Z);
            Vector3 smaxs = new(
                MathF.Max(p.X, q.X) + boxMaxs.X,
                MathF.Max(p.Y, q.Y) + boxMaxs.Y,
                MathF.Max(p.Z, q.Z) + boxMaxs.Z);
            GridRange(smins, smaxs, out int x0, out int y0, out int x1, out int y1, out _);
            for (int y = y0; y < y1; y++)
            {
                int row = y * GridDim;
                for (int x = x0; x < x1; x++)
                {
                    var bucket = _cells[row + x];
                    if (bucket == null) continue;
                    for (int k = 0; k < bucket.Count; k++)
                    {
                        int idx = bucket[k];
                        if (_mark![idx] == mark) continue;   // already in result
                        var b = _brushes[idx];
                        if (BoxesOverlap(smins, smaxs, b.Mins, b.Maxs))
                        {
                            _mark[idx] = mark;
                            result.Add(b);
                        }
                    }
                }
            }
            p = q;
        }
    }
}

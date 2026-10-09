// Port of Base/darkplaces/model_brush.c: Mod_Q1BSP_RecursiveHullCheck, Mod_Q1BSP_RecursiveHullCheckPoint,
// Mod_Q1BSP_TracePoint, Mod_Q1BSP_TraceLine, Mod_Q1BSP_TraceBox, Mod_Q1BSP_PointSuperContents,
// Mod_Q1BSP_TraceLineAgainstSurfaces (and its two helpers), Mod_Q1BSP_SuperContentsFromNativeContents,
// Mod_Q1BSP_MakeHull0, Mod_Q1BSP_RoundUpToHullSize, Mod_BSP_LightPoint.
//
// The arithmetic follows the C source type for type - which values are float and which double decides the last
// bit of a fraction, and the last bit of an end position is where the next move of a predicted player starts.
using System.Numerics;
using VortexArena.Formats.Bsp;

namespace VortexArena.Engine.Collision;

/// <summary>What one trace through a Quake 1 map model found: the fields of DarkPlaces' <c>trace_t</c> that
/// the hull code writes. Contents are the collision library's <see cref="SuperContents"/> bits.</summary>
public struct Q1HullTrace
{
    /// <summary>How far the move got, 0 to 1. A double, as in DarkPlaces.</summary>
    public double Fraction;
    public bool StartSolid, AllSolid, InOpen, InWater;
    public Vector3 PlaneNormal;
    public float PlaneDist;
    /// <summary>The contents of the last leaf looked at (or of the surface hit) - set whether or not anything
    /// was hit, as DarkPlaces leaves it.</summary>
    public int HitContents;
    public int HitSurfaceFlags;
    public string? HitTexture;
    public int StartContents;
    internal bool StartFound;
}

/// <summary>
/// Collision against a Quake 1 format map the way DarkPlaces does it: not against brushes (the format has
/// none) but against clipping hulls - binary trees whose leaves are contents, one tree for a point and one
/// for each box size the map compiler expanded the level by. A box is traced as a point through the hull
/// made for the nearest box size at or above it, offset so that the box's minimum corner sits where the
/// hull's would.
///
/// One object serves every model of the map (the world and "*N"); it holds no per-trace state, so a
/// client and a server thread may trace through the same one.
/// </summary>
public sealed class Q1HullCollision
{
    // collision.c collision_impactnudge
    private const float ImpactNudge = 0.03125f;

    // Q3SURFACEFLAG_* (bspfile.h) of the five stand-in textures of model_brush.c Mod_BrushInit
    private const int SurfaceSky = 4 | 16 | 32 | 131072 | 1024; // SKY | NOIMPACT | NOMARKS | NODLIGHT | NOLIGHTMAP
    private const int SurfaceNoMarks = 32;

    private readonly Q1BspData _bsp;
    private readonly Q1Plane[] _planes;
    private readonly Q1ClipNode[] _hull0;      // Mod_Q1BSP_MakeHull0: the node tree as a clipping hull
    private readonly Q1ClipNode[] _clipNodes;
    private readonly int[] _faceContents;
    private readonly int[] _faceSurfaceFlags;
    private readonly string[] _faceTexture;
    private readonly Vector3[] _faceNormal;

    /// <summary>mod_q1bsp_traceoutofsolid (1): a box that starts in solid is traced on, out into the open.</summary>
    public bool TraceOutOfSolid { get; set; } = true;
    /// <summary>mod_q1bsp_zero_hullsize_cutoff: a box narrower than this in X is traced as a point.
    /// DarkPlaces' default is 3; it sets 8.03125 for Xonotic (cmd.c), which is what legacy mode runs.</summary>
    public float ZeroHullSizeCutoff { get; set; } = 8.03125f;
    /// <summary>sv_gameplayfix_q1bsptracelinereportstexture (1): a line is traced against the faces of the map,
    /// so that it can name the texture it hit, instead of through hull 0.</summary>
    public bool LineReportsTexture { get; set; } = true;

    /// <param name="areaWeightedNormals">
    /// How the normal of a face is formed for the surface traceline (Mod_BuildNormals): true adds the fan's
    /// triangle normals as they are (r_smoothnormals_areaweighting 1, what a DarkPlaces client - and a listen
    /// server, which is one - does); false makes each a unit vector first, which is what a DEDICATED DarkPlaces
    /// server does, because the cvar belongs to the renderer and is never registered there, so reads 0. The
    /// two differ in the last bits of a normal, and on a face with a sliver in its fan by much more (its
    /// normal can come out as zero, and a face with a zero normal stops every line that reaches its node).
    /// </param>
    public Q1HullCollision(Q1BspData bsp, bool areaWeightedNormals = true)
    {
        _bsp = bsp ?? throw new ArgumentNullException(nameof(bsp));
        _planes = bsp.Planes;
        _clipNodes = bsp.ClipNodes;
        _hull0 = new Q1ClipNode[bsp.Nodes.Length];
        for (int i = 0; i < _hull0.Length; i++)
        {
            ref readonly Q1Node n = ref bsp.Nodes[i];
            _hull0[i] = new Q1ClipNode(n.PlaneIndex, n.Child0 >= 0 ? n.Child0 : LeafContents(n.Child0), n.Child1 >= 0 ? n.Child1 : LeafContents(n.Child1));
        }
        int faces = bsp.Faces.Length;
        _faceContents = new int[faces];
        _faceSurfaceFlags = new int[faces];
        _faceTexture = new string[faces];
        for (int i = 0; i < faces; i++)
        {
            int texture = bsp.Faces[i].TextureIndex;
            string name = texture >= 0 ? bsp.Textures[texture].Name : "NO TEXTURE FOUND";
            bool present = texture >= 0 && bsp.Textures[texture].Present;
            TextureCollision(name, present, texture == -2, out _faceContents[i], out _faceSurfaceFlags[i]);
            _faceTexture[i] = name;
        }
        _faceNormal = new Vector3[faces];
        for (int i = 0; i < faces; i++) _faceNormal[i] = areaWeightedNormals ? bsp.Faces[i].Normal : UnweightedNormal(bsp, bsp.Faces[i]);
    }

    // Mod_BuildNormals with areaweighting off: "if (!areaweighting) VectorNormalize(areaNormal)" before the sum.
    private static Vector3 UnweightedNormal(Q1BspData bsp, in Q1Face face)
    {
        Vector3 normal = default;
        Vector3[] v = bsp.FaceVertices;
        for (int t = 0; t + 2 < face.VertexCount; t++)
        {
            Vector3 a = v[face.FirstVertex], b = v[face.FirstVertex + t + 1], c = v[face.FirstVertex + t + 2];
            Vector3 n = new((a.Y - b.Y) * (c.Z - b.Z) - (a.Z - b.Z) * (c.Y - b.Y), (a.Z - b.Z) * (c.X - b.X) - (a.X - b.X) * (c.Z - b.Z), (a.X - b.X) * (c.Y - b.Y) - (a.Y - b.Y) * (c.X - b.X));
            normal += Normalized(n);
        }
        return Normalized(normal);
    }

    // VectorNormalize: "float ilength = (float)DotProduct(v, v); if (ilength) ilength = 1.0f / sqrt(ilength); v *= ilength"
    private static Vector3 Normalized(Vector3 v)
    {
        float ilength = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        if (ilength != 0) ilength = (float)(1.0 / Math.Sqrt(ilength));
        return float.IsFinite(ilength) ? new Vector3(v.X * ilength, v.Y * ilength, v.Z * ilength) : default;
    }

    private int LeafContents(int child)
    {
        int leaf = -(child + 1);
        int contents = leaf < _bsp.Leafs.Length ? _bsp.Leafs[leaf].Contents : Q1Contents.Solid;
        // Contents are negative. A leaf of a damaged file that says otherwise would be followed as a node
        // (DarkPlaces does follow it, out of its array); it is taken as empty here.
        return contents < 0 ? contents : Q1Contents.Empty;
    }

    /// <summary>The map this was built from.</summary>
    public Q1BspData Bsp => _bsp;
    public int ModelCount => _bsp.Models.Length;

    /// <summary>
    /// What a line that hits a face of this texture reports (Mod_Q1BSP_LoadTextures): a name starting with
    /// "*" is liquid ("*lava", "*slime", anything else water), "sky" is sky - and solid, "for the surface
    /// traceline we need to hit this surface as a solid" - and the rest is solid.
    /// </summary>
    public static void TextureCollision(string name, bool present, bool spareWater, out int contents, out int surfaceFlags)
    {
        surfaceFlags = 0;
        if (!present)
        {
            // the two spare textures: "NO TEXTURE FOUND" as a wall, and as water for a TEX_SPECIAL face
            contents = spareWater ? SuperContents.Water : SuperContents.Solid;
            surfaceFlags = spareWater ? SurfaceNoMarks : 0;
        }
        else if (name.StartsWith('*'))
        {
            surfaceFlags = SurfaceNoMarks;
            if (name.StartsWith("*lava", StringComparison.Ordinal)) contents = SuperContents.Lava | SuperContents.NoDrop;
            else if (name.StartsWith("*slime", StringComparison.Ordinal)) contents = SuperContents.Slime;
            else contents = SuperContents.Water;
        }
        else if (name.StartsWith("sky", StringComparison.Ordinal))
        {
            contents = SuperContents.Sky | SuperContents.NoDrop | SuperContents.Solid;
            surfaceFlags = SurfaceSky;
        }
        else contents = SuperContents.Solid;
    }

    /// <summary>Mod_Q1BSP_SuperContentsFromNativeContents, in the collision library's bits.</summary>
    public static int SuperContentsFromNative(int native) => native switch
    {
        Q1Contents.Solid => SuperContents.Solid | SuperContents.Opaque,
        Q1Contents.Water => SuperContents.Water,
        Q1Contents.Slime => SuperContents.Slime,
        Q1Contents.Lava => SuperContents.Lava | SuperContents.NoDrop,
        Q1Contents.Sky => SuperContents.Sky | SuperContents.NoDrop | SuperContents.Opaque, // "to match behaviour of Q3 maps, let sky count as opaque"
        _ => 0,
    };

    /// <summary>Mod_Q1BSP_NativeContentsFromSuperContents.</summary>
    public static int NativeFromSuperContents(int contents)
    {
        if ((contents & (SuperContents.Solid | SuperContents.Body)) != 0) return Q1Contents.Solid;
        if ((contents & SuperContents.Sky) != 0) return Q1Contents.Sky;
        if ((contents & SuperContents.Lava) != 0) return Q1Contents.Lava;
        if ((contents & SuperContents.Slime) != 0) return Q1Contents.Slime;
        if ((contents & SuperContents.Water) != 0) return Q1Contents.Water;
        return Q1Contents.Empty;
    }

    /// <summary>
    /// The hull a box of this size is traced through (the choice in Mod_Q1BSP_TraceBox): 0 for one narrower
    /// than <see cref="ZeroHullSizeCutoff"/>; on a Quake map 1 up to 32 units wide ("a minor tolerance (the .1)
    /// because of minor float precision errors from the box being transformed around") and 2 beyond; on a
    /// Half-Life map 3 (crouched) or 1 by height up to 32 wide, and 2 beyond.
    /// </summary>
    public int HullForTrace(Vector3 boxMins, Vector3 boxMaxs)
    {
        // VectorSubtract(boxmaxs, boxmins, boxsize): a single precision difference, kept in a double
        double sizeX = boxMaxs.X - boxMins.X, sizeZ = boxMaxs.Z - boxMins.Z;
        if (sizeX < ZeroHullSizeCutoff) return 0;
        if (_bsp.IsHalfLife) return sizeX < 32.1 ? (sizeZ < 54 ? 3 : 1) : 2;
        return sizeX < 32.1 ? 1 : 2;
    }

    /// <summary>Mod_Q1BSP_RoundUpToHullSize: the box an entity really collides as (setsize on a Quake map uses it).</summary>
    public void RoundUpToHullSize(Vector3 inMins, Vector3 inMaxs, out Vector3 outMins, out Vector3 outMaxs)
    {
        Vector3 size = inMaxs - inMins;
        int hull;
        if (size.X < ZeroHullSizeCutoff) hull = 0;
        else if (_bsp.IsHalfLife) hull = size.X <= 32 ? (size.Z < 54 ? 3 : 1) : 2;
        else hull = size.X <= 32 ? 1 : 2;
        (Vector3 mins, Vector3 maxs) = _bsp.HullSizes[hull];
        outMins = inMins;
        outMaxs = inMins + (maxs - mins);
    }

    private Q1ClipNode[] HullNodes(int hull) => hull == 0 ? _hull0 : _clipNodes;

    private int HeadNode(int model, int hull)
    {
        int head = _bsp.Models[model].HeadNode(hull);
        // a head node past the end cannot be followed (the reader refuses such a file; this is for data built by hand)
        return head >= HullNodes(hull).Length ? Q1Contents.Solid : head;
    }

    // ---- the trace --------------------------------------------------------------------------------------

    private struct Check
    {
        public Q1ClipNode[] Nodes;
        public double StartX, StartY, StartZ, EndX, EndY, EndZ, DistX, DistY, DistZ;
        public int HitMask;
        public bool TraceOutOfSolid;
    }

    private const int StateEmpty = 0, StateSolid = 1, StateDone = 2;

    /// <summary>
    /// Mod_Q1BSP_TraceBox on model <paramref name="model"/> (0 is the world), in the model's own space: a box
    /// of no size is a line, a line of no length a point.
    /// </summary>
    /// <param name="hitMask">The <see cref="SuperContents"/> that stop the move.</param>
    public void TraceBox(int model, Vector3 start, Vector3 boxMins, Vector3 boxMaxs, Vector3 end, int hitMask, out Q1HullTrace trace)
    {
        trace = new Q1HullTrace { Fraction = 1, AllSolid = true };
        if ((uint)model >= (uint)_bsp.Models.Length) { trace.AllSolid = false; return; }
        if (boxMins == boxMaxs)
        {
            if (start == end) TracePoint(model, start, hitMask, ref trace);
            else TraceLine(model, start, end, hitMask, ref trace);
            return;
        }
        int hull = HullForTrace(boxMins, boxMaxs);
        Vector3 clipMins = _bsp.HullSizes[hull].Mins;
        // VectorMAMAM(1, start, 1, boxmins, -1, hull->clip_mins, rhc.start): single precision, then widened
        Check t = new() { Nodes = HullNodes(hull), HitMask = hitMask, TraceOutOfSolid = TraceOutOfSolid };
        t.StartX = start.X + boxMins.X + -1 * clipMins.X;
        t.StartY = start.Y + boxMins.Y + -1 * clipMins.Y;
        t.StartZ = start.Z + boxMins.Z + -1 * clipMins.Z;
        t.EndX = end.X + boxMins.X + -1 * clipMins.X;
        t.EndY = end.Y + boxMins.Y + -1 * clipMins.Y;
        t.EndZ = end.Z + boxMins.Z + -1 * clipMins.Z;
        t.DistX = t.EndX - t.StartX;
        t.DistY = t.EndY - t.StartY;
        t.DistZ = t.EndZ - t.StartZ;
        int head = HeadNode(model, hull);
        if (t.DistX * t.DistX + t.DistY * t.DistY + t.DistZ * t.DistZ != 0)
            RecursiveHullCheck(ref t, ref trace, head, 0, 1, t.StartX, t.StartY, t.StartZ, t.EndX, t.EndY, t.EndZ);
        else
            RecursiveHullCheckPoint(ref t, ref trace, head);
    }

    // Mod_Q1BSP_TracePoint. (It leaves the caller's mask out of the trace: the mask it tests is the zero the
    // memset left, so a point is never "in solid" by this function - DarkPlaces as it is.)
    private void TracePoint(int model, Vector3 start, int hitMask, ref Q1HullTrace trace)
    {
        _ = hitMask;
        Check t = new() { Nodes = _hull0, HitMask = 0, TraceOutOfSolid = TraceOutOfSolid };
        t.StartX = t.EndX = start.X;
        t.StartY = t.EndY = start.Y;
        t.StartZ = t.EndZ = start.Z;
        RecursiveHullCheckPoint(ref t, ref trace, HeadNode(model, 0));
    }

    // Mod_Q1BSP_TraceLine
    private void TraceLine(int model, Vector3 start, Vector3 end, int hitMask, ref Q1HullTrace trace)
    {
        Check t = new() { Nodes = _hull0, HitMask = hitMask, TraceOutOfSolid = TraceOutOfSolid };
        t.StartX = start.X; t.StartY = start.Y; t.StartZ = start.Z;
        t.EndX = end.X; t.EndY = end.Y; t.EndZ = end.Z;
        t.DistX = t.EndX - t.StartX;
        t.DistY = t.EndY - t.StartY;
        t.DistZ = t.EndZ - t.StartZ;
        int head = HeadNode(model, 0);
        if (LineReportsTexture)
        {
            // Mod_Q1BSP_TraceLineAgainstSurfaces
            if (head >= 0) SurfaceLineNode(ref t, ref trace, head, t.StartX, t.StartY, t.StartZ, t.EndX, t.EndY, t.EndZ);
            else SurfaceLineLeaf(ref t, ref trace, head);
            return;
        }
        if (t.DistX * t.DistX + t.DistY * t.DistY + t.DistZ * t.DistZ != 0)
            RecursiveHullCheck(ref t, ref trace, head, 0, 1, t.StartX, t.StartY, t.StartZ, t.EndX, t.EndY, t.EndZ);
        else
            RecursiveHullCheckPoint(ref t, ref trace, head);
    }

    private int RecursiveHullCheck(ref Check t, ref Q1HullTrace trace, int num, double p1f, double p2f,
        double p1x, double p1y, double p1z, double p2x, double p2y, double p2z)
    {
        Q1ClipNode[] nodes = t.Nodes;
        Q1Plane[] planes = _planes;
        // "keep looping until we hit a leaf"
        while (num >= 0)
        {
            ref readonly Q1ClipNode node = ref nodes[num];
            ref readonly Q1Plane plane = ref planes[node.PlaneIndex];
            double t1, t2;
            // "axial planes can be calculated more quickly without the DotProduct"
            if (plane.Type < 3)
            {
                t1 = (plane.Type == 0 ? p1x : plane.Type == 1 ? p1y : p1z) - plane.Dist;
                t2 = (plane.Type == 0 ? p2x : plane.Type == 1 ? p2y : p2z) - plane.Dist;
            }
            else
            {
                t1 = plane.Normal.X * p1x + plane.Normal.Y * p1y + plane.Normal.Z * p1z - plane.Dist;
                t2 = plane.Normal.X * p2x + plane.Normal.Y * p2y + plane.Normal.Z * p2z - plane.Dist;
            }
            // "negative plane distances indicate children[1] (behind plane)"
            bool p1side = t1 < 0, p2side = t2 < 0;
            if (p1side == p2side)
            {
                num = p1side ? node.Child1 : node.Child0;
                continue;
            }

            // "find the midpoint where the line crosses the plane, use the original line for best accuracy"
            if (plane.Type < 3)
            {
                t1 = (plane.Type == 0 ? t.StartX : plane.Type == 1 ? t.StartY : t.StartZ) - plane.Dist;
                t2 = (plane.Type == 0 ? t.EndX : plane.Type == 1 ? t.EndY : t.EndZ) - plane.Dist;
            }
            else
            {
                t1 = plane.Normal.X * t.StartX + plane.Normal.Y * t.StartY + plane.Normal.Z * t.StartZ - plane.Dist;
                t2 = plane.Normal.X * t.EndX + plane.Normal.Y * t.EndY + plane.Normal.Z * t.EndZ - plane.Dist;
            }
            double midf = t1 / (t1 - t2);
            // bound(p1f, midf, p2f): ((midf) < (p1f) ? (p1f) : ((midf) >= (p2f) ? (p2f) : (midf))) - a NaN goes through
            midf = midf < p1f ? p1f : midf >= p2f ? p2f : midf;
            double midx = t.StartX + midf * t.DistX, midy = t.StartY + midf * t.DistY, midz = t.StartZ + midf * t.DistZ;

            // "recurse both sides, front side first"
            int ret = RecursiveHullCheck(ref t, ref trace, p1side ? node.Child1 : node.Child0, p1f, midf, p1x, p1y, p1z, midx, midy, midz);
            // "if this side is not empty, return what it is (solid or done)"
            if (ret != StateEmpty && (!trace.AllSolid || !t.TraceOutOfSolid)) return ret;

            ret = RecursiveHullCheck(ref t, ref trace, p2side ? node.Child1 : node.Child0, midf, p2f, midx, midy, midz, p2x, p2y, p2z);
            // "if other side is not solid, return what it is (empty or done)"
            if (ret != StateSolid) return ret;

            // "front is air and back is solid, this is the impact point..."
            if (p1side)
            {
                trace.PlaneDist = -plane.Dist;
                trace.PlaneNormal = -plane.Normal;
            }
            else
            {
                trace.PlaneDist = plane.Dist;
                trace.PlaneNormal = plane.Normal;
            }
            // "calculate the return fraction which is nudged off the surface a bit"
            t1 = trace.PlaneNormal.X * t.StartX + trace.PlaneNormal.Y * t.StartY + trace.PlaneNormal.Z * t.StartZ - trace.PlaneDist;
            t2 = trace.PlaneNormal.X * t.EndX + trace.PlaneNormal.Y * t.EndY + trace.PlaneNormal.Z * t.EndZ - trace.PlaneDist;
            midf = (t1 - ImpactNudge) / (t1 - t2);
            trace.Fraction = midf < 0 ? 0 : midf >= 1 ? 1 : midf;
            return StateDone;
        }

        // "we reached a leaf contents"
        int contents = SuperContentsFromNative(num);
        if (!trace.StartFound)
        {
            trace.StartFound = true;
            trace.StartContents |= contents;
        }
        if ((contents & SuperContents.LiquidsMask) != 0) trace.InWater = true;
        if (contents == 0) trace.InOpen = true;
        if ((contents & SuperContents.Solid) != 0) { trace.HitTexture = "solid"; trace.HitSurfaceFlags = 0; }
        else if ((contents & SuperContents.Sky) != 0) { trace.HitTexture = "sky"; trace.HitSurfaceFlags = SurfaceSky; }
        else if ((contents & SuperContents.Lava) != 0) { trace.HitTexture = "*lava"; trace.HitSurfaceFlags = SurfaceNoMarks; }
        else if ((contents & SuperContents.Slime) != 0) { trace.HitTexture = "*slime"; trace.HitSurfaceFlags = SurfaceNoMarks; }
        else { trace.HitTexture = "*water"; trace.HitSurfaceFlags = SurfaceNoMarks; }
        trace.HitContents = contents;
        if ((contents & t.HitMask) != 0)
        {
            // "if the first leaf is solid, set startsolid"
            if (trace.AllSolid) trace.StartSolid = true;
            return StateSolid;
        }
        trace.AllSolid = false;
        return StateEmpty;
    }

    private void RecursiveHullCheckPoint(ref Check t, ref Q1HullTrace trace, int num)
    {
        // VectorCopy(t->start, point): the point is tested in single precision
        float x = (float)t.StartX, y = (float)t.StartY, z = (float)t.StartZ;
        int contents = SuperContentsFromNative(PointLeaf(t.Nodes, num, x, y, z));
        trace.StartContents |= contents;
        if ((contents & SuperContents.LiquidsMask) != 0) trace.InWater = true;
        if (contents == 0) trace.InOpen = true;
        trace.AllSolid = trace.StartSolid = (contents & t.HitMask) != 0;
    }

    private int PointLeaf(Q1ClipNode[] nodes, int num, float x, float y, float z)
    {
        Q1Plane[] planes = _planes;
        while (num >= 0)
        {
            ref readonly Q1ClipNode node = ref nodes[num];
            ref readonly Q1Plane plane = ref planes[node.PlaneIndex];
            float d = plane.Type < 3 ? (plane.Type == 0 ? x : plane.Type == 1 ? y : z) : plane.Normal.X * x + plane.Normal.Y * y + plane.Normal.Z * z;
            num = d < plane.Dist ? node.Child1 : node.Child0;
        }
        return num;
    }

    /// <summary>Mod_Q1BSP_PointSuperContents: the contents of the leaf of hull 0 the point is in.</summary>
    public int PointContents(int model, Vector3 point)
    {
        if ((uint)model >= (uint)_bsp.Models.Length) return 0;
        return SuperContentsFromNative(PointLeaf(_hull0, HeadNode(model, 0), point.X, point.Y, point.Z));
    }

    /// <summary>The native contents (CONTENTS_*) at a point of model <paramref name="model"/>.</summary>
    public int PointNativeContents(int model, Vector3 point)
    {
        if ((uint)model >= (uint)_bsp.Models.Length) return Q1Contents.Empty;
        return PointLeaf(_hull0, HeadNode(model, 0), point.X, point.Y, point.Z);
    }

    // ---- Mod_Q1BSP_TraceLineAgainstSurfaces ---------------------------------------------------------------

    // Mod_Q1BSP_TraceLineAgainstSurfacesRecursiveBSPNode on a node (num >= 0)
    private int SurfaceLineNode(ref Check t, ref Q1HullTrace trace, int num, double p1x, double p1y, double p1z, double p2x, double p2y, double p2z)
    {
        Q1Plane[] planes = _planes;
        while (num >= 0)
        {
            ref readonly Q1ClipNode node = ref _hull0[num];
            ref readonly Q1Plane plane = ref planes[node.PlaneIndex];
            double t1, t2;
            if (plane.Type < 3)
            {
                t1 = (plane.Type == 0 ? p1x : plane.Type == 1 ? p1y : p1z) - plane.Dist;
                t2 = (plane.Type == 0 ? p2x : plane.Type == 1 ? p2y : p2z) - plane.Dist;
            }
            else
            {
                t1 = plane.Normal.X * p1x + plane.Normal.Y * p1y + plane.Normal.Z * p1z - plane.Dist;
                t2 = plane.Normal.X * p2x + plane.Normal.Y * p2y + plane.Normal.Z * p2z - plane.Dist;
            }
            int side;
            if (t1 < 0)
            {
                if (t2 < 0) { num = node.Child1; continue; }
                side = 1;
            }
            else
            {
                if (t2 >= 0) { num = node.Child0; continue; }
                side = 0;
            }

            // "the line intersects, find intersection point ... this uses the original trace for maximum accuracy"
            if (plane.Type < 3)
            {
                t1 = (plane.Type == 0 ? t.StartX : plane.Type == 1 ? t.StartY : t.StartZ) - plane.Dist;
                t2 = (plane.Type == 0 ? t.EndX : plane.Type == 1 ? t.EndY : t.EndZ) - plane.Dist;
            }
            else
            {
                t1 = plane.Normal.X * t.StartX + plane.Normal.Y * t.StartY + plane.Normal.Z * t.StartZ - plane.Dist;
                t2 = plane.Normal.X * t.EndX + plane.Normal.Y * t.EndY + plane.Normal.Z * t.EndZ - plane.Dist;
            }
            double midf = t1 / (t1 - t2);
            double midx = t.StartX + midf * t.DistX, midy = t.StartY + midf * t.DistY, midz = t.StartZ + midf * t.DistZ;

            // "recurse both sides, front side first, return if we hit a surface"
            int front = side == 1 ? node.Child1 : node.Child0;
            int ret = front >= 0 ? SurfaceLineNode(ref t, ref trace, front, p1x, p1y, p1z, midx, midy, midz) : SurfaceLineLeaf(ref t, ref trace, front);
            if (ret == StateDone) return StateDone;

            // "test each surface on the node"
            if (FindTextureOnNode(ref t, ref trace, num, midx, midy, midz)) return StateDone;

            // "recurse back side"
            num = side == 1 ? node.Child0 : node.Child1;
            p1x = midx; p1y = midy; p1z = midz;
        }
        return SurfaceLineLeaf(ref t, ref trace, num);
    }

    // the leaf half of the same function; `contents` is the native contents hull 0 holds for the leaf
    private static int SurfaceLineLeaf(ref Check t, ref Q1HullTrace trace, int contents)
    {
        int side = SuperContentsFromNative(contents);
        if (!trace.StartFound)
        {
            trace.StartFound = true;
            trace.StartContents |= side;
        }
        if ((side & SuperContents.LiquidsMask) != 0) trace.InWater = true;
        if (side == 0) trace.InOpen = true;
        if ((side & t.HitMask) != 0)
        {
            if (trace.AllSolid) trace.StartSolid = true;
            return StateSolid;
        }
        trace.AllSolid = false;
        return StateEmpty;
    }

    // Mod_Q1BSP_TraceLineAgainstSurfacesFindTextureOnNode
    private bool FindTextureOnNode(ref Check t, ref Q1HullTrace trace, int nodeIndex, double midx, double midy, double midz)
    {
        ref readonly Q1Node node = ref _bsp.Nodes[nodeIndex];
        float px = (float)midx, py = (float)midy, pz = (float)midz;
        Vector3[] verts = _bsp.FaceVertices;
        for (int i = 0; i < node.FaceCount; i++)
        {
            int faceIndex = node.FirstFace + i;
            ref readonly Q1Face face = ref _bsp.Faces[faceIndex];
            // "skip faces with contents we don't care about"
            if ((t.HitMask & _faceContents[faceIndex]) == 0) continue;
            Vector3 normal = _faceNormal[faceIndex];
            // "skip backfaces"
            if (t.DistX * normal.X + t.DistY * normal.Y + t.DistZ * normal.Z > 0) continue;
            // "iterate edges and see if the point is outside one of them"
            int count = face.VertexCount, j, k;
            for (j = 0, k = count - 1; j < count; k = j, j++)
            {
                Vector3 v0 = verts[face.FirstVertex + k], v1 = verts[face.FirstVertex + j];
                float ex = v0.X - v1.X, ey = v0.Y - v1.Y, ez = v0.Z - v1.Z;
                // CrossProduct(edgedir, normal, edgenormal)
                float nx = ey * normal.Z - ez * normal.Y, ny = ez * normal.X - ex * normal.Z, nz = ex * normal.Y - ey * normal.X;
                if (nx * px + ny * py + nz * pz > nx * v0.X + ny * v0.Y + nz * v0.Z) break;
            }
            if (j < count) continue;

            // "we hit a surface, this is the impact point..."
            trace.PlaneNormal = normal;
            trace.PlaneDist = normal.X * px + normal.Y * py + normal.Z * pz;
            // "calculate the return fraction which is nudged off the surface a bit" (t1, t2 and midf are floats there)
            float t1 = (float)(t.StartX * normal.X + t.StartY * normal.Y + t.StartZ * normal.Z - trace.PlaneDist);
            float t2 = (float)(t.EndX * normal.X + t.EndY * normal.Y + t.EndZ * normal.Z - trace.PlaneDist);
            float midf = (t1 - ImpactNudge) / (t1 - t2);
            trace.Fraction = midf < 0 ? 0 : midf >= 1 ? 1 : midf;
            trace.HitTexture = _faceTexture[faceIndex];
            trace.HitSurfaceFlags = _faceSurfaceFlags[faceIndex];
            trace.HitContents = _faceContents[faceIndex];
            return true;
        }
        return false;
    }

    // ---- Mod_BSP_LightPoint -------------------------------------------------------------------------------

    /// <summary>
    /// Mod_BSP_LightPoint: the light a model standing at <paramref name="point"/> is drawn with on a map
    /// without a light grid - the lightmap of the first face straight below the point, bilinearly filtered,
    /// every style layer times its current value. The result is in DarkPlaces' ambient colour scale (1 is
    /// full light, the lightmap's 128). A map without light data answers (1, 1, 1); a point over nothing
    /// answers black and false.
    /// </summary>
    /// <param name="styleValue">The current value of a light style, 0 to about 2 (1 is normal): r_refdef.scene.rtlightstylevalue.</param>
    public bool LightPoint(Vector3 point, Func<int, float> styleValue, out Vector3 ambient)
    {
        ambient = Vector3.Zero;
        if (_bsp.LightData.Length == 0)
        {
            ambient = Vector3.One;
            return true;
        }
        if (_bsp.Models.Length == 0) return false;
        return LightPointNode(_bsp.Models[0].HeadNode0, point.X, point.Y, point.Z + 0.125f, point.Z - 65536, styleValue, ref ambient);
    }

    private bool LightPointNode(int num, float x, float y, float startz, float endz, Func<int, float> styleValue, ref Vector3 ambient)
    {
        float distz = endz - startz;
        Q1Node[] nodes = _bsp.Nodes;
        while (num >= 0)
        {
            ref readonly Q1Node node = ref nodes[num];
            ref readonly Q1Plane plane = ref _planes[node.PlaneIndex];
            bool side;
            float mid;
            if (plane.Type == 0) { num = x < plane.Dist ? node.Child1 : node.Child0; continue; }
            if (plane.Type == 1) { num = y < plane.Dist ? node.Child1 : node.Child0; continue; }
            if (plane.Type == 2)
            {
                side = startz < plane.Dist;
                if ((endz < plane.Dist) == side) { num = side ? node.Child1 : node.Child0; continue; }
                mid = plane.Dist;
            }
            else
            {
                float back, front;
                back = front = x * plane.Normal.X + y * plane.Normal.Y;
                front += startz * plane.Normal.Z;
                back += endz * plane.Normal.Z;
                side = front < plane.Dist;
                if ((back < plane.Dist) == side) { num = side ? node.Child1 : node.Child0; continue; }
                mid = startz + distz * (front - plane.Dist) / (front - back);
            }

            // "go down front side"
            int frontChild = side ? node.Child1 : node.Child0;
            if (frontChild >= 0 && LightPointNode(frontChild, x, y, startz, mid, styleValue, ref ambient)) return true;

            // "check for impact on this node"
            for (int i = 0; i < node.FaceCount; i++)
            {
                ref readonly Q1Face face = ref _bsp.Faces[node.FirstFace + i];
                // "no lightmaps": not a wall (sky), or no samples
                if ((_faceContents[node.FirstFace + i] & SuperContents.Sky) != 0) continue;
                if (!face.WhiteLight && (face.LightOffset < 0 || !face.Lightmapped)) continue;
                ref readonly Q1TexInfo ti = ref _bsp.TexInfo[face.TexInfoIndex];
                float ds = (x * ti.S.X + y * ti.S.Y + mid * ti.S.Z + ti.S.W - face.TextureMinS) * 0.0625f;
                float dt = (x * ti.T.X + y * ti.T.Y + mid * ti.T.Z + ti.T.W - face.TextureMinT) * 0.0625f;
                int dsi = (int)MathF.Floor(ds), dti = (int)MathF.Floor(dt);
                int lmwidth = face.LightWidth, lmheight = face.LightHeight;
                if (dsi < 0 || dsi >= lmwidth || dti < 0 || dti >= lmheight) continue;
                if (face.WhiteLight)
                {
                    // the block of 128 a liquid without light data was given: full light, on style 0
                    ambient += new Vector3(styleValue(0));
                    return true;
                }
                if (dsi > lmwidth - 2) dsi = lmwidth - 2;
                if (dti > lmheight - 2) dti = lmheight - 2;
                if (dsi < 0 || dti < 0) return true; // a block one sample wide: DarkPlaces reads outside it; nothing is added here
                float dsfrac = ds - dsi, dtfrac = dt - dti;
                float w00 = (1 - dsfrac) * (1 - dtfrac) * (1.0f / 128.0f), w01 = dsfrac * (1 - dtfrac) * (1.0f / 128.0f);
                float w10 = (1 - dsfrac) * dtfrac * (1.0f / 128.0f), w11 = dsfrac * dtfrac * (1.0f / 128.0f);
                int line3 = lmwidth * 3, size3 = lmwidth * lmheight * 3;
                byte[] light = _bsp.LightData;
                int at = face.LightOffset + dti * line3 + dsi * 3;
                for (int maps = 0; maps < 4 && face.Style(maps) != 255; maps++)
                {
                    if (at < 0 || at + line3 + 6 > light.Length) break;
                    float scale = styleValue(face.Style(maps));
                    Add(ref ambient, w00 * scale, light, at);
                    Add(ref ambient, w01 * scale, light, at + 3);
                    Add(ref ambient, w10 * scale, light, at + line3);
                    Add(ref ambient, w11 * scale, light, at + line3 + 3);
                    at += size3;
                }
                return true;
            }

            // "go down back side"
            num = side ? node.Child0 : node.Child1;
            startz = mid;
            distz = endz - startz;
        }
        return false;
    }

    private static void Add(ref Vector3 colour, float w, byte[] light, int at)
    {
        colour.X += w * light[at];
        colour.Y += w * light[at + 1];
        colour.Z += w * light[at + 2];
    }
}

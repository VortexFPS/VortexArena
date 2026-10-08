// Port of Base/darkplaces/curves.c Q3PatchTesselationOnX / OnY, Q3PatchTesselation, Squared3xCurveArea,
// Q3PatchAdjustTesselation (FindEqualOddVertexInArray, GetSide), Q3PatchDimForTess,
// Q3PatchTesselateFloat, Q3PatchTriangleElements; model_brush.c Mod_Q3BSP_LoadFaces (the collision
// half of Q3FACETYPE_PATCH: PATCH_LOD_COLLISION, the lodgroup pass, data_collisionvertex3f /
// data_collisionelement3i) and Mod_MakeCollisionBIH (which surfaces become BIH_COLLISIONTRIANGLE
// leaves); model_shared.c Mod_SnapVertices, Mod_RemoveDegenerateTriangles; collision.c
// Collision_TraceBrushTriangleFloat (the brush it builds: Collision_SnapCopyPoints,
// Collision_CalcPlanesForTriangleBrushFloat) and Collision_TraceLineTriangleFloat.
using System.Buffers.Binary;
using System.Numerics;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;

namespace VortexArena.Legacy.Server;

/// <summary>
/// Collision geometry for a Q3 map's curved surfaces ("patches"), built exactly as DarkPlaces builds
/// it. The map compiler writes no brushes for a patch, so DarkPlaces tessellates each one a second
/// time - coarsely (mod_q3bsp_curves_subdivisions_tolerance 15, against 4 for drawing), with the
/// vertices then snapped DOWN to whole units (Mod_SnapVertices(..., 1)) - and collides with the
/// resulting triangles: a box against a triangle as a brush of no thickness (two face planes and
/// three axial side planes), a line against a triangle from its front side only, a point against
/// nothing (a triangle has no volume).
///
/// Every one of those choices is visible to the game: where an item dropped onto a curved floor
/// comes to rest, whether a spawn point beside a curved wall is "in solid", what the program's
/// DropToFloor and move-out-of-solid passes do to both. The port's shared collision builder
/// (VortexArena.Engine.Collision.BspCollisionBuilder) makes thick slabs from its own finer
/// tessellation instead, which suits the native game and puts things in different places than a
/// DarkPlaces server does; so the server builds its patch collision here and gives the shared
/// builder a map without patches.
///
/// Not ported: surfaces whose shader says dpmeshcollisions (MATERIALFLAG_MESHCOLLISIONS: collide with
/// the drawn triangles) - the shader scripts are not read here; stock Xonotic maps do not use it.
/// </summary>
internal static class SvPatchCollision
{
    // model_brush.c: mod_q3bsp_curves_subdivisions_tolerance / _mintess / _maxtess / _maxvertices
    // (the dedicated-server limit: "cls.state == ca_dedicated ? mod_q3bsp_curves_subdivisions_maxvertices")
    private const float CollisionTolerance = 15;
    private const int MinTess = 0, MaxTess = 1024, MaxVertices = 4225;
    private const int MaxTessellatedVertices = 65536;   // r_subdivisions_maxvertices, used here as a hard bound
    private const int MaxTriangles = 4_000_000;         // on the whole map
    private const int FaceRecordSize = 104, FaceLodBoundsOffset = 60;   // q3dface_t: specific.patch.mins / maxs
    private const float SnapScale = 32.0f, Snap = 1.0f / SnapScale;      // COLLISION_SNAPSCALE, COLLISION_SNAP

    private sealed class Patch
    {
        public int Face, XSize, YSize, XTess, YTess;
        public float[] Vertices = Array.Empty<float>();   // originalvertex3f
        public float L0, L1, L2, L3, L4, L5;              // lodgroup: the patch's LOD bounds
        public bool SameLodGroup(Patch o) => L0 == o.L0 && L1 == o.L1 && L2 == o.L2 && L3 == o.L3 && L4 == o.L4 && L5 == o.L5;
    }

    /// <summary>A brush made by <see cref="Build"/> for one collision triangle: two face planes and three side planes around three points.</summary>
    public static bool IsTriangle(Brush brush) => brush.Sides.Length == 5 && brush.Points.Length == 3;

    /// <summary>
    /// The collision triangles of every patch in the map, as brushes, per inline model: index 0 is
    /// the world, index N the model "*N". <paramref name="file"/> is the map file the data was read
    /// from - the LOD bounds that group patches for seamless tessellation are not in the parsed data.
    /// </summary>
    public static List<Brush>[] Build(BspData bsp, ReadOnlySpan<byte> file)
    {
        int models = Math.Max(1, bsp.Models.Length);
        List<Brush>[] result = new List<Brush>[models];
        for (int i = 0; i < models; i++) result[i] = new List<Brush>();

        RawLump faceLump = bsp.RawLumps.Length > (int)BspLump.Faces ? bsp.RawLumps[(int)BspLump.Faces] : default;
        bool haveRaw = !faceLump.IsEmpty && faceLump.Offset >= 0 && (long)faceLump.Offset + faceLump.Length <= file.Length
            && faceLump.Length / FaceRecordSize == bsp.Faces.Length;

        // First pass (Mod_Q3BSP_LoadFaces' first loop): validate each patch and choose its tessellation.
        List<Patch> patches = new();
        for (int i = 0; i < bsp.Faces.Length; i++)
        {
            BspFace face = bsp.Faces[i];
            if (face.Type != BspFaceType.Patch) continue;
            if (face.TextureIndex < 0 || face.TextureIndex >= bsp.Textures.Length) continue;   // "invalid textureindex"
            int w = face.PatchWidth, h = face.PatchHeight;
            // "invalid patchsize"
            if (w > 32767 || h > 32767 || face.VertexCount != w * h || w < 3 || h < 3 || (w & 1) == 0 || (h & 1) == 0 || w * h >= MaxVertices) continue;
            if (face.FirstVertex < 0 || (long)face.FirstVertex + face.VertexCount > bsp.Vertices.Length) continue;
            Patch patch = new() { Face = i, XSize = w, YSize = h, Vertices = new float[w * h * 3] };
            for (int v = 0; v < w * h; v++)
            {
                Vector3 p = bsp.Vertices[face.FirstVertex + v].Position;
                patch.Vertices[v * 3] = p.X;
                patch.Vertices[v * 3 + 1] = p.Y;
                patch.Vertices[v * 3 + 2] = p.Z;
            }
            // "lower quality collision patches! Same procedure as before, but different cvars";
            // bound to user settings, then to sanity settings
            patch.XTess = Math.Clamp(Math.Clamp(TessellationOnX(w, h, patch.Vertices, CollisionTolerance), MinTess, MaxTess), 0, 1024);
            patch.YTess = Math.Clamp(Math.Clamp(TessellationOnY(w, h, patch.Vertices, CollisionTolerance), MinTess, MaxTess), 0, 1024);
            if (haveRaw)
            {
                ReadOnlySpan<byte> bounds = file.Slice(faceLump.Offset + i * FaceRecordSize + FaceLodBoundsOffset, 24);
                patch.L0 = BinaryPrimitives.ReadSingleLittleEndian(bounds);
                patch.L1 = BinaryPrimitives.ReadSingleLittleEndian(bounds[4..]);
                patch.L2 = BinaryPrimitives.ReadSingleLittleEndian(bounds[8..]);
                patch.L3 = BinaryPrimitives.ReadSingleLittleEndian(bounds[12..]);
                patch.L4 = BinaryPrimitives.ReadSingleLittleEndian(bounds[16..]);
                patch.L5 = BinaryPrimitives.ReadSingleLittleEndian(bounds[20..]);
            }
            else
            {
                // Without the file's LOD bounds no two patches share a group (NaN equals nothing).
                patch.L0 = float.NaN;
            }
            patches.Add(patch);
        }

        // "Fix patches tesselations so that they make no seams": patches of one LOD group that share
        // an edge take the finer of their two tessellations along it, until nothing changes.
        bool again;
        int rounds = 0;
        do
        {
            again = false;
            for (int i = 0; i < patches.Count; i++)
                for (int j = i + 1; j < patches.Count; j++)
                    if (patches[i].SameLodGroup(patches[j]) && AdjustTessellation(patches[i], patches[j])) again = true;
        }
        while (again && ++rounds < 4096);   // each round raises a tessellation that is bounded by 1024

        // Second pass: tessellate, snap, drop degenerate triangles, make the brushes.
        int triangles = 0;
        foreach (Patch patch in patches)
        {
            BspFace face = bsp.Faces[patch.Face];
            BspTexture texture = bsp.Textures[face.TextureIndex];
            int contents = SuperContents.FromQ3Native(texture.ContentFlags);
            // A triangle of contents nothing can ask for is never hit; it need not exist.
            if (contents == 0) continue;

            // Not DarkPlaces': a bound on what one patch may allocate. The tessellation level comes from
            // the patch's own coordinates, and a map file - which on the client side may be one a server
            // sent - can ask for millions of vertices per patch. No real curve comes near this (a
            // tolerance of 15 units gives the stock maps levels of 0 to 3).
            while ((long)DimForTess(patch.XSize, patch.XTess) * DimForTess(patch.YSize, patch.YTess) > MaxTessellatedVertices && (patch.XTess > 1 || patch.YTess > 1))
            {
                if (patch.XTess >= patch.YTess) patch.XTess--;
                else patch.YTess--;
            }
            int finalWidth = DimForTess(patch.XSize, patch.XTess), finalHeight = DimForTess(patch.YSize, patch.YTess);
            if (finalWidth < 2 || finalHeight < 2 || (long)finalWidth * finalHeight > MaxTessellatedVertices) continue;
            if (triangles > MaxTriangles) break;
            float[] vertices = new float[finalWidth * finalHeight * 3];
            Tessellate(vertices, patch.XSize, patch.YSize, patch.Vertices, patch.XTess, patch.YTess);
            int[] elements = new int[(finalWidth - 1) * (finalHeight - 1) * 6];
            TriangleElements(elements, finalWidth, finalHeight);
            // Mod_SnapVertices(3, finalvertices, surfacecollisionvertex3f, 1): floor to whole units
            for (int v = 0; v < vertices.Length; v++) vertices[v] = (float)Math.Floor(vertices[v] * 1.0);

            List<Brush> into = result[ModelOfFace(bsp, patch.Face)];
            for (int t = 0; t + 2 < elements.Length; t += 3)
            {
                Vector3 v0 = Vertex(vertices, elements[t]), v1 = Vertex(vertices, elements[t + 1]), v2 = Vertex(vertices, elements[t + 2]);
                // Mod_RemoveDegenerateTriangles: "a degenerate triangle is one with no width"
                if (Vector3.Cross(v1 - v0, v2 - v0).LengthSquared() < 0.001f) continue;
                if (TriangleBrush(v0, v1, v2, contents, texture.SurfaceFlags, texture.ShaderName) is { } brush)
                {
                    into.Add(brush);
                    triangles++;
                }
            }
        }
        return result;
    }

    private static Vector3 Vertex(float[] vertices, int index) => new(vertices[index * 3], vertices[index * 3 + 1], vertices[index * 3 + 2]);

    private static int ModelOfFace(BspData bsp, int face)
    {
        for (int m = 1; m < bsp.Models.Length; m++)
            if (face >= bsp.Models[m].FirstFace && face < bsp.Models[m].FirstFace + bsp.Models[m].FaceCount) return m;
        return 0;
    }

    // ---- curves.c ----------------------------------------------------------------------------------------

    // Squared3xCurveArea: "an upper bound of the area between the spline a->control->b and the line
    // a->b: the area of the triangle a->control->b->a", squared.
    private static float Squared3xCurveArea(float[] v, int a, int control, int b)
    {
        float aa = 0, bb = 0, ab = 0;
        for (int c = 0; c < 3; c++)
        {
            float xa = v[a + c] - v[control + c], xb = v[b + c] - v[control + c];
            aa += xa * xa;
            ab += xa * xb;
            bb += xb * xb;
        }
        return aa * bb - ab * ab;
    }

    // Q3PatchTesselation: "f is actually a squared 2x curve area... so the formula had to be adjusted
    // to give roughly the same subdivisions"
    private static int Tessellation(float largestSquared3xCurveArea, float tolerance)
    {
        // pow() and the division are C doubles; the result is stored in a float
        float f = (float)(Math.Pow(largestSquared3xCurveArea / 64.0f, 0.25f) / tolerance);
        if (f < 0.0001) return 0;   // TOTALLY flat patches
        if (f < 2) return 1;
        return (int)Math.Floor(Math.Log(f) / Math.Log(2.0f)) + 1;
    }

    // "returns how much tesselation of each segment is needed to remain under tolerance"
    private static int TessellationOnX(int width, int height, float[] v, float tolerance)
    {
        float largest = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width - 1; x += 2)
            {
                int patch = (y * width + x) * 3;
                float area = Squared3xCurveArea(v, patch, patch + 3, patch + 6);
                if (largest < area) largest = area;
            }
        return Tessellation(largest, tolerance);
    }

    private static int TessellationOnY(int width, int height, float[] v, float tolerance)
    {
        float largest = 0;
        for (int y = 0; y < height - 1; y += 2)
            for (int x = 0; x < width; x++)
            {
                int patch = (y * width + x) * 3;
                float area = Squared3xCurveArea(v, patch, patch + width * 3, patch + 2 * width * 3);
                if (largest < area) largest = area;
            }
        return Tessellation(largest, tolerance);
    }

    // Q3PatchDimForTess
    private static int DimForTess(int size, int tess) => tess > 0 ? (size - 1) * tess + 1 : tess == 0 ? (size - 1) / 2 + 1 : 0;

    // FindEqualOddVertexInArray: "Check only vertices with odd X and Y" (the control points that lie on the surface).
    private static int FindEqualOddVertex(float[] from, int vertex, float[] vertices, int width, int height)
    {
        for (int y = 0; y < height; y += 2)
            for (int x = 0; x < width; x += 2)
            {
                int at = (y * width + x) * 3;
                // "this is notably smaller than the smallest radiant grid but large enough so we don't
                // need to get scared of roundoff errors"
                if (Math.Abs(from[vertex] - vertices[at]) > 0.05 || Math.Abs(from[vertex + 1] - vertices[at + 1]) > 0.05 || Math.Abs(from[vertex + 2] - vertices[at + 2]) > 0.05) continue;
                return y * width + x;
            }
        return -1;
    }

    private const int SideInvalid = -1, SideX = 0, SideY = 1;

    private static int GetSide(int p1, int p2, int width, out int pointDist)
    {
        pointDist = 0;
        if (p1 < 0 || p2 < 0) return SideInvalid;
        int x1 = p1 % width, y1 = p1 / width, x2 = p2 % width, y2 = p2 / width;
        if (x1 == x2)
        {
            if (y1 == y2) return SideInvalid;
            pointDist = Math.Abs(y2 - y1);
            return SideY;
        }
        if (y1 == y2)
        {
            pointDist = Math.Abs(x2 - x1);
            return SideX;
        }
        return SideInvalid;
    }

    // Q3PatchAdjustTesselation: "Increase tesselation of one of two touching patches to make a
    // seamless connection between them. Returns 0 in case if patches were not modified".
    private static bool AdjustTessellation(Patch patch1, Patch patch2)
    {
        Span<int> id1 = stackalloc int[8], id2 = stackalloc int[8];
        // Potential paired vertices (corners of the first patch)
        id1[0] = 0;
        id1[1] = patch1.XSize - 1;
        id1[2] = patch1.XSize * (patch1.YSize - 1);
        id1[3] = patch1.XSize * patch1.YSize - 1;
        for (int i = 0; i < 4; i++) id2[i] = FindEqualOddVertex(patch1.Vertices, id1[i] * 3, patch2.Vertices, patch2.XSize, patch2.YSize);
        // Corners of the second patch
        id2[4] = 0;
        id2[5] = patch2.XSize - 1;
        id2[6] = patch2.XSize * (patch2.YSize - 1);
        id2[7] = patch2.XSize * patch2.YSize - 1;
        for (int i = 4; i < 8; i++) id1[i] = FindEqualOddVertex(patch2.Vertices, id2[i] * 3, patch1.Vertices, patch1.XSize, patch1.YSize);

        bool modified = false;
        for (int i = 0; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
            {
                int side1 = GetSide(id1[i], id1[j], patch1.XSize, out int dist1), side2 = GetSide(id2[i], id2[j], patch2.XSize, out int dist2);
                if (side1 == SideInvalid || side2 == SideInvalid) continue;
                if (dist1 != dist2) continue;   // "no patch welding if the resolutions mismatch"
                // "Update every lod level": only the collision level exists here.
                int tess1 = side1 == SideX ? patch1.XTess : patch1.YTess, tess2 = side2 == SideX ? patch2.XTess : patch2.YTess;
                if (tess1 == tess2) continue;
                int larger = Math.Max(tess1, tess2);
                if (side1 == SideX) patch1.XTess = larger; else patch1.YTess = larger;
                if (side2 == SideX) patch2.XTess = larger; else patch2.YTess = larger;
                modified = true;
            }
        return modified;
    }

    // Q3PatchTesselateFloat for three components: "iterate over the individual 3x3 quadratic spline
    // surfaces one at a time expanding them to fill the output array (with some overlap to ensure the
    // edges are filled)". Single-precision throughout, as the C is.
    private static void Tessellate(float[] output, int patchWidth, int patchHeight, float[] patch, int tessWidth, int tessHeight)
    {
        int outputWidth = DimForTess(patchWidth, tessWidth);
        int xmax = Math.Max(1, 2 * tessWidth), ymax = Math.Max(1, 2 * tessHeight);
        Span<float> temp = stackalloc float[9];
        for (int k = 0; k < patchHeight - 1; k += 2)
            for (int l = 0; l < patchWidth - 1; l += 2)
                for (int y = 0; y <= ymax; y++)
                {
                    // calculate control points for this row by collapsing the 3 rows of control
                    // points to one row using py
                    float py = (float)y / (float)ymax;
                    float a = (1.0f - py) * (1.0f - py), b = (1.0f - py) * (2.0f * py), c = py * py;
                    for (int component = 0; component < 3; component++)
                        for (int x = 0; x < 3; x++)
                            temp[x * 3 + component] = patch[((k + 0) * patchWidth + (l + x)) * 3 + component] * a
                                + patch[((k + 1) * patchWidth + (l + x)) * 3 + component] * b
                                + patch[((k + 2) * patchWidth + (l + x)) * 3 + component] * c;
                    int v = ((k * ymax / 2 + y) * outputWidth + l * xmax / 2) * 3;
                    for (int x = 0; x <= xmax; x++)
                    {
                        float px = (float)x / (float)xmax;
                        a = (1.0f - px) * (1.0f - px);
                        b = (1.0f - px) * (2.0f * px);
                        c = px * px;
                        for (int component = 0; component < 3; component++)
                            output[v + component] = temp[component] * a + temp[3 + component] * b + temp[6 + component] * c;
                        v += 3;
                    }
                }
    }

    // Q3PatchTriangleElements: "(width-1)*(height-1)*2 triangles"; "swap the triangle order in odd
    // rows as optimization for collision stride".
    private static void TriangleElements(int[] elements, int width, int height)
    {
        int e = 0;
        for (int y = 0; y < height - 1; y++)
        {
            if (y % 2 != 0)
            {
                int row0 = (y + 0) * width + width - 2, row1 = (y + 1) * width + width - 2;
                for (int x = 0; x < width - 1; x++)
                {
                    elements[e++] = row1;
                    elements[e++] = row1 + 1;
                    elements[e++] = row0 + 1;
                    elements[e++] = row0;
                    elements[e++] = row1;
                    elements[e++] = row0 + 1;
                    row0--;
                    row1--;
                }
            }
            else
            {
                int row0 = (y + 0) * width, row1 = (y + 1) * width;
                for (int x = 0; x < width - 1; x++)
                {
                    elements[e++] = row0;
                    elements[e++] = row1;
                    elements[e++] = row0 + 1;
                    elements[e++] = row1;
                    elements[e++] = row1 + 1;
                    elements[e++] = row0 + 1;
                    row0++;
                    row1++;
                }
            }
        }
    }

    // ---- collision.c -------------------------------------------------------------------------------------

    private static float SnapPoint(float v) => MathF.Floor(v * SnapScale + 0.5f) * Snap;

    /// <summary>
    /// The brush Collision_TraceBrushTriangleFloat builds for a triangle: the three points snapped to
    /// 1/32, the triangle's plane and its opposite, and three side planes - with
    /// collision_triangle_axialsides (the default) each side plane stands on its edge parallel to
    /// the axis the triangle faces most ("generate axially-aligned edge planes"). Not an AABB brush
    /// and without AABB planes, so a box is tested against it on every axis: its planes, the box's,
    /// and the cross products of their edges. Null for a triangle with no area.
    /// </summary>
    public static Brush? TriangleBrush(Vector3 v0, Vector3 v1, Vector3 v2, int contents, int surfaceFlags, string? texture)
    {
        Vector3 p0 = new(SnapPoint(v0.X), SnapPoint(v0.Y), SnapPoint(v0.Z));
        Vector3 p1 = new(SnapPoint(v1.X), SnapPoint(v1.Y), SnapPoint(v1.Z));
        Vector3 p2 = new(SnapPoint(v2.X), SnapPoint(v2.Y), SnapPoint(v2.Z));
        // TriangleNormal(a, b, c, n): (a - b) x (c - b)
        Vector3 normal = Vector3.Cross(p0 - p1, p2 - p1);
        // "there's no point in processing a degenerate triangle (GIGO - Garbage In, Garbage Out)"
        if (normal.LengthSquared() < 0.0001f) return null;
        normal = Vector3.Normalize(normal);
        float dist = Vector3.Dot(p0, normal);
        // edge directions are easy to calculate
        Vector3 edge0 = p2 - p0, edge1 = p0 - p1, edge2 = p1 - p2;

        // collision_triangle_axialsides
        int best = 0;
        float bestDist = MathF.Abs(normal.X);
        if (bestDist < MathF.Abs(normal.Y)) { bestDist = MathF.Abs(normal.Y); best = 1; }
        if (bestDist < MathF.Abs(normal.Z)) best = 2;
        float sign = (best == 0 ? normal.X : best == 1 ? normal.Y : normal.Z) < 0 ? -1 : 1;
        Vector3 projectionNormal = best == 0 ? new Vector3(sign, 0, 0) : best == 1 ? new Vector3(0, sign, 0) : new Vector3(0, 0, sign);
        Vector3 n2 = SideNormal(edge0, projectionNormal, best), n3 = SideNormal(edge1, projectionNormal, best), n4 = SideNormal(edge2, projectionNormal, best);

        BrushPlane[] sides =
        {
            new(normal, dist, surfaceFlags, contents, texture),
            new(-normal, -dist, surfaceFlags, contents, texture),
            new(n2, Vector3.Dot(p2, n2), surfaceFlags, contents, texture),
            new(n3, Vector3.Dot(p0, n3), surfaceFlags, contents, texture),
            new(n4, Vector3.Dot(p1, n4), surfaceFlags, contents, texture),
        };
        Brush brush = new(sides, new[] { p0, p1, p2 }, new[] { edge0, edge1, edge2 }, contents, surfaceFlags, isAabb: false, texture: texture);
        // The broadphase box of a BIH_COLLISIONTRIANGLE leaf: the triangle's bounds grown by one unit.
        brush.Mins -= Vector3.One;
        brush.Maxs += Vector3.One;
        return brush;
    }

    private static Vector3 SideNormal(Vector3 edge, Vector3 projectionNormal, int best)
    {
        if (best == 0) edge.X = 0; else if (best == 1) edge.Y = 0; else edge.Z = 0;
        Vector3 normal = Vector3.Cross(edge, projectionNormal);
        // VectorNormalize leaves a zero vector zero
        float length = normal.Length();
        return length > 0 ? normal / length : normal;
    }

    /// <summary>What <see cref="TraceLineTriangle"/> found: the closest impact so far.</summary>
    public struct LineHit
    {
        /// <summary>trace->fraction: the fraction of the line travelled; tests only accept a closer one.</summary>
        public float Fraction;
        public Vector3 PlaneNormal;
        public float PlaneDist;
        public Brush? Triangle;
    }

    /// <summary>
    /// Collision_TraceLineTriangleFloat: a line against one triangle (the brush's three points, in
    /// its winding). "If start point is on the back side there is no collision (we don't care about
    /// traces going through the triangle the wrong way)" - a patch stops a line from its front only.
    /// </summary>
    public static void TraceLineTriangle(ref LineHit trace, Vector3 lineStart, Vector3 lineEnd, Brush triangle)
    {
        Vector3 point0 = triangle.Points[0], point1 = triangle.Points[1], point2 = triangle.Points[2];
        // calculate the faceplanenormal of the triangle, this represents the front side
        Vector3 edge01 = point0 - point1, edge21 = point2 - point1;
        Vector3 facePlaneNormal = Vector3.Cross(edge01, edge21);
        float facePlaneNormalLength2 = Vector3.Dot(facePlaneNormal, facePlaneNormal);
        if (facePlaneNormalLength2 < 0.0001f) return;
        float facePlaneDist = Vector3.Dot(point0, facePlaneNormal);

        // if start point is on the back side there is no collision
        float d1 = Vector3.Dot(facePlaneNormal, lineStart);
        if (d1 <= facePlaneDist) return;
        // if both are in front, there is no collision
        float d2 = Vector3.Dot(facePlaneNormal, lineEnd);
        if (d2 >= facePlaneDist) return;

        // the line starts infront and ends behind, passing through it
        float d = 1.0f / (d1 - d2);
        float f = (d1 - facePlaneDist) * d;
        float f2 = f - Collision.ImpactNudge * d;
        // skip out if this impact is further away than previous ones
        if (f2 >= trace.Fraction) return;
        // calculate the perfect impact point for classification of insidedness
        Vector3 impact = new(lineStart.X + f * (lineEnd.X - lineStart.X), lineStart.Y + f * (lineEnd.Y - lineStart.Y), lineStart.Z + f * (lineEnd.Z - lineStart.Z));

        // calculate the edge normal and reject if impact is outside triangle (an edge normal faces
        // away from the triangle; because of the way the insidedness comparison is written it does
        // not need to be normalized)
        Vector3 edgeNormal = Vector3.Cross(edge01, facePlaneNormal);
        if (Vector3.Dot(impact, edgeNormal) > Vector3.Dot(point1, edgeNormal)) return;
        edgeNormal = Vector3.Cross(facePlaneNormal, edge21);
        if (Vector3.Dot(impact, edgeNormal) > Vector3.Dot(point2, edgeNormal)) return;
        Vector3 edge02 = point0 - point2;
        edgeNormal = Vector3.Cross(facePlaneNormal, edge02);
        if (Vector3.Dot(impact, edgeNormal) > Vector3.Dot(point0, edgeNormal)) return;

        // store the new trace fraction and plane (because collisions only happen from the front this
        // is always simply the triangle normal, never flipped)
        trace.Fraction = f2;
        d = 1.0f / MathF.Sqrt(facePlaneNormalLength2);
        trace.PlaneNormal = facePlaneNormal * d;
        trace.PlaneDist = facePlaneDist * d;
        trace.Triangle = triangle;
    }
}

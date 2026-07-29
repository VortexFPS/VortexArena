using System;
using System.Collections.Generic;
using SVec2 = System.Numerics.Vector2;
using SVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Formats.Bsp;

/// <summary>
/// Tessellates Quake-3 bezier <see cref="BspFaceType.Patch"/> faces into triangle meshes.
///
/// A patch face's vertex range is a <c>PatchWidth x PatchHeight</c> grid of control points (both
/// dimensions are odd: 3, 5, 7, 9, …). The grid is decomposed into overlapping <c>3 x 3</c> control
/// groups — <c>(PatchWidth-1)/2</c> across by <c>(PatchHeight-1)/2</c> down — and each group is a single
/// biquadratic bezier surface. Adjacent groups share an edge row/column of control points, so the
/// tessellated surfaces join seamlessly.
///
/// Each <c>3 x 3</c> group is subdivided to <see cref="Subdivisions"/> steps in both parametric
/// directions, evaluating position, normal, texcoord and lightmap-texcoord with the same quadratic
/// Bernstein weights so every interpolated attribute stays consistent. This is the standard Q3
/// tessellation (id Tech 3 <c>R_SubdividePatchToGrid</c> / Darkplaces <c>Mod_Q3BSP_LoadFaces</c> patch
/// path), evaluated at fixed resolution rather than curvature-adaptive — simpler and deterministic.
///
/// Output is in Quake space exactly as stored in <see cref="BspVertex"/>; the render host applies the
/// Quake→Godot axis conversion when it packs the ArrayMesh, and the collision builder
/// (<c>BspCollisionBuilder</c>) consumes the Quake-space triangles directly. Lives in the Godot-free
/// Assets layer so both the renderer and the headless collision/trace path can tessellate patches.
/// </summary>
public static class BezierPatch
{
    /// <summary>Subdivision steps per 3x3 control group (≈ <c>r_subdivisions</c> 8). Higher = smoother.</summary>
    public const int Subdivisions = 8;

    /// <summary>
    /// The subdivision level a patch needs for its tessellation to sit within <paramref name="tolerance"/>
    /// world units of the true surface (backlog B3).
    ///
    /// A quadratic Bezier's maximum deviation from the chord joining its endpoints is <c>|c1 - (c0+c2)/2| / 2</c>
    /// — the "sag" at the midpoint. Subdividing into <c>n</c> segments cuts that by <c>n²</c>, so the level
    /// needed for a given tolerance follows directly: <c>n >= sqrt(sag / tolerance)</c>. Taken over every
    /// control triple in the grid and rounded up to the next power of two, because a level that divides the
    /// grid evenly keeps the seams between adjacent groups aligned.
    ///
    /// This exists because collision and render disagreeing about where a curve IS puts items and players at
    /// a visibly wrong height on it. Measuring the patch rather than picking a constant is what keeps the
    /// flat ones — most floors and grates, and exact at any level — from paying for the curved ones.
    /// </summary>
    public static int SubdivisionsFor(in BspFace face, BspVertex[] vertices, float tolerance, int max = Subdivisions)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        int w = face.PatchWidth, h = face.PatchHeight;
        if (w < 3 || h < 3 || (w & 1) == 0 || (h & 1) == 0)
            return 1;

        // Copied out of the `in` parameter so the shared implementation can index it from a local function;
        // an `in` parameter cannot be captured.
        int first = face.FirstVertex;
        if (first < 0 || first + w * h > vertices.Length)
            return 1;

        var controls = new SVec3[w * h];
        for (int i = 0; i < controls.Length; i++)
            controls[i] = vertices[first + i].Position;

        return SubdivisionsFor(controls, w, h, tolerance, max);
    }

    /// <summary>
    /// <see cref="SubdivisionsFor(in BspFace, BspVertex[], float, int)"/> over a raw control grid, for callers
    /// holding a <c>.vmap</c> patch rather than a BSP face. Same metric, same reasoning.
    /// </summary>
    public static int SubdivisionsFor(
        IReadOnlyList<SVec3> controls, int width, int height, float tolerance, int max = Subdivisions)
    {
        ArgumentNullException.ThrowIfNull(controls);

        if (width < 3 || height < 3 || (width & 1) == 0 || (height & 1) == 0)
            return 1;
        if (controls.Count < width * height)
            return 1;
        if (tolerance <= 0f)
            return max;

        SVec3 At(int col, int row) => controls[row * width + col];

        float worstSag = 0f;
        for (int row = 0; row < height; row++)
            for (int col = 0; col + 2 < width; col += 2)
                worstSag = MathF.Max(worstSag, Sag(At(col, row), At(col + 1, row), At(col + 2, row)));
        for (int col = 0; col < width; col++)
            for (int row = 0; row + 2 < height; row += 2)
                worstSag = MathF.Max(worstSag, Sag(At(col, row), At(col, row + 1), At(col, row + 2)));

        if (worstSag <= tolerance)
            return 1;

        int needed = (int)MathF.Ceiling(MathF.Sqrt(worstSag / tolerance));
        int level = 1;
        while (level < needed && level < max)
            level *= 2;
        return Math.Clamp(level, 1, max);
    }

    /// <summary>
    /// Roughly how horizontal a patch is, 0 (a wall) to 1 (a floor), from the average of its control-grid
    /// normals.
    ///
    /// Collision accuracy is worth far more on a surface things REST on than one they slide along: a floor a
    /// few units out leaves items floating or sunk, while a wall a few units out just stops you slightly
    /// early. Letting the caller spend its subdivisions accordingly is what keeps a curved wall from costing
    /// the same as a curved floor.
    /// </summary>
    public static float Horizontality(IReadOnlyList<SVec3> controls, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (width < 2 || height < 2 || controls.Count < width * height)
            return 1f;   // unknown shape: assume it matters

        SVec3 At(int col, int row) => controls[row * width + col];

        float sum = 0f;
        int n = 0;
        for (int row = 0; row + 1 < height; row++)
            for (int col = 0; col + 1 < width; col++)
            {
                SVec3 cross = SVec3.Cross(At(col + 1, row) - At(col, row), At(col, row + 1) - At(col, row));
                float len = cross.Length();
                if (len < 1e-6f)
                    continue;
                sum += MathF.Abs(cross.Z / len);
                n++;
            }

        return n == 0 ? 1f : sum / n;
    }

    /// <inheritdoc cref="Horizontality(IReadOnlyList{SVec3}, int, int)"/>
    public static float Horizontality(in BspFace face, BspVertex[] vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        int w = face.PatchWidth, h = face.PatchHeight, first = face.FirstVertex;
        if (w < 2 || h < 2 || first < 0 || first + w * h > vertices.Length)
            return 1f;

        var controls = new SVec3[w * h];
        for (int i = 0; i < controls.Length; i++)
            controls[i] = vertices[first + i].Position;
        return Horizontality(controls, w, h);
    }

    /// <summary>
    /// How far a quadratic Bezier bows away from the straight line between its endpoints, at the midpoint —
    /// zero for three collinear, evenly-spaced control points, which is what a flat patch is made of.
    /// </summary>
    private static float Sag(SVec3 c0, SVec3 c1, SVec3 c2)
        => (c1 - (c0 + c2) * 0.5f).Length() * 0.5f;

    /// <summary>
    /// One fully-interpolated patch vertex in Quake space, mirroring <see cref="BspVertex"/>'s render
    /// channels. Positions/normals are converted to Godot only when the mesh is built.
    /// </summary>
    public readonly struct PatchVertex
    {
        public readonly SVec3 Position;
        public readonly SVec2 TexCoord;
        public readonly SVec2 LightmapCoord;
        public readonly SVec3 Normal;

        public PatchVertex(SVec3 position, SVec2 texCoord, SVec2 lightmapCoord, SVec3 normal)
        {
            Position = position;
            TexCoord = texCoord;
            LightmapCoord = lightmapCoord;
            Normal = normal;
        }
    }

    /// <summary>The tessellated triangle soup of a patch: parallel vertex list + 0-based index list.</summary>
    public sealed class Tessellation
    {
        public readonly List<PatchVertex> Vertices = new();
        public readonly List<int> Indices = new();

        public bool IsEmpty => Indices.Count == 0;
    }

    /// <summary>
    /// Tessellate one patch face. <paramref name="face"/> must be <see cref="BspFaceType.Patch"/> with a
    /// valid <c>PatchWidth x PatchHeight</c> control grid lying inside <paramref name="vertices"/>. Returns
    /// <c>null</c> when the control grid is malformed (non-odd dimensions, &lt; 3, or out of range).
    /// </summary>
    public static Tessellation? Tessellate(in BspFace face, BspVertex[] vertices, int subdivisions = Subdivisions)
    {
        int w = face.PatchWidth;
        int h = face.PatchHeight;

        // Q3 control grids are odd and at least 3 in each dimension; the vertex range must cover w*h.
        if (w < 3 || h < 3 || (w & 1) == 0 || (h & 1) == 0)
            return null;
        if (face.VertexCount < w * h)
            return null;
        int first = face.FirstVertex;
        if (first < 0 || (long)first + w * h > vertices.Length)
            return null;

        int steps = subdivisions < 1 ? 1 : subdivisions;

        var result = new Tessellation();

        // Walk the overlapping 3x3 control groups. Each group starts every 2 control points, so the last
        // column/row of one group is the first of the next (shared edge => watertight seams).
        for (int py = 0; py + 2 < h; py += 2)
        for (int px = 0; px + 2 < w; px += 2)
        {
            // Gather the 9 control vertices of this group (row-major: [row][col]).
            var c = new BspVertex[9];
            for (int r = 0; r < 3; r++)
            for (int col = 0; col < 3; col++)
                c[r * 3 + col] = vertices[first + (py + r) * w + (px + col)];

            TessellateGroup(c, steps, result);
        }

        return result.IsEmpty ? null : result;
    }

    /// <summary>
    /// Tessellate a single biquadratic 3x3 control group into a (steps+1)x(steps+1) vertex grid and emit
    /// two triangles per cell. Appends into <paramref name="result"/> (so all groups of a face accumulate
    /// into one buffer).
    /// </summary>
    private static void TessellateGroup(BspVertex[] c, int steps, Tessellation result)
    {
        int rowVerts = steps + 1;
        int baseIndex = result.Vertices.Count;

        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            // Bernstein basis for the V (row) direction.
            float bv0 = (1f - t) * (1f - t);
            float bv1 = 2f * t * (1f - t);
            float bv2 = t * t;

            // Per-column curves at this V: blend the three rows into one quadratic curve, then sample U.
            for (int j = 0; j <= steps; j++)
            {
                float s = (float)j / steps;
                float bu0 = (1f - s) * (1f - s);
                float bu1 = 2f * s * (1f - s);
                float bu2 = s * s;

                // Biquadratic blend: sum over the 3x3 grid of weight(u)*weight(v)*attribute.
                SVec3 pos = SVec3.Zero;
                SVec2 uv = SVec2.Zero;
                SVec2 lm = SVec2.Zero;
                SVec3 nrm = SVec3.Zero;

                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[0], bu0 * bv0);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[1], bu1 * bv0);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[2], bu2 * bv0);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[3], bu0 * bv1);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[4], bu1 * bv1);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[5], bu2 * bv1);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[6], bu0 * bv2);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[7], bu1 * bv2);
                Accumulate(ref pos, ref uv, ref lm, ref nrm, c[8], bu2 * bv2);

                float nlen2 = nrm.LengthSquared();
                nrm = nlen2 > 1e-12f ? nrm * (1f / MathF.Sqrt(nlen2)) : new SVec3(0f, 0f, 1f);

                result.Vertices.Add(new PatchVertex(pos, uv, lm, nrm));
            }
        }

        // Two triangles per grid cell, winding kept consistent with the source quad.
        for (int i = 0; i < steps; i++)
        for (int j = 0; j < steps; j++)
        {
            int row0 = baseIndex + i * rowVerts + j;
            int row1 = baseIndex + (i + 1) * rowVerts + j;

            result.Indices.Add(row0);
            result.Indices.Add(row1);
            result.Indices.Add(row0 + 1);

            result.Indices.Add(row0 + 1);
            result.Indices.Add(row1);
            result.Indices.Add(row1 + 1);
        }
    }

    private static void Accumulate(
        ref SVec3 pos, ref SVec2 uv, ref SVec2 lm, ref SVec3 nrm, in BspVertex v, float weight)
    {
        pos += v.Position * weight;
        uv += v.TexCoord * weight;
        lm += v.LightmapCoord * weight;
        nrm += v.Normal * weight;
    }
}

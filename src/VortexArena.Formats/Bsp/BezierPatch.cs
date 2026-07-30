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

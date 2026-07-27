using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// One triangulated, single-material surface generated from the truth geometry — the engine-neutral hand-off
/// the Godot host turns into an <c>ArrayMesh</c>.
/// </summary>
public sealed class VmapSurface
{
    /// <summary>Shader/texture name shared by every triangle in this surface.</summary>
    public string Material { get; init; } = string.Empty;

    /// <summary>Q3 surface flags (union over the contributing faces).</summary>
    public int SurfaceFlags { get; init; }

    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> Uvs { get; } = new();

    /// <summary>Triangle indices, counter-clockwise seen from OUTSIDE the solid (the front face).</summary>
    public List<int> Indices { get; } = new();

    public int VertexCount => Positions.Count;
    public int TriangleCount => Indices.Count / 3;
}

/// <summary>
/// Generates renderable geometry from a <see cref="VmapDocument"/> — the "live generation" half of the
/// truth/bake split (design doc §11.2). Brush faces become polygons via <see cref="VmapWinding"/> and are
/// triangulated and grouped by material; bezier patches are tessellated on the same grouping.
///
/// This is what makes the format self-sufficient: a <c>.map</c> import (or an edited brush) carries only
/// planes, and this turns them into something drawable without a compile step.
/// </summary>
public static class VmapGeometryBuilder
{
    /// <summary>Q3SURFACEFLAG_NODRAW — the face exists for collision/vis only and is never rendered.</summary>
    public const int SurfaceNoDraw = 0x0080;

    /// <summary>Q3SURFACEFLAG_SKY — drawn by the sky system rather than as world geometry.</summary>
    public const int SurfaceSky = 0x0004;

    /// <summary>Default bezier subdivision level (matches <see cref="Bsp.BezierPatch"/>'s default).</summary>
    public const int DefaultPatchSubdivisions = 6;

    /// <summary>
    /// Build every drawable surface in the document, grouped by material.
    /// </summary>
    /// <param name="doc">The truth document.</param>
    /// <param name="includeSky">
    /// When false (the default) sky-flagged faces are dropped, because the sky is drawn by the skybox system;
    /// the editor's "Base"/wireframe render modes pass true so the sky brushes remain visible as geometry.
    /// </param>
    /// <param name="patchSubdivisions">Bezier tessellation level.</param>
    public static IReadOnlyList<VmapSurface> BuildSurfaces(
        VmapDocument doc,
        bool includeSky = false,
        int patchSubdivisions = DefaultPatchSubdivisions)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var byMaterial = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (VmapBrush brush in doc.Brushes)
            AppendBrush(brush, byMaterial, includeSky);

        foreach (VmapPatch patch in doc.Patches)
            AppendPatch(patch, byMaterial, includeSky, patchSubdivisions);

        var result = new List<VmapSurface>(byMaterial.Count);
        // Deterministic output order so two builds of the same document produce identical meshes.
        foreach (string key in byMaterial.Keys.OrderBy(k => k, StringComparer.Ordinal))
            result.Add(byMaterial[key].ToSurface());
        return result;
    }

    private static void AppendBrush(VmapBrush brush, Dictionary<string, Builder> byMaterial, bool includeSky)
    {
        Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);
        for (int i = 0; i < windings.Length; i++)
        {
            Vector3[] w = windings[i];
            if (w.Length < 3)
                continue; // bevel plane or fully-clipped face: contributes no surface

            VmapFace face = brush.Faces[i];
            if (!IsDrawable(face.SurfaceFlags, includeSky))
                continue;

            Builder b = Get(byMaterial, face.Material, face.SurfaceFlags);
            int baseIndex = b.Positions.Count;
            Vector3 n = face.Plane.Normal;
            VmapTexProjection proj = face.Projection.IsValid
                ? face.Projection
                : VmapTexProjection.AxialFor(n);

            for (int v = 0; v < w.Length; v++)
            {
                b.Positions.Add(w[v]);
                b.Normals.Add(n);
                b.Uvs.Add(proj.Evaluate(w[v]));
            }

            // Fan-triangulate the convex polygon. VmapWinding yields vertices counter-clockwise seen from
            // outside, which is exactly the front-face order the surface contract promises.
            for (int v = 1; v + 1 < w.Length; v++)
            {
                b.Indices.Add(baseIndex);
                b.Indices.Add(baseIndex + v);
                b.Indices.Add(baseIndex + v + 1);
            }
        }
    }

    private static void AppendPatch(VmapPatch patch, Dictionary<string, Builder> byMaterial, bool includeSky, int subdivisions)
    {
        if (!patch.IsValid || !IsDrawable(patch.SurfaceFlags, includeSky))
            return;

        Builder b = Get(byMaterial, patch.Material, patch.SurfaceFlags);
        int patchesX = (patch.Width - 1) / 2;
        int patchesY = (patch.Height - 1) / 2;
        int steps = Math.Max(1, subdivisions);

        for (int py = 0; py < patchesY; py++)
        for (int px = 0; px < patchesX; px++)
            TessellateBiquadratic(patch, px * 2, py * 2, steps, b);
    }

    /// <summary>
    /// Tessellate one 3x3 biquadratic bezier control block into a (steps+1)^2 vertex grid. Standard Quake 3
    /// patch evaluation: quadratic Bernstein basis in both parameters.
    /// </summary>
    private static void TessellateBiquadratic(VmapPatch patch, int col, int row, int steps, Builder b)
    {
        int baseIndex = b.Positions.Count;
        int stride = steps + 1;

        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            for (int j = 0; j <= steps; j++)
            {
                float s = (float)j / steps;

                Vector3 pos = Vector3.Zero;
                Vector2 uv = Vector2.Zero;
                Vector3 dS = Vector3.Zero, dT = Vector3.Zero;

                for (int cy = 0; cy < 3; cy++)
                {
                    float bt = Bernstein(cy, t), dbt = BernsteinDerivative(cy, t);
                    for (int cx = 0; cx < 3; cx++)
                    {
                        int idx = (row + cy) * patch.Width + (col + cx);
                        Vector3 cp = patch.Controls[idx];
                        Vector2 cuv = patch.ControlUvs[idx];
                        float bs = Bernstein(cx, s), dbs = BernsteinDerivative(cx, s);

                        pos += cp * (bs * bt);
                        uv += cuv * (bs * bt);
                        dS += cp * (dbs * bt);
                        dT += cp * (bs * dbt);
                    }
                }

                Vector3 normal = Vector3.Cross(dS, dT);
                float len = normal.Length();
                normal = len > 1e-6f ? normal / len : Vector3.UnitZ;

                b.Positions.Add(pos);
                b.Normals.Add(normal);
                b.Uvs.Add(uv);
            }
        }

        for (int i = 0; i < steps; i++)
        for (int j = 0; j < steps; j++)
        {
            int v0 = baseIndex + i * stride + j;
            int v1 = v0 + 1;
            int v2 = v0 + stride;
            int v3 = v2 + 1;
            b.Indices.Add(v0); b.Indices.Add(v2); b.Indices.Add(v1);
            b.Indices.Add(v1); b.Indices.Add(v2); b.Indices.Add(v3);
        }
    }

    /// <summary>Quadratic Bernstein basis B_i(x) for i in 0..2.</summary>
    private static float Bernstein(int i, float x) => i switch
    {
        0 => (1f - x) * (1f - x),
        1 => 2f * x * (1f - x),
        _ => x * x,
    };

    /// <summary>Derivative of <see cref="Bernstein"/>, for the patch surface normal.</summary>
    private static float BernsteinDerivative(int i, float x) => i switch
    {
        0 => 2f * x - 2f,
        1 => 2f - 4f * x,
        _ => 2f * x,
    };

    private static bool IsDrawable(int surfaceFlags, bool includeSky)
    {
        if ((surfaceFlags & SurfaceNoDraw) != 0)
            return false;
        if (!includeSky && (surfaceFlags & SurfaceSky) != 0)
            return false;
        return true;
    }

    private static Builder Get(Dictionary<string, Builder> map, string material, int surfaceFlags)
    {
        if (!map.TryGetValue(material, out Builder? b))
        {
            b = new Builder(material);
            map[material] = b;
        }
        b.SurfaceFlags |= surfaceFlags;
        return b;
    }

    /// <summary>Mutable accumulator behind the immutable <see cref="VmapSurface"/> hand-off.</summary>
    private sealed class Builder
    {
        public Builder(string material) => Material = material;

        public string Material { get; }
        public int SurfaceFlags { get; set; }
        public List<Vector3> Positions { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> Uvs { get; } = new();
        public List<int> Indices { get; } = new();

        public VmapSurface ToSurface()
        {
            var s = new VmapSurface { Material = Material, SurfaceFlags = SurfaceFlags };
            s.Positions.AddRange(Positions);
            s.Normals.AddRange(Normals);
            s.Uvs.AddRange(Uvs);
            s.Indices.AddRange(Indices);
            return s;
        }
    }
}

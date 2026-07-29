using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// One triangulated, single-material surface generated from the truth geometry — the engine-neutral hand-off
/// the Godot host turns into an <c>ArrayMesh</c>.
/// </summary>
public sealed class VmapSurface
{
    /// <summary>Shader/texture name shared by every triangle in this surface — the BASE layer's.</summary>
    public string Material { get; init; } = string.Empty;

    /// <summary>
    /// Layers ABOVE the base, shared by every triangle here. Empty for an ordinary single-textured surface.
    ///
    /// Faces batch by their whole stack, not just by base material, so a wall textured <c>metal01</c> and a
    /// wall textured <c>metal01</c> with rust blended over it are different surfaces — they have to be, since
    /// they need different materials on the GPU.
    /// </summary>
    public IReadOnlyList<VmapFaceLayer> ExtraLayers { get; init; } = Array.Empty<VmapFaceLayer>();

    /// <summary>Q3 surface flags (union over the contributing faces).</summary>
    public int SurfaceFlags { get; init; }

    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> Uvs { get; } = new();

    /// <summary>
    /// Triangle indices in the same winding a compiled Q3 face uses — CLOCKWISE seen from outside the solid.
    /// That is what the renderer treats as front-facing, so it is the order both this builder and the BSP
    /// loader must emit; the opposite order silently turns the world inside out.
    /// </summary>
    public List<int> Indices { get; } = new();

    public int VertexCount => Positions.Count;
    public int TriangleCount => Indices.Count / 3;
}

/// <summary>
/// What to include when generating geometry from a document. Different consumers want genuinely different
/// answers: the editor's view wants only the brushes belonging to the selected gametype and only their visible
/// skin, while collision wants every solid volume intact.
/// </summary>
public sealed class VmapSurfaceOptions
{
    /// <summary>Keep sky-flagged faces as drawable geometry (wireframe/Base render modes).</summary>
    public bool IncludeSky { get; init; }

    /// <summary>Bezier tessellation level.</summary>
    public int PatchSubdivisions { get; init; } = VmapGeometryBuilder.DefaultPatchSubdivisions;

    /// <summary>
    /// Inline-model indices to hide (see <see cref="VmapBrush.SubmodelIndex"/>). A compiled map carries every
    /// gametype's <c>func_wall</c> geometry at once; showing all of them at once fills the level with solid
    /// slabs belonging to modes that are not running.
    /// </summary>
    public IReadOnlySet<int>? HiddenSubmodels { get; init; }

    /// <summary>Draw caulk/hint/clip scaffolding volumes too (an explicit editor toggle).</summary>
    public bool IncludeToolBrushes { get; init; }

    /// <summary>
    /// Remove face area buried inside other opaque solids (see <see cref="VmapFaceCulling"/>). On for the
    /// editor's world view; off for collision, which needs whole volumes, and for single-brush picking, which
    /// has no neighbours to be buried in.
    /// </summary>
    public bool CullOccludedFaces { get; init; }

    /// <summary>
    /// The editor's live view filter — group visibility, ad-hoc hide, region (backlog F8, F9). Null for every
    /// non-editor consumer: collision wants whole volumes, and single-brush picking has no view to filter by.
    ///
    /// Kept alongside the two older fields rather than replacing them, so the fifteen callers that only ever
    /// wanted "hide these submodels" are untouched.
    /// </summary>
    public VmapVisibility? Visibility { get; init; }

    /// <summary>Whether a brush belongs to the view these options describe.</summary>
    public bool IsBrushVisible(VmapBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (brush.IsToolBrush && !IncludeToolBrushes)
            return false;
        if (brush.SubmodelIndex != 0 && HiddenSubmodels is not null
            && HiddenSubmodels.Contains(brush.SubmodelIndex))
            return false;
        return Visibility?.IsBrushVisible(brush) ?? true;
    }

    /// <summary>Whether a patch belongs to the view. Always true without a <see cref="Visibility"/>.</summary>
    public bool IsPatchVisible(VmapPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return Visibility?.IsPatchVisible(patch) ?? true;
    }
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

    /// <summary>
    /// Default bezier subdivision level, taken FROM the BSP tessellator rather than restated.
    ///
    /// The comment already claimed these matched while the numbers were 6 and 8, and the two paths draw the
    /// same patches in the same session: the compiled world at one level, the regenerated world at another.
    /// A quadratic's chords lie INSIDE its curve, so the coarser path pulls every curved surface inward —
    /// which reads as the patch being shifted or distorted next to geometry that was built against the true
    /// curve. Sharing the constant is what stops them drifting apart again.
    /// </summary>
    public const int DefaultPatchSubdivisions = Bsp.BezierPatch.Subdivisions;

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
        => BuildSurfaces(doc, new VmapSurfaceOptions
        {
            IncludeSky = includeSky,
            PatchSubdivisions = patchSubdivisions,
        });

    /// <summary>
    /// Build every drawable surface, with control over which brushes participate and whether buried faces are
    /// removed. The render path wants both; collision and single-brush picking want neither.
    /// </summary>
    public static IReadOnlyList<VmapSurface> BuildSurfaces(VmapDocument doc, VmapSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(options);

        var byMaterial = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
        bool Visible(VmapBrush b) => options.IsBrushVisible(b);

        // Faces are only hidden by solids that are themselves part of the view, so the occluder set is built
        // from the same visibility predicate the geometry is.
        VmapFaceCulling? culling = options.CullOccludedFaces ? new VmapFaceCulling(doc, Visible) : null;

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (!Visible(brush))
                continue;
            AppendBrush(brush, byMaterial, options.IncludeSky, culling);
        }

        foreach (VmapPatch patch in doc.Patches)
        {
            // Patches had no visibility check at all: a gametype filter or a region that ignored curves would
            // look broken on any map with patch architecture, and there was nothing in a log to explain it.
            if (!options.IsPatchVisible(patch))
                continue;
            AppendPatch(patch, byMaterial, options.IncludeSky, options.PatchSubdivisions);
        }

        var result = new List<VmapSurface>(byMaterial.Count);
        // Deterministic output order so two builds of the same document produce identical meshes.
        foreach (string key in byMaterial.Keys.OrderBy(k => k, StringComparer.Ordinal))
            result.Add(byMaterial[key].ToSurface());
        return result;
    }

    private static void AppendBrush(
        VmapBrush brush, Dictionary<string, Builder> byMaterial, bool includeSky, VmapFaceCulling? culling)
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
            // Also skip the invisible shader families. A compiled map's brushes carry caulk, noshader and
            // common/* sides that q3map2 never emits as surfaces; drawing them buries the level inside its own
            // sealing shell and multiplies the draw count for geometry nobody can see.
            if (VmapBrush.IsToolMaterial(face.Material))
                continue;

            if (culling is null)
            {
                AppendPolygon(byMaterial, face, w);
                continue;
            }

            foreach (List<Vector3> fragment in culling.Subtract(brush, face.Plane, w))
                AppendPolygon(byMaterial, face, fragment);
        }
    }

    /// <summary>Emit one convex polygon of a face as a fan-triangulated run of vertices.</summary>
    private static void AppendPolygon(
        Dictionary<string, Builder> byMaterial, VmapFace face, IReadOnlyList<Vector3> polygon)
    {
        if (polygon.Count < 3)
            return;

        Builder b = Get(byMaterial, face.Material, face.SurfaceFlags,
            face.IsLayered ? face.Layers.Skip(1).ToArray() : null);
        int baseIndex = b.Positions.Count;
        Vector3 n = face.Plane.Normal;
        VmapTexProjection proj = face.Projection.IsValid
            ? face.Projection
            : VmapTexProjection.AxialFor(n);

        for (int v = 0; v < polygon.Count; v++)
        {
            b.Positions.Add(polygon[v]);
            b.Normals.Add(n);
            b.Uvs.Add(proj.Evaluate(polygon[v]));
        }

        // Fan-triangulate the convex polygon, REVERSING the winding. VmapWinding yields vertices
        // counter-clockwise seen from outside; compiled Q3 faces are the other way round, and that is the
        // order the renderer treats as front-facing (measured: stormkeep's BSP faces wind opposed to their
        // own vertex normals 19611 times against 52). Emitting the natural order instead makes every brush
        // face back-facing, so the level renders inside out — near walls vanish and you see through them to
        // whatever is behind, which is exactly what a hole in the floor looks like.
        for (int v = 1; v + 1 < polygon.Count; v++)
        {
            b.Indices.Add(baseIndex);
            b.Indices.Add(baseIndex + v + 1);
            b.Indices.Add(baseIndex + v);
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
        => Get(map, material, surfaceFlags, null);

    /// <summary>
    /// The accumulator for a material, or for a material PLUS a layer stack.
    ///
    /// The key carries the stack because two faces sharing a base material but differing above it cannot share
    /// a mesh surface — they resolve to different GPU materials. Keying on the base alone would merge them and
    /// whichever face was seen first would decide how both looked.
    /// </summary>
    private static Builder Get(
        Dictionary<string, Builder> map, string material, int surfaceFlags, IReadOnlyList<VmapFaceLayer>? extra)
    {
        string key = extra is null || extra.Count == 0 ? material : StackKey(material, extra);
        if (!map.TryGetValue(key, out Builder? b))
        {
            b = new Builder(material, extra);
            map[key] = b;
        }
        b.SurfaceFlags |= surfaceFlags;
        return b;
    }

    /// <summary>
    /// A batching key for a layered face. Includes each layer's projection, because two faces with the same
    /// textures but different scroll offsets are genuinely different surfaces.
    /// </summary>
    private static string StackKey(string material, IReadOnlyList<VmapFaceLayer> extra)
    {
        var sb = new System.Text.StringBuilder(material.Length + extra.Count * 48);
        sb.Append(material);
        foreach (VmapFaceLayer l in extra)
        {
            VmapTexProjection p = l.Projection;
            sb.Append('').Append(l.Material)
              .Append('').Append((int)l.Blend)
              .Append('').Append(l.WeightChannel)
              .Append('').Append(p.AxisU.X).Append(',').Append(p.AxisU.Y).Append(',').Append(p.AxisU.Z)
              .Append('').Append(p.AxisV.X).Append(',').Append(p.AxisV.Y).Append(',').Append(p.AxisV.Z)
              .Append('').Append(p.OffsetU).Append(',').Append(p.OffsetV);
        }
        return sb.ToString();
    }

    /// <summary>Mutable accumulator behind the immutable <see cref="VmapSurface"/> hand-off.</summary>
    private sealed class Builder
    {
        public Builder(string material, IReadOnlyList<VmapFaceLayer>? extra = null)
        {
            Material = material;
            ExtraLayers = extra ?? Array.Empty<VmapFaceLayer>();
        }

        public string Material { get; }
        public IReadOnlyList<VmapFaceLayer> ExtraLayers { get; }
        public int SurfaceFlags { get; set; }
        public List<Vector3> Positions { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> Uvs { get; } = new();
        public List<int> Indices { get; } = new();

        public VmapSurface ToSurface()
        {
            var s = new VmapSurface
            {
                Material = Material,
                ExtraLayers = ExtraLayers,
                SurfaceFlags = SurfaceFlags,
            };
            s.Positions.AddRange(Positions);
            s.Normals.AddRange(Normals);
            s.Uvs.AddRange(Uvs);
            s.Indices.AddRange(Indices);
            return s;
        }
    }
}

using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// Builds the per-surface render geometry the <c>getsurface*</c> builtins query (<see cref="SurfaceService"/>),
/// from a parsed <see cref="BspData"/>. One <see cref="ModelSurface"/> per BSP face; faces are grouped by the
/// inline model (<see cref="BspData.Models"/>) that owns them, so an entity with <c>setmodel("*N")</c> sees
/// exactly its faces (the companion of <see cref="BspCollisionBuilder"/>, which does the same split for
/// collision brushes). Attaches the surfaces to the matching <c>"*N"</c> <see cref="ModelService.ModelDef"/>.
///
/// Geometry is stored in MODEL-LOCAL space (here == the BSP's world space, since inline brush models compile
/// at the origin); <see cref="SurfaceService"/> applies the entity transform per query.
///
/// Ground truth: DarkPlaces <c>Mod_Q3BSP_LoadFaces</c> (the meshvert offsets are face-local, added to
/// <c>firstvertex</c>) and the patch tessellation in <c>Mod_Q3BSP_LoadFaces</c>.
/// </summary>
public static class BspSurfaceBuilder
{
    /// <summary>
    /// Build the inline brush models' surfaces and attach them to the matching <c>"*N"</c> models on
    /// <paramref name="models"/> (registering the model if the collision builder hasn't already). Worldspawn
    /// (model 0) is included as <c>"*0"</c> so the world entity can be surface-queried too.
    /// </summary>
    public static void BuildAndAttach(BspData bsp, ModelService models)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(models);
        if (bsp.Models.Length == 0)
            return;

        for (int mi = 0; mi < bsp.Models.Length; mi++)
        {
            BspModel m = bsp.Models[mi];
            var surfaces = new List<ModelSurface>(m.FaceCount);
            int end = m.FirstFace + m.FaceCount;
            for (int fi = m.FirstFace; fi < end; fi++)
            {
                if (fi < 0 || fi >= bsp.Faces.Length) continue;
                if (BuildSurface(bsp, bsp.Faces[fi]) is { } s)
                    surfaces.Add(s);
            }

            string name = $"*{mi}";
            if (!models.TryGetModel(name, out ModelService.ModelDef def))
            {
                models.Register(name, m.Mins, m.Maxs, isBrushModel: true);
                models.TryGetModel(name, out def);
            }
            def.Surfaces = surfaces;
        }
    }

    /// <summary>Build one <see cref="ModelSurface"/> from a BSP face (Flat/Mesh use the index list; Patch is
    /// triangulated from its control-point grid). Returns null for a no-geometry face (Flare).</summary>
    private static ModelSurface? BuildSurface(BspData bsp, BspFace face)
    {
        if (face.VertexCount <= 0)
            return null;

        int vc = face.VertexCount;
        var points = new Vector3[vc];
        var normals = new Vector3[vc];
        var tex = new Vector2[vc];
        var lm = new Vector2[vc];
        var col = new Vector4[vc];
        Vector3 normalSum = Vector3.Zero;
        for (int j = 0; j < vc; j++)
        {
            int vi = face.FirstVertex + j;
            if (vi < 0 || vi >= bsp.Vertices.Length) continue;
            BspVertex v = bsp.Vertices[vi];
            points[j] = v.Position;
            normals[j] = v.Normal;
            tex[j] = v.TexCoord;
            lm[j] = v.LightmapCoord;
            col[j] = new Vector4(v.Color.R / 255f, v.Color.G / 255f, v.Color.B / 255f, v.Color.A / 255f);
            normalSum += v.Normal;
        }

        int[] tris;
        if (face.Type == BspFaceType.Patch && face.PatchWidth >= 2 && face.PatchHeight >= 2
            && face.PatchWidth * face.PatchHeight == vc)
        {
            // Triangulate the control-point grid directly (a coarse, watertight mesh — enough for surface
            // queries; the renderer does the full bezier subdivision separately).
            tris = TriangulateGrid(face.PatchWidth, face.PatchHeight);
        }
        else
        {
            int ic = face.IndexCount;
            if (ic < 3) return null;
            tris = new int[ic];
            for (int k = 0; k < ic; k++)
            {
                int idx = face.FirstIndex + k;
                tris[k] = (idx >= 0 && idx < bsp.Triangles.Length) ? bsp.Triangles[idx] : 0;
                if (tris[k] < 0 || tris[k] >= vc) tris[k] = 0; // clamp (matches DP)
            }
        }

        Vector3 planeNormal = normalSum.LengthSquared() > 1e-12f
            ? Vector3.Normalize(normalSum)
            : GeometricNormal(points, tris);

        string texName = (face.TextureIndex >= 0 && face.TextureIndex < bsp.Textures.Length)
            ? bsp.Textures[face.TextureIndex].ShaderName
            : "";

        return new ModelSurface
        {
            Points = points,
            Normals = normals,
            TexCoords = tex,
            LightmapCoords = lm,
            Colors = col,
            Triangles = tris,
            Texture = texName,
            PlaneNormal = planeNormal,
        };
    }

    private static int[] TriangulateGrid(int w, int h)
    {
        var t = new List<int>((w - 1) * (h - 1) * 6);
        for (int y = 0; y < h - 1; y++)
        for (int x = 0; x < w - 1; x++)
        {
            int i00 = y * w + x, i10 = y * w + x + 1, i01 = (y + 1) * w + x, i11 = (y + 1) * w + x + 1;
            t.Add(i00); t.Add(i10); t.Add(i11);
            t.Add(i00); t.Add(i11); t.Add(i01);
        }
        return t.ToArray();
    }

    private static Vector3 GeometricNormal(Vector3[] points, int[] tris)
    {
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            Vector3 a = points[tris[t]], b = points[tris[t + 1]], c = points[tris[t + 2]];
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() > 1e-12f) return Vector3.Normalize(n);
        }
        return Vector3.UnitZ;
    }
}

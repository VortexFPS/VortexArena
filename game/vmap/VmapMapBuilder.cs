using Godot;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Loaders;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// Builds Godot render geometry from an editable <see cref="VmapDocument"/> — the <c>.vmap</c> counterpart of
/// <see cref="MapLoader.BuildMap"/> (design doc §11.2, phase E0).
///
/// The critical difference from the BSP path: nothing here is precompiled. Brush planes are evaluated into
/// polygons by <see cref="VmapGeometryBuilder"/> every build, which is exactly what makes an edited map
/// viewable immediately — no compile step between dragging a wall and seeing it.
///
/// Geometry arrives in Quake space and is converted per-vertex through <see cref="Coords.ToGodot"/>. That
/// mapping is orientation-preserving (determinant +1), so the counter-clockwise-from-outside winding the
/// geometry builder guarantees survives into Godot as correct front faces.
/// </summary>
public static class VmapMapBuilder
{
    /// <summary>
    /// World-space cell edge in Quake units for the frustum-culling split. Mirrors
    /// <see cref="MapLoader"/>'s default: without a spatial split every material would be one map-spanning
    /// MeshInstance3D whose AABB never leaves the frustum, so nothing would ever cull.
    /// </summary>
    public const float CellSize = 1024f;

    /// <summary>
    /// Build the render tree: a root <see cref="Node3D"/> with one <see cref="MeshInstance3D"/> per occupied
    /// spatial cell, each holding one mesh surface per material present in that cell.
    /// </summary>
    /// <param name="doc">The truth document.</param>
    /// <param name="assets">Material facade (resolves shader names to Godot materials).</param>
    /// <param name="options">
    /// Which brushes take part and whether buried faces are removed. Defaults to the editor's world view:
    /// gametype-filtered, occlusion-culled, no sky.
    /// </param>
    public static Node3D BuildMap(VmapDocument doc, AssetSystem assets, VmapSurfaceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(assets);

        options ??= new VmapSurfaceOptions { CullOccludedFaces = true };

        var root = new Node3D { Name = "VmapWorld" };
        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc, options);

        // Bucket every triangle into its spatial cell, keyed by cell then material.
        var cells = new Dictionary<(int X, int Y, int Z), Dictionary<string, CellSurface>>();

        // Warpzone windows and their pocket decor are pulled OUT of the cell meshes into their own addressable
        // nodes, exactly as MapLoader does — PortalRenderer finds them by node metadata and swaps a live
        // see-through render onto the window. Left in the merged cell mesh they are unaddressable, so the
        // warpzone draws its flat placeholder shader and the portal never appears.
        var portalRoot = new Node3D { Name = "Portals" };

        foreach (VmapSurface surface in surfaces)
        {
            if (IsPortalCameraShader(assets, surface.Material))
            {
                AppendPortalNodes(portalRoot, surface, assets, decor: false);
                continue;
            }
            if (IsWarpzoneDecorShader(assets, surface.Material))
            {
                AppendPortalNodes(portalRoot, surface, assets, decor: true);
                continue;
            }

            for (int i = 0; i + 2 < surface.Indices.Count; i += 3)
            {
                int i0 = surface.Indices[i], i1 = surface.Indices[i + 1], i2 = surface.Indices[i + 2];
                NVec3 p0 = surface.Positions[i0], p1 = surface.Positions[i1], p2 = surface.Positions[i2];

                (int, int, int) key = CellKey((p0 + p1 + p2) / 3f);
                if (!cells.TryGetValue(key, out Dictionary<string, CellSurface>? byMaterial))
                {
                    byMaterial = new Dictionary<string, CellSurface>(StringComparer.Ordinal);
                    cells[key] = byMaterial;
                }
                if (!byMaterial.TryGetValue(surface.Material, out CellSurface? cell))
                {
                    cell = new CellSurface(surface.Material);
                    byMaterial[surface.Material] = cell;
                }

                cell.AddTriangle(surface, i0, i1, i2);
            }
        }

        // Deterministic node order so two builds of the same document produce an identical tree.
        foreach ((int X, int Y, int Z) key in cells.Keys.OrderBy(k => k.X).ThenBy(k => k.Y).ThenBy(k => k.Z))
        {
            Dictionary<string, CellSurface> byMaterial = cells[key];
            var mesh = new ArrayMesh();
            var materials = new List<Material>(byMaterial.Count);

            foreach (string material in byMaterial.Keys.OrderBy(m => m, StringComparer.Ordinal))
            {
                CellSurface cell = byMaterial[material];
                if (cell.Indices.Count == 0)
                    continue;
                cell.Pack(mesh);
                materials.Add(EditorMaterial(assets, material));
            }

            if (mesh.GetSurfaceCount() == 0)
                continue;

            var instance = new MeshInstance3D
            {
                Name = $"VmapCell_{key.X}_{key.Y}_{key.Z}",
                Mesh = mesh,
            };
            for (int s = 0; s < materials.Count; s++)
                instance.SetSurfaceOverrideMaterial(s, materials[s]);

            root.AddChild(instance);
        }

        if (portalRoot.GetChildCount() > 0)
            root.AddChild(portalRoot);
        else
            portalRoot.QueueFree();

        return root;
    }

    /// <summary>
    /// A shader that camera-renders a portal view (<c>dpcamera</c>, e.g. <c>effects_warpzone/wavy</c>).
    /// Same authority as <see cref="MapLoader"/>: the parsed shader def, with the name patterns as the
    /// fallback for a portal-ish shader that has no script.
    /// </summary>
    private static bool IsPortalCameraShader(AssetSystem assets, string shaderName)
    {
        string name = (shaderName ?? string.Empty).Replace('\\', '/');
        if (assets.GetShader(name) is { } def)
            return def.Dp.Camera;
        string n = name.ToLowerInvariant();
        return n.Contains("/portals/") || n.StartsWith("portals/", StringComparison.Ordinal)
            || n.Contains("portals_") || n.Contains("mirror");
    }

    /// <summary>
    /// Warpzone pocket DECOR — an <c>effects_warpzone/</c> shader WITHOUT <c>dpcamera</c> (the backdrop and the
    /// blueedge/rededge rims). Drawn normally, but it has to be addressable so the portal cameras — which sit
    /// inside the pocket, behind the exit plane — can cull it; otherwise the backdrop fills the portal view.
    /// </summary>
    private static bool IsWarpzoneDecorShader(AssetSystem assets, string shaderName)
    {
        string name = (shaderName ?? string.Empty).Replace('\\', '/');
        if (!name.ToLowerInvariant().Contains("/effects_warpzone/"))
            return false;
        return assets.GetShader(name) is not { Dp.Camera: true };
    }

    /// <summary>
    /// Emit one node per coplanar group of a portal/decor surface, carrying the metadata contract
    /// <see cref="XonoticGodot.Game.Client.PortalRenderer"/> matches on.
    ///
    /// Grouping by plane is what makes a window ONE node: a warpzone surface is usually several brush faces, and
    /// a node per face would give the renderer several overlapping portals for one opening. Unlike the BSP path
    /// this needs no plane quantisation to recover the grouping — a regenerated face already carries its exact
    /// plane — but the same 8-unit bucketing is kept so faces that differ by float noise still merge.
    /// </summary>
    private static void AppendPortalNodes(Node3D portalRoot, VmapSurface surface, AssetSystem assets, bool decor)
    {
        var groups = new Dictionary<(long, long, long, long), CellSurface>();
        var planes = new Dictionary<(long, long, long, long), (NVec3 NSum, NVec3 OSum, int N)>();

        for (int i = 0; i + 2 < surface.Indices.Count; i += 3)
        {
            int i0 = surface.Indices[i], i1 = surface.Indices[i + 1], i2 = surface.Indices[i + 2];
            NVec3 p0 = surface.Positions[i0], p1 = surface.Positions[i1], p2 = surface.Positions[i2];

            NVec3 n = surface.Normals[i0];
            n = n.LengthSquared() > 1e-9f ? NVec3.Normalize(n) : new NVec3(0f, 0f, 1f);
            NVec3 centre = (p0 + p1 + p2) / 3f;

            var key = (
                (long)MathF.Round(n.X * 32f), (long)MathF.Round(n.Y * 32f), (long)MathF.Round(n.Z * 32f),
                (long)MathF.Round(NVec3.Dot(centre, n) / 8f));

            if (!groups.TryGetValue(key, out CellSurface? group))
            {
                groups[key] = group = new CellSurface(surface.Material);
                planes[key] = (NVec3.Zero, NVec3.Zero, 0);
            }
            group.AddTriangle(surface, i0, i1, i2);

            (NVec3 NSum, NVec3 OSum, int N) acc = planes[key];
            planes[key] = (acc.NSum + n, acc.OSum + centre, acc.N + 1);
        }

        foreach ((long, long, long, long) key in groups.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2)
                     .ThenBy(k => k.Item3).ThenBy(k => k.Item4))
        {
            CellSurface group = groups[key];
            if (group.Indices.Count == 0)
                continue;

            var mesh = new ArrayMesh();
            group.Pack(mesh);
            mesh.SurfaceSetMaterial(0, EditorMaterial(assets, surface.Material));

            (NVec3 NSum, NVec3 OSum, int N) acc = planes[key];
            NVec3 planeN = acc.NSum.LengthSquared() > 1e-9f ? NVec3.Normalize(acc.NSum) : new NVec3(0f, 0f, 1f);
            NVec3 planeO = acc.OSum / Math.Max(1, acc.N);

            var mi = new MeshInstance3D
            {
                Name = decor ? $"PortalDecor_{portalRoot.GetChildCount()}" : $"Portal_{portalRoot.GetChildCount()}",
                Mesh = mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };

            if (decor)
            {
                mi.SetMeta("wz_decor", true);
            }
            else
            {
                // QUAKE-space plane, stored raw in a Vector3 holder — PortalRenderer reads these back as Quake
                // and matches them against WarpzoneManager's zones, which are also Quake. NOT Coords-converted.
                mi.SetMeta("wz_origin", new Vector3(planeO.X, planeO.Y, planeO.Z));
                mi.SetMeta("wz_normal", new Vector3(planeN.X, planeN.Y, planeN.Z));
            }

            portalRoot.AddChild(mi);
        }
    }

    /// <summary>Cache so a shared material is built once per map build, not once per cell.</summary>
    private static readonly Dictionary<string, Material> EditorMaterials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// FULLBRIGHT textured material for the editor world.
    ///
    /// The truth document carries no lightmap UVs — lighting is baked data derived FROM geometry, and the
    /// geometry is what is being edited — so the game's normal lit materials resolve to unlit black here and
    /// the map looks destroyed even though every surface is exactly where it should be. Editors solve this the
    /// same way Radiant does: draw the world fullbright. You lose the lighting, which is meaningless mid-edit
    /// anyway, and you can actually see what you are building.
    /// </summary>
    private static Material EditorMaterial(AssetSystem assets, string shaderName)
    {
        if (EditorMaterials.TryGetValue(shaderName, out Material? cached) && GodotObject.IsInstanceValid(cached))
            return cached;

        AssetSystem.LightmapDiffuse diffuse = assets.ResolveLightmapDiffuse(shaderName);
        Texture2D? albedo = diffuse.Texture ?? assets.LoadTexture(shaderName);

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = albedo,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            // A shader we cannot resolve to an image becomes flat grey rather than invisible: unfound geometry
            // must still be visible and clickable in an editor.
            AlbedoColor = albedo is null ? new Color(0.55f, 0.55f, 0.58f) : Colors.White,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };

        // The shader's static `tcMod scale`, which the BSP path applies as albedoUvScale. Dropping it does not
        // shift the texture, it RESIZES it — the surface is drawn at the wrong texel density and stops lining
        // up with its neighbours, which looks exactly like a broken texture alignment even though the UVs the
        // importer recovered are correct. Guarded against a zero scale, which would collapse the whole surface
        // onto one texel.
        if (diffuse.UvScale.X != 0f && diffuse.UvScale.Y != 0f)
            mat.Uv1Scale = new Vector3(diffuse.UvScale.X, diffuse.UvScale.Y, 1f);

        // Alpha-tested shaders (grates, ladders, foliage cards) are cut-outs: drawn opaque they become solid
        // rectangles, which reads as geometry the mapper does not have.
        if (diffuse.AlphaCutoff > 0f)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            mat.AlphaScissorThreshold = diffuse.AlphaCutoff;
            mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;   // cut-outs are authored to be seen both ways
        }
        else if (diffuse.Translucent)
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }

        EditorMaterials[shaderName] = mat;
        return mat;
    }

    /// <summary>Drop cached materials (called when a session closes so textures are not pinned forever).</summary>
    public static void ClearMaterialCache() => EditorMaterials.Clear();

    private static (int, int, int) CellKey(NVec3 quakePosition) => (
        (int)MathF.Floor(quakePosition.X / CellSize),
        (int)MathF.Floor(quakePosition.Y / CellSize),
        (int)MathF.Floor(quakePosition.Z / CellSize));

    /// <summary>
    /// Per-(cell, material) vertex accumulator. Vertices are re-indexed as triangles are added, because a
    /// source surface's vertices are shared across cells and each cell needs its own compact buffer.
    /// </summary>
    private sealed class CellSurface
    {
        private readonly Dictionary<int, int> _remap = new();

        public CellSurface(string material) => Material = material;

        public string Material { get; }
        public List<Vector3> Positions { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> Uvs { get; } = new();
        public List<int> Indices { get; } = new();

        public void AddTriangle(VmapSurface source, int i0, int i1, int i2)
        {
            Indices.Add(Map(source, i0));
            Indices.Add(Map(source, i1));
            Indices.Add(Map(source, i2));
        }

        private int Map(VmapSurface source, int sourceIndex)
        {
            if (_remap.TryGetValue(sourceIndex, out int local))
                return local;

            local = Positions.Count;
            _remap[sourceIndex] = local;

            Positions.Add(Coords.ToGodot(source.Positions[sourceIndex]));
            Normals.Add(Coords.ToGodot(source.Normals[sourceIndex]));
            NVec2 uv = source.Uvs[sourceIndex];
            Uvs.Add(new Vector2(uv.X, uv.Y));
            return local;
        }

        public void Pack(ArrayMesh mesh)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = Positions.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = Normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = Uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = Indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
    }
}

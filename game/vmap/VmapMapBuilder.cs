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
    /// <param name="includeSky">Keep sky-flagged faces as drawable geometry (the editor's Base/wireframe views).</param>
    public static Node3D BuildMap(VmapDocument doc, AssetSystem assets, bool includeSky = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(assets);

        var root = new Node3D { Name = "VmapWorld" };
        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc, includeSky);

        // Bucket every triangle into its spatial cell, keyed by cell then material.
        var cells = new Dictionary<(int X, int Y, int Z), Dictionary<string, CellSurface>>();

        foreach (VmapSurface surface in surfaces)
        {
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
                materials.Add(assets.ResolveMaterial(material));
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

        return root;
    }

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

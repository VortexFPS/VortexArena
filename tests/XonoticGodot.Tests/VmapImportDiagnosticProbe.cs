using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vmap;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Diagnostic probe comparing what a compiled BSP actually DRAWS against what the vmap importer regenerates
/// from the same file's brush lump. Opt-in via <c>XG_PROBE_BSP</c> (a path to a .bsp) because it needs real
/// map data; skipped everywhere else.
///
/// The editor's render path is "re-derive polygons from planes", which is only trustworthy if it lands on the
/// same set of surfaces the compiler produced. This measures the gap directly rather than inferring it from
/// screenshots: per-shader triangle coverage, shaders present in one representation and absent from the
/// other, and brush-winding orientation against each side's own plane normal.
/// </summary>
public class VmapImportDiagnosticProbe
{
    private readonly ITestOutputHelper _out;

    public VmapImportDiagnosticProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public void CompareBspRenderFacesAgainstVmapSurfaces()
    {
        string? path = Environment.GetEnvironmentVariable("XG_PROBE_BSP");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        byte[] bytes = File.ReadAllBytes(path);
        BspData bsp = BspReader.Read(bytes);
        VmapDocument doc = BspToVmap.Import(bsp, Path.GetFileNameWithoutExtension(path), path);

        _out.WriteLine($"=== {Path.GetFileName(path)} ===");
        _out.WriteLine($"bsp: {bsp.Brushes.Length} brushes, {bsp.Faces.Length} faces, {bsp.Models.Length} models");
        _out.WriteLine($"doc: {doc.Brushes.Count} brushes, {doc.Patches.Count} patches");

        // ---- 1. which brushes belong to which inline model -------------------------------------
        var bySubmodel = new SortedDictionary<int, int>();
        foreach (VmapBrush b in doc.Brushes)
            bySubmodel[b.SubmodelIndex] = bySubmodel.GetValueOrDefault(b.SubmodelIndex) + 1;
        _out.WriteLine($"brushes per submodel: {string.Join(", ", bySubmodel.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        _out.WriteLine($"tool brushes: {doc.Brushes.Count(b => b.IsToolBrush)}");

        // ---- 2. per-shader triangle coverage, BSP faces vs regenerated surfaces ------------------
        var bspTris = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bspVerts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (BspFace f in bsp.Faces)
        {
            string shader = bsp.Textures[f.TextureIndex].ShaderName;
            int tris = f.Type == BspFaceType.Patch ? 0 : f.IndexCount / 3;
            bspTris[shader] = bspTris.GetValueOrDefault(shader) + tris;
            bspVerts[shader] = bspVerts.GetValueOrDefault(shader) + f.VertexCount;
        }

        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc, includeSky: true);
        var vmapTris = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (VmapSurface s in surfaces)
            vmapTris[s.Material] = vmapTris.GetValueOrDefault(s.Material) + s.TriangleCount;

        _out.WriteLine($"bsp drawn tris (non-patch): {bspTris.Values.Sum()}   vmap tris: {vmapTris.Values.Sum()}");

        // Shaders the compiler drew but we regenerate nothing for: real missing geometry.
        var missing = bspTris
            .Where(kv => kv.Value > 0 && !VmapBrush.IsToolMaterial(kv.Key) && vmapTris.GetValueOrDefault(kv.Key) == 0)
            .OrderByDescending(kv => kv.Value)
            .ToList();
        _out.WriteLine($"--- shaders DRAWN by bsp but MISSING from vmap: {missing.Count} ---");
        foreach ((string shader, int tris) in missing.Take(25))
            _out.WriteLine($"  {tris,7} tris  {shader}");

        // Shaders we regenerate that the compiler drew nothing for: geometry that should not be visible.
        var extra = vmapTris
            .Where(kv => kv.Value > 0 && bspTris.GetValueOrDefault(kv.Key) == 0)
            .OrderByDescending(kv => kv.Value)
            .ToList();
        _out.WriteLine($"--- shaders in vmap but NOT DRAWN by bsp: {extra.Count} ---");
        foreach ((string shader, int tris) in extra.Take(25))
            _out.WriteLine($"  {tris,7} tris  {shader}");

        // ---- 2b. per-shader DRAWN AREA. Triangle counts lie (the compiler splits faces against the tree);
        //          area is the honest measure of "is this surface present in the world at all".
        var bspArea = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Vector3 bspMin = new(float.MaxValue), bspMax = new(float.MinValue);
        foreach (BspFace f in bsp.Faces)
        {
            if (f.Type == BspFaceType.Patch)
                continue;
            string shader = bsp.Textures[f.TextureIndex].ShaderName;
            double a = 0;
            for (int i = 0; i + 2 < f.IndexCount; i += 3)
            {
                Vector3 p0 = bsp.Vertices[f.FirstVertex + bsp.Triangles[f.FirstIndex + i]].Position;
                Vector3 p1 = bsp.Vertices[f.FirstVertex + bsp.Triangles[f.FirstIndex + i + 1]].Position;
                Vector3 p2 = bsp.Vertices[f.FirstVertex + bsp.Triangles[f.FirstIndex + i + 2]].Position;
                a += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).Length();
                bspMin = Vector3.Min(bspMin, Vector3.Min(p0, Vector3.Min(p1, p2)));
                bspMax = Vector3.Max(bspMax, Vector3.Max(p0, Vector3.Max(p1, p2)));
            }
            bspArea[shader] = bspArea.GetValueOrDefault(shader) + a;
        }

        var vmapArea = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Vector3 vmMin = new(float.MaxValue), vmMax = new(float.MinValue);
        foreach (VmapSurface s in surfaces)
        {
            double a = 0;
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
            {
                Vector3 p0 = s.Positions[s.Indices[i]], p1 = s.Positions[s.Indices[i + 1]], p2 = s.Positions[s.Indices[i + 2]];
                a += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).Length();
                vmMin = Vector3.Min(vmMin, Vector3.Min(p0, Vector3.Min(p1, p2)));
                vmMax = Vector3.Max(vmMax, Vector3.Max(p0, Vector3.Max(p1, p2)));
            }
            vmapArea[s.Material] = vmapArea.GetValueOrDefault(s.Material) + a;
        }

        _out.WriteLine($"bsp bounds {bspMin} .. {bspMax}");
        _out.WriteLine($"vmap bounds {vmMin} .. {vmMax}");
        _out.WriteLine($"total drawn area: bsp {bspArea.Values.Sum():N0}   vmap {vmapArea.Values.Sum():N0}");

        // ---- 2c. the same measure after occlusion culling: does it land on the compiler's answer? ----------
        var options = new VmapSurfaceOptions { IncludeSky = true, CullOccludedFaces = true };
        IReadOnlyList<VmapSurface> culled = VmapGeometryBuilder.BuildSurfaces(doc, options);   // warm up the JIT
        var sw = System.Diagnostics.Stopwatch.StartNew();
        culled = VmapGeometryBuilder.BuildSurfaces(doc, options);
        sw.Stop();
        var swPlain = System.Diagnostics.Stopwatch.StartNew();
        VmapGeometryBuilder.BuildSurfaces(doc, new VmapSurfaceOptions { IncludeSky = true });
        swPlain.Stop();
        _out.WriteLine($"build cost: {swPlain.ElapsedMilliseconds} ms without culling, {sw.ElapsedMilliseconds} ms with");
        var culledArea = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (VmapSurface s in culled)
        {
            double a = 0;
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
            {
                Vector3 p0 = s.Positions[s.Indices[i]], p1 = s.Positions[s.Indices[i + 1]], p2 = s.Positions[s.Indices[i + 2]];
                a += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).Length();
            }
            culledArea[s.Material] = culledArea.GetValueOrDefault(s.Material) + a;
        }
        _out.WriteLine($"AFTER occlusion culling: area {culledArea.Values.Sum():N0} "
            + $"(bsp {bspArea.Values.Sum():N0}, unculled {vmapArea.Values.Sum():N0}) in {sw.ElapsedMilliseconds} ms");
        _out.WriteLine("--- residual per-shader gap after culling (worst 12) ---");
        foreach (var kv in culledArea.OrderByDescending(kv => Math.Abs(kv.Value - bspArea.GetValueOrDefault(kv.Key))).Take(12))
            _out.WriteLine($"  culled {kv.Value,12:N0}  bsp {bspArea.GetValueOrDefault(kv.Key),12:N0}   {kv.Key}");

        // ---- 2f. TRIANGLE winding, against the same reference the working BSP render path uses. Both feed
        //          Godot through the identical orientation-preserving Coords.ToGodot, so if the compiled
        //          faces wind one way and the regenerated ones the other, the editor world is inside out and
        //          every near surface is backface-culled — you would see straight through it into the sky.
        int bspFwd = 0, bspRev = 0;
        foreach (BspFace f in bsp.Faces)
        {
            if (f.Type != BspFaceType.Flat)
                continue;
            for (int i = 0; i + 2 < f.IndexCount; i += 3)
            {
                BspVertex v0 = bsp.Vertices[f.FirstVertex + bsp.Triangles[f.FirstIndex + i]];
                BspVertex v1 = bsp.Vertices[f.FirstVertex + bsp.Triangles[f.FirstIndex + i + 1]];
                BspVertex v2 = bsp.Vertices[f.FirstVertex + bsp.Triangles[f.FirstIndex + i + 2]];
                Vector3 cross = Vector3.Cross(v1.Position - v0.Position, v2.Position - v0.Position);
                if (cross.LengthSquared() < 1e-6f)
                    continue;
                if (Vector3.Dot(cross, v0.Normal) > 0) bspFwd++; else bspRev++;
            }
        }

        int vmFwd = 0, vmRev = 0;
        foreach (VmapSurface s in culled)
        {
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
            {
                Vector3 p0 = s.Positions[s.Indices[i]], p1 = s.Positions[s.Indices[i + 1]], p2 = s.Positions[s.Indices[i + 2]];
                Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                if (cross.LengthSquared() < 1e-6f)
                    continue;
                if (Vector3.Dot(cross, s.Normals[s.Indices[i]]) > 0) vmFwd++; else vmRev++;
            }
        }
        _out.WriteLine($"--- triangle winding vs vertex normal: bsp {bspFwd} agree / {bspRev} opposed, "
            + $"vmap {vmFwd} agree / {vmRev} opposed ---");

        _out.WriteLine("--- biggest AREA surpluses in vmap (drawn but the compiler drew far less) ---");
        foreach (var kv in vmapArea.OrderByDescending(kv => kv.Value - bspArea.GetValueOrDefault(kv.Key)).Take(15))
            _out.WriteLine($"  vmap {kv.Value,12:N0}  bsp {bspArea.GetValueOrDefault(kv.Key),12:N0}   {kv.Key}");

        _out.WriteLine("--- biggest AREA deficits (compiler drew it, we do not) ---");
        foreach (var kv in bspArea.OrderByDescending(kv => kv.Value - vmapArea.GetValueOrDefault(kv.Key)).Take(15))
            _out.WriteLine($"  bsp {kv.Value,12:N0}  vmap {vmapArea.GetValueOrDefault(kv.Key),12:N0}   {kv.Key}");

        // ---- 3. winding orientation: does each generated polygon face the way its plane says? ----
        int checkedFaces = 0, flipped = 0, degenerate = 0;
        foreach (VmapBrush brush in doc.Brushes)
        {
            Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);
            for (int i = 0; i < windings.Length; i++)
            {
                Vector3[] w = windings[i];
                if (w.Length < 3)
                    continue;
                Vector3 newell = Vector3.Zero;
                for (int v = 0; v < w.Length; v++)
                {
                    Vector3 a = w[v], b = w[(v + 1) % w.Length];
                    newell += new Vector3(
                        (a.Y - b.Y) * (a.Z + b.Z),
                        (a.Z - b.Z) * (a.X + b.X),
                        (a.X - b.X) * (a.Y + b.Y));
                }
                float len = newell.Length();
                if (len < 1e-4f)
                {
                    degenerate++;
                    continue;
                }
                checkedFaces++;
                if (Vector3.Dot(newell / len, brush.Faces[i].Plane.Normal) < 0f)
                    flipped++;
            }
        }
        _out.WriteLine($"--- winding orientation: {checkedFaces} checked, {flipped} flipped, {degenerate} degenerate ---");

        // ---- 3b. how many generated faces are BURIED inside another solid brush? -----------------
        // q3map2's CSG drops exactly these: a brush side with solid on both sides was never drawn, so if the
        // regenerated world keeps them it is painting the inside of the level's own masonry over the rooms.
        var bounds = new (Vector3 Min, Vector3 Max)[doc.Brushes.Count];
        var planes = new (Vector3 N, float D)[doc.Brushes.Count][];
        for (int i = 0; i < doc.Brushes.Count; i++)
        {
            VmapBrush b = doc.Brushes[i];
            Vector3[] pts = VmapWinding.BrushPoints(b);
            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
            foreach (Vector3 p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            bounds[i] = (mn, mx);
            var pl = new (Vector3 N, float D)[b.Faces.Count];
            for (int k = 0; k < b.Faces.Count; k++)
                pl[k] = (b.Faces[k].Plane.Normal, b.Faces[k].Plane.Dist);
            planes[i] = pl;
        }

        int buried = 0, exposed = 0, buriedTris = 0, exposedTris = 0;
        for (int i = 0; i < doc.Brushes.Count; i++)
        {
            VmapBrush brush = doc.Brushes[i];
            if (brush.IsToolBrush)
                continue;
            Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);
            for (int fi = 0; fi < windings.Length; fi++)
            {
                Vector3[] w = windings[fi];
                if (w.Length < 3 || VmapBrush.IsToolMaterial(brush.Faces[fi].Material))
                    continue;

                Vector3 c = Vector3.Zero;
                foreach (Vector3 p in w) c += p;
                c /= w.Length;
                // A point just OUTSIDE the face. If that is inside another solid, nobody can see this face.
                Vector3 probe = c + brush.Faces[fi].Plane.Normal * 0.5f;

                bool inside = false;
                for (int j = 0; j < doc.Brushes.Count && !inside; j++)
                {
                    if (j == i || doc.Brushes[j].IsToolBrush)
                        continue;
                    (Vector3 mn, Vector3 mx) = bounds[j];
                    if (probe.X < mn.X || probe.X > mx.X || probe.Y < mn.Y || probe.Y > mx.Y
                        || probe.Z < mn.Z || probe.Z > mx.Z)
                        continue;
                    bool all = true;
                    foreach ((Vector3 n, float d) in planes[j])
                        if (Vector3.Dot(probe, n) - d > -0.01f) { all = false; break; }
                    inside = all;
                }

                int tris = w.Length - 2;
                if (inside) { buried++; buriedTris += tris; }
                else { exposed++; exposedTris += tris; }
            }
        }
        _out.WriteLine($"--- face burial: {buried} faces ({buriedTris} tris) buried in solid, "
            + $"{exposed} faces ({exposedTris} tris) exposed ---");

        // ---- 4. how much of the world is drawn from geometry with no brush at all ----------------
        int meshTris = bsp.Faces.Where(f => f.Type == BspFaceType.Mesh).Sum(f => f.IndexCount / 3);
        int flatTris = bsp.Faces.Where(f => f.Type == BspFaceType.Flat).Sum(f => f.IndexCount / 3);
        int patchFaces = bsp.Faces.Count(f => f.Type == BspFaceType.Patch);
        _out.WriteLine($"bsp face mix: flat {flatTris} tris, mesh(no brush) {meshTris} tris, patch faces {patchFaces} (doc has {doc.Patches.Count})");

        // ---- 5. faces per model: how much of what is drawn lives in a non-world inline model -----
        var facesPerModel = new SortedDictionary<int, int>();
        for (int mi = 0; mi < bsp.Models.Length; mi++)
        {
            BspModel m = bsp.Models[mi];
            int tris = 0;
            for (int i = 0; i < m.FaceCount; i++)
            {
                BspFace f = bsp.Faces[m.FirstFace + i];
                if (f.Type != BspFaceType.Patch)
                    tris += f.IndexCount / 3;
            }
            facesPerModel[mi] = tris;
        }
        _out.WriteLine("drawn tris per model: " + string.Join(", ",
            facesPerModel.Where(kv => kv.Value > 0).Take(20).Select(kv => $"{kv.Key}:{kv.Value}")));
    }
}

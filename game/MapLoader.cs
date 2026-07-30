using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Materials;
using VortexArena.Engine.Collision;
using VortexArena.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game;

/// <summary>
/// Turns a parsed <see cref="BspData"/> (the Godot-free IBSP loader output) into Godot scene geometry
/// and an engine <see cref="CollisionWorld"/>. Four products:
///
///  * <see cref="BuildMap"/>       — render geometry: an <see cref="ArrayMesh"/> surface per material
///    (texture + lightmap page), textured via the <see cref="AssetSystem"/> material facade, with
///    bezier patches tessellated (<see cref="BezierPatch"/>) and lightmaps wired as UV2 + a lightmap
///    <see cref="ShaderMaterial"/>.
///  * <see cref="BuildCollision"/> — the brush set fed to the trace service (Quake coords, contentflags),
///    plus a secondary Godot <see cref="StaticBody3D"/> trimesh for engine-physics needs.
///  * <see cref="SpawnPoints"/>    — info_player_* spawn entities parsed from the entity lump.
///  * <see cref="BuildWorldspawn"/>— fog/gravity/sky keys from the worldspawn entity.
///
/// Render geometry is converted to Godot's Y-up space at the vertex level (<see cref="Coords.ToGodot"/>);
/// collision stays in Quake space because the whole sim/trace stack runs in Quake coords.
/// </summary>
public static class MapLoader
{
    // -------------------------------------------------------------------------------------------------
    //  Render geometry
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Identifies one ArrayMesh surface. Faces are grouped by their texture (so each surface gets one
    /// material) AND their lightmap page (a lightmapped surface needs its page's texture baked into a
    /// per-page <see cref="ShaderMaterial"/>; faces that share a texture but land on different pages must
    /// therefore split). Non-lightmapped faces all share <see cref="LightmapIndex"/> = -1.
    /// </summary>
    private readonly record struct SurfaceKey(int TextureIndex, int LightmapIndex);

    /// <summary>
    /// Sentinel <see cref="SurfaceKey.LightmapIndex"/> for a q3map2 <i>vertex-lit</i> surface — a face with a
    /// negative lightmap index (e.g. -3) and no <c>.shader</c> entry, which DP shades from the per-vertex RGB
    /// (MODE_VERTEXCOLOR) rather than a lightmap page. Kept distinct from -1 (truly unlit / shader-owned) so
    /// these faces bucket into their own vertex-color material with a packed COLOR array.
    /// </summary>
    private const int VertexLitKey = -2;

    /// <summary>
    /// Sentinel <see cref="SurfaceKey.LightmapIndex"/> for a lightmapped surface AFTER the atlas regroup
    /// (§12.5 R5b): every per-page bucket merges onto this one key per texture (UV2s already remapped into
    /// atlas space), so lightmapped materials/draw-surfaces no longer multiply by page count.
    /// </summary>
    private const int AtlasLitKey = -4;

    /// <summary>
    /// World-space cell edge (Quake units) for the frustum-culling split (§12.5 R5a). 1024 qu ≈ a large room:
    /// big enough that cells×materials stays in the low hundreds of draw surfaces, small enough that looking
    /// at a wall culls most of the map. Triangles are binned by centroid; a tri spanning a border lands in
    /// exactly one cell (no seams — geometry is unchanged, only its mesh grouping).
    /// </summary>
    private const float WorldCellSizeDefault = 1024f;

    /// <summary>
    /// (§12.5) Resolve the spatial-cell edge length for this map. Default is the fixed
    /// <see cref="WorldCellSizeDefault"/> (1024) — today's behavior, unchanged. With <c>r_world_cell_adaptive 1</c>
    /// it scales to the map's size so the cell COUNT (and thus draw-call count) stays bounded: small/enclosed maps
    /// get finer cells (more PVS/frustum culling), large/open maps stay coarse (protecting draw calls — where a
    /// fixed small size would regress). <c>r_world_cell_div</c> is roughly "cells along the longest axis";
    /// <c>r_world_cell_min</c>/<c>_max</c> clamp it. Read once at map load (a build-time mesh parameter — changing
    /// it needs a map reload). Falls back to the default if the cvar store isn't up (e.g. a non-client build).
    /// </summary>
    private static float ResolveCellSize(BspData bsp)
    {
        var cv = VortexArena.Game.Menu.MenuState.Cvars;
        if (cv is null)
            return WorldCellSizeDefault;
        float fixedSize = cv.GetFloat("r_world_cell_size");
        if (fixedSize < 64f)
            fixedSize = WorldCellSizeDefault;        // unset / garbage → default
        if (cv.GetFloat("r_world_cell_adaptive") == 0f)
            return fixedSize;

        float div = cv.GetFloat("r_world_cell_div"); if (div < 1f) div = 8f;
        float lo = cv.GetFloat("r_world_cell_min");  if (lo < 64f) lo = 256f;
        float hi = cv.GetFloat("r_world_cell_max");  if (hi < lo) hi = 4096f;
        if (bsp.Models.Length == 0)
            return fixedSize;
        BspModel world = bsp.Models[0];               // worldspawn bounds (Quake; max-axis is swap-invariant)
        float ext = MathF.Max(world.Maxs.X - world.Mins.X,
                    MathF.Max(world.Maxs.Y - world.Mins.Y, world.Maxs.Z - world.Mins.Z));
        return ext > 0f ? Math.Clamp(ext / div, lo, hi) : fixedSize;
    }

    /// <summary>One spatial cell of the split world mesh, keyed by floor(centroid / cell size).</summary>
    private readonly record struct CellKey(int X, int Y, int Z);

    /// <summary>
    /// The packed lightmap (+ deluxe) atlas (§12.5 R5b): one texture holding every USED page in a uniform
    /// grid with replicated-edge gutters (bilinear sampling at a page border reads the same texels the
    /// standalone page's CLAMP would — no bleed between unrelated pages), plus each page's UV2 transform.
    /// </summary>
    private sealed class LightmapAtlas
    {
        public Texture2D Lightmap = null!;
        public Texture2D? Deluxe;
        public readonly Dictionary<int, (Vector2 Offset, Vector2 Scale)> PageUv = new();
        public int PageCount => PageUv.Count;
    }

    /// <summary>Per-surface accumulation buffers (Quake-space positions; converted at pack time).</summary>
    private sealed class SurfaceBuilder
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<Vector2> Uv2 = new();   // lightmap UVs (only meaningful for lightmapped surfaces)
        public readonly List<Color> Colors = new();  // per-vertex RGBA (only packed for vertex-lit surfaces)
        public readonly List<int> Indices = new();

        /// <summary>(§12.8) Source BSP face index per TRIANGLE (parallel to <see cref="Indices"/>/3, same order),
        /// so the cell regroup can union each triangle's face → PVS-cluster set (lump-5 leaffaces) into its cell.
        /// Appended one entry per complete triangle by the face/patch appenders.</summary>
        public readonly List<int> TriangleFaces = new();
    }

    /// <summary>
    /// Build the map's render geometry. Renderable faces (polygon/mesh + tessellated bezier patches) are
    /// grouped into surfaces keyed by texture+lightmap; each surface becomes one <see cref="ArrayMesh"/>
    /// surface with its material assigned via the <paramref name="assets"/> facade. Faces flagged nodraw,
    /// sky-with-no-surface, or flares are skipped.
    ///
    /// <paramref name="mapName"/> (e.g. "maps/foo" or just "foo") is used only for the external-lightmap
    /// fallback (<c>lm_NNNN</c>) when the BSP carries no internal lightmap pages; pass empty to disable it.
    ///
    /// <paramref name="droppedSubmodels"/> (optional) names inline-model indices the active gametype filters
    /// out (see <see cref="VortexArena.Engine.Collision.MapEntityFilter.DroppedSubmodels"/>): the faces owned by
    /// those <c>"*N"</c> brush entities are skipped so a gametype-conditional barrier doesn't render in a
    /// gametype it doesn't belong to. Null/empty → render every face (prior behavior).
    /// </summary>
    public static Node3D BuildMap(BspData bsp, AssetSystem assets, string mapName = "",
        IReadOnlySet<int>? droppedSubmodels = null)
    {
        var root = new Node3D { Name = "Map" };

        var surfaces = new Dictionary<SurfaceKey, SurfaceBuilder>();

        // Warpzone/portal-shader faces are pulled OUT of the merged cell meshes and rebuilt as their own per-plane
        // MeshInstance3D nodes (the see-through portal "window" meshes); PortalRenderer turns matched ones into a
        // live SubViewport render. Empty on the vast majority of maps (no portals) → zero overhead.
        var portalFaces = new List<int>();

        // Warpzone POCKET DECOR — the authored backdrop (`warpzone_backdrop`, the dark quad ~64qu behind each
        // window) and the additive rims (`blueedge`/`rededge`). They render normally for the player, but the
        // PORTAL CAMERA sits inside that pocket (behind the exit plane) and the backdrop otherwise fills the
        // whole exit view (the "portal shows black" report — the near-clip can't save it at viewing angles, when
        // the min-corner depth collapses). Pulled into their own nodes so PortalRenderer can layer them OFF the
        // portal cameras, exactly like the window quads themselves.
        var decorFaces = new List<int>();

        // Faces belonging to a filtered-out "*N" brush model (resolved from the models lump's face ranges).
        bool[]? dropFace = BuildDroppedFaceMask(bsp, droppedSubmodels);

        // Whether the map's lightmaps are paired with deluxe (light-direction) pages. BspReader can't resolve
        // this for an external-lightmap map (no VFS at parse time), so refine the decision here (see
        // EffectiveDeluxemapped). Computed once and threaded into the per-surface material build.
        bool deluxe = EffectiveDeluxemapped(bsp, assets, mapName);

        // --- bucket polygon/mesh faces ---
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];

            if (face.Type != BspFaceType.Flat && face.Type != BspFaceType.Mesh)
                continue; // patches handled below; flares have no geometry
            if (face.IndexCount <= 0 || face.VertexCount <= 0)
                continue;
            if (dropFace is not null && dropFace[fi])
                continue; // gametype-filtered brush entity
            if (ShouldSkip(bsp, assets, face.TextureIndex))
                continue;

            if (IsPortalCameraShader(bsp, assets, face.TextureIndex))
            {
                portalFaces.Add(fi); // its own MeshInstance3D below; kept OUT of the cell mesh (no double-draw)
                continue;
            }
            if (IsWarpzoneDecorShader(bsp, assets, face.TextureIndex))
            {
                decorFaces.Add(fi); // warpzone pocket decor (backdrop/rims) — own node so portal cams can skip it
                continue;
            }

            int lm = LightmapKeyForFace(bsp, assets, face);
            SurfaceBuilder sb = GetSurface(surfaces, face.TextureIndex, lm);
            AppendPolygonFace(sb, bsp, face, fi);
        }

        // --- bucket bezier patches (tessellated) into the same surface keys ---
        // S2: BezierPatch.Tessellate is pure CPU and independent per face (the patch-heavy load-time cost), so
        // tessellate the patches in PARALLEL, then append the results SEQUENTIALLY in face order — the append
        // touches the shared surface builders + the asset facade (not thread-safe), and the fixed order keeps the
        // packed mesh byte-identical to the old serial build. The skip / texture / lightmap-key decisions stay on
        // the main thread (they read the asset facade); only the math runs on the worker threads.
        var patchJobs = new List<(int Fi, int TexIndex, int Lm)>();
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Patch)
                continue;
            if (dropFace is not null && dropFace[fi])
                continue; // gametype-filtered brush entity
            if (ShouldSkip(bsp, assets, face.TextureIndex))
                continue;
            patchJobs.Add((fi, face.TextureIndex, LightmapKeyForFace(bsp, assets, face)));
        }
        if (patchJobs.Count > 0)
        {
            var tessResults = new BezierPatch.Tessellation?[patchJobs.Count];
            System.Threading.Tasks.Parallel.For(0, patchJobs.Count, j =>
                tessResults[j] = BezierPatch.Tessellate(bsp.Faces[patchJobs[j].Fi], bsp.Vertices));
            for (int j = 0; j < patchJobs.Count; j++)
            {
                BezierPatch.Tessellation? tess = tessResults[j];
                if (tess is null)
                    continue;
                SurfaceBuilder sb = GetSurface(surfaces, patchJobs[j].TexIndex, patchJobs[j].Lm);
                AppendTessellation(sb, tess, patchJobs[j].Fi);
            }
        }

        // --- (§12.8 A/B) build a conservative occluder from the OPAQUE world surfaces for Godot's native
        // occlusion culling (gated behind r_occlusion_cull; WorldOcclusion toggles it at runtime). Built from
        // the render mesh, NOT brushes — finalrage alone has ~17.6k bevel-padded solid brushes (hundreds of k
        // of tris) vs ~24k render tris, and the render surface is both lower-poly and guaranteed conservative.
        // Built unconditionally (cheap, load-time only) so the cvar can A/B-toggle without a map reload.
        Occluder3D? worldOccluder = BuildWorldOccluder(bsp, assets, surfaces);

        // --- (§12.5 R5b) lightmap atlas: pack every USED page (+ its deluxe pair) into one gutter-padded
        // texture so lightmapped surfaces no longer split per page — materials (and with the regroup below,
        // draw calls) collapse from textures × pages to just textures. Null when the map has no loadable
        // pages; lit keys then degrade exactly like today's null-lightmap path.
        LightmapAtlas? atlas = BuildLightmapAtlas(bsp, assets, mapName, deluxe, surfaces.Keys);

        // --- (§12.5 R5a+b) regroup: split every triangle into a spatial CELL (frustum culling — the old
        // single MeshInstance3D drew the whole map every frame) and merge the per-page lightmap buckets onto
        // the atlas (UV2 remapped per page). Appends above stay byte-identical; this is a pure repack.
        // (§12.8) The regroup also records each cell's visibility clusters so the PVS culler below can hide
        // cells the map compiler PROVED can't be seen from the camera's cluster (occlusion the frustum can't).
        // Cluster labels are DP's EXACT per-surface vis — the union of clusters of every leaf that references a
        // face (lump-5 leaffaces) — not a sampled point, so no face is mislabeled into solid (the old bug).
        var pvs = new VortexArena.Formats.Bsp.BspPvs(bsp);
        int[][]? faceClusters = pvs.HasVis ? pvs.BuildFaceClusterSets() : null;
        float cellSize = ResolveCellSize(bsp);
        var cells = RegroupIntoCells(surfaces, atlas, faceClusters, cellSize,
            out Dictionary<CellKey, HashSet<int>> cellClusters);

        // --- pack each cell into its own ArrayMesh + MeshInstance3D, sharing materials across cells ---
        int nLit = 0, nLitMissing = 0, nVtx = 0, nPlain = 0, nGlow = 0, nTrans = 0, nNormal = 0; // surface-material tally (logged below)
        var materialCache = new Dictionary<SurfaceKey, Material>();
        var pvsCells = new List<(MeshInstance3D Node, int[] Clusters)>();
        int cellCount = 0, drawSurfaces = 0;
        foreach (var cellKv in cells)
        {
            var cellMesh = new ArrayMesh();
            int surfaceIndex = 0;
            foreach (var kv in cellKv.Value)
            {
                SurfaceBuilder sb = kv.Value;
                if (sb.Indices.Count == 0 || sb.Positions.Count == 0)
                    continue;

                bool lightmapped = kv.Key.LightmapIndex >= 0 || kv.Key.LightmapIndex == AtlasLitKey;
                bool vertexLit = kv.Key.LightmapIndex == VertexLitKey;
                // The lightmap shader reads a per-surface tangent frame for the deluxemap directional diffuse term
                // (DP MODE_LIGHTDIRECTIONMAP_MODELSPACE). Generate tangents for every lightmapped surface so that
                // shader's TANGENT/BINORMAL inputs are always backed by real mesh data; on a non-deluxemapped map
                // they go unused (use_deluxemap is off) for only a little load-time work. Vertex-lit surfaces carry
                // a COLOR array instead (their modulation source); lightmapped surfaces carry UV2.
                PackSurface(cellMesh, sb, lightmapped, withTangents: lightmapped, withColor: vertexLit);

                // One material per MERGED key, shared across every cell (same instance → shared GPU state).
                if (!materialCache.TryGetValue(kv.Key, out Material? mat))
                {
                    mat = kv.Key.LightmapIndex == AtlasLitKey
                        ? ResolveAtlasSurfaceMaterial(bsp, assets, kv.Key.TextureIndex, atlas!)
                        : ResolveSurfaceMaterial(bsp, assets, kv.Key, mapName, deluxe);
                    materialCache[kv.Key] = mat;

                    // Tally ONCE per unique material (the per-page split is gone, so this now counts the
                    // user-meaningful number: distinct lightmapped/vertex-lit/plain material families).
                    bool onLightmapShader = mat is ShaderMaterial sm && LightmapShader.IsLightmapShader(sm.Shader);
                    if (vertexLit) nVtx++;
                    else if (lightmapped) { if (onLightmapShader) nLit++; else nLitMissing++; }
                    else nPlain++;
                    if (onLightmapShader && mat is ShaderMaterial g &&
                        g.GetShaderParameter(LightmapShader.UseGlowUniform).AsBool()) nGlow++;
                    // _norm-mapped surfaces (per-pixel relief under the deluxe light). A regression that drops the
                    // LightmapDiffuse.Normal thread or the companion lookup takes this to 0 on a normal-mapped map.
                    if (onLightmapShader && mat is ShaderMaterial nmm &&
                        nmm.GetShaderParameter(LightmapShader.UseNormalUniform).AsBool()) nNormal++;
                    // Translucent (alpha-blended) lightmap surfaces — glass etc. (Q3 blendFunc blend). A regression
                    // that re-opaques these (e.g. losing the LightmapDiffuse.Translucent thread) drops this to 0.
                    if (mat is ShaderMaterial tr && tr.Shader == LightmapShader.TranslucentShader) nTrans++;
                }
                cellMesh.SurfaceSetMaterial(surfaceIndex, mat); // facade never returns null (checkerboard fallback)
                surfaceIndex++;
                drawSurfaces++;
            }
            if (surfaceIndex == 0)
                continue;

            // The world never casts realtime shadows: static shadowing is lightmap-authoritative (the
            // LightmapShader is unshaded, so the sun's cascades can't change the world's own look anyway), and
            // keeping the map out of the directional shadow pass saves re-rendering every surface into every
            // cascade each frame. Trade-off: dynamic-model shadows are no longer occluded by world geometry
            // (a player indoors keeps a sun shadow — consistent with the sun already lighting models indoors).
            var cellInstance = new MeshInstance3D
            {
                Name = $"Geometry_{cellKv.Key.X}_{cellKv.Key.Y}_{cellKv.Key.Z}",
                Mesh = cellMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            root.AddChild(cellInstance);
            cellClusters.TryGetValue(cellKv.Key, out HashSet<int>? clusters);
            pvsCells.Add((cellInstance, clusters is { Count: > 0 } ? System.Linq.Enumerable.ToArray(clusters) : System.Array.Empty<int>()));
            cellCount++;
        }

        // (§12.8) PVS-driven cell visibility: the map's own precomputed vis hides cells behind walls — true
        // occlusion culling, conservative by construction (q3map2 vis is a superset of real visibility),
        // exactly the data DP culls with. Only wired when the map actually carries vis.
        if (pvs.HasVis && pvsCells.Count > 0)
            root.AddChild(new WorldPvsCuller(pvs, pvsCells));

        // (§12.8 A/B) Godot-native occlusion culling — orthogonal to the PVS culler, off by default
        // (r_occlusion_cull 0). Present on every map (no vis dependency); WorldOcclusion self-gates on the cvar.
        if (worldOccluder is not null)
            root.AddChild(new WorldOcclusion(worldOccluder));

        // Surface-material summary — guards the external-lightmap wiring (lightmapped=0 or a non-zero
        // lightmapMissing on a stock map means lightmaps stopped binding; see LoadLightmap / the map-name thread).
        GD.Print($"[MapLoader] '{mapName}' materials: lightmapped={nLit} vertexLit={nVtx} plain={nPlain}" +
                 (nLitMissing > 0 ? $" lightmapMissing={nLitMissing}" : string.Empty) +
                 (nTrans > 0 ? $" translucent={nTrans}" : string.Empty) +
                 $" glow={nGlow} normalMapped={nNormal} (deluxe={deluxe}, internalPages={bsp.Lightmaps.Length}" +
                 $", atlas={(atlas is not null ? $"{atlas.PageCount}p" : "none")}, cellSize={cellSize:0}, cells={cellCount}, surfaces={drawSurfaces})");

        // Build the warpzone/portal "window" meshes (no-op when the map has none).
        BuildPortalSurfaces(root, bsp, assets, mapName, deluxe, portalFaces, decorFaces);

        return root;
    }

    // ---- Warpzone / portal surfaces (the see-through portal "window" meshes) ----------------------------------

    /// <summary>True when the face's shader is a CAMERA/portal surface — Base only camera-renders a surface whose
    /// shader carries the <c>dpcamera</c> directive (DP's r_water portal pass; e.g. <c>effects_warpzone/wavy</c>),
    /// so consult the parsed <see cref="ShaderDef.Camera"/> first. The sibling warpzone decor — the additive
    /// <c>blueedge</c>/<c>rededge</c> rims and the opaque <c>warpzone_backdrop</c> — carries NO dpcamera and must
    /// stay in the normal merged-mesh pipeline with its authored material (previously the name test pulled ALL
    /// <c>effects_warpzone/</c> faces out as portal windows: the coplanar rim then z-fought the real window with a
    /// duplicate portal quad, and the interior backdrop rendered as a bogus third portal). The name patterns remain
    /// only as the fallback for a portal-ish shader with no parsed def (a mirror without a script).</summary>
    private static bool IsPortalCameraShader(BspData bsp, AssetSystem assets, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;
        string name = (bsp.Textures[textureIndex].ShaderName ?? string.Empty).Replace('\\', '/');
        ShaderDef? def = assets.GetShader(name);
        if (def is not null)
            return def.Dp.Camera; // authoritative: dpcamera == a portal window; rims/backdrops stay normal faces
        string n = name.ToLowerInvariant();
        return n.Contains("/portals/") || n.StartsWith("portals/", StringComparison.Ordinal)
            || n.Contains("portals_") || n.Contains("mirror");
    }

    /// <summary>True for warpzone pocket DECOR — an <c>effects_warpzone/</c> shader WITHOUT <c>dpcamera</c> (the
    /// backdrop + blueedge/rededge rims). Rendered normally for the player, but pulled into its own node so the
    /// portal cameras (which sit inside the pocket, behind the exit plane) can cull it — the backdrop otherwise
    /// fills the portal view (see <see cref="VortexArena.Game.Client.PortalRenderer"/>).</summary>
    private static bool IsWarpzoneDecorShader(BspData bsp, AssetSystem assets, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;
        string name = (bsp.Textures[textureIndex].ShaderName ?? string.Empty).Replace('\\', '/');
        if (!name.ToLowerInvariant().Contains("/effects_warpzone/"))
            return false;
        return assets.GetShader(name) is not { Dp.Camera: true }; // the camera window itself is NOT decor
    }

    /// <summary>
    /// Rebuild the warpzone/portal faces as their own <see cref="MeshInstance3D"/> nodes under a "Portals" child
    /// (one per coplanar group = one warpzone surface), each tagged via node metadata with its plane (QUAKE origin
    /// + normal) so <see cref="VortexArena.Game.Client.PortalRenderer"/> can match it to a warpzone and swap in a
    /// live see-through render. They carry the existing dark-mirror placeholder material, so a map with no
    /// PortalRenderer (or an unmatched surface) renders EXACTLY as before — this only adds addressable meshes.
    /// </summary>
    private static void BuildPortalSurfaces(Node3D root, BspData bsp, AssetSystem assets, string mapName, bool deluxe,
        List<int> portalFaces, List<int> decorFaces)
    {
        if (portalFaces.Count == 0 && decorFaces.Count == 0)
            return;

        var portalsRoot = new Node3D { Name = "Portals" };
        int built = BuildPortalGroup(portalsRoot, bsp, assets, mapName, deluxe, portalFaces, decor: false, 0);
        int decor = BuildPortalGroup(portalsRoot, bsp, assets, mapName, deluxe, decorFaces, decor: true, built);
        if (built + decor > 0)
            root.AddChild(portalsRoot);
        GD.Print($"[MapLoader] '{mapName}' portal surfaces: {built} (from {portalFaces.Count} faces)"
            + (decor > 0 ? $", pocket decor: {decor} (from {decorFaces.Count} faces)" : ""));
    }

    /// <summary>One coplanar-grouped build pass for <see cref="BuildPortalSurfaces"/>: window faces get the
    /// <c>wz_origin</c>/<c>wz_normal</c> plane metas (PortalRenderer matches + swaps in the live render); DECOR
    /// faces (backdrop/rims) get <c>wz_decor</c> and keep their authored material — PortalRenderer only moves
    /// them onto the portal-excluded render layer. Returns the number of nodes built.</summary>
    private static int BuildPortalGroup(Node3D portalsRoot, BspData bsp, AssetSystem assets, string mapName,
        bool deluxe, List<int> faces, bool decor, int nameBase)
    {
        if (faces.Count == 0)
            return 0;

        // Coplanar grouping: quantize each face's plane (Quake normal direction + plane distance) so the (often
        // several) faces of one warpzone surface collapse into a single window mesh. Decor also groups by texture
        // (a rim and a backdrop must not merge even if somehow coplanar).
        var groupFaces = new Dictionary<(long, long, long, long, int), List<int>>();
        var groupPlane = new Dictionary<(long, long, long, long, int), (NVec3 NSum, NVec3 OSum, int N, int Lm)>();
        foreach (int fi in faces)
        {
            BspFace face = bsp.Faces[fi];
            FacePlaneQuake(bsp, face, out NVec3 nq, out NVec3 oq);
            NVec3 nn = nq.LengthSquared() > 1e-9f ? NVec3.Normalize(nq) : new NVec3(0f, 0f, 1f);
            var key = (
                (long)MathF.Round(nn.X * 32f), (long)MathF.Round(nn.Y * 32f), (long)MathF.Round(nn.Z * 32f),
                (long)MathF.Round(NVec3.Dot(oq, nn) / 8f), // 8-unit plane-distance buckets
                decor ? face.TextureIndex : 0);
            if (!groupFaces.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                groupFaces[key] = list;
                groupPlane[key] = (NVec3.Zero, NVec3.Zero, 0, LightmapKeyForFace(bsp, assets, face));
            }
            list.Add(fi);
            (NVec3 NSum, NVec3 OSum, int N, int Lm) acc = groupPlane[key];
            groupPlane[key] = (acc.NSum + nn, acc.OSum + oq, acc.N + 1, acc.Lm);
        }

        int built = 0;
        foreach (KeyValuePair<(long, long, long, long, int), List<int>> kv in groupFaces)
        {
            var sb = new SurfaceBuilder();
            int tex = bsp.Faces[kv.Value[0]].TextureIndex;
            foreach (int fi in kv.Value)
                AppendPolygonFace(sb, bsp, bsp.Faces[fi], fi);
            if (sb.Indices.Count == 0 || sb.Positions.Count == 0)
                continue;

            (NVec3 NSum, NVec3 OSum, int N, int Lm) acc = groupPlane[kv.Key];
            var mesh = new ArrayMesh();
            // Decor keeps its authored (potentially lightmapped) look; the window gets the placeholder that the
            // PortalRenderer material override replaces.
            PackSurface(mesh, sb, lightmapped: decor && acc.Lm >= 0, withTangents: false, withColor: false);
            Material decorMat = ResolveSurfaceMaterial(bsp, assets, new SurfaceKey(tex, acc.Lm), mapName, deluxe);
            // The blueedge/rededge RIM is an ADDITIVE glow decal (Q3 `blendfunc GL_SRC_ALPHA GL_ONE`) sitting a
            // hair IN FRONT of the window plane, spanning the whole window. When its blend is lost it compiles
            // to an OPAQUE StandardMaterial3D — a solid near-black quad that covers the portal entirely (THE
            // original "portal shows black" — it predates the portal renderer). Re-impose the authored additive
            // blend on the standard-material fallback path.
            if (decor && decorMat is StandardMaterial3D std
                && (bsp.Textures[tex].ShaderName ?? "").Replace('\\', '/').ToLowerInvariant().Contains("edge"))
            {
                std.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                std.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                std.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                std.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            }
            mesh.SurfaceSetMaterial(0, decorMat);

            NVec3 planeN = acc.NSum.LengthSquared() > 1e-9f ? NVec3.Normalize(acc.NSum) : new NVec3(0f, 0f, 1f);
            NVec3 planeO = acc.OSum / MathF.Max(1, acc.N);

            var mi = new MeshInstance3D
            {
                Name = decor ? $"PortalDecor_{nameBase + built}" : $"Portal_{nameBase + built}",
                Mesh = mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            if (decor)
            {
                mi.SetMeta("wz_decor", true);
            }
            else
            {
                // QUAKE-space plane (stored raw in a Vector3 holder), for PortalRenderer to match against the
                // WarpzoneManager zones (also Quake). NOT Coords-converted — the renderer reads them back as Quake.
                mi.SetMeta("wz_origin", new Vector3(planeO.X, planeO.Y, planeO.Z));
                mi.SetMeta("wz_normal", new Vector3(planeN.X, planeN.Y, planeN.Z));
            }
            portalsRoot.AddChild(mi);
            built++;
        }
        return built;
    }

    /// <summary>Area-unweighted plane of a BSP face in QUAKE space (centroid + averaged vertex normal).</summary>
    private static void FacePlaneQuake(BspData bsp, BspFace face, out NVec3 normalQuake, out NVec3 originQuake)
    {
        NVec3 nSum = NVec3.Zero, oSum = NVec3.Zero;
        int c = 0;
        for (int v = 0; v < face.VertexCount; v++)
        {
            int src = face.FirstVertex + v;
            if (src < 0 || src >= bsp.Vertices.Length)
                continue;
            BspVertex bv = bsp.Vertices[src];
            nSum += bv.Normal;
            oSum += bv.Position;
            c++;
        }
        normalQuake = c > 0 ? nSum : new NVec3(0f, 0f, 1f);
        originQuake = c > 0 ? oSum / c : NVec3.Zero;
    }

    /// <summary>
    /// Mark every render face that belongs to a filtered-out inline brush model. The BSP models lump gives each
    /// <c>"*N"</c> model a contiguous face range <c>[FirstFace, FirstFace+FaceCount)</c>; a face index in a
    /// dropped model's range is masked so <see cref="BuildMap"/> skips it. Returns null when nothing is dropped
    /// (the common case) so the per-face check is free. Model 0 (worldspawn) is never dropped.
    /// </summary>
    private static bool[]? BuildDroppedFaceMask(BspData bsp, IReadOnlySet<int>? droppedSubmodels)
    {
        if (droppedSubmodels is null || droppedSubmodels.Count == 0 || bsp.Models.Length == 0)
            return null;

        var mask = new bool[bsp.Faces.Length];
        bool any = false;
        foreach (int mi in droppedSubmodels)
        {
            if (mi < 1 || mi >= bsp.Models.Length)
                continue;
            BspModel m = bsp.Models[mi];
            int end = m.FirstFace + m.FaceCount;
            for (int fi = m.FirstFace; fi < end; fi++)
                if (fi >= 0 && fi < mask.Length)
                {
                    mask[fi] = true;
                    any = true;
                }
        }
        return any ? mask : null;
    }

    private static SurfaceBuilder GetSurface(Dictionary<SurfaceKey, SurfaceBuilder> surfaces, int texture, int lightmap)
    {
        // LightmapKeyForFace already returns the canonical key (>=0 page slot, -1 unlit, or VertexLitKey),
        // so pass it through verbatim — do NOT collapse negatives, that would merge vertex-lit with unlit.
        var key = new SurfaceKey(texture, lightmap);
        if (!surfaces.TryGetValue(key, out var sb))
        {
            sb = new SurfaceBuilder();
            surfaces[key] = sb;
        }
        return sb;
    }

    /// <summary>Append one polygon/mesh face's vertex window + index window into a surface (Quake->Godot).
    /// <paramref name="faceIndex"/> tags each appended triangle for the §12.8 cell→cluster regroup.</summary>
    private static void AppendPolygonFace(SurfaceBuilder sb, BspData bsp, BspFace face, int faceIndex)
    {
        int baseIndex = sb.Positions.Count;
        int indexBase = sb.Indices.Count;

        // Copy this face's vertex range [FirstVertex, FirstVertex+VertexCount).
        for (int v = 0; v < face.VertexCount; v++)
        {
            int src = face.FirstVertex + v;
            if (src < 0 || src >= bsp.Vertices.Length)
            {
                sb.Positions.Add(Vector3.Zero);
                sb.Normals.Add(Vector3.Up);
                sb.Uvs.Add(Vector2.Zero);
                sb.Uv2.Add(Vector2.Zero);
                sb.Colors.Add(Colors.White); // keep COLOR aligned with the other channels
                continue;
            }
            BspVertex bv = bsp.Vertices[src];
            sb.Positions.Add(Coords.ToGodot(bv.Position));
            sb.Normals.Add(Coords.ToGodot(bv.Normal));
            sb.Uvs.Add(new Vector2(bv.TexCoord.X, bv.TexCoord.Y));
            sb.Uv2.Add(new Vector2(bv.LightmapCoord.X, bv.LightmapCoord.Y));
            sb.Colors.Add(new Color(bv.Color.R / 255f, bv.Color.G / 255f, bv.Color.B / 255f, bv.Color.A / 255f));
        }

        // Indices are mesh-local into Triangles, relative to FirstVertex; rebase onto baseIndex.
        int end = face.FirstIndex + face.IndexCount;
        for (int e = face.FirstIndex; e < end; e++)
        {
            if (e < 0 || e >= bsp.Triangles.Length)
                continue;
            int local = bsp.Triangles[e]; // 0-based within this face's vertex window
            if (local < 0 || local >= face.VertexCount)
                local = 0;
            sb.Indices.Add(baseIndex + local);
        }

        // Tag each complete triangle just appended with its source face (parallel to Indices/3).
        for (int t = (sb.Indices.Count - indexBase) / 3; t > 0; t--)
            sb.TriangleFaces.Add(faceIndex);
    }

    /// <summary>Append a tessellated bezier patch (Quake-space vertices) into a surface (Quake->Godot).
    /// <paramref name="faceIndex"/> tags each appended triangle for the §12.8 cell→cluster regroup.</summary>
    private static void AppendTessellation(SurfaceBuilder sb, BezierPatch.Tessellation tess, int faceIndex)
    {
        int baseIndex = sb.Positions.Count;
        int indexBase = sb.Indices.Count;

        for (int i = 0; i < tess.Vertices.Count; i++)
        {
            BezierPatch.PatchVertex pv = tess.Vertices[i];
            sb.Positions.Add(Coords.ToGodot(pv.Position));
            sb.Normals.Add(Coords.ToGodot(pv.Normal));
            sb.Uvs.Add(new Vector2(pv.TexCoord.X, pv.TexCoord.Y));
            sb.Uv2.Add(new Vector2(pv.LightmapCoord.X, pv.LightmapCoord.Y));
            sb.Colors.Add(Colors.White); // patches carry no per-vertex color; a vertex-lit patch renders fullbright
        }

        for (int i = 0; i < tess.Indices.Count; i++)
            sb.Indices.Add(baseIndex + tess.Indices[i]);

        // Tag each complete triangle just appended with its source patch face (parallel to Indices/3).
        for (int t = (sb.Indices.Count - indexBase) / 3; t > 0; t--)
            sb.TriangleFaces.Add(faceIndex);
    }

    /// <summary>
    /// Add the accumulated buffers as one ArrayMesh surface (UV2 only when lightmapped). When
    /// <paramref name="withTangents"/> is set, a per-vertex tangent array is generated and attached so the
    /// lightmap shader can rotate the deluxe light direction into the surface tangent frame (DP
    /// MODE_LIGHTDIRECTIONMAP_MODELSPACE); every lightmapped surface requests it, and the deluxe diffuse
    /// path is its only consumer.
    /// </summary>
    private static void PackSurface(ArrayMesh mesh, SurfaceBuilder sb, bool lightmapped,
        bool withTangents = false, bool withColor = false)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = sb.Positions.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = sb.Normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = sb.Uvs.ToArray();
        if (lightmapped)
            arrays[(int)Mesh.ArrayType.TexUV2] = sb.Uv2.ToArray();
        if (withTangents)
            arrays[(int)Mesh.ArrayType.Tangent] = BuildTangents(sb);
        if (withColor)
            arrays[(int)Mesh.ArrayType.Color] = sb.Colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = sb.Indices.ToArray();

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>
    /// Build Godot's packed per-vertex tangent array (4 floats/vertex: xyz tangent + handedness w) for a
    /// lightmapped surface. The lightmap shader needs the tangent frame (DP VectorS/T/R =
    /// tangent/binormal/normal) to rotate the modelspace deluxe light direction into tangentspace and apply
    /// the <c>dot(N, lightdir)</c> diffuse term (<c>shader_glsl.h</c> MODE_LIGHTDIRECTIONMAP_MODELSPACE).
    ///
    /// Tangents are accumulated per triangle from the albedo-UV gradient (Lengyel's method) and then
    /// Gram-Schmidt orthonormalized against the vertex normal, so {tangent, binormal, normal} is an
    /// orthonormal basis. For a flat surface (no per-surface normalmap) only the normal axis affects the
    /// shaded result, so the in-plane tangent direction is not critical — but a valid, orthonormal frame is.
    /// All inputs are already Godot-space (positions/normals were converted at append time).
    /// </summary>
    private static float[] BuildTangents(SurfaceBuilder sb)
    {
        int n = sb.Positions.Count;
        var tangentAccum = new Vector3[n];   // ∂P/∂u direction, summed over adjacent triangles
        var bitangentAccum = new Vector3[n]; // ∂P/∂v direction, summed (used only for the handedness sign)

        for (int i = 0; i + 2 < sb.Indices.Count; i += 3)
        {
            int i0 = sb.Indices[i], i1 = sb.Indices[i + 1], i2 = sb.Indices[i + 2];
            Vector3 e1 = sb.Positions[i1] - sb.Positions[i0];
            Vector3 e2 = sb.Positions[i2] - sb.Positions[i0];
            Vector2 du1 = sb.Uvs[i1] - sb.Uvs[i0];
            Vector2 du2 = sb.Uvs[i2] - sb.Uvs[i0];

            float det = du1.X * du2.Y - du2.X * du1.Y;
            if (Mathf.Abs(det) < 1e-12f)
                continue; // degenerate UV mapping on this triangle; neighbors define the vertex tangent
            float r = 1.0f / det;
            Vector3 t = (e1 * du2.Y - e2 * du1.Y) * r;
            Vector3 b = (e2 * du1.X - e1 * du2.X) * r;

            tangentAccum[i0] += t; tangentAccum[i1] += t; tangentAccum[i2] += t;
            bitangentAccum[i0] += b; bitangentAccum[i1] += b; bitangentAccum[i2] += b;
        }

        var tangents = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            Vector3 nrm = sb.Normals[v];
            // Gram-Schmidt: project the accumulated tangent onto the plane perpendicular to the normal.
            Vector3 t = tangentAccum[v] - nrm * nrm.Dot(tangentAccum[v]);
            if (t.LengthSquared() < 1e-12f)
            {
                // No usable UV gradient here (flat-shaded/degenerate): synthesize any in-plane tangent so the
                // frame stays orthonormal. The shaded result for a flat surface does not depend on which one.
                Vector3 axis = Mathf.Abs(nrm.Z) < 0.99f ? Vector3.Back : Vector3.Right;
                t = nrm.Cross(axis);
            }
            t = t.Normalized();
            // Handedness w so Godot reconstructs BINORMAL = cross(NORMAL, TANGENT) * w in the UV-v direction.
            float w = nrm.Cross(t).Dot(bitangentAccum[v]) < 0.0f ? -1.0f : 1.0f;
            tangents[v * 4 + 0] = t.X;
            tangents[v * 4 + 1] = t.Y;
            tangents[v * 4 + 2] = t.Z;
            tangents[v * 4 + 3] = w;
        }
        return tangents;
    }

    /// <summary>
    /// Resolve the material for a surface. Lightmapped surfaces get a lightmap shader material built from
    /// the page texture (internal page uploaded as an <see cref="ImageTexture"/>, or an external
    /// <c>lm_NNNN</c> image) combined with the shader's albedo. Non-lightmapped surfaces resolve straight
    /// from the shader name. Falls back to a plain shader-name material if a lightmap texture is missing.
    ///
    /// On a deluxemapped map the matching deluxe page is also bound so the lightmap shader can apply the
    /// directional re-modulation (DP MODE_LIGHTDIRECTIONMAP_MODELSPACE). A static <c>tcMod scale</c> on the
    /// albedo stage (e.g. <c>map_catharsis/chain</c> — <c>tcMod scale 2 2</c>) is folded into the albedo UV
    /// scale (DP Q3TCMOD_SCALE), leaving the baked lightmap UV2 untouched.
    /// </summary>
    private static Material ResolveSurfaceMaterial(BspData bsp, AssetSystem assets, SurfaceKey key, string mapName, bool deluxe)
    {
        string shaderName = (key.TextureIndex >= 0 && key.TextureIndex < bsp.Textures.Length)
            ? bsp.Textures[key.TextureIndex].ShaderName
            : string.Empty;

        if (key.LightmapIndex == VertexLitKey)
        {
            // q3map2 vertex-lit surface (negative lightmap index, no .shader entry): modulate the diffuse by
            // the per-vertex COLOR, unshaded (DP MODE_VERTEXCOLOR). No shader exists for this name — that is
            // what classified it vertex-lit — so the albedo is the texture of that name; a null albedo falls
            // back to white (pure vertex lighting, the same as a missing diffuse on the lightmap path).
            return LightmapShader.MakeVertexLitMaterial(assets.LoadTexture(shaderName));
        }

        if (key.LightmapIndex >= 0)
        {
            Texture2D? lightmapTex = LoadLightmap(bsp, assets, key.LightmapIndex, mapName, deluxe);
            if (lightmapTex is not null)
            {
                // Albedo resolves through the shader's DIFFUSE STAGE, not a bare LoadTexture(shaderName): a Q3
                // shader's color image lives in a stage, so a shadered surface would otherwise come back white
                // (untextured). This also carries the diffuse stage's alpha-test cutoff (masked grates/foliage)
                // and static tcMod scale (DP Q3TCMOD_SCALE). A pure-shader/$lightmap surface yields a null
                // texture → the lightmap shader falls back to white (lighting only, no diffuse).
                AssetSystem.LightmapDiffuse diffuse = assets.ResolveLightmapDiffuse(shaderName);

                // Deluxemapped maps: bind the matching light-direction page so the lightmap shader applies
                // the 1/max(0.25, lightnormal.z) directional rescale (DP shader_glsl.h). Faces address the
                // de-interleaved Deluxemaps array with the same (already-halved) index as Lightmaps.
                Texture2D? deluxeTex = LoadDeluxemap(bsp, assets, key.LightmapIndex, mapName, deluxe);

                // Built directly (not via the AssetSystem facade) so the deluxe page / UV scale / alpha cutoff
                // / glow page / translucency reach the lightmap shader without widening the facade signature.
                // A blendFunc-blend diffuse (glass) routes to the translucent variant so it renders see-through.
                return LightmapShader.MakeMaterial(diffuse.Texture, lightmapTex, deluxemap: deluxeTex,
                    albedoUvScale: diffuse.UvScale, alphaCutoff: diffuse.AlphaCutoff, glow: diffuse.Glow,
                    translucent: diffuse.Translucent, normal: diffuse.Normal, gloss: diffuse.Gloss);
            }
            // No lightmap available — degrade to the plain material rather than dropping the surface.
        }

        return assets.ResolveMaterial(shaderName);
    }

    // =================================================================================================
    //  (§12.5 R5) Lightmap atlas + spatial regroup
    // =================================================================================================

    /// <summary>Gutter width (px) around each atlas cell, filled with replicated page edges.</summary>
    private const int AtlasGutter = 2;

    /// <summary>
    /// Pack every lightmap page the bucketed surfaces actually reference (plus, on a deluxemapped map, the
    /// paired light-direction pages into a SECOND atlas with the identical layout) and record each page's
    /// UV2 offset+scale. Returns null when no page loads (the caller then keeps today's degrade path).
    /// </summary>
    private static LightmapAtlas? BuildLightmapAtlas(BspData bsp, AssetSystem assets, string mapName,
        bool deluxe, IEnumerable<SurfaceKey> usedKeys)
    {
        // Distinct used pages, ordered for a deterministic layout.
        var pages = new List<int>();
        foreach (SurfaceKey key in usedKeys)
            if (key.LightmapIndex >= 0 && !pages.Contains(key.LightmapIndex))
                pages.Add(key.LightmapIndex);
        if (pages.Count == 0)
            return null;
        pages.Sort();

        // Load every page as a CPU Image (internal lump bytes or the external lm_NNNN file).
        var pageImages = new Dictionary<int, Image>();
        var deluxeImages = new Dictionary<int, Image>();
        int maxW = 0, maxH = 0;
        foreach (int k in pages)
        {
            Image? img = LoadLightmapImage(bsp, assets, k, mapName, deluxe);
            if (img is null)
                continue; // a missing page keeps its surfaces on the degrade path below
            if (img.GetFormat() != Image.Format.Rgb8)
                img.Convert(Image.Format.Rgb8);
            pageImages[k] = img;
            maxW = Math.Max(maxW, img.GetWidth());
            maxH = Math.Max(maxH, img.GetHeight());

            if (deluxe && LoadDeluxemapImage(bsp, assets, k, mapName) is { } dimg)
            {
                if (dimg.GetFormat() != Image.Format.Rgb8)
                    dimg.Convert(Image.Format.Rgb8);
                deluxeImages[k] = dimg;
            }
        }
        if (pageImages.Count == 0)
            return null;

        int cols = (int)Math.Ceiling(Math.Sqrt(pageImages.Count));
        int rows = (int)Math.Ceiling(pageImages.Count / (double)cols);
        int cellW = maxW + 2 * AtlasGutter, cellH = maxH + 2 * AtlasGutter;
        int atlasW = cols * cellW, atlasH = rows * cellH;

        Image atlasImg = Image.CreateEmpty(atlasW, atlasH, false, Image.Format.Rgb8);
        // Deluxe atlas (same layout). Cells with no deluxe page stay at the NEUTRAL "straight up" direction
        // (128,128,255 → unit +Z) so the shader's 1/max(0.25, z) rescale is exactly 1 there — a defensively
        // missing deluxe page must not brighten or darken its surfaces.
        Image? deluxeImg = null;
        if (deluxe && deluxeImages.Count > 0)
        {
            deluxeImg = Image.CreateEmpty(atlasW, atlasH, false, Image.Format.Rgb8);
            deluxeImg.Fill(new Color(128f / 255f, 128f / 255f, 1f));
        }

        var atlas = new LightmapAtlas();
        int slot = 0;
        foreach (int k in pages)
        {
            if (!pageImages.TryGetValue(k, out Image? img))
                continue;
            int cx = slot % cols, cy = slot / cols;
            int x = cx * cellW + AtlasGutter, y = cy * cellH + AtlasGutter;
            int w = img.GetWidth(), h = img.GetHeight();

            BlitWithGutter(atlasImg, img, x, y, w, h);
            if (deluxeImg is not null && deluxeImages.TryGetValue(k, out Image? dimg))
                BlitWithGutter(deluxeImg, dimg, x, y, w, h);

            atlas.PageUv[k] = (new Vector2((float)x / atlasW, (float)y / atlasH),
                               new Vector2((float)w / atlasW, (float)h / atlasH));
            slot++;
        }

        atlas.Lightmap = ImageTexture.CreateFromImage(atlasImg);
        if (deluxeImg is not null)
            atlas.Deluxe = ImageTexture.CreateFromImage(deluxeImg);
        return atlas;
    }

    /// <summary>Blit a page at (x, y) and fill the <see cref="AtlasGutter"/> ring around it with replicated
    /// edge texels (rows/columns/corners) — bilinear CLAMP semantics across cell borders.</summary>
    private static void BlitWithGutter(Image atlas, Image page, int x, int y, int w, int h)
    {
        atlas.BlitRect(page, new Rect2I(0, 0, w, h), new Vector2I(x, y));
        for (int g = 1; g <= AtlasGutter; g++)
        {
            atlas.BlitRect(page, new Rect2I(0, 0, w, 1), new Vector2I(x, y - g));          // top edge
            atlas.BlitRect(page, new Rect2I(0, h - 1, w, 1), new Vector2I(x, y + h + g - 1)); // bottom
            atlas.BlitRect(page, new Rect2I(0, 0, 1, h), new Vector2I(x - g, y));          // left
            atlas.BlitRect(page, new Rect2I(w - 1, 0, 1, h), new Vector2I(x + w + g - 1, y)); // right
        }
        for (int gy = 1; gy <= AtlasGutter; gy++)
            for (int gx = 1; gx <= AtlasGutter; gx++)
            {
                atlas.BlitRect(page, new Rect2I(0, 0, 1, 1), new Vector2I(x - gx, y - gy));                  // corners
                atlas.BlitRect(page, new Rect2I(w - 1, 0, 1, 1), new Vector2I(x + w + gx - 1, y - gy));
                atlas.BlitRect(page, new Rect2I(0, h - 1, 1, 1), new Vector2I(x - gx, y + h + gy - 1));
                atlas.BlitRect(page, new Rect2I(w - 1, h - 1, 1, 1), new Vector2I(x + w + gx - 1, y + h + gy - 1));
            }
    }

    /// <summary>The CPU <see cref="Image"/> for a de-interleaved lightmap slot (internal bytes or the
    /// external <c>lm_NNNN</c> file) — the atlas-side mirror of <see cref="LoadLightmap"/>.</summary>
    private static Image? LoadLightmapImage(BspData bsp, AssetSystem assets, int lightmapIndex, string mapName, bool deluxe)
    {
        if (bsp.Lightmaps.Length > 0)
        {
            if (lightmapIndex < 0 || lightmapIndex >= bsp.Lightmaps.Length)
                return null;
            return PageToImage(bsp.Lightmaps[lightmapIndex], bsp.LightmapWidth, bsp.LightmapHeight);
        }
        if (!string.IsNullOrEmpty(mapName))
        {
            int fileIndex = deluxe ? lightmapIndex * 2 : lightmapIndex;
            return assets.LoadImage($"maps/{ExternalLightmapBaseName(mapName)}/lm_{fileIndex:0000}");
        }
        return null;
    }

    /// <summary>The CPU <see cref="Image"/> for a slot's deluxe pair — the atlas-side mirror of
    /// <see cref="LoadDeluxemap"/> (call only on a deluxemapped map).</summary>
    private static Image? LoadDeluxemapImage(BspData bsp, AssetSystem assets, int lightmapIndex, string mapName)
    {
        if (bsp.Deluxemaps.Length > 0)
        {
            if (lightmapIndex < 0 || lightmapIndex >= bsp.Deluxemaps.Length)
                return null;
            byte[]? page = bsp.Deluxemaps[lightmapIndex];
            return page is null ? null : PageToImage(page, bsp.LightmapWidth, bsp.LightmapHeight);
        }
        if (!string.IsNullOrEmpty(mapName))
            return assets.LoadImage($"maps/{ExternalLightmapBaseName(mapName)}/lm_{lightmapIndex * 2 + 1:0000}");
        return null;
    }

    /// <summary>Raw tightly-packed RGB page bytes → an <see cref="Image"/> (pad/clip defensively).</summary>
    private static Image PageToImage(byte[] page, int width, int height)
    {
        int needed = width * height * 3;
        byte[] data = page;
        if (page.Length != needed)
        {
            data = new byte[needed];
            Array.Copy(page, data, Math.Min(page.Length, needed));
        }
        return Image.CreateFromData(width, height, false, Image.Format.Rgb8, data);
    }

    /// <summary>
    /// Repack the bucketed surfaces into spatial cells (R5a) while merging per-page lightmap buckets onto the
    /// atlas key (R5b, UV2 remapped per page). Pure data movement — appends upstream are untouched, geometry
    /// is unchanged; triangles bin by centroid; vertex sharing is preserved per (source, target) pair.
    /// Surfaces whose page missed the atlas (or when there is no atlas) keep their original key/UVs.
    /// </summary>
    private static Dictionary<CellKey, Dictionary<SurfaceKey, SurfaceBuilder>> RegroupIntoCells(
        Dictionary<SurfaceKey, SurfaceBuilder> surfaces, LightmapAtlas? atlas,
        int[][]? faceClusters, float cellSize, out Dictionary<CellKey, HashSet<int>> cellClusters)
    {
        var cells = new Dictionary<CellKey, Dictionary<SurfaceKey, SurfaceBuilder>>();
        var remap = new Dictionary<(CellKey Cell, int OldIndex), int>();   // vertex dedup per source bucket
        cellClusters = new Dictionary<CellKey, HashSet<int>>();

        foreach (var kv in surfaces)
        {
            SurfaceBuilder src = kv.Value;
            if (src.Indices.Count == 0)
                continue;

            // Merge lightmapped pages onto the atlas key when this page made it into the atlas.
            SurfaceKey targetKey = kv.Key;
            (Vector2 Offset, Vector2 Scale) uv = (Vector2.Zero, Vector2.One);
            if (atlas is not null && kv.Key.LightmapIndex >= 0
                && atlas.PageUv.TryGetValue(kv.Key.LightmapIndex, out var pageUv))
            {
                targetKey = new SurfaceKey(kv.Key.TextureIndex, AtlasLitKey);
                uv = pageUv;
            }

            remap.Clear();
            bool hasColor = src.Colors.Count > 0;
            for (int i = 0; i + 2 < src.Indices.Count; i += 3)
            {
                int a = src.Indices[i], b = src.Indices[i + 1], c = src.Indices[i + 2];
                Vector3 centroid = (src.Positions[a] + src.Positions[b] + src.Positions[c]) / 3f;
                var cell = new CellKey(
                    (int)MathF.Floor(centroid.X / cellSize),
                    (int)MathF.Floor(centroid.Y / cellSize),
                    (int)MathF.Floor(centroid.Z / cellSize));

                if (!cells.TryGetValue(cell, out Dictionary<SurfaceKey, SurfaceBuilder>? cellSurfaces))
                    cells[cell] = cellSurfaces = new Dictionary<SurfaceKey, SurfaceBuilder>();
                if (!cellSurfaces.TryGetValue(targetKey, out SurfaceBuilder? dst))
                    cellSurfaces[targetKey] = dst = new SurfaceBuilder();

                // (§12.8) Record which visibility clusters this cell's geometry occupies — the per-frame PVS
                // culler shows a cell iff ANY of its clusters is potentially visible from the camera's cluster
                // (the map compiler's own conservative vis; per-cell union = strictly MORE conservative than
                // DP's per-face culling, so nothing DP would draw is ever hidden). The cluster set per triangle
                // is DP's EXACT per-surface vis: the union of clusters of every leaf that references the source
                // face (lump-5 leaffaces; see BspPvs.BuildFaceClusterSets). This replaces the old point-sampling
                // (centroid → FindLeaf) which mislabeled ~10% of triangles whose centroid landed on the surface
                // boundary in solid — whole cells then winked out to the skybox. A face referenced only by solid
                // leaves contributes nothing; a cell with NO clusters is treated always-visible by the culler.
                if (faceClusters is not null)
                {
                    int tri = i / 3;
                    int face = tri < src.TriangleFaces.Count ? src.TriangleFaces[tri] : -1;
                    if (face >= 0 && face < faceClusters.Length)
                    {
                        int[] clusters = faceClusters[face];
                        if (clusters.Length > 0)
                        {
                            if (!cellClusters.TryGetValue(cell, out HashSet<int>? set))
                                cellClusters[cell] = set = new HashSet<int>();
                            foreach (int cl in clusters)
                                set.Add(cl);
                        }
                    }
                }

                dst.Indices.Add(CopyVertex(src, dst, a, cell, remap, uv, hasColor));
                dst.Indices.Add(CopyVertex(src, dst, b, cell, remap, uv, hasColor));
                dst.Indices.Add(CopyVertex(src, dst, c, cell, remap, uv, hasColor));
            }
        }
        return cells;
    }

    /// <summary>Copy one vertex from a source bucket into a cell's target builder (memoized per
    /// (cell, source index)), remapping UV2 into atlas space on the way.</summary>
    private static int CopyVertex(SurfaceBuilder src, SurfaceBuilder dst, int index, CellKey cell,
        Dictionary<(CellKey, int), int> remap, (Vector2 Offset, Vector2 Scale) uv, bool hasColor)
    {
        if (remap.TryGetValue((cell, index), out int mapped))
            return mapped;
        int n = dst.Positions.Count;
        dst.Positions.Add(src.Positions[index]);
        dst.Normals.Add(src.Normals[index]);
        dst.Uvs.Add(src.Uvs[index]);
        if (index < src.Uv2.Count)
        {
            Vector2 v = src.Uv2[index];
            dst.Uv2.Add(uv.Offset + new Vector2(v.X * uv.Scale.X, v.Y * uv.Scale.Y));
        }
        if (hasColor && index < src.Colors.Count)
            dst.Colors.Add(src.Colors[index]);
        remap[(cell, index)] = n;
        return n;
    }

    /// <summary>The shared material for an atlas-merged lightmapped surface family (one per texture): the
    /// regular lightmap-shader build, but bound to the ATLAS textures instead of a single page.</summary>
    private static Material ResolveAtlasSurfaceMaterial(BspData bsp, AssetSystem assets, int textureIndex,
        LightmapAtlas atlas)
    {
        string shaderName = (textureIndex >= 0 && textureIndex < bsp.Textures.Length)
            ? bsp.Textures[textureIndex].ShaderName
            : string.Empty;
        AssetSystem.LightmapDiffuse diffuse = assets.ResolveLightmapDiffuse(shaderName);
        return LightmapShader.MakeMaterial(diffuse.Texture, atlas.Lightmap, deluxemap: atlas.Deluxe,
            albedoUvScale: diffuse.UvScale, alphaCutoff: diffuse.AlphaCutoff, glow: diffuse.Glow,
            translucent: diffuse.Translucent, normal: diffuse.Normal, gloss: diffuse.Gloss);
    }

    /// <summary>
    /// Get the lightmap page texture for the de-interleaved slot <paramref name="lightmapIndex"/>: upload the
    /// internal 128x128 RGB page as an <see cref="ImageTexture"/>, or — when the BSP has no internal pages —
    /// try the external <c>maps/&lt;mapname&gt;/lm_NNNN</c> image. Returns null if neither is available (clean
    /// fallback). Pages are cached on the <see cref="BspData"/> instance so repeated surfaces reuse one texture.
    ///
    /// On a deluxemapped map the on-disk <c>lm_NNNN</c> files still interleave lightmap/deluxe pages, so the
    /// de-interleaved slot <c>k</c> maps to file <c>lm_{2k}</c> (the even page) — mirroring DP, which merges the
    /// external set the same way it merges the internal lump (<c>Mod_Q3BSP_LoadLightmaps</c>).
    /// </summary>
    private static Texture2D? LoadLightmap(BspData bsp, AssetSystem assets, int lightmapIndex, string mapName, bool deluxe)
    {
        // Internal lightmap pages.
        if (bsp.Lightmaps.Length > 0)
        {
            if (lightmapIndex < 0 || lightmapIndex >= bsp.Lightmaps.Length)
                return null;
            if (_lightmapCache.TryGetValue((bsp, lightmapIndex), out Texture2D? cached))
                return cached;

            ImageTexture tex = UploadLightmapPage(bsp.Lightmaps[lightmapIndex], bsp.LightmapWidth, bsp.LightmapHeight);
            _lightmapCache[(bsp, lightmapIndex)] = tex;
            return tex;
        }

        // External lm_NNNN images (only if we know the map name and the facade can find them).
        if (!string.IsNullOrEmpty(mapName))
        {
            // Deluxemapped external set: lightmap slot k is the even file lm_{2k} (odd files are deluxe).
            int fileIndex = deluxe ? lightmapIndex * 2 : lightmapIndex;
            return assets.LoadTexture($"maps/{ExternalLightmapBaseName(mapName)}/lm_{fileIndex:0000}");
        }

        return null;
    }

    /// <summary>Reduce a map name ("maps/foo", "foo", or a path with extension) to the bare map basename.</summary>
    private static string ExternalLightmapBaseName(string mapName)
    {
        string baseName = mapName;
        int slash = baseName.Replace('\\', '/').LastIndexOf('/');
        if (slash >= 0)
            baseName = baseName[(slash + 1)..];
        int dot = baseName.LastIndexOf('.');
        if (dot >= 0)
            baseName = baseName[..dot];
        return baseName;
    }

    /// <summary>
    /// Get the deluxe (light-direction) page that pairs with the de-interleaved slot
    /// <paramref name="lightmapIndex"/>, or null when the map is not deluxemapped. The slot index matches the
    /// (already-halved) lightmap index because <see cref="BspData.Deluxemaps"/> is de-interleaved in lockstep
    /// with <see cref="BspData.Lightmaps"/> (DP <c>Mod_Q3BSP_LoadLightmaps</c>: even page = lightmap, odd =
    /// deluxe). Internal pages upload from the byte buffer; an external deluxemapped set reads file
    /// <c>lm_{2k+1}</c> (the odd page) through the VFS. Uploaded as plain RGB (a packed direction, not a
    /// color), cached per slot.
    /// </summary>
    private static Texture2D? LoadDeluxemap(BspData bsp, AssetSystem assets, int lightmapIndex, string mapName, bool deluxe)
    {
        if (!deluxe)
            return null;
        if (lightmapIndex < 0)
            return null;
        if (_deluxemapCache.TryGetValue((bsp, lightmapIndex), out Texture2D? cached))
            return cached;

        // Internal deluxe pages (de-interleaved out of the lightmap lump).
        if (bsp.Deluxemaps.Length > 0)
        {
            if (lightmapIndex >= bsp.Deluxemaps.Length)
                return null;
            byte[]? page = bsp.Deluxemaps[lightmapIndex];
            if (page is null)
                return null;
            ImageTexture tex = UploadLightmapPage(page, bsp.LightmapWidth, bsp.LightmapHeight);
            _deluxemapCache[(bsp, lightmapIndex)] = tex;
            return tex;
        }

        // External deluxemapped set: deluxe slot k is the odd file lm_{2k+1}.
        if (!string.IsNullOrEmpty(mapName))
        {
            Texture2D? ext = assets.LoadTexture(
                $"maps/{ExternalLightmapBaseName(mapName)}/lm_{lightmapIndex * 2 + 1:0000}");
            _deluxemapCache[(bsp, lightmapIndex)] = ext;
            return ext;
        }

        return null;
    }

    /// <summary>Upload a raw 128x128 RGB lightmap page to an <see cref="ImageTexture"/> (RGB8, no mips).</summary>
    private static ImageTexture UploadLightmapPage(byte[] page, int width, int height)
    {
        // BSP pages are tightly-packed RGB (3 bytes/texel); Godot's Rgb8 format matches that layout.
        int needed = width * height * 3;
        byte[] data = page;
        if (page.Length != needed)
        {
            // Pad/clip defensively so a short/long page still produces a valid texture.
            data = new byte[needed];
            Array.Copy(page, data, Math.Min(page.Length, needed));
        }

        Image img = Image.CreateFromData(width, height, false, Image.Format.Rgb8, data);
        return ImageTexture.CreateFromImage(img);
    }

    // Cache of uploaded internal lightmap pages, keyed by (map, page index), so the many surfaces that
    // share one page reuse a single GPU texture rather than re-uploading the 48 KB image each time.
    private static readonly Dictionary<(BspData, int), Texture2D?> _lightmapCache = new();

    // Same idea for the de-interleaved deluxe (light-direction) pages on a deluxemapped map.
    private static readonly Dictionary<(BspData, int), Texture2D?> _deluxemapCache = new();

    /// <summary>
    /// The effective "is this map deluxemapped" decision, refined for external-lightmap maps that
    /// <see cref="VortexArena.Formats.Bsp.BspReader"/> could not resolve at parse time (no VFS there, so it can't
    /// probe the external pages). DP detects deluxemapping on the external set the same way as the internal
    /// lump: an even page count whose faces only reference even lightmap indices, minus the q3map2 "blank
    /// padding" special case. For a single-lightmap external map (faces index only slot 0) the count-1-vs-2
    /// case is ambiguous without the files, so BspReader conservatively reports "not deluxemapped"; here we have
    /// the VFS, so probe whether the <c>lm_0001</c> page exists and is a real (non-blank) deluxe page and
    /// upgrade the decision to match DP.
    ///
    /// Only the single-lightmap (max referenced slot 0) case can be upgraded — a map with a referenced slot
    /// >= 2 or any odd index was already decided authoritatively by BspReader — and for slot 0 the
    /// <see cref="BspData.RealLightmapIndex"/> halving is a no-op, so this refinement only governs whether the
    /// deluxe page is bound, never the lightmap slot a face resolves to.
    /// </summary>
    private static bool EffectiveDeluxemapped(BspData bsp, AssetSystem assets, string mapName)
    {
        if (bsp.IsDeluxemapped)
            return true;
        if (bsp.Lightmaps.Length != 0)
            return false;                       // internal-page map: BspReader's decision was authoritative
        if (string.IsNullOrEmpty(mapName))
            return false;
        if (!AllFaceLightmapIndicesEven(bsp))
            return false;                       // an odd lightmap index means lm_0001 is a real lightmap page
        // DP keeps deluxemapping when the paired page exists and is not a blank q3map2 pad.
        Texture2D? d1 = assets.LoadTexture($"maps/{ExternalLightmapBaseName(mapName)}/lm_0001");
        return d1 is not null && !IsTextureNearBlack(d1);
    }

    /// <summary>True if no face references an ODD lightmap index (which would make lm_0001 a real lightmap page,
    /// not a deluxe page). Negative indices (unlit / vertex-lit) are ignored.</summary>
    private static bool AllFaceLightmapIndicesEven(BspData bsp)
    {
        foreach (BspFace f in bsp.Faces)
            if (f.LightmapIndex >= 0 && (f.LightmapIndex & 1) != 0)
                return false;
        return true;
    }

    /// <summary>
    /// True if a candidate deluxe page is effectively all-black — q3map2's blank padding rather than a real
    /// light-direction page (a real deluxe page encodes directions around (128,128,255)). Sampled sparsely; an
    /// unreadable image is assumed real (returns false) so a decode quirk never silently drops deluxe lighting.
    /// </summary>
    private static bool IsTextureNearBlack(Texture2D tex)
    {
        Image img = tex.GetImage();
        if (img is null)
            return false;
        if (img.IsCompressed())
            img.Decompress();
        int w = img.GetWidth(), h = img.GetHeight();
        if (w <= 0 || h <= 0)
            return false;
        int step = Math.Max(1, Math.Min(w, h) / 16);
        for (int y = 0; y < h; y += step)
        for (int x = 0; x < w; x += step)
        {
            Color c = img.GetPixel(x, y);
            if (c.R > 0.02f || c.G > 0.02f || c.B > 0.02f)
                return false;
        }
        return true;
    }

    /// <summary>
    /// True if the face's texture should be skipped from the render mesh: NODRAW (caulk/clip/trigger
    /// shaders) and sky draw nothing here. The decision unions two authorities — the Q3 surface bits the
    /// reader stored on <see cref="BspTexture.SurfaceFlags"/>, AND the shader's <c>surfaceparm</c> set
    /// (resolved through <paramref name="assets"/>), because many Xonotic surfaces declare
    /// <c>nodraw</c>/<c>sky</c> only in the <c>.shader</c> with no BSP bit set.
    /// </summary>
    private static bool ShouldSkip(BspData bsp, AssetSystem assets, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;

        // BSP lump bits.
        int flags = bsp.Textures[textureIndex].SurfaceFlags;
        const int skipMask = Q3SurfaceFlags.NoDraw | Q3SurfaceFlags.Sky;
        if ((flags & skipMask) != 0)
            return true;

        // Shader surfaceparm bits (nodraw / sky).
        SurfaceFlags.SurfaceInfo info = assets.GetSurfaceInfo(bsp.Textures[textureIndex].ShaderName);
        return info.NoDraw || info.Sky;
    }

    /// <summary>
    /// (§12.8 A/B) Merge every OPAQUE world surface into one <see cref="ArrayOccluder3D"/> for Godot's native
    /// occlusion culling (driven by <see cref="WorldOcclusion"/>). Positions are already Godot world-space
    /// (ToGodot'd at append), so the occluder lines up 1:1 with the rendered solid — it can never extend past
    /// real geometry and over-cull. Translucent, liquid, nonsolid (grates/fences), sky and nodraw surfaces are
    /// excluded so nothing visible THROUGH them is wrongly hidden. Returns null when nothing qualifies.
    /// </summary>
    private static Occluder3D? BuildWorldOccluder(BspData bsp, AssetSystem assets,
        Dictionary<SurfaceKey, SurfaceBuilder> surfaces)
    {
        var verts = new List<Vector3>();
        var indices = new List<int>();
        foreach (var kv in surfaces)
        {
            if (!IsOpaqueOccluder(bsp, assets, kv.Key.TextureIndex))
                continue;
            SurfaceBuilder sb = kv.Value;
            int baseIndex = verts.Count;
            verts.AddRange(sb.Positions);
            foreach (int idx in sb.Indices)
                indices.Add(baseIndex + idx);
        }
        if (indices.Count == 0)
            return null;

        var occ = new ArrayOccluder3D();
        occ.SetArrays(verts.ToArray(), indices.ToArray());
        GD.Print($"[MapLoader] occluder: {verts.Count} verts / {indices.Count / 3} tris (r_occlusion_cull A/B)");
        return occ;
    }

    /// <summary>
    /// True if a texture's surfaces are safe to use as solid occluders: opaque, solid, and actually drawn.
    /// Excludes everything <see cref="ShouldSkip"/> drops (sky/nodraw) plus translucent / liquid / nonsolid
    /// shaders and the compiler-baked NONSOLID bit — any of which would let the camera see PAST the surface,
    /// so occluding behind it would pop real geometry in/out.
    /// </summary>
    private static bool IsOpaqueOccluder(BspData bsp, AssetSystem assets, int textureIndex)
    {
        if (ShouldSkip(bsp, assets, textureIndex))
            return false;
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;
        if ((bsp.Textures[textureIndex].SurfaceFlags & Q3SurfaceFlags.NonSolid) != 0)
            return false;
        SurfaceFlags.SurfaceInfo info = assets.GetSurfaceInfo(bsp.Textures[textureIndex].ShaderName);
        return !info.NonSolid && !info.Translucent && !info.Liquid;
    }

    /// <summary>
    /// The de-interleaved lightmap page a face renders with, or -1 if it should render unlit (no lightmap).
    /// Returns -1 when the face has no valid <see cref="BspFace.LightmapIndex"/> OR the shader suppresses
    /// lightmaps (<c>surfaceparm nolightmap</c>/sky/liquid/fog) — those surfaces must not split into a lightmap
    /// material, and feeding them a page would double-light. Note <c>surfaceparm trans</c> does NOT suppress:
    /// alpha-masked grates and lit glass keep the lightmap their BSP face carries.
    ///
    /// On a deluxemapped map the face's stored index counts both lightmap and deluxe pages, so it is halved
    /// (<see cref="BspData.RealLightmapIndex"/>) to address the de-interleaved <see cref="BspData.Lightmaps"/>
    /// array — this is what keeps the light-direction pages out of the lightmap atlas.
    /// </summary>
    private static int LightmapForFace(BspData bsp, AssetSystem assets, BspFace face)
    {
        if (face.LightmapIndex < 0)
            return -1;
        if (face.TextureIndex >= 0 && face.TextureIndex < bsp.Textures.Length)
        {
            SurfaceFlags.SurfaceInfo info = assets.GetSurfaceInfo(bsp.Textures[face.TextureIndex].ShaderName);
            if (info.NoLightmap)
                return -1;
        }
        return bsp.RealLightmapIndex(face.LightmapIndex);
    }

    /// <summary>
    /// The bucketing key for a face's lighting: a real de-interleaved lightmap slot (>= 0), the vertex-lit
    /// sentinel (<see cref="VertexLitKey"/>), or -1 (unlit / shader-owned). A face with no lightmap page that
    /// is a plain default-shaded world surface (no <c>.shader</c> entry) is vertex-lit — q3map2 baked its
    /// lighting into the per-vertex RGB (DP MODE_VERTEXCOLOR) — so it must not be dropped into a realtime-lit
    /// StandardMaterial3D; surfaces WITH a shader keep that shader (it carries its own rgbGen/lighting).
    /// </summary>
    private static int LightmapKeyForFace(BspData bsp, AssetSystem assets, BspFace face)
    {
        int lm = LightmapForFace(bsp, assets, face);
        if (lm >= 0)
            return lm;
        return IsVertexLitCandidate(bsp, assets, face) ? VertexLitKey : -1;
    }

    /// <summary>
    /// True if a face with no lightmap page should render vertex-lit (DP MODE_VERTEXCOLOR): it has no
    /// <c>.shader</c> entry, so it falls on Q3's default shading where, lacking a lightmap, the per-vertex RGB
    /// is the lighting. Sky/nodraw faces are already removed by <see cref="ShouldSkip"/> before bucketing, so
    /// the only discriminator left is whether a shader owns the surface's coloring.
    /// </summary>
    private static bool IsVertexLitCandidate(BspData bsp, AssetSystem assets, BspFace face)
    {
        if (face.TextureIndex < 0 || face.TextureIndex >= bsp.Textures.Length)
            return false;
        return assets.GetShader(bsp.Textures[face.TextureIndex].ShaderName) is null;
    }

    // -------------------------------------------------------------------------------------------------
    //  Collision
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Convert every BSP brush into an engine <see cref="Brush"/> and build the broadphase grid. Each
    /// brush side's plane comes from <see cref="BspData.Planes"/>; the brush carries the content flags
    /// from its texture entry (lava/slime/clip/solid) which drive the trace's SUPERCONTENTS mask. Brushes
    /// with no derivable geometry (fewer than 4 valid planes / degenerate windings) are skipped.
    /// Everything is in Quake coords — the trace service consumes it directly.
    ///
    /// <paramref name="assets"/> is accepted for signature symmetry (surface-flag lookups, future shader
    /// content overrides); brush contents are the BSP texture's RAW Q3 native flags converted to SUPERCONTENTS
    /// inside <see cref="BspCollisionBuilder"/> (DP's Mod_Q3BSP_SuperContentsFromNativeContents) — NOT the raw
    /// on-disk bits, which would alias the trace masks.
    /// </summary>
    public static CollisionWorld BuildCollision(BspData bsp, AssetSystem? assets = null)
    {
        _ = assets;
        // The brush-building moved to the Godot-free engine layer (BspCollisionBuilder) so the headless server
        // and unit tests can build collision without Godot. This returns only the static world (worldspawn);
        // the inline "*N" brush models are registered separately via BspCollisionBuilder.RegisterSubmodels so
        // SOLID_BSP entities (doors/plats) clip against their real moving brushes instead of an AABB.
        return BspCollisionBuilder.Build(bsp).World;
    }

    /// <summary>
    /// Build a secondary Godot collision body from the render triangles (a single concave trimesh). This
    /// is optional and independent of the engine <see cref="CollisionWorld"/> — it exists only for Godot's
    /// own physics (e.g. ragdolls, dropped pickups, area overlaps) that want a <see cref="StaticBody3D"/>
    /// in the scene tree. Sky/nodraw faces are skipped; patches are included (tessellated). Returns a
    /// <see cref="StaticBody3D"/> ready to add to the scene, or null if there is nothing to collide.
    /// </summary>
    public static StaticBody3D? BuildCollisionMesh(BspData bsp)
    {
        var faces = new List<Vector3>();

        // Polygon/mesh faces: expand each index into a triangle vertex (concave shapes take a flat soup).
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Flat && face.Type != BspFaceType.Mesh)
                continue;
            if (ShouldSkipCollision(bsp, face.TextureIndex))
                continue;

            int end = face.FirstIndex + face.IndexCount;
            for (int e = face.FirstIndex; e < end; e++)
            {
                if (e < 0 || e >= bsp.Triangles.Length)
                    continue;
                int local = bsp.Triangles[e];
                int src = face.FirstVertex + local;
                if (src < 0 || src >= bsp.Vertices.Length)
                    continue;
                faces.Add(Coords.ToGodot(bsp.Vertices[src].Position));
            }
        }

        // Bezier patches: triangulated.
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Patch)
                continue;
            if (ShouldSkipCollision(bsp, face.TextureIndex))
                continue;
            BezierPatch.Tessellation? tess = BezierPatch.Tessellate(face, bsp.Vertices);
            if (tess is null)
                continue;
            for (int i = 0; i < tess.Indices.Count; i++)
                faces.Add(Coords.ToGodot(tess.Vertices[tess.Indices[i]].Position));
        }

        if (faces.Count < 3)
            return null;

        var shape = new ConcavePolygonShape3D { Data = faces.ToArray() };
        var body = new StaticBody3D { Name = "MapCollision" };
        body.AddChild(new CollisionShape3D { Name = "Trimesh", Shape = shape });
        return body;
    }

    /// <summary>Trimesh skip mask: drop nodraw and non-solid hint surfaces (but keep sky as a wall).</summary>
    private static bool ShouldSkipCollision(BspData bsp, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;
        return (bsp.Textures[textureIndex].SurfaceFlags & (Q3SurfaceFlags.NoDraw | Q3SurfaceFlags.NonSolid)) != 0;
    }

    /// <summary>
    /// Compute the corner vertices of a convex brush from its bounding planes: intersect every triple of
    /// planes and keep the points that lie inside (or on) all planes. This is the standard Quake
    /// brush-winding derivation; the trace service uses these points for its SAT projections.
    /// </summary>
    // -------------------------------------------------------------------------------------------------
    //  Spawn points
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parse player spawn entities from the entity lump. Matches any classname beginning with
    /// <c>info_player_</c> (covers <c>info_player_deathmatch</c>, <c>info_player_start</c>,
    /// <c>info_player_team*</c>). Yields each spawn's Quake-space origin and yaw angle (the entity
    /// <c>"angle"</c> key, default 0). Origin/angle stay in Quake coords for the sim.
    /// </summary>
    public static IEnumerable<(string classname, NVec3 origin, float angle)> SpawnPoints(BspData bsp)
    {
        foreach (var ent in bsp.Entities)
        {
            if (!ent.TryGetValue("classname", out string? classname) || classname is null)
                continue;
            if (!classname.StartsWith("info_player_", StringComparison.Ordinal))
                continue;

            NVec3 origin = NVec3.Zero;
            if (ent.TryGetValue("origin", out string? originStr) && originStr is not null)
                origin = ParseVec3(originStr);

            float angle = 0f;
            if (ent.TryGetValue("angle", out string? angleStr) && angleStr is not null)
                angle = ParseFloat(angleStr);

            yield return (classname, origin, angle);
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Worldspawn
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Map-wide environment settings pulled from the <c>worldspawn</c> entity (the first entity in the
    /// lump). All fields are optional; unset keys keep their defaults. Colors are linear RGB 0..1, the
    /// gravity is the Quake scalar (Xonotic default 800), and <see cref="Sky"/> is the skybox basename.
    /// </summary>
    public readonly struct Worldspawn
    {
        public readonly string Message;       // map title ("message")
        public readonly string Sky;           // skybox basename ("sky" / "_skybox" / "skyname")
        public readonly float Gravity;        // "gravity" (default 800)
        public readonly bool HasFog;
        public readonly Color FogColor;       // parsed from "fog" (… r g b …)
        public readonly float FogDensity;     // density term from "fog" (DP fog_density)
        public readonly float FogAlpha;       // max fog opacity (DP fog_alpha; default 1)
        public readonly float FogStart;       // distance before which there is no fog (DP fog_start; default 0)
        public readonly float FogEnd;         // distance at which fog buildup stops (DP fog_end; default 16384)

        public Worldspawn(string message, string sky, float gravity, bool hasFog, Color fogColor,
            float fogDensity, float fogAlpha, float fogStart, float fogEnd)
        {
            Message = message;
            Sky = sky;
            Gravity = gravity;
            HasFog = hasFog;
            FogColor = fogColor;
            FogDensity = fogDensity;
            FogAlpha = fogAlpha;
            FogStart = fogStart;
            FogEnd = fogEnd;
        }
    }

    /// <summary>
    /// Read the worldspawn entity for environment keys (sky, gravity, fog, title). Quake/Xonotic store the
    /// skybox under <c>sky</c>/<c>_skybox</c>/<c>skyname</c>, gravity under <c>gravity</c>, and a fog
    /// directive under <c>fog</c> as <c>"density r g b ..."</c> (DP's R_GetFogFromShader-style string).
    /// Returns defaults (gravity 800, no fog) when worldspawn is absent.
    /// </summary>
    public static Worldspawn BuildWorldspawn(BspData bsp)
    {
        string message = string.Empty;
        string sky = string.Empty;
        float gravity = 800f;
        bool hasFog = false;
        Color fogColor = new Color(0.3f, 0.3f, 0.3f);
        float fogDensity = 0f;
        float fogAlpha = 1f;
        float fogStart = 0f;
        float fogEnd = 16384f;   // DP default fog_end (gl_rmain.c R_UpdateFog).

        if (bsp.Entities.Count > 0)
        {
            IReadOnlyDictionary<string, string> ws = FindWorldspawn(bsp);

            if (ws.TryGetValue("message", out string? m) && m is not null)
                message = m;

            sky = FirstNonEmpty(ws, "sky", "_skybox", "skyname", "skybox");

            if (ws.TryGetValue("gravity", out string? g) && g is not null)
            {
                float parsed = ParseFloat(g);
                if (parsed > 0f)
                    gravity = parsed;
            }

            if (ws.TryGetValue("fog", out string? fog) && !string.IsNullOrWhiteSpace(fog))
            {
                // DP fog string: "<density> <r> <g> <b> [alpha] [start] [end] [...]" (density first, then
                // colour 0..1, then max-opacity alpha, then the start/end distances). See cl_parse.c (sscanf)
                // + gl_rmain.c R_BuildFogTexture.
                var parts = fog.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    hasFog = true;
                    fogDensity = ParseFloat(parts[0]);
                    if (parts.Length >= 4)
                        fogColor = new Color(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
                    if (parts.Length >= 5)
                        fogAlpha = ParseFloat(parts[4]);
                    if (parts.Length >= 6)
                        fogStart = ParseFloat(parts[5]);
                    if (parts.Length >= 7 && ParseFloat(parts[6]) > 0f)
                        fogEnd = ParseFloat(parts[6]);
                }
            }
        }

        return new Worldspawn(message, sky, gravity, hasFog, fogColor, fogDensity, fogAlpha, fogStart, fogEnd);
    }

    /// <summary>
    /// Apply the map's <c>worldspawn</c> "fog" directive to <paramref name="env"/> as Godot depth fog, or
    /// leave fog untouched when the map declares none.
    ///
    /// <para>The crucial faithfulness point: <b>DP fog is BOUNDED, Godot's exponential fog is NOT.</b> DP's
    /// default linear fog (<c>r_fog_exp2 0</c>) gives visibility <c>exp(-density * 0.004 * (dist - start))</c>
    /// (gl_rmain.c R_BuildFogTexture), but the buildup STOPS at <c>fogrange = 2048/density + start</c> (clamped
    /// to <c>fog_end</c>) and the whole fog is scaled by <c>fog_alpha</c> — so most maps are a subtle, capped
    /// haze, not a wall. Feeding the raw <c>density*0.004</c> into Godot's unbounded fog overshoots badly at
    /// distance (the "too thick" bug). Instead we compute DP's true <i>maximum</i> fog opacity (reached at
    /// fogrange and held) and choose the Godot density that reaches exactly that opacity at fogrange — matching
    /// DP across the visible range; it overshoots only beyond fogrange, which is usually past the sightlines.
    /// <see cref="Coords"/> is 1:1 with Godot units. <c>fog_start</c> (no near clearing in Godot's exp fog) is
    /// the one residual approximation.</para>
    /// </summary>
    public static void ApplyFog(Godot.Environment env, BspData? bsp)
    {
        if (bsp is null)
            return;
        Worldspawn ws = BuildWorldspawn(bsp);
        if (!ws.HasFog || ws.FogDensity <= 0f || ws.FogAlpha <= 0f)
            return;

        // DP fogrange (gl_rmain.c R_UpdateFog, linear mode): the distance at which fog saturates and is held.
        float fogrange = Mathf.Clamp(2048f / ws.FogDensity + ws.FogStart, ws.FogStart, ws.FogEnd);
        // DP visibility at fogrange: exp(-density * 0.004 * (fogrange - start)); the max fog opacity is then
        // (1 - vis) * fog_alpha (R_BuildFogTexture). This is the haze DP actually shows — typically subtle.
        float buildup = Mathf.Max(1f, fogrange - ws.FogStart);
        float maxVis = Mathf.Exp(-ws.FogDensity * 0.004f * buildup);
        float maxFog = (1f - maxVis) * Mathf.Clamp(ws.FogAlpha, 0f, 1f);
        if (maxFog < 0.002f || fogrange <= 0f)
            return;   // negligible fog — leave it off rather than add an invisible cost.

        // Godot's exponential fog amount is 1 - exp(-FogDensity * dist); pick the density that reaches maxFog
        // at fogrange so the visible range tracks DP. (Overshoots only beyond fogrange — past most sightlines.)
        float godotDensity = -Mathf.Log(1f - Mathf.Min(maxFog, 0.98f)) / fogrange;

        env.FogEnabled = true;
        env.FogLightColor = ws.FogColor;
        env.FogDensity = godotDensity;
        // Don't fully drown the skybox in fog — DP fogs the sky only partially; keep the horizon readable.
        env.FogSkyAffect = 0.5f;
        GD.Print($"[MapLoader] fog: density {ws.FogDensity:0.####} alpha {ws.FogAlpha:0.##} start {ws.FogStart:0} " +
                 $"-> maxFog {maxFog:0.00} @ {fogrange:0}u, godotDensity {godotDensity:0.000000} " +
                 $"color ({ws.FogColor.R:0.00} {ws.FogColor.G:0.00} {ws.FogColor.B:0.00})");
    }

    /// <summary>Find the worldspawn entity (by classname; falls back to the first entity, Quake convention).</summary>
    private static IReadOnlyDictionary<string, string> FindWorldspawn(BspData bsp)
    {
        foreach (var ent in bsp.Entities)
        {
            if (ent.TryGetValue("classname", out string? cn) && cn == "worldspawn")
                return ent;
        }
        return bsp.Entities[0];
    }

    private static string FirstNonEmpty(IReadOnlyDictionary<string, string> dict, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (dict.TryGetValue(k, out string? v) && !string.IsNullOrWhiteSpace(v))
                return v;
        }
        return string.Empty;
    }

    // -------------------------------------------------------------------------------------------------
    //  Parsing helpers
    // -------------------------------------------------------------------------------------------------

    private static NVec3 ParseVec3(string s)
    {
        var parts = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        float x = parts.Length > 0 ? ParseFloat(parts[0]) : 0f;
        float y = parts.Length > 1 ? ParseFloat(parts[1]) : 0f;
        float z = parts.Length > 2 ? ParseFloat(parts[2]) : 0f;
        return new NVec3(x, y, z);
    }

    private static float ParseFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
}

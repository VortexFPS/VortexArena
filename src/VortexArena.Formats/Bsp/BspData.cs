using System.Numerics;

namespace XonoticGodot.Formats.Bsp;

/// <summary>
/// Engine-neutral, Godot-free representation of an IBSP v46/47/48 (Quake 3) map, parsed from the
/// on-disk lump directory by <see cref="BspReader"/>. Geometry uses Quake coordinates and units
/// exactly as stored (no axis swap, no scale) — the Godot host applies any conversion when it
/// builds ArrayMesh surfaces.
///
/// Ground truth: Darkplaces <c>model_q3bsp.h</c> / <c>model_brush.c</c> (<c>Mod_Q3BSP_Load*</c>).
/// </summary>
public sealed class BspData
{
    /// <summary>BSP format version from the header (46 = Q3, 47 = "live"/RtCW-ish, 48 = IG/ZeroRadiant).</summary>
    public int Version { get; init; }

    /// <summary>Raw, NUL-terminated entity lump text (lump 0), exactly as on disk minus the trailing padding.</summary>
    public string EntitiesText { get; init; } = string.Empty;

    /// <summary>
    /// Entity lump parsed into one dictionary per <c>{ "key" "value" ... }</c> block. The first entry
    /// is conventionally <c>worldspawn</c>. Keys with a leading underscore are NOT stripped here (unlike
    /// DP's worldspawn-only parse) so callers see the raw key; gameplay's <c>spawnfunc_*</c> consume these.
    /// Later duplicate keys within one entity overwrite earlier ones (Quake semantics).
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Entities { get; init; } =
        Array.Empty<IReadOnlyDictionary<string, string>>();

    /// <summary>Lump 1 — shader/texture references used by faces and brushes.</summary>
    public BspTexture[] Textures { get; init; } = Array.Empty<BspTexture>();

    /// <summary>Lump 2 — planes (normal + distance). Planes are paired: i and i^1 are opposites.</summary>
    public BspPlane[] Planes { get; init; } = Array.Empty<BspPlane>();

    /// <summary>
    /// Lump 7 — inline brush models. <c>Models[0]</c> is the worldspawn geometry; <c>Models[N]</c> (N≥1) backs
    /// the <c>"*N"</c> model an entity references (func_door/plat/button/rotating/…). Each owns a contiguous
    /// range of <see cref="Brushes"/> and <see cref="Faces"/>. Used to split the static world geometry from the
    /// per-entity collision brushes of moving SOLID_BSP entities (the rest go to the static collision world).
    /// </summary>
    public BspModel[] Models { get; init; } = Array.Empty<BspModel>();

    /// <summary>Lump 3 — BSP tree nodes (splitting planes + front/back children). Drives point→leaf descent for PVS.</summary>
    public BspNode[] Nodes { get; init; } = Array.Empty<BspNode>();

    /// <summary>Lump 4 — BSP leaves; each carries the visibility <c>Cluster</c> a point in it belongs to.</summary>
    public BspLeaf[] Leafs { get; init; } = Array.Empty<BspLeaf>();

    /// <summary>
    /// Lump 5 — leaf→face references: a flat array of face indices. Each leaf owns the slice
    /// <c>[FirstLeafFace, FirstLeafFace+LeafFaceCount)</c> (see <see cref="BspLeaf"/>); a face index can appear in
    /// several leaves (it bounds them all). Lets the §12.8 PVS culler label a face by the clusters of EVERY leaf
    /// that references it — DP's exact per-surface vis — instead of approximating from a sampled point. Empty on
    /// a BSP without the lump (some minimal/test maps), in which case the culler degrades to all-visible.
    /// </summary>
    public int[] LeafFaces { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Lump 16 — the potentially-visible-set bit matrix (compiled by the map's vis stage). Empty on an unvised
    /// map, in which case every cluster is conservatively treated as visible (DP behaviour).
    /// </summary>
    public BspVis Vis { get; init; }

    /// <summary>Lump 8 — brushes (convex volumes), each a contiguous range of <see cref="BrushSides"/>.</summary>
    public BspBrush[] Brushes { get; init; } = Array.Empty<BspBrush>();

    /// <summary>Lump 9 — brush sides (plane + texture; <see cref="BspBrushSide.SurfaceFlags"/> set only on v48/IG).</summary>
    public BspBrushSide[] BrushSides { get; init; } = Array.Empty<BspBrushSide>();

    /// <summary>Lump 10 — render vertices (position, two texcoords, normal, color).</summary>
    public BspVertex[] Vertices { get; init; } = Array.Empty<BspVertex>();

    /// <summary>
    /// Lump 11 — triangle indices, as a flat array of <c>int</c> indices into <see cref="Vertices"/>
    /// (length is a multiple of 3). These are mesh-LOCAL indices; a face's indices are
    /// <c>Triangles[face.FirstIndex .. face.FirstIndex+face.IndexCount]</c> and are offset by
    /// <c>face.FirstVertex</c>. Out-of-range indices are clamped to 0 (matching DP).
    /// </summary>
    public int[] Triangles { get; init; } = Array.Empty<int>();

    /// <summary>Lump 13 — surfaces/faces describing how to draw ranges of vertices/triangles.</summary>
    public BspFace[] Faces { get; init; } = Array.Empty<BspFace>();

    /// <summary>
    /// Lump 14 — the <b>true</b> lightmap pages, each a 128*128 RGB (3 bytes/texel) image, split out of the
    /// raw lump. Stored as raw bytes; the host uploads them as textures and feeds face lightmap UVs as UV2.
    /// An empty array means the map has no internal lightmaps (may use external lm_NNNN.tga instead).
    ///
    /// When the map is <see cref="IsDeluxemapped"/>, the on-disk lump 14 interleaves real lightmap pages
    /// with per-texel light-<i>direction</i> ("deluxe") pages — even index = lightmap, odd index = deluxemap
    /// (DP <c>Mod_Q3BSP_LoadLightmaps</c>). The reader de-interleaves them: this array holds only the real
    /// lightmap pages (the de-interleaved even pages), so a deluxemapped map no longer lights through the
    /// direction data. The matching deluxe pages are kept in <see cref="Deluxemaps"/>.
    /// </summary>
    public byte[][] Lightmaps { get; init; } = Array.Empty<byte[]>();

    /// <summary>
    /// Lump 14 (deluxemapped maps only) — the per-texel light-<i>direction</i> pages de-interleaved out of
    /// the lightmap lump (the odd on-disk pages), in the same RGB layout as <see cref="Lightmaps"/>. These
    /// encode a tangent/model-space light direction, not radiosity, so they must NOT be sampled as a
    /// lightmap; they are exposed here (rather than dropped) so a future bumped-lighting pass can use them.
    /// Empty when the map is not deluxemapped. Page <c>i</c> here pairs with <see cref="Lightmaps"/> page <c>i</c>.
    /// </summary>
    public byte[][] Deluxemaps { get; init; } = Array.Empty<byte[]>();

    /// <summary>
    /// True when lump 14 carries interleaved deluxemaps (light-direction pages) alongside the real
    /// lightmaps. Detected exactly as Darkplaces does (worldspawn <c>deluxeMaps</c> key, else the even-count
    /// + face-index heuristic; see <see cref="BspReader"/>). When true, a face's stored
    /// <see cref="BspFace.LightmapIndex"/> must be halved to index <see cref="Lightmaps"/> — use
    /// <see cref="RealLightmapIndex"/>.
    /// </summary>
    public bool IsDeluxemapped { get; init; }

    /// <summary>
    /// Map a face's raw <see cref="BspFace.LightmapIndex"/> to an index into the de-interleaved
    /// <see cref="Lightmaps"/> array. On a deluxemapped map the on-disk lightmap index counts both the
    /// lightmap and deluxe pages, so DP shifts it right by one (<c>lightmapindex &gt;&gt; deluxemapping</c>,
    /// <c>model_brush.c</c>); on a non-deluxemapped map it is returned unchanged. A negative index
    /// (no lightmap) is returned as -1.
    /// </summary>
    public int RealLightmapIndex(int rawLightmapIndex)
    {
        if (rawLightmapIndex < 0)
            return -1;
        return IsDeluxemapped ? rawLightmapIndex >> 1 : rawLightmapIndex;
    }

    /// <summary>Pixel width of each lightmap page (always 128 for internal Q3 lightmaps).</summary>
    public int LightmapWidth { get; init; } = LightmapSize;

    /// <summary>Pixel height of each lightmap page (always 128 for internal Q3 lightmaps).</summary>
    public int LightmapHeight { get; init; } = LightmapSize;

    /// <summary>The native Q3 internal lightmap page edge length, in texels.</summary>
    public const int LightmapSize = 128;

    // --- Lumps intentionally left as raw byte ranges (TODO for later passes) ---
    // LeafBrushes(6) indexes leaf collision brushes; Effects/Fog(12) are renderer hints.
    // BspReader exposes their raw [offset,length) slices via RawLumps so a later pass can parse them without
    // re-reading the header. (LeafFaces(5) and LightGrid(15) are now parsed.)

    /// <summary>Raw, unparsed slices of every lump in the directory, indexed by lump number (see <see cref="BspLump"/>).</summary>
    public RawLump[] RawLumps { get; init; } = Array.Empty<RawLump>();

    /// <summary>The parsed lightgrid (lump 15) — the baked per-position MODEL light probes DP samples for
    /// entity lighting (<c>Mod_Q3BSP_LightPoint</c>). Null when the map ships none or the lump length doesn't
    /// match the derived grid dims. (playtest r12 #41)</summary>
    public LightGridData? LightGrid { get; init; }
}

/// <summary>Q3 BSP lump indices (the 17-entry directory). See <c>model_q3bsp.h</c>.</summary>
public enum BspLump
{
    Entities = 0,
    Textures = 1,
    Planes = 2,
    Nodes = 3,
    Leafs = 4,
    LeafFaces = 5,
    LeafBrushes = 6,
    Models = 7,
    Brushes = 8,
    BrushSides = 9,
    Vertices = 10,
    Triangles = 11,
    Effects = 12,
    Faces = 13,
    Lightmaps = 14,
    LightGrid = 15,
    Pvs = 16,
}

/// <summary>Q3 face/surface types (lump 13). PATCH faces are bezier control grids needing tessellation.</summary>
public enum BspFaceType
{
    /// <summary>Planar polygon soup, renderable directly as a triangle mesh.</summary>
    Flat = 1,
    /// <summary>Bezier patch: <see cref="BspFace.PatchWidth"/>x<see cref="BspFace.PatchHeight"/> control points; tessellate to triangles.</summary>
    Patch = 2,
    /// <summary>Arbitrary triangle mesh (e.g. imported model surfaces).</summary>
    Mesh = 3,
    /// <summary>Billboard flare sprite, no geometry.</summary>
    Flare = 4,
}

/// <summary>A raw, unparsed lump: an absolute byte range within the original file buffer.</summary>
public readonly record struct RawLump(int Offset, int Length)
{
    /// <summary>True if this lump has no data (length 0).</summary>
    public bool IsEmpty => Length <= 0;
}

/// <summary>Lump 1 entry: a shader/texture reference plus its surface/content flag bits.</summary>
public readonly record struct BspTexture(string ShaderName, int SurfaceFlags, int ContentFlags);

/// <summary>Lump 2 entry: a plane in Quake form (unit normal + distance along it from origin).</summary>
public readonly record struct BspPlane(Vector3 Normal, float Distance);

/// <summary>Lump 9 entry: one side of a brush. <see cref="SurfaceFlags"/> is -1 unless the map is v48/IG.</summary>
public readonly record struct BspBrushSide(int PlaneIndex, int TextureIndex, int SurfaceFlags);

/// <summary>
/// Lump 7 entry: an inline brush model (<c>dmodel_t</c>). <see cref="Mins"/>/<see cref="Maxs"/> are its
/// world-space bounds; it owns the brushes <c>[FirstBrush, FirstBrush+BrushCount)</c> in
/// <see cref="BspData.Brushes"/> and the faces <c>[FirstFace, FirstFace+FaceCount)</c> in
/// <see cref="BspData.Faces"/>. Model 0 is worldspawn; models 1..N are the <c>"*N"</c> entity brush models.
/// </summary>
public readonly record struct BspModel(Vector3 Mins, Vector3 Maxs, int FirstFace, int FaceCount, int FirstBrush, int BrushCount);

/// <summary>
/// Lump 3 entry: a BSP tree node (<c>dnode_t</c>). <see cref="Child0"/>/<see cref="Child1"/> are the front/back
/// children: a value ≥0 is a node index, a value &lt;0 is the leaf index <c>(-child-1)</c>. <see cref="PlaneIndex"/>
/// selects the splitting plane in <see cref="BspData.Planes"/> (front = positive side). (The node bbox is skipped.)
/// </summary>
public readonly record struct BspNode(int PlaneIndex, int Child0, int Child1);

/// <summary>
/// Lump 4 entry: a BSP leaf (<c>dleaf_t</c>). <see cref="Cluster"/> is the visibility cluster (−1 = no cluster /
/// solid leaf, treated as conservatively visible). The leaf-face/leaf-brush ranges index the leaf-face and
/// leaf-brush lumps (kept for later mesh/brush queries; not needed by PVS itself). The bbox is skipped.
/// </summary>
public readonly record struct BspLeaf(int Cluster, int Area, int FirstLeafFace, int LeafFaceCount, int FirstLeafBrush, int LeafBrushCount);

/// <summary>
/// Lump 16: the potentially-visible-set bit matrix. <see cref="ClusterCount"/> rows, each
/// <see cref="BytesPerCluster"/> bytes; bit <c>b</c> of row <c>a</c> is set when cluster <c>b</c> is potentially
/// visible from cluster <c>a</c>. <see cref="ClusterVisible"/> performs the lookup. Default value = no vis data.
/// </summary>
public readonly record struct BspVis(int ClusterCount, int BytesPerCluster, byte[] Data)
{
    /// <summary>True if cluster <paramref name="to"/> is potentially visible from cluster <paramref name="from"/>.</summary>
    public bool ClusterVisible(int from, int to)
    {
        if (Data is null || ClusterCount <= 0 || BytesPerCluster <= 0)
            return true; // no vis data → conservatively visible
        if (from < 0 || to < 0 || from >= ClusterCount || to >= ClusterCount)
            return true; // out of range / solid-leaf cluster → conservatively visible
        int idx = from * BytesPerCluster + (to >> 3);
        if (idx < 0 || idx >= Data.Length)
            return true;
        return (Data[idx] & (1 << (to & 7))) != 0;
    }
}

/// <summary>
/// Lump 8 entry: a convex brush defined by <see cref="SideCount"/> sides starting at
/// <see cref="FirstSide"/> in <see cref="BspData.BrushSides"/>. <see cref="TextureIndex"/> carries the
/// brush's overall content flags (lava/slime/clip/solid) for the collision/trace service.
/// </summary>
public readonly record struct BspBrush(int FirstSide, int SideCount, int TextureIndex);

/// <summary>
/// Lump 10 entry: a render vertex. <see cref="LightmapCoord"/> is the precomputed lightmap UV (feed as UV2);
/// <see cref="Color"/> is the per-vertex RGBA (0-255) used by vertex-lit surfaces.
/// </summary>
public readonly record struct BspVertex(
    Vector3 Position,
    Vector2 TexCoord,
    Vector2 LightmapCoord,
    Vector3 Normal,
    BspColor Color);

/// <summary>RGBA color, 8 bits per channel, as stored in vertices and elsewhere.</summary>
public readonly record struct BspColor(byte R, byte G, byte B, byte A);

/// <summary>
/// Lump 13 entry: a surface. Indices/vertices index into <see cref="BspData.Triangles"/> and
/// <see cref="BspData.Vertices"/>. For <see cref="BspFaceType.Patch"/>, <see cref="PatchWidth"/>x
/// <see cref="PatchHeight"/> give the control-point grid dimensions (the vertex range is that grid);
/// the host tessellates the bezier surface. For Flat/Mesh, use the triangle index range directly.
/// </summary>
public readonly record struct BspFace(
    int TextureIndex,
    int EffectIndex,
    BspFaceType Type,
    int FirstVertex,
    int VertexCount,
    int FirstIndex,
    int IndexCount,
    int LightmapIndex,
    int PatchWidth,
    int PatchHeight);

using System.Numerics;

namespace XonoticGodot.Formats.Bsp;

/// <summary>
/// Parses an IBSP v46/47/48 (Quake 3 / Darkplaces) map from a raw byte buffer into <see cref="BspData"/>.
///
/// Faithful to the Darkplaces on-disk layout (<c>model_q3bsp.h</c>, <c>Mod_Q3BSP_Load*</c> in
/// <c>model_brush.c</c>): header is <c>int ident; int version; lump_t lumps[17]</c> where
/// <c>lump_t = { int fileofs; int filelen; }</c>. We parse the lumps the Godot mesh/collision/entity
/// build needs (geometry, brushes, entities, textures, lightmaps) and expose the rest as raw ranges.
///
/// All readers bounds-check; malformed input throws <see cref="AssetParseException"/>.
/// </summary>
public static class BspReader
{
    private const string Magic = "IBSP";

    // Versions: 46 = Q3 (Xonotic), 47 = "live" (18-lump dir), 48 = IG/ZeroRadiant (3-int brushsides).
    public const int VersionQ3 = 46;
    public const int VersionLive = 47;
    public const int VersionIg = 48;

    private const int LumpCount = 17;       // Q3HEADER_LUMPS
    private const int LumpDirEntrySize = 8; // lump_t { int fileofs, filelen }
    private const int HeaderSize = 8 + LumpCount * LumpDirEntrySize; // ident+version + 17*8 = 144

    // On-disk record sizes (bytes).
    private const int TextureSize = 64 + 4 + 4;     // name[64] + surfaceflags + contents = 72
    private const int PlaneSize = 12 + 4;           // normal[3] + dist = 16
    private const int BrushSize = 4 + 4 + 4;        // firstside + numsides + textureindex = 12
    private const int BrushSideSize = 4 + 4;        // planeindex + textureindex = 8
    private const int BrushSideIgSize = 4 + 4 + 4;  // + surfaceflags = 12
    private const int ModelSize = 12 + 12 + 4 * 4;  // mins[3] + maxs[3] + firstface,numfaces,firstbrush,numbrushes = 40
    private const int NodeSize = 4 + 4 + 4 + 12 + 12; // plane + children[2] + mins[3](int) + maxs[3](int) = 36
    private const int LeafSize = 4 * 6 + 12 + 12;     // cluster,area + mins[3],maxs[3](int) + 4 leafface/leafbrush ints = 48
    private const int VertexSize = 12 + 8 + 8 + 12 + 4; // xyz + st + lmst + normal + rgba = 44
    private const int FaceSize = 26 * 4;            // 12 header ints + 14-int type union = 104

    private const int ShaderNameLen = 64; // Q3PATHLENGTH

    /// <summary>Parses a complete BSP from <paramref name="data"/>. Throws <see cref="AssetParseException"/> if malformed.</summary>
    public static BspData Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(new ReadOnlySpan<byte>(data));
    }

    public static BspData Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new AssetParseException($"BSP too small: {data.Length} bytes, need at least {HeaderSize} for the header.");

        string magic = BinaryUtil.ReadMagic(data, 0);
        if (magic != Magic)
            throw new AssetParseException($"Not an IBSP file: magic is \"{magic}\", expected \"{Magic}\".");

        int version = BinaryUtil.ReadInt32(data, 4);
        if (version != VersionQ3 && version != VersionLive && version != VersionIg)
            throw new AssetParseException($"Unsupported IBSP version {version} (accept 46/47/48).");

        // Read the 17-entry lump directory and validate every range lies within the buffer.
        var lumps = new RawLump[LumpCount];
        for (int i = 0; i < LumpCount; i++)
        {
            int baseOff = 8 + i * LumpDirEntrySize;
            int ofs = BinaryUtil.ReadInt32(data, baseOff);
            int len = BinaryUtil.ReadInt32(data, baseOff + 4);
            if (ofs < 0 || len < 0 || (long)ofs + len > data.Length)
                throw new AssetParseException(
                    $"Lump {i} ({(BspLump)i}) range [{ofs}, {ofs + (long)len}) lies outside the {data.Length}-byte file.");
            lumps[i] = new RawLump(ofs, len);
        }

        BspTexture[] textures = ReadTextures(data, lumps[(int)BspLump.Textures]);
        BspPlane[] planes = ReadPlanes(data, lumps[(int)BspLump.Planes]);
        BspBrushSide[] brushSides = ReadBrushSides(data, lumps[(int)BspLump.BrushSides], version == VersionIg);
        BspBrush[] brushes = ReadBrushes(data, lumps[(int)BspLump.Brushes]);
        BspModel[] models = ReadModels(data, lumps[(int)BspLump.Models]);
        BspNode[] nodes = ReadNodes(data, lumps[(int)BspLump.Nodes]);
        BspLeaf[] leafs = ReadLeafs(data, lumps[(int)BspLump.Leafs]);
        int[] leafFaces = ReadLeafFaces(data, lumps[(int)BspLump.LeafFaces]);
        BspVis vis = ReadVis(data, lumps[(int)BspLump.Pvs]);
        BspVertex[] vertices = ReadVertices(data, lumps[(int)BspLump.Vertices]);
        int[] triangles = ReadTriangles(data, lumps[(int)BspLump.Triangles], vertices.Length);
        BspFace[] faces = ReadFaces(data, lumps[(int)BspLump.Faces]);
        byte[][] rawLightmapPages = ReadLightmaps(data, lumps[(int)BspLump.Lightmaps]);
        (string entText, var entities) = ReadEntities(data, lumps[(int)BspLump.Entities]);

        // Detect interleaved deluxemaps and split the lightmap lump into true-lightmap / deluxe halves.
        // (Mirror of DP Mod_Q3BSP_LoadLightmaps: worldspawn "deluxeMaps" key, else the even-count + face
        // probe heuristic; even page = lightmap, odd page = light direction.)
        DeluxemapSplit split = DetectAndSplitDeluxemaps(rawLightmapPages, faces, entities, BspData.LightmapSize);

        // Lightgrid (lump 15) — the baked per-position MODEL light probes (DP Mod_Q3BSP_LoadLightGrid).
        // Dims derive from the world model's bounds; a length mismatch disables the grid (Build → null).
        LightGridData? lightGrid = null;
        {
            RawLump gl = lumps[(int)BspLump.LightGrid];
            if (gl.Length >= 8 && models.Length > 0)
                lightGrid = LightGridData.Build(models[0].Mins, models[0].Maxs,
                    data.Slice(gl.Offset, gl.Length).ToArray());
        }

        return new BspData
        {
            LightGrid = lightGrid,
            Version = version,
            EntitiesText = entText,
            Entities = entities,
            Textures = textures,
            Planes = planes,
            Brushes = brushes,
            Models = models,
            Nodes = nodes,
            Leafs = leafs,
            LeafFaces = leafFaces,
            Vis = vis,
            BrushSides = brushSides,
            Vertices = vertices,
            Triangles = triangles,
            Faces = faces,
            Lightmaps = split.Lightmaps,
            Deluxemaps = split.Deluxemaps,
            IsDeluxemapped = split.IsDeluxemapped,
            RawLumps = lumps,
        };
    }

    private static int Count(RawLump l, int recordSize, string what)
    {
        if (l.Length % recordSize != 0)
            throw new AssetParseException(
                $"Funny lump size for {what}: {l.Length} is not a multiple of record size {recordSize}.");
        return l.Length / recordSize;
    }

    private static BspTexture[] ReadTextures(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, TextureSize, "textures");
        var outArr = new BspTexture[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * TextureSize;
            string name = BinaryUtil.ReadFixedString(data, o, ShaderNameLen);
            int surfaceFlags = BinaryUtil.ReadInt32(data, o + 64);
            int contents = BinaryUtil.ReadInt32(data, o + 68);
            outArr[i] = new BspTexture(name, surfaceFlags, contents);
        }
        return outArr;
    }

    private static BspPlane[] ReadPlanes(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, PlaneSize, "planes");
        var outArr = new BspPlane[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * PlaneSize;
            Vector3 normal = BinaryUtil.ReadVec3(data, o);
            float dist = BinaryUtil.ReadFloat(data, o + 12);
            outArr[i] = new BspPlane(normal, dist);
        }
        return outArr;
    }

    private static BspBrushSide[] ReadBrushSides(ReadOnlySpan<byte> data, RawLump l, bool ig)
    {
        int recSize = ig ? BrushSideIgSize : BrushSideSize;
        int count = Count(l, recSize, "brushsides");
        var outArr = new BspBrushSide[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * recSize;
            int planeIndex = BinaryUtil.ReadInt32(data, o);
            int textureIndex = BinaryUtil.ReadInt32(data, o + 4);
            int surfaceFlags = ig ? BinaryUtil.ReadInt32(data, o + 8) : -1;
            outArr[i] = new BspBrushSide(planeIndex, textureIndex, surfaceFlags);
        }
        return outArr;
    }

    private static BspBrush[] ReadBrushes(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, BrushSize, "brushes");
        var outArr = new BspBrush[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * BrushSize;
            int firstSide = BinaryUtil.ReadInt32(data, o);
            int sideCount = BinaryUtil.ReadInt32(data, o + 4);
            int textureIndex = BinaryUtil.ReadInt32(data, o + 8);
            outArr[i] = new BspBrush(firstSide, sideCount, textureIndex);
        }
        return outArr;
    }

    private static BspModel[] ReadModels(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, ModelSize, "models");
        var outArr = new BspModel[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * ModelSize;
            Vector3 mins = BinaryUtil.ReadVec3(data, o);
            Vector3 maxs = BinaryUtil.ReadVec3(data, o + 12);
            int firstFace = BinaryUtil.ReadInt32(data, o + 24);
            int faceCount = BinaryUtil.ReadInt32(data, o + 28);
            int firstBrush = BinaryUtil.ReadInt32(data, o + 32);
            int brushCount = BinaryUtil.ReadInt32(data, o + 36);
            outArr[i] = new BspModel(mins, maxs, firstFace, faceCount, firstBrush, brushCount);
        }
        return outArr;
    }

    private static BspNode[] ReadNodes(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, NodeSize, "nodes");
        var outArr = new BspNode[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * NodeSize;
            int plane = BinaryUtil.ReadInt32(data, o);
            int child0 = BinaryUtil.ReadInt32(data, o + 4);
            int child1 = BinaryUtil.ReadInt32(data, o + 8);
            // mins[3]/maxs[3] ints follow (o+12 .. o+35) — not needed for PVS descent.
            outArr[i] = new BspNode(plane, child0, child1);
        }
        return outArr;
    }

    private static BspLeaf[] ReadLeafs(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, LeafSize, "leafs");
        var outArr = new BspLeaf[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * LeafSize;
            int cluster = BinaryUtil.ReadInt32(data, o);
            int area = BinaryUtil.ReadInt32(data, o + 4);
            // mins[3]/maxs[3] ints at o+8 .. o+31 (skipped).
            int firstLeafFace = BinaryUtil.ReadInt32(data, o + 32);
            int leafFaceCount = BinaryUtil.ReadInt32(data, o + 36);
            int firstLeafBrush = BinaryUtil.ReadInt32(data, o + 40);
            int leafBrushCount = BinaryUtil.ReadInt32(data, o + 44);
            outArr[i] = new BspLeaf(cluster, area, firstLeafFace, leafFaceCount, firstLeafBrush, leafBrushCount);
        }
        return outArr;
    }

    /// <summary>
    /// Read the leaf→face lump (lump 5): a flat <c>int[]</c> of face indices. Each leaf owns the slice
    /// <c>[FirstLeafFace, FirstLeafFace+LeafFaceCount)</c> of this array (see <see cref="BspLeaf"/>); a face
    /// index can appear in several leaves (it bounds them all). The PVS culler uses this to label a face by the
    /// clusters of EVERY leaf that references it — DP's exact per-surface vis. Out-of-range values are left
    /// as-is (consumers bounds-check); absent/sub-int lump (some minimal/test BSPs) → empty array.
    /// </summary>
    private static int[] ReadLeafFaces(ReadOnlySpan<byte> data, RawLump l)
    {
        if (l.Length < 4)
            return Array.Empty<int>();
        int n = l.Length / 4;
        var outArr = new int[n];
        for (int i = 0; i < n; i++)
            outArr[i] = BinaryUtil.ReadInt32(data, l.Offset + i * 4);
        return outArr;
    }

    /// <summary>
    /// Read the visibility lump (<c>dvis_t</c>): <c>int numClusters, int bytesPerCluster</c> header followed by
    /// the <c>numClusters * bytesPerCluster</c> bitset bytes. Returns an empty <see cref="BspVis"/> for an
    /// unvised map (lump absent/too small or a mismatched payload), in which case all clusters read as visible.
    /// </summary>
    private static BspVis ReadVis(ReadOnlySpan<byte> data, RawLump l)
    {
        if (l.Length < 8)
            return default; // no vis data
        int numClusters = BinaryUtil.ReadInt32(data, l.Offset);
        int bytesPerCluster = BinaryUtil.ReadInt32(data, l.Offset + 4);
        if (numClusters <= 0 || bytesPerCluster <= 0)
            return default;
        long need = 8L + (long)numClusters * bytesPerCluster;
        if (need > l.Length)
            return default; // truncated/garbage vis lump — treat as unvised
        var bytes = new byte[numClusters * bytesPerCluster];
        data.Slice(l.Offset + 8, bytes.Length).CopyTo(bytes);
        return new BspVis(numClusters, bytesPerCluster, bytes);
    }

    private static BspVertex[] ReadVertices(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, VertexSize, "vertices");
        var outArr = new BspVertex[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * VertexSize;
            Vector3 pos = BinaryUtil.ReadVec3(data, o);
            Vector2 st = BinaryUtil.ReadVec2(data, o + 12);
            Vector2 lm = BinaryUtil.ReadVec2(data, o + 20);
            Vector3 normal = BinaryUtil.ReadVec3(data, o + 28);
            var color = new BspColor(data[o + 40], data[o + 41], data[o + 42], data[o + 43]);
            outArr[i] = new BspVertex(pos, st, lm, normal, color);
        }
        return outArr;
    }

    private static int[] ReadTriangles(ReadOnlySpan<byte> data, RawLump l, int vertexCount)
    {
        // The lump is a flat int[] of indices; total must be a multiple of 3 (DP checks sizeof(int[3])).
        if (l.Length % 12 != 0)
            throw new AssetParseException($"Funny lump size for triangles: {l.Length} is not a multiple of 12.");
        int n = l.Length / 4;
        // DP ignores triangles entirely if there are no vertices (broken-compiler workaround).
        if (vertexCount == 0)
            return Array.Empty<int>();
        var outArr = new int[n];
        for (int i = 0; i < n; i++)
        {
            int idx = BinaryUtil.ReadInt32(data, l.Offset + i * 4);
            // DP clamps out-of-range indices to 0 rather than failing.
            outArr[i] = (idx < 0 || idx >= vertexCount) ? 0 : idx;
        }
        return outArr;
    }

    private static BspFace[] ReadFaces(ReadOnlySpan<byte> data, RawLump l)
    {
        int count = Count(l, FaceSize, "faces");
        var outArr = new BspFace[count];
        for (int i = 0; i < count; i++)
        {
            int o = l.Offset + i * FaceSize;
            int textureIndex = BinaryUtil.ReadInt32(data, o);
            int effectIndex = BinaryUtil.ReadInt32(data, o + 4);
            int typeRaw = BinaryUtil.ReadInt32(data, o + 8);
            int firstVertex = BinaryUtil.ReadInt32(data, o + 12);
            int vertexCount = BinaryUtil.ReadInt32(data, o + 16);
            int firstElement = BinaryUtil.ReadInt32(data, o + 20);
            int numElements = BinaryUtil.ReadInt32(data, o + 24);
            int lightmapIndex = BinaryUtil.ReadInt32(data, o + 28);
            // o+32..o+47 = lightmap_base[2], lightmap_size[2] (ints) — not needed by the mesh build.
            // The type-specific union begins at o+48 (14 ints). For PATCH, patchsize[2] sits at
            // union int indices [12],[13] (after unused1[3] + mins[3] + maxs[3] + unused2[3]).
            int patchWidth = 0, patchHeight = 0;
            if (typeRaw == (int)BspFaceType.Patch)
            {
                patchWidth = BinaryUtil.ReadInt32(data, o + 48 + 12 * 4);
                patchHeight = BinaryUtil.ReadInt32(data, o + 48 + 13 * 4);
            }
            outArr[i] = new BspFace(
                textureIndex, effectIndex, (BspFaceType)typeRaw,
                firstVertex, vertexCount, firstElement, numElements,
                lightmapIndex, patchWidth, patchHeight);
        }
        return outArr;
    }

    private static byte[][] ReadLightmaps(ReadOnlySpan<byte> data, RawLump l)
    {
        // Each internal page is 128*128*3 bytes. DP tolerates a partial trailing page; we keep only
        // whole pages (a partial tail would be an external-lightmap / corruption case).
        const int pageBytes = BspData.LightmapSize * BspData.LightmapSize * 3;
        if (l.Length <= 0)
            return Array.Empty<byte[]>();
        int pages = l.Length / pageBytes;
        var outArr = new byte[pages][];
        for (int i = 0; i < pages; i++)
        {
            var page = new byte[pageBytes];
            data.Slice(l.Offset + i * pageBytes, pageBytes).CopyTo(page);
            outArr[i] = page;
        }
        return outArr;
    }

    /// <summary>Result of de-interleaving the lightmap lump: the true lightmaps, the deluxe pages, and the flag.</summary>
    private readonly record struct DeluxemapSplit(byte[][] Lightmaps, byte[][] Deluxemaps, bool IsDeluxemapped);

    /// <summary>
    /// Detect whether the internal lightmap lump interleaves deluxemaps (per-texel light direction) and, if
    /// so, split it into the true-lightmap pages (the even indices) and the deluxe pages (the odd indices).
    ///
    /// Faithful to Darkplaces <c>Mod_Q3BSP_LoadLightmaps</c> (<c>model_brush.c</c>):
    /// <list type="number">
    ///   <item>The worldspawn <c>deluxeMaps</c> key forces deluxemapping when "1" (modelspace) or "2"
    ///   (tangentspace) — q3map2 FS-R sets it. DP strips a leading <c>_</c> from worldspawn keys, so we accept
    ///   both <c>deluxeMaps</c> and <c>_deluxeMaps</c>.</item>
    ///   <item>Otherwise auto-detect: a deluxemapped lump has an <b>even</b> page count, AND no face may
    ///   reference an <b>odd</b> lightmap index or an index whose pair (<c>j+1</c>) would run off the end —
    ///   either disqualifies (a real lightmap was stored at an odd page, so the pages are not paired).</item>
    ///   <item>Special case: if exactly one lightmap is actually used but the lump has &gt;1 page and that
    ///   second page is entirely black, q3map2 just padded an unused blank page — not a deluxemap.</item>
    /// </list>
    /// When deluxemapped, even page <c>i</c> → lightmap slot <c>i/2</c>, odd page <c>i</c> → deluxe slot
    /// <c>i/2</c>; a face's stored lightmap index is then halved (<see cref="BspData.RealLightmapIndex"/>).
    ///
    /// External-lightmap maps (<c>lm_NNNN</c>, no internal lump) are de-interleaved by DP exactly the same way
    /// (the detection runs unconditionally after the internal-OR-external load). The external pages live on
    /// the VFS, not in this byte buffer, so when <paramref name="pages"/> is empty we only DETECT (and set the
    /// flag so the host halves the face index and treats odd <c>lm_NNNN</c> files as deluxe); there is nothing
    /// here to split. See <see cref="DetectExternalDeluxemapping"/>.
    /// </summary>
    private static DeluxemapSplit DetectAndSplitDeluxemaps(
        byte[][] pages, BspFace[] faces, IReadOnlyList<IReadOnlyDictionary<string, string>> entities, int size)
    {
        int count = pages.Length;
        if (count == 0)
        {
            // No internal pages: this is either a map with no lightmaps at all, or an external lm_NNNN map.
            // DP runs deluxemap detection on the external set too, so don't gate it on the internal count —
            // detect from the data we have here (worldspawn key + face indices) so the host halves correctly.
            return new DeluxemapSplit(
                Array.Empty<byte[]>(), Array.Empty<byte[]>(),
                DetectExternalDeluxemapping(faces, entities));
        }

        // (1) worldspawn "deluxeMaps" key (DP: deluxemapping_modelspace=true for "1", false for "2").
        bool deluxemapping = WorldspawnDeluxeMaps(entities);

        // (2) auto-detect only if the key was not present/true (DP gates the heuristic the same way).
        if (!deluxemapping)
        {
            deluxemapping = (count & 1) == 0;
            int endLightmap = 0;
            if (deluxemapping)
            {
                foreach (BspFace f in faces)
                {
                    int j = f.LightmapIndex;
                    if (j >= 0)
                    {
                        if (j + 1 > endLightmap) endLightmap = j + 1;
                        // An odd lightmap index, or one whose pair would run past the last page, means the
                        // pages are NOT lightmap/deluxe pairs.
                        if ((j & 1) != 0 || j + 1 >= count)
                        {
                            deluxemapping = false;
                            break;
                        }
                    }
                }
            }

            // (3) q3map2 sometimes appends a blank second lightmap when only one is used — that is not a
            // deluxemap. If only page 0 is referenced and page 1 is all-black, clear the flag.
            if (deluxemapping && endLightmap == 1 && count > 1 && IsPageBlack(pages[1], size))
                deluxemapping = false;
        }

        if (!deluxemapping)
            return new DeluxemapSplit(pages, Array.Empty<byte[]>(), false);

        // De-interleave: even -> lightmap, odd -> deluxe. realcount = count >> 1 (DP).
        int realCount = count >> 1;
        var lightmaps = new byte[realCount][];
        var deluxemaps = new byte[realCount][];
        for (int i = 0; i < count; i++)
        {
            int slot = i >> 1;
            if (slot >= realCount)
                break; // defensive: an odd trailing page with no pair is dropped
            if ((i & 1) == 0)
                lightmaps[slot] = pages[i];
            else
                deluxemaps[slot] = pages[i];
        }
        return new DeluxemapSplit(lightmaps, deluxemaps, true);
    }

    /// <summary>
    /// Decide whether an EXTERNAL-lightmap map (<c>lm_NNNN</c>; no internal lump 14) is deluxemapped, using
    /// only the data available without the external pages: the worldspawn <c>deluxeMaps</c> key, else the
    /// face-index heuristic. Mirrors the auto-detect in <see cref="DetectAndSplitDeluxemaps"/> minus the two
    /// checks that need the external set (the on-disk page <c>count</c> for the <c>j+1 &gt;= count</c> bound and
    /// the blank-second-page probe).
    ///
    /// A genuinely deluxemapped set pairs (lightmap, deluxe) pages, so faces only ever index EVEN lightmap
    /// slots; any odd index disqualifies it (an ordinary external set numbers pages 0,1,2,3…). DP also gates
    /// on an even on-disk page count, which we cannot read here — the only resulting ambiguity is a
    /// single-lightmap map (all faces index 0): deluxemapped that is one lightmap + one deluxe (count 2),
    /// non-deluxemapped it is one lightmap (count 1). DP resolves the tie via the odd count, so without it we
    /// match DP's "not deluxemapped" outcome by requiring real pairing evidence — a referenced index &gt;= 2 —
    /// before auto-detecting deluxemapping (the worldspawn key remains authoritative for the count-2 case).
    ///
    /// The host (MapLoader) consumes the resulting flag to halve the face's stored lightmap index and to map
    /// lightmap slot <c>k</c> → file <c>lm_{2k}</c> / deluxe slot <c>k</c> → file <c>lm_{2k+1}</c>.
    /// </summary>
    private static bool DetectExternalDeluxemapping(
        BspFace[] faces, IReadOnlyList<IReadOnlyDictionary<string, string>> entities)
    {
        // (1) worldspawn "deluxeMaps" key wins (q3map2 FS-R writes it).
        if (WorldspawnDeluxeMaps(entities))
            return true;

        // (2) auto-detect from face lightmap indices.
        int maxIndex = -1;
        foreach (BspFace f in faces)
        {
            int j = f.LightmapIndex;
            if (j >= 0)
            {
                if ((j & 1) != 0)
                    return false;       // odd lightmap index → pages are not lightmap/deluxe pairs
                if (j > maxIndex) maxIndex = j;
            }
        }
        // Require pairing evidence (an index >= 2) to avoid the single-lightmap (count-1 vs count-2) ambiguity;
        // a lone index 0 matches DP's "not deluxemapped" (odd count=1) outcome.
        return maxIndex >= 2;
    }

    /// <summary>
    /// Read the worldspawn <c>deluxeMaps</c> key. Worldspawn is conventionally the first entity; DP strips a
    /// leading underscore from worldspawn keys, so both <c>deluxeMaps</c> and <c>_deluxeMaps</c> are honored.
    /// A value of "1" or "2" turns deluxemapping on (the modelspace/tangentspace distinction is not modeled
    /// here — both produce interleaved pages we must de-interleave identically).
    /// </summary>
    private static bool WorldspawnDeluxeMaps(IReadOnlyList<IReadOnlyDictionary<string, string>> entities)
    {
        if (entities.Count == 0)
            return false;

        // Find worldspawn (by classname; fall back to the first entity, Quake convention).
        IReadOnlyDictionary<string, string> ws = entities[0];
        foreach (var ent in entities)
            if (ent.TryGetValue("classname", out string? cn) && cn == "worldspawn")
            {
                ws = ent;
                break;
            }

        foreach (var kv in ws)
        {
            string key = kv.Key.Length > 0 && kv.Key[0] == '_' ? kv.Key.Substring(1) : kv.Key;
            if (string.Equals(key, "deluxeMaps", StringComparison.Ordinal))
                return kv.Value == "1" || kv.Value == "2";
        }
        return false;
    }

    /// <summary>True if every RGB texel of a 3-byte/texel <paramref name="size"/>×<paramref name="size"/> page is black
    /// (DP's blank-page probe for the q3map2 padding special case). A short page is treated as black.</summary>
    private static bool IsPageBlack(byte[] page, int size)
    {
        int needed = size * size * 3;
        if (page is null || page.Length < needed)
            return true;
        for (int i = 0; i < needed; i++)
            if (page[i] != 0)
                return false;
        return true;
    }

    private static (string text, IReadOnlyList<IReadOnlyDictionary<string, string>> entities)
        ReadEntities(ReadOnlySpan<byte> data, RawLump l)
    {
        if (l.Length <= 0)
            return (string.Empty, Array.Empty<IReadOnlyDictionary<string, string>>());

        // The lump is ASCII text (DP appends a NUL); strip a trailing NUL if present.
        ReadOnlySpan<byte> raw = data.Slice(l.Offset, l.Length);
        int textLen = raw.IndexOf((byte)0);
        if (textLen < 0) textLen = raw.Length;
        string text = System.Text.Encoding.ASCII.GetString(raw.Slice(0, textLen));

        var entities = EntityLumpParser.Parse(text);
        return (text, entities);
    }
}

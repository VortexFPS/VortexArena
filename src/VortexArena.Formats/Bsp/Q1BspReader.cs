// Port of the reading half of Base/darkplaces/model_brush.c Mod_Q1BSP_Load and the Mod_Q1BSP_Load* /
// Mod_BSP_LoadSubmodels / Mod_BSP_DecompressVis / Mod_Q1BSP_CheckWaterAlphaSupport functions it calls.
// What is built for drawing or for collision from this data lives with the renderer and in
// VortexArena.Engine.Collision.Q1HullCollision.
using System.Numerics;

namespace VortexArena.Formats.Bsp;

/// <summary>
/// Reads a Quake 1 format map: version 29, "BSP2", "2PSB" and Half-Life's version 30. The Quake 3 reader
/// (<see cref="BspReader"/>) is separate; <see cref="IsQ1Format"/> tells the two apart from the first bytes.
///
/// The file is untrusted (a server names the map and may have supplied the package): every index the tracing
/// and drawing code later follows is checked here, the two trees are checked to be free of cycles and of
/// bounded depth, and anything DarkPlaces answers with <c>Host_Error</c> is an <see cref="AssetParseException"/>.
/// </summary>
public static class Q1BspReader
{
    private const int LumpCount = 15;
    private const int HeaderSize = 4 + LumpCount * 8;
    private const int LumpEntities = 0, LumpPlanes = 1, LumpTextures = 2, LumpVertexes = 3, LumpVisibility = 4, LumpNodes = 5,
        LumpTexInfo = 6, LumpFaces = 7, LumpLighting = 8, LumpClipNodes = 9, LumpLeafs = 10, LumpMarkSurfaces = 11, LumpEdges = 12,
        LumpSurfEdges = 13, LumpModels = 14;

    /// <summary>The deepest tree accepted. The tracers recurse once per level; a real map is a few hundred deep at most.</summary>
    public const int MaxTreeDepth = 4096;
    /// <summary>The most vertices one face may have (DarkPlaces has no bound; qbsp writes at most 64).</summary>
    public const int MaxFaceVertices = 1024;

    /// <summary>True when the bytes start like a Quake 1 format map: version 29 or 30, "BSP2" or "2PSB".</summary>
    public static bool IsQ1Format(ReadOnlySpan<byte> data) => TryFormat(data, out _);

    private static bool TryFormat(ReadOnlySpan<byte> data, out Q1BspFormat format)
    {
        format = Q1BspFormat.Bsp29;
        if (data.Length < 4) return false;
        if (data[0] == (byte)'B' && data[1] == (byte)'S' && data[2] == (byte)'P' && data[3] == (byte)'2') { format = Q1BspFormat.Bsp2; return true; }
        if (data[0] == (byte)'2' && data[1] == (byte)'P' && data[2] == (byte)'S' && data[3] == (byte)'B') { format = Q1BspFormat.Bsp2Rmqe; return true; }
        int version = BinaryUtil.ReadInt32(data, 0);
        if (version == 29) return true;
        if (version == 30) { format = Q1BspFormat.HalfLife; return true; }
        return false;
    }

    /// <param name="data">The map file.</param>
    /// <param name="lit">The map's <c>.lit</c> file (coloured light for a Quake map), or null. Used only when it
    /// is well formed and matches the map's lighting lump, as DarkPlaces checks; ignored for Half-Life.</param>
    /// <param name="dlit">The map's <c>.dlit</c> file (light directions), or null. Read only with a valid <paramref name="lit"/>.</param>
    public static Q1BspData Read(ReadOnlySpan<byte> data, ReadOnlySpan<byte> lit = default, ReadOnlySpan<byte> dlit = default)
    {
        if (!TryFormat(data, out Q1BspFormat format))
            throw new AssetParseException("Not a Quake 1 format map: the file does not start with version 29 or 30, \"BSP2\" or \"2PSB\".");
        if (data.Length < HeaderSize)
            throw new AssetParseException($"Map too small: {data.Length} bytes, need at least {HeaderSize} for the header.");
        bool bsp2 = format is Q1BspFormat.Bsp2 or Q1BspFormat.Bsp2Rmqe, rmqe = format == Q1BspFormat.Bsp2Rmqe, hl = format == Q1BspFormat.HalfLife;

        Span<int> ofs = stackalloc int[LumpCount], len = stackalloc int[LumpCount];
        for (int i = 0; i < LumpCount; i++)
        {
            ofs[i] = BinaryUtil.ReadInt32(data, 4 + i * 8);
            len[i] = BinaryUtil.ReadInt32(data, 8 + i * 8);
            if (ofs[i] < 0 || len[i] < 0 || ofs[i] > data.Length || len[i] > data.Length - ofs[i])
                throw new AssetParseException($"Map has an invalid lump {i} (offset {ofs[i]}, size {len[i]}, file size {data.Length}).");
        }
        ReadOnlySpan<byte> Lump(int i, Span<int> o, Span<int> l, ReadOnlySpan<byte> d) => d.Slice(o[i], l[i]);

        // ---- entities
        ReadOnlySpan<byte> entityLump = Lump(LumpEntities, ofs, len, data);
        int entityEnd = entityLump.IndexOf((byte)0);
        string entitiesText = System.Text.Encoding.Latin1.GetString(entityEnd < 0 ? entityLump : entityLump[..entityEnd]);
        IReadOnlyList<IReadOnlyDictionary<string, string>> entities;
        try { entities = EntityLumpParser.Parse(entitiesText); }
        catch (Exception e) when (e is not OutOfMemoryException) { entities = Array.Empty<IReadOnlyDictionary<string, string>>(); }

        // ---- vertexes, edges, surfedges
        ReadOnlySpan<byte> vl = Lump(LumpVertexes, ofs, len, data);
        Funny(vl.Length, 12, "vertexes");
        var vertexes = new Vector3[vl.Length / 12];
        for (int i = 0; i < vertexes.Length; i++) vertexes[i] = BinaryUtil.ReadVec3(vl, i * 12);

        ReadOnlySpan<byte> el = Lump(LumpEdges, ofs, len, data);
        int edgeSize = bsp2 ? 8 : 4;
        Funny(el.Length, edgeSize, "edges");
        int edgeCount = el.Length / edgeSize;
        var edgeA = new int[edgeCount];
        var edgeB = new int[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            uint a = bsp2 ? BinaryUtil.ReadUInt32(el, i * 8) : U16(el, i * 4);
            uint b = bsp2 ? BinaryUtil.ReadUInt32(el, i * 8 + 4) : U16(el, i * 4 + 2);
            if (a >= (uint)vertexes.Length || b >= (uint)vertexes.Length)
            {
                if (vertexes.Length == 0) throw new AssetParseException("Map has edges but no vertexes.");
                a = b = 0;
            }
            edgeA[i] = (int)a;
            edgeB[i] = (int)b;
        }

        ReadOnlySpan<byte> sl = Lump(LumpSurfEdges, ofs, len, data);
        Funny(sl.Length, 4, "surfedges");
        int surfEdgeCount = sl.Length / 4;

        // ---- textures, light, planes, texinfo
        Q1Texture[] textures = ReadTextures(Lump(LumpTextures, ofs, len, data), hl);
        ReadOnlySpan<byte> lightLump = Lump(LumpLighting, ofs, len, data);
        byte[] lightData = ReadLighting(lightLump, hl, lit, dlit, out bool coloured, out byte[] deluxe);

        ReadOnlySpan<byte> pl = Lump(LumpPlanes, ofs, len, data);
        Funny(pl.Length, 20, "planes");
        var planes = new Q1Plane[pl.Length / 20];
        for (int i = 0; i < planes.Length; i++)
        {
            Vector3 n = BinaryUtil.ReadVec3(pl, i * 20);
            // PlaneClassify (the type in the file is not used)
            int type = n.X == 1 ? 0 : n.Y == 1 ? 1 : n.Z == 1 ? 2 : 3;
            planes[i] = new Q1Plane(n, BinaryUtil.ReadFloat(pl, i * 20 + 12), type);
        }

        ReadOnlySpan<byte> tl = Lump(LumpTexInfo, ofs, len, data);
        Funny(tl.Length, 40, "texinfo");
        var texInfo = new Q1TexInfo[tl.Length / 40];
        for (int i = 0; i < texInfo.Length; i++)
        {
            int o = i * 40;
            var s = new Vector4(BinaryUtil.ReadFloat(tl, o), BinaryUtil.ReadFloat(tl, o + 4), BinaryUtil.ReadFloat(tl, o + 8), BinaryUtil.ReadFloat(tl, o + 12));
            var t = new Vector4(BinaryUtil.ReadFloat(tl, o + 16), BinaryUtil.ReadFloat(tl, o + 20), BinaryUtil.ReadFloat(tl, o + 24), BinaryUtil.ReadFloat(tl, o + 28));
            texInfo[i] = new Q1TexInfo(s, t, BinaryUtil.ReadInt32(tl, o + 32), BinaryUtil.ReadInt32(tl, o + 36));
        }

        // ---- faces
        ReadOnlySpan<byte> fl = Lump(LumpFaces, ofs, len, data);
        int faceSize = bsp2 ? 28 : 20;
        Funny(fl.Length, faceSize, "faces");
        int faceCount = fl.Length / faceSize;
        long totalVerts = 0;
        for (int i = 0; i < faceCount; i++)
        {
            int n = bsp2 ? BinaryUtil.ReadInt32(fl, i * faceSize + 12) : (int)U16(fl, i * faceSize + 8);
            if (n < 0 || n > MaxFaceVertices) throw new AssetParseException($"Face {i} has an invalid edge count {n}.");
            totalVerts += n;
        }
        if (totalVerts > 0x0FFFFFFF) throw new AssetParseException("Map has too many face vertices.");
        var faces = new Q1Face[faceCount];
        var faceVertices = new Vector3[totalVerts];
        int firstVertex = 0;
        for (int i = 0; i < faceCount; i++)
        {
            int o = i * faceSize;
            int planeNum, side, firstEdge, numEdges, texInfoIndex, styleAt;
            if (bsp2)
            {
                planeNum = BinaryUtil.ReadInt32(fl, o); side = BinaryUtil.ReadInt32(fl, o + 4); firstEdge = BinaryUtil.ReadInt32(fl, o + 8);
                numEdges = BinaryUtil.ReadInt32(fl, o + 12); texInfoIndex = BinaryUtil.ReadInt32(fl, o + 16); styleAt = o + 20;
            }
            else
            {
                planeNum = (int)U16(fl, o); side = (int)U16(fl, o + 2); firstEdge = BinaryUtil.ReadInt32(fl, o + 4);
                numEdges = (int)U16(fl, o + 8); texInfoIndex = (int)U16(fl, o + 10); styleAt = o + 12;
            }
            byte s0 = fl[styleAt], s1 = fl[styleAt + 1], s2 = fl[styleAt + 2], s3 = fl[styleAt + 3];
            int lightOffset = BinaryUtil.ReadInt32(fl, styleAt + 4);

            if ((uint)firstEdge > (uint)surfEdgeCount || (uint)numEdges > (uint)surfEdgeCount || (uint)firstEdge + (uint)numEdges > (uint)surfEdgeCount)
                throw new AssetParseException($"Face {i} has an invalid edge range (first {firstEdge}, count {numEdges}, map has {surfEdgeCount}).");
            if ((uint)texInfoIndex >= (uint)texInfo.Length) throw new AssetParseException($"Face {i} has an invalid texinfo index {texInfoIndex}.");
            if ((uint)planeNum >= (uint)planes.Length) throw new AssetParseException($"Face {i} has an invalid plane index {planeNum}.");

            ref readonly Q1TexInfo ti = ref texInfo[texInfoIndex];
            // Mod_Q1BSP_LoadTexinfo: the miptex if it is valid, else one of the two spare textures
            int textureIndex = textures.Length > 0 && (uint)ti.MipTex < (uint)textures.Length ? ti.MipTex : ti.IsSpecial ? -2 : -1;
            string textureName = textureIndex >= 0 ? textures[textureIndex].Name : string.Empty;

            Vector3 mins = new(float.MaxValue), maxs = new(float.MinValue);
            float minS = 0, maxS = 0, minT = 0, maxT = 0;
            for (int v = 0; v < numEdges; v++)
            {
                int lindex = BinaryUtil.ReadInt32(sl, (firstEdge + v) * 4);
                if (lindex <= -edgeCount || lindex >= edgeCount) throw new AssetParseException($"Surfedge {firstEdge + v} references edge {lindex}, which does not exist.");
                Vector3 p = lindex >= 0 ? vertexes[edgeA[lindex]] : vertexes[edgeB[-lindex]];
                faceVertices[firstVertex + v] = p;
                mins = Vector3.Min(mins, p);
                maxs = Vector3.Max(maxs, p);
                float s = p.X * ti.S.X + p.Y * ti.S.Y + p.Z * ti.S.Z + ti.S.W;
                float t = p.X * ti.T.X + p.Y * ti.T.Y + p.Z * ti.T.Z + ti.T.W;
                if (v == 0) { minS = maxS = s; minT = maxT = t; }
                else { minS = MathF.Min(minS, s); maxS = MathF.Max(maxS, s); minT = MathF.Min(minT, t); maxT = MathF.Max(maxT, t); }
            }
            if (numEdges == 0) mins = maxs = default;

            // Mod_BuildNormals over the fan (area weighted), of which vertex 0 is in every triangle
            Vector3 normal = default;
            for (int t = 0; t + 2 < numEdges; t++)
            {
                Vector3 a = faceVertices[firstVertex], b = faceVertices[firstVertex + t + 1], c = faceVertices[firstVertex + t + 2];
                // TriangleNormal(a, b, c, n), term for term
                normal.X += (a.Y - b.Y) * (c.Z - b.Z) - (a.Z - b.Z) * (c.Y - b.Y);
                normal.Y += (a.Z - b.Z) * (c.X - b.X) - (a.X - b.X) * (c.Z - b.Z);
                normal.Z += (a.X - b.X) * (c.Y - b.Y) - (a.Y - b.Y) * (c.X - b.X);
            }
            // VectorNormalize: "float ilength = (float)DotProduct(v, v); if (ilength) ilength = 1.0f / sqrt(ilength); v *= ilength"
            // (a face with no area keeps a zero normal - and, in the surface traceline, stops every line that reaches its node)
            float ilength = normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z;
            if (ilength != 0) ilength = (float)(1.0 / Math.Sqrt(ilength));
            normal = float.IsFinite(ilength) ? new Vector3(normal.X * ilength, normal.Y * ilength, normal.Z * ilength) : default;

            if (!float.IsFinite(minS) || !float.IsFinite(maxS) || !float.IsFinite(minT) || !float.IsFinite(maxT)) throw new AssetParseException($"Face {i}: bad surface extents.");
            double fs0 = Math.Floor(minS / 16.0) * 16, fs1 = Math.Ceiling(maxS / 16.0) * 16, ft0 = Math.Floor(minT / 16.0) * 16, ft1 = Math.Ceiling(maxT / 16.0) * 16;
            if (Math.Abs(fs0) > 1e9 || Math.Abs(fs1) > 1e9 || Math.Abs(ft0) > 1e9 || Math.Abs(ft1) > 1e9) throw new AssetParseException($"Face {i}: bad surface extents.");
            int texMinS = (int)fs0, texMinT = (int)ft0, extS = (int)fs1 - texMinS, extT = (int)ft1 - texMinT;
            int ssize = (extS >> 4) + 1, tsize = (extT >> 4) + 1;
            if (ssize < 1 || tsize < 1) throw new AssetParseException($"Face {i}: bad surface extents.");

            // "the samples for every style of this face must lie inside the light data, otherwise treat the face as unlit"
            long sampleOffset = -1;
            if (lightOffset != -1 && lightData.Length > 0)
            {
                long start = hl ? lightOffset : (long)lightOffset * 3, size = 0;
                if (s0 != 255) size += (long)ssize * tsize * 3;
                if (s1 != 255) size += (long)ssize * tsize * 3;
                if (s2 != 255) size += (long)ssize * tsize * 3;
                if (s3 != 255) size += (long)ssize * tsize * 3;
                if (start >= 0 && start + size <= lightData.Length) sampleOffset = start;
            }
            // "give non-lightmapped water a 1x white lightmap"
            bool white = false;
            if (sampleOffset < 0 && textureName.StartsWith('*') && ti.IsSpecial && ssize <= 256 && tsize <= 256)
            {
                white = true;
                s0 = 0;
            }
            bool hasSamples = sampleOffset >= 0 || white;
            // "check if we should apply a lightmap to this"
            bool lightmapped = !ti.IsSpecial || hasSamples;
            if (lightmapped && (ssize > 256 || tsize > 256)) throw new AssetParseException($"Face {i}: bad surface extents.");

            faces[i] = new Q1Face(planeNum, side != 0, firstVertex, numEdges, texInfoIndex, textureIndex, s0, s1, s2, s3,
                (int)sampleOffset, white, lightmapped, texMinS, texMinT, extS, extT, normal, mins, maxs);
            firstVertex += numEdges;
        }

        // ---- marksurfaces
        ReadOnlySpan<byte> ml = Lump(LumpMarkSurfaces, ofs, len, data);
        int markSize = bsp2 ? 4 : 2;
        Funny(ml.Length, markSize, "marksurfaces");
        var markSurfaces = new int[ml.Length / markSize];
        for (int i = 0; i < markSurfaces.Length; i++)
        {
            int j = bsp2 ? BinaryUtil.ReadInt32(ml, i * 4) : (int)U16(ml, i * 2);
            if (j < 0 || j >= faceCount) throw new AssetParseException("Marksurfaces: bad surface number.");
            markSurfaces[i] = j;
        }

        // ---- models (before leafs: model 0 says how many leafs take part in visibility)
        ReadOnlySpan<byte> modelLump = Lump(LumpModels, ofs, len, data);
        const int modelSize = 48 + 4 * 4;
        Funny(modelLump.Length, modelSize, "models");
        var models = new Q1Model[modelLump.Length / modelSize];
        if (models.Length == 0) throw new AssetParseException("Map has no models.");
        for (int i = 0; i < models.Length; i++)
        {
            int o = i * modelSize;
            // "spread out the mins / maxs by a pixel"
            models[i] = new Q1Model(BinaryUtil.ReadVec3(modelLump, o) - Vector3.One, BinaryUtil.ReadVec3(modelLump, o + 12) + Vector3.One, BinaryUtil.ReadVec3(modelLump, o + 24),
                BinaryUtil.ReadInt32(modelLump, o + 36), BinaryUtil.ReadInt32(modelLump, o + 40), BinaryUtil.ReadInt32(modelLump, o + 44), BinaryUtil.ReadInt32(modelLump, o + 48),
                BinaryUtil.ReadInt32(modelLump, o + 52), BinaryUtil.ReadInt32(modelLump, o + 56), BinaryUtil.ReadInt32(modelLump, o + 60));
        }

        // ---- leafs and visibility
        ReadOnlySpan<byte> vis = Lump(LumpVisibility, ofs, len, data);
        ReadOnlySpan<byte> ll = Lump(LumpLeafs, ofs, len, data);
        int leafSize = rmqe ? 32 : bsp2 ? 44 : 28;
        Funny(ll.Length, leafSize, "leafs");
        int leafCount = ll.Length / leafSize;
        int clusters = Math.Clamp(models[0].VisLeafs, 0, leafCount);
        int clusterBytes = (clusters + 7) >> 3;
        if ((long)clusters * clusterBytes > 0x3FFFFFFF) throw new AssetParseException($"Map has too many vis clusters ({clusters}).");
        var pvs = new byte[clusters * clusterBytes];
        pvs.AsSpan().Fill(0xFF);
        var leafs = new Q1Leaf[leafCount];
        for (int i = 0; i < leafCount; i++)
        {
            int o = i * leafSize;
            int contents = BinaryUtil.ReadInt32(ll, o);
            int cluster = i - 1;
            if (cluster >= clusters) cluster = -1;
            int visOfs = BinaryUtil.ReadInt32(ll, o + 4);
            if (visOfs >= 0 && cluster >= 0 && visOfs < vis.Length)
                DecompressVis(vis[visOfs..], pvs.AsSpan(cluster * clusterBytes, clusterBytes));
            Vector3 mins, maxs;
            int firstMark, markCount, ambientAt;
            if (bsp2 && !rmqe)
            {
                mins = BinaryUtil.ReadVec3(ll, o + 8); maxs = BinaryUtil.ReadVec3(ll, o + 20);
                firstMark = BinaryUtil.ReadInt32(ll, o + 32); markCount = BinaryUtil.ReadInt32(ll, o + 36); ambientAt = o + 40;
            }
            else
            {
                mins = S16Vec(ll, o + 8); maxs = S16Vec(ll, o + 14);
                if (rmqe) { firstMark = BinaryUtil.ReadInt32(ll, o + 20); markCount = BinaryUtil.ReadInt32(ll, o + 24); ambientAt = o + 28; }
                else { firstMark = (int)U16(ll, o + 20); markCount = (int)U16(ll, o + 22); ambientAt = o + 24; }
            }
            if (firstMark < 0 || markCount < 0 || (long)firstMark + markCount > markSurfaces.Length) { firstMark = 0; markCount = 0; }
            leafs[i] = new Q1Leaf(contents, cluster, mins, maxs, firstMark, markCount, BinaryUtil.ReadUInt32(ll, ambientAt));
        }

        // ---- nodes
        ReadOnlySpan<byte> nlump = Lump(LumpNodes, ofs, len, data);
        int nodeSize = rmqe ? 32 : bsp2 ? 44 : 24;
        Funny(nlump.Length, nodeSize, "nodes");
        int nodeCount = nlump.Length / nodeSize;
        if (nodeCount == 0) throw new AssetParseException("Map has no BSP tree.");
        var nodes = new Q1Node[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            int o = i * nodeSize;
            int plane = BinaryUtil.ReadInt32(nlump, o);
            if ((uint)plane >= (uint)planes.Length) throw new AssetParseException($"Node {i} has an invalid plane index {plane}.");
            int c0, c1, firstFace, numFaces;
            Vector3 mins, maxs;
            if (bsp2)
            {
                c0 = BinaryUtil.ReadInt32(nlump, o + 4); c1 = BinaryUtil.ReadInt32(nlump, o + 8);
                if (rmqe) { mins = S16Vec(nlump, o + 12); maxs = S16Vec(nlump, o + 18); firstFace = BinaryUtil.ReadInt32(nlump, o + 24); numFaces = BinaryUtil.ReadInt32(nlump, o + 28); }
                else { mins = BinaryUtil.ReadVec3(nlump, o + 12); maxs = BinaryUtil.ReadVec3(nlump, o + 24); firstFace = BinaryUtil.ReadInt32(nlump, o + 36); numFaces = BinaryUtil.ReadInt32(nlump, o + 40); }
            }
            else
            {
                c0 = (int)U16(nlump, o + 4); c1 = (int)U16(nlump, o + 6);
                if (c0 >= nodeCount) c0 -= 65536;
                if (c1 >= nodeCount) c1 -= 65536;
                mins = S16Vec(nlump, o + 8); maxs = S16Vec(nlump, o + 14);
                firstFace = (int)U16(nlump, o + 20); numFaces = (int)U16(nlump, o + 22);
            }
            // a face range outside the file would be followed by the surface traceline and the light sampler
            if (firstFace < 0 || numFaces < 0 || (long)firstFace + numFaces > faceCount) { firstFace = 0; numFaces = 0; }
            nodes[i] = new Q1Node(plane, FixChild(c0, nodeCount, leafCount), FixChild(c1, nodeCount, leafCount), mins, maxs, firstFace, numFaces);
        }

        // ---- clipnodes
        ReadOnlySpan<byte> cl = Lump(LumpClipNodes, ofs, len, data);
        int clipSize = bsp2 ? 12 : 8;
        Funny(cl.Length, clipSize, "clipnodes");
        int clipCount = cl.Length / clipSize;
        var clipNodes = new Q1ClipNode[clipCount];
        for (int i = 0; i < clipCount; i++)
        {
            int o = i * clipSize;
            int plane = BinaryUtil.ReadInt32(cl, o);
            if (plane < 0 || plane >= planes.Length) throw new AssetParseException("Corrupt clipping hull (out of range plane number).");
            int c0, c1;
            if (bsp2)
            {
                c0 = BinaryUtil.ReadInt32(cl, o + 4); c1 = BinaryUtil.ReadInt32(cl, o + 8);
                if (c0 >= clipCount || c1 >= clipCount) throw new AssetParseException("Corrupt clipping hull (invalid child index).");
            }
            else
            {
                // "values above count are assumed to be contents values"
                c0 = (int)U16(cl, o + 4); c1 = (int)U16(cl, o + 6);
                if (c0 >= clipCount) c0 -= 65536;
                if (c1 >= clipCount) c1 -= 65536;
            }
            clipNodes[i] = new Q1ClipNode(plane, c0, c1);
        }

        // ---- the checks of Mod_Q1BSP_Load on each model
        for (int i = 0; i < models.Length; i++)
        {
            ref readonly Q1Model m = ref models[i];
            if (m.FirstFace < 0 || m.FaceCount < 0 || m.FirstFace > faceCount - m.FaceCount) throw new AssetParseException($"Submodel {i} has an invalid face range.");
            if (m.HeadNode0 < 0 || m.HeadNode0 >= nodeCount) throw new AssetParseException($"Submodel {i} has an invalid head node.");
            for (int h = 1; h < 4; h++)
                if (m.HeadNode(h) > 0 && m.HeadNode(h) >= clipCount)
                {
                    // Quake writes three hulls and leaves the fourth head node unset; only Half-Life traces hull 3
                    if (h == 3 && !hl) continue;
                    throw new AssetParseException($"Submodel {i} has an invalid clip node.");
                }
        }

        // ---- neither tree may loop, and neither may be deeper than the tracers can recurse
        var nodeChildren = new (int, int)[nodeCount];
        for (int i = 0; i < nodeCount; i++) nodeChildren[i] = (nodes[i].Child0, nodes[i].Child1);
        CheckTree(nodeChildren, "BSP tree");
        var clipChildren = new (int, int)[clipCount];
        for (int i = 0; i < clipCount; i++) clipChildren[i] = (clipNodes[i].Child0, clipNodes[i].Child1);
        CheckTree(clipChildren, "clipping hull");

        (Vector3, Vector3)[] hullSizes = hl
            ? new[] { (Vector3.Zero, Vector3.Zero), (new Vector3(-16, -16, -36), new Vector3(16, 16, 36)), (new Vector3(-32, -32, -32), new Vector3(32, 32, 32)), (new Vector3(-16, -16, -18), new Vector3(16, 16, 18)) }
            : new[] { (Vector3.Zero, Vector3.Zero), (new Vector3(-16, -16, -24), new Vector3(16, 16, 32)), (new Vector3(-32, -32, -24), new Vector3(32, 32, 64)), (Vector3.Zero, Vector3.Zero) };

        return new Q1BspData
        {
            Format = format,
            EntitiesText = entitiesText,
            Entities = entities,
            Planes = planes,
            Textures = textures,
            TexInfo = texInfo,
            Faces = faces,
            FaceVertices = faceVertices,
            LightData = lightData,
            LightIsColoured = coloured,
            DeluxeData = deluxe,
            Nodes = nodes,
            Leafs = leafs,
            MarkSurfaces = markSurfaces,
            ClipNodes = clipNodes,
            Models = models,
            PvsClusters = pvs,
            PvsClusterCount = clusters,
            PvsClusterBytes = clusterBytes,
            HasVis = vis.Length > 0,
            SupportsWaterAlpha = WaterAlphaSupport(leafs, pvs, clusters, clusterBytes, vis.Length > 0),
            HullSizes = hullSizes,
        };
    }

    // Mod_Q1BSP_LoadNodes: a child at or over zero is a node (an index past the end goes to the solid leaf);
    // a negative one is leaf -(p+1) (past the end: the solid leaf, leaf 0).
    private static int FixChild(int p, int nodeCount, int leafCount)
    {
        if (p >= 0) return p < nodeCount ? p : -1;
        int leaf = -(p + 1);
        return leaf < leafCount ? p : -1;
    }

    private static void Funny(int length, int size, string what)
    {
        if (length % size != 0) throw new AssetParseException($"Funny lump size in {what}: {length} bytes is not a multiple of {size}.");
    }

    private static uint U16(ReadOnlySpan<byte> d, int o) => (ushort)BinaryUtil.ReadInt16(d, o);
    private static Vector3 S16Vec(ReadOnlySpan<byte> d, int o) => new(BinaryUtil.ReadInt16(d, o), BinaryUtil.ReadInt16(d, o + 2), BinaryUtil.ReadInt16(d, o + 4));

    // Depth-first over every node: a child met again while still on the path is a loop.
    private static void CheckTree((int A, int B)[] children, string what)
    {
        int n = children.Length;
        var state = new byte[n];      // 0 unseen, 1 on the path, 2 done
        var depthOf = new int[n];     // the deepest path below a finished node
        var stack = new Stack<(int Node, int Phase)>();
        for (int root = 0; root < n; root++)
        {
            if (state[root] != 0) continue;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                (int node, int phase) = stack.Pop();
                if (phase == 0)
                {
                    if (state[node] == 2) continue;
                    state[node] = 1;
                    stack.Push((node, 1));
                    (int a, int b) = children[node];
                    if (a >= 0 && a < n)
                    {
                        if (state[a] == 1) throw new AssetParseException($"The map's {what} loops back on itself.");
                        if (state[a] == 0) stack.Push((a, 0));
                    }
                    if (b >= 0 && b < n)
                    {
                        if (state[b] == 1) throw new AssetParseException($"The map's {what} loops back on itself.");
                        if (state[b] == 0) stack.Push((b, 0));
                    }
                }
                else
                {
                    (int a, int b) = children[node];
                    int d = 0;
                    if (a >= 0 && a < n) d = Math.Max(d, depthOf[a]);
                    if (b >= 0 && b < n) d = Math.Max(d, depthOf[b]);
                    depthOf[node] = d + 1;
                    if (d + 1 > MaxTreeDepth) throw new AssetParseException($"The map's {what} is deeper than {MaxTreeDepth} levels.");
                    state[node] = 2;
                }
            }
        }
    }

    // Mod_BSP_DecompressVis: a zero byte is followed by a count of zero bytes.
    private static void DecompressVis(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int i = 0, o = 0;
        while (o < output.Length)
        {
            if (i >= input.Length) return;
            byte c = input[i++];
            if (c != 0) { output[o++] = c; continue; }
            if (i >= input.Length) return;
            for (int run = input[i++]; run > 0; run--)
            {
                if (o == output.Length) return;
                output[o++] = 0;
            }
        }
    }

    private static bool WaterAlphaSupport(Q1Leaf[] leafs, byte[] pvs, int clusters, int clusterBytes, bool hasVis)
    {
        // "if there's no vis data, assume supported (because everything is visible all the time)"
        // (DarkPlaces tests the cluster table, which exists whenever model 0 has vis leafs; a table filled
        // with ones answers true below as well, provided the map has an empty leaf.)
        if (clusters == 0) return true;
        _ = hasVis;
        for (int i = 0; i < leafs.Length; i++)
        {
            if ((leafs[i].Contents != Q1Contents.Water && leafs[i].Contents != Q1Contents.Slime) || leafs[i].Cluster < 0) continue;
            ReadOnlySpan<byte> row = pvs.AsSpan(leafs[i].Cluster * clusterBytes, clusterBytes);
            for (int j = 0; j < leafs.Length; j++)
            {
                int c = leafs[j].Cluster;
                if (c >= 0 && (row[c >> 3] & (1 << (c & 7))) != 0 && leafs[j].Contents == Q1Contents.Empty) return true;
            }
        }
        return false;
    }

    private static Q1Texture[] ReadTextures(ReadOnlySpan<byte> lump, bool halfLife)
    {
        if (lump.Length < 4) return Array.Empty<Q1Texture>();
        int count = BinaryUtil.ReadInt32(lump, 0);
        if (count < 0 || count > (lump.Length - 4) / 4) throw new AssetParseException($"Map has an invalid texture count {count}.");
        var textures = new Q1Texture[count];
        Span<char> name = stackalloc char[16];
        for (int i = 0; i < count; i++)
        {
            int offset = BinaryUtil.ReadInt32(lump, 4 + i * 4);
            if (offset < 0 || offset > lump.Length - 40)
            {
                // "miptex #%i missing": the slot keeps DarkPlaces' placeholder
                textures[i] = new Q1Texture("NO TEXTURE FOUND", 16, 16, null, null, false);
                continue;
            }
            ReadOnlySpan<byte> mt = lump[offset..];
            int nameLength = 0;
            while (nameLength < 16 && mt[nameLength] != 0)
            {
                char c = (char)mt[nameLength];
                name[nameLength++] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
            string textureName = nameLength == 0 ? $"unnamed{i}" : new string(name[..nameLength]);
            int width = BinaryUtil.ReadInt32(mt, 16), height = BinaryUtil.ReadInt32(mt, 20), dataOffset = BinaryUtil.ReadInt32(mt, 24);
            byte[]? pixels = null, palette = null;
            if (dataOffset != 0)
            {
                // "texture included"
                if (dataOffset < 40 || width < 0 || height < 0 || width > 16384 || height > 16384 || dataOffset > mt.Length || (long)width * height > mt.Length - dataOffset)
                {
                    // "is corrupt or incomplete": DarkPlaces leaves the placeholder in the slot
                    textures[i] = new Q1Texture("NO TEXTURE FOUND", 16, 16, null, null, false);
                    continue;
                }
                pixels = mt.Slice(dataOffset, width * height).ToArray();
                if (halfLife)
                {
                    // WAD3: the four mip levels, a two byte colour count, then 256 RGB colours
                    long paletteAt = dataOffset + (long)width * height + (width / 2) * (height / 2) + (width / 4) * (height / 4) + (width / 8) * (height / 8) + 2;
                    if (paletteAt + 768 <= mt.Length) palette = mt.Slice((int)paletteAt, 768).ToArray();
                }
            }
            if (width < 0 || height < 0 || width > 16384 || height > 16384)
            {
                textures[i] = new Q1Texture("NO TEXTURE FOUND", 16, 16, null, null, false);
                continue;
            }
            textures[i] = new Q1Texture(textureName, width, height, pixels, palette, true);
        }
        return textures;
    }

    // Mod_Q1BSP_LoadLighting.
    private static byte[] ReadLighting(ReadOnlySpan<byte> lump, bool halfLife, ReadOnlySpan<byte> lit, ReadOnlySpan<byte> dlit, out bool coloured, out byte[] deluxe)
    {
        deluxe = Array.Empty<byte>();
        coloured = false;
        if (halfLife)
        {
            // "load the colored lighting data straight", halved to the 0-2 range of a Quake map
            var hlData = new byte[lump.Length];
            for (int i = 0; i < lump.Length; i++) hlData[i] = (byte)(lump[i] >> 1);
            coloured = hlData.Length > 0;
            return hlData;
        }
        if (ValidLit(lit, lump.Length))
        {
            coloured = true;
            if (ValidLit(dlit, lump.Length)) deluxe = dlit[8..].ToArray();
            return lit[8..].ToArray();
        }
        if (lump.Length == 0) return Array.Empty<byte>();
        // "oh well, expand the white lighting data"
        var data = new byte[(long)lump.Length * 3];
        for (int i = 0, o = 0; i < lump.Length; i++)
        {
            byte d = lump[i];
            data[o++] = d;
            data[o++] = d;
            data[o++] = d;
        }
        return data;
    }

    // "QLIT", version 1, and exactly three bytes for each byte of the map's own lighting.
    private static bool ValidLit(ReadOnlySpan<byte> lit, int lightLumpLength) =>
        lit.Length >= 8 && lit.Length == 8 + (long)lightLumpLength * 3 && lit[0] == (byte)'Q' && lit[1] == (byte)'L' && lit[2] == (byte)'I' && lit[3] == (byte)'T' &&
        BinaryUtil.ReadInt32(lit, 4) == 1 && lightLumpLength > 0;
}

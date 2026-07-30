using System.Numerics;

namespace XonoticGodot.Formats.Dpm;

/// <summary>
/// Parses a DarkPlaces Model (DPM, id "DARKPLACESMODEL\0", type 2) from a raw byte buffer into
/// <see cref="DpmData"/>.
///
/// Faithful to <c>Mod_DARKPLACESMODEL_Load</c> (DarkPlaces <c>model_alias.c</c>) and the structs in
/// <c>model_dpmodel.h</c>. <b>Every</b> multi-byte value on disk is big-endian; all numeric reads go
/// through <see cref="DpmBinary"/>. Header offsets (ofs_bones / ofs_meshs / ofs_frames), the per-mesh
/// offsets (ofs_verts / ofs_texcoords / ofs_indices / ofs_groupids) and a frame's ofs_bonepositions
/// are all <i>absolute</i>, relative to the start of the file.
///
/// The parser stores the raw, weighted, bone-relative vertices plus the per-frame 3x4 bone matrices.
/// It deliberately does <b>not</b> skin (blend vertices through bone matrices) — that is the Godot
/// builder's job; see the remarks on <see cref="DpmMesh"/>.
///
/// All reads are bounds-checked; malformed input throws <see cref="AssetParseException"/>.
/// </summary>
public static class DpmReader
{
    /// <summary>The 16-byte file identifier, including its trailing NUL ("DARKPLACESMODEL\0").</summary>
    private const string Magic = "DARKPLACESMODEL\0";

    /// <summary>DPM model type for hierarchical skeletal pose models (the only one supported).</summary>
    public const uint SkeletalType = 2;

    private const int IdLen = 16;     // char id[16]
    private const int NameLen = 32;   // bone / shader / frame names are char[32]

    // On-disk struct sizes (bytes).
    private const int HeaderSize = 80;     // id[16] + type + filesize + mins[3]+maxs[3]+yawradius+allradius(8f) + 6 uints
    private const int BoneSize = 40;       // name[32] + parent(int) + flags(uint)
    private const int MeshSize = 56;       // shadername[32] + num_verts + num_tris + 4 offsets
    private const int FrameSize = 68;      // name[32] + mins[3]+maxs[3]+yawradius+allradius(8f) + ofs_bonepositions(int)
    private const int BoneVertSize = 32;   // origin[3] + influence + normal[3] + bonenum  (dpmbonevert_t)
    private const int BonePoseSize = 48;   // float matrix[3][4]  (dpmbonepose_t)
    private const int TexCoordSize = 8;    // float[2]
    private const int IndexSize = 4;       // uint
    private const int GroupIdSize = 4;     // uint

    public static DpmData Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(new ReadOnlySpan<byte>(data));
    }

    public static DpmData Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new AssetParseException(
                $"DPM too small: {data.Length} bytes, need at least {HeaderSize} for the header.");

        // --- header (all big-endian) ---
        string id = DpmBinary.ReadId(data, 0, IdLen);
        if (id != Magic)
            throw new AssetParseException(
                $"Not a DPM file: id is \"{Describe(id)}\", expected \"DARKPLACESMODEL\\0\".");

        uint type = DpmBinary.ReadUInt32BE(data, 16);
        if (type != SkeletalType)
            throw new AssetParseException(
                $"DPM has unsupported type {type}; only type {SkeletalType} (hierarchical skeletal pose) is supported.");

        // data[20] = filesize (informational; not trusted for bounds — we validate against the real span).
        Vector3 mins = DpmBinary.ReadVec3BE(data, 24);
        Vector3 maxs = DpmBinary.ReadVec3BE(data, 36);
        float yawRadius = DpmBinary.ReadFloatBE(data, 48);
        float allRadius = DpmBinary.ReadFloatBE(data, 52);

        long numBones = DpmBinary.ReadUInt32BE(data, 56);
        long numMeshes = DpmBinary.ReadUInt32BE(data, 60);
        long numFrames = DpmBinary.ReadUInt32BE(data, 64);
        int ofsBones = checked((int)DpmBinary.ReadUInt32BE(data, 68));
        int ofsMeshes = checked((int)DpmBinary.ReadUInt32BE(data, 72));
        int ofsFrames = checked((int)DpmBinary.ReadUInt32BE(data, 76));

        // DarkPlaces rejects empty geometry/frames outright.
        if (numBones < 1)
            throw new AssetParseException("DPM has no bones.");
        if (numMeshes < 1)
            throw new AssetParseException("DPM has no meshes.");
        if (numFrames < 1)
            throw new AssetParseException("DPM has no frames.");

        // Counts are read as uint; cap them so downstream array allocations and offset math stay sane.
        if (numBones > int.MaxValue || numMeshes > int.MaxValue || numFrames > int.MaxValue)
            throw new AssetParseException("DPM declares an absurd bone/mesh/frame count.");

        int boneCount = (int)numBones;

        DpmBone[] bones = ReadBones(data, ofsBones, boneCount);
        DpmMesh[] meshes = ReadMeshes(data, ofsMeshes, (int)numMeshes, boneCount);
        DpmFrame[] frames = ReadFrames(data, ofsFrames, (int)numFrames, boneCount);

        return new DpmData
        {
            Mins = mins,
            Maxs = maxs,
            YawRadius = yawRadius,
            AllRadius = allRadius,
            Bones = bones,
            Meshes = meshes,
            Frames = frames,
        };
    }

    private static DpmBone[] ReadBones(ReadOnlySpan<byte> data, int ofsBones, int boneCount)
    {
        var bones = new DpmBone[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            int o = ofsBones + i * BoneSize;
            string name = DpmBinary.ReadName(data, o, NameLen);
            int parent = DpmBinary.ReadInt32BE(data, o + 32);
            uint flags = DpmBinary.ReadUInt32BE(data, o + 36);

            // Spec invariant: a bone's parent must be a lower-numbered bone (or -1 for a root). This is
            // what lets the host compose world matrices in one forward pass; DarkPlaces hard-errors here.
            if (parent >= i)
                throw new AssetParseException(
                    $"DPM bone[{i}] \"{name}\" has parent {parent}, which must be < {i} (parents come first).");
            if (parent < -1)
                throw new AssetParseException(
                    $"DPM bone[{i}] \"{name}\" has invalid parent {parent} (must be -1 or a prior bone).");

            bones[i] = new DpmBone(name, parent, flags);
        }
        return bones;
    }

    private static DpmMesh[] ReadMeshes(ReadOnlySpan<byte> data, int ofsMeshes, int meshCount, int boneCount)
    {
        var meshes = new DpmMesh[meshCount];
        for (int m = 0; m < meshCount; m++)
        {
            int o = ofsMeshes + m * MeshSize;
            string shaderName = DpmBinary.ReadName(data, o, NameLen);
            long numVerts = DpmBinary.ReadUInt32BE(data, o + 32);
            long numTris = DpmBinary.ReadUInt32BE(data, o + 36);
            int ofsVerts = checked((int)DpmBinary.ReadUInt32BE(data, o + 40));
            int ofsTexCoords = checked((int)DpmBinary.ReadUInt32BE(data, o + 44));
            int ofsIndices = checked((int)DpmBinary.ReadUInt32BE(data, o + 48));
            int ofsGroupIds = checked((int)DpmBinary.ReadUInt32BE(data, o + 52));

            if (numVerts > int.MaxValue || numTris > int.MaxValue)
                throw new AssetParseException($"DPM mesh {m} declares an absurd vertex/triangle count.");
            int vertCount = (int)numVerts;
            int triCount = (int)numTris;

            DpmVertex[] vertices = ReadVertices(data, ofsVerts, vertCount, boneCount, m);
            Vector2[] texCoords = ReadTexCoords(data, ofsTexCoords, vertCount);
            int[] triangles = ReadIndices(data, ofsIndices, triCount, vertCount, m);
            uint[] groupIds = ReadGroupIds(data, ofsGroupIds, triCount);

            meshes[m] = new DpmMesh
            {
                ShaderName = shaderName,
                Vertices = vertices,
                TexCoords = texCoords,
                Triangles = triangles,
                GroupIds = groupIds,
            };
        }
        return meshes;
    }

    /// <summary>
    /// Reads <paramref name="vertCount"/> variable-size vertices, parsed sequentially. Each vertex is a
    /// uint <c>numbones</c> followed by that many <c>dpmbonevert_t</c> records — there is no per-vertex
    /// offset table, so we must walk the stream and advance the cursor by each vertex's real size.
    /// </summary>
    private static DpmVertex[] ReadVertices(
        ReadOnlySpan<byte> data, int ofsVerts, int vertCount, int boneCount, int meshIndex)
    {
        var vertices = new DpmVertex[vertCount];
        int cursor = ofsVerts;
        for (int v = 0; v < vertCount; v++)
        {
            long numWeights = DpmBinary.ReadUInt32BE(data, cursor);
            cursor += 4; // past dpmvertex_t.numbones

            if (numWeights < 1)
                throw new AssetParseException(
                    $"DPM mesh {meshIndex} vertex {v} has {numWeights} bone weights (need at least 1).");
            if (numWeights > int.MaxValue)
                throw new AssetParseException($"DPM mesh {meshIndex} vertex {v} declares an absurd weight count.");

            int weightCount = (int)numWeights;
            var weights = new DpmBoneWeight[weightCount];
            for (int w = 0; w < weightCount; w++)
            {
                int wo = cursor + w * BoneVertSize;
                Vector3 origin = DpmBinary.ReadVec3BE(data, wo);
                float influence = DpmBinary.ReadFloatBE(data, wo + 12);
                Vector3 normal = DpmBinary.ReadVec3BE(data, wo + 16);
                int boneNum = checked((int)DpmBinary.ReadUInt32BE(data, wo + 28));

                if (boneNum < 0 || boneNum >= boneCount)
                    throw new AssetParseException(
                        $"DPM mesh {meshIndex} vertex {v} weight {w} references bone {boneNum}, out of range 0..{boneCount - 1}.");

                weights[w] = new DpmBoneWeight(boneNum, origin, influence, normal);
            }

            cursor += weightCount * BoneVertSize;
            vertices[v] = new DpmVertex { Weights = weights };
        }
        return vertices;
    }

    private static Vector2[] ReadTexCoords(ReadOnlySpan<byte> data, int ofsTexCoords, int vertCount)
    {
        var texCoords = new Vector2[vertCount];
        for (int i = 0; i < vertCount; i++)
            texCoords[i] = DpmBinary.ReadVec2BE(data, ofsTexCoords + i * TexCoordSize);
        return texCoords;
    }

    /// <summary>
    /// Reads <paramref name="triCount"/> triangles as 3 big-endian uint indices each. Order is preserved
    /// exactly as on disk (no winding flip — DarkPlaces reverses to match its clockwise convention, but
    /// Godot is CCW-front like DPM). Every index is range-checked against the mesh's vertex count.
    /// </summary>
    private static int[] ReadIndices(
        ReadOnlySpan<byte> data, int ofsIndices, int triCount, int vertCount, int meshIndex)
    {
        var triangles = new int[triCount * 3];
        for (int t = 0; t < triCount; t++)
        {
            int baseOff = ofsIndices + t * 3 * IndexSize;
            for (int k = 0; k < 3; k++)
            {
                int idx = checked((int)DpmBinary.ReadUInt32BE(data, baseOff + k * IndexSize));
                if (idx < 0 || idx >= vertCount)
                    throw new AssetParseException(
                        $"DPM mesh {meshIndex} triangle {t} index {idx} out of range 0..{vertCount - 1}.");
                triangles[t * 3 + k] = idx;
            }
        }
        return triangles;
    }

    private static uint[] ReadGroupIds(ReadOnlySpan<byte> data, int ofsGroupIds, int triCount)
    {
        var groupIds = new uint[triCount];
        for (int t = 0; t < triCount; t++)
            groupIds[t] = DpmBinary.ReadUInt32BE(data, ofsGroupIds + t * GroupIdSize);
        return groupIds;
    }

    private static DpmFrame[] ReadFrames(ReadOnlySpan<byte> data, int ofsFrames, int frameCount, int boneCount)
    {
        var frames = new DpmFrame[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            int o = ofsFrames + f * FrameSize;
            string name = DpmBinary.ReadName(data, o, NameLen);
            Vector3 mins = DpmBinary.ReadVec3BE(data, o + 32);
            Vector3 maxs = DpmBinary.ReadVec3BE(data, o + 44);
            float yawRadius = DpmBinary.ReadFloatBE(data, o + 56);
            float allRadius = DpmBinary.ReadFloatBE(data, o + 60);
            int ofsBonePositions = DpmBinary.ReadInt32BE(data, o + 64);

            if (ofsBonePositions < 0)
                throw new AssetParseException(
                    $"DPM frame {f} \"{name}\" has negative bone-position offset {ofsBonePositions}.");

            var poses = new DpmBonePose[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                // dpmbonepose_t: float matrix[3][4], row-major. Each row is (col0, col1, col2, translation).
                int po = ofsBonePositions + b * BonePoseSize;
                float m00 = DpmBinary.ReadFloatBE(data, po);
                float m01 = DpmBinary.ReadFloatBE(data, po + 4);
                float m02 = DpmBinary.ReadFloatBE(data, po + 8);
                float m03 = DpmBinary.ReadFloatBE(data, po + 12);
                float m10 = DpmBinary.ReadFloatBE(data, po + 16);
                float m11 = DpmBinary.ReadFloatBE(data, po + 20);
                float m12 = DpmBinary.ReadFloatBE(data, po + 24);
                float m13 = DpmBinary.ReadFloatBE(data, po + 28);
                float m20 = DpmBinary.ReadFloatBE(data, po + 32);
                float m21 = DpmBinary.ReadFloatBE(data, po + 36);
                float m22 = DpmBinary.ReadFloatBE(data, po + 40);
                float m23 = DpmBinary.ReadFloatBE(data, po + 44);

                // Columns 0/1/2 are the basis vectors (images of x/y/z); column 3 is the translation.
                var right = new Vector3(m00, m10, m20);
                var up = new Vector3(m01, m11, m21);
                var forward = new Vector3(m02, m12, m22);
                var origin = new Vector3(m03, m13, m23);
                poses[b] = new DpmBonePose(right, up, forward, origin);
            }

            frames[f] = new DpmFrame
            {
                Name = name,
                Mins = mins,
                Maxs = maxs,
                YawRadius = yawRadius,
                AllRadius = allRadius,
                BonePoses = poses,
            };
        }
        return frames;
    }

    /// <summary>Renders an id string for error messages with the trailing NUL shown as "\0".</summary>
    private static string Describe(string id) => id.Replace("\0", "\\0");
}

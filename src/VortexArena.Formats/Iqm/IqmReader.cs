using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XonoticGodot.Formats.Iqm;

/// <summary>
/// Parses an IQM ("INTERQUAKEMODEL", version 1 or 2) skeletal model from a raw byte buffer into
/// <see cref="IqmData"/>.
///
/// Faithful to <c>Mod_INTERQUAKEMODEL_Load</c> (Darkplaces <c>model_alias.c</c>) and the structs in
/// <c>model_iqm.h</c>. Everything is little-endian and every section offset in the header is relative to
/// the start of the file. All reads are bounds-checked against the buffer; malformed input throws
/// <see cref="AssetParseException"/> rather than crashing.
///
/// What is decoded:
/// <list type="bullet">
/// <item>Header + names text blob (offsets into it resolve mesh/joint/anim names).</item>
/// <item>Joints → the skeleton and its bone-local rest ("base") pose. Version-1 joints store only the
///   quaternion xyz; the w component is reconstructed exactly as DP does.</item>
/// <item>Vertex arrays: POSITION(float3) and TEXCOORD(float2) are required; NORMAL(float3),
///   TANGENT(float4), BLENDINDEXES(ubyte4), BLENDWEIGHTS(ubyte4 or float4) and COLOR(ubyte4/float4) are
///   decoded when present. Arrays of other type/format are ignored (as DP does).</item>
/// <item>Triangles (validated against the vertex count) and meshes (name + material from the text blob).</item>
/// <item>Anims, and — the load-bearing part — the poses+frames: each pose is decoded against the flat
///   framedata ushort stream using its channel bitmask and per-channel offset/scale, producing a
///   bone-local transform (translate + rotate quat + scale) for every bone of every frame.</item>
/// </list>
/// </summary>
public static class IqmReader
{
    // 16-byte magic, NUL-padded. DP compares the first 16 bytes against "INTERQUAKEMODEL".
    private static ReadOnlySpan<byte> MagicBytes => "INTERQUAKEMODEL\0"u8;
    private const int MagicLen = 16;

    public const int Version1 = 1;
    public const int Version2 = 2;

    // On-disk struct sizes (bytes). These are packed little-endian with natural 4-byte alignment.
    private const int HeaderSize = 124;       // iqmheader_t: char[16] + 27 * uint32
    private const int VertexArraySize = 20;   // iqmvertexarray_t: 5 * uint32
    private const int TriangleSize = 12;      // iqmtriangle_t: 3 * uint32
    private const int MeshSize = 24;          // iqmmesh_t: 6 * uint32
    private const int Joint1Size = 44;        // iqmjoint1_t: 2*uint + (3+3+3)*float
    private const int Joint2Size = 48;        // iqmjoint_t:  2*uint + (3+4+3)*float
    private const int Pose1Size = 80;         // iqmpose1_t: int + uint + 2*9*float
    private const int Pose2Size = 88;         // iqmpose_t:  int + uint + 2*10*float
    private const int AnimSize = 20;          // iqmanim_t: 5 * uint32 (name,first,num,framerate(float),flags)
    private const int BoundsSize = 32;        // iqmbounds_t: (3+3)*float + 2*float

    // Vertex-array type ids (model_iqm.h IQM_*).
    private const uint TYPE_POSITION = 0;
    private const uint TYPE_TEXCOORD = 1;
    private const uint TYPE_NORMAL = 2;
    private const uint TYPE_TANGENT = 3;
    private const uint TYPE_BLENDINDEXES = 4;
    private const uint TYPE_BLENDWEIGHTS = 5;
    private const uint TYPE_COLOR = 6;

    // Vertex-array element formats.
    private const uint FORMAT_UBYTE = 1;
    private const uint FORMAT_FLOAT = 7;

    private const uint IQM_LOOP = 1;

    public static IqmData Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(new ReadOnlySpan<byte>(data));
    }

    public static IqmData Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new AssetParseException(
                $"IQM too small: {data.Length} bytes, need at least {HeaderSize} for the header.");

        if (!data.Slice(0, MagicLen).SequenceEqual(MagicBytes))
            throw new AssetParseException("Not an IQM file: magic is not \"INTERQUAKEMODEL\\0\".");

        var h = ReadHeader(data);

        if (h.Version != Version1 && h.Version != Version2)
            throw new AssetParseException(
                $"IQM has unsupported version {h.Version} (only 1 and 2 are supported).");

        // Validate the major sections up front so later per-record reads are within a region we already
        // know fits the file. (BinaryUtil also bounds-checks each individual read as a backstop.)
        int jointSize = h.Version == Version1 ? Joint1Size : Joint2Size;
        int poseSize = h.Version == Version1 ? Pose1Size : Pose2Size;
        RequireSection(data, h.OfsText, h.NumText, 1, "text");
        RequireSection(data, h.OfsMeshes, h.NumMeshes, MeshSize, "meshes");
        RequireSection(data, h.OfsVertexArrays, h.NumVertexArrays, VertexArraySize, "vertexarrays");
        RequireSection(data, h.OfsTriangles, h.NumTriangles, TriangleSize, "triangles");
        RequireSection(data, h.OfsJoints, h.NumJoints, jointSize, "joints");
        RequireSection(data, h.OfsPoses, h.NumPoses, poseSize, "poses");
        RequireSection(data, h.OfsAnims, h.NumAnims, AnimSize, "anims");
        RequireSection(data, h.OfsComment, h.NumComment, 1, "comment");
        if (h.OfsBounds != 0)
            RequireSection(data, h.OfsBounds, h.NumFrames, BoundsSize, "bounds");
        // framedata is num_frames * num_framechannels ushorts.
        RequireSection(data, h.OfsFrames, checked(h.NumFrames * h.NumFramechannels), 2, "frames");

        // The text blob: NUL-separated strings; mesh/joint/anim "name" fields are byte offsets into it.
        ReadOnlySpan<byte> text = (h.NumText != 0 && h.OfsText != 0)
            ? data.Slice(h.OfsText, h.NumText)
            : ReadOnlySpan<byte>.Empty;

        IqmJoint[] joints = ReadJoints(data, in h, text);
        IqmMesh[] meshes = ReadMeshes(data, in h, text);
        int[] triangles = ReadTriangles(data, in h);
        VertexArrays va = ReadVertexArrays(data, in h);
        IqmAnim[] anims = ReadAnims(data, in h, text);
        IqmPose[] poses = ReadPoses(data, in h);
        IqmFrame[] frames = DecodeFrames(data, in h, poses);
        IqmBounds[]? bounds = ReadBounds(data, in h);

        string comment = (h.NumComment != 0 && h.OfsComment != 0)
            ? DecodeStringAt(data.Slice(h.OfsComment, h.NumComment), 0)
            : string.Empty;

        return new IqmData
        {
            Version = h.Version,
            Flags = h.Flags,
            VertexCount = h.NumVertexes,
            Joints = joints,
            Meshes = meshes,
            Triangles = triangles,
            Positions = va.Positions,
            TexCoords = va.TexCoords,
            Normals = va.Normals,
            Tangents = va.Tangents,
            BlendIndexes = va.BlendIndexes,
            BlendWeights = va.BlendWeights,
            Colors = va.Colors,
            Anims = anims,
            Frames = frames,
            Poses = poses,
            Bounds = bounds,
            Comment = comment,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Header
    // ---------------------------------------------------------------------------------------------

    private readonly struct Header
    {
        public int Version { get; init; }
        public uint Flags { get; init; }
        public int NumText { get; init; }
        public int OfsText { get; init; }
        public int NumMeshes { get; init; }
        public int OfsMeshes { get; init; }
        public int NumVertexArrays { get; init; }
        public int NumVertexes { get; init; }
        public int OfsVertexArrays { get; init; }
        public int NumTriangles { get; init; }
        public int OfsTriangles { get; init; }
        public int OfsNeighbors { get; init; }
        public int NumJoints { get; init; }
        public int OfsJoints { get; init; }
        public int NumPoses { get; init; }
        public int OfsPoses { get; init; }
        public int NumAnims { get; init; }
        public int OfsAnims { get; init; }
        public int NumFrames { get; init; }
        public int NumFramechannels { get; init; }
        public int OfsFrames { get; init; }
        public int OfsBounds { get; init; }
        public int NumComment { get; init; }
        public int OfsComment { get; init; }
    }

    private static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        // All fields after id[16] are uint32. We read as uint then range-check to int so a hostile
        // 0x80000000+ count/offset surfaces cleanly instead of wrapping negative.
        int o = MagicLen;
        uint version = ReadU32(data, o + 0);
        // filesize at +4, flags at +8.
        uint flags = ReadU32(data, o + 8);
        uint numText = ReadU32(data, o + 12);
        uint ofsText = ReadU32(data, o + 16);
        uint numMeshes = ReadU32(data, o + 20);
        uint ofsMeshes = ReadU32(data, o + 24);
        uint numVertexArrays = ReadU32(data, o + 28);
        uint numVertexes = ReadU32(data, o + 32);
        uint ofsVertexArrays = ReadU32(data, o + 36);
        uint numTriangles = ReadU32(data, o + 40);
        uint ofsTriangles = ReadU32(data, o + 44);
        uint ofsNeighbors = ReadU32(data, o + 48);
        uint numJoints = ReadU32(data, o + 52);
        uint ofsJoints = ReadU32(data, o + 56);
        uint numPoses = ReadU32(data, o + 60);
        uint ofsPoses = ReadU32(data, o + 64);
        uint numAnims = ReadU32(data, o + 68);
        uint ofsAnims = ReadU32(data, o + 72);
        uint numFrames = ReadU32(data, o + 76);
        uint numFramechannels = ReadU32(data, o + 80);
        uint ofsFrames = ReadU32(data, o + 84);
        uint ofsBounds = ReadU32(data, o + 88);
        uint numComment = ReadU32(data, o + 92);
        uint ofsComment = ReadU32(data, o + 96);
        // num_extensions / ofs_extensions at +100 / +104 — not needed.

        return new Header
        {
            Version = (int)version, // version is small; checked by caller
            Flags = flags,
            NumText = ToCount(numText, "num_text"),
            OfsText = ToOffset(ofsText, "ofs_text"),
            NumMeshes = ToCount(numMeshes, "num_meshes"),
            OfsMeshes = ToOffset(ofsMeshes, "ofs_meshes"),
            NumVertexArrays = ToCount(numVertexArrays, "num_vertexarrays"),
            NumVertexes = ToCount(numVertexes, "num_vertexes"),
            OfsVertexArrays = ToOffset(ofsVertexArrays, "ofs_vertexarrays"),
            NumTriangles = ToCount(numTriangles, "num_triangles"),
            OfsTriangles = ToOffset(ofsTriangles, "ofs_triangles"),
            OfsNeighbors = ToOffset(ofsNeighbors, "ofs_neighbors"),
            NumJoints = ToCount(numJoints, "num_joints"),
            OfsJoints = ToOffset(ofsJoints, "ofs_joints"),
            NumPoses = ToCount(numPoses, "num_poses"),
            OfsPoses = ToOffset(ofsPoses, "ofs_poses"),
            NumAnims = ToCount(numAnims, "num_anims"),
            OfsAnims = ToOffset(ofsAnims, "ofs_anims"),
            NumFrames = ToCount(numFrames, "num_frames"),
            NumFramechannels = ToCount(numFramechannels, "num_framechannels"),
            OfsFrames = ToOffset(ofsFrames, "ofs_frames"),
            OfsBounds = ToOffset(ofsBounds, "ofs_bounds"),
            NumComment = ToCount(numComment, "num_comment"),
            OfsComment = ToOffset(ofsComment, "ofs_comment"),
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Joints (skeleton + base pose)
    // ---------------------------------------------------------------------------------------------

    private static IqmJoint[] ReadJoints(ReadOnlySpan<byte> data, in Header h, ReadOnlySpan<byte> text)
    {
        var joints = new IqmJoint[h.NumJoints];
        bool v1 = h.Version == Version1;
        int stride = v1 ? Joint1Size : Joint2Size;

        for (int i = 0; i < h.NumJoints; i++)
        {
            int o = h.OfsJoints + i * stride;
            uint nameOfs = ReadU32(data, o + 0);
            int parent = BinaryUtil.ReadInt32(data, o + 4);
            if (parent >= i)
                throw new AssetParseException(
                    $"IQM joint[{i}] has parent {parent} >= {i}; parents must precede children.");

            Vector3 translate = BinaryUtil.ReadVec3(data, o + 8);
            Quaternion rotate;
            Vector3 scale;
            if (v1)
            {
                // Only the quaternion xyz are stored; reconstruct w (DP Matrix4x4_FromDoom3Joint).
                float qx = BinaryUtil.ReadFloat(data, o + 20);
                float qy = BinaryUtil.ReadFloat(data, o + 24);
                float qz = BinaryUtil.ReadFloat(data, o + 28);
                rotate = ReconstructQuaternion(qx, qy, qz);
                scale = BinaryUtil.ReadVec3(data, o + 32);
            }
            else
            {
                float qx = BinaryUtil.ReadFloat(data, o + 20);
                float qy = BinaryUtil.ReadFloat(data, o + 24);
                float qz = BinaryUtil.ReadFloat(data, o + 28);
                float qw = BinaryUtil.ReadFloat(data, o + 32);
                rotate = NormalizeJointQuaternion(qx, qy, qz, qw);
                scale = BinaryUtil.ReadVec3(data, o + 36);
            }

            joints[i] = new IqmJoint(
                ResolveName(text, nameOfs),
                parent,
                translate,
                rotate,
                scale);
        }
        return joints;
    }

    // ---------------------------------------------------------------------------------------------
    // Meshes / triangles
    // ---------------------------------------------------------------------------------------------

    private static IqmMesh[] ReadMeshes(ReadOnlySpan<byte> data, in Header h, ReadOnlySpan<byte> text)
    {
        var meshes = new IqmMesh[h.NumMeshes];
        for (int i = 0; i < h.NumMeshes; i++)
        {
            int o = h.OfsMeshes + i * MeshSize;
            uint nameOfs = ReadU32(data, o + 0);
            uint materialOfs = ReadU32(data, o + 4);
            int firstVertex = ToCount(ReadU32(data, o + 8), "mesh.first_vertex");
            int numVertexes = ToCount(ReadU32(data, o + 12), "mesh.num_vertexes");
            int firstTriangle = ToCount(ReadU32(data, o + 16), "mesh.first_triangle");
            int numTriangles = ToCount(ReadU32(data, o + 20), "mesh.num_triangles");

            // Sanity: a mesh's ranges must lie inside the shared buffers.
            if ((long)firstVertex + numVertexes > h.NumVertexes)
                throw new AssetParseException(
                    $"IQM mesh[{i}] vertex range {firstVertex}+{numVertexes} exceeds {h.NumVertexes} vertices.");
            if ((long)firstTriangle + numTriangles > h.NumTriangles)
                throw new AssetParseException(
                    $"IQM mesh[{i}] triangle range {firstTriangle}+{numTriangles} exceeds {h.NumTriangles} triangles.");

            meshes[i] = new IqmMesh(
                ResolveName(text, nameOfs),
                ResolveName(text, materialOfs),
                firstVertex,
                numVertexes,
                firstTriangle,
                numTriangles);
        }
        return meshes;
    }

    private static int[] ReadTriangles(ReadOnlySpan<byte> data, in Header h)
    {
        var tris = new int[checked(h.NumTriangles * 3)];
        for (int i = 0; i < h.NumTriangles; i++)
        {
            int o = h.OfsTriangles + i * TriangleSize;
            for (int k = 0; k < 3; k++)
            {
                uint raw = ReadU32(data, o + k * 4);
                if (raw >= (uint)h.NumVertexes)
                    throw new AssetParseException(
                        $"IQM triangle index {raw} out of range (0..{h.NumVertexes - 1}).");
                tris[i * 3 + k] = (int)raw;
            }
        }
        return tris;
    }

    // ---------------------------------------------------------------------------------------------
    // Vertex arrays
    // ---------------------------------------------------------------------------------------------

    private readonly struct VertexArrays
    {
        public Vector3[] Positions { get; init; }
        public Vector2[] TexCoords { get; init; }
        public Vector3[]? Normals { get; init; }
        public Vector4[]? Tangents { get; init; }
        public byte[]? BlendIndexes { get; init; }
        public byte[]? BlendWeights { get; init; }
        public Vector4[]? Colors { get; init; }
    }

    private static VertexArrays ReadVertexArrays(ReadOnlySpan<byte> data, in Header h)
    {
        int n = h.NumVertexes;
        Vector3[]? positions = null;
        Vector2[]? texCoords = null;
        Vector3[]? normals = null;
        Vector4[]? tangents = null;
        byte[]? blendIndexes = null;
        byte[]? blendWeights = null;
        Vector4[]? colors = null;

        for (int i = 0; i < h.NumVertexArrays; i++)
        {
            int o = h.OfsVertexArrays + i * VertexArraySize;
            uint type = ReadU32(data, o + 0);
            // flags at +4 (ignored)
            uint format = ReadU32(data, o + 8);
            uint size = ReadU32(data, o + 12);
            int offset = ToOffset(ReadU32(data, o + 16), "vertexarray.offset");

            // Element byte width for the formats we accept; others we cannot size, so skip (as DP).
            int elemBytes = format switch
            {
                FORMAT_FLOAT => 4,
                FORMAT_UBYTE => 1,
                _ => 0,
            };
            if (elemBytes == 0)
                continue;

            // Total bytes this array occupies; bounds-check the whole region once.
            long arrayBytes = (long)n * size * elemBytes;
            if (offset + arrayBytes > data.Length)
                continue; // DP silently skips arrays that don't fit

            switch (type)
            {
                case TYPE_POSITION:
                    if (format == FORMAT_FLOAT && size == 3)
                        positions = ReadVec3Array(data, offset, n);
                    break;
                case TYPE_TEXCOORD:
                    if (format == FORMAT_FLOAT && size == 2)
                        texCoords = ReadVec2Array(data, offset, n);
                    break;
                case TYPE_NORMAL:
                    if (format == FORMAT_FLOAT && size == 3)
                        normals = ReadVec3Array(data, offset, n);
                    break;
                case TYPE_TANGENT:
                    if (format == FORMAT_FLOAT && size == 4)
                        tangents = ReadVec4Array(data, offset, n);
                    break;
                case TYPE_BLENDINDEXES:
                    if (format == FORMAT_UBYTE && size == 4)
                        blendIndexes = ReadUByte4Array(data, offset, n);
                    break;
                case TYPE_BLENDWEIGHTS:
                    if (format == FORMAT_UBYTE && size == 4)
                        blendWeights = ReadUByte4Array(data, offset, n);
                    else if (format == FORMAT_FLOAT && size == 4)
                        blendWeights = ReadFloat4AsUByte4Array(data, offset, n);
                    break;
                case TYPE_COLOR:
                    if (format == FORMAT_FLOAT && size == 4)
                        colors = ReadVec4Array(data, offset, n);
                    else if (format == FORMAT_UBYTE && size == 4)
                        colors = ReadUByte4AsColorArray(data, offset, n);
                    break;
            }
        }

        // Mirror DP's hard requirement: a model with vertices must have positions+texcoords, and an
        // animated/boned model must have skinning arrays. (n == 0 ⇒ headless static; allow it.)
        if (n > 0)
        {
            if (positions is null || texCoords is null)
                throw new AssetParseException(
                    "IQM is missing required vertex arrays (POSITION float3 and/or TEXCOORD float2).");
            if ((h.NumFrames > 0 || h.NumAnims > 0) && (blendIndexes is null || blendWeights is null))
                throw new AssetParseException(
                    "IQM animated model is missing BLENDINDEXES/BLENDWEIGHTS skinning arrays.");
        }

        return new VertexArrays
        {
            Positions = positions ?? Array.Empty<Vector3>(),
            TexCoords = texCoords ?? Array.Empty<Vector2>(),
            Normals = normals,
            Tangents = tangents,
            BlendIndexes = blendIndexes,
            BlendWeights = blendWeights,
            Colors = colors,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Animations
    // ---------------------------------------------------------------------------------------------

    private static IqmAnim[] ReadAnims(ReadOnlySpan<byte> data, in Header h, ReadOnlySpan<byte> text)
    {
        var anims = new IqmAnim[h.NumAnims];
        for (int i = 0; i < h.NumAnims; i++)
        {
            int o = h.OfsAnims + i * AnimSize;
            uint nameOfs = ReadU32(data, o + 0);
            int firstFrame = ToCount(ReadU32(data, o + 4), "anim.first_frame");
            int numFrames = ToCount(ReadU32(data, o + 8), "anim.num_frames");
            float framerate = BinaryUtil.ReadFloat(data, o + 12);
            uint flags = ReadU32(data, o + 16);

            anims[i] = new IqmAnim(
                ResolveName(text, nameOfs),
                firstFrame,
                numFrames,
                framerate,
                (flags & IQM_LOOP) != 0);
        }
        return anims;
    }

    // ---------------------------------------------------------------------------------------------
    // Poses + frames — the critical skeletal-animation decode.
    //
    // Channel layout in the framedata bitstream (LSB first), per pose, per frame:
    //   v2 (10 channels): 0,1,2 = translate xyz ; 3,4,5,6 = rotate xyzw ; 7,8,9 = scale xyz
    //   v1 ( 9 channels): 0,1,2 = translate xyz ; 3,4,5   = rotate xyz   ; 6,7,8 = scale xyz
    //
    // For each frame, for each pose in order, we walk channels 0..C-1: if the pose's channelmask has that
    // bit set we consume one little-endian ushort from the flat framedata stream and the channel value is
    //   value = channeloffset[c] + raw * channelscale[c]
    // otherwise the value is just channeloffset[c] (constant). We MUST advance the stream pointer for every
    // set bit in index order even for channels we don't ultimately need, or the stream desyncs — this is
    // exactly DP's `*framedata++` sequence (it reads every set channel; it merely discards scale).
    // ---------------------------------------------------------------------------------------------

    private static IqmPose[] ReadPoses(ReadOnlySpan<byte> data, in Header h)
    {
        var poses = new IqmPose[h.NumPoses];
        bool v1 = h.Version == Version1;
        int stride = v1 ? Pose1Size : Pose2Size;
        int channels = v1 ? 9 : 10;

        for (int i = 0; i < h.NumPoses; i++)
        {
            int o = h.OfsPoses + i * stride;
            int parent = BinaryUtil.ReadInt32(data, o + 0);
            uint channelmask = ReadU32(data, o + 4);

            // Read raw offset/scale into the 10-slot layout. For v1 we expand 9 → 10 by mapping the
            // 9-channel order (t.xyz, r.xyz, s.xyz) onto slots (0..2 translate, 3..5 rotate xyz,
            // 7..9 scale) and leaving slot 6 (rotate w) inert, so consumers see one uniform layout.
            var offset = new float[10];
            var scale = new float[10];
            int baseOff = o + 8;
            int baseScale = o + 8 + channels * 4;

            for (int c = 0; c < channels; c++)
            {
                int slot = (v1 && c >= 3) ? (c >= 6 ? c + 1 : c) : c; // shift scale channels past the w slot
                offset[slot] = BinaryUtil.ReadFloat(data, baseOff + c * 4);
                scale[slot] = BinaryUtil.ReadFloat(data, baseScale + c * 4);
            }

            poses[i] = new IqmPose
            {
                Parent = parent,
                ChannelMask = channelmask,
                ChannelOffset = offset,
                ChannelScale = scale,
            };
        }
        return poses;
    }

    private static IqmFrame[] DecodeFrames(ReadOnlySpan<byte> data, in Header h, IqmPose[] poses)
    {
        if (h.NumFrames <= 0 || poses.Length == 0)
            return Array.Empty<IqmFrame>();

        bool v1 = h.Version == Version1;
        int numPoses = poses.Length;
        var frames = new IqmFrame[h.NumFrames];

        // framedata is a flat little-endian ushort stream; we consume strictly left-to-right.
        int cursor = h.OfsFrames;       // byte offset into `data`
        int frameEnd = h.OfsFrames + checked(h.NumFrames * h.NumFramechannels * 2);

        for (int f = 0; f < h.NumFrames; f++)
        {
            var bones = new IqmBonePose[numPoses];
            for (int p = 0; p < numPoses; p++)
            {
                IqmPose pose = poses[p];
                uint mask = pose.ChannelMask;
                float[] off = pose.ChannelOffset;
                float[] scl = pose.ChannelScale;

                // Translate (channels 0,1,2).
                float tx = DecodeChannel(data, ref cursor, frameEnd, mask, 0, off, scl);
                float ty = DecodeChannel(data, ref cursor, frameEnd, mask, 1, off, scl);
                float tz = DecodeChannel(data, ref cursor, frameEnd, mask, 2, off, scl);

                Quaternion rot;
                if (v1)
                {
                    // v1 stores rotate xyz in channels 3,4,5; reconstruct w. The 10-slot offset/scale put
                    // these in slots 3,4,5 (slot 6 inert). The bitmask, however, is the on-disk 9-bit mask,
                    // so bit 3,4,5 are rotate and 6,7,8 are scale.
                    float rx = DecodeChannel(data, ref cursor, frameEnd, mask, 3, off, scl);
                    float ry = DecodeChannel(data, ref cursor, frameEnd, mask, 4, off, scl);
                    float rz = DecodeChannel(data, ref cursor, frameEnd, mask, 5, off, scl);
                    rot = ReconstructQuaternion(rx, ry, rz);

                    float sx = DecodeChannel(data, ref cursor, frameEnd, mask, 6, off, scl, slotForValue: 7);
                    float sy = DecodeChannel(data, ref cursor, frameEnd, mask, 7, off, scl, slotForValue: 8);
                    float sz = DecodeChannel(data, ref cursor, frameEnd, mask, 8, off, scl, slotForValue: 9);
                    bones[p] = new IqmBonePose(new Vector3(tx, ty, tz), rot, new Vector3(sx, sy, sz));
                }
                else
                {
                    // v2 stores the full quaternion xyzw in channels 3,4,5,6; scale xyz in 7,8,9.
                    float rx = DecodeChannel(data, ref cursor, frameEnd, mask, 3, off, scl);
                    float ry = DecodeChannel(data, ref cursor, frameEnd, mask, 4, off, scl);
                    float rz = DecodeChannel(data, ref cursor, frameEnd, mask, 5, off, scl);
                    float rw = DecodeChannel(data, ref cursor, frameEnd, mask, 6, off, scl);
                    rot = NormalizeJointQuaternion(rx, ry, rz, rw);

                    float sx = DecodeChannel(data, ref cursor, frameEnd, mask, 7, off, scl);
                    float sy = DecodeChannel(data, ref cursor, frameEnd, mask, 8, off, scl);
                    float sz = DecodeChannel(data, ref cursor, frameEnd, mask, 9, off, scl);
                    bones[p] = new IqmBonePose(new Vector3(tx, ty, tz), rot, new Vector3(sx, sy, sz));
                }
            }
            frames[f] = new IqmFrame { Bones = bones };
        }
        return frames;
    }

    /// <summary>
    /// Decodes one animation channel for the current pose at the current stream cursor. If the channel's
    /// bit (<paramref name="bit"/>) is set in <paramref name="mask"/>, consumes one little-endian ushort
    /// from <paramref name="data"/> at <paramref name="cursor"/> (advancing it by 2) and returns
    /// <c>offset[slot] + raw*scale[slot]</c>; otherwise returns the constant <c>offset[slot]</c> without
    /// touching the stream. <paramref name="slotForValue"/> defaults to <paramref name="bit"/> (the common
    /// case) but is overridden for v1 scale channels whose value lives in a shifted offset/scale slot.
    /// </summary>
    private static float DecodeChannel(
        ReadOnlySpan<byte> data, ref int cursor, int frameEnd, uint mask, int bit,
        float[] offset, float[] scale, int slotForValue = -1)
    {
        int slot = slotForValue < 0 ? bit : slotForValue;
        if ((mask & (1u << bit)) == 0)
            return offset[slot];

        if (cursor + 2 > frameEnd)
            throw new AssetParseException(
                "IQM framedata exhausted while decoding channels (num_framechannels too small for the channel masks).");
        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(cursor, 2));
        cursor += 2;
        return offset[slot] + raw * scale[slot];
    }

    // ---------------------------------------------------------------------------------------------
    // Bounds
    // ---------------------------------------------------------------------------------------------

    private static IqmBounds[]? ReadBounds(ReadOnlySpan<byte> data, in Header h)
    {
        if (h.OfsBounds == 0 || h.NumFrames <= 0)
            return null;
        var bounds = new IqmBounds[h.NumFrames];
        for (int i = 0; i < h.NumFrames; i++)
        {
            int o = h.OfsBounds + i * BoundsSize;
            Vector3 mins = BinaryUtil.ReadVec3(data, o + 0);
            Vector3 maxs = BinaryUtil.ReadVec3(data, o + 12);
            float xyradius = BinaryUtil.ReadFloat(data, o + 24);
            float radius = BinaryUtil.ReadFloat(data, o + 28);
            bounds[i] = new IqmBounds(mins, maxs, xyradius, radius);
        }
        return bounds;
    }

    // ---------------------------------------------------------------------------------------------
    // Quaternion helpers (port of DP semantics)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reconstructs a unit quaternion from only its xyz (IQM version-1 joints and rotate channels). Mirrors
    /// <c>Matrix4x4_FromDoom3Joint</c>: <c>w = 1 - (x²+y²+z²)</c>, then <c>w = w &gt; 0 ? -sqrt(w) : 0</c>.
    /// The negative-w hemisphere matches DP's convention; the rotation represented is unaffected by sign.
    /// </summary>
    private static Quaternion ReconstructQuaternion(float x, float y, float z)
    {
        float w = 1.0f - (x * x + y * y + z * z);
        w = w > 0.0f ? -MathF.Sqrt(w) : 0.0f;
        return new Quaternion(x, y, z, w);
    }

    /// <summary>
    /// Normalizes a full quaternion the way DP does for version-2 joints/poses: force the scalar part
    /// non-positive (negate the whole quat if <paramref name="w"/> &gt; 0), then normalize to unit length.
    /// A zero-length quaternion degrades to identity. Sign-flipping a quaternion does not change its
    /// rotation, so this is purely a canonical-hemisphere normalization.
    /// </summary>
    private static Quaternion NormalizeJointQuaternion(float x, float y, float z, float w)
    {
        if (w > 0.0f)
        {
            x = -x; y = -y; z = -z; w = -w;
        }
        float lenSq = x * x + y * y + z * z + w * w;
        if (lenSq <= 0.0f)
            return Quaternion.Identity;
        float inv = 1.0f / MathF.Sqrt(lenSq);
        return new Quaternion(x * inv, y * inv, z * inv, w * inv);
    }

    // ---------------------------------------------------------------------------------------------
    // Low-level read helpers
    // ---------------------------------------------------------------------------------------------

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) => BinaryUtil.ReadUInt32(data, offset);

    private static Vector3[] ReadVec3Array(ReadOnlySpan<byte> data, int offset, int count)
    {
        var arr = new Vector3[count];
        for (int i = 0; i < count; i++)
            arr[i] = BinaryUtil.ReadVec3(data, offset + i * 12);
        return arr;
    }

    private static Vector2[] ReadVec2Array(ReadOnlySpan<byte> data, int offset, int count)
    {
        var arr = new Vector2[count];
        for (int i = 0; i < count; i++)
            arr[i] = BinaryUtil.ReadVec2(data, offset + i * 8);
        return arr;
    }

    private static Vector4[] ReadVec4Array(ReadOnlySpan<byte> data, int offset, int count)
    {
        var arr = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            int o = offset + i * 16;
            arr[i] = new Vector4(
                BinaryUtil.ReadFloat(data, o + 0),
                BinaryUtil.ReadFloat(data, o + 4),
                BinaryUtil.ReadFloat(data, o + 8),
                BinaryUtil.ReadFloat(data, o + 12));
        }
        return arr;
    }

    /// <summary>Reads a ubyte4 per-vertex array as one FLAT byte[count*4] (vertex v at [v*4..v*4+3]) — the
    /// on-disk bytes verbatim. Flat, not jagged: one byte[4] per vertex was tens of thousands of tiny GC
    /// objects per player model. The whole region was bounds-checked by the caller.</summary>
    private static byte[] ReadUByte4Array(ReadOnlySpan<byte> data, int offset, int count)
        => data.Slice(offset, count * 4).ToArray();

    /// <summary>Reads float4 blend weights and quantizes each to the 0..255 byte scale so all weights have
    /// one representation, in the same FLAT byte[count*4] layout as <see cref="ReadUByte4Array"/>. Values
    /// are clamped into [0,1] before scaling.</summary>
    private static byte[] ReadFloat4AsUByte4Array(ReadOnlySpan<byte> data, int offset, int count)
    {
        var arr = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            int o = offset + i * 16;
            for (int k = 0; k < 4; k++)
            {
                float v = BinaryUtil.ReadFloat(data, o + k * 4);
                v = v < 0f ? 0f : (v > 1f ? 1f : v);
                arr[i * 4 + k] = (byte)MathF.Round(v * 255.0f);
            }
        }
        return arr;
    }

    private static Vector4[] ReadUByte4AsColorArray(ReadOnlySpan<byte> data, int offset, int count)
    {
        const float inv255 = 1.0f / 255.0f;
        var arr = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            int o = offset + i * 4;
            arr[i] = new Vector4(
                data[o] * inv255,
                data[o + 1] * inv255,
                data[o + 2] * inv255,
                data[o + 3] * inv255);
        }
        return arr;
    }

    // ---------------------------------------------------------------------------------------------
    // Names / strings
    // ---------------------------------------------------------------------------------------------

    /// <summary>Resolves a name field (a byte offset into the text blob) to a NUL-terminated ASCII string.
    /// An out-of-range offset yields the empty string rather than throwing (DP would read past, but we are
    /// defensive). Offset 0 with an empty blob is also "".</summary>
    private static string ResolveName(ReadOnlySpan<byte> text, uint nameOffset)
    {
        if (nameOffset >= (uint)text.Length)
            return string.Empty;
        return DecodeStringAt(text, (int)nameOffset);
    }

    /// <summary>Reads an ASCII string starting at <paramref name="start"/> in <paramref name="blob"/> up to
    /// the next NUL (or the end of the blob). Trailing whitespace is trimmed (Quake tools sometimes pad).</summary>
    private static string DecodeStringAt(ReadOnlySpan<byte> blob, int start)
    {
        if (start < 0 || start >= blob.Length)
            return string.Empty;
        ReadOnlySpan<byte> rest = blob.Slice(start);
        int n = rest.IndexOf((byte)0);
        if (n < 0) n = rest.Length;
        return Encoding.ASCII.GetString(rest.Slice(0, n)).TrimEnd('\0', ' ', '\t', '\r', '\n');
    }

    // ---------------------------------------------------------------------------------------------
    // Validation helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Casts an on-disk uint count to int, rejecting values that don't fit a positive int
    /// (they would otherwise wrap negative and corrupt allocations/loops).</summary>
    private static int ToCount(uint value, string what)
    {
        if (value > int.MaxValue)
            throw new AssetParseException($"IQM {what} = {value} is too large.");
        return (int)value;
    }

    /// <summary>Same as <see cref="ToCount"/> for offset fields (also non-negative, ≤ int.MaxValue).</summary>
    private static int ToOffset(uint value, string what)
    {
        if (value > int.MaxValue)
            throw new AssetParseException($"IQM {what} = {value} is too large.");
        return (int)value;
    }

    /// <summary>Verifies that a section [offset, offset + count*stride) lies within the buffer, throwing a
    /// clear error otherwise. A zero offset with a non-zero count is treated as "absent" only by callers
    /// that special-case it (e.g. bounds); here count==0 always passes.</summary>
    private static void RequireSection(ReadOnlySpan<byte> data, int offset, int count, int stride, string what)
    {
        if (count == 0)
            return;
        long need = (long)count * stride;
        if (offset < 0 || need < 0 || offset + need > data.Length)
            throw new AssetParseException(
                $"IQM {what} section out of bounds: offset {offset}, {count} * {stride} = {need} byte(s), buffer is {data.Length} byte(s).");
    }
}

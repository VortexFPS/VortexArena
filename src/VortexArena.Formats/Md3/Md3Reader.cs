using System.Numerics;

namespace XonoticGodot.Formats.Md3;

/// <summary>
/// Parses an MD3 ("IDP3", version 15) model from a raw byte buffer into <see cref="Md3Data"/>.
///
/// Faithful to <c>Mod_IDP3_Load</c> (Darkplaces <c>model_alias.c</c>) and the structs in
/// <c>model_alias.h</c>. All lump offsets in the model header are relative to the start of the file;
/// all lump offsets in a mesh sub-header are relative to the start of that mesh struct. Tags are stored
/// frame-major: all tags of frame 0, then all tags of frame 1, etc.
///
/// All reads are bounds-checked; malformed input throws <see cref="AssetParseException"/>.
/// </summary>
public static class Md3Reader
{
    private const string Magic = "IDP3";
    public const int Version = 15;

    private const int NameLen = 64;       // MD3NAME
    private const int FrameNameLen = 16;  // MD3FRAMENAME

    // On-disk struct sizes (bytes).
    private const int ModelHeaderSize = 108; // ident[4]+version+name[64]+flags+5 ints+4 lump offsets
    private const int FrameInfoSize = 56;    // mins[3]+maxs[3]+origin[3]+radius + name[16]
    private const int TagSize = 112;         // name[64] + origin[3] + rotationmatrix[9]
    private const int MeshHeaderSize = 108;  // ident[4]+name[64]+flags+num*4+lump*5
    private const int ShaderSize = 68;       // name[64] + shadernum
    private const int TriangleSize = 12;     // 3 * int
    private const int TexCoordSize = 8;      // 2 * float
    private const int VertexSize = 8;        // short[3] + pitch + yaw

    // MD3 normal lat/long byte angles are quantized to 0-255 over a full turn.
    private const float ByteAngleToRadians = MathF.Tau / 256.0f;

    public static Md3Data Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(new ReadOnlySpan<byte>(data));
    }

    public static Md3Data Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < ModelHeaderSize)
            throw new AssetParseException($"MD3 too small: {data.Length} bytes, need at least {ModelHeaderSize} for the header.");

        string magic = BinaryUtil.ReadMagic(data, 0);
        if (magic != Magic)
            throw new AssetParseException($"Not an MD3 file: magic is \"{magic}\", expected \"{Magic}\".");

        int version = BinaryUtil.ReadInt32(data, 4);
        if (version != Version)
            throw new AssetParseException($"MD3 has wrong version {version} (expected {Version}).");

        string name = BinaryUtil.ReadFixedString(data, 8, NameLen);
        int flags = BinaryUtil.ReadInt32(data, 72);
        int numFrames = BinaryUtil.ReadInt32(data, 76);
        int numTags = BinaryUtil.ReadInt32(data, 80);
        int numMeshes = BinaryUtil.ReadInt32(data, 84);
        // data[88] = num_skins (unused engine field)
        int lumpFrameInfo = BinaryUtil.ReadInt32(data, 92);
        int lumpTags = BinaryUtil.ReadInt32(data, 96);
        int lumpMeshes = BinaryUtil.ReadInt32(data, 100);
        // data[104] = lump_end

        if (numFrames < 0 || numTags < 0 || numMeshes < 0)
            throw new AssetParseException($"MD3 has negative counts (frames={numFrames}, tags={numTags}, meshes={numMeshes}).");

        Md3Frame[] frames = ReadFrames(data, lumpFrameInfo, numFrames);
        (var tags, var tagsByName) = ReadTags(data, lumpTags, numTags, numFrames);
        Md3Surface[] surfaces = ReadSurfaces(data, lumpMeshes, numMeshes, numFrames);

        return new Md3Data
        {
            Name = name,
            Flags = flags,
            FrameCount = numFrames,
            Frames = frames,
            Tags = tags,
            TagsByName = tagsByName,
            Surfaces = surfaces,
        };
    }

    private static Md3Frame[] ReadFrames(ReadOnlySpan<byte> data, int lumpOffset, int numFrames)
    {
        var outArr = new Md3Frame[numFrames];
        for (int i = 0; i < numFrames; i++)
        {
            int o = lumpOffset + i * FrameInfoSize;
            Vector3 mins = BinaryUtil.ReadVec3(data, o);
            Vector3 maxs = BinaryUtil.ReadVec3(data, o + 12);
            Vector3 origin = BinaryUtil.ReadVec3(data, o + 24);
            float radius = BinaryUtil.ReadFloat(data, o + 36);
            string frameName = BinaryUtil.ReadFixedString(data, o + 40, FrameNameLen);
            outArr[i] = new Md3Frame(mins, maxs, origin, radius, frameName);
        }
        return outArr;
    }

    private static (Md3Tag[] tags, IReadOnlyDictionary<string, Md3Tag> byName)
        ReadTags(ReadOnlySpan<byte> data, int lumpOffset, int numTags, int numFrames)
    {
        // Tags are laid out frame-major: tag t of frame f is at index (f * numTags + t).
        // We pivot to tag-major so each Md3Tag carries its transform for every frame.
        var transforms = new Md3TagTransform[numTags][];
        var names = new string[numTags];
        for (int t = 0; t < numTags; t++)
            transforms[t] = new Md3TagTransform[numFrames];

        for (int f = 0; f < numFrames; f++)
        {
            for (int t = 0; t < numTags; t++)
            {
                int o = lumpOffset + (f * numTags + t) * TagSize;
                string tagName = BinaryUtil.ReadFixedString(data, o, NameLen);
                Vector3 origin = BinaryUtil.ReadVec3(data, o + 64);
                // rotationmatrix[9] = three axis vectors (forward, left, up), 3 floats each.
                Vector3 axisX = BinaryUtil.ReadVec3(data, o + 76);
                Vector3 axisY = BinaryUtil.ReadVec3(data, o + 88);
                Vector3 axisZ = BinaryUtil.ReadVec3(data, o + 100);
                transforms[t][f] = new Md3TagTransform(origin, axisX, axisY, axisZ);
                // Tag name is constant across frames; capture it from frame 0.
                if (f == 0) names[t] = tagName;
            }
        }

        var tags = new Md3Tag[numTags];
        var byName = new Dictionary<string, Md3Tag>(StringComparer.Ordinal);
        for (int t = 0; t < numTags; t++)
        {
            tags[t] = new Md3Tag { Name = names[t], Transforms = transforms[t] };
            byName.TryAdd(names[t], tags[t]); // first occurrence wins on duplicate names
        }
        return (tags, byName);
    }

    private static Md3Surface[] ReadSurfaces(ReadOnlySpan<byte> data, int lumpOffset, int numMeshes, int modelFrames)
    {
        var outArr = new Md3Surface[numMeshes];
        int meshOffset = lumpOffset;
        for (int m = 0; m < numMeshes; m++)
        {
            // Each mesh sub-header begins with its own "IDP3" identifier (NO version field).
            string meshMagic = BinaryUtil.ReadMagic(data, meshOffset);
            if (meshMagic != Magic)
                throw new AssetParseException($"MD3 mesh {m} has invalid identifier \"{meshMagic}\" (expected \"{Magic}\").");

            string meshName = BinaryUtil.ReadFixedString(data, meshOffset + 4, NameLen);
            int flags = BinaryUtil.ReadInt32(data, meshOffset + 68);
            int numFrames = BinaryUtil.ReadInt32(data, meshOffset + 72);
            int numShaders = BinaryUtil.ReadInt32(data, meshOffset + 76);
            int numVertices = BinaryUtil.ReadInt32(data, meshOffset + 80);
            int numTriangles = BinaryUtil.ReadInt32(data, meshOffset + 84);
            int lumpElements = BinaryUtil.ReadInt32(data, meshOffset + 88);
            int lumpShaders = BinaryUtil.ReadInt32(data, meshOffset + 92);
            int lumpTexCoords = BinaryUtil.ReadInt32(data, meshOffset + 96);
            int lumpFrameVertices = BinaryUtil.ReadInt32(data, meshOffset + 100);
            int lumpEnd = BinaryUtil.ReadInt32(data, meshOffset + 104);

            if (numFrames != modelFrames)
                throw new AssetParseException(
                    $"MD3 mesh {m} frame count {numFrames} differs from model frame count {modelFrames}.");
            if (numShaders < 0 || numVertices < 0 || numTriangles < 0)
                throw new AssetParseException($"MD3 mesh {m} has negative counts.");
            if (lumpEnd <= 0)
                throw new AssetParseException($"MD3 mesh {m} has non-positive lump_end {lumpEnd}.");

            // All mesh lump_* offsets are relative to the start of this mesh struct.
            string[] shaders = ReadShaders(data, meshOffset + lumpShaders, numShaders);
            int[] triangles = ReadTriangles(data, meshOffset + lumpElements, numTriangles, numVertices);
            Vector2[] texCoords = ReadTexCoords(data, meshOffset + lumpTexCoords, numVertices);
            Md3Vertex[][] frameVerts = ReadFrameVertices(data, meshOffset + lumpFrameVertices, numFrames, numVertices);

            outArr[m] = new Md3Surface
            {
                Name = meshName,
                Flags = flags,
                Shaders = shaders,
                VertexCount = numVertices,
                Triangles = triangles,
                TexCoords = texCoords,
                FrameVertices = frameVerts,
            };

            // Advance to the next mesh (lump_end is relative to this mesh's start).
            long next = (long)meshOffset + lumpEnd;
            if (next <= meshOffset || next > data.Length)
                throw new AssetParseException($"MD3 mesh {m} lump_end points outside the file.");
            meshOffset = (int)next;
        }
        return outArr;
    }

    private static string[] ReadShaders(ReadOnlySpan<byte> data, int offset, int numShaders)
    {
        var outArr = new string[numShaders];
        for (int i = 0; i < numShaders; i++)
            outArr[i] = BinaryUtil.ReadFixedString(data, offset + i * ShaderSize, NameLen);
        return outArr;
    }

    private static int[] ReadTriangles(ReadOnlySpan<byte> data, int offset, int numTriangles, int numVertices)
    {
        var outArr = new int[numTriangles * 3];
        for (int i = 0; i < numTriangles; i++)
        {
            int o = offset + i * TriangleSize;
            for (int k = 0; k < 3; k++)
            {
                int idx = BinaryUtil.ReadInt32(data, o + k * 4);
                if (idx < 0 || idx >= numVertices)
                    throw new AssetParseException(
                        $"MD3 triangle index {idx} out of range (0..{numVertices - 1}).");
                outArr[i * 3 + k] = idx;
            }
        }
        return outArr;
    }

    private static Vector2[] ReadTexCoords(ReadOnlySpan<byte> data, int offset, int numVertices)
    {
        var outArr = new Vector2[numVertices];
        for (int i = 0; i < numVertices; i++)
            outArr[i] = BinaryUtil.ReadVec2(data, offset + i * TexCoordSize);
        return outArr;
    }

    private static Md3Vertex[][] ReadFrameVertices(ReadOnlySpan<byte> data, int offset, int numFrames, int numVertices)
    {
        // Stored as numFrames blocks of numVertices vertices each (frame-major).
        var outArr = new Md3Vertex[numFrames][];
        for (int f = 0; f < numFrames; f++)
        {
            var verts = new Md3Vertex[numVertices];
            int frameBase = offset + f * numVertices * VertexSize;
            for (int v = 0; v < numVertices; v++)
            {
                int o = frameBase + v * VertexSize;
                short x = BinaryUtil.ReadInt16(data, o);
                short y = BinaryUtil.ReadInt16(data, o + 2);
                short z = BinaryUtil.ReadInt16(data, o + 4);
                byte pitch = data[o + 6];
                byte yaw = data[o + 7];

                var position = new Vector3(
                    x * Md3Data.VertexScale,
                    y * Md3Data.VertexScale,
                    z * Md3Data.VertexScale);
                verts[v] = new Md3Vertex(position, DecodeNormal(pitch, yaw));
            }
            outArr[f] = verts;
        }
        return outArr;
    }

    /// <summary>
    /// Decodes the MD3 lat/long byte-quantized normal. Matches Darkplaces' <c>mod_md3_sin</c> table
    /// decode: normal = (cos(yaw)*sin(pitch), sin(yaw)*sin(pitch), cos(pitch)) where the angles are
    /// the byte values scaled by 2*pi/256.
    /// </summary>
    private static Vector3 DecodeNormal(byte pitch, byte yaw)
    {
        float lat = pitch * ByteAngleToRadians;
        float lng = yaw * ByteAngleToRadians;
        float sinLat = MathF.Sin(lat);
        return new Vector3(
            MathF.Cos(lng) * sinLat,
            MathF.Sin(lng) * sinLat,
            MathF.Cos(lat));
    }
}

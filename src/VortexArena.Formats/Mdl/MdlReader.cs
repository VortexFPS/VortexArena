using System;
using System.Buffers.Binary;
using System.Numerics;
using VortexArena.Formats.Images;

namespace VortexArena.Formats.Mdl;

/// <summary>
/// Parses a Quake1 MDL ("IDPO", <see cref="Version"/> 6) alias model from a raw byte buffer into
/// <see cref="MdlData"/>. Faithful to <c>Mod_IDP0_Load</c> / <c>Mod_MDL_LoadFrames</c> /
/// <c>Mod_Alias_CalculateBoundingBox</c> (Darkplaces <c>model_alias.c</c>) and the on-disk structs in
/// <c>modelgen.h</c>: a fixed 84-byte header, then N skins, then the shared texcoord (stvert) and triangle
/// tables, then N frames of byte-quantized vertices.
///
/// <para><b>Skins</b>: every skin picture is kept as raw palette indices (<see cref="MdlData.Skins"/>), with
/// the skin groups as <see cref="MdlData.SkinScenes"/>; the first is also decoded through the built-in Quake
/// palette to opaque RGBA (<see cref="MdlData.SkinRgba"/>). <b>Texcoords</b>: MDL stores one st per vertex
/// plus an "on seam" flag; a back-facing triangle's seam vertex uses U+0.5 (DP's
/// <c>vertonseam</c>/<c>facesfront</c> butcher). Both forms are produced: non-indexed corners with their
/// resolved UV, and DarkPlaces' compacted vertex set with an element array. <b>Normals</b>: each vertex
/// carries a byte index into the shared Quake vertex-normal table (<see cref="ByteNormals"/> = DP's
/// <c>m_bytenormals</c>), decoded to a unit normal. <b>Frames</b>: a frame group's poses are flattened into
/// <see cref="MdlData.Frames"/> and described by <see cref="MdlData.Scenes"/>.</para>
///
/// <para>All reads are bounds-checked; malformed input throws <see cref="AssetParseException"/>. Where
/// DarkPlaces is lenient this is too: bytes after the last frame are ignored, a triangle index outside the
/// vertex table draws vertex 0, a group interval below 0.01 becomes 0.1.</para>
/// </summary>
public static class MdlReader
{
    private const string Magic = "IDPO";
    public const int Version = 6;               // modelgen.h ALIAS_VERSION

    // aliasframetype_t / aliasskintype_t: 0 = single, non-zero = group (modelgen.h).
    private const int AliasSingle = 0;

    // On-disk struct sizes (bytes), little-endian. See modelgen.h.
    private const int HeaderSize = 84;          // mdl_t through `float size`
    private const int StVertSize = 12;          // onseam,s,t (3 * int)
    private const int TriangleSize = 16;        // facesfront + vertindex[3] (4 * int)
    private const int TriVertSize = 4;          // v[3] (byte) + lightnormalindex (byte)
    private const int FrameHeaderSize = 24;     // bboxmin(4) + bboxmax(4) + name[16]  (daliasframe_t)
    private const int FrameGroupHeaderSize = 12; // numframes(4) + bboxmin(4) + bboxmax(4)  (daliasgroup_t)
    private const int SkinGroupHeaderSize = 4;  // numskins(4)  (daliasskingroup_t)
    private const int Int32Size = 4;            // daliasskintype_t / aliasframetype_t / interval

    private const int FrameNameLen = 16;        // daliasframe_t name[16]
    private const int MaxDim = 65535;           // DP BOUNDI(VALUE,0,65536): VALUE >= 65536 is an error

    /// <summary>What DarkPlaces plays a single frame or single skin at, and a group whose interval is invalid.</summary>
    private const float DefaultInterval = 0.1f;

    public static MdlData Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(new ReadOnlySpan<byte>(data));
    }

    public static MdlData Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new AssetParseException($"MDL too small: {data.Length} bytes, need at least {HeaderSize} for the header.");

        string magic = BinaryUtil.ReadMagic(data, 0);
        if (magic != Magic)
            throw new AssetParseException($"Not an MDL file: magic is \"{magic}\", expected \"{Magic}\".");

        int version = BinaryUtil.ReadInt32(data, 4);
        if (version != Version)
            throw new AssetParseException($"MDL has wrong version {version} (expected {Version}).");

        // Header fields (offsets from mdl_t in modelgen.h).
        Vector3 scale = BinaryUtil.ReadVec3(data, 8);        // multiply byte verts by this...
        Vector3 origin = BinaryUtil.ReadVec3(data, 20);      // ...then add this (scale_origin/translate)
        int numSkins = BinaryUtil.ReadInt32(data, 48);
        int skinWidth = BinaryUtil.ReadInt32(data, 52);
        int skinHeight = BinaryUtil.ReadInt32(data, 56);
        int numVerts = BinaryUtil.ReadInt32(data, 60);
        int numTris = BinaryUtil.ReadInt32(data, 64);
        int numFrames = BinaryUtil.ReadInt32(data, 68);
        int syncType = BinaryUtil.ReadInt32(data, 72);
        int flags = BinaryUtil.ReadInt32(data, 76);

        Bound(numSkins, "numskins");
        Bound(skinWidth, "skinwidth");
        Bound(skinHeight, "skinheight");
        Bound(numVerts, "numverts");
        Bound(numTris, "numtris");
        Bound(numFrames, "numframes");
        if (syncType < 0 || syncType >= 2)
            throw new AssetParseException($"MDL synctype {syncType} is out of range (0..1).");
        if (numVerts == 0 || numTris == 0 || numFrames == 0)
            throw new AssetParseException($"MDL is empty (verts={numVerts}, tris={numTris}, frames={numFrames}).");

        int p = HeaderSize;

        // ── Skins: every picture, and one scene per header skin ─────────────────────────────────────────
        long skinTexels = (long)skinWidth * skinHeight;
        var skins = new System.Collections.Generic.List<byte[]>(numSkins);
        var skinScenes = new MdlScene[numSkins];
        for (int i = 0; i < numSkins; i++)
        {
            int skinType = BinaryUtil.ReadInt32(data, p);
            p += Int32Size;
            int groupSkins = 1;
            float interval = DefaultInterval;
            if (skinType != AliasSingle)
            {
                groupSkins = BinaryUtil.ReadInt32(data, p);
                p += SkinGroupHeaderSize;
                Bound(groupSkins, "skin group count");
                Need(data, p, (long)groupSkins * Int32Size, "skin intervals");
                // "interval = LittleFloat(pinskinintervals[0].interval)": the first one, for the whole group.
                if (groupSkins > 0)
                    interval = ValidInterval(BinaryUtil.ReadFloat(data, p));
                p += groupSkins * Int32Size;
            }
            skinScenes[i] = new MdlScene($"skin {i}", skins.Count, groupSkins, 1.0f / interval, true);
            for (int g = 0; g < groupSkins; g++)
            {
                Need(data, p, skinTexels, "skin pixels");
                skins.Add(data.Slice(p, (int)skinTexels).ToArray());
                p = AdvanceChecked(p, skinTexels, data.Length, "skin pixels");
            }
        }

        // ── Shared stverts (texcoord + seam flag) ────────────────────────────────────────────────────
        Need(data, p, (long)numVerts * StVertSize, "stverts");
        var onseam = new int[numVerts];
        // vertst: [0, numVerts) the vertex's own st, [numVerts, 2 * numVerts) its seam copy (s + 0.5).
        float scaleS = (float)(1.0 / skinWidth);        // "scales = 1.0 / skinwidth" (a float holding a double division)
        float scaleT = (float)(1.0 / skinHeight);
        var vertSt = new Vector2[numVerts * 2];
        for (int v = 0; v < numVerts; v++)
        {
            int o = p + v * StVertSize;
            onseam[v] = BinaryUtil.ReadInt32(data, o);
            float s = BinaryUtil.ReadInt32(data, o + 4) * scaleS;
            float t = BinaryUtil.ReadInt32(data, o + 8) * scaleT;
            vertSt[v] = new Vector2(s, t);
            vertSt[v + numVerts] = new Vector2((float)(s + 0.5), t);
        }
        p += numVerts * StVertSize;

        // ── Shared triangles: DarkPlaces' butchered, compacted element array, and plain corners ─────────
        Need(data, p, (long)numTris * TriangleSize, "triangles");
        var elements = new int[numTris * 3];
        for (int t = 0; t < numTris; t++)
        {
            int o = p + t * TriangleSize;
            bool backFace = BinaryUtil.ReadInt32(data, o) == 0;
            for (int j = 0; j < 3; j++)
            {
                int vi = BinaryUtil.ReadInt32(data, o + 4 + j * 4);
                // Mod_ValidateElements: an index outside the table is reported and drawn as the first vertex.
                if (vi < 0 || vi >= numVerts)
                    vi = 0;
                // "now butcher the elements according to vertonseam and tri->facesfront": a back-facing
                // triangle's on-seam vertex samples the far half of the skin (its copy, U + 0.5).
                if (backFace && onseam[vi] != 0)
                    vi += numVerts;
                elements[t * 3 + j] = vi;
            }
        }
        p += numTris * TriangleSize;

        // "count the usage ... build remapping table and compact array"
        var remap = new int[numVerts * 2];
        foreach (int e in elements)
            remap[e]++;
        int meshVertexCount = 0;
        for (int i = 0; i < remap.Length; i++)
            remap[i] = remap[i] != 0 ? meshVertexCount++ : -1;
        var meshVertices = new int[meshVertexCount];
        var meshTexCoords = new Vector2[meshVertexCount];
        for (int i = 0; i < remap.Length; i++)
        {
            if (remap[i] < 0)
                continue;
            meshVertices[remap[i]] = i < numVerts ? i : i - numVerts;
            meshTexCoords[remap[i]] = vertSt[i];
        }
        var corners = new MdlCorner[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            int e = elements[i];
            corners[i] = new MdlCorner(e < numVerts ? e : e - numVerts, vertSt[e]);
            elements[i] = remap[e];
        }

        // ── Frames: one scene per header frame; decode each pose's byte-quantized vertices + normals ────
        var frames = new System.Collections.Generic.List<MdlFrame>(numFrames);
        var scenes = new MdlScene[numFrames];
        for (int i = 0; i < numFrames; i++)
        {
            int frameType = BinaryUtil.ReadInt32(data, p);
            p += Int32Size;
            int groupFrames = 1;
            float interval = DefaultInterval;       // "a single frame is still treated as a group"
            if (frameType != AliasSingle)
            {
                // daliasgroup_t { numframes; bboxmin; bboxmax; } then groupFrames intervals (floats).
                groupFrames = BinaryUtil.ReadInt32(data, p);
                p += FrameGroupHeaderSize;
                Bound(groupFrames, "frame group count");
                Need(data, p, (long)groupFrames * Int32Size, "frame intervals");
                // "interval = LittleFloat (intervals->interval); // FIXME: support variable framerate groups"
                if (groupFrames > 0)
                    interval = ValidInterval(BinaryUtil.ReadFloat(data, p));
                p += groupFrames * Int32Size;
            }

            // "get scene name from first frame" - of whatever follows, even for an empty group.
            string sceneName = p + FrameHeaderSize <= data.Length ? BinaryUtil.ReadFixedString(data, p + 8, FrameNameLen) : string.Empty;
            scenes[i] = new MdlScene(sceneName, frames.Count, groupFrames, 1.0f / interval, true);

            for (int g = 0; g < groupFrames; g++)
            {
                // daliasframe_t { bboxmin; bboxmax; name[16]; } then numVerts trivertx_t.
                Need(data, p, FrameHeaderSize, "frame header");
                string name = BinaryUtil.ReadFixedString(data, p + 8, FrameNameLen);
                p += FrameHeaderSize;

                Need(data, p, (long)numVerts * TriVertSize, "frame vertices");
                var verts = new MdlVertex[numVerts];
                for (int v = 0; v < numVerts; v++)
                {
                    int o = p + v * TriVertSize;
                    var pos = new Vector3(
                        origin.X + data[o] * scale.X,
                        origin.Y + data[o + 1] * scale.Y,
                        origin.Z + data[o + 2] * scale.Z);
                    int ni = data[o + 3];
                    verts[v] = new MdlVertex(pos, ByteNormals[ni < ByteNormals.Length ? ni : 0]);
                }
                p += numVerts * TriVertSize;
                frames.Add(new MdlFrame { Name = name, Vertices = verts });
            }
        }
        if (frames.Count == 0)
            throw new AssetParseException("MDL has no poses (every frame is an empty group).");

        // ── Mod_Alias_CalculateBoundingBox: every used vertex of every pose ─────────────────────────────
        Vector3 mins = Vector3.Zero, maxs = Vector3.Zero;
        float yawRadius2 = 0, radius2 = 0;
        bool first = true, animated = false;
        MdlVertex[] reference = frames[0].Vertices;
        foreach (MdlFrame frame in frames)
        {
            MdlVertex[] verts = frame.Vertices;
            foreach (int source in meshVertices)
            {
                Vector3 v = verts[source].Position;
                if (first)
                {
                    first = false;
                    mins = maxs = v;
                }
                else
                {
                    mins = Vector3.Min(mins, v);
                    maxs = Vector3.Max(maxs, v);
                }
                float dist = v.X * v.X + v.Y * v.Y;
                if (yawRadius2 < dist)
                    yawRadius2 = dist;
                dist += v.Z * v.Z;
                if (radius2 < dist)
                    radius2 = dist;
                if (!animated && v != reference[source].Position)
                    animated = true;
            }
        }

        return new MdlData
        {
            Name = frames[0].Name,
            SkinWidth = skinWidth,
            SkinHeight = skinHeight,
            SkinRgba = skins.Count > 0 && skinTexels > 0 ? QuakePalette.Default.ToRgba(skins[0]) : Array.Empty<byte>(),
            Skins = skins.ToArray(),
            SkinScenes = skinScenes,
            Flags = flags,
            SyncType = syncType,
            VertexCount = numVerts,
            Corners = corners,
            MeshElements = elements,
            MeshVertices = meshVertices,
            MeshTexCoords = meshTexCoords,
            Frames = frames.ToArray(),
            Scenes = scenes,
            Mins = mins,
            Maxs = maxs,
            YawRadius = MathF.Sqrt(yawRadius2),
            Radius = MathF.Sqrt(radius2),
            IsAnimated = animated,
        };
    }

    /// <summary>"has an invalid interval %f, changing to 0.1" (a NaN is invalid too).</summary>
    private static float ValidInterval(float interval) => interval >= 0.01f ? interval : DefaultInterval;

    private static void Bound(int value, string what)
    {
        if (value < 0 || value > MaxDim)
            throw new AssetParseException($"MDL {what} {value} is out of range (0..{MaxDim}).");
    }

    /// <summary>Bounds-check a variable-length block (mirrors BinaryUtil's private Require for byte blocks).</summary>
    private static void Need(ReadOnlySpan<byte> data, int offset, long count, string what)
    {
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
            throw new AssetParseException(
                $"Truncated MDL reading {what}: need {count} byte(s) at offset {offset}, buffer is {data.Length} byte(s).");
    }

    /// <summary>Advance <paramref name="offset"/> by <paramref name="count"/>, throwing if it runs past the buffer.</summary>
    private static int AdvanceChecked(int offset, long count, int length, string what)
    {
        long next = (long)offset + count;
        if (count < 0 || next > length)
            throw new AssetParseException(
                $"Truncated MDL skipping {what}: {count} byte(s) at offset {offset} overruns the {length}-byte buffer.");
        return (int)next;
    }

    // ── Embedded Quake constants (sourced verbatim from Darkplaces) ───────────────────────────────────

    /// <summary>
    /// The 162-entry Quake vertex-normal table — DP's <c>m_bytenormals</c> (<c>mathlib.c</c>), stored here as
    /// little-endian float triples. A frame vertex's <c>lightnormalindex</c> byte selects one of these.
    /// </summary>
    private static readonly Vector3[] ByteNormals = DecodeNormals(
        "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");

    private static Vector3[] DecodeNormals(string base64)
    {
        byte[] raw = Convert.FromBase64String(base64);
        int n = raw.Length / 12;
        var outArr = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 12;
            outArr[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(o, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(o + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(o + 8, 4)));
        }
        return outArr;
    }
}

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace XonoticGodot.Formats.Mdl;

/// <summary>
/// Parses a Quake1 MDL ("IDPO", <see cref="Version"/> 6) alias model from a raw byte buffer into
/// <see cref="MdlData"/>. Faithful to <c>Mod_IDP0_Load</c> (Darkplaces <c>model_alias.c</c>) and the on-disk
/// structs in <c>modelgen.h</c>: a fixed 84-byte header, then N skins, then the shared texcoord (stvert) and
/// triangle tables, then N frames of byte-quantized vertices.
///
/// <para><b>Skin</b>: the first skin is decoded from 8-bit palette indices straight through the Quake palette
/// (<see cref="QuakePalette"/> = DP's <c>host_quakepal</c> fallback, which is what applies because Xonotic
/// ships no external <c>gfx/palette.lmp</c>) to opaque RGBA — the single-texture equivalent of DP's
/// <c>R_SkinFrame_LoadInternalQuake</c>. <b>Texcoords</b>: MDL stores one st per vertex plus an "on seam"
/// flag; a back-facing triangle's seam vertex uses U+0.5 (DP's <c>vertonseam</c>/<c>facesfront</c> butcher),
/// so we expand triangles to non-indexed corners with their resolved UV rather than compact a vertex set.
/// <b>Normals</b>: each vertex carries a byte index into the shared Quake vertex-normal table
/// (<see cref="ByteNormals"/> = DP's <c>m_bytenormals</c>), decoded to a unit normal.</para>
///
/// <para>Group skins/frames (the animated variants) are parsed to walk the file correctly and each group
/// sub-frame becomes a plain frame; the shipped models that reach this loader are all single-frame. All reads
/// are bounds-checked; malformed input throws <see cref="AssetParseException"/>.</para>
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
    private const int MaxDim = 1 << 16;         // DP BOUNDI(...,0,65536) guard on every count

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

        Bound(numSkins, "numskins");
        Bound(skinWidth, "skinwidth");
        Bound(skinHeight, "skinheight");
        Bound(numVerts, "numverts");
        Bound(numTris, "numtris");
        Bound(numFrames, "numframes");
        if (numVerts == 0 || numTris == 0 || numFrames == 0)
            throw new AssetParseException($"MDL is empty (verts={numVerts}, tris={numTris}, frames={numFrames}).");

        int p = HeaderSize;

        // ── Skins: walk every skin so the offset lands on the stverts; keep the first skin's pixels ──────
        long skinTexels = (long)skinWidth * skinHeight;
        byte[]? firstSkinIndices = null;
        for (int i = 0; i < numSkins; i++)
        {
            int skinType = BinaryUtil.ReadInt32(data, p);
            p += Int32Size;
            int groupSkins = 1;
            if (skinType != AliasSingle)
            {
                groupSkins = BinaryUtil.ReadInt32(data, p);
                p += SkinGroupHeaderSize;
                Bound(groupSkins, "skin group count");
                p = AdvanceChecked(p, (long)groupSkins * Int32Size, data.Length, "skin intervals"); // floats
            }
            for (int g = 0; g < groupSkins; g++)
            {
                Need(data, p, skinTexels, "skin pixels");
                if (firstSkinIndices is null && skinTexels > 0)
                    firstSkinIndices = data.Slice(p, (int)skinTexels).ToArray();
                p = AdvanceChecked(p, skinTexels, data.Length, "skin pixels");
            }
        }

        // ── Shared stverts (texcoord + seam flag) ────────────────────────────────────────────────────
        Need(data, p, (long)numVerts * StVertSize, "stverts");
        var onseam = new int[numVerts];
        var stS = new int[numVerts];
        var stT = new int[numVerts];
        for (int v = 0; v < numVerts; v++)
        {
            int o = p + v * StVertSize;
            onseam[v] = BinaryUtil.ReadInt32(data, o);
            stS[v] = BinaryUtil.ReadInt32(data, o + 4);
            stT[v] = BinaryUtil.ReadInt32(data, o + 8);
        }
        p += numVerts * StVertSize;

        // ── Shared triangles → expand to seam-resolved, non-indexed render corners ──────────────────────
        Need(data, p, (long)numTris * TriangleSize, "triangles");
        float invW = skinWidth > 0 ? 1f / skinWidth : 0f;
        float invH = skinHeight > 0 ? 1f / skinHeight : 0f;
        var corners = new MdlCorner[numTris * 3];
        for (int t = 0; t < numTris; t++)
        {
            int o = p + t * TriangleSize;
            int facesFront = BinaryUtil.ReadInt32(data, o);
            for (int j = 0; j < 3; j++)
            {
                int vi = BinaryUtil.ReadInt32(data, o + 4 + j * 4);
                if (vi < 0 || vi >= numVerts)
                    throw new AssetParseException($"MDL triangle index {vi} out of range (0..{numVerts - 1}).");
                // DP: a back-facing triangle's on-seam vertex samples the far half of the skin (U += 0.5).
                float u = stS[vi] * invW;
                if (facesFront == 0 && onseam[vi] != 0)
                    u += 0.5f;
                corners[t * 3 + j] = new MdlCorner(vi, new Vector2(u, stT[vi] * invH));
            }
        }
        p += numTris * TriangleSize;

        // ── Frames: decode each pose's byte-quantized vertices + anorms normals ─────────────────────────
        var frames = new System.Collections.Generic.List<MdlFrame>(numFrames);
        for (int i = 0; i < numFrames; i++)
        {
            int frameType = BinaryUtil.ReadInt32(data, p);
            p += Int32Size;
            int groupFrames = 1;
            if (frameType != AliasSingle)
            {
                // daliasgroup_t { numframes; bboxmin; bboxmax; } then groupFrames intervals (floats).
                groupFrames = BinaryUtil.ReadInt32(data, p);
                p += FrameGroupHeaderSize;
                Bound(groupFrames, "frame group count");
                p = AdvanceChecked(p, (long)groupFrames * Int32Size, data.Length, "frame intervals");
            }
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
                        data[o] * scale.X + origin.X,
                        data[o + 1] * scale.Y + origin.Y,
                        data[o + 2] * scale.Z + origin.Z);
                    int ni = data[o + 3];
                    verts[v] = new MdlVertex(pos, ByteNormals[ni < ByteNormals.Length ? ni : 0]);
                }
                p += numVerts * TriVertSize;
                frames.Add(new MdlFrame { Name = name, Vertices = verts });
            }
        }

        return new MdlData
        {
            Name = frames[0].Name,
            SkinWidth = skinWidth,
            SkinHeight = skinHeight,
            SkinRgba = DecodeSkin(firstSkinIndices, skinWidth, skinHeight),
            VertexCount = numVerts,
            Corners = corners,
            Frames = frames.ToArray(),
        };
    }

    /// <summary>Expand 8-bit palette indices to opaque RGBA8 via the Quake palette; empty when no skin.</summary>
    private static byte[] DecodeSkin(byte[]? indices, int width, int height)
    {
        if (indices is null || width <= 0 || height <= 0)
            return Array.Empty<byte>();
        int count = width * height;
        var rgba = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            int c = indices[i] * 3;
            rgba[i * 4 + 0] = QuakePalette[c];
            rgba[i * 4 + 1] = QuakePalette[c + 1];
            rgba[i * 4 + 2] = QuakePalette[c + 2];
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

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

    // ── Embedded Quake constants (public domain; sourced verbatim from Darkplaces) ──────────────────────

    /// <summary>
    /// The Quake palette (256 RGB triples, 768 bytes) — DP's <c>host_quakepal</c> from <c>palette.c</c>, the
    /// fallback used when no external <c>gfx/palette.lmp</c> is mounted (Xonotic ships none, so this is what
    /// MDL skins decode through). id/Carmack placed this palette in the public domain.
    /// </summary>
    private static readonly byte[] QuakePalette = Convert.FromBase64String(
        "AAAADw8PHx8fLy8vPz8/S0tLW1tba2tre3t7i4uLm5ubq6uru7u7y8vL29vb6+vrDwsHFw8LHxcLJxsPLyMTNysXPy8XSzcbUzsbW0MfY0sfa1Mfc1cfe18jg2cjj28jCwsPExMbGxsnJyczLy8/NzdLPz9XR0dnT09zW1t/Y2OLa2uXc3Oje3uvg4O7i4vLAAAABwcACwsAExMAGxsAIyMAKysHLy8HNzcHPz8HR0cHS0sLU1MLW1sLY2MLa2sPBwAADwAAFwAAHwAAJwAALwAANwAAPwAARwAATwAAVwAAXwAAZwAAbwAAdwAAfwAAExMAGxsAIyMALysANy8AQzcASzsHV0MHX0cHa0sLd1MPg1cTi1sTl18bo2Mfr2cjIxMHLxcLOx8PSyMTVysXYy8fczcjfzsrj0Mzn08zr2Mvv3cvz48r36sn78sf//MbCwcAGxMAKyMPNysTRzMbUzcjYz8rb0czf1M/i19Hm2tTp3tft4drw5N706OL47OXq4ujn3+Xk3OHi2d7f1tvd1Nja0tXXz9LVzdDSy83QycvNx8jKxcbIxMTFwsLDwcHu3Ofr2uPo1+Dl1d3i09rf0tfc0NTaztLXzM/Uys3RyMrOx8jLxcbIxMTFwsLDwcH28O7y7Onv6Obr5eLo4d7l3tvh29fe2NTa1dHX0s7Uz8zQzMnNysfJx8XGxMPDwsHb4N7Z3tvX3NnV2tfT2NXR1tPP1NHN0s/L0M3KzsvIzMnHysfFyMXDxsTCxMLBwsH//Mb798X28sTy7cPu6cPq5cLm4MHi3MHe2MHa1MAW0cASzcAOysAKx8AGw8ACwcAAAD/CwvvExPfGxvPIyO/KyuvLy+fLy+PLy9/Ly9vLy9fKytPIyM/GxsvExMfCwsPKwAAOwAASwcAXwcAbw8AfxcHkx8HoycLtzMPw0sbz2Mr238745dP56tf779399OLp3s7t5s3x8M35+NXf7//q+f/1///ZwAAiwAAswAA1wAA/wAA//OT//fH////n1tT");

    /// <summary>
    /// The 162-entry Quake vertex-normal table — DP's <c>m_bytenormals</c> (<c>mathlib.c</c>), stored here as
    /// little-endian float triples. A frame vertex's <c>lightnormalindex</c> byte selects one of these.
    /// </summary>
    private static readonly Vector3[] ByteNormals = DecodeNormals(
        "T5YGvwAAAABExFk/8L7ivquWdD5tO10/9imXvgAAAACalnQ/ejeevgAAAD+9G08/6lsmvl+Whj5oeHM/AAAAAAAAAAAAAIA/AAAAAETEWT9PlgY/9ikXvu9wNz8ShS4/9ikXPu9wNz8ShS4/AAAAAE+WBj9ExFk/ejeePgAAAD+9G08/T5YGPwAAAABExFk/9imXPgAAAACalnQ/8L7iPquWdD5tO10/6lsmPl+Whj5oeHM/EoUuv/YpFz7vcDc/vRtPv3o3nj4AAAA/FHkWvzPE2T5JLTA/RMRZv0+WBj8AAAAAbTtdv/C+4j6rlnQ+73A3vxKFLj/2KRc+SS0wvxR5Fj8zxNk+AAAAv70bTz96N54+q5Z0vm07XT/wvuI+M8TZvkktMD8UeRY/73A3vxKFLj/2KRe+AAAAv70bTz96N56+T5YGv0TEWT8AAAAAAAAAAETEWT9Plga/q5Z0vm07XT/wvuK+AAAAAJqWdD/2KZe+X5aGvmh4cz/qWya+AAAAAAAAgD8AAAAAAAAAAJqWdD/2KZc+X5aGvmh4cz/qWyY+q5Z0Pm07XT/wvuI+X5aGPmh4cz/qWyY+AAAAP70bTz96N54+q5Z0Pm07XT/wvuK+X5aGPmh4cz/qWya+AAAAP70bTz96N56+RMRZP0+WBj8AAAAA73A3PxKFLj/2KRc+73A3PxKFLj/2KRe+T5YGP0TEWT8AAAAAM8TZPkktMD8UeRY/bTtdP/C+4j6rlnQ+SS0wPxR5Fj8zxNk+vRtPP3o3nj4AAAA/EoUuP/YpFz7vcDc/FHkWPzPE2T5JLTA/mpZ0P/Yplz4AAAAAAACAPwAAAAAAAAAAaHhzP+pbJj5floY+RMRZP0+WBr8AAAAAmpZ0P/Ypl74AAAAAbTtdP/C+4r6rlnQ+aHhzP+pbJr5floY+vRtPP3o3nr4AAAA/EoUuP/YpF77vcDc/RMRZPwAAAABPlgY/bTtdP/C+4j6rlnS+vRtPP3o3nj4AAAC/aHhzP+pbJj5floa+T5YGPwAAAABExFm/EoUuP/YpFz7vcDe/EoUuP/YpF77vcDe/RMRZPwAAAABPlga/vRtPP3o3nr4AAAC/bTtdP/C+4r6rlnS+aHhzP+pbJr5floa+9ikXPu9wNz8ShS6/ejeePgAAAD+9G0+/M8TZPkktMD8UeRa/8L7iPquWdD5tO12/FHkWPzPE2T5JLTC/SS0wPxR5Fj8zxNm+9ikXvu9wNz8ShS6/ejeevgAAAD+9G0+/AAAAAE+WBj9ExFm/T5YGvwAAAABExFm/8L7ivquWdD5tO12/9imXvgAAAACalnS/6lsmvl+Whj5oeHO/AAAAAAAAAAAAAIC/9imXPgAAAACalnS/6lsmPl+Whj5oeHO/8L7ivquWdL5tO12/ejeevgAAAL+9G0+/6lsmvl+Whr5oeHO/AAAAAETEWb9Plga/9ikXvu9wN78ShS6/9ikXPu9wN78ShS6/AAAAAE+WBr9ExFm/ejeePgAAAL+9G0+/8L7iPquWdL5tO12/6lsmPl+Whr5oeHO/q5Z0Pm07Xb/wvuK+AAAAP70bT796N56+M8TZPkktML8UeRa/73A3PxKFLr/2KRe+SS0wPxR5Fr8zxNm+FHkWPzPE2b5JLTC/AAAAAJqWdL/2KZe+AAAAAAAAgL8AAAAAX5aGPmh4c7/qWya+AAAAAETEWb9PlgY/AAAAAJqWdL/2KZc+q5Z0Pm07Xb/wvuI+X5aGPmh4c7/qWyY+AAAAP70bT796N54+73A3PxKFLr/2KRc+T5YGP0TEWb8AAAAAq5Z0vm07Xb/wvuK+AAAAv70bT796N56+X5aGvmh4c7/qWya+RMRZv0+WBr8AAAAA73A3vxKFLr/2KRe+73A3vxKFLr/2KRc+T5YGv0TEWb8AAAAAAAAAv70bT796N54+q5Z0vm07Xb/wvuI+X5aGvmh4c7/qWyY+bTtdv/C+4r6rlnQ+vRtPv3o3nr4AAAA/SS0wvxR5Fr8zxNk+EoUuv/YpF77vcDc/8L7ivquWdL5tO10/FHkWvzPE2b5JLTA/ejeevgAAAL+9G08/9ikXvu9wN78ShS4/M8TZvkktML8UeRY/6lsmvl+Whr5oeHM/8L7iPquWdL5tO10/6lsmPl+Whr5oeHM/ejeePgAAAL+9G08/9ikXPu9wN78ShS4/AAAAAE+WBr9ExFk/M8TZPkktML8UeRY/FHkWPzPE2b5JLTA/SS0wPxR5Fr8zxNk+mpZ0v/Yplz4AAAAAaHhzv+pbJj5floY+AACAvwAAAAAAAAAARMRZvwAAAABPlgY/mpZ0v/Ypl74AAAAAaHhzv+pbJr5floY+bTtdv/C+4j6rlnS+aHhzv+pbJj5floa+vRtPv3o3nj4AAAC/bTtdv/C+4r6rlnS+aHhzv+pbJr5floa+vRtPv3o3nr4AAAC/EoUuv/YpFz7vcDe/EoUuv/YpF77vcDe/RMRZvwAAAABPlga/SS0wvxR5Fj8zxNm+FHkWvzPE2T5JLTC/M8TZvkktMD8UeRa/M8TZvkktML8UeRa/FHkWvzPE2b5JLTC/SS0wvxR5Fr8zxNm+");

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

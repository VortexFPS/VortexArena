using System;
using System.Buffers.Binary;

namespace VortexArena.Formats.Images;

/// <summary>
/// Writes a block-compressed DDS file — the other half of <c>DdsDecoder</c>, and the thing that lets this port
/// stop re-compressing the same textures on every launch.
///
/// <para><b>Why a writer at all.</b> DarkPlaces never runs a texture encoder: it hands OpenGL uncompressed
/// pixels with a <c>GL_COMPRESSED_*</c> internal format and the driver compresses during upload, for free.
/// Vulkan deliberately removed that — a BC-format image must be fed already-compressed blocks — so a Godot 4
/// port has to encode itself, and encoding ~290 textures costs 105 s per launch at BC7. DarkPlaces' own answer
/// to that is <c>r_texture_dds_save</c>: compress once, write <c>dds/&lt;name&gt;.dds</c>, and load that
/// instead next time. This is that file format.</para>
///
/// <para>The output is deliberately the same shape Xonotic already ships (3,207 files under <c>dds/</c> in the
/// stock maps pack), so a cache this writes and a texture Xonotic shipped are read by the identical path.</para>
/// </summary>
public static class DdsWriter
{
    private const uint Magic = 0x20534444;      // "DDS "
    private const uint HeaderSize = 124;
    private const uint PfSize = 32;

    // DDSD_ flags: CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT | LINEARSIZE
    private const uint HeaderFlags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000;
    private const uint PfFourCc = 0x4;          // DDPF_FOURCC
    private const uint CapsTexture = 0x1000;
    private const uint CapsMipmap = 0x400000;
    private const uint CapsComplex = 0x8;

    /// <summary>FourCC for the classic S3TC families; <see cref="Dx10"/> for everything newer.</summary>
    public const string FourCcDxt1 = "DXT1";
    public const string FourCcDxt3 = "DXT3";
    public const string FourCcDxt5 = "DXT5";
    public const string FourCcBc4 = "ATI1";
    public const string FourCcBc5 = "ATI2";

    /// <summary>Sentinel FourCC that says "a DDS_HEADER_DXT10 follows" — how BC7/BC6H are expressed.</summary>
    public const string Dx10 = "DX10";

    /// <summary>DXGI_FORMAT_BC7_UNORM.</summary>
    public const uint DxgiBc7Unorm = 98;

    /// <summary>DXGI_FORMAT_BC6H_UF16.</summary>
    public const uint DxgiBc6hUf16 = 95;

    /// <summary>
    /// Build a complete DDS file for pre-compressed block data.
    /// </summary>
    /// <param name="width">Level-0 width in pixels.</param>
    /// <param name="height">Level-0 height in pixels.</param>
    /// <param name="mipCount">Number of mip levels present in <paramref name="blocks"/> (at least 1).</param>
    /// <param name="fourCc">One of the FourCC constants, or <see cref="Dx10"/>.</param>
    /// <param name="dxgiFormat">Only read when <paramref name="fourCc"/> is <see cref="Dx10"/>.</param>
    /// <param name="blocks">All mip levels concatenated, largest first — Godot's <c>Image.GetData()</c> layout.</param>
    /// <param name="blockBytes">Bytes per 4×4 block: 8 for BC1/BC4, 16 for BC2/BC3/BC5/BC6H/BC7.</param>
    public static byte[] Write(int width, int height, int mipCount, string fourCc, uint dxgiFormat,
                               ReadOnlySpan<byte> blocks, int blockBytes)
    {
        if (width <= 0 || height <= 0 || mipCount < 1 || fourCc.Length != 4)
            throw new ArgumentException("invalid DDS parameters");

        bool dx10 = fourCc == Dx10;
        int headerBytes = 128 + (dx10 ? 20 : 0);
        var outp = new byte[headerBytes + blocks.Length];
        Span<byte> s = outp;

        BinaryPrimitives.WriteUInt32LittleEndian(s[0..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], HeaderFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)width);
        // pitchOrLinearSize for a block format is the byte size of level 0.
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)(BlocksFor(width, height) * blockBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], 0);              // depth
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)mipCount);
        // 32..75 reserved1[11] stays zero.

        // DDS_PIXELFORMAT at offset 76.
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], PfSize);
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], PfFourCc);
        s[84] = (byte)fourCc[0]; s[85] = (byte)fourCc[1]; s[86] = (byte)fourCc[2]; s[87] = (byte)fourCc[3];
        // 88..107: RGB bit counts/masks, all zero for a FourCC format.

        uint caps = CapsTexture | (mipCount > 1 ? CapsMipmap | CapsComplex : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[108..], caps);
        // 112..127: caps2/3/4 + reserved2 stay zero.

        if (dx10)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[128..], dxgiFormat);
            BinaryPrimitives.WriteUInt32LittleEndian(s[132..], 3);   // D3D10_RESOURCE_DIMENSION_TEXTURE2D
            BinaryPrimitives.WriteUInt32LittleEndian(s[136..], 0);   // miscFlag
            BinaryPrimitives.WriteUInt32LittleEndian(s[140..], 1);   // arraySize
            BinaryPrimitives.WriteUInt32LittleEndian(s[144..], 0);   // miscFlags2
        }

        blocks.CopyTo(s[headerBytes..]);
        return outp;
    }

    /// <summary>4×4 block count for a mip level, rounding up like every BC format does.</summary>
    private static int BlocksFor(int w, int h) => ((w + 3) / 4) * ((h + 3) / 4);
}

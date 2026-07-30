using Godot;

namespace VortexArena.Game.Loaders;

/// <summary>
/// A self-contained DirectDraw Surface (.dds) decoder producing a Godot <see cref="Image"/> in
/// <see cref="Image.Format.Rgba8"/> (top mip level only).
///
/// Xonotic ships GPU-precompressed textures under a parallel <c>dds/</c> tree (e.g.
/// <c>dds/textures/map_stormkeep/largebrick1a.dds</c>). For maps such as stormkeep the <c>.dds</c> is the ONLY
/// variant present, so without this the world surfaces fall back to the missing-texture magenta. Godot's
/// scripting API has no DDS-from-buffer loader, so — exactly as the asset pipeline already does for TGA — we
/// decode it ourselves.
///
/// Covered (the formats Xonotic actually ships):
/// <list type="bullet">
///   <item>DXT1 / BC1 — RGB with optional 1-bit alpha (FourCC <c>"DXT1"</c>)</item>
///   <item>DXT3 / BC2 — explicit 4-bit alpha (FourCC <c>"DXT3"</c>)</item>
///   <item>DXT5 / BC3 — interpolated alpha (FourCC <c>"DXT5"</c>)</item>
///   <item>uncompressed RGB/RGBA, 24/32-bit, via the pixel-format channel masks</item>
/// </list>
/// DDS rows are stored top-down (row 0 = top), matching Godot, so unlike TGA no vertical flip is needed.
/// Returns null on a malformed/unsupported header (including the DX10 extended header and BC4/BC5, which
/// Xonotic's world textures don't use) rather than throwing, so a bad asset is skipped, not fatal.
/// </summary>
internal static class DdsDecoder
{
    // DDS_PIXELFORMAT.dwFlags
    private const uint DDPF_ALPHAPIXELS = 0x1;
    private const uint DDPF_FOURCC      = 0x4;
    private const uint DDPF_RGB         = 0x40;

    private enum BcKind { Dxt1, Dxt3, Dxt5 }

    /// <summary>
    /// Decode <paramref name="data"/> (a full .dds file) into a Godot <see cref="Image"/>, or null if the
    /// header is malformed or the surface format is unsupported. Back-compat entry — see the length overload.
    /// </summary>
    public static Image? Decode(byte[] data) => Decode(data, data?.Length ?? 0);

    /// <summary>
    /// Length-taking overload (perf 2026-07-03) so the caller can hand in a POOLED, possibly-oversized file
    /// buffer: only the first <paramref name="length"/> bytes are the file.
    ///
    /// <para><b>S3TC pass-through (perf 2026-07-03):</b> when the file is classic DXT1/3/5 with a full mip
    /// chain, the compressed payload is handed to Godot AS-IS (<see cref="Image.Format.Dxt1"/>/Dxt3/Dxt5) —
    /// no CPU block-decode, no CPU mip regeneration (the file already ships the chain the old path threw away),
    /// and the GPU stores the texture compressed (~4-6× less VRAM + upload) — which is exactly what DarkPlaces
    /// does with these files. Files without a full chain (or non-S3TC) keep the RGBA8 decode path.</para>
    /// </summary>
    public static Image? Decode(byte[] data, int length)
    {
        // "DDS " magic (4) + 124-byte DDS_HEADER = 128 bytes before the pixel data.
        if (data == null || length < 128)
            return null;
        if (data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ')
            return null;
        if (U32(data, 4) != 124) // DDS_HEADER.dwSize
            return null;

        uint hdrFlags = U32(data, 8);      // DDS_HEADER.dwFlags (DDSD_*)
        int height = (int)U32(data, 12);
        int width  = (int)U32(data, 16);
        if (width <= 0 || height <= 0 || width > 1 << 14 || height > 1 << 14)
            return null;
        // DDSD_MIPMAPCOUNT (0x20000) gates dwMipMapCount@28; absent → a single level.
        int mipCount = (hdrFlags & 0x20000) != 0 ? (int)U32(data, 28) : 1;

        uint pfFlags     = U32(data, 80);
        uint fourCc      = U32(data, 84);
        int  rgbBitCount = (int)U32(data, 88);
        uint rMask       = U32(data, 92);
        uint gMask       = U32(data, 96);
        uint bMask       = U32(data, 100);
        uint aMask       = U32(data, 104);

        const int dataOffset = 128; // no DX10 extended header (rejected below)

        if ((pfFlags & DDPF_FOURCC) != 0)
        {
            // FourCC "DX10" carries a 20-byte extended header we don't parse (Xonotic ships classic S3TC).
            BcKind kind;
            int blockBytes;
            Image.Format gpuFormat;
            if (fourCc == FourCc('D', 'X', 'T', '1')) { kind = BcKind.Dxt1; blockBytes = 8;  gpuFormat = Image.Format.Dxt1; }
            else if (fourCc == FourCc('D', 'X', 'T', '3')) { kind = BcKind.Dxt3; blockBytes = 16; gpuFormat = Image.Format.Dxt3; }
            else if (fourCc == FourCc('D', 'X', 'T', '5')) { kind = BcKind.Dxt5; blockBytes = 16; gpuFormat = Image.Format.Dxt5; }
            else return null; // DX10 / BC4 / BC5 / etc. — not used by Xonotic world textures

            // ---- pass-through: full mip chain present → give Godot the compressed payload verbatim ----------
            (int chainLevels, long chainBytes) = FullChainSize(width, height, blockBytes);
            if (mipCount >= chainLevels && dataOffset + chainBytes <= length && chainBytes <= int.MaxValue)
            {
                // CreateFromData copies synchronously, so the exact-size pooled slice returns to the pool here.
                byte[] slice = RgbaDecodeBuffer.Rent((int)chainBytes, clear: false);
                try
                {
                    System.Array.Copy(data, dataOffset, slice, 0, (int)chainBytes);
                    return Image.CreateFromData(width, height, true, gpuFormat, slice);
                }
                catch (System.Exception)
                {
                    // Unexpected layout (Godot's expected chain size disagreed) — fall through to the CPU decode.
                }
                finally
                {
                    RgbaDecodeBuffer.Return(slice);
                }
            }

            // Pooled shared scratch (the block decode writes every pixel; CreateFromData copies it out below,
            // so the finally can return it). Collapses the per-texture decode-burst allocation (§12.6b) — see
            // RgbaDecodeBuffer.
            byte[] outRgba = RgbaDecodeBuffer.Rent(width * height * 4);
            try
            {
                if (!DecodeBc(data, length, dataOffset, width, height, blockBytes, kind, outRgba))
                    return null;
                return Image.CreateFromData(width, height, false, Image.Format.Rgba8, outRgba);
            }
            finally
            {
                RgbaDecodeBuffer.Return(outRgba);
            }
        }

        if ((pfFlags & DDPF_RGB) != 0)
        {
            byte[] outRgba = RgbaDecodeBuffer.Rent(width * height * 4);
            try
            {
                uint usedAlpha = (pfFlags & DDPF_ALPHAPIXELS) != 0 ? aMask : 0u;
                if (!DecodeUncompressed(data, length, dataOffset, width, height, rgbBitCount, rMask, gMask, bMask, usedAlpha, outRgba))
                    return null;
                return Image.CreateFromData(width, height, false, Image.Format.Rgba8, outRgba);
            }
            finally
            {
                RgbaDecodeBuffer.Return(outRgba);
            }
        }

        return null; // luminance / YUV / other — not shipped for world textures
    }

    /// <summary>The level count + total byte size of a FULL block-compressed mip chain (down to 1×1) — the
    /// layout Godot expects for CreateFromData(mipmaps: true), which matches the standard DDS packing.</summary>
    private static (int Levels, long Bytes) FullChainSize(int width, int height, int blockBytes)
    {
        int w = width, h = height, levels = 0;
        long bytes = 0;
        while (true)
        {
            bytes += (long)((w + 3) / 4) * ((h + 3) / 4) * blockBytes;
            levels++;
            if (w == 1 && h == 1)
                break;
            w = System.Math.Max(1, w >> 1);
            h = System.Math.Max(1, h >> 1);
        }
        return (levels, bytes);
    }

    /// <summary>
    /// Decode a block-compressed surface (BC1/BC2/BC3). Each 4×4 block is <paramref name="blockBytes"/> bytes;
    /// for BC2/BC3 the first 8 bytes are the alpha block and the next 8 the BC1-style colour block.
    /// </summary>
    private static bool DecodeBc(byte[] data, int dataLength, int offset, int width, int height, int blockBytes, BcKind kind, byte[] outRgba)
    {
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        if (offset + (long)blocksX * blocksY * blockBytes > dataLength)
            return false;

        // 4-entry colour palette (RGBA8) rebuilt per block; 8-entry alpha ramp for DXT5.
        var colors = new byte[16];
        var alphas = new byte[8];

        int p = offset;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int alphaBlock = p;
                int colorBlock = p + (blockBytes == 16 ? 8 : 0);

                int c0 = data[colorBlock] | (data[colorBlock + 1] << 8);
                int c1 = data[colorBlock + 2] | (data[colorBlock + 3] << 8);
                Rgb565(c0, out byte r0, out byte g0, out byte b0);
                Rgb565(c1, out byte r1, out byte g1, out byte b1);

                // BC1 with c0<=c1 is the 3-colour + 1-bit-alpha mode; BC2/BC3 always use the 4-colour ramp.
                bool oneBitAlpha = kind == BcKind.Dxt1 && c0 <= c1;
                colors[0] = r0; colors[1] = g0; colors[2] = b0; colors[3] = 255;
                colors[4] = r1; colors[5] = g1; colors[6] = b1; colors[7] = 255;
                if (!oneBitAlpha)
                {
                    colors[8]  = (byte)((2 * r0 + r1) / 3); colors[9]  = (byte)((2 * g0 + g1) / 3); colors[10] = (byte)((2 * b0 + b1) / 3); colors[11] = 255;
                    colors[12] = (byte)((r0 + 2 * r1) / 3); colors[13] = (byte)((g0 + 2 * g1) / 3); colors[14] = (byte)((b0 + 2 * b1) / 3); colors[15] = 255;
                }
                else
                {
                    colors[8]  = (byte)((r0 + r1) / 2); colors[9] = (byte)((g0 + g1) / 2); colors[10] = (byte)((b0 + b1) / 2); colors[11] = 255;
                    colors[12] = 0; colors[13] = 0; colors[14] = 0; colors[15] = 0; // transparent black
                }

                uint colorIndices = (uint)(data[colorBlock + 4] | (data[colorBlock + 5] << 8)
                                         | (data[colorBlock + 6] << 16) | (data[colorBlock + 7] << 24));

                // DXT5 alpha ramp + 48 bits of 3-bit indices.
                ulong alphaBits = 0;
                if (kind == BcKind.Dxt5)
                {
                    int a0 = data[alphaBlock];
                    int a1 = data[alphaBlock + 1];
                    alphas[0] = (byte)a0;
                    alphas[1] = (byte)a1;
                    if (a0 > a1)
                    {
                        for (int i = 1; i <= 6; i++)
                            alphas[1 + i] = (byte)(((7 - i) * a0 + i * a1) / 7);
                    }
                    else
                    {
                        for (int i = 1; i <= 4; i++)
                            alphas[1 + i] = (byte)(((5 - i) * a0 + i * a1) / 5);
                        alphas[6] = 0;
                        alphas[7] = 255;
                    }
                    for (int i = 0; i < 6; i++)
                        alphaBits |= (ulong)data[alphaBlock + 2 + i] << (8 * i);
                }

                for (int ty = 0; ty < 4; ty++)
                {
                    int py = by * 4 + ty;
                    if (py >= height)
                        break;
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int px = bx * 4 + tx;
                        if (px >= width)
                            continue;

                        int texel = ty * 4 + tx;
                        int ci = (int)((colorIndices >> (2 * texel)) & 0x3) * 4;
                        byte r = colors[ci], g = colors[ci + 1], b = colors[ci + 2], a = colors[ci + 3];

                        if (kind == BcKind.Dxt3)
                        {
                            // 16 explicit 4-bit alphas (8 bytes), texel 0 in the low nibble of byte 0.
                            int byteIdx = alphaBlock + (texel >> 1);
                            int nib = (texel & 1) == 0 ? (data[byteIdx] & 0x0F) : (data[byteIdx] >> 4);
                            a = (byte)(nib * 17); // 4-bit → 8-bit (0→0, 15→255)
                        }
                        else if (kind == BcKind.Dxt5)
                        {
                            a = alphas[(int)((alphaBits >> (3 * texel)) & 0x7)];
                        }

                        int dst = (py * width + px) * 4;
                        outRgba[dst + 0] = r;
                        outRgba[dst + 1] = g;
                        outRgba[dst + 2] = b;
                        outRgba[dst + 3] = a;
                    }
                }

                p += blockBytes;
            }
        }
        return true;
    }

    /// <summary>Decode an uncompressed RGB/RGBA surface using the pixel-format channel masks.</summary>
    private static bool DecodeUncompressed(byte[] data, int dataLength, int offset, int width, int height, int bitCount,
                                           uint rMask, uint gMask, uint bMask, uint aMask, byte[] outRgba)
    {
        int bytesPerPixel = bitCount / 8;
        if (bytesPerPixel is not (3 or 4))
            return false;
        if (offset + (long)width * height * bytesPerPixel > dataLength)
            return false;

        int rShift = MaskShift(rMask), rBits = MaskBits(rMask);
        int gShift = MaskShift(gMask), gBits = MaskBits(gMask);
        int bShift = MaskShift(bMask), bBits = MaskBits(bMask);
        int aShift = MaskShift(aMask), aBits = MaskBits(aMask);

        int src = offset, dst = 0;
        int count = width * height;
        for (int i = 0; i < count; i++)
        {
            uint pixel = 0;
            for (int k = 0; k < bytesPerPixel; k++)
                pixel |= (uint)data[src + k] << (8 * k);
            src += bytesPerPixel;

            outRgba[dst + 0] = Channel(pixel, rMask, rShift, rBits, 0);
            outRgba[dst + 1] = Channel(pixel, gMask, gShift, gBits, 0);
            outRgba[dst + 2] = Channel(pixel, bMask, bShift, bBits, 0);
            outRgba[dst + 3] = aMask != 0 ? Channel(pixel, aMask, aShift, aBits, 255) : (byte)255;
            dst += 4;
        }
        return true;
    }

    private static void Rgb565(int v, out byte r, out byte g, out byte b)
    {
        int r5 = (v >> 11) & 0x1F;
        int g6 = (v >> 5) & 0x3F;
        int b5 = v & 0x1F;
        r = (byte)((r5 << 3) | (r5 >> 2)); // replicate high bits so 0x1F → 0xFF
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    /// <summary>Extract one channel from a packed pixel and scale it to 8 bits; <paramref name="fallback"/> when the mask is empty.</summary>
    private static byte Channel(uint pixel, uint mask, int shift, int bits, byte fallback)
    {
        if (mask == 0 || bits == 0)
            return fallback;
        uint v = (pixel & mask) >> shift;
        if (bits >= 8)
            return (byte)(v >> (bits - 8));
        int max = (1 << bits) - 1;
        return (byte)(v * 255 / max);
    }

    private static int MaskShift(uint mask)
    {
        if (mask == 0)
            return 0;
        int s = 0;
        while ((mask & 1) == 0) { mask >>= 1; s++; }
        return s;
    }

    private static int MaskBits(uint mask)
    {
        mask >>= MaskShift(mask);
        int b = 0;
        while ((mask & 1) == 1) { mask >>= 1; b++; }
        return b;
    }

    private static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    private static uint FourCc(char a, char b, char c, char d) => (uint)(a | (b << 8) | (c << 16) | (d << 24));
}

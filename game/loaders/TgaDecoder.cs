using Godot;

namespace VortexArena.Game.Loaders;

/// <summary>
/// A self-contained Targa (.tga) decoder producing a Godot <see cref="Image"/> in
/// <see cref="Image.Format.Rgba8"/>.
///
/// Xonotic ships the overwhelming majority of its textures as <c>.tga</c>, and Godot's
/// <see cref="Image.LoadTgaFromBuffer"/> is unreliable for the full spread the game uses (notably
/// origin/orientation handling and some RLE files), so the asset pipeline decodes TGA itself. The
/// formats actually present in Xonotic are covered:
/// <list type="bullet">
///   <item>type 2  — uncompressed true-color, 24-bit (BGR) and 32-bit (BGRA)</item>
///   <item>type 10 — RLE true-color, 24/32-bit</item>
///   <item>type 3  — uncompressed grayscale, 8-bit</item>
///   <item>type 11 — RLE grayscale, 8-bit</item>
///   <item>type 1 / 9 — color-mapped (paletted), 8-bit index → 24/32-bit palette (rare, but handled)</item>
/// </list>
/// 16-bit (ARRRRRGGGGGBBBBB) true-color is also decoded. The image-descriptor byte's vertical-origin
/// bit is honored so top-left and bottom-left TGAs both come out upright. Returns null on a malformed
/// or unsupported header rather than throwing, so a bad asset is skipped, not fatal.
/// </summary>
internal static class TgaDecoder
{
    /// <summary>
    /// Decode <paramref name="data"/> (a full .tga file image) into an RGBA8 <see cref="Image"/>, or
    /// null if the header is malformed or the image type is unsupported.
    /// </summary>
    public static Image? Decode(byte[] data) => Decode(data, data?.Length ?? 0);

    /// <summary>Length-taking overload (perf 2026-07-03) so the caller can hand in a POOLED, possibly-oversized
    /// file buffer: only the first <paramref name="length"/> bytes are the file.</summary>
    public static Image? Decode(byte[] data, int length)
    {
        if (data == null || length < 18)
            return null;

        // --- TGA header (18 bytes, little-endian) ---
        int idLength      = data[0];
        int colorMapType  = data[1];
        int imageType     = data[2];
        int cmapFirst     = data[3] | (data[4] << 8);
        int cmapLength    = data[5] | (data[6] << 8);
        int cmapEntrySize = data[7];                       // bits per palette entry
        // bytes 8..11: x/y origin (ignored)
        int width  = data[12] | (data[13] << 8);
        int height = data[14] | (data[15] << 8);
        int pixelDepth = data[16];                         // bits per pixel
        int descriptor = data[17];

        if (width <= 0 || height <= 0 || width > 1 << 14 || height > 1 << 14)
            return null;

        bool topToBottom = (descriptor & 0x20) != 0;       // origin bit 5: 1 = top-left

        // Image type families: low nibble selects the kind, bit 3 (value 8) flags RLE.
        bool rle = (imageType & 0x08) != 0;
        int baseType = imageType & 0x07;                   // 1=colormapped, 2=truecolor, 3=grayscale
        if (baseType != 1 && baseType != 2 && baseType != 3)
            return null;

        int offset = 18 + idLength;
        if (offset > length)
            return null;

        // --- color map (palette) for type 1/9 ---
        byte[]? palette = null;       // RGBA8, cmapLength entries
        int paletteBytesPerEntry = 0;
        if (colorMapType == 1 || baseType == 1)
        {
            if (cmapEntrySize is not (15 or 16 or 24 or 32) || cmapLength <= 0)
                return null;
            paletteBytesPerEntry = (cmapEntrySize + 7) / 8;
            int cmapBytes = cmapLength * paletteBytesPerEntry;
            if (offset + cmapBytes > length)
                return null;

            palette = new byte[cmapLength * 4];
            for (int i = 0; i < cmapLength; i++)
            {
                int src = offset + i * paletteBytesPerEntry;
                DecodeTexel(data, src, cmapEntrySize, out byte r, out byte g, out byte b, out byte a);
                int d = i * 4;
                palette[d + 0] = r; palette[d + 1] = g; palette[d + 2] = b; palette[d + 3] = a;
            }
            offset += cmapBytes;
        }

        int bytesPerPixel = (pixelDepth + 7) / 8;
        // Validate per-type pixel depth.
        if (baseType == 1)
        {
            if (pixelDepth is not (8 or 16)) return null;   // palette index width
        }
        else if (baseType == 2)
        {
            if (pixelDepth is not (16 or 24 or 32)) return null;
        }
        else // grayscale
        {
            if (pixelDepth is not (8 or 16)) return null;   // 16-bit gray = gray+alpha
        }

        int pixelCount = width * height;
        // Pooled shared scratch (CreateFromData copies it out below, so the finally can return it).
        // Collapses the per-texture decode-burst allocation (§12.6b) — see RgbaDecodeBuffer.
        byte[] outRgba = RgbaDecodeBuffer.Rent(pixelCount * 4);
        try
        {
            // Read pixels into a linear buffer in source order (the file's first row), then flip if needed.
            if (rle)
            {
                if (!DecodeRle(data, length, offset, pixelCount, bytesPerPixel, baseType, pixelDepth, palette, outRgba))
                    return null;
            }
            else
            {
                if (!DecodeRaw(data, length, offset, pixelCount, bytesPerPixel, baseType, pixelDepth, palette, outRgba))
                    return null;
            }

            // TGA's default origin is bottom-left: row 0 in the file is the BOTTOM of the image. Godot
            // expects row 0 = top. Flip vertically unless the descriptor said top-to-bottom already.
            if (!topToBottom)
                FlipVertical(outRgba, width, height);

            return Image.CreateFromData(width, height, false, Image.Format.Rgba8, outRgba);
        }
        finally
        {
            RgbaDecodeBuffer.Return(outRgba);
        }
    }

    private static bool DecodeRaw(byte[] data, int dataLength, int offset, int pixelCount, int bpp, int baseType,
                                  int depth, byte[]? palette, byte[] outRgba)
    {
        if (offset + (long)pixelCount * bpp > dataLength)
            return false;
        for (int i = 0; i < pixelCount; i++)
        {
            int src = offset + i * bpp;
            EmitPixel(data, src, baseType, depth, palette, outRgba, i * 4);
        }
        return true;
    }

    private static bool DecodeRle(byte[] data, int dataLength, int offset, int pixelCount, int bpp, int baseType,
                                  int depth, byte[]? palette, byte[] outRgba)
    {
        int produced = 0;
        int pos = offset;
        int n = dataLength;
        while (produced < pixelCount)
        {
            if (pos >= n)
                return false;
            int packet = data[pos++];
            int count = (packet & 0x7F) + 1;
            bool runLength = (packet & 0x80) != 0;

            if (produced + count > pixelCount)
                count = pixelCount - produced; // clamp a malformed over-long run

            if (runLength)
            {
                // One pixel value repeated 'count' times.
                if (pos + bpp > n)
                    return false;
                for (int k = 0; k < count; k++)
                    EmitPixel(data, pos, baseType, depth, palette, outRgba, (produced + k) * 4);
                pos += bpp;
            }
            else
            {
                // 'count' literal pixels.
                if (pos + count * bpp > n)
                    return false;
                for (int k = 0; k < count; k++)
                    EmitPixel(data, pos + k * bpp, baseType, depth, palette, outRgba, (produced + k) * 4);
                pos += count * bpp;
            }
            produced += count;
        }
        return true;
    }

    /// <summary>Decode one source pixel at <paramref name="src"/> into RGBA8 at <paramref name="dst"/> in <paramref name="outRgba"/>.</summary>
    private static void EmitPixel(byte[] data, int src, int baseType, int depth, byte[]? palette,
                                  byte[] outRgba, int dst)
    {
        byte r, g, b, a;
        if (baseType == 1) // color-mapped: source is a palette index
        {
            int index = depth == 16 ? (data[src] | (data[src + 1] << 8)) : data[src];
            if (palette != null && index >= 0 && index * 4 + 3 < palette.Length)
            {
                int p = index * 4;
                r = palette[p]; g = palette[p + 1]; b = palette[p + 2]; a = palette[p + 3];
            }
            else { r = g = b = 0; a = 255; }
        }
        else if (baseType == 3) // grayscale
        {
            byte gray = data[src];
            r = g = b = gray;
            a = depth == 16 ? data[src + 1] : (byte)255; // 16-bit gray carries an alpha byte
        }
        else // true-color
        {
            DecodeTexel(data, src, depth, out r, out g, out b, out a);
        }

        outRgba[dst + 0] = r;
        outRgba[dst + 1] = g;
        outRgba[dst + 2] = b;
        outRgba[dst + 3] = a;
    }

    /// <summary>Decode a true-color texel (BGR(A) byte order, or 15/16-bit packed) to RGBA8 components.</summary>
    private static void DecodeTexel(byte[] data, int src, int depth, out byte r, out byte g, out byte b, out byte a)
    {
        switch (depth)
        {
            case 32: // BGRA
                b = data[src]; g = data[src + 1]; r = data[src + 2]; a = data[src + 3];
                return;
            case 24: // BGR
                b = data[src]; g = data[src + 1]; r = data[src + 2]; a = 255;
                return;
            case 15:
            case 16: // ARRRRRGGGGGBBBBB (little-endian 16-bit)
            {
                int v = data[src] | (data[src + 1] << 8);
                int r5 = (v >> 10) & 0x1F;
                int g5 = (v >> 5) & 0x1F;
                int b5 = v & 0x1F;
                r = (byte)((r5 << 3) | (r5 >> 2));
                g = (byte)((g5 << 3) | (g5 >> 2));
                b = (byte)((b5 << 3) | (b5 >> 2));
                // 15-bit has no alpha; 16-bit's top "attribute" bit is unreliable across writers and
                // Xonotic's 16-bit TGAs are opaque, so treat both as fully opaque.
                a = 255;
                return;
            }
            default:
                r = g = b = 0; a = 255;
                return;
        }
    }

    private static void FlipVertical(byte[] rgba, int width, int height)
    {
        int rowBytes = width * 4;
        var tmp = new byte[rowBytes];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * rowBytes;
            int bot = (height - 1 - y) * rowBytes;
            System.Array.Copy(rgba, top, tmp, 0, rowBytes);
            System.Array.Copy(rgba, bot, rgba, top, rowBytes);
            System.Array.Copy(tmp, 0, rgba, bot, rowBytes);
        }
    }
}

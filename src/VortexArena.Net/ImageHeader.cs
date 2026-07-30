using System.Buffers.Binary;

namespace VortexArena.Net;

/// <summary>Reads an image's format and pixel dimensions out of its header, without decoding it.
///
/// Exists for the map catalog, which has to declare a thumbnail's format, width and height alongside the
/// bytes (map-catalog-v1 §4) and is then held to them: the master checks the declaration against the
/// magic bytes and rejects anything over the §7 dimension cap. Declaring what the file says about itself
/// is the only way to be sure those two agree.
///
/// <para>Header parsing only, deliberately. Nothing here allocates from a length field or walks into
/// pixel data, so a malformed or hostile file produces false rather than work.</para></summary>
internal static class ImageHeader
{
    /// <summary>The protocol's format name (<c>png</c> / <c>jpeg</c>) and the declared dimensions, or
    /// false when this is neither, is truncated, or has a header this cannot read.
    ///
    /// WebP is missing on purpose even though §7 accepts it on the way in. Nothing in the content tree
    /// produces one — every shipped levelshot is JPEG — so a VP8/VP8L/VP8X header reader here would be
    /// code no map in the wild exercises, and getting it subtly wrong means declaring dimensions that do
    /// not match the image, which is the one mistake this function exists to prevent. A .webp levelshot
    /// therefore reports no thumbnail, which §10 already treats as ordinary.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out string format, out int width, out int height)
    {
        format = "";
        width = 0;
        height = 0;

        if (TryReadPng(data, out width, out height))
        {
            format = "png";
            return true;
        }

        if (TryReadJpeg(data, out width, out height))
        {
            format = "jpeg";
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>PNG puts IHDR first by spec, so width and height are at fixed offsets: 8 bytes of
    /// signature, then a 4-byte chunk length, "IHDR", then two big-endian uint32s.</summary>
    private static bool TryReadPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 24 || !data.StartsWith(PngMagic) || !data[12..16].SequenceEqual("IHDR"u8))
            return false;

        var w = BinaryPrimitives.ReadUInt32BigEndian(data[16..20]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(data[20..24]);
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue)
            return false;

        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>JPEG has no fixed layout: the dimensions live in whichever start-of-frame marker the
    /// encoder emitted, after any number of application and quantization segments, so the segment chain
    /// has to be walked. Every step is bounded by the buffer, and a segment length that does not advance
    /// ends the walk rather than looping.</summary>
    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return false;

        var at = 2;
        while (at + 3 < data.Length)
        {
            // Fill bytes: a marker may be preceded by any number of 0xFF padding bytes.
            if (data[at] != 0xFF)
                return false;
            var marker = data[at + 1];
            at += 2;
            if (marker == 0xFF)
            {
                at--;
                continue;
            }

            // Standalone markers carry no length: restart markers, and TEM.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
                continue;

            // Start of scan: everything after this is entropy-coded data, and a frame header that has
            // not appeared by now is not going to.
            if (marker == 0xDA)
                return false;

            if (at + 1 >= data.Length)
                return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[at..(at + 2)]);
            if (length < 2)
                return false;

            // SOF0-SOF15, excluding the three that share the range but are not frame headers: DHT (0xC4),
            // JPG (0xC8) and DAC (0xCC).
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // length(2), precision(1), height(2), width(2).
                if (at + 7 > data.Length)
                    return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 3)..(at + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..(at + 7)]);
                if (width == 0 || height == 0)
                    return false;
                return true;
            }

            at += length;
        }

        return false;
    }
}

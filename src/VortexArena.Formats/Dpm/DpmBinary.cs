using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XonoticGodot.Formats.Dpm;

/// <summary>
/// Big-endian read helpers over a <see cref="ReadOnlySpan{T}"/> of bytes, for the DarkPlaces Model
/// (DPM, "DARKPLACESMODEL\0", type 2) skeletal format.
///
/// Unlike Quake's IBSP/MD3 (which are little-endian), DPM stores <b>every</b> multi-byte value
/// big-endian — "network byte order", as the format spec puts it. The DarkPlaces loader passes each
/// field through <c>BigLong</c>/<c>BigFloat</c>. We therefore read explicitly big-endian via
/// <see cref="BinaryPrimitives"/> so the parser is correct on any host endianness. The sibling
/// <see cref="BinaryUtil"/> (little-endian) is deliberately not reused here.
///
/// All readers take an absolute byte <paramref name="offset"/> and bounds-check against the span,
/// throwing <see cref="AssetParseException"/> on overrun rather than the default
/// <see cref="IndexOutOfRangeException"/>, so a malformed file surfaces a clear, catchable error.
/// </summary>
public static class DpmBinary
{
    private static void Require(ReadOnlySpan<byte> data, int offset, int count, string what)
    {
        // offset+count can overflow for hostile inputs; compare via long.
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
            throw new AssetParseException(
                $"Truncated DPM data reading {what}: need {count} byte(s) at offset {offset}, buffer is {data.Length} byte(s).");
    }

    public static int ReadInt32BE(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "int32");
        return BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
    }

    public static uint ReadUInt32BE(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "uint32");
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    }

    public static float ReadFloatBE(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "float");
        return BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4));
    }

    /// <summary>Reads 3 consecutive big-endian floats as a <see cref="Vector3"/> (Quake xyz order).</summary>
    public static Vector3 ReadVec3BE(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 12, "vec3");
        return new Vector3(
            BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4)),
            BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset + 4, 4)),
            BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset + 8, 4)));
    }

    /// <summary>Reads 2 consecutive big-endian floats as a <see cref="Vector2"/> (texcoord st order).</summary>
    public static Vector2 ReadVec2BE(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 8, "vec2");
        return new Vector2(
            BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4)),
            BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset + 4, 4)));
    }

    /// <summary>
    /// Reads the 16-byte file identifier verbatim (no NUL trimming) so the caller can compare it
    /// against the exact magic "DARKPLACESMODEL\0", which includes a trailing NUL inside the field.
    /// </summary>
    public static string ReadId(ReadOnlySpan<byte> data, int offset, int length)
    {
        Require(data, offset, length, "id");
        Span<char> c = stackalloc char[length];
        for (int i = 0; i < length; i++)
            c[i] = (char)data[offset + i];
        return new string(c);
    }

    /// <summary>
    /// Reads a fixed-length ASCII name field of <paramref name="length"/> bytes (bone/shader/frame
    /// names are all <c>char[32]</c> in DPM) and trims at the first NUL. Trailing whitespace is also
    /// trimmed because some exporters pad with spaces. Bytes are treated as ASCII per the format's
    /// contract.
    /// </summary>
    public static string ReadName(ReadOnlySpan<byte> data, int offset, int length)
    {
        Require(data, offset, length, $"name[{length}]");
        ReadOnlySpan<byte> field = data.Slice(offset, length);
        int n = field.IndexOf((byte)0);
        if (n < 0) n = length;
        string s = Encoding.ASCII.GetString(field.Slice(0, n));
        return s.TrimEnd('\0', ' ', '\t', '\r', '\n');
    }
}

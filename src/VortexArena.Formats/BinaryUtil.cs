using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XonoticGodot.Formats;

/// <summary>
/// Little-endian read helpers over a <see cref="ReadOnlySpan{T}"/> of bytes.
///
/// Both Quake IBSP maps and MD3 models store all multi-byte values little-endian
/// (Darkplaces passes every field through <c>LittleLong</c>/<c>LittleFloat</c>/<c>LittleShort</c>).
/// We therefore read explicitly little-endian via <see cref="BinaryPrimitives"/> so the parsers
/// are correct on a big-endian host as well. (DPM would be big-endian, but DPM is out of scope here.)
///
/// All readers take an absolute byte <paramref name="offset"/> and bounds-check against the span,
/// throwing <see cref="AssetParseException"/> on overrun rather than the default
/// <see cref="IndexOutOfRangeException"/>, so malformed files surface a clear, catchable error.
/// </summary>
public static class BinaryUtil
{
    private static void Require(ReadOnlySpan<byte> data, int offset, int count, string what)
    {
        // offset+count can overflow for hostile inputs; compare via long.
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
            throw new AssetParseException(
                $"Truncated data reading {what}: need {count} byte(s) at offset {offset}, buffer is {data.Length} byte(s).");
    }

    public static int ReadInt32(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "int32");
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "uint32");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    public static short ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 2, "int16");
        return BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    }

    public static float ReadFloat(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "float");
        return BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
    }

    /// <summary>Reads 3 consecutive little-endian floats as a <see cref="Vector3"/> (Quake xyz order).</summary>
    public static Vector3 ReadVec3(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 12, "vec3");
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 8, 4)));
    }

    /// <summary>Reads 2 consecutive little-endian floats as a <see cref="Vector2"/> (texcoord st order).</summary>
    public static Vector2 ReadVec2(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 8, "vec2");
        return new Vector2(
            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 4, 4)));
    }

    /// <summary>Reads a 4-byte ASCII tag (e.g. "IBSP", "IDP3") without null handling.</summary>
    public static string ReadMagic(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "magic");
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
            c[i] = (char)data[offset + i];
        return new string(c);
    }

    /// <summary>
    /// Reads a fixed-length ASCII name field of <paramref name="length"/> bytes (e.g. shader name[64],
    /// frame name[16]) and trims at the first NUL. Trailing whitespace is also trimmed because Quake
    /// tools sometimes pad with spaces. Non-ASCII / control bytes are kept verbatim except NUL.
    /// </summary>
    public static string ReadFixedString(ReadOnlySpan<byte> data, int offset, int length)
    {
        Require(data, offset, length, $"name[{length}]");
        ReadOnlySpan<byte> field = data.Slice(offset, length);
        int n = field.IndexOf((byte)0);
        if (n < 0) n = length;
        // ASCII is the contract for these Quake path/name fields.
        string s = Encoding.ASCII.GetString(field.Slice(0, n));
        return s.TrimEnd('\0', ' ', '\t', '\r', '\n');
    }
}

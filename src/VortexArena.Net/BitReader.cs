using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace XonoticGodot.Net;

/// <summary>
/// Byte-oriented message reader over a <see cref="ReadOnlySpan{T}"/>, the exact inverse of
/// <see cref="BitWriter"/> (XonoticGodot analogue of the Darkplaces <c>sizebuf_t</c> reader + Xonotic
/// <c>net.qh</c> Read* wrappers). A <c>ref struct</c>: it never escapes to the heap, holds the source
/// span directly, and tracks a read cursor — zero-allocation by construction.
///
/// On underflow it sets <see cref="BadRead"/> and returns 0/empty (mirroring DP's <c>badread</c>
/// flag) rather than throwing on the hot path; check <see cref="BadRead"/> after parsing a message.
/// </summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        BadRead = false;
    }

    /// <summary>Set when a read ran past the end of the buffer. Sticky until a new reader is constructed.</summary>
    public bool BadRead { get; private set; }

    /// <summary>Current read offset in bytes.</summary>
    public readonly int Position => _pos;

    /// <summary>Total length of the source buffer.</summary>
    public readonly int Length => _data.Length;

    /// <summary>Bytes remaining to read.</summary>
    public readonly int Remaining => _data.Length - _pos;

    /// <summary>True while there is at least one more byte (and no prior bad read).</summary>
    public readonly bool CanRead => !BadRead && _pos < _data.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Check(int count)
    {
        if (_pos + count > _data.Length)
        {
            BadRead = true;
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------------
    // primitive integers (little-endian) — inverse of BitWriter
    // ---------------------------------------------------------------------

    /// <summary>Read one unsigned byte [0,255]. Returns 0 on underflow (sets <see cref="BadRead"/>).
    /// Port of <c>ReadByte</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadByte()
    {
        if (!Check(1)) return 0;
        return _data[_pos++];
    }

    /// <summary>Read one signed byte [-128,127]. Port of <c>ReadSByte</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSByte()
    {
        if (!Check(1)) return 0;
        return (sbyte)_data[_pos++];
    }

    /// <summary>Read a signed 16-bit short (little-endian). Port of <c>ReadShort</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadShort()
    {
        if (!Check(2)) return 0;
        short v = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_pos, 2));
        _pos += 2;
        return v;
    }

    /// <summary>Read an unsigned 16-bit short (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadUShort()
    {
        if (!Check(2)) return 0;
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_pos, 2));
        _pos += 2;
        return v;
    }

    /// <summary>Read a signed 32-bit int (little-endian). Port of <c>ReadLong</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadLong()
    {
        if (!Check(4)) return 0;
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_pos, 4));
        _pos += 4;
        return v;
    }

    /// <summary>Read an unsigned 32-bit int (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadULong()
    {
        if (!Check(4)) return 0;
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
        _pos += 4;
        return v;
    }

    /// <summary>Read a raw IEEE-754 32-bit float (little-endian). Port of <c>MSG_ReadFloat</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadFloat()
    {
        if (!Check(4)) return 0f;
        float v = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_pos, 4));
        _pos += 4;
        return v;
    }

    /// <summary>Read a bool written by <see cref="BitWriter.WriteBool"/> (any nonzero byte is true).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool() => ReadByte() != 0;

    /// <summary>Read a length-prefixed UTF-8 string written by <see cref="BitWriter.WriteString"/>.
    /// Empty/zero-length yields <see cref="string.Empty"/>.</summary>
    public string ReadString()
    {
        int byteCount = ReadUShort();
        if (byteCount == 0) return string.Empty;
        if (!Check(byteCount)) return string.Empty;
        string s = Encoding.UTF8.GetString(_data.Slice(_pos, byteCount));
        _pos += byteCount;
        return s;
    }

    /// <summary>Read <paramref name="count"/> raw bytes as a sub-span (no copy; valid for the reader's lifetime).</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count <= 0) return ReadOnlySpan<byte>.Empty;
        if (!Check(count)) return ReadOnlySpan<byte>.Empty;
        ReadOnlySpan<byte> s = _data.Slice(_pos, count);
        _pos += count;
        return s;
    }

    // ---------------------------------------------------------------------
    // Int24 (net.qh ReadInt24_t) — (signed short << 8) + unsigned byte
    // ---------------------------------------------------------------------

    /// <summary>Read a signed 24-bit value written by <see cref="BitWriter.WriteInt24"/>. Port of <c>ReadInt24_t</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt24()
    {
        short high = (short)ReadShort();
        byte low = (byte)ReadByte();
        return Quantize.DecodeInt24(high, low);
    }

    // ---------------------------------------------------------------------
    // Width-selected integer (net.qh Readbits)
    // ---------------------------------------------------------------------

    /// <summary>Read a value written by <see cref="BitWriter.WriteBits"/> using the same bit-width selection.
    /// Port of <c>Readbits</c>.</summary>
    public int ReadBits(int bitWidth)
    {
        if (bitWidth > 16) return ReadInt24();
        if (bitWidth > 8) return ReadShort();
        return ReadByte();
    }

    // ---------------------------------------------------------------------
    // Coordinates / angles / vectors — inverse of BitWriter, via Quantize
    // ---------------------------------------------------------------------

    /// <summary>Read a coordinate at the given precision. Port of <c>ReadCoord</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadCoord(NetPrecision precision = NetPrecision.Low)
    {
        switch (precision)
        {
            case NetPrecision.Float: return ReadFloat();
            case NetPrecision.High: return Quantize.DecodeCoord16((short)ReadShort());
            default: return Quantize.DecodeCoord13((short)ReadShort());
        }
    }

    /// <summary>Read an angle (degrees) at the given precision. Port of <c>ReadAngle</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadAngle(NetPrecision precision = NetPrecision.Low)
    {
        switch (precision)
        {
            case NetPrecision.Float: return ReadFloat();
            case NetPrecision.High: return Quantize.DecodeAngle16((ushort)ReadUShort());
            default: return Quantize.DecodeAngle8((byte)ReadByte());
        }
    }

    /// <summary>Read a 3-component position vector. Port of <c>ReadVector</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 ReadVector(NetPrecision precision = NetPrecision.Low)
    {
        float x = ReadCoord(precision);
        float y = ReadCoord(precision);
        float z = ReadCoord(precision);
        return new Vector3(x, y, z);
    }

    /// <summary>Read a 3-component Euler angle vector (pitch, yaw, roll). Port of <c>ReadAngleVector</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 ReadAngles(NetPrecision precision = NetPrecision.Low)
    {
        float x = ReadAngle(precision);
        float y = ReadAngle(precision);
        float z = ReadAngle(precision);
        return new Vector3(x, y, z);
    }

    // ---------------------------------------------------------------------
    // Approx past time (net.qh ReadApproxPastTime)
    // ---------------------------------------------------------------------

    /// <summary>Read the latency (seconds) written by <see cref="BitWriter.WriteApproxPastTime"/>.
    /// Returns <c>dt</c>; caller subtracts from its server clock to recover the event time
    /// (the QC version returns <c>servertime - dt</c>). Port of <c>ReadApproxPastTime</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadApproxPastTime() => Quantize.DecodeApproxPastTime((byte)ReadByte());
}

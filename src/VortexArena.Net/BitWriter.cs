using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace XonoticGodot.Net;

/// <summary>
/// Selects coordinate/angle precision for a write/read, mirroring the Darkplaces protocol tiers
/// (<c>com_msg.c</c>): low = 13i coords (1/8 unit) + 8i angles (1.40625°), high = 16i coords +
/// 16i angles, full = raw 32-bit float. The XonoticGodot wire format picks per-field; this enum lets the
/// vector/angle helpers share one code path with a precision argument.
/// </summary>
public enum NetPrecision : byte
{
    /// <summary>13i coords (short, ÷8) and 8i angles (byte). Cheapest; arena-default for entity origins.</summary>
    Low = 0,
    /// <summary>16i coords (short, whole units) and 16i angles (ushort). Larger world range / finer angles.</summary>
    High = 1,
    /// <summary>32-bit float coords and angles. Lossless; for the local predicted player snapshot.</summary>
    Float = 2,
}

/// <summary>
/// Byte-oriented message writer over a growable buffer. The XonoticGodot analogue of a Darkplaces
/// <c>sizebuf_t</c> writer + the Xonotic <c>net.qh</c> Write* wrappers, but we own the layout
/// (<see href="ADR-0011"/>): little-endian integers via <see cref="BinaryPrimitives"/>,
/// length-prefixed UTF-8 strings, quantization delegated to <see cref="Quantize"/>.
///
/// Allocation discipline (this is hot, per the networking spec): the instance is reusable — call
/// <see cref="Reset"/> between messages to keep the backing array. Growth doubles capacity. Prefer
/// one writer per send thread, reset and refilled, over per-message allocation.
/// </summary>
public sealed class BitWriter
{
    private byte[] _buffer;
    private int _length;

    public BitWriter(int initialCapacity = 1024)
    {
        _buffer = new byte[initialCapacity < 16 ? 16 : initialCapacity];
        _length = 0;
    }

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>The written bytes as a span (valid until the next write/reset).</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>The written bytes as a memory (valid until the next write/reset). Handy for async transport sends.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);

    /// <summary>Copy the written bytes into a fresh array (use only when you must detach from the buffer).</summary>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    /// <summary>Reset length to zero, retaining the backing array for reuse. The cheap path between messages.</summary>
    public void Reset() => _length = 0;

    // ---------------------------------------------------------------------
    // capacity
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureExtra(int extra)
    {
        int need = _length + extra;
        if (need <= _buffer.Length) return;
        Grow(need);
    }

    private void Grow(int need)
    {
        int cap = _buffer.Length * 2;
        if (cap < need) cap = need;
        Array.Resize(ref _buffer, cap);
    }

    /// <summary>Reserve <paramref name="count"/> bytes and return their span for direct fill, advancing the
    /// cursor (the C# analogue of <c>SZ_GetSpace</c>). The span is valid until the next write/grow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetSpace(int count)
    {
        EnsureExtra(count);
        Span<byte> s = _buffer.AsSpan(_length, count);
        _length += count;
        return s;
    }

    // ---------------------------------------------------------------------
    // primitive integers (little-endian) — names mirror net.qh Write*
    // ---------------------------------------------------------------------

    /// <summary>Write one byte (low 8 bits of <paramref name="b"/>). Port of <c>WriteByte</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(int b)
    {
        EnsureExtra(1);
        _buffer[_length++] = (byte)b;
    }

    /// <summary>Write a signed byte [-128,127]. Port of the QC <c>WriteByte</c> used for SByte payloads.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSByte(int b) => WriteByte(b & 0xFF);

    /// <summary>Write a 16-bit short (little-endian). Port of <c>WriteShort</c> (layout is ours, LE not QC's BE).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteShort(int s) => BinaryPrimitives.WriteInt16LittleEndian(GetSpace(2), (short)s);

    /// <summary>Write a 16-bit unsigned short (little-endian).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUShort(int s) => BinaryPrimitives.WriteUInt16LittleEndian(GetSpace(2), (ushort)s);

    /// <summary>Write a 32-bit int (little-endian). Port of <c>WriteLong</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteLong(int l) => BinaryPrimitives.WriteInt32LittleEndian(GetSpace(4), l);

    /// <summary>Write a 32-bit unsigned int (little-endian). Used for sequence numbers / content hashes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteULong(uint l) => BinaryPrimitives.WriteUInt32LittleEndian(GetSpace(4), l);

    /// <summary>Write a raw IEEE-754 32-bit float (little-endian). Port of <c>MSG_WriteFloat</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFloat(float f) => BinaryPrimitives.WriteSingleLittleEndian(GetSpace(4), f);

    /// <summary>Write a length-prefixed UTF-8 string (ushort byte-length prefix, then bytes). Diverges from
    /// DP's NUL-terminated <c>MSG_WriteString</c> on purpose — a length prefix is span-friendly and avoids
    /// embedded-NUL ambiguity. <c>null</c> and empty both encode as a zero length.</summary>
    public void WriteString(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            WriteUShort(0);
            return;
        }
        int byteCount = Encoding.UTF8.GetByteCount(s);
        WriteUShort(byteCount);
        Span<byte> dst = GetSpace(byteCount);
        Encoding.UTF8.GetBytes(s, dst);
    }

    /// <summary>Write a raw block of bytes verbatim.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        bytes.CopyTo(GetSpace(bytes.Length));
    }

    // ---------------------------------------------------------------------
    // Back-patching — overwrite an already-written field once a later count/length is known
    // ---------------------------------------------------------------------

    /// <summary>
    /// Overwrite the 16-bit little-endian unsigned value at byte offset <paramref name="offset"/> (which must
    /// already have been reserved by a prior <see cref="WriteUShort"/>). The XonoticGodot analogue of writing a
    /// placeholder count, appending a variable number of records, then patching the real count in — used by
    /// the snapshot/event bundlers where the record count isn't known until after the loop. No-op (and a
    /// debug-time guard) if the range is out of the written region.
    /// </summary>
    public void PatchUShortAt(int offset, int value)
    {
        if (offset < 0 || offset + 2 > _length) return;
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(offset, 2), (ushort)value);
    }

    /// <summary>Write a bool as one byte (0/1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBool(bool b) => WriteByte(b ? 1 : 0);

    // ---------------------------------------------------------------------
    // Int24 (net.qh WriteInt24_t) — short<<8 + byte, little-endian byte order for the short part
    // ---------------------------------------------------------------------

    /// <summary>Write a signed 24-bit value as (short high, byte low) per <c>WriteInt24_t</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt24(int value)
    {
        Quantize.EncodeInt24(value, out short high, out byte low);
        WriteShort(high);
        WriteByte(low);
    }

    // ---------------------------------------------------------------------
    // Width-selected integer (net.qh Writebits): byte / short / int24 by magnitude budget
    // ---------------------------------------------------------------------

    /// <summary>Write <paramref name="value"/> using the smallest of byte/short/int24 that holds
    /// <paramref name="bitWidth"/> bits, matching <c>Writebits</c>: &gt;16 → int24, &gt;8 → short, else byte.</summary>
    public void WriteBits(int value, int bitWidth)
    {
        if (bitWidth > 16) { WriteInt24(value); return; }
        if (bitWidth > 8) { WriteShort(value); return; }
        WriteByte(value);
    }

    // ---------------------------------------------------------------------
    // Coordinates / angles / vectors — quantized via Quantize, precision-selected
    // ---------------------------------------------------------------------

    /// <summary>Write a single coordinate at the given precision (default <see cref="NetPrecision.Low"/> = 13i,
    /// matching DP's <c>MSG_WriteCoord</c> for the Quake protocol). Port of <c>WriteCoord</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCoord(float value, NetPrecision precision = NetPrecision.Low)
    {
        switch (precision)
        {
            case NetPrecision.Float: WriteFloat(value); break;
            case NetPrecision.High: WriteShort(Quantize.EncodeCoord16(value)); break;
            default: WriteShort(Quantize.EncodeCoord13(value)); break;
        }
    }

    /// <summary>Write a single angle (degrees) at the given precision (default 8i byte). Port of <c>WriteAngle</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAngle(float deg, NetPrecision precision = NetPrecision.Low)
    {
        switch (precision)
        {
            case NetPrecision.Float: WriteFloat(deg); break;
            case NetPrecision.High: WriteUShort(Quantize.EncodeAngle16(deg)); break;
            default: WriteByte(Quantize.EncodeAngle8(deg)); break;
        }
    }

    /// <summary>Write a 3-component position vector as three coords. Port of <c>WriteVector</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVector(Vector3 v, NetPrecision precision = NetPrecision.Low)
    {
        WriteCoord(v.X, precision);
        WriteCoord(v.Y, precision);
        WriteCoord(v.Z, precision);
    }

    /// <summary>Write a 3-component Euler angle vector (pitch, yaw, roll) as three angles.
    /// Port of <c>WriteAngleVector</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAngles(Vector3 angles, NetPrecision precision = NetPrecision.Low)
    {
        WriteAngle(angles.X, precision);
        WriteAngle(angles.Y, precision);
        WriteAngle(angles.Z, precision);
    }

    // ---------------------------------------------------------------------
    // Approx past time (net.qh WriteApproxPastTime)
    // ---------------------------------------------------------------------

    /// <summary>Write a latency (seconds) as one byte via the nonlinear approx-past-time encoding.
    /// <paramref name="dt"/> is <c>serverTime - eventTime</c>. Port of <c>WriteApproxPastTime</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteApproxPastTime(float dt) => WriteByte(Quantize.EncodeApproxPastTime(dt));
}

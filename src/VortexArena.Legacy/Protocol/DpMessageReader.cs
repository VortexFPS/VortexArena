// Port of Base/darkplaces/com_msg.c MSG_Read* and the MSG_ReadByte/MSG_ReadChar macros of common.h.
using System.Numerics;
using System.Text;

namespace VortexArena.Legacy.Protocol;

/// <summary>
/// Reads one DarkPlaces message: little-endian integers and floats, NUL-terminated strings.
///
/// A class, not a ref struct, on purpose. Two server messages (svc_csqcentities, svc_temp_entity)
/// have payloads only the game's QuakeC knows the length of, so the parser hands this same reader to
/// whoever runs that QuakeC and carries on from wherever they stopped.
///
/// A read past the end never throws: it sets <see cref="BadRead"/> and returns -1 (or an empty
/// string), exactly as sizebuf_t.badread does, and the caller decides what a short message means.
/// </summary>
public sealed class DpMessageReader
{
    private readonly byte[] _data;
    private readonly int _start;
    private readonly int _end;
    private int _pos;

    public DpMessageReader(byte[] data) : this(data, 0, data?.Length ?? 0) { }

    public DpMessageReader(byte[] data, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));
        _data = data;
        _start = offset;
        _end = offset + length;
        _pos = offset;
    }

    /// <summary>Bytes consumed so far (sizebuf_t.readcount).</summary>
    public int Position
    {
        get => _pos - _start;
        // Clamped so that a handler restoring a saved position can never point the reader outside its buffer.
        set => _pos = _start + Math.Clamp(value, 0, _end - _start);
    }

    public int Length => _end - _start;
    public int Remaining => _end - _pos;

    /// <summary>Set once any read ran off the end. Cleared only by the owner (MSG_BeginReading, or the
    /// temp-entity rewind in CL_VM_Parse_TempEntity).</summary>
    public bool BadRead { get; set; }

    /// <summary>The whole message, for diagnostics and for demos that re-record it.</summary>
    public ReadOnlySpan<byte> Buffer => _data.AsSpan(_start, _end - _start);

    /// <summary>MSG_ReadByte: 0..255, or -1 at the end of the message.</summary>
    public int ReadByte()
    {
        if (_pos >= _end) { BadRead = true; return -1; }
        return _data[_pos++];
    }

    /// <summary>MSG_ReadChar: -128..127, or -1 at the end (indistinguishable from a real -1, as in C).</summary>
    public int ReadChar()
    {
        if (_pos >= _end) { BadRead = true; return -1; }
        return (sbyte)_data[_pos++];
    }

    /// <summary>MSG_ReadLittleShort: signed 16-bit, or -1 at the end.</summary>
    public int ReadShort()
    {
        if (_pos + 2 > _end) { BadRead = true; return -1; }
        _pos += 2;
        return (short)(_data[_pos - 2] | (_data[_pos - 1] << 8));
    }

    /// <summary>The <c>(unsigned short)MSG_ReadShort()</c> idiom: 0..65535, and 65535 at the end.</summary>
    public int ReadUShort() => (ushort)ReadShort();

    /// <summary>MSG_ReadLittleLong.</summary>
    public int ReadLong()
    {
        if (_pos + 4 > _end) { BadRead = true; return -1; }
        _pos += 4;
        return _data[_pos - 4] | (_data[_pos - 3] << 8) | (_data[_pos - 2] << 16) | (_data[_pos - 1] << 24);
    }

    /// <summary>MSG_ReadBigLong. Only the netchan header and the legacy control packets are big-endian.</summary>
    public int ReadBigLong()
    {
        if (_pos + 4 > _end) { BadRead = true; return -1; }
        _pos += 4;
        return (_data[_pos - 4] << 24) | (_data[_pos - 3] << 16) | (_data[_pos - 2] << 8) | _data[_pos - 1];
    }

    /// <summary>MSG_ReadLittleFloat. Returns -1 at the end, as the C does.</summary>
    public float ReadFloat()
    {
        if (_pos + 4 > _end) { BadRead = true; return -1; }
        _pos += 4;
        return BitConverter.Int32BitsToSingle(
            _data[_pos - 4] | (_data[_pos - 3] << 8) | (_data[_pos - 2] << 16) | (_data[_pos - 1] << 24));
    }

    /// <summary>
    /// MSG_ReadString as raw bytes: consumes up to and including the NUL, keeps at most
    /// <paramref name="maxLength"/> - 1 bytes (the C buffer keeps room for its terminator). A string
    /// that runs off the end of the message sets <see cref="BadRead"/> and yields what was there.
    /// </summary>
    public ReadOnlySpan<byte> ReadStringBytes(int maxLength = DpProtocol.MaxInputLine)
    {
        int begin = _pos;
        while (_pos < _end && _data[_pos] != 0)
            _pos++;
        int length = _pos - begin;
        if (_pos >= _end)
            BadRead = true; // MSG_ReadByte_opt hit the end while looking for the terminator
        else
            _pos++; // the NUL
        return _data.AsSpan(begin, Math.Min(length, Math.Max(0, maxLength - 1)));
    }

    /// <summary>
    /// MSG_ReadString decoded as UTF-8 (Xonotic runs with utf8_enable 1). Bytes that are not valid
    /// UTF-8, such as Quake's coloured-font characters, come out as U+FFFD; use
    /// <see cref="ReadStringBytes"/> where they must survive.
    /// </summary>
    public string ReadString(int maxLength = DpProtocol.MaxInputLine)
    {
        ReadOnlySpan<byte> bytes = ReadStringBytes(maxLength);
        return bytes.IsEmpty ? "" : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>MSG_ReadBytes: copies up to <paramref name="destination"/>.Length bytes, stopping at the
    /// end of the message. Returns how many were copied; a short copy sets <see cref="BadRead"/>.</summary>
    public int ReadBytes(Span<byte> destination)
    {
        int n = Math.Min(destination.Length, _end - _pos);
        _data.AsSpan(_pos, n).CopyTo(destination);
        _pos += n;
        if (n < destination.Length)
            BadRead = true;
        return n;
    }

    /// <summary>A view of the next <paramref name="count"/> bytes without copying (fewer at the end,
    /// which sets <see cref="BadRead"/>). Valid for as long as the message buffer is.</summary>
    public ReadOnlySpan<byte> ReadSpan(int count)
    {
        int n = Math.Clamp(count, 0, _end - _pos);
        ReadOnlySpan<byte> span = _data.AsSpan(_pos, n);
        _pos += n;
        if (n < count)
            BadRead = true;
        return span;
    }

    /// <summary>MSG_ReadCoord13i: 16-bit fixed point, 1/8 unit. EntityFrame5 uses it for low-precision origins.</summary>
    public float ReadCoord13i() => ReadShort() * (1.0f / 8.0f);
    /// <summary>MSG_ReadCoord16i: whole units in a signed 16-bit.</summary>
    public float ReadCoord16i() => (short)ReadShort();
    /// <summary>MSG_ReadCoord32f.</summary>
    public float ReadCoord32f() => ReadFloat();
    /// <summary>MSG_ReadCoord for DP7: a float (com_msg.c falls through to MSG_ReadCoord32f for DP5 and later).</summary>
    public float ReadCoord() => ReadFloat();

    /// <summary>MSG_ReadVector for DP7.</summary>
    public Vector3 ReadVector()
    {
        float x = ReadFloat(), y = ReadFloat(), z = ReadFloat();
        return new Vector3(x, y, z);
    }

    /// <summary>MSG_ReadAngle8i: a signed byte of 360/256 degree steps.</summary>
    public float ReadAngle8i() => (float)((sbyte)ReadByte() * (360.0 / 256.0));
    /// <summary>MSG_ReadAngle16i: a signed 16-bit of 360/65536 degree steps.</summary>
    public float ReadAngle16i() => (float)((short)ReadShort() * (360.0 / 65536.0));
    /// <summary>MSG_ReadAngle for DP7: the 16-bit form (the 8-bit one is for DP4 and older).</summary>
    public float ReadAngle() => ReadAngle16i();

    public Vector3 ReadAngles()
    {
        float x = ReadAngle16i(), y = ReadAngle16i(), z = ReadAngle16i();
        return new Vector3(x, y, z);
    }
}

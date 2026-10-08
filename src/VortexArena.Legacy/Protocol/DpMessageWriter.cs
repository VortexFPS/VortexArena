// Port of Base/darkplaces/com_msg.c MSG_Write* and the sizebuf_t overflow rule of SZ_GetSpace (common.c).
using System.Numerics;
using System.Text;

namespace VortexArena.Legacy.Protocol;

/// <summary>
/// Builds one DarkPlaces message. Like sizebuf_t it has a hard maximum; a write that would exceed it
/// is dropped whole and sets <see cref="Overflowed"/> instead of throwing, so one oversized string
/// command cannot take the frame loop down.
/// </summary>
public sealed class DpMessageWriter
{
    private byte[] _data;
    private int _length;

    public DpMessageWriter(int maxSize = DpProtocol.NetMaxMessage, int initialCapacity = 256)
    {
        if (maxSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxSize));
        MaxSize = maxSize;
        _data = new byte[Math.Min(Math.Max(initialCapacity, 16), maxSize)];
    }

    public int MaxSize { get; }
    public int Length => _length;
    public bool Overflowed { get; private set; }
    public ReadOnlySpan<byte> WrittenSpan => _data.AsSpan(0, _length);
    public byte[] ToArray() => _data.AsSpan(0, _length).ToArray();

    /// <summary>SZ_Clear.</summary>
    public void Clear()
    {
        _length = 0;
        Overflowed = false;
    }

    /// <summary>
    /// The server's "<c>msg->cursize = oldcursize; msg->overflowed = false;</c>" idiom
    /// (sv_ents_csqc.c EntityFrameCSQC_WriteFrame): forget everything written after the message was
    /// <paramref name="length"/> bytes long, and the overflow those writes may have caused. A length
    /// that is negative or beyond what has been written is ignored, so this can only shorten.
    /// </summary>
    public void Rollback(int length)
    {
        if ((uint)length > (uint)_length)
            return;
        _length = length;
        Overflowed = false;
    }

    private Span<byte> GetSpace(int count)
    {
        if (count > MaxSize - _length)
        {
            Overflowed = true;
            return default;
        }
        if (_length + count > _data.Length)
            Array.Resize(ref _data, Math.Min(MaxSize, Math.Max(_data.Length * 2, _length + count)));
        Span<byte> span = _data.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public void WriteByte(int value)
    {
        Span<byte> s = GetSpace(1);
        if (!s.IsEmpty) s[0] = (byte)value;
    }

    public void WriteChar(int value) => WriteByte(value);

    public void WriteShort(int value)
    {
        Span<byte> s = GetSpace(2);
        if (s.IsEmpty) return;
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
    }

    public void WriteLong(int value)
    {
        Span<byte> s = GetSpace(4);
        if (s.IsEmpty) return;
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
        s[2] = (byte)(value >> 16);
        s[3] = (byte)(value >> 24);
    }

    /// <summary>StoreBigLong, for the netchan header.</summary>
    public void WriteBigLong(uint value)
    {
        Span<byte> s = GetSpace(4);
        if (s.IsEmpty) return;
        s[0] = (byte)(value >> 24);
        s[1] = (byte)(value >> 16);
        s[2] = (byte)(value >> 8);
        s[3] = (byte)value;
    }

    public void WriteFloat(float value) => WriteLong(BitConverter.SingleToInt32Bits(value));

    /// <summary>MSG_WriteString: the UTF-8 bytes and a NUL. A NUL inside the text would end the string
    /// early on the other side, so the text is cut there, which is what strlen() does to it in C.</summary>
    public void WriteString(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            WriteByte(0);
            return;
        }
        int nul = text.IndexOf('\0');
        ReadOnlySpan<char> chars = nul >= 0 ? text.AsSpan(0, nul) : text.AsSpan();
        int byteCount = Encoding.UTF8.GetByteCount(chars);
        Span<byte> s = GetSpace(byteCount + 1);
        if (s.IsEmpty) return;
        Encoding.UTF8.GetBytes(chars, s);
        s[byteCount] = 0;
    }

    /// <summary>SZ_Write.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        Span<byte> s = GetSpace(bytes.Length);
        if (!s.IsEmpty) bytes.CopyTo(s);
    }

    // Q_rint (mathlib.h): round half away from zero, then truncate. Math.Round would bank to even.
    private static int Rint(double x) => x > 0 ? (int)(x + 0.5) : (int)(x - 0.5);

    /// <summary>MSG_WriteCoord13i.</summary>
    public void WriteCoord13i(float value) => WriteShort(Rint(value * 8.0));
    /// <summary>MSG_WriteCoord16i. clc_move sends the three movement speeds this way.</summary>
    public void WriteCoord16i(float value) => WriteShort(Rint(value));
    /// <summary>MSG_WriteCoord for DP7.</summary>
    public void WriteCoord(float value) => WriteFloat(value);

    public void WriteVector(Vector3 value)
    {
        WriteFloat(value.X);
        WriteFloat(value.Y);
        WriteFloat(value.Z);
    }

    /// <summary>MSG_WriteAngle8i.</summary>
    public void WriteAngle8i(float degrees) => WriteByte(Rint(degrees * (256.0 / 360.0)) & 255);
    /// <summary>MSG_WriteAngle16i.</summary>
    public void WriteAngle16i(float degrees) => WriteShort(Rint(degrees * (65536.0 / 360.0)) & 65535);
    /// <summary>MSG_WriteAngle for DP7.</summary>
    public void WriteAngle(float degrees) => WriteAngle16i(degrees);
}

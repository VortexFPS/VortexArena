using System.Buffers.Binary;

namespace VortexArena.Modding;

/// <summary>
/// Decodes the buffer a guest hands to the <c>commands</c> import.
///
/// A guest that drew through one host call per primitive would pay the sandbox boundary every time:
/// measured on the Wasmtime .NET embedding at roughly 47 ns per guest-to-host call, against about
/// 1.4 ns per command when a thousand are written into guest memory and flushed in one call. So the
/// ABI has exactly one draw import, and this is what reads it.
///
/// Layout: a sequence of records, each <c>u16 opcode, u16 size, payload</c>, little-endian, where
/// <c>size</c> is the whole record including the 4-byte header and is a multiple of 4. The decoder
/// is all-or-nothing in spirit: the first malformed record stops decoding and reports failure, and
/// the caller disables the mod. It never throws on hostile input and never reads outside the span.
/// </summary>
public static class ModCommandDecoder
{
    public const int HeaderSize = 4;

    /// <summary>Returns the number of commands dispatched, or -1 if the buffer was malformed.</summary>
    public static int Decode(ReadOnlySpan<byte> buffer, IModCommandSink sink)
    {
        int count = 0;
        while (buffer.Length > 0)
        {
            if (buffer.Length < HeaderSize) return -1;
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]);
            if (size < HeaderSize || (size & 3) != 0 || size > buffer.Length) return -1;
            ReadOnlySpan<byte> p = buffer.Slice(HeaderSize, size - HeaderSize);

            switch ((ModCommand)opcode)
            {
                case ModCommand.DrawRect:
                    if (p.Length != 20 || !Finite(p, 4)) return -1;
                    sink.DrawRect(F(p, 0), F(p, 4), F(p, 8), F(p, 12), U(p, 16));
                    break;
                case ModCommand.DrawPic:
                    if (p.Length != 24 || !Finite(p[4..], 4)) return -1;
                    sink.DrawPic(I(p, 0), F(p, 4), F(p, 8), F(p, 12), F(p, 16), U(p, 20));
                    break;
                case ModCommand.DrawText:
                {
                    if (p.Length < 24 || !Finite(p[4..], 3)) return -1;
                    uint byteLength = U(p, 20);
                    // The text is padded to the record's 4-byte alignment, so the record may be up to
                    // 3 bytes longer than the text - but not shorter, and not arbitrarily longer.
                    if (byteLength > (uint)(p.Length - 24) || p.Length - 24 - (int)byteLength > 3) return -1;
                    sink.DrawText(I(p, 0), F(p, 4), F(p, 8), F(p, 12), U(p, 16), p.Slice(24, (int)byteLength));
                    break;
                }
                case ModCommand.SetClip:
                    if (p.Length != 16 || !Finite(p, 4)) return -1;
                    sink.SetClip(F(p, 0), F(p, 4), F(p, 8), F(p, 12));
                    break;
                case ModCommand.ResetClip:
                    if (p.Length != 0) return -1;
                    sink.ResetClip();
                    break;
                case ModCommand.PlaySound:
                    if (p.Length != 16 || !Finite(p[8..], 2)) return -1;
                    sink.PlaySound(I(p, 0), I(p, 4), F(p, 8), F(p, 12));
                    break;
                default:
                    return -1;
            }

            count++;
            buffer = buffer[size..];
        }
        return count;
    }

    private static int I(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadInt32LittleEndian(p[at..]);
    private static uint U(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadUInt32LittleEndian(p[at..]);
    private static float F(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadSingleLittleEndian(p[at..]);

    // NaN and infinity are rejected here so that no renderer downstream has to wonder what a
    // rectangle at x = NaN means.
    private static bool Finite(ReadOnlySpan<byte> p, int floats)
    {
        for (int i = 0; i < floats; i++)
            if (!float.IsFinite(F(p, i * 4))) return false;
        return true;
    }
}

/// <summary>Builds a command buffer. The host does not need one; tests and the C# guest SDK share this layout.</summary>
public sealed class ModCommandWriter
{
    private byte[] _buffer = new byte[256];
    private int _length;

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);
    public void Clear() => _length = 0;

    public void DrawRect(float x, float y, float w, float h, uint rgba)
    {
        Span<byte> p = Begin(ModCommand.DrawRect, 20);
        W(p, 0, x); W(p, 4, y); W(p, 8, w); W(p, 12, h); W(p, 16, rgba);
    }

    public void DrawPic(int assetId, float x, float y, float w, float h, uint rgba)
    {
        Span<byte> p = Begin(ModCommand.DrawPic, 24);
        W(p, 0, assetId); W(p, 4, x); W(p, 8, y); W(p, 12, w); W(p, 16, h); W(p, 20, rgba);
    }

    public void DrawText(int fontId, float x, float y, float size, uint rgba, ReadOnlySpan<byte> utf8)
    {
        int padded = (utf8.Length + 3) & ~3;
        Span<byte> p = Begin(ModCommand.DrawText, 24 + padded);
        W(p, 0, fontId); W(p, 4, x); W(p, 8, y); W(p, 12, size); W(p, 16, rgba); W(p, 20, (uint)utf8.Length);
        utf8.CopyTo(p[24..]);
        p.Slice(24 + utf8.Length).Clear();
    }

    public void SetClip(float x, float y, float w, float h)
    {
        Span<byte> p = Begin(ModCommand.SetClip, 16);
        W(p, 0, x); W(p, 4, y); W(p, 8, w); W(p, 12, h);
    }

    public void ResetClip() => Begin(ModCommand.ResetClip, 0);

    public void PlaySound(int assetId, int channel, float volume, float pitch)
    {
        Span<byte> p = Begin(ModCommand.PlaySound, 16);
        W(p, 0, assetId); W(p, 4, channel); W(p, 8, volume); W(p, 12, pitch);
    }

    private Span<byte> Begin(ModCommand opcode, int payload)
    {
        int size = ModCommandDecoder.HeaderSize + payload;
        if (size > ushort.MaxValue) throw new ArgumentException("command record exceeds 65535 bytes");
        if (_length + size > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + size));
        Span<byte> record = _buffer.AsSpan(_length, size);
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(record[2..], (ushort)size);
        _length += size;
        return record[ModCommandDecoder.HeaderSize..];
    }

    private static void W(Span<byte> p, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(p[at..], v);
    private static void W(Span<byte> p, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(p[at..], v);
    private static void W(Span<byte> p, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(p[at..], v);
}

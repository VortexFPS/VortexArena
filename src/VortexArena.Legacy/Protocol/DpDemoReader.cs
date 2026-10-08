// Port of Base/darkplaces/cl_demo.c CL_ReadDemoMessage (the block format) and CL_PlayDemo (the header
// line), matching what CL_Record_f and CL_WriteDemoMessage write.
using System.Buffers.Binary;

namespace VortexArena.Legacy.Protocol;

/// <summary>One recorded server message.</summary>
public readonly struct DpDemoMessage
{
    /// <summary>The player's view angles when the message arrived (pitch, yaw, roll in degrees).</summary>
    public System.Numerics.Vector3 ViewAngles { get; init; }
    /// <summary>The message exactly as the netchan delivered it; feed it to <see cref="DpServerMessageParser"/>.</summary>
    public byte[] Data { get; init; }
    /// <summary>Byte offset of this block's length field in the file.</summary>
    public long FileOffset { get; init; }
}

/// <summary>
/// Reads a DarkPlaces <c>.dem</c> recording. The format is NetQuake's:
///
/// <code>
/// text line   the forced CD track as a decimal number, "-1" for none, ended by '\n'
/// repeat:
///   int32     message length, little-endian
///   float[3]  view angles
///   bytes     the server message
/// </code>
///
/// A length with the top bit set (DEMOMSG_CLIENT_TO_SERVER) marks a recorded client-to-server
/// message, which DarkPlaces skips on playback and so does this.
///
/// A demo is the byte stream a client received, so it exercises the same parser a live connection
/// does. Two things it leaves out: the netchan (messages are already reassembled) and anything the
/// client sent.
/// </summary>
public sealed class DpDemoReader
{
    private const uint ClientToServer = 0x80000000; // DEMOMSG_CLIENT_TO_SERVER (quakedef.h)

    private readonly Stream _stream;

    public DpDemoReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ForceTrack = ReadHeader();
    }

    /// <summary>cls.forcetrack: the CD track the recording wants played, or -1.</summary>
    public int ForceTrack { get; }

    /// <summary>Set when the file ended in the middle of a block, or a block claimed an impossible
    /// length. A demo cut off by a crash ends this way; everything before it is still good.</summary>
    public string? Error { get; private set; }

    /// <summary>Client-to-server blocks skipped so far.</summary>
    public int SkippedClientMessages { get; private set; }

    // CL_PlayDemo reads characters up to '\n', accumulating digits and noting a '-'.
    private int ReadHeader()
    {
        int track = 0;
        bool negative = false;
        // A real header is at most a few characters. Bounding the scan keeps a file that is not a
        // demo from being read to its end in search of a newline.
        for (int i = 0; i < 16; i++)
        {
            int c = _stream.ReadByte();
            if (c < 0)
            {
                Error = "demo ended inside its header line";
                break;
            }
            if (c == '\n')
                return negative ? -track : track;
            if (c == '-')
                negative = true;
            else if (c >= '0' && c <= '9')
                track = track * 10 + (c - '0');
        }
        Error ??= "demo header line is not a track number";
        return -1;
    }

    /// <summary>Read the next server message. False at the end of the file, or on <see cref="Error"/>.</summary>
    public bool TryReadMessage(out DpDemoMessage message)
    {
        message = default;
        if (Error is not null)
            return false;
        Span<byte> header = stackalloc byte[16];
        while (true)
        {
            long offset = _stream.CanSeek ? _stream.Position : -1;
            int got = ReadFully(header[..4]);
            if (got == 0)
                return false; // clean end of file
            if (got < 4)
                return Fail("demo ended inside a block length");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if ((length & ClientToServer) != 0)
            {
                // skip over demo packet: 12 bytes of angles and the message
                long skip = 12 + (long)(length & ~ClientToServer);
                if (!Skip(skip))
                    return Fail("demo ended inside a client-to-server block");
                SkippedClientMessages++;
                continue;
            }
            if (length > DpProtocol.NetMaxMessage)
                return Fail($"Demo message ({length}) > cl_message.maxsize ({DpProtocol.NetMaxMessage})");
            if (ReadFully(header.Slice(4, 12)) < 12)
                return Fail("demo ended inside a block's view angles");
            var data = new byte[length];
            if (ReadFully(data) < data.Length)
                return Fail("demo ended inside a message");
            message = new DpDemoMessage
            {
                ViewAngles = new System.Numerics.Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(header.Slice(4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(header.Slice(8, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(header.Slice(12, 4))),
                Data = data,
                FileOffset = offset,
            };
            return true;
        }
    }

    private bool Fail(string error)
    {
        Error = error;
        return false;
    }

    private int ReadFully(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _stream.Read(buffer[total..]);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    private bool Skip(long count)
    {
        if (_stream.CanSeek)
        {
            if (_stream.Position + count > _stream.Length)
                return false;
            _stream.Seek(count, SeekOrigin.Current);
            return true;
        }
        Span<byte> scratch = stackalloc byte[512];
        while (count > 0)
        {
            int n = _stream.Read(scratch[..(int)Math.Min(count, scratch.Length)]);
            if (n <= 0)
                return false;
            count -= n;
        }
        return true;
    }
}

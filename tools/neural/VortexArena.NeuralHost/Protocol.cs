using System.Buffers.Binary;
using System.Net.Sockets;

namespace VortexArena.NeuralHost;

/// <summary>
/// The wire protocol between this host and the Python trainer.
///
/// <para><b>Why a localhost socket and not shared memory.</b> At the measured throughput the traffic is
/// roughly 120 MB/s of observations, which loopback TCP carries without noticing. Shared memory would be
/// faster on paper and would cost a cross-platform synchronisation primitive shared between .NET and
/// CPython, which is a category of bug that shows up as a training run that hangs after four hours. A
/// socket is debuggable with tcpdump and works identically on all three platforms.</para>
///
/// <para>Every message is <c>[u8 opcode][u32 payload length][payload]</c>, little-endian throughout, which
/// is the byte order of every platform this ships on and of numpy's default.</para>
/// </summary>
public enum OpCode : byte
{
    /// <summary>Trainer to host: version, agents, ticks-per-step, stage, seed and the episode flags.</summary>
    Hello = 1,

    /// <summary>Host to trainer: observation size, action size, agent count.</summary>
    HelloAck = 2,

    /// <summary>Trainer to host: start a fresh episode. Payload empty.</summary>
    Reset = 3,

    /// <summary>Host to trainer: observations for every agent.</summary>
    Observation = 4,

    /// <summary>Trainer to host: one action per agent.</summary>
    Step = 5,

    /// <summary>Host to trainer: observations, rewards, done flags, truncation flags.</summary>
    StepResult = 6,

    /// <summary>Trainer to host: switch curriculum stage. Payload is one i32.</summary>
    SetStage = 7,

    /// <summary>Host to trainer: the finished episode's arrival stats.</summary>
    EpisodeStats = 8,

    /// <summary>Trainer to host: shut down.</summary>
    Close = 9,

    /// <summary>Host to trainer: something went wrong; payload is a UTF-8 message.</summary>
    Error = 10,
}

/// <summary>Length-prefixed framing over a <see cref="NetworkStream"/>.</summary>
public sealed class Frames
{
    /// <summary>
    /// Refuse a frame larger than this. A trainer bug that sends a garbage length would otherwise make the
    /// host try to allocate several gigabytes and die with an OOM that says nothing about the cause.
    /// </summary>
    public const int MaxFrame = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[5];
    private byte[] _buffer = new byte[64 * 1024];

    public Frames(Stream stream) => _stream = stream;

    public void Write(OpCode op, ReadOnlySpan<byte> payload)
    {
        _header[0] = (byte)op;
        BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(1), (uint)payload.Length);
        _stream.Write(_header);
        if (payload.Length > 0) _stream.Write(payload);
        _stream.Flush();
    }

    /// <summary>
    /// Read one frame. The returned span points into a reused buffer and is valid until the next read,
    /// which keeps a 30 Hz message loop from allocating a fresh array per step.
    /// </summary>
    public bool TryRead(out OpCode op, out ReadOnlySpan<byte> payload)
    {
        op = default;
        payload = default;
        if (!ReadExact(_header, 5)) return false;

        op = (OpCode)_header[0];
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(1));
        if (len < 0 || len > MaxFrame)
            throw new InvalidDataException($"frame length {len} is out of range");

        if (len > _buffer.Length) _buffer = new byte[Math.Max(len, _buffer.Length * 2)];
        if (len > 0 && !ReadExact(_buffer, len)) return false;

        payload = _buffer.AsSpan(0, len);
        return true;
    }

    private bool ReadExact(byte[] dest, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(dest, read, count - read);
            if (n <= 0) return false;   // peer closed
            read += n;
        }
        return true;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace VortexArena.Modding;

/// <summary>
/// The kinds of frame in the mod-offer protocol (planning/specs/modding.md, section 9). Every frame is
/// <c>u8 kind, u8 offerSequence, body</c> and travels on the game connection's reliable, ordered channel
/// inside one envelope message, so the game protocol needs exactly one new message id for all of this.
/// </summary>
public enum ModFrameKind : byte
{
    /// <summary>Server to client: "this server has a mod" - the transfer version and the manifest.</summary>
    Offer = 1,
    /// <summary>Client to server: the files the client does not have, by SHA-256.</summary>
    Need = 2,
    /// <summary>Server to client: the next piece of a requested file.</summary>
    Chunk = 3,
    /// <summary>Client to server: the mod is verified, loaded and running.</summary>
    Ready = 4,
    /// <summary>Client to server: the client will not run the mod, and why.</summary>
    Decline = 5,
    /// <summary>Server to client: the offer is withdrawn (the server cannot serve a file, or is dropping the mod).</summary>
    Abort = 6,
    /// <summary>Client to server: a message from the mod to the server half of the mod.</summary>
    ToServer = 7,
    /// <summary>Server to client: a message from the server half of the mod to the mod.</summary>
    ToClient = 8,
}

/// <summary>Why a client is not running the offered mod. Sent in <see cref="ModFrameKind.Decline"/>.</summary>
public enum ModDeclineReason : byte
{
    None = 0,
    /// <summary>The player has mods switched off (<c>cl_allow_mods 0</c>). The manifest was not even parsed.</summary>
    ModsDisabled = 1,
    /// <summary>This client cannot run mods at all (no WebAssembly runtime for its platform).</summary>
    Unsupported = 2,
    /// <summary>The offer uses a transfer version this client does not speak.</summary>
    UnsupportedTransferVersion = 3,
    BadManifest = 4,
    /// <summary>The mod was built against a guest interface this client does not provide.</summary>
    UnsupportedAbi = 5,
    /// <summary>The manifest was made for a different base protocol than this connection uses.</summary>
    ProtocolMismatch = 6,
    ConsentDenied = 7,
    /// <summary>The download is larger than the player allows.</summary>
    TooLarge = 8,
    /// <summary>A file arrived out of order, oversized, or with the wrong SHA-256, or could not be stored.</summary>
    DownloadFailed = 9,
    Timeout = 10,
    /// <summary>The module was refused by the sandbox or failed while starting.</summary>
    LoadFailed = 11,
    /// <summary>The mod was running and has been disabled (a trap, a budget overrun, an interface violation).</summary>
    Faulted = 12,
    Cancelled = 13,
    /// <summary>Too many offers in a short time; this one was not looked at.</summary>
    Busy = 14,
}

/// <summary>A parsed mod-offer frame. The spans point into the buffer that was parsed.</summary>
public readonly ref struct ModFrame
{
    public ModFrameKind Kind { get; init; }
    public byte Sequence { get; init; }
    /// <summary>Offer: the server's transfer version. Decline: the client's.</summary>
    public byte Version { get; init; }
    public ModDeclineReason Reason { get; init; }
    /// <summary>Chunk: which entry of the client's Need list this piece belongs to.</summary>
    public byte Slot { get; init; }
    /// <summary>Chunk: where in the file this piece starts.</summary>
    public uint Offset { get; init; }
    public int EventId { get; init; }
    /// <summary>
    /// Offer: the manifest JSON. Need: the hashes, 32 bytes each. Chunk: the piece. Decline and Abort: a
    /// short UTF-8 explanation. ToServer and ToClient: the payload.
    /// </summary>
    public ReadOnlySpan<byte> Body { get; init; }

    /// <summary>Need: how many hashes <see cref="Body"/> holds.</summary>
    public int HashCount => Body.Length / ModWire.HashBytes;
    public ReadOnlySpan<byte> Hash(int index) => Body.Slice(index * ModWire.HashBytes, ModWire.HashBytes);
}

/// <summary>
/// Encoding and strict decoding of the mod-offer frames. Both ends treat the other as hostile: the
/// client because the server is a stranger's, the server because any client can send any bytes. A frame
/// that is too short, too long, or carries a count or length outside its cap fails to parse as a whole;
/// nothing here allocates in proportion to a number read from the wire.
/// </summary>
public static class ModWire
{
    /// <summary>
    /// Version of this transfer protocol - the frames in this file, not the guest interface (that is the
    /// manifest's <c>abi</c>). An offer carries it first so a client that does not speak it can say so
    /// without trying to read the rest.
    /// </summary>
    public const byte TransferVersion = 1;

    public const int HeaderBytes = 2;
    public const int HashBytes = 32;
    /// <summary>Largest piece of a file in one frame. Small enough that a frame never monopolises the reliable channel.</summary>
    public const int MaxChunkBytes = 16 * 1024;
    /// <summary>A manifest names at most one module and <see cref="ModManifest.MaxAssetPacks"/> packs.</summary>
    public const int MaxNeedCount = ModManifest.MaxAssetPacks + 1;
    public const int MaxReasonBytes = 200;
    /// <summary>Largest message a mod may send to the server.</summary>
    public const int MaxToServerBytes = 1024;
    /// <summary>Largest message the server may send to a mod.</summary>
    public const int MaxToClientBytes = 4096;
    /// <summary>The largest frame of any kind: an offer carrying a full-size manifest.</summary>
    public const int MaxFrameBytes = HeaderBytes + 1 + ModManifest.MaxManifestBytes;

    public static byte[] Offer(byte sequence, ReadOnlySpan<byte> manifestJson, byte transferVersion = TransferVersion)
    {
        if (manifestJson.Length > ModManifest.MaxManifestBytes) throw new ArgumentException("manifest exceeds the size limit", nameof(manifestJson));
        byte[] frame = Begin(ModFrameKind.Offer, sequence, 1 + manifestJson.Length);
        frame[HeaderBytes] = transferVersion;
        manifestJson.CopyTo(frame.AsSpan(HeaderBytes + 1));
        return frame;
    }

    /// <param name="sha256Hex">Lower-case hex hashes, as they appear in the manifest.</param>
    public static byte[] Need(byte sequence, IReadOnlyList<string> sha256Hex)
    {
        if (sha256Hex.Count is 0 or > MaxNeedCount) throw new ArgumentException("a Need frame lists between 1 and 65 files", nameof(sha256Hex));
        byte[] frame = Begin(ModFrameKind.Need, sequence, 1 + sha256Hex.Count * HashBytes);
        frame[HeaderBytes] = (byte)sha256Hex.Count;
        for (int i = 0; i < sha256Hex.Count; i++)
        {
            if (!ModManifest.IsSha256(sha256Hex[i])) throw new ArgumentException("not a SHA-256", nameof(sha256Hex));
            Convert.FromHexString(sha256Hex[i]).CopyTo(frame.AsSpan(HeaderBytes + 1 + i * HashBytes));
        }
        return frame;
    }

    public static byte[] Chunk(byte sequence, byte slot, uint offset, ReadOnlySpan<byte> piece)
    {
        if (piece.Length is 0 or > MaxChunkBytes) throw new ArgumentException("a chunk carries between 1 and 16384 bytes", nameof(piece));
        byte[] frame = Begin(ModFrameKind.Chunk, sequence, 5 + piece.Length);
        frame[HeaderBytes] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(HeaderBytes + 1), offset);
        piece.CopyTo(frame.AsSpan(HeaderBytes + 5));
        return frame;
    }

    public static byte[] Ready(byte sequence) => Begin(ModFrameKind.Ready, sequence, 0);

    public static byte[] Decline(byte sequence, ModDeclineReason reason, string? text = null)
    {
        byte[] utf8 = ReasonBytes(text);
        byte[] frame = Begin(ModFrameKind.Decline, sequence, 2 + utf8.Length);
        frame[HeaderBytes] = (byte)reason;
        frame[HeaderBytes + 1] = TransferVersion;
        utf8.CopyTo(frame.AsSpan(HeaderBytes + 2));
        return frame;
    }

    public static byte[] Abort(byte sequence, string? text = null)
    {
        byte[] utf8 = ReasonBytes(text);
        byte[] frame = Begin(ModFrameKind.Abort, sequence, utf8.Length);
        utf8.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static byte[] ToServer(byte sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxToServerBytes) throw new ArgumentException("message exceeds the size limit", nameof(payload));
        byte[] frame = Begin(ModFrameKind.ToServer, sequence, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static byte[] ToClient(byte sequence, int eventId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxToClientBytes) throw new ArgumentException("message exceeds the size limit", nameof(payload));
        byte[] frame = Begin(ModFrameKind.ToClient, sequence, 4 + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(HeaderBytes), eventId);
        payload.CopyTo(frame.AsSpan(HeaderBytes + 4));
        return frame;
    }

    /// <summary>
    /// Parses one frame. False means the bytes are not a well-formed frame of any kind; the caller
    /// decides what that costs the sender. A well-formed frame can still be wrong for the moment (a chunk
    /// nobody asked for) - that is the state machines' business, not the parser's.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out ModFrame parsed)
    {
        parsed = default;
        if (frame.Length < HeaderBytes || frame.Length > MaxFrameBytes) return false;
        ModFrameKind kind = (ModFrameKind)frame[0];
        byte sequence = frame[1];
        ReadOnlySpan<byte> body = frame[HeaderBytes..];

        switch (kind)
        {
            case ModFrameKind.Offer:
                if (body.Length < 1) return false;
                parsed = new ModFrame { Kind = kind, Sequence = sequence, Version = body[0], Body = body[1..] };
                return true;

            case ModFrameKind.Need:
            {
                if (body.Length < 1) return false;
                int count = body[0];
                if (count is 0 or > MaxNeedCount || body.Length != 1 + count * HashBytes) return false;
                parsed = new ModFrame { Kind = kind, Sequence = sequence, Body = body[1..] };
                return true;
            }

            case ModFrameKind.Chunk:
                if (body.Length < 6 || body.Length > 5 + MaxChunkBytes) return false;
                parsed = new ModFrame
                {
                    Kind = kind, Sequence = sequence, Slot = body[0],
                    Offset = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), Body = body[5..],
                };
                return true;

            case ModFrameKind.Ready:
                if (body.Length != 0) return false;
                parsed = new ModFrame { Kind = kind, Sequence = sequence };
                return true;

            case ModFrameKind.Decline:
                if (body.Length < 2 || body.Length > 2 + MaxReasonBytes) return false;
                parsed = new ModFrame { Kind = kind, Sequence = sequence, Reason = (ModDeclineReason)body[0], Version = body[1], Body = body[2..] };
                return true;

            case ModFrameKind.Abort:
                if (body.Length > MaxReasonBytes) return false;
                parsed = new ModFrame { Kind = kind, Sequence = sequence, Body = body };
                return true;

            case ModFrameKind.ToServer:
                if (body.Length > MaxToServerBytes) return false;
                parsed = new ModFrame { Kind = kind, Sequence = sequence, Body = body };
                return true;

            case ModFrameKind.ToClient:
                if (body.Length < 4 || body.Length > 4 + MaxToClientBytes) return false;
                parsed = new ModFrame { Kind = kind, Sequence = sequence, EventId = BinaryPrimitives.ReadInt32LittleEndian(body), Body = body[4..] };
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Text from the other end, made safe to print and log: decoded leniently, control characters
    /// replaced, length capped. The other end chose every byte of it.
    /// </summary>
    public static string SafeText(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxReasonBytes) utf8 = utf8[..MaxReasonBytes];
        string text = Encoding.UTF8.GetString(utf8);
        StringBuilder clean = new(text.Length);
        foreach (char c in text)
            clean.Append(char.IsControl(c) || c == '�' || char.IsSurrogate(c) ? '?' : c);
        return clean.ToString();
    }

    public static string HashHex(ReadOnlySpan<byte> hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private static byte[] Begin(ModFrameKind kind, byte sequence, int bodyLength)
    {
        byte[] frame = new byte[HeaderBytes + bodyLength];
        frame[0] = (byte)kind;
        frame[1] = sequence;
        return frame;
    }

    private static byte[] ReasonBytes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        if (utf8.Length <= MaxReasonBytes) return utf8;
        // Cut on a character boundary so the receiver does not see a broken sequence at the end.
        int length = MaxReasonBytes;
        while (length > 0 && (utf8[length] & 0xC0) == 0x80) length--;
        return utf8.AsSpan(0, length).ToArray();
    }
}

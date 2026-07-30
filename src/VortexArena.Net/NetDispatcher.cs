namespace XonoticGodot.Net;

/// <summary>
/// Handler invoked when a message of a given id arrives and has been deserialized. The
/// <paramref name="message"/> is a pooled instance owned by the dispatcher — read it synchronously;
/// do not retain it past the callback (copy out what you need). <see cref="NetMessage"/> analogue of a
/// QC <c>m_read</c> body's "act on the parsed entity" tail.
/// </summary>
public delegate void NetMessageHandler(NetMessage message);

/// <summary>
/// Routes incoming messages to handlers by their byte id, and frames outgoing messages with that id —
/// the C# successor to the QC dispatch in <c>CSQC_Ent_Update</c> (Linked), <c>CSQC_Parse_TempEntity</c>
/// (Temp) and <c>Net_ClientCommand</c> (C2S). One flat table over the whole <see cref="NetMessageId"/>
/// byte space (we collapsed the three QC registries — see <see cref="NetMessageRanges"/>).
///
/// Allocation discipline: each registered id owns a single reusable <see cref="NetMessage"/> instance
/// (a factory builds it once). On receive we deserialize into that instance and hand it to the handler;
/// no per-message heap allocation on the parse path. This is the hot loop the networking spec calls out.
///
/// A registry/source-generator can later populate this from <c>[Register]</c>-attributed message types
/// (ADR-0003); the manual <see cref="Register"/> path keeps the core Godot-free and testable now.
/// </summary>
public sealed class NetDispatcher
{
    // Indexed by (byte)NetMessageId. Parallel arrays keep the per-id state branch-free to look up.
    private readonly NetMessage?[] _prototype = new NetMessage?[256];
    private readonly NetMessageHandler?[] _handler = new NetMessageHandler?[256];

    /// <summary>Raised when an unknown/unregistered id is read, mirroring the QC
    /// <c>LOG_SEVEREF("...malformed C2S=%d")</c> / dropped-temp-entity diagnostics. Arg is the raw id byte.</summary>
    public event Action<int>? OnUnknownMessage;

    /// <summary>Raised when the reader underflowed while parsing a message body (DP <c>badread</c>).
    /// Arg is the message id whose parse failed.</summary>
    public event Action<NetMessageId>? OnBadRead;

    /// <summary>
    /// Register a message type by id: <paramref name="prototype"/> is the single reusable instance the
    /// dispatcher deserializes into, <paramref name="handler"/> is invoked after a successful parse.
    /// Idempotent-ish: re-registering an id overwrites it (last wins), like a registry re-bind.
    /// </summary>
    public void Register(NetMessage prototype, NetMessageHandler handler)
    {
        int id = (byte)prototype.Id;
        _prototype[id] = prototype;
        _handler[id] = handler;
    }

    /// <summary>True if an id has a registered prototype + handler.</summary>
    public bool IsRegistered(NetMessageId id) => _prototype[(byte)id] is not null;

    // ---------------------------------------------------------------------
    // sending
    // ---------------------------------------------------------------------

    /// <summary>
    /// Frame and write one message: emits the id byte header (port of <c>WriteHeader</c>) then the
    /// payload via <see cref="NetMessage.Serialize"/>. The caller owns the <paramref name="writer"/>
    /// (typically reset once per packet, then several messages appended).
    /// </summary>
    public static void Write(BitWriter writer, NetMessage message)
    {
        writer.WriteByte((byte)message.Id);
        message.Serialize(writer);
    }

    // ---------------------------------------------------------------------
    // receiving
    // ---------------------------------------------------------------------

    /// <summary>
    /// Read exactly one message: an id byte then its body, deserialize into the registered prototype,
    /// and invoke its handler. Returns false (and raises <see cref="OnUnknownMessage"/>/<see cref="OnBadRead"/>)
    /// on an unknown id or a truncated body. Mirrors one iteration of the QC parse loop.
    /// </summary>
    public bool ReadOne(ref BitReader reader)
    {
        if (!reader.CanRead) return false;
        int id = reader.ReadByte();
        NetMessage? proto = _prototype[id];
        if (proto is null)
        {
            OnUnknownMessage?.Invoke(id);
            return false;
        }

        proto.Deserialize(ref reader);
        if (reader.BadRead)
        {
            OnBadRead?.Invoke((NetMessageId)id);
            return false;
        }

        _handler[id]?.Invoke(proto);
        return true;
    }

    /// <summary>
    /// Drain a whole packet: repeatedly <see cref="ReadOne"/> until the buffer is exhausted or a parse
    /// fails. Returns the count of successfully dispatched messages. This is the packet-level analogue of
    /// the <c>for (int C2S; (C2S = ReadByte()) >= 0; )</c> loop in <c>Net_ClientCommand</c>.
    /// </summary>
    public int ReadAll(ref BitReader reader)
    {
        int count = 0;
        while (reader.CanRead)
        {
            if (!ReadOne(ref reader)) break;
            count++;
        }
        return count;
    }
}

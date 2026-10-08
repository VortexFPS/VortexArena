// Port of Base/darkplaces/netconn.c NetConn_SendUnreliableMessage (the non-QuakeWorld branch,
// lines 939-1063), NetConn_CanSend and NetConn_UpdateCleartime (lines 803-835) and
// NetConn_ReceivedMessage (lines 1453-1651), with the header layout of netconn.h.
namespace VortexArena.Legacy.Protocol;

/// <summary>What <see cref="DpNetChannel.Receive"/> made of a datagram (the 0/1/2 return of NetConn_ReceivedMessage).</summary>
public enum DpChannelReceive
{
    /// <summary>Not a datagram for this channel: too short, wrong length field, or a control packet.</summary>
    Invalid = 0,
    /// <summary>Consumed, nothing to parse: an ACK, a fragment, a duplicate, or a stale datagram.</summary>
    Handled = 1,
    /// <summary>A complete game message is ready in the <c>message</c> out parameter.</summary>
    Message = 2,
}

/// <summary>
/// The NetQuake-style channel DarkPlaces runs its game protocol over. Every datagram starts with two
/// big-endian 32-bit words: flags in the top half of the first with the datagram length (header
/// included) in the bottom half, then a sequence number.
///
/// There are two independent streams. Unreliable datagrams carry a counter and the receiver simply
/// drops anything older than the newest it has seen. Reliable messages are stop-and-wait: the message
/// is cut into fragments of at most 1024 bytes, one fragment is in flight at a time, the receiver
/// ACKs every DATA datagram it sees (duplicates included, since the ACK may be what was lost), and
/// the sender repeats the fragment if a second passes without the ACK.
///
/// No socket and no clock in here: datagrams come in through <see cref="Receive"/>, datagrams to send
/// are appended to the caller's list, and the time is whatever the caller says it is.
///
/// The rate limiter is the "clear time" of NetConn_UpdateCleartime: every datagram sent pushes forward
/// the moment at which the line is considered clear again, by its size over the rate, and
/// <see cref="CanSend"/> says whether that moment has passed. It limits nothing by itself - the caller
/// asks before sending (cl_input.c CL_SendMove does, and sends anyway when the input is important).
///
/// Left out, knowingly: the netgraph bookkeeping, and encryption (the NETFLAG_CRYPTO* bits and
/// Crypto_EncryptPacket).
/// </summary>
public sealed class DpNetChannel
{
    /// <summary>Seconds without an ACK before the reliable fragment in flight is sent again (netconn.c:957).</summary>
    public const double ResendInterval = 1.0;

    /// <summary>What NetConn_SendUnreliableMessage adds to every datagram's length when charging it
    /// against the rate: the IPv4 and UDP headers (20 + 8 bytes).</summary>
    public const int PacketOverhead = 28;

    // conn->sendMessage / sendMessageLength: the reliable message being transmitted, shrinking from the
    // front as fragments are acknowledged.
    private readonly byte[] _sendMessage = new byte[DpProtocol.NetMaxMessage];
    private int _sendMessageLength;
    // conn->receiveMessage / receiveMessageLength: fragments of the incoming reliable message so far.
    private readonly byte[] _receiveMessage = new byte[DpProtocol.NetMaxMessage];
    private int _receiveMessageLength;

    // conn->nq.*
    private uint _sendSequence;
    private uint _ackSequence;
    private uint _receiveSequence;
    private uint _unreliableReceiveSequence;
    private uint _outgoingUnreliableSequence;
    private double _lastSendTime;
    // conn->cleartime: when the rate limit next allows a datagram.
    private double _clearTime;

    public DpNetChannel(double now)
    {
        LastMessageTime = now;
    }

    /// <summary>conn->message: reliable data waiting for the message in flight to finish. Callers append
    /// to it freely; it becomes the next reliable message as a whole.</summary>
    public DpMessageWriter Reliable { get; } = new(DpProtocol.NetMaxMessage);

    /// <summary>
    /// NetConn_SendUnreliableMessage's quakesignon_suppressreliables argument: while set, a new
    /// reliable message is not started (one already in flight is still resent). A server sets it
    /// between sending svc_serverinfo and hearing the client's first command, so that nothing else
    /// reliable reaches a client that is still loading the level. False by default.
    /// </summary>
    public bool SuppressNewReliables { get; set; }

    /// <summary>True while a reliable message is in flight (conn->sendMessageLength != 0).</summary>
    public bool ReliableInFlight => _sendMessageLength != 0;
    /// <summary>The sequence number the next unreliable datagram will carry. DarkPlaces stamps each
    /// input command with this (cl.cmd.sequence), which is how the server tells the client which input
    /// its last update already includes.</summary>
    public uint OutgoingUnreliableSequence => _outgoingUnreliableSequence;
    /// <summary>When the last in-sequence datagram arrived (conn->lastMessageTime), for timeout checks.</summary>
    public double LastMessageTime { get; private set; }

    public int DroppedDatagrams { get; private set; }
    public int DuplicatesReceived { get; private set; }
    public int PacketsResent { get; private set; }
    public int ReliableMessagesReceived { get; private set; }
    public int ReliableMessagesSent { get; private set; }
    /// <summary>How often <see cref="CanSend"/> answered no (NETGRAPH_CHOKEDPACKET).</summary>
    public int PacketsChoked { get; private set; }
    /// <summary>conn->cleartime: the moment from which <see cref="CanSend"/> answers yes.</summary>
    public double ClearTime => _clearTime;

    /// <summary>
    /// NetConn_CanSend: whether the rate limit allows a datagram now. Strictly later than the clear
    /// time, as in the C, so two sends at the same instant are never both allowed once the burst
    /// allowance is used up.
    /// </summary>
    public bool CanSend(double now)
    {
        if (now > _clearTime)
            return true;
        PacketsChoked++;
        return false;
    }

    // NetConn_UpdateCleartime. A connection that has been quiet is credited at most burstSize bytes:
    // the clear time is first pulled up to "one burst ago", then pushed on by what was just sent.
    // (net_test's "dialup mode" branch is a debugging cvar and is not here.)
    private void UpdateClearTime(double now, int rate, int burstSize, int length)
    {
        if (rate <= 0)
            return;
        double burstTime = burstSize / (double)rate;
        // delay later packets to obey rate limit
        if (_clearTime < now - burstTime)
            _clearTime = now - burstTime;
        _clearTime += length / (double)rate;
    }

    /// <summary>True when the reliable fragment in flight has gone <see cref="ResendInterval"/>
    /// without its ACK, so the next Transmit will repeat it.</summary>
    public bool ReliableResendDue(double now) => _sendMessageLength != 0 && now - _lastSendTime > ResendInterval;

    /// <summary>
    /// <see cref="Transmit(ReadOnlySpan{byte}, double, List{byte[]})"/>, charged against the rate limit as
    /// NetConn_SendUnreliableMessage charges it: every datagram produced counts its length plus
    /// <see cref="PacketOverhead"/>.
    /// </summary>
    /// <param name="rate">Bytes per second (cl_rate, which CL_SendMove raises to 20 * (size + 40)).</param>
    /// <param name="burstSize">cl_rate_burstsize: bytes a quiet connection may send at once.</param>
    public bool Transmit(ReadOnlySpan<byte> unreliable, double now, List<byte[]> outgoing, int rate, int burstSize)
    {
        int before = outgoing.Count;
        bool ok = Transmit(unreliable, now, outgoing);
        int total = 0;
        for (int i = before; i < outgoing.Count; i++)
            total += outgoing[i].Length + PacketOverhead;
        UpdateClearTime(now, rate, burstSize, total);
        return ok;
    }

    /// <summary>
    /// NetConn_SendUnreliableMessage. In this order: repeat the reliable fragment if its ACK is overdue,
    /// start the next reliable message if none is in flight, then send <paramref name="unreliable"/>
    /// if it is not empty. Each datagram produced is appended to <paramref name="outgoing"/>.
    /// Returns false, sending nothing further, if a message is too large to be framed.
    /// </summary>
    public bool Transmit(ReadOnlySpan<byte> unreliable, double now, List<byte[]> outgoing)
    {
        // if a reliable message fragment has been lost, send it again
        if (_sendMessageLength != 0 && now - _lastSendTime > ResendInterval)
        {
            // The sequence was already advanced when this fragment first went out, hence the - 1.
            outgoing.Add(BuildFragment(_sendSequence - 1));
            _lastSendTime = now;
            PacketsResent++;
        }

        // if we have a new reliable message to send, do so
        if (_sendMessageLength == 0 && Reliable.Length != 0 && !SuppressNewReliables)
        {
            if (Reliable.Overflowed || Reliable.Length > _sendMessage.Length)
                return false;
            Reliable.WrittenSpan.CopyTo(_sendMessage);
            _sendMessageLength = Reliable.Length;
            Reliable.Clear();
            outgoing.Add(BuildFragment(_sendSequence));
            _sendSequence++;
            _lastSendTime = now;
            ReliableMessagesSent++;
        }

        // if we have an unreliable message to send, do so
        if (!unreliable.IsEmpty)
        {
            if (unreliable.Length > DpProtocol.NetMaxMessage)
                return false;
            int packetLength = DpProtocol.NetHeaderSize + unreliable.Length;
            // The length shares its header word with the flags and has 16 bits. NET_MAXMESSAGE plus the
            // header is 8 more than fits; DarkPlaces would wrap the field, so such a datagram is refused.
            if (packetLength > (int)DpProtocol.NetFlagLengthMask)
                return false;
            var packet = new byte[packetLength];
            StoreBigLong(packet, 0, (uint)packetLength | DpProtocol.NetFlagUnreliable);
            StoreBigLong(packet, 4, _outgoingUnreliableSequence);
            unreliable.CopyTo(packet.AsSpan(DpProtocol.NetHeaderSize));
            _outgoingUnreliableSequence++;
            outgoing.Add(packet);
        }
        return true;
    }

    /// <summary>
    /// NetConn_ReceivedMessage for an in-band datagram. Replies the channel owes (the ACK for a DATA
    /// datagram, the next fragment after an ACK) are appended to <paramref name="outgoing"/>.
    /// Never throws on malformed input: anything that is not a well-formed datagram is
    /// <see cref="DpChannelReceive.Invalid"/>.
    /// </summary>
    public DpChannelReceive Receive(ReadOnlySpan<byte> datagram, double now, List<byte[]> outgoing, out byte[]? message)
    {
        message = null;
        if (datagram.Length < DpProtocol.NetHeaderSize)
            return DpChannelReceive.Invalid;

        uint first = BuffBigLong(datagram, 0);
        uint flags = first & ~DpProtocol.NetFlagLengthMask;
        uint length = first & DpProtocol.NetFlagLengthMask;
        // control packets are not ours, and the length field must describe exactly this datagram
        if ((flags & DpProtocol.NetFlagCtl) != 0 || length != (uint)datagram.Length)
            return DpChannelReceive.Invalid;

        uint sequence = BuffBigLong(datagram, 4);
        ReadOnlySpan<byte> data = datagram[DpProtocol.NetHeaderSize..];

        if ((flags & DpProtocol.NetFlagUnreliable) != 0)
        {
            if (sequence >= _unreliableReceiveSequence)
            {
                if (sequence > _unreliableReceiveSequence)
                    DroppedDatagrams += (int)Math.Min(sequence - _unreliableReceiveSequence, int.MaxValue / 2);
                _unreliableReceiveSequence = sequence + 1;
                LastMessageTime = now;
                if (data.Length > 0)
                {
                    message = data.ToArray();
                    return DpChannelReceive.Message;
                }
            }
            // else: a stale datagram, older than one already delivered
            return DpChannelReceive.Handled;
        }

        if ((flags & DpProtocol.NetFlagAck) != 0)
        {
            // Only the ACK for the fragment in flight means anything; an older one is a late duplicate.
            if (sequence == _sendSequence - 1 && sequence == _ackSequence)
            {
                _ackSequence++;
                LastMessageTime = now;
                if (_sendMessageLength > DpProtocol.MaxPacketFragment)
                {
                    // That fragment arrived; slide the rest down and send the next one straight away.
                    _sendMessageLength -= DpProtocol.MaxPacketFragment;
                    Array.Copy(_sendMessage, DpProtocol.MaxPacketFragment, _sendMessage, 0, _sendMessageLength);
                    outgoing.Add(BuildFragment(_sendSequence));
                    _sendSequence++;
                    _lastSendTime = now;
                }
                else
                    _sendMessageLength = 0;
            }
            return DpChannelReceive.Handled;
        }

        if ((flags & DpProtocol.NetFlagData) != 0)
        {
            // ACK first and unconditionally: if this is a duplicate, the sender never got our last ACK.
            var ack = new byte[DpProtocol.NetHeaderSize];
            StoreBigLong(ack, 0, DpProtocol.NetHeaderSize | DpProtocol.NetFlagAck);
            StoreBigLong(ack, 4, sequence);
            outgoing.Add(ack);

            if (sequence != _receiveSequence)
            {
                DuplicatesReceived++;
                return DpChannelReceive.Handled;
            }
            LastMessageTime = now;
            _receiveSequence++;
            if (_receiveMessageLength + data.Length > _receiveMessage.Length)
            {
                // "Reliable message too big for message buffer! Dropping the message!"
                _receiveMessageLength = 0;
                return DpChannelReceive.Handled;
            }
            data.CopyTo(_receiveMessage.AsSpan(_receiveMessageLength));
            _receiveMessageLength += data.Length;
            if ((flags & DpProtocol.NetFlagEom) != 0)
            {
                ReliableMessagesReceived++;
                int total = _receiveMessageLength;
                _receiveMessageLength = 0;
                if (total > 0)
                {
                    message = _receiveMessage.AsSpan(0, total).ToArray();
                    return DpChannelReceive.Message;
                }
            }
            return DpChannelReceive.Handled;
        }

        return DpChannelReceive.Invalid;
    }

    // The first (up to) 1024 bytes of the message in flight, flagged EOM when that is all of it.
    private byte[] BuildFragment(uint sequence)
    {
        int dataLength = Math.Min(_sendMessageLength, DpProtocol.MaxPacketFragment);
        uint eom = _sendMessageLength <= DpProtocol.MaxPacketFragment ? DpProtocol.NetFlagEom : 0;
        var packet = new byte[DpProtocol.NetHeaderSize + dataLength];
        StoreBigLong(packet, 0, (uint)packet.Length | DpProtocol.NetFlagData | eom);
        StoreBigLong(packet, 4, sequence);
        _sendMessage.AsSpan(0, dataLength).CopyTo(packet.AsSpan(DpProtocol.NetHeaderSize));
        return packet;
    }

    private static void StoreBigLong(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint BuffBigLong(ReadOnlySpan<byte> buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
}

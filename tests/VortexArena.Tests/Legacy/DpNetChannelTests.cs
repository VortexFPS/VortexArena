using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VortexArena.Legacy.Protocol;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The reliable/unreliable channel (netconn.c) and the connection handshake, driven with hand-built
/// datagrams and with two channels talking to each other across a lossy "wire".
/// </summary>
public class DpNetChannelTests
{
    private const uint Data = DpProtocol.NetFlagData, Ack = DpProtocol.NetFlagAck, Eom = DpProtocol.NetFlagEom,
        Unreliable = DpProtocol.NetFlagUnreliable;

    private static byte[] Packet(uint flags, uint sequence, params byte[] payload)
    {
        var p = new byte[8 + payload.Length];
        uint first = flags | (uint)p.Length;
        p[0] = (byte)(first >> 24); p[1] = (byte)(first >> 16); p[2] = (byte)(first >> 8); p[3] = (byte)first;
        p[4] = (byte)(sequence >> 24); p[5] = (byte)(sequence >> 16); p[6] = (byte)(sequence >> 8); p[7] = (byte)sequence;
        payload.CopyTo(p, 8);
        return p;
    }

    private static (uint flags, int length, uint sequence, byte[] payload) Split(byte[] p)
    {
        uint first = ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];
        uint seq = ((uint)p[4] << 24) | ((uint)p[5] << 16) | ((uint)p[6] << 8) | p[7];
        return (first & ~0xFFFFu, (int)(first & 0xFFFF), seq, p[8..]);
    }

    private static byte[] Pattern(int n, int seed = 1)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++)
            b[i] = (byte)(i * 31 + seed + (i >> 8));
        return b;
    }

    // ---------------------------------------------------------------- header layout

    [Fact]
    public void Unreliable_Datagram_Has_Big_Endian_Header_With_Length_And_Sequence()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.True(ch.Transmit(new byte[] { 0xAA, 0xBB }, 0, o));
        Assert.True(ch.Transmit(new byte[] { 0xCC }, 0, o));
        Assert.Equal(new byte[] { 0x00, 0x10, 0x00, 0x0A, 0, 0, 0, 0, 0xAA, 0xBB }, o[0]);
        Assert.Equal(new byte[] { 0x00, 0x10, 0x00, 0x09, 0, 0, 0, 1, 0xCC }, o[1]);
        Assert.Equal(2u, ch.OutgoingUnreliableSequence);
    }

    [Fact]
    public void Small_Reliable_Message_Is_One_Data_Datagram_With_Eom()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        ch.Reliable.WriteBytes(new byte[] { 4, (byte)'x', 0 });
        Assert.True(ch.Transmit(default, 0, o));
        Assert.Single(o);
        Assert.Equal(new byte[] { 0x00, 0x09, 0x00, 0x0B, 0, 0, 0, 0, 4, (byte)'x', 0 }, o[0]); // DATA|EOM, length 11
        Assert.True(ch.ReliableInFlight);
        Assert.Equal(0, ch.Reliable.Length);

        // The ACK frees the channel.
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Ack, 0), 0.1, o, out _));
        Assert.False(ch.ReliableInFlight);
    }

    [Fact]
    public void Nothing_To_Send_Sends_Nothing()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.True(ch.Transmit(default, 5, o));
        Assert.Empty(o);
    }

    // ---------------------------------------------------------------- receiving reliable

    [Fact]
    public void Reliable_Data_Is_Acked_And_Delivered_At_Eom()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Data, 0, 1, 2), 1, o, out byte[]? m0));
        Assert.Null(m0);
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Data | Eom, 1, 3), 1, o, out byte[]? m1));
        Assert.Equal(new byte[] { 1, 2, 3 }, m1);
        // one 8-byte ACK per DATA, echoing its sequence
        Assert.Equal(2, o.Count);
        Assert.Equal(new byte[] { 0x00, 0x02, 0x00, 0x08, 0, 0, 0, 0 }, o[0]);
        Assert.Equal(new byte[] { 0x00, 0x02, 0x00, 0x08, 0, 0, 0, 1 }, o[1]);
        Assert.Equal(1, ch.ReliableMessagesReceived);
    }

    [Fact]
    public void Duplicate_Reliable_Data_Is_Acked_Again_But_Not_Delivered_Twice()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Data | Eom, 0, 5), 1, o, out _));
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Data | Eom, 0, 5), 1, o, out byte[]? dup));
        Assert.Null(dup);
        Assert.Equal(2, o.Count); // the sender evidently missed the first ACK
        Assert.Equal(o[0], o[1]);
        Assert.Equal(1, ch.DuplicatesReceived);
    }

    [Fact]
    public void Reliable_Fragment_From_The_Future_Is_Acked_But_Ignored()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Data | Eom, 3, 9), 1, o, out byte[]? m));
        Assert.Null(m);
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Data | Eom, 0, 1), 1, o, out m));
        Assert.Equal(new byte[] { 1 }, m);
    }

    [Fact]
    public void Reliable_Message_Larger_Than_The_Buffer_Is_Dropped_Without_Throwing()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        var chunk = new byte[1024];
        // 65 fragments of 1024 bytes is one more than NET_MAXMESSAGE holds.
        for (uint i = 0; i < 64; i++)
            Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Data, i, chunk), 1, o, out _));
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Data | Eom, 64, chunk), 1, o, out byte[]? m));
        Assert.Null(m);
        // The channel is usable afterwards.
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Data | Eom, 65, 7), 1, o, out m));
        Assert.Equal(new byte[] { 7 }, m);
    }

    // ---------------------------------------------------------------- receiving unreliable

    [Fact]
    public void Unreliable_Stale_And_Duplicate_Datagrams_Are_Dropped_And_Gaps_Counted()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Unreliable, 0, 1), 1, o, out _));
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Unreliable, 5, 2), 2, o, out byte[]? m));
        Assert.Equal(new byte[] { 2 }, m);
        Assert.Equal(4, ch.DroppedDatagrams);
        Assert.Equal(2, ch.LastMessageTime);
        // reordered (older) and duplicated datagrams
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Unreliable, 3, 3), 3, o, out m));
        Assert.Null(m);
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Unreliable, 5, 2), 3, o, out m));
        Assert.Null(m);
        Assert.Equal(2, ch.LastMessageTime); // stale datagrams do not count as hearing from the server
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Unreliable, 6, 4), 4, o, out m));
        Assert.Empty(o); // unreliable datagrams are never acknowledged
    }

    [Fact]
    public void Empty_Unreliable_Datagram_Advances_The_Sequence_But_Is_Not_A_Message()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Unreliable, 0), 1, o, out byte[]? m));
        Assert.Null(m);
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Unreliable, 0, 1), 1, o, out _)); // now stale
    }

    // ---------------------------------------------------------------- garbage

    [Fact]
    public void Garbage_Datagrams_Are_Rejected()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.Equal(DpChannelReceive.Invalid, ch.Receive(ReadOnlySpan<byte>.Empty, 0, o, out _));
        Assert.Equal(DpChannelReceive.Invalid, ch.Receive(new byte[7], 0, o, out _));
        // length field disagrees with the datagram
        byte[] wrong = Packet(Unreliable, 0, 1, 2, 3);
        wrong[3]++;
        Assert.Equal(DpChannelReceive.Invalid, ch.Receive(wrong, 0, o, out _));
        // truncated in transit
        Assert.Equal(DpChannelReceive.Invalid, ch.Receive(Packet(Data | Eom, 0, 1, 2, 3).AsSpan(0, 10), 0, o, out _));
        // control packet
        Assert.Equal(DpChannelReceive.Invalid, ch.Receive(Packet(DpProtocol.NetFlagCtl, 0, 1), 0, o, out _));
        // no type flag at all
        Assert.Equal(DpChannelReceive.Invalid, ch.Receive(Packet(0, 0, 1), 0, o, out _));
        // connectionless packet handed to the channel by mistake
        Assert.Equal(DpChannelReceive.Invalid, ch.Receive(new byte[] { 255, 255, 255, 255, (byte)'a', (byte)'c', (byte)'k', 0 }, 0, o, out _));
        Assert.Empty(o);
        // and a valid one still works
        Assert.Equal(DpChannelReceive.Message, ch.Receive(Packet(Unreliable, 0, 1), 0, o, out _));
    }

    [Fact]
    public void Stale_Or_Unexpected_Acks_Are_Ignored()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        // an ACK when nothing was sent
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Ack, 0), 0, o, out _));
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Ack, 0xFFFFFFFF), 0, o, out _));
        ch.Reliable.WriteByte(1);
        ch.Transmit(default, 0, o);
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Ack, 7), 0, o, out _)); // wrong sequence
        Assert.True(ch.ReliableInFlight);
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Ack, 0), 0, o, out _));
        Assert.False(ch.ReliableInFlight);
        Assert.Equal(DpChannelReceive.Handled, ch.Receive(Packet(Ack, 0), 0, o, out _)); // duplicate ACK
        Assert.False(ch.ReliableInFlight);
    }

    [Fact]
    public void Oversized_Outgoing_Messages_Are_Refused()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        Assert.False(ch.Transmit(new byte[65536], 0, o));  // header + 65536 does not fit the 16-bit length
        Assert.Empty(o);
        Assert.True(ch.Transmit(new byte[65527], 0, o));   // 65535 total: the largest that does
        Assert.Equal(65535, Split(o[0]).length);

        ch.Reliable.WriteBytes(new byte[DpProtocol.NetMaxMessage]);
        ch.Reliable.WriteByte(1); // overflows the reliable buffer
        Assert.False(ch.Transmit(default, 0, new List<byte[]>()));
    }

    // ---------------------------------------------------------------- fragmentation and resend

    [Fact]
    public void Five_Kilobyte_Reliable_Message_Is_Sent_Stop_And_Wait_In_1024_Byte_Fragments()
    {
        byte[] message = Pattern(5000);
        var sender = new DpNetChannel(0);
        var receiver = new DpNetChannel(0);
        var fromSender = new List<byte[]>();
        var fromReceiver = new List<byte[]>();

        sender.Reliable.WriteBytes(message);
        sender.Transmit(default, 0, fromSender);
        Assert.Single(fromSender); // exactly one fragment in flight

        byte[]? delivered = null;
        int fragments = 0;
        while (fromSender.Count > 0)
        {
            byte[] f = fromSender[0];
            fromSender.RemoveAt(0);
            var (flags, length, sequence, payload) = Split(f);
            Assert.Equal((uint)fragments, sequence);
            Assert.True((flags & Data) != 0);
            bool last = fragments == 4;
            Assert.Equal(last ? 5000 - 4 * 1024 : 1024, payload.Length);
            Assert.Equal(last, (flags & Eom) != 0);
            Assert.Equal(f.Length, length);
            fragments++;

            var result = receiver.Receive(f, 0, fromReceiver, out byte[]? m);
            Assert.Equal(last ? DpChannelReceive.Message : DpChannelReceive.Handled, result);
            delivered ??= m;
            Assert.Single(fromReceiver);
            // the ACK releases the next fragment immediately, without waiting for Transmit
            sender.Receive(fromReceiver[0], 0, fromSender, out _);
            fromReceiver.Clear();
        }
        Assert.Equal(5, fragments);
        Assert.Equal(message, delivered);
        Assert.False(sender.ReliableInFlight);
    }

    [Fact]
    public void Lost_Fragment_Is_Resent_After_One_Second_With_The_Same_Sequence()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        ch.Reliable.WriteBytes(Pattern(1500));
        ch.Transmit(default, 10.0, o);
        Assert.Single(o);
        byte[] first = o[0];
        o.Clear();

        ch.Transmit(default, 10.5, o);
        Assert.Empty(o);                 // not yet
        ch.Transmit(default, 11.0, o);
        Assert.Empty(o);                 // "> 1.0", not ">="
        ch.Transmit(default, 11.01, o);
        Assert.Single(o);
        Assert.Equal(first, o[0]);       // same bytes, same sequence
        Assert.Equal(1, ch.PacketsResent);
        o.Clear();
        ch.Transmit(default, 11.5, o);
        Assert.Empty(o);                 // the timer restarted at the resend

        // Second fragment after the ACK, and its own resend carries sequence 1.
        ch.Receive(Packet(Ack, 0), 11.6, o, out _);
        Assert.Single(o);
        byte[] second = o[0];
        Assert.Equal(1u, Split(second).sequence);
        Assert.True((Split(second).flags & Eom) != 0);
        o.Clear();
        ch.Transmit(default, 12.7, o);
        Assert.Equal(second, Assert.Single(o));
    }

    [Fact]
    public void New_Reliable_Data_Waits_For_The_Message_In_Flight()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        ch.Reliable.WriteByte(1);
        ch.Transmit(default, 0, o);
        ch.Reliable.WriteByte(2);
        ch.Reliable.WriteByte(3);
        o.Clear();
        ch.Transmit(new byte[] { 9 }, 0.1, o);
        Assert.Single(o);                               // only the unreliable datagram
        Assert.True((Split(o[0]).flags & Unreliable) != 0);
        Assert.Equal(2, ch.Reliable.Length);
        o.Clear();
        ch.Receive(Packet(Ack, 0), 0.2, o, out _);
        ch.Transmit(default, 0.2, o);
        Assert.Equal(new byte[] { 2, 3 }, Split(Assert.Single(o)).payload); // batched into one message, sequence 1
        Assert.Equal(1u, Split(o[0]).sequence);
        Assert.Equal(2, ch.ReliableMessagesSent);
    }

    [Fact]
    public void Resend_Reliable_And_Unreliable_Go_Out_In_That_Order()
    {
        var ch = new DpNetChannel(0);
        var o = new List<byte[]>();
        ch.Reliable.WriteByte(1);
        ch.Transmit(default, 0, o);
        o.Clear();
        ch.Transmit(new byte[] { 9 }, 2, o);
        Assert.Equal(2, o.Count);
        Assert.True((Split(o[0]).flags & Data) != 0);
        Assert.True((Split(o[1]).flags & Unreliable) != 0);
    }

    /// <summary>
    /// Two channels over a wire that loses, duplicates and reorders datagrams, with a fixed seed.
    /// Every reliable message must arrive exactly once, whole and in order; unreliable ones must
    /// arrive at most once and never out of order.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Two_Channels_Survive_Loss_Duplication_And_Reordering(int seed)
    {
        var rng = new Random(seed);
        var a = new DpNetChannel(0);
        var b = new DpNetChannel(0);
        var aToB = new List<byte[]>();
        var bToA = new List<byte[]>();

        var toSend = new Queue<byte[]>();
        for (int i = 0; i < 12; i++)
            toSend.Enqueue(Pattern(rng.Next(1, 6000), seed * 100 + i));
        byte[][] expected = toSend.ToArray();
        var received = new List<byte[]>();
        var unreliableSeen = new List<int>();
        int unreliableCounter = 0;

        void Deliver(List<byte[]> wire, DpNetChannel to, List<byte[]> replies, double now, bool collect)
        {
            // loss, duplication, reordering
            var arriving = new List<byte[]>();
            foreach (byte[] d in wire)
            {
                int roll = rng.Next(100);
                if (roll < 25) continue;              // lost
                arriving.Add(d);
                if (roll >= 90) arriving.Add(d);      // duplicated
            }
            wire.Clear();
            for (int i = arriving.Count - 1; i > 0; i--)
                if (rng.Next(3) == 0)
                    (arriving[i], arriving[i - 1]) = (arriving[i - 1], arriving[i]);
            foreach (byte[] d in arriving)
            {
                var result = to.Receive(d, now, replies, out byte[]? m);
                Assert.NotEqual(DpChannelReceive.Invalid, result);
                if (result != DpChannelReceive.Message || !collect) continue;
                if (m![0] == 0xEE && m.Length == 5)
                    unreliableSeen.Add(BitConverter.ToInt32(m, 1));
                else
                    received.Add(m);
            }
        }

        double now = 0;
        for (int tick = 0; tick < 4000 && received.Count < expected.Length; tick++)
        {
            now += 0.25;
            if (!a.ReliableInFlight && a.Reliable.Length == 0 && toSend.Count > 0)
            {
                byte[] next = toSend.Dequeue();
                // Reliable payloads never start with 0xEE followed by exactly 4 bytes here: length is 1 or > 5 mostly,
                // so mark them distinctly instead of relying on that.
                a.Reliable.WriteByte(0x11);
                a.Reliable.WriteBytes(next);
            }
            var unreliable = new byte[5];
            unreliable[0] = 0xEE;
            BitConverter.GetBytes(unreliableCounter++).CopyTo(unreliable, 1);
            Assert.True(a.Transmit(unreliable, now, aToB));
            Deliver(aToB, b, bToA, now, collect: true);
            b.Transmit(default, now, bToA);
            Deliver(bToA, a, aToB, now, collect: false);
        }

        Assert.Equal(expected.Length, received.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(0x11, received[i][0]);
            Assert.Equal(expected[i], received[i][1..]);
        }
        Assert.True(a.PacketsResent > 0, "the test must actually exercise resends");
        Assert.True(b.DuplicatesReceived > 0, "and duplicates");
        // strictly increasing: no duplicates, no reordering
        Assert.True(unreliableSeen.Count > 10);
        for (int i = 1; i < unreliableSeen.Count; i++)
            Assert.True(unreliableSeen[i] > unreliableSeen[i - 1]);
    }

    // ---------------------------------------------------------------- handshake

    private static byte[] Oob(string text)
    {
        byte[] t = Encoding.ASCII.GetBytes(text);
        var p = new byte[4 + t.Length];
        p[0] = p[1] = p[2] = p[3] = 0xFF;
        t.CopyTo(p, 4);
        return p;
    }

    private static string OobText(byte[] p)
    {
        Assert.True(MasterServerProtocol.TryStripOob(p, out ReadOnlySpan<byte> body));
        return Encoding.ASCII.GetString(body);
    }

    [Fact]
    public void Handshake_Accept_Path()
    {
        var hs = new DpConnectionHandshake();
        var o = new List<byte[]>();
        Assert.Equal(DpHandshakeState.Idle, hs.State);
        hs.Update(0, o);
        Assert.Empty(o); // not started
        hs.Start(100);
        hs.Update(100, o);
        Assert.Equal("getchallenge", OobText(Assert.Single(o)));
        o.Clear();

        Assert.True(hs.Receive(Oob("challenge AbC123"), 100.1, o));
        Assert.Equal("AbC123", hs.Challenge);
        Assert.Equal("connect\\protocol\\darkplaces 3\\protocols\\DP7\\challenge\\AbC123", OobText(Assert.Single(o)));
        Assert.Equal(DpHandshakeState.Connecting, hs.State);
        o.Clear();

        Assert.True(hs.Receive(Oob("accept"), 100.2, o));
        Assert.Equal(DpHandshakeState.Accepted, hs.State);
        Assert.Empty(o);
        hs.Update(200, o);
        Assert.Empty(o); // no more getchallenge once accepted
    }

    [Fact]
    public void Handshake_Reject_Surfaces_The_Reason()
    {
        var hs = new DpConnectionHandshake();
        var o = new List<byte[]>();
        hs.Start(0);
        hs.Update(0, o);
        hs.Receive(Oob("challenge x"), 0, o);
        Assert.True(hs.Receive(Oob("reject This server requires authentication and encryption to be supported by your client"), 0, o));
        Assert.Equal(DpHandshakeState.Rejected, hs.State);
        Assert.Equal("This server requires authentication and encryption to be supported by your client", hs.RejectReason);
        // nothing further is processed as part of the handshake
        hs.Receive(Oob("accept"), 0, o);
        Assert.Equal(DpHandshakeState.Rejected, hs.State);
    }

    [Fact]
    public void Handshake_Challenge_With_Trailing_Crypto_Blob_Uses_Only_The_Token()
    {
        var hs = new DpConnectionHandshake();
        var o = new List<byte[]>();
        hs.Start(0);
        hs.Update(0, o);
        o.Clear();
        // "challenge <token>\0vlen.<binary>..." as a d0_blind_id server sends it
        byte[] head = Oob("challenge 9fQ2zz8");
        byte[] blob = { 0, (byte)'v', (byte)'l', (byte)'e', (byte)'n', 0xFF, 0x00, 0x80, 0x5C, 0x22, 0x0A };
        byte[] packet = head.Concat(blob).ToArray();
        Assert.True(hs.Receive(packet, 0, o));
        Assert.Equal("9fQ2zz8", hs.Challenge);
        Assert.Equal("connect\\protocol\\darkplaces 3\\protocols\\DP7\\challenge\\9fQ2zz8", OobText(Assert.Single(o)));
    }

    [Fact]
    public void Handshake_UserInfo_Is_Inserted_Before_The_Challenge()
    {
        byte[] p = DpConnectionHandshake.BuildConnect("tok", "\\password\\secret");
        Assert.Equal("connect\\protocol\\darkplaces 3\\protocols\\DP7\\password\\secret\\challenge\\tok", OobText(p));
        // and it is a well-formed infostring after the "connect" verb
        var info = MasterServerProtocol.ParseInfostring(OobText(p)["connect".Length..]);
        Assert.Equal("darkplaces 3", info["protocol"]);
        Assert.Equal("DP7", info["protocols"]);
        Assert.Equal("tok", info["challenge"]);
    }

    [Fact]
    public void Handshake_Retries_Once_A_Second_Then_Times_Out()
    {
        var hs = new DpConnectionHandshake(maxTries: 3);
        var o = new List<byte[]>();
        hs.Start(50);
        hs.Update(50, o);
        Assert.Single(o);
        hs.Update(50.5, o);
        hs.Update(51.0, o);
        Assert.Single(o);          // "nextsendtime < realtime": not at exactly one second
        hs.Update(51.1, o);
        Assert.Equal(2, o.Count);
        hs.Update(52.2, o);
        Assert.Equal(3, o.Count);
        Assert.Equal(0, hs.RemainingTries);
        Assert.Equal(DpHandshakeState.Connecting, hs.State);
        hs.Update(53.3, o);
        Assert.Equal(3, o.Count);
        Assert.Equal(DpHandshakeState.TimedOut, hs.State);
        // a late challenge no longer does anything
        hs.Receive(Oob("challenge late"), 54, o);
        Assert.Equal(3, o.Count);
    }

    [Fact]
    public void Handshake_Answers_Ping_With_Ack_In_Any_State_And_Passes_In_Band_Datagrams_Through()
    {
        var hs = new DpConnectionHandshake();
        var o = new List<byte[]>();
        Assert.True(hs.Receive(Oob("ping"), 0, o));
        Assert.Equal("ack", OobText(Assert.Single(o)));
        o.Clear();
        Assert.True(hs.Receive(Oob("ack"), 0, o));           // consumed, no reply
        Assert.True(hs.Receive(Oob("infoResponse\n\\a\\b"), 0, o));
        Assert.Empty(o);
        Assert.False(hs.Receive(Packet(Unreliable, 0, 1), 0, o)); // netchan datagram: not ours
        Assert.False(hs.Receive(new byte[] { 255, 255, 255 }, 0, o));
        Assert.False(hs.Receive(ReadOnlySpan<byte>.Empty, 0, o));
    }

    [Fact]
    public void Handshake_Ignores_Messages_That_Only_Resemble_The_Real_Ones()
    {
        var hs = new DpConnectionHandshake();
        var o = new List<byte[]>();
        hs.Start(0);
        hs.Update(0, o);
        o.Clear();
        hs.Receive(Oob("acceptance"), 0, o);            // DarkPlaces requires length == 6
        hs.Receive(Oob("reject"), 0, o);                // no reason: length must exceed 7
        hs.Receive(Oob("challenge"), 0, o);             // no space, no token
        hs.Receive(Oob("challenge " + new string('x', 4000)), 0, o); // absurd token
        Assert.Equal(DpHandshakeState.Connecting, hs.State);
        Assert.Empty(o);
        // An empty token is still answered, as the C would.
        hs.Receive(Oob("challenge "), 0, o);
        Assert.Single(o);
    }
}

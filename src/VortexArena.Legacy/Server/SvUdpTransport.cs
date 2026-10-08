// Port of Base/darkplaces/netconn.c NetConn_ServerFrame (the read loop, lines 3877-3890),
// NetConn_Read and NetConn_Write as far as they concern a server socket; the socket calls themselves
// are lhnet.c LHNET_Read / LHNET_Write. Modelled on Protocol/DpUdpTransport.cs, its client twin.
using System.Net;
using System.Net.Sockets;

namespace VortexArena.Legacy.Server;

/// <summary>
/// One bound UDP socket for a server: datagrams in from anyone, datagrams out to whoever the caller
/// names. Deliberately thin, so that everything with protocol logic in it
/// (<see cref="SvConnectionless{TAddress}"/>, the netchan, the game) can be tested with byte arrays.
///
/// The peer's <see cref="IPEndPoint"/> is the address key the rest of the server uses. It has value
/// equality, so it works as <c>TAddress</c> of <see cref="SvConnectionless{TAddress}"/>, with
/// <see cref="WithoutPort"/> as that class's port-stripping function.
///
/// Not ported: net_fakelag / net_fakeloss (debugging cvars), the packet log, and the second socket
/// DarkPlaces opens for IPv6 (construct two of these).
/// </summary>
public sealed class SvUdpTransport : IDisposable
{
    /// <summary>
    /// sizeof(readbuffer) in NetConn_ServerFrame: NET_HEADERSIZE + NET_MAXMESSAGE. It is larger than
    /// any UDP datagram over IPv4 can be (65507 bytes of payload), so in practice this bounds nothing
    /// and <see cref="MaxDatagramSize"/> exists for an owner that wants a tighter limit.
    /// </summary>
    public const int ReceiveBufferSize = 8 + 65536;

    private readonly Socket _socket;
    // One byte more than the limit, so that a datagram of exactly the limit and one that was cut off
    // by it can be told apart on every platform (Windows reports truncation as an error, others do not).
    private readonly byte[] _buffer = new byte[ReceiveBufferSize + 1];
    private EndPoint _from;
    private readonly EndPoint _any;

    /// <param name="bind">The local address and port (net_address and the <c>port</c> cvar, 26000).
    /// Port 0 lets the system choose; read it back from <see cref="LocalEndPoint"/>.</param>
    public SvUdpTransport(IPEndPoint bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.Blocking = false;
            if (OperatingSystem.IsWindows())
            {
                // Windows turns an ICMP "port unreachable" for an earlier send into a WSAECONNRESET on
                // the next receive. On a server that would let one departed client's stray reply
                // interrupt reading everyone else's packets; SIO_UDP_CONNRESET off.
                try { _socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); }
                catch (SocketException) { }
            }
            _socket.Bind(bind);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        _any = new IPEndPoint(bind.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        _from = _any;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>The largest datagram accepted; anything longer is dropped whole, never truncated.</summary>
    public int MaxDatagramSize { get; set; } = ReceiveBufferSize;

    /// <summary>The most datagrams one <see cref="TryReceive"/> will discard while looking for a good
    /// one, so a flood of junk cannot stall a frame. The owner bounds its own receive loop likewise.</summary>
    public int MaxPacketsPerPoll { get; set; } = 256;

    public int DatagramsSent { get; private set; }
    public int DatagramsReceived { get; private set; }
    /// <summary>Datagrams discarded for being empty or longer than <see cref="MaxDatagramSize"/>.</summary>
    public int DatagramsDropped { get; private set; }

    /// <summary>Send one datagram. A transient socket error loses it, as the network would.</summary>
    public void Send(ReadOnlySpan<byte> datagram, IPEndPoint to)
    {
        ArgumentNullException.ThrowIfNull(to);
        try
        {
            _socket.SendTo(datagram, SocketFlags.None, to);
            DatagramsSent++;
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Read one waiting datagram without blocking. False when nothing (more) is waiting.
    ///
    /// DarkPlaces' loop is "while ((length = NetConn_Read(...)) > 0)", so an empty datagram ends its
    /// reading for the frame; here an empty one is skipped and reading goes on, which gives a sender
    /// of empty datagrams no way to delay the packets queued behind them.
    /// </summary>
    public bool TryReceive(out byte[] datagram, out IPEndPoint from)
    {
        datagram = Array.Empty<byte>();
        from = (IPEndPoint)_any;
        int limit = Math.Clamp(MaxDatagramSize, 1, ReceiveBufferSize);
        for (int i = 0; i < MaxPacketsPerPoll; i++)
        {
            int length;
            try
            {
                if (_socket.Available <= 0 && !_socket.Poll(0, SelectMode.SelectRead))
                    return false;
                _from = _any;
                length = _socket.ReceiveFrom(_buffer, 0, limit + 1, SocketFlags.None, ref _from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize)
            {
                DatagramsDropped++; // longer than the buffer: Windows discards it and says so
                continue;
            }
            catch (SocketException)
            {
                return false; // would-block or transient: done for this frame
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            if (length <= 0 || length > limit || _from is not IPEndPoint peer)
            {
                DatagramsDropped++;
                continue;
            }
            datagram = _buffer.AsSpan(0, length).ToArray();
            from = peer;
            DatagramsReceived++;
            return true;
        }
        return false;
    }

    /// <summary>The same host with port 0 (LHNETADDRESS_SetPort(&amp;address, 0)): what the flood
    /// limits key on, so that a sender cannot evade them by varying its source port.</summary>
    public static IPEndPoint WithoutPort(IPEndPoint address) => new(address.Address, 0);

    public void Dispose() => _socket.Dispose();
}

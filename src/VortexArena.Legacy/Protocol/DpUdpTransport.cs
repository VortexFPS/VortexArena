// The socket for DpClient. Follows src/VortexArena.Net/MasterServerLink.cs (a non-blocking BCL
// UdpClient drained once per frame); the DarkPlaces counterpart is lhnet.c LHNET_Read / LHNET_Write.
using System.Net;
using System.Net.Sockets;

namespace VortexArena.Legacy.Protocol;

/// <summary>
/// One UDP socket talking to one server. Deliberately thin and kept apart from <see cref="DpClient"/>,
/// so that everything with protocol logic in it can be tested with byte arrays.
///
/// Datagrams from any other address are discarded: the protocol has no other authentication before
/// the handshake completes, and DarkPlaces makes the same check (net_sourceaddresscheck).
/// </summary>
public sealed class DpUdpTransport : ILegacyTransport
{
    private readonly UdpClient _udp;

    public DpUdpTransport(IPEndPoint server, int localPort = 0)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        _udp = new UdpClient(new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, localPort));
        _udp.Client.Blocking = false;
        if (OperatingSystem.IsWindows())
        {
            // Windows turns an ICMP "port unreachable" for an earlier send into a WSAECONNRESET on
            // the next receive. For a connectionless protocol that is noise; SIO_UDP_CONNRESET off.
            try { _udp.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); }
            catch (SocketException) { }
        }
    }

    public IPEndPoint Server { get; }
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>The most datagrams one <see cref="Pump"/> will read, so a flood cannot stall a frame.</summary>
    public int MaxPacketsPerPoll { get; set; } = 256;

    public int DatagramsSent { get; private set; }
    public int DatagramsReceived { get; private set; }
    long ILegacyTransport.Sent => DatagramsSent;
    long ILegacyTransport.Received => DatagramsReceived;
    string ILegacyTransport.Peer => "udp " + Server;

    /// <summary>Send one datagram. A transient socket error loses it, as the network would.</summary>
    public void Send(byte[] datagram)
    {
        try
        {
            _udp.Send(datagram, datagram.Length, Server);
            DatagramsSent++;
        }
        catch (SocketException) { }
    }

    /// <summary>Read one waiting datagram from the server without blocking.</summary>
    public bool TryReceive(out byte[] datagram)
    {
        datagram = Array.Empty<byte>();
        for (int i = 0; i < MaxPacketsPerPoll; i++)
        {
            try
            {
                if (_udp.Available <= 0)
                    return false;
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _udp.Receive(ref from);
                if (!from.Address.Equals(Server.Address) || from.Port != Server.Port)
                    continue; // not our server
                datagram = data;
                DatagramsReceived++;
                return true;
            }
            catch (SocketException)
            {
                return false; // would-block or transient: done for this frame
            }
        }
        return false;
    }

    /// <summary>
    /// One frame of a client: feed everything that has arrived to <paramref name="client"/>, let it
    /// advance to <paramref name="now"/>, and send what it produced.
    /// </summary>
    public void Pump(DpClient client, double now)
    {
        for (int i = 0; i < MaxPacketsPerPoll && TryReceive(out byte[] datagram); i++)
            client.Receive(datagram, now);
        foreach (byte[] datagram in client.Update(now))
            Send(datagram);
    }

    public void Dispose() => _udp.Dispose();
}

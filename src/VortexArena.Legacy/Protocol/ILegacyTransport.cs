// The seam between a legacy client and whatever carries its datagrams. DarkPlaces has the same seam in
// lhnet.c: LHNETADDRESSTYPE_LOOP (two queues inside the process, which is how a listen server's own
// player is connected) beside LHNETADDRESSTYPE_INET4 (a socket). Everything above it - netconn.c, the
// whole client - does not know which one it is talking through.
namespace VortexArena.Legacy.Protocol;

/// <summary>
/// What carries one client's datagrams to one server and back: a UDP socket
/// (<see cref="DpUdpTransport"/>) or a pair of queues inside the process
/// (<see cref="VortexArena.Legacy.Local.LegacyLoopbackTransport"/>, DarkPlaces' loopback driver).
/// Neither end blocks. A datagram that cannot be sent is lost, as on a network; the protocol above
/// resends what has to arrive.
/// </summary>
public interface ILegacyTransport : IDisposable
{
    /// <summary>Send one datagram to the server.</summary>
    void Send(byte[] datagram);
    /// <summary>The next datagram from the server, if one is waiting.</summary>
    bool TryReceive(out byte[] datagram);
    /// <summary>Datagrams handed to <see cref="Send"/> and returned by <see cref="TryReceive"/> so far.</summary>
    long Sent { get; }
    long Received { get; }
    /// <summary>What the far end is, for a log line: "udp 192.0.2.1:26000" or "local".</summary>
    string Peer { get; }
}

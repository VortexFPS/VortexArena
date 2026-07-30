using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace XonoticGodot.Net;

/// <summary>
/// The UDP socket layer for the connectionless master-server / server-info protocol — the C# successor to DP's
/// out-of-band <c>netconn.c</c> path (server heartbeats to dpmaster, the in-game server browser's
/// <c>getservers</c> query, and per-server <c>getinfo</c>/<c>infoResponse</c>). It wraps a single
/// <see cref="UdpClient"/> and uses the tested byte codec in <see cref="MasterServerProtocol"/>; the parsing is
/// pure, so this layer is just send + non-blocking receive + dispatch.
///
/// Godot-free (BCL sockets), so both the dedicated server (heartbeat + answer getinfo) and the client browser
/// (query masters, ping servers) share it. Drive it by calling <see cref="Poll"/> each frame to dispatch
/// received datagrams to the events.
/// </summary>
public sealed class MasterServerLink : IDisposable
{
    private readonly UdpClient _udp;

    /// <summary>The local UDP endpoint actually bound (port is ephemeral when 0 was requested).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    public MasterServerLink(int localPort = 0)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        _udp.Client.Blocking = false;
    }

    // ---- events (raised from Poll) ----

    /// <summary>A master replied with a server list (client browser). The list is (ip, port) game servers.</summary>
    public event Action<IReadOnlyList<(IPAddress ip, int port)>>? ServerListReceived;

    /// <summary>A game server replied to our getinfo with its infostring (server browser detail row).</summary>
    public event Action<IPEndPoint, IReadOnlyDictionary<string, string>>? InfoReceived;

    /// <summary>A client asked this (server) for its info — answer with <see cref="SendInfoResponse"/>.</summary>
    public event Action<IPEndPoint, string>? GetInfoRequested;

    // =====================================================================================
    //  Send (server + client)
    // =====================================================================================

    /// <summary>Server → master: a keepalive heartbeat so the master keeps us in its list (call every ~3 min).</summary>
    public void SendHeartbeat(IPEndPoint master)
    {
        byte[] p = MasterServerProtocol.EncodeHeartbeat();
        _udp.Send(p, p.Length, master);
    }

    /// <summary>Client → master: request the server list for <paramref name="game"/>/<paramref name="protocol"/>.</summary>
    public void RequestServers(IPEndPoint master, string game, int protocol)
    {
        byte[] p = MasterServerProtocol.EncodeGetServers(game, protocol);
        _udp.Send(p, p.Length, master);
    }

    /// <summary>Client → server: ask a game server for its info (browser detail / ping). The challenge echoes back.</summary>
    public void RequestInfo(IPEndPoint server, string challenge)
    {
        byte[] p = MasterServerProtocol.EncodeGetInfo(challenge);
        _udp.Send(p, p.Length, server);
    }

    /// <summary>Server → client: answer a getinfo with our infostring (hostname, map, players, …).</summary>
    public void SendInfoResponse(IPEndPoint to, IReadOnlyDictionary<string, string> info)
    {
        byte[] p = MasterServerProtocol.EncodeInfoResponse(info);
        _udp.Send(p, p.Length, to);
    }

    /// <summary>Send a pre-built datagram verbatim (e.g. a master forwarding a canned getserversResponse).</summary>
    public void SendRaw(byte[] datagram, IPEndPoint to) => _udp.Send(datagram, datagram.Length, to);

    // =====================================================================================
    //  Receive + dispatch
    // =====================================================================================

    /// <summary>Drain pending datagrams (non-blocking) and raise the matching event for each recognised message.</summary>
    public void Poll()
    {
        while (true)
        {
            byte[] data;
            IPEndPoint from;
            try
            {
                if (_udp.Available <= 0)
                    return;
                var ep = new IPEndPoint(IPAddress.Any, 0);
                data = _udp.Receive(ref ep);
                from = ep;
            }
            catch (SocketException)
            {
                return; // would-block / transient — done for this frame
            }

            Dispatch(data, from);
        }
    }

    private void Dispatch(byte[] data, IPEndPoint from)
    {
        if (!MasterServerProtocol.TryStripOob(data, out ReadOnlySpan<byte> body))
            return;
        string text = System.Text.Encoding.ASCII.GetString(body);

        if (text.StartsWith("getserversResponse", StringComparison.Ordinal))
        {
            ServerListReceived?.Invoke(MasterServerProtocol.ParseGetServersResponse(data));
        }
        else if (text.StartsWith("infoResponse", StringComparison.Ordinal))
        {
            InfoReceived?.Invoke(from, MasterServerProtocol.ParseInfoResponse(data));
        }
        else if (text.StartsWith("getinfo", StringComparison.Ordinal))
        {
            // a client probing this server — pass the challenge token (everything after "getinfo ").
            int sp = text.IndexOf(' ');
            string challenge = sp >= 0 ? text[(sp + 1)..].Trim() : "";
            GetInfoRequested?.Invoke(from, challenge);
        }
    }

    public void Dispose() => _udp.Dispose();
}

using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace VortexArena.Net;

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
    /// <summary>Null in ATTACHED mode. Every send goes through <see cref="SendDatagram"/> rather than
    /// touching this directly, so the two modes differ in exactly one place.</summary>
    private readonly UdpClient? _udp;

    /// <summary>ATTACHED mode's way out. Set together with a null <see cref="_udp"/>.</summary>
    private readonly Action<IPEndPoint, byte[]>? _send;

    private readonly IPEndPoint _localEndPoint;

    /// <summary>The local UDP endpoint this link answers on. In ATTACHED mode it is the GAME port, because
    /// that is the socket the datagrams arrived on and the one replies leave from.</summary>
    public IPEndPoint LocalEndPoint => _localEndPoint;

    /// <summary>True when this link rides a socket it does not own (the ENet game socket) and is fed by
    /// <see cref="Deliver"/> instead of reading for itself.</summary>
    public bool IsAttached => _udp is null;

    /// <summary>OWNED mode: bind a UDP socket of this link's own. The classic path — the client browser
    /// always uses it, and a server falls back to it when the engine cannot hand over its game socket.</summary>
    public MasterServerLink(int localPort = 0)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        _udp.Client.Blocking = false;
        _localEndPoint = (IPEndPoint)_udp.Client.LocalEndPoint!;
    }

    /// <summary>ATTACHED mode: no socket of our own. Out-of-band datagrams are pushed in by
    /// <see cref="Deliver"/>, and replies go out through <paramref name="send"/>.
    ///
    /// <para>This is what lets a dedicated server answer <c>getinfo</c> and rcon ON THE GAME PORT. The game
    /// socket belongs to ENet, which drops anything that is not an ENet packet, so historically the only way
    /// to answer OOB was a second socket on a nearby port. A patched engine hands us the rejected datagrams
    /// instead (<c>tools/engine-patches/godot-4.6.3-enet-oob-intercept.patch</c>) and this constructor
    /// receives them.</para>
    ///
    /// <para>Answering on the game port is what the rest of the system already assumes. Conductor verifies a
    /// server by sending <c>getinfo</c> to the address and port it ANNOUNCED and requires the reply from that
    /// same address and port; a server answering on gamePort+1 fails that check while every announce still
    /// returns 200, so it is never listed and neither side logs an error.</para></summary>
    /// <param name="send">Sends one datagram from the game socket. Backed by
    /// <c>ENetConnection.socket_send</c>, which writes to <c>host->socket</c>, so the reply's source address
    /// and port are the game server's own.</param>
    /// <param name="localEndPoint">The game port. Reported by <see cref="LocalEndPoint"/>; not bound here.</param>
    public MasterServerLink(Action<IPEndPoint, byte[]> send, IPEndPoint localEndPoint)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _localEndPoint = localEndPoint ?? throw new ArgumentNullException(nameof(localEndPoint));
    }

    private void SendDatagram(byte[] packet, IPEndPoint to)
    {
        if (_udp is not null)
            _udp.Send(packet, packet.Length, to);
        else
            _send!(to, packet);
    }

    // ---- events (raised from Poll) ----

    /// <summary>
    /// How many datagrams one <see cref="Poll"/> will drain. BUDGETED: an unauthenticated UDP flood (rcon
    /// getchallenge spam, master noise) would otherwise pin the calling thread inside the drain and stop the
    /// sim from ticking at all. Anything left over is read next frame — the socket buffer is the queue.
    ///
    /// <para>A CLIENT-side browser link raises this a long way, because there the leftovers are not a backlog
    /// to work through later but measurement error: the browser times a server's ping from when it sent the
    /// probe to when it reads the reply, so a reply that waits several frames for a drain slot reports the
    /// queue depth rather than the round trip. A browser link only ever receives answers to probes it sent,
    /// so there is no flood for the low budget to protect it from.</para>
    /// </summary>
    public int MaxPacketsPerPoll { get; set; } = 64;

    /// <summary>A master replied with a server list (client browser). The list is (ip, port) game servers.</summary>
    public event Action<IReadOnlyList<(IPAddress ip, int port)>>? ServerListReceived;

    /// <summary>A game server replied to our getinfo with its infostring (server browser detail row).</summary>
    public event Action<IPEndPoint, IReadOnlyDictionary<string, string>>? InfoReceived;

    /// <summary>A client asked this (server) for its info — answer with <see cref="SendInfoResponse"/>.</summary>
    public event Action<IPEndPoint, string>? GetInfoRequested;

    /// <summary>DS-6: a connectionless <c>rcon</c>/<c>srcon</c>/<c>getchallenge</c> packet arrived. Arg is the RAW
    /// OOB body (header already stripped) — passed as bytes, not a string, because a secure srcon packet embeds a
    /// raw 16-byte HMAC that ASCII-decoding would corrupt. The server hands it to <see cref="RconProtocol"/> and
    /// replies via <see cref="SendRaw"/>. Null handler = rcon disabled (the packet is ignored).</summary>
    public event Action<IPEndPoint, byte[]>? RconReceived;

    // =====================================================================================
    //  Send (server + client)
    // =====================================================================================

    /// <summary>Server → master: a keepalive heartbeat so the master keeps us in its list (call every ~3 min).</summary>
    public void SendHeartbeat(IPEndPoint master)
    {
        byte[] p = MasterServerProtocol.EncodeHeartbeat();
        SendDatagram(p, master);
    }

    /// <summary>Client → master: request the server list for <paramref name="game"/>/<paramref name="protocol"/>.</summary>
    public void RequestServers(IPEndPoint master, string game, int protocol)
    {
        byte[] p = MasterServerProtocol.EncodeGetServers(game, protocol);
        SendDatagram(p, master);
    }

    /// <summary>Client → server: ask a game server for its info (browser detail / ping). The challenge echoes back.</summary>
    public void RequestInfo(IPEndPoint server, string challenge)
    {
        byte[] p = MasterServerProtocol.EncodeGetInfo(challenge);
        SendDatagram(p, server);
    }

    /// <summary>Server → client: answer a getinfo with our infostring (hostname, map, players, …).</summary>
    public void SendInfoResponse(IPEndPoint to, IReadOnlyDictionary<string, string> info)
    {
        byte[] p = MasterServerProtocol.EncodeInfoResponse(info);
        SendDatagram(p, to);
    }

    /// <summary>Send a pre-built datagram verbatim (e.g. a master forwarding a canned getserversResponse).</summary>
    public void SendRaw(byte[] datagram, IPEndPoint to) => SendDatagram(datagram, to);

    // =====================================================================================
    //  Receive + dispatch
    // =====================================================================================

    /// <summary>Hand this link one out-of-band datagram that arrived on a socket it does not own, and
    /// dispatch it exactly as <see cref="Poll"/> would have.
    ///
    /// <para>ATTACHED mode only. The caller is the engine's ENet intercept, which sees every datagram the
    /// game socket receives and forwards the ones ENet rejected. The same try/catch as <see cref="Poll"/>
    /// wraps the dispatch, and for a stronger reason: this runs INSIDE <c>ENetConnection.service()</c>, so
    /// an escaping exception would not merely kill a frame, it would abort the network service call that
    /// every connected player's traffic is riding on.</para></summary>
    public void Deliver(byte[] data, IPEndPoint from)
    {
        try { Dispatch(data, from); }
        catch (Exception ex) { VortexArena.Common.Diagnostics.Log.Warn($"[master] dropped packet: {ex.Message}"); }
    }

    /// <summary>Drain pending datagrams (non-blocking) and raise the matching event for each recognised message.
    ///
    /// <para>A no-op in ATTACHED mode: there is no socket of our own to drain, and packets arrive through
    /// <see cref="Deliver"/> instead. Callers therefore need no mode check — they pump every frame either
    /// way.</para></summary>
    public void Poll()
    {
        if (_udp is null)
            return;

        for (int i = 0; i < MaxPacketsPerPoll; i++)
        {
            byte[] data;
            IPEndPoint from;
            try
            {
                if (_udp!.Available <= 0)
                    return;
                var ep = new IPEndPoint(IPAddress.Any, 0);
                data = _udp!.Receive(ref ep);
                from = ep;
            }
            catch (SocketException)
            {
                return; // would-block / transient — done for this frame
            }

            // Dispatch runs handler code (rcon auth + command execution + the reply send). A throw here used
            // to escape Poll → PumpMasterServer → _Process and take the whole frame loop down; one bad packet
            // or an oversized reply must never do that.
            try { Dispatch(data, from); }
            catch (Exception ex) { VortexArena.Common.Diagnostics.Log.Warn($"[master] dropped packet: {ex.Message}"); }
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
        // DS-6: rcon / srcon / getchallenge — the server-console-over-UDP verbs. Detected on the ASCII PREFIX
        // (intact even in a binary srcon packet) but forwarded as RAW bytes so the embedded 16-byte HMAC survives.
        // "getchallenge" is checked before "getinfo" (both start with "get") so a challenge request isn't mistaken
        // for an info probe.
        else if (text.StartsWith("getchallenge", StringComparison.Ordinal)
              || text.StartsWith("srcon ", StringComparison.Ordinal)
              || text.StartsWith("rcon ", StringComparison.Ordinal))
        {
            RconReceived?.Invoke(from, body.ToArray());
        }
        else if (text.StartsWith("getinfo", StringComparison.Ordinal))
        {
            // a client probing this server — pass the challenge token (everything after "getinfo ").
            int sp = text.IndexOf(' ');
            string challenge = sp >= 0 ? text[(sp + 1)..].Trim() : "";
            GetInfoRequested?.Invoke(from, challenge);
        }
    }

    /// <summary>DS-6: send a pre-built connectionless packet (rcon reply / challenge reply) to <paramref name="to"/>.
    /// The bytes already include the OOB header (<see cref="RconProtocol"/> builds them).</summary>
    public void SendRaw(IPEndPoint to, byte[] packet) => SendDatagram(packet, to);

    public void Dispose() => _udp?.Dispose();
}

using System;
using System.Collections.Generic;
using Godot;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The raw ENet packet transport for the XonoticGodot link — a thin wrapper over Godot's
/// <see cref="ENetMultiplayerPeer"/> that sends/receives byte buffers on a reliable and an unreliable
/// channel (<see cref="NetProtocol.ReliableChannel"/> / <see cref="NetProtocol.UnreliableChannel"/>) and
/// surfaces peer connect/disconnect + per-packet receive as plain C# events.
///
/// We drive ENet manually (poll + put/get packet) rather than going through Godot's high-level
/// <c>MultiplayerApi</c>/<c>rpc</c>/<c>MultiplayerSynchronizer</c>: the spec is explicit that the high-level
/// layer gives no prediction/reconciliation/lag-comp and degrades past ~16 players, so we own the loop and
/// only borrow ENet's reliability/channels/fragmentation. <see cref="ENetMultiplayerPeer"/> is exactly that
/// — an ENet host exposed as a <see cref="MultiplayerPeer"/> with manual <see cref="MultiplayerPeer.Poll"/>,
/// <see cref="PacketPeer.PutPacket"/> / <see cref="PacketPeer.GetPacket"/>, and target-peer/channel/mode
/// selection.
///
/// Lifecycle: construct <see cref="Server"/> or <see cref="Client"/>, subscribe to the events, then call
/// <see cref="Poll"/> once per host frame (before reading game state) so queued packets are dispatched and
/// connect/disconnect signals fire. <see cref="Send"/> queues an outgoing packet on the chosen channel.
/// </summary>
public abstract class NetTransport : IDisposable
{
    /// <summary>The Godot peer ID Godot assigns the server itself (host) — peers are positive ids.</summary>
    public const int ServerPeerId = 1;

    protected ENetMultiplayerPeer Peer = null!;

    /// <summary>Raised (on <see cref="Poll"/>) when a peer connects. Arg is the Godot peer id.</summary>
    public event Action<int>? PeerConnected;

    /// <summary>Raised (on <see cref="Poll"/>) when a peer disconnects. Arg is the Godot peer id.</summary>
    public event Action<int>? PeerDisconnected;

    /// <summary>Raised for each received packet: (sourcePeerId, channel, payload). The payload span is only
    /// valid for the duration of the callback — copy out what you must retain.</summary>
    public event Action<int, int, byte[]>? PacketReceived;

    /// <summary>True once the underlying ENet host is up.</summary>
    public bool IsActive => Peer is not null && Peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected;

    /// <summary>
    /// Pump ENet: poll the host, fire connect/disconnect for any peers whose status changed, then drain all
    /// queued packets, raising <see cref="PacketReceived"/> for each. Call once per frame before the game
    /// reads its inputs/snapshots. Safe to call when inactive (no-op).
    /// </summary>
    public virtual void Poll()
    {
        if (Peer is null)
            return;

        // A DEAD peer (a connect attempt that timed out/was refused, or a torn-down link) isn't pollable —
        // Godot's ENet layer prints a native "The multiplayer instance isn't currently active" ERROR on every
        // Poll (the 2026-07-11 join-test storm: a client whose connect expired spammed stderr for its whole
        // session). Drain the connection events one last time so PeerDisconnected/loss detection still fires,
        // then go quiet; the host's reconnect logic (ClientNet) re-creates the peer, it never revives this one.
        if (Peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected)
        {
            DrainConnectionEvents();
            return;
        }

        Peer.Poll();

        DrainConnectionEvents();

        // Drain packets. ENetMultiplayerPeer queues across all peers/channels; GetPacket returns the next,
        // with the source peer + channel queryable on the peer.
        while (Peer.GetAvailablePacketCount() > 0)
        {
            int from = Peer.GetPacketPeer();
            int channel = Peer.GetPacketChannel();
            byte[] data = Peer.GetPacket();
            if (data is { Length: > 0 })
                PacketReceived?.Invoke(from, channel, data);
        }
    }

    /// <summary>
    /// Push any queued outgoing packets onto the wire NOW (an ENet host service send pass) WITHOUT draining
    /// incoming — so a packet just <see cref="Send"/>-queued this frame physically leaves the socket immediately
    /// instead of waiting for the next <see cref="Poll"/>. On the in-process listen loop this lets the peer on
    /// the other end consume the packet on its very next service instead of a render-frame later: flushing the
    /// client's input after sampling lets the server tick consume it next tick, and flushing the server's
    /// snapshot after ticking lets the client receive it the same frame — together cutting ~2 frames of
    /// input→fire→feedback latency with no change to tick ordering or received-packet handling. No-op when
    /// inactive. (Godot's <c>ENetMultiplayerPeer.Host</c> is the underlying <c>ENetConnection</c>; its
    /// <c>Flush()</c> is enet_host_flush — send-only, it never receives.)
    /// </summary>
    public void Flush() => Peer?.Host?.Flush();

    /// <summary>
    /// Send <paramref name="payload"/> to <paramref name="targetPeerId"/> (use <see cref="ServerPeerId"/> from
    /// a client, or a client's id / <see cref="Godot.MultiplayerPeer.TargetPeerBroadcast"/> from the server)
    /// on the reliable or unreliable channel. Sets the transfer mode + channel + target on the peer, then
    /// enqueues the packet for the next <see cref="ENetMultiplayerPeer"/> flush (which Godot does after the
    /// scene-tree process step, or on the next <see cref="Poll"/>).
    /// </summary>
    public void Send(int targetPeerId, ReadOnlySpan<byte> payload, bool reliable)
    {
        if (Peer is null || payload.IsEmpty)
            return;

        Peer.SetTargetPeer(targetPeerId);
        Peer.TransferMode = reliable
            ? MultiplayerPeer.TransferModeEnum.Reliable
            : MultiplayerPeer.TransferModeEnum.Unreliable;
        Peer.TransferChannel = reliable ? NetProtocol.ReliableChannel : NetProtocol.UnreliableChannel;
        // ENetMultiplayerPeer.PutPacket has a ReadOnlySpan overload that copies into ENet's packet buffer
        // without an intermediate managed array — keep the hot send path allocation-free.
        Peer.PutPacket(payload);
    }

    /// <summary>Broadcast to every connected peer (server-side convenience).</summary>
    public void Broadcast(ReadOnlySpan<byte> payload, bool reliable)
        => Send((int)MultiplayerPeer.TargetPeerBroadcast, payload, reliable);

    // --- connect/disconnect: ENetMultiplayerPeer raises Godot signals; we bridge them to C# events. ---
    private readonly List<int> _pendingConnects = new();
    private readonly List<int> _pendingDisconnects = new();

    // ENet per-peer UNRELIABLE packet throttle. A fresh peer starts with the throttle pinned near 0 and only
    // recovers it at the default recalc interval (ENET_PEER_PACKET_THROTTLE_INTERVAL = 5000 ms), so for the first
    // ~5 s it DROPS almost every unreliable datagram — which starved the client's input on connect (the player
    // crawled while the client predicted full-speed → the spawn-stutter; confirmed: throttle 0→32 exactly when
    // input began flowing, with loss=0). We reconfigure every peer the moment it connects to recover fast: a SHORT
    // recalc interval + full acceleration → the throttle reaches its max within ~one short interval instead of 5 s,
    // and a modest deceleration still lets it back off under genuine remote congestion. Godot's
    // ENetPacketPeer.ThrottleConfigure == enet_peer_throttle_configure (the change is replicated to the other end).
    private const int ThrottleIntervalMs = 100;                          // recalc 50× faster than the 5000 ms default
    private const int ThrottleAccel = 32;                                // ENET_PEER_PACKET_THROTTLE_SCALE → max in one interval
    private const int ThrottleDecel = 4;                                 // still backs off on real congestion

    protected void HookSignals()
    {
        Peer.PeerConnected += id =>
        {
            Peer.GetPeer((int)id)?.ThrottleConfigure(ThrottleIntervalMs, ThrottleAccel, ThrottleDecel);
            _pendingConnects.Add((int)id);
        };
        Peer.PeerDisconnected += id => _pendingDisconnects.Add((int)id);
    }

    private void DrainConnectionEvents()
    {
        if (_pendingConnects.Count > 0)
        {
            // copy then clear so a handler that sends (re-entrant Poll is not expected, but be safe) can't
            // mutate the list mid-iteration.
            for (int i = 0; i < _pendingConnects.Count; i++)
                PeerConnected?.Invoke(_pendingConnects[i]);
            _pendingConnects.Clear();
        }
        if (_pendingDisconnects.Count > 0)
        {
            for (int i = 0; i < _pendingDisconnects.Count; i++)
                PeerDisconnected?.Invoke(_pendingDisconnects[i]);
            _pendingDisconnects.Clear();
        }
    }

    public virtual void Dispose()
    {
        if (Peer is not null)
        {
            Peer.Close();
            Peer.Dispose();
            Peer = null!;
        }
        GC.SuppressFinalize(this);
    }

    // =====================================================================================
    //  Server
    // =====================================================================================

    /// <summary>
    /// The host side: an ENet server bound to a UDP port. Peers connect to it; the server addresses them by
    /// their Godot-assigned positive peer ids (and the server itself is <see cref="ServerPeerId"/>).
    /// </summary>
    public sealed class Server : NetTransport
    {
        /// <summary>The connected client peer ids (excludes the server's own id).</summary>
        public IReadOnlyList<int> Peers => _peers;
        private readonly List<int> _peers = new();

        private Server() { }

        /// <summary>
        /// Start listening on <paramref name="port"/> for up to <paramref name="maxClients"/> clients. Returns
        /// the server, or null on failure (port in use, etc.). The caller subscribes to the events and calls
        /// <see cref="Poll"/> each frame. The peer list is maintained from the connect/disconnect signals.
        /// </summary>
        public static Server? Start(int port, int maxClients = 32)
        {
            var s = new Server();
            s.Peer = new ENetMultiplayerPeer();
            Error err = s.Peer.CreateServer(port, maxClients, NetProtocol.ChannelCount);
            if (err != Error.Ok)
            {
                GD.PrintErr($"[NetTransport.Server] CreateServer({port}) failed: {err}");
                s.Peer.Dispose();
                return null;
            }
            s.HookSignals();
            s.PeerConnected += id => { if (!s._peers.Contains(id)) s._peers.Add(id); };
            s.PeerDisconnected += id => s._peers.Remove(id);
            GD.Print($"[NetTransport.Server] listening on UDP {port} (max {maxClients}).");
            return s;
        }

        /// <summary>Forcibly drop a client (e.g. after a build-parity reject).</summary>
        public void Disconnect(int peerId, bool now = false)
        {
            if (Peer is null) return;
            Peer.DisconnectPeer(peerId, now);
        }

        /// <summary>QC <c>CS(e).ping</c> (server/sv_main.qc bot_think / scoreboard.qc SP_PING): a connected
        /// peer's round-trip time in milliseconds — ENet's own smoothed mean RTT estimate for the peer
        /// (<see cref="ENetPacketPeer.PeerStatistic.RoundTripTime"/>, in ms), the "ping" the scoreboard shows.
        /// Returns -1 for an unknown/disconnected peer (a bot / loopback host reads ~0). Mirrors the client-side
        /// <see cref="Client.RoundTripMs"/> so the server can network each human's ping on the score row.</summary>
        public int RoundTripMs(int peerId)
        {
            if (Peer is null) return -1;
            ENetPacketPeer p = Peer.GetPeer(peerId);
            if (p is null) return -1;
            double rtt = p.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
            return (int)System.Math.Round(rtt);
        }

        /// <summary>QC <c>CS(e).ping_packetloss</c> (server/world.qc:74): a connected peer's measured packet loss
        /// as a 0..1 fraction. ENet reports <see cref="ENetPacketPeer.PeerStatistic.PacketLoss"/> on a 0..65536
        /// scale (ENET_PEER_PACKET_LOSS_SCALE); we normalize it. Returns 0 for an unknown/disconnected peer.</summary>
        public float PacketLoss(int peerId)
        {
            if (Peer is null) return 0f;
            ENetPacketPeer p = Peer.GetPeer(peerId);
            if (p is null) return 0f;
            return Mathf.Clamp((float)(p.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss) / 65536.0), 0f, 1f);
        }
    }

    // =====================================================================================
    //  Client
    // =====================================================================================

    /// <summary>
    /// The client side: an ENet client connected to one server. All <see cref="Send"/> targets are the
    /// server (<see cref="ServerPeerId"/>); <see cref="PacketReceived"/> always reports the server as source.
    /// </summary>
    public sealed class Client : NetTransport
    {
        /// <summary>True once the ENet connection handshake has completed (status == Connected).</summary>
        public bool IsConnected => Peer is not null && Peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

        /// <summary>True while still negotiating the ENet connection (status == Connecting).</summary>
        public bool IsConnecting => Peer is not null && Peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connecting;

        /// <summary>
        /// The round-trip time to the server in milliseconds — ENet's own smoothed mean RTT estimate for the
        /// server peer (<see cref="ServerPeerId"/>), the "ping" the HUD shows. Returns -1 when not yet connected.
        /// This is measured by ENet from its reliable-packet acknowledgements (independent of the gameplay
        /// snapshot-time echo the server uses for antilag in <see cref="ServerNet"/>), so it's available client-side
        /// with no protocol cooperation. On a loopback listen server it reads ~0; on a remote server it's the real
        /// network ping. (Godot exposes ENet's per-peer <c>ENetPacketPeer</c> via
        /// <see cref="ENetMultiplayerPeer.GetPeer"/>; <see cref="ENetPacketPeer.PeerStatistic.RoundTripTime"/> is in ms.)
        /// </summary>
        public int RoundTripMs()
        {
            if (!IsConnected)
                return -1;
            ENetPacketPeer p = Peer.GetPeer(ServerPeerId);
            if (p is null)
                return -1;
            double rtt = p.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
            return (int)System.Math.Round(rtt);
        }

        private Client() { }

        /// <summary>
        /// Begin connecting to <paramref name="host"/>:<paramref name="port"/>. Returns immediately — the ENet
        /// handshake completes asynchronously; watch <see cref="IsConnected"/> (or the transport-level
        /// <see cref="NetControl.HandshakeAccept"/>) before sending gameplay. Returns null on a setup failure.
        /// </summary>
        public static Client? Connect(string host, int port)
        {
            var c = new Client();
            c.Peer = new ENetMultiplayerPeer();
            Error err = c.Peer.CreateClient(host, port, NetProtocol.ChannelCount);
            if (err != Error.Ok)
            {
                GD.PrintErr($"[NetTransport.Client] CreateClient({host}:{port}) failed: {err}");
                c.Peer.Dispose();
                return null;
            }
            c.HookSignals();
            GD.Print($"[NetTransport.Client] connecting to {host}:{port} …");
            return c;
        }

        /// <summary>Send a packet to the server on the chosen channel.</summary>
        public void SendToServer(ReadOnlySpan<byte> payload, bool reliable)
            => Send(ServerPeerId, payload, reliable);

        /// <summary>Diagnostic (surfaced by net_input_trace): the server peer's ENet throttle/loss/RTT stats. The
        /// packet throttle (0..32, ENET_PEER_PACKET_THROTTLE_SCALE) gates UNRELIABLE sends — a low value drops most
        /// of them (the bug the per-peer ThrottleConfigure in HookSignals fixes); packetLoss is ENet's measured loss
        /// (0..65536). Returns (-1,…) if not connected.</summary>
        public (double Throttle, double ThrottleLimit, double Loss, double Rtt) DbgEnetStats()
        {
            if (!IsConnected) return (-1, -1, -1, -1);
            ENetPacketPeer p = Peer.GetPeer(ServerPeerId);
            if (p is null) return (-1, -1, -1, -1);
            return (p.GetStatistic(ENetPacketPeer.PeerStatistic.PacketThrottle),
                    p.GetStatistic(ENetPacketPeer.PeerStatistic.PacketThrottleLimit),
                    p.GetStatistic(ENetPacketPeer.PeerStatistic.PacketLoss),
                    p.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime));
        }
    }
}

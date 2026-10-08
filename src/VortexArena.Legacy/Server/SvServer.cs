// Port of Base/darkplaces/sv_main.c SV_Frame (the network half), SV_CheckTimeouts, SV_ConnectClient
// (the half for a real connection), SV_DropClient's goodbye, SV_Shutdown; netconn.c
// NetConn_ServerParsePacket (dispatch of a datagram to its client's channel) and NetConn_ServerFrame;
// sv_ccmds.c SV_Map_f / SV_Changelevel_f / SV_Restart_f (acting on the level's request); host.c
// Host_Frame's order: receive, run the server, send.
using System.Net;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

/// <summary>
/// What a connected client has beyond its slot and its message buffers: the channel, its address,
/// and the per-client halves of the entity streams and the file download.
/// </summary>
public sealed class SvNetConnection : SvConnection
{
    internal SvNetConnection(IPEndPoint address, DpNetChannel channel) : base(channel.Reliable)
    {
        Address = address;
        Channel = channel;
    }

    public IPEndPoint Address { get; }
    public DpNetChannel Channel { get; }
    /// <summary>conn->timeout: the client is dropped when host.realtime passes it.</summary>
    public double Timeout { get; internal set; }
    internal double KeepAliveTime, NameTime, ClMovementDisableTimeout;
    internal SvEntityFrame5Database? EntityDatabase;
    internal SvCsqcEntityFrames? CsqcFrames;
    internal SvDownload? Download;
    internal int LatestFrameNum, NumSkippedEntityFrames;
    internal uint LastMoveSequence;
    /// <summary>client->visibletime[]: until when an entity last seen by a line-of-sight test stays sent.</summary>
    internal double[]? VisibleTime;
    internal int DatagramsThisFrame;
    /// <summary>Datagrams and bytes sent to and received from this client.</summary>
    public long DatagramsSent { get; internal set; }
    public long BytesSent { get; internal set; }
    public long DatagramsReceived { get; internal set; }
}

public sealed class SvServerOptions
{
    public int MaxClients { get; init; } = 8;
    /// <summary>See <see cref="SvqcHostOptions.KeepRunningAfterFault"/>.</summary>
    public bool KeepRunningAfterFault { get; init; }
    public int? RandomSeed { get; init; }
    /// <summary>The most datagrams one client is heard out on between two server frames. DarkPlaces
    /// reads until its socket is empty; this bounds what one address can make the server do.</summary>
    public int MaxDatagramsPerClientPerFrame { get; init; } = 256;
    /// <summary>Console output of the engine (Con_Printf).</summary>
    public Action<string>? Print { get; init; }
}

/// <summary>
/// A whole server without a socket: the level (<see cref="SvqcHost"/>), the player slots, the
/// connectionless handshake and server-browser replies, each client's channel, signon, input, entity
/// streams and download - everything between a datagram arriving and datagrams leaving.
///
/// The owner supplies time and datagrams and takes datagrams back:
/// <code>
/// server.Start("stormkeep");
/// each tick:
///     foreach datagram received: server.Receive(datagram, from, now, outgoing);
///     server.Frame(now, outgoing);
///     foreach ((to, bytes) in outgoing) socket.Send(bytes, to);
/// </code>
/// Nothing in here touches a socket or a clock, so the test suite runs it against the legacy client
/// over a pair of queues and a console program runs it on UDP.
///
/// Everything a client sends is hostile: every length, index, entity number, string and command is
/// checked where it is read, no datagram can make a method here throw, and a client that sends
/// nonsense is dropped the way DarkPlaces drops it.
/// </summary>
public sealed partial class SvServer : IDisposable, ISvConnectionlessHost<IPEndPoint>, ISvEntityFrame5Host, ISvCsqcEntityHost
{
    private readonly SvEnvironment _env;
    private readonly SvServerOptions _options;
    private readonly SvClient[] _clients;
    private readonly Dictionary<IPEndPoint, SvClient> _byAddress = new();
    private readonly SvConnectionless<IPEndPoint> _connectionless;
    private readonly SvCsqcEntityVersions _csqcVersions = new();
    private readonly List<byte[]> _scratch = new();
    private readonly Random _random;
    private List<(IPEndPoint To, byte[] Datagram)>? _outgoing;
    private SvCsqcProgram? _csqcProgram;
    private double _lastFrame;
    private bool _started, _disposed;

    public SvServer(SvEnvironment environment, SvServerOptions? options = null)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? new SvServerOptions();
        int maxClients = Math.Clamp(_options.MaxClients, 1, DpProtocol.MaxScoreboard);
        _clients = new SvClient[maxClients];
        for (int i = 0; i < maxClients; i++) _clients[i] = new SvClient(i);
        _random = _options.RandomSeed is { } seed ? new Random(seed) : new Random();
        _connectionless = new SvConnectionless<IPEndPoint>(this, SvUdpTransport.WithoutPort, null, _options.RandomSeed is null ? null : _random);
    }

    /// <summary>The running level, or null before <see cref="Start"/> and after a level that would not load.</summary>
    public SvqcHost? Host { get; private set; }
    public IReadOnlyList<SvClient> Clients => _clients;
    public SvEnvironment Environment => _env;
    /// <summary>host.realtime as last given to <see cref="Frame"/> or <see cref="Receive"/>.</summary>
    public double RealTime { get; private set; }
    public long DatagramsReceived { get; private set; }
    public long DatagramsSent { get; private set; }
    public long BytesSent { get; private set; }
    /// <summary>Datagrams from clients that were refused before parsing (not a client, flood cap).</summary>
    public long DatagramsIgnored { get; private set; }
    public long ConnectionlessPackets { get; private set; }
    public long LevelsStarted { get; private set; }
    /// <summary>The console asked for the server to end.</summary>
    public bool QuitRequested => Host?.QuitRequested ?? false;
    /// <summary>What happened, for a log: connections, drops, level changes. Never chat.</summary>
    public event Action<string>? Event;
    /// <summary>A level has started (SV_SpawnServer returned with a level running): its host, which is
    /// also <see cref="Host"/>. Raised for the first level and for every one after a change.</summary>
    public event Action<SvqcHost>? LevelStarted;
    /// <summary>The running level is about to be shut down for a level change or the server's end;
    /// its counters (faults, warnings, unimplemented builtins) are still readable.</summary>
    public event Action<SvqcHost>? LevelEnding;
    /// <summary>
    /// While <see cref="LevelEnding"/> is raised for a level change: which command asked for it. Null at
    /// any other time, and when the level ends because the server does. <see cref="SvMapRequest.Map"/> is
    /// the one that matters to a client: SV_Map_f drops everyone, and whoever wants to play the new level
    /// has to connect again (the local player of a listen server does - "connect local").
    /// </summary>
    public SvMapRequest? LevelChangeKind { get; private set; }
    /// <summary>QuakeC faults of every level this server has run, the current one included.</summary>
    public long TotalFaults => _faultsOfEndedLevels + (Host?.FaultCount ?? 0);
    private long _faultsOfEndedLevels;
    private int _serverFlags;
    private int _spawnFrames;
    private bool _publicNoticeGiven;

    /// <summary>
    /// "islocal" (netconn.c:3213, LHNETADDRESSTYPE_LOOP): which addresses are endpoints inside this
    /// process rather than peers on a socket. Such a client is exempt from sv_public and its
    /// .netaddress is "local", which is what the program's IS_LOCAL tests. Set by the owner of the
    /// transport before any client connects; null means no address is local.
    /// </summary>
    public Func<IPEndPoint, bool>? IsLocalAddress
    {
        get => _connectionless.IsLocal;
        set => _connectionless.IsLocal = value;
    }

    /// <summary>
    /// Drops whoever is connected from an address, as SV_DropClient with leaving set: the peer is
    /// gone and is not written to. False if no client has that address.
    /// </summary>
    public bool DropAddress(IPEndPoint address, string reason)
    {
        if (Host is not { } host || !_byAddress.TryGetValue(address, out SvClient? client)) return false;
        host.DropClient(client, reason, leaving: true);
        return true;
    }

    /// <summary>Cbuf_AddText on the server console: queue console text for the next frame. Dropped if no level is running.</summary>
    public void AddCommandText(string text) => Host?.AddCommandText(text);

    private void Note(string text) => Event?.Invoke(text);
    private void Print(string text) => _options.Print?.Invoke(text);
    private float Cvar(string name, float fallback) => _env.Cvars.Has(name) ? _env.Cvars.GetFloat(name) : fallback;

    // ---- levels ----------------------------------------------------------------------------------------

    /// <summary>SV_Map_f on a server not yet running: start the first level. False if it could not be loaded.</summary>
    public bool Start(string map, double now = 0)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SvServer));
        RealTime = _lastFrame = now;
        _started = true;
        return SpawnLevel(map);
    }

    private bool SpawnLevel(string map)
    {
        SvqcHost? host = _env.StartLevel(map,
            new SvqcHostOptions { MaxClients = _clients.Length, KeepRunningAfterFault = _options.KeepRunningAfterFault, RandomSeed = _options.RandomSeed, ServerFlags = _serverFlags },
            _clients, _options.Print);
        Host = host;
        if (host is null) return false;
        LevelsStarted++;
        host.RealTime = RealTime;
        host.EdictFreed += OnEdictFreed;
        host.ClientDropped += OnClientDropped;
        host.ClientCommandHandler = ExecuteClientCommand;
        host.PacketLossCounter = _ => 0;   // the channel keeps no NETGRAPH_PACKETS history
        _csqcVersions.Clear();
        _connectionless.ResetFloodTables();
        _connectionless.Public = (int)Cvar("sv_public", 0);
        if (_connectionless.Public > 0 && !_publicNoticeGiven)
        {
            // NetConn_Heartbeat is not ported: sv_public 1 cannot list this server on a master, and
            // it must not look as though it had.
            _publicNoticeGiven = true;
            Print("sv_public is 1, but this server does not send master server heartbeats: it is NOT listed publicly and answers direct queries only (as with sv_public 0)\n");
        }
        // "reset timer after level change": SV_Frame zeroes the time it is handed on the frame a
        // level spawned and the one after, so the load's own duration is not simulated.
        _spawnFrames = 2;
        _csqcProgram = host.CsqcProgData is { } data ? SvCsqcProgram.Create(host.CsqcProgName, data) : null;
        // send serverinfo to all connected clients
        foreach (SvClient client in _clients)
            if (client.Active && client.Connection is SvNetConnection connection)
                host.Guard("SV_SendServerinfo", () => SendServerInfo(client, connection));
        Note($"level {host.WorldName} started: {host.Vm.NumEdicts} edicts, {host.ModelCount - 1} models, {host.SoundCount - 1} sounds, {host.FaultCount} faults");
        LevelStarted?.Invoke(host);
        return true;
    }

    // SV_Changelevel_f / SV_Map_f / SV_Restart_f, once the level that asked has finished its frame.
    private void ChangeLevel(string map, SvMapRequest kind)
    {
        bool dropEveryone = kind == SvMapRequest.Map;
        SvqcHost? old = Host;
        // SV_SpawnServer looks for the map file before it touches the running level: "SpawnServer: no
        // map file named ..." leaves the old level running (in Xonotic, sitting in its intermission).
        if (old is not null && !old.MapExists(map))
        {
            Print($"SpawnServer: no map file named maps/{Printable(map)}.bsp\n");
            Note($"level change to {Printable(map)} refused: no such map");
            // svs.changelevel_issued stays set in DarkPlaces here, which leaves the program unable to
            // ask again with the changelevel builtin; Xonotic asks by console command (localcmd), which
            // is not gated by it.
            return;
        }
        if (old is not null)
        {
            LevelChangeKind = kind;
            try { LevelEnding?.Invoke(old); }
            finally { LevelChangeKind = null; }
            _faultsOfEndedLevels += old.FaultCount;
            if (dropEveryone)
            {
                // SV_Shutdown: "This only happens at the end of a game, not between levels"
                foreach (SvClient client in _clients)
                    if (client.Active) old.DropClient(client, "Server shutting down");
            }
            else
            {
                // SV_Changelevel_f saves the spawn parameters; SV_Restart_f does not, so a restarted
                // level gives each player the parameters it entered the level with.
                if (kind == SvMapRequest.ChangeLevel) old.SaveSpawnParms();
                // tell all connected clients that we are going to a new level
                foreach (SvClient client in _clients)
                {
                    if (client.Connection is not { } connection) continue;
                    connection.Message.WriteByte((int)Svc.StuffText);
                    connection.Message.WriteString("reconnect\n");
                }
            }
            old.EdictFreed -= OnEdictFreed;
            old.ClientDropped -= OnClientDropped;
            old.ClientCommandHandler = null;
            // svs.serverflags: read back by SV_SaveSpawnparms, cleared by SV_Map_f ("haven't completed
            // an episode yet"), untouched by a restart.
            _serverFlags = dropEveryone ? 0 : old.ServerFlags;
            old.Shutdown();
        }
        Host = null;
        Note($"changing level to {map}");
        if (!SpawnLevel(map)) Note($"level {map} could not be started; the server has no level");
    }

    /// <summary>SV_Shutdown: drop everyone and end the level.</summary>
    public void Shutdown(List<(IPEndPoint To, byte[] Datagram)>? outgoing = null)
    {
        if (Host is not { } host) return;
        _outgoing = outgoing;
        foreach (SvClient client in _clients)
            if (client.Active) host.DropClient(client, "Server shutting down");
        _outgoing = null;
        LevelEnding?.Invoke(host);
        _faultsOfEndedLevels += host.FaultCount;
        host.EdictFreed -= OnEdictFreed;
        host.ClientDropped -= OnClientDropped;
        host.ClientCommandHandler = null;
        host.Shutdown();
        Host = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Host?.Dispose();
        Host = null;
    }

    // ---- the frame -------------------------------------------------------------------------------------

    /// <summary>
    /// One datagram from the network (NetConn_ServerParsePacket). Replies the channel or the
    /// connectionless layer owes at once are appended to <paramref name="outgoing"/>.
    /// </summary>
    public void Receive(ReadOnlySpan<byte> datagram, IPEndPoint from, double now, List<(IPEndPoint To, byte[] Datagram)> outgoing)
    {
        if (_disposed || !_started || from is null) return;
        if (now > RealTime) RealTime = now;
        DatagramsReceived++;
        if (Host is { } live) live.RealTime = RealTime;
        _outgoing = outgoing;
        try
        {
            _scratch.Clear();
            SvConnectionlessResult result = _connectionless.Handle(datagram, from, Math.Max(RealTime, 1e-3), _scratch);
            if (result.IsConnectionless)
            {
                ConnectionlessPackets++;
                foreach (byte[] reply in _scratch) Send(from, reply);
                return;
            }
            if (Host is not { } host || !_byAddress.TryGetValue(from, out SvClient? client) || client.Connection is not SvNetConnection connection)
            {
                DatagramsIgnored++;
                return;
            }
            if (++connection.DatagramsThisFrame > _options.MaxDatagramsPerClientPerFrame)
            {
                DatagramsIgnored++;
                return;
            }
            connection.DatagramsReceived++;
            _scratch.Clear();
            DpChannelReceive received = connection.Channel.Receive(datagram, RealTime, _scratch, out byte[]? message);
            foreach (byte[] reply in _scratch) Send(connection, reply);
            if (received == DpChannelReceive.Invalid) return;
            // NetConn_ReceivedMessage's newtimeout: "host_client->begun ? net_messagetimeout : net_connecttimeout"
            connection.Timeout = RealTime + (client.Begun ? Cvar("net_messagetimeout", 300) : Cvar("net_connecttimeout", 15));
            if (received == DpChannelReceive.Message && message is not null)
                host.Guard("SV_ReadClientMessage", () => ReadClientMessage(host, client, connection, message));
        }
        finally { _outgoing = null; }
    }

    /// <summary>
    /// The rest of SV_Frame: drop clients that have timed out, run the server frames that are due,
    /// send every client its messages, and act on a level change the frame asked for. Returns the
    /// number of server frames run.
    /// </summary>
    public int Frame(double now, List<(IPEndPoint To, byte[] Datagram)> outgoing)
    {
        if (_disposed || !_started) return 0;
        double elapsed = Math.Max(0, now - _lastFrame);
        _lastFrame = now;
        if (_spawnFrames > 0)
        {
            _spawnFrames--;
            elapsed = 0;
        }
        if (now > RealTime) RealTime = now;
        if (Host is not { } host) return 0;
        host.RealTime = RealTime;
        _outgoing = outgoing;
        int frames;
        try
        {
            // SV_CheckTimeouts
            foreach (SvClient client in _clients)
                if (client.Connection is SvNetConnection connection)
                {
                    connection.DatagramsThisFrame = 0;
                    if (RealTime > connection.Timeout) host.DropClient(client, "Timed out");
                }

            frames = host.Advance(elapsed);
            host.ExecuteCommands();
            // SV_SendClientMessages belongs to the frame block: it runs whenever that block does, also
            // when the block ran no physics (a paused server still talks to its clients).
            if (host.FrameBlockRan) host.Guard("SV_SendClientMessages", () => SendClientMessages(host));
        }
        finally { _outgoing = null; }

        if (host.PendingMap is { } map)
        {
            SvMapRequest kind = host.PendingMapKind;
            host.PendingMap = null;
            _outgoing = outgoing;
            try { ChangeLevel(map, kind); }
            finally { _outgoing = null; }
        }
        return frames;
    }

    private void Send(IPEndPoint to, byte[] datagram)
    {
        DatagramsSent++;
        BytesSent += datagram.Length;
        _outgoing?.Add((to, datagram));
    }

    private void Send(SvNetConnection connection, byte[] datagram)
    {
        connection.DatagramsSent++;
        connection.BytesSent += datagram.Length;
        Send(connection.Address, datagram);
    }

    // ---- connecting and dropping -----------------------------------------------------------------------

    string ISvConnectionlessHost<IPEndPoint>.GameName => "Xonotic";
    string ISvConnectionlessHost<IPEndPoint>.ModName => "data";
    int ISvConnectionlessHost<IPEndPoint>.GameVersion => (int)Cvar("gameversion", 0);
    int ISvConnectionlessHost<IPEndPoint>.MaxClients => _clients.Length;
    string ISvConnectionlessHost<IPEndPoint>.MapName => Host?.WorldBaseName ?? "";
    string ISvConnectionlessHost<IPEndPoint>.HostName => _env.Cvars.GetString("hostname");
    int ISvConnectionlessHost<IPEndPoint>.TeamPlay => (int)Cvar("teamplay", 0);

    string ISvConnectionlessHost<IPEndPoint>.WorldStatus =>
        Host is { State: not SvState.Dead } host && host.G.WorldStatus >= 0 ? host.Vm.GetString(host.Vm.GlobalInt(host.G.WorldStatus)) : "";

    void ISvConnectionlessHost<IPEndPoint>.GetStatusPlayers(List<SvStatusPlayer> players)
    {
        if (Host is not { State: not SvState.Dead } host) return;
        foreach (SvClient client in _clients)
        {
            if (!client.Active) continue;
            string status = host.Vm.GetString(host.Vm.FieldInt(client.Edict, host.F.ClientStatus));
            players.Add(new SvStatusPlayer(client.Name, client.Frags, client.Ping, client.Connection is null, client.Colors, status));
        }
    }

    SvClientPresence ISvConnectionlessHost<IPEndPoint>.FindClient(IPEndPoint address) =>
        !_byAddress.TryGetValue(address, out SvClient? client) ? SvClientPresence.None : client.Begun ? SvClientPresence.Begun : SvClientPresence.Connecting;

    // The connect case of NetConn_ServerParsePacket: "find a slot for the new client" and
    // SV_ConnectClient. The accept itself has already been written by the connectionless layer.
    bool ISvConnectionlessHost<IPEndPoint>.TryConnectClient(IPEndPoint address, string userInfo)
    {
        if (Host is not { State: SvState.Active } host || _byAddress.ContainsKey(address)) return false;
        foreach (SvClient client in _clients)
        {
            if (client.Active) continue;
            SvNetConnection connection = new(address, new DpNetChannel(RealTime));
            bool ok = host.Guard("SV_ConnectClient", () =>
            {
                // LHNETADDRESS_ToString(..., false): the host without the port, "local" for the loopback driver
                host.ConnectClient(client.Index, connection, IsLocalAddress is { } local && local(address) ? "local" : address.Address.ToString());
                SendServerInfo(client, connection);
            });
            if (!ok)
            {
                // The program faulted while taking the client in: leave the slot empty.
                client.Clear();
                return false;
            }
            _byAddress[address] = client;
            Note($"client connected from {address} as #{client.Edict}");
            return true;
        }
        return false;
    }

    // "duplicate connection request": a client already in the game asking to connect again (it did
    // not see the level change, or lost its state) is sent the level afresh.
    void ISvConnectionlessHost<IPEndPoint>.ReconnectClient(IPEndPoint address)
    {
        if (Host is { State: SvState.Active } host && _byAddress.TryGetValue(address, out SvClient? client) && client.Connection is SvNetConnection connection)
            host.Guard("SV_SendServerinfo", () => SendServerInfo(client, connection));
    }

    // SV_DropClient's network half, heard through the level's ClientDropped event: say goodbye three
    // times (the datagram is unreliable), close the connection, forget the address.
    private void OnClientDropped(SvClient client, string? reason, bool leaving)
    {
        if (client.Connection is not SvNetConnection connection) return;
        if (!leaving)
        {
            // DP7 has no reason string in svc_disconnect; the reason goes ahead of it as a print.
            DpMessageWriter goodbye = new(600);
            if (reason is not null)
            {
                goodbye.WriteByte((int)Svc.Print);
                goodbye.WriteString(reason.Length > 500 ? reason[..500] + "\n" : reason + "\n");
            }
            goodbye.WriteByte((int)Svc.Disconnect);
            for (int i = 0; i < 3; i++)
            {
                _scratch.Clear();
                connection.Channel.SuppressNewReliables = true;
                connection.Channel.Transmit(goodbye.WrittenSpan, RealTime, _scratch);
                foreach (byte[] datagram in _scratch) Send(connection, datagram);
            }
        }
        connection.Download?.Abort();
        _byAddress.Remove(connection.Address);
        _connectionless.ClearConnectFlood(connection.Address);
        Note($"client #{client.Edict} \"{Printable(client.Name)}\" dropped{(reason is null ? "" : ": " + reason)}");
    }

    private void OnEdictFreed(int edict)
    {
        _csqcVersions.EdictFreed(edict);
        foreach (SvClient client in _clients)
            if (client.Connection is SvNetConnection { CsqcFrames: { } frames }) frames.EdictFreed(edict);
    }

    private static string Printable(string s)
    {
        System.Text.StringBuilder text = new(Math.Min(s.Length, 64));
        foreach (char c in s)
        {
            if (text.Length >= 64) break;
            text.Append(c < ' ' || c == 0x7F ? '?' : c);
        }
        return text.ToString();
    }
}

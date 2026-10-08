// Port of Base/darkplaces/host.c Host_Frame's server half as a listen server runs it (network in,
// SV_Frame, network out, once per host frame), sv_ccmds.c SV_Map_f's hook into the local client
// (host.hook.ConnectLocal: the local player is connected over the loopback driver rather than a
// socket), netconn.c NetConn_OpenServerPorts (which addresses a server listens on: the loopback
// driver always, UDP only where net_address / the port say) and NetConn_Heartbeat's precondition
// (sv_public > 0) - of which only the precondition exists here: nothing is ever sent to a master.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace VortexArena.Legacy.Server;

/// <summary>The local game could not be started. The message says why (no data, no map, no program, port in use).</summary>
public sealed class SvLocalGameException : Exception
{
    public SvLocalGameException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What <see cref="SvLocalGame.Start"/> needs to know.</summary>
public sealed class SvLocalGameOptions
{
    /// <summary>A Xonotic "data" directory: its packs, default.cfg, progs.dat and maps.</summary>
    public required string DataDirectory { get; init; }
    /// <summary>Where files the server program writes go (DarkPlaces' user directory), or null to refuse every write.</summary>
    public string? WriteRoot { get; init; }
    /// <summary>The first map's base name ("stormkeep"), as the map command takes it.</summary>
    public string Map { get; init; } = "stormkeep";
    /// <summary>The game mode by the name a .mapinfo and the "gametype" command use ("dm", "ctf", "ca", ...;
    /// <see cref="SvGameTypes.All"/>), or null for whatever the configuration selects (deathmatch).
    /// A map that does not support the mode is played in one it does - the program decides, and
    /// <see cref="SvLocalGame.GameType"/> says what it chose.</summary>
    public string? GameType { get; init; }
    /// <summary>bot_number: how many bots the program keeps in the game.</summary>
    public int Bots { get; init; }
    /// <summary>svs.maxclients: player slots, bots included (1..255).</summary>
    public int MaxPlayers { get; init; } = 8;
    /// <summary>
    /// The UDP address to listen on for other players, or null (the default) for no socket at all:
    /// then the only way in is <see cref="SvLocalGame.AttachLocalClient"/>. The socket is bound to
    /// exactly this address - 127.0.0.1 for this machine only, a LAN interface's address for that
    /// LAN, IPAddress.Any only if the caller means every interface.
    /// </summary>
    public IPEndPoint? Listen { get; init; }
    /// <summary>sv_dedicated. False (the default) is DarkPlaces' listen server: Xonotic then starts
    /// matches without the 15-second wait, lets sv_autopause pause an empty or single-player game and
    /// does not kick idle players. True runs the dedicated-server configuration.</summary>
    public bool Dedicated { get; init; }
    /// <summary>Cvars to set after the default configuration has run and before the level starts ("+set" on a command line).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Cvars { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    /// <summary>Everything the server prints: the engine's console lines and the program's. Text arrives in pieces; a line ends at '\n'.</summary>
    public Action<string>? Print { get; init; }
    /// <summary>VM warnings (a program doing something DarkPlaces would warn about).</summary>
    public Action<string>? Warning { get; init; }
    /// <summary>A fixed seed for the program's random() and the server's own, for a reproducible game. Null: not seeded.</summary>
    public int? RandomSeed { get; init; }
    /// <summary>See <see cref="SvqcHostOptions.KeepRunningAfterFault"/>. False is DarkPlaces: a faulted level stops.</summary>
    public bool KeepRunningAfterFault { get; init; }
}

/// <summary>The stock game modes: the short name a .mapinfo and the "gametype" command use, and the cvar that selects it.</summary>
public static class SvGameTypes
{
    /// <summary>qcsrc/common/gametypes/gametype/*/*.qh, each gametype_init(this, title, short name, cvar, ...).</summary>
    public static readonly IReadOnlyList<(string ShortName, string Cvar)> All = new (string, string)[]
    {
        ("dm", "g_dm"), ("tdm", "g_tdm"), ("ctf", "g_ctf"), ("ca", "g_ca"), ("ft", "g_freezetag"), ("kh", "g_keyhunt"),
        ("dom", "g_domination"), ("lms", "g_lms"), ("ka", "g_keepaway"), ("tka", "g_tka"), ("cts", "g_cts"), ("rc", "g_race"),
        ("as", "g_assault"), ("ons", "g_onslaught"), ("nb", "g_nexball"), ("inv", "g_invasion"), ("mayhem", "g_mayhem"),
        ("tmayhem", "g_tmayhem"), ("duel", "g_duel"), ("surv", "g_survival"),
    };

    /// <summary>The cvar for a short name ("ctf") or for the cvar's own name ("g_ctf"), or null.</summary>
    public static string? CvarFor(string gameType)
    {
        foreach ((string shortName, string cvar) in All)
            if (shortName.Equals(gameType, StringComparison.OrdinalIgnoreCase) || cvar.Equals(gameType, StringComparison.OrdinalIgnoreCase)) return cvar;
        return null;
    }

    /// <summary>
    /// MapInfo_SwitchGameType as an operator does it before a level starts: the wanted mode's cvar 1,
    /// every other mode's 0. The program's MapInfo_LoadMapSettings then plays it, or - on a map that
    /// does not support it - switches to one the map does.
    /// </summary>
    public static bool Select(SvEnvironment environment, string gameType)
    {
        if (CvarFor(gameType) is not { } wanted) return false;
        foreach ((string _, string cvar) in All) environment.SetCvar(cvar, cvar == wanted ? "1" : "0");
        return true;
    }

    /// <summary>The short name of the mode whose cvar is set, or null: what the program settled on once a level has started.</summary>
    public static string? Current(SvEnvironment environment)
    {
        foreach ((string shortName, string cvar) in All)
            if (environment.Cvars.Has(cvar) && environment.Cvars.GetFloat(cvar) != 0) return shortName;
        return null;
    }
}

/// <summary>
/// One end of an in-process connection to an <see cref="SvLocalGame"/>: what a socket is to a
/// client, without one. The client puts the datagrams it would send to the server into
/// <see cref="Send"/> and takes the server's from <see cref="TryReceive"/>; the server reads and
/// writes the other ends inside <see cref="SvLocalGame.Frame"/>.
///
/// Both queues are bounded, like a socket buffer: when one is full the newest datagram is dropped
/// and counted, which the protocol treats as packet loss. Send and TryReceive may be called from a
/// thread other than the one that runs the server.
/// </summary>
public sealed class SvLocalEndpoint
{
    /// <summary>Datagrams a queue holds before it drops. A client sends at most a few per frame and
    /// the server a few per tick, so this is seconds of a stalled peer.</summary>
    public const int Capacity = 1024;

    private readonly ConcurrentQueue<byte[]> _toServer = new(), _toClient = new();
    private int _toServerCount, _toClientCount;
    private long _dropped;

    internal SvLocalEndpoint(IPEndPoint address) => Address = address;

    /// <summary>The address the server knows this endpoint by. It is not a network address: nothing
    /// is bound to it and no datagram from a socket is ever accepted as coming from it.</summary>
    public IPEndPoint Address { get; }
    /// <summary>Set when the endpoint was detached or the game shut down: nothing more will arrive.</summary>
    public bool Closed { get; internal set; }
    /// <summary>Datagrams dropped because a queue was full or the endpoint closed.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The client's side: one datagram to the server. Copied; the caller may reuse the buffer.</summary>
    public void Send(ReadOnlySpan<byte> datagram)
    {
        if (Closed || datagram.IsEmpty || datagram.Length > SvUdpTransport.ReceiveBufferSize || Volatile.Read(ref _toServerCount) >= Capacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        Interlocked.Increment(ref _toServerCount);
        _toServer.Enqueue(datagram.ToArray());
    }

    /// <summary>The client's side: the next datagram from the server, if one is waiting.</summary>
    public bool TryReceive(out byte[] datagram)
    {
        if (_toClient.TryDequeue(out byte[]? next))
        {
            Interlocked.Decrement(ref _toClientCount);
            datagram = next;
            return true;
        }
        datagram = Array.Empty<byte>();
        return false;
    }

    internal bool TryTakeForServer(out byte[] datagram)
    {
        if (_toServer.TryDequeue(out byte[]? next))
        {
            Interlocked.Decrement(ref _toServerCount);
            datagram = next;
            return true;
        }
        datagram = Array.Empty<byte>();
        return false;
    }

    internal void Deliver(byte[] datagram)
    {
        if (Closed || Volatile.Read(ref _toClientCount) >= Capacity)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        Interlocked.Increment(ref _toClientCount);
        _toClient.Enqueue(datagram);
    }
}

/// <summary>
/// A Xonotic server for a local game, for a host process to embed: the stock server program
/// (progs.dat) on this engine's QuakeC VM, a map, a game mode, bots, and a way in for the process's
/// own player that needs no socket.
///
/// <code>
/// using SvLocalGame game = SvLocalGame.Start(new SvLocalGameOptions
/// {
///     DataDirectory = xonoticData, WriteRoot = userDir, Map = "stormkeep", GameType = "dm", Bots = 4, MaxPlayers = 8,
///     Listen = null,                       // or new IPEndPoint(lanAddress, 26000) to let LAN players in
///     Print = text => console.Write(text),
/// }, now);
/// game.LevelChanged += (from, to) => ...;  // the match ended and the next map is up
/// SvLocalEndpoint wire = game.AttachLocalClient();
/// // the local client: wire.Send(datagram) where it would send to a socket, wire.TryReceive(out d) where it would read one
///
/// each frame:                              // any rate; the server runs the ticks that are due
///     game.Frame(now);                     // now: seconds on any steady clock, the same one every call
/// game.Command("kick # 2");                // the server console; output comes back through Print
/// game.Dispose();                          // says goodbye to every client, ends the level, closes the socket
/// </code>
///
/// <b>Threading.</b> Start, Frame, Command, AttachLocalClient, DetachLocalClient and Dispose belong
/// to one thread. Only <see cref="SvLocalEndpoint.Send"/> and <see cref="SvLocalEndpoint.TryReceive"/>
/// may be called from another.
///
/// <b>Time.</b> The owner's clock is the server's only clock (host.realtime): timeouts, rate limits
/// and the tick accumulator all run on the value given to <see cref="Frame"/>. A frame that arrives
/// late runs at most a bounded number of ticks (sv_maxphysicsframesperserverframe), so a stall in
/// the host slows the game down rather than freezing it to catch up.
///
/// <b>Level changes.</b> When a match ends the program asks for the next map; the old level is shut
/// down and the new one started inside the <see cref="Frame"/> call that sees the request, which
/// therefore takes as long as a level load (seconds). Connected clients are carried across: each is
/// sent the new level's svc_serverinfo and signs on again, as on a DarkPlaces server.
/// <see cref="LevelChanged"/> is raised when the new level is running.
///
/// <b>What a local game does not do to the network.</b> With <see cref="SvLocalGameOptions.Listen"/>
/// null no socket exists. With it set, one UDP socket is bound to exactly that address. The server
/// never contacts a master server: heartbeats (NetConn_Heartbeat) are not ported, so sv_public 1
/// does not list the game anywhere, and Start says so on the console if the configuration asks for
/// it. sv_public keeps its other meanings (0: answers LAN browser queries; -1: no status replies;
/// -2: no connections) for socket peers; the in-process endpoints are exempt, as DarkPlaces' loopback is.
///
/// Everything a socket peer sends is hostile and is handled as on a public server (see
/// <see cref="SvServer"/>). A datagram from a socket that claims an in-process endpoint's address is
/// discarded before the server sees it.
/// </summary>
public sealed class SvLocalGame : IDisposable
{
    /// <summary>The most datagrams read from the socket in one <see cref="Frame"/>; the rest wait in the socket's buffer.</summary>
    public const int MaxSocketDatagramsPerFrame = 4096;

    private readonly SvLocalGameOptions _options;
    private readonly List<SvLocalEndpoint> _endpoints = new();
    private readonly Dictionary<IPEndPoint, SvLocalEndpoint> _byAddress = new();
    private readonly List<(IPEndPoint To, byte[] Datagram)> _outgoing = new();
    private readonly SvUdpTransport? _transport;
    private int _nextEndpoint = 1;
    private string _map;
    private bool _disposed;

    private SvLocalGame(SvLocalGameOptions options, SvEnvironment environment, SvServer server, SvUdpTransport? transport)
    {
        _options = options;
        Environment = environment;
        Server = server;
        _transport = transport;
        _map = server.Host?.WorldBaseName ?? options.Map;
        server.IsLocalAddress = address => _byAddress.ContainsKey(address);
        server.LevelStarted += host =>
        {
            string from = _map;
            _map = host.WorldBaseName;
            LevelChanged?.Invoke(from, _map);
        };
    }

    /// <summary>The server itself: slots, counters, the running level (<see cref="SvServer.Host"/>).</summary>
    public SvServer Server { get; }
    /// <summary>Cvars, console interpreter and mounted game data.</summary>
    public SvEnvironment Environment { get; }
    /// <summary>The running map's base name.</summary>
    public string Map => _map;
    /// <summary>The short name of the game mode the program is playing ("dm", "ctf", ...).</summary>
    public string GameType => SvGameTypes.Current(Environment) ?? "dm";
    /// <summary>The bound UDP address, or null when the game has no socket.</summary>
    public IPEndPoint? ListenAddress => _transport?.LocalEndPoint;
    /// <summary>False once the level has stopped: the program faulted (and KeepRunningAfterFault is off), a map could not be loaded, or the console said quit.</summary>
    public bool Running => !_disposed && !Server.QuitRequested && Server.Host is { State: SvState.Active } host && (!host.Faulted || _options.KeepRunningAfterFault);
    /// <summary>The server console asked to end the game ("quit"). The owner decides what that means.</summary>
    public bool QuitRequested => Server.QuitRequested;
    /// <summary>QuakeC faults of every level so far. Not zero means the program misbehaved or this engine did.</summary>
    public long Faults => Server.TotalFaults;
    /// <summary>
    /// A level change has completed: (map it was, map it is now). Raised inside <see cref="Frame"/>,
    /// after the new level has started and before any client has signed on to it. Also raised for
    /// "restart" (both names equal).
    /// </summary>
    public event Action<string, string>? LevelChanged;
    /// <summary>Connections, drops and level changes, one line each, for a log. Never chat.</summary>
    public event Action<string>? Event
    {
        add => Server.Event += value;
        remove => Server.Event -= value;
    }

    /// <summary>
    /// Mounts the data, runs the default configuration, starts the first level and - if asked - binds
    /// the UDP socket. Takes as long as a level load.
    /// </summary>
    /// <param name="now">The owner's clock, in seconds; <see cref="Frame"/> continues from it.</param>
    /// <exception cref="SvLocalGameException">Nothing was started; nothing is left open.</exception>
    public static SvLocalGame Start(SvLocalGameOptions options, double now = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        SvEnvironment environment;
        try { environment = new SvEnvironment(options.DataDirectory, options.WriteRoot, options.Print, options.Warning, options.Dedicated); }
        catch (DirectoryNotFoundException e) { throw new SvLocalGameException(e.Message, e); }

        SvServer? server = null;
        SvUdpTransport? transport = null;
        try
        {
            if (!environment.DefaultsExecuted) throw new SvLocalGameException($"\"{options.DataDirectory}\" has no default.cfg: not a Xonotic data directory");
            environment.SetCvar("bot_number", Math.Max(0, options.Bots).ToString(CultureInfo.InvariantCulture));
            // A local game is not advertised unless the caller's own cvars say otherwise - and then
            // only as far as SvServer goes, which is a console line saying it cannot be.
            environment.SetCvar("sv_public", "0");
            if (options.GameType is { } gameType && !SvGameTypes.Select(environment, gameType))
                throw new SvLocalGameException($"\"{gameType}\" is not a game mode (known: {string.Join(" ", SvGameTypes.All.Select(g => g.ShortName))})");
            foreach ((string name, string value) in options.Cvars) environment.SetCvar(name, value);

            server = new SvServer(environment, new SvServerOptions
            {
                MaxClients = options.MaxPlayers, KeepRunningAfterFault = options.KeepRunningAfterFault, RandomSeed = options.RandomSeed, Print = options.Print,
            });
            if (!server.Start(options.Map, now) || server.Host is null)
                throw new SvLocalGameException($"the level \"{options.Map}\" could not be started (see the console output)");
            if (server.Host.Faulted && !options.KeepRunningAfterFault)
                throw new SvLocalGameException($"the server program faulted while starting \"{options.Map}\": {server.Host.Faults[0].Message}");

            if (options.Listen is { } listen)
            {
                try { transport = new SvUdpTransport(listen); }
                catch (SocketException e) { throw new SvLocalGameException($"cannot listen on {listen}: {e.Message}", e); }
            }
            return new SvLocalGame(options, environment, server, transport);
        }
        catch
        {
            transport?.Dispose();
            if (server is not null)
            {
                server.Shutdown();
                server.Dispose();
            }
            environment.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A new in-process endpoint for a client in this process. The client then connects through it
    /// exactly as it would through a socket (getchallenge, connect, signon); the server knows it as
    /// "local", which exempts it from sv_public and is what the program's IS_LOCAL tests.
    /// </summary>
    public SvLocalEndpoint AttachLocalClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nextEndpoint > 250) throw new InvalidOperationException("too many local endpoints were attached");
        // 0.0.0.N: "this host on this network" addresses, which no datagram on a socket legitimately
        // comes from - and Frame discards any that claims to. One host part per endpoint, because the
        // server's connect-flood limit is per host.
        SvLocalEndpoint endpoint = new(new IPEndPoint(new IPAddress(new byte[] { 0, 0, 0, (byte)_nextEndpoint++ }), 1));
        _endpoints.Add(endpoint);
        _byAddress[endpoint.Address] = endpoint;
        return endpoint;
    }

    /// <summary>Closes an endpoint. A client still connected through it is dropped as if its cable had been cut.</summary>
    public void DetachLocalClient(SvLocalEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!_endpoints.Remove(endpoint)) return;
        endpoint.Closed = true;
        Server.DropAddress(endpoint.Address, "Disconnect by user");
        _byAddress.Remove(endpoint.Address);
    }

    /// <summary>
    /// One host frame of the server: read what the endpoints and the socket hold, run the server
    /// ticks that are due at <paramref name="now"/>, send what the server has to say, and carry out
    /// a level change if the frame asked for one. Returns the number of ticks run (0 when none was
    /// due yet). Nothing a peer sent can make this throw.
    /// </summary>
    public int Frame(double now)
    {
        if (_disposed) return 0;
        _outgoing.Clear();
        foreach (SvLocalEndpoint endpoint in _endpoints)
        {
            // Bounded by the queue's capacity; the server bounds what one address may make it do.
            for (int i = 0; i < SvLocalEndpoint.Capacity && endpoint.TryTakeForServer(out byte[] datagram); i++)
                Server.Receive(datagram, endpoint.Address, now, _outgoing);
        }
        if (_transport is not null)
        {
            for (int i = 0; i < MaxSocketDatagramsPerFrame && _transport.TryReceive(out byte[] datagram, out IPEndPoint from); i++)
            {
                if (IsReserved(from))
                {
                    SocketDatagramsRefused++;
                    continue;
                }
                Server.Receive(datagram, from, now, _outgoing);
            }
        }
        int ticks = Server.Frame(now, _outgoing);
        Flush();
        return ticks;
    }

    /// <summary>Datagrams from the socket discarded for claiming an in-process endpoint's address.</summary>
    public long SocketDatagramsRefused { get; private set; }

    // 0.0.0.0/8 never arrives on a socket honestly; refuse the whole block, not only the addresses in use.
    private static bool IsReserved(IPEndPoint from)
    {
        IPAddress address = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out _) && bytes[0] == 0;
    }

    private void Flush()
    {
        foreach ((IPEndPoint to, byte[] datagram) in _outgoing)
        {
            if (_byAddress.TryGetValue(to, out SvLocalEndpoint? endpoint)) endpoint.Deliver(datagram);
            else if (_transport is not null && !IsReserved(to)) _transport.Send(datagram, to);
        }
        _outgoing.Clear();
    }

    /// <summary>
    /// Console text for the server ("kick # 2", "endmatch", "gotomap boil", "sv_cmd ...", a cvar
    /// assignment). It runs in the next <see cref="Frame"/>; what it prints comes back through
    /// <see cref="SvLocalGameOptions.Print"/>.
    /// </summary>
    public void Command(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;
        Server.AddCommandText(text.EndsWith('\n') ? text : text + "\n");
    }

    /// <summary>
    /// Ends the game: every client is told the server is shutting down (three times, the datagram
    /// being unreliable), the program's shutdown hook runs, the socket closes, the data is unmounted.
    /// Datagrams for local endpoints stay readable on them.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _outgoing.Clear();
        Server.Shutdown(_outgoing);
        Flush();
        foreach (SvLocalEndpoint endpoint in _endpoints) endpoint.Closed = true;
        _transport?.Dispose();
        Server.Dispose();
        Environment.Dispose();
    }
}

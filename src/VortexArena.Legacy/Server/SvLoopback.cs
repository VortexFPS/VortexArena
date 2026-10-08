// Port of Base/darkplaces/netconn.c's loopback driver (NetConn_Write / NetConn_Read on a
// LHNETADDRESSTYPE_LOOP socket: two queues of datagrams, one each way) and host.c Host_Frame's order
// for a listen server - the client's frame and the server's frame in one process, one after the
// other, on one clock.
using System.Net;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

/// <summary>
/// One headless legacy client of an <see cref="SvLoopback"/>: its session, its own cvar store and
/// console interpreter (a DarkPlaces client's are its own even when the server runs in the same
/// process), and the pair of datagram queues that stands in for its socket.
/// </summary>
public sealed class SvLoopbackClient : IDisposable
{
    private readonly SvServer _server;
    internal readonly Queue<byte[]> ToClient = new(), ToServer = new();
    internal double NextDraw;

    internal SvLoopbackClient(SvServer server, VirtualFileSystem files, IPEndPoint address, string? writeRoot, LegacyClientOptions? options,
        Action<string>? print, Action<string>? warning)
    {
        _server = server;
        Address = address;
        Cvars = new CvarService();
        ConfigInterpreter interpreter = new(Cvars, path => files.Exists(path) ? files.ReadText(path) : null);
        CsqcEngineCvars.Register(Cvars);
        Cvars.Register("pr_checkextension", "1");
        Cvars.Register("utf8_enable", "1");
        Cvars.Register("developer", "0");
        interpreter.ExecuteFile("default.cfg");
        LegacyQcHost services = new(Cvars, files)
        {
            WriteRoot = writeRoot,
            PrintSink = print ?? (_ => { }),
            WarningSink = warning ?? (_ => { }),
        };
        Services = services;
        Presentation = new HeadlessLegacyPresentation(files);
        options ??= new LegacyClientOptions();
        options.Client.Signon.Rate = Cvars.Has("_cl_rate") && Cvars.GetFloat("_cl_rate") > 0 ? (int)Cvars.GetFloat("_cl_rate") : 262144;
        options.Client.Signon.RateBurstSize = 1024;
        options.PredictMovement = !Cvars.Has("cl_movement") || Cvars.GetFloat("cl_movement") != 0;
        Session = new LegacyClientSession(services, interpreter, Presentation, options);
        if (Cvars.Has("cl_nettimesyncboundmode")) Session.Clock.BoundMode = (int)Cvars.GetFloat("cl_nettimesyncboundmode");
    }

    /// <summary>The address the server knows this client by. Nothing is bound to it.</summary>
    public IPEndPoint Address { get; }
    public LegacyClientSession Session { get; }
    public HeadlessLegacyPresentation Presentation { get; }
    /// <summary>The engine services the client's program runs against (its clock can be replaced).</summary>
    public LegacyQcHost Services { get; }
    public CvarService Cvars { get; }
    /// <summary>The input the client applies in the next step.</summary>
    public LegacyInput Input;
    /// <summary>While set, nothing this client sends reaches the server (a cable pulled out): its
    /// datagrams are discarded instead of queued.</summary>
    public bool Muted { get; set; }
    /// <summary>While set, the client is not run at all: no frame, no draw, nothing read.</summary>
    public bool Frozen { get; set; }
    public long DatagramsToClient { get; internal set; }
    public long DatagramsToServer { get; internal set; }
    public long BytesToClient { get; internal set; }
    /// <summary>Called with each datagram the server sends this client, before the client sees it (for a transcript).</summary>
    public Action<byte[]>? ServerDatagram { get; set; }

    /// <summary>The server's slot for this client, once it has one.</summary>
    public SvClient? ServerSlot
    {
        get
        {
            foreach (SvClient client in _server.Clients)
                if (client.Connection is SvNetConnection connection && connection.Address.Equals(Address)) return client;
            return null;
        }
    }

    /// <summary>True once the client has reached the last signon stage of the level it is on.</summary>
    public bool InGame => Session.Client.State == DpClientState.Connected && Session.Client.Signon.Stage >= DpProtocol.Signons;

    /// <summary>A datagram put on the wire to the server as if this client had sent it (for hostile-input tests).</summary>
    public void InjectToServer(byte[] datagram) => ToServer.Enqueue(datagram);

    /// <summary>The local player's origin as the client program has it, if it has a player entity yet.</summary>
    public bool TryGetClientPlayerOrigin(out QcVector origin)
    {
        origin = default;
        if (Session.Host is not { } host) return false;
        int edict = host.EdictForServerEntity(Session.State.PlayerEntity);
        if (edict <= 0 || edict >= host.Vm.NumEdicts || host.Vm.IsFree(edict)) return false;
        origin = host.Vm.FieldVector(edict, host.Fields.Origin);
        return true;
    }

    /// <summary>The same player's origin as the server has it.</summary>
    public bool TryGetServerPlayerOrigin(out QcVector origin)
    {
        origin = default;
        if (_server.Host is not { } host || ServerSlot is not { } slot) return false;
        origin = host.Vm.FieldVector(slot.Edict, host.F.Origin);
        return true;
    }

    public void Dispose() => Session.Dispose();
}

/// <summary>
/// Legacy clients and this server in one process, joined by pairs of datagram queues instead of
/// sockets: what DarkPlaces is when it hosts a game for its own player - and, with more than one
/// client, a whole LAN game in one process for the tests. The owner advances one clock and supplies
/// each player's input; nothing here sleeps, reads a wall clock or opens a socket, so a run is as
/// repeatable as the programs are.
///
/// This is the test harness. A game process that wants a server for its own player uses
/// <see cref="SvLocalGame"/>, which owns the server and hands out the same kind of in-process
/// endpoint without knowing what the client is.
/// </summary>
public sealed class SvLoopback : IDisposable
{
    private readonly List<SvLoopbackClient> _clients = new();
    private readonly List<(IPEndPoint To, byte[] Datagram)> _serverOut = new();
    private readonly Dictionary<IPEndPoint, SvLoopbackClient> _byAddress = new();
    private readonly VirtualFileSystem _clientFiles;
    private readonly string? _clientWriteRoot;

    /// <param name="server">A server that has been started.</param>
    /// <param name="dataDirectory">The game data the clients mount (the same directory the server's environment mounted).</param>
    /// <param name="clientWriteRoot">Where the client programs may write files, or null to refuse.</param>
    public SvLoopback(SvServer server, string dataDirectory, string? clientWriteRoot = null, LegacyClientOptions? options = null,
        Action<string>? clientPrint = null, Action<string>? clientWarning = null)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        _clientFiles = new VirtualFileSystem();
        if (!_clientFiles.MountGameDir(dataDirectory))
        {
            _clientFiles.Dispose();
            throw new DirectoryNotFoundException($"nothing could be mounted from \"{dataDirectory}\"");
        }
        _clientWriteRoot = clientWriteRoot;
        AddClient(options, clientPrint, clientWarning);
    }

    public SvServer Server { get; }
    /// <summary>Every client, in the order added. The first is the one the constructor made.</summary>
    public IReadOnlyList<SvLoopbackClient> Clients => _clients;
    /// <summary>The first client's session, presentation and cvars.</summary>
    public LegacyClientSession Client => _clients[0].Session;
    public HeadlessLegacyPresentation Presentation => _clients[0].Presentation;
    public CvarService ClientCvars => _clients[0].Cvars;
    /// <summary>The clock every half runs on, in seconds.</summary>
    public double Now { get; private set; }
    /// <summary>How often each client draws (CSQC_UpdateView), per second of that clock.</summary>
    public double DrawRate { get; set; } = 60;
    public long DatagramsToClient => _clients[0].DatagramsToClient;
    public long DatagramsToServer => _clients[0].DatagramsToServer;
    public long BytesToClient => _clients[0].BytesToClient;
    /// <summary>Called with each datagram the server sends the first client, before the client sees it (for a transcript).</summary>
    public Action<byte[]>? ServerDatagram { get => _clients[0].ServerDatagram; set => _clients[0].ServerDatagram = value; }
    /// <summary>Datagrams the server addressed to nobody this harness knows (a client that was removed).</summary>
    public long DatagramsToNobody { get; private set; }

    /// <summary>The server's slot for the first client, once it has one.</summary>
    public SvClient? ServerSlot => _clients[0].ServerSlot;

    /// <summary>
    /// Another client, with a host address of its own (127.0.0.1, 127.0.0.2, ...), as players on a
    /// LAN have. The server's connect-flood limit is per host and ignores the port
    /// (NetConn_PreventFlood, net_connectfloodblockingtimeout 5): a second connect request from one
    /// host inside five seconds is dropped unanswered and renews the block, so two clients sharing
    /// an address that connect together lock the second one out for as long as it keeps retrying -
    /// which is DarkPlaces' behaviour, and not what this harness is for.
    /// </summary>
    public SvLoopbackClient AddClient(LegacyClientOptions? options = null, Action<string>? print = null, Action<string>? warning = null)
    {
        IPEndPoint address = new(new IPAddress(new byte[] { 127, 0, 0, (byte)(1 + _clients.Count) }), 34000);
        SvLoopbackClient client = new(Server, _clientFiles, address, _clientWriteRoot, options, print, warning);
        _clients.Add(client);
        _byAddress[address] = client;
        return client;
    }

    /// <summary>CL_EstablishConnection at the current time, for the first client.</summary>
    public void Connect() => Client.Connect(Now);

    /// <summary>CL_EstablishConnection at the current time.</summary>
    public void Connect(SvLoopbackClient client) => client.Session.Connect(Now);

    /// <summary>CL_Disconnect: the client says goodbye; the farewell reaches the server in the next step.</summary>
    public void Disconnect(SvLoopbackClient client)
    {
        foreach (byte[] datagram in client.Session.Disconnect(Now))
            if (!client.Muted) client.ToServer.Enqueue(datagram);
        client.ToClient.Clear();
    }

    /// <summary>One step with the given input for the first client (the others use their <see cref="SvLoopbackClient.Input"/>).</summary>
    public void Step(double seconds, in LegacyInput input)
    {
        _clients[0].Input = input;
        Step(seconds);
    }

    /// <summary>
    /// One step of <paramref name="seconds"/>: each client's frame (clock, network in, input and
    /// network out, draw), then the server's (network in, the server frames that are due, network out).
    /// </summary>
    public void Step(double seconds)
    {
        Now += seconds;
        foreach (SvLoopbackClient client in _clients)
        {
            if (client.Frozen) continue;
            LegacyClientSession session = client.Session;
            session.BeginFrame(Now);
            while (client.ToClient.TryDequeue(out byte[]? datagram)) session.Receive(datagram, Now);
            foreach (byte[] datagram in session.Frame(Now, client.Input))
            {
                if (client.Muted) continue;
                // The client reuses nothing it hands out, but the queue outlives its list.
                client.ToServer.Enqueue(datagram);
                client.DatagramsToServer++;
            }
            if (DrawRate > 0 && Now >= client.NextDraw)
            {
                client.NextDraw += 1.0 / DrawRate;
                if (client.NextDraw < Now) client.NextDraw = Now;
                session.Draw(1.0 / DrawRate);
            }
        }

        _serverOut.Clear();
        foreach (SvLoopbackClient client in _clients)
            while (client.ToServer.TryDequeue(out byte[]? datagram)) Server.Receive(datagram, client.Address, Now, _serverOut);
        Server.Frame(Now, _serverOut);
        foreach ((IPEndPoint to, byte[] datagram) in _serverOut)
        {
            if (!_byAddress.TryGetValue(to, out SvLoopbackClient? client) || client.Frozen)
            {
                DatagramsToNobody++;
                continue;
            }
            client.ServerDatagram?.Invoke(datagram);
            client.ToClient.Enqueue(datagram);
            client.DatagramsToClient++;
            client.BytesToClient += datagram.Length;
        }
    }

    /// <summary>The first client's player origin as its program has it, if it has a player entity yet.</summary>
    public bool TryGetClientPlayerOrigin(out QcVector origin) => _clients[0].TryGetClientPlayerOrigin(out origin);

    /// <summary>The same player's origin as the server has it.</summary>
    public bool TryGetServerPlayerOrigin(out QcVector origin) => _clients[0].TryGetServerPlayerOrigin(out origin);

    public void Dispose()
    {
        foreach (SvLoopbackClient client in _clients) client.Dispose();
        _clientFiles.Dispose();
    }
}

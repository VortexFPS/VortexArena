// Port of Base/darkplaces/cl_main.c CL_Frame (the order of one client frame: clock, input, network
// in, network out, draw), cl_parse.c CL_BeginDownloads and CL_ParseServerInfo (when the old client
// program is unloaded and the new one loaded), cl_input.c CL_Input / CL_SendMove (the cl.movecmd
// history the program predicts from) and CL_UpdateMoveVars (tick rate and time scale from the stats).
using System.Numerics;
using VortexArena.Common.Config;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy;

/// <summary>One frame of player input, as cl_input.c gathers it from the keys (cl.cmd before it is stamped).</summary>
public struct LegacyInput
{
    /// <summary>cl_forwardspeed and friends applied: units per second wished for, signed.</summary>
    public float ForwardMove, SideMove, UpMove;
    /// <summary>Button bits: 1 attack, 2 jump, 4 attack2, 8 zoom, 16 crouch, ...</summary>
    public int Buttons;
    /// <summary>A one-shot command number (weapon switch and the like), 0 for none. It is sent once.</summary>
    public byte Impulse;
}

public sealed class LegacyClientOptions
{
    public DpClientConfig Client { get; } = new();
    /// <summary>Download the server's client program even when the game data has a file of the
    /// announced size and checksum. Off, such a file is used and nothing is downloaded.</summary>
    public bool AlwaysDownloadProgram { get; set; }
    /// <summary>vid.mode.width / height, as CSQC_UpdateView is told.</summary>
    public float ViewWidth { get; set; } = 1024;
    public float ViewHeight { get; set; } = 768;
    /// <summary>cl_movement: whether input commands are marked as predicted (Xonotic's configuration sets it).</summary>
    public bool PredictMovement { get; set; } = true;
    /// <summary>Options for each client program loaded. Null for the defaults. Its
    /// <see cref="CsqcHostOptions.UriRequests"/> (the program's HTTP requests), if any, is the session's
    /// from here on: the session delivers the replies each frame and disposes of it.</summary>
    public CsqcHostOptions? Host { get; set; }
    /// <summary>
    /// DarkPlaces' download cache (cl_parse.c CL_BeginDownloads looks for "dlcache/csprogs.dat.SIZE.CRC"
    /// when the game data's own csprogs.dat is not the one the server names): given the program's name,
    /// size and CRC-16, the cached file's bytes or null. The answer is verified against the size and
    /// CRC again before it is run, so a wrong or tampered cache entry is only a wasted read. Not
    /// consulted when <see cref="AlwaysDownloadProgram"/> is set.
    /// </summary>
    public Func<string, int, int, byte[]?>? ProgramCache { get; set; }
    /// <summary>Called once with a client program that was downloaded and verified (name, size, CRC-16,
    /// bytes): where an owner writes it to its download cache.</summary>
    public Action<string, int, int, byte[]>? ProgramDownloaded { get; set; }
    /// <summary>The package downloads a server may start with "curl" commands (libcurl.c), or null: the
    /// commands are then ignored, and a level whose map the client lacks is entered without it or refused
    /// (<see cref="DpSignonConfig.RequireWorld"/>). The session drives it and disposes of it.</summary>
    public VortexArena.Legacy.Downloads.LegacyPackageDownloads? Packages { get; set; }
}

/// <summary>
/// A whole legacy client without a window: the DarkPlaces connection, the client's clock, the state
/// a client program reads, the program itself (loaded and unloaded as levels come and go) and the
/// input history it predicts from - everything between a socket and a presentation.
///
/// The owner supplies time, datagrams and input, and takes datagrams back:
/// <code>
/// session.Connect(now);
/// each frame:
///     session.BeginFrame(now);
///     foreach datagram received: session.Receive(datagram, now);
///     foreach (byte[] d in session.Frame(now, input)) socket.Send(d);
///     session.Draw(frameTime);
/// </code>
/// That is CL_Frame's order: the clock runs on, the network is read (and may correct the clock),
/// the input command is stamped with the corrected time and sent, the program draws.
///
/// Nothing in here touches a socket, a wall clock or a renderer, so the test suite runs it against a
/// scripted server and a console program runs it against a real one.
/// </summary>
public sealed class LegacyClientSession : IDisposable
{
    // qstats.h
    private const int StatMoveVarsTicRate = 240, StatMoveVarsTimeScale = 241;

    private readonly LegacyQcHost _services;
    private readonly LegacyClientOptions _options;
    private readonly Sink _sink;
    private bool _programPending;
    private bool _clockStarted;
    private double _lastFrame;
    private float _lastSentMoveTime;
    private byte _pendingImpulse;
    private bool _disposed;

    public LegacyClientSession(LegacyQcHost services, ConfigInterpreter interpreter, ILegacyPresentation presentation, LegacyClientOptions? options = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(interpreter);
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _options = options ?? new LegacyClientOptions();
        _sink = new Sink(this);

        Console = new CsqcConsole(interpreter, services) { SendToServer = text => Client!.SendStringCommand(text) };
        Handler = new CsqcMessageHandler(State, Console, presentation) { Next = _sink };
        if (!_options.AlwaysDownloadProgram)
            _options.Client.Signon.HaveFile = (name, size, crc) => LocalProgram(name, size, crc) is not null;
        Client = new DpClient(Handler, _options.Client);
        Client.MessageStarting += Handler.BeginMessage;
        Client.MessageFinished += OnMessageFinished;
        Client.Signon.Note += text =>
        {
            Note(text);
            services.Print(text + "\n");
        };
        if (_options.Packages is { } packages)
        {
            Client.Signon.Packages = packages;
            // DarkPlaces loads the world when its curl downloads are done; a level that announces some waits for them.
            Handler.DeferLevelLoad = () => Client.StuffedCommandPending("curl");
            Client.Signon.FileDownloaded += result =>
            {
                string? error = packages.AcceptInBand(result.Name, result.Data, result.Crc);
                Note(error is null
                    ? $"{result.Name} ({result.Data.Length} bytes, CRC {result.Crc}) arrived through the game connection and was added to the search path"
                    : $"{result.Name} arrived through the game connection but was not used: {error}");
                if (error is not null) Client.Signon.FallbackLog.Add($"{result.Name} was not used: {error}");
            };
        }
    }

    /// <summary>The package downloads, or null (<see cref="LegacyClientOptions.Packages"/>).</summary>
    public VortexArena.Legacy.Downloads.LegacyPackageDownloads? Packages => _options.Packages;
    private int _mountsAtLevelStart;

    public DpClient Client { get; }
    public CsqcClientState State { get; } = new();
    public CsqcConsole Console { get; }
    public CsqcMessageHandler Handler { get; }
    public ILegacyPresentation Presentation { get; }
    public DpClientClock Clock { get; } = new();
    /// <summary>The loaded client program, or null: before the level's is loaded, or when the server has none.</summary>
    public CsqcHost? Host { get; private set; }

    /// <summary>Why the server's client program could not be started, or null. The connection goes on
    /// without it, but nothing the program was to decode can be decoded.</summary>
    public string? ProgramError { get; private set; }

    /// <summary>
    /// Why this level cannot be entered although the game data has its map: the presentation could not load
    /// the file (<see cref="ILegacyPresentation.WorldLoadError"/>: an unsupported or damaged map). Set when the
    /// level's program would have been started; the program is then not started and nothing more is sent for
    /// this level. The owner leaves the server and shows the reason, exactly as for a map that is missing
    /// (<see cref="DpSignon.MissingWorld"/>). DarkPlaces would enter an empty world.
    /// </summary>
    public string? WorldError { get; private set; }
    /// <summary>Client programs started on this session (one per level).</summary>
    public int ProgramsStarted { get; private set; }
    /// <summary>Calls of CSQC_UpdateView, and how many of them faulted.</summary>
    public long FramesDrawn { get; private set; }
    public long FramesFaulted { get; private set; }
    /// <summary>svc_entities frames received.</summary>
    public long EntityFrames { get; private set; }
    /// <summary>Server messages that did not parse to their end (a desync or a protocol error).</summary>
    public long MessagesNotDecoded { get; private set; }
    /// <summary>The first such message's outcome, with the program's desync if it was the program's.</summary>
    public string? FirstUndecoded { get; private set; }
    /// <summary>What happened, for a log: level changes, program loads, faults. Never chat or prints.</summary>
    public event Action<string>? Event;
    /// <summary>
    /// Receives the server messages that are neither client state nor the program's to decode, but the
    /// engine's to present: svc_sound, svc_stopsound, svc_spawnstaticsound, svc_particle, svc_effect,
    /// svc_pointparticles, svc_trailparticles, svc_spawnstatic, svc_spawnbaseline, svc_cdtrack and the
    /// entity frames. Null drops them, which is what a session without a renderer wants.
    /// </summary>
    public IDpClientHandler? EngineMessages { get; set; }
    /// <summary>Where the running program came from: "from the game data", "from the download cache" or
    /// "downloaded". Null before one is loaded.</summary>
    public string? ProgramSource { get; private set; }

    private void Note(string text) => Event?.Invoke(text);

    /// <summary>CL_EstablishConnection.</summary>
    public void Connect(double now)
    {
        _lastFrame = now;
        _clockStarted = false;
        Clock.Reset();
        Client.Connect(now);
    }

    // ---- demo playback (cl_demo.c CL_PlayDemo_f, CL_ReadDemoMessage) --------------------------------------

    private DpDemoReader? _demo;
    private Stream? _demoStream;
    private Vector3 _demoAngles0, _demoAngles1;

    /// <summary>True while a recording is being played (cls.demoplayback).</summary>
    public bool DemoPlaying => _demo is not null;
    /// <summary>Recorded messages handed to the parser so far.</summary>
    public int DemoMessages { get; private set; }
    /// <summary>cls.demopaused (the "pausedemo" command): no message is read and the clock stands still, so
    /// every frame drawn is the same instant of the recording.</summary>
    public bool DemoPaused { get; set; }
    /// <summary>Developer aid for comparing a frame with DarkPlaces: the playback pauses itself after the first
    /// message whose server time is at least this (the message a "pausedemo" was written into for DarkPlaces),
    /// with the clock on that message's time. Negative: never.</summary>
    public double DemoPauseAt { get; set; } = -1;

    /// <summary>
    /// CL_PlayDemo_f: play a DarkPlaces recording in place of a connection. The stream is the session's from
    /// here on. <paramref name="timeDemo"/> is cls.timedemo: one message a frame, as fast as frames come.
    /// The owner calls <see cref="ReadDemo"/> once a frame where it would hand over datagrams.
    /// </summary>
    public void PlayDemo(Stream demo, double now, bool timeDemo = false)
    {
        _demoStream = demo ?? throw new ArgumentNullException(nameof(demo));
        _demo = new DpDemoReader(demo);
        DemoMessages = 0;
        _demoAngles0 = _demoAngles1 = default;
        State.IsDemo = true;
        _lastFrame = now;
        _clockStarted = false;
        Clock.Reset();
        Clock.Demo = true;
        Clock.TimeDemo = timeDemo;
        Client.BeginDemo(now);
    }

    /// <summary>
    /// CL_ReadDemoMessage: every message until the client is in the game; after that the messages whose
    /// time has come (one a frame for a timedemo). The recorded view angles are interpolated between the last
    /// two messages, as cl_main.c does "if playing a demo". Returns false once the recording has ended.
    /// </summary>
    public bool ReadDemo()
    {
        if (_demo is not { } demo) return false;
        if (DemoPaused) return true;   // "LadyHavoc: pausedemo"
        while (Client.State == DpClientState.Connected)
        {
            bool signedOn = State.Signon >= DpProtocol.Signons;
            if (signedOn && !Clock.TimeDemo && Clock.Time < Clock.ServerTime) break;
            if (!demo.TryReadMessage(out DpDemoMessage message))
            {
                Note(demo.Error is { } error ? "the demo stopped: " + error : $"the demo ended after {DemoMessages} messages");
                Client.EndDemo();
                _demoStream?.Dispose();
                _demoStream = null;
                _demo = null;
                return false;
            }
            DemoMessages++;
            _demoAngles1 = _demoAngles0;
            _demoAngles0 = message.ViewAngles;
            Client.ReceiveDemoMessage(message.Data);
            if (signedOn && DemoPauseAt >= 0 && Clock.ServerTime >= DemoPauseAt)
            {
                DemoPauseAt = -1;
                DemoPaused = true;
                Clock.HoldAtServerTime();
                break;
            }
            if (signedOn && Clock.TimeDemo) break;
        }
        // CL_LerpPoint, then "interpolate the angles if playing a demo".
        double span = Math.Min(Clock.ServerTime - Clock.ServerPrevTime, 0.1);
        double frac = span <= 0 || Clock.TimeDemo ? 1 : Math.Clamp((Clock.Time - (Clock.ServerTime - span)) / span, 0, 1);
        State.ViewAngles = new QcVector(LerpAngle(_demoAngles1.X, _demoAngles0.X, frac), LerpAngle(_demoAngles1.Y, _demoAngles0.Y, frac), LerpAngle(_demoAngles1.Z, _demoAngles0.Z, frac));
        if (State.Signon >= DpProtocol.Signons) State.Time = Clock.Time;
        return true;

        static float LerpAngle(float from, float to, double frac)
        {
            float d = to - from;
            if (d > 180) d -= 360;
            else if (d < -180) d += 360;
            return (float)(from + frac * d);
        }
    }

    /// <summary>
    /// The start of a client frame: "cl.oldtime = cl.time; cl.time += clframetime". It comes before
    /// the frame's datagrams are handed over, because a server time stamp among them corrects the
    /// clock as it stands after this step.
    /// </summary>
    public void BeginFrame(double now)
    {
        double frameTime = _clockStarted ? Math.Max(0, now - _lastFrame) : 0;
        _clockStarted = true;
        _lastFrame = now;
        if (Client.State != DpClientState.Connected) return;
        Clock.Advance(frameTime, State.Paused || DemoPaused);
        if (State.Signon >= DpProtocol.Signons) State.Time = Clock.Time;
    }

    /// <summary>One datagram from the server.</summary>
    public void Receive(ReadOnlySpan<byte> datagram, double now) => Client.Receive(datagram, now);

    /// <summary>
    /// The frame's send (CL_SendMove): the input becomes the command at the head of the history,
    /// and the connection decides whether a packet goes out. Returns the datagrams to send, valid
    /// until the next call.
    /// </summary>
    public IReadOnlyList<byte[]> Frame(double now, in LegacyInput input)
    {
        if (Client.State != DpClientState.Connected) return Client.Update(now);

        // Curl_Frame: finished package downloads are mounted; when the last one the level waits for has
        // ended, its loading goes on ("cl_begindownloads").
        if (_options.Packages is { } packages && packages.Update())
        {
            if (packages.Failures.Count > 0) Note("package downloads for this level failed: " + string.Join("; ", packages.Failures));
            Client.ContinueDownloads();
            TryStartProgram();
        }

        bool signedOn = State.Signon >= DpProtocol.Signons;
        UpdateMoveVars();

        // CL_SendMove: "we build up cl.cmd and then decide whether to send or not; we store this into
        // cl.movecmd[0] for prediction each frame even if we do not send, to make sure that
        // prediction is instant".
        if (input.Impulse != 0) _pendingImpulse = input.Impulse;
        uint sequence = Client.Channel.OutgoingUnreliableSequence;
        float time = (float)State.Time;
        double commandFrameTime = Math.Clamp(time - _lastSentMoveTime, 0.0, 0.255);
        if (commandFrameTime > 0.25) commandFrameTime = 0.1;   // ridiculous value rejection (matches qw)
        CsqcUserCommand command = new()
        {
            Sequence = sequence, ViewAngles = State.ViewAngles, ForwardMove = input.ForwardMove, SideMove = input.SideMove, UpMove = input.UpMove,
            Buttons = input.Buttons, FrameTime = (float)commandFrameTime, Crouch = (input.Buttons & 16) != 0,
        };
        // always dump the first two moves, because they may contain leftover inputs from the last level
        if (sequence <= 2) command.ForwardMove = command.SideMove = command.UpMove = command.Buttons = 0;
        State.MoveCommands[0] = command;

        if (signedOn)
            Client.QueueMove(new DpUserCmd
            {
                Predicted = _options.PredictMovement, Time = time,
                ViewAngles = new Vector3(State.ViewAngles.X, State.ViewAngles.Y, State.ViewAngles.Z),
                ForwardMove = command.ForwardMove, SideMove = command.SideMove, UpMove = command.UpMove,
                Buttons = command.Buttons, Impulse = sequence <= 2 ? (byte)0 : _pendingImpulse,
            });

        IReadOnlyList<byte[]> outgoing = Client.Update(now);
        if (Client.LastUpdateSentMove)
        {
            // "update the cl.movecmd array which holds the most recent moves, because we now need a
            // new slot for the next input": the head stays as a copy until the next frame rebuilds it.
            State.PushMoveCommand(command);
            _lastSentMoveTime = time;
            _pendingImpulse = 0;   // "clear impulse"
        }
        State.ServerMoveSequence = Client.ServerMoveSequence;
        return outgoing;
    }

    /// <summary>
    /// The rest of the frame: run the console's command buffer and have the program draw
    /// (CL_VM_UpdateView). Does nothing until the client is in the game with a program. Returns
    /// false if the program faulted in this frame.
    /// </summary>
    public bool Draw(double frameTime)
    {
        if (Host is not { Initialized: true } host || State.Signon < DpProtocol.Signons) return true;
        // CL_Frame begins with CL_VM_PreventInformationLeaks.
        host.PreventInformationLeaks();
        Console.Execute();
        // Curl_Frame: the replies to the program's uri_get requests, between its entry points and before it draws.
        host.DeliverUriReplies();
        int faults = host.FaultCount;
        host.UpdateView(_options.ViewWidth, _options.ViewHeight, frameTime);
        FramesDrawn++;
        Console.Execute();
        if (host.FaultCount == faults) return true;
        FramesFaulted++;
        if (FramesFaulted == 1) Note("CSQC_UpdateView faulted: " + host.Faults[^1].Message);
        return false;
    }

    /// <summary>CL_KeepaliveMessage, for an owner to call from inside long loading work: the
    /// datagrams to send at once (usually none). See <see cref="DpClient.KeepAlive"/>.</summary>
    public IReadOnlyList<byte[]> KeepAlive(double now) => Client.KeepAlive(now);

    /// <summary>CL_DisconnectEx: say goodbye, shut the program down. The farewell datagrams are returned.</summary>
    public IReadOnlyList<byte[]> Disconnect(double now)
    {
        Client.Disconnect(now);
        IReadOnlyList<byte[]> outgoing = Client.Update(now);
        UnloadProgram();
        return outgoing;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _demoStream?.Dispose();
        _demoStream = null;
        _demo = null;
        UnloadProgram();
        Console.Detach();
        _options.Packages?.Dispose();
        _options.Host?.UriRequests?.Dispose();
    }

    // CL_UpdateMoveVars: Xonotic publishes its physics settings as stats; the two that matter to the
    // connection itself are how long a server tick is and how fast its clock runs.
    private void UpdateMoveVars()
    {
        int rawTicRate = State.Stats[StatMoveVarsTicRate];
        if (rawTicRate != 0)
        {
            float ticRate = BitConverter.Int32BitsToSingle(rawTicRate), timeScale = BitConverter.Int32BitsToSingle(State.Stats[StatMoveVarsTimeScale]);
            Client.MoveVarsTicRate = Clock.TicRate = float.IsFinite(ticRate) && ticRate > 0 ? ticRate : 0;
            Clock.TimeScale = float.IsFinite(timeScale) && timeScale > 0 ? timeScale : 1;
        }
        else
        {
            // "no guessing, unavailable ticrate triggers better fallbacks"
            Client.MoveVarsTicRate = Clock.TicRate = 0;
            Clock.TimeScale = 1;
        }
    }

    // After every server message: a new level unloads the old program; the level's own is loaded
    // once signon stage 1 has been reached and its file is at hand, which is where CL_BeginDownloads
    // calls CL_VM_Init - before "prespawn" has left, so before the server sends anything the program
    // must decode.
    private void OnMessageFinished(DpParseResult result)
    {
        CsqcDesync? desync = Host?.MessageDesync;
        bool serverInfo = Handler.ServerInfoReceived;
        Handler.EndMessage();
        if (result.Status != DpParseStatus.Complete)
        {
            MessagesNotDecoded++;
            if (FirstUndecoded is null)
            {
                FirstUndecoded = $"message {Client.MessagesParsed - 1}: {result}" + (desync is null ? "" : " [" + desync + "]");
                Note("a server message was not decoded to its end: " + FirstUndecoded);
            }
        }

        if (serverInfo)
        {
            UnloadProgram();
            _programPending = true;
            ProgramError = null;
            WorldError = null;
            Clock.Reset();
            _lastSentMoveTime = 0;
            _mountsAtLevelStart = _options.Packages?.MountedCount ?? 0;
            Note($"level {State.WorldModel} (\"{State.WorldMessage}\"), {State.MaxClients} player slots");
        }
        TryStartProgram();
    }

    // The program is started when the level's loading is finished as far as downloads go ("prespawn" is
    // queued): after the packages, after the program's own download, after the map check.
    private void TryStartProgram()
    {
        if (!_programPending || Client.Signon.Stage < 1 || !Client.Signon.LoadFinished || Client.Download.Active || Client.State != DpClientState.Connected) return;
        if (State.LevelLoadDeferred || (_options.Packages?.MountedCount ?? 0) != _mountsAtLevelStart)
        {
            State.LevelLoadDeferred = false;
            _mountsAtLevelStart = _options.Packages?.MountedCount ?? 0;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Presentation.LevelFilesArrived(State);
            Note($"the level's files were loaded after its downloads ({System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s)");
        }
        if (Presentation.WorldLoadError is { Length: > 0 } worldError)
        {
            // Never a level without its world: no program, no "prespawn" answered by play.
            _programPending = false;
            WorldError = worldError;
            Note($"the level is not entered: {worldError}");
            return;
        }
        StartProgram();
    }

    // The game data's own copy of the program the server names, if it is the same file.
    private byte[]? LocalProgram(string name, int size, int crc)
    {
        if (crc < 0 || name.Length == 0) return null;
        if (_services.FileExists(name) && Matches(_services.ReadFile(name), size, crc) is { } own)
        {
            _localSource = "from the game data";
            return own;
        }
        // "dlcache/csprogs.dat.SIZE.CRC": a program an earlier session downloaded from this or another server.
        if (_options.ProgramCache is { } cache && LegacyQcHost.IsSafePath(name) && Matches(cache(name, size, crc), size, crc) is { } cached)
        {
            _localSource = "from the download cache";
            return cached;
        }
        return null;

        static byte[]? Matches(byte[]? data, int size, int crc) =>
            data is not null && (size < 0 || data.Length == size) && Crc16.Block(data) == crc ? data : null;
    }

    private string _localSource = "from the game data";

    private void StartProgram()
    {
        DpSignon signon = Client.Signon;
        // "csqc_progcrc" negative: the server has no client program, and the engine's own client is all there is.
        if (signon.CsqcProgCrc < 0)
        {
            _programPending = false;
            Note("the server names no client program");
            Presentation.EndLevelLoad(State);
            return;
        }
        byte[]? program = signon.CsprogsData ?? (_options.AlwaysDownloadProgram ? null : LocalProgram(signon.CsqcProgName, signon.CsqcProgSize, signon.CsqcProgCrc));
        if (program is null)
        {
            // A download that failed leaves nothing to wait for; one that has not been asked for yet does.
            if (signon.LastDownload is { } failed && failed.Status != DpDownloadStatus.Completed)
            {
                _programPending = false;
                ProgramError = $"the download of {signon.CsqcProgName} ended with {failed.Status}";
                Note(ProgramError);
            }
            return;
        }
        _programPending = false;
        try
        {
            CsqcHost host = new(program, signon.CsqcProgSize, signon.CsqcProgCrc, _services, Console, Presentation, State, _options.Host);
            ProgramsStarted++;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ok = host.Init();
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            Host = host;
            Handler.Host = host;
            Console.Execute();
            ProgramSource = signon.CsprogsData is null ? _localSource : "downloaded";
            // CL_ParseDownload's "save to disk": a verified download goes to the owner's cache, once.
            if (signon.CsprogsData is not null && signon.CsprogsVerified)
            {
                try { _options.ProgramDownloaded?.Invoke(signon.CsqcProgName, signon.CsqcProgSize, signon.CsqcProgCrc, program); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Note("the downloaded program could not be cached: " + e.Message); }
            }
            Note($"client program {signon.CsqcProgName} loaded ({program.Length} bytes, crc {host.ProgramCrc}, {ProgramSource}); CSQC_Init {(ok ? "completed" : "FAULTED: " + host.FaultMessage)} in {seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s");
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            Presentation.EndLevelLoad(State);
            seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (seconds >= 0.05) Note($"the level's precached models and sounds were made ready in {seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s");
        }
        catch (CsqcLoadException e)
        {
            ProgramError = e.Message;
            Note("the client program was refused: " + e.Message);
        }
    }

    private void UnloadProgram()
    {
        if (Host is not { } host) return;
        Handler.Host = null;
        Host = null;
        host.Shutdown();
    }

    // What the connection reports that the program's message handler does not keep itself.
    private sealed class Sink : IDpClientHandler
    {
        private readonly LegacyClientSession _s;
        public Sink(LegacyClientSession session) => _s = session;

        // CL_NetworkTimeReceived: the handler has stored the stamp; the clock decides what time it is.
        public void OnTime(float time)
        {
            bool signedOn = _s.State.Signon >= DpProtocol.Signons;
            _s.Clock.NetworkTimeReceived(time, signedOn);
            _s.State.Time = _s.Clock.Time;
        }

        public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities)
        {
            _s.EntityFrames++;
            _s.EngineMessages?.OnEntityFrame(frame, entities);
        }

        public void OnDisconnect() => _s.Note("the server ended the connection (svc_disconnect)");

        // The engine's own presentation messages: nothing in the session acts on them.
        public void OnSound(in DpSound sound) => _s.EngineMessages?.OnSound(sound);
        public void OnStopSound(int entity, int channel) => _s.EngineMessages?.OnStopSound(entity, channel);
        public void OnSpawnStaticSound(in DpStaticSound sound) => _s.EngineMessages?.OnSpawnStaticSound(sound);
        public void OnParticle(in DpParticle particle) => _s.EngineMessages?.OnParticle(particle);
        public void OnEffect(in DpEffect effect) => _s.EngineMessages?.OnEffect(effect);
        public void OnPointParticles(in DpPointParticles particles) => _s.EngineMessages?.OnPointParticles(particles);
        public void OnTrailParticles(in DpTrailParticles trail) => _s.EngineMessages?.OnTrailParticles(trail);
        public void OnSpawnStatic(in EntityState state) => _s.EngineMessages?.OnSpawnStatic(state);
        public void OnSpawnBaseline(int entity, in EntityState baseline) => _s.EngineMessages?.OnSpawnBaseline(entity, baseline);
        public void OnCdTrack(int track, int loopTrack) => _s.EngineMessages?.OnCdTrack(track, loopTrack);
        public void OnPrint(string text) => _s._services.Print(text);
        public void OnCenterPrint(string text) => _s._services.Print(text + "\n");
    }
}

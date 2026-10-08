// Port of Base/darkplaces/host.c Host_Frame's split between the server and the client of one process
// (DarkPlaces runs SV_Frame and CL_Frame in turn on one thread, or - with sv_threaded 1 - SV_ThreadFunc
// on a thread of its own, sv_main.c:2570, which is the arrangement here), of lhnet.c's loopback driver
// (LHNETADDRESSTYPE_LOOP: the local player's datagrams go through two in-process queues) and of
// sv_ccmds.c SV_Map_f's "host.hook.ConnectLocal" (the local client connects once the level is up).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using VortexArena.Common.Diagnostics;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;

namespace VortexArena.Legacy.Local;

/// <summary>Where a <see cref="LegacyLocalServer"/> is in its life.</summary>
public enum LegacyLocalServerState
{
    /// <summary>The data is being mounted and the first level started. Nothing can connect yet.</summary>
    Starting,
    /// <summary>A level is running and <see cref="LegacyLocalServer.Transport"/> leads to it.</summary>
    Running,
    /// <summary>It never started, or stopped by a fault: <see cref="LegacyLocalServer.Error"/> says why.</summary>
    Failed,
    /// <summary>It was shut down, or its console said quit.</summary>
    Stopped,
}

/// <summary>
/// DarkPlaces' loopback driver for the local player of a listen server: an
/// <see cref="ILegacyTransport"/> over an <see cref="SvLocalEndpoint"/>'s two queues. No socket exists.
/// </summary>
public sealed class LegacyLoopbackTransport : ILegacyTransport
{
    private readonly SvLocalEndpoint _endpoint;
    private readonly Action? _sent;
    private long _sentCount, _receivedCount;

    /// <param name="sent">Called after each <see cref="Send"/>: a server on another thread uses it to wake up.</param>
    public LegacyLoopbackTransport(SvLocalEndpoint endpoint, Action? sent = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _sent = sent;
    }

    /// <summary>The queues underneath.</summary>
    public SvLocalEndpoint Endpoint => _endpoint;
    public long Sent => _sentCount;
    public long Received => _receivedCount;
    /// <summary>Datagrams lost to a full queue or a closed endpoint (packet loss, as far as the protocol can tell).</summary>
    public long Dropped => _endpoint.Dropped;
    public string Peer => "local";

    public void Send(byte[] datagram)
    {
        _endpoint.Send(datagram);
        _sentCount++;
        _sent?.Invoke();
    }

    public bool TryReceive(out byte[] datagram)
    {
        if (!_endpoint.TryReceive(out datagram)) return false;
        _receivedCount++;
        return true;
    }

    /// <summary>Nothing to close: the server that made the endpoint detaches it.</summary>
    public void Dispose() { }
}

/// <summary>What a <see cref="LegacyLocalServer"/> measured since the last <see cref="LegacyLocalServer.TakeStats"/>.</summary>
public readonly record struct LegacyLocalServerStats(long Frames, long Ticks, double TickMilliseconds, double LongestFrameMilliseconds, double Seconds)
{
    /// <summary>Mean cost of a server frame that ran at least one tick, in milliseconds.</summary>
    public double MeanFrameMilliseconds => Frames > 0 ? TickMilliseconds / Frames : 0;
    /// <summary>Server ticks per second of wall clock.</summary>
    public double TicksPerSecond => Seconds > 0 ? Ticks / Seconds : 0;
}

/// <summary>
/// The server half of a local Xonotic game inside a host process (a "listen server"): an
/// <see cref="SvLocalGame"/> together with the thread it runs on and everything that has to cross
/// between that thread and the owner's.
///
/// <b>Threads.</b> <see cref="SvLocalGame"/> belongs to one thread. With <c>threaded</c> set that is a
/// thread this class starts ("legacy-server"); the owner then never touches the game, only this
/// class: datagrams cross through <see cref="Transport"/> (two thread-safe queues), console text and
/// cvar changes through a queue the server drains before each frame, and everything the server has
/// to say (console output, level changes) through a queue the owner drains in <see cref="Update"/>,
/// which is where the events below are raised - on the owner's thread. Without <c>threaded</c> the
/// same code runs inside <see cref="Update"/>: starting the level, each server frame, a level change -
/// DarkPlaces' default arrangement, where a level load stops the client for as long as it takes.
///
/// <b>Clock.</b> The server runs on its own steady clock (seconds since this object was made); the
/// client's clock is the client's. As on a network, the two only meet in the protocol.
///
/// <b>Shutdown.</b> <see cref="Dispose"/> ends the game on the thread that owns it - every client is
/// told, the program's shutdown hook runs, a UDP socket if any is closed - and waits for that. A
/// server still starting cannot be interrupted; Dispose does not wait for it, and the thread shuts
/// the game down itself as soon as the start returns. The thread is a background thread: it never
/// keeps a process alive.
/// </summary>
public sealed class LegacyLocalServer : IDisposable
{
    private const int MaxQueuedEvents = 16384;
    private static int s_liveThreads;

    /// <summary>Server threads alive in this process right now, over every instance. A diagnostic: after a
    /// local game has been left it has to read 0, or a server was orphaned.</summary>
    public static int LiveThreads => Volatile.Read(ref s_liveThreads);

    private enum EventKind { Print, Note, LevelChanging, LevelChanged, PlayerCvar }

    private readonly SvLocalGameOptions _options;
    private readonly bool _threaded;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<(EventKind Kind, string A, string B)> _events = new();
    private readonly ConcurrentQueue<Action<SvLocalGame>> _actions = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new(false), _finished = new(false);
    private readonly object _statsGate = new();
    private readonly Thread? _thread;
    private SvLocalGame? _game;
    private volatile LegacyLoopbackTransport? _transport;
    private volatile string? _error;
    private volatile string _map, _gameType = "";
    private volatile IPEndPoint? _listenAddress;
    private volatile int _state = (int)LegacyLocalServerState.Starting;
    private volatile bool _stop, _disposed, _ending;
    private int _queuedEvents;
    private long _eventsDropped, _faults, _levels;
    private long _statFrames, _statTicks;
    private double _statMs, _statMax, _statSince, _startSeconds, _lastLevelChangeSeconds;
    private int _activeClients;

    /// <param name="options">What to start. Its Print and Warning are not used: console output arrives through <see cref="Print"/>.</param>
    /// <param name="threaded">Run the server on a thread of its own (see the class comment).</param>
    public LegacyLocalServer(SvLocalGameOptions options, bool threaded)
    {
        ArgumentNullException.ThrowIfNull(options);
        _threaded = threaded;
        _map = options.Map;
        _options = new SvLocalGameOptions
        {
            DataDirectory = options.DataDirectory, WriteRoot = options.WriteRoot, Map = options.Map, GameType = options.GameType, Bots = options.Bots,
            MaxPlayers = options.MaxPlayers, Listen = options.Listen, Dedicated = options.Dedicated, Cvars = options.Cvars, RandomSeed = options.RandomSeed,
            KeepRunningAfterFault = options.KeepRunningAfterFault,
            Print = text => Post(EventKind.Print, text, ""),
            Warning = options.Warning is null ? null : text => Post(EventKind.Note, "VM warning: " + text.TrimEnd(), ""),
        };
        if (threaded)
        {
            _thread = new Thread(ThreadMain) { Name = "legacy-server", IsBackground = true };
            _thread.Start();
        }
    }

    // ---- raised by Update, on the owner's thread -------------------------------------------------------

    /// <summary>Console output of the server and its program. Text arrives in pieces; a line ends at '\n'.</summary>
    public event Action<string>? Print;
    /// <summary>One line for a log: connections, drops, level changes, VM warnings. Never chat.</summary>
    public event Action<string>? Note;
    /// <summary>The level (its map's base name) is about to be shut down for a level change or a restart;
    /// the next one is being loaded and no datagram will come until it is up.</summary>
    public event Action<string>? LevelChanging;
    /// <summary>A level change has completed: (map it was, map it is now). Equal for a restart.</summary>
    public event Action<string, string>? LevelChanged;
    /// <summary>
    /// The server program has set a cvar that is the PLAYER's (name, value): the campaign's progress,
    /// <see cref="LegacyLocalCvars.IsCampaignProgress"/>, and nothing else. In DarkPlaces the menu reads
    /// the same variable because there is one store; here the owner puts it into the player's. Raised
    /// when the program sets it, and once more for each such cvar when the level or the game ends (also
    /// from <see cref="Dispose"/>), so that a value which was created rather than changed is not missed.
    /// </summary>
    public event Action<string, string>? PlayerCvar;

    // ---- readable from the owner's thread --------------------------------------------------------------

    public bool Threaded => _threaded;
    public LegacyLocalServerState State => (LegacyLocalServerState)_state;
    /// <summary>Why the server is <see cref="LegacyLocalServerState.Failed"/> (or why it stopped by itself), or null.</summary>
    public string? Error => _error;
    /// <summary>The local player's way in. Null until the server is <see cref="LegacyLocalServerState.Running"/>.</summary>
    public ILegacyTransport? Transport => _transport;
    /// <summary>The running map's base name.</summary>
    public string Map => _map;
    /// <summary>The game mode the program is playing ("dm", "ctf", ...); empty until the first level is up.</summary>
    public string GameType => _gameType;
    /// <summary>The bound UDP address, or null: no socket exists.</summary>
    public IPEndPoint? ListenAddress => _listenAddress;
    /// <summary>QuakeC faults of every level so far.</summary>
    public long Faults => Interlocked.Read(ref _faults);
    /// <summary>Levels started, the first included.</summary>
    public long LevelsStarted => Interlocked.Read(ref _levels);
    /// <summary>Player slots in use, bots included, as of the last server frame.</summary>
    public int ActiveClients => Volatile.Read(ref _activeClients);
    /// <summary>How long mounting the data and starting the first level took.</summary>
    public double StartSeconds => Volatile.Read(ref _startSeconds);
    /// <summary>How long the last level change took (shutting one level down and starting the next).</summary>
    public double LastLevelChangeSeconds => Volatile.Read(ref _lastLevelChangeSeconds);
    /// <summary>Console lines and notes dropped because the owner did not call <see cref="Update"/> for a long time.</summary>
    public long EventsDropped => Interlocked.Read(ref _eventsDropped);

    /// <summary>Server frames, ticks and their cost since the last call; the counters start again.</summary>
    public LegacyLocalServerStats TakeStats()
    {
        lock (_statsGate)
        {
            double now = _clock.Elapsed.TotalSeconds;
            LegacyLocalServerStats stats = new(_statFrames, _statTicks, _statMs, _statMax, now - _statSince);
            _statFrames = _statTicks = 0;
            _statMs = _statMax = 0;
            _statSince = now;
            return stats;
        }
    }

    /// <summary>Blocks until the server has started or failed to. For a host with nothing else to do meanwhile (a test, a tool).</summary>
    public bool WaitUntilStarted(TimeSpan timeout)
    {
        if (!_threaded)
        {
            Update();
            return State == LegacyLocalServerState.Running;
        }
        _started.Wait(timeout);
        return State == LegacyLocalServerState.Running;
    }

    // ---- the owner's side ------------------------------------------------------------------------------

    /// <summary>
    /// Once per frame of the owner: raise the events the server queued and - when the server is not
    /// threaded - start it (the first call) or run one server frame (every call after).
    /// </summary>
    public void Update()
    {
        if (_disposed) return;
        if (!_threaded)
        {
            if (_game is null && State == LegacyLocalServerState.Starting) StartGame();
            else if (_game is { } game && State == LegacyLocalServerState.Running)
            {
                try { RunFrame(game); }
                catch (Exception e) when (e is not OutOfMemoryException) { Fail("the server stopped with an internal error: " + e.Message); }
            }
        }
        while (_events.TryDequeue(out (EventKind Kind, string A, string B) item))
        {
            Interlocked.Decrement(ref _queuedEvents);
            switch (item.Kind)
            {
                case EventKind.Print: Print?.Invoke(item.A); break;
                case EventKind.Note: Note?.Invoke(item.A); break;
                case EventKind.LevelChanging: LevelChanging?.Invoke(item.A); break;
                case EventKind.LevelChanged: LevelChanged?.Invoke(item.A, item.B); break;
                case EventKind.PlayerCvar: PlayerCvar?.Invoke(item.A, item.B); break;
            }
        }
    }

    /// <summary>
    /// Console text for the server ("kick # 2", "sv_cmd endmatch", "changelevel boil", a cvar
    /// assignment). It runs before the server's next frame; what it prints comes back through <see cref="Print"/>.
    /// </summary>
    public void Command(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text) || text.Length > 8192) return;
        Enqueue(game => game.Command(text));
    }

    /// <summary>
    /// Sets a cvar in the server's store before its next frame - what a shared cvar store does by
    /// itself in DarkPlaces. Unless <paramref name="create"/> is set, a cvar the server does not have is left alone.
    /// </summary>
    public void SetCvar(string name, string value, bool create = false)
    {
        if (_disposed || string.IsNullOrEmpty(name)) return;
        Enqueue(game =>
        {
            if (create || game.Environment.Cvars.Has(name)) game.Environment.SetCvar(name, value);
        });
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the game on the server's thread, before its next frame - the general
    /// form of <see cref="Command"/> and <see cref="SetCvar"/>, for a host that needs the server program itself
    /// (a diagnostic tool, a test calling one of the program's functions). Nothing in it may touch the owner's
    /// objects: it does not run on the owner's thread when the server has its own.
    /// </summary>
    public void Post(Action<SvLocalGame> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!_disposed) Enqueue(work);
    }

    private void Enqueue(Action<SvLocalGame> action)
    {
        if (_actions.Count >= 4096) return;   // an owner flooding a stalled server loses commands, not memory
        _actions.Enqueue(action);
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _ending = true;
        _wake.Set();
        if (_threaded)
        {
            // A start in progress cannot be interrupted and there is nobody to say goodbye to yet: the
            // thread ends the game itself when the start returns. Otherwise wait for the goodbye.
            if (State != LegacyLocalServerState.Starting) _finished.Wait(TimeSpan.FromSeconds(30));
        }
        else EndGame();
        if (State is LegacyLocalServerState.Starting or LegacyLocalServerState.Running) _state = (int)LegacyLocalServerState.Stopped;
        // The game is over and Update will not run again: what the program saved in its last frames (a level
        // won as the player leaves) still goes to the player. Console output and notes are dropped. Not while
        // the server's thread is still alive (a start that could not be interrupted): the queue is its to fill.
        if (!_threaded || _finished.IsSet)
            while (_events.TryDequeue(out (EventKind Kind, string A, string B) item))
                if (item.Kind == EventKind.PlayerCvar) PlayerCvar?.Invoke(item.A, item.B);
    }

    // ---- the server's side -----------------------------------------------------------------------------

    private void Post(EventKind kind, string a, string b)
    {
        if (Volatile.Read(ref _queuedEvents) >= MaxQueuedEvents && kind is EventKind.Print or EventKind.Note)
        {
            Interlocked.Increment(ref _eventsDropped);
            return;
        }
        Interlocked.Increment(ref _queuedEvents);
        _events.Enqueue((kind, a, b));
    }

    private void Fail(string reason)
    {
        _error = reason;
        _state = (int)LegacyLocalServerState.Failed;
        Post(EventKind.Note, "local server: " + reason, "");
    }

    private void ThreadMain()
    {
        Interlocked.Increment(ref s_liveThreads);
        try
        {
            if (StartGame() && _game is { } game)
            {
                while (!_stop)
                {
                    RunFrame(game);
                    if (State != LegacyLocalServerState.Running) break;
                    // Woken by the local player's datagrams and by queued commands; otherwise a
                    // millisecond's sleep, well under a tick (1/60 s in Xonotic's configuration).
                    _wake.WaitOne(1);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail("the server stopped with an internal error: " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            try { EndGame(); }
            catch (Exception e) when (e is not OutOfMemoryException) { Post(EventKind.Note, "local server: shutdown failed: " + e.Message, ""); }
            Interlocked.Decrement(ref s_liveThreads);
            _started.Set();
            _finished.Set();
        }
    }

    private bool StartGame()
    {
        double began = _clock.Elapsed.TotalSeconds;
        SvLocalGame game;
        try { game = SvLocalGame.Start(_options, began); }
        catch (SvLocalGameException e)
        {
            Fail(e.Message);
            _started.Set();
            return false;
        }
        _game = game;
        // The values the server starts with came from the player: only what the program makes of them is news.
        foreach ((string name, string value) in _options.Cvars)
            if (LegacyLocalCvars.IsCampaignProgress(name, value)) _playerCvarsPosted[name] = value;
        game.Event += text => Post(EventKind.Note, text, "");
        // The campaign's progress, as the program sets it (CampaignSaveCvar: registercvar, then cvar_set).
        game.Environment.Cvars.Changed += name =>
        {
            if (name.StartsWith("g_campaign", StringComparison.Ordinal)) PostPlayerCvar(game, name);
        };
        game.Server.LevelEnding += host =>
        {
            PostPlayerCvars(game);
            if (!_ending) Post(EventKind.LevelChanging, host.WorldBaseName, "");
        };
        game.LevelChanged += (from, to) =>
        {
            Interlocked.Increment(ref _levels);
            Post(EventKind.LevelChanged, from, to);
        };
        Interlocked.Increment(ref _levels);
        Snapshot(game);
        _startSeconds = _clock.Elapsed.TotalSeconds - began;
        lock (_statsGate) _statSince = _clock.Elapsed.TotalSeconds;
        if (_stop)
        {
            // Asked to stop while starting: nobody ever connected.
            _state = (int)LegacyLocalServerState.Stopped;
            _started.Set();
            return false;
        }
        _transport = new LegacyLoopbackTransport(game.AttachLocalClient(), () => _wake.Set());
        _state = (int)LegacyLocalServerState.Running;
        string socket = game.ListenAddress is { } listen ? $"also listening on UDP {listen}" : "no socket";
        Post(EventKind.Note, string.Create(CultureInfo.InvariantCulture,
            $"local server: {game.Map} ({game.GameType}) started in {_startSeconds:0.00} s on {(_threaded ? "its own thread" : "the caller's thread")}, {game.Server.Clients.Count} slots, {socket}"), "");
        _started.Set();
        return true;
    }

    private void RunFrame(SvLocalGame game)
    {
        while (_actions.TryDequeue(out Action<SvLocalGame>? action)) action(game);
        long levels = Interlocked.Read(ref _levels);
        long began = Stopwatch.GetTimestamp();
        int ticks;
        using (Prof.Sample("legacy-server")) ticks = game.Frame(_clock.Elapsed.TotalSeconds);
        double ms = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (Interlocked.Read(ref _levels) != levels)
        {
            // The frame carried out a level change: its cost is a load, not a tick.
            _lastLevelChangeSeconds = ms / 1000;
            Snapshot(game);
        }
        else if (ticks > 0)
        {
            lock (_statsGate)
            {
                _statFrames++;
                _statTicks += ticks;
                _statMs += ms;
                if (ms > _statMax) _statMax = ms;
            }
        }
        Interlocked.Exchange(ref _faults, game.Faults);
        Volatile.Write(ref _activeClients, game.Server.Host?.ActiveClients ?? 0);
        if (game.Running) return;
        if (game.QuitRequested)
        {
            _error = "the server console said quit";
            _state = (int)LegacyLocalServerState.Stopped;
        }
        else if (game.Server.Host is { Faulted: true } host)
            Fail("the server program stopped with an error: " + (host.Faults.Count > 0 ? host.Faults[^1].Message : "unknown fault"));
        else Fail("the level could not be started (see the console output)");
    }

    private void Snapshot(SvLocalGame game)
    {
        _map = game.Map;
        _gameType = game.GameType;
        _listenAddress = game.ListenAddress;
    }

    // What the owner has been told of the player's cvars, so that each value is handed over once.
    private readonly Dictionary<string, string> _playerCvarsPosted = new(StringComparer.Ordinal);

    private void PostPlayerCvar(SvLocalGame game, string name)
    {
        if (!game.Environment.Cvars.Has(name)) return;
        string value = game.Environment.Cvars.GetString(name);
        if (!LegacyLocalCvars.IsCampaignProgress(name, value)) return;
        if (_playerCvarsPosted.TryGetValue(name, out string? posted) && posted == value) return;
        if (_playerCvarsPosted.Count >= 256 && !_playerCvarsPosted.ContainsKey(name)) return;
        _playerCvarsPosted[name] = value;
        Interlocked.Increment(ref _queuedEvents);
        _events.Enqueue((EventKind.PlayerCvar, name, value));
    }

    // registercvar with the value it is then set to raises no change: look once more when a level or the game ends.
    private void PostPlayerCvars(SvLocalGame game)
    {
        foreach (string name in game.Environment.Cvars.Names)
            if (name.StartsWith("g_campaign", StringComparison.Ordinal)) PostPlayerCvar(game, name);
    }

    private void EndGame()
    {
        _ending = true;
        if (_game is not { } game) return;
        _game = null;
        try { PostPlayerCvars(game); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        game.Dispose();
        if (State == LegacyLocalServerState.Running) _state = (int)LegacyLocalServerState.Stopped;
    }
}

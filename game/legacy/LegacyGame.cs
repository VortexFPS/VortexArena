// Port of Base/darkplaces/cl_main.c CL_Frame (the order of one client frame: clock, network in, input,
// network out, draw) and CL_EstablishConnection / CL_Disconnect; cl_input.c CL_Input (keys and mouse into
// the move: the arithmetic is LegacyInputMath, the wiring is here) and the IN_*Down / IN_*Up button
// commands with IN_Impulse; keys.c Key_Event's key_game case (CSQC_InputEvent sees a key before its
// bind does); cl_screen.c SCR_UpdateLoadingScreen as far as a status line goes; and host.c's rule that a
// fault in the client program ends the connection.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Godot;
using VortexArena.Common.Config;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Console;
using VortexArena.Game.Loaders;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Local;
using VortexArena.Legacy.Presentation;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Game.Legacy;

/// <summary>
/// A match on a stock Xonotic server: the legacy-mode sibling of <see cref="VortexArena.Game.Net.NetGame"/>
/// (planning/specs/legacy-compat.md). It owns the UDP socket and a <see cref="LegacyClientSession"/> - the
/// DarkPlaces connection plus the server's own client program on the QuakeC VM - and each frame runs
/// DarkPlaces' client frame: the clock, the datagrams that arrived, the input command, the datagrams
/// to send, then CSQC_UpdateView, whose scene and 2D drawing <see cref="GodotLegacyPresentation"/>
/// turns into Godot nodes.
///
/// ISOLATION. The program that runs here was written by whoever runs the server. It defines and sets
/// thousands of cvars and aliases, and the server sends console commands of its own. None of that may
/// reach the player's Vortex configuration, so a session has its OWN cvar store, its OWN command
/// interpreter and its OWN virtual filesystem (Xonotic's data, mounted read-only; writes go to
/// <see cref="LegacyData.WriteRoot"/>). What crosses over from the player's real configuration is
/// read once, here, by <see cref="SeedFromPlayer"/> and by the bind table lookups, and nothing crosses
/// back.
///
/// THE EXCEPTION: a session started from Xonotic's own menu (<see cref="Menu"/>). The menu program and
/// the client program are two halves of one game and talk through one console, so such a session runs
/// on the menu's <see cref="VortexArena.Legacy.Menu.LegacyConsole"/> - Xonotic's cvars, aliases and key
/// bindings as the player has them - instead of building a private one. That console is still not the
/// player's Vortex configuration, and it undoes at the end of the session whatever the session changed
/// (see its class comment); everything this session does is bracketed so it can tell.
/// </summary>
public partial class LegacyGame : Node
{
    public const int DefaultPort = 26000;

    private const int MaxDatagramsPerFrame = 512;
    private const int MaxLoggedPrints = 400;

    // ---- set by the shell before the node enters the tree ----------------------------------------------

    /// <summary>"host" or "host:port".</summary>
    public string Address { get; set; } = "";
    /// <summary>The Xonotic data directory (<see cref="LegacyData.TryResolve"/>).</summary>
    public string DataDirectory { get; set; } = "";
    /// <summary>The player's own cvar store. Read, never written.</summary>
    public CvarService? PlayerCvars { get; set; }
    /// <summary>Called once when the connection cannot go on, with a sentence for the player.</summary>
    public Action<string>? ConnectionFailed { get; set; }
    /// <summary>Called when the session ends by its own doing (the "disconnect" command, the server's goodbye).</summary>
    public Action? Disconnected { get; set; }
    /// <summary>Lines for the developer console: the server's prints and chat.</summary>
    public Action<string>? ConsolePrint { get; set; }
    /// <summary>The loading screen to keep informed until the player is in the game, and how to take it down.</summary>
    public LoadingScreen? LoadingScreen { get; set; }
    public Action? DismissLoadingScreen { get; set; }
    /// <summary>Whether a menu is in front of the game (the shell's pause menu): the game then has no keyboard.</summary>
    public Func<bool>? UiHasFocus { get; set; }
    /// <summary>The "togglemenu" console command, which Xonotic's program and configuration use.</summary>
    public Action<int>? ToggleMenu { get; set; }
    /// <summary>The "messagemode" (false) and "messagemode2" (true, team chat) commands: DarkPlaces' chat input
    /// line is the engine's, so here it is the shell's. What is typed comes back through <see cref="ConsoleCommand"/>.</summary>
    public Action<bool>? OpenChat { get; set; }
    /// <summary>The Xonotic menu this session was started from, or null. With one, the session runs on the
    /// menu's console (cvars, commands, key bindings, game data) instead of a private one, and its keys
    /// arrive through the menu's key dispatch (<see cref="ProgramInputEvent"/>) rather than from the window.</summary>
    public LegacyMenu? Menu { get; set; }
    /// <summary>
    /// Set for a LOCAL game (DarkPlaces' listen server): the session then starts Xonotic's server program
    /// in this process (<see cref="LegacyLocalServer"/>) and connects to it through two in-process queues
    /// instead of a socket. <see cref="Address"/> is then only a name for messages. Null: join <see cref="Address"/>.
    /// </summary>
    public LegacyLocalGameRequest? LocalGame { get; set; }
    /// <summary>
    /// Set to play a DarkPlaces recording (a <c>.dem</c> file on disk) instead of joining anything: DarkPlaces'
    /// <c>playdemo</c>. No server, no socket, no input; the view is the recorded player's. Only the command
    /// line sets it (<c>--legacy-demo</c>), so nothing a server or a program sends can. With the environment
    /// variable VORTEX_LEGACY_TIMEDEMO set it is <c>timedemo</c>: one recorded message a frame.
    /// </summary>
    public string? DemoPath { get; set; }
    /// <summary>A level-changing console line ("map x", "changelevel x", "restart") typed during a local game
    /// that has no Xonotic menu to read it. True if it was acted on.</summary>
    public Func<string, bool>? MapCommand { get; set; }
    /// <summary>Puts the loading screen back up for a level change (the server went to its next map) and
    /// returns it. Null: a level change loads behind the frozen picture of the old level.</summary>
    public Func<string, LoadingScreen?>? ShowLoadingScreen { get; set; }

    // ---- the session -----------------------------------------------------------------------------------

    private VirtualFileSystem? _vfs;
    private CvarService? _cvars;
    private ConfigInterpreter? _interpreter;
    private LegacyClientOptions? _options;
    private VortexArena.Legacy.Downloads.LegacyUriRequests? _uriRequests;
    private LegacyClientSession? _session;
    private ILegacyTransport? _transport;
    // The server of a local game; null on a remote one. Owned here: it starts with the session and ends with it.
    private LegacyLocalServer? _server;
    private double _serverAskedAt;
    private bool _levelChangePending;
    // SV_Map_f's "connect local": the server of this game started a new game and dropped everyone (the campaign's
    // next level). The session stays and connects again when the level is up.
    private readonly LegacyLocalReconnect _reconnect = new();
    private int _levelsEntered;
    private readonly StringBuilder _serverPrintLine = new();
    private int _serverPrintsLogged;
    private GodotLegacyPresentation? _presentation;
    private Node3D? _sceneRoot;
    private LegacyDrawLayer? _drawLayer;
    // The menu's console when the session runs on it (Menu is set); null for a private session.
    private VortexArena.Legacy.Menu.LegacyConsole? _shared;
    private LegacyQcHost? _services;
    // The engine commands of this session ("+attack", "impulse", "+showscores", ...). Through a relay: on the
    // Xonotic menu's console the interpreter outlives the session and must not hold it (LegacySessionCommands).
    private readonly LegacySessionCommands _commands = new();
    private Action<string, IReadOnlyList<string>>? _unknownCommandBefore;
    private bool _shutDown, _failed, _inGame, _loadingDismissed;
    private double _startedAt, _inGameAt = -1, _nextStatus, _autoJoinAfter;
    private bool _autoJoined;
    private int _lastStage = -1, _printsLogged;
    private DpClientState _lastState = DpClientState.Disconnected;
    private readonly StringBuilder _printLine = new();
    private readonly List<(double Time, string Text)> _chatLines = new();
    private long _keepAlives;

    // Input.
    private LegacyHeldButtons _scriptHeld;
    private byte _pendingImpulse;
    private Vector2 _mouseDelta, _mousePosition, _reportedMousePosition = new(-1, -1);
    private bool _showScores;
    private double _enteredAt;
    private const double MaxSettleSeconds = 3;
    private Vector2 _viewSize;
    private readonly Dictionary<int, string> _bindSnapshot = new();
    private double _bindSnapshotAt = -1;

    /// <summary>True once the first entity frame has arrived (signon stage 4).</summary>
    public bool InGame => _inGame;
    /// <summary>True once the session has failed or been shut down.</summary>
    public bool Ended => _shutDown || _failed;
    /// <summary>The session, for the shell's console wiring and for tests of the node.</summary>
    public LegacyClientSession? Session => _session;
    public GodotLegacyPresentation? Presentation => _presentation;
    /// <summary>True for a local game: this session owns the server it is connected to.</summary>
    public bool IsLocal => LocalGame is not null;
    /// <summary>The local game's server, for a status line. Null on a remote session.</summary>
    public LegacyLocalServer? LocalServer => _server;
    /// <summary>The session's mounted game data (the menu's when it runs on the menu's console).</summary>
    public VirtualFileSystem? Files => _vfs;
    /// <summary>Levels entered in this session: 1 in the first, one more after each level change.</summary>
    public int LevelsEntered => _levelsEntered;

    private static double Now => Time.GetTicksUsec() / 1_000_000.0;
    private static bool Headless => DisplayServer.GetName() == "headless";

    // To the process's output, and to the session's own log file (LegacyLog): a game started the ordinary
    // way has no output anyone can read afterwards.
    private static void Log(string line)
    {
        GD.Print("[legacy] " + line);
        LegacyLog.Write(line);
    }

    // =====================================================================================================
    //  Start
    // =====================================================================================================

    public override void _Ready()
    {
        _startedAt = Now;
        try
        {
            Start();
        }
        catch (Exception e) when (e is SocketException or System.IO.IOException or InvalidOperationException or ArgumentException)
        {
            Fail($"The connection could not be started: {e.Message}");
        }
    }

    private void Start()
    {
        int engineCvars;
        if (Menu is { Console: { } shared, Files: { } sharedFiles })
        {
            // --- started from the Xonotic menu: its console and its game data are this session's too ---
            _shared = shared;
            _vfs = sharedFiles;
            _cvars = shared.Cvars;
            _interpreter = shared.Interpreter;
            engineCvars = shared.EngineCvarCount;
            shared.BeginSession();
        }
        else
        {
            // --- the session's own filesystem: Xonotic's data and nothing else ---
            _vfs = new VirtualFileSystem();
            if (!System.IO.Directory.Exists(DataDirectory) || !_vfs.MountGameDir(DataDirectory))
            {
                Fail($"Nothing could be mounted from the Xonotic data folder \"{DataDirectory}\". " + LegacyData.SetupHint);
                return;
            }

            // --- the session's own console: DarkPlaces' engine cvars, then Xonotic's defaults ---
            _cvars = new CvarService();
            VirtualFileSystem files = _vfs;
            _interpreter = new ConfigInterpreter(_cvars, path => LegacyQcHost.IsSafePath(path) && files.Exists(path) ? files.ReadText(path) : null);
            // Xonotic's configuration uses DarkPlaces' "${$1}" (makesaved, the menu's forced-saved list, #ifdef).
            _interpreter.NestedReferences = true;
            engineCvars = CsqcEngineCvars.Register(_cvars);
            _cvars.Register("pr_checkextension", "1");
            _cvars.Register("utf8_enable", "1");
            _cvars.Register("developer", "0");
        }

        // --- the scene: its nodes have to exist before the configuration runs, because loadfont is a command ---
        _sceneRoot = new Node3D { Name = "LegacyScene" };
        AddChild(_sceneRoot);
        CanvasLayer canvas = new() { Name = "LegacyHud", Layer = 5 };
        AddChild(canvas);
        _drawLayer = new LegacyDrawLayer { Name = "LegacyDraw" };
        canvas.AddChild(_drawLayer);
        // The session's OWN loader, also when it runs under the Xonotic menu (which shares its game data and
        // its console with the session, but not this): a loader keeps every texture, material, mesh and sound
        // it has made for as long as it lives, so a level loaded through the menu's stayed in memory after the
        // game was left - about 0.9 GB of a level nobody was in. This one dies with the session.
        AssetLoader assets = new(_vfs);
        LegacyData.MountTextureCache(_vfs, assets.Assets);
        _presentation = new GodotLegacyPresentation(_sceneRoot, _drawLayer, _vfs, assets, _cvars, Log);
        // No slow texture compression where a texture is loaded: uploaded uncompressed, compressed into the
        // session's cache in the background (LegacyTextureBank). VORTEX_LEGACY_INLINECOMPRESS=1 is the old way.
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_INLINECOMPRESS")))
        {
            _textureBank = new LegacyTextureBank(assets.Assets, Log) { Enabled = string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_NOBANK")) };
            assets.Assets.DeferCompression = _textureBank.Defer;
        }
        // The native menu's background warm of the native game's own assets (which compresses textures on
        // most of the machine's cores) rests while a Xonotic server is being played.
        RestMenuWarmer();

        RegisterEngineCommands(_interpreter);
        if (_shared is null)
        {
            bool defaults = _interpreter.ExecuteFile("default.cfg");
            // The fonts are not part of default.cfg: font-xolonium.cfg "must be loaded AFTER config.cfg", and in
            // DarkPlaces it is the MENU program that executes it (m_init: "exec $menu_font_cfg"). A client
            // started straight into a server runs no menu program, so its font slots stayed empty - every
            // HUD string was drawn in the interface font, bold never bold, no size snapped. Run it here.
            if (defaults)
            {
                string fonts = _cvars.Has("menu_font_cfg") ? _cvars.GetString("menu_font_cfg") : "";
                if (fonts.Length == 0 || fonts.Length > 64 || !VortexArena.Legacy.Csqc.LegacyQcHost.IsSafePath(fonts)) fonts = "font-xolonium.cfg";
                _interpreter.ExecuteFile(fonts);
            }
            if (!defaults)
            {
                Fail($"The Xonotic data folder \"{DataDirectory}\" has no default.cfg, so it is not Xonotic's game data. " + LegacyData.SetupHint);
                return;
            }
            SeedFromPlayer();
            Log($"data {DataDirectory}: {_vfs.MountedPaths.Count} packages mounted, {engineCvars} engine cvars, default.cfg executed ({_interpreter.FilesExecuted} files, {_interpreter.AliasesDefined} aliases)");
        }
        else
        {
            // The configuration ran when the menu started (its loadfont commands included): the HUD's canvas
            // is given the same font slots, and the player's name and settings are the ones set in that menu.
            Menu!.AttachSession(this, _presentation.Canvas);
            Log($"data {DataDirectory}: on the Xonotic menu's console ({engineCvars} engine cvars, {_cvars.Names.Count} cvars in all)");
        }

        // --- the client ---
        LegacyQcHost services = new(_cvars, _vfs)
        {
            WriteRoot = LegacyData.WriteRoot,
            PrintSink = OnPrint,
            WarningSink = text => { if (_cvars.GetFloat("developer") != 0) Log("VM warning: " + Printable(text.TrimEnd(), 300)); },
        };
        bool forceDownload = PlayerCvars is { } player && player.GetFloat(LegacyData.ForceDownloadCvar) != 0;
        string dataDirectory = DataDirectory;
        // HTTP for the client program (uri_get), for a server somebody else runs and for a local game; a
        // recording has no use for it. Every limit comes from the player's own settings, and the session owns it.
        _uriRequests = string.IsNullOrEmpty(DemoPath)
            ? new VortexArena.Legacy.Downloads.LegacyUriRequests(LegacyData.UriLimits(PlayerCvars)) { Print = text => Log(Printable(text.TrimEnd(), 300)) }
            : null;
        _options = new LegacyClientOptions
        {
            AlwaysDownloadProgram = forceDownload,
            Host = new CsqcHostOptions { KeyBinding = KeyBinding, FindKeysForCommand = FindKeysForCommand, CenterPrint = text => ConsolePrint?.Invoke(text), UriRequests = _uriRequests },
            ProgramCache = (name, size, crc) => LegacyData.ReadCachedProgram(dataDirectory, name, size, crc),
            ProgramDownloaded = LegacyData.WriteCachedProgram,
        };
        DpSignonConfig signon = _options.Client.Signon;
        _assetLoader = assets;
        if (LocalGame is null && string.IsNullOrEmpty(DemoPath))
        {
            // A server somebody else runs: the packages it names are fetched (libcurl.c), and its level is not
            // entered without its map. Every limit comes from the player's own settings.
            _options.Packages = new VortexArena.Legacy.Downloads.LegacyPackageDownloads(LegacyData.DownloadCache, LegacyData.DownloadLimits(PlayerCvars))
            {
                FileExists = services.FileExists,
                MountPack = MountDownloadedPackage,
                Print = OnPrint,
            };
            signon.RequireWorld = true;
            signon.FileExists = services.FileExists;
            signon.InBandFallback = PlayerCvars is not { } settings || !settings.Has(LegacyData.InBandCvar) || settings.GetFloat(LegacyData.InBandCvar) != 0;
        }
        signon.Name = _cvars.GetString("_cl_name") is { Length: > 0 } name ? name : "player";
        int color = (int)_cvars.GetFloat("_cl_color");
        signon.TopColor = (color >> 4) & 15;
        signon.BottomColor = color & 15;
        // DarkPlaces' "rate" default of 20000 bytes a second is for modems; Xonotic's configuration asks for more.
        signon.Rate = _cvars.Has("_cl_rate") && _cvars.GetFloat("_cl_rate") > 0 ? (int)_cvars.GetFloat("_cl_rate") : 262144;
        signon.RateBurstSize = 1024;
        signon.PlayerModel = _cvars.GetString("_cl_playermodel");
        signon.PlayerSkin = _cvars.GetString("_cl_playerskin");
        if (_cvars.Has("cl_netfps") && _cvars.GetFloat("cl_netfps") > 0) _options.Client.NetFps = _cvars.GetFloat("cl_netfps");
        _options.PredictMovement = !_cvars.Has("cl_movement") || _cvars.GetFloat("cl_movement") != 0;
        // Developer aid (VORTEX_LEGACY_NETFPS): another packet rate, for measuring what a download's speed depends on.
        if (double.TryParse(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_NETFPS"), NumberStyles.Float, CultureInfo.InvariantCulture, out double netFps) && netFps > 0)
            _options.Client.NetFps = netFps;

        _shared?.EnterSession();   // from here to the end of Start is the session's own work
        _services = services;
        _session = new LegacyClientSession(services, _interpreter, _presentation, _options) { EngineMessages = _presentation };
        // Every block of an in-band download is acknowledged, also after a long frame (see DpDownload.MaxPendingAcks).
        _session.Client.Download.MaxPendingAcks = 64;
        if (_cvars.Has("cl_nettimesyncboundmode")) _session.Clock.BoundMode = (int)_cvars.GetFloat("cl_nettimesyncboundmode");
        _session.Event += text => Log("event: " + Printable(text, 600));
        int commandsLogged = 0;
        _session.Client.CommandSent += command =>
        {
            // The signon's own commands, for the log file; never a player's chat, and LegacyLog drops anything that names rcon or a password.
            if (commandsLogged++ >= 40) return;
            if (Headless && commandsLogged <= 12) Log("cmd> " + Printable(command));
            else LegacyLog.Write("cmd> " + Printable(command));
        };
        // Cmd_ForwardToServer: a command the client does not know is the server's to answer. Set after the
        // defaults ran, so a typo in a configuration file is not sent anywhere. (On the menu's console the
        // menu already forwards unknown commands to its session; its handler stays.)
        if (_shared is null) _interpreter.UnknownCommandHandler = (_, argv) => _session?.Client.SendStringCommand(JoinArguments(argv));
        // CL_KeepaliveMessage: starting the client program parses a few hundred model files in one call.
        _presentation.ModelData.Working = () =>
        {
            if (_session is null || _transport is null) return;
            foreach (byte[] datagram in _session.KeepAlive(Now))
            {
                _transport.Send(datagram);
                _keepAlives++;
            }
        };

        _autoJoinAfter = PlayerCvars is { } p ? p.GetFloat(LegacyData.AutoJoinCvar) : 0;
        if (DemoPath is { Length: > 0 } demoPath)
        {
            // --- a recording: CL_PlayDemo_f. The messages are read in Frame, where datagrams would be ---
            System.IO.FileStream demo = new(demoPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 1 << 16);
            bool timeDemo = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_TIMEDEMO"));
            _transport = new NoTransport(demoPath);
            _autoJoinAfter = 0;
            Log($"playing the demo {demoPath} ({demo.Length} bytes{(timeDemo ? ", as a timedemo: one message a frame" : "")})");
            _session.PlayDemo(demo, Now, timeDemo);
            LoadingScreen?.UpdateProgress(0.05f, "Reading the demo...");
            _presentation.BeginPreload(null);
            _shared?.LeaveSession();
            return;
        }
        if (LocalGame is { } local)
        {
            // --- a local game: the server first, the connection when it is up (PumpServer) ---
            StartLocalServer(local, signon.Name);
            _shared?.LeaveSession();
            return;
        }

        // --- the socket ---
        if (!TryParseAddress(Address, out string host, out int port))
        {
            Fail($"\"{Address}\" is not a server address (expected host or host:port).");
            return;
        }
        IPAddress? ip;
        if (!IPAddress.TryParse(host, out ip))
        {
            try { ip = Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork) ?? Dns.GetHostAddresses(host)[0]; }
            catch (Exception e) when (e is SocketException or ArgumentException or IndexOutOfRangeException)
            {
                Fail($"The server name \"{host}\" could not be resolved.");
                return;
            }
        }
        _transport = new DpUdpTransport(new IPEndPoint(ip, port));
        if (_uriRequests is { } uri)
        {
            // As for package downloads: "http:///x" means the game server, and a request may go to this
            // machine or a private network only if the game server is at one.
            uri.ServerHost = ip.ToString();
            uri.ServerPort = port;
            uri.ServerIsPrivate = VortexArena.Legacy.Downloads.LegacyPackageDownloads.IsPrivateServer(ip);
            VortexArena.Legacy.Downloads.LegacyUriLimits u = uri.Limits;
            Log(string.Create(CultureInfo.InvariantCulture, $"client program HTTP (uri_get): {(u.Enabled ? "on" : "OFF (legacy_uri_get_enabled 0)")}, replies of at most {u.MaxResponseBytes} bytes, posts of at most {u.MaxPostBytes >> 10} KiB, ") +
                string.Create(CultureInfo.InvariantCulture, $"{u.MaxConcurrent} at once, {u.MaxPending} unanswered, {u.Burst} in a burst then one every {u.RefillSeconds:0.#} s, {u.MaxRedirects} redirects, {u.TotalTimeoutSeconds:0} s a request, ") +
                $"private addresses {(uri.ServerIsPrivate ? "allowed (the server is at one)" : "refused")}");
        }
        if (_options.Packages is { } downloads)
        {
            // "http:///x.pk3" means "on the game server"; a download may be on a private network only if the server is.
            downloads.ServerHost = ip.ToString();
            downloads.ServerPort = port;
            downloads.ServerIsPrivate = VortexArena.Legacy.Downloads.LegacyPackageDownloads.IsPrivateServer(ip);
            VortexArena.Legacy.Downloads.LegacyDownloadLimits limits = downloads.Limits;
            Log(string.Create(CultureInfo.InvariantCulture, $"package downloads: {(limits.Enabled ? "on" : "OFF (legacy_curl_enabled 0)")}, at most {limits.MaxFileBytes >> 20} MiB a package and {limits.MaxConnectionBytes >> 20} MiB a connection, ") +
                string.Create(CultureInfo.InvariantCulture, $"{limits.MaxConcurrent} at once, {limits.MaxRedirects} redirects, given up after {limits.StallTimeoutSeconds:0} s without data, speed limit {(limits.MaxKiBPerSecond > 0 ? limits.MaxKiBPerSecond.ToString("0", CultureInfo.InvariantCulture) + " KiB/s" : "none")}, ") +
                $"private addresses {(downloads.ServerIsPrivate ? "allowed (the server is at one)" : "refused")}; cache {LegacyData.DownloadCache}; log {LegacyLog.Path ?? "(opening)"}");
        }
        Log($"connecting to {ip}:{port} as \"{Printable(signon.Name)}\" (rate {signon.Rate}, cl_netfps {_options.Client.NetFps.ToString(CultureInfo.InvariantCulture)}, " +
            $"client program {(forceDownload ? "always downloaded" : "from the game data or the download cache if it matches")}); writes go to {LegacyData.UserRoot}");
        _session.Connect(Now);
        LoadingScreen?.UpdateProgress(0.05f, "Connecting...");
        // While the server answers: the files of the last level's precache lists, on the worker threads.
        _presentation.BeginPreload(null);
        _shared?.LeaveSession();
    }

    private AssetLoader? _assetLoader;
    private LegacyTextureBank? _textureBank;
    private Node? _menuWarmer;
    private double _menuWarmerLookedAt;

    // The warmer may not exist yet when a session starts (a join from the command line): looked for again now and then.
    private void RestMenuWarmer()
    {
        if (_menuWarmer is not null || _shutDown) return;
        _menuWarmerLookedAt = Now;
        if (GetParent()?.GetNodeOrNull("MenuAssetWarmer") is { } warmer && warmer.IsProcessing())
        {
            warmer.SetProcess(false);
            _menuWarmer = warmer;
            Log("the native menu's background asset warm rests while this session runs");
        }
    }
    private readonly StringBuilder _mountedPackages = new();

    // FS_AddPack for a package this session downloaded (or found in its download cache): mounted below the
    // loose directories and above the other packages, as DarkPlaces does, and only on this session's own
    // file system, which ends with the session ("fs_unload_dlcache").
    private string? MountDownloadedPackage(string path)
    {
        if (_vfs is not { } vfs || _shutDown) return "the session is over";
        try
        {
            if (!vfs.MountBelowDirectories(path)) return "the file is not there any more";
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or VortexArena.Formats.AssetParseException)
        {
            return "it could not be mounted (" + e.Message + ")";
        }
        _presentation?.GameDataChanged();
        long length = 0;
        try { if (System.IO.File.Exists(path)) length = new System.IO.FileInfo(path).Length; }
        catch (System.IO.IOException) { }
        Log($"package mounted for this session: {path} ({length} bytes)");
        // Textures compressed from here on are kept apart from Xonotic's own (see UseDownloadTextureCache).
        _mountedPackages.Append(System.IO.Path.GetFileName(path).ToLowerInvariant()).Append(':').Append(length).Append(';');
        if (_assetLoader is { } loader)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_mountedPackages.ToString()));
            LegacyData.UseDownloadTextureCache(vfs, loader.Assets, Convert.ToHexString(hash, 0, 8).ToLowerInvariant());
        }
        return null;
    }

    // cl_screen.c SCR_DrawQWDownload and SCR_DrawCurlDownload: what is being downloaded, how far it is and how
    // fast, on the loading screen and once a second in the log.
    private string _downloadName = "";
    private double _downloadBegan, _downloadRateAt, _downloadLogAt;
    private int _downloadRateBytes, _downloadRate, _downloadSeen;
    private bool _downloadShown;
    private double _downloadLast;
    private long _downloadSentAt, _downloadReceivedAt;

    private void UpdateDownloadDisplay(LegacyClientSession session, double now)
    {
        DpDownload download = session.Client.Download;
        StringBuilder? text = null;
        float fraction = 0.2f;
        if (download.Active)
        {
            if (_downloadName != download.Name)
            {
                _downloadName = download.Name;
                _downloadBegan = _downloadRateAt = now;
                _downloadRate = _downloadRateBytes = _downloadSeen = 0;
                _downloadSentAt = _transport?.Sent ?? 0;
                _downloadReceivedAt = _transport?.Received ?? 0;
            }
            _downloadRateBytes += Math.Max(0, download.ReceivedSize - _downloadSeen);
            _downloadSeen = download.ReceivedSize;
            if (now >= _downloadRateAt + 1)
            {
                // cls.qw_downloadspeedrate: the bytes of the last whole second.
                _downloadRate = (int)(_downloadRateBytes / (now - _downloadRateAt));
                _downloadRateAt = now;
                _downloadRateBytes = 0;
            }
            int percent = download.ExpectedSize > 0 ? Math.Clamp((int)Math.Floor(download.ReceivedSize * 100.0 / download.ExpectedSize), 0, 100) : 0;
            text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
                $"Downloading {Printable(download.Name, 80)} {percent,3}% ({download.ReceivedSize}/{download.ExpectedSize}) at {_downloadRate} bytes/s"));
            fraction = 0.2f + 0.1f * percent / 100f;
            _downloadLast = now;
        }
        else if (_downloadName.Length > 0)
        {
            // Up to the last frame it was still running: what follows in this frame (the program's start) is not download.
            double seconds = Math.Max(0.001, _downloadLast - _downloadBegan);
            Log(string.Create(CultureInfo.InvariantCulture,
                $"download of {Printable(_downloadName, 80)} through the game connection ended after {seconds:0.00} s: {_downloadSeen} bytes, {_downloadSeen / seconds:0} bytes/s on average; ") +
                $"{download.BlocksReceived} blocks received, {download.BlocksRepeated} of them repeats, {download.AcksDropped} acknowledgements dropped; datagrams sent {(_transport?.Sent ?? 0) - _downloadSentAt} ({((_transport?.Sent ?? 0) - _downloadSentAt) / seconds:0} a second), received {(_transport?.Received ?? 0) - _downloadReceivedAt}");
            _downloadName = "";
        }
        if (session.Packages is { Running: true } packages)
        {
            foreach (VortexArena.Legacy.Downloads.LegacyDownloadInfo info in packages.Snapshot())
            {
                if (text is null) text = new StringBuilder();
                else text.Append('\n');
                text.Append(Printable(info.Text, 160));
                if (info.Total > 0) text.Append(CultureInfo.InvariantCulture, $"  ({info.Received}/{info.Total})");
                if (info.Fraction > 0) fraction = 0.1f + 0.1f * (float)info.Fraction;
            }
            if (packages.AdditionalInfo is { } more) text?.Append('\n').Append(more);
        }
        if (text is null)
        {
            if (_downloadShown)
            {
                _downloadShown = false;
                if (!_inGame) LoadingScreen?.UpdateProgress(0.35f, "Loading the level...");
            }
            return;
        }
        _downloadShown = true;
        string shown = text.ToString();
        if (!_inGame) LoadingScreen?.UpdateProgress(fraction, shown);
        if (now >= _downloadLogAt)
        {
            _downloadLogAt = now + 1;
            Log("download: " + shown.Replace("\n", " | "));
            if (!string.IsNullOrEmpty(s_shotDirectory) && !Headless && _downloadShots < 6) SaveShot($"download-{_downloadShots++:00}");
        }
    }
    private int _downloadShots;

    // What a demo is "connected" through: nothing arrives and what is sent is dropped.
    private sealed class NoTransport : ILegacyTransport
    {
        public NoTransport(string peer) => Peer = "demo " + peer;
        public void Send(byte[] datagram) { }
        public bool TryReceive(out byte[] datagram) { datagram = Array.Empty<byte>(); return false; }
        public long Sent => 0;
        public long Received => 0;
        public string Peer { get; }
        public void Dispose() { }
    }

    // =====================================================================================================
    //  A local game: the server program in this process (DarkPlaces' listen server)
    // =====================================================================================================

    // Developer aid, as the other VORTEX_LEGACY_* variables: "name=value;name=value" cvars for the server of a
    // local game started without the Xonotic menu (a short timelimit for a review run). An environment variable,
    // so nothing a server or a program sends can set it.
    private static readonly string? s_serverCvars = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_SERVER_CVARS");

    // SV_Map_f on a client: start the server; host.hook.ConnectLocal follows once the level is running.
    private void StartLocalServer(LegacyLocalGameRequest request, string playerName)
    {
        if (!string.IsNullOrEmpty(s_serverCvars))
        {
            List<KeyValuePair<string, string>> cvars = new(request.Cvars);
            foreach (string pair in s_serverCvars.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = pair.IndexOf('=');
                if (equals > 0) cvars.Add(new KeyValuePair<string, string>(pair[..equals].Trim(), pair[(equals + 1)..].Trim()));
            }
            request = new LegacyLocalGameRequest
            {
                Map = request.Map, GameType = request.GameType, Bots = request.Bots, MaxPlayers = request.MaxPlayers, Cvars = cvars, Listen = request.Listen, Threaded = request.Threaded,
            };
            LocalGame = request;
        }
        VortexArena.Legacy.Server.SvLocalGameOptions options = request.ToOptions(DataDirectory, LegacyData.WriteRoot);
        _serverAskedAt = Now;
        _server = new LegacyLocalServer(options, request.Threaded);
        _server.Print += OnServerPrint;
        _server.Note += text => Log("server: " + Printable(text, 400));
        _server.GameRestarting += from =>
        {
            _reconnect.GameRestarting();
            Log($"server: a new game is starting after {Printable(from)} (\"map\"): every client is dropped, and this one connects again when the level is up");
        };
        _server.LevelChanging += OnLevelChanging;
        _server.PlayerCvar += OnServerPlayerCvar;
        _server.LevelChanged += (from, to) =>
        {
            _reconnect.LevelChanged();
            Log(string.Create(CultureInfo.InvariantCulture, $"server: level change {from} -> {to} took {_server?.LastLevelChangeSeconds:0.00} s"));
        };
        // One store in DarkPlaces; two here. What the PLAYER changes while the game runs is sent on (see
        // LegacyLocalGameRequest for the whole rule); what the session itself sets is not.
        if (_shared is not null) _shared.Cvars.Changed += OnSharedCvarChanged;
        if (PlayerCvars is { } nativeCvars) nativeCvars.Changed += OnNativeCvarTyped;
        Log($"local game: starting the server for \"{Printable(request.Map)}\" ({request.GameType ?? "mode from the cvars"}, bots {options.Bots}, {options.MaxPlayers} slots, " +
            $"{options.Cvars.Count} cvars from the player's configuration, {(request.Threaded ? "on its own thread" : "on the main thread")}, " +
            $"{(options.Listen is { } listen ? "also listening on UDP " + listen : "no socket")}) as \"{Printable(playerName)}\"; writes go to {LegacyData.UserRoot}");
        if (options.Cvars.Count > 0)
        {
            StringBuilder names = new();
            foreach ((string name, string value) in options.Cvars)
                if (names.Length < 1500) names.Append(name).Append('=').Append(Printable(value, 24)).Append(' ');
            Log("local game: cvars for the server: " + names.ToString().TrimEnd());
        }
        LoadingScreen?.UpdateProgress(0.05f, "Starting the server...");
        // The server is loading the level on its own thread. This thread would only wait for it: it loads its
        // own copy of the map now instead, and the worker threads start on the files of the last level's lists.
        _presentation?.BeginPreload(LegacyLocalCommands.IsMapName(request.Map) ? "maps/" + request.Map + ".bsp" : null);
    }

    // Host_Frame's server half, as far as this thread has one: the server's queued console output and events,
    // its frame when it has no thread of its own, and the moment the local player connects.
    private void PumpServer(LegacyLocalServer server)
    {
        server.Update();
        if (_shutDown || _failed) return;
        switch (server.State)
        {
            case LegacyLocalServerState.Running when _transport is null && _session is { } session && server.Transport is { } wire:
                _transport = wire;
                Log(string.Create(CultureInfo.InvariantCulture, $"local game: the server is up after {Now - _serverAskedAt:0.00} s ({server.Map}, {server.GameType}); connecting through the loopback"));
                _shared?.EnterSession();
                try { session.Connect(Now); }
                finally { _shared?.LeaveSession(); }
                LoadingScreen?.UpdateProgress(0.1f, "Connecting...");
                break;
            case LegacyLocalServerState.Running when _session is { } dropped && _reconnect.TakeConnect():
                // "connect local": the new game's level is up and the old game's goodbye has been read.
                Log("local game: the new game is up; connecting again through the loopback");
                _shared?.EnterSession();
                try { dropped.Connect(Now); }
                finally { _shared?.LeaveSession(); }
                LoadingScreen?.UpdateProgress(0.1f, "Connecting...");
                break;
            case LegacyLocalServerState.Failed:
                _reconnect.Cancel();
                Fail(_transport is null
                    ? "The local game could not be started: " + Printable(server.Error ?? "unknown reason", 300)
                    : "The local server stopped: " + Printable(server.Error ?? "unknown reason", 300));
                break;
            case LegacyLocalServerState.Stopped:
                _reconnect.Cancel();
                Log("the local server ended: " + Printable(server.Error ?? "shut down", 200));
                Callable.From(() => { if (!_shutDown) Disconnected?.Invoke(); }).CallDeferred();
                _failed = true;   // nothing more to pump; the shell tears the node down
                break;
        }
    }

    // Con_Print on a listen server: the server's console lines are this console's too.
    private void OnServerPrint(string text)
    {
        foreach (char c in text)
        {
            if (c != '\n')
            {
                if (_serverPrintLine.Length < 512) _serverPrintLine.Append(c);
                continue;
            }
            string line = _serverPrintLine.ToString();
            _serverPrintLine.Clear();
            if (line.Length == 0) continue;
            PostConsoleLine(ConsolePrint, line);
            if (_serverPrintsLogged++ < MaxLoggedPrints && (Headless || !string.IsNullOrEmpty(s_shotDirectory))) Log("server print: " + Printable(line, 300));
        }
    }

    // The server is leaving its level for the next one (match end, vote, "changelevel", "restart"): nothing will
    // arrive until the new level is up, so the loading screen goes up now, while frames are still being drawn.
    private void OnLevelChanging(string from)
    {
        if (_shutDown || _failed || !_inGame) return;
        _levelChangePending = true;
        Log($"server: leaving level {Printable(from)}; the loading screen is up until the next one is entered");
        RaiseLoadingScreen(from);
    }

    private void RaiseLoadingScreen(string map)
    {
        if (!_loadingDismissed && LoadingScreen is not null) return;
        _loadingDismissed = false;
        LoadingScreen = ShowLoadingScreen?.Invoke(map);
        LoadingScreen?.UpdateProgress(0.05f, "Changing level...");
    }

    // The developer console belongs to the native game first: a line it understands never reaches the Xonotic
    // console. "bot_number 6" or "timelimit 5" is such a line - the native game has cvars of those names - so
    // during a local game a cvar typed there that Xonotic's console also has is given to it as if typed there.
    // Only while the console is open, and only this way round: nothing of the session's reaches the native store.
    private void OnNativeCvarTyped(string name)
    {
        if (!ConsoleState.IsOpen || _server is null || _shutDown || _failed || _session is null) return;
        if (PlayerCvars is not { } player || _cvars is not { } cvars || !cvars.Has(name) || name.StartsWith("legacy_", StringComparison.Ordinal)) return;
        string value = player.GetString(name);
        if (value.Length > 512) return;
        foreach (char c in value)
            if (c < ' ' || c is '"' or ';' or '$' or '\\') return;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return;
        ConsoleCommand($"{name} \"{value}\"");
    }

    // DarkPlaces has one cvar store, so its menu sees the campaign level the server program has just unlocked.
    // Here the server hands exactly those cvars back (LegacyLocalServer.PlayerCvar) and they go into the
    // console the Xonotic menu runs on. A game without that menu has no such store: the program's campaign.cfg
    // (written under the legacy user folder) carries the progress to the next start instead.
    private void OnServerPlayerCvar(string name, string value)
    {
        if ((_shared ?? Menu?.Console) is not { } console) return;
        if (console.AcceptLocalServerCvar(name, value))
            Log($"local game: the server program saved {Printable(name)} = {Printable(value, 16)} (campaign progress); it is now in the player's Xonotic settings");
    }

    private void OnSharedCvarChanged(string name)
    {
        if (_server is not { } server || _shared is not { } shared || shared.SessionOrigin || _shutDown) return;
        string value = shared.Cvars.GetString(name);
        if (LegacyLocalCvars.ReachesServer(name, value)) server.SetCvar(name, value);
    }

    /// <summary>Puts the pointer at a window pixel (the review scripts' "mouse"), as a mouse motion event would.</summary>
    public void SetPointer(Vector2 position) => _mousePosition = position;

    /// <summary>Turns the view (the review scripts' "look"): pitch and yaw in degrees, as cl.viewangles.</summary>
    public void SetViewAngles(float pitch, float yaw)
    {
        if (_session is { } session && float.IsFinite(pitch) && float.IsFinite(yaw)) session.State.ViewAngles = new QcVector(pitch, yaw, 0);
    }

    /// <summary>Console text for the local game's server ("sv_cmd endmatch", "kick # 2", "changelevel boil"). False on a remote session.</summary>
    public bool ServerCommand(string line)
    {
        if (_server is not { } server || _shutDown || _failed) return false;
        server.Command(line);
        return true;
    }

    /// <summary>
    /// What crosses from the player's real configuration into the session, all of it read-only and all of
    /// it here: the name, the shirt and pants colours, the mouse sensitivity and pitch direction, and the
    /// field of view. (Key binds are consulted live, also read-only: <see cref="KeyBinding"/> and the bind
    /// table's held buttons.) Everything else in the session is Xonotic's default.
    /// </summary>
    private void SeedFromPlayer()
    {
        if (PlayerCvars is not { } player || _cvars is not { } session) return;
        string name = player.GetString("_cl_name");
        if (string.IsNullOrWhiteSpace(name)) name = player.GetString("name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            session.Set("_cl_name", name);
            session.Set("name", name);
        }
        foreach (string cvar in new[] { "_cl_color", "sensitivity", "m_pitch", "m_yaw", "fov" })
            if (player.Has(cvar) && player.GetString(cvar) is { Length: > 0 } value && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number))
                session.Set(cvar, value);
    }

    /// <summary>"host" or "host:port" (the last colon, when a port number follows it).</summary>
    public static bool TryParseAddress(string address, out string host, out int port)
    {
        host = (address ?? "").Trim();
        port = DefaultPort;
        if (host.Length == 0 || host.Length > 255) return false;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && colon < host.Length - 1 && int.TryParse(host.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            if (parsed is <= 0 or > 65535) return false;
            port = parsed;
            host = host[..colon];
        }
        return host.Length > 0;
    }

    // =====================================================================================================
    //  The session's console: the engine commands a configuration file or the server may run
    // =====================================================================================================

    private void RegisterEngineCommands(ConfigInterpreter interpreter)
    {
        // On the Xonotic menu's console the commands of this first group are the menu's: bind and its
        // relatives edit Xonotic's own key bindings there, connect / quit / disconnect / togglemenu / loadfont
        // act on the engine the menu stands in for, and each of them already refuses a server's text where it
        // has to (LegacyConsole.RegisterPlayerCommand). A private session registers its own.
        if (_shared is null)
        {
            // Binds in the session's configuration (binds-xonotic.cfg) and from the server stay in the session:
            // the player's bind table is not this interpreter's to write.
            foreach (string name in new[] { "bind", "unbind", "unbindall", "in_bind", "in_unbind", "in_bindmap", "in_releaseall", "bindlist" })
                _commands.Register(interpreter, name, _ => { }, "ignored in a legacy session: the player's own binds are used, read-only");
            // Engine commands with nothing to do here, kept from reaching the server as unknown commands.
            foreach (string name in new[] { "snd_restart", "r_restart", "vid_restart", "menu_restart", "toggleconsole", "screenshot" })
                _commands.Register(interpreter, name, _ => { }, "ignored in a legacy session");
            // snd_main.c S_Play_f / S_Play2_f / S_PlayVol_f / S_StopAllSounds_f and cd_shared.c CD_f: a server sends
            // "play2" for announcements and "cd loop" for a level's music.
            foreach (string name in new[] { "play", "play2", "playvol", "stopsound", "cd" })
                _commands.Register(interpreter, name, argv => _presentation?.SoundCommand(argv), "a DarkPlaces sound command: play / play2 / playvol <sample>, stopsound, cd [play|loop|stop|pause|resume|remap] [track]");
            // libcurl.c Curl_Curl_f, when it comes through the console (typed, or the client program's localcmd:
            // Xonotic's map vote fetches its screenshot packages so). A server's own "curl" lines are read by
            // the signon before they get here.
            _commands.Register(interpreter, "curl", argv =>
            {
                if (_session?.Packages is { } packages) packages.Command(argv, loadBegun: true);
                else Log("curl: there are no package downloads in this session");
            }, "curl --info | --cancel [file] | --pak [--as name.pk3] <url>: package downloads from a Xonotic server");
            // A server may tell a DarkPlaces client to go elsewhere or to exit. This client does neither on a server's say-so.
            foreach (string name in new[] { "connect", "reconnect", "quit", "exit", "playdemo", "record" })
            {
                string refused = name;
                _commands.Register(interpreter, name, _ => Log($"the session asked to run \"{refused}\": not followed"), "refused in a legacy session");
            }
            _commands.Register(interpreter, "disconnect", _ => Callable.From(() => { if (!_shutDown) Disconnected?.Invoke(); }).CallDeferred(),
                "leave the server and return to the menu");
            _commands.Register(interpreter, "togglemenu", argv =>
            {
                int mode = argv.Count > 1 && int.TryParse(argv[1], out int parsed) ? parsed : 1;
                ToggleMenu?.Invoke(mode);
            }, "open or close the menu");
            _commands.Register(interpreter, "loadfont", argv => _presentation?.LoadFontCommand(argv), "loadfont slot face[,fallback...] [sizes...]");
        }
        if (_shared is null && LocalGame is not null)
        {
            // A listen server's console: these run on the SERVER (sv_ccmds.c, prvm_edict.c PRVM_GameCommand).
            // On the menu's console the menu registers them itself, for local and remote sessions alike.
            foreach (string name in LegacyLocalCommands.ServerCommands)
                _commands.Register(interpreter, name, argv => ServerCommand(JoinArguments(argv)), "runs on the local game's server");
            foreach (string name in new[] { "map", "devmap", "changelevel", "restart", "maps" })
                _commands.Register(interpreter, name, argv =>
                {
                    string line = JoinArguments(argv);
                    Callable.From(() => { if (!_shutDown && MapCommand?.Invoke(line) != true) Log($"\"{Printable(line, 120)}\": not acted on"); }).CallDeferred();
                }, "level change on the local game's server");
        }
        // sbar.c Sbar_ShowScores / Sbar_DontShowScores: "+showscores" is the ENGINE's command. It sets sb_showscores
        // and tells the program through its sb_showscores global (CL_VM_UpdateShowingScoresState); Xonotic's
        // scoreboard is drawn while that global is set. The program does not register the command itself.
        _commands.Register(interpreter, "+showscores", _ => _session?.Host?.UpdateShowingScoresState(true), "show the scoreboard while held");
        _commands.Register(interpreter, "-showscores", _ => _session?.Host?.UpdateShowingScoresState(false), "hide the scoreboard");
        _commands.Register(interpreter, "messagemode", _ => OpenChat?.Invoke(false), "open the chat input line");
        _commands.Register(interpreter, "messagemode2", _ => OpenChat?.Invoke(true), "open the team chat input line");
        _commands.Register(interpreter, "impulse", argv =>
        {
            if (argv.Count > 1 && int.TryParse(argv[1], out int impulse)) _pendingImpulse = (byte)Math.Clamp(impulse, 0, 255);
        }, "send an impulse number to the server (select weapon, use item, etc)");

        // cl_input.c IN_*Down / IN_*Up. The player's own keys arrive through the bind table; these are for the
        // session's aliases (+hook is "+button6", +jetpack "+button10") and for anything the program runs.
        Button("attack", (ref LegacyHeldButtons h, bool down) => h.Attack = down);
        Button("jump", (ref LegacyHeldButtons h, bool down) => h.Jump = down);
        Button("use", (ref LegacyHeldButtons h, bool down) => h.Use = down);
        Button("speed", (ref LegacyHeldButtons h, bool down) => h.Speed = down);
        Button("forward", (ref LegacyHeldButtons h, bool down) => h.Forward = down);
        Button("back", (ref LegacyHeldButtons h, bool down) => h.Back = down);
        Button("moveleft", (ref LegacyHeldButtons h, bool down) => h.MoveLeft = down);
        Button("moveright", (ref LegacyHeldButtons h, bool down) => h.MoveRight = down);
        Button("moveup", (ref LegacyHeldButtons h, bool down) => h.MoveUp = down);
        Button("movedown", (ref LegacyHeldButtons h, bool down) => h.MoveDown = down);
        Button("button3", (ref LegacyHeldButtons h, bool down) => h.Button3 = down);
        Button("button4", (ref LegacyHeldButtons h, bool down) => h.Button4 = down);
        Button("button5", (ref LegacyHeldButtons h, bool down) => h.Button5 = down);
        Button("button6", (ref LegacyHeldButtons h, bool down) => h.Button6 = down);
        Button("button7", (ref LegacyHeldButtons h, bool down) => h.Button7 = down);
        Button("button8", (ref LegacyHeldButtons h, bool down) => h.Button8 = down);
        Button("button9", (ref LegacyHeldButtons h, bool down) => h.Button9 = down);
        Button("button10", (ref LegacyHeldButtons h, bool down) => h.Button10 = down);
        Button("button11", (ref LegacyHeldButtons h, bool down) => h.Button11 = down);
        Button("button12", (ref LegacyHeldButtons h, bool down) => h.Button12 = down);
        Button("button13", (ref LegacyHeldButtons h, bool down) => h.Button13 = down);
        Button("button14", (ref LegacyHeldButtons h, bool down) => h.Button14 = down);
        Button("button15", (ref LegacyHeldButtons h, bool down) => h.Button15 = down);
        Button("button16", (ref LegacyHeldButtons h, bool down) => h.Button16 = down);

        void Button(string name, ButtonSetter set)
        {
            _commands.Register(interpreter, "+" + name, _ => set(ref _scriptHeld, true), "engine button: press");
            _commands.Register(interpreter, "-" + name, _ => set(ref _scriptHeld, false), "engine button: release");
        }
    }

    private delegate void ButtonSetter(ref LegacyHeldButtons held, bool down);

    private static string JoinArguments(IReadOnlyList<string> argv)
    {
        StringBuilder text = new();
        for (int i = 0; i < argv.Count; i++)
        {
            if (i > 0) text.Append(' ');
            string argument = argv[i];
            bool quote = argument.Length == 0;
            foreach (char c in argument)
                if (c <= ' ' || c is '"' or ';') { quote = true; break; }
            if (quote) text.Append('"').Append(argument.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            else text.Append(argument);
        }
        return text.ToString();
    }

    /// <summary>A line typed into the developer console that the shell's own interpreter did not claim:
    /// it goes to the session's console, where an unknown command is forwarded to the server.</summary>
    public void ConsoleCommand(string line)
    {
        if (_session is null || string.IsNullOrWhiteSpace(line) || line.Length > 2048) return;
        // On the menu's console a typed line is the player's, not the session's: it goes into the menu's buffer.
        if (_shared is not null) _shared.AddText(line + "\n");
        else
        {
            // One store in DarkPlaces: a cvar the player types during a local game is the server's too.
            if (_server is { } server && _cvars is { } cvars && _interpreter is { } interpreter
                && LegacyLocalCommands.TryParseCvarAssignment(line, name => cvars.Has(name) && !interpreter.CommandNames.Contains(name), out string cvar, out string value)
                && LegacyLocalCvars.ReachesServer(cvar, value))
                server.SetCvar(cvar, value);
            _session.Console.AddText(line + "\n");
        }
    }

    /// <summary>
    /// CL_VM_InputEvent, for the menu's key dispatch (keys.c Key_Event with key_dest == key_game): offer a key
    /// to the client program before its bind runs. True if the program consumed it.
    /// </summary>
    public bool ProgramInputEvent(int type, int key, int character)
    {
        if (_shutDown || _failed || !_inGame || _session?.Host is not { Initialized: true } host) return false;
        _shared?.EnterSession();
        try { return host.InputEvent(type, key, character); }
        finally { _shared?.LeaveSession(); }
    }

    /// <summary>CL_VM_ConsoleCommand: a command created with registercommand was typed. True if the client program handled it.</summary>
    public bool ProgramConsoleCommand(string line)
    {
        if (_shutDown || _failed || _session?.Host is not { Initialized: true } host) return false;
        _shared?.EnterSession();
        try { return host.ConsoleCommand(line); }
        finally { _shared?.LeaveSession(); }
    }

    /// <summary>Cmd_ForwardToServer: a console line this client does not know goes to the server as it stands.</summary>
    public void SendToServer(string line)
    {
        if (_shutDown || _failed || line.Length is 0 or > 1024) return;
        _session?.Client.SendStringCommand(line);
    }

    // =====================================================================================================
    //  One frame
    // =====================================================================================================

    public override void _Process(double delta)
    {
        if (_shutDown || _failed) return;
        LegacyPerfLog.BeginFrame(_inGame ? 1 : 0);
        try { ProcessFrame(delta); }
        finally { LegacyPerfLog.EndFrame(); }
    }

    private void ProcessFrame(double delta)
    {
        // host.c Host_Frame: the server's part of the frame first, then the client's.
        if (_server is { } server)
        {
            using var _serverScope = FrameProfiler.Scope("legacy-server");
            PumpServer(server);
        }
        LegacyPerfLog.Part(LegacyPerfLog.Server);
        using var _scope = FrameProfiler.Scope("legacy");
        if (_menuWarmer is null && Now - _menuWarmerLookedAt > 1) RestMenuWarmer();
        // Frames spent waiting (for the local server, for the first message): models whose files the worker
        // threads have finished get their nodes built, a few milliseconds a frame.
        if (!_inGame && _presentation is { } waiting) waiting.PrebuildReady(0.008);
        // No transport yet: the local server is still starting, and the loading screen is all there is to draw.
        if (_shutDown || _failed || _session is not { } session || _transport is not { } transport || _presentation is not { } presentation)
            return;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_inGame) _frameMeter.Add(delta * 1000);

        // Everything below is the session's own work: the server's console text runs in it and its client
        // program sets cvars in it. On the menu's console that has to be told apart from the player's.
        _shared?.EnterSession();
        try { Frame(session, transport, presentation, delta); }
        finally { _shared?.LeaveSession(); }
        if (_inGame) _clientMeter.Add(System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds);
    }

    // A line for the console is handed over on a pool thread, in order. The console's own work is small, but
    // the line also goes to the process's standard output, and a write there is not always quick: with the
    // output redirected to a file, the first line after a few quiet seconds held the frame for 14 ms
    // (measured: every kill message was a dropped frame). The log facade the console prints through is
    // made for calls from any thread.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(Action<string> Sink, string Line)> s_consoleLines = new();
    private static int s_consolePumping;

    private static void PostConsoleLine(Action<string>? sink, string line)
    {
        if (sink is null) return;
        s_consoleLines.Enqueue((sink, line));
        if (Interlocked.CompareExchange(ref s_consolePumping, 1, 0) == 0) System.Threading.Tasks.Task.Run(PumpConsoleLines);
    }

    private static void PumpConsoleLines()
    {
        do
        {
            while (s_consoleLines.TryDequeue(out (Action<string> Sink, string Line) item))
            {
                try { item.Sink(item.Line); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }
            Volatile.Write(ref s_consolePumping, 0);
        }
        while (!s_consoleLines.IsEmpty && Interlocked.CompareExchange(ref s_consolePumping, 1, 0) == 0);
    }

    private bool _traceInstalled;
    private long[] _tracedTicks = new long[700];
    private int _tracedCount;
    private long _tracedBuiltins;
    private int _tracedCommand = -1;
    private long _tracedSince;

    private void CloseTracedCommand(int next)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        QcProfile? profile = _session?.Host?.Vm.Profile;
        if (_tracedCommand >= 0 && _inGame && System.Diagnostics.Stopwatch.GetElapsedTime(_tracedSince, now).TotalMilliseconds >= 2)
        {
            string detail = "";
            if (profile is not null)
            {
                // The three builtins the command spent most under, and all of them together.
                Span<(long Ticks, int Number)> top = stackalloc (long, int)[3];
                for (int number = 0; number < _tracedCount; number++)
                {
                    long spent = profile.TicksOf(number) - _tracedTicks[number];
                    if (spent <= top[2].Ticks) continue;
                    top[2] = (spent, number);
                    for (int i = 2; i > 0 && top[i].Ticks > top[i - 1].Ticks; i--) (top[i], top[i - 1]) = (top[i - 1], top[i]);
                }
                detail = string.Create(CultureInfo.InvariantCulture, $" (builtins {QcProfile.ToMilliseconds(profile.BuiltinTicks - _tracedBuiltins):0.0} ms: #{top[0].Number} {QcProfile.ToMilliseconds(top[0].Ticks):0.0}, #{top[1].Number} {QcProfile.ToMilliseconds(top[1].Ticks):0.0}, #{top[2].Number} {QcProfile.ToMilliseconds(top[2].Ticks):0.0}; longest call #{profile.WorstBuiltin}: {QcProfile.ToMilliseconds(profile.WorstTicks):0.00} ms)");
            }
            LegacyPerfLog.Event("server command " + ((VortexArena.Legacy.Protocol.Svc)_tracedCommand).ToString() + detail, _tracedSince);
        }
        profile?.ResetWorst();
        _tracedCommand = next;
        _tracedSince = now;
        if (profile is not null && next >= 0)
        {
            _tracedCount = profile.CopyTicks(ref _tracedTicks);
            _tracedBuiltins = profile.BuiltinTicks;
        }
    }

    // What a run is measured by: the whole frame as Godot reports it (delta), and this node's own share of it.
    private struct Meter
    {
        private float[] _samples;
        private int _count;
        public long Total;
        public double Sum, Max;
        public void Add(double value)
        {
            _samples ??= new float[4096];
            _samples[_count++ & 4095] = (float)value;
            Total++;
            Sum += value;
            if (value > Max) Max = value;
        }
        public readonly double Mean => Total > 0 ? Sum / Total : 0;
        /// <summary>A percentile over the last 4096 samples.</summary>
        public readonly double Percentile(double p)
        {
            int n = Math.Min(_count, 4096);
            if (n == 0 || _samples is null) return 0;
            float[] sorted = new float[n];
            Array.Copy(_samples, sorted, n);
            Array.Sort(sorted);
            return sorted[Math.Clamp((int)(p * (n - 1)), 0, n - 1)];
        }
        public void Reset() { _count = 0; Total = 0; Sum = Max = 0; }

        /// <summary>Copies the samples kept (at most 4096) into <paramref name="into"/>; returns how many.</summary>
        public readonly int CopyTo(float[] into)
        {
            int n = Math.Min(Math.Min(_count, 4096), into.Length);
            if (n > 0 && _samples is not null) Array.Copy(_samples, into, n);
            return n;
        }

        /// <summary>A percentile of <paramref name="n"/> samples, which are sorted in place.</summary>
        public static double PercentileOf(float[] samples, int n, double p, bool sort)
        {
            if (n <= 0) return 0;
            if (sort) Array.Sort(samples, 0, n);
            return samples[Math.Clamp((int)(p * (n - 1)), 0, n - 1)];
        }
    }

    // The status line's percentiles and the process's working set are worked out on a pool thread: sorting two
    // times 4096 samples twice over and asking the operating system about the process took the frame that did
    // it some 40 ms, once a second. The frame only copies the samples.
    private readonly float[] _statusFrames = new float[4096], _statusClient = new float[4096];
    private int _statusBusy;
    private Meter _frameMeter, _clientMeter;

    private void Frame(LegacyClientSession session, ILegacyTransport transport, GodotLegacyPresentation presentation, double delta)
    {
        double now = Now;
        // --- the clock runs on, then the network is read (and may correct the clock) ---
        session.BeginFrame(now);
        if (LegacyPerfLog.Enabled && !_traceInstalled)
        {
            // Developer aid (VORTEX_LEGACY_PERFLOG): a server command that took over two milliseconds to act on
            // is named in the perf log - what a slow "receive" was.
            _traceInstalled = true;
            session.Client.Parser.CommandTrace = (svc, _) => CloseTracedCommand(svc);
        }
        for (int i = 0; i < MaxDatagramsPerFrame && transport.TryReceive(out byte[] datagram); i++)
        {
            session.Receive(datagram, now);
            if (_traceInstalled) CloseTracedCommand(-1);
            if (_shutDown || _failed) return;
        }
        if (session.DemoPlaying)
        {
            // CL_ReadDemoMessage: the recorded messages whose time has come.
            session.ReadDemo();
            // "pausedemo": DarkPlaces' particles and lights run on cl.time and stand still with it.
            presentation.FreezeEffects(session.DemoPaused);
            if (_traceInstalled) CloseTracedCommand(-1);
            if (_shutDown || _failed) return;
        }
        // Loading a level inside Receive can take seconds; everything after it uses the time it is now.
        now = Now;
        LegacyPerfLog.Part(LegacyPerfLog.Receive);

        // --- input, the command, the send ---
        UpdateViewSize();
        // A recording has its own view angles and takes no input.
        LegacyInput input = session.State.IsDemo ? default : SampleInput(session);
        foreach (byte[] datagram in session.Frame(now, input))
            transport.Send(datagram);

        if (!CheckConnection(session, now)) return;
        LegacyPerfLog.Part(LegacyPerfLog.Send);

        // --- draw: the engine's view for the frame, CSQC_UpdateView, then what it submitted ---
        presentation.BeginFrame(_viewSize, session.State.Time);
        QcProfile? frameProfile = s_profileProgram && _inGame ? session.Host?.Vm.Profile : null;
        long drawBegan = 0, builtinsBefore = 0;
        int profiled = 0;
        if (frameProfile is not null)
        {
            profiled = frameProfile.CopyTicks(ref _ticksBefore);
            builtinsBefore = frameProfile.BuiltinTicks;
            drawBegan = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        session.Draw(delta);
        if (frameProfile is not null && System.Diagnostics.Stopwatch.GetElapsedTime(drawBegan).TotalMilliseconds >= 8) NoteSlowProgramFrame(session, frameProfile, drawBegan, builtinsBefore, profiled);
        LegacyPerfLog.Part(LegacyPerfLog.Program);
        presentation.AdvanceParticles(delta);
        // SCR_DrawScreen: the engine's own 2D goes on after the program's (Con_DrawNotify after CL_VM_UpdateView).
        if (_inGame) presentation.DrawChatArea(_chatLines, now);
        if (_inGame && session.State.Paused) presentation.DrawPause();
        presentation.EndFrame();
        // Textures the bank has compressed since they were uploaded take their uncompressed versions' place, a few a frame.
        _textureBank?.Pump();
        if (session.Host is { FaultCount: > 0 } faulted)
        {
            // Host_Error: DarkPlaces drops the connection when the client program faults.
            Fail("The server's game code stopped with an error and the connection was closed. (" + Printable(faulted.FaultMessage ?? "unknown fault", 300) + ")");
            return;
        }

        UpdateCursor(session);
        if (_autoJoinAfter > 0 && !_autoJoined && _inGameAt >= 0 && now - _inGameAt >= _autoJoinAfter)
        {
            _autoJoined = true;
            session.Client.SendStringCommand("join");
            Log("legacy_autojoin: sent \"join\"");
        }
        if (now >= _nextStatus) Status(session, presentation, now);
        CaptureForReview(now);
        LegacyPerfLog.Part(LegacyPerfLog.Present);
    }

    // Developer aid (VORTEX_LEGACY_QCPROFILE): a frame whose CSQC_UpdateView took 8 ms or more is named in the perf
    // log by the builtins it spent the time under - what a slow "program" was.
    private long[] _ticksBefore = new long[700];

    private void NoteSlowProgramFrame(LegacyClientSession session, QcProfile profile, long began, long builtinsBefore, int count)
    {
        Span<(long Ticks, int Number)> top = stackalloc (long, int)[5];
        for (int number = 0; number < count; number++)
        {
            long spent = profile.TicksOf(number) - _ticksBefore[number];
            if (spent <= top[4].Ticks) continue;
            top[4] = (spent, number);
            for (int i = 4; i > 0 && top[i].Ticks > top[i - 1].Ticks; i--) (top[i], top[i - 1]) = (top[i - 1], top[i]);
        }
        StringBuilder text = new("slow program frame: builtins ");
        text.Append(CultureInfo.InvariantCulture, $"{QcProfile.ToMilliseconds(profile.BuiltinTicks - builtinsBefore):0.0} ms -");
        foreach ((long ticks, int number) in top)
        {
            if (ticks <= 0) break;
            string name = "?";
            if (session.Host?.Vm is { } vm)
                foreach (QcFunction function in vm.Functions)
                    if (function.IsBuiltin && -function.FirstStatement == number) { name = function.Name; break; }
            text.Append(CultureInfo.InvariantCulture, $" #{number} {name} {QcProfile.ToMilliseconds(ticks):0.0};");
        }
        LegacyPerfLog.Event(text.ToString(), began);
    }

    // Developer aid for checking legacy mode by eye without sitting at the machine: with the environment
    // variable VORTEX_LEGACY_SHOTS naming a directory, the window is saved there as shot-NN.png every five
    // seconds once in the game. An environment variable rather than a cvar so that nothing a server sends
    // can turn it on. The image is the last frame the renderer finished, which is what was on screen.
    private static readonly string? s_shotDirectory = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_SHOTS");
    private static readonly bool s_dumpDraws = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_DUMP"));
    private double _nextShot;
    private int _shotCount;

    private void CaptureForReview(double now)
    {
        if (string.IsNullOrEmpty(s_shotDirectory) || _inGameAt < 0) return;
        // Started from the Xonotic menu, the script file is the menu's to run (it drives the pointer and keys too).
        if (Menu is not null && LegacyMenu.HasReviewScript) return;
        RunReviewScript(now);
        if (_script is not null || now < _nextShot || _shotCount >= 60) return;
        _nextShot = now + 5;
        SaveShot($"shot-{_shotCount:00}");
    }

    private void SaveShot(string name)
    {
        Image? image = GetViewport()?.GetTexture()?.GetImage();
        if (image is null) return; // a windowless run has no frame to save
        System.IO.Directory.CreateDirectory(s_shotDirectory!);
        string path = System.IO.Path.Combine(s_shotDirectory!, name + ".png");
        int index = _shotCount++;
        // Encoding a PNG takes most of a second; the frame it would cost is the thing being photographed.
        Log($"saving {path}");
        System.Threading.Tasks.Task.Run(() => image.SavePng(path));
        if (s_dumpDraws && _presentation is { } presentation) presentation.DumpFrame(line => Log($"s{index:00} " + line));
    }

    // The second developer aid: VORTEX_LEGACY_SCRIPT names a text file of lines "<seconds in the game> <command>".
    // Each command goes to the session's console at its time, as if typed ("+forward", "impulse 2", "+showscores",
    // "messagemode"); "shot <name>" saves the window instead, and "look <pitch> <yaw>" turns the view. With a
    // script the five-second shots stop. An environment variable for the same reason as the shots: nothing a
    // server sends can reach it.
    private static readonly string? s_scriptPath = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_SCRIPT");
    private List<(double At, string Command)>? _script;
    private int _scriptNext;
    private bool _scriptLoaded;
    private double _scriptBase = -1;
    private int _scriptWaitLevel;
    private bool _scriptWaitPause;
    private double _scriptPauseLineAt;

    private double _trackUntil, _watchUntil;
    private string[] _watched = Array.Empty<string>();
    private int _watchFrame;

    private void Watch(int playerEntity)
    {
        if (_server is not { } server || (_watchFrame++ % 3) != 0) return;
        string[] names = _watched;
        server.Post(game =>
        {
            if (game.Server.Host is not { } host || playerEntity <= 0 || playerEntity >= host.Vm.NumEdicts) return;
            StringBuilder line = new();
            QcVector p = host.Vm.FieldVector(playerEntity, host.F.Origin);
            line.Append(CultureInfo.InvariantCulture, $"watch: player {p.X:0.0} {p.Y:0.0} {p.Z:0.0} ground {host.Vm.FieldInt(playerEntity, host.F.GroundEntity)}");
            foreach (string name in names)
            {
                string className = name;
                int nth = 1, hash = name.IndexOf('#');
                if (hash > 0 && int.TryParse(name.AsSpan(hash + 1), out int parsed) && parsed > 0) { nth = parsed; className = name[..hash]; }
                for (int e = 1, seen = 0; e < host.Vm.NumEdicts; e++)
                    if (!host.Vm.IsFree(e) && host.Vm.GetString(host.Vm.FieldInt(e, host.F.ClassName)) == className && ++seen == nth)
                    {
                        QcVector o = host.Vm.FieldVector(e, host.F.Origin), hi = host.Vm.FieldVector(e, host.F.AbsMax);
                        line.Append(CultureInfo.InvariantCulture, $" | {name} e{e} origin z {o.Z:0.0} top {hi.Z:0.0}");
                        break;
                    }
            }
            game.Command("echo " + line);
        });
    }

    private void RunReviewScript(double now)
    {
        if (now < _watchUntil && _session is { } watchedSession) Watch(watchedSession.State.PlayerEntity);
        if (now < _trackUntil && _presentation is { } tracked && _session is { } trackedSession)
            Log(string.Create(CultureInfo.InvariantCulture, $"track t {trackedSession.State.Time:0.0000} camera {tracked.CameraPosition.X:0.0000} {tracked.CameraPosition.Y:0.0000} {tracked.CameraPosition.Z:0.0000}"));
        if (!_scriptLoaded)
        {
            _scriptLoaded = true;
            if (!string.IsNullOrEmpty(s_scriptPath) && System.IO.File.Exists(s_scriptPath))
            {
                _script = new List<(double, string)>();
                foreach (string raw in System.IO.File.ReadAllLines(s_scriptPath))
                {
                    string line = raw.Trim();
                    int space = line.IndexOf(' ');
                    if (line.Length == 0 || line[0] == '#' || space <= 0) continue;
                    if (double.TryParse(line.AsSpan(0, space), NumberStyles.Float, CultureInfo.InvariantCulture, out double at))
                        _script.Add((at, line[(space + 1)..].Trim()));
                }
                _script.Sort((a, b) => a.At.CompareTo(b.At));
                Log($"review script: {_script.Count} lines from {s_scriptPath}");
            }
        }
        if (_script is null || _session is not { } session) return;
        if (_scriptBase < 0) _scriptBase = _inGameAt;
        if (_scriptWaitPause)
        {
            // "sync pause": hold the script until the recording has paused itself (demopause), and time what
            // follows from there.
            if (!session.DemoPaused) return;
            Log(string.Create(CultureInfo.InvariantCulture, $"script: sync pause reached at t+{now - _inGameAt:0.00}, demo time {session.State.Time:0.000000}, message {session.DemoMessages}"));
            _scriptWaitPause = false;
            // The lines that follow are timed from the pause, on the script's own running clock: a line written
            // two seconds after the "sync pause" line runs two seconds after the pause.
            _scriptBase = now - _scriptPauseLineAt;
        }
        if (_scriptWaitLevel > 0)
        {
            // "sync level": hold the script until the NEXT level has been entered, and time what follows from there.
            if (_levelsEntered < _scriptWaitLevel || !_inGame) return;
            Log($"script: sync level reached at t+{now - _inGameAt:0.00} (level {_levelsEntered})");
            _scriptWaitLevel = 0;
            _scriptBase = now;
        }
        while (_scriptNext < _script.Count && now - _scriptBase >= _script[_scriptNext].At)
        {
            string command = _script[_scriptNext++].Command;
            Log($"script t+{now - _inGameAt:0.00}: {command}");
            if (command == "sync level")
            {
                _scriptWaitLevel = _levelsEntered + 1;
                return;
            }
            if (command.StartsWith("demopause ", StringComparison.Ordinal))
            {
                // "demopause <server time>": the recording pauses itself on the first message at or after that
                // time - the frame DarkPlaces holds when a "pausedemo" was written into that message.
                if (double.TryParse(command.AsSpan(10), NumberStyles.Float, CultureInfo.InvariantCulture, out double pauseAt)) session.DemoPauseAt = pauseAt;
                continue;
            }
            if (command.StartsWith("colourdbg ", StringComparison.Ordinal))
            {
                // "colourdbg <switch> [value]": one part of the picture's colour path on or off, or "dump"
                // (GodotLegacyPresentation.ColourDebug) - for taking a frame apart beside DarkPlaces.
                _presentation?.ColourDebug(command[10..]);
                continue;
            }
            if (command == "sync pause")
            {
                _scriptWaitPause = true;
                _scriptPauseLineAt = _script[_scriptNext - 1].At;
                return;
            }
            if (command == "resume")
            {
                session.DemoPaused = false;
                continue;
            }
            if (command.StartsWith("sv ", StringComparison.Ordinal))
            {
                ServerCommand(command[3..]);
                continue;
            }
            if (command == "mem" || command.StartsWith("mem ", StringComparison.Ordinal))
            {
                // "mem [label]": what the process holds right now (the menu's review scripts have the same line).
                Log("memory " + (command.Length > 4 ? command[4..].Trim() : "") + ": " + LegacyData.MemoryReport());
                continue;
            }
            if (command.StartsWith("track ", StringComparison.Ordinal))
            {
                // "track <seconds>": the camera's position in the log every frame for that long (a lift ride).
                if (double.TryParse(command.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out double trackFor)) _trackUntil = now + Math.Clamp(trackFor, 0, 60);
                continue;
            }
            if (command.StartsWith("watch ", StringComparison.Ordinal))
            {
                // "watch <seconds> <classname>[#n] ...": the server's own positions of those entities and of the
                // player, in the log every few frames for that long (who carried whom on a lift).
                string[] watch = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (watch.Length >= 3 && double.TryParse(watch[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double watchFor))
                {
                    _watchUntil = now + Math.Clamp(watchFor, 0, 30);
                    _watched = watch[2..];
                }
                continue;
            }
            if (command == "nodes")
            {
                // "nodes": which nodes of the whole tree are processed every frame, by type - what else runs
                // beside the session (the perf log's "outside the node").
                Dictionary<string, int> processing = new();
                int all = 0;
                CountProcessing(GetTree().Root, processing, ref all);
                List<KeyValuePair<string, int>> rows = new(processing);
                rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                StringBuilder line = new($"nodes: {all} in the tree; processed each frame:");
                foreach ((string type, int count) in rows) line.Append(' ').Append(type).Append(" x").Append(count).Append(';');
                Log(line.ToString());
                continue;
            }
            if (command.StartsWith("warp ", StringComparison.Ordinal))
            {
                Warp(session.State.PlayerEntity, command[5..].Trim());
                continue;
            }
            if (command.StartsWith("shot ", StringComparison.Ordinal))
            {
                SaveShot(command[5..].Trim());
                return; // what follows a shot waits for the next frame, so the shot shows the state before it
            }
            else if (command.StartsWith("look ", StringComparison.Ordinal))
            {
                string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw))
                    session.State.ViewAngles = new QcVector(pitch, yaw, 0);
            }
            else if (command.StartsWith("server ", StringComparison.Ordinal)) session.Client.SendStringCommand(command[7..]);
            else ConsoleCommand(command);   // as if typed
        }
    }

    private static void CountProcessing(Node node, Dictionary<string, int> into, ref int all)
    {
        all++;
        string modes = (node.IsProcessing() ? "P" : "") + (node.IsPhysicsProcessing() ? "F" : "") + (node.IsProcessingInternal() ? "p" : "") + (node.IsPhysicsProcessingInternal() ? "f" : "");
        if (modes.Length > 0 && node.CanProcess())
        {
            string key = node.GetType().Name + "/" + modes + (node is CanvasItem { Visible: false } or Node3D { Visible: false } ? "(hidden)" : "");
            into[key] = into.GetValueOrDefault(key) + 1;
        }
        foreach (Node child in node.GetChildren()) CountProcessing(child, into, ref all);
    }

    // "warp <classname>[#n] [x y z]" or "warp at <x> <y> <z>" in a review script of a LOCAL game: puts the local
    // player at the first server entity of that class (plus an offset), or at a point - setorigin from outside
    // the program, for reaching a flag or a lift without steering there. The touch that follows is the
    // program's own. Part of the review script, so it exists only under VORTEX_LEGACY_SCRIPT.
    private void Warp(int playerEntity, string arguments)
    {
        if (_server is not { } server) return;
        string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        bool point = parts[0] == "at";
        // "warp top <classname>[#n] [dz]": onto a brush entity (a lift, a door) - the middle of its box, dz above its top.
        bool top = parts[0] == "top" && parts.Length >= 2;
        if (top) parts = parts[1..];
        float[] v = new float[3];
        for (int i = 0; i < 3; i++)
            if (parts.Length > 1 + i) float.TryParse(parts[1 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
        // "item_flag_team#2": the second entity of that class, in edict order.
        string className = parts[0];
        int nth = 1;
        int hash = className.IndexOf('#');
        if (hash > 0 && int.TryParse(className.AsSpan(hash + 1), out int parsedNth) && parsedNth > 0)
        {
            nth = parsedNth;
            className = className[..hash];
        }
        server.Post(game =>
        {
            if (game.Server.Host is not { } host || playerEntity <= 0 || playerEntity >= host.Vm.NumEdicts || host.Vm.IsFree(playerEntity)) return;
            QcVector target = new(v[0], v[1], v[2]);
            if (!point)
            {
                int found = 0;
                for (int e = 1, seen = 0; e < host.Vm.NumEdicts && found == 0; e++)
                    if (!host.Vm.IsFree(e) && host.Vm.GetString(host.Vm.FieldInt(e, host.F.ClassName)) == className && ++seen == nth) found = e;
                if (found == 0) { game.Command("echo warp: no entity of that class"); return; }
                QcVector at = host.Vm.FieldVector(found, host.F.Origin);
                target = new QcVector(at.X + v[0], at.Y + v[1], at.Z + v[2]);
                if (top)
                {
                    QcVector lo = host.Vm.FieldVector(found, host.F.AbsMin), hi = host.Vm.FieldVector(found, host.F.AbsMax);
                    target = new QcVector((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, hi.Z + (v[0] != 0 ? v[0] : 30));
                    game.Command(string.Create(CultureInfo.InvariantCulture, $"echo warp: entity {found} box {lo.X:0} {lo.Y:0} {lo.Z:0} to {hi.X:0} {hi.Y:0} {hi.Z:0}"));
                }
            }
            host.Vm.FieldVector(playerEntity, host.F.Origin) = target;
            host.Vm.FieldVector(playerEntity, host.F.OldOrigin) = target;
            host.Vm.FieldVector(playerEntity, host.F.Velocity) = default;
            // Not on the ground any more (FL_ONGROUND, 512): with sv_gameplayfix_nogravityonground a player who is
            // told it stands on something does not fall, and would hang where it was put until it moved.
            ref float flags = ref host.Vm.FieldFloat(playerEntity, host.F.Flags);
            flags = (int)flags & ~512;
            host.Vm.FieldInt(playerEntity, host.F.GroundEntity) = 0;
            host.LinkEdict(playerEntity);
            game.Command(string.Create(CultureInfo.InvariantCulture, $"echo warp: player {playerEntity} to {target.X:0} {target.Y:0} {target.Z:0}"));
        });
    }

    // The window size as the program is told it, and the 2D space derived from it. Xonotic's menu program
    // normally maintains vid_conwidth / vid_conheight; there is none here.
    private void UpdateViewSize()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        if (size == _viewSize || !(size.X >= 1) || !(size.Y >= 1) || _cvars is null || _options is null) return;
        _viewSize = size;
        _options.ViewWidth = size.X;
        _options.ViewHeight = size.Y;
        // On the Xonotic menu's console the menu program and its host keep these four cvars (LegacyMenu.UpdateViewSize).
        if (_shared is not null) return;
        float pixelHeight = _cvars.Has("vid_pixelheight") && _cvars.GetFloat("vid_pixelheight") > 0 ? _cvars.GetFloat("vid_pixelheight") : 1;
        (int conWidth, int conHeight) = LegacyConsoleSize.For(size.X, size.Y, pixelHeight, _cvars.GetFloat("menu_vid_scale"));
        _cvars.Set("vid_width", ((int)size.X).ToString(CultureInfo.InvariantCulture));
        _cvars.Set("vid_height", ((int)size.Y).ToString(CultureInfo.InvariantCulture));
        _cvars.Set("vid_conwidth", conWidth.ToString(CultureInfo.InvariantCulture));
        _cvars.Set("vid_conheight", conHeight.ToString(CultureInfo.InvariantCulture));
    }

    // Connection state worth acting on. False if the session ended.
    private bool CheckConnection(LegacyClientSession session, double now)
    {
        DpClient client = session.Client;
        if (client.State != _lastState)
        {
            _lastState = client.State;
            Log($"client state: {_lastState}" + (client.LastError is null ? "" : $" ({Printable(client.LastError, 300)})"));
            switch (_lastState)
            {
                case DpClientState.Rejected:
                    Fail("The server refused the connection: " + Printable(client.LastError ?? "no reason given", 200));
                    return false;
                case DpClientState.TimedOut:
                    Fail(_inGame ? "The connection to the server was lost (no answer)." : "The server did not answer.");
                    return false;
                case DpClientState.Failed:
                    Fail("The server sent something this client could not read: " + Printable(client.LastError ?? "protocol error", 200));
                    return false;
                case DpClientState.Disconnected:
                    if (_server is { State: LegacyLocalServerState.Running } && _reconnect.ClientDisconnected())
                    {
                        // The goodbye of a game that "map" replaced. No held key is carried into the next one.
                        Log("the server ended the connection to start a new game; the session waits for its level");
                        _scriptHeld = default;
                        _pendingImpulse = 0;
                        break;
                    }
                    Log("the server ended the connection");
                    Callable.From(() => { if (!_shutDown) Disconnected?.Invoke(); }).CallDeferred();
                    return false;
            }
        }
        if (session.ProgramError is { } programError)
        {
            Fail("The server's game code could not be started: " + Printable(programError, 300));
            return false;
        }
        if (session.WorldError is { } worldError)
        {
            // The map is there and cannot be used (a format this client does not read, a damaged file).
            // DarkPlaces would print the loader's error and go on into an empty world. This client leaves.
            Fail("The map could not be loaded: " + Printable(worldError, 400));
            return false;
        }
        if (client.Signon.MissingWorld is { } missingWorld)
        {
            // DarkPlaces prints "Map %s not found" and goes on into an empty world. This client leaves instead.
            StringBuilder why = new($"Map {Printable(missingWorld, 80)} not found: this client does not have the map and could not get it from the server.");
            if (session.Packages is { } tried)
            {
                if (!tried.Limits.Enabled) why.Append(" Package downloads are switched off (legacy_curl_enabled 0).");
                else if (tried.Failures.Count == 0 && tried.Fetched + tried.FromCache == 0) why.Append(" The server named no download address for it (sv_curl_defaulturl is not set there).");
                foreach (string failure in tried.Failures) why.Append(" Download failed - ").Append(Printable(failure, 300)).Append('.');
            }
            foreach (string attempt in client.Signon.FallbackLog) why.Append(' ').Append(Printable(attempt, 200)).Append('.');
            Fail(why.ToString());
            return false;
        }
        if (session.MessagesNotDecoded > 0)
        {
            Fail("A message from the server could not be read and the connection was closed. (" + Printable(session.FirstUndecoded ?? "", 300) + ")");
            return false;
        }

        int stage = client.Signon.Stage;
        if (stage != _lastStage)
        {
            _lastStage = stage;
            Log($"signon stage {stage}" + stage switch
            {
                1 => $": level {session.State.WorldModel}, client program {client.Signon.CsqcProgName} size {client.Signon.CsqcProgSize} crc {client.Signon.CsqcProgCrc}",
                2 => ": sent \"spawn\"",
                3 => ": sent \"begin\"",
                4 => $": in the game. view entity {session.State.ViewEntity}, player entity {session.State.PlayerEntity}, {now - _startedAt:0.0} s after starting, keepalives sent while loading {_keepAlives}, " +
                     $"model files parsed for the program {_presentation?.ModelData.ModelsParsed} ({_presentation?.ModelData.ModelBytesRead / (1024 * 1024)} MB) in {_presentation?.ModelData.ModelLoadSeconds:0.00} s",
                _ => "",
            });
            if (!string.IsNullOrEmpty(s_shotDirectory) && stage is >= 1 and <= 3 && !Headless) SaveShot($"loading-stage{stage}");
            LoadingScreen?.UpdateProgress(stage switch { 0 => 0.1f, 1 => 0.35f, 2 => 0.8f, 3 => 0.9f, _ => 1f },
                stage switch { 0 => "Connecting...", 1 => "Loading the game code...", 2 => "Spawning...", 3 => "Entering the game...", _ => "" });
        }
        UpdateDownloadDisplay(session, now);

        if (_inGame && session.State.Signon < DpProtocol.Signons)
        {
            // CL_ParseServerInfo on a client that was in the game: the server has started another level. The
            // session has already rebuilt its side of it (the map, the collision, a fresh client program);
            // what is left is this node's: no held keys carried over, the loading screen until signon is done.
            _inGame = false;
            _levelChangePending = false;
            _scriptHeld = default;
            _pendingImpulse = 0;
            _chatLines.Clear();
            if (_textureBank is { } resting) resting.Playing = false;
            Log($"level change: now loading {session.State.WorldModel}");
            RaiseLoadingScreen(session.State.WorldNameNoExtension);
        }
        if (!_inGame && session.State.Signon >= DpProtocol.Signons)
        {
            _inGame = true;
            _enteredAt = now;
            _levelsEntered++;
            if (_inGameAt < 0)
            {
                _inGameAt = now;
                LegacyPerfLog.Mark("in the game");
                LegacyPerfLog.SetState(1);
                Log(string.Create(CultureInfo.InvariantCulture, $"in the game {now - _startedAt:0.00} s after the session was started"));
            }
            else Log(string.Create(CultureInfo.InvariantCulture, $"in the game again (level {_levelsEntered} of this session)"));
            _nextStatus = now + 1;
            _frameMeter.Reset();
            _clientMeter.Reset();
            if (s_profileProgram && session.Host is { } profiled)
            {
                profiled.Vm.Profile ??= new QcProfile();
                if (_presentation is { } scene) scene.SceneProfile ??= new long[8];
            }
        }
        // The loading screen stays for the level's first few frames: the entities of the first server frames are
        // given their models under it, and the pipelines of what was built ahead are compiled, instead of both
        // being the first thing the player sees of the level. Bounded: a level that never settles is shown anyway.
        if (_inGame && !_loadingDismissed && !_levelChangePending
            && (_presentation is not { } settling || settling.SceneSettled || now - _enteredAt > MaxSettleSeconds))
        {
            if (_presentation is { } shown) shown.Loading = false;
            // F3: a later level of the session - what only the level before it used is forgotten first.
            if (_levelsEntered > 1) _presentation?.TrimCachesAfterLevelChange();
            CollectAfterLoad();
            now = Now;   // the collection is part of the load
            if (_textureBank is { } bank) bank.Playing = true;
            LegacyPerfLog.Mark("loading screen down");
            Log(string.Create(CultureInfo.InvariantCulture, $"the loading screen came down {now - _startedAt:0.00} s after the session was started ({now - _enteredAt:0.00} s after entering the game)"));
            _loadingDismissed = true;
            // The screen is the shell's node and is freed by this call: nothing here may touch it afterwards.
            LoadingScreen = null;
            DismissLoadingScreen?.Invoke();
        }
        return true;
    }

    // A level's load leaves most of a gigabyte of garbage behind it (file buffers, decoded images, the parsers'
    // scratch arrays), much of it in large objects, which the collector does not compact on its own and is in no
    // hurry to collect at all: without this the managed heap in play was two to four times what the level keeps.
    // One blocking, compacting collection while the loading screen still covers the pause.
    // VORTEX_LEGACY_LOADGC: 0 none, 1 blocking and compacting, 2 a background collection, 3 (default) blocking,
    // compacting and "aggressive", which is the one that also hands the emptied heap back to the operating
    // system - measured in play on stormkeep: heap committed 2179 MB with none, 1529 with 1, 1581 with 2, 785 with 3,
    // for a pause of half a second (0.48 s for 1, 0.56 s for 3).
    private static readonly int s_loadCollect = int.TryParse(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_LOADGC"), out int loadCollect) ? loadCollect : 3;

    private static void CollectAfterLoad()
    {
        if (s_loadCollect <= 0) return;
        long began = LegacyPerfLog.Stamp();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long before = GC.GetTotalMemory(false);
        if (s_loadCollect == 2) GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        else if (s_loadCollect == 3)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        else
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        LegacyPerfLog.Event("collection after the load", began);
        Log(string.Create(CultureInfo.InvariantCulture, $"collection after the load: managed {before / (1024 * 1024)} MB -> {GC.GetTotalMemory(false) / (1024 * 1024)} MB in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms"));
    }

    // Developer aid, with VORTEX_LEGACY_PERFLOG: VORTEX_LEGACY_QCPROFILE times every builtin the client program
    // calls while in the game and writes the totals into the perf log when the session ends. It costs two
    // timestamps per builtin call, so a run with it is for finding where the time goes, not for frame times.
    private static readonly bool s_profileProgram = LegacyPerfLog.Enabled && !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_QCPROFILE"));

    private void DumpProgramProfile(LegacyClientSession session)
    {
        if (session.Host?.Vm is not { Profile: { } profile } vm) return;
        long frames = Math.Max(1, session.FramesDrawn);
        Dictionary<int, string> names = new();
        foreach (QcFunction function in vm.Functions)
            if (function.IsBuiltin) names.TryAdd(-function.FirstStatement, function.Name);
        LegacyPerfLog.Mark(string.Create(CultureInfo.InvariantCulture,
            $"qcprofile total {QcProfile.ToMilliseconds(profile.TotalTicks) / frames:0.000} ms/frame = interpreter {QcProfile.ToMilliseconds(profile.InterpreterTicks) / frames:0.000} + builtins {QcProfile.ToMilliseconds(profile.BuiltinTicks) / frames:0.000} ({profile.BuiltinCalls / frames} calls/frame) over {frames} frames"));
        if (_presentation?.SceneProfile is { } sceneTicks)
            LegacyPerfLog.Mark(string.Create(CultureInfo.InvariantCulture,
                $"qcprofile scene submission ms/frame: engine entities {QcProfile.ToMilliseconds(sceneTicks[0]) / frames:0.0000}, placement {QcProfile.ToMilliseconds(sceneTicks[1]) / frames:0.0000}, proxy {QcProfile.ToMilliseconds(sceneTicks[2]) / frames:0.0000}, transform {QcProfile.ToMilliseconds(sceneTicks[3]) / frames:0.0000}, render state {QcProfile.ToMilliseconds(sceneTicks[4]) / frames:0.0000}, tint {QcProfile.ToMilliseconds(sceneTicks[5]) / frames:0.0000}, pose {QcProfile.ToMilliseconds(sceneTicks[6]) / frames:0.0000}"));
        int listed = 0;
        foreach ((int number, long calls, long ticks) in profile.ByBuiltin())
        {
            if (listed++ >= 40) break;
            LegacyPerfLog.Mark(string.Create(CultureInfo.InvariantCulture,
                $"qcprofile #{number} {names.GetValueOrDefault(number, "?")}: {QcProfile.ToMilliseconds(ticks) / frames:0.0000} ms/frame; {(double)calls / frames:0.0} calls/frame; {QcProfile.ToMilliseconds(ticks) * 1000 / Math.Max(1, calls):0.00} us/call"));
        }
    }

    private void Fail(string reason)
    {
        if (_failed) return;
        _failed = true;
        GD.PrintErr("[legacy] connection failed: " + reason);
        LegacyLog.Write("CONNECTION FAILED: " + reason);
        LegacyLog.Flush();
        // Developer aid (VORTEX_LEGACY_SHOTS): the window two seconds later, when the menu shows the reason.
        if (!string.IsNullOrEmpty(s_shotDirectory) && !Headless && IsInsideTree() && GetTree() is { } tree)
        {
            string shot = System.IO.Path.Combine(s_shotDirectory!, "failed.png");
            tree.CreateTimer(2.0, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout += () =>
            {
                Image? image = tree.Root.GetTexture()?.GetImage();
                if (image is null) return;
                System.IO.Directory.CreateDirectory(s_shotDirectory!);
                System.Threading.Tasks.Task.Run(() => image.SavePng(shot));
            };
        }
        Action<string>? callback = ConnectionFailed;
        // Deferred: this can be reached from inside the session's own call stack, and the shell tears the node down.
        Callable.From(() => callback?.Invoke(reason)).CallDeferred();
    }

    // =====================================================================================================
    //  Input
    // =====================================================================================================

    private bool GameHasKeyFocus => _inGame && !ConsoleState.IsOpen && UiHasFocus?.Invoke() != true;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_shutDown || _failed || _session is not { } session) return;
        if (@event is InputEventMouseMotion motion)
        {
            _mouseDelta += motion.Relative;
            _mousePosition = motion.Position;
            return;
        }
        // On the menu's console keys do not come from here: the menu's key dispatch has them first (it is an
        // earlier input stage) and hands the game's to ProgramInputEvent and to Xonotic's own bindings.
        if (!GameHasKeyFocus || _shared is not null) return;

        // Key_Event, key_game: "csqc has priority" - the program is offered the key before its bind runs.
        int key = -1, character = 0;
        bool down = false;
        switch (@event)
        {
            case InputEventKey { Echo: false } k:
                key = LegacyKeyMap.KeyNumber(k);
                character = LegacyKeyMap.Character(k);
                down = k.Pressed;
                break;
            case InputEventMouseButton m:
                key = LegacyKeyMap.MouseButtonNumber(m.ButtonIndex);
                down = m.Pressed;
                break;
            default:
                return;
        }
        if (key >= 0 && session.Host is { Initialized: true } host && host.InputEvent(down ? 0 : 1, key, character))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        // The player's own binds, read-only: a held button goes into the bind table's state (sampled below),
        // anything else is a command for the session's console.
        BindInput.HandleEvent(@event, RunBoundCommand);
    }

    private void RunBoundCommand(string command)
    {
        if (_session is null || command.Length is 0 or > 1024) return;
        _session.Console.AddText(command + "\n");
    }

    private LegacyInput SampleInput(LegacyClientSession session)
    {
        CsqcClientState state = session.State;
        bool focus = GameHasKeyFocus;
        state.GameHasKeyFocus = focus;
        Vector2 delta = _mouseDelta;
        _mouseDelta = Vector2.Zero;
        if (!_inGame || _cvars is not { } cvars) return default;

        // +showscores is a command the program registers, not an engine button.
        bool showScores = focus && _shared is null && BindTable.ShowScores;
        if (showScores != _showScores)
        {
            _showScores = showScores;
            session.Console.AddText(showScores ? "+showscores\n" : "-showscores\n");
        }

        LegacyHeldButtons held = _scriptHeld;
        if (focus && _shared is not null)
        {
            // Xonotic's own bindings: every held button arrived as a "+command" through the console
            // (the IN_*Down commands registered above) and is already in _scriptHeld.
        }
        else if (focus)
        {
            // The bind table folds +forward/+back into one axis, and so on; opposite keys cancel either way.
            held.Forward |= BindTable.Forward > 0;
            held.Back |= BindTable.Forward < 0;
            held.MoveRight |= BindTable.Side > 0;
            held.MoveLeft |= BindTable.Side < 0;
            held.Attack |= BindTable.AttackHeld;
            held.Jump |= BindTable.JumpHeld;
            held.Button3 |= BindTable.Attack2Held;     // +fire2
            held.Button4 |= BindTable.ZoomHeld;        // +zoom
            held.Button5 |= BindTable.CrouchHeld;      // +crouch
            held.Button6 |= BindTable.HookHeld;        // +hook
            held.Use |= BindTable.UseHeld;
        }
        else held = default;

        // --- the mouse: the program's cursor, or the view ---
        if (session.Host is { Initialized: true } host)
        {
            if (state.WantsMouseMove)
            {
                if (_mousePosition != _reportedMousePosition && _viewSize.X > 0 && _viewSize.Y > 0)
                {
                    _reportedMousePosition = _mousePosition;
                    float x = _mousePosition.X * cvars.GetFloat("vid_conwidth") / _viewSize.X, y = _mousePosition.Y * cvars.GetFloat("vid_conheight") / _viewSize.Y;
                    state.MousePosition = new QcVector(x, y, 0);
                    host.InputEvent(3, x, y);
                }
            }
            else if (focus && delta != Vector2.Zero)
            {
                host.InputEvent(2, delta.X, delta.Y);
                float zoom = state.Stats[CsqcClientState.StatViewZoom] > 0 ? state.Stats[CsqcClientState.StatViewZoom] / 255f : 1;
                state.ViewAngles = LegacyInputMath.MouseLook(state.ViewAngles, delta.X, delta.Y,
                    Positive(cvars, "sensitivity", 3), float.IsFinite(state.SensitivityScale) ? state.SensitivityScale : 1, zoom,
                    Number(cvars, "m_yaw", 0.022f), Number(cvars, "m_pitch", 0.022f), Number(cvars, "in_pitch_min", -90), Number(cvars, "in_pitch_max", 90));
            }
        }

        LegacyMoveSpeeds speeds = new()
        {
            Forward = Positive(cvars, "cl_forwardspeed", 400), Back = Positive(cvars, "cl_backspeed", 400), Side = Positive(cvars, "cl_sidespeed", 350),
            Up = Positive(cvars, "cl_upspeed", 400), SpeedKey = Positive(cvars, "cl_movespeedkey", 2),
        };
        LegacyInputMath.Move(held, speeds, out float forward, out float side, out float up);
        LegacyInput input = new()
        {
            ForwardMove = forward, SideMove = side, UpMove = up, Buttons = LegacyInputMath.Buttons(held, focus), Impulse = _pendingImpulse,
        };
        _pendingImpulse = 0;
        return input;

        static float Number(CvarService cvars, string name, float fallback) =>
            cvars.Has(name) && float.IsFinite(cvars.GetFloat(name)) ? cvars.GetFloat(name) : fallback;
        static float Positive(CvarService cvars, string name, float fallback) =>
            cvars.Has(name) && cvars.GetFloat(name) > 0 && float.IsFinite(cvars.GetFloat(name)) ? cvars.GetFloat(name) : fallback;
    }

    // The pointer is the game's for mouse look, unless the program asked for a cursor (setcursormode) or a
    // menu or the console is in front. MouseCapture also releases it while the window is not focused.
    private void UpdateCursor(LegacyClientSession session)
    {
        if (Headless) return;
        MouseCapture.SetWantCapture(GameHasKeyFocus && !session.State.WantsMouseMove);
    }

    /// <summary>
    /// Key_GetBind for the program's getkeybind / findkeysforcommand: the player's own bind for a
    /// DarkPlaces key number, read from the bind table. The table is copied once a second; the program
    /// asks about every key number there is, several times a frame.
    /// </summary>
    // Key_FindKeysForCommand over the same table KeyBinding answers from: the keys bound to exactly this command,
    // lowest numbers first.
    private void FindKeysForCommand(string command, Span<int> keys, int bindMap)
    {
        if (_shared is not null)
        {
            _shared.Keys.FindKeysForCommand(command, keys, bindMap);
            return;
        }
        KeyBinding(0, bindMap);   // refreshes the snapshot when it is due
        _foundKeys.Clear();
        foreach ((int number, string bound) in _bindSnapshot)
            if ((uint)number < VortexArena.Legacy.Csqc.CsqcKeys.MaxKeys && string.Equals(bound, command, StringComparison.Ordinal)) _foundKeys.Add(number);
        _foundKeys.Sort();
        for (int i = 0; i < keys.Length && i < _foundKeys.Count; i++) keys[i] = _foundKeys[i];
    }
    private readonly List<int> _foundKeys = new();

    private string? KeyBinding(int key, int bindMap)
    {
        // On the menu's console the bindings are Xonotic's own table, which is DarkPlaces' in every respect.
        if (_shared is not null) return _shared.Keys.GetBind(key, bindMap);
        double now = Now;
        if (now - _bindSnapshotAt > 1 || _bindSnapshotAt < 0)
        {
            _bindSnapshotAt = now;
            _bindSnapshot.Clear();
            foreach (int number in LegacyKeyMap.BindableKeyNumbers())
                if (LegacyKeyMap.BindTableKey(number) is { } name && BindTable.Get(name) is { Length: > 0 } command)
                    _bindSnapshot[number] = command;
        }
        return _bindSnapshot.GetValueOrDefault(key);
    }

    // =====================================================================================================
    //  Log
    // =====================================================================================================

    private void OnPrint(string text)
    {
        foreach (char c in text)
        {
            if (c != '\n')
            {
                if (_printLine.Length < 512) _printLine.Append(c);
                continue;
            }
            string line = _printLine.ToString();
            _printLine.Clear();
            // Con_MaskPrint: a line that begins with byte 1, 2 or 3 is chat (1 also plays the talk sound).
            if (line.Length > 1 && line[0] is '\u0001' or '\u0002' or '\u0003')
            {
                if (_chatLines.Count >= 64) _chatLines.RemoveAt(0);
                _chatLines.Add((Now, line[1..]));
                line = line[1..];
            }
            PostConsoleLine(ConsolePrint, line);
            if (Headless && _printsLogged++ < MaxLoggedPrints) Log("print: " + Printable(line, 300));
            else LegacyLog.Write("print: " + Printable(line, 300));
        }
    }

    private static string Printable(string text, int limit = 200)
    {
        StringBuilder result = new(Math.Min(text.Length, limit));
        foreach (char c in text)
        {
            if (result.Length >= limit) { result.Append("..."); break; }
            result.Append(c == '\n' ? ' ' : c < ' ' ? '?' : c);
        }
        return result.ToString();
    }

    // The once-a-second line: what a headless run can show in place of a picture.
    private void Status(LegacyClientSession session, GodotLegacyPresentation presentation, double now)
    {
        // One line per second of wall clock; a frame that took several seconds (loading) skips the lines it missed.
        _nextStatus = Math.Max(_nextStatus + 1, now + 0.5);
        if (!_inGame) return;
        NoteUnimplementedBuiltins(session, final: false);
        // To the output when asked for (always in a headless run); otherwise every tenth second to the log file only.
        bool toOutput = Headless || (PlayerCvars is { } player && player.GetFloat(LegacyData.StatusCvar) != 0);
        if (!toOutput && (_statusLines++ % 10) != 0) return;
        CsqcHost? host = session.Host;
        if (toOutput && _server is { } server && Interlocked.CompareExchange(ref _statusBusy, 1, 0) == 0)
        {
            LegacyLocalServerStats stats = server.TakeStats();
            int frames = _frameMeter.CopyTo(_statusFrames), client = _clientMeter.CopyTo(_statusClient);
            double frameMean = _frameMeter.Mean, frameMax = _frameMeter.Max, clientMean = _clientMeter.Mean, clientMax = _clientMeter.Max, at = now - _inGameAt;
            string threaded = server.Threaded ? "(own thread)" : "(main thread)", map = server.Map, gameType = server.GameType;
            long players = server.ActiveClients, faults = server.Faults;
            long sent = _transport?.Sent ?? 0, received = _transport?.Received ?? 0;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    double p50 = Meter.PercentileOf(_statusFrames, frames, 0.5, sort: true), p99 = Meter.PercentileOf(_statusFrames, frames, 0.99, sort: false);
                    double clientP99 = Meter.PercentileOf(_statusClient, client, 0.99, sort: true);
                    long workingSet;
                    using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess()) workingSet = process.WorkingSet64;
                    Log(string.Create(CultureInfo.InvariantCulture,
                        $"t+{at:0}: timing: frame mean {frameMean:0.00} ms p50 {p50:0.00} p99 {p99:0.00} max {frameMax:0.0}, " +
                        $"legacy (client) mean {clientMean:0.00} ms p99 {clientP99:0.00} max {clientMax:0.0}, " +
                        $"legacy-server {threaded} mean {stats.MeanFrameMilliseconds:0.00} ms/frame max {stats.LongestFrameMilliseconds:0.0}, {stats.TicksPerSecond:0.0} ticks/s, " +
                        $"players {players}, server faults {faults}, map {map} ({gameType}), loopback sent {sent} received {received}, " +
                        $"working set {workingSet / (1024 * 1024)} MB"));
                }
                finally { Volatile.Write(ref _statusBusy, 0); }
            });
        }
        string bankText = _textureBank is { } textureBank
            ? string.Create(CultureInfo.InvariantCulture, $"texture bank: {textureBank.Pending} to compress, {textureBank.Encoded} compressed, {textureBank.Swapped} swapped in ({textureBank.SavedBytes / (1024 * 1024)} MB given back), ")
            : "";
        string second = string.Create(CultureInfo.InvariantCulture,
            $"t+{now - _inGameAt:0}: signon {session.State.Signon}, entity frames {session.EntityFrames}, csqc frames {session.FramesDrawn} ({session.FramesFaulted} faulted), " +
            $"faults {host?.FaultCount ?? 0}, desyncs {host?.DesyncCount ?? 0}, undecoded {session.MessagesNotDecoded}, " +
            $"scene entities {presentation.LastSceneEntities} (submodels {presentation.SubmodelSubmissions} total, {presentation.SubmodelsBuilt} built in {presentation.SubmodelBuildSeconds:0.00} s), proxy nodes {presentation.ProxyNodes}, " +
            $"2d commands {presentation.LastDrawCommands} (dropped {presentation.DrawCommandsDropped}), sounds started {presentation.SoundsStarted}, " +
            $"effects spawned {presentation.EffectsSpawned} (unknown {presentation.EffectsUnknown}, not drawn {presentation.TempEntitiesNotDrawn + presentation.SpriteEffectsNotDrawn}), " +
            $"dynamic lights {presentation.LastDynamicLights}, extra views skipped {presentation.ExtraViewsSkipped}, polygons {presentation.PolygonsDrawn}, " +
            $"nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0}, objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):0}, " +
            $"managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB, native {OS.GetStaticMemoryUsage() / (1024 * 1024)} MB, " +
            $"{bankText}" +
            $"view '{presentation.View.Origin.X:0.0} {presentation.View.Origin.Y:0.0} {presentation.View.Origin.Z:0.0}' fovy {presentation.View.VerticalFovDegrees:0.0}");
        if (toOutput) System.Threading.Tasks.Task.Run(() => Log(second));
        else LegacyLog.Write(second);
    }
    private int _statusLines;

    // A server's own client program (a mod's) may call an engine builtin this client does not have. DarkPlaces
    // would stop the program; here the call is answered with nothing and counted, and the log names it - once
    // when a new one turns up, and with the totals when the session ends.
    private int _unimplementedSeen;

    private void NoteUnimplementedBuiltins(LegacyClientSession session, bool final)
    {
        if (session.Host is not { } host || host.UnimplementedBuiltins.Count == 0) return;
        if (!final && host.UnimplementedBuiltins.Count == _unimplementedSeen) return;
        _unimplementedSeen = host.UnimplementedBuiltins.Count;
        StringBuilder text = new(final ? "the server's client program called engine builtins this client does not implement (each call was answered with nothing): "
            : "WARNING: the server's client program calls engine builtins this client does not implement (answered with nothing; something may look or behave differently): ");
        int listed = 0;
        foreach (KeyValuePair<(int Number, string Name), long> entry in host.UnimplementedBuiltins)
        {
            if (listed++ >= 24) { text.Append("..."); break; }
            text.Append(CultureInfo.InvariantCulture, $"#{entry.Key.Number} {Printable(entry.Key.Name, 40)} x{entry.Value}; ");
        }
        Log(text.ToString());
    }

    // =====================================================================================================
    //  End
    // =====================================================================================================

    /// <summary>
    /// CL_Disconnect: say goodbye, shut the program down, close the socket, stop every sound. Synchronous
    /// and safe to call twice; the shell calls it before freeing the node so the port is released at once.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _shared?.EnterSession();   // CSQC_Shutdown may set cvars: still the session's doing
        try
        {
            if (_session is { } session)
            {
                DumpProgramProfile(session);   // before the goodbye: that unloads the program
                NoteUnimplementedBuiltins(session, final: true);
                if (_transport is { } transport && session.Client.State == DpClientState.Connected)
                {
                    foreach (byte[] datagram in session.Disconnect(Now)) transport.Send(datagram);
                    Log("sent the disconnect");
                }
                Log($"session over: in the game {(_inGameAt >= 0 ? Now - _inGameAt : 0).ToString("0.0", CultureInfo.InvariantCulture)} s, entity frames {session.EntityFrames}, " +
                    $"csqc frames {session.FramesDrawn} ({session.FramesFaulted} faulted), undecoded messages {session.MessagesNotDecoded}, " +
                    $"datagrams sent {_transport?.Sent ?? 0} received {_transport?.Received ?? 0} over {_transport?.Peer ?? "nothing"}; " +
                    string.Create(CultureInfo.InvariantCulture, $"frame mean {_frameMeter.Mean:0.00} ms p99 {_frameMeter.Percentile(0.99):0.00} max {_frameMeter.Max:0.0}, legacy (client) mean {_clientMeter.Mean:0.00} ms p99 {_clientMeter.Percentile(0.99):0.00}"));
                session.Dispose();
            }
        }
        finally
        {
            if (_shared is not null) _shared.Cvars.Changed -= OnSharedCvarChanged;
            if (PlayerCvars is { } nativeCvars) nativeCvars.Changed -= OnNativeCvarTyped;
            if (_server is { } server)
            {
                // SV_Shutdown: every other client is told, the program's shutdown hook runs, a UDP socket is
                // closed - on the server's own thread, which this waits for (not for a server still starting).
                double began = Now;
                LegacyLocalServerState was = server.State;
                server.Dispose();
                _server = null;
                Log(string.Create(CultureInfo.InvariantCulture,
                    $"local server shut down in {Now - began:0.00} s (it was {was}; {server.LevelsStarted} levels, {server.Faults} program faults, now {server.State})"));
            }
            _transport?.Dispose();
            _transport = null;
            _presentation?.Shutdown();
            if (_assetLoader is { } loader) loader.Assets.DeferCompression = null;
            _textureBank?.Dispose();
            _textureBank = null;
            if (_menuWarmer is { } warmer && GodotObject.IsInstanceValid(warmer)) warmer.SetProcess(true);
            _menuWarmer = null;
            _session = null;
            // On the menu's console the cvar store outlives the session: unless the program's host lets go of
            // it, the store holds the host, the host this node, and this node the whole game that was played.
            _services?.Detach();
            _services = null;
            _commands.Release();
            if (_shared is { } shared)
            {
                // The console is the menu's and outlives the session: put back what the session changed, and
                // leave the game data mounted.
                int restored = shared.EndSession();
                Log($"session over on the menu's console: {restored} cvars put back to the player's values");
                Menu?.DetachSession(this);
                _shared = null;
            }
            else _vfs?.Dispose();
            _vfs = null;
            if (!Headless) MouseCapture.SetWantCapture(false);
            ReleaseLevelMemory(Godot.Engine.GetMainLoop() as SceneTree);
            LegacyPerfLog.Mark("session over");
            LegacyPerfLog.Flush();
            LegacyLog.Flush();
        }
    }

    public override void _ExitTree() => Shutdown();

    // A level is several hundred megabytes: the parsed map and the programs as managed arrays, and textures,
    // meshes and sounds on Godot's side, each of which stays allocated for as long as a managed wrapper of it
    // exists - until the collector has RUN that wrapper's finalizer, not merely until nothing refers to it. So
    // the collector is asked now (the server's and the program's arrays), and once more a little later, when
    // the session's nodes have really been freed (QueueFree waits for the end of the frame) and their wrappers
    // can go: collect, let the finalizers hand the resources back to Godot, collect what those were holding.
    // It costs one pause of some tens of milliseconds while the screen is changing anyway. Static, and given
    // the tree rather than this node: nothing here may keep the session alive.
    private static void ReleaseLevelMemory(SceneTree? tree)
    {
        long before = GC.GetTotalMemory(false);
        GC.Collect();
        Log($"memory when the session ended: managed {before / (1024 * 1024)} MB -> {GC.GetTotalMemory(false) / (1024 * 1024)} MB; {LegacyData.MemoryReport()}");
        if (tree is null) return;
        int passes = 0;
        void Pass()
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            // Twice: what the first pass's finalizers released on Godot's side frees further wrappers. The last
            // collection is an aggressive one: it also gives the emptied heap back to the operating system
            // (several hundred megabytes of a level's arrays would otherwise stay committed, if unused).
            if (++passes < 2)
            {
                GC.Collect();
                tree.CreateTimer(0.5, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout += Pass;
                return;
            }
            // (The large object heap is compacted only when asked: without this line a level's freed arrays left
            // it a third of a gigabyte of holes that stayed committed at the menu.)
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Log(string.Create(CultureInfo.InvariantCulture,
                $"memory after the level was released (the last pass took {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms): {LegacyData.MemoryReport()}"));
        }
        tree.CreateTimer(0.25, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout += Pass;
    }
}

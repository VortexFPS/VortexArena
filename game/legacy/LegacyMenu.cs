// Port of Base/darkplaces/menu.c MR_Init / MR_Restart / MR_SetRouting as far as "use menu.dat" goes;
// cl_screen.c SCR_DrawScreen's MR_Draw (after the HUD, every frame) and the
// scr_menuforcewhiledisconnected rule of CL_UpdateScreen; keys.c Key_Event's callers (which window
// events become which key numbers: vid_sdl.c Sys_SendKeyEvents); host.c Host_Quit_f / Host_SaveConfig
// at shutdown; cl_main.c CL_Connect_f / CL_Disconnect_f as console commands; and the stand-ins for the
// engine commands a menu issues that this client has no engine for (vid_restart, r_restart, map).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Console;
using VortexArena.Game.Loaders;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Menu;
using VortexArena.Legacy.Presentation;
using VortexArena.QuakeC;

namespace VortexArena.Game.Legacy;

/// <summary>
/// Xonotic's own menu - main menu, server browser, settings, the in-game menu and the dialogs the client
/// program asks for - running as the QuakeC program it is (menu.dat on a third VM, <see cref="MenuHost"/>),
/// with this node standing in for the DarkPlaces engine around it: the window, the keys and the pointer,
/// the sound, the server list's socket, and the console commands that act outside the menu.
///
/// It exists only when asked for (<c>--legacy-menu</c>, or the <c>legacy_menu</c> console command); the
/// native Vortex front end is otherwise untouched. While it exists it owns the Xonotic console
/// (<see cref="LegacyConsole"/>): Xonotic's configuration as the player has it, kept under the legacy
/// user folder and nowhere near the player's Vortex configuration. A legacy session started from this
/// menu runs on that same console, as the three programs of a DarkPlaces client do - see
/// <see cref="LegacyConsole"/> for how a server is nevertheless kept out of what gets saved.
///
/// WHAT THE MENU CAN AND CANNOT REACH. menu.dat comes with the player's own game data, so it is trusted
/// with the player's Xonotic settings. What it displays is not trusted - server names and player lists
/// come from the internet. The address a "connect" goes to is one this client built from a datagram's
/// source; nothing a server sent is ever run as a command. Pictures, fonts and sounds are read from the
/// mounted game data by relative path; files are written only under the legacy user folder.
/// </summary>
public partial class LegacyMenu : Node
{
    /// <summary>What "start a local game" paths say when nothing is there to start one (no owner filled <see cref="StartLocalGame"/>).</summary>
    public const string LocalGameNotBuilt =
        "A local Xonotic game could not be started from here: nothing is wired to host the server program.";

    // ---- set by the shell before the node enters the tree ----------------------------------------------

    /// <summary>The Xonotic data directory (<see cref="LegacyData.TryResolve"/>).</summary>
    public string DataDirectory { get; set; } = "";
    /// <summary>Lines for the developer console.</summary>
    public Action<string>? ConsolePrint { get; set; }
    /// <summary>The "connect &lt;address&gt;" command, with a checked host[:port].</summary>
    public Action<string>? ConnectRequested { get; set; }
    /// <summary>The "disconnect" command.</summary>
    public Action? DisconnectRequested { get; set; }
    /// <summary>The "quit" command. The configuration has been saved when this is called.</summary>
    public Action? QuitRequested { get; set; }
    /// <summary>The menu could not be started, or its program stopped with an error: a sentence for the player.
    /// The owner removes this node and shows its own menu.</summary>
    public Action<string>? Failed { get; set; }
    /// <summary>"toggleconsole".</summary>
    public Action? ToggleConsole { get; set; }
    /// <summary>
    /// A level-changing command of a listen server's console ("map &lt;name&gt;" from the campaign, Instant
    /// action or the Create dialog; "changelevel", "restart", "maps", "load"), as the whole console line.
    /// The owner starts or steers the local game (<see cref="LegacyLocalGames.HandleMapCommand"/>); true
    /// means the line was dealt with. Null, or false: the player is told nothing could be done with it.
    /// </summary>
    public Func<string, bool>? StartLocalGame { get; set; }

    // ---- the engine around the program -----------------------------------------------------------------

    private VirtualFileSystem? _vfs;
    private AssetLoader? _assets;
    private LegacyConsole? _console;
    private LegacyCanvas? _canvas;
    private LegacyDrawLayer? _drawLayer;
    private CanvasLayer? _canvasLayer;
    private MenuHost? _host;
    private MenuHostCache? _serverList;
    private LegacyServerListLink? _serverLink;
    private LegacyMenuSound? _sound;
    private LegacyKeyEvents? _keys;
    private LegacyGame? _session;
    private LegacyCanvas? _sessionCanvas;
    private readonly List<string[]> _fontCommands = new();
    private readonly HashSet<string> _unknownLogged = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _program;
    private bool _started, _shutDown, _failed;
    private int _restarts;
    private int _framesDisconnected;
    private Vector2 _viewSize, _mouse, _mouseDelta;
    private double _startedAt, _lastFrameAt;
    private string _notice = "";
    private double _noticeUntil;
    private string _lastAddress = "";
    private long _frames, _menuKeys;
    private Image? _blankCursorImage;
    private bool _cursorHidden;

    /// <summary>The Xonotic console: cvars, commands, key bindings. Null until the node is ready.</summary>
    public LegacyConsole? Console => _console;
    public VirtualFileSystem? Files => _vfs;
    public AssetLoader? Assets => _assets;
    public MenuHost? Host => _host;
    /// <summary>True while the menu has the keyboard (key_dest is key_menu or key_menu_grabbed): the game has neither keys nor mouse.</summary>
    public bool HasKeys => _host is { } host && host.KeyDest is MenuKeyDest.Menu or MenuKeyDest.MenuGrabbed;
    /// <summary>The session running on this menu's console, if any.</summary>
    public LegacyGame? Session => _session;

    private static double Now => Time.GetTicksUsec() / 1_000_000.0;
    private static bool Headless => DisplayServer.GetName() == "headless";

    private static void Log(string line) => GD.Print("[legacy-menu] " + line);

    // =====================================================================================================
    //  Start
    // =====================================================================================================

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _startedAt = _lastFrameAt = Now;
        try
        {
            Start();
        }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or ArgumentException or MenuLoadException)
        {
            Fail("The Xonotic menu could not be started: " + e.Message);
        }
    }

    private void Start()
    {
        _vfs = new VirtualFileSystem();
        if (!System.IO.Directory.Exists(DataDirectory) || !_vfs.MountGameDir(DataDirectory))
        {
            Fail($"Nothing could be mounted from the Xonotic data folder \"{DataDirectory}\". " + LegacyData.SetupHint);
            return;
        }
        _assets = new AssetLoader(_vfs);
        LegacyData.MountTextureCache(_vfs, _assets.Assets);

        // --- the canvas: above a session's HUD (5) and the mod layer (6), below the native menu (10) and the console ---
        _canvasLayer = new CanvasLayer { Name = "LegacyMenuLayer", Layer = 8 };
        AddChild(_canvasLayer);
        _drawLayer = new LegacyDrawLayer { Name = "LegacyMenuDraw" };
        _canvasLayer.AddChild(_drawLayer);
        _canvas = new LegacyCanvas(_vfs, _assets);

        // --- the console: DarkPlaces' engine cvars, Xonotic's defaults, then the player's own config.cfg ---
        _console = new LegacyConsole(_vfs, LegacyData.WriteRoot, OnPrint);
        _sound = new LegacyMenuSound(this, _vfs, _assets, _console.Cvars, Log);
        RegisterEngineCommands(_console);
        _console.Interpreter.UnknownCommandHandler = OnUnknownCommand;
        if (!_console.LoadConfig())
        {
            Fail($"The Xonotic data folder \"{DataDirectory}\" has no default.cfg, so it is not Xonotic's game data. " + LegacyData.SetupHint);
            return;
        }
        UpdateViewSize(force: true);
        // vid_fullscreen as the window IS, not as the configuration asks: the window is the native game's
        // to manage, and the Video settings show this cvar as their "Full screen" box.
        if (!Headless)
            _console.Cvars.Set("vid_fullscreen", DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen ? "1" : "0");
        Log($"data {DataDirectory}: {_vfs.MountedPaths.Count} packages mounted, {_console.EngineCvarCount} engine cvars, default.cfg and config.cfg executed " +
            $"({_console.Interpreter.FilesExecuted} files, {_console.Interpreter.AliasesDefined} aliases, {CountBinds()} keys bound); the configuration is saved to " +
            System.IO.Path.Combine(LegacyData.WriteRoot, LegacyConsole.ConfigFileName));

        // --- the server list and its socket ---
        _serverList = new MenuHostCache(_console.Cvars) { GameName = "Xonotic" };
        try
        {
            _serverLink = new LegacyServerListLink(_serverList, Log);
            _serverList.Queries = _serverLink;
        }
        catch (System.Net.Sockets.SocketException e)
        {
            Log("no socket for the server list (" + e.Message + "): the browser will stay empty");
        }

        // --- keys ---
        _keys = new LegacyKeyEvents(_console.Keys)
        {
            KeyDest = () => Hud.ChatPrompt.IsOpen ? MenuKeyDest.Message : _host?.KeyDest ?? MenuKeyDest.Game,
            ConsoleActive = () => ConsoleState.IsOpen,
            MenuKey = (key, ascii, down) =>
            {
                _menuKeys++;
                _host?.KeyEvent(key, ascii, down);
            },
            ToggleMenu = mode => _host?.ToggleMenu(mode),
            GameInput = (type, key, ascii) => _session?.ProgramInputEvent(type, key, ascii) ?? false,
            ToggleConsole = () => ToggleConsole?.Invoke(),
            InsertText = text => _console.InsertText(text),
            AddText = text => _console.AddText(text),
        };

        // --- the program ---
        _program = LegacyQcHost.IsSafePath("menu.dat") && _vfs.Exists("menu.dat") ? _vfs.ReadBytes("menu.dat") : null;
        if (_program is null)
        {
            Fail($"The Xonotic data folder \"{DataDirectory}\" has no menu.dat. " + LegacyData.SetupHint);
            return;
        }
        if (!LoadProgram()) return;
        // host.c Host_Init, its last act: "togglemenu 1". Queued, so it runs after the menu_restart that
        // Xonotic's m_init asks for on a first start (it sets the language and wants to be loaded again).
        _console.AddText("togglemenu 1\n");
        _started = true;
    }

    private int CountBinds()
    {
        int count = 0;
        if (_console is null) return 0;
        for (int map = 0; map < MenuKeyBindings.MaxBindMaps; map++)
            foreach (KeyValuePair<int, string> _ in _console.Keys.Bindings(map)) count++;
        return count;
    }

    // MP_Init: load menu.dat and run m_init.
    private bool LoadProgram()
    {
        if (_program is null || _console is null || _canvas is null || _serverList is null) return false;
        MenuHostOptions options = new()
        {
            Connected = () => _session is { } session && !session.Ended,
            // sv.active, for the menu's isserver() and its GAME_ISSERVER status bit: a local game is running.
            ServerActive = () => _session is { IsLocal: true, Ended: false },
            MaxClients = () => _session is { IsLocal: true, Ended: false } ? _session.LocalGame?.MaxPlayers ?? 1 : _console?.MaxPlayers ?? 8,
            ConsoleActive = () => ConsoleState.IsOpen,
            VideoSize = () => (_viewSize.X, _viewSize.Y),
            WindowMouse = () => (_mouse.X, _mouse.Y),
            MouseDelta = () => (_mouseDelta.X, _mouseDelta.Y),
            DesktopMode = DesktopMode,
            Sound = _sound,
            ServerList = _serverList,
            CenterPrint = text => ConsolePrint?.Invoke(text),
            QcCommand = line => _session?.ProgramConsoleCommand(line) ?? false,
            GameDirs = new[] { ("data", "Xonotic") },
            // MR_Restart_f runs from the command buffer, between frames - never from inside the program.
            Restart = Restart,
        };
        MenuHost host;
        try
        {
            host = new MenuHost(_program, _console, _canvas, options, OnPrint,
                text => { if (_console.Cvars.GetFloat("developer") != 0) Log("VM warning: " + Printable(text.TrimEnd(), 300)); });
        }
        catch (MenuLoadException e)
        {
            Fail("The Xonotic menu program could not be loaded: " + e.Message);
            return false;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ok = host.Init();
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        _host = host;
        Log($"menu.dat loaded ({_program.Length} bytes, {host.Program.Functions.Length} functions, {host.AutocvarsBound} autocvars); m_init {(ok ? "completed" : "FAULTED")} in {seconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
        if (!ok)
        {
            Fail("The Xonotic menu program stopped with an error while starting. (" + Printable(host.Faults[0].Message, 300) + ")");
            return false;
        }
        return true;
    }

    // MR_Restart: m_shutdown, then MP_Init again.
    private void Restart()
    {
        if (_host is { } old)
        {
            old.Shutdown();
            _host = null;
        }
        _keys?.ReleaseAll();
        _framesDisconnected = 0;
        _restarts++;
        LoadProgram();
    }

    private static MenuVideoMode DesktopMode()
    {
        if (Headless) return new MenuVideoMode(1280, 720, 1);
        Vector2I size = DisplayServer.ScreenGetSize();
        return new MenuVideoMode(size.X, size.Y, 1);
    }

    private void Fail(string reason)
    {
        if (_failed) return;
        _failed = true;
        GD.PrintErr("[legacy-menu] " + reason);
        Action<string>? callback = Failed;
        Callable.From(() => callback?.Invoke(reason)).CallDeferred();
    }

    // =====================================================================================================
    //  The console commands that act outside the program
    // =====================================================================================================

    private void RegisterEngineCommands(LegacyConsole console)
    {
        Common.Config.ConfigInterpreter interpreter = console.Interpreter;

        interpreter.RegisterCommand("loadfont", argv =>
        {
            if (argv.Count < 2 || argv.Count > 32) return;
            string[] copy = new string[argv.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = argv[i];
            // Remembered: a session's HUD canvas is made later and needs the same slots.
            _fontCommands.RemoveAll(c => c[1] == copy[1]);
            if (_fontCommands.Count < 64) _fontCommands.Add(copy);
            _canvas?.LoadFontCommand(copy);
            _sessionCanvas?.LoadFontCommand(copy);
        }, "loadfont slot face[,fallback...] [sizes...]");

        // CL_Connect_f. The address is checked here: it is the one thing a click in the server browser
        // turns into an action outside the menu.
        console.RegisterPlayerCommand("connect", argv =>
        {
            if (argv.Count < 2)
            {
                OnPrint("connect <serveraddress> : connect to a multiplayer game\n");
                return;
            }
            if (!LegacyGame.TryParseAddress(argv[1], out string host, out int port) || !IsHostName(host))
            {
                OnPrint($"connect: \"{Printable(argv[1], 80)}\" is not a server address\n");
                return;
            }
            _lastAddress = string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
            // A review script clicks where it expects a server to be listed, and the list is live: with
            // VORTEX_LEGACY_SCRIPT_HOST set, a click that landed on any other server connects nowhere.
            if (HasReviewScript && !string.IsNullOrEmpty(s_scriptHost) && !string.Equals(host, s_scriptHost, StringComparison.OrdinalIgnoreCase))
            {
                Log($"connect {_lastAddress}: NOT FOLLOWED - the review script may only join {s_scriptHost}");
                Notice($"The review script may only join {s_scriptHost}; {_lastAddress} was not joined.");
                return;
            }
            Log($"connect {_lastAddress}");
            ConnectRequested?.Invoke(_lastAddress);
        }, "connect to a server by IP address or hostname");
        console.RegisterPlayerCommand("reconnect", _ => { if (_lastAddress.Length != 0) ConnectRequested?.Invoke(_lastAddress); },
            "reconnect to the last server you were on, or resets a singleplayer game");
        interpreter.RegisterCommand("disconnect", _ => Callable.From(() => DisconnectRequested?.Invoke()).CallDeferred(),
            "disconnect from server (or disconnect all clients if running a server)");
        foreach (string name in new[] { "quit", "exit" })
            console.RegisterPlayerCommand(name, _ => Callable.From(Quit).CallDeferred(), "quit the game");

        // SV_Map_f and its relatives: DarkPlaces starts (or steers) its server here; the owner does, through
        // StartLocalGame. Deferred to the end of the frame, in order, for the sake of what the menu sends:
        // "disconnect; maxplayers 16; map x" in one go, where the disconnect is itself deferred - a game
        // started at once would be the one that disconnect then ends.
        foreach (string name in new[] { "map", "devmap", "changelevel", "restart", "load", "maps" })
        {
            string command = name;
            console.RegisterPlayerCommand(command, argv =>
            {
                string line = CommandLine(argv);
                Callable.From(() =>
                {
                    if (_shutDown || StartLocalGame?.Invoke(line) == true) return;
                    Log($"\"{Printable(line, 120)}\": not acted on ({LocalGameNotBuilt})");
                    Notice(LocalGameNotBuilt);
                }).CallDeferred();
            }, command switch
            {
                "map" or "devmap" => "start a new game on the specified map (kicks off all players)",
                "changelevel" => "continue the running local game on another level",
                "restart" => "restart the current level of the running local game",
                "maps" => "list the maps in the game data",
                _ => "load a saved game (not available in this client)",
            });
        }
        // What a listen server's console runs on the SERVER (sv_cmd, kick, status). With a local game it goes
        // to that game's server; on someone else's server DarkPlaces forwards the line as it stands; with
        // neither there is no server to run it.
        foreach (string name in VortexArena.Legacy.Local.LegacyLocalCommands.ServerCommands)
        {
            string command = name;
            console.RegisterPlayerCommand(command, argv =>
            {
                string line = CommandLine(argv);
                if (_session is { Ended: false } session)
                {
                    if (!session.ServerCommand(line)) session.SendToServer(line);
                }
                else OnPrint($"{command}: no server is running\n");
            }, "runs on the server of a local game");
        }
        foreach (string name in new[] { "playdemo", "timedemo", "startdemos", "demos", "record", "stop", "cl_capturevideo_start" })
        {
            string command = name;
            console.RegisterPlayerCommand(command, _ => Notice($"\"{command}\": demos cannot be played or recorded by this client yet."), "not available in this client");
        }

        // The renderer, the sound system and the window are Godot's: a restart of any of them has nothing
        // to do, and saying nothing is what success looks like for these commands.
        foreach (string name in new[] { "vid_restart", "r_restart", "snd_restart", "fs_rescan", "r_editlights_reload", "in_releaseall", "stopsound",
                     "stopvideo", "screenshot", "envmap", "sv_startdownload", "cl_particles_reload", "r_glsl_restart", "curl", "sendcvar", "prvm_edictset",
                     "scoreboard_columns_set", "crypto_keygen", "crypto_reload", "locs_reload" })
            interpreter.RegisterCommand(name, _ => { }, "accepted and ignored: this client has no such engine subsystem to restart");
        interpreter.RegisterCommand("toggleconsole", _ => Callable.From(() => ToggleConsole?.Invoke()).CallDeferred(), "opens or closes the console");

        interpreter.RegisterCommand("cd", argv => _sound?.CdCommand(argv), "execute a CD drive command (cd [reset|on|off|play|loop|stop|pause|resume] [track])");
        foreach (string name in new[] { "play", "play2", "playvol" })
            interpreter.RegisterCommand(name, argv =>
            {
                if (argv.Count < 2) return;
                float volume = argv.Count >= 3 && float.TryParse(argv[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 1;
                _sound?.Local(argv[1], 0, volume);
            }, "play a sound at your current location (not heard by anyone else)");

        // The archive flag is the player's to give: a "seta" from a server does not make a cvar saved.
        Action<string>? archive = interpreter.CvarArchiveHook;
        interpreter.CvarArchiveHook = name => { if (!console.SessionOrigin) archive?.Invoke(name); };
    }

    private static bool IsHostName(string host)
    {
        if (host.Length is 0 or > 253) return false;
        foreach (char c in host)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
        return true;
    }

    private static string CommandLine(IReadOnlyList<string> argv)
    {
        StringBuilder text = new();
        for (int i = 0; i < argv.Count; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(argv[i].Contains(' ') || argv[i].Length == 0 ? "\"" + argv[i] + "\"" : argv[i]);
        }
        return text.ToString();
    }

    // A command nothing here knows. With a session up DarkPlaces forwards it to the server
    // (Cmd_ForwardToServer); without one it is "unknown command" - logged once per name, so the gaps show.
    private void OnUnknownCommand(string name, IReadOnlyList<string> argv)
    {
        if (_session is { } session && !session.Ended)
        {
            session.SendToServer(CommandLine(argv));
            return;
        }
        if (_session is null && _unknownLogged.Count < 256 && _unknownLogged.Add(name)) Log($"unknown command \"{Printable(name, 60)}\" ({Printable(CommandLine(argv), 120)})");
    }

    /// <summary>A line typed into the developer console that the shell's own interpreter did not claim.</summary>
    public void ConsoleCommand(string line)
    {
        if (_console is null || string.IsNullOrWhiteSpace(line) || line.Length > 2048) return;
        _console.AddText(line + "\n");
    }

    // Host_Quit_f: save the configuration, then leave.
    private void Quit()
    {
        SaveConfig();
        QuitRequested?.Invoke();
    }

    /// <summary>Host_SaveConfig. Called on quit, when a session ends, and when the node goes away.</summary>
    public void SaveConfig()
    {
        if (_console is null || !_started) return;
        if (_console.SaveConfig() is { } path) Log("configuration saved to " + path);
    }

    /// <summary>A sentence for the player, drawn over the menu for a few seconds (the engine's own, not the program's).</summary>
    public void ShowNotice(string text) => Notice(Printable(text, 400));

    private void Notice(string text)
    {
        _notice = text;
        _noticeUntil = Now + 7;
        ConsolePrint?.Invoke(text);
    }

    // =====================================================================================================
    //  A session on this console
    // =====================================================================================================

    /// <summary>A legacy session starts on this menu's console: its HUD canvas gets the fonts the configuration loaded.</summary>
    internal void AttachSession(LegacyGame session, LegacyCanvas hud)
    {
        _session = session;
        _sessionCanvas = hud;
        foreach (string[] command in _fontCommands) hud.LoadFontCommand(command);
        _framesDisconnected = 0;
        // CL_ParseServerInfo: "MR_NewMap" tells the menu a level is loading.
        _host?.NewMap();
    }

    /// <summary>The session is over (it has put the console's cvars back): save what the player changed meanwhile.</summary>
    internal void DetachSession(LegacyGame session)
    {
        if (!ReferenceEquals(_session, session)) return;
        _session = null;
        _sessionCanvas = null;
        _keys?.ReleaseAll();
        SaveConfig();
    }

    /// <summary>MR_ToggleMenu: 1 opens, 0 closes, -1 toggles.</summary>
    public void ToggleMenu(int mode) => _host?.ToggleMenu(mode);

    // =====================================================================================================
    //  One frame
    // =====================================================================================================

    public override void _Process(double delta)
    {
        // The menu alone is recorded as its own kind of frame; with a game running the game's frame is the record.
        bool recorded = _session is null;
        if (recorded) LegacyPerfLog.BeginFrame(2);
        try { ProcessFrame(); }
        finally
        {
            if (recorded)
            {
                LegacyPerfLog.Part(LegacyPerfLog.Program);
                LegacyPerfLog.EndFrame();
            }
        }
    }

    private void ProcessFrame()
    {
        using var _scope = FrameProfiler.Scope("legacy-menu");
        if (!_started || _shutDown || _failed || _console is not { } console || _canvas is not { } canvas || _drawLayer is not { } layer) return;
        double now = Now;
        double frameTime = Math.Clamp(now - _lastFrameAt, 0, 0.5);
        _lastFrameAt = now;
        _frames++;

        UpdateViewSize(force: false);
        if (!_scriptMouseActive && !Headless) _mouse = GetViewport().GetMousePosition();
        RunReviewScript(now);

        // Cbuf_Frame: what the menu queued last frame, the binds pressed since, deferred commands.
        console.Execute(now - _startedAt);
        if (_host is not { } host) return;

        // The server list: replies first, then this frame's share of queries.
        if (_serverList is { } list)
        {
            _serverLink?.Poll();
            list.Frame(list.Clock(), frameTime);
        }

        // cl_screen.c SCR_SetUpToDrawConsole: "scr_menuforcewhiledisconnected && key_dest == key_game &&
        // cls.state == ca_disconnected" opens the menu again from the third frame on. The cvar is 0 in
        // DarkPlaces and Xonotic leaves it there: what normally opens the menu is Host_Init's one
        // "togglemenu 1", and after a disconnect the menu program's own m_draw.
        if (console.Cvars.GetFloat("scr_menuforcewhiledisconnected") != 0 && _session is null && host.KeyDest == MenuKeyDest.Game && !Hud.ChatPrompt.IsOpen)
        {
            if (_framesDisconnected >= 2) host.ToggleMenu(1);
            else _framesDisconnected++;
        }
        else if (_session is not null) _framesDisconnected = 0;

        // MP_Draw, every frame: the program fades itself in and out and draws nothing when it is hidden.
        canvas.PixelWidth = _viewSize.X;
        canvas.PixelHeight = _viewSize.Y;
        canvas.ConWidth = Math.Max(1, console.Cvars.GetFloat("vid_conwidth"));
        canvas.ConHeight = Math.Max(1, console.Cvars.GetFloat("vid_conheight"));
        canvas.ReadTextCvars(name => console.Cvars.Has(name) ? console.Cvars.GetFloat(name) : null);
        canvas.DrawList.Clear();
        host.DrawFrame(_viewSize.X, _viewSize.Y);
        _mouseDelta = Vector2.Zero;
        if (host.FaultCount > 0)
        {
            Fail("The Xonotic menu program stopped with an error. (" + Printable(host.Faults[0].EntryPoint + ": " + host.Faults[0].Message, 400) + ")");
            return;
        }
        // The program sets vid_conwidth / vid_conheight itself (updateConwidths) and draws in them at once.
        canvas.ConWidth = Math.Max(1, console.Cvars.GetFloat("vid_conwidth"));
        canvas.ConHeight = Math.Max(1, console.Cvars.GetFloat("vid_conheight"));
        DrawNotice(canvas, now);
        layer.Present(canvas);

        _sound?.UpdateMusicVolume();
        UpdateCursor();
        if (now >= _nextStatus) Status(now);
    }

    private double _nextStatus;

    // The once-every-few-seconds line: what a run without eyes on it can show.
    private void Status(double now)
    {
        _nextStatus = now + (Headless ? 2 : 5);
        if (_host is not { } host || _canvas is null || _drawLayer is null) return;
        if (!Headless && string.IsNullOrEmpty(s_shotDirectory)) return;
        MenuHostCache? list = _serverList;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"t+{now - _startedAt:0}: frames {_frames}, key_dest {host.KeyDest}, faults {host.FaultCount}, unimplemented builtins {host.UnimplementedBuiltins.Count}, " +
            $"2d commands {_canvas.DrawList.Count} (dropped {_canvas.DrawList.TotalDropped}) in {_drawLayer.SegmentsInUse} segments ({_drawLayer.ClippedSegmentsInUse} clipped), " +
            $"entities {host.Vm.NumEdicts}, zoned strings {host.Vm.ZonedStringCount}, keys to the menu {_menuKeys}, " +
            $"servers {list?.ViewCount ?? 0} shown / {list?.CacheCount ?? 0} known (masters asked {list?.MasterQueryCount ?? 0} replied {list?.MasterReplyCount ?? 0}, servers asked {list?.ServerQueryCount ?? 0} replied {list?.ServerReplyCount ?? 0}, refused {list?.RepliesRefused ?? 0}), " +
            $"sounds started {_sound?.SoundsStarted ?? 0} (last \"{_sound?.LastSound}\"), music \"{_sound?.Track}\", session {(_session is null ? "none" : _session.IsLocal ? "local game" : "up")}, " +
            $"managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB, working set {System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB, " +
            $"threads named legacy-server {CountServerThreads()}"));
    }

    // How many local-server threads exist: 0 at the menu is what "the server is gone" looks like from outside.
    private static int CountServerThreads() => VortexArena.Legacy.Local.LegacyLocalServer.LiveThreads;

    private void DrawNotice(LegacyCanvas canvas, double now)
    {
        if (_notice.Length == 0 || now >= _noticeUntil) return;
        float w = canvas.ConWidth, size = 10;
        float alpha = (float)Math.Clamp(_noticeUntil - now, 0, 1);
        int font = canvas.Fonts.Find("user0");
        QcVector cell = new(size, size, 0), unit = new(1, 1, 0);
        // Wrapped by words to the width of the virtual screen.
        List<string> lines = new();
        StringBuilder line = new();
        foreach (string word in _notice.Split(' '))
        {
            if (line.Length > 0 && canvas.StringWidth(line + " " + word, true, cell, font, unit) > w - 80)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        float height = lines.Count * (size + 3) + 14, top = 24;
        canvas.DrawList.ResetClip();
        canvas.DrawList.Fill(new QcVector(30, top, 0), new QcVector(w - 60, height, 0), new QcVector(0.05f, 0.05f, 0.08f), 0.92f * alpha, 0);
        for (int i = 0; i < lines.Count; i++)
        {
            float width = canvas.StringWidth(lines[i], true, cell, font, unit);
            canvas.DrawList.Text(new LegacyText
            {
                Position = new QcVector((w - width) / 2, top + 7 + i * (size + 3), 0), Scale = cell, Color = new QcVector(1, 0.85f, 0.4f), Alpha = alpha,
                Text = lines[i], Font = font, FontScale = unit, IgnoreColorCodes = true,
            });
        }
    }

    // vid.mode.width / height and the cvars that mirror them, then cl_screen.c CL_UpdateScreen's bounds on the
    // virtual 2D size: vid_conheight is the menu program's to choose (its updateConwidths), and with
    // vid_conwidthauto the width follows from it and the window's shape, rounded DOWN - which is why a
    // 1280x720 window is 1066 units wide in DarkPlaces although the menu itself computes 1067.
    private void UpdateViewSize(bool force)
    {
        if (_console is null) return;
        Engine.Simulation.CvarService cvars = _console.Cvars;
        Vector2 size = Headless ? new Vector2(1280, 720) : GetViewport().GetVisibleRect().Size;
        if (size.X >= 1 && size.Y >= 1 && (force || size != _viewSize))
        {
            _viewSize = size;
            cvars.Set("vid_width", ((int)size.X).ToString(CultureInfo.InvariantCulture));
            cvars.Set("vid_height", ((int)size.Y).ToString(CultureInfo.InvariantCulture));
        }
        if (!(_viewSize.X >= 1) || !(_viewSize.Y >= 1)) return;
        float conWidth = Math.Clamp(cvars.GetFloat("vid_conwidth"), 160, 32768), conHeight = Math.Clamp(cvars.GetFloat("vid_conheight"), 90, 24576);
        float pixelHeight = cvars.GetFloat("vid_pixelheight") > 0 ? cvars.GetFloat("vid_pixelheight") : 1;
        if (!cvars.Has("vid_conwidthauto") || cvars.GetFloat("vid_conwidthauto") != 0)
            conWidth = MathF.Floor(conHeight * _viewSize.X / (_viewSize.Y * pixelHeight));
        if (cvars.GetFloat("vid_conwidth") != conWidth) cvars.Set("vid_conwidth", conWidth.ToString("0.######", CultureInfo.InvariantCulture));
        if (cvars.GetFloat("vid_conheight") != conHeight) cvars.Set("vid_conheight", conHeight.ToString("0.######", CultureInfo.InvariantCulture));
    }

    // The menu draws its own pointer (in_client_mouse: "VID_SetMouse(false, hidecursor)"), so the system's is
    // hidden while the menu has the keys - by giving it an empty picture, which, unlike a mouse MODE, is
    // nobody else's to set.
    private void UpdateCursor()
    {
        if (Headless) return;
        bool hide = HasKeys && !ConsoleState.IsOpen;
        if (hide == _cursorHidden) return;
        _cursorHidden = hide;
        if (hide)
        {
            _blankCursorImage ??= Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
            Input.SetCustomMouseCursor(_blankCursorImage);
        }
        else Input.SetCustomMouseCursor(null);
    }

    // =====================================================================================================
    //  Input: window events to DarkPlaces key events
    // =====================================================================================================

    public override void _Input(InputEvent @event)
    {
        if (!_started || _shutDown || _failed || _keys is not { } keys || _host is null) return;
        if (@event is InputEventMouseMotion motion)
        {
            if (!_scriptMouseActive) _mouse = motion.Position;
            _mouseDelta += motion.Relative;
            return;   // not consumed: a session reads the same motion for mouse look
        }
        // The developer console is the shell's: while it is open, and for the key that opens it, keys are not ours.
        if (ConsoleState.IsOpen || Hud.ChatPrompt.IsOpen) return;

        int key = -1, character = 0;
        bool down;
        switch (@event)
        {
            case InputEventKey k:
                if (k.Keycode == Key.Quoteleft || k.PhysicalKeycode == Key.Quoteleft) return;
                key = LegacyKeyMap.KeyNumber(k);
                character = LegacyKeyMap.Character(k);
                down = k.Pressed;
                break;
            case InputEventMouseButton m:
                key = LegacyKeyMap.MouseButtonNumber(m.ButtonIndex);
                down = m.Pressed;
                if (!_scriptMouseActive) _mouse = m.Position;
                break;
            default:
                return;
        }
        if (key < 0) return;
        // With no session there is nothing behind the menu for a key to fall through to; with one, every key
        // is DarkPlaces' to route (to the menu, to the client program, or to its bind).
        KeyEvent(key, character, down);
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Key_Event, with one repair: a mouse grab swallows the PRESS of Escape, so a release that
    /// arrives for a key that never went down is given its press first.</summary>
    private void KeyEvent(int key, int character, bool down)
    {
        if (_keys is not { } keys) return;
        if (!down && key == LegacyKeyEvents.KEscape && keys.Down(key) == 0) keys.Event(key, 0, true);
        // Typed characters arrive with the press: K_* in one argument, the character in the other.
        keys.Event(key, character, down);
    }

    public override void _Notification(int what)
    {
        // Key_ReleaseAll when the window loses focus: a key held across an alt-tab must not stay down.
        if (what == NotificationWMWindowFocusOut || what == NotificationApplicationFocusOut) _keys?.ReleaseAll();
    }

    // =====================================================================================================
    //  The developer aid: a script of pointer moves, clicks and keys, and screenshots
    // =====================================================================================================

    // VORTEX_LEGACY_SCRIPT names a text file of lines "<seconds> <command>", as for a legacy session
    // (LegacyGame.RunReviewScript), timed from when the menu started. Commands: "mouse <x> <y>" puts the pointer
    // at window pixel x,y (and keeps the real pointer out of it from then on); "click" presses and releases
    // MOUSE1 there; "key <name>" presses and releases a key by its DarkPlaces name (ESCAPE, ENTER, DOWNARROW, a);
    // "keydown" / "keyup" for one edge; "text <string>" types characters; "shot <name>" saves the window to
    // VORTEX_LEGACY_SHOTS; "sync game" waits for a session started from the menu to be in the game and restarts
    // the clock there, "sync menu" waits for it to be over; anything else is a console line. Environment variables
    // rather than cvars, so nothing a server or a program sends can drive the menu.
    private static readonly string? s_scriptPath = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_SCRIPT");
    private static readonly string? s_shotDirectory = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_SHOTS");
    // The one host a scripted run may connect to (see the connect command).
    private static readonly string? s_scriptHost = System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_SCRIPT_HOST");
    private static readonly bool s_dumpDraws = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_DUMP"));
    private List<(double At, string Command)>? _script;
    private int _scriptNext;
    private bool _scriptLoaded, _scriptMouseActive;
    private double _scriptBase;
    private string? _scriptWaiting;
    private int _scriptWaitLevel;
    private readonly Queue<(int Key, int Character, bool Down)> _scriptKeys = new();

    /// <summary>True when a review script drives this menu (a session then leaves the script file to it).</summary>
    public static bool HasReviewScript => !string.IsNullOrEmpty(s_scriptPath) && System.IO.File.Exists(s_scriptPath);

    private void RunReviewScript(double now)
    {
        if (!_scriptLoaded)
        {
            _scriptLoaded = true;
            _scriptBase = _startedAt;
            if (HasReviewScript)
            {
                _script = new List<(double, string)>();
                foreach (string raw in System.IO.File.ReadAllLines(s_scriptPath!))
                {
                    string line = raw.Trim();
                    int space = line.IndexOf(' ');
                    if (line.Length == 0 || line[0] == '#' || space <= 0) continue;
                    if (double.TryParse(line.AsSpan(0, space), NumberStyles.Float, CultureInfo.InvariantCulture, out double at))
                        _script.Add((at, line[(space + 1)..].Trim()));
                }
                Log($"review script: {_script.Count} lines from {s_scriptPath}");
            }
        }
        // One key edge a frame, so a press and its release are seen in order by a program that polls.
        if (_scriptKeys.Count > 0)
        {
            (int key, int character, bool down) = _scriptKeys.Dequeue();
            KeyEvent(key, character, down);
            return;
        }
        if (_script is null) return;
        if (_scriptWaiting is not null)
        {
            bool ready = _scriptWaiting switch
            {
                "game" => _session is { InGame: true },
                "level" => _session is { InGame: true } session && session.LevelsEntered >= _scriptWaitLevel,
                _ => _session is null,
            };
            if (!ready) return;
            Log($"script: sync {_scriptWaiting} reached at t+{now - _startedAt:0.00}");
            _scriptWaiting = null;
            _scriptBase = now;
        }
        while (_scriptNext < _script.Count && now - _scriptBase >= _script[_scriptNext].At)
        {
            string command = _script[_scriptNext++].Command;
            Log($"script t+{now - _startedAt:0.00}: {command}");
            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "shot" when parts.Length >= 2:
                    SaveShot(parts[1]);
                    return;   // what follows a shot waits for the next frame, so the shot shows the state before it
                case "mouse" when parts.Length >= 3 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                                  && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y):
                    _scriptMouseActive = true;
                    _mouse = new Vector2(x, y);
                    // The client program's own pointer (the map vote, the HUD editor) is the session's to report.
                    _session?.SetPointer(_mouse);
                    return;   // the program reads the pointer once a frame: let it see the move before a click
                case "click":
                    _scriptKeys.Enqueue((512, 0, true));
                    _scriptKeys.Enqueue((512, 0, false));
                    return;
                case "key" or "keydown" or "keyup" when parts.Length >= 2:
                {
                    int key = CsqcKeys.StringToKeynum(parts[1]);
                    if (key < 0) { Log($"script: no key named \"{parts[1]}\""); break; }
                    int character = key is >= 32 and < 127 ? key : 0;
                    if (parts[0] != "keyup") _scriptKeys.Enqueue((key, character, true));
                    if (parts[0] != "keydown") _scriptKeys.Enqueue((key, 0, false));
                    return;
                }
                case "text" when command.Length > 5:
                    foreach (char c in command[5..])
                    {
                        int key = c is >= 'A' and <= 'Z' ? c + 32 : c;
                        _scriptKeys.Enqueue((key, c, true));
                        _scriptKeys.Enqueue((key, 0, false));
                    }
                    return;
                case "look" when parts.Length >= 3 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch)
                                 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw):
                    _session?.SetViewAngles(pitch, yaw);
                    break;
                case "window" when parts.Length >= 2 && !Headless:
                    // What alt-tabbing away does to the window: "window minimize", later "window restore".
                    DisplayServer.WindowSetMode(parts[1] == "minimize" ? DisplayServer.WindowMode.Minimized : DisplayServer.WindowMode.Windowed);
                    break;
                case "sv" when command.Length > 3:
                    _session?.ServerCommand(command[3..]);
                    break;
                case "mem":
                    Log("memory " + (parts.Length >= 2 ? parts[1] : "") + ": " + LegacyData.MemoryReport());
                    break;
                case "sync" when parts.Length >= 2 && parts[1] is "game" or "menu" or "level":
                    // "sync level": until the session has entered the level AFTER the one it is in (a level change).
                    _scriptWaitLevel = (_session?.LevelsEntered ?? 0) + 1;
                    _scriptWaiting = parts[1];
                    return;
                default:
                    _console?.AddText(command + "\n");
                    break;
            }
        }
    }

    private int _shotCount;

    private void SaveShot(string name)
    {
        if (string.IsNullOrEmpty(s_shotDirectory)) return;
        Image? image = GetViewport()?.GetTexture()?.GetImage();
        if (image is null) return; // a windowless run has no frame to save
        System.IO.Directory.CreateDirectory(s_shotDirectory);
        string path = System.IO.Path.Combine(s_shotDirectory, name + ".png");
        int index = _shotCount++;
        Log($"saving {path}");
        System.Threading.Tasks.Task.Run(() => image.SavePng(path));
        if (s_dumpDraws && _canvas is { } canvas) DumpFrame(canvas, line => Log($"s{index:00} " + line));
    }

    // One frame's 2D draw list as text: what was drawn, where, with which picture or font.
    private void DumpFrame(LegacyCanvas canvas, Action<string> log)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        log(string.Create(inv, $"dump: con {canvas.ConWidth}x{canvas.ConHeight} window {_viewSize.X}x{_viewSize.Y} mouse {_mouse.X:0},{_mouse.Y:0} commands {canvas.DrawList.Count} segments {_drawLayer?.SegmentsInUse} ({_drawLayer?.ClippedSegmentsInUse} clipped) key_dest {_host?.KeyDest}"));
        int n = 0;
        foreach (LegacyDrawCommand c in canvas.DrawList.Commands)
        {
            if (n++ >= 1200) break;
            string extra = c.Kind switch
            {
                LegacyDrawKind.Picture => $"\"{c.Text}\" tex {(canvas.LoadPictureTexture(c.Text ?? "") is { } t ? t.GetSize().ToString() : "MISSING")} src {c.SourceX:0.##},{c.SourceY:0.##} {c.SourceWidth:0.##}x{c.SourceHeight:0.##}",
                LegacyDrawKind.Text => $"font {c.Font} \"{(c.Text is { Length: > 70 } s ? s[..70] : c.Text)}\"",
                _ => "",
            };
            log(string.Create(inv, $"dump: {n,4} {c.Kind,-9} f{c.Flags} @{c.X:0.#},{c.Y:0.#} {c.Width:0.#}x{c.Height:0.#} rgba {c.Color.R:0.##} {c.Color.G:0.##} {c.Color.B:0.##} {c.Color.A:0.##} {extra}"));
        }
    }

    // =====================================================================================================
    //  Log and end
    // =====================================================================================================

    private readonly StringBuilder _printLine = new();
    private int _printsLogged;

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
            ConsolePrint?.Invoke(line);
            if (_printsLogged++ < 400 && (Headless || !string.IsNullOrEmpty(s_shotDirectory))) Log("print: " + Printable(line, 300));
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

    /// <summary>
    /// MR_Shutdown and Host_SaveConfig: m_shutdown, the configuration written, the socket and the sounds
    /// closed. Synchronous and safe to call twice.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        try
        {
            // A session on this console goes first: it puts the cvars back, and it reads the game data to the end.
            _session?.Shutdown();
            _host?.Shutdown();
            SaveConfig();
            LegacyPerfLog.Flush();
        }
        finally
        {
            _host = null;
            _serverLink?.Dispose();
            _serverLink = null;
            _sound?.Shutdown();
            if (_cursorHidden && !Headless) Input.SetCustomMouseCursor(null);
            _cursorHidden = false;
            _canvas?.DrawList.Clear();
            if (_canvas is not null) _drawLayer?.Present(_canvas);
            _vfs?.Dispose();
            _vfs = null;
        }
    }

    public override void _ExitTree() => Shutdown();
}

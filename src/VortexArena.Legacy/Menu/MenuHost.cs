// Port of Base/darkplaces/menu.c, the "Menu prog handling" half: MP_Init, MP_CheckRequiredFuncs,
// MP_Draw, MP_KeyEvent, MP_ToggleMenu, MP_NewMap, MP_GetServerListEntryCategory, MP_Shutdown,
// MVM_error_cmd, and the router's console commands (MR_Restart_f, Call_MR_ToggleMenu_f); mvm_cmds.c
// MP_ConsoleCommand; prvm_edict.c PRVM_GameCommand (menu_cmd); prvm_cmds.c uri_to_string_callback
// (URI_Get_Callback).
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Menu;

/// <summary>The program could not be started. The message says exactly why.</summary>
public sealed class MenuLoadException : Exception
{
    public MenuLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>One fault of the menu program: which entry point was running, and the VM's message with the QuakeC stack.</summary>
public sealed record MenuFault(string EntryPoint, string Message);

/// <summary>keydest_t as the menu program numbers it (getkeydest): key_game 0, key_message 1, key_menu 2, key_menu_grabbed 3.</summary>
public enum MenuKeyDest
{
    Game = 0,
    Message = 1,
    Menu = 2,
    /// <summary>The menu has the keyboard to itself: it is waiting for the key to bind, and wants even the function keys.</summary>
    MenuGrabbed = 3,
}

/// <summary>A video mode as getresolution reports it.</summary>
public readonly record struct MenuVideoMode(int Width, int Height, float PixelHeight = 1);

/// <summary>
/// The engine around the menu program, as far as it is not the console: the state of the client and
/// the window, which an owner keeps current, and the things only an owner can do.
/// </summary>
public sealed class MenuHostOptions
{
    /// <summary>sv.active: a local server is running.</summary>
    public Func<bool>? ServerActive { get; init; }
    /// <summary>svs.maxclients.</summary>
    public Func<int>? MaxClients { get; init; }
    /// <summary>cls.state == ca_connected.</summary>
    public Func<bool>? Connected { get; init; }
    /// <summary>cls.demoplayback.</summary>
    public Func<bool>? DemoPlayback { get; init; }
    /// <summary>key_consoleactive: the console is down over everything, and the menu has no mouse.</summary>
    public Func<bool>? ConsoleActive { get; init; }
    /// <summary>vid.mode.width and height: the window in pixels.</summary>
    public Func<(float Width, float Height)>? VideoSize { get; init; }
    /// <summary>in_windowmouse_x / y: the pointer in window pixels.</summary>
    public Func<(float X, float Y)>? WindowMouse { get; init; }
    /// <summary>in_mouse_x / y: the pointer's movement since the last frame, for a menu that moves its own cursor.</summary>
    public Func<(float X, float Y)>? MouseDelta { get; init; }
    /// <summary>video_resolutions: the modes the Video settings offer. Empty: DarkPlaces' hard-coded list.</summary>
    public IReadOnlyList<MenuVideoMode> VideoModes { get; init; } = Array.Empty<MenuVideoMode>();
    /// <summary>VID_GetDesktopMode.</summary>
    public Func<MenuVideoMode>? DesktopMode { get; init; }
    /// <summary>The sound system: precache_sound, localsound, soundlength. Null: sound is not initialised.</summary>
    public ILegacySound? Sound { get; init; }
    /// <summary>The server list. Null: an empty one that asks nobody.</summary>
    public MenuHostCache? ServerList { get; init; }
    /// <summary>The uri_get builtin: (url, id) to "request started". Null: no HTTP, as when DarkPlaces
    /// runs without libcurl. The result is delivered with <see cref="MenuHost.UriGetCallback"/>.</summary>
    public Func<string, int, bool>? UriGet { get; init; }
    /// <summary>SCR_CenterPrint.</summary>
    public Action<string>? CenterPrint { get; init; }
    /// <summary>A command the program created with registercommand was typed: the whole line. DarkPlaces
    /// offers such a command to the CLIENT program (Cmd_CL_Callback), not back to the menu. True if handled.</summary>
    public Func<string, bool>? QcCommand { get; init; }
    /// <summary>fs_all_gamedirs: (name, description) of the game directories the mod list offers.</summary>
    public IReadOnlyList<(string Name, string Description)> GameDirs { get; init; } = Array.Empty<(string, string)>();
    /// <summary>The <c>menu_restart</c> command: the owner unloads this host and loads a new one.</summary>
    public Action? Restart { get; init; }
    /// <summary>host.realtime in seconds, what gettime() answers. Null: the wall clock.</summary>
    public Func<double>? Clock { get; init; }
    /// <summary>DarkPlaces falls back to its built-in menu when the program faults, for good. A
    /// diagnostic run can keep calling it instead; the VM is unwound after a fault and safe to re-enter.</summary>
    public bool KeepRunningAfterFault { get; init; }
}

/// <summary>
/// One loaded menu program (menu.dat) and the engine around it: the third QuakeC VM of a DarkPlaces
/// client, with its own builtin table (<see cref="MenuBuiltinTable"/>), its entry points and its
/// engine globals.
///
/// The program is the player's own - it comes with the game data, not from a server - but what it
/// shows does not: server names, info strings and player lists arrive from the internet through the
/// server list, and HTTP replies through uri_get. So the bounds are the same as for any program: a
/// fault inside it is recorded and never thrown, and it reaches the machine only through
/// <see cref="MenuQcHost"/> and <see cref="MenuHostOptions"/>.
/// </summary>
public sealed partial class MenuHost : IDisposable
{
    /// <summary>M_MAX_EDICTS.</summary>
    public const int MaxEdicts = 32768;

    private readonly MenuHostOptions _options;
    private readonly QcCoreBuiltins _core;
    private readonly QcStringBuiltins _strings;
    private readonly QcAutocvars _autocvars;
    private readonly List<MenuFault> _faults = new();
    private readonly int _fnInit, _fnShutdown, _fnDraw, _fnKeyDown, _fnKeyUp, _fnToggle, _fnNewMap, _fnHostCacheCategory, _fnGameCommand, _fnUriGetCallback;
    private readonly int _globalSelf, _globalDrawFont, _globalDrawFontScale;
    private bool _disposed, _initialized;
    private double _frameStart;

    public QcVm Vm { get; }
    public ProgsFile Program => Vm.Progs;
    public MenuQcHost Services { get; }
    public LegacyConsole Console { get; }
    public ILegacyDraw Draw { get; }
    public MenuHostCache ServerList { get; }
    public int AutocvarsBound { get; }

    /// <summary>key_dest. The program sets it (setkeydest) when it shows and hides itself; the owner
    /// reads it to decide where a key goes and whether the game has the mouse.</summary>
    public MenuKeyDest KeyDest { get; set; } = MenuKeyDest.Game;
    /// <summary>in_client_mouse: true when the program wants the window's own pointer position
    /// (getmousepos is absolute), false when it wants movement and keeps a cursor of its own.</summary>
    public bool ClientMouse { get; set; } = true;

    /// <summary>mp_failed: the program faulted and is not run again (unless
    /// <see cref="MenuHostOptions.KeepRunningAfterFault"/>).</summary>
    public bool Failed => FaultCount > 0 && !_options.KeepRunningAfterFault;
    public int FaultCount { get; private set; }
    /// <summary>The first 64 faults.</summary>
    public IReadOnlyList<MenuFault> Faults => _faults;
    /// <summary>Raised once per fault, after the VM has been unwound: "Falling back to engine menu".</summary>
    public event Action<MenuFault>? Faulted;
    /// <summary>True once m_init has returned without a fault.</summary>
    public bool Initialized => _initialized;

    /// <summary>Builtins the program called that nothing implements: (number, name) to call count. Each such call returned 0.</summary>
    public Dictionary<(int Number, string Name), long> UnimplementedBuiltins { get; } = new();
    /// <summary>Calls received by menu builtin number.</summary>
    public long CallCount(int number) => (uint)number < (uint)_calls.Length ? _calls[number] : 0;
    /// <summary>The builtins called at least once: number, C function, count.</summary>
    public IEnumerable<(int Number, string Function, long Calls)> CallCounts
    {
        get
        {
            foreach ((int number, string function) in MenuBuiltinTable.Entries)
                if (_calls[number] != 0) yield return (number, function, _calls[number]);
        }
    }

    private bool CanRun => !_disposed && (FaultCount == 0 || _options.KeepRunningAfterFault);

    /// <summary>
    /// Loads <paramref name="program"/> and prepares it to run: everything MP_Init does up to, but not
    /// including, the call of m_init (that is <see cref="Init"/>).
    /// </summary>
    /// <param name="program">The bytes of menu.dat.</param>
    /// <param name="draw">Where the program's 2D drawing goes.</param>
    /// <exception cref="MenuLoadException">The file is not a valid program or lacks a function the engine needs.</exception>
    public MenuHost(ReadOnlySpan<byte> program, LegacyConsole console, ILegacyDraw draw, MenuHostOptions? options = null,
        Action<string>? print = null, Action<string>? warning = null)
    {
        Console = console ?? throw new ArgumentNullException(nameof(console));
        Draw = draw ?? throw new ArgumentNullException(nameof(draw));
        _options = options ?? new MenuHostOptions();
        Services = new MenuQcHost(console, print, warning) { Clock = _options.Clock };
        ServerList = _options.ServerList ?? new MenuHostCache(console.Cvars);

        ProgsFile progs;
        try { progs = ProgsFile.Load(program); }
        catch (ProgsFormatException e) { throw new MenuLoadException("menu.dat failed to load: " + e.Message, e); }

        // prog->num_edicts = 1, limit_edicts = M_MAX_EDICTS. (max_edicts starts at 512 as for any program.)
        Vm = new QcVm(progs, "menu", 512) { EdictLimit = MaxEdicts };

        // MP_CheckRequiredFuncs.
        foreach (string required in new[] { "m_init", "m_keydown", "m_draw", "m_toggle", "m_shutdown" })
            if (Vm.FindFunction(required) == 0) throw new MenuLoadException($"menu: {required} not found in menu.dat");
        _fnInit = Vm.FindFunction("m_init");
        _fnShutdown = Vm.FindFunction("m_shutdown");
        _fnDraw = Vm.FindFunction("m_draw");
        _fnKeyDown = Vm.FindFunction("m_keydown");
        _fnKeyUp = Vm.FindFunction("m_keyup");
        _fnToggle = Vm.FindFunction("m_toggle");
        _fnNewMap = Vm.FindFunction("m_newmap");
        _fnHostCacheCategory = Vm.FindFunction("m_gethostcachecategory");
        _fnGameCommand = Vm.FindFunction("GameCommand");
        _fnUriGetCallback = Vm.FindFunction("URI_Get_Callback");

        // m_required_globals / m_required_fields: self, drawfont, drawfontscale, require_spawnfunc_prefix
        // and the classname field. The C appends the ones a program lacks; only the three read here matter.
        _globalSelf = GlobalOffset("self", QcType.Entity);
        _globalDrawFont = GlobalOffset("drawfont", QcType.Float);
        _globalDrawFontScale = GlobalOffset("drawfontscale", QcType.Vector);
        try { Vm.EnsureField("classname", QcType.String); }
        catch (QcRuntimeException e) { throw new MenuLoadException("menu.dat failed to load: " + e.Message, e); }

        _core = new QcCoreBuiltins(Vm, Services, new HashSet<string>(MenuBuiltinTable.Extensions, StringComparer.OrdinalIgnoreCase))
        {
            // MVM_init_edict and MVM_free_edict are empty: the menu has no world to link entities into.
            ReservedEdicts = 0,
            // gettime(GETTIME_HIRES): seconds since the frame began.
            FrameDirtyTime = () => _frameStart,
        };
        _strings = new QcStringBuiltins(Vm, Services) { OpenFile = _core.FileStream };
        RegisterBuiltins();
        Vm.UnknownBuiltin = (_, number, name) =>
            UnimplementedBuiltins[(number, name)] = UnimplementedBuiltins.GetValueOrDefault((number, name)) + 1;

        _autocvars = new QcAutocvars(Vm, Services);
        AutocvarsBound = _autocvars.Bind();
        Services.CvarChanged += OnCvarChanged;

        ServerList.Category = ServerListEntryCategory;
        RegisterConsoleCommands();
    }

    private int GlobalOffset(string name, QcType type) =>
        Vm.FindGlobal(name) is { } def && def.Type == type && def.Offset + (type == QcType.Vector ? 3 : 1) <= Vm.NumGlobals ? def.Offset : -1;

    private void OnCvarChanged(string name)
    {
        if (!_disposed) _autocvars.Update(name);
    }

    // ---- running the program -----------------------------------------------------------------------

    private bool Run(int function, int argCount, string entryPoint)
    {
        if (function == 0 || !CanRun) return false;
        try
        {
            Vm.Execute(function, argCount);
            return true;
        }
        catch (QcRuntimeException e)
        {
            RecordFault(entryPoint, e.Message);
            return false;
        }
    }

    // The text argument is a temp string, dropped again after the call (restorevm_tempstringsbuf_cursize).
    private bool RunWithText(int function, string text, string entryPoint)
    {
        if (function == 0 || !CanRun) return false;
        int mark = Vm.TempStringMark;
        bool ok;
        try
        {
            Vm.SetArgInt(0, Vm.TempString(text));
            ok = Run(function, 1, entryPoint);
        }
        catch (QcRuntimeException e)
        {
            RecordFault(entryPoint, e.Message);
            ok = false;
        }
        Vm.ReleaseTempStrings(mark);
        return ok;
    }

    // MVM_error_cmd: "Menu_Error: ...", key_dest = key_game, mp_failed = true, and the command buffer
    // is left alone here (the C clears it "to prevent an endless loop if the error was triggered by a
    // command"; the buffer is the owner's and bounded per frame).
    private void RecordFault(string entryPoint, string message)
    {
        FaultCount++;
        MenuFault fault = new(entryPoint, message);
        if (_faults.Count < 64) _faults.Add(fault);
        Services.Print("Menu_Error: " + message + "\n");
        KeyDest = MenuKeyDest.Game;
        Faulted?.Invoke(fault);
    }

    /// <summary>Forgets recorded faults, so a diagnostic run can count them per phase. Does not repair the program.</summary>
    public void ClearFaults()
    {
        FaultCount = 0;
        _faults.Clear();
    }

    /// <summary>MP_Init's last step: m_init(). Returns false if it faulted.</summary>
    public bool Init()
    {
        if (!CanRun) return false;
        // "in_client_mouse = true" before the program runs.
        ClientMouse = true;
        bool ok = Run(_fnInit, 0, "m_init");
        // "Once m_init was called, we consider menuqc code fully initialized."
        _core.StartTime = Services.RealTime;
        _initialized = ok;
        return ok;
    }

    /// <summary>MP_Shutdown: m_shutdown(), key_dest back to the game, and the program is unloaded.</summary>
    public void Shutdown()
    {
        if (_disposed) return;
        Run(_fnShutdown, 0, "m_shutdown");
        KeyDest = MenuKeyDest.Game;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Services.CvarChanged -= OnCvarChanged;
        if (ReferenceEquals(ServerList.Category?.Target, this)) ServerList.Category = null;
        _core.Dispose();
    }

    /// <summary>
    /// MP_Draw: m_draw(vid.mode.width, vid.mode.height), once a frame, whether or not the menu is
    /// showing - the program fades itself in and out and notices on its own that the client was
    /// disconnected.
    /// </summary>
    /// <remarks>PRVM_GarbageCollection, which MP_Draw runs first, is not ported (see the QuakeC VM's notes).</remarks>
    public bool DrawFrame(float width, float height)
    {
        if (!CanRun) return false;
        _frameStart = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        Vm.SetArgFloat(0, width);
        Vm.SetArgFloat(1, height);
        return Run(_fnDraw, 2, "m_draw");
    }

    /// <summary>MP_KeyEvent: m_keydown(key, ascii) or, if the program has one, m_keyup(key, ascii).</summary>
    public void KeyEvent(int key, int ascii, bool down)
    {
        if (!CanRun) return;
        int function = down ? _fnKeyDown : _fnKeyUp;
        if (function == 0) return;
        Vm.SetArgFloat(0, key);
        Vm.SetArgFloat(1, ascii);
        Run(function, 2, down ? "m_keydown" : "m_keyup");
    }

    /// <summary>MP_ToggleMenu: m_toggle(mode). 1 opens, 0 closes, -1 toggles (and closes only while connected).</summary>
    public void ToggleMenu(int mode)
    {
        if (!CanRun) return;
        Vm.SetArgFloat(0, mode);
        Run(_fnToggle, 1, "m_toggle");
    }

    /// <summary>MP_NewMap: m_newmap(), when the client starts loading a level.</summary>
    public void NewMap() => Run(_fnNewMap, 0, "m_newmap");

    /// <summary>
    /// The <c>menu_cmd</c> console command (PRVM_GameCommand): GameCommand(text), with neither time nor
    /// self set. This is how the client program and the configuration talk to the menu
    /// (<c>menu_cmd directmenu Welcome</c>, <c>menu_cmd sync</c>, <c>menu_cmd closemenu</c>).
    /// </summary>
    public void GameCommand(string text)
    {
        if (_fnGameCommand == 0)
        {
            if (!_disposed) Services.Print("menu program do not support GameCommand!\n");
            return;
        }
        RunWithText(_fnGameCommand, text, "GameCommand");
    }

    /// <summary>
    /// MP_ConsoleCommand: GameCommand with a whole command line, self set to -1 as the C sets it.
    /// Nothing in DarkPlaces calls it (commands made by registercommand go to the client program);
    /// it is here because the menu's entry points are.
    /// </summary>
    public bool ConsoleCommand(string text)
    {
        if (_fnGameCommand == 0 || !CanRun) return false;
        if (_globalSelf >= 0) Vm.GlobalInt(_globalSelf) = -1;
        return RunWithText(_fnGameCommand, text, "GameCommand") && QcVm.FloatToInt(Vm.ResultFloat) != 0;
    }

    /// <summary>
    /// uri_to_string_callback: the outcome of a uri_get the program started. URI_Get_Callback(id, status,
    /// data) with the status DarkPlaces passes: 0 for success with the body, otherwise an error code
    /// (the HTTP status, or a negative curl error) and no body.
    /// </summary>
    public void UriGetCallback(int id, float status, string data)
    {
        if (_fnUriGetCallback == 0 || !CanRun) return;
        // The body came from a web server: cut to what a tempstring holds.
        if (data.Length >= Vm.MaxStringLength) data = data[..(Vm.MaxStringLength - 1)];
        int mark = Vm.TempStringMark;
        try
        {
            Vm.SetArgFloat(0, id);
            Vm.SetArgFloat(1, status);
            Vm.SetArgInt(2, Vm.TempString(data));
            Run(_fnUriGetCallback, 3, "URI_Get_Callback");
        }
        catch (QcRuntimeException e) { RecordFault("URI_Get_Callback", e.Message); }
        Vm.ReleaseTempStrings(mark);
    }

    // MP_GetServerListEntryCategory: m_gethostcachecategory(-1). The entry is the server list's
    // CallbackEntry for the length of the call, and getserverliststring(field, -1) reads it.
    private int ServerListEntryCategory(HostCacheEntry entry)
    {
        if (_fnHostCacheCategory == 0 || !CanRun) return 0;
        Vm.SetArgFloat(0, -1);
        return Run(_fnHostCacheCategory, 1, "m_gethostcachecategory") ? QcVm.FloatToInt(Vm.ResultFloat) : 0;
    }

    // ---- the console commands that lead into the program -------------------------------------------

    private void RegisterConsoleCommands()
    {
        Console.Interpreter.RegisterCommand("menu_cmd", argv =>
        {
            // PRVM_GameCommand passes Cmd_Args: everything after the command word.
            if (Console.SessionOrigin && !(argv.Count > 1 && SessionMenuCommands.Contains(argv[1])))
            {
                Services.Print($"the server asked to run \"menu_cmd {(argv.Count > 1 ? argv[1] : "")}\": not followed\n");
                return;
            }
            GameCommand(CsqcConsole.JoinArguments(argv, 1));
        }, "calls the menu QC function GameCommand with the supplied string as argument");
        // Call_MR_ToggleMenu_f: no argument means -1.
        Console.Interpreter.RegisterCommand("togglemenu", argv => ToggleMenu(argv.Count < 2 ? -1 : Atoi(argv[1])), "opens or closes menu");
        Console.RegisterPlayerCommand("menu_restart", _ => _options.Restart?.Invoke(), "restart menu system (reloads menu.dat)");
    }

    // What a session's own text (the server's console commands, its client program's localcmd) may ask
    // of the menu: to show or close a dialog and to re-read its cvars - which is all Xonotic's client
    // program does ask. Xonotic's GameCommand also has general-purpose verbs ("nextframe <command>" runs
    // any console command a frame later, "rpn" and "addtolist" write cvars); from a server those would be
    // a way around everything a session is kept from doing, so they are the player's alone.
    private static readonly HashSet<string> SessionMenuCommands = new(StringComparer.Ordinal)
    {
        "directmenu", "directpanelhudmenu", "closemenu", "sync", "isdemo",
    };

    // C's atoi, for a console argument.
    private static int Atoi(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        bool negative = false;
        if (i < text.Length && text[i] is '+' or '-') negative = text[i++] == '-';
        long value = 0;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++) value = Math.Min(value * 10 + (text[i] - '0'), int.MaxValue);
        return (int)(negative ? -value : value);
    }

    internal bool ServerActive => _options.ServerActive?.Invoke() ?? false;
}

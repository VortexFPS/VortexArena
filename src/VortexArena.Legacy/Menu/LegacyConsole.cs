// Port of Base/darkplaces/host.c Host_AddConfigText / Host_LoadConfig_f (the start-up scripts) and
// Host_SaveConfig; cmd.c Cbuf_AddText, Cbuf_InsertText, Cbuf_Execute, Cbuf_Execute_Deferred, Cmd_Wait_f,
// Cmd_Defer_f, Cmd_Toggle_f, Cmd_Exec (config.cfg from the user directory; cvar_lockdefaults after
// default.cfg); cvar.c Cvar_WriteVariables, Cvar_LockDefaults_f and Cvar_RegisterVirtual (with every call of
// it in the engine: the second names some cvars answer to); prvm_cmds.c VM_cvar_type as far as the flags
// of a cvar go.
using System.Globalization;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Menu;

/// <summary>
/// The one console a DarkPlaces client has: its cvars, its commands and aliases, its key bindings and its
/// command buffer - shared, as in DarkPlaces, by the menu program and by the client program of whatever
/// server is joined from that menu.
///
/// HOW THE STORES RELATE. There are now three, and they do not mix:
/// <list type="bullet">
/// <item>The player's native Vortex configuration. Nothing here reads or writes it.</item>
/// <item>This console: Xonotic's configuration as the PLAYER has it. It starts from Xonotic's
/// default.cfg and the player's own <c>config.cfg</c> under the legacy user folder, the Xonotic menu
/// changes it, and <see cref="SaveConfig"/> writes it back the way Host_SaveConfig does. It persists.</item>
/// <item>A legacy session started WITHOUT the Xonotic menu (the native browser, --legacy-connect) still
/// builds a private store of its own that dies with the session, as before.</item>
/// </list>
/// A session started FROM the Xonotic menu runs on this console, because the two programs talk to each
/// other through it (the client program sets a cvar and runs <c>menu_cmd directmenu TeamSelect</c>; the
/// menu's settings have to reach the running game). That would let a server reach the player's saved
/// configuration - it sends console commands, and its client program sets cvars freely - so everything
/// a session does is done between <see cref="EnterSession"/> and <see cref="LeaveSession"/>, every cvar
/// changed in there is remembered, and <see cref="EndSession"/> puts each one back to what the player had,
/// unless the player changed it again in the meantime. Commands that act outside the game (bind, connect,
/// quit, exec of the config, saveconfig) ask <see cref="SessionOrigin"/> and refuse a server's text.
/// </summary>
public sealed class LegacyConsole
{
    /// <summary>CONFIGFILENAME.</summary>
    public const string ConfigFileName = "config.cfg";

    /// <summary>
    /// Cvar_RegisterVirtual: (alias, cvar). An alias is a second name for the same variable - "showfps" IS
    /// cl_showfps - and Xonotic's menu and configuration use the aliases freely (the Video settings' frame
    /// rate box is "showfps", the player's name is set as "name" and read as "_cl_name"). The cvar store has
    /// one value per name, so the two are kept equal: a write to either is copied to the other. Only the
    /// real cvar is ever saved.
    /// </summary>
    public static readonly (string Alias, string Cvar)[] VirtualCvars =
    {
        ("name", "_cl_name"), ("_cl_rate", "rate"), ("_cl_rate_burstsize", "rate_burstsize"), ("_cl_pmodel", "pmodel"),
        ("shownetgraph", "net_graph"), ("_cl_playermodel", "playermodel"), ("_cl_playerskin", "playerskin"),
        ("cl_curl_enabled", "curl_enabled"), ("cl_curl_maxdownloads", "curl_maxdownloads"), ("cl_curl_maxspeed", "curl_maxspeed"),
        ("cl_curl_useragent", "curl_useragent"), ("cl_curl_useragent_append", "curl_useragent_append"),
        ("cl_netlocalping", "net_fakelag"), ("cl_netpacketloss_send", "net_fakeloss_send"),
        ("cl_netpacketloss_receive", "net_fakeloss_receive"), ("showfps", "cl_showfps"), ("showsound", "cl_showsound"),
        ("showblur", "cl_showblur"), ("showspeed", "cl_showspeed"), ("showtopspeed", "cl_showtopspeed"), ("showtime", "cl_showtime"),
        ("showtime_format", "cl_showtime_format"), ("showdate", "cl_showdate"), ("showdate_format", "cl_showdate_format"),
        ("showtex", "cl_showtex"), ("scr_sbaralpha", "sbar_alpha_bg"), ("slowmo", "host_timescale"), ("timescale", "host_timescale"),
        ("sv_ratelimitlocalplayer", "host_limitlocal"),
    };

    private readonly Dictionary<string, string> _aliasOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _aliasesFor = new(StringComparer.Ordinal);

    private const int MaxQueuedCommands = 65536;
    private const int MaxDeferred = 256;

    private readonly VirtualFileSystem _vfs;
    private readonly string? _userData;
    private readonly Action<string> _print;
    private readonly List<string> _queue = new();
    private readonly List<(double Delay, string Text)> _deferred = new();
    private readonly HashSet<string> _archived = new(StringComparer.Ordinal);
    private readonly HashSet<string> _engineCvars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _programCvars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _playerCommands = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _sessionSnapshot;
    private readonly HashSet<string> _sessionTouched = new(StringComparer.Ordinal);
    private int _sessionDepth;
    private bool _wait;
    private double _deferredOldTime = double.NaN;

    /// <param name="files">Xonotic's game data, mounted.</param>
    /// <param name="userDataDirectory">
    /// DarkPlaces' "user directory + data/": where config.cfg is read from and written to, and where a
    /// program's own files go. Null: nothing is read from or written to disk (a test, a probe).
    /// </param>
    public LegacyConsole(VirtualFileSystem files, string? userDataDirectory, Action<string>? print = null)
    {
        _vfs = files ?? throw new ArgumentNullException(nameof(files));
        _userData = userDataDirectory is null ? null : Path.GetFullPath(userDataDirectory);
        _print = print ?? (_ => { });

        Cvars = new CvarService();
        Interpreter = new ConfigInterpreter(Cvars, ReadConfigFile)
        {
            // Xonotic's configuration uses DarkPlaces' "${$1}" (makesaved, the menu's forced-saved list).
            NestedReferences = true,
            // The archive flag is the player's to give: a "seta" from a server does not make a cvar saved.
            CvarArchiveHook = name => { if (SessionOrigin) return; _archived.Add(name); Cvars.MarkArchived(name); },
            CvarDescriptionHook = Cvars.SetDescription,
        };
        Keys = new MenuKeyBindings();

        EngineCvarCount = CsqcEngineCvars.Register(Cvars);
        Cvars.Register("pr_checkextension", "1");
        Cvars.Register("utf8_enable", "1");
        Cvars.Register("developer", "0");
        foreach ((string name, int flags, string description) in MenuEngineCvarInfo.Table)
        {
            if (!Cvars.Has(name)) continue;
            _engineCvars.Add(name);
            if ((flags & MenuEngineCvarInfo.Archive) != 0) Cvars.MarkArchived(name);
            if (description.Length != 0) Cvars.SetDescription(name, description);
        }
        foreach ((string alias, string cvar) in VirtualCvars)
        {
            if (!Cvars.Has(cvar) || Cvars.Has(alias)) continue;   // "is a cvar": an alias never takes over a real one
            Cvars.Register(alias, Cvars.GetString(cvar));
            _aliasOf[alias] = cvar;
            if (!_aliasesFor.TryGetValue(cvar, out List<string>? list)) _aliasesFor[cvar] = list = new List<string>();
            list.Add(alias);
            string description = Cvars.GetDescription(cvar);
            if (description.Length != 0) Cvars.SetDescription(alias, description);
        }
        Cvars.Changed += OnCvarChanged;

        Keys.RegisterCommands(Interpreter, _print, () => !SessionOrigin);
        RegisterCommands();
    }

    public CvarService Cvars { get; }
    public ConfigInterpreter Interpreter { get; }
    public MenuKeyBindings Keys { get; }
    public VirtualFileSystem Files => _vfs;
    /// <summary>The directory config.cfg lives in, or null.</summary>
    public string? UserDataDirectory => _userData;
    public int EngineCvarCount { get; }
    /// <summary>True once default.cfg has been executed and the defaults locked.</summary>
    public bool ConfigLoaded { get; private set; }
    /// <summary>svs.maxclients_next: the player slots the next local server gets, as the last "maxplayers" command left it (DarkPlaces starts at 8).</summary>
    public int MaxPlayers { get; private set; } = 8;
    /// <summary>Lines run through the buffer so far.</summary>
    public long CommandsExecuted { get; private set; }

    /// <summary>Offered each command before the interpreter sees it; true means "done". For an owner's
    /// own commands that need the whole line rather than its arguments.</summary>
    public Func<string, bool>? CommandFilter { get; set; }

    // ---- whose text is running ---------------------------------------------------------------------

    /// <summary>True while a session's own work is running: a server's console text, its client program.</summary>
    public bool SessionOrigin => _sessionDepth > 0;

    /// <summary>A session begins on this console: remember every cvar as the player has it.</summary>
    public void BeginSession()
    {
        _sessionSnapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in Cvars.Names) _sessionSnapshot[name] = Cvars.GetString(name);
        _sessionTouched.Clear();
    }

    /// <summary>The session's own work starts (its datagrams are parsed, its program runs, its command
    /// buffer executes). Nested calls are counted.</summary>
    public void EnterSession() => _sessionDepth++;

    public void LeaveSession()
    {
        if (_sessionDepth > 0) _sessionDepth--;
    }

    /// <summary>
    /// The session is over: every cvar it changed goes back to what the player had when it began. A cvar
    /// the player (or the menu, which is the player's) changed during the session keeps that value. Cvars
    /// the session created stay, set to whatever they are; they are not the player's and are never saved
    /// unless the player's own configuration declared them. Returns how many were put back.
    /// </summary>
    public int EndSession()
    {
        _sessionDepth = 0;
        if (_sessionSnapshot is not { } snapshot) return 0;
        _sessionSnapshot = null;
        int restored = 0;
        foreach (string name in _sessionTouched)
        {
            if (!snapshot.TryGetValue(name, out string? value) || Cvars.GetString(name) == value) continue;
            Cvars.Set(name, value);
            restored++;
        }
        _sessionTouched.Clear();
        return restored;
    }

    /// <summary>
    /// A cvar the server program of a LOCAL game has set, offered to the player's store: taken only if it
    /// is the campaign's progress (<see cref="VortexArena.Legacy.Local.LegacyLocalCvars.IsCampaignProgress"/>),
    /// and then it is the player's own value - it is not put back when the session ends, and the menu's
    /// level list (menu/xonotic/campaign.qc XonoticCampaignList_draw compares the cvar with what it shows,
    /// every frame) unlocks the next level. It is not a saved cvar, in DarkPlaces either: the server program
    /// has written it to campaign.cfg, which <see cref="LoadConfig"/> executes at the next start.
    /// Never call this for a remote server: nothing a remote server does may reach the player's configuration.
    /// </summary>
    public bool AcceptLocalServerCvar(string name, string value)
    {
        if (!VortexArena.Legacy.Local.LegacyLocalCvars.IsCampaignProgress(name, value)) return false;
        // Whatever is running around this call, the change is made as the player's (see OnCvarChanged).
        int depth = _sessionDepth;
        _sessionDepth = 0;
        try
        {
            // The menu creates it the same way when its level list is first shown: registercvar(name, "", 0).
            if (!Cvars.Has(name))
            {
                Cvars.Register(name, "", CvarFlags.None);
                _programCvars.Add(name);
            }
            Cvars.Set(name, value);
            _sessionTouched.Remove(name);
            if (_sessionSnapshot is { } snapshot) snapshot[name] = value;
        }
        finally { _sessionDepth = depth; }
        return true;
    }

    private void OnCvarChanged(string name)
    {
        // One variable under two names: copy the write across. (Set does nothing when the value is already there.)
        if (_aliasOf.TryGetValue(name, out string? real)) Cvars.Set(real, Cvars.GetString(name));
        else if (_aliasesFor.TryGetValue(name, out List<string>? aliases))
            foreach (string alias in aliases) Cvars.Set(alias, Cvars.GetString(name));

        if (_sessionSnapshot is not { } snapshot) return;
        if (_sessionDepth > 0)
        {
            if (_sessionTouched.Count < 65536) _sessionTouched.Add(name);
        }
        else
        {
            // The player's own change, made while a session is up: it stands, and it is the new baseline.
            _sessionTouched.Remove(name);
            snapshot[name] = Cvars.GetString(name);
        }
    }

    /// <summary>
    /// Registers a command that a server's text may not run (connect, quit, saveconfig, ...): inside a
    /// session's own work it prints a refusal and does nothing.
    /// </summary>
    public void RegisterPlayerCommand(string name, Action<IReadOnlyList<string>> handler, string? description = null)
    {
        _playerCommands.Add(name);
        Interpreter.RegisterCommand(name, argv =>
        {
            if (SessionOrigin)
            {
                _print($"the server asked to run \"{name}\": not followed\n");
                return;
            }
            handler(argv);
        }, description);
    }

    // ---- the cvar facts a program asks for ---------------------------------------------------------

    /// <summary>The cvar_type bits (prvm_cmds.c VM_cvar_type): 1 exists, 2 saved, 4 private, 8 engine,
    /// 16 has a description, 32 read-only. 0 for a cvar that does not exist.</summary>
    public int CvarTypeFlags(string name)
    {
        if (name.Length == 0 || !Cvars.Has(name) || IsAbsentOnThisPlatform(name)) return 0;
        // An alias is its cvar, flags and all.
        if (_aliasOf.TryGetValue(name, out string? real)) name = real;
        int engine = MenuEngineCvarInfo.Flags(name);
        int flags = 1;
        if (Cvars.IsArchived(name)) flags |= 2;
        if ((engine & MenuEngineCvarInfo.Private) != 0) flags |= 4;
        // "!(cvar->flags & CF_ALLOCATED)": declared by the engine rather than created by "set".
        if (_engineCvars.Contains(name) || _programCvars.Contains(name)) flags |= 8;
        if (Cvars.GetDescription(name).Length != 0) flags |= 16;
        if ((engine & MenuEngineCvarInfo.ReadOnly) != 0) flags |= 32;
        return flags;
    }

    /// <summary>A cvar a program created with registercvar: not "allocated" in DarkPlaces' sense.</summary>
    public void NoteProgramCvar(string name) => _programCvars.Add(name);

    /// <summary>
    /// An engine cvar DarkPlaces declares only on another platform. The cvar table here is every
    /// declaration in the C sources; a menu asks "does this cvar exist" to decide whether to offer a
    /// setting (cvar_type), and "Disable system mouse acceleration" exists only on macOS.
    /// </summary>
    public static bool IsAbsentOnThisPlatform(string name) => name == "apple_mouse_noaccel" && !OperatingSystem.IsMacOS();

    /// <summary>CF_PRIVATE: hidden from QuakeC (rcon_password and its restricted sibling).</summary>
    public static bool IsPrivateCvar(string name) => (MenuEngineCvarInfo.Flags(name) & MenuEngineCvarInfo.Private) != 0;

    // ---- start-up and saving -----------------------------------------------------------------------

    // FS_LoadFile for "exec": the user directory first (that is where config.cfg is), then the game data.
    private string? ReadConfigFile(string path)
    {
        if (!LegacyQcHost.IsSafePath(path)) return null;
        if (UserFile(path) is { } user && File.Exists(user))
        {
            try
            {
                // Shared for writing too (fs.c opens with _SH_DENYNO): a program may still hold the file open -
                // Xonotic's server never closes the campaign.cfg it has written.
                using FileStream stream = new(user, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        try { return _vfs.Exists(path) ? _vfs.ReadText(path) : null; }
        catch (IOException) { return null; }
    }

    private string? UserFile(string path)
    {
        if (_userData is null) return null;
        string full = Path.GetFullPath(Path.Combine(_userData, path));
        return full.StartsWith(_userData + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// The start-up scripts, in Xonotic's quake.rc order as far as a client is concerned: default.cfg
    /// (after which the cvar defaults are locked - what "changed" means when saving), config.cfg, the
    /// campaign state, config_update.cfg, the font configuration, autoexec.cfg. False if the data has
    /// no default.cfg, in which case nothing was run.
    /// </summary>
    /// <remarks>
    /// Run directly, not through the buffer: these are files of the player's own installation.
    /// <c>stuffcmds</c>, <c>crypto_keygen</c> and post-config.cfg of quake.rc are not run (no
    /// DarkPlaces command line, no identity, and the last is for dedicated servers).
    /// </remarks>
    public bool LoadConfig()
    {
        // "alias startmap_sp/startmap_dm" are Host_AddConfigText's; a mod may override them.
        Interpreter.DefineAlias("startmap_sp", "map start");
        Interpreter.DefineAlias("startmap_dm", "map start");
        if (!Interpreter.ExecuteFile("default.cfg")) return false;
        // Cmd_Exec: "if executing default.cfg for the first time, lock the cvar defaults".
        Cvars.LockDefaults();
        ConfigLoaded = true;
        Interpreter.ExecuteFile(ConfigFileName);
        Interpreter.ExecuteFile("data/campaign.cfg");
        Interpreter.ExecuteFile("config_update.cfg");
        Interpreter.ExecuteFile("font-xolonium.cfg");
        Interpreter.ExecuteFile("autoexec.cfg");
        return true;
    }

    /// <summary>
    /// Host_SaveConfig: the key bindings (Key_WriteBindings), then every saved cvar that is not at its
    /// default (Cvar_WriteVariables). Written to a temporary file first, so a crash cannot leave half a
    /// configuration. Returns the path written, or null if there is nowhere to write or the defaults
    /// were never loaded ("don't save a config if it crashed in startup").
    /// </summary>
    public string? SaveConfig(string fileName = ConfigFileName)
    {
        if (!ConfigLoaded || !LegacyQcHost.IsSafePath(fileName) || UserFile(fileName) is not { } path) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            using (StreamWriter file = new(temporary, false, new UTF8Encoding(false)) { NewLine = "\n" })
                WriteConfig(file);
            File.Move(temporary, path, overwrite: true);
            _print($"Saving config to {fileName} ...\n");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _print($"Couldn't write {fileName}\n");
            return null;
        }
    }

    /// <summary>What <see cref="SaveConfig"/> writes.</summary>
    public void WriteConfig(TextWriter file)
    {
        Keys.Write(file);
        // Cvar_WriteVariables: 'CF_ARCHIVE and (string != defstring or (CF_ALLOCATED and not
        // CF_DEFAULTSET))' - which is exactly ArchivedNamesToPersist. An engine cvar is written bare,
        // one a configuration created with "seta" in front. While a session is up, a cvar it changed is
        // written as the player had it.
        HashSet<string> names = new(Cvars.ArchivedNamesToPersist, StringComparer.Ordinal);
        Dictionary<string, string>? snapshot = _sessionSnapshot;
        if (snapshot is not null)
            foreach (string touched in _sessionTouched)
                if (Cvars.IsArchived(touched)) names.Add(touched);
        List<string> ordered = new(names);
        ordered.Sort(StringComparer.Ordinal);
        foreach (string name in ordered)
        {
            if (IsPrivateCvar(name)) continue;
            string value = Cvars.GetString(name);
            if (snapshot is not null && _sessionTouched.Contains(name))
            {
                // Not the player's value: write the one the player had, if it is worth writing at all.
                if (!snapshot.TryGetValue(name, out string? own) || own == Cvars.GetDefault(name)) continue;
                value = own;
            }
            string prefix = _engineCvars.Contains(name) ? "" : "seta ";
            file.Write($"{prefix}\"{MenuKeyBindings.Quote(name, "\"\\$")}\" \"{MenuKeyBindings.Quote(value, "\"\\$")}\"\n");
        }
    }

    // ---- the command buffer ------------------------------------------------------------------------

    /// <summary>
    /// Cbuf_AddText: append text to the end of the buffer. The text is NOT cut into commands here: DarkPlaces'
    /// buffer is one run of characters, and a program may build a single command out of several calls -
    /// Xonotic's menu starts a campaign level with <c>localcmd("set _campaign_name \"")</c>,
    /// <c>localcmd(name)</c>, then a call with the closing quote and the line end. Cut up per call, that was a <c>set</c> with an
    /// unterminated quote, an unknown command and a stray quote, and the campaign started with no name.
    /// The buffer is cut into commands when it is run (<see cref="Execute"/>), as Cbuf_Execute does.
    /// </summary>
    public void AddText(string text)
    {
        if (string.IsNullOrEmpty(text) || _unsplit.Length + text.Length > MaxUnsplitCharacters) return;
        _unsplit.Append(text);
    }

    // The text added since the buffer was last cut into commands.
    private const int MaxUnsplitCharacters = 4 * 1024 * 1024;
    private readonly StringBuilder _unsplit = new();

    private void SplitAddedText()
    {
        if (_unsplit.Length == 0) return;
        string text = _unsplit.ToString();
        _unsplit.Clear();
        foreach (string command in ConfigInterpreter.SplitIntoCommands(text))
            if (_queue.Count < MaxQueuedCommands) _queue.Add(command);
    }

    /// <summary>Cbuf_InsertText: queue text at the front (a key's bind runs before what is already waiting).</summary>
    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        List<string> commands = ConfigInterpreter.SplitIntoCommands(text);
        if (_queue.Count + commands.Count <= MaxQueuedCommands) _queue.InsertRange(0, commands);
    }

    /// <summary>
    /// Cbuf_Frame: move the deferred commands whose time has come into the buffer, then run the buffer
    /// until it is empty or a <c>wait</c> stops it for this frame.
    /// </summary>
    /// <param name="realTime">host.realtime.</param>
    public void Execute(double realTime)
    {
        // Cbuf_Execute_Deferred.
        if (double.IsNaN(_deferredOldTime) || realTime - _deferredOldTime < 0 || realTime - _deferredOldTime > 1800) _deferredOldTime = realTime;
        double eat = realTime - _deferredOldTime;
        if (eat >= 1.0 / 128.0)
        {
            _deferredOldTime = realTime;
            for (int i = 0; i < _deferred.Count; i++)
            {
                (double delay, string text) = _deferred[i];
                delay -= eat;
                if (delay <= 0)
                {
                    // "parse deferred string and append its cmdstring(s)", with "pending = false": a deferred
                    // string is whole commands of its own. Appended as bare text, four that came due in one
                    // frame ran together into one unknown word - the menu's Leave button is
                    // "defer 0.4 disconnect; defer 0.4 wait; defer 0.4 "g_campaign 0"; defer 0.4 menu_sync",
                    // and it closed the menu and left nothing.
                    AddText("\n" + text + "\n");
                    _deferred.RemoveAt(i--);
                }
                else _deferred[i] = (delay, text);
            }
        }

        _wait = false;
        SplitAddedText();
        // Bounded: a command that queues itself again costs one frame of work, not a hang.
        for (int budget = 16384; budget > 0 && !_wait; budget--)
        {
            // What a command added while it ran (a program's localcmd, an alias) runs in this same pass.
            if (_queue.Count == 0) SplitAddedText();
            if (_queue.Count == 0) break;
            string command = _queue[0];
            _queue.RemoveAt(0);
            ExecuteNow(command);
        }
    }

    /// <summary>Cmd_ExecuteString: run one command now, bypassing the buffer.</summary>
    public void ExecuteNow(string command)
    {
        CommandsExecuted++;
        if (CommandFilter?.Invoke(command) == true) return;
        Interpreter.ExecuteLine(command);
    }

    /// <summary>Commands waiting in the buffer.</summary>
    public int Pending
    {
        get
        {
            SplitAddedText();
            return _queue.Count;
        }
    }

    private void RegisterCommands()
    {
        // Cmd_Wait_f: "make remaining commands wait until next frame". Only the buffer can wait: a wait
        // inside an alias body stops the buffer AFTER the alias has run to its end, where the C would
        // stop in the middle of it.
        Interpreter.RegisterCommand("wait", _ => _wait = true, "make script execution wait for next rendered frame");

        Interpreter.RegisterCommand("defer", argv =>
        {
            if (argv.Count == 1)
            {
                if (_deferred.Count == 0) _print("No commands are pending.\n");
                foreach ((double delay, string text) in _deferred)
                    _print(string.Create(CultureInfo.InvariantCulture, $"-> In {delay,9:0.00}: {text}\n"));
            }
            else if (argv.Count == 2 && argv[1].Equals("clear", StringComparison.OrdinalIgnoreCase)) _deferred.Clear();
            else if (argv.Count == 3 && argv[2].Length != 0)
            {
                if (_deferred.Count < MaxDeferred) _deferred.Add((QcNumber(argv[1]), argv[2]));
            }
            else _print("usage: defer <seconds> <command>\n       defer clear\n");
        }, "execute a command in the future");

        Interpreter.RegisterCommand("toggle", Toggle, "toggles a console variable's values (use for more info)");

        Interpreter.RegisterCommand("echo", argv => _print(string.Join(' ', argv.Skip(1)) + "\n"), "print a message to the console (useful in scripts)");

        Interpreter.RegisterCommand("cvar_lockdefaults", _ => { }, "stores the current values of all cvars into their default values, only used once during startup after parsing default.cfg");

        // Three names the interpreter keeps on its list of words that are never cvars (they are commands in
        // the native game). In DarkPlaces the first two are cvars and the third a command that only a
        // server acts on; Xonotic's configuration and menu set them like any other.
        foreach (string cvar in new[] { "prvm_language", "rcon_secure", "name", "playermodel", "playerskin" })
        {
            string name = cvar;
            Interpreter.RegisterCommand(name, argv =>
            {
                if (argv.Count >= 2) Cvars.Set(name, argv[1]);
                else _print($"\"{name}\" is \"{Cvars.GetString(name)}\"\n");
            }, "engine cvar");
        }
        // sv_ccmds.c SV_MaxPlayers_f: a command, not a cvar - it sizes svs.clients for the NEXT server
        // (svs.maxclients_next). Xonotic's configuration issues one (16) and its menu another before each
        // "map"; whoever starts the local server reads the number here.
        Interpreter.RegisterCommand("maxplayers", argv =>
        {
            if (argv.Count != 2)
            {
                _print($"\"maxplayers\" is \"{MaxPlayers}\"\n");
                return;
            }
            if (SessionOrigin) return;   // a server's text does not size the player's next local game
            MaxPlayers = Math.Clamp((int)QcNumber(argv[1]), 1, 255);
        }, "sets limit on how many players (or bots) may be connected to the server at once");
        // cl_cmd.c CL_Color_f: "color <shirt> [pants]" (one number is both); stored as shirt * 16 + pants.
        Interpreter.RegisterCommand("color", argv =>
        {
            if (argv.Count < 2)
            {
                int current = (int)Cvars.GetFloat("_cl_color");
                _print($"\"color\" is \"{current >> 4} {current & 15}\"\ncolor <0-15> [0-15]\n");
                return;
            }
            int top = (int)QcNumber(argv[1]), bottom = argv.Count >= 3 ? (int)QcNumber(argv[2]) : top;
            Cvars.Set("_cl_color", (((top & 15) << 4) | (bottom & 15)).ToString(CultureInfo.InvariantCulture));
        }, "change your player shirt and pants colors");

        RegisterPlayerCommand("saveconfig", argv => SaveConfig(argv.Count > 1 ? argv[1] : ConfigFileName),
            "save settings to config.cfg (or a specified filename) immediately (also automatic when quitting)");

        // Cvar_ResetToDefault_f, as "cvar_resettodefaults_*" and the menu's "reset" buttons use it.
        Interpreter.RegisterCommand("cvar_resettodefaults_all", _ => ResetToDefaults(_ => true), "sets all cvars to their locked default values");
        Interpreter.RegisterCommand("cvar_resettodefaults_nosaveonly", _ => ResetToDefaults(name => !Cvars.IsArchived(name)), "sets all non-saved cvars to their locked default values (variables that will not be saved to config.cfg)");
        Interpreter.RegisterCommand("cvar_resettodefaults_saveonly", _ => ResetToDefaults(Cvars.IsArchived), "sets all saved cvars to their locked default values (variables that will be saved to config.cfg)");
    }

    private void ResetToDefaults(Func<string, bool> which)
    {
        if (SessionOrigin) return;
        foreach (string name in Cvars.Names)
            if (which(name) && (MenuEngineCvarInfo.Flags(name) & MenuEngineCvarInfo.ReadOnly) == 0) Cvars.ResetToDefault(name);
    }

    private static double QcNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0;

    // Cmd_Toggle_f.
    private void Toggle(IReadOnlyList<string> argv)
    {
        if (argv.Count == 1)
        {
            _print("Toggle Console Variable - Usage\n  toggle <variable> - toggles between 0 and 1\n  toggle <variable> <value> - toggles between 0 and <value>\n  toggle <variable> [string 1] [string 2]...[string n] - cycles through all strings\n");
            return;
        }
        string name = argv[1];
        if (!Cvars.Has(name))
        {
            _print($"ERROR : CVar '{name}' not found\n");
            return;
        }
        if ((MenuEngineCvarInfo.Flags(name) & MenuEngineCvarInfo.ReadOnly) != 0) return;
        int integer = QcVm.FloatToInt(Cvars.GetFloat(name));
        if (argv.Count == 2) Cvars.Set(name, integer != 0 ? "0" : "1");
        else if (argv.Count == 3)
        {
            // "CVar is Specified Value: reset to 0; CVar is 0: specify value; else reset to 0"
            int wanted = (int)QcNumber(argv[2]);
            Cvars.Set(name, integer == wanted ? "0" : integer == 0 ? argv[2] : "0");
        }
        else
        {
            string current = Cvars.GetString(name);
            for (int i = 2; i < argv.Count; i++)
            {
                if (argv[i] != current) continue;
                Cvars.Set(name, i + 1 == argv.Count ? argv[2] : argv[i + 1]);
                return;
            }
            Cvars.Set(name, argv[2]);
        }
    }
}

// Port of Base/darkplaces/mvm_cmds.c: the registration of vm_m_builtins[] and VM_M_setmousetarget,
// VM_M_getmousetarget, VM_M_setkeydest, VM_M_getkeydest, VM_M_getresolution, VM_M_getgamedirinfo,
// VM_M_WriteByte .. VM_M_WriteEntity (VM_M_WriteDest), VM_M_copyentity, VM_M_getmousepos, the
// VM_M_crypto_* family, VM_cin_open / close / setstate / getstate / restart, VM_M_registercommand;
// prvm_cmds.c VM_bprint, VM_sprint, VM_centerprint, VM_precache_sound, VM_isserver, VM_clientcount,
// VM_clientstate, VM_changelevel, VM_localsound, VM_crash, VM_stackdump, VM_chr, VM_itof,
// VM_altstr_count / prepare / get / set / ins, VM_keynumtostring, VM_stringtokeynum, VM_getkeybind,
// VM_setkeybind, VM_getbindmaps, VM_setbindmaps, VM_findkeysforcommand, VM_CL_isdemo,
// VM_CL_videoplaying, VM_CL_getextresponse, VM_netaddress_resolve, VM_uri_get, VM_getsoundtime,
// VM_soundlength, VM_gecko_*; menu.c video_resolutions_hardcoded[].
using System.Text;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Menu;

public sealed partial class MenuHost
{
    private const int FindKeysForCommandKeys = 5; // FKFC_NUMKEYS

    private readonly long[] _calls = new long[700];
    private readonly Dictionary<string, QcBuiltin> _own = new(StringComparer.Ordinal);

    /// <summary>The C functions of vm_m_builtins[] this host has no implementation for: none are
    /// expected. A program calling one is counted in <see cref="UnimplementedBuiltins"/>.</summary>
    public IReadOnlyList<(int Number, string Function)> MissingBuiltins => _missing;
    private readonly List<(int Number, string Function)> _missing = new();

    /// <summary>For each registered number, where its implementation lives: "core", "strings", "draw" or "menu".</summary>
    public IReadOnlyDictionary<int, string> BuiltinSources => _sources;
    private readonly Dictionary<int, string> _sources = new();

    // menu.c video_resolutions_hardcoded[]: what getresolution lists for a window (and for full screen
    // when the video system reports no modes).
    private static readonly MenuVideoMode[] HardcodedModes =
    {
        new(320, 240), new(400, 300), new(512, 384), new(640, 480), new(800, 600), new(1024, 768), new(1152, 864),
        new(1280, 960), new(1400, 1050), new(1600, 1200), new(1792, 1344), new(1856, 1392), new(1920, 1440),
        new(2048, 1536), new(320, 256, 0.9375f), new(640, 512, 0.9375f), new(1280, 1024, 0.9375f), new(320, 200, 1.2f),
        new(640, 400, 1.2f), new(840, 525, 1.2f), new(960, 600, 1.2f), new(1680, 1050, 1.2f), new(1920, 1200, 1.2f),
        new(320, 256), new(640, 512), new(1280, 1024), new(640, 384), new(1280, 768), new(320, 200), new(640, 400),
        new(720, 450), new(840, 525), new(960, 600), new(1280, 800), new(1440, 900), new(1680, 1050), new(1920, 1200),
        new(2560, 1600), new(3840, 2400), new(840, 540), new(1680, 1080), new(640, 360), new(683, 384), new(960, 540),
        new(1280, 720), new(1360, 768), new(1366, 768), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160),
        new(360, 240, 1.125f), new(720, 480, 1.125f), new(360, 283, 0.9545f), new(720, 566, 0.9545f),
        new(256, 224, 1.1667f), new(512, 448, 1.1667f),
    };

    private void RegisterBuiltins()
    {
        LegacyDrawBuiltins draw = new(Vm, Draw, Services, _globalDrawFont, _globalDrawFontScale);
        Dictionary<string, QcBuiltin> drawing = new(StringComparer.Ordinal)
        {
            ["VM_iscachedpic"] = draw.IsCachedPic, ["VM_precache_pic"] = draw.PrecachePic, ["VM_freepic"] = draw.FreePic,
            ["VM_drawcharacter"] = draw.DrawCharacter, ["VM_drawstring"] = draw.DrawString, ["VM_drawpic"] = draw.DrawPic,
            ["VM_drawfill"] = draw.DrawFill, ["VM_drawsetcliparea"] = draw.DrawSetClipArea,
            ["VM_drawresetcliparea"] = draw.DrawResetClipArea, ["VM_getimagesize"] = draw.GetImageSize,
            ["VM_drawline"] = draw.DrawLine, ["VM_drawcolorcodedstring"] = draw.DrawColorCodedString,
            ["VM_stringwidth"] = draw.StringWidth, ["VM_drawsubpic"] = draw.DrawSubPic, ["VM_drawrotpic"] = draw.DrawRotPic,
            ["VM_findfont"] = draw.FindFont, ["VM_loadfont"] = draw.LoadFont,
        };
        AddOwn();

        foreach ((int number, string function) in MenuBuiltinTable.Entries)
        {
            QcBuiltin? builtin;
            string source;
            // The menu's own first: where a shared class has a builtin of the same name with the client
            // table's behaviour (VM_CL_copyentity for copyentity), the menu table's function wins.
            if (_own.TryGetValue(function, out builtin)) source = "menu";
            else if (drawing.TryGetValue(function, out builtin)) source = "draw";
            else
            {
                // #229 is VM_strncasecmp taking two parameters: the shared class has it under the QuakeC name.
                string name = number == 229 ? "strcasecmp" : MenuBuiltinTable.SharedName(function);
                builtin = _core.Find(name);
                source = "core";
                if (builtin is null)
                {
                    builtin = _strings.Find(name);
                    source = "strings";
                }
            }
            if (builtin is null)
            {
                _missing.Add((number, function));
                continue;
            }
            _sources[number] = source;
            long[] calls = _calls;
            QcBuiltin target = builtin;
            Vm.RegisterBuiltin(number, vm =>
            {
                calls[number]++;
                target(vm);
            });
        }
    }

    private void AddOwn()
    {
        _own["VM_bprint"] = BPrint;
        _own["VM_sprint"] = SPrint;
        _own["VM_centerprint"] = CenterPrint;
        _own["VM_precache_sound"] = PrecacheSound;
        _own["VM_M_copyentity"] = CopyEntity;
        _own["VM_isserver"] = vm => { Parms(0, "VM_isserver"); vm.ReturnFloat(ServerActive ? 1 : 0); };
        _own["VM_clientcount"] = vm => { Parms(0, "VM_clientcount"); vm.ReturnFloat(_options.MaxClients?.Invoke() ?? 1); };
        _own["VM_clientstate"] = ClientState;
        _own["VM_changelevel"] = ChangeLevel;
        _own["VM_localsound"] = LocalSound;
        _own["VM_M_getmousepos"] = GetMousePos;
        _own["VM_crash"] = vm => { Parms(0, "VM_crash"); throw Fault("Crash called by menu"); };
        _own["VM_stackdump"] = vm => { Parms(0, "VM_stackdump"); Services.Print(vm.StackTrace() + "\n"); };
        _own["VM_chr"] = Chr;
        _own["VM_itof"] = vm => { Parms(1, "VM_itof"); vm.ReturnFloat(vm.ArgInt(0)); };
        _own["VM_altstr_count"] = AltStrCount;
        _own["VM_altstr_prepare"] = AltStrPrepare;
        _own["VM_altstr_get"] = AltStrGet;
        _own["VM_altstr_set"] = AltStrSet;
        _own["VM_altstr_ins"] = AltStrIns;
        _own["VM_keynumtostring"] = vm => { Parms(1, "VM_keynumtostring"); vm.ReturnString(CsqcKeys.KeynumToString(ArgInt(0))); };
        _own["VM_stringtokeynum"] = vm => { Parms(1, "VM_stringtokeynum"); vm.ReturnFloat(CsqcKeys.StringToKeynum(vm.ArgString(0))); };
        _own["VM_getkeybind"] = GetKeyBind;
        _own["VM_setkeybind"] = SetKeyBind;
        _own["VM_getbindmaps"] = GetBindMaps;
        _own["VM_setbindmaps"] = SetBindMaps;
        _own["VM_findkeysforcommand"] = FindKeysForCommand;
        _own["VM_CL_isdemo"] = vm => { Parms(0, "VM_CL_isdemo"); vm.ReturnFloat(_options.DemoPlayback?.Invoke() == true ? 1 : 0); };
        _own["VM_M_registercommand"] = RegisterCommand;
        // cl_videoplaying: there is no video player, so never.
        _own["VM_CL_videoplaying"] = vm => { Parms(0, "VM_CL_videoplaying"); vm.ReturnFloat(0); };
        foreach (string write in new[] { "Byte", "Char", "Short", "Long", "Angle", "Coord", "String", "Entity" })
        {
            string name = "VM_M_Write" + write;
            _own[name] = vm => { Parms(1, name); WriteDest(); };
        }
        _own["VM_cin_open"] = CinOpen;
        _own["VM_cin_close"] = vm => { Parms(1, "VM_cin_close"); CheckEmptyString(vm.ArgString(0)); };
        _own["VM_cin_setstate"] = vm => { Parms(2, "VM_cin_setstate"); CheckEmptyString(vm.ArgString(0)); };
        _own["VM_cin_getstate"] = vm => { Parms(1, "VM_cin_getstate"); CheckEmptyString(vm.ArgString(0)); vm.ReturnFloat(0); };
        _own["VM_cin_restart"] = vm => { Parms(1, "VM_cin_restart"); CheckEmptyString(vm.ArgString(0)); };
        // "REMOVED" in DarkPlaces itself: the Gecko web view builtins all answer 0.
        foreach (string gecko in new[] { "create", "destroy", "navigate", "keyevent", "movemouse", "resize" })
            _own["VM_gecko_" + gecko] = vm => vm.ReturnFloat(0);
        _own["VM_gecko_get_texture_extent"] = vm => vm.ReturnVector(default);
        _own["VM_uri_get"] = UriGet;
        _own["VM_getsoundtime"] = GetSoundTime;
        _own["VM_soundlength"] = vm => { Parms(1, "VM_soundlength"); vm.ReturnFloat(_options.Sound?.Length(vm.ArgString(0)) ?? -1); };
        _own["VM_M_setkeydest"] = SetKeyDest;
        _own["VM_M_getkeydest"] = GetKeyDest;
        _own["VM_M_setmousetarget"] = SetMouseTarget;
        _own["VM_M_getmousetarget"] = vm => { Parms(0, "VM_M_getmousetarget"); vm.ReturnFloat(ClientMouse ? 2 : 1); };
        _own["VM_M_getresolution"] = GetResolution;
        _own["VM_M_getgamedirinfo"] = GetGameDirInfo;
        _own["VM_CL_getextresponse"] = GetExtResponse;
        _own["VM_netaddress_resolve"] = NetAddressResolve;
        _own["VM_M_crypto_getkeyfp"] = vm => CryptoHostKey(vm, "VM_M_crypto_getkeyfp");
        _own["VM_M_crypto_getidfp"] = vm => CryptoHostKey(vm, "VM_M_crypto_getidfp");
        _own["VM_M_crypto_getencryptlevel"] = vm => CryptoHostKey(vm, "VM_M_crypto_getencryptlevel");
        _own["VM_M_crypto_getidstatus"] = vm => { Parms(1, "VM_M_crypto_getidstatus"); CheckEmptyString(vm.ArgString(0)); vm.ReturnFloat(0); };
        _own["VM_M_crypto_getmykeyfp"] = vm => CryptoLocalKey(vm, "VM_M_crypto_getmykeyfp");
        _own["VM_M_crypto_getmyidfp"] = vm => CryptoLocalKey(vm, "VM_M_crypto_getmyidfp");
        _own["VM_M_crypto_getmyidstatus"] = CryptoMyIdStatus;
        AddServerListBuiltins();
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private QcRuntimeException Fault(string message) => new($"{Vm.Name}: {message}");

    private void Warning(string message) => Services.Warning($"{Vm.Name} VM warning: {message}");

    // VM_SAFEPARMCOUNT: a wrong argument count is fatal to the program, which is also what stops a
    // builtin from reading parameter cells the caller never wrote.
    private void Parms(int count, string name)
    {
        if (Vm.ArgCount != count) throw Fault($"{name} wrong parameter count {Vm.ArgCount} ({count} expected ) !");
    }

    private void Parms(int min, int max, string name)
    {
        if (Vm.ArgCount < min || Vm.ArgCount > max)
            throw Fault($"{name} wrong parameter count {Vm.ArgCount} ({min} to {max} expected ) !");
    }

    // VM_CheckEmptyString.
    private void CheckEmptyString(string s)
    {
        if (s.Length == 0 || s[0] is ' ' or '\t' or '\r' or '\n') throw Fault("Bad string");
    }

    // VM_VarString: arguments from `first` on, concatenated, cut at the tempstring size.
    private string VarString(int first)
    {
        int count = Math.Min(Vm.ArgCount, ProgsFile.MaxParms);
        StringBuilder text = new();
        int room = Vm.MaxStringLength - 1;
        for (int i = first; i < count && text.Length < room; i++)
        {
            string s = Vm.ArgString(i);
            text.Append(text.Length + s.Length <= room ? s : s[..(room - text.Length)]);
        }
        return text.ToString();
    }

    private int ArgInt(int index) => QcVm.FloatToInt(Vm.ArgFloat(index));

    // A string result that may be the null string (OFS_NULL) rather than an empty one.
    private void ReturnStringOrNull(string? text)
    {
        if (text is null) Vm.ReturnInt(0);
        else Vm.ReturnString(text);
    }

    // The tempstring a builtin builds in a "char outstr[VM_TEMPSTRING_MAXSIZE]".
    private int Room => Vm.MaxStringLength - 1;

    // ---- server-side prints: a menu has a server only when the engine is hosting one ---------------

    // void(string s, ...) bprint
    private void BPrint(QcVm vm)
    {
        if (!ServerActive)
        {
            Warning("VM_bprint: server is not active!\n");
            return;
        }
        Services.Print(VarString(0));
    }

    // void(float clientnum, string s, ...) sprint
    private void SPrint(QcVm vm)
    {
        Parms(1, 8, "VM_sprint");
        // There is no server whose clients a menu could print to.
        Warning("VM_sprint: invalid client or server is not active!\n");
    }

    // void(string s, ...) centerprint
    private void CenterPrint(QcVm vm)
    {
        Parms(1, 8, "VM_centerprint");
        _options.CenterPrint?.Invoke(VarString(0));
    }

    // VM_M_WriteDest: "game is not server" is a program error.
    private void WriteDest()
    {
        if (!ServerActive) throw Fault("VM_M_WriteDest: game is not server (menu)");
        // With a local server the C writes into its message buffers. This host's servers are not
        // reachable from the menu VM; Xonotic's menu never calls these.
        throw Fault("WriteDest: bad destination");
    }

    // void(string map) changelevel
    private void ChangeLevel(QcVm vm)
    {
        Parms(1, "VM_changelevel");
        if (!ServerActive)
        {
            Warning("VM_changelevel: server is not active!\n");
            return;
        }
        Services.LocalCommand($"changelevel {vm.ArgString(0)}\n");
    }

    // ---- client state ------------------------------------------------------------------------------

    // float() clientstate: 0 uninitialised or dedicated, 1 disconnected, 2 connected.
    private void ClientState(QcVm vm)
    {
        Parms(0, "VM_clientstate");
        vm.ReturnFloat(_options.Connected?.Invoke() == true ? 2 : 1);
    }

    // #47 void(entity src, entity dst) copyentity: a plain copy of every field, with none of the
    // world-entity and free-entity checks the client table's VM_CL_copyentity makes.
    private void CopyEntity(QcVm vm)
    {
        Parms(2, "VM_M_copyentity");
        int from = vm.ArgEdict(0), to = vm.ArgEdict(1);
        for (int i = 0; i < vm.EntityFields; i++) vm.FieldInt(to, i) = vm.FieldInt(from, i);
    }

    // ---- sound -------------------------------------------------------------------------------------

    // string(string s) precache_sound: its argument; a sample that cannot be loaded is only a warning.
    private void PrecacheSound(QcVm vm)
    {
        Parms(1, "VM_precache_sound");
        string sample = vm.ArgString(0);
        vm.ReturnInt(vm.ArgInt(0));
        // "snd_initialized.integer &&": without a sound system nothing is loaded and nothing is said.
        if (_options.Sound is { } sound && !sound.Precache(sample)) Warning($"VM_precache_sound: Failed to load {sample} !\n");
    }

    // void(string sample[, float channel, float volume]) localsound
    private void LocalSound(QcVm vm)
    {
        Parms(1, 3, "VM_localsound");
        string sample = vm.ArgString(0);
        int channel = 0;
        float volume = 1;
        if (vm.ArgCount == 3)
        {
            channel = ArgInt(1);
            volume = vm.ArgFloat(2) == 0 ? 1 : vm.ArgFloat(2);
        }
        if (_options.Sound is not { } sound || !sound.Local(sample, channel, volume))
        {
            vm.ReturnFloat(-4);
            Warning($"VM_localsound: Failed to play {sample} !\n");
            return;
        }
        vm.ReturnFloat(1);
    }

    // float(entity e, float channel) getsoundtime: "not supported on this progs".
    private void GetSoundTime(QcVm vm)
    {
        Parms(2, "VM_getsoundtime");
        Warning("VM_getsoundtime: not supported on this progs\n");
        vm.ReturnFloat(-1);
    }

    // ---- keys, mouse, video ------------------------------------------------------------------------

    // #601 void(float dest) setkeydest: 0 game, 2 menu, 3 menu with the keyboard grabbed. 1 (the chat
    // line) is "wrong destination", as in the C.
    private void SetKeyDest(QcVm vm)
    {
        Parms(1, "VM_M_setkeydest");
        switch (ArgInt(0))
        {
            case 0: KeyDest = MenuKeyDest.Game; break;
            case 2: KeyDest = MenuKeyDest.Menu; break;
            case 3: KeyDest = MenuKeyDest.MenuGrabbed; break;
            default: throw Fault($"VM_M_setkeydest: wrong destination {vm.ArgFloat(0)} !");
        }
    }

    // #602 float() getkeydest: -1 for the chat line ("not supported").
    private void GetKeyDest(QcVm vm)
    {
        Parms(0, "VM_M_getkeydest");
        vm.ReturnFloat(KeyDest switch { MenuKeyDest.Game => 0, MenuKeyDest.Menu => 2, MenuKeyDest.MenuGrabbed => 3, _ => -1 });
    }

    // #603 void(float target) setmousetarget: 1 (MT_MENU) relative movement, 2 (MT_CLIENT) the window's pointer.
    private void SetMouseTarget(QcVm vm)
    {
        Parms(1, "VM_M_setmousetarget");
        switch (ArgInt(0))
        {
            case 1: ClientMouse = false; break;
            case 2: ClientMouse = true; break;
            default: throw Fault($"VM_M_setmousetarget: wrong destination {vm.ArgFloat(0)} !");
        }
    }

    // #66 vector() getmousepos: zero while the console is down or the menu does not have the keys;
    // otherwise the pointer (or its movement) scaled from window pixels to the virtual screen.
    private void GetMousePos(QcVm vm)
    {
        Parms(0, "VM_M_getmousepos");
        if (_options.ConsoleActive?.Invoke() == true || KeyDest is not (MenuKeyDest.Menu or MenuKeyDest.MenuGrabbed))
        {
            vm.ReturnVector(default);
            return;
        }
        (float width, float height) = _options.VideoSize?.Invoke() ?? (640, 480);
        if (!(width >= 1) || !(height >= 1))
        {
            vm.ReturnVector(default);
            return;
        }
        (float x, float y) = (ClientMouse ? _options.WindowMouse?.Invoke() : _options.MouseDelta?.Invoke()) ?? (0, 0);
        // vid_conwidth.integer / vid_conheight.integer
        int conWidth = QcVm.FloatToInt(Services.CvarFloat("vid_conwidth")), conHeight = QcVm.FloatToInt(Services.CvarFloat("vid_conheight"));
        vm.ReturnVector(new QcVector(x * conWidth / width, y * conHeight / height, 0));
    }

    // #608 vector(float number[, float forfullscreen]) getresolution: width, height and pixel aspect of
    // mode `number`; -1 is the desktop's; past the end is '0 0 0'.
    private void GetResolution(QcVm vm)
    {
        Parms(1, 2, "VM_M_getresolution");
        int number = ArgInt(0);
        bool fullScreen = vm.ArgCount <= 1 || ArgInt(1) != 0;
        IReadOnlyList<MenuVideoMode> modes = fullScreen && _options.VideoModes.Count > 0 ? _options.VideoModes : HardcodedModes;
        if (number < -1 || number >= modes.Count) vm.ReturnVector(default);
        else
        {
            MenuVideoMode mode = number == -1 ? _options.DesktopMode?.Invoke() ?? new MenuVideoMode(0, 0, 1) : modes[number];
            vm.ReturnVector(new QcVector(mode.Width, mode.Height, mode.PixelHeight));
        }
    }

    // #626 string(float n, float prop) getgamedirinfo: name (0) or description (1) of a game directory.
    private void GetGameDirInfo(QcVm vm)
    {
        Parms(2, "VM_getgamedirinfo");
        int number = ArgInt(0), item = ArgInt(1);
        vm.ReturnInt(0);
        if (number < 0 || number >= _options.GameDirs.Count) return;
        if (item == 0) vm.ReturnString(_options.GameDirs[number].Name);
        else if (item == 1) vm.ReturnString(_options.GameDirs[number].Description);
    }

    // bound(-1, bindmap, MAX_BINDMAPS-1) of the optional argument; 0 without it ("consistent to bind").
    private int BindMapArg(int index)
    {
        if (Vm.ArgCount <= index) return 0;
        float value = Vm.ArgFloat(index);
        return value >= -1 ? (value < MenuKeyBindings.MaxBindMaps - 1 ? QcVm.FloatToInt(value) : MenuKeyBindings.MaxBindMaps - 1) : -1;
    }

    // #342 string(float keynum[, float bindmap]) getkeybind: the null string for an unbound key.
    private void GetKeyBind(QcVm vm)
    {
        Parms(1, 2, "VM_getkeybind");
        ReturnStringOrNull(Console.Keys.GetBind(ArgInt(0), BindMapArg(1)));
    }

    // #630 float(float key, string bind[, float bindmap]) setkeybind
    private void SetKeyBind(QcVm vm)
    {
        Parms(2, 3, "VM_setkeybind");
        vm.ReturnFloat(Console.Keys.SetBinding(ArgInt(0), BindMapArg(2), vm.ArgString(1)) ? 1 : 0);
    }

    // #631 vector() getbindmaps: foreground and fallback map.
    private void GetBindMaps(QcVm vm)
    {
        Parms(0, "VM_getbindmaps");
        (int foreground, int background) = Console.Keys.BindMap;
        vm.ReturnVector(new QcVector(foreground, background, 0));
    }

    // #632 float(vector bm) setbindmaps
    private void SetBindMaps(QcVm vm)
    {
        Parms(1, "VM_setbindmaps");
        QcVector maps = vm.ArgVector(0);
        vm.ReturnFloat(maps.Z == 0 && Console.Keys.SetBindMap(QcVm.FloatToInt(maps.X), QcVm.FloatToInt(maps.Y)) ? 1 : 0);
    }

    // #610 string(string command[, float bindmap]) findkeysforcommand: up to five key numbers bound
    // to the command, as " 'n' 'n' 'n' 'n' 'n'" with -1 for the unused places.
    private void FindKeysForCommand(QcVm vm)
    {
        Parms(1, 2, "VM_findkeysforcommand");
        string command = vm.ArgString(0);
        int bindMap = BindMapArg(1);
        CheckEmptyString(command);
        Span<int> keys = stackalloc int[FindKeysForCommandKeys];
        Console.Keys.FindKeysForCommand(command, keys, bindMap);
        StringBuilder text = new();
        foreach (int key in keys) text.Append(" '").Append(key).Append('\'');
        vm.ReturnString(text.ToString());
    }

    // #352 void(string cmdname) registercommand: "console command created by QuakeC". Typing it calls
    // the CLIENT program's CSQC_ConsoleCommand (Cmd_CL_Callback), not the menu's GameCommand.
    private void RegisterCommand(QcVm vm)
    {
        Parms(1, "VM_M_registercommand");
        string name = vm.ArgString(0);
        if (name.Length is 0 or > 128 || _qcCommands.Count >= 1024 || !_qcCommands.Add(name)) return;
        Console.Interpreter.RegisterCommand(name, argv =>
        {
            string line = CsqcConsole.JoinArguments(argv, 0);
            if (_options.QcCommand?.Invoke(line) != true) Services.Print($"Command \"{argv[0]}\" can not be executed\n");
        }, "console command created by QuakeC");
    }

    private readonly HashSet<string> _qcCommands = new(StringComparer.OrdinalIgnoreCase);

    // ---- strings the string class does not have ----------------------------------------------------

    // #78 string(float c) chr: one character from its code (u8_fromchar).
    private void Chr(QcVm vm)
    {
        Parms(1, "VM_chr");
        int code = ArgInt(0);
        vm.ReturnString(code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "");
    }

    // An "alternative string" is a list of 'quoted' items in one string, with \' for a quote inside
    // an item. The menu keeps its lists in them.

    // #82 float(string altstr) altstr_count
    private void AltStrCount(QcVm vm)
    {
        Parms(1, "VM_altstr_count");
        string text = vm.ArgString(0);
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                if (++i >= text.Length) break;
            }
            else if (text[i] == '\'') count++;
        }
        vm.ReturnFloat(count / 2);
    }

    // #83 string(string str) altstr_prepare: every quote escaped.
    private void AltStrPrepare(QcVm vm)
    {
        Parms(1, "VM_altstr_prepare");
        string text = vm.ArgString(0);
        StringBuilder result = new(text.Length + 8);
        foreach (char c in text)
        {
            if (result.Length >= Room) break;
            if (c == '\'' && result.Length < Room - 1) result.Append("\\'");
            else result.Append(c);
        }
        vm.ReturnString(result.ToString());
    }

    // #84 string(string altstr, float num) altstr_get: item `num`, unescaped; the null string past the end.
    private void AltStrGet(QcVm vm)
    {
        Parms(2, "VM_altstr_get");
        string text = vm.ArgString(0);
        int count = ArgInt(1) * 2 + 1;
        int pos = 0;
        for (; pos < text.Length && count != 0; pos++)
        {
            if (text[pos] == '\\')
            {
                if (++pos >= text.Length) break;
            }
            else if (text[pos] == '\'') count--;
        }
        if (pos >= text.Length)
        {
            vm.ReturnInt(0);
            return;
        }
        StringBuilder result = new();
        // "(size is decremented twice for an escaped character, so it can skip past zero)"
        for (int size = Room; size > 0 && pos < text.Length; size--, pos++)
        {
            if (text[pos] == '\\')
            {
                if (++pos >= text.Length) break;
                result.Append(text[pos]);
                size--;
            }
            else if (text[pos] == '\'') break;
            else result.Append(text[pos]);
        }
        vm.ReturnString(result.ToString());
    }

    // #85 string(string altstr, float num, string set) altstr_set: item `num` replaced.
    private void AltStrSet(QcVm vm)
    {
        Parms(3, "VM_altstr_set");
        string text = vm.ArgString(0), set = vm.ArgString(2);
        int number = ArgInt(1) * 2 + 1;
        StringBuilder result = new();
        int pos = 0;
        // "every copy into outstr stops at outend, the inputs can be longer than the buffer"
        for (; pos < text.Length && number != 0 && result.Length < Room; result.Append(text[pos++]))
        {
            if (text[pos] == '\\')
            {
                if (++pos >= text.Length) break;
            }
            else if (text[pos] == '\'') number--;
        }
        foreach (char c in set)
        {
            if (result.Length >= Room) break;
            result.Append(c);
        }
        // "now jump over the old content"
        for (; pos < text.Length; pos++)
            if (text[pos] == '\'' || (text[pos] == '\\' && ++pos >= text.Length)) break;
        AppendRest(result, text, pos);
        vm.ReturnString(result.ToString());
    }

    // #86 string(string altstr, float num, string set) altstr_ins: a new item after item `num`.
    private void AltStrIns(QcVm vm)
    {
        Parms(3, "VM_altstr_ins");
        string text = vm.ArgString(0), set = vm.ArgString(2);
        int number = ArgInt(1) * 2 + 2;
        StringBuilder result = new();
        int pos = 0;
        for (; pos < text.Length && number > 0 && result.Length < Room; result.Append(text[pos++]))
        {
            if (text[pos] == '\\')
            {
                if (++pos >= text.Length) break;
            }
            else if (text[pos] == '\'') number--;
        }
        if (result.Length < Room) result.Append('\'');
        foreach (char c in set)
        {
            if (result.Length >= Room) break;
            result.Append(c);
        }
        if (result.Length < Room) result.Append('\'');
        AppendRest(result, text, pos);
        vm.ReturnString(result.ToString());
    }

    // dp_strlcpy(out, in, room left)
    private void AppendRest(StringBuilder result, string text, int pos)
    {
        if (pos >= text.Length) return;
        int room = Room - result.Length;
        if (room <= 0) return;
        result.Append(text, pos, Math.Min(room, text.Length - pos));
    }

    // ---- network, video, crypto: what this host does not have, answered honestly -------------------

    // #624 string() getextresponse: the next queued "extResponse" packet, or the null string. Nothing
    // queues them here (they are replies to a server-list extension Xonotic's menu does not use).
    private void GetExtResponse(QcVm vm)
    {
        Parms(0, "VM_argv");
        vm.ReturnInt(0);
    }

    // #625 string(string address[, float port]) netaddress_resolve: the address normalised, with its
    // port when one was given; "" if it is not an address. Only literal IPv4 addresses are recognised:
    // DarkPlaces would also look a host name up, which would block the frame on DNS here.
    private void NetAddressResolve(QcVm vm)
    {
        Parms(1, 2, "VM_netaddress_resolve");
        string text = vm.ArgString(0);
        int port = vm.ArgCount > 1 ? ArgInt(1) : 0;
        string? address = text.Length <= 128 ? MenuHostCache.NormalizeAddress(text, port is > 0 and <= 65535 ? port : 1) : null;
        if (address is null) vm.ReturnString("");
        else vm.ReturnString(vm.ArgCount > 1 ? address : address[..address.LastIndexOf(':')]);
    }

    // #513 float(string uri, float id[, string post_contenttype, string post_delim[, float buf[, float keyid]]]) uri_get.
    // The C writes the result as an integer cell (PRVM_G_INT(OFS_RETURN) = 1): a QuakeC float whose
    // bits are 1 is a denormal, but it is not zero, and "if (uri_get(...))" is a bit test.
    private void UriGet(QcVm vm)
    {
        if (_fnUriGetCallback == 0) throw Fault("uri_get called by menu without URI_Get_Callback defined");
        Parms(2, 6, "VM_uri_get");
        string url = vm.ArgString(0);
        // Only a plain fetch is offered to the owner; a POST (and a signed one even more so) is refused.
        bool started = vm.ArgCount == 2 && url.Length is > 0 and < 2048 && _options.UriGet?.Invoke(url, ArgInt(1)) == true;
        vm.ReturnInt(started ? 1 : 0);
    }

    // #461 float(string file, string name) cin_open: 0, no video could be opened.
    private void CinOpen(QcVm vm)
    {
        Parms(2, "VM_cin_open");
        CheckEmptyString(vm.ArgString(0));
        CheckEmptyString(vm.ArgString(1));
        vm.ReturnFloat(0);
    }

    // #633 crypto_getkeyfp, #634 crypto_getidfp, #635 crypto_getencryptlevel: string(string addr).
    // Crypto_RetrieveHostKey finds nothing - this client has no d0_blind_id and has stored no host
    // keys - so each answers the null string, which is what DarkPlaces answers for an unknown server.
    private void CryptoHostKey(QcVm vm, string name)
    {
        Parms(1, name);
        CheckEmptyString(vm.ArgString(0));
        vm.ReturnInt(0);
    }

    // crypto.h MAX_PUBKEYS: the identity slots DarkPlaces has, all of them empty here.
    private const int MaxPubKeys = 16;

    // #636 crypto_getmykeyfp, #637 crypto_getmyidfp: string(float i). Crypto_RetrieveLocalKey answers -1
    // for a slot with no key in it ("have no ID there": the empty string) and 0 past the last slot (the
    // null string, which ends the menu's loop over the slots).
    private void CryptoLocalKey(QcVm vm, string name)
    {
        Parms(1, name);
        int slot = ArgInt(0);
        if (slot is >= 0 and < MaxPubKeys) vm.ReturnString("");
        else vm.ReturnInt(0);
    }

    // #641 float(float i) crypto_getmyidstatus: 0 "have no ID there", -1 "out of range".
    private void CryptoMyIdStatus(QcVm vm)
    {
        Parms(1, "VM_M_crypto_getmyidstatus");
        vm.ReturnFloat(ArgInt(0) is >= 0 and < MaxPubKeys ? 0 : -1);
    }
}

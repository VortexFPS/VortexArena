// Port of Base/darkplaces/csprogs.c: CL_VM_Init, CL_VM_ShutDown, CL_CheckRequiredFuncs, CSQC_SetGlobals,
// CSQC_UpdateNetworkTimes, CL_VM_UpdateView, CL_VM_InputEvent, CL_VM_ConsoleCommand,
// CL_VM_Parse_TempEntity, CL_VM_Parse_StuffCmd, CSQC_AddPrintText, CL_VM_Parse_CenterPrint,
// CSQC_ReadEntities, CL_VM_UpdateIntermissionState, CL_VM_UpdateShowingScoresState,
// CL_VM_UpdateDmgGlobals, CL_VM_UpdateCoopDeathmatchGlobals, CL_VM_PreventInformationLeaks, CSQC_Think,
// CSQC_Predraw; with
// cl_collision.c CL_LinkEdict, CL_GetModelFromEdict and prvm_edict.c PRVM_GameCommand.
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>The program could not be started. The message says exactly why.</summary>
public sealed class CsqcLoadException : Exception
{
    public CsqcLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>One fault of the program: which entry point was running, and the VM's message with the QuakeC stack.</summary>
public sealed record CsqcFault(string EntryPoint, string Message, int MessageIndex, double Time);

/// <summary>
/// A network read that ran off the end of a message, or a fault while the program was decoding one.
/// Either means the client and the server disagree about a message's layout - and since the messages
/// carry no lengths, the disagreement is located here or not at all: by the time the engine parser
/// reports "bad server message" it is looking at unrelated bytes.
/// </summary>
/// <param name="MessageIndex">Which message of the connection or demo, counted by the driver.</param>
/// <param name="Context">The entry point decoding the payload: CSQC_Ent_Update or CSQC_Parse_TempEntity.</param>
/// <param name="ServerEntity">The server entity being updated, or -1.</param>
/// <param name="ReaderOffset">Where the reader stood after the failed read.</param>
/// <param name="PayloadStart">Where the payload began.</param>
/// <param name="MessageLength">Length of the message.</param>
/// <param name="Reason">"read past the end" with the builtin's name, or the fault's first line.</param>
/// <param name="Stack">The QuakeC stack at that moment, innermost first.</param>
public sealed record CsqcDesync(int MessageIndex, string Context, int ServerEntity, int ReaderOffset, int PayloadStart, int MessageLength, string Reason, string Stack)
{
    /// <summary>The innermost QuakeC frame, "file : function : statement n".</summary>
    public string Where => Stack.Split('\n')[0].Trim();

    public override string ToString() =>
        $"message {MessageIndex}: {Context}" + (ServerEntity >= 0 ? $" (server entity {ServerEntity})" : "")
        + $": {Reason} at offset {ReaderOffset} of {MessageLength} (payload began at {PayloadStart}); in {Where}";
}

/// <summary>See <see cref="CsqcHostOptions.FindKeysForCommand"/>.</summary>
public delegate void CsqcFindKeys(string command, Span<int> keys, int bindMap);

public sealed class CsqcHostOptions
{
    /// <summary>gamename: CSQC_Init is told the engine is "DarkPlaces " + this.</summary>
    public string GameName { get; init; } = "Xonotic";

    /// <summary>
    /// DarkPlaces stops the client when the program faults (Host_Error), so by default the first fault
    /// disables the program for good. A diagnostic run can keep calling it instead, to see how much
    /// else works; the VM is unwound after a fault and safe to re-enter, but the program's own state
    /// is whatever the fault left behind.
    /// </summary>
    public bool KeepRunningAfterFault { get; init; }

    /// <summary>Accept a program whose size or CRC is not the one the server named. DarkPlaces does
    /// this only when playing a demo, with a warning; a live connection is refused.</summary>
    public bool AllowMismatch { get; init; }

    /// <summary>SCR_CenterPrint: text for the middle of the screen (the centerprint builtin, and
    /// svc_centerprint when the program has no CSQC_Parse_CenterPrint).</summary>
    public Action<string>? CenterPrint { get; init; }

    /// <summary>Key_GetBind: the command bound to a key in a bind map (-1: the active maps), or null.</summary>
    public Func<int, int, string?>? KeyBinding { get; init; }

    /// <summary>Key_FindKeysForCommand, for an owner whose bind table can answer it without being asked about
    /// every one of the 44,032 key numbers in turn: the first keys.Length key numbers bound to exactly this
    /// command in ascending order, the rest left as they are (-1). Null: <see cref="KeyBinding"/> is asked key by
    /// key, which gives the same answer (1.3 ms a call; a HUD that shows ten key hints asked ten times in a frame).</summary>
    public CsqcFindKeys? FindKeysForCommand { get; init; }

    /// <summary>The uri_get builtin (#513) for an owner with an HTTP client of its own: (url, id) to
    /// "request started", GET only. Used when <see cref="UriRequests"/> is null; the extension is then not
    /// advertised. Null with that null too: no HTTP, as when DarkPlaces runs without libcurl.</summary>
    public Func<string, int, bool>? UriGet { get; init; }

    /// <summary>
    /// HTTP for the program (libcurl.c as prvm_cmds.c VM_uri_get uses it): GET and POST requests with the
    /// outcome delivered to URI_Get_Callback. While it is <see cref="VortexArena.Legacy.Downloads.LegacyUriRequests.Available"/>
    /// when the program is loaded, checkextension answers yes to DP_QC_URI_GET and DP_QC_URI_POST. The owner
    /// calls <see cref="CsqcHost.DeliverUriReplies"/> once a frame; the program's requests are cancelled when
    /// it is unloaded. Null: see <see cref="UriGet"/>.
    /// </summary>
    public VortexArena.Legacy.Downloads.LegacyUriRequests? UriRequests { get; init; }

    /// <summary>Seed for the program's random() and randomvec(). Null (the default): unseeded, as in play.
    /// A measuring or comparing run sets it so that two runs of one build do the same thing.</summary>
    public int? RandomSeed { get; init; }

    /// <summary>Replaces the monotonic clock behind gettime(GETTIME_REALTIME / GETTIME_HIRES) and the
    /// frame-start reading. Null (the default): the stopwatch. For repeatable runs only.</summary>
    public Func<double>? DirtyTime { get; init; }

    /// <summary>Keep the per-entity index that lets addentities skip entities with no think, predraw or
    /// drawmask (QcVm.WatchFields). On by default; off runs the plain pass over every entity.</summary>
    public bool EntityIndex { get; init; } = true;

    /// <summary>Check the index against every entity on each addentities, faulting the program if an
    /// entity it leaves out has something to do. For tests and measuring runs: it costs what the index saves.</summary>
    public bool VerifyEntityIndex { get; init; }

    /// <summary>Before each CSQC_UpdateView, ask the processor for the memory the previous one touched
    /// (QcVm.PrefetchRecorded). On by default; it changes nothing the program can observe.</summary>
    public bool PrefetchFrameMemory { get; init; } = true;
}

/// <summary>
/// One loaded client program (csprogs.dat) and the engine around it: the VM, its builtins, the engine
/// globals, the map from server entities to the program's own, and the entry points the engine calls.
///
/// The program comes from whatever server the player joined. Nothing it does can make a method here
/// throw: a fault inside it is recorded (<see cref="Faults"/>) and, as in DarkPlaces, the program is
/// not run again.
/// </summary>
public sealed partial class CsqcHost : IDisposable
{
    private const float SolidBsp = 4;

    private readonly CsqcHostOptions _options;
    private readonly QcCoreBuiltins _core;
    private readonly QcAutocvars _autocvars;
    private readonly DpMessageReader _noMessage = new(Array.Empty<byte>());
    private readonly List<CsqcFault> _faults = new();
    private readonly List<CsqcDesync> _desyncs = new();
    private readonly int[] _serverToCsqc = new int[DpProtocol.MaxEdicts];
    private readonly Dictionary<string, (bool Exists, QcVector Mins, QcVector Maxs)> _modelBounds = new(StringComparer.Ordinal);

    private readonly int _fnInit, _fnShutdown, _fnUpdateView, _fnInputEvent, _fnConsoleCommand, _fnParseStuffCmd,
        _fnParsePrint, _fnParseCenterPrint, _fnParseTempEntity, _fnEntUpdate, _fnEntRemove, _fnEntSpawn, _fnGameCommand, _fnUriGetCallback;
    private readonly QcStringBuiltins _strings;

    private string _printBuffer = "";
    private DpMessageReader? _message;
    private string? _payloadContext;
    private int _payloadEntity = -1, _payloadStart;
    private CsqcDesync? _pendingDesync;
    private CsqcEffectInfo? _effects;
    private bool _disposed;
    private double _frameStart;

    // The area grid of world.c, reduced to what findradius and findbox need: the box each entity was
    // last linked with.
    private bool[] _linked = new bool[512];
    private QcVector[] _linkMins = new QcVector[512], _linkMaxs = new QcVector[512];
    // The linked entities' numbers in ascending order. Xonotic's client program has some 3,300 entities, most
    // of them objects that are never placed in the world; findradius and findbox walked every one of them
    // three or four times a frame to find the few hundred that are.
    private int[] _linkedIds = new int[256];
    private int _linkedCount;

    public QcVm Vm { get; }
    public ProgsFile Program => Vm.Progs;
    public CsqcClientState State { get; }
    public LegacyQcHost Services { get; }
    public CsqcConsole Console { get; }
    public ILegacyPresentation Presentation { get; }
    public CsqcFieldOffsets Fields { get; }
    public CsqcGlobalOffsets Globals { get; }
    public CsqcBuiltins Builtins { get; }
    public int AutocvarsBound { get; }

    /// <summary>CRC-16 and size of the program file that was loaded.</summary>
    public int ProgramCrc { get; }
    public int ProgramSize { get; }

    /// <summary>True once the program has faulted. Unless <see cref="CsqcHostOptions.KeepRunningAfterFault"/>, nothing runs after that.</summary>
    public bool Faulted => FaultCount > 0;
    public int FaultCount { get; private set; }
    /// <summary>The first 256 faults.</summary>
    public IReadOnlyList<CsqcFault> Faults => _faults;
    public string? FaultMessage => _faults.Count > 0 ? _faults[0].Message : null;

    /// <summary>The first 64 desyncs; see <see cref="CsqcDesync"/>.</summary>
    public IReadOnlyList<CsqcDesync> Desyncs => _desyncs;
    public int DesyncCount { get; private set; }
    /// <summary>The desync recorded since <see cref="BeginMessage"/>, if any.</summary>
    public CsqcDesync? MessageDesync { get; private set; }

    /// <summary>Builtins the program called that nothing implements: (number, name) to call count.
    /// Each such call returned 0.</summary>
    public Dictionary<(int Number, string Name), long> UnimplementedBuiltins { get; } = new();

    /// <summary>Every extension name the program has passed to checkextension, with the answer it got.</summary>
    public IReadOnlyDictionary<string, bool> ExtensionChecks => _core.ExtensionAnswers;

    /// <summary>Network reads made while no message was being parsed. They return -1, as in DarkPlaces.</summary>
    public long ReadsOutsideMessage { get; internal set; }

    /// <summary>True once CSQC_Init has returned without a fault (or the program has none).</summary>
    public bool Initialized { get; private set; }

    /// <summary>Index of the message being parsed, for diagnostics. Set by <see cref="BeginMessage"/>.</summary>
    public int MessageIndex { get; private set; } = -1;

    public long EntityUpdates { get; private set; }
    public long EntityRemoves { get; private set; }
    public long TempEntitiesConsumed { get; private set; }
    public long TempEntitiesDeclined { get; private set; }

    internal bool VerifyEntityIndex => _options.VerifyEntityIndex;

    private bool CanRun => !_disposed && (FaultCount == 0 || _options.KeepRunningAfterFault);

    /// <summary>True when the program will not be run again: it faulted (and the host was not asked to
    /// keep going) or was shut down. Messages only it can measure are undecodable from then on.</summary>
    public bool Disabled => !CanRun;

    /// <summary>
    /// Loads <paramref name="program"/> and prepares it to run: everything CL_VM_Init does up to, but
    /// not including, the call of CSQC_Init (that is <see cref="Init"/>).
    /// </summary>
    /// <param name="program">The bytes of csprogs.dat.</param>
    /// <param name="expectedSize">csqc_progsize as the server announced it.</param>
    /// <param name="expectedCrc">csqc_progcrc as the server announced it.</param>
    /// <exception cref="CsqcLoadException">The file is not the one the server named, is not a valid
    /// program, or lacks the function the engine needs.</exception>
    public CsqcHost(ReadOnlySpan<byte> program, int expectedSize, int expectedCrc, LegacyQcHost services, CsqcConsole console,
        ILegacyPresentation presentation, CsqcClientState state, CsqcHostOptions? options = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Console = console ?? throw new ArgumentNullException(nameof(console));
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        State = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? new CsqcHostOptions();

        ProgramSize = program.Length;
        ProgramCrc = Crc16.Block(program);
        if ((ProgramCrc != expectedCrc || ProgramSize != expectedSize) && !_options.AllowMismatch)
            throw new CsqcLoadException($"Your csprogs.dat is not the same version as the server (CRC is {ProgramCrc}/{ProgramSize} but should be {expectedCrc}/{expectedSize})");

        ProgsFile progs;
        try { progs = ProgsFile.Load(program); }
        catch (ProgsFormatException e) { throw new CsqcLoadException("CSQC csprogs.dat failed to load: " + e.Message, e); }

        // prog->max_edicts = 512, limit_edicts = CL_MAX_EDICTS, reserved_edicts = 0.
        Vm = new QcVm(progs, "client", 512) { EdictLimit = DpProtocol.MaxEdicts };

        // CL_CheckRequiredFuncs. CSQC_DrawHud alone means a "CSQC_SIMPLE" hud-only program, which this
        // host does not drive; none of the Xonotic lineage is one.
        _fnUpdateView = Vm.FindFunction("CSQC_UpdateView");
        if (_fnUpdateView == 0)
            throw new CsqcLoadException(Vm.FindFunction("CSQC_DrawHud") != 0
                ? "client: csprogs.dat is a hud-only (CSQC_SIMPLE) program; only programs with CSQC_UpdateView are supported"
                : "client: no CSQC_UpdateView (EXT_CSQC) or CSQC_DrawHud (CSQC_SIMPLE) function found in csprogs.dat");
        _fnInit = Vm.FindFunction("CSQC_Init");
        _fnShutdown = Vm.FindFunction("CSQC_Shutdown");
        _fnInputEvent = Vm.FindFunction("CSQC_InputEvent");
        _fnConsoleCommand = Vm.FindFunction("CSQC_ConsoleCommand");
        _fnParseStuffCmd = Vm.FindFunction("CSQC_Parse_StuffCmd");
        _fnParsePrint = Vm.FindFunction("CSQC_Parse_Print");
        _fnParseCenterPrint = Vm.FindFunction("CSQC_Parse_CenterPrint");
        _fnParseTempEntity = Vm.FindFunction("CSQC_Parse_TempEntity");
        _fnEntUpdate = Vm.FindFunction("CSQC_Ent_Update");
        _fnEntRemove = Vm.FindFunction("CSQC_Ent_Remove");
        _fnEntSpawn = Vm.FindFunction("CSQC_Ent_Spawn");
        _fnGameCommand = Vm.FindFunction("GameCommand");
        _fnUriGetCallback = Vm.FindFunction("URI_Get_Callback");

        // Engine fields first: they can only be appended while the world is the only entity.
        Fields = new CsqcFieldOffsets(Vm);
        Globals = new CsqcGlobalOffsets(Vm);

        HashSet<string> extensions = new(CsqcExtensions.All, StringComparer.OrdinalIgnoreCase);
        // prvm_cmds.c checkextension: "special shreck for libcurl ... return Curl_Available()".
        if (_options.UriRequests is { Available: true })
            foreach (string name in CsqcExtensions.Http) extensions.Add(name);
        _core = new QcCoreBuiltins(Vm, services, extensions)
        {
            ReservedEdicts = 0,
            EdictFreeing = UnlinkEdict,
            EdictLinked = LinkEdict,
            // gettime(GETTIME_HIRES): seconds since the frame began (Sys_DirtyTime() - host.dirtytime).
            FrameDirtyTime = () => _frameStart,
        };
        if (_options.RandomSeed is int seed) _core.Random = new Random(seed);
        if (_options.DirtyTime is not null) _core.DirtyTime = _options.DirtyTime;
        _core.Register();
        _strings = new QcStringBuiltins(Vm, services) { OpenFile = _core.FileStream };
        _strings.Register();
        Builtins = new CsqcBuiltins(this, _core);
        Builtins.Register();
        Vm.UnknownBuiltin = (_, number, name) =>
            UnimplementedBuiltins[(number, name)] = UnimplementedBuiltins.GetValueOrDefault((number, name)) + 1;

        _autocvars = new QcAutocvars(Vm, services);
        AutocvarsBound = _autocvars.Bind();
        services.CvarChanged += OnCvarChanged;

        try
        {
            // "set time", then the level: its name, and the world entity as the map.
            SetFloat(Globals.Time, (float)State.Time);
            SetInt(Globals.Self, 0);
            SetInt(Globals.MapName, Vm.EngineString(State.WorldBaseName));
            SetFloat(Globals.PlayerLocalNum, State.RealPlayerEntity - 1);
            SetFloat(Globals.PlayerLocalEntNum, State.ViewEntity);

            presentation.World.Bounds(out QcVector worldMins, out QcVector worldMaxs);
            Vm.FieldInt(0, Fields.Message) = Vm.EngineString(State.WorldMessage);
            Vm.FieldVector(0, Fields.Mins) = worldMins;
            Vm.FieldVector(0, Fields.Maxs) = worldMaxs;
            Vm.FieldVector(0, Fields.AbsMin) = worldMins;
            Vm.FieldVector(0, Fields.AbsMax) = worldMaxs;
            Vm.FieldFloat(0, Fields.Solid) = SolidBsp;
            Vm.FieldFloat(0, Fields.ModelIndex) = 1;
            Vm.FieldInt(0, Fields.Model) = Vm.EngineString(State.WorldModel);
        }
        catch (QcRuntimeException e)
        {
            // A program whose own definition of an engine field is too small to hold it.
            throw new CsqcLoadException("CSQC csprogs.dat failed to load: " + e.Message, e);
        }

        // After the engine fields above: watching freezes the field layout.
        if (_options.EntityIndex && Fields.Think >= 0 && Fields.Predraw >= 0 && Fields.DrawMask >= 0)
            Vm.WatchFields(stackalloc int[] { Fields.Think, Fields.Predraw, Fields.DrawMask });

        Vm.RecordTouches = _options.PrefetchFrameMemory;

        presentation.Attach(this);
        console.Host = this;
    }

    /// <summary>The effect-name numbering for this level (effectinfo.txt), loaded on first use.</summary>
    public CsqcEffectInfo Effects => _effects ??= CsqcEffectInfo.Load(Services.ReadFile, State.WorldNameNoExtension);

    /// <summary>
    /// The cl_particles_reloadeffects console command, which Xonotic servers send once the level is
    /// known: read effectinfo.txt again, with the map's own file - or, given a file name, that file
    /// alone.
    /// </summary>
    public void ReloadEffects(string? customFile = null) =>
        _effects = CsqcEffectInfo.Load(Services.ReadFile, State.WorldNameNoExtension, customFile);

    // ---- globals -----------------------------------------------------------------------------------

    internal void SetFloat(int offset, float value) { if (offset >= 0) Vm.GlobalFloat(offset) = value; }
    internal void SetInt(int offset, int value) { if (offset >= 0) Vm.GlobalInt(offset) = value; }
    internal void SetVector(int offset, QcVector value) { if (offset >= 0) Vm.GlobalVector(offset) = value; }
    internal float GetFloat(int offset) => offset >= 0 ? Vm.GlobalFloat(offset) : 0;
    internal int GetInt(int offset) => offset >= 0 ? Vm.GlobalInt(offset) : 0;
    internal QcVector GetVector(int offset) => offset >= 0 ? Vm.GlobalVector(offset) : default;

    /// <summary>The program's entity for the local player, or 0: what <c>self</c> is on entry.</summary>
    private int PlayerEdict => _serverToCsqc[State.PlayerEntity & (DpProtocol.MaxEdicts - 1)];

    private void EnterAsPlayer()
    {
        SetFloat(Globals.Time, (float)State.Time);
        SetInt(Globals.Self, PlayerEdict);
    }

    /// <summary>CSQC_UpdateNetworkTimes: called when svc_time arrives.</summary>
    public void UpdateNetworkTimes()
    {
        if (_disposed) return;
        SetFloat(Globals.ServerTime, (float)State.ServerTime);
        SetFloat(Globals.ServerPrevTime, (float)State.ServerPrevTime);
        SetFloat(Globals.ServerDeltaTime, (float)(State.ServerTime - State.ServerPrevTime));
    }

    /// <summary>CL_VM_UpdateIntermissionState.</summary>
    public void UpdateIntermissionState(int intermission)
    {
        if (!_disposed) SetFloat(Globals.Intermission, intermission);
    }

    /// <summary>CL_VM_UpdateShowingScoresState.</summary>
    public void UpdateShowingScoresState(bool showing)
    {
        if (!_disposed) SetFloat(Globals.SbShowScores, showing ? 1 : 0);
    }

    /// <summary>CL_VM_UpdateDmgGlobals: svc_damage. Cleared again after each CSQC_UpdateView.</summary>
    public void UpdateDamageGlobals(int take, int save, QcVector origin)
    {
        if (_disposed) return;
        SetFloat(Globals.DmgTake, take);
        SetFloat(Globals.DmgSave, save);
        SetVector(Globals.DmgOrigin, origin);
    }

    /// <summary>
    /// CL_VM_PreventInformationLeaks: wipe the trace globals. DarkPlaces does this at the start of
    /// every client frame so that what one frame's traces learned about the world (who is behind which
    /// wall) is not sitting in memory for the next entry point to read - "anti-triggerbot safeguard".
    /// The owner of the frame loop calls it.
    /// </summary>
    public void PreventInformationLeaks()
    {
        if (_disposed) return;
        SetFloat(Globals.TraceAllSolid, 0);
        SetFloat(Globals.TraceStartSolid, 0);
        SetFloat(Globals.TraceFraction, 0);
        SetFloat(Globals.TraceInWater, 0);
        SetFloat(Globals.TraceInOpen, 0);
        SetVector(Globals.TraceEndPos, default);
        SetVector(Globals.TracePlaneNormal, default);
        SetFloat(Globals.TracePlaneDist, 0);
        SetInt(Globals.TraceEnt, 0);
        SetFloat(Globals.TraceDpStartContents, 0);
        SetFloat(Globals.TraceDpHitContents, 0);
        SetFloat(Globals.TraceDpHitQ3SurfaceFlags, 0);
        SetInt(Globals.TraceDpHitTextureName, 0);
        SetFloat(Globals.TraceNetworkEntity, 0);
    }

    // CL_VM_UpdateCoopDeathmatchGlobals.
    private void UpdateCoopDeathmatchGlobals()
    {
        SetFloat(Globals.Coop, State.GameType == 0 ? 1 : 0);
        SetFloat(Globals.Deathmatch, State.GameType == 1 ? 1 : 0);
    }

    // CSQC_SetGlobals: "set globals before calling R_UpdateView".
    private void SetFrameGlobals(double frameTime)
    {
        ref CsqcUserCommand newest = ref State.MoveCommands[0];
        SetFloat(Globals.Time, (float)State.Time);
        SetFloat(Globals.ClTime, (float)Services.RealTime);
        SetFloat(Globals.FrameTime, (float)frameTime);
        SetFloat(Globals.ServerCommandFrame, State.ServerMoveSequence);
        SetFloat(Globals.ClientCommandFrame, newest.Sequence);
        SetVector(Globals.InputAngles, State.ViewAngles);
        SetFloat(Globals.InputButtons, newest.Buttons);
        SetVector(Globals.InputMoveValues, new QcVector(newest.ForwardMove, newest.SideMove, newest.UpMove));
        // "Spike says not to do this, but without pmove_org the CSQC is useless as it can't alter the
        // view origin without completely replacing it"
        SetVector(Globals.PmoveOrg, State.ViewEntityOrigin);
        SetVector(Globals.PmoveVel, State.Velocity);
        SetFloat(Globals.PmoveOnGround, State.OnGround ? 1 : 0);
        SetFloat(Globals.PmoveInWater, State.InWater ? 1 : 0);
        SetVector(Globals.ViewAngles, State.ViewAngles);
        SetVector(Globals.ViewPunchAngle, State.PunchAngle);
        SetVector(Globals.ViewPunchVector, State.PunchVector);
        SetFloat(Globals.MaxClients, State.MaxClients);
        SetFloat(Globals.PlayerLocalEntNum, State.ViewEntity);
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

    private void RecordFault(string entryPoint, string message)
    {
        FaultCount++;
        if (_faults.Count < 256) _faults.Add(new CsqcFault(entryPoint, message, MessageIndex, State.Time));
        if (_payloadContext is not null)
        {
            // The fault happened while decoding a message: that is where the stream stopped making sense.
            int newline = message.IndexOf('\n');
            _pendingDesync = new CsqcDesync(MessageIndex, _payloadContext, _payloadEntity, _message?.Position ?? 0, _payloadStart,
                _message?.Length ?? 0, "fault: " + (newline < 0 ? message : message[..newline]), newline < 0 ? "" : message[(newline + 1)..]);
        }
    }

    /// <summary>Forgets recorded faults, so a diagnostic run can count them per phase. Does not repair the program.</summary>
    public void ClearFaults()
    {
        FaultCount = 0;
        _faults.Clear();
    }

    /// <summary>CSQC_Init(apilevel, enginename, engineversion). Returns false if it faulted.</summary>
    public bool Init()
    {
        if (!CanRun) return false;
        bool ok = true;
        if (_fnInit != 0)
        {
            Vm.SetArgFloat(0, 1.0f); // "CSQC_SIMPLE engines always pass 0, FTE always passes 1"
            // "always include "DarkPlaces" so it can be recognised when gamename doesn't include it"
            Vm.SetArgInt(1, Vm.EngineString("DarkPlaces " + _options.GameName));
            Vm.SetArgFloat(2, 1.0f);
            ok = Run(_fnInit, 3, "CSQC_Init");
        }
        // "Once CSQC_Init was called, we consider csqc code fully initialized."
        _core.StartTime = Services.RealTime;
        if (ok) UpdateCoopDeathmatchGlobals();
        Initialized = ok;
        return ok;
    }

    /// <summary>CSQC_Shutdown, then unloads: the host is unusable afterwards.</summary>
    public void Shutdown()
    {
        if (_disposed) return;
        SetFloat(Globals.Time, (float)State.Time);
        SetInt(Globals.Self, 0);
        Run(_fnShutdown, 0, "CSQC_Shutdown");
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Services.CvarChanged -= OnCvarChanged;
        if (ReferenceEquals(Console.Host, this)) Console.Host = null;
        // "curl reply came too late... so just drop it": nothing a program started outlives it.
        _options.UriRequests?.CancelAll();
        _core.Dispose();
    }

    private void OnCvarChanged(string name)
    {
        if (!_disposed) _autocvars.Update(name);
    }

    /// <summary>
    /// CSQC_UpdateView(width, height, notmenu): the program draws one frame.
    /// </summary>
    /// <param name="width">vid.mode.width, or vid_conwidth with csqc_lowres.</param>
    /// <param name="height">vid.mode.height, or vid_conheight.</param>
    /// <param name="frameTime">Seconds since the previous frame, for the frametime global.</param>
    /// <param name="gameHasFocus">key_dest == key_game: false while a menu is in front.</param>
    public bool UpdateView(float width, float height, double frameTime = 0, bool gameHasFocus = true)
    {
        if (!CanRun) return false;
        if (_options.PrefetchFrameMemory) Vm.PrefetchRecorded();
        _frameStart = _options.DirtyTime?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        EnterAsPlayer();
        SetFrameGlobals(frameTime);
        Builtins.BeginFrame();
        Vm.SetArgFloat(0, width);
        Vm.SetArgFloat(1, height);
        Vm.SetArgFloat(2, gameHasFocus ? 1 : 0);
        bool ok = Run(_fnUpdateView, 3, "CSQC_UpdateView");
        // "Dresk : Reset Dmg Globals Here"
        UpdateDamageGlobals(0, 0, default);
        return ok;
    }

    /// <summary>
    /// CSQC_InputEvent(type, a, b). Type 0 is key down and 1 key up (key number, character), 2 a
    /// relative and 3 an absolute mouse move (x, y). True if the program consumed the event.
    /// </summary>
    public bool InputEvent(int type, float a, float b)
    {
        if (_fnInputEvent == 0 || !CanRun) return false;
        EnterAsPlayer();
        Vm.SetArgFloat(0, type);
        Vm.SetArgFloat(1, a);
        Vm.SetArgFloat(2, b);
        return Run(_fnInputEvent, 3, "CSQC_InputEvent") && Vm.ResultFloat != 0;
    }

    /// <summary>CSQC_ConsoleCommand(text): a command the program registered was typed. True if it handled it.</summary>
    public bool ConsoleCommand(string text)
    {
        if (_fnConsoleCommand == 0 || !CanRun) return false;
        EnterAsPlayer();
        return RunWithText(_fnConsoleCommand, text, "CSQC_ConsoleCommand") && QcVm.FloatToInt(Vm.ResultFloat) != 0;
    }

    /// <summary>GameCommand(text): the <c>cl_cmd</c> console command. Neither time nor self is set, as in the C.</summary>
    public void GameCommand(string text)
    {
        if (_fnGameCommand == 0)
        {
            if (!_disposed) Services.Print("client program do not support GameCommand!\n");
            return;
        }
        RunWithText(_fnGameCommand, text, "GameCommand");
    }

    /// <summary>
    /// svc_stufftext. A program with CSQC_Parse_StuffCmd is given the text (Xonotic's passes most of
    /// it on with localcmd); otherwise it goes to the command buffer. Text beginning "csqc" is the
    /// engine's (csqc_progname and friends) and is executed at once, never shown to the program.
    /// </summary>
    public void ParseStuffCmd(string text)
    {
        if (text.StartsWith("csqc", StringComparison.Ordinal))
        {
            Console.ExecuteNow(text);
            return;
        }
        if (_fnParseStuffCmd == 0 || !CanRun)
        {
            Console.AddText(text);
            return;
        }
        EnterAsPlayer();
        RunWithText(_fnParseStuffCmd, text, "CSQC_Parse_StuffCmd");
    }

    /// <summary>
    /// svc_print (CSQC_AddPrintText): text is gathered until a line ends, and CSQC_Parse_Print gets
    /// whole lines. Without that function the text goes to the console.
    /// </summary>
    public void ParsePrint(string text)
    {
        if (text.Length == 0) return;
        if (_fnParsePrint == 0 || !CanRun)
        {
            Services.Print(text);
            return;
        }

        const int limit = DpProtocol.MaxInputLine;
        if (text[^1] is not ('\n' or '\r'))
        {
            if (_printBuffer.Length + text.Length + 1 >= limit)
            {
                // As the C has it: the buffer is flushed and THIS text is dropped, not appended.
                string full = _printBuffer;
                _printBuffer = "";
                EnterAsPlayer();
                RunWithText(_fnParsePrint, full, "CSQC_Parse_Print");
            }
            else _printBuffer += text;
            return;
        }

        string line = _printBuffer + text;
        _printBuffer = "";
        if (line.Length >= limit) line = line[..(limit - 1)];
        EnterAsPlayer();
        RunWithText(_fnParsePrint, line, "CSQC_Parse_Print");
    }

    /// <summary>svc_centerprint.</summary>
    public void ParseCenterPrint(string text)
    {
        if (_fnParseCenterPrint == 0 || !CanRun)
        {
            CenterPrint(text);
            return;
        }
        EnterAsPlayer();
        RunWithText(_fnParseCenterPrint, text, "CSQC_Parse_CenterPrint");
    }

    internal void CenterPrint(string text) => _options.CenterPrint?.Invoke(text);
    internal string? KeyBinding(int key, int bindMap) => _options.KeyBinding?.Invoke(key, bindMap);
    internal bool HasKeyBindings => _options.KeyBinding is not null;
    internal CsqcFindKeys? FindKeysForCommand => _options.FindKeysForCommand;
    internal int UriGetCallbackFunction => _fnUriGetCallback;

    // Curl_Begin_ToMemory_POST: true if the request was taken and URI_Get_Callback will hear of it.
    internal bool UriBegin(string url, float id, string? postContentType, byte[]? postBody)
    {
        if (_options.UriRequests is { } requests) return requests.Begin(url, id, postContentType, postBody);
        return postContentType is null && (_options.UriGet?.Invoke(url, QcVm.FloatToInt(id)) ?? false);
    }

    // The "implode" of VM_uri_get: a string buffer's strings joined by the separator, or null if there is no such buffer.
    internal string? ImplodeStringBuffer(float handle, string separator, int maxLength) => _strings.Implode(handle, separator, maxLength);

    /// <summary>
    /// uri_to_string_callback: the outcome of a uri_get the program started. URI_Get_Callback(id, status,
    /// data) with the status DarkPlaces passes: 0 with the reply, the HTTP status of a 4xx or 5xx reply, or
    /// a negative libcurl.h CURLCBSTATUS (-1 failed, -2 aborted, -3 server error without a status, -4
    /// unknown). Neither time nor self is set, as in the C. Must not be called while the program runs.
    /// </summary>
    public void UriGetCallback(float id, int status, string data)
    {
        if (_fnUriGetCallback == 0 || !CanRun) return;
        // The reply came from a web server: cut to what a tempstring holds.
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

    /// <summary>
    /// Curl_Frame for the program's requests: every one that has finished is given to URI_Get_Callback.
    /// For the owner of the frame to call once a frame, between the program's entry points. Returns how
    /// many were delivered.
    /// </summary>
    public int DeliverUriReplies() =>
        _options.UriRequests is { } requests && CanRun ? requests.Deliver(UriGetCallback) : 0;

    // ---- the message being parsed ------------------------------------------------------------------

    /// <summary>The reader the Read* builtins consume: the message being parsed, or an empty one.</summary>
    internal DpMessageReader MessageReader => _message ?? _noMessage;
    /// <summary>True between <see cref="BeginMessage"/> and <see cref="EndMessage"/>.</summary>
    public bool InMessage => _message is not null;

    /// <summary>A server message is about to be parsed; its reader is what ReadByte and the rest read.</summary>
    public void BeginMessage(DpMessageReader reader, int messageIndex)
    {
        _message = reader ?? throw new ArgumentNullException(nameof(reader));
        MessageIndex = messageIndex;
        MessageDesync = null;
        _pendingDesync = null;
    }

    /// <summary>The message has been parsed (or abandoned).</summary>
    public void EndMessage()
    {
        _message = null;
        _payloadContext = null;
    }

    // Called by a Read* builtin whose read ran off the end. The first one per payload is the one that
    // matters; it is only "pending" because CSQC_Parse_TempEntity may yet decline the message, in
    // which case its reads are undone and were never a desync.
    internal void NoteBadRead(string builtin)
    {
        if (_message is null || _payloadContext is null || _pendingDesync is not null) return;
        _pendingDesync = new CsqcDesync(MessageIndex, _payloadContext, _payloadEntity, _message.Position, _payloadStart, _message.Length,
            builtin + " read past the end of the message", Vm.StackTrace());
    }

    private void BeginPayload(string context, int serverEntity)
    {
        _payloadContext = context;
        _payloadEntity = serverEntity;
        _payloadStart = _message?.Position ?? 0;
        _pendingDesync = null;
    }

    private void EndPayload(bool keepDesync)
    {
        if (keepDesync && _pendingDesync is not null)
        {
            DesyncCount++;
            if (_desyncs.Count < 64) _desyncs.Add(_pendingDesync);
            MessageDesync ??= _pendingDesync;
        }
        _pendingDesync = null;
        _payloadContext = null;
        _payloadEntity = -1;
    }

    /// <summary>
    /// CL_VM_Parse_TempEntity: offer a svc_temp_entity to the program. True if it consumed the effect.
    /// If it declines, the reader is put back where it was and its bad-read flag cleared, so the
    /// engine's own decoder starts from the same byte.
    /// </summary>
    public bool ParseTempEntity()
    {
        if (_fnParseTempEntity == 0 || !CanRun || _message is null) return false;
        DpMessageReader reader = _message;
        int start = reader.Position;
        EnterAsPlayer();
        BeginPayload("CSQC_Parse_TempEntity", -1);
        bool ran = Run(_fnParseTempEntity, 0, "CSQC_Parse_TempEntity");
        bool handled = ran && Vm.ResultFloat != 0;
        if (!handled)
        {
            reader.Position = start;
            reader.BadRead = false;
        }
        // A fault leaves the payload half-read with no way to tell how long it was: that is a desync
        // even though the reader has been rewound for the engine decoder to try.
        EndPayload(keepDesync: handled || !ran);
        if (handled) TempEntitiesConsumed++;
        else TempEntitiesDeclined++;
        return handled;
    }

    /// <summary>
    /// One entity of svc_csqcentities (the body of CSQC_ReadEntities' loop): find or create the
    /// program's entity for <paramref name="serverEntity"/> and call CSQC_Ent_Update, which reads the
    /// update from the message. False if the program could not be run, in which case nothing more of
    /// the message can be decoded.
    /// </summary>
    public bool EntUpdate(int serverEntity)
    {
        if ((uint)serverEntity >= DpProtocol.MaxEdicts || !CanRun || _fnEntUpdate == 0)
        {
            if (!_disposed && _fnEntUpdate == 0 && FaultCount == 0) RecordFault("CSQC_Ent_Update", "QC function CSQC_Ent_Update is missing");
            return false;
        }

        int oldSelf = GetInt(Globals.Self);
        SetFloat(Globals.Time, (float)State.Time);
        BeginPayload("CSQC_Ent_Update", serverEntity);
        bool ok;
        int edict = _serverToCsqc[serverEntity];
        SetInt(Globals.Self, edict);
        if (edict == 0)
        {
            ok = SpawnFor(serverEntity, out edict);
            if (ok)
            {
                SetInt(Globals.Self, edict);
                Vm.SetArgFloat(0, 1);
                ok = Run(_fnEntUpdate, 1, "CSQC_Ent_Update");
            }
        }
        else
        {
            Vm.SetArgFloat(0, 0);
            ok = Run(_fnEntUpdate, 1, "CSQC_Ent_Update");
        }
        SetInt(Globals.Self, oldSelf);
        EndPayload(keepDesync: true);
        if (ok) EntityUpdates++;
        return ok;
    }

    // The entity for a server entity seen for the first time: the program's CSQC_Ent_Spawn if it has
    // one ("this way it also can return world"), else a plain new entity with .entnum set.
    private bool SpawnFor(int serverEntity, out int edict)
    {
        edict = 0;
        if (_fnEntSpawn == 0)
        {
            try
            {
                edict = _core.AllocEdict();
                Vm.FieldFloat(edict, Fields.EntNum) = serverEntity;
            }
            catch (QcRuntimeException e)
            {
                RecordFault("CSQC_Ent_Update", e.Message);
                return false;
            }
        }
        else
        {
            Vm.SetArgFloat(0, serverEntity);
            SetInt(Globals.Self, 0); // "make sure no one gets wrong ideas"
            if (!Run(_fnEntSpawn, 1, "CSQC_Ent_Spawn")) return false;
            edict = Vm.ResultInt;
            if ((uint)edict >= (uint)Vm.MaxEdicts)
            {
                RecordFault("CSQC_Ent_Spawn", $"client: CSQC_Ent_Spawn returned entity {edict}, which does not exist");
                edict = 0;
                return false;
            }
        }
        _serverToCsqc[serverEntity] = edict;
        return true;
    }

    /// <summary>svc_csqcentities with the remove bit: CSQC_Ent_Remove with <c>self</c> set, then the
    /// mapping is dropped. Removing an entity the program never had is normal (a repeated remove).</summary>
    public void EntRemove(int serverEntity)
    {
        if ((uint)serverEntity >= DpProtocol.MaxEdicts || _disposed) return;
        int edict = _serverToCsqc[serverEntity];
        if (edict == 0) return;
        int oldSelf = GetInt(Globals.Self);
        SetFloat(Globals.Time, (float)State.Time);
        SetInt(Globals.Self, edict);
        Run(_fnEntRemove, 0, "CSQC_Ent_Remove");
        _serverToCsqc[serverEntity] = 0;
        SetInt(Globals.Self, oldSelf);
        EntityRemoves++;
    }

    /// <summary>cl.csqc_server2csqcentitynumber: the program's entity for a server entity, or 0.</summary>
    public int EdictForServerEntity(int serverEntity) =>
        (uint)serverEntity < DpProtocol.MaxEdicts ? _serverToCsqc[serverEntity] : 0;

    // ---- entities ----------------------------------------------------------------------------------

    /// <summary>CSQC_Think: run an entity's think function if its time has come.</summary>
    internal void Think(int edict)
    {
        int think = Vm.FieldInt(edict, Fields.Think);
        if (think == 0) return;
        float next = Vm.FieldFloat(edict, Fields.NextThink);
        if (next == 0 || !(next <= GetFloat(Globals.Time))) return;
        Vm.FieldFloat(edict, Fields.NextThink) = 0;
        int oldSelf = GetInt(Globals.Self);
        SetInt(Globals.Self, edict);
        Vm.Execute(think);
        SetInt(Globals.Self, oldSelf);
    }

    /// <summary>CSQC_Predraw: run an entity's predraw function.</summary>
    internal void Predraw(int edict)
    {
        int predraw = Vm.FieldInt(edict, Fields.Predraw);
        if (predraw == 0) return;
        int oldSelf = GetInt(Globals.Self);
        SetInt(Globals.Self, edict);
        Vm.Execute(predraw);
        SetInt(Globals.Self, oldSelf);
    }

    /// <summary>CL_GetModelFromEdict: the name of an entity's model, or null if it is free or has none.</summary>
    public string? ModelNameOf(int edict)
    {
        if ((uint)edict >= (uint)Vm.MaxEdicts || Vm.IsFree(edict)) return null;
        return State.ModelNameForIndex(QcVm.FloatToInt(Vm.FieldFloat(edict, Fields.ModelIndex)));
    }

    /// <summary>A model's existence and normal bounds, asked of the presentation once per name.</summary>
    internal bool ModelBounds(string name, out QcVector mins, out QcVector maxs)
    {
        if (!_modelBounds.TryGetValue(name, out (bool Exists, QcVector Mins, QcVector Maxs) entry))
        {
            // "null" is a model the engine makes up (model_shared.c Mod_LoadModel): nothing to draw,
            // zero size, always loaded. Programs precache it for entities that exist only as logic.
            entry.Exists = name == "null" || Presentation.Models.TryGetBounds(name, out entry.Mins, out entry.Maxs);
            // Bounded by what can name a model: the two precache tables.
            if (_modelBounds.Count < 2 * DpProtocol.MaxModels) _modelBounds[name] = entry;
        }
        mins = entry.Mins;
        maxs = entry.Maxs;
        return entry.Exists;
    }

    /// <summary>
    /// CL_LinkEdict: recompute an entity's absolute box and file it where area queries find it.
    /// </summary>
    /// <remarks>
    /// For a SOLID_BSP entity the C uses the model's own box, and a larger one (yawmins, rotatedmins)
    /// when the entity is turned. Only the unrotated box is known here, so a rotated brush entity
    /// gets a box that is too small.
    /// </remarks>
    internal void LinkEdict(int edict)
    {
        if (edict <= 0 || edict >= Vm.NumEdicts || Vm.IsFree(edict)) return; // "don't add the world"
        QcVector origin = Vm.FieldVector(edict, Fields.Origin);
        QcVector mins = Vm.FieldVector(edict, Fields.Mins), maxs = Vm.FieldVector(edict, Fields.Maxs);
        if (Vm.FieldFloat(edict, Fields.Solid) == SolidBsp)
        {
            string? model = ModelNameOf(edict);
            if (model is null) Services.Print($"edict {edict}: SOLID_BSP with invalid modelindex!\n");
            // "SOLID_BSP with no model is valid, mainly because some QC setup code does so temporarily"
            else if (ModelBounds(model, out QcVector modelMins, out QcVector modelMaxs)) { mins = modelMins; maxs = modelMaxs; }
        }
        QcVector absMin = new(origin.X + mins.X, origin.Y + mins.Y, origin.Z + mins.Z);
        QcVector absMax = new(origin.X + maxs.X, origin.Y + maxs.Y, origin.Z + maxs.Z);
        Vm.FieldVector(edict, Fields.AbsMin) = absMin;
        Vm.FieldVector(edict, Fields.AbsMax) = absMax;

        if (edict >= _linked.Length)
        {
            int size = Math.Max(edict + 1, _linked.Length * 2);
            Array.Resize(ref _linked, size);
            Array.Resize(ref _linkMins, size);
            Array.Resize(ref _linkMaxs, size);
        }
        if (!_linked[edict])
        {
            int at = Array.BinarySearch(_linkedIds, 0, _linkedCount, edict);
            if (at < 0)
            {
                at = ~at;
                if (_linkedCount == _linkedIds.Length) Array.Resize(ref _linkedIds, _linkedIds.Length * 2);
                Array.Copy(_linkedIds, at, _linkedIds, at + 1, _linkedCount - at);
                _linkedIds[at] = edict;
                _linkedCount++;
            }
        }
        _linked[edict] = true;
        _linkMins[edict] = absMin;
        _linkMaxs[edict] = absMax;
        Presentation.World.LinkEdict(edict, absMin, absMax);
    }

    // CLVM_free_edict (World_UnlinkEdict).
    private void UnlinkEdict(int edict)
    {
        if ((uint)edict < (uint)_linked.Length && _linked[edict])
        {
            _linked[edict] = false;
            int at = Array.BinarySearch(_linkedIds, 0, _linkedCount, edict);
            if (at >= 0)
            {
                Array.Copy(_linkedIds, at + 1, _linkedIds, at, _linkedCount - at - 1);
                _linkedCount--;
            }
            Presentation.World.UnlinkEdict(edict);
        }
    }

    /// <summary>
    /// World_EntitiesInBox: the linked entities whose box (as last linked) touches the given box.
    /// Returns how many were written to <paramref name="edicts"/>.
    /// </summary>
    /// <remarks>In ascending entity order. DarkPlaces returns them in area-grid order, which depends on
    /// where and when each was linked; the set is the same.</remarks>
    internal int EntitiesInBox(QcVector mins, QcVector maxs, Span<int> edicts)
    {
        int count = 0;
        int end = Math.Min(Vm.NumEdicts, _linked.Length);
        int[] ids = _linkedIds;
        for (int i = 0; i < _linkedCount && count < edicts.Length; i++)
        {
            int e = ids[i];
            if (e >= end) break;
            if (e < 1 || Vm.IsFree(e)) continue;
            ref QcVector lo = ref _linkMins[e];
            ref QcVector hi = ref _linkMaxs[e];
            if (maxs.X < lo.X || mins.X > hi.X || maxs.Y < lo.Y || mins.Y > hi.Y || maxs.Z < lo.Z || mins.Z > hi.Z) continue;
            edicts[count++] = e;
        }
        return count;
    }
}

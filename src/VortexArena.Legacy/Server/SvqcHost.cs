// Port of Base/darkplaces/sv_main.c: SV_VM_Setup, SV_CheckRequiredFuncs, SV_SpawnServer, SV_VM_Shutdown,
// SVVM_init_edict, SVVM_free_edict, SVVM_load_edict, SV_ModelIndex, SV_SoundIndex,
// SV_ParticleEffectIndex, SV_GetModelByIndex, SV_ConnectClient (the bot half; the network half is
// SvServer), SV_DropClient, SV_SaveSpawnparms, SV_Prepare_CSQC; prvm_edict.c PRVM_ED_ClearEdict and
// PRVM_GameCommand / PRVM_ConsoleCommand; sv_send.c SV_FlushBroadcastMessages, SV_ClientCommands,
// SV_BroadcastPrint.
using System.Globalization;
using System.IO.Compression;
using VortexArena.Common.Config;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Server;

/// <summary>The server program could not be started. The message says exactly why.</summary>
public sealed class SvqcLoadException : Exception
{
    public SvqcLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>One fault of the server program: which engine entry point was running, and the VM's message with the QuakeC stack.</summary>
public sealed record SvFault(string EntryPoint, string Message, double Time);

/// <summary>Which console command asked for a level: they differ in what happens to the players.</summary>
public enum SvMapRequest
{
    /// <summary>SV_Changelevel_f: SV_SaveSpawnparms, then the new level with everyone carried over.</summary>
    ChangeLevel,
    /// <summary>SV_Map_f: SV_Shutdown (everyone dropped), svs.serverflags cleared, then the new level.</summary>
    Map,
    /// <summary>SV_Restart_f: the same level again with everyone carried over and the spawn parameters
    /// they arrived with - SV_SaveSpawnparms is not called.</summary>
    Restart,
}

/// <summary>server_state_t.</summary>
public enum SvState { Dead, Loading, Active }

public sealed class SvqcHostOptions
{
    /// <summary>svs.maxclients: player slots, and with them the entities reserved after the world.</summary>
    public int MaxClients { get; init; } = 8;
    /// <summary>sv_progs.</summary>
    public string ProgsName { get; init; } = "progs.dat";
    /// <summary>csqc_progname: the client program this server names to its clients, if the game data has it.</summary>
    public string CsqcProgName { get; init; } = "csprogs.dat";
    /// <summary>
    /// DarkPlaces ends the game when the server program faults (Host_Error). By default so does this:
    /// after the first fault nothing more runs. A diagnostic run can keep going instead; a fault then
    /// abandons the rest of that server frame only. The VM is unwound and safe to re-enter, but the
    /// program's own state is whatever the fault left behind.
    /// </summary>
    public bool KeepRunningAfterFault { get; init; }
    /// <summary>skill, deathmatch, coop as SV_SpawnServer reads them.</summary>
    public int Skill { get; init; } = 1;
    /// <summary>sv_random_seed: a fixed seed for the program's random(), for reproducible runs. Null: not seeded.</summary>
    public int? RandomSeed { get; init; }
    /// <summary>svs.serverflags as the level before left it. A level is a new <see cref="SvqcHost"/>,
    /// so whoever owns the sequence of levels hands the value on.</summary>
    public int ServerFlags { get; init; }
}

/// <summary>
/// One running level: the server program (progs.dat) on the QuakeC VM and the engine around it -
/// system globals and fields, the world and its area grid, precache tables, light styles, the
/// message buffers the program writes to, the player slots, and the frame loop (SvqcHost.Physics.cs).
/// DarkPlaces loads the program afresh for every level (SV_VM_Setup inside SV_SpawnServer), so one
/// instance is one level; the player slots are handed from one instance to the next.
///
/// The program is the game's own and is trusted about as far as DarkPlaces trusts it: it cannot
/// reach outside its VM, its entity numbers and offsets are checked, and a fault in it is recorded
/// (<see cref="Faults"/>) rather than thrown at the caller.
/// </summary>
public sealed partial class SvqcHost : IDisposable
{
    // server.h
    public const int SolidNot = 0, SolidTrigger = 1, SolidBBox = 2, SolidSlideBox = 3, SolidBsp = 4, SolidCorpse = 5;
    public const int MoveTypeNone = 0, MoveTypeAngleNoClip = 1, MoveTypeAngleClip = 2, MoveTypeWalk = 3, MoveTypeStep = 4,
        MoveTypeFly = 5, MoveTypeToss = 6, MoveTypePush = 7, MoveTypeNoClip = 8, MoveTypeFlyMissile = 9, MoveTypeBounce = 10,
        MoveTypeBounceMissile = 11, MoveTypeFollow = 12, MoveTypeFakePush = 13, MoveTypeFlyWorldOnly = 33,
        MoveTypePhysics = 32, MoveTypeUserFirst = 128, MoveTypeUserLast = 159;
    public const int FlFly = 1, FlSwim = 2, FlConveyor = 4, FlClient = 8, FlInWater = 16, FlMonster = 32, FlGodMode = 64,
        FlNoTarget = 128, FlItem = 256, FlOnGround = 512, FlPartialGround = 1024, FlWaterJump = 2048, FlJumpReleased = 4096;
    private const int SpawnFlagNotEasy = 256, SpawnFlagNotMedium = 512, SpawnFlagNotHard = 1024, SpawnFlagNotDeathmatch = 2048;

    private readonly SvqcHostOptions _options;
    private readonly QcCoreBuiltins _core;
    private readonly QcAutocvars _autocvars;
    private readonly List<SvFault> _faults = new();
    private readonly HostServices _hostServices;
    private bool _disposed;
    private EdictPrivate[] _priv = new EdictPrivate[1024];

    /// <summary>edict_engineprivate_t: what the engine keeps per entity beside its fields.</summary>
    internal struct EdictPrivate
    {
        /// <summary>false on spawn: "don't move on first frame" (sv_gameplayfix_delayprojectiles).</summary>
        public bool Move;
        /// <summary>The entity came to rest on a brush model, or was dropped to the floor: if that
        /// ground entity is later removed, it may stay where it is (suspended items).</summary>
        public bool SuspendedInAir;
        public bool WaterPositionForceUpdate;
        public QcVector WaterPositionOrigin;
        public QcVector MovedFrom, MovedFromAngles;
        /// <summary>PRVM_EDICT_MARK_*: 0, -1 wait-for-setorigin, -2 setorigin-caught (SV_Impact).</summary>
        public int Mark;
    }

    public QcVm Vm { get; }
    public ProgsFile Program => Vm.Progs;
    public SvFieldOffsets F { get; }
    public SvGlobalOffsets G { get; }
    public SvFunctions Fn { get; }
    public SvWorld World { get; }
    public LegacyQcHost Services { get; }
    public CvarService Cvars { get; }
    public ConfigInterpreter Interpreter { get; }
    public VirtualFileSystem Files { get; }
    public FormatLegacyModels Models { get; }
    public int AutocvarsBound { get; }

    /// <summary>svs.clients. Slot i is entity i + 1.</summary>
    public SvClient[] Clients { get; }
    public int MaxClients => Clients.Length;

    public SvState State { get; private set; } = SvState.Dead;
    /// <summary>sv.time: seconds of game time. Starts at 1.0, as in DarkPlaces.</summary>
    public double Time { get; private set; } = 1.0;
    /// <summary>sv.frametime: the length of the frame being run.</summary>
    public double FrameTime { get; private set; }
    /// <summary>host.realtime: wall-clock seconds, supplied by the owner (never read from a clock here).</summary>
    public double RealTime { get; set; }
    public bool Paused { get; internal set; }
    public double PausedStart { get; internal set; }
    /// <summary>svs.serverflags: "episode completion information", carried across levels.</summary>
    public int ServerFlags { get; set; }
    /// <summary>What the pending level change is: <see cref="SvMapRequest"/>.</summary>
    public SvMapRequest PendingMapKind { get; internal set; }
    /// <summary>svs.changelevel_issued: the changelevel builtin was called; cleared by the next level.</summary>
    public bool ChangeLevelIssued { get; internal set; }
    /// <summary>The map the program asked to change to (changelevel, or the map / changelevel console commands), or null.</summary>
    public string? PendingMap { get; internal set; }
    /// <summary>Whether <see cref="PendingMap"/> came from "map" (drop everyone) rather than "changelevel" or "restart" (carry players over).</summary>
    public bool PendingMapIsRestart => PendingMapKind == SvMapRequest.Map;

    /// <summary>sv.worldname "maps/x.bsp", sv.worldnamenoextension "maps/x", sv.worldbasename "x".</summary>
    public string WorldName { get; private set; } = "";
    public string WorldNameNoExtension { get; private set; } = "";
    public string WorldBaseName { get; private set; } = "";

    // sv.model_precache / sv.sound_precache: index 0 is "", and the list ends at the first empty name.
    private readonly string[] _modelPrecache = new string[DpProtocol.MaxModels];
    private readonly string[] _soundPrecache = new string[DpProtocol.MaxSounds];
    private readonly SvModelBounds?[] _modelBounds = new SvModelBounds?[DpProtocol.MaxModels];
    private readonly bool[] _modelIsBrush = new bool[DpProtocol.MaxModels];
    private readonly Dictionary<string, int> _modelIndex = new(StringComparer.Ordinal), _soundIndex = new(StringComparer.Ordinal);
    private int _modelCount = 1, _soundCount = 1;
    private CsqcEffectInfo? _effects;

    /// <summary>sv.lightstyles.</summary>
    public string[] LightStyles { get; } = new string[64];   // the server's MAX_LIGHTSTYLES check is "style >= 64"

    /// <summary>sv.datagram: unreliable, to everyone in the game; flushed into each client's queue as written.</summary>
    public DpMessageWriter Datagram { get; } = new(DpProtocol.NetMaxMessage);
    /// <summary>sv.reliable_datagram: reliable, to everyone connected; appended to each client's message once a frame.</summary>
    public DpMessageWriter ReliableDatagram { get; } = new(DpProtocol.NetMaxMessage);
    /// <summary>sv.signon: what every client is sent between "prespawn" and "spawn" (static entities and sounds, MSG_INIT writes).</summary>
    public DpMessageWriter Signon { get; } = new(DpProtocol.NetMaxMessage);
    /// <summary>sv.writeentitiestoclient_msg: where MSG_ENTITY writes go while a SendEntity function runs. Null otherwise.</summary>
    public DpMessageWriter? EntityMessage { get; set; }

    /// <summary>sv.csqc_progname / progsize / progcrc and svs.csqc_progdata(_deflated): the client program this server names.</summary>
    public string CsqcProgName { get; private set; } = "";
    public int CsqcProgSize { get; private set; }
    public int CsqcProgCrc { get; private set; } = -1;
    public byte[]? CsqcProgData { get; private set; }
    public byte[]? CsqcProgDataDeflated { get; private set; }

    /// <summary>prog->filecrc: CRC-16 of the server program file, which the server greets clients with.</summary>
    public int ProgramCrc { get; }
    public int ProgramSize { get; }

    public bool Faulted => FaultCount > 0;
    public int FaultCount { get; private set; }
    /// <summary>The first 256 faults.</summary>
    public IReadOnlyList<SvFault> Faults => _faults;
    private bool CanRun => !_disposed && State != SvState.Dead && (FaultCount == 0 || _options.KeepRunningAfterFault);

    /// <summary>Builtins the program called that nothing implements: (number, name) to call count. Each such call returned 0.</summary>
    public Dictionary<(int Number, string Name), long> UnimplementedBuiltins { get; } = new();
    /// <summary>VM warnings (VM_Warning) by text, with counts; bounded.</summary>
    public Dictionary<string, int> Warnings { get; } = new(StringComparer.Ordinal);
    /// <summary>Server frames run (SV_Physics calls).</summary>
    public long Frames { get; private set; }

    /// <summary>An entity was freed (SVVM_free_edict's "make sure csqc networking is aware of the removed entity").</summary>
    public event Action<int>? EdictFreed;
    /// <summary>A client slot emptied or filled, for whoever keeps network state beside the slots.</summary>
    public event Action<SvClient, string?, bool>? ClientDropped;
    /// <summary>Console output of the engine itself (Con_Printf), as opposed to the program's print.</summary>
    public Action<string> Print { get; set; } = _ => { };

    // IQcHost for the builtins, on the server's own clock: entity slots are reused by host.realtime,
    // and a server that is fed its time is deterministic only if that clock is fed too.
    private sealed class HostServices : IQcHost
    {
        private readonly SvqcHost _host;
        private readonly LegacyQcHost _inner;
        public HostServices(SvqcHost host, LegacyQcHost inner) { _host = host; _inner = inner; }
        public void Print(string text) => _inner.Print(text);
        public void Warning(string text) => _host.Warning(text);
        public bool Developer => _inner.Developer;
        public bool Utf8Enabled => _inner.Utf8Enabled;
        public double RealTime => _host.RealTime;
        public bool CvarExists(string name) => _inner.CvarExists(name);
        public string CvarString(string name) => _inner.CvarString(name);
        // Cvar_VariableValue is atof(var->string), and the C library's atof("nan") is the quiet NaN
        // with the sign bit CLEAR (0x7FC00000; "-nan" sets it), while .NET's float.NaN - what a parse
        // of "nan" yields - has it SET (0xFFC00000). Both are NaN to every comparison, but Xonotic
        // sends two such cvars to every client as stats (sv_jumpspeedcap_min / _max, "nan" meaning
        // "no cap"), and a stat is sent as its bits.
        public float CvarFloat(string name)
        {
            float value = _inner.CvarFloat(name);
            if (!float.IsNaN(value)) return value;
            bool negative = _inner.CvarString(name).AsSpan().TrimStart().StartsWith("-");
            return BitConverter.Int32BitsToSingle(negative ? unchecked((int)0xFFC00000) : 0x7FC00000);
        }
        public string CvarDefaultString(string name) => _inner.CvarDefaultString(name);
        public string CvarDescription(string name) => _inner.CvarDescription(name);
        public int CvarTypeFlags(string name) => _inner.CvarTypeFlags(name);
        public void CvarSet(string name, string value) => _inner.CvarSet(name, value);
        public bool RegisterCvar(string name, string value, int flags) => _inner.RegisterCvar(name, value, flags);
        public IEnumerable<string> CvarNames(string prefix, string antiPrefix) => _inner.CvarNames(prefix, antiPrefix);
        public void LocalCommand(string text) => _inner.LocalCommand(text);
        public Stream? OpenRead(string path) => _inner.OpenRead(path);
        public Stream? OpenWrite(string path, bool append) => _inner.OpenWrite(path, append);
        // FS_Search matches with matchpattern_with_separator(pattern, name, caseinsensitive, "/\:", false):
        // a wildcard never crosses a directory separator. The shared file service lets it, which makes
        // "maps/*.bsp" find "maps/_init/_init.bsp" - a map name the program then rejects with a warning.
        public IReadOnlyList<string> Search(string pattern, bool caseInsensitive, string? packFile)
        {
            IReadOnlyList<string> found = _inner.Search(pattern, caseInsensitive, packFile);
            if (found.Count == 0 || pattern.IndexOfAny(Wildcards) < 0) return found;
            int depth = pattern.Count(c => c == '/');
            List<string> kept = new(found.Count);
            foreach (string name in found)
                if (name.Count(c => c == '/') == depth) kept.Add(name);
            return kept;
        }
        private static readonly char[] Wildcards = { '*', '?' };
        public string WhichPack(string path) => _inner.WhichPack(path);
    }

    /// <summary>
    /// SV_VM_Setup: loads the server program and prepares it to run. The level itself is started by
    /// <see cref="SpawnServer"/>.
    /// </summary>
    /// <param name="program">The bytes of progs.dat.</param>
    /// <param name="clients">The player slots of a server already running (a level change), or null for new ones.</param>
    /// <exception cref="SvqcLoadException">Not a valid program, or not a server program.</exception>
    public SvqcHost(ReadOnlySpan<byte> program, LegacyQcHost services, CvarService cvars, ConfigInterpreter interpreter,
        VirtualFileSystem files, SvqcHostOptions? options = null, SvClient[]? clients = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Cvars = cvars ?? throw new ArgumentNullException(nameof(cvars));
        Interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        Files = files ?? throw new ArgumentNullException(nameof(files));
        _options = options ?? new SvqcHostOptions();
        int maxClients = clients?.Length ?? Math.Clamp(_options.MaxClients, 1, DpProtocol.MaxScoreboard);
        if (clients is null)
        {
            clients = new SvClient[maxClients];
            for (int i = 0; i < maxClients; i++) clients[i] = new SvClient(i);
        }
        Clients = clients;
        ServerFlags = _options.ServerFlags;
        _hostServices = new HostServices(this, services);

        ProgramSize = program.Length;
        ProgramCrc = Crc16.Block(program);
        ProgsFile progs;
        try { progs = ProgsFile.Load(program); }
        catch (ProgsFormatException e) { throw new SvqcLoadException($"server: {_options.ProgsName} failed to load: {e.Message}", e); }

        // prog->max_edicts = 512, limit_edicts = MAX_EDICTS, reserved_edicts = svs.maxclients.
        Vm = new QcVm(progs, "server", 512) { EdictLimit = DpProtocol.MaxEdicts };

        // SV_CheckRequiredFuncs: the entry points every client's life goes through.
        foreach (string required in new[] { "ClientConnect", "ClientDisconnect", "ClientKill", "PlayerPostThink", "PlayerPreThink", "PutClientInServer", "SetChangeParms", "SetNewParms", "StartFrame" })
            if (Vm.FindFunction(required) == 0)
                throw new SvqcLoadException($"server: {required} function not found in {_options.ProgsName}");

        try
        {
            // Engine fields first: they can only be appended while the world is the only entity.
            F = new SvFieldOffsets(Vm);
        }
        catch (QcRuntimeException e) { throw new SvqcLoadException($"server: {_options.ProgsName} failed to load: {e.Message}", e); }
        G = new SvGlobalOffsets(Vm);
        if (G.Missing.Count > 0)
            throw new SvqcLoadException($"server: {_options.ProgsName} lacks the system globals {string.Join(", ", G.Missing)}");
        Fn = new SvFunctions(Vm);

        World = new SvWorld(files);
        Models = new FormatLegacyModels(files);

        HashSet<string> extensions = new(CsqcExtensions.All, StringComparer.OrdinalIgnoreCase);
        _core = new QcCoreBuiltins(Vm, _hostServices, extensions)
        {
            ReservedEdicts = maxClients,
            EdictSpawned = InitEdict,
            EdictFreeing = FreeingEdict,
            EdictLinked = LinkEdict,
        };
        _core.Register();
        QcStringBuiltins strings = new(Vm, _hostServices) { OpenFile = _core.FileStream };
        strings.Register();
        RegisterBuiltins();
        Vm.UnknownBuiltin = (_, number, name) =>
            UnimplementedBuiltins[(number, name)] = UnimplementedBuiltins.GetValueOrDefault((number, name)) + 1;

        _autocvars = new QcAutocvars(Vm, _hostServices);
        AutocvarsBound = _autocvars.Bind();
        services.CvarChanged += OnCvarChanged;

        World.Attach(Vm, F, ModelNameOfEdict);
        Array.Fill(_modelPrecache, "");
        Array.Fill(_soundPrecache, "");
        Array.Fill(LightStyles, "");
        RegisterConsoleCommands();
        PrepareCsqc();
    }

    private void OnCvarChanged(string name)
    {
        if (!_disposed) _autocvars.Update(name);
        _cvarsDirty = true;
    }

    /// <summary>The program's builtins that are engine-independent, for a caller that needs its files or searches.</summary>
    public QcCoreBuiltins Core => _core;

    // ---- faults and warnings ---------------------------------------------------------------------------

    private void Warning(string text)
    {
        if (Warnings.Count < 512 || Warnings.ContainsKey(text)) Warnings[text] = Warnings.GetValueOrDefault(text) + 1;
        Services.Warning(text);
    }

    private void RecordFault(string entryPoint, string message)
    {
        FaultCount++;
        if (_faults.Count < 256) _faults.Add(new SvFault(entryPoint, message, Time));
        Print($"server: QuakeC fault in {entryPoint}: {message}\n");
    }

    /// <summary>
    /// SVVM_ExecuteProgram from inside the engine's own loops: runs a function and lets a fault
    /// propagate, so that it abandons the whole engine operation (the frame, the level load) the way
    /// Host_Error does. A null function is the fault DarkPlaces makes of it.
    /// </summary>
    internal void Exec(int function, string missingMessage)
    {
        if (function <= 0) throw new QcRuntimeException($"server: SVVM_ExecuteProgram: {missingMessage}");
        Vm.Execute(function);
    }

    /// <summary>Runs one engine operation; a fault inside it is recorded and ends it. False if it faulted or the program may not run.</summary>
    internal bool Guard(string entryPoint, Action operation)
    {
        if (!CanRun) return false;
        try
        {
            operation();
            return true;
        }
        catch (QcRuntimeException e)
        {
            RecordFault(entryPoint, e.Message);
            return false;
        }
    }

    // ---- globals and fields ----------------------------------------------------------------------------

    internal int Self { get => Vm.GlobalInt(G.Self); set => Vm.GlobalInt(G.Self) = value; }
    internal int Other { get => Vm.GlobalInt(G.Other); set => Vm.GlobalInt(G.Other) = value; }
    internal void SetTime(double time) => Vm.GlobalFloat(G.Time) = (float)time;

    internal ref float Fl(int edict, int field) => ref Vm.FieldFloat(edict, field);
    internal ref QcVector Vec(int edict, int field) => ref Vm.FieldVector(edict, field);
    internal ref int Int(int edict, int field) => ref Vm.FieldInt(edict, field);
    internal int IntFlags(int edict) => QcVm.FloatToInt(Vm.FieldFloat(edict, F.Flags));

    /// <summary>An entity field holding an entity number, made safe to index with: 0 for anything out of range.</summary>
    internal int EdictField(int edict, int field)
    {
        int value = Vm.FieldInt(edict, field);
        return (uint)value < (uint)Vm.NumEdicts ? value : 0;
    }

    internal ref EdictPrivate Priv(int edict)
    {
        if (edict >= _priv.Length) Array.Resize(ref _priv, Math.Min(DpProtocol.MaxEdicts, Math.Max(edict + 1, _priv.Length * 2)));
        return ref _priv[edict];
    }

    /// <summary>True for an entity number the engine may act on: in range, spawned, not freed.</summary>
    public bool IsLive(int edict) => (uint)edict < (uint)Vm.NumEdicts && !Vm.IsFree(edict);

    // ---- edict hooks -----------------------------------------------------------------------------------

    // SVVM_init_edict: "for consistency set these here".
    private void InitEdict(int edict)
    {
        Priv(edict) = default;   // move = false: "don't move on first frame"
        int num = edict - 1;
        if (num < 0 || num >= Clients.Length) return;
        SvClient client = Clients[num];
        // set colormap and team on newly created player entity
        Fl(edict, F.ColorMap) = num + 1;
        Fl(edict, F.Team) = (client.Colors & 15) + 1;
        // set netname/clientcolors back to client values so that DP_SV_CLIENTNAME and
        // DP_SV_CLIENTCOLORS will not immediately reset them
        Int(edict, F.NetName) = SlotString(ref client.NetNameHandle, client.Name);
        Fl(edict, F.ClientColors) = client.Colors;
        // NEXUIZ_PLAYERMODEL and NEXUIZ_PLAYERSKIN
        Int(edict, F.PlayerModel) = SlotString(ref client.PlayerModelHandle, client.PlayerModel);
        Int(edict, F.PlayerSkin) = SlotString(ref client.PlayerSkinHandle, client.PlayerSkin);
        Int(edict, F.NetAddress) = SlotString(ref client.NetAddressHandle, client.Connection is null ? "null/botclient" : client.NetAddress);
        // No d0_blind_id: nobody is authenticated, and the crypto_* fields say so.
        Int(edict, F.CryptoIdfp) = Int(edict, F.CryptoKeyfp) = Int(edict, F.CryptoMyKeyfp) = 0;
        Int(edict, F.CryptoEncryptMethod) = Int(edict, F.CryptoSignMethod) = 0;
        Fl(edict, F.CryptoIdfpSigned) = 0;
    }

    // PRVM_SetEngineString of a buffer in client_t: one handle per slot and field, rewritten in place.
    private int SlotString(ref int handle, string text)
    {
        if (handle == 0) handle = Vm.AllocString(text);
        else Vm.SetString(handle, text);
        return handle;
    }

    /// <summary>SV_Name's first line: the player entity's .netname is the slot's name.</summary>
    internal void SetSlotName(SvClient client) => Int(client.Edict, F.NetName) = SlotString(ref client.NetNameHandle, client.Name);

    /// <summary>The player entity's .playermodel and .playerskin are the slot's.</summary>
    internal void SetSlotModel(SvClient client)
    {
        Int(client.Edict, F.PlayerModel) = SlotString(ref client.PlayerModelHandle, client.PlayerModel);
        Int(client.Edict, F.PlayerSkin) = SlotString(ref client.PlayerSkinHandle, client.PlayerSkin);
    }

    /// <summary>PRVM_ED_ClearEdict: zero an entity and run the engine's init for it.</summary>
    internal void ClearEdict(int edict)
    {
        World.UnlinkEdict(edict);
        Vm.ClearEdict(edict);
        InitEdict(edict);
    }

    // SVVM_free_edict. The VM zeroes the fields after this, so only what lives outside them is done here.
    private void FreeingEdict(int edict)
    {
        World.UnlinkEdict(edict);
        Priv(edict) = default;
        EdictFreed?.Invoke(edict);
    }

    // ---- SV_SpawnServer --------------------------------------------------------------------------------

    /// <summary>
    /// SV_SpawnServer: load the map, set up the world entity and the player slots, spawn the map's
    /// entities through the program, and run the settling frames. False if the map could not be
    /// loaded (nothing has run) or the program faulted while starting (<see cref="Faults"/>).
    /// </summary>
    /// <param name="map">The map's base name ("stormkeep"), as the map command takes it.</param>
    public bool SpawnServer(string map)
    {
        if (_disposed || State != SvState.Dead) throw new InvalidOperationException("this host already ran a level");
        Print($"SpawnServer: {map}\n");
        string modelName = $"maps/{map}.bsp";
        if (!LegacyQcHost.IsSafePath(modelName) || !Files.Exists(modelName))
        {
            Print($"SpawnServer: no map file named {modelName}\n");
            return false;
        }
        World.LinkSolidNot = !Cvars.Has("sv_areagrid_link_SOLID_NOT") || Cvars.GetFloat("sv_areagrid_link_SOLID_NOT") != 0;
        if (!World.LoadMap(modelName))
        {
            Print($"Couldn't load map {modelName}: {World.LoadError}\n");
            return false;
        }

        // let's not have any servers with no name
        if (Cvars.GetString("hostname").Length == 0) SetCvar("hostname", "UNNAMED");
        ChangeLevelIssued = false;   // now safe to issue another

        // make cvars consistant
        if (Cvars.GetFloat("coop") != 0) SetCvar("deathmatch", "0");
        SetCvar("halflifebsp", "0");
        SetCvar("sv_mapformat_is_quake2", "0");
        SetCvar("sv_mapformat_is_quake3", "1");
        if (_options.RandomSeed is { } seed) _core.Random = new Random(seed);

        // set level base name variables for later use
        WorldName = modelName;
        WorldNameNoExtension = modelName[..^4];
        WorldBaseName = WorldNameNoExtension.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) ? WorldNameNoExtension[5..] : WorldNameNoExtension;
        SetCvar("sv_worldname", WorldName);
        SetCvar("sv_worldnamenoextension", WorldNameNoExtension);
        SetCvar("sv_worldbasename", WorldBaseName);

        State = SvState.Loading;
        Paused = false;
        Time = 1.0;
        RefreshCvars();

        // leave slots at start for clients only
        Vm.ReserveEdicts(Clients.Length);

        // clear world interaction links; the map and its submodels take the first precache slots
        _modelPrecache[1] = WorldName;
        _modelIndex[WorldName] = 1;
        World.Bounds(out QcVector worldMins, out QcVector worldMaxs);
        _modelBounds[1] = SvModelBounds.FromNormal(worldMins, worldMaxs);
        _modelIsBrush[1] = true;
        _modelCount = 2;
        for (int i = 1; i < World.NumSubmodels && i + 1 < DpProtocol.MaxModels; i++)
        {
            string name = "*" + i.ToString(CultureInfo.InvariantCulture);
            _modelPrecache[i + 1] = name;
            _modelIndex[name] = i + 1;
            _modelCount = i + 2;
        }

        bool ok = Guard("SV_SpawnServer", () =>
        {
            // load the rest of the entities
            Vm.ClearEdict(0);
            Int(0, F.Model) = Vm.EngineString(WorldName);
            Fl(0, F.ModelIndex) = 1;   // world model
            Fl(0, F.Solid) = SolidBsp;
            Fl(0, F.MoveType) = MoveTypePush;
            Vec(0, F.Mins) = worldMins;
            Vec(0, F.Maxs) = worldMaxs;
            Vec(0, F.AbsMin) = worldMins;
            Vec(0, F.AbsMax) = worldMaxs;

            int coop = (int)Cvars.GetFloat("coop"), deathmatch = (int)Cvars.GetFloat("deathmatch");
            if (coop != 0) Vm.GlobalFloat(G.Coop) = coop;
            else Vm.GlobalFloat(G.Deathmatch) = deathmatch;
            Vm.GlobalInt(G.MapName) = Vm.EngineString(WorldBaseName);
            // serverflags are for cross level information (sigils)
            Vm.GlobalFloat(G.ServerFlags) = ServerFlags;

            // we need to reset the spawned flag on all connected clients here so that their thinks
            // don't run during startup (before PutClientInServer); we also need to set up the client
            // entities now
            foreach (SvClient client in Clients)
            {
                client.Begun = false;
                client.NetNameHandle = client.PlayerModelHandle = client.PlayerSkinHandle = client.NetAddressHandle = 0;
                ClearEdict(client.Edict);
            }

            // load replacement entity file if found
            string entities = World.EntitiesText;
            string entFile = WorldNameNoExtension + ".ent";
            if (Cvars.GetFloat("sv_entpatch") != 0 && Services.ReadFile(entFile) is { } replacement)
            {
                Print($"Loaded {entFile}\n");
                entities = System.Text.Encoding.UTF8.GetString(replacement);
            }
            int skill = _options.Skill;
            _core.LoadMapEntities(entities, loadIntoWorld: true,
                keep: edict =>
                {
                    // SVVM_load_edict: remove things from different skill levels or deathmatch
                    int flags = QcVm.FloatToInt(Fl(edict, F.SpawnFlags));
                    if (deathmatch != 0) return (flags & SpawnFlagNotDeathmatch) == 0;
                    return !((skill <= 0 && (flags & SpawnFlagNotEasy) != 0) || (skill == 1 && (flags & SpawnFlagNotMedium) != 0)
                        || (skill >= 2 && (flags & SpawnFlagNotHard) != 0));
                },
                beforeCall: () => SetTime(Time));

            // LadyHavoc: clear world angles (to fix e3m3.bsp)
            Vec(0, F.Angles) = default;

            // run two frames to allow everything to settle
            Time = 1.0001;
            int settle = Cvars.Has("sv_init_frame_count") ? (int)Cvars.GetFloat("sv_init_frame_count") : 2;
            for (int i = 0; i < settle; i++)
            {
                FrameTime = 0.1;
                Physics();
            }
            // Once all init frames have been run, we consider svqc code fully initialized.
            _core.StartTime = RealTime;
        });
        if (!ok && !_options.KeepRunningAfterFault) return false;

        State = SvState.Active;

        // set up botclients coming back from a level change; a connected client is sent the new
        // level's serverinfo by the network layer
        foreach (SvClient client in Clients)
        {
            client.ClientConnectCalled = false;   // do NOT call ClientDisconnect if he drops before ClientConnect!
            if (!client.Active || client.Connection is not null) continue;
            SvClient bot = client;
            Guard("ClientConnect", () =>
            {
                for (int j = 0; j < SvClient.NumSpawnParms; j++) Vm.GlobalFloat(G.Parm1 + j) = bot.SpawnParms[j];
                bot.ClientConnectCalled = true;
                SetTime(Time);
                Self = bot.Edict;
                Exec(Fn.ClientConnect, "QC function ClientConnect is missing");
                Exec(Fn.PutClientInServer, "QC function PutClientInServer is missing");
                bot.Begun = true;
            });
        }

        // update the map title cvar (not related to filename)
        SetCvar("sv_worldmessage", Vm.GetString(Int(0, F.Message)));
        Print("Server spawned.\n");
        return ok;
    }

    /// <summary>Whether SV_SpawnServer would find the map file for this name ("maps/NAME.bsp" in the game data).</summary>
    public bool MapExists(string map)
    {
        string modelName = $"maps/{map}.bsp";
        return LegacyQcHost.IsSafePath(modelName) && Files.Exists(modelName);
    }

    private void SetCvar(string name, string value)
    {
        if (Cvars.Has(name)) Cvars.Set(name, value);
        else Cvars.Register(name, value);
    }

    // SV_Prepare_CSQC: "Load csprogs.dat and compress it so it doesn't need to be reloaded on request."
    private void PrepareCsqc()
    {
        string name = Cvars.GetString("csqc_progname");
        if (name.Length == 0) name = _options.CsqcProgName;
        if (name.Length == 0 || !LegacyQcHost.IsSafePath(name) || Services.ReadFile(name) is not { Length: > 0 } data) return;
        CsqcProgName = name;
        CsqcProgData = data;
        CsqcProgSize = data.Length;
        CsqcProgCrc = Crc16.Block(data);
        // FS_Deflate(..., -1): raw DEFLATE, no zlib header - which is what the client inflates.
        using MemoryStream deflated = new();
        using (DeflateStream deflate = new(deflated, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
        CsqcProgDataDeflated = deflated.ToArray();
    }

    /// <summary>
    /// SV_VM_Shutdown: tell the program the level is over (SV_Shutdown, if it has one) and release
    /// what it held open. The slots are left as they are for the next level.
    /// </summary>
    public void Shutdown()
    {
        if (_disposed) return;
        if (State != SvState.Dead && Fn.SvShutdown != 0)
        {
            Guard("SV_Shutdown", () =>
            {
                SetTime(Time);
                Self = 0;
                Vm.Execute(Fn.SvShutdown);
            });
        }
        State = SvState.Dead;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        State = SvState.Dead;
        Services.CvarChanged -= OnCvarChanged;
        UnregisterConsoleCommands();
        _core.Dispose();
    }

    // ---- precaches -------------------------------------------------------------------------------------

    /// <summary>sv.model_precache, entries 1 up to the first empty one: what svc_serverinfo lists.</summary>
    public IEnumerable<string> PrecachedModels
    {
        get { for (int i = 1; i < _modelCount; i++) yield return _modelPrecache[i]; }
    }

    public IEnumerable<string> PrecachedSounds
    {
        get { for (int i = 1; i < _soundCount; i++) yield return _soundPrecache[i]; }
    }

    public string ModelName(int index) => (uint)index < (uint)_modelCount ? _modelPrecache[index] : "";
    public string SoundName(int index) => (uint)index < (uint)_soundCount ? _soundPrecache[index] : "";
    public int ModelCount => _modelCount;
    public int SoundCount => _soundCount;

    // dp_strlcpy(filename, s, sizeof(filename)): a precache name is cut at MAX_QPATH - 1.
    private static string QPath(string name) => name.Length < DpProtocol.MaxQPath ? name : name[..(DpProtocol.MaxQPath - 1)];

    /// <summary>
    /// SV_ModelIndex. <paramref name="precacheMode"/>: 0 look up only; 1 precache with a complaint
    /// (used by a model that was not precached); 2 precache (precache_model).
    /// </summary>
    public int ModelIndex(string name, int precacheMode)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        name = QPath(name);
        if (_modelIndex.TryGetValue(name, out int index)) return index;
        if (_modelCount >= DpProtocol.MaxModels)
        {
            Print($"SV_ModelIndex(\"{name}\"): i ({_modelCount}) == MAX_MODELS ({DpProtocol.MaxModels})\n");
            return 0;
        }
        if (precacheMode == 0)
        {
            Print($"SV_ModelIndex(\"{name}\"): not precached\n");
            return 0;
        }
        if (precacheMode == 1) Print($"SV_ModelIndex(\"{name}\"): not precached (fix your code), precaching anyway\n");
        index = _modelCount++;
        _modelPrecache[index] = name;
        _modelIndex[name] = index;
        if (State != SvState.Loading)
        {
            // DP_SV_PRECACHEANYTIME: clients are told of a model precached after the level started.
            ReliableDatagram.WriteByte((int)Svc.Precache);
            ReliableDatagram.WriteShort(index);
            ReliableDatagram.WriteString(name);
        }
        return index;
    }

    /// <summary>SV_SoundIndex; see <see cref="ModelIndex"/>.</summary>
    public int SoundIndex(string name, int precacheMode)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        name = QPath(name);
        if (_soundIndex.TryGetValue(name, out int index)) return index;
        if (_soundCount >= DpProtocol.MaxSounds)
        {
            Print($"SV_SoundIndex(\"{name}\"): i ({_soundCount}) == MAX_SOUNDS ({DpProtocol.MaxSounds})\n");
            return 0;
        }
        if (precacheMode == 0)
        {
            Print($"SV_SoundIndex(\"{name}\"): not precached\n");
            return 0;
        }
        if (precacheMode == 1) Print($"SV_SoundIndex(\"{name}\"): not precached (fix your code), precaching anyway\n");
        index = _soundCount++;
        _soundPrecache[index] = name;
        _soundIndex[name] = index;
        if (State != SvState.Loading)
        {
            ReliableDatagram.WriteByte((int)Svc.Precache);
            ReliableDatagram.WriteShort(index + 32768);
            ReliableDatagram.WriteString(name);
        }
        return index;
    }

    /// <summary>SV_ParticleEffectIndex: the number of an effect name in effectinfo.txt's order, or 0.</summary>
    public int ParticleEffectIndex(string name)
    {
        _effects ??= CsqcEffectInfo.Load(Services.ReadFile, WorldNameNoExtension);
        return _effects.IndexForName(name);
    }

    /// <summary>
    /// SV_GetModelByIndex, reduced to what the server asks of a model: its three boxes, or null for
    /// no model (index 0, out of range, or a file that could not be loaded - whose box DarkPlaces
    /// leaves zero). Loaded on first use; DarkPlaces loads at precache time.
    /// </summary>
    internal SvModelBounds? ModelBounds(int index)
    {
        if (index <= 0 || index >= _modelCount) return null;
        if (_modelBounds[index] is { } known) return known;
        string name = _modelPrecache[index];
        SvModelBounds bounds;
        if (name.Length > 0 && name[0] == '*')
        {
            _modelIsBrush[index] = true;
            bounds = World.TryGetSubmodelBounds(name, out QcVector mins, out QcVector maxs) ? SvModelBounds.FromNormal(mins, maxs) : default;
        }
        // A Wavefront OBJ (map decoration) is not a format the shared model reader knows.
        else if (name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            bounds = Services.ReadFile(name) is { } obj && SvObjModel.TryGetBounds(obj, !Cvars.Has("mod_obj_orientation") || Cvars.GetFloat("mod_obj_orientation") != 0, out System.Numerics.Vector3 lo, out System.Numerics.Vector3 hi)
                ? SvModelBounds.FromNormal(new QcVector(lo.X, lo.Y, lo.Z), new QcVector(hi.X, hi.Y, hi.Z)) : default;
        else bounds = Models.TryGetBounds(name, out QcVector mins, out QcVector maxs) ? SvModelBounds.FromNormal(mins, maxs) : default;
        _modelBounds[index] = bounds;
        return bounds;
    }

    /// <summary>Whether the model is a brush model (the map or one of its submodels) rather than an alias model or sprite.</summary>
    internal bool ModelIsBrush(int index) => (uint)index < (uint)_modelCount && (index == 1 || (_modelPrecache[index].Length > 0 && _modelPrecache[index][0] == '*'));

    private string? ModelNameOfEdict(int edict)
    {
        if (!IsLive(edict)) return null;
        int index = QcVm.FloatToInt(Fl(edict, F.ModelIndex));
        return index > 0 && index < _modelCount ? _modelPrecache[index] : null;
    }

    // ---- messages --------------------------------------------------------------------------------------

    /// <summary>
    /// SV_FlushBroadcastMessages: what was just written to <see cref="Datagram"/> goes into the
    /// unreliable queue of every client in the game, as one unit a datagram may not split.
    /// </summary>
    public void FlushBroadcastMessages()
    {
        if (Datagram.Length <= 0) return;
        if (!Datagram.Overflowed)
        {
            foreach (SvClient client in Clients)
            {
                if (!client.Begun || client.Connection is not { } connection) continue;
                if (connection.UnreliableMsg.Length + Datagram.Length > connection.UnreliableMsg.MaxSize
                    || connection.UnreliableSplitPoints.Count >= SvConnection.MaxSplitPoints)
                    continue;
                connection.UnreliableMsg.WriteBytes(Datagram.WrittenSpan);
                connection.UnreliableSplitPoints.Add(connection.UnreliableMsg.Length);
            }
        }
        Datagram.Clear();
    }

    /// <summary>SV_ClientCommands: stuff console text into one client.</summary>
    public void ClientCommands(SvClient client, string text)
    {
        if (client.Connection is not { } connection) return;
        // char string[MAX_INPUTLINE]
        if (text.Length >= DpProtocol.MaxInputLine) text = text[..(DpProtocol.MaxInputLine - 1)];
        connection.Message.WriteByte((int)Svc.StuffText);
        connection.Message.WriteString(text);
    }

    /// <summary>SV_ClientPrint.</summary>
    public void ClientPrint(SvClient client, string text)
    {
        if (client.Connection is not { } connection) return;
        connection.Message.WriteByte((int)Svc.Print);
        connection.Message.WriteString(text);
    }

    /// <summary>SV_BroadcastPrint: to every connected client, and the server's own console.</summary>
    public void BroadcastPrint(string text)
    {
        foreach (SvClient client in Clients)
            if (client.Active) ClientPrint(client, text);
        Print(text);
    }

    // ---- clients ---------------------------------------------------------------------------------------

    /// <summary>
    /// SV_ConnectClient: fill a slot. With no <paramref name="connection"/> the client is a bot
    /// (the spawnclient builtin): it is in the game at once, and whoever called is to run
    /// ClientConnect and PutClientInServer for it (the program does, for its own bots).
    /// </summary>
    public void ConnectClient(int slot, SvConnection? connection, string netAddress = "")
    {
        SvClient client = Clients[slot];
        int name = client.NetNameHandle, model = client.PlayerModelHandle, skin = client.PlayerSkinHandle, address = client.NetAddressHandle;
        client.Clear();
        client.NetNameHandle = name; client.PlayerModelHandle = model; client.PlayerSkinHandle = skin; client.NetAddressHandle = address;
        client.Active = true;
        client.Connection = connection;
        client.NetAddress = netAddress;
        client.Name = client.OldName = "unconnected";
        client.ConnectTime = RealTime;

        // call the progs to get default spawn parms for the new client
        SetTime(Time);
        Self = 0;
        Exec(Fn.SetNewParms, "QC function SetNewParms is missing");
        for (int i = 0; i < SvClient.NumSpawnParms; i++) client.SpawnParms[i] = Vm.GlobalFloat(G.Parm1 + i);

        // set up the entity for this client (including .colormap, .team, etc)
        ClearEdict(client.Edict);

        if (connection is null) client.PreSpawned = client.Spawned = client.Begun = true;
    }

    /// <summary>
    /// SV_DropClient: "Called when the player is getting totally kicked off the host". Runs
    /// ClientDisconnect if ClientConnect ran, tells everyone the slot is empty, and clears it. The
    /// network layer hears of it through <see cref="ClientDropped"/> and says goodbye on the wire.
    /// </summary>
    /// <param name="leaving">The client is already gone (it said goodbye, or cannot be written to): "don't bother sending signofs".</param>
    public void DropClient(SvClient client, string? reason, bool leaving = false)
    {
        if (!client.Active) return;
        Print($"Client \"{client.Name}\" dropped{(reason is null ? "" : $" ({reason})")}\n");
        ClientDropped?.Invoke(client, reason, leaving);

        if (client.ClientConnectCalled && State != SvState.Dead)
        {
            // call qc ClientDisconnect function; this will set the body to a dead frame, among other things
            int saveSelf = Self;
            client.ClientConnectCalled = false;
            Guard("ClientDisconnect", () =>
            {
                SetTime(Time);
                Self = client.Edict;
                Exec(Fn.ClientDisconnect, "QC function ClientDisconnect is missing");
            });
            Self = saveSelf;
        }

        bool wasConnected = client.Connection is not null;
        string name = client.Name;
        client.Connection = null;
        if (wasConnected) BroadcastPrint(reason is null ? $"\x03^3{name} left the game\n" : $"\x03^3{name} left the game ({reason})\n");

        // send notification to all clients: an empty name, no colours, no frags
        int index = client.Index;
        ReliableDatagram.WriteByte((int)Svc.UpdateName);
        ReliableDatagram.WriteByte(index);
        ReliableDatagram.WriteString("");
        ReliableDatagram.WriteByte((int)Svc.UpdateColors);
        ReliableDatagram.WriteByte(index);
        ReliableDatagram.WriteByte(0);
        ReliableDatagram.WriteByte((int)Svc.UpdateFrags);
        ReliableDatagram.WriteByte(index);
        ReliableDatagram.WriteShort(0);

        int nameHandle = client.NetNameHandle, model = client.PlayerModelHandle, skin = client.PlayerSkinHandle, address = client.NetAddressHandle;
        client.Clear();
        client.NetNameHandle = nameHandle; client.PlayerModelHandle = model; client.PlayerSkinHandle = skin; client.NetAddressHandle = address;
        // clear a fields that matter to DP_SV_CLIENTNAME and DP_SV_CLIENTCOLORS, and also frags
        if (State != SvState.Dead) ClearEdict(client.Edict);
    }

    /// <summary>SV_SaveSpawnparms: "Grabs the current state of each client for saving across the transition to another level".</summary>
    public void SaveSpawnParms()
    {
        if (State == SvState.Dead) return;
        ServerFlags = QcVm.FloatToInt(Vm.GlobalFloat(G.ServerFlags));
        foreach (SvClient client in Clients)
        {
            if (!client.Active) continue;
            SvClient c = client;
            Guard("SetChangeParms", () =>
            {
                // call the progs to get default spawn parms for the new client
                SetTime(Time);
                Self = c.Edict;
                Exec(Fn.SetChangeParms, "QC function SetChangeParms is missing");
                for (int j = 0; j < SvClient.NumSpawnParms; j++) c.SpawnParms[j] = Vm.GlobalFloat(G.Parm1 + j);
            });
        }
    }

    /// <summary>The slot an entity number names, or null: 1..maxclients are the players.</summary>
    public SvClient? ClientOfEdict(int edict) => edict >= 1 && edict <= Clients.Length ? Clients[edict - 1] : null;

    public int ActiveClients
    {
        get
        {
            int count = 0;
            foreach (SvClient client in Clients) if (client.Active) count++;
            return count;
        }
    }
}

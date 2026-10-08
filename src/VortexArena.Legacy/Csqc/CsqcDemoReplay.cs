// Port of the playback half of Base/darkplaces/cl_demo.c CL_ReadDemoMessage as a batch job: every
// message of a recording goes through the protocol parser and the client program, with the points at
// which DarkPlaces would load the program (cl_parse.c CL_BeginDownloads, at signon 1) and draw a
// frame (cl_screen.c, once signed on) reproduced.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

public sealed class CsqcDemoReplayOptions
{
    /// <summary>Call CSQC_UpdateView once after each message that carried svc_time, as a frame would.
    /// Off, the run measures message decoding alone.</summary>
    public bool CallUpdateView { get; init; } = true;
    public float Width { get; init; } = 1024;
    public float Height { get; init; } = 768;
    /// <summary>Stop after this many messages.</summary>
    public int MaxMessages { get; init; } = int.MaxValue;
    /// <summary>Keep calling a program that has faulted, to see how much else works (see
    /// <see cref="CsqcHostOptions.KeepRunningAfterFault"/>).</summary>
    public bool KeepRunningAfterFault { get; init; } = true;
    /// <summary>Where the program may write files, or null for nowhere.</summary>
    public string? WriteRoot { get; init; }
    /// <summary>Called with each host as it is created, before CSQC_Init, for a caller that wants to look inside.</summary>
    public Action<CsqcHost>? HostCreated { get; init; }
    /// <summary>The collector the host services print into, if the report should include that output.</summary>
    public CsqcReplayLog? Log { get; init; }
}


/// <summary>
/// Collects what the program prints and the VM warnings it causes during a replay. The caller makes
/// one, points its <see cref="LegacyQcHost"/>'s PrintSink and WarningSink at it, and passes it in the
/// options; the result then carries the totals and a sample.
/// </summary>
public sealed class CsqcReplayLog
{
    private readonly StringBuilder _line = new();
    public int PrintLines { get; private set; }
    public int Warnings { get; private set; }
    public List<string> PrintSample { get; } = new();
    public Dictionary<string, int> WarningCounts { get; } = new(StringComparer.Ordinal);

    public void Print(string text)
    {
        foreach (char c in text)
        {
            if (c != '\n')
            {
                if (_line.Length < 400) _line.Append(c);
                continue;
            }
            PrintLines++;
            if (PrintSample.Count < 40) PrintSample.Add(_line.ToString());
            _line.Clear();
        }
    }

    public void Warning(string text)
    {
        Warnings++;
        text = text.TrimEnd();
        if (WarningCounts.Count < 4096 || WarningCounts.ContainsKey(text)) WarningCounts[text] = WarningCounts.GetValueOrDefault(text) + 1;
    }
}

/// <summary>What happened to one message that was not decoded to its end.</summary>
public sealed record CsqcReplayStop(int MessageIndex, long FileOffset, int Length, DpParseStatus Status, int Svc, int Offset, string? Reason, CsqcDesync? Desync)
{
    /// <summary>Whether the client program was decoding when it stopped (as opposed to the engine parser).</summary>
    public bool InQuakeC => Desync is not null || Status == DpParseStatus.Aborted;

    public override string ToString() =>
        $"message {MessageIndex} (file offset {FileOffset}, {Length} bytes): {Status} at svc {Svc} offset {Offset}: {Reason}"
        + (Desync is null ? "" : "\n      desync: " + Desync + "\n      stack:\n        " + Desync.Stack.Replace("\n", "\n      "));
}

/// <summary>The numbers of one replay. <see cref="Describe"/> renders them as the report.</summary>
public sealed class CsqcDemoReplayResult
{
    public string Name = "";
    public string? ReaderError;
    public int Messages, FullyDecoded, Stopped, StoppedInQuakeC, ProtocolErrors, ProtocolErrorsBeforeFirstQuakeCStop;
    public long Bytes, BytesDecoded;
    public readonly List<CsqcReplayStop> Stops = new();
    public CsqcReplayStop? FirstStop;
    public readonly SortedDictionary<int, int> StopsBySvc = new();
    public readonly Dictionary<string, int> StopsByPlace = new(StringComparer.Ordinal);

    public string ProgramName = "";
    public int ProgramSizeExpected = -1, ProgramCrcExpected = -1, ProgramSize = -1, ProgramCrc = -1;
    public bool ProgramFound, ProgramLoaded;
    public string? ProgramRefusal;
    public int Statements, Functions, Globals, FieldCells, AutocvarsBound;
    public int ProgramStartMessage = -1;
    public bool InitRan, InitCompleted;

    public long EntityUpdates, EntityRemoves, TempEntitiesConsumed, TempEntitiesDeclined, EngineTempEntities;
    public int Frames, FrameFaults;
    public int Faults;
    public readonly List<CsqcFault> FaultSamples = new();
    public readonly Dictionary<string, int> FaultsByPlace = new(StringComparer.Ordinal);
    public int Desyncs;
    public long ReadsOutsideMessage;
    public readonly List<(int Number, string Name, long Calls)> UnimplementedBuiltins = new();
    public readonly List<(int Number, string Name, bool Forwarded, long Calls)> BuiltinCalls = new();
    public readonly SortedDictionary<string, long> PresentationCalls = new(StringComparer.Ordinal);
    public long ConsoleLines, GameCommands, MenuCommands, ForwardedToServer;
    public int QcCommands;
    public readonly List<string> SignonStages = new();
    public string Level = "";
    public int EffectNames;
    public int Edicts;
    public int PrintLines, Warnings;
    public readonly List<string> PrintSample = new();
    public readonly Dictionary<string, int> WarningCounts = new(StringComparer.Ordinal);
    public double TotalSeconds, FrameSeconds, InitSeconds;
    public bool UpdateViewEnabled;
    /// <summary>An exception that escaped the host: always a bug in the host, never the program's doing.</summary>
    public Exception? Unexpected;

    public string Describe()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        StringBuilder s = new();
        s.AppendLine($"=== CSQC demo replay: {Name} ({(UpdateViewEnabled ? "with" : "without")} CSQC_UpdateView) ===");
        s.AppendLine($"demo: {Messages} messages, {Bytes} bytes; reader error: {ReaderError ?? "none"}; level: {Level}; signon stages: [{string.Join(",", SignonStages)}]");
        s.AppendLine($"program: {ProgramName}, announced size {ProgramSizeExpected} crc {ProgramCrcExpected}; embedded in demo: {(ProgramFound ? $"yes, size {ProgramSize} crc {ProgramCrc}" : "NO")}");
        if (ProgramLoaded)
            s.AppendLine($"program loaded at message {ProgramStartMessage}: {Statements} statements, {Functions} functions, {Globals} globals, {FieldCells} cells per entity, {AutocvarsBound} autocvars bound, {EffectNames} effect names");
        else s.AppendLine("program NOT loaded: " + (ProgramRefusal ?? "the demo never reached signon stage 1 with a program"));
        s.AppendLine($"CSQC_Init: {(InitRan ? (InitCompleted ? "completed" : "FAULTED") : "not run")} ({InitSeconds.ToString("F3", inv)} s)");
        s.AppendLine();
        s.AppendLine($"messages: {Messages} total, {FullyDecoded} fully decoded ({Percent(FullyDecoded, Messages)}), {Stopped} stopped ({StoppedInQuakeC} while the client program was decoding, {ProtocolErrors} in engine protocol)");
        s.AppendLine($"bytes decoded before any stop: {BytesDecoded} of {Bytes} ({Percent(BytesDecoded, Bytes)})");
        s.AppendLine($"engine-protocol parse errors before the first QuakeC-attributed stop: {ProtocolErrorsBeforeFirstQuakeCStop}");
        s.AppendLine($"entities: {EntityUpdates} CSQC_Ent_Update, {EntityRemoves} CSQC_Ent_Remove; {Edicts} entities at the end");
        s.AppendLine($"temp entities: {TempEntitiesConsumed} consumed by CSQC_Parse_TempEntity, {TempEntitiesDeclined} declined, {EngineTempEntities} decoded by the engine");
        s.AppendLine($"frames (CSQC_UpdateView): {Frames} run, {FrameFaults} faulted");
        s.AppendLine($"console: {ConsoleLines} lines executed, {GameCommands} cl_cmd, {MenuCommands} menu_cmd, {ForwardedToServer} cmd; {QcCommands} commands registered by the program");
        s.AppendLine($"VM faults: {Faults}; desyncs: {Desyncs}; network reads outside a message: {ReadsOutsideMessage}");
        s.AppendLine($"time: {TotalSeconds.ToString("F2", inv)} s total, {FrameSeconds.ToString("F2", inv)} s in CSQC_UpdateView"
            + (Frames > 0 ? $" ({(FrameSeconds * 1000 / Frames).ToString("F3", inv)} ms per frame)" : ""));
        s.AppendLine();

        if (FirstStop is not null)
        {
            s.AppendLine("FIRST STOP:");
            s.AppendLine("  " + FirstStop);
            s.AppendLine();
        }
        if (Stopped > 0)
        {
            s.AppendLine("stops by svc: " + string.Join(", ", StopsBySvc.Select(kv => $"{kv.Key} {SvcName(kv.Key)}: {kv.Value}")));
            s.AppendLine("stops by place (top 12):");
            foreach ((string place, int count) in StopsByPlace.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(12))
                s.AppendLine($"  {count,7}  {place}");
            s.AppendLine("first stops:");
            foreach (CsqcReplayStop stop in Stops.Take(6)) s.AppendLine("  " + stop);
            s.AppendLine();
        }

        if (Faults > 0)
        {
            s.AppendLine("VM faults by place (top 12):");
            foreach ((string place, int count) in FaultsByPlace.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(12))
                s.AppendLine($"  {count,7}  {place}");
            s.AppendLine("first faults, with QuakeC stacks:");
            foreach (CsqcFault fault in FaultSamples.Take(5))
                s.AppendLine($"  [{fault.EntryPoint}, message {fault.MessageIndex}, time {fault.Time.ToString("F3", inv)}] " + fault.Message.Replace("\n", "\n    "));
            s.AppendLine();
        }

        s.AppendLine($"unimplemented builtins CALLED ({UnimplementedBuiltins.Count} distinct, {UnimplementedBuiltins.Sum(u => u.Calls)} calls):");
        foreach ((int number, string name, long calls) in UnimplementedBuiltins) s.AppendLine($"  {calls,9}  #{number,-4} {name}");
        s.AppendLine();
        s.AppendLine("presentation calls by builtin (forwarded to ILegacyPresentation):");
        foreach ((int number, string name, bool _, long calls) in BuiltinCalls.Where(b => b.Forwarded)) s.AppendLine($"  {calls,9}  #{number,-4} {name}");
        s.AppendLine("host builtin calls (implemented in the host):");
        foreach ((int number, string name, bool _, long calls) in BuiltinCalls.Where(b => !b.Forwarded)) s.AppendLine($"  {calls,9}  #{number,-4} {name}");
        s.AppendLine("presentation calls by interface member:");
        foreach ((string member, long calls) in PresentationCalls) s.AppendLine($"  {calls,9}  {member}");
        s.AppendLine();
        s.AppendLine($"VM warnings: {Warnings} ({WarningCounts.Count} distinct); most frequent:");
        foreach ((string warning, int count) in WarningCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(15))
            s.AppendLine($"  {count,7}  {warning}");
        s.AppendLine($"console output: {PrintLines} lines; first {PrintSample.Count}:");
        foreach (string line in PrintSample) s.AppendLine("  " + line);
        if (Unexpected is not null) s.AppendLine().AppendLine("UNEXPECTED EXCEPTION (a host bug): " + Unexpected);
        return s.ToString();

        static string Percent(long part, long whole) => whole == 0 ? "n/a" : (100.0 * part / whole).ToString("F1", CultureInfo.InvariantCulture) + "%";
        static string SvcName(int svc) => Enum.IsDefined(typeof(Svc), (byte)svc) ? ((Svc)svc).ToString() : "?";
    }
}

/// <summary>
/// Plays a DarkPlaces demo through the protocol parser and the client program it was recorded with,
/// with no window and no world, and reports how far the program got. A demo is the byte stream a real
/// client received, so every message the program decodes to its end is evidence that the VM and the
/// builtins behave as DarkPlaces' do; a message it cannot is a bug to find, and the report says where.
///
/// The program is taken from the demo itself: DarkPlaces writes the csprogs.dat in use into the head
/// of every recording, because network data can only be decoded by the build that matches it.
/// </summary>
public static class CsqcDemoReplay
{
    /// <param name="demo">The .dem file.</param>
    /// <param name="name">A label for the report.</param>
    /// <param name="services">Host services over a filesystem with the game's data and a cvar store that
    /// already holds what a DarkPlaces client has by the time it connects: the engine's own cvars
    /// (<see cref="CsqcEngineCvars.Register"/>) and then the game's default configuration on top. The
    /// program reads hundreds of both at start-up.</param>
    /// <param name="interpreter">The console interpreter over the same cvar store.</param>
    public static CsqcDemoReplayResult Run(Stream demo, string name, LegacyQcHost services, ConfigInterpreter interpreter,
        ILegacyPresentation presentation, CsqcDemoReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(presentation);
        options ??= new CsqcDemoReplayOptions();
        CsqcDemoReplayResult result = new() { Name = name, UpdateViewEnabled = options.CallUpdateView };
        Stopwatch total = Stopwatch.StartNew();
        try
        {
            new Session(result, services, interpreter, presentation, options).Play(demo);
        }
        catch (Exception e)
        {
            // Nothing in the host may throw on any demo or program. If something does, the replay
            // reports it rather than hiding it behind a test-runner stack trace.
            result.Unexpected = e;
        }
        result.TotalSeconds = total.Elapsed.TotalSeconds;
        return result;
    }

    /// <summary>
    /// Why <see cref="ProgsFile.Load"/> would refuse a file on account of its instruction set: every
    /// opcode outside the classic 0..65 range with the number of statements using it, or null if there
    /// is none (or the file is too damaged to tell).
    /// </summary>
    public static string? DescribeUnsupportedOpcodes(ReadOnlySpan<byte> program)
    {
        if (program.Length < ProgsFile.HeaderSize) return null;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(program[8..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(program[12..]);
        if (offset < 0 || count < 0 || (long)offset + (long)count * 8 > program.Length) return null;
        SortedDictionary<int, int> extended = new();
        for (int i = 0; i < count; i++)
        {
            int op = BinaryPrimitives.ReadUInt16LittleEndian(program[(offset + i * 8)..]);
            if (op > (int)QcOp.BitOrF) extended[op] = extended.GetValueOrDefault(op) + 1;
        }
        if (extended.Count == 0) return null;
        return $"{extended.Values.Sum()} of {count} statements use {extended.Count} opcodes above {(int)QcOp.BitOrF}: "
            + string.Join(", ", extended.Select(kv => $"opcode {kv.Key} x{kv.Value}"));
    }

    private sealed class Session : IDpClientHandler
    {
        private readonly CsqcDemoReplayResult _result;
        private readonly LegacyQcHost _services;
        private readonly ILegacyPresentation _presentation;
        private readonly CsqcDemoReplayOptions _options;
        private readonly CsqcClientState _state = new() { IsDemo = true };
        private readonly CsqcConsole _console;
        private readonly CsqcMessageHandler _handler;
        private readonly DpServerMessageParser _parser;
        private readonly DpDownload _download = new();
        private readonly DpSignon _signon;
        private CsqcHost? _host;
        private bool _programPending;
        private bool _quakeCStopSeen;
        private readonly Stopwatch _frameClock = new();

        public Session(CsqcDemoReplayResult result, LegacyQcHost services, ConfigInterpreter interpreter, ILegacyPresentation presentation, CsqcDemoReplayOptions options)
        {
            _result = result;
            _services = services;
            _presentation = presentation;
            _options = options;
            _signon = new DpSignon(new DpSignonConfig(), _download);
            _console = new CsqcConsole(interpreter, services)
            {
                // The engine's own commands (csqc_progcrc, cl_downloadbegin, ...) before anything else.
                EngineCommand = _signon.HandleCommand,
            };
            _handler = new CsqcMessageHandler(_state, _console, presentation) { Next = this };
            _parser = new DpServerMessageParser(_handler);
        }

        public void Play(Stream stream)
        {
            DpDemoReader demo = new(stream);
            while (_result.Messages < _options.MaxMessages && demo.TryReadMessage(out DpDemoMessage message))
            {
                int index = _result.Messages++;
                _result.Bytes += message.Data.Length;
                // The recorded view angles are the player's (cl.mviewangles in CL_ReadDemoMessage).
                _state.ViewAngles = new QcVector(message.ViewAngles.X, message.ViewAngles.Y, message.ViewAngles.Z);

                // One message is one client frame here, and CL_Frame begins with this.
                _host?.PreventInformationLeaks();
                DpMessageReader reader = new(message.Data);
                _handler.BeginMessage(reader, index);
                DpParseResult parsed = _parser.Parse(reader);
                CsqcDesync? desync = _host?.MessageDesync;
                // Stuffed commands run now, after the message; then the signon's "next frame" step.
                _handler.EndMessage();
                _signon.EndOfMessage();
                _signon.Commands.Clear();

                if (parsed.Status == DpParseStatus.Complete)
                {
                    _result.FullyDecoded++;
                    _result.BytesDecoded += message.Data.Length;
                }
                else RecordStop(new CsqcReplayStop(index, message.FileOffset, message.Data.Length, parsed.Status, parsed.Svc, parsed.Offset, parsed.Message, desync));

                // A new level: DarkPlaces shuts the old program down when the server info arrives and
                // loads the new one from cl_begindownloads, the frame after signon stage 1.
                if (_handler.ServerInfoReceived)
                {
                    Unload();
                    _programPending = true;
                    _result.Level = _state.WorldModel;
                }
                if (_programPending && _state.Signon >= 1)
                {
                    _programPending = false;
                    StartProgram(index);
                }

                if (_options.CallUpdateView && _handler.TimeReceived && _host is { Initialized: true } host && _state.Signon >= DpProtocol.Signons)
                {
                    double frameTime = Math.Max(0, _state.ServerTime - _state.Time);
                    _state.Time = _state.ServerTime;
                    _console.Execute();
                    int faults = host.FaultCount;
                    _frameClock.Start();
                    host.UpdateView(_options.Width, _options.Height, frameTime);
                    _frameClock.Stop();
                    _result.Frames++;
                    if (host.FaultCount != faults) _result.FrameFaults++;
                    _console.Execute();
                }
            }
            _result.ReaderError = demo.Error;
            Unload();
            _result.FrameSeconds = _frameClock.Elapsed.TotalSeconds;
            _result.ProgramName = _signon.CsqcProgName;
            if (_presentation is ILegacyCallCounts counts)
                foreach ((string member, long calls) in counts.Calls)
                    _result.PresentationCalls[member] = calls;
            _result.ConsoleLines = _console.LinesExecuted;
            _result.GameCommands = _console.GameCommands;
            _result.MenuCommands = _console.MenuCommands;
            _result.ForwardedToServer = _console.ForwardedToServer;
            _result.QcCommands = _console.QcCommands.Count;
            if (_options.Log is { } log)
            {
                _result.PrintLines = log.PrintLines;
                _result.Warnings = log.Warnings;
                _result.PrintSample.AddRange(log.PrintSample);
                foreach ((string warning, int count) in log.WarningCounts) _result.WarningCounts[warning] = count;
            }
        }

        private void RecordStop(CsqcReplayStop stop)
        {
            _result.Stopped++;
            _result.FirstStop ??= stop;
            if (_result.Stops.Count < 32) _result.Stops.Add(stop);
            _result.StopsBySvc[stop.Svc] = _result.StopsBySvc.GetValueOrDefault(stop.Svc) + 1;
            string place = stop.Desync is { } d ? $"{d.Context}: {d.Reason} in {d.Where}" : $"{stop.Status}: {stop.Reason}";
            _result.StopsByPlace[place] = _result.StopsByPlace.GetValueOrDefault(place) + 1;
            if (stop.InQuakeC)
            {
                _result.StoppedInQuakeC++;
                _quakeCStopSeen = true;
            }
            else
            {
                _result.ProtocolErrors++;
                if (!_quakeCStopSeen) _result.ProtocolErrorsBeforeFirstQuakeCStop++;
            }
        }

        private void StartProgram(int messageIndex)
        {
            _result.ProgramSizeExpected = _signon.CsqcProgSize;
            _result.ProgramCrcExpected = _signon.CsqcProgCrc;
            byte[]? program = _signon.CsprogsData;
            if (program is null)
            {
                _result.ProgramRefusal ??= "the demo carries no client program (no cl_downloadbegin / svc_downloaddata / cl_downloadfinished sequence completed)";
                return;
            }
            _result.ProgramFound = true;
            _result.ProgramSize = program.Length;
            _result.ProgramCrc = Crc16.Block(program);

            CsqcHost host;
            try
            {
                host = new CsqcHost(program, _signon.CsqcProgSize, _signon.CsqcProgCrc, _services, _console, _presentation, _state,
                    new CsqcHostOptions { KeepRunningAfterFault = _options.KeepRunningAfterFault });
            }
            catch (CsqcLoadException e)
            {
                string? opcodes = DescribeUnsupportedOpcodes(program);
                _result.ProgramRefusal = e.Message + (opcodes is null ? "" : " [" + opcodes + "]");
                return;
            }

            _result.ProgramLoaded = true;
            _result.ProgramStartMessage = messageIndex;
            _result.Statements = host.Program.Statements.Length;
            _result.Functions = host.Program.Functions.Length;
            _result.Globals = host.Program.NumGlobals;
            _result.FieldCells = host.Vm.EntityFields;
            _result.AutocvarsBound = host.AutocvarsBound;
            _result.EffectNames = host.Effects.Names.Count - 1;
            _options.HostCreated?.Invoke(host);

            Stopwatch init = Stopwatch.StartNew();
            _result.InitRan = true;
            _result.InitCompleted = host.Init();
            _result.InitSeconds = init.Elapsed.TotalSeconds;
            _host = host;
            _handler.Host = host;
            _console.Execute();
        }

        // Folds a finished host's counters into the result, then shuts it down.
        private void Unload()
        {
            if (_host is not { } host) return;
            _result.EntityUpdates += host.EntityUpdates;
            _result.EntityRemoves += host.EntityRemoves;
            _result.TempEntitiesConsumed += host.TempEntitiesConsumed;
            _result.TempEntitiesDeclined += host.TempEntitiesDeclined;
            _result.EngineTempEntities = _handler.EngineTempEntities;
            _result.Faults += host.FaultCount;
            _result.Desyncs += host.DesyncCount;
            _result.ReadsOutsideMessage += host.ReadsOutsideMessage;
            _result.Edicts = host.Vm.NumEdicts;
            foreach (CsqcFault fault in host.Faults)
            {
                if (_result.FaultSamples.Count < 16) _result.FaultSamples.Add(fault);
                string[] lines = fault.Message.Split('\n');
                string place = fault.EntryPoint + ": " + lines[0] + (lines.Length > 1 ? " in " + lines[1].Trim() : "");
                _result.FaultsByPlace[place] = _result.FaultsByPlace.GetValueOrDefault(place) + 1;
            }
            foreach (((int number, string builtinName), long calls) in host.UnimplementedBuiltins.OrderByDescending(u => u.Value).ThenBy(u => u.Key.Number))
                _result.UnimplementedBuiltins.Add((number, builtinName, calls));
            foreach ((int number, string builtinName, bool forwarded, long calls) in host.Builtins.CallCounts.OrderByDescending(c => c.Calls).ThenBy(c => c.Number))
                _result.BuiltinCalls.Add((number, builtinName, forwarded, calls));
            _handler.Host = null;
            host.Shutdown();
            _host = null;
        }

        // ---- the parts of the connection the program does not see ------------------------------------

        public void OnServerInfo(DpServerInfo info) => _signon.OnServerInfo();

        public void OnSignonNum(int stage)
        {
            _signon.OnSignonNum(stage);
            _result.SignonStages.Add(stage.ToString(CultureInfo.InvariantCulture));
        }

        public void OnEntityFrame(in DpEntityFrame frame, DpEntityTable entities) => _signon.OnEntityFrame();
        public void OnDownloadData(int start, ReadOnlySpan<byte> data) => _download.OnData(start, data);
    }
}

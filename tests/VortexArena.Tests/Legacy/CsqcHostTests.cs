using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using VortexArena.Tests.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The client-program host against small programs assembled in the test: what DarkPlaces' csprogs.c
/// does around the VM (entry points, the entity map, the message hand-off, faults) and the builtins of
/// clvm_cmds.c that need no renderer. The real program and real network data are CsqcDemoReplayTests.
/// </summary>
public class CsqcHostTests
{
    /// <summary>A global or constant passed as a builtin argument: one cell, or three for a vector.</summary>
    private readonly record struct Arg(int Offset, bool IsVector)
    {
        public static implicit operator Arg(int offset) => new(offset, false);
    }

    /// <summary>A few lines of assembler over <see cref="ProgsBuilder"/>: named globals, builtin calls, functions.</summary>
    private sealed class Asm
    {
        public readonly ProgsBuilder B = new();
        private readonly Dictionary<int, int> _builtins = new();
        public readonly int Self, Time;

        public Asm(bool updateView = true)
        {
            Self = B.Int(0, "self", QcType.Entity);
            Time = B.Float(0, "time");
            if (updateView) { Begin("CSQC_UpdateView"); End(); }
        }

        public int F(string name, float value = 0) => B.Float(value, name);
        public int V(string name, float x = 0, float y = 0, float z = 0) => B.Vector(x, y, z, name);
        public int S(string name) => B.Int(0, name, QcType.String);
        public int E(string name) => B.Int(0, name, QcType.Entity);
        public int Const(float value) => B.Float(value);
        public int Text(string text) => B.Int(B.String(text), null, QcType.String);
        public static Arg Vec(int offset) => new(offset, true);

        public void Begin(string function) => B.Function(function);
        public void End() => B.Emit(QcOp.Done);
        public void Return(int global) => B.Emit(QcOp.Return, global);
        /// <summary>Copies the n-th parameter cell (as the caller left it) into a global.</summary>
        public void Parm(int index, int destination) => B.Emit(QcOp.StoreF, ProgsFile.OfsParm0 + index * 3, destination);
        public void Copy(int from, int to) => B.Emit(QcOp.StoreF, from, to);

        public void Call(int builtin, params Arg[] args)
        {
            if (!_builtins.TryGetValue(builtin, out int function)) _builtins[builtin] = function = B.Builtin("builtin" + builtin, builtin);
            for (int i = 0; i < args.Length; i++)
                B.Emit(args[i].IsVector ? QcOp.StoreV : QcOp.StoreF, args[i].Offset, ProgsFile.OfsParm0 + i * 3);
            B.EmitRaw((int)QcOp.Call0 + args.Length, function);
        }

        /// <summary>Calls a builtin and stores its (one-cell) result.</summary>
        public void Get(int builtin, int result, params Arg[] args)
        {
            Call(builtin, args);
            B.Emit(QcOp.StoreF, ProgsFile.OfsReturn, result);
        }

        public void GetV(int builtin, int result, params Arg[] args)
        {
            Call(builtin, args);
            B.Emit(QcOp.StoreV, ProgsFile.OfsReturn, result);
        }

        public byte[] Build() => B.Build();
    }

    private sealed class Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "va-csqchost-" + Guid.NewGuid().ToString("N"));
        public CvarService Cvars { get; } = new();
        public VirtualFileSystem Vfs { get; } = new();
        public ConfigInterpreter Interpreter { get; }
        public LegacyQcHost Services { get; }
        public CsqcConsole Console { get; }
        public NullLegacyPresentation Presentation { get; }
        public CsqcClientState State { get; } = new();
        public CsqcMessageHandler Handler { get; }
        public DpServerMessageParser Parser { get; }
        public List<string> Warnings { get; } = new();
        public StringBuilder Printed { get; } = new();
        public List<string> Sent { get; } = new();
        public List<string> CenterPrints { get; } = new();
        public CsqcHost? Host { get; private set; }
        private int _messages;

        public Rig(params (string Path, string Text)[] files)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "placeholder.txt"), "x");
            foreach ((string path, string text) in files)
            {
                string full = Path.Combine(Root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            Assert.True(Vfs.Mount(Root));
            Interpreter = new ConfigInterpreter(Cvars, path => Vfs.Exists(path) ? Vfs.ReadText(path) : null);
            Services = new LegacyQcHost(Cvars, Vfs) { PrintSink = s => Printed.Append(s), WarningSink = Warnings.Add };
            Console = new CsqcConsole(Interpreter, Services) { SendToServer = Sent.Add };
            Presentation = new NullLegacyPresentation { FileExists = Services.FileExists };
            Handler = new CsqcMessageHandler(State, Console, Presentation);
            Parser = new DpServerMessageParser(Handler);
            State.ApplyServerInfo(new DpServerInfo
            {
                Protocol = DpProtocol.ProtocolNumberDp7, MaxClients = 8, GameType = 1, WorldMessage = "Storm Keep",
                Models = new[] { "", "maps/stormkeep.bsp", "*1", "models/items/a_cells.md3" },
                Sounds = new[] { "", "misc/talk.wav" },
            });
            State.SetView(1);
        }

        public CsqcHost Load(Asm asm, CsqcHostOptions? options = null, bool init = true) => Load(asm.Build(), options, init);

        public CsqcHost Load(byte[] program, CsqcHostOptions? options = null, bool init = true)
        {
            options ??= new CsqcHostOptions { CenterPrint = CenterPrints.Add };
            Host = new CsqcHost(program, program.Length, Crc16.Block(program), Services, Console, Presentation, State, options);
            Handler.Host = Host;
            if (init) Assert.True(Host.Init(), Host.FaultMessage);
            return Host;
        }

        /// <summary>One server message through the real parser, with the hand-off to the program around it.</summary>
        public DpParseResult Message(Action<DpMessageWriter> write)
        {
            DpMessageWriter writer = new();
            write(writer);
            DpMessageReader reader = new(writer.ToArray());
            Handler.BeginMessage(reader, _messages++);
            DpParseResult result = Parser.Parse(reader);
            Handler.EndMessage();
            return result;
        }

        public float Global(string name) => Host!.Vm.GlobalFloat(Host.Vm.FindGlobal(name)!.Offset);
        public int GlobalInt(string name) => Host!.Vm.GlobalInt(Host.Vm.FindGlobal(name)!.Offset);
        public QcVector GlobalVector(string name) => Host!.Vm.GlobalVector(Host.Vm.FindGlobal(name)!.Offset);
        public string GlobalString(string name) => Host!.Vm.GetString(GlobalInt(name));

        public void Dispose()
        {
            Host?.Dispose();
            Vfs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static void CsqcEntities(DpMessageWriter w, Action<DpMessageWriter> body)
    {
        w.WriteByte((int)Svc.CsqcEntities);
        body(w);
        w.WriteShort(0);
    }

    // ---- loading -----------------------------------------------------------------------------------

    [Fact]
    public void Load_RefusesAProgramThatIsNotTheOneTheServerNamed()
    {
        using Rig rig = new();
        byte[] program = new Asm().Build();
        int crc = Crc16.Block(program);

        CsqcLoadException wrongCrc = Assert.Throws<CsqcLoadException>(() =>
            new CsqcHost(program, program.Length, crc ^ 1, rig.Services, rig.Console, rig.Presentation, rig.State));
        Assert.Contains($"CRC is {crc}/{program.Length} but should be {crc ^ 1}/{program.Length}", wrongCrc.Message);
        Assert.Throws<CsqcLoadException>(() =>
            new CsqcHost(program, program.Length + 1, crc, rig.Services, rig.Console, rig.Presentation, rig.State));

        // Demo playback only warns about a mismatch.
        using CsqcHost lenient = new(program, 1, 2, rig.Services, rig.Console, rig.Presentation, rig.State, new CsqcHostOptions { AllowMismatch = true });
        Assert.Equal(crc, lenient.ProgramCrc);
    }

    [Fact]
    public void Load_RefusesGarbageAndAProgramWithoutUpdateView_WithTheReason()
    {
        using Rig rig = new();
        byte[] garbage = new byte[200];
        new Random(5).NextBytes(garbage);
        CsqcLoadException bad = Assert.Throws<CsqcLoadException>(() => rig.Load(garbage));
        Assert.Contains("failed to load", bad.Message);

        CsqcLoadException noView = Assert.Throws<CsqcLoadException>(() => rig.Load(new Asm(updateView: false)));
        Assert.Contains("CSQC_UpdateView", noView.Message);
        Assert.Null(rig.Console.Host);
    }

    [Fact]
    public void UnsupportedOpcodes_AreReportedByNumberAndCount()
    {
        ProgsBuilder b = new();
        b.Function("CSQC_UpdateView");
        b.EmitRaw(113); b.EmitRaw(113); b.EmitRaw(200);
        b.Emit(QcOp.Done);
        byte[] program = b.Build();
        Assert.Throws<ProgsFormatException>(() => ProgsFile.Load(program));
        string? description = CsqcDemoReplay.DescribeUnsupportedOpcodes(program);
        Assert.NotNull(description);
        Assert.Contains("opcode 113 x2", description);
        Assert.Contains("opcode 200 x1", description);
        Assert.Null(CsqcDemoReplay.DescribeUnsupportedOpcodes(new Asm().Build()));
        Assert.Null(CsqcDemoReplay.DescribeUnsupportedOpcodes(new byte[10]));
    }

    [Fact]
    public void Init_PassesDarkPlacesArguments_AndSetsUpTheWorld()
    {
        using Rig rig = new();
        Asm a = new();
        int api = a.F("api"), version = a.F("version"), engine = a.S("engine");
        a.S("mapname"); a.F("player_localentnum"); a.F("player_localnum"); a.F("deathmatch"); a.F("coop");
        a.Begin("CSQC_Init");
        a.Parm(0, api); a.Parm(1, engine); a.Parm(2, version);
        a.End();
        CsqcHost host = rig.Load(a);

        Assert.True(host.Initialized);
        Assert.Equal(1f, rig.Global("api"));
        Assert.Equal(1f, rig.Global("version"));
        Assert.Equal("DarkPlaces Xonotic", rig.GlobalString("engine"));
        Assert.Equal("stormkeep", rig.GlobalString("mapname"));
        Assert.Equal(1f, rig.Global("player_localentnum"));
        Assert.Equal(0f, rig.Global("player_localnum"));
        Assert.Equal(1f, rig.Global("deathmatch"));
        Assert.Equal(0f, rig.Global("coop"));

        // csprogs.c:1140-1150: the world entity is the map.
        QcVm vm = host.Vm;
        Assert.Equal("Storm Keep", vm.GetString(vm.FieldInt(0, host.Fields.Message)));
        Assert.Equal("maps/stormkeep.bsp", vm.GetString(vm.FieldInt(0, host.Fields.Model)));
        Assert.Equal(4f, vm.FieldFloat(0, host.Fields.Solid));
        Assert.Equal(1f, vm.FieldFloat(0, host.Fields.ModelIndex));
        // Engine fields the program never declared were appended.
        Assert.NotNull(vm.FindField("entnum"));
        Assert.NotNull(vm.FindField("drawmask"));
    }

    // ---- svc_csqcentities --------------------------------------------------------------------------

    private static Asm EntityProgram(bool withSpawn = false)
    {
        Asm a = new();
        int isNew = a.F("isnew"), b = a.F("gbyte"), c = a.F("gcoord"), who = a.E("gself"), updates = a.F("updates"), one = a.Const(1);
        int removed = a.E("removed"), spawnArg = a.F("spawnarg");
        a.Begin("CSQC_Ent_Update");
        a.Parm(0, isNew);
        a.B.Emit(QcOp.StoreEnt, a.Self, who);
        a.Get(360, b);
        a.Get(364, c);
        a.B.Emit(QcOp.AddF, updates, one, updates);
        a.End();
        a.Begin("CSQC_Ent_Remove");
        a.B.Emit(QcOp.StoreEnt, a.Self, removed);
        a.End();
        if (withSpawn)
        {
            int made = a.E("made");
            a.Begin("CSQC_Ent_Spawn");
            a.Parm(0, spawnArg);
            a.Get(14, made); // spawn()
            a.Return(made);
        }
        return a;
    }

    [Fact]
    public void EntUpdate_ReadsItsOwnPayload_AndMapsServerEntitiesToTheProgramsOwn()
    {
        using Rig rig = new();
        CsqcHost host = rig.Load(EntityProgram());

        DpParseResult first = rig.Message(w => CsqcEntities(w, m =>
        {
            m.WriteShort(300); m.WriteByte(77); m.WriteCoord(12.5f);
            m.WriteShort(301); m.WriteByte(78); m.WriteCoord(-3f);
        }));
        Assert.Equal(DpParseStatus.Complete, first.Status);
        Assert.Equal(2f, rig.Global("updates"));
        Assert.Equal(1f, rig.Global("isnew"));
        Assert.Equal(78f, rig.Global("gbyte"));
        Assert.Equal(-3f, rig.Global("gcoord"));

        int e300 = host.EdictForServerEntity(300), e301 = host.EdictForServerEntity(301);
        Assert.True(e300 > 0 && e301 > 0 && e300 != e301);
        Assert.Equal(e301, rig.GlobalInt("gself"));
        // Without CSQC_Ent_Spawn the engine allocates the entity and sets .entnum.
        Assert.Equal(300f, host.Vm.FieldFloat(e300, host.Fields.EntNum));
        Assert.Equal(0, rig.GlobalInt("self")); // restored afterwards

        // A second update of the same entity is not new, and reuses it.
        Assert.Equal(DpParseStatus.Complete, rig.Message(w => CsqcEntities(w, m => { m.WriteShort(300); m.WriteByte(1); m.WriteCoord(2f); })).Status);
        Assert.Equal(0f, rig.Global("isnew"));
        Assert.Equal(e300, rig.GlobalInt("gself"));
        Assert.Equal(e300, host.EdictForServerEntity(300));

        // Remove: CSQC_Ent_Remove with self set, then the mapping is gone; a repeated remove is silent.
        Assert.Equal(DpParseStatus.Complete, rig.Message(w => CsqcEntities(w, m => { m.WriteShort(300 | 0x8000); m.WriteShort(300 | 0x8000); })).Status);
        Assert.Equal(e300, rig.GlobalInt("removed"));
        Assert.Equal(0, host.EdictForServerEntity(300));
        Assert.Equal(1, host.EntityRemoves);
        Assert.Equal(3, host.EntityUpdates);
        Assert.False(host.Faulted);
    }

    [Fact]
    public void EntUpdate_UsesTheProgramsSpawnFunctionWhenItHasOne()
    {
        using Rig rig = new();
        CsqcHost host = rig.Load(EntityProgram(withSpawn: true));
        Assert.Equal(DpParseStatus.Complete, rig.Message(w => CsqcEntities(w, m => { m.WriteShort(42); m.WriteByte(9); m.WriteCoord(1f); })).Status);
        Assert.Equal(42f, rig.Global("spawnarg"));
        int edict = rig.GlobalInt("made");
        Assert.Equal(edict, host.EdictForServerEntity(42));
        Assert.Equal(edict, rig.GlobalInt("gself"));
        Assert.Equal(1f, rig.Global("isnew"));
        // The spawn function, not the engine, is responsible for .entnum.
        Assert.Equal(0f, host.Vm.FieldFloat(edict, host.Fields.EntNum));
    }

    [Fact]
    public void ReadPastTheEnd_IsReportedAsADesync_WithEntityFunctionAndOffset()
    {
        using Rig rig = new();
        CsqcHost host = rig.Load(EntityProgram());

        // The update should be a byte and a 4-byte coord; the message has the byte and two more.
        DpParseResult result = rig.Message(w =>
        {
            w.WriteByte((int)Svc.CsqcEntities);
            w.WriteShort(300); w.WriteByte(77); w.WriteByte(1); w.WriteByte(2);
        });
        Assert.Equal(DpParseStatus.Error, result.Status);
        Assert.Equal((int)Svc.CsqcEntities, result.Svc);

        CsqcDesync desync = Assert.Single(host.Desyncs);
        Assert.Equal(1, host.DesyncCount);
        Assert.Equal(0, desync.MessageIndex);
        Assert.Equal("CSQC_Ent_Update", desync.Context);
        Assert.Equal(300, desync.ServerEntity);
        Assert.Equal(3, desync.PayloadStart);
        Assert.Equal(6, desync.MessageLength);
        Assert.Contains("ReadCoord read past the end", desync.Reason);
        Assert.Contains("CSQC_Ent_Update : statement 4", desync.Where);
        Assert.Contains("message 0", desync.ToString());
        // A short read is not a fault: the program ran to its end with -1 for the missing value.
        Assert.False(host.Faulted);
        Assert.Equal(-1f, rig.Global("gcoord"));

        // The next, well-formed message decodes and carries no desync.
        Assert.Equal(DpParseStatus.Complete, rig.Message(w => CsqcEntities(w, m => { m.WriteShort(300); m.WriteByte(1); m.WriteCoord(2f); })).Status);
        Assert.Null(host.MessageDesync);
    }

    [Fact]
    public void AFaultInEntUpdate_AbortsTheMessage_IsRecorded_AndDisablesTheProgram()
    {
        Asm Program()
        {
            Asm a = new();
            int frames = a.F("frames"), one = a.Const(1);
            a.Begin("CSQC_UpdateView2");
            a.End();
            a.Begin("CSQC_Ent_Update");
            a.Call(360);
            a.Call(10, a.Text("boom")); // error()
            a.End();
            a.Begin("CSQC_Parse_Print");
            a.B.Emit(QcOp.AddF, frames, one, frames);
            a.End();
            return a;
        }

        using Rig rig = new();
        CsqcHost host = rig.Load(Program());
        DpParseResult result = rig.Message(w => CsqcEntities(w, m => { m.WriteShort(5); m.WriteByte(1); }));
        Assert.Equal(DpParseStatus.Aborted, result.Status);
        Assert.True(host.Faulted);
        CsqcFault fault = Assert.Single(host.Faults);
        Assert.Equal("CSQC_Ent_Update", fault.EntryPoint);
        Assert.Contains("boom", fault.Message);
        Assert.Contains("Program error in function CSQC_Ent_Update", fault.Message);
        Assert.Contains(" : CSQC_Ent_Update : statement 2", fault.Message); // the QuakeC stack
        CsqcDesync desync = Assert.Single(host.Desyncs);
        Assert.StartsWith("fault:", desync.Reason);
        Assert.Equal(5, desync.ServerEntity);

        // "after a fault no further QuakeC runs": the print goes to the console instead, and a temp
        // entity - which only the program could have claimed or declined - stops its message.
        Assert.True(host.Disabled);
        Assert.Equal(DpParseStatus.Aborted, rig.Message(w => { w.WriteByte((int)Svc.TempEntity); w.WriteByte(80); }).Status);
        host.ParsePrint("hello\n");
        Assert.Equal(0f, rig.Global("frames"));
        Assert.Contains("hello", rig.Printed.ToString());
        Assert.False(host.UpdateView(640, 480));
        Assert.Equal(1, host.FaultCount);

        // A diagnostic run may keep going.
        using Rig lenient = new();
        CsqcHost second = lenient.Load(Program(), new CsqcHostOptions { KeepRunningAfterFault = true });
        lenient.Message(w => CsqcEntities(w, m => { m.WriteShort(5); m.WriteByte(1); }));
        second.ParsePrint("hello\n");
        Assert.Equal(1f, lenient.Global("frames"));
        Assert.True(second.UpdateView(640, 480));
    }

    [Fact]
    public void AProgramWithoutEntUpdate_CannotConsumeEntities_AndSaysSo()
    {
        using Rig rig = new();
        CsqcHost host = rig.Load(new Asm());
        Assert.Equal(DpParseStatus.Aborted, rig.Message(w => CsqcEntities(w, m => { m.WriteShort(5); m.WriteByte(1); })).Status);
        Assert.Contains("CSQC_Ent_Update is missing", host.FaultMessage);
    }

    // ---- svc_temp_entity ---------------------------------------------------------------------------

    private static Asm TempEntityProgram()
    {
        Asm a = new();
        int type = a.F("tetype"), value = a.F("tevalue"), is80 = a.F("is80"), c80 = a.Const(80), one = a.Const(1), zero = a.Const(0);
        a.Begin("CSQC_Parse_TempEntity");
        a.Get(360, type);
        a.B.Emit(QcOp.EqF, type, c80, is80);
        int jump = a.B.Emit(QcOp.IfNot, is80);
        a.Get(362, value);
        a.Return(one);
        a.B.PatchJump(jump, a.B.NextStatement);
        // Reads further before declining: the engine must still see the message from its first byte.
        a.Call(363);
        a.Call(363);
        a.Call(363);
        a.Call(363);
        a.Return(zero);
        return a;
    }

    [Fact]
    public void TempEntity_Declined_LeavesTheReaderWhereItWas_ForTheEngineDecoder()
    {
        using Rig rig = new();
        CsqcHost host = rig.Load(TempEntityProgram());

        // TE_GUNSHOT (2) and a position: 13 payload bytes. The program reads 1 + 16 (running past the
        // end of the message) and returns false.
        DpParseResult result = rig.Message(w =>
        {
            w.WriteByte((int)Svc.TempEntity);
            w.WriteByte((int)TempEntityType.Gunshot);
            w.WriteVector(new Vector3(1, 2, 3));
            w.WriteByte((int)Svc.Nop);
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(2, result.Commands);
        Assert.Equal(1, host.TempEntitiesDeclined);
        Assert.Equal(0, host.TempEntitiesConsumed);
        Assert.Equal(1, rig.Handler.EngineTempEntities);
        Assert.Equal(1, rig.Presentation.Calls["TempEntity"]);
        // The reads past the end were undone with the rewind: not a desync.
        Assert.Empty(host.Desyncs);
        Assert.Null(host.MessageDesync);
    }

    [Fact]
    public void TempEntity_Accepted_IsConsumedByTheProgram()
    {
        using Rig rig = new();
        CsqcHost host = rig.Load(TempEntityProgram());
        DpParseResult result = rig.Message(w =>
        {
            w.WriteByte((int)Svc.TempEntity);
            w.WriteByte(80);
            w.WriteShort(-1234);
            w.WriteByte((int)Svc.Nop);
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(2, result.Commands);
        Assert.Equal(-1234f, rig.Global("tevalue"));
        Assert.Equal(1, host.TempEntitiesConsumed);
        Assert.Equal(0, rig.Handler.EngineTempEntities);

        // Accepted but short: the message ends inside the payload, and the report says who was reading.
        DpParseResult truncated = rig.Message(w => { w.WriteByte((int)Svc.TempEntity); w.WriteByte(80); w.WriteByte(1); });
        Assert.Equal(DpParseStatus.Error, truncated.Status);
        Assert.Equal("CSQC_Parse_TempEntity", host.MessageDesync!.Context);
        Assert.Contains("ReadShort", host.MessageDesync.Reason);
        Assert.Equal(-1, host.MessageDesync.ServerEntity);
    }

    [Fact]
    public void ReadsOutsideAMessage_FailAsInDarkPlaces()
    {
        using Rig rig = new();
        Asm a = new();
        int b = a.F("b", 5), s = a.S("s"), f = a.F("f", 5);
        a.Begin("CSQC_Init");
        a.Get(360, b);
        a.Get(366, s);
        a.Get(367, f);
        a.End();
        CsqcHost host = rig.Load(a);
        Assert.Equal(-1f, rig.Global("b"));
        Assert.Equal(-1f, rig.Global("f"));
        Assert.Equal("", rig.GlobalString("s"));
        Assert.Equal(3, host.ReadsOutsideMessage);
        Assert.Empty(host.Desyncs);
    }

    [Fact]
    public void ReadBuiltins_DecodeEveryWireType()
    {
        using Rig rig = new();
        Asm a = new();
        int b = a.F("b"), c = a.F("c"), sh = a.F("sh"), l = a.F("l"), co = a.F("co"), an = a.F("an"), f = a.F("f"), s = a.S("s"), pic = a.S("pic"), after = a.F("after");
        a.Begin("CSQC_Parse_TempEntity");
        a.Get(360, b); a.Get(361, c); a.Get(362, sh); a.Get(363, l); a.Get(364, co); a.Get(365, an); a.Get(367, f);
        a.Get(366, s);
        a.Get(501, pic);
        a.Get(360, after);
        a.Return(a.Const(1));
        CsqcHost host = rig.Load(a);

        DpParseResult result = rig.Message(w =>
        {
            w.WriteByte((int)Svc.TempEntity);
            w.WriteByte(200); w.WriteChar(-5); w.WriteShort(-300); w.WriteLong(100000); w.WriteCoord(1.25f);
            w.WriteShort(16384); // an angle: 90 degrees in 1/65536 turns
            w.WriteFloat(-0.5f);
            w.WriteString("héllo");
            // ReadPicture: name, 16-bit length, that many bytes of JPEG.
            w.WriteString("gfx/preview"); w.WriteShort(5); w.WriteBytes(new byte[] { 1, 2, 3, 4, 5 });
            w.WriteByte(99);
        });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(200f, rig.Global("b"));
        Assert.Equal(-5f, rig.Global("c"));
        Assert.Equal(-300f, rig.Global("sh"));
        Assert.Equal(100000f, rig.Global("l"));
        Assert.Equal(1.25f, rig.Global("co"));
        Assert.Equal(90f, rig.Global("an"));
        Assert.Equal(-0.5f, rig.Global("f"));
        Assert.Equal("héllo", rig.GlobalString("s"));
        Assert.Equal("gfx/preview", rig.GlobalString("pic"));
        // The picture's bytes were consumed exactly: the next read is the byte after them.
        Assert.Equal(99f, rig.Global("after"));
        // No such picture on disk, so the attached one was handed over.
        Assert.Equal(1, rig.Presentation.Calls["DefinePicture"]);
        Assert.False(host.Faulted);
    }

    // ---- text and the console ----------------------------------------------------------------------

    [Fact]
    public void StuffText_GoesThroughTheProgram_WhichQueuesItForTheConsole()
    {
        using Rig rig = new();
        Asm a = new();
        int seen = a.S("seen"), stuffed = a.F("stuffed"), one = a.Const(1);
        a.Begin("CSQC_Parse_StuffCmd");
        a.Parm(0, seen);
        a.Get(118, seen, seen);  // strzone, so the text outlives the call
        a.B.Emit(QcOp.AddF, stuffed, one, stuffed);
        a.Call(46, seen);         // localcmd
        a.End();
        CsqcHost host = rig.Load(a);

        bool ranDuringParse = true;
        rig.Interpreter.RegisterCommand("probe", argv => ranDuringParse = rig.Handler.Host!.InMessage);
        DpParseResult result = rig.Message(w => { w.WriteByte((int)Svc.StuffText); w.WriteString("set stuffed_cvar 7; probe\n"); });
        Assert.Equal(DpParseStatus.Complete, result.Status);
        Assert.Equal(1f, rig.Global("stuffed"));
        Assert.Equal("set stuffed_cvar 7; probe\n", rig.GlobalString("seen"));
        // The commands ran after the message, not from inside the parser.
        Assert.Equal("7", rig.Cvars.GetString("stuffed_cvar"));
        Assert.False(ranDuringParse);

        // "csqc" text is the engine's: executed at once and never shown to the program.
        rig.Message(w => { w.WriteByte((int)Svc.StuffText); w.WriteString("csqc_progcrc 123\n"); });
        Assert.Equal(1f, rig.Global("stuffed"));
        Assert.Equal("123", rig.Cvars.GetString("csqc_progcrc"));
        Assert.False(host.Faulted);
    }

    [Fact]
    public void StuffText_WithoutAProgramOrWithoutTheFunction_IsExecuted()
    {
        using Rig rig = new();
        rig.Message(w => { w.WriteByte((int)Svc.StuffText); w.WriteString("set early 1\n"); });
        Assert.Equal("1", rig.Cvars.GetString("early"));

        rig.Load(new Asm());
        // Split across two messages: the half line waits for its end.
        rig.Message(w => { w.WriteByte((int)Svc.StuffText); w.WriteString("set late "); });
        Assert.False(rig.Cvars.Has("late"));
        rig.Message(w => { w.WriteByte((int)Svc.StuffText); w.WriteString("2\n"); });
        Assert.Equal("2", rig.Cvars.GetString("late"));
    }

    [Fact]
    public void RegisterCommand_RoutesTheTypedLineToConsoleCommand_AndClCmdToGameCommand()
    {
        using Rig rig = new();
        Asm a = new();
        int typed = a.S("typed"), game = a.S("game"), calls = a.F("calls"), one = a.Const(1), self = a.E("cmdself");
        a.Begin("CSQC_Init");
        a.Call(352, a.Text("hello"));
        a.End();
        a.Begin("CSQC_ConsoleCommand");
        a.Parm(0, typed);
        a.Get(118, typed, typed);
        a.B.Emit(QcOp.AddF, calls, one, calls);
        a.B.Emit(QcOp.StoreEnt, a.Self, self);
        a.Return(one);
        a.Begin("GameCommand");
        a.Parm(0, game);
        a.Get(118, game, game);
        a.End();
        CsqcHost host = rig.Load(a);

        Assert.Contains("hello", rig.Console.QcCommands);
        rig.Console.AddText("hello world \"two words\" 3\n");
        Assert.Equal(0f, rig.Global("calls")); // queued, not run
        rig.Console.Execute();
        Assert.Equal(1f, rig.Global("calls"));
        Assert.Equal("hello world \"two words\" 3", rig.GlobalString("typed"));

        rig.Console.AddText("cl_cmd hud scoreboard_columns_set default; cmd say hi there\nmenu_cmd sync\n");
        rig.Console.Execute();
        Assert.Equal("hud scoreboard_columns_set default", rig.GlobalString("game"));
        Assert.Equal(new[] { "say hi there" }, rig.Sent);
        Assert.Equal(1, rig.Console.MenuCommands);

        // Entry points are called as the local player's entity when the program has one for it.
        Assert.Equal(0, rig.GlobalInt("cmdself"));
        Assert.True(host.ConsoleCommand("direct"));
        Assert.Equal("direct", rig.GlobalString("typed"));
        Assert.False(host.Faulted);
        Assert.Equal(0, host.Vm.TempStringMark); // the text argument did not leak a temp string
    }

    [Fact]
    public void Print_IsLineBuffered_AndCenterPrintFallsBackToTheEngine()
    {
        using Rig rig = new();
        Asm a = new();
        int line = a.S("line"), lines = a.F("lines"), one = a.Const(1);
        a.Begin("CSQC_Parse_Print");
        a.Parm(0, line);
        a.Get(118, line, line);
        a.B.Emit(QcOp.AddF, lines, one, lines);
        a.End();
        a.Begin("CSQC_Init");
        a.Call(338, a.Text("from "), a.Text("builtin")); // centerprint, varargs
        a.End();
        rig.Load(a);
        Assert.Equal(new[] { "from builtin" }, rig.CenterPrints);

        rig.Message(w => { w.WriteByte((int)Svc.Print); w.WriteString("half a "); });
        Assert.Equal(0f, rig.Global("lines"));
        rig.Message(w => { w.WriteByte((int)Svc.Print); w.WriteString("line\n"); });
        Assert.Equal(1f, rig.Global("lines"));
        Assert.Equal("half a line\n", rig.GlobalString("line"));

        // No CSQC_Parse_CenterPrint in this program: the engine shows it.
        rig.Message(w => { w.WriteByte((int)Svc.CenterPrint); w.WriteString("round begins"); });
        Assert.Equal("round begins", rig.CenterPrints[^1]);
    }

    [Fact]
    public void Autocvars_FollowTheCvar_HoweverItChanges()
    {
        using Rig rig = new();
        Asm a = new();
        a.F("autocvar_hud_scale", 2f);
        rig.Load(a);
        Assert.Equal("2", rig.Cvars.GetString("hud_scale")); // created from the compiled-in default
        rig.Console.AddText("set hud_scale 3.5\n");
        rig.Console.Execute();
        Assert.Equal(3.5f, rig.Global("autocvar_hud_scale"));
    }

    // ---- stats and players -------------------------------------------------------------------------

    [Fact]
    public void Stats_AsFloatIntegerBitFieldAndString()
    {
        using Rig rig = new();
        Asm a = new();
        int f = a.F("statf"), whole = a.F("stati"), bits = a.F("bits"), bit = a.F("bit"), s = a.S("stats"), bad = a.F("bad", 9), badS = a.S("bads");
        a.Begin("CSQC_Init");
        a.Get(330, f, a.Const(40));
        a.Get(331, whole, a.Const(41));
        a.Get(331, bits, a.Const(41), a.Const(4), a.Const(8));  // 8 bits starting at bit 4
        a.Get(331, bit, a.Const(41), a.Const(31));              // one bit
        a.Get(332, s, a.Const(50));
        a.Get(330, bad, a.Const(256));
        a.Get(332, badS, a.Const(253));
        a.End();

        rig.Message(w =>
        {
            w.WriteByte((int)Svc.UpdateStat); w.WriteByte(40); w.WriteLong(BitConverter.SingleToInt32Bits(1.5f));
            w.WriteByte((int)Svc.UpdateStat); w.WriteByte(41); w.WriteLong(unchecked((int)0x80000ABC));
            w.WriteByte((int)Svc.UpdateStat); w.WriteByte(50); w.WriteLong('a' | ('b' << 8) | ('c' << 16) | ('d' << 24));
            w.WriteByte((int)Svc.UpdateStat); w.WriteByte(51); w.WriteLong('e');
            w.WriteByte((int)Svc.UpdateStatUByte); w.WriteByte(60); w.WriteByte(200);
        });
        Assert.Equal(200, rig.State.Stats[60]);
        rig.Load(a);

        Assert.Equal(1.5f, rig.Global("statf"));
        Assert.Equal((float)unchecked((int)0x80000ABC), rig.Global("stati"));
        Assert.Equal((float)0xAB, rig.Global("bits"));
        Assert.Equal(1f, rig.Global("bit"));
        Assert.Equal("abcde", rig.GlobalString("stats"));
        // Out of range: 0 (or the null string) and a warning, never an out-of-bounds read.
        Assert.Equal(0f, rig.Global("bad"));
        Assert.Equal(0, rig.GlobalInt("bads"));
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_getstatf"));
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_getstats"));
    }

    [Fact]
    public void PlayerKeys_AllOfThem_AndRankedLookup()
    {
        using Rig rig = new();
        rig.Message(w =>
        {
            void Player(int slot, string name, int frags, int colors)
            {
                w.WriteByte((int)Svc.UpdateName); w.WriteByte(slot); w.WriteString(name);
                w.WriteByte((int)Svc.UpdateFrags); w.WriteByte(slot); w.WriteShort(frags);
                w.WriteByte((int)Svc.UpdateColors); w.WriteByte(slot); w.WriteByte(colors);
            }
            // The parser checks slots against maxclients, which it learns from svc_serverinfo.
            w.WriteByte((int)Svc.ServerInfo); w.WriteLong(DpProtocol.ProtocolNumberDp7); w.WriteByte(8); w.WriteByte(1);
            w.WriteString("Storm Keep"); w.WriteString("maps/stormkeep.bsp"); w.WriteByte(0); w.WriteString("misc/talk.wav"); w.WriteByte(0);
            Player(0, "Alice", 3, 0x4C);
            Player(2, "Bob", 10, 0x12);
            Player(5, "Carol", 10, 0);
        });

        string[] keys = { "name", "frags", "colors", "topcolor", "bottomcolor", "viewentity", "ping", "pl", "movementloss", "entertime", "NAME", "nosuchkey" };
        Asm a = new();
        int[] results = keys.Select((_, i) => a.S("key" + i)).ToArray();
        int leader = a.S("leader"), second = a.S("second"), last = a.S("last"), nobody = a.S("nobody"), empty = a.S("empty"), outOfRange = a.S("oor");
        a.Begin("CSQC_Init");
        for (int i = 0; i < keys.Length; i++) a.Get(348, results[i], a.Const(0), a.Text(keys[i]));
        a.Get(348, leader, a.Const(-1), a.Text("name"));
        a.Get(348, second, a.Const(-2), a.Text("name"));
        a.Get(348, last, a.Const(-3), a.Text("name"));
        a.Get(348, nobody, a.Const(-4), a.Text("name"));
        a.Get(348, empty, a.Const(1), a.Text("name"));
        a.Get(348, outOfRange, a.Const(200), a.Text("name"));
        a.End();
        rig.Load(a);

        string Key(int i) => rig.GlobalString("key" + i);
        Assert.Equal("Alice", Key(0));
        Assert.Equal("3", Key(1));
        Assert.Equal("76", Key(2));
        Assert.Equal("64", Key(3));    // colors & 0xf0
        Assert.Equal("192", Key(4));   // (colors & 15) << 4
        Assert.Equal("1", Key(5));
        Assert.Equal("0", Key(6));
        Assert.Equal("0", Key(7));
        Assert.Equal("0", Key(8));
        Assert.Equal("0.000000", Key(9));
        Assert.Equal("Alice", Key(10)); // keys are matched without regard to case
        Assert.Equal(0, rig.GlobalInt("key11"));
        // Equal scores keep slot order, as the engine's bubble sort leaves them.
        Assert.Equal("Bob", rig.GlobalString("leader"));
        Assert.Equal("Carol", rig.GlobalString("second"));
        Assert.Equal("Alice", rig.GlobalString("last"));
        Assert.Equal(0, rig.GlobalInt("nobody"));
        Assert.Equal(0, rig.GlobalInt("empty"));  // an empty value is the null string
        Assert.Equal(0, rig.GlobalInt("oor"));
    }

    [Fact]
    public void ServerKey_IsDemo_IsServer()
    {
        Assert.Equal("dm", CsqcBuiltins.InfoValue("\\maxclients\\8\\mode\\dm\\x\\", "mode"));
        Assert.Equal("", CsqcBuiltins.InfoValue("\\maxclients\\8", "mode"));
        Assert.Equal("", CsqcBuiltins.InfoValue("", "mode"));

        using Rig rig = new();
        rig.State.IsDemo = true;
        rig.State.ServerInfoString = "\\hostname\\test server";
        Asm a = new();
        int demo = a.F("demo"), server = a.F("server", 5), key = a.S("key");
        a.Begin("CSQC_Init");
        a.Get(349, demo);
        a.Get(350, server);
        a.Get(354, key, a.Text("hostname"));
        a.End();
        rig.Load(a);
        Assert.Equal(1f, rig.Global("demo"));
        Assert.Equal(0f, rig.Global("server"));
        Assert.Equal("test server", rig.GlobalString("key"));
    }

    // ---- effects -----------------------------------------------------------------------------------

    [Fact]
    public void EffectNumbers_FollowDarkPlaces_BuiltInNamesThenFileOrder()
    {
        const string main = """
            // comment line
            effect TE_GUNSHOT      // a built-in name: keeps number 1
            count 10
            effect laser_impact
            countabsolute 1
            type spark
            effect "quoted name"
            /* a block comment
               over two lines */
            effect rocket_explode
            effect laser_impact
            effect smoke_ring
            color 0x101010 0x202020
            """;
        const string perMap = "effect map_fog\ncount 1\neffect laser_impact\neffect broken extra words\neffect never_reached\n";
        Dictionary<string, string> files = new() { ["effectinfo.txt"] = main, ["maps/stormkeep_effectinfo.txt"] = perMap };
        CsqcEffectInfo info = CsqcEffectInfo.Load(name => files.TryGetValue(name, out string? text) ? Encoding.UTF8.GetBytes(text) : null, "maps/stormkeep");

        Assert.Equal(1, info.IndexForName("TE_GUNSHOT"));
        Assert.Equal(17, info.IndexForName("TE_BLOOD"));
        Assert.Equal(CsqcEffectInfo.SvcParticle, info.IndexForName("SVC_PARTICLE"));
        Assert.Equal(36, info.IndexForName("laser_impact"));
        Assert.Equal(37, info.IndexForName("quoted name"));
        Assert.Equal(38, info.IndexForName("rocket_explode"));
        Assert.Equal(39, info.IndexForName("smoke_ring"));
        Assert.Equal(40, info.IndexForName("map_fog"));
        // A line with the wrong number of words ends the file, as DarkPlaces' parser does.
        Assert.Equal(0, info.IndexForName("never_reached"));
        Assert.Equal(0, info.IndexForName("broken"));
        Assert.Equal(0, info.IndexForName(""));
        Assert.Equal(41, info.Names.Count);
        Assert.Equal("smoke_ring", info.NameForIndex(39));
        Assert.Null(info.NameForIndex(0));
        Assert.Null(info.NameForIndex(999));
        Assert.Contains(info.Warnings, w => w.Contains("effect given 4 parameters, should be 2"));

        // A custom file replaces both.
        CsqcEffectInfo custom = CsqcEffectInfo.Load(name => name == "other.txt" ? Encoding.UTF8.GetBytes("effect only\n") : null, "maps/stormkeep", "other.txt");
        Assert.Equal(36, custom.IndexForName("only"));
    }

    [Fact]
    public void EffectInfo_StopsAtACommandBeforeAnyEffect_AndSurvivesGarbage()
    {
        CsqcEffectInfo info = new();
        info.Parse("count 5\neffect unreachable\n", "x.txt");
        Assert.Equal(0, info.IndexForName("unreachable"));
        Assert.Contains(info.Warnings, w => w.Contains("encountered before effect"));

        byte[] noise = new byte[4096];
        new Random(11).NextBytes(noise);
        new CsqcEffectInfo().Parse(noise, "noise");
        new CsqcEffectInfo().Parse("effect \"unterminated", "x");
        new CsqcEffectInfo().Parse("/* never closed", "x");
    }

    [Fact]
    public void ParticleEffectNum_ReadsTheGameFiles_AndAnswersMinusOneForUnknown()
    {
        using Rig rig = new(("effectinfo.txt", "effect spark_small\ncount 1\n"), ("maps/stormkeep_effectinfo.txt", "effect local_dust\n"));
        Asm a = new();
        int known = a.F("known"), map = a.F("map"), standard = a.F("standard"), unknown = a.F("unknown");
        a.Begin("CSQC_Init");
        a.Get(335, known, a.Text("spark_small"));
        a.Get(335, map, a.Text("local_dust"));
        a.Get(335, standard, a.Text("TR_ROCKET"));
        a.Get(335, unknown, a.Text("no_such_effect"));
        a.End();
        rig.Load(a);
        Assert.Equal(36f, rig.Global("known"));
        Assert.Equal(37f, rig.Global("map"));
        Assert.Equal(25f, rig.Global("standard"));
        Assert.Equal(-1f, rig.Global("unknown"));
    }

    // ---- models and entity placement ---------------------------------------------------------------

    [Fact]
    public void Models_PrecacheIndices_SetModelSetSizeSetOrigin()
    {
        using Rig rig = new(("models/own.md3", "x"));
        Asm a = new();
        int own = a.F("own"), again = a.F("again"), missing = a.F("missing", 7), nullModel = a.F("nullmodel"), e1 = a.E("e1"), e2 = a.E("e2"), e3 = a.E("e3");
        int name = a.S("name"), badName = a.S("badname"), pic = a.S("pic"), snd = a.S("snd");
        int origin = a.V("origin", 10, 20, 30), mins = a.V("mins", -1, -2, -3), maxs = a.V("maxs", 4, 5, 6);
        a.Begin("CSQC_Init");
        a.Get(20, own, a.Text("models/own.md3"));
        a.Get(20, again, a.Text("models/own.md3"));
        a.Get(20, missing, a.Text("models/missing.md3"));
        a.Get(20, nullModel, a.Text("null"));
        a.Get(14, e1); a.Get(14, e2); a.Get(14, e3);
        a.Call(3, e1, a.Text("models/own.md3"));                 // setmodel: the program's precache
        a.Call(3, e2, a.Text("models/items/a_cells.md3"));       // setmodel: the server's
        a.Call(3, e3, a.Text("models/never_precached.md3"));
        a.Call(4, e1, Asm.Vec(mins), Asm.Vec(maxs));             // setsize
        a.Call(2, e1, Asm.Vec(origin));                          // setorigin
        a.Get(334, name, a.Const(3));                           // modelnameforindex
        a.Get(334, badName, a.Const(9000));
        a.Get(317, pic, a.Text("gfx/nothere"));                 // precache_pic: null string when missing
        a.Get(19, snd, a.Text("misc/talk.wav"));                // precache_sound returns its argument
        a.End();
        CsqcHost host = rig.Load(a);
        QcVm vm = host.Vm;
        CsqcFieldOffsets f = host.Fields;

        Assert.Equal(-1f, rig.Global("own"));
        Assert.Equal(-1f, rig.Global("again"));
        Assert.Equal(0f, rig.Global("missing"));
        Assert.Equal(-2f, rig.Global("nullmodel")); // the engine's built-in empty model always loads
        Assert.Contains(rig.Warnings, w => w.Contains("models/missing.md3") && w.Contains("not found"));

        int a1 = rig.GlobalInt("e1"), a2 = rig.GlobalInt("e2"), a3 = rig.GlobalInt("e3");
        Assert.Equal(-1f, vm.FieldFloat(a1, f.ModelIndex));
        Assert.Equal("models/own.md3", vm.GetString(vm.FieldInt(a1, f.Model)));
        Assert.Equal(3f, vm.FieldFloat(a2, f.ModelIndex));
        Assert.Equal("models/items/a_cells.md3", vm.GetString(vm.FieldInt(a2, f.Model)));
        Assert.Equal(0f, vm.FieldFloat(a3, f.ModelIndex));
        Assert.Equal(0, vm.FieldInt(a3, f.Model));
        Assert.Contains(rig.Warnings, w => w.Contains("setmodel: model 'models/never_precached.md3' not precached"));

        Assert.Equal("'-1 -2 -3'", vm.FieldVector(a1, f.Mins).ToString());
        Assert.Equal("'4 5 6'", vm.FieldVector(a1, f.Maxs).ToString());
        Assert.Equal("'5 7 9'", vm.FieldVector(a1, f.Size).ToString());
        Assert.Equal("'10 20 30'", vm.FieldVector(a1, f.Origin).ToString());
        Assert.Equal("'9 18 27'", vm.FieldVector(a1, f.AbsMin).ToString());
        Assert.Equal("'14 25 36'", vm.FieldVector(a1, f.AbsMax).ToString());

        Assert.Equal("models/items/a_cells.md3", rig.GlobalString("name"));
        Assert.Equal(0, rig.GlobalInt("badname"));
        Assert.Equal(0, rig.GlobalInt("pic"));
        Assert.Equal("misc/talk.wav", rig.GlobalString("snd"));
        Assert.Equal("models/own.md3", host.ModelNameOf(a1));
        Assert.Null(host.ModelNameOf(a3));
    }

    [Fact]
    public void SetSize_WithBackwardsBounds_IsAProgramFault_NotAnException()
    {
        using Rig rig = new();
        Asm a = new();
        int e = a.E("e"), mins = a.V("mins", 5, 0, 0), maxs = a.V("maxs", -5, 0, 0);
        a.Begin("CSQC_Init");
        a.Get(14, e);
        a.Call(4, e, Asm.Vec(mins), Asm.Vec(maxs));
        a.End();
        CsqcHost host = rig.Load(a, init: false);
        Assert.False(host.Init());
        Assert.Contains("backwards mins/maxs", host.FaultMessage);
    }

    [Fact]
    public void FindRadiusAndFindBox_SeeLinkedEntities_ChainedThroughTheField()
    {
        using Rig rig = new();
        Asm a = new();
        int near = a.E("near"), far = a.E("far"), removed = a.E("removed"), found = a.E("found"), boxed = a.E("boxed"), nothing = a.E("nothing");
        int p1 = a.V("p1", 100, 0, 0), p2 = a.V("p2", 900, 0, 0), centre = a.V("centre", 90, 0, 0);
        int boxMin = a.V("boxmin", 800, -10, -10), boxMax = a.V("boxmax", 1000, 10, 10), elsewhere = a.V("elsewhere", 0, 0, 5000);
        a.Begin("CSQC_Init");
        a.Get(14, near); a.Get(14, far); a.Get(14, removed);
        a.Call(2, near, Asm.Vec(p1));
        a.Call(2, far, Asm.Vec(p2));
        a.Call(2, removed, Asm.Vec(p1));
        a.Call(15, removed);                                  // remove: unlinked
        a.Get(22, found, Asm.Vec(centre), a.Const(50));      // findradius
        a.Get(566, boxed, Asm.Vec(boxMin), Asm.Vec(boxMax)); // findbox
        a.Get(22, nothing, Asm.Vec(elsewhere), a.Const(50));
        a.End();
        CsqcHost host = rig.Load(a);

        Assert.Equal(rig.GlobalInt("near"), rig.GlobalInt("found"));
        Assert.Equal(0, host.Vm.FieldInt(rig.GlobalInt("near"), host.Fields.Chain)); // end of the chain is the world
        Assert.Equal(rig.GlobalInt("far"), rig.GlobalInt("boxed"));
        Assert.Equal(0, rig.GlobalInt("nothing"));
    }

    [Fact]
    public void Traces_WriteEveryTraceGlobal_EvenWhenNothingIsHit()
    {
        using Rig rig = new();
        Asm a = new();
        string[] floats = { "trace_allsolid", "trace_startsolid", "trace_fraction", "trace_inwater", "trace_inopen", "trace_plane_dist",
            "trace_dpstartcontents", "trace_dphitcontents", "trace_dphitq3surfaceflags", "trace_networkentity" };
        foreach (string name in floats) a.F(name, 99);
        a.V("trace_endpos", 9, 9, 9); a.V("trace_plane_normal", 9, 9, 9);
        a.B.Int(77, "trace_ent", QcType.Entity);
        a.B.Int(a.B.String("stale"), "trace_dphittexturename", QcType.String);
        int start = a.V("start", 1, 2, 3), end = a.V("end", 4, 5, 6), mins = a.V("bmins", -1, -1, -1), maxs = a.V("bmaxs", 1, 1, 1), world = a.E("worldent");
        int contents = a.F("contents"), pvs = a.F("pvs");
        a.Begin("CSQC_Init");
        a.Call(16, Asm.Vec(start), Asm.Vec(end), a.Const(0), world);                                  // traceline
        a.Call(90, Asm.Vec(start), Asm.Vec(mins), Asm.Vec(maxs), Asm.Vec(end), a.Const(1), world);     // tracebox
        a.Get(41, contents, Asm.Vec(start));                                                         // pointcontents
        a.Get(240, pvs, Asm.Vec(start), world);                                                      // checkpvs
        a.End();
        rig.Load(a);

        Assert.Equal(0f, rig.Global("trace_allsolid"));
        Assert.Equal(0f, rig.Global("trace_startsolid"));
        Assert.Equal(1f, rig.Global("trace_fraction"));
        Assert.Equal(0f, rig.Global("trace_inwater"));
        Assert.Equal(1f, rig.Global("trace_inopen"));
        Assert.Equal("'4 5 6'", rig.GlobalVector("trace_endpos").ToString());
        Assert.Equal("'0 0 0'", rig.GlobalVector("trace_plane_normal").ToString());
        Assert.Equal(0f, rig.Global("trace_plane_dist"));
        Assert.Equal(0, rig.GlobalInt("trace_ent"));
        Assert.Equal(0f, rig.Global("trace_dpstartcontents"));
        Assert.Equal(0f, rig.Global("trace_dphitcontents"));
        Assert.Equal(0f, rig.Global("trace_dphitq3surfaceflags"));
        Assert.Equal(0, rig.GlobalInt("trace_dphittexturename"));
        Assert.Equal(0f, rig.Global("trace_networkentity"));
        Assert.Equal(-1f, rig.Global("contents")); // CONTENTS_EMPTY
        Assert.Equal(3f, rig.Global("pvs"));       // no visibility data
        Assert.Equal(2, rig.Presentation.Calls["Trace"]);
        Assert.Equal(1, rig.Host!.Builtins.CallCount(16));
        Assert.Equal(1, rig.Host.Builtins.CallCount(90));
        Assert.Contains(rig.Host.Builtins.Registered, r => r is { Number: 16, Name: "traceline", Forwarded: true });
        Assert.Contains(rig.Host.Builtins.Registered, r => r is { Number: 360, Name: "ReadByte", Forwarded: false });

        // What a frame's traces learned does not outlive the frame.
        rig.Host.PreventInformationLeaks();
        Assert.Equal(0f, rig.Global("trace_fraction"));
        Assert.Equal("'0 0 0'", rig.GlobalVector("trace_endpos").ToString());
    }

    // ---- input -------------------------------------------------------------------------------------

    [Fact]
    public void GetInputState_ReplaysARememberedCommand_AndRotateMovesTurnsUnacknowledgedOnes()
    {
        using Rig rig = new();
        rig.State.PlayerCrouchMaxs = new QcVector(16, 16, 10);
        for (uint sequence = 1; sequence <= 200; sequence++)
            rig.State.PushMoveCommand(new CsqcUserCommand
            {
                Sequence = sequence, ViewAngles = new QcVector(0, sequence == 150 ? 90 : 0, 0), ForwardMove = sequence, SideMove = 2, UpMove = 3,
                Buttons = 5, FrameTime = 0.0125f, Crouch = sequence == 150,
            });
        rig.State.ServerMoveSequence = 149;

        Asm a = new();
        a.V("input_angles"); a.F("input_buttons"); a.V("input_movevalues"); a.F("input_timelength"); a.V("pmove_mins"); a.V("pmove_maxs");
        int found = a.F("found"), tooOld = a.F("tooold", 9), negative = a.F("negative", 9), turn = a.V("turn", 0, 90, 0);
        a.Begin("CSQC_Init");
        a.Get(345, tooOld, a.Const(72));    // only the last 128 commands are kept: 73..200
        a.Get(345, negative, a.Const(-1));
        a.Get(345, found, a.Const(150));
        a.Call(638, Asm.Vec(turn));          // CL_RotateMoves
        a.End();
        rig.Load(a);

        Assert.Equal(0f, rig.Global("tooold"));
        Assert.Equal(0f, rig.Global("negative"));
        Assert.Equal(1f, rig.Global("found"));
        Assert.Equal("'0 90 0'", rig.GlobalVector("input_angles").ToString());
        Assert.Equal(5f, rig.Global("input_buttons"));
        Assert.Equal("'150 2 3'", rig.GlobalVector("input_movevalues").ToString());
        Assert.Equal(0.0125f, rig.Global("input_timelength"));
        Assert.Equal("'16 16 10'", rig.GlobalVector("pmove_maxs").ToString()); // the crouching box

        // Commands after the acknowledged one were turned a quarter turn; older ones were not.
        CsqcUserCommand[] commands = rig.State.MoveCommands;
        Assert.Equal(180f, commands.Single(c => c.Sequence == 150).ViewAngles.Y, 3);
        Assert.Equal(90f, commands.Single(c => c.Sequence == 151).ViewAngles.Y, 3);
        Assert.Equal(0f, commands.Single(c => c.Sequence == 149).ViewAngles.Y, 3);
    }

    [Fact]
    public void Keys_NamesNumbersAndBindings()
    {
        Assert.Equal(512, CsqcKeys.StringToKeynum("MOUSE1"));
        Assert.Equal(512, CsqcKeys.StringToKeynum("mouse1"));
        Assert.Equal(13, CsqcKeys.StringToKeynum("ENTER"));
        Assert.Equal('a', CsqcKeys.StringToKeynum("A"));
        Assert.Equal(';', CsqcKeys.StringToKeynum("SEMICOLON"));
        Assert.Equal(-1, CsqcKeys.StringToKeynum(""));
        Assert.Equal(-1, CsqcKeys.StringToKeynum("NOSUCHKEY"));
        Assert.Equal(0xE9, CsqcKeys.StringToKeynum("é"));
        Assert.Equal("MOUSE1", CsqcKeys.KeynumToString(512));
        Assert.Equal("KP_INS", CsqcKeys.KeynumToString(157)); // the first of two names for one key
        Assert.Equal("SEMICOLON", CsqcKeys.KeynumToString(';'));
        Assert.Equal("a", CsqcKeys.KeynumToString('a'));
        Assert.Equal("<KEY NOT FOUND>", CsqcKeys.KeynumToString(-1));
        Assert.Equal("<UNKNOWN KEYNUM>", CsqcKeys.KeynumToString(5));
        Assert.Equal(291, CsqcKeys.Names.Length);
        // Round trip for every named key (aliases map back to the first name).
        foreach ((string keyName, int keyNumber) in CsqcKeys.Names) Assert.Equal(keyNumber, CsqcKeys.StringToKeynum(keyName));

        using Rig rig = new();
        Dictionary<int, string> binds = new() { [512] = "+fire", ['w'] = "+forward", [32] = "+jump", [134] = "+jump" };
        Asm a = new();
        int name = a.S("name"), num = a.F("num"), bind = a.S("bind"), unbound = a.S("unbound"), keys = a.S("keys"), none = a.S("none");
        int otherMap = a.S("othermap"), mouse = a.V("mouse", 9, 9, 9);
        a.Begin("CSQC_Init");
        a.Get(340, name, a.Const(13));
        a.Get(341, num, a.Text("SPACE"));
        a.Get(342, bind, a.Const(512));
        a.Get(342, unbound, a.Const(513));
        a.Get(342, otherMap, a.Const(512), a.Const(3));
        a.Get(610, keys, a.Text("+jump"));
        a.Get(610, none, a.Text("+nothing"));
        a.Call(343, a.Const(1));                 // setcursormode
        a.GetV(344, mouse);                     // getmousepos
        a.Call(346, a.Const(0.5f));              // setsensitivityscale
        a.End();
        rig.State.MousePosition = new QcVector(320, 240, 0);
        rig.Load(a, new CsqcHostOptions { KeyBinding = (key, map) => map <= 0 && binds.TryGetValue(key, out string? command) ? command : null });

        Assert.Equal("ENTER", rig.GlobalString("name"));
        Assert.Equal(32f, rig.Global("num"));
        Assert.Equal("+fire", rig.GlobalString("bind"));
        Assert.Equal(0, rig.GlobalInt("unbound"));
        Assert.Equal(0, rig.GlobalInt("othermap"));
        Assert.Equal(" '32' '134' '-1' '-1' '-1'", rig.GlobalString("keys"));
        Assert.Equal(" '-1' '-1' '-1' '-1' '-1'", rig.GlobalString("none"));
        Assert.True(rig.State.WantsMouseMove);
        Assert.Equal("'320 240 0'", rig.GlobalVector("mouse").ToString());
        Assert.Equal(0.5f, rig.State.SensitivityScale);
    }

    // ---- the frame ---------------------------------------------------------------------------------

    [Fact]
    public void UpdateView_SetsEngineGlobals_RunsThinkAndPredraw_AndSubmitsDrawnEntities()
    {
        using Rig rig = new();
        Asm a = new(updateView: false);
        a.F("frametime"); a.F("cltime"); a.F("servertime"); a.F("serverprevtime"); a.F("serverdeltatime"); a.F("maxclients");
        a.F("player_localentnum"); a.V("view_angles"); a.V("pmove_org"); a.F("pmove_onground"); a.F("clientcommandframe"); a.F("servercommandframe");
        a.V("v_forward"); a.V("v_right"); a.V("v_up"); a.F("dmg_take");
        int width = a.F("width"), height = a.F("height"), focus = a.F("focus"), thinks = a.F("thinks"), predraws = a.F("predraws"), one = a.Const(1);
        int drawn = a.E("drawn"), hidden = a.E("hidden"), thinkSelf = a.E("thinkself"), frameTime = a.F("seentime");
        a.Begin("CSQC_Init");
        a.Get(14, drawn);
        a.Get(14, hidden);
        a.End();
        a.Begin("thinker");
        a.B.Emit(QcOp.AddF, thinks, one, thinks);
        a.B.Emit(QcOp.StoreEnt, a.Self, thinkSelf);
        a.End();
        a.Begin("predrawer");
        a.B.Emit(QcOp.AddF, predraws, one, predraws);
        a.End();
        a.Begin("CSQC_UpdateView");
        a.Parm(0, width); a.Parm(1, height); a.Parm(2, focus);
        a.Copy(a.Time, frameTime);
        a.Call(300);                 // clearscene
        a.Call(301, a.Const(4));     // addentities(ENTMASK_NORMAL)
        a.Call(304);                 // renderscene
        a.End();
        CsqcHost host = rig.Load(a);
        QcVm vm = host.Vm;
        int e1 = rig.GlobalInt("drawn"), e2 = rig.GlobalInt("hidden");
        foreach (int e in new[] { e1, e2 })
        {
            vm.FieldFloat(e, host.Fields.ModelIndex) = 3;
            vm.FieldInt(e, host.Fields.Predraw) = vm.FindFunction("predrawer");
        }
        vm.FieldFloat(e1, host.Fields.DrawMask) = 4;
        vm.FieldInt(e1, host.Fields.Think) = vm.FindFunction("thinker");
        vm.FieldFloat(e1, host.Fields.NextThink) = 5;   // due at time 5

        rig.Message(w =>
        {
            w.WriteByte((int)Svc.Time); w.WriteFloat(10f);
            w.WriteByte((int)Svc.SetView); w.WriteShort(1);
        });
        rig.State.Time = 10;
        rig.State.ViewAngles = new QcVector(1, 2, 3);
        rig.State.ViewEntityOrigin = new QcVector(7, 8, 9);
        rig.State.ServerMoveSequence = 41;
        rig.State.PushMoveCommand(new CsqcUserCommand { Sequence = 44 });
        host.UpdateDamageGlobals(12, 3, new QcVector(1, 1, 1));

        Assert.True(host.UpdateView(1024, 768, frameTime: 0.016));
        Assert.Equal(1024f, rig.Global("width"));
        Assert.Equal(768f, rig.Global("height"));
        Assert.Equal(1f, rig.Global("focus"));
        Assert.Equal(10f, rig.Global("seentime"));
        Assert.Equal(0.016f, rig.Global("frametime"));
        Assert.Equal(10f, rig.Global("servertime"));
        Assert.Equal(8f, rig.Global("maxclients"));
        Assert.Equal(1f, rig.Global("player_localentnum"));
        Assert.Equal("'1 2 3'", rig.GlobalVector("view_angles").ToString());
        Assert.Equal("'7 8 9'", rig.GlobalVector("pmove_org").ToString());
        Assert.Equal(44f, rig.Global("clientcommandframe"));
        Assert.Equal(41f, rig.Global("servercommandframe"));
        Assert.Equal(0f, rig.Global("dmg_take")); // "Reset Dmg Globals Here"

        // The think ran once (and cleared nextthink), both predraws ran, one entity matched the mask.
        Assert.Equal(1f, rig.Global("thinks"));
        Assert.Equal(e1, rig.GlobalInt("thinkself"));
        Assert.Equal(0f, vm.FieldFloat(e1, host.Fields.NextThink));
        Assert.Equal(2f, rig.Global("predraws"));
        Assert.Equal(1, rig.Presentation.SceneEntities);
        Assert.Equal(1, rig.Presentation.Calls["AddEngineEntities"]);
        Assert.Equal(1, rig.Presentation.Calls["RenderScene"]);

        Assert.True(host.UpdateView(1024, 768));
        Assert.Equal(1f, rig.Global("thinks")); // not due again
        Assert.False(host.Faulted);
    }

    [Fact]
    public void ViewProperties_SetAndGet_AndViewAnglesBelongToTheInputState()
    {
        using Rig rig = new();
        Asm a = new();
        int origin = a.V("origin", 5, 6, 7), back = a.V("back"), ok = a.F("ok"), bad = a.F("bad", 9), angles = a.V("angles", 10, 20, 30), fov = a.F("fov");
        a.Begin("CSQC_Init");
        a.Get(303, ok, a.Const(11), Asm.Vec(origin));  // VF_ORIGIN
        a.GetV(309, back, a.Const(11));
        a.Get(303, bad, a.Const(9999), a.Const(1));
        a.Call(303, a.Const(33), Asm.Vec(angles));       // VF_CL_VIEWANGLES
        a.Call(303, a.Const(9), a.Const(100));           // VF_FOVX
        a.Get(309, fov, a.Const(9));
        a.End();
        rig.Load(a);
        Assert.Equal(1f, rig.Global("ok"));
        Assert.Equal("'5 6 7'", rig.GlobalVector("back").ToString());
        Assert.Equal(0f, rig.Global("bad"));
        Assert.Contains(rig.Warnings, w => w.Contains("unknown parm 9999"));
        Assert.Equal("'10 20 30'", rig.State.ViewAngles.ToString());
        Assert.Equal(100f, rig.Global("fov"));
    }

    [Fact]
    public void DrawBuiltins_ValidateLikeDarkPlaces_AndPolygonsArriveWhole()
    {
        using Rig rig = new();
        Asm a = new();
        int pos = a.V("pos", 1, 2, 0), scale = a.V("scale", 8, 8, 0), zero = a.V("zero"), rgb = a.V("rgb", 1, 1, 1);
        int good = a.F("good"), badFlag = a.F("badflag"), badScale = a.F("badscale"), width = a.F("width"), widthColors = a.F("widthcolors"), size = a.V("size", 9, 9, 9);
        a.Begin("CSQC_Init");
        a.Get(321, good, Asm.Vec(pos), a.Text("hi"), Asm.Vec(scale), Asm.Vec(rgb), a.Const(1), a.Const(0));       // drawstring
        a.Get(321, badFlag, Asm.Vec(pos), a.Text("hi"), Asm.Vec(scale), Asm.Vec(rgb), a.Const(1), a.Const(7));
        a.Get(321, badScale, Asm.Vec(pos), a.Text("hi"), Asm.Vec(zero), Asm.Vec(rgb), a.Const(1), a.Const(0));
        a.Get(327, width, a.Text("^1red^7"), a.Const(0), Asm.Vec(scale));        // stringwidth, codes drawn
        a.Get(327, widthColors, a.Text("^1red^7"), a.Const(1), Asm.Vec(scale));  // codes interpreted
        a.GetV(318, size, a.Text("gfx/nothere"));                                // getimagesize
        a.Call(306, a.Text(""), a.Const(0), a.Const(1));                          // R_BeginPolygon, 2D
        a.Call(307, Asm.Vec(pos), Asm.Vec(zero), Asm.Vec(rgb), a.Const(1));
        a.Call(307, Asm.Vec(scale), Asm.Vec(zero), Asm.Vec(rgb), a.Const(1));
        a.Call(307, Asm.Vec(rgb), Asm.Vec(zero), Asm.Vec(rgb), a.Const(1));
        a.Call(308);
        a.Call(308);                                                               // without a begin: a warning
        a.End();
        rig.Load(a);
        Assert.Equal(1f, rig.Global("good"));
        Assert.Equal(-2f, rig.Global("badflag"));
        Assert.Equal(-3f, rig.Global("badscale"));
        Assert.Equal(7 * 8f, rig.Global("width"));
        Assert.Equal(3 * 8f, rig.Global("widthcolors"));
        Assert.Equal("'0 0 0'", rig.GlobalVector("size").ToString());
        Assert.Equal(1, rig.Presentation.Calls["Text"]);
        Assert.Equal(1, rig.Presentation.Calls["DrawPolygon"]);
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_R_PolygonEnd: VM_CL_R_PolygonBegin wasn't called"));
    }

    // ---- the program is not trusted ----------------------------------------------------------------

    [Fact]
    public void BadArguments_BecomeRecordedFaults_NeverExceptions()
    {
        // Each body is one builtin call a hostile program might make.
        (string Name, Action<Asm> Body)[] cases =
        {
            ("entity out of range to setorigin", a => a.Call(2, a.B.Int(1 << 20, null, QcType.Entity), Asm.Vec(a.V("v")))),
            ("entity out of range to setmodel", a => a.Call(3, a.B.Int(-5, null, QcType.Entity), a.Text("x"))),
            ("wrong parameter count to ReadByte", a => a.Call(360, a.Const(1))),
            ("wrong parameter count to getstati", a => a.Call(331)),
            ("chain field out of range to findradius", a => a.Call(22, Asm.Vec(a.V("v")), a.Const(10), a.B.Int(1 << 24))),
            ("NaN to traceline", a => a.Call(16, Asm.Vec(a.V("nan", float.NaN, 0, 0)), Asm.Vec(a.V("v")), a.Const(0), a.B.Int(0, null, QcType.Entity))),
            ("empty picture name to drawpic", a => a.Call(322, Asm.Vec(a.V("v")), a.Text(""), Asm.Vec(a.V("w")), Asm.Vec(a.V("x")), a.Const(1))),
            ("entity out of range to sound", a => a.Call(8, a.B.Int(int.MaxValue, null, QcType.Entity), a.Const(0), a.Text("x"), a.Const(1), a.Const(1))),
        };
        foreach ((string name, Action<Asm> body) in cases)
        {
            using Rig rig = new();
            Asm a = new();
            a.Begin("CSQC_Init");
            body(a);
            a.End();
            CsqcHost host = rig.Load(a, init: false);
            bool ok = host.Init();
            Assert.False(ok, name);
            Assert.True(host.Faulted, name);
            Assert.Contains("CSQC_Init", host.FaultMessage);
        }
    }

    [Fact]
    public void OutOfRangeIndices_AreAnsweredWithoutFaulting()
    {
        using Rig rig = new();
        Asm a = new();
        int v = a.V("v"), s = a.S("s"), f = a.F("f");
        a.Begin("CSQC_Init");
        a.Get(331, f, a.Const(-1));               // stat index
        a.Get(331, f, a.Const(1e9f));
        a.Get(331, f, a.Const(3), a.Const(40), a.Const(99)); // absurd bit field
        a.Get(348, s, a.Const(1e9f), a.Text("name"));         // player slot
        a.Get(348, s, a.Const(-1e9f), a.Text("name"));
        a.Get(334, s, a.Const(-99999));           // model index
        a.Call(333, a.Self, a.Const(123456));      // setmodelindex on the world: allowed, warns
        a.Get(345, f, a.Const(1e12f));            // input frame
        a.Get(340, s, a.Const(1e9f));             // key number
        a.Get(342, s, a.Const(-7), a.Const(99));  // getkeybind
        a.GetV(504, v, a.Const(1e6f), a.Const(1)); // getentity
        a.Call(35, a.Const(4000), a.Text("m"));    // lightstyle
        a.Call(8, a.Self, a.Const(999), a.Text("x"), a.Const(1), a.Const(1)); // sound channel
        a.Call(8, a.Self, a.Const(1), a.Text("x"), a.Const(5), a.Const(1));   // sound volume
        a.End();
        CsqcHost host = rig.Load(a);
        Assert.False(host.Faulted, host.FaultMessage);
        Assert.False(rig.Presentation.Calls.ContainsKey("Start"));
        Assert.False(rig.Presentation.Calls.ContainsKey("SetLightStyle"));
    }

    [Fact]
    public void ArbitraryBytes_AsAMessage_NeverThrow_WhateverTheProgramReads()
    {
        // A program that reads greedily in every payload it is offered.
        Asm a = new();
        int n = a.F("n"), s = a.S("s");
        a.Begin("CSQC_Ent_Update");
        a.Get(360, n); a.Get(366, s); a.Get(501, s); a.Get(363, n); a.Get(364, n);
        a.End();
        a.Begin("CSQC_Parse_TempEntity");
        a.Get(360, n);
        a.Get(501, s);
        a.Return(n);
        a.Begin("CSQC_Parse_StuffCmd");
        a.End();

        using Rig rig = new();
        CsqcHost host = rig.Load(a, new CsqcHostOptions { KeepRunningAfterFault = true });
        Random random = new(20261007);
        for (int i = 0; i < 3000; i++)
        {
            byte[] bytes = new byte[random.Next(1, 96)];
            random.NextBytes(bytes);
            // Bias towards the two messages that hand the reader to the program.
            if (i % 3 == 0) bytes[0] = (byte)Svc.CsqcEntities;
            else if (i % 3 == 1) bytes[0] = (byte)Svc.TempEntity;
            DpParseResult result = rig.Message(w => w.WriteBytes(bytes));
            Assert.DoesNotContain("handler threw", result.Message ?? "");
        }
        Assert.True(host.EntityUpdates > 0);
        // Entities are bounded by the edict limit, not by what the stream asks for.
        Assert.True(host.Vm.NumEdicts <= DpProtocol.MaxEdicts);
    }

    [Fact]
    public void Shutdown_CallsTheProgram_AndDetaches()
    {
        using Rig rig = new();
        Asm a = new();
        int down = a.F("down"), one = a.Const(1);
        a.Begin("CSQC_Shutdown");
        a.Copy(one, down);
        a.End();
        a.Begin("GameCommand");
        a.End();
        CsqcHost host = rig.Load(a);
        host.Shutdown();
        Assert.Equal(1f, rig.Global("down"));
        Assert.Null(rig.Console.Host);
        // Afterwards nothing runs and nothing throws.
        Assert.False(host.UpdateView(1, 1));
        host.GameCommand("x");
        host.ParsePrint("x\n");
        rig.Console.AddText("cl_cmd anything\n");
        rig.Console.Execute();
    }
}

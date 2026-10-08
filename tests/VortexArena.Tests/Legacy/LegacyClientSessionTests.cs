using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using VortexArena.Tests.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The whole legacy client - connection, clock, client program, input history - against a scripted
/// server: everything the live join against a real DarkPlaces server found working or broken, pinned
/// here so it stays that way without a server. (The live run itself is tools/dp-probe.)
/// </summary>
public class LegacyClientSessionTests
{
    /// <summary>
    /// A client program that reads two bytes per entity update into globals and counts its frames -
    /// enough to show that the bytes of svc_csqcentities reach the program and the parser resumes at
    /// the right place after it.
    /// </summary>
    private static byte[] Program()
    {
        ProgsBuilder b = new();
        b.Int(0, "self", QcType.Entity);
        b.Float(0, "time");
        int first = b.Float(0, "ent_first"), second = b.Float(0, "ent_second"), frames = b.Float(0, "frames_drawn"), one = b.Float(1);
        int readByte = b.Builtin("ReadByte", 360);
        b.Function("CSQC_UpdateView");
        b.Emit(QcOp.AddF, frames, one, frames);
        b.Emit(QcOp.Done);
        b.Function("CSQC_Ent_Update");
        b.EmitRaw((int)QcOp.Call0, readByte);
        b.Emit(QcOp.StoreF, ProgsFile.OfsReturn, first);
        b.EmitRaw((int)QcOp.Call0, readByte);
        b.Emit(QcOp.StoreF, ProgsFile.OfsReturn, second);
        b.Emit(QcOp.Done);
        b.Function("CSQC_Ent_Remove");
        b.Emit(QcOp.Done);
        return b.Build();
    }

    private sealed class Rig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "va-session-" + Guid.NewGuid().ToString("N"));
        public readonly VirtualFileSystem Vfs = new();
        public readonly CvarService Cvars = new();
        public readonly LegacyClientSession Session;
        public readonly List<string> Events = new(), Commands = new();
        public readonly StringBuilder Printed = new();
        public DpNetChannel Server = new(0);
        public readonly List<byte[]> ToClient = new();
        public readonly byte[] ProgramBytes = Program();
        public double Now;
        public int Frame = 500, Nops, MoveDatagrams;

        public Rig(bool localProgram)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "placeholder.txt"), "x");
            if (localProgram) File.WriteAllBytes(Path.Combine(Root, "csprogs.dat"), ProgramBytes);
            Assert.True(Vfs.Mount(Root));
            ConfigInterpreter interpreter = new(Cvars, path => Vfs.Exists(path) ? Vfs.ReadText(path) : null);
            LegacyQcHost services = new(Cvars, Vfs) { PrintSink = s => Printed.Append(s) };
            LegacyClientOptions options = new();
            options.Client.NetFps = 1000;
            options.Client.Signon.Rate = 1_000_000;
            Session = new LegacyClientSession(services, interpreter, new HeadlessLegacyPresentation(Vfs), options);
            Session.Event += Events.Add;
        }

        private static byte[] Oob(string text)
        {
            byte[] t = Encoding.ASCII.GetBytes(text);
            byte[] p = new byte[4 + t.Length];
            p[0] = p[1] = p[2] = p[3] = 0xFF;
            t.CopyTo(p, 4);
            return p;
        }

        public void Take(IReadOnlyList<byte[]> sent)
        {
            foreach (byte[] d in sent)
            {
                if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xFF && d[2] == 0xFF && d[3] == 0xFF)
                {
                    string text = Encoding.ASCII.GetString(d, 4, d.Length - 4);
                    if (text == "getchallenge") ToClient.Add(Oob("challenge abc"));
                    else if (text.StartsWith("connect\\", StringComparison.Ordinal)) { Server = new DpNetChannel(Now); ToClient.Add(Oob("accept")); }
                    continue;
                }
                if (Server.Receive(d, Now, ToClient, out byte[]? message) != DpChannelReceive.Message) continue;
                DpMessageReader r = new(message!);
                bool move = false;
                while (r.Position < r.Length && !r.BadRead)
                {
                    int clc = r.ReadByte();
                    if (clc == 1) Nops++;
                    else if (clc == 3) { r.ReadSpan(55); move = true; }
                    else if (clc == 4) Commands.Add(r.ReadString());
                    else if (clc == 50) r.ReadLong();
                    else if (clc == 51) { r.ReadLong(); r.ReadUShort(); }
                    else break;
                }
                if (move) MoveDatagrams++;
            }
        }

        public void Step(double dt = 0.01, LegacyInput input = default)
        {
            Now += dt;
            Session.BeginFrame(Now);
            byte[][] batch = ToClient.ToArray();
            ToClient.Clear();
            foreach (byte[] d in batch) Session.Receive(d, Now);
            Take(Session.Frame(Now, input));
            Session.Draw(dt);
            Server.Transmit(default, Now, ToClient);
        }

        public void Settle(int steps = 30)
        {
            for (int i = 0; i < steps; i++) Step();
        }

        public void Reliable(Action<DpMessageWriter> build)
        {
            build(Server.Reliable);
            Server.Transmit(default, Now, ToClient);
            Settle();
        }

        public void Unreliable(Action<DpMessageWriter> build)
        {
            DpMessageWriter w = new();
            build(w);
            Server.Transmit(w.WrittenSpan, Now, ToClient);
        }

        public void Dispose()
        {
            Session.Dispose();
            Vfs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void Joins_Runs_The_Local_Program_Decodes_Csqc_Entities_And_Sends_Predicted_Input()
    {
        using Rig rig = new(localProgram: true);
        LegacyClientSession session = rig.Session;
        session.Connect(0);
        rig.Settle();
        Assert.Equal(DpClientState.Connected, session.Client.State);

        // Signon 1 names a client program the game data already has: no download, straight to prespawn,
        // and the program is running before the next server message can arrive.
        rig.Reliable(w =>
        {
            w.WriteByte(9); w.WriteString("csqc_progname csprogs.dat\n");
            w.WriteByte(9); w.WriteString($"csqc_progsize {rig.ProgramBytes.Length}\n");
            w.WriteByte(9); w.WriteString($"csqc_progcrc {Crc16.Block(rig.ProgramBytes)}\n");
            w.WriteByte(9); w.WriteString("cl_serverextension_download 2\n");
            DpServerMessageParserTests.WriteServerInfo(w, maxClients: 8);
            w.WriteByte(5); w.WriteShort(1);   // svc_setview 1
            w.WriteByte(25); w.WriteByte(1);   // svc_signonnum 1
        });
        Assert.Contains("prespawn", rig.Commands);
        Assert.DoesNotContain(rig.Commands, c => c.StartsWith("download", StringComparison.Ordinal));
        Assert.NotNull(session.Host);
        Assert.True(session.Host!.Initialized);
        Assert.Equal(1, session.ProgramsStarted);
        Assert.Contains(rig.Events, e => e.Contains("from the game data") && e.Contains("CSQC_Init completed"));
        // The map named by the server is not in this test's data: an empty world, and the client goes on.
        Assert.Contains("not in the game data", ((HeadlessLegacyPresentation)session.Presentation).Map.LoadError);

        // Signon 2 and 3. Two commands stuffed in one string stay two commands (the second is not
        // glued to the first), "cmd" is forwarded with $gameversion filled in, and the program has
        // not been asked to draw yet.
        rig.Reliable(w =>
        {
            w.WriteByte(9); w.WriteString("set probe_a 1\nset probe_b \"two words\"\n");
            w.WriteByte(25); w.WriteByte(2);
        });
        Assert.Contains("spawn", rig.Commands);
        Assert.Equal("1", rig.Cvars.GetString("probe_a"));
        Assert.Equal("two words", rig.Cvars.GetString("probe_b"));
        rig.Reliable(w =>
        {
            w.WriteByte(9); w.WriteString("cmd clientversion $gameversion\n");
            w.WriteByte(25); w.WriteByte(3);
        });
        Assert.Equal(new[] { "begin", "clientversion 806" }, rig.Commands.Skip(rig.Commands.Count - 2));
        Assert.Equal(0, session.FramesDrawn);
        Assert.Equal(0, rig.MoveDatagrams);

        // The first update: time, a CSQC entity whose two payload bytes only the program can measure,
        // then - after it - an entity frame. If the program did not consume exactly its bytes, the
        // parser would not find svc_entities where it is.
        rig.Unreliable(w =>
        {
            w.WriteByte(7); w.WriteFloat(10.0f);
            w.WriteByte(58); w.WriteShort(77); w.WriteByte(41); w.WriteByte(42); w.WriteShort(0);
            w.WriteByte(57); w.WriteLong(rig.Frame++); w.WriteLong(0);
            w.WriteShort(0x8000);
        });
        rig.Step();
        Assert.Equal(DpProtocol.Signons, session.State.Signon);
        Assert.Equal(1, session.EntityFrames);
        Assert.Equal(0, session.MessagesNotDecoded);
        Assert.Equal(1, session.Host.EntityUpdates);
        Assert.Equal(0, session.Host.DesyncCount);
        Assert.Equal(0, session.Host.ReadsOutsideMessage);
        QcVm vm = session.Host.Vm;
        Assert.Equal(41, vm.GlobalFloat(vm.FindGlobal("ent_first")!.Offset));
        Assert.Equal(42, vm.GlobalFloat(vm.FindGlobal("ent_second")!.Offset));
        Assert.True(session.Host.EdictForServerEntity(77) > 0);
        // Until now the clock simply was the server's.
        Assert.Equal(10.0, session.State.Time, 3);

        // In the game: a second of frames at 100 Hz with a 50 Hz server. The clock runs between
        // stamps, input goes out, each command sent enters the history the program predicts from.
        LegacyInput forward = new() { ForwardMove = 360 };
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
                rig.Unreliable(w =>
                {
                    w.WriteByte(7); w.WriteFloat(10.0f + (i + 2) * 0.01f);
                    w.WriteByte(57); w.WriteLong(rig.Frame++); w.WriteLong((int)session.Client.ServerMoveSequence);
                    w.WriteShort(0x8000);
                });
            rig.Step(0.01, forward);
        }
        Assert.InRange(session.State.Time, 10.9, 11.05);
        Assert.InRange(session.FramesDrawn, 99, 101);
        Assert.Equal(session.FramesDrawn, (long)vm.GlobalFloat(vm.FindGlobal("frames_drawn")!.Offset));
        Assert.Equal(0, session.FramesFaulted);
        Assert.InRange(rig.MoveDatagrams, 90, 101);
        Assert.Equal(51, session.EntityFrames);
        CsqcUserCommand head = session.State.MoveCommands[0], previous = session.State.MoveCommands[1];
        Assert.Equal(360, head.ForwardMove);
        Assert.Equal(360, previous.ForwardMove);
        Assert.True(head.Sequence >= previous.Sequence && previous.Sequence > 0);
        Assert.InRange(previous.FrameTime, 0.005f, 0.03f);
        // Sequences in the history are distinct and descending past the head: one entry per packet sent.
        uint[] sequences = session.State.MoveCommands.Skip(1).Take(20).Select(c => c.Sequence).ToArray();
        Assert.Equal(sequences.OrderByDescending(s => s), sequences);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());

        // A new level: the program is shut down and a new one started for it.
        CsqcHost old = session.Host;
        rig.Reliable(w =>
        {
            DpServerMessageParserTests.WriteServerInfo(w, maxClients: 8);
            w.WriteByte(5); w.WriteShort(1);
            w.WriteByte(25); w.WriteByte(1);
        });
        Assert.NotSame(old, session.Host);
        Assert.True(old.Disabled);
        Assert.Equal(2, session.ProgramsStarted);
        Assert.Equal(1, session.State.Signon);

        rig.Take(session.Disconnect(rig.Now));
        Assert.Null(session.Host);
        Assert.Equal(DpClientState.Disconnected, session.Client.State);
    }

    [Fact]
    public void A_Program_That_Is_Not_The_One_Named_Is_Downloaded_And_Keepalives_Cover_A_Long_Load()
    {
        using Rig rig = new(localProgram: false);
        LegacyClientSession session = rig.Session;
        session.Connect(0);
        rig.Settle();
        rig.Reliable(w =>
        {
            w.WriteByte(9); w.WriteString($"csqc_progname csprogs.dat\ncsqc_progsize {rig.ProgramBytes.Length}\ncsqc_progcrc {Crc16.Block(rig.ProgramBytes)}\ncl_serverextension_download 2\n");
            DpServerMessageParserTests.WriteServerInfo(w, maxClients: 8);
            w.WriteByte(25); w.WriteByte(1);
        });
        // Not in the game data: asked for, and no program (and no prespawn) until it has arrived.
        Assert.Contains("download csprogs.dat deflate", rig.Commands);
        Assert.DoesNotContain("prespawn", rig.Commands);
        Assert.Null(session.Host);

        byte[] wire = DpDownload.DeflateBytes(rig.ProgramBytes);
        rig.Reliable(w => { w.WriteByte(9); w.WriteString($"cl_downloadbegin {wire.Length} csprogs.dat deflate\n"); });
        Assert.Contains("sv_startdownload", rig.Commands);
        rig.Unreliable(w => { w.WriteByte(50); w.WriteLong(0); w.WriteShort(wire.Length); w.WriteBytes(wire); });
        rig.Settle(3);
        rig.Unreliable(w => { w.WriteByte(50); w.WriteLong(wire.Length); w.WriteShort(0); });
        rig.Settle(3);
        rig.Reliable(w => { w.WriteByte(9); w.WriteString($"cl_downloadfinished {wire.Length} {Crc16.Block(wire)} csprogs.dat\n"); });
        Assert.Contains("prespawn", rig.Commands);
        Assert.NotNull(session.Host);
        Assert.Contains(rig.Events, e => e.Contains("downloaded") && e.Contains("CSQC_Init completed"));

        // CL_KeepaliveMessage: asked from inside long work, it answers with a nop once five seconds
        // have passed since anything was sent, and with nothing before that.
        int nops = rig.Nops;
        Assert.Empty(session.KeepAlive(rig.Now + 1));
        Assert.Empty(session.KeepAlive(rig.Now + 4));
        rig.Now += 6;
        rig.Take(session.KeepAlive(rig.Now));
        Assert.Equal(nops + 1, rig.Nops);
        Assert.Empty(session.KeepAlive(rig.Now + 1));
        rig.Now += 5.5;
        rig.Take(session.KeepAlive(rig.Now));
        Assert.Equal(nops + 2, rig.Nops);
    }

    [Fact]
    public void A_Program_With_The_Wrong_Checksum_Is_Refused_And_Reported()
    {
        using Rig rig = new(localProgram: false);
        LegacyClientSession session = rig.Session;
        session.Connect(0);
        rig.Settle();
        byte[] other = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
        byte[] wire = DpDownload.DeflateBytes(other);
        rig.Reliable(w =>
        {
            w.WriteByte(9); w.WriteString($"csqc_progname csprogs.dat\ncsqc_progsize {other.Length}\ncsqc_progcrc {Crc16.Block(other)}\ncl_serverextension_download 2\n");
            DpServerMessageParserTests.WriteServerInfo(w, maxClients: 8);
            w.WriteByte(25); w.WriteByte(1);
        });
        rig.Reliable(w => { w.WriteByte(9); w.WriteString($"cl_downloadbegin {wire.Length} csprogs.dat deflate\n"); });
        rig.Unreliable(w => { w.WriteByte(50); w.WriteLong(0); w.WriteShort(wire.Length); w.WriteBytes(wire); });
        rig.Settle(3);
        rig.Reliable(w => { w.WriteByte(9); w.WriteString($"cl_downloadfinished {wire.Length} {Crc16.Block(wire)} csprogs.dat\n"); });
        // The bytes are what the server named, but they are not a program: refused with the reason, no throw.
        Assert.Null(session.Host);
        Assert.NotNull(session.ProgramError);
        Assert.Contains(rig.Events, e => e.Contains("refused"));
        Assert.Equal(DpClientState.Connected, session.Client.State);
    }
}

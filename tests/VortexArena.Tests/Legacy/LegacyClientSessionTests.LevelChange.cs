using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VortexArena.Engine.Simulation;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using VortexArena.Tests.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// What the player can still do after the server has changed level. Found on a public server: a join late in
/// a match, the level changed, and from then on nothing the player did reached the server - no chat, no
/// "join", no movement - while everything the server sent kept arriving. The client's input history still
/// held the old level's last command, whose time stamp (the old level's clock) was far ahead of the new
/// level's, so every new command measured zero milliseconds and CL_SendMove's "do not send 0ms packets"
/// held every packet back, the reliable stream with it. DarkPlaces wipes cl (CL_ClearState) at svc_serverinfo.
/// </summary>
public partial class LegacyClientSessionTests
{
    /// <summary>A client program that registers a console command and, when it is typed, sends "cmd qcseen <level>".</summary>
    private static byte[] CommandProgram()
    {
        ProgsBuilder b = new();
        b.Int(0, "self", QcType.Entity);
        b.Float(0, "time");
        int name = b.Int(b.String("qctest"), null, QcType.String);
        int send = b.Int(b.String("cmd qcseen\n"), null, QcType.String);
        int one = b.Float(1);
        int registerCommand = b.Builtin("registercommand", 352), localCmd = b.Builtin("localcmd", 46);
        b.Function("CSQC_Init");
        b.Emit(QcOp.StoreS, name, ProgsFile.OfsParm0);
        b.EmitRaw((int)QcOp.Call1, registerCommand);
        b.Emit(QcOp.Done);
        b.Function("CSQC_UpdateView");
        b.Emit(QcOp.Done);
        b.Function("CSQC_ConsoleCommand");
        b.Emit(QcOp.StoreS, send, ProgsFile.OfsParm0);
        b.EmitRaw((int)QcOp.Call1, localCmd);
        b.Emit(QcOp.StoreF, one, ProgsFile.OfsReturn);
        b.Emit(QcOp.Done);
        b.Function("CSQC_Ent_Update");
        b.Emit(QcOp.Done);
        b.Function("CSQC_Ent_Remove");
        b.Emit(QcOp.Done);
        return b.Build();
    }

    // One level's signon, then "in the game" at the given server time.
    private static void EnterLevel(Rig rig, float serverTime)
    {
        rig.Reliable(w =>
        {
            w.WriteByte(9); w.WriteString("csqc_progname csprogs.dat\n");
            w.WriteByte(9); w.WriteString($"csqc_progsize {rig.ProgramBytes.Length}\n");
            w.WriteByte(9); w.WriteString($"csqc_progcrc {Crc16.Block(rig.ProgramBytes)}\n");
            DpServerMessageParserTests.WriteServerInfo(w, maxClients: 8);
            w.WriteByte(5); w.WriteShort(1);
            w.WriteByte(25); w.WriteByte(1);
        });
        rig.Reliable(w => { w.WriteByte(25); w.WriteByte(2); });
        rig.Reliable(w => { w.WriteByte(25); w.WriteByte(3); });
        // The frame that enters the game is a long one (the level's loading ends in it): two server updates
        // are waiting when it runs, so the client is in the game with two different time stamps before it
        // builds its first command.
        ServerTick(rig, serverTime);
        ServerTick(rig, serverTime + 0.02f);
        rig.Step();
        Assert.Equal(DpProtocol.Signons, rig.Session.State.Signon);
    }

    private static void ServerTick(Rig rig, float serverTime) => rig.Unreliable(w =>
    {
        w.WriteByte(7); w.WriteFloat(serverTime);
        w.WriteByte(57); w.WriteLong(rig.Frame++); w.WriteLong((int)rig.Session.Client.ServerMoveSequence);
        w.WriteShort(0x8000);
    });

    // Half a second of play: a server tick every other 10 ms frame, the player walking forward.
    private static float Play(Rig rig, float serverTime, int frames = 50)
    {
        LegacyInput forward = new() { ForwardMove = 360 };
        for (int i = 0; i < frames; i++)
        {
            if (i % 2 == 0) ServerTick(rig, serverTime += 0.02f);
            rig.Step(0.01, forward);
        }
        return serverTime;
    }

    [Fact]
    public void After_Two_Level_Changes_A_Say_A_Move_And_A_Program_Command_Still_Reach_The_Server()
    {
        using Rig rig = new(localProgram: true, program: CommandProgram());
        LegacyClientSession session = rig.Session;
        // The session's own console, as a join from the native server browser has it: what it does not know
        // goes to the server (Cmd_ForwardToServer).
        session.Console.Interpreter.UnknownCommandHandler = (_, argv) => session.Client.SendStringCommand(string.Join(' ', argv));
        session.Connect(0);
        rig.Settle();

        // Level 1, joined late: the server's clock is at 900 seconds. Level 2 and 3 start their clocks over.
        float[] levelStart = { 900f, 1f, 1f };
        for (int level = 0; level < levelStart.Length; level++)
        {
            EnterLevel(rig, levelStart[level]);
            Assert.Equal(level + 1, session.ProgramsStarted);
            int movesBefore = rig.MoveDatagrams;
            rig.Commands.Clear();
            float time = Play(rig, levelStart[level] + 0.02f);
            Assert.True(rig.MoveDatagrams - movesBefore >= 40, $"level {level + 1}: {rig.MoveDatagrams - movesBefore} input packets reached the server in half a second of play");

            session.Console.AddText("say hello level " + (level + 1) + "\n");
            session.Console.AddText("qctest\n");
            session.Console.AddText("cmd join\n");
            Play(rig, time, 20);
            Assert.Contains("say hello level " + (level + 1), rig.Commands);
            Assert.Contains("qcseen", rig.Commands);
            Assert.Contains("join", rig.Commands);
            Assert.DoesNotContain("qctest", rig.Commands);   // the program's command is the program's, not the server's
        }
        Assert.Equal(0, session.MessagesNotDecoded);
        Assert.Equal(0, session.FramesFaulted);
    }
}

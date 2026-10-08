// legacy-server loopback: the headless legacy client (LegacyClientSession, the one tools/dp-probe
// drives against a real DarkPlaces server) joined to this server in one process over a pair of
// datagram queues, on a simulated clock: connect, download csprogs.dat in band, sign on, join, walk,
// jump - and at every step compare what the client believes with what the server knows.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private static int Loopback(Options o)
    {
        Dictionary<string, int> serverWarnings = new(StringComparer.Ordinal), clientWarnings = new(StringComparer.Ordinal);
        StringBuilder printLine = new();
        int printLines = 0;
        using SvEnvironment? env = Environment(o, serverWarnings, printLine, line => { if (printLines++ < 300) Log("server print: " + Printable(line)); });
        if (env is null) return 1;
        Log($"legacy-server loopback: map {o.Map}, {o.Seconds} s in the game, data {Path.GetFullPath(o.Data)}");

        using SvServer server = new(env, new SvServerOptions { MaxClients = o.MaxClients > 0 ? o.MaxClients : env.MaxPlayers, KeepRunningAfterFault = o.KeepRunning, RandomSeed = o.Seed ?? 1, Print = text => Log("server: " + Printable(text.TrimEnd('\n'))) });
        server.Event += text => Log("server event: " + Printable(text, 400));
        int serverCommands = 0;
        server.ClientCommandReceived += (c, text) => { if (!text.StartsWith("sentcvar", StringComparison.Ordinal) && serverCommands++ < 60) Log($"server cmd< #{c.Edict} " + Printable(text)); };
        Stopwatch wall = Stopwatch.StartNew();
        if (!server.Start(o.Map) || server.Host is null)
        {
            Log("RESULT: FAILED (the level could not be started)");
            return 1;
        }
        Log($"server up in {wall.Elapsed.TotalSeconds:0.0} s wall; faults {server.Host.FaultCount}");

        string clientWriteRoot = Path.Combine(Path.GetTempPath(), "legacy-loopback-" + Guid.NewGuid().ToString("N"));
        int clientPrints = 0;
        StringBuilder clientLine = new();
        LegacyClientOptions options = new() { AlwaysDownloadProgram = true, Host = new CsqcHostOptions { KeepRunningAfterFault = true } };
        options.Client.Signon.Name = "loopback";
        using SvLoopback loop = new(server, o.Data, clientWriteRoot, options,
            clientPrint: text =>
            {
                foreach (char c in text)
                {
                    if (c != '\n') { if (clientLine.Length < 400) clientLine.Append(c); continue; }
                    if (clientPrints++ < 120) Log("client print: " + Printable(clientLine.ToString()));
                    clientLine.Clear();
                }
            },
            clientWarning: text =>
            {
                text = text.TrimEnd();
                if (clientWarnings.Count < 500 || clientWarnings.ContainsKey(text)) clientWarnings[text] = clientWarnings.GetValueOrDefault(text) + 1;
            });
        LegacyClientSession session = loop.Client;
        DpClient client = session.Client;
        session.Event += text => Log("client event: " + Printable(text, 600));
        int commandsLogged = 0;
        client.CommandSent += command => { if (!command.StartsWith("sentcvar", StringComparison.Ordinal) && commandsLogged++ < 60) Log("client cmd> " + Printable(command)); };

        // What the server sends, by message kind, for the transcript: counted off the datagrams
        // themselves by the client's own parser statistics below, and here by size.
        const double Step = 1.0 / 256;
        const double JoinAt = 3, TurnFrom = 6, TurnTo = 8, ForwardFrom = 8, ForwardTo = 12, JumpAt = 14, JumpFor = 0.15;
        double inGameAt = -1, nextSecond = 0, maxDisagreement = 0, disagreementSum = 0;
        long disagreementSamples = 0;
        int lastStage = -1, second = 0;
        bool joined = false, turnStarted = false, sawForwardStart = false, sawForwardEnd = false, sawJump = false, reportedDownload = false;
        float scriptYawStart = 0, highestDuringJump = float.MinValue;
        QcVector forwardStart = default, forwardEnd = default, beforeJump = default, serverForwardStart = default, serverForwardEnd = default;
        long lastEntityFrames = 0, lastUpdates = 0, lastToClient = 0, lastBytes = 0;
        string? failure = null;
        DpClientState lastState = DpClientState.Disconnected;

        loop.Connect();
        wall.Restart();
        while (true)
        {
            bool inGame = inGameAt >= 0;
            if (!inGame && loop.Now > 120) { failure = $"not in the game after 120 s of simulated time (signon stage {client.Signon.Stage})"; break; }
            if (inGame && loop.Now - inGameAt >= o.Seconds) break;

            LegacyInput input = default;
            double t = inGame ? loop.Now - inGameAt : -1;
            if (inGame)
            {
                if (!joined && t >= JoinAt)
                {
                    joined = true;
                    client.SendStringCommand("join");
                    Log("script: sent \"join\"");
                }
                if (t >= TurnFrom && t < TurnTo)
                {
                    if (!turnStarted) { turnStarted = true; scriptYawStart = session.State.ViewAngles.Y; }
                    QcVector angles = session.State.ViewAngles;
                    angles.Y = scriptYawStart + (float)((t - TurnFrom) * 30);
                    session.State.ViewAngles = angles;
                }
                if (t >= ForwardFrom && t < ForwardTo)
                {
                    input.ForwardMove = loop.ClientCvars.Has("cl_forwardspeed") && loop.ClientCvars.GetFloat("cl_forwardspeed") > 0 ? loop.ClientCvars.GetFloat("cl_forwardspeed") : 400;
                    if (!sawForwardStart && loop.TryGetClientPlayerOrigin(out forwardStart))
                    {
                        sawForwardStart = true;
                        loop.TryGetServerPlayerOrigin(out serverForwardStart);
                        Log($"script: +forward for {ForwardTo - ForwardFrom} s, view yaw {session.State.ViewAngles.Y:0.0}; client origin {V(forwardStart)}, server origin {V(serverForwardStart)}");
                    }
                }
                else if (t >= ForwardTo + 0.5 && sawForwardStart && !sawForwardEnd && loop.TryGetClientPlayerOrigin(out forwardEnd))
                {
                    sawForwardEnd = true;
                    loop.TryGetServerPlayerOrigin(out serverForwardEnd);
                    Log($"script: -forward; client origin {V(forwardEnd)}, server origin {V(serverForwardEnd)}");
                }
                if (t >= JumpAt && t < JumpAt + JumpFor)
                {
                    input.Buttons |= 2;
                    if (!sawJump && loop.TryGetClientPlayerOrigin(out beforeJump)) { sawJump = true; Log($"script: +jump; client origin {V(beforeJump)}"); }
                }
                if (t >= JumpAt && t < JumpAt + 1.5 && loop.TryGetClientPlayerOrigin(out QcVector jumping)) highestDuringJump = MathF.Max(highestDuringJump, jumping.Z);
            }

            loop.Step(Step, input);

            if (client.State != lastState)
            {
                lastState = client.State;
                Log($"client state: {lastState}" + (client.LastError is null ? "" : $" ({client.LastError})"));
                if (lastState is DpClientState.Rejected or DpClientState.TimedOut or DpClientState.Failed or DpClientState.Disconnected) { failure = $"{lastState}: {client.LastError}"; break; }
            }
            if (client.Signon.Stage != lastStage)
            {
                lastStage = client.Signon.Stage;
                Log($"t={loop.Now:0.000}: client signon stage {lastStage}" + lastStage switch
                {
                    1 => $": csqc_progname {client.Signon.CsqcProgName}, csqc_progsize {client.Signon.CsqcProgSize}, csqc_progcrc {client.Signon.CsqcProgCrc}; level {session.State.WorldModel}, {session.State.MaxClients} slots",
                    4 => $": first svc_entities - in the game. view entity {session.State.ViewEntity}, player entity {session.State.PlayerEntity}",
                    _ => "",
                });
                if (lastStage == DpProtocol.Signons)
                {
                    inGameAt = loop.Now;
                    nextSecond = loop.Now + 1;
                }
            }
            if (client.Signon.LastDownload is { } download && !reportedDownload)
            {
                reportedDownload = true;
                byte[]? own = server.Host?.CsqcProgData;
                bool identical = download.Status == DpDownloadStatus.Completed && own is not null && own.AsSpan().SequenceEqual(download.Data);
                Log($"t={loop.Now:0.000}: download of {client.Signon.CsqcProgName}: {download.Status}, {download.WireSize} bytes on the wire, deflated={download.WasDeflated}, " +
                    $"{download.Data?.Length ?? 0} bytes inflated, verified against csqc_progsize/crc: {client.Signon.CsprogsVerified}, identical to the server's file: {identical}");
                if (!identical) failure ??= "the downloaded client program is not the server's";
            }

            if (inGame && joined && loop.Now - inGameAt > JoinAt + 1 && loop.TryGetClientPlayerOrigin(out QcVector mine) && loop.TryGetServerPlayerOrigin(out QcVector theirs))
            {
                double dx = mine.X - theirs.X, dy = mine.Y - theirs.Y, dz = mine.Z - theirs.Z, d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                maxDisagreement = Math.Max(maxDisagreement, d);
                disagreementSum += d;
                disagreementSamples++;
            }

            if (inGame && loop.Now >= nextSecond)
            {
                nextSecond += 1;
                second++;
                CsqcHost? h = session.Host;
                SvqcHost? sv = server.Host;
                bool hasMine = loop.TryGetClientPlayerOrigin(out QcVector c), hasTheirs = loop.TryGetServerPlayerOrigin(out QcVector s);
                int slot = session.State.PlayerEntity - 1;
                int frags = (uint)slot < (uint)session.State.Scores.Length ? session.State.Scores[slot].Frags : 0;
                Log(string.Create(CultureInfo.InvariantCulture, $"t+{second,2}: svc_entities {session.EntityFrames - lastEntityFrames,3}/s, datagrams to client {loop.DatagramsToClient - lastToClient,3}/s ({loop.BytesToClient - lastBytes} bytes), ") +
                    $"csqc ent updates {(h?.EntityUpdates ?? 0) - lastUpdates,4}/s, client faults {h?.FaultCount ?? 0} desyncs {h?.DesyncCount ?? 0} undecoded {session.MessagesNotDecoded}, server faults {sv?.FaultCount ?? 0}, " +
                    $"client origin {(hasMine ? V(c) : "-")}, server origin {(hasTheirs ? V(s) : "-")}, frags {frags}{(frags == -666 ? " (spectator)" : "")}, server acked move {client.ServerMoveSequence}");
                lastEntityFrames = session.EntityFrames;
                lastUpdates = h?.EntityUpdates ?? 0;
                lastToClient = loop.DatagramsToClient;
                lastBytes = loop.BytesToClient;
            }
        }

        CsqcHost? program = session.Host;
        SvqcHost? level = server.Host;
        double played = inGameAt >= 0 ? loop.Now - inGameAt : 0;
        Log($"summary: {loop.Now:0.0} s simulated in {wall.Elapsed.TotalSeconds:0.0} s wall; in the game for {played:0.0} s; client state {client.State}, signon stage {client.Signon.Stage}, last error {client.LastError ?? "none"}");
        Log($"summary: client: messages parsed {client.MessagesParsed}, not decoded {session.MessagesNotDecoded}{(session.FirstUndecoded is null ? "" : " (first: " + Printable(session.FirstUndecoded, 500) + ")")}, " +
            $"svc_entities frames {session.EntityFrames}, frame acks sent {client.FrameAcksSent}, input packets {client.MovePacketsSent}, reliable resends {client.Channel.PacketsResent}, unreliable gaps {client.Channel.DroppedDatagrams}");
        if (program is not null)
        {
            Log($"summary: client program: frames {session.FramesDrawn} ({session.FramesFaulted} faulted), CSQC_Ent_Update {program.EntityUpdates}, CSQC_Ent_Remove {program.EntityRemoves}, " +
                $"temp entities consumed {program.TempEntitiesConsumed} declined {program.TempEntitiesDeclined}, faults {program.FaultCount}, desyncs {program.DesyncCount}, entities {program.Vm.NumEdicts}");
            foreach (CsqcFault fault in program.Faults.Take(5)) Log($"client fault [{fault.EntryPoint}, message {fault.MessageIndex}]: {Printable(fault.Message, 1200)}");
            foreach (CsqcDesync desync in program.Desyncs.Take(3)) Log("client desync: " + Printable(desync.ToString(), 600) + " | " + Printable(desync.Stack, 500));
            foreach (((int number, string builtin), long calls) in program.UnimplementedBuiltins.OrderByDescending(u => u.Value).Take(10)) Log($"client unimplemented builtin: #{number} {builtin} x{calls}");
        }
        else Log($"summary: no client program was running ({session.ProgramError ?? "none was loaded"})");
        Log($"summary: server: datagrams received {server.DatagramsReceived} sent {server.DatagramsSent} ({server.BytesSent} bytes), ignored {server.DatagramsIgnored}, connectionless {server.ConnectionlessPackets}, " +
            $"string commands {server.StringCommandsReceived}, moves {server.MovesReceived}, entity frames sent {server.EntityFramesSent} skipped {server.EntityFramesSkipped}, clients dropped for bad messages {server.ClientsDroppedForBadMessages}");
        if (level is not null) Report(level, serverWarnings);
        if (sawForwardStart && sawForwardEnd)
        {
            float dx = forwardEnd.X - forwardStart.X, dy = forwardEnd.Y - forwardStart.Y, sx = serverForwardEnd.X - serverForwardStart.X, sy = serverForwardEnd.Y - serverForwardStart.Y;
            Log(string.Create(CultureInfo.InvariantCulture, $"movement: +forward for {ForwardTo - ForwardFrom} s moved the player {MathF.Sqrt(dx * dx + dy * dy):0.0} units by the client's account, {MathF.Sqrt(sx * sx + sy * sy):0.0} by the server's"));
        }
        if (sawJump) Log(string.Create(CultureInfo.InvariantCulture, $"movement: +jump raised the player from z {beforeJump.Z:0.0} to at most z {highestDuringJump:0.0} ({highestDuringJump - beforeJump.Z:0.0} units)"));
        if (disagreementSamples > 0)
            Log(string.Create(CultureInfo.InvariantCulture, $"agreement: client and server origin of the player differed by {disagreementSum / disagreementSamples:0.00} units on average, {maxDisagreement:0.00} at most, over {disagreementSamples} samples"));
        foreach ((string warning, int count) in clientWarnings.OrderByDescending(w => w.Value).Take(8)) Log($"client VM warning x{count}: {Printable(warning, 240)}");

        bool clean = failure is null && client.State == DpClientState.Connected && inGameAt >= 0 && session.MessagesNotDecoded == 0 && session.ProgramError is null
            && program is { FaultCount: 0, DesyncCount: 0 } && level is { FaultCount: 0 };
        failure ??= clean ? null : "see the summary: a fault, a desync, an undecoded message or a lost connection";
        Log(failure is null ? "RESULT: OK" : "RESULT: FAILED (" + failure + ")");
        try { if (Directory.Exists(clientWriteRoot)) Directory.Delete(clientWriteRoot, recursive: true); } catch (IOException) { }
        return failure is null ? 0 : 1;
    }
}

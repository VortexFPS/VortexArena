// legacy-server soak: a long game on a busy map - bots and in-process legacy clients that walk,
// turn, jump and fire for as long as asked - sampled once a minute: faults on both sides, memory,
// entity and zoned-string counts, and what a server frame cost. A leak or a slow drift shows as a
// column that keeps climbing; a fault ends the run.
using System.Diagnostics;
using System.Globalization;
using VortexArena.Legacy;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private static int Soak(Options o)
    {
        int bots = o.Bots >= 0 ? o.Bots : 8, clientCount = o.ClientCount > 0 ? o.ClientCount : 2;
        double gameSeconds = o.HasSeconds ? o.Seconds : 1800;
        // A match that would end inside the soak is a level change, which the lifecycle mode covers;
        // here the limits are lifted so that one level runs the whole time.
        using Rig rig = new(o, Math.Max(16, bots + clientCount + 2), ("bot_number", bots.ToString(CultureInfo.InvariantCulture)), ("timelimit_override", "0"), ("fraglimit_override", "0"), ("leadlimit_override", "0"),
            ("g_maplist_votable", "0"), ("skill", "5"));
        Log($"legacy-server soak: map {o.Map}, {gameSeconds:0} s of game time, {bots} bots, {clientCount} in-process clients");
        if (!rig.Server.Start(o.Map)) { Log("RESULT: FAILED (the level could not be started)"); return 1; }
        List<SvLoopbackClient> clients = new();
        for (int i = 0; i < clientCount; i++) clients.Add(rig.AddClient("soak" + (i + 1)));
        rig.Loop!.DrawRate = 20;
        if (!rig.Until(() => clients.All(c => c.InGame), 90)) { Log("RESULT: FAILED (the clients did not reach the game): " + string.Join("; ", clients.Select(ClientSummary))); return 1; }
        rig.Run(1.5);
        foreach (SvLoopbackClient c in clients) c.Session.Client.SendStringCommand("join");

        SvqcHost host = rig.Server.Host!;
        double start = host.Time, nextSample = start + 60;
        Stopwatch wall = Stopwatch.StartNew();
        long peakManaged = 0, peakWorkingSet = 0;
        int peakEdicts = 0, peakStrings = 0, minute = 0;
        double worstFrameMs = 0, frameMsSum = 0, worstInMinute = 0;
        long serverFrames = 0, lastFrames = host.Frames;
        Random script = new(o.Seed ?? 1);
        double[] turn = new double[clientCount], until = new double[clientCount];
        string? failure = null;
        Log("minute  game s  edicts  zoned strings  managed MB  working set MB  mean frame ms  worst frame ms  server faults  client faults/desyncs/undecoded  bots  frags");
        while (host.Time - start < gameSeconds)
        {
            // Each client: forward most of the time, a new turn rate and a jump or a shot now and then.
            for (int i = 0; i < clientCount; i++)
            {
                SvLoopbackClient c = clients[i];
                if (rig.Now >= until[i])
                {
                    until[i] = rig.Now + 0.5 + script.NextDouble() * 2;
                    turn[i] = (script.NextDouble() - 0.5) * 240;
                    c.Input = default;
                    c.Input.ForwardMove = script.Next(5) == 0 ? 0 : 400;
                    c.Input.SideMove = script.Next(4) == 0 ? (script.Next(2) == 0 ? 400 : -400) : 0;
                    if (script.Next(3) == 0) c.Input.Buttons |= 2;   // jump
                    if (script.Next(2) == 0) c.Input.Buttons |= 1;   // fire
                }
                QcVector angles = c.Session.State.ViewAngles;
                angles.Y += (float)(turn[i] * Rig.StepSeconds);
                c.Session.State.ViewAngles = angles;
            }
            long t0 = Stopwatch.GetTimestamp();
            rig.Loop.Step(Rig.StepSeconds);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            // One step is every client's frame and the server's; the server's share is what a tick costs.
            if (host.Frames != lastFrames)
            {
                serverFrames += host.Frames - lastFrames;
                lastFrames = host.Frames;
                frameMsSum += ms;
                if (ms > worstInMinute) worstInMinute = ms;
            }
            if (rig.Server.Host != host) { failure = "the level changed"; break; }
            if (rig.Server.TotalFaults > 0 && !o.KeepRunning) { failure = "the server program faulted: " + (host.Faults.Count > 0 ? Printable(host.Faults[0].EntryPoint + ": " + host.Faults[0].Message, 1200) : ""); break; }
            if (clients.Any(c => !c.InGame)) { failure = "a client left the game: " + string.Join("; ", clients.Select(ClientSummary)); break; }

            if (host.Time >= nextSample)
            {
                nextSample += 60;
                minute++;
                long managed = GC.GetTotalMemory(false), workingSet = Process.GetCurrentProcess().WorkingSet64;
                peakManaged = Math.Max(peakManaged, managed);
                peakWorkingSet = Math.Max(peakWorkingSet, workingSet);
                peakEdicts = Math.Max(peakEdicts, host.Vm.NumEdicts);
                peakStrings = Math.Max(peakStrings, host.Vm.ZonedStringCount);
                worstFrameMs = Math.Max(worstFrameMs, worstInMinute);
                int clientFaults = clients.Sum(c => c.Session.Host?.FaultCount ?? 0), desyncs = clients.Sum(c => c.Session.Host?.DesyncCount ?? 0);
                long undecoded = clients.Sum(c => c.Session.MessagesNotDecoded);
                float frags = 0;
                int totalFrags = host.Vm.FindField("totalfrags")?.Offset ?? -1;
                if (totalFrags >= 0) foreach (SvClient sc in host.Clients) if (sc.Active) frags += host.Vm.FieldFloat(sc.Edict, totalFrags);
                Log(string.Create(CultureInfo.InvariantCulture, $"{minute,6} {host.Time - start,7:0} {host.Vm.NumEdicts,7} {host.Vm.ZonedStringCount,14} {managed / 1048576.0,11:0.0} {workingSet / 1048576.0,15:0.0} {frameMsSum / Math.Max(1, serverFrames),14:0.000} {worstInMinute,15:0.0} {rig.Server.TotalFaults,14} {clientFaults,14}/{desyncs}/{undecoded} {host.Clients.Count(sc => sc.Active && sc.Connection is null),5} {frags,6:0}"));
                worstInMinute = 0;
                if (clientFaults > 0 || desyncs > 0 || undecoded > 0)
                {
                    failure = "a client faulted or lost a message: " + string.Join("; ", clients.Select(ClientSummary));
                    foreach (SvLoopbackClient c in clients)
                        foreach (VortexArena.Legacy.Csqc.CsqcFault fault in c.Session.Host?.Faults.Take(2) ?? Enumerable.Empty<VortexArena.Legacy.Csqc.CsqcFault>()) Log($"client fault [{fault.EntryPoint}]: {Printable(fault.Message, 1200)}");
                    break;
                }
            }
        }
        double played = host.Time - start;
        Log(string.Create(CultureInfo.InvariantCulture, $"soaked {played:0} s of game time ({serverFrames} server frames) in {wall.Elapsed.TotalSeconds:0} s wall; peak managed {peakManaged / 1048576.0:0.0} MB, peak working set {peakWorkingSet / 1048576.0:0.0} MB, peak edicts {peakEdicts}, peak zoned strings {peakStrings}, worst step {worstFrameMs:0.0} ms, mean step {frameMsSum / Math.Max(1, serverFrames):0.000} ms"));
        Log($"server: datagrams in {rig.Server.DatagramsReceived} out {rig.Server.DatagramsSent} ({rig.Server.BytesSent} bytes), moves {rig.Server.MovesReceived}, entity frames sent {rig.Server.EntityFramesSent}, culled by pvs {rig.Server.EntitiesCulledByPvs} by trace {rig.Server.EntitiesCulledByTrace}, camera eyes {rig.Server.CameraEyesAdded}, clients dropped for bad messages {rig.Server.ClientsDroppedForBadMessages}");
        for (int i = 0; i < clients.Count; i++) Log($"client{i + 1}: {ClientSummary(clients[i])}, frames drawn {clients[i].Session.FramesDrawn} ({clients[i].Session.FramesFaulted} faulted), frags {clients[i].ServerSlot?.Frags}");
        Report(host, rig.ServerWarnings);
        if (failure is null && played < gameSeconds - 1) failure = "the run ended early";
        Log(failure is null ? "RESULT: OK - 0 faults" : "RESULT: FAILED (" + failure + ")");
        return failure is null ? 0 : 1;
    }
}

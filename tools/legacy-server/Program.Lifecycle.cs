// legacy-server lifecycle: matches played to their end and through level changes, in one process -
// what sv_main.c SV_SpawnServer does the second and later times it runs (the program reloaded, the
// player slots carried over, connected clients sent the new level's serverinfo), driven the way a
// real server gets there: the time limit runs out, the program holds its intermission and map vote,
// and asks for the next map with "changelevel". With --clients the headless legacy client is
// connected across every change. One line per level: what leaked, what faulted.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private sealed class LevelRecord
    {
        public string Map = "";
        public double StartedAt, EndedAt = -1, GameTime;
        public int EdictsAtStart, EdictsAtEnd, StringsAtStart, StringsAtEnd, Faults, Unimplemented, Warnings, BotsAtEnd;
        public long ManagedBytes, WorkingSet, Frames;
        public double[] ClientInGameAfter = Array.Empty<double>();
        public string FirstFault = "";
        /// <summary>The program held a map vote at the end of this level, and how many clients' votes it had recorded.</summary>
        public bool MapVote;
        public int Votes;
    }

    /// <summary>What every multi-level mode shares: collects one record per level off the server's events.</summary>
    private sealed class LevelLog
    {
        public readonly List<LevelRecord> Levels = new();
        public readonly Dictionary<(int, string), long> Unimplemented = new();
        public readonly Dictionary<string, int> EngineWarnings = new(StringComparer.Ordinal);
        public readonly List<string> Faults = new();
        private readonly Func<double> _now;

        public LevelLog(SvServer server, Func<double> now, int clients)
        {
            _now = now;
            server.LevelStarted += host =>
            {
                // A full collection at each level start: what is still held then is held by something
                // that outlived the level before it.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Levels.Add(new LevelRecord
                {
                    ManagedBytes = GC.GetTotalMemory(true), WorkingSet = Process.GetCurrentProcess().WorkingSet64,
                    Map = host.WorldBaseName, StartedAt = _now(), EdictsAtStart = host.Vm.NumEdicts, StringsAtStart = host.Vm.ZonedStringCount,
                    ClientInGameAfter = Enumerable.Repeat(-1.0, clients).ToArray(),
                });
            };
            server.LevelEnding += host => Close(host);
        }

        public LevelRecord? Current => Levels.Count > 0 ? Levels[^1] : null;

        public void Close(SvqcHost host)
        {
            if (Current is not { EndedAt: < 0 } level) return;
            level.EndedAt = _now();
            level.GameTime = host.Time;
            level.Frames = host.Frames;
            level.EdictsAtEnd = host.Vm.NumEdicts;
            level.StringsAtEnd = host.Vm.ZonedStringCount;
            level.Faults = host.FaultCount;
            level.BotsAtEnd = host.Clients.Count(c => c.Active && c.Connection is null);
            // common/mapvoting/sv_mapvoting.qc: mapvote_initialized, and each voter's .mapvote (1-based choice)
            level.MapVote = host.Vm.FindGlobal("mapvote_initialized") is { } voting && host.Vm.GlobalFloat(voting.Offset) != 0;
            if (host.Vm.FindField("mapvote") is { } choice)
                level.Votes = host.Clients.Count(c => c.Active && host.Vm.FieldFloat(c.Edict, choice.Offset) != 0);
            level.Unimplemented = host.UnimplementedBuiltins.Count;
            level.Warnings = host.Warnings.Values.Sum();
            foreach ((string text, int count) in host.Warnings) EngineWarnings[text] = EngineWarnings.GetValueOrDefault(text) + count;
            foreach (((int number, string name), long count) in host.UnimplementedBuiltins)
                Unimplemented[(number, name)] = Unimplemented.GetValueOrDefault((number, name)) + count;
            foreach (SvFault fault in host.Faults.Take(4))
            {
                if (level.FirstFault.Length == 0) level.FirstFault = $"{fault.EntryPoint}: {fault.Message}";
                if (Faults.Count < 40) Faults.Add($"[{level.Map} t={fault.Time:0.00}] {fault.EntryPoint}: {fault.Message}");
            }
        }
    }

    private static void Default(SvEnvironment env, Options o, string name, string value)
    {
        foreach ((string set, string _) in o.Sets) if (set == name) return;
        env.SetCvar(name, value);
    }

    private static int Lifecycle(Options o)
    {
        Dictionary<string, int> warnings = new(StringComparer.Ordinal);
        StringBuilder printLine = new();
        int printLines = 0;
        using SvEnvironment? env = Environment(o, warnings, printLine, line => { if (printLines++ < 600) Log("print: " + Printable(line)); });
        if (env is null) return 1;
        Default(env, o, "bot_number", (o.Bots >= 0 ? o.Bots : 4).ToString(CultureInfo.InvariantCulture));
        Default(env, o, "bot_join_empty", "1");
        Default(env, o, "timelimit_override", "0.5");
        Default(env, o, "g_maplist", "stormkeep glowplant boil");
        Default(env, o, "sv_public", "0");
        if (o.Mode is not null) env.SetCvar("g_" + o.Mode, "1");
        int maxClients = o.MaxClients > 0 ? o.MaxClients : env.MaxPlayers;
        Log($"legacy-server lifecycle: first map {o.Map}, {o.Changes} level changes, {o.ClientCount} in-process clients, {env.Cvars.GetString("bot_number")} bots, " +
            $"timelimit_override {env.Cvars.GetString("timelimit_override")}, g_maplist \"{env.Cvars.GetString("g_maplist")}\", command {(o.Command is null ? "none" : "\"" + o.Command + "\" 10 s into each level")}");

        using SvServer server = new(env, new SvServerOptions { MaxClients = maxClients, KeepRunningAfterFault = o.KeepRunning, RandomSeed = o.Seed ?? 1, Print = text => { if (printLines++ < 600) Log("server: " + Printable(text.TrimEnd('\n'))); } });
        server.Event += text => Log("server event: " + Printable(text, 400));
        double now = 0;
        LevelLog log = new(server, () => now, o.ClientCount);
        Stopwatch wall = Stopwatch.StartNew();
        if (!server.Start(o.Map) || server.Host is null)
        {
            Log("RESULT: FAILED (the level could not be started)");
            return 1;
        }

        string clientWriteRoot = Path.Combine(Path.GetTempPath(), "legacy-lifecycle-" + Guid.NewGuid().ToString("N"));
        SvLoopback? loop = null;
        List<(string Name, int Count)> clientEvents = new();
        if (o.ClientCount > 0)
        {
            for (int i = 0; i < o.ClientCount; i++)
            {
                LegacyClientOptions options = new() { Host = new CsqcHostOptions { KeepRunningAfterFault = true } };
                options.Client.Signon.Name = "client" + (i + 1).ToString(CultureInfo.InvariantCulture);
                SvLoopbackClient c;
                if (loop is null)
                {
                    loop = new SvLoopback(server, o.Data, clientWriteRoot, options);
                    c = loop.Clients[0];
                }
                else c = loop.AddClient(options);
                int index = i;
                c.Session.Event += text => Log($"client{index + 1} event: " + Printable(text, 500));
                loop.Connect(c);
            }
        }

        double ticRate = env.Cvars.GetFloat("sys_ticrate") > 0 ? env.Cvars.GetFloat("sys_ticrate") : 1.0 / 72;
        double step = loop is null ? ticRate : 1.0 / 128;
        List<(System.Net.IPEndPoint To, byte[] Datagram)> outgoing = new();
        double levelBudget = o.HasSeconds ? o.Seconds : 240;
        bool commandIssued = false;
        int joinedOnLevel = -1;
        string? failure = null;
        long levelsSeen = 1;
        double levelDeadline = levelBudget;
        while (server.LevelsStarted <= o.Changes)
        {
            if (server.Host is null) { failure = "the server has no level"; break; }
            if (now > levelDeadline) { failure = $"level {log.Current?.Map} did not end within {levelBudget} s"; break; }
            if (server.LevelsStarted != levelsSeen)
            {
                levelsSeen = server.LevelsStarted;
                levelDeadline = now + levelBudget;
                commandIssued = false;
            }
            LevelRecord level = log.Current!;
            if (o.Command is not null && !commandIssued && now - level.StartedAt >= 10)
            {
                commandIssued = true;
                Log($"t={now:0.0}: console> {o.Command}");
                server.AddCommandText(o.Command + "\n");
            }
            if (loop is not null)
            {
                bool all = true;
                for (int i = 0; i < loop.Clients.Count; i++)
                {
                    SvLoopbackClient c = loop.Clients[i];
                    // "In the game" on THIS level: the client has heard of the level and signed on to it.
                    bool onThisLevel = c.InGame && c.Session.State.WorldModel == server.Host.WorldName;
                    if (onThisLevel && level.ClientInGameAfter[i] < 0)
                    {
                        level.ClientInGameAfter[i] = now - level.StartedAt;
                        Log($"t={now:0.0}: client{i + 1} in the game on {level.Map} after {level.ClientInGameAfter[i]:0.00} s (programs started {c.Session.ProgramsStarted})");
                    }
                    all &= level.ClientInGameAfter[i] >= 0;
                    if (c.Session.Client.State is DpClientState.Rejected or DpClientState.TimedOut or DpClientState.Failed or DpClientState.Disconnected)
                        failure ??= $"client{i + 1} is {c.Session.Client.State}: {c.Session.Client.LastError}";
                }
                if (all && joinedOnLevel != levelsSeen && now - level.StartedAt - level.ClientInGameAfter.Max() > 2)
                {
                    joinedOnLevel = (int)levelsSeen;
                    foreach (SvLoopbackClient c in loop.Clients) c.Session.Client.SendStringCommand("join");
                }
                // Walk in a slow circle once in the game, so a level change happens to moving players.
                foreach (SvLoopbackClient c in loop.Clients)
                {
                    c.Input = default;
                    if (!c.InGame) continue;
                    // While the map vote is up, vote: a client votes with impulses 1..9 (MapVote_Think).
                    if (server.Host.Vm.FindGlobal("mapvote_initialized") is { } voting && server.Host.Vm.GlobalFloat(voting.Offset) != 0)
                    {
                        c.Input.Impulse = 1;
                        continue;
                    }
                    c.Input.ForwardMove = 400;
                    VortexArena.QuakeC.QcVector angles = c.Session.State.ViewAngles;
                    angles.Y += (float)(step * 40);
                    c.Session.State.ViewAngles = angles;
                }
                if (failure is not null) break;
                now += step;
                loop.Step(step);
            }
            else
            {
                now += step;
                outgoing.Clear();
                server.Frame(now, outgoing);
            }
            if (!o.KeepRunning && server.TotalFaults > 0) { failure = "the server program faulted"; break; }
        }
        if (server.Host is { } last) log.Close(last);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long managed = GC.GetTotalMemory(true), working = Process.GetCurrentProcess().WorkingSet64;

        Log("");
        Log("level  map                 real s  game s  frames  edicts start>end  zoned strings start>end  bots  faults  unimpl  warnings  map vote (votes)  managed MB / working set MB at start  clients in game after (s)");
        for (int i = 0; i < log.Levels.Count; i++)
        {
            LevelRecord r = log.Levels[i];
            string clients = r.ClientInGameAfter.Length == 0 ? "-" : string.Join(" ", r.ClientInGameAfter.Select(t => t < 0 ? "NEVER" : t.ToString("0.00", CultureInfo.InvariantCulture)));
            Log(string.Create(CultureInfo.InvariantCulture, $"{i + 1,5}  {r.Map,-18} {r.EndedAt - r.StartedAt,7:0.0} {r.GameTime,7:0.0} {r.Frames,7} {r.EdictsAtStart,7}>{r.EdictsAtEnd,-7} {r.StringsAtStart,9}>{r.StringsAtEnd,-9} {r.BotsAtEnd,10} {r.Faults,7} {r.Unimplemented,7} {r.Warnings,9}  {(r.MapVote ? "yes" : "no"),8} ({r.Votes})         {r.ManagedBytes / 1048576.0,7:0.0} / {r.WorkingSet / 1048576.0,7:0.0}  {clients}"));
        }
        Log($"memory at the end, after a full collection: managed {managed / (1024.0 * 1024):0.0} MB, working set {working / (1024.0 * 1024):0.0} MB; {wall.Elapsed.TotalSeconds:0.0} s wall for {now:0.0} s simulated");
        foreach (((int number, string name), long count) in log.Unimplemented.OrderByDescending(p => p.Value)) Log($"unimplemented builtin #{number} {name}: {count}");
        foreach ((string text, int count) in log.EngineWarnings.OrderByDescending(p => p.Value).Take(20)) Log($"VM warning x{count}: {Printable(text, 240)}");
        foreach (string fault in log.Faults) Log("FAULT " + Printable(fault, 1500));
        if (loop is not null)
        {
            for (int i = 0; i < loop.Clients.Count; i++)
            {
                LegacyClientSession s = loop.Clients[i].Session;
                Log($"client{i + 1}: state {s.Client.State}, programs started {s.ProgramsStarted}, program faults now {s.Host?.FaultCount ?? 0}, desyncs now {s.Host?.DesyncCount ?? 0}, messages not decoded {s.MessagesNotDecoded}" +
                    (s.FirstUndecoded is null ? "" : " (first: " + Printable(s.FirstUndecoded, 400) + ")") + $", frames drawn {s.FramesDrawn} ({s.FramesFaulted} faulted)");
                if (s.MessagesNotDecoded > 0 || s.FramesFaulted > 0 || (s.Host?.FaultCount ?? 0) > 0) failure ??= $"client{i + 1} faulted or lost a message";
                foreach (CsqcFault fault in s.Host?.Faults.Take(3) ?? Enumerable.Empty<CsqcFault>()) Log($"client{i + 1} fault [{fault.EntryPoint}]: {Printable(fault.Message, 1000)}");
            }
            if (log.Levels.Any(l => l.ClientInGameAfter.Any(t => t < 0) && l.EndedAt - l.StartedAt > 20)) failure ??= "a client never got into the game on a level";
            loop.Dispose();
        }
        if (server.TotalFaults > 0) failure ??= $"{server.TotalFaults} server faults";
        if (log.Levels.Count <= o.Changes) failure ??= $"only {log.Levels.Count - 1} of {o.Changes} level changes happened";
        Log(failure is null ? $"RESULT: OK - {log.Levels.Count - 1} level changes, 0 faults" : "RESULT: FAILED (" + failure + ")");
        try { if (Directory.Exists(clientWriteRoot)) Directory.Delete(clientWriteRoot, recursive: true); } catch (IOException) { }
        return failure is null ? 0 : 1;
    }
}

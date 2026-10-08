// legacy-server perf: what one client frame of Xonotic's own client program (csprogs.dat) costs on this
// engine's QuakeC VM, with no window and no renderer - a local game with bots on a simulated clock, the
// headless client joined to it and driven by a seeded input script, CSQC_UpdateView called once a step
// and timed. Everything that is not the program or its builtins is outside the measurement, which is
// the point: this is the part of the frame the interpreter and the builtin table own.
//
// The run is repeatable (seeded random numbers on both sides, the simulated clock in place of every
// wall clock the program can read), so the digest printed at the end - a hash of the client program's
// whole memory, sampled through the run - is the same for two builds that compute the same thing. An
// optimisation that changes it has changed behaviour.
//
// Modes: default (frame times and allocation), "profile" (QcVm.Profile: interpreter against builtins,
// per builtin), and, in a build made with -p:QcOpStats=1, "ops" (statements per frame, per opcode, per
// pair of consecutive opcodes, per function).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VortexArena.Legacy;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private static int Perf(Options o)
    {
        int bots = o.Bots >= 0 ? o.Bots : 4;
        double seconds = o.HasSeconds ? o.Seconds : 20;
        const double Fps = 180, Dt = 1.0 / Fps, Warm = 8;
        bool profile = o.Mode == "profile", ops = o.Mode == "ops";

        StringBuilder serverLine = new();
        Dictionary<string, int> serverWarnings = new(StringComparer.Ordinal);
        using SvEnvironment env = new(o.Data, null, print: _ => { }, warning: text => { text = text.TrimEnd(); serverWarnings[text] = serverWarnings.GetValueOrDefault(text) + 1; });
        foreach ((string name, string value) in new[]
                 {
                     ("sv_public", "0"), ("g_warmup", "0"), ("g_start_delay", "0"), ("bot_number", bots.ToString(CultureInfo.InvariantCulture)), ("skill", "5"),
                     ("timelimit_override", "0"), ("fraglimit_override", "0"), ("leadlimit_override", "0"), ("g_maplist_votable", "0"),
                     ("g_forced_respawn", "1"), ("sv_spectate", "0"),
                 }) env.SetCvar(name, value);
        foreach ((string name, string value) in o.Sets) env.SetCvar(name, value);
        using SvServer server = new(env, new SvServerOptions { MaxClients = Math.Max(16, bots + 3), RandomSeed = o.Seed ?? 1, KeepRunningAfterFault = o.KeepRunning, Print = _ => { } });
        Log($"legacy-server perf: map {o.Map}, {bots} bots, {seconds:0} s measured at {Fps:0} client frames a second after {Warm:0} s of play, mode {o.Mode ?? "time"}");
        Stopwatch wall = Stopwatch.StartNew();
        if (!server.Start(o.Map)) { Log("RESULT: FAILED (the level could not be started)"); return 1; }
        Log(string.Create(CultureInfo.InvariantCulture, $"server up in {wall.Elapsed.TotalSeconds:0.0} s"));

        double clock = 0;
        LegacyClientOptions options = new()
        {
            ViewWidth = 1280, ViewHeight = 720,
            Host = new CsqcHostOptions { KeepRunningAfterFault = true, RandomSeed = 7, DirtyTime = () => clock, EntityIndex = o.Mode != "noindex", PrefetchFrameMemory = System.Environment.GetEnvironmentVariable("QC_NOPREFETCH") != "1", VerifyEntityIndex = o.Mode == "verify" },
        };
        options.Client.Signon.Name = "perf";
        using SvLoopback loop = new(server, o.Data, null, options);
        SvLoopbackClient c = loop.Clients[0];
        c.Services.RealTimeSource = () => clock;
        loop.Connect();
        wall.Restart();
        while (!c.InGame && loop.Now < 120) { loop.Step(1.0 / 128); clock = loop.Now; }
        if (!c.InGame) { Log("RESULT: FAILED (the client did not reach the game)"); return 1; }
        Log(string.Create(CultureInfo.InvariantCulture, $"client in the game after {loop.Now:0.0} s simulated, {wall.Elapsed.TotalSeconds:0.0} s wall"));

        LegacyClientSession session = c.Session;
        loop.DrawRate = 0;
        Random script = new(o.Seed ?? 1);
        double turn = 0, until = 0, joinAt = loop.Now + 1.5;
        bool joined = false;

        void Script()
        {
            if (!joined && loop.Now >= joinAt) { joined = true; session.Client.SendStringCommand("join"); }
            if (loop.Now >= until)
            {
                until = loop.Now + 0.5 + script.NextDouble() * 2;
                turn = (script.NextDouble() - 0.5) * 240;
                c.Input = default;
                c.Input.ForwardMove = script.Next(5) == 0 ? 0 : 400;
                c.Input.SideMove = script.Next(4) == 0 ? (script.Next(2) == 0 ? 400 : -400) : 0;
                if (script.Next(3) == 0) c.Input.Buttons |= 2;
                if (script.Next(2) == 0) c.Input.Buttons |= 1;
            }
            QcVector angles = session.State.ViewAngles;
            angles.Y += (float)(turn * Dt);
            session.State.ViewAngles = angles;
        }

        ulong digest = 14695981039346656037UL;
        void Digest()
        {
            if (session.Host is not { } h) return;
            QcVm vm = h.Vm;
            ulong d = digest;
            for (int i = 0; i < vm.NumGlobals; i++) d = (d ^ (uint)vm.GlobalInt(i)) * 1099511628211UL;
            int fields = vm.EntityFields;
            for (int e = 0; e < vm.NumEdicts; e++)
            {
                if (vm.IsFree(e)) { d = (d ^ 0xFFFFFFFFUL) * 1099511628211UL; continue; }
                for (int f = 0; f < fields; f++) d = (d ^ (uint)vm.PeekField(e, f)) * 1099511628211UL;
            }
            digest = d;
        }

        for (double end = loop.Now + Warm; loop.Now < end;)
        {
            Script();
            loop.Step(Dt);
            clock = loop.Now;
            session.Draw(Dt);
        }
        Digest();
        if (session.Host is not { } host) { Log("RESULT: FAILED (no client program)"); return 1; }
        QcVm client = host.Vm;

        int frames = (int)(seconds * Fps);
        double[] drawMs = new double[frames], stepMs = new double[frames];
        long drawAlloc = 0, stepAlloc = 0;
        double[]? again = o.Mode == "again" ? new double[frames] : null;
        QcProfile? prof = profile ? new QcProfile { MeasureAllocation = true } : null;
        long profDrawTicks = 0, profDrawBuiltinTicks = 0;
        client.Profile = prof;
#if QC_OPSTATS
        Array.Clear(client.OpCounts);
        Array.Clear(client.OpPairCounts);
        Array.Clear(client.FunctionStatements);
        long drawStatements = 0, touchedCode = 0, touchedGlobals = 0, touchedFields = 0;
        client.OpTiming = System.Environment.GetEnvironmentVariable("QC_OPTIMING") == "1";
        client.TakeTouched();
        client.ExecuteCalls = client.EnterCalls = client.LocalsCopied = client.BuiltinCallsCounted = 0;
#endif
        long framesBefore = session.FramesDrawn, updatesBefore = host.EntityUpdates;
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        wall.Restart();
        for (int i = 0; i < frames; i++)
        {
            Script();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            loop.Step(Dt);
            clock = loop.Now;
            long t1 = Stopwatch.GetTimestamp();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            long p0 = prof?.TotalTicks ?? 0, b0 = prof?.BuiltinTicks ?? 0;
#if QC_OPSTATS
            long s0 = Sum(client.OpCounts);
#endif
#if QC_OPSTATS
            client.TakeTouched();
#endif
            session.Draw(Dt);
            long t2 = Stopwatch.GetTimestamp();
#if QC_OPSTATS
            (int tc, int tg, int tf) = client.TakeTouched();
            touchedCode += tc; touchedGlobals += tg; touchedFields += tf;
#endif
            long a2 = GC.GetAllocatedBytesForCurrentThread();
#if QC_OPSTATS
            drawStatements += Sum(client.OpCounts) - s0;
#endif
            if (prof is not null) { profDrawTicks += prof.TotalTicks - p0; profDrawBuiltinTicks += prof.BuiltinTicks - b0; }
            stepMs[i] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            drawMs[i] = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
            if (again is not null)
            {
                // An experiment, not a measurement of play: the same frame drawn a second time straight
                // away, with everything it touches still in the processor's caches. The gap between the
                // two is what cold caches cost the first. It changes what the program does afterwards.
                long t3 = Stopwatch.GetTimestamp();
                session.Draw(0);
                again[i] = (Stopwatch.GetTimestamp() - t3) * 1000.0 / Stopwatch.Frequency;
            }
            stepAlloc += a1 - a0;
            drawAlloc += a2 - a1;
            if ((i & 63) == 63) Digest();
            if (session.Host != host) { Log("RESULT: FAILED (the level changed during the measurement)"); return 1; }
        }
        double wallSeconds = wall.Elapsed.TotalSeconds;
        client.Profile = null;
        Digest();

        static string Stats(double[] v)
        {
            double[] s = (double[])v.Clone();
            Array.Sort(s);
            double P(double q) => s[Math.Min(s.Length - 1, (int)(q * s.Length))];
            return string.Create(CultureInfo.InvariantCulture, $"mean {s.Average():0.000} p50 {P(0.5):0.000} p90 {P(0.9):0.000} p99 {P(0.99):0.000} p99.9 {P(0.999):0.000} worst {s[^1]:0.00} ms");
        }
        Log($"client program: {client.Progs.Statements.Length} statements, {client.NumGlobals} globals, {client.EntityFields} cells per entity ({client.NumEdicts * (long)client.EntityFields * 4 / 1048576} MB of entity fields in use)");
        {
            int watchedCount = 0, live = 0, thinkers = 0, predrawers = 0, masked = 0;
            for (int e = 1; e < client.NumEdicts; e++)
            {
                if (client.IsFree(e)) continue;
                live++;
                if (client.IsWatched(e)) watchedCount++;
                if (client.FieldInt(e, host.Fields.Think) != 0) thinkers++;
                if (client.FieldInt(e, host.Fields.Predraw) != 0) predrawers++;
                if (client.FieldInt(e, host.Fields.DrawMask) != 0) masked++;
            }
            Log($"entities at the end: {live} live, {watchedCount} in the addentities index; {thinkers} with a think function, {predrawers} with a predraw function, {masked} with a draw mask");
        }
        Log($"frames {frames} ({session.FramesDrawn - framesBefore} drawn, {session.FramesFaulted} faulted), CSQC_Ent_Update {host.EntityUpdates - updatesBefore}, client entities {client.NumEdicts}, scene entities last frame {loop.Presentation.SceneEntities}, wall {wallSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        Log("CSQC_UpdateView (the program's frame):      " + Stats(drawMs));
        Log("the rest of the step (network, server tick): " + Stats(stepMs));
        if (again is not null) Log("the same frame drawn again at once (warm):   " + Stats(again));
        Log(string.Create(CultureInfo.InvariantCulture, $"managed allocation per frame: draw {drawAlloc / (double)frames / 1024:0.00} KB, rest of the step {stepAlloc / (double)frames / 1024:0.00} KB; GC gen0/1/2 {GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2}"));
        Log($"client faults {host.FaultCount}, desyncs {host.DesyncCount}, undecoded {session.MessagesNotDecoded}; server faults {server.TotalFaults}");
        foreach (CsqcFault fault in host.Faults.Take(3)) Log($"client fault [{fault.EntryPoint}]: {Printable(fault.Message, 1200)}");
        Log($"digest {digest:X16}");
        // Per-frame draw times, one per line: runs of one build do the same frames, so the minimum of
        // each frame over several runs is that frame without whatever else the machine was doing.
        if (o.Dump is not null) File.WriteAllLines(o.Dump, drawMs.Select(v => v.ToString("0.00000", CultureInfo.InvariantCulture)));

        if (prof is not null)
        {
            double Ms(long ticks) => QcProfile.ToMilliseconds(ticks) / frames;
            Log(string.Create(CultureInfo.InvariantCulture, $"profile (timing every builtin call adds its own cost): in the draw: program {Ms(profDrawTicks):0.000} ms/frame = interpreter {Ms(profDrawTicks - profDrawBuiltinTicks):0.000} + builtins {Ms(profDrawBuiltinTicks):0.000}; " +
                $"outside the draw (entity updates, temp entities, commands): {Ms(prof.TotalTicks - profDrawTicks):0.000} ms/frame; builtin calls {prof.BuiltinCalls / (double)frames:0} per frame"));
            Dictionary<int, string> names = new();
            foreach (QcFunction f in client.Functions) if (f is not null && f.IsBuiltin) names.TryAdd(f.BuiltinNumber, f.Name);
            Log("  builtin                           calls/frame    us/frame   ns/call  bytes/frame allocated under it");
            foreach ((int number, long calls, long ticks) in prof.ByBuiltin().Take(40))
                Log(string.Create(CultureInfo.InvariantCulture, $"  #{number,-4} {names.GetValueOrDefault(number, "?"),-28} {calls / (double)frames,10:0.0} {Ms(ticks) * 1000,10:0.0} {QcProfile.ToMilliseconds(ticks) * 1e6 / calls,9:0} {prof.AllocatedBytes(number) / (double)frames,10:0}"));
        }
#if QC_OPSTATS
        {
            long total = Sum(client.OpCounts);
            Log(string.Create(CultureInfo.InvariantCulture, $"statements: {total / (double)frames:0} per frame in all, {drawStatements / (double)frames:0} of them in the draw"));
            Log(string.Create(CultureInfo.InvariantCulture, $"per frame: {client.ExecuteCalls / (double)frames:0.0} calls into the program from the engine, {client.EnterCalls / (double)frames:0.0} QuakeC function entries saving {client.LocalsCopied / (double)Math.Max(1, client.EnterCalls):0.0} local cells each, {client.BuiltinCallsCounted / (double)frames:0.0} builtin calls"));
            Log(string.Create(CultureInfo.InvariantCulture, $"memory one draw touches (64-byte lines, mean): instruction stream {touchedCode / (double)frames:0} ({touchedCode / (double)frames * 64 / 1024:0} KB), globals {touchedGlobals / (double)frames:0} ({touchedGlobals / (double)frames * 64 / 1024:0} KB), entity fields {touchedFields / (double)frames:0} ({touchedFields / (double)frames * 64 / 1024:0} KB)"));
            Log("  opcode        share   ns each when timed (clock overhead included, the same for every opcode)   us/frame");
            foreach ((long n, int op) in client.OpCounts.Select((n, op) => (n, op)).OrderByDescending(x => x.n).Take(40))
                if (n > 0) Log(string.Create(CultureInfo.InvariantCulture, $"  {(QcOp)op,-12} {100.0 * n / total,6:0.00}% {QcProfile.ToMilliseconds(client.OpTicks[op]) * 1e6 / n,8:0.0} {QcProfile.ToMilliseconds(client.OpTicks[op]) * 1e3 / frames,8:0.0}"));
            Log("  consecutive pair              share");
            foreach ((long n, int pair) in client.OpPairCounts.Select((n, pair) => (n, pair)).OrderByDescending(x => x.n).Take(40))
                if (n > 0) Log(string.Create(CultureInfo.InvariantCulture, $"  {(QcOp)(pair / 128),-12} {(QcOp)(pair % 128),-12} {100.0 * n / total,6:0.00}%"));
            Log("  function (statements per frame)");
            foreach ((long n, int fn) in client.FunctionStatements.Select((n, fn) => (n, fn)).OrderByDescending(x => x.n).Take(50))
                if (n > 0) Log(string.Create(CultureInfo.InvariantCulture, $"  {n / (double)frames,9:0.0} {100.0 * n / total,6:0.00}%  {client.Functions[fn].Name} ({client.Functions[fn].File})"));
        }
        static long Sum(long[] v) { long s = 0; foreach (long x in v) s += x; return s; }
#else
        if (ops) Log("the \"ops\" mode needs a build made with -p:QcOpStats=1");
#endif
        Log(host.FaultCount == 0 && server.TotalFaults == 0 ? "RESULT: OK" : "RESULT: FAILED (faults)");
        return host.FaultCount == 0 && server.TotalFaults == 0 ? 0 : 1;
    }
}

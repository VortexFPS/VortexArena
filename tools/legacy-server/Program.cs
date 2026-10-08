// legacy-server: Xonotic's server program (progs.dat) on this engine, in place of a DarkPlaces
// dedicated server. What DarkPlaces does at each point is in Base/darkplaces/host.c (Host_Init,
// Host_Frame), sv_main.c (SV_SpawnServer, SV_Frame) and sys_shared.c / sys_unix.c (the dedicated
// console read from stdin). The server itself is src/VortexArena.Legacy/Server.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    private const string Usage =
        "usage: legacy-server <mode> [options]\n" +
        "  probe  [--data DIR] [--out FILE]                      what progs.dat is and which engine builtins it needs\n" +
        "  run    [--data DIR] [--map NAME] [--seconds N] [--maxclients N] [--set CVAR VALUE]... [--out FILE] [--seed N] [--keep-running]\n" +
        "                                                        boot a level and simulate it headless, as fast as it goes\n" +
        "  serve  [--data DIR] [--map NAME] [--port N] [--bind ADDRESS] [--maxclients N] [--set CVAR VALUE]... [--out FILE] [--seconds N]\n" +
        "                                                        the same on a UDP port in real time, with a console on stdin; binds 127.0.0.1 unless --bind says otherwise\n" +
        "  loopback [--data DIR] [--map NAME] [--seconds N] [--set CVAR VALUE]... [--out FILE]\n" +
        "                                                        the headless legacy client joined to the server in one process, on a simulated clock\n" +
        "  capture --host ADDRESS [--port N] [--data DIR] [--seconds N] [--out FILE]\n" +
        "                                                        join any DP7 server as that client and write down the structure of what it sends\n" +
        "  lifecycle [--map NAME] [--changes N] [--clients N] [--bots N] [--command TEXT] [--set CVAR VALUE]... [--out FILE]\n" +
        "                                                        play matches to their end through N level changes, bots only or with in-process clients\n" +
        "  matrix [--maps a,b|all] [--modes dm,ctf|all] [--seconds N] [--bots N] [--out FILE]\n" +
        "                                                        boot and run every map in every game mode it supports; one line per cell\n" +
        "  clients [--map NAME] [--clients N] [--out FILE]       several in-process clients: visibility, reconnect, timeout, kick, full server, garbage, commands\n" +
        "  soak   [--map NAME] [--seconds N] [--bots N] [--clients N] [--out FILE]\n" +
        "                                                        a long run with bots and in-process clients; memory, edicts, strings, frame times\n" +
        "  trace  [--map NAME] --from \"x y z\" [--to \"x y z\"] [--mins \"x y z\"] [--maxs \"x y z\"] [--mode nudge|leaves|scan|pvs|file|bench]\n" +
        "                                                        one question to a level's collision world, answered in full\n" +
        "  run also takes --dump FILE (every entity's class, origin and box, as \"prvm_edicts server\" prints them) and --mode stats;\n" +
        "  capture also takes --mode spectate (stay an observer) and lists the entities visible at the end\n" +
        "  --data DIR   the Xonotic data directory to mount (default ../Base/data)\n" +
        "  --set        a cvar to set after the default configuration has run (bot_number 2, g_maplist ..., sv_public 0)";

    private static StreamWriter? _out;

    private static void Log(string line)
    {
        Console.WriteLine(line);
        _out?.WriteLine(line);
        _out?.Flush();
    }

    private sealed class Options
    {
        public string Data = Path.Combine("..", "Base", "data"), Map = "stormkeep", Bind = "127.0.0.1";
        public string? Out;
        public string Host = "127.0.0.1";
        public double Seconds = 60;
        public int MaxClients = 0, Port = 26000;
        public int? Seed;
        public bool KeepRunning, HasSeconds, Udp;
        public int Changes = 10, ClientCount = 0, Bots = -1, Parallel = 4;
        public string? Mode, Maps, Modes, Command, From, To, Mins, Maxs, Dump;
        public List<(string Name, string Value)> Sets = new();
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        Options o = new();
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--data": o.Data = Next(); break;
                    case "--map": o.Map = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--bind": o.Bind = Next(); break;
                    case "--host": o.Host = Next(); break;
                    case "--seconds": o.Seconds = double.Parse(Next(), CultureInfo.InvariantCulture); o.HasSeconds = true; break;
                    case "--maxclients": o.MaxClients = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--keep-running": o.KeepRunning = true; break;
                    case "--changes": o.Changes = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--clients": o.ClientCount = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--bots": o.Bots = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--mode": o.Mode = Next(); break;
                    case "--maps": o.Maps = Next(); break;
                    case "--modes": o.Modes = Next(); break;
                    case "--command": o.Command = Next(); break;
                    case "--udp": o.Udp = true; break;
                    case "--from": o.From = Next(); break;
                    case "--dump": o.Dump = Next(); break;
                    case "--to": o.To = Next(); break;
                    case "--mins": o.Mins = Next(); break;
                    case "--maxs": o.Maxs = Next(); break;
                    case "--parallel": o.Parallel = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--set": { string name = Next(); o.Sets.Add((name, Next())); break; }
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (o.Out is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Out))!);
            _out = new StreamWriter(o.Out, append: false, new UTF8Encoding(false));
        }
        try
        {
            return args[0] switch
            {
                "probe" => Probe(o),
                "run" => Run(o),
                "serve" => Serve(o),
                "loopback" => Loopback(o),
                "capture" => Capture(o, o.Host),
                "lifecycle" => Lifecycle(o),
                "matrix" => Matrix(o),
                "clients" => Clients(o),
                "trace" => TraceMode(o),
                "soak" => Soak(o),
                _ => Fail(Usage),
            };
        }
        finally { _out?.Dispose(); }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static SvEnvironment? Environment(Options o, Dictionary<string, int> warnings, StringBuilder printLine, Action<string>? onPrintLine = null)
    {
        string writeRoot = Path.Combine(Path.GetTempPath(), "legacy-server-" + Guid.NewGuid().ToString("N"));
        try
        {
            SvEnvironment env = new(o.Data, writeRoot,
                print: text =>
                {
                    foreach (char c in text)
                    {
                        if (c != '\n') { if (printLine.Length < 600) printLine.Append(c); continue; }
                        onPrintLine?.Invoke(printLine.ToString());
                        printLine.Clear();
                    }
                },
                warning: text =>
                {
                    text = text.TrimEnd();
                    if (warnings.Count < 2000 || warnings.ContainsKey(text)) warnings[text] = warnings.GetValueOrDefault(text) + 1;
                });
            foreach ((string name, string value) in o.Sets) env.SetCvar(name, value);
            return env;
        }
        catch (DirectoryNotFoundException e)
        {
            Log("RESULT: FAILED (" + e.Message + ")");
            return null;
        }
    }

    private static string Printable(string s, int limit = 300)
    {
        StringBuilder sb = new(s.Length);
        foreach (char c in s) sb.Append(c == '\n' ? "\\n" : c < ' ' ? $"\\x{(int)c:X2}" : c.ToString());
        return sb.Length > limit ? sb.ToString(0, limit) + "..." : sb.ToString();
    }

    private static string V(QcVector v) => string.Create(CultureInfo.InvariantCulture, $"'{v.X:0.0} {v.Y:0.0} {v.Z:0.0}'");

    // ---- run: boot a level, simulate it headless ----------------------------------------------------------

    private static int Run(Options o)
    {
        Dictionary<string, int> warnings = new(StringComparer.Ordinal);
        StringBuilder printLine = new();
        int printLines = 0;
        using SvEnvironment? env = Environment(o, warnings, printLine, line => { if (printLines++ < 400) Log("print: " + Printable(line)); });
        if (env is null) return 1;
        Log($"legacy-server run: map {o.Map}, {o.Seconds} s of game time, {(o.MaxClients > 0 ? o.MaxClients : env.MaxPlayers)} slots, data {Path.GetFullPath(o.Data)}");
        Log($"mounted {env.Files.MountedPaths.Count} packages; {env.EngineCvars} engine cvars; default.cfg {(env.DefaultsExecuted ? "executed" : "NOT FOUND")} ({env.Interpreter.FilesExecuted} files)");

        Stopwatch wall = Stopwatch.StartNew();
        SvqcHost? host = env.StartLevel(o.Map, new SvqcHostOptions { MaxClients = o.MaxClients > 0 ? o.MaxClients : env.MaxPlayers, KeepRunningAfterFault = o.KeepRunning, RandomSeed = o.Seed }, print: text => Log("engine: " + Printable(text.TrimEnd('\n'))));
        if (host is null)
        {
            Log("RESULT: FAILED (the level could not be started)");
            return 1;
        }
        using SvqcHost _ = host;
        Log($"level started in {wall.Elapsed.TotalSeconds:0.0} s wall: {host.Vm.NumEdicts} edicts, {host.ModelCount - 1} models, {host.SoundCount - 1} sounds precached, " +
            $"{host.AutocvarsBound} autocvars, signon buffer {host.Signon.Length} bytes, {host.FaultCount} faults, csprogs {(host.CsqcProgData is null ? "none" : $"{host.CsqcProgSize} bytes crc {host.CsqcProgCrc}")}");
        host.ExecuteCommands();

        int totalFrags = host.Vm.FindField("totalfrags")?.Offset ?? -1, deaths = host.Vm.FindField("death_time")?.Offset ?? -1;
        double ticRate = env.Cvars.GetFloat("sys_ticrate") > 0 ? env.Cvars.GetFloat("sys_ticrate") : 1.0 / 72;
        Dictionary<int, (QcVector First, QcVector Last, double Distance)> travelled = new();
        double start = host.Time, nextReport = start + 5;
        wall.Restart();
        long frames = 0;
        while (host.Time - start < o.Seconds && !host.QuitRequested)
        {
            // The simulation is its own clock: real time advances exactly as game time does.
            host.RealTime += ticRate;
            if (!host.RunFrame(ticRate) && !o.KeepRunning) break;
            frames++;
            foreach (SvClient client in host.Clients)
            {
                if (!client.Active) continue;
                QcVector origin = host.Vm.FieldVector(client.Edict, host.F.Origin);
                if (travelled.TryGetValue(client.Edict, out (QcVector First, QcVector Last, double Distance) t))
                {
                    double dx = origin.X - t.Last.X, dy = origin.Y - t.Last.Y, dz = origin.Z - t.Last.Z;
                    double step = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    // A respawn is a teleport, not travel.
                    travelled[client.Edict] = (t.First, origin, t.Distance + (step < 64 ? step : 0));
                }
                else travelled[client.Edict] = (origin, origin, 0);
            }
            if (host.Time >= nextReport)
            {
                nextReport += 5;
                StringBuilder line = new();
                line.Append(CultureInfo.InvariantCulture, $"t={host.Time - start,5:0.0}s frames {frames} edicts {host.Vm.NumEdicts} faults {host.FaultCount}");
                foreach (SvClient client in host.Clients)
                {
                    if (!client.Active) continue;
                    int e = client.Edict;
                    line.Append(CultureInfo.InvariantCulture, $" | #{e} \"{Printable(host.Vm.GetString(host.Vm.FieldInt(e, host.F.NetName)), 20)}\" at {V(host.Vm.FieldVector(e, host.F.Origin))} hp {host.Vm.FieldFloat(e, host.F.Health):0}");
                    if (totalFrags >= 0) line.Append(CultureInfo.InvariantCulture, $" frags {host.Vm.FieldFloat(e, totalFrags):0}");
                    line.Append(CultureInfo.InvariantCulture, $" moved {travelled.GetValueOrDefault(e).Distance:0}u");
                }
                Log(line.ToString());
            }
        }
        // SVVM_count_edicts ("prvm_edictcount server"), and the entities by classname for comparison
        // with a DarkPlaces server's "prvm_edicts server".
        {
            int active = 0, models = 0, solid = 0;
            SortedDictionary<string, int> classes = new(StringComparer.Ordinal);
            for (int e = 0; e < host.Vm.NumEdicts; e++)
            {
                if (host.Vm.IsFree(e)) continue;
                active++;
                if (host.Vm.FieldFloat(e, host.F.Solid) != 0) solid++;
                if (host.Vm.FieldInt(e, host.F.Model) != 0) models++;
                string name = host.Vm.GetString(host.Vm.FieldInt(e, host.F.ClassName));
                classes[name] = classes.GetValueOrDefault(name) + 1;
            }
            Log($"num_edicts:{host.Vm.NumEdicts} active:{active} view:{models} touch:{solid}");
            foreach ((string name, int count) in classes) Log($"class {count} {name}");
        }
        if (o.Dump is not null) DumpEdicts(host, o.Dump);
        if (o.Mode == "stats")
        {
            // vm_customstats: which entity field feeds each stat the program registered (addstat).
            Dictionary<int, string> fieldNames = new();
            foreach (QcDef def in host.Program.FieldDefs)
                if (!fieldNames.ContainsKey(def.Offset)) fieldNames[def.Offset] = def.Name;
            for (int i = 32; i < 256; i++)
            {
                (byte type, int field) = host.GetCustomStat(i);
                if (type == 0) continue;
                SvClient? first = host.Clients.FirstOrDefault(c => c.Active);
                string value = first is null ? "" : string.Create(CultureInfo.InvariantCulture, $" = {host.Vm.FieldFloat(first.Edict, field):R} (bits 0x{host.Vm.FieldInt(first.Edict, field):X8}) on entity {first.Edict}");
                Log($"stat {i}: type {type} field {fieldNames.GetValueOrDefault(field, "?")}{value}");
            }
        }
        double simulated = host.Time - start;
        Log($"simulated {simulated:0.00} s of game time in {frames} frames, {wall.Elapsed.TotalSeconds:0.0} s wall ({wall.Elapsed.TotalMilliseconds / Math.Max(1, frames):0.000} ms a frame); traces {host.World.Traces}, candidate overflows {host.World.CandidateOverflows}");
        Report(host, warnings);

        bool botsMoved = travelled.Values.Any(t => t.Distance > 100);
        float frags = 0;
        if (totalFrags >= 0) foreach (SvClient client in host.Clients) if (client.Active) frags += host.Vm.FieldFloat(client.Edict, totalFrags);
        Log($"RESULT: {(host.FaultCount == 0 && simulated >= o.Seconds - ticRate ? "OK" : "FAILED")} - {host.FaultCount} faults, {host.ActiveClients} clients, bots moved: {botsMoved}, total frags: {frags:0}, " +
            $"{host.UnimplementedBuiltins.Count} unimplemented builtins called");
        return host.FaultCount == 0 ? 0 : 1;
    }

    // "prvm_edicts server", reduced to the fields a placement comparison needs and written the way
    // DarkPlaces prints them (a field that is zero or empty is not printed; vectors as %.9g).
    private static void DumpEdicts(SvqcHost host, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using StreamWriter w = new(path, false, new UTF8Encoding(false));
        static string G(float f) => ((double)f).ToString("G9", CultureInfo.InvariantCulture);
        static string Vec3(QcVector v) => $"'{G(v.X)} {G(v.Y)} {G(v.Z)}'";
        static bool NonZero(QcVector v) => v.X != 0 || v.Y != 0 || v.Z != 0;
        w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"time {host.Time:0.000} num_edicts {host.Vm.NumEdicts}"));
        for (int e = 0; e < host.Vm.NumEdicts; e++)
        {
            w.WriteLine($"server EDICT {e}:");
            if (host.Vm.IsFree(e)) { w.WriteLine("FREE"); continue; }
            float solid = host.Vm.FieldFloat(e, host.F.Solid);
            if (solid != 0) w.WriteLine($"solid          {G(solid)}");
            QcVector origin = host.Vm.FieldVector(e, host.F.Origin), angles = host.Vm.FieldVector(e, host.F.Angles);
            if (NonZero(origin)) w.WriteLine($"origin         {Vec3(origin)}");
            if (NonZero(angles)) w.WriteLine($"angles         {Vec3(angles)}");
            string cls = host.Vm.GetString(host.Vm.FieldInt(e, host.F.ClassName)), model = host.Vm.GetString(host.Vm.FieldInt(e, host.F.Model));
            if (cls.Length > 0) w.WriteLine($"classname      {cls}");
            if (model.Length > 0) w.WriteLine($"model          {model}");
            QcVector mins = host.Vm.FieldVector(e, host.F.Mins), maxs = host.Vm.FieldVector(e, host.F.Maxs);
            if (NonZero(mins)) w.WriteLine($"mins           {Vec3(mins)}");
            if (NonZero(maxs)) w.WriteLine($"maxs           {Vec3(maxs)}");
        }
    }

    private static void Report(SvqcHost host, Dictionary<string, int> warnings)
    {
        Log($"console: {host.CommandLinesExecuted} lines executed, {host.GameCommands} sv_cmd, {host.QcCommands.Count} commands registered by the program; " +
            $"bad movetypes {host.BadMoveTypes}, engine player physics skipped {host.EnginePlayerPhysicsSkipped}");
        if (host.UnimplementedBuiltins.Count == 0) Log("unimplemented builtins called: none");
        else
        {
            Log($"unimplemented builtins called ({host.UnimplementedBuiltins.Count}):");
            foreach (((int number, string name), long count) in host.UnimplementedBuiltins.OrderByDescending(p => p.Value))
                Log($"  #{number} {name}: {count}");
        }
        Log($"VM warnings: {warnings.Values.Sum()} ({warnings.Count} distinct)");
        foreach ((string text, int count) in warnings.OrderByDescending(p => p.Value).Take(25)) Log($"  {count}x {Printable(text, 220)}");
        foreach (SvFault fault in host.Faults.Take(10))
            Log($"FAULT in {fault.EntryPoint} at t={fault.Time:0.00}: {Printable(fault.Message, 1500)}");
    }
}

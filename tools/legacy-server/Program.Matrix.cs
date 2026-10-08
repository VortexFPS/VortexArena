// legacy-server matrix: every map in the game data, in every game mode the server program will play
// on it, booted and run with bots - one line per cell, saying what faulted, which builtins nothing
// implements, what the VM warned about, whether the bots moved and scored, and what a frame cost.
//
// A game mode is chosen the way a server operator chooses it: its cvar (g_dm, g_ctf, ...) is set
// before the level starts, and the program's MapInfo_LoadMapSettings either plays that mode or - when
// the map does not support it - switches to one the map does. The mode a cell ended up in is read
// back from the same cvars, so "unsupported" is the program's verdict, not this tool's reading of a
// .mapinfo file.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    // common/gametypes/gametype/*/*.qh: the short name (what "gametype" and a .mapinfo use) and the cvar.
    private static readonly (string Short, string Cvar)[] GameModes =
    {
        ("dm", "g_dm"), ("tdm", "g_tdm"), ("ctf", "g_ctf"), ("ca", "g_ca"), ("ft", "g_freezetag"), ("kh", "g_keyhunt"),
        ("dom", "g_domination"), ("lms", "g_lms"), ("ka", "g_keepaway"), ("tka", "g_tka"), ("cts", "g_cts"), ("rc", "g_race"),
        ("as", "g_assault"), ("ons", "g_onslaught"), ("nb", "g_nexball"), ("inv", "g_invasion"), ("mayhem", "g_mayhem"),
        ("tmayhem", "g_tmayhem"), ("duel", "g_duel"), ("surv", "g_survival"),
    };

    private sealed class Cell
    {
        public string Map = "", Mode = "", Played = "", Status = "", FirstFault = "";
        public int Faults, Bots, Edicts, WarningsTotal;
        public double BootSeconds, MeanFrameMs, WorstFrameMs, Simulated, MaxMoved, Frags, Score;
        public long Traces;
        public List<(int Number, string Name, long Count)> Unimplemented = new();
        public List<(string Text, int Count)> Warnings = new();
        public List<string> Prints = new();
    }

    private static int Matrix(Options o)
    {
        List<string> maps;
        using (SvEnvironment probe = new(o.Data, null))
        {
            // "maps/*.bsp" through the virtual filesystem, as the program's own map list is built
            // (MapInfo_Enumerate: search_begin("maps/*.bsp")); a wildcard does not cross a directory.
            maps = probe.Files.Find("maps/", ".bsp")
                .Where(p => p.Count(c => c == '/') == 1)
                .Select(p => p["maps/".Length..^".bsp".Length])
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (o.Maps is not null && o.Maps != "all")
        {
            HashSet<string> wanted = new(o.Maps.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            maps = maps.Where(wanted.Contains).ToList();
        }
        List<(string Short, string Cvar)> modes = GameModes.ToList();
        if (o.Modes is not null && o.Modes != "all")
        {
            HashSet<string> wanted = new(o.Modes.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            modes = modes.Where(m => wanted.Contains(m.Short)).ToList();
        }
        int bots = o.Bots >= 0 ? o.Bots : 4;
        Log($"legacy-server matrix: {maps.Count} maps x {modes.Count} modes, {o.Seconds} s of game time a cell, {bots} bots, {o.Parallel} at a time; data {Path.GetFullPath(o.Data)}");
        Log("maps: " + string.Join(" ", maps));
        Log("");
        Log("map                mode     status       faults unimpl warns  bots  moved   frags  score edicts  boot s  ms/frame  worst ms  notes");

        List<(string Map, (string Short, string Cvar) Mode)> work = new();
        foreach (string map in maps)
            foreach ((string Short, string Cvar) mode in modes) work.Add((map, mode));
        ConcurrentBag<Cell> cells = new();
        object gate = new();
        Stopwatch wall = Stopwatch.StartNew();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Parallel) }, item =>
        {
            Cell cell;
            try { cell = RunCell(o, item.Map, item.Mode.Short, item.Mode.Cvar, bots); }
            catch (Exception e)
            {
                // An exception out of the server is itself a finding: nothing a level does may throw.
                cell = new Cell { Map = item.Map, Mode = item.Mode.Short, Status = "EXCEPTION", FirstFault = e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() };
            }
            cells.Add(cell);
            if (cell.Status == "unsupported") return;
            lock (gate) Log(CellLine(cell));
        });

        List<Cell> all = cells.OrderBy(c => c.Map, StringComparer.OrdinalIgnoreCase).ThenBy(c => Array.FindIndex(GameModes, m => m.Short == c.Mode)).ToList();
        List<Cell> played = all.Where(c => c.Status != "unsupported").ToList();
        Log("");
        Log("==== sorted ====");
        Log("map                mode     status       faults unimpl warns  bots  moved   frags  score edicts  boot s  ms/frame  worst ms  notes");
        foreach (Cell c in played) Log(CellLine(c));
        Log("");
        Log("==== supported modes per map (the program's own verdict) ====");
        foreach (IGrouping<string, Cell> g in all.GroupBy(c => c.Map))
            Log($"{g.Key,-18} {string.Join(" ", g.Where(c => c.Status != "unsupported").Select(c => c.Mode))}");
        Log("");
        int clean = played.Count(c => c.Status == "clean");
        Log($"==== summary: {played.Count} cells played ({all.Count - played.Count} map/mode pairs the program does not support), {clean} clean, {played.Count - clean} not; {wall.Elapsed.TotalSeconds:0} s wall ====");
        foreach (IGrouping<string, Cell> g in played.Where(c => c.Status != "clean").GroupBy(c => c.Status)) Log($"{g.Key}: {string.Join(", ", g.Select(c => c.Map + "/" + c.Mode))}");
        Dictionary<(int, string), (long Calls, int Cells)> unimplemented = new();
        foreach (Cell c in played)
            foreach ((int number, string name, long count) in c.Unimplemented)
            {
                (long calls, int n) = unimplemented.GetValueOrDefault((number, name));
                unimplemented[(number, name)] = (calls + count, n + 1);
            }
        Log(unimplemented.Count == 0 ? "unimplemented builtins called: none" : "unimplemented builtins called:");
        foreach (((int number, string name), (long calls, int n)) in unimplemented.OrderByDescending(p => p.Value.Calls)) Log($"  #{number} {name}: {calls} calls in {n} cells");
        Dictionary<string, (int Count, int Cells, string Example)> warnings = new(StringComparer.Ordinal);
        foreach (Cell c in played)
            foreach ((string text, int count) in c.Warnings)
            {
                (int total, int n, string example) = warnings.GetValueOrDefault(text, (0, 0, c.Map + "/" + c.Mode));
                warnings[text] = (total + count, n + 1, example);
            }
        Log($"VM warnings: {warnings.Count} distinct");
        foreach ((string text, (int total, int n, string example)) in warnings.OrderByDescending(p => p.Value.Count).Take(40)) Log($"  {total}x in {n} cells (e.g. {example}): {Printable(text, 260)}");
        Dictionary<string, (int Cells, string Example)> faults = new(StringComparer.Ordinal);
        foreach (Cell c in played.Where(c => c.FirstFault.Length > 0))
        {
            string key = c.FirstFault.Length > 200 ? c.FirstFault[..200] : c.FirstFault;
            (int n, string example) = faults.GetValueOrDefault(key, (0, c.Map + "/" + c.Mode));
            faults[key] = (n + 1, example);
        }
        Log($"distinct first faults: {faults.Count}");
        foreach ((string text, (int n, string example)) in faults.OrderByDescending(p => p.Value.Cells)) Log($"  {n} cells (e.g. {example}): {Printable(text, 900)}");
        Dictionary<string, (int Cells, string Example)> prints = new(StringComparer.Ordinal);
        foreach (Cell c in played)
            foreach (string line in c.Prints.Distinct())
            {
                (int n, string example) = prints.GetValueOrDefault(line, (0, c.Map + "/" + c.Mode));
                prints[line] = (n + 1, example);
            }
        Log($"program warnings and errors printed (LOG_WARN / LOG_SEVERE / engine complaints): {prints.Count} distinct");
        foreach ((string text, (int n, string example)) in prints.OrderByDescending(p => p.Value.Cells).Take(120)) Log($"  {n} cells (e.g. {example}): {Printable(text, 300)}");
        Log($"RESULT: {(clean == played.Count ? "OK" : "NOT CLEAN")} - {clean} of {played.Count} cells clean");
        return clean == played.Count ? 0 : 1;
    }

    private static string CellLine(Cell c) => string.Create(CultureInfo.InvariantCulture,
        $"{c.Map,-18} {c.Mode,-8} {c.Status,-12} {c.Faults,6} {c.Unimplemented.Count,6} {c.WarningsTotal,5} {c.Bots,5} {c.MaxMoved,6:0} {c.Frags,7:0} {c.Score,6:0} {c.Edicts,6} {c.BootSeconds,7:0.0} {c.MeanFrameMs,9:0.000} {c.WorstFrameMs,9:0.0}  ") +
        (c.FirstFault.Length > 0 ? Printable(c.FirstFault, 220) : c.Unimplemented.Count > 0 ? string.Join(" ", c.Unimplemented.Select(u => $"#{u.Number} {u.Name} x{u.Count}")) : "");

    private static Cell RunCell(Options o, string map, string mode, string cvar, int bots)
    {
        Cell cell = new() { Map = map, Mode = mode };
        StringBuilder line = new();
        Dictionary<string, int> warnings = new(StringComparer.Ordinal);
        using SvEnvironment env = new(o.Data, null,
            print: text =>
            {
                foreach (char ch in text)
                {
                    if (ch != '\n') { if (line.Length < 400) line.Append(ch); continue; }
                    string s = line.ToString();
                    line.Clear();
                    // What the program itself flags, with the coordinates and names taken out so that
                    // the same complaint on two maps is one line of the summary.
                    if (cell.Prints.Count < 200 && (s.Contains("WARNING", StringComparison.Ordinal) || s.Contains("ERROR", StringComparison.Ordinal) || s.Contains("SEVERE", StringComparison.Ordinal) || s.Contains("FATAL", StringComparison.Ordinal)))
                        cell.Prints.Add(Normalise(s));
                }
            },
            warning: text =>
            {
                text = text.TrimEnd();
                if (warnings.Count < 500 || warnings.ContainsKey(text)) warnings[text] = warnings.GetValueOrDefault(text) + 1;
            });
        env.SetCvar("bot_number", bots.ToString(CultureInfo.InvariantCulture));
        env.SetCvar("bot_join_empty", "1");
        env.SetCvar("sv_public", "0");
        env.SetCvar("g_warmup", "0");
        foreach ((string name, string value) in o.Sets) env.SetCvar(name, value);
        // MapInfo_SwitchGameType's own form: the wanted mode's cvar 1, every other 0.
        foreach ((string _, string other) in GameModes) env.SetCvar(other, other == cvar ? "1" : "0");

        Stopwatch wall = Stopwatch.StartNew();
        List<string> engine = new();
        SvqcHost? host = env.StartLevel(map, new SvqcHostOptions { MaxClients = o.MaxClients > 0 ? o.MaxClients : env.MaxPlayers, KeepRunningAfterFault = true, RandomSeed = o.Seed ?? 1 },
            print: text => { if (engine.Count < 50) engine.Add(text.TrimEnd('\n')); });
        cell.BootSeconds = wall.Elapsed.TotalSeconds;
        if (host is null)
        {
            cell.Status = "NO LEVEL";
            cell.FirstFault = string.Join(" | ", engine.TakeLast(3));
            return cell;
        }
        using SvqcHost _ = host;
        host.ExecuteCommands();
        // Which mode did the program settle on?
        string played = "?";
        foreach ((string s, string c) in GameModes) if (env.Cvars.Has(c) && env.Cvars.GetFloat(c) != 0) { played = s; break; }
        cell.Played = played;
        if (played != mode && host.FaultCount == 0)
        {
            cell.Status = "unsupported";
            return cell;
        }

        int totalFrags = host.Vm.FindField("totalfrags")?.Offset ?? -1;
        double ticRate = env.Cvars.GetFloat("sys_ticrate") > 0 ? env.Cvars.GetFloat("sys_ticrate") : 1.0 / 72;
        Dictionary<int, (QcVector Last, double Distance)> travelled = new();
        double start = host.Time, worst = 0;
        long frames = 0;
        wall.Restart();
        while (host.Time - start < o.Seconds && host.FaultCount < 50)
        {
            host.RealTime += ticRate;
            long t0 = Stopwatch.GetTimestamp();
            host.RunFrame(ticRate);
            host.ExecuteCommands();
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            // The first frames carry one-off work (waypoint loading, model bounds on first use).
            if (frames > 64 && ms > worst) worst = ms;
            frames++;
            foreach (SvClient client in host.Clients)
            {
                if (!client.Active) continue;
                QcVector origin = host.Vm.FieldVector(client.Edict, host.F.Origin);
                if (travelled.TryGetValue(client.Edict, out (QcVector Last, double Distance) t))
                {
                    double dx = origin.X - t.Last.X, dy = origin.Y - t.Last.Y, dz = origin.Z - t.Last.Z, step = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    travelled[client.Edict] = (origin, t.Distance + (step < 64 ? step : 0));   // a respawn is a teleport, not travel
                }
                else travelled[client.Edict] = (origin, 0);
            }
            // A level change asked for inside the minute (a match that ended) ends the cell: this mode
            // runs one level. The lifecycle mode is what follows a change through.
            if (host.PendingMap is not null) break;
        }
        cell.Simulated = host.Time - start;
        cell.MeanFrameMs = wall.Elapsed.TotalMilliseconds / Math.Max(1, frames);
        cell.WorstFrameMs = worst;
        cell.Faults = host.FaultCount;
        cell.Edicts = host.Vm.NumEdicts;
        cell.Traces = host.World.Traces;
        cell.Bots = host.Clients.Count(c => c.Active);
        cell.MaxMoved = travelled.Count == 0 ? 0 : travelled.Values.Max(t => t.Distance);
        foreach (SvClient client in host.Clients)
        {
            if (!client.Active) continue;
            if (totalFrags >= 0) cell.Frags += host.Vm.FieldFloat(client.Edict, totalFrags);
            cell.Score += Math.Abs(host.Vm.FieldFloat(client.Edict, host.F.Frags));
        }
        cell.Unimplemented = host.UnimplementedBuiltins.Select(p => (p.Key.Number, p.Key.Name, p.Value)).OrderByDescending(u => u.Value).ToList();
        cell.Warnings = warnings.Select(p => (Normalise(p.Key), p.Value)).GroupBy(p => p.Item1).Select(g => (g.Key, g.Sum(p => p.Value))).ToList();
        cell.WarningsTotal = warnings.Values.Sum();
        if (host.Faults.Count > 0) cell.FirstFault = host.Faults[0].EntryPoint + ": " + host.Faults[0].Message;
        cell.Status = cell.Faults > 0 ? "FAULT"
            : cell.Unimplemented.Count > 0 ? "UNIMPLEMENTED"
            : cell.WarningsTotal > 0 ? "WARNINGS"
            : cell.Bots == 0 ? "NO BOTS"
            : cell.MaxMoved < 100 ? "BOTS IDLE"
            : "clean";
        return cell;
    }

    // Numbers out of a message, so that one complaint about two entities is one line.
    private static string Normalise(string text)
    {
        StringBuilder sb = new(text.Length);
        bool inNumber = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c) || (inNumber && c == '.'))
            {
                if (!inNumber) sb.Append('#');
                inNumber = true;
                continue;
            }
            inNumber = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}

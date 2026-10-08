// legacy-server probe: what the server program is (the facts ProgsFile.Load establishes), which
// engine builtins it declares and calls, and which of them this engine implements. The engine's
// table is Base/darkplaces/svvm_cmds.c vm_sv_builtins[].
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VortexArena.Legacy.Protocol;
using VortexArena.Legacy.Server;
using VortexArena.QuakeC;

namespace VortexArena.Tools.LegacyServer;

internal static partial class Program
{
    // The groups the server's own builtins fall into, by number (svvm_cmds.c).
    private static string Group(int n) => n switch
    {
        2 or 3 or 4 or 400 or 443 or 451 or 452 => "placement, models and tags",
        16 or 64 or 90 or 41 or 240 or 17 or 44 or 92 or 567 => "collision queries",
        22 or 566 => "area queries",
        32 or 34 or 40 or 67 => "monster movement",
        8 or 74 or 483 or 533 or 534 => "sound",
        19 or 20 or 75 or 76 or 35 or 69 or 335 => "precaches, light styles, static entities",
        21 or 23 or 24 or 73 or 440 or 453 or 454 or 455 or 78 or 232 or 401 or 531 => "clients",
        >= 52 and <= 59 or 456 or 501 => "message writing",
        48 or 404 or 336 or 337 or (>= 405 and <= 433) or 457 => "particles and temp entities",
        >= 263 and <= 277 => "skeletons and frames",
        >= 434 and <= 439 or 486 or 628 or 629 => "surface queries",
        70 or 352 or 513 or 624 => "engine and console",
        >= 540 and <= 542 => "ODE physics",
        _ => "other",
    };

    private static int Probe(Options o)
    {
        Dictionary<string, int> warnings = new(StringComparer.Ordinal);
        using SvEnvironment? env = Environment(o, warnings, new StringBuilder());
        if (env is null) return 1;
        string progsName = env.Cvars.GetString("sv_progs") is { Length: > 0 } name ? name : "progs.dat";
        byte[]? data = env.Services.ReadFile(progsName);
        if (data is null)
        {
            Log($"RESULT: FAILED ({progsName} is not in the game data)");
            return 1;
        }
        Log($"legacy-server probe of {progsName}, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, data {Path.GetFullPath(o.Data)}");

        // The raw header and opcode histogram, read before the loader gets a say: if the file used an
        // opcode the loader refuses, this is where it would be named.
        int version = BinaryPrimitives.ReadInt32LittleEndian(data), headerCrc = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int ofsStatements = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)), numStatements = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        SortedDictionary<int, int> opcodes = new();
        for (int i = 0; i < numStatements && ofsStatements + i * 8 + 2 <= data.Length; i++)
        {
            int op = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ofsStatements + i * 8));
            opcodes[op] = opcodes.GetValueOrDefault(op) + 1;
        }
        Log($"file: {data.Length} bytes, CRC-16 {Crc16.Block(data)}, version {version}, header checksum {headerCrc}");
        Log($"opcodes: {opcodes.Count} distinct, highest {opcodes.Keys.Max()}; above 65 (the classic set): " +
            (opcodes.Keys.Any(k => k > 65) ? string.Join(", ", opcodes.Where(p => p.Key > 65).Select(p => $"{p.Key} x{p.Value}")) : "none") +
            $"; of 0..65 unused: {string.Join(", ", Enumerable.Range(0, 66).Where(k => !opcodes.ContainsKey(k)))}");

        ProgsFile progs;
        try { progs = ProgsFile.Load(data); }
        catch (ProgsFormatException e)
        {
            Log("RESULT: FAILED (ProgsFile.Load refused the file: " + e.Message + ")");
            return 1;
        }
        int builtinDecls = progs.Functions.Count(f => f.IsBuiltin);
        Log($"ProgsFile.Load: {progs.Statements.Length} statements, {progs.Functions.Length} functions ({progs.Functions.Length - builtinDecls} QuakeC, {builtinDecls} builtin declarations), " +
            $"{progs.NumGlobals} globals ({progs.GlobalDefs.Length} definitions), {progs.EntityFields} entity cells ({progs.FieldDefs.Length} field definitions), " +
            $"{progs.Strings.Length} bytes of strings, {progs.GlobalDefs.Count(d => d.Name.StartsWith("autocvar_", StringComparison.Ordinal))} autocvar globals, " +
            $"largest function's locals {progs.Functions.Where(f => !f.IsBuiltin).Max(f => f.Locals)} cells");

        // Builtins declared, and how many call sites name each (a CALL whose function operand is a
        // constant holding a builtin's function index).
        Dictionary<int, string> declared = new();
        Dictionary<int, int> functionToBuiltin = new();
        for (int i = 0; i < progs.Functions.Length; i++)
        {
            QcFunction f = progs.Functions[i];
            if (!f.IsBuiltin) continue;
            declared.TryAdd(f.BuiltinNumber, f.Name);
            functionToBuiltin[i] = f.BuiltinNumber;
        }
        Dictionary<int, int> callSites = new();
        int indirectCalls = 0;
        foreach (QcStatement s in progs.Statements)
        {
            if (s.Op < (int)QcOp.Call0 || s.Op > (int)QcOp.Call8) continue;
            int value = progs.Globals[s.A];
            if (functionToBuiltin.TryGetValue(value, out int number)) callSites[number] = callSites.GetValueOrDefault(number) + 1;
            else if (value <= 0 || value >= progs.Functions.Length) indirectCalls++;
        }
        Log($"builtins: {declared.Count} numbers declared, {callSites.Count} with a direct call site; {indirectCalls} call sites through a variable");

        SvqcHost host;
        try { host = new SvqcHost(data, env.Services, env.Cvars, env.Interpreter, env.Files); }
        catch (SvqcLoadException e)
        {
            Log("RESULT: FAILED (" + e.Message + ")");
            return 1;
        }
        using (host)
        {
            Log($"host: {host.AutocvarsBound} autocvars bound; client program {(host.CsqcProgData is null ? "none" : $"{host.CsqcProgName} {host.CsqcProgSize} bytes crc {host.CsqcProgCrc}, {host.CsqcProgDataDeflated!.Length} deflated")}");
            HashSet<int> server = host.RegisteredBuiltins.Select(b => b.Number).ToHashSet();
            List<int> implemented = declared.Keys.Where(host.Vm.HasBuiltin).OrderBy(n => n).ToList();
            List<int> missing = declared.Keys.Where(n => !host.Vm.HasBuiltin(n)).OrderBy(n => n).ToList();
            Log($"implemented: {implemented.Count} of {declared.Count} declared ({implemented.Count(n => !server.Contains(n))} engine-independent from VortexArena.QuakeC, {implemented.Count(server.Contains)} server builtins)");
            Log("server builtins implemented (number name): " + string.Join(", ", host.RegisteredBuiltins.OrderBy(b => b.Number).Select(b => $"{b.Number} {b.Name}")));
            Log($"not implemented: {missing.Count} declared ({missing.Count(n => callSites.ContainsKey(n))} of them with direct call sites), by group:");
            foreach (IGrouping<string, int> group in missing.GroupBy(Group).OrderBy(g => g.Key))
                Log($"  {group.Key}: " + string.Join(", ", group.Select(n => $"#{n} {declared[n]}({callSites.GetValueOrDefault(n)})")));
        }
        Log("RESULT: OK");
        return 0;
    }

    private static int Serve(Options o) => ServeImpl(o);
}

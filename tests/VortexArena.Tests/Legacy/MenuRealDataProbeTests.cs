using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Menu;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The measurement behind the menu-program host (planning/specs/legacy-compat.md, LC-12): what the
/// stock Xonotic menu.dat is made of, which engine builtins it declares and which of them it calls
/// while starting up and drawing its first frames against REAL Xonotic data, and which of those
/// nothing implements. The report goes to _scratch/menu-probe.txt; the assertions are the ones that
/// make it a regression test - start-up completes with no fault and no unimplemented builtin.
///
/// Needs the upstream reference checkout (../Base); returns early without it.
/// </summary>
public class MenuRealDataProbeTests
{
    private readonly ITestOutputHelper _output;
    public MenuRealDataProbeTests(ITestOutputHelper output) => _output = output;

    internal static string MenuDat => Path.Combine(TestPaths.BaseCorePk3Dir, "menu.dat");

    /// <summary>A console with Xonotic's data mounted and its start-up scripts run; nothing is read from or written to disk outside <paramref name="userData"/>.</summary>
    internal static LegacyConsole NewConsole(VirtualFileSystem vfs, string? userData, StringBuilder? printed = null)
    {
        Assert.True(vfs.MountGameDir(TestPaths.BaseData));
        LegacyConsole console = new(vfs, userData, text => printed?.Append(text));
        Assert.True(console.LoadConfig(), "default.cfg was not found in the Xonotic data");
        return console;
    }

    [Fact]
    public void MenuInit_AgainstRealXonoticData()
    {
        if (!File.Exists(MenuDat)) return;
        byte[] file = File.ReadAllBytes(MenuDat);
        ProgsFile progs = ProgsFile.Load(file);
        StringBuilder report = new();

        // ---- what the file is --------------------------------------------------------------------
        int[] opcodes = new int[65536];
        foreach (QcStatement statement in progs.Statements) opcodes[statement.Op]++;
        int distinct = opcodes.Count(c => c != 0), highest = Array.FindLastIndex(opcodes, c => c != 0);
        long above65 = 0;
        for (int op = 66; op < opcodes.Length; op++) above65 += opcodes[op];
        QcFunction[] declared = progs.Functions.Where(f => f.IsBuiltin).ToArray();
        int[] declaredNumbers = declared.Select(f => f.BuiltinNumber).Distinct().OrderBy(n => n).ToArray();
        int indirect = 0;
        HashSet<int> functionCells = new();
        foreach (QcDef def in progs.GlobalDefs)
            if (def.Type == QcType.Function) functionCells.Add(def.Offset);
        // A direct call names a global that holds a function constant and is never stored to; counting
        // the calls through any other cell needs data flow, so only the total is given.
        foreach (QcStatement statement in progs.Statements)
            if (statement.Op is >= (int)QcOp.Call0 and <= (int)QcOp.Call8) indirect++;

        report.AppendLine("menu.dat probe - " + MenuDat);
        report.AppendLine($"file: {file.Length:N0} bytes, version 6, header CRC {progs.HeaderCrc}");
        report.AppendLine($"statements: {progs.Statements.Length:N0}; distinct opcodes {distinct}, highest {highest}; statements with an opcode above 65: {above65}");
        report.AppendLine("opcodes of the classic set that do not occur: " + string.Join(", ", Enumerable.Range(0, 66).Where(op => opcodes[op] == 0).Select(op => $"{op} ({(QcOp)op})")));
        report.AppendLine($"functions: {progs.Functions.Length:N0} ({progs.Functions.Length - declared.Length:N0} QuakeC, {declared.Length} builtin declarations naming {declaredNumbers.Length} distinct numbers); call statements {indirect:N0}");
        report.AppendLine($"globals: {progs.Globals.Length:N0} cells, {progs.GlobalDefs.Length:N0} definitions, {progs.GlobalDefs.Count(d => d.Name.StartsWith("autocvar_", StringComparison.Ordinal) && !(d.Name.Length > 2 && d.Name[^2] == '_' && d.Name[^1] is 'x' or 'y' or 'z')):N0} autocvar globals");
        report.AppendLine($"entity fields: {progs.EntityFields:N0} cells per entity, {progs.FieldDefs.Length:N0} definitions");
        report.AppendLine($"string table: {progs.Strings.Length:N0} bytes; largest function's locals: {progs.Functions.Max(f => f.Locals)} cells");
        report.AppendLine();

        // ---- run it ------------------------------------------------------------------------------
        using VirtualFileSystem vfs = new();
        string userData = Path.Combine(Path.GetTempPath(), "va-menuprobe-" + Guid.NewGuid().ToString("N"));
        StringBuilder printed = new();
        List<string> warnings = new();
        try
        {
            LegacyConsole console = NewConsole(vfs, userData, printed);
            HeadlessMenuDraw draw = new(vfs);
            console.Interpreter.RegisterCommand("loadfont", argv => { if (argv.Count >= 3) draw.Fonts.Load(argv[1], argv[2], -1, 1, 0); });
            List<string> unknown = new();
            console.Interpreter.UnknownCommandHandler = (name, argv) => unknown.Add(string.Join(' ', argv));
            MenuHost host = new(file, console, draw, new MenuHostOptions { VideoSize = () => (1280, 720), WindowMouse = () => (640, 360) },
                text => printed.Append(text), warnings.Add);

            // ---- the builtin table against what the file declares -----------------------------------
            Dictionary<int, string> tableNames = MenuBuiltinTable.Entries.ToDictionary(e => e.Number, e => e.Function);
            string NameOf(int number) => declared.First(f => f.BuiltinNumber == number).Name;
            report.AppendLine($"builtin numbers declared by the program: {declaredNumbers.Length}");
            foreach (IGrouping<string, int> group in declaredNumbers.GroupBy(n => host.BuiltinSources.GetValueOrDefault(n, tableNames.ContainsKey(n) ? "NOT IMPLEMENTED" : "NULL in vm_m_builtins[]")).OrderBy(g => g.Key))
            {
                string what = group.Key switch
                {
                    "core" => "already implemented: QcCoreBuiltins (shared with the client and server tables)",
                    "strings" => "already implemented: QcStringBuiltins (shared)",
                    "draw" => "already implemented for the client program, now shared: LegacyDrawBuiltins",
                    "menu" => "mvm_cmds.c / prvm_cmds.c functions no shared class had: the work queue, implemented in Menu/MenuHost.Builtins*.cs",
                    _ => group.Key,
                };
                report.AppendLine($"  {group.Count(),3}  {what}");
                report.AppendLine("       " + string.Join(", ", group.Select(n => $"#{n} {NameOf(n)}")));
            }
            report.AppendLine();

            bool initialized = host.Init();
            console.Execute(1);
            report.AppendLine(initialized ? "RESULT: m_init ran to completion." : "RESULT: m_init ended in a fault:\n" + host.Faults[0].Message);
            report.AppendLine($"afterwards: {host.Vm.NumEdicts:N0} entities, {host.Vm.ZonedStringCount:N0} zoned strings, {host.AutocvarsBound:N0} autocvars bound, key_dest {host.KeyDest}, {warnings.Count} VM warnings");
            AppendCalls(report, host, "builtins called by m_init");

            // The first frames: m_draw runs m_init_delayed (the skin, every dialog) on the first one.
            int frames = 0, commands = 0;
            for (; frames < 60 && host.FaultCount == 0; frames++)
            {
                // cl_screen.c: "scr_menuforcewhiledisconnected && key_dest == key_game && cls.state ==
                // ca_disconnected" opens the menu from the third frame on. Nothing else would: m_init hides it.
                if (frames >= 2 && host.KeyDest == MenuKeyDest.Game) host.ToggleMenu(1);
                draw.List.Clear();
                host.DrawFrame(1280, 720);
                console.Execute(2 + frames * 0.05);
                commands = Math.Max(commands, draw.List.Count);
            }
            report.AppendLine();
            report.AppendLine($"then {frames} frames of m_draw(1280, 720): {host.FaultCount} faults, {host.Vm.NumEdicts:N0} entities, {host.Vm.ZonedStringCount:N0} zoned strings, key_dest {host.KeyDest}, up to {commands} draw commands a frame, {draw.Pictures.Count} pictures cached");
            if (host.FaultCount != 0) report.AppendLine("FAULT: " + host.Faults[0].EntryPoint + ": " + host.Faults[0].Message);
            AppendCalls(report, host, "builtins called so far");

            report.AppendLine();
            report.AppendLine($"unimplemented builtins CALLED ({host.UnimplementedBuiltins.Count} distinct, {host.UnimplementedBuiltins.Values.Sum()} calls):");
            foreach (((int number, string name), long count) in host.UnimplementedBuiltins.OrderByDescending(m => m.Value).ThenBy(m => m.Key.Number))
                report.AppendLine($"  {count,7}  #{number,-4} {name}");
            report.AppendLine($"C functions of vm_m_builtins[] with no implementation: {host.MissingBuiltins.Count}" +
                (host.MissingBuiltins.Count == 0 ? "" : " - " + string.Join(", ", host.MissingBuiltins.Select(m => $"#{m.Number} {m.Function}"))));
            report.AppendLine();
            report.AppendLine($"console commands the menu issued that nothing here runs ({unknown.Distinct().Count()} distinct): " + string.Join(" | ", unknown.Distinct().Take(40)));
            report.AppendLine();
            report.AppendLine($"VM warnings, first 15 distinct of {warnings.Count}:");
            foreach (string warning in warnings.Distinct().Take(15)) report.AppendLine("  " + warning.TrimEnd());
            report.AppendLine();
            string[] lines = printed.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            report.AppendLine($"console output, first 25 of {lines.Length} lines:");
            foreach (string line in lines.Take(25)) report.AppendLine("  " + line);

            string text = report.ToString();
            _output.WriteLine(text);
            string scratch = Path.Combine(TestPaths.RepoRoot, "_scratch");
            Directory.CreateDirectory(scratch);
            File.WriteAllText(Path.Combine(scratch, "menu-probe.txt"), text);

            Assert.Equal(0, above65);
            Assert.True(initialized, host.Faults.Count > 0 ? host.Faults[0].Message : "m_init did not run");
            Assert.True(host.FaultCount == 0, host.Faults.Count > 0 ? host.Faults[0].EntryPoint + ": " + host.Faults[0].Message : "");
            Assert.True(host.UnimplementedBuiltins.Count == 0, "unimplemented builtins were called: " + string.Join(", ", host.UnimplementedBuiltins.Keys));
            Assert.Empty(host.MissingBuiltins);
            Assert.True(commands > 0, "the menu drew nothing");
            host.Shutdown();
        }
        finally
        {
            if (Directory.Exists(userData)) Directory.Delete(userData, recursive: true);
        }
    }

    private static void AppendCalls(StringBuilder report, MenuHost host, string title)
    {
        (int Number, string Function, long Calls)[] calls = host.CallCounts.ToArray();
        report.AppendLine($"{title}: {calls.Length} distinct, {calls.Sum(c => c.Calls):N0} calls");
        foreach (IGrouping<string, (int Number, string Function, long Calls)> group in calls.GroupBy(c => host.BuiltinSources[c.Number]).OrderBy(g => g.Key))
            report.AppendLine($"  {group.Key,-7} {group.Sum(c => c.Calls),9:N0} calls: " +
                string.Join(", ", group.OrderByDescending(c => c.Calls).Select(c => $"{c.Function[3..]} {c.Calls}")));
    }
}

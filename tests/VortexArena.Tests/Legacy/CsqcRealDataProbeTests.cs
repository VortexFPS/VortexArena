using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// Runs the stock Xonotic client program's start-up against the REAL host services - Xonotic's own
/// data mounted in the virtual filesystem, its default configuration executed into the cvar store -
/// and records every engine builtin the program calls that nothing implements yet. That table is the
/// work queue for legacy mode (planning/specs/legacy-compat.md section 9): it is measured, not guessed.
///
/// The sibling probe in QuakeC/CsqcInitProbeTests.cs uses an empty fake host, so every file check
/// fails there and the program takes its "missing asset" branches; this one takes the real ones.
/// Needs the upstream reference checkout (../Base); returns early without it.
/// </summary>
public class CsqcRealDataProbeTests
{
    private readonly ITestOutputHelper _output;
    public CsqcRealDataProbeTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    [Fact]
    public void CsqcInit_AgainstRealXonoticData()
    {
        string repo = RepoRoot();
        string baseData = Path.GetFullPath(Path.Combine(repo, "..", "Base", "data"));
        string csprogs = Path.Combine(baseData, "xonotic-data.pk3dir", "csprogs.dat");
        if (!File.Exists(csprogs)) return;

        using VirtualFileSystem vfs = new();
        Assert.True(vfs.MountGameDir(baseData));

        CvarService cvars = new();
        ConfigInterpreter interp = new(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null);
        // What the engine itself registers and the program checks before anything else.
        cvars.Register("pr_checkextension", "1");
        cvars.Register("utf8_enable", "1");
        cvars.Register("developer", "0");
        interp.ExecuteFile("default.cfg");

        string writeRoot = Path.Combine(Path.GetTempPath(), "va-csqcprobe-" + Guid.NewGuid().ToString("N"));
        StringBuilder printed = new();
        List<string> warnings = new();
        LegacyQcHost host = new(cvars, vfs) { WriteRoot = writeRoot, PrintSink = s => printed.Append(s), WarningSink = warnings.Add };

        try
        {
            ProgsFile progs = ProgsFile.Load(File.ReadAllBytes(csprogs));
            QcVm vm = new(progs, "client");
            using QcCoreBuiltins core = new(vm, host);
            core.Register();
            QcStringBuiltins strings = new(vm, host) { OpenFile = core.FileStream };
            strings.Register();

            Dictionary<(int Number, string Name), int> missing = new();
            vm.UnknownBuiltin = (_, number, name) => missing[(number, name)] = missing.GetValueOrDefault((number, name)) + 1;

            int cvarsBefore = cvars.Names.Count;
            int bound = new QcAutocvars(vm, host).Bind();

            // What CL_VM_Init sets before the call (Base/darkplaces/csprogs.c:1133-1161).
            const string map = "stormkeep";
            vm.GlobalInt(vm.FindGlobal("mapname")!.Offset) = vm.EngineString(map);
            vm.GlobalFloat(vm.FindGlobal("player_localentnum")!.Offset) = 1;
            vm.FieldFloat(0, vm.FindField("solid")!.Offset) = 4; // SOLID_BSP
            vm.FieldFloat(0, vm.FindField("modelindex")!.Offset) = 1;
            vm.FieldInt(0, vm.FindField("model")!.Offset) = vm.EngineString($"maps/{map}.bsp");
            vm.SetArgFloat(0, 1);
            vm.SetArgInt(1, vm.EngineString("DarkPlaces Xonotic"));
            vm.SetArgFloat(2, 1);

            QcRuntimeException? fault = null;
            try { vm.Execute(vm.FindFunction("CSQC_Init"), 3); }
            catch (QcRuntimeException e) { fault = e; }

            IReadOnlyList<string> commands = host.TakePendingCommands();
            StringBuilder report = new();
            report.AppendLine("CSQC_Init against real Xonotic data - " + csprogs);
            report.AppendLine($"default.cfg: {interp.FilesExecuted} files executed, {interp.FilesMissing} missing, {cvarsBefore} cvars, {interp.AliasesDefined} aliases, {interp.UnknownCommands} unknown commands");
            report.AppendLine($"autocvars bound: {bound} ({cvars.Names.Count - cvarsBefore} created from their globals; the rest already existed in the configuration)");
            report.AppendLine(fault is null ? "RESULT: CSQC_Init ran to completion." : "RESULT: CSQC_Init ended in a fault:\n" + fault.Message);
            report.AppendLine($"afterwards: {vm.NumEdicts} entities, {vm.ZonedStringCount} zoned strings, {commands.Count} console commands queued, {warnings.Count} VM warnings");
            report.AppendLine();
            report.AppendLine($"unimplemented builtins CALLED ({missing.Count} distinct, {missing.Values.Sum()} calls):");
            foreach (((int number, string name), int count) in missing.OrderByDescending(m => m.Value).ThenBy(m => m.Key.Number))
                report.AppendLine($"  {count,7}  #{number,-4} {name}");
            report.AppendLine();
            report.AppendLine($"VM warnings, first 15 distinct of {warnings.Count}:");
            foreach (string warning in warnings.Distinct().Take(15)) report.AppendLine("  " + warning.TrimEnd());
            report.AppendLine();
            string[] lines = printed.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            report.AppendLine($"console output, first 25 of {lines.Length} lines:");
            foreach (string line in lines.Take(25)) report.AppendLine("  " + line);

            string text = report.ToString();
            _output.WriteLine(text);
            Directory.CreateDirectory(Path.Combine(repo, "_scratch"));
            File.WriteAllText(Path.Combine(repo, "_scratch", "csqc-init-probe-realdata.txt"), text);

            // Start-up must get all the way through on the real data; a fault here is a regression in the
            // VM or in a builtin, and the message carries the QuakeC stack that locates it.
            Assert.True(fault is null, fault?.Message);
            // The stock configuration really was loaded: the program found its cvars rather than inventing them.
            Assert.True(cvarsBefore > 3000, $"only {cvarsBefore} cvars after default.cfg");
        }
        finally
        {
            if (Directory.Exists(writeRoot)) Directory.Delete(writeRoot, recursive: true);
        }
    }
}

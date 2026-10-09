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
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// What a client program OTHER than the stock one asks of this engine: a modded community server sends its
/// own csprogs, built from a fork of Xonotic's code, and that program may call engine builtins the stock one
/// never does. The probe runs such a program - any file named by the environment variable
/// VORTEX_PROBE_CSPROGS, or dropped into _scratch/join/probe/ - the way a session does (the whole client
/// builtin set, Xonotic's default configuration, a real map), through CSQC_Init and a few hundred frames,
/// and reports every builtin it declares or calls that nothing implements, next to the stock program's.
/// It asserts only what must hold for ANY program: an unimplemented builtin is counted, never a fault of
/// the session. Without the Base checkout or a program to probe it does nothing.
/// </summary>
public class CustomProgramProbeTests
{
    private readonly ITestOutputHelper _output;
    public CustomProgramProbeTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private sealed record Probe(string Path, int Size, int Crc, int Functions, int Globals, int EntityFields, SortedSet<int> Declared, SortedDictionary<int, string> DeclaredNotImplemented,
        Dictionary<(int, string), long> CalledNotImplemented, bool InitOk, string? Fault, int Frames, int FramesFaulted, int Extended);

    private static Probe Run(string programPath, string baseData)
    {
        byte[] program = File.ReadAllBytes(programPath);
        using VirtualFileSystem vfs = new();
        Assert.True(vfs.MountGameDir(baseData));
        CvarService cvars = new();
        ConfigInterpreter interpreter = new(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null) { NestedReferences = true };
        CsqcEngineCvars.Register(cvars);
        cvars.Register("pr_checkextension", "1");
        cvars.Register("utf8_enable", "1");
        cvars.Register("developer", "0");
        interpreter.ExecuteFile("default.cfg");
        string writeRoot = Path.Combine(Path.GetTempPath(), "va-customprobe-" + Guid.NewGuid().ToString("N"));
        try
        {
            LegacyQcHost services = new(cvars, vfs) { WriteRoot = writeRoot };
            CsqcConsole console = new(interpreter, services);
            HeadlessLegacyPresentation presentation = new(vfs);
            CsqcClientState state = new();
            state.ApplyServerInfo(new DpServerInfo
            {
                Protocol = DpProtocol.ProtocolNumberDp7, MaxClients = 8, GameType = 1, WorldMessage = "probe",
                Models = new[] { "", "maps/stormkeep.bsp" }, Sounds = new[] { "" },
            });
            state.SetView(1);
            presentation.BeginLevel(state);

            // The program's own declarations first: every builtin number it names.
            ProgsFile file = ProgsFile.Load(program);
            SortedSet<int> declared = new();
            SortedDictionary<int, string> names = new();
            foreach (QcFunction function in file.Functions)
                if (function.IsBuiltin)
                {
                    declared.Add(function.BuiltinNumber);
                    names.TryAdd(function.BuiltinNumber, function.Name);
                }

            CsqcHost host = new(program, program.Length, Crc16.Block(program), services, console, presentation, state);
            SortedDictionary<int, string> notImplemented = new();
            foreach (int number in declared)
                if (!host.Vm.HasBuiltin(number)) notImplemented[number] = names[number];
            bool ok = host.Init();
            string? fault = host.FaultMessage;
            console.Execute();
            int frames = 0, faulted = 0;
            if (ok)
            {
                state.Signon = DpProtocol.Signons;
                for (int i = 0; i < 300; i++)
                {
                    state.Time = 1 + i / 60.0;
                    int before = host.FaultCount;
                    host.UpdateView(1280, 720, 1 / 60.0);
                    console.Execute();
                    frames++;
                    if (host.FaultCount != before)
                    {
                        faulted++;
                        fault ??= host.Faults[^1].Message;
                        host.ClearFaults();
                    }
                }
            }
            Probe result = new(programPath, program.Length, Crc16.Block(program), file.Functions.Length, file.Globals.Length, file.EntityFields, declared, notImplemented,
                new Dictionary<(int, string), long>(host.UnimplementedBuiltins), ok, fault, frames, faulted, 0);
            host.Shutdown();
            return result;
        }
        finally
        {
            if (Directory.Exists(writeRoot)) Directory.Delete(writeRoot, recursive: true);
        }
    }

    [Fact]
    public void A_Custom_Client_Program_Is_Run_And_What_It_Needs_Is_Reported()
    {
        string repo = RepoRoot();
        string baseData = Path.GetFullPath(Path.Combine(repo, "..", "Base", "data"));
        string stock = Path.Combine(baseData, "xonotic-data.pk3dir", "csprogs.dat");
        List<string> custom = new();
        if (Environment.GetEnvironmentVariable("VORTEX_PROBE_CSPROGS") is { Length: > 0 } named && File.Exists(named)) custom.Add(named);
        string dropped = Path.Combine(repo, "_scratch", "join", "probe");
        if (Directory.Exists(dropped)) custom.AddRange(Directory.GetFiles(dropped).Where(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
        if (!File.Exists(stock) || custom.Count == 0) return;

        Probe reference = Run(stock, baseData);
        StringBuilder report = new();
        void Describe(Probe p, Probe? against)
        {
            report.AppendLine($"== {p.Path}");
            report.AppendLine($"   {p.Size} bytes, CRC-16 {p.Crc}; {p.Functions} functions, {p.Globals} globals, entity size {p.EntityFields} cells; {p.Declared.Count} builtin numbers declared");
            report.AppendLine(p.InitOk ? $"   CSQC_Init ran to completion; {p.Frames} frames of CSQC_UpdateView, {p.FramesFaulted} faulted" : "   CSQC_Init FAULTED");
            if (p.Fault is not null) report.AppendLine("   first fault: " + p.Fault.Replace("\n", "\n      "));
            report.AppendLine($"   builtins DECLARED that nothing implements ({p.DeclaredNotImplemented.Count}): " +
                string.Join(", ", p.DeclaredNotImplemented.Select(kv => $"#{kv.Key} {kv.Value}")));
            report.AppendLine($"   unimplemented builtins CALLED during the run ({p.CalledNotImplemented.Count}): " +
                string.Join(", ", p.CalledNotImplemented.OrderByDescending(kv => kv.Value).Select(kv => $"#{kv.Key.Item1} {kv.Key.Item2} x{kv.Value}")));
            if (against is not null)
            {
                report.AppendLine("   declared here and not by the stock program: " +
                    string.Join(", ", p.Declared.Except(against.Declared).Select(n => "#" + n + (p.DeclaredNotImplemented.ContainsKey(n) ? " (NOT implemented)" : ""))));
                report.AppendLine("   declared by the stock program and not here: " + string.Join(", ", against.Declared.Except(p.Declared).Select(n => "#" + n)));
            }
            report.AppendLine();
        }
        Describe(reference, null);
        foreach (string path in custom)
        {
            Probe probe;
            try { probe = Run(path, baseData); }
            catch (CsqcLoadException e)
            {
                report.AppendLine($"== {path}\n   REFUSED at load: {e.Message}\n");
                continue;
            }
            Describe(probe, reference);
            // An unimplemented builtin is counted and answered with nothing; it is never what stops a program.
            Assert.DoesNotContain("unimplemented", probe.Fault ?? "", StringComparison.OrdinalIgnoreCase);
        }
        string text = report.ToString();
        _output.WriteLine(text);
        Directory.CreateDirectory(Path.Combine(repo, "_scratch", "join"));
        File.WriteAllText(Path.Combine(repo, "_scratch", "join", "custom-program-probe.txt"), text);
        Assert.True(reference.InitOk, reference.Fault);
    }
}

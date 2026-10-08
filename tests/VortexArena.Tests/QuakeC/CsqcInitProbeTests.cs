using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using VortexArena.QuakeC;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests.QuakeC;

/// <summary>
/// Runs the stock Xonotic client program's CSQC_Init the way DarkPlaces' CL_VM_Init does
/// (Base/darkplaces/csprogs.c:1133), with only the engine-independent builtins present, and records how
/// far it gets and which builtins it asked for that nothing implements. A measuring instrument, not a
/// pass/fail gate: the only assertion is that the VM comes out of it intact.
/// </summary>
public class CsqcInitProbeTests
{
    private readonly ITestOutputHelper _output;

    public CsqcInitProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CsqcInit_RunsAsFarAsTheAvailableBuiltinsAllow()
    {
        string repo = RepoRoot();
        string csprogs = Path.Combine(TestPaths.BaseCorePk3Dir, "csprogs.dat");
        // Built with a private --artifacts-path the test binary is outside the repo and TestPaths finds
        // nothing; the source file's own location still names the checkout.
        if (!File.Exists(csprogs)) csprogs = Path.GetFullPath(Path.Combine(repo, "..", "Base", "data", "xonotic-data.pk3dir", "csprogs.dat"));
        if (!File.Exists(csprogs)) return; // needs the upstream reference checkout (../Base)

        ProgsFile progs = ProgsFile.Load(File.ReadAllBytes(csprogs));
        QcVm vm = new(progs, "client");
        CoreTestHost host = new() { RealTime = 10 };
        host.Set("pr_checkextension", "1");
        host.Set("developer", "0");
        host.Set("utf8_enable", "1");
        host.Set("sv_entfields_noescapes", "wad");

        StringBuilder report = new();
        report.AppendLine("CSQC_Init probe - " + csprogs);
        report.AppendLine($"program: {progs.Statements.Length} statements, {progs.Functions.Length} functions, {progs.NumGlobals} globals, {progs.EntityFields} field cells");

        QcCoreBuiltins core = new(vm, host);
        foreach (string extension in DarkPlacesExtensions(csprogs)) core.Extensions.Add(extension);
        core.Register();
        report.AppendLine($"core builtins registered: {core.Registered.Count()}; extensions advertised: {core.Extensions.Count}");
        report.AppendLine("string builtins: " + RegisterStringBuiltins(vm, host));

        Dictionary<(int Number, string Name), int> missing = new();
        vm.UnknownBuiltin = (_, number, name) => missing[(number, name)] = missing.GetValueOrDefault((number, name)) + 1;

        QcAutocvars autocvars = new(vm, host);
        int cvarsBefore = host.Cvars.Count;
        int bound = autocvars.Bind();
        report.AppendLine($"autocvars bound: {bound} ({host.Cvars.Count - cvarsBefore} cvars created from their globals)");
        host.CvarMisses.Clear();

        // What CL_VM_Init sets before the call (csprogs.c:1133-1149).
        const string map = "stormkeep";
        SetGlobalFloat(vm, "time", 0);
        SetGlobalInt(vm, "self", 0);
        SetGlobalInt(vm, "mapname", vm.EngineString(map));
        SetGlobalFloat(vm, "player_localnum", 0);
        SetGlobalFloat(vm, "player_localentnum", 1);
        SetFieldInt(vm, "message", vm.EngineString("Stormkeep"));
        foreach (string field in new[] { "mins", "absmin" }) SetFieldVector(vm, field, new QcVector(-4096, -4096, -4096));
        foreach (string field in new[] { "maxs", "absmax" }) SetFieldVector(vm, field, new QcVector(4096, 4096, 4096));
        SetFieldFloat(vm, "solid", 4); // SOLID_BSP
        SetFieldFloat(vm, "modelindex", 1);
        SetFieldInt(vm, "model", vm.EngineString($"maps/{map}.bsp"));

        // CSQC_Init(float apilevel, string enginename, float engineversion) - csprogs.c:1154.
        vm.SetArgFloat(0, 1);
        vm.SetArgInt(1, vm.EngineString("DarkPlaces Xonotic"));
        vm.SetArgFloat(2, 1);

        int init = vm.FindFunction("CSQC_Init");
        Assert.True(init > 0);
        QcRuntimeException? fault = null;
        try { vm.Execute(init, 3); }
        catch (QcRuntimeException e) { fault = e; }

        report.AppendLine();
        report.AppendLine(fault is null ? "RESULT: CSQC_Init ran to completion." : "RESULT: CSQC_Init ended in a QcRuntimeException:");
        if (fault is not null) report.AppendLine(Indent(fault.Message));
        report.AppendLine($"state afterwards: {vm.NumEdicts} entities in use, {vm.ZonedStringCount} zoned strings, {host.Cvars.Count} cvars, " +
                          $"{host.Commands.Count} console commands issued, {host.Warnings.Count} VM warnings");

        report.AppendLine();
        report.AppendLine($"unimplemented builtins CALLED ({missing.Count} distinct, {missing.Values.Sum()} calls), by call count:");
        foreach (((int number, string name), int count) in missing.OrderByDescending(m => m.Value).ThenBy(m => m.Key.Number))
            report.AppendLine($"  {count,7}  #{number,-4} {name}");

        // Static view: every builtin the program declares that has no implementation here.
        List<QcFunction> declared = progs.Functions.Where(f => f.IsBuiltin && !vm.HasBuiltin(f.BuiltinNumber)).OrderBy(f => f.BuiltinNumber).ToList();
        report.AppendLine();
        report.AppendLine($"builtins the program DECLARES that nothing implements ({declared.Count} of {progs.Functions.Count(f => f.IsBuiltin)}):");
        report.AppendLine(Wrap(declared.Select(f => $"#{f.BuiltinNumber} {f.Name}")));

        report.AppendLine();
        report.AppendLine($"VM warnings ({host.Warnings.Count}), first 20 distinct:");
        foreach (string warning in host.Warnings.Distinct().Take(20)) report.AppendLine("  " + warning.TrimEnd());
        report.AppendLine();
        report.AppendLine($"console commands issued ({host.Commands.Count}), first 20:");
        foreach (string command in host.Commands.Take(20)) report.AppendLine("  " + command.TrimEnd().Replace("\n", "\\n"));
        report.AppendLine();
        // Cvars the program read that neither the fake host nor an autocvar global defines: engine cvars a
        // real host has to provide.
        List<KeyValuePair<string, int>> cvarMisses = host.CvarMisses.Where(m => !host.Cvars.ContainsKey(m.Key)).OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
        report.AppendLine($"cvars read that do not exist ({cvarMisses.Count}):");
        report.AppendLine(Wrap(cvarMisses.Select(m => m.Value > 1 ? $"{m.Key} x{m.Value}" : m.Key)));
        report.AppendLine();
        List<string> paths = host.PathsAsked.Distinct().ToList();
        report.AppendLine($"files and searches asked of the host ({host.PathsAsked.Count} requests, {paths.Count} distinct; the fake host has no files), first 15:");
        foreach (string path in paths.Take(15)) report.AppendLine("  " + path);
        report.AppendLine();
        string[] console = host.Printed.ToString().Split('\n');
        static bool MissingFile(string line) => line.Contains("Missing sound", StringComparison.Ordinal) || line.Contains("Missing model", StringComparison.Ordinal);
        report.AppendLine($"console output ({console.Length} lines; {console.Count(MissingFile)} are \"Missing sound\"/\"Missing model\" for files the fake host does not have and are left out), first 40 of the rest:");
        foreach (string line in console.Where(l => l.Length > 0 && !MissingFile(l)).Take(40)) report.AppendLine("  " + line);

        string text = report.ToString();
        _output.WriteLine(text);
        if (Directory.Exists(repo))
        {
            string scratch = Path.Combine(repo, "_scratch");
            Directory.CreateDirectory(scratch);
            File.WriteAllText(Path.Combine(scratch, "csqc-init-probe.txt"), text);
        }

        // The VM survived: its bookkeeping is sane and it still runs code. A fault inside CSQC_Init is an
        // expected outcome at this stage; anything other than QcRuntimeException would have escaped above.
        Assert.InRange(vm.NumEdicts, 1, vm.EdictLimit);
        vm.SetArgFloat(0, 9);
        vm.Execute(FunctionOfBuiltin(progs, 62), 1); // sqrt
        Assert.Equal(3f, vm.ResultFloat);
        core.Dispose();
    }

    /// <summary>
    /// The string builtins are written by a parallel effort. Found by name so this file compiles with
    /// or without them.
    /// </summary>
    private static string RegisterStringBuiltins(QcVm vm, IQcHost host)
    {
        Type? type = typeof(QcVm).Assembly.GetType("VortexArena.QuakeC.QcStringBuiltins");
        if (type is null) return "QcStringBuiltins not present in the assembly";
        try
        {
            object? instance = Activator.CreateInstance(type, vm, host);
            MethodInfo? register = type.GetMethod("Register", Type.EmptyTypes);
            if (instance is null || register is null) return "QcStringBuiltins has no Register()";
            register.Invoke(instance, null);
            return "registered (QcStringBuiltins)";
        }
        catch (Exception e) when (e is MissingMethodException or TargetInvocationException or MemberAccessException)
        {
            return "QcStringBuiltins could not be registered: " + (e.InnerException ?? e).Message;
        }
    }

    /// <summary>The extension list DarkPlaces hands the client program (csprogs.c:1081 uses vm_sv_extensions).</summary>
    private static IEnumerable<string> DarkPlacesExtensions(string csprogs)
    {
        string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csprogs)!, "..", "..", "darkplaces", "svvm_cmds.c"));
        if (!File.Exists(source)) yield break;
        bool inside = false;
        foreach (string line in File.ReadLines(source))
        {
            if (line.Contains("vm_sv_extensions[]", StringComparison.Ordinal)) { inside = true; continue; }
            if (!inside) continue;
            if (line.StartsWith("NULL", StringComparison.Ordinal) || line.StartsWith("}", StringComparison.Ordinal)) yield break;
            Match match = Regex.Match(line, "^\"([A-Za-z0-9_]+)\",");
            // libcurl, d0_blind_id and ODE are not here (prvm_cmds.c:323 answers false without them).
            if (match.Success && !Regex.IsMatch(match.Groups[1].Value, "^DP_(CRYPTO|QC_DIGEST_SHA256|QC_URI_GET|QC_URI_POST|PHYSICS_ODE)")) yield return match.Groups[1].Value;
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        if (TestPaths.RepoRoot != TestPaths.Unresolved) return TestPaths.RepoRoot;
        // <repo>/tests/VortexArena.Tests/QuakeC/CsqcInitProbeTests.cs
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "..", ".."));
    }

    private static int FunctionOfBuiltin(ProgsFile progs, int number) =>
        Array.FindIndex(progs.Functions, f => f.IsBuiltin && f.BuiltinNumber == number);

    private static void SetGlobalFloat(QcVm vm, string name, float value)
    {
        if (vm.FindGlobal(name) is QcDef def) vm.GlobalFloat(def.Offset) = value;
    }

    private static void SetGlobalInt(QcVm vm, string name, int value)
    {
        if (vm.FindGlobal(name) is QcDef def) vm.GlobalInt(def.Offset) = value;
    }

    private static void SetFieldFloat(QcVm vm, string name, float value)
    {
        if (vm.FindField(name) is QcDef def) vm.FieldFloat(0, def.Offset) = value;
    }

    private static void SetFieldInt(QcVm vm, string name, int value)
    {
        if (vm.FindField(name) is QcDef def) vm.FieldInt(0, def.Offset) = value;
    }

    private static void SetFieldVector(QcVm vm, string name, QcVector value)
    {
        if (vm.FindField(name) is QcDef def) vm.FieldVector(0, def.Offset) = value;
    }

    private static string Indent(string text) => "  " + text.TrimEnd().Replace("\n", "\n  ");

    private static string Wrap(IEnumerable<string> items)
    {
        StringBuilder text = new();
        int column = 0;
        foreach (string item in items)
        {
            if (column + item.Length > 116)
            {
                text.Append('\n');
                column = 0;
            }
            text.Append(column == 0 ? "  " : ", ").Append(item);
            column += item.Length + 2;
        }
        return text.ToString();
    }
}

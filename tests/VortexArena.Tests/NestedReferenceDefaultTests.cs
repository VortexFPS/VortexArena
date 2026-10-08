using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <see cref="ConfigInterpreter.NestedReferences"/> (DarkPlaces' <c>${$1}</c>) is on by default. That is a
/// change to how the native client reads the stock configuration, so its whole effect is pinned here:
/// run the client's configuration chain with it off and on, and the only cvars that may differ are the
/// ones listed - and only in the listed way. If this fails after a config change, the new difference is
/// either a setting that used to be silently blank (add it here, knowingly) or a regression.
/// </summary>
public class NestedReferenceDefaultTests
{
    private static Dictionary<string, string> RunClientChain(bool nested)
    {
        using VirtualFileSystem vfs = new();
        Assert.True(vfs.MountContentRoot(TestPaths.Data));
        CvarService cvars = new();
        ConfigInterpreter interp = new(cvars, path => vfs.Exists(path) ? vfs.ReadText(path) : null) { NestedReferences = nested };
        // What ConfigLoader.Load sets up before executing (it constructs its own interpreter, which is
        // why the chain is spelled out here: the flag has to be chosen per run).
        interp.DefineAlias("if_client", "${* asis}");
        interp.DefineAlias("if_dedicated", "${* asis}");
        ServerSlots.RegisterCommand(interp, _ => { });
        foreach (string file in new[] { "xonotic-client.cfg", ConfigLoader.ServerEntry, ConfigLoader.NotificationsEntry, ConfigLoader.VortexCommonEntry })
            interp.ExecuteFile(file);
        return cvars.Names.ToDictionary(n => n, n => cvars.GetString(n));
    }

    [Fact]
    public void IsOnByDefault()
    {
        Assert.True(new ConfigInterpreter(new CvarService(), _ => null).NestedReferences);
    }

    [Fact]
    public void ChangesOnlyTheKnownCvars_InTheNativeClientChain()
    {
        if (!Directory.Exists(TestPaths.Data)) return; // needs the content tree

        Dictionary<string, string> off = RunClientChain(nested: false), on = RunClientChain(nested: true);
        Assert.Equal(off.Keys.OrderBy(k => k, StringComparer.Ordinal), on.Keys.OrderBy(k => k, StringComparer.Ordinal));

        List<string> differences = off.Where(kv => on[kv.Key] != kv.Value)
            .Select(kv => $"{kv.Key}: \"{kv.Value}\" -> \"{on[kv.Key]}\"")
            .OrderBy(d => d, StringComparer.Ordinal).ToList();

        // Both were blanked by `makesaved` and now keep the value they had; both are 0 as numbers either way.
        Assert.Equal(new[] { "cl_maxfps_alwayssleep: \"\" -> \"0\"", "v_kicktime: \"\" -> \"0\"" }, differences);
    }

    [Fact]
    public void MakesavedKeepsACvarsValue()
    {
        CvarService cvars = new();
        ConfigInterpreter interp = new(cvars, _ => null);
        interp.ExecuteLine("set r_drawviewmodel 1");
        interp.ExecuteLine("alias makesaved \"seta $1 \\\"${$1 ?}\\\"\"");
        interp.ExecuteLine("makesaved r_drawviewmodel");
        Assert.Equal("1", cvars.GetString("r_drawviewmodel"));
    }
}

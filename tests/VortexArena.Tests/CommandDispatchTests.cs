using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The <c>commands.cfg</c> alias chain, asserted against the REAL shipped config tree.
///
/// <para>Every player-facing console verb in Xonotic is an alias of the form
/// <c>alias lsmaps "qc_cmd_svcmd lsmaps ${* ?}"</c>, and each <c>qc_cmd_*</c> is itself an alias that
/// resolves — through the <c>if_client</c>/<c>if_dedicated</c> pair in <c>xonotic-common.cfg</c> — to one of
/// four prefix verbs: <c>cmd</c>, <c>sv_cmd</c>, <c>cl_cmd</c>, <c>menu_cmd</c>. In QC those four are
/// registered commands. In the port they were not, so all ~158 aliases expanded correctly and then died on
/// the last hop: the line reached the console's unknown-command router as <c>cmd &lt;verb&gt;</c> and came
/// back "Unknown command". <c>ConsoleOverlay.RegisterHostCommands</c> supplies that hop.</para>
///
/// <para>These tests deliberately assert the EXPANSION, not the handler: the handlers live in
/// <c>game/</c> (Godot, unreachable from here), but what an alias expands to is pure interpreter behaviour
/// over shipped content — and it is the half that silently rotted. A stand-in interpreter registers the four
/// verbs so the terminal line each alias produces is observable.</para>
/// </summary>
public class CommandDispatchTests
{
    private static readonly string Pk3Dir = TestPaths.CorePk3Dir;

    private static Func<string, string?> DiskReader => path =>
    {
        string full = Path.Combine(Pk3Dir, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    };

    private static bool HaveData => File.Exists(Path.Combine(Pk3Dir, "commands.cfg"));

    /// <summary>Load the real config tree; returns the interpreter plus the list every dispatched prefix
    /// verb appends to, so a test can assert "typing X ends up as `cmd X`".</summary>
    private static (ConfigInterpreter Interp, List<string> Dispatched) LoadTree()
    {
        var cvars = new CvarService();
        // xonotic-common.cfg defines if_client/if_dedicated and execs commands.cfg — the whole chain under test.
        ConfigInterpreter interp = ConfigLoader.Load(cvars, DiskReader, "xonotic-common.cfg");

        var dispatched = new List<string>();
        foreach (string verb in new[] { "cmd", "sv_cmd", "cl_cmd", "menu_cmd" })
        {
            string captured = verb;
            interp.RegisterCommand(verb, argv => dispatched.Add($"{captured}:{string.Join(' ', argv.Skip(1))}"));
        }
        // Anything reaching the unknown handler is a chain that did NOT bottom out in a prefix verb.
        interp.UnknownCommandHandler = (name, argv) => dispatched.Add($"UNKNOWN:{string.Join(' ', argv)}");
        return (interp, dispatched);
    }

    [Fact]
    public void The_Four_Prefix_Verbs_Are_What_Every_qc_cmd_Alias_Resolves_To()
    {
        if (!HaveData) return;
        (ConfigInterpreter interp, _) = LoadTree();

        // If these ever stop being aliases (e.g. upstream registers them in QC instead), the port's four
        // registrations become dead weight and this test says so.
        Assert.Equal("cmd $*", interp.Aliases["qc_cmd_cmd"]);
        Assert.Equal("cmd $*", interp.Aliases["qc_cmd_svcmd"]);
        Assert.Equal("sv_cmd $*", interp.Aliases["qc_cmd_sv"]);
        Assert.Equal("cl_cmd $*", interp.Aliases["qc_cmd_cl"]);
        Assert.Equal("cl_cmd $*", interp.Aliases["qc_cmd_svcl"]);
        Assert.Equal("menu_cmd $*", interp.Aliases["qc_cmd_svmenu"]);
    }

    [Theory]
    // the CommonCommand family the report was about — all qc_cmd_svcmd → `cmd`
    [InlineData("lsmaps", "cmd:lsmaps")]
    [InlineData("printmaplist", "cmd:printmaplist")]
    [InlineData("records", "cmd:records")]
    [InlineData("rankings", "cmd:rankings")]
    [InlineData("ladder", "cmd:ladder")]
    [InlineData("teamstatus", "cmd:teamstatus")]
    [InlineData("time", "cmd:time")]
    [InlineData("cvar_changes", "cmd:cvar_changes")]
    [InlineData("cvar_purechanges", "cmd:cvar_purechanges")]
    [InlineData("info", "cmd:info")]
    // a qc_cmd_sv admin verb, and a qc_cmd_svmenu front-end one
    [InlineData("gotomap", "sv_cmd:gotomap")]
    [InlineData("shuffleteams", "sv_cmd:shuffleteams")]
    public void A_Shipped_Alias_Bottoms_Out_In_A_Prefix_Verb(string typed, string expected)
    {
        if (!HaveData) return;
        (ConfigInterpreter interp, List<string> dispatched) = LoadTree();

        interp.ExecuteLine(typed);

        // The trailing `${* ?}` contributes nothing with no arguments, so the dispatched tail is the bare verb.
        Assert.Equal(new[] { expected }, dispatched.Select(d => d.TrimEnd()));
    }

    [Fact]
    public void An_Aliased_Command_Carries_Its_Arguments_Through()
    {
        if (!HaveData) return;
        (ConfigInterpreter interp, List<string> dispatched) = LoadTree();

        interp.ExecuteLine("gotomap stormkeep");

        // `${* ?}` is what forwards the arguments; losing it would strip every argument from every verb.
        Assert.Single(dispatched);
        Assert.Contains("stormkeep", dispatched[0]);
    }

    [Fact]
    public void No_Shipped_Alias_Dies_Before_Reaching_A_Prefix_Verb()
    {
        if (!HaveData) return;
        (ConfigInterpreter interp, List<string> dispatched) = LoadTree();

        // Sweep the whole shipped alias table rather than a hand-picked sample: this is the assertion that
        // would have caught the original breakage, and it stays true only while all four verbs are wired.
        var chainAliases = interp.Aliases
            .Where(kv => kv.Value.Contains("qc_cmd_", StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        Assert.True(chainAliases.Count > 100, $"expected the full commands.cfg table, saw {chainAliases.Count}");

        var stranded = new List<string>();
        foreach (string name in chainAliases)
        {
            dispatched.Clear();
            interp.ExecuteLine(name);
            if (dispatched.Count == 0 || dispatched.Any(d => d.StartsWith("UNKNOWN:", StringComparison.Ordinal)))
                stranded.Add(name);
        }

        Assert.True(stranded.Count == 0,
            $"{stranded.Count} shipped alias(es) never reach a prefix verb: {string.Join(", ", stranded.Take(20))}");
    }

    [Fact]
    public void Vortex_Adds_maps_And_listmaps_As_Aliases_For_lsmaps()
    {
        if (!HaveData || DiskReader("vortex-client.cfg") is null) return;
        var cvars = new CvarService();
        ConfigInterpreter interp = ConfigLoader.Load(cvars, DiskReader, "vortex-common.cfg");

        // Both must forward arguments, so `maps ctf` filters the same way `lsmaps ctf` does.
        Assert.Equal("lsmaps ${* ?}", interp.Aliases["listmaps"]);
        Assert.Equal("lsmaps ${* ?}", interp.Aliases["maps"]);

        // And they must land on `lsmaps` — which at runtime is a REGISTERED command (registered names outrank
        // aliases), i.e. the client-side one that answers with no server running.
        string? reached = null;
        interp.RegisterCommand("lsmaps", argv => reached = string.Join(' ', argv));
        interp.ExecuteLine("maps ctf");
        Assert.Equal("lsmaps ctf", reached);

        reached = null;
        interp.ExecuteLine("listmaps");
        Assert.Equal("lsmaps", reached?.TrimEnd());
    }
}

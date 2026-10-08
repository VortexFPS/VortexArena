using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// DarkPlaces' "${$1}" (cmd.c Cmd_GetCvarValue: a reference whose NAME is itself a reference), which
/// Xonotic's own configuration relies on. Found by eye: a legacy session drew no first-person weapon
/// because xonotic-client.cfg runs <c>makesaved r_drawviewmodel</c>, where
/// <c>alias makesaved "seta $1 \"${$1 ?}\""</c> re-sets a cvar to its own value, and the interpreter read
/// a cvar literally named "$1" instead - blanking r_drawviewmodel and every other cvar the alias touches.
/// The legacy session turns <see cref="ConfigInterpreter.NestedReferences"/> on; it is off by default so
/// nothing else changes.
/// </summary>
public class LegacyConfigNestedReferenceTests
{
    private const string MakeSaved = "alias makesaved \"seta $1 \\\"${$1 ?}\\\"\"\n";

    private static ConfigInterpreter New(CvarService cvars, bool nested) =>
        new(cvars, _ => null) { NestedReferences = nested };

    [Fact]
    public void MakeSaved_Keeps_The_Value_When_Nested_References_Are_On()
    {
        var cvars = new CvarService();
        cvars.Register("r_drawviewmodel", "1");
        New(cvars, nested: true).ExecuteScript(MakeSaved + "makesaved r_drawviewmodel\n");
        Assert.Equal("1", cvars.GetString("r_drawviewmodel"));
    }

    [Fact]
    public void A_Value_With_Spaces_Survives_The_Round_Trip()
    {
        var cvars = new CvarService();
        cvars.Register("g_maplist", "stormkeep xoylent");
        New(cvars, nested: true).ExecuteScript(MakeSaved + "makesaved g_maplist\n");
        Assert.Equal("stormkeep xoylent", cvars.GetString("g_maplist"));
    }

    [Fact]
    public void A_Nested_Reference_Selects_What_To_Run()
    {
        // binds-xonotic.cfg: alias _userbind_call "${$1}" - run the command stored in the cvar the argument names.
        var cvars = new CvarService();
        cvars.Register("userbind1_press", "set fired yes");
        New(cvars, nested: true).ExecuteScript("alias _userbind_call \"${$1}\"\n_userbind_call userbind1_press\n");
        Assert.Equal("yes", cvars.GetString("fired"));
    }

    [Fact]
    public void A_Nested_Reference_To_Nothing_Is_Empty()
    {
        var cvars = new CvarService();
        cvars.Register("target", "kept");
        // No argument: "$1" is empty, so "${$1}" names no cvar.
        New(cvars, nested: true).ExecuteScript("alias blank \"set target \\\"${$1 ?}\\\"\"\nblank\n");
        Assert.Equal("", cvars.GetString("target"));
    }

    [Fact]
    public void Switched_Off_The_Reference_Reads_A_Cvar_Literally_Named_Dollar_One()
    {
        // The old behaviour, still reachable for comparison. It was the default until 2026-10-07, when
        // nested references were turned on everywhere after measuring that the native client's
        // configuration changes in exactly two cvars (see NestedReferenceDefaultTests).
        var cvars = new CvarService();
        cvars.Register("r_drawviewmodel", "1");
        var interpreter = new ConfigInterpreter(cvars, _ => null) { NestedReferences = false };
        interpreter.ExecuteScript(MakeSaved + "makesaved r_drawviewmodel\n");
        Assert.Equal("", cvars.GetString("r_drawviewmodel"));
    }
}

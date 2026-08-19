using System.Collections.Generic;
using VortexArena.Common.Config;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The server slot count and its <c>maxplayers</c> command — the port of DP's svs.maxclients /
/// svs.maxclients_next pair (server.h:28), MaxPlayers_f (host_cmd.c:2517) and the next-map adoption in
/// Host_Map_f (host_cmd.c:375-380).
///
/// <para>The regression these exist for: <c>maxplayers</c> is a COMMAND, and before it was registered the
/// shipped <c>xonotic-server.cfg:31</c> line fell through the interpreter's bare-assignment path and created a
/// <c>maxplayers</c> CVAR nobody read — leaving the real cap hardcoded at 16 in the host, which is why a listen
/// server capped out at 15 bots.</para>
/// </summary>
[Collection("GlobalState")]
public class ServerSlotsTests
{
    public ServerSlotsTests() => ServerSlots.Reset();

    private static ConfigInterpreter New(CvarService cvars)
    {
        var interp = new ConfigInterpreter(cvars, _ => null);
        ServerSlots.RegisterCommand(interp, _ => { });
        return interp;
    }

    /// <summary>Collects the command's Con_Printf output so the reporting branches can be asserted.</summary>
    private static ConfigInterpreter NewCapturing(CvarService cvars, List<string> output)
    {
        var interp = new ConfigInterpreter(cvars, _ => null);
        ServerSlots.RegisterCommand(interp, output.Add);
        return interp;
    }

    [Theory]
    [InlineData("maxplayers 24", 24)]
    [InlineData("maxplayers 1", 1)]
    [InlineData("maxplayers 255", 255)]
    [InlineData("maxplayers 0", ServerSlots.MinSlots)]        // DP bound(1, n, MAX_SCOREBOARD)
    [InlineData("maxplayers -8", ServerSlots.MinSlots)]
    [InlineData("maxplayers 9999", ServerSlots.MaxSlots)]
    [InlineData("maxplayers banana", ServerSlots.MinSlots)]   // DP atoi: unparseable is 0, clamped up to 1
    public void Maxplayers_RecordsThePendingCount_ClampedToTheEngineRange(string line, int expected)
    {
        New(new CvarService()).ExecuteLine(line);
        Assert.Equal(expected, ServerSlots.MaxClientsNext);
    }

    [Fact]
    public void Maxplayers_DoesNotMoveTheLiveCount_UntilTheNextServerStart()
    {
        // 40, deliberately not DefaultSlots — a value equal to the default would make every assertion below
        // pass without the deferral working at all.
        New(new CvarService()).ExecuteLine("maxplayers 40");

        // DP writes only maxclients_next; the running server keeps its own count (host_cmd.c:2537).
        Assert.Equal(ServerSlots.DefaultSlots, ServerSlots.MaxClients);
        Assert.NotEqual(40, ServerSlots.DefaultSlots);
        Assert.Equal(40, ServerSlots.MaxClientsNext);

        // ...and Host_Map_f promotes it as the next server starts (host_cmd.c:375-380).
        Assert.Equal(40, ServerSlots.Adopt());
        Assert.Equal(40, ServerSlots.MaxClients);
    }

    [Fact]
    public void Maxplayers_WithNoArgument_ReportsThePendingCount()
    {
        var output = new List<string>();
        ConfigInterpreter interp = NewCapturing(new CvarService(), output);

        interp.ExecuteLine("maxplayers 20");
        output.Clear();
        interp.ExecuteLine("maxplayers");

        Assert.Equal(new[] { "\"maxplayers\" is \"20\"" }, output);
        Assert.Equal(20, ServerSlots.MaxClientsNext); // a bare query changes nothing
    }

    [Fact]
    public void Maxplayers_OnALiveServer_SaysItIsDeferred_ButStillRecordsIt()
    {
        var output = new List<string>();
        ConfigInterpreter interp = NewCapturing(new CvarService(), output);
        ServerSlots.Adopt();                       // a server started at the default count...
        ServerSlots.IsServerActive = () => true;   // ...and is still running (DP sv.active)

        interp.ExecuteLine("maxplayers 40");

        Assert.Contains("maxplayers can not be changed while a server is running.", output);
        Assert.Contains("It will be changed on next server startup (\"map\" command).", output);
        // DP prints the refusal and records the value anyway — deferred, not dropped.
        Assert.Equal(40, ServerSlots.MaxClientsNext);
        Assert.Equal(ServerSlots.DefaultSlots, ServerSlots.MaxClients);
    }

    [Fact]
    public void ShippedServerConfigLine_SetsTheSlotCount_AndMintsNoPhantomCvar()
    {
        // Exactly what data/core.pk3dir/xonotic-server.cfg:31 ships, loaded the way the host loads it.
        var cvars = new CvarService();
        var files = new Dictionary<string, string> { ["entry.cfg"] = "maxplayers 24\n" };
        ConfigLoader.Load(cvars, p => files.TryGetValue(p, out string? t) ? t : null, "entry.cfg");

        Assert.Equal(24, ServerSlots.MaxClientsNext);
        // The bug this pins: without a registered handler the line was a bare cvar assignment instead.
        Assert.False(cvars.Has("maxplayers"));
    }

    [Fact]
    public void ConfigLoader_RegistersTheCommand_BeforeExecutingAnyEntryFile()
    {
        // Ordering is the whole point: an entry file's own `maxplayers` has to be seen by the handler, not by
        // the bare-assignment fallback that runs when no command is registered.
        var cvars = new CvarService();
        var files = new Dictionary<string, string>
        {
            ["a.cfg"] = "maxplayers 12\n",
            ["b.cfg"] = "maxplayers 30\n", // later entry files override earlier ones (DP set semantics)
        };
        ConfigLoader.Load(cvars, p => files.TryGetValue(p, out string? t) ? t : null, "a.cfg", "b.cfg");

        Assert.Equal(30, ServerSlots.MaxClientsNext);
        Assert.False(cvars.Has("maxplayers"));
    }
}

using System.Numerics;
using VortexArena.Common;
using VortexArena.Common.Framework;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The live tick length (<see cref="SimulationLoop.TickSeconds"/> — the DP <c>sys_ticrate</c> port,
/// 2026-08-03). The compile-time <see cref="SimulationLoop.TicRate"/> stays the DEFAULT (DP's engine
/// default, 1/72 s) so every existing determinism test is untouched; these pin the three properties the
/// cvar plumbing relies on: the default holds, a set value retunes tick cadence AND the published
/// frametime, and garbage values clamp to DP's sane band instead of stalling or spinning the server.
/// </summary>
public class SimulationTickRateTests
{
    private static SimulationLoop NewSim()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096, -4096, -64), new Vector3(4096, 4096, 0), SuperContents.Solid));
        world.BuildGrid();
        var services = new EngineServices(world);
        GameInit.Boot(services);
        return new SimulationLoop(services, world);
    }

    [Fact]
    public void Default_IsTheEngineDefault()
    {
        var sim = NewSim();
        Assert.Equal(SimulationLoop.TicRate, sim.TickSeconds);

        // One second of real time at the default = the default tick count (72), and FrameTime publishes it.
        int ticks = 0;
        for (int i = 0; i < 100; i++)
            ticks += sim.Advance(0.01f);
        Assert.InRange(ticks, 71, 72);
        Assert.Equal(SimulationLoop.TicRate, sim.FrameTime);
    }

    [Fact]
    public void SixtyHz_TicksSixtyTimesPerSecond_AndPublishesTheLength()
    {
        var sim = NewSim();
        sim.TickSeconds = 1f / 60f;   // the Vortex default (vortex-server.cfg sys_ticrate 0.0166667)

        int ticks = 0;
        for (int i = 0; i < 100; i++)
            ticks += sim.Advance(0.01f);   // 1.0 s of real time
        Assert.InRange(ticks, 59, 60);

        // sv.frametime + the QC-facing clock both carry the LIVE length after a tick ran.
        Assert.Equal(1f / 60f, sim.FrameTime, 6);
        // Sim time advanced by ticks * tick-length (the accumulator holds the sub-tick remainder).
        Assert.Equal(ticks * (1f / 60f), sim.Time, 4);
    }

    /// <summary>The REAL boot path: a private-store world with the disk config chain must come up at the
    /// layer's 60 Hz BEFORE any tick or handshake runs (the boot-apply in GameWorld.Boot). This reproduces
    /// the release-capture path (sv_threaded listen world: private store + ConfigReader) in-process, so a
    /// regression here is caught in milliseconds instead of a 4-minute capture.</summary>
    [Fact]
    public void BootedWorld_WithConfigChain_ComesUpAt60Hz()
    {
        string root = FindRepoRoot();
        var world = new VortexArena.Server.GameWorld(NewCollision());
        world.ConfigReader = path =>
        {
            string p = System.IO.Path.Combine(root, "data", "core.pk3dir", path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : null;
        };
        world.Boot();
        Assert.Equal(0.0166667f, world.Simulation.TickSeconds, 5);
    }

    private static CollisionWorld NewCollision()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096, -4096, -64), new Vector3(4096, 4096, 0), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    private static string FindRepoRoot()
    {
        string dir = System.AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "VortexArena.csproj")))
            dir = System.IO.Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void GarbageValues_ClampToTheSaneBand()
    {
        var sim = NewSim();

        sim.TickSeconds = 0f;                 // unset/zero -> default
        Assert.Equal(SimulationLoop.TicRate, sim.TickSeconds);
        sim.TickSeconds = float.NaN;          // garbage -> default
        Assert.Equal(SimulationLoop.TicRate, sim.TickSeconds);
        sim.TickSeconds = 5f;                 // absurdly slow -> DP's 100 ms floor rate
        Assert.Equal(0.1f, sim.TickSeconds);
        sim.TickSeconds = 0.00001f;           // absurdly fast -> DP's 1 ms ceiling rate
        Assert.Equal(0.001f, sim.TickSeconds);
    }
}

using System;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// DP overload semantics in <see cref="SimulationLoop.Advance"/> (frametime parity audit 2026-07-11):
/// sv_overload_timedrop → <see cref="SimulationLoop.BacklogDropSeconds"/> (DP sv_main.c:2604 — cap the owed
/// backlog, SHED the excess: overload = brief uniform slow-motion, not burst catch-up), and
/// sv_catchup_wallbudget_ms → <see cref="SimulationLoop.CatchupWallBudgetSeconds"/> (DP aborttime :2676 —
/// stop starting catch-up ticks past the wall budget; the first owed tick always runs).
/// </summary>
public class SimulationOverloadTests
{
    private readonly ITestOutputHelper _out;
    public SimulationOverloadTests(ITestOutputHelper o) => _out = o;

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
    public void BacklogDrop_ShedsExcessTime_InsteadOfBurstCatchup()
    {
        // One 250ms stall (the Advance input clamp caps a single delta there). Legacy pays the whole debt back
        // in ticks; DP timedrop caps the owed backlog at 100ms and sheds the rest as lost time.
        var legacy = NewSim();                                    // BacklogDropSeconds = 0 (legacy)
        var dp = NewSim();
        dp.BacklogDropSeconds = 0.1f;                             // sv_overload_timedrop 1

        int legacyTicks = 0, dpTicks = 0;
        legacyTicks += legacy.Advance(0.25f);
        dpTicks += dp.Advance(0.25f);
        for (int i = 0; i < 30; i++)                              // calm frames drain any preserved backlog
        {
            legacyTicks += legacy.Advance(1f / 200f);
            dpTicks += dp.Advance(1f / 200f);
        }

        _out.WriteLine($"250ms stall + 150ms calm: legacy={legacyTicks} ticks, timedrop={dpTicks} ticks, " +
                       $"lost={dp.TimeLostSeconds * 1000:F0}ms");
        // Legacy: the full ~0.25s debt + 0.15s of calm frames ⇒ ~28-29 ticks. Timedrop: at most 0.1s of the
        // stall survives ⇒ ~7 owed + ~10 from calm frames ⇒ ~17-18, with ~0.15s accounted as shed.
        Assert.True(legacyTicks >= dpTicks + 9,
            $"timedrop must run meaningfully fewer catch-up ticks (legacy {legacyTicks} vs {dpTicks})");
        Assert.InRange(dp.TimeLostSeconds, 0.12, 0.16);           // 0.25 accumulated − 0.1 kept ≈ 0.15 shed
        Assert.Equal(0, (int)legacy.TimeLostSeconds);             // legacy under spiral guard loses nothing
    }

    [Fact]
    public void CatchupWallBudget_StopsStartingTicks_PastTheBudget()
    {
        var sim = NewSim();
        sim.CatchupWallBudgetSeconds = 0.004f;                    // sv_catchup_wallbudget_ms 4
        // Deterministic clock: each WallClock() call advances 3ms. budgetStart consumes the first call, so the
        // pre-tick checks read 3ms (< 4ms budget → tick 2 runs), then 6ms (>= 4ms → stop before tick 3).
        double now = 0.0;
        sim.WallClock = () => { double v = now; now += 0.003; return v; };

        int ticks = sim.Advance(6f / 72f);                        // 6 owed ticks
        _out.WriteLine($"6 owed ticks, 4ms budget @3ms/tick-check: ran {ticks}");
        Assert.Equal(2, ticks);

        // The deferred ticks stay owed and drain once the budget pressure is gone.
        sim.CatchupWallBudgetSeconds = 0f;
        int drained = sim.Advance(0f);
        Assert.Equal(4, drained);
        Assert.Equal(0, (int)sim.TimeLostSeconds);                // deferral is not loss
    }

    [Fact]
    public void CatchupWallBudget_FirstOwedTickAlwaysRuns()
    {
        var sim = NewSim();
        sim.CatchupWallBudgetSeconds = 0.001f;
        double now = 0.0;
        sim.WallClock = () => { now += 10.0; return now; };       // every check reads 10s elapsed: always over budget
        // A wall-clock already past the budget must still make progress: exactly one tick per Advance.
        Assert.Equal(1, sim.Advance(3f / 72f));
        Assert.Equal(1, sim.Advance(0f));
        Assert.Equal(1, sim.Advance(0f));
        Assert.Equal(0, sim.Advance(0f));                         // debt fully drained, one tick at a time
    }
}

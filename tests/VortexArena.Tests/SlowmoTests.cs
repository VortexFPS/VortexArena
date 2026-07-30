using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Tests.Camera;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Slowmo / host_timescale time-scaling tests (Path B "while we're at it"). The time scale maps real time → fixed
/// sim ticks: the WHOLE simulation runs slower/faster in real time while each tick stays a fixed 1/72 s of SIM
/// time. Two layers: (1) <see cref="SimulationLoop.TimeScale"/> scales the server world tick rate (REAL code);
/// (2) the full listen-server harness scales BOTH accumulators (server sim + client input) so movement slows
/// CONSISTENTLY and client prediction stays in lockstep (the same command-driven model that makes it fps-independent).
/// </summary>
public class SlowmoTests
{
    private readonly ITestOutputHelper _out;
    public SlowmoTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void SimulationLoop_TimeScale_ScalesTheWorldTickRate()
    {
        // Over the SAME real duration, TimeScale 0.5 must run ~half the fixed ticks of TimeScale 1.0, and 2.0 ~double.
        const float realDuration = 2.0f, frameDt = 1f / 100f;
        int full = SumTicks(1.0f, realDuration, frameDt);
        int half = SumTicks(0.5f, realDuration, frameDt);
        int dbl = SumTicks(2.0f, realDuration, frameDt);
        int paused = SumTicks(0.0f, realDuration, frameDt);
        _out.WriteLine($"ticks over {realDuration}s @100fps: x1={full}  x0.5={half}  x2={dbl}  x0={paused}");

        int expectFull = (int)(realDuration * 72f);                  // ~144
        Assert.InRange(full, expectFull - 2, expectFull + 2);
        Assert.InRange(half, full / 2 - 2, full / 2 + 2);            // ~half
        Assert.InRange(dbl, full * 2 - 2, full * 2 + 2);             // ~double
        Assert.Equal(0, paused);                                     // 0 = paused, no ticks
    }

    private static int SumTicks(float timeScale, float realDuration, float frameDt)
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096, -4096, -64), new Vector3(4096, 4096, 0), SuperContents.Solid));
        world.BuildGrid();
        var services = new EngineServices(world);
        GameInit.Boot(services);
        var sim = new SimulationLoop(services, world) { TimeScale = timeScale };
        int ticks = 0;
        for (float t = 0; t < realDuration; t += frameDt)
            ticks += sim.Advance(frameDt);
        return ticks;
    }

    [Fact]
    public void Slowmo_HalfSpeedDoubleTime_SameDistance_AndStaysInSync()
    {
        // Command-driven (the fix) + slowmo: half speed for DOUBLE the real time must cover the SAME sim distance
        // (identical sim progress), and client prediction must stay in lockstep (reconcile correction ~0) the whole
        // time. Run forward+auto-bhop at 43fps under both regimes.
        var full = RunBhop(slowmo: 1.0f, seconds: 4f, fps: 43f);
        var slow = RunBhop(slowmo: 0.5f, seconds: 8f, fps: 43f); // half speed, double time → same sim progress

        float fullX = full.ServerOrigin.X, slowX = slow.ServerOrigin.X;
        _out.WriteLine($"forward distance: x1 over 4s = {fullX:F1}u ; x0.5 over 8s = {slowX:F1}u " +
                       $"(client maxCorr x1={MaxCorr(full.Metrics):F3}u x0.5={MaxCorr(slow.Metrics):F3}u)");

        Assert.True(fullX > 500f, $"sanity: the player should have travelled forward (got {fullX}u)");
        Assert.True(MathF.Abs(slowX - fullX) < 0.05f * fullX,
            $"half-speed for double the time must cover the same distance (x1={fullX:F1}u vs x0.5={slowX:F1}u)");
        Assert.True(MaxCorr(full.Metrics) < 0.5f, "no rubberband at slowmo 1 (command-driven)");
        Assert.True(MaxCorr(slow.Metrics) < 0.5f, "slowmo must stay in lockstep too (command-driven)");
    }

    [Fact]
    public void Slowmo_IsFrameRateIndependent()
    {
        // The slowed distance over a fixed real time must be the SAME at any fps (true frame-independence under slowmo).
        float d60 = RunBhop(slowmo: 0.5f, seconds: 6f, fps: 60f).ServerOrigin.X;
        float d43 = RunBhop(slowmo: 0.5f, seconds: 6f, fps: 43f).ServerOrigin.X;
        float d144 = RunBhop(slowmo: 0.5f, seconds: 6f, fps: 144f).ServerOrigin.X;
        _out.WriteLine($"slowmo 0.5 distance @ 60/43/144 fps: {d60:F1} / {d43:F1} / {d144:F1} u");
        float spread = MathF.Max(d60, MathF.Max(d43, d144)) - MathF.Min(d60, MathF.Min(d43, d144));
        Assert.True(spread < 0.03f * d60, $"slowmo distance must be fps-independent (spread {spread:F1}u over ~{d60:F0}u)");
    }

    private static ListenServerHarness RunBhop(float slowmo, float seconds, float fps)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();
        var dt = 1f / fps;
        var stream = new List<float>();
        for (float t = 0; t < seconds; t += dt) stream.Add(dt);
        var h = new ListenServerHarness { CommandDriven = true, Slowmo = slowmo, ServerSoftCap = 4 };
        h.Run(stream, world, clock);
        return h;
    }

    private static float MaxCorr(List<ListenServerHarness.FrameMetric> m)
    { float x = 0; foreach (var f in m) x = MathF.Max(x, f.ReconcileCorrection); return x; }

    private static (AnalyticWorld, MutableClock) FlatWorld()
    {
        var brushes = new List<(int, float[])>
        { (AnalyticWorld.ContSolid, new float[] { 0,0,1,0, 0,0,-1,64, 1,0,0,16384, -1,0,0,16384, 0,1,0,16384, 0,-1,0,16384 }) };
        return (AnalyticWorld.FromPlanes(brushes), new MutableClock());
    }
}

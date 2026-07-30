using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Variable-dt (Phase 2 / cl_movement_perframe) invariance guard. The per-frame input path feeds the SHARED
/// <see cref="PlayerPhysics"/> a sequence of commands stamped with the real frame dt instead of a fixed 1/72 s,
/// and the server integrates one move per command (each with its own dt) — DP's process-queued-client-moves.
/// The correctness property Option A relies on is that this preserves WALL-CLOCK speed: running forward for the
/// same real time covers the same ground whether the input is one 72 Hz stream or many jittery variable frames.
/// (Client prediction and server authority both call the identical <see cref="PlayerPhysics.Move"/> on the same
/// per-command sequence, so they are in lockstep by construction; the only residual vs the fixed cadence is the
/// integrator's split-variance, which this test bounds.)
///
/// Reuses the analytic trace harness (<see cref="AnalyticWorld"/>/<see cref="MovementTestServices"/>) the
/// golden-trace parity tests use, so it isolates the movement maths from BSP collision.
/// </summary>
public class PerFrameInputTests
{
    private readonly ITestOutputHelper _out;
    public PerFrameInputTests(ITestOutputHelper o) => _out = o;

    // A large flat solid floor with its top surface at z = 0.
    private static AnalyticWorld FlatFloor() => AnalyticWorld.FromPlanes(new (int, float[])[]
    {
        (AnalyticWorld.ContSolid, new float[]
        {
            0f,  0f,  1f, 0f,      // top    (inside z <= 0)
            0f,  0f, -1f, 128f,    // bottom (inside z >= -128)
            1f,  0f,  0f, 8192f,
           -1f,  0f,  0f, 8192f,
            0f,  1f,  0f, 8192f,
            0f, -1f,  0f, 8192f,
        }),
    });

    /// <summary>Run +forward for the given dt sequence on flat ground from rest; return the final forward (X) pos.</summary>
    private static float RunForward(AnalyticWorld world, IReadOnlyList<float> dts)
    {
        var clock = new MutableClock();
        Api.Services = new MovementTestServices(world, clock);
        var physics = new PlayerPhysics();
        var player = new Entity
        {
            Origin = new Vector3(0f, 0f, 24f),   // feet on the floor (hull mins.z = -24)
            Velocity = Vector3.Zero,
            Angles = Vector3.Zero,
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            Gravity = 1f,
            Flags = EntFlags.OnGround,
        };

        foreach (float dt in dts)
        {
            clock.Time += dt;                    // the engine advances `time` before the player physics
            physics.Move(player, new MovementInput
            {
                ViewAngles = Vector3.Zero,
                MoveValues = new Vector3(400f, 0f, 0f), // +forward at cl_forwardspeed
                FrameTime = dt,
            });
        }
        return player.Origin.X;
    }

    [Fact]
    public void VariableDt_GroundRun_PreservesWallClockSpeed()
    {
        AnalyticWorld world = FlatFloor();
        const float total = 1.0f;
        const float tic = 1f / 72f;

        // (a) the legacy fixed cadence: 72 ticks of 1/72 s.
        var fixed72 = new List<float>();
        for (int i = 0; i < 72; i++) fixed72.Add(tic);

        // (b) a jittery per-frame cadence (~140 fps, alternating 6.5/7.5 ms) summing to the SAME 1 s wall clock.
        var variable = new List<float>();
        float acc = 0f;
        int k = 0;
        while (acc < total - 1e-4f)
        {
            float dt = (k++ % 2 == 0) ? 0.0065f : 0.0075f;
            if (acc + dt > total) dt = total - acc;
            variable.Add(dt);
            acc += dt;
        }

        float xFixed = RunForward(world, fixed72);
        float xVar = RunForward(world, variable);
        float diff = MathF.Abs(xFixed - xVar);
        _out.WriteLine($"fixed72 X={xFixed:F3} qu   variable({variable.Count} frames) X={xVar:F3} qu   diff={diff:F4} qu");

        // Both actually ran (ground accel from rest over ~1 s) — guards a no-op regression.
        Assert.True(xFixed > 100f, $"fixed-72 run barely moved: X={xFixed:F2}");
        Assert.True(xVar > 100f, $"variable-dt run barely moved: X={xVar:F2}");

        // The variable-dt split lands within a small tolerance of the fixed cadence over the same wall clock — the
        // wall-clock-speed invariance Option A depends on. A per-fps SPEEDUP (the bug if DeltaTime were wrong)
        // would diverge proportionally to the ~250 qu travelled, far outside this bound; the residual here is only
        // the integrator's dt split-variance.
        Assert.True(diff < 2.0f, $"variable-dt diverged from fixed-72 by {diff:F3} qu over 1 s (expected < 2.0)");
    }

    [Fact]
    public void VariableDt_HalfRateFrames_AlsoPreserveSpeed()
    {
        // Same wall clock, a DIFFERENT split again (≈30 fps, 33.3 ms frames) — a low-fps client must cover the
        // same ground as a high-fps one (each command carries the dt it represents), not less.
        AnalyticWorld world = FlatFloor();
        const float total = 1.0f;

        var fixed72 = new List<float>();
        for (int i = 0; i < 72; i++) fixed72.Add(1f / 72f);

        var lowFps = new List<float>();
        float acc = 0f;
        while (acc < total - 1e-4f)
        {
            float dt = MathF.Min(1f / 30f, total - acc);
            lowFps.Add(dt);
            acc += dt;
        }

        float xFixed = RunForward(world, fixed72);
        float xLow = RunForward(world, lowFps);
        float diff = MathF.Abs(xFixed - xLow);
        _out.WriteLine($"fixed72 X={xFixed:F3} qu   30fps({lowFps.Count} frames) X={xLow:F3} qu   diff={diff:F4} qu");

        Assert.True(xLow > 100f, $"low-fps run barely moved: X={xLow:F2}");
        Assert.True(diff < 4.0f, $"30 fps run diverged from fixed-72 by {diff:F3} qu over 1 s (expected < 4.0)");
    }
}

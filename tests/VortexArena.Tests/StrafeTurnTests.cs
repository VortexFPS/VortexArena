using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;
using XonoticGodot.Tests.Camera;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// STRAFE-TURN speed-gain characterization — the coverage gap the bhop tests leave open.
///
/// <see cref="MovementTimingTests"/> proves STRAIGHT-LINE bhop speed gain is fps-independent (the
/// QW clamp converges to the same capped speed regardless of step size). But the speed you gain while
/// TURNING (air-strafing) is a PATH INTEGRAL over a rotating wishdir — sum of accel·dt steps along a
/// direction that changes every step — and that integral's value can depend on the physics step size.
/// This matters for Base parity because Base's authoritative server steps each usercmd at the (variable,
/// ~cl_netfps≈72 Hz) inter-command time, while our client predicts per render frame: if the turning gain
/// is dt-sensitive, the two land on different speeds for the SAME turn (a reconcile error that only shows
/// up while turning), and our gain can diverge from Base's.
///
/// The test sweeps an IDENTICAL yaw trajectory (yaw(t) is a function of wall-clock time, so it is the same
/// across every dt stream) while holding strafe, and measures the final horizontal speed at several fixed
/// dt and under jitter. The spread vs the fine-dt reference quantifies the effect.
/// </summary>
public class StrafeTurnTests
{
    private readonly ITestOutputHelper _out;
    public StrafeTurnTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void StrafeTurn_SpeedGain_DtSensitivity()
    {
        // Choose a constant turn rate that actually GAINS speed, scanned once at the fine reference dt.
        const float refDt = 1f / 2000f;
        float bestOmega = 0f, bestGain = 0f;
        foreach (float w in new[] { 80f, 120f, 160f, 200f, 240f, 280f, 320f, 360f, 420f })
        {
            float fs = RunAirStrafe(FixedStream(refDt, Duration), w);
            if (fs > bestGain) { bestGain = fs; bestOmega = w; }
        }
        float reference = RunAirStrafe(FixedStream(refDt, Duration), bestOmega);
        _out.WriteLine($"chosen turn rate = {bestOmega:F0} deg/s; reference (dt=1/2000) final speed = {reference:F2} u/s (start 360)");

        // Sweep realistic fixed frame rates + a jittery stream; report each landing vs the continuous reference.
        // The PLAYABLE band (>=45 fps) is what must stay tight — at 30 fps the steps are coarse enough to under-
        // integrate the turn (measured ~-15 u/s), which is expected and not a fidelity concern.
        float[] dts = { 1f / 30f, 1f / 45f, 1f / 60f, 1f / 72f, 1f / 100f, 1f / 144f, 1f / 250f };
        float maxPlayableDelta = 0f;
        foreach (float dt in dts)
        {
            float fs = RunAirStrafe(FixedStream(dt, Duration), bestOmega);
            float delta = fs - reference;
            if (1f / dt >= 45f) maxPlayableDelta = MathF.Max(maxPlayableDelta, MathF.Abs(delta));
            _out.WriteLine($"  {1f / dt,5:F0} fps (dt {dt * 1000f,5:F2} ms): final {fs,7:F2} u/s   (delta vs ref {delta,7:F2})");
        }
        float jitter = RunAirStrafe(JitterStream(Duration), bestOmega);
        _out.WriteLine($"  jitter        : final {jitter,7:F2} u/s   (delta vs ref {jitter - reference,7:F2})");

        // The two rates that matter for live play: Base/server authority ~72 Hz vs typical client predict 144 Hz.
        // If this gap were large, a turn would predict one speed locally and reconcile to another every snapshot
        // (a turning-only rubberband). Measured ~1.6 u/s — negligible.
        float s72 = RunAirStrafe(FixedStream(1f / 72f, Duration), bestOmega);
        float s144 = RunAirStrafe(FixedStream(1f / 144f, Duration), bestOmega);
        float predictAuthorityGap = MathF.Abs(s72 - s144);
        _out.WriteLine($"server-rate(72Hz) vs client-predict(144Hz) strafe gain: {s72:F2} vs {s144:F2}  -> per-turn gap {predictAuthorityGap:F2} u/s");
        _out.WriteLine($"max |delta| across the playable band (45..250 fps): {maxPlayableDelta:F2} u/s");

        // The config must genuinely gain (so the numbers above mean something).
        Assert.True(bestGain > 360f, $"the chosen turn must actually gain speed over 360 (got {bestGain:F1})");
        // REGRESSION GUARDS — the QW air-accel + CPM aircontrol turning gain is ~dt-independent across the playable
        // band, so client-predict (high fps) and ~72 Hz authority land on the same speed for the same turn. These
        // lock that in. Measured margins are wide (delta <= ~4.4, gap ~1.6); the bounds leave headroom. Tighten
        // further once a Base csprogs reference trace exists (tools/camera-ref).
        Assert.True(maxPlayableDelta < 10f,
            $"turning strafe gain must be ~dt-stable in the playable band (max delta {maxPlayableDelta:F2} u/s vs continuous reference)");
        Assert.True(predictAuthorityGap < 6f,
            $"72 Hz authority vs 144 Hz predict must agree on turning gain (gap {predictAuthorityGap:F2} u/s)");
    }

    private const float Duration = 1.2f;

    // Start airborne high over the flat floor and air-strafe: hold Side=+1 (strafe) and sweep the yaw at a
    // constant rate. yaw(t) depends only on wall-clock time, so the angle trajectory is IDENTICAL across every
    // dt stream — any difference in the final speed is therefore pure integration granularity, not a different
    // turn. Returns the final horizontal speed (u/s).
    private float RunAirStrafe(IEnumerable<float> dts, float omegaDegPerSec)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        // High up so the whole run is airborne (the air branch), no floor contact over the window.
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 4000f), Velocity = new Vector3(360f, 0f, 0f), OnGround = false };

        const float startYaw = 120f; // wishdir = the view's right vector leads the +X start velocity
        float now = 0f;
        foreach (float dt in dts)
        {
            now += dt; clock.Time = now;
            float yaw = startYaw + omegaDegPerSec * now;
            var c = new InputCommand { ViewAngles = new Vector3(0f, yaw, 0f), Side = 1f, DeltaTime = dt };
            step.Step(ref s, in c, default);
        }
        return MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
    }

    private static IEnumerable<float> FixedStream(float dt, float seconds)
    {
        float t = 0f;
        while (t < seconds) { yield return dt; t += dt; }
    }

    private static IEnumerable<float> JitterStream(float seconds)
    {
        float[] pattern = { 1f / 144f, 1f / 60f, 1f / 90f, 1f / 45f, 1f / 120f, 1f / 72f, 1f / 165f, 1f / 50f };
        float t = 0f; int i = 0;
        while (t < seconds) { float dt = pattern[i++ % pattern.Length]; yield return dt; t += dt; }
    }

    private static (AnalyticWorld world, MutableClock clock) FlatWorld()
    {
        var brushes = new List<(int, float[])>
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                0, 0, 1, 0, 0, 0, -1, 64, 1, 0, 0, 8192, -1, 0, 0, 8192, 0, 1, 0, 8192, 0, -1, 0, 8192,
            }),
        };
        return (AnalyticWorld.FromPlanes(brushes), new MutableClock());
    }
}

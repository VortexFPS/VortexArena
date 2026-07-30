using System.Numerics;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for <see cref="ProjectilePredictor"/> — the client-side projectile motion model ported from CSQC's
/// <c>Projectile_Draw</c> (client-animated path): snap to each authoritative server origin as it arrives and
/// extrapolate locally by the networked velocity in between. These pin the three behaviours the renderer
/// depends on: (1) full-speed extrapolation between snapshots (the fix for the "blaster looks slower" lag),
/// (2) snap to truth on a fresh snapshot, and (3) no double-integration when the origin is updated every frame
/// (the demo-driver case).
/// </summary>
public class ProjectilePredictorTests
{
    private static void AssertVecEqual(Vector3 expected, Vector3 actual, int precision = 3)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    [Fact]
    public void FirstStep_SeedsFromTheSnapshot()
    {
        var p = new ProjectilePredictor();
        var origin = new Vector3(10f, 0f, 0f);
        var vel = new Vector3(6000f, 0f, 0f); // blaster speed, +X

        Vector3 pos = p.Step(origin, vel, 1f / 72f);

        Assert.True(p.Initialized);
        AssertVecEqual(origin, pos);           // first frame sits exactly on the spawn origin (no ease-in)
        AssertVecEqual(vel, p.Velocity);
    }

    [Fact]
    public void BetweenSnapshots_ExtrapolatesAtFullSpeed()
    {
        // The core fix: with NO new snapshot, the bolt advances by velocity*dt every frame — full speed,
        // immediately, no exponential ease and no interpolation delay.
        var p = new ProjectilePredictor();
        var origin = new Vector3(0f, 0f, 0f);
        var vel = new Vector3(6000f, 0f, 0f);
        float dt = 1f / 144f; // render faster than the 72Hz snapshot rate

        p.Step(origin, vel, dt); // seed

        // 5 idle frames (same netOrigin) → 5 * dt of travel at the full networked velocity.
        Vector3 pos = origin;
        for (int i = 0; i < 5; i++)
            pos = p.Step(origin, vel, dt);

        AssertVecEqual(origin + vel * (5f * dt), pos);
        // distance covered in 5 frames at 144fps = 6000 * 5/144 ≈ 208 units (NOT the ~tens of units a
        // dt*30 exponential ease would have produced from rest).
        Assert.Equal(6000f * (5f * dt), pos.X, 2);
    }

    [Fact]
    public void NewSnapshot_SnapsToServerTruth_AndRefreshesVelocity()
    {
        var p = new ProjectilePredictor();
        var o0 = new Vector3(0f, 0f, 0f);
        var v0 = new Vector3(6000f, 0f, 0f);
        float dt = 1f / 72f;

        p.Step(o0, v0, dt);               // seed at o0
        p.Step(o0, v0, dt);               // idle frame → extrapolated ahead of o0
        Assert.True(p.Position.X > 0f);

        // A fresh snapshot arrives with a new origin + a turned velocity: snap exactly to it.
        var o1 = new Vector3(83.3f, 0f, 0f);
        var v1 = new Vector3(0f, 6000f, 0f);
        Vector3 pos = p.Step(o1, v1, dt);

        AssertVecEqual(o1, pos);          // snapped to the authoritative origin
        AssertVecEqual(v1, p.Velocity);   // and adopted the new velocity for subsequent extrapolation
    }

    [Fact]
    public void OriginUpdatedEveryFrame_DoesNotDoubleIntegrate()
    {
        // The demo-driver / high-snapshot-rate case: the caller advances the origin itself every frame. The
        // predictor must then follow it EXACTLY, never adding a second velocity*dt on top (which would render
        // the projectile at double speed).
        var p = new ProjectilePredictor();
        var vel = new Vector3(1000f, 0f, 0f);
        float dt = 1f / 72f;
        var origin = new Vector3(0f, 0f, 0f);

        p.Step(origin, vel, dt); // seed

        for (int i = 0; i < 10; i++)
        {
            origin += vel * dt;                 // caller integrates (as DemoProjectileDriver / a per-frame stream does)
            Vector3 pos = p.Step(origin, vel, dt);
            AssertVecEqual(origin, pos);         // rendered position tracks the caller's origin 1:1, no overshoot
        }
    }

    [Fact]
    public void Trace_StopMode_HaltsAtTheWall()
    {
        // A detonate-on-impact flier (bounce=false): when the extrapolation segment crosses a wall, the
        // predicted position clamps to the surface and freezes (the server's removal lands a moment later) —
        // instead of overrunning the wall.
        var p = new ProjectilePredictor();
        var o = new Vector3(0f, 0f, 0f);
        var v = new Vector3(6000f, 0f, 0f); // +X at blaster speed
        float dt = 1f / 72f;
        p.Step(o, v, dt); // seed

        // A wall plane at X = 50 (normal -X). The sweep from ~0 toward ~83 crosses it.
        ProjectileWorldTrace wall = (start, end) =>
        {
            float wallX = 50f;
            if (start.X <= wallX && end.X >= wallX)
                return new ProjectileTraceHit(true, new Vector3(wallX, start.Y, start.Z), new Vector3(-1f, 0f, 0f));
            return new ProjectileTraceHit(false, end, default);
        };

        Vector3 pos = p.Step(o, v, dt, wall, bounce: false);
        Assert.Equal(50f, pos.X, 3);             // clamped to the wall
        AssertVecEqual(Vector3.Zero, p.Velocity); // frozen until the server removes it

        // Subsequent idle frames stay put (no overrun past the wall).
        pos = p.Step(o, v, dt, wall, bounce: false);
        Assert.Equal(50f, pos.X, 3);
    }

    [Fact]
    public void Trace_BounceMode_ReflectsOffTheWall()
    {
        // A gravity-free BOUNCEMISSILE (bounce=true, elastic factor 1): reflects off the plane and keeps its
        // speed, continuing to fly. Fire +X into a wall whose normal is -X → velocity flips to -X (mirror).
        var p = new ProjectilePredictor();
        var o = new Vector3(0f, 0f, 0f);
        var v = new Vector3(1000f, 0f, 0f);
        float dt = 1f / 72f;
        p.Step(o, v, dt); // seed

        ProjectileWorldTrace wall = (start, end) =>
            start.X <= 10f && end.X >= 10f
                ? new ProjectileTraceHit(true, new Vector3(10f, 0f, 0f), new Vector3(-1f, 0f, 0f))
                : new ProjectileTraceHit(false, end, default);

        p.Step(o, v, dt, wall, bounce: true, bounceFactor: 1f);
        // v -= (1+1)(v·n)n = (1000,0,0) - 2*(-1000)*(-1,0,0) = (1000,0,0) - (2000,0,0) = (-1000,0,0)
        AssertVecEqual(new Vector3(-1000f, 0f, 0f), p.Velocity);
        Assert.Equal(1000f, p.Velocity.Length(), 2); // elastic: speed preserved
    }

    [Fact]
    public void RegularSnapshots_ProduceContinuousMotion_NoBackwardJerk()
    {
        // Snapshots arrive every server tick; the client renders at 2x that rate. Each rendered frame's X must
        // be monotonic and advance by a consistent amount — i.e. the snap-on-update aligns with the
        // extrapolation so there is no visible backward hitch.
        var p = new ProjectilePredictor();
        var vel = new Vector3(6000f, 0f, 0f);
        float tick = 1f / 72f;     // server snapshot interval
        float frame = tick / 2f;   // render twice per tick

        var serverOrigin = new Vector3(0f, 0f, 0f);
        p.Step(serverOrigin, vel, frame); // seed on first snapshot

        float prevX = p.Position.X;
        float serverTime = 0f;
        float clientTime = 0f;
        for (int f = 0; f < 12; f++)
        {
            clientTime += frame;
            // a new snapshot becomes available once the server has advanced a whole tick past clientTime's pair
            if (clientTime >= serverTime + tick)
            {
                serverTime += tick;
                serverOrigin += vel * tick; // server advanced one tick of straight-line travel
            }
            Vector3 pos = p.Step(serverOrigin, vel, frame);
            Assert.True(pos.X >= prevX - 1e-3f, $"frame {f}: backward jerk {prevX} -> {pos.X}");
            prevX = pos.X;
        }
        // After ~6 ticks of travel the bolt is far downrange (not lagging tens of units behind).
        Assert.True(prevX > 6000f * (5f * tick), $"under-traveled: {prevX}");
    }
}

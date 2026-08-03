using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Particles;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The per-frame BSP-work budgets in <see cref="ParticleSim.Update"/> (hitch fix 2026-08-03): the bounce
/// trace and the PointContents checks run under fair rotating rings (<see cref="ParticleSim.TraceBudgetPerFrame"/>
/// / <see cref="ParticleSim.ContentBudgetPerFrame"/>) with a 2× per-frame spend cap as the mega-burst
/// backstop. These tests pin the two properties the budgets must keep:
/// <list type="number">
///   <item><b>Bounded work</b> — a single-frame burst far above the budget does NOT get all its world queries
///   in one frame (the pre-fix behaviour was exactly that, at 400-840 ms a frame on stormkeep).</item>
///   <item><b>No starvation</b> — every particle's collision/content verdict still lands within a few ring
///   laps: budgeted particles die on the surfaces/contents they should, just a few frames late. The first
///   budget cut (a first-come spend counter) failed this — pool indexes above the budget starved forever.</item>
/// </list>
/// Parity with the C reference is NOT tested here — pools at-or-under the ring width are fully covered every
/// frame, which is what keeps <see cref="ParticleParityTests"/> bit-identical (their pools are far smaller).
/// </summary>
public class ParticleBudgetRingTests
{
    /// <summary>A world whose only brush is the half-space z &lt;= 0 (a solid floor).</summary>
    private static ParticleAnalyticWorld FloorWorld()
        => ParticleAnalyticWorld.FromBrushes(new List<(int, int, float[])>
        {
            (ParticleAnalyticWorld.ContSolid, 0, new float[] { 0f, 0f, 1f, 0f }), // inside when z <= 0
        });

    private static ParticleSim NewSim(ParticleAnalyticWorld world, out MutableClock clock)
    {
        clock = new MutableClock { Time = 0f };
        Api.Services = new ParticleTestServices(world, clock, collisions: true);
        // Pool above both ring widths so the budgets actually engage (guarded by the asserts below).
        return new ParticleSim(new XorShiftParticleRng(1234), initialCapacity: 8192, maxParticles: 8192);
    }

    private static List<ParticleEmitterInfo> OneBlock(ParticleEmitterInfo block) => new() { block };

    [Fact]
    public void TraceRing_NoStarvation_EveryImpactResolves()
    {
        const int Count = 3000;
        Assert.True(Count > ParticleSim.TraceBudgetPerFrame,
            "test premise: the burst must exceed the trace ring or nothing is being exercised");

        var sim = NewSim(FloorWorld(), out MutableClock clock);
        // Sparks that DIE on impact (Bounce < 0), falling fast onto the floor. With the ring at 512 and
        // 3000 live, each particle is traced every ~6 frames — an out-of-ring particle tunnels BELOW the
        // floor untraced, and only the LastTraced resume segment can still catch its crossing. So this also
        // pins the resume mechanism: if the budgeted trace ran oldorg→org instead, the tunnelled particles
        // would never see the floor and survive to the assert.
        sim.Update(0f);
        sim.SpawnEffect(OneBlock(new ParticleEmitterInfo
        {
            CountAbsolute = Count,
            Type = ParticleType.AlphaStatic,
            Bounce = -1f,                       // die on the surface it hits
            Gravity = 0f,
            TimeMin = 1e6f, TimeMax = 1e6f,     // no age-out: only the impact may kill them
            AlphaMin = 256f, AlphaMax = 256f, AlphaFade = 0f,
        }), pcount: 0f,
            originMins: new Vector3(-200f, -200f, 100f), originMaxs: new Vector3(200f, 200f, 120f),
            velocityMins: new Vector3(0f, 0f, -500f), velocityMaxs: new Vector3(0f, 0f, -500f));

        Assert.True(sim.HighWater >= Count, "spawn failed to fill the pool");

        // 100 qu of fall at 500 qu/s crosses the floor inside ~15 steps at 60 Hz; give the ring a generous
        // number of laps on top. Every particle must be gone — a starved particle lives forever here.
        for (int step = 1; step <= 120 && sim.LiveCount != 0; step++)
        {
            clock.Time = step / 60f;
            sim.Update(clock.Time);
        }
        Assert.Equal(0, sim.LiveCount);
    }

    [Fact]
    public void ContentRing_BoundsTheFrame_ThenKillsEveryone()
    {
        const int Count = 6000;
        Assert.True(Count > ParticleSim.ContentBudgetPerFrame * 2,
            "test premise: the burst must exceed even the backstop cap or the bounded-frame assert is vacuous");

        var sim = NewSim(FloorWorld(), out MutableClock clock);
        // Stationary BLOOD spawned INSIDE the solid floor. Velocity is zero, so the bounce trace never runs
        // (DP's own vel!=0 gate) — the ONLY thing that can kill these is the budgeted per-type PointContents
        // kill-check ("blood in solid dies"). LiquidFriction 0 keeps it to one content query per particle.
        sim.Update(0f);
        sim.SpawnEffect(OneBlock(new ParticleEmitterInfo
        {
            CountAbsolute = Count,
            Type = ParticleType.Blood,
            Bounce = 0f, Gravity = 0f, LiquidFriction = 0f,
            TimeMin = 1e6f, TimeMax = 1e6f,
            AlphaMin = 256f, AlphaMax = 256f, AlphaFade = 0f,
        }), pcount: 0f,
            originMins: new Vector3(-100f, -100f, -60f), originMaxs: new Vector3(100f, 100f, -40f),
            velocityMins: Vector3.Zero, velocityMaxs: Vector3.Zero);

        Assert.True(sim.HighWater >= Count, "spawn failed to fill the pool");

        // First Update after the burst: the ring modulus is last frame's count (0 ⇒ everyone in-ring), so
        // the 2× spend cap is what bounds the frame. If it didn't, all 6000 checks would run right here —
        // the exact unbounded frame this fix exists to prevent.
        clock.Time = 1 / 60f;
        sim.Update(clock.Time);
        Assert.True(sim.LiveCount > 0,
            $"the backstop cap did not bind: all {Count} content checks ran in one frame");

        // ...and within a few ring laps every blood particle inside the floor must be found and killed.
        for (int step = 2; step <= 30 && sim.LiveCount != 0; step++)
        {
            clock.Time = step / 60f;
            sim.Update(clock.Time);
        }
        Assert.Equal(0, sim.LiveCount);
    }
}

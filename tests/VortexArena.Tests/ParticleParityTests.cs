using System.Numerics;
using System.Text.Json;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Particles;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// PARTICLE-PARITY / GOLDEN-TRACE tests (planning/particles-dual-system.md §C.5). Each fixture in
/// <c>tests/XonoticGodot.Tests/golden/particles/*.json</c> was produced by <c>tools/particles-ref/particles_ref.c</c>
/// — an INDEPENDENT C reference transcribed line-for-line from Darkplaces' <c>cl_particles.c</c>
/// (CL_NewParticle / CL_NewParticlesFromEffectinfo spawn + R_DrawParticles update). The fixture records the
/// emitter block, the spawn inputs, the analytic collision world, the EXACT libc rand() draw sequence, and a
/// per-step per-particle [org,vel,size,alpha,active] dump.
///
/// We replay the recorded rand() stream through <see cref="RecordedParticleRng"/>, run the ported
/// <see cref="ParticleSim"/> over the same dt sequence against a bit-identical analytic trace world
/// (<see cref="ParticleAnalyticWorld"/>, a twin of the C trace), and assert the trajectory matches. A
/// divergence is then a pure math/port bug, never RNG drift or a collision-implementation artefact.
///
/// MUST-PASS (no-collision archetypes): jittered_burst, snow, trail_sweep + the distribution / accumulator /
/// perf checks. Collision archetypes (gravity_spark_bounce_plane, liquid_pool, blood_vs_wall) are best-effort.
/// </summary>
public class ParticleParityTests
{
    private readonly ITestOutputHelper _out;
    public ParticleParityTests(ITestOutputHelper o) => _out = o;

    // Componentwise tolerances. The C reference is float throughout and the C# sim uses System.Numerics.Vector3
    // (also float); the only divergence is last-ULP transcendentals (sin/cos/atan2 in the spawn basis). Kept
    // tight so a real port bug fails immediately.
    private const float PosTol = 1e-3f;
    private const float VelTol = 2e-3f;
    private const float SizeTol = 1e-3f;
    private const float AlphaTol = 0.05f;

    public static IEnumerable<object[]> NoCollisionScenarios() => new[]
    {
        new object[] { "jittered_burst" },
        new object[] { "snow" },
        new object[] { "trail_sweep" },
    };

    public static IEnumerable<object[]> CollisionScenarios() => new[]
    {
        new object[] { "gravity_spark_bounce_plane" },
        new object[] { "liquid_pool" },
        new object[] { "blood_vs_wall" },
    };

    [Theory]
    [MemberData(nameof(NoCollisionScenarios))]
    public void Port_Matches_Reference_NoCollision(string name) => RunScenario(name);

    // Collision archetypes: the analytic point-trace twin reproduces the C trace, so these should match too.
    // If a specific one proves fragile under transcendental drift at the impact instant, it can be moved to a
    // [Fact(Skip=...)] — but as written all three pass against the committed goldens.
    [Theory]
    [MemberData(nameof(CollisionScenarios))]
    public void Port_Matches_Reference_Collision(string name) => RunScenario(name);

    private void RunScenario(string name)
    {
        Fixture fx = LoadFixture(name);

        var world = ParticleAnalyticWorld.FromBrushes(fx.World);
        var clock = new MutableClock { Time = 0f };
        Api.Services = new ParticleTestServices(world, clock, fx.Collisions);

        var rng = new RecordedParticleRng(fx.Rng);
        var sim = new ParticleSim(rng, initialCapacity: 4096, maxParticles: 4096);

        // Spawn at t=0 (the C reference spawns once before any update).
        sim.SpawnEffect(fx.Blocks, fx.Pcount, fx.OriginMins, fx.OriginMaxs, fx.VelocityMins, fx.VelocityMaxs,
            tintRgba: 0xFFFFFFFFu, fade: fx.Fade);

        float worstPos = 0f, worstVel = 0f, worstSize = 0f, worstAlpha = 0f;
        int worstStep = -1;

        for (int s = 0; s < fx.Steps.Count; s++)
        {
            Step step = fx.Steps[s];
            clock.Time = step.Time;
            sim.Update(step.Time);

            // Gather the live particles in pool order, mirroring the C's [0, num_particles) scan that skips
            // delayed-spawn (active flag 0) entries but still emits them in the dump.
            var refParts = step.Particles;
            // The C dump lists every slot in [0, num_particles); we compare slot-by-slot. The active flag
            // tells us whether the slot is a live, post-delayedspawn particle.
            int n = System.Math.Min(refParts.Count, sim.HighWater);

            for (int i = 0; i < refParts.Count; i++)
            {
                RefParticle rp = refParts[i];
                bool simActive = i < sim.HighWater && sim.Pool[i].Active &&
                                 sim.Pool[i].DelayedSpawn <= step.Time + 1e-6f;

                Assert.True((rp.Active != 0) == simActive,
                    $"{name} step {s} slot {i}: active {(simActive ? 1 : 0)} != reference {rp.Active} " +
                    $"(simHigh={sim.HighWater})");

                if (rp.Active == 0) continue;

                Particle p = sim.Pool[i];
                float dOrg = (p.Org - rp.Org).Length();
                float dVel = (p.Vel - rp.Vel).Length();
                float dSize = System.MathF.Abs(p.Size - rp.Size);
                float dAlpha = System.MathF.Abs(p.Alpha - rp.Alpha);
                if (dOrg > worstPos) { worstPos = dOrg; worstStep = s; }
                if (dVel > worstVel) worstVel = dVel;
                if (dSize > worstSize) worstSize = dSize;
                if (dAlpha > worstAlpha) worstAlpha = dAlpha;

                Assert.True(dOrg <= PosTol,
                    $"{name} step {s} slot {i}: org {p.Org} != ref {rp.Org} (err {dOrg:E3})");
                Assert.True(dVel <= VelTol,
                    $"{name} step {s} slot {i}: vel {p.Vel} != ref {rp.Vel} (err {dVel:E3})");
                Assert.True(dSize <= SizeTol,
                    $"{name} step {s} slot {i}: size {p.Size} != ref {rp.Size} (err {dSize:E3})");
                Assert.True(dAlpha <= AlphaTol,
                    $"{name} step {s} slot {i}: alpha {p.Alpha} != ref {rp.Alpha} (err {dAlpha:E3})");
            }

            // No live C# particle may exist past the reference's high-water dump (the C reference shrinks
            // num_particles past trailing dead slots; the C# Update must shrink identically).
            for (int i = refParts.Count; i < sim.HighWater; i++)
                Assert.False(sim.Pool[i].Active && sim.Pool[i].DelayedSpawn <= step.Time + 1e-6f,
                    $"{name} step {s}: C# has a live particle at slot {i} beyond the reference high-water {refParts.Count}");
            _ = n;
        }

        // The reference records EXACTLY the draws it consumed (spawn + every update step). If the C# sim
        // drew a different number, either RecordedParticleRng already threw (over-draw) or a value diverged
        // (caught above); this final equality pins under-draw too — a strong RNG-order guard.
        Assert.True(rng.Consumed == fx.Rng.Length,
            $"{name}: C# consumed {rng.Consumed} rand() draws but the reference recorded {fx.Rng.Length} " +
            "(RNG-order divergence between spawn/update paths)");

        _out.WriteLine($"{name}: {fx.Steps.Count} steps, worst org {worstPos:E3} (step {worstStep}), " +
                       $"vel {worstVel:E3}, size {worstSize:E3}, alpha {worstAlpha:E3}, rng consumed {rng.Consumed}/{fx.Rng.Length}");
    }

    // ---------------------------------------------------------------------------------------------
    //  Distribution: VectorRandom ball jitter should be roughly uniform in the unit ball (radial).
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void BallJitter_Distribution_IsRoughlyUniform()
    {
        var world = new ParticleAnalyticWorld();
        Api.Services = new ParticleTestServices(world, new MutableClock(), collisions: false);

        var rng = new XorShiftParticleRng(0xC0FFEEu);
        const int N = 40000;
        // Radial histogram: shell volume scales with r^3, so for a uniform ball the fraction with radius < r
        // is r^3. Bin by cumulative count and compare to the analytic CDF.
        int[] bins = new int[10];
        for (int i = 0; i < N; i++)
        {
            Vector3 v = ParticleRandom.VectorRandom(rng);
            float r = v.Length();
            Assert.True(r <= 1.0001f, $"VectorRandom escaped the unit ball: |v|={r}");
            int b = System.Math.Min(9, (int)(r * 10f));
            bins[b]++;
        }
        // cumulative fraction at shell boundary k/10 should track (k/10)^3.
        int cum = 0;
        for (int k = 1; k <= 10; k++)
        {
            cum += bins[k - 1];
            float frac = (float)cum / N;
            float expect = System.MathF.Pow(k / 10f, 3f);
            Assert.True(System.MathF.Abs(frac - expect) < 0.05f,
                $"radial CDF at r={k / 10f}: {frac:F3} vs expected {expect:F3}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  Accumulator drain: 0.025 particles/call over 1000 calls -> ~25 particles total.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Accumulator_Drains_FractionalCounts()
    {
        var world = new ParticleAnalyticWorld();
        var clock = new MutableClock();
        Api.Services = new ParticleTestServices(world, clock, collisions: false);

        var sim = new ParticleSim(new XorShiftParticleRng(1u), 4096, 4096);
        var block = new ParticleEmitterInfo
        {
            Type = ParticleType.AlphaStatic,
            CountMultiplier = 0.025f,    // 0.025 particles per SpawnEffect call
            CountAbsolute = 0f,
            TimeMin = 1000f, TimeMax = 1000f,    // long-lived so none expire during the test
            AlphaMin = 256f, AlphaMax = 256f, AlphaFade = 0f,
            SizeMin = 1f, SizeMax = 1f,
        };
        var blocks = new[] { block };

        for (int i = 0; i < 1000; i++)
            sim.SpawnEffect(blocks, pcount: 1f, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);

        // 1000 * 0.025 = 25 exactly; accumulator drains whole particles so we expect 25 spawned.
        Assert.InRange(sim.LiveCountOrHighWater(), 23, 27);
    }

    // ---------------------------------------------------------------------------------------------
    //  Perf sanity: a 32k-particle Update completes. Loose bound (CI machines vary).
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void Update_32k_Particles_Completes()
    {
        var world = new ParticleAnalyticWorld();
        var clock = new MutableClock();
        Api.Services = new ParticleTestServices(world, clock, collisions: false);

        var sim = new ParticleSim(new XorShiftParticleRng(7u), 1 << 16, 1 << 16);
        var block = new ParticleEmitterInfo
        {
            Type = ParticleType.AlphaStatic,
            CountAbsolute = 32000f,
            TimeMin = 100f, TimeMax = 100f,
            AlphaMin = 256f, AlphaMax = 256f, AlphaFade = 1f,
            SizeMin = 2f, SizeMax = 2f,
            VelocityJitter = new Vector3(50f, 50f, 50f),
            Gravity = 1f, AirFriction = 1f,
        };
        sim.SpawnEffect(new[] { block }, 1f, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        float t = 0f;
        for (int frame = 0; frame < 100; frame++)
        {
            t += 0.0166f;
            clock.Time = t;
            sim.Update(t);
        }
        sw.Stop();
        _out.WriteLine($"32k-particle 100-frame Update took {sw.ElapsedMilliseconds} ms (high-water {sim.HighWater})");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"32k Update too slow: {sw.ElapsedMilliseconds} ms");
        Assert.True(sim.HighWater > 0);
    }

    // ---------------------------------------------------------------------------------------------
    //  fixture loading
    // ---------------------------------------------------------------------------------------------
    private static Fixture LoadFixture(string name)
    {
        string path = System.IO.Path.Combine(GoldenDir(), name + ".json");
        Assert.True(System.IO.File.Exists(path),
            $"golden particle fixture missing: {path} (run tools/particles-ref/particles_ref)");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        JsonElement r = doc.RootElement;

        bool collisions = r.GetProperty("collisions").GetInt32() != 0;

        var brushes = new List<(int contents, int surfaceflags, float[] planes)>();
        foreach (JsonElement b in r.GetProperty("world").EnumerateArray())
        {
            int contents = b.GetProperty("contents").GetInt32();
            int sflags = b.GetProperty("surfaceflags").GetInt32();
            var flat = new List<float>();
            foreach (JsonElement pl in b.GetProperty("planes").EnumerateArray())
                foreach (JsonElement v in pl.EnumerateArray())
                    flat.Add(v.GetSingle());
            brushes.Add((contents, sflags, flat.ToArray()));
        }

        var blocks = new List<ParticleEmitterInfo>();
        foreach (JsonElement b in r.GetProperty("blocks").EnumerateArray())
            blocks.Add(ParseBlock(b));

        JsonElement sp = r.GetProperty("spawn");
        float pcount = sp.GetProperty("pcount").GetSingle();
        float fade = sp.GetProperty("fade").GetSingle();
        Vector3 omin = Vec3(sp, "originmins"), omax = Vec3(sp, "originmaxs");
        Vector3 vmin = Vec3(sp, "velocitymins"), vmax = Vec3(sp, "velocitymaxs");

        var rngList = new List<int>();
        foreach (JsonElement v in r.GetProperty("rng").EnumerateArray())
            rngList.Add(v.GetInt32());

        var steps = new List<Step>();
        foreach (JsonElement st in r.GetProperty("steps").EnumerateArray())
        {
            float time = st.GetProperty("time").GetSingle();
            var parts = new List<RefParticle>();
            foreach (JsonElement p in st.GetProperty("particles").EnumerateArray())
            {
                // [active, ox,oy,oz, vx,vy,vz, size, alpha]
                int active = p[0].GetInt32();
                var org = new Vector3(p[1].GetSingle(), p[2].GetSingle(), p[3].GetSingle());
                var vel = new Vector3(p[4].GetSingle(), p[5].GetSingle(), p[6].GetSingle());
                float size = p[7].GetSingle();
                float alpha = p[8].GetSingle();
                parts.Add(new RefParticle(active, org, vel, size, alpha));
            }
            steps.Add(new Step(time, parts));
        }

        return new Fixture(collisions, brushes, blocks, pcount, fade, omin, omax, vmin, vmax,
            rngList.ToArray(), steps);
    }

    private static ParticleEmitterInfo ParseBlock(JsonElement b)
    {
        return new ParticleEmitterInfo
        {
            CountAbsolute = b.GetProperty("countabsolute").GetSingle(),
            CountMultiplier = b.GetProperty("countmultiplier").GetSingle(),
            TrailSpacing = b.GetProperty("trailspacing").GetSingle(),
            Type = (ParticleType)b.GetProperty("type").GetInt32(),
            Blend = (ParticleBlend)b.GetProperty("blend").GetInt32(),
            Orientation = (ParticleOrientation)b.GetProperty("orientation").GetInt32(),
            Color0 = (uint)b.GetProperty("color0").GetInt64(),
            Color1 = (uint)b.GetProperty("color1").GetInt64(),
            Tex0 = b.GetProperty("tex0").GetInt32(),
            Tex1 = b.GetProperty("tex1").GetInt32(),
            SizeMin = Arr(b, "size", 0), SizeMax = Arr(b, "size", 1), SizeIncrease = Arr(b, "size", 2),
            AlphaMin = Arr(b, "alpha", 0), AlphaMax = Arr(b, "alpha", 1), AlphaFade = Arr(b, "alpha", 2),
            TimeMin = Arr(b, "time", 0), TimeMax = Arr(b, "time", 1),
            Gravity = b.GetProperty("gravity").GetSingle(),
            Bounce = b.GetProperty("bounce").GetSingle(),
            AirFriction = b.GetProperty("airfriction").GetSingle(),
            LiquidFriction = b.GetProperty("liquidfriction").GetSingle(),
            StretchFactor = b.GetProperty("stretchfactor").GetSingle(),
            VelocityMultiplier = b.GetProperty("velocitymultiplier").GetSingle(),
            OriginOffset = Vec3(b, "originoffset"),
            RelativeOriginOffset = Vec3(b, "relativeoriginoffset"),
            VelocityOffset = Vec3(b, "velocityoffset"),
            RelativeVelocityOffset = Vec3(b, "relativevelocityoffset"),
            OriginJitter = Vec3(b, "originjitter"),
            VelocityJitter = Vec3(b, "velocityjitter"),
            StainColor0 = unchecked((uint)b.GetProperty("staincolor0").GetInt64()),
            StainColor1 = unchecked((uint)b.GetProperty("staincolor1").GetInt64()),
            StainTex0 = b.GetProperty("staintex0").GetInt32(),
            StainTex1 = b.GetProperty("staintex1").GetInt32(),
            StainAlphaMin = Arr(b, "stainalpha", 0), StainAlphaMax = Arr(b, "stainalpha", 1),
            StainSizeMin = Arr(b, "stainsize", 0), StainSizeMax = Arr(b, "stainsize", 1),
            RotateBaseMin = Arr(b, "rotate", 0), RotateBaseMax = Arr(b, "rotate", 1),
            RotateSpinMin = Arr(b, "rotate", 2), RotateSpinMax = Arr(b, "rotate", 3),
            Underwater = b.GetProperty("underwater").GetInt32() != 0,
            NotUnderwater = b.GetProperty("notunderwater").GetInt32() != 0,
        };
    }

    private static float Arr(JsonElement e, string prop, int idx) => e.GetProperty(prop)[idx].GetSingle();

    private static Vector3 Vec3(JsonElement e, string prop)
    {
        JsonElement a = e.GetProperty(prop);
        return new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "golden", "particles");

    private readonly record struct RefParticle(int Active, Vector3 Org, Vector3 Vel, float Size, float Alpha);
    private readonly record struct Step(float Time, List<RefParticle> Particles);
    private sealed record Fixture(
        bool Collisions,
        List<(int contents, int surfaceflags, float[] planes)> World,
        List<ParticleEmitterInfo> Blocks,
        float Pcount, float Fade,
        Vector3 OriginMins, Vector3 OriginMaxs, Vector3 VelocityMins, Vector3 VelocityMaxs,
        int[] Rng, List<Step> Steps);
}

/// <summary>Convenience accessor so the accumulator test can read the live count uniformly.</summary>
internal static class ParticleSimTestExtensions
{
    public static int LiveCountOrHighWater(this ParticleSim sim)
        => sim.LiveCount > 0 ? sim.LiveCount : sim.HighWater;
}

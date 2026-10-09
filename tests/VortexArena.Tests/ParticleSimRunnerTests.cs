using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Particles;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The particle work on its own thread (<see cref="ParticleSimRunner"/>) must leave the particles exactly as
/// doing it in place leaves them. Two simulations are fed the same script - spawns before each update and
/// spawns "during" it, which the runner records and applies ahead of the next update - against the same level,
/// one through a share of the collision world (<see cref="CollisionWorld.ShareForThread"/>). After every frame
/// the two pools are compared byte for byte and the marks and beams raised are compared in order.
/// </summary>
public class ParticleSimRunnerTests
{
    private static ParticleEmitterInfo[] Blocks() => new[]
    {
        // Sparks that bounce off the floor and the wall and leave a mark where they hit.
        new ParticleEmitterInfo
        {
            Type = ParticleType.Spark, Orientation = ParticleOrientation.Spark, Blend = ParticleBlend.Add,
            CountMultiplier = 1f, TimeMin = 0.5f, TimeMax = 3f, AlphaMin = 200f, AlphaMax = 256f, AlphaFade = 60f,
            SizeMin = 1f, SizeMax = 2f, Gravity = 1f, Bounce = 1.5f, AirFriction = 0.2f, LiquidFriction = 4f, StretchFactor = 2f,
            Color0 = 0xFF8020, Color1 = 0xFFFF80, Tex0 = 40, Tex1 = 44, StainTex0 = 16, StainTex1 = 24,
            StainColor0 = 0x808080, StainColor1 = 0x404040, StainSizeMin = 1f, StainSizeMax = 2f, StainAlphaMin = 0.5f, StainAlphaMax = 1f,
            VelocityJitter = new Vector3(300f, 300f, 300f), OriginJitter = new Vector3(4f, 4f, 4f),
        },
        // Blood: dies on the first surface and leaves a blood mark.
        new ParticleEmitterInfo
        {
            Type = ParticleType.Blood, Blend = ParticleBlend.InvMod, CountMultiplier = 0.5f, TimeMin = 2f, TimeMax = 4f,
            AlphaMin = 156f, AlphaMax = 256f, AlphaFade = 20f, SizeMin = 4f, SizeMax = 8f, Gravity = 1f, Bounce = -1f,
            LiquidFriction = 4f, Color0 = 0xA8FFFF, Color1 = 0xA8FFFF, Tex0 = 24, Tex1 = 32, StainTex0 = -1, StainTex1 = -1,
            StainColor0 = unchecked((uint)-1), StainColor1 = unchecked((uint)-1), StainSizeMin = 1f, StainSizeMax = 1f, StainAlphaMin = 1f, StainAlphaMax = 1f,
            VelocityJitter = new Vector3(200f, 200f, 200f),
        },
        // Smoke that only rises and fades.
        new ParticleEmitterInfo
        {
            Type = ParticleType.Smoke, Blend = ParticleBlend.Alpha, CountAbsolute = 0.4f, CountMultiplier = 0.25f, TimeMin = 1f, TimeMax = 2f,
            AlphaMin = 100f, AlphaMax = 200f, AlphaFade = 150f, SizeMin = 4f, SizeMax = 9f, SizeIncrease = 12f, Gravity = -0.1f,
            Color0 = 0x202020, Color1 = 0x606060, Tex0 = 0, Tex1 = 8, StainTex0 = -1, StainTex1 = -1,
            RotateBaseMin = 0f, RotateBaseMax = 360f, RotateSpinMin = -40f, RotateSpinMax = 40f,
            VelocityOffset = new Vector3(0f, 0f, 30f), VelocityJitter = new Vector3(10f, 10f, 10f), OriginJitter = new Vector3(8f, 8f, 8f),
        },
        // A beam, raised to the host when the effect is asked for as a trail.
        new ParticleEmitterInfo
        {
            Type = ParticleType.Static, Orientation = ParticleOrientation.Beam, Blend = ParticleBlend.Add, CountAbsolute = 1f,
            TimeMin = 0.2f, TimeMax = 0.2f, AlphaMin = 128f, AlphaMax = 128f, AlphaFade = 512f, SizeMin = 2f, SizeMax = 2f,
            Color0 = 0x40C0FF, Color1 = 0x40C0FF, Tex0 = 200, Tex1 = 200, StainTex0 = -1, StainTex1 = -1,
        },
    };

    private static CollisionWorld Level()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-2048f, -2048f, -64f), new Vector3(2048f, 2048f, 0f), SuperContents.Solid));
        world.AddBrush(Brush.FromBox(new Vector3(256f, -2048f, 0f), new Vector3(320f, 2048f, 512f), SuperContents.Solid));
        world.AddBrush(Brush.FromBox(new Vector3(-2048f, 300f, 0f), new Vector3(2048f, 364f, 512f), SuperContents.Solid));
        world.AddBrush(Brush.FromBox(new Vector3(-600f, -600f, 0f), new Vector3(-200f, -200f, 96f), SuperContents.Water));
        return world;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_run_through_the_runner_leaves_the_particles_a_run_in_place_leaves(bool threaded, bool bih)
    {
        ICvarService cvars = new ParticleTestServices(new ParticleAnalyticWorld(), new MutableClock(), collisions: true).Cvars;
        CollisionWorld world = Level();
        world.UseBih = bih;

        var direct = new ParticleSim(new XorShiftParticleRng(99u), 256, 4096) { Cvars = cvars, Trace = new TraceService(world) };
        var through = new ParticleSim(new XorShiftParticleRng(99u), 256, 4096) { Cvars = cvars, Trace = new TraceService(world.ShareForThread()) };
        using var runner = new ParticleSimRunner(through) { Threaded = threaded };

        var directEvents = new List<string>();
        var throughEvents = new List<string>();
        int owner = Environment.CurrentManagedThreadId, wrongThread = 0;
        direct.OnStain = e => directEvents.Add(Describe(e));
        direct.OnBeam = e => directEvents.Add(Describe(e));
        runner.OnStain = e => { if (Environment.CurrentManagedThreadId != owner) Interlocked.Increment(ref wrongThread); throughEvents.Add(Describe(e)); };
        runner.OnBeam = e => { if (Environment.CurrentManagedThreadId != owner) Interlocked.Increment(ref wrongThread); throughEvents.Add(Describe(e)); };

        // Each simulation drains its own copy of the blocks (a block carries the fraction of a particle left over).
        ParticleEmitterInfo[] directBlocks = Blocks(), throughBlocks = Blocks();
        var script = new Random(4242);
        float time = 0f;
        int queued = 0, stains = 0, peak = 0;
        var later = new List<(Vector3 Origin, Vector3 End, Vector3 Velocity, float Count, uint Tint, bool Trail)>();

        (Vector3 Origin, Vector3 End, Vector3 Velocity, float Count, uint Tint, bool Trail) Next()
        {
            var origin = new Vector3(script.Next(-700, 240), script.Next(-700, 280), script.Next(8, 200));
            var velocity = new Vector3(script.Next(-200, 200), script.Next(-200, 200), script.Next(-50, 300));
            float count = (float)(script.NextDouble() * 12);
            bool trail = script.Next(4) == 0;
            Vector3 end = trail ? origin + new Vector3(script.Next(-80, 80), script.Next(-80, 80), script.Next(-20, 60)) : origin;
            uint tint = script.Next(5) == 0 ? 0x80FF40C0u : 0xFFFFFFFFu;
            return (origin, end, velocity, count, tint, trail);
        }

        void Compare(string when)
        {
            Assert.Equal(direct.HighWater, through.HighWater);
            Assert.Equal(direct.LiveCount, through.LiveCount);
            ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(direct.Pool.AsSpan(0, direct.HighWater));
            ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(through.Pool.AsSpan(0, through.HighWater));
            Assert.True(a.SequenceEqual(b), "the pools differ " + when);
            Assert.Equal(directEvents, throughEvents);
        }

        for (int frame = 0; frame < 600; frame++)
        {
            // The frame's spawns that come before its update.
            for (int i = script.Next(0, 4); i > 0; i--)
            {
                var s = Next();
                direct.SpawnEffect(directBlocks, s.Count, s.Origin, s.End, s.Velocity, s.Velocity, s.Tint, 1f, s.Trail);
                runner.Spawn(throughBlocks, s.Count, s.Origin, s.End, s.Velocity, s.Velocity, s.Tint, 1f, s.Trail);
            }
            time += 0.004f + (float)script.NextDouble() * 0.02f;

            direct.Update(time);
            runner.Begin(time, through.ReadUpdateSettings());
            // What the frame's later nodes ask for while the worker runs. In place these come after the update;
            // the runner records them behind it. The in-place twin gets them once the two have been compared.
            later.Clear();
            for (int i = script.Next(0, 4); i > 0; i--)
            {
                var s = Next();
                // (Not threaded, the runner applies each at its call, as in place: the twin gets it at once too.)
                if (threaded) later.Add(s);
                else direct.SpawnEffect(directBlocks, s.Count, s.Origin, s.End, s.Velocity, s.Velocity, s.Tint, 1f, s.Trail);
                if (runner.InFlight) queued++;
                runner.Spawn(throughBlocks, s.Count, s.Origin, s.End, s.Velocity, s.Velocity, s.Tint, 1f, s.Trail);
            }
            runner.Join();
            Compare($"after the update of frame {frame}");
            foreach (var s in later)
                direct.SpawnEffect(directBlocks, s.Count, s.Origin, s.End, s.Velocity, s.Velocity, s.Tint, 1f, s.Trail);
            // Now and then something reads the simulation between frames: the recorded spawns are applied for it.
            if (frame % 37 == 36)
            {
                runner.Flush();
                Compare($"after the flush of frame {frame}");
            }
            peak = Math.Max(peak, direct.HighWater);
        }

        runner.Flush();
        Compare("at the end");
        Assert.Equal(0, wrongThread);
        foreach (string e in directEvents)
            if (e[0] == 'S') stains++;
        // The script did exercise what it is meant to: particles alive, marks raised, spawns kept for the join.
        Assert.True(peak > 200, $"only {peak} particles at the peak");
        Assert.True(stains > 50, $"only {stains} marks");
        Assert.True(directEvents.Count > stains, "no beam was raised");
        if (threaded) Assert.True(queued > 100, $"only {queued} spawns were asked for during an update");
    }

    [Fact]
    public void A_shared_world_answers_as_the_world_it_was_shared_from()
    {
        foreach (bool bih in new[] { false, true })
        {
            CollisionWorld world = Level();
            world.UseBih = bih;
            var a = new TraceService(world);
            var b = new TraceService(world.ShareForThread());
            var random = new Random(17);
            for (int i = 0; i < 4000; i++)
            {
                var from = new Vector3(random.Next(-900, 400), random.Next(-900, 400), random.Next(-100, 300));
                var to = from + new Vector3(random.Next(-600, 600), random.Next(-600, 600), random.Next(-400, 400));
                TraceResult ra = a.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.NoMonsters, null);
                TraceResult rb = b.Trace(from, Vector3.Zero, Vector3.Zero, to, MoveFilter.NoMonsters, null);
                Assert.Equal(ra.Fraction, rb.Fraction);
                Assert.Equal(ra.EndPos, rb.EndPos);
                Assert.Equal(ra.PlaneNormal, rb.PlaneNormal);
                Assert.Equal(ra.StartSolid, rb.StartSolid);
                Assert.Equal(ra.DpHitContents, rb.DpHitContents);
                Assert.Equal(a.PointContents(to), b.PointContents(to));
            }
        }
    }

    [Fact]
    public void A_failure_on_the_worker_reaches_the_owner_at_the_join()
    {
        ICvarService cvars = new ParticleTestServices(new ParticleAnalyticWorld(), new MutableClock(), collisions: false).Cvars;
        var sim = new ParticleSim(new XorShiftParticleRng(1u), 64, 64) { Cvars = cvars, Trace = new TraceService(Level()) };
        using var runner = new ParticleSimRunner(sim);
        runner.Begin(0.1f, sim.ReadUpdateSettings(), () => throw new InvalidOperationException("pack failed"));
        Assert.Throws<InvalidOperationException>(() => runner.Join());
        // And the runner goes on working afterwards.
        runner.Begin(0.2f, sim.ReadUpdateSettings());
        Assert.True(runner.Join());
        Assert.False(runner.Join());
    }

    private static string Describe(in StainEvent e) => FormattableString.Invariant(
        $"S {e.Org.X:R} {e.Org.Y:R} {e.Org.Z:R} {e.Dir.X:R} {e.Dir.Y:R} {e.Dir.Z:R} {e.ColorR} {e.ColorG} {e.ColorB} {e.Size:R} {e.Alpha:R} {e.TexNum} {e.IsBlood} {e.Projected} {e.MaxDist:R}");

    private static string Describe(in BeamEvent e) => FormattableString.Invariant(
        $"B {e.Start.X:R} {e.Start.Y:R} {e.Start.Z:R} {e.End.X:R} {e.End.Y:R} {e.End.Z:R} {e.Size:R} {e.Alpha:R} {e.AlphaFade:R} {e.Lifetime:R} {e.ColorR} {e.ColorG} {e.ColorB} {e.TexNum}");
}

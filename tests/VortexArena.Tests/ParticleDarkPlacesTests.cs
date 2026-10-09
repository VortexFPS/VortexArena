using System.Globalization;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Particles;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// The particle simulation held against DarkPlaces on the game's REAL effects.
///
/// <para><see cref="DpRef"/> below is a transcription of the three DarkPlaces functions that decide where
/// a particle is, how big and how opaque, and what quad a spark draws — <c>CL_NewParticlesFromEffectinfo</c>
/// and <c>CL_NewParticle</c> (spawn), the integration loop of <c>R_DrawParticles</c> (per frame), and the
/// <c>PARTICLE_SPARK</c> case of <c>R_DrawParticle_TransparentCallback</c> with <c>R_CalcBeam_Vertex3f</c>
/// (the quad) — written from <c>Base/darkplaces/cl_particles.c</c> and <c>gl_rmain.c</c> and sharing nothing
/// with <see cref="ParticleSim"/>: its own effectinfo reader, its own pool, its own maths. Both are fed the
/// same <c>rand()</c> stream and stepped over the same frame times; every live particle's position,
/// velocity, size and alpha and every spark's four corners are then compared slot by slot.</para>
///
/// <para>The older <see cref="ParticleParityTests"/> do the same against a compiled C reference, on six
/// hand-written emitter blocks. These run the effects the game actually plays — stormkeep's map emitter
/// with its exact entity keys, the weapon impacts, one effect for every particle <c>type</c> the shipped
/// effectinfo.txt uses — and both ways of asking for an effect (as a point/box and as a trail).</para>
///
/// Needs the Xonotic reference checkout (<c>../Base</c>) for effectinfo.txt; without it the cases return.
/// </summary>
public class ParticleDarkPlacesTests
{
    private readonly ITestOutputHelper _out;
    public ParticleDarkPlacesTests(ITestOutputHelper o) => _out = o;

    private static string EffectInfoPath => Path.Combine(TestPaths.BaseCorePk3Dir, "effectinfo.txt");

    // A floor slab under the spawn point and a pool of water beside it (the analytic world of the older
    // parity tests): sparks bounce off the floor, blood dies on it, bubbles live in the pool.
    private static ParticleAnalyticWorld World(bool water)
    {
        var brushes = new List<(int, int, float[])>
        {
            (ParticleAnalyticWorld.ContSolid, 0, Box(-4096, -4096, -64, 4096, 4096, 0)),
            (ParticleAnalyticWorld.ContSolid, 0, Box(96, -4096, 0, 160, 4096, 512)),      // a wall at x = 96
        };
        if (water)
            brushes.Add((ParticleAnalyticWorld.ContWater, 0, Box(-512, -512, 0, 90, 512, 256)));
        return ParticleAnalyticWorld.FromBrushes(brushes);
    }

    private static float[] Box(float x0, float y0, float z0, float x1, float y1, float z1) => new[]
    {
        1f, 0, 0, x1,   -1f, 0, 0, -x0,
        0, 1f, 0, y1,   0, -1f, 0, -y0,
        0, 0, 1f, z1,   0, 0, -1f, -z0,
    };

    public sealed record Case(string Name, string Effect, float Count, Vector3 Mins, Vector3 Maxs,
        Vector3 VelMins, Vector3 VelMaxs, bool WantTrail, bool Water = false, int Calls = 1)
    {
        public override string ToString() => Name;
    }

    private static readonly Vector3 P = new(0, 0, 48);

    public static IEnumerable<object[]> Cases() => new[]
    {
        // The stormkeep map emitter: func_pointparticles "mdl" "sparks" "count" "6" "velocity" "50 -100 30".
        new Case("stormkeep sparks emitter (type spark)", "sparks", 6, P, P, new(50, -100, 30), new(50, -100, 30), false),
        // Weapon impacts and explosions.
        new Case("machinegun_impact", "machinegun_impact", 1, P, P, new(0, 0, 300), new(0, 0, 300), false),
        new Case("shotgun_impact", "shotgun_impact", 1, P, P, new(-200, 0, 100), new(-200, 0, 100), false),
        new Case("electro_impact", "electro_impact", 1, P, P, default, default, false),
        new Case("crylink_impact", "crylink_impact", 1, P, P, new(0, 100, 0), new(0, 100, 0), false),
        new Case("rocket_explode", "rocket_explode", 1, P, P, default, default, false),
        new Case("grenade_explode", "grenade_explode", 1, P, P, default, default, false),
        // One effect for each particle type the shipped file uses that the cases above do not reach.
        new Case("steam (type smoke, map emitter)", "steam", 1, P, P, new(0, 0, 60), new(0, 0, 60), false),
        new Case("item_despawn (type snow)", "item_despawn", 1, P, P, default, default, false),
        new Case("blood, twelve calls (type blood, fraction carried between calls)", "blood", 0.6f, P, P,
            new(120, 0, 40), new(120, 0, 40), false, Calls: 12),
        new Case("rocket_explode under water (type bubble)", "rocket_explode", 1, P, P, default, default, false, Water: true),
        new Case("fountain01 (type snow)", "fountain01", 1, P, P, new(0, 0, 200), new(0, 0, 200), false),
        new Case("smoking_smallemitter (type alphastatic)", "smoking_smallemitter", 1, P, P, default, default, false),
        // A box, as boxparticles / a volume emitter asks for one.
        new Case("sparks in a box", "sparks", 2, new(-40, -40, 30), new(40, 40, 90), new(-10, -10, 0), new(10, 10, 50), false),
        // Trails: a projectile's per-frame segment (trailspacing blocks), a trail effect asked for as a
        // point, a count effect asked for as a trail, and a trail with a beam block (the bullet tracer).
        new Case("TR_ROCKET trail segment", "TR_ROCKET", 1, new(-60, 0, 80), new(40, 30, 100), new(900, 270, 180), new(900, 270, 180), true),
        new Case("TR_ROCKET asked for as a point", "TR_ROCKET", 1, P, P, new(900, 0, 0), new(900, 0, 0), false),
        new Case("TR_ROCKET asked for as a box", "TR_ROCKET", 1, new(-60, 0, 80), new(40, 30, 100), default, default, false),
        new Case("sparks asked for as a trail", "sparks", 1, new(-60, 0, 80), new(40, 30, 100), new(50, 0, 0), new(50, 0, 0), true),
        new Case("tr_bullet trail (beam + smoke)", "tr_bullet", 1, new(-80, -20, 60), new(80, 20, 70), default, default, true),
        new Case("tr_bullet asked for as a point", "tr_bullet", 1, P, P, default, default, false),
        new Case("TR_NEXUIZPLASMA trail (type snow in a trail)", "TR_NEXUIZPLASMA", 1, new(-60, 0, 80), new(40, 30, 100), new(700, 200, 0), new(700, 200, 0), true),
    }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Simulation_Matches_DarkPlaces(Case c)
    {
        if (!File.Exists(EffectInfoPath))
            return;   // no reference checkout on this machine
        List<DpRef.Info> blocks = DpRef.ParseEffectInfo(File.ReadAllText(EffectInfoPath), c.Effect);
        Assert.NotEmpty(blocks);

        ParticleAnalyticWorld world = World(c.Water);
        var clock = new MutableClock { Time = 0f };
        Api.Services = new ParticleTestServices(world, clock, collisions: true);
        int bloodStain = (int)Api.Cvars.GetFloat(ParticleCvars.DecalsImmediateBloodStain);

        // One rand() stream, two readers.
        var seed = new Random(c.Name.Length * 7919 + 12345);
        int[] stream = new int[1 << 20];
        for (int i = 0; i < stream.Length; i++)
            stream[i] = seed.Next();   // [0, 2^31-1): the range of glibc rand()

        var sim = new ParticleSim(new RecordedParticleRng(stream), initialCapacity: 16384, maxParticles: 16384);
        ParticleEmitterInfo[] ours = blocks.Select(ToOurs).ToArray();
        int ourDecals = 0;
        var ourBeams = new List<BeamEvent>();
        sim.OnStain += ev => { if (ev.Projected) ourDecals++; };
        sim.OnBeam += ev => ourBeams.Add(ev);

        var dp = new DpRef(stream, world) { ImmediateBloodStain = bloodStain };

        // Frame times the way a client produces them: uneven, from a 250 fps frame to a 30 fps one.
        float[] dts = { 1f / 250f, 1f / 144f, 1f / 60f, 1f / 30f, 1f / 125f, 1f / 72f };
        var viewOrigin = new Vector3(-260, -140, 110);
        float time = 0.5f;
        sim.Update(time);
        dp.Time = time;
        dp.Update();

        float worstPos = 0, worstVel = 0, worstSize = 0, worstAlpha = 0, worstCorner = 0, worstCornerRel = 0;
        int compared = 0, sparks = 0, peak = 0, step = 0;
        int callsLeft = c.Calls;
        while (step < 240)
        {
            if (callsLeft > 0)
            {
                callsLeft--;
                sim.SpawnEffect(ours, c.Count, c.Mins, c.Maxs, c.VelMins, c.VelMaxs, wantTrail: c.WantTrail);
                dp.NewParticlesFromEffectinfo(blocks, c.Count, c.Mins, c.Maxs, c.VelMins, c.VelMaxs, 1f, c.WantTrail);
            }

            time += dts[step % dts.Length];
            step++;
            clock.Time = time;
            sim.Update(time);
            dp.Time = time;
            dp.Update();

            Assert.True(sim.HighWater == dp.NumParticles,
                $"{c.Name} step {step}: pool reaches {sim.HighWater}, DarkPlaces {dp.NumParticles}");
            int live = 0;
            for (int i = 0; i < dp.NumParticles; i++)
            {
                DpRef.Part r = dp.Particles[i];
                Particle p = sim.Pool[i];
                Assert.True(p.Active == (r.TypeIndex != 0),
                    $"{c.Name} step {step} slot {i}: live {p.Active}, DarkPlaces type {r.TypeIndex}");
                if (r.TypeIndex == 0)
                    continue;
                live++;
                compared++;
                float dPos = (p.Org - r.Org).Length(), dVel = (p.Vel - r.Vel).Length();
                float dSize = MathF.Abs(p.Size - r.Size), dAlpha = MathF.Abs(p.Alpha - r.Alpha);
                worstPos = MathF.Max(worstPos, dPos);
                worstVel = MathF.Max(worstVel, dVel);
                worstSize = MathF.Max(worstSize, dSize);
                worstAlpha = MathF.Max(worstAlpha, dAlpha);
                Assert.True(dPos <= 2e-2f, $"{c.Name} step {step} slot {i}: org {p.Org} vs DarkPlaces {r.Org}");
                Assert.True(dVel <= 5e-2f, $"{c.Name} step {step} slot {i}: vel {p.Vel} vs DarkPlaces {r.Vel}");
                Assert.True(dSize <= 1e-3f, $"{c.Name} step {step} slot {i}: size {p.Size} vs DarkPlaces {r.Size}");
                Assert.True(dAlpha <= 5e-2f, $"{c.Name} step {step} slot {i}: alpha {p.Alpha} vs DarkPlaces {r.Alpha}");
                Assert.Equal(r.Die, p.Die, 3);
                Assert.Equal(r.TexNum, p.TexNum);
                Assert.Equal(r.Stretch, p.Stretch, 5);
                Assert.Equal((int)p.TypeIndex + 1, r.TypeIndex);
                Assert.Equal((int)p.Orientation, r.Orientation);

                // The spark's quad, when it is in view (DarkPlaces queues only what passes its own near plane).
                if (r.Orientation == DpRef.Spark && r.DelayedSpawn <= time)
                {
                    Vector3[] q = DpRef.SparkQuad(r, viewOrigin);
                    bool ok = ParticleGeometry.SparkAxes(p.Org, p.Vel, p.Size, p.Stretch, viewOrigin,
                        out Vector3 along, out Vector3 across);
                    if (ok)
                    {
                        // DarkPlaces: v0 = tail + right, v1 = tail − right, v2 = head − right, v3 = head + right.
                        Vector3[] mine =
                        {
                            p.Org - along + across, p.Org - along - across,
                            p.Org + along - across, p.Org + along + across,
                        };
                        for (int k = 0; k < 4; k++)
                        {
                            float d = (mine[k] - q[k]).Length();
                            worstCorner = MathF.Max(worstCorner, d);
                            worstCornerRel = MathF.Max(worstCornerRel, d / MathF.Max(r.Size, 1e-3f));
                        }
                        sparks++;
                    }
                }
            }
            peak = Math.Max(peak, live);
            if (callsLeft == 0 && dp.NumParticles == 0 && step > 12)
                break;
        }

        Assert.Equal(dp.Decals, ourDecals);
        Assert.Equal(dp.Beams.Count, ourBeams.Count);
        for (int i = 0; i < ourBeams.Count; i++)
        {
            DpRef.Part b = dp.Beams[i];
            Assert.True((ourBeams[i].Start - b.Org).Length() <= 1e-3f && (ourBeams[i].End - b.Vel).Length() <= 1e-3f,
                $"{c.Name}: beam {ourBeams[i].Start}..{ourBeams[i].End} vs DarkPlaces {b.Org}..{b.Vel}");
            Assert.Equal(b.Size, ourBeams[i].Size, 4);
            Assert.Equal(b.Alpha, ourBeams[i].Alpha, 2);
            Assert.Equal(b.AlphaFade, ourBeams[i].AlphaFade, 2);
        }
        Assert.True(peak > 0 || dp.Decals > 0 || dp.Beams.Count > 0 || !c.WantTrail,
            $"{c.Name}: nothing was spawned on either side");
        // The quad's width axis is taken at the spark's centre rather than at each end (one instanced quad
        // holds one): the corners may differ from DarkPlaces' by a small part of the spark's own half-width.
        Assert.True(worstCornerRel <= 0.15f, $"{c.Name}: spark corner off by {worstCornerRel:P1} of its half-width");

        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{c.Name}: {blocks.Count} blocks, peak {peak} live, {compared} particle-frames over {step} frames, " +
            $"decals {dp.Decals}, beams {dp.Beams.Count}; worst |org| {worstPos:E2} qu, |vel| {worstVel:E2} qu/s, " +
            $"size {worstSize:E2}, alpha {worstAlpha:E2}; {sparks} spark quads, worst corner {worstCorner:E2} qu " +
            $"({worstCornerRel:P2} of the half-width)"));
    }

    /// <summary>
    /// What the spark quad was before: its width axis came from the camera's FORWARD vector instead of the
    /// direction to the eye. Kept as a measurement, so the size of that error stays on record: for sparks
    /// spread over a 90° field of view it reports how far the old corners sat from DarkPlaces' and how much
    /// narrower the old quad was to the eye.
    /// </summary>
    [Fact]
    public void Spark_Quad_Faces_The_Eye_Not_The_View_Axis()
    {
        var rng = new Random(4242);
        var eye = new Vector3(0, 0, 64);
        var forward = new Vector3(1, 0, 0);
        float worstNew = 0, worstOld = 0, worstOldWidthLoss = 0;
        for (int n = 0; n < 4000; n++)
        {
            // a spark somewhere in a 90° cone in front of the eye, flying any way
            var dir = Vector3.Normalize(new Vector3(1f, (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)));
            var part = new DpRef.Part
            {
                Org = eye + dir * (float)(200 + rng.NextDouble() * 600),
                Vel = PointParticleEmitter.RandomVec(rng) * 400f,
                Size = (float)(1 + rng.NextDouble() * 2),
                Stretch = 1f,
            };
            if (part.Vel.Length() < 20f)
                continue;
            Vector3[] q = DpRef.SparkQuad(part, eye);

            Assert.True(ParticleGeometry.SparkAxes(part.Org, part.Vel, part.Size, part.Stretch, eye,
                out Vector3 along, out Vector3 across));
            worstNew = MathF.Max(worstNew, ((part.Org - along + across) - q[0]).Length() / part.Size);

            // the old formula: across = normalize(vel × viewForward) · size
            Vector3 oldAcross = Vector3.Cross(Vector3.Normalize(part.Vel), forward);
            if (oldAcross.Length() < 1e-3f)
                continue;
            oldAcross = Vector3.Normalize(oldAcross) * part.Size;
            if (Vector3.Dot(oldAcross, across) < 0) oldAcross = -oldAcross;
            worstOld = MathF.Max(worstOld, ((part.Org - along + oldAcross) - q[0]).Length() / part.Size);
            // how much of the old half-width the eye actually saw: its part perpendicular to the sight line
            Vector3 sight = Vector3.Normalize(part.Org - eye);
            float seen = (oldAcross - sight * Vector3.Dot(oldAcross, sight)).Length() / part.Size;
            worstOldWidthLoss = MathF.Max(worstOldWidthLoss, 1f - seen);
        }
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"spark quad corner error against DarkPlaces, as a part of the spark's half-width: now {worstNew:P2}, " +
            $"with the old view-axis formula up to {worstOld:P0}; the old quad lost up to {worstOldWidthLoss:P0} of its width to the eye"));
        Assert.True(worstNew <= 0.12f);
        Assert.True(worstOld > 0.5f, "the old formula is expected to be visibly wrong; if not, this measurement is broken");
    }

    // ------------------------------------------------------------------------------------------------
    //  The map emitter (Draw_PointParticles)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Stormkeep's emitter: impulse 4 means four BURSTS a second on average, each at a point of
    /// the brush — whatever the frame rate — and not a stream of single particles.</summary>
    [Theory]
    [InlineData(1f / 30f)]
    [InlineData(1f / 144f)]
    [InlineData(1f / 500f)]
    public void Map_Emitter_Emits_Impulse_Bursts_Per_Second_At_Any_Frame_Rate(float frametime)
    {
        var em = new PointParticleEmitter { BoxMin = new(-1048, 1458, 230), BoxMax = new(-952, 1606, 290) };
        em.Configure(mapImpulse: 4f, impulseSpawnflag: false);
        var rng = new Random(99);
        var pts = new List<Vector3>();
        int emissions = 0, frames = (int)(600f / frametime);   // ten minutes
        for (int f = 0; f < frames; f++)
            emissions += em.Step(frametime, rng, pts);
        float perSecond = emissions / (frames * frametime);
        _out.WriteLine($"frametime {frametime:0.0000}: {perSecond:0.000} emissions a second");
        Assert.InRange(perSecond, 3.8f, 4.2f);
    }

    [Fact]
    public void Map_Emitter_Takes_Its_Points_From_The_Brush_Not_Its_Bounding_Box()
    {
        // A slab tilted inside its box: the half-space z <= y fills half of the unit-ish box below.
        var slab = new Brush(new[]
        {
            new BrushPlane(new Vector3(1, 0, 0), 64), new BrushPlane(new Vector3(-1, 0, 0), 0),
            new BrushPlane(new Vector3(0, 1, 0), 64), new BrushPlane(new Vector3(0, -1, 0), 0),
            new BrushPlane(new Vector3(0, 0, 1), 64), new BrushPlane(new Vector3(0, 0, -1), 0),
            new BrushPlane(Vector3.Normalize(new Vector3(0, -1, 1)), 0),
        }, Array.Empty<Vector3>(), Array.Empty<Vector3>(), contents: 1);
        var origin = new Vector3(1000, 2000, 300);
        var em = new PointParticleEmitter
        {
            BoxMin = origin, BoxMax = origin + new Vector3(64, 64, 64), Brushes = new[] { slab }, BrushOrigin = origin,
        };
        var rng = new Random(5);
        var pts = new List<Vector3>();

        // absolute: a miss is retried, so the rate is kept and every point is inside
        em.Configure(mapImpulse: 50f, impulseSpawnflag: false);
        int n = 0;
        for (int f = 0; f < 6000; f++)
        {
            n += em.Step(1f / 60f, rng, pts);
            foreach (Vector3 p in pts)
                Assert.True(p.Z - origin.Z <= p.Y - origin.Y + 1e-3f, $"point {p - origin} is outside the slab");
        }
        Assert.InRange(n / 100f, 48f, 52f);

        // relative (negative impulse = density per 64³): a miss is dropped, so half the box gives half the rate
        em.Configure(mapImpulse: -50f, impulseSpawnflag: false);
        Assert.Equal(PointParticleEmitter.Mode.Relative, em.Absolute);
        Assert.Equal(50f, em.Impulse, 3);
        n = 0;
        for (int f = 0; f < 6000; f++)
            n += em.Step(1f / 60f, rng, pts);
        Assert.InRange(n / 100f, 23f, 27f);
    }

    [Fact]
    public void Map_Emitter_With_The_Impulse_Spawnflag_Fires_Only_When_Switched_On()
    {
        var em = new PointParticleEmitter { BoxMin = Vector3.Zero, BoxMax = new(8, 8, 8) };
        em.Configure(mapImpulse: 20f, impulseSpawnflag: true);
        var rng = new Random(1);
        var pts = new List<Vector3>();
        int idle = 0;
        for (int f = 0; f < 500; f++)
            idle += em.Step(1f / 60f, rng, pts);
        Assert.Equal(0, idle);
        em.JustToggled = true;
        int burst = em.Step(1f / 60f, rng, pts);
        Assert.InRange(burst, 20, 21);                // for (i = random(); i <= 20; ++i)
        Assert.Equal(0, em.Step(1f / 60f, rng, pts));

        // an ordinary emitter switched on emits at once, however short the frame ("if (n < 1) n = 1")
        var em2 = new PointParticleEmitter { BoxMin = Vector3.Zero, BoxMax = new(8, 8, 8) };
        em2.Configure(mapImpulse: 0.5f, impulseSpawnflag: false);
        em2.JustToggled = true;
        Assert.Equal(1, em2.Step(1f / 1000f, rng, pts));
        Assert.False(em2.JustToggled);
    }

    private static ParticleEmitterInfo ToOurs(DpRef.Info b) => new()
    {
        CountAbsolute = b.CountAbsolute, CountMultiplier = b.CountMultiplier, TrailSpacing = b.TrailSpacing,
        Type = (ParticleType)(b.ParticleType - 1), Blend = (ParticleBlend)b.BlendMode,
        Orientation = (ParticleOrientation)b.Orientation,
        Color0 = b.Color[0], Color1 = b.Color[1], Tex0 = b.Tex[0], Tex1 = b.Tex[1],
        StainTex0 = b.StainTex[0], StainTex1 = b.StainTex[1], StainColor0 = b.StainColor[0], StainColor1 = b.StainColor[1],
        StainSizeMin = b.StainSize[0], StainSizeMax = b.StainSize[1], StainAlphaMin = b.StainAlpha[0], StainAlphaMax = b.StainAlpha[1],
        SizeMin = b.Size[0], SizeMax = b.Size[1], SizeIncrease = b.Size[2],
        AlphaMin = b.Alpha[0], AlphaMax = b.Alpha[1], AlphaFade = b.Alpha[2],
        TimeMin = b.TimeRange[0], TimeMax = b.TimeRange[1],
        Gravity = b.Gravity, Bounce = b.Bounce, AirFriction = b.AirFriction, LiquidFriction = b.LiquidFriction,
        StretchFactor = b.StretchFactor, VelocityMultiplier = b.VelocityMultiplier,
        OriginOffset = V(b.OriginOffset), RelativeOriginOffset = V(b.RelativeOriginOffset),
        VelocityOffset = V(b.VelocityOffset), RelativeVelocityOffset = V(b.RelativeVelocityOffset),
        OriginJitter = V(b.OriginJitter), VelocityJitter = V(b.VelocityJitter),
        RotateBaseMin = b.Rotate[0], RotateBaseMax = b.Rotate[1], RotateSpinMin = b.Rotate[2], RotateSpinMax = b.Rotate[3],
        Underwater = (b.Flags & 1) != 0, NotUnderwater = (b.Flags & 2) != 0,
    };

    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
}

/// <summary>
/// DarkPlaces' particle code, transcribed. Names follow the C (<c>cl_particles.c</c>); the line numbers in
/// the comments are that file's. Nothing here calls into the game's own particle code.
/// </summary>
public sealed class DpRef
{
    // ptype_t
    public const int PtDead = 0, PtAlphaStatic = 1, PtStatic = 2, PtSpark = 3, PtBeam = 4, PtRain = 5,
        PtRainDecal = 6, PtSnow = 7, PtBubble = 8, PtBlood = 9, PtSmoke = 10, PtDecal = 11, PtEntityParticle = 12;
    // pblend_t / porientation_t, in the order the game's enums use
    public const int BlendAlpha = 0, BlendAdd = 1, BlendInvMod = 2;
    public const int Billboard = 0, Spark = 1, Oriented = 2, HBeam = 3;

    // particletype[] (:28-43): blend, orientation
    private static readonly (int Blend, int Orient)[] TypeTable =
    {
        (-1, -1), (BlendAlpha, Billboard), (BlendAdd, Billboard), (BlendAdd, Spark), (BlendAdd, HBeam),
        (BlendAdd, Spark), (BlendAdd, Oriented), (BlendAdd, Billboard), (BlendAdd, Billboard),
        (BlendInvMod, Billboard), (BlendAdd, Billboard), (BlendInvMod, Oriented), (BlendAlpha, Billboard),
    };

    /// <summary>particleeffectinfo_t with baselineparticleeffectinfo's values (:201-277).</summary>
    public sealed class Info
    {
        public int Flags;
        public double ParticleAccumulator;
        public float CountAbsolute, CountMultiplier, TrailSpacing;
        public int ParticleType = PtAlphaStatic, BlendMode = BlendAlpha, Orientation = Billboard;
        public uint[] Color = { 0xFFFFFF, 0xFFFFFF };
        public int[] Tex = { 63, 63 };
        public float[] Size = { 1, 1, 0 };
        public float[] Alpha = { 0, 256, 256 };
        public float[] TimeRange = { 16777216f, 16777216f };
        public float Gravity, Bounce, AirFriction, LiquidFriction;
        public float StretchFactor = 1f;
        public float[] OriginOffset = new float[3], RelativeOriginOffset = new float[3], VelocityOffset = new float[3],
            RelativeVelocityOffset = new float[3], OriginJitter = new float[3], VelocityJitter = new float[3];
        public float VelocityMultiplier;
        public uint[] StainColor = { 0xFFFFFFFF, 0xFFFFFFFF };
        public int[] StainTex = { -1, -1 };
        public float[] StainAlpha = { 1, 1 };
        public float[] StainSize = { 2, 2 };
        public float[] Rotate = { 0, 360, 0, 0 };
        public bool Defined;
    }

    /// <summary>CL_Particles_ParseEffectInfo (:314-479), for the blocks of one effect name.</summary>
    public static List<Info> ParseEffectInfo(string text, string effect)
    {
        var list = new List<Info>();
        Info? info = null;
        bool mine = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) line = line[..c];
            string[] argv = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (argv.Length == 0) continue;
            if (argv[0] == "effect")
            {
                mine = argv[1] == effect;
                info = new Info();
                if (mine) list.Add(info);
                continue;
            }
            if (info is null || !mine) continue;
            info.Defined = true;
            float F(int i) => float.Parse(argv[i], CultureInfo.InvariantCulture);
            int I(int i) => argv[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (int)Convert.ToUInt32(argv[i][2..], 16) : int.Parse(argv[i], CultureInfo.InvariantCulture);
            void Fs(float[] a, int n) { for (int i = 0; i < n; i++) a[i] = F(1 + i); }
            switch (argv[0])
            {
                case "countabsolute": info.CountAbsolute = F(1); break;
                case "count": info.CountMultiplier = F(1); break;
                case "type":
                    info.ParticleType = argv[1] switch
                    {
                        "alphastatic" => PtAlphaStatic, "static" => PtStatic, "spark" => PtSpark, "beam" => PtBeam,
                        "rain" => PtRain, "raindecal" => PtRainDecal, "snow" => PtSnow, "bubble" => PtBubble,
                        "blood" => PtBlood, "smoke" => PtSmoke, "decal" => PtDecal, "entityparticle" => PtEntityParticle,
                        _ => info.ParticleType,
                    };
                    if (argv[1] == "blood") info.Gravity = 1;
                    info.BlendMode = TypeTable[info.ParticleType].Blend;
                    info.Orientation = TypeTable[info.ParticleType].Orient;
                    break;
                case "blend":
                    info.BlendMode = argv[1] switch { "alpha" => BlendAlpha, "add" => BlendAdd, "invmod" => BlendInvMod, _ => info.BlendMode };
                    break;
                case "orientation":
                    info.Orientation = argv[1] switch { "billboard" => Billboard, "spark" => Spark, "oriented" => Oriented, "beam" => HBeam, _ => info.Orientation };
                    break;
                case "color": info.Color[0] = (uint)I(1); info.Color[1] = (uint)I(2); break;
                case "tex": info.Tex[0] = I(1); info.Tex[1] = I(2); break;
                case "size": Fs(info.Size, 2); break;
                case "sizeincrease": info.Size[2] = F(1); break;
                case "alpha": Fs(info.Alpha, 3); break;
                case "time": Fs(info.TimeRange, 2); break;
                case "gravity": info.Gravity = F(1); break;
                case "bounce": info.Bounce = F(1); break;
                case "airfriction": info.AirFriction = F(1); break;
                case "liquidfriction": info.LiquidFriction = F(1); break;
                case "originoffset": Fs(info.OriginOffset, 3); break;
                case "relativeoriginoffset": Fs(info.RelativeOriginOffset, 3); break;
                case "velocityoffset": Fs(info.VelocityOffset, 3); break;
                case "relativevelocityoffset": Fs(info.RelativeVelocityOffset, 3); break;
                case "originjitter": Fs(info.OriginJitter, 3); break;
                case "velocityjitter": Fs(info.VelocityJitter, 3); break;
                case "velocitymultiplier": info.VelocityMultiplier = F(1); break;
                case "underwater": info.Flags |= 1; break;
                case "notunderwater": info.Flags |= 2; break;
                case "trailspacing":
                    info.TrailSpacing = F(1);
                    if (info.TrailSpacing > 0) info.CountMultiplier = 1.0f / info.TrailSpacing;
                    break;
                case "stretchfactor": info.StretchFactor = F(1); break;
                case "staincolor": info.StainColor[0] = (uint)I(1); info.StainColor[1] = (uint)I(2); break;
                case "stainalpha": Fs(info.StainAlpha, 2); break;
                case "stainsize": Fs(info.StainSize, 2); break;
                case "staintex": info.StainTex[0] = I(1); info.StainTex[1] = I(2); break;
                case "rotate": Fs(info.Rotate, 4); break;
            }
        }
        list.RemoveAll(b => !b.Defined);
        return list;
    }

    /// <summary>particle_t (cl_particles.h).</summary>
    public sealed class Part
    {
        public int TypeIndex, BlendMode, Orientation, TexNum, StainTexNum;
        public Vector3 Org, Vel;
        public float Size, SizeIncrease, Alpha, AlphaFade, Gravity, Bounce, AirFriction, LiquidFriction, Stretch;
        public float Die, DelayedSpawn, Time2, Angle, Spin;
        public byte[] Color = new byte[3];
    }

    public Part[] Particles = new Part[16384];
    public int NumParticles, FreeParticle;
    public float Time;                  // cl.time
    private float _updateTime;          // cl.particles_updatetime
    public float Gravity = 800f;        // cl.movevars_gravity
    public float Quality = 1f;          // cl_particles_quality
    public int ImmediateBloodStain = 2; // cl_decals_newsystem_immediatebloodstain
    public int Decals;                  // CL_SpawnDecalParticleForPoint calls
    public readonly List<Part> Beams = new();

    private readonly int[] _rand;
    private int _randAt;
    private readonly ParticleAnalyticWorld _world;

    public DpRef(int[] rand, ParticleAnalyticWorld world)
    {
        _rand = rand;
        _world = world;
        for (int i = 0; i < Particles.Length; i++) Particles[i] = new Part();
    }

    private int Rand() => _rand[_randAt++];
    // #define lhrandom(MIN,MAX) (((double)(rand() + 0.5) / ((double)RAND_MAX + 1)) * ((MAX)-(MIN)) + (MIN))
    private double Lhrandom(double min, double max) => ((Rand() + 0.5) / (2147483647.0 + 1.0)) * (max - min) + min;
    // #define VectorRandom(v) do{(v)[0] = lhrandom(-1, 1);...}while(DotProduct(v, v) > 1)
    private Vector3 VectorRandom()
    {
        Vector3 v;
        do
        {
            v.X = (float)Lhrandom(-1, 1);
            v.Y = (float)Lhrandom(-1, 1);
            v.Z = (float)Lhrandom(-1, 1);
        }
        while (Vector3.Dot(v, v) > 1);
        return v;
    }

    // AnglesFromVectors(angles, forward, NULL, false) (mathlib.c)
    private static Vector3 AnglesFromVectors(Vector3 forward)
    {
        double pitch, yaw;
        if (forward.X == 0 && forward.Y == 0)
        {
            pitch = forward.Z > 0 ? -Math.PI * 0.5 : Math.PI * 0.5;
            yaw = 0;
        }
        else
        {
            yaw = Math.Atan2(forward.Y, forward.X);
            pitch = -Math.Atan2(forward.Z, Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y));
        }
        pitch *= 180 / Math.PI;
        yaw *= 180 / Math.PI;
        if (pitch < 0) pitch += 360;
        if (yaw < 0) yaw += 360;
        return new Vector3((float)pitch, (float)yaw, 0);
    }

    // AngleVectors (mathlib.c)
    private static void AngleVectors(Vector3 angles, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        double angle = angles.Y * (Math.PI * 2 / 360);
        double sy = Math.Sin(angle), cy = Math.Cos(angle);
        angle = angles.X * (Math.PI * 2 / 360);
        double sp = Math.Sin(angle), cp = Math.Cos(angle);
        forward = new Vector3((float)(cp * cy), (float)(cp * sy), (float)-sp);
        // roll is 0 on this path
        right = new Vector3((float)sy, (float)-cy, 0);
        up = new Vector3((float)(sp * cy), (float)(sp * sy), (float)cp);
    }

    /// <summary>CL_NewParticle (:668-854). Returns null when no particle was made.</summary>
    private Part? NewParticle(int ptypeindex, uint pcolor1, uint pcolor2, int ptex, float psize, float psizeincrease,
        float palpha, float palphafade, float pgravity, float pbounce, float px, float py, float pz,
        float pvx, float pvy, float pvz, float pairfriction, float pliquidfriction, float originjitter,
        float velocityjitter, float lifetime, float stretch, int blendmode, int orientation, int staintex,
        float angle, float spin, bool beam = false)
    {
        for (; FreeParticle < Particles.Length && Particles[FreeParticle].TypeIndex != 0; FreeParticle++) { }
        if (FreeParticle >= Particles.Length)
            return null;
        if (lifetime == 0)
            lifetime = palpha / MathF.Min(1, palphafade);
        Part part;
        if (beam)
            part = new Part();   // kept aside: the game's pool has no beam particles, it reports them
        else
        {
            part = Particles[FreeParticle++];
            if (NumParticles < FreeParticle)
                NumParticles = FreeParticle;
        }
        part.TypeIndex = ptypeindex;
        part.BlendMode = blendmode;
        part.Orientation = orientation;
        int l2 = (int)Lhrandom(0.5, 256.5);
        int l1 = 256 - l2;
        part.Color[0] = (byte)(((int)(((pcolor1 >> 16) & 0xFF) * l1 + ((pcolor2 >> 16) & 0xFF) * l2) >> 8) & 0xFF);
        part.Color[1] = (byte)(((int)(((pcolor1 >> 8) & 0xFF) * l1 + ((pcolor2 >> 8) & 0xFF) * l2) >> 8) & 0xFF);
        part.Color[2] = (byte)(((int)(((pcolor1 >> 0) & 0xFF) * l1 + ((pcolor2 >> 0) & 0xFF) * l2) >> 8) & 0xFF);
        part.Alpha = palpha;
        part.AlphaFade = palphafade;
        part.StainTexNum = staintex;
        if (_pendingStainLerp)                    // (:745-748) the staincolor lerp draws one lhrandom
            Lhrandom(0.5, 256.5);
        part.TexNum = ptex;
        part.Size = psize;
        part.SizeIncrease = psizeincrease;
        part.Gravity = pgravity;
        part.Bounce = pbounce;
        part.Stretch = stretch;
        Vector3 v = VectorRandom();
        part.Org = new Vector3(px + originjitter * v.X, py + originjitter * v.Y, pz + originjitter * v.Z);
        part.Vel = new Vector3(pvx + velocityjitter * v.X, pvy + velocityjitter * v.Y, pvz + velocityjitter * v.Z);
        part.AirFriction = pairfriction;
        part.LiquidFriction = pliquidfriction;
        part.Die = Time + lifetime;
        part.DelayedSpawn = Time;
        part.Time2 = 0;
        part.Angle = angle;
        part.Spin = spin;
        return part;
    }

    // staincolor1 >= 0 && staincolor2 >= 0 (:745) draws one more lhrandom inside CL_NewParticle; the stain
    // colour itself is not compared here, only the draw is kept in step.
    private bool _pendingStainLerp;

    /// <summary>CL_NewParticlesFromEffectinfo (:1574-1793), spawnparticles = true, no tint.</summary>
    public void NewParticlesFromEffectinfo(List<Info> infos, float pcount, Vector3 originmins, Vector3 originmaxs,
        Vector3 velocitymins, Vector3 velocitymaxs, float fade, bool wanttrail)
    {
        Vector3 center = originmins + (originmaxs - originmins) * 0.5f;                 // VectorLerp
        int supercontents = _world.PointContents(center);
        bool underwater = (supercontents & (ParticleAnalyticWorld.ContWater | ParticleAnalyticWorld.ContSlime)) != 0;
        Vector3 traildir = originmaxs - originmins;
        float traillen = traildir.Length();
        if (traillen != 0) traildir /= traillen;                                          // VectorNormalize
        foreach (Info info in infos)
        {
            bool definedastrail = info.TrailSpacing > 0;
            bool drawastrail = wanttrail;       // cl_particles_forcetraileffects 0
            if ((info.Flags & 1) != 0 && !underwater) continue;
            if ((info.Flags & 2) != 0 && underwater) continue;

            int tex = info.Tex[0];
            if (info.Tex[1] > info.Tex[0])
            {
                tex = (int)Lhrandom(info.Tex[0], info.Tex[1]);
                tex = Math.Min(tex, info.Tex[1] - 1);
            }
            int staintex;
            if (info.StainTex[0] < 0)
                staintex = info.StainTex[0];
            else
            {
                staintex = (int)Lhrandom(info.StainTex[0], info.StainTex[1]);
                staintex = Math.Min(staintex, info.StainTex[1] - 1);
            }
            _pendingStainLerp = (int)info.StainColor[0] >= 0 && (int)info.StainColor[1] >= 0;

            Vector3 angles, velocity, forward, right, up, trailpos;
            if (info.ParticleType == PtDecal)
            {
                // CL_SpawnDecalParticleForPoint(trailpos, originjitter[0], lhrandom(size), lhrandom(alpha), ...)
                // then CL_SpawnDecalParticleForSurface draws one lhrandom for the colour (:974)
                Lhrandom(info.Size[0], info.Size[1]);
                Lhrandom(info.Alpha[0], info.Alpha[1]);
                Lhrandom(0.5, 256.5);
                Decals++;
            }
            else if (info.Orientation == HBeam)
            {
                if (!drawastrail)
                    continue;
                angles = AnglesFromVectors(traildir);
                AngleVectors(angles, out forward, out right, out up);
                trailpos = forward * info.RelativeOriginOffset[0] + right * info.RelativeOriginOffset[1] + up * info.RelativeOriginOffset[2];
                float bsize = (float)Lhrandom(info.Size[0], info.Size[1]);
                float balpha = (float)Lhrandom(info.Alpha[0], info.Alpha[1]);
                float btime = (float)Lhrandom(info.TimeRange[0], info.TimeRange[1]);
                Lhrandom(info.StainAlpha[0], info.StainAlpha[1]);
                Lhrandom(info.StainSize[0], info.StainSize[1]);
                Part? beam = NewParticle(info.ParticleType, info.Color[0], info.Color[1], tex, bsize, info.Size[2],
                    balpha, info.Alpha[2], 0, 0, originmins.X + trailpos.X, originmins.Y + trailpos.Y, originmins.Z + trailpos.Z,
                    originmaxs.X, originmaxs.Y, originmaxs.Z, 0, 0, 0, 0, btime, info.StretchFactor, info.BlendMode,
                    info.Orientation, staintex, 0, 0, beam: true);
                if (beam is not null) Beams.Add(beam);
            }
            else
            {
                float cnt = info.CountAbsolute;
                cnt += (pcount * info.CountMultiplier) * Quality;
                if (drawastrail && definedastrail)
                    cnt += (traillen / info.TrailSpacing) * Quality;
                cnt *= fade;
                if (cnt == 0)
                    continue;
                info.ParticleAccumulator += cnt;

                bool immediatebloodstain;
                if (drawastrail || definedastrail)
                    immediatebloodstain = false;
                else
                    immediatebloodstain = (ImmediateBloodStain >= 1 && info.ParticleType == PtBlood)
                        || (ImmediateBloodStain >= 2 && staintex != 0);

                float trailstep;
                if (drawastrail)
                {
                    trailpos = originmins;
                    trailstep = traillen / cnt;
                }
                else
                {
                    trailpos = center;
                    trailstep = 0;
                }

                if (trailstep == 0)
                {
                    velocity = velocitymins * 0.5f + velocitymaxs * 0.5f;
                    angles = AnglesFromVectors(velocity);
                }
                else
                    angles = AnglesFromVectors(traildir);

                AngleVectors(angles, out forward, out right, out up);
                trailpos += forward * info.RelativeOriginOffset[0] + right * info.RelativeOriginOffset[1] + up * info.RelativeOriginOffset[2];
                velocity = forward * info.RelativeVelocityOffset[0] + right * info.RelativeVelocityOffset[1] + up * info.RelativeVelocityOffset[2];
                info.ParticleAccumulator = Math.Clamp(info.ParticleAccumulator, 0, 16384);
                for (; info.ParticleAccumulator >= 1; info.ParticleAccumulator--)
                {
                    if (info.Tex[1] > info.Tex[0])
                    {
                        tex = (int)Lhrandom(info.Tex[0], info.Tex[1]);
                        tex = Math.Min(tex, info.Tex[1] - 1);
                    }
                    if (!(drawastrail || definedastrail))
                    {
                        trailpos.X = (float)Lhrandom(originmins.X, originmaxs.X);
                        trailpos.Y = (float)Lhrandom(originmins.Y, originmaxs.Y);
                        trailpos.Z = (float)Lhrandom(originmins.Z, originmaxs.Z);
                    }
                    Vector3 rvec = VectorRandom();
                    // the arguments in the order the game's port draws them (C leaves the order to the compiler)
                    float vx = (float)Lhrandom(velocitymins.X, velocitymaxs.X) * info.VelocityMultiplier + info.VelocityOffset[0] + info.VelocityJitter[0] * rvec.X + velocity.X;
                    float vy = (float)Lhrandom(velocitymins.Y, velocitymaxs.Y) * info.VelocityMultiplier + info.VelocityOffset[1] + info.VelocityJitter[1] * rvec.Y + velocity.Y;
                    float vz = (float)Lhrandom(velocitymins.Z, velocitymaxs.Z) * info.VelocityMultiplier + info.VelocityOffset[2] + info.VelocityJitter[2] * rvec.Z + velocity.Z;
                    float size = (float)Lhrandom(info.Size[0], info.Size[1]);
                    float alpha = (float)Lhrandom(info.Alpha[0], info.Alpha[1]);
                    float life = (float)Lhrandom(info.TimeRange[0], info.TimeRange[1]);
                    Lhrandom(info.StainAlpha[0], info.StainAlpha[1]);
                    Lhrandom(info.StainSize[0], info.StainSize[1]);
                    float angle = (float)Lhrandom(info.Rotate[0], info.Rotate[1]);
                    float spin = (float)Lhrandom(info.Rotate[2], info.Rotate[3]);
                    NewParticle(info.ParticleType, info.Color[0], info.Color[1], tex, size, info.Size[2], alpha, info.Alpha[2],
                        info.Gravity, info.Bounce,
                        trailpos.X + info.OriginOffset[0] + info.OriginJitter[0] * rvec.X,
                        trailpos.Y + info.OriginOffset[1] + info.OriginJitter[1] * rvec.Y,
                        trailpos.Z + info.OriginOffset[2] + info.OriginJitter[2] * rvec.Z,
                        vx, vy, vz, info.AirFriction, info.LiquidFriction, 0, 0, life, info.StretchFactor,
                        info.BlendMode, info.Orientation, staintex, angle, spin);
                    immediatebloodstain = false;   // CL_ImmediateBloodStain draws no rand() the pool depends on
                    if (trailstep != 0)
                        trailpos += traildir * trailstep;
                }
            }
        }
    }

    /// <summary>R_DrawParticles (:2912-3187): the per-frame update, without the drawing.</summary>
    public void Update()
    {
        float frametime = Math.Clamp(Time - _updateTime, 0, 1);
        _updateTime = Math.Clamp(_updateTime + frametime, Time - 1, Time + 1);
        if (NumParticles == 0)
            return;
        float gravity = frametime * Gravity;
        bool update = frametime > 0;
        for (int i = 0; i < NumParticles; i++)
        {
            Part p = Particles[i];
            if (p.TypeIndex == 0)
            {
                if (FreeParticle > i) FreeParticle = i;
                continue;
            }
            if (update)
            {
                if (p.DelayedSpawn > Time)
                    continue;
                p.Size += p.SizeIncrease * frametime;
                p.Alpha -= p.AlphaFade * frametime;
                if (p.Alpha <= 0 || p.Die <= Time) { Kill(p, i); continue; }

                if (p.Orientation != HBeam && frametime > 0)
                {
                    float f;
                    if (p.LiquidFriction != 0 && (_world.PointContents(p.Org) & ParticleAnalyticWorld.ContLiquidsMask) != 0)
                    {
                        if (p.TypeIndex == PtBlood)
                            p.Size += frametime * 8;
                        else
                            p.Vel.Z -= p.Gravity * gravity;
                        f = 1.0f - MathF.Min(p.LiquidFriction * frametime, 1);
                        p.Vel *= f;
                    }
                    else
                    {
                        p.Vel.Z -= p.Gravity * gravity;
                        if (p.AirFriction != 0)
                        {
                            f = 1.0f - MathF.Min(p.AirFriction * frametime, 1);
                            p.Vel *= f;
                        }
                    }

                    Vector3 oldorg = p.Org;
                    p.Org += p.Vel * frametime;
                    if (p.Bounce != 0 && p.Vel.Length() != 0)
                    {
                        int mask = ParticleAnalyticWorld.ContSolid
                            | ((p.TypeIndex == PtRain || p.TypeIndex == PtSnow) ? ParticleAnalyticWorld.ContLiquidsMask : 0);
                        TraceResult trace = _world.Trace(oldorg, p.Org, mask);
                        int startcontents = trace.StartSolid ? trace.DpHitContents : 0;
                        if (((startcontents | trace.DpHitContents) & ParticleAnalyticWorld.ContNoDrop) != 0
                            || (startcontents & ParticleAnalyticWorld.ContSolid) != 0)
                        { Kill(p, i); continue; }
                        p.Org = trace.EndPos;
                        if (trace.Fraction < 1)
                        {
                            if (p.TypeIndex == PtBlood) { Kill(p, i); continue; }
                            else if (p.Bounce < 0) { Kill(p, i); continue; }
                            else
                            {
                                float dist = Vector3.Dot(p.Vel, trace.PlaneNormal) * -p.Bounce;
                                p.Vel += trace.PlaneNormal * dist;
                            }
                        }
                    }

                    if (Vector3.Dot(p.Vel, p.Vel) < 0.03f)
                    {
                        if (p.Orientation == Spark) { Kill(p, i); continue; }
                        p.Vel = Vector3.Zero;
                    }
                }

                if (p.TypeIndex != PtStatic)
                {
                    int a;
                    switch (p.TypeIndex)
                    {
                        case PtEntityParticle:
                            if (p.Time2 != 0) { Kill(p, i); continue; }
                            p.Time2 = 1;
                            break;
                        case PtBlood:
                            a = _world.PointContents(p.Org);
                            if ((a & (ParticleAnalyticWorld.ContSolid | ParticleAnalyticWorld.ContLava | ParticleAnalyticWorld.ContNoDrop)) != 0) { Kill(p, i); continue; }
                            break;
                        case PtBubble:
                            a = _world.PointContents(p.Org);
                            if ((a & (ParticleAnalyticWorld.ContWater | ParticleAnalyticWorld.ContSlime)) == 0) { Kill(p, i); continue; }
                            break;
                        case PtRain:
                            a = _world.PointContents(p.Org);
                            if ((a & (ParticleAnalyticWorld.ContSolid | ParticleAnalyticWorld.ContLiquidsMask)) != 0) { Kill(p, i); continue; }
                            break;
                        case PtSnow:
                            if (Time > p.Time2)
                            {
                                p.Time2 = Time + (Rand() & 3) * 0.1f;
                                p.Vel.X = p.Vel.X * 0.9f + (float)Lhrandom(-32, 32);
                                p.Vel.Y = p.Vel.X * 0.9f + (float)Lhrandom(-32, 32);
                            }
                            a = _world.PointContents(p.Org);
                            if ((a & (ParticleAnalyticWorld.ContSolid | ParticleAnalyticWorld.ContLiquidsMask)) != 0) { Kill(p, i); continue; }
                            break;
                    }
                }
            }
        }
        while (NumParticles > 0 && Particles[NumParticles - 1].TypeIndex == 0)
            NumParticles--;
    }

    private void Kill(Part p, int i)
    {
        p.TypeIndex = 0;
        if (FreeParticle > i) FreeParticle = i;
    }

    /// <summary>
    /// The four corners DarkPlaces draws for a spark: case PARTICLE_SPARK (:2817-2825) and
    /// R_CalcBeam_Vertex3f (gl_rmain.c:6253-6281), cl_particles_size 1.
    /// </summary>
    public static Vector3[] SparkQuad(Part p, Vector3 viewOrigin)
    {
        float size = p.Size;
        float len = p.Vel.Length();
        Vector3 up = len != 0 ? p.Vel / len : Vector3.Zero;                  // VectorNormalize2
        float lenfactor = p.Stretch * 0.04f * len;
        if (lenfactor < size * 0.5f)
            lenfactor = size * 0.5f;
        Vector3 org1 = p.Org + up * -lenfactor;                              // v
        Vector3 org2 = p.Org + up * lenfactor;                               // up2
        Vector3 normal = org2 - org1;
        Vector3 right1 = Vector3.Cross(normal, viewOrigin - org1);
        right1 = right1.Length() != 0 ? Vector3.Normalize(right1) : right1;
        Vector3 right2 = Vector3.Cross(normal, viewOrigin - org2);
        right2 = right2.Length() != 0 ? Vector3.Normalize(right2) : right2;
        return new[]
        {
            org1 + right1 * size, org1 - right1 * size, org2 - right2 * size, org2 + right2 * size,
        };
    }
}

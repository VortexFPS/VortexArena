using System.Collections.Generic;
using System.Numerics;
using VortexArena.Engine.Particles;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <see cref="ParticleSim.SpawnDirect"/>: Darkplaces' <c>CL_NewParticle</c> for one fully described particle
/// (the CSQC particle spawner's route into the pool), with DP's own type / blend / orientation numbers.
/// </summary>
public class ParticleDirectSpawnTests
{
    private static ParticleSim NewSim(out CvarService cvars, int max = 64)
    {
        cvars = new CvarService();
        cvars.Register(ParticleCvars.Particles, "1");
        // The sim's own cvar store and no collisions: nothing here reaches the ambient services.
        return new ParticleSim(new XorShiftParticleRng(99), initialCapacity: max, maxParticles: max) { Cvars = cvars };
    }

    private static DirectParticle Plain() => new()
    {
        Origin = new Vector3(10, 20, 30), Velocity = new Vector3(100, 0, 0),
        Type = 2, Blend = 1, Orientation = 0, Color1 = 0x204060, Color2 = 0x204060, Texture = 63,
        Size = 2, Alpha = 256, AlphaFade = 64, Lifetime = 4, Stretch = 1, StainColor1 = -1, StainColor2 = -1, StainTexture = -1,
    };

    [Fact]
    public void A_Direct_Particle_Lands_In_The_Pool_With_The_Given_Values()
    {
        ParticleSim sim = NewSim(out _);
        sim.Update(1f);
        DirectParticle d = Plain();
        d.SizeIncrease = 3; d.Gravity = 0.5f; d.Bounce = 1.5f; d.AirFriction = 0.25f; d.LiquidFriction = 2; d.Angle = 30; d.Spin = 60;
        d.StainTexture = 12; d.StainAlpha = 0.5f; d.StainSize = 3;
        Assert.True(sim.SpawnDirect(d));
        Assert.Equal(1, sim.HighWater);

        ref Particle p = ref sim.Pool[0];
        Assert.True(p.Active);
        Assert.Equal(ParticleType.Static, p.TypeIndex);            // ptype_t 2 = pt_static
        Assert.Equal(ParticleBlend.Add, p.BlendMode);
        Assert.Equal(ParticleOrientation.Billboard, p.Orientation);
        Assert.Equal(new Vector3(10, 20, 30), p.Org);
        Assert.Equal(new Vector3(10, 20, 30), p.SortOrg);
        Assert.Equal(new Vector3(100, 0, 0), p.Vel);
        Assert.Equal((0x20, 0x40, 0x60), ((int)p.ColorR, (int)p.ColorG, (int)p.ColorB));
        Assert.Equal(63, p.TexNum);
        Assert.Equal(2f, p.Size);
        Assert.Equal(3f, p.SizeIncrease);
        Assert.Equal(256f, p.Alpha);
        Assert.Equal(64f, p.AlphaFade);
        Assert.Equal(0.5f, p.Gravity);
        Assert.Equal(1.5f, p.Bounce);
        Assert.Equal(0.25f, p.AirFriction);
        Assert.Equal(2f, p.LiquidFriction);
        Assert.Equal(5f, p.Die);                                   // now + lifetime
        Assert.Equal(1f, p.DelayedSpawn);
        Assert.Equal(30f, p.Angle);
        Assert.Equal(60f, p.Spin);
        Assert.Equal(12, p.StainTexNum);
        Assert.Equal(128f, p.StainAlpha);                          // alpha * stainalpha
        Assert.Equal(6f, p.StainSize);                             // size * stainsize
        // staincolor -1: the stain takes the particle's colour.
        Assert.Equal((p.ColorR, p.ColorG, p.ColorB), (p.StainColorR, p.StainColorG, p.StainColorB));
    }

    [Theory]
    [InlineData(1, ParticleType.AlphaStatic)]
    [InlineData(3, ParticleType.Spark)]
    [InlineData(7, ParticleType.Snow)]
    [InlineData(9, ParticleType.Blood)]
    [InlineData(12, ParticleType.EntityParticle)]
    [InlineData(13, ParticleType.AlphaStatic)]   // pt_explode: no behaviour of its own in DP's update loop
    [InlineData(14, ParticleType.AlphaStatic)]
    public void DarkPlaces_Type_Numbers_Are_One_Above_The_Pools(int dpType, ParticleType expected)
    {
        ParticleSim sim = NewSim(out _);
        DirectParticle d = Plain();
        d.Type = dpType;
        Assert.True(sim.SpawnDirect(d));
        Assert.Equal(expected, sim.Pool[0].TypeIndex);
    }

    [Fact]
    public void Blend_And_Orientation_Numbers_Are_DarkPlaces_Own()
    {
        ParticleSim sim = NewSim(out _);
        DirectParticle d = Plain();
        d.Blend = 0; d.Orientation = 1;
        Assert.True(sim.SpawnDirect(d));
        d.Blend = 2; d.Orientation = 2;
        Assert.True(sim.SpawnDirect(d));
        d.Blend = 77;   // not a pblend_t: drawn as the first of them
        Assert.True(sim.SpawnDirect(d));
        Assert.Equal((ParticleBlend.Alpha, ParticleOrientation.Spark), (sim.Pool[0].BlendMode, sim.Pool[0].Orientation));
        Assert.Equal((ParticleBlend.InvMod, ParticleOrientation.Oriented), (sim.Pool[1].BlendMode, sim.Pool[1].Orientation));
        Assert.Equal(ParticleBlend.Alpha, sim.Pool[2].BlendMode);

        // An orientation R_DrawParticle has no case for, and pt_dead: "made" as far as the caller can tell, never seen.
        int before = sim.HighWater;
        d = Plain(); d.Orientation = 9;
        Assert.True(sim.SpawnDirect(d));
        d = Plain(); d.Orientation = -1;
        Assert.True(sim.SpawnDirect(d));
        d = Plain(); d.Type = 0;
        Assert.True(sim.SpawnDirect(d));
        Assert.Equal(before, sim.HighWater);
    }

    [Fact]
    public void A_Beam_Is_Handed_To_The_Host_And_Takes_No_Pool_Slot()
    {
        ParticleSim sim = NewSim(out _);
        List<BeamEvent> beams = new();
        sim.OnBeam = beams.Add;
        DirectParticle d = Plain();
        d.Type = 4; d.Orientation = 4; d.Velocity = new Vector3(50, 60, 70); d.Lifetime = 0; d.Alpha = 128; d.AlphaFade = 256; d.Size = 8; d.Texture = 60;
        Assert.True(sim.SpawnDirect(d));
        d.Orientation = 3;
        Assert.True(sim.SpawnDirect(d));
        Assert.Equal(0, sim.HighWater);
        Assert.Equal(2, beams.Count);
        Assert.Equal(new Vector3(10, 20, 30), beams[0].Start);
        Assert.Equal(new Vector3(50, 60, 70), beams[0].End);
        Assert.Equal(8f, beams[0].Size);
        Assert.Equal(60, beams[0].TexNum);
        Assert.Equal(ParticleBlend.Add, beams[0].Blend);
        Assert.Equal(128f, beams[0].Lifetime);    // lifetime 0: alpha / min(1, alphafade)
        Assert.Equal((0x20, 0x40, 0x60), ((int)beams[0].ColorR, (int)beams[0].ColorG, (int)beams[0].ColorB));
    }

    [Fact]
    public void What_NewParticle_Refuses_Is_Refused()
    {
        ParticleSim sim = NewSim(out CvarService cvars, max: 64);
        DirectParticle d = Plain();
        foreach (int type in new[] { 15, 16, 4464, -1, int.MinValue })
        {
            d.Type = type;
            Assert.False(sim.SpawnDirect(d));
        }
        d = Plain();
        foreach (int texture in new[] { 256, -1, int.MaxValue })
        {
            d.Texture = texture;
            Assert.False(sim.SpawnDirect(d));
        }
        Assert.Equal(0, sim.HighWater);

        // A stain texture past the font is "no stain", not a refusal.
        d = Plain(); d.StainTexture = 300;
        Assert.True(sim.SpawnDirect(d));
        Assert.Equal(-1, sim.Pool[0].StainTexNum);

        // cl_particles 0.
        cvars.Set(ParticleCvars.Particles, "0");
        Assert.False(sim.SpawnDirect(Plain()));
        d = Plain(); d.Type = 0;
        Assert.False(sim.SpawnDirect(d));
        cvars.Set(ParticleCvars.Particles, "1");

        // A full pool: the 64th particle is the last.
        for (int i = sim.HighWater; i < 64; i++) Assert.True(sim.SpawnDirect(Plain()));
        Assert.False(sim.SpawnDirect(Plain()));
    }

    [Fact]
    public void A_Delayed_Particle_Waits_Then_Moves_And_Dies_On_Its_Own_Clock()
    {
        ParticleSim sim = NewSim(out _);
        sim.Update(10f);
        DirectParticle d = Plain();
        d.Delay = 0.5f; d.AlphaFade = 0; d.Lifetime = 2;
        Assert.True(sim.SpawnDirect(d));
        ref Particle p = ref sim.Pool[0];
        Assert.Equal(10.5f, p.DelayedSpawn);
        Assert.Equal(12f, p.Die);          // the lifetime runs from the call, as part->die does in DP

        sim.Update(10.25f);
        Assert.True(sim.Pool[0].Active);
        Assert.Equal(new Vector3(10, 20, 30), sim.Pool[0].Org);   // not yet spawned: it has not moved
        sim.Update(10.75f);
        Assert.True(sim.Pool[0].Org.X > 10f);
        sim.Update(11.5f);
        Assert.True(sim.Pool[0].Active);
        sim.Update(12.25f);
        Assert.False(sim.Pool[0].Active);
        Assert.Equal(0, sim.LiveCount);
    }

    [Fact]
    public void Jitter_Moves_Origin_And_Velocity_By_The_Same_Random_Vector()
    {
        ParticleSim sim = NewSim(out _);
        DirectParticle d = Plain();
        d.OriginJitter = 10; d.VelocityJitter = 20;
        Assert.True(sim.SpawnDirect(d));
        Vector3 dOrg = sim.Pool[0].Org - d.Origin, dVel = sim.Pool[0].Vel - d.Velocity;
        Assert.True(dOrg.Length() <= 10.001f && dOrg.Length() > 0f);
        Assert.Equal(dOrg.X * 2, dVel.X, 3);
        Assert.Equal(dOrg.Y * 2, dVel.Y, 3);
        Assert.Equal(dOrg.Z * 2, dVel.Z, 3);
        Assert.Equal(d.Origin, sim.Pool[0].SortOrg);   // the sort origin is the caller's point, not the jittered one
    }
}

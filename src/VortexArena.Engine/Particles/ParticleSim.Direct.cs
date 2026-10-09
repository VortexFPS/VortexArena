using System.Numerics;

namespace VortexArena.Engine.Particles;

/// <summary>
/// The arguments of Darkplaces' <c>CL_NewParticle</c> (cl_particles.c:668) for ONE particle that does not
/// come from an effectinfo block: the CSQC particle spawner (clvm_cmds.c VM_CL_SpawnParticle) hands the
/// engine a fully described particle. Numbers are DP's own: <see cref="Type"/> is a <c>ptype_t</c> (0 =
/// pt_dead … 14 = pt_explode2), <see cref="Blend"/> a <c>pblend_t</c>, <see cref="Orientation"/> a
/// <c>porientation_t</c> (3 = vertical beam, 4 = horizontal beam), alpha on the 0..256 scale.
/// </summary>
public struct DirectParticle
{
    public Vector3 Origin, Velocity;
    public int Type, Blend, Orientation;
    public int Color1, Color2, Texture;
    public float Size, SizeIncrease, Alpha, AlphaFade, Gravity, Bounce, AirFriction, LiquidFriction, OriginJitter, VelocityJitter;
    public float Lifetime, Stretch;
    public int StainColor1, StainColor2, StainTexture;
    public float StainAlpha, StainSize, Angle, Spin;
    /// <summary>Seconds until the particle appears and starts to move (DP <c>part->delayedspawn - cl.time</c>).</summary>
    public float Delay;
}

public sealed partial class ParticleSim
{
    /// <summary>DP <c>pt_total</c>: the first value that is not a particle type.</summary>
    public const int DirectTypeCount = 15;
    /// <summary>DP <c>MAX_PARTICLETEXTURES</c>.</summary>
    public const int DirectMaxTextures = 256;

    /// <summary>
    /// <c>CL_NewParticle</c> for a caller outside the effect tables. Returns what the C's "part != NULL"
    /// is: false when <c>cl_particles</c> is 0, the type or the texture number is outside its table, or the
    /// pool is full.
    /// <para>Where this pool cannot hold what DP's can: a beam (orientation 3 or 4) is handed to
    /// <see cref="OnBeam"/> like an effectinfo beam, and its delay is not honoured; pt_explode and
    /// pt_explode2 (13, 14) have no behaviour of their own in DP's update loop and are simulated as a
    /// plain particle; type 0 (pt_dead) and an orientation DP has no drawing case for count as made and
    /// show nothing, as in DP; <c>qualityreduction</c> (DP drops such particles when the frame rate is
    /// low) is not applied.</para>
    /// </summary>
    public bool SpawnDirect(in DirectParticle d)
    {
        if (!CvBool(ParticleCvars.Particles)) return false;                                            // (702)
        if ((uint)d.Type >= DirectTypeCount || (uint)d.Texture >= DirectMaxTextures) return false;       // (705)
        int stainTex = d.StainTexture >= DirectMaxTextures ? -1 : d.StainTexture;                      // (707)
        if (d.Type == 0) return true;                 // pt_dead: DP's slot stays "free" and is never drawn
        ParticleBlend blend = d.Blend switch { 1 => ParticleBlend.Add, 2 => ParticleBlend.InvMod, _ => ParticleBlend.Alpha };
        float now = _currentTime;

        if (d.Orientation is 3 or 4)
        {
            // CL_NewParticle's own draws for a beam, then the host draws it (this pool holds sprites only).
            int l2 = (int)ParticleRandom.Lhrandom(Rng, 0.5, 256.5);
            int l1 = 256 - l2;
            byte r = (byte)(((((d.Color1 >> 16) & 0xFF) * l1 + ((d.Color2 >> 16) & 0xFF) * l2) >> 8) & 0xFF);
            byte g = (byte)(((((d.Color1 >> 8) & 0xFF) * l1 + ((d.Color2 >> 8) & 0xFF) * l2) >> 8) & 0xFF);
            byte b = (byte)(((((d.Color1 >> 0) & 0xFF) * l1 + ((d.Color2 >> 0) & 0xFF) * l2) >> 8) & 0xFF);
            if (d.StainColor1 >= 0 && d.StainColor2 >= 0) ParticleRandom.Lhrandom(Rng, 0.5, 256.5);
            Vector3 jv = ParticleRandom.VectorRandom(Rng);
            float lifetime = d.Lifetime == 0f ? d.Alpha / MathF.Min(1f, d.AlphaFade) : d.Lifetime;
            OnBeam?.Invoke(new BeamEvent(d.Origin + jv * d.OriginJitter, d.Velocity + jv * d.VelocityJitter, d.Size, d.SizeIncrease,
                d.Alpha, d.AlphaFade, lifetime, r, g, b, d.Texture, d.Stretch, blend));
            return true;
        }
        if ((uint)d.Orientation > 2) return true;      // no case in R_DrawParticle's switch: made, never seen

        // ptype_t is this pool's ParticleType shifted by one (pt_dead has no entry here).
        ParticleType type = d.Type >= 13 ? ParticleType.AlphaStatic : (ParticleType)(d.Type - 1);
        int idx = NewParticle(now, type, unchecked((uint)d.Color1), unchecked((uint)d.Color2), d.Texture,
            d.Size, d.SizeIncrease, d.Alpha, d.AlphaFade, d.Gravity, d.Bounce,
            d.Origin.X, d.Origin.Y, d.Origin.Z, d.Velocity.X, d.Velocity.Y, d.Velocity.Z,
            d.AirFriction, d.LiquidFriction, d.Lifetime, d.Stretch, blend, (ParticleOrientation)d.Orientation,
            d.StainColor1, d.StainColor2, stainTex, d.StainAlpha, d.StainSize, d.Angle, d.Spin, null,
            d.OriginJitter, d.VelocityJitter);
        if (idx < 0) return false;
        ref Particle p = ref _pool[idx];
        p.SortOrg = d.Origin;                           // "VectorCopy(sortorigin, part->sortorigin)": the caller's org
        if (d.Delay != 0f) p.DelayedSpawn = now + d.Delay;
        return true;
    }
}

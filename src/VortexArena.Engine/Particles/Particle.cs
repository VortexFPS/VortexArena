using System.Numerics;

namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  The faithful particle pool element — the C# mirror of Darkplaces' particle_t (Base/darkplaces/
//  cl_particles.h:80-120). A mutable struct stored in a flat array (ParticleSim owns the pool); the
//  renderer (game/client/particles/FaithfulParticleRenderer.cs) reads the live ones to fill MultiMesh
//  batches. Liveness is tracked by Active; DP's die/alpha kill conditions clear it.
// =====================================================================================================

/// <summary>One simulated particle. Field semantics match particle_t; see cl_particles.c for the math.</summary>
public struct Particle
{
    // --- pool management ---
    public bool Active;            // false = free slot

    // --- rendering fields (org/vel/size/alpha/stretch + color) ---
    public Vector3 Org;            // world position (Quake units)
    public Vector3 Vel;            // velocity, or beam endpoint for beams
    public Vector3 SortOrg;        // transparent-sort origin: the EFFECT's spawn center, not the particle org
                                   // (particle_t.sortorigin — all particles of one burst share it, so they sort
                                   // as a group and draw in pool/spawn order within it, cl_particles.c:3145)
    public float Size;             // world-unit half-size
    public float Alpha;            // 0..255
    public float Stretch;          // spark stretch factor (sparks only)

    // --- non-render fields ---
    public float StainSize;
    public float StainAlpha;
    public float SizeIncrease;     // size change per second
    public float AlphaFade;        // alpha reduced per second
    public float Time2;            // snow flutter timer / decal fade / explosion ramp
    public float Bounce;           // bounce-back amount (<0 removes on impact)
    public float Gravity;          // gravity multiplier
    public float AirFriction;
    public float LiquidFriction;
    public float DelayedSpawn;     // time the particle appears and begins moving
    public float Die;              // absolute time the particle is removed regardless of alpha

    // --- orientation (degrees / deg-per-sec; do not affect the position/vel/alpha golden trace) ---
    public float Angle;            // base rotation
    public float Spin;             // rotation speed around the particle normal

    // --- packed bytes (color/stain/tex/type) ---
    public byte ColorR, ColorG, ColorB;
    public byte StainColorR, StainColorG, StainColorB;
    public ParticleType TypeIndex;
    public ParticleBlend BlendMode;
    public ParticleOrientation Orientation;
    public byte TexNum;            // atlas cell index drawn for this particle
    public sbyte StainTexNum;      // < 0 => no stain
}

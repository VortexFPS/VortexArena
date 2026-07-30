using System;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

// =====================================================================================================
//  Parsed effectinfo.txt data model — the C# successor to Darkplaces' particleeffectinfo_t and its
//  pt_/PBLEND_/PARTICLE_ enums (Base/darkplaces/cl_particles.c). One "effect <name>" block parses into
//  one EffectInfoEmitter; an effect name owns a list of these (DP layers multiple same-named blocks).
//
//  Numeric conventions are kept identical to DP so the values from the file map without re-tuning:
//   * color is hex RRGGBB; a particle picks a random lerp between Color[0] and Color[1].
//   * size  = {min, max, sizeincrease}: world-unit half-size chosen in [min,max]; sizeincrease is
//     units/sec growth (negative shrinks).  (particleeffectinfo_t.size[3])
//   * alpha = {min, max, alphafade}: opacity 0..256 (256 = fully opaque) chosen in [min,max];
//     alphafade is alpha-units faded per second.  (particleeffectinfo_t.alpha[3])
//   * time  = {min,max} lifetime seconds; if 0 DP derives life = alpha/min(1,alphafade).
//   * gravity is a multiplier on world gravity (DP sv_gravity, default 800); negative floats up.
//   * originjitter/velocityjitter add jitter*VectorRandom() (uniform unit sphere) per axis at spawn.
//   * velocitymultiplier scales the supplied emit velocity; velocityoffset adds a constant.
//   * count is countmultiplier (particles per requested count); countabsolute is an unconditional add.
// =====================================================================================================

/// <summary>DP particle kind (ptype_t). Drives default blend/orientation and a few behaviours.</summary>
public enum EiType
{
    AlphaStatic,  // pt_alphastatic — alpha-blended billboard (smoke-like, the baseline)
    Static,       // pt_static      — additive billboard (fire/energy)
    Spark,        // pt_spark       — velocity-stretched streak
    Beam,         // pt_beam        — oriented beam (HBEAM), only drawn as a trail
    Rain,         // pt_rain
    RainDecal,    // pt_raindecal
    Snow,         // pt_snow
    Bubble,       // pt_bubble      — underwater bubble
    Blood,        // pt_blood       — invmod blood spark; gravity forced to 1 by DP
    Smoke,        // pt_smoke       — additive billboard puff
    Decal,        // pt_decal       — projected onto the hit surface (invmod), not a free particle
    EntityParticle,
}

/// <summary>DP blend mode (pblend_t).</summary>
public enum EiBlend
{
    Alpha,   // PBLEND_ALPHA  — standard src-alpha
    Add,     // PBLEND_ADD    — additive (glow)
    InvMod,  // PBLEND_INVMOD — inverse-modulate (darkening; blood/decals)
}

/// <summary>DP orientation (porientation_t) — billboard vs spark-stretch vs beam.</summary>
public enum EiOrientation
{
    Billboard, // PARTICLE_BILLBOARD
    Spark,     // PARTICLE_SPARK (velocity-stretched)
    Oriented,  // PARTICLE_ORIENTED_DOUBLESIDED (decals)
    Beam,      // PARTICLE_HBEAM/VBEAM
}

/// <summary>
/// One parsed emitter — the C# mirror of <c>particleeffectinfo_t</c> with the same field set and
/// defaults (<c>baselineparticleeffectinfo</c>). Mutable during parse; consumed read-only at spawn.
/// </summary>
public sealed class EffectInfoEmitter
{
    // --- counts ---
    public float CountAbsolute;     // countabsolute
    public float CountMultiplier;   // count (or 1/trailspacing for trails)
    public float TrailSpacing;      // trailspacing (>0 => this block is a trail emitter)

    // --- kind / blend / orientation ---
    public EiType Type = EiType.AlphaStatic;
    public EiBlend Blend = EiBlend.Alpha;
    public EiOrientation Orientation = EiOrientation.Billboard;

    // --- color (hex RRGGBB pair) ---
    public uint Color0 = 0xFFFFFF;
    public uint Color1 = 0xFFFFFF;

    // --- texture range (atlas indices into the DP particlefont) — the sprite drawn for each particle ---
    public int Tex0 = 63;
    public int Tex1 = 63;

    // --- stain (the decal/stainmap a particle leaves where it hits a surface: blood splat, scorch) -------
    // DP particleeffectinfo_t.staintex[2]/staincolor[2]/stainsize[2]/stainalpha[2]. A pt_blood particle
    // that hits the world leaves a tex_blooddecal mark tinted by staincolor (cl_particles.c:3020-3041);
    // explosion/impact blocks declare these so the splat reuses the dedicated blood/scorch decal sprites.
    public int StainTex0 = -1, StainTex1 = -1;     // < 0 => no stain declared
    // DP baseline staincolor = {(unsigned int)-1, (unsigned int)-1} (cl_particles.c:271). staincolor is a
    // MODDING FACTOR on the particle's own colour (0x808080 = neutral), NOT an absolute tint; the sim casts
    // these to int and the -1 (== 0xFFFFFFFF) default takes the "stain = particle colour" shorthand branch
    // (cl_particles.c:740-764). Defaulting 0xFFFFFF (a positive int) would wrongly enter the modding branch.
    public uint StainColor0 = 0xFFFFFFFF, StainColor1 = 0xFFFFFFFF;
    // DP baseline stainsize = {2, 2} (cl_particles.c:274). 22 of the 30 staintex blocks in the shipped
    // effectinfo.txt omit stainsize and inherit this baseline; defaulting 1 would halve their splat size.
    public float StainSizeMin = 2f, StainSizeMax = 2f;
    public float StainAlphaMin = 1f, StainAlphaMax = 1f; // DP stainalpha is 0..1 (already normalized)

    /// <summary>True if this emitter declares a stain texture range (staintex tex0 tex1, tex1 exclusive).</summary>
    public bool HasStain => StainTex0 >= 0 && StainTex1 > StainTex0;

    // --- size {min,max,sizeincrease} / alpha {min,max,alphafade} / time {min,max} ---
    public float SizeMin = 1f, SizeMax = 1f, SizeIncrease;
    public float AlphaMin, AlphaMax = 256f, AlphaFade = 256f;
    public float TimeMin = 16777216f, TimeMax = 16777216f;

    // --- physics ---
    public float Gravity;        // multiplier on world gravity (negative floats up)
    public float Bounce;         // <0 removes on impact (blood splat)
    public float AirFriction;
    public float LiquidFriction;
    public float StretchFactor = 1f;
    public float VelocityMultiplier;

    // --- offsets / jitter (3-vectors) ---
    public NVec3 OriginOffset;
    public NVec3 RelativeOriginOffset;
    public NVec3 VelocityOffset;
    public NVec3 RelativeVelocityOffset;
    public NVec3 OriginJitter;
    public NVec3 VelocityJitter;

    // --- rotation {baseMin,baseMax,spinMin,spinMax} degrees & deg/sec ---
    public float RotateBaseMin, RotateBaseMax = 360f, RotateSpinMin, RotateSpinMax;

    // --- dlight ---
    public float LightRadius;        // lightradius (lightradiusstart)
    public float LightRadiusFade;    // lightradiusfade
    public float LightTime = 16777216f;
    public NVec3 LightColor = new(1f, 1f, 1f);

    // --- water gating (PARTICLEEFFECT_UNDERWATER / NOTUNDERWATER) ---
    public bool Underwater;
    public bool NotUnderwater;

    /// <summary>True if any of this block's lines actually set a parameter (DEFINED flag). Undefined
    /// placeholder blocks (an <c>effect</c> header with no following keywords) are skipped at spawn.</summary>
    public bool Defined;

    /// <summary>
    /// Persistent fractional particle accumulator — the C# mirror of <c>particleeffectinfo_t.particleaccumulator</c>
    /// (Base/darkplaces/cl_particles.c:55-57). Blood and sub-1-count effects spawn well under one particle per
    /// call; DP keeps the fraction here ACROSS calls and only emits whole particles, draining the remainder. The
    /// spawn loop adds this call's <c>cnt</c>, takes the integer part, and leaves the rest for next time — so
    /// e.g. <c>count 0.025</c> yields ~1 particle every ~40 calls instead of over-spawning each call.
    /// </summary>
    public double ParticleAccumulator;

    /// <summary>This block describes a trail emitter (trailspacing &gt; 0) or a beam (only valid as a trail).</summary>
    public bool IsTrailEmitter => TrailSpacing > 0f || Orientation == EiOrientation.Beam;

    // ------------------------------------------------------------------------------------------------
    //  Convenience accessors used by the Godot burst builder (decode the DP packed values)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Midpoint of the color range as linear 0..1 RGB (the representative tint for a Godot burst).</summary>
    public (float R, float G, float B) MidColor()
    {
        (float r0, float g0, float b0) = Unpack(Color0);
        (float r1, float g1, float b1) = Unpack(Color1);
        return ((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f);
    }

    public (float R, float G, float B) Color0Rgb() => Unpack(Color0);
    public (float R, float G, float B) Color1Rgb() => Unpack(Color1);

    private static (float, float, float) Unpack(uint hex)
        => (((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);

    /// <summary>Representative initial opacity 0..1 (alpha midpoint / 256, clamped).</summary>
    public float MidAlpha01() => Math.Clamp((AlphaMin + AlphaMax) * 0.5f / 256f, 0f, 1f);

    /// <summary>
    /// The staincolor MODDING FACTOR (not an absolute tint) for the splat decal — DP staincolor scales the
    /// particle's own colour: <c>stain = staincolor * particlecolor / 0x8000</c> with the random lerp factor
    /// <c>l1+l2 = 256</c>, so a component byte <c>s</c> contributes <c>s/128</c> (0x80 = neutral 1.0). The
    /// <c>(unsigned int)-1</c> default (and any negative staincolor) is the "stain = particle colour" shorthand
    /// (cl_particles.c:740-764), i.e. a neutral factor of 1.0. The caller multiplies the particle colour by
    /// this, matching the live faithful sim's per-particle formula.
    /// </summary>
    public (float R, float G, float B) StainMidColor()
    {
        if ((int)StainColor0 < 0 || (int)StainColor1 < 0)
            return (1f, 1f, 1f); // -1 shorthand: stain = particle colour (no modding)
        (float r0, float g0, float b0) = ModFactor(StainColor0);
        (float r1, float g1, float b1) = ModFactor(StainColor1);
        return ((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f);
    }

    // staincolor byte -> modding factor (0x80 -> 1.0). Mirrors DP's /0x8000 with the 256 lerp weight.
    private static (float, float, float) ModFactor(uint hex)
        => (((hex >> 16) & 0xFF) / 128f, ((hex >> 8) & 0xFF) / 128f, (hex & 0xFF) / 128f);

    /// <summary>Representative stain decal half-size (size midpoint) and opacity (0..1).</summary>
    public float StainMidSize() => MathF.Max(0.5f, (StainSizeMin + StainSizeMax) * 0.5f);
    public float StainMidAlpha01() => Math.Clamp((StainAlphaMin + StainAlphaMax) * 0.5f, 0f, 1f);

    /// <summary>
    /// The particle's effective <i>visible</i> lifetime in seconds. DP removes a particle when either its
    /// <c>time</c> elapses OR its alpha fades to zero (CL_NewParticle: a particle "is also removed if alpha
    /// drops to nothing"). The <c>time</c> field's giant 16777216 default means almost every combat particle
    /// is governed by the alpha fade: it dies in <c>alpha / alphafade</c> seconds. So we take the alpha-fade
    /// duration when no explicit <c>time</c> is set (and the min of the two when both apply), then clamp to a
    /// sane render range. This is why an explosion spark (alpha 256, alphafade 1300) lives ~0.2s, not 6s.
    /// </summary>
    public float Lifetime()
    {
        float midAlpha = MathF.Max(1f, (AlphaMin + AlphaMax) * 0.5f);
        bool explicitTime = TimeMin < 16777216f || TimeMax < 16777216f;
        float timeLife = explicitTime ? (TimeMin + TimeMax) * 0.5f : float.PositiveInfinity;
        float fadeLife = AlphaFade > 0.0001f ? midAlpha / AlphaFade : float.PositiveInfinity;

        float life = MathF.Min(timeLife, fadeLife);
        if (float.IsInfinity(life))
            life = 1f; // neither bound set (e.g. an everlasting static): use a short default for a one-shot.
        return Math.Clamp(life, 0.05f, 6f);
    }
}

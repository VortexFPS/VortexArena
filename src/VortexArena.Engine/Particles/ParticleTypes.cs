using System.Numerics;

namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  Engine-side (Godot-free) mirror of the parsed effectinfo data model. The host's
//  game/client/EffectInfoParticle.cs (EiType/EiBlend/EiOrientation + EffectInfoEmitter) is the parse-time
//  representation; these are the SAME enums in the SAME ORDER so the host converts with a plain cast, and
//  ParticleEmitterInfo is the sim-facing snapshot the faithful backend + parity tests consume. Keeping it
//  here (not in game/) lets ParticleSim live in XonoticGodot.Engine and stay headless-testable.
// =====================================================================================================

/// <summary>DP particle kind (ptype_t). Order mirrors host <c>EiType</c> exactly — cast-compatible.</summary>
public enum ParticleType
{
    AlphaStatic, Static, Spark, Beam, Rain, RainDecal, Snow, Bubble, Blood, Smoke, Decal, EntityParticle,
}

/// <summary>DP blend mode (pblend_t). Order mirrors host <c>EiBlend</c>.</summary>
public enum ParticleBlend { Alpha, Add, InvMod }

/// <summary>DP orientation (porientation_t). Order mirrors host <c>EiOrientation</c>.</summary>
public enum ParticleOrientation { Billboard, Spark, Oriented, Beam }

/// <summary>
/// The sim-facing snapshot of one parsed emitter block — the C# mirror of <c>particleeffectinfo_t</c>
/// (Base/darkplaces/cl_particles.c:50-127) with the field set the faithful simulation reads. Field names
/// and defaults match the host's <see cref="!:EffectInfoEmitter"/> so the converter is a 1:1 copy.
/// Construct directly in tests; the host fills it from a parsed EffectInfoEmitter at spawn.
/// </summary>
public sealed class ParticleEmitterInfo
{
    // --- counts ---
    public float CountAbsolute;
    public float CountMultiplier;
    public float TrailSpacing;

    // --- kind / blend / orientation ---
    public ParticleType Type = ParticleType.AlphaStatic;
    public ParticleBlend Blend = ParticleBlend.Alpha;
    public ParticleOrientation Orientation = ParticleOrientation.Billboard;

    // --- color (hex RRGGBB pair) ---
    public uint Color0 = 0xFFFFFF;
    public uint Color1 = 0xFFFFFF;

    // --- texture range (atlas indices, tex1 exclusive) ---
    public int Tex0 = 63;
    public int Tex1 = 63;

    // --- stain (DP baselineparticleeffectinfo, cl_particles.c:271-274) ---
    public int StainTex0 = -1, StainTex1 = -1;
    // staincolor default {(unsigned int)-1,...}: a MODDING FACTOR on the particle colour (0x808080 neutral),
    // and -1 (== 0xFFFFFFFF, signed -1) is the "stain = particle colour" shorthand the NewParticle branch reads.
    public uint StainColor0 = 0xFFFFFFFF, StainColor1 = 0xFFFFFFFF;
    public float StainSizeMin = 2f, StainSizeMax = 2f; // DP baseline stainsize = {2,2}
    public float StainAlphaMin = 1f, StainAlphaMax = 1f;
    public bool HasStain => StainTex0 >= 0 && StainTex1 > StainTex0;

    // --- size {min,max,sizeincrease} / alpha {min,max,alphafade} / time {min,max} ---
    public float SizeMin = 1f, SizeMax = 1f, SizeIncrease;
    public float AlphaMin, AlphaMax = 256f, AlphaFade = 256f;
    public float TimeMin = 16777216f, TimeMax = 16777216f;

    // --- physics ---
    public float Gravity;
    public float Bounce;
    public float AirFriction;
    public float LiquidFriction;
    public float StretchFactor = 1f;
    public float VelocityMultiplier;

    // --- offsets / jitter (3-vectors) ---
    public Vector3 OriginOffset;
    public Vector3 RelativeOriginOffset;
    public Vector3 VelocityOffset;
    public Vector3 RelativeVelocityOffset;
    public Vector3 OriginJitter;
    public Vector3 VelocityJitter;

    // --- rotation {baseMin,baseMax,spinMin,spinMax} ---
    public float RotateBaseMin, RotateBaseMax = 360f, RotateSpinMin, RotateSpinMax;

    // --- water gating ---
    public bool Underwater;
    public bool NotUnderwater;

    /// <summary>True if this block is a trail emitter (trailspacing &gt; 0) or a beam.</summary>
    public bool IsTrailEmitter => TrailSpacing > 0f || Orientation == ParticleOrientation.Beam;

    /// <summary>
    /// DP <c>particleeffectinfo_t::particleaccumulator</c> (cl_particles.c:208). The fraction-overflow
    /// accumulator shared across every spawn of this block: each <see cref="ParticleSim.SpawnEffect"/> adds
    /// the (fractional) requested count, bounds it to [0,16384], then drains whole particles — so an effect
    /// that asks for e.g. 0.025 particles per call still emits one roughly every 40 calls. Persisted on the
    /// emitter snapshot, NOT reset per spawn (that is the whole point — blood/sparks rely on it).
    /// </summary>
    public double ParticleAccumulator;
}

using System.Numerics;

namespace XonoticGodot.Engine.Particles;

// =====================================================================================================
//  RNG contract for the faithful particle simulation — the parity-critical seam.
//
//  Darkplaces' particle math is driven entirely by lhrandom() and VectorRandom() (Base/darkplaces/
//  mathlib.h:48,119), both built on libc rand(). To get bit-comparable golden traces the C# sim and the
//  C reference harness (tools/particles-ref) must consume rand() in the SAME ORDER and reduce it through
//  the SAME formulas. So the seam is the raw integer draw: IParticleRng.NextRaw() returns one rand()
//  value in [0, RandMax]; ParticleRandom layers the exact lhrandom/VectorRandom reductions on top.
//
//   * In game: XorShiftParticleRng — cheap, deterministic, produces 31-bit values like glibc rand().
//   * In tests: RecordedParticleRng — replays the exact rand() sequence the C reference recorded, so a
//     divergence is pure math, not RNG drift (mirrors tools/movement-ref's recorded-input approach).
// =====================================================================================================

/// <summary>A source of raw rand() draws for the particle sim. <see cref="NextRaw"/> returns one value in
/// [0, <see cref="ParticleRandom.RandMax"/>], matching the C reference's libc rand().</summary>
public interface IParticleRng
{
    /// <summary>The next raw rand() integer in [0, RandMax].</summary>
    int NextRaw();
}

/// <summary>
/// The exact C# transcription of Darkplaces' RNG reductions (mathlib.h). Both the in-game sim and the
/// parity tests reduce raw draws through these, so they stay bit-comparable to the C reference.
/// </summary>
public static class ParticleRandom
{
    /// <summary>glibc RAND_MAX (2^31 - 1). The C reference harness is compiled with gcc/glibc, so this is
    /// the divisor in lhrandom. MUST match the harness exactly.</summary>
    public const int RandMax = 2147483647;

    /// <summary>
    /// DP <c>lhrandom(MIN,MAX) = (((double)(rand()+0.5) / ((double)RAND_MAX+1)) * (MAX-MIN) + MIN)</c>
    /// (mathlib.h:48). Computed in double exactly as the C does; never returns exactly MIN or MAX.
    /// </summary>
    public static double Lhrandom(IParticleRng rng, double min, double max)
        => ((rng.NextRaw() + 0.5) / ((double)RandMax + 1.0)) * (max - min) + min;

    /// <summary>
    /// DP <c>VectorRandom(v)</c> (mathlib.h:119): rejection-sample a uniform point in the unit ball. Each
    /// component is <c>lhrandom(-1,1)</c> NARROWED TO FLOAT (vec3_t is float in DP), and the loop repeats
    /// while <c>dot(v,v) > 1</c> tested on the float components — so the consumed-draw count matches the C.
    /// </summary>
    public static Vector3 VectorRandom(IParticleRng rng)
    {
        Vector3 v;
        do
        {
            v.X = (float)Lhrandom(rng, -1.0, 1.0);
            v.Y = (float)Lhrandom(rng, -1.0, 1.0);
            v.Z = (float)Lhrandom(rng, -1.0, 1.0);
        }
        while (Vector3.Dot(v, v) > 1f);
        return v;
    }
}

/// <summary>
/// In-game RNG: an xorshift generator masked to 31 bits so its range matches glibc rand() ([0, 2^31-1]).
/// Deterministic from a seed; cheap enough for thousands of spawns per frame. Not used in parity tests
/// (those use <see cref="RecordedParticleRng"/>) — the distribution shape is all that matters in game.
/// </summary>
public sealed class XorShiftParticleRng : IParticleRng
{
    private uint _state;

    public XorShiftParticleRng(uint seed = 0x9E3779B9u) => _state = seed == 0 ? 0x9E3779B9u : seed;

    public int NextRaw()
    {
        // xorshift32 (Marsaglia), then drop the top bit to land in [0, 2^31-1] like glibc rand().
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return (int)(x & 0x7FFFFFFFu);
    }
}

/// <summary>
/// Test RNG: replays a recorded sequence of raw rand() draws (the exact integers the C reference consumed,
/// serialized into the golden fixture). Lets <c>ParticleParityTests</c> assert pure-math agreement.
/// </summary>
public sealed class RecordedParticleRng : IParticleRng
{
    private readonly int[] _values;
    private int _index;

    public RecordedParticleRng(int[] values) => _values = values;

    /// <summary>Number of draws consumed so far (for assertions that both sides drew the same count).</summary>
    public int Consumed => _index;

    public int NextRaw()
    {
        if (_index >= _values.Length)
            throw new System.InvalidOperationException(
                $"RecordedParticleRng exhausted after {_values.Length} draws — the sim consumed more rand() " +
                "values than the C reference recorded (RNG order divergence).");
        return _values[_index++];
    }
}

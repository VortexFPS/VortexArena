using System.Numerics;

namespace XonoticGodot.Common.Math;

/// <summary>
/// A deterministic pseudo-random generator INSTANCE (QC prandom/psrandom, lib/random.qc) — the per-context RNG
/// state machine. The server seeds it and broadcasts the seed (QC <c>randomseed</c>) so a predicting client
/// reproduces the same spread/effects (ADR-0010). NOT <see cref="System.Random"/> (whose algorithm isn't
/// guaranteed stable across runtimes). PCG-XSH-RR style.
///
/// <para>[W14a] This was hoisted out of the static <see cref="Prandom"/> facade so a listen server can own TWO
/// independent contexts — one for the server world, one for the client — without their draws corrupting each
/// other's stream (the old process-global static interleaved server + client draws on a listen server). The
/// static <see cref="Prandom"/> API still works (it delegates to a default context), so the ~200 existing callers
/// are unchanged; a context-owner (server/client) sets <see cref="Prandom.Active"/> to route the static calls to
/// its own context. The bit-exact CRC16-vs-PCG algorithm swap vs Base's stream is DEFERRED — no client effect
/// consumes a server seed yet, so there is nothing to desync.</para>
/// </summary>
public sealed class PrandomContext
{
    private ulong _state = 0x853c49e6748fea9bUL;
    private const ulong Inc = 0xda3e39cb94b95bdbUL;

    public void Seed(uint seed)
    {
        _state = 0UL;
        Next();
        _state += seed;
        Next();
    }

    private uint Next()
    {
        ulong old = _state;
        _state = old * 6364136223846793005UL + Inc;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform float in [0,1) (QC random()).</summary>
    public float Float() => (Next() >> 8) * (1.0f / 16777216.0f);

    /// <summary>Uniform float in [-1,1).</summary>
    public float Signed() => Float() * 2f - 1f;

    private float _gaussLast;
    private bool _gaussLastSet;

    /// <summary>
    /// A Gaussian (normal) random variate with mean 0 and standard deviation 1 (QC
    /// <c>gsl_ran_ugaussian</c>, lib/random.qc:87) — the Box-Muller transform, caching the second variate
    /// of each pair exactly as the GSL/QC code does. Uses the deterministic <see cref="Float"/> stream so
    /// server and predicting client agree (ADR-0010). Used by the gauss spread styles (W_CalculateSpread 3/4).
    /// </summary>
    public float Gaussian()
    {
        if (_gaussLastSet)
        {
            _gaussLastSet = false;
            return _gaussLast;
        }

        float a = Float() * (2f * QMath.Pi);
        float b = MathF.Sqrt(-2f * MathF.Log(Float()));
        _gaussLast = MathF.Cos(a) * b;
        _gaussLastSet = true;
        return MathF.Sin(a) * b;
    }

    public float Range(float min, float max) => min + (max - min) * Float();

    public int RangeInt(int minInclusive, int maxExclusive)
        => maxExclusive <= minInclusive ? minInclusive : minInclusive + (int)(Next() % (uint)(maxExclusive - minInclusive));

    /// <summary>
    /// A random vector uniformly distributed inside the UNIT BALL (|v| &lt;= 1), matching the DarkPlaces
    /// engine <c>randomvec()</c> builtin (#91) and the QC <c>prandomvec</c> fallback (lib/random.qc:120,
    /// <c>while (v*v &gt; 1)</c>): rejection-sample a point in the cube [-1,1)^3, retry while it lies
    /// outside the unit ball. This is the distribution every QC <c>randomvec()</c> caller actually sees
    /// (weapon spread density <c>sqrt(1-r^2)</c>, loot/buff/pinata scatter, etc.) — NOT a raw cube, which
    /// would bias toward the 8 corner diagonals and overshoot |v| up to ~1.73. A degenerate
    /// <c>(0,0,-1)</c> fallback after too many rejects mirrors the engine builtin and bounds the loop.
    /// </summary>
    public Vector3 Vec()
    {
        for (int tries = 0; tries < 16; tries++)
        {
            float x = Signed(), y = Signed(), z = Signed();
            if (x * x + y * y + z * z <= 1f)
                return new Vector3(x, y, z);
        }
        return new Vector3(0f, 0f, -1f);
    }

    /// <summary>A spread offset cone around a forward direction (QC W_CalculateSpread core).</summary>
    public Vector3 Spread(Vector3 forward, Vector3 right, Vector3 up, float spread)
    {
        // uniform-in-disc spread, scaled, projected onto the right/up plane
        float r = spread * MathF.Sqrt(Float());
        float a = Float() * 2f * QMath.Pi;
        return QMath.Normalize(forward + right * (r * MathF.Cos(a)) + up * (r * MathF.Sin(a)));
    }
}

/// <summary>
/// The static deterministic-random facade (QC prandom/psrandom). Backward-compatible with every existing caller:
/// the static methods delegate to the <see cref="Active"/> context. A context owner (a server world, a client)
/// sets <see cref="Active"/> to its own <see cref="PrandomContext"/> so its draws stay on its own stream — on a
/// listen server this stops the server and client from corrupting each other's sequence (the [W14a] per-context
/// split). When no owner sets it, a process-default context is used (single-process tools / tests).
/// </summary>
public static class Prandom
{
    /// <summary>The default context used when no owner has installed one (kept for single-process callers/tests).</summary>
    public static PrandomContext Default { get; } = new();

    /// <summary>The context the static API routes to. Defaults to <see cref="Default"/>; a server/client installs
    /// its own so a listen server's two RNGs don't share state. Never null.</summary>
    public static PrandomContext Active { get; set; } = Default;

    public static void Seed(uint seed) => Active.Seed(seed);

    /// <summary>Uniform float in [0,1) (QC random()).</summary>
    public static float Float() => Active.Float();

    /// <summary>Uniform float in [-1,1).</summary>
    public static float Signed() => Active.Signed();

    /// <summary>A Gaussian (normal) variate, mean 0 / stddev 1 (QC gsl_ran_ugaussian).</summary>
    public static float Gaussian() => Active.Gaussian();

    public static float Range(float min, float max) => Active.Range(min, max);

    public static int RangeInt(int minInclusive, int maxExclusive) => Active.RangeInt(minInclusive, maxExclusive);

    /// <summary>A random vector uniformly distributed inside the unit ball (QC randomvec / prandomvec).</summary>
    public static Vector3 Vec() => Active.Vec();

    /// <summary>A spread offset cone around a forward direction (QC W_CalculateSpread core).</summary>
    public static Vector3 Spread(Vector3 forward, Vector3 right, Vector3 up, float spread)
        => Active.Spread(forward, right, up, spread);
}

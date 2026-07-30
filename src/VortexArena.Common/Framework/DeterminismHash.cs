using System.Numerics;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// An order-sensitive FNV-1a checksum over the <b>exact bit patterns</b> of the values fed to it — the tool
/// for validating simulation determinism (REMAINING-WORK §6 / ADR-0010). Two runs of the same deterministic
/// code on the same architecture must produce the same hash; a difference means the simulation diverged
/// (a wall-clock read, an unordered-collection iteration, a <c>System.Random</c>, an FMA/fast-math reorder…).
///
/// Because it hashes the raw IEEE-754 bits (<see cref="System.BitConverter.SingleToUInt32Bits"/>), it is exact:
/// a single-ULP difference flips the hash. That makes it a same-architecture reproducibility guard and a
/// cross-architecture <i>detector</i> — x64 and ARM agree on +,-,*,/,sqrt (IEEE-754 correctly rounded) but may
/// differ in the last ULP of the transcendentals (sin/cos/pow/log/atan2), so a hash mismatch across arches is
/// expected and must be evaluated against the prediction-error envelope rather than treated as a hard failure
/// (see <c>planning/process/determinism.md</c>).
/// </summary>
public struct DeterminismHash
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private ulong _h;

    public static DeterminismHash New() => new() { _h = FnvOffset };

    /// <summary>The accumulated 64-bit hash.</summary>
    public readonly ulong Value => _h;

    public void Add(uint v)
    {
        // hash all four bytes, low to high (fixed order, so the result is endianness-independent)
        _h = (_h ^ (v & 0xFF)) * FnvPrime;
        _h = (_h ^ ((v >> 8) & 0xFF)) * FnvPrime;
        _h = (_h ^ ((v >> 16) & 0xFF)) * FnvPrime;
        _h = (_h ^ ((v >> 24) & 0xFF)) * FnvPrime;
    }

    public void Add(int v) => Add((uint)v);
    public void Add(bool v) => Add(v ? 1u : 0u);

    /// <summary>Hash a float by its exact IEEE-754 bits. Canonicalizes the two zero encodings and all NaNs so
    /// <c>-0</c>/<c>+0</c> and differing NaN payloads don't spuriously diverge the hash.</summary>
    public void Add(float f)
    {
        if (f == 0f) f = 0f;                       // collapse -0 to +0
        else if (float.IsNaN(f)) { Add(0x7FC00000u); return; } // canonical quiet NaN
        Add(BitConverter.SingleToUInt32Bits(f));
    }

    public void Add(Vector3 v) { Add(v.X); Add(v.Y); Add(v.Z); }
    public void Add(Vector2 v) { Add(v.X); Add(v.Y); }
    public void Add(Vector4 v) { Add(v.X); Add(v.Y); Add(v.Z); Add(v.W); }
    public void Add(Quaternion q) { Add(q.X); Add(q.Y); Add(q.Z); Add(q.W); }
}

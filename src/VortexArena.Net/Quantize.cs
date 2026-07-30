using System.Numerics;
using System.Runtime.CompilerServices;

namespace XonoticGodot.Net;

/// <summary>
/// Coordinate/angle/time quantization primitives for the XonoticGodot wire format.
///
/// We reuse the *quantization schemes* from Darkplaces <c>com_msg.c</c> (Coord13i/16i/32f,
/// Angle8i/16i/32f) and the nonlinear "approx past time" + Int24 encodings from Xonotic's
/// <c>qcsrc/lib/net.qh</c>, but per <see href="ADR-0011"/> we own the byte layout — these helpers
/// only produce/consume the integer payloads; <see cref="BitWriter"/>/<see cref="BitReader"/> decide
/// endianness (little-endian, via <see cref="System.Buffers.Binary.BinaryPrimitives"/>).
///
/// All helpers are pure, branch-light, and round-trip-stable: <c>Decode(Encode(x)) == Encode-grid(x)</c>.
/// Port references are noted per-method.
/// </summary>
public static class Quantize
{
    // --- coordinate scales (DP com_msg.c: MSG_WriteCoord13i = Q_rint(f*8)) ---

    /// <summary>Fixed-point scale for the low-precision 13i coordinate path (1/8 unit resolution).</summary>
    public const float Coord13Scale = 8.0f;
    public const float Coord13InvScale = 1.0f / 8.0f;

    // --- angle scales (DP com_msg.c: MSG_WriteAngle8i = round(f*256/360) & 255) ---

    public const float Angle8Scale = 256.0f / 360.0f;
    public const float Angle8InvScale = 360.0f / 256.0f;
    public const float Angle16Scale = 65536.0f / 360.0f;
    public const float Angle16InvScale = 360.0f / 65536.0f;

    // --- approx-past-time (net.qh) ---
    // APPROXPASTTIME_ACCURACY_REQUIREMENT = 0.05
    // APPROXPASTTIME_MAX = 16384 * 0.05 = 819.2   (the "16384" is a bit budget, not seconds)
    // The 1-byte encoding maps 0..255 nonlinearly onto latency, with fine resolution near zero
    // (accuracy ~MAX/(256*255) ≈ 0.0125s at dt≈0) coarsening with distance. With the QC constant
    // K = MAX/256 = 3.2, the curve is dt_decoded = K*(q/(256-q)); q=128 ≈ 3.2s, q=255 ≈ 816s. (The stale
    // "255 -> 12.75" comment in net.qh would only hold if K were 0.05; we port the QC arithmetic as-is.)
    public const float ApproxPastTimeAccuracy = 0.05f;
    public const float ApproxPastTimeMax = 16384f * ApproxPastTimeAccuracy; // 819.2
    private const float ApproxPastTimeK = ApproxPastTimeMax / 256f;          // 3.2

    /// <summary>Round to nearest, ties away from zero — matches Darkplaces <c>Q_rint</c> (rint with FE_TONEAREST,
    /// but the DP/QC pipeline rounds halves away from zero). We use <see cref="MathF.Round(float, MidpointRounding)"/>
    /// with <see cref="MidpointRounding.AwayFromZero"/> for determinism across runtimes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Rint(float f) => (int)MathF.Round(f, MidpointRounding.AwayFromZero);

    // ---------------------------------------------------------------------
    // Coordinates
    // ---------------------------------------------------------------------

    /// <summary>13i encode: <c>short(round(value * 8))</c>. Range ±4096 units at 1/8 resolution.
    /// Port of <c>MSG_WriteCoord13i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short EncodeCoord13(float value) => (short)Rint(value * Coord13Scale);

    /// <summary>13i decode: <c>short / 8</c>. Port of <c>MSG_ReadCoord13i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DecodeCoord13(short q) => q * Coord13InvScale;

    /// <summary>16i encode: <c>short(round(value))</c> — whole-unit resolution, ±32768 range.
    /// Port of <c>MSG_WriteCoord16i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short EncodeCoord16(float value) => (short)Rint(value);

    /// <summary>16i decode: signed short as-is. Port of <c>MSG_ReadCoord16i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DecodeCoord16(short q) => q;

    /// <summary>Snap a coordinate to the 13i grid without serializing (for prediction-error comparison
    /// against what the wire can actually represent).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SnapCoord13(float value) => DecodeCoord13(EncodeCoord13(value));

    // ---------------------------------------------------------------------
    // Angles
    // ---------------------------------------------------------------------

    /// <summary>8i encode: <c>byte(round(deg * 256/360)) &amp; 255</c>. Port of <c>MSG_WriteAngle8i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte EncodeAngle8(float deg) => (byte)(Rint(deg * Angle8Scale) & 255);

    /// <summary>8i decode: <c>(signed char) * 360/256</c>. The DP reader casts the byte to a *signed* char,
    /// yielding [-180, ~180) degrees; callers that want [0,360) can add 360 when negative.
    /// Port of <c>MSG_ReadAngle8i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DecodeAngle8(byte q) => (sbyte)q * Angle8InvScale;

    /// <summary>16i encode: <c>short(round(deg * 65536/360)) &amp; 65535</c>. Port of <c>MSG_WriteAngle16i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort EncodeAngle16(float deg) => (ushort)(Rint(deg * Angle16Scale) & 65535);

    /// <summary>16i decode: <c>(signed short) * 360/65536</c>. Port of <c>MSG_ReadAngle16i</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DecodeAngle16(ushort q) => (short)q * Angle16InvScale;

    /// <summary>Snap an angle to the 8i grid without serializing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SnapAngle8(float deg) => DecodeAngle8(EncodeAngle8(deg));

    // ---------------------------------------------------------------------
    // Int24 (net.qh WriteInt24_t / ReadInt24_t) — short<<8 + byte
    // ---------------------------------------------------------------------
    // QC encode: WriteShort(floor(val >> 8)); WriteByte(val - (hi << 8))
    // QC decode: v = ReadShort() << 8 (signed); v += ReadByte() (unsigned)
    // i.e. the high 16 bits are a signed short, the low 8 bits an unsigned byte.
    // Representable signed range: [-2^23, 2^23 - 1].

    /// <summary>Split a signed 24-bit value into its (signed high short, unsigned low byte) parts,
    /// matching <c>WriteInt24_t</c>. Use with <see cref="BitWriter.WriteInt24"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EncodeInt24(int value, out short high, out byte low)
    {
        // Arithmetic shift keeps the sign (QC uses floor(val>>8); for negative values floor == arithmetic >>).
        high = (short)(value >> 8);
        low = (byte)(value & 0xFF);
    }

    /// <summary>Reassemble a signed 24-bit value from (signed high short, unsigned low byte),
    /// matching <c>ReadInt24_t</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DecodeInt24(short high, byte low) => (high << 8) + low;

    // ---------------------------------------------------------------------
    // Approx past time (net.qh WriteApproxPastTime / ReadApproxPastTime)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Encode a latency <paramref name="dt"/> (seconds, server-time minus event-time) into a single byte,
    /// nonlinearly with finer resolution near zero (the regime that matters for lag comp). Port of
    /// <c>WriteApproxPastTime</c>. Caller computes <c>dt = serverTime - eventTime</c> and passes it here (the
    /// QC version reads the global <c>time</c>; we keep it explicit for testability/determinism).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte EncodeApproxPastTime(float dt)
    {
        // map to range...
        float d = 256f * (dt / (ApproxPastTimeK + dt));
        // round + clamp to a byte
        d = MathF.Round(QMathClamp(d, 0f, 255f), MidpointRounding.AwayFromZero);
        return (byte)d;
    }

    /// <summary>
    /// Decode the past-time byte back to a latency in seconds (the inverse mapping of
    /// <see cref="EncodeApproxPastTime"/>). Port of <c>ReadApproxPastTime</c>; the QC version returns
    /// <c>servertime - dt</c>, here we return <c>dt</c> and let the caller subtract from its clock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DecodeApproxPastTime(byte q)
    {
        // map from range... (guard the 255 endpoint where (256-q) -> 1, not 0)
        float d = q;
        return ApproxPastTimeK * (d / (256f - d));
    }

    // Local clamp to avoid taking a dependency on XonoticGodot.Common.Math here (this file is leaf-level).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float QMathClamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}

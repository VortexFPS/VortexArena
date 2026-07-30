using System.Numerics;

namespace XonoticGodot.Common.Math;

/// <summary>
/// Quake-style vector &amp; angle math. The vector type throughout XonoticGodot is
/// <see cref="System.Numerics.Vector3"/>.
///
/// IMPORTANT semantic note for porters: in QuakeC the operator <c>*</c> applied to two vectors is the
/// DOT product, and <c>&gt;&lt;</c> is the CROSS product. In C# those are <see cref="Vector3"/> ops, so
/// translate <c>a * b</c> (QC) to <see cref="Dot"/>(a, b), and <c>a &gt;&lt; b</c> to <see cref="Cross"/>(a, b).
///
/// Angles are Quake Euler angles in DEGREES, ordered (pitch=X, yaw=Y, roll=Z).
/// AngleVectors is a faithful port of Darkplaces <c>AngleVectors</c> (mathlib.c) — part of the
/// fidelity contract (planning/specs/determinism-and-physics.md).
/// </summary>
public static class QMath
{
    public const float Pi = 3.14159265358979323846f;
    public const float Deg2Rad = Pi / 180f;
    public const float Rad2Deg = 180f / Pi;

    // Quake angle component indices.
    public const int Pitch = 0, Yaw = 1, Roll = 2;

    public static float Dot(Vector3 a, Vector3 b) => Vector3.Dot(a, b);
    public static Vector3 Cross(Vector3 a, Vector3 b) => Vector3.Cross(a, b);
    public static float VLen(Vector3 v) => v.Length();

    /// <summary>Normalize, returning zero for a zero-length vector (Quake semantics, unlike Vector3.Normalize).</summary>
    public static Vector3 Normalize(Vector3 v)
    {
        float len = v.Length();
        return len > 0f ? v / len : Vector3.Zero;
    }

    /// <summary>
    /// makevectors(): produce forward/right/up basis from Euler angles (degrees). Port of DP <c>AngleVectors</c>
    /// (mathlib.c). Note the Quake pitch convention: <c>forward.Z = -sin(pitch)</c>, so a <b>positive</b> pitch aims
    /// forward <b>down</b> (−Z). That is the opposite sign from <see cref="VecToAngles"/>; see the remarks there for
    /// why a <see cref="VecToAngles"/>→<see cref="AngleVectors"/> round-trip mirrors pitch, and use
    /// <see cref="FixedVecToAngles"/> when you need it not to.
    /// </summary>
    public static void AngleVectors(Vector3 angles, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        float ay = angles.Y * Deg2Rad; float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
        float ap = angles.X * Deg2Rad; float sp = MathF.Sin(ap), cp = MathF.Cos(ap);
        float ar = angles.Z * Deg2Rad; float sr = MathF.Sin(ar), cr = MathF.Cos(ar);

        forward = new Vector3(cp * cy, cp * sy, -sp);
        right = new Vector3(
            -sr * sp * cy + cr * sy,
            -sr * sp * sy - cr * cy,
            -sr * cp);
        up = new Vector3(
            cr * sp * cy + sr * sy,
            cr * sp * sy - sr * cy,
            cr * cp);
    }

    /// <summary>Forward-only fast path of AngleVectors (yaw/pitch only).</summary>
    public static Vector3 Forward(Vector3 angles)
    {
        float ay = angles.Y * Deg2Rad; float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
        float ap = angles.X * Deg2Rad; float sp = MathF.Sin(ap), cp = MathF.Cos(ap);
        return new Vector3(cp * cy, cp * sy, -sp);
    }

    /// <summary>
    /// vectoangles(): direction vector → Euler angles <c>(pitch, yaw, 0)</c> in degrees, each wrapped to
    /// <c>[0, 360)</c>. Faithful port of the QuakeC <c>vectoangles()</c> builtin
    /// (DP <c>VM_vectoangles</c> → <c>AnglesFromVectors(..., flippitch:true)</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Pitch sign — read this before deriving angles you intend to re-vector.</b> This returns
    /// <c>pitch = atan2(z, horiz)</c>, so a direction pointing <b>up</b> (+Z) yields a <b>positive</b> pitch. That is
    /// the <b>opposite</b> sign convention from <see cref="AngleVectors"/>/<c>makevectors</c>, which encodes
    /// <c>forward.Z = -sin(pitch)</c> (positive pitch aims <b>down</b>). The two are deliberately inverse on pitch:
    /// <c>VecToAngles(AngleVectors(a).forward).X == -a.pitch</c> (mod 360). Yaw round-trips cleanly.</para>
    /// <para>This is <b>not</b> a missing negation — it reproduces DarkPlaces exactly. In <c>AnglesFromVectors</c>
    /// (mathlib.c) the pitch is computed as <c>-atan2(z, horiz)</c> and then negated <i>again</i> under
    /// <c>flippitch</c> — the flag the <c>vectoangles</c> builtin passes — so the net engine result is
    /// <c>+atan2(z, horiz)</c>, matching the body below. Quake's <c>makevectors</c>/<c>vectoangles</c> pair has
    /// always been non-inverting on pitch; Xonotic ships a <c>fixedvectoangles</c> macro
    /// (lib/warpzone/anglestransform.qh, whose own comment calls it "a makevectors that actually inverts
    /// vectoangles") precisely to bridge the gap.</para>
    /// <para><b>When you need a round-trippable angle</b> — one that, fed back through <see cref="AngleVectors"/>,
    /// reproduces the input direction (look-at facing, motion-derived orientation that must match the renderer,
    /// snapshot angle interpolation) — use <see cref="FixedVecToAngles"/>, the port of <c>fixedvectoangles</c>.
    /// Projectiles that set <c>angles = vectoangles(velocity)</c> in the QC originals (client/weapons/projectile.qc
    /// and the per-weapon attacks) deliberately do <b>not</b> flip, so the many projectile call sites that use
    /// <see cref="VecToAngles"/> directly are faithful as-is.</para>
    /// </remarks>
    public static Vector3 VecToAngles(Vector3 value)
    {
        float yaw, pitch;
        if (value.Y == 0f && value.X == 0f)
        {
            yaw = 0f;
            pitch = value.Z > 0f ? 90f : 270f;
        }
        else
        {
            yaw = MathF.Atan2(value.Y, value.X) * Rad2Deg;
            if (yaw < 0f) yaw += 360f;
            float fwd = MathF.Sqrt(value.X * value.X + value.Y * value.Y);
            pitch = MathF.Atan2(value.Z, fwd) * Rad2Deg;
            if (pitch < 0f) pitch += 360f;
        }
        return new Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// fixedvectoangles(): like <see cref="VecToAngles"/> but with pitch negated, so the result — when re-vectored
    /// through <see cref="AngleVectors"/>/<c>makevectors</c> — reproduces the input direction instead of mirroring it
    /// vertically. Faithful port of Xonotic's <c>fixedvectoangles</c> macro (lib/warpzone/anglestransform.qh, default
    /// <c>POSITIVE_PITCH_IS_DOWN</c>). Use this wherever the QC original uses <c>fixedvectoangles</c> or the manual
    /// <c>angles_x = -angles_x</c> flip (func_train facing, view aiming, csqcmodel angle interpolation); use plain
    /// <see cref="VecToAngles"/> where the QC sets <c>angles = vectoangles(...)</c> unflipped (projectiles).
    /// </summary>
    public static Vector3 FixedVecToAngles(Vector3 dir)
    {
        Vector3 angles = VecToAngles(dir);
        angles.X = -angles.X;   // convert vectoangles' "up-positive" pitch to makevectors' "down-positive" frame
        return angles;
    }

    /// <summary>
    /// vectoangles2(): direction + up reference → Euler angles, preserving roll about <paramref name="forward"/>.
    /// Faithful port of DarkPlaces <c>AnglesFromVectors(forward, up, flippitch:true)</c> (mathlib.c), the
    /// 2-argument form of the <c>vectoangles</c> builtin. <c>up == null</c> equivalent: pass <c>'1 0 0'</c>
    /// (roll 0). Like <see cref="VecToAngles"/> this returns "up-positive" pitch; use
    /// <see cref="FixedVecToAngles2"/> for the round-trippable (makevectors-consistent) form.
    /// </summary>
    public static Vector3 VecToAngles2(Vector3 forward, Vector3 up)
    {
        float pitch, yaw, roll;
        if (forward.X == 0f && forward.Y == 0f)
        {
            if (forward.Z > 0f) { pitch = -Pi * 0.5f; yaw = MathF.Atan2(-up.Y, -up.X); }
            else { pitch = Pi * 0.5f; yaw = MathF.Atan2(up.Y, up.X); }
            roll = 0f;
        }
        else
        {
            yaw = MathF.Atan2(forward.Y, forward.X);
            pitch = -MathF.Atan2(forward.Z, MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y));
            float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
            float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
            Vector3 tleft = new(-sy, cy, 0f);
            Vector3 tup = new(sp * cy, sp * sy, cp);
            roll = -MathF.Atan2(Dot(up, tleft), Dot(up, tup));
        }
        pitch *= Rad2Deg; yaw *= Rad2Deg; roll *= Rad2Deg;
        pitch *= -1f; // VM_vectoangles passes flippitch=true
        if (pitch < 0f) pitch += 360f;
        if (yaw < 0f) yaw += 360f;
        if (roll < 0f) roll += 360f;
        return new Vector3(pitch, yaw, roll);
    }

    /// <summary>fixedvectoangles2(): <see cref="VecToAngles2"/> with pitch negated (makevectors-consistent).</summary>
    public static Vector3 FixedVecToAngles2(Vector3 forward, Vector3 up)
    {
        Vector3 a = VecToAngles2(forward, up);
        a.X = -a.X;
        return a;
    }

    /// <summary>
    /// QC <c>AnglesTransform_Apply(transform, v)</c> (lib/warpzone/anglestransform.qc): rotate the vector
    /// <paramref name="v"/> by the basis of <paramref name="transform"/> — <c>forward*v.x + right*-v.y + up*v.z</c>
    /// where (forward,right,up) = <c>FIXED_MAKE_VECTORS(transform)</c> (which, with the default
    /// POSITIVE_PITCH_IS_DOWN, is plain <see cref="AngleVectors"/>).
    /// </summary>
    public static Vector3 AnglesTransformApply(Vector3 transform, Vector3 v)
    {
        AngleVectors(transform, out Vector3 forward, out Vector3 right, out Vector3 up);
        return forward * v.X + right * -v.Y + up * v.Z;
    }

    /// <summary>
    /// QC <c>AnglesTransform_Multiply(t1, t2)</c> (lib/warpzone/anglestransform.qc): compose two angle
    /// transforms — make the basis of <paramref name="t2"/>, rotate its forward+up by <paramref name="t1"/>, then
    /// read the result back with <see cref="FixedVecToAngles2"/>.
    /// </summary>
    public static Vector3 AnglesTransformMultiply(Vector3 t1, Vector3 t2)
    {
        AngleVectors(t2, out Vector3 forward, out _, out Vector3 up);
        forward = AnglesTransformApply(t1, forward);
        up = AnglesTransformApply(t1, up);
        return FixedVecToAngles2(forward, up);
    }

    /// <summary>
    /// QC <c>AnglesTransform_ApplyToAngles(transform, v)</c> (default POSITIVE_PITCH_IS_DOWN branch): apply an
    /// angle transform to a set of entity angles, accounting for the pitch-sign flip — negate pitch, multiply,
    /// negate pitch back. Used by the <c>make</c> cheat's surface-align (<c>transform=fixedvectoangles2(normal,
    /// forward)</c>, <c>v='-90 0 0'</c>) so unrotated models stand up on the hit surface.
    /// </summary>
    public static Vector3 AnglesTransformApplyToAngles(Vector3 transform, Vector3 v)
    {
        v.X = -v.X;
        v = AnglesTransformMultiply(transform, v);
        v.X = -v.X;
        return v;
    }

    /// <summary>
    /// QC <c>AnglesTransform_Normalize(t, minimize_roll)</c> (lib/warpzone/anglestransform.qc): bring an Euler
    /// angle triple into canonical range — yaw/roll wrapped to (−180, 180], pitch folded into [−90, 90] (with a
    /// 180° yaw+roll flip when it falls outside). Base ends <c>WarpZone_TransformVAngles</c> with this; without it
    /// a <see cref="FixedVecToAngles"/> result (pitch in (−360, 0]) reaches a view consumer that
    /// <c>Mathf.Clamp(pitch, -89, 89)</c>s it — so an exit facing even slightly DOWN (raw pitch ≈ −355) clamps to
    /// −89 and the view snaps STRAIGHT UP. <paramref name="minimizeRoll"/> false = keep pitch in range (view/body
    /// angles); true = keep roll in range instead (the camera-transform case). <c>rint</c> == round-half-to-even,
    /// which <see cref="MathF.Round"/> does by default.
    /// </summary>
    public static Vector3 AnglesTransformNormalize(Vector3 t, bool minimizeRoll = false)
    {
        t.X -= 360f * MathF.Round(t.X / 360f);
        t.Y -= 360f * MathF.Round(t.Y / 360f);
        t.Z -= 360f * MathF.Round(t.Z / 360f);
        bool needFlip = minimizeRoll ? (t.Z > 90f || t.Z <= -90f) : (t.X > 90f || t.X < -90f);
        if (needFlip)
        {
            t.X = t.X >= 0f ? 180f - t.X : -180f - t.X;
            t.Y = t.Y > 0f ? t.Y - 180f : t.Y + 180f;
            t.Z = t.Z > 0f ? t.Z - 180f : t.Z + 180f;
        }
        return t;
    }

    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>QuakeC bound(min, value, max).</summary>
    public static float Bound(float lo, float v, float hi) => Clamp(v, lo, hi);
}

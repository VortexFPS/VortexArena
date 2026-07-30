using System.Numerics;

namespace XonoticGodot.Common.Math;

/// <summary>
/// A bone transform — the C# stand-in for DarkPlaces <c>matrix4x4_t</c> as used by the skeletal builtins
/// (<c>skel_*</c>). It is a 3×4 affine map stored as its three basis COLUMNS (<see cref="Fwd"/>,
/// <see cref="Left"/>, <see cref="Up"/> — DP's forward/left/up) plus the translation <see cref="Origin"/>,
/// exactly matching <c>Matrix4x4_FromVectors</c>/<c>Matrix4x4_ToVectors</c>. A point maps as
/// <c>Fwd*p.x + Left*p.y + Up*p.z + Origin</c>.
///
/// The skeletal builtins read/write the rotation through QuakeC's <c>v_forward/v_right/v_up</c> globals, where
/// <c>v_right = -Left</c> (DP negates <c>left</c> into <c>v_right</c>); <see cref="ToVectors"/>/
/// <see cref="FromVectors"/> here keep the left-handed internal basis and callers do that negation at the
/// boundary (mirroring <c>VM_CL_skel_get_bonerel</c> et al.).
/// </summary>
public readonly struct BoneMatrix
{
    public readonly Vector3 Fwd;     // column 0 (DP forward / vx)
    public readonly Vector3 Left;    // column 1 (DP left   / vy)
    public readonly Vector3 Up;      // column 2 (DP up     / vz)
    public readonly Vector3 Origin;  // column 3 (translation)

    public BoneMatrix(Vector3 fwd, Vector3 left, Vector3 up, Vector3 origin)
    {
        Fwd = fwd; Left = left; Up = up; Origin = origin;
    }

    /// <summary>The identity transform (DP <c>identitymatrix</c>).</summary>
    public static readonly BoneMatrix Identity =
        new(new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), Vector3.Zero);

    /// <summary>The all-zero matrix (the accumulation seed for the weighted frame blend in skel_build).</summary>
    public static readonly BoneMatrix Zero = new(Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero);

    /// <summary>Matrix4x4_FromVectors: build from the three basis columns + translation.</summary>
    public static BoneMatrix FromVectors(Vector3 fwd, Vector3 left, Vector3 up, Vector3 origin)
        => new(fwd, left, up, origin);

    /// <summary>Matrix4x4_ToVectors: read out the three basis columns + translation.</summary>
    public void ToVectors(out Vector3 fwd, out Vector3 left, out Vector3 up, out Vector3 origin)
    {
        fwd = Fwd; left = Left; up = Up; origin = Origin;
    }

    /// <summary>Rotate a direction (the linear part only) — Fwd*v.x + Left*v.y + Up*v.z.</summary>
    public Vector3 Rotate(Vector3 v) => Fwd * v.X + Left * v.Y + Up * v.Z;

    /// <summary>Transform a point — <see cref="Rotate"/> + <see cref="Origin"/>.</summary>
    public Vector3 Transform(Vector3 p) => Rotate(p) + Origin;

    /// <summary>Matrix4x4_Concat: matrix product <c>a · b</c> (apply b, then a).</summary>
    public static BoneMatrix Concat(in BoneMatrix a, in BoneMatrix b)
        => new(a.Rotate(b.Fwd), a.Rotate(b.Left), a.Rotate(b.Up), a.Rotate(b.Origin) + a.Origin);

    /// <summary>
    /// Matrix4x4_Interpolate: <c>a·(1-frac) + b·frac</c>, element-wise on every column. (skel_build uses this
    /// with <c>a</c>=new pose, <c>b</c>=old pose, <c>frac</c>=retainfrac → "how much of the old pose to keep".)
    /// </summary>
    public static BoneMatrix Interpolate(in BoneMatrix a, in BoneMatrix b, float frac)
    {
        float ia = 1f - frac;
        return new BoneMatrix(
            a.Fwd * ia + b.Fwd * frac,
            a.Left * ia + b.Left * frac,
            a.Up * ia + b.Up * frac,
            a.Origin * ia + b.Origin * frac);
    }

    /// <summary>Matrix4x4_Accumulate: <c>this + m·weight</c> (column-wise; used to sum weighted frame poses).</summary>
    public BoneMatrix Accumulate(in BoneMatrix m, float weight)
        => new(Fwd + m.Fwd * weight, Left + m.Left * weight, Up + m.Up * weight, Origin + m.Origin * weight);

    /// <summary>Matrix4x4_Normalize3: scale each basis column back to unit length (renormalize a blended rotation).</summary>
    public BoneMatrix Normalize3()
        => new(SafeNormalize(Fwd), SafeNormalize(Left), SafeNormalize(Up), Origin);

    private static Vector3 SafeNormalize(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-12f ? v / len : v;
    }

    /// <summary>
    /// Matrix4x4_FromBonePose: build the affine bone matrix from a bone-local TRS (translate, unit quaternion,
    /// scale). The three rotated basis vectors (scaled) become the columns; the translation is the origin.
    /// </summary>
    public static BoneMatrix FromTRS(Vector3 translate, Quaternion q, Vector3 scale)
    {
        // rotation basis columns from the quaternion (column-vector convention: R·p = col0*x + col1*y + col2*z)
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        Vector3 col0 = new(1f - 2f * (y * y + z * z), 2f * (x * y + z * w), 2f * (x * z - y * w));
        Vector3 col1 = new(2f * (x * y - z * w), 1f - 2f * (x * x + z * z), 2f * (y * z + x * w));
        Vector3 col2 = new(2f * (x * z + y * w), 2f * (y * z - x * w), 1f - 2f * (x * x + y * y));
        return new BoneMatrix(col0 * scale.X, col1 * scale.Y, col2 * scale.Z, translate);
    }

    /// <summary>
    /// The inverse, assuming an orthonormal rotation (the case for animation-blended bones, scale 1): the
    /// rotation inverts to its transpose and the translation to <c>-Rᵀ·T</c>. Used by the "set bone in model
    /// space" path (relative = parentAbs⁻¹ · desiredAbs), the matrix-native equivalent of the QC
    /// <c>AnglesTransform</c> divide in <c>skel_set_boneabs</c>.
    /// </summary>
    public BoneMatrix InverseOrthonormal()
    {
        // Rᵀ columns are the rows of R: (Fwd.x,Left.x,Up.x), (Fwd.y,Left.y,Up.y), (Fwd.z,Left.z,Up.z).
        Vector3 ix = new(Fwd.X, Left.X, Up.X);
        Vector3 iy = new(Fwd.Y, Left.Y, Up.Y);
        Vector3 iz = new(Fwd.Z, Left.Z, Up.Z);
        Vector3 it = -(ix * Origin.X + iy * Origin.Y + iz * Origin.Z);
        return new BoneMatrix(ix, iy, iz, it);
    }

    /// <summary>The pure rotation→angles read used by callers that want the bone's orientation as Quake
    /// Euler angles (makevectors-consistent, like <c>fixedvectoangles2(v_forward, v_up)</c>).</summary>
    public Vector3 ToAngles() => QMath.FixedVecToAngles2(SafeNormalize(Fwd), SafeNormalize(Up));
}

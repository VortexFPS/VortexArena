using System.Numerics;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// A rigid (rotation + translation, uniform-scale-1) 3x4 transform — the collision-relevant slice of
/// Darkplaces' <c>matrix4x4_t</c> (matrixlib.c, non-OpenGL row-major orientation: <c>m[row][col]</c>,
/// bottom row implicitly <c>0 0 0 1</c>). It is the local→world transform of a brush-model entity:
/// columns 0/1/2 are the entity's forward/left/up basis, column 3 (M03/M13/M23) is its origin.
///
/// This is the exact port of <c>Matrix4x4_CreateFromQuakeEntity(origin, pitch, yaw, roll, scale=1)</c>
/// that <c>SV_ClipMoveToEntity</c> builds for a SOLID_BSP edict, plus the helpers
/// <c>Matrix4x4_Transform</c> (point), <c>Matrix4x4_TransformPositivePlane</c> (plane), and
/// <c>Matrix4x4_Invert_Simple</c> (rigid inverse). Used by <see cref="TraceService"/> to clip a moving
/// box against a rotated brush model in the entity's local space and transform the impact plane back to
/// world space (Collision_ClipToGenericEntity / Collision_TransformBrush, collision.c).
///
/// Scale is fixed at 1 here (engine entities never carry a collision scale), so the plane-normal scale
/// term in <c>Matrix4x4_TransformPositivePlane</c> and the inverse-scale term in
/// <c>Matrix4x4_Invert_Simple</c> both reduce to 1, which is asserted by construction.
/// </summary>
public readonly struct EntityMatrix
{
    // Row-major 3x4: [ M00 M01 M02 M03 ; M10 M11 M12 M13 ; M20 M21 M22 M23 ].
    public readonly float M00, M01, M02, M03;
    public readonly float M10, M11, M12, M13;
    public readonly float M20, M21, M22, M23;

    public EntityMatrix(
        float m00, float m01, float m02, float m03,
        float m10, float m11, float m12, float m13,
        float m20, float m21, float m22, float m23)
    {
        M00 = m00; M01 = m01; M02 = m02; M03 = m03;
        M10 = m10; M11 = m11; M12 = m12; M13 = m13;
        M20 = m20; M21 = m21; M22 = m22; M23 = m23;
    }

    public static readonly EntityMatrix Identity = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0);

    /// <summary>
    /// Port of <c>Matrix4x4_CreateFromQuakeEntity(out, x, y, z, pitch, yaw, roll, 1)</c> (matrixlib.c:715,
    /// non-OpenGL branch). <paramref name="angles"/> is the Quake Euler triple in degrees (pitch=X, yaw=Y,
    /// roll=Z). The four progressively-cheaper branches (roll / pitch / yaw / none) are reproduced exactly so
    /// the bit pattern matches DP for the common no-rotation and yaw-only cases.
    /// </summary>
    public static EntityMatrix FromQuakeEntity(Vector3 origin, Vector3 angles)
    {
        float x = origin.X, y = origin.Y, z = origin.Z;
        float pitch = angles.X, yaw = angles.Y, roll = angles.Z;

        if (roll != 0f)
        {
            float ay = yaw * Deg2Rad; float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
            float ap = pitch * Deg2Rad; float sp = MathF.Sin(ap), cp = MathF.Cos(ap);
            float ar = roll * Deg2Rad; float sr = MathF.Sin(ar), cr = MathF.Cos(ar);
            return new EntityMatrix(
                cp * cy,                 sr * sp * cy + cr * -sy,   cr * sp * cy + -sr * -sy,  x,
                cp * sy,                 sr * sp * sy + cr * cy,    cr * sp * sy + -sr * cy,   y,
                -sp,                     sr * cp,                   cr * cp,                   z);
        }
        if (pitch != 0f)
        {
            float ay = yaw * Deg2Rad; float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
            float ap = pitch * Deg2Rad; float sp = MathF.Sin(ap), cp = MathF.Cos(ap);
            return new EntityMatrix(
                cp * cy,   -sy,   sp * cy,   x,
                cp * sy,    cy,   sp * sy,   y,
                -sp,        0f,   cp,        z);
        }
        if (yaw != 0f)
        {
            float ay = yaw * Deg2Rad; float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
            return new EntityMatrix(
                cy,  -sy,  0f,  x,
                sy,   cy,  0f,  y,
                0f,   0f,  1f,  z);
        }
        return new EntityMatrix(
            1f, 0f, 0f, x,
            0f, 1f, 0f, y,
            0f, 0f, 1f, z);
    }

    /// <summary>True when this is a pure translation (no rotation component) — DP's fast SOLID_BSP path.</summary>
    public bool IsTranslationOnly =>
        M01 == 0f && M02 == 0f && M10 == 0f && M12 == 0f && M20 == 0f && M21 == 0f;

    /// <summary>This matrix's 3x3 rotation with the translation column zeroed (rotation about the origin).</summary>
    public EntityMatrix RotationOnly() => new(
        M00, M01, M02, 0f,
        M10, M11, M12, 0f,
        M20, M21, M22, 0f);

    /// <summary>Port of <c>Matrix4x4_Transform</c> (matrixlib.c:1657, non-OpenGL): transform a point.</summary>
    public Vector3 TransformPoint(Vector3 v) => new(
        v.X * M00 + v.Y * M01 + v.Z * M02 + M03,
        v.X * M10 + v.Y * M11 + v.Z * M12 + M13,
        v.X * M20 + v.Y * M21 + v.Z * M22 + M23);

    /// <summary>Port of <c>Matrix4x4_Transform3x3</c>: rotate a direction (ignore the translation column).</summary>
    public Vector3 TransformDirection(Vector3 v) => new(
        v.X * M00 + v.Y * M01 + v.Z * M02,
        v.X * M10 + v.Y * M11 + v.Z * M12,
        v.X * M20 + v.Y * M21 + v.Z * M22);

    /// <summary>
    /// Port of <c>Matrix4x4_TransformPositivePlane</c> (matrixlib.c:1699, non-OpenGL) for scale 1: rotate the
    /// plane normal by the matrix basis and shift the distance by the projection of the translation onto the
    /// transformed normal. Returns (normal, dist) for the plane <c>Dot(n, p) = dist</c>.
    /// </summary>
    public (Vector3 normal, float dist) TransformPositivePlane(Vector3 n, float d)
    {
        Vector3 o = new(
            n.X * M00 + n.Y * M01 + n.Z * M02,
            n.X * M10 + n.Y * M11 + n.Z * M12,
            n.X * M20 + n.Y * M21 + n.Z * M22);
        float dist = d + (o.X * M03 + o.Y * M13 + o.Z * M23);
        return (o, dist);
    }

    /// <summary>
    /// Port of <c>Matrix4x4_Invert_Simple</c> (matrixlib.c:422) for a rigid (scale-1) matrix: transpose the
    /// 3x3 rotation and apply it to the negated translation. Gives the world→local transform from a
    /// local→world one.
    /// </summary>
    public EntityMatrix Inverted()
    {
        // scale == 1 ⇒ inverse rotation is the transpose.
        float i00 = M00, i01 = M10, i02 = M20;
        float i10 = M01, i11 = M11, i12 = M21;
        float i20 = M02, i21 = M12, i22 = M22;

        float i03 = -(M03 * i00 + M13 * i01 + M23 * i02);
        float i13 = -(M03 * i10 + M13 * i11 + M23 * i12);
        float i23 = -(M03 * i20 + M13 * i21 + M23 * i22);

        return new EntityMatrix(
            i00, i01, i02, i03,
            i10, i11, i12, i13,
            i20, i21, i22, i23);
    }

    private const float Deg2Rad = MathF.PI / 180f;
}

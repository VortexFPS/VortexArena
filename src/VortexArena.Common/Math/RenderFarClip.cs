using System.Numerics;

namespace VortexArena.Common.Math;

/// <summary>
/// The view's far clip distance, as DarkPlaces computes it (gl_rmain.c R_UpdateVariables):
/// <c>farclip = r_farclip_base + worldmodel-&gt;radius * r_farclip_world * 2</c>, with the cvar defaults
/// <c>r_farclip_base 65536</c> and <c>r_farclip_world 2</c>. DP additionally defaults to an infinite-far projection
/// (<c>r_useinfinitefarclip 1</c>); Godot's camera needs a finite plane, and this one is already far beyond any
/// Xonotic map, so nothing a mapper built is ever clipped.
///
/// <para>playtest-bugs #79: the native camera never set <c>Camera3D.Far</c>, so it ran at Godot's default of 4000
/// units — one Quake unit is one Godot unit here, so anything more than 4000 qu away (half of space-elevator or
/// geoplanetary) was cut off and the sky showed through.</para>
/// </summary>
public static class RenderFarClip
{
    /// <summary>DP <c>r_farclip_base</c> default.</summary>
    public const float DefaultBase = 65536f;

    /// <summary>DP <c>r_farclip_world</c> default.</summary>
    public const float DefaultWorld = 2f;

    /// <summary>DP <c>RadiusFromBounds</c> (mathlib.c): the length of the corner made of the larger absolute bound on
    /// each axis — the radius of a sphere about the origin that contains the box.</summary>
    public static float RadiusFromBounds(Vector3 mins, Vector3 maxs)
    {
        var corner = new Vector3(
            System.MathF.Max(System.MathF.Abs(mins.X), System.MathF.Abs(maxs.X)),
            System.MathF.Max(System.MathF.Abs(mins.Y), System.MathF.Abs(maxs.Y)),
            System.MathF.Max(System.MathF.Abs(mins.Z), System.MathF.Abs(maxs.Z)));
        return corner.Length();
    }

    /// <summary>The far clip for a world whose model-0 bounds are <paramref name="worldMins"/>..<paramref name="worldMaxs"/>
    /// (pass <c>null</c> before a map is known: DP then uses the base alone).</summary>
    public static float Compute(float farclipBase, float farclipWorld, Vector3? worldMins, Vector3? worldMaxs)
    {
        float far = farclipBase;
        if (worldMins is { } mn && worldMaxs is { } mx)
            far += RadiusFromBounds(mn, mx) * farclipWorld * 2f;
        // A nonsense cvar (0 or negative) must not collapse the view; DP bounds the near plane under it, the port
        // simply never goes below Godot's own default.
        return float.IsFinite(far) && far > 4000f ? far : 4000f;
    }
}

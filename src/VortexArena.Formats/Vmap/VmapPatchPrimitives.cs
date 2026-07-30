using System.Numerics;

namespace VortexArena.Formats.Vmap;

/// <summary>The patch shapes the Create dialog offers — NetRadiant's own primitive set.</summary>
public enum PatchPrimitive
{
    /// <summary>A flat n x m grid. The starting point for hand-sculpted terrain and curved walls.</summary>
    SimpleMesh,

    /// <summary>A quarter-cylinder: the rounded outside of a corner.</summary>
    Bevel,

    /// <summary>A half-cylinder: the rounded end of a wall or pillar.</summary>
    EndCap,

    /// <summary>A full closed tube.</summary>
    Cylinder,

    /// <summary>A cylinder with more rows, for a tube that bends or is lit along its length.</summary>
    DenseCylinder,

    /// <summary>A tube tapering to a point at the top.</summary>
    Cone,

    /// <summary>A closed ball.</summary>
    Sphere,
}

/// <summary>
/// Builds patch control grids for the editor's Create dialog (design doc §11.9), matching NetRadiant's
/// primitive set and its dimensions.
///
/// <b>These curves are not circles, and that is correct.</b> A Q3 patch is biquadratic, and a quadratic bezier
/// cannot represent a circular arc. Radiant builds its round primitives from a bounding BOX — the arc's end
/// points sit at the box edge midpoints and its middle control point at the box corner — which puts the curve
/// at 1.06r where a true circle would be at r. Every stock Q3 and Xonotic map is built out of exactly that
/// shape, so reproducing it is what makes a patch authored here sit flush against one authored in Radiant.
/// "Fixing" it to a real circle would make our pillars visibly not match the level around them.
///
/// Grids are (2n+1) x (2m+1) with the odd dimension the format requires, and every primitive is built inside a
/// caller-supplied box so the Create dialog can hand it the selection bounds — the way Radiant makes a
/// cylinder out of whatever brush you had selected.
/// </summary>
public static class VmapPatchPrimitives
{
    /// <summary>
    /// World units one texture repeat spans by default. Matches the axial brush projection, so a patch and the
    /// wall it meets come out at the same texel density instead of one looking blurry against the other.
    /// </summary>
    public const float UnitsPerRepeat = 64f;

    /// <summary>Grid dimensions of a primitive, before any subdivision the caller asks for.</summary>
    public static (int Width, int Height) DimensionsOf(PatchPrimitive kind) => kind switch
    {
        PatchPrimitive.Bevel => (3, 3),
        PatchPrimitive.EndCap => (5, 3),
        PatchPrimitive.Cylinder => (9, 3),
        PatchPrimitive.DenseCylinder => (9, 5),
        PatchPrimitive.Cone => (9, 3),
        PatchPrimitive.Sphere => (9, 5),
        _ => (3, 3),
    };

    /// <summary>One-line description for the Create dialog.</summary>
    public static string Describe(PatchPrimitive kind) => kind switch
    {
        PatchPrimitive.SimpleMesh => "A flat grid you sculpt by hand. The starting point for terrain and curved walls.",
        PatchPrimitive.Bevel => "A quarter tube — the rounded outside of a corner. Four of them make a cylinder.",
        PatchPrimitive.EndCap => "A half tube — the rounded end of a wall or pillar.",
        PatchPrimitive.Cylinder => "A closed tube. Built from the box you give it, so it matches the brush it replaces.",
        PatchPrimitive.DenseCylinder => "A cylinder with extra rows, for a tube that has to bend or catch light along its length.",
        PatchPrimitive.Cone => "A tube tapering to a point. Straight-sided, not curved, along its height.",
        PatchPrimitive.Sphere => "A closed ball, poles collapsed to a point at top and bottom.",
        _ => "",
    };

    /// <summary>
    /// Build a primitive inside the box <paramref name="mins"/>..<paramref name="maxs"/>.
    ///
    /// <paramref name="width"/> and <paramref name="height"/> apply to <see cref="PatchPrimitive.SimpleMesh"/>
    /// only; the round primitives have the fixed dimensions their shape needs, and letting a caller ask for a
    /// 7-wide cylinder would produce something that is not a cylinder.
    /// </summary>
    public static VmapPatch Build(
        PatchPrimitive kind, Vector3 mins, Vector3 maxs, string material,
        int width = 3, int height = 3)
    {
        Vector3 lo = Vector3.Min(mins, maxs);
        Vector3 hi = Vector3.Max(mins, maxs);

        VmapPatch patch = kind switch
        {
            PatchPrimitive.SimpleMesh => Grid(lo, hi, OddAtLeast3(width), OddAtLeast3(height)),
            PatchPrimitive.Bevel => Bevel(lo, hi),
            PatchPrimitive.EndCap => EndCap(lo, hi),
            PatchPrimitive.Cylinder => Cylinder(lo, hi, rows: 3),
            PatchPrimitive.DenseCylinder => Cylinder(lo, hi, rows: 5),
            PatchPrimitive.Cone => Cone(lo, hi),
            PatchPrimitive.Sphere => Sphere(lo, hi),
            _ => Grid(lo, hi, 3, 3),
        };

        patch.Material = material ?? string.Empty;
        ApplyDefaultUvs(patch, lo, hi);
        return patch;
    }

    /// <summary>Round up to the next odd number, floored at 3 — the grid sizes the format allows.</summary>
    public static int OddAtLeast3(int n)
    {
        if (n < 3)
            return 3;
        return (n & 1) == 1 ? n : n + 1;
    }

    // =====================================================================================
    //  Shapes
    // =====================================================================================

    /// <summary>A flat grid spanning the box's X/Y extent at its top Z, evenly spaced.</summary>
    private static VmapPatch Grid(Vector3 lo, Vector3 hi, int width, int height)
    {
        var p = new VmapPatch { Width = width, Height = height };
        for (int row = 0; row < height; row++)
        {
            float v = height == 1 ? 0f : row / (float)(height - 1);
            for (int col = 0; col < width; col++)
            {
                float u = width == 1 ? 0f : col / (float)(width - 1);
                p.Controls.Add(new Vector3(
                    Lerp(lo.X, hi.X, u),
                    Lerp(lo.Y, hi.Y, v),
                    hi.Z));
            }
        }
        return p;
    }

    /// <summary>
    /// A quarter tube. Three columns sweeping one corner: edge midpoint, box CORNER (the bezier's middle
    /// control point, which is what makes the arc), edge midpoint.
    /// </summary>
    private static VmapPatch Bevel(Vector3 lo, Vector3 hi)
    {
        var ring = new[]
        {
            new Vector2(hi.X, lo.Y),
            new Vector2(hi.X, hi.Y),   // corner: the arc's control point
            new Vector2(lo.X, hi.Y),
        };
        return Extrude(ring, lo.Z, hi.Z, rows: 3);
    }

    /// <summary>A half tube: five columns sweeping two corners.</summary>
    private static VmapPatch EndCap(Vector3 lo, Vector3 hi)
    {
        float midX = (lo.X + hi.X) * 0.5f;
        var ring = new[]
        {
            new Vector2(hi.X, lo.Y),
            new Vector2(hi.X, hi.Y),   // corner
            new Vector2(midX, hi.Y),
            new Vector2(lo.X, hi.Y),   // corner
            new Vector2(lo.X, lo.Y),
        };
        return Extrude(ring, lo.Z, hi.Z, rows: 3);
    }

    /// <summary>
    /// A closed tube: nine columns around the box, alternating edge midpoint and corner, with the ninth
    /// repeating the first so the surface closes.
    /// </summary>
    private static VmapPatch Cylinder(Vector3 lo, Vector3 hi, int rows)
        => Extrude(BoxRing(lo, hi), lo.Z, hi.Z, rows);

    /// <summary>
    /// A cone: the box ring at the bottom, collapsing to a point at the top.
    ///
    /// The middle row is the straight-line MIDPOINT between base and apex, not a curve — a cone has flat sides,
    /// and a quadratic bezier through evenly spaced control points is exactly a straight line.
    /// </summary>
    private static VmapPatch Cone(Vector3 lo, Vector3 hi)
    {
        Vector2[] ring = BoxRing(lo, hi);
        var apex = new Vector2((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f);

        var p = new VmapPatch { Width = ring.Length, Height = 3 };
        foreach (Vector2 xy in ring)
            p.Controls.Add(new Vector3(xy.X, xy.Y, lo.Z));
        foreach (Vector2 xy in ring)
            p.Controls.Add(new Vector3((xy.X + apex.X) * 0.5f, (xy.Y + apex.Y) * 0.5f, (lo.Z + hi.Z) * 0.5f));
        for (int i = 0; i < ring.Length; i++)
            p.Controls.Add(new Vector3(apex.X, apex.Y, hi.Z));
        return p;
    }

    /// <summary>
    /// A ball: poles collapsed to a point, the equator on the box ring, and the two intermediate rows at FULL
    /// radius but at the pole's height.
    ///
    /// That last part looks wrong and is not: for a quadratic bezier from the pole to the equator, the middle
    /// control point sits where the two tangents meet — directly above the equator point, level with the pole.
    /// Putting it half way up instead gives a lemon rather than a ball.
    /// </summary>
    private static VmapPatch Sphere(Vector3 lo, Vector3 hi)
    {
        Vector2[] ring = BoxRing(lo, hi);
        var centre = new Vector2((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f);
        float midZ = (lo.Z + hi.Z) * 0.5f;

        var p = new VmapPatch { Width = ring.Length, Height = 5 };

        for (int i = 0; i < ring.Length; i++)                       // south pole
            p.Controls.Add(new Vector3(centre.X, centre.Y, lo.Z));
        foreach (Vector2 xy in ring)                                // tangent row, at the pole's height
            p.Controls.Add(new Vector3(xy.X, xy.Y, lo.Z));
        foreach (Vector2 xy in ring)                                // equator
            p.Controls.Add(new Vector3(xy.X, xy.Y, midZ));
        foreach (Vector2 xy in ring)                                // tangent row, at the far pole's height
            p.Controls.Add(new Vector3(xy.X, xy.Y, hi.Z));
        for (int i = 0; i < ring.Length; i++)                       // north pole
            p.Controls.Add(new Vector3(centre.X, centre.Y, hi.Z));

        return p;
    }

    /// <summary>
    /// The nine-point ring around a box: edge midpoint, corner, edge midpoint, … closing back on the first.
    /// The corners are the bezier control points that bend each quarter.
    /// </summary>
    private static Vector2[] BoxRing(Vector3 lo, Vector3 hi)
    {
        float midX = (lo.X + hi.X) * 0.5f;
        float midY = (lo.Y + hi.Y) * 0.5f;
        return new[]
        {
            new Vector2(hi.X, midY),
            new Vector2(hi.X, hi.Y),
            new Vector2(midX, hi.Y),
            new Vector2(lo.X, hi.Y),
            new Vector2(lo.X, midY),
            new Vector2(lo.X, lo.Y),
            new Vector2(midX, lo.Y),
            new Vector2(hi.X, lo.Y),
            new Vector2(hi.X, midY),   // closes the loop
        };
    }

    /// <summary>Sweep a 2D ring vertically into a patch of <paramref name="rows"/> evenly spaced rows.</summary>
    private static VmapPatch Extrude(Vector2[] ring, float bottomZ, float topZ, int rows)
    {
        var p = new VmapPatch { Width = ring.Length, Height = rows };
        for (int row = 0; row < rows; row++)
        {
            float t = rows == 1 ? 0f : row / (float)(rows - 1);
            float z = Lerp(bottomZ, topZ, t);
            foreach (Vector2 xy in ring)
                p.Controls.Add(new Vector3(xy.X, xy.Y, z));
        }
        return p;
    }

    // =====================================================================================
    //  Texturing
    // =====================================================================================

    /// <summary>
    /// Default UVs: u runs around the width, v down the height, scaled so one repeat spans
    /// <see cref="UnitsPerRepeat"/> world units.
    ///
    /// Scaled by SIZE rather than normalized 0..1, because a normalized patch stretches one texture over the
    /// whole thing however big it is — a 1024-unit cylinder would wear a single smeared copy, and the mapper's
    /// first job would be fixing it.
    /// </summary>
    public static void ApplyDefaultUvs(VmapPatch patch, Vector3 lo, Vector3 hi)
    {
        ArgumentNullException.ThrowIfNull(patch);
        patch.ControlUvs.Clear();

        // Approximate the surface's extent in each direction so the repeat count reflects real size.
        Vector3 size = hi - lo;
        float across = MathF.Max(1f, (MathF.Abs(size.X) + MathF.Abs(size.Y)) * 0.5f * MathF.PI);
        float down = MathF.Max(1f, MathF.Abs(size.Z) > 0.01f ? MathF.Abs(size.Z) : MathF.Abs(size.Y));

        float uRepeats = across / UnitsPerRepeat;
        float vRepeats = down / UnitsPerRepeat;

        for (int row = 0; row < patch.Height; row++)
        {
            float v = patch.Height == 1 ? 0f : row / (float)(patch.Height - 1);
            for (int col = 0; col < patch.Width; col++)
            {
                float u = patch.Width == 1 ? 0f : col / (float)(patch.Width - 1);
                patch.ControlUvs.Add(new Vector2(u * uRepeats, v * vRepeats));
            }
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

using Godot;

namespace VortexArena.Game;

/// <summary>
/// The render-boundary coordinate bridge between the simulation (Quake convention, Z-up,
/// <see cref="System.Numerics.Vector3"/>) and Godot (Y-up, <see cref="Godot.Vector3"/>).
///
/// The sim and collision libraries (<c>VortexArena.Engine</c>, <c>VortexArena.Common.Physics</c>) operate
/// entirely in Quake units/axes — see planning/specs/determinism-and-physics.md. Geometry stays in
/// Quake space the whole way through; we only swap axes here, when building/positioning Godot nodes.
///
/// Quake (X forward, Y left, Z up)  ->  Godot (X right, Y up, Z back):
///   <c>godot = (q.X, q.Z, -q.Y)</c>   and the inverse   <c>q = (g.X, -g.Z, g.Y)</c>.
/// The two are exact inverses, so a value round-trips without drift. The map is a PROPER rotation (det +1,
/// not a mirror) and is linear with no translation, so it applies to directions/basis vectors as well as points.
///
/// <para>⚠ ANGLES are a separate trap: Quake pitch is DOWN-positive and the Euler order differs from Godot —
/// NEVER feed Quake angles to a Godot Euler ctor. Build a Godot basis from <c>QMath.AngleVectors</c> + these
/// swaps (columns <c>right, up, -forward</c>), and round-trip directions→angles with <c>FixedVecToAngles</c>,
/// not <c>VecToAngles</c>. Full rules + the warpzone/portal seam: <b>planning/COORDINATE_CONVENTIONS.md</b>.</para>
/// </summary>
public static class Coords
{
    /// <summary>Quake (Z-up, System.Numerics) position/vector -> Godot (Y-up) position/vector.</summary>
    public static Godot.Vector3 ToGodot(System.Numerics.Vector3 q) => new(q.X, q.Z, -q.Y);

    /// <summary>Godot (Y-up) position/vector -> Quake (Z-up, System.Numerics) position/vector.</summary>
    public static System.Numerics.Vector3 ToQuake(Godot.Vector3 g) => new(g.X, -g.Z, g.Y);

    /// <summary>
    /// The Godot basis that orients an entity's MODEL as DarkPlaces does for QC <c>.angles</c> (pitch, yaw, roll):
    /// the columns are the Quake-space images of the model's forward (+X), up (+Z) and right (-Y, which is Godot's
    /// local +Z) from <see cref="VortexArena.Common.Math.EntityRenderAxes"/>, so there is no Godot Euler angle to
    /// get the sign of wrong. <paramref name="model"/> selects DP's alias-model pitch reversal
    /// (<see cref="VortexArena.Common.Math.EntityRenderAxes.ReversesPitch"/>). A pure yaw θ comes out as a
    /// rotation of <b>+θ</b> about Godot +Y (playtest-bugs #76; the old <c>Rotation.Y = -yaw</c> mirrored it).
    /// </summary>
    public static Basis EntityBasis(System.Numerics.Vector3 anglesQuake, string? model)
    {
        VortexArena.Common.Math.EntityRenderAxes.Axes(anglesQuake,
            VortexArena.Common.Math.EntityRenderAxes.ReversesPitch(model),
            out System.Numerics.Vector3 forward, out System.Numerics.Vector3 left, out System.Numerics.Vector3 up);
        return new Basis(ToGodot(forward), ToGodot(up), -ToGodot(left));
    }

    /// <summary>The Godot basis for a pure Quake yaw (degrees): <see cref="EntityBasis"/> with pitch and roll 0.</summary>
    public static Basis YawBasis(float yawDeg) => EntityBasis(new System.Numerics.Vector3(0f, yawDeg, 0f), null);
}

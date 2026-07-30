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
}

using System.Numerics;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// Per-player state for the playermodel lean effect — the port of the never-merged Base branch
/// <c>mirceakitsune/lean_players</c> (2011, recovered from the branch tip <c>c3fe0de24</c>): the player
/// model tips against its acceleration and away from incoming damage. See
/// <see cref="XonoticGodot.Common.Gameplay.PlayerLean"/> for the math and the divergences from the
/// original (the original wrote the lean INTO <c>self.angles</c>; the port keeps it in a separate render-only
/// offset because <c>Player.Angles</c> drives the weapon fire direction here).
/// </summary>
public partial class Entity
{
    /// <summary>
    /// The composed lean angle transform (a makevectors-space Euler triple, degrees; Zero = no lean),
    /// recomputed from scratch every tick by <c>PlayerLean.Step</c> and NETWORKED to clients
    /// (<c>EntityField.Lean</c>). Render-only: the client composes it onto the body basis
    /// (EntityNode) — it is never fed back into aim, physics, or the next tick's lean (the original fed its
    /// output back through <c>self.angles</c>, which is what made dead players spin).
    /// </summary>
    public Vector3 LeanAngles;

    /// <summary>QC <c>.avg_vel</c> (lean_players): the exponentially-averaged velocity the acceleration
    /// lean diffs against. Server-only working state.</summary>
    public Vector3 LeanAvgVel;

    /// <summary>QC <c>.leanangle_damage_loc</c>: hit location relative to the player origin of the most
    /// recent damage (the lever arm the damage lean rotates about). Server-only working state.</summary>
    public Vector3 LeanDmgLoc;

    /// <summary>QC <c>.leanangle_damage_force</c>: the accumulated, decaying damage-lean force vector.
    /// Server-only working state; decays exponentially in <c>PlayerLean.Step</c>.</summary>
    public Vector3 LeanDmgForce;
}

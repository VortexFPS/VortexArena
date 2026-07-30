using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// Player-movement bookkeeping fields, split out of <see cref="Entity"/> so the physics port
/// (<c>XonoticGodot.Common.Physics</c>) can carry the small amount of per-player state the Xonotic
/// movement code keeps between ticks without touching <c>Framework/Entity.cs</c>.
///
/// These mirror the QuakeC fields used by <c>qcsrc/common/physics/player.qc</c> and
/// <c>qcsrc/ecs/systems/physics.qc</c>:
/// <list type="bullet">
///   <item><c>.lastground</c>      -> <see cref="LastGroundTime"/> (landing-friction window).</item>
///   <item><c>.lastflags</c>       -> <see cref="LastFlags"/> (WAS_ONGROUND / WAS_ONSLICK).</item>
///   <item><c>.jumppadcount</c>    -> <see cref="JumpPadCount"/> (reset on jump/land).</item>
///   <item>the <c>FL_ONSLICK</c> bit -> <see cref="OnSlick"/> (Xonotic adds this flag beyond the
///         <see cref="EntFlags"/> set we are allowed to extend; kept as a bool here).</item>
///   <item><c>.wasFlying</c>       -> <see cref="WasFlying"/> (airshot / hitground sound bookkeeping).</item>
/// </list>
///
/// Field names are deliberately distinct from the existing <see cref="Entity"/> members
/// (per the porting constraints).
/// </summary>
public partial class Entity
{
    /// <summary>QC <c>.lastground</c>: simulation time at which the player was last on the ground
    /// (used by the landing-friction window in PlayerJump / the ground branch of PM_Main).</summary>
    public float LastGroundTime = float.NegativeInfinity;

    /// <summary>QC <c>.lastflags</c>: the value of <see cref="Entity.Flags"/> at the end of the previous
    /// tick. Read via WAS_ONGROUND/WAS_ONSLICK to detect the landing transition.</summary>
    public EntFlags LastFlags;

    /// <summary>QC <c>.jumppadcount</c>: number of jumppad bounces in the current air-time; reset to 0
    /// on landing and on jump. Not load-bearing for the core accel math but kept for fidelity.</summary>
    public int JumpPadCount;

    /// <summary>QC <c>.jumppadsused[NUM_JUMPPADSUSED]</c> (jumppads.qh:29, NUM_JUMPPADSUSED=3): the ring of
    /// the last few jumppads that launched this player in the current air-time, used by jumppad_push to
    /// dedupe a re-touch of the same pad before bumping <see cref="JumpPadCount"/> (multi-pad combo credit).
    /// Lazily allocated; never read outside the dedup loop.</summary>
    public Entity?[]? JumpPadsUsed;

    /// <summary>Xonotic's <c>FL_ONSLICK</c> flag (slick/icy surface under the player). The base
    /// <see cref="EntFlags"/> enum does not define it, so the port tracks it here. Set from a
    /// downward trace whose surface carries <c>Q3SURFACEFLAG_SLICK</c> (PM_check_slick).</summary>
    public bool OnSlick;

    /// <summary>QC <c>.wasFlying</c>: set when the player was airborne with clearance below (IsFlying);
    /// consumed by the landing-sound logic. Tracked for parity; not used by the accel math.</summary>
    public bool WasFlying;

    /// <summary>Simulation time of the last footstep sound; used to debounce footstep playback.</summary>
    public float LastFootstepTime = float.NegativeInfinity;

    // --- convenience accessors mirroring the player.qh macros (porter ergonomics) ---

    /// <summary>QC WAS_ONGROUND(s): was the player on the ground at the end of the previous tick.</summary>
    public bool WasOnGround => (LastFlags & EntFlags.OnGround) != 0;

    /// <summary>QC IS_JUMP_HELD(s): the jump button is considered held while FL_JUMPRELEASED is clear.</summary>
    public bool IsJumpHeld => (Flags & EntFlags.JumpReleased) == 0;
}

using System.Numerics;
using VortexArena.Common.Framework;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// What the deterministic tactician (<see cref="BotBrain"/> + <see cref="BotRoles"/>) tells the learned
/// locomotion policy each think. This is the ENTIRE contract between the two halves: a destination and a
/// set of permissions. Nothing in here says *how* to move.
///
/// <para>Keeping it that way is load-bearing. The moment the strategist starts encoding technique into the
/// intent — "jump here", "use the devastator for this gap" — the split rots and the policy stops being
/// swappable, which is the whole point of the seam (risk R-N5 in
/// <c>planning/neural-bots-2026-08-07.md</c>).</para>
/// </summary>
public struct MoveIntent
{
    /// <summary>World position the strategist wants reached (QC the current <c>goalcurrent</c> origin).</summary>
    public Vector3 GoalPos;

    /// <summary>
    /// The goal's own entity when it has one (an item, a flag, a player), else null. The policy never reads
    /// it; the reward and the arrival test do (arriving AT a moving player is not the same test as arriving
    /// at a point).
    /// </summary>
    public Entity? GoalEntity;

    /// <summary>
    /// The next route node beyond <see cref="GoalPos"/>, for look-ahead. Equal to <see cref="GoalPos"/> when
    /// the route runs out. Two nodes of warning is what lets the policy carry speed through a corner instead
    /// of arriving at it flat.
    /// </summary>
    public Vector3 CorridorA;

    /// <summary>The node after <see cref="CorridorA"/>. Same fallback.</summary>
    public Vector3 CorridorB;

    /// <summary>
    /// 0..1. 1 = get there now and accept risk (an enemy is escaping with the flag); 0 = amble. Feeds the
    /// speed-versus-safety trade the policy learns, and scales the time penalty during training.
    /// </summary>
    public float Urgency;

    /// <summary>
    /// False = the deterministic combat logic has claimed the weapon this think and the policy MUST NOT
    /// fire. Enforced as a hard mask on the attack logits before the argmax, not as a reward penalty: a
    /// soft penalty would let a well-trained policy occasionally override combat, and that bug would be
    /// invisible in a live match. Training randomises the flag so both settings are in distribution.
    /// </summary>
    public bool WeaponMovementAllowed;

    /// <summary>True when combat wants the crosshair somewhere specific this think.</summary>
    public bool AimRequired;

    /// <summary>
    /// Where combat wants the crosshair, in world pitch/yaw/roll degrees (Quake convention, pitch
    /// down-positive). Already lead-compensated for projectile travel and already degraded by the bot's
    /// skill error model — the policy sees the angle it is *supposed* to hit, not the perfect one, so aim
    /// skill never has to be learned. See <see cref="BotAim.ComputeDesiredAngles"/>.
    /// </summary>
    public Vector3 RequiredAimAngles;

    /// <summary>
    /// 0..1: how badly combat needs <see cref="RequiredAimAngles"/>. Enters the observation directly and
    /// the reward as <c>-AimWeight * angularError</c>, which is what teaches the policy to find speed from
    /// the wishmove pattern when it cannot get it from turning.
    /// </summary>
    public float AimWeight;

    /// <summary>
    /// The hull the policy is steering, so the navigation-field sampler and the reward's clearance terms use
    /// the same box the physics does. Crouched bots have a different one.
    /// </summary>
    public Vector3 HullMins, HullMaxs;

    /// <summary>An intent with no goal, no aim requirement, and weapons released back to the policy.</summary>
    public static MoveIntent Idle(Vector3 at) => new()
    {
        GoalPos = at,
        CorridorA = at,
        CorridorB = at,
        Urgency = 0f,
        WeaponMovementAllowed = true,
        AimRequired = false,
        RequiredAimAngles = Vector3.Zero,
        AimWeight = 0f,
        HullMins = new Vector3(-16f, -16f, -24f),
        HullMaxs = new Vector3(16f, 16f, 45f),
    };
}

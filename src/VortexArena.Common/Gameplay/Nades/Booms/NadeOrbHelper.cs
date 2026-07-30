// Shared helpers for the orb-based nade booms (heal/ammo/entrap/veil) + the freeze/dark fields.
//
// These mirror the QC team/liveness macros the nade/*.qc touch functions rely on:
//   SAME_TEAM(a,b) = teamplay ? (a.team == b.team) : (a == b)   (common/teams.qh:241)
//   DIFF_TEAM(a,b) = teamplay ? (a.team != b.team) : (a != b)   (common/teams.qh:242)
//   IS_REAL_CLIENT / IS_DEAD / STAT(FROZEN)
//
// The port's Teams.SameTeam is team-equality only (false for two team-0 FFA players even if a==b), which is
// WRONG for the FFA branch of the macro — in FFA every other player is a "foe" and only yourself is a
// "friend". These helpers reproduce the exact macro (keyed on the GameScores.Teamplay global, the port's
// stand-in for the QC `teamplay` global) so heal_foe / entrap-slow / veil-friend behave correctly in both
// FFA and team modes. This file is owned by T11 (the Nades subsystem); it does not touch Teams.cs.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Common.Gameplay.Nades.Booms;

/// <summary>Team/liveness predicates shared by the orb + field nade booms (faithful SAME_TEAM/DIFF_TEAM).</summary>
internal static class NadeOrbHelper
{
    /// <summary>QC <c>SAME_TEAM(a, b)</c>: in teamplay, equal teams; in FFA, the same entity.</summary>
    public static bool SameTeam(Entity? a, Entity? b)
    {
        if (a is null || b is null) return false;
        return GameScores.Teamplay ? a.Team == b.Team : ReferenceEquals(a, b);
    }

    /// <summary>QC <c>DIFF_TEAM(a, b)</c>: in teamplay, different teams; in FFA, a different entity.</summary>
    public static bool DiffTeam(Entity? a, Entity? b)
    {
        if (a is null || b is null) return true;
        return GameScores.Teamplay ? a.Team != b.Team : !ReferenceEquals(a, b);
    }

    /// <summary>
    /// QC <c>SAME_TEAM(toucher, orb)</c> — some nade touch fns check the toucher against the ORB entity
    /// (whose team was stamped to the thrower's team in nades_spawn_orb). In FFA the orb is not the toucher,
    /// so this is only ever true in teamplay (matching the QC macro's FFA reference-equality branch).
    /// </summary>
    public static bool SameTeamOrb(Entity toucher, Entity orb)
        => GameScores.Teamplay ? toucher.Team == orb.Team : ReferenceEquals(toucher, orb);

    /// <summary>QC <c>IS_DEAD(e) || STAT(FROZEN, e)</c> — the common touch-skip guard for the orbs.</summary>
    public static bool IsDeadOrFrozen(Entity e)
        => e.DeadState != DeadFlag.No || e.FrozenStat != 0 || IsStatusFrozen(e);

    /// <summary>QC <c>IS_REAL_CLIENT(e)</c> — approximated by the engine client flag (bots are out of scope headless).</summary>
    public static bool IsRealClient(Entity e) => (e.Flags & EntFlags.Client) != 0;

    private static bool IsStatusFrozen(Entity e)
    {
        var fz = StatusEffectsCatalog.Frozen;
        return fz is not null && StatusEffectsCatalog.Has(e, fz);
    }
}

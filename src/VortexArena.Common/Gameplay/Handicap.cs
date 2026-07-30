// Port of server/handicap.qc — the forced-handicap layer + handicap_level computation.
//
// Handicap makes the game harder for strong players (handicap > 1 => deal less / take more) and easier
// for weak ones (< 1). There are two layers that MULTIPLY into a total: voluntary (the cl_handicap* client
// cvars, read in DamageSystem.VoluntaryHandicapProvider) and forced (set by server mutators — only the
// Dynamic Handicap mutator in stock Base — via Handicap_SetForcedHandicap). This file is the forced layer:
// the per-entity forced give/take fields (Entity.HandicapGive/HandicapTake), the value<=0 guard, the
// per-(re)spawn reset (Handicap_Initialize), and the derived 0..16 handicap_level for the scoreboard icon.

using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Forced-handicap subsystem — faithful port of <c>server/handicap.qc</c>'s forced layer
/// (<c>Handicap_Initialize</c>, <c>Handicap_GetForcedHandicap</c>, <c>Handicap_SetForcedHandicap</c>,
/// <c>Handicap_UpdateHandicapLevel</c>). The voluntary layer + the total (forced × voluntary) read live in
/// <see cref="Damage.DamageSystem"/>; this class owns the forced entity fields + the derived
/// <see cref="Entity.HandicapLevel"/>.
/// </summary>
public static class Handicap
{
    /// <summary>
    /// QC <c>HANDICAP_DISABLED()</c> (server/handicap.qh:57) == <c>IS_GAMETYPE(CTS) || IS_GAMETYPE(RACE)</c>.
    /// The port selects the gametype as a C# object (never stamps a <c>g_cts</c>/<c>g_race</c> cvar), so the
    /// server wires this to <c>GameType is Cts or Race</c> (same provider-seam pattern as the rest of the
    /// handicap path). Null provider = false (the common non-CTS/RACE case, e.g. unit tests / headless).
    /// </summary>
    public static System.Func<bool>? DisabledProvider;

    /// <summary>QC <c>HANDICAP_DISABLED()</c>.</summary>
    public static bool Disabled => DisabledProvider?.Invoke() ?? false;

    /// <summary>QC <c>HANDICAP_MAX_LEVEL_EQUIVALENT</c> (server/handicap.qh:66) = 2.0.</summary>
    public const float MaxLevelEquivalent = 2.0f;

    /// <summary>
    /// QC <c>Handicap_Initialize(player)</c> (server/handicap.qc:16-23), called from
    /// <c>PutClientInServer</c> (server/client.qc:1240). Resets the FORCED handicaps to their no-handicap
    /// default (give = take = 1) on every (re)spawn so a stale forced handicap from a previous match/round
    /// doesn't bleed across, then refreshes the networked handicap level.
    /// </summary>
    public static void Initialize(Entity player)
    {
        // forced handicap defaults
        player.HandicapGive = 1f;
        player.HandicapTake = 1f;
        UpdateHandicapLevel(player);
    }

    /// <summary>
    /// QC <c>Handicap_GetForcedHandicap(player, receiving)</c> (server/handicap.qc:71-79): the forced give/take
    /// field (1 if disabled). The forced fields default to 1, so a value &lt;= 0 (never written in normal play)
    /// is treated as no handicap.
    /// </summary>
    public static float GetForcedHandicap(Entity player, bool receiving)
    {
        if (Disabled) return 1f;
        float v = receiving ? player.HandicapTake : player.HandicapGive;
        return v > 0f ? v : 1f;
    }

    /// <summary>
    /// QC <c>Handicap_SetForcedHandicap(player, value, receiving)</c> (server/handicap.qc:81-94): a no-op when
    /// disabled, hard-faults on a non-positive value (QC <c>error()</c> → <see cref="Log.Fatal"/>), writes the
    /// give/take field, then tails into <see cref="UpdateHandicapLevel"/> so the scoreboard level tracks it.
    /// </summary>
    public static void SetForcedHandicap(Entity player, float value, bool receiving)
    {
        if (Disabled) return;
        if (value <= 0f)
            Log.Fatal("Handicap_SetForcedHandicap: Invalid handicap value.");
        if (receiving) player.HandicapTake = value;
        else player.HandicapGive = value;
        UpdateHandicapLevel(player);
    }

    /// <summary>
    /// QC <c>Handicap_UpdateHandicapLevel(player)</c> (server/handicap.qc:104-115): map the both-ways average
    /// TOTAL handicap (1.0 .. <see cref="MaxLevelEquivalent"/>) to an int level 0..16. Networked to clients
    /// (ent_cs) purely to color the <c>player_handicap</c> scoreboard icon. Disabled ⇒ level 0.
    /// </summary>
    public static void UpdateHandicapLevel(Entity player)
    {
        if (Disabled)
        {
            player.HandicapLevel = 0;
            return;
        }
        // Base uses the full total handicap (forced × voluntary). The voluntary layer is read per-direction by
        // DamageSystem.VoluntaryHandicapProvider; mirror it here so the level reflects a player's cl_handicap*
        // too, not just the forced half. The provider is null-safe (returns 1 headless / in CTS/RACE).
        float total = (GetTotalHandicap(player, true) + GetTotalHandicap(player, false)) / 2f;
        player.HandicapLevel = (int)System.MathF.Floor(MapBoundRanges(total, 1f, MaxLevelEquivalent, 0f, 16f));
    }

    /// <summary>
    /// QC <c>Handicap_GetTotalHandicap(player, receiving)</c> = forced × voluntary. Delegates to
    /// <see cref="Damage.DamageSystem.GetTotalHandicap"/> so there's a single total-handicap implementation;
    /// kept here as the level computation's input.
    /// </summary>
    private static float GetTotalHandicap(Entity player, bool receiving) =>
        Damage.DamageSystem.GetTotalHandicap(player, receiving);

    /// <summary>
    /// QC <c>map_bound_ranges(x, from_min, from_max, to_min, to_max)</c> (lib/math.qh:377): clamp x to
    /// [from_min, from_max] then lerp into [to_min, to_max].
    /// </summary>
    public static float MapBoundRanges(float x, float fromMin, float fromMax, float toMin, float toMax)
    {
        if (x <= fromMin) return toMin;
        if (x >= fromMax) return toMax;
        return toMin + (toMax - toMin) * (x - fromMin) / (fromMax - fromMin);
    }
}

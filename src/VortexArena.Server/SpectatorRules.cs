using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The spectator targeting rules — the C# successor to QuakeC's <c>SpectateSet</c>/<c>SpectateNext</c>/
/// <c>SpectatePrev</c> mutator hooks (the round-mode "spectate-enemies" restriction, e.g. CA
/// <c>g_ca_spectate_enemies</c>). The point is anti-ghosting: an <em>in-game but eliminated</em> player
/// (waiting out the round on a team) must not be able to follow an enemy and relay their position. A pure
/// observer (no team / not in the round) may watch anyone.
///
/// Modes (QC <c>g_*_spectate_enemies</c>): <c>1</c> = anyone; <c>0</c> = teammates only while in-game;
/// <c>-1</c> = an eliminated in-game player may not spectate at all (observing blocked).
/// </summary>
public static class SpectatorRules
{
    /// <summary>QC <c>1</c>: any player may be spectated (no anti-ghost restriction).</summary>
    public const int SpectateAnyone = 1;
    /// <summary>QC <c>0</c>: an in-game eliminated player may spectate only teammates.</summary>
    public const int SpectateTeammatesOnly = 0;
    /// <summary>QC <c>-1</c>: an in-game eliminated player may not spectate at all.</summary>
    public const int SpectateBlocked = -1;

    /// <summary>
    /// The spectate-enemies mode for a gametype (QC <c>g_&lt;gametype&gt;_spectate_enemies</c>). Defaults to
    /// <see cref="SpectateAnyone"/> for gametypes without a configured restriction. CA reads
    /// <c>g_ca_spectate_enemies</c> (gametype default 0 = teammates only).
    /// </summary>
    public static int SpectateEnemiesMode(string? gametypeNetName)
    {
        if (Api.Services is null || string.IsNullOrEmpty(gametypeNetName))
            return SpectateAnyone;
        string cvar = "g_" + gametypeNetName + "_spectate_enemies";
        string s = Api.Cvars.GetString(cvar);
        if (string.IsNullOrEmpty(s))
            return gametypeNetName == "ca" ? SpectateTeammatesOnly : SpectateAnyone; // CA's gametype default
        return (int)Api.Cvars.GetFloat(cvar);
    }

    /// <summary>
    /// QC <c>SpectateSet</c>: may <paramref name="spectator"/> follow <paramref name="target"/>? An in-game
    /// eliminated player in a team game is restricted to teammates unless <paramref name="mode"/> is
    /// <see cref="SpectateAnyone"/> (and is blocked entirely under <see cref="SpectateBlocked"/>). A pure
    /// observer (<paramref name="spectatorInGame"/> false) may watch anyone. Non-team games never restrict.
    /// </summary>
    public static bool CanSpectate(Player spectator, Player target, bool spectatorInGame, int mode, bool teamGame)
    {
        if (ReferenceEquals(spectator, target)) return false; // can't spectate yourself
        if (mode == SpectateAnyone || !teamGame || !spectatorInGame)
            return true;
        if (mode == SpectateBlocked)
            return false; // an eliminated in-game player may not spectate at all
        // mode == teammates-only: block a cross-team target (QC DIFF_TEAM → return true [blocked]).
        return Teams.SameTeam(spectator, target);
    }

    /// <summary>
    /// QC <c>SpectateNext</c>/<c>SpectatePrev</c>: pick the next valid spectatee after <paramref name="current"/>,
    /// cycling through <paramref name="players"/> (skipping anyone <see cref="CanSpectate"/> forbids). Returns the
    /// next allowed player, or null if none qualifies. <paramref name="forward"/> chooses next vs previous.
    /// </summary>
    public static Player? CycleSpectatee(Player spectator, IReadOnlyList<Player> players, Player? current,
        bool spectatorInGame, int mode, bool teamGame, bool forward = true)
    {
        int n = players.Count;
        if (n == 0) return null;

        int start = current is null ? -1 : IndexOf(players, current);
        for (int step = 1; step <= n; step++)
        {
            int idx = start + (forward ? step : -step);
            idx = ((idx % n) + n) % n; // wrap
            Player cand = players[idx];
            if (cand.DeadState != XonoticGodot.Common.Framework.DeadFlag.No) continue; // only living players are spectatable
            if (CanSpectate(spectator, cand, spectatorInGame, mode, teamGame))
                return cand;
        }
        return null;
    }

    private static int IndexOf(IReadOnlyList<Player> players, Player p)
    {
        for (int i = 0; i < players.Count; i++) if (ReferenceEquals(players[i], p)) return i;
        return -1;
    }
}

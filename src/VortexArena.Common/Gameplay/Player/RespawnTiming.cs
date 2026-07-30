using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Port of <c>calculate_player_respawn_time</c> (server/client.qc:1399-1485): compute a dead player's
/// respawn delay, scaling between <c>g_respawn_delay_small</c> and <c>g_respawn_delay_large</c> by the live
/// player count, quantizing to <c>g_respawn_waves</c>, deriving the forced-respawn ceiling
/// (<c>respawn_time_max</c> = <c>g_respawn_delay_max</c>), the announcer countdown start, and the
/// <see cref="RespawnFlag.Force"/> flag from <c>g_forced_respawn</c>. Replaces the old flat 2 s schedule
/// (which was only coincidentally right at stock defaults).
/// </summary>
public static class RespawnTiming
{
    /// <summary>
    /// Fill <paramref name="p"/>'s respawn timers (<see cref="Player.RespawnTime"/>/<see cref="Player.RespawnTimeMax"/>/
    /// <see cref="Player.RespawnCountdown"/>/<see cref="Player.RespawnFlags"/>) for a player who just died.
    /// <paramref name="roster"/> is the connected-player list (for the player-count scaling);
    /// <paramref name="teamplay"/> selects same-team vs all-others counting.
    /// <paramref name="gametype"/> is the active gametype NetName (QC <c>GetGametype()</c>): the respawn delays are
    /// read via <c>GAMETYPE_DEFAULTED_SETTING</c>, so a gametype-specific override (e.g. CTS's <c>g_cts_respawn_delay_*
    /// = -1</c> instant respawn) wins over the generic <c>g_respawn_delay_*</c>.
    /// </summary>
    public static void Calculate(Player p, IReadOnlyList<Player> roster, bool teamplay, string? gametype = null)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC GAMETYPE_DEFAULTED_SETTING (server/client.qh:347): prefer g_<gametype>_respawn_delay_* over the generic
        // g_respawn_delay_* — a <0 override means 0 (instant; e.g. CTS's -1), a 0 override (or g_respawn_delay_forced)
        // falls back to the generic value.
        float sdelayMax    = GametypeDefaulted(gametype, "respawn_delay_max", 5f);
        float sdelaySmall  = GametypeDefaulted(gametype, "respawn_delay_small", 2f);
        float sdelayLarge  = GametypeDefaulted(gametype, "respawn_delay_large", 2f);
        float smallCount   = GametypeDefaulted(gametype, "respawn_delay_small_count", 0f);
        float largeCount   = GametypeDefaulted(gametype, "respawn_delay_large_count", 8f);
        float waves        = GametypeDefaulted(gametype, "respawn_waves", 0f);

        // QC: pcount = 1 (include myself). Then count the OTHER in-game players (same team in teamplay).
        int pcount = 1;
        for (int i = 0; i < roster.Count; i++)
        {
            Player it = roster[i];
            if (ReferenceEquals(it, p) || it.IsObserver || it.FragsStatus == Player.FragsSpectator)
                continue; // QC IS_PLAYER(it) && it != this
            if (!teamplay || it.Team == p.Team)
                pcount++;
        }

        // QC: an unset (<=0) small/large count means "the minimum to have gameplay" (2 in FFA, 1 in teamplay).
        if (smallCount <= 0f) smallCount = teamplay ? 1f : 2f;
        if (largeCount <= 0f) largeCount = teamplay ? 1f : 2f;

        float sdelay;
        if (pcount <= smallCount)
            sdelay = sdelaySmall;
        else if (pcount >= largeCount)
            sdelay = sdelayLarge;
        else // smallCount < pcount < largeCount  (implies largeCount > smallCount)
            sdelay = sdelaySmall + (sdelayLarge - sdelaySmall) * (pcount - smallCount) / (largeCount - smallCount);

        if (waves > 0f)
            p.RespawnTime = MathF.Ceiling((now + sdelay) / waves) * waves;
        else
            p.RespawnTime = now + sdelay;

        p.RespawnTimeMax = sdelay < sdelayMax ? now + sdelayMax : p.RespawnTime;

        // QC: arm the 10-9-8… announcer only for a long-enough wait.
        p.RespawnCountdown = (sdelay + waves >= 5f && p.RespawnTime - now > 1.75f) ? 10 : -1;

        // QC calculate_player_respawn_time (client.qc:1483-1484): forced respawn auto-respawns at
        // respawn_time_max even without a fire press, and is armed ONLY by g_forced_respawn — bots are NOT
        // special-cased. In this port (T39) bots DO drive an input stream: BotBrain.ThinkProduce presses JUMP
        // while DEAD_DEAD (QC bot.qc:147) to advance the same button-gated DEAD_* machine a human uses, so giving
        // a bot RESPAWN_FORCE here would short-circuit DYING→RESPAWNING and skip DEAD_DEAD entirely (the bot
        // would never get to press jump). Match QC: force only on g_forced_respawn.
        // QC client.qc:1483-1484 only OR's in RESPAWN_FORCE here — it never resets respawn_flags (that happens on
        // spawn, SpawnSystem). So flags a gametype set on the death edge (CTS PlayerDies → RESPAWN_FORCE for instant
        // respawn; KickTeamkiller → RESPAWN_SILENT) must be PRESERVED, not clobbered.
        if (Cvar("g_forced_respawn", 0f) != 0f)
            p.RespawnFlags |= RespawnFlag.Force;
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// QC <c>GAMETYPE_DEFAULTED_SETTING</c> (server/client.qh:347): read <c>g_&lt;gametype&gt;_&lt;str&gt;</c>; if it is
    /// &lt;0 use 0, if it is 0 (or <c>g_respawn_delay_forced</c> is set) fall back to <c>max(0, g_&lt;str&gt;)</c>,
    /// otherwise use the override. With no active gametype this is just the generic <c>g_&lt;str&gt;</c>.
    /// </summary>
    private static float GametypeDefaulted(string? gametype, string str, float fallback)
    {
        float generic = MathF.Max(0f, Cvar("g_" + str, fallback));
        if (Api.Services is null || string.IsNullOrEmpty(gametype))
            return generic;

        string key = "g_" + gametype + "_" + str;
        string raw = Api.Cvars.GetString(key);
        if (string.IsNullOrEmpty(raw))
            return generic; // override cvar not registered → generic (matches a 0/unset override)

        float v = Api.Cvars.GetFloat(key);
        if (v < 0f)
            return 0f; // QC: a negative override means 0 (e.g. CTS instant respawn)
        if (v == 0f || Cvar("g_respawn_delay_forced", 0f) != 0f)
            return generic;
        return v;
    }
}

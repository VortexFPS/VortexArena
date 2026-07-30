using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Deathmatch (FFA) gametype — port of <c>CLASS(Deathmatch, Gametype)</c>
/// (common/gametypes/gametype/deathmatch/deathmatch.qh + sv_deathmatch.qc) fused with the DM-relevant
/// slice of the obituary/scoring path (server/damage.qc <c>Obituary</c> / <c>GiveFrags</c>).
///
/// Everyone frags everyone; reaching the frag limit ends the match. On <see cref="OnInit"/> the registry
/// bootstrap constructs this; the host calls <see cref="Activate"/> to subscribe the kill handler and
/// <see cref="Deactivate"/> to unsubscribe.
///
/// Faithfully ported (GiveFrags / Obituary scoring matrix, server/damage.qc):
///  - enemy frag  → attacker +1 (Obituary "MURDER" non-teammate → GiveFrags(attacker, targ, +1));
///  - suicide     → victim   −1 (Obituary "SUICIDE" → GiveFrags(attacker==targ, targ, −1));
///  - world/accident death (no player attacker) → victim −1 (Obituary "ACCIDENT/TRAP" → GiveFrags(targ, targ, −1));
///  - teamkill    → attacker −1 (GiveFrags ... −1) — present for completeness though DM is non-team;
///  - frag-limit end-of-match check (QC GameRules pointlimit / checkrules) and the respawn-time schedule
///    (server/client.qc <c>calculate_respawntime</c>: respawn_time = time + delay).
///
/// Win conditions (server/world.qc <c>WinningCondition_Scores</c>): the frag limit, the lead limit, and
/// <c>leadlimit_and_fraglimit</c> are all checked in <see cref="RecomputeLeader"/>; the DM-only
/// <c>Scores_CountFragsRemaining</c> hook is wired there too, firing the "1/2/3 frags left" announcer
/// (REMAINING_FRAG_{1,2,3}) once as the leader approaches the limit.
///
/// Deferred (NOTE — cross-boundary): score networking/HUD, the suddenDeath-end frags-left=1 forcing
/// (checkrules_suddendeathend is framework-owned), timelimit/overtime, warmup, team scoring, the
/// GiveFragsForKill mutator hook, and the dying-animation interim.
/// </summary>
[GameType]
public sealed class Deathmatch : GameType
{
    // ----- frag-limit cvars + default (gametype default pointlimit=30; m_legacydefaults "30 20 0") -----
    private const string CvarFragLimitDm  = "g_dmlimit";   // DM-specific override
    private const string CvarFragLimit    = "fraglimit";   // generic engine frag limit
    private const float  DefaultFragLimit = 30f;

    // ----- lead-limit cvars + default (engine generic; DM has no gametype-prefixed lead cvar) -----
    private const string CvarLeadLimit    = "leadlimit";              // generic engine lead margin
    private const string CvarLeadAndFrag  = "leadlimit_and_fraglimit"; // both limits required to win
    private const float  DefaultLeadLimit = 0f;                       // 0 = disabled

    // ----- respawn delay cvars + default (xonotic-server.cfg: g_respawn_delay_small/large = 2) -----
    private const string CvarRespawnDelaySmall = "g_respawn_delay_small";
    private const string CvarRespawnDelayLarge = "g_respawn_delay_large";
    private const float  DefaultRespawnDelay   = 2f;

    /// <summary>Cached delegate so <see cref="Deactivate"/> can <see cref="HookChain{T}.Remove"/> the exact instance.</summary>
    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>
    /// Optional sink the gametype notifies when a kill is scored, so the <see cref="MatchController"/> (or a
    /// host) can react (e.g. schedule the victim's respawn) without the gametype owning the player list. Set
    /// by <see cref="MatchController"/> in its <c>Activate</c>.
    /// </summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a player reaches the frag limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The current frag leader (highest <see cref="Player.ScoreFrags"/>), or null before any frag.</summary>
    public Player? Leader { get; private set; }

    public Deathmatch()
    {
        NetName = "dm";
        DisplayName = "Deathmatch";
        TeamGame = false;
    }

    /// <summary>
    /// QC <c>WinningConditionHelper_equality</c> for FFA (server/scores.qc:537): the top two players are tied
    /// on the primary score (SP_SCORE — the fraglimit key). A tie at the time/score limit must enter overtime
    /// rather than declare a draw (server/world.qc), so this drives the overtime cascade for tied timed DM.
    /// </summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster) => FfaTie.TopTwoTied(roster);

    /// <summary>
    /// QC MENUQC <c>METHOD(Deathmatch, describe)</c> (deathmatch.qc:7-16, 3-paragraph PAGE_TEXT): the guide
    /// text displayed under the Deathmatch icon in the gametype-vote picker. Faithful prose port — COLORED_NAME
    /// placeholders for this, ITEM_HealthMega and ITEM_ArmorMega are resolved to their plain English names;
    /// the map-vote panel renders this as a wrapped multi-line block (MapVotePanel.DrawDescription).
    /// </summary>
    public override string? MenuDescription =>
        "Deathmatch is a simple free-for-all fight to the death, where everyone plays against everyone else, " +
        "and the winner is the one with the most points. " +
        "When fragged, you respawn with only the starter weapons, so any you collected will have to be picked up again.\n\n" +
        "Scoring is quite simple, fragging an opponent adds one to your score, and fragging yourself subtracts one.\n\n" +
        "Maps often have powerups and items like Mega Health and Mega Armor, which are usually highly contested " +
        "since they sometimes have the power to make or break a game, so making good use of them is important.";

    /// <summary>QC INIT(Deathmatch) gametype_init: identity is set in the ctor; kept for parity / future cvar seeding.</summary>
    public override void OnInit()
    {
        // QC INIT(Deathmatch): the gametype identity (NetName/DisplayName/TeamGame=false) is set in the ctor
        // and the frag limit is read on demand (FragLimit). The gametype_init flags (USEPOINTS|PREFERRED) and
        // the mapinfo pointlimit are engine-side metadata — there is no per-match objective state to seed here.
    }

    /// <summary>
    /// The frag limit currently in force (g_dmlimit, else fraglimit, else default 30). A value of 0 means
    /// "no limit" (QC pointlimit 0 == unlimited) — distinguished from an unset cvar via the string read, so
    /// an explicit <c>g_dmlimit 0</c> is honored as unlimited rather than falling back to the default.
    /// </summary>
    public float FragLimit
    {
        get
        {
            if (TryCvar(CvarFragLimitDm, out float dm)) return dm;
            if (TryCvar(CvarFragLimit, out float fl)) return fl;
            return DefaultFragLimit;
        }
    }

    /// <summary>
    /// The lead limit currently in force (QC <c>autocvar_leadlimit</c>, server/world.qc): the match ends when the
    /// leader is ahead of the runner-up by at least this margin. 0 (the default) disables it. DM has no
    /// gametype-prefixed lead cvar, so this reads the generic engine <c>leadlimit</c> directly.
    /// </summary>
    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimit, out float ll) && ll >= 0f) return ll;
            return DefaultLeadLimit;
        }
    }

    /// <summary>QC <c>autocvar_leadlimit_and_fraglimit</c>: when set, a finish needs BOTH the frag and lead limits
    /// reached (else either suffices). Only honored when both limits are actually set, matching world.qc:1630.</summary>
    private static bool LeadAndFrag => Api.Services is not null && Api.Cvars.GetFloat(CvarLeadAndFrag) != 0f;

    /// <summary>Subscribe the kill handler to the obituary bus (QC: gametype hooks installed on activation).</summary>
    public void Activate()
    {
        if (_deathHandler is not null)
            return; // idempotent
        MatchEnded = false;
        Leader = null;
        // QC ScoreRules_basics (deathmatch.qh has no extra columns): SP_SCORE is the primary sort/fraglimit key
        // (sprio default SFL_SORT_PRIO_PRIMARY); no per-mode columns. Re-pin it so switching from a mode that set
        // CTF_CAPS/RACE_LAPS/… as primary reverts the scoreboard to sorting by score.
        Scoring.GameScores.ScoreRulesBasics(teams: false);
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score);
        // QC fragsleft_last reset: re-arm the remaining-frags announcer so 1/2/3-frags-left can fire this match.
        Scoring.GameScores.ResetFragsRemaining();
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>Unsubscribe the kill handler (QC: gametype teardown on map end / gametype switch).</summary>
    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>
    /// The obituary/scoring handler — the Godot-free essence of <c>Obituary</c> + <c>GiveFrags</c>
    /// (server/damage.qc). Awards frags per the DM matrix, schedules the victim's respawn, and checks the
    /// frag limit. Returns false (does not consume the event) so other subscribers still run.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        // QC Obituary sanity check: only score player victims.
        if (ev.Victim is not Player victim)
            return false;

        // QC GiveFrags bails when game_stopped; once the match has ended we stop scoring.
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        if (attacker is null || ReferenceEquals(attacker, victim))
        {
            // SUICIDE (targ == attacker) or ACCIDENT/TRAP (no player attacker): victim loses a frag.
            // QC: GiveFrags(attacker, targ, -1) with attacker==targ, and GiveFrags(targ, targ, -1).
            victim.ScoreFrags -= 1;
            victim.GtSuicides += 1; // QC SP_SUICIDES
        }
        else if (SameTeam(attacker, victim))
        {
            // TEAMKILL: attacker loses a frag (QC GiveFrags(attacker, targ, -1)). DM is FFA so this is
            // effectively unreachable (SameTeam is guarded false when TeamGame=false → FFA never teamkills).
            // The escalating g_teamkill_punishing penalty (f -= (tk*(tk-1))*0.5 → -1,-2,-4,-7,…) lives on the
            // shared SCORE-owning path (Scores.Obituary), which is what the team gametypes route through; it is
            // not duplicated into this dead FFA branch.
            attacker.ScoreFrags -= 1;
            attacker.GtKillCount = 0; // a teamkill breaks the attacker's spree
        }
        else
        {
            // ENEMY FRAG: attacker gains a frag (QC GiveFrags(attacker, targ, +1)).
            attacker.ScoreFrags += 1;
            attacker.GtKillCount += 1; // QC .killcount — the attacker's frag spree
        }

        // QC obituary bookkeeping: the victim's DEATHS counter ticks and their frag spree resets.
        victim.GtDeaths += 1;   // QC SP_DEATHS
        victim.GtKillCount = 0; // dying ends a spree

        // Schedule the victim's respawn (QC calculate_respawntime: respawn_time = time + delay).
        ScheduleRespawn(victim);

        // Recompute the leader and check the frag limit (QC checkrules / Scores_CountFragsRemaining).
        UpdateLeaderAndCheckLimit(attacker, victim);

        // Let the host/controller react to the kill (e.g. enqueue the respawn).
        Events?.OnFrag(attacker, victim, ev.DeathType);

        return false; // not "handled" — allow other Death subscribers (stats, mutators) to run
    }

    /// <summary>
    /// QC <c>calculate_respawntime</c> (server/client.qc) reduced to FFA: <c>respawn_time = time + delay</c>.
    /// The full version scales the delay between g_respawn_delay_small/large by player count; here we use the
    /// small delay (both default to 2s in xonotic-server.cfg, so the simplification is exact at defaults).
    /// </summary>
    public void ScheduleRespawn(Player victim)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float delay = RespawnDelay;
        victim.RespawnTime = now + delay;
        // QC also sets deadflag/respawn_flags; the damage system already set DeadState=Dead before this fires.
    }

    /// <summary>
    /// The respawn delay in seconds (g_respawn_delay_small, falling back to large, then 2s). An explicit 0
    /// (instant respawn) is honored — the string read distinguishes it from an unset cvar.
    /// </summary>
    public float RespawnDelay
    {
        get
        {
            if (TryCvar(CvarRespawnDelaySmall, out float small)) return small;
            if (TryCvar(CvarRespawnDelayLarge, out float large)) return large;
            return DefaultRespawnDelay;
        }
    }

    private void UpdateLeaderAndCheckLimit(Player? attacker, Player victim)
    {
        // Track the leader among the two players whose score just changed (cheap, no full scan); the
        // controller can call RecomputeLeader for an authoritative pass if it holds the whole roster.
        Player candidate = attacker ?? victim;
        if (Leader is null || candidate.ScoreFrags > Leader.ScoreFrags)
            Leader = candidate;

        // Cheap incremental fraglimit latch. The lead-limit + leadlimit_and_fraglimit decision needs the true
        // runner-up score (not known from just the two changed players), so it is resolved authoritatively in
        // RecomputeLeader (called every tick on the live path); skip the incremental end when both limits are
        // required, to avoid latching on the frag limit alone before the lead margin is checked.
        float limit = FragLimit;
        bool both = limit > 0f && LeadLimit > 0f && LeadAndFrag;
        if (!both && limit > 0f && Leader is not null && Leader.ScoreFrags >= limit)
            MatchEnded = true;
    }

    /// <summary>
    /// Authoritative leader + frag-limit pass over the full roster (QC checkrules over all clients). The host
    /// may call this each tick; the incremental path in <see cref="OnDeath"/> keeps it correct between calls.
    /// </summary>
    public void RecomputeLeader(IReadOnlyList<Player> players)
    {
        Player? best = null, second = null;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            if (best is null || p.ScoreFrags > best.ScoreFrags)
            {
                second = best;
                best = p;
            }
            else if (second is null || p.ScoreFrags > second.ScoreFrags)
            {
                second = p;
            }
        }
        Leader = best;

        float limit = FragLimit;
        float leadlimit = LeadLimit;

        // QC WinningCondition_Scores remaining-frags announcer (server/world.qc:1590-1622, gated by the DM-only
        // Scores_CountFragsRemaining hook): fire "1/2/3 frags left" once as the leader approaches the limit. Uses
        // the primary score (SP_SCORE) over non-spectators, matching WinningConditionHelper_topscore/_secondscore.
        if (Api.Services is not null)
        {
            int topScore = 0, secondScore = 0;
            bool haveTop = false, haveSecond = false;
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                if (Scoring.GameScores.IsSpectator(p))
                    continue;
                int s = Scoring.GameScores.PrimaryScore(p);
                if (!haveTop || s > topScore)
                {
                    secondScore = topScore; haveSecond = haveTop;
                    topScore = s; haveTop = true;
                }
                else if (!haveSecond || s > secondScore)
                {
                    secondScore = s; haveSecond = true;
                }
            }
            // suddenDeathEnding (checkrules_suddendeathend) is framework-owned and not surfaced here; pass false.
            Scoring.GameScores.CountFragsRemaining(limit, leadlimit, topScore, secondScore, suddenDeathEnding: false);
        }

        // QC limit_reached (server/world.qc:1625-1633): fraglimit OR leadlimit ends the match — unless both are
        // set and leadlimit_and_fraglimit requires BOTH. (topscore-secondscore >= leadlimit.)
        bool fraglimitReached = limit > 0f && best is not null && best.ScoreFrags >= limit;
        bool leadlimitReached = leadlimit > 0f && best is not null && second is not null
            && (best.ScoreFrags - second.ScoreFrags) >= leadlimit;

        bool limitReached;
        if (limit > 0f && leadlimit > 0f && LeadAndFrag)
            limitReached = fraglimitReached && leadlimitReached;
        else
            limitReached = fraglimitReached || leadlimitReached;

        if (limitReached)
            MatchEnded = true;
    }

    /// <summary>
    /// QC SAME_TEAM. In FFA (TeamGame=false) players are never teammates, so two players only "share a team"
    /// when this is a team game AND both have the same nonzero team — this keeps the FFA frag path from ever
    /// being treated as a teamkill.
    /// </summary>
    private bool SameTeam(Player a, Player b)
        => TeamGame && a.Team != 0f && a.Team == b.Team;

    /// <summary>
    /// Read a float cvar IF it is actually set (non-empty string), so an explicit 0 is distinguishable from
    /// "unset". The <see cref="ICvarService"/> returns "" for an unregistered/unset cvar (vs. "0" when set
    /// to zero). Returns false (and <paramref name="value"/> = 0) when unset or no services are wired.
    /// </summary>
    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s))
            return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }
}

/// <summary>
/// Shared equality (tie) reporters for the <see cref="GameType.ReportsTie"/> hook — the Godot-free essence of
/// QC <c>WinningConditionHelper_equality</c> (server/scores.qc:500/537). Two flavors:
///   • <see cref="FfaTie.TopTwoTied"/> — non-teamplay: the top two players' primary scores are equal;
///   • <see cref="TeamTie.TopTwoTied"/> — teamplay: the top two teams' primary scores (by the gametype's
///     ranking slot) are equal.
/// A tie at the time/score limit must enter overtime instead of declaring a draw (server/world.qc
/// <c>GetWinningCode</c> → STARTSUDDENDEATHOVERTIME). Lives in this file (the DM gametype) so the helper is
/// shared across the gametype namespace without a new translation unit; the team modes call it via their own
/// primary-slot getter.
/// </summary>
public static class FfaTie
{
    /// <summary>
    /// QC FFA equality: scan the roster for the top two non-spectator players by primary score (SP_SCORE) and
    /// report whether they are tied. Fewer than two scoring players → not a tie (a sole leader or empty server
    /// is decisive). Mirrors PlayerScore_Compare(winner, second, strict=false) == 0.
    /// </summary>
    public static bool TopTwoTied(IReadOnlyList<Player> roster)
    {
        bool haveTop = false, haveSecond = false;
        int topScore = 0, secondScore = 0;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (Scoring.GameScores.IsSpectator(p))
                continue; // QC nospectators: a spectator never ranks
            int s = Scoring.GameScores.PrimaryScore(p);
            if (!haveTop || s > topScore)
            {
                secondScore = topScore; haveSecond = haveTop;
                topScore = s; haveTop = true;
            }
            else if (!haveSecond || s > secondScore)
            {
                secondScore = s; haveSecond = true;
            }
        }
        return haveTop && haveSecond && topScore == secondScore;
    }
}

/// <summary>
/// Shared team equality (tie) reporter — QC <c>WinningConditionHelper_equality</c> for teamplay
/// (server/scores.qc:500): the top two teams are tied on the gametype's ranking slot. Each team mode passes
/// its leader/runner-up (from <c>GameScores.LeaderTeam()</c>/<c>SecondTeam()</c>) and a getter for the
/// primary score it ranks by (ST_SCORE for TDM/Dom/KH/TeamMayhem/TeamKeepaway, ST_CTF_CAPS for CTF,
/// ST_NEXBALL_GOALS for Nexball).
/// </summary>
public static class TeamTie
{
    /// <summary>True when both teams exist and their ranking-slot scores are equal.</summary>
    public static bool TopTwoTied(int leaderTeam, int secondTeam, System.Func<int, int> scoreOf)
        => leaderTeam != Teams.None && secondTeam != Teams.None
           && scoreOf(leaderTeam) == scoreOf(secondTeam);
}

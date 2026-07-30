using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared team-assignment helper — the Godot-free essence of <c>TeamBalance_JoinBestTeam</c> /
/// <c>TeamBalance_FindBestTeam</c> (server/teamplay.qc). The full QC path weighs skill ratings, bot
/// autobalance, forced teams, and queueing; here we keep the core rule every team gametype relies on:
/// a joining player is placed on the active team with the fewest players (ties → first such team).
/// Shared by the team gametypes (TDM/CTF/CA/FreezeTag/Domination/KeyHunt) in this namespace.
///
/// Deferred (NOTE — cross-boundary): inverse-variance skill weighting (g_balance_teams_skill), bot autobalance,
/// g_forced_team_*, team-change kill + score clear (KillPlayerForTeamChange / PlayerScore_Clear),
/// and the warmup join queue.
/// </summary>
public static class TeamBalance
{
    /// <summary>
    /// Count how many <paramref name="roster"/> players are on <paramref name="team"/>
    /// (excluding <paramref name="ignore"/>, matching QC's "ignore self when joining" argument).
    /// </summary>
    public static int CountTeam(int team, IReadOnlyList<Player> roster, Player? ignore = null)
    {
        int n = 0;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (ReferenceEquals(p, ignore))
                continue;
            if ((int)p.Team == team)
                n++;
        }
        return n;
    }

    /// <summary>
    /// Pick the smallest active team for <paramref name="joiner"/> among the first <paramref name="teamCount"/>
    /// teams, counting <paramref name="roster"/> (ignoring the joiner itself). Writes the result to
    /// <see cref="Entity.Team"/> and returns the chosen team color code.
    /// </summary>
    public static int JoinSmallestTeam(Player joiner, IReadOnlyList<Player> roster, int teamCount)
    {
        int bestTeam = Teams.None, bestCount = int.MaxValue;
        foreach (int team in Teams.Active(teamCount))
        {
            int c = CountTeam(team, roster, joiner);
            if (c < bestCount) { bestCount = c; bestTeam = team; }
        }
        if (bestTeam == Teams.None)
            bestTeam = Teams.Red; // degenerate (teamCount <= 0): default to the first team
        joiner.Team = bestTeam;
        return bestTeam;
    }
}

/// <summary>
/// Team Deathmatch — port of <c>CLASS(TeamDeathmatch, Gametype)</c>
/// (common/gametypes/gametype/tdm/{tdm.qh,sv_tdm.qc}) fused with the team slice of the obituary/scoring
/// path (server/damage.qc <c>Obituary</c>/<c>GiveFrags</c> + GameRules_scoring_add_team).
///
/// Two-or-more teams frag each other; a kill awards the attacker's TEAM a frag, a teamkill/suicide costs
/// one. The match ends when a team reaches the point limit (QC GameRules_limit_score → fraglimit) or the
/// lead limit (leadlimit). Players are balanced to the smallest team on join (server/teamplay.qc
/// TeamBalance_JoinBestTeam → TeamBalance_FindBestTeam: smallest <c>m_num_players</c>).
///
/// QC defaults (gametype_init): "timelimit=15 pointlimit=50 teams=2 leadlimit=0".
///
/// Faithfully ported (Godot-free essence):
///  - team assignment to the smallest active team on join (<see cref="AssignTeam"/>);
///  - per-team frag scoring on the obituary bus (enemy kill → attacker team +1; teamkill/suicide/world → −1),
///    mirroring GiveFrags routed through GameRules_scoring_add_team;
///  - point-limit + lead-limit end-of-match check (GameRules_limit_score/lead).
///
/// Deferred (NOTE — cross-boundary): skill-weighted balancing, bot autobalance, team-spawn selection
/// (GameRules_spawning_teams / info_player_team*), score networking/HUD, leadlimit announcer,
/// tdm_team map entities (custom team colors), and warmup.
/// </summary>
[GameType]
public sealed class Tdm : GameType
{
    // ----- limit cvars + defaults (gametype_init pointlimit=50, leadlimit=0; tdm.qh legacydefaults "50 20 2 0") -----
    private const string CvarPointLimitTdm = "g_tdm_point_limit";       // TDM-specific override
    private const string CvarPointLimit    = "fraglimit";              // GameRules_limit_score writes this
    private const string CvarLeadLimitTdm  = "g_tdm_point_leadlimit";
    private const string CvarLeadLimit     = "leadlimit";
    private const string CvarLeadAndFrag   = "leadlimit_and_fraglimit"; // both limits required to win (world.qc:1630)
    private const float  DefaultPointLimit = 50f;
    private const float  DefaultLeadLimit  = 0f;

    // ----- timelimit (gametype_init "timelimit=15"; generic Cvars.cs default is 20) -----
    private const string CvarTimeLimit          = "timelimit";
    private const float  DefaultTimeLimitMinutes = 15f;

    // ----- team count cvars (sv_tdm.qc: g_tdm_teams_override >= 2 ? override : g_tdm_teams), clamped 2..4 -----
    private const string CvarTeamsOverride = "g_tdm_teams_override";
    private const string CvarTeams         = "g_tdm_teams";
    private const int    DefaultTeams      = 2;

    private HookHandler<DeathEvent>? _deathHandler;

    // Per-team running frag totals (QC ST_SCORE, team slot 0) now live in the unified GameScores two-slot team
    // store — the source of truth (common/scores.qh). TDM's team primary IS ST_SCORE (no slot-1 field), so the
    // flag-aware GameScores.LeaderTeam() ranks TDM teams correctly. Read/written via GetTeamScore / AddTeamScore.

    /// <summary>QC checkrules end-of-match latch: true once a team reaches the point or lead limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The leading team's color code (the GameScores flag-aware ST_SCORE leader), or <see cref="Teams.None"/>.</summary>
    public int LeaderTeam { get; private set; }

    public Tdm()
    {
        NetName = "tdm";
        DisplayName = "Team Deathmatch";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC INIT(TeamDeathmatch): the gametype-relevant flags (TeamGame) are set in the ctor; team-count is
        // read on demand from g_tdm_teams (TeamCount). GameRules_teams(true) / GameRules_spawning_teams and
        // the tdm_team map-entity team colors are engine-side registration the host performs from TeamGame +
        // TeamCount.
        //
        // QC sv_tdm.qc tdm_Initialize seeds the engine limit cvars from the gametype defaults:
        //   GameRules_limit_score(g_tdm_point_limit)  -> cvar_set("fraglimit", limit) UNLESS limit < 0
        //   GameRules_limit_lead (g_tdm_point_leadlimit) -> cvar_set("leadlimit", limit) UNLESS limit < 0
        // (sv_rules.qc: `if (limit < 0) return;` — a negative shipped cvar means "keep the mapinfo/gametype
        // default limit", which for TDM is gametype_init "pointlimit=50 leadlimit=0 timelimit=15"). We mirror
        // that seeding here so a stock TDM ends on the 50-frag mapinfo limit (not only on the timelimit) and on
        // TDM's 15-min timelimit rather than the generic Cvars.cs default of 20. The fraglimit/leadlimit cvars
        // are then the single source the per-frame win check reads through (PointLimit / LeadLimit below).
        SeedLimit(CvarPointLimitTdm, CvarPointLimit, DefaultPointLimit);
        SeedLimit(CvarLeadLimitTdm, CvarLeadLimit, DefaultLeadLimit);
        SeedTimeLimit(DefaultTimeLimitMinutes);
    }

    /// <summary>
    /// QC <c>GameRules_limit_score</c> / <c>GameRules_limit_lead</c> (sv_rules.qc): write the gametype's limit
    /// into the engine cvar (<paramref name="engineCvar"/>) from the TDM-specific cvar
    /// (<paramref name="tdmCvar"/>) — but treat a NEGATIVE shipped value as "use the mapinfo/gametype default"
    /// (<paramref name="mapinfoDefault"/>) by seeding that default instead of the literal −1. A non-negative
    /// TDM cvar (incl. an explicit 0 == unlimited) is written through verbatim.
    /// </summary>
    private static void SeedLimit(string tdmCvar, string engineCvar, float mapinfoDefault)
    {
        if (Api.Services is null)
            return;
        float limit = TryCvar(tdmCvar, out float v) ? v : -1f; // unset TDM cvar behaves like −1 (use mapinfo)
        // QC GameRules_limit_*: skip the cvar_set when limit < 0 (keep the mapinfo/gametype default) — we make
        // the mapinfo default explicit so the port (which has no mapinfo limit row) still ends correctly.
        Api.Cvars.Set(engineCvar, (limit < 0f ? mapinfoDefault : limit).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Apply TDM's gametype-default timelimit (gametype_init "timelimit=15") if the host has not overridden it.
    /// QC applies the default-limit string at gametype registration; the port's generic <c>timelimit</c> default
    /// is 20, so without this a stock TDM would run 20 minutes instead of 15. We only seed when the live value
    /// still equals the generic default (so an explicit host/server.cfg timelimit wins, matching QC where an
    /// admin-set cvar overrides the gametype default).
    /// </summary>
    private static void SeedTimeLimit(float minutes)
    {
        if (Api.Services is null)
            return;
        const float genericDefault = 20f; // Cvars.cs generic timelimit default
        // Only override the GENERIC default (20). An explicit host value — including 0 ("no time limit") — is a
        // deliberate choice that wins, matching QC where an admin-set cvar overrides the gametype default.
        if (Api.Cvars.GetFloat(CvarTimeLimit) == genericDefault)
            Api.Cvars.Set(CvarTimeLimit, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// QC sv_tdm.qc: <c>GameRules_spawning_teams(autocvar_g_tdm_team_spawns)</c> — TDM gates team spawns on
    /// g_tdm_team_spawns (stock default 0, so TDM does NOT use team spawnpoints by default; players spawn from
    /// any spot like FFA even though kills are scored per-team).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_tdm_team_spawns", out float v) ? v != 0f : false;

    /// <summary>Number of teams in play (g_tdm_teams_override if &gt;= 2, else g_tdm_teams), clamped to 2..4.</summary>
    public int TeamCount
    {
        get
        {
            int n = DefaultTeams;
            if (TryCvar(CvarTeamsOverride, out float ov) && ov >= 2f) n = (int)ov;
            else if (TryCvar(CvarTeams, out float t)) n = (int)t;
            return System.Math.Clamp(n, 2, 4);
        }
    }

    /// <summary>The point limit in force (g_tdm_point_limit, else fraglimit, else 50). 0 == unlimited.
    /// A NEGATIVE value (the shipped g_tdm_point_limit −1 sentinel) resolves to the mapinfo/gametype default
    /// (50) per QC GameRules_limit_score, so the frag limit actually fires for a stock TDM. OnInit already
    /// seeds fraglimit=50 at boot; this read mirrors the −1 resolution for any runtime re-read.</summary>
    public float PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimitTdm, out float pl)) return pl < 0f ? DefaultPointLimit : pl;
            if (TryCvar(CvarPointLimit, out float fl)) return fl < 0f ? DefaultPointLimit : fl;
            return DefaultPointLimit;
        }
    }

    /// <summary>The lead limit in force (g_tdm_point_leadlimit, else leadlimit, else 0). 0 == no lead limit.
    /// A NEGATIVE value (the shipped g_tdm_point_leadlimit −1 sentinel) resolves to the mapinfo/gametype
    /// default (0 == off) per QC GameRules_limit_lead, rather than being read literally.</summary>
    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimitTdm, out float ll)) return ll < 0f ? DefaultLeadLimit : ll;
            if (TryCvar(CvarLeadLimit, out float l)) return l < 0f ? DefaultLeadLimit : l;
            return DefaultLeadLimit;
        }
    }

    /// <summary>QC <c>autocvar_leadlimit_and_fraglimit</c> (gametypes-server.cfg default 0): when set, a finish
    /// needs BOTH the frag and lead limits reached (else either suffices). Only honored when both limits are
    /// actually set, matching world.qc:1630.</summary>
    private static bool LeadAndFrag => Api.Services is not null && Api.Cvars.GetFloat(CvarLeadAndFrag) != 0f;

    public void Activate()
    {
        if (_deathHandler is not null)
            return; // idempotent
        MatchEnded = false;
        LeaderTeam = Teams.None;
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero the team slots before declaring
        // QC ScoreRules_basics (TDM declares no extra columns): SP_SCORE is the player primary, the team ST_SCORE
        // the team primary (sprio/stprio = SFL_SORT_PRIO_PRIMARY). Teamkills shown for a team game. Re-pin the key.
        Scoring.GameScores.ScoreRulesBasics(teams: true);
        Scoring.GameScores.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.SortPrioPrimary); // ST_SCORE is team primary
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score);
        // QC fragsleft_last reset: re-arm the remaining-frags team announcer so "1/2/3 frags left"
        // (ANNCE_REMAINING_FRAG_{1,2,3}) can fire again this match (TDM's Scores_CountFragsRemaining hook
        // returns true, enabling the cue).
        Scoring.GameScores.ResetFragsRemaining();
        SeedTeamScores();
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>Ensure every active team has a zeroed ST_SCORE slot (so the leader scan is stable from frame 0).</summary>
    private void SeedTeamScores() => Scoring.GameScores.SeedTeams(TeamCount);

    /// <summary>
    /// Assign <paramref name="joiner"/> to the smallest active team (QC TeamBalance_JoinBestTeam →
    /// TeamBalance_FindBestTeam: pick the team with the fewest <c>m_num_players</c>, ties broken by the
    /// first such team). <paramref name="roster"/> is the current set of teamed players to count against.
    /// Returns the chosen team color code, which is also written to <see cref="Entity.Team"/>.
    /// </summary>
    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        // QC GiveFrags routes every frag through GameRules_scoring_add_team -> PlayerTeamScore_Add, which writes
        // BOTH the team's ST_SCORE (AddTeamScore below) AND the attacker's individual SP_SCORE (Player.ScoreFrags).
        // The team total is read-through to GameScores via TeamScoreSource; the per-player SP_SCORE is written HERE
        // (Scores.Obituary runs ownsScore:false for TDM and skips SP_SCORE, only filling the aux KILLS/DEATHS/...
        // columns + its own KillStreak, so without this credit every TDM player's scoreboard Score reads 0).
        if (attacker is null || ReferenceEquals(attacker, victim))
        {
            // SUICIDE / world death: the victim's team loses a frag (QC GiveFrags(targ, targ, -1) team-routed),
            // and the victim's individual SP_SCORE drops by 1 (PlayerTeamScore_Add player side).
            // QC server/damage.qc:304 special-cases DEATH_AUTOTEAMCHANGE (an auto-balance team move): the suicide
            // frag is NOT negated, since the player didn't choose to switch. A manual DEATH_TEAMCHANGE still does.
            if (!DeathTypes.IsAutoTeamChange(ev.DeathType))
            {
                AddTeamScore(victim.Team, -1);
                victim.ScoreFrags -= 1;
            }
        }
        else if (Teams.SameTeam(attacker, victim))
        {
            // TEAMKILL: attacker's team loses a frag (QC GiveFrags(attacker, targ, -1)), and the attacker's own
            // SP_SCORE drops by 1; a teamkill also breaks the attacker's frag spree (.killcount).
            AddTeamScore(attacker.Team, -1);
            attacker.ScoreFrags -= 1;
            attacker.GtKillCount = 0;
        }
        else
        {
            // ENEMY FRAG: attacker's team gains a frag (QC GiveFrags(attacker, targ, +1) → scoring_add_team),
            // the attacker's own SP_SCORE gains 1, and their .killcount spree advances (read by NadeBonus).
            AddTeamScore(attacker.Team, +1);
            attacker.ScoreFrags += 1;
            attacker.GtKillCount += 1;
        }

        // QC obituary: dying ends the victim's frag spree (.killcount = 0). (Their SP_DEATHS/aux columns are
        // owned by Scores.Obituary's read-through pass, so we do not double-count them here.)
        victim.GtKillCount = 0;

        // QC calculate_respawntime: arm the victim's respawn (the controller drives the actual respawn).
        ScheduleRespawn(victim);

        UpdateLeaderAndCheckLimit();
        return false; // allow other Death subscribers (stats, mutators) to run
    }

    /// <summary>QC <c>GameRules_scoring_add_team(player, SCORE, delta)</c>'s team side: add to a team's ST_SCORE
    /// total (GameScores team slot 0 — the team primary). No-op for the neutral team.</summary>
    public void AddTeamScore(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None)
            return;
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotScore, delta);
    }

    /// <summary>Read a team's running frag total (0 if the team has never scored).</summary>
    public int GetTeamScore(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotScore);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on team points (ST_SCORE),
    /// so a tied timed TDM enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(Scoring.GameScores.LeaderTeam(), Scoring.GameScores.SecondTeam(), GetTeamScore);

    /// <summary>QC calculate_respawntime reduced: respawn_time = time + small delay (g_respawn_delay_small, 2s).</summary>
    public void ScheduleRespawn(Player victim)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (TryCvar("g_respawn_delay_small", out float d) || TryCvar("g_respawn_delay_large", out d))
            victim.RespawnTime = now + d;
        else
            victim.RespawnTime = now + 2f;
    }

    /// <summary>
    /// Recompute the leading team and apply the win conditions (QC checkrules): a team reaching the point
    /// limit, OR leading the runner-up by at least the lead limit, ends the match.
    /// </summary>
    public void UpdateLeaderAndCheckLimit()
    {
        // QC: TDM teams rank by the team primary slot ST_SCORE. LeaderTeam / SecondTeam read the flag-aware
        // two-slot compare from GameScores (the source of truth) so the standings stay consistent.
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;

        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamScore(bestTeam);
        float pointLimit = PointLimit;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        int secondScore = secondTeam != Teams.None ? GetTeamScore(secondTeam) : 0;

        // QC limit_reached (server/world.qc:1625-1633): the team frag limit OR the team lead limit ends the match —
        // unless BOTH limits are set and leadlimit_and_fraglimit requires both. (top - second >= leadlimit.)
        bool fraglimitReached = pointLimit > 0f && bestScore >= pointLimit;
        bool leadlimitReached = leadLimit > 0f && secondTeam != Teams.None
            && (bestScore - secondScore) >= leadLimit;

        bool limitReached;
        if (pointLimit > 0f && leadLimit > 0f && LeadAndFrag)
            limitReached = fraglimitReached && leadlimitReached;
        else
            limitReached = fraglimitReached || leadlimitReached;

        if (limitReached)
            MatchEnded = true;

        // QC WinningCondition_Scores remaining-frags announcer, gated by the TDM Scores_CountFragsRemaining hook
        // (sv_tdm.qc returns true): announce "N frags left" once as the leading TEAM approaches the point limit.
        // For team modes the top/second "scores" are the team ST_SCORE totals (WinningConditionHelper ranks teams
        // by their primary slot); suddenDeathEnding is framework-owned and surfaced elsewhere, so pass false.
        Scoring.GameScores.CountFragsRemaining(pointLimit, leadLimit, bestScore, secondScore, suddenDeathEnding: false);
    }

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

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Domination — port of <c>CLASS(Domination, Gametype)</c>
/// (common/gametypes/gametype/domination/{domination.qh,sv_domination.qc}). Teams fight over fixed
/// control points; a point captured by a team periodically ticks points to that team's score (and to the
/// capturing player). First team to the point limit (QC g_domination_point_limit, default 200) wins.
///
/// QC defaults (gametype_init): "timelimit=20 pointlimit=200 teams=2 leadlimit=0" (legacydefaults "200 20 0").
///
/// Faithfully ported (Godot-free essence):
///  - smallest-team assignment on join (<see cref="TeamBalance"/>);
///  - control points with an owning team (<see cref="ControlPoint"/>), each backed by a world
///    <see cref="Entity"/> spawned at the map's dom_controlpoint/team_dom_point spawns
///    (<see cref="SpawnControlPoint"/>, QC dom_controlpoint_setup), captured by a player touching them
///    (<see cref="CapturePoint"/>, QC dompointtouch → dompoint_captured → set goalentity.team) with the
///    capturer's DOM_TAKES credit;
///  - the periodic point tick (<see cref="Tick"/> + per-point <see cref="PointThink"/>, QC dompointthink →
///    TeamScore_AddToTeam ST_SCORE +amt every g_domination_point_rate seconds) crediting the owning team and
///    the capturing player, honoring each point's per-point amt/rate (point frags/wait from the entity);
///  - point-limit win condition (GameRules_limit_score → fraglimit, read here as g_domination_point_limit);
///  - the ROUND-BASED variant (g_domination_roundbased, QC dom_DelayedInit → round_handler_Spawn): a team
///    wins a round by owning ALL control points (<see cref="CheckRoundWinner"/> /
///    <see cref="CountAndFindRoundWinner"/>, QC Domination_CheckWinner + Team_GetWinnerTeam_WithOwnedItems),
///    banked as ST_DOM_CAPS — no per-tick scoring in this mode (PointThink early-returns when RoundBased).
///
/// Deferred (NOTE — cross-boundary): point models/animation/waypoint sprites (CSQC), the neutral capture-interim
/// state, and the pps HUD state — client/networking concerns.
/// </summary>
[GameType]
public sealed class Domination : GameType
{
    // ----- point-limit cvars + default (g_domination_point_limit → fraglimit; legacydefaults 200) -----
    private const string CvarPointLimitDom = "g_domination_point_limit";
    private const string CvarPointLimit    = "fraglimit";
    private const string CvarLeadLimitDom  = "g_domination_point_leadlimit"; // QC GameRules_limit_lead source
    private const string CvarLeadLimit     = "leadlimit";
    private const float  DefaultPointLimit = 200f;

    // ----- round-based match cap limit (g_domination_roundbased_point_limit; QC sv_domination.qh REGISTER_MUTATOR:
    //   point_limit = (roundbased && roundbased_point_limit) ? roundbased_point_limit : g_domination_point_limit) -----
    private const string CvarRoundbasedPointLimit = "g_domination_roundbased_point_limit";
    private const float  DefaultRoundbasedPointLimit = 5f; // QC autocvar default (gametypes-server.cfg)

    // ----- tick cvars + defaults (g_domination_point_amt / g_domination_point_rate) -----
    private const string CvarPointAmt  = "g_domination_point_amt";
    private const string CvarPointRate = "g_domination_point_rate";
    private const float  DefaultPointAmt  = 1f;   // QC: falls back to per-point .frags; default per-tick 1
    private const float  DefaultPointRate = 2f;   // QC: falls back to per-point .wait; default ~2s

    // ----- team count cvars (g_domination_teams_override >= 2 ? override : g_domination_default_teams) -----
    private const string CvarTeamsOverride = "g_domination_teams_override";
    private const string CvarTeams         = "g_domination_default_teams";
    private const int    DefaultTeams      = 2;

    // Per-team running point totals (QC ST_SCORE slot 0 + ST_DOM_TICKS slot 1) now live in the unified GameScores
    // two-slot team store — the source of truth (common/scores.qh). Dom adds each tick to BOTH slots (QC
    // dompoint_captured); the team primary is slot 0 (SCORE) normally, slot 1 (TICKS) when disable_frags.

    /// <summary>The control points on the map (QC g_dompoints).</summary>
    public readonly List<ControlPoint> Points = new();

    public bool MatchEnded { get; private set; }
    public int LeaderTeam { get; private set; }

    /// <summary>
    /// QC round_handler_GetEndTime for the round-based variant: a source for the current round's end time (the
    /// host wires this to the world round handler's RoundEndTime; default 0 = no per-round time limit). When the
    /// returned end time has elapsed the round ends with no winner (<see cref="CheckRoundWinner"/>). Kept as a
    /// source (rather than owning a round handler) so the round flow is driven by the host's single round handler
    /// — see GameWorld.ActivateGameType wiring <c>EnableRounds(dom...)</c> — and stays headlessly testable.
    /// </summary>
    public Func<float>? RoundEndTimeSource { get; set; }

    /// <summary>The team that won the most recent round (round-based variant), or 0. Reset each round start.</summary>
    public int LastRoundWinner { get; private set; }

    /// <summary>
    /// QC round_handler_IsRoundStarted for the round-based variant: a source telling whether the current round
    /// has actually started (players unblocked). The host wires this to the live round handler's IsRoundStarted
    /// (see GameWorld.ActivateGameType). When null (headless / tick variant) it is treated as "started" so
    /// captures aren't blocked — the round-not-started capture gate (QC dompointtouch) only applies in the
    /// round-based variant where the host has supplied this source.
    /// </summary>
    public Func<bool>? RoundStartedSource { get; set; }

    /// <summary>
    /// QC <c>dom_EventLog</c> (sv_domination.qc:24-28) — the server gamelog echo seam. When wired (the host
    /// calls <c>GameWorld.ActivateGameType</c>), <see cref="CapturePoint"/> emits
    /// <c>:dom:taken:&lt;team_before&gt;:&lt;playerid&gt;</c> to the server event log (gated by
    /// <c>sv_eventlog</c> at the host call site). When null (headless / tests) the echo is a no-op.
    /// QC: <c>if(autocvar_sv_eventlog) GameLogEcho(strcat(":dom:", mode, ":", ftos(team_before), …))</c>.
    /// </summary>
    public Action<string>? EventLogEcho { get; set; }

    public Domination()
    {
        NetName = "dom";
        DisplayName = "Domination";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC dom_DelayedInit / dom_controlpoint_setup: the control points are spawned from the map's
        // dom_controlpoint entities (see SpawnControlPoint). GameRules_teams(true), the dom_team palette
        // entities, and the round-based variant are the engine's / a later phase's job; OnInit clears state.
        Points.Clear();
    }

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

    /// <summary>
    /// Point/cap limit in force. QC sv_domination.qh REGISTER_MUTATOR(dom) ONADD:
    /// <c>point_limit = autocvar_g_domination_point_limit; if (roundbased &amp;&amp; roundbased_point_limit) point_limit
    /// = roundbased_point_limit; GameRules_limit_score(point_limit);</c> — so in the round-based variant the team
    /// limit is the cumulative CAPS limit (g_domination_roundbased_point_limit, default 5), NOT the per-tick
    /// score limit. Non-roundbased reads g_domination_point_limit (else fraglimit, else 200). 0 == unlimited.
    /// </summary>
    public float PointLimit
    {
        get
        {
            if (RoundBased)
            {
                // QC: only overrides when roundbased_point_limit is non-zero (else falls through to point_limit).
                if (TryCvar(CvarRoundbasedPointLimit, out float rpl) && rpl != 0f) return rpl;
                if (TryCvar(CvarPointLimitDom, out float rplBase)) return rplBase;
                return DefaultRoundbasedPointLimit;
            }
            if (TryCvar(CvarPointLimitDom, out float pl)) return pl;
            if (TryCvar(CvarPointLimit, out float fl)) return fl;
            return DefaultPointLimit;
        }
    }

    /// <summary>QC GameRules_limit_lead(autocvar_g_domination_point_leadlimit): the dom-specific lead limit
    /// (falls back to the generic leadlimit cvar, then 0 = no lead limit).</summary>
    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimitDom, out float dl)) return dl;
            return TryCvar(CvarLeadLimit, out float l) ? l : 0f;
        }
    }

    /// <summary>Points granted to the owning team per tick (g_domination_point_amt, else 1).</summary>
    public float PointAmount => TryCvar(CvarPointAmt, out float v) ? v : DefaultPointAmt;

    /// <summary>Seconds between point ticks for an owned control point (g_domination_point_rate, else 2).</summary>
    public float PointRate => TryCvar(CvarPointRate, out float v) ? v : DefaultPointRate;

    /// <summary>QC autocvar_g_domination_disable_frags: when set, kills give no individual frags at all.</summary>
    public bool DisableFrags => Cvar("g_domination_disable_frags", 0f) != 0f;

    /// <summary>QC autocvar_g_domination_roundbased (domination_roundbased): the round-based Domination variant —
    /// a team wins a round by capturing all points, scored as CAPS (no per-tick TICKS column). The tick-based
    /// (non-roundbased) variant is the port's active path; this selects the matching ScoreRules schema (D4).</summary>
    public bool RoundBased => Cvar("g_domination_roundbased", 0f) != 0f;

    public void Activate()
    {
        MatchEnded = false;
        LeaderTeam = Teams.None;
        LastRoundWinner = Teams.None;
        Scoring.GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring
        DeclareScoreRules();
        Scoring.GameScores.SeedTeams(TeamCount); // zero both team slots for the active teams (stable leader scan)
        // Domination scores via control-point ticks, not kills (frags are cosmetic, and g_domination_disable_frags
        // can suppress them entirely — see DisableFrags). No Combat.Death subscription is needed.
        //
        // QC dom_DelayedInit (sv_domination.qc:632): the ROUND-BASED variant spawns a round handler over
        // Domination_CheckPlayers / Domination_CheckWinner / Domination_RoundStart. The host drives that round
        // handler (GameWorld.ActivateGameType calls EnableRounds(CanRoundStart, CheckRoundWinner, RoundStart)
        // when RoundBased) and wires RoundEndTimeSource; a team wins a round by owning ALL control points
        // (banked as ST_DOM_CAPS). The tick variant has no round handler.
    }

    /// <summary>
    /// QC Domination_CheckPlayers (sv_domination.qc:362): the round-based gate to start a round. QC returns true
    /// unconditionally (the engine's player-presence check is upstream); mirrored here.
    /// </summary>
    public bool CanRoundStart() => true;

    /// <summary>QC Domination_RoundStart (sv_domination.qc:367): unblock players at round start; clear the
    /// per-round winner latch so the next <see cref="CheckRoundWinner"/> can credit again.</summary>
    public void RoundStart() => LastRoundWinner = Teams.None;

    /// <summary>
    /// QC Domination_count_controlpoints (sv_domination.qc:311) + Team_GetWinnerTeam_WithOwnedItems: count how
    /// many points each team owns; a team owning ALL of them is the round winner. Returns the winner team
    /// (&gt;0), -1 for a tie/over-time-out, or 0 while the round is still contested. (Neutral points mean no
    /// team owns all, so the round continues.)
    /// </summary>
    public int CountAndFindRoundWinner()
    {
        if (Points.Count == 0)
            return 0;
        int total = Points.Count;
        var owned = new Dictionary<int, int>();
        foreach (ControlPoint cp in Points)
            if (cp.OwnerTeam != Teams.None)
                owned[cp.OwnerTeam] = (owned.TryGetValue(cp.OwnerTeam, out int c) ? c : 0) + 1;

        foreach (var kv in owned)
            if (kv.Value >= total)
                return kv.Key; // this team owns every point → it wins the round
        return 0; // still contested
    }

    /// <summary>
    /// QC Domination_CheckWinner (sv_domination.qc:332): the round handler's canRoundEnd. The round ends when a
    /// team owns every control point (bank ST_DOM_CAPS +1 for it) OR the round time limit elapses (no winner).
    /// Returns true when the round is decided this frame; idempotent within a round via <see cref="LastRoundWinner"/>.
    /// </summary>
    public bool CheckRoundWinner()
    {
        // QC: round_handler_GetEndTime() > 0 && endtime - time <= 0 → round over, no winner (time ran out).
        float endTime = RoundEndTimeSource?.Invoke() ?? 0f;
        if (endTime > 0f)
        {
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            if (endTime - now <= 0f)
            {
                // QC: Send_Notification(NOTIF_ALL, MSG_CENTER, CENTER_ROUND_OVER) + (MSG_INFO, INFO_ROUND_OVER).
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "ROUND_OVER");
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "ROUND_OVER");
                return true;
            }
        }

        int winner = CountAndFindRoundWinner();
        if (winner <= 0)
            return false;

        if (LastRoundWinner == Teams.None)
        {
            LastRoundWinner = winner;
            // QC: Send_Notification(NOTIF_ALL, MSG_CENTER, APP_TEAM_NUM(winner, CENTER_ROUND_TEAM_WIN)) +
            //     (MSG_INFO, APP_TEAM_NUM(winner, INFO_ROUND_TEAM_WIN)) — the per-team round-win banner.
            string suffix = TeamSuffix(winner);
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, $"ROUND_TEAM_WIN_{suffix}");
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, $"ROUND_TEAM_WIN_{suffix}");
            // QC: TeamScore_AddToTeam(winner_team, ST_DOM_CAPS, +1). The round-based schema makes slot 1 the
            // "caps" team primary (DeclareScoreRules roundbased arm).
            Scoring.GameScores.AddToTeam(winner, Scoring.GameScores.TeamSlotSecondary, 1);
            // QC GameRules_limit_score(point_limit) with point_limit = roundbased_point_limit (default 5): the
            // match ends once a team banks that many round CAPS. UpdateLeaderAndCheckLimit is skipped in the
            // round-based variant (it ranks the per-tick ST_SCORE slot, which never moves here), so latch the
            // cumulative-cap match win directly off ST_DOM_CAPS.
            float capLimit = PointLimit;
            if (capLimit > 0f && GetTeamCaps(winner) >= capLimit)
            {
                LeaderTeam = winner;
                MatchEnded = true;
            }
        }
        return true;
    }

    /// <summary>QC teamscores(team, ST_DOM_CAPS) — the round wins banked by a team in the round-based variant.</summary>
    public int GetTeamCaps(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotSecondary);

    /// <summary>QC <c>APP_TEAM_NUM</c> team-code → notification suffix (RED/BLUE/YELLOW/PINK).</summary>
    private static string TeamSuffix(int team) => team switch
    {
        Teams.Red => "RED", Teams.Blue => "BLUE", Teams.Yellow => "YELLOW", Teams.Pink => "PINK", _ => "NEUTRAL",
    };

    /// <summary>
    /// QC <c>ScoreRules_dom</c> (sv_domination.qc): declare Domination's columns + the two TEAM-score slots and
    /// pin the sort keys, branching on <see cref="RoundBased"/> exactly like QC's two arms (D4):
    /// <list type="bullet">
    /// <item>roundbased → <c>field_team(ST_DOM_CAPS,"caps",PRIMARY) + field(SP_DOM_TAKES,"takes")</c> — caps is the
    /// sole score, NO ticks column.</item>
    /// <item>non-roundbased → <c>field_team(ST_DOM_TICKS,"ticks",sp_domticks) + field(SP_DOM_TICKS,"ticks") +
    /// field(SP_DOM_TAKES,"takes")</c>; ST_SCORE (slot 0) carries stprio=sp_score.</item>
    /// </list>
    /// The PLAYER/TEAM primary is SP_SCORE/ST_SCORE normally, but SP_DOM_TICKS/ST_DOM_TICKS when
    /// <c>g_domination_disable_frags</c> is set (kills give no frags, so ticks become the sole score).
    /// </summary>
    private void DeclareScoreRules()
    {
        GS.ScoreRulesBasics(teams: true);
        if (RoundBased)
        {
            // QC roundbased arm: GameRules_scoring(teams, PRIMARY, 0, { field_team(ST_DOM_CAPS,"caps",PRIMARY);
            // field(SP_DOM_TAKES,"takes",0); }) — caps is the team primary (slot 1), ST_SCORE (slot 0) no-prio,
            // and there is NO ticks column.
            GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None);
            GS.SetTeamLabel(GS.TeamSlotSecondary, "caps", Scoring.ScoreFlags.SortPrioPrimary); // ST_DOM_CAPS
            GS.DeclareColumn("DOM_TAKES", Scoring.ScoreFlags.None, "takes");
            GS.SetSortKeys(GS.Score); // player primary = SP_SCORE (sprio=PRIMARY in the QC roundbased arm)
            return;
        }

        // QC non-roundbased arm. sp_domticks/sp_score select which gets PRIMARY (disable_frags ⇒ ticks).
        Scoring.ScoreFlags spDomTicks = DisableFrags ? Scoring.ScoreFlags.SortPrioPrimary : Scoring.ScoreFlags.None;
        Scoring.ScoreFlags spScore    = DisableFrags ? Scoring.ScoreFlags.None : Scoring.ScoreFlags.SortPrioPrimary;
        GS.TeamRulesBasics(scorePrio: spScore); // ST_SCORE (slot 0) "score" with stprio = sp_score
        GS.SetTeamLabel(GS.TeamSlotSecondary, "ticks", spDomTicks); // ST_DOM_TICKS (slot 1)
        GS.DeclareColumn("DOM_TICKS", spDomTicks, "ticks");
        GS.DeclareColumn("DOM_TAKES", Scoring.ScoreFlags.None, "takes");
        Scoring.ScoreField primary = DisableFrags ? GS.Field("DOM_TICKS")! : GS.Score;
        GS.SetSortKeys(primary);
    }

    public override void Deactivate()
    {
        // No hook to remove (point ticks are driven by Tick()).
    }

    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>Register a control point (QC IL_PUSH g_dompoints during dom_controlpoint_setup).</summary>
    public ControlPoint AddControlPoint(Vector3 origin, int initialTeam = Teams.None)
    {
        var cp = new ControlPoint(origin) { OwnerTeam = initialTeam };
        Points.Add(cp);
        return cp;
    }

    /// <summary>
    /// QC dom_controlpoint_setup (spawnfunc dom_controlpoint / team_dom_point): register a control point AND
    /// spawn its world entity at <paramref name="origin"/> (touch = capture, think = tick). Per-point
    /// <paramref name="pointAmt"/>/<paramref name="pointRate"/> mirror the QC entity .frags/.wait fields
    /// (defaults 1 point / 5s) and are used unless the global g_domination_point_amt/rate overrides them.
    /// </summary>
    public ControlPoint SpawnControlPoint(Vector3 origin, int initialTeam = Teams.None,
        float pointAmt = 1f, float pointRate = 5f)
    {
        var cp = new ControlPoint(origin)
        {
            OwnerTeam = initialTeam,
            PerPointAmt = pointAmt <= 0f ? 1f : pointAmt,
            PerPointRate = pointRate <= 0f ? 5f : pointRate,
        };
        Points.Add(cp);

        Entity? e = GametypeEntities.SpawnObjective("dom_controlpoint", origin, Teams.None,
            new Vector3(-48f, -48f, -32f), new Vector3(48f, 48f, 32f),
            touch: PointTouchEntity, think: PointThinkEntity);
        if (e is not null)
        {
            e.GtPointTeam = initialTeam;
            // QC dompoint_captured sets goalentity.team; mirror the owning team onto Entity.Team so the bot
            // goal-rating (havocbot_goalrating_controlpoints → Entity_GetTeam(it.goalentity)) reads ownership.
            e.Team = initialTeam;
            e.GtPointAmt = cp.PerPointAmt;
            e.GtPointRate = cp.PerPointRate;
            e.NextThink = GametypeEntities.Now + 0.1f; // QC dompointthink rate

            // QC dom_controlpoint_setup: setorigin(this, this.origin + '0 0 20'); DropToFloor_QC_DelayedInit(this).
            // Nudge up 20qu then settle the bbox onto the floor (a downward box trace, the port's DropToFloor — cf.
            // MapModels.DropBySpawnflags ALIGN_BOTTOM) so the point rests on the ground rather than at the raw origin.
            Vector3 placed = DropPointToFloor(e, origin + new Vector3(0f, 0f, 20f));
            GametypeEntities.SetOrigin(e, placed);
            cp.Origin = placed;
            e.GtSpawnOrigin = placed;

            // QC spawnfunc(dom_controlpoint): scale 0.6 default + this.effects |= EF_LOWPRECISION, and EF_FULLBRIGHT
            // when g_domination_point_fullbright. The model comes from the owning dom_team (dom_spawnteams default
            // palette: models/domination/dom_<color>.md3, dom_unclaimed.md3 for the neutral start) — set it so the
            // point body is visible (the same fix the CTF flag needed; Entity.Model networks + renders).
            e.Model = DomPointModel(initialTeam);
            e.ScaleFactor = 0.6f; // QC spawnfunc(dom_controlpoint): scale 0.6
            e.Effects |= EfLowPrecision; // QC EF_LOWPRECISION bandwidth hint
            if (Cvar("g_domination_point_fullbright", 0f) != 0f)
                e.Effects |= EfFullBright; // QC EF_FULLBRIGHT when g_domination_point_fullbright

            cp.Entity = e;
        }
        return cp;
    }

    /// <summary>EF_LOWPRECISION (dpextensions.qc:274) — bandwidth hint; QC dom_controlpoint sets it. (== Nexball's.)</summary>
    private const int EfLowPrecision = 4194304;

    /// <summary>EF_FULLBRIGHT — set on the point when g_domination_point_fullbright (QC spawnfunc dom_controlpoint).</summary>
    private const int EfFullBright = 512;

    /// <summary>
    /// QC <c>DropToFloor</c> for the control point: trace the point's bbox straight down from
    /// <paramref name="start"/> and return the rested origin (the box-trace impact), or <paramref name="start"/>
    /// when headless / nothing below. Mirrors MapModels' ALIGN_BOTTOM box trace.
    /// </summary>
    private static Vector3 DropPointToFloor(Entity e, Vector3 start)
    {
        if (Api.Services is null)
            return start;
        Vector3 down = start - new Vector3(0f, 0f, 4096f); // QC droptofloor traces far down for the floor
        TraceResult tr = Api.Trace.Trace(start, e.Mins, e.Maxs, down, MoveFilter.NoMonsters, e);
        return tr.StartSolid ? start : tr.EndPos; // keep the placed origin if it starts embedded
    }

    /// <summary>QC dom_team default palette model (dom_spawnteams): per-team dom_&lt;color&gt;.md3, dom_unclaimed.md3
    /// for the neutral (empty-netname) team. Used for the point body model on spawn and capture.</summary>
    private static string DomPointModel(int team) => team switch
    {
        Teams.Red => "models/domination/dom_red.md3",
        Teams.Blue => "models/domination/dom_blue.md3",
        Teams.Yellow => "models/domination/dom_yellow.md3",
        Teams.Pink => "models/domination/dom_pink.md3",
        _ => "models/domination/dom_unclaimed.md3",
    };

    /// <summary>The <see cref="ControlPoint"/> bound to a world dom_controlpoint <see cref="Entity"/>, or null.</summary>
    public ControlPoint? PointForEntity(Entity e)
    {
        for (int i = 0; i < Points.Count; i++)
            if (ReferenceEquals(Points[i].Entity, e)) return Points[i];
        return null;
    }

    /// <summary>
    /// QC dom_controlpoint_touch → dompoint_captured: <paramref name="player"/> captures
    /// <paramref name="point"/> for their team. Sets the owner team and records the capturing player so
    /// subsequent ticks can credit them (QC point.enemy / enemy_playerid). No-op if already owned by the
    /// player's team. Arms the next tick after the capture.
    /// </summary>
    public void CapturePoint(Player player, ControlPoint point)
    {
        if (MatchEnded || player.IsDead)
            return;
        int team = (int)player.Team;
        if (team == Teams.None || point.OwnerTeam == team)
            return;

        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC dompointtouch: while a round-based round is active but not yet started, captures are blocked
        // (if(round_handler_IsActive() && !round_handler_IsRoundStarted()) return;). Only gate when the host has
        // wired a round-started source (round-based variant); headless/tick play is never blocked.
        if (RoundStartedSource is { } startedSrc && !startedSrc())
            return;

        // QC dompointtouch: the 0.3s self-recapture guard (if(time < this.captime + 0.3) return;) — prevents two
        // straddling enemies from thrashing the point faster than Base and over-crediting DOM_TAKES. In Base
        // `captime` is 0 only before the first-ever capture, where `time` is already well past 0.3s of match clock
        // so the guard is inert; the port's headless clock can read 0, so only arm the guard once the point has
        // actually been captured (Captime > 0) to keep that first-capture behavior Base-faithful.
        if (point.Captime > 0f && now < point.Captime + RecaptureGuard)
            return;

        int teamBefore = point.OwnerTeam; // QC dom_EventLog "taken": this.team at capture time (the PREVIOUS team)
        point.OwnerTeam = team;
        point.Capturer = player;
        point.Captime = now; // QC this.captime = time (arms the recapture guard)
        point.NextTickTime = now + PointRateFor(point);
        if (point.Entity is not null)
        {
            point.Entity.GtPointTeam = team;
            // QC dompoint_captured sets goalentity.team (the bot goal-rating reads Entity_GetTeam(it.goalentity)):
            // mirror the owning team onto Entity.Team too so havocbot_goalrating_controlpoints stops treating every
            // owned point as unclaimed (BotObjectiveRoles.GoalrateControlPoints reads cp.Team).
            point.Entity.Team = team;
            point.Entity.GtCapturer = player;
            point.Entity.GtNextTick = point.NextTickTime;
            // QC dompoint_captured: this.model = head.mdl — swap the point body to the capturing team's dom model
            // (dom_spawnteams palette: dom_<color>.md3). The waypoint sprite/radar tint track this via CollectWaypoints.
            point.Entity.Model = DomPointModel(team);
        }
        // QC dompoint_captured: the capturing player gets DOM_TAKES +1 (a captures-made scoreboard stat).
        player.GtPointTakes += 1; // DOM_TAKES (entity-side counter, kept for compatibility)
        AddCol(player, "DOM_TAKES", 1); // QC GameRules_scoring_add(this.enemy, DOM_TAKES, 1)

        // QC dom_EventLog("taken", this.team, this.dmg_inflictor): emit `:dom:taken:<team_before>[:<playerid>]`
        // to the server event log when sv_eventlog is set. The host gates the sv_eventlog check at the wire site
        // (see GameWorld.ActivateGameType → dom.EventLogEcho); the seam is null in headless / test contexts.
        EventLogEcho?.Invoke(":dom:taken:" + teamBefore +
            (player.PlayerId > 0 ? ":" + player.PlayerId : ""));

        AnnounceCapture(player, point); // QC dompoint_captured: cap sound + narration + INFO notification
    }

    /// <summary>QC dompointtouch self-recapture guard window (time &lt; captime + 0.3).</summary>
    private const float RecaptureGuard = 0.3f;

    /// <summary>
    /// QC dompoint_captured presentation: the local capture sound (dom_team .noise → SND_DOM_CLAIM) plays on the
    /// capturer, the narration (dom_team .noise1 → play2all, default "&lt;Color&gt; team has captured a control
    /// point") plays globally, and the INFO_DOMINATION_CAPTURE_TIME notification ("&lt;player&gt;&lt;message&gt;
    /// (N points every M seconds)") is broadcast to everyone — except the round-based variant, which QC sends as
    /// a bprint (collapsed here to the same INFO line). No-op headlessly (Api/sink guarded).
    /// </summary>
    private void AnnounceCapture(Player player, ControlPoint point)
    {
        // QC: head.noise → _sound(this.enemy or this, CH_TRIGGER, head.noise, ...). The default dom_team cap sound
        // is SND_DOM_CLAIM (domination/claim); play it positionally on the capturing player.
        SoundSystem.PlayOn(player, "DOM_CLAIM");

        // QC: head.noise1 → play2all(narration). The default dom_spawnteams narration is a per-team line; play it
        // globally as the announcer. (The registry has no per-team dom narration sound; the cap sound stands in.)
        // INFO notification carries the textual "<Color> team has captured ..." line below.

        // QC INFO_DOMINATION_CAPTURE_TIME args: s1 = capturer netname, s2 = point .message (default
        // " has captured a control point"), f1 = points-per-tick, f2 = seconds-per-tick.
        float points = PointAmountFor(point);
        float seconds = PointRateFor(point);
        string message = point.Entity is { } e && !string.IsNullOrEmpty(e.Message)
            ? e.Message
            : DefaultCaptureMessage;
        NotificationSystem.Info("DOMINATION_CAPTURE_TIME", player.NetName, message, points, seconds);
    }

    /// <summary>QC dom_controlpoint_setup default: <c>if(this.message == "") this.message = " has captured a
    /// control point";</c> — the point's capture-notification suffix.</summary>
    private const string DefaultCaptureMessage = " has captured a control point";

    /// <summary>
    /// QC dom_controlpoint_setup's WaypointSprite_SpawnFixed(WP_DomNeut, ...) + dompoint_captured's
    /// WaypointSprite_UpdateSprites(WP_Dom&lt;team&gt;) / WaypointSprite_UpdateTeamRadar(RADARICON_DOMPOINT,
    /// colormapPaletteColor(team-1)): one fixed marker per control point at its origin, the def + radar tint
    /// resolved from the current owner (neutral → DomNeut/cyan, else DomRed/Blue/Yellow/Pink + the team color).
    /// Rebuilt each tick from the live ownership (the port's CollectWaypoints pull, like CTF's flag sprites).
    /// </summary>
    public override void CollectWaypoints(List<Waypoints.WaypointSprite> into)
    {
        foreach (ControlPoint cp in Points)
        {
            Vector3 pos = (cp.Entity is { } e ? e.Origin : cp.Origin) + new Vector3(0f, 0f, 32f); // QC origin + '0 0 32'
            into.Add(new Waypoints.WaypointSprite
            {
                SpriteName = DomSpriteName(cp.OwnerTeam),
                FixedOrigin = pos,
                // Visibility Team = 0: QC dom_controlpoint_setup spawns control-point waypoints via
                // WaypointSprite_SpawnFixed (showto=NULL, t=0) → shown to EVERYONE (no SPRITERULE_DEFAULT team
                // restriction). The owning-team color is carried in Color, not the visibility team.
                Team = 0,
                Color = cp.OwnerTeam == Teams.None ? new Vector3(0f, 1f, 1f) : TeamRadarColor(cp.OwnerTeam),
                RadarIcon = 1, // RADARICON_DOMPOINT
                Health = -1f,
            });
        }
    }

    /// <summary>QC dompoint_captured WP_Dom&lt;team&gt; switch (NUM_TEAM_1..4 → Red/Blue/Yellow/Pink; else Neutral).</summary>
    private static string DomSpriteName(int team) => team switch
    {
        Teams.Red => "DomRed",
        Teams.Blue => "DomBlue",
        Teams.Yellow => "DomYellow",
        Teams.Pink => "DomPink",
        _ => "DomNeut",
    };

    /// <summary>Team → radar tint (QC colormapPaletteColor(team-1); neutral handled by the caller as cyan).</summary>
    private static Vector3 TeamRadarColor(int team) => team switch
    {
        Teams.Red => new Vector3(1f, 0.0625f, 0.0625f),
        Teams.Blue => new Vector3(0.0625f, 0.0625f, 1f),
        Teams.Yellow => new Vector3(1f, 1f, 0.0625f),
        Teams.Pink => new Vector3(1f, 0.0625f, 1f),
        _ => new Vector3(1f, 1f, 1f),
    };

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a DOM player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>Entity touch trampoline: a live player on a valid team captures the control point.</summary>
    private void PointTouchEntity(Entity self, Entity other)
    {
        if (other is not Player p || p.IsDead)
            return;
        ControlPoint? cp = PointForEntity(self);
        if (cp is not null)
            CapturePoint(p, cp);
    }

    /// <summary>
    /// QC AnimateDomPoint (sv_domination.qc:140): advance the point's frame animation. The frame increments
    /// every <paramref name="cp"/>.<see cref="ControlPoint.TWidth"/> seconds (default 0.02s), wrapping from
    /// <see cref="ControlPoint.TLength"/> (default 239) back to 0.
    /// </summary>
    private static void AnimateDomPoint(ControlPoint cp, Entity self)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (cp.PainFinished > now)
            return;
        cp.PainFinished = now + cp.TWidth;
        // Adjust the entity's next think if this animation tick is sooner than the scheduled think.
        if (self.NextThink > cp.PainFinished)
            self.NextThink = cp.PainFinished;

        self.Frame++;
        if (self.Frame > cp.TLength)
            self.Frame = 0;
    }

    /// <summary>Per-point think trampoline (QC dompointthink): animate the point frame and tick if owned and timer elapsed.</summary>
    private void PointThinkEntity(Entity self)
    {
        self.NextThink = GametypeEntities.Now + 0.1f; // QC dompointthink rate
        ControlPoint? cp = PointForEntity(self);
        if (cp is not null)
        {
            AnimateDomPoint(cp, self); // QC AnimateDomPoint frame animation
            PointThink(cp);
        }
    }

    /// <summary>
    /// QC dompointthink for one point: if owned and its tick timer elapsed, grant the per-point amount to the
    /// owning team (and the capturing player while present), then re-arm. Applies the win condition.
    /// </summary>
    public void PointThink(ControlPoint cp)
    {
        // QC dompointthink only grants per-tick score in the non-roundbased mode (`if (!domination_roundbased)`);
        // roundbased Domination scores via round wins into ST_DOM_CAPS, not ticks.
        if (MatchEnded || RoundBased || cp.OwnerTeam == Teams.None)
            return;
        // QC dompointthink: `if (game_stopped || this.delay > time || time < game_starttime) return;` — no points
        // during warmup/match-over (game_stopped) or before the match clock starts (game_starttime). Read the live
        // game_stopped mirror the rest of gameplay uses (VehicleCommon.GameStopped, pushed every frame from
        // GameWorld.OnEndFrame = Intermission.Running || MatchEnded || Timeout.IsPaused) — Scoring.GameScores.GameStopped
        // is never assigned on the server path, so a Timeout-paused live match would otherwise keep ticking points.
        if (VehicleCommon.GameStopped)
            return;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float gameStart = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
        if (now < gameStart)
            return;
        if (now < cp.NextTickTime)
            return;

        int amt = (int)PointAmountFor(cp);
        AddTeamScore(cp.OwnerTeam, amt); // QC TeamScore_AddToTeam(team, ST_SCORE, amt) + (team, ST_DOM_TICKS, amt)
        if (cp.Capturer is { IsFreed: false } cap && (int)cap.Team == cp.OwnerTeam)
        {
            cap.ScoreFrags += amt;            // QC GameRules_scoring_add(enemy, SCORE, fragamt)
            AddCol(cap, "DOM_TICKS", amt);    // QC GameRules_scoring_add(enemy, DOM_TICKS, fragamt)
        }
        cp.NextTickTime = now + PointRateFor(cp);
        if (cp.Entity is not null)
            cp.Entity.GtNextTick = cp.NextTickTime;
        UpdateLeaderAndCheckLimit();
    }

    /// <summary>Per-tick amount for a point: g_domination_point_amt override, else the point's own amount.</summary>
    public float PointAmountFor(ControlPoint cp)
        => TryCvar(CvarPointAmt, out float v) ? v : cp.PerPointAmt;

    /// <summary>Per-tick interval for a point: g_domination_point_rate override, else the point's own rate.</summary>
    public float PointRateFor(ControlPoint cp)
        => TryCvar(CvarPointRate, out float v) ? v : cp.PerPointRate;

    /// <summary>
    /// Advance domination one step (QC dompointthink, evaluated per point): every owned control point whose
    /// tick timer has elapsed grants its per-point amount (<see cref="PointAmountFor"/>) to its owning team
    /// (and the capturing player), then re-arms. Delegates to <see cref="PointThink"/>. Call each tick; safe
    /// after <see cref="MatchEnded"/>. Applies the point-limit win condition after granting.
    /// </summary>
    public void Tick()
    {
        if (MatchEnded)
            return;
        // Delegate to the per-point think so each point honors its own amt/rate (QC dompointthink per entity).
        for (int i = 0; i < Points.Count; i++)
            PointThink(Points[i]);
    }

    /// <summary>
    /// QC <c>dompoint_captured</c>'s team credit: a tick adds <paramref name="delta"/> to BOTH the team's
    /// ST_SCORE (slot 0) and ST_DOM_TICKS (slot 1) — they track the same running tick total
    /// (<c>TeamScore_AddToTeam(team, ST_SCORE, fragamt); TeamScore_AddToTeam(team, ST_DOM_TICKS, fragamt);</c>).
    /// </summary>
    public void AddTeamScore(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None)
            return;
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotScore, delta);     // QC ST_SCORE
        Scoring.GameScores.AddToTeam(t, Scoring.GameScores.TeamSlotSecondary, delta); // QC ST_DOM_TICKS
    }

    public int GetTeamScore(int team) => Scoring.GameScores.TeamScore(team, Scoring.GameScores.TeamSlotScore);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on the ranking primary
    /// (ST_SCORE / ST_DOM_TICKS, read via GetTeamScore), so a tied timed Domination enters overtime instead of
    /// drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(Scoring.GameScores.LeaderTeam(), Scoring.GameScores.SecondTeam(), GetTeamScore);

    public void UpdateLeaderAndCheckLimit()
    {
        // QC: Dom teams rank by the team primary slot (ST_SCORE, or ST_DOM_TICKS when disable_frags). LeaderTeam /
        // SecondTeam read the flag-aware two-slot compare from GameScores (the source of truth). The point-limit
        // check uses that primary slot's value too (ticks == score, so GetTeamScore reads it correctly either way).
        int bestTeam = Scoring.GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamScore(bestTeam);
        float pointLimit = PointLimit;
        if (pointLimit > 0f && bestScore >= pointLimit)
            MatchEnded = true;

        int secondTeam = Scoring.GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamScore(secondTeam)) >= leadLimit)
            MatchEnded = true;
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

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;
}

/// <summary>
/// A Domination control point — the Godot-free essence of the QC dom_controlpoint edict
/// (.goalentity.team owner, .enemy capturer, .delay next-tick time, per-point .frags/.wait, frame anim
/// .t_width/.t_length/.pain_finished). Tracks its world position, owning team, capturer, next-tick time,
/// per-point amt/rate, and — when a facade is wired — the world <see cref="Entity"/>. The point model,
/// neutral interim state, and waypoint sprites remain client concerns.
/// </summary>
public sealed class ControlPoint
{
    /// <summary>World position of the point (QC entity origin) — updated once after the spawn DropToFloor.</summary>
    public Vector3 Origin;

    /// <summary>The team currently owning the point (QC goalentity.team), or <see cref="Teams.None"/>.</summary>
    public int OwnerTeam = Teams.None;

    /// <summary>The player who last captured the point (QC .enemy), credited on ticks while still present.</summary>
    public Player? Capturer;

    /// <summary>Absolute sim time the next point tick is due (QC .delay).</summary>
    public float NextTickTime;

    /// <summary>Absolute sim time of the last capture (QC .captime), arming the 0.3s self-recapture guard.</summary>
    public float Captime;

    /// <summary>Per-point score amount per tick (QC entity .frags), used unless g_domination_point_amt overrides.</summary>
    public float PerPointAmt = 1f;

    /// <summary>Per-point tick interval (QC entity .wait), used unless g_domination_point_rate overrides.</summary>
    public float PerPointRate = 5f;

    /// <summary>The world entity representing this point (QC the dom_controlpoint edict), or null (headless).</summary>
    public Entity? Entity;

    /// <summary>QC .pain_finished — absolute sim time when the next frame increment is due (frame animation).</summary>
    public float PainFinished;

    /// <summary>QC .t_width — frame animation interval (default 0.02s between frame increments).</summary>
    public float TWidth = 0.02f;

    /// <summary>QC .t_length — frame animation max frame number (default 239, so frames 0..239).</summary>
    public float TLength = 239f;

    public ControlPoint(Vector3 origin) => Origin = origin;
}

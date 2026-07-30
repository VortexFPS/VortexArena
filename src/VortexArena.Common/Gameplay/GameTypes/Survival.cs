using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Survival gametype — port of <c>CLASS(Survival, Gametype)</c>
/// (common/gametypes/gametype/survival/survival.qh + sv_survival.qc).
///
/// A round-based hidden-role mode (built on the LMS family): at round start a fraction of the players
/// (cvar <c>g_survival_hunter_count</c>, default 0.25, ≥1 and &lt; total) are secretly assigned the
/// <see cref="SurvStatus.Hunter"/> role; everyone else is <see cref="SurvStatus.Prey"/>. Roles are concealed
/// (frags/obituaries are anonymized so hunters aren't outed). A death eliminates the player for the round
/// (no respawn). The round ends when one side is entirely gone (QC <c>WinningCondition</c>): if any hunter is
/// still alive the hunters win, otherwise the surviving prey win. Kills bank no points mid-round — score is
/// awarded to the winning side when the round ends.
///
/// Faithfully ported (the role/win rule):
///  - per-player role + alive state (<see cref="SurvState"/>) and the hunter-count assignment
///    (<see cref="AssignRoles"/>, QC g_survival_hunter_count);
///  - Combat.Death → eliminate the victim for the round (no respawn) and re-check the side-wipe condition;
///  - round-over win latch: hunters win if ≥1 hunter alive, else prey win (QC survivor/hunter counts).
///
/// Faithfully ported (objective layer):
///  - the round handler + warmup (<see cref="Handler"/>, QC round_handler_Spawn with survival_CheckTeams /
///    survival_CheckWinner / survival_RoundStart), driven by <see cref="Tick"/>: a new round assigns roles
///    (<see cref="AssignRoles"/>) and resolves on a side wipe (<see cref="CheckWinningCondition"/>).
///
/// Faithfully ported (scoring):
///  - banked validkills (QC GiveFragsForKill / survival_validkills): a kill during an active round banks its
///    frag value rather than scoring immediately (<see cref="SurvState.ValidKills"/>); at round end the banked
///    kills are awarded as SP_SCORE plus the survival/hunt bonuses (<see cref="UpdateScores"/>, QC Surv_UpdateScores);
///  - role anonymization (QC the AddPlayerScore hook): SP_KILLS/DEATHS/SUICIDES/DMG/DMGTAKEN are suppressed
///    while a survival round runs so the scoreboard doesn't out the hidden hunters.
///
/// Deferred (NOTE — cross-boundary): the obituary color anonymization (client), forced spectate of eliminated
/// players, and the score networking/HUD — client / host concerns.
/// </summary>
[GameType]
public sealed class Survival : GameType
{
    // ----- hunter-count cvar + default (QC g_survival_hunter_count = 0.25) -----
    private const string CvarHunterCount = "g_survival_hunter_count";
    private const float  DefaultHunterCount = 0.25f;

    // ----- teamkill-punishment cvar (QC g_survival_punish_teamkill = true) -----
    private const string CvarPunishTeamkill = "g_survival_punish_teamkill";

    /// <summary>QC <c>survival_status</c> (SURV_STATUS_*): which side a player is on for the round.</summary>
    public enum SurvStatus
    {
        None = 0,
        /// <summary>SURV_STATUS_PREY: a survivor who must outlast the hunters.</summary>
        Prey = 1,
        /// <summary>SURV_STATUS_HUNTER: secretly tasked with eliminating the prey.</summary>
        Hunter = 2,
    }

    /// <summary>Per-player survival role + alive flag (QC survival_status + IS_DEAD).</summary>
    public sealed class SurvState
    {
        public SurvStatus Status;
        /// <summary>True while the player is still in the round (set false on death — no respawn in Survival).</summary>
        public bool Alive = true;
        /// <summary>QC <c>.survival_validkills</c>: frags banked this round, awarded to the side at round end.</summary>
        public int ValidKills;
    }

    private readonly Dictionary<Player, SurvState> _states = new();

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to an elimination.</summary>
    public IMatchEvents? Events;

    /// <summary>
    /// Optional host sink for the Survival notifications QC sends via <c>Send_Notification</c> (the actual
    /// MSG_CENTER/MSG_INFO routing is a host/CSQC concern, so the gametype only signals WHAT to send and the
    /// host wires it to the notification system). Set by the host on <see cref="Activate"/>; null = headless.
    /// </summary>
    public ISurvivalNotifications? Notifications;

    /// <summary>The Survival notifications the gametype raises (QC Surv_RoundStart / Surv_CheckWinner sends).</summary>
    public interface ISurvivalNotifications
    {
        /// <summary>QC <c>CENTER_SURVIVAL_SURVIVOR</c> / <c>CENTER_SURVIVAL_HUNTER</c>: tell each player their role
        /// privately at round start (<paramref name="hunter"/> = the player is a hunter).</summary>
        void Role(Player p, bool hunter);

        /// <summary>QC <c>CENTER/INFO_SURVIVAL_HUNTER_WIN</c> / <c>SURVIVAL_SURVIVOR_WIN</c> / <c>ROUND_TIED</c>:
        /// broadcast the round result. <paramref name="winningSide"/> is the side that won (None = tie).</summary>
        void RoundResult(SurvStatus winningSide);

        /// <summary>QC <c>CENTER_ALONE</c> (surv_LastPlayerForTeam_Notify): tell the now-sole survivor of a side
        /// that their last same-status ally just died/left mid-round.</summary>
        void Alone(Player p);
    }

    /// <summary>QC checkrules end-of-round latch: true once one side has been entirely eliminated.</summary>
    public bool RoundOver { get; private set; }

    /// <summary>The side that won the round once <see cref="RoundOver"/> is set (None until then).</summary>
    public SurvStatus WinningSide { get; private set; }

    /// <summary>
    /// QC <c>round_handler_GetEndDelayTime()</c> (server/round_handler.qc): the absolute sim time the round's
    /// resolution has been deferred to when <c>g_survival_round_enddelay &gt; 0</c> is set — a side has been wiped
    /// but Surv_CheckWinner waits out this delay before awarding scores (and re-checks each frame, resetting it if
    /// both sides become alive again). <c>-1</c> = no deferral scheduled (QC's sentinel). Stock cfg sets enddelay=0
    /// so this is never armed; only an operator setting a positive enddelay engages it.
    /// </summary>
    private float _endDelayTime = -1f;

    public Survival()
    {
        NetName = "surv";
        DisplayName = "Survival";
        TeamGame = false; // QC: GAMETYPE_FLAG_USEPOINTS only (roles are hidden, not visible teams)
    }

    /// <summary>The round-phase driver (QC round_handler) — created on <see cref="Activate"/>.</summary>
    public RoundHandler? Handler { get; private set; }

    /// <summary>The roster the round handler evaluates (set by the host before <see cref="Tick"/>).</summary>
    private IReadOnlyList<Player> _roster = System.Array.Empty<Player>();

    /// <summary>Provide the controller with the current roster (call before <see cref="Tick"/>).</summary>
    public void SetRoster(IReadOnlyList<Player> roster) => _roster = roster;

    public override void OnInit()
    {
        // QC: round_handler_Spawn drives the round (survival_RoundStart assigns roles, survival_CheckWinner
        // resolves the side wipe). The handler is created in Activate (it needs the roster).
    }

    public SurvState GetState(Player p)
    {
        if (!_states.TryGetValue(p, out SurvState? st))
        {
            st = new SurvState();
            _states[p] = st;
        }
        return st;
    }

    public void AddPlayer(Player p) => GetState(p);
    public void RemovePlayer(Player p) => _states.Remove(p);

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        RoundOver = false;
        WinningSide = SurvStatus.None;

        // QC round_handler_Spawn(survival_CheckTeams, survival_CheckWinner, survival_RoundStart).
        Handler = new RoundHandler(() => Api.Services is not null ? Api.Clock.Time : 0f)
        {
            CanRoundStart = () => CountActive() >= 2, // need at least 2 players for both sides
            CanRoundEnd = () => { CheckWinningCondition(); return RoundOver; },
            OnRoundStart = () => AssignRoles(_roster), // QC survival_RoundStart secretly assigns hunters
        };
        // QC round_handler_Init(5, g_survival_warmup=10, g_survival_round_timelimit=120). The end-delay arg is a
        // hardcoded 5 in QC (NOT g_survival_round_enddelay, which gates a SEPARATE deferred-resolution path);
        // warmup/round-timelimit come from cvars with Base-faithful fallbacks (10 / 120).
        float warmup = TryCvar("g_survival_warmup", out float w) ? w : 10f;
        float rtl = TryCvar("g_survival_round_timelimit", out float t) ? t : 120f;
        Handler.Init(5f, warmup, rtl);

        // QC surv_Initialize GameRules_scoring(0, SFL_SORT_PRIO_PRIMARY, 0, { field(SP_SURV_SURVIVALS, "survivals",
        // 0); field(SP_SURV_HUNTS, "hunts", SFL_SORT_PRIO_SECONDARY); }): SP_SCORE is the player primary (banked
        // validkills awarded at round end), SP_SURV_HUNTS the secondary, plus the SURV_SURVIVALS stat column.
        GameScores.ScoreRulesBasics(teams: false);
        GameScores.DeclareColumn("SURV_SURVIVALS", ScoreFlags.None, "survivals"); // QC field(SP_SURV_SURVIVALS, "survivals", 0)
        GameScores.DeclareColumn("SURV_HUNTS", ScoreFlags.None, "hunts");         // QC field(SP_SURV_HUNTS, "hunts", SECONDARY)
        GameScores.SetSortKeys(GameScores.Score, GameScores.Field("SURV_HUNTS"));

        // QC the survival AddPlayerScore hook: anonymize kills/deaths so the scoreboard can't out the hunters.
        GameScores.AddPlayerScoreHook = AnonymizeScore;

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>
    /// Advance the Survival round handler one frame (QC round_handler_Think). Assigns roles at round start and
    /// resolves the round on a side wipe. Call each tick after <see cref="SetRoster"/>.
    /// </summary>
    public void Tick() => Handler?.Tick();

    /// <summary>Number of tracked players currently in the round (alive or not), for the start gate.</summary>
    private int CountActive()
    {
        int n = 0;
        for (int i = 0; i < _roster.Count; i++)
            if ((int)_roster[i].Team != Teams.None || _states.ContainsKey(_roster[i]))
                n++;
        return n == 0 ? _roster.Count : n;
    }

    /// <summary>
    /// QC <c>Surv_CheckPlayers</c> (sv_survival.qc:195-226), the round handler's canRoundStart: the countdown may
    /// proceed once ≥2 LIVE players (IS_PLAYER &amp;&amp; !IS_DEAD) are present (both sides need a member). Used as
    /// the LIVE round handler's start predicate so the round-start moment (and the AssignRoles it fires) tracks the
    /// real player count, not the generic team-based default.
    /// </summary>
    public bool HandlerCanRoundStart()
    {
        int live = 0;
        for (int i = 0; i < _roster.Count; i++)
            if (IsLive(_roster[i]))
                live++;
        return live >= 2;
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
        if (GameScores.AddPlayerScoreHook == AnonymizeScore)
            GameScores.AddPlayerScoreHook = null; // stop anonymizing once survival ends
    }

    /// <summary>The hunter role of a player (None if untracked).</summary>
    public SurvStatus StatusOf(Player p) => _states.TryGetValue(p, out SurvState? st) ? st.Status : SurvStatus.None;

    /// <summary>
    /// QC <c>Surv_RoundStart</c> (sv_survival.qc:160-193) role assignment. The hunter count is derived from the
    /// LIVE player count (FOREACH_CLIENT(IS_PLAYER &amp;&amp; !IS_DEAD) — observers and dead players don't count);
    /// the count comes from <c>g_survival_hunter_count</c> (≥1 = absolute, &lt;1 = a fraction of the live total)
    /// bounded to at least 1 and at most (live − 1). The hunter subset is a RANDOM selection over the live
    /// players (QC FOREACH_CLIENT_RANDOM — an inside-out Fisher–Yates shuffle via the seeded
    /// <see cref="XonoticGodot.Common.Math.Prandom"/> facade), NOT the first roster slots. Everyone starts as
    /// prey and alive; the chosen subset becomes hunters. Also clears any prior round result.
    /// </summary>
    public void AssignRoles(IReadOnlyList<Player> roster)
    {
        RoundOver = false;
        WinningSide = SurvStatus.None;
        _endDelayTime = -1f; // QC round_handler_Init resets the end-delay sentinel for the new round

        // QC FOREACH_CLIENT: everyone resets to prey (a non-live client gets survival_status = 0, but the port's
        // roster is the in-round set; untracked/observer/dead players keep Prey and don't count for the split).
        foreach (Player p in roster)
        {
            SurvState st = GetState(p);
            st.Status = SurvStatus.Prey;
            st.Alive = true;
        }

        // QC: playercount only counts live players (IS_PLAYER && !IS_DEAD). Build that live subset.
        var live = new List<Player>(roster.Count);
        foreach (Player p in roster)
            if (IsLive(p))
                live.Add(p);

        int playercount = live.Count;
        if (playercount < 2)
            return; // need at least 2 live players to have both sides

        int hunters = HunterCount(playercount);

        // QC FOREACH_CLIENT_RANDOM(IS_PLAYER && !IS_DEAD): inside-out Knuth–Fisher–Yates shuffle of the live
        // players (server/utils.qh:54-79), then take the first hunter_count from the shuffled order. Faithful to
        // QC's floor(random() * (cnt + 1)) using the seeded deterministic Prandom (ADR-0010).
        var shuffled = new Player[playercount];
        for (int cnt = 0; cnt < playercount; cnt++)
        {
            int j = (int)System.MathF.Floor(XonoticGodot.Common.Math.Prandom.Float() * (cnt + 1));
            if (j > cnt) j = cnt; // guard: random() can theoretically reach the open bound after float rounding
            if (j != cnt)
                shuffled[cnt] = shuffled[j];
            shuffled[j] = live[cnt];
        }
        for (int i = 0; i < hunters && i < playercount; i++)
            GetState(shuffled[i]).Status = SurvStatus.Hunter;

        // QC Surv_RoundStart (sv_survival.qc:186-192): privately tell each live player their secret role at
        // round start (prey see CENTER_SURVIVAL_SURVIVOR, hunters CENTER_SURVIVAL_HUNTER).
        if (Notifications is not null)
            foreach (Player p in live)
                Notifications.Role(p, GetState(p).Status == SurvStatus.Hunter);
    }

    /// <summary>QC IS_PLAYER(it) &amp;&amp; !IS_DEAD(it): a live, non-observer player counts for the role split.</summary>
    private static bool IsLive(Player p) => !p.IsObserver && !p.IsDead;

    /// <summary>
    /// QC hunter-count formula (sv_survival.qc:175): bound(1, (cvar≥1 ? cvar : floor(playercount*cvar)),
    /// playercount−1) — over the LIVE player count.
    /// </summary>
    public int HunterCount(int playercount)
    {
        float c = TryCvar(CvarHunterCount, out float v) ? v : DefaultHunterCount;
        int count = c >= 1f ? (int)c : (int)System.MathF.Floor(playercount * c);
        int max = playercount - 1;
        if (count < 1) count = 1;
        if (count > max) count = max;
        return count;
    }

    /// <summary>Number of prey still alive this round.</summary>
    public int AlivePrey() => CountAlive(SurvStatus.Prey);

    /// <summary>Number of hunters still alive this round.</summary>
    public int AliveHunters() => CountAlive(SurvStatus.Hunter);

    private int CountAlive(SurvStatus side)
    {
        int n = 0;
        foreach (SurvState st in _states.Values)
            if (st.Alive && st.Status == side) n++;
        return n;
    }

    /// <summary>
    /// The obituary handler — eliminate the victim for the round (no respawn in Survival, QC forces
    /// spectate). Kills bank no points here (QC GiveFragsForKill zeroes the score and stashes it for the
    /// round winner). After the elimination, re-check the side-wipe win condition.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (RoundOver)
            return false;

        // QC surv_LastPlayerForTeam_Notify (PlayerDies): before the victim is removed, if its death leaves
        // exactly one same-status ally alive, that lone survivor gets the CENTER_ALONE notify. Round-active only.
        if (RoleAssigned)
            NotifyLastForTeam(victim);

        bool roundStarted = RoleAssigned; // QC: round_handler_IsActive() && round_handler_IsRoundStarted()

        Player? attacker = ev.Attacker as Player;
        bool sameStatus = attacker is not null && !ReferenceEquals(attacker, victim)
            && StatusOf(attacker) != SurvStatus.None
            && StatusOf(attacker) == StatusOf(victim);

        // QC GiveFragsForKill: bank the kill into the attacker's validkills instead of scoring it now (the score
        // goes to the winning side at round end). QC only banks while the round is actually started (not warmup).
        // A valid kill is by a live, distinct attacker.
        if (roundStarted && attacker is not null && !ReferenceEquals(attacker, victim) && GetState(attacker).Alive)
        {
            // QC banks the NET of two GiveFrags calls. Survival has no teams (USEPOINTS only), so even a
            // same-status ("ally") kill takes the NON-same-team MURDER branch in Obituary (damage.qc:330) and
            // banks the normal +1 via GiveFragsForKill. THEN survival's ClientObituary hook (sv_survival.qc) adds
            // a second GiveFrags(-1 if punish else -2), which ALSO banks through GiveFragsForKill. Net banked for
            // an ally kill is therefore +1 + (-1) = 0 (punish) or +1 + (-2) = -1 (no-punish); a legit kill is +1.
            int delta = sameStatus ? (1 + (PunishTeamkill ? -1 : -2)) : +1;
            GetState(attacker).ValidKills += delta;
        }

        GetState(victim).Alive = false; // eliminated for the round

        // QC PlayerDies (sv_survival.qc:377-383): force the eliminated player to spectate. While a round is live
        // (!allowed_to_spawn) the respawn is made SILENT and pushed out (respawn_time = time+2) so the kill-cam
        // doesn't snap the spectator camera around mid-round; the FORCE flag is always set. The actual no-respawn
        // is enforced by the round gate (RespawnDuePlayers / DeadPlayerThink skip while a round is started), so
        // setting these flags here is the same camera-stability nuance QC adds, not the elimination itself.
        if (roundStarted) // QC !allowed_to_spawn (a round is in progress)
        {
            victim.RespawnFlags |= RespawnFlag.Silent;
            victim.RespawnTime = (Api.Services is not null ? Api.Clock.Time : 0f) + 2f;
        }
        victim.RespawnFlags |= RespawnFlag.Force;
        // QC bot_clearqueue(frag_target): the port has no bot command-queue subsystem to clear (bot navigation
        // goals aren't modeled as a queue here), so there is nothing to clear — the bot simply stays eliminated.

        Events?.OnFrag(attacker, victim, ev.DeathType);

        // QC PlayerDies teamkill punishment (sv_survival.qc:392-394): killing a same-status ally costs the
        // killer their life (mirror damage), but only once the round has actually started and not for the
        // "needs-kill" environmental deaths (slime/lava/swamp/void), which are routed differently.
        if (PunishTeamkill && sameStatus && roundStarted && attacker is not null
            && !NeedKill(ev.DeathType) && GetState(attacker).Alive)
        {
            // QC: Damage(attacker, attacker, attacker, 100000, DEATH_MIRRORDAMAGE, attacker.origin, '0 0 0').
            Combat.Damage(attacker, attacker, attacker, 100000f, DeathTypes.MirrorDamage, attacker.Origin, Vector3.Zero);
        }

        CheckWinningCondition();
        return false;
    }

    /// <summary>QC <c>g_survival_punish_teamkill</c> (default true): auto-kill a player who frags a same-status ally.</summary>
    private static bool PunishTeamkill => !TryCvar(CvarPunishTeamkill, out float v) || v != 0f;

    /// <summary>
    /// QC <c>ITEM_DAMAGE_NEEDKILL(dt)</c> (util.qh:123): the environmental "must kill" deathtypes
    /// (DEATH_HURTTRIGGER/SLIME/LAVA/SWAMP — the port's Void stands in for HURTTRIGGER). The teamkill
    /// auto-punish is suppressed for these so a victim shoved into lava doesn't kill its (innocent) attacker.
    /// </summary>
    private static bool NeedKill(string? deathType)
    {
        string b = DeathTypes.BaseOf(deathType);
        return b == DeathTypes.Void || b == DeathTypes.Slime || b == DeathTypes.Lava || b == DeathTypes.Swamp;
    }

    /// <summary>
    /// QC <c>surv_LastPlayerForTeam_Notify</c> (sv_survival.qc:345-368): when <paramref name="leaving"/> dies or
    /// leaves mid-round, if it leaves EXACTLY ONE living same-status ally, send that lone survivor CENTER_ALONE.
    /// Called from the death path (and, host-side, from disconnect / forced-observe — see todos).
    /// </summary>
    public void NotifyLastForTeam(Player leaving)
    {
        if (Notifications is null || !RoleAssigned)
            return;
        SurvStatus side = StatusOf(leaving);
        if (side == SurvStatus.None)
            return;
        Player? last = null;
        foreach (var (p, st) in _states)
        {
            if (ReferenceEquals(p, leaving) || !st.Alive || st.Status != side)
                continue;
            if (last is null) last = p;
            else return; // more than one same-status ally still alive → nobody is "alone"
        }
        if (last is not null)
            Notifications.Alone(last);
    }

    /// <summary>
    /// QC the <c>MUTATOR_HOOKFUNCTION(surv, ClientDisconnect)</c> / <c>MakePlayerObserver</c> hooks
    /// (sv_survival.qc:398-429): a player who leaves play mid-round — disconnects or is forced to spectate — also
    /// triggers the last-of-side "you are alone" notify for any same-status ally it leaves solo, EXACTLY like a
    /// death does (both QC hooks call <c>surv_LastPlayerForTeam_Notify</c>, gated on the leaver having been a live
    /// player). The port dispatches this override from both ClientManager paths (ClientDisconnect + the
    /// MakePlayerObserver demotion), closing the gap where 'alone' fired on an ally's death but not on a leave.
    /// </summary>
    public override void OnPlayerRemoved(Player player)
    {
        // QC: if (IS_PLAYER(player) && !IS_DEAD(player)) surv_LastPlayerForTeam_Notify(player). Only a leaver who
        // was still IN the round (alive, not already eliminated) newly orphans a same-status ally; an already-dead
        // player's allies were already counted alone at the death.
        if (_states.TryGetValue(player, out SurvState? st) && st.Alive)
            NotifyLastForTeam(player);
    }

    /// <summary>
    /// QC Survival WinningCondition (core): while both sides still have a living member the round continues.
    /// Once one side is wiped, the round is over — hunters win if any hunter is still alive, otherwise the
    /// surviving prey win. The round handler's warmup/end-delay timing is deferred.
    /// </summary>
    public void CheckWinningCondition()
    {
        if (_states.Count == 0)
            return;

        if (RoundOver)
            return; // already resolved (avoid double-awarding the round score)

        int prey = AlivePrey();
        int hunters = AliveHunters();

        // QC Surv_CheckWinner (sv_survival.qc:76-95): the match-timeout → survivors-win branch ONLY runs when
        // g_survival_round_enddelay == -1 (sv_survival.qc:78-79). The shipped default is 0 (gametypes-server.cfg:704),
        // so in stock config Surv_CheckWinner returns 0 with both sides alive at the timer and the round does NOT
        // auto-resolve. We gate the timeout the same way so the port doesn't end a round stock Base leaves running.
        if (prey > 0 && hunters > 0)
        {
            // QC sv_survival.qc:108-111: reset the end-delay timer here only for consistency (Survival players
            // can't be resurrected, so a both-sides-alive frame after a wipe shouldn't normally happen, but match
            // QC and clear any scheduled deferral if it does).
            _endDelayTime = -1f;
            if (RoundTimedOut())
                EndRoundTimedOut();
            return; // both sides alive — round continues until the timer (above) or a wipe
        }

        // QC Surv_CheckWinner (sv_survival.qc:114-128): "delay round ending a bit" — when
        // g_survival_round_enddelay > 0 and we're still before the round timelimit, defer the resolution by
        // enddelay seconds (re-checked each frame). Stock cfg sets enddelay=0 so this is skipped and the round
        // resolves immediately on the first wipe frame (matching Base at the shipped default).
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float roundEnd = Handler?.RoundEndTime ?? 0f;
        if (TryCvar("g_survival_round_enddelay", out float enddelay) && enddelay > 0f
            && roundEnd > 0f && roundEnd - now > 0f) // don't delay past the round timelimit
        {
            if (_endDelayTime == -1f)
            {
                // QC: round_handler_SetEndDelayTime(min(time + enddelay, round_handler_GetEndTime())).
                _endDelayTime = System.MathF.Min(now + enddelay, roundEnd);
                return; // schedule the deferral, re-check next frame
            }
            if (_endDelayTime >= now)
                return; // still waiting out the end-delay
        }

        RoundOver = true;
        // QC Surv_CheckWinner (sv_survival.qc:130-144): hunters win if any remain, else surviving prey win, else
        // (both sides reached zero on the same frame) the round is a tie (ROUND_TIED).
        WinningSide = hunters > 0 ? SurvStatus.Hunter
            : prey > 0 ? SurvStatus.Prey
            : SurvStatus.None; // tie
        UpdateScores(timedOut: false); // a side wipe ends the round → award the banked scores
        Notifications?.RoundResult(WinningSide); // QC SURVIVAL_*_WIN broadcast (None winner → ROUND_TIED)
    }

    /// <summary>
    /// QC Surv_CheckWinner's timeout branch test (sv_survival.qc:77-79): true once the round handler's
    /// <see cref="RoundHandler.RoundEndTime"/> (armed from <c>g_survival_round_timelimit</c>) has elapsed —
    /// BUT only when <c>g_survival_round_enddelay == -1</c>. The shipped default is 0
    /// (gametypes-server.cfg:704), so in stock config this branch is OFF and a hiding-prey round is NOT
    /// auto-resolved on the timer (Surv_CheckWinner returns 0 with both sides alive).
    /// </summary>
    private bool RoundTimedOut()
    {
        if (Handler is null || !Handler.IsRoundStarted)
            return false;
        // QC sv_survival.qc:79: the timeout→survivors-win branch is gated on autocvar_g_survival_round_enddelay == -1.
        if (!(TryCvar("g_survival_round_enddelay", out float enddelay) && enddelay == -1f))
            return false;
        float end = Handler.RoundEndTime;
        return end > 0f && (Api.Services is not null ? Api.Clock.Time : 0f) >= end;
    }

    /// <summary>
    /// QC <c>Surv_UpdateScores</c>: with the round over, award every player their banked validkills as SP_SCORE,
    /// then the per-side bonuses — surviving prey get SURV_SURVIVALS (+ a SCORE point if the round timed out and
    /// g_survival_reward_survival is set), surviving hunters get SURV_HUNTS. Clears the banked kills. Idempotent
    /// per round (CheckWinningCondition guards re-entry).
    /// </summary>
    public void UpdateScores(bool timedOut)
    {
        ScoreField? survivals = GameScores.Field("SURV_SURVIVALS");
        ScoreField? hunts = GameScores.Field("SURV_HUNTS");
        bool rewardSurvival = !TryCvar("g_survival_reward_survival", out float rs) || rs != 0f;

        foreach (var (player, st) in _states)
        {
            if (st.ValidKills != 0)
            {
                GameScores.AddToPlayer(player, GameScores.Score, st.ValidKills); // banked frags → SP_SCORE
                st.ValidKills = 0;
            }
            if (!st.Alive)
                continue; // bonuses are only for players who survived the round

            if (st.Status == SurvStatus.Prey)
            {
                if (timedOut && rewardSurvival)
                    GameScores.AddToPlayer(player, GameScores.Score, 1); // reward outlasting the round timer
                if (survivals is not null) GameScores.AddToPlayer(player, survivals, 1);
            }
            else if (st.Status == SurvStatus.Hunter)
            {
                if (hunts is not null) GameScores.AddToPlayer(player, hunts, 1);
            }
        }
    }

    // ============================================================================================
    //  HUD/wire disclosure surface (QC SurvivalStatuses_SendEntity + surv_isEliminated)
    // ============================================================================================

    /// <summary>
    /// True while the round's hidden roles are live (assigned by <see cref="AssignRoles"/> when
    /// <see cref="Handler"/> starts the round). Gates the networked own-role stat: pre-round the server
    /// sends <see cref="SurvStatus.None"/>, standing in for QC's <c>STAT(GAMESTARTTIME)/STAT(ROUNDSTARTTIME)
    /// &gt; time</c> HUD hide (cl_survival.qc:60).
    /// </summary>
    public bool RoleAssigned => Handler?.IsRoundStarted ?? false;

    /// <summary>QC the SurvivalStatuses hunter bit (sv_survival.qc:31-37): <c>INGAME(e) &amp;&amp;
    /// e.survival_status == SURV_STATUS_HUNTER</c>. A DEAD hunter is still a hunter — the bitfield carries the
    /// role, not aliveness (the round-end scoreboard outs every hunter, dead or not).</summary>
    public bool IsHunter(Player p) => StatusOf(p) == SurvStatus.Hunter;

    /// <summary>
    /// QC <c>surv_isEliminated</c> (sv_survival.qc:228-235; same shape as ca_isEliminated) approximated like
    /// CA's: the port lacks INGAME_JOINING / FRAGS_PLAYER_OUT_OF_GAME, so eliminated = tracked-and-not-alive
    /// (<see cref="SurvState.Alive"/> goes false on death — no respawn in Survival — and resets at the next
    /// <see cref="AssignRoles"/>, matching QC where the dead stay out until the round resets).
    /// </summary>
    public bool IsEliminatedPlayer(Player p) => _states.TryGetValue(p, out SurvState? st) && !st.Alive;

    /// <summary>
    /// QC SurvivalStatuses_SendEntity's visibility rule (sv_survival.qc:19-20 + Surv_CheckWinner sendflags at
    /// lines 93/151): a HUNTER destination always receives the hunter set ("hunters know all hunters"); everyone
    /// else only receives it once the round is over (so the round-end scoreboard can out the hunters). The
    /// anti-cheat invariant: hunter identities must NEVER reach a prey/observer recipient mid-round.
    /// </summary>
    public bool DisclosesHuntersTo(Player viewer) => RoundOver || StatusOf(viewer) == SurvStatus.Hunter;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(surv, ForbidSpawn) + PutClientInServer (sv_survival.qc:307-333): the mid-round
    /// late-join lockout. While a round is live (QC <c>!allowed_to_spawn</c>) NO client may become a live player —
    /// an already-in-game player is forbidden to (re)spawn by ForbidSpawn, and a fresh observer is allowed past
    /// ForbidSpawn only to be immediately TRANSMUTE'd back to Observer by PutClientInServer. Either way the spawn
    /// outcome is identical: nobody enters the world mid-round; they sit out until the next round reset
    /// (reset_map_players transmutes the joining/in-game set back to Player). The port models that single net
    /// outcome by refusing the join entirely while the round is live — the refused client stays an observer, which
    /// is exactly the state Base leaves it in. <paramref name="roundStarted"/> stands in for QC's !allowed_to_spawn
    /// (a non-warmup round is in progress). Returns true to allow the join, false to forbid it (force-observe).
    /// </summary>
    public bool CanJoin(Player p, bool roundStarted)
    {
        // QC ForbidSpawn forbids INGAME players and PutClientInServer force-observes the rest, so the combined
        // mid-round outcome is "no client spawns": refuse every join while the round is live.
        if (roundStarted)
            return false; // FORBID: a round is in progress → force-observe until the next round
        return true; // ALLOW: pre-round / warmup → the joiner becomes a live player and gets a role at round start
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(surv, PutClientInServer) (sv_survival.qc:319-333): register a joiner with the
    /// gametype before its spawn loadout is applied (the host invokes this only on an ALLOWED join, i.e. pre-round
    /// or warmup — see <see cref="CanJoin"/>). The mid-round force-observe half of QC's PutClientInServer is handled
    /// by <see cref="CanJoin"/> refusing the join (the refused client never reaches this seed), so this only needs
    /// to track the joiner for the upcoming round. (The earlier attempt to set <c>IsObserver = true</c> here was a
    /// dead no-op: the server's ClientManager.Spawn runs right after and unconditionally clears it.)
    /// </summary>
    public void OnJoin(Player p)
    {
        if (p is not null)
            GetState(p); // track the joiner so the next AssignRoles includes them (QC reset_map_players)
    }

    /// <summary>QC the timed-out round end (Surv_CheckWinner timeout branch): survivors win, award scores.</summary>
    public void EndRoundTimedOut()
    {
        if (RoundOver) return;
        RoundOver = true;
        WinningSide = SurvStatus.Prey; // QC: if the match times out, survivors win
        UpdateScores(timedOut: true);
        Notifications?.RoundResult(SurvStatus.Prey); // QC CENTER/INFO_SURVIVAL_SURVIVOR_WIN broadcast
    }

    /// <summary>
    /// QC the survival AddPlayerScore hook: while a survival round is active, suppress the kills/deaths/suicides/
    /// damage columns so the scoreboard can't out the hidden hunters. Installed on <see cref="Activate"/> into
    /// the shared <see cref="GameScores.AddPlayerScoreHook"/>, removed on <see cref="Deactivate"/>.
    /// </summary>
    private static (bool allow, int delta, bool claimed) AnonymizeScore(Entity _, ScoreField field, int delta)
    {
        // QC MUTATOR_HOOKFUNCTION(surv, AddPlayerScore) only rewrites M_ARGV(1) and falls through (returns
        // false), so claimed=false: the hook never claims the write, leaving the game_stopped clamp intact.
        switch (field.Name)
        {
            case "KILLS": case "DEATHS": case "SUICIDES": case "DMG": case "DMGTAKEN":
                return (true, 0, false); // allow the call, but contribute nothing (anonymized)
            default:
                return (true, delta, false);
        }
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

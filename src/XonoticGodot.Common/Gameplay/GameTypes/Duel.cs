using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Duel (1v1) gametype — port of <c>CLASS(Duel, Gametype)</c>
/// (common/gametypes/gametype/duel/duel.qh + sv_duel.qc).
///
/// Duel is Deathmatch constrained to exactly two players: it reuses the DM frag matrix wholesale, the only
/// gametype-specific rules being the hard player limit of 2 (QC MUTATOR_HOOKFUNCTION(duel, GetPlayerLimit)
/// → 2) and the powerup filter (FilterItemDefinition → block powerups unless g_duel_with_powerups). The win
/// condition is the same frag/time limit as DM, but the gametype default is <c>pointlimit=0</c> (no frag
/// limit — duels are decided by the time limit by default), so it relies on the engine <c>fraglimit</c>
/// only when an admin sets one.
///
/// Faithfully ported (the duel-relevant slice):
///  - GetPlayerLimit → 2 (<see cref="PlayerLimit"/>): duel is always 1v1;
///  - the DM obituary/scoring matrix (enemy frag +1, suicide/world −1) reused verbatim from Deathmatch;
///  - frag-limit end-of-match latch (g_duel default pointlimit=0 ⇒ unlimited unless fraglimit set).
///
/// Faithfully ported (the duel-relevant slice, cont.):
///  - the powerup FilterItemDefinition hook (<see cref="OnFilterItemDefinition"/>, QC: block powerups unless
///    g_duel_with_powerups), subscribed live into MutatorHooks.FilterItemDefinition in <see cref="Activate"/>.
///
/// Cross-boundary enforcement (the limit is the const <see cref="PlayerLimit"/> = 2; the two QC GetPlayerLimit
/// enforcement points live in host files this class doesn't own, so they read the const directly):
///  - the human free-slot join gate (QC nJoinAllowed) — GameWorld.GametypeHasFreeSlot refuses a join once two
///    clients are already in the duel, wired into ClientManager.JoinAllowed via Clients.GametypeJoinGate;
///  - the bot-fill cap (QC bot_fixcount → GetPlayerLimit) — BotPopulation.FixCount feeds Duel.PlayerLimit (not
///    g_maxplayers) as the player limit, so bot fill stops at the 1v1 cap.
///
/// Deferred (NOTE — cross-boundary, recorded as cross-file todos):
///  - the 'playerA vs playerB' duel title (QC Announcer_Duel → CenterPrintPanel.SetDuelTitle): the panel side
///    exists but the m_1v1 countdown driver that calls it is a client/HUD concern;
///  - map-size support gating (m_isAlwaysSupported diameter &lt; 3250) — requires BSP world bounds not
///    available in the menu's file-only parsing layer; the m_isForcedSupported (DM→duel auto-list) part
///    IS implemented in MapInfoBackend.ApplyForcedGametypes + MapInfoCache.Get (duel.qh:15-28).
/// </summary>
[GameType]
public sealed class Duel : GameType
{
    // ----- frag/time limit cvars (gametype default pointlimit=0 ⇒ no frag limit; timelimit=10) -----
    private const string CvarFragLimitDuel = "g_duellimit"; // duel-specific override, if an admin sets one
    private const string CvarFragLimit     = "fraglimit";   // generic engine frag limit
    private const float  DefaultFragLimit  = 0f;            // QC m_legacydefaults / gametype_init: pointlimit=0

    // ----- timelimit cvar (gametype default timelimit=10 / generic default 20) -----
    private const string CvarTimeLimit      = "timelimit";
    private const float  DefaultTimeLimitMinutes = 10f;  // QC duel.qh gametype_init timelimit=10

    // ----- respawn delay cvars (shared with DM; xonotic-server.cfg g_respawn_delay_small/large = 2) -----
    private const string CvarRespawnDelaySmall = "g_respawn_delay_small";
    private const string CvarRespawnDelayLarge = "g_respawn_delay_large";
    private const float  DefaultRespawnDelay   = 2f;

    /// <summary>QC GetPlayerLimit → 2: duel is always exactly 1v1.</summary>
    public const int PlayerLimit = 2;

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _filterItemHandler;

    /// <summary>The pre-duel <c>g_spawn_avoid_los</c> value, restored on <see cref="Deactivate"/> (null = not saved).</summary>
    private string? _avoidLosPrior;

    /// <summary>Optional sink for the host/controller to react to a frag (e.g. schedule the respawn).</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a player reaches the frag limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The current frag leader (highest <see cref="Player.ScoreFrags"/>), or null before any frag.</summary>
    public Player? Leader { get; private set; }

    public Duel()
    {
        NetName = "duel";
        DisplayName = "Duel";
        TeamGame = false;
    }

    public override void OnInit()
    {
        // QC INIT(Duel): identity is set in the ctor; the player limit is the const PlayerLimit (2) and the
        // powerup filter is FilterItem. gametype_init flags (USEPOINTS|1V1) and map-size gating are
        // engine/map-pool concerns.
        //
        // QC duel.qh gametype_init applies "timelimit=10 pointlimit=0 leadlimit=0" at gametype registration.
        // The timelimit is a generic engine cvar (not a g_duel_* one), so we seed the gametype default here.
        // QC's _MapInfo_Map_ApplyGametypeEx (common/mapinfo.qc:551,572) FIRST resets timelimit to its compiled
        // defstring, THEN UNCONDITIONALLY applies the gametype default string's `timelimit=10` (cvar_set, no
        // guard), so a prior mode/vote/round's non-10 leftover is always reset to duel's 10. We mirror that
        // unconditional set (the per-map override path is the mapinfo `timelimit=` line, which QC appends after
        // the gametype default and which the port does not model here). (pointlimit=0 is enforced by FragLimit;
        // leadlimit=0 is not modeled but is N/A for 1v1 FFA.)
        SeedTimeLimit(DefaultTimeLimitMinutes);
    }

    /// <summary>
    /// Apply duel's gametype-default timelimit (gametype_init "timelimit=10"), UNCONDITIONALLY, mirroring QC's
    /// <c>_MapInfo_Map_ApplyGametypeEx</c> (common/mapinfo.qc:551 reset-to-defstring, then :572
    /// <c>cvar_set("timelimit", v)</c>): every gametype-select forces the gametype's default time limit, wiping
    /// any non-10 timelimit a prior mode, vote, or round left in place. (Matches the TeamMayhem precedent; the
    /// earlier guarded "only if still the generic default" variant would wrongly preserve a leftover non-default
    /// timelimit.) A host wanting a different limit re-applies it after select via the menu/vote, exactly as a
    /// mapinfo <c>timelimit=</c> override does in QC.
    /// </summary>
    private static void SeedTimeLimit(float minutes)
    {
        if (Api.Services is null)
            return;
        Api.Cvars.Set(CvarTimeLimit, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>QC FFA equality (server/scores.qc:537): the two duelists are tied on the primary score. Duel
    /// is timed (pointlimit=0), so a tie at the time limit is the canonical overtime trigger (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster) => FfaTie.TopTwoTied(roster);

    /// <summary>QC autocvar_g_duel_with_powerups: when set, powerups are allowed in duel (off by default).</summary>
    public bool WithPowerups => Api.Services is not null && Api.Cvars.GetFloat("g_duel_with_powerups") != 0f;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(duel, FilterItemDefinition) (sv_duel.qc:14): block powerup items from spawning
    /// unless g_duel_with_powerups is set. Returns true if the item should be FILTERED OUT (not spawned).
    /// Subscribed live via the MutatorHooks.FilterItemDefinition chain in <see cref="Activate"/> (matching how
    /// Mayhem/TeamMayhem register their item filters); the hook's <c>Definition</c> is the item entity, so the
    /// powerup test is by classname (the same stand-in Mayhem/NixMutator use — the item-class registry isn't
    /// fully ported).
    /// </summary>
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        string id = args.Definition.ClassName;
        // QC: if(definition.instanceOfPowerup) return !autocvar_g_duel_with_powerups;
        bool isPowerup = id is "item_strength" or "item_shield" or "item_invincible";
        if (isPowerup)
            return !WithPowerups;
        return false;
    }

    /// <summary>
    /// The frag limit currently in force (g_duellimit, else fraglimit, else default 0 = unlimited). 0 means
    /// "no frag limit" — duels are decided by the time limit unless an admin sets a fraglimit.
    /// </summary>
    public float FragLimit
    {
        get
        {
            if (TryCvar(CvarFragLimitDuel, out float dl)) return dl;
            if (TryCvar(CvarFragLimit, out float fl)) return fl;
            return DefaultFragLimit;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Leader = null;
        // QC ScoreRules_basics (Duel is 1v1 deathmatch, no extra columns): SP_SCORE is the primary sort/fraglimit
        // key. Re-pin it so switching from a column-mode reverts the scoreboard to sorting by score.
        Scoring.GameScores.ScoreRulesBasics(teams: false);
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score);
        // QC fragsleft_last reset: re-arm the "N frags left" announcer (duel's Scores_CountFragsRemaining hook).
        Scoring.GameScores.ResetFragsRemaining();
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTION(duel, FilterItemDefinition): keep powerups out of the item pool unless
        // g_duel_with_powerups is set. Subscribe the filter into the live hook chain the same way Mayhem does.
        _filterItemHandler = OnFilterItemDefinition;
        MutatorHooks.FilterItemDefinition.Add(_filterItemHandler);

        // [R1 preset] Disable the port's LOS spawn demotion for the duel (g_spawn_avoid_los, default ON
        // elsewhere): reading and controlling spawns is core 1v1 skill, and demoting enemy-visible spots
        // would rewrite that meta (planning/spawn-system-analysis-2026-07-06.md). Save/restore so a host's
        // global setting survives the mode switch — an admin who wants it ON in duel can still set it after
        // match start. Mirrors the SeedTimeLimit gametype-preset mechanism.
        if (Api.Services is not null)
        {
            _avoidLosPrior = Api.Cvars.GetString("g_spawn_avoid_los");
            Api.Cvars.Set("g_spawn_avoid_los", "0");
        }
    }

    public override void Deactivate()
    {
        // [R1 preset] Restore the host's pre-duel g_spawn_avoid_los so switching duel -> DM doesn't leave
        // the LOS demotion silently disabled.
        if (_avoidLosPrior is not null)
        {
            if (Api.Services is not null)
                Api.Cvars.Set("g_spawn_avoid_los", _avoidLosPrior);
            _avoidLosPrior = null;
        }
        if (_filterItemHandler is not null)
        {
            MutatorHooks.FilterItemDefinition.Remove(_filterItemHandler);
            _filterItemHandler = null;
        }
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>
    /// The DM obituary/scoring handler restricted to duel — identical matrix to Deathmatch (enemy frag +1,
    /// suicide/world death −1), since duel only differs from DM by the player limit and item filter.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        if (attacker is null || ReferenceEquals(attacker, victim))
            victim.ScoreFrags -= 1;       // SUICIDE / ACCIDENT: victim loses a frag
        else
            attacker.ScoreFrags += 1;     // ENEMY FRAG: attacker gains a frag (no teams in duel)

        ScheduleRespawn(victim);
        UpdateLeaderAndCheckLimit(attacker, victim);
        Events?.OnFrag(attacker, victim, ev.DeathType);
        return false;
    }

    /// <summary>QC calculate_respawntime reduced to 1v1: respawn_time = time + delay (small delay).</summary>
    public void ScheduleRespawn(Player victim)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        victim.RespawnTime = now + RespawnDelay;
    }

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
        Player candidate = attacker ?? victim;
        if (Leader is null || candidate.ScoreFrags > Leader.ScoreFrags)
            Leader = candidate;

        float limit = FragLimit;
        if (limit > 0f && Leader is not null && Leader.ScoreFrags >= limit)
            MatchEnded = true;
    }

    /// <summary>Authoritative leader + frag-limit pass over the (two-player) roster.</summary>
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
        if (limit > 0f && best is not null && best.ScoreFrags >= limit)
            MatchEnded = true;

        // QC MUTATOR_HOOKFUNCTION(duel, Scores_CountFragsRemaining) returns true: announce "N frags left" once
        // as the leader approaches the fraglimit (WinningCondition_Scores remaining-frags announcer, server/world.qc:1590-1622).
        int topScore = best is not null ? best.ScoreFrags : 0;
        int secondScore = second is not null ? second.ScoreFrags : 0;
        Scoring.GameScores.CountFragsRemaining(limit, 0f, topScore, secondScore, suddenDeathEnding: false);
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

// Port of common/gametypes/gametype/tmayhem/{tmayhem.qh, sv_tmayhem.qc}.
//
// Team Mayhem is the team variant of Mayhem (see Mayhem.cs): 2-4 teams compete for the most damage dealt +
// frags, scored by the SAME MayhemCalculatePlayerScore (the QC helper branches on `teamplay`; here we call
// MayhemScoring.Calculate with teamGame:true so the recomputed SP_SCORE delta also routes to the team total).
// Friendly fire is SAME_TEAM-aware (teammate damage subtracts from total_damage_dealt and never accrues
// positively) and mirror damage is zeroed. Structurally derived from Tdm.cs (TeamCount 2..4 from the
// teams-override / teams cvars, AddTeamScore into GameScores.AddToTeam, LeaderTeam + SecondTeam, the
// point/lead-limit check, RequestsTeamSpawns from g_tmayhem_team_spawns), with the Mayhem scoring hooks
// from Mayhem.cs (Damage_Calculate, PlayerDamage_SplitHealthArmor, PlayerRegen, SetStartItems,
// SetWeaponArena, FilterItemDefinition, ForbidThrowCurrentWeapon).

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Team Mayhem — port of <c>CLASS(tmayhem, Gametype)</c> + <c>sv_tmayhem.qc</c>. The team variant of
/// <see cref="Mayhem"/>; shares <see cref="MayhemScoring"/> (teamGame:true) and structurally mirrors
/// <see cref="Tdm"/>.
///
/// Faithfully ported:
///  - identity (tmayhem.qh ctor: NetName "tmayhem", DisplayName "Team Mayhem", TeamGame true, gametype_init
///    "timelimit=20 pointlimit=1500 teams=2 leadlimit=0");
///  - team count 2..4 (sv_tmayhem.qc tmayhem_DelayedInit: g_tmayhem_teams_override >= 2 ? override : teams);
///  - team spawns gated on g_tmayhem_team_spawns (default 0);
///  - the per-kill + per-damage score recompute via MayhemCalculatePlayerScore (teamplay branch), routing the
///    SP_SCORE delta to the player AND the team total (QC PlayerTeamScore_Add);
///  - the SAME_TEAM friendly-fire accrual + zeroed mirror damage (sv_tmayhem.qc Damage_Calculate / SplitHealthArmor);
///  - the point-limit + lead-limit end-of-match check;
///  - reset_map_players (clear GtTotalDamageDealt).
///
/// Deferred (NOTE — cross-boundary): score networking/HUD, skill-weighted team balance,
/// the tmayhem_team map-entity team colors, and warmup.
/// </summary>
[GameType]
public sealed class TeamMayhem : GameType
{
    /// <summary>The cvar prefix for this mode's cvars (the g_tmayhem_* reads / <see cref="MayhemScoring.GetConfig"/>).</summary>
    private const string Prefix = "g_tmayhem";

    // ----- point/lead limit cvars + gametype defaults (tmayhem.qh: pointlimit=1500 leadlimit=0) -----
    // g_tmayhem_point_limit default is -1 in gametypes-server.cfg = "use the mapinfo/gametype limit", so a -1
    // (or unset) falls back to the gametype_init default (1500). 0 = explicitly unlimited.
    private const string CvarPointLimit    = "g_tmayhem_point_limit";
    private const string CvarLeadLimit     = "g_tmayhem_point_leadlimit";
    private const float  DefaultPointLimit = 1500f; // tmayhem.qh gametype_init pointlimit=1500
    private const float  DefaultLeadLimit  = 0f;    // tmayhem.qh gametype_init leadlimit=0

    // ----- timelimit (tmayhem.qh gametype_init "timelimit=20"; the generic Cvars.cs default is also 20) -----
    private const string CvarTimeLimit          = "timelimit";
    private const float  DefaultTimeLimitMinutes = 20f; // tmayhem.qh gametype_init timelimit=20

    // ----- team count cvars (sv_tmayhem.qc: g_tmayhem_teams_override >= 2 ? override : g_tmayhem_teams), 2..4 -----
    private const string CvarTeamsOverride = "g_tmayhem_teams_override";
    private const string CvarTeams         = "g_tmayhem_teams";
    private const int    DefaultTeams      = 2;

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<GameHooks.PlayerDamageArgs>? _splitHandler;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _regenHandler;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _startItemsHandler;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _weaponArenaHandler;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _filterItemHandler;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _forbidThrowHandler;

    /// <summary>QC checkrules end-of-match latch: true once a team reaches the point/lead limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The leading team's color code (the GameScores ST_SCORE leader), or <see cref="Teams.None"/>.</summary>
    public int LeaderTeam { get; private set; }

    public TeamMayhem()
    {
        NetName = "tmayhem";
        DisplayName = "Team Mayhem";
        TeamGame = true;
    }

    public override void OnInit()
    {
        // QC INIT(tmayhem): identity (TeamGame) is set in the ctor; team count is read on demand (TeamCount).
        // GameRules_teams(true) / GameRules_spawning_teams + tmayhem_team map-entity colors are engine-side.
        //
        // QC tmayhem.qh gametype_init applies "timelimit=20 pointlimit=1500 teams=2 leadlimit=0" at gametype
        // registration. The point/lead limits are read on demand (PointLimit/LeadLimit fall back to 1500/0), but
        // the timelimit is a generic engine cvar, so we seed the gametype default here exactly as Tdm/Mayhem do.
        SeedTimeLimit(DefaultTimeLimitMinutes);
    }

    /// <summary>
    /// Apply tmayhem's gametype-default timelimit (gametype_init "timelimit=20") if the host has not overridden it.
    /// QC's _MapInfo_Map_ApplyGametypeEx resets timelimit to its defstring then applies the gametype default string;
    /// the port's generic <c>timelimit</c> default is itself 20 (== tmayhem's default), so for stock play this is a
    /// no-op. We only seed when the live value still equals the generic default — an explicit host/server.cfg
    /// timelimit (including 0 = "no time limit") is a deliberate choice that wins, matching the guarded Tdm/Mayhem
    /// sibling pattern (Tdm.SeedTimeLimit).
    /// </summary>
    private static void SeedTimeLimit(float minutes)
    {
        if (Api.Services is null)
            return;
        const float genericDefault = 20f; // Cvars.cs generic timelimit default
        if (Api.Cvars.GetFloat(CvarTimeLimit) == genericDefault)
            Api.Cvars.Set(CvarTimeLimit, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// QC sv_tmayhem.qc: <c>GameRules_spawning_teams(autocvar_g_tmayhem_team_spawns)</c> — gates team spawns on
    /// g_tmayhem_team_spawns (stock default 0, so TMayhem does NOT use team spawnpoints by default).
    /// </summary>
    public override bool RequestsTeamSpawns => TryCvar("g_tmayhem_team_spawns", out float v) ? v != 0f : false;

    /// <summary>The resolved scoring config (weights + spawn HP/armor divisor) for the team cvars.</summary>
    public MayhemScoring.Config Scoring => MayhemScoring.GetConfig(Prefix);

    /// <summary>Number of teams in play (g_tmayhem_teams_override if &gt;= 2, else g_tmayhem_teams), clamped 2..4.</summary>
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

    /// <summary>The point limit in force (g_tmayhem_point_limit, else the gametype default 1500). -1/unset =
    /// "use the gametype default"; 0 = explicitly unlimited.</summary>
    public float PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimit, out float pl) && pl >= 0f) return pl;
            return DefaultPointLimit;
        }
    }

    /// <summary>The lead limit in force (g_tmayhem_point_leadlimit, else 0). -1/unset → default 0 (no lead limit).</summary>
    public float LeadLimit
    {
        get
        {
            if (TryCvar(CvarLeadLimit, out float ll) && ll >= 0f) return ll;
            return DefaultLeadLimit;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return; // idempotent
        MatchEnded = false;
        LeaderTeam = Teams.None;

        GameScores.ResetTeams(); // QC Score_ClearAll at match start: zero the team slots before declaring
        // QC ScoreRules_basics (tmayhem declares no extra columns): SP_SCORE is the player primary, ST_SCORE the
        // team primary; teamkills shown for a team game. MayhemCalculatePlayerScore reads kills/teamkills/suicides
        // (maintained by Scores.Obituary) — those are part of ScoreRules_basics.
        GameScores.ScoreRulesBasics(teams: true);
        GameScores.TeamRulesBasics(scorePrio: ScoreFlags.SortPrioPrimary); // ST_SCORE is the team primary
        GameScores.SetSortKeys(GameScores.Score);
        GameScores.SeedTeams(TeamCount);

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        _damageCalcHandler = OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_damageCalcHandler);

        _splitHandler = OnSplitHealthArmor;
        GameHooks.PlayerDamageSplitHealthArmor.Add(_splitHandler);

        _regenHandler = OnPlayerRegen;
        MutatorHooks.PlayerRegen.Add(_regenHandler);

        _startItemsHandler = OnSetStartItems;
        MutatorHooks.SetStartItems.Add(_startItemsHandler);

        _weaponArenaHandler = OnSetWeaponArena;
        MutatorHooks.SetWeaponArena.Add(_weaponArenaHandler);

        _filterItemHandler = OnFilterItemDefinition;
        MutatorHooks.FilterItemDefinition.Add(_filterItemHandler);

        _forbidThrowHandler = OnForbidThrowCurrentWeapon;
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_forbidThrowHandler);
    }

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;

        if (_damageCalcHandler is not null) { MutatorHooks.DamageCalculate.Remove(_damageCalcHandler); _damageCalcHandler = null; }
        if (_splitHandler is not null) { GameHooks.PlayerDamageSplitHealthArmor.Remove(_splitHandler); _splitHandler = null; }
        if (_regenHandler is not null) { MutatorHooks.PlayerRegen.Remove(_regenHandler); _regenHandler = null; }
        if (_startItemsHandler is not null) { MutatorHooks.SetStartItems.Remove(_startItemsHandler); _startItemsHandler = null; }
        if (_weaponArenaHandler is not null) { MutatorHooks.SetWeaponArena.Remove(_weaponArenaHandler); _weaponArenaHandler = null; }
        if (_filterItemHandler is not null) { MutatorHooks.FilterItemDefinition.Remove(_filterItemHandler); _filterItemHandler = null; }
        if (_forbidThrowHandler is not null) { MutatorHooks.ForbidThrowCurrentWeapon.Remove(_forbidThrowHandler); _forbidThrowHandler = null; }
    }

    /// <summary>
    /// Assign <paramref name="joiner"/> to the smallest active team (QC TeamBalance_JoinBestTeam). Returns the
    /// chosen team color code, also written to <see cref="Entity.Team"/>.
    /// </summary>
    public int AssignTeam(Player joiner, IReadOnlyList<Player> roster)
        => TeamBalance.JoinSmallestTeam(joiner, roster, TeamCount);

    /// <summary>
    /// QC tmayhem reset_map_players (sv_tmayhem.qc:217): zero <c>GtTotalDamageDealt</c> for every player on a
    /// round/map reset. Public so the host (<c>GameWorld.ResetMap</c>) can call it.
    /// </summary>
    public void ResetMapPlayers(IReadOnlyList<Player> roster)
    {
        for (int i = 0; i < roster.Count; i++)
            roster[i].GtTotalDamageDealt = 0f;
    }

    /// <summary>QC <c>TeamScore_AddToTeam(player, ST_SCORE, delta)</c>'s team side (used by tests + the host).</summary>
    public void AddTeamScore(float team, int delta)
    {
        int t = (int)team;
        if (t == Teams.None) return;
        GameScores.AddToTeam(t, GameScores.TeamSlotScore, delta);
    }

    /// <summary>Read a team's running ST_SCORE total (0 if the team has never scored).</summary>
    public int GetTeamScore(int team) => GameScores.TeamScore(team, GameScores.TeamSlotScore);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on team points (ST_SCORE),
    /// so a tied timed Team Mayhem enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(GameScores.LeaderTeam(), GameScores.SecondTeam(), GetTeamScore);

    /// <summary>
    /// The per-kill driver — mirrors QC's GiveFragsForKill effect (sv_tmayhem.qc:207) fused with the obituary.
    /// The DM frag matrix is NOT applied (Team Mayhem scores from damage+frags); the kills/teamkills/suicides
    /// aux columns are maintained by Scores.Obituary on this same Combat.Death bus (it subscribes in Boot, this
    /// in Activate, so they are current). We recompute the attacker's score (enemy/teamkill/self) and the
    /// victim's on a world death; the recompute routes the SP_SCORE delta to the team total (teamGame:true).
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;
        MayhemScoring.Config cfg = Scoring;

        if (attacker is not null && !ReferenceEquals(attacker, victim))
        {
            // ENEMY FRAG or TEAMKILL: recompute the attacker's score (kills/teamkills already ticked in Obituary).
            MayhemScoring.Calculate(attacker, cfg, teamGame: true);
        }
        else
        {
            // SUICIDE / world death: recompute the victim's score.
            MayhemScoring.Calculate(victim, cfg, teamGame: true);
        }

        ScheduleRespawn(victim);
        UpdateLeaderAndCheckLimit();
        return false; // allow other Death subscribers (stats, mutators) to run
    }

    // =====================================================================================
    //  mutator-style hooks (subscribed in Activate, removed in Deactivate)
    // =====================================================================================

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(tmayhem, Damage_Calculate) (sv_tmayhem.qc:128): nullify self-damage when
    /// g_tmayhem_selfdamage == 0 and target == attacker, ALWAYS nullify DEATH_FALL (live player target only),
    /// and ALWAYS zero mirror damage (no mirror damaging in Team Mayhem).
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity target = args.Target;
        if ((target.Flags & EntFlags.Client) != 0 && target.DeadState == DeadFlag.No && !target.IsCorpse)
        {
            bool selfDamage = SelfDamageEnabled;
            bool isSelf = args.Attacker is not null && ReferenceEquals(args.Attacker, target);
            bool isFall = DeathTypes.BaseOf(args.DeathType) == DeathTypes.Fall;
            if ((!selfDamage && isSelf) || isFall)
                args.Damage = 0f;
        }

        args.MirrorDamage = 0f; // QC: frag_mirrordamage = 0 — no mirror damaging
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tmayhem, PlayerDamage_SplitHealthArmor): SAME_TEAM-aware damage accrual.</summary>
    private bool OnSplitHealthArmor(ref GameHooks.PlayerDamageArgs args)
    {
        MayhemScoring.AccrueSplitHealthArmor(ref args, Scoring, teamGame: true);
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tmayhem, PlayerRegen): disable regen/rot per g_tmayhem_regenerate / _rot.</summary>
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args)
    {
        bool regenerate = Cvar(Prefix + "_regenerate", 0f) != 0f;
        bool rot = Cvar(Prefix + "_rot", 0f) != 0f;
        return !regenerate && !rot;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tmayhem, SetStartItems): 200/200 + ammo + unlimited-ammo flag.</summary>
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        // Item-flag tags match the UPPERCASE convention used by InstagibMutator/OverkillMutator.
        l.ItemFlags.Remove("UNLIMITED_AMMO");
        l.ItemFlags.Remove("UNLIMITED_SUPERWEAPONS");
        bool useAmmo = Cvar("g_use_ammunition", 1f) != 0f;
        if (!useAmmo || Cvar(Prefix + "_unlimited_ammo", 0f) != 0f)
            l.ItemFlags.Add("UNLIMITED_AMMO");

        l.Health      = Cvar(Prefix + "_start_health", 200f);
        l.Armor       = Cvar(Prefix + "_start_armor", 200f);
        l.AmmoShells  = Cvar(Prefix + "_start_ammo_shells", 60f);
        l.AmmoBullets = Cvar(Prefix + "_start_ammo_nails", 320f);
        l.AmmoRockets = Cvar(Prefix + "_start_ammo_rockets", 160f);
        l.AmmoCells   = Cvar(Prefix + "_start_ammo_cells", 180f);
        l.AmmoFuel    = Cvar(Prefix + "_start_ammo_fuel", 0f);
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tmayhem, SetWeaponArena): default to g_tmayhem_weaponarena ("most_available").</summary>
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        if (args.Arena == "0" || string.IsNullOrEmpty(args.Arena))
            args.Arena = CvarString(Prefix + "_weaponarena", "most_available");
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tmayhem, FilterItem): the powerup + pickup_items logic (same as Mayhem).</summary>
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        string id = args.Definition.ClassName;
        bool isPowerup = id is "item_strength" or "item_shield" or "item_invincible";
        bool isAmmo = id is "item_shells" or "item_bullets" or "item_nails" or "item_rockets"
                      or "item_cells" or "item_fuel";
        bool isWeaponPickup = id.StartsWith("weapon_", System.StringComparison.Ordinal);

        float gPowerups = Cvar("g_powerups", -1f);
        float gPickupItems = Cvar("g_pickup_items", -1f);
        bool modePowerups = Cvar(Prefix + "_powerups", 1f) != 0f;
        bool modePickupItems = Cvar(Prefix + "_pickup_items", 0f) != 0f;
        bool removeWeaponsAndAmmo = Cvar(Prefix + "_pickup_items_remove_weapons_and_ammo", 1f) != 0f;

        if (gPowerups == 1f || (gPowerups == -1f && modePowerups))
            if (isPowerup) return false;
        if (gPowerups == 0f || !modePowerups)
            if (isPowerup) return true;
        if (gPickupItems == 0f)
            return true;
        if (modePickupItems && removeWeaponsAndAmmo && gPickupItems <= 0f)
            if (isAmmo || isWeaponPickup) return true;
        if (gPickupItems == -1f && !modePickupItems)
            return true;
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(tmayhem, ForbidThrowCurrentWeapon): always forbid throwing.</summary>
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;

    // =====================================================================================
    //  respawn + leader/limit
    // =====================================================================================

    /// <summary>QC autocvar_g_tmayhem_selfdamage: when 0, self-damage is nullified (Damage_Calculate).</summary>
    private bool SelfDamageEnabled => Cvar(Prefix + "_selfdamage", 0f) != 0f;

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
    /// Recompute the leading team and apply the win conditions (QC checkrules): a team reaching the point limit,
    /// OR leading the runner-up by at least the lead limit, ends the match. Reads the flag-aware ST_SCORE leader
    /// from GameScores (the source of truth), like Tdm.
    /// </summary>
    public void UpdateLeaderAndCheckLimit()
    {
        int bestTeam = GameScores.LeaderTeam();
        LeaderTeam = bestTeam;
        if (bestTeam == Teams.None)
            return;

        int bestScore = GetTeamScore(bestTeam);
        float pointLimit = PointLimit;
        if (pointLimit > 0f && bestScore >= pointLimit)
            MatchEnded = true;

        int secondTeam = GameScores.SecondTeam();
        float leadLimit = LeadLimit;
        if (leadLimit > 0f && secondTeam != Teams.None && (bestScore - GetTeamScore(secondTeam)) >= leadLimit)
            MatchEnded = true;

        // QC MUTATOR_HOOKFUNCTION(tmayhem, Scores_CountFragsRemaining) is commented out (disabled): Team Mayhem's
        // upscaled score (damage-weight + frag-weight, often 1000+ limit) doesn't play well with per-frag
        // announcements; a single shot (~40-80 dmg = 2-3 score) would trigger "2/3 frags left" as the match ends,
        // leaving no time for the cue to process. The port matches this by not calling CountFragsRemaining.
    }

    // ----- cvar helpers (the gametype TryCvar idiom) ---------------------------------------------------

    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null) return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;

    private static string CvarString(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }
}

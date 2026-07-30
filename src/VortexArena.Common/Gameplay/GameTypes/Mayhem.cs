// Port of common/gametypes/gametype/mayhem/{mayhem.qh, sv_mayhem.qc} (+ the shared scoring used by tmayhem).
//
// Mayhem is a chaotic FFA mode scored on a blend of DAMAGE DEALT and FRAGS rather than the plain DM ±1 frag
// matrix: a kill is worth `kill_weight` frags and dealing damage equal to your spawn health+armor is worth
// `damage_weight` frags, both upscaled by `upscaler` (defaults 0.25 / 0.75 / 20). The per-player running
// damage total (Entity.GtTotalDamageDealt, QC .total_damage_dealt) is accrued in the
// PlayerDamage_SplitHealthArmor hook, and the full score is recomputed (MayhemCalculatePlayerScore) on every
// damage event and every kill. Structurally this mirrors Duel.cs (a standalone DM-derivative with its own
// GameType attribute / Activate / Deactivate / OnDeath, MatchEnded + Leader, the TryCvar idiom) — Mayhem is
// NOT a Deathmatch subclass and NOT routed through MatchController.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared Mayhem / Team Mayhem score math — the Godot-free port of <c>MayhemCalculatePlayerScore</c>
/// (sv_mayhem.qc:145) and the <c>PlayerDamage_SplitHealthArmor</c> total-damage accrual (sv_mayhem.qc:281 /
/// sv_tmayhem.qc:147). Both modes recompute a player's SP_SCORE from their <c>GtTotalDamageDealt</c> +
/// kills/teamkills/suicides on every relevant event; this static helper hosts that math so the FFA and team
/// modes share it verbatim (QC's single <c>MayhemCalculatePlayerScore</c> branches on <c>teamplay</c>).
/// </summary>
public static class MayhemScoring
{
    // ----- scoring cvars (gametypes-server.cfg defaults) ------------------------------------------------
    // FFA (g_mayhem_*) and team (g_tmayhem_*) carry identical stock values; the divisor (spawn health+armor)
    // is the mode start cvars (g_*_start_health + g_*_start_armor, defaults 200+200 = 400). Read via TryCvar
    // so an explicit 0 is distinct from "unset" (a 0 weight switches the scoring method, exactly like QC).
    private const float DefUpscaler     = 20f;   // g_*_scoring_upscaler
    private const float DefKillWeight   = 0.25f; // g_*_scoring_kill_weight
    private const float DefDamageWeight = 0.75f; // g_*_scoring_damage_weight
    private const float DefStartHealth  = 200f;  // g_*_start_health
    private const float DefStartArmor   = 200f;  // g_*_start_armor

    /// <summary>The scoring weights/divisor for a mode (the resolved cvar values).</summary>
    public readonly struct Config
    {
        public readonly float Upscaler;
        public readonly float KillWeight;
        public readonly float DamageWeight;
        public readonly bool DisableSelfDamage2Score;
        /// <summary>The divisor (QC <c>start_health + start_armorvalue</c>) — the mode's spawn HP+armor.</summary>
        public readonly float SpawnHealthArmor;

        public Config(float upscaler, float killWeight, float damageWeight, bool disableSelfDamage2Score,
            float spawnHealthArmor)
        {
            Upscaler = upscaler;
            KillWeight = killWeight;
            DamageWeight = damageWeight;
            DisableSelfDamage2Score = disableSelfDamage2Score;
            SpawnHealthArmor = spawnHealthArmor;
        }
    }

    /// <summary>
    /// Resolve a mode's scoring config from its cvar prefix (<c>g_mayhem</c> / <c>g_tmayhem</c>). QC reads the
    /// FFA cvars for Mayhem and the team cvars for Team Mayhem (sv_mayhem.qc:158 teamplay branch).
    /// </summary>
    public static Config GetConfig(string prefix)
    {
        float up    = TryCvar(prefix + "_scoring_upscaler", out float u) ? u : DefUpscaler;
        float kw    = TryCvar(prefix + "_scoring_kill_weight", out float k) ? k : DefKillWeight;
        float dw    = TryCvar(prefix + "_scoring_damage_weight", out float d) ? d : DefDamageWeight;
        bool  dsd   = TryCvar(prefix + "_scoring_disable_selfdamage2score", out float s) && s != 0f;
        float hp    = TryCvar(prefix + "_start_health", out float h) ? h : DefStartHealth;
        float armor = TryCvar(prefix + "_start_armor", out float a) ? a : DefStartArmor;
        return new Config(up, kw, dw, dsd, hp + armor);
    }

    /// <summary>QC <c>rint(f)</c> (Darkplaces prvm_cmds.c VM_rint): round half AWAY from zero.</summary>
    public static float Rint(float f) => f > 0f ? MathF.Floor(f + 0.5f) : MathF.Ceiling(f - 0.5f);

    /// <summary>
    /// Port of <c>MayhemCalculatePlayerScore(scorer)</c> (sv_mayhem.qc:145): recompute how much SP_SCORE the
    /// player SHOULD have from their <c>GtTotalDamageDealt</c> and kills/teamkills/suicides, and apply the
    /// difference via <c>GameRules_scoring_add_team(scorer, SCORE, …)</c>. Branches on the three scoring
    /// methods (1 = both weights, 2 = frags only, 3 = damage only) and guards the divide-by-zero (both weights
    /// 0 → no scoring). The ×100 / ÷100 integer-scaling is ported verbatim to match the QC scoreboard exactly.
    /// <paramref name="teamGame"/> selects whether the SCORE delta also routes to the scorer's team total
    /// (QC PlayerTeamScore_Add: in FFA the team is None so the team add no-ops).
    /// </summary>
    public static void Calculate(Player scorer, in Config cfg, bool teamGame)
    {
        float upscaler = cfg.Upscaler;
        float fragWeight = cfg.KillWeight;
        float damageWeight = cfg.DamageWeight;
        bool disableSelfDamage2Score = cfg.DisableSelfDamage2Score;

        // QC: pick the scoring method and avoid divide-by-0.
        int scoringMethod;
        if (fragWeight != 0f && damageWeight != 0f) scoringMethod = 1;
        else if (fragWeight != 0f) scoringMethod = 2;
        else if (damageWeight != 0f) scoringMethod = 3;
        else return; // neither frags nor damage give score

        float divisor = cfg.SpawnHealthArmor;
        // QC divides total_damage_dealt by (start_health + start_armorvalue); guard a 0 divisor defensively
        // (QC relies on the mode always setting a positive start HP+armor via SetStartItems).
        if (divisor <= 0f) divisor = DefStartHealth + DefStartArmor;

        int spKills = GameScores.Get(scorer, GameScores.Kills);
        int spTeamKills = GameScores.Get(scorer, GameScores.TeamKills);
        int spSuicides = GameScores.Get(scorer, GameScores.Suicides);
        int spScore = scorer.ScoreFrags; // SP_SCORE

        switch (scoringMethod)
        {
            default:
            case 1:
            {
                // A different (harsher) suicide weight when method 1 lacks selfdamage2score, to avoid exploiting.
                float suicideWeight = 1f + (disableSelfDamage2Score ? 1f : 0f) / fragWeight;

                float playerDamageScore = ((scorer.GtTotalDamageDealt / divisor) * 100f) * upscaler * damageWeight;
                float roundedPlayerDamageScore = Rint(playerDamageScore * 10f) / 10f;

                float killCount = spKills - spTeamKills - (spSuicides * suicideWeight);
                float playerKillScore = (killCount * 100f) * upscaler * fragWeight;

                float playerScore = roundedPlayerDamageScore + playerKillScore;
                float scoreToAdd = playerScore - (spScore * 100f);
                ScoringAddTeam(scorer, (int)MathF.Floor(scoreToAdd / 100f), teamGame);
                return;
            }

            case 2:
            {
                float playerKillScore = spKills - spTeamKills - spSuicides;
                float upscaledPlayerScore = playerKillScore * upscaler;
                float scoreToAdd = upscaledPlayerScore - spScore;
                ScoringAddTeam(scorer, (int)MathF.Floor(scoreToAdd), teamGame);
                return;
            }

            case 3:
            {
                float playerDamageScore = (scorer.GtTotalDamageDealt / divisor) * 100f;
                float roundedPlayerDamageScore = Rint(playerDamageScore * 10f) / 10f;
                float upscaledPlayerScore = roundedPlayerDamageScore * upscaler;
                float scoreToAdd = upscaledPlayerScore - (spScore * 100f);
                ScoringAddTeam(scorer, (int)MathF.Floor(scoreToAdd / 100f), teamGame);
                return;
            }
        }
    }

    /// <summary>
    /// QC <c>GameRules_scoring_add_team(client, SCORE, delta)</c> = <c>PlayerTeamScore_Add(client, SP_SCORE,
    /// ST_SCORE, delta)</c>: always add to the player's SP_SCORE column, and (team games only) also add to the
    /// player's team's ST_SCORE total. In FFA the scorer team is None so the team add no-ops (QC's behavior).
    /// </summary>
    private static void ScoringAddTeam(Player scorer, int delta, bool teamGame)
    {
        if (delta == 0) return;
        GameScores.AddToPlayer(scorer, GameScores.Score, delta);
        if (teamGame)
            GameScores.AddToTeam((int)scorer.Team, GameScores.TeamSlotScore, delta);
    }

    /// <summary>
    /// Port of the <c>PlayerDamage_SplitHealthArmor</c> total-damage accrual (sv_mayhem.qc:281 /
    /// sv_tmayhem.qc:147): compute the "useful" damage (frag_damage minus overkill excess), accrue it on the
    /// right scorer (the attacker for an enemy hit, minus for self/teammate, the victim for an environmental
    /// suicide), then recompute that scorer's score. <paramref name="teamGame"/> selects the team
    /// friendly-fire rule (SAME_TEAM accrues nothing positive). Mirrors QC's early-return when the damage
    /// weight is 0 and the spawn-shield handling. No-op (returns) when there is no useful damage.
    /// </summary>
    public static void AccrueSplitHealthArmor(ref GameHooks.PlayerDamageArgs args, in Config cfg, bool teamGame)
    {
        if (cfg.DamageWeight == 0f) return; // QC: if (!autocvar_g_*_scoring_damage_weight) return;

        Entity fragTarget = args.Target;
        if (!IsPlayer(fragTarget)) return; // only player victims accrue damage score

        float shieldBlock = Cvar("g_spawnshield_blockdamage", 1f);
        // QC: if (SpawnShield active && g_spawnshield_blockdamage >= 1) return;
        if (HasSpawnShield(fragTarget) && shieldBlock >= 1f) return;

        Entity? fragAttacker = args.FragAttacker; // the TRUE attacker (null = world/environment)
        string deathType = args.FragDeathType;
        float fragDamage = args.FragDamage;
        // QC: damage_take = bound(0, M_ARGV(4), GetResource(target, RES_HEALTH));
        //     damage_save = bound(0, M_ARGV(5), GetResource(target, RES_ARMOR));
        float damageTake = Bound(0f, args.DamageTake, fragTarget.GetResource(ResourceType.Health));
        float damageSave = Bound(0f, args.DamageSave, fragTarget.GetResource(ResourceType.Armor));
        float excess = MathF.Max(0f, fragDamage - damageTake - damageSave);
        float total = fragDamage - excess;

        if (total == 0f) return;

        // QC: if (SpawnShield active && g_spawnshield_blockdamage) total *= 1 - blockdamage;
        if (HasSpawnShield(fragTarget) && shieldBlock != 0f)
            total *= 1f - shieldBlock;

        Player? scorer; // the entity whose score needs updating

        if (fragAttacker is Player attacker)
        {
            if (!teamGame)
            {
                // FFA (sv_mayhem.qc:304): enemy hit accrues; self damage subtracts (unless disabled).
                if (!ReferenceEquals(fragTarget, attacker))
                    attacker.GtTotalDamageDealt += total;
                if (ReferenceEquals(fragTarget, attacker) && !cfg.DisableSelfDamage2Score)
                    attacker.GtTotalDamageDealt -= total;
            }
            else
            {
                // TEAM (sv_tmayhem.qc:170): only non-SAME_TEAM accrues; SAME_TEAM or self subtracts.
                bool sameTeam = Teams.SameTeam(fragTarget, attacker);
                if (!sameTeam)
                    attacker.GtTotalDamageDealt += total;
                if (sameTeam || (ReferenceEquals(fragTarget, attacker) && !cfg.DisableSelfDamage2Score))
                    attacker.GtTotalDamageDealt -= total;
            }
            scorer = attacker;
        }
        else
        {
            // World/environmental death: subtract for the punishable environmental deathtypes (kill, drown,
            // hurttrigger/void, lava, slime, swamp). QC also lists DEATH_CAMP (campcheck) — the port has no
            // camp deathtype constant yet, so it is a documented omission (no campcheck death is produced).
            if (!cfg.DisableSelfDamage2Score && IsEnvironmentalSuicide(deathType) && fragTarget is Player vp)
                vp.GtTotalDamageDealt -= total;
            scorer = fragTarget as Player;
        }

        if (scorer is not null)
            Calculate(scorer, cfg, teamGame);
    }

    /// <summary>
    /// QC the environmental-suicide deathtype set in the Mayhem damage-accrual frag handler (sv_mayhem.qc:325-332):
    /// DEATH_KILL / DROWN / HURTTRIGGER / CAMP / LAVA / SLIME / SWAMP — these self-damage deaths are subtracted back
    /// out of the victim's total_damage_dealt. DEATH_HURTTRIGGER maps to the port's <c>void</c> tag; <c>camp</c> is
    /// the campcheck mutator's deathtype (now live via <see cref="DeathTypes.Camp"/> / CampcheckMutator).
    /// </summary>
    private static bool IsEnvironmentalSuicide(string? deathType)
    {
        string b = DeathTypes.BaseOf(deathType);
        return b == DeathTypes.Kill
            || b == DeathTypes.Drown
            || b == DeathTypes.Void   // QC DEATH_HURTTRIGGER
            || b == DeathTypes.Camp   // QC DEATH_CAMP (campcheck mutator)
            || b == DeathTypes.Lava
            || b == DeathTypes.Slime
            || b == DeathTypes.Swamp;
    }

    // ----- small helpers (mirror the gametype TryCvar / DamageSystem predicates) ------------------------

    private static bool IsPlayer(Entity e) => (e.Flags & EntFlags.Client) != 0 && !e.IsCorpse;

    /// <summary>QC <c>StatusEffects_active(STATUSEFFECT_SpawnShield, e)</c> (DamageSystem keeps this on the entity).</summary>
    private static bool HasSpawnShield(Entity e)
        => e.SpawnShieldExpire > (Api.Services is null ? 0f : Api.Clock.Time);

    private static float Bound(float lo, float v, float hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;

    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null) return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }
}

/// <summary>
/// The Mayhem (FFA) gametype — port of <c>CLASS(mayhem, Gametype)</c> + <c>sv_mayhem.qc</c>. A chaotic FFA
/// mode scored on damage dealt + frags (see <see cref="MayhemScoring"/>), structurally derived from
/// <see cref="Duel"/> (standalone, own Combat.Death handler, MatchEnded + Leader, NOT a Deathmatch subclass,
/// NOT routed through <see cref="MatchController"/>).
///
/// Faithfully ported:
///  - identity (mayhem.qh ctor: NetName "mayhem", DisplayName "Mayhem", TeamGame false, gametype_init
///    "timelimit=15 pointlimit=1000 leadlimit=0");
///  - the score recompute on every kill (<see cref="OnDeath"/> → <see cref="MayhemScoring.Calculate"/>) — QC
///    mirrors this in both GiveFragsForKill (which zeroes the direct frag) and PlayerDamage_SplitHealthArmor;
///  - the mutator-style hooks Mayhem registers while active: Damage_Calculate (self-damage / DEATH_FALL
///    nullify), PlayerDamage_SplitHealthArmor (the damage accrual), PlayerRegen (regen/rot disable),
///    SetStartItems (200/200 + ammo), SetWeaponArena, FilterItemDefinition (powerup/item filter),
///    ForbidThrowCurrentWeapon;
///  - the point-limit + lead-limit end-of-match check;
///  - reset_map_players (<see cref="ResetMapPlayers"/>, called by the host on round/map reset): clear GtTotalDamageDealt.
///
/// Deferred (NOTE — cross-boundary): score networking/HUD and map-size support gating
/// (m_isAlwaysSupported / m_isForcedSupported — map-pool concern).
/// </summary>
[GameType]
public sealed class Mayhem : GameType
{
    /// <summary>The cvar prefix for this mode's cvars (<see cref="MayhemScoring.GetConfig"/> / the g_mayhem_* reads).</summary>
    private const string Prefix = "g_mayhem";

    // ----- point/lead limit cvars + gametype defaults (mayhem.qh: pointlimit=1000 leadlimit=0) -----
    // g_mayhem_point_limit default is -1 in gametypes-server.cfg = "use the mapinfo/gametype limit", so a -1
    // (or unset) falls back to the gametype_init default (1000). 0 = explicitly unlimited.
    private const string CvarPointLimit     = "g_mayhem_point_limit";
    private const string CvarLeadLimit      = "g_mayhem_point_leadlimit";
    private const float  DefaultPointLimit  = 1000f; // mayhem.qh gametype_init pointlimit=1000
    private const float  DefaultLeadLimit   = 0f;    // mayhem.qh gametype_init leadlimit=0

    // ----- timelimit (mayhem.qh gametype_init "timelimit=15"; the generic Cvars.cs default is 20) -----
    private const string CvarTimeLimit      = "timelimit";

    /// <summary>
    /// QC mayhem.qh <c>gametype_init</c> default time limit, in MINUTES (<c>"timelimit=15 …"</c>). Applied by the
    /// host (<c>GameWorld.ActivateGameType</c>) when Mayhem is selected and no explicit <c>timelimit</c> cvar was
    /// set, mirroring QC's GameType_SetTeamplay/gametype_init applying the gametype's default args. (mayhem.qh's
    /// <c>m_legacydefaults</c> lists 20 min, but the active <c>gametype_init</c> string is 15 — 15 is canonical.)
    /// </summary>
    public const float DefaultTimeLimitMinutes = 15f;

    // ----- respawn delay cvars (shared with DM; xonotic-server.cfg g_respawn_delay_small/large = 2) -----
    private const string CvarRespawnDelaySmall = "g_respawn_delay_small";
    private const string CvarRespawnDelayLarge = "g_respawn_delay_large";
    private const float  DefaultRespawnDelay   = 2f;

    private HookHandler<DeathEvent>? _deathHandler;
    private HookHandler<MutatorHooks.DamageCalculateArgs>? _damageCalcHandler;
    private HookHandler<GameHooks.PlayerDamageArgs>? _splitHandler;
    private HookHandler<MutatorHooks.PlayerRegenArgs>? _regenHandler;
    private HookHandler<MutatorHooks.SetStartItemsArgs>? _startItemsHandler;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _weaponArenaHandler;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _filterItemHandler;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _forbidThrowHandler;

    /// <summary>Optional sink for the host/controller to react to a frag (e.g. schedule the respawn).</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a player reaches the point/lead limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The current score leader (highest SP_SCORE), or null before any score.</summary>
    public Player? Leader { get; private set; }

    public Mayhem()
    {
        NetName = "mayhem";
        DisplayName = "Mayhem";
        TeamGame = false;
    }

    public override void OnInit()
    {
        // QC INIT(mayhem): identity is set in the ctor; the point/lead limits are read on demand
        // (PointLimit/LeadLimit fall back to 1000/0). gametype_init flags (USEPOINTS) and map-size gating are
        // engine/map-pool concerns.
        //
        // QC mayhem.qh gametype_init applies "timelimit=15 pointlimit=1000 leadlimit=0" at gametype registration.
        // The timelimit is a generic engine cvar (not a g_mayhem_* one), so we seed the gametype default here the
        // way Tdm/TeamMayhem do: a prior mode/admin could have left a non-15 timelimit, and selecting mayhem must
        // reset to its default of 15.
        SeedTimeLimit(DefaultTimeLimitMinutes);
    }

    /// <summary>
    /// Apply mayhem's gametype-default timelimit (gametype_init "timelimit=15") if the host has not overridden it.
    /// QC applies the default-limit string at gametype registration; the port's generic <c>timelimit</c> default is
    /// 20, so we only seed when the live value still equals that generic default — an explicit host/server.cfg
    /// timelimit (including 0 = "no time limit") is a deliberate choice that wins, matching QC where an admin-set
    /// cvar overrides the gametype default.
    /// </summary>
    private static void SeedTimeLimit(float minutes)
    {
        if (Api.Services is null)
            return;
        const float genericDefault = 20f; // Cvars.cs generic timelimit default
        if (Api.Cvars.GetFloat(CvarTimeLimit) == genericDefault)
            Api.Cvars.Set(CvarTimeLimit, minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>QC FFA equality (server/scores.qc:537): the top two players are tied on the primary score, so a
    /// tied timed Mayhem enters overtime instead of drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster) => FfaTie.TopTwoTied(roster);

    /// <summary>The resolved scoring config (weights + spawn HP/armor divisor) for the FFA cvars.</summary>
    public MayhemScoring.Config Scoring => MayhemScoring.GetConfig(Prefix);

    /// <summary>The point limit in force (g_mayhem_point_limit, else the gametype default 1000). -1/unset =
    /// "use the gametype default"; 0 = explicitly unlimited.</summary>
    public float PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimit, out float pl) && pl >= 0f) return pl;
            return DefaultPointLimit;
        }
    }

    /// <summary>The lead limit in force (g_mayhem_point_leadlimit, else 0). -1/unset → default 0 (no lead limit).</summary>
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
        Leader = null;

        // QC ScoreRules_basics (mayhem declares no extra columns): SP_SCORE is the primary sort/limit key. Mayhem
        // depends on kills/teamkills/suicides being maintained (Scores.Obituary) so MayhemCalculatePlayerScore can
        // read them — those columns are part of ScoreRules_basics. Re-pin SP_SCORE as the sort key.
        GameScores.ScoreRulesBasics(teams: false);
        GameScores.SetSortKeys(GameScores.Score);

        // NOTE: total_damage_dealt is zeroed via ResetMapPlayers, which the host (GameWorld.ResetMap) calls with
        // the roster on a round/map reset (QC mayhem reset_map_players). Activate has no roster handle, and a
        // freshly-spawned player's GtTotalDamageDealt already defaults to 0, so no extra zeroing is needed here.

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
    /// QC mayhem reset_map_players (sv_mayhem.qc:359): zero <c>GtTotalDamageDealt</c> for every player on a
    /// round/map reset. Public so the host (<c>GameWorld.ResetMap</c>) can call it; also run in Activate.
    /// </summary>
    public void ResetMapPlayers(IReadOnlyList<Player> roster)
    {
        for (int i = 0; i < roster.Count; i++)
            roster[i].GtTotalDamageDealt = 0f;
    }

    /// <summary>
    /// The per-kill driver — QC's GiveFragsForKill effect (sv_mayhem.qc:349: zero the direct frag, then
    /// MayhemCalculatePlayerScore on the attacker) fused with the obituary's victim handling. The DM ±1 frag
    /// matrix is NOT applied (Mayhem scores from damage+frags, not the frag matrix); the kills/suicides/
    /// teamkills aux columns are maintained by Scores.Obituary on this same Combat.Death bus (which runs BEFORE
    /// this handler — it subscribes in Boot, this in Activate), so they are current when we recompute. We then
    /// recompute the attacker's score (enemy/self) and the victim's on a world death.
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
            // ENEMY FRAG: QC GiveFragsForKill recomputes the attacker's score (the +1 frag is zeroed in QC).
            MayhemScoring.Calculate(attacker, cfg, teamGame: false);
        }
        else
        {
            // SUICIDE / world death: recompute the victim's score (their SP_SUICIDES just ticked in Obituary,
            // and any environmental-damage subtraction landed via the SplitHealthArmor hook).
            MayhemScoring.Calculate(victim, cfg, teamGame: false);
        }

        ScheduleRespawn(victim);
        UpdateLeaderAndCheckLimit();
        Events?.OnFrag(attacker, victim, ev.DeathType);
        return false; // allow other Death subscribers (stats, mutators) to run
    }

    // =====================================================================================
    //  mutator-style hooks (subscribed in Activate, removed in Deactivate) — Mayhem is NOT a MutatorBase
    // =====================================================================================

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(mayhem, Damage_Calculate) (sv_mayhem.qc:130): nullify self-damage when
    /// g_mayhem_selfdamage == 0 and target == attacker, and ALWAYS nullify DEATH_FALL — but only for a LIVE
    /// player target (so corpses can still be gibbed, even by delayed self-damage).
    /// </summary>
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        Entity target = args.Target;
        if ((target.Flags & EntFlags.Client) == 0) return false; // IS_PLAYER(frag_target)
        if (target.DeadState != DeadFlag.No || target.IsCorpse) return false; // !IS_DEAD(frag_target)

        bool selfDamage = SelfDamageEnabled;
        bool isSelf = args.Attacker is not null && ReferenceEquals(args.Attacker, target);
        bool isFall = DeathTypes.BaseOf(args.DeathType) == DeathTypes.Fall;

        if ((!selfDamage && isSelf) || isFall)
            args.Damage = 0f;

        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(mayhem, PlayerDamage_SplitHealthArmor): accrue total_damage_dealt + rescore.</summary>
    private bool OnSplitHealthArmor(ref GameHooks.PlayerDamageArgs args)
    {
        MayhemScoring.AccrueSplitHealthArmor(ref args, Scoring, teamGame: false);
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(mayhem, PlayerRegen) (sv_mayhem.qc:77): disable regen and/or rot per
    /// g_mayhem_regenerate / g_mayhem_rot. The port's PlayerRegen hook only models the "disable regen" return,
    /// so we return true (disable) when regen is off — matching QC's <c>return (!regenerate &amp;&amp; !rot)</c> in the
    /// common case (both default off → regen disabled).
    /// </summary>
    private bool OnPlayerRegen(ref MutatorHooks.PlayerRegenArgs args)
    {
        bool regenerate = Cvar(Prefix + "_regenerate", 0f) != 0f;
        bool rot = Cvar(Prefix + "_rot", 0f) != 0f;
        return !regenerate && !rot; // QC: returns true (regen fully disabled) when neither is set
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(mayhem, SetStartItems) (sv_mayhem.qc:62): set the spawn loadout to the mode's
    /// start health/armor/ammo (defaults 200/200/60/320/160/180/0) and the unlimited-ammo flag.
    /// </summary>
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        // QC: start_items &= ~(IT_UNLIMITED_AMMO|IT_UNLIMITED_SUPERWEAPONS); then set IT_UNLIMITED_AMMO when
        // g_use_ammunition is off OR g_mayhem_unlimited_ammo is set. (The item-flag tags match the UPPERCASE
        // convention the other SetStartItems handlers use — InstagibMutator/OverkillMutator.)
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

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(mayhem, SetWeaponArena) (sv_mayhem.qc:91): default the weapon arena to
    /// g_mayhem_weaponarena ("most_available") when unset / "0".
    /// </summary>
    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        if (args.Arena == "0" || string.IsNullOrEmpty(args.Arena))
            args.Arena = CvarString(Prefix + "_weaponarena", "most_available");
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(mayhem, FilterItem) (sv_mayhem.qc:97): the powerup + pickup_items logic. Returns
    /// true to FILTER OUT (not spawn) the item. The item-class registry isn't fully ported, so powerup/ammo/
    /// weapon are detected via the definition classname (the same stand-in NixMutator uses).
    /// </summary>
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

        // QC: enable powerups if forced on globally OR (global == -1 AND mode powerups on).
        if (gPowerups == 1f || (gPowerups == -1f && modePowerups))
            if (isPowerup) return false;
        // QC: disable powerups if forced off globally OR in this mode.
        if (gPowerups == 0f || !modePowerups)
            if (isPowerup) return true;
        // QC: remove all items if items are forced off globally.
        if (gPickupItems == 0f)
            return true;
        // QC: if items on in this mode but weapons/ammo removal requested AND global <= 0, strip ammo+weapons.
        if (modePickupItems && removeWeaponsAndAmmo && gPickupItems <= 0f)
            if (isAmmo || isWeaponPickup) return true;
        // QC: remove items if global is "follow mode" (-1) AND mode items off.
        if (gPickupItems == -1f && !modePickupItems)
            return true;
        return false;
    }

    /// <summary>QC MUTATOR_HOOKFUNCTION(mayhem, ForbidThrowCurrentWeapon) (sv_mayhem.qc:86): always forbid throwing.</summary>
    private bool OnForbidThrowCurrentWeapon(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;

    // =====================================================================================
    //  respawn + leader/limit
    // =====================================================================================

    /// <summary>QC autocvar_g_mayhem_selfdamage: when 0, self-damage is nullified (Damage_Calculate).</summary>
    private bool SelfDamageEnabled => Cvar(Prefix + "_selfdamage", 0f) != 0f;

    /// <summary>QC calculate_respawntime reduced: respawn_time = time + small delay (g_respawn_delay_small, 2s).</summary>
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

    private void UpdateLeaderAndCheckLimit()
    {
        // Authoritative-ish incremental update: the host also calls RecomputeLeader each frame.
        // (Mayhem's score is recomputed on the bus, so a full scan is the safe source of truth — but the
        // incremental leader here keeps MatchEnded responsive between RecomputeLeader passes.)
        Player? best = Leader;
        if (best is not null)
        {
            float limit = PointLimit;
            if (limit > 0f && best.ScoreFrags >= limit)
                MatchEnded = true;
        }
    }

    /// <summary>
    /// Authoritative leader + point/lead-limit pass over the roster (QC checkrules). The host calls this each
    /// tick; it scans SP_SCORE (the recomputed Mayhem score). Lead limit ends the match when the leader is
    /// ahead of the runner-up by at least the lead limit.
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

        float pointLimit = PointLimit;
        if (pointLimit > 0f && best is not null && best.ScoreFrags >= pointLimit)
            MatchEnded = true;

        float leadLimit = LeadLimit;
        if (leadLimit > 0f && best is not null && second is not null
            && (best.ScoreFrags - second.ScoreFrags) >= leadLimit)
            MatchEnded = true;

        // QC MUTATOR_HOOKFUNCTION(mayhem, Scores_CountFragsRemaining) is commented out (disabled): Mayhem's
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

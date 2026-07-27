using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// A resolved spawn location — the QC spawnpoint edict (info_player_deathmatch / info_player_start /
/// info_player_team*) reduced to what placement needs: an <see cref="Origin"/>, facing
/// <see cref="Angles"/>, and the owning <see cref="Team"/> (0 = no team / FFA). Kept as a small struct so
/// selection is allocation-free; the underlying world <see cref="Entity"/> is carried for callers that want
/// to mark it used or read more fields later.
/// </summary>
public readonly struct SpawnPoint
{
    public readonly Vector3 Origin;
    public readonly Vector3 Angles;
    public readonly float Team;
    public readonly Entity? Source;

    public SpawnPoint(Vector3 origin, Vector3 angles, float team = 0f, Entity? source = null)
    {
        Origin = origin;
        Angles = angles;
        Team = team;
        Source = source;
    }

    public bool IsValid => Source is not null || Origin != Vector3.Zero || Angles != Vector3.Zero;
}

/// <summary>
/// The spawn lifecycle — a Godot-free port of <c>SelectSpawnPoint</c> / <c>Spawn_Score</c>
/// (server/spawnpoints.qc) and <c>PutPlayerInServer</c> (server/client.qc). This is the "spawn at a
/// spawnpoint with a loadout" half of the DM match loop.
///
/// Faithfully ported:
///  - spawnpoint discovery across the QC classnames (info_player_deathmatch / info_player_start /
///    info_player_team*), via <see cref="IEntityService.FindByClass"/>;
///  - Spawn_Score's idea: each spot is scored by the distance to the NEAREST live player (a spot far from
///    everyone scores higher), with a priority bonus when that distance exceeds <see cref="SpawnMinDist"/>
///    (QC SPAWN_PRIO_GOOD_DISTANCE / mindist=100), bad spots filtered out (Spawn_FilterOutBadSpots);
///  - Spawn_WeightedPoint's 50/50 split between a near-random pick and a strongly-far-biased pick
///    (the "won't get spawn-fragged twice in a row" rule), via the weighted random in
///    <see cref="WeightedPick"/>;
///  - Spawn_Score's wrong-team / inactive / restriction filters (teamcheck, ACTIVE_ACTIVE, restriction 1/2);
///  - PutPlayerInServer's reset: MOVETYPE_WALK, SOLID, DAMAGE_AIM, FL_CLIENT, DEAD_NO, frags baseline,
///    start health/armor/ammo loadout (SetResource), the player bbox (PL_MIN/MAX_CONST), velocities/angles
///    cleared, placement at the spot (origin nudged +1 like the QC setorigin), the spawn shield
///    (Entity.SpawnShieldExpire = time + g_spawnshieldtime), start_items, and the random-start-weapons set.
///
/// NOTE — genuinely cross-boundary (owned by other systems / agents): the assault target spawn_evalfunc
/// chains and race target requirement (need the assault/race objective graph + race-mode flag), the
/// move-out-of-solid relocation (relocate_spawnpoint), the random-weapon AMMO grant + superweapon timer
/// (need a NetName→ammo/superweapon weapon registry), the countdown shield/pause extension (needs the
/// MatchController start-time clock), weapon view entities (CL_SpawnWeaponentity) + SpawnEvent networking
/// (client/net), and the warmup loadout path. Each is flagged precisely at its call site.
/// </summary>
public static class SpawnSystem
{
    /// <summary>The QC spawnpoint classnames searched, in priority order (info_player_deathmatch is canonical).
    /// <c>info_player_survivor</c> is a Survival-mode alias for info_player_deathmatch (spawnpoints.qc:182:
    /// <c>spawnfunc(info_player_survivor) { spawnfunc_info_player_deathmatch(this); }</c>) with no team; Survival
    /// mode is not ported but the class is registered here so maps that use those spots don't silently drop them.</summary>
    public static readonly string[] SpawnClassNames =
    {
        "info_player_deathmatch",
        "info_player_survivor",   // QC spawnpoints.qc:182 alias; Survival-mode maps only, no team
        "info_player_start",
        "info_player_team1",
        "info_player_team2",
        "info_player_team3",
        "info_player_team4",
    };

    /// <summary>QC mindist arg to Spawn_FilterOutBadSpots (server/spawnpoints.qc): 100 qu.</summary>
    public const float SpawnMinDist = 100f;

    /// <summary>QC SPAWN_PRIO_GOOD_DISTANCE (server/spawnpoints.qh).</summary>
    public const int PrioGoodDistance = 10;

    /// <summary>QC PL_MIN_CONST (common/constants.qh).</summary>
    public static readonly Vector3 PlayerMins = new(-16f, -16f, -24f);

    /// <summary>QC PL_MAX_CONST (common/constants.qh).</summary>
    public static readonly Vector3 PlayerMaxs = new(16f, 16f, 45f);

    // ----- starting-loadout cvar names + defaults (Xonotic balance-xonotic.cfg; OPEN Q5 names kept) -----
    private const string CvarHealthStart = "g_balance_health_start"; // 100
    private const string CvarArmorStart  = "g_balance_armor_start";  // 0
    private const string CvarAmmoShells  = "g_start_ammo_shells";
    private const string CvarAmmoNails   = "g_start_ammo_nails";
    private const string CvarAmmoRockets = "g_start_ammo_rockets";
    private const string CvarAmmoCells   = "g_start_ammo_cells";
    private const string CvarAmmoFuel    = "g_start_ammo_fuel";

    private const float DefHealthStart = 100f;
    private const float DefArmorStart  = 0f;

    // spawn shield (QC g_spawnshieldtime, xonotic-server.cfg default 1s; the damage pipeline reads the expiry).
    private const string CvarSpawnShieldTime = "g_spawnshieldtime";
    private const float  DefSpawnShieldTime  = 1f;

    // player model (QC FixPlayermodel → setplayermodel; server/client.qc). sv_defaultplayermodel is the universal
    // default; sv_defaultplayerskin the skin. DefaultPlayerModel is the hardcoded fallback (xonotic-server.cfg:105).
    private const string CvarDefaultPlayerModel = "sv_defaultplayermodel";
    private const string CvarDefaultPlayerSkin  = "sv_defaultplayerskin";
    public  const string DefaultPlayerModel     = "models/player/erebus.iqm";

    // random-start-weapons + start_items (QC server/world.qc; defaults make these no-ops in stock DM).
    private const string CvarRandomStartCount    = "g_random_start_weapons_count"; // 0
    private const string CvarRandomStartWeapons  = "g_random_start_weapons";        // weapon NetName list
    private const string CvarStartItems          = "g_start_items";                 // start_items bitfield, 0
    // per-resource random-start ammo (QC random_start_ammo: SetResource(RES_*, g_random_start_*)).
    private const string CvarRandomStartShells   = "g_random_start_shells";
    private const string CvarRandomStartBullets  = "g_random_start_bullets";
    private const string CvarRandomStartRockets  = "g_random_start_rockets";
    private const string CvarRandomStartCells    = "g_random_start_cells";
    // superweapon timer (QC g_balance_superweapons_time): how long a held superweapon lasts before it's removed.
    private const string CvarSuperweaponsTime    = "g_balance_superweapons_time";
    private const float  DefSuperweaponsTime     = 30f;

    /// <summary>
    /// QC <c>start_weapons</c> for the stock DM loadout: just the Blaster (the always-available sidearm).
    /// Other weapons are picked up from the map. Kept as NetNames to match <see cref="Player.OwnedWeapons"/>.
    /// </summary>
    public static readonly string[] DefaultLoadout = { "blaster" };

    /// <summary>Deterministic RNG for spawn selection (QC random()). Seeded so headless sims are reproducible.</summary>
    private static Random _rng = new(0x5EED);

    /// <summary>Reseed the spawn RNG (test/determinism support — the sim sets this from the world seed).</summary>
    public static void Reseed(int seed) => _rng = new Random(seed);

    // ============================================================================================
    //  Team-spawn state — port of server/spawnpoints.qh globals + the spawnpoints.qc link/select logic.
    //  have_team_spawns (tri-state): 0 = no team spawns requested, -1 = requested but none found, 1 =
    //  requested and found. have_team_spawns_forteams: bit X set ⇒ team X has at least one spawnpoint
    //  (team 0 = the "no-team" spots). some_spawn_has_been_used: a team spawn was claimed by a control point.
    // ============================================================================================

    /// <summary>QC <c>have_team_spawns</c> (server/spawnpoints.qh:16): 0 none requested, -1 requested/none found, 1 found.</summary>
    public static int HaveTeamSpawns;

    /// <summary>QC <c>have_team_spawns_forteams</c>: bit X set ⇒ team X has spawns (team 0 = no-team spots).</summary>
    public static int HaveTeamSpawnsForTeams;

    /// <summary>QC <c>some_spawn_has_been_used</c>: a team spawn was claimed (Onslaught control-point capture).</summary>
    public static bool SomeSpawnHasBeenUsed;

    /// <summary>True once <see cref="RequestTeamSpawns"/> was called for this match (host set the request explicitly).</summary>
    private static bool _teamSpawnsRequestExplicit;

    /// <summary>
    /// QC <c>.spawn_evalfunc</c> on a spawn spot's target (server/spawnpoints.qc Spawn_Score: <c>findchain(targetname,
    /// spot.target)</c> → <c>targ.spawn_evalfunc(targ, player, spot, current)</c>; a returned '-1 0 0' marks the spot
    /// unusable). Assault's <c>target_objective_spawn_evalfunc</c> (sv_assault.qc:28) rejects a spot that targets an
    /// objective whose health is inactive (&gt;= ASSAULT_VALUE_INACTIVE) or destroyed (&lt; 0). The active gametype
    /// installs this (Assault sets it in Activate, clears it in Deactivate); given a spot, return true to REJECT it.
    /// Only consulted on the target-checking spawn path (<c>targetCheck</c>). Null = no objective spawn bias.
    /// </summary>
    public static System.Func<Entity, bool>? SpotEvalReject;

    /// <summary>
    /// Port of <c>race_spawns</c> (server/race.qh:16): when true, the Race gametype has registered at least one
    /// <c>trigger_race_checkpoint</c> that was linked with <c>spawn_evalfunc = trigger_race_checkpoint_spawn_evalfunc</c>
    /// (server/race.qc:1241 <c>++race_spawns</c>; spawnpoints.qc:246 <c>if(race_spawns) if(!spot.target) return '-1 0 0'</c>).
    /// A spawnpoint without a target is always rejected on the target-checking path when this is true — only spots
    /// targeting a checkpoint entity (and passing the checkpoint's spawn_evalfunc) are valid race spawns. Set by
    /// <see cref="Race.Activate"/> when the map has at least one checkpoint; cleared by
    /// <see cref="ResetTeamSpawns"/> on map/mode change and by <see cref="Race.Deactivate"/>. Non-race maps leave
    /// this false, so the gate is a no-op in DM/CTF/etc.
    /// </summary>
    public static bool RaceSpawnsActive;

    /// <summary>
    /// Port of the score-rewriting branch of <c>spawn_evalfunc</c> (server/spawnpoints.qc:285:
    /// <c>spawn_score = targ.spawn_evalfunc(targ, player, spot, spawn_score)</c>): unlike the REJECT path
    /// (<see cref="SpotEvalReject"/>), this delegate ADDS a priority delta to the spot's score. Race uses it to
    /// apply <c>SPAWN_PRIO_RACE_PREVIOUS_SPAWN = 50</c> when the spot is the player's last passed checkpoint
    /// (<c>trigger_race_checkpoint_spawn_evalfunc</c>, server/race.qc:1071). The delegate receives the spot and
    /// the spawning player; it returns a prio delta (0 = no change, positive = boost). Null = no score adjustment.
    /// Only consulted on the target-checking spawn path. The active gametype installs this in Activate and clears
    /// it in Deactivate; <see cref="ResetTeamSpawns"/> also clears it on map/mode change.
    /// </summary>
    public static System.Func<Entity, Player, float>? SpotEvalScore;

    /// <summary>
    /// Port of <c>GameRules_spawning_teams(value)</c> (common/gametypes/sv_rules.qh): the active gametype declares
    /// whether it wants team-only spawnpoints. Sets <see cref="HaveTeamSpawns"/> to -1 (requested) or 0 (not), and
    /// records that the request was made explicitly (so <see cref="SelectSpawnPoint"/> trusts it over the auto
    /// fallback). The host calls this once when a gametype activates, passing
    /// <see cref="GameType.RequestsTeamSpawns"/>. The "found" upgrade to 1 happens during spawnpoint detection.
    /// </summary>
    public static void RequestTeamSpawns(bool value)
    {
        HaveTeamSpawns = value ? -1 : 0;       // QC: have_team_spawns = (value) ? -1 : 0
        _teamSpawnsRequestExplicit = true;
    }

    /// <summary>
    /// Reset the team-spawn globals (QC: cleared per map load). Call when changing maps/gametypes so a stale
    /// "found" latch / request flag from a prior match doesn't leak into the next one. (The forteams mask is
    /// recomputed from the live spots each selection, so it never goes stale on its own.)
    /// </summary>
    public static void ResetTeamSpawns()
    {
        HaveTeamSpawns = 0;
        HaveTeamSpawnsForTeams = 0;
        SomeSpawnHasBeenUsed = false;
        _teamSpawnsRequestExplicit = false;
        SpotEvalReject = null;  // QC: the spawn_evalfunc chain is owned by the active gametype; clear on map/mode change.
        RaceSpawnsActive = false;
        SpotEvalScore = null;
    }

    /// <summary>
    /// QC <c>IL_EACH(g_spawnpoints, true, { ... it.team ... })</c> over the linked spawnpoint list — used by
    /// <c>WinningCondition_RanOutOfSpawns</c> (server/world.qc:1641) to count which teams still have at least one
    /// spawnpoint on the map. Yields each live spawnpoint's team value (<see cref="Teams.None"/> for a non-team
    /// spot), applying the classname-derived team exactly like <see cref="DetectTeamSpawns"/> so an
    /// <c>info_player_teamN</c> that the map left without an explicit <c>team</c> key still reports its team.
    /// </summary>
    public static IEnumerable<float> EnumerateSpawnPointTeams()
    {
        foreach (Entity spot in GatherSpawnPoints())
        {
            float team = spot.Team;
            if (team == Teams.None)
                team = TeamForSpawnClass(spot.ClassName);
            yield return team;
        }
    }

    /// <summary>
    /// Port of the team assignment in the <c>spawnfunc(info_player_teamN)</c> bodies (server/spawnpoints.qc:201-234):
    /// each <c>info_player_teamN</c> sets <c>this.team = NUM_TEAM_N</c> before chaining to info_player_deathmatch.
    /// In this port spawnpoints have no spawnfunc (they're kept as passive edicts), so the team is carried by the
    /// classname when no explicit <c>team</c> key was on the map entity. Returns <see cref="Teams.None"/> for a
    /// non-team spawn class (info_player_deathmatch / info_player_start).
    /// </summary>
    public static float TeamForSpawnClass(string className) => className switch
    {
        "info_player_team1" => Teams.Red,    // QC NUM_TEAM_1
        "info_player_team2" => Teams.Blue,   // QC NUM_TEAM_2
        "info_player_team3" => Teams.Yellow, // QC NUM_TEAM_3
        "info_player_team4" => Teams.Pink,   // QC NUM_TEAM_4
        _ => Teams.None,
    };

    /// <summary>
    /// Port of the team-spawn detection tail of <c>relocate_spawnpoint</c> (server/spawnpoints.qc:159-165): over the
    /// gathered spots, ensure each carries its team (from the classname when the map left it unset), then fold into
    /// the globals — <c>if (have_team_spawns != 0) if (this.team) have_team_spawns = 1;</c> and
    /// <c>have_team_spawns_forteams |= BIT(this.team)</c>. Recomputed from scratch each selection (idempotent and
    /// O(spots) — selection only, never per-frame) so a map change can't leave a stale forteams mask; it always
    /// reflects the spots currently on the map, matching QC's link-time computation. <see cref="HaveTeamSpawns"/>
    /// stays sticky at -1/0 (the gametype's request) until a team spot upgrades it to 1.
    /// </summary>
    private static void DetectTeamSpawns(List<Entity> spots)
    {
        int forTeams = 0;
        bool anyTeamSpot = false;
        foreach (Entity spot in spots)
        {
            // relocate_spawnpoint runs before the spawnfunc in QC; here apply the classname team when the map
            // entity didn't already provide one (info_player_teamN with no explicit "team" key).
            if (spot.Team == Teams.None)
            {
                float t = TeamForSpawnClass(spot.ClassName);
                if (t != Teams.None)
                    spot.Team = t;
            }

            forTeams |= TeamBit(spot.Team);   // QC: have_team_spawns_forteams |= BIT(this.team)
            if (spot.Team != Teams.None)
                anyTeamSpot = true;
        }
        HaveTeamSpawnsForTeams = forTeams;

        // QC: if (have_team_spawns != 0) if (this.team) have_team_spawns = 1; — team spawns were requested
        // (-1) or already found (1), and at least one spot has a team ⇒ "found" (1).
        if (HaveTeamSpawns != 0 && anyTeamSpot)
            HaveTeamSpawns = 1;
    }

    /// <summary>
    /// QC <c>BIT(this.team)</c> over a spawnpoint's team color. Team 0 (no-team) maps to bit 0; a real team color
    /// (<see cref="Teams.Red"/>=4 … <see cref="Teams.Pink"/>=9) maps to its own bit. Internally consistent with
    /// <see cref="Player.Team"/> (same color encoding), which is all the <c>have_team_spawns_forteams</c> test needs.
    /// </summary>
    private static int TeamBit(float team)
    {
        int t = (int)team;
        if (t < 0 || t > 30) return 0; // guard a bogus team value (BIT shift in range)
        return 1 << t;
    }

    /// <summary>
    /// Port of the <c>teamcheck</c> computation in <c>SelectSpawnPoint</c> (server/spawnpoints.qc:375-396).
    /// Given the requesting player's <see cref="Entity.Team"/> and the team-spawn globals, returns the team a
    /// spot must match (or -1 for "any spot"). Mirrors the QC branch ladder exactly:
    /// <list type="bullet">
    /// <item>useallspawns / anypoint ⇒ -1 (any spot).</item>
    /// <item>team spawns found: if the player's team has spawns ⇒ MUST be that team; else fall back to the
    ///   no-team spots if they exist, else any spot.</item>
    /// <item>no team spawns but no-team spots exist ⇒ MUST be a no-team spot (0).</item>
    /// <item>otherwise ⇒ -1 (require team spawns but have none, or require non-team and have none: use any).</item>
    /// </list>
    /// </summary>
    private static float ComputeTeamCheck(Player forPlayer, bool anyPoint)
    {
        // QC: if (anypoint || autocvar_g_spawn_useallspawns) teamcheck = -1;
        if (anyPoint || CvarBool("g_spawn_useallspawns", false))
            return -1f;

        // If the host never explicitly requested team spawns (no RequestTeamSpawns call), fall back to deriving
        // the request from the player: a team game assigns a valid team, FFA leaves it None. This keeps the team
        // gametypes spawning on team spots out of the box even before the host wires the per-mode request.
        if (!_teamSpawnsRequestExplicit && HaveTeamSpawns == 0
            && IsValidTeam(forPlayer.Team) && (HaveTeamSpawnsForTeams & ~BitZero) != 0)
        {
            HaveTeamSpawns = 1; // a team player + team spots present ⇒ treat as requested-and-found
        }

        int playerBit = TeamBit(forPlayer.Team);

        if (HaveTeamSpawns > 0)
        {
            // QC: if (!(have_team_spawns_forteams & BIT(this.team)))
            if ((HaveTeamSpawnsForTeams & playerBit) == 0)
            {
                // the player's team has no spawns: try the no-team spots, else any spot.
                if ((HaveTeamSpawnsForTeams & BitZero) != 0)
                    return 0f;       // QC: try noteam spawns
                return -1f;          // QC: if not, any spawn has to do
            }
            return forPlayer.Team;   // QC: teamcheck = this.team — MUST be team
        }

        // QC: else if (have_team_spawns == 0 && (have_team_spawns_forteams & BIT(0))) teamcheck = 0; // MUST be noteam
        if (HaveTeamSpawns == 0 && (HaveTeamSpawnsForTeams & BitZero) != 0)
            return 0f;

        // QC: else teamcheck = -1; — require team spawns but have none, or require non-team and have none.
        return -1f;
    }

    /// <summary>QC <c>BIT(0)</c>: the bit for the "no-team" (team 0) spawnpoints.</summary>
    private const int BitZero = 1; // 1 << 0

    /// <summary>Sentinel for the <c>teamCheck</c> parameter meaning "derive teamcheck from the player + globals".</summary>
    private const float AutoTeamCheck = float.NaN;

    /// <summary>QC <c>Team_IsValidTeam</c> (common/teams.qh): one of the four real team colors (not None / spectator).</summary>
    private static bool IsValidTeam(float team)
    {
        int t = (int)team;
        return t == Teams.Red || t == Teams.Blue || t == Teams.Yellow || t == Teams.Pink;
    }

    /// <summary>
    /// Port of <c>SelectSpawnPoint(this, anypoint)</c> (server/spawnpoints.qc): gather every spawnpoint, derive
    /// the team filter, score each by distance to the nearest live player, drop the unusable ones, and
    /// weighted-random-pick among the survivors (50/50 near-random vs. far-biased). Returns null only when no
    /// spawnpoint exists at all.
    /// </summary>
    /// <param name="forPlayer">The player being spawned (excluded from the "nearest other player" check; its
    /// <see cref="Entity.Team"/> drives the team-spawn filter when <paramref name="teamCheck"/> is auto).</param>
    /// <param name="livePlayers">The currently-alive players to keep new spawns away from.</param>
    /// <param name="teamCheck">
    /// QC <c>teamcheck</c>: when &gt;= 0 only spots whose <see cref="Entity.Team"/> equals it are eligible (the
    /// teamplay team-spawn path, <c>have_team_spawns</c>); -1 is the FFA path (any spot). The default
    /// (<see cref="AutoTeamCheck"/>) DERIVES teamcheck from the player's team + the team-spawn globals exactly as
    /// QC's SelectSpawnPoint does — that is the normal path; an explicit value is for tests / forced spots.
    /// </param>
    /// <param name="targetCheck">
    /// QC <c>targetcheck</c> (the 2nd arg to SelectSpawnPoint): enables the ACTIVE_ACTIVE gate and the
    /// assault <c>spawn_evalfunc</c> target chain. Plain DM passes false; assault/onslaught pass true.
    /// </param>
    /// <param name="anyPoint">QC <c>anypoint</c>: ignore team/target filtering and pick from every spot (the
    /// <c>g_spawn_useallspawns</c> / testspawn path). Forces <paramref name="teamCheck"/> to -1.</param>
    public static SpawnPoint? SelectSpawnPoint(Player forPlayer, IReadOnlyList<Player> livePlayers,
        float teamCheck = AutoTeamCheck, bool targetCheck = false, bool anyPoint = false)
    {
        // QC SelectSpawnPoint (spawnpoints.qc): `if (this.spawnpoint_targ) return this.spawnpoint_targ;` — a
        // target_spawnpoint that was `use`d on this player FORCES the next spawn to that spot, short-circuiting
        // all gathering/scoring/team-filtering. The redirect is one-shot: PutPlayerInServer clears it (client.qc:735),
        // so it only applies to the immediately-following spawn. (The forced spot can still be embedded — Base
        // doesn't re-filter it either.)
        if (forPlayer.SpawnPointTarg is { IsFreed: false } forced)
            return ToSpawnPoint(forced);

        var spots = GatherSpawnPoints();
        if (spots.Count == 0)
            return null;

        // Port of the relocate_spawnpoint team-spawn detection tail (spawnpoints.qc:159-165): assign each spot
        // its classname team + fold into have_team_spawns / have_team_spawns_forteams. QC computes this once at
        // link time; here it's recomputed from the live spots each selection (idempotent, O(spots)).
        DetectTeamSpawns(spots);

        // Port of the teamcheck branch ladder in SelectSpawnPoint (spawnpoints.qc:375-396). When the caller left
        // teamCheck at the auto sentinel (the normal path), derive it from the player's team + the globals; an
        // explicit value (tests / a forced team) is honored as-is.
        float resolvedTeamCheck = float.IsNaN(teamCheck) ? ComputeTeamCheck(forPlayer, anyPoint)
                                : (anyPoint ? -1f : teamCheck);

        // QC SelectSpawnPoint anypoint branch (spawnpoints.qc:413-416): `if(anypoint) spot =
        // Spawn_WeightedPoint(firstspot, 1, 1, 1);` — Spawn_FilterOutBadSpots/Spawn_ScoreAll is NEVER called, so
        // every spot keeps its default '0 0 0' score: weight = bound(1,0,1)^1 * cnt = cnt, priority = 0. The pick
        // is therefore a uniform (cnt-weighted) random over ALL spots, with NO startsolid / team / active /
        // restriction filtering. Seed each spot at (prio 0, weight 0) and weighted-pick directly.
        if (anyPoint)
        {
            var all = new List<(Entity spot, float prio, float weight)>(spots.Count);
            foreach (var spot in spots)
                all.Add((spot, 0f, 0f));
            return ToSpawnPoint(WeightedPick(all, lower: 1f, upper: 1f, exponent: 1f));
        }

        // Spawn_ScoreAll + Spawn_FilterOutBadSpots: score each spot, keep prio >= 0.
        // score = (prio, weight): weight is the distance to the nearest live OTHER player. Base always primary-
        // filters with targetcheck=true (spawnpoints.qc:419); the port's targetCheck arg drives the primary pass
        // (true on the live ClientManager path, false on the bot/match sim paths where the gate is intentionally off).
        var scored = ScoreAndFilter(forPlayer, spots, livePlayers, resolvedTeamCheck, targetCheck);

        // QC SelectSpawnPoint emergency fallback (spawnpoints.qc:421-435): "double check without targets — fixes
        // some crashes with improperly repacked maps". When the target-checking filter dropped EVERY spot, re-run
        // the filter once with targetcheck=false (relaxes the ACTIVE_ACTIVE gate + the assault spawn_evalfunc /
        // race-target requirement) before giving up. This is what keeps a player off the prio-0 in-solid fallback
        // when the only thing rejecting the spots was the target/active gate, not their geometry. Only meaningful
        // after a primary pass that target-checked (targetCheck=false already relaxed those gates).
        if (scored.Count == 0 && targetCheck)
            scored = ScoreAndFilter(forPlayer, spots, livePlayers, resolvedTeamCheck, targetCheck: false);

        // QC SelectSpawnPoint (spawnpoints.qc:446-457): if even the targetcheck=false re-filter came back empty,
        // Spawn_WeightedPoint is called with a NULL firstspot and returns NULL (RandomSelection_chosen_ent = world).
        // Base then falls into `if(!spot)` → either GotoNextMap (spawn_debug) or `some_spawn_has_been_used` → NULL
        // (team locked out by enemy control-point capture, a legitimate transient state) or error("Cannot find a
        // spawn point"). The port mirrors this: return null here so the ClientManager caller can schedule a retry
        // (or log the error), rather than seeding all spots at prio-0 and placing the player in solid / the wrong
        // team's area. The some_spawn_has_been_used latch is now LIVE (SpawnPointUse sets it when a control point
        // claims a team spot), so the "team can't spawn any more" case is genuinely reachable on Onslaught/Assault
        // maps — the caller's 1s RespawnTime retry IS Base's "wait for your team to retake a point" behavior.
        // Stock DM/CTF never trigger this path (every spot is active and the -1/any fallback widens the teamcheck).
        if (scored.Count == 0)
            return null;

        // QC SelectSpawnPoint: with probability (1 - g_spawn_furthest) use a near-uniform pick (1,1,1); otherwise
        // a strongly-far-biased pick (1,5000,5) — `if (random() > g_spawn_furthest)` takes the near branch, so a
        // HIGHER g_spawn_furthest means MORE spawns far from players. Default 0.5 = 50/50 (identical to before).
        // [R0b] CvarOr (not the 0-fallback Cvar) so an explicit g_spawn_furthest 0 (always-uniform, a legitimate
        // Base config) reads back as 0 instead of being silently coerced to the 0.5 default.
        bool farBranch = !(_rng.NextDouble() > CvarOr("g_spawn_furthest", 0.5f));
        Entity chosen = !farBranch
            ? WeightedPick(scored, lower: 1f, upper: 1f, exponent: 1f)
            // [R3] The far pick: dist^5 roulette by default, or (when g_spawn_furthest_topfraction > 0) a uniform
            // pick among the "far set" — every spot within topfraction of the max weight — so the single furthest
            // spot is never a near-certainty (the spawn-control lever). topfraction 0 keeps the faithful exponent.
            : WeightedPickFar(scored, CvarOr("g_spawn_furthest_topfraction", 0f));

        // [R5] Occupied-spot re-pick: if the chosen spot's placement box overlaps a live player hull, re-pick once
        // from the remaining scored set (UT-style) rather than dropping the spawnee on top of them (Base overlap).
        chosen = RepickIfOccupied(chosen, scored, livePlayers, farBranch);

        // [sv_spawn_debug] one structured line per resolved spawn so a bot match can be scored for bad spawns
        // (a visible enemy within LOS distance) before/after the anti-abuse levers. Gated; silent by default.
        LogSpawnDebug(forPlayer, chosen, livePlayers, farBranch);

        return ToSpawnPoint(chosen);
    }

    /// <summary>
    /// Port of <c>Spawn_FilterOutBadSpots</c> (via <c>Spawn_ScoreAll</c>, spawnpoints.qc:303-334): score every
    /// spot with the given <paramref name="targetCheck"/> and keep only those with prio &gt;= 0. Factored out so
    /// <see cref="SelectSpawnPoint"/> can run it twice — once target-checking, then (if nothing survived) the
    /// emergency re-filter with <paramref name="targetCheck"/>=false (spawnpoints.qc:421-435).
    /// </summary>
    private static List<(Entity spot, float prio, float weight)> ScoreAndFilter(Player forPlayer,
        List<Entity> spots, IReadOnlyList<Player> livePlayers, float teamCheck, bool targetCheck)
    {
        var scored = new List<(Entity spot, float prio, float weight)>(spots.Count);
        foreach (var spot in spots)
        {
            (float prio, float weight) = ScoreSpot(forPlayer, spot, livePlayers, teamCheck, targetCheck);
            if (prio >= 0f)
                scored.Add((spot, prio, weight));
        }
        return scored;
    }

    /// <summary>
    /// Port of <c>Spawn_Score</c> (server/spawnpoints.qc), single-FFA path. Returns (prio, weight):
    /// weight = distance to the nearest live other player (a large, world-sized value if none);
    /// prio = <see cref="PrioGoodDistance"/> when that distance exceeds <see cref="SpawnMinDist"/>, else 0.
    /// A negative prio (none produced here yet) would mark the spot unusable.
    /// </summary>
    private static (float prio, float weight) ScoreSpot(Player forPlayer, Entity spot,
        IReadOnlyList<Player> livePlayers, float teamCheck, bool targetCheck)
    {
        // Spawn_Score filters (server/spawnpoints.qc:239), all returning '-1 0 0' (unusable):
        //  - wrong team: teamcheck >= 0 && spot.team != teamcheck
        //  - inactive (only when target-checking): spot.active != ACTIVE_ACTIVE && targetcheck
        //  - restriction: real client rejected by restriction==1; bot rejected by restriction==2
        if (teamCheck >= 0f && spot.Team != teamCheck)
            return (-1f, 0f);
        // QC Spawn_Score (server/spawnpoints.qc:246-248): if(race_spawns) if(!spot.target || spot.target == "") return '-1 0 0';
        // When race checkpoints exist, a spawnpoint without a target cannot be a valid race spawn — only spots that
        // target a checkpoint entity (so the checkpoint's spawn_evalfunc fires) are admitted. This reject is
        // UNCONDITIONAL in QC (NOT gated on targetcheck) and runs BEFORE the active gate — so it also fires on the
        // emergency targetcheck=false re-filter: QC's "double check without targets" relaxes ONLY the active gate +
        // the spawn_evalfunc target chain (line 277, which IS targetcheck-gated), never the race_spawns requirement.
        // In practice DM/CTF leave RaceSpawnsActive=false, so this is a no-op outside Race.
        if (RaceSpawnsActive && string.IsNullOrEmpty(spot.Target))
            return (-1f, 0f);
        if (spot.SpawnActive != MapMover.ActiveActive && targetCheck)
            return (-1f, 0f);
        // QC Spawn_Score spawn_evalfunc chain (server/spawnpoints.qc): findchain(targetname, spot.target) →
        // targ.spawn_evalfunc(...); a '-1 0 0' return rejects the spot. Assault installs target_objective_spawn_-
        // evalfunc (rejects a spot targeting an inactive/destroyed objective). Only on the target-checking path.
        if (targetCheck && SpotEvalReject is not null && SpotEvalReject(spot))
            return (-1f, 0f);
        if (!forPlayer.IsBot)
        {
            if (spot.SpawnRestriction == 1) return (-1f, 0f); // bots-only spot
        }
        else
        {
            if (spot.SpawnRestriction == 2) return (-1f, 0f); // real-clients-only spot
        }

        // Spawn_FilterOutBadSpots (server/spawnpoints.qc): a spot where the player hull would spawn EMBEDDED IN
        // SOLID. QC nudges such a spot OUT of solid at link time (relocate_spawnpoint: move_out_of_solid, gated by
        // g_spawnpoints_auto_move_out_of_solid default 1) and keeps it; only when it can't get out at all (or the
        // cvar is off) is the spot unusable. The port has no link-time relocation, so do the equivalent here at
        // select time: trace the player box at the actual placement (spot.origin + the PutPlayerInServer z-nudge)
        // and, on trace_startsolid, attempt the move-out-of-solid relocation, persisting it onto the spot's origin
        // (the spot entity is re-read each selection, so the nudge sticks like Base's permanent link-time move).
        //
        // [R0a] The trace is NOMONSTERS (world + brush-model entities, no player hulls). Base runs this once at
        // map LOAD (relocate_spawnpoint), when the world is empty of players — so it only ever caught spots
        // embedded in solid geometry (a mapper error). The port runs it per-SELECTION, when players are live; a
        // plain Normal trace here would clip against player hulls, so an enemy standing on a spot would trip
        // startsolid and either PERMANENTLY relocate the spot ~70qu upward (the persisted SetOrigin below) or,
        // under a low ceiling, get it scored -1 and dropped from the pool while they stand there — a body-block
        // spawn-control lever Base doesn't have. NoMonsters restores Base's intent (players can't displace spots)
        // while STILL rejecting a spot covered by a closed func_door/plat (Solid.Bsp clips under NoMonsters —
        // WorldOnly would skip those and spawn the player inside the door). Occupied spots are policy (R5), not here.
        if (Api.Services is not null)
        {
            Vector3 place = spot.Origin + new Vector3(0f, 0f, 1f - PlayerMins.Z - 24f);
            TraceResult bad = Api.Trace.Trace(place, PlayerMins, PlayerMaxs, place, MoveFilter.NoMonsters, forPlayer);
            if (bad.StartSolid)
            {
                if (!CvarBool("g_spawnpoints_auto_move_out_of_solid", true)
                    || !RelocateSpawnOutOfSolid(spot, forPlayer))
                    return (-1f, 0f); // couldn't get out of solid (or relocation disabled) — drop the spot
            }
        }

        // QC seeds 'shortest' with vlen(world.maxs - world.mins); without world bounds here we use a large
        // constant so a spot with no nearby players still gets a high (good) weight.
        // [R4] In teamplay, g_spawn_distance_enemies_only measures the nearest-player distance over ENEMIES only
        // (skipping live teammates), so the scorer stops shoving you away from your own team the way it pushes you
        // off enemies — the UT/Halo teammate-positive asymmetry. Off (default) keeps Base's all-players distance.
        bool enemiesOnly = Scoring.GameScores.Teamplay && CvarBool("g_spawn_distance_enemies_only", false);
        float shortest = 1_000_000f;
        for (int i = 0; i < livePlayers.Count; i++)
        {
            Player other = livePlayers[i];
            // Skip self, the dead, and observers (a free-fly spectator has DeadState==No but isn't a live
            // player — QC IS_PLAYER excludes spectators, so they must not bias spawn distance scoring).
            if (ReferenceEquals(other, forPlayer) || other.IsDead || other.IsObserver)
                continue;
            if (enemiesOnly && SameTeamLive(forPlayer, other))
                continue; // [R4] a live teammate no longer repels the spawn
            float d = Vector3.Distance(other.Origin, spot.Origin);
            if (d < shortest)
                shortest = d;
        }

        float prio = shortest > SpawnMinDist ? PrioGoodDistance : 0f;

        // [R1] LOS-aware demotion (g_spawn_avoid_los, default ON): if any live enemy can SEE this spot — map-PVS
        // prefilter, then a world-only eye-to-eye traceline within g_spawn_avoid_los_distance — drop the
        // good-distance tier so a hidden spot outscores a visible one. This is the direct answer to "I spawned in
        // his crosshair". Floored at 0 so it never makes a spot unusable (a visible spot is a last resort, not
        // rejected). The Duel gametype disables this for the match (Duel.Activate saves + sets g_spawn_avoid_los 0,
        // Deactivate restores) to preserve the competitive spawn-reading meta.
        if (CvarBool("g_spawn_avoid_los", true) && SpotSeenByEnemy(forPlayer, spot, livePlayers))
            prio = MathF.Max(0f, prio - PrioGoodDistance);

        // [R2] Death-point avoidance (g_spawn_avoid_death_radius, default 0/off): demote spots within the radius of
        // where THIS player just died, decaying over g_spawn_avoid_death_time since the death — breaks the "respawn
        // into the same fight" loop and the killer's re-aim camp. Off by default (a pure divergence from Base).
        prio = MathF.Max(0f, prio - DeathAvoidPenalty(forPlayer, spot));

        // QC Spawn_Score spawn_evalfunc score-rewriting branch (server/spawnpoints.qc:285): after computing the base
        // (prio, weight), pass the score through any installed spawn_evalfunc that can ADD to the priority (rather than
        // just veto the spot). Race installs trigger_race_checkpoint_spawn_evalfunc (server/race.qc:1055/1071) which
        // adds SPAWN_PRIO_RACE_PREVIOUS_SPAWN=50 when the spot is the player's last passed checkpoint, biasing the
        // scorer toward respawning the racer at the checkpoint they most recently crossed. Only on the target-check path.
        if (targetCheck && SpotEvalScore is not null)
            prio += SpotEvalScore(spot, forPlayer);

        // MUTATOR_CALLHOOK(Spawn_Score, player, spawn_spot, spawn_score) — server/spawnpoints.qc Spawn_Score.
        // Mutators bias a spot's priority here: spawn_unique demotes a repeat spot, spawn_near_teammate
        // promotes spots near a live teammate. QC packs (prio, weight) into the spawn_score vector's (x, y);
        // the port keeps them as the two floats returned below, so the hook exposes both and reads them back.
        var ss = new MutatorHooks.SpawnScoreArgs(forPlayer, spot, prio, shortest);
        MutatorHooks.SpawnScore.Call(ref ss);
        prio = ss.Priority;
        shortest = ss.Weight;

        return (prio, shortest);
    }

    /// <summary>
    /// Port of <c>Spawn_WeightedPoint</c> (server/spawnpoints.qc): weighted random selection where a spot's
    /// weight is <c>bound(lower, distance, upper) ^ exponent</c>, and the priority bonus is folded in as in
    /// QC's RandomSelection (prio contributes a tie-break "preference" so good-distance spots win ties). The
    /// classic Quake reservoir trick (RandomSelection_AddEnt): keep each candidate with probability
    /// weight/runningTotal.
    /// </summary>
    private static Entity WeightedPick(List<(Entity spot, float prio, float weight)> scored, float lower, float upper, float exponent)
    {
        Entity? chosen = null;
        float bestPriority = -1f;
        float total = 0f;

        foreach (var (spot, prio, weight) in scored)
        {
            // QC Spawn_WeightedPoint (spawnpoints.qc:344): weight = bound(lower, score.y, upper)^exponent * spot.cnt.
            // .cnt lets a mapper weight a cluster of nearby spawnpoints more heavily; relocate_spawnpoint defaults
            // it to 1 (`if(!this.cnt) this.cnt = 1`), so a spot that left Cnt at 0 (the port's default) counts as 1.
            int cnt = spot.Cnt != 0 ? spot.Cnt : 1;
            float w = MathF.Pow(QBound(lower, weight, upper), exponent) * cnt;
            // QC priority: (weight >= lower) * 0.5 + prio — higher-priority candidates are preferred and
            // a strictly-higher priority resets the reservoir (never undercut by a lower-priority pick).
            float priority = (weight >= lower ? 0.5f : 0f) + prio;

            if (priority > bestPriority)
            {
                // a better priority tier appears: restart the weighted reservoir on it
                bestPriority = priority;
                total = w;
                chosen = spot;
            }
            else if (priority == bestPriority)
            {
                total += w;
                if (w > 0f && _rng.NextDouble() * total <= w)
                    chosen = spot;
            }
        }

        return chosen ?? scored[0].spot;
    }

    /// <summary>
    /// [R3] The far-branch pick. With <c>g_spawn_furthest_topfraction</c> at 0 this is the faithful QC far pick
    /// (<c>dist^5</c> roulette over the best prio tier). With a positive top-fraction it instead picks UNIFORMLY
    /// among the "far set" — every spot in the best tier whose <c>dist^5·cnt</c> weight is within
    /// <paramref name="topFraction"/> of the tier's max — so the single furthest spot is no longer a near-certainty
    /// (the spawn-control lever the complaints describe), while spawns still land far from players.
    /// </summary>
    private static Entity WeightedPickFar(List<(Entity spot, float prio, float weight)> scored, float topFraction)
    {
        if (topFraction <= 0f)
            return WeightedPick(scored, lower: 1f, upper: 5000f, exponent: 5f);

        // Best prio tier (QC priority = (weight>=1)*0.5 + prio; weight is a distance >= 1 here, so the tier is the
        // max prio), then the max dist^5·cnt weight within it.
        float bestPrio = float.NegativeInfinity;
        foreach (var s in scored)
            if (s.prio > bestPrio) bestPrio = s.prio;

        float maxW = 0f;
        foreach (var s in scored)
        {
            if (s.prio != bestPrio) continue;
            int cnt = s.spot.Cnt != 0 ? s.spot.Cnt : 1;
            float w = MathF.Pow(QBound(1f, s.weight, 5000f), 5f) * cnt;
            if (w > maxW) maxW = w;
        }

        float threshold = topFraction * maxW;
        Entity? chosen = null;
        int count = 0;
        foreach (var s in scored)
        {
            if (s.prio != bestPrio) continue;
            int cnt = s.spot.Cnt != 0 ? s.spot.Cnt : 1;
            float w = MathF.Pow(QBound(1f, s.weight, 5000f), 5f) * cnt;
            if (w < threshold) continue;
            count++;
            if (_rng.Next(count) == 0) chosen = s.spot; // uniform reservoir over the far set
        }
        return chosen ?? WeightedPick(scored, lower: 1f, upper: 5000f, exponent: 5f);
    }

    /// <summary>
    /// [R5] Occupied-spot re-pick (<c>g_spawn_occupied_repick</c>, default on): if <paramref name="chosen"/>'s
    /// player-placement box overlaps a live player's hull, drop it and re-pick ONCE from the remaining scored
    /// spots (UT-style avoid-overlap), rather than dropping the spawnee on top of them (Base's overlap). Bounded to
    /// a single re-pick — the replacement is accepted even if it too is occupied (an all-crowded map), so there is
    /// no loop and no failure path.
    /// </summary>
    private static Entity RepickIfOccupied(Entity chosen, List<(Entity spot, float prio, float weight)> scored,
        IReadOnlyList<Player> livePlayers, bool farBranch)
    {
        if (!CvarBool("g_spawn_occupied_repick", true) || !IsOccupied(chosen, livePlayers))
            return chosen;

        var remaining = new List<(Entity spot, float prio, float weight)>(scored.Count);
        foreach (var s in scored)
            if (!ReferenceEquals(s.spot, chosen)) remaining.Add(s);
        if (remaining.Count == 0)
            return chosen; // only the occupied spot exists — nothing better to do

        return farBranch
            ? WeightedPickFar(remaining, CvarOr("g_spawn_furthest_topfraction", 0f))
            : WeightedPick(remaining, lower: 1f, upper: 1f, exponent: 1f);
    }

    /// <summary>[R5] Does a live player's hull overlap the spawn placement box at <paramref name="spot"/>? A pure
    /// AABB test (no trace) so only an actual body on the spot counts — a wall/floor never does.</summary>
    private static bool IsOccupied(Entity spot, IReadOnlyList<Player> livePlayers)
    {
        Vector3 place = spot.Origin + new Vector3(0f, 0f, 1f - PlayerMins.Z - 24f);
        Vector3 lo = place + PlayerMins, hi = place + PlayerMaxs;
        for (int i = 0; i < livePlayers.Count; i++)
        {
            Player other = livePlayers[i];
            if (other.IsDead || other.IsObserver) continue;
            Vector3 omin = other.Origin + PlayerMins, omax = other.Origin + PlayerMaxs;
            if (lo.X <= omax.X && hi.X >= omin.X &&
                lo.Y <= omax.Y && hi.Y >= omin.Y &&
                lo.Z <= omax.Z && hi.Z >= omin.Z)
                return true;
        }
        return false;
    }

    /// <summary>[R1] The standing eye height (QC PL_VIEW_OFS) used as both endpoints of the LOS sightline.</summary>
    private static readonly Vector3 StandEye = XonoticGodot.Common.Physics.PlayerPhysics.StandViewOfs;

    /// <summary>QC <c>SAME_TEAM</c> for two live players: equal, non-zero team colors.</summary>
    private static bool SameTeamLive(Player a, Player b) => a.Team != Teams.None && a.Team == b.Team;

    /// <summary>Is <paramref name="other"/> an enemy of <paramref name="self"/>? In FFA everyone is; in teamplay
    /// only a different (valid) team.</summary>
    private static bool IsEnemy(Player self, Player other)
        => !Scoring.GameScores.Teamplay || !SameTeamLive(self, other);

    /// <summary>
    /// [R1] Can any live enemy currently SEE <paramref name="spot"/>? For each enemy within
    /// <c>g_spawn_avoid_los_distance</c>, a cheap map-PVS reject (<see cref="ITraceService.CheckPvs"/>, a
    /// conservative superset) then an exact WORLD-ONLY eye-to-eye traceline — a clear map sightline means the spot
    /// is exposed. World-only so a teammate momentarily standing in the sightline doesn't mask an exposed spot.
    /// </summary>
    private static bool SpotSeenByEnemy(Player forPlayer, Entity spot, IReadOnlyList<Player> livePlayers)
    {
        if (Api.Services is null)
            return false;
        float losDist = CvarOr("g_spawn_avoid_los_distance", 1250f);
        if (losDist <= 0f)
            return false;

        Vector3 spotEye = spot.Origin + StandEye;
        for (int i = 0; i < livePlayers.Count; i++)
        {
            Player other = livePlayers[i];
            if (ReferenceEquals(other, forPlayer) || other.IsDead || other.IsObserver)
                continue;
            if (!IsEnemy(forPlayer, other))
                continue;
            if (Vector3.Distance(other.Origin, spot.Origin) > losDist)
                continue;
            Vector3 enemyEye = other.Origin + StandEye;
            if (!Api.Trace.CheckPvs(spotEye, enemyEye))
                continue; // not even potentially visible per the compiled PVS — skip the trace
            TraceResult t = Api.Trace.Trace(spotEye, Vector3.Zero, Vector3.Zero, enemyEye, MoveFilter.WorldOnly, forPlayer);
            if (t.Fraction >= 1f)
                return true; // clear sightline → the spot is visible to this enemy
        }
        return false;
    }

    /// <summary>
    /// [R2] The death-point avoidance penalty for <paramref name="spot"/> (0 when <c>g_spawn_avoid_death_radius</c>
    /// is 0/off). Spots within the radius of where <paramref name="forPlayer"/> last died are demoted, the penalty
    /// decaying linearly to 0 over <c>g_spawn_avoid_death_time</c> seconds since the death (Halo-style). A player
    /// who hasn't died yet (zero <see cref="Player.DeathOrigin"/>) is never penalized.
    /// </summary>
    private static float DeathAvoidPenalty(Player forPlayer, Entity spot)
    {
        float radius = CvarOr("g_spawn_avoid_death_radius", 0f);
        if (radius <= 0f || forPlayer.DeathOrigin == Vector3.Zero)
            return 0f;
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float window = CvarOr("g_spawn_avoid_death_time", 8f);
        float elapsed = now - forPlayer.DeathTime;
        if (window > 0f && elapsed >= window)
            return 0f; // the death is too old to still steer the spawn
        if (Vector3.Distance(spot.Origin, forPlayer.DeathOrigin) >= radius)
            return 0f;
        float decay = window > 0f ? MathF.Max(0f, 1f - elapsed / window) : 1f;
        return PrioGoodDistance * decay;
    }

    /// <summary>
    /// [sv_spawn_debug] Emit one structured line per resolved spawn — the spawning player, the chosen spot, the
    /// pick branch, the nearest-enemy distance, and whether the spot is visible to an enemy — so a bot match can be
    /// scored for "bad spawns" (a visible enemy within LOS distance) before/after the anti-abuse levers. Gated on
    /// <c>sv_spawn_debug</c> (distinct from Base's <c>spawn_debug</c>, which triggers GotoNextMap); silent by default.
    /// </summary>
    private static void LogSpawnDebug(Player forPlayer, Entity chosen, IReadOnlyList<Player> livePlayers, bool farBranch)
    {
        if (Api.Services is null || !CvarBool("sv_spawn_debug", false))
            return;

        float nearest = float.PositiveInfinity;
        for (int i = 0; i < livePlayers.Count; i++)
        {
            Player other = livePlayers[i];
            if (ReferenceEquals(other, forPlayer) || other.IsDead || other.IsObserver)
                continue;
            if (!IsEnemy(forPlayer, other))
                continue;
            float d = Vector3.Distance(other.Origin, chosen.Origin);
            if (d < nearest) nearest = d;
        }
        bool seen = SpotSeenByEnemy(forPlayer, chosen, livePlayers);
        string near = float.IsPositiveInfinity(nearest)
            ? "none"
            : nearest.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        XonoticGodot.Common.Diagnostics.Log.Info(
            $"[spawn] player=#{forPlayer.Index} spot=#{chosen.Index} branch={(farBranch ? "far" : "uniform")} " +
            $"nearestEnemy={near} enemyLOS={(seen ? "YES" : "no")}");
    }

    /// <summary>Collect all spawnpoint entities across the known classnames (deduped, stable order).</summary>
    private static List<Entity> GatherSpawnPoints()
    {
        var list = new List<Entity>();
        if (Api.Services is null)
            return list;

        foreach (string cls in SpawnClassNames)
        {
            foreach (Entity e in Api.Entities.FindByClass(cls))
            {
                if (e is { IsFreed: false } && !list.Contains(e))
                {
                    // QC relocate_spawnpoint (spawnpoints.qc:151-153): `this.setactive = spawnpoint_setactive;
                    // this.use = spawnpoint_use`. Each spawnpoint gets a Use + SetActive handler at link time so a
                    // control point's target chain (Onslaught/Assault) that captures a spot retags it + latches
                    // some_spawn_has_been_used, and a relay_(de)activate that toggles it flips SpawnActive (which
                    // ScoreSpot's ACTIVE_ACTIVE gate honors). The port has no link-time pass, so install them lazily
                    // here (idempotent: only set when unset; never clobbers a map-wired handler).
                    e.Use ??= SpawnPointUse;
                    e.SetActive ??= SpawnPointSetActive;
                    list.Add(e);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Port of <c>spawnpoint_use</c> (server/spawnpoints.qc:67-77): when a control point's target chain fires a
    /// claimed team spawnpoint in teamplay (and team spawns are in use), retag the spot to the activator's team
    /// and latch <see cref="SomeSpawnHasBeenUsed"/> — the enemy can no longer spawn there, and once a team has
    /// lost all its spots <see cref="SelectSpawnPoint"/> returns null (team locked out) rather than error()ing.
    /// </summary>
    private static void SpawnPointUse(Entity self, Entity activator)
    {
        if (!GameScores.Teamplay)
            return;
        if (HaveTeamSpawns <= 0)
            return;
        self.Team = activator.Team;
        SomeSpawnHasBeenUsed = true;
    }

    /// <summary>
    /// Port of <c>spawnpoint_setactive</c> (server/spawnpoints.qc:84-99): toggle/set a spawnpoint's ACTIVE_*
    /// state (a control-point loss/restore in Onslaught/Assault deactivates/reactivates its linked spawns via a
    /// relay_(de)activate). ACTIVE_TOGGLE flips; otherwise the state is set directly. On an actual state CHANGE,
    /// when teamplay + team spawns are in use, latch <see cref="SomeSpawnHasBeenUsed"/> (mappers can let players
    /// disable enemy spawns). The spawnpoint's active state lives in <see cref="Entity.SpawnActive"/> (distinct
    /// from the mover <c>.active</c>) so ScoreSpot's ACTIVE_ACTIVE gate reads back the new value immediately.
    /// </summary>
    private static void SpawnPointSetActive(Entity self, int act)
    {
        int old = self.SpawnActive;
        if (act == MapMover.ActiveToggle)
            self.SpawnActive = self.SpawnActive == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
        else
            self.SpawnActive = act;

        if (self.SpawnActive != old && GameScores.Teamplay && HaveTeamSpawns > 0)
            SomeSpawnHasBeenUsed = true;
    }

    /// <summary>
    /// Port of <c>relocate_spawnpoint</c>'s move-out-of-solid nudge (server/spawnpoints.qc:121-145, the
    /// <c>move_out_of_solid</c> branch) collapsed to select time: the spot's player-hull placement is embedded in
    /// solid, so step it straight up (the common rest-in-floor case) until the box is clear and commit the new
    /// origin onto the spot entity. Returns false when no clear position is found within the box-height search
    /// range — QC's <c>objerror "could not get out of solid at all!"</c> case, where the spot is dropped.
    /// The trace is the PutPlayerInServer placement box (player hull at spot.origin + the z-nudge), so a success
    /// here guarantees the player lands clear.
    /// </summary>
    private static bool RelocateSpawnOutOfSolid(Entity spot, Player forPlayer)
    {
        if (Api.Services is null)
            return false;

        float zNudge = 1f - PlayerMins.Z - 24f;
        // Search up to the player-box height + a small margin (QC move_out_of_solid expands by the box extent).
        float maxRise = (PlayerMaxs.Z - PlayerMins.Z) + 16f;
        for (float dz = 2f; dz <= maxRise; dz += 2f)
        {
            Vector3 newOrigin = spot.Origin + new Vector3(0f, 0f, dz);
            Vector3 place = newOrigin + new Vector3(0f, 0f, zNudge);
            // [R0a] NoMonsters to match the ScoreSpot gate that called us: we relocate out of world/brush-entity
            // embedding only, never off a live player hull (a player can't permanently displace a spawnpoint by
            // standing on it — but a closed door still counts as solid, so we don't relocate INTO one either).
            TraceResult t = Api.Trace.Trace(place, PlayerMins, PlayerMaxs, place, MoveFilter.NoMonsters, forPlayer);
            if (!t.StartSolid)
            {
                // Commit the relocation onto the spot (QC setorigin in relocate_spawnpoint). Persists across
                // selections because GatherSpawnPoints re-reads the same live spot entity each time.
                if (Api.Services is not null)
                    Api.Entities.SetOrigin(spot, newOrigin);
                else
                    spot.Origin = newOrigin;
                return true;
            }
        }
        return false; // QC: "could not get out of solid at all!" — spot is unusable
    }

    private static SpawnPoint ToSpawnPoint(Entity spot)
    {
        // QC never spawns tilted even if the spot says to: angles_z = 0.
        Vector3 a = spot.Angles;
        return new SpawnPoint(spot.Origin, new Vector3(a.X, a.Y, 0f), spot.Team, spot);
    }

    /// <summary>
    /// Port of <c>PutPlayerInServer</c> (server/client.qc), Godot-free core: reset the player's physics
    /// state, give the starting loadout, clear the dead state, and place it at <paramref name="sp"/>.
    /// Faithfully mirrors the QC field assignments in order; deferred mechanics flagged inline.
    /// </summary>
    public static void PutPlayerInServer(Player p, SpawnPoint sp, bool warmup = false)
    {
        // --- physics / solidity (QC: set_movetype WALK; solid SLIDEBOX; takedamage AIM; flags FL_CLIENT) ---
        p.MoveType = MoveType.Walk;
        p.Solid = Solid.SlideBox;          // QC SOLID_SLIDEBOX (a moving player hull; not SOLID_BBOX)
        p.TakeDamage = DamageMode.Aim;     // QC DAMAGE_AIM (autoaim-eligible, takes damage)
        p.Flags = EntFlags.Client;         // QC this.flags = FL_CLIENT | FL_PICKUPITEMS (no FL_PICKUPITEMS enum yet)
        p.DeadState = DeadFlag.No;         // QC this.deadflag = DEAD_NO

        // QC: the live edict is REUSED on respawn (the corpse is a separate CopyBody clone), so it must come
        // back as a clean LIVE player. DamageSystem.Killed set IsCorpse=true (routing further hits to the corpse
        // path) and may have gibbed it (Alpha=-1) or set the corpse ballistics density — none of which were ever
        // reset. Without this, after the first death+respawn the player stayed flagged a corpse forever: it could
        // never re-enter PlayerDamage (so it couldn't die again or award a frag) and was bullet-penetrable.
        p.IsCorpse = false;
        // [W14b Stage 4] QC PutPlayerInServer: animdecide_setstate(this, 0, false) (player.qc:150) clears the death
        // anim state on (re)spawn. The port's upper-action latch carries the DIE1/DIE2 overlay (set on death, never
        // windowed), so it MUST be cleared here or GetUpperAnim would keep returning DEAD priority on the live
        // respawned player. None = the static aim pose until the spawned weapon's raise re-latches DRAW.
        p.AnimUpperAction = AnimDecide.AnimUpperAction.None;
        p.AnimActionStart = 0f;
        // [sv-antilag.clear.on_spawn] QC PutClientInServer fires antilag_clear(this, CS(this)) (client.qc:858)
        // so the freshly-(re)spawned player's lag-comp ring is wiped — a shot can't rewind it toward its old
        // (pre-respawn) position for the next ~0.4s. The net driver honors this flag on its next record pass.
        // Explicit (vs the origin-jump heuristic) so even a respawn landing near the previous origin clears.
        p.AntilagNeedsClear = true;
        p.Alpha = MutatorHooks.DefaultPlayerAlpha; // QC default_player_alpha (client.qc:788) — cloaked/running-guns lower this
        p.BallisticsDensity = 0f;          // QC live-player density (corpse density reset)
        p.RespawnFlags = RespawnFlag.None; // QC this.respawn_flags = 0
        p.RespawnTimeMax = 0f;
        p.RespawnCountdown = 0;
        // QC client.qc:676 this.damageforcescale = autocvar_g_player_damageforcescale (default 2). Every knockback
        // consumer (base damage push AND the globalforces mutator) multiplies by the victim's damageforcescale, so
        // a player spawned with 0 here takes NO knockback — must seed it on (re)spawn.
        p.DamageForceScale = Cvar("g_player_damageforcescale", 2f);

        // QC client.qc:776-781: a player who was a SPECTATOR (killcount == FRAGS_SPECTATOR) is now joining the
        // match — clear its score and stamp startplaytime = time. The port models the spectator sentinel with the
        // STATUS .frags field (FragsStatus == FragsSpectator), which an observing/connecting client carries
        // (ClientManager sets it on connect) and which is reset to FragsPlayer just below. Read it BEFORE that
        // reset so a genuine spectator→player transition (a fresh Join) stamps the per-client playtime origin,
        // while a mid-life respawn (FragsStatus already FragsPlayer) leaves StartPlayTime untouched. This backs the
        // kick_teamkiller rate denominator (time - startplaytime) faithfully for mid-match joiners.
        if (p.FragsStatus == Player.FragsSpectator)
            p.StartPlayTime = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC: this.frags = FRAGS_PLAYER on every (re)spawn. The .frags field is the player STATUS sentinel
        // (FRAGS_PLAYER / FRAGS_SPECTATOR / FRAGS_OUT_OF_GAME), NOT the match score — the running score lives in
        // the real score table (Player.ScoreFrags -> GameScores SP_SCORE), so resetting status here no longer
        // wipes score progress. Restores the QC semantics the scores-table port enables.
        p.FragsStatus = Player.FragsPlayer;

        // --- starting loadout (QC PutPlayerInServer: warmup vs non-warmup branch) ---
        if (warmup)
            ApplyWarmupLoadout(p);
        else
            ApplyStartLoadout(p);

        // --- respawn bookkeeping cleared (QC: death_time/respawn_flags/respawn_time = 0) ---
        p.RespawnTime = 0f;

        // QC client.qc:735 `this.spawnpoint_targ = NULL;` — a target_spawnpoint forced-spawn redirect is
        // consumed by THIS spawn (SelectSpawnPoint short-circuits to it), so clear it now or every subsequent
        // respawn would keep snapping back to the same forced spot.
        p.SpawnPointTarg = null;

        // --- think cleared: "players have no think function" (QC setthink func_null; nextthink 0) ---
        p.Think = null;
        p.NextThink = 0f;

        // --- orientation + motion reset (QC angles = spot.angles; angles_z = 0; velocities/punch cleared) ---
        p.Angles = sp.Angles;              // already roll-zeroed in ToSpawnPoint
        // QC PutPlayerInServer: this.fixangle = true — the (re)spawn forcibly turns the view to the spawn-spot
        // facing. The client owns its view angles (prediction), so p.Angles alone never reaches the camera (and is
        // overwritten by the client's input view angles on the very next live tick). Latch the spawn facing in the
        // QC .fixangle channel (reused for teleporters) so the client snaps the view to it on the respawn edge.
        p.FixAngle = true;
        p.FixAngleAngles = sp.Angles;
        p.Velocity = Vector3.Zero;
        p.OldOrigin = Vector3.Zero;        // recomputed below after placement
        p.AVelocity = Vector3.Zero;
        p.PunchAngle = Vector3.Zero;
        p.WaterLevel = 0;                  // QC WATERLEVEL_NONE
        p.WaterType = (int)Contents.Empty; // QC CONTENT_EMPTY

        // --- bbox + placement (QC setsize PL_MIN/MAX; setorigin spot.origin + '0 0 1'*(1-mins.z-24)) ---
        if (Api.Services is not null)
            Api.Entities.SetSize(p, PlayerMins, PlayerMaxs);
        else
        {
            p.Mins = PlayerMins; p.Maxs = PlayerMaxs; p.Size = PlayerMaxs - PlayerMins;
        }

        // QC PutPlayerInServer: this.view_ofs = STAT(PL_VIEW_OFS) — seed the STANDING eye height. UpdateCrouch only
        // re-sets ViewOfs on a crouch EDGE, so without this the (re)spawned server player's eye sits at the origin
        // (ViewOfs 0) and W_SetupShot fires ~35u too LOW until the first crouch cycle. Reset the duck state too so
        // it matches the standing hull set above (a respawn while ducked must not carry the crouch eye/hull).
        p.IsDucked = false;
        p.ViewOfs = XonoticGodot.Common.Physics.PlayerPhysics.StandViewOfs;

        // QC nudge: 1 - mins.z - 24 = 1 - (-24) - 24 = 1 qu above the marker, so the hull clears the floor.
        float zNudge = 1f - PlayerMins.Z - 24f;
        Vector3 origin = sp.Origin + new Vector3(0f, 0f, zNudge);
        if (Api.Services is not null)
            Api.Entities.SetOrigin(p, origin);
        else
            p.Origin = origin;

        // QC: "don't reset back to last position even if new position is stuck in solid".
        p.OldOrigin = p.Origin;

        // Spawn-event particle flash (QC PutClientInServer → the ENT_CLIENT_SPAWNEVENT entity each client
        // renders via boxparticles(EFFECT_SPAWN, ...), client/spawnpoints.qc:60). Networking a plain effect
        // burst is the port's simpler equivalent — the client-side cl_spawn_event_particles gating collapses
        // to the effect either rendering or not. The burst is TEAM-COLORED exactly like Base
        // (client/spawnpoints.qc:58: `tcolor = teamplay ? Team_ColorRGB(teamnum) : entcs_GetColor(entnum-1)`):
        // in teamplay the spot's team flash color, in FFA the player's own pants-palette color. EffectEmitter's
        // tint range networks through EffectNetProtocol (colorMin/colorMax), and a zero vector = "no override",
        // so a neutral/uncolored spawn (white) is sent untinted — exactly Base's '1 1 1' fallthrough no-op.
        Vector3 spawnTint = SpawnEventTint(p);
        if (spawnTint == new Vector3(1f, 1f, 1f))
            EffectEmitter.Emit("SPAWN", p.Origin);          // white → untinted (matches the '1 1 1' no-op)
        else
            EffectEmitter.Emit(Effects.ByName("SPAWN"), p.Origin, Vector3.Zero, 1, spawnTint, spawnTint);

        // Spawn SOUND (QC client/spawnpoints.qc:64 `sound(this, CH_TRIGGER, SND_SPAWN, VOL_BASE, ATTEN_NORM)`),
        // the audio half of the SpawnEvent. Base gates the whole event send on g_spawn_alloweffects (default 3 =
        // particles | sound); the client then plays the sound when `cl_spawn_event_sound && (alloweffects & BIT(1))`.
        // Model that server-side: emit SND_SPAWN on CH_TRIGGER when alloweffects has bit1 set (the client cvar is
        // default-on, so this is the live gate). Played on the player so it follows the spawn origin like the burst.
        if (Api.Services is not null && ((int)CvarOr("g_spawn_alloweffects", 3f) & 2) != 0)
            SoundSystem.PlayOn(p, Sounds.ByName("SPAWN"), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);

        // QC client.qc:630 `this.effects = EF_TELEPORT_BIT | EF_RESTARTANIM_BIT;` — set the teleport-sparkle +
        // anim-restart effect bits on the (re)spawned player model. The client csqcmodel hooks consume these to
        // play the teleport flash and restart the player's animation on the respawn edge. Both bits are networked
        // through Entity.Effects. OR'd (not assigned) so a powerup/instagib glow applied elsewhere on the spawn
        // path isn't clobbered — those bits are re-applied by their own mutator hooks regardless of order here.
        p.Effects |= EffectFlags.Teleport | EffectFlags.RestartAnim;

        // --- spawn shield (QC PutPlayerInServer ~659/674: StatusEffects_apply(SpawnShield, this, shieldtime)) ---
        // The damage pipeline reads the shield off Entity.SpawnShieldExpire (an absolute sim time), so set it
        // here to now + g_spawnshieldtime. Firing a weapon clears it (handled by the weapon/damage side).
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        // QC client.qc:690 `this.spawn_time = time;` — stamp the player's most-recent spawn time for the CTS
        // trigger_multiple per-client wait buffer ("haven't respawned since triggering" check in multi_trigger).
        p.SpawnTime = now;
        // Honor an explicit g_spawnshieldtime 0 (disable the shield) — CvarOr distinguishes unset from 0 (SPAWN5).
        p.SpawnShieldExpire = now + CvarOr(CvarSpawnShieldTime, DefSpawnShieldTime);

        // QC client.qc:661-664 PRIMES the rot/regen pause timers on spawn (rather than zeroing) so a player
        // spawned above the stable point (e.g. a 200 HP CA/LMS start) holds for a few seconds before rotting.
        // These live on the Entity (DamageEntityState) shared with the regen tick + the pickup path (REGEN3).
        // CvarOr honors an explicit 0 (a host disabling a spawn pause), unlike the 0-fallback Cvar helper.
        p.PauseRegenFinished     = now + CvarOr("g_balance_pause_health_regen_spawn", 0f); // shared (incl. fuel regen)
        p.PauseRotHealthFinished = now + CvarOr("g_balance_pause_health_rot_spawn", 5f);
        p.PauseRotArmorFinished  = now + CvarOr("g_balance_pause_armor_rot_spawn", 5f);
        p.PauseRotFuelFinished   = now + CvarOr("g_balance_pause_fuel_rot_spawn", 10f);

        // QC client.qc:665-669: if (!sv_ready_restart_after_countdown && time < game_starttime), extend each
        // primed pause timer (and the spawn shield) by (game_starttime - time) so timers don't elapse while
        // the pre-match countdown is running and the player is frozen. Without this a player who spawns at the
        // start of a countdown has already burned through their rot-pause by the time the match goes live.
        // StartItem.GameStartTimeProvider is the same host-wired seam DamageSystem/CampcheckMutator etc. use
        // for game_starttime; sv_ready_restart_after_countdown is read via the cvar facade (default 0).
        float gameStartTime = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
        if (gameStartTime > now)
        {
            bool afterCountdown = CvarOr("sv_ready_restart_after_countdown", 0f) != 0f;
            if (!afterCountdown)
            {
                float countdownRemaining = gameStartTime - now;
                // QC client.qc:667-671 extends ONLY shieldtime + pauserotarmor + pauserothealth + pauseregen by f.
                // pauserotfuel_finished is deliberately NOT extended (fuel rot keeps elapsing through the countdown).
                p.PauseRegenFinished     += countdownRemaining;
                p.PauseRotHealthFinished += countdownRemaining;
                p.PauseRotArmorFinished  += countdownRemaining;
                // Extend the spawn shield by the same offset (QC client.qc:667: shieldtime += f before apply).
                p.SpawnShieldExpire += countdownRemaining;
            }
        }

        // QC client.qc:674: StatusEffects_apply(STATUSEFFECT_SpawnShield, this, shieldtime, 0). Base's player
        // spawn shield IS the SpawnShield status effect — which is what produces the EF_ADDITIVE|EF_FULLBRIGHT
        // shimmer (SpawnShieldTick, once time >= game_starttime) and is read by SpawnEffects-aware mutators
        // (nades nade_refire, spawn-near-teammate). The port keeps the authoritative damage-block on
        // Entity.SpawnShieldExpire (woven through DamageSystem/WeaponFireGate/Mayhem/Vampire), but ALSO mirrors
        // it into the status effect here so the player shimmer + the StatusEffects_active(SpawnShield) readers
        // are faithful — not monster-only. OnPlayerPostThink removes the effect the instant the shield lapses
        // or is consumed (fire / first damage zero SpawnShieldExpire). Only meaningful while the shield is live.
        if (StatusEffectsCatalog.SpawnShield is { } shieldDef && p.SpawnShieldExpire > now)
            StatusEffectsCatalog.Apply(p, shieldDef, p.SpawnShieldExpire - now);

        // --- player model (QC PutPlayerInServer: this.model = ""; FixPlayermodel(this) → setplayermodel;
        //     server/client.qc:720-721 / :241-248) ---
        // sv_defaultplayermodel is the universal default ("models/player/erebus.iqm"). With sv_defaultcharacter 0
        // (stock) the model is really the client's own `playermodel` cvar, which isn't plumbed client→server yet —
        // so every player AND bot resolves to the same default for now (a faithful stand-in; QC also falls back to
        // erebus.iqm for an empty/invalid choice). The model is networked by NAME (NetEntityState.Model) so the
        // client loads the skeletal IQM. The hull set by SetSize above is unchanged (a Player keeps its mins/maxs).
        string playerModel = CvarStr(CvarDefaultPlayerModel);
        if (string.IsNullOrEmpty(playerModel))
            playerModel = DefaultPlayerModel;
        if (Api.Services is not null)
            Api.Entities.SetModel(p, playerModel);
        else
            p.Model = playerModel;
        p.Skin = Cvar(CvarDefaultPlayerSkin, 0f);

        // NOTE (client/networking): weapon view entities (CL_SpawnWeaponentity), SpawnEvent networking, the bot
        // aim reset and fixangle/v_angle are client-render / bot-AI concerns handled by the renderer and bot
        // agents; the server-authoritative owned-weapon set + the player model (above) are applied here.

        // QC wr_resetplayer (server/client.qc:800 FOREACH(Weapons, true, it.wr_resetplayer(...))): Base runs
        // wr_resetplayer for EVERY registered weapon on respawn, not just the held one (e.g. Hagar clears loaded
        // rockets, porto clears porto_current). The held-weapon dispatch below covers the common case; the porto
        // latch in particular must be cleared on respawn even when the player isn't holding porto (a placed/queued
        // porto from a prior life would otherwise keep the single-portal latch set), so dispatch porto's reset
        // unconditionally for an owned-but-not-held porto.
        Weapon? current = null;
        for (int slot = 0; slot < WeaponFireDriver.MaxWeaponSlots; ++slot)
        {
            Weapon? wep = null;
            if (slot == 0)
                wep = current = Inventory.CurrentWeapon(p); // the active weapon after SwitchToBest above
            // Slot 0 is populated; higher slots (future dual-wield) are empty. Call WrResetPlayer for all populated slots.
            if (wep is not null)
            {
                var weaponentity = new WeaponSlot(slot);
                wep.WrResetPlayer(p, weaponentity);
            }
        }
        if (Registry<Weapon>.ByName("porto") is { } porto && porto != current)
            porto.WrResetPlayer(p, new WeaponSlot(0));
    }

    /// <summary>
    /// QC <c>readplayerstartcvars()</c> (server/world.qc:1983) — the SetWeaponArena/SetStartItems seam.
    /// Builds the start-of-life loadout globals (start_health / start_armorvalue / start_ammo_* /
    /// start_weapons / start_items), firing BOTH mutator hooks gametypes/arena mutators subscribe to:
    /// <list type="number">
    /// <item><c>MUTATOR_CALLHOOK(SetWeaponArena, s)</c> — a gametype/mutator picks the arena string
    ///   (CA/LMS "most", Mayhem "most_available", or "off" from instagib/overkill/melee). The resulting
    ///   string is EXPANDED into <see cref="StartLoadout.Weapons"/> via <see cref="ExpandWeaponArena"/>,
    ///   and when an arena is active QC ORs in IT_UNLIMITED_AMMO | IT_UNLIMITED_SUPERWEAPONS (world.qc:2089).</item>
    /// <item><c>MUTATOR_CALLHOOK(SetStartItems)</c> — arena/loadout mutators rewrite health/armor/ammo +
    ///   the weapon set (weaponarena_random randomizes start_weapons, new_toys swaps in the new-toy
    ///   variants, overkill sets the OK set; CA/Mayhem set 200/200 + full ammo).</item>
    /// </list>
    /// QC computes these once at match config; the port computes them per-spawn (handlers are deterministic,
    /// so the result is identical) and feeds the result into <see cref="ApplyStartLoadout"/>. The stock
    /// defaults (Blaster + zero ammo, no arena) mirror balance-xonotic.cfg, so with no mutator this is the
    /// plain DM loadout.
    /// </summary>
    public static StartLoadout ComputeStartItems()
    {
        var l = new StartLoadout
        {
            Health = Cvar(CvarHealthStart, DefHealthStart),
            Armor  = Cvar(CvarArmorStart,  DefArmorStart),
            AmmoShells  = Cvar(CvarAmmoShells,  0f),
            AmmoBullets = Cvar(CvarAmmoNails,   0f),
            AmmoRockets = Cvar(CvarAmmoRockets, 0f),
            AmmoCells   = Cvar(CvarAmmoCells,   0f),
            AmmoFuel    = Cvar(CvarAmmoFuel,    0f),
            // QC world.qc:2140 (no-arena branch): warmup_start_ammo_fuel = cvar("g_warmup_start_ammo_fuel").
            // An active arena overrides this to mirror the live start fuel below (world.qc:2127).
            WarmupAmmoFuel = Cvar("g_warmup_start_ammo_fuel", 0f),
        };

        // QC world.qc:2004-2006: s = cvar_string("g_weaponarena"); MUTATOR_CALLHOOK(SetWeaponArena, s);
        // The arena string defaults to the g_weaponarena cvar; a gametype/mutator overrides it in its hook
        // (CA/LMS force "most", Mayhem "most_available"; instagib/overkill/melee force "off").
        var arenaArgs = new MutatorHooks.SetWeaponArenaArgs(CvarStr("g_weaponarena"));
        MutatorHooks.SetWeaponArena.Call(ref arenaArgs);
        WepSet arena = ExpandWeaponArena(arenaArgs.Arena, out bool arenaActive);

        if (arenaActive)
        {
            // QC world.qc:2085-2091: start_weapons = g_weaponarena_weapons; the arena REPLACES the stock
            // start loadout and grants unlimited ammo + non-expiring superweapons for the whole arsenal.
            l.Weapons.Clear();
            foreach (Weapon w in Weapons.All)
                if (arena.Has(w)) l.Weapons.Add(w.NetName);
            l.ItemFlags.Add("UNLIMITED_AMMO");
            l.ItemFlags.Add("UNLIMITED_SUPERWEAPONS");
            // QC world.qc:2127: with an active weapon arena, warmup mirrors the live start fuel.
            l.WarmupAmmoFuel = l.AmmoFuel;
        }
        else
        {
            // No arena: the normal DM start weapons (just the Blaster sidearm; other weapons are map pickups).
            foreach (string w in DefaultLoadout) l.Weapons.Add(w);
        }

        // QC world.qc:2106-2110: g_balance_superweapons_time < 0 ⇒ IT_UNLIMITED_SUPERWEAPONS;
        // !g_use_ammunition ⇒ IT_UNLIMITED_AMMO (independent of any arena).
        if (CvarOr(CvarSuperweaponsTime, DefSuperweaponsTime) < 0f)
            l.ItemFlags.Add("UNLIMITED_SUPERWEAPONS");
        // CvarOr honors an explicit g_use_ammunition 0 (the 0-fallback Cvar helper would read it back as the
        // default 1 and never grant unlimited ammo).
        if (CvarOr("g_use_ammunition", 1f) == 0f)
            l.ItemFlags.Add("UNLIMITED_AMMO");

        // MUTATOR_CALLHOOK(SetStartItems) — arena/loadout mutators rewrite the loadout (weaponarena_random
        // randomizes start_weapons, new_toys swaps in the new-toy variants, overkill sets the OK set; the
        // gametype SetStartItems handlers — CA/LMS/Mayhem — set 200/200 + full ammo on top of the arena).
        var args = new MutatorHooks.SetStartItemsArgs(l);
        MutatorHooks.SetStartItems.Call(ref args);
        return args.Loadout;
    }

    /// <summary>
    /// Port of the <c>g_weaponarena</c> string → weapon-set expansion in <c>readplayerstartcvars()</c>
    /// (server/world.qc:2009-2083). Resolves the arena keyword (or a space-separated weapon-name list) to the
    /// concrete <see cref="WepSet"/> of weapons every player spawns owning, and reports whether an arena is
    /// active at all (the "off"/"0"/"" cases leave <paramref name="active"/> false so the caller keeps the
    /// stock start loadout). The Wave-2 gametypes (CA/LMS) and the Mayhem family drive this through their
    /// SetWeaponArena hooks; arena-suppressing mutators (instagib/overkill/melee) set "off".
    ///
    /// Keyword set (QC world.qc):
    /// <list type="bullet">
    /// <item><c>""</c> / <c>"0"</c> / <c>"off"</c> ⇒ no arena (active=false).</item>
    /// <item><c>"all"</c> / <c>"1"</c> ⇒ every non-hidden, non-mutator-blocked weapon (<c>weapons_all</c>).</item>
    /// <item><c>"devall"</c> ⇒ literally every weapon (<c>weapons_devall</c>).</item>
    /// <item><c>"most"</c> ⇒ the WEP_FLAG_NORMAL, non-hidden, non-blocked set (<c>weapons_most</c>).</item>
    /// <item><c>"none"</c> ⇒ an active arena with NO weapons (the Blaster-only "No Weapons Arena").</item>
    /// <item><c>"all_available"</c> / <c>"devall_available"</c> / <c>"most_available"</c> ⇒ QC intersects with the
    ///   weapons present on the map; the port has no map-weapon set, so it uses the no-map-weapons fallback
    ///   QC itself uses (the full all/devall/most set). Flagged as a divergence in the spec.</item>
    /// <item>anything else ⇒ tokenized as a weapon-name list (each <see cref="Weapon.NetName"/> OR'd in).</item>
    /// </list>
    /// </summary>
    public static WepSet ExpandWeaponArena(string arena, out bool active)
    {
        active = false;
        var set = new WepSet();
        if (string.IsNullOrEmpty(arena) || arena == "0" || arena == "off")
            return set; // no arena — caller keeps the stock start weapons

        active = true;
        switch (arena)
        {
            case "all":
            case "1":
            case "all_available":          // no map-weapon set in the port → QC's no-map fallback = weapons_all
                foreach (Weapon w in Weapons.All)
                    if (!HasAny(w, WeaponFlags.MutatorBlocked | WeaponFlags.Hidden)) set.Add(w);
                break;
            case "devall":
            case "devall_available":       // → weapons_devall (literally every weapon)
                foreach (Weapon w in Weapons.All) set.Add(w);
                break;
            case "most":
            case "most_available":         // → weapons_most (WEP_FLAG_NORMAL, non-hidden, non-blocked)
                foreach (Weapon w in Weapons.All)
                    if (HasAny(w, WeaponFlags.Normal) && !HasAny(w, WeaponFlags.MutatorBlocked | WeaponFlags.Hidden))
                        set.Add(w);
                break;
            case "none":
                break;                     // "No Weapons Arena": active, but empty (Blaster-only)
            default:
                // QC tokenize: a space-separated weapon-name list. Unknown tokens are skipped (QC WEP_Null).
                foreach (string tok in arena.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
                    if (Weapons.ByName(tok) is { } wep) set.Add(wep);
                break;
        }
        return set;
    }

    /// <summary>QC <c>(weaponinfo.spawnflags &amp; flags)</c> test — any of the given <see cref="WeaponFlags"/> bits set.</summary>
    private static bool HasAny(Weapon w, int flags) => (w.SpawnFlags & flags) != 0;

    /// <summary>
    /// Translate the loadout's string <see cref="StartLoadout.ItemFlags"/> tags (the UPPERCASE convention the
    /// SetStartItems/SetWeaponArena handlers use — "UNLIMITED_AMMO", "UNLIMITED_SUPERWEAPONS", "FUEL_REGEN",
    /// "JETPACK") into the concrete <see cref="ItemFlag"/> bits OR'd onto QC's <c>start_items</c>. Unknown tags
    /// are ignored. This is what lets a weapon arena's unlimited ammo / the hook mutator's fuel-regen actually
    /// reach the spawned player's <see cref="Entity.Items"/>.
    /// </summary>
    private static int StartItemFlagBits(StartLoadout l)
    {
        ItemFlag bits = ItemFlag.None;
        foreach (string tag in l.ItemFlags)
        {
            bits |= tag switch
            {
                "UNLIMITED_AMMO"          => ItemFlag.UnlimitedAmmo,
                "UNLIMITED_SUPERWEAPONS"  => ItemFlag.UnlimitedSuperweapons,
                "FUEL_REGEN"              => ItemFlag.FuelRegen,
                "JETPACK"                 => ItemFlag.Jetpack,
                _ => ItemFlag.None,
            };
        }
        return (int)bits;
    }

    /// <summary>
    /// QC starting-resource block of PutPlayerInServer: SetResource for health/armor and the five ammo
    /// pools, plus STAT(WEAPONS) = start_weapons. Sources the values from <see cref="ComputeStartItems"/>
    /// (the SetStartItems seam) so arena mutators take effect; the defaults match balance-xonotic.cfg.
    /// Armor caps at the per-entity limit via <see cref="Resources.SetResource"/>.
    /// </summary>
    private static void ApplyStartLoadout(Player p)
    {
        StartLoadout start = ComputeStartItems();

        float health = start.Health;
        float armor  = start.Armor;

        // QC keeps MaxHealth as the cap; spawn health is start_health (100), max is 100 unless a pickup raised it.
        p.MaxHealth = health;
        // Health/armor are set explicitly (no waste hooks) like the QC SetResource on spawn.
        p.SetResource(ResourceType.Health, health);
        p.SetResource(ResourceType.Armor,  armor);

        p.SetResource(ResourceType.Shells,  start.AmmoShells);
        p.SetResource(ResourceType.Bullets, start.AmmoBullets);
        p.SetResource(ResourceType.Rockets, start.AmmoRockets);
        p.SetResource(ResourceType.Cells,   start.AmmoCells);
        p.SetResource(ResourceType.Fuel,    start.AmmoFuel);

        // QC STAT(WEAPONS, this) = start_weapons — reset the owned-weapon set to the (seam-computed) loadout.
        // Populate BOTH the NetName set (status / superweapon / HasWeapon checks) AND the canonical WepSet
        // bitset (Entity.OwnedWeaponSet — the authority Inventory + the W_WeaponFrame driver read). The spawn
        // path previously filled only the NetName set, leaving OwnedWeaponSet empty, so the player spawned
        // with no equippable weapon and could never fire once the fire driver started gating on the active weapon.
        p.OwnedWeapons.Clear();
        p.OwnedWeaponSet.Clear();
        foreach (string w in start.Weapons)
        {
            p.OwnedWeapons.Add(w);
            if (Weapons.ByName(w) is { } wep) p.OwnedWeaponSet.Add(wep);
        }

        // GiveRandomWeapons (QC server/items.qc:440): in random-start-weapons modes give N extra weapons
        // drawn at random (without replacement) from g_random_start_weapons. Default count is 0, so this is a
        // no-op in stock DM. Deterministic via the spawn RNG (ADR-0010: no nondeterministic random()).
        // QC client.qc:644 gates this on the ForbidRandomStartWeapons hook: arena-style mutators
        // (instagib/overkill/melee_only/nix) return true and suppress the random-start grant so it can't add
        // weapons on top of their forced loadout.
        int randomCount = (int)Cvar(CvarRandomStartCount, 0f);
        if (randomCount > 0 && !MutatorHooks.FireForbidRandomStartWeapons(p))
            GiveRandomWeapons(p, randomCount, CvarStr(CvarRandomStartWeapons));

        // start_items (QC: this.items = start_items) — the starting powerup/key bitfield. The base value is
        // the g_start_items cvar; the SetWeaponArena/SetStartItems seam ORs in the loadout's IT_* flags
        // (IT_UNLIMITED_AMMO / IT_UNLIMITED_SUPERWEAPONS from a weapon arena, IT_FUEL_REGEN from the hook
        // mutator). Without folding ItemFlags in, an arena's unlimited ammo never reached the player.
        p.Items = (int)Cvar(CvarStartItems, 0f) | StartItemFlagBits(start);

        // QC client.qc: if the loadout includes any superweapon (STAT(WEAPONS) & WEPSET_SUPERWEAPONS), arm the
        // Superweapon status effect for g_balance_superweapons_time so the held superweapon expires on schedule
        // (the per-frame countdown in PlayerFrameLogic strips the weapon when it lapses). Uses the central
        // NetName→superweapon registry (Weapons.OwnsAnySuperWeapon).
        if (Weapons.OwnsAnySuperWeapon(p.OwnedWeapons) && StatusEffectsCatalog.Superweapon is { } sw)
            StatusEffectsCatalog.Apply(p, sw, Cvar(CvarSuperweaponsTime, DefSuperweaponsTime));

        // QC client.qc PutPlayerInServer slot loop (~line 830): equip slot 0's best owned weapon
        // (w_getbestweapon) on every (re)spawn so the W_WeaponFrame driver has an ACTIVE weapon to fire.
        // Without this the player spawns with ActiveWeaponId = -1 and the fire path no-ops — the
        // "press fire and nothing happens" bug on the networked / listen-server path.
        Inventory.SwitchToBest(p);
    }

    /// <summary>
    /// QC <c>GiveWarmupResources</c> (server/client.qc:568) + the <c>warmup_stage</c> branch of PutPlayerInServer:
    /// the gear-up loadout used during warmup — <c>g_warmup_start_health</c> (100) / <c>g_warmup_start_armor</c>
    /// (100) / the warmup ammo pools, and <c>WARMUP_START_WEAPONS</c> = all normal weapons when
    /// <c>g_warmup_allguns</c> (else the normal start loadout). Lets players practice with the full arsenal
    /// before the match goes live (SPAWN3). No-op in normal play (g_warmup ships 0).
    /// </summary>
    private static void ApplyWarmupLoadout(Player p)
    {
        float health = Cvar("g_warmup_start_health", 100f);
        float armor  = Cvar("g_warmup_start_armor", 100f);
        p.MaxHealth = MathF.Max(health, 100f);
        p.SetResource(ResourceType.Health, health);
        p.SetResource(ResourceType.Armor,  armor);
        p.SetResource(ResourceType.Shells,  Cvar("g_warmup_start_ammo_shells", 30f));
        p.SetResource(ResourceType.Bullets, Cvar("g_warmup_start_ammo_nails", 160f));
        p.SetResource(ResourceType.Rockets, Cvar("g_warmup_start_ammo_rockets", 80f));
        p.SetResource(ResourceType.Cells,   Cvar("g_warmup_start_ammo_cells", 30f));

        // QC world.qc:2161-2167: MUTATOR_CALLHOOK(SetStartItems) runs ONCE and maxes warmup_start_ammo_fuel
        // against rotstable for the hook/jetpack fuel-regen grant. ComputeStartItems runs that deterministic
        // seam; use its warmup fuel (rather than the flat g_warmup_start_ammo_fuel cvar) so warmup spawns get
        // the bumped fuel, and fold in its IT_* flags (FUEL_REGEN) below.
        StartLoadout startSeam = ComputeStartItems();
        p.SetResource(ResourceType.Fuel, startSeam.WarmupAmmoFuel);

        p.OwnedWeapons.Clear();
        p.OwnedWeaponSet.Clear();

        // QC world.qc:2130: with an active weapon arena, warmup_start_weapons = start_weapons — warmup mirrors
        // the live arena set (CA/LMS/Mayhem keep their full arsenal in warmup). Run the SetWeaponArena seam to
        // see if an arena is in force before the allguns/normal split below.
        var warmArenaArgs = new MutatorHooks.SetWeaponArenaArgs(CvarStr("g_weaponarena"));
        MutatorHooks.SetWeaponArena.Call(ref warmArenaArgs);
        WepSet warmArena = ExpandWeaponArena(warmArenaArgs.Arena, out bool warmArenaActive);
        var warmFlags = new StartLoadout();
        var warmupLoadout = new StartLoadout();

        if (warmArenaActive)
        {
            foreach (Weapon w in Weapons.All)
                if (warmArena.Has(w))
                {
                    warmupLoadout.Weapons.Add(w.NetName);
                    p.OwnedWeaponSet.Add(w);
                }
            warmFlags.ItemFlags.Add("UNLIMITED_AMMO");
            warmFlags.ItemFlags.Add("UNLIMITED_SUPERWEAPONS");
        }
        else if (CvarBool("g_warmup_allguns", true))
        {
            // QC WARMUP_START_WEAPONS = weapons_all() with allguns (server/world.qc:1953): want_weapon filters
            // ONLY on WEP_FLAG_HIDDEN (+ mutator-blocked) — so superweapons ARE included (they're not hidden)
            // and the hidden Tuba is excluded. Matching that exactly avoids dropping superweapons / leaking Tuba.
            foreach (Weapon w in Weapons.All)
            {
                if ((w.SpawnFlags & WeaponFlags.Hidden) != 0) continue;
                if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) continue;
                warmupLoadout.Weapons.Add(w.NetName);
                p.OwnedWeaponSet.Add(w);
            }
        }
        else
        {
            foreach (string w in DefaultLoadout)
            {
                warmupLoadout.Weapons.Add(w);
                if (Weapons.ByName(w) is { } wep) p.OwnedWeaponSet.Add(wep);
            }
        }

        // QC world.qc:2161-2167: apply MUTATOR_CALLHOOK(SetStartItems) to warmup weapons just as for the live
        // path, rearranging them through mutator-defined replacements (e.g. New Toys swap core→new-toy). Base
        // applies the hook to both start_weapons AND warmup_start_weapons via the same seam (SetStartItems
        // signature doesn't distinguish); the port now matches by firing the hook on the built warmupLoadout.
        var warmupArgs = new MutatorHooks.SetStartItemsArgs(warmupLoadout);
        MutatorHooks.SetStartItems.Call(ref warmupArgs);
        warmupLoadout = warmupArgs.Loadout;

        // Transfer the rearranged warmup weapons into the player's arsenal.
        p.OwnedWeapons.Clear();
        foreach (string w in warmupLoadout.Weapons)
        {
            p.OwnedWeapons.Add(w);
            if (Weapons.ByName(w) is { } wep) p.OwnedWeaponSet.Add(wep);
        }

        // Fold the SetStartItems seam's IT_* flags (e.g. the hook mutator's FUEL_REGEN) into the warmup items
        // so warmup spawns get the same start_items the live path does (QC start_items is shared, world.qc:2165).
        foreach (string flag in startSeam.ItemFlags) warmFlags.ItemFlags.Add(flag);
        p.Items = (int)Cvar(CvarStartItems, 0f) | StartItemFlagBits(warmFlags);
        Inventory.SwitchToBest(p);
    }

    /// <summary>
    /// Port of <c>GiveRandomWeapons</c> (server/items.qc:440), weapon-set half: pick
    /// <paramref name="numWeapons"/> distinct weapons at random (without replacement) from the space-
    /// separated <paramref name="weaponNames"/> list, skipping any the player already owns, and add their
    /// NetNames to <see cref="Player.OwnedWeapons"/>. Each draw uses RandomSelection's uniform pick over the
    /// still-eligible candidates (here all weights are 1, so a plain uniform index), via the deterministic
    /// spawn RNG. Stops early if no eligible weapon remains (QC: RandomSelection_chosen_ent == NULL).
    /// </summary>
    private static void GiveRandomWeapons(Player receiver, int numWeapons, string weaponNames)
    {
        if (numWeapons <= 0 || string.IsNullOrWhiteSpace(weaponNames))
            return;
        string[] potential = weaponNames.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (potential.Length == 0)
            return;

        for (int attempt = 0; attempt < numWeapons; attempt++)
        {
            // Gather the candidates the receiver doesn't already own (RandomSelection_Init + the FOREACH).
            _eligibleScratch.Clear();
            foreach (string w in potential)
                if (!receiver.OwnedWeapons.Contains(w) && !_eligibleScratch.Contains(w))
                    _eligibleScratch.Add(w);
            if (_eligibleScratch.Count == 0)
                return; // QC: chosen_ent == NULL → stop
            string chosen = _eligibleScratch[_rng.Next(_eligibleScratch.Count)];
            receiver.OwnedWeapons.Add(chosen);
            if (Weapons.ByName(chosen) is { } chosenWep) receiver.OwnedWeaponSet.Add(chosenWep);

            // QC: give the chosen weapon's ammo from random_start_ammo — but only when the player has none of
            // that resource yet (don't override ammo a previous random weapon already provided).
            ResourceType ammo = Weapons.AmmoTypeOf(chosen);
            if (ammo != ResourceType.None && receiver.GetResource(ammo) == 0f)
            {
                float amount = RandomStartAmmoFor(ammo);
                if (amount > 0f) receiver.SetResource(ammo, amount);
            }
        }
    }

    /// <summary>QC <c>GetResource(random_start_ammo, res)</c>: the configured random-start ammo for a resource.</summary>
    private static float RandomStartAmmoFor(ResourceType res) => res switch
    {
        ResourceType.Shells  => Cvar(CvarRandomStartShells, 0f),
        ResourceType.Bullets => Cvar(CvarRandomStartBullets, 0f),
        ResourceType.Rockets => Cvar(CvarRandomStartRockets, 0f),
        ResourceType.Cells   => Cvar(CvarRandomStartCells, 0f),
        _ => 0f,
    };

    /// <summary>Scratch list for the GiveRandomWeapons candidate set (single-threaded spawn path).</summary>
    private static readonly List<string> _eligibleScratch = new(16);

    /// <summary>QC <c>bound(lo, v, hi)</c>.</summary>
    private static float QBound(float lo, float v, float hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>
    /// The spawn-event particle-burst tint, faithful to client/spawnpoints.qc:58
    /// (<c>tcolor = teamplay ? Team_ColorRGB(teamnum) : entcs_GetColor(entnum - 1)</c>). In teamplay the burst
    /// flashes the player's team color (<see cref="Teams.ColorRgb"/> = the bright Team_ColorRGB); in FFA it
    /// flashes the player's individual pants-nibble palette color (<see cref="Teams.ColormapPaletteColor"/> over
    /// <c>clientcolors &amp; 15</c>, the same low-nibble entcs_GetColor reads). A neutral/uncolored player resolves
    /// to white, which the caller emits untinted (Base's '1 1 1' fallthrough is a no-op tint).
    /// </summary>
    private static Vector3 SpawnEventTint(Player p)
    {
        if (GameScores.Teamplay)
            return Teams.ColorRgb((int)p.Team);

        // FFA: entcs_GetColor(entnum-1) → colormapPaletteColor(clientcolors & 15, isPants:true). clientcolors is
        // 16*shirt+pants, so the low nibble is the pants color. A 0 colormap (no color chosen) → palette[0] = white.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        return Teams.ColormapPaletteColor(p.ClientColors & 15, now);
    }

    /// <summary>Read a float cvar through the facade, falling back to the documented balance default.</summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>
    /// Read a float cvar, falling back to <paramref name="fallback"/> ONLY when the cvar is unset (empty string)
    /// — an explicit <c>0</c> is honored. Use for cvars where 0 is a meaningful value a host may set (e.g.
    /// <c>g_spawnshieldtime 0</c> to disable the spawn shield), which the plain <see cref="Cvar"/> 0-fallback
    /// would silently override back to the default.
    /// </summary>
    private static float CvarOr(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>Read a string cvar through the facade (empty when unset / no services).</summary>
    private static string CvarStr(string name)
        => Api.Services is null ? "" : Api.Cvars.GetString(name);

    /// <summary>
    /// Read a bool cvar (<c>cvar != 0</c>), falling back to <paramref name="fallback"/> when unset (empty string)
    /// or when no services are wired. Distinguishes "unset" from an explicit "0" like the weapon balance reads,
    /// so a default-on team-spawn cvar (e.g. g_ca_team_spawns 1) isn't read as off before configs load.
    /// </summary>
    private static bool CvarBool(string name, bool fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }
}

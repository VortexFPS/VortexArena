# Team Mayhem (tmayhem) — parity spec

**Base refs:** `common/gametypes/gametype/tmayhem/{tmayhem.qh, tmayhem.qc, sv_tmayhem.qc, sv_tmayhem.qh}` + the shared scoring in `common/gametypes/gametype/mayhem/sv_mayhem.qc` (`MayhemCalculatePlayerScore`)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/TeamMayhem.cs` · `src/VortexArena.Common/Gameplay/GameTypes/Mayhem.cs` (the shared `MayhemScoring` static) · `src/VortexArena.Server/GameWorld.cs` (activation + per-frame drive + limits + reset)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Team Mayhem is the **team variant of Mayhem**: 2–4 teams compete in nonstop FFA-style combat, but scored on a
blend of **damage dealt + frags** rather than the ±1 TDM frag matrix. It is *not* round-based (unlike Clan Arena,
which it superficially resembles). Structurally it is Team-Deathmatch-with-Mayhem-scoring: it borrows TDM's
team-count / team-spawn / point-lead-limit machinery and Mayhem's scoring + combat hooks. The scoring helper
`MayhemCalculatePlayerScore` is literally shared between Mayhem and Team Mayhem (the QC function branches on
`teamplay`; the port shares the static `MayhemScoring` and passes `teamGame:true`).

Activated whenever the server gametype NetName is `tmayhem`. There is **no `cl_tmayhem.qc`** — the mode is purely
authority + shared; it has no mode-specific CSQC/HUD beyond the generic team scoreboard columns.

## Base algorithm (authoritative)

### Identity + registration  (`tmayhem.qh:CLASS(tmayhem)`, `sv_tmayhem.qc:tmayhem_Initialize`)
- `gametype_init`: name "Team Mayhem", netname `tmayhem`, cvar prefix `g_tmayhem`, flags
  `GAMETYPE_FLAG_TEAMPLAY | GAMETYPE_FLAG_USEPOINTS`, defaults string `"timelimit=20 pointlimit=1500 teams=2 leadlimit=0"`.
  `m_legacydefaults "1500 20 2 0"`.
- `tmayhem_Initialize` (MUTATOR_ONADD): `GameRules_teams(true)` (enables teamplay), `GameRules_spawning_teams(g_tmayhem_team_spawns)`,
  `GameRules_limit_score(g_tmayhem_point_limit)`, `GameRules_limit_lead(g_tmayhem_point_leadlimit)`, then
  `InitializeEntity(tmayhem_DelayedInit, INITPRIO_GAMETYPE)`.
- `tmayhem_DelayedInit`: `numteams = g_tmayhem_teams_override >= 2 ? override : g_tmayhem_teams`; then
  `teamplay_bitmask = Team_MapEnts_FindOrSpawn("tmayhem_team", BITS(bound(2, numteams, 4)))` — i.e. 2..4 teams,
  spawning `tmayhem_team` map entities if absent.
- `GameRules_limit_score(limit)` / `_limit_lead(limit)` are **no-ops when limit < 0** (sv_rules.qc:26/36), so the
  stock `-1` cvar defaults leave `fraglimit`/`leadlimit` at whatever the mapinfo set them to (which the
  gametype_init default 1500 / 0 supplies). `0` = explicitly unlimited.

### Shared scoring: MayhemCalculatePlayerScore  (`sv_mayhem.qc:145`)
Recompute how much SP_SCORE the player *should* have, then add the missing delta (idempotent). Reads team cvars
when `teamplay` (Team Mayhem):
- `upscaler = g_tmayhem_scoring_upscaler` (**20**) — score per 1 "frag".
- `frag_weight = g_tmayhem_scoring_kill_weight` (**0.25**) — frags per kill.
- `damage_weight = g_tmayhem_scoring_damage_weight` (**0.75**) — frags per (spawn HP+armor) of damage.
- `disable_selfdamage2score = g_tmayhem_scoring_disable_selfdamage2score` (**0**).
- divisor = `start_health + start_armorvalue` = the mode spawn HP+armor (**200 + 200 = 400** stock).

Scoring method selection (divide-by-zero guard): both weights → method 1; only frag → method 2; only damage →
method 3; neither → return (no scoring).

**Method 1** (both weights, the default):
```
suicide_weight        = 1 + (disable_selfdamage2score / frag_weight)          // harsher suicide penalty when selfdamage2score disabled
playerdamagescore     = (total_damage_dealt / (start_health+start_armorvalue)) * 100 * upscaler * damage_weight
rounded               = rint(playerdamagescore * 10) / 10                      // round to 1 decimal, half away from zero
killcount             = SP_KILLS - SP_TEAMKILLS - (SP_SUICIDES * suicide_weight)
playerkillscore       = killcount * 100 * upscaler * frag_weight
playerscore           = rounded + playerkillscore
scoretoadd            = playerscore - (SP_SCORE * 100)
GameRules_scoring_add_team(scorer, SCORE, floor(scoretoadd / 100))            // ×100/÷100 integer-scaling to dodge float error
```
**Method 2** (frags only): `floor((SP_KILLS - SP_TEAMKILLS - SP_SUICIDES) * upscaler - SP_SCORE)`.
**Method 3** (damage only): like method 1's damage branch, `floor(rounded * upscaler - SP_SCORE*100)/100`.

`GameRules_scoring_add_team(client, SCORE, delta)` = `PlayerTeamScore_Add`: add to the player's SP_SCORE **and**
(team game) the player's team ST_SCORE total. Worked stock examples: 400 dmg, 0 kills → 0.75·20 = **15** score; 1
kill, 0 dmg → 0.25·20 = **5** score.

### Damage accrual: PlayerDamage_SplitHealthArmor  (`sv_tmayhem.qc:147`)
Early-return if `g_tmayhem_scoring_damage_weight == 0`. Spawn-shield: full block (`g_spawnshield_blockdamage >= 1`)
→ return; partial block → scale `total` by `1 - blockdamage`. Compute "useful" damage:
`damage_take = bound(0, M_ARGV(4), health)`, `damage_save = bound(0, M_ARGV(5), armor)`,
`excess = max(0, frag_damage - take - save)`, `total = frag_damage - excess`. Return if `total == 0`.
Then accrue on the scorer:
- **Attacker is a player:** `if (!SAME_TEAM(target, attacker)) attacker.total_damage_dealt += total;`
  `if (SAME_TEAM(target, attacker) || (target==attacker && !disable_selfdamage2score)) attacker.total_damage_dealt -= total;`
  → friendly fire **never credits positively**; teammate / self damage **subtracts**. scorer = attacker.
- **World/environment (no player attacker):** subtract `total` from the victim for the punishable deathtypes
  `DEATH_KILL, DROWN, HURTTRIGGER, CAMP, LAVA, SLIME, SWAMP` (unless `disable_selfdamage2score`). scorer = victim.
- Finally `MayhemCalculatePlayerScore(scorer)`.

(Note this is the one place tmayhem **differs** from FFA mayhem: FFA uses `target != attacker` for the credit
gate; team uses `SAME_TEAM`.)

### Per-kill: GiveFragsForKill (CBC_ORDER_FIRST)  (`sv_tmayhem.qc:207`)
`M_ARGV(2,float) = 0` (zero the direct +1 frag — Team Mayhem doesn't use it), then
`if (IS_PLAYER(attacker)) MayhemCalculatePlayerScore(attacker)`. Returns true.

### Damage_Calculate  (`sv_tmayhem.qc:128`)
For a **live** player target (`IS_PLAYER && !IS_DEAD` — corpses may still be gibbed): nullify damage when
`(g_tmayhem_selfdamage==0 && target==attacker) || frag_deathtype == DEATH_FALL`. **Always** zero mirror damage
(`frag_mirrordamage = 0`) — no mirror damaging in Team Mayhem. (FFA mayhem does NOT zero mirror damage; this line
is tmayhem-only.)

### Other mutator hooks
- **PlayerRegen** (`sv_tmayhem.qc:75`): if `!g_tmayhem_regenerate` zero the regen arg; if `!g_tmayhem_rot` zero the
  rot arg; return `(!regenerate && !rot)`. Stock both **0** → regen+rot fully disabled.
- **SetStartItems** (`sv_tmayhem.qc:60`): strip `IT_UNLIMITED_AMMO|IT_UNLIMITED_SUPERWEAPONS`; add
  `IT_UNLIMITED_AMMO` if `!g_use_ammunition || g_tmayhem_unlimited_ammo`. Set start (and warmup) HP/armor/ammo from
  `g_tmayhem_start_*` (defaults **200/200/60/320/160/180/0** shells/nails/rockets/cells/fuel).
- **SetWeaponArena** (`sv_tmayhem.qc:89`): when the arena arg is "0"/empty, default to `g_tmayhem_weaponarena`
  (**"most_available"** — the full arsenal at spawn).
- **FilterItem** (`sv_tmayhem.qc:95`): powerups allowed per `g_powerups`(-1) + `g_tmayhem_powerups`(**1**); pickup
  items off by default (`g_tmayhem_pickup_items` **0**); when items are on, weapons+ammo can still be stripped
  (`g_tmayhem_pickup_items_remove_weapons_and_ammo` **1**). Identical branch logic to FFA mayhem.
- **ForbidThrowCurrentWeapon** (`sv_tmayhem.qc:84`): always true (cannot drop weapon).
- **reset_map_players** (`sv_tmayhem.qc:217`): zero `total_damage_dealt` for every client on reset.

### Constants (Base defaults, all authority / `set` in gametypes-server.cfg / balance-xonotic.cfg)
| cvar | default | units |
|---|---|---|
| g_tmayhem_scoring_upscaler | 20 | score per frag |
| g_tmayhem_scoring_kill_weight | 0.25 | frags per kill |
| g_tmayhem_scoring_damage_weight | 0.75 | frags per (spawn HP+armor) damage |
| g_tmayhem_scoring_disable_selfdamage2score | 0 | bool |
| g_tmayhem_point_limit | -1 (→ gametype 1500) | score |
| g_tmayhem_point_leadlimit | -1 (→ gametype 0) | score |
| g_tmayhem_teams | 2 | count |
| g_tmayhem_teams_override | 0 | count (≥2 wins) |
| g_tmayhem_team_spawns | 0 | bool |
| g_tmayhem_weaponarena | "most_available" | arena spec |
| g_tmayhem_powerups | 1 | tri-state w/ g_powerups |
| g_tmayhem_pickup_items | 0 | bool |
| g_tmayhem_pickup_items_remove_weapons_and_ammo | 1 | bool |
| g_tmayhem_selfdamage | 0 | bool |
| g_tmayhem_regenerate | 0 | bool |
| g_tmayhem_rot | 0 | bool |
| g_tmayhem_unlimited_ammo | 0 (unset) | bool |
| g_tmayhem_start_health / _armor | 200 / 200 | HP / armor |
| g_tmayhem_start_ammo_shells/nails/rockets/cells/fuel | 60 / 320 / 160 / 180 / 0 | ammo |
| timelimit (gametype_init) | 20 | min |

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `CLASS(tmayhem)` identity | `TeamMayhem` ctor (NetName/DisplayName/TeamGame) + `GameWorld.ActivateGameType` case | live |
| `tmayhem_DelayedInit` team count 2..4 | `TeamMayhem.TeamCount` | override≥2 ? override : teams, clamp 2..4 |
| `GameRules_spawning_teams(g_tmayhem_team_spawns)` | `TeamMayhem.RequestsTeamSpawns` → `ClientManager`/`SpawnSystem.RequestTeamSpawns` | live, default false |
| `MayhemCalculatePlayerScore` (teamplay) | `MayhemScoring.Calculate(.., teamGame:true)` | shared with Mayhem; SP_SCORE delta routes to player + team |
| `PlayerDamage_SplitHealthArmor` (SAME_TEAM) | `MayhemScoring.AccrueSplitHealthArmor(.., teamGame:true)` ← `TeamMayhem.OnSplitHealthArmor` ← `DamageSystem.cs:401` | live |
| `GiveFragsForKill` (zero frag + recompute) | `TeamMayhem.OnDeath` + `Scores.Obituary` OwnsScore-gated | live (Combat.Death bus) |
| `Damage_Calculate` (self/fall nullify + zero mirror) | `TeamMayhem.OnDamageCalculate` ← `DamageSystem.cs:219` | live; zeroes MirrorDamage |
| `PlayerRegen` | `TeamMayhem.OnPlayerRegen` ← `PlayerFrameLogic.cs:46` | live; narrower hook (single disable bool) |
| `SetStartItems` | `TeamMayhem.OnSetStartItems` ← `SpawnSystem.cs:669` | live; HP/armor/ammo set |
| `SetWeaponArena` | `TeamMayhem.OnSetWeaponArena` | **DEAD — no `.Call` site anywhere** |
| `FilterItem` | `TeamMayhem.OnFilterItemDefinition` ← `StartItem.cs:90` | live; classname-based item detection |
| `ForbidThrowCurrentWeapon` | `TeamMayhem.OnForbidThrowCurrentWeapon` ← `WeaponThrowing.cs:149,185` | live |
| point/lead limit + team leader | `TeamMayhem.UpdateLeaderAndCheckLimit` / `PointLimit` / `LeadLimit` ← `GameWorld.DriveGametypeFrame` | live; ST_SCORE leader |
| team tie → overtime | `TeamMayhem.ReportsTie` (TeamTie.TopTwoTied) | live via CheckRulesAndIntermission |
| `reset_map_players` | `TeamMayhem.ResetMapPlayers` ← `GameWorld.cs:2409` | live |
| team-score read-through | `Scores.TeamScoreSource = tm.GetTeamScore` (ST_SCORE) | live |
| menu describe / m_configuremenu / m_parse_mapinfo / map support | NOT IMPLEMENTED | MENUQC / map-pool, deferred |

## Parity assessment

**Gameplay logic + scoring numbers are a faithful, well-tested port.** The shared `MayhemScoring` math is
line-by-line equivalent to `MayhemCalculatePlayerScore` (all three methods, `suicide_weight`, the ×100/floor(÷100)
fixed-point scaling, `Rint` = round half away from zero) and has dedicated unit-test coverage
(`tests/VortexArena.Tests/MayhemScoringTests.cs`) including the Team Mayhem SAME_TEAM friendly-fire rule, the
team-routed score, the zeroed mirror damage, environmental-suicide subtraction, spawn-shield handling, and
`reset_map_players`. Team count, point/lead-limit end-of-match, team-leader, team-tie overtime, team-spawn gating,
and team-score read-through are all wired live in `GameWorld`.

**Gaps (observable):**
- **Players spawn with only the Blaster, not the full `most_available` arsenal** — `MutatorHooks.SetWeaponArena`
  has **zero `.Call` sites** in the entire port, so `OnSetWeaponArena` (faithful in isolation) never runs. This is
  the single biggest gameplay divergence and is **inherited** (also breaks Mayhem/Instagib/Melee/Overkill). It is
  an engine-seam defect, not tmayhem-specific logic.
- **DEATH_CAMP environmental-suicide penalty omitted** — the port models no `camp` deathtype (campcheck mutator is
  not ported), so a campcheck suicide would not subtract from `total_damage_dealt`. Only matters with campcheck
  active (non-default).
- **PlayerRegen hook is narrower than QC** — it returns a single "disable" bool rather than independently zeroing
  the regen vs rot args. Identical observable result with the stock defaults (both off → fully disabled); would
  diverge only if exactly one of `g_tmayhem_regenerate`/`_rot` were enabled (non-default).
- **FilterItem detects powerup/ammo/weapon by hardcoded classname lists** instead of `itemdef.instanceOf*` flags —
  a custom/modded item could be misclassified. Branch order + cvar reads match QC exactly.
- **timelimit=20 gametype default not applied** — the port reads the global `timelimit` cvar; selecting tmayhem
  does not force 20 minutes. **Mitigated:** the global `timelimit` default is itself `20` (`Cvars.cs:108`), so stock
  play matches Base; the gap only bites if a prior mode/admin left a non-20 timelimit that tmayhem would not reset.
- **Menu / map-pool concerns missing** — no `describe` blurb, no point-limit configure slider (200..3000 step 100,
  with the `g_tmayhem_teams_override` tcvar), no `m_isAlwaysSupported`/`m_isForcedSupported` DM/TDM-map gating, no
  `m_parse_mapinfo`/`m_setTeams`. Explicitly deferred (cross-boundary, not gameplay).

**Liveness:** `TeamMayhem.Activate()` is invoked on the live activation path (`GameWorld.ActivateGameType`,
`case TeamMayhem tm`), and every hook except `SetWeaponArena` has a confirmed live `.Call` site. The mode is fully
reachable in a normal match selected by NetName `tmayhem`.

**Intended divergences:** the per-kill routing through the `Combat.Death` bus with `OwnsScore=false` (rather than a
GiveFragsForKill hook) is a deliberate port architecture choice with the same observable result (no ±1 frag; score
== recompute); the narrower PlayerRegen hook is faithful in the default config.

## Verification
- Scoring math: `MayhemScoringTests.cs` — three methods vs hand-computed QC values, idempotency, suicides,
  SAME_TEAM friendly fire, team-routed score, zeroed mirror, environmental suicide, spawn shield, reset, identity
  (TeamCount 2/3/4, point-limit default 1500). **pass** (code + unit tests).
- Liveness: grep confirms `SetWeaponArena.Call` has no call site; `SetStartItems`/`DamageCalculate`/`PlayerRegen`/
  `FilterItemDefinition`/`ForbidThrowCurrentWeapon`/`PlayerDamageSplitHealthArmor` all have live `.Call` sites;
  `TeamMayhem.Activate` is invoked in `GameWorld.ActivateGameType`. **pass** (code).
- Exact in-match score progression and the full match-end flow were not runtime-verified in a live server (code +
  test only).

## Open questions
- Does the host ever set the `timelimit` cvar to 20 when tmayhem is selected (some boot path / mapinfo), or does it
  always inherit the global default? (Affects the timelimit-default gap severity.)
- Are `tmayhem_team` map entities / team colours registered anywhere (the QC `Team_MapEnts_FindOrSpawn`), or does
  the port rely purely on `TeamCount` + the generic team registration? (Presentation/team-color parity.)

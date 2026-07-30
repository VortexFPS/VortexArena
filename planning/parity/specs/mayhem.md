# Mayhem — parity spec

**Base refs:** `common/gametypes/gametype/mayhem/{mayhem.qc, mayhem.qh, sv_mayhem.qc, sv_mayhem.qh}` (no `cl_mayhem.qc` — Mayhem has zero client/CSQC code) · scoring cvars in `gametypes-server.cfg` · loadout cvars in `balance-xonotic.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Mayhem.cs` (gametype + `MayhemScoring`) · `src/VortexArena.Server/GameWorld.cs` (Activate/DriveGametypeFrame/ResetMap wiring) · hook call sites `DamageSystem.cs`, `SpawnSystem.cs`, `StartItem.cs`, `PlayerFrameLogic.cs`, `WeaponThrowing.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Mayhem ("mayhem") is a **free-for-all** deathmatch variant (`GAMETYPE_FLAG_USEPOINTS`, no teams;
gametype_init defaults `timelimit=15 pointlimit=1000 leadlimit=0`). Players spawn with the full weapon
arsenal (weaponarena `most_available`) and high health/armor (200/200), there are no item pickups by
default, self-damage is disabled, and regen/rot are off. The defining mechanic is **score from a blend of
damage dealt + frags**: a kill is worth `kill_weight` frags and dealing damage equal to your spawn
health+armor is worth `damage_weight` frags, both multiplied by `upscaler` (defaults 0.25 / 0.75 / 20). A
running per-player `total_damage_dealt` is accrued in the `PlayerDamage_SplitHealthArmor` hook, and the
player's whole SP_SCORE is **recomputed from scratch** on every damage event and every kill. The mode is
implemented as a self-registering mutator-like gametype (it installs eight mutator hooks while active).
First to the point limit (or lead limit) wins; a timed tie goes to overtime.

## Base algorithm (authoritative)

### Identity + limits  (`mayhem.qh:CLASS(mayhem)`, `sv_mayhem.qc:mayhem_Initialize`)
- `gametype_init(... "Mayhem","mayhem","g_mayhem", GAMETYPE_FLAG_USEPOINTS, "", "timelimit=15 pointlimit=1000 leadlimit=0", ...)`.
  `m_legacydefaults = "1000 20 0"` (pointlimit, timelimit, leadlimit — note the legacy timelimit 20 vs the
  new-default 15). FFA, no teams.
- `mayhem_Initialize` (REGISTER_MUTATOR ONADD): `GameRules_limit_score(g_mayhem_point_limit)` +
  `GameRules_limit_lead(g_mayhem_point_leadlimit)`. The cvars default `-1` ("use the mapinfo/gametype limit"),
  so the effective default limit is 1000 score / 0 lead.
- `m_isAlwaysSupported` → true (Mayhem runs on any map ≥ any size). `m_isForcedSupported` → true on DM or TDM
  maps that don't natively list mayhem. (Map-pool concern.)
- `m_configuremenu` exposes a single slider: Point limit 200..2000 step 100 (`g_mayhem_point_limit`).
- `describe` (MENUQC) is the menu blurb only.

### Start loadout  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, SetStartItems)`)
- Clears `IT_UNLIMITED_AMMO | IT_UNLIMITED_SUPERWEAPONS`; sets `IT_UNLIMITED_AMMO` when `g_use_ammunition` is
  off OR `g_mayhem_unlimited_ammo` is set.
- Sets both the live and warmup start globals: `start_health = 200`, `start_armorvalue = 200`,
  `start_ammo_shells = 60`, `_nails = 320`, `_rockets = 160`, `_cells = 180`, `_fuel = 0` (balance-xonotic.cfg).
- **The weapon set is NOT set here** — it comes from the arena (below).

### Weapon arena  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, SetWeaponArena)`)
- If the incoming arena string is "0"/"" it is replaced with `g_mayhem_weaponarena` (default `"most_available"`).
  This is what grants every player **all available weapons** at spawn. Without it the player would get only
  the stock start weapons.

### Damage nullify  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, Damage_Calculate)`)
- For a LIVE player target only (so corpses can still be gibbed, even by delayed self-damage): if
  (`g_mayhem_selfdamage == 0` AND target == attacker) OR `frag_deathtype == DEATH_FALL`, set `frag_damage = 0`.
- Net effect with defaults: no self-damage, no fall damage; blaster/rocket self-jumps are free.

### Regen / rot  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, PlayerRegen)`)
- If `!g_mayhem_regenerate` zero the regen arg; if `!g_mayhem_rot` zero the rot arg; return
  `(!regenerate && !rot)` (i.e. true = fully handled/disabled when both off, the default). Defaults: both 0 →
  no health/armor regen and no rot.

### Item / powerup filter  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, FilterItem)`)
- Returns true to REMOVE an item. Logic (in order):
  1. powerups enabled (`g_powerups==1` OR (`g_powerups==-1` AND `g_mayhem_powerups==1`)) → keep powerups (false).
  2. powerups disabled (`g_powerups==0` OR `g_mayhem_powerups==0`) → remove powerups (true).
  3. `g_pickup_items==0` → remove everything (true).
  4. `g_mayhem_pickup_items==1` AND `g_mayhem_pickup_items_remove_weapons_and_ammo==1` AND `g_pickup_items<=0`
     → remove ammo + weapon pickups (true).
  5. `g_pickup_items==-1` AND `g_mayhem_pickup_items==0` → remove (true).
- Defaults: `g_mayhem_powerups 1`, `g_mayhem_pickup_items 0`, `_remove_weapons_and_ammo 1`. (Note the shipped
  Base `g_powerups`/`g_pickup_items` default is `-1` = "follow gametype".)

### Forbid weapon throw  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, ForbidThrowCurrentWeapon)`)
- Always returns true: players cannot drop their current weapon.

### Damage accrual  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, PlayerDamage_SplitHealthArmor)`)
- Early-out if `g_mayhem_scoring_damage_weight == 0`.
- Spawn-shield: if shield active and `g_spawnshield_blockdamage >= 1` → return (no accrual); else if active
  and blockdamage set, scale `total *= 1 - blockdamage`.
- Compute the "useful" damage: `damage_take = bound(0, take, RES_HEALTH)`, `damage_save = bound(0, save,
  RES_ARMOR)`, `excess = max(0, frag_damage - take - save)`, `total = frag_damage - excess`. Return if total 0.
- If the attacker is a player: enemy hit → `attacker.total_damage_dealt += total`; self hit (and
  `!disable_selfdamage2score`) → `-= total`. scorer = attacker.
- Else (world/environment): if `!disable_selfdamage2score` AND deathtype ∈ {KILL, DROWN, HURTTRIGGER, CAMP,
  LAVA, SLIME, SWAMP} → `target.total_damage_dealt -= total`. scorer = target.
- Then `MayhemCalculatePlayerScore(scorer)`.

### Score recompute  (`sv_mayhem.qc:MayhemCalculatePlayerScore`)
- Reads the FFA cvars (`g_mayhem_scoring_*`) — or the team cvars `g_tmayhem_scoring_*` when `teamplay`.
- Picks a `scoringmethod` to avoid divide-by-0: 1 = both weights set; 2 = frag weight only; 3 = damage weight
  only; if neither, return.
- **Method 1** (default): `suicide_weight = 1 + (disable_selfdamage2score / frag_weight)`.
  `playerdamagescore = (total_damage_dealt / (start_health + start_armorvalue)) * 100 * upscaler *
  damage_weight`; `roundedplayerdamagescore = rint(playerdamagescore*10)/10`.
  `killcount = SP_KILLS - SP_TEAMKILLS - SP_SUICIDES*suicide_weight`.
  `playerkillscore = killcount * 100 * upscaler * frag_weight`.
  `playerscore = roundedplayerdamagescore + playerkillscore`.
  `scoretoadd = playerscore - SP_SCORE*100`. `GameRules_scoring_add_team(scorer, SCORE, floor(scoretoadd/100))`.
- **Method 2**: `(SP_KILLS - SP_TEAMKILLS - SP_SUICIDES) * upscaler`, delta vs SP_SCORE, floor.
- **Method 3**: damage-only version of method 1 (no kills term).
- The ×100/÷100 is the fixed-point trick to carry one decimal of damage score through integer SP_SCORE.

### Per-kill driver  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, GiveFragsForKill, CBC_ORDER_FIRST)`)
- Sets `M_ARGV(2)=0` (the direct +1 frag is suppressed — Mayhem scores only via the recompute), then
  `MayhemCalculatePlayerScore(frag_attacker)`; returns true. (The kills/teamkills/suicides aux columns are
  still maintained by the normal obituary path so the recompute can read them.)

### Reset  (`sv_mayhem.qc:MUTATOR_HOOKFUNCTION(mayhem, reset_map_players)`)
- On a map/round reset: `FOREACH_CLIENT(true) it.total_damage_dealt = 0;`.

### Frags-remaining suppression (COMMENTED OUT in Base)
- The `Scores_CountFragsRemaining` hook that would suppress the "N frags left" announcer is present but
  `/* */`-commented in current Base, so it is NOT active — the announcer behaves like DM. (Documented because
  the port's class header lists it as "deferred"; in Base it is dead too.)

### Constants (Base defaults)
| cvar | default | units / note |
|---|---|---|
| `g_mayhem_scoring_upscaler` | 20 | score per 1 frag-equivalent |
| `g_mayhem_scoring_kill_weight` | 0.25 | frags per kill |
| `g_mayhem_scoring_damage_weight` | 0.75 | frags per (spawn HP+armor) of damage |
| `g_mayhem_scoring_disable_selfdamage2score` | 0 | bool |
| `g_mayhem_point_limit` | -1 → 1000 | -1 = use gametype default (1000); 0 = unlimited |
| `g_mayhem_point_leadlimit` | -1 → 0 | -1 = use gametype default (0 = none) |
| timelimit (gametype_init) | 15 | minutes (legacydefaults says 20) |
| `g_mayhem_weaponarena` | "most_available" | all weapons |
| `g_mayhem_powerups` | 1 | only consulted when g_powerups==-1 |
| `g_mayhem_pickup_items` | 0 | no item pickups |
| `g_mayhem_pickup_items_remove_weapons_and_ammo` | 1 | |
| `g_mayhem_selfdamage` | 0 | self-damage off |
| `g_mayhem_regenerate` | 0 | regen off |
| `g_mayhem_rot` | 0 | rot off |
| `g_mayhem_unlimited_ammo` | (unset → 0) | |
| `g_mayhem_start_health` | 200 | |
| `g_mayhem_start_armor` | 200 | |
| `g_mayhem_start_ammo_shells` | 60 | |
| `g_mayhem_start_ammo_nails` | 320 | |
| `g_mayhem_start_ammo_rockets` | 160 | |
| `g_mayhem_start_ammo_cells` | 180 | |
| `g_mayhem_start_ammo_fuel` | 0 | |

## Port mapping
- **Gametype class** `Mayhem : GameType` (`Mayhem.cs`). Instantiated via `GameTypes.ByName("mayhem")` in
  `GameWorld.ResolveGameType`, activated in `GameWorld.ActivateGameType` (`case Mayhem m: m.Activate()`),
  driven each frame by `GameWorld.DriveGametypeFrame` (`RespawnDuePlayers()` + `m.RecomputeLeader`), and reset
  by `GameWorld.ResetMap` (`m.ResetMapPlayers`). All LIVE.
- **Scoring math** `MayhemScoring.Calculate` / `.AccrueSplitHealthArmor` / `.GetConfig` / `.Rint` — a verbatim
  port of `MayhemCalculatePlayerScore` + the SplitHealthArmor accrual (methods 1/2/3, the ×100/÷100, the
  suicide weight, the rint-away-from-zero). Shared with `TeamMayhem` (the QC `teamplay` branch).
- **`total_damage_dealt`** → `Player.GtTotalDamageDealt` (server-side field). Zeroed by `ResetMapPlayers`.
- **Eight hooks** installed in `Activate` / removed in `Deactivate`:
  - `Damage_Calculate` → `OnDamageCalculate` via `MutatorHooks.DamageCalculate` — LIVE (DamageSystem.cs:219).
  - `PlayerDamage_SplitHealthArmor` → `OnSplitHealthArmor` via `GameHooks.PlayerDamageSplitHealthArmor` —
    LIVE (DamageSystem.cs:401, fired unconditionally with FragAttacker/FragDeathType/FragDamage).
  - `PlayerRegen` → `OnPlayerRegen` via `MutatorHooks.PlayerRegen` — LIVE (PlayerFrameLogic.cs:46).
  - `SetStartItems` → `OnSetStartItems` via `MutatorHooks.SetStartItems` — LIVE (SpawnSystem.cs:669).
  - `FilterItem` → `OnFilterItemDefinition` via `MutatorHooks.FilterItemDefinition` — LIVE (StartItem.cs:90).
  - `ForbidThrowCurrentWeapon` → `OnForbidThrowCurrentWeapon` via `MutatorHooks.ForbidThrowCurrentWeapon` —
    LIVE (WeaponThrowing.cs:149,185).
  - **`SetWeaponArena` → `OnSetWeaponArena` via `MutatorHooks.SetWeaponArena` — DEAD.** The hook chain has NO
    `.Call` site anywhere in the port (only `.Add`/`.Remove`). Confirmed across Instagib/Melee/Overkill/
    TeamMayhem too — the whole chain is dead.
- **Per-kill** is NOT routed through a `GiveFragsForKill` subscription (the port's Mayhem does not subscribe to
  that chain). Instead `Mayhem.OnDeath` (on the `Combat.Death` bus) calls `MayhemScoring.Calculate`. The QC
  "+1 frag suppression" is achieved differently: under `GameWorld`, `Scores.OwnsScore == false`, so
  `Scores.Obituary` records only the kills/teamkills/suicides aux columns and never writes SP_SCORE; Mayhem's
  recompute is the sole SP_SCORE writer (via `GameScores.AddToPlayer(SCORE, delta)`). Net effect matches QC.
- **Limits**: `PointLimit` (g_mayhem_point_limit, -1/unset → default 1000), `LeadLimit`
  (g_mayhem_point_leadlimit, -1/unset → 0). `RecomputeLeader` enforces both each frame; `ReportsTie` →
  `FfaTie.TopTwoTied` so a timed tie goes to overtime (LIVE via GameWorld checkrules).
- **timelimit=15** gametype default: NOT applied by the port. The port reads the global `timelimit` cvar; no
  code applies the per-gametype `gametype_init`/`m_legacydefaults` default. The point/lead defaults ARE
  hardcoded correctly (1000 / 0).
- **Deferred** (per the class header): the `Scores_CountFragsRemaining` suppression (dead in Base too anyway),
  the menu slider/describe (MENUQC), and `m_isAlwaysSupported`/`m_isForcedSupported` (map-pool gating).

## Parity assessment

### Gaps
1. **Players spawn with only the Blaster, not the full arsenal.** The `SetWeaponArena` hook is dead (no
   `.Call` site), so `g_mayhem_weaponarena = "most_available"` is never applied. The spawn loadout falls back
   to `SpawnSystem.DefaultLoadout = { "blaster" }`. In Base, Mayhem hands every player every available weapon.
   This is the single biggest gameplay divergence — the mode plays almost nothing like Base. (Affects
   `TeamMayhem`, Instagib, Melee-only, Overkill identically — a shared engine-seam defect, not Mayhem-specific
   logic.)
2. **timelimit default 15 is not applied.** Mayhem relies on the global `timelimit` cvar; selecting Mayhem
   does not set the 15-minute default. Pointlimit (1000) and leadlimit (0) ARE correct.
3. **`DEATH_CAMP` (campcheck) environmental suicide is not punished.** The port's
   `IsEnvironmentalSuicide` omits `camp` because the port has no campcheck deathtype constant (camp deaths
   aren't produced). Latent, not currently observable; only matters if a campcheck deathtype is ever added.
   (Documented in code.)

(The earlier draft listed a fourth gap — a "spawn-shield read approximation". On verification this is NOT a
gap: `MayhemScoring.HasSpawnShield` uses `SpawnShieldExpire > now`, the SAME model `DamageSystem.HasSpawnShield`
(DamageSystem.cs:730) uses for the entire damage pipeline. The port's StatusEffects catalog deliberately keeps
spawn-shield on the entity rather than in the catalog, so `StatusEffects_active(SpawnShield)` IS this read.
Equivalence confirmed.)

### Liveness
- The gametype, its frame drive, its reset, and 7 of its 8 hooks are LIVE on the real match path (verified by
  tracing `GameWorld` + each hook's `.Call` site). The 8th hook (`SetWeaponArena`) is DEAD — no call site.

### Intended divergences
- **Per-kill mechanism**: routing the score recompute through `Combat.Death`/`OnDeath` + `OwnsScore=false`
  gating instead of subscribing to a `GiveFragsForKill` hook. The observable result (no ±1 frag, score = the
  recompute) matches Base, so this is a faithful re-architecture rather than a defect.

## Verification
- **Code-traced (high confidence):** gametype instantiation/activation/frame-drive/reset wiring; all eight
  hook subscriptions and their (presence/absence of) `.Call` sites; the scoring math line-by-line vs
  `MayhemCalculatePlayerScore`; the `OwnsScore=false` non-double-count of SP_SCORE; the constant defaults vs
  cfg.
- **Not runtime-verified:** actual in-match score numbers; the spawn-shield equivalence; whether the
  weaponarena defect actually leaves a player blaster-only in a live Mayhem match (strongly implied by the
  code but not observed).

## Open questions
- Is the dead `SetWeaponArena` chain meant to be wired at spawn (the natural fix), or does the port intend a
  different weapon-arena resolution path that simply hasn't reached Mayhem yet? (Engine-seam owner question —
  affects all arena modes/mutators.)
- **Resolved (verifier):** no path applies per-gametype `gametype_init` timelimit defaults. Every gametype
  hardcodes its point/lead limits as C# constants; `CheckRulesAndIntermission` reads `timelimit` purely from
  the global `Cvars.TimeLimitMinutes`. So the timelimit=15 gap is systemic (affects all modes), not a
  Mayhem-specific miss, and will not close without a gametype-default-seeding seam.

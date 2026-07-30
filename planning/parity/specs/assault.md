# Assault — parity spec

**Base refs:** `common/gametypes/gametype/assault/{assault.qc,assault.qh,sv_assault.qc,sv_assault.qh}`
(there is **no** `cl_assault.qc` — Assault has no dedicated CSQC; its only client-visible state rides on the
generic waypoint-sprite + notification systems)
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Assault.cs` ·
`src/VortexArena.Server/GameWorld.cs` (WireObjectiveSpawns / ActivateGameType / MatchEnded / win read) ·
`src/VortexArena.Common/Gameplay/MapObjects/{MapObjectsRegistry.cs,Breakable.cs}` ·
`src/VortexArena.Server/Bot/{BotRoles.cs,BotObjectiveRoles.cs}` ·
`src/VortexArena.Common/Gameplay/Notifications/NotificationsList.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Assault is an asymmetric team gametype (always red vs blue; `teamplay_bitmask = BITS(2)`). One team **attacks**
a chain of map objectives that culminates in a final power core; the other **defends**. The attackers progress
by shooting `func_assault_destructible` walls — each destroyed wall fires a `target_objective_decrease` that
strips health from its linked `target_objective`; a destroyed objective `SUB_UseTargets` the next one, and the
terminal objective targets `target_assault_roundend`. Destroying the core wins the round for the attackers; if
the **timelimit** (`timelimit=20` default, minutes) elapses first the **defenders** win. A full match is **two
rounds with roles swapped**: round two's timelimit is forced to the time the first attackers took to destroy
the core, so the faster destroyer wins overall. In campaign it is a single round.

Activation: `g_assault` (the `as` mutator/gametype). Incompatible with warmup (`warmup_stage = 0`) and with
ready-restart (`sv_ready_restart_after_countdown = 0`; `ReadyRestart_Deny` returns true except in campaign).

## Base algorithm (authoritative)

### Mode registration & scoring layout  (`assault.qh:CLASS(Assault)`, `sv_assault.qh:REGISTER_MUTATOR(as)`)
- Gametype flags `GAMETYPE_FLAG_TEAMPLAY`, default args `timelimit=20`, legacy defaults `"20 0"`,
  `m_isTwoBaseMode()=true`, mapinfo support keyed on a `target_assault_roundend` entity present in the map.
- `MUTATOR_ONADD`: create the three intrusive lists (`g_assault_destructibles`, `_objectivedecreasers`,
  `_objectives`), `GameRules_teams(true)`, `teamplay_bitmask = BITS(2)`, and a scoring layout:
  - **player** primary `SP_ASSAULT_OBJECTIVES` ("objectives", SFL_SORT_PRIO_PRIMARY), secondary `SP_SCORE`.
  - **team** slot `ST_ASSAULT_OBJECTIVES = 1` ("objectives", PRIMARY); `ST_SCORE` (slot 0) is team SECONDARY.
- Constant `ASSAULT_VALUE_INACTIVE = 1000` — the health sentinel for an objective that is not yet active.
- Constant `AS_ROUND_DELAY = 5` (seconds) — the inter-round freeze before round two starts.

### Objective entities & the target graph  (spawnfuncs, `sv_assault.qc:287-394`)
All spawnfuncs early-out with `delete(this)` unless `g_assault`.
- `target_objective`: pushed to `g_assault_objectives`; `.use = assault_objective_use`,
  `.reset = assault_objective_reset` (called immediately → health = INACTIVE), `.spawn_evalfunc =
  target_objective_spawn_evalfunc` (a spawn point inside an active/destroyed objective scores `-1`/unusable).
- `target_objective_decrease`: pushed to `g_assault_objectivedecreasers`; if `.dmg` unset → **`.dmg = 101`**;
  `.use = assault_objective_decrease_use`; health = INACTIVE; `.enemy` resolved at INITPRIO_FINDTARGET via
  `assault_setenemytoobjective` (its `.target` names the objective; >1 or 0 matches → `objerror`).
- `func_assault_destructible`: `spawnflags = 3`, `event_heal = destructible_heal`, pushed to
  `g_assault_destructibles`; team = the **defender** team; `func_breakable_setup(this)` (so it is a breakable
  BSP wall with `.health`, default 100, debris, optional blast).
- `func_assault_wall`: a cosmetic SOLID_BSP wall whose model/solidity toggle each 0.2s in `assault_wall_think`
  based on `this.enemy`'s health (hidden+non-solid once the objective is destroyed). `.enemy` set at
  FINDTARGET.
- `target_assault_roundend`: `.winning = 0`, `.use = target_assault_roundend_use` (sets `winning = 1`),
  `.cnt = 0` (round counter), `.reset = target_assault_roundend_reset` (++cnt, winning=false).
- `target_assault_roundstart`: sets `assault_attacker_team = NUM_TEAM_1`, `.use = assault_roundstart_use`,
  `.reset2`, and runs `assault_roundstart_use_this` at FINDTARGET.
- `info_player_attacker` / `info_player_defender`: spawn points; set `team = NUM_TEAM_1`/`NUM_TEAM_2` then
  chain to `spawnfunc_info_player_deathmatch`. (Teams get swapped every round.)

### Activate an objective  (`assault_objective_use` + `target_objective_decrease_activate`)
`assault_objective_use`: `SetResourceExplicit(this, RES_HEALTH, 100)`, then for each decreaser targeting it,
`target_objective_decrease_activate`. Activation spawns the **waypoint sprite** (`WP_AssaultDefend`, radar icon
`RADARICON_OBJECTIVE`) at the destructible's center, sets the sprite team to `assault_attacker_team`
(`SPRITERULE_TEAMPLAY`), and — for a `func_assault_destructible` — wires the sprite to show
`WP_AssaultDestroy` (attacker view) with max-health/health bars; otherwise `WP_AssaultPush`. The sprite's
`waypointsprite_visible_for_player = assault_decreaser_sprite_visible` hides it once the objective is gone.

### Shoot a wall → decrease the objective  (`assault_objective_decrease_use`)
Triggered when a `func_assault_destructible` is destroyed (via `func_breakable` → its targets). Steps:
1. **Team gate:** `if(actor.team != assault_attacker_team) return;` — only the attacking team counts.
2. **Once-per-round gate:** if `trigger.assault_sprite` exists, `WaypointSprite_Disown(...,
   waypointsprite_deadlifetime)` and unset the sprite; else `return` (already activated, can't re-fire).
3. Read the linked objective's health (`this.enemy`). If active (`< INACTIVE`):
   - If `hlth - this.dmg > 0.5`: `GameRules_scoring_add_team(actor, SCORE, this.dmg)`; `TakeResource(enemy,
     RES_HEALTH, this.dmg)` (objective survives).
   - Else (would drop to ≤0): `GameRules_scoring_add_team(actor, SCORE, hlth)` (score only the remaining
     health), `GameRules_scoring_add_team(actor, ASSAULT_OBJECTIVES, 1)`, set objective health `-1`
     (destroyed), centerprint `enemy.message` to all players if set, and `SUB_UseTargets(enemy, ...)` to fire
     its `.target` (activating the next objective, or the roundend).

### Win condition  (`WinningCondition_Assault`, hooked via `CheckRules_World`)
Run every server frame (the `as` `CheckRules_World` hook returns `WinningCondition_Assault()`):
- If an inter-round `as_round` entity is pending → `WINNING_NO` (game frozen during the 5s delay).
- `WinningConditionHelper(NULL)` (worldstatus). Default: assume the **defending** team wins
  (`SetWinners(team, the-non-attacker)`).
- Find `target_assault_roundend`. If `.winning` (attackers destroyed the core):
  - `bprint` "<attacker> destroyed the objective in <process_time>"; `SetWinners(team, attacker)`;
    set the attacker's `ST_ASSAULT_OBJECTIVES` team score to the **666 sentinel** (so they top the board).
  - If `ent.cnt == 1` (this was the second round) **or** `autocvar_g_campaign` → `WINNING_YES` (match over).
  - Else: `Send_Notification(NOTIF_ALL, ... CENTER_ASSAULT_OBJ_DESTROYED, ceil(time - game_starttime))`,
    spawn `as_round` (think = `as_round_think` at `time + AS_ROUND_DELAY`), `game_stopped = true`, and bump
    `timelimit` so it isn't hit during the freeze.
- The timelimit-defender win is implicit: if attackers never set `winning`, the default `SetWinners(defender)`
  stands and the engine ends the match when `timelimit` elapses (this code never returns WINNING_YES for the
  defenders — the engine's timelimit handler does, with the winners already latched to the defender).

### Second round  (`as_round_think` → `assault_new_round`)
After the 5s delay: `game_stopped = false`, `assault_new_round(roundend)`:
- `++this.winning` (round-2 marker on the roundend ent).
- Swap `assault_attacker_team` (NUM_TEAM_1 ↔ NUM_TEAM_2) and swap every saved non-client team
  (`g_saved_team`).
- `cvar_set("timelimit", ftos(ceil(time - AS_ROUND_DELAY - game_starttime) / 60))` — round two's clock is the
  first attacker's destruction time (in minutes).
- `bprint("Starting second round...")`, `ReadyRestart_force(true)` — fully restarts the map (resets every
  `.reset`/`.reset2`, re-runs roundstart). This is the engine-level round restart.

### Roundstart turret team swap  (`assault_roundstart_use`)
`SUB_UseTargets(this,...)`, then for every turret: unless `assault_turrets_teamswap_forbidden` (set true only
in campaign by `ReadyRestart_Deny`), swap its team NUM_TEAM_1↔2, then `turret_respawn(it)` (also doubles as the
team change). The `as` `TurretSpawn` hook seeds an unteamed turret to `assault_attacker_team` (reversed on
roundstart).

### Notifications & per-spawn role  (`MUTATOR_HOOKFUNCTION(as, PlayerSpawn)`)
On every PlayerSpawn: centerprint `CENTER_ASSAULT_ATTACKING` ("You are attacking!") if the player is on the
attacker team, else `CENTER_ASSAULT_DEFENDING` ("You are defending!"). `CPID_ASSAULT_ROLE` is the center id.
`CENTER_ASSAULT_OBJ_DESTROYED` ("Objective destroyed in %s!") is broadcast on a first-round core destruction.

### Hooks summary
- `PlayHitsound` → true only for `func_assault_destructible` victims (hitsound feedback when shelling a wall).
- `OnEntityPreSpawn` → delete `info_player_team1..4` (Assault uses attacker/defender spawns, not team spawns).
- `VehicleInit` → `nextthink = time + 0.5`. `ReadLevelCvars` → disable warmup + ready-restart-after-countdown.
- `HavocBot_ChooseRole` → `havocbot_ast_reset_role` (offense if on attacker team, else defense).

### Bots  (`havocbot_role_ast_*`, `havocbot_goalrating_ast_targets`)
Offense/defense roles with a 120s role timeout; both rate enemy players, the assault targets, and items. The
target rating finds destructibles whose linked objective is active (`0 < hlth < INACTIVE`), picks the best
nearby PVS-visible waypoint, and bumps `havocbot_attack_time` so the bot commits to the push. Defense rates
enemies at a wider radius (3000 vs 650).

## Port mapping

| Base feature | Port symbol | Live? |
|---|---|---|
| Mode registration + scoring layout | `Assault` ctor + `Activate()` (GameScores layout) | live |
| `ASSAULT_VALUE_INACTIVE = 1000` | `Assault.ObjectiveInactive = 1_000_000` | live (**value differs**) |
| Objective graph entities | `WireObjectiveSpawns` Assault arm + `Add/Stage*`, `ResolveObjectiveGraph` | live |
| `assault_objective_use` / activate | `Assault.ActivateObjective` (+ re-arm decreasers/destructibles) | live |
| Decreaser `.dmg` default 101 | `StageDecreaser(...)` → 101 when 0 | live |
| Wall destroyed → decrease → score | `DamageDestructible`→`DecreaseObjective` (SCORE += removed, ASSAULT_OBJECTIVES +1) | live |
| Objective destroyed → next/roundend | `DecreaseObjective` advances chain → `DestroyFinalObjective` | live |
| Attackers win round (666 sentinel) | `DestroyFinalObjective` sets team slot to 666; `GameWorld.MatchEnded`/win read | live |
| Timelimit → defenders win | `Assault.TimeLimitReached(bool)` | **DEAD (no caller)** |
| Two-round role swap + round-2 clock | `Assault.StartSecondRound()` + `FirstRoundDestroyTime` | **DEAD (no caller)** |
| `info_player_attacker/defender` spawns | `MapObjectsRegistry` retags to `info_player_deathmatch` + team | live |
| Attacker/defender per-spawn centerprint | notification registered only; **no sender** | **DEAD (not sent)** |
| `CENTER_ASSAULT_OBJ_DESTROYED` broadcast | notification registered only; **no sender** | **DEAD (not sent)** |
| Waypoint sprites (WP_AssaultDefend/Push/Destroy + health bars) | none | **NOT IMPLEMENTED** |
| `func_assault_wall` toggle (model/solid by objective) | edict consumed, no behavior | **NOT IMPLEMENTED** |
| Turret roundstart team-swap + respawn | none | **NOT IMPLEMENTED** |
| PlayHitsound for destructibles | none (generic hitsound only) | NOT IMPLEMENTED (parity-neutral) |
| `target_objective_spawn_evalfunc` (objective-aware spawn bias) | none (SpawnSystem targetCheck arg unused for Assault) | **NOT IMPLEMENTED** |
| `destructible_heal` / `event_heal` (walls regen) | none (decrease-only GtEventDamage; no func_breakable_setup) | **NOT IMPLEMENTED** |
| `ReadLevelCvars` disables warmup + ready-restart; `ReadyRestart_Deny` | none (no Assault warmup override) | **NOT IMPLEMENTED** |
| OnEntityPreSpawn drops info_player_team* | not modeled (Assault uses attacker/defender spawns anyway) | n/a |
| Bot offense/defense roles | `BotObjectiveRoles.RoleAssault` (collapsed) via `BotRoles.ChooseRole("as")` | live |
| `func_breakable` substrate | `Breakable.cs` (full port) | live |
| MapInfo: `as`, two-base, timelimit=20 | `MapInfoBackend` maps "assault"→"as"; default timelimit not asserted | partial |

## Parity assessment

**Logic — the core attack chain is faithful and live.** Spawn-order-independent graph resolution (QC
INITPRIO_FINDTARGET) is correctly mirrored; the team gate, once-per-round decreaser gate, per-hit scoring
(SCORE += health removed, ASSAULT_OBJECTIVES +1 on a kill), chain advancement, and the attacker single-round
win (with the 666 team-score sentinel) are all exercised by `AssaultSpawnTests`. The live damage path
(player shell → `Combat.Damage` → `DamageSystem.EventDamage` → `GtEventDamage` → `DamageDestructible`) is
verified.

**Logic — the round/timelimit half is DEAD.** `Assault` exposes `TimeLimitReached(isFinalRound)` and
`StartSecondRound()`, but **nothing calls them**: `DriveGametypeFrame` has no `case Assault` arm, and
`EnableRounds()` is invoked for Assault with **no** `canStart`/`canEnd`/`onRoundStart` callbacks (contrast
Domination, which passes `dom.CanRoundStart, dom.CheckRoundWinner, dom.RoundStart`). Observable consequences:
- If the timelimit elapses without the attackers reaching the core, **the defenders never win** — the match
  does not resolve via Assault (no `TimeLimitReached` call sets `WinningTeam`/`MatchEnded`).
- **There is no second round, and the match never ends on ANY path.** Destroying the core calls
  `DestroyFinalObjective(elapsed, isFinalRound=State.Round>=1)`. The sole caller (`DecreaseObjective`,
  Assault.cs:344) always passes `State.Round>=1`, which is `false` in round 1 and can never become `true`
  because round 2 never begins. So it records `FirstRoundDestroyTime` and returns *without* ending the match
  and without flipping sides; the match hangs in a won-but-not-ended state (attacker `WinningTeam` set,
  `MatchEnded` false). No host code calls `StartSecondRound`.
- **Campaign Assault is also broken (verifier finding).** QC ends a campaign match immediately
  (`WinningCondition_Assault`: `ent.cnt==1 || autocvar_g_campaign` ⇒ `WINNING_YES`). The port has **no
  campaign awareness** at the `DestroyFinalObjective` call site, so even a single-round campaign Assault never
  terminates — it sits in the same limbo. The first-pass note ("the final/campaign round ends the match")
  overstated reality: nothing ever passes `isFinalRound=true`.
- The 5s `AS_ROUND_DELAY` inter-round freeze, the `ReadyRestart_force` map reset, and the turret team swap are
  all part of this dead path / not implemented.

**Values.** `ObjectiveInactive` is `1_000_000` in the port vs `ASSAULT_VALUE_INACTIVE = 1000` in Base. This is
a sentinel used only for "objective not yet active" comparisons (`>= INACTIVE`); both are far above any real
objective health, so within the implemented logic it is behaviorally equivalent — but it is a literal value
mismatch and would diverge if any map set an objective health between 1000 and 1e6. Decreaser default 101 and
destructible default health 100 match. `AS_ROUND_DELAY=5` is not represented (round path dead). The default
`timelimit=20` minutes is not asserted in the port's mapinfo defaults.

**Timing.** The per-hit/destruction logic is event-driven and frame-rate-independent (faithful). The
timelimit countdown, the 5s round delay, and round-2 reclocking are absent (dead path).

**Presentation.** No waypoint sprites at all — the attacker has **no on-screen objective marker, no
health/progress bar, no radar icon** for the objectives (QC `WP_AssaultDefend`/`Push`/`Destroy` +
`WaypointSprite_UpdateHealth`). `func_assault_wall` cosmetic walls never hide/reappear with objective state.
The destructible wall itself does render (it is a real breakable BSP edict) and goes non-solid when destroyed.

**Audio.** No assault-specific audio in Base beyond the `PlayHitsound` true-for-destructible hook (which the
port doesn't special-case — destructibles fall through to generic hitsound handling; effectively
parity-neutral since QC's hook only *enables* the standard hitsound).

**Notifications.** `ASSAULT_ATTACKING`, `ASSAULT_DEFENDING`, and `ASSAULT_OBJ_DESTROYED` are registered in
`NotificationsList.cs` but **never sent** — the only references are the registrations. A `MutatorHooks.PlayerSpawn`
hook DOES exist in the port (Campcheck/Dodging/Damagetext mutators subscribe to it) — the first-pass claim that
"no PlayerSpawn hook exists in any port gametype" was inaccurate. The real gap: `Assault.Activate` subscribes
only to `Combat.Death`, never to `MutatorHooks.PlayerSpawn`, so the per-spawn "You are attacking/defending!"
centerprint never fires. The "Objective destroyed in Xs!" broadcast never fires either (its sole trigger is the
dead round-transition branch).

**Liveness summary.** Live: objective-chain build, activate, shoot→decrease→advance, attacker single-round
win, scoring, attacker/defender spawn points, bot role. Dead/missing: timelimit-defender win, second round +
role swap + reclock, the 5s round freeze, turret team swap, all three notifications, all waypoint sprites,
`func_assault_wall` toggle.

**Intended divergences.** None claimed by the port as deliberate; the dead round half is documented in the
`Assault.cs` header as "Deferred (NOTE — cross-boundary): turret team-swap, the engine round-restart machinery
(ReadyRestart_force), the objective/wall models + waypoint sprites (CSQC), and the score networking/HUD." That
is a known-deferred note, not a deliberate behavioral change, so it is recorded as gaps (not
intended_divergence).

## Verification
- `tests/VortexArena.Tests/AssaultSpawnTests.cs` — chain build, spawn-order-independent resolution, dmg-101
  default, head-objective arming, single-round attacker win, live damage-pipeline path, wrong-team gate,
  attacker/defender spawn retag. (All the *implemented & live* logic above.)
- Liveness of the dead paths established by code reading: grep shows `StartSecondRound`/`TimeLimitReached` have
  no callers; `DriveGametypeFrame` has no Assault arm; `EnableRounds()` for Assault passes no callbacks; the
  three notifications have no `Send`/`NotificationSystem.Send` call site.
- Notification text/args verified equal to Base (`NotificationsList.cs:786-788` vs `all.inc:560-562`).
- The `ObjectiveInactive` value mismatch read directly (`Assault.cs:38` vs `sv_assault.qh:7`).
- Waypoint sprites / func_assault_wall / turret swap: confirmed absent by grep (no `WP_Assault*` symbol, no
  Assault turret-swap, func_assault_wall edict consumed with no behavior).

## Open questions
- Does the host intend to drive the Assault round loop from a yet-unwritten `MatchController`/round callback,
  or should `DriveGametypeFrame` gain a `case Assault` that (a) calls `TimeLimitReached` when the clock
  expires and (b) calls `StartSecondRound` + reclocks the timelimit after a first-round core destruction? The
  POJO API is present and unit-tested; only the live wiring is missing.
- Should `ObjectiveInactive` be brought back to 1000 to match Base exactly (low risk, removes a latent
  map-authored-health edge case)?
- Are the three assault notifications expected to be sent from a future PlayerSpawn hook + the round-destroy
  branch, or are they intentionally suppressed? (Currently dead, no rationale recorded.)

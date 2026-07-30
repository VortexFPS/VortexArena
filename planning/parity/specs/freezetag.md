# Freeze Tag — parity spec

**Base refs:** `common/gametypes/gametype/freezetag/{freezetag.qc,freezetag.qh,sv_freezetag.qc,sv_freezetag.qh,cl_freezetag.qc,cl_freezetag.qh}`
· **Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/FreezeTag.cs`, `src/VortexArena.Server/GameWorld.cs` (drive/wire), `src/VortexArena.Server/RoundHandler.cs` (live round flow), `src/VortexArena.Common/Gameplay/GameTypes/RoundHandler.cs` (FT's own, dead), `src/VortexArena.Net/GametypeStatusBlock.cs`, `game/hud/ModIconsPanel.cs`, `game/hud/CrosshairPanel.cs`, `game/client/ClientWorld.cs`, `game/client/WaypointSpriteLayer.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Freeze Tag is a round-based teamplay gametype (2–4 teams). A fragged player is not removed — instead they are
**frozen** (encased in an ice model, HP forced to 1, movement and weapons locked) and count as eliminated. A
living teammate standing in close proximity slowly **revives** them; damaging a frozen enemy speeds their
auto-revival back up. A team is eliminated when every member is frozen (or dead); the last team standing wins
the round (team score `ST_FT_ROUNDS +1`). The first team to the round limit (mapinfo `pointlimit`, default 10,
overridable by `g_freezetag_teams_override`/fraglimit) wins the match. Rounds open with a ~10 s warmup grace
period during which weapons are locked. Frozen players also auto-thaw after `g_freezetag_frozen_maxtime` (60 s).

## Base algorithm (authoritative)

### Mode registration + scoring fields (`sv_freezetag.qh:REGISTER_MUTATOR(ft)`, `sv_freezetag.qc:freezetag_Initialize`)
- `GameRules_teams(true)`, `GameRules_spawning_teams(g_freezetag_team_spawns=1)`, `GameRules_limit_score(g_freezetag_point_limit=-1 → mapinfo)`, `GameRules_limit_lead(g_freezetag_point_leadlimit=-1 → mapinfo)`.
- Team count: `g_freezetag_teams_override` if ≥2, else `g_freezetag_teams`; clamped `BITS(bound(2,n,4))`.
- Scoring: player primary `SP_SCORE` (freeze/revive ±1); team primary `ST_FT_ROUNDS` ("rounds", sort prio primary); player column `SP_FREEZETAG_REVIVALS` ("revivals").
- `round_handler_Spawn(freezetag_CheckTeams, freezetag_CheckWinner, func_null)` then `round_handler_Init(5, g_freezetag_warmup, g_freezetag_round_timelimit)`.
- `EliminatedPlayers_Init(freezetag_isEliminated)` — networks the eliminated (grey-out) set.
- Gametype default args (`freezetag.qh INIT`): `timelimit=20 pointlimit=10 teams=2 leadlimit=6`; legacy defaults `"10 20 0"`.

### Freeze (`freezetag_Freeze`, `freezetag_Add_Score`)
- **Trigger:** sv, from `PlayerDies` hook (round active, countdown not running) when a player is fragged; also from `PlayerSpawn` for a late joiner / `freezetag_frozen_timeout <= -2` (died) / team-change.
- **Algorithm:** if already frozen, no-op. Set `freezetag_frozen_time = time`; if `revive_auto && frozen_maxtime>0` set `freezetag_frozen_timeout = time + frozen_maxtime`. `STAT(FROZEN)=true`, `STAT(REVIVE_PROGRESS)=0`, `RES_HEALTH=1`, `revive_speed=0`, remove from `g_bot_targets`/`bot_attack=false`, `freeze_time=time`. Spawn `ice` entity: model `MDL_ICE`, random frame `floor(random()*21)` (20 looks), `colormod/glowmod = Team_ColorRGB(team)`, alpha 1, follows owner at `origin - '0 0 16'` each think. Remove grappling hooks aimed at the target. Spawn `WP_Frozen` waypoint sprite (offset `'0 0 64'`). Recount alive. Apply score.
- **Score matrix (`freezetag_Add_Score`):** self-freeze → victim `SCORE -1`; teammate freeze → attacker `-1` + victim `-1`; enemy freeze → attacker `+1` + victim `-1`; NULL / non-player (gametype rules) attacker → **no** score.
- **Constants:** `MDL_ICE` 20-frame model; ice offset `'0 0 16'`; WP offset `'0 0 64'`.

### Unfreeze (`freezetag_Unfreeze`)
- Clears `STAT(FROZEN)`, `STAT(REVIVE_PROGRESS)=0`; if `reset_health` set HP to `start_health` (player) / `max_health`. Sets `pauseregen_finished = time + g_balance_pause_health_regen`. Re-adds to `g_bot_targets`. Kills the waypoint sprite, deletes the ice block, removes hooks aimed at target, clears `freezetag_frozen_time/timeout`.

### Per-frame revive + auto-thaw (`PlayerPreThink CBC_ORDER_FIRST`)
- **Trigger:** sv, every frame per player, but gated: skipped if `game_stopped` or round not started.
- **Range test** `IN_REVIVING_RANGE`: living non-dead same-team teammate whose bbox (expanded by `g_freezetag_revive_extra_size=100`) overlaps the frozen player's bbox.
- Counts nearby revivers `n`. If `revive_time_to_score>0` (DEFAULT 1.5), each reviver accrues `freezetag_revive_time += frametime / revive_time_to_score`; for each whole unit, reviver gets `SCORE +1` (time-based revive scoring).
- `base_progress`: when auto-revive on (`revive_auto && frozen_maxtime>0 && revive_auto_progress`), `bound(0, 1 - (timeout-time)/frozen_maxtime, 1)` — auto-revival floor that ramps as the timeout approaches.
- **No reviver (`n==0`):** if `revive_time_to_score>0`, progress floored to `base_progress` (and merged with manual progress, never cleared — entering/leaving can't stack); else `REVIVE_PROGRESS` decays by `frametime * clearspeed * (1-base_progress)` (clearspeed `g_freezetag_revive_clearspeed=1.6`). Non-frozen idle player's progress tracks `base_progress`. Auto-thaw: if `timeout>0 && time>=timeout`, treat as `n=-1` (auto-revive).
- **Has reviver:** speed `spd = revive_speed_t2s=0.25` when t2s active, else `revive_speed=0.4 * (1-base_progress)`. `REVIVE_PROGRESS += frametime * max(1/60, spd)`. On reaching 1.0: unfreeze (HP `start_health` warmup-aware), set `spawnshieldtime = time + revive_spawnshield=1`, recount. If auto (`n==-1`): `INFO/CENTER_FREEZETAG_AUTO_REVIVED` (+eventlog `:ft:autorevival:`). Else every nearby reviver gets `FREEZETAG_REVIVALS +1`, `SCORE +1` (only if t2s off), nade bonus `g_nades_bonus_score_low`; `CENTER_FREEZETAG_REVIVED`/`REVIVE`, `INFO_FREEZETAG_REVIVED`, eventlog `:ft:revival:`. All revivers' progress mirrored to the player's.
- **Waypoint:** while frozen, `WP_Reviving` (color `WP_REVIVING_COLOR`) if `n>0` or progress>0.95, else `WP_Frozen`; sprite health bar = `REVIVE_PROGRESS` (max 1).

### Damage on frozen players (`Damage_Calculate` hook)
- Saves `freezetag_frozen_armor = current armor` every hit (for soft-kill restore).
- **Auto-revive reduction** (`revive_auto_reducible`, DEFAULT 1): if frozen + auto on + maxtime>0, and (reducible<0 OR enemy attacker) and `timeout>time`: for `|reducible|==1`, accumulate hit force up to `revive_auto_reducible_maxforce=400`, convert via `revive_auto_reducible_forcefactor=0.01`, and subtract from `freezetag_frozen_timeout` (clamped to ≥time). So shooting a frozen enemy speeds their thaw.
- **Frozen takes no health damage:** if frozen & not a NEEDKILL/teamchange deathtype → `frag_damage = 0`, `frag_force *= g_frozen_force=0.6`. Fall-damage revive: if `g_frozen_revive_falldamage>0` (DEFAULT 0/off) and fall damage ≥ threshold → unfreeze with `g_frozen_revive_falldamage_health=40`, ice-shatter effect, notifications.
- **Void/lava soft-kill:** if frozen & NEEDKILL deathtype & `!g_frozen_damage_trigger` (DEFAULT trigger=1 → frozen players DIE in void/lava) → teleport to a spawn point instead of dying (zero velocities, teleport effect). With `g_frozen_damage_trigger=1` (default) the player really dies (then re-frozen on respawn via PlayerDies soft-kill path).

### PlayerDies / respawn (`PlayerDies` hook, sv_freezetag.qc:452)
- Round countdown running: unfreeze (reset_health), recount, immediate forced respawn.
- Otherwise `respawn_time = time+1`, forced. Team-change deathtype: score, recount, `freezetag_frozen_timeout=-2` (freeze on respawn). NEEDKILL deathtype: soft-kill — restore HP=1 + saved armor, relocate to spawn, no weapon/ammo reset. If not already frozen: `freezetag_Freeze`; notifications `INFO_FREEZETAG_FREEZE` (enemy) / `INFO_FREEZETAG_SELF` + `CENTER_FREEZETAG_SELF` (self/null).
- `GiveFragsForKill → 0` (no frags in FT). `LockWeapon`/`PlayerRegen`/`PlayerDamaged`/`ItemTouch`/`BuffTouch` all gated by `STAT(FROZEN)`. `MakePlayerObserver`/`ClientDisconnect` → `ft_RemovePlayer` (unfreeze + HP 0 + recount). `ClientKill` blocked while frozen.

### Win / round resolution (`freezetag_CheckTeams`, `freezetag_CheckWinner`)
- `CheckTeams` (canRoundStart): true if every active team has a live (non-frozen, HP≥1) player and `total_players>0`.
- `CheckWinner` (canRoundEnd): if round timelimit elapsed → `CENTER/INFO_ROUND_OVER`, thaw all, `game_stopped`, re-init round. `Team_GetWinnerAliveTeam`: one team alive → `APP_TEAM_NUM ROUND_TEAM_WIN` + `TeamScore_AddToTeam(winner, ST_FT_ROUNDS, +1)`; `-1` → `ROUND_TIED`. `round_enddelay` (DEFAULT 0) delays evaluation. On resolve, thaw all + `round_handler_Init(5, warmup, round_timelimit)`.

### Start loadout (`SetStartItems` hook)
- Strips unlimited ammo (unless `!g_use_ammunition`). `start_health/armor = g_ft_start_health/armor` (100/100, xonotic balance); ammo `shells 60 / nails 320 / rockets 160 / cells 180 / fuel 0`. warmup_* mirror the same.

### Bot roles (`havocbot_role_ft_offense/freeing`, `HavocBot_ChooseRole`)
- 50/50 initial role; offense rates items/enemies/free-players/waypoints, switches to freeing when alone/timeout; freeing prioritizes frozen teammates. Role timeout `time + 10..20 s`.

### Client presentation (`cl_freezetag.qc`)
- `HUD_Mod_FreezeTag` → `HUD_Mod_CA_Draw` (per-team alive-count grid from `STAT(REDALIVE..PINKALIVE)`), layout cvar `hud_panel_modicons_freezetag_layout`.
- `WantEventchase`: `cl_eventchase_frozen && FROZEN` → third-person chase cam while frozen.
- `HUD_Draw_overlay`: while frozen, full-screen icy-blue tint `'0.25 0.90 1'`, alpha `0.3 + 0.7*(1 - max(0, REVIVE_PROGRESS*4-3))` (fades out as thaw completes); color warms toward white as `REVIVE_PROGRESS*2-1` rises.
- `HUD_Damage_show`: damage HUD shown while frozen.
- The crosshair `REVIVE_PROGRESS` ring (view.qc HUD_Draw) is a generic objective ring.

## Port mapping

| Base feature | Port symbol | Liveness |
|---|---|---|
| Mode registration / team count / scoring fields | `FreezeTag.Activate` (GameScores rules, TeamCount) | live |
| Round handler spawn/init | `FreezeTag.Activate` builds its own `Common…RoundHandler ft.Handler` **BUT it is never ticked**; the live round flow uses the **server** `RoundHandler` via `GameWorld.EnableRounds()` with the *generic* `DefaultCanRoundStart/End` | partial — FT's own handler dead; server handler with default predicates is the live driver |
| Freeze + score matrix | `FreezeTag.Freeze` (via `OnDeath` → `Combat.Death`) | live |
| Unfreeze | `FreezeTag.Unfreeze` | live |
| Per-frame revive + range geometry | `FreezeTag.ReviveTick` (+ `InRevivingRange`) called from `GameWorld.DriveGametypeFrame` | live |
| Auto-thaw timeout | `ReviveTick` autoThaw branch | live |
| Eliminated/alive counts net | `GametypeStatusBlock.Capture` (FreezeTag case) → `ModIconsPanel` + scoreboard grey-out | live |
| Frozen model tint | `ClientWorld` `FrozenColormod` on `HasStatusEffect(Frozen)` | live |
| Crosshair revive ring | `CrosshairPanel.ReviveProgress` draws a ring, but the property is **never assigned on the client** (server mirrors → `NetEntityState.ReviveProgress` → decoded into `NetEntity.ReviveProgress`, then the wire→HUD hop is missing) | **dead — unfed** |
| Weapon lock while frozen | `WeaponFireDriver.WeaponLocked` / `WeaponFiring.IsFrozen` (reads status effect) | live |
| Movement lock while frozen | `PlayerPhysics` `IsFrozen` (zeros move, blocks jump) | live |
| Win/round resolution + round/lead limit | `FreezeTag.CheckRound`/`CheckWinner`/`UpdateLeaderAndCheckLimit` (called per frame) | live |
| Start loadout (g_ft_start_*) | `FreezeTag.ApplyStartItems` | **dead — no caller** |
| Ice entity model (MDL_ICE, 20 frames, team colormod) | NOT IMPLEMENTED (only a color tint on the player model) | missing |
| Frozen/Reviving waypoint sprite | catalog entries `WaypointSpriteLayer "Frozen"/"Reviving"` exist but **never spawned for a frozen player** | dead |
| Freeze/revive/self/auto-revive/spawn-late notifications | defined in `NotificationsList` but **never sent** | dead |
| Full-screen frozen overlay (icy tint, progress-fade) | NOT IMPLEMENTED | missing |
| `cl_eventchase_frozen` third-person cam | NOT IMPLEMENTED | missing |
| Damage_Calculate: frozen takes 0 damage + `g_frozen_force` | NOT IMPLEMENTED (frozen players take full damage) | missing |
| `revive_auto_reducible` (hit speeds thaw) | NOT IMPLEMENTED | missing |
| `revive_time_to_score` / `revive_speed_t2s` (default-on time-based revive scoring) | NOT IMPLEMENTED (port uses per-completion +1 at `revive_speed=0.4`) | missing |
| `revive_auto_progress` base_progress ramp | NOT IMPLEMENTED (binary auto-thaw at timeout) | missing |
| `revive_spawnshield` post-revive shield | NOT IMPLEMENTED | missing |
| Void/lava soft-kill teleport + `g_frozen_damage_trigger` | NOT IMPLEMENTED | missing |
| Fall-damage revive (`g_frozen_revive_falldamage`) | NOT IMPLEMENTED (off by default) | missing |
| Nade self-revive (`g_freezetag_revive_nade`) | NOT IMPLEMENTED | missing |
| Bot freeing/offense roles | NOT IMPLEMENTED | missing |
| Round timelimit → ROUND_OVER tie | partial: `CheckRound` has no round-timer-expiry tie path | partial |

## Parity assessment

### What is faithful + live
The **core freeze loop** is solid: a fragged player is frozen via the death hook (HP→1, status effect applied),
movement and weapon firing are correctly locked off the shared `Frozen` status effect, the freeze/revive **score
matrix** matches Base exactly (self −1; teammate −1/−1; enemy +1/−1; rules-freeze no score), manual revive by a
nearby teammate accumulates progress over a box-overlap range test (`extra_size=100`), the auto-thaw timeout works,
the round win condition (`ST_FT_ROUNDS +1`, last team standing) and round/lead limit drive `MatchEnded`, and the
networked alive-count / eliminated set feeds the FT mod-icons panel and scoreboard grey-out. The icy player-model
tint (`ClientWorld.FrozenColormod` on `HasStatusEffect(Frozen)`) is live presentation.

**Adversarial correction:** the crosshair revive-progress ring is NOT live. `CrosshairPanel.ReviveProgress` is
declared and drawn, and the server does ship the value over the wire, but no client code ever assigns the decoded
`NetEntity.ReviveProgress` into the panel — so the property stays 0 and the ring never appears. The icy model tint
is therefore the ONLY live frozen-state visual feedback.

### Gaps (player-observable)
1. **Frozen players take full damage** (Damage_Calculate freeze branch missing). A frozen player has HP=1, so any
   chip damage re-kills them → re-freezes — players cannot "guard" or be safely shot while frozen, and the
   `g_frozen_force=0.6` knockback scaling is absent. Biggest gameplay correctness gap.
2. **No `revive_auto_reducible`** — damaging a frozen enemy does NOT speed their auto-thaw, removing a core FT
   tactic ("avoid shooting frozen enemies"). (Compounded by #1, which would instead re-kill them.)
3. **Time-to-score revive model absent** — Base default `revive_time_to_score=1.5` is ON: revivers earn +1 per
   1.5 s of reviving and revive at `speed_t2s=0.25`, progress is never cleared. The port instead awards +1 per
   completed revive at `revive_speed=0.4` and decays progress out of range. Different revive feel + scoreboard.
4. **Start loadout never applied** — `ApplyStartItems` has no caller; players don't get the FT-specific
   100/100 + ammo loadout (g_ft_start_*).
5. **No ice entity model** — frozen players show only a blue model tint, not the `MDL_ICE` block (random frame,
   team-colored). Missing the signature visual.
6. **No Frozen/Reviving waypoint sprite over frozen players** (catalog entries dead) — teammates can't see who's
   frozen / being revived on the radar/overhead.
7. **No freeze/revive notifications sent** — none of `INFO/CENTER_FREEZETAG_FREEZE/REVIVE/REVIVED/SELF/
   AUTO_REVIVED/SPAWN_LATE` fire (kill feed + center prints + their sounds are silent for all FT events).
8. **No full-screen frozen overlay** (icy-blue tint that fades as you thaw) and **no `cl_eventchase_frozen`**
   third-person cam while frozen.
9. **No void/lava soft-kill teleport** (`g_frozen_damage_trigger` path), **no fall-damage revive**, **no nade
   self-revive**, **no `revive_spawnshield`**, **no `revive_auto_progress` ramp** (auto-thaw is binary at the
   timeout, not a continuous floor).
10. **Round flow uses the generic server predicates, not FT's CheckTeams/CheckWinner.** `EnableRounds()` is
    called with no callbacks → `DefaultCanRoundEnd` counts a player alive on `!IsDead` only and does **not**
    treat frozen players as eliminated, so the server round handler's end/reset cadence can disagree with the
    actual all-frozen condition that `CheckRound` resolves. FT's own `Handler` is constructed but never ticked
    (dead code). No round-timelimit-expiry tie path in `CheckRound`.
11. **No bot freeing/offense roles** — bots have no FT-specific behavior.

### Intended divergences
None identified as deliberate; the divergences above appear to be unfinished scope, not design choices.

### Value notes
The C# fallback constants for warmup (5 vs Base 10), round timelimit (180 vs 360), end-delay (5 vs 0), and
`DefaultReviveClearSpeed` (0.4 vs 1.6) are wrong, but `gametypes-server.cfg` IS shipped in the port's data, so
the **live** cvar values are correct; the fallbacks only bite in unit tests with no cvar store. Flagged as
`values: partial` where the fallback is observable.

## Verification
- **Code-read, high confidence:** freeze/unfreeze/score-matrix logic, revive geometry, weapon/movement lock,
  alive-count networking, status-block wiring, `ApplyStartItems` having no caller, notifications never sent,
  waypoint sprites never spawned, `Damage_Calculate` frozen branch absent, `revive_auto_reducible`/t2s/
  spawnshield/falldamage/nade-revive/soft-kill all absent, FT.Handler never ticked.
- **Not runtime-verified:** the exact in-match feel of #1 (re-kill loop), whether the server round handler's
  default-predicate cadence visibly mis-times round resets in practice, and the precise client overlay behavior.
  Marked `unknown`/`low` where I could not confirm at runtime.

## Open questions
- Does the frozen-takes-full-damage gap actually produce an observable re-kill loop in a live match, or does
  some upstream guard (e.g. `IsDead`/respawn gating) mask it? Needs a runtime FT match.
- Is the duplicate `RoundHandler` (Common, dead) intended to replace the server one for FT later, or is its
  construction in `Activate` vestigial?
- Should `CheckRound` / the server round handler gain a round-timelimit-expiry tie path (Base ROUND_OVER)?

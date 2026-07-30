# Clan Arena — parity spec

**Base refs:** `common/gametypes/gametype/clanarena/{sv_clanarena.qc, sv_clanarena.qh, cl_clanarena.qc, cl_clanarena.qh, clanarena.qc, clanarena.qh}` · `server/round_handler.qc` · `server/elimination.qc` · `server/teamplay.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/ClanArena.cs` · `src/VortexArena.Common/Gameplay/GameTypes/RoundHandler.cs` (dead) · `src/VortexArena.Server/RoundHandler.cs` (live) · `src/VortexArena.Server/GameWorld.cs` (wiring + DriveGametypeFrame) · `src/VortexArena.Server/SpectatorRules.cs` · `src/VortexArena.Net/GametypeStatusBlock.cs` · `game/hud/ModIconsPanel.cs` · `game/net/NetGame.cs` (UpdateModIcons)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22 (adversarial verify pass)

## Overview
Clan Arena (`ca`) is a round-based elimination teamplay gametype. Players spawn with **all weapons** and
**maximum health/armor (200/200)**, there are **no item pickups on the map**, and each player has **one life
per round**. A round opens with a ~10 s grace countdown during which weapons cannot be fired (group-up time).
The last team with a living player wins the round and banks one round point (`ST_CA_ROUNDS`); first team to the
round limit (default 10), or with a lead of `leadlimit` (default 6), wins the match. Kills award **no individual
frags** — instead damage dealt accrues to a cosmetic per-player SCORE (`g_ca_damage2score`, 1 pt / 100 dmg) for
the scoreboard only. There is no friendly fire, no self-damage, no fall damage, and no health regen. If the
round time limit (180 s) elapses with multiple teams alive, the round is normally a stalemate (no score) unless
`g_ca_prevent_stalemate` breaks it by survivor count and/or total team health.

## Base algorithm (authoritative)

### Registration + match config  (`clanarena.qh:INIT(ClanArena)` / `sv_clanarena.qh:REGISTER_MUTATOR(ca)`)
- **gametype_init defaults:** `"timelimit=20 pointlimit=10 teams=2 leadlimit=6"`, flags
  `GAMETYPE_FLAG_TEAMPLAY | GAMETYPE_FLAG_USEPOINTS`, legacydefaults `"10 20 0"`.
- **ONADD (authority):** `GameRules_teams(true)`; `GameRules_spawning_teams(g_ca_team_spawns)`;
  `GameRules_limit_score(g_ca_point_limit)`; `GameRules_limit_lead(g_ca_point_leadlimit)`; team count =
  `g_ca_teams_override>=2 ? override : g_ca_teams`, clamped 2..4 → `teamplay_bitmask`;
  `GameRules_scoring(bitmask, …, { field_team(ST_CA_ROUNDS,"rounds",PRIMARY) })`; `allowed_to_spawn = true`;
  `g_ca_spectate_enemies` cached; `observe_blocked_if_eliminated = (spectate_enemies == -1)`;
  `round_handler_Spawn(CA_CheckTeams, CA_CheckWinner, CA_RoundStart)`;
  `round_handler_Init(5, g_ca_warmup=10, g_ca_round_timelimit=180)`; `EliminatedPlayers_Init(ca_isEliminated)`.

### Round lifecycle  (`server/round_handler.qc:round_handler_Think` driving CA's three callbacks)
- The `round_handler` edict thinks every frame and cycles: **wait** → **countdown** → **round live** →
  **end-delay** → next round. `round_handler_Init(delay=5, count=10, timelimit=180)`: delay = 5 s between a
  round ending and the next countdown; count = the 10 s grace countdown (cnt = count+1 down to 0, decremented
  at **1 Hz** while `canRoundStart` holds); timelimit = the round's own 180 s clock.
- On leaving `wait`: `round_starttime = time + count`, `reset_map(false)` (re-spawn players + reset map objects).
- Countdown only advances while `canRoundStart()` (= `CA_CheckTeams`) is true; otherwise it stalls/resets and
  `round_starttime = -1`. At cnt==0 each present player gets `GameRules_scoring_add(ROUNDS_PL, 1)`,
  `round_endtime = time + round_timelimit`, `++rounds_played`, then `roundStart()` (= `CA_RoundStart`).
- While live, `canRoundEnd()` (= `CA_CheckWinner`) is polled **every frame**; when it returns true the handler
  enters `wait` for `delay` seconds.

### Count alive players + alive-count stats  (`sv_clanarena.qc:CA_count_alive_players`)
- Zeroes each team's `m_num_players_alive`, then `FOREACH_CLIENT(IS_PLAYER && HasValidTeam)`: counts
  `total_players`, and for each non-dead player increments its team's alive count. Then for every real client
  sets `STAT(REDALIVE/BLUEALIVE/YELLOWALIVE/PINKALIVE)` = the four teams' alive counts (networked to CSQC).

### Round-start gate  (`sv_clanarena.qc:CA_CheckTeams` — the `canRoundStart` callback)
- `allowed_to_spawn = true`; recount alive; `missing_teams_mask = 0`. Returns true iff
  `Team_GetNumberOfAliveTeams() == AVAILABLE_TEAMS` (every active team has ≥1 live player). If
  `total_players == 0` returns false. Otherwise builds `missing_teams_mask` (teams with 0 alive) and returns false.

### Round resolution  (`sv_clanarena.qc:CA_CheckWinner` — the `canRoundEnd` callback)
- If `round_endtime > 0 && round_endtime - time <= 0` (time limit elapsed): if
  `g_ca_prevent_stalemate & (BIT0|BIT1)` → `winner_team = CA_PreventStalemate()` else `-2`.
- `CA_count_alive_players()`. If `winner_team == 0` → `winner_team = Team_GetWinnerAliveTeam()` (the sole
  team with alive players → that team; if none alive → `-1`; if two+ alive → `0`).
- If `winner_team == 0` (round still live): `round_handler_ResetEndDelayTime()`; return 0.
- **End-delay:** if `g_ca_round_enddelay` and the round timelimit hasn't passed: arm `round_enddelaytime =
  min(time + g_ca_round_enddelay, round_endtime)` and return 0; on a later frame once that time arrives, proceed.
- On a decided round: send notifications — `winner>0`: `CENTER/INFO ROUND_TEAM_WIN_<team>` +
  `TeamScore_AddToTeam(winner, ST_CA_ROUNDS, +1)`; `-1`: `ROUND_TIED`; `-2`: `ROUND_OVER`. Then
  `allowed_to_spawn = false`, `game_stopped = true`, `round_handler_Init(5, g_ca_warmup, g_ca_round_timelimit)`,
  and `nades_RemovePlayer` for all players. Returns 1.

### Stalemate prevention  (`sv_clanarena.qc:CA_PreventStalemate`)
- bit0 (survivors): find the team with the most alive players and the second-most; if they differ, that team
  wins (bprint the result). bit1 (health): sum `floor(health)+floor(armor)` of each team's live players; if the
  top two team-health totals differ, the top team wins. If neither breaks the tie → return `-2` (true stalemate).

### Damage rules  (`sv_clanarena.qc:Damage_Calculate` / `PlayerDamage_SplitHealthArmor` / `CalculateRespawnTime` / `PlayerRegen`)
- **Damage_Calculate:** if target is a live player AND (self OR same-team OR deathtype == FALL) → `frag_damage = 0`.
  `frag_mirrordamage = 0` always (force/knockback untouched).
- **damage2score (PlayerDamage_SplitHealthArmor):** skipped before `game_starttime` or while a round is active
  but not started. `excess = max(0, frag_damage - damage_take - damage_save)`; if `g_ca_damage2score<=0` or
  `frag_damage-excess==0` return. Enemy player attacker → `+ (frag_damage-excess)`; teammate → `-(…)`; certain
  environmental suicides (kill/drown/hurttrigger/camp/lava/slime/swamp) by the victim themselves → `-(…)`. Award
  via `GameRules_scoring_add_float2int(scorer, SCORE, dmg, float2int_decimal_fld, g_ca_damage2score)` — a
  **decimal-accumulating** add: 1 SCORE per `g_ca_damage2score` damage, carrying the fractional remainder in
  `float2int_decimal_fld` across hits.
- **CalculateRespawnTime / PlayerRegen:** both return true (no respawn calc — dead players are forced spectate;
  no health/armor regen in CA).

### Death + elimination  (`sv_clanarena.qc:PlayerDies / MakePlayerObserver / ca_isEliminated` + `server/elimination.qc`)
- **PlayerDies:** `ca_LastPlayerForTeam_Notify` (CENTER `ALONE` to the team's last survivor); if
  `!allowed_to_spawn`: `respawn_flags = RESPAWN_SILENT`, `respawn_time = time + 2`. Always
  `respawn_flags |= RESPAWN_FORCE`. If not warmup, `eliminatedPlayers.SendFlags = 0xFFFFFF` (resend the grey-out).
- **ca_isEliminated:** `(INGAME_JOINED && (IS_DEAD || frags == FRAGS_PLAYER_OUT_OF_GAME))` OR `INGAME_JOINING`.
  Drives `EliminatedPlayers_SendEntity` — an 8-bit-per-byte maxclients bitfield that greys eliminated rows on the
  scoreboard.
- **GiveFragsForKill (CBC_ORDER_FIRST):** `M_ARGV(2) = 0` — kills give **no** individual frags.

### Start items + weapon arena  (`sv_clanarena.qc:SetStartItems / SetWeaponArena / FilterItem / ForbidThrowCurrentWeapon`)
- **SetStartItems:** strips `IT_UNLIMITED_AMMO|IT_UNLIMITED_SUPERWEAPONS` (adds unlimited ammo only if
  `!g_use_ammunition`); sets `start_health = warmup_start_health = g_ca_start_health` (200);
  `start_armorvalue = …= g_ca_start_armor` (200); `start_ammo_{shells,nails,rockets,cells,fuel} =
  60/320/160/180/0`. (Same values applied to warmup loadout.)
- **SetWeaponArena:** if the requested arena is "" or "0", use `g_ca_weaponarena` (default `"most"`) — i.e. spawn
  with the full arsenal.
- **FilterItem:** removes powerups if `g_powerups<=0`; removes ALL items if `g_pickup_items<=0` — CA maps have no
  pickups.
- **ForbidThrowCurrentWeapon:** returns true (can't drop your weapon).

### Spectating + join handling  (`sv_clanarena.qc:SpectateSet/Next/Prev / ClientCommand_Spectate / PutClientInServer / ForbidSpawn`)
- `g_ca_spectate_enemies` (default 0): `1` = spectate anyone; `0` = an in-game eliminated player may only
  spectate teammates; `-1` = blocks the freeroam camera and forbids observing entirely (`observe_blocked_if_eliminated`).
- A late joiner while `!allowed_to_spawn` is forced to Observer (`TRANSMUTE(Observer)`), gets
  `INGAME_STATUS_JOINING` + `INFO_CA_JOIN_LATE`, and plays from the next round. `Bot_FixCount` counts in-game
  joined clients so bot fill works mid-round.

### Constants / cvars (Base defaults)
| cvar | default | units | side |
|---|---|---|---|
| `g_ca_start_health` | 200 | hp | authority |
| `g_ca_start_armor` | 200 | armor | authority |
| `g_ca_start_ammo_shells` | 60 | ammo | authority |
| `g_ca_start_ammo_nails` | 320 | ammo | authority |
| `g_ca_start_ammo_rockets` | 160 | ammo | authority |
| `g_ca_start_ammo_cells` | 180 | ammo | authority |
| `g_ca_start_ammo_fuel` | 0 | ammo | authority |
| `g_ca_damage2score` | 100 | dmg/pt | authority |
| `g_ca_warmup` | 10 | s (round-start grace countdown) | authority |
| `g_ca_round_timelimit` | 180 | s | authority |
| `g_ca_round_enddelay` | 0 | s | authority |
| `g_ca_prevent_stalemate` | 0 | bitmask (b0 survivors, b1 health) | authority |
| `g_ca_point_limit` | -1 (use mapinfo → 10) | rounds | authority |
| `g_ca_point_leadlimit` | -1 (→ 6) | rounds | authority |
| `g_ca_spectate_enemies` | 0 | mode (-1/0/1) | authority |
| `g_ca_team_spawns` | 1 | bool | authority |
| `g_ca_teams_override` | 0 | count | authority |
| `g_ca_teams` (mapinfo) | 0 (→2) | count | authority |
| `g_ca_weaponarena` | "most" | arena spec | authority |
| round_handler delay/count/timelimit | 5 / 10 / 180 | s | authority |
| `hud_panel_modicons_ca_layout` | 0 | layout | presentation |

## Port mapping

The port splits CA's round driver across **two** RoundHandler classes, only one of which is live:
- **`src/VortexArena.Server/RoundHandler.cs`** (`GameWorld.Rounds`) — the LIVE round state machine. For CA it
  is created by a **bare `EnableRounds()`** with NO CA callbacks, so it runs the generic `DefaultCanRoundStart`
  (≥2 teams have players) / `DefaultCanRoundEnd` (≤1 team alive) and the QC-faithful `Init(5, 5, 180)` from
  `Spawn`. It handles the countdown/end-delay/`reset_map`/round-start announcer (NUM_ROUNDSTART/BEGIN).
- **`ClanArena.Handler`** (the Common `RoundHandler` created in `Activate()`, `Init(g_ca_round_enddelay, g_ca_warmup,
  g_ca_round_timelimit)`) — **DEAD**: its `Tick()` is never called by GameWorld.
- **`ClanArena.CheckWinner()` / `PreventStalemate()`** — DEAD on the live path (only `Handler.CanRoundEnd` would
  call them, and `Handler` is dead). The LIVE per-frame resolution is **`ClanArena.CheckRound(Clients.Players)`**
  (called from `DriveGametypeFrame`), which awards the round point and runs `UpdateLeaderAndCheckLimit`, but does
  NOT apply the round time limit, stalemate prevention, or any notifications.

Other features:
- **Team assignment / smallest-team join** → `ClanArena.AssignTeam` → `TeamBalance.JoinSmallestTeam` (live).
- **No friendly fire / self / fall damage** → `ClanArena.OnDamageCalculate` on `MutatorHooks.DamageCalculate`
  (live — DamageSystem calls the hook).
- **No per-kill frags** → CA's `OnDeath` returns false and never writes `ScoreFrags`; the Scores table doesn't own
  SP_SCORE while a gametype scorer is active (net effect: 0 per-kill frags — faithful).
- **Alive-count stats + scoreboard grey-out** → `ClanArena.AliveCount` / `IsEliminatedPlayer` →
  `GametypeStatusBlock.Capture` (live, sent per-peer in ServerNet) → `NetGame.UpdateModIcons` → `ModIconsPanel`
  (CA mod-icons HUD). Live.
- **Spectate-enemies rule** → `SpectatorRules` (`g_ca_spectate_enemies`), live via `ClientManager` spectate cycling.
- **CA start loadout (200/200 + full ammo)** → `ClanArena.ApplyStartItems` — **DEAD** (no caller). The live spawn
  path (`SpawnSystem.ApplyStartLoadout` → `ComputeStartItems`) reads the GENERIC `g_balance_health_start` (100) /
  `g_balance_armor_start` (0) and stock ammo, NOT the `g_ca_start_*` values.
- **CA weapon arena ("most")** → NOT IMPLEMENTED. CA does not subscribe the `SetWeaponArena` hook (Mayhem/TeamMayhem
  do); players do not get the full arsenal on spawn.
- **damage2score scoreboard accrual** → `ClanArena.AddDamageScore` — **DEAD** (no caller); also truncates per-hit
  (`(int)(dmg*d2s/100)`) instead of QC's decimal-accumulating `float2int` add.
- **No-pickups item filter (`FilterItem`)** → NOT IMPLEMENTED in `ClanArena.cs`.
- **Round-win notifications (ROUND_TEAM_WIN/TIED/OVER) + ALONE center** → registered in NotificationsList but
  NEVER SENT on the CA path.
- **Round-start grace fire-block (10 s no-fire)** → NOT enforced: `WeaponFireDriver.weaponUseForbidden` is a stub
  that always returns false; players can fire during the countdown.

## Parity assessment

- **logic** — Faithful only at the core combat layer (no-friendly-fire damage filter, no-per-kill-frags are
  faithful + live). The round **lifecycle/gate/resolution are `partial`, not faithful**: the LIVE path is a
  **simplified `CheckRound`** + the generic `DefaultCanRoundStart`/`DefaultCanRoundEnd`, which lack the round
  time limit, stalemate prevention, end-delay score evaluation, the all-active-teams-alive start gate (diverges
  with 3-4 teams), the per-player ROUNDS_PL award, and the late-join/`allowed_to_spawn` gating. The faithful
  `CheckTeams`/`CheckWinner`/`PreventStalemate` code exists in ClanArena.cs but is dead (stranded behind the
  unticked Common `Handler`).
- **values** — Start health/armor/ammo (200/200 + 60/320/160/180/0) are NOT applied on the live path (players get
  generic ~100/0). The round-start countdown runs at the generic **5 s** instead of CA's **10 s** (`g_ca_warmup`
  is never passed to the live handler). Round limit 10 / lead 6 are honored. damage2score value matches but the
  feature is dead and truncates the decimal remainder.
- **timing** — Countdown cadence (1 Hz) and end-delay (5 s) are faithful via the Server RoundHandler, but the
  countdown LENGTH is 5 s not 10 s, and the per-round time limit (180 s → stalemate/prevent-stalemate) is not
  evaluated on the live path. Round resolution polls per-frame (faithful).
- **presentation** — Alive-count mod-icons HUD + scoreboard grey-out are live and faithful. Round-win center/info
  prints (ROUND_TEAM_WIN/TIED/OVER) and the "You are now alone" center are NOT shown. Round-start countdown
  center/announcer IS shown.
- **audio** — Round-win announcer cues are tied to the missing notifications, so they don't play. The round-start
  countdown announcer (NUM_ROUNDSTART/BEGIN) does play.

### Liveness summary
- Live: team assignment; no-FF damage filter; no-per-kill-frags; alive-count stats + grey-out HUD; spectate-enemies;
  round point award + round/lead-limit match end (`CheckRound`); round-start countdown announcer + `reset_map`.
- Dead: `ClanArena.Handler` (Common RoundHandler); `CheckTeams`; `CheckWinner`; `PreventStalemate`; `ApplyStartItems`;
  `AddDamageScore`; the `OnRoundCounted` per-player ROUNDS_PL hook (unwired for CA).
- Missing: CA start loadout on spawn; CA weapon arena ("most"); no-pickups item filter; round-win/ALONE
  notifications; round-time-limit + stalemate evaluation on the live path; 10 s no-fire grace block; per-player
  ROUNDS_PL award; late-join→Observer (`INGAME_JOINING`) handling; `ForbidThrowCurrentWeapon` (impact unverified);
  explicit no-regen rule (effective via no-respawn, but not an explicit hook).

### Intended divergences
None identified as deliberate. The dual-RoundHandler split appears to be an unfinished port (the more faithful
Common `Handler` was superseded by the live Server `Rounds` + `CheckRound`, leaving CA-specific timing/stalemate
behavior stranded).

## Verification
- Base behavior: read in full from `sv_clanarena.qc`, `clanarena.{qc,qh}`, `cl_clanarena.qc`,
  `round_handler.qc`, `elimination.qc`, `teamplay.qc`, and cvar defaults from `gametypes-server.cfg` /
  `balance-xonotic.cfg`.
- Port liveness: traced `GameWorld.ActivateGameType` (CA → `ca.Activate()` + bare `EnableRounds()`) and
  `DriveGametypeFrame` (CA → `ca.CheckRound`); confirmed `ClanArena.Tick`/`CheckWinner`/`Handler` have no live
  caller; confirmed `ApplyStartItems`/`AddDamageScore` have zero callers across `src/` and `game/`; confirmed
  `SpawnSystem.ComputeStartItems` reads generic balance cvars; confirmed CA subscribes neither `SetStartItems`
  nor `SetWeaponArena` (Mayhem does, for contrast); confirmed `weaponUseForbidden` is a `return false` stub;
  confirmed ROUND_TEAM_WIN/TIED/OVER/ALONE are registered but never `Send`-ed on the CA path; confirmed the
  alive-count status block + ModIconsPanel + SpectatorRules are wired live.
- Not run in-game; all conclusions are from static trace (confidence noted per row in the registry).

## Open questions
- Is the dual RoundHandler intended (does the team plan to retire the Common `ClanArena.Handler` + `CheckWinner`,
  or wire it back as the live driver)? It currently strands the round-time-limit/stalemate behavior.
- Where should the CA start loadout + weapon-arena be applied — by subscribing the `SetStartItems`/`SetWeaponArena`
  hooks (like Mayhem) or by calling `ApplyStartItems` from the spawn path? Today neither happens.
- Confirm in-game whether CA players actually spawn at 100/0 with no full arsenal (the static trace says yes).

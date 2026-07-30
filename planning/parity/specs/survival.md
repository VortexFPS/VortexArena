# Survival — parity spec

**Base refs:** `common/gametypes/gametype/survival/{survival,sv_survival,cl_survival}.qc` + `.qh`, `server/round_handler.qc`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/Survival.cs`, `src/VortexArena.Common/Gameplay/GameTypes/RoundHandler.cs`, `src/VortexArena.Server/RoundHandler.cs`, `src/VortexArena.Server/GameWorld.cs`, `src/VortexArena.Net/GametypeStatusBlock.cs`, `game/net/NetGame.cs`, `game/hud/ModIconsPanel.cs`, `game/hud/ScoreboardPanel.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Survival is a round-based hidden-role mode built on the LMS gametype family. At each round start a small
fraction of the live players are secretly assigned the **hunter** role; everyone else is **prey** (survivor).
Roles are concealed — obituaries, frag announcements and the scoreboard kills/deaths columns are anonymized so
prey cannot tell who the hunters are. Death eliminates a player for the rest of the round (no respawn). A round
ends when one side is entirely wiped (hunters win if any hunter still lives, else prey win), OR when the round
time limit expires (survivors win), OR — when `g_survival_round_enddelay == -1` — when the whole match times out
(survivors also win). Kills bank no points during the round; the banked frags plus per-side bonuses are awarded
to the players when the round resolves. The registered defaults are `timelimit=20 pointlimit=12`.

## Base algorithm (authoritative)

### Gametype registration  (`survival.qh:CLASS(Survival)`)
- `gametype_init(_("Survival"), "surv", "g_survival", GAMETYPE_FLAG_USEPOINTS, "", "timelimit=20 pointlimit=12", …)`.
  USEPOINTS only — no visible teams. `m_isAlwaysSupported` returns true (any map). `m_isForcedSupported`:
  unless `g_survival_not_lms_maps` is set, every LMS-capable map also lists Survival.
- Shared state (`survival.qh`): `.int survival_status` with `SURV_STATUS_PREY = 1`, `SURV_STATUS_HUNTER = 2`;
  hardcoded colors `SURV_COLOR_PREY = 51` (green), `SURV_COLOR_HUNTER = 68` (red).
- Authority state (`sv_survival.qh`): `.int survival_validkills` — frags banked this round, awarded at round end.

### Initialization  (`sv_survival.qc:surv_Initialize`)
- Registers score columns: primary `SP_SURV_SURVIVALS` ("survivals"), secondary `SP_SURV_HUNTS` ("hunts").
- `allowed_to_spawn = true`; spawns the round handler:
  `round_handler_Spawn(Surv_CheckPlayers, Surv_CheckWinner, Surv_RoundStart)`;
  `round_handler_Init(5, g_survival_warmup, g_survival_round_timelimit)` (delay 5, countdown = warmup, round
  timelimit = round_timelimit). `EliminatedPlayers_Init(surv_isEliminated)`. `SurvivalStatuses_Init()`.

### Cvars / constants (`sv_survival.qc` autocvars; `gametypes-server.cfg`)
- `g_survival_hunter_count = 0.25` — ≥1 = absolute count, <1 = fraction of LIVE players.
- `g_survival_round_timelimit = 120` (s) — per-round time limit.
- `g_survival_warmup = 10` (s) — grace period (countdown) before a round starts.
- `g_survival_punish_teamkill = 1` (true) — auto-kill a player who frags an ally.
- `g_survival_reward_survival = 1` (true) — +1 SCORE to all surviving prey if the round timer is reached.
- `g_survival_round_enddelay = 0` (s) — delay before score evaluation after a side could win; `-1` = wait for
  the match timeout (then survivors win).
- `g_survival_not_lms_maps = 0` — exclude LMS maps from Survival's map list.

### Round start / role assignment  (`sv_survival.qc:Surv_RoundStart`)
- `allowed_to_spawn = boolean(warmup_stage)`.
- Every live player (`IS_PLAYER && !IS_DEAD`) → `survival_status = SURV_STATUS_PREY`; non-live → `0`;
  everyone's `survival_validkills = 0`. `playercount` counts only live players.
- `hunter_count = bound(1, (cvar≥1 ? cvar : floor(playercount*cvar)), playercount-1)` — at least 1, at most
  live−1 (never all-hunters).
- `FOREACH_CLIENT_RANDOM(live)` picks the first `hunter_count` of a random shuffle → `SURV_STATUS_HUNTER`.
- `survivalStatuses.SendFlags = STATUS_SEND_RESET`.
- Center-prints each player their role: `CENTER_SURVIVAL_SURVIVOR` (prey) / `CENTER_SURVIVAL_HUNTER` (hunter).

### Win condition  (`sv_survival.qc:Surv_CheckWinner`, the round handler's canRoundEnd)
- **Match-timeout branch** (only when `round_enddelay == -1`): if `round_handler_GetEndTime()` has passed,
  survivors win → `CENTER/INFO_SURVIVAL_SURVIVOR_WIN`, remove nades, `Surv_UpdateScores(timed_out=true)`,
  `allowed_to_spawn=false`, `game_stopped=true`, re-init the round handler, disclose hunters.
- Count live prey + live hunters. If both > 0 → round continues (reset end-delay).
- `round_enddelay` handling: if set and before the round timelimit, schedule end-delay then wait it out.
- Else resolve: `hunter_count>0` → hunters win (`SURVIVAL_HUNTER_WIN`); else `survivor_count>0` → survivors win
  (`SURVIVAL_SURVIVOR_WIN`); else tie (`ROUND_TIED`). `Surv_UpdateScores(false)`, `allowed_to_spawn=false`,
  `game_stopped=true`, re-init round handler, disclose hunters, remove nades.

### Round timelimit timeout (survivors win) — `round_handler` arms `round_endtime = time + round_timelimit`
- When `round_enddelay != -1`, the per-round timeout is realized via the round handler's own end time interacting
  with the global match timelimit + `Surv_CheckWinner`; reaching the round timer with prey alive means the prey
  win the round and surviving prey get the `reward_survival` bonus (`Surv_UpdateScores(timed_out=true)`).

### Scoring  (`sv_survival.qc:Surv_UpdateScores`, GiveFragsForKill, AddPlayerScore)
- `GiveFragsForKill` (CBC_ORDER_FIRST): during an active round, `survival_validkills += frags`, then zero the
  immediate score (banked, not shown — concealment). Only when round started, not in warmup.
- `Surv_UpdateScores(timed_out)`: for every client add `survival_validkills` to SCORE (`totalfrags` too), zero
  it; then for each live player: prey → `SURV_SURVIVALS +1` (and `+1 SCORE` if `timed_out && reward_survival`);
  hunter → `SURV_HUNTS +1`.
- `AddPlayerScore` hook: zero the reported delta for `SP_KILLS/DEATHS/SUICIDES/DMG/DMGTAKEN` (concealment —
  rewriting kills/deaths would out the hunters). Falls through (does not claim the write).

### Death / elimination  (`sv_survival.qc:PlayerDies`, ClientObituary, surv_isEliminated)
- `PlayerDies`: notify the now-alone last teammate (`CENTER_ALONE`); set `RESPAWN_FORCE` (+ `RESPAWN_SILENT`
  and `respawn_time = time+2` while `!allowed_to_spawn`); flag eliminatedPlayers resend; clear the bot queue.
  **Teamkill punishment**: if `punish_teamkill` and attacker≠target are same-status players and not a needkill
  death, and the round has started → `Damage(attacker, … 100000, DEATH_MIRRORDAMAGE)` (the killer dies too).
- `ClientObituary`: same-status frag (teamkill) costs the attacker −1 frag (or −2 if no punish); a hunter
  attacker is forced anonymous (`M_ARGV(5,bool)=true`).
- `surv_isEliminated`: a joined player who is dead / `FRAGS_PLAYER_OUT_OF_GAME`, or a player who is joining,
  counts as eliminated (drives the grey-out + the round start gate).

### Hidden-role networking  (`sv_survival.qc:SurvivalStatuses_SendEntity`, `cl_survival.qc` NET_HANDLE)
- A linked entity `survivalStatuses`. `SendEntity`: a HUNTER recipient always gets `STATUS_SEND_HUNTERS`
  (hunters know all hunters). The bitfield encodes, per maxclients block of 8, which players are hunters.
  `STATUS_SEND_RESET` tells the client to set everyone to prey first.
- Client handler colors every player via `colormap = 1024 + (hunter ? SURV_COLOR_HUNTER : SURV_COLOR_PREY)`
  — overriding scoreboard + player model colors. The `cl_surv` `ForcePlayercolors_Skip` hook applies it.

### Client HUD / scoreboard  (`cl_survival.qc:HUD_Mod_Survival`, DrawScoreboard_Force)
- `HUD_Mod_Survival`: `mod_active=1` always; hide while `GAMESTARTTIME/ROUNDSTARTTIME > time` or spectating;
  draw the local player's own role tag — "Hunter" red `'1 0 0'` / "Survivor" green `'0 1 0'`.
- `DrawScoreboard_Force`: force-show the scoreboard while `GAME_STOPPED` so the round-end out reveals hunters.

### Other hooks
- `ForbidSpawn`/`PutClientInServer`: late joiners are forced to observe until the next round (CA-style);
  `INFO_CA_JOIN_LATE`. `MakePlayerObserver` / `reset_map_*` / `ClientDisconnect` manage INGAME status + the
  "you are alone" notify. `Bot_ForbidAttack`: a bot never attacks a same-status player. `ClientCommand_Spectate`:
  forced-spectate join semantics. `CalculateRespawnTime`: no-op (player is forced to spectate).

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `Surv_RoundStart` role assignment | `Survival.AssignRoles` | live, faithful (random pick, live-only count, bound formula) |
| `g_survival_hunter_count` formula | `Survival.HunterCount` | faithful |
| `GiveFragsForKill` banking | `Survival.OnDeath` (`ValidKills += 1`) | live; banks **1 per kill**, not the frag delta |
| `Surv_UpdateScores` | `Survival.UpdateScores` | live on side-wipe; survivals/hunts/reward bonuses faithful |
| `AddPlayerScore` anonymize | `Survival.AnonymizeScore` (via `GameScores.AddPlayerScoreHook`) | live, faithful |
| Side-wipe win | `Survival.CheckWinningCondition` | live; sets `RoundOver`/`WinningSide`, awards scores |
| Match/round timeout survivor win | `Survival.EndRoundTimedOut` | **DEAD** — no caller; *and* the Common `RoundHandler` never tests `RoundEndTime` in `InProgress`, so the timer can't resolve either way |
| `surv_LastPlayerForTeam_Notify` (CENTER_ALONE) | NOT IMPLEMENTED | last-of-side "you are alone" notify absent |
| Round handler | `Survival.Handler` (Common `RoundHandler`) ticked by `surv.Tick()` **and** the world `Server.RoundHandler` via `EnableRounds()` | dual handler; see liveness gap |
| Hidden-role networking | `GametypeStatusBlock.Capture(Survival …)` → `ClientNet`/`NetGame` | live; own role + hunter-id disclosure rule faithful |
| Own-role HUD tag | `ModIconsPanel.DrawSurvival` | live, faithful (red Hunter / green Survivor) |
| Eliminated grey-out | `ScoreboardPanel` row `Eliminated` via `NetGame.SetWireRows(EliminatedNetIds)` | live |
| Player model green/red coloring | NOT IMPLEMENTED | colormap tint is team-based; no survival role color |
| Win/role center+info notifications | `NotificationsList` declares them; **never sent** | dead presentation |
| `punish_teamkill` mirror-damage | NOT IMPLEMENTED | no auto-kill on teamkill |
| `Bot_ForbidAttack` (don't shoot allies) | NOT IMPLEMENTED | bots will shoot fellow prey/hunters |
| Late-join forced-observe | NOT IMPLEMENTED for Survival | relies on generic round respawn gating |
| `MatchEnded` for Survival | NOT WIRED (`GameWorld.MatchEnded` `_ => false`) | match never ends via Survival |

## Parity assessment

### Live + faithful
- **Role assignment** (`AssignRoles`): live-only player count, `bound(1,…,live−1)` formula, and a seeded
  Fisher–Yates random pick — all pinned by `SurvivalRolesTests`. Faithful.
- **Banked-kill scoring + bonuses** (`UpdateScores`) and the **score anonymization** hook are live and pinned by
  `SurvivalScoringTests`. Two nuances: (a) `OnDeath` banks **+1 per kill** rather than the QC frag *delta*
  (`M_ARGV(2,float)`), so with frag multipliers the banked total can differ; for stock 1-per-kill it matches.
  (b) **Timing:** QC `GiveFragsForKill` banks only when `!warmup_stage && round_handler_IsRoundStarted()`; the
  port's `OnDeath` has **no round-started/warmup guard** (it banks on any kill with a live attacker while
  `!RoundOver`), so a warmup-phase kill banks in the port but would not in QC. (`bank_validkills` timing →
  partial.)
- **Hidden-role disclosure** over the wire (`GametypeStatusBlock`): own role hidden until the round is live;
  hunter ids never reach prey/observer mid-round; everyone gets the hunter set at round end. Pinned by
  `GametypeStatusTests.Surv_HunterIdsNeverLeakToPreyMidRound_DisclosedAtRoundEnd`. Faithful anti-cheat invariant.
- **Own-role HUD tag** and **eliminated scoreboard grey-out** are wired and faithful.

### Gaps (observable)
1. **No round-timer / match-timeout resolution (the mode's headline rule).** Base ends the round and rewards
   survivors when the 120 s round timer (or the match timeout under `round_enddelay==-1`) expires. The port's
   `EndRoundTimedOut()` has **no caller** — AND, even if it were wired, the Common `RoundHandler.Tick()`
   `InProgress` branch (`RoundHandler.cs:124-132`) checks **only** `CanRoundEnd()` and never compares
   `now >= RoundEndTime`, despite arming `RoundEndTime` at cs:118. So the timer is structurally incapable of
   firing on either handler. `Survival.CanRoundEnd` resolves on a full side-wipe only. If a prey simply hides,
   the round never ends — "survive until time runs out" does not work.
2. **No win / role notifications.** `SURVIVAL_HUNTER_WIN`, `SURVIVAL_SURVIVOR_WIN`, `ROUND_TIED`,
   `SURVIVAL_SURVIVOR`, `SURVIVAL_HUNTER` are declared in `NotificationsList` but **never sent** by any server
   code. The player never sees "You are a survivor/hunter" at round start nor the win banner at round end.
3. **No teamkill punishment.** `g_survival_punish_teamkill` (default on) should auto-kill a player who frags an
   ally (100000 mirror damage) and dock frags. Neither the mirror-kill nor the −1/−2 frag adjustment exists.
4. **Bots attack allies.** `Bot_ForbidAttack` (don't shoot same-status players) is not ported, so bots fire on
   fellow prey/hunters, breaking the hidden-role tension and inflating teamkills.
5. **No survival player coloring.** Base recolors every player green (prey) / red (hunter) via `colormap`
   (own-side reveal at round end). The port leaves model/scoreboard tint team-based; the hidden-role colors are
   absent. (Low severity — only own role + round-end set are disclosed anyway.)
6. **`MatchEnded` returns false for Survival** (`GameWorld.cs:285`, the `_ => false` default — note LMS, the
   family Survival is built on, *does* have a `MatchEnded` arm; Survival does not), so the standard
   match-end/intermission/pointlimit path never fires for this mode; combined with (1) the match has no faithful
   end state.
8. **No "you are alone" notify.** Base's `surv_LastPlayerForTeam_Notify` (called from `PlayerDies`,
   `ClientDisconnect`, `MakePlayerObserver`) center-prints `CENTER_ALONE` to the last living member of a status
   when their second-to-last ally drops mid-round. The port has no last-of-side detection nor sender. (Omitted by
   the first pass — added as `survival.notify.last_survivor_alone`.)
7. **Dual round-handler architecture.** `Survival.Activate()` builds its own Common `RoundHandler` (`Handler`,
   driven by `surv.Tick()`), while `ActivateGameType` *also* calls `EnableRounds()` which spawns the separate
   `Server.RoundHandler` (`GameWorld.Rounds`, driven by `Rounds.Think()`). The world handler — the one that
   actually resets the map, gates respawns and broadcasts the countdown — uses `DefaultCanRoundStart/End`, so it
   does **not** call `Survival.AssignRoles`; role assignment rides only on `surv.Handler`. The two handlers run
   on independent timers (different warmup/end-delay defaults), so the role-assign moment and the map-reset/
   respawn-gate moment are not guaranteed to coincide. Needs a runtime check; flagged as a partial-liveness risk.

### Latent value bug (does not fire if the cfg is loaded)
- `Survival.Activate()` hardcodes fallback defaults `warmup=5, round_timelimit=180, round_enddelay=5` when the
  cvar is unset. Base defaults are `10 / 120 / 0`. The shipped `gametypes-server.cfg` carries the correct values,
  so as long as that cfg is loaded into the cvar store the fallback never fires; if it isn't, the port diverges.

### Intended divergences
- The Common `RoundHandler` and the per-mode disclosure-over-a-status-block (instead of a CSQC linked entity +
  maxclients bit slots) are deliberate port architecture (stable net ids, headless sim). Behaviorally equivalent
  on the disclosure invariant; not flagged as a bug.

## Verification
- `tests/VortexArena.Tests/SurvivalRolesTests.cs` — role assignment (live-only count, bound formula, random
  pick, <2-player guard). Pass.
- `tests/VortexArena.Tests/SurvivalScoringTests.cs` — banked-kill award, timed-out reward, anonymization. Pass.
- `tests/VortexArena.Tests/GametypeStatusTests.cs` — hunter-id anti-cheat disclosure invariant. Pass.
- Code-trace: `GameWorld.ActivateGameType` (Survival arm + `EnableRounds()`), `DriveGametypeFrame` (Survival
  arm), `GametypeStatusBlock.Capture`, `NetGame.UpdateModIcons`, `ModIconsPanel.DrawSurvival`. The missing
  callers (EndRoundTimedOut, win notifications, punish-teamkill, Bot_ForbidAttack, MatchEnded) verified by
  absence of any reference in the server/common source.
- Unverified at runtime: whether the dual round-handler causes a visible desync of role-assign vs map-reset, and
  whether a round ever resolves at all in a live match (no timeout path, side-wipe only).

## Open questions
- Which round handler is intended to be authoritative for Survival — does `surv.Tick()`'s `Handler` actually
  drive the live round, or is it shadowed by `GameWorld.Rounds`? (Runtime trace needed.)
- Should the port wire `EndRoundTimedOut` to `Survival.Handler.RoundEndTime` (and the match timeout) to restore
  the "survive the timer" win, plus the win/role notifications, teamkill punishment and `Bot_ForbidAttack`?

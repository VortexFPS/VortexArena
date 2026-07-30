# sv-clientkill — parity spec

**Base refs:** `server/clientkill.qc` · `server/clientkill.qh` · callers in `server/command/cmd.qc`, `server/impulse.qc`, `server/player.qc`; mutator hooks in `common/gametypes/gametype/{cts,race,freezetag}/sv_*.qc`; announcer in `common/util.qc:Announcer_PickNumber`
**Port refs:** `src/VortexArena.Server/Commands.cs` (`CmdKill`, `CmdSelectTeam`, `CmdSpectate`) · `src/VortexArena.Server/Teamplay.cs:KillPlayerForTeamChange` · `src/VortexArena.Common/Gameplay/GameTypes/Cts.cs` (finish retract) · `src/VortexArena.Server/ClientCommandRegistry.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
This subsystem handles a player *voluntarily* removing themselves from play: the `kill` console command (suicide), a deferred team change (`selectteam`/`spectate` route through the same machinery), and silent scripted self-kills used by race/CTS finish logic. In Base it is **not** an instant death — it spawns a countdown "kill indicator" entity attached above the player's head that ticks down once per second, plays a number announcer ("3… 2… 1…"), shows a center-print ("Suicide in N" / "Changing to RED in N" / "Spectating in N"), and only at zero does it run the lethal `Damage(... DEATH_KILL ...)`. An anti-spam penalty extends the next allowed kill time if the player mashes `kill`. It also doubles as the deferred team-change mechanism: the chosen team/spectate intent is stashed in `killindicator_teamchange`, and when the countdown completes the player is moved (and killed) instead of just dying.

Activation: any normal match where a player types `kill`, uses `selectteam`/`spectate`, or finishes a race/CTS stage. The whole subsystem is server-side authority (`sv_`); the visible countdown is networked as a CSQC center-print + an `MSG_ANNCE` announcer cue.

## Base algorithm (authoritative)

### `ClientKill` — the `kill` command entry (`server/clientkill.qc:226`)
- **Trigger:** `CLIENT_COMMAND(kill)` (`cmd.qc:552`, gated on alive + not spec/observer), impulse-driven waypoint-clear in race/cts (`impulse.qc:505/518`).
- **Algorithm:** bail if `game_stopped` or `this.player_blocked`; else `ClientKill_TeamChange(this, 0)` (targetteam 0 = "just die").

### `ClientKill_TeamChange(this, targetteam)` — the core deferral (`clientkill.qc:94`)
- `targetteam`: `0` = die, `-1` = auto team, `-2` = spectate, `>0` = a specific team.
- **Algorithm (step-by-step):**
  1. Bail if `game_stopped`.
  2. `killtime = autocvar_g_balance_kill_delay` (default **2** s).
  3. `MUTATOR_CALLHOOK(ClientKill, this, killtime)` — if a mutator returns true, **abort entirely** (e.g. freezetag returns `STAT(FROZEN)` so a frozen player cannot self-kill). Otherwise re-read `killtime` from the hook's out-arg (cts forces `0`; race forces `0` only while `g_race_qualifying`).
  4. If a round is active but not started: `killtime = min(killtime, 1)`.
  5. If `targetteam == -1` (auto): if `team <= 0` set `team_selected = -1` and **return** (defer autoselect to Join); else compute the best team now and, if it equals the current team, center-print `CENTER_TEAMCHANGE_ALREADYBEST` and return; otherwise set `targetteam` to that team.
  6. `this.killindicator_teamchange = targetteam`.
  7. If `killtime <= 0` and a *silent* killindicator already exists (`count == 1`): `ClientKill_Now` (instant) and return.
  8. If no killindicator yet:
     - If alive: `killtime = max(killtime, clientkill_nexttime - time)` (carry forward any pending anti-spam window); `antispam_delay = autocvar_g_balance_kill_antispam` (default **5** s), clamped to `min(_, 2)` during an unstarted round; `clientkill_nexttime = time + killtime + antispam_delay`.
     - If `killtime <= 0 || !IS_PLAYER || IS_DEAD`: `ClientKill_Now` (instant).
     - Else **spawn the killindicator**: `new(killindicator)`, `scale 0.5`, attached to player, origin `'0 0 52'`, think = `KillIndicator_Think`, `nextthink = max(time, clientkilltime) + lip*0.05`, advance the shared `clientkilltime` so staggered, `cnt = ceil(killtime)`, `count = bound(0,ceil(killtime),10)`. Also clone a killindicator onto every `g_clones` ghost of this player. Reset `this.lip = 0`.
  9. If a killindicator now exists, set its `colormod` + choose the center notif by target: `0` → black + `CENTER_TEAMCHANGE_SUICIDE`; `-2` → grey + `CENTER_TEAMCHANGE_SPECTATE`; `>0` → team color + `APP_TEAM_NUM(targetteam, CENTER_TEAMCHANGE)`. If real client and `cnt > 0`, `Send_Notification(MSG_CENTER, notif, cnt)`.

### `KillIndicator_Think` — the per-second countdown (`clientkill.qc:61`)
- Runs every `time + 1`. If `game_stopped` or the owner is gone (`alpha < 0` and no vehicle), detach + delete. When `cnt <= 0`, run `ClientKill_Now(owner)`. Otherwise (unless `count == 1`, the silent flag): if `cnt <= 10` `setmodel(MDL_NUM(cnt))` (the floating digit model) and, for a real client, `Send_Notification(MSG_ANNCE, Announcer_PickNumber(CNT_KILL, cnt))` — the spoken "ten…one" countdown. Then `nextthink = time + 1; --cnt`.

### `ClientKill_Now(this)` — the lethal step (`clientkill.qc:36`)
- If in a vehicle, `vehicles_exit(VHEF_RELEASE)` and (unless a team change) force-damage the vehicle. Delete the killindicator. If `killindicator_teamchange != 0` → `ClientKill_Now_TeamChange` (which moves team or becomes observer, and the team move itself does the kill). Else, if not spec/observer and the `ClientKill_Now` mutator hook returns false, `Damage(this, this, this, 100000, DEATH_KILL, DMG_NOWEP, origin, '0 0 0')` — the actual death.

### `ClientKill_Now_TeamChange(this)` (`clientkill.qc:18`)
- `killindicator_teamchange == -2` → `PutObserverInServer` (becomes spectator; warns if `sv_spectate 0`). Else `SetPlayerTeam(Team_TeamToIndex(target), TEAM_CHANGE_MANUAL)` (this kills the player as part of the move). Clear the flag; rebalance if `g_balance_teams_remove`.

### `ClientKill_Silent(this, _delay)` (`clientkill.qc:212`)
- Spawns/reuses a killindicator with `count = 1` (silent: no announcer, no center-print, no digit model), `cnt = ceil(_delay)`, think `KillIndicator_Think`. Used by CTS finish (`g_cts_finish_kill_delay`, default **2** s) to silently kill the runner after they cross the line so they cannot keep their speed.

### Constants / cvars
- `g_balance_kill_delay` — default **2** s (kill countdown length). Authority.
- `g_balance_kill_antispam` — default **5** s (added to the next-allowed-kill time on repeat use; XPM/XDF rulesets set `0`). Authority.
- `g_cts_finish_kill_delay` — default **2** s (silent finish kill; `-1` = instant, `0` = never). Authority (cts).
- killindicator entity: `scale 0.5`, origin `'0 0 52'` above the head, digit model `MDL_NUM(cnt)` for cnt 1..10, colormod per target, `lip*0.05` stagger. Presentation.
- Announcer cue: `Announcer_PickNumber(CNT_KILL, cnt)` → `ANNCE_NUM_KILL_1..10` (`MSG_ANNCE`). Presentation.
- Center notifs: `CENTER_TEAMCHANGE_SUICIDE`, `CENTER_TEAMCHANGE_SPECTATE`, `CENTER_TEAMCHANGE_{RED,BLUE,YELLOW,PINK}`, `CENTER_TEAMCHANGE_ALREADYBEST` (all carry `^COUNT`). Presentation.

### State / networking
- `.entity killindicator`, `.int killindicator_teamchange`, `.float lip`, global `clientkilltime`, `.float clientkill_nexttime`. The killindicator is a real networked entity (CSQC sees the floating digit); the center-print + announcer are `Send_Notification` messages.

## Port mapping
- **`kill` command** → `Commands.CmdKill` (`Commands.cs:1128`), registered as a live client command (`ClientCommandRegistry.cs:49`). It guards spectator/dead, then **immediately** `Combat.Damage(p, null, null, 100000, DeathTypes.Kill, …)`. There is **no** `ClientKill_TeamChange` deferral, no killindicator, no countdown, no announcer, no center-print, no anti-spam, and no `ClientKill`/`ClientKill_Now` mutator hooks.
- **`selectteam` / `spectate`** → `CmdSelectTeam` (`:1112`) sets `Caller.Team` then `Teamplay.KillPlayerForTeamChange` (instant `DEATH_AUTOTEAMCHANGE` damage); `CmdSpectate` (`:1090`) calls `PutObserverInServer` immediately. Both bypass the `killindicator_teamchange` deferral and the team-change countdown entirely.
- **`KillPlayerForTeamChange`** → `Teamplay.cs:215`: clears aux score columns and force-damages the player (`DeathTypes.AutoTeamChange`) **instantly**. This is the analogue of `ClientKill_Now_TeamChange`'s kill, but with no preceding countdown.
- **CTS finish silent kill** → `Cts.cs:ScheduleRetract`/`Tick`/`RetractRunner` honors the *positive* `g_cts_finish_kill_delay` *duration* via a deferred timer, but on expiry it fires `OnFinishRetract`, wired in `GameWorld.cs:1376` to `Clients.Spawn(p)` — a **respawn/teleport**, not a `Damage(... DEATH_KILL ...)`. So the runner is sent to start but never actually dies through the damage pipeline (no death obituary, no death stats). It is also non-silent in the sense that it doesn't even use the kill machinery. The `-1`/`0` special cases are BOTH mishandled: `ScheduleRetract` uses `MathF.Max(0f, delay)`, so Base `-1` (= kill instantly) and Base `0` (= never kill) both collapse to "retract this same frame" — the `0 = never` case is silently broken.
- **Notifications** → the port *defines* `TEAMCHANGE_SUICIDE`/`SPECTATE`/per-team center-prints and `NUM_KILL_1..10` announcer entries (`NotificationsList.cs`), but `NUM_KILL_n` is registered `enabled: false` and nothing emits the countdown notifications, because the countdown logic doesn't exist.
- **cvars** → `g_balance_kill_delay` and `g_balance_kill_antispam` are **not registered** anywhere in the port (`Cvars.cs` has neither); only `g_cts_finish_kill_delay` exists (read in `Cts.cs`).

## Parity assessment

### Logic
- Suicide *eventually kills the player*: faithful in outcome, but the entire deferral/countdown state machine (`ClientKill_TeamChange` → killindicator → `KillIndicator_Think` → `ClientKill_Now`) is missing — the port kills instantly. The team-change-via-kill coupling (`killindicator_teamchange`, deferring auto-select to Join, "already best team" early-out) is absent; selectteam/spectate kill or transition instantly. Mutator gating is missing: a frozen freezetag player **can** self-kill in the port (Base's `ft` hook blocks it), and race/cts no longer need their `killtime=0` hooks because there's no delay anyway. This is a real gameplay difference (frozen-player exploit + no countdown to cancel a misclick).

### Values
- `g_balance_kill_delay` (2 s) and `g_balance_kill_antispam` (5 s) are unimplemented (effectively 0). `g_cts_finish_kill_delay` (2 s) duration is honored. Anti-spam carry-forward (`clientkill_nexttime`) absent.

### Timing
- Base: 1 s/tick countdown over `ceil(killtime)` seconds, staggered by `lip*0.05`, with a shared `clientkilltime` floor. Port: zero delay for suicide/team change; the CTS retract timer is the one timer that matches duration but resolves to a respawn, not a death.

### Presentation
- Missing entirely: floating digit killindicator model above the head, the center-print "Suicide in N / Changing to TEAM in N / Spectating in N" with live `^COUNT`, and the per-target colormod. The notification strings exist but are never sent.

### Audio
- Missing: the `ANNCE_NUM_KILL_n` spoken countdown ("ten…one") via `MSG_ANNCE`. The announcer entries exist but `NUM_KILL_n` is disabled and never invoked.

### Liveness
- `CmdKill` / `CmdSelectTeam` / `CmdSpectate` / `KillPlayerForTeamChange` are **live** (registered client commands + team-management callers). The countdown/indicator/announcer machinery is **na** (no code exists). The CTS finish retract is **live** but diverges (respawn not silent-kill).

### Intended divergence
- None documented as intentional. `planning/PLAYER_LOOP_RULES.md:103` explicitly lists the `/kill` countdown + anti-spam as **DEFERRED (P2, DEATH5) — currently instant**, i.e. a known, accepted-for-now gap rather than a deliberate design change. Treated here as a gap (not `intended_divergence`).

## Verification
- Base behavior read in full from `server/clientkill.qc`/`.qh`, callers in `cmd.qc`/`impulse.qc`/`player.qc`, mutator hooks in cts/race/freezetag, `Announcer_PickNumber` in `util.qc`, and cvar defaults in `xonotic-server.cfg`/`gametypes-server.cfg`.
- Port read from `Commands.cs` (`CmdKill`/`CmdSelectTeam`/`CmdSpectate`), `Teamplay.cs:KillPlayerForTeamChange`, `Cts.cs` (retract), `ClientCommandRegistry.cs`, `NotificationsList.cs`. Confirmed by grep that `g_balance_kill_delay`/`g_balance_kill_antispam` appear in no source file (only in a planning doc), and that no `killindicator`/`KillIndicator`/`ClientKill_*` symbol exists in the port.
- Not runtime-verified in-game; conclusions are from static reads of both trees. Confidence high on "instant kill, no countdown/indicator/announcer/anti-spam"; medium on the exact freezetag-frozen exploit (depends on whether some other port path blocks self-kill while frozen — none found).

### Adversarial-verification corrections (2026-06-22)
- **Frozen-self-kill exploit confirmed (was "medium" → now high).** `FreezeTag.cs:Freeze` sets `RES_HEALTH=1` + `IsFrozen` but leaves the player **not** `IsDead` (`IsEliminated = IsDead || IsFrozen` keeps them distinct). `CmdKill` only gates on `FragsSpectator`/`IsDead`, so a frozen player passes both gates and `Combat.Damage(100000)` kills them — defeating the freeze. Nothing in `FreezeTag.cs` intercepts the kill. Base's `ft` ClientKill hook returns `STAT(FROZEN)` to forbid exactly this.
- **`g_cts_finish_kill_delay` values downgraded faithful→partial.** Re-examined: Base `-1` = instant, `0` = never; the port's `MathF.Max(0f, delay)` makes both collapse to an immediate retract, so the `0 = never` case is broken (the draft scored this `faithful`/`match:true`).
- **New row added: `sv-clientkill.impulse.waypoint_kill`.** Base `impulse.qc:505/518` routes the personal-waypoint-clear impulse through `ClientKill` when `(g_cts||g_race) && g_allow_checkpoints`. The port has no `waypoint_clear`/`wpeditor` personal-checkpoint feature (only the off-by-default `g_allow_checkpoints` cvar shell), so this Base caller of `ClientKill` is unmapped.
- **port_ref fix.** Registration of `kill` is `Commands.cs:509` (`Register("kill", …, CmdKill)`); `ClientCommandRegistry.cs:49` is only the privilege-allowlist string. Handler body is `Commands.cs:1128`.

## Open questions
- Does the port intend to keep CTS finish as a respawn (`Clients.Spawn`) permanently, or restore the silent `DEATH_KILL` so the death registers in stats/obituary, and fix the `0 = never` case? Owner decision.

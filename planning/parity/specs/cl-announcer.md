# cl-announcer — parity spec

**Base refs:** `client/announcer.qc` · `client/announcer.qh` · `common/util.qc:Announcer_PickNumber` · `common/notifications/all.qc` (`Local_Notification_sound` / `Local_Notification_Queue_*` / `AnnouncerFilename`) · `common/notifications/all.inc` (the `MSG_ANNCE` notification table)
**Port refs:** `src/VortexArena.Server/AnnouncerController.cs` · `src/VortexArena.Server/GameWorld.cs` (`BroadcastGameStartCountdown` / `BroadcastRoundStartCountdown` / `AnnceIfEnabled` / the `Announcer.*` wiring) · `game/hud/HudNotifications.cs` (the client announcer-voice player + antispam + queue) · `game/hud/CenterPrintPanel.cs` (title/duel title) · `game/net/NetGame.cs` (live client routing)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The announcer is the client subsystem that (a) drives the pre-match / per-round **countdown** ("3-2-1 / prepare for battle / begin"), (b) plays the **remaining map-time** voice cues ("5 minutes remain" / "1 minute remains"), (c) sets the **centerprint title** above the countdown (gametype name, or the duel "A vs B" line in 1v1), and (d) provides the **announcer voice playback** path that *every* `MSG_ANNCE` notification flows through (countdown numbers, BEGIN, PREPARE, remaining-time, plus externally-driven cues such as headshot / killstreak / multifrag / vote / timeout). In Base this is a CSQC (client) timer run from `view.qc`. The port has **no CSQC announcer timer**, so the timing/decision logic was moved server-side (`AnnouncerController` + the `GameWorld` countdown broadcasters) and the result is broadcast to every client through `NotificationSystem`; the client then realizes the `MSG_ANNCE` event as an actual voice sample in `HudNotifications`.

## Base algorithm (authoritative)

### Driver entry  (`client/announcer.qc:Announcer`)
- **Trigger:** CSQC, called every frame from `view.qc:1810` (after the world is drawn). Returns immediately if there is no `gametype`. Calls `Announcer_Gamestart()` then `Announcer_Time()`.
- The notification **queue** is pumped separately each frame: `view.qc:1041 Local_Notification_Queue_Process()`.

### Announcer_Gamestart  (`announcer.qc:127-174`)
- Reads `STAT(GAMESTARTTIME)` and `STAT(ROUNDSTARTTIME)`; if `time > startTime && roundstarttime > startTime` it uses `roundstarttime` as the effective start.
- During `intermission || warmup_stage`: tears down any live `announcer_countdown` think entity, clears the title, kills `centerprint(CPID_ROUND)`, returns.
- If a countdown is live and `gametype.m_1v1`, refreshes the duel title (`Announcer_Duel`).
- **One-shot on restart:** when `previous_game_starttime != startTime` and `time < startTime`, it (re)creates the `announcer_countdown` think entity, sets the title (duel title in 1v1, else `^BG<MapInfo_Type_ToText(gametype)>` = the gametype display name), and plays `ANNCE_PREPARE` **once** — but only if `time + 5.0 < startTime` (i.e. ≥5 s of countdown remain, so a late join into an in-progress restart does not always replay "prepare for battle"). Then it synchronizes `nextthink` to `startTime`.

### Announcer_Countdown  (`announcer.qc:58-117`)  — the per-tick countdown think
- **roundstarttime == -1** (round cannot start): plays `CENTER_COUNTDOWN_ROUNDSTOP` ("^F4Round cannot start"), deletes itself, clears title. Returns.
- `inround = (roundstarttime && time >= starttime)`; `countdown = inround ? roundstarttime - time : starttime - time`; `countdown_rounded = floor(0.5 + countdown)`.
- On a start-time change it resets `this.skin = 0` to restart the centerprint countdown.
- **countdown ≤ 0** (start reached): plays `CENTER_COUNTDOWN_BEGIN` + the `COUNTDOWN_BEGIN` multi (→ `ANNCE_BEGIN`), deletes itself, clears title.
- **Otherwise (still counting):**
  - **In round:** on the first tic shows `CENTER_COUNTDOWN_ROUNDSTART` with `STAT(ROUNDS_PLAYED)+1` and the rounded count; picks `Announcer_PickNumber(CNT_ROUNDSTART, n)` and plays it as `MSG_ANNCE`. `nextthink = roundstarttime - (countdown - 1)`.
  - **Pre-game (no round):** on the first tic shows `CENTER_COUNTDOWN_GAMESTART` with the rounded count; picks `Announcer_PickNumber(CNT_GAMESTART, n)` but only plays it when `!roundstarttime` (round modes don't announce game start). `nextthink = starttime - (countdown - 1)`.
  - Sets `this.skin = 1` after the first tic so the centerprint countdown auto-continues and `^COUNT` isn't re-shown under high slowmo / lag.

### Announcer_PickNumber  (`common/util.qc:1924-2016`)
- Pure switch on (`type` ∈ {`CNT_GAMESTART`, `CNT_KILL`, `CNT_RESPAWN`, `CNT_ROUNDSTART`, `CNT_NORMAL`}, `num` ∈ 1..10) → the matching `ANNCE_NUM_*` notification, else NULL. The announcer only uses the GAMESTART / ROUNDSTART branches; KILL / RESPAWN / NORMAL are used by other callers (clientkill, instagib timer).

### Announcer_Time  (`announcer.qc:188-226`)  + `ANNOUNCER_CHECKMINUTE` (176-186)
- Static `warmup_stage_prev`; client globals `announcer_5min`, `announcer_1min` (hysteresis latches).
- Returns during `intermission`. A warmup↔match stage flip clears both latches and skips one tick.
- Before the match goes live (`time < starttime`) clears both latches.
- `timeleft`: in warmup uses `STAT(WARMUP_TIMELIMIT)` (≤0 → 0), else `STAT(TIMELIMIT)*60`; both `max(0, limit + starttime - time)`.
- `ANNOUNCER_CHECKMINUTE(m)`: latch set → clears when `timeleft > m*60`; latch clear → fires `ANNCE_REMAINING_MIN_m` and sets the latch when `timeleft < m*60 && timeleft > m*60 - 1` (a 1-second arming window so it fires once per crossing).
- Gating: `cl_announcer_maptime >= 2` → check 5; `== 1 || == 3` → check 1.

### Announcer_Duel / titles  (`announcer.qc:29-53`, `client/hud/panel/centerprint.qc`)
- `Announcer_Duel`: reads the top two sorted players; if names changed, `centerprint_SetDuelTitle(pl1, pl2)`.
- `centerprint_SetTitle` / `centerprint_ClearTitle` set/clear the bold title line above the centerprint countdown.

### Announcer voice playback  (`common/notifications/all.qc:985-1156`, `all.qc:388`)
- `AnnouncerFilename(snd) = "announcer/<cl_announcer>/<snd>.wav"` — the voice pack is `autocvar_cl_announcer` (default `"default"`).
- `Local_Notification_sound`: **antispam dedup** — plays only if `soundfile != prev_soundfile || time >= prev_soundtime + autocvar_cl_announcer_antispam`; on a play, records prev_soundfile / prev_soundtime. Calls `_sound(NULL, channel, file, vol, position)`.
- `Local_Notification_Queue_Add`: `queue_time == 0` → guess `soundlength(file)`; `== -1` or `time > notif_queue_next_time` → play now and bump `notif_queue_next_time`; else enqueue (parallel arrays, cap `NOTIF_QUEUE_MAX`).
- `Local_Notification_Queue_Process`: if the front entry's time has arrived, run it and shift the queue left.

### Constants / cvars (Base defaults)
| Cvar / constant | Base default | Side | Meaning |
|---|---|---|---|
| `cl_announcer` | `"default"` | cl (presentation) | voice pack directory under `sound/announcer/` |
| `cl_announcer_antispam` | `2` (seconds) | cl (presentation) | min gap before the *same* sample replays |
| `cl_announcer_maptime` | `3` | cl (presentation) | gate: `>=2` ⇒ 5-min cue, `==1\|\|==3` ⇒ 1-min cue. Shipped menu values: 0 off / 1 = 1-min / **5** = 5-min / 3 = both (the audio-settings slider's `addRange(1,5,4)` step=4 yields the points {1,5}). |
| `ANNCE_PREPARE` gate | `time + 5.0 < startTime` | shared | only "prepare for battle" if ≥5 s countdown |
| `NUM_GAMESTART_n` enabled | `n ≤ 5` (`N__ALWAYS`), n≥6 `N___NEVER` | presentation | which game-start counts speak |
| `NUM_ROUNDSTART_n` enabled | `n ≤ 3` (`N__ALWAYS`), n≥4 `N___NEVER` | presentation | which round-start counts speak |
| `REMAINING_MIN_1`/`_5` | `N__ALWAYS` | presentation | remaining-time cues default-on |
| `MULTIFRAG` | `N___NEVER` (default off) | presentation | externally-driven, not from this unit |
| `MSG_ANNCE` channel / vol / atten | `CH_INFO` / `VOL_BASEVOICE` / `ATTEN_NONE` | presentation | UI sound, non-positional |
| `NOTIF_QUEUE_MAX` | 10 | presentation | announcer queue depth |

## Port mapping

| Base feature | Port symbol | Liveness |
|---|---|---|
| `Announcer` per-frame driver | split: `AnnouncerController.Tick` (GameWorld:949, server) + countdown broadcasters + `HudNotifications` (client) | live |
| `Announcer_Gamestart` countdown setup + PREPARE one-shot | `GameWorld.BroadcastGameStartCountdown` (PREPARE-once via `_gameStartCountdownArmed`, the `>5 s` gate) | live |
| `Announcer_Countdown` per-tic number + center | `WarmupController.OnCountdownTick` / `RoundHandler.OnCountdownTick` → `BroadcastGameStart/RoundStartCountdown` (NUM_* + COUNTDOWN_* center + BEGIN) | live |
| `Announcer_PickNumber` (GAMESTART/ROUNDSTART) | `AnnouncerController.PickCountdownNumber` (pure helper) + `GameWorld.AnnceIfEnabled` registry gate | live (helper is auxiliary) |
| `countdown_rounded = floor(0.5+countdown)` | `AnnouncerController.CountdownRounded` | live (host advances per whole second) |
| `Announcer_Time` + `ANNOUNCER_CHECKMINUTE` latches | `AnnouncerController.Tick` + `CheckMinute` | live |
| roundstarttime == -1 → `COUNTDOWN_ROUNDSTOP` | NOT WIRED (registry has `COUNTDOWN_ROUNDSTOP` but no broadcaster) | dead |
| `Announcer_Duel` duel title | `CenterPrintPanel.SetDuelTitle` exists, **no live caller** | dead |
| gametype-name title (`Announcer_Gamestart:163`) | `CenterPrintPanel.SetTitle` exists, **no live caller** | dead |
| voice playback + antispam + queue | `HudNotifications.PlayAnnouncer/QueueRun/ProcessAnnouncerQueue` | live |
| `cl_announcer` voice pack | `HudNotifications.AnnouncerVoice` (hardcoded `"default"`, never read from cvar) | dead wiring |
| `cl_announcer_antispam` | `HudNotifications.AntiSpamInterval` (hardcoded `2f`, never read from cvar) | dead wiring |
| `cl_announcer_maptime` | `GameWorld:625` reads server **global** store; menu `DialogSettingsAudio` slider | live (intended server-side divergence) |

## Parity assessment

**Logic** is faithful and well-tested. The countdown number schedule, the PREPARE one-shot + ≥5 s gate, the round vs game-start branch (`!roundstarttime`), the registry Enabled gate matching the shipped NUM_* defaults, the `floor(0.5+countdown)` rounding, and the `ANNOUNCER_CHECKMINUTE` hysteresis latches (fire-once-per-crossing, re-arm on the way up, warmup-flip re-arm, intermission/pre-live clears, mode 0/1/2/3 gating) are all ported and covered by `CountdownAnnouncerTests` + `ClientFeedbackTests`. The voice-playback antispam dedup and the announcer queue (queuetime 0 = guess length, −1 = immediate, cap 10, shift-left process) are faithfully reproduced in `HudNotifications`.

**Gaps (concrete):**
- **Duel title is never shown.** `Announcer_Duel` / `centerprint_SetDuelTitle` is dead in the port — `CenterPrintPanel.SetDuelTitle` has no live caller, so in 1v1 the "PlayerA ^7vs^7 PlayerB" title above the countdown never appears.
- **Gametype-name title is never shown.** `Announcer_Gamestart` sets the title to the gametype display name (`^BG<MapInfo_Type_ToText>`); `CenterPrintPanel.SetTitle` has no live caller, so the title line above the pre-match countdown is blank.
- **`COUNTDOWN_ROUNDSTOP` ("Round cannot start") never fires.** The notification is registered but nothing broadcasts it (the `roundstarttime == -1` path is unmodeled in `RoundHandler`/`GameWorld`).
- **`cl_announcer` (voice pack) is dead.** `HudNotifications.AnnouncerVoice` is hardcoded `"default"` and never read from the cvar; changing `cl_announcer` has no effect. (Value coincidentally matches Base default; Xonotic ships only the "default" pack.)
- **`cl_announcer_antispam` is dead.** `HudNotifications.AntiSpamInterval` is hardcoded `2f` and never read from the cvar; the value matches the Base default so behavior is correct out of the box, but the cvar is inert.
- **`cl_announcer` voice cannot be overridden by mutators.** Base's `AnnouncerFilename` resolves the voice pack via `AnnouncerOption()` = `autocvar_cl_announcer` plus the `MUTATOR_CALLHOOK(AnnouncerOption)` override (e.g. overkill swaps the announcer voice). The port has no `AnnouncerOption` hook, so a mutator cannot change the announcer voice. (Same dead-wiring story as the `cl_announcer` cvar itself.)

**Intended divergences:**
- **Server-side announcer.** The whole timing/decision layer runs on the server and is broadcast, because the port has no CSQC announcer timer. Documented at `AnnouncerController` class summary + `GameWorld:616-625`.
- **`cl_announcer_maptime` is read from the server's global config store** rather than per-client, a consequence of the global broadcast. Documented in `../../../docs/reference/CVARS.md:128`. The menu mode *values* (0/1/5/3) are faithful to Base.

**Verifier correction (2026-06-22):** an earlier draft flagged the menu's "5 min" → `cl_announcer_maptime 5` as a bug (claiming Base's 5-min mode is value 2). This is FALSE. Base's shipped audio-settings slider (`menu/xonotic/dialog_settings_audio.qc:168`) uses `addRange(1, 5, 4)` whose third argument is the **step**, not a count (`menu/item/mixedslider.qc:MixedSlider_addRange`), so it yields exactly the values {1, 5}; "5 min" is literally value 5 in Base, and the port matches. The `>=2` gate means any value ≥2 reads as "5-min on", so 5 is both correct and canonical. The maptime feature row is therefore `logic/values: faithful` with only the server-global read scope diverging (intended).

**Liveness:** the live chain is `GameWorld.OnStartFrame → Announcer.Tick()` (remaining-time) and `WarmupController/RoundHandler.OnCountdownTick → BroadcastGameStart/RoundStartCountdown → NotificationSystem` (countdown), then on the client `ClientNet.NotificationReceived → NetGame.OnNotificationReceived → HudNotifications.OnNotification → PlayAnnouncer`, with `ProcessAnnouncerQueue` pumped each frame from `NetGame` (≈2104). The title methods and the ROUNDSTOP notification are the only present-but-dead pieces.

## Verification
- `tests/VortexArena.Tests/CountdownAnnouncerTests.cs` — countdown fires once per whole second (not per frame), maps to NUM_GAMESTART_5..1 / NUM_ROUNDSTART_3..1 + COUNTDOWN_* + BEGIN, and the disabled NUM_ROUNDSTART_5 is suppressed.
- `tests/VortexArena.Tests/ClientFeedbackTests.cs` — `PickCountdownNumber` (3/2/1, out-of-range → null, RoundStart family), `CountdownRounded` (`floor(0.5+x)`), and the `Announcer_Time` hysteresis (fire-once-per-crossing, mode 1/2/3 gating, intermission suppression).
- Voice-playback antispam + queue: logic verified by code read of `HudNotifications` against `common/notifications/all.qc`; not behaviorally tested in-game (no automated coverage of the client audio path) — **unverified at runtime**.
- Dead callers (titles, ROUNDSTOP, `cl_announcer`/`cl_announcer_antispam` wiring): verified by grep — zero live call sites / cvar reads.

## Open questions
- Does the port intend to surface the centerprint title (gametype name + duel "vs" line) at all, or was it deliberately dropped? `CenterPrintPanel` has the rendering but nothing drives it.
- Should `cl_announcer` / `cl_announcer_antispam` be wired to the live cvars (so they're user-configurable), or is the hardcoded default acceptable given only the "default" pack ships?
- The `roundstarttime == -1` "round cannot start" condition: is it ever produced by any port gametype's round handler? If not, `COUNTDOWN_ROUNDSTOP` may be intentionally unreachable.

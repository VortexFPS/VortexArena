# Centerprint HUD panel (cl-centerprint) — parity spec

**Base refs:** `client/hud/panel/centerprint.qc` · `client/hud/panel/centerprint.qh` · `client/announcer.qc` · `common/notifications/all.qc` (`Local_Notification` / `Local_Notification_centerprint_Add` / `ENT_CLIENT_NOTIFICATION` NET_HANDLE) · `common/notifications/all.qh` (`ENUMCLASS(CPID)`)
**Port refs:** `game/hud/CenterPrintPanel.cs` · `game/hud/HudNotifications.cs` · `game/net/NetGame.cs` (`OnNotificationReceived`) · `game/net/ClientNet.cs` (`HandleReliableBundle`) · `src/VortexArena.Common/Gameplay/Notifications/NotificationSystem.cs` · `src/VortexArena.Server/GameWorld.cs` (countdown broadcast) · `src/VortexArena.Server/AnnouncerController.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The centerprint panel (HUD panel #16) is the big stack of timed messages at screen-center: frag/objective lines, the MOTD, the pre-match / round countdown ("Game starts in 3"), the gametype title (or the "left vs right" duel title), CTF pickup/capture/return lines, item pickups, mutator lines (NIX/instagib/campcheck), etc. It is a **presentation** subsystem (CSQC-only in Base): all rules/timing of *which* messages appear live elsewhere; this panel only renders, queues, fades, replaces (by `cpid` group), counts down (`^COUNT`), and kills messages, and draws an optional title line. It is driven by `Local_Notification(MSG_CENTER, …)` (server-pushed notifications via `ENT_CLIENT_NOTIFICATION`, or local) and, for the title + countdown, by the client-side `Announcer()` think loop reading the game-start/round-start stats.

## Base algorithm (authoritative)

### Message ring + add/replace  (`centerprint.qc:centerprint_Add`)
- **Trigger:** `Local_Notification_centerprint_Add` (from `Local_Notification` MSG_CENTER), `centerprint_AddStandard` (MOTD/`CSQC_Parse_CenterPrint`, `cl_cmd` debug), HUD-config preview.
- **Algorithm:** strip leading+trailing `\n`; bail if message empty and id 0. Ring of `CENTERPRINT_MAX_MSGS = 10` parallel arrays indexed by a descending `cpm_index`. If `new_id` (the CPID ordinal) matches an existing slot: empty message ⇒ fade the slot out (`time = min(5, fade_out)`, `start_time = 0`); else replace that slot in place. New ids prepend at `--cpm_index` (wrapping). `duration < 0` ⇒ sticky/forced (`time = -1`); `duration == 0` ⇒ `max(1, hud_panel_centerprint_time)`. `countdown_num` is the `^COUNT` start value.
- **Constants:** `CENTERPRINT_MAX_MSGS=10`, `CENTERPRINT_MAX_ENTRIES=50`, `CENTERPRINT_BASE_SIZE=1.3`, `CENTERPRINT_SPACING=0.3`, `CENTERPRINT_TITLE_SPACING=0.35`.

### Kill / kill-all  (`centerprint.qc:centerprint_Kill`/`centerprint_KillAll`; `all.qc` NET_HANDLE `MSG_CENTER_KILL`)
- `centerprint_Kill(id)` = `centerprint_Add(id, "", 0, 0)` ⇒ graceful fade-out of the group. `centerprint_KillAll()` clears all slots. The server can push **`MSG_CENTER_KILL`**: `net_name == CPID_Null` ⇒ `centerprint_KillAll()`, else `centerprint_Kill(ORDINAL(net_name))` to kill a whole `cpid` group remotely (`all.qc:1372-1392`). Also `centerprint_KillAll()` on notification queue clear (`all.qc:116`) and HUD-config enter/leave.

### Countdown + ^COUNT  (`centerprint.qc:HUD_CenterPrint` draw loop, lines 338-360, 407-410)
- A slot with `countdown_num` and a finite `time` and a `start_time`: when its window passes, decrement the number, drop it at 0, else extend `expire_time += time`. `^COUNT` token is `strreplace`'d with the live number each draw. Countdowns force `fade_in = fade_out = 0` and are always laid out (hold their position) even at ~0 alpha.

### Title / duel title  (`centerprint.qc:269-336`, driven by `announcer.qc`)
- `centerprint_SetTitle(title)` shows a single bold title line above messages, drawn at `fontscale_title`. `centerprint_SetDuelTitle(left,right)` shows "left  vs  right" centered with an underline under each name (names shortened to `hud_panel_scoreboard_namesize`). `centerprint_ClearTitle()` clears both.
- **Driver (`announcer.qc:Announcer`→`Announcer_Gamestart`):** on the pre-match restart, if `gametype.m_1v1` ⇒ `Announcer_Duel()` (sets duel title, refreshed when the two players change); else `centerprint_SetTitle("^BG" + MapInfo_Type_ToText(gametype))`. The title is cleared when the match begins / round stops (`Announcer_ClearTitle`, `centerprint_Kill(CPID_ROUND)`).

### Countdown driver  (`announcer.qc:Announcer_Countdown`)
- A client-side think entity reading `STAT(GAMESTARTTIME)` / `STAT(ROUNDSTARTTIME)`. Emits `Local_Notification(MSG_CENTER, CENTER_COUNTDOWN_GAMESTART, countdown_rounded)` (or `…_ROUNDSTART, round+1, countdown_rounded`) **once** on the first tic (`this.skin` latch), letting the centerprint's own `^COUNT` machinery count the rest down locally; emits `…_BEGIN` / `…_ROUNDSTOP` at the end. Plays the `NUM_*` announcer numbers on each tic. Round-based modes don't speak the game-start number.

### Fade + layout  (`centerprint.qc:HUD_CenterPrint`)
- Per-message alpha = fade-in ramp (`fade_in`), steady 1, or fade-out ramp (`fade_out`), times the **subsequent-message progressive fade** (two passes: `1 - g/passone` clamped ≥ `passone_minalpha`, then `1 - g/passtwo` ≥ `passtwo_minalpha`), times `panel_fg_alpha`. Font size scales with alpha down to `fade_minfontsize`. Word-wrapped to panel width; `^BOLD`-prefixed lines use the bold font at `fontscale_bold`; lines color-coded. `flip` lays bottom-up. Panel slides below the scoreboard/radar when they're open. `CPID_TIMEIN` is exempt from fading.
- **Cvar defaults (centerprint.qh):** `fade_in=0.15`, `fade_out=0.15`, `fade_subsequent=1`, `passone=3`, `passone_minalpha=0.5`, `passtwo=10`, `passtwo_minalpha=0.5`, `fade_minfontsize=1`, `fontscale=1`, `fontscale_bold=1.4`, `fontscale_title=1.8`, `dynamichud=1`, `align`/`flip`/`time` per-skin (`align=0.5`, `flip=0`, `time` 0⇒default-min-1; the shipped HUD skins set `time` via `_hud_common.cfg`). NOTE the **default skin** ships `fontscale_bold=1.2`, `fontscale_title=1.3` (e.g. `hud_luma.cfg`), overriding the header defaults.

### CPID groups (`all.qh:ENUMCLASS(CPID)`)
33 ids incl. `Null`, `ASSAULT_ROLE`, `ROUND`, `CAMPCHECK`, `CTF_CAPSHIELD`, `CTF_LOWPRIO`, `CTF_PASS`, `STALEMATE`, `NADES`, `IDLING`, `REMOVE`, `ITEM`, `PREVENT_JOIN`, `KEEPAWAY(_WARN)`, `KEYHUNT(_OTHER)`, `LMS`, `MISSING_PLAYERS`, `INSTAGIB_FINDAMMO`, `NIX`, `ONSLAUGHT`, `ONS_CAPSHIELD`, `OVERTIME`, `POWERUP`, `RACE_FINISHLAP`, `SURVIVAL`, `TEAMCHANGE`, `TIMEOUT`, `TIMEIN`, `VEHICLES(_OTHER)`. These tag messages for replace (same id ⇒ replace) and remote group-kill.

## Port mapping

| Base feature | Port symbol | Liveness |
|---|---|---|
| `centerprint_Add` ring/replace | `CenterPrintPanel.Push` (id ⇒ `_messages.RemoveAll(Id==id)`) | LIVE via `HudNotifications.ShowCenter` |
| `centerprint_AddStandard` / raw `centerprint()` builtin (door/trigger/target `.message`, item hints, MOTD) | `CenterPrintPanel.Add`/`AddTimed` | DEAD (no caller); mapobjects play only the audible half + DROP the text |
| Server `tell`/private-msg centerprint (`cmsg`/`sourcecmsg`) | `Chat.CenterPrint` → `Chat.DeliveredCenter` | DEAD (recorded server-side, never networked/routed) |
| HUD-config live preview messages + "Title" | NOT IMPLEMENTED (no `_hud_configure` preview branch) | n/a |
| `centerprint_Kill` (graceful group fade) | `CenterPrintPanel.Kill` | DEAD (no caller) |
| `centerprint_KillAll` | `CenterPrintPanel.ClearAll` | DEAD (no caller) |
| `MSG_CENTER_KILL` remote group/all kill | NOT IMPLEMENTED (no wire msg, no handler) | n/a |
| `^COUNT` countdown decrement | `CenterPrintPanel._Process` + `MessageAlpha` | LIVE but driven differently (see below) |
| `centerprint_SetTitle` (gametype title) | `CenterPrintPanel.SetTitle` + `DrawTitle` | DEAD (no caller) |
| `centerprint_SetDuelTitle` | `CenterPrintPanel.SetDuelTitle` + `DrawTitle` | DEAD (no caller) |
| `centerprint_ClearTitle` | `CenterPrintPanel.ClearTitle` | DEAD (no caller) |
| `Announcer_Gamestart` title driver | NOT IMPLEMENTED (no client title driver) | n/a |
| `Announcer_Duel` | NOT IMPLEMENTED | n/a |
| Countdown driver (`Announcer_Countdown`) | `GameWorld.BroadcastGameStartCountdown` / `BroadcastRoundStartCountdown` (SERVER-side, per-second re-broadcast) | LIVE |
| fade in/out + subsequent two-pass | `CenterPrintPanel.MessageAlpha` | LIVE |
| `flip`/`align`/wrap/`^BOLD`/font scales | `CenterPrintPanel.DrawPanel`/`DrawMessage`/`WrapLine` | LIVE |
| `MSG_CENTER` notification feed | `NotificationSystem.Send(…Center…)` → `ServerNet.WriteNotification` → `ClientNet.HandleReliableBundle` → `NetGame.OnNotificationReceived` → `HudNotifications.ShowCenter` → `Push` | LIVE |
| MOTD raw `centerprint`/`CSQC_Parse_CenterPrint` | NOT IMPLEMENTED (welcome shown via `DialogWelcome` menu, not the panel) | n/a |

## Parity assessment

**Logic / values / presentation of the panel itself — faithful.** `CenterPrintPanel.cs` is a careful, well-annotated reimplementation: the ring cap (10), base size 1.3, spacing 0.3, title spacing 0.35, the replace-by-id semantics, the two-pass subsequent fade with the exact `passone/passtwo` floors, the alpha→fontsize shrink, `^BOLD` per-line bold font, word-wrap, `flip`/`align`, and the title/duel-title block all match Base. The behaviour cvars are registered with matching header defaults and read live.

**Gaps:**
1. **`fade_in` default is `0` in the port vs `0.15` in Base** (`RegisterDefaults` registers `hud_panel_centerprint_fade_in` "0"; the C# field default also reads `0f`). Messages pop in instantly instead of the 0.15 s ramp. (`fade_out` 0.15 matches.)
2. **No title at all on the live path.** `SetTitle`/`SetDuelTitle`/`ClearTitle` exist but have **zero callers** — there is no port equivalent of `Announcer()`/`Announcer_Gamestart`/`Announcer_Duel`. So the gametype-name title (e.g. "^BGCapture the Flag") above the countdown and the "P1 vs P2" duel title never appear. This is a visible presentation regression on every map start and in Duel.
3. **No remote kill.** `MSG_CENTER_KILL` (group-kill / kill-all from the server) is not on the wire and not handled; `CenterPrintPanel.Kill`/`ClearAll` are dead. Base uses this to retract a centerprint group remotely (e.g. clear `CPID_ROUND` when a countdown is aborted, kill `CPID_CTF_*` lines, the notification-queue clear). In the port these lines just expire on their timers instead of being cleared on the triggering event.
4. **Countdown is server-driven, not client-`^COUNT`-driven, AND `^COUNT` reads the wrong arg in round modes.** Base emits the countdown center *once* and lets the client `^COUNT` machinery tick it down locally; the port re-broadcasts `COUNTDOWN_GAMESTART`/`_ROUNDSTART` from the server **every second** (`GameWorld.Broadcast*Countdown`) AND the client `CenterPrintPanel` *also* runs its own `^COUNT` decrement (`_Process`). The per-second re-broadcast cadence is an *intended* consequence of the no-CSQC architecture. **But** `HudNotifications.ShowCenter` hardcodes the `^COUNT` value to `flts[0]` regardless of the notification's `durcnt` arg selector. `COUNTDOWN_ROUNDSTART`'s `durcnt` is `"1 f2"` (count = f2 = seconds; f1 = round# feeds the `%s` in "Round %s starts in"), but the port broadcasts `(round+1, seconds)`, so `flts[0]` = the round number — the `^COUNT` shows the **round number** where the seconds should count down. This is an **unintended** defect (GAMESTART's `flts[0]` happens to be the seconds, so it is correct). The `durcnt` per-step duration (1s) is also ignored — `Push` always uses `DefaultDuration=3`. End-of-countdown uses `MSG_MULTI "BEGIN"` (→ `COUNTDOWN_BEGIN`, `CPID_ROUND`) which matches.
5. **The entire raw `centerprint()` engine-builtin feed is dead — far broader than MOTD.** Not just `CSQC_Parse_CenterPrint`/`centerprint_AddStandard`: the centerprint TEXT half of map/trigger `.message` (doors — "you need the key", `target_print`, item pickup hints, Q3-compat `target_print`) is **systematically dropped** by the port. `MapObjectsCommon.cs:365-366`, `TargetUtilities.cs:495/796`, `CompatRemaps.cs:421-422/466` all explicitly "play the audible half" and discard the centerprint string as "presentation/client-side". `CenterPrintPanel.Add` is caller-less. Any map relying on door/trigger/target centerprint text shows only the talk sound, no on-screen text. MOTD/welcome uses the `DialogWelcome` menu instead.
6. **Server `tell`/private-message centerprint is recorded but never delivered.** Base `server/chat.qc` builds the "X tells you:" centerprint string (`cmsgstr`/`sourcecmsgstr`) and centerprints it to the recipient. The port's `Chat.CenterPrint` builds the text and records it onto `Chat.DeliveredCenter`, but that list has **no consumer** — it is only `Add`'d and `Clear`'d, never networked to a client or routed to the panel. So `/tell` centerprints never appear.
7. **No HUD-config live preview.** When the HUD editor is open, Base generates rotating sample centerprint lines + a "Title" so the panel previews while being positioned (`HUD_CenterPrint` `_hud_configure` block). The port's panel has no such branch and renders empty in the editor.
8. **Default-skin font-scale overrides not applied:** Base's shipped HUD skins (`hud_luma.cfg`/`hud_luminos*.cfg:290-291`) set `fontscale_bold=1.2`/`fontscale_title=1.3`; the port keeps the header defaults 1.4/1.8. Titles never render anyway (gap 2); bold message lines render slightly large. Minor. (Also: the port does not reproduce Base's slide-below-scoreboard/radar repositioning — minor layout omission.)

**Liveness:** The core add/render/fade/countdown path **is live** (server `NotificationSystem` Center callers across CTF/instagib/NIX/items/buffs/LMS + the countdown broadcast → reliable bundle / local `HudSink` → `HudNotifications.ShowCenter` → `Push`). NOTE that `Push` is the **only** live entry — `ShowCenter` always passes `DefaultDuration=3` and never a sticky (<0) or time-cvar default, so those add-paths are coded-but-unexercised. The **title, duel-title, graceful-kill, remote-kill, raw-builtin, chat-`tell`, and HUD-config-preview features are dead/missing** (present but uncalled, or not implemented).

**Intended divergences:** The server-side countdown/announcer split (`AnnouncerController` + `GameWorld.Broadcast*Countdown`) is a deliberate port decision — the port has no CSQC think loop, so the announcer runs on the server and re-broadcasts each second (vs Base's once-then-local-`^COUNT`). That **cadence** is intended. Everything else flagged here is an *unintended* gap, including: the `^COUNT`-reads-`flts[0]` bug (wrong number in round modes), the missing client title driver, the dead kill/remote-kill, and the dropped raw-`centerprint()` text. The countdown row is therefore marked `intended_divergence: false` so the wrong-`^COUNT` bug is not permanently excused; the cadence-is-deliberate point lives in its `notes`.

## Verification
- Code read of `centerprint.qc`/`.qh`, `announcer.qc`, `all.qc` notification dispatch + `MSG_CENTER_KILL` handler, `all.qh` CPID enum — Base side fully traced.
- Port: full read of `CenterPrintPanel.cs`, `HudNotifications.cs`; liveness traced `NetGame.OnNotificationReceived` → `HudNotifications.ShowCenter` → `Push` (LIVE) and `NotificationSystem.Center(…)` server callers (grep, many live). `SetTitle`/`SetDuelTitle`/`ClearTitle`/`Kill`/`ClearAll` confirmed caller-less by repo-wide grep (DEAD). No `MSG_CENTER_KILL`/`centerprint_Kill`/MOTD-route equivalent found.
- Tests: `ObituaryEmissionTests.cs` covers server-side MSG_CENTER *emission* (FRAG choice), not the panel's rendering/fade/title — the panel itself is **unverified by tests**.
- Cvar defaults compared header-vs-`RegisterDefaults`: `fade_in` mismatch (0 vs 0.15) found by diff.

## Open questions
- Is the missing gametype/duel title an intentional simplification or a TODO? (No rationale in code — treated as a gap.)
- Does any server-side script path need the raw `centerprint` builtin (map-trigger messages)? If maps rely on it, gap 5 becomes gameplay-visible.
- Runtime check needed: confirm the per-second countdown re-broadcast doesn't visibly stutter the `^COUNT` number vs Base's smooth local tick (timing-only; not behaviorally wrong).

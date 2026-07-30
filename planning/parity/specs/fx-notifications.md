# fx-notifications — parity spec

**Base refs:** `common/notifications/all.qh`, `common/notifications/all.qc`, `common/notifications/all.inc`, `client/announcer.qc`, `common/util.qc:Announcer_PickNumber`
**Port refs:** `src/VortexArena.Common/Gameplay/Notifications/{Notification,NotificationSystem,NotificationTokens,NotificationsList,NotificationChoiceState}.cs`, `src/VortexArena.Server/AnnouncerController.cs`, `game/hud/{HudNotifications,CenterPrintPanel,NotifyPanel}.cs`, `game/net/{ServerNet,ClientNet,NetGame}.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The notification system is Xonotic's unified message dispatcher: every kill-feed line (MSG_INFO), centerprint (MSG_CENTER), announcer voice (MSG_ANNCE), and the bundles/choices that fan out to them (MSG_MULTI, MSG_CHOICE). The server emits a notification with `Send_Notification(broadcast, client, type, name, args…)`; it is networked as a linked entity (`ENT_CLIENT_NOTIFICATION`) to the appropriate clients, then `Local_Notification` on the client turns it into console print, HUD kill-notify icon, centerprint, or queued announcer sound. A second driver, `client/announcer.qc`, generates time-based announcements locally on the client (pre-match/round countdown 3-2-1-prepare, "5 minutes remain"/"1 minute remains", and the duel/gametype centerprint title). The registry of ~727 notifications lives in `all.inc` via the `MSG_*_NOTIF` macros.

This unit is heavily cross-cutting: nearly every gametype/weapon/mutator/item/scoring system is a *caller*. This spec audits the **infrastructure** (registry, arg-token expansion, networking/broadcast routing, the announcer queue + countdown/maptime driver, centerprint rendering), not the per-feature message text of every caller.

## Base algorithm (authoritative)

### Registry + message types  (`all.qh:16-29` ENUMCLASS(MSG), `all.inc`)
Five live types: `MSG_ANNCE` (announcer sound), `MSG_INFO` (global kill-feed/console line), `MSG_CENTER` (personal centerprint), `MSG_MULTI` (subcall annce+info+center), `MSG_CHOICE` (pick optiona/optionb by per-client cvar). A sixth `MSG_CENTER_KILL` is a deprecated kill-group marker used by `Kill_Notification`. Each notification is a `new_pure` entity with fields `nent_default`, `nent_enabled`, `nent_type`, `nent_stringcount`, `nent_floatcount`, `nent_teamnum`, plus type-specific payload (`nent_snd/channel/vol/position/queuetime` for annce; `nent_args/hudargs/icon/cpid/durcnt/string` for info+center; `nent_msg{annce,info,center}` for multi; `nent_optiona/optionb/challow_*/choice_idx` for choice). Registered through `REGISTRY(Notifications, BITS(11))` and sorted at init; the registry has a content hash so client and server agree.

Counts in this rev's `all.inc`: 80 `MSG_ANNCE_NOTIF` + 4 `_TEAM`, 237 `MSG_INFO_NOTIF` + 4 `_TEAM` + 1 `MULTIICON_INFO`, 197 `MSG_CENTER_NOTIF` + 4 `_TEAM`, 166 `MSG_MULTI_NOTIF`, 13 `MSG_CHOICE_NOTIF` + 4 `_TEAM`. MULTITEAM macros further expand a single name into RED/BLUE/YELLOW/PINK rows.

### Arg-token expansion  (`all.qh:424-470` NOTIF_ARGUMENT_LIST, `all.qc:922` Local_Notification_sprintf)
Each info/center notification declares an `args` string of space-separated tokens (one per template `%s`). At send/display time the token table resolves each token into a display string from `s1..s4` / `f1..f4`. Tokens include literal `s1..s4`; location suffixes `s2loc/s3loc/s4loc` (`" (near %s)"` gated on `notification_show_location`); numeric `f1..f4`, `f1dtime/f2dtime` (2-decimal), `f2primsec/f3primsec`, `f1secs`, `f1points`, `f1ord` (ordinal), `f1time`; race times `f{1,2,3}race_time`, `race_col`, `race_diff`; key hints `pass_key/nade_key/join_key`; frag presentation `frag_ping/frag_stats/frag_pos`; kill-spree `spree_cen/spree_inf/spree_end/spree_lost` (KILL_SPREE_LIST milestones 3/5/10/15/20/25/30); item names `item_wepname/item_buffname/item_wepammo/item_centime`; `death_team`; the `#s2`/`s3#s2` hash-replace; and `minigame1_name/minigame1_d`. Tokens are flagged by which program they resolve on (`ARG_CS_SV_HA` = client+server+hudargs, `ARG_CS` = client only, `ARG_SV` = server only, `ARG_DC` = durcnt only). `hudargs` (max 2) feed the HUD kill-notify icon; `durcnt` (max 2) feed centerprint duration/count.

- **Constants/cvars:** `notification_show_location=0`, `notification_show_location_string=""`, `notification_show_sprees=1`, `notification_show_sprees_info=3`, `notification_show_sprees_info_newline=1`, `notification_show_sprees_info_specialonly=1`, `notification_show_sprees_center=1`, `notification_show_sprees_center_specialonly=1`, `notification_item_centerprinttime=1.5` (CSQC), `notification_allow_chatboxprint=0` (disabled in code), `notification_errors_are_fatal=1`. `NOTIF_MAX_ARGS=7`, `NOTIF_MAX_HUDARGS=2`, `NOTIF_MAX_DURCNT=2`.

### Networking + broadcast routing  (`all.qc:1432-1676` Send_Notification, `all.qc:62-91` Notification_ShouldSend)
`Send_Notification` validates the broadcast/target (`Notification_CheckArgs`), checks `stringcount+floatcount==count`, and for non-choice types creates a `net_notification` linked entity (`Net_LinkEntity` with `Net_Write_Notification`) that writes byte(net_type) + short(net_name) + the strings + the longs. `MSG_CHOICE` is handled by per-client recursion (each client gets a `NOTIF_ONE_ONLY` resolved to their chosen option). `Notification_ShouldSend` is the per-recipient filter:
- `NOTIF_ONE` → `to == other || (IS_SPEC(to) && to.enemy == other)` (spectators following the target also get it).
- `NOTIF_ONE_ONLY` → `to == other`.
- `NOTIF_TEAM` → `to.team == other.team || (IS_SPEC(to) && to.enemy.team == other.team)`.
- `NOTIF_TEAM_EXCEPT` → team match AND not the excepted person (with the spec-follow nuance).
- `NOTIF_ALL` → everyone; `NOTIF_ALL_EXCEPT` → everyone except the excepted (and their spectators).
Recipients must be `IS_REAL_CLIENT`. On a dedicated server, `NOTIF_ALL`/`ALL_EXCEPT` non-annce/center notifs are *also* printed locally (`Local_Notification_Core`). `Kill_Notification` networks a `MSG_CENTER_KILL` and flips matching live notif entities to net_name=-1. Lifetime: `notification_lifetime_runtime=0.5` (SVQC), `notification_lifetime_mapload=10` (SVQC) — fresh-connect clients still see recent notifs.

### Local dispatch + announcer queue  (`all.qc:1176-1354`, `all.qc:984-1173`)
`Local_Notification` switches on type: MSG_INFO → `print(...)` + optional `HUD_Notify_Push(icon, …)`; MSG_CENTER → `centerprint_Add(cpid, text, dur, cnt)`; MSG_ANNCE → `Local_Notification_Queue_Add`; MSG_MULTI → recurse into each set sub; MSG_CHOICE → resolve & recurse. The announcer queue (`NOTIF_QUEUE_MAX=10`): `queue_time==0` → guess from `soundlength`; `queue_time==-1` or `time>queue_next_time` → play now and reserve `now+queue_time`; else append. `Local_Notification_sound` anti-spam: skip if same file within `cl_announcer_antispam=2`s of the previous play.

### Announcer time/countdown driver  (`client/announcer.qc`)
Client-side timer, not networked notifications: `Announcer_Gamestart` shows the gametype/duel centerprint title and `ANNCE_PREPARE` at restart; `Announcer_Countdown` emits `CENTER_COUNTDOWN_{GAMESTART,ROUNDSTART,BEGIN}` + the `ANNCE_NUM_{GAMESTART,ROUNDSTART}_n` number announcements (rounded `floor(0.5+countdown)`); `Announcer_Time` fires `ANNCE_REMAINING_MIN_{5,1}` via the `ANNOUNCER_CHECKMINUTE` hysteresis (window `timeleft < m*60 && timeleft > m*60-1`, latch persists). `Announcer_PickNumber(type, num)` (`util.qc`) maps a 1..10 second to the per-family `ANNCE_NUM_*` notif (GAMESTART/KILL/RESPAWN/ROUNDSTART). Cvars: `cl_announcer="default"`, `cl_announcer_antispam=2`, `cl_announcer_maptime=3` (0/1/2/3 = off/1min/5min/both).

### MSG_CHOICE replication  (`all.qh:882-894` ReplicateVars, `all.qc:707` Notification_GetCvars)
Each MSG_CHOICE has a per-client cvar `notification_<name>` replicated to the server (`msg_choice_choices[choice_idx]`): 0=off, 1=optiona (terse), 2=optionb (verbose). The server honours it only if `challow` allows (1=warmup-only, 2=always). Team-choice families share one `choice_idx` (counted once).

## Port mapping
The port is a faithful, server-authoritative re-implementation with a real client render layer and a live wire protocol.

| Base | Port |
|---|---|
| `ENUMCLASS(MSG)` | `MsgType` enum (`Notification.cs`) — 5 types (no separate MSG_CENTER_KILL) |
| notification entity + fields | `Notification` class; `Notifications` static registry (`Registry<Notification>`) |
| `all.inc` macro table | `NotificationsList.RegisterAll()` — **680** entries registered (vs ~727) |
| `MSG_*_NOTIF` macros | `Notifications.{Annce,Info,Center,Multi,Choice}` builders |
| `Send_Notification` | `NotificationSystem.Send(broadcast, target, type, name, args)` |
| `NOTIF` broadcast enum | `NotifBroadcast` enum |
| `Notification_ShouldSend` | `ServerNet.NotificationReaches` |
| `Net_Write_Notification` / `ENT_CLIENT_NOTIFICATION` | `ServerNet.{NotificationNetSink,WriteNotification,FlushNotifications}` → reliable bundle; `ClientNet` decode → `NotificationReceived` event |
| `NOTIF_ARGUMENT_LIST` / `Local_Notification_sprintf` | `NotifTokens.Resolve` + `NotificationSystem.FormatTokens`/`Sprintf` |
| MSG_CHOICE resolution + replication | `NotificationSystem.DispatchChoice` + `NotificationChoiceState` (cvar replicate) |
| `Local_Notification` client switch | `HudNotifications.OnNotification` (wired in `NetGame.OnNotificationReceived`) |
| `centerprint_Add` + cpid replace/kill + ^COUNT | `CenterPrintPanel.Push/Kill` |
| `HUD_Notify_Push` | `NotifyPanel.Push` |
| announcer queue + anti-spam | `HudNotifications` queue (`PlayAnnouncer`/`ProcessAnnouncerQueue`/`QueueRun`) |
| `Announcer_Time` + `Announcer_PickNumber` | `AnnouncerController.Tick` + `PickCountdownNumber` (server-side broadcast) |
| `Announcer_Countdown` (3-2-1-prepare) | `WarmupController`/`RoundHandler.OnCountdownTick` → `GameWorld.BroadcastGameStartCountdown` |

**Layer split:** the registry, send path, token expansion, choice resolution and broadcast routing are in `VortexArena.Common`/`Server` (authority/shared). The announcer queue, centerprint, kill-notify panel and announcer audio are in `game/hud` (presentation). Notably the **announcer time/countdown driver runs server-side** in the port (`AnnouncerController`, broadcast to all) rather than CSQC-local as in Base — a deliberate architectural divergence (the port has no CSQC announcer timer).

## Parity assessment

### Liveness — LIVE end-to-end.
`Notifications.RegisterAll()` runs at `GameInit`. Server gameplay calls `NotificationSystem.Send/Info/Center/Announce` from Ctf, LastManStanding, ItemPickupRules, Buffs/Campcheck/Instagib/KickTeamkiller/Nix mutators, WeaponFiring/Throwing, Scores, GameWorld, AnnouncerController (50+ live call sites). `ServerNet` installs `NotificationNetSink`; dispatches are filtered per-peer (`NotificationReaches`), serialized into the reliable bundle, decoded by `ClientNet` into `NotificationReceived`, and `NetGame.OnNotificationReceived` feeds `HudNotifications.OnNotification` → centerprint / kill-feed / announcer. Tested by `NotificationPolishTests`, `ObituaryEmissionTests`, `CountdownAnnouncerTests`, `ClientFeedbackTests`, `CvarReplicationTests`, `HeadshotTests`, `MonsterTurretVehicleObituaryTests`.

### Gaps (concrete defects)
1. **`durcnt` / per-notification centerprint duration is entirely dropped.** The port `Notification` has no `Durcnt`/`Duration` field; `Notifications.Center(...)` ignores the QC durcnt arg, and `NotificationDispatch`/`WriteNotification` carry no duration. `CenterPrintPanel.Push` always uses a fixed `DefaultDuration=3f`. Consequences: item-pickup centerprints that should show for `notification_item_centerprinttime=1.5`s, and every notification with an explicit durcnt duration, all render for 3s instead. The `^COUNT` *count* is reconstructed client-side from `f1`, so countdowns still work, but the *duration* is wrong for any non-default-duration message. (timing: partial)
2. **Gentle-mode message variants are never selected.** `NotificationSystem.GentleMode` exists but has no live setter (no read of `cl_gentle`/`sv_gentle`). `cl_gentle` gibs/messages settings exist in the menu and `sv_gentle` gates gore/voices in `DamageSystem`, but the gentle *text variants* (`%s made a TRIPLE SCORE!` etc.) are dead — gentle players still get the normal violent message text. (presentation: partial / dead toggle)
3. **Spectator-follow recipients are not routed.** `ServerNet.NotificationReaches` implements ONE/ONE_ONLY/TEAM/etc. but omits the QC `IS_SPEC(to) && to.enemy == other` clause — a spectator following a player will NOT receive that player's personal NOTIF_ONE/TEAM notifications. (logic: partial)
4. **Token display cvars resolved server-side + three inverted-default `CvarBool` fallbacks.** Because the port formats text server-side, `NotifTokens` reads the *server's* `notification_show_location` / `notification_show_sprees*` cvars, so all clients get the server's preference (Base resolves these per-client in CSQC). Worse, three `CvarBool` fallbacks have inverted polarity vs Base defaults: `notification_show_location` is read with fallback `true` (Base default `0`), and both `notification_show_sprees_info_specialonly` / `notification_show_sprees_center_specialonly` are read with fallback `false` (Base default `1`). When the cvar is genuinely unset the port forces locations ON and generic (non-achievement) spree lines ON — the opposite of Base. This is masked at runtime because `ConfigLoader.LoadServerConfig` exec's `notifications.cfg` (which sets location=0, specialonly=1), but it is wrong on any headless/test/early-format path. (values/presentation: partial)
5. **`item_wepammo` token returns generic "ammo".** `NotifTokens.WeaponAmmoName` returns the literal `"ammo"` rather than the weapon's actual ammo type name (cells/rockets/…) from `notif_arg_item_wepammo`. Minor wording defect on weapon-drop info lines. (values: partial)
6. **`MULTIICON_INFO` (iconargs) not modelled.** The single `MULTIICON_INFO` entry (`MINIGAME_INVITE`, a dynamic kill-notify icon from args) has no port path; minigames are not a port priority. (presentation: missing, low impact)
7. **`Kill_Notification` / MSG_CENTER_KILL group-kill not networked.** The port centerprint has a local `Kill(id)`, but there is no server→client "kill this cpid group" message equivalent to `Kill_Notification`. Base calls it at **19+ live sites** (timeout/timein, idle/join-prevention, keyhunt, vehicles, instagib find-ammo, nades, campcheck, map remove). The port relies on local expiry / replace-by-id — adequate for replace-style countdowns but NOT for "clear this warning when its condition ends" (campcheck/idle/find-ammo). (logic: missing)
8. **`notification_allow_chatboxprint` not modelled.** Base disables it in code anyway (default 0), so this is benign, but MSG_INFO chatbox routing is absent. (presentation: na/benign)
9. **Race tokens partially unimplemented.** `race_col`, `f{1,2,3}race_time`, and `race_diff` are not in `NotifTokens.ResolveOne` (return `""`), so race split-time lines render with blank slots. The race *notifications themselves* are registered. (values: partial, low impact)

**Registry coverage (CORRECTED from a prior draft):** an earlier draft claimed the registry was incomplete (~680 of ~727) with per-monster-variant centerprints, race split-times and onslaught lines "missing". Direct grep refutes this: the RACE / ONSLAUGHT / MONSTER / KEYHUNT families are all registered in `NotificationsList.cs` (port match counts equal or exceed Base in every sampled family), and the port has ~715 explicit builder calls plus loop/team expansions. Coverage is effectively at parity; the only *structural* gap is the absent `MSG_CENTER_KILL` type (gap 7). A `Send` for an unregistered name is still a no-op + `LastError`, and a missing MSG_MULTI sub is silently dropped, so any not-yet-registered cue is absent rather than erroring.

### Intended divergences
- **Server-side announcer time/countdown** (`AnnouncerController`, `WarmupController`/`RoundHandler` broadcast) instead of Base's CSQC-local `Announcer_Time`/`Announcer_Countdown`. The port has no CSQC announcer timer, so the server computes the schedule and broadcasts `MSG_ANNCE`/`MSG_CENTER` to all clients. Net result is equivalent for a standard match; the hysteresis window, rounding (`floor(0.5+countdown)`) and PickNumber 1..10 mapping are preserved exactly.
- **`cl_announcer_maptime` treated as a server config value** (broadcast is global) rather than per-client, a direct consequence of the server-side relocation.

### Values verified faithful
Announcer queue (`NOTIF_QUEUE_MAX=10`, queuetime 0/-1 semantics), anti-spam window (`cl_announcer_antispam=2`), countdown rounding, PickNumber range, KILL_SPREE_LIST milestones (3/5/10/15/20/25/30 with exact phrases), the spree/frag/ordinal/time token *formatting* (the phrasing matches; the *gating* defaults are the gap above), the `stringcount+floatcount==count` send-time validation, and the broadcast enum mapping all match Base. (Note: the `notification_show_location` and `*_specialonly` *defaults* do NOT match — see gap 4 — but the formatting they gate is faithful when the cvars hold Base values.)

## Verification
- **Liveness:** call-site grep across `src`/`game` (50+ live `Send`/`Info`/`Center`/`Announce`); wire chain traced `ServerNet.NotificationNetSink → FlushNotifications → ClientNet decode → NetGame.OnNotificationReceived → HudNotifications` (code read). RegisterAll in `GameInit.cs:23`.
- **Tests:** `NotificationPolishTests`, `CountdownAnnouncerTests`, `ObituaryEmissionTests`, `ClientFeedbackTests`, `CvarReplicationTests`, `HeadshotTests`, `MonsterTurretVehicleObituaryTests` (test files present under `tests/VortexArena.Tests/`).
- **Gaps 1-3:** confirmed by code read — no `Durcnt` field anywhere; no `GentleMode =` assignment; `NotificationReaches` switch lacks the IS_SPEC clause.
- **Counts:** Base `all.inc` macro counts via grep (730 macro invocations); port 680 builder invocations via grep.
- **Not runtime-verified:** in-game centerprint duration on item pickup; gentle-mode text; spectator-following kill feed.

## Open questions
- Is the fixed 3s centerprint duration noticeable in practice (item pickups, "You captured the flag")? Needs an in-game check, or wiring `durcnt` onto the wire (or precomputing duration server-side into the dispatch).
- Should `GentleMode` be driven from `sv_gentle` (server-authoritative, so the formatted text is decided at send) or `cl_gentle` (client re-formats)? Base re-formats client-side from the local cvar; the port formats server-side, so `sv_gentle` is the natural source — but that loses per-client gentle preference.
- Does any live gametype rely on `Kill_Notification` to clear a centerprint group mid-round (e.g. cancelling a countdown), and is the local replace-by-id path sufficient?

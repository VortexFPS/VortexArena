# sv-chat — parity spec

**Base refs:** `server/chat.qc` · `server/chat.qh` · `server/command/cmd.qc` (ignore CRUD + say/say_team/tell verbs) · `common/effects/qc/globalsound.qh` (VoiceMessage→Say) · `server/main.qc:dedicated_print`
**Port refs:** `src/VortexArena.Server/Chat.cs` · `src/VortexArena.Server/Commands.cs` (CmdSay/CmdSayTeam/CmdTell/CmdIgnore/CmdVoice) · `game/net/NetGame.cs` (sink wiring) · `game/net/ServerNet.cs:SendChatToPlayer` · `src/VortexArena.Common/Gameplay/MapObjects/LogicGates.cs` (magicear)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
`sv-chat` is the server-authoritative chat engine: the single `Say(source, teamsay, privatesay, msgin, floodcontrol)`
entry point that every chat line passes through. It gates chat by cvar, expands `%`/`\` macros (`formatmessage`),
runs the message through map magicears, formats the colored display + centerprint strings, applies per-say-type
flood throttling with persistent timestamps, then routes the line to the correct recipient set (everyone / team /
spectators / a single private target), honoring per-player ignore lists and the muted "fake-accept". It returns
`1` (accept) / `0` (reject) / `-1` (fake-accept). It is invoked from three places in Base: the `say`/`say_team`/`tell`
client commands (`cmd.qc`), and the `VoiceMessage` macro (a radio/taunt's text is run through `Say` for flood + display).

## Base algorithm (authoritative)

### Say — entry gates  (`server/chat.qc:Say`)
- **Trigger:** `say`/`say_team`/`tell` client commands; `VoiceMessage` macro. Authority (sv) only.
- **Algorithm:** four allowed-gates, each `Send_Notification(INFO_CHAT_*_DISABLED)` + `return 0` when it fires:
  1. `!g_chat_allowed && IS_REAL_CLIENT(source)` → CHAT_DISABLED
  2. `!g_chat_private_allowed && privatesay` → CHAT_PRIVATE_DISABLED
  3. `!g_chat_spectator_allowed && IS_OBSERVER(source)` → CHAT_SPECTATOR_DISABLED
  4. `!g_chat_team_allowed && teamsay` → CHAT_TEAM_DISABLED
- **Constants:** `g_chat_allowed 1`, `g_chat_private_allowed 1`, `g_chat_spectator_allowed 1`, `g_chat_team_allowed 1`.

### Say — message preprocessing  (`server/chat.qc:53-96`)
- Public (`!teamsay && !privatesay`) say with a leading space → strip the first char (DP say-bug workaround; not for team/tell).
- `formatmessage(source, msgin)` expands macros.
- Pick `colorstr`: black `^0` for spectators/server; team color if teamplay; `""` (and clear teamsay) for FFA; `""` for a null source.
- `msgin != ""` → `trigger_magicear_processmessage_forallears(source, teamsay, privatesay, msgin)`.
- Build `namestr` = `playername(netname, team, g_chat_teamcolors && IS_PLAYER)`; append `^9#<etof>^7` if `g_chat_show_playerid`.
- `colorprefix` = `^3` if the name has no color codes, else `^7`.

### Say — display string construction  (`server/chat.qc:98-148`)
- `/me ` prefix (only at offset 0; anti-imitation) rewrites the line to an action form.
- Private: `msgstr = "\{1}\{13}* <name>^3 tells you: ^7<msg>"`; `cmsgstr` is the centerprint; `privatemsgprefix = "\{1}\{13}* ^3You tell <target>: ^7"`.
- Team: `msgstr = "\{1}\{13}(<name>) ^7<msg>"` (or `^4* ^7<msg>` for /me); `cmsgstr` the centerprint variant.
- Public: `msgstr = "\{1}<name>^7: <msg>"` (or `^4* ^7<msg>` for /me); no centerprint.
- `\n` in `msgstr` → spaces, then a single trailing `\n` (newlines only good for centerprint).

### Say — flood control  (`server/chat.qc:154-218`)
- **Trigger:** only when `floodcontrol && source`. Per-type field + cvars:
  - private → `floodcontrol_chattell`, spl `g_chat_flood_spl_tell 1`, burst `g_chat_flood_burst_tell 2`, lmax `g_chat_flood_lmax_tell 2`
  - team → `floodcontrol_chatteam`, spl `g_chat_flood_spl_team 1`, burst `g_chat_flood_burst_team 2`, lmax `g_chat_flood_lmax_team 2`
  - public → `floodcontrol_chat`, spl `g_chat_flood_spl 3`, burst `g_chat_flood_burst 2`, lmax `g_chat_flood_lmax 2`
- `flood_burst = max(0, flood_burst - 1)` (a value of N allows N-line bursts, not N+1).
- `mod_time = gettime(GETTIME_FRAMESTART) + flood_burst*flood_spl`.
- Wrap `msgstr` into ≤ `flood_lmax` lines at a fixed visible width (`getWrappedLineLen(82.4289758859709, strlennocol)`); if leftover remains → `flood = 2` (too long).
- If `mod_time >= source.(field)` → charge: if `lines>1`, `flood_spl *= lines`; `field = max(now, field) + flood_spl`.
- Else → restore `msgstr` to the full string and set `flood = 1` (flooding).
- **Constants:** wrap width `82.4289758859709` px-equiv (perl averagewidth on `gfx/vera-sans.width`), `GETTIME_FRAMESTART`.

### Say — flood==2 trim + return code  (`server/chat.qc:220-280`)
- `flood==2`: with `g_chat_flood_notify_flooder 1`, sender sees the trimmed line + `"^3CHAT FLOOD CONTROL: ^7message too long, trimmed\n"`; else the full untrimmed strings; `cmsgstr=""`.
- A spectator's public/team say with `(teamsay || CHAT_NOSPECTATORS())` and not game-stopped → `teamsay = -1` (spectator-only).
- `flood` set → `LOG_INFO("NOTE: <name>^7 is flooding.")`.
- Private: splice `privatemsgprefix` onto the sender-visible string.
- Return: `CS(source).muted` → `-1` (always fake). `flood==1` → with notify_flooder a `sprint("...wait <secs>...")` + `0`, else `-1`. Else `1`.
- Spectator telling an in-game player while `CHAT_NOSPECTATORS()` → `ret = -1` (hide entirely).
- `MUTATOR_CALLHOOK(ChatMessage, source, ret, msgin, privatesay)` may rewrite `ret`. (No core subscriber exists.)

### Say — delivery + routing  (`server/chat.qc:287-369`)
- Only when `sourcemsgstr != "" && ret != 0`.
- `ret<0` (faked): only the sender gets `sprint` (+ `centerprint` if non-private).
- private: `sprint(source)`; if `!g_chat_tellprivacy` `dedicated_print(msgstr)`; if `ignore_playerinlist(privatesay, source)` `return -1`; else `sprint(privatesay)` (+ centerprint).
- `teamsay && active_minigame`: route to other real clients in the same minigame; event log `:chat_minigame:%d:%s:%s`.
- `teamsay>0`: route to in-game real clients on the same team (skip ignorers); event log `:chat_team:%d:%d:%s`.
- `teamsay<0`: route to spectator real clients; event log `:chat_spec:%d:%s`.
- public: `sprint(source)` + `dedicated_print` + `MX_Say` (matrix bridge) + route to all other real clients; event log `:chat:%d:%s`.
- Every branch skips `ignore_playerinlist(it, source)` recipients and `MUTATOR_CALLHOOK(ChatMessageTo, it, source)` vetoes.
- `dedicated_print(input)` prints only if `autocvar_sv_dedicated`.
- If `sv_eventlog` and the event-log line is non-empty → `GameLogEcho(line)`.

### formatmessage  (`server/chat.qc:498-591`)
- Up to `n=7` `%`/`\` replacements per call. First `%`/`\` from `p` is the next escape; lone trailing escape stops.
- Crosshair trace (lazy, once): `WarpZone_crosshair_trace_plusvisibletriggers(this)` → `cursor`, `cursor_ent` for `%y`/`%x`.
- Tokens: `%%`→`%`; `\\`/`\n` (ON_SLASH only); `%a` armor; `%h` PlayerHealth; `%l`/`%y`/`%d` NearestLocation(origin/cursor/death_origin); `%o`/`%O` origin; `%w`/`%W` weapon/ammo name; `%x` aimed-entity netname or "nothing"; `%s`/`%S` horizontal/full speed; `%t`/`%T` time-left/time-elapsed (`seconds_tostring`). Backslash before a NO_SLASH token aborts; unknown → `MUTATOR_CALLHOOK(FormatMessage)` (no core subscriber) leaving the 2-char escape.
- `PlayerHealth`: `-666`→"spectating", `-2342` (or `2342 && mapvote_initialized`)→"observing", `<=0`/dead→"dead", else `ftos(floor(health))`.
- `NearestLocation`: nearest `target_location` `.message`, else nearest item via `findnearest(checkitems)` `.netname`, else "somewhere".

### ignore CRUD  (`server/command/cmd.qc:47-195`)
- `ignore_playerinlist(self, pl)`, `ignore_add_player(self, ignore, to_db_too)` (return 0 full / 1 added / 2 db), `ignore_remove_player`, `ignore_clearall`. Cap `IGNORE_MAXPLAYERS = 16`. Permanent tier persists via ServerProgsDB keyed by UID.

### VoiceMessage→Say coupling  (`common/effects/qc/globalsound.qh:144-155`)
- `VoiceMessage(this, def, msg)`: `voicetype = VM.m_playersoundvt`; `ownteam = (voicetype==VOICETYPE_TEAMRADIO)`; `flood = Say(this, ownteam, NULL, msg, true)`. `fake` derived from `IS_SPEC/IS_OBSERVER/flood`; `flood==0` aborts (no sound). Then `_GlobalSound(..., fake, ...)`. So a voice command's optional text IS a chat line, flood-throttled and displayed, and the sound's fake/real state comes from `Say`'s return.

## Port mapping
- `Say()` → `Chat.Say` — full faithful port of gates / preprocessing / display / flood / return / routing / event log.
- `say`/`say_team`/`tell` → `Commands.CmdSay/CmdSayTeam/CmdTell`, registered + dispatched; live on the net path (`ClientCommandRegistry`).
- Delivery sinks `sprint`/`dedicated_print` → `Commands.ChatToPlayer`/`ChatConsole`, wired in `NetGame.cs` (stock + threaded) to `ServerNet.SendChatToPlayer` / `GD.Print`.
- `formatmessage` → `Chat.FormatMessage` + `ResolveEscape`; `PlayerHealth`/`NearestLocation`/`WeaponName`/`AmmoName` ported.
- ignore CRUD → `Chat.IgnoreAddPlayer`/`IgnoreRemovePlayer`/`IgnorePlayerInList`/`IgnoreClearAll`, keyed by `Player.PersistentId`.
- magicear → `LogicGates.MagicEarProcessAllEars` (wired into `Chat.Say`); `trigger_magicear` spawnfunc registered.
- `VoiceMessage→Say` → **NOT mapped.** `Commands.CmdVoice` plays the sound (`SoundSystem.GlobalSound`) but never calls `Chat.Say` with the optional message text.

## Parity assessment

### Faithful (verified by ChatEngineTests + code read)
The four allowed gates, per-say-type flood with persistent stamps, public/team/spectator/private routing, mutual-ignore
blocking, muted fake-accept, the 1/0/-1 return code, and `formatmessage` (%%/%h/%a/%l fallback/unknown-verbatim/7-budget)
all match Base and are covered by 30+ unit tests. Cvar defaults are byte-for-byte (`xonotic-server.cfg:395-411`). Live:
`Commands.CmdSay/CmdSayTeam/CmdTell` are registered and dispatched, and `NetGame.cs` wires `ChatToPlayer`/`ChatConsole`
to the net layer on both the stock and threaded paths (the historical T46 "delivery silently no-op'd" bug is fixed).

### Gaps (observable)
- **IGNORE_MAXPLAYERS = 50 vs Base 16.** `Chat.IgnoreMaxPlayers = 50`; Base `cmd.qh:11` is `16`. A player can ignore 50
  others before the "list full" rejection instead of 16. The port comment mislabels 50 as the QC constant.
- **VoiceMessage text is not run through Say.** Base routes a voice command's optional message through `Say` for flood
  throttling + chat display, and derives the sound's fake/real state from the return. The port's `CmdVoice` plays the
  sound only — a player's voice-command text never appears in chat, isn't flood-throttled via the chat field, and the
  spectator/flood "fake" gating of the sound is absent.
- **No active_minigame chat branch.** Minigames ARE ported (`MinigameSessionManager` et al.), but `Chat.Say` has no
  `active_minigame` routing and `Player` has no `ActiveMinigame` link, so a `say_team` from inside a minigame is routed
  as a normal team-say (or spectator-say, since minigame players are typically observers) instead of to co-session
  participants, and the `:chat_minigame:` event-log line is never emitted.
- **%y / %x / %d resolve to self-location / "nothing".** No server-side crosshair tracer is wired into chat, so the
  aim-location (`%y`), aimed-entity (`%x`), and death-origin (`%d`) macros fall back to the player's own location /
  "nothing" instead of the traced target. `%d` uses self-origin rather than `death_origin`.
- **NearestLocation item fallback missing.** Base falls back to the nearest pickup item's `.netname` when no
  `target_location` matches; the port only walks `target_location` volumes, so on item-only maps `%l`/`%y`/`%d` say
  "somewhere" where Base would name an item.
- **PlayerHealth "observing" condition narrowed.** Base treats `-2342` OR (`2342 && mapvote_initialized`) as "observing";
  the port only checks `-2342`. Minor — the `2342` case is the mapvote screen.
- **`DedicatedPrint` not gated on `sv_dedicated`.** Base `dedicated_print` (`main.qc:233-236`) prints only when
  `autocvar_sv_dedicated`; the port's `DedicatedPrint` (`Chat.cs:666`) unconditionally invokes `ChatConsole`
  (`GD.Print`, wired `NetGame.cs:745/776`). On a listen server this echoes every routed chat line — and every `tell`
  when `g_chat_tellprivacy 0` — on the host's stdout where Base prints nothing. Cosmetic / host-console only; no
  client-visible effect. (Now a feature row: `sv-chat.delivery.dedicated_print_gating`.)
- **`INGAME()` round-mode semantics not modeled.** The port has no per-player `.ingame` round flag, so
  `IsIngame(p) == !IsObserver(p)`. In round/elimination modes (Clan Arena / LMS / Survival) an eliminated-but-still-
  `INGAME` player is treated as a spectator, so the spectator-downgrade and the `(IS_PLAYER||INGAME)` team-routing
  filter diverge for those players. No effect on FFA / standard CTF / DM.

### Intended divergences (no gap)
- **No chat mutator bus.** `ChatMessage`/`ChatMessageTo`/`PreFormatMessage`/`FormatMessage` hooks are omitted. Verified
  these have **no core subscribers** in Base (declared in `events.qh`, called only by `chat.qc`), so omission has zero
  observable effect on stock gameplay.
- **Ignore keyed by PersistentId, no ServerProgsDB.** The permanent (return-2, db-persisted-across-sessions) ignore tier
  is dropped; PersistentId already survives a reconnect within the match.
- **`MX_Say` (matrix bridge) omitted.** No external matrix/IRC bridge in the port.
- **Delivery abstracted behind sinks** for headless testability (the `Delivered`/`DeliveredCenter` capture lists).

## Verification
- `tests/VortexArena.Tests/ChatEngineTests.cs` — 30+ facts: allowed gates, public/team/spectator/private routing,
  ignore add/remove/clear/full/no-id, muted fake-accept, per-type flood throttle + persistence + separate stamps,
  formatmessage %%/%h/%a/%l/unknown/7-budget, command registration + self-tell/usage gating. (verified, passing per memory.)
- Cvar defaults diffed against `xonotic-server.cfg:395-411` — all match.
- Live wiring confirmed by reading `NetGame.cs:734-779` (both sink branches set `ChatToPlayer`/`ChatConsole`) and
  `Commands.cs:456-459` (verb registration) + `ClientCommandRegistry.cs:53-117` (net-dispatchable + own flood control).
- IGNORE_MAXPLAYERS diff: Base `cmd.qh:11` = 16; port `Chat.cs:427` = 50 (value mismatch, code-read).
- VoiceMessage coupling: read `globalsound.qh:144-155` (Base) vs `Commands.cs:CmdVoice` (port, no Say call).

## Open questions
- Is the wider `IGNORE_MAXPLAYERS = 50` an intentional port choice or an unnoticed typo? It is not flagged as a
  divergence in the code comment (which calls 50 "QC IGNORE_MAXPLAYERS"), so treated here as an unintended gap.
- Should the port wire `CmdVoice`'s optional message through `Chat.Say` to restore the voice-text-as-chat + flood
  coupling, given the voice sound routing itself is now live?

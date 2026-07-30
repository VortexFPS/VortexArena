# sv-ipban — parity spec

**Base refs:** `server/ipban.qc`, `server/ipban.qh`, `server/command/banning.qc`, `server/command/banning.qh`, `lib/urllib.qh`
**Port refs:** `src/VortexArena.Server/Bans.cs`, `src/VortexArena.Server/Commands.cs`, `src/VortexArena.Server/GameWorld.cs`, `src/VortexArena.Server/Cvars.cs`, `src/VortexArena.Server/Chat.cs`, `src/VortexArena.Server/VoteController.cs`, `src/VortexArena.Common/Gameplay/Minigames/MinigameSessionManager.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The server-authority ban subsystem. It owns a local, index-stable ban store of up to `BAN_MAX = 256`
slots (parallel arrays `ban_ip[]`/`ban_expire[]`), persisted in the `g_banned_list` cvar as a version-1
token string of `<ip> <seconds-remaining>` pairs. It enforces bans on connect (drop banned clients),
exposes the admin command surface (`ban`, `kickban`, `unban`, `banlist`), derives IPv4/IPv6 network
masks + crypto-id keys from a client's address, and maintains three separate prefix-match lists for the
soft bans — `g_chatban_list` (mute), `g_playban_list` (forced spectate), `g_voteban_list` (no voting).
A second, optional feature is the **online ban-list sync**: an HTTP cross-server propagation protocol
(`g_ban_sync_*`) that reports/queries bans against a list provider via `uri_get`.

## Base algorithm (authoritative)

### IP / identity mask derivation  (`server/ipban.qc:Ban_GetClientIP`)
- **Trigger:** sv, called by `Ban_IsClientBanned` and `Ban_KickBanClient`. Cannot tokenize (it may run
  mid ban-list parse), so it scans the `.netaddress` string for separators by hand.
- **Algorithm:** if `crypto_idfp_signed`, `ban_idfp = crypto_idfp` else null. For IPv4 (first `.` found):
  finds dots at i1/i2/i3, strips anything past a 4th dot, then `ban_ip1 = s[0..i1]` (/8), `ban_ip2 = s[0..i2]`
  (/16), `ban_ip3 = s[0..i3]` (/24), `ban_ip4 = whole` (/32). **IPv4 masks are bare prefixes, no `/N`
  suffix** (e.g. `"1.2.3"`). For IPv6 (no dot, two `:` minimum): `ban_ip1 = s[0..c1]+"::/32"`,
  `ban_ip2 = s[0..c2]+"::/48"`, `ban_ip4 = s[0..c3]+"::/64"`, and `ban_ip3` is a /56 nibble-truncation
  (`s[0..c2]+":"+s[c2+1..c3-3]+"00::/56"`, or `…:0::/56` when the third group is ≤2 chars). Returns false
  for unparsable / non-remote addresses.

### Ban check  (`server/ipban.qc:Ban_IsClientBanned`)
- **Trigger:** sv, on connect via `Ban_MaybeEnforceBan(Once)` and on every `Ban_Enforce`/`Ban_Insert`.
- **Algorithm:** loads the store on first use. Derives the client masks. For each active slot (skip
  `time > ban_expire[i]`): a stored string equal to any of the 4 IP masks sets `ipbanned`; a stored string
  equal to `ban_idfp` returns true **immediately** (crypto-id ban always wins). After the scan: if `ipbanned`,
  return true unless `g_banned_list_idmode` is set AND the client has a crypto id (idmode: an IP ban only
  catches anonymous clients).

### Enforce on connect  (`server/ipban.qc:Ban_MaybeEnforceBan` / `Ban_MaybeEnforceBanOnce`)
- **Trigger:** sv. `Ban_MaybeEnforceBanOnce` is called from `server/client.qc:1145` (ClientConnect) and
  `server/command/cmd.qc:1181` (first client command), guarded by a one-shot `.ban_checked` flag.
- **Algorithm:** if banned, log a NOTE, `sprint` "You are banned from this server." when `g_ban_telluser`,
  then `dropclient`. Returns true if dropped.

### Insert  (`server/ipban.qc:Ban_Insert`)
- **Trigger:** sv, from `ban`/`kickban` commands and the online-sync callback.
- **Algorithm:** if `ip` already present, **prolong only** (never shorten: `ban_expire = max(old, time+bantime)`),
  re-enforce, and if `dosync` and the reason is non-empty and not `~`-prefixed, `OnlineBanList_SendBan`. Else
  find a free/expired slot; if full, evict the soonest-to-expire victim, **refusing** if that victim's ban
  outlasts the new one ("long-term bans never get overridden by short-term bans"). Set the slot, save, enforce,
  optionally sync. Returns true only when a NEW slot was created.

### Kick-ban  (`server/ipban.qc:Ban_KickBanClient`)
- **Trigger:** sv, `kickban <client> [bantime] [masksize] [reason]`.
- **Algorithm:** resolve the client IP; on failure just `dropclient` with a "Kickbanned:" sprint. Pick the IP
  by masksize (1→/8, 2→/16, 3→/24, else→/32), `Ban_Insert(ip, …, dosync=1)`, then if a crypto id exists also
  `Ban_Insert(id, …, dosync=1)`. The kick happens inside `Ban_Insert`→`Ban_Enforce`.

### Enforce a slot against the live roster  (`server/ipban.qc:Ban_Enforce`)
- **Algorithm:** `FOREACH_CLIENTSLOT(IS_REAL_CLIENT)`: if `Ban_IsClientBanned(it, j)` (j<0 = all slots),
  append to a reason string ("…: affects <name>, …") and `dropclient(it)`. `bprint` a summary.

### Persistence  (`server/ipban.qc:Ban_SaveBans` / `Ban_LoadBans`)
- **Save:** no-op unless loaded. Emit `"1"` then ` <ip> <ban_expire-time>` for each non-expired slot (time
  REMAINING, not absolute). `cvar_set("g_banned_list", …)` (empty string if no real entries). Called from
  `Ban_Delete`, `Ban_Insert`, and at server shutdown (`server/world.qc:2631`).
- **Load:** delete all slots, set `ban_loaded`, tokenize `g_banned_list`; if `argv(0)==1`, read
  `ban_count=(n-1)/2` pairs, `ban_expire = time + secs`. **Also spawns a `bansyncer` entity** whose think is
  `OnlineBanList_Think` (`nextthink = time+1`). Called from `server/world.qc:959` (world spawn) and lazily by
  `Ban_IsClientBanned`/`Ban_Insert`.

### View / Delete  (`Ban_View` / `Ban_Delete`)
- **View:** print `#<i>: <ip> is still banned for <secs> seconds` for each active slot + a "Done listing all
  active (N) bans" footer.
- **Delete:** validate index, if the slot was a real (>0) ban call `OnlineBanList_SendUnban` and free it; save.

### Online ban-list sync  (`server/ipban.qc:OnlineBanList_*`)
- **Protocol:** HTTP GET to each `g_ban_sync_uri` token (up to `MAX_IPBAN_URIS = 16`, from
  `URI_GET_IPBAN..URI_GET_IPBAN_END` = 1..16). `action=ban|unban|list` with url-escaped `hostname`, `ip`,
  `duration`, `reason`, `servers`. `OnlineBanList_Think` (the `bansyncer` entity, reschedules every
  `max(60, g_ban_sync_interval*60)` s) issues the `list` query; `OnlineBanList_URI_Get_Callback` validates the
  reply (not HTML, no CR, item count %4==0), discards entries within `1.5*g_ban_sync_timeout` of expiry,
  validates IP chars (unless a 44-char crypto id), optionally filters to `g_ban_sync_trusted_servers`
  (`g_ban_sync_trusted_servers_verify`), clamps remaining time, and `Ban_Insert`s with `dosync=0`.

### Soft-ban prefix lists  (`server/command/banning.qc`)
- `mute`/`unmute` → `g_chatban_list`, sets `CS(client).muted`; a muted client's chat is fake-accepted
  (`server/chat.qc:255`). On connect, `server/client.qc:1246` re-applies `muted` for clients in the list.
- `playban`/`unplayban` → `g_playban_list`, `PutObserverInServer` (forced spectate); `g_playban_minigames`
  optionally removes them from minigames. On connect, `server/client.qc:1243` TRANSMUTEs them to Observer.
- `voteban`/`unvoteban` → `g_voteban_list`, blocks `vote`/calling votes.
- Membership is `PlayerInIPList`/`PlayerInIDList` (a list word is a **prefix** of the IP or the crypto id).

### Constants / cvars (Base defaults, `xonotic-server.cfg`)
- `g_ban_default_bantime = 5400` (s) · `g_ban_default_masksize = 3` (/24)
- `g_ban_telluser = 1` · `g_banned_list = ""` · `g_banned_list_idmode = 1`
- `g_ban_sync_uri = ""` · `g_ban_sync_interval = 5` (min) · `g_ban_sync_timeout = 45` (s)
- `g_ban_sync_trusted_servers = ""` · `g_ban_sync_trusted_servers_verify = 0`
- `g_chatban_list = ""` · `g_playban_list = ""` · `g_voteban_list = ""` · **`g_playban_minigames = 0`**
- `BAN_MAX = 256` · `MAX_IPBAN_URIS = 16`

## Port mapping
- `Ban_GetClientIP` → `Bans.GetClientIp` / `ClientBanIp` struct. Faithful IPv4 + IPv6 mask derivation,
  including the bare-prefix IPv4 form and the /56 nibble-truncation. Adds a `:port` strip on the /32 form
  (a port suffix the QC tokenizer would also have dropped; behaviorally harmless).
- `Ban_IsClientBanned` → `Bans.IsClientBanned` / `IsClientBannedBySlot`. Faithful incl. idmode + crypto-wins.
- `Ban_MaybeEnforceBan(Once)` → `Bans.MaybeEnforceBan`, called from `GameWorld.InfraClientConnect` (live).
  The one-shot `.ban_checked` flag and the on-first-command re-check are NOT modeled; enforced once on connect.
- `Ban_Insert` / `Ban_Delete` / `Ban_KickBanClient` / `Ban_Enforce` / `Ban_View` →
  `Bans.Insert` / `Delete` / `KickBanClient` / `Enforce` / `View`. Faithful slot logic, prolong-not-shorten,
  evict-soonest, refuse-shorter. `dosync` is dropped (no online sync).
- `Ban_SaveBans` / `Ban_LoadBans` → `Bans.Save` / `Load` via `g_banned_list`. `Load` wired at world init
  (`GameWorld.WireServerInfrastructure`). `Save` runs inside Insert/Delete. **No shutdown Save** and **no
  `bansyncer` entity spawn** (the latter is moot without sync).
- Commands → `Commands.cs` `CmdBan`/`CmdKickBan`/`CmdUnban`/`CmdBanList`/`CmdMute`/`CmdUnmute`/`CmdPlayBan`/
  `unplayban`/`voteban`/`unvoteban`, all registered (Commands.cs:520-529). Live via the sv command bus.
- Soft lists → `Bans.PlayerInList`/`AddToList`/`RemoveFromList`. Mute consumed by `Chat.cs:313`
  (`source.Muted`); voteban by `VoteController.cs:120/227` (call + cast — live, faithful);
  playban-minigames *gate* by `MinigameSessionManager.PlayBanned` (GameWorld.cs:609, start/join/invite only).
  **Playban is only weakly enforced:** `CmdPlayBan` (Commands.cs:1398) adds to the list and sets
  `FragsStatus = FragsSpectator` (the −666 scoreboard sentinel), but does NOT run the real observer
  transition (`PutObserverInServer`) the way Base does, nor does it `part_minigame` the player out of an
  in-progress minigame, nor is there any join-attempt gate (Base `client.qc:2274`) — `CmdJoin`
  (Commands.cs:1072) lets a play-banned player re-join freely. `ClientManager.cs:254` documents the playban
  join gate as "deferred".
- Online sync (`OnlineBanList_*`, `g_ban_sync_*`, `URI_GET_IPBAN`) → **NOT IMPLEMENTED** (intentional per the
  `Bans` class doc-comment: the Godot-free core keeps only the local store + console surface).

## Parity assessment

### Faithful (verified by unit tests + caller trace)
- Mask derivation, ban check (idmode + crypto-wins), insert slot policy, delete, save/load round-trip, and
  the prefix-list match are all covered by `ServerInfraTests.cs` (`Bans_*` cases) and the logic matches QC
  line-for-line. `live`: enforce-on-connect is wired in `InfraClientConnect`; the command surface is registered.
  Voteban (call + cast block) is live and faithful via `VoteController.cs:120/227`.
- NOT verified by unit tests (code-traced only): `kickban`, `playban`, `voteban` — only `mute` has a soft-ban test.

### Gaps (concrete)
0. **Play-ban is not enforced on the live join path (worst gap).** Base `client.qc:2274` (`Join_Try`) refuses
   a join attempt with `CENTER_JOIN_PLAYBAN` when a non-INGAME client is in `g_playban_list`. The port's
   `CmdJoin` (Commands.cs:1072) and `ClientManager` join path consult no such list (ClientManager.cs:254
   marks it "deferred"). Worse, `CmdPlayBan` (Commands.cs:1403) only sets `FragsStatus = FragsSpectator`
   (the −666 scoreboard sentinel), not the real observer transition (`PutObserverInServer`) — per the
   CmdSpectate fix-comment (Commands.cs:1095-1097) that leaves the player solid/shootable/scoring. Net: a
   play-banned player simply runs `join` and re-enters play. `MinigameSessionManager` gates start/join/invite,
   but Base also calls `part_minigame` at ban time to evict a banned player from an in-progress minigame — the
   port does no active removal.
1. **Connect-time re-application of chatban/playban is missing.** Base `client.qc:1243/1246` re-mutes /
   re-spectates any connecting client found in `g_chatban_list` / `g_playban_list`. The port only sets
   `Player.Muted` / flips the scoreboard sentinel at the instant the admin runs `mute`/`playban` (Commands.cs). A
   chat-banned or play-banned player who reconnects (or persists across a map change) is NOT re-muted /
   re-forced-to-spectate. `InfraClientConnect`/`InfraClientSpawn` never read these lists. Observable: a muted
   player reconnects and can talk; a play-banned player reconnects and can play.
2. **`g_playban_minigames` default is wrong: port `1` (Cvars.cs:341), Base `0` (xonotic-server.cfg:436).**
   Out of the box the port blocks play-banned players from minigames; Base allows them. (Note: the
   Commands.cs:1167 comment claims the port "ships 0" — that comment is factually wrong about its own default.)
   Observable: a play-banned player can join a minigame on a stock Base server but not on the port.
3. **No shutdown `Ban_SaveBans`.** Base saves at `world.qc:2631`. The port saves on every Insert/Delete, so
   the persisted list is current, but the final per-shutdown refresh of remaining-time is skipped — a long
   session's persisted "seconds remaining" can be stale by the session length on next load. Minor.
4. **Online ban-list sync entirely absent** (`OnlineBanList_*`, `g_ban_sync_*` cvars, `bansyncer` entity,
   `URI_GET_IPBAN`). Intended divergence (Godot-free core), but listed for completeness: cross-server ban
   propagation and the `~`-prefixed unauthenticated-banner sync guard do not exist.
5. **One-shot enforce semantics differ.** Base uses `Ban_MaybeEnforceBanOnce` (the `.ban_checked` flag) and
   also re-checks on the client's first command; the port enforces once on connect with no first-command
   re-check. Low impact (connect-time drop is the dominant path).

### Intended divergences
- Online sync omitted — see the `Bans` class summary doc-comment. Rationale recorded in the registry row.

## Verification
- `Bans_GetClientIp_IPv4_DerivesMasks`, `Bans_GetClientIp_RejectsLocalAndBot`, `Bans_InsertAndCheck_ByIp`,
  `Bans_IdMode_IpBanOnlyCatchesAnonymous`, `Bans_CryptoIdBan_AlwaysWins`, `Bans_Delete_RemovesBan`,
  `Bans_SaveLoad_RoundTripsThroughCvar`, `Bans_PrefixList_MuteMatchesByIpAndId`
  (`tests/VortexArena.Tests/ServerInfraTests.cs`).
- Liveness traced: `GameWorld.cs:660` (`Bans.Load`), `:724` (`MaybeEnforceBan` on connect), Commands.cs:520-529
  (command registration), Chat.cs:313 (mute consumed), VoteController.cs:120/227 (voteban consumed),
  GameWorld.cs:609 (playban-minigames).
- Value diffs read from `xonotic-server.cfg:424-437` (Base) vs `Cvars.cs:333-341` (port).

## Open questions
- Does any host path call `Bans.Save()` at process exit / map change? (Searched GameWorld.Shutdown — it only
  detaches the cvar hook; no Save.) Needs an owner decision on whether the per-Insert save is deemed sufficient.

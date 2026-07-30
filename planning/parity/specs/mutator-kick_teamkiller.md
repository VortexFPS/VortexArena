# Kick Teamkiller mutator — parity spec

**Base refs:** `common/mutators/mutator/kick_teamkiller/sv_kick_teamkiller.qc` (+ `.qh`, `_mod.inc`)  ·  **Port refs:** `src/VortexArena.Common/Gameplay/Mutators/KickTeamkillerMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
An **anti-griefing, server-authority-only** mutator. In any **teamplay** mode, every time a real
(non-bot) client kills a teammate, the mutator checks the attacker's accumulated teamkill count
against a **rate threshold** (teamkills per minute of that player's playtime). If a player teamkills
fast enough — and has at least a floor number of teamkills — the server punishes them. The
punishment escalates by `g_kick_teamkiller_severity`: **kick** (1), **IP-ban** (2), or the default
**play-ban** (force-spectate + add to a persistent playban list). The mutator is **registered only
when `g_kick_teamkiller_rate > 0`**, which is **0 by default**, so it is **off in stock play** — it
exists for admins who opt in. There is no client / HUD / presentation surface beyond the broadcast
notification it sends, and one center-print to the offender on the play-ban path.

## Base algorithm (authoritative)

### Registration / enable gate  (`sv_kick_teamkiller.qc:REGISTER_MUTATOR`)
- `REGISTER_MUTATOR(kick_teamkiller, (autocvar_g_kick_teamkiller_rate > 0));`
- `STATIC_INIT_LATE(Mutators)` adds the mutator (subscribes its hooks) only if the predicate is true.
  With the default `g_kick_teamkiller_rate 0` the mutator is **inert**.

### PlayerDies detection + punishment  (`sv_kick_teamkiller.qc:MUTATOR_HOOKFUNCTION(kick_teamkiller, PlayerDies)`)
- **Trigger / entry:** the `PlayerDies` mutator hook, fired server-side from the kill path
  (`server/damage.qc` → `PlayerDamage` → `MUTATOR_CALLHOOK(PlayerDies, inflictor, attacker, target,
  deathtype, damage)`). `M_ARGV(1, entity)` = **attacker**.
- **Algorithm (step by step):**
  1. `if (!teamplay) return;` — only team modes.
  2. `if (warmup_stage) return;` — **never punish during warmup**.
  3. `attacker = M_ARGV(1, entity); if (!IS_REAL_CLIENT(attacker)) return;` — ignore bots / world /
     non-client attackers.
  4. Read `masksize = autocvar_g_ban_default_masksize` and `bantime = autocvar_g_kick_teamkiller_bantime`.
  5. `teamkills = PlayerScore_Get(attacker, SP_TEAMKILLS)` — the attacker's running teamkill tally
     (incremented in `GiveFrags`, `server/damage.qc:56`, whenever `f < 0` and `targ != attacker`).
  6. `playtime = time - CS(attacker).startplaytime` — **seconds since this client last switched from
     spectator to player** (set at `server/client.qc:780`), **not** since level start.
  7. Threshold (both must hold):
     `teamkills >= autocvar_g_kick_teamkiller_lower_limit`
     **AND** `teamkills >= autocvar_g_kick_teamkiller_rate * playtime / 60.0`
     (rate is teamkills/minute; playtime is seconds, hence `/60`).
  8. If tripped, `switch (autocvar_g_kick_teamkiller_severity)`:
     - **case 1 (kick):** `if (dropclient_schedule(attacker)) Send_Notification(NOTIF_ALL, NULL,
       MSG_INFO, INFO_QUIT_KICK_TEAMKILL, attacker.netname);` — schedule a client drop; broadcast
       the kick info notification only if the drop was scheduled.
     - **case 2 (ban):** `attacker.respawn_flags = RESPAWN_SILENT;`
       `Ban_KickBanClient(attacker, bantime, masksize, "Team Killing");`
       `Send_Notification(NOTIF_ALL, NULL, MSG_INFO, INFO_QUIT_KICK_TEAMKILL, attacker.netname);`
     - **default (play-ban / observe):** `attacker.respawn_flags = RESPAWN_SILENT;` then build a
       playban id string (`netaddress` if not already in `g_playban_list`, `crypto_idfp` if not),
       `LOG_INFO("Play-banning player …")`, `PutObserverInServer(attacker, true, true)`,
       `cvar_set("g_playban_list", cons(autocvar_g_playban_list, theid))`,
       `Send_Notification(NOTIF_ALL, NULL, MSG_INFO, INFO_QUIT_PLAYBAN_TEAMKILL, attacker.netname)`,
       `Send_Notification(NOTIF_ONE, attacker, MSG_CENTER, CENTER_QUIT_PLAYBAN_TEAMKILL)`, and if the
       player is now in the playban list, `TRANSMUTE(Observer, attacker)`.

### Constants / cvars (Base defaults)
| cvar | default | units / meaning | side |
|---|---|---|---|
| `g_kick_teamkiller_rate` | **0** | teamkills/minute before drop; **0 = mutator disabled** | authority (`sv`) |
| `g_kick_teamkiller_lower_limit` | **5** | minimum teamkills before the rate is even considered | authority |
| `g_kick_teamkiller_severity` | **0** (unset → 0) | 1 = kick, 2 = ban, else = play-ban/observe | authority |
| `g_kick_teamkiller_bantime` | **0** (unset → 0) | seconds for `Ban_KickBanClient` on severity 2 | authority |
| `g_ban_default_masksize` | **3** | ban mask: 0=UID, 1=/8, 2=/16, **3=/24**, 4=single IP | authority |
| `g_playban_list` | `""` | persistent forced-spectate list (IP or playerkey) | authority |

`severity` and `bantime` have **no `set` in any cfg** in Base — they resolve to the autocvar zero
default, so out of the box the tripped action is the **play-ban** case with `bantime 0`.

### Notifications
- `INFO_QUIT_KICK_TEAMKILL` (MSG_INFO, broadcast): "^BG%s^F3 was kicked for excessive teamkilling" — severity 1 & 2.
- `INFO_QUIT_PLAYBAN_TEAMKILL` (MSG_INFO, broadcast): "^BG%s^F3 was forced to spectate for excessive teamkilling" — default case.
- `CENTER_QUIT_PLAYBAN_TEAMKILL` (MSG_CENTER, to the offender): "You are forced to spectate …" — default case only.

### Edge cases
- Suicides do not count (teamkill score only increments when `targ != attacker`).
- `RESPAWN_SILENT` suppresses the normal respawn announce for the punished player (severity 2 + default).
- Play-ban dedups the id against the existing list before appending; transmute to Observer only if the append took.

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| `REGISTER_MUTATOR(... rate>0)` | `KickTeamkillerMutator.IsEnabled` (`g_kick_teamkiller_rate > 0`) | faithful |
| `STATIC_INIT_LATE → Mutator_Add` | `MutatorActivation.Apply()` @ `GameWorld.cs:511` (live boot) | faithful, live |
| `PlayerDies` hook | `MutatorHooks.PlayerDies` ← `DamageSystem.PlayerDies` (`Scores.cs` obituary fires the kill path) | live |
| `!teamplay` gate | `if (!GameScores.Teamplay) return false;` | faithful |
| `warmup_stage` gate | **NOT IMPLEMENTED** — comment says "treat the match as live" | **missing** |
| `IS_REAL_CLIENT(attacker)` (= `clienttype(v)==CLIENTTYPE_REAL`, `server/utils.qh:17`) | `attacker.Flags & EntFlags.Client` + `attacker is Player {IsBot:true}` reject | faithful (approximation: connected non-bot client) |
| `PlayerScore_Get(SP_TEAMKILLS)` | `GameScores.Get(attacker, GameScores.TeamKills)` (same unified store written at `Scores.cs:501`) | faithful, live |
| `time - startplaytime` | `Api.Clock.Time` (sim clock since level start) — **no per-client startplaytime** | **partial** |
| threshold `tk>=limit && tk>=rate*pt/60` | identical expression in C# | faithful (values) |
| severity 1 kick (`dropclient_schedule`) | **records `LastAction`** only; `Bans.DropClient` exists in Server layer but is NOT called | **stub** |
| severity 2 ban (`Ban_KickBanClient`) | **records `LastAction`** only; `Bans.KickBanClient` exists but is NOT called | **stub** |
| default play-ban (`PutObserverInServer` + list + TRANSMUTE) | **records `LastAction`** only; playban list infra exists in Server layer but is NOT called | **stub** |
| `RESPAWN_SILENT` | NOT IMPLEMENTED | missing |
| info / center notifications | always sends `QUIT_KICK_TEAMKILL`, regardless of severity | **partial** |

## Parity assessment

**Detection (logic + values):** The gating chain (teamplay → real-client) and the threshold
expression are a faithful, character-for-character port, reading the correct teamkill column from the
same `GameScores` store that the live obituary path increments. Rate (0) and lower_limit (5) defaults
ship via the bundled `mutators.cfg` (loaded by `ConfigLoader`). Because rate defaults to 0, the
mutator is correctly **disabled by default** in both.

**Gaps (what a player/admin would observe):**
- **No warmup exclusion:** Base bails during `warmup_stage`; the port does not. With the mutator
  enabled, teamkills accrued during warmup count toward the threshold and could trip a punishment the
  instant the match path fires — a behavioral divergence from Base. *(observable: punished for warmup
  teamkills)*
- **Wrong playtime denominator:** Base uses `time - startplaytime` (per-client time since the player
  last became a player); the port uses the absolute sim clock since level start. For a player who
  joins mid-match, the port's denominator is larger, so the same teamkill count yields a *lower*
  computed rate → the player is punished *later* (or not at all) compared to Base. For a player
  present since level start the two coincide. *(observable: late-joining teamkillers escape the rate
  gate longer than in Base)*
- **No punishment is ever enforced:** all three severity branches only set `LastAction` and send one
  notification. No client is kicked, IP-banned, force-spectated, or added to `g_playban_list`. The
  griefer keeps playing. The mutator's own doc-comment claims the substrate "doesn't exist here," but
  that is **stale**: `VortexArena.Server/Bans.cs` implements `DropClient`, `KickBanClient`, and the
  playban-list add/remove, and `Commands.cs` wires `playban`/`unplayban`. The actions are *unwired*,
  not *un-buildable*. *(observable: a teamkiller is announced but never actually removed)*
- **Notification mismatch:** the port always sends `QUIT_KICK_TEAMKILL`. Base sends
  `QUIT_PLAYBAN_TEAMKILL` (broadcast) + `CENTER_QUIT_PLAYBAN_TEAMKILL` (to offender) on the **default
  (play-ban)** path — which is the path the stock `severity 0` default hits — and only uses
  `QUIT_KICK_TEAMKILL` for severity 1 & 2. So with default cvars the port shows the **wrong** kill-feed
  message ("kicked" instead of "forced to spectate") and never shows the offender center-print.
  Note both correct notifications are **already defined in the port** (`NotificationsList.cs:740`
  `QUIT_PLAYBAN_TEAMKILL` Info, `:977` `QUIT_PLAYBAN_TEAMKILL` Center) — they are simply never sent, so
  this is a wiring/branching gap, not a missing asset. *(observable: wrong kill-feed text; no
  center-print to the punished player)*
- **`RESPAWN_SILENT` not set** on the punished attacker (cosmetic; only relevant once an action is wired).

**Liveness:** The **detection** is live — `MutatorActivation.Apply()` subscribes the hook at server
boot when enabled, and `DamageSystem.PlayerDies` fires `MutatorHooks.PlayerDies` on every kill, with
the teamkill score incremented on the same obituary path (`Scores.cs:501`). The **punishment
actions** are dead (no enforcement caller). Note: because `g_kick_teamkiller_rate` defaults to 0, the
mutator is not subscribed at all in a default match; it only becomes live when an admin sets rate > 0.

**Intended divergences:** None declared. The substrate-blocker rationale in the source comment is
treated as a **gap, not an intended divergence**, because (a) the punishment substrate does in fact
exist in the Server layer, and (b) the missing warmup gate and wrong playtime/notification are
behavioral defects, not deliberate design choices.

## Verification
- **Code read** (high confidence): enable gate, teamplay/real-client gates, threshold expression,
  teamkill-score read all line up with Base; the same `GameScores.TeamKills` column is written on the
  live obituary path (`Scores.cs:Obituary` → `AddAux(TeamKills,1)`).
- **Liveness traced** (high): `GameWorld.cs:492` (`SubscribeToDeaths`) + `GameWorld.cs:511`
  (`MutatorActivation.Apply`) are the live server-boot callers; `DamageSystem.PlayerDies` →
  `MutatorHooks.PlayerDies.Call`.
- **Substrate check** (high): `Server/Bans.cs` exposes `DropClient`, `KickBanClient`, playban list
  add/remove; `Commands.cs` registers `playban`/`unplayban`; `Cvars.cs:337` ships
  `g_ban_default_masksize` default `3`.
- **Tests** (low coverage): the only test (`MutatorBatchT51Tests.cs:103`) asserts the mutator is
  registered (`Mutators.ByName("kick_teamkiller") != null`). No behavioral test exercises the
  threshold, severity switch, playtime, or notifications — so timing is **unverified by test**.

## Open questions
- Does the port set/seed `g_kick_teamkiller_severity` / `g_kick_teamkiller_bantime` anywhere, or do
  they resolve to 0 like Base? (No `set` found; assumed 0, matching Base. The default-case play-ban is
  therefore the stock tripped action — exactly the path whose notification the port gets wrong.)
- Is there a per-client "became a player" timestamp anywhere in the port (spawn/observer→player
  transition) that could back a faithful `startplaytime`? Not found on the mutator's read path.

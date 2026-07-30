# PlayerStats / XonStat — parity spec

**Base refs:** `common/playerstats.qc` + `.qh` · `lib/urllib.qc` + `.qh` · `lib/json.qc` + `.qh` (feeds: `server/damage.qc`, `server/scores.qc`, `server/anticheat.qc`, `server/client.qc`, `server/player.qc`, `common/state.qc`, `server/world.qc`)
**Port refs:** `src/VortexArena.Server/PlayerStats.cs` · `src/VortexArena.Server/GameWorld.cs` · `game/menu/dialogs/DialogMultiplayerProfile.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-07-02

> Scope note: the prompt-era path `server/playerstats.qc` does not exist at this rev — playerstats
> lives at `common/playerstats.qc` (one file with `SVQC` and `MENUQC` sections).
> Boundary: `scoring.yaml:scoring.report.playerstats` owns the pipeline-level verdict (Init/Finalize/
> GameReport ordering from GameWorld); `sv-world-rules.yaml:sv-world-rules.net.pingplreport` owns the
> 10s latency sampler this unit's avglatency record consumes. This unit owns feeds, consent, the V9
> payload field set, the URI layer, and the client-side fetches.

## Overview

The playerstats subsystem is Xonotic's XonStat integration: (1) the **game report** — a per-match
event DB accumulated server-side and POSTed as a "format version 9" text document to
`g_playerstats_gamereport_uri` at match end; (2) the **playerbasic skill fetch** — a per-player JSON
GET the server issues on join to seed the SP_SKILL scoreboard column and skill-based team balancing;
(3) the **playerdetail profile fetch** — the menu's rankings screen data. All three ride
`lib/urllib.qc`, an async HTTP client built on `crypto_uri_postbuf`; the skill fetch parses its
response with `lib/json.qc`.

## Base algorithm (authoritative)

### Game-report event DB  (`common/playerstats.qc:PlayerStats_GameReport_Init (288)`, `_AddPlayer (79)`, `_AddTeam (117)`, `_AddEvent (136)`, `_Event (155)`, `_Reset_All (36)`)
- **Trigger:** `PlayerStats_GameReport_Init` from `server/world.qc:925` before `InitGameplayMode`; `_Reset_All` from `server/command/vote.qc:361` (readyrestart).
- **Algorithm:** a string DB (`PS_GR_OUT_DB`) exists only when `g_playerstats_gamereport_uri != ""`.
  Three linked lists thread through it (teams `PS_GR_OUT_TL`, players `PS_GR_OUT_PL`, events
  `PS_GR_OUT_EVL`); `_Event(prefix, id, value)` does read-add-write on key `<prefix>:<id>` and
  returns the new total; the report serializer walks only *registered* event ids, so unregistered
  events never appear.
- **Identity (consent!):** `_AddPlayer` keys a player by `crypto_idfp` **only if the replicated
  `cvar_cl_allow_uidtracking == 1`**; bots use `bot#<skill>#<cleanname>`; empty/colliding ids fall
  back to `player#<playerid>` / `bot#<playerid>` (unique per match, untrackable across matches).
  `REPLICATE_APPLYCHANGE("cl_allow_uidtracking", ...)` re-registers on a mid-match consent change.
- **Pre-registered events (Init):** `alivetime avglatency wins matches joins scoreboardvalid
  scoreboardpos rank handicapgiven handicaptaken`, per-weapon `acc-<netname>-{real,hit,fired,cnt-hit,
  cnt-fired,frags}`, `achievement-kill-spree-{3,5,10,15,20,25,30}`, `achievement-botlike`,
  `achievement-firstblood`, `achievement-firstvictim`, plus anticheat registration
  (`anticheat_register_to_playerstats`). Per-connect: `kills-<playerid>`. Per score label:
  `total-<label>` + `scoreboard-<label>` (registered in `_Reset_All`; on the normal path scores
  register them when added).
- **serverflags:** Init sets `SERVERFLAG_PLAYERSTATS` (BIT 2; requires `DP_CRYPTO` + `DP_QC_URI_POST`
  when using the official URI) and `SERVERFLAG_PLAYERSTATS_CUSTOM` (BIT 3, non-default URI).

### Per-match feeds (live emitters)
- `server/damage.qc:66` — `kills-<victim playerid> += 1` on every murder.
- `server/damage.qc:343-359` — `achievement-kill-spree-N` at killcount milestones 3/5/10/15/20/25/30;
  `achievement-firstblood` / `achievement-firstvictim` once per match.
- `server/damage.qc:475` — `achievement-botlike` (accident/trap deaths at score -5).
- `server/scores.qc:375` — `total-<scorelabel> += delta` on **every** `PlayerScore_Add`.
- `server/scores.qc:952-967` — `PlayerScore_PlayerStats` writes end-of-match `scoreboard-<label>`
  values per player, and `PlayerScore_TeamStats` writes team `Q team#N` events.
- `common/playerstats.qc:PlayerStats_GameReport_Accuracy (167)` — six `acc-*` columns per weapon at finalize.
- `server/anticheat.qc:221-224` — `anticheat-*` raw detector values at finalize.
- Alivetime: started in `PutClientInServer`; **flushed at every death** (`server/player.qc:456`) and
  **observer transition** (`server/client.qc:329`), final flush in `_FinalizePlayer`. AFK gate: in
  `PlayerFrame`, if idle ≥ **30 s**, `alivetime_start += frametime` (idle time excluded).
- Latency: `_FinalizePlayer (208-224)` averages `CS(p).latency_sum / latency_cnt` (sampled every
  `LATENCY_THINKRATE = 10 s` by the PingPLReport entity, `server/world.qc`); on reconnect the new
  average is `(prev + latency) / 2`.

### FinalizePlayer  (`common/playerstats.qc:183-227`)
Per player (on disconnect and at match end): flush alivetime → `_playerid` → `_netname` **only if
`cvar_cl_allow_uid2name == 1` or bot** → `_team` if teamplay → `joins = 1` if alivetime > 0 →
accuracy → anticheat → avglatency → `_ranked` = `cvar_cl_allow_uidranking` (real clients) → free the
stats id (no double finalize).

### GameReport + V9 payload  (`common/playerstats.qc:PlayerStats_GameReport (229)`, `_Handler (359-530)`, `PlayerStats_GetGametype (346)`)
- **Trigger:** `NextLevel` (`server/world.qc:1432`, `finished=true`) and `Shutdown`
  (`server/world.qc:2634`, `finished=false` for aborted matches).
- Sorts scores, then per client: `rank` (dense sort position), and if scoreboard-valid:
  `scoreboardvalid=1`, `scoreboardpos`, score columns, and when finished `wins += it.winning`,
  `matches += 1`; `handicapgiven/taken` = damage-weighted averages (`given<=0 ? 1 :
  handicap_avg_given_sum/given`); then FinalizePlayer.
- **Discard:** if URI empty **or `warmup_stage`** → close DB, no upload, `DelayMapVote = false`.
- Otherwise `url_multi_fopen(uri, FILE_APPEND, PlayerStats_GameReport_Handler, NULL)` and
  `PlayerStats_GameReport_DelayMapVote = true` — the **map vote is delayed until the async upload
  callback** (CLOSED or ERROR) clears it.
- **V9 lines** (CANWRITE): `V 9` · `R <watermark>` · `G <gametype>` (with the **duel hack**: DM +
  `g_maxplayers==2` + ≤2 players → `"duel"`) · `O <modname>` · `M <mapname>` · `I <matchid>` ·
  `S <hostname>` · `C <cvar_purechanges_count>` · `U <port>` · `D max(0, time - game_starttime)` ·
  `RP <rounds_played>` (if > 0) · `L <ladder>` (if set); then per team `Q team#N` + `e` lines;
  then per player `P <statsid>` · `i <playerid>` · `n <netname>` (if consented) · `t <team>` ·
  `r <ranked>` · `e <event> <value>` (non-zero only, `%g`); trailing blank line.

### Playerbasic skill fetch  (`common/playerstats.qc:PlayerStats_PlayerBasic (532)`, `_CheckUpdate (601)`, `_Handler (622)`)
- **Trigger:** `common/state.qc:55` — `ClientState_attach` calls `_CheckUpdate` on every join.
- **Gate:** `g_playerstats_playerbasic_uri != ""` (default `https://stats.xonotic.org`) **and**
  (`g_balance_teams_skill || sv_showskill`); player must have a `crypto_idfp` (else SKILL = -1).
- GET `<uri>/skill?game_type_cd=<gametype>&hashkey=<uri_escape(idfp)>`; response is a JSON array;
  if `0.game_type_cd` matches: `m_skill_var = sigma²`, `m_skill_mu = max(0, mu)`,
  `SP_SKILL = mu + 1` (mean skill displayed, not mu−3σ). Status codes: ERROR −2, IDLE −1,
  WAITING 0, RECEIVED 1, UPDATING 2.

### Playerdetail profile fetch (MENUQC)  (`common/playerstats.qc:PlayerStats_PlayerDetail (715)`, `_CheckUpdate (748)`, `_Handler (774)`, `_AddItem (693)`)
- **Trigger:** `menu/xonotic/statslist.qc:297` (stats list `showNotify` → `_CheckUpdate`); refetch
  when `time >= PS_D_NEXTUPDATETIME` (`g_playerstats_playerdetail_autoupdatetime = 1800 s`) or
  `cl_matchcount` increased.
- **Gate:** `g_playerstats_playerdetail_uri != ""` (default `https://stats.xonotic.org/player/me`)
  and `crypto_getmyidstatus(0) > 0`.
- POSTs `V 1` + watermark + `l <language>` + `n <_cl_name>` + `m <model> <skin>`; parses the reply's
  `V/R/T/S/P/n/i` info keys and `G`-namespaced `e <event> <data>` lines into `PS_D_IN_DB` items
  (`<gametype>/<event>`), then `statslist.getStats()` re-renders.

### urllib async HTTP client  (`lib/urllib.qc:url_single_fopen (90)`, `url_fclose (209)`, `url_fgets (291)`, `url_fputs (312)`, `url_URI_Get_Callback (28)`, `url_multi_fopen (360)`)
- URL with `://` + WRITE/APPEND → buffered writer; `url_fclose` commits the buffer as an HTTP POST
  (`crypto_uri_postbuf`, content-type `text/plain`); the response later arrives via
  `URI_Get_Callback` → callback with CANREAD, lines readable via `url_fgets`, second `url_fclose` →
  CLOSED. READ → immediate GET. 64 request slots (`URI_GET_URLLIB` 128..191) with round-robin
  `_urllib_nextslot` persisted as a cvar across map changes. Status codes: ERROR −1 (or negative
  HTTP status), CLOSED 0, CANWRITE 1, CANREAD 2. Plain paths fall back to `fopen`; `"-"` = stdout.
- `url_multi_fopen`: space-separated URI list tried in order on error; HTTP **422** aborts the chain
  (data unusable, don't retry).

### QC JSON parser  (`lib/json.qc:json_parse (266)`, `json_get (323)`)
Recursive descent over a string iterator into a flat interleaved key/value bufstr; nested keys join
with `.`; arrays add `<ns>.length`; `true/false/null` → `1/0/""`; escapes `\" \\ \n \t \/` (`\u`
unimplemented TODO); ints reject leading zeros. Consumers: playerbasic handler, `lib/matrix`.

## Port mapping

| Base | Port | State |
|---|---|---|
| GameReport accumulator (`_Init/_AddPlayer/_AddTeam/_AddEvent/_Event/_Reset_All`) | `src/VortexArena.Server/PlayerStats.cs` (Init 93, AddPlayer 157, AddTeam 177, AddEvent 146, Event 191, ResetAll 132); wired `GameWorld.cs:1256/1443/4662` | **Faithful + live** |
| Consent gating (uidtracking/uid2name/uidranking, `r` line) | none — `AddPlayer` uses `PersistentId` unconditionally; `_netname` unconditional; `_ranked` never written. Menu checkboxes exist (`DialogMultiplayerProfile.cs:121-133`); server cvar-read plumbing exists (`GameWorld.cs:3190`, race records) | **Missing gate** |
| Feed: kills-\<id\> / achievements / total- / scoreboard- / team Q / acc-* | not fed. `EventTeam` (PlayerStats.cs:204) zero callers; `AccuracyProvider`/`ScoreColumnsProvider`/`PingProvider` never wired (`GameWorld.cs:1250-1255` wires 6 of 9). `WeaponAccuracy` (Scores.cs:176) tracks the data but isn't bridged | **Dead slots** |
| Feed: rank / scoreboardpos / wins / matches / joins / handicap / anticheat | wired (`GameWorld.cs:1250-1255`, `AntiCheat.cs:337/348`) | **Live** |
| Alivetime + 30s AFK gate | `BeginAlivetime/AdvanceAliveStart/FlushAlivetime` (PlayerStats.cs:211/221/229), wired `GameWorld.cs:1611/1981/1645` — **no flush on death/observe**, every respawn overwrites the stamp | **Partial** (last-life only) |
| Avglatency | averaging formula ported (PlayerStats.cs:278-284) but `PingProvider` defaults 0 and is never wired; no 10s sampler exists (see sv-world-rules.pingplreport) | **Dead** |
| V9 serializer | `BuildReport` (PlayerStats.cs:343): `V/G/O/M/S/D/Q/P/i/n/t/e` only — **missing `R I C U RP L r`**; `G` reads never-set cvar `g_playerstats_gametype` (always blank, no duel hack); `D` = absolute `Time`, not `time - game_starttime` | **Partial** |
| HTTP submission (`url_multi_fopen` POST, async DelayMapVote) | NOT IMPLEMENTED — `GameReport` returns the body; both callers (`GameWorld.cs:4067`, `:1823`) discard it; `DelayMapVote` cleared synchronously; `g_playerstats_gamereport_uri` defaults `""` (Cvars.cs:431) vs Base submit URL; `ServerFlags.PlayerStats*` bits defined (ServerFlags.cs:29-33) but never set | **Missing** |
| Playerbasic skill fetch | NOT IMPLEMENTED — SP_SKILL column registered (`GameScores.cs:166`) and preserved by score clears (`:597`) but never fed; no `g_playerstats_playerbasic_uri` cvar | **Missing** (stub column) |
| Playerdetail profile fetch | NOT IMPLEMENTED — `DialogMultiplayerProfile.cs:134-151` honest placeholder + inert `menu_cmd playerstats_update` (`MenuCommand.cs:281` logs it) | **Missing** (dialog chrome live) |
| lib/urllib.qc | NOT IMPLEMENTED (no async HTTP facility anywhere in src/ or game/) | **Missing** |
| lib/json.qc | intended divergence — .NET `System.Text.Json` replaces it (already used, `CameraTrace.cs:65`) | **N/A by design** |

## Parity assessment

- **Logic** — the accumulator core and the finalize/report flow are faithful and unit-tested; the
  consent gates, per-life alivetime flush, most event feeds, the gametype/duel resolution and seven
  V9 header lines are absent. Everything downstream of "build the report" (upload, skill fetch,
  profile fetch, urllib) is missing.
- **Values** — event-id strings, bot/fallback identity formats, the reconnect-latency formula, the
  30 s AFK threshold and the e-line non-zero filter all match. Mismatches: `g_playerstats_gamereport_uri`
  default `""` vs Base's submit URL, `D` absolute-time, `G` blank, and every value in the
  never-fed event families (effectively 0 vs Base's real tallies).
- **Timing** — the async-upload/DelayMapVote dance is replaced by a synchronous clear (map vote
  never blocked — reasonable interim, but also means no delay semantics to be faithful to).
- **Presentation** — Profile dialog chrome + consent checkboxes live; stats list honestly empty.
- **Liveness** — accumulator/report path live (Init at boot, AddPlayer on connect, FinalizePlayer on
  disconnect, GameReport at NextLevel + Shutdown, ResetAll on readyrestart). The report OUTPUT is a
  dead end (string discarded). kills/achievements/accuracy/score-column/team feeds: dead slots.
  Skill fetch, profile fetch, urllib: no code.
- **Intended divergences** — `lib/json.qc` (System.Text.Json), the synchronous DelayMapVote clear
  (documented in scoring.yaml's pipeline row), and arguably the port's `""` URI default (honest
  while no uploader exists).

## Verification

- `tests/VortexArena.Tests/ServerInfraTests.cs:444-490` — disabled-without-uri, accumulate+build,
  warmup-discard (pass; covers the accumulator row).
- Code reads with line refs as cited per row: grep sweeps for `EventPlayer/EventTeam` emitters,
  `allow_uid`, `g_playerstats_gametype` (single read, zero writes), `urllib|HttpClient|crypto_uri`
  (comments only), `playerstats_update` (single inert call site).
- Boot-log observation: `[MenuCommand] 'playerstats_update' has no client backend yet (inert)`.
- Unverified at runtime: none of the missing pieces need a runtime check; the alivetime last-life-only
  claim is a code-path deduction (BeginAlivetime overwrite without intermediate flush) — a live match
  with 2+ deaths would confirm the undercount.

## Open questions

- Should the port ever submit to the official XonStat? Doing so requires the DP_CRYPTO player-ID
  scheme (signed `crypto_idfp`) for reports to be accepted/attributed — the port's `PersistentId`
  may not be wire-compatible with XonStat's hashkey expectations. If not, the submission row could
  become an intended divergence with the URI default kept `""`.
- Where should `matchid` (`I` line) come from in the port (Base generates it in `world.qc`)? No
  port concept exists yet.
- The port builds the report on Shutdown only when `!Intermission.Running` to avoid double-reporting
  a finished match — Base files the `finished=false` report unconditionally on Shutdown (the DB is
  gone after a successful upload, so no double-report). Equivalent outcomes; noted for drift audits.

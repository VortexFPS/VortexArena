# sv-mapvoting — parity spec

**Base refs:** `server/mapvoting.qc` · `server/mapvoting.qh` · `client/mapvoting.qc` · `client/mapvoting.qh` · `common/constants.qh` (GTV_*, MAPVOTE_COUNT) · `common/net_linked.qh` (ENT_CLIENT_MAPVOTE, TE_CSQC_PICTURE)
**Port refs:** `src/VortexArena.Server/MapVoting.cs` · `src/VortexArena.Server/GameWorld.cs` (DriveEndOfMatchMapFlow) · `src/VortexArena.Server/Commands.cs` (CmdSuggestMap, DispatchImpulse) · `game/hud/MapVotePanel.cs` · `src/VortexArena.Server/Cvars.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
At the end of a match (intermission, after the player-stats game report is sent), the server presents the
players with a ballot. Two distinct votes exist:

1. **Map vote** (`g_maplist_votable > 0`): a grid of up to `g_maplist_votable` candidate maps drawn from the
   rotation (plus any player **suggestions**, plus an optional **abstain** "Don't care" slot). Each human
   player casts exactly one vote (impulses 1..N). After a timeout, or once the leader is mathematically
   unbeatable, the most-voted map wins (ties broken by a per-candidate `random()` value, with the currently
   running gametype's index acting as a fallback in gametype voting). Optionally, partway through, the ballot
   is **reduced** to the top `reduce_count` options.
2. **Gametype vote** (`sv_vote_gametype = 1`): runs *before* the map vote. Players pick a gametype from
   `sv_vote_gametype_options`; the winner switches the gametype (running maplist hooks, optionally resetting
   `g_maplist`), then a normal map vote follows.

The whole vote is **server-authoritative**: a linked CSQC entity (`ENT_CLIENT_MAPVOTE`) streams the ballot,
the live tallies, the availability mask and the winner to every client; the client only draws and forwards key
presses as `impulse N`. Map screenshots are fetched on demand (`TE_CSQC_PICTURE` / `cmd mv_getpicture`).

## Base algorithm (authoritative)

### Vote lifecycle / think loop  (`server/mapvoting.qc:MapVote_Start`, `MapVote_Think`)
- **Trigger:** `MapVote_Start` (sv) is called when the match ends; it gates on `PlayerStats_GameReport_DelayMapVote`,
  then enumerates maps and sets `mapvote_run = true`. `MapVote_Think` (sv) runs every server frame.
- **Algorithm:**
  - `MapVote_Think` early-returns until `!mapvote_run`. Once a winner is set (`mapvote_winner_time`), it waits
    `time > mapvote_winner_time + 1` then applies the gametype (if a gametype was voted) and `Map_Goto`.
  - Otherwise throttles to `mapvote_nextthink = time + 0.5` (snapping to `mapvote_timeout + 0.001` near timeout),
    handles `rescan_pending` map-info rebuild, runs `DoNextMapOverride`, and if no override: if
    `!g_maplist_votable || player_count <= 0` → `GotoNextMap(0)` (silent rotation, no vote); if
    `sv_vote_gametype` → `GameTypeVote_Start`; else if `get_nextmap()==""` → `MapVote_Init`. Then `MapVote_Tick`.
- **Constants:** think cadence **0.5 s**; winner→changelevel delay **1 s**.

### Build the ballot  (`MapVote_Init`, `MapVote_AddVotableMaps`, `MapVote_AddVotable`)
- `nmax = min(MAPVOTE_COUNT-1, g_maplist_votable)` if abstain else `min(MAPVOTE_COUNT, g_maplist_votable)`.
- `smax = min3(nmax, g_maplist_votable_suggestions, mapvote_suggestion_ptr)` — how many **suggested** maps to seed.
- Seed suggested maps first (randomized order, dropping invalid ones), then fill the rest from `GetNextMap()`
  (rotation) for up to `min(available*5, 100)` attempts; dedup by name; resolve each map's screenshot pakfile
  across `g_maplist_votable_screenshot_dir` dirs; assign each a `random()` tiebreak value (`mapvote_rng[i]`).
- `mapvote_count_real = mapvote_count`; if abstain, append a "don't care" slot (`MapVote_AddVotable(-2)`).
- Arm timers: `mapvote_reduce_time = time + g_maplist_votable_reduce_time`,
  `mapvote_reduce_count = g_maplist_votable_reduce_count`, `mapvote_timeout = time + g_maplist_votable_timeout`.
  Reduce disabled if `mapvote_count_real < 3` or reduce_time already elapsed.
- **Constants (defaults, all `g_maplist_votable*`):** `=6` candidates, `_timeout=30 s`, `_reduce_time=15 s`,
  `_reduce_count=2`, `_detail=1`, `_abstain=0`, `_suggestions=2`,
  `_suggestions_override_mostrecent=0`, `_show_suggester=1`, `_screenshot_dir="maps levelshots"`.
  `MAPVOTE_COUNT=20`. `GTV_FORBIDDEN=0`, `GTV_AVAILABLE=1`, `GTV_CUSTOM=2`.

### Casting votes  (`MapVote_Tick`)
- **Trigger:** every tick (sv). For each real client: forces `health = 2342` (a sentinel that hides the
  scoreboard), zeroes impulse, sends `SVC_FINALE ""`. Clears an invalid `.mapvote` (option no longer available).
  If `CS(it).impulse` in `1..mapvote_count` and that option is available, sets `it.mapvote = impulse` and
  flags `MapVote_TouchVotes`. Bots get the sentinel health but no vote.
- **State:** `.mapvote` (1-based; 0 = no vote). The client sets impulse via `localcmd("impulse N")` from the
  vote panel; the server reads `CS(it).impulse` each tick.

### Tally + decide  (`MapVote_CheckRules_count`, `MapVote_CheckRules_decide`, `MapVote_Finished`, `MapVote_Winner`)
- `_count`: reset `mapvote_selections`, count `mapvote_voters` (real clients) and per-option selections,
  then `heapsort(mapvote_count_real, …MapVote_ranked_cmp…)` into `mapvote_ranked` (descending votes; the
  running gametype index wins ties when an option has 0 votes; otherwise ties broken by `mapvote_rng`).
- `_decide`:
  - `mapvote_count_real == 1` → finish to option 0.
  - Compute `mapvote_voters_real` (minus abstainers). Finish to `mapvote_ranked[0]` ("choose best") if
    `time > mapvote_timeout`, OR the leader is unbeatable (`(voters_real - running_total) < votes_recent`),
    OR everyone abstained (`voters_real == 0`).
  - **Reduce:** if `mapvote_reduce_count >= 2` keep exactly that many top options; else keep all options with
    ≥1 vote (if ≥2 qualify). When `mapvote_reduce_time` elapses (and the keep condition holds), strip
    `GTV_AVAILABLE` from the losers, `MapVote_TouchMask`, log `:vote:reduce`.
- `MapVote_Finished(mappos)`: event-log `:vote:finished`, `FixClientCvars` all, then `MapVote_Winner(mappos)`
  (sets `mapvote_winner`, `mapvote_winner_time = time`, SendFlags BIT(3)) and `alreadychangedlevel = true`.
  (In gametype voting, instead calls `GameTypeVote_Finished` and either `Map_Goto` or restarts `MapVote_Init`.)

### Gametype vote  (`GameTypeVote_Start`, `GameTypeVote_AddVotable`, `GameTypeVote_Finished`, `GameTypeVote_SetGametype`)
- Tokenize `sv_vote_gametype_options` (default `"dm tdm ca ctf"`), add each as a votable gametype (real or
  `sv_vote_gametype_<name>_type` custom, flagged `GTV_CUSTOM`); availability checked vs the next map's
  supported gametypes. 0 available → keep current; 1 available → pick it; else open the vote
  (`timeout = time + sv_vote_gametype_timeout`, `detail = sv_vote_gametype_detail`, abstain off).
  If `sv_vote_gametype_default_current`, the current gametype's index becomes the 0-vote tiebreak winner.
- On finish: `GameTypeVote_SetGametype` runs `sv_vote_gametype_hook_all` + `sv_vote_gametype_hook_<name>`,
  `MapInfo_SwitchGameType`, and (if `sv_vote_gametype_maplist_reset` or a per-gametype maplist is set) rewrites
  `g_maplist`. Then a map vote starts for the new gametype.
- **Constants (defaults `sv_vote_gametype*`):** `=0` (off), `_timeout=20 s`, `_reduce_time=10 s`,
  `_reduce_count=2`, `_detail=1`, `_options="dm tdm ca ctf"`, `_default_current=1`, `_maplist_reset=1`.

### Suggestions  (`MapVote_Suggest`, command `suggestmap`)
- Before voting starts, players run `cmd suggestmap <map>`. Validated against `g_maplist_votable_suggestions`
  (off when 0), recency (`Map_IsRecent` unless `_suggestions_override_mostrecent`), gametype support, and
  dedup. Stored into `mapvote_maps_suggestions[]` / `mapvote_maps_suggesters[]`; event-logged `:vote:suggested`.
  Seeded into the ballot at `MapVote_Init`. `g_maplist_votable_show_suggester` reveals the suggester name.

### Networking  (`MapVote_SendEntity` / `MapVote_Spawn`, client `NET_HANDLE(ENT_CLIENT_MAPVOTE)`)
- `MapVote_Spawn` links `ENT_CLIENT_MAPVOTE`. `SendFlags` bits: BIT(0)=init (dirs, count, abstain, detail,
  timeout, gametype flags, mask, all options), BIT(1)=mask update, BIT(2)=vote tallies (+ detail tie-winner +
  `to.mapvote`), BIT(3)=winner (`mapvote_winner + 1`). Map screenshots sent via `TE_CSQC_PICTURE`
  (`MapVote_SendPicture` / client `Net_MapVote_Picture`), or fetched by curl/`mv_getpicture`.

### Client presentation  (`client/mapvoting.qc:MapVote_Draw`, `MapVote_DrawMapItem`, `MapVote_InputEvent`)
- HUD panel #MAPVOTE. Bold title "Vote for a map" / "Decide the gametype", green seconds countdown
  (`ceil(max(1, mv_timeout-time))`), a best-aspect-ratio grid of cells each with a level-shot thumbnail,
  `"N. <name> (M votes)"` label (detail-gated; `^5`-tinted tie winner), own-vote green border, selection
  yellow pulse, abstain "Don't care" line, suggester line. On winner: the winning thumbnail grows to center,
  losers fade (`mv_winner_alpha = max(0.2, 1-sqrt(time-mv_winner_time))`). Input: arrows move selection,
  enter/space/click/digits cast (`localcmd impulse N`), Ctrl+digit for two-digit indices.
  `MV_FADETIME = 0.2`. `hud_panel_mapvote_highlight_border` defaults to 1 in every shipped Base HUD skin
  (`hud_*.cfg`); the engine cvar in `_hud_descriptions.cfg` ships `""`. **The port registers it as 2** — a values mismatch.

## Port mapping

| Base feature | Port symbol | State |
|---|---|---|
| `MapVote_Start`/`MapVote_Init` ballot build | `MapVoting.Start` (from `GameWorld.DriveEndOfMatchMapFlow` via `Rotation.BuildBallot`) | live (rotation only) |
| `MapVote_Think` 0.5 s + 1 s winner delay | none — `Tick` runs every frame, no 0.5 s gate; winner applied immediately | partial/missing |
| `MapVote_Tick` cast from impulse | `MapVoting.CastVote` / `Abstain` exist but **no caller**; `DispatchImpulse` returns early during intermission | DEAD |
| `MapVote_CheckRules_count` tally + heapsort rank | `RecountFromBallots` (count only; no rank array) | partial |
| `MapVote_CheckRules_decide` timeout/unbeatable | `MapVoting.Tick` (timeout + `LeaderIsUnbeatable` + all-voted) | live logic, but no votes feed it |
| Ballot **reduce** (reduce_time/reduce_count) | NOT IMPLEMENTED | missing |
| `MapVote_Finished`/`MapVote_Winner` | `MapVoting.Finish` (+ `WinningMap`) → `ApplyMapChange` | live |
| Tiebreak `mapvote_rng` | `Finish` RandomSelection reservoir (seeded `Random`) | partial (logic differs) |
| Abstain slot | `MapVoting.Abstain` / panel "Don't care" | dead (server caster dead; panel unfed) |
| Suggestions (`suggestmap`) | `Commands.CmdSuggestMap` → `_mapSuggestions` | command live, but **never seeded into ballot** |
| Gametype vote (`GameTypeVote_*`) | NOT IMPLEMENTED on server (panel has `GametypeVote` draw only) | missing (server) |
| `ENT_CLIENT_MAPVOTE` net sync | NOT IMPLEMENTED (no mapvote net entity) | missing |
| `TE_CSQC_PICTURE` screenshot fetch | `MapVotePanel.DrawMapPicture` via `TextureCache` (local VFS) | partial (no net fetch) |
| Client `MapVote_Draw` panel | `game/hud/MapVotePanel.cs` (full draw incl. grid/abstain/winner/detail) | DEAD (never fed; no `SetVote`/`SetVotes`/`SetWinner` caller) |
| `MapVote_InputEvent` keyboard/mouse | NOT IMPLEMENTED (panel has no input handler) | missing |
| cvars | `Cvars.cs` registers a subset; several missing/misnamed | partial values |

## Parity assessment

- **Server lifecycle (live but neutered):** `MapVoting.Start/Tick/Finish/WinningMap` ARE on the live
  intermission path (`GameWorld.DriveEndOfMatchMapFlow`, gated correctly on `PlayerStats.DelayMapVote`,
  `g_maplist_votable>0`, `PlayerCount>0`, ballot>1). So a vote object is created and resolved. BUT there is
  **no way for a player to actually vote**: `CastVote`/`Abstain` have zero callers, and `DispatchImpulse`
  explicitly drops all impulses while `Intermission.Running`. Net result: the vote always finishes with zero
  votes → the winner is chosen at random (RNG reservoir over all candidates) after the 30 s timeout (or
  immediately if `ExpectedVoters` somehow reached). Observable: at match end no vote screen, a 30 s blank
  intermission, then a pseudo-random next map.
- **Client panel (dead):** `MapVotePanel` is a faithful, fairly complete port of `MapVote_Draw` (grid layout,
  level-shots with `nopreview_map` fallback, detail vote counts, `^5` tie tint, own-vote border, selection
  pulse, abstain line, winner grow/fade), but it is **never fed** — `SetVote`/`SetVotes`/`SetWinner` have no
  callers, and there is no `ENT_CLIENT_MAPVOTE` equivalent on the wire. The panel never becomes `Active` in a
  real match. It also has **no input handler**, so even if shown it couldn't cast a vote.
- **Reduce / keep-two (missing):** the port's `Tick` has no `reduce_time`/`reduce_count` logic; options are
  never dimmed/removed mid-vote. The port registers a **misnamed** cvar `g_maplist_votable_keeptwotime`
  (default 15) — the Base cvar is `g_maplist_votable_reduce_time` (default 15) — and never reads it.
- **Gametype vote (missing):** the entire `GameTypeVote_*` server machinery (pre-map gametype ballot,
  custom gametypes, hook execution, maplist reset, gametype switch) is absent. The panel can *draw* a gametype
  vote (`GametypeVote=true`) but nothing drives it.
- **Suggestions (half-wired):** `suggestmap` validates and stores suggestions faithfully (existence, dedup,
  `g_maplist_votable_suggestions==0` disable), but `_mapSuggestions` is **never seeded into the ballot**
  (`MapVoting.Start` uses only `Rotation.BuildBallot`). Recency/gametype-support checks differ.
- **Override chain (live, faithful):** the pre-vote `DoNextMapOverride` priority (campaign / queued
  `nextmap` / `samelevel`) and the no-vote silent `GotoNextMap` rotation ARE implemented faithfully in
  `GameWorld.DriveEndOfMatchMapFlow` (campaign → QueuedNextMap → samelevel → vote → rotation).
- **Vote-time client state (missing):** Base `MapVote_Tick` forces every client (incl. bots) to
  `RES_HEALTH=2342` and sends `SVC_FINALE ""` once to hide the scoreboard and zero `CS(it).impulse` each
  tick; the port models none of this (the 2342 sentinel does not exist on the vote path).
- **Values (partial):** present defaults match Base (`votable=6`, `timeout=30`, `abstain=0`,
  `suggestions=2`, `suggestions_override_mostrecent=0`, `sv_vote_gametype=0/_timeout=20/_options` close —
  port ships `"dm tdm ctf"` vs Base `"dm tdm ca ctf"`). Missing entirely: `_reduce_count` (2), `_detail` (1),
  `_screenshot_dir`, `_show_suggester` (1), and all of `sv_vote_gametype_reduce_time/_reduce_count/_detail/
  _default_current/_maplist_reset`. Misnamed: `g_maplist_votable_reduce_time` → `g_maplist_votable_keeptwotime`.
- **Timing (partial):** no 0.5 s think gate and no 1 s winner→changelevel delay; the port applies the map
  change as soon as the vote finishes. Tiebreak uses a per-finish RNG reservoir rather than per-candidate
  `mapvote_rng` assigned at ballot build; the running-gametype 0-vote tiebreak is absent.

### Intended divergences
None declared. All differences above are gaps, not deliberate port changes.

## Verification
- **Static/caller trace (high confidence):** grep across `src/`+`game/` shows `CastVote`/`Abstain`/`Disqualify`
  and `MapVotePanel.SetVote`/`SetVotes`/`SetWinner` have **no callers** outside their own files; `DispatchImpulse`
  returns early on `Intermission.Running` (`src/VortexArena.Server/Commands.cs`). Server lifecycle calls
  confirmed at `GameWorld.cs:2003/2017-2023`.
- **cvar diff (high confidence):** `src/VortexArena.Server/Cvars.cs:133-135,391-396` vs
  `Base/.../xonotic-server.cfg:375-393`.
- **Behavioral (unverified at runtime):** the "random next map after blank 30 s intermission" outcome is
  inferred from code, not observed in a running match.

## Open questions
- Is the blank intermission + random next-map actually what players see at end of match, or does some other
  path (campaign / `QueuedNextMap` / `samelevel`) usually short-circuit the vote before it matters? Needs a
  live multiplayer end-of-match observation.
- Was the `g_maplist_votable_keeptwotime` cvar an intentional rename or a transcription error? (No Base cvar by
  that name exists; Base uses `g_maplist_votable_reduce_time`.)

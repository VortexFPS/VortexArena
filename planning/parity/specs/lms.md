# Last Man Standing (LMS) — parity spec

**Base refs:** `common/gametypes/gametype/lms/{lms.qh,lms.qc,sv_lms.qc,sv_lms.qh,cl_lms.qc,cl_lms.qh}`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/LastManStanding.cs`, `src/VortexArena.Server/GameWorld.cs` (gametype drive), `src/VortexArena.Server/ClientManager.cs` (roster)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
LMS is a **free-for-all elimination** gametype: every player starts with a fixed pool of *lives*. A
death costs the **victim** one life (not the killer — kills score no points; lives are the currency).
At 0 lives a player is permanently **out of the game** (`frags = FRAGS_PLAYER_OUT_OF_GAME`) and gets a
finishing rank; they keep watching as a ranked spectator but cannot respawn or rejoin. The match ends
when **at most one** player still has lives — that last survivor wins. Crucially, LMS is **NOT
round-based**: living players respawn continuously throughout the single match; only running out of
lives takes you out. LMS also layers on: a CA-like fixed start loadout with no item pickups and no
health/armor regen; a "leader" system (players ≥2 lives ahead glow + get periodic radar waypoints to
discourage hiding); a dynamic per-player respawn delay (further-behind players wait longer); a dynamic
vampire lifesteal for under-dogs; and optional extra-life pickups.

## Base algorithm (authoritative)

### Lives bookkeeping & scoring fields  (`sv_lms.qh` REGISTER_MUTATOR; `sv_lms.qc:GiveFragsForKill`)
- Scoring fields: `SP_LMS_LIVES` ("lives", secondary sort) and `SP_LMS_RANK` ("rank",
  `SFL_LOWER_IS_BETTER | SFL_RANK | SORT_PRIO_PRIMARY | SFL_ALLOW_HIDE`). `GameRules_score_enabled(false)`
  (frag score column hidden). `GameRules_limit_score(g_lms_lives_override>0 ? override : -1)` and
  `GameRules_limit_lead(0)`.
- **On a kill** (`GiveFragsForKill`, runs only when `!warmup && time > game_starttime`): victim
  `LMS_LIVES -= 1`; track `lms_lowest_lives = min(lms_lowest_lives, tl)`; if `tl <= 0` the victim becomes
  `FRAGS_PLAYER_OUT_OF_GAME` and is assigned `LMS_RANK = (count of players still FRAGS_PLAYER)`. The frag
  score itself is zeroed (`M_ARGV(2)=0`).
- `lms_lowest_lives` is reset to `999` at map reset (`reset_map_global`) and in `lms_Initialize`.

### New-player lives gate  (`sv_lms.qc:LMS_NewPlayerLives`)
- `fl = floor(fraglimit)`; if `fl==0 || fl>999` → `fl=999`.
- If `lms_lowest_lives < 1` → return 0 (first player already eliminated → match locked, nobody joins).
- If `!g_lms_join_anytime` and `lms_lowest_lives < fl - max(0, floor(g_lms_last_join))` → return 0 (too late).
- Else return `bound(1, lms_lowest_lives, fl)`. A late joiner gets the **current lowest** life count so
  they don't start ahead.

### Win condition  (`sv_lms.qc:WinningCondition_LMS`, via `CheckRules_World` hook)
- Returns `WINNING_NO` while `warmup_stage || time <= game_starttime`.
- Count active living players (`IS_PLAYER && frags == FRAGS_PLAYER`) and "played" players (have a rank).
  - `>1` living → game continues (campaign special-case: if a human's lives hit 0, game over).
  - exactly 1 living → if `LMS_NewPlayerLives()` still allows joins AND enough match time has passed
    (`time > game_starttime + g_lms_forfeit_min_match_time`), forfeit-win for the survivor; else if no more
    joiners possible, that survivor **wins** (rank 1, `winning=1`).
  - 0 living → forfeit/draw end.
- With ≥2 living it runs `WinningConditionHelper`: if top two scores are **equal** → `WINNING_NEVER`
  (cancels the time limit). Different → `WINNING_NO` (let the timelimit decide).

### Player lifecycle  (`sv_lms.qc`)
- `ClientConnect`: `frags = FRAGS_SPECTATOR`.
- `lms_AddPlayer` / `PutClientInServer` / `ForbidSpawn`: a player only enters with lives from
  `LMS_NewPlayerLives()`; 0 → stays observer (and gets `CENTER_LMS_NOLIVES` if mid-match).
- `PlayerSpawn`: a mid-match joiner (`INGAME_JOINING`) spawns with health/armor clamped down to the
  **least-healthy least-lives** opponent (so they can't out-heal the field), tracked via
  `last_forfeiter_*`.
- `lms_RemovePlayer` / `ClientDisconnect` / `MakePlayerObserver`: on leave/forced-spectate after
  game start the player is assigned the next finishing rank (`FRAGS_PLAYER_OUT_OF_GAME`), lives zeroed,
  and `INFO_LMS_NOLIVES` is broadcast. Ranked (out-of-game) players cannot become real spectators
  (`ClientCommand_Spectate` returns).
- `CalculateRespawnTime`: always `RESPAWN_FORCE`. If `LMS_LIVES <= 0` → `RESPAWN_SILENT`,
  `respawn_time = time + 2` (parked, no rejoin). Else if `g_lms_dynamic_respawn_delay > 0`:
  `delay = g_lms_dynamic_respawn_delay_base + g_lms_dynamic_respawn_delay_increase * max(0, maxLives - plLives)`,
  clamped to `g_lms_dynamic_respawn_delay_max`. With only 2 players left, `maxLives` is forced to 0 (flat
  base delay).

### Start loadout / no items / no regen  (`sv_lms.qc:SetStartItems`, `ReadLevelCvars`, `PlayerRegen`, `FilterItem*`)
- `start_health = g_lms_start_health`, `start_armorvalue = g_lms_start_armor`, and the five ammo pools
  from `g_lms_start_ammo_*` (warmup copies too). `IT_UNLIMITED_AMMO` if `!g_use_ammunition`.
- `SetWeaponArena`: default arena `g_lms_weaponarena = "most_available"` (all available weapons at spawn).
- `PlayerRegen`: if `!g_lms_regenerate` health/armor **regen disabled**; if `!g_lms_rot` rot disabled.
- `FilterItemDefinition` / `FilterItem`: unless `g_lms_items` / `g_pickup_items>0`, item pickups are
  suppressed (HealthMega may be replaced by an ExtraLife when `g_powerups && g_lms_extra_lives`).
- `ForbidThrowCurrentWeapon`: dropping weapons is forbidden.

### Leaders  (`sv_lms.qc:lms_UpdateLeaders`, `SV_StartFrame`, `cl_lms.qc`)
- A player is a "leader" when their lives == max AND `(maxLives - secondMaxLives) >= g_lms_leader_lives_diff (2)`
  AND the count of max-lives players `<= pl_cnt * g_lms_leader_minpercent (0.5)`. Recomputed on
  `PlayerDied` and `lms_RemovePlayer`.
- `PlayerPowerups`: a leader (with the waypoint attached) gets `EF_ADDITIVE | EF_FULLBRIGHT` (glow).
- `SV_StartFrame`: `lms_leaders` counted; leaders' radar waypoint (`WP_LmsLeader`) is shown only
  periodically — visible for `g_lms_leader_wp_time (5s)` then hidden for
  `g_lms_leader_wp_interval (25s) + random()*g_lms_leader_wp_interval_jitter (10s)`. On the visibility
  edge, leaders get `CENTER_LMS_VISIBLE_LEADER`, others `CENTER_LMS_VISIBLE_OTHER`. Stats recycled:
  `STAT(REDALIVE)=lms_leaders`, `STAT(BLUEALIVE)=lms_leaders_lives_diff`, `STAT(OBJECTIVE_STATUS)=visible`.
- `cl_lms.qc:HUD_Mod_LMS_Draw`: the mod-icon panel draws the leader count (player_neutral icon + number),
  a stalemate flag when visible, and a colored `+N` lives-diff (yellow / orange at 3 / red at ≥4).
- `cl_lms.qc:DrawInfoMessages`: shows `^1You have no more lives left` when the local player's primary
  score (rank) > 0.

### Dynamic vampire  (`sv_lms.qc:Damage_Calculate`)
- If `g_lms_dynamic_vampire (1)` and attacker & target are living players (attacker != target):
  `diff = targetLives - attackerLives - g_lms_dynamic_vampire_min_lives_diff (2)`; if `diff >= 0`,
  `factor = g_lms_dynamic_vampire_factor_base (0.1) + diff * g_lms_dynamic_vampire_factor_increase (0.1)`,
  clamped to `g_lms_dynamic_vampire_factor_max (0.5)`. Attacker heals `damage * factor` (capped at
  `start_health`). Under-dogs steal health from leaders.

### Extra-life pickups  (`sv_lms.qc:ItemTouch`, `lms_replace_with_extralife`)
- When enabled (`g_powerups && g_lms_extra_lives`), HealthMega spawns are replaced by `ITEM_ExtraLife`.
  Touching one grants `g_lms_extra_lives` lives and shows `CENTER_EXTRALIVES`.

### Constants (Base defaults, all `g_lms_*` are authority/server cvars)
| cvar | default | unit | side |
|---|---|---|---|
| gametype `lives` (gametype_init) | 5 | lives | shared |
| legacy default `m_legacydefaults` | "9 20 0" (lives/timelimit/leadlimit) | — | shared |
| `g_lms_lives_override` | -1 (off → use 5) | lives | authority |
| `g_lms_join_anytime` | 1 | bool | authority |
| `g_lms_last_join` | 3 | lives | authority |
| `g_lms_forfeit_min_match_time` | 30 | s | authority |
| `g_lms_start_health` | 200 (xonotic.cfg) | hp | authority |
| `g_lms_start_armor` | 200 (xonotic.cfg) | ap | authority |
| `g_lms_weaponarena` | "most_available" | — | authority |
| `g_lms_regenerate` | 0 | bool | authority |
| `g_lms_rot` | 0 | bool | authority |
| `g_lms_items` | 0 | bool | authority |
| `g_lms_extra_lives` | 0 | lives | authority |
| `g_lms_leader_lives_diff` | 2 | lives | authority |
| `g_lms_leader_minpercent` | 0.5 | fraction | authority |
| `g_lms_leader_wp_time` | 5 | s | authority |
| `g_lms_leader_wp_interval` | 25 | s | authority |
| `g_lms_leader_wp_interval_jitter` | 10 | s | authority |
| `g_lms_dynamic_respawn_delay` | 1 | bool | authority |
| `g_lms_dynamic_respawn_delay_base` | 2 | s | authority |
| `g_lms_dynamic_respawn_delay_increase` | 3 | s/life | authority |
| `g_lms_dynamic_respawn_delay_max` | 20 | s | authority |
| `g_lms_dynamic_vampire` | 1 | bool | authority |
| `g_lms_dynamic_vampire_factor_base` | 0.1 | fraction | authority |
| `g_lms_dynamic_vampire_factor_increase` | 0.1 | fraction/life | authority |
| `g_lms_dynamic_vampire_factor_max` | 0.5 | fraction | authority |
| `g_lms_dynamic_vampire_min_lives_diff` | 2 | lives | authority |

## Port mapping
The port gametype is `LastManStanding.cs` (`[GameType]`, registered, resolvable via `GameTypes.ByName("lms")`).
It is selected and **activated live** in `GameWorld.ActivateGameType` (`case LastManStanding lms: lms.Activate(); EnableRounds();`)
and driven each frame in `GameWorld.DriveGametypeFrame` (`lms.UpdateLeaders(); lms.CheckWinningCondition();`).
`MatchEnded`/`Winner` are consumed by the end-of-match flow.

| Base feature | Port symbol | Status |
|---|---|---|
| Lives lose-on-death, frag score zeroed | `LastManStanding.OnDeath` (subscribes `Combat.Death` in `Activate`) | LIVE — logic faithful |
| Elimination at 0 lives + finishing rank | `OnDeath` → `st.OutOfGame`, `Rank = CountAlive()+1` | LIVE but rank uses tracked players, not QC `FRAGS_PLAYER` semantics |
| Win when ≤1 has lives | `CheckWinningCondition` | LIVE — reduced core only |
| Scoring columns LMS_LIVES / LMS_RANK | `Activate` (GS.DeclareColumn / SetSortKeys), `SyncColumns` | LIVE |
| `LMS_NewPlayerLives` join gate | `NewPlayerLives` / `LowestLives` | **DEAD** (no caller) |
| Starting lives (override/cvar/5, fraglimit cap) | `StartingLives` (used by `GetState`) | LIVE but **logic/values partial**: reads a non-existent cvar `g_lms_lives` as "primary" (falls through to const 5) and ignores the per-map mapinfo `lives=N` override + the `m_legacydefaults "9 20 0"` legacy=9 switch. Default value 5 still lands. |
| Dynamic respawn delay | `RespawnDelayFor` | **DEAD** (no caller; respawn timing comes from generic `RespawnTiming`) |
| 0-lives → RESPAWN_SILENT / parked | — | **MISSING** (out-of-game players are not denied respawn by the lives system) |
| Continuous respawn for living players | (LMS uses `EnableRounds` → round gate) | **DIVERGENT** (round-based, not continuous) |
| Leader computation | `UpdateLeaders` / `LeadersLivesDiff` / `Leaders` | LIVE (computed) but consumers DEAD |
| Leader glow (EF_ADDITIVE\|EF_FULLBRIGHT) | — | **MISSING** |
| Leader radar waypoint + periodic visibility timer | — | **MISSING** |
| Leader notifications (VISIBLE_LEADER/OTHER) | notifications defined; no caller | **DEAD** |
| HUD mod icon (leader count + `+N`) | — (ModIconsMode has no Lms) | **MISSING** |
| "no more lives" info message | notification defined; no caller | **DEAD** |
| Dynamic vampire lifesteal | — (generic `VampireMutator` exists but is a different feature, not enabled) | **MISSING** |
| Extra-life pickups | `SpawnExtraLife`/`GiveExtraLife`/`ExtraLifeTouch` | **DEAD** (no spawner caller) |
| Start loadout (g_lms_start_health/armor/ammo) | — (`g_lms_start_*` unreferenced) | **MISSING** (players get normal DM loadout) |
| Weapon arena `most_available` | — | **MISSING** |
| No regen / no rot | — (`g_lms_regenerate`/`g_lms_rot` unreferenced) | **MISSING** (players regen) |
| No item pickups | — (`g_lms_items` unreferenced) | **MISSING** |
| Forbid weapon drop | — | **MISSING** |
| Mid-joiner health clamp to weakest | — | **MISSING** |
| Ranked players can't spectate | — | **MISSING** |

The per-player `LmsState` (`_states`) is **separate** from `MatchController`'s roster. `ClientManager`
calls `MatchController.AddPlayer/RemovePlayer` (Deathmatch roster), which do **not** forward to
`LastManStanding.AddPlayer/RemovePlayer`. LMS state is created lazily in `OnDeath` via `GetState`, so the
lives count works for deaths but there is no join-time seeding broadcast, no disconnect-time rank
assignment, and no leader recompute on disconnect.

## Parity assessment

### Gaps (what a player observes)
- **LMS plays as a round-elimination mode, not a lives mode.** Because `Activate` calls
  `EnableRounds()` with the default CA-style predicates, respawns are gated by the round handler
  (`DefaultCanRoundEnd` counts *alive teams* — degenerate in an FFA where everyone is team None). A
  living player who dies mid-match will not respawn until a "round" resets, and elimination at 0 lives
  is not what stops them — the round gate is. This is the headline logic divergence.
- **No fixed LMS loadout.** Players spawn with the normal Deathmatch start items and **regenerate
  health/armor** (g_lms_regenerate/g_lms_rot ignored), and **items still spawn** (g_lms_items ignored).
  Base LMS is a CA-like all-weapons, no-pickup, no-regen survival mode. Players will see health regen and
  item pickups that should not exist.
- **No leader visuals at all.** Leaders don't glow, no radar waypoint appears over their head, no
  periodic "you can now be seen" centerprint, and the HUD mod-icon (leader count + colored `+N` lives
  lead) is absent. The whole anti-hiding mechanic is invisible.
- **No dynamic respawn delay.** A player far behind on lives respawns at the same time as anyone else
  (when they respawn at all), instead of waiting `2 + 3*(maxLives-plLives)` s up to 20 s.
- **No dynamic vampire.** Under-dogs do not steal health when hitting leaders.
- **No extra-life pickups on the live path** (the code exists but nothing spawns the item).
- **No join gate / late-join lives clamp.** `NewPlayerLives` is never called; a late joiner is not
  given the lowest life count and the "match locked, can't join" rule is absent.
- **No "no more lives" info message** and no elimination broadcast on the live path.

### Liveness
- **Live:** `Activate` (Combat.Death subscription), `OnDeath` (lose-a-life + eliminate + zero frag
  score), `UpdateLeaders` (computes `IsLeader`/`LeadersLivesDiff` — but no consumer), `CheckWinningCondition`,
  `MatchEnded`/`Winner`, `StartingLives`/`GetState` (lazy lives seeding), scoring columns via `SyncColumns`.
- **Dead:** `NewPlayerLives`/`LowestLives` (no caller), `RespawnDelayFor` (no caller),
  `SpawnExtraLife`/`GiveExtraLife`/`ExtraLifeTouch` (no spawner), `LivesOf`/`Leaders`/`LeadersLivesDiff`
  (no external reader), `AddPlayer`/`RemovePlayer` (MatchController doesn't forward to LMS), the three LMS
  notifications (defined, no fire on live path).
- **Missing:** loadout/regen/items, leader visuals + HUD icon, dynamic vampire, 0-lives respawn-deny,
  mid-joiner clamp, weapon-drop forbid, spectate restriction.

### Intended divergences
None declared. The round-based implementation is most likely an expedient reuse of the round handler
rather than a deliberate design choice, so it is flagged as a gap, not an intended divergence.

## Verification
- Code-trace verified (read): `LastManStanding.cs` in full; `GameWorld.cs` ActivateGameType (1288-1377),
  DriveGametypeFrame (1596-1650), RespawnDuePlayers/DeadPlayerThink (1658-1763), MatchEnded switch (276-284),
  winner switch (2055-2063); `MatchController.cs` (Deathmatch-only roster, no LMS forwarding); `ClientManager.cs`
  (172/568 call MatchController, not LMS); `ModIconsPanel.cs` (enum has no Lms).
- Grep-verified DEAD/MISSING: no callers of `RespawnDelayFor`, `SpawnExtraLife`, `GiveExtraLife`,
  `NewPlayerLives` outside the class; `g_lms_start_*`/`g_lms_weaponarena`/`g_lms_regenerate`/`g_lms_rot`
  unreferenced in `src/`; no `Mod_LMS`/Lms case in the HUD mod-icon panel; no vampire wiring in LMS.
- Constants cross-checked against `gametypes-server.cfg` (489-512) and `balance-xonotic.cfg` (57-63),
  `lms.qh` gametype_init (`lives=5`, legacy "9 20 0").
- **Unverified at runtime:** exact in-match respawn behavior under the round handler for FFA LMS (the
  degenerate `DefaultCanRoundEnd` team count); whether `CheckWinningCondition`'s `alive<=1` latch fires
  before the round handler resets. Marked the affected rows `timing: unknown` / `confidence: medium`.

## Open questions
- Should LMS be converted off the round handler to a continuous-respawn + per-player-lives model, or is a
  round-based approximation acceptable for the port? (Affects the headline logic row.)
- Where should the LMS fixed loadout / no-regen / no-items be wired — a SetStartItems-equivalent hook, or
  per-gametype spawn config? The port has no `g_lms_start_*` plumbing yet.
- Is the leader/waypoint/HUD-icon presentation in scope, or deferred like other cross-boundary client work?

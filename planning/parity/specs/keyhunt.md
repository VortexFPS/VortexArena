# Key Hunt — parity spec

**Base refs:** `common/gametypes/gametype/keyhunt/{sv_keyhunt.qc, sv_keyhunt.qh, cl_keyhunt.qc, cl_keyhunt.qh, keyhunt.qc, keyhunt.qh}`
**Port refs:** `src/VortexArena.Common/Gameplay/GameTypes/KeyHunt.cs` · `src/VortexArena.Net/GametypeStatusBlock.cs` · `game/hud/ModIconsPanel.cs` (DrawKeyhunt) · `src/VortexArena.Server/GameWorld.cs` (wiring) · `src/VortexArena.Server/Bot/BotObjectiveRoles.cs` (RoleKeyHunt)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
Key Hunt (`kh`) is a round-based teamplay gametype. Each active team (2–4, default 3) owns exactly one key.
At round start one randomly-chosen live player on each team is given that team's key. A team wins the round
("capture") when it holds **all** keys simultaneously **and** every key carrier is within `maxdist` of each
other. Captures, collecting an enemy key, fragging an enemy key carrier, and pushing/destroying carriers all
feed the team SCORE; first team to the point limit (default 1000) wins the match. Keys are dropped on death,
on suicide, on disconnect, and voluntarily via the +use key, and auto-return after a timeout (or when they
fall into lava/slime/triggers if `return_when_unreachable`). A periodic alarm plays while all keys are owned
by one team, and an "interfere/help/meet" center-print storm warns the other teams.

## Base algorithm (authoritative)

### Controller + round loop  (`sv_keyhunt.qc:kh_Initialize / kh_Controller_Think / kh_WaitForPlayers / kh_StartRound`)
- **Trigger:** `kh_Initialize` (REGISTER_MUTATOR kh ONADD) creates `kh_controller`, a pure entity whose
  `kh_Controller_Think` runs every 1s. `kh_Controller_SetThink(t, func)` schedules `func` after `t` seconds
  (counts `cnt` down each think; `cnt==0` fires the func).
- **Algorithm:** boots in `kh_WaitForPlayers`. Each second it computes `kh_GetMissingTeams()` (a team is
  "missing" if it has no live, non-chatting player). If any team is missing → re-arm `kh_WaitForPlayers(1)`.
  When all present → center-print `CENTER_KEYHUNT_ROUNDSTART delay_round` and `SetThink(delay_round, kh_StartRound)`.
  `kh_StartRound` spawns one key per team (reservoir-random live player on that team via `random()*players<=1`),
  disables tracking, and (if `delay_tracking >= 0`) center-prints `CENTER_KEYHUNT_SCAN` and schedules
  `kh_EnableTrackingDevice` after `delay_tracking`.
- **Constants:** `g_balance_keyhunt_delay_round = 5`, `g_balance_keyhunt_delay_tracking = 10`. Spawn angle per
  key `360*i/AVAILABLE_TEAMS`.
- **State:** `game_starttime` gate (wait until match start); `kh_tracking_enabled` (radar/waypoint visibility).

### Key entity spawn + presentation  (`sv_keyhunt.qc:kh_Key_Spawn`)
- Creates `item_kh_key` edict: `modelindex = kh_key_dropped` (model "key", `MDL_KH_KEY`), random yaw, touch
  `kh_Key_Touch`, think `kh_Key_Think` (0.05s cadence), `event_damage = kh_Key_Damage`, `takedamage = DAMAGE_YES`,
  `damagedbytriggers/contents = return_when_unreachable`, `colormod = Team_ColorRGB(team) * KH_KEY_BRIGHTNESS`,
  per-team colored `netname` ("^1red key"/"^4blue key"/"^3yellow key"/"^6pink key"), bbox
  `KH_KEY_MIN '-25 -25 -46'` / `KH_KEY_MAX '25 25 4'`. Spawns the dropped-key WAYPOINT SPRITE
  (`WP_KeyDropped`), center-prints `CENTER_KEYHUNT_START` to the initial owner, then `kh_Key_AssignTo(initial_owner)`.
- **Constants:** `KH_KEY_BRIGHTNESS = 2`, `KH_KEY_ZSHIFT = 22`, `KH_KEY_XYDIST = 24`, `KH_KEY_XYSPEED = 45`,
  `KH_KEY_WP_ZSHIFT = 20`.

### Attach / detach / orbit  (`kh_Key_Attach / kh_Key_Detach / kh_Key_Think`)
- **Attach (carried):** `SOLID_NOT`, `MOVETYPE_NONE`, set attachment to owner at `'0 0 1'*KH_KEY_ZSHIFT`,
  `team = owner.team`, `damageforcescale=0`, `takedamage=DAMAGE_NO`, `modelindex = kh_key_carried`.
- **Orbit:** while carried, `kh_Key_Think` spins the key around the owner: `makevectors('0 1 0'*(cnt + (time%360)*KH_KEY_XYSPEED)); setorigin(v_forward*KH_KEY_XYDIST + zshift)` — the key circles the carrier at radius 24.
- **Detach (dropped):** `SOLID_TRIGGER`, `FL_ITEM`, `MOVETYPE_TOSS`, `nudgeoutofsolid`, `pain_finished = time + delay_return`,
  `damageforcescale = damageforcescale cvar`, `takedamage=DAMAGE_YES`, `modelindex = kh_key_dropped`,
  record `previous_owner`.

### Pickup / collect  (`kh_Key_Touch / kh_Key_Collect / kh_Key_AssignTo`)
- Touch by a live, non-independent player who isn't the recent dropper-within-`delay_collect` → `kh_Key_Collect`:
  play `SND_KH_COLLECT`, and if the collector isn't the dropper's team, award `score_collect` (kh_Scores_Event
  "collect") + `KH_PICKUPS +1`. Send `INFO_KEYHUNT_PICKUP`. Then `kh_Key_AssignTo`.
- `kh_Key_AssignTo` maintains the per-player `kh_next` linked list of carried keys, attaches the key,
  attaches/updates the carrier WAYPOINT SPRITE (per-team WP_KeyCarrier*), and on a change of "all-owned team"
  flips all carrier sprites between "Run here"/"Key Carrier" and arms the interfere message.
- **Constants:** `g_balance_keyhunt_delay_collect = 1.5`, `g_balance_keyhunt_score_collect = 3`.

### Capture (win the round)  (`kh_Key_Think → kh_Key_AllOwnedByWhichTeam → kh_WinnerTeam → kh_FinishRound`)
- Each per-key think (0.05s): if all keys exist and are owned by the same team (`kh_Key_AllOwnedByWhichTeam != -1`):
  - play periodic `SND_KH_ALARM` on the owner every **2.5 s** (`siren_time`);
  - if every carrier is within `maxdist` of the **first key's owner** (`vdist(key.owner.origin - p, >, maxdist)`)
    → `kh_WinnerTeam(team)`.
- `kh_WinnerTeam`: distribute `(AVAILABLE_TEAMS-1) * score_capture` evenly across the keys (DistributeEvenly),
  `KH_CAPS +1` per key owner, `nades_GiveBonus`, center-print `CENTER_ROUND_TEAM_WIN`, info `INFO_KEYHUNT_CAPTURE`,
  draw `EFFECT_TR_NEXUIZPLASMA` lightning between key origins + `te_customflash` at the midpoint,
  `play2all(SND_KH_CAPTURE)`, then `kh_FinishRound`.
- `kh_FinishRound`: remove all keys, center-print `CENTER_KEYHUNT_ROUNDSTART delay_round`,
  `SetThink(delay_round, kh_StartRound)`.
- **Constants:** `g_balance_keyhunt_maxdist = 150`, `g_balance_keyhunt_score_capture = 100`. Alarm period 2.5 s.

### Loss / destruction  (`kh_Key_Think (timeout) / kh_LoserTeam`)
- A dropped key whose `pain_finished` (set to `time + delay_return`, default **60 s**) elapses → `kh_LoserTeam`.
- `kh_LoserTeam`: if pushed by an enemy → award `score_push` to the pusher + `KH_PUSHES +1`, log the previous
  owner `-score_push`, info `INFO_KEYHUNT_PUSHED`. Else (destroyed/timed out) → award `score_destroyed` split
  among the other teams' players + key holders (DistributeEvenly with `score_destroyed_ownfactor`), log the
  previous owner `-score_destroyed`, `KH_DESTRUCTIONS +1`, info `INFO_KEYHUNT_DESTROYED`. Center-print
  `CENTER_ROUND_TEAM_LOSS`, `play2all(SND_KH_DESTROY)`, `te_tarexplosion`, then `kh_FinishRound`.
- **Constants:** `delay_return = 60`, `delay_damage_return = 5`, `score_push = 60`, `score_destroyed = 50`,
  `score_destroyed_ownfactor = 1`.

### Drop on death / suicide / disconnect / observe  (`kh_Key_DropAll` + hooks)
- `PlayerDies`/`MakePlayerObserver`/`ClientDisconnect`/`DropSpecialItems` → `kh_Key_DropAll(player, suicide)`:
  for each carried key, `KH_LOSSES +1`, info `INFO_KEYHUNT_LOST`, detach, throw with
  `W_CalculateProjectileVelocity(... dropvelocity * v_forward ...)` at a random up/yaw angle, set `pusher`,
  `pushltime = time + protecttime`, mark `kh_dropperteam` on suicide, play `SND_KH_DROP`.
- **Constants:** `dropvelocity = 300`, `protecttime = 0.8`.

### Voluntary drop  (`MUTATOR_HOOKFUNCTION(kh, PlayerUseKey) → kh_Key_DropOne`)
- +use drops ONE key: `KH_LOSSES +1`, info `INFO_KEYHUNT_DROP`, set `kh_droptime`, throw with `throwvelocity`,
  `pushltime = time + protecttime`, `kh_dropperteam = key.team`, play `SND_KH_DROP`.
- **Constants:** `throwvelocity = 400`.

### Combat scaling  (`MUTATOR_HOOKFUNCTION(kh, Damage_Calculate)`)
- Scales player-vs-player damage AND force by 3-vectors selected by attacker/target carry state:
  `carrier_damage`/`carrier_force` when the attacker carries a key (x=self, y=other carrier, z=noncarrier),
  `noncarrier_damage`/`noncarrier_force` otherwise. All stock defaults `"1 1 1"` (no-op at defaults).

### Frag scoring  (`kh_HandleFrags` via `GiveFragsForKill`)
- Fragging an enemy key carrier: `score_carrierfrag - 1` (kh_Scores_Event "carrierfrag") + `KH_KCKILLS +1`
  (the kill frag is added separately). Team-killing a teammate carrier: `-nk*score_collect` penalty.
- **Constants:** `score_carrierfrag = 2`.

### HUD modicon pack  (`kh_update_state` → `STAT(OBJECTIVE_STATUS)`; decode `cl_keyhunt.qc:HUD_Mod_KH`)
- Packs four 5-bit slots (one per key, in `key.count` = team index order). Slot value: 0 = no key,
  30 = dropped (no owner), the carrier team code otherwise; the recipient's own carried keys are overwritten
  with 31 (per-recipient personalization). The CSQC decode subtracts 1: 31→30 ("carried by me", blink),
  30→29 (dropped), else maps to a team. Lays icons out in a quad/horizontal/vertical grid with
  `kh_<color>_taken` / `kh_<color>_carrying` / `kh_dropped` skin art; blinks when the local team holds all keys.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `kh_Initialize`/controller | `KeyHunt.OnInit` / `Tick` / `WaitForPlayers`(folded into Tick) | live (GameWorld.Tick → `kh.Tick()`) |
| `kh_StartRound` | `KeyHunt.StartRound` / `PickRandomLivePlayer` | live; reservoir RNG faithful |
| `kh_Key_Spawn` (model/color/netname) | `KeyHunt.SpawnKey` / `SetKeyVisual` | live; model + glow + per-team name + spin |
| `kh_Key_Attach/Detach` | `GametypeEntities.AttachToCarrier`/`DetachFromCarrier` | live; **no orbit** (carry offset fixed `(0,0,20)`) |
| `kh_Key_Touch`/`Collect` | `KeyHunt.KeyTouchEntity` → `CollectKey` | live; collect-score + delay_collect faithful; **no SND_KH_COLLECT, no INFO_KEYHUNT_PICKUP** |
| `kh_Key_AssignTo` (at spawn) | `KeyHunt.AssignKeyNoScore` | live |
| `kh_Key_AssignTo` (pickup, instant capture) | `KeyHunt.AssignKey` → `CheckCapture` (no maxdist) | **DEAD** (no live caller) |
| `kh_Key_Think` capture geometry | `KeyHunt.KeyThinkEntity` / `TickKeys` → `CheckCaptureGeometry` | live; maxdist check present but DEFAULT WRONG (4000 vs 150); **no alarm** |
| `kh_WinnerTeam` | folded into `CheckCaptureGeometry` | partial; team SCORE + KH_CAPS only; **no notify/sound/effects, no nade bonus** |
| `kh_FinishRound`/`delay_round` | `Phase=WaitingForPlayers` → `Countdown` | partial; countdown happens but **no ROUNDSTART notify** |
| `kh_LoserTeam` (push/destroy split) | — | **NOT IMPLEMENTED** (a timed-out key just resets the round) |
| `kh_Key_DropAll` (death) | `KeyHunt.DropAllKeys` (via `OnDeath`) | partial; KH_LOSSES + drop bookkeeping; **no throw velocity, no INFO_KEYHUNT_LOST, no SND_KH_DROP** |
| `PlayerUseKey`/`kh_Key_DropOne` | — | **NOT IMPLEMENTED** (no voluntary +use drop) |
| `Damage_Calculate` carrier/noncarrier mult | — | **NOT IMPLEMENTED** (no damage/force scaling hook) |
| `kh_HandleFrags` carrier frag | `KeyHunt.OnDeath` | partial; carrier-frag bonus + KH_KCKILLS; **no team-kill penalty, no `-1` offset** |
| auto-return timeout | `KeyHunt.AutoReturnKey` | partial; uses delay_return (DEFAULT WRONG 15 vs 60); just resets round (no loser logic) |
| `return_when_unreachable`/`delay_damage_return`/`kh_Key_Damage` | — | **NOT IMPLEMENTED** (no lava/trigger destroy) |
| `kh_ScoreRules` | `KeyHunt.DeclareScoreRules` | live; all 7 columns + sort keys |
| `STAT(OBJECTIVE_STATUS)` pack | `KeyHunt.PackKeyState` + `GametypeStatusBlock` | live; per-recipient 31 slot; index+1 wire deviation (intended) |
| HUD `HUD_Mod_KH` | `ModIconsPanel.DrawKeyhunt` | live; layout + blink + skin art faithful |
| waypoint sprites (dropped key + carrier) | — | **NOT IMPLEMENTED** (no WP_KeyDropped / WP_KeyCarrier*) |
| `havocbot_role_kh_*` | `BotObjectiveRoles.RoleKeyHunt` | **DEAD** (searches classname `keyhunt_key`, spawned is `item_kh_key`; reads `Owner` never set) |
| `g_keyhunt_team_spawns` | `KeyHunt.RequestsTeamSpawns` | live |
| point/lead limit | `KeyHunt.PointLimit`/`LeadLimit`/`UpdateLeaderAndCheckLimit` | live |

## Parity assessment

- **logic** — Core round flow (wait-for-players → countdown → spawn → capture-geometry → reset) is faithful and
  live. But several Base behaviors are absent: the loser/destroy path (`kh_LoserTeam` push & destroy score split),
  voluntary +use drop (`kh_Key_DropOne`), the lava/trigger auto-destroy (`return_when_unreachable`/`kh_Key_Damage`),
  the carrier/noncarrier damage+force mutator, the team-kill carrier penalty, and the `score_carrierfrag-1` offset.
  There is also a dead second capture path (`AssignKey`→`CheckCapture`, no maxdist) with no live caller.

- **values** — Multiple DEFAULTS diverge from the stock cfg: `delay_return` 15 vs **60**, `maxdist` 4000 vs **150**,
  `score_collect` 1 vs **3**, `score_carrierfrag` 1 vs **2**. The maxdist default is the most consequential — at 4000
  a team capturing while spread across the map would still score, vs Base's tight 150-unit huddle requirement. The
  key bbox (-10/3) matches the legacy 0.8.6 box, not the current Base const (-25/4). Score-push/destroyed/
  ownfactor/throwvelocity/dropvelocity/protecttime/delay_tracking/delay_damage_return/damageforcescale are unread.
  Capture score: the port credits `(teams-1)*score_capture` to the winning team's ST_SCORE — and this TEAM total
  is faithful in both magnitude and mechanism (Base's per-key DistributeEvenly chunks each go to the owner's
  TEAM ST_SCORE via `GameRules_scoring_add_team_float2int` and sum to the same total). What the port omits is the
  per-carrier *individual* SP_SCORE that the same Base call also credits, plus the per-carrier `nades_GiveBonus` —
  a logic gap, not a value gap.

- **timing** — Controller cadence 0.05s key-think + per-frame Tick is faithful. `delay_round` countdown is honored.
  The **2.5 s alarm cadence is absent** (no siren). Tracking delay (`delay_tracking`) is unimplemented (no
  waypoints anyway).

- **presentation** — Key model + team colormap + fullbright glow + per-team netname are live and rendered via the
  entity stream; the HUD modicon panel is faithful. MISSING: the carried-key orbit around the carrier (port uses a
  fixed (0,0,20) offset, no XYSPEED circle), waypoint sprites (dropped key + carrier "Run here"/"Key Carrier"),
  the capture lightning/customflash/tarexplosion effects, and every center-print/info notification
  (START/SCAN/ROUNDSTART/INTERFERE/MEET/HELP/CAPTURE/PICKUP/DROP/LOST/PUSHED/DESTROYED) — the strings are
  registered in `NotificationsList.cs` but `KeyHunt.cs` never emits them.

- **audio** — All five KH samples (KH_ALARM/CAPTURE/COLLECT/DESTROY/DROP) are registered in `SoundsList.cs` but
  `KeyHunt.cs` plays **none** of them. No alarm, no capture jingle, no collect/drop/destroy cue.

- **liveness** — The mode is selectable and the core capture loop is live. Dead/ineffective: the `AssignKey`/
  `CheckCapture` instant-capture path (no caller), the bot role (classname + owner mismatch), and all the
  not-implemented features above.

### Intended divergences
- `PackKeyState` writes a 1-based team INDEX (not the SVQC NUM_TEAM code) in each slot; the port's panel decode
  expects index+1, so the rendered icons are identical — one index convention on the wire. (Flagged in code.)

## Verification
- Read all six Base keyhunt files + `gametypes-server.cfg` (cvar defaults). Read `KeyHunt.cs`, `EntityGametypeState.cs`,
  `GametypeStatusBlock.cs`, `ModIconsPanel.DrawKeyhunt`, `BotObjectiveRoles.RoleKeyHunt`, and the GameWorld wiring.
- Liveness traced: `GameWorld.ActivateGameType` (case KeyHunt) → `kh.Activate()/SetRoster/EnableRounds`;
  `GameWorld` per-frame → `kh.Tick()`; win-detect + status block both switch on KeyHunt. Confirmed live.
- Grep confirmed `KeyHunt.cs` contains zero notification/sound/waypoint/effect emissions (Ctf.cs has 20).
- Grep confirmed spawn classname `item_kh_key` vs bot-role search `keyhunt_key` (mismatch) and that `key.Owner`
  is never set (carrier is `GtCarrier`).
- Existing tests (`KeyHuntVisualTests.cs`) cover only the spawn-visual (model/color/glow/spin), not gameplay.
- Value diffs cross-checked against `gametypes-server.cfg` lines 459–483.

## Open questions
- Does the carried key visibly orbit in-game, or sit at the fixed (0,0,20) offset? (Code says fixed — needs a
  visual check, but it clearly omits the QC `KH_KEY_XYSPEED` circle math.)
- Are the KH center-print/info notifications surfaced through any OTHER path (e.g. a generic round-win notify in
  RoundHandler) rather than KeyHunt.cs? Grep suggests not, but a runtime check would confirm the player sees
  nothing on capture/drop/collect.
- Is the `maxdist` default of 4000 deliberate (to make captures easier in the port) or an oversight? No rationale
  in code — treated as an unintended value gap.

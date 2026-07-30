# Camp Check mutator — parity spec

**Base refs:** `common/mutators/mutator/campcheck/sv_campcheck.qc`, `common/mutators/mutator/campcheck/campcheck.qc`, `mutators.cfg` (`g_campcheck*`), `common/notifications/all.inc:574`, `common/deathtypes/all.inc:4`
**Port refs:** `src/VortexArena.Common/Gameplay/Mutators/CampcheckMutator.cs`, `src/VortexArena.Common/Gameplay/Mutators/EntityMutatorState.cs`, `src/VortexArena.Common/Gameplay/Notifications/NotificationsList.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
Camp Check is a server-side anti-camping mutator. While enabled (`g_campcheck` string cvar, default `0` = off),
every `g_campcheck_interval` seconds it measures how far each live player moved (in 2D only, so bunny-hopping in
place does not count) and, if that distance is below `g_campcheck_distance`, sends the player a "Don't camp!"
centerprint and deals `g_campcheck_damage` self-damage (capped so it cannot one-shot from full health/armor).
Taking part in a fight (dealing or receiving damage) instantly credits the combatants with a full distance so
they are never punished while actively fighting. It applies only to real clients (bots are exempt because they may
"camp" due to missing waypoints). The kill, if it happens, is the `DEATH_CAMP` self-deathtype.

## Base algorithm (authoritative)

### Mutator enable + cvars  (`campcheck.qc:9`)
- `REGISTER_MUTATOR(campcheck, expr_evaluate(autocvar_g_campcheck))` — enabled when the **string** cvar
  `g_campcheck` evaluates truthy (`expr_evaluate`: "", "0", "false" → off).
- Cvars (defaults from `mutators.cfg:328-332`):
  - `g_campcheck` = `0` (string; "damages campers every few seconds")
  - `g_campcheck_interval` = `10` (seconds)
  - `g_campcheck_damage` = `100` (hp)
  - `g_campcheck_distance` = `1800` (Quake units, 2D)
  - `g_campcheck_typecheck` = `0` (bool; when 1, also damage players who are typing in chat)
- Per-entity fields: `.float campcheck_nextcheck`, `.float campcheck_traveled_distance`, `.vector campcheck_prevorigin`.

### Per-frame camp check  (`sv_campcheck.qc:35` MUTATOR_HOOKFUNCTION campcheck PlayerPreThink) — authority
Runs each frame per player. Guard chain (ALL must pass to "check"):
1. `autocvar_g_campcheck_interval` non-zero
2. `!game_stopped && !warmup_stage && time >= game_starttime`
3. `IS_PLAYER(player) && !IS_DEAD(player) && !STAT(FROZEN, player)`
4. `autocvar_g_campcheck_typecheck || !PHYS_INPUT_BUTTON_CHAT(player)` — exempt while typing unless typecheck on
5. `IS_REAL_CLIENT(player)` — **bots are exempt** (they may "camp" from missing waypoints)
6. `!weaponLocked(player)`

When the guard passes:
- `dist = vec2(campcheck_prevorigin - origin)` (Z dropped); `campcheck_traveled_distance += fabs(vlen(dist))`.
- **Pre-match / round reset:** if `(g_campaign && !campaign_bots_may_start) || time < game_starttime ||
  (round_handler_IsActive() && !round_handler_IsRoundStarted())` → `nextcheck = time + interval*2`,
  `traveled_distance = 0` (do not punish before the round is live).
- **The check:** if `time > campcheck_nextcheck`:
  - if `traveled_distance < g_campcheck_distance`:
    - `Send_Notification(NOTIF_ONE, player, MSG_CENTER, CENTER_CAMPCHECK)` → centerprint "^F2Don't camp!"
    - if `player.vehicle`: `Damage(player.vehicle, NULL, NULL, g_campcheck_damage * 2, DEATH_CAMP, DMG_NOWEP, vehicle.origin, '0 0 0')`
    - else: `max_dmg = health + armor * g_balance_armor_blockpercent + 5`;
      `Damage(player, NULL, NULL, bound(0, g_campcheck_damage, max_dmg), DEATH_CAMP, DMG_NOWEP, origin, '0 0 0')`
      (the cap means a full-HP target survives the first hit; repeated camping eventually kills).
  - `nextcheck = time + interval`; `traveled_distance = 0`.
- mark `checked = true`.

If the guard chain fails (`!checked`): `nextcheck = time + interval` (keep the timer fresh so the player isn't
instantly punished the moment they become eligible). Always: `campcheck_prevorigin = origin`.

`g_balance_armor_blockpercent` default (balance-xonotic.cfg) = `0.7`.

### Fight resets distance  (`sv_campcheck.qc:23` Damage_Calculate) — shared/authority
On any damage event where `frag_attacker != frag_target && IS_PLAYER(both)`, set BOTH
`frag_target.campcheck_traveled_distance` and `frag_attacker.campcheck_traveled_distance` to
`g_campcheck_distance` — i.e. fighters are credited a full interval of movement and never punished mid-fight.

### Death clears the centerprint  (`sv_campcheck.qc:16` PlayerDies) — authority→presentation
`Kill_Notification(NOTIF_ONE, frag_target, MSG_CENTER, CPID_CAMPCHECK)` — removes the lingering "Don't camp!"
centerprint from the victim's screen when they die.

### Spawn init  (`sv_campcheck.qc:91` PlayerSpawn) — authority
`campcheck_nextcheck = time + interval*2`; `campcheck_traveled_distance = 0` (grace period after (re)spawn).

### Corpse clone copies origin  (`sv_campcheck.qc:83` CopyBody) — authority
`clone.campcheck_prevorigin = player.campcheck_prevorigin` — when the engine makes a corpse clone, copy the field
so the reused player edict's next 2D-delta is not a huge jump.

### Server-info advertise  (`sv_campcheck.qc:99` BuildMutatorsString) — authority
Appends `":CampCheck"` to the active-mutators string reported in server info / scoreboard.

### Deathtype  (`common/deathtypes/all.inc:4`)
`REGISTER_DEATHTYPE(CAMP, DEATH_SELF_CAMP, NULL, NULL, NULL, "")` — `DEATH_CAMP` is a SELF death (no killer),
notification key `DEATH_SELF_CAMP`, empty murder/HUD string (no "X killed Y" — it's a self-death message).

## Port mapping

| Base feature | Port symbol | Status |
|---|---|---|
| `REGISTER_MUTATOR(expr_evaluate(g_campcheck))` | `CampcheckMutator.IsEnabled` (`ExprEvaluate(GetString("g_campcheck"))`) + `[Mutator]` auto-discovery into `Mutators.All`, activated by `MutatorActivation.Apply()` at `GameWorld.cs:511` | live, faithful |
| cvars `g_campcheck_{damage,distance,interval}` | read in `Hook()` (`GetFloat`) into `Damage/Distance/Interval` | faithful |
| `.campcheck_{nextcheck,traveled_distance,prevorigin}` | `Entity.Campcheck{NextCheck,TraveledDistance,PrevOrigin}` (EntityMutatorState.cs) | faithful |
| PlayerPreThink camp check | `OnPlayerPreThink` (hook fired GameWorld.cs:988) | live, partial — see gaps |
| 2D distance accumulation | `delta.Z = 0; CampcheckTraveledDistance += abs(delta.Length())` | faithful |
| interval/distance/damage cap math | `QMath.Bound(0, Damage, health + armor*blockpercent + 5)` | faithful |
| vehicle double damage | `Combat.Damage(Vehicle, …, Damage*2, "camp", …)` | logic faithful; liveness unknown (vehicle path) |
| Damage_Calculate fight reset | `OnDamageCalculate` (hook fired DamageSystem.cs:219) | live, faithful |
| PlayerSpawn init | `OnPlayerSpawn` (hook fired ClientManager.cs:542) | live, faithful |
| CENTER_CAMPCHECK centerprint | `NotificationSystem.Send(..., MsgType.Center, "CAMPCHECK")`; `NotificationsList.cs:792` "^F2Don't camp!" | faithful |
| DEATH_CAMP deathtype string | `Combat.Damage(..., "camp", ...)` | faithful (string only) |
| DEATH_SELF_CAMP obituary ("Die camper!" / "thought they found a nice camping ground") | notif EXISTS (NotificationsList.cs:157/443/1053) but `"camp"` is **NOT registered in DeathTypes**, so `SelectSpecial("camp", false)` (Scores.cs:566) returns `DEATH_SELF_GENERIC` → camp obituary never shown | **broken / dead** |
| `typecheck` / `!PHYS_INPUT_BUTTON_CHAT` gate | **NOT IMPLEMENTED** (no chat-button concept in headless sim) | missing |
| `!warmup_stage && time >= game_starttime` gate | collapsed to `!VehicleCommon.GameStopped` | partial |
| pre-match / `round_handler` reset block | **NOT IMPLEMENTED** | missing |
| `IS_REAL_CLIENT` (bot exemption) | **NOT IMPLEMENTED** — `IsPlayer()` checks only `EntFlags.Client`, does not exclude `Player.IsBot` | missing |
| `weaponLocked` gate | **NOT IMPLEMENTED** | missing |
| PlayerDies centerprint clear | `OnPlayerDies` returns false, **sends nothing** (no-op stub) | stub |
| CopyBody clone origin copy | **NOT IMPLEMENTED** (no CopyBody hook in MutatorHooks) | missing |
| BuildMutatorsString ":CampCheck" | **NOT IMPLEMENTED** (no BuildMutatorsString hook) | missing |

## Parity assessment

**Liveness — LIVE.** The mutator is auto-registered via `[Mutator]`, and `MutatorActivation.Apply()` (GameWorld.cs:511,
on the live server boot path) subscribes `Hook()` whenever `g_campcheck` is truthy. All four subscribed hooks fire on
the real match path: PlayerPreThink (GameWorld.cs:988), DamageCalculate (DamageSystem.cs:219), PlayerDies
(DamageSystem.cs:552), PlayerSpawn (ClientManager.cs:542). Default `g_campcheck 0` keeps it off exactly like Base.

**Core logic & values — faithful.** The 2D distance accumulation, the interval cadence, the `bound(0, damage,
health+armor*blockpercent+5)` non-one-shot cap, the vehicle `*2` damage, the fight-reset (both combatants set to full
distance), the spawn grace (`interval*2`), the centerprint text, and the DEATH_CAMP self-death all match Base, with the
correct defaults (interval 10, damage 100, distance 1800, blockpercent 0.7).

**Gaps (observable):**
- **Camp-kill obituary is wrong (generic instead of camp-specific).** Base `REGISTER_DEATHTYPE(CAMP, DEATH_SELF_CAMP, …)`
  makes a campcheck kill print the camp-specific obituary (kill feed "^BG%s^K1 thought they found a nice camping
  ground", self-center "^K1Die camper!"). The port deals damage with deathtype string `"camp"`, but `"camp"` is **not
  registered** in `DeathTypes.cs` (no `Camp` constant, no `_registry` row, no `SelectSpecial` switch case). On the kill,
  `EmitObituary` (`Scores.cs:565-566`) sees `IsWeapon("camp")==false` and calls `SelectSpecial("camp", murder:false)`,
  which hits the default branch → `DEATH_SELF_GENERIC`. The `DEATH_SELF_CAMP` notification entries exist
  (`NotificationsList.cs:157/443/1053`) but are orphaned — never selected. Fix: register a `camp` `DeathTypeDef` with
  `self="DEATH_SELF_CAMP"`. (Missed entirely by the first-pass draft.)
- **Bots are punished (should be exempt).** Base gates on `IS_REAL_CLIENT`; the port's `IsPlayer()` only checks
  `EntFlags.Client` and never consults `Player.IsBot` (which exists and is used elsewhere, e.g.
  KickTeamkillerMutator). On a listen server with bots and `g_campcheck` on, camping bots get "Don't camp!" + camp
  damage/death — directly contradicting Base and the mutator's own docstring claim that it mirrors `IS_REAL_CLIENT`.
- **Typing players are punished.** Base exempts players in chat (unless `g_campcheck_typecheck 1`). The port has no
  chat-button state and never applies this gate, so a player typing a long message can be camp-damaged/killed.
  `g_campcheck_typecheck` is read by neither the cvar inventory nor the mutator (effectively a dead cvar).
- **Death does not clear the "Don't camp!" centerprint.** `OnPlayerDies` is a no-op (returns false, sends no
  notification). Base does `Kill_Notification(CPID_CAMPCHECK)`; in the port the centerprint persists by its own
  timeout after the victim dies.
- **No pre-match / round-start reset.** Base zeroes the timer+distance during campaign-bot-wait, before
  `game_starttime`, and while a round hasn't started; the port relies solely on `GameStopped`. A player who stood
  still through the warmup→round transition could be camp-checked on the first interval after the round starts
  instead of getting the fresh `interval*2` grace.
- **No `weaponLocked` gate.** When a player's weapon is locked (e.g. certain mutator/round states), Base skips the
  check; the port does not.
- **Corpse-clone origin not copied (CopyBody).** Not ported — the player edict is reused on respawn in the port
  (SpawnSystem.cs:518) and PlayerSpawn re-inits the fields, so the practical impact is limited, but the explicit
  CopyBody parity step is absent.
- **`:CampCheck` not advertised in server info (BuildMutatorsString).** The active-mutators string the port builds
  does not include CampCheck.

**Intended divergence:** The collapse of `!warmup_stage && time >= game_starttime` into `!GameStopped` is a
sim-wide convention shared by sibling PlayerPreThink mutators (TouchExplode, Bloodloss) — the headless sim's only
match-live signal is `GameStopped`. Marked as the documented simplification it is, but the *missing pre-match
reset block* (item above) is a distinct gap, not covered by this convention.

## Verification
- **Liveness:** traced statically — `[Mutator]` discovery → `Mutators.All` → `MutatorActivation.Apply()` (GameWorld.cs:511)
  → `Hook()` subscribes; the four hook `.Call`/`.Invoke` sites confirmed on the live server/damage paths. Not run in-game.
- **Values:** diffed against `mutators.cfg:328-332` and `balance-xonotic.cfg:178`. Match.
- **Logic:** line-by-line diff of `OnPlayerPreThink`/`OnDamageCalculate`/`OnPlayerSpawn` vs `sv_campcheck.qc`.
- **Gaps (bot/typecheck/PlayerDies/reset/weaponLocked/CopyBody/BuildMutatorsString):** confirmed absent by grep —
  `Player.IsBot` is never referenced in CampcheckMutator.cs; no `CopyBody`/`BuildMutatorsString` hook exists in
  MutatorHooks.cs; `OnPlayerDies` body returns false with no send.
- No unit test exercising campcheck was found.

## Open questions
- ~~Does any host path set `Player.IsBot` for listen-server bots such that the bot-exemption gap is reachable?~~
  **RESOLVED:** bots are assigned `EntFlags.Client` (`ClientManager.cs:164/392`) and `Player.IsBot=true`
  (`ClientManager.cs:53`), and are driven through `OnClientMove` → `PlayerPreThink`. The bot-camp gap is reachable
  in a real match — `IsPlayer()` returns true for bots.
- Is the vehicle branch (`Vehicle is not null`, `Damage*2`) ever reachable? Vehicles' overall liveness in the port
  is itself unverified, so this branch is `unknown` for liveness even though its logic is faithful.
- Should `g_campcheck_typecheck` be wired to anything, given the port lacks a chat-typing input state?

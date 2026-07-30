# Map-object movers: platforms (`func_plat`) — parity spec

**Base refs:** `common/mapobjects/platforms.qc`, `common/mapobjects/func/plat.qc`, `common/mapobjects/subs.qc` (SUB_CalcMove/SUB_CalcAngleMove), `lib/math.qh` (cubic_speedfunc)
**Port refs:** `src/VortexArena.Common/Gameplay/MapObjects/Platforms.cs`, `src/VortexArena.Common/Gameplay/MapObjects/MapObjectsCommon.cs` (MapMover), `src/VortexArena.Engine/Simulation/FlyMove.cs` (PhysicsPusher/PushMove)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
A `func_plat` is a brush entity (`MOVETYPE_PUSH`) that rides vertically between a raised position
(`pos1` = spawn origin) and a lowered position (`pos2` = pos1 − height on Z). The common case starts
LOWERED (`STATE_BOTTOM`); a live creature stepping onto a spawned center-trigger raises it
(`plat_go_up`); at the top it dwells 3s then lowers (`plat_go_down`). A blocker is crushed (CRUSH
spawnflag → 10000 dmg) or bitten for `.dmg`, after which the plat reverses. A *targeted* plat starts
RAISED (`STATE_UP`) and is sent down by a trigger firing its `.use`.

The motion is produced by `SUB_CalcMove` (subs.qc), the shared mover driver: it sets `.velocity`
toward the destination and schedules a think on the entity's local clock `.ltime`. The engine's
`MOVETYPE_PUSH` pusher integrator (DP `SV_Physics_Pusher`/`SV_PushMove`) does the actual sweep, rider
carrying, and blocked detection. Plats use `TSPEED_LINEAR` and the default linear `platmovetype`, so
they take `SUB_CalcMove`'s linear branch (not the bezier easing controller).

This unit is **server-authoritative**: although Base declares `REGISTER_NET_LINKED(ENT_CLIENT_PLAT)`
and ships a `plat_send`/`NET_HANDLE`, the actual link call `plat_link` is commented out
(`//Net_LinkEntity(...)`). So in Base the plat is NOT CSQC-predicted — it is simulated on the server
and its position is sent as a normal networked brush entity. The port mirrors this (server-only sim).

## Base algorithm (authoritative)

### spawnfunc(func_plat)  (`func/plat.qc:spawnfunc(func_plat)`)
- **Trigger / entry:** map-load, when the BSP entity lump reads `classname "func_plat"` (SVQC side).
- **Algorithm:**
  1. q3compat: `spawnflags = 0`; if no `.dmg`, `.dmg = 2`. Else if `spawnflags & CRUSH`: `.dmg = 10000`.
  2. If `.dmg` and no `.message`: `.message = "was squished"`. If `.dmg` and no `.message2`: `.message2 = "was squished by"`.
  3. Sound selection: `sounds==1` → `plats/plat1.wav` / `plats/plat2.wav`; `sounds==2` or q3compat → `plats/medplat1.wav` / `plats/medplat2.wav`. Back-compat: `.sound1`→noise, `.sound2`→noise1. q3compat reads CPMA `sound_start`/`sound_end` spawn keys, else probes Q3 default `sound/movers/plats/pt1_strt.wav` / `pt1_end.wav` in the map pack. Precache noise/noise1.
  4. `mangle = angles; angles = '0 0 0'` (plats don't rotate via `.angles`). `classname = "plat"`. `draggable = drag_undraggable`.
  5. `InitMovingBrushTrigger(this)` → SOLID_BSP, MOVETYPE_PUSH, set brush model bounds; returns false (abort) if no brushes.
  6. `effects |= EF_LOWPRECISION`. `setsize(mins,maxs)`. `setblocked(this, plat_crush)`.
  7. Defaults: `if (!speed) speed = q3compat ? 200 : 150`. `if (!lip) lip = q3compat ? 8 : 16`. `if (!height) height = size.z - lip`.
  8. `pos1 = origin; pos2 = origin; pos2_z = origin.z - height`.
  9. `reset = plat_reset; reset(this)`.
  10. `InitializeEntity(this, plat_delayedinit, INITPRIO_FINDTARGET)` → after FINDTARGET prio, `plat_link` (dead) then `plat_spawn_inside_trigger` (unless q3compat with a targetname).
- **Constants:** `speed` default 150 (q3 200), `lip` default 16 (q3 8), `height` default `size.z - lip`, CRUSH dmg 10000, q3 default dmg 2, top dwell 3s, top-refresh 1s.

### plat_spawn_inside_trigger  (`platforms.qc:plat_spawn_inside_trigger`)
- Spawns a SOLID_TRIGGER child whose `.touch = plat_center_touch`, MOVETYPE_NONE, `.enemy = plat`.
- Volume: `tmin = absmin + '25 25 0'`, `tmax = absmax - '25 25 -8'`, then `tmin_z = tmax_z - (pos1_z - pos2_z + 8)`. If `PLAT_LOW_TRIGGER` (BIT 0): `tmax_z = tmin_z + 8`. If `size_x <= 50`: collapse X to a 1-unit slab at the bbox center; same for `size_y <= 50`. `objerror` (delete trigger) if the resulting box is degenerate.

### State machine  (`platforms.qc`)
- `plat_go_up`: play `noise`, `state = STATE_UP`, `SUB_CalcMove(pos1, TSPEED_LINEAR, speed, plat_hit_top)`.
- `plat_go_down`: play `noise`, `state = STATE_DOWN`, `SUB_CalcMove(pos2, TSPEED_LINEAR, speed, plat_hit_bottom)`.
- `plat_hit_top`: play `noise1`, `state = STATE_TOP`, set think `plat_go_down`, `nextthink = ltime + 3` (3s dwell).
- `plat_hit_bottom`: play `noise1`, `state = STATE_BOTTOM` (rests; trigger re-raises).
- `plat_center_touch(toucher)`: SVQC requires `iscreature` AND health > 0 (CSQC requires IS_PLAYER and !IS_DEAD). If plat `state==STATE_BOTTOM` → `plat_go_up`; if `state==STATE_TOP` → `nextthink = ltime + 1` (refresh dwell).
- `plat_outside_touch(toucher)`: same creature/health guard; if plat `state==STATE_TOP` → `plat_go_down`. (Not spawned by stock func_plat; available for mapper-wired outside triggers.)
- `plat_trigger_use`: if the plat already has a think (already moving) return; else `plat_go_down`.
- `plat_target_use`: if `state==STATE_TOP` → refresh `nextthink = ltime + 1`; else if `state != STATE_UP` → `plat_go_up`. (Used by Q3COMPAT targeted plats.)
- `plat_use(actor)`: clears `.use`, `objerror` if `state != STATE_UP`, else `plat_go_down`. (Targeted "start raised" send-down.)

### plat_crush  (`platforms.qc:plat_crush`)
- If `(spawnflags & CRUSH)` and `blocker.takedamage != DAMAGE_NO`: `Damage(blocker, 10000, DEATH_HURTTRIGGER)`.
- Else: if `.dmg` and damageable, `Damage(blocker, .dmg, DEATH_HURTTRIGGER)`, and if `IS_DEAD(blocker)` gib it (10000). Then reverse: `STATE_UP → plat_go_down`; `STATE_DOWN → plat_go_up`; other states are a no-op (delayed-event tolerance).
- **NOTE:** `plat_crush` is the plat's `setblocked` handler — NOT `generic_plat_blocked`. `plat_crush` has **no** `dmgtime`/`dmgtime2` bite-rate-limit (that cooldown lives in `generic_plat_blocked`, used by trains/doors). The port's `PlatCrush` faithfully omits the cooldown.

### Sound channel
- Every plat `_sound` call uses `CH_TRIGGER_SINGLE` (= **3** in `sounds/sound.qh`). The port plays on `SoundChannel.Voice` (= **2**), which corresponds to Base's *commented-out* `CH_VOICE_SINGLE`, not `CH_TRIGGER_SINGLE`. The cue (noise/noise1) is correct; the channel number diverges.

### plat_reset  (`platforms.qc:plat_reset`)
- Targeted (`targetname != ""`) and NOT Q3COMPAT: `setorigin(pos1)`, `state = STATE_UP`, `use = plat_use`.
- Else: `setorigin(pos2)`, `state = STATE_BOTTOM`, `use = (targetname != "" && Q3COMPAT) ? plat_target_use : plat_trigger_use`.
- SVQC: `SendFlags |= SF_TRIGGER_RESET`.

### SUB_CalcMove  (`subs.qc:SUB_CalcMove`)  [shared driver, used by all movers]
- `think1 = func; finaldest = tdest; setthink(SUB_CalcMoveDone)`.
- If `tdest == origin`: stop, `nextthink = ltime + 0.1`, return.
- `delta = tdest - origin`. traveltime = (LINEAR/START/END) `vlen(delta)/tspeed`, (TIME) `tspeed`. Clamp `traveltime <= 0 → 0.001` (Q3 InitMover fallback).
- If `traveltime < 0.15` OR (`platmovetype_start==1 && platmovetype_end==1`): LINEAR branch — `velocity = delta * (1/traveltime)`, `nextthink = ltime + traveltime`, return.
- Otherwise fall through to `SUB_CalcMove_Bezier` (midpoint control), spawning a `move_controller` sub-entity that re-samples a quadratic bezier with `cubic_speedfunc` easing each `PHYS_INPUT_FRAMETIME`, driving `.velocity` (and `.avelocity` if `platmovetype_turn`).
- `SUB_CalcMoveDone`: `setorigin(finaldest)`, `velocity = 0`, `nextthink = -1`, run `think1` (the arrival callback).
- **Note:** func_plat uses `platmovetype` default linear → ALWAYS takes the LINEAR branch. The bezier controller is exercised by func_train curves, not plats.

### cubic_speedfunc  (`math.qh`)
`((((s+e-2)*t - 2s - e + 3)*t + s)*t)` — the eased phase remap. `cubic_speedfunc_is_sane` rejects start/end factors that would reverse the platform.

### SUB_CalcAngleMove  (`subs.qc:SUB_CalcAngleMove`)
Linear angular counterpart: shortest-path wrap each axis, `avelocity = delta/traveltime`, snap on arrival (`SUB_CalcAngleMoveDone`). Not used by func_plat (plats don't rotate); used by rotating doors / turning trains.

### State / networking
- Entity fields: `pos1`, `pos2`, `state`, `finaldest`, `think1`, `speed`, `height`, `lip`, `dmg`, `mangle`, `noise`, `noise1`, `move_controller`.
- `REGISTER_NET_LINKED(ENT_CLIENT_PLAT)` + `plat_send`/`NET_HANDLE` exist BUT `plat_link` is commented out — **the plat is not CSQC-linked**; it is a server-authoritative networked brush. The CSQC `NET_HANDLE` path is effectively dead in stock Xonotic.

### Edge cases
- Top dwell refresh: stepping on a topped plat resets the 3s timer to 1s.
- Crush mid-transition in an unexpected state is a deliberate no-op.
- `plat_trigger_use` is one-shot-guarded by "already has a think".
- Targeted-vs-untargeted decides start position and which `.use` is installed.

## Port mapping
| Base | Port |
|---|---|
| `spawnfunc(func_plat)` | `Platforms.PlatSetup` (registered `MapObjectsRegistry:40`) |
| `plat_spawn_inside_trigger` | `Platforms.SpawnInsideTrigger` |
| `plat_go_up` / `plat_go_down` | `Platforms.PlatGoUp` / `PlatGoDown` |
| `plat_hit_top` / `plat_hit_bottom` | `Platforms.PlatHitTop` / `PlatHitBottom` |
| `plat_center_touch` | `Platforms.PlatCenterTouch` |
| `plat_trigger_use` | `Platforms.PlatTriggerUse` |
| `plat_use` | `Platforms.PlatUse` |
| `plat_crush` | `Platforms.PlatCrush` |
| `plat_reset` | `Platforms.PlatReset` |
| `plat_outside_touch` | NOT IMPLEMENTED |
| `plat_target_use` | NOT IMPLEMENTED |
| `SUB_CalcMove` (+ bezier/controller) | `MapMover.CalcMove` / `CalcMoveBezier` / `CalcMoveControllerThink` / `CalcMoveDone` |
| `SUB_CalcAngleMove` | `MapMover.CalcAngleMove` / `CalcAngleMoveDone` |
| `cubic_speedfunc` | `MapMover.CubicSpeedFunc` |
| `InitMovingBrushTrigger` | `MapMover.InitMovingBrushTrigger` |
| `generic_plat_blocked` | `MapMover.GenericPlatBlocked` |
| `SV_Physics_Pusher` / `SV_PushMove` (engine) | `PhysicsContext.PhysicsPusher` / `PushMove` (FlyMove.cs) |

**Liveness:** Live and server-authoritative. `SimulationLoop` (`SimulationLoop.cs:243`) runs
`MoveTypePhysics.RunEntity` over every entity each server tick; a `MOVETYPE_PUSH` plat is driven by
`PhysicsPusher` (advances `LTime`, fires the due think) + `PushMove` (carries riders standing on it
and those swept by it, reverts the move and invokes `.Blocked = PlatCrush` when a rider can't be
cleared). `func_plat` is in the spawnfunc table consumed by the BSP entity lump on map load
(`GameWorld.cs`). The center trigger's `PlatCenterTouch` fires via the trigger touch path.

## Parity assessment

### Faithful (high confidence)
- **State machine / control flow** — up/down/top/bottom transitions, 3s dwell, 1s top-refresh,
  trigger-use one-shot guard, targeted-start-raised reset, crush-reverse logic — all match Base.
  (Caveat: the go/hit sounds play on the wrong channel — see gap 10.)
- **Core constants** — non-q3 `speed=150`, `lip=16`, `height=size.z-lip`, CRUSH dmg 10000, dwell 3s,
  refresh 1s, center-trigger box geometry (`+25/-25`, `-8` lip, `pos1_z-pos2_z+8`, LOW_TRIGGER 8-unit,
  `size<=50` slab collapse) — all match.
- **SUB_CalcMove linear branch + timing** — `velocity = delta/traveltime`, `traveltime<0.15` short-cut,
  `<=0 → 0.001` clamp, `ltime`-scheduled think, exact-snap on arrival. Plats take the linear branch
  (default linear platmovetype), bit-for-bit with Base.
- **Crush / bite damage** — CRUSH=10000, `.dmg` bite + dead-gib, `DEATH_HURTTRIGGER` (= `DeathTypes.Void`).
- **Server-authoritative model** — matches Base (CSQC plat link is dead in Base too), so the port's
  lack of CSQC plat prediction is faithful, NOT a gap.

### Gaps
1. **q3compat plat defaults not ported** — Base gives Q3 plats `speed=200`, `lip=8`, `dmg=2` (when
   unset), medplat sounds, and CPMA `sound_start`/`sound_end` / `pt1_strt.wav` probing. The port has no
   q3compat branch in `PlatSetup`, so a Q3/Q3DF map's func_plat moves at the Xonotic default 150 (not
   200), uses an 8→16 lip, takes no default bite damage, and plays plat1/plat2 instead of medplat. Player-observable on imported Q3 maps (slower plat, different feel + sound, no squish damage).
2. **`sounds` field selection not ported** — Base maps `sounds 1`→plat1/plat2 and `sounds 2`→medplat;
   the port always defaults to plat1/plat2 and never reads `.sounds`. A map authored with `"sounds" "2"`
   plays the wrong (light) plat sound.
3. **`sound1`/`sound2` back-compat fields not ported** — Base honors these legacy per-entity sound
   overrides; the port ignores them, so a map using them gets default sounds.
4. **`message2` default not set** — Base's `spawnfunc(func_plat)` sets `message2 = "was squished by"` (when
   `.dmg` is set) for the obituary attacker phrasing; `PlatSetup` sets only `Message`. A map's explicit
   `"message2"` key still survives (parsed in `MapObjectFieldsExtra`), but the *default* is missing, so a
   default plat crush uses the wrong attacker wording. (This is a spawnfunc gap, not a `plat_crush` gap —
   `plat_crush` never sets `message2`.)
5. **`plat_target_use` missing** — Base installs `plat_target_use` as `.use` for a Q3COMPAT *targeted*
   non-raised plat (and on the CSQC path). The port always installs `PlatUse` for a targeted plat.
   `plat_target_use` re-raises a down/idle plat and refreshes a topped one; `PlatUse` is a one-shot
   send-down. On a Q3 map with a targeted ground plat, the trigger does the wrong thing.
6. **`plat_outside_touch` missing** — Base provides this handler (sends a topped plat back down when a
   creature touches an outside trigger). Stock `func_plat` doesn't spawn an outside trigger, so this is
   only reachable via mapper-wired entities; low practical impact but absent.
7. **`PlatUse` accepts STATE_TOP, Base accepts only STATE_UP** — `PlatUse` returns early unless state
   is `StateUp` OR `StateTop`; Base `plat_use` `objerror`s for any non-`STATE_UP` state (it only fires
   from a freshly-reset start-raised plat, which is in `STATE_UP`). Minor: in practice `PlatUse` is
   cleared after first use and only ever called from `STATE_UP`, so the extra `StateTop` acceptance is
   unreachable on the normal path — a latent logic divergence, not an observed one.
8. **`draggable = drag_undraggable` and `effects |= EF_LOWPRECISION` not set** — cosmetic/physics-mod
   metadata; no gameplay impact in the port (no drag mutator / lowprecision networking path).
9. **`set_platmovetype` / `cubic_speedfunc_is_sane` not ported** — the `"platmovetype"` spawn-key parser
   (tokenizes `start end [force]` into `platmovetype_start`/`_end`) and its reverse-curve sanity reject are
   absent. `PlatMoveStart`/`PlatMoveEnd` are hardcoded to `1` (linear) on every mover, so a map authoring a
   curved platmovetype is silently treated as linear (the bezier *machinery* is ported; only the key input +
   sanity check are missing). Dormant for stock func_plat (always linear); affects func_train curves most.
10. **Sound channel 2 vs 3** — plat sounds play on `SoundChannel.Voice` (=2) but Base uses
   `CH_TRIGGER_SINGLE` (=3). The sample is right; the channel number diverges (low audible impact).
11. **`plat_spawn_inside_trigger` degenerate-box guard not reproduced** — Base deletes the trigger +
   `objerror`s when the computed box is inverted (`tmin >= tmax` on any axis); the port unconditionally
   `SetSize`s the (possibly inverted) box. No impact for valid plats.

### Intended divergences
- **No CSQC plat prediction.** Faithful to Base (link is commented out there too); server-authoritative.
  Not flagged as a divergence row since it matches Base behavior.

## Verification
- **Code read** (high): `Platforms.cs`, `MapObjectsCommon.cs` (MapMover), `FlyMove.cs` PhysicsPusher/PushMove
  vs `platforms.qc`, `func/plat.qc`, `subs.qc`, `math.qh`. State machine, constants, CalcMove linear
  branch, crush logic verified line-by-line.
- **Liveness** (high): traced spawnfunc registration (`MapObjectsRegistry:40`) → BSP lump consumption
  (`GameWorld.cs`) → per-tick `SimulationLoop:243` `RunEntity` → `PhysicsPusher`/`PushMove`. Plat moves,
  carries riders, and crushes on the live server path.
- **No dedicated unit test** found for func_plat (searched `tests/` — plat appears only in BSP collision
  tests, not behavioral). The CalcMove/PushMove primitives are exercised by determinism tests.
- q3compat / `sounds` / `message2` gaps verified by absence in `PlatSetup` (no `q3compat`/`sounds`/
  `Message2`/`sound1` reads).

## Open questions
- Are any shipped Xonotic stock maps using `func_plat` with `"sounds" "2"` or the `sound1/sound2`
  legacy fields? If not, gaps 2–3 are dormant. (Needs a map-pack scan.)
- Runtime confirmation that a player riding the plat is carried smoothly (PushMove rider carry) and that
  standing under a closing CRUSH plat is gibbed — not yet verified in-game.

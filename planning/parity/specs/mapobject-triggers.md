# Map-object triggers — parity spec

**Base refs:** `common/mapobjects/trigger/*.qc` (multi, once, hurt, heal, gravity, impulse, jumppads, swamp, viewloc, secret, counter, relay, delay, monoflop, keylock, flipflop, multivibrator, disablerelay, relay_if, relay_teamcheck, relay_activators, gamestart, magicear), `common/mapobjects/target/music.qc`, `common/mapobjects/teleport.qc`, `common/mapobjects/triggers.qc` (SUB_UseTargets / isPushable / generic_setactive), `common/mapobjects/teleporters.qc` (Simple_TeleportPlayer)
**Port refs:** `src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs` (multi/once/hurt/heal/gravity/counter/relay/delay/secret/swamp/impulse/keylock), `LogicGates.cs` (monoflop/flipflop/multivibrator/disablerelay/relay_if/relay_teamcheck/relay_activators/gamestart/magicear), `Jumppads.cs`, `Teleporters.cs`, `ViewLocation.cs`, `TargetMusic.cs`, `MapObjectsCommon.cs` (UseTargets/isPushable), `MapObjectsRegistry.cs` (spawnfunc wiring); engine drivers `src/VortexArena.Engine/Simulation/TriggerTouch.cs` + `SimulationLoop.cs` (SV_TouchTriggers / SV_RunThink), `src/VortexArena.Common/Physics/PlayerPhysics.cs` (player touch pass); client `game/client/MusicPlayer.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

> ADVERSARIAL RE-AUDIT 2026-06-22: the first-pass draft documented only 14 of the 25 implemented trigger families. ADDED rows for `trigger_heal`/`target_heal`, `trigger_keylock`, `trigger_flipflop`, `trigger_multivibrator`, `trigger_disablerelay`, `trigger_relay_if`, `trigger_relay_teamcheck`, `relay_activate`/`_deactivate`/`_activatetoggle`, `trigger_gamestart`, and `trigger_magicear` (the last is ported but UNWIRED → liveness `dead`). Also corrected: counter/keylock notifications add a `misc/talk.wav` Base does NOT emit (audio divergence), and the swamp slowdown is confirmed dead by a Physics grep.

## Overview
The "trigger" family is the set of brush-volume and relay map entities that drive map logic: they fire
their `target`s (via `SUB_UseTargets`) on touch / on use / on a schedule, and some act directly on the
toucher (hurt, gravity, swamp, push, teleport). They are SOLID_TRIGGER (non-solid to the move sweep), so
the engine fires their `.touch` in a dedicated post-move area-grid pass (`SV_TouchTriggers`); the
self-thinking ones (swamp, viewloc, gravity-checker, monoflop/delay/counter timers) run via `SV_RunThink`.
Almost all logic is server-authoritative (`#ifdef SVQC`); the client mirrors only push/teleport (for
prediction), trigger_impulse, viewloc camera, and music playback.

## Base algorithm (authoritative)

### SUB_UseTargets — the firing primitive  (`triggers.qc:SUB_UseTargets_Ex`)
- **Entry:** every trigger's fire path calls this with `(this, actor, trigger)`.
- **Algorithm:** if `this.delay` > 0, spawn a `DelayedUse` think entity capturing message/killtarget/targets
  and fire after `delay`. Otherwise: (1) if actor is a real client and `this.message != ""`, centerprint it
  and — when `noise == ""` — `play2(actor, SND(TALK))`; (2) delete every entity named by `killtarget`;
  (3) for each of `target`,`target2`,`target3`,`target4` (skippable by bitmask), call `t.use(t, actor, this)`
  for every entity with that targetname (`target_random` instead picks exactly one by weighted reservoir).
- **Constants:** none numeric; `SND(TALK)` = `misc/talk.wav`.

### trigger_multiple / trigger_once  (`multi.qc`)
- **Trigger:** touch (creature, unless ALL_ENTITIES) or `use`, or — if `health` set — being shot to 0 HP
  (`multi_eventdamage`). `multi_touch` gates: ALL_ENTITIES else iscreature; `q3compat`+2teams red/blue
  spawnflag; `this.team` + INVERT_TEAMS team match; `movedir` (angle) facing check (`v_forward·movedir<0`);
  `pressedkeys` (player must hold those keys).
- **Algorithm (multi_trigger):** ONLY_PLAYERS gate; rearm window check (`nextthink > time` → return; CTS uses
  per-client `.wait` buffers keyed by client number); play `this.noise`; `takedamage=NO`;
  `SUB_UseTargets`; then schedule: `wait>0` → `multi_wait` after `wait`; `wait<-1` (xon) → rearm now;
  `wait==-1` → disable touch+use (fire once).
- **Constants:** `wait` default 0.2 (xon) / 0.5 (q3compat); sounds 1=`misc/secret.wav`,2=`misc/talk.wav`,
  3=`misc/trigger1.wav`. `multi_eventdamage`: NOSPLASH ignores HITTYPE_SPLASH non-special deaths; team-gates
  the attacker; fires when HP≤0.

### trigger_hurt  (`hurt.qc`)
- **Trigger:** touch by anything with `takedamage`, while `active==ACTIVE_ACTIVE`.
- **Algorithm:** team/INVERT gate; creatures throttled to one hit per `1s` (q3compat non-HURT_SLOW: `0.05s`);
  `Damage(toucher, this, owner, dmg, DEATH_HURTTRIGGER, …)` where owner = a player who `use`d it else the
  trigger itself; non-creatures with `damagedbytriggers` take `dmg` every touch.
- **Constants:** `dmg` default `10000` (q3compat `5`); messages `"was in the wrong place"` /
  `"was thrown into a world of hurt by"`; cooldown `1s` / `0.05s`.

### trigger_gravity  (`gravity.qc`)
- **Trigger:** touch while `active != ACTIVE_NOT`. Legacy `use` toggles active.
- **Algorithm:** set toucher gravity to `this.gravity` (×toucher.gravity for non-sticky). For non-STICKY
  zones, spawn a per-toucher `trigger_gravity_check` whose `think` decrements `.count` (reset to 2 each touch
  frame); when the toucher leaves, `.count` hits 0 → restore saved gravity. Higher-priority zones (`.cnt`)
  win. Sticky zones leave the new gravity forever. Plays `this.noise` on a gravity change.
- **Constants:** GRAVITY_STICKY=BIT(0), GRAVITY_START_DISABLED=BIT(1). No-op if `gravity==1`.

### trigger_impulse  (`impulse.qc`)
- **Trigger:** touch a pushable while active; per-toucher `lastpushtime` timestep (`pushdeltatime`, capped
  `0.15`, 0 on first touch).
- **Three modes:** radial (if `.radius`): push out from center, FALLOFF none/linear/inv;
  directional (if `.target`): push toward `target_position` at `str·dt`, or accelerate to `str` speed cap
  (SPEEDTARGET, accel ≤ `8·dt·str`); accel (otherwise): `velocity *= strength**dt` (ticrate-independent).
- **Constants:** radial default str `2000`, directional `950`, accel `0.9`; cvars
  `g_triggerimpulse_{radial,directional,accel}_multiplier`=1, `_accel_power`=1; MAX_PUSHDELTATIME 0.15,
  DIRECTIONAL_MAX_ACCEL_FACTOR 8.

### trigger_push / target_push / trigger_push_velocity  (`jumppads.qc`)
- **Trigger:** touch while active (+team/INVERT gate). `target_push_use` is the `use` variant.
- **Algorithm (trigger_push):** `trigger_push_calculatevelocity` solves the ballistic launch from `org` to
  the target midpoint, arc apex controlled by `.height` (sign = apex inside/outside; up/down-jump root
  selection). No target → `velocity = movedir` (`movedir·speed·10`). Then (SVQC): debounced jumppad sound +
  EFFECT_JUMPPAD flash (`pushltime`), `jumppadcount`/`jumppadsused` tracking, `centerprint(message)`,
  `animdecide_setaction(JUMP)`, and `SUB_UseTargets(this.enemy,…)` if the dest has targets. PUSH_ONCE pads
  delete themselves. `trigger_push_velocity` sets/adds player-directional XY (`speed`) + Z (`count`) with
  bidirectional/add/clamp spawnflags.
- **Constants:** `speed` default `1000`; sound `misc/jumppad.wav`; `pushltime` debounce `0.2s` (q3
  target_push `1.5s`); q3compat gravity correction `grav /= 750/800`; PUSH_STATIC pushes from pad center.
  Runs on CSQC too (client prediction).

### trigger_swamp  (`swamp.qc`)
- **Trigger:** self-think every frame; `FOREACH_ENTITY_RADIUS` over its bbox claims live players via
  `swampslug`, then damages each on the `swamp_interval`.
- **Slowdown:** the player physics reads `this.swampslug.swamp_slowdown` as a maxspeed multiplier
  (`player.qc:50 maxspd_mod`).
- **Constants:** `dmg` 5, `swamp_interval` 1, `swamp_slowdown` 0.5; `DEATH_SWAMP`.

### trigger_secret  (`secret.qc`)
- **Trigger:** player touch only. `++secrets_found`; `SUB_UseTargets`; disable touch (fires once).
  `++secrets_total` at spawn. `delay` forced 0. Default message `"You found a secret!"`, sound 1
  (`misc/secret.wav`).

### trigger_counter  (`counter.qc`)
- **Trigger:** `use`, while active. `++counter_cnt` (shared, or per-(counter,player) for COUNTER_PER_PLAYER);
  fire `SUB_UseTargets` when count reaches `.count` (default 2), or on every press if COUNTER_FIRE_AT_COUNT.
  Real-client notifications: CENTER_SEQUENCE_COMPLETED / _COUNTER / _COUNTER_FEWMORE (text + sound), unless
  SPAWNFLAG_NOMESSAGE. `respawntime` re-arms after the sequence completes.

### trigger_relay / target_relay / target_delay / trigger_delay  (`relay.qc`, `delay.qc`)
- **trigger_relay/target_relay:** `use` → `SUB_UseTargets` (a pure relay; cannot be touched).
- **target_delay:** relay with `wait` default 1 → `delay`.
- **trigger_delay:** `use` captures actor and fires `SUB_UseTargets` after `.wait` (default 1) via own think.

### trigger_monoflop  (`monoflop.qc`)
- **`use`:** fire ON (`SUB_UseTargets`) on the rising edge; (re)arm the off-timer to `time+wait`; MONOFLOP_FIXED
  sets the timer only once. **think:** clear state, fire OFF (`SUB_UseTargets(this,this.enemy,NULL)`).
  `wait` default 1.

### trigger_teleport / target_teleporter  (`teleport.qc`, `teleporters.qc`)
- **Trigger:** touch (trigger_teleport) or `use` (target_teleporter). `Teleport_Active` gates: teleportable,
  not a turret, not dead, team/INVERT, TELEPORT_OBSERVERS_ONLY. `Simple_TeleportPlayer`: resolve destination
  (cached `enemy` if exactly one, else weighted-random with telefrag-avoid priority), reproject out-velocity
  along the dest `mangle` forward (`v_forward · vlen(velocity)`), optional `.speed` cap and
  TELEPORT_MIN/MAXSPEED clamps (skipped by KEEP_SPEED), relocate to `dest.origin + (1 - mins.z - 24)·Z`,
  `RemoveGrapplingHooks`, fire SUB_UseTargets at both teleporter and destination. Teleport flash both ends.

### target_music / trigger_music  (`target/music.qc`)
- **Server:** records track/volume/fade params; `target_music.use` (re)sends the track to the activator for
  `lifetime` seconds (lifetime 0 → replaces the default slot); `trigger_music` is a brush whose presence sets
  the highest-priority track. **Client (TargetMusic_Advance):** priority trigger_music > target_music(lifetime)
  > cdtrack/default; per-source volume eases up/down by `frametime/fade_time` (in) and `frametime/fade_rate`
  (out). START_DISABLED + relay toggling.

### trigger_viewlocation / target_viewlocation_start|end  (`viewloc.qc`)
- **Server:** resolve start/end anchors at INITPRIO_FINDTARGET; per-frame think clears + re-stamps each inside
  player's `.viewloc` (NOT touch — can't "untouch" multiple clients). Anchors fold single-float `.angle` into
  `angles_y`. **Client:** the 2.5D side-scroller camera locks the view to the start→end line (CSQC only).

## Port mapping
| Base | Port |
|---|---|
| SUB_UseTargets_Ex | `MapObjectsCommon.UseTargetsEx` (faithful: delay/killtarget/message-talk/target_random) |
| multi/once | `Triggers.MultipleSetup/OnceSetup/MultiTrigger/MultiTouch/MultiUse/MultiWait` + Death hook `OnDeath` (shootable) — **team-gate + pressedkeys gate MISSING in MultiTouch** |
| hurt | `Triggers.HurtSetup/HurtTouch/HurtUse` |
| heal/target_heal | `Triggers.HealSetup/TargetHealSetup/HealTouch` (HEAL_SOUND_ALWAYS not honored) |
| gravity (+checker) | `Triggers.GravitySetup/GravityTouch/GravityCheckThink/GravityRemove` |
| impulse (3 modes) | `Triggers.ImpulseSetup/ImpulseTouch{Radial,Directional,Accel}` |
| push/velocity/target_push | `Jumppads.*` (CalculateVelocity = trigger_push_calculatevelocity; SolveQuadratic) |
| swamp | `Triggers.SwampSetup/SwampThink` — **slowdown DEAD (no physics reader), deathtype=Void not Swamp** |
| secret | `Triggers.SecretSetup/SecretTouch` (+ `MapObjectsState.Secrets{Found,Total}`) |
| counter | `Triggers.CounterSetup/CounterUse/CounterReset` (+ per-player store) — **notif text dropped + an invented talk.wav** |
| keylock | `Triggers.KeylockSetup/KeylockTouch/KeylockTrigger` (CENTER_DOOR_LOCKED text + unlock centerprint unported) |
| relay/target_relay/target_delay/trigger_delay | `Triggers.RelaySetup/TargetDelaySetup/DelaySetup/Delay*` |
| monoflop | `LogicGates.MonoflopSetup/MonoflopUse/MonoflopFixedUse/MonoflopThink/MonoflopReset` |
| flipflop | `LogicGates.FlipflopSetup/FlipflopUse` |
| multivibrator | `LogicGates.MultivibratorSetup/MultivibratorSend/MultivibratorToggle/MultivibratorReset` |
| disablerelay | `LogicGates.DisableRelaySetup/DisableRelayUse` |
| relay_if | `LogicGates.RelayIfSetup/RelayIfUse` (cvar compare) |
| relay_teamcheck | `LogicGates.RelayTeamCheckSetup/RelayTeamCheckUse` |
| relay_activate/_deactivate/_activatetoggle | `LogicGates.Relay*Setup/RelayActivatorsUse` (+ `GenericSetActive`) |
| gamestart | `LogicGates.GamestartSetup/GamestartUse/AdaptorThink2Use` (GameStartTime defaults 0) |
| magicear | `LogicGates.MagicEarSetup/MagicEarProcessMessage/MagicEarProcessAllEars` — **UNWIRED (dead): no say pipeline** |
| teleport src + target_teleporter | `Teleporters.TeleportSetup/TargetTeleporterSetup/Teleport*/SimpleTeleportPlayer` |
| target_music/trigger_music | `TargetMusic.*` (server state) + `game/client/MusicPlayer.cs` (playback) |
| viewloc | `ViewLocation.*` |

All 24 spawnfuncs are registered in `MapObjectsRegistry`. Touch is driven live for ALL movers by
`TriggerTouch.Run` (server post-move area-grid pass via `MoveTypePhysics.RunEntity` + the player physics
`TouchAreaGrid`). Thinks are driven by `SimulationLoop.RunThink` (SV_RunThink). Push & teleport additionally
run a CSQC-style client-prediction pass (`TriggerTouch.PredictJumppadsAmbient`/`PredictTeleportsAmbient`).

## Parity assessment

### Faithful / live
SUB_UseTargets (delay/killtarget/message/target_random), trigger_impulse (all 3 modes + cvar multipliers + the
ticrate-independent `strength**dt`), trigger_push ballistic solver + velocity pads + PUSH_ONCE + sound debounce,
gravity zones + exit-checker + `.cnt` priority, counter (shared + per-player + respawntime), relay/delay/
target_delay, monoflop (use/fixed/think), secret (count + fire-once), teleport (dest resolve + telefrag-avoid +
reproject + flash both ends), all spawnflag bit values, default constants (wait 0.2, impulse strengths, push
speed 1000, swamp dmg 5/interval 1, counter default 2, jumppad sound). Verified by code-read against QC.

### Gaps (observable)
1. **trigger_multiple team-gate + pressedkeys check MISSING** (`MultiTouch`): the port checks only
   ALL_ENTITIES + the facing (`movedir`) angle. A team-restricted `trigger_multiple` (`team`/INVERT_TEAMS)
   fires for the wrong team, and a `pressedkeys`-gated trigger fires without the key held. (logic: partial)
2. **trigger_swamp slowdown is DEAD:** `SwampThink` stamps `SwampSlug`/`SwampSlowdown` but NO code in the
   player physics reads it (Base `player.qc:50 maxspd_mod`). Players in a swamp take damage but move at full
   speed. (logic/values: partial, liveness of the slowdown half: dead)
3. **trigger_swamp obituary uses `DeathTypes.Void` instead of `DEATH_SWAMP`** (`DeathTypes.Swamp` exists and
   is unused): a swamp kill prints the void death message, not the swamp one. (values: partial)
4. **Counter / secret notifications are audible-stub only:** CENTER_SEQUENCE_COMPLETED/_COUNTER/_FEWMORE are
   reduced to a `misc/talk.wav` play with no center-print text; `secrets_found`/`secrets_total` are counted
   but never surfaced (no "N secrets found" HUD/notification). (presentation: partial)
5. **Jumppad presentation bookkeeping missing:** no `centerprint(this.message)` on a message-jumppad, no
   `animdecide_setaction(JUMP)` jump pose, no `jumppadcount`/`jumppadsused` tracking. The launch + sound +
   flash are faithful. (presentation: partial)
6. **q3compat per-trigger behaviors not applied:** hurt creature cooldown `0.05s` (non-HURT_SLOW) vs the
   hard-coded `1s`, hurt/multi q3 defaults (dmg 5, wait 0.5/forever), the jumppad `grav /= 750/800`
   correction, and q3 red/blue multi spawnflags. Stock (non-q3) maps are unaffected. (values: partial on q3)
7. **CTS client-specific `.wait` buffers (multi_trigger) MISSING:** in CTS/Race, each client has an
   independent re-trigger timer keyed by client number; the port uses the single shared `nextthink`. Affects
   CTS checkpoint/finish triggers. (logic: partial in CTS)
8. **trigger_hurt non-creature gate:** the port has no `DamagedByTriggers` field, so the non-creature branch
   damages any non-creature toucher unconditionally (Base gates on `toucher.damagedbytriggers`). Minor —
   most non-creature touchers that reach a trigger are projectiles. (logic: partial)
9. **trigger_viewlocation camera not locked:** server stamps `.viewloc` faithfully but there is no client
   2.5D camera consumer, so the side-scroller view region has no visible effect. (presentation: missing)
10. **target_music crossfade is approximated:** the client uses a two-player A/B crossfade keyed on the
    3-layer priority + lifetime model rather than QC's per-source `state` easing by `frametime/fade_time` /
    `frametime/fade_rate`; fade curves differ. Track selection priority is faithful. (presentation: partial)

### Intended divergences
- **trigger_hurt deathtype → `DeathTypes.Void`:** faithful, not a gap — QC `DEATH_HURTTRIGGER`'s notification
  IS the void/`DEATH_VOID` message ("was in the wrong place"); the port's DeathTypes comment documents this.
- **TELEPORT_MIN/MAXSPEED STAT clamps out of scope:** those stats default to 0 in stock physics, so omitting
  the clamp is a no-op on stock maps; KEEP_SPEED is therefore also a no-op for now.
- **CSQC networking / warpzone exact-trigger clip / bot waypoint trajectory probing** are deliberately not
  ported in this unit (client-net, rendering-only clip refinement, and the bot-nav layer respectively).

## Verification
- **Code-read** of every Base `.qc`/`.qh` in scope vs the port files (this audit). High confidence on logic/
  values for the implemented features; spawnflag bits and default constants diffed exactly.
- **Liveness traced:** touch via `TriggerTouch.Run` (server `MoveTypePhysics.RunEntity` + player
  `TouchAreaGrid`); thinks via `SimulationLoop.RunThink`; music via `game/client/MusicPlayer.cs` scan of
  `trigger_music`/`target_music`. Push/teleport client prediction via `TriggerTouch.Predict*Ambient`.
- **Not behaviorally tested in-game** (no runtime check this pass): swamp slowdown absence, multi team-gate,
  q3compat numbers, and the music fade curve are inferred from code only.

## Open questions
- Does any stock Xonotic map actually use a team-gated or `pressedkeys`-gated `trigger_multiple`? If not, gap
  #1 is latent rather than observed. (Needs a map-entity survey.)
- Is the swamp slowdown meant to be wired into the existing `maxspd_mod` chain in the port's PlayerPhysics, or
  was it deliberately deferred? (Owner input.)
- CTS `.wait` buffers (gap #7): confirm whether CTS/Race checkpoint triggers in the port rely on the shared
  `nextthink` being "forever" (`wait==-1`) which may mask the per-client divergence for finish lines.

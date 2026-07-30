# Wyvern (monster) — parity spec

**Base refs:** `common/monsters/monster/wyvern.qc` · `common/monsters/monster/wyvern.qh`
(framework: `common/monsters/sv_monsters.qc`, `monster.qh`, `monsters.cfg`)
**Port refs:** `src/VortexArena.Common/Gameplay/Monsters/Wyvern.cs` · `MonsterAI.cs` · `MonsterFramework.cs` · `MonsterSpawnFuncs.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Wyvern is one of the five stock Xonotic monsters. It is a fragile **flying** reptile
(`MONSTER_TYPE_FLY | MON_FLAG_RANGED | MON_FLAG_RIDE`) that glides after prey and, from range,
lobs a fast explosive **fireball** that does radius damage + knockback and then **ignites**
everything in the blast radius (burning DoT). It spawns from map placement (`monster_wyvern`),
from a `monster_spawner`, randomly via `spawnmonster "random"`, and as an Invasion wave monster.
Active only when `g_monsters` is enabled (default on) and on maps/modes that place monsters
(MonsterHunt-style maps, Invasion, the `monster_spawner` entity). It is NOT present in ordinary
DM/CTF unless a map places one.

## Base algorithm (authoritative)

### Identity / spawn flags / hitbox  (`wyvern.qh`)
- `spawnflags = MONSTER_TYPE_FLY | MON_FLAG_RANGED | MON_FLAG_RIDE` (BIT5 | BIT9 | BIT12).
- `m_mins = '-30 -30 -48'`, `m_maxs = '30 30 30'`. (Asymmetric — taller below origin.)
- `m_model = wyvern.dpm`; `netname = "wyvern"`; `m_name = "Wyvern"`.
- `MONSTER_TYPE_FLY` makes the monster movetype FLY with `FL_FLY`, no gravity, and (in
  sv_monsters Move) lets it chase/wander vertically.

### mr_setup  (`wyvern.qc:175 METHOD(Wyvern, mr_setup)`, SVQC/authority)
- If health unset: `SetResourceExplicit(RES_HEALTH, g_monster_wyvern_health=150)`.
- `speed = g_monster_wyvern_speed_walk (120)`, `speed2 = g_monster_wyvern_speed_run (250)`,
  `stopspeed = g_monster_wyvern_speed_stop (300)`, `damageforcescale = 0.6`.
- `monster_loot = g_monster_wyvern_loot ("cells")`.
- `monster_attackfunc = M_Wyvern_Attack`.

### Attack dispatch  (`wyvern.qc:101 M_Wyvern_Attack`, authority)
- Both `MONSTER_ATTACK_MELEE` and `MONSTER_ATTACK_RANGED` answer identically: queue a fireball.
- `Monster_Delay(actor, 0, 1, M_Wyvern_Attack_Fireball)` — a **1s wind-up** then fire.
- `setanim(actor, anim_shoot, false, true, true)`.
- `anim_finished = (animstate_endtime > time) ? animstate_endtime : time + 1.2`.
- `attack_finished_single[0] = anim_finished + 0.2` (so ≈1.4s total lock).

### Fireball spawn  (`wyvern.qc:92 M_Wyvern_Attack_Fireball` → `wr_think`, authority)
- `M_Wyvern_Attack_Fireball` sets `w_shotdir = normalize((enemy.origin + '0 0 10') - this.origin)`
  then invokes `WEP_WYVERN_ATTACK.wr_think(... fire=1)`.
- `wr_think` (gated by `time > attack_finished_single[0] || weapon_prepareattack(..., 1.2)`):
  - missile = `new(WyvernAttack)`, owner=realowner=actor, `solid=SOLID_TRIGGER`,
    `movetype=MOVETYPE_FLYMISSILE`, `projectiledeathtype=DEATH_MONSTER_WYVERN`.
  - `setsize('-6 -6 -6','6 6 6')`, origin = `actor.origin + view_ofs + v_forward*14`.
  - `flags=FL_PROJECTILE`; pushed to `g_projectiles` + `g_bot_dodge`.
  - `velocity = w_shotdir * g_monster_wyvern_attack_fireball_speed (1200)`.
  - `avelocity = '300 300 300'` (spinning), `nextthink = time + 5`.
  - think = `M_Wyvern_Attack_Fireball_Explode`; touch = `M_Wyvern_Attack_Fireball_Touch`.
  - `CSQCProjectile(missile, true, PROJECTILE_FIREMINE, true)` — networks the fire-mine visual
    (model + glow + trail) to clients.
  - For a monster actor: `attack_finished_single[0] = anim_finished = time + 1.2`.

### Fireball explode  (`wyvern.qc:61 M_Wyvern_Attack_Fireball_Explode`, authority)
- `Send_Effect(EFFECT_FIREBALL_EXPLODE, origin, '0 0 0', 1)` — the blast particle effect.
- `RadiusDamage(this, own, damage=50, edgedamage=20, force=50, own, NULL, radius=120,
  DEATH_MONSTER_WYVERN, DMG_NOWEP, NULL)`.
- Then `FOREACH_ENTITY_RADIUS(origin, 120, it.takedamage==DAMAGE_AIM && it!=own,
  Fire_AddDamage(it, own, 5 * MONSTER_SKILLMOD(own), g_monster_wyvern_attack_fireball_damagetime=2,
  deathtype))` — **ignites** every aim-damageable entity in radius for a burning DoT (`5×skillmod`
  total damage over 2s).
- `M_Wyvern_Attack_Fireball_Touch` = `PROJECTILE_TOUCH` then `Explode`.

### Per-frame brain  (framework `Monster_Think`/`Monster_Move`/`Monster_Attack_Check`, sv_monsters.qc)
- `mr_think` is a no-op (`return true`) — the wyvern uses the generic flying brain.
- Move logic: flyers move toward goal in 3D; speed clamped by `MONSTER_SKILLMOD` and `speed2*2.5`.

### Pain  (`wyvern.qc:139 METHOD(Wyvern, mr_pain)`, authority)
- `pain_finished = time + 0.5`; `setanim(anim_pain1, restart=true, ...)`. Returns `damage_take`.

### Death  (`wyvern.qc:147 METHOD(Wyvern, mr_death)`, authority)
- `setanim(anim_die1, false, true, true)`.
- **Death-launch scatter:** `velocity.x = 400*random()-200`, `velocity.y = 400*random()-200`,
  `velocity.z = 100*random()+100` — the corpse is flung in a random horizontal direction and
  popped upward (a flier dropping out of the sky).

### Dead think  (`wyvern.qc:131 METHOD(Wyvern, mr_deadthink)`, authority)
- Once the falling corpse `IS_ONGROUND`: `setanim(anim_die2, ...)` — swap to the landed/folded
  death animation.

### Animation table  (`wyvern.qc:158 METHOD(Wyvern, mr_anim)`, GAMEQC / presentation)
- `anim_idle='0 1 1'`, `anim_walk='1 1 1'`, `anim_run='2 1 1'`, `anim_pain1='3 1 2'` (0.5s),
  `anim_pain2='4 1 2'`, `anim_melee='5 1 5'`, `anim_shoot='6 1 5'`, `anim_die1='7 1 0.5'` (2s),
  `anim_die2='8 1 0.5'` (2s). Played client-side (CSQC frame playback).

### describe  (`wyvern.qc:192`, MENUQC) — monster-tools dialog flavor text.

### Constants (Base defaults, `monsters.cfg`)
| cvar | default | units |
|---|---|---|
| `g_monster_wyvern_health` | 150 | hp |
| `g_monster_wyvern_attack_fireball_damage` | 50 | hp (core) |
| `g_monster_wyvern_attack_fireball_edgedamage` | 20 | hp (edge) |
| `g_monster_wyvern_attack_fireball_force` | 50 | knockback |
| `g_monster_wyvern_attack_fireball_radius` | 120 | qu |
| `g_monster_wyvern_attack_fireball_speed` | 1200 | qu/s |
| `g_monster_wyvern_attack_fireball_damagetime` | 2 | s (burn duration) |
| `g_monster_wyvern_damageforcescale` | 0.6 | scale |
| `g_monster_wyvern_loot` | "cells" | item list |
| `g_monster_wyvern_speed_walk` | 120 | qu/s |
| `g_monster_wyvern_speed_run` | 250 | qu/s |
| `g_monster_wyvern_speed_stop` | 300 | qu/s |
| `avelocity` | `'300 300 300'` | deg/s spin |
| missile size | `'-6 -6 -6' '6 6 6'` | qu |
| muzzle offset | `v_forward*14` | qu |
| burn damage | `5 * MONSTER_SKILLMOD` | hp total |
| wind-up | 1.0 | s |
| attack lock | 1.2 + 0.2 | s |
| pain window | 0.5 | s |
| death scatter | x/y `400r-200`, z `100r+100` | qu/s |

## Port mapping
| Base feature | Port symbol | Status |
|---|---|---|
| identity / spawnflags / hitbox | `Wyvern.cs` ctor + `Spawn` (`SetSize -30..30/-48..30`, `EntFlags.Fly`, `MoveType.Fly`, `Gravity=0`, `MonsterFlag_FlyVertical`) | faithful |
| mr_setup (health/speeds/forcescale/loot) | `Wyvern.Spawn` + `MonsterAI.Setup` | faithful |
| attack dispatch (1s wind-up, ranged+melee → fireball) | `Wyvern.Attack` → `MonsterAI.QueueDelayedAttack(windUp:1, totalLock:1.4)` | faithful |
| fireball spawn | `Wyvern.FireFireball` → `MonsterAI.SpawnProjectile(speed 1200, size ±6, lifetime 5, FlyMissile)` + `AVelocity '300 300 300'` | faithful logic/values |
| fireball explode radius damage + knockback | `SpawnProjectile.Explode` → `WeaponSplash.RadiusDamage(50,20,120,force 50, DEATH_MONSTER_WYVERN)` | faithful |
| burning in radius | `Wyvern.FireFireball` `onExplode` → `MonsterFramework.AddFireDamage(it, 5×skillmod, 2s)` | faithful logic, values approx |
| `EFFECT_FIREBALL_EXPLODE` blast particle | NOT EMITTED (no `Send_Effect`/EffectEmitter in the wyvern explode path) | missing (presentation) |
| `CSQCProjectile PROJECTILE_FIREMINE` (in-flight visual) | NOT IMPLEMENTED (server-only `monster_projectile` entity, no fire-mine CSQC visual/trail) | missing (presentation) |
| fireball launch sound (`electro_fire`) | `Api.Sound.Play(e, Weapon, "weapons/electro_fire.wav")` | faithful (audio) |
| per-frame brain (mr_think no-op + generic Move) | `Wyvern.Think` → `MonsterAI.RunThink` (flying move) | faithful |
| mr_pain (0.5s window, anim_pain1) | generic `MonsterAI.MarkPain` (0.34s window, `MonsterAnim.Pain`) | partial (timing + presentation) |
| mr_death (anim_die1 + random launch velocity) | generic `MonsterAI.MarkDead` (`MonsterAnim.Death`, flyer velocity left unchanged — NO scatter) | partial (presentation) |
| mr_deadthink (swap to anim_die2 on landing) | NOT IMPLEMENTED (no on-ground die2 swap) | missing (presentation) |
| mr_anim frame table | NOT IMPLEMENTED as frame groups — port keeps a logical `MonsterAnim` phase only; CSQC frame playback deferred for all monsters | stub (presentation) |
| obituary notification (`was fireballed by a Wyvern`) | `NotificationsList.cs:179 D("MON_WYVERN", …)` | faithful |
| describe (menu flavor) | NOT IMPLEMENTED (monster-tools menu text) | missing (low importance) |
| spawnfunc `monster_wyvern` | `MonsterSpawnFuncs.Wyvern` registered in `MapObjectsRegistry.cs:200` | faithful + live |

## Parity assessment

**Logic — faithful + live.** The whole attack chain is correctly ported and reachable. The
liveness path: `GameInit.InstallGameplaySystems` → `MapObjectsRegistry.RegisterAll()` registers
`monster_wyvern` → BSP entity loader (and Invasion / `monster_spawner` / `spawnmonster "random"`)
calls `MonsterSpawnFuncs.Wyvern` → `MonsterAI.SpawnFromMap` runs `Wyvern.Spawn` and wires
`e.Think = self => { NextThink = Now; Def.Think(self); }` → `SimulationLoop.RunThink`
(SV_RunThink, sv_phys.c port) fires it every frame → `RunThink` → `Move` (FlyMove engine
integrator handles `MoveType.Fly`) + `AttackCheck` → `Wyvern.Attack` → wind-up → `FireFireball`
→ `SpawnProjectile` whose touch/think detonate via `RadiusDamage` + the burning `onExplode`.
Nothing on this path is dead.

**Values — faithful** for all balance cvars (health 150, fireball 50/20/50/120/1200, speeds
120/250/300, forcescale 0.6, loot "cells", spin `'300 300 300'`, size ±6, muzzle +14, wind-up 1s,
burn `5×skillmod`/2s). The port reads each via `MonsterAI.Cvar(name, fallback)` with the cfg name,
so runtime tuning carries through. **One values nuance:** the burning is reshaped from QC's
`.fire_*` accumulator (`Fire_AddDamage` ticked in `Fire_ApplyDamage`) into a `Burning`
status-effect whose per-tick `strength*0.05` is derived from `dps × FrameTime`; the documented
intent is that total damage over the burn window ≈ QC, but the per-tick cadence is approximate, so
the burn DoT is `values: partial` rather than bit-exact.

**Timing — mostly faithful.** Wind-up 1s and ≈1.4s attack lock match. **Gap:** pain window is the
shared generic 0.34s, not the wyvern's 0.5s `pain_finished` (`mr_pain` not overridden).

**Presentation — the weak dimension.** Several visible behaviours are missing because Wyvern.cs
does not override Death/Pain/DeadThink and the monster system has no CSQC frame playback yet:
- No `EFFECT_FIREBALL_EXPLODE` blast particle on detonation (QC `Send_Effect`).
- No `PROJECTILE_FIREMINE` in-flight visual (spinning fire-mine model + glow + trail); the port
  spawns a server-only invisible `monster_projectile`.
- Death does not fling the corpse with the random `400r-200 / 100r+100` scatter (QC `mr_death`);
  the generic `MarkDead` leaves a flyer's velocity unchanged, so a killed wyvern won't tumble out
  of the air the way Base shows.
- No `anim_die2` swap when the corpse lands (`mr_deadthink`).
- All wyvern animations (idle/walk/run/pain/shoot/die1/die2) exist only as the abstract
  `MonsterAnim` phase enum; the `.dpm` frame groups from `mr_anim` are never driven (shared
  monster-system limitation, not wyvern-specific). This is a stub on the presentation dimension.

**Audio — faithful** for the one cue the wyvern registers: `SND_WyvernAttack_FIRE = electro_fire`
is played on fire. QC registers no wyvern pain/death sound, and the port's generic `MarkDead`
attempts a `monsters/wyvern_death.wav` that has no Base counterpart — a benign extra (likely a
silent/missing sample), not a parity loss.

**Intended divergences:** none declared. The CSQC-frame-playback and projectile-visual deferrals
are systemic to the whole monster port (documented as "client render deferred"), but for the
wyvern unit they are genuine player-observable gaps, so they are recorded as gaps, not intended
divergences.

## Verification
- Source read of `wyvern.qc`/`.qh`, `monsters.cfg`, `monster.qh`, `sv_monsters.qc` (Base) and
  `Wyvern.cs`, `MonsterAI.cs`, `MonsterFramework.cs`, `MonsterSpawnFuncs.cs` (port).
- Liveness traced statically end-to-end: `GameInit` → `MapObjectsRegistry.cs:200` spawnfunc
  registration → `MonsterAI.SpawnFromMap` think-wiring (`MonsterAI.cs:403`) → `SimulationLoop.cs`
  `RunThink` (line ~276) firing `ent.Think` → `RunThink`/`AttackCheck`/`Wyvern.Attack`.
- Cvar defaults diffed against `monsters.cfg` lines 67–78 (all match the C# fallbacks).
- Obituary confirmed at `NotificationsList.cs:179`.
- NOT verified at runtime (no in-game spawn/observe this pass): the exact burn-DoT total, the
  absence of the fire-mine visual on screen, and whether `wyvern_death.wav` resolves to a sample.
  Confidence on logic/values/liveness is high; presentation gaps are high-confidence from code.

## Open questions
- Burn DoT: does the `Burning` status-effect's `strength*0.05`/tick scheme actually sum to QC's
  `5×skillmod` over 2s under the live tick rate, or drift? Needs a headless tick test or in-game
  measure. (Same approximation affects every monster that ignites.)
- Is `monsters/wyvern_death.wav` a real shipped sample, or does the generic `MarkDead` death-sound
  call silently no-op for the wyvern (QC has no such sound)? Runtime check.
- CSQC monster animation + `PROJECTILE_FIREMINE` visual are deferred system-wide — when that lands,
  the wyvern's `mr_anim`/`mr_death`/`mr_deadthink` frame behaviour and the fire-mine trail need a
  re-audit.

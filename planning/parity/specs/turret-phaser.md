# Phaser Cannon turret — parity spec

**Base refs:** `common/turrets/turret/phaser.qc` · `phaser.qh` · `phaser_weapon.qc` · `phaser_weapon.qh` (+ shared `common/turrets/sv_turrets.qc`, `util.qc` `FireImoBeam`, `turrets.cfg`)
**Port refs:** `src/VortexArena.Common/Gameplay/Turrets/PhaserTurret.cs` (+ shared `TurretAI.cs`, `TurretSpawn.cs`, `TurretSpawnFuncs.cs`)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Phaser Cannon is a sniper-class **hitscan** turret (`TUR_FLAG_SNIPER | TUR_FLAG_HITSCAN | TUR_FLAG_PLAYER`)
that fires a **sustained, invisible energy beam**. Firing spawns a dedicated `PhaserTurret_beam` entity that
re-traces along the head's aim **every frame for `shot_speed` (4) seconds**, ticking damage and **slowing**
each entity it crosses (`velocity *= 0.75`). The turret keeps tracking while the beam burns and only arms its
long refire (`shot_refire` = 4 s) when the beam ends. It activates wherever a stock map places a
`turret_phaser` entity and the `g_turrets` master switch is on (default on). Authority is server-side
(`#ifdef SVQC`); the only client-render pieces are the beam model visual and the head charge/discharge frame
animation.

## Base algorithm (authoritative)

### Spawn / identity  (`phaser.qh`, `phaser.qc:spawnfunc(turret_phaser)`)
- `spawnfunc(turret_phaser)` → `turret_initialize(this, TUR_PHASER)`; delete on failure.
- Hitbox `m_mins='-32 -32 0'`, `m_maxs='32 32 64'`. Body model `models/turrets/base.md3`, head model
  `models/turrets/phaser.md3`. `m_weapon = WEP_PHASER` (`PhaserTurretAttack`, a hidden special-attack weapon).

### `tr_setup`  (`phaser.qc:36`)  — side: shared/sv
- `ammo_flags = TFL_AMMO_ENERGY | TFL_AMMO_RECHARGE | TFL_AMMO_RECIEVE`.
- **`aim_flags = TFL_AIM_LEAD`** — lead-only. **No** `SHOTTIMECOMPENSATE`, **no** `ZPREDICT`, **no** `SPLASH`.
- Installs custom `turret_firecheckfunc = turret_phaser_firecheck`.

### `turret_phaser_firecheck`  (`phaser.qc:43`)  — side: sv
- Returns `false` while `this.fireflag != 0` (a beam is active or discharging); otherwise defers to the generic
  `turret_firecheck`. This is what prevents re-firing mid-beam and during the discharge animation.

### `wr_think` (fire)  (`phaser_weapon.qc:10`)  — side: sv
- Player-path branch (`IS_PLAYER(actor)`) handles the manned/`PhaserTurretAttack` weapon case; for a real
  turret edict only the beam-spawn branch runs.
- Spawns `beam = new(PhaserTurret_beam)`; `setmodel(MDL_TUR_PHASER_BEAM)`; `effects = EF_LOWPRECISION`;
  `solid = SOLID_NOT`; `movetype = MOVETYPE_NONE`.
- `beam.cnt = time + actor.shot_speed` (**beam burns for `shot_speed` = 4 s** — `shot_speed` is overloaded as
  *duration*, not projectile speed).
- `beam.shot_spread = time + 2` (the 2 s beam-sound re-trigger clock).
- `beam.shot_dmg = actor.shot_dmg / (actor.shot_speed / frametime)` — total `shot_dmg` (100) spread evenly
  across all ticks of the 4 s burn.
- `beam.scale = actor.target_range / 256` (visual length scale).
- `beam.owner = actor`, `beam.enemy = actor.enemy`, `beam.bot_dodge = true`, `bot_dodgerating = shot_dmg`.
- **Sound** `SND_TUR_PHASER` (`turrets/phaser`) on the beam, `CH_SHOTS_SINGLE`, `VOL_BASE`, `ATTEN_NORM`.
- `actor.fireflag = 1` (drives the head charge animation in `tr_think`).
- Stash + reset refire: `beam.attack_finished_single[0] = actor.attack_finished_single[0]`; then
  `actor.attack_finished_single[0] = time` (the beam re-parks this each frame).
- `setattachment(beam, actor.tur_head, "tag_fire")`.
- **Impact sound** `neximpact` (`SND_PhaserTurretAttack_IMPACT`) at `trace_endpos`, `CH_SHOTS`.
- If `!isPlayer && tur_head.frame == 0` → `tur_head.frame = 1` (start charge animation).

### `beam_think`  (`phaser_weapon.qc:56`)  — side: sv, runs every frame
- **End condition** `time > cnt || IS_DEAD(actor)`: `actor.attack_finished_single[0] = time + shot_refire`;
  `actor.fireflag = 2`; `actor.tur_head.frame = 10`; **stop the beam sound** (`SND_Null`); `delete(beam)`.
- Otherwise: `turret_do_updates(actor)` (refresh muzzle/aim distances + `tur_shotdir_updated`/`tur_shotorg`).
- Every 2 s (`time - shot_spread > 0`): reset `shot_spread = time + 2` and **replay** `SND_TUR_PHASER`.
- `nextthink = time`; `actor.attack_finished_single[0] = time + frametime` (holds the turret's refire across
  the beam so the generic gate can't re-fire — reinforced by the `fireflag` firecheck).
- **`FireImoBeam`** from `tur_shotorg` to `tur_shotorg + tur_shotdir_updated * target_range`, box
  `±shot_radius` (8), force `shot_force` (5), damage `beam.shot_dmg`, velfactor **0.75**, deathtype
  `DEATH_TURRET_PHASER`.
- `beam.scale = vlen(tur_shotorg - trace_endpos) / 256` (visual length to the impact).

### `FireImoBeam`  (`util.qc:25`)  — side: sv, shared
- "Railgun-like beam, but has thickness and supports slowing of target." **Penetrating:** traces repeatedly,
  pushing each hit entity onto `g_railgunhit`, making it `SOLID_NOT`, and continuing **until it hits a wall
  (`SOLID_BSP`)**. Then it damages **every** entity collected (no falloff) and **slows each**
  (`velocity *= f_velfactor`). So a single phaser tick can hit and slow **multiple players in a line**, not
  just the first.

### `tr_think` (head animation)  (`phaser.qc:13`)  — side: sv → networked frame, presentation
- When `fireflag == 1`: cycle `tur_head.frame` 1→10 then back to 1 (charge/firing loop).
- When `fireflag == 2`: advance the frame; on reaching 15, reset frame=0 and `fireflag=0` (discharge → idle,
  re-enabling fire via `turret_phaser_firecheck`).

### Constants (`turrets.cfg` `g_turrets_unit_phaser_*`, defaults)
| cvar | default | meaning |
|---|---|---|
| health | 500 | turret hp |
| respawntime | 90 | post-death respawn delay (s) |
| shot_dmg | 100 | **total** beam damage spread over the burn |
| shot_refire | 4 | refire after the beam ends (s) |
| shot_radius | 8 | beam half-thickness |
| shot_speed | 4 | **beam DURATION** (s), not projectile speed |
| shot_spread | 0 | (unused for the beam) |
| shot_force | 5 | knockback |
| shot_volly | 0 | no volley |
| shot_volly_refire | 5 | (unused) |
| target_range | 3000 | beam length / acquisition range |
| target_range_min | 0 | min range |
| target_range_optimal | 1500 | killzone for the distance score |
| target_select_rangebias | 0.85 | |
| target_select_samebias | 0 | (no sticky bonus) |
| target_select_anglebias | 0.25 | |
| target_select_playerbias | 1 | |
| target_select_missilebias | 0 | (ignores missiles) |
| ammo_max | 2000 | energy pool |
| ammo_recharge | 25 | energy/s |
| aim_firetolerance_dist | 100 | muzzle-on-target tolerance |
| aim_speed | 300 | head slew deg/s |
| aim_maxrot | 360 | yaw limit |
| aim_maxpitch | 30 | pitch limit |
| track_type | 3 | fluid-inertia head motor |
| track_accel_pitch | 0.5 | |
| track_accel_rot | 0.65 | |
| track_blendrate | 0.2 | |

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| `spawnfunc(turret_phaser)` | `TurretSpawnFuncs.Phaser` → `SpawnFuncs.Register("turret_phaser", …)` (MapObjectsRegistry:212) | **LIVE**: `GameWorld.SpawnMapEntities` → `SpawnFuncs.TrySpawn` → sets `e.Think`/`e.NextThink`; ticked by `SimulationLoop.RunThink`. |
| identity / hitbox / health | `PhaserTurret` ctor + `Spawn`→`TurretSpawn.Init` | mins/maxs/health 500 faithful. Head model not separately attached (client-render). |
| `tr_setup` ammo flags | `TurretSpawn.Init` ammoMax/recharge | energy pool modeled. |
| `tr_setup` aim_flags = LEAD | `PhaserTurret.Think` `TurretParams(... lead:true, shotTimeCompensate:true, zPredict:true ...)` | **Divergence:** Base sets LEAD only; port additionally enables shot-time-compensate + z-predict. |
| `turret_phaser_firecheck` (fireflag guard) | `BeamThink` holds `AttackFinished = endTime` | functional analogue; no `fireflag`/discharge concept. |
| `wr_think` beam spawn | `PhaserTurret.Attack` | spawns `PhaserTurret_beam`, sets `endTime`, `perTickDamage`. Faithful. |
| `beam_think` per-frame trace + damage | `PhaserTurret.BeamThink` | re-traces, damages, slows; tears down + arms refire on end. |
| `FireImoBeam` penetrating multi-hit | `BeamThink` single `Api.Trace.Trace` → `tr.Ent` only | **GAP:** port hits only the first entity; Base damages/slows ALL in the line. |
| velocity slow 0.75 | `hit.Velocity *= VelFactor` (0.75) | faithful for the one entity it hits. |
| `SND_TUR_PHASER` beam sound (spawn + every 2 s + silence on end) | none | **GAP:** port only plays `electro_fire` once. |
| `neximpact` impact sound | none | **GAP.** |
| `tr_think` head charge/discharge frames (fireflag 1↔10, 2→15→0) | none | **GAP (presentation).** |
| `MDL_TUR_PHASER_BEAM` beam visual scaled to hit dist | none (commented TODO in `Attack`) | **GAP (presentation).** |
| beam `bot_dodge`/`g_bot_dodge` registration | none | **GAP:** beam not registered as a dodgeable threat; port has no `g_bot_dodge` avoidance system (BotBrain does only generic `havocbot_dodge` strafe). Bots don't dodge the beam. |
| `StatusEffectsCatalog.Apply` extra slow | `PhaserTurret.ApplySlow` | **Port-only addition** (no Base equivalent). |
| death blast / respawn (shared) | `TurretAI.Die`/`Respawn` (respawntime via `Init`, default 60 — phaser cfg is 90) | shared lifecycle; phaser passes the default 60, not 90 (see gap). |

## Parity assessment

**Logic** — Core acquire→lead→track→fire and the sustained-beam state machine are faithfully ported and LIVE.
Two real logic gaps: (1) the beam is **single-hit** in the port vs **penetrating multi-hit** in Base
(`FireImoBeam` makes each hit unsolid and continues to the wall, damaging/slowing everyone in the line); a
player standing behind another player in the beam is hit in Base but not the port. (2) Base's `fireflag`
firecheck additionally blocks fire during the *discharge* phase (fireflag==2, ~5 frames); the port re-enables
fire the instant the beam ends. Minor. (3) The beam is never registered on `g_bot_dodge`
(`beam.bot_dodge=true`, `bot_dodgerating=shot_dmg`), and the port has no `g_bot_dodge` projectile-avoidance feed
at all (`BotBrain` only does generic `havocbot_dodge` strafe-on-clock), so bots don't specifically dodge the
phaser beam. Engine-wide gap, not phaser-specific.

**Values** — Damage/range/ammo/track constants match the cfg. Two divergences: (a) the port enables
`shotTimeCompensate`+`zPredict` lead flags that Base's phaser does **not** set (`aim_flags = TFL_AIM_LEAD`
only) — the port leads farther/differently than Base, especially against airborne targets. (b) the phaser
spawn passes the shared default `respawntime` (60) instead of the cfg's 90 (verify against `TurretSpawn.Init`
default — phaser's `Spawn` does not pass `respawnTime`, so it gets 60).

**Timing** — Beam duration (4 s), per-tick damage spread (`shot_dmg/(shot_speed/frametime)`), and the
refire-after-end (4 s) are faithful. The 2 s beam-sound re-trigger has no port effect because the sound is
absent. Frame-rate handling matches (per-frame `NextThink = now`).

**Presentation** — The invisible beam is *meant* to be invisible (the menu text says so), but Base still
draws a faint `MDL_TUR_PHASER_BEAM` scaled to the hit distance and animates the head's charge/discharge frames
(`tr_think`). The port renders neither. Both are acknowledged client-render TODOs.

**Audio** — Substantial gap. Base plays `SND_TUR_PHASER` (the phaser hum) at beam start, re-triggers it every
2 s while burning, and silences it on end; plus a `neximpact` impact cue. The port plays only a single
`electro_fire` at fire-start and nothing else — the characteristic phaser beam sound is never heard.

**Liveness** — **LIVE.** The full chain is wired: `MapObjectsRegistry.RegisterAll()` (called from
`GameInit`) registers `turret_phaser`; `GameWorld.SpawnMapEntities` dispatches it via `SpawnFuncs.TrySpawn`;
`TurretSpawnFuncs.Spawn` runs `def.Spawn(e)` and installs `e.Think`; `SimulationLoop.RunThink` (the SV_RunThink
port) fires it every tick; `Think`→`TurretAI.RunCombat`→`Attack` spawns the beam, whose own `NextThink` drives
`BeamThink`. Gated by the `g_turrets` master switch (default on).

**Intended divergences** — `PhaserTurret.ApplySlow` layers a brief status effect on top of the velocity slow;
this is a port-only addition with no Base counterpart (the slow in Base is purely the `velocity *= 0.75`
scale). Flagged as intended_divergence on that feature row.

## Verification
- Base algorithm + constants: read from `phaser.qc`, `phaser.qh`, `phaser_weapon.qc`, `util.qc:FireImoBeam`,
  `turret.qh`, `turrets.cfg` (value diff).
- Port liveness: traced `MapObjectsRegistry.cs:212` → `EntityClasses.cs:SpawnFuncs.TrySpawn` →
  `GameWorld.cs:2158` → `TurretSpawnFuncs.cs:Spawn` → `SimulationLoop.cs:RunThink` (static caller-chain read).
- Symbol existence: `DeathTypes.TurretPhaser` (DeathTypes.cs:276), `SoundsList "TUR_PHASER"` (SoundsList.cs:137,
  registered but never *played* by PhaserTurret), `Combat.Damage`, `StatusEffectsCatalog`.
- No phaser-specific unit test exists (turret tests cover aim/track/targeting/lifecycle generically, not the
  phaser beam). Behavioral claims (multi-hit, audio, animation) are code-read, **not** runtime-verified.

## Open questions
- Confirm at runtime that `TurretSpawn.Init` default `respawnTime` (60) is what phaser actually gets (no
  `respawnTime` argument is passed in `PhaserTurret.Spawn`), i.e. whether the 90 s cfg value is honored.
- Does any stock Xonotic map actually place `turret_phaser`? (Liveness is wired, but in-match exposure depends
  on map authorship — turrets are rare in stock rotation.)
- The port's `shotTimeCompensate`/`zPredict` lead flags: deliberate AI-quality choice or an accidental copy
  from a projectile turret's params? Base phaser is LEAD-only.

# Arc — parity spec

**Base refs:** `common/weapons/weapon/arc.qc` · `common/weapons/weapon/arc.qh` · `bal-wep-xonotic.cfg` (g_balance_arc_*) · shared fire math in `common/weapons/calculations.qc`, `server/weapons/tracing.qc`, `lib/math.qh` (ExponentialFalloff)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Arc.cs` · `WeaponFireDriver.cs` · `WeaponFireGate.cs` · `WeaponFiring.cs` · `WeaponSplash.cs` · `EntityWeaponState.cs` · `game/client/BeamRenderer.cs` · `game/client/EffectSystem.cs` · `game/client/ProjectileCatalog.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Arc is a hitscan electric weapon. Primary is a CONTINUOUS lightning beam that sweeps to follow the
player's aim (curving toward the crosshair, limited by a max angle) and deals damage-per-second to whatever
it touches while heating a barrel toward an overheat/jam limit. Its secondary depends on the
`g_balance_arc_bolt` cvar: with bolt enabled (the stock default = 1) the secondary fires a short burst of
bouncing energy bolts that explode with radius damage; with bolt disabled the secondary is a higher-damage
"burst" variant of the beam (and the beam also gains burst visuals when ATCK2 is held). Teammate-targeted
beams HEAL (health + armor) instead of damaging. The Arc is mutator-blocked (not in the stock weapon set; it
appears via the `arc` mutator / map placement). All gameplay is server authority; the visible beam, muzzle
flash, smoke, overheat fire, and impact effects are presentation (CSQC `Draw_ArcBeam` / a networked
`ENT_CLIENT_ARC_BEAM` entity in Base).

## Base algorithm (authoritative)

### Weapon identity / attributes (`arc.qh:CLASS(Arc)`)
- ammo_type RES_CELLS; impulse 3; spawnflags `WEP_FLAG_MUTATORBLOCKED | WEP_TYPE_HITSCAN`;
  m_color `'0.463 0.612 0.886'`; models h_arc.iqm / v_arc.md3 / g_arc.md3; muzzle model flash.md3;
  muzzle effect EFFECT_ARC_MUZZLEFLASH; crosshair `gfx/crosshairhlac` size 0.7; bot_pickupbasevalue 8000.

### Beam upkeep + cooldown branch (`arc.qc:W_Arc_Beam_Think`)
- **Trigger:** a live `W_Arc_Beam` entity thinks every frame (nextthink = time) while the beam is active.
- **End/cooldown conditions:** the beam ends (and the barrel cools) when not a player / dead / FROZEN /
  game_stopped / in a vehicle / `weapon_prepareattack_check` fails / switched away / neither ATCK nor burst
  held / heat ≥ overheat_max. Cooldown speed: if `heat > overheat_min` use `cooldown`; else if not bursting,
  `heat / beam_refire`; else 0. On overheat: emit EFFECT_ARC_OVERHEAT + play SND_ARC_STOP, and (if
  cooldown_release or overheat) set `arc_overheat = time + heat/cooldown_speed`, `arc_cooldown = cooldown_speed`.
  On end, if out of ammo and not unlimited, `W_SwitchToOtherWeapon` (does NOT force).

### Beam DPS core (`arc.qc:W_Arc_Beam_Think`)
- `coefficient = frametime`, clamped to remaining-ammo fraction (`curr_ammo / rootammo`); ammo decremented by
  `rootammo * frametime` (rootammo = burst_ammo when bursting, else beam_ammo; cells/sec).
- Heat: `beam_heat = min(overheat_max, beam_heat + heat_speed * frametime)` (heat_speed = burst_heat when
  bursting else beam_heat).
- `W_SetupShot_Range` for aim → w_shotorg / w_shotdir (wantdir). After teleport, lock beam_dir until
  `time >= beam_teleporttime + ANTILAG_LATENCY`.
- **Curving beam_dir:** if beam_dir ≠ wantdir, `angle = |wantdir - beam_dir| * RAD2DEG`; snap if < 0.01°,
  else `max_blendfactor = (angle > beam_maxangle) ? beam_maxangle/angle : 1`,
  `blendfactor = bound(0, 1 - beam_returnspeed*frametime, max_blendfactor)`,
  `beam_dir = normalize(wantdir*(1-blend) + beam_dir*blend)`. Segment count from
  beam_degreespersegment / beam_distancepersegment (capped ARC_MAX_SEGMENTS = 20).
- **Trace:** bezier-quadratic path (w_shotorg → controlpoint at `range*bound(0.001,1-tightness,1)` along
  wantdir → endpos at `range` along beam_dir), traced segment-by-segment with `WarpZone_traceline_antilag`.
  First hit terminates: wall → ARC_BT_WALL; entity → heal (same team) or damage.
- **Damage:** per-player `beam_damage` (or `burst_damage` if bursting); non-players `beam_nonplayerdamage`
  (only if `beam_nonplayerdamage` set). `damage *= coefficient * falloff`. Falloff = `ExponentialFalloff`
  over beam_falloff_mindist/maxdist/halflifedist if halflifedist set, else 1. Force `beam_force * coef *
  falloff` along the segment direction. `Damage(...)` + accuracy_add.
- **Healing (same team):** `Heal(trace_ent, own, roothealth*coef, hplimit)` where roothealth = burst/beam
  healing_hps, hplimit = beam_healing_hmax for players (no limit for non-players). Armor: if player and
  rootarmor set and current armor ≤ beam_healing_amax, `GiveResourceWithLimit(ARMOR, rootarmor*coef,
  beam_healing_amax)` + refresh armor-rot pause.
- **beam_type** networked (MISS/WALL/HEAL/HIT, OR'd with the 0x10 BURSTMASK) → drives the CSQC visual.

### Beam lifecycle (`arc.qc:W_Arc_Beam`, `W_Arc_Attack`, `wr_think`)
- `wr_think`: SetHeat + Smoke first; then if `time >= arc_overheat` and (ATCK or burst-secondary or
  beam_bursting), and no live beam, `weapon_prepareattack` → `W_Arc_Beam(burst)`. `W_Arc_Beam` plays
  SND_ARC_FIRE if >1s since last beam, spawns the beam entity, `Net_LinkEntity`, and immediately thinks once.
- On release (arc_BUTTON_ATCK_prev was set): play SND_ARC_STOP, `ATTACK_FINISHED = time + beam_refire *
  ratefactor`. The Arc primary does NOT use a refire on the beam itself (continuous).

### Bolt secondary (`arc.qc:wr_think fire&2`, `W_Arc_Attack_Bolt`, `W_Arc_Bolt_*`)
- Active only when `bolt = 1`. On secondary press: `weapon_prepareattack(secondary)`, then to_shoot =
  `bolt_count` scaled down by the affordable ammo fraction (`min(1, ammo/bolt_ammo)`), `W_DecreaseAmmo` by
  `min(bolt_ammo, ammo)`, `misc_bulletcounter = -to_shoot`, then `W_Arc_Attack_Bolt`.
- `W_Arc_Attack_Bolt` fires ONE bolt: W_SetupShot recoil 2, muzzle flash, spawns a shootable
  `MOVETYPE_BOUNCEMISSILE` missile (health = bolt_health, takedamage YES, event_damage W_Arc_Bolt_Damage),
  velocity = `W_SetupProjVelocity_PRE(bolt_)` (bolt_speed, bolt_spread), lifetime bolt_lifetime,
  `CSQCProjectile(PROJECTILE_ARC_BOLT, bounce)`, MUTATOR EditProjectile. Then `++misc_bulletcounter`; if it
  reaches 0 (burst done) sets `ATTACK_FINISHED = time + bolt_refire2 * ratefactor` and schedules `w_ready`
  after `bolt_refire`; otherwise re-schedules `W_Arc_Attack_Bolt` after `bolt_refire` (one bolt per
  bolt_refire until the burst is spent).
- `W_Arc_Bolt_Touch`: explode (`use`) on a DAMAGE_AIM toucher OR when bounce count exhausted OR bounce
  disabled; otherwise ++cnt, EFFECT_BALL_SPARKS, clear owner, set HITTYPE_BOUNCE; if bolt_bounce_explode,
  RadiusDamage on bounce; on first bounce (cnt==1) if bolt_bounce_lifetime, reset nextthink.
- `W_Arc_Bolt_Explode`: `RadiusDamage(bolt_damage, bolt_edgedamage, bolt_radius, force bolt_force,
  HITTYPE_SECONDARY)`, delete.
- `W_Arc_Bolt_Damage`: shootable — TakeResource HEALTH by damage; explode when HP ≤ 0.

### Heat / overheat / drop-pickup (`arc.qc`)
- `Arc_GetHeat_Percent` / `Arc_Player_SetHeat`: HUD heat %. `wr_drop` moves arc_overheat/arc_cooldown to
  the dropped weapon; `wr_pickup` restores if still hot. `wr_resetplayer` / `wr_playerdeath` clear them.

### Presentation (`arc.qc CSQC`)
- `Draw_ArcBeam` (+ `ENT_CLIENT_ARC_BEAM` network handler): a dedicated networked beam entity. Replicates the
  curving bezier sweep client-side, draws the beam as a cylindric line (`cl_arcbeam_simple` true default) or a
  segmented view-facing ribbon, with a spinning `MDL_ARC_MUZZLEFLASH` flash entity, trailparticles
  (EFFECT_ARC_BEAM / ARC_BEAM_HEAL), hit/muzzle dynamic lights, and per-beam_type thickness (8 normal / 14
  burst), color, and hit/muzzle effects. Loops SND_ARC_LOOP; `Remove_ArcBeam` silences it.
- `wr_impacteffect` (bolt): EFFECT_ELECTRO_IMPACT + SND_ARC_BOLT_IMPACT.
- `Arc_Smoke`: overheat/heat-fraction smoke (EFFECT_ARC_SMOKE), overheat fire (EFFECT_ARC_OVERHEAT_FIRE) +
  SND_ARC_LOOP_OVERHEAT loop while overheated and firing.

### Constants (Base defaults, bal-wep-xonotic.cfg)
beam_ammo 6 (cells/s) · beam_animtime 0.1 · beam_damage 100 (dps) · beam_degreespersegment 1 ·
beam_distancepersegment 0 · beam_falloff_{halflifedist,maxdist,mindist} 0 · beam_force 600 ·
beam_healing_amax 0 · beam_healing_aps 50 · beam_healing_hmax 150 · beam_healing_hps 50 · beam_heat 0 ·
beam_maxangle 10 · beam_nonplayerdamage 80 · beam_range 1500 · beam_refire 0.25 · beam_returnspeed 8 ·
beam_tightness 0.6 · burst_ammo 15 · burst_damage 250 · burst_healing_aps 100 · burst_healing_hps 100 ·
burst_heat 5 · cooldown 2.5 · cooldown_release 0 · overheat_max 5 · overheat_min 3 · bolt 1 · bolt_ammo 1 ·
bolt_bounce_count 0 · bolt_bounce_explode 0 · bolt_bounce_lifetime 0 · bolt_count 1 ·
bolt_damageforcescale 0 · bolt_damage 25 · bolt_edgedamage 12.5 · bolt_force 120 · bolt_health 15 ·
bolt_lifetime 5 · bolt_radius 65 · bolt_refire 0.16667 · bolt_refire2 0.16667 · bolt_speed 2300 ·
bolt_spread 0 · pickup_ammo 30 · switchdelay_drop 0.2 · switchdelay_raise 0.2 · weaponstart 0 ·
weaponstartoverride -1 · weaponthrowable 1 · bot_aimspeed/lifetime 0.

## Port mapping
| Base | Port |
|---|---|
| Weapon identity (arc.qh) | `Arc()` ctor + `Configure()` — all 40+ cvars loaded with correct defaults |
| `wr_think` dispatch | `Arc.WrThink` driven every tick by `WeaponFireDriver.Frame` (live, GameWorld.cs:1182) |
| `W_Arc_Beam_Think` DPS | `Arc.BeamTick` (heat, coefficient/ammo, curve, single trace, damage/heal/falloff/force) |
| beam cooldown branch | `Arc.BeamCooldown` (cools by `cooldown * frametime`; resets BeamInitialized) |
| overheat jam | `Arc.BeamTick` head (sets ArcOverheat = time + overheat_max, plays SND_ARC_STOP) |
| `W_Arc_Attack_Bolt` | `Arc.AttackBoltBurst` (refire-gated via `PrepareAttack`) |
| `W_Arc_Bolt_Touch/Explode/Damage` | `Arc.BoltTouch` / `ExplodeBolt` (RadiusDamage via WeaponSplash) |
| `wr_checkammo1/2` | `Arc.CheckAmmoPrimary/Secondary` (wired into the fire gate + auto-switch) |
| ExponentialFalloff | `WeaponFiring.ExponentialFalloff` (faithful) |
| `Draw_ArcBeam` / ENT_CLIENT_ARC_BEAM | `EffectEmitter.Emit("ARC_BEAM", origin, endpos)` per frame → classifies as **Trail** (isTrail:true) → `EffectSystem.BuildTrail` spark particles (NOT BeamRenderer.Beam — that route is unreached; no bezier/muzzle/type table/lights) |
| PROJECTILE_ARC_BOLT visual | `ProjectileCatalog` ArcBolt (TR_WIZSPIKE trail, GlowSprite, Bounce collide) |
| `wr_drop/pickup/resetplayer/playerdeath` | NOT IMPLEMENTED (heat not migrated across drop/pickup; tracked as feature weapon-arc.heat_persistence) |
| `Arc_Smoke` (smoke/overheat fire/loop) | NOT IMPLEMENTED (no per-tick smoke/overheat-fire/overheat-loop) |
| `wr_aim` bot aim | NOT IMPLEMENTED in Arc.cs (general bot fire path only) |

## Parity assessment

- **Beam DPS, curve, heal, falloff, force, ammo, heat (logic/values):** faithful. The curve math, blend
  factor, max-angle clamp, coefficient/ammo clamp, and heat accumulation match Base 1:1.
- **Beam trace shape (timing/presentation):** the port traces a SINGLE straight segment along beam_dir; Base
  traces a multi-segment bezier (control point pulled toward wantdir by `tightness`). For gameplay hit
  detection along a near-straight beam the endpoint is the same, but the *curved body* of the beam can clip
  geometry differently near a swept corner, and `beam_tightness`/`beam_degreespersegment`/
  `beam_distancepersegment`/ARC_MAX_SEGMENTS are loaded but unused. Minor gameplay divergence; notable visual
  divergence (no curving beam body).
- **Visible beam (presentation):** the dedicated `Draw_ArcBeam` networked entity is NOT ported. The port
  emits an `arc_beam` effect each server tick; this is registered `isTrail: true` (EffectsList.cs:51) and the
  `arc_beam` effectinfo block is `type spark` (trailspacing 10) with NO `orientation beam` line. CORRECTION
  to the first draft: the beam does **NOT** route through `BeamRenderer.Beam`. `EffectSystem.ClassifyUncached`
  tests the trail flag FIRST (line 676), so `arc_beam` classifies as `Trail` and never reaches the
  `EffectClass.Beam` route (line 695); the `EiOrientation.Beam` route inside `BuildFromInfo` (line 1116) is
  also skipped because the block carries no Beam orientation. So the beam renders as a **spark-particle trail**
  swept from origin to endpos — faintly visible, but the wrong visual. Missing: the cylindric/bezier beam body,
  the spinning muzzle-flash entity, per-beam_type thickness (8 vs 14 burst) / color / hit & muzzle dynamic
  lights, the heal-beam variant visuals, and team-color tinting. The `Beams` renderer IS wired non-null in a
  live match (ClientWorld.cs:332) — the route simply isn't taken for the arc. Liveness is therefore `live` (a
  trail is emitted), but fidelity is `partial`/`missing`.
- **Bolt secondary (logic):** mostly faithful but with a CADENCE divergence — the port fires the ENTIRE
  `bolt_count` burst in ONE WrThink call (a single PrepareAttack), whereas Base fires one bolt per
  `bolt_refire`, spacing the burst over time and enforcing `bolt_refire2` only after the last bolt
  (via misc_bulletcounter counting up from −to_shoot). With the stock `bolt_count = 1` this is observationally
  identical, but for `bolt_count > 1` the port would dump the whole burst in one frame.
- **Bolt ammo (logic):** the port consumes a flat `bolt_ammo` per shot and does NOT scale to_shoot by the
  affordable fraction or use `W_DecreaseAmmo(min(bolt_ammo, ammo))`. Again moot at bolt_count 1.
- **Beam ammo gate (values):** `CheckAmmoPrimary` requires `cells >= beam_ammo (6)`; Base `wr_checkammo1`
  requires only `cells > 0`. A player with 1–5 cells can start the beam in Base but the port auto-switches
  away. Observable values mismatch.
- **Overheat / cooldown subtleties:** the port's overheat sets `ArcOverheat = time + overheat_max` (a fixed
  5s) rather than Base's `time + heat/cooldown_speed`; `cooldown_release` and the `heat/beam_refire`
  rest-cooldown branch are not modeled. The heat-percent HUD stat, drop/pickup heat migration, and reset on
  death are not ported. Functional overheat jam happens, but its duration and HUD feedback diverge.
- **Audio:** SND_ARC_FIRE (beam start), SND_ARC_LOOP (looping), SND_ARC_STOP (release/overheat), and
  SND_ARC_BOLT_IMPACT/SND_ARC_BOLT_FIRE are called server-side from Arc.cs. The >1s gate on the fire sound
  (Base `time - beam_prev > 1`) is NOT reproduced (BeamPrev field exists but is unused) — every fresh beam
  plays SND_ARC_FIRE. SND_ARC_LOOP_OVERHEAT (overheat loop) is missing (tied to the un-ported Arc_Smoke).
- **Smoke / overheat fire (presentation):** `Arc_Smoke` (heat-fraction smoke, overheat fire particles, the
  overheat sound loop) is entirely absent.

### Liveness
- The whole weapon is LIVE: `WeaponFireDriver.Frame` (GameWorld.cs:1182) calls `Arc.WrThink` every server
  tick for the active player weapon; the fire gate, ammo checks, and auto-switch are wired. The Arc is
  mutator-blocked so it only appears in matches with the arc mutator / map placement, but when present it is
  fully driven.
- The bolt projectile renders (ProjectileCatalog ArcBolt). The beam visual IS live (a spark-particle trail is
  emitted each tick and drawn), but it routes through the trail path, NOT the `BeamRenderer.Beam` cylindric
  beam (verified: `arc_beam` is isTrail + `type spark` with no Beam orientation), so the Draw_ArcBeam fidelity
  is absent.

### Intended divergences
None declared. The single-trace-vs-bezier and "beam visual via per-frame trail rather than a networked
Draw_ArcBeam entity" are pragmatic port substitutes documented in the Arc.cs class comment, but they DO
change observable behavior (no curving beam body / no per-type visuals), so they are tracked as gaps rather
than intended divergences pending an owner decision.

## Verification
- Base constants read directly from bal-wep-xonotic.cfg (lines 735–789) and arc.qh — exact.
- Port constants read from Arc.cs `Configure()` — defaults match Base 1:1 (verified field-by-field).
- Liveness: traced `WeaponFireDriver.Frame` ← `GameWorld.WeaponThink` ← per-tick player frame; ammo dispatch
  via `WeaponAmmo.Check` Arc case. Confirmed by code read.
- Beam-render route: `EffectSystem` line 1116–1124 routes Beam-orientation trail blocks to `BeamRenderer.Beam`;
  ARC_BEAM registered as a trail (EffectsList.cs:51). NOT run at runtime — orientation of the `arc_beam`
  effectinfo block and a wired `Beams` instance unverified → `unknown`.
- No dedicated Arc unit tests located.

## Open questions
1. RESOLVED: the `arc_beam` effectinfo block does NOT carry `EiOrientation.Beam` (it's `type spark`
   trailspacing 10), and it's registered `isTrail: true`, so it renders via the spark-trail path, not
   `BeamRenderer.Beam`. The `Beams` renderer is wired (ClientWorld.cs:332) but unreached for arc. The primary
   beam is faintly visible as a sparkle trail, not the cylindric Draw_ArcBeam line.
2. Is `bolt_count` ever raised above 1 in any shipped balance/mutator? If not, the burst-cadence and
   ammo-fraction divergences are dormant.
3. Should the beam-ammo gate be relaxed to Base's `> 0` (so 1–5 cells still fire the beam)? CONFIRMED real
   bug: `CheckAmmoPrimary` uses `cells >= 6` vs Base `cells > 0`; dispatched live via WeaponAmmo.Check.
4. Should the bolt shoot-down subtract HP (Base) rather than detonate on first damage (port)? Port's
   ExplodeBolt fires immediately on any ProjectileDamage; Base only explodes when RES_HEALTH hits 0.

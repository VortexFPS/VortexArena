# Mine Layer — parity spec

**Base refs:** `common/weapons/weapon/minelayer.qc` · `common/weapons/weapon/minelayer.qh` · balance in `bal-wep-xonotic.cfg` (`g_balance_minelayer_*`); shared fire/projectile math in `common/weapons/calculations.qc` + `server/weapons/tracing.qc` + `server/weapons/common.qh`
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Minelayer.cs` · shared: `WeaponFiring.cs`, `WeaponSplash.cs`, `Projectiles.cs`, `WeaponFireDriver.cs`; physics: `src/VortexArena.Engine/Simulation/{SimulationLoop,MoveTypePhysics,FlyMove}.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Mine Layer is a splash weapon. Primary fire lobs a mine (`MOVETYPE_TOSS`, speed 800) that on touching a
BSP surface sticks in place (`MOVETYPE_NONE`), arms, and faces against the surface normal. An armed mine
detonates when an enemy enters its proximity radius (sooner the closer they get), at the end of its lifetime
(after a short warning countdown), when the owner dies/disconnects, or when remote-detonated. Secondary fire
remote-detonates ALL of this player's placed mines (gated by a spawnshield timer and a team-safety radius
check). Mines are shootable (15 hp) and limited to 4 per player. It is `WEP_FLAG_MUTATORBLOCKED` (not in the
default weapon set / disabled in most arenas) — so it only appears when explicitly enabled.

## Base algorithm (authoritative)

### Primary fire — lay a mine  (`minelayer.qc:W_MineLayer_Attack`, dispatched from `wr_think`)
- **Trigger:** sv `wr_think`, `fire & 1`, gated by `weapon_prepareattack(refire)`. Before that, an
  over-limit check runs in `wr_think`-adjacent `W_MineLayer_Attack`: if `limit && Count >= limit`, send
  `WEAPON_MINELAYER_LIMIT` notification, play `SND(UNAVAILABLE)`, and return WITHOUT firing.
- **Algorithm:**
  1. `W_DecreaseAmmo(ammo=4 rockets)`.
  2. `W_SetupShot_ProjectileSize(actor, '-4 -4 -4','4 4 4', false, 5 /*recoil*/, SND_MINE_FIRE, CH_WEAPON_A, damage, m_id)` — sets `w_shotorg/w_shotdir`, plays `mine_fire`, applies recoil 5.
  3. `W_MuzzleFlash(thiswep, …, w_shotorg, w_shotdir)` — EFFECT_ROCKET_MUZZLEFLASH at the muzzle.
  4. Spawn `mine` entity (`classname="mine"`), `owner=realowner=actor`. `spawnshieldtime = detonatedelay>=0 ? time+detonatedelay : -1` (default −1).
  5. `takedamage=DAMAGE_YES`, `health=15`, `damageforcescale=1.25`, `event_damage=W_MineLayer_Damage`, `damagedbycontents=true`.
  6. `MOVETYPE_TOSS`, `PROJECTILE_MAKETRIGGER`, size `±4`, `setorigin(w_shotorg - v_forward*4)`.
  7. `W_SetupProjVelocity_Basic(mine, speed=800, 0)` → velocity along shotdir, `angles = vectoangles(velocity)`.
  8. `settouch(W_MineLayer_Touch)`, `setthink(W_MineLayer_Think)`, `nextthink=time`. `cnt = lifetime - lifetime_countdown` and if `>0`, `cnt += time` (forced-detonation deadline).
  9. `flags=FL_PROJECTILE`, `missile_flags=MIF_SPLASH|MIF_ARC|MIF_PROXY`, `bot_dodge=true`, `bot_dodgerating=damage*2`.
  10. `CSQCProjectile(mine, true, PROJECTILE_MINE, true)` — networks it to clients as a mine projectile.
  11. `MUTATOR_CALLHOOK(EditProjectile, actor, mine)` (Rocket Flying mutator clears the detonate gate here).
- **Constants:** `ammo=4`, `animtime=0.4`, `damage=40`, `damageforcescale=1.25`, `detonatedelay=-1`,
  `edgedamage=20`, `force=250`, `health=15`, `lifetime=30`, `lifetime_countdown=0.5`, `limit=4`,
  `proximity_radius=125`, `proximity_time_core=0.1`, `proximity_time_edge=0.3`, `radius=175`, `refire=1`,
  `reload_ammo=0` (so RELOADABLE flag is a no-op at default), `reload_time=2`, `remote_damage=40`,
  `remote_edgedamage=20`, `remote_force=300`, `remote_radius=200`, `speed=800`,
  `switchdelay_drop=0.2`, `switchdelay_raise=0.2`. (Authority sv-side balance cvars.)

### Stick to surface  (`minelayer.qc:W_MineLayer_Stick`, from `W_MineLayer_Touch`)
- **Trigger:** `W_MineLayer_Touch` when `toucher.solid == SOLID_BSP` (and not already stuck, and `time > wait`).
- **Algorithm:** plays `mine_stick`; spawns a fresh SERVER-side entity (NOT a CSQC projectile, so it can
  orient to the surface), copies owner/health/damage state, `setmodel(MDL_MINELAYER_MINE)` (`models/mine.md3`),
  size `±4`, `angles = vectoangles(-trace_plane_normal)` (face against the surface), `movedir = -trace_plane_normal`,
  `MOVETYPE_NONE` (locked), `settouch(func_null)`, re-arms `W_MineLayer_Think`, deletes the original mine.
  If touching a moving entity, `SetMovetypeFollow` glues it.
- **Edge:** `glowmod = colormapPaletteColor(owner team color)`.

### Think — countdown, owner-death, proximity  (`minelayer.qc:W_MineLayer_Think`)
- Runs every tick (`nextthink=time`).
- **Lifetime countdown:** when `time > cnt && !mine_time && cnt > 0`: if `lifetime_countdown>0` play
  `mine_trigger`; set `mine_time = time + lifetime_countdown`; `mine_explodeanyway=1` (ignore team safety).
- **Owner death/disconnect/frozen:** if owner not a player, dead, or frozen → tag `HITTYPE_BOUNCE`, explode.
- **Proximity:** `WarpZone_FindRadius(origin, proximity_radius=125)`; for each enemy player (alive, unfrozen,
  not independent, DIFF_TEAM): if not yet armed, play `mine_trigger` once; compute
  `new_mine_time = time + time_core; if (time_edge != time_core) new_mine_time += (time_edge - time_core) * dist/proxrad`;
  keep the EARLIEST `mine_time`.
- **Detonate:** if `mine_time && time >= mine_time` → `W_MineLayer_ProximityExplode`.
- **Remote:** if owner still holds the Mine Layer, alive, and `minelayer_detonate` flagged → `W_MineLayer_RemoteExplode`.
- **Re-ground:** a knocked-loose `MOVETYPE_BOUNCE` mine that lands clears ONGROUND so it can re-touch & re-stick.

### Proximity explode  (`minelayer.qc:W_MineLayer_ProximityExplode`)
- If `protection && mine_explodeanyway==0`: `WarpZone_FindRadius(origin, radius=175)`; if any SAME_TEAM is in
  radius, return (delay). NOTE: `protection` defaults to **0**, so this team-safety check is OFF by default.
- Else `mine_time=0; W_MineLayer_Explode(NULL)`.

### Explode  (`minelayer.qc:W_MineLayer_Explode`)
- Airshot achievement: if directhit is an enemy flying player → `ANNCE_ACHIEVEMENT_AIRSHOT` to the owner.
- `event_damage=func_null; takedamage=DAMAGE_NO`.
- `RadiusDamage(damage=40, edgedamage=20, radius=175, force=250, deathtype, directhitentity)`.
- If the owner still holds the Mine Layer and is out of ammo (and not unlimited): force a weapon switch
  (`ATTACK_FINISHED=time`, `m_switchweapon=w_getbestweapon`).
- `delete(this)`.
- CSQC `wr_impacteffect`: `pointparticles(EFFECT_ROCKET_EXPLODE)` + `sound(CH_SHOTS, SND_MINE_EXP)`.

### Shoot-down  (`minelayer.qc:W_MineLayer_Damage` = `.event_damage`)
- Invoked by the damage pipeline when the mine takes damage. If `health<=0` already, return.
- If `damageforcescale && inflictor != "mine"`: `MOVETYPE_BOUNCE`, `wait = time + 0.0625`,
  re-arm touch, `avelocity += force * -damageforcescale` (knock the mine loose & spinning).
- `W_CheckProjectileDamage` gate (g_projectiles_damage). `TakeResource(HEALTH, damage)`,
  `angles = vectoangles(velocity)`. If `health<=0` → `W_PrepareExplosionByDamage(W_MineLayer_Explode_think)`.

### Remote detonate  (`minelayer.qc:W_MineLayer_RemoteExplode` → `W_MineLayer_DoRemoteExplode`)
- Secondary fire flags all of the actor's mines (`W_MineLayer_PlacedMines(detonate=true)` sets
  `minelayer_detonate`) and plays `mine_det` (`CH_WEAPON_B`) if any were flagged. The think then detonates each.
- Gate (`W_MineLayer_RemoteExplode`): if owner dead, return. If `spawnshieldtime>=0`: require `time>=spawnshieldtime`.
  Else (`<0`, the default): require NO SAME_TEAM entity within `remote_radius=200` of the mine (team safety).
- `DoRemoteExplode`: if movetype NONE/FOLLOW, set `velocity = movedir` (so fx/decal work),
  `RadiusDamage(remote_damage=40, remote_edgedamage=20, remote_radius=200, remote_force=300, deathtype|HITTYPE_BOUNCE, NULL)`.

### Ammo / reload / messages
- `wr_checkammo1`: rockets>=4 OR clip_load>=4. `wr_checkammo2`: any placed mine exists.
- `wr_reload`: `W_Reload(ammo=4, SND_RELOAD)` — but `reload_ammo=0` so the forced-reload branch in `wr_think` never runs by default.
- `wr_resetplayer`: zero `minelayer_mines`. `wr_suicidemessage=WEAPON_MINELAYER_SUICIDE`, `wr_killmessage=WEAPON_MINELAYER_MURDER`.

### Bot AI  (`minelayer.qc:wr_aim`)
- `bot_aim` for primary; over-limit → don't fire. `skill>=2`: estimate self/team/enemy damage from each
  placed mine vs each bot target; set `BUTTON_ATCK2` (remote detonate) when desirable damage ≥ 0.75 (or 0.1 for
  tracked) of core damage; cancel if self-damage would kill (skill>6.5). Primary suppressed when detonating.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| Identity/attribs/balance | `Minelayer` ctor + `Configure()` | All cvars present with correct defaults. |
| `wr_think` dispatch | `WrThink` (+ `WeaponFireDriver.Frame`) | Driver calls both fire modes per tick — correct, but reached only when the MUTATORBLOCKED weapon is granted (arena/NewToys), not in a default match. |
| `W_MineLayer_Attack` | `Attack` | Faithful spawn/velocity/gate; see gaps (no muzzle flash, no notification, no model). |
| `W_SetupShot_ProjectileSize` | `WeaponFiring.SetupShot(recoil:5)` | Faithful (recoil, muzzle, trueaim). |
| `W_SetupProjVelocity_Basic` | `mine.Velocity = shot.Dir * Speed` | spread 0 → faithful. |
| `W_MineLayer_Think` | `OnThink` | Faithful logic; reuses `Frame`(=mine_explodeanyway) & `MaxHealth`(=mine_time). |
| `W_MineLayer_Think` proximity team-filter | `OnThink` | **BUG: keys on `self.Team` (never set) instead of `self.Owner.Team` → triggers on teammates in team modes.** |
| `W_MineLayer_ProximityExplode` | `ProximityExplode` | Team-safety block is dead (`self.Team` never set); matches Base-default (protection=0) by accident. |
| `W_MineLayer_Stick`/`_Touch` | `OnTouch` | Locks in place + plays `mine_stick`; **does NOT set the mine model, orient to surface normal, store movedir, or honor the `time<=wait` reattach delay**; also sticks on `non-client other` where Base sticks only on `SOLID_BSP`. |
| `W_MineLayer_Explode` + `wr_impacteffect` | `Explode` | RadiusDamage + `mine_exp` + ROCKET_EXPLODE effect faithful; airshot + out-of-ammo switch omitted. |
| `W_MineLayer_RemoteExplode`/`Do…` | `RemoteDetonate` | Folds the flag step + detonate into one; faithful gate (spawnshield timer / team safety). |
| `W_MineLayer_Damage` (shoot-down) | `OnMineDamage` (installed as `ProjectileDamage`) | **DEAD — the damage pipeline never invokes `ProjectileDamage` for mines.** |
| `wr_checkammo1/2` | `CheckAmmoPrimary/Secondary` | Faithful. |
| suicide/kill messages | `DeathMessages.cs` (minelayer cases) | Faithful. |
| `WEAPON_MINELAYER_LIMIT` notify + UNAVAILABLE sound | — | **NOT IMPLEMENTED** (over-limit returns silently). |
| Rocket-Flying EditProjectile clear | `MutatorHooks.EditProjectile` + `Entity.ProjectileDetonateTime` | Faithful, unit-tested. |
| `wr_aim` bot detonation AI | — | **NOT IMPLEMENTED** (per-weapon `wr_aim` deferred for all weapons). |
| CSQCProjectile networking / `mine.md3` render | — | **NOT IMPLEMENTED** (placed mine has no model → invisible). |

## Parity assessment

### Logic
The core state machine is faithfully ported: toss→stick→arm→proximity/lifetime/owner-death/remote detonation,
the earliest-explosion-time selection, the team-safety hold, and the spawnshield/team-safety remote gate. Two
logic deviations:
- **Shoot-down is dead.** `OnMineDamage` is installed only as `Entity.ProjectileDamage`, but the damage
  pipeline (`DamageSystem.EventDamage`) dispatches a non-player damageable target through `GtEventDamage`,
  never through `ProjectileDamage` (only `BreakablehookMutator` calls `ProjectileDamage`). So a mine takes
  knockback (`ApplyKnockback` runs) but its HP is never subtracted and the knock-loose / detonate-on-zero-HP
  behavior never fires. A player cannot shoot a mine loose or destroy it. (This also affects Mortar/Devastator
  shoot-down — a shared pipeline gap, not minelayer-specific.)
- **Proximity team-filter is DEAD (real bug).** The proximity trigger excludes teammates with
  `self.Team != 0 && head.Team == self.Team`, but `Attack` NEVER assigns `mine.Team` (only `Owner`). So
  `self.Team` is always 0, the exclusion never fires, and in team modes a placed mine arms and detonates on a
  TEAMMATE who walks near it. Base keys this on the OWNER (`DIFF_TEAM(head, this.realowner)`) and correctly
  skips teammates. Fix: compare `head.Team` against `self.Owner.Team` (and likewise in `ProximityExplode`).
- **`protection` analysis corrected.** Earlier audits claimed the port is "MORE protective than Base" by
  omitting the `protection` cvar in `ProximityExplode`. That is wrong: the port's `ProximityExplode`
  team-safety block is itself dead (gated on `self.Team != 0`, never true), so it never holds; and Base's
  block is gated on `WEP_CVAR(protection)` which defaults to **0**, so Base never holds either. Net
  ProximityExplode behavior matches Base-default by accident (both always explode), not by being more
  protective. (The `RemoteDetonate` team-safety check, by contrast, reads `actor.Team` which IS set, so it is
  faithful.)

### Values
All balance constants match Base defaults exactly (verified against `bal-wep-xonotic.cfg`). Recoil 5, sizes
±4, ammo 4, etc. all faithful. `reload_ammo=0` correctly makes reload a no-op, matching Base.

### Timing
Faithful: lifetime deadline (`time + (lifetime - lifetime_countdown)`), the 0.5s countdown, proximity
core/edge interpolation, refire 1s and animtime 0.4 routed through `WeaponFireDriver`. Mine think runs every
tick via `SimulationLoop.RunThink` driven by `MoveTypePhysics.RunEntity` (MOVETYPE_TOSS/NONE/BOUNCE all run
the think). Switch delays (0.2/0.2) are read by the driver but the precise switch timing is a known port-wide
T6 deferral.

### Presentation
Several gaps:
- **No mine model.** Base `setmodel(MDL_MINELAYER_MINE)` (`models/mine.md3`); the port never calls
  `SetModel` on the mine, so the placed mine is invisible. Surface-normal facing (`vectoangles(-trace_plane_normal)`)
  is also not done (the port zeroes velocity instead).
- **No muzzle flash.** Base `W_MuzzleFlash` (EFFECT_ROCKET_MUZZLEFLASH); the port omits it (Mortar/Devastator emit theirs).
- **No airshot achievement** announcement.
- The explosion effect (`ROCKET_EXPLODE`) IS emitted faithfully.

### Audio
Fire (`mine_fire`), stick (`mine_stick`), trigger/countdown (`mine_trigger`), remote-detonate (`mine_det`),
explosion (`mine_exp`) are all played with correct samples. Channels approximate Base (port uses Body where
Base uses CH_SHOTS/CH_WEAPON_B; functionally similar). The over-limit `SND(UNAVAILABLE)` cue is missing.

### Liveness
- **Partial (conditional):** lay-mine primary, stick, proximity/lifetime/owner-death detonation,
  remote-detonate secondary, splash damage, ammo checks. The dispatch wiring (`WeaponFireDriver` → `WrThink` →
  physics think) is correct, BUT the weapon is `WEP_FLAG_MUTATORBLOCKED` and excluded from the default loadout
  / `allweapons` (`GiveItems.cs:340`), so NONE of this runs in a normal match. It is reached only when the
  weapon is granted via weaponarena / NewToys / impulse-give (`SpawnSystem.cs:765`, `NixMutator`,
  `RandomItemsMutator` all honor the flag). Per the schema (`live` = invoked in a normal match), these are
  `liveness: partial`.
- **Live:** death messages / obituary (the death pipeline runs every match and fires whenever someone dies to
  a mine), RocketFlying gate clear.
- **Dead:** mine shoot-down (`ProjectileDamage` never invoked by the damage pipeline).
- **Not implemented:** over-limit notification + UNAVAILABLE sound; bot `wr_aim` (mine-detonation AI) — bots
  lay mines via the generic primary path but never remote-detonate them and ignore the per-weapon aim throttle.

## Verification
- Base constants: diffed against `bal-wep-xonotic.cfg:178-208` (exact match).
- Port logic: read `Minelayer.cs` in full; confirmed `WrThink` is invoked live by `WeaponFireDriver.Frame`
  (both fire modes) and the mine think/touch by `SimulationLoop.RunThink` + `MoveTypePhysics`/`FlyMove`.
- Shoot-down dead: traced `Entity.ProjectileDamage` consumers — only `BreakablehookMutator.cs:79`;
  `DamageSystem.EventDamage` routes non-players via `GtEventDamage`, never `ProjectileDamage`.
- RocketFlying gate: `tests/VortexArena.Tests/RocketFlyingGateTests.cs` covers the mine detonate-delay clear.
- No dedicated Minelayer behavioral test exists (only WeaponById/order/remap coverage). Detonation timing,
  team-safety, and remote gate are unverified by test → marked accordingly.

## Open questions
- Is the Mine Layer ever enabled on a live map/mutator in this port (it's MUTATORBLOCKED)? If never, the whole
  unit's in-match liveness is moot and the gaps are latent. Needs a runtime check on an arena/mutator that grants it.
- Shoot-down: is the shared projectile-shoot-down path (`ProjectileDamage` dispatch) planned to be wired into
  `DamageSystem` for all shootable projectiles, or intentionally dropped? Affects Mortar/Devastator/Arc too.
- Channel mapping (Body vs CH_SHOTS/CH_WEAPON_B) — does the port's channel model reproduce Base's
  cutoff/stacking semantics for stacked mine sounds? Unverified.

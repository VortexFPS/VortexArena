# Devastator (Rocket Launcher) — parity spec

**Base refs:** `common/weapons/weapon/devastator.qc` · `common/weapons/weapon/devastator.qh` · `bal-wep-xonotic.cfg` (g_balance_devastator_*) · shared `server/weapons/tracing.qc` (W_SetupShot*, W_SetupProjVelocity_Basic) · `server/damage.qc` (RadiusDamage / RadiusDamageForSource) · `common/weapons/calculations.qc` (W_WeaponSpeedFactor)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Devastator.cs` · `WeaponSplash.cs` · `WeaponFiring.cs` · `WeaponFireDriver.cs` · `WeaponFireGate.cs` · `Projectiles.cs` · `game/client/ProjectileCatalog.cs` + `ProjectileRenderer.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Devastator (legacy netname `rocketlauncher`) is Xonotic's splash weapon. Primary fire launches a single
guidable rocket that accelerates from `speedstart` toward `speed`, detonates on contact / end-of-lifetime
dealing radius damage + knockback, and can be steered toward the owner's aim while the primary is held.
Secondary fire remote-detonates rockets already in flight (the basis of "rocket flying"). The rocket is
shootable (it has health) and is consumed-ammo-refunded on explode if the owner is otherwise dry. Active in
every gametype that allows weapons. Primary fire, projectile flight and detonation are server authority; the
flying rocket's model/trail/fly-sound and impact FX are presentation; bot aim/auto-detonation is authority but
not modeled per-weapon in the port.

## Base algorithm (authoritative)

### Fire / launch  (`devastator.qc:W_Devastator_Attack`, `wr_think`)
- **Trigger:** `wr_think` (SVQC, per W_WeaponFrame tick). On `fire & 1`, when `(rl_release || guidestop)` AND
  `weapon_prepareattack(refire)` succeeds → `W_Devastator_Attack`, then `weapon_thinkf(WFRAME_FIRE1, animtime, w_ready)`,
  clear `rl_release`. When primary is NOT held, set `rl_release = 1` (re-arm latch). The `rl_release` latch makes
  the primary require a fresh press between launches; holding fire instead guides the rocket.
- **Algorithm:** `W_DecreaseAmmo(ammo)`; `W_SetupShot_ProjectileSize(actor, '-3 -3 -3','3 3 3', false, 5, SND_DEVASTATOR_FIRE, CH_WEAPON_A, damage, m_id)`
  (recoil 5, fire sound on CH_WEAPON_A); `W_MuzzleFlash(MDL_DEVASTATOR_MUZZLEFLASH=models/flash.md3, EFFECT_ROCKET_MUZZLEFLASH)`.
  Spawn missile (WarpZone_RefSys_SpawnSameRefSys): classname "rocket", owner/realowner = actor, register as
  `actor.(weaponentity).lastrocket`. `spawnshieldtime = (detonatedelay>=0 ? time+detonatedelay : -1)`,
  `pushltime = time+guidedelay`. `takedamage=DAMAGE_YES`, `health = health`, `damageforcescale`, `event_damage = W_Devastator_Damage`,
  `damagedbycontents`. `MOVETYPE_FLY`, `PROJECTILE_MAKETRIGGER` (SOLID_CORPSE + hitmask SOLID|BODY|CORPSE),
  `setsize '-3 -3 -3'..'3 3 3'`, `setorigin(w_shotorg - v_forward*3)`. Velocity `W_SetupProjVelocity_Basic(speedstart, 0)`.
  `cnt = time+lifetime` (death timer), `rl_detonate_later = (fire & 2)`, `missile_flags = MIF_SPLASH`,
  `bot_dodgerating = damage*2`. `CSQCProjectile(..., clientanimate = (guiderate==0 && speedaccel==0), PROJECTILE_ROCKET)`.
  `MUTATOR_CALLHOOK(EditProjectile, actor, missile)`. Think runs immediately if due.
- **Constants:** `ammo 4`, `animtime 0.4`, `refire 1.1`, `speedstart 1000`, `lifetime 10`, `health 30`,
  `damageforcescale 0`, `guidedelay 0.2`. Projectile bbox `'-3 -3 -3'..'3 3 3'`, recoil 5.

### Acceleration  (`devastator.qc:W_Devastator_Think`)
- **Algorithm:** each think `nextthink = time`. If `time > cnt` → set HITTYPE_BOUNCE, `W_Devastator_Explode`.
  Else accelerate: `makevectors(angles)`; `velspeed = speed*W_WeaponSpeedFactor - velocity·v_forward`;
  if `velspeed>0`, `velocity += v_forward * min(speedaccel*W_WeaponSpeedFactor*frametime, velspeed)`.
- **Constants:** `speed 1300`, `speedaccel 1300`. `W_WeaponSpeedFactor = g_weaponspeedfactor` (default 1).

### Laser guiding  (`devastator.qc:W_Devastator_Think`, `W_Devastator_SteerTo`)
- **Trigger / gate:** this rocket is the owner's `lastrocket`, `!rl_release` (primary still held), `!PHYS_INPUT_BUTTON_ATCK2`,
  `guiderate>0`, `time > pushltime`, owner alive.
- **Algorithm:** ramp `f = guideratedelay ? bound(0,(time-pushltime)/guideratedelay,1) : 1`. `velspeed = vlen(velocity)`.
  `makevectors(realowner.v_angle)`; `desireddir = WarpZone_RefSys_TransformVelocity(owner, this, v_forward)`;
  `desiredorigin = WarpZone_RefSys_TransformOrigin(owner, this, owner.origin + owner.view_ofs + dv)` where
  `dv = v_right*-movedir.y + v_up*movedir.z` ONLY when dual-wielding (else `dv = 0`). `olddir = normalize(velocity)`.
  Goal-point steering: `goal = desiredorigin + ((origin - desiredorigin)·desireddir + guidegoal) * desireddir`;
  `newdir = W_Devastator_SteerTo(olddir, normalize(goal-origin), cos(guiderate*f*frametime*DEG2RAD))`.
  `velocity = newdir*velspeed`, `angles = vectoangles(velocity)`. On the first guide tick (`!count`):
  `Send_Effect(EFFECT_ROCKET_GUIDE, origin, velocity, 1)`, `sound(CH_WEAPON_B, SND_DEVASTATOR_MODE)`, `count=1`.
- **SteerTo:** rotate `thisdir` toward `goaldir` by at most the angle whose cosine is maxturn; solves a
  quadratic for the larger blend root; if `thisdir·goaldir > maxturn` return goaldir; if `< -0.9998` refuse (return thisdir).
- **Constants:** `guiderate 90` (deg/s), `guideratedelay 0.01`, `guidegoal 512`, `guidedelay 0.2`, `guidestop 0`.

### Contact / lifetime explosion  (`devastator.qc:W_Devastator_Explode`, `_Touch`)
- **Trigger:** `W_Devastator_Touch` (after WarpZone_Projectile_Touch) or the lifetime branch in Think.
- **Algorithm:** `W_Devastator_Unregister` (clear owner's lastrocket). **Airshot:** if directhit is `DAMAGE_AIM`,
  IS_PLAYER, DIFF_TEAM, `!IS_DEAD`, `IsFlying(directhit)` → `Send_Notification(MSG_ANNCE, ANNCE_ACHIEVEMENT_AIRSHOT)`.
  `event_damage=null; takedamage=NO`. `force_xyzscale = (force_xyscale, force_xyscale, 1)`.
  `RadiusDamageForSource(this, origin, velocity, realowner, damage, edgedamage, radius, NULL, NULL, false, force,
  force_xyzscale, projectiledeathtype, weaponentity, directhitentity)`. **Ammo-refund auto-switch:** if owner's
  active weapon is still this AND `GetResource(ammo_type) < ammo` AND not unlimited-ammo →
  `cnt = m_id; ATTACK_FINISHED = time; m_switchweapon = w_getbestweapon(...)`. `delete(this)`.
- **Constants:** `damage 80`, `edgedamage 40`, `radius 110`, `force 400`, `force_xyscale 1`.

### Remote detonation  (`devastator.qc:W_Devastator_RemoteExplode`, `_DoRemoteExplode`, `wr_think fire&2`)
- **Trigger:** `wr_think` on `fire & 2` (and `m_switchweapon == this`): flag every live owned rocket with
  `rl_detonate_later = true`; if any newly flagged, `sound(CH_WEAPON_B, SND_DEVASTATOR_DET)`. The rocket's own
  Think then calls `W_Devastator_RemoteExplode`.
- **Gate (`W_Devastator_RemoteExplode`):** owner alive AND owner.lastrocket set, AND
  `(spawnshieldtime >= 0 ? time >= spawnshieldtime : vdist(NearestPointOnBox(owner,origin) - origin, >, remote_radius))`.
- **`_DoRemoteExplode`:** Unregister; takedamage=NO. **Rocket jump** (if `remote_jump` allowed via AllowRocketJumping
  hook AND `remote_jump_radius`): WarpZone_FindRadius for the owner; if within radius, optionally bump velocity
  (`vel.xy *= 0.9; vel.z = bound(min, vel.z + add, max)`), then `RadiusDamage(remote_jump_damage,_,remote_jump_radius,
  force=remote_jump_force, deathtype|HITTYPE_BOUNCE)`. Main remote blast: `RadiusDamage(this, realowner,
  remote_damage, remote_edgedamage, remote_radius, (rocketjump?head:NULL), NULL, remote_force, deathtype|HITTYPE_BOUNCE)`
  — plain RadiusDamage wrapper, forcexyzscale '1 1 1' (NO force_xyscale shaping). Same ammo-refund auto-switch. `delete`.
- **Constants:** `detonatedelay 0.02`, `remote_damage 70`, `remote_edgedamage 35`, `remote_radius 110`, `remote_force 300`,
  `remote_jump 0`, `remote_jump_damage 70`, `remote_jump_force 450`, `remote_jump_radius 100`,
  `remote_jump_velocity_z_add 0`, `_z_max 1500`, `_z_min 400`.

### Shoot-down  (`devastator.qc:W_Devastator_Damage`)
- The rocket is `DAMAGE_YES` with `health 30`. `event_damage` takes resource HEALTH; gated by
  `W_CheckProjectileDamage` (g_projectiles_damage). On health<=0 → `W_PrepareExplosionByDamage` → Explode.
  `angles = vectoangles(velocity)` on hit.

### Reload / ammo / misc  (`wr_reload`, `wr_checkammo1/2`, `wr_resetplayer`, `wr_setup`, kill/suicide msgs)
- `wr_setup`: `rl_release = 1`. `wr_reload`: `W_Reload(ammo, SND_RELOAD)` (reload_ammo 0 → no clip reload).
  `wr_checkammo1`: `rockets >= ammo || weapon_load >= ammo`. `wr_checkammo2`: false (no secondary ammo).
  `wr_resetplayer`: clear lastrocket + rl_release on all slots. Kill messages: bounce/splash → MURDER_SPLASH else MURDER_DIRECT.
- **Constants:** `reload_ammo 0`, `reload_time 2`, `pickup_ammo 40`, `switchdelay_drop/raise 0.2`, `weaponthrowable 1`.

### Presentation / networking (CSQC)
- Fly model + trail: `CSQCProjectile(PROJECTILE_ROCKET)` → client `Projectile_Draw`: MDL_PROJECTILE_ROCKET (rocket.md3,
  scale 2, roll 720°/s), EFFECT_TR_ROCKET smoke trail, dynamic light, looping `weapons/rocket_fly` (SND_DEVASTATOR_FLY).
- `wr_impacteffect` (CSQC): `pointparticles(EFFECT_ROCKET_EXPLODE)` at `w_org + w_backoff*2`; `sound(CH_SHOTS, SND_ROCKET_IMPACT)`.
- Guide: `EFFECT_ROCKET_GUIDE` particles + `SND_DEVASTATOR_MODE` on first guide tick. Crosshair `gfx/crosshairrocketlauncher` size 0.7.

## Port mapping
- **Fire / launch:** `Devastator.WrThink` + `Attack`. `rl_release` latch = `WeaponSlotState.RlRelease`; `guidestop`
  read live. `PrepareAttack` is the refire/ammo/READY gate. Ammo via `actor.TakeResource` (port has no
  W_DecreaseAmmo clip handling but reload_ammo=0 so identical). `WeaponFiring.SetupShot` (recoil 5). Missile spawned,
  `Projectiles.MakeTrigger` (SOLID_CORPSE + SOLID|BODY|CORPSE — verified by test). Gate field `ProjectileDetonateTime`
  (QC spawnshieldtime), `LTime` = pushltime. `EditProjectile` hook called before first think. **MuzzleFlash MODEL
  (models/flash.md3) NOT emitted** — only `EffectEmitter.Emit("ROCKET_MUZZLEFLASH")`. Fire sound played from
  `Attack` directly (server-side) rather than via SetupShot's sound arg — same cue `weapons/rocket_fire.wav`.
- **Acceleration:** `OnThink`. Faithful except it **omits `W_WeaponSpeedFactor`** on both `speed` and
  `speedaccel*frametime` (default factor 1 → no observable diff at stock; diverges if g_weaponspeedfactor != 1).
- **Guiding:** `OnThink` + `SteerTo`. Goal-point steering + quadratic solve ported faithfully. `guideratedelay`
  ramp ported. **Dual-wield `dv` movedir offset omitted** (dv always 0 — port is single-slot; QC also zeroes dv
  when not dual-wielding, so faithful for the live single-weapon path). **`EFFECT_ROCKET_GUIDE` particle NOT emitted**
  on first guide tick; `rocket_mode.wav` IS played (CH_WEAPON_B → SoundChannel.Body). WarpZone RefSys transforms
  on the aim ray are dropped (no warpzone steering).
- **Contact/lifetime explosion:** `Explode` → `WeaponSplash.RadiusDamage` with `forceScale = (force_xyscale, force_xyscale, 1)`,
  `directHit` skips LOS. force_xyscale shaping verified by test. **Airshot achievement detection NOT ported**
  (no DAMAGE_AIM/IsFlying check → ANNCE_AIRSHOT never fires from a rocket direct hit, even though the notification
  + sound exist in the registries). **Ammo-refund auto-switch on explode NOT ported** (the `cnt/ATTACK_FINISHED/
  m_switchweapon` block when out of ammo). HITTYPE_BOUNCE on lifetime expiry not set (affects kill-message splash/direct
  classification — port `Explode` always takes the same path).
- **Remote detonation:** `RemoteDetonate` (flag rl_detonate_later via `DeadState=Dying`, det sound) +
  `RemoteExplode`/`DoRemoteExplode`. Gate (timer vs proximity) ported and verified by tests. Remote blast uses plain
  RadiusDamage (no force shaping) — verified. **Rocket-jump variant (remote_jump*) NOT ported** (the AllowRocketJumping
  hook, the velocity bump, the separate remote_jump RadiusDamage). **deathtype HITTYPE_BOUNCE flag NOT applied** to the
  remote blast (kill-message classification). Same ammo-refund auto-switch omission.
- **Shoot-down:** `missile.ProjectileDamage = Explode` + `Health`/`TakeDamage=Yes`. The
  `W_CheckProjectileDamage` (g_projectiles_damage) gate and the incremental TakeResource(HEALTH)/threshold are
  collapsed: any ProjectileDamage call detonates immediately (the rocket has 30hp but the port doesn't subtract —
  it explodes on the first damage event). **DEAD on the live path (verified 2026-06-22):** `DamageSystem.EventDamage`
  dispatches non-players only via `GtEventDamage` (else `PlayerDamage`); a "rocket" has no `GtEventDamage`, and the
  ONLY caller of `Entity.ProjectileDamage` anywhere in `src/` is `BreakablehookMutator` (hooks). So a flying rocket
  can never be shot down in a normal match and its 30hp is never read.
- **Reload / checkammo / resetplayer / kill messages:** `CheckAmmoPrimary` (rockets >= ammo) ported. `WrReload`
  not overridden (base falls back to generic). `wr_resetplayer` (clear lastrocket/rl_release on death/reset) and the
  bounce/splash vs direct **kill-message split NOT ported** here (handled, if at all, by central DeathMessages).
- **Bot aim / auto-detonation (`wr_aim`):** NOT ported per-weapon (no WrAim on any port weapon). Bots fire via a
  separate brain; the rocket-specific lead-prediction, skill-gated detonation heuristics and self/team damage
  avoidance are absent.
- **Presentation:** `game/client/ProjectileCatalog.cs` Rocket entry (rocket.md3 scale 2, spin 720, TR_ROCKET
  smoke, light, `weapons/rocket_fly` loop) + `ProjectileRenderer` Classify("rocket") — live when the host wires the
  renderer. Impact: `EffectEmitter.Emit("ROCKET_EXPLODE")` + `WeaponSplash.ImpactSound("weapons/rocket_impact.wav")`
  emitted SERVER-side and networked. Crosshair image/size not audited here.

## Parity assessment
- **logic:** Core fire/accel/guide/contact-explode/remote-detonate/shoot-down state machine is faithfully ported
  and the live caller chain is confirmed (GameWorld.WeaponThink → WeaponFireDriver.Frame → Devastator.WrThink).
  Gaps: airshot detection, ammo-refund auto-switch, rocket-jump remote variant, HITTYPE_BOUNCE deathtype flagging,
  per-weapon bot aim/detonation, and the incremental shoot-down health subtraction (port explodes on first hit).
- **values:** Every ported balance constant matches Base defaults exactly (ammo 4, damage 80, edgedamage 40,
  radius 110, force 400, speed 1300, speedaccel 1300, speedstart 1000, lifetime 10, health 30, refire 1.1,
  animtime 0.4, guiderate 90, guidegoal 512, guidedelay 0.2, guideratedelay 0.01, detonatedelay 0.02,
  remote_* set). The rocket-jump cvars and force_xyscale Z-handling are correct. `W_WeaponSpeedFactor` term
  dropped (values-faithful at default 1, partial otherwise).
- **timing:** Refire/animtime gated by the shared PrepareAttack/ATTACK_FINISHED machinery; lifetime/guidedelay/
  detonatedelay timers ported off the sim clock. Acceleration is frametime-scaled. guideratedelay ramp ported.
  Largely faithful; the only timing-relevant divergence is the dropped W_WeaponSpeedFactor (no effect at default).
- **presentation:** Flying rocket model/trail/spin/light/fly-sound + impact explosion are wired via the
  projectile catalog and emitted server-side. Gaps: the muzzle-flash MODEL (models/flash.md3) is not attached
  (only the muzzle particle effect), and the EFFECT_ROCKET_GUIDE guide particle is not emitted. Crosshair overlay
  unaudited.
- **audio:** Fire (`rocket_fire`), guide-mode (`rocket_mode`), detonate-cue (`rocket_det`), impact (`rocket_impact`)
  and the looping fly sound (`rocket_fly`) are all wired with the correct cues/channels. Faithful for the audited cues.

## Verification
- `tests/VortexArena.Tests/DevastatorForceXyScaleTests.cs` — contact blast force_xyscale shapes X/Y not Z; remote
  blast ignores it; xyscale 0 is a no-op. PASS-asserted (drives real WrThink → Attack → Explode path).
- `tests/VortexArena.Tests/RocketFlyingGateTests.cs` — detonate-gate field timer-vs-proximity seeding, rocket-flying
  mutator clears the gate, SOLID_CORPSE firer-transparency, end-to-end remote-detonate hold/open. PASS-asserted.
- Live caller chain read: `GameWorld.cs:1182` WeaponFireDriver.Frame; `WeaponFireDriver.cs:155` weapon.WrThink.
- Balance defaults diffed against `bal-wep-xonotic.cfg:428-467` (exact match for ported constants).
- Airshot / ammo-refund / rocket-jump / bot-aim absence verified by grep (no port symbols).
- VERIFIED (2026-06-22): `missile.ProjectileDamage` is NEVER invoked for a flying rocket on the live path — shoot-down
  is dead (the damage pipeline has no ProjectileDamage dispatch for "rocket" entities; only the hook mutator calls it).
- STILL UNVERIFIED: the client-side projectile renderer host wiring at runtime; muzzle-flash model attachment at runtime.

## Open questions
- Shoot-down is confirmed DEAD: to make it live, the damage pipeline must dispatch `Entity.ProjectileDamage` for
  damageable non-player projectiles (today only the hook mutator calls it), and the rocket should then subtract 30hp
  incrementally (QC TakeResource(HEALTH)) rather than detonating on the first damage event.
- Should the airshot achievement (ANNCE_AIRSHOT) be wired for rocket direct hits — the notification + sound exist
  but nothing triggers them from the Devastator?
- Are the ammo-refund auto-switch and the kill-message bounce/splash-vs-direct classification handled centrally
  elsewhere, or genuinely missing?
- Is the rocket-jump remote variant (remote_jump*) intentionally out of scope (stock remote_jump 0 → inert by
  default), or a gap to close for the rocketflying mutator path?

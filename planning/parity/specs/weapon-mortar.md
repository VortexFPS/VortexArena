# Mortar (Grenade Launcher) — parity spec

**Base refs:** `common/weapons/weapon/mortar.qc` · `common/weapons/weapon/mortar.qh` · `server/weapons/tracing.qc` (W_SetupProjVelocity_Explicit, W_SetupShot) · `bal-wep-xonotic.cfg` (g_balance_mortar_*)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Mortar.cs` · `WeaponFiring.cs` (SetupShot, ProjectileVelocity) · `WeaponSplash.cs` (RadiusDamage) · `WeaponFireGate.cs` (PrepareAttack/WrCheckAmmo/WrReload) · `WeaponFireDriver.cs` (WrThink driver) · `VortexArena.Engine/Simulation/MoveTypePhysics.cs` (bounce/gravity/touch) · `Notifications/DeathMessages.cs` (kill/suicide)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Mortar (internal `mortar`, legacy `grenadelauncher`, impulse 4, ammo = rockets) fires bouncing
grenades under gravity. It is a `WEP_FLAG_NOTRUEAIM` splash weapon: grenades launch straight along the
player's forward view vector (`v_forward`), *not* the trueaim-corrected crosshair direction. Primary
(`type 0`) is an impact grenade that detonates immediately on any contact. Secondary (`type 1`) is a
bouncing grenade that detonates a short fuse after its first bounce (or at end of lifetime). A third
`type 2` (stick) mode exists in code but is not used by the default Xonotic balance. Both grenades are
shootable (have HP) and detonate when destroyed by incoming damage. Secondary can optionally remote-
detonate the player's live primary grenades (`remote_detonateprimary`, default off).

## Base algorithm (authoritative)

### Firing — W_Mortar_Attack / W_Mortar_Attack2 (`mortar.qc:W_Mortar_Attack`, `:W_Mortar_Attack2`)
- **Trigger / entry:** sv, from `wr_think` when the fire button is held and `weapon_prepareattack`
  passes the refire gate + ammo check.
- **Algorithm:**
  1. `W_DecreaseAmmo(thiswep, actor, ammo, weaponentity)` — subtract `ammo` rockets (from the clip if
     reloading is enabled, else from the pool).
  2. `W_SetupShot_ProjectileSize(actor, weaponentity, '-3 -3 -3','3 3 3', false, 4, SND_MORTAR_FIRE,
     CH_WEAPON_A, damage, m_id)` — establishes `w_shotorg`/`w_shotdir`, plays the fire sound, kicks
     recoil 4, projectile bbox ±3.
  3. `w_shotdir = v_forward` — override trueaim; grenades fire straight forward.
  4. `W_MuzzleFlash(... EFFECT_GRENADE_MUZZLEFLASH ...)`.
  5. Spawn `grenade` entity: `MOVETYPE_BOUNCE`, `bouncefactor = g_balance_mortar_bouncefactor (0.5)`,
     `bouncestop = g_balance_mortar_bouncestop (0.075)`, bbox ±3, origin = `w_shotorg`,
     `takedamage = DAMAGE_YES`, `health = *_health`, `damageforcescale = *_damageforcescale`,
     `event_damage = W_Mortar_Grenade_Damage`, `damagedbycontents = true`,
     `missile_flags = MIF_SPLASH | MIF_ARC`, `flags = FL_PROJECTILE`, `projectiledeathtype = m_id`
     (secondary also OR `HITTYPE_SECONDARY`).
  6. `W_SetupProjVelocity_UP_PRI/SEC` → velocity = `speed * normalize(v_forward + v_up*(speed_up/speed)
     + z*(speed_z/speed))` then spread (spread 0). `speed_z` default 0. Newton velocity-inheritance OFF.
  7. **Primary:** `cnt = time + lifetime`; `nextthink = time`; `think = W_Mortar_Grenade_Think1`;
     `use = W_Mortar_Grenade_Explode_use`; `touch = W_Mortar_Grenade_Touch1`.
  8. **Secondary:** `nextthink = time + lifetime`; `think = adaptor_think2use_hittype_splash` (i.e. the
     timeout explode); `use = W_Mortar_Grenade_Explode2_use`; `touch = W_Mortar_Grenade_Touch2`.
  9. `CSQCProjectile(gren, true, type 0/2 ? PROJECTILE_GRENADE : PROJECTILE_GRENADE_BOUNCING, true)` —
     network the projectile to clients with the right model/trail.
  10. `MUTATOR_CALLHOOK(EditProjectile, actor, gren)`.
- After firing, `wr_think` schedules `weapon_thinkf(..., WFRAME_FIRE1/2, animtime, w_ready)` (animtime
  0.3 both modes) to return the slot to READY.

### Bounce / impact contact — W_Mortar_Grenade_Touch1 / Touch2 (`mortar.qc:W_Mortar_Grenade_Touch1`)
- **Trigger / entry:** sv engine touch callback while the grenade is solid.
- **Algorithm:**
  - `PROJECTILE_TOUCH(this, toucher)` (warpzone-pass + ignore-owner handling).
  - If `toucher.takedamage == DAMAGE_AIM` (a player/damageable) **or** `type == 0`: detonate
    (`this.use(...)`).
  - `type == 1` (bounce): `spamsound(CH_SHOTS, SND_GRENADE_BOUNCE_RANDOM, VOL_BASE, ATTN_NORM)`,
    `Send_Effect(EFFECT_HAGAR_BOUNCE, origin, velocity, 1)`, OR `HITTYPE_BOUNCE`, `++gl_bouncecnt`.
    Secondary additionally: on the FIRST bounce (`gl_bouncecnt == 1`) and `lifetime_bounce != 0`, set
    `nextthink = time + lifetime_bounce` (0.5 s default) — the short fuse.
  - `type == 2` (stick), only when hitting non-player static geometry (`move_movetype == MOVETYPE_NONE`):
    `spamsound(SND_MORTAR_STICK)`, save `movedir = velocity`, zero velocity, `MOVETYPE_NONE`, gravity 0,
    `UpdateCSQCProjectile`, `solid = SOLID_NOT`, `nextthink = min(nextthink, time + lifetime_stick)`.
- The actual velocity REFLECTION on bounce is the engine `MOVETYPE_BOUNCE` integrator (clip velocity by
  `1 + bouncefactor`; come to rest when speed below `sv_gravity * bouncestop * entgravity` on a floor).

### Lifetime / forced detonation — W_Mortar_Grenade_Think1 (`mortar.qc:W_Mortar_Grenade_Think1`)
- **Primary only.** Runs every frame (`nextthink = time`). If `time > cnt` → OR `HITTYPE_BOUNCE`, explode.
  Else if `gl_detonate_later && gl_bouncecnt >= remote_minbouncecnt` → explode (remote detonation).
- **Secondary** uses the single timed think (the `adaptor_think2use` → Explode2), rescheduled to
  `time + lifetime_bounce` after the first bounce.

### Explosion — W_Mortar_Grenade_Explode / Explode2 (`mortar.qc:W_Mortar_Grenade_Explode`)
- **Trigger:** detonate-on-contact (`use`), lifetime timeout, remote detonation, or being shot down.
- Airshot achievement: if the direct-hit entity is an airborne enemy player (`DAMAGE_AIM`, `IS_PLAYER`,
  `DIFF_TEAM`, `!IS_DEAD`, `IsFlying`) → `Send_Notification(ANNCE_ACHIEVEMENT_AIRSHOT)` to the owner.
- Clear `event_damage`/`takedamage`; if `MOVETYPE_NONE` (stuck) restore `velocity = movedir` so fx/decal
  have a direction. `RadiusDamage(this, realowner, damage, edgedamage, radius, force, deathtype, ...,
  directhitentity)`. `delete(this)`.

### Shoot-down — W_Mortar_Grenade_Damage (`mortar.qc:W_Mortar_Grenade_Damage`)
- Incoming damage subtracts from the grenade's `RES_HEALTH` (gated by `W_CheckProjectileDamage` /
  `g_projectiles_damage`); at ≤0 HP → `W_PrepareExplosionByDamage` → explode.

### Remote-detonate primary (`mortar.qc:wr_think` fire&2 branch)
- If `remote_detonateprimary` (default 0): secondary instead flags every live primary grenade the actor
  owns (`gl_detonate_later = true`); if any found, play `SND_MORTAR_DET` on `CH_WEAPON_B`. The flagged
  grenades detonate via `Think1` once they have bounced `remote_minbouncecnt` times.

### Bot aim (`mortar.qc:wr_aim`)
- Custom per-weapon aim: `bot_aim` with the mode's `speed`/`speed_up`/`lifetime` (ballistic lead);
  randomly toggles between primary and secondary ("grenademooth" 1%/2% chance).

### Other hooks
- `wr_checkammo1/2`: pool **or** clip ≥ `ammo`. `wr_reload`: `W_Reload(min(pri.ammo, sec.ammo), SND_RELOAD)`.
- Forced reload at top of `wr_think`: if `g_balance_mortar_reload_ammo` (default 0) and
  `clip_load < min(pri.ammo, sec.ammo)` → reload and return.
- `wr_suicidemessage`/`wr_killmessage`: `HITTYPE_SECONDARY ? *_BOUNCE : *_EXPLODE`.
- `wr_impacteffect` (cl): `pointparticles(EFFECT_GRENADE_EXPLODE, w_org + w_backoff*2)` + `SND_MORTAR_IMPACT`.

### Constants (Xonotic balance defaults — bal-wep-xonotic.cfg)
| cvar | default | units | side |
|---|---|---|---|
| g_balance_mortar_bouncefactor | 0.5 | factor | shared (engine MOVETYPE_BOUNCE) |
| g_balance_mortar_bouncestop | 0.075 | factor | shared |
| g_balance_mortar_reload_ammo | 0 | rockets (0=off) | authority |
| g_balance_mortar_reload_time | 2 | s | authority |
| g_balance_mortar_pickup_ammo | 40 | rockets | authority |
| g_balance_mortar_switchdelay_drop / _raise | 0.2 / 0.2 | s | authority |
| g_balance_mortar_weaponstart / _weaponstartoverride / _weaponthrowable | 0 / -1 / 1 | — | authority |
| **primary** ammo | 2 | rockets | authority |
| primary animtime | 0.3 | s | authority |
| primary damage | 55 | hp | authority |
| primary edgedamage | 25 | hp | authority |
| primary force | 250 | knockback | authority |
| primary radius | 120 | qu | authority |
| primary refire | 0.8 | s | authority |
| primary health | 15 | hp (shootable) | authority |
| primary damageforcescale | 0 | factor | authority |
| primary lifetime | 20 | s | authority |
| primary lifetime_stick | 0 | s | authority |
| primary speed | 1900 | qu/s | authority |
| primary speed_up | 225 | qu/s | authority |
| primary speed_z | 0 | qu/s | authority |
| primary spread | 0 | — | authority |
| primary type | 0 (impact) | enum | authority |
| primary remote_minbouncecnt | 0 | bounces | authority |
| **secondary** ammo | 2 | rockets | authority |
| secondary animtime | 0.3 | s | authority |
| secondary damage | 55 | hp | authority |
| secondary edgedamage | 30 | hp | authority |
| secondary force | 250 | knockback | authority |
| secondary radius | 120 | qu | authority |
| secondary refire | 0.7 | s | authority |
| secondary health | 30 | hp (shootable) | authority |
| secondary damageforcescale | 4 | factor | authority |
| secondary lifetime | 20 | s | authority |
| secondary lifetime_bounce | 0.5 | s (fuse after 1st bounce) | authority |
| secondary lifetime_stick | 0 | s | authority |
| secondary speed | 1400 | qu/s | authority |
| secondary speed_up | 150 | qu/s | authority |
| secondary speed_z | 0 | qu/s | authority |
| secondary spread | 0 | — | authority |
| secondary type | 1 (bounce) | enum | authority |
| secondary remote_detonateprimary | 0 (off) | bool | authority |

## Port mapping
- **Identity / attributes** → `Mortar.cs` ctor: netname, ammo=Rockets, impulse 4, flags (Normal|
  Reloadable|CanClimb|TypeSplash|NoTrueAim), color, view/world/item models. Faithful.
- **Balance constants** → `Mortar.Configure()` reads every `g_balance_mortar_*` with the correct
  default. Faithful. (`RemoteDetonatePrimary`/`RemoteMinBounceCount` fields exist but `Configure()`
  never assigns them — see gaps.)
- **Fire driver / refire / animtime / ammo gate** → base `Weapon.PrepareAttack` (WeaponFireGate.cs),
  driven live by `WeaponFireDriver.RunSlot` → `WrThink`. `RefireFor`/`AnimtimeFor` return the per-mode
  values. Faithful and live.
- **Firing** → `Mortar.Attack()`: `WeaponFiring.SetupShot` (recoil 4, bbox ±3), `dir = forward`
  (no trueaim), `WeaponFiring.ProjectileVelocity(dir, up, speed, speedUp)` (faithful Explicit port,
  speed_z omitted but defaults 0), spawn `grenade` entity with `MoveType.Bounce`, HP, force scale.
- **Bounce/impact** → `Mortar.OnTouch()`: impact (player or type 0) → Explode; type 1 → bounce sound +
  `++Count`, secondary first-bounce fuse; type 2 → stick. Engine `MoveTypePhysics` reflects velocity.
- **Lifetime / remote detonation** → `Mortar.OnThink()`: explode past deadline; remote latch via
  `DeadState == Dying` + `Count >= RemoteMinBounceCount`.
- **Explosion** → `Mortar.Explode()` → `WeaponSplash.RadiusDamage` (faithful RadiusDamageForSource port)
  + `EffectEmitter.Emit("GRENADE_EXPLODE")` + `WeaponSplash.ImpactSound("grenade_impact")`.
- **Shoot-down** → `gren.ProjectileDamage` delegate → Explode is assigned, but it is **DEAD**: the damage
  pipeline (`DamageSystem.EventDamage`, DamageSystem.cs:294) routes every non-player victim through
  `GtEventDamage` and never invokes `ProjectileDamage` (only `BreakablehookMutator` does). The grenade also
  has no `GtEventDamage`, so it cannot be shot out of the air. Same cross-weapon gap as Seeker/Minelayer.
- **Kill/suicide messages** → `DeathMessages.cs` maps `mortar` + secondary → BOUNCE/EXPLODE notifications,
  which are registered in `NotificationsList.cs`. Faithful (split keys on secondary fire = HITTYPE_SECONDARY).
- **wr_aim (bot)** → NOT IMPLEMENTED per-weapon; bots use the generic `BotAim` lead (BotBrain.cs:334 notes
  the per-weapon `wr_aim` is deferred).

## Parity assessment

### Faithful (live)
- Server-side gameplay: launch direction (no-trueaim), speed/up arc, refire/animtime gates, ammo cost,
  splash damage core/edge/radius/force, primary impact detonation, secondary bounce + 0.5 s post-bounce
  fuse, lifetime timeout, MOVETYPE_BOUNCE physics under gravity. All values match the cfg. The fire path
  is live (WeaponFireDriver → WrThink) and the grenade Touch/Think/bounce are driven by the engine
  `MoveTypePhysics` integrator each tick (SimulationLoop.cs:243).
  - **NOT live:** shootable-grenade detonate-on-destroy — the HP is set but the shoot-down callback is
    never invoked by the damage pipeline (see Shoot-down above; gap below).
- Fire sound (`grenade_fire`), explosion impact sound (`grenade_impact`), bounce sound
  (`grenade_bounce1`), stick sound (`mortar_stick`), remote-detonate sound (`grenade_det`).
- Kill/suicide obituary lines (bounce vs explode).

### Gaps (observable)
1. **CSQC projectile model/trail missing (presentation):** Base networks the grenade as
   `PROJECTILE_GRENADE` (primary/stick) or `PROJECTILE_GRENADE_BOUNCING` (bounce) via `CSQCProjectile`,
   giving a visible spinning grenade model + smoke trail. The port spawns the `grenade` entity but never
   sets a net `Kind`/model (`NetEntityKind.Projectile` is defined but assigned nowhere) — the player sees
   no grenade in flight, no trail, and no bounce-vs-impact model distinction.
1b. **Grenade shoot-down is DEAD (logic/liveness):** `gren.ProjectileDamage = Explode` is assigned, but
   `DamageSystem.EventDamage` (DamageSystem.cs:294) routes every non-player victim through `GtEventDamage`
   and returns — it never invokes `ProjectileDamage` (only `BreakablehookMutator` does, for hooks). The
   grenade sets no `GtEventDamage`, so a player cannot shoot a grenade out of the air; primary/secondary
   `*_health` are inert. This is the same cross-weapon shoot-down gap documented for Seeker and Minelayer.
   (Even if wired, the callback detonates on any damage rather than depleting HP — a secondary divergence.)

2. **Bounce effect missing (presentation):** `OnTouch` type-1 plays the bounce sound but does NOT emit
   `EFFECT_HAGAR_BOUNCE` (Base `Send_Effect(EFFECT_HAGAR_BOUNCE, ...)` on every bounce). No spark puff.
3. **Airshot achievement not implemented (audio/notification):** Base announces
   `ANNCE_ACHIEVEMENT_AIRSHOT` to the owner when a grenade directly hits an airborne enemy. `Explode()`
   has no direct-hit airborne check and never sends the announcement.
4. **Remote-detonate cvars never seeded (logic/values):** `RemoteDetonatePrimary` and
   `RemoteMinBounceCount` are read in `WrThink`/`OnThink` but `Configure()` never assigns them from
   `g_balance_mortar_secondary_remote_detonateprimary` / `g_balance_mortar_primary_remote_minbouncecnt`.
   They default to `false`/`0`. Matches the DEFAULT balance (remote_detonateprimary 0, minbouncecnt 0),
   so no observable difference in stock play, but the feature is unconfigurable and would silently ignore
   a server that enables it.
5. **Per-entity bouncefactor/bouncestop are dead (logic, latent):** `MoveTypePhysics.cs` hardcodes the
   MOVETYPE_BOUNCE `bf = 0.5` / `bouncestop = 0.075` and ignores the entity's `BounceFactor`/`BounceStop`
   fields that `Mortar.Attack` sets. The mortar's configured values equal the hardcoded defaults, so the
   bounce behaves correctly today — but any server changing `g_balance_mortar_bouncefactor`/`_bouncestop`
   would see no effect.
6. **Sticky (type 2) "restore velocity on explode" not modeled (presentation/logic, default-inactive):**
   For a stuck grenade, Base restores `velocity = movedir` before exploding so explosion fx/decals get a
   direction; the port zeroes velocity and never restores it. Stock balance never uses type 2, so this is
   inert by default.
7. **Clip-aware ammo + forced reload not ported (logic, default-inactive):** `wr_checkammo` only checks
   the resource pool, not `weapon_load[]` (clip); `Attack` always subtracts from the pool; the forced-
   reload-at-top-of-wr_think is absent. `g_balance_mortar_reload_ammo` defaults to 0 (reloading off), so
   no observable effect in stock play.
8. **Stuck `+= HITTYPE_BOUNCE` on primary timeout deathtype not ORed (logic, cosmetic):** Base ORs
   `HITTYPE_BOUNCE` on a primary that times out / remote-detonates. The port does not. Affects only the
   internal deathtype bit, which the obituary keys on HITTYPE_SECONDARY (not BOUNCE), so no message change.
9. **Secondary first-bounce fuse guard (timing, sub-threshold):** Base sets the post-bounce fuse
   unconditionally (`nextthink = time + lifetime_bounce`); the port only sets it `if (fuse < deathTime)`.
   With defaults (0.5 ≪ 20) the guard always passes — no observable difference.
10. **Per-weapon bot aim (`wr_aim`) deferred (bot):** bots use the generic aim lead, not the mortar's
    ballistic grenade-lead + primary/secondary "grenademooth" toggle. Bots are weaker with the mortar.

### Intended divergences
- None specific to the mortar. (The systemic `FL_PROJECTILE → EntFlags.Item` mapping and server-side
  explosion emission are shared port conventions documented elsewhere, not mortar decisions.)

## Verification
- **Values:** read `bal-wep-xonotic.cfg` lines 130-175 and matched every `Mortar.Configure()` default;
  `WeaponBalanceTests.cs:132-134` asserts loaded-cfg `Primary.Damage == 55`. (high)
- **Logic / liveness:** traced `WeaponFireDriver.RunSlot` → `WrThink` (live), base `PrepareAttack`
  (ammo/refire/animtime), `MoveTypePhysics.RunEntity` Bounce case (gravity + bounce + touch), and
  `WeaponSplash.RadiusDamage` (faithful RadiusDamageForSource port). (high)
- **Presentation gaps:** confirmed `NetEntityKind` is defined but never assigned (no projectile model),
  `OnTouch` type-1 emits no `EFFECT_HAGAR_BOUNCE`, and `Explode` has no airshot check, by reading
  `Mortar.cs`, `NetEntity.cs`, and grepping the engine/net for `NetEntityKind.`/`AIRSHOT`. (high)
- **Remote-detonate seeding:** confirmed `Configure()` does not assign `RemoteDetonatePrimary`/
  `RemoteMinBounceCount` by reading `Mortar.cs:65-104`. (high)
- **Bouncefactor dead field:** confirmed `MoveTypePhysics.cs:161-176` hardcodes 0.5/0.075 and no engine
  code reads `ent.BounceFactor`/`BounceStop`. (high)
- No live in-game playtest was performed for the visual/audio gaps (model/trail/bounce-fx/airshot);
  those are static-analysis conclusions. (presentation marked accordingly)

## Open questions
- Does any client-side path render the spawned `grenade` entity with a default model (e.g. via
  `e.Model` if set elsewhere), or is it truly invisible in flight? `Mortar.Attack` never sets `Model`
  and no `NetEntityKind` is assigned, so the working assumption is invisible — needs a runtime check.
- Is the engine `MoveType.Bounce` integrator intended to grow per-entity bouncefactor support, or is the
  hardcoded mortar-tuned default a deliberate simplification? (Affects whether gap #5 is a bug or
  intended divergence.)

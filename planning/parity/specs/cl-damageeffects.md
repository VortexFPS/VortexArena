# Client damage effects (DamageInfo) — parity spec

**Base refs:** `common/effects/qc/damageeffects.qc` + `.qh`
**Port refs:** `src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs` (no-op seam + blood),
`src/VortexArena.Common/Gameplay/Weapons/WeaponFiring.cs` / `WeaponSplash.cs` (server-side impact FX),
`game/client/EffectSystem.cs` / `ModelGibs.cs` (raptor shellfrags), `game/menu/dialogs/DialogSettingsEffects.cs` (dead cvar UI)
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-07-02

## Overview

`damageeffects.qc` is Base's networked per-blast damage event. Whenever the server deals
edge damage (`RadiusDamage`, plus hitscan call sites like vaporizer/rifle), it links a
short-lived `ENT_CLIENT_DAMAGEINFO` entity broadcasting ~16 quantized bytes: deathtype, floored
origin, core/edge/radius bytes, force direction (3×char) + length (byte, /4), the victim species
(blood type) and the owner colormap. The CSQC handler then does *everything visible about a hit*
locally: pushes client-only entities (gibs/casings) with proper falloff, spawns attached
lingering damage effects (burning players), selects team-tinted particles, and dispatches the
per-weapon / per-vehicle / per-turret impact particles + sounds at the surface.

## Base algorithm (authoritative)

### Server broadcast  (`Damage_DamageInfo` 35, `Damage_DamageInfo_SendEntity` 7)
- **Trigger:** server, from `RadiusDamageForSource` and hitscan weapon fire paths.
- **Encoding:** deathtype short (bit 0x8000 = silent when `!sound_allowed(MSG_BROADCAST, dmgowner)`);
  origin floored per component; `bound(1,dmg,255)`, `bound(0,rad,255)`, `bound(1,edge,255)` bytes;
  force dir mapped [-1,1]→[-127,127] (3 WriteChar) + `bound(0, dmg_force/4, 255)` byte (max 1020);
  species = `bloodtype & BITS(4)`, BIT(7) = negative (attractive) radius; colormap =
  `teamplay ? dmgowner.team : dmgowner.clientcolors`. Net_LinkEntity with 0.2 cull radius.

### Client radius application  (NET_HANDLE 259–305, qh 22–26)
- Decode force dir (`'0 0 0'` → `'0 0 -1'`), `vlen_force = byte*4 + 2` (the +2 keeps decal
  tracelines working).
- `FOREACH_ENTITY_RADIUS(w_org, rad + MAX_DAMAGEEXTRARADIUS=16)` over client entities
  (skip attached/pure): distance = nearest point on box, minus per-entity
  `damageextraradius` (clamped 2..16); falloff `thisdmg = dmg + (edge-dmg)*frac`; force scaled by
  `thisdmg/dmg`, direction `normalize(it.origin - w_org)`, `forcemul = -1` for negative radius.
- Entities with `.damageforcescale` get `velocity += damage_explosion_calcpush(...)` +
  `UNSET_ONGROUND` — this is what scatters gibs/casings; `.event_damage` is called (gib fragility);
  `it.silent = 1` when the broadcast was silent. Sets `hitplayer` if a non-local player model was hit.

### Team tint  (243–257)
- `tcolor = (dmg_colors-1)*0x11` in teamplay (else raw), `| BIT(10)` RENDER_COLORMAPPED; sets the
  globals `particles_colormin/max = colormapPaletteColor(...)` consumed by weapon impact effects.

### Attached damage effects  (`DamageEffect` 131–203, `DamageEffect_Think` 84–114)
- Gates: `cl_damageeffect` (default **1**; 1 = skeletal only, 2 = all models), `cl_gentle`,
  `cl_gentle_damage`; victim must have modelindex+drawmask.
- Picks the skeletal bone nearest the hit (blacklist: `master`, `knee_L/R`, `leg_L/R`); limit
  `cl_damageeffect_bones` (**5**) concurrent effects on rigged meshes, 1 on non-skeletal (and only
  if `cl_damageeffect >= 2`).
- Lifetime = `bound(3, damage * 0.1, 6)` s (`_lifetime_min/_lifetime/_lifetime_max`).
- Effect name `damage_<wep.netname>` (or `wr_damageeffect_getname`); `WEP_FLAG_BLEED` weapons get
  the species prefix (`species_prefix` 116: alien_/robot_/animal_), and non-players return
  (objects don't bleed).
- Think: repeat every `cl_damageeffect_ticrate` (**0.1** s), × `total_damages` when
  `cl_damageeffect_distribute` (**1**); expires at `cnt`, on gib/disconnect
  (`!modelindex||!drawmask`), and on the dead→alive edge (respawn must not inherit corpse
  flames); own effects hidden in first person (`ISPLAYER_LOCAL && !chase_active`);
  spawns `__pointparticles(effect, gettaginfo(this,0), '0 0 0', 1)`.

### Impact dispatch  (307–461)
- **Vehicles** (`DEATH_ISVEHICLE`): backoff traceline (±16 along force dir, MOVE_NOMONSTERS),
  `setorigin(this, w_org + w_backoff*2)` for sound; switch on `DEATH_ENT`: spiderbot minigun =
  ric + SPIDERBOT_MINIGUN_IMPACT; rockets = SND_ROCKET_IMPACT + *_ROCKET_EXPLODE; racer gun /
  raptor cannon = SND_LASERIMPACT + RACER_IMPACT / RAPTOR_CANNON_IMPACT; raptor fragment = 3×
  `RaptorCBShellfragToss` at 120° yaw steps + RAPTOR_BOMB_SPREAD + rocket-impact sound; bumblebee
  gun = SND_VEH_BUMBLEBEE_IMPACT + BIGPLASMA_IMPACT; `*_DEATH` = EXPLOSION_BIG at ATTEN_LOW.
- **Turrets** (`DEATH_ISTURRET`): ewheel = laser + BLASTER_IMPACT; flac = HAGAR_EXPLODE +
  SND_FLACEXP_RANDOM; mlrs/hk/walker-rocket/hellion = ROCKET_EXPLODE; machinegun/walker-gun =
  ric + MACHINEGUN_IMPACT; plasma = SND_TUR_PLASMA_IMPACT + ELECTRO_IMPACT; walker melee =
  TE_SPARK; tesla = `te_smallflash`; phaser/crush = nothing.
- **Weapons:** `MUTATOR_CALLHOOK(DamageInfo, ...)` (client nades), then unless
  `DEATH_ISSPECIAL`, and only `if(!hitplayer || rad)` (hitscan that hit a player shows no ground
  impact): `w_random = prandom()`; backoff traceline with a start-solid retry from `-dir*40`;
  suppress everything on `Q3SURFACEFLAG_SKY`; else `Weapon_ImpactEffect` hook →
  `hitwep.wr_impacteffect(hitwep, this)` (per-weapon particles + CH_SHOTS sound + decal).

## Port mapping

| Base | Port |
|---|---|
| `Damage_DamageInfo` broadcast | **No-op** `DamageSystem.DamageInfoNetwork` (DamageSystem.cs:1082) — a named placeholder with ZERO call sites (RadiusDamage never calls it). Deliberate: effects are emitted server-side over the EFF_NET channel (`EffectEmitter` → `ServerNet.EffectNetSink`, live — see fx-effectinfo). |
| Client radius push + `event_damage` on client entities | **NOT IMPLEMENTED** (gibs/casings never shoved by blasts) |
| Team-tinted impact particles | **NOT IMPLEMENTED** (wire supports color min/max; no impact caller passes it) |
| `DamageEffect` attached particles + lifecycle | **NOT IMPLEMENTED**; `cl_damageeffect` slider exists in DialogSettingsEffects.cs:127 but the cvar is dead |
| `species_prefix` / WEP_FLAG_BLEED | **NOT IMPLEMENTED** — all blood is generic `BLOOD` (DamageSystem.cs:536 `TeBlood`) |
| Per-weapon `wr_impacteffect` dispatch | Server-side per-weapon emission, **live**: `WeaponFiring.BulletImpactFx` (hitscan, org+backoff*2, backoff*1000), `WeaponSplash.ImpactSound(At)` + `EffectEmitter.Emit` at each explode (e.g. Mortar.cs:343-344) |
| Vehicle DEATH_VH switch | Only raptor bomb-spread burst is live (Raptor.cs:640 → EffectSystem.cs:474 → ModelGibs.TossShellfrags); all other vehicle impact FX/sounds missing (RACER_IMPACT etc. registered in EffectsList.cs:160-185, zero emit sites) |
| Turret DEATH_TURRET switch | **NOT IMPLEMENTED** — turret explosions (FlacTurret.cs:144/267, GuidedProjectile.cs:100, TurretSpawn.cs:165) apply damage with no impact FX/sound |

## Parity assessment

- **Logic:** the wire entity and everything only it enabled (attached effects, client push,
  species, tint) are missing; the weapon impact dispatch is live but re-homed server-side with
  two gate divergences (sky suppresses only the ric, not the puff; no `!hitplayer` suppression —
  the WorldOnly impact trace paints the wall behind a hit player).
- **Values:** where ported (impact origin/velocity math, sound channel/volume), values match QC.
- **Presentation:** worst gaps are the burning-victim attached effects, turret/vehicle impact FX,
  species blood, team tint.
- **Audio:** weapon impact sounds live (ImpactSound at explode sites); turret and vehicle impact
  sounds entirely missing; raptor burst missing its rocket-impact crack.
- **Liveness:** server-side emission path verified live (ServerNet.EffectNetSink, cross-checked
  in fx-effectinfo.emit.send_effect); `cl_damageeffect` is a dead menu setting.
- **Intended divergences:** the DamageInfo wire mechanism itself (server-side EFF_NET emission
  replaces client-computed FX — headless-gameplay architecture); the gate mismatches are NOT
  intended.

## Verification

- Base file read end-to-end at the pinned rev; port claims each cite file:line above.
- Live weapon path: Machinegun.cs:398-408 (every shot), Mortar.cs:343-344 (every explode);
  sink liveness inherited from fx-effectinfo.emit.send_effect (verified there).
- Vehicle/turret absences: exhaustive grep of `EffectEmitter.Emit|ImpactSound` under
  `src/.../Vehicles/` and `src/.../Turrets/`; impact-effect name grep over src/ + game/.
- `cl_damageeffect` deadness: repo-wide grep — only the menu writer + docs.
- Shellfrag burst: VehicleRuntimeTests.cs:410.

## Open questions

- Does the faithful particle backend consume per-emission tint (EFF_NET_COLOR_*) for the impact
  effect blocks that reference team colors (crylink)? (Affects how team_tint should be fixed.)
- Where should attached damage effects live in the port architecture — a client consumer keyed
  off a new per-hit event, or server-side emission at the victim? (Base semantics — first-person
  self-hide, respawn cleanup, chase_active — need client knowledge, arguing for a client system.)

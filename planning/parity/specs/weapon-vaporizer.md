# Vaporizer — parity spec

**Base refs:** `common/weapons/weapon/vaporizer.{qc,qh}` · `server/weapons/tracing.qc` (`FireRailgunBullet`, `W_SetupShot`) · `common/mutators/mutator/instagib/sv_instagib.qc` · `common/mutators/mutator/rocketminsta/sv_rocketminsta.qc` · balance `bal-wep-xonotic.cfg` (`g_balance_vaporizer_*`), `mutators.cfg` (`g_instagib*`, `g_rm*`)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Vaporizer.cs` · `WeaponFiring.cs` (`FireRailgunBullet`/`SetupShot`/`Headshot`) · `WeaponSplash.cs` · `Mutators/InstagibMutator.cs` · `Mutators/RocketMinstaMutator.cs` · `game/client/WeaponFireSounds.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Vaporizer (Nexuiz "MinstaNex") is a hitscan rail superweapon. Primary fire is an instant beam from the
muzzle to `max_shot_distance` that pierces every entity in its path, dealing a large fixed damage chunk
(`g_balance_vaporizer_primary_damage 150`, or — when the cvar is `<= 0` — a `10000` instakill) with knockback
`force 800` and optional distance falloff (all falloff cvars default 0 = off). Secondary fire is a
Blaster-style knockback laser. It is the InstaGib loadout weapon: under `g_instagib` each shot costs exactly
1 cell, every body shot one-shots (armor acts as extra "lives"), and the kill always gibs. A sub-mode
`g_rm` (Rocket Minsta) adds a Devastator-style explosion at the beam endpoint and turns the secondary into a
fan of bouncing laser bolts. Authority/shared logic lives in `vaporizer.qc` (SVQC) + `tracing.qc`;
presentation (beam particle, muzzle flash, impact effect, reticle, achievements) is CSQC.

## Base algorithm (authoritative)

### Primary attack — rail beam  (`vaporizer.qc:W_Vaporizer_Attack`)
- **Trigger:** SVQC `wr_think`, `(fire & 1)` while `GetResource(CELLS)` (or `!g_rm`) and not weapon-locked,
  gated by `weapon_prepareattack(... refire)`.
- **Algorithm:**
  1. `flying = IsFlying(actor)` captured BEFORE the trace (FireRailgunBullet clobbers trace globals).
  2. `vaporizer_damage = (primary_damage > 0) ? primary_damage : 10000`.
  3. `W_SetupShot(actor, weaponentity, antilag=true, recoil=0, SND_Null, CH_WEAPON_A, vaporizer_damage, m_id)`
     — note recoil is **0** and the fire sound is suppressed here.
  4. `sound(actor, CH_WEAPON_A, SND_VAPORIZER_FIRE, VOL_BASE*0.8, ATTEN_NORM)` — played manually at 0.8 volume
     (so Strength does not add the strength fire-sound).
  5. `yoda = 0; impressive_hits = 0;` then `FireRailgunBullet(actor, weaponentity, w_shotorg,
     w_shotorg + w_shotdir*max_shot_distance, vaporizer_damage, headshot_notify=true, force,
     falloff_mindist, falloff_maxdist, falloff_halflife, falloff_forcehalflife, m_id)`.
  6. `W_MuzzleFlash(...)`, `SendCSQCVaporizerBeamParticle(actor, impressive_hits)`.
  7. Achievements: if `yoda && flying` → ANNCE_YODA; if `impressive_hits && actor.vaporizer_lasthit` →
     ANNCE_IMPRESSIVE (every *second* qualifying hit — toggled via `vaporizer_lasthit`).
  8. If `g_rm` and the endpoint surface is not sky/noimpact → `W_RocketMinsta_Explosion(actor, trace_endpos)`.
  9. `W_DecreaseAmmo(thiswep, actor, (g_instagib ? 1 : primary_ammo), weaponentity)`.
- **Constants:** `primary_damage 150`, `primary_force 800`, `primary_refire 1`, `primary_animtime 0.3`,
  `primary_ammo 10`, all four `damagefalloff_* 0`, `pickup_ammo 30`, `switchdelay_raise/drop 0.2`,
  `reload_ammo 0`, `reload_time 0`, `weaponthrowable 1`, `weaponstart 0`, `weaponstartoverride -1`.
  `max_shot_distance = 32768`. Sound `weapons/minstanexfire.wav` at `VOL_BASE*0.8`.

### FireRailgunBullet — piercing beam  (`tracing.qc:FireRailgunBullet`)
- **Shared/authority.** Trace repeatedly start→end; each non-world entity hit is recorded
  (`railgunhitloc`, `railgundistance`) and made `SOLID_NOT` so the next trace passes through; a `SOLID_BSP`
  brush stops the beam. Solidity is restored, then each recorded entity takes
  `Damage(bdamage * ExponentialFalloff(mindist,maxdist,halflifedist,dist))` with force
  `railgunforce * ExponentialFalloff(...,forcehalflifedist,...)`. Plays a per-nearby-client whoosh
  (`NEXWHOOSH`, VOL_BASEVOICE, ATTEN_IDLE). Headshot (head-AABB test, only living unfrozen players) →
  ANNCE_HEADSHOT when `headshot_notify`. Accuracy: `accuracy_add(hit = min(bdamage, totaldmg))`.
- **Headshot box** (`tracing.qc:Headshot`): horizontal 0.6× body box, vertical
  `1.3*view_ofs_z - 0.3*maxs_z` up to `maxs_z`, centred on (antilagged) origin.

### Secondary — Blaster laser / RM laser fan  (`vaporizer.qc:wr_think (fire&2)`, `W_RocketMinsta_Attack`)
- `(fire & 2)` OR `(fire & 1 && no cells && g_rm)`.
- If `(g_rm && g_rm_laser) || g_rm_laser == 2` → RM laser barrage with rapid-fire ladder
  (`jump_interval`/`jump_interval2`, `g_rm_laser_refire 0.7`, `g_rm_laser_rapid 1`,
  `g_rm_laser_rapid_delay 0.6`, `g_rm_laser_rapid_refire 0.35`). Mode 0 fires `g_rm_laser_count 3` bouncing
  `MOVETYPE_BOUNCEMISSILE` plasma bolts (`g_rm_laser_speed 6000`, `g_rm_laser_spread 0.05`,
  `g_rm_laser_zspread 0`, `g_rm_laser_lifetime 30`), each doing `g_rm_laser_damage 80 / count` /
  `g_rm_laser_force 400 / count` over `g_rm_laser_radius 150` on touch (and on think/timeout, splash).
  Uses Electro/Crylink sounds + Electro muzzle effect. Mode 1 (rapid) fires a single bolt.
- Else (plain instagib) — manual `jump_interval` gate at `WEP_CVAR_PRI(BLASTER, refire) * W_WeaponRateFactor`,
  optional `WEP_CVAR_PRI(BLASTER, ammo)` decrement, then `W_Blaster_Attack` (the Blaster's primary bolt).
  Blaster defaults: `primary_refire 0.7`, `primary_animtime 0.1`, `primary_speed 6000`,
  `primary_lifetime 5`, `primary_ammo 0`.

### RocketMinsta explosion  (`vaporizer.qc:W_RocketMinsta_Explosion`)
- `RadiusDamage(dmgent, actor, g_rm_damage 70, g_rm_edgedamage 38, g_rm_radius 140, force g_rm_force 400,
  WEP_DEVASTATOR.m_id | HITTYPE_SPLASH)`. Accuracy credited to DEVASTATOR.

### Ammo / checkammo / reload  (`vaporizer.qc` methods)
- `wr_checkammo1`: have `>= (g_instagib ? 1 : primary_ammo)` cells (counting clip load).
- `wr_checkammo2`: `!BLASTER.ammo` → always true, else `>= BLASTER.ammo`.
- `wr_reload`: reload `min(vaporizer_ammo, BLASTER.ammo)` (or `vaporizer_ammo`) with SND_RELOAD; the forced
  reload at the top of `wr_think` triggers when `clip_load < min(...)` and `reload_ammo` is set.
- `wr_setup`/`wr_resetplayer`: `vaporizer_lasthit = 0` (the impressive-every-2nd toggle).
- `wr_aim` (bot): primary if ammo, else secondary blaster aim.
- `wr_suicidemessage` WEAPON_THINKING_WITH_PORTALS; `wr_killmessage` WEAPON_VAPORIZER_MURDER.

### InstaGib integration  (`sv_instagib.qc`)
- `SetStartItems`: 100 health, 0 armor, `g_instagib_ammo_start 10` cells, `start_weapons = WEPSET(VAPORIZER)`,
  `IT_UNLIMITED_SUPERWEAPONS`.
- `Damage_Calculate`: a VAPORIZER hit on a player with armor decrements armor (a "life"),
  `frag_damage = 0`, CENTER_INSTAGIB_LIVES_REMAINING; friendly push gated by `g_instagib_friendlypush 1`.
  Blaster damage/force nullified unless `g_instagib_blaster_keepdamage/keepforce`.
- `PlayerDies`: a VAPORIZER death forces `damage = 1000` (always gib).
- `instagib_ammocheck` (PlayerPreThink): cell-less player bleeds out (`instagib_countdown`: 5 or 10 dmg/s,
  DEATH_NOAMMO, announcer countdown). PlayerRegen disabled. PlayerSpawn `EF_FULLBRIGHT`. ExtraLife pickup
  grants `g_instagib_extralives 1` armor; VaporizerCells pickup refills health to 100.

### Presentation (CSQC, `vaporizer.qc` #ifdef CSQC)
- `SendCSQCVaporizerBeamParticle` / NET_HANDLE(TE_CSQC_VAPORBEAMPARTICLE): draws the rail beam as a
  cylindric line (gauntletbeam if hit else lgbeam), team-coloured (`cl_vaporizerbeam_teamcolor 1`),
  `cl_vaporizerbeam_lifetime 0.8`, `cl_vaporizerbeam_colorboost 0.7`; optional trail particles
  (`cl_vaporizerbeam_particle 0`).
- `wr_impacteffect`: `EFFECT_VORTEX_IMPACT` pointparticles + `SND_VAPORIZER_IMPACT` (`neximpact`).
- `wr_zoom`: zoom-only (no weapon scope image). `wr_init`: precache reticle `gfx/reticle_nex`.
- Crosshair `gfx/crosshairminstanex` size 0.6. Weapon color `'0.592 0.557 0.824'`. Muzzle effect
  `EFFECT_VORTEX_MUZZLEFLASH`.

## Port mapping
- **Primary attack** → `Vaporizer.Attack`: damage `>0?:10000`, `SetupShot`, `FireRailgunBullet(headshotNotify:true)`,
  fire sound, RM explosion, ammo decrement. NOT passed: `wep:`/`maxDamage:` (no accuracy "fired" credit),
  recoil arg (correctly 0). Falloff args NOT passed (all 0 in Base → harmless today). NO muzzle flash,
  NO beam particle, NO yoda/impressive achievements (explicitly deferred in a code comment).
- **FireRailgunBullet/SetupShot/Headshot/falloff** → `WeaponFiring.cs` — full, faithful headless ports
  (pierce loop, restore solidity, exponential falloff for damage+force, head-AABB headshot + announce,
  antilag begin/end, warpzone-aware sweep, accuracy credit `min(bdamage,totaldmg)`). The nearby-whoosh
  sound is deferred.
- **Secondary** → `Vaporizer.WrThink`/`BlasterSecondary`/`RocketMinstaLaserBarrage`. Blaster path fires
  `Blaster.FirePrimaryDirect` past the Blaster's own gate; RM laser fan spawns bouncing plasma bolts with
  touch+think splash. RM rapid-fire ladder (`jump_interval2`, `g_rm_laser_rapid*`) is NOT modeled — only the
  single `jump_interval` at `g_rm_laser_refire`. Blaster secondary uses a hardcoded `0.7` refire (matches
  Base default) but omits `W_WeaponRateFactor` and the optional Blaster-ammo decrement.
- **RM explosion** → `Vaporizer.RocketMinstaExplosion` → `WeaponSplash.RadiusDamage`, but deathType is the
  **Vaporizer** id, not DEVASTATOR (Base tags `WEP_DEVASTATOR | HITTYPE_SPLASH`). Constant fallbacks differ
  (see below).
- **Ammo** → `actor.TakeResource(AmmoType, instagib?1:primary_ammo)`; `CheckAmmoPrimary` present. `wr_checkammo2`,
  `wr_reload`, the forced-reload branch, `wr_aim` (bot) — NOT ported on this class.
- **Kill/suicide messages** → `DeathMessages.SelectKillMessage`/`SelectSuicideMessage` (centralized string table keyed by
  weapon NetName): `WEAPON_VAPORIZER_MURDER` (kill) and `WEAPON_THINKING_WITH_PORTALS` (suicide, via the generic default)
  ARE ported — just not as methods on the `Vaporizer` class. [VERIFIER 2026-06-22: corrects the original draft, which
  listed these as "not ported".]
- **InstaGib** → `InstagibMutator.cs` — faithful: start loadout, armor-as-lives, blaster nullify, gib,
  ammocheck bleed-out, regen off, fullbright, extralife/cells pickups. Routed on the live PlayerSpawn/
  PreThink/Damage hooks.
- **Rocket Minsta** → `RocketMinstaMutator.cs` (self/round Devastator+Electro damage nullify + gib) — note
  this is the Devastator/Electro side, NOT the Vaporizer-primary explosion (that lives in `Vaporizer.cs`).
- **Presentation:** fire sound wired (`WeaponFireSounds["vaporizer"]`); generic reticle path exists. Beam
  particle, muzzle flash, impact effect (VORTEX_IMPACT), impact sound (neximpact), achievements — all MISSING
  for the Vaporizer (the sister Vortex DOES emit impact effect+sound+recoil; the Vaporizer does not).

## Parity assessment
- **Logic (rail beam + pierce + falloff + headshot):** faithful and live. The heavy lifting is in the shared
  `FireRailgunBullet`, which is a careful port. Vaporizer drives it with the correct damage/force/headshot args.
- **Values — primary:** faithful (150/800/1/0.3/10 all read from `g_balance_vaporizer_*` with correct fallbacks;
  falloff 0).
- **Values — RM / instagib defaults:** PARTIAL. `Vaporizer.RocketMinstaExplosion` and `RocketMinstaLaserBarrage`
  use cvar **fallbacks that do not match Base defaults**: `g_rm_damage` fallback 35 vs Base 70;
  `g_rm_edgedamage` 15 vs 38; `g_rm_radius` 90 vs 140; `g_rm_force` 200 vs 400; `g_rm_laser_damage` 25 vs 80;
  `g_rm_laser_radius` 80 vs 150; `g_rm_laser_force` 300 vs 400; `g_rm_laser_speed` 5000 vs 6000;
  `g_rm_laser_count` 1 vs 3; `g_rm_laser_lifetime` 0.3 vs 30; `g_rm_laser_refire` 0.7 (matches). These
  fallbacks only bite when the cvar is unset/0; if `mutators.cfg` is loaded the live cvar wins, but the
  embedded defaults are wrong and would silently halve RM damage on a bare config. Also the `Cvar()` helper
  treats a legitimate `0` cvar as "use fallback" (e.g. `g_rm_laser_zspread 0` → fallback 0; harmless here, but
  a footgun for any future 0-valued default).
- **Logic — RM deathtype:** PARTIAL. The RM explosion is tagged as a Vaporizer death, so the instagib
  always-gib + the rocketminsta self-damage-nullify hooks (which key on `devastator`) won't match Base's
  `WEP_DEVASTATOR` attribution; accuracy is credited to the wrong weapon too.
- **Logic — secondary RM rapid ladder:** PARTIAL/missing — only the base refire is modeled; the hold-to-rapid
  acceleration (`jump_interval2`/`g_rm_laser_rapid*`) and the mode-1 single-bolt rapid shot are absent.
- **Logic — ammo edge cases:** PARTIAL. `Instagib` is **hardcoded `true`** on the class, so in NON-instagib
  play the Vaporizer spends 1 cell per shot instead of `primary_ammo 10` (Base: `g_instagib ? 1 : primary_ammo`).
  The `Attack` re-reads `g_instagib` for the live decrement so under the mutator it's correct, but a
  map-placed Vaporizer in normal play under-charges ammo. `wr_checkammo2`, `wr_reload`, the forced-reload
  branch, and `wr_aim` are not on this class.
- **Timing:** faithful for the primary (refire 1 / animtime 0.3 via the shared driver + PrepareAttack). RM
  rapid timing missing as above.
- **Presentation:** MISSING — no rail beam drawn (the signature visual), no muzzle flash for this weapon, no
  impact effect/sound. This is the most player-visible gap: a fired Vaporizer produces no beam, no impact
  spark, and no impact sound (the fire sound does play). Contrast the Vortex, which emits VORTEX_IMPACT +
  neximpact.
- **Audio:** PARTIAL — fire sound `minstanexfire` plays at full `VOL_BASE` (Base uses `VOL_BASE*0.8`); impact
  sound `neximpact` MISSING; nearby-beam whoosh MISSING; the secondary RM uses `electro_fire2` (Base uses
  CRYLINK_FIRE for mode 0 / ELECTRO_FIRE2 for mode 1 — port always plays electro_fire2 once per barrage).
- **Achievements:** yoda / impressive (and the every-2nd-hit `vaporizer_lasthit` toggle) — MISSING.
- **Liveness:** the weapon class is `[Weapon]`-registered and driven by the generic `WeaponFireDriver`, and
  the InstaGib mutator gives it on spawn through the live `PlayerSpawn` hook — so the primary rail + ammo +
  one-shot gib path is genuinely live in an instagib match. The RM sub-mode and the non-instagib map-pickup
  path are present but I did not confirm a live caller equips the Vaporizer outside instagib, nor that `g_rm`
  is reachable from a live match config — marked unknown where so.

## Verifier corrections (2026-06-22, adversarial pass)
- **Headshot announce IS wired (audio upgraded unknown→faithful):** `FireRailgunBullet(headshotNotify:true)` →
  `NotificationSystem.Announce(actor,"HEADSHOT")` → `"HEADSHOT"` registered as an ANNCE with sound cue `headshot`
  (`NotificationsList.cs:90`), dispatched via `Send(MsgType.Annce)` to the notification Sink.
- **Secondary blaster liveness upgraded unknown→partial:** `WeaponFireDriver.Frame` calls `WrThink(Secondary)` every
  tick ATK2 is held; the manual `jump_interval` gate fires `BlasterSecondary` → `Blaster.FirePrimaryDirect` (Blaster is a
  registered `[Weapon]`). The path is reachable; only the RM-laser/rapid siblings are un-wired.
- **Kill/suicide messages ARE ported** (see above) — draft was wrong.
- **RM-laser deathtype gap added:** the port tags bouncing-laser splash with the Vaporizer `RegistryId`; Base sets
  `proj.projectiledeathtype = WEP_ELECTRO.m_id` (`vaporizer.qc:245`). Mis-attributes the laser hit/kill to the Vaporizer.
- Re-confirmed faithful+live: primary rail logic/values (150/800/1/0.3/10 vs `bal-wep-xonotic.cfg:488-496`), InstaGib
  integration (hooks gated on `g_instagib` via `MutatorActivation`), fire-sound volume bug (default `1.0` vs `VOL_BASE*0.8`),
  and that all beam/muzzle/impact/achievement presentation is genuinely absent (no emit; explicitly comment-deferred).

## Verification
- Code read: `vaporizer.{qc,qh}`, `tracing.qc:FireRailgunBullet`/`tracing.qh:W_SetupShot`,
  `sv_instagib.qc`, `sv_rocketminsta.qc`, balance/mutators cfg defaults; port `Vaporizer.cs`,
  `WeaponFiring.cs`, `WeaponSplash.cs`, `WeaponFireDriver.cs`, `InstagibMutator.cs`, `RocketMinstaMutator.cs`,
  `WeaponFireSounds.cs`, `Vortex.cs` (sister-weapon presentation baseline).
- Constant diffs taken directly from `bal-wep-xonotic.cfg`/`mutators.cfg` vs the C# `Cvar(...)`/`Bal(...)`
  fallbacks.
- No runtime/in-game observation performed; presentation "missing" claims are from absence of any emit call
  in `Vaporizer.cs` + no game-side Vaporizer beam/impact handler. Liveness of RM and non-instagib equip is
  unverified (marked unknown).

## Open questions
- Is the Vaporizer ever equipped on a live path outside the InstaGib mutator (map `weapon_vaporizer` pickup,
  superweapon spawn)? If not, the non-instagib ammo-rate bug and the missing reload methods are latent only.
- Is `g_rm` reachable from a shipped game mode/config in the port, and does anything load the `g_rm_*` cvars
  with Base defaults? If not, the wrong embedded fallbacks are the live values.
- Does the generic muzzle-flash/fire event fire for the Vaporizer (so at least a view-model flash shows), or
  is the weapon entirely silent visually apart from the fire sound?

# Overkill weapons — parity spec

**Base refs:** `common/mutators/mutator/overkill/{okhmg,okmachinegun,oknex,okrpc,okshotgun}.{qc,qh}`, shared `sv_weapons.qc` / `sv_overkill.qc`, balance `bal-wep-xonotic.cfg`
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Ok{Hmg,Machinegun,Nex,Rpc,Shotgun}.cs`, `OkWeapons.cs`, `Mutators/OverkillMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
The Overkill ("OK") loadout is a five-weapon arena set unlocked by the Overkill mutator
(`g_overkill 1`, shipped via `ruleset-overkill.cfg`). Players spawn with okmachinegun + oknex +
okshotgun (plus okrpc/okhmg when their `weaponstart` cvars are set), get unlimited ammo, and every
weapon's secondary is the shared "blaster jump" (a damage/force-less Blaster shot on a dedicated
per-player `jump_interval` timer used for movement). All five weapons are `WEP_FLAG_HIDDEN |
WEP_FLAG_MUTATORBLOCKED` (never normal pickups); okhmg and okrpc are additionally
`WEP_FLAG_SUPERWEAPON` and spawn from powerup replacement on some maps. This spec covers the five
weapons' fire behavior, their shared secondary, and the identity/balance attributes. The broader
Overkill mutator (loot, loadout, blaster nullification) is its own unit but is summarized where it
gates the weapons.

## Base algorithm (authoritative)

### OkMachineGun primary — auto bullet  (`okmachinegun.qc:W_OverkillMachineGun_Attack_Auto`)
- **Trigger:** sv, `wr_think` `fire & 1` → `weapon_prepareattack(..., 0)` → reset `misc_bulletcounter=0` → AttackAuto; self-reschedules via `weapon_thinkf(WFRAME_FIRE1, refire, ...)` while ATCK held + ammo.
- **Algorithm:** decrease ammo (clip); `W_SetupShot(SND_OK_MG_FIRE)`; punchangle recoil PRNG unless `g_norecoil`; `spread = bound(spread_min, spread_min + spread_add*misc_bulletcounter, spread_max)`; `fireBullet_falloff(...)` with `solidpenetration`, `damage`, the `damagefalloff_*` cvars, `force`, `EFFECT_RIFLE`; `++misc_bulletcounter`; muzzle flash; casing if `g_casings>=2`; `ATTACK_FINISHED = time + refire*W_WeaponRateFactor`.
- **Constants (bal-wep-xonotic.cfg):** ammo 1, damage 25, force 5, refire 0.1, solidpenetration 100, spread_add 0.012, spread_max 0.05, spread_min 0, reload_ammo 30, reload_time 1.5, secondary_refire_type 1; damagefalloff_{forcehalflife,halflife,maxdist,mindist} all 0.

### OkHeavyMachineGun primary — superweapon auto bullet  (`okhmg.qc:W_OverkillHeavyMachineGun_Attack_Auto`)
- Same shape as okmachinegun, but: **superweapon gate** at attack entry — fires NOTHING unless `StatusEffects_active(Superweapon)` OR `IT_UNLIMITED_SUPERWEAPONS`; otherwise `W_SwitchWeapon_Force(w_getbestweapon)` + `w_ready`. Uses plain `fireBullet` (no falloff). `SND_HMG_FIRE` (uzi_fire).
- **Constants:** ammo 1, damage 30, force 10, refire 0.05, solidpenetration 127, spread_add 0.005, spread_max 0.06, spread_min 0.01, reload_ammo 120, reload_time 1, secondary_refire_type 1.
- **okhmg_nadesupport hook (`okhmg.qc:5-12`):** a `Nade_Damage` mutator hook scales nade self-damage from the HMG to `max_health*0.1`.

### OkNex primary — rail beam  (`oknex.qc:W_OverkillNex_Attack`)
- **Trigger:** sv `wr_think` `fire & 1` → `weapon_prepareattack(..., refire)` → Attack → `weapon_thinkf(WFRAME_FIRE1, animtime, w_ready)`.
- **Algorithm:** `mydmg/myforce` from primary cvars; **charge** (default OFF): `charge = charge_mindmg/dmg + (1-charge_mindmg/dmg)*oknex_charge`, then `oknex_charge *= charge_shot_multiplier`; with charge off `charge=1`. `W_SetupShot(SND_OK_NEX_FIRE)`; overcharge plays extra `SND_OK_NEX_CHARGE`. `FireRailgunBullet(...)` (with falloff cvars). Yoda/Impressive announces on flying/impressive hits. `SendCSQCVortexBeamParticle`. Decrease ammo. `wr_think` also regens `oknex_charge` by `charge_rate*frametime/W_TICSPERFRAME`, regens chargepool, and (charge-velocity) the GetPressedKeys hook adds charge while moving fast.
- **Constants:** ammo 10, animtime 0.65, damage 100, force 500, refire 1, reload_ammo 50, reload_time 2, secondary 2, secondary_refire_type 1. charge 0 (off), charge_animlimit 0.5, charge_limit 1, charge_maxspeed 800, charge_mindmg 40, charge_minspeed 400, charge_rate 0.6, charge_shot_multiplier 0, charge_start 0.5, charge_velocity_rate 0; all damagefalloff_* 0.

### OkShotgun primary — pellet fan  (`okshotgun.qc:wr_think` → `W_Shotgun_Attack`)
- Calls the SHARED `W_Shotgun_Attack(thiswep, actor, weaponentity, true, ammo, damage, falloff…, bullets, spread, spread_pattern, …, solidpenetration, force, …, EFFECT_RIFLE_WEAK)` — same routine as the normal Shotgun: N hitscan pellets with per-pellet random spread (pattern off), solidpenetration + force. `weapon_thinkf(WFRAME_FIRE1, animtime, w_ready)`.
- **Constants:** ammo 3, animtime 0.65, bot_range 512, bullets 10, damage 17, force 80, refire 0.75, solidpenetration 3.8, spread 0.07, spread_pattern 0, reload_ammo 24, reload_time 2, secondary_refire_type 1; all damagefalloff_* 0.

### OkRPC primary — rocket-propelled chainsaw  (`okrpc.qc:W_OverkillRocketPropelledChainsaw_Attack`)
- **Trigger:** sv `wr_think` `fire & 1` → `weapon_prepareattack(..., refire)` → Attack → `weapon_thinkf(WFRAME_FIRE1, animtime, w_ready)`.
- **Algorithm:** spawn missile (`MOVETYPE_FLY`, size '-3..3', `takedamage=DAMAGE_YES`, health 25, `damageforcescale`, `damagedbycontents`, `bot_dodge`); `W_SetupShot_ProjectileSize` (SND_RPC_FIRE); origin `= w_shotorg - v_forward*3`; `W_SetupProjVelocity_Basic(missile, speed, 0)`; `cnt = time + lifetime`. **Think** (every frame): if `cnt<=time` delete; tracebox forward `2*myspeed*sys_frametime`; if it passes through a player → `Damage(damage2)` + accuracy bookkeeping; `velocity = mydir*(myspeed + speedaccel*sys_frametime)`. **Touch / event_damage** → Explode: `RadiusDamage(damage core, edgedamage, radius, force)`, plus the accuracy "add fired back" fixup. Shot down by its own `event_damage` (W_PrepareExplosionByDamage).
- **Constants:** ammo 10, animtime 1, damage 150, damage2 500, damageforcescale 2, edgedamage 50, force 400, health 25, lifetime 30, radius 300, refire 1, speed 2500, speedaccel 5000, reload_ammo 10, reload_time 1, secondary_refire_type 1.

### Shared secondary — Overkill blaster jump  (every `ok*.qc:wr_think` head)
- `if (refire_type == 1 && (fire & 2) && time >= actor.jump_interval) { actor.jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire)*W_WeaponRateFactor; makevectors; W_Blaster_Attack(); set FIRE2 anim }`. Refire_type 0 instead fires the blaster in the normal `(fire & 2)` branch on the shared timer. The blaster's damage/force are zeroed by the Overkill mutator (`Damage_Calculate`) unless `keepdamage`/`keepforce`. Default `secondary_refire_type 1` for all five.

### Identity attributes (the `.qh` CLASS blocks)
- okhmg: impulse 3, RES_BULLETS, color '0.992 0.471 0.396', flags MUTATORBLOCKED|HIDDEN|RELOADABLE|HITSCAN|SUPERWEAPON|PENETRATEWALLS, models h_ok_hmg.iqm/v_ok_hmg.md3/g_ok_hmg.md3, deprecated netname "hmg".
- okmachinegun: impulse 3, RES_BULLETS, color '0.678 0.886 0.267', flags HIDDEN|RELOADABLE|HITSCAN|PENETRATEWALLS|MUTATORBLOCKED, models ok_mg.
- oknex: impulse 7, RES_CELLS, color '0.459 0.765 0.835', flags HIDDEN|RELOADABLE|HITSCAN|MUTATORBLOCKED, models ok_sniper.
- okrpc: impulse 9, RES_ROCKETS, color '0.914 0.745 0.341', flags MUTATORBLOCKED|HIDDEN|CANCLIMB|RELOADABLE|SPLASH|SUPERWEAPON, models ok_rl, deprecated netname "rpc".
- okshotgun: impulse 2, RES_SHELLS, color '0.518 0.608 0.659', flags HIDDEN|RELOADABLE|HITSCAN|MUTATORBLOCKED, models ok_shotgun.

## Port mapping
| Base | Port | Notes |
|---|---|---|
| `ok*.qc` weapon classes | `Ok{Hmg,Machinegun,Nex,Rpc,Shotgun}.cs` (`[Weapon]`) | registered, dispatched by `WeaponFireDriver` → `WrThink` every tick; ammo via `WeaponAmmo.Check` |
| `wr_think` primary | each `WrThink` + `Attack`/`AttackAuto` | faithful structure (PrepareAttack gate, self-reschedule for auto) |
| shared secondary head | `OkWeapons.FireSecondaryBlasterJump` | refire_type==1 only |
| HMG superweapon gate | `OkHmg.SuperweaponGate` | checks Superweapon status / IT_UNLIMITED_SUPERWEAPONS |
| `fireBullet`/`fireBullet_falloff` | `WeaponFiring.FireBullet` | port omits the falloff variant (all OK falloff cvars are 0) |
| `FireRailgunBullet` | `WeaponFiring.FireRailgunBullet` | headshotNotify false (like Vortex) |
| `W_Shotgun_Attack` | inline pellet loop in `OkShotgun.Attack` | per-pellet random spread (pattern off) |
| RPC missile think/explode | `OkRpc.OnThink/Explode` | pass-through damage2 + RadiusDamage; networking/accuracy/bot_dodge omitted |
| balance cvars | `Configure()` `Bal(...)` reads of `g_balance_ok*_*` | values 1:1 with bal-wep-xonotic.cfg |
| `okhmg_nadesupport` Nade_Damage | NOT IMPLEMENTED | deferred |
| oknex charge / chargepool / wr_glow / velocity-charge | present but charge-gated; default OFF | inert at stock balance |
| oknex yoda/impressive announce | NOT IMPLEMENTED | stat/announce |
| casings / muzzle flash / punchangle recoil | NOT IMPLEMENTED | presentation |
| CSQC `wr_impacteffect` (impact particle + ric/impact sound) | NOT IMPLEMENTED (client) | fire sounds ARE played |
| Overkill mutator (loadout/loot/blaster null/arena-off) | `OverkillMutator.cs` | live when `g_overkill` set |

## Parity assessment

- **Logic:** Faithful for all five primaries (bullet auto-fire with accumulating spread, rail beam,
  pellet fan, chainsaw missile pass-through + explosion), the HMG superweapon gate, forced reload,
  and the shared refire_type==1 blaster jump. The refire_type==0 secondary path is NOT handled by
  the port, but the default is 1 for all five, so no observable gap at stock balance.
- **Values:** Faithful — every shipped cvar default matches `bal-wep-xonotic.cfg` exactly and the
  port reads the same file. okmachinegun's `fireBullet_falloff` is replaced by plain `FireBullet`,
  identical at the stock all-zero falloff cvars.
- **Timing:** Faithful refire/animtime/reload (rate-factor scaled); the auto weapons self-reschedule
  per refire. oknex charge regen uses `frametime` rather than QC's `frametime/W_TICSPERFRAME` but is
  inert (charge off by default).
- **Presentation:** Partial/missing — no muzzle flash, no shell casings, no `g_norecoil` punchangle
  recoil PRNG, no CSQC impact particles, no oknex `wr_glow` charge glow / beam-charge particle. Fire
  sounds play; impact ric/explosion sounds (client `wr_impacteffect`) are not wired.
- **Audio:** Fire sounds correct (uzi_fire, nexfire, rocket_fire, shotgun_fire). Missing: oknex
  overcharge sound (charge-gated, inert), impact/ric sounds (client side).
- **Liveness:** The weapons are LIVE on the server tick path **when `g_overkill` is set** (NetGame
  loadout + OverkillMutator + WeaponFireDriver all honor it; `ruleset-overkill.cfg` sets it). BUT the
  port's mutators menu (`DialogMutators.cs`) exposes InstaGib and NIX but NO Overkill toggle — Base
  also drives OK via a ruleset rather than the arena menu, so this matches Base's pathing; OK is
  reachable only by exec'ing the ruleset / setting the cvar, not via a dedicated UI control.

### Gaps (player-observable)
1. No muzzle flash or ejected shell casings on any OK weapon fire.
2. No view recoil — firing the OK MG/HMG does not jitter the view (`punchangle` PRNG absent).
3. No client impact effects/sounds: bullet/rail/explosion impacts show no particle decal and play no
   ricochet/impact sound (only the fire sound is heard).
4. HMG nade self-damage is not reduced to 10% (okhmg_nadesupport hook missing) — throwing a nade
   while holding the HMG deals full self-damage.
5. oknex Yoda / Impressive achievement announcements never fire.
6. okrpc missile is not dodgeable by bots, not networked as a CSQC projectile (visual), and accuracy
   stats for the chainsaw pass-through/explosion are not bookkept.
7. Overkill loot drop spawns a simplified item entity (basic toss + timed removal) rather than the
   full `Item_Initialise`/`Item_RandomFromList` pipeline.

### Intended divergences
- oknex charge model (velocity-charge GetPressedKeys hook, chargepool, wr_glow, overcharge sound)
  intentionally deferred: `g_balance_oknex_charge` defaults 0, so the stock weapon is
  "Vortex-without-charge" and the charge path is inert. Documented in OkNex.cs.

## Verification
- Base defaults read from `bal-wep-xonotic.cfg` lines 792–922 (value diff: all match).
- Liveness traced: `WeaponFireDriver` → `weapon.WrThink` (every tick), `WeaponAmmo.Check` cases for
  all five OK weapons, `MutatorActivation.Apply()` at `GameWorld.cs:511`, `OverkillMutator.IsEnabled`
  = `g_overkill != 0`, NetGame loadout `NetGame.cs:1708`. No runtime in-match capture performed.
- Identity attributes diffed field-by-field against the `.qh` CLASS blocks (all match).

## Open questions
- Does any shipped map/gametype in the port exec `ruleset-overkill.cfg` automatically, or is the mode
  only reachable by manual cvar? (Menu has no Overkill control.) Needs a runtime check.
- okrpc chainsaw pass-through uses `EntFlags.Client` + `TakeDamage != No` to detect a player; confirm
  this matches QC `IS_PLAYER(trace_ent)` (e.g. corpses/spectators) in a live match.

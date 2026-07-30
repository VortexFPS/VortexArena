# Grappling Hook (weapon-hook) — parity spec

**Base refs:** `common/weapons/weapon/hook.qc` + `hook.qh` · `server/hook.qc` (grapple pull physics) · `common/mutators/mutator/hook/sv_hook.qc` (offhand mutator) · `common/mutators/mutator/hook/cl_hook.qc` · balance in `bal-wep-xonotic.cfg` (`g_balance_hook_*`) + `balance-xonotic.cfg` (`g_balance_grapplehook_*`, `g_grappling_hook_tarzan`)
**Port refs:** `src/VortexArena.Common/Gameplay/Weapons/Hook.cs` · `src/VortexArena.Common/Gameplay/Mutators/HookMutator.cs` · `.../Mutators/VampireHookMutator.cs` · `.../Mutators/BreakablehookMutator.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Grappling Hook is a dual-role weapon. **Primary fire** launches a chain projectile (`grapplinghook`) that flies forward, latches onto whatever it touches, and then *reels the firer toward the latch point* — a pure movement tool that consumes fuel. **Secondary fire** lobs a "gravity bomb" (`hookbomb`) that falls under its own gravity (MOVETYPE_TOSS) and, on contact or at end of lifetime, applies a *negative-force* (pull-inward) radius blast spread evenly over a `duration`. Both projectiles are shootable (have HP). The hook also exists as an **offhand** weapon via the `hook` mutator (`g_grappling_hook`), letting every player reel while keeping their primary weapon out (this is the form used in the instahook ruleset and several campaign levels). The whole reel mechanic — and most of the weapon — runs on the **authority** (SVQC `server/hook.qc`); the rope line, impact sound, and muzzle/explode particles are **presentation** (CSQC `hook.qc`/`cl_hook.qc`).

## Base algorithm (authoritative)

### Weapon identity / attributes (`hook.qh`)
- ammo_type RES_FUEL; impulse 0; spawnflags `WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH | WEP_FLAG_NOTRUEAIM`; m_color `'0.471 0.817 0.392'`.
- models: view `h_hookgun.iqm`, world `v_hookgun.md3`, item `g_hookgun.md3`; chain model `models/hook.md3`. crosshair `gfx/crosshairhook` size 0.5.
- `ammo_factor = 1` (set to 0 by the hook mutator when `g_grappling_hook_useammo` is off → reeling is free).

### Primary: grapple lifecycle (`wr_think`, `hook.qc` + `server/hook.qc`)
The primary is a *continuous* weapon driven by a hook_state bitfield (`HOOK_FIRING | HOOK_WAITING_FOR_RELEASE | HOOK_PULLING | HOOK_RELEASING | HOOK_REMOVING`).
- **On press** (`fire & 1`), if no live hook AND not WAITING_FOR_RELEASE AND `hook_refire < time` AND `weapon_prepareattack(...false, -1)`: decrease ammo by `ammo_factor * WEP_CVAR_PRI(ammo)`, set `HOOK_FIRING | HOOK_WAITING_FOR_RELEASE`, schedule `WFRAME_FIRE1` over `pri.animtime`.
- **On release** (`!(fire & 1)`): set `HOOK_REMOVING`, clear `HOOK_WAITING_FOR_RELEASE`.
- **While a hook exists:** bump `hook_refire = max(hook_refire, time + pri.refire * W_WeaponRateFactor)`; inhibit health regen for 1 s (`pauseregen_finished`, unless unlimited ammo).
- **While latched** (`hook.state == 1`): if `pri.hooked_time_max > 0` and `time > hook_time_hooked + hooked_time_max` → `HOOK_REMOVING`. Fuel drain: `hooked_fuel = ammo_factor * pri.hooked_ammo` per second once `time > hook_time_fueldecrease`; if fuel insufficient, set fuel 0, `HOOK_REMOVING`, and (offhand) switch to best weapon.
- **Not latched:** reset `hook_time_hooked = time`, `hook_time_fueldecrease = time + pri.hooked_time_free`.
- **Crouch-slide:** `HOOK_PULLING = (!CROUCH || !g_balance_grapplehook_crouchslide)`. With crouchslide on (default 0 → always pulling), holding crouch suspends the pull.
- **State machine tail:** if `HOOK_FIRING` → RemoveHook(old), `FireGrapplingHook`, clear FIRING, bump `hook_refire` by `g_balance_grapplehook_refire * W_WeaponRateFactor`. Else if `HOOK_REMOVING` → RemoveHook, clear REMOVING.

### FireGrapplingHook (`server/hook.qc`)
- Bails if `weaponLocked` or in a vehicle.
- `W_SetupShot_ProjectileSize(... '-3 -3 -3','3 3 3', true, 0, SND_HOOK_FIRE, CH_WEAPON_B, 0, WEP_HOOK.m_id)`; `W_MuzzleFlash(WEP_HOOK,...)`.
- Spawns `grapplinghook`: `FL_PROJECTILE`, movetype `MOVETYPE_TOSS` if `g_balance_grapplehook_gravity` else `MOVETYPE_FLY`, `PROJECTILE_MAKETRIGGER`, bbox `'-3 -3 -3'..'3 3 3'`, `state=0` (in flight).
- Velocity via `W_SetupProjVelocity_Explicit(missile, w_shotdir, v_up, g_balance_grapplehook_speed_fly, 0,0,0,false)` (default 1800). angles = vectoangles(velocity). `EF_LOWPRECISION`.
- HP = `g_balance_grapplehook_health` (50), `event_damage = GrapplingHook_Damage`, `takedamage = DAMAGE_AIM`, damageforcescale 0, optionally `damagedbycontents` (default 1).
- touch `GrapplingHookTouch`, think `GrapplingHookThink`. Networked via `Net_LinkEntity(..GrapplingHookSend)` (CSQC `ENT_CLIENT_HOOK`). `MUTATOR_CALLHOOK(EditProjectile)`.

### GrapplingHookTouch / GrapplingHook_Stop (`server/hook.qc`)
- Touch: ignore MOVETYPE_FOLLOW touchers; `PROJECTILE_TOUCH`; then `GrapplingHook_Stop`; then `SetMovetypeFollow(this, toucher)` (the hook *follows* the surface/entity it hit, via WarpZone refsys).
- Stop: emit `EFFECT_HOOK_IMPACT` + `SND_HOOK_IMPACT`; `state = 1` (latched); think `GrapplingHookThink` @ time; clear touch; velocity 0; `MOVETYPE_NONE`; `hook_length = -1` (recomputed next think).

### GrapplingHookThink — the reel (`server/hook.qc`)
Runs every think (`nextthink = time`). Computes the gun shot-origin in world space (`hook_shotorigin[align]` rotated by `v_angle`), `org = owner.origin + view_ofs + vs`, transforms through warpzones to `myorg`. On first latched think sets `hook_length = vlen(myorg - origin)`.
`MUTATOR_CALLHOOK(GrappleHookThink, this, tarzan, pull_entity, velocity_multiplier)` — vampirehook drains here; tarzan/pull_entity/mult can be overridden.

Two pull modes, selected by `g_grappling_hook_tarzan` (**Base default = 2**):
- **tarzan (≥1, the live default):** elastic rubber-band rope.
  - Pull rope in by `speed_pull * frametime` down to `length_min`; if overstretched (`newlength < dist - stretch`) clamp and, if moving away from hook, add `frametime * dir * force_rubber_overstretch`.
  - If `HOOK_RELEASING`, `hook_length = dist`. Else accelerate player along the rope: `spd = bound(0, (dist - hook_length)/stretch, 1)`; apply airfriction `v *= 1 - frametime*airfriction`; `v += frametime*dir*spd*force_rubber`.
  - tarzan ≥2 can also pull a hooked *player/nade/vehicle* (`aiment`): splits the velocity delta (`dv*0.5`) between puller and target, unsets onground, sets pusher/pushltime for frag credit, honours `g_balance_grapplehook_pull_frozen`.
  - Writes back `pull_entity.velocity` (warpzone-transformed) unless frozen-pulling / aiment is a projectile.
- **non-tarzan (0):** straight constant-speed reel. `end = origin - dir*50`; `dist = vlen(end - myorg)`; `spd = dist < 200 ? dist*(speed_pull/200) : speed_pull`; if `spd < 50` → 0; `owner.velocity = dir*spd`; owner `MOVETYPE_FLY`; unset onground.

After pulling, recompute `hook_start`/`hook_end` and set SendFlags so CSQC redraws the rope.

### RemoveHook / RemoveGrapplingHooks / reset
- `RemoveHook`: clear owner's `.hook`, restore `MOVETYPE_WALK` if owner was FLY, delete the chain.
- `RemoveGrapplingHooks(pl)`: restore walk, delete every slot's hook. Called from `wr_resetplayer`, freezetag freeze, teleport, vehicle enter.
- `GrapplingHook_Damage`: TakeResource HP; at ≤0, credit `attacker` as pusher then RemoveHook.

### Secondary: gravity bomb (`W_Hook_Attack2`, `hook.qc`)
- `W_SetupShot(actor, weaponentity, false, 4, SND_HOOKBOMB_FIRE, CH_WEAPON_A, sec.damage, WEP_HOOK.m_id | HITTYPE_SECONDARY)` — note the **recoil = 4**. Secondary ammo is *not* consumed (WEAPONTODO).
- Spawns `hookbomb`: `MOVETYPE_TOSS`, `PROJECTILE_MAKETRIGGER`, bbox `'0 0 0'`, `gravity = sec.gravity` (5), `velocity = '0 0 1' * sec.speed` (default 0, plus owner velocity if `g_projectiles_newton_style`), `missile_flags = MIF_SPLASH | MIF_ARC`.
- `nextthink = time + sec.lifetime` (5) with think `adaptor_think2use_hittype_splash` → `W_Hook_Explode2_use`; touch `W_Hook_Touch2` (PROJECTILE_TOUCH then `use`). HP = `sec.health` (15), `takedamage = DAMAGE_YES`, `damageforcescale = sec.damageforcescale` (0), `event_damage = W_Hook_Damage`, `damagedbycontents`. `CSQCProjectile(... PROJECTILE_HOOKBOMB ...)`. `MUTATOR_CALLHOOK(EditProjectile)`.

### Secondary blast curve (`W_Hook_Explode2` + `W_Hook_ExplodeThink`, `hook.qc`)
- On detonation: clear event_damage/touch, `EF_NODRAW`, think `W_Hook_ExplodeThink` @ time, `teleport_time = time`, `dmg_last = 1`, `MOVETYPE_NONE`. (CSQC plays `EFFECT_HOOK_EXPLODE` + `SND_HOOKBOMB_IMPACT` via `wr_impacteffect`.)
- Each ExplodeThink (every 0.05 s for `duration`): `dt = time - teleport_time`; `dmg_remaining = bound(0, 1 - dt/duration, 1) ** power`; `f = dmg_last - dmg_remaining`; `dmg_last = dmg_remaining`; `RadiusDamage(this, realowner, f*damage, f*edgedamage, radius, realowner, f*force, deathtype...)`. So the blast is **spread over `duration`** with a power-curve falloff (the negative force pulls victims inward over time). Adds `HITTYPE_SPAM` after first tick. Repeats until `dt ≥ duration`, then deletes.

### Ammo checks
- `wr_checkammo1`: if `ammo_factor == 0` → true; if hooked → `fuel > 0`; else `fuel >= pri.ammo`.
- `wr_checkammo2`: always true (secondary is ammo-free for now).

### Offhand mutator (`sv_hook.qc`)
- `REGISTER_MUTATOR(hook, expr_evaluate(cvar_string("g_grappling_hook")))`. ONADD: `g_grappling_hook = true`; if `!useammo` set `WEP_HOOK.ammo_factor = 0`.
- PlayerSpawn: `player.offhand = OFFHAND_HOOK`. `OFFHAND_HOOK.offhand_think` calls `WEP_HOOK.wr_think(... weaponentities[1], key_pressed?1:0)` each PlayerPreThink while the +hook button is held.
- SetStartItems (useammo): grant `FuelRegen` + start fuel = `g_balance_fuel_rotstable`. FilterItem: suppress the `weapon_hook` world pickup.
- Overridden by `offhand_blaster` mutator if both active.

### CSQC presentation (`hook.qc` CSQC + `cl_hook.qc`)
- `Draw_GrapplingHook`: draws the rope as a stack of `Draw_CylindricLine` segments (thickness 8, team-coloured texture `particles/hook_<team>`), traced from the firer's gun muzzle (or chase origin) to the hook end through warpzones, alpha `cl_grapplehook_alpha`. The model is placed at the trace endpoint and angled along the rope.
- `wr_impacteffect`: hookbomb explodes slightly below floor (`w_backoff * -2`), emits `EFFECT_HOOK_EXPLODE` + `SND_HOOKBOMB_IMPACT`.
- `ENT_CLIENT_HOOK` net handler reads owner/slot/start/end and sets up interpolation + the rope model.

### Constants (Base defaults)
| cvar | default | units | side |
|---|---|---|---|
| g_balance_hook_primary_ammo | 5 | fuel | authority |
| g_balance_hook_primary_animtime | 0.3 | s | authority |
| g_balance_hook_primary_hooked_ammo | 5 | fuel/s | authority |
| g_balance_hook_primary_hooked_time_free | 2 | s | authority |
| g_balance_hook_primary_hooked_time_max | 0 (∞) | s | authority |
| g_balance_hook_primary_refire | 0.2 | s | authority |
| g_balance_hook_secondary_animtime | 0.3 | s | authority |
| g_balance_hook_secondary_damage | 25 | hp | authority |
| g_balance_hook_secondary_damageforcescale | 0 | — | authority |
| g_balance_hook_secondary_duration | 1.5 | s | authority |
| g_balance_hook_secondary_edgedamage | 5 | hp | authority |
| g_balance_hook_secondary_force | -2000 | (pull) | authority |
| g_balance_hook_secondary_gravity | 5 | grav-scale | authority |
| g_balance_hook_secondary_health | 15 | hp | authority |
| g_balance_hook_secondary_lifetime | 5 | s | authority |
| g_balance_hook_secondary_power | 3 | exponent | authority |
| g_balance_hook_secondary_radius | 500 | qu | authority |
| g_balance_hook_secondary_refire | 3 | s | authority |
| g_balance_hook_secondary_speed | 0 | qu/s | authority |
| g_balance_hook_pickup_ammo | 50 | fuel | authority |
| g_balance_hook_switchdelay_drop/_raise | 0.2 / 0.2 | s | authority |
| g_balance_hook_weaponstart / _weaponstartoverride | 0 / -1 | — | authority |
| g_balance_hook_weaponthrowable | 1 | bool | authority |
| g_balance_grapplehook_speed_fly | 1800 | qu/s | authority |
| g_balance_grapplehook_speed_pull | 2000 | qu/s | authority |
| g_balance_grapplehook_force_rubber | 2000 | — | authority |
| g_balance_grapplehook_force_rubber_overstretch | 1000 | — | authority |
| g_balance_grapplehook_length_min | 50 | qu | authority |
| g_balance_grapplehook_stretch | 50 | qu | authority |
| g_balance_grapplehook_airfriction | 0.2 | /s | authority |
| g_balance_grapplehook_health | 50 | hp | authority |
| g_balance_grapplehook_damagedbycontents | 1 | bool | authority |
| g_balance_grapplehook_refire | 0.2 | s | authority |
| g_balance_grapplehook_nade_time | 0.7 | s | authority |
| g_balance_grapplehook_crouchslide | 0 | bool | authority |
| g_balance_grapplehook_gravity | 0 | grav-scale | authority |
| g_balance_grapplehook_pull_frozen | 0 | enum | authority |
| g_grappling_hook_tarzan | **2** | enum (0/1/2) | authority |
| g_grappling_hook_useammo | 0 | bool | authority |
| autocvar_cl_grapplehook_alpha | 1 | — | presentation |
| hook_shotorigin (grapple) | '8 8 -12' (all aligns) | qu | shared |

## Port mapping
- **Weapon identity / balance** → `Hook.cs` ctor + `Configure()`. All `g_balance_hook_*` cvars read with correct defaults. Grapple cvars (`speed_fly`, `speed_pull`, `length_min`, `health`) hardcoded as fields (NOT read from `g_balance_grapplehook_*`); the rest of the grapple cvars (force_rubber, stretch, airfriction, overstretch, crouchslide, gravity, refire, tarzan, nade_time, pull_frozen) are **absent**.
- **Primary state machine** → `Hook.WrThink` (Primary branch) — faithful: press/release latch, WAITING_FOR_RELEASE, hooked_time_max, hooked-fuel drain, hooked_time_free grace, HOOK_PULLING always-on, FIRING/REMOVING tail. (`hook.state==1` latched flag is modelled by reusing `hook.Health > 0`.) Omits: `W_WeaponRateFactor` scaling on refire, the 1 s regen inhibit (`pauseregen_finished`), and the offhand best-weapon-switch on fuel-out.
- **FireGrapplingHook** → `Hook.FireGrapplingHook` — spawns the chain, MOVETYPE_FLY, fly speed, MakeTrigger, shootable HP. Omits: muzzle flash, warpzone refsys, gravity-mode movetype, and the dedicated `Net_LinkEntity`/`GrapplingHookSend` rope-endpoint stream. **CORRECTION:** the chain *head* IS networked — it spawns as a normal projectile entity (`EntFlags.Item` + Owner + `MoveType.Fly`), so `ServerNet.Classify` returns `Projectile` and it renders as a red Hookbomb spike while in flight (`ProjectileCatalog.cs:252`). What is missing is the dedicated hook stream (`hook_start`/`hook_end`) and therefore any rope line.
- **GrapplingHookTouch / Stop** → `Hook.GrapplingHookTouch` — latches, stops, plays impact sound. Omits `EFFECT_HOOK_IMPACT` particle and `SetMovetypeFollow` (the hook does not follow a moving surface/entity).
- **GrapplingHookThink reel** → `Hook.GrapplingHookThink` — implements **only the non-tarzan branch** (straight constant-speed reel). `myorg` uses `actor.Origin + actor.ViewOfs` (no gun shot-origin offset). Fires the `GrappleHookThink` mutator hook.
- **Secondary bomb** → `Hook.AttackGravityBomb` + `StartBlast` + `ExplodeThink` — faithful toss, lifetime/touch/shoot-down detonation, and the power-curve duration-spread RadiusDamage (the centrepiece). Routed through the refire gate (intended divergence, see below). Omits Newton-style velocity add and `nade_time`.
- **Offhand mutator** → `HookMutator` — PlayerSpawn offhand assign, PlayerPreThink offhand-think driving `Hook.WrThink` on a dedicated high slot, useammo fuel grant + FilterItem suppress, no-fuel top-up when useammo off. The `g_grappling_hook` string-cvar gate is faithful (`ruleset-instahook.cfg` sets it 1).
- **vampirehook / breakablehook mutators** → `VampireHookMutator` / `BreakablehookMutator` — bodies are faithful but vampirehook is inert because the port never sets a hooked-player `aiment` (geometry-only latch).
- **CSQC rope** → **NOT IMPLEMENTED.** No `Draw_GrapplingHook` / `Draw_CylindricLine` equivalent and no dedicated `GrapplingHookSend`/`ENT_CLIENT_HOOK` stream, so no rope LINE is drawn. The chain *head* IS networked + classified as `ProjectileType.Hookbomb` (a spike) while flying, then disappears on latch (the latch sets `MoveType.None`, which makes the model-less entity drop out of the snapshot). Net effect: a floating spike in flight, nothing once latched, never a rope.

## Parity assessment

### Gaps
- **No rope line is drawn (presentation, severe).** Base draws a thick team-coloured cylindric line from the muzzle to the latch point every frame; the port neither networks the chain entity (no `GrapplingHookSend`/`ENT_CLIENT_HOOK`) nor has any line renderer. A player sees no hook chain.
- **Reel feel is the wrong variant (logic/values, severe).** Base default `g_grappling_hook_tarzan 2` makes the **elastic rubber-band rope** the live pull (accelerate-along-rope with airfriction, overstretch spring, length clamp, and the ability to pull other players). The port implements only the `tarzan 0` straight constant-speed reel. The reel speed/curve, the "swing", and player-vs-player pull are all different. None of `force_rubber`, `stretch`, `airfriction`, `force_rubber_overstretch`, `length_min`(read), `speed_pull`(read), `tarzan` are honoured from cvars.
- **myorg origin offset wrong (values, minor).** Base reels toward `owner.origin + view_ofs + gun_shotorigin('8 8 -12')`; the port uses `owner.Origin + ViewOfs` only, so the latch/length math is offset by the gun-hand vector.
- **Hook does not follow a moving surface (logic, moderate).** Base `SetMovetypeFollow` makes a hook stuck to a mover/player track it; the port leaves the latch fixed in world space.
- **Offhand path is dead; in-hand path is LIVE.** The offhand path depends on `Entity.OffhandFirePressed`, but the `+hook` bind (present in `BindInput.ActionToCommand`) is **never sampled into the net input**: `InputButtons` has no hook bit and nothing in `src/` ever assigns `OffhandFirePressed` (verified by grep). So even with `g_grappling_hook 1` (instahook ruleset / campaign), pressing the bound key never reaches the server → the offhand hook can't fire. **Correction vs draft:** the in-hand weapon IS live — `Hook.WrThink` is dispatched per-tick by `WeaponFireDriver` for the active weapon, the Hook is a registered weapon (`WeaponOrder.cs:209`), and `ItemSpawnFuncs.cs:149` registers a `weapon_hook` map-pickup spawnfunc, so it is obtainable + selectable on the normal match path. In-hand rows therefore carry `liveness: live`.
- **Missing primary niceties (values/logic, minor):** `W_WeaponRateFactor` refire scaling, 1 s regen inhibit while hooked, offhand best-weapon switch on fuel-out, muzzle flash on hook fire, `EFFECT_HOOK_IMPACT` on latch.
- **vampirehook inert (logic, moderate, documented):** the health-drain-while-hooked-to-enemy mechanic never fires because no hooked-player `aiment` is set (consequence of the geometry-only / non-tarzan reel).

### Intended divergences
- **Secondary bomb routed through the refire gate.** QC gates the secondary via `weapon_prepareattack` on `sec.refire` (3 s); since the port bomb is ammo-free, the in-code rationale notes that without the gate, holding ATK2 would spawn one bomb per tick. The port uses `RefireFor(Secondary)=sec.refire`. Net behaviour matches Base's 3 s cadence.
- **`hook.state==1` latched flag reused as `hook.Health > 0`.** Implementation detail; behaviourally equivalent to QC's `.state`.

### Liveness summary
- `Hook.WrThink` / secondary bomb / reel: **live** — dispatched via `WeaponFireDriver` (`weapon.WrThink(...)` every tick for the active weapon), and the Hook is obtainable via the `weapon_hook` map pickup (`ItemSpawnFuncs.cs:149`) + selectable (`WeaponOrder.cs:209`).
- `HookMutator` offhand path: `PlayerPreThink` is fired live (`GameWorld.cs:988`), but the offhand button never arrives (no net hook bit, `OffhandFirePressed` never assigned) ⇒ **dead** for the offhand-fire path.
- `VampireHookMutator.GrappleHookThink`: the hook is subscribed and fired, but the guarded body can never reach its effect because `Aiment` is never set by the geometry-latching grapple (every guard returns early) ⇒ **dead** (`logic: partial`). Corrected from the draft's `live`: the handler running but producing nothing is the dead-code failure mode, not a live feature.

## Verification
- **Code read** (Base `hook.qc`/`hook.qh`/`server/hook.qc`/`sv_hook.qc`/`cl_hook.qc`; port `Hook.cs`/`HookMutator.cs`/`VampireHookMutator.cs`/`BreakablehookMutator.cs`) — all status rows.
- **Constant diff** against `bal-wep-xonotic.cfg` + `balance-xonotic.cfg` — secondary balance matches; grapple cvars hardcoded/absent.
- **Liveness trace:** `WeaponFireDriver` WrThink dispatch (verified); `MutatorHooks.PlayerPreThink.Call` at `GameWorld.cs:988` (verified live); `InputButtons` enum + `NetGame.SampleInput` (verified: **no hook button bit**, `OffhandFirePressed` never assigned). `ruleset-instahook.cfg:9` sets `g_grappling_hook 1`.
- **In-hand pickup/selection wiring confirmed (code):** `weapon_hook` spawnfunc (`ItemSpawnFuncs.cs:149`) + Hook in the weapon order (`WeaponOrder.cs:209`) ⇒ obtainable + selectable.
- **Entity networking confirmed (code):** `grapplinghook` + `hookbomb` both classify as `Projectile` in `ServerNet.Classify` (FL_PROJECTILE + Owner + projectile movetype) and render via `ProjectileCatalog` as `Hookbomb`; the latched hook (`MoveType.None`) drops out of the snapshot.
- **Not runtime-verified:** actual reel motion / pull feel in a live match; the on-screen spike vs Base rope appearance.

## Open questions
- Is the offhand `+hook` button ever intended to be wired into `InputButtons`, or is the offhand hook deliberately deferred? (Determines whether the instahook/campaign-special hook is playable at all — same missing input wire as nade-throw, ../../TODO.md T11.)
- Does the port intend to add the tarzan rubber-band reel (the Base default feel) or ship the simpler constant-speed reel as a deliberate divergence?

# Vehicle framework — parity spec

**Base refs:** `common/vehicles/sv_vehicles.qc` · `sv_vehicles.qh` · `vehicle.qh` · `vehicles.qc` · `cl_vehicles.qc` · `cl_vehicles.qh`
**Port refs:** `src/VortexArena.Common/Gameplay/Vehicles/{VehicleCommon,VehicleBoarding,VehiclePhysicsHelpers,VehicleSpawnFuncs,EntityVehicleStateExtra}.cs` · `game/hud/VehicleHud.cs` · `game/net/ServerNet.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The vehicle framework is the shared, vehicle-agnostic infrastructure every concrete vehicle (Racer / Raptor /
Spiderbot / Bumblebee) leans on: the `Vehicle` registry descriptor + its `vr_*` virtual hooks, the per-edict
`.vehicle_*` fields, the enter/exit/spawn/think state machine, resource regen, the shield-then-health damage
split, the shared bolt/rocket projectile, turret aiming + homing lock-on, the crush/impact/pain-frame combat
texture, and the client HUD/cockpit/auxiliary-crosshair presentation. It is active only when `g_vehicles` is on
and a map (or a mutator like Onslaught) places vehicle spawnpoints; the concrete vehicles are audited as their
own units. This unit covers ONLY the framework (`common/vehicles/*.qc`, not `common/vehicles/vehicle/*.qc`).

## Base algorithm (authoritative)

### Master init + placement  (`sv_vehicles.qc:vehicle_initialize`, `:1168`)
- **Trigger:** each map `spawnfunc(vehicle_X)` calls `vehicle_initialize(this, VEH_X, false)`.
- **Algorithm:** bail if `!autocvar_g_vehicles` or no `vehicleid`; precache; resolve `.targetname` controller
  (sets `.active`/`.team`/`.use = vehicle_use`); clamp team off when `!teamplay || !g_vehicles_teams`; set model
  (`.mdl` override else `info.model`); spawn `vehicle_viewport`/`vehicle_hudmodel`/`tur_head`; push to
  `g_bot_targets`/`g_damagedbycontents`; set `iscreature`, `teleportable=false`, `damagedbycontents`,
  `MOVETYPE_STEP`-via-spawn, `dphitcontentsmask = BODY|SOLID (+PLAYERCLIP if g_playerclip_collisions)`; attach
  the tur_head/hudmodel/viewport to model tags (`tag_head`/`tag_hud`/`tag_view`); `setsize(m_mins, m_maxs)`; run
  `vr_setup`; drop to floor via a downward tracebox unless `nodrop`; record `pos1 = origin`, `pos2 = angles`;
  schedule the first think (`0` if ACTIVE_NOT; `time + respawntime + random()*delayspawn_jitter` if
  `g_vehicles_delayspawn`; else `time + game_starttime`); finally `MUTATOR_CALLHOOK(VehicleInit, this)` —
  a true return ABORTS init.
- **Constants:** `g_vehicles=1`, `g_vehicles_teams=1`, `g_vehicles_delayspawn=1`,
  `g_vehicles_delayspawn_jitter=10`, `g_playerclip_collisions` (gates PLAYERCLIP), `g_nodepthtestplayers`,
  `g_fullbrightplayers`.

### Spawn / respawn  (`sv_vehicles.qc:vehicles_spawn`, `:1107`)
- Reset to idle/ownerless/shootable at the spawn point: `owner=NULL`, `touch=vehicles_touch`,
  `event_damage=vehicles_damage`, `event_heal=vehicles_heal`, `reset=vehicles_reset`, `iscreature=true`,
  `MOVETYPE_STEP`, `SOLID_SLIDEBOX`, `DAMAGE_AIM`, `DEAD_NO`, push to bot targets, `FL_NOTARGET`, zero velocities,
  `think=vehicles_think` at `time`; reset lock state + `misc_bulletcounter`; `angles=pos2`, `setorigin(pos1)`;
  spawn `EFFECT_TELEPORT`; team from controller; remove any hooks aimed at it; run `vr_spawn`;
  `vehicles_reset_colors`; `CSQCMODEL_AUTOINIT`.

### Think  (`sv_vehicles.qc:vehicles_think`, `:1080`)
- `nextthink = time + autocvar_g_vehicles_thinkrate` (**0.1**); mirror `VEHICLESTAT_W2MODE` onto the owner; run
  `vr_think`; `vehicles_painframe`; `CSQCMODEL_AUTOUPDATE`. When owned, the vehicle stops self-thinking and the
  pilot's `PlayerPhysplug` (the per-vehicle `*_frame`) drives it instead.

### Enter  (`sv_vehicles.qc:vehicles_enter`, `:931`)
- **Guards:** reject bots unless `g_vehicles_allow_bots`; require `IS_PLAYER`, `veh.phase < time`,
  `pl.vehicle_enter_delay < time`, not frozen, not dead, not already in a vehicle.
- MULTISLOT gunner branch (only if `g_vehicles_enter`, body owned, same team): `vr_gunner_enter`.
- If `veh.owner` already → return. Teamplay+team+different-team branch: only enters if `g_vehicles_steal`
  (zeroes shield, backs up flags into `old_vehicle_flags`, strips `VHF_SHIELDREGEN`, optional
  `WP_VehicleIntruder` waypoint + steal notifications); else return.
- `RemoveGrapplingHooks(pl)`; zero the vehicle ammo/reload/energy mirrors; link `veh.owner=pl`/`pl.vehicle=veh`;
  freeze player (`DAMAGE_NO`, `SOLID_NOT`, `MOVETYPE_NOCLIP`, `disableclientprediction=1`, `alpha=-1`,
  `view_ofs=0`, `teleportable=false`, clear jetpack); copy colormap; swap weaponslots to `temp_wepent`;
  `STAT(HUD)=vehicleid`; `pl.PlayerPhysplug = veh.PlayerPhysplug`; mirror ammo to the player; clear ONGROUND;
  team from player; clear FL_NOTARGET; `vehicles_reset_colors`; SVC_SETVIEWPORT to `vehicle_viewport` +
  SVC_SETVIEWANGLES; `CSQCVehicleSetup`; `MUTATOR_CALLHOOK(VehicleEnter)`; `CSQCModel_UnlinkEntity`; `vr_enter`;
  `antilag_clear`; CENTER_VEHICLE_ENTER notification.

### Exit  (`sv_vehicles.qc:vehicles_exit`, `:775`)
- Re-entrancy guard (`vehicles_exit_running`). PLAYERSLOT (gunner) → run slot's `vehicle_exit` and return.
- Restore player: SVC_SETVIEWPORT(self)+SETVIEWANGLES(vehic.yaw); `DAMAGE_AIM`, `SOLID_SLIDEBOX`,
  `MOVETYPE_WALK`, clear NODRAW, `teleportable=NORMAL`, `alpha=default`, `PlayerPhysplug=null`, `vehicle=NULL`,
  `view_ofs = STAT(PL_VIEW_OFS)`, `event_damage=PlayerDamage`, `STAT(HUD)=HUD_NORMAL`; restore weaponslots
  switchweapon + delete temp_wepent; `last_vehiclecheck = time+3`; `vehicle_enter_delay = time+2`;
  `setsize(PL_MIN,PL_MAX)`; `CSQCVehicleSetup(HUD_NORMAL)`; kill CPID_VEHICLES notifications. Then: vehic
  `FL_NOTARGET`; stop avelocity (if alive); restore team; kill intruder WP; `MUTATOR_CALLHOOK(VehicleExit)`;
  restore SHIELDREGEN from `old_vehicle_flags`; `phase = time+1`; `vr_exit`; `vehicles_setreturn`;
  `vehicles_reset_colors`; `owner=NULL`; `CSQCMODEL_AUTOINIT`.
- **`vehicles_findgoodexit` (`:751`):** tracebox the preferred spot; else try `g_vehicles_exit_attempts`
  (**25**) random points on a `1.5*size` ring; else fall back to the vehicle origin.

### Damage  (`sv_vehicles.qc:vehicles_damage`, `:625`)
- **Per-weapon damage rate (WEAPONTODO):** Vortex/Machinegun/Rifle ×`0.75`, Vaporizer ×`0.5`, Seeker(tag) ×`5`,
  any other weapon ×`2`, non-weapon ×`1`. Set `dmg_time=time`, `enemy=attacker`, `pain_finished=time`.
- **Shield-then-health:** if `VHF_HASSHIELD` and shield>0, spawn/refresh the `vehicle_shieldent` flash, subtract
  damage from `vehicle_shield`; if it goes negative, spill `|shield|` into RES_HEALTH and play `SND_ONS_HIT2`
  (colormod red, alpha 0.75); else play `SND_ONS_ELECTRICITY_EXPLODE`. Without a shield: `TakeResource(HEALTH)`
  + `SND_ONS_HIT2`.
- Knockback: `velocity += force * damageforcescale` (when `0<scale<1`) else `+= force`.
- **Death (`HEALTH<=0`):** eject (`VHF_DEATHEJECT` → `VHEF_EJECT`, else `VHEF_RELEASE`) → `antilag_clear` →
  `vr_death` → `vehicles_setreturn`.

### Heal  (`sv_vehicles.qc:vehicles_heal`, `:708`)
- `GiveResourceWithLimit(RES_HEALTH, amount, limit||max_health)` if 0<health<limit; mirror % to owner. (Used by
  the bumblebee healray and `func_heal`/heal nades.)

### Regen  (`sv_vehicles.qc:vehicles_regen` `:549` / `vehicles_regen_resource` `:564`)
- If pool<max and `timer + rpause < time`: optionally scale rate by `health/max_health`; add `regen*dt` clamped
  to max; mirror `(pool/max)*100` to the owner.

### Touch / crush / impact  (`sv_vehicles.qc:vehicles_touch` `:874`, `vehicles_crushable` `:721`, `vehicles_impact` `:731`)
- `MUTATOR_CALLHOOK(VehicleTouch)` (true suppresses). When owned: if the toucher is below the top and
  `vehicles_crushable` (a player past `vehicle_enter_delay`, or a monster) and not weapon-locked, and the vehicle
  is moving ≥ `g_vehicles_crush_minspeed` (**100**): `Damage(crush_dmg=70, DEATH_VH_CRUSH, force=crush_force=50
  away)`; otherwise run `vr_impact`. When un-owned and `!g_vehicles_enter`: `vehicles_enter(toucher)`
  (touch-board).
- `vehicles_impact`: `Damage(min(speedfac*Δspeed, maxpain), DEATH_FALL)` once per 0.25s when
  `Δspeed > minspeed`, skipping NOIMPACT surfaces.

### Pain frame  (`sv_vehicles.qc:vehicles_painframe`, `:594`)
- When health ≤ 50%, every `0.1..0.6s`: emit `EFFECT_SMOKE_SMALL`, and (per flags) `VHF_DMGSHAKE` →
  `velocity += randomvec()*30`, `VHF_DMGROLL`/`VHF_DMGHEADROLL` → random angle jitter.

### Turret aim + homing lock  (`sv_vehicles.qc:vehicle_aimturret` `:356`, `vehicles_locktarget` `:99`)
- `vehicle_aimturret`: slew the head toward a world target within pitch/yaw limits at `aimspeed*dt`.
- `vehicles_locktarget`: build `lock_strength` by `incr` toward 1 on a valid (non-team, alive, vehicle/turret,
  visible) traced target, decay by `decr` (×2 if a different target) when off-target; full lock holds for
  `_lock_time`; plays `vehicles/locking.wav` / `lock.wav` / `locked.wav`.

### Shared projectile + gibs  (`sv_vehicles.qc:vehicles_projectile` `:221`, `vehicle_tossgib` `:296`)
- `vehicles_projectile`: a `MOVETYPE_FLYMISSILE` bbox owned by the vehicle, credited to the pilot, explodes via
  `RadiusDamage` on touch/use/think, self-removes at `time+30`; shootable when `_health>0`. Muzzle FX + sound +
  `CSQCProjectile` networking.
- `vehicle_tossgib`: spawn a model gib at a tag with toss physics, optional flame/explode, fade-out think.

### Force-from-tag hover  (`vehicles.qc:vehicles_force_fromtag_hover` `:10` / `_maglev` `:22`, `vehicle_altitude` `:4`)
- Spring traces from an engine tag; closer ground = stronger upward push (`(1-frac)*max_power`); maglev also
  repels ceilings and sinks (`'0 0 -200'`) over a void.

### Auxiliary crosshair + setup networking  (`sv_vehicles.qc:SendAuxiliaryXhair`/`CSQCVehicleSetup`, `cl_vehicles.qc`)
- `UpdateAuxiliaryXhair` links a per-owner `auxiliary_xhair` entity (origin/color SendFlags); `CSQCVehicleSetup`
  writes `TE_CSQC_VEHICLESETUP(vehicle_id)` to the owner (0/HUD_NORMAL tears down the xhairs). CSQC
  `AuxiliaryXhair_Draw2D` projects them 2D; `Vehicles_drawHUD`/`Vehicles_drawCrosshair` draw the cockpit frame,
  health/shield/ammo bars + icons, low-health blink + `SND_VEH_ALARM`, and the colorized crosshair.
- **Client cvars:** `cl_vehicles_alarm=0`, `cl_vehicles_hudscale=0.5`, `cl_vehicles_crosshair_size=0.5`,
  `cl_vehicles_crosshair_colorize=1`, `cl_vehicles_notify_time=15`, `cl_vehicles_hud_tactical=1`.

### Reset / return / use  (`vehicles_reset` `:1095`, `vehicles_setreturn`/`vehicles_showwp`/`vehicles_return`, `vehicle_use` `:522`)
- `vehicles_reset` (round reset): exit any pilot, clear return helper, respawn if active.
- `vehicles_setreturn` spawns a `vehicle_return` helper that, after `respawntime` (−5 dead / −1 alive), draws a
  `WP_Vehicle` waypoint then re-runs `vehicles_spawn`. `vehicle_use` (targetname-controlled) toggles active/team.

### Impulse  (`sv_vehicles.qc:vehicle_impulse`, `:912`)
- A seated player's impulse routes to `veh.vehicles_impulse` first (Raptor/Spiderbot mode set + cycle); the
  shared `IMP_weapon_drop` (17) toggles `cl_eventchase_vehicle`.

## Port mapping

| Base feature | Port symbol | Layer | Notes |
|---|---|---|---|
| `vehicle_initialize` + placement | `VehicleSpawnFuncs.Spawn` (+ descriptor `Spawn`) | authority | folded into spawnfunc + descriptor; **no VehicleInit hook dispatch, no drop-to-floor tracebox** |
| `vehicles_spawn` | `VehicleCommon.SpawnVehicle` | authority | core reset present; teleport FX / CSQCMODEL / hook-removal deferred |
| `vehicles_think` | descriptor `Think` (entity scheduler) | authority | thinkrate 0.1 in `VehicleCommon.DefaultThinkRate`; **W2MODE owner-mirror does NOT exist (descriptors only write vehicle.VehW2Mode, never owner)**; painframe not called |
| `vehicles_enter` | `VehicleBoarding.Enter` + `VehicleCommon.EnterVehicle` | authority | guards/gunner/steal-gate/hook present; **steal gameplay, viewport/HUD net, temp_wepent swap deferred** |
| `vehicles_exit` | `VehicleBoarding.Exit` + `VehicleCommon.ExitVehicle` | authority | restore present; viewport/HUD net, temp_wepent restore, notif deferred |
| `vehicles_findgoodexit` | `VehicleCommon.FindGoodExit` | authority | faithful, `g_vehicles_exit_attempts` 25 |
| `vehicles_damage` | `VehicleCommon.DamageVehicle` | authority | **DEAD: body faithful but NO live caller (only VehicleRuntimeTests); no vehicle sets event_damage/GtEventDamage → live damage falls through to PlayerDamage**; shieldent flash is client |
| `vehicles_heal` | **NOT IMPLEMENTED** | authority | **no shared heal helper / event_heal / owner % mirror; the bumblebee healray inline-heals (it is the SOURCE, not this SINK)** |
| `vehicles_regen[_resource]` | `VehicleCommon.Regen` / `RegenResource` | authority | faithful |
| `vehicles_locktarget` | `VehiclePhysics.LockTarget` | authority | faithful incl. sounds; visibility/alpha gate partial |
| `vehicle_aimturret` | `VehiclePhysics.AimTurret` | authority | faithful slew |
| `vehicles_projectile` | `VehicleCommon.SpawnProjectile` | authority | faithful; CSQCProjectile net + bot-dodge deferred |
| `vehicles_force_fromtag_*` / `vehicle_altitude` | `VehiclePhysics.ForceFromTag` / `Altitude` | shared | faithful |
| `vehicles_touch` crush | **NOT IMPLEMENTED** | authority | no crush damage, no touch-board |
| `vehicles_crushable` | **NOT IMPLEMENTED** | authority | — |
| `vehicles_impact` (fall/collision dmg) | **NOT IMPLEMENTED** | authority | — |
| `vehicles_painframe` (smoke/shake/roll) | **NOT IMPLEMENTED** | authority+presentation | no low-health smoke or DMGSHAKE/DMGROLL |
| `vehicle_tossgib` | **NOT IMPLEMENTED** | presentation | descriptors handle their own death FX |
| `vehicles_setreturn`/`showwp`/`return` | descriptor `Death` reschedules respawn | authority | **no WP_Vehicle return waypoint; living-abandoned vehicle is not returned** |
| `vehicle_use` (targetname controller) | **NOT IMPLEMENTED** | authority | targetname/ACTIVE/delayspawn init not ported |
| `vehicle_impulse` | `VehicleBoarding.Impulse` | authority | live (Commands.cs); chase toggle is a no-op |
| `+use` board (`PlayerUseKey`) | `VehicleBoarding.UseKey` ← `ServerNet` +use edge | authority | **live** |
| `MUTATOR_CALLHOOK(VehicleEnter/Exit)` | `MutatorHooks.VehicleEnter/Exit.Call` | authority | live |
| `MUTATOR_CALLHOOK(VehicleInit/Touch)` | hooks DEFINED, **never `.Call`** | authority | dead |
| `SendAuxiliaryXhair`/`CSQCVehicleSetup`/`Vehicles_drawHUD`/`drawCrosshair` | `VehicleHud.cs` (present) | presentation | **registered but unfed: no live caller sets InVehicle / stats / aux xhairs** |
| in-vehicle cockpit/chase camera (SVC_SETVIEWPORT) | **NOT IMPLEMENTED** | presentation/net | no client reads `p.Vehicle` to switch view |

## Parity assessment

**Gaps (player-observable):**
- **vehicles_damage is DEAD on the live path (headline gap).** `VehicleCommon.DamageVehicle` is a bit-faithful
  port of `vehicles_damage`, but it has NO caller except `tests/VortexArena.Tests/VehicleRuntimeTests.cs:470`. No vehicle entity
  installs a damage handler — neither `vehicles_spawn` (`SpawnVehicle`) nor `vehicles_enter` (`EnterVehicle`)
  set `veh.event_damage`, and grep for `GtEventDamage =` under `Vehicles/` is empty. So when a player shoots a
  vehicle, `DamageSystem.EventDamage` (DamageSystem.cs:294) sees `GtEventDamage == null` and falls through to
  `PlayerDamage(targ)` — the vehicle takes **armor/health-split PLAYER damage** with no per-weapon damagerate, no
  shield-then-health split, no `damageforcescale` knockback, and **no death eject/respawn**. The vehicle damage
  rule set, the death->eject->`vr_death`->respawn chain, and the shield FX/sounds are all unreachable in a match.
- **No shared `vehicles_heal`.** There is no `Heal` helper, no `event_heal` handler, and no owner `vehicle_health`
  percentage mirror. Only the Bumblebee healray heals (inline `GiveResourceWithLimit`) — it is the heal SOURCE,
  not the `vehicles_heal` SINK. `func_heal` / heal nades cannot heal a vehicle.
- **vehicles_locktarget validity filter is incomplete.** The port checks only same-team + dead; QC also rejects
  non-(vehicle|turret) targets and near-invisible (alpha<=0.5) targets, so the port's lock can acquire players
  and any live entity. (The code comment claims the full filter but the code omits it.)
- **No in-vehicle HUD or cockpit camera.** `VehicleHud` exists and is registered in `HudManager`, but nothing on
  the live path calls `ConfigureForVehicle`, sets the `VEHICLESTAT_*` mirror, or sets `InVehicle` — the
  `TE_CSQC_VEHICLESETUP`/`VEHICLESTAT_*`/`SVC_SETVIEWPORT` networking is unwired. A pilot drives with no vehicle
  HUD, no aux lock crosshairs, and (probably) the on-foot first-person view rather than the cockpit/chase view.
- **No crush, no fall/collision damage, no pain-frame FX.** `vehicles_touch` crush (`crush_dmg=70`),
  `vehicles_impact` (DEATH_FALL self-damage), and `vehicles_painframe` (low-health smoke + DMGSHAKE/DMGROLL
  jitter) are entirely missing — vehicles do not run players over, take no terrain/landing damage, and show no
  damage state.
- **No vehicle return waypoint / living-vehicle return.** `vehicles_setreturn`/`vehicles_showwp` is not ported;
  the respawn timer is handled by each descriptor's `Death`, but an abandoned LIVING vehicle is never returned
  and no `WP_Vehicle` radar/return sprite is shown.
- **No targetname controller / delayspawn jitter / ACTIVE gating.** `vehicle_use` and the controller branch of
  `vehicle_initialize` are unported; map-scripted vehicle activation (and the random delayspawn jitter) is absent.
- **VehicleInit / VehicleTouch mutator hooks are dead** (defined, never `.Call`ed) — a mutator cannot abort
  vehicle init or suppress a vehicle touch.
- **Steal mechanic deferred** (`g_vehicles_steal` enemy-board): default-off so not a default-config gap, but the
  shield-zero / intruder-waypoint / steal-notification gameplay is unported.
- **No drop-to-floor at spawn** (the downward tracebox in `vehicle_initialize`) — a vehicle keeps its mapper
  origin; minor for well-placed spawnpoints.

**Liveness:**
- LIVE: boarding (`VehicleBoarding.UseKey` ← `ServerNet` +use rising edge, both per-frame and merged paths),
  seated input feed (`GameWorld.cs:1054` → `VehInput`), exit, impulse routing (`Commands.cs:1324`), descriptor
  think (entity scheduler), regen/lock/aim/projectile core (called from the descriptors), `VehicleEnter/Exit`
  hooks (verified `.Call` sites in `VehicleBoarding.cs`).
- DEAD / unfed: **`vehicles_damage` (`DamageVehicle` — test-only caller; vehicles install no damage handler)**,
  the entire client presentation (`VehicleHud` + aux xhairs + cockpit camera), the VEHICLESTAT_W2MODE / regen
  owner-% mirrors, `VehicleInit` / `VehicleTouch` hooks.
- NOT IMPLEMENTED: `vehicles_heal` shared helper, crush, impact, painframe, tossgib, return waypoint, targetname
  controller, steal gameplay.

> **g_vehicles_enter default mismatch:** Base default is `g_vehicles_enter=0` (touch-to-board is the DEFAULT;
> use-key is the opt-in). The port treats `g_vehicles_enter=1` (use-key) as its parity path and documents
> touch-board as unreachable. So in stock config the boarding METHOD itself diverges, not just the unported
> touch-board path. Tracked under `combat.crush` (touch-board) and `enter.handshake`.

**Intended divergences:** none asserted — the deferrals above are gaps, not deliberate design changes. The port
notes them as "cross-boundary / out of scope" for the headless phase, but they remain parity gaps until wired.

**Values:** the framework numeric defaults that ARE ported match Base exactly (thinkrate 0.1, exit_attempts 25,
per-weapon damage rates 0.75/0.75/0.75/0.5/5/2, enter_radius 250, enter-delay 2s). The crush/impact constants
(crush_dmg 70, crush_force 50, crush_minspeed 100) are unported because the feature is missing.

## Verification
- **Code trace (boarding live):** `game/net/ServerNet.cs:1257` and `:1363` call `VehicleBoarding.UseKey(p)` on
  the +use rising edge — confirmed live. `GameWorld.cs:1052-1058` feeds `VehInput`; `Commands.cs:1324` routes
  impulses. PASS.
- **Code trace (HUD dead):** `VehicleHud.ConfigureForVehicle` / `.SetAuxiliaryXhair` / the stat setters have no
  caller outside the file itself (grep) — unfed. `HudManager.cs:133` only instantiates it. FAIL (dead).
- **Code trace (hooks):** `MutatorHooks.cs` defines `VehicleInit`/`VehicleTouch`/`VehicleEnter`/`VehicleExit`;
  only `VehicleEnter`/`VehicleExit` have `.Call` sites (`VehicleBoarding.cs`). VehicleInit/Touch dead.
- **Code trace (damage DEAD):** `DamageVehicle`'s only caller across `src/`, `game/`, `tests/` is
  `tests/VortexArena.Tests/VehicleRuntimeTests.cs:470`. No `GtEventDamage =` / `event_damage =` assignment
  exists under `src/**/Vehicles/`. `DamageSystem.EventDamage` (DamageSystem.cs:294) routes a non-player with no
  `GtEventDamage` to `PlayerDamage`. So live vehicle damage NEVER reaches `vehicles_damage`. FAIL (dead).
- **Code trace (heal MISSING):** no `Heal` / `vehicles_heal` / `event_heal` helper under `src/**/Vehicles/`; the
  bumblebee healray inline-heals via `GiveResourceWithLimit`. FAIL.
- **Code trace (W2MODE mirror):** grep `owner.VehW2Mode =` → no matches (only `vehicle.VehW2Mode =`). The
  owner-stat mirror QC runs every think is absent. FAIL.
- **Code trace (missing):** no symbol for crush / `vehicles_impact` / `vehicles_painframe` / `vehicles_setreturn`
  / `vehicle_use` anywhere under `src/**` or `game/**` (grep). Confirmed NOT IMPLEMENTED.
- **Unit tests:** `tests/VortexArena.Tests/VehicleRuntimeTests.cs` covers spawn+think, board (every guard),
  drive (VehInput → descriptor frame), impulse routing, exit, death→eject→respawn, multi-seat gunner, and the
  enter/exit hooks — all headless. No test exercises crush/impact/painframe/HUD/camera (the missing features).

## Open questions
- Does the on-foot first-person camera produce a usable (if non-faithful) view while seated, or is the pilot
  effectively blind? Needs a runtime check — no client code reads `p.Vehicle` for the view.
- Are stock vehicle maps actually reachable in the port's map rotation (so a player can ever board), or is the
  whole subsystem unreachable in practice outside tests? The spawnfuncs are registered, but no audited gametype
  here places vehicles except Onslaught.
- Bot vehicle use is gated off in Base by default (`g_vehicles_allow_bots=0`) and the port honors that; whether
  any port bot AI could drive if enabled is untraced.

# Spiderbot vehicle — parity spec

**Base refs:** `common/vehicles/vehicle/spiderbot.qc` · `spiderbot.qh` · `spiderbot_weapons.qc` · `spiderbot_weapons.qh` (shared core in `common/vehicles/sv_vehicles.qc`, `common/physics/movelib.qc`)
**Port refs:** `src/VortexArena.Common/Gameplay/Vehicles/Spiderbot.cs` · `VehicleCommon.cs` · `VehiclePhysicsHelpers.cs` · `VehicleBoarding.cs` · `VehicleSpawnFuncs.cs` · `game/client/VehicleVisuals.cs` · `game/client/VehicleCatalog.cs` · `game/hud/VehicleHud.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The Spiderbot is a single-seat bipedal walker vehicle hand-placed on maps via `spawnfunc(vehicle_spiderbot)`
(active when `g_vehicle_spiderbot 1`, the cfg default). It walks/strafes on legs with a 4-point ground-aligning
suspension, turns a head turret toward the pilot's crosshair, can launch a directional jump that survives any
fall height, and mounts twin alternating-barrel hitscan miniguns (primary, heat/ammo belt) plus a 3-mode
rocket launcher (secondary: VOLLY salvo / GUIDE crosshair-homing / ARTILLERY ballistic lob). It has 800 health
and a 200 regenerating shield. On death it tips over, burns ~3.4s, then detonates a 250-damage radius blast and
respawns after 45s. The pilot drives it via the seated input path; impulses 1/2/3 select the rocket mode and
weapon-next/prev cycle it.

## Base algorithm (authoritative)

### Spawn / setup  (`base_refs: spiderbot.qc:vr_spawn, vr_setup`)
- **Trigger:** `spawnfunc(vehicle_spiderbot)` → `vehicle_initialize(this, VEH_SPIDERBOT, false)` → deferred
  `vehicles_spawn` → `vr_spawn`. `vr_setup` runs once at registration.
- **vr_setup:** sets `VHF_HASSHIELD` (if shield>0), `VHF_SHIELDREGEN`, `VHF_HEALTHREGEN`; `respawntime=45`;
  `RES_HEALTH = 800`; `vehicle_shield = 200`; `max_health = health`; `pushable = true` (can use jumppads).
  Class attrib `spawnflags = VHF_DMGSHAKE`.
- **vr_spawn:** creates gun1/gun2 (`spiderbot_gun` model) attached to `tur_head` at `tag_hardpoint01`/`02`;
  `gravity = 2`; `mass = 5000`; `frame = 5` (idle); `tur_head.frame = 1`; `MOVETYPE_STEP`; `SOLID_SLIDEBOX`;
  alphas = 1; `tur_head.angles = 0`; origin = `pos1 + '0 0 128'`; angles = `pos2`; `damageforcescale = 0.03`;
  `PlayerPhysplug = spiderbot_frame`.
- **Hitbox (spiderbot.qh):** mins `'-75 -75 10'`, maxs `'75 75 125'`. view_ofs `'0 0 70'`, height 170.
- **Models:** body `spiderbot.dpm`, head `spiderbot_top.dpm`, hud/cockpit `spiderbot_cockpit.dpm`.

### Per-frame controller  (`base_refs: spiderbot.qc:spiderbot_frame`)
Runs as the seated pilot's `PlayerPhysplug` each movement frame (dt = `PHYS_INPUT_FRAMETIME`).
1. `game_stopped` → park (SOLID_NOT/DAMAGE_NO/MOVETYPE_NONE) and bail.
2. `vehicles_frame(vehic, this)` (shared); zero the pilot's ZOOM/CROUCH; mirror W2MODE to the player stat.
3. **Aux crosshairs:** average `tag_hardpoint01` + `tag_hardpoint02` positions and the two `v_forward`s, trace,
   `UpdateAuxiliaryXhair`. (Presentation.)
4. **Head turret aim:** `crosshair_trace`, compute the body-relative angle delta to the aim point, clamp the
   per-frame step to `head_turnspeed(110)*frametime`, then clamp yaw to `±head_turnlimit(90)` and pitch to
   `[head_pitchlimit_down(-20), head_pitchlimit_up(30)]`.
5. **`makevectors(vehic.angles + '-2 0 0'*angles.x)`** then `movelib_groundalign4point(springlength=150,
   springup=20, springblend=0.1, tiltlimit=90)` — 4 corner spring traces pitch/roll the chassis to the ground.
6. **On-ground reset:** `if IS_ONGROUND: jump_delay = time`.
7. **Land sound:** on ground with `frame==4 && tur_head.wait != 0` → play `SND_VEH_SPIDERBOT_LAND`, set frame 5.
8. **Jump:** on ground + JUMP held + not latched (`button2`) + `tur_head.wait < time` → play
   `SND_VEH_SPIDERBOT_JUMP`; set `tur_head.wait = time+2`, `jump_delay = time+2`, `button2 = true`; build a
   directional launch from the sign of wishmove: `velocity = sd*700 + rt*600 + v_up*600` (sd = forward axis,
   rt = right axis; defaults to forward when no wishmove); `frame = 4`; clear ONGROUND.
9. **Else (after jump_delay):**
   - **No movement + on ground:** idle sound (`SND_VEH_SPIDERBOT_IDLE`, gated by `sound_nexttime`/`delay`,
     6.4865s loop), `movelib_brake_simple(speed_stop=50)`, frame 5.
   - **Movement:** turn body toward head yaw (turnspeed 90, or turnspeed_strafe 300 when strafing) clamped to
     `tur_head.angles.y`, subtract from head yaw; forward/back → frame 0/1, `movelib_move_simple(forward*sign,
     speed_walk=500, inertia=0.15)`; strafe → frame 2/3, `movelib_move_simple(right*sign, speed_strafe=400,
     inertia=0.15)`; preserve vert velocity, apply half-or-full gravity step when `velocity.z<=20`; walk/strafe
     locomotion sounds gated by `sound_nexttime`/`delay`.
10. **Tilt clamp:** `angles.x` and `angles.z` clamped to `±tiltlimit(90)`.
11. **Minigun (primary):** see below.
12. **Rockets (secondary):** `spiderbot_rocket_do(vehic)` — see below.
13. **Regen:** minigun belt regen (when not firing), shield regen (`VHF_SHIELDREGEN`), health regen
    (`VHF_HEALTHREGEN`).
14. Clear ATCK/ATCK2; compute `vehicle_ammo2 = (9 - tur_head.frame)/8*100` (rocket-belt %) and `reload2`.
15. **Glue pilot:** `setorigin(this, vehic.origin + '0 0 1'*vehic.maxs.z)`; `oldorigin = origin` (negate fall
    damage); `velocity = vehic.velocity`; mirror health/shield % to the player stats.

### Minigun  (`base_refs: spiderbot.qc:spiderbot_frame` minigun block, `spiderbot_weapons.qh`)
- Fires while ATCK held, gated by `vehicle_ammo1 >= ammo_cost(1)` AND `tur_head.attack_finished_single[0] <= time`.
- Alternates `gun1`/`gun2` by `misc_bulletcounter % 2`; fires from the gun's `barrels` tag + `v_forward*50`.
- `fireBullet(spread=0.012, solidpenetration=32, damage=16, force=9, DEATH_VH_SPID_MINIGUN, EFFECT_BULLET)`.
- Plays `SND_VEH_SPIDERBOT_MINIGUN_FIRE`; `Send_Effect(SPIDERBOT_MINIGUN_MUZZLEFLASH)`.
- Deduct ammo; set `attack_finished_single[0] = time + refire(0.06)`. Spin the guns cosmetically
  (`gun.angles.z ±= 45`, wrap at 360).
- **Constants:** damage 16, refire 0.06 (→ ~400 DPS per gun), spread 0.012, ammo_cost 1, ammo_max 100,
  ammo_regen 40/s, ammo_regen_pause 1s, force 9, solidpenetration 32.

### Rocket launcher (3 modes + guide-release)  (`base_refs: spiderbot_weapons.qc:spiderbot_rocket_do`)
- Belt-driven: `tur_head.frame` 1..9 is the belt counter; at frame 9 the reload is `rocket_reload(4s)`, else
  `rocket_refire(0.1s)` (or `rocket_refire2(0.025s)` for VOLLY). `gun2.cnt` gates the next shot.
- **Guide-hold bookkeeping (`wait` field):** while ATCK2 held in GUIDE mode the belt pauses at frame 9/1; on
  release, `spiderbot_guide_release` converts this pilot's in-flight guided rockets to unguided aimed at the
  last crosshair point. `wait == -10` = VOLLY hold-to-empty-belt in progress.
- **SBRM_VOLLY (1):** spawn a dumb rocket (spread `rocket_spread(0.05)`) via `vehicles_projectile`; compute a
  randomized flight time around the crosshair distance and detonate via `vehicles_projectile_explode_think`.
  Holding ATCK2 at frame 1 latches `wait=-10` to auto-empty the belt.
- **SBRM_GUIDE (2):** spawn a rocket flying `v_forward`, set `pos1 = crosshair`, `setthink(spiderbot_rocket_guided)`
  — each tick re-traces the pilot crosshair and steers `velocity = normalize(olddir + newdir*turnrate)*speed`
  with `randomvec()*noise`; detonates on owner death or lifetime. Leaving the vehicle → `spiderbot_rocket_unguided`.
- **SBRM_ARTILLERY (3):** spawn a rocket, compute a ballistic toss via `spiberbot_calcartillery` (solve_quadratic
  on gravity to reach a clearance height over the crosshair point + random scatter `0.75*radius`), `MOVETYPE_TOSS`,
  `gravity = 1`.
- All three: `rocket.classname = "spiderbot_rocket"`, `rocket.cnt = time + rocket_lifetime(20)`; advance the belt
  frame; set the next refire/reload.
- **Constants:** damage 50, force 150, radius 250, speed 3500, spread 0.05, refire 0.1, refire2 0.025,
  reload 4, health 100 (shootable), noise 0.2, turnrate 0.25, lifetime 20.

### Rocket-mode switch  (`base_refs: spiderbot.qc:spiderbot_impulse`)
- impulse 1 → VOLLY, 2 → GUIDE, 3 → ARTILLERY; weapon-next/prev (10/11/12) cycle within [1..3] with wrap; each
  sets `STAT(VEHICLESTAT_W2MODE)` and calls `CSQCVehicleSetup` (HUD/crosshair networking).

### Enter / exit  (`base_refs: spiderbot.qc:vr_enter, spiderbot_exit`)
- **vr_enter:** W2MODE = GUIDE; `MOVETYPE_STEP`; CSQCVehicleSetup; seed the player's vehicle_health/shield %;
  if the pilot carries a CTF flag, reattach it to `tur_head` at `'-20 0 120'`.
- **spiderbot_exit(eject):** in-flight `spiderbot_rocket`s owned by the pilot lose guidance (owner cleared,
  realowner set); the vehicle reverts to `vehicles_think`, frame 5, `MOVETYPE_STEP`. Eject: pilot launched
  `(v_up + v_forward*0.25)*750` at a found exit 100u ahead. Normal: if `|velocity| > speed_strafe(400)` carry
  momentum + `z+200` (exit 128u ahead), else `velocity*0.5 + z+10` (exit 256u ahead). `antilag_clear`.

### Death / blowup  (`base_refs: spiderbot.qc:vr_death, spiderbot_blowup, spiderbot_headfade`)
- **vr_death:** RES_HEALTH 0; DAMAGE_NO; clear touch; `cnt = 3.4 + time + random()*2`;
  `setthink(spiderbot_blowup)`; DEAD_DYING; `frame=10`; head EF_FLAME; colormod dark; `MOVETYPE_TOSS`;
  `CSQCModel_UnlinkEntity` (death scene runs client-locally).
- **spiderbot_blowup:** while `cnt > time` spit random `EFFECT_EXPLOSION_SMALL` + `SND_ROCKET_IMPACT` every 0.1s.
  Then spawn 4 gib entities (body frame 11, head MF_ROCKET+EF_FLAME bouncing with avelocity + fade, 2 guns
  tossed) with dark colormod; `RadiusDamage(this, this.enemy, core=250, edge=15, radius=250, force=250,
  DEATH_VH_SPID_DEATH)`; hide originals; DEAD_DEAD; SOLID_NOT.
- **spiderbot_headfade:** the flung head fades over `1/min(respawntime,10)`, exploding (`SND_ROCKET_IMPACT`,
  `EFFECT_EXPLOSION_BIG`) if it dies while still visible.

### CSQC presentation  (`base_refs: spiderbot.qc:vr_hud, vr_crosshair, vr_setup` CSQC)
- **vr_hud:** `Vehicles_drawHUD(VEH_SPIDERBOT.m_icon, "vehicle_spider_weapon1/2", ammo1/2 icons + colors)`.
- **vr_crosshair:** per-mode reticle — VOLLY → `vCROSS_BURST`, GUIDE → `vCROSS_GUIDE`, ARTILLERY → `vCROSS_RAIN`.
- **vr_setup (CSQC):** the two minigun aux crosshairs use `vCROSS_HINT`.

## Port mapping
| Base feature | Port symbol | Notes |
|---|---|---|
| spawnfunc(vehicle_spiderbot) | `VehicleSpawnFuncs.Spiderbot` → `Spiderbot.Spawn` | LIVE: registered `SpawnFuncs.Register("vehicle_spiderbot", …)` (MapObjectsRegistry.cs:221), instantiated by the BSP entity loader. |
| vr_setup / vr_spawn | `Spiderbot.Spawn` + `VehicleCommon.SpawnVehicle` | health 800, shield 200, flags, gravity 2, hitbox, gun sub-entities, think armed. |
| spiderbot_frame | `Spiderbot.Think` → `Spiderbot.Frame` | LIVE: seated input → `vehicle.VehInput` (GameWorld.cs:1056) consumed by the vehicle's `Think` pumped by `SimulationLoop.RunThink`. |
| head turret aim | `Spiderbot.Frame` (head-aim block) | uses only `tag_hardpoint01` for the muzzle, not the avg of both. |
| movelib_groundalign4point | `VehiclePhysics.GroundAlign4Point` | spring traces + pitch/roll blend. |
| movelib_move_simple / brake_simple | `VehiclePhysics.MoveSimple` / `BrakeSimple` | inertia lerp / horizontal bleed. |
| directional jump | `Spiderbot.Frame` (jump block) | `VehJumpLatched`/`VehLandTime`/`VehJumpDelay`; `sd*700+rt*600+up*600`. |
| minigun | `Spiderbot.FireMinigun` + `FireBulletPenetrating` | alternating barrels, spread, solid-penetration loop. |
| spiderbot_rocket_do (3 modes) | `Spiderbot.RocketDo` | VOLLY/GUIDE/ARTILLERY + guide-release latch. |
| spiderbot_rocket_guided/unguided | `Spiderbot.AttachGuidance` (inline think) | re-traces pilot crosshair; converts to unguided on exit. |
| spiderbot_guide_release | `Spiderbot.GuideRelease` | converts this pilot's guided rockets to unguided. |
| spiberbot_calcartillery | `Spiderbot.CalcArtillery` + `SolveQuadratic` | ballistic solve. |
| spiderbot_impulse | `VehicleBoarding.Impulse` → `Spiderbot.SetMode`/`CycleMode` | LIVE: routed from Commands.cs:1324 before weapon impulses. |
| vr_enter / spiderbot_exit | `Spiderbot.Enter` / `Spiderbot.Exit` + `VehicleCommon.Enter/ExitVehicle` | LIVE via `VehicleBoarding.UseKey` (+use edge, `game/net/ServerNet.cs:1257/1363`). GAP: `vr_enter`'s CTF-flag reattach to `tur_head` at `'-20 0 120'` is NOT ported (no `flagcarried` handling in `src/**/Vehicles`). |
| vr_impact (bouncepain) | — (not ported) | `g_vehicle_spiderbot_bouncepain` default `'0 0 0'` => no-op; no port handler. Flagged for completeness only. |
| vr_death / spiderbot_blowup | `Spiderbot.Death` (think closure) | burn timer + 250-dmg blast + respawn; gibs/EF_FLAME/head-fade are client-cosmetic TODOs. |
| vr_hud / vr_crosshair | `game/hud/VehicleHud.cs` (Spiderbot kind) | weapon1/2 + ammo1/2 art + aux crosshairs. |
| client model/sounds/FX | `game/client/VehicleVisuals.cs` + `VehicleCatalog.cs` | frame-driven legs, barrel spin, muzzle flash, engine sounds (idle/walk/strafe), death gibs (approximate). |

## Parity assessment

### Liveness — LIVE end-to-end.
The whole chain is wired: spawnfunc registered → boardable via +use rising edge (`VehicleBoarding.UseKey`,
ServerNet.cs) → driven (seated input stashed on `vehicle.VehInput` each tick, GameWorld.cs:1052-1059;
`vehicle.Think` pumped by the generic `SimulationLoop.RunThink` DP think scheduler) → mode-switch impulses routed
(`Commands.cs:1324` before weapon impulses) → death/respawn via the descriptor `Death`. `VehicleRuntimeTests.cs`
covers board, drive, projectile spawn, impulse set/cycle, and exit. (The ../../TODO.md "orphaned / VehInput never
written / dead code" note predates the T37 wave-a2 wiring and is now STALE.)

### Logic / values — largely faithful; a few divergences:
- **Head-aim muzzle point (partial):** QC averages `tag_hardpoint01` + `tag_hardpoint02` (and both forward
  vectors) for the aim trace; the port uses only `tag_hardpoint01` with a hardcoded fallback offset. Minor aim
  bias; negligible with both hardpoints close.
- **Body-turn / gravity-step / jump / minigun / rocket constants:** all match the cfg defaults (verified value by
  value against `spiderbot_weapons.qh` and the `autocvar_*` block).
- **Artillery scatter / solve:** ported faithfully (`spiberbot_calcartillery` → `CalcArtillery`), with the same
  solve-quadratic root selection per up/down/straight-line case.
- **VHF_DMGSHAKE / pushable:** the port sets `DmgShake` but does NOT set `pushable` (jumppad use) — a flagged gap
  (the port has no `pushable`/jumppad-pushes-vehicle concept yet). `mass=5000` is also not modeled.
- **CTF flag-reattach on enter (MISSING):** `vr_enter` reattaches a flag-carrier's flag to `tur_head` at
  `'-20 0 120'`; the port's `Enter`/`EnterVehicle` does not (deferred as cross-boundary). A real authority gap —
  notable because CTF flag-rotation is this initiative's validation anchor.
- **vr_impact / bouncepain (MISSING):** not ported; default `'0 0 0'` makes it a no-op, so no live effect with
  stock cvars.

### Timing — DOUBLE defect (sharper than first audited): the seated frame is driven from the vehicle's `Think`
at the 0.1s `DefaultThinkRate`, NOT per movement-frame as QC's `PlayerPhysplug` does. QC `spiderbot_frame` runs
EVERY movement frame and multiplies its turn/gravity steps by that frame's `PHYS_INPUT_FRAMETIME`. The port
INVOKES the controller once per 0.1s but multiplies the same steps by `Api.Clock.FrameTime` (= `TicRate`,
~0.0167s) — i.e. the per-step multiplier is decoupled from the actual 0.1s cadence. Net result: head-turn,
body-turn and the locomotion gravity step accumulate at only ~`TicRate/0.1` (~1/6) of the Base rate, so turning
is roughly **6x slower**, not merely "coarser". This is a magnitude error, not a feel nuance. Flagged on
`frame.controller`, `frame.head_aim`, and `frame.locomotion`.

### Presentation / audio:
- **Engine sounds re-architected (intended):** the port drives idle/walk/strafe as continuous client-side loop
  overlays (`VehicleVisuals` + `VehicleCatalog.EngineSounds`) instead of QC's discrete server `sound()` calls
  gated by `delay`/`sound_nexttime`/frame. Jump is played server-side (`spiderbot_jump.wav`). This is a
  deliberate presentation port (centralized engine-sound model shared across all vehicles).
- **Land sound MISSING:** QC plays `SND_VEH_SPIDERBOT_LAND` on the frame 4→5 landing transition; no port
  equivalent.
- **Death scene simplified:** the QC blowup spawns 4 physics gib entities (head MF_ROCKET+EF_FLAME bounce + fade,
  2 tossed guns, body frame 11) and `spiderbot_headfade`; the port's `VehicleVisuals` death does an approximate
  scripted gib arc (GibCount 6) + death sound, and the server `Death` only does the burn-timer + blast + respawn
  (the per-burn random small explosions are a TODO). Functionally the blast + respawn are faithful; the gib
  fidelity is partial.
- **Per-mode crosshair / aux crosshairs:** `VehicleHud` has the vehicle HUD + aux-crosshair scaffold; the
  per-mode reticle selection (BURST/GUIDE/RAIN) maps onto the W2MODE the server networks. Minigun muzzle flash +
  barrel spin are implemented client-side.

## Verification
- **Liveness / boarding / impulse / projectile-spawn:** `tests/VortexArena.Tests/VehicleRuntimeTests.cs`
  (`Impulse_Spiderbot_SetsAndCyclesRocketMode` boards a spiderbot, asserts default GUIDE, set 1/3, cycle 10/12;
  other tests cover the shared board/drive/exit/projectile-fire path on the sibling vehicles). PASS (code read).
- **Spawnfunc registration:** `MapObjectsRegistry.cs:221` — code read.
- **Seated-drive wiring:** `GameWorld.cs:1052-1059` (VehInput stash) + `SimulationLoop.cs:276` (RunThink) — code read.
- **+use boarding edge:** `game/net/ServerNet.cs:1257,1363` → `VehicleBoarding.UseKey` — code read (the only live
  caller of `UseKey`; it lives under `game/`, not `src/`).
- **Impulse route:** `Commands.cs:1324` `VehicleBoarding.Impulse` before weapon impulses — code read.
- **Constants:** value-by-value diff of `Spiderbot.cs` fields vs `spiderbot_weapons.qh` + the `autocvar_*` block —
  all match.
- **Death blast args:** QC `RadiusDamage` signature (`damage.qh:119`) vs the port `Death` call — match (core 250,
  edge 15, radius 250, force 250, DEATH_VH_SPID_DEATH).
- **Runtime in-game behavior** (head-slew feel at the 0.1s think cadence, land sound, gib visuals, per-mode
  crosshair render) — UNVERIFIED (no in-game session this audit).

## Open questions
- Does the 0.1s think-rate controller (vs QC's per-movement-frame `PlayerPhysplug`) produce a noticeably
  laggier head/body turn and choppier locomotion at high fps? Needs an in-game A/B.
- Is the per-mode secondary crosshair (BURST/GUIDE/RAIN) actually drawn from the networked W2MODE on the client,
  or only the generic vehicle crosshair? `VehicleHud` has the scaffold but the live selection wasn't traced.
- `pushable` (jumppad-launches-the-spiderbot) has no port concept — is that ever exercised on stock vehicle maps?

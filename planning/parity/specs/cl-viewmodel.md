# Client view-model (cl-viewmodel) — parity spec

**Base refs:** `client/view.qc` · `common/weapons/all.qc` · `common/weapons/calculations.qc` · `common/effects/qc/casings.qc` · `common/wepent.qc`
**Port refs:** `game/client/ViewModel.cs` · `game/client/ViewModelEquip.cs` · `game/client/FirstPersonView.cs` · `game/client/ShellCasings.cs` · `game/client/EffectSystem.cs` · `game/net/NetGame.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The first-person weapon view-model is the gun the local player sees in front of the camera: the model
display + skin/colormap, the muzzle flash on each shot, the brass/shell casings ejected, the weapon-switch
raise/lower animation, the per-frame sway (follow / lean / bob), and the alignment/offset placement. In Base
this is a CSQC presentation subsystem (`MAX_WEAPONSLOTS` `viewmodel` entities) driven entirely client-side
from networked weapon state (`wepent.qc` SVQC→CSQC sync + the `wframe`/`w_muzzleflash`/`casings` temp
entities). It is active whenever the local player is alive, in first person (`!chase_active`,
`r_drawviewmodel != 0`), and not in a vehicle. None of it is authority — it is pure view, but it reads
shared weapon state (active/switch weapon, shot origin, animation frame) the server networks.

## Base algorithm (authoritative)

### View-model entity + model select  (`view.qc:viewmodel_draw`, `all.qc:CL_WeaponEntity_SetModel`)
- **Trigger:** `CSQC_UpdateView` (view.qc:1742) loops `viewmodels[slot]` and calls `viewmodel_draw` each
  frame when `r_drawviewmodel` is set and the slot has an `activeweapon`. Runs CSQC (presentation).
- **Algorithm:** `viewmodel_draw` picks the model name from `wep.mdl` (or `wr_viewmodel` override), and only
  rebuilds via `CL_WeaponEntity_SetModel` when the name changed. `CL_WeaponEntity_SetModel` loads the
  `v_<name>.md3` to read the `shot` tag, then loads `h_<name>.iqm` as the animation rig and presets the four
  anim framegroups (`anim_fire1='0 1 0.01'`, `anim_fire2='1 1 0.01'`, `anim_idle='2 1 0.01'`,
  `anim_reload='3 1 0.01'` via `animfixfps`). If the `h_` rig has a `weapon`/`tag_weapon` tag, the `v_` model is
  attached to it as a `weaponchild` ("invisible-hand"); otherwise the rig IS the rendered model. Computes
  `movedir` (shot origin) from the `shot` tag, `spawnorigin` (casing ejection) from the `shell` tag (SVQC),
  and `oldorigin` (muzzle offset) from the `handle`/`weapon` tag.
- **Skin / colormap / glow / effects** (`viewmodel_draw:312-321`): every child entity gets
  `drawmask = (intermission||dead||chase) ? 0 : MASK_NORMAL`, `alpha`, `skin = m_skin`,
  `colormap = 256 + c` (player colors), `glowmod = weaponentity_glowmod(wep,c)` (per-weapon `wr_glow` or the
  palette color), and `csqcmodel_effects = (cheap EF mask | EF_NODEPTHTEST) & ~EF_FULLBRIGHT`.
- **Constants:** `cl_viewmodel_alpha = 1` (max opacity), `cl_viewmodel_alpha_min = 0`, `cl_gunoffset = "0 0 0"`,
  `cl_gunalign = 3`, `r_drawviewmodel = 1`.
- **Edge cases:** in a vehicle (`player_localentnum > maxclients`) alpha is forced -1 (hidden). Dead /
  intermission / chase → `drawmask 0`. `chase_active` forces `r_drawviewmodel -1` so the server still throws
  casings for the now-visible 3rd-person gun.

### Shot origin alignment  (`all.qc:shotorg_adjust`, `shotorg_adjustfromclient`, `calculations.qc:W_GunAlign`)
- `cl_gunalign`: 3 = right (default, model authored position), 4 = left (mirror Y), 1/2 = center
  (`vecs.y = 0; vecs.z -= 2`). `W_GunAlign` (SVQC) auto-resolves slot collisions so dual-wield slots don't
  overlap; CSQC just returns the preferred align. `view_ofs = movedir_aligned - movedir`.

### Weapon-switch raise/lower  (`view.qc:viewmodel_draw:339-362`)
- Reads `this.state` (WS_RAISE / WS_DROP / WS_CLEAR) + `weapon_nextthink` + `weapon_switchdelay` (networked via
  the `wframe` temp entity, all.qc:533-573). Computes a fraction `f` in 0..1 (0 = fully active) using
  `eta = (weapon_nextthink - time)/WEAPONRATEFACTOR`, then **tips the gun down**: `this.angles_x = -90*f*f`.
  So during a raise/drop the gun rotates up to -90° pitch about its origin (barrel swings down out of view).
  `switchdelay` = the weapon's `switchdelay_raise` / `switchdelay_drop`.

### Per-frame sway  (`view.qc:viewmodel_animate`, `calc_followmodel_ofs`, `leanmodel_ofs`, `bobmodel_ofs`)
- **follow** (`cl_followmodel 1`): view-relative velocity → velocity-lowpass (`cl_followmodel_velocity_lowpass
  0.05`) → scale `-cl_followmodel_speed*0.042` → highpass (`0.05`) → lowpass (`0.03`) to turn velocity into a
  lagging acceleration offset. `cl_followmodel_limit 135`. `cl_followmodel_velocity_absolute 0`.
- **lean** (`cl_leanmodel 1`): highpass on the view angles (`cl_leanmodel_highpass1 0.2`, limit
  `cl_leanmodel_limit 30`) → scale `-cl_leanmodel_speed 0.3` → highpass (`0.2`) → lowpass (`0.05`), pitch
  re-inverted at the end. Resets the prev-angle on teleport (`csqcmodel_teleported`).
- **bob** (`cl_bobmodel 1`): ramps `bobmodel_scale` in/out at `±frametime*5` on ground/air; sinusoidal side/up
  sway: `gunorg.y = bspeed*cl_bobmodel_side*sin(s)`, `gunorg.z = bspeed*cl_bobmodel_up*cos(2s)` where
  `s = (time - time_ofs)*cl_bobmodel_speed`, `bspeed = avg_xyspeed*0.01*bobmodel_scale`, speed clamped 0..400,
  bob frequency reduced when crouch-walking (`map_bound_ranges(avg_xyspeed,150,400,0.08,0)`).
  `cl_bobmodel_side 0.2`, `cl_bobmodel_up 0.1`, `cl_bobmodel_speed 10`.
- **vertical view-bob (`cl_bob`/`cl_bob2`/`cl_bobfall`)**: TODO in Base too (view.qc:273-282) — NOT implemented.
- All sway is applied as `this.origin += offset` / `this.angles += gunangles`, then `cl_gunoffset` added.
  `cl_followmodel_time` gates one calc per frame.

### Muzzle flash  (`all.qc:W_MuzzleFlash`, `W_MuzzleFlash_Model`)
- **SVQC** `W_MuzzleFlash` (slot 0 only): `Send_Effect_Except(m_muzzleeffect, shotorg, shotdir*1000, ...)` to
  everyone except the firer (the world-space particle flash others see), spawns an exterior `m_muzzlemodel`
  (a spinning flash md3 — only Devastator `MDL_DEVASTATOR_MUZZLEFLASH` + Machinegun `MDL_MACHINEGUN_MUZZLEFLASH`,
  all others `MDL_Null`), then sends a `w_muzzleflash` temp entity to the firer + their spectators.
- **CSQC** `NET_HANDLE(w_muzzleflash)`: for the local firer, computes the muzzle world position from
  `viewmodels[slot].movedir_aligned` projected through the view vectors and emits `m_muzzleeffect`
  `pointparticles` there; attaches the `m_muzzlemodel` flash to the view-model's `shot` tag. In `chase_active`
  emits at the server shot origin instead. Skipped when `!r_drawviewmodel`.
- `W_MuzzleFlash_Model_Think`: the flash md3 spins/scales/fades over ~3 frames (`frame+=2`, `scale*=0.5`,
  `alpha-=0.25`, every 0.05 s).

### Brass / shell casings  (`casings.qc`)
- **SVQC** `SpawnCasing(vel, ang, casingtype, owner, weaponentity)`: weapons that eject brass call this on fire
  (e.g. machinegun, shotgun, rifle, vortex). Origin = `weaponentity.spawnorigin` projected from the player
  view vectors. Sent as the `casings` temp entity per-client, gated on `cl_casings`, `r_drawviewmodel`, PVS,
  and `sound_allowed` (silent flag). Bit 0x40 = first-person owner (client adds `cl_gunoffset`), 0x80 = silent.
- **CSQC** `NET_HANDLE(casings)`: spawns a `MOVETYPE_BOUNCE` casing (state 1 = shotgun shell
  `MDL_CASING_SHELL`, else bullet `MDL_CASING_BULLET`), tumbling avelocity `'0 10 0'+100*prandomvec`,
  `bouncefactor` 0.25 (shell) / 0.5 (bullet), advanced by `Movetype_Physics_MatchTicrate` at
  `cl_casings_ticrate 0.03125`, fades over its last second, lifetime `cl_casings_shell_time 30` /
  `cl_casings_bronze_time 10`, `cl_casings_maxcount 100`. Bounce sound (`brass*`/`casings*`) on touch when
  `vdist(velocity,>,50)`.

## Port mapping
- **Model select + skin/glow/colormap** → `ViewModel.SetWeaponModel` + `ViewModelEquip.Build` (invisible-hand vs
  full-rig classification, faithful to `CL_WeaponEntity_SetModel`). `EquipNetworkedWeapon` (NetGame:1364)
  rebuilds only on a weapon-id change and hides on dead/holster/chase. **Skin / colormap (player colors) /
  glowmod / per-child draw effects are NOT applied** — the gun renders with its base material, no team-tint or
  glow, no `EF_NODEPTHTEST` (so it can clip world geometry / be occluded). `cl_viewmodel_alpha`/`_min` unused.
- **Alignment** → `ViewModel.GunAlignOffset` (center drops 2u; right/left side offset is hardcoded `0`, so 3/4
  are visually identical and rely on the v_ model's authored position). `cl_gunoffset` → `GunOffset` export.
- **Switch raise/lower** → `ViewModel.PlayHolster`/`PlayRaise` + `UpdateSwitch`: an INTENDED DIVERGENCE — instead
  of the QC `-90*f*f` pitch-tip keyed to `weapon_switchdelay`/`weapon_nextthink` (which the port does not
  network), the gun slides straight down off-screen on a keypress-predicted holster with a server-confirm raise
  + auto-recover grace. Driven live from `RunBoundCommand` (holster on switch impulse) + `EquipNetworkedWeapon`
  (raise on confirmed change).
- **Sway** → `ViewModel.FollowModelOffset` / `LeanModelOffset` / `BobModelOffset` — a faithful, near
  line-by-line port of the QC lowpass/highpass/avg_factor macros, same constants. Fed by `BuildViewState`
  (NetGame:1331) from predicted velocity + live view angles + onground. Does NOT handle
  `cl_followmodel_velocity_absolute` or the lean teleport reset (`csqcmodel_teleported`). Toggles/constants are
  `[Export]` defaults that match Base but are NOT read from cvars.
- **Muzzle flash** → local: `ViewModel.Fire` → `EffectSystem.MuzzleFlash` — the FULL effectinfo `Spawn` path
  (parsed blocks + block dlight + faithful/modern router, the same path the networked copy takes) at the
  `tag_shot` socket's live world transform with `dir*1000`, + a flash `OmniLight3D` + recoil; driven live from
  NetGame fire prediction (`PredictFireShot`/`UpdateLocalFireFeedback`). (The old heuristic attached burst,
  `MuzzleFlashAttached`, was deleted — it made every weapon's local flash read generic, playtest #49.)
  Remote: server `EffectEmitter.Emit("*_MUZZLEFLASH", ..., except: actor)` → world-space burst (the QC
  `Send_Effect_Except`). Secondary shots flash on the press edge (`SecondaryFiresShot` gate).
- **Casings** → `ShellCasings.cs` is a complete, faithful client casing sim (bounce, tumble, fade, maxcount,
  real `casing_bronze.iqm`/generated shell). BUT it is **DEAD**: `EffectSystem.SpawnCasing` has ZERO live
  callers and there is NO server-side `SpawnCasing`/`casings` emission. No brass is ever ejected in a match.
  (Constants also differ: bullet 1.5s vs Base 10s, shell 2.0s vs 30s, maxcount 64 vs 100.)
- **Weapon fire/idle/reload animation** → the QC `wframe` net temp entity is NOT networked; the port plays a
  best-effort local "fire"/"idle" clip via `ModelAnimator` when the model has one. No reload anim, no
  server-synced frame timing.
- **Camera/zoom/FOV/eventchase** (the other half of `view.qc`) → `FirstPersonView.cs`, live and faithful
  (separate concern from the gun model; covered here as the view container).
- **Fill light** → `ViewModel._fillLight`: a port-only constant light so the gun isn't a black silhouette
  (intended divergence; DP relies on `EF_FULLBRIGHT`-ish viewmodel lighting the port doesn't replicate).

## Parity assessment
- **logic:** model select + invisible-hand/full-rig branch + equip-on-change + sway chain are faithful and
  live. Switch anim is a deliberate re-implementation. Casing logic exists but is unreachable. Muzzle is
  particle-only (no model). Skin/colormap/glow logic missing.
- **values:** sway constants match Base exactly. Casing lifetimes/maxcount differ. `cl_viewmodel_alpha`,
  `cl_gunalign` right/left side, `cl_gunoffset` are inert (defaults baked, not cvar-driven).
- **timing:** sway frametime handling faithful (avg_factor clamp). Switch timing diverges (custom durations,
  not `switchdelay_*`). Casing ticrate moot (dead).
- **presentation:** gun renders + sways + flashes. Missing: team colormap, glowmod, no-depth-test, muzzle
  model, casings, networked fire/reload anim, viewmodel alpha fade.
- **audio:** casing bounce sounds (`brass*`/`casings*`) absent (casings dead). Muzzle flash itself is silent in
  Base too (the fire sound is the weapon's, separate unit). `na` for the model/sway features.
- **liveness:** view-model + sway + local/remote muzzle flash are LIVE on the NetGame match path. Casings are
  DEAD. `ClientWorld.OnMuzzleFlash` wrapper has no caller (NetGame calls `_viewModel.Fire()` directly) — dead
  wrapper, but the feature is live by another path.

### Gaps (observable)
1. No brass/shell casings ever eject from any weapon (dead `ShellCasings`, no server emit) — Base shows
   tumbling, bouncing, fading casings on every machinegun/shotgun/rifle/vortex shot.
2. View-model ignores player team colors and per-weapon glowmod — the gun never tints to your team color or
   glows (e.g. Vortex charge glow), unlike Base.
3. View-model has no `EF_NODEPTHTEST` — the gun can clip into / be occluded by nearby world geometry instead of
   always drawing on top.
4. `cl_gunalign` left/right are visually identical (side offset hardcoded 0); only center differs. Menu
   gunalign radio buttons therefore barely change the gun.
5. `cl_viewmodel_alpha` / `cl_viewmodel_alpha_min` do nothing; the menu opacity slider is inert for the gun.
6. Sway/align toggles (`cl_followmodel`/`cl_leanmodel`/`cl_bobmodel`/`cl_gunoffset`) are baked `[Export]`
   defaults, not read from cvars — the menu "Gun model swaying/bobbing" checkboxes don't affect the live gun.
7. Weapon-switch animation is a slide-down, not the Base barrel-tip (`-90*f*f`); not keyed to per-weapon
   `switchdelay_raise/drop`.
8. No muzzle-flash MODEL for Devastator / Machinegun (the spinning flash md3) — only the particle burst.
9. No networked weapon fire/idle/**reload** animation (`wframe`); the gun doesn't visibly cycle/reload.
10. Casing lifetimes (1.5/2.0s) + maxcount (64) differ from Base (10/30s, 100) — moot while casings are dead.

### Intended divergences
- Weapon-switch slide-down (vs `-90*f*f` tip) with keypress-prediction + auto-recover — documented in
  `ViewModel.PlayHolster`/`PlayRaise`; chosen because the port doesn't network `wframe`/`switchdelay`.
- View-model fill light — port-only legibility fix for missing fullbright viewmodel lighting.

## Verification
- Code read of `ViewModel.cs`, `ViewModelEquip.cs`, `FirstPersonView.cs`, `ShellCasings.cs`, `EffectSystem.cs`,
  `NetGame.cs` (equip/fire/switch/sway wiring) — confirms liveness of model+sway+muzzle, deadness of casings.
- `grep` confirms `EffectSystem.SpawnCasing` has no caller and no server casing emit exists (casings dead).
- `grep` confirms no `wframe` networking, no skin/colormap/glowmod application, no `cl_viewmodel_alpha` read.
- Sway math diffed against `view.qc` macros — faithful (same lowpass/highpass/avg_factor, same constants).
- NOT verified at runtime: exact on-screen gun placement, whether the heuristic muzzle burst reads like Base.

## Open questions
- Should casings be wired live (server emit + `SpawnCasing`)? The client sim is ready; only the emit is missing.
- Are the menu viewmodel cvars (`cl_gunalign`/`cl_followmodel`/`cl_viewmodel_alpha`) expected to be live? They
  are wired in the settings dialog but ignored by `ViewModel`.
- Does the missing colormap/glowmod matter for gameplay readability (team weapon tint, Vortex charge glow)?

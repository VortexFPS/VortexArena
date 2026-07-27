# cl-view — parity spec

**Base refs:** `client/view.qc`, `client/view.qh`, `lib/csqcmodel/cl_player.qc` (CalcRefdef / bobbing / smoothing / chase / deathtilt / idle), `client/hud/crosshair.qc:DrawReticle`
**Port refs:** `game/client/FirstPersonView.cs`, `game/client/ViewEffects.cs`, `game/client/ViewModel.cs`, `game/client/ReticleOverlay.cs`, `game/net/NetGame.cs` (host wiring), `src/XonoticGodot.Net/FaithfulViewSmoothing.cs`, `src/XonoticGodot.Common/Physics/PlayerPhysics.cs:CheckPunch`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
`cl-view` is the client-only first-person view driver: every frame `CSQC_UpdateView` (view.qc) + `CSQCPlayer_SetCamera`/`CSQCPlayer_CalcRefdef` (cl_player.qc) compute the rendered camera origin and angles from the predicted player state, then layer on view effects. It owns: the zoom/FOV state machine (`+zoom`, weapon zoom, spawn-zoom, zoom-scroll, velocity-zoom, sensitivity scaling, the `*0.75` 4:3 frustum), the event/death chase camera and the classic `chase_active` third-person cam, the view-bob (vertical `cl_bob`, horizontal `cl_bob2`, fall-bob `cl_bobfall`) and view-roll (`cl_rollangle`), the punch-angle/punch-vector recoil kick, the death tilt (`v_deathtilt`), idle view-waving (`v_idlescale`), stair + viewheight smoothing, the screen tints (`HUD_Damage` red flash, `HUD_Contents` liquid tint), the GLSL post-process blur/sharpen, night-vision, the zoom reticle, the demo free-camera, and the viewmodel sway (`cl_followmodel`/`cl_leanmodel`/`cl_bobmodel`). It is purely presentation — none of it changes authoritative state (the only network feedback is the FPS report and weapon-hook localcmds).

Important Xonotic shipped defaults: `cl_bob 0`, `cl_bob2 0`, `cl_rollangle 0`, `v_kicktime 0`, `v_deathtilt 0`, `v_idlescale 0`, `cl_velocityzoom_enabled 0`, `gl_polyblend 0` — so vertical/horizontal/fall view-bob, view-roll, death-tilt, idle-wave and velocity-zoom are **OFF by default** and a stock match never shows them. `cl_bobmodel/followmodel/leanmodel 1`, `cl_spawnzoom 1`, `cl_reticle 1`, `hud_damage 0.55`, `hud_contents 1`, `fov 100`, `cl_zoomfactor 5`, `cl_zoomspeed 8`, `cl_eventchase_death 2`, `cl_eventchase_distance 140` are ON by default.

## Base algorithm (authoritative)

### Zoom / FOV state machine  (`view.qc:GetCurrentFov`, `IsZooming`, `ZoomScroll`)
- **Trigger:** every frame from `View_UpdateFov` → `GetCurrentFov(autocvar_fov)` (cl). `IsZooming` ORs `button_zoom`, each weapon's `wr_zoomdir`, and the spectator zoom toggle.
- **Algorithm:** integrate `current_viewzoom` (1 = unzoomed .. `1/zoomfactor` fully zoomed). Holding zoom: `current_viewzoom = 1/bound(1, 1/current_viewzoom + drawframetime*zoomspeed*(zoomfactor-1), zoomfactor)`; releasing eases back to 1. `zoomspeed<0` snaps instant. Spawn-zoom branch (when `cl_spawnzoom && zoomin_effect`) eases out from `1/spawnzoomfactor`. Camera-active eases `current_viewzoom` toward 1 by `drawframetime`. `current_zoomfraction` derived 0..1. `setsensitivityscale(current_viewzoom ** (1-cl_zoomsensitivity))`. Velocity-zoom multiplies the frustum by an exp of averaged speed. Final frustum: `frustumy = tan(fov*PI/360) * 0.75 * current_viewzoom * velocityzoom`, `fovx/fovy = atan2(frustum,1)*360/PI`.
- **Constants:** `fov 100`, `cl_zoomfactor 5` (clamp 1..30 else 2.5), `cl_zoomspeed 8` (range 0.5..16 else 3.5), `cl_zoomsensitivity 0`, `cl_spawnzoom 1`, `cl_spawnzoom_factor 2`, `cl_spawnzoom_speed 1`, `MAX_ZOOMFACTOR 30`, `cl_zoomscroll 1`, `cl_zoomscroll_scale 0.2`, `cl_zoomscroll_speed 16`, `cl_velocityzoom_enabled 0`, `cl_velocityzoom_factor 0`, `cl_velocityzoom_type 3`, `cl_velocityzoom_speed 1000`, `cl_velocityzoom_time 0.2`.

### Event / death chase camera  (`view.qc:WantEventchase`, `View_EventChase`; classic `CSQCPlayer_ApplyChase`)
- **Trigger:** `View_EventChase` each frame (cl). `WantEventchase` returns true on game-stop/intermission, viewloc, vehicle chase, frozen, or death (`cl_eventchase_death`: 1 = immediately, 2 = only once the corpse stops moving / once running, default 2), or spectated-change.
- **Algorithm:** pivot = raw player origin lifted by `cl_eventchase_viewoffset '0 0 20'` (ceiling-traced); pull back along `-forward` by a smoothed `eventchase_current_distance` that eases toward `cl_eventchase_distance 140` at `cl_eventchase_speed 1.3`; box-trace (`cl_eventchase_mins '-12 -12 -8'`/`maxs '12 12 8'`, MOVE_WORLDONLY) so the cam clears walls; sets `chase_active -1`. The classic `chase_active>0` path (`CSQCPlayer_ApplyChase`) uses `chase_back`, `chase_up`, `chase_front`, `chase_overhead`, `chase_pitchangle`.
- **Constants:** `cl_eventchase_death 2`, `cl_eventchase_distance 140`, `cl_eventchase_speed 1.3`, `cl_eventchase_viewoffset '0 0 20'`, `cl_eventchase_mins/maxs`, `cl_eventchase_frozen 0`, `cl_eventchase_vehicle 1`, `cl_eventchase_vehicle_distance 250`, `cl_eventchase_spectated_change 0`, `chase_back`, `chase_up`, `chase_front 0`.

### View bob / roll / fall-bob / punch / deathtilt / idle  (`cl_player.qc:CSQCPlayer_CalcRefdef` and friends)
- **Trigger:** `CSQCPlayer_CalcRefdef` (cl), non-chase, non-intermission branch.
- **Algorithm:** `view_angles += view_punchangle`; `view_angles.z += CalcRoll` (side velocity → roll up to `cl_rollangle` past `cl_rollspeed`); `vieworg += view_punchvector`; `vieworg = ApplyBobbing` (vertical `cl_bob`/`cl_bobcycle` sin-cycle scaled by xy-speed; horizontal `cl_bob2`; fall-bob `cl_bobfall` on landing); `ApplyDeathTilt` (rolls `view_angles.z` to `v_deathtiltangle` over time when `v_deathtilt` and dead, NOT under `cl_eventchase_death 2`); `ApplyIdleScaling` (`v_idlescale` sin-waves pitch/yaw/roll). `view_punchangle`/`view_punchvector` are networked from the server (engine fields) and decayed server-side in `PM_check_punch` (punchangle 10°/s, punchvector 30u/s).
- **Constants:** `cl_bob 0`, `cl_bobcycle 0.5`, `cl_bob_limit 7`, `cl_bobup 0.5`, `cl_bob2 0`, `cl_bob2cycle 1`, `cl_bobfall 0.05`, `cl_bobfallcycle 3`, `cl_bobfallminspeed 200`, `cl_rollangle 0` (DP default 2.0), `cl_rollspeed 200`, `v_deathtilt 0`, `v_deathtiltangle`, `v_idlescale 0`, `v_kicktime 0`.

### Stair + viewheight smoothing  (`cl_player.qc:CSQCPlayer_ApplySmoothing`)
- **Algorithm:** `stairsmoothz` glides the view Z toward the real Z bounded by `±cl_stairsmoothspeed*dt` (within a step height), reset on teleport/airborne/ground-entity; `viewheightavg` low-passes the eye height over `cl_smoothviewheight` for a smooth crouch transition.
- **Constants:** `cl_stairsmoothspeed 200`, `cl_smoothviewheight 0.05`.

### Screen tints + post-process + night vision  (`view.qc:HUD_Damage`, `HUD_Contents`, `View_PostProcessing`, `View_NightVision`)
- **HUD_Damage:** accumulator `myhealth_flash` rises with `dmg_take*hud_damage_factor`, decays by `hud_damage_fade_rate`/s, alpha = `flash - pain_threshold` (with a near-death sin-pulsing threshold), drawn as `gfx/blood` (or a gentle solid colour). Constants `hud_damage 0.55`, `hud_damage_factor 0.025`, `hud_damage_fade_rate 0.75`, `hud_damage_maxalpha 1.5`, `hud_damage_pain_threshold 0.1`, `hud_damage_color "1 0 0"`, the `*_pain_threshold_lower*` set.
- **HUD_Contents:** low-passed `contentavgalpha` fades in (`hud_contents_fadeintime 0.02`) / out (`hud_contents_fadeouttime 0.25`) tinting per content: water `0.4 0.6 1.0`@0.5, lava `1.0 0.3 0.0`@0.7, slime `0.0 0.8 0.0`@0.7.
- **View_PostProcessing:** sets `r_glsl_postprocess_uservec1/2` for damage/content blur and powerup edge-sharpen.
- **View_NightVision:** yellow tint + animated noise when `r_fakelight>=2`/`r_fullbright` (and server allows).

### Zoom reticle  (`crosshair.qc:DrawReticle`)
- Draws `gfx/reticle_normal` (generic `+zoom`) or the weapon scope (`w_reticle`, type 2) while zooming, alpha = `max(0.25, current_zoomfraction) * cl_reticle_*_alpha`; suppressed when dead/spectating/chase (unless `cl_reticle_chase`). `cl_reticle 1`, `cl_reticle_stretch 0`.

### Demo free camera / lockview / orthoview / FPS counter  (`view.qc:CSQC_Demo_Camera`, `View_Lock`, `View_Ortho`, `fpscounter_update`)
- Demo playback chase/free camera; `cl_lockview` freezes origin/angles; `cl_orthoview` top-down radar render; `STAT(SHOWFPS)` reports FPS to the server.

## Port mapping
| Base feature | Port symbol | Liveness |
|---|---|---|
| Zoom/FOV state machine (`GetCurrentFov`) | `FirstPersonView.UpdateZoom` + `ComputeVerticalFov` | live (NetGame.UpdateCamera) |
| Spawn-zoom | `FirstPersonView.TriggerSpawnZoom` (NetGame health 0→>0 edge) | live |
| Weapon zoom (Vortex secondary) | NetGame `weaponZoom` → `_view.ZoomHeld` | live |
| Sensitivity scale | `FirstPersonView.SensitivityScale` | live (read by host) |
| Velocity-zoom | NOT IMPLEMENTED (menu cvar only) | na |
| Zoom-scroll | NOT IMPLEMENTED | na |
| Zoomscript auto-zoom + button mgmt (View_CheckButtonStatus) | partial: unpress-zoom-on-death via host gate; zoomscript/weapon-switch-unpress/hook absent | partial |
| Event/death chase cam | `FirstPersonView.ApplyEventChase` | live |
| Classic `chase_active` third-person | partial: `ChaseMode.Chase` pulls back, but `chase_back/up/front/overhead/pitchangle` ignored | dead (no host sets ChaseMode≠None) |
| Punch-angle recoil | `Entity.PunchAngle` (set by weapons, decayed `CheckPunch`, networked, added to view in NetGame:3529) | live |
| Punch-vector recoil | NOT IMPLEMENTED (not decoded by ClientNet, not applied) | na |
| View-bob (vertical/horizontal) | `FirstPersonView.ApplyBobbing` (cl_bob/cl_bob2 — off by default) | live |
| Fall-bob (`cl_bobfall`) | `FirstPersonView.ApplyBobbing` — ON by default (0.05, Base parity) | live |
| View-roll (`cl_rollangle`) | `FirstPersonView.CalcRoll` (off by default) | live |
| Death tilt (`v_deathtilt`) | `FirstPersonView.ApplyDeathTilt` (off by default) | live |
| Idle view-wave (`v_idlescale`) | `FirstPersonView.ApplyIdleScaling` (off by default; menu "View waving while idle") | live |
| Stair + viewheight smoothing | `FaithfulViewSmoothing` (faithful mode) / `ClientNet.PredictedStairOffset` (port mode) | live |
| HUD_Damage red flash | `ViewEffects.UpdateDamage` | live |
| HUD_Contents liquid tint | `ViewEffects.UpdateContents` | live |
| GLSL blur/sharpen post-process | NOT IMPLEMENTED (no Godot analogue) | na |
| Night-vision | NOT IMPLEMENTED | na |
| Zoom reticle | `ReticleOverlay.UpdateReticle` | live |
| Viewmodel sway (follow/lean/bob) | `ViewModel.UpdateSway` | live |
| Demo free camera | NOT IMPLEMENTED | na |
| Lockview / orthoview | NOT IMPLEMENTED | na |
| Viewloc (2.5D side-scroller, GetViewLocationFOV) | NOT IMPLEMENTED | na |
| Spectator camera (View_SpectatorCamera) | NOT IMPLEMENTED (spectate is first-person SampleRemote) | na |
| FPS report to server | NOT IMPLEMENTED | na |

## Parity assessment
- **Zoom/FOV** — faithful: the `current_viewzoom` integration, the spawn-zoom latch, `current_zoomfraction`, sensitivity scaling, the `*0.75` frustum, the Vortex secondary-zoom fold-in all match. Live values seeded (`cl_zoomfactor 5`, `cl_zoomspeed 8`, `fov 100`). The 2.5/3.5 fallbacks are only used when unset and match Base's out-of-range clamp. **Gap:** velocity-zoom and zoom-scroll are not implemented (menu cvars exist but nothing reads them) — both default-off / opt-in, so a stock match is unaffected.
- **Event/death chase** — faithful for the death-cam (`cl_eventchase_death 2` corpse-settle gate + `eventchase_running` latch, the viewoffset ceiling-trace pivot, the smoothed pull-back, the world-only box-trace with start-solid fallback). **Gap:** the classic user `chase_active`/`chase_front`/`chase_overhead` third-person cam (the `CSQCPlayer_ApplyChase` knobs `chase_back`/`chase_up`/`chase_pitchangle`) is not wired — *verified zero `CameraMode =` assignments in the whole repo*, and the menu sliders for `chase_back`/`chase_up` (DialogSettingsGame.cs:489-496) are dead. Spectator third-person (`View_SpectatorCamera` + `STAT(CAMERA_SPECTATOR)` 1/2 cycling) is also absent — spectating follows the target in FIRST person via `SampleRemote`. Vehicle chase, frozen chase, spectated-change chase, the intermission/game-stop/viewloc chase triggers, and the `WantEventchase` mutator hook are unimplemented.
- **Punch / bob / roll / tilt / idle** — punch-angle is faithful and live (decay 10°/s matches `PM_check_punch`). ~~The view-bob, fall-bob, view-roll, death-tilt and idle-wave are unimplemented~~ — STALE (2026-06-22): all five are now ported in `FirstPersonView` (`ApplyBobbing`/`CalcRoll`/`ApplyDeathTilt`/`ApplyIdleScaling`, transcribed from `CSQCPlayer_ApplyBobbing`/`CalcRoll`/`ApplyDeathTilt`/`ApplyIdleScaling` in cl_player.qc). Of these only the FALL-bob is ON in stock Xonotic (`cl_bobfall 0.05`; `cl_bob 0`, `cl_rollangle 0`, `v_deathtilt 0`, `v_idlescale 0` are opt-in via the View settings tab). Note the Base CSQC TODO stub at `viewmodel_animate` lines 273-285 is the *viewmodel-entity* bob (the gun), not the camera — the camera bob Base actually runs is the cl_player.qc path ported here. Punch-vector: see the resolved note below (nil observable impact under default content). **Verified: no stock Xonotic code ever sets `view_punchvector` to a non-zero value** (only resets + decay; the freezetag/`server/client.qc`/`util.qc` occurrences are `= '0 0 0'` resets or bmodel angle-coupling), so the missing origin kick is invisible under default content — the gap is real in code but has nil observable impact.
- **Smoothing** — faithful in `cl_movement_*` faithful mode (`FaithfulViewSmoothing` ports the bounded `stairsmoothz` glide + `viewheightavg` blend, defaults 200 / 0.05); the port-default mode uses an adaptive render-only stair offset (intended divergence — see `camera-drift-render-smoothing` memory).
- **Screen tints** — `HUD_Damage` and `HUD_Contents` are faithful in logic/values/timing (accumulator + pain threshold + near-death pulse; per-content fade in/out + colours). Presentation diverges: the port draws a solid red `ColorRect` instead of the `gfx/blood` splatter texture, and the gentle-mode randomized colour path isn't ported. **Gaps:** the GLSL blur/sharpen post-process and night-vision are not implemented (no Godot analogue) — drivers for those are engine shader cvars.
- **Reticle** — faithful: type selection, the `max(0.25, zoomfraction)` fade, stretch vs square geometry, the dead/spectate/chase suppression, the weapon-scope vs generic choice.
- **Viewmodel sway** — faithful: `calc_followmodel_ofs`, `leanmodel_ofs`, `bobmodel_ofs` ported with the same low/high-pass macro chains and constants; live with the predicted view feeding `ViewStateProvider`.
- **Demo cam / lockview / orthoview / FPS report** — unimplemented (orthoview is a radar/dev tool; demo cam is being tracked separately on the demo-cinematics branch).

## Verification
- Code-read of `FirstPersonView.cs`, `ViewEffects.cs`, `ViewModel.cs`, `ReticleOverlay.cs`, `FaithfulViewSmoothing.cs`, `PlayerPhysics.CheckPunch`, and the NetGame wiring (UpdateCamera/BuildViewState/UpdateReticle/UpdateEffects/EquipNetworkedWeapon) confirmed liveness on the net play path.
- ~~Grep over `src/`+`game/` confirmed velocityzoom/zoomscroll/cl_bob/cl_rollangle/v_deathtilt/v_idlescale/... have no live readers~~ — STALE (2026-06-22): the bob/roll/tilt/idle family was ported into `FirstPersonView` in a later wave (registry rows `cl-view.bob.view_bob` / `cl-view.tilt.death_idle` track it).
- Base defaults read from `xonotic-client.cfg` and `_hud_common.cfg`.
- **2026-07-05 runtime-verified**: `--camera-trace` jump-land capture on stormkeep (tools/camera-ref/jumpland.json) shows the landing dip — land at vz −251 → eye swings to −12.56u (= clamp(vz,−400,0)×0.05, exact) at the sin midpoint and recovers over 1/cl_bobfallcycle = 0.33 s; a `cl_bobfall 0` control kills it.

## 2026-07-05 update — landing bob + idle sway made reachable
- `cl_bobfall`'s read fallback was 0 while Base ships `cl_bobfall 0.05` (xonotic-client.cfg:151) — with the stock cfg tree loaded the store supplied 0.05 anyway, but any store-less/context-degraded read silently disabled the shipped-ON landing dip. The fallback now matches Base (`FirstPersonView.ApplyBobbing`).
- The Settings→Game→View checkboxes (cl_eventchase_death / cl_bobfall / cl_smoothviewheight / cl_bob(+cl_bob2) / v_idlescale) were wired as BIT-flag toggles with bit 0 — `(v & 0) != 0` is always false and toggling wrote the value back unchanged (int-truncated), so every one of them was inert and could corrupt the cvar. They are now faithful QC value-pair checkboxes (`makeXonoticCheckBoxEx` semantics — midpoint load rule, mindecimals yes/no save, `makeMulti` cl_bob→cl_bob2), so "View waving while idle" (v_idlescale 1/0) and friends actually work.

## Open questions
- **RESOLVED (adversarial pass 2026-06-22):** the third-person path is fully dead — grep found zero `CameraMode =` assignments anywhere in `game/` or `src/`; the spectate path follows the target in first person via `SampleRemote` (NetGame:3420), never engaging a chase pull-back. `View_SpectatorCamera`/`STAT(CAMERA_SPECTATOR)` is unported.
- **RESOLVED (adversarial pass 2026-06-22):** stock Xonotic never sets `view_punchvector` non-zero — every `punchvector` write in the Base QC tree is a `= '0 0 0'` reset or bmodel angle-coupling (no weapon/knockback `+=`). So the missing origin kick has nil observable impact under default content.
- `ViewEffects` uses a solid-colour rect for the damage flash; is the `gfx/blood` splatter intended to be restored, or is the solid tint an accepted divergence? (Still open — currently flagged as a `presentation: partial` gap, not an intended divergence.)

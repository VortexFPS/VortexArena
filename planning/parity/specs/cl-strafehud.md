# StrafeHUD panel (#25) — parity spec

**Base refs:** `client/hud/panel/strafehud.qc` (804 ln main) · `client/hud/panel/strafehud.qh` (cvar defaults + enums) · `client/hud/panel/strafehud/{util,draw,draw_core,extra}.qc(+.qh)` · `client/hud/panel/strafehud/_mod.{inc,qh}` (build glue)
**Port refs:** `game/hud/StrafeHudPanel.cs` (single-file port) · feed: `game/net/NetGame.cs:4382` · gate: `game/hud/HudPanel.cs:108`, `game/hud/HudManager.cs:269`, `game/hud/HudLayoutDefaults.cs:82` · tests: `tests/VortexArena.Tests/HudPanelRegistryTests.cs:334`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-07-02

> Supersedes the coarse row `cl-hud.panel.strafehud` in `registry/cl-hud.yaml` ("faithful-to-stock
> detail unverified"). Movement simulation itself is owned by `physics-player.yaml`; this unit scores
> only how the HUD reads and visualizes it.

## Overview
The StrafeHUD (by Juhu, k9er physics docs at gitlab.com/otta8634/xonotic-physics) is a Race/CTS
training bar: a horizontal angle strip centered on the player showing, for the current speed and
physics, which view-angle relative to velocity gains speed. Zones: **neutral** (no change),
**pre-accel** (speed preserved), **accel** (the strafe sweet-spot), **overturn** (speed loss). On top:
a ratio-colored current-angle marker, a best-angle ghost, switch indicators (where to flip strafe
direction), W-turn markers, a slick-surface detector frame, fading text readouts (start speed, jump
height, vertical angle, strafe efficiency) and an optional audible "sonar". Default enable is `3` =
Race/CTS only, hidden while free-observing.

## Base algorithm (authoritative)

### Show gate (`strafehud.qc:HUD_StrafeHUD` 21-52)
- Hidden when `hud_panel_strafehud` 0; when `spectatee_status == -1` and mode is 1 or 3; when mode 3
  and the `HUD_StrafeHUD_showoptional` mutator hook (implemented by `cl_race.qc`/`cl_cts.qc`) is false.
  Values ≥4 fall through every check (behave as always-on). `_hud_configure` forces drawing with a
  demo sweep. `dynamichud` toggles HUD shake/scale.

### Player state (`strafehud.qc` 54-122, `util.qc` 109-347)
- `strafeplayer` = local csqcplayer, or `CSQCModel_server2csqc(player_localentnum-1)` when spectating
  (`is_local = !(spectatee_status > 0 || isdemo())`). **Works while spectating and in demos.**
- `jumpheld` from PHYS_INPUT_BUTTON_JUMP/JETPACK (local) or `keys & KEY_JUMP` (remote), suppressed by
  track_canjump. While held, air physics is assumed (anti-flicker on landing).
- `onground` (local `IS_ONGROUND`, remote `!anim_implicit_state & INAIR`); on ground the **slick check**
  is a 1qu-down `tracebox` testing `Q3SURFACEFLAG_SLICK`. A ground timeout
  (`hud_panel_strafehud_timeout_ground` 0.1 s) keeps ground physics briefly after leaving a slick ramp.
- Waterlevel via a side-effect-free `_Movetype_CheckWater` probe; swimming zeroes the ground timeout
  and suppresses jump-height capture.
- Physics inputs are **per-entity stats**: `PHYS_MAXSPEED/MAXAIRSPEED/ACCELERATE/AIRACCELERATE/
  SLICKACCELERATE/FRICTION(_SLICK)/STOPSPEED/AIRSTOPACCELERATE(_FULL)/MAXAIRSTRAFESPEED/
  AIRSTRAFEACCELERATE/AIRCONTROL(_FLAGS/_PENALTY/_POWER)/AIRACCEL_QW` — these carry haste/mutator
  modifiers. Crouch: `maxspeed_mod = 0.5` when `IS_DUCKED(csqcplayer)`. In `_hud_configure`:
  maxspeed 320, maxaccel 1, speed 1337.

### Wish angle / keys (`util.qc` 178-236, main 124-148)
- Local: `wishangle = RAD2DEG * atan2(movement.y, movement.x)` wrapped past ±90; forward keys from
  sign of `movement.x`; `movespeed = min(vlen(vec2(movement)), maxspeed)`, 0 → maxspeed.
- Remote: from `STAT(PRESSED_KEYS)` — 45° if fwd/back held else 90°, negated for KEY_LEFT, 0 with no
  side key; movespeed = maxspeed.
- `strafekeys = |wishangle| > 45`. Air side-strafe "turn" mode latches while strafekeys, retained for
  `timeout_turn` 0.1 s; `strafity = 1-(90-|wishangle|)/45` GeomLerps maxspeed toward
  MAXAIRSTRAFESPEED and maxaccel toward AIRSTRAFEACCELERATE.

### Frame time (`util.qc:StrafeHUD_DetermineFrameTime` 138-175)
- Predicted local player: weighted arithmetic mean of `input_timelength` (weight = the frametime
  itself: `dt = Σdt²/Σdt`), frames > 0.05 s halved first (DarkPlaces server split), refreshed every
  `fps_update` 0.5 s. Spectating: `dt = ticrate`. Then `maxaccel *= dt * movespeed`;
  `bestspeed = max(movespeed - maxaccel, 0)`.

### Friction replica (main 200-235)
- On ground, `strafespeed` = speed after one tick of engine friction using the frame-rate-independent
  replica from `ecs/systems/physics.qc`: `independent_geometric = (1 - friction*dt_r)^(dt/dt_r)` with
  **`dt_r = PHYS_FRICTION_REPLICA_DT = 0.00390625` (1/256)**; three cases around stopspeed S=100.
  `frictionspeed = speed - strafespeed`. Slick uses `PHYS_FRICTION_SLICK` (stock 0.5).

### Angles & zones (main 237-543)
- `angle` = velocity yaw − view yaw, wrapped to ±180 (interior); forwards/backwards from keys or
  |angle|≤90 (or wishangle sign at ±90); backwards shifts angle ±180 and negates wishangle;
  `v_flipped` negates both.
- Best/prebest/overturn (deg, RAD2DEG·acos forms):
  - ground+friction: `best = acos(bestspeed/strafespeed)`; `prebest = acos(sqrt(movespeed²+strafespeed²−speed²)/strafespeed)`
    (cases → 0 / 90); `overturn = acos((speed²−strafespeed²−maxaccel²)/(2·maxaccel·strafespeed))` (cases → 180 / 0).
    High-speed-landing degeneracy handled per `onground_mode`: 0 = overturn fills bar, 1 = collapse to
    bestangle, 2 (default) = use air formulas.
  - air: `best = acos(bestspeed/speed)`, `prebest = acos(movespeed/speed)`,
    `overturn = acos(−airstopaccel·maxaccel/2 / speed)` (or the not-applied-fully form
    `acos(−maxaccel/(2·speed−(airstopaccel−1)·maxaccel))` when `!PHYS_AIRSTOPACCELERATE_FULL`);
    airstopaccel 0→1, disabled while side-strafing.
- W-turn (aircontrol, penalty 0, `airaccel_qw==1` or `wturn_unrestricted`): power==2 →
  `acos(−speed/a·(cos((acos(V)+2π)/3)·2+1))` with `a = 32·|aircontrol|·dt`, `V = 1−a²/speed²` when
  `wturn_proper` and in-range, else the asymptote `ACOS_SQRT2_3_DEG = 35.2643896827546543153`
  (`acos(sqrt(2/3))`); other powers → `acos(sqrt(p/(p+1)))` (non-proper only).
- "Normal" (W+A) ghost angles recomputed from MAXAIRSPEED/AIRACCELERATE for switch display while
  W-turning or side-strafing (`switch` modes 2/3).
- Direction left/right from wishangle sign, else angle vs `antiflicker_angle` (0.01). Mode 0
  (view-centered, default) shifts everything by −angle; mode 1 (velocity-centered) moves the
  current-angle marker instead (with ±180 antiflicker hold).

### Bar rendering (`draw_core.qc:DrawStrafeMeter` 5-137, `draw.qc:DrawStrafeHUD` 8-180)
- Not moving → neutral fills the bar (drawfill or progressbar style). Moving → zone strip order
  preaccelR, accelR, overturn (`360−2·overturn`), accelL, preaccelL, neutral (remainder), shifted by
  `neutral/2 − wishangle + shiftangle`, then each segment drawn with 360° wrap into a mirror copy;
  `hidden_width = (360−range)/range·panel_size.x`. Styles: 0 drawfill, 1 progressbar (skin art),
  2 gradient (two `R_BeginPolygon` vertex-gradient quads, projection applied per-vertex later),
  3 soft gradient (per-1px-segment loop, needed for correct color in non-linear projections).
- Projection (`util.qc:Project*` 24-79): linear; perspective `tan(r)/tan(range/2)` (clamp 170°);
  panoramic `tan(r/2)/tan(range/4)` (clamp 350°). HUD range (`DetermineHudAngle` 238-293):
  `range` 90 default; 0 → minimal containing angle; −1 → `getproperty(VF_FOVX)`;
  `range_sidestrafe` −2 default (= use normal), else GeomLerp by strafity.
- Indicators (`draw_core.qc` 140-197): dashed line (`n·2−1` segments) + 45° triangle arrows
  (top/bottom per `_arrow` mode 1/2/3) at the projected offset, clamped to the HUD range. Current
  angle color: neutral `1 1 0` / preaccel+accel `0 1 1` / overturn `1 0 1`, blended by `|strafe_ratio|`
  for gradient styles. Switch indicators gated by `speed >= minspeed` (`switch_minspeed` −1 → auto
  `bestspeed + frictionspeed`). Text is offset below/above by the max indicator extrusion.

### Extras (`extra.qc`)
- **Slick detector** (23-105): hemispherical `traceline` fan below the feet — polar steps
  `90°/2^granularity` (granularity bound 0-4), azimuth same step, radius `slickdetector_range` 200;
  hit = `Q3SURFACEFLAG_SLICK` or any hit when `PHYS_FRICTION==0`; draws top+bottom bars
  (height 0.125·panel, color `0 1 1`, alpha 0.5) and reserves that height for text placement.
- **Text indicators** (108-213, `draw.qc:DrawTextIndicator` 308-338): fade `cos(frac·π/2)`; pos
  vectors in panel-relative units (`±1` = above/below). Start speed latches `race_timespeed` when
  `race_nextcheckpoint==1` (or 254/255 finish==start) at a new `race_checkpointtime`. Jump height
  tracks origin.z rise while `velocity.z>0` and airborne, min display 50 qu. Vertical angle =
  `−pitch`, 2 decimals. Strafe efficiency = `strafe_ratio·100%`, white→green/red blend. Units via
  `hud_speed_unit` 1-5 (qu/m/km/mi/nmi; speed variants m/s, km/h, mph, knots — factors 0.0254,
  ×3.6, ×0.6213711922, 1.943844492).
- **Sonar** (216-272): when enabled and `strafe_ratio ≥ sonar_start` 0.5, plays
  `hud_panel_strafehud_sonar_audio` ("misc/talk") on CH_INFO at interval
  `0.333333 + (−0.222222)·ratio^exp`, volume `0.333333 + 0.666666·ratio^exp`, pitch
  `(0.9 + 0.1·ratio^exp)·100`, ATTN_NONE; sound path re-fixed + precached on cvar change.

## Port mapping
`game/hud/StrafeHudPanel.cs` is a 1:1 single-class port of the four QC files (region comments map to
each). Inputs arrive via properties: `Player` (velocity/angles/onground/origin/IsDead), `WishDir`,
`JumpHeld`, `OnSlick`, `FovX`, `RaceStartSpeed`/`RaceCheckpointTime`, `SpeedUnit`.
**Live wiring feeds only `Player` (`NetGame.cs:4382`, = `LocalServerPlayer`, null on a pure client).**
Show gate via `ResolveVisible` → `ResolveShowMode` (`HudPanel.cs:108`) with `RaceOrCts` from
`HudManager.cs:269`; default enable "3" (`HudLayoutDefaults.cs:82`). All ~100 behaviour cvars are
registered with Base defaults in `RegisterDefaults` (127-239) **except the 13 sonar cvars**. PHYS_*
becomes a shim over the global cvar store (261-280). Sonar: NOT IMPLEMENTED. Slick trace fan: NOT
IMPLEMENTED (display-only via the never-fed `OnSlick`).

## Parity assessment
- **Logic** — the math core (strafe angle, zone acos forms incl. onground_mode triage, turn/W-turn,
  bar wrap/mirror/gradients, projections, indicators, text placement, demo sweep) is a verbatim
  transcription (verified by line-by-line diff). Partial at the edges: player-state acquisition
  (no waterlevel/slick probe, no spectate path), non-local wishangle fallback hardcoded to W+A 45°
  instead of reading pressed keys, frame-time estimator replaced by ticrate, sonar missing.
- **Values** — cvar defaults match Base exactly (pinned by `HudPanelRegistryTests`). Three concrete
  numeric defects: `dt_r` 1/60 vs Base **0.00390625**; `PhysSlickAccelerate` reads `sv_airaccelerate`
  (stock 2) instead of `sv_slickaccelerate` (stock 15); `PhysAirStopAccelerateFull` reads
  `sv_aircontrol==0` instead of `sv_airstopaccelerate_full`. Crouch maxspeed_mod and per-player stat
  modifiers (haste) never reach the HUD. `hud_speed_unit` ignored (SpeedUnit property never fed).
  `FovX` never fed (range −1 mode wrong unless fov=90).
- **Timing** — timeouts (ground/turn) and text fades faithful; dt = ticrate always (no weighted
  frame-time average, no 50 ms split) skews zone widths vs client fps.
- **Presentation** — bar/indicator geometry faithful. Intended divergences: STYLE_GRADIENT rendered
  via the per-segment soft path (projected up-front) instead of two vertex-gradient polygons;
  STYLE_PROGRESSBAR loses the skin's progressbar art (flat fill); `drawstring_aspect` approximated by
  centered text with an 8-64 px font clamp. Port adds NaN/IsFinite guards absent in QC (inert).
- **Audio** — sonar wholly missing (`audio: missing` on its row; every other row `na`).
- **Liveness** — the panel is live in local/listen-server Race/CTS play (default show-mode 3). But:
  on a **pure network client it never draws** (Player stays null); spectating shows nothing;
  WishDir/JumpHeld/OnSlick/FovX/Race*/SpeedUnit have **zero live callers**, so side-strafe mode,
  W-turn detection (wturning), the slick warning, jump-held anti-flicker and the start-speed readout
  are unreachable in a real match even where the code is faithful.

## Verification
- Code diff of all 2260 Base lines vs `StrafeHudPanel.cs` (this audit).
- `tests/VortexArena.Tests/HudPanelRegistryTests.cs:334` `StrafeHud_CoreBehaviourCvarDefaults_MatchBase`
  pins mode/style/range/range_sidestrafe/unit_show/projection/onground_*/timeouts/antiflicker/fps_update.
- Liveness by grep: `WishDir|JumpHeld|OnSlick|FovX|RaceStartSpeed|RaceCheckpointTime|SpeedUnit` have no
  assignments outside the class; `NetGame.cs:4378-4383` comment confirms Player-only feed by design.
- Base constants checked at source: `common/physics/player.qh:96` (SLICKACCELERATE stat), `:331`
  (PHYS_FRICTION_REPLICA_DT), `physicsX.cfg` (sv_slickaccelerate 15, sv_friction_slick 0.5).
- No runtime/visual check performed this audit (all `faithful` claims are code-diff based).

## Open questions
- Should the port feed WishDir/JumpHeld from `BindTable`/the pending move (both exist client-side —
  `NetGame.cs:5543/5780`) or keep the degraded mode as an intended privacy/simplicity divergence?
  Currently scored as unintended (the Base panel's core purpose — visualizing *your own inputs* — is lost).
- Whether the HUD-configure editor reaches `_hud_configure`/demo sweep live is owned by cl-hud.
- `PhysAirStopAccelerateFull` and `PhysSlickAccelerate` fixes are one-line cvar-name changes; the
  friction-replica `dt_r` fix is one constant — all three are safe surgical candidates.

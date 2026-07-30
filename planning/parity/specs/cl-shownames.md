# cl-shownames (player nameplates / health bars / fade) — parity spec

**Base refs:** `client/shownames.qc`, `client/shownames.qh`, `_hud_common.cfg:331-349`, `client/mutators/events.qh:241-246`
**Port refs:** `game/client/ShowNamesLayer.cs`, `game/hud/ShownamesPanel.cs`, `game/net/NetGame.cs` (wiring), `tests/VortexArena.Tests/ShowNamesTests.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-06-22

## Overview
A purely-presentation client overlay: a floating name tag (and, for living teammates, a small
health/armor status bar) drawn in screen space above each visible player, projected from the
player's world origin through the first-person view. Tags fade in/out by branch (dead, view-blocked,
off-screen, overlapping, teammate, enemy), shrink and dim with distance, and may be gated to only
appear near the crosshair. It is enabled by `hud_shownames` (default on) and is entirely client-side
— it consumes the per-player networked `entcs` slice (origin / health / armor / team / dead / name),
computes nothing about gameplay, and emits no audio.

## Base algorithm (authoritative)

### Per-frame iteration over all clients (`shownames.qc:Draw_ShowNames_All`)
- **Trigger / entry:** CSQC `CSQC_UpdateView` draw pass (presentation), every rendered frame.
- **Algorithm:** early-out if `!hud_shownames`. A static `shownames_ent` LinkedList holds one
  `shownames_tag` pure entity per client slot (`maxclients`, indexed by `sv_entnum`). For each: fetch
  the `entcs_receiver(i)`; if none, `make_pure` and skip. Run the entcs think (interpolation). Skip if
  `!has_origin`. If `entcs.m_entcs_private` (private slice is only networked for teammates / self):
  copy `healthvalue` + `RES_ARMOR` and set `sameteam = true`; else zero health/armor and
  `sameteam = false`. Compute `dead = entcs_IsDead(i) || entcs_IsSpectating(i)`. Move the tag origin to
  the entcs origin (unless dead and already faded). Set `csqcmodel_isdead = dead`. Call `Draw_ShowNames(it)`.
- **State / networking:** the data source is the `entcs` (entity-cs) networked per-player record.
  `m_entcs_private` is the server-gated flag that gives a viewer health/armor + sameteam ONLY for
  teammates (and self) — enemies never expose health/armor to the tag.

### Per-tag draw + fade (`shownames.qc:Draw_ShowNames`)
- **Self gate:** if `sv_entnum == current_player + 1` (self or spectatee): return unless
  `chase_active`; then return unless `hud_shownames_self` OR (`spectatee_status > 0 && time <=
  spectatee_status_changed_time + 1`) — i.e. the own tag shows only in third-person, and briefly when
  switching spectatee.
- **Team/enemy gate:** `if (!sameteam && !hud_shownames_enemies) return;`
- **Line of sight:** if `!hud_shownames_crosshairdistance && sameteam` → `hit = true` (teammates skip
  the trace). Else `traceline(view_origin, origin, MOVE_NOMONSTERS, this)` and
  `hit = !(trace_fraction < 1 && trace_networkentity != sv_entnum && trace_ent.entnum != sv_entnum)`
  (blocked only when world geometry, not the player itself, is between eye and player).
- **Projection:** `o = project_3d_to_2d(origin + eZ * hud_shownames_offset)`.
- **Crosshair-distance gate:** if `hud_shownames_crosshairdistance` set: compute screen distance from
  center `(w,h)`; if `d*d > w*w+h*h` record `pointtime = time`; if
  `pointtime + hud_shownames_crosshairdistance_time <= time` → `overlap = 1` (fade out); else if
  `!hud_shownames_crosshairdistance_antioverlap` → `overlap = 0` (skip anti-overlap).
- **Anti-overlap:** if `overlap == -1 && hud_shownames_antioverlap`: for every OTHER tag whose entcs
  has an origin and whose projected box is on-screen, if the two screen boxes overlap AND the other is
  closer to the viewer (`vlen2` compare) → `overlap = 1`.
- **Fade ramp** (ordered if/else, branch order is load-bearing):
  1. `dead` → `alpha = max(0, alpha - SHOWNAMES_FADESPEED*0.25*frametime)` (slow fade-out)
  2. `!sameteam && !hit` (blocked enemy) → `alpha -= SHOWNAMES_FADESPEED*frametime`; reset `fadedelay = 0`
  3. `OFF_SCREEN(o)` → `alpha -= SHOWNAMES_FADESPEED*frametime`
  4. `overlap > 0` → ramp toward `hud_shownames_antioverlap_minalpha` from whichever side
  5. `sameteam` → `alpha = min(1, alpha + SHOWNAMES_FADESPEED*frametime)`
  6. `time > fadedelay || alpha > 0` (enemy fade-in) → `alpha += SHOWNAMES_FADESPEED*frametime`
  (`OFF_SCREEN(o)` ≡ `o.z<0 || o.x<0 || o.y<0 || o.x>vid_conwidth || o.y>vid_conheight`.
  `fadedelay` is seeded to `time + SHOWNAMES_FADEDELAY` the first frame.)
- **Alpha assembly:** `a = hud_shownames_alpha * alpha`. Then `if (!sameteam || self)` multiply by
  `entcs_GetAlpha(sv_entnum-1)` (the remote model's render alpha; `0→1`, `<0→0`). Then
  `MUTATOR_CALLHOOK(ShowNames_Draw, this, a)` (return if consumed; re-read `a`). Return if
  `a < ALPHA_MIN_VISIBLE`.
- **Distance fade + cull:** if `hud_shownames_maxdistance`: `max_dist = min(maxdistance,
  max_shot_distance)`; return if `dist >= max_dist`; between `mindistance` and `maxdistance`,
  `a *= (f - max(0, dist-min)) / f` with `f = max - min`. Else return if `dist >= max_shot_distance`.
- **Resize:** if `hud_shownames_resize && dist >= mindistance`,
  `resize = 0.5 + 0.5*(f - max(0, dist-min))/f` (floor 0.5).
- **Geometry:** `mySize = (aspect, 1) * fontsize`; `myPos = o - (0.5*mySize.x, mySize.y)`; scale by
  resize about the anchor. Sets `box_org`/`box_ofs` for the anti-overlap test (expanded for the status
  bar when drawn).
- **Status bar:** if `hud_shownames_status && sameteam && !csqcmodel_isdead`: a half-width red health
  bar (baralign 1, divided by `hud_panel_healtharmor_maxhealth`) and a half-width green armor bar
  (baralign 0, divided by `hud_panel_healtharmor_maxarmor`), with an optional grey highlight backing
  when `hud_shownames_statusbar_highlight`. Drawn via `HUD_Panel_DrawProgressBar` with the
  `nametag_statusbar` skin image.
- **Name:** `entcs_GetName(sv_entnum-1)`; if `(decolorize==1 && teamplay) || decolorize==2` →
  `playername(s, team, true)` (strip the player's own `^`-color codes). `textShortenToWidth` to the box
  width, then `drawcolorcodedstring` centered on the anchor at `fontsize` and alpha `a`.

### Constants (Base defaults, `_hud_common.cfg:331-349` + `shownames.qh`)
| cvar | default | units / meaning |
|---|---|---|
| `hud_shownames` | 1 | master enable |
| `hud_shownames_enemies` | 1 | show enemy tags |
| `hud_shownames_crosshairdistance` | 0 | px from crosshair gate (0 = off) |
| `hud_shownames_crosshairdistance_time` | 5 | s tag stays after pointing |
| `hud_shownames_crosshairdistance_antioverlap` | 0 | allow anti-overlap with crosshairdistance on |
| `hud_shownames_self` | 0 | own tag in chase/spectate |
| `hud_shownames_status` | 1 | teammate health/armor bar |
| `hud_shownames_statusbar_height` | 4 | px bar height |
| `hud_shownames_statusbar_highlight` | 1 | grey backing on bar |
| `hud_shownames_aspect` | 8 | name drawing-area aspect |
| `hud_shownames_fontsize` | 12 | px font size |
| `hud_shownames_decolorize` | 1 | 0 never / 1 team-only / 2 always strip name color |
| `hud_shownames_alpha` | 0.7 | tag max alpha |
| `hud_shownames_resize` | 1 | shrink with distance |
| `hud_shownames_mindistance` | 1000 | qu, start fade/shrink |
| `hud_shownames_maxdistance` | 5000 | qu, alpha/size 0 |
| `hud_shownames_antioverlap` | 1 | fade the farther of two overlapping tags |
| `hud_shownames_antioverlap_minalpha` | 0.4 | overlap floor |
| `hud_shownames_offset` | 52 | qu, Z offset above origin |
| `SHOWNAMES_FADESPEED` | 4 | const, fade rate /s |
| `SHOWNAMES_FADEDELAY` | 0 | const, enemy fade-in delay |
| `ALPHA_MIN_VISIBLE` | 0.003 | const, min drawable alpha |
| `max_shot_distance` | 32768 | qu, hard cull cap |

## Port mapping
`ShowNamesLayer.cs` is a `Control` on a low CanvasLayer (Layer 3, below HUD) added in
`NetGame.SetupCameraAndHud` (line ~1283) under the `Waypoints` layer; `_Process` calls `QueueRedraw`,
`_Draw` runs the full algorithm every frame. It reads the remote player set from
`ClientNet.RemoteIds`, the interpolated pose from `SampleRemote`, and the health/armor/team/dead slice
from `TryGetRemoteState`; the display name comes from `ResolveScoreboardName` (the scoreboard name
slice, the port's `entcs_GetName` stand-in). Per-tag fade state (`alpha`/`fadedelay`/`pointtime` +
the screen box) lives in a `TagState` dict keyed by net id. `ShownamesPanel.cs` is a no-draw cvar
registrar that seeds the 19 `hud_shownames_*` defaults verbatim. `NetGame._Process` (line ~2461)
feeds `LocalNetId`, `ChaseActive`, `LocalTeam` each frame.

Mapping is feature-complete on the core path: the team/enemy gate, the six-branch fade ramp (exact
speeds + branch order), the crosshair-distance gate, anti-overlap box test, distance fade, resize,
geometry, teammate status bar (health red / armor green / highlight backing), decolorize rule, and
all 21 constants are reproduced. `ShowNamesTests.cs` locks the cvar defaults, the fade-branch order
and step math, distance fade + resize curves, status-bar gating, and decolorize rule, plus the new
networked ARMOR codec field.

## Parity assessment

### Faithful (live)
- Cvar defaults, fade-ramp branches/speeds, distance fade/resize, geometry, status bar, decolorize,
  team/enemy gate, crosshair-distance gate, anti-overlap, offset — all reproduced and the layer is
  wired live (instantiated + added to the tree + fed per frame; `_Draw` runs every frame).

### Gaps / divergences
- **Self / spectatee own-tag is effectively dead.** The local player is predicted, not interpolated,
  so it is never in `ClientNet.RemoteIds` (HandleSnapshot skips `LocalNetId`). The self branch
  (chase + `hud_shownames_self`) is present verbatim but unreachable, and the spectatee-switch window
  (`spectatee_status > 0 && time <= spectatee_status_changed_time + 1`) is not modelled at all. Observable:
  in third-person with `hud_shownames_self 1`, the local player has no tag; when spectating, the
  spectatee's tag does not get the 1s post-switch grace.
- **LOS trace is a `Fraction >= 0.99` heuristic, not the entnum check.** QC excludes the player's own
  edict from blocking (`trace_networkentity`/`trace_ent.entnum`). The port can't match a remote to a
  trace edict, so it treats "nearly reached the target" as clear LOS. Observable: a tag may flicker
  when something thin (or the target player's own collision, if present in the client trace world) sits
  right at the eye-line; generally a close approximation.
- **`entcs_GetAlpha` factor not applied.** QC multiplies enemy/self tag alpha by the remote model's
  render alpha (so a fading/cloaked/respawning model's tag fades with it). The port has no per-remote
  model alpha and leaves the factor at 1. Observable: a tag stays full-alpha while its player model is
  mid-fade (e.g. spawn-shield / invisibility / respawn fade).
- **`MUTATOR_CALLHOOK(ShowNames_Draw)` not invoked.** No-op in stock gameplay — no shipped Base
  mutator implements this hook (verified: only the `events.qh:241-246` definition and the
  `shownames.qc:139` CALLHOOK exist, zero `MUTATOR_HOOKFUNCTION(*, ShowNames_Draw)`), so this is
  harmless today; flagged only because a future/custom mutator that hooks it would not run.
- **Name is never truncated to the tag width.** QC calls `textShortenToWidth(s, namewidth, …)` (with
  `namewidth = mySize.x`) before `drawcolorcodedstring`, clamping the name to its drawing area. The port
  (`ShowNamesLayer.DrawName`) draws the full name centered on the anchor with **no** width clamp, so long
  names overflow the tag box. (An earlier draft of this spec wrongly stated truncation was present.)
- **Port adds a 1px black drop-shadow behind the name** (`DrawString` offset at `alpha*0.7`); QC's
  `drawcolorcodedstring` has no shadow. Cosmetic presentation divergence.
- **`sameteam` + health/armor source is the public net slice, not `m_entcs_private`.** QC only
  populates health/armor AND sets `sameteam` from the private entcs record the server networks to
  teammates/self (`m_entcs_private`, set when the ENTNUM property bit is received — `ent_cs.qc:359`);
  enemies get `sameteam = false` and zeroed health/armor. The port derives `sameteam` client-side from
  `(s.Colormap & 0xFF) == LocalTeam` (`ShowNamesLayer.cs:170`) and reads `s.Health`/`s.Armor` from the
  always-present remote state regardless of team (`:344`). Since the status bar is teammate-only anyway
  the visible result matches, but (a) the `sameteam` authority differs (client-side vs server-gated),
  and (b) if the port's net layer streams enemy health it is available where QC withholds it (potential
  info-parity divergence vs Base; depends on the server's entity-state gating — see Open questions).
  This is now tracked as two registry rows: `cl-shownames.gate.team_enemy` (logic downgraded to partial)
  and the new `cl-shownames.entcs.private_healtharmor`.

## Verification
- **Constants / fade math / gates:** verified by `ShowNamesTests.cs` (mirrors Base values + branch
  order + step formulas) and by direct read of `ShowNamesLayer.cs` against `shownames.qc`.
- **Liveness:** verified by tracing `NetGame.SetupCameraAndHud` (instantiation + `AddChild`) and
  `NetGame._Process` (per-frame feed); `_Process → QueueRedraw → _Draw`. Live.
- **Self/spectatee, entcs_GetAlpha, LOS entnum, mutator hook:** verified absent/divergent by code read
  (the port's own comments at `ShowNamesLayer.cs:156-160, 267-268, 477-487` acknowledge the first three).
- **Not run in-game this audit** — visual fidelity (font, status-bar skin image vs the QC
  `nametag_statusbar` gfx) not observed at runtime.

## Open questions
- Does the port's server actually stream enemy health/armor to clients, or is the public slice zeroed
  like `m_entcs_private`? If enemy health reaches the client, it is a behavioral info-parity gap even
  though the bar isn't drawn for enemies.
- Runtime visual check: does the status bar use the `nametag_statusbar` skin image (QC) or flat rects
  (port draws `DrawRect`)? The port uses flat colored rects — a presentation-fidelity nuance vs the
  skinned progress bar.

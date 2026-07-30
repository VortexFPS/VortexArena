# Warpzones — parity spec

**Base refs:** `lib/warpzone/{anglestransform,common,server,client,util_server,mathlib}.qc/.qh` (+ `trigger_warpzone` / `trigger_warpzone_position` / `trigger_warpzone_reconnect` / `func_camera` spawnfuncs in server.qc)
**Port refs:** `src/VortexArena.Common/Gameplay/MapObjects/Warpzone.cs` · `src/VortexArena.Common/Gameplay/Warpzone/WarpzoneRadiusQuery.cs` · `src/VortexArena.Engine/Collision/TraceServiceWarpzoneExt.cs` · `game/loaders/HeroMaterials.cs` (portal surface)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
A warpzone is a seamless portal: two planar `trigger_warpzone` brushes are glued so that a player/projectile crossing the IN surface emerges from the linked OUT surface with origin, velocity, angles and view rotated by the relative plane transform (a 180° "turn around" composed with the rotation between the two planes), preserving momentum so strafe-jumps carry through. The system is a shared client/server library (`lib/warpzone`) plus integration hooks across movement, weapons (hitscan + splash trace through), bots and rendering. A `func_camera` (`dpcamera` surface) is the degenerate fixed-view variant; `trigger_warpzone_reconnect` re-links zones at runtime (e.g. moving warpzones). The player only sees a working warpzone if the **client** half renders the far side onto the portal surface and rotates the view across the seam.

## Base algorithm (authoritative)

### Rotation algebra — AnglesTransform (`anglestransform.qc`)  _(shared)_
A "transform" is a pure rotation stored as a Quake Euler `vector` in fixedmakevectors/fixedvectoangles space.
- `AnglesTransform_Apply(t,v)` = `forward*v.x + right*(-v.y) + up*v.z` (basis from `FIXED_MAKE_VECTORS(t)`; the `-v.y` is the v_right handedness correction).
- `AnglesTransform_Multiply(t1,t2)` = compose (t1 outer): rotate t2's forward+up by t1, re-derive angles with `fixedvectoangles2`.
- `AnglesTransform_Invert` = transpose of the orthonormal basis.
- `AnglesTransform_TurnDirectionFR(t)` = `(-t.x, 180+t.y, -t.z)` (180° about up; in→out direction flip). FU variant uses `180-t.z`.
- `AnglesTransform_RightDivide(to,from)` = `to · from⁻¹`; `LeftDivide(from,to)` = `from⁻¹ · to`.
- `AnglesTransform_Normalize(t, minimize_roll)` canonicalises to (−180,180] then flips to the equivalent triple with small pitch (or roll). `CancelRoll` folds roll into yaw within ±30° of the ±90° pole.
- `ApplyToAngles` vs `ApplyToVAngles` differ by a pitch-sign flip (`POSITIVE_PITCH_IS_DOWN`, default 1) — angles and v_angles use opposite pitch conventions.
- `Multiply_GetPostShift` / `PrePostShift_GetPostShift`: the translation half of an affine compose (`st0 + Apply(t0,st1)` / `st - Apply(t,sf)`).

### Transform core (`common.qc WarpZone_SetUp / _Transform*`)  _(shared)_
- `WarpZone_SetUp(e, my_org, my_ang, other_org, other_ang)`: `warpzone_transform = RightDivide(other_ang, TurnDirectionFR(my_ang))`; `warpzone_shift = PrePostShift_GetPostShift(my_org, transform, other_org)`; caches `warpzone_forward`/`warpzone_targetforward` from the basis; registers `setcamera_transform(WarpZone_camera_transform)` for the renderer.
- `WarpZone_TransformOrigin(wz,v)` = `shift + Apply(transform, v)`; `_TransformVelocity` = `Apply(transform,v)`; `_TransformAngles` = `ApplyToAngles`; `_TransformVAngles` = `ApplyToVAngles` then `Normalize`. Un-transform variants invert. `WarpZone_PlaneDist(wz,v)` = `(v-warpzone_origin)·warpzone_forward`.

### Spawn + linking (`server.qc`)  _(authority)_
- `spawnfunc(trigger_warpzone)`: `scale` defaults from `modelscale` then 1; `WarpZoneLib_ExactTrigger_Init` sets the brush solid=SOLID_TRIGGER, movetype=NONE, derives `warpzone_isboxy` (box trigger, no exacttrigger match) vs model-bounded; `setSendEntity(WarpZone_Send)`; `EF_NODEPTHTEST`; push onto `g_warpzones` + the `warpzone_first` list.
- `spawnfunc(trigger_warpzone_position)` / `misc_warpzone_position`: explicit-orientation helper, pushed onto `warpzone_position_first`.
- `WarpZone_StartFrame` first-frame init (deferred to after all entities spawn): `FindOriginTarget` (resolve `killtarget` → aiment target_position), `WarpZonePosition_InitStep_FindTarget` (a position attaches to its target zone as `.aiment`), `WarpZone_InitStep_UpdateTransform` (derive the IN plane from brush geometry), `WarpZones_Reconnect`, `WarpZone_PostInitialize_Callback`.
- `WarpZone_InitStep_UpdateTransform`: area-weighted average of every non-trigger triangle normal+centroid via `getsurface*` builtins → IN plane origin/angles; if an aiment is present its origin/angles seed the result and the plane only corrects orientation (`vectoangles2(norm,up)`); errors if the brush is non-planar and there's no position helper.
- `WarpZone_InitStep_FindTarget`: two-pass `.target`/`.targetname` pairing (`this.enemy = e2; e2.enemy = this`). `autocvar_sv_warpzone_allow_selftarget` (default **0**) — a zone never self-links unless set.
- `WarpZone_InitStep_FinalizeTransform`: `WarpZone_SetUp` both directions, `settouch(WarpZone_Touch)`, `SendFlags=0xFFFFFF`; if `spawnflags & 1` runs a per-frame `WarpZone_Think` that re-derives the transform when the zone or its partner moved (moving warpzones).
- `WarpZone_Send` / `WarpZone_Camera_Send`: CSQC entity serialisation (modelindex, mins/maxs, scale, the 4 transform vectors, fade start/end; sf bit1=isboxy, bit2=fade, bit4=origin).

### Teleport (`server.qc WarpZone_Touch / WarpZone_Teleport / WarpZone_TeleportPlayer`)  _(authority + shared)_
- `WarpZone_Touch(this,toucher)`: ignore other warpzones; skip if `time <= toucher.warpzone_teleport_finishtime` (already teleported this frame); skip MOVETYPE_NONE/FOLLOW/tag_entity; **skip unless `WarpZone_PlaneDist(this, origin+view_ofs) < 0`** (must have crossed past the plane). `EXACTTRIGGER_TOUCH`. Computes a frame-back `f` (`-1` for clients, `-d/(bound …)` for non-clients) and calls `WarpZone_Teleport`. On success fires `SUB_UseTargets_SkipTargets` on both this and `this.enemy`.
- `WarpZone_Teleport(wz, player, f0, f1)`: transform origin (`o0 = origin+view_ofs`), velocity, angles/v_angles through the zone; for `f≠0` re-runs the last move behind the warpzone with two traceboxes to avoid double-touch; `WarpZoneLib_MoveOutOfSolid` if it lands stuck (aborts if it can't); `WarpZone_RefSys_Add` (chained-transform reference system); `WarpZone_TeleportPlayer`; stamps `warpzone_teleport_time`/`_finishtime` (+ a back-teleport guard of `PHYS_INPUT_FRAMETIME - dt`).
- `WarpZone_TeleportPlayer`: `setorigin` (aborts move), set angles, `oldorigin` for DP unsticking, `fixangle=true` (unless `WARPZONE_USE_FIXANGLE` undefined → instead send a `warpzone_teleported` entity to the client to rotate the view smoothly); `BITXOR EF_TELEPORT_BIT` (cancels client interpolation across the seam); clears `FL_ONGROUND` for players; bots reset aim; `WarpZone_PostTeleportPlayer_Callback`.
- `WarpZone_StartFrame` per-frame: for observers / SOLID_NOT clients, `WarpZone_Find` + `WarpZone_Teleport(e,…,-1,0)` (NOT firing targets) so spectators pass through; same for `teleportable` simple teleporters. Also `WarpZone_StoreProjectileData` on all projectiles + clients each frame.
- `WarpZone_PlayerPhysics_FixVAngle`: server-side v_angle re-rotate within one ping of the teleport (the `v_angle_z += 720` adjusted-marker).

### Combat traversal (`common.qc traces + FindRadius`)  _(shared)_
- `WarpZone_TraceBox/_TraceLine/_ThroughZone`: sweep org→end as a sequence of plain traces; when a segment hits a (made-solid) `trigger_warpzone` transform the remaining segment through it and continue, accumulating `WarpZone_trace_transform`, up to a **16-zone guard** (`i = 16`). `WarpZone_MakeAllSolid`/`MakeAllOther` toggle zone solidity for the trace. `WarpZone_TraceToss` is the ballistic variant.
- `WarpZone_FindRadius(org,rad,needlineofsight)`: warpzone-aware radius query — `FOREACH_ENTITY_RADIUS`, blacklist via `WarpZoneLib_BadEntity` (weapon models, waypoints, info_/target_/buff_model/pure), recurse through each in-radius zone with `rad - vlen(...)` clamped to `[0, rad-8]`, tagging each victim with the transform back to the blast frame (`warpzone_transform`/`_shift`, `WarpZone_findradius_findorigin`). Nearest path wins.
- `WarpZone_RefSys_*`: per-entity chained reference system for cumulative transforms (used by `WarpZone_Teleport` and the trail/find recursion).
- `WarpZone_TrailParticles[_WithMultiplier]`: spawn a particle trail that bends through portals (CSQC).

### Client render + view (`client.qc`)  _(presentation)_
- `NET_HANDLE(ENT_CLIENT_WARPZONE / _CAMERA)`: receive the zone, `WarpZone_SetUp`/`Camera_SetUp`, `setpredraw(WarpZone_Fade_PreDraw)`; force `r_water 1` + full resolution so the camera-rendered far side is visible.
- `WarpZone_Fade_PreDraw`: PVS-cull (`checkpvs`) → alpha 0; else fade by `warpzone_fadestart/_fadeend` distance.
- `NET_HANDLE(ENT_CLIENT_WARPZONE_TELEPORTED)`: on a server crossing, rotate `VF_CL_VIEWANGLES` through the zone + `CL_RotateMoves` (DP builtin #638) so input/view rotate seamlessly.
- `WarpZone_FixView` (per-frame view hook): if the camera origin is inside a zone, transform org+vangles through it (`WarpZone_View_Inside` → hide exterior player model `r_drawexteriormodel 0`); roll-kill via `cl_rollkillspeed` (default **10**); `WarpZone_FixNearClip` pushes the near-clip plane out of the portal so geometry isn't clipped at the seam; `WarpZone_FixPMove` rotates `pmove_org`/`input_angles` for prediction.

## Port mapping

| Base feature | Port symbol | Layer | Status |
|---|---|---|---|
| AnglesTransform algebra | folded into `WarpzoneTransform` explicit fwd/right/up basis (`Warpzone.cs:21`); not a separate AnglesTransform module | shared | logic faithful (different encoding, intended) |
| `WarpZone_SetUp` / `_Transform*` | `WarpzoneTransform` ctor + `Rotate`/`TransformOrigin`/`TransformVelocity`/`TransformAngles` (`Warpzone.cs:37-65`) | shared | faithful |
| transform chain accumulator | `WarpzoneTransformChain` (`WarpzoneRadiusQuery.cs:29`) | shared | faithful |
| `spawnfunc(trigger_warpzone)` + `_position` | `WarpzoneSpawns` → `WarpzoneManager.OnMapEntity`/`AddMapZone`/`AddMapPosition` (`Warpzone.cs:376-397`); registered `MapObjectsRegistry.cs:108-109`; sink wired `GameWorld.cs:393` | authority | live |
| `WarpZone_InitStep_UpdateTransform` (brush plane) | `DerivePlaneFromBrush` (`Warpzone.cs:229`) via `ISurfaceService.GetSurface*` | authority | faithful/live |
| killtarget / position aiment resolve | `ResolveAiment` (`Warpzone.cs:420`) | authority | live |
| `WarpZone_InitStep_FindTarget` linking + selftarget | `Link`/`LinkOneWay` (`Warpzone.cs:139`), `sv_warpzone_allow_selftarget` read (cvar registered `Cvars.cs:105`) | authority | live |
| deferred first-frame init | `InitMapZones` (`Warpzone.cs:406`), called `GameWorld.cs:552` | authority | live |
| `WarpZone_Touch` + `WarpZone_Teleport` (player) | `Warpzone.Teleport` (`Warpzone.cs:186`); trigger `.Touch` set in `SpawnFromBrush`/`SpawnTriggerFor`; fired by `TriggerTouch.Run` (Engine) on the per-tick `TouchAreaGrid` pass | authority | live (server) — logic **partial**: gated on velocity-sign (`Dot(velocity,InForward)>0`) not Base's plane-side `WarpZone_PlaneDist(origin+view_ofs) >= 0` (server.qc:193); no `warpzone_teleport_finishtime` guard; `WarpZone_Projectile_Touch` projectile double-touch/impact filter (server.qc:344) unported. |
| `SUB_UseTargets` on crossing | `MapMover.UseTargets` (`Warpzone.cs:203`) | authority | live |
| combat trace-through (hitscan) | `WarpzoneTrace.TraceLineWarpzone`/`TraceBoxWarpzone` (`WarpzoneRadiusQuery.cs:148`), 16-zone guard; called `WeaponFiring.cs:141,233,358` | shared | live |
| `WarpZone_FindRadius` (splash) | `WarpzoneRadiusQuery.FindRadiusWarpzone` (`:341`); called `WeaponSplash.cs:76`, LOS via `TraceLineWarpzone` `WeaponSplash.cs:161` | shared | live — values **partial**: radius reduced by distance to the zone PLANE not Base's `vlen(org_new-org0_new)`; per-victim distance uses the entity CENTER not `WarpZoneLib_NearestPointOnBox`. Outcome-equivalent for the common single-portal small-victim blast; edge cases diverge. |
| `WarpZoneLib_BadEntity` blacklist | `IsBadEntity` (`WarpzoneRadiusQuery.cs:430`) | shared | faithful (minus `is_pure()`) |
| host bridge (`g_warpzones` publish) | `TraceService.SetWarpzoneManager` → `TraceServiceWarpzoneBridge.Publish` (`GameWorld.cs:558`) | authority | live |
| Porto-weapon portals as warpzones | `PlacePortoPortal`/`LinkPair` (`Warpzone.cs:335-362`); called `GameWorld.cs:389` | authority | live |
| **client portal render** (SubViewport/camera view of far side) | **NOT IMPLEMENTED** — `HeroMaterials.BuildPortal` is a static dark `StandardMaterial3D` mirror placeholder; `NetGame.cs` has zero warpzone code | presentation | missing |
| **`WarpZone_FixView` / `_FixNearClip` / `_View_Inside`/`_FixPMove`** | **NOT IMPLEMENTED** | presentation | missing |
| **`ENT_CLIENT_WARPZONE_TELEPORTED` view/input rotate** (smooth crossing) | **NOT IMPLEMENTED** — no client view rotate; `EF_TELEPORT_BIT` exists but isn't toggled by the warpzone teleport | presentation | missing |
| **client prediction of warpzone crossing** | **NOT IMPLEMENTED** — `TriggerTouch.PredictTeleportsAmbient` predicts `trigger_teleport` only (`TriggerTouch.cs:139`) | presentation/shared | missing |
| **`func_camera` / `func_warpzone_camera`** (fixed-view dpcamera) | **NOT IMPLEMENTED** — no spawnfunc | authority+presentation | missing |
| **`trigger_warpzone_reconnect` / `target_warpzone_reconnect`** (runtime re-link) | **NOT IMPLEMENTED** | authority | missing |
| **`WarpZone_Think` (moving warpzones, spawnflag 1)** | **NOT IMPLEMENTED** — `Link` runs once, never re-derives | authority | missing |
| **observer / SOLID_NOT per-frame warp** (`WarpZone_StartFrame`) | **NOT IMPLEMENTED** — spectators don't pass through warpzones | authority | missing |
| **`WarpZone_TraceToss`** (ballistic trace through) | **NOT IMPLEMENTED** | shared | missing |
| **`WarpZone_TrailParticles`** (trail bends through portal) | **NOT IMPLEMENTED** | presentation | missing |
| `EF_TELEPORT_BIT` / fixangle teleported-entity send | partial — server sets `OldOrigin = newOrigin` to cancel interp; does NOT send a teleported entity nor toggle EF_TELEPORT | authority/presentation | partial |
| `WarpZone_PlayerPhysics_FixVAngle` (server v_angle re-rotate) | **NOT IMPLEMENTED** | authority | missing |

## Parity assessment

**The server transform + combat traversal are correct and live; the entire client experience is missing.** This is the single dominant gap: at runtime a warpzone reads as an opaque/wavy dark wall (no see-through), and crossing it snaps/rubber-bands the camera because (a) there is no portal render (no Godot SubViewport+Camera3D drawing the linked OUT plane onto the surface — `HeroMaterials.BuildPortal` is a static dark mirror), (b) the client never rotates the view across the seam (`WarpZone_FixView` / `ENT_CLIENT_WARPZONE_TELEPORTED` unported, `NetGame.cs` has no warpzone code), and (c) the crossing isn't client-predicted (`TriggerTouch.PredictTeleportsAmbient` handles only `trigger_teleport`), so the server-authoritative teleport arrives as a correction snap.

**Gaps (player-observable):**
- Portal surface is opaque — you cannot see through a warpzone (`HeroMaterials.cs:168` placeholder; no SubViewport).
- Crossing a warpzone snaps/rubber-bands the camera (no view rotate + no client prediction).
- Near-clip clipping at the seam (no `WarpZone_FixNearClip`), no roll-kill on a roll-changing crossing.
- Spectators/observers cannot pass through warpzones (per-frame observer warp unported).
- Moving warpzones and runtime `trigger_warpzone_reconnect` don't update (link computed once at init).
- `func_camera` security-cam surfaces render as dark mirrors, not a fixed camera view.
- Grenade/toss trajectory prediction and particle trails don't bend through portals (`WarpZone_TraceToss` / `WarpZone_TrailParticles` unported) — minor.

**Liveness — what IS live:** map `trigger_warpzone` brushes spawn → orient (brush geometry) → link → the per-tick `TouchAreaGrid` pass fires the trigger's `.Touch` → `Teleport` rotates origin/velocity/angles/avelocity through the seam (server-authoritative, momentum-preserved). Hitscan weapon traces (`WeaponFiring` ×3) and splash radius/LOS (`WeaponSplash`) cross zones. Porto-weapon portals reuse the same manager. All verified by `WarpzoneSpawnTests`, `WarpzoneTests`, `WarpzoneTraceTests`.

**Intended divergences:**
- The port encodes the transform with an explicit forward/right/up basis (`WarpzoneTransform`) instead of QC's `AnglesTransform` Euler-vector algebra. Same math, headless-testable, no `AnglesTransform_*` / `mathlib.qc` shim ported. Logic-equivalent for the transform; `AnglesTransform_Normalize`/`CancelRoll` view-angle canonicalisation is NOT reproduced (only matters once the client view rotate exists, so currently moot).
- `FindCrossedZone` / `Recurse` detect crossings analytically (plane + trigger AABB) rather than QC's `WarpZone_MakeAllSolid` + tracebox-hits-zone. Same outcome; deterministic.
- The radius recursion uses an explicit front-side gate instead of QC's LOS-trace + radius-reduction to discard the wrong side (noted in `WarpzoneRadiusQuery.cs:407`) — same result without the extra trace.

## Verification
- `tests/VortexArena.Tests/WarpzoneTests.cs`: transform maps plane centers, preserves speed, moving-into-IN emerges-out-of-OUT, Teleport warps + preserves momentum, skips when moving out of the zone.
- `tests/VortexArena.Tests/WarpzoneSpawnTests.cs`: map brushes spawn/orient/link from brush geometry, position entity overrides orientation, two-way link when only one carries a target.
- `tests/VortexArena.Tests/WarpzoneTraceTests.cs`: chain identity/append, trace crosses one portal + accumulates transform, no-portal/no-manager plain trace, pathological self-recrossing stops at the 16-guard, FindRadius reaches a victim only through a portal, skips blacklisted classnames.
- Liveness traced through code (callers named above), not runtime-observed.
- Client-side absence verified by grep: `NetGame.cs` has zero warpzone/portal/SubViewport/FixView references; no `func_camera`/`warpzone_reconnect`/`WarpZone_Think` symbols in `src/` or `game/`.

## Open questions
- Does the static dark portal placeholder cause z-fighting / depth issues at the seam in a live map (e.g. `warpzone` test maps)? Needs a runtime check.
- Whether the server `OldOrigin = newOrigin` interp-cancel is enough to avoid a one-frame stretch on the *remote* (other-player) model crossing a zone, absent EF_TELEPORT toggling — needs observation.
- Exact behaviour of bot navigation across warpzones (Base resets bot aim on teleport) — bot path layer not audited here.

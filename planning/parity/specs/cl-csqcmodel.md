# cl-csqcmodel (CSQC player-model hooks) — parity spec

**Base refs:** `client/csqcmodel_hooks.qc` · `client/player_skeleton.qc` · `common/animdecide.qc/.qh` · `common/csqcmodel_settings.qh`
**Port refs:** `src/VortexArena.Engine/Simulation/Csqc*.cs` · `src/VortexArena.Engine/Simulation/{PlayerSkeleton,LocomotionBlend,Skeleton}.cs` · `game/client/{ClientWorld,ModelTint,CsqcModelEffects,PlayerModel,ModelAnimator,ViewEntityRenderer}.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
This is the CSQC (client-side QuakeC) predraw pipeline that turns every networked model entity — players,
gibs, items, projectiles attached by tag — into what you actually see each rendered frame. Base runs
`CSQCModel_Hook_PreDraw(this, isplayer)` once per entity per frame, which (in order) applies the
**force-model/force-color/glowmod/death-fade appearance** pass (player models only), the **LOD model swap**,
the per-model **skeletal animation** (the upper/lower-body split + view-pitch aim bones for the local/own
player via `player_skeleton.qc`, and the `animdecide` frame inference for remote players), the **auto
tag-index** weapon/attachment resolution, and the **EF_*/MF_* effects** pass (dynamic lights, trail effects,
particle emitters, render-flags, jetpack loop sound). It also owns the CSQCMODEL networking contract
(`csqcmodel_settings.qh`): which entity properties the server streams to the client (effects, modelflags,
skin, traileffect, colormap, anim_state, anim_upper/lower_action+time, v_angle, scale, glowmod, colormod, …).

The port reproduces a substantial subset as Godot-free, unit-tested pure helpers (`CsqcModelAppearance`,
`CsqcModelLod`, `CsqcFallbackFrame`, `CsqcModelEffectFlags`, `PlayerSkeleton`, `LocomotionBlend`) wired through
`ClientWorld.DriveCsqcModelHooks` (called every frame from `_Process`). The big divergence is the **animation
decision system**: the port does NOT port `animdecide` — neither its networked `anim_state`/`anim_*_action`
inputs nor its 8-directional run/strafe + duck-variant + upper-body-action state machine. It substitutes a
coarse 6-state clip heuristic.

## Base algorithm (authoritative)

### Predraw dispatch + order  (`csqcmodel_hooks.qc:674 CSQCModel_Hook_PreDraw`)
- **Trigger:** the engine CSQC predraw, once per entity per frame; guarded by `csqcmodel_predraw_run==framecount`.
- **Algorithm:** if `!modelindex || model=="null"` → `drawmask=0`, stop jetpack loop, return. Else
  `drawmask=MASK_NORMAL`. For a player MODEL (`isplayermodel & ISPLAYER_MODEL`): `ModelAppearance_Apply` →
  `LOD_Apply(true)` → skeletal anim (local/own player runs `animdecide_*` + interpolate + `skeleton_from_frames`;
  remote runs `FallbackFrame_Apply` + optional blend; non-player MODEL clones from server frames). Else just
  `LOD_Apply(false)`. Then `AutoTagIndex_Apply` → `Effects_Apply`. **Order matters: appearance MUST precede LOD.**

### Force model / skin  (`csqcmodel_hooks.qc:141 CSQCPlayer_ModelAppearance_Apply`)
- **Side:** presentation (cl). Decides which model+skin a player renders as.
- **Algorithm:** resolve a guaranteed-good fallback (`_cl_playermodel` default); for the local player trust
  the server's networked model unless demo; else `cl_forcemyplayermodel` (friend only) > `cl_forceplayermodels`
  (all, if server allows via `sv_defaultcharacter 0`) > the entity's own good model > guaranteed-good fallback.
- **Constants:** `_cl_playermodel` = `models/player/erebus.iqm`; `_cl_playerskin` 0; `cl_forceplayermodels` 0;
  `cl_forcemyplayermodel` ""; `cl_forcemyplayerskin` 0.

### Force colors / unique colors  (`csqcmodel_hooks.qc:239-327`)
- **Side:** presentation. Reassigns `this.colormap`. `forceplayercolors_enabled` gated by gametype:
  1v1/Duel → `cl_forceplayercolors ∈ {1,2,3,5}`; 2-team → `{2,4,5}`; FFA → `{1,2}`. Teamplay forces friend
  color (`cl_forcemyplayercolors`) and enemy color (`_cl_color`) with own-team-collision suppression. FFA forces
  my color (local), unique per-enemy combos (`cl_forceuniqueplayercolors`: `c1=num%15, q=floor(num/15),
  c2=(c1+1+q)%15, colormap=1024+(c1<<4)+c2`), or `player_localnum+1`.
- **Constants:** `cl_forceplayercolors` 0, `cl_forcemyplayercolors` 0, `cl_forceuniqueplayercolors` 0.

### Glowmod + death fade + respawn ghost  (`csqcmodel_hooks.qc:331-360`)
- Respawn ghost (`CSQCMODEL_EF_RESPAWNGHOST == EF_SELECTABLE`) with `cl_respawn_ghosts_keepcolors`==0 →
  `glowmod='0 0 0'`, `colormap=0`, return early.
- Else `glowmod = colormapPaletteColor(pants_nibble, true)` when colormap>0 else `'1 1 1'`.
- Death fade: if `cl_deathglow>0` and dead: `min_factor=bound(0,cl_deathglow_min,1)` (halved if colored);
  `glow_fade=bound(0, 1-(time-death_time)/cl_deathglow, 1)`; `glowmod *= min_factor + glow_fade*(1-min_factor)`;
  clamp `'0 0 0'`→`x=0.000001`.
- HDR clamp: if `r_hdr_glowintensity>1`, `glowmod /= r_hdr_glowintensity`.
- **Constants:** `cl_deathglow` 2s · `cl_deathglow_min` 0.5 · `cl_respawn_ghosts_keepcolors` 1 · `r_hdr_glowintensity` 1.

### LOD  (`csqcmodel_hooks.qc:27-92 CSQCModel_LOD_Apply`)
- On first sight, probes `<model>_lod1.<ext>`/`_lod2.<ext>` with `fexists`+`precache_model` and records up to 3
  modelindices. Each frame: if `detailreduction<=0`, static pick (`<=-2`→lod2, `<=-1`→lod1, else lod0); else
  `f = (distance*current_viewzoom + 100) * detailreduction; f /= bound(0.01,view_quality,1)`; `f>dist2`→lod2,
  `f>dist1`→lod1, else lod0. Non-players use `NearestPointOnBox(ent,view_origin)`; players use origin.
- **Constants:** `cl_playerdetailreduction` 4 · `cl_modeldetailreduction` 1 · `cl_loddistance1` 1024 · `cl_loddistance2` 3072.

### Skeletal animation: upper/lower split + aim bones  (`player_skeleton.qc` + `csqcmodel_hooks.qc:715-772`)
- For the local/own player, Base runs the FULL animdecide pipeline each frame: `skeleton_loadinfo` (resolve
  `bone_upperbody`/`bone_weapon`/aim bones + `fixbone` from `get_model_parameters`), `animdecide_setimplicitstate`
  (infer movement state from velocity), `animdecide_setframes` (resolve upper+lower 4-frame blend), then the
  2→4 interpolate, then `skeleton_from_frames`: bones split into UPPER (descendants of `bone_upperbody`) playing
  frames 1+3 and LOWER playing frames 2+4 via lerpfrac juggling; `fixbone` re-anchors the upper body; aim bones
  bend by `bound(-90, v_angle.x, 90)*weight`.
- Remote players run `FallbackFrame_Apply` + (if `bone_upperbody>=0`) the same `skeleton_from_frames` split,
  using the networked frame fields, NOT a locally-decided state.

### animdecide state machine  (`common/animdecide.qc`)
- `animdecide_load_if_needed`: resolves all ~30 named anim framegroups (`die1/2, draw, duck, duckwalk,
  duckjump, duckidle, idle, jump, pain1/2, shoot, taunt, run, runbackwards, strafeleft/right, forwardright/left,
  backright/left, melee, duckwalk{backwards,strafeleft,straferight,forwardright,forwardleft,backright,backleft}`)
  with per-anim default framerates (die 0.5fps→2s, shoot 5, jump 10, taunt 0.33, draw 3, others 1/2).
- `animdecide_setimplicitstate`: 8-direction detection from `velocity·forward / velocity·right` with the engine's
  0.5 cot threshold; sets FORWARD/BACKWARDS/LEFT/RIGHT/INAIR; **CSQC infers jump here (the explicit JUMP action
  is never networked to CSQC)**.
- `animdecide_getupperanim` / `getloweranim`: priority cascade DEAD>CROUCH>ACTIVE>IDLE selecting upper-body
  ACTION (draw/pain/shoot/taunt/melee) over the idle, and lower-body locomotion (jump/run/strafe/duck-variants).
- **Networked inputs** (`csqcmodel_settings.qh`): `anim_state` (BIT(7)), `anim_time`, `anim_lower_action`+time
  (BIT(8), non-local), `anim_upper_action`+time (BIT(9)). These are how the SERVER tells the client what action
  is playing; the directional/locomotion state is inferred client-side from velocity.

### Fallback frame  (`csqcmodel_hooks.qc:414-443`)
- Remaps a frame the model lacks to one it has: melee(23)→shoot(11); all duckwalk variants(24-30)→duckwalk(4);
  static model unfixable. Probe via `frameduration(modelindex, f)`.

### Auto tag-index  (`csqcmodel_hooks.qc:449-520 CSQCModel_AutoTagIndex_Apply`)
- For an entity attached via networked `tag_networkentity`: resolves the parent entity, recursively predraws it,
  and computes `tag_index` — for `models/weapons` on a player MODEL it uses the skeleton's `bone_weapon`; for a
  weapon-on-weapon it uses `gettagindex(weapon/tag_weapon)`; falls back to `shot`/`tag_shot`. Sets `drawmask=0`
  when the tag can't resolve.

### Effects  (`csqcmodel_hooks.qc:522-661 CSQCModel_Effects_Apply`)
- Each frame turns the networked `effects` (EF_*) + `modelflags` (MF_*) + `traileffect` into: dynamic lights
  (BRIGHTLIGHT 400/'3 3 3', DIMLIGHT 200/'1.5..', BLUE/RED 200, FLAME orange@+'0 0 10' 200 + EF_FLAME box
  particles, SHOCK 50/'3.1 4.4 10' + EF_SHOCK/ARC_LIGHTNING particles, STARDUST particles), render-flags
  (ADDITIVE/FULLBRIGHT/NODEPTHTEST/NOSHADOW/NODRAW→hide), MF→trail (ROCKET→TR_ROCKET, GRENADE→TR_GRENADE,
  GIB→TR_BLOOD, TRACER→TR_WIZSPIKE, ZOMGIB→TR_SLIGHTBLOOD, TRACER2→TR_KNIGHTSPIKE, TRACER3→TR_VORESPIKE,
  BRIGHTFIELD→TR_NEXUIZPLASMA), MF_ROTATE→spin via makevectors. RESPAWNGHOST→RF_ADDITIVE. **Jetpack loop**:
  MF_ROCKET on → loop `SND_JETPACK_FLY` on CH_TRIGGER_SINGLE at `cl_jetpack_attenuation` (2); off → stop.
- **State/networking** (`csqcmodel_settings.qh`): the Effects/modelflags/traileffect are saved/restored across
  the PreUpdate/PostUpdate so the server's transient `effects=0` reset each frame is honored; the ghost bit is
  `EF_SELECTABLE`. `CSQCPLAYER_FORCE_UPDATES 4` (forced resend/s).

## Port mapping
| Base feature | Port symbol | Live caller |
|---|---|---|
| Predraw dispatch+order | `ClientWorld.DriveCsqcModelHooks` | `_Process`→`cw.csqc` (line 942) — LIVE |
| Force model/skin | NOT IMPLEMENTED as a per-frame override (server-trusted model only; `cl_force*model` not honored) | — |
| Force colors/unique | `CsqcModelAppearance.ResolveForcedColormap/ForcePlayerColorsEnabled/UniqueColormap` + `ClientWorld.ResolveForcedColormap` | live, but gated on `AppearanceProvider` |
| Glowmod+deathfade+ghost | `CsqcModelAppearance.{ColormapPaletteColor,DeathGlowFactor}` + `ModelTint.ApplyAppearance/ComputeAppearance` | LIVE per-frame |
| LOD | `CsqcModelLod.SelectLodIndex/LodModelName/NearestPointOnBox` + `ClientWorld.ApplyLod` | index computed, **swap is a no-op** |
| Skeletal split + aim bones | `PlayerSkeleton.FromFrames` + `PlayerSkeletonConfig` + `SkeletonManager` + `PlayerModel.Pose` | LIVE for skeletal IQM players |
| animdecide state machine | `LocomotionBlend.SelectLegs/Split` (6-state heuristic) | LIVE but heavily simplified |
| Fallback frame | `CsqcFallbackFrame.Remap` + `ModelAnimator.FrameDuration` | LIVE only on MD3-animator player path |
| Auto tag-index (weapon) | `ViewEntityRenderer` (tag_weapon marker attach) | LIVE for held weapons; QC h_/v_ resolution simplified |
| Effects (EF_*/MF_*/trail/lights/jetpack) | `CsqcModelEffects.Apply` + `CsqcModelEffectFlags` | LIVE for EF_* (networked); MF_* = 0 for remotes |
| CSQCMODEL networking (anim_state, actions, colormod, glowmod, scale, traileffect, modelflags, alpha, v_angle) | NOT IMPLEMENTED — Entity carries only Frame/Skin/Effects/Team/ModelIndex (`cl-csqcmodel.networking.csqcmodel_contract`). colormod is networked for ALL entities in Base; scale + v_angle also unnetworked | — |

## Parity assessment

**Pure helper math (faithful).** `CsqcModelAppearance` (force-color cascade, unique-color combo,
colormapPaletteColor 0..15 incl. animated rainbow, DeathGlowFactor), `CsqcModelLod` (distance pick + name
derivation), `CsqcFallbackFrame` (remap table), `CsqcModelEffectFlags` (EF/MF bit values + trail map) are
careful 1:1 ports with constants pinned and unit tests. These dimensions are faithful.

**Animation system (the dominant gap).** Base's `animdecide` is NOT ported. The port:
- Does NOT network or use `anim_state`, `anim_upper_action`/`anim_lower_action`/`anim_time` — so upper-body
  ACTIONS (shoot/melee/pain/draw/taunt overlays on the torso) never play on the third-person model.
- Replaces the 8-direction + duck-variant locomotion (`forwardleft`, `backright`, `duckwalkstrafeleft`, …, ~24
  movement clips) with a 6-state coarse heuristic (`Idle/Walk/Run/Jump/Crouch/Dead`) keyed only on 2D speed
  thresholds (20/220 u/s), on-ground, ducked, dead — no directional strafe anims, no backward-run, no duck-walk
  directional variants.
- The torso always shows a static idle/aim clip frame (`LocomotionBlend.Split` torsoTime=0), so the upper body
  never animates beyond the aim-bone pitch bend.
- Per-anim framerates (die 0.5fps, shoot 5, jump 10, taunt 0.33) and the per-clip framegroup names are not used;
  clips are picked by a fuzzy name `Contains` match with a 20fps fallback.
Player visible result: remote players run/strafe with a single forward-run cycle regardless of direction, never
visibly shoot/flinch/reload/taunt with the torso, and ducking shows a generic crouch rather than the duck-walk
directional set.

**LOD swap (no-op — REAL regression on stock content).** `SelectLodIndex` is computed faithfully but the port
never swaps to a `_lodN` model (`ApplyLod` keeps lod0 always; the index is discarded). The draft claimed this
"matches the QC fexists-miss path" — that is WRONG for default content: `Base/data/.../models/player/` ships
`erebus_lod1/2.iqm`, `gak_lod1/2.iqm`, `gakmasked_lod1/2.iqm`, and `models/monsters/` ships
`golem_lod1.dpm`, `spider_lod1.dpm`, `nanomage_lod1.dpm`, `wyvern_lod1.dpm`, etc. With the default
`cl_playerdetailreduction 4`, Base downshifts distant players/monsters to lod1/lod2; the port always renders
full-detail lod0. So this is a genuine perf + visible-detail parity gap, not just an inert fexists guard.

**Force model/skin (missing).** `cl_forceplayermodels` / `cl_forcemyplayermodel` / `cl_forcemyplayerskin` do
nothing in the port — only the server-networked model is rendered. (Colors ARE forced; models are not.)

**Force colors (partial-live).** The cascade math is faithful but: (a) it only runs when an `AppearanceProvider`
is wired (returns own colormap otherwise); (b) the friend-only-force vs. all-other-team-colors collision check
is reduced to a local-team-only compare (documented in `CsqcModelAppearance` line 101-103) because the port
doesn't network the team list; (c) the port works in team-id units and reconstructs `1024+17*team`.

**Effects (mostly faithful, two structural gaps).** EF_* lights/particles/render-flags/jetpack are faithful
where the bit is networked. **MF_* model flags are NOT networked** (`Entity` has no `modelflags`/`traileffect`),
so MF-driven trails (rocket/grenade/gib/tracer) and the MF_ROCKET jetpack loop only fire when a LOCAL caller
supplies `modelFlags` — in `DriveCsqcModelHooks` it's hardcoded `modelFlags: 0`, so remote players/projectiles
in `_entityNodes` get NO MF trail and NO jetpack loop from this path (projectiles route trails via
`ProjectileRenderer` separately). Render-flags (additive/fullbright/nodepthtest) only apply to per-instance
`BaseMaterial3D` overrides, not to shared/cached shader materials (documented RC3/RC4 lesson) — so on resolved
player/weapon models additive/fullbright/depthhack are a parity gap. EF_BRIGHTFIELD on a player model is dropped
(trail return unused in the entity-node pass). MF_ROTATE spin is not applied (no caller supplies the bit).

**Death-fade `isdead` source diverges (intended).** Base derives dead from `IS_DEAD_FRAME(frame)` (frame 0/1);
the port uses networked `DeadState`/`Health<=0` because it doesn't network the 0/1 death frame and a literal port
would false-positive living idle players. Intended divergence (documented at `ClientWorld.IsDeadModel`).

**`death_time` not networked (intended).** Base networks `death_time` (BIT(5), ReadApproxPastTime); the port
captures a client-observed death instant the first frame it sees the entity dead. Slightly different fade origin
under packet loss; intended.

## Liveness
`DriveCsqcModelHooks` is invoked every frame from `ClientWorld._Process` (line 942) — the appearance/deathglow,
LOD-index, and EF_* effects passes are all genuinely live. `PlayerModel.Pose` (skeletal split + aim bones) is
live for skeletal-IQM players. `CsqcFallbackFrame.Remap` is live only on the MD3 `ModelAnimator` player path.
Force-color resolution is live but inert without an `AppearanceProvider`. Force-MODEL, LOD swap, MF_* trails,
MF_ROTATE, the auto-tag-index recursion, and the entire animdecide action/8-direction system are dead/missing.

## Verification
- Code-read of all Base files + every port helper and the `DriveCsqcModelHooks` live pass (this audit).
- Live-caller trace: `grep DriveCsqcModelHooks` → `_Process` line 942; helper callers grepped to `ClientWorld`/
  `ModelTint`/`ModelAnimator`/`PlayerModel`.
- Unit tests exist for the pure helpers (`CsqcModelHooksTests`, `CsqcModelAppearance*`, referenced in headers) —
  not re-run here.
- Constants diffed against `xonotic-client.cfg`/`xonotic-common.cfg`/`effects-*.cfg`.
- NOT verified in-game (no runtime capture): exact rendered animation fidelity, the force-color collision case,
  the deathglow fade curve on screen.

## Adversarial-verify corrections (2026-06-22)
- **fixbone IS ported** (draft said "unknown"/"not clearly reproduced"). `PlayerSkeleton.FromFrames` lines
  124-154 implement the build-as-upper → snapshot split-bone orientation → re-anchor-after-split sequence as a
  1:1 port of `player_skeleton.qc:131-179`. The aim-bone pitch math is likewise faithful. The skeleton row's
  presentation gap is purely the static torso (animdecide) + the v_angle source — its logic/values are faithful.
- **EF_BLUE / EF_RED / EF_SHOCK lights ARE present and faithful** (draft marked them "unknown"). See
  `CsqcModelEffects.cs:106-107,123-124` — `200/(0.15,0.15,1.5)`, `200/(1.5,0.15,0.15)`, `50/(3.1,4.4,10)` all
  match Base. The effects-light values dimension is faithful; the remaining values gaps are the box→point
  particle reduction and the MF=0/shared-material render-flags.
- **CSQCMODEL networking contract added as a feature row** (`cl-csqcmodel.networking.csqcmodel_contract`). The
  draft only mentioned it in the port-mapping table. The notable omission beyond the per-feature rows: **colormod
  is networked for EVERY entity in Base** (`csqcmodel_settings.qh:68-70`, scaled 16/0/255) and is not networked
  in the port (so server-driven per-model tint never reaches the client), and **scale** (BIT(12)) is likewise
  unnetworked.

## Open questions (resolved by this verify)
- ~~Is an `AppearanceProvider` wired on the live NetGame path?~~ YES — `NetGame.cs:960`
  `_render.AppearanceProvider = BuildAppearanceContext` (`NetGame.cs:3909`). Force-colors is live in a match.
- ~~Does any stock player model ship `_lod1`/`_lod2`?~~ YES — erebus/gak/gakmasked + multiple monsters do (see
  the LOD section above). The no-op swap is a real regression on default content.
- Is there appetite to port `animdecide` proper (network `anim_state`/actions + 8-direction inference) and the
  CSQCMODEL colormod/scale/v_angle properties, or is the coarse 6-state blend the intended permanent design? If
  intended, the animdecide + networking rows should flip to `intended_divergence` (currently `false`).

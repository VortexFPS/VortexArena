# Lighting implementation — what landed, and the F9 settings audit

**Date:** 2026-08-05
**Companion to:** `planning/lighting-realtime-and-lightgrid-2026-08-05.md` (the research + options doc; the
F/N identifiers below are its).
**Status of claims:** every "wired" row was checked by reading the reader, and the runtime rows were checked
in a windowed run on the named map. Frame-cost numbers are `[verified — read off the in-game profiler in a
DEBUG build]` and are therefore indicative, not release numbers; `./vx perf-smoke` on the release export is
the authority.

---

## 1. What landed

| ID | Feature | State |
|---|---|---|
| **F1-B** | Per-pixel model lighting from the BSP light grid (DP `mod_q3bsp_lightgrid_texture`) | done |
| **F2** | Coronas (`r_coronas`) | done |
| **F3-A** | Dynamic-light shadows, budget-capped | done, off by default |
| **F4** | `.rtlights` realtime world lighting | done, off by default |
| **F5** | `r_fakeshadows` — cheap projected ground shadows | done, off by default |
| **F6** | Light-style animation of world lightmaps | **deferred** (as requested) |
| **F7** | Cubemap light filters (gobos) | done, approximated — see §4 |
| **F8** | Eye-trace light culling with hysteresis | **deferred** (as requested) |
| **F9** | Wire the effects presets + audit the menus | done — §2 and §3 |
| **N2** | Dynamic lights reach grid-lit models | done, on by default |
| **N3** | Volumetric fog + light shafts | done, off by default |
| **N6** | Light budget manager | done |
| **N7** | `.rtlights` import/export console commands | done |
| **N8** | Gameplay rim light | done, off by default |
| **N9** | SDFGI global illumination | done, off by default |

**The default look changed in exactly one way**: models are now lit by the map instead of by one hardcoded
sun. Everything else added is off unless a cvar turns it on, so the shipping frame profile is unchanged
except for F1-B's single 3-D texture fetch per model fragment.

---

## 2. F9 — the effects presets

Before this, `data/core.pk3dir/effects-*.cfg` carried the `r_shadow_*` / `r_coronas` block verbatim from
Base, and **every line of it was inert** — the cvars were unregistered, so the presets described DarkPlaces'
renderer rather than this one. The 2026-06-14 graphics audit called this out as finding 12.

Now: the Base-inherited lighting cvars that have a reader are honoured, and a Vortex block was appended to
each preset with the port-only ones. The escalation follows Xonotic's own logic — nothing that costs real
frame time turns on below `ultra`, which is exactly where Base first sets
`r_shadow_realtime_dlight_shadows 1`.

| cvar | low | med | normal | high | ultra | ultimate | omg |
|---|---|---|---|---|---|---|---|
| `r_model_lightgrid` | 1 | 1 | 1 | 1 | 1 | 1 | 0 |
| `r_model_dlight` | 1 | 1 | 1 | 1 | 1 | 1 | 0 |
| `r_shadow_dlight_max` | 8 | 12 | 16 | 24 | 0 | 0 | 4 |
| `r_shadow_dlight_shadow_budget` | 0 | 0 | 0 | 0 | 2 | 4 | 0 |
| `r_shadow_world_casts` | 0 | 0 | 0 | 0 | 1 | 1 | 0 |
| `r_fakeshadows` | 0 | 0 | 2 | 2 | 2 | 2 | 0 |
| `cl_rimlight` | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

`r_volumetricfog` and `r_gi` are **deliberately absent from every preset**. Both are opt-in only: SDFGI is far
above the frame budget, and fog that hides players is a gameplay change rather than a graphics one, so no
preset should be able to switch it on behind a player's back.

`cl_rimlight` is 0 in every preset for the same reason — it makes players easier to see, which is a balance
decision. It is exposed in the menu so a player can opt in, and a server that wants everyone matched can
force it.

---

## 3. F9 — the settings menus

**Effects tab** (`game/menu/dialogs/DialogSettingsEffects.cs`). The "Lights & Shadows" section already bound
the Base cvars faithfully. Four of those bindings are now live rather than inert:

| control | cvar | now |
|---|---|---|
| Realtime dynamic lights | `r_shadow_realtime_dlight` | **wired** — master gate in the light budget |
| ⤷ Shadows | `r_shadow_realtime_dlight_shadows` | **wired** — grants shadows by rank |
| Realtime world lights | `r_shadow_realtime_world` | **wired** — the `.rtlights` renderer |
| Corona brightness | `r_coronas` | **wired** — the flare renderer |
| ⤷ Fade coronas by visibility | `r_coronas_occlusionquery` | **wired** — trace-based, see §4 |

Five port-only controls were added below them, kept together and after the Base-parity ones so the tab still
reads as Xonotic's: *Light models from the map*, *Dynamic lights on models*, *Ground shadows*, *World casts
shadows*, *Team rim light*.

**Still inert — and now greyed rather than merely documented.** Six controls are bound to real cvars that
nothing in this renderer reads, so toggling them could never do anything. They are now permanently disabled
via `Dependent.Unsupported`, which greys the widget and appends the reason to its tooltip:

| control | cvar | why it cannot work here |
|---|---|---|
| Gloss | `r_shadow_gloss` | data-driven: applied wherever a `_gloss` companion exists, decided at material build |
| Use normal maps | `r_shadow_usenormalmap` | same, for `_norm` |
| Offset mapping | `r_glsl_offsetmapping` | no offset/parallax path exists at all |
| Relief mapping | `r_glsl_offsetmapping_reliefmapping` | ditto |
| Shadows (under Realtime world lights) | `r_shadow_realtime_world_shadows` | world lights draw shadow grants from the same ranked budget as dynamic lights |
| Soft shadows | `r_shadow_shadowmapping` | DP picks shadow maps over stencil volumes; Godot has no stencil path, and filtering is renderer-wide |

The cvars are deliberately NOT deleted — they are inherited from Xonotic and must keep parsing, so a player
carrying an Xonotic autoexec gets no "unknown command" spam and a preset that sets one still applies cleanly.
What was wrong was the menu implying they did something. The widget stays, shows the cvar's real current
value (Gloss and normal maps read as checked-but-greyed, which is honest), and explains itself.

Where a control had an ordinary `Dependent.Bind`, that bind was **removed** rather than left alongside: two
Dependents on one target fight, and which wins depends on which cvar changed last.

### Reflections vs warpzones, and the two restart commands

`r_water` in DarkPlaces names the reflection/refraction pass. This port renders **warpzone portals** through
`PortalRenderer`, and briefly gated that on `r_water` — so switching off "Reflections" blanked every warpzone,
which is the opposite of what anyone wants from that checkbox. They are separate cvars now:

* **`r_warpzone`** (new, default 1) gates warpzone portal views. Every portal this renderer builds had to match
  a live warpzone to exist at all, so this is exactly what it draws. `0` freezes the portal viewports rather
  than blacking them — the surface keeps its last image.
* **`r_water`** is back to greyed. `dpreflect` / `dprefract` / `dpwater` are parsed by `Q3ShaderParser` but no
  renderer consumes them, so a mirror or water surface draws its placeholder. `r_water_resolutionmultiplier`
  is greyed with it: it sizes a pass that does not exist. Warpzone view resolution is `cl_portal_resolution`.

**`vid_restart` vs `r_restart`.** In DarkPlaces `vid_restart` recreates the GL context, which re-uploads every
texture — so `gl_picmip` and `gl_texturecompression` genuinely are applied by it there. Here textures are
cached Godot resources that re-applying the window settings never touches, so a literal port would silently do
less than the same command does in Base. **`vid_restart_resetrenderer`** (default 1) makes `vid_restart` also
do what `r_restart` does. It is a cvar because the reset is a *map reload*, far more disruptive than the
resolution change that usually triggers it; set it to 0 and `vid_restart` only touches the window.

### The Apply button

"Apply immediately" now reflects whether there is anything to apply, instead of being permanently live.
`AppliedState` records what is actually running (the appliers themselves record it, so a `vid_restart` typed
at the console counts, and the boot-time apply seeds it); `PendingApply` enables the button only while a cvar
that its action consumes has drifted from that.

| tab | deferred set | button |
|---|---|---|
| **Video** | `vid_width`, `vid_height`, `vid_fullscreen`, `vid_borderless`, `sys_priority_boost` | live only when one has drifted |
| **Audio** | `mastervolume`, `bgmvolume`, `snd_channel{0,1,2,7}volume` | live only when one has drifted |
| **Effects** | *(empty)* | permanently greyed, with the reason |

`vid_vsync`, `cl_maxfps` and `cl_engine_jitterfix` are deliberately absent from the Video set: they are
re-applied the moment they change (`ClientSettings.InstallLiveVideoCvars`), so listing them would light the
button up for a change that had already happened.

The Effects set is empty because **every Effects cvar with a reader is polled live** — a change is visible
the instant you make it. The one exception is `r_shadow_world_casts`, read once when the map geometry is
built; a `vid_restart` would not apply that either, so it says "takes effect on the next map load" in its own
tooltip rather than claiming the button.

The argument for bothering: an always-live Apply button teaches players that settings need applying, which on
these tabs is mostly false. It becomes a ritual — pressed after every change — and then carries no signal on
the rare occasion it genuinely matters. Greying it makes the button the answer to "did that take effect?".

**Video tab.** Untouched by this work; its open items (MSAA and anisotropy hardwired in `project.godot`,
shadow atlas size / filter quality / max distance unexposed) are unchanged and still listed in the
2026-06-14 audit's Table 3.

`docs/reference/CVARS.md` was regenerated, so the inventory now reflects which of these are registered.

---

## 4. Approximations, stated

Three places where this port does something visibly different from DarkPlaces. All three are documented at
the code as well.

* **F7 cubemap light filters.** DP multiplies a light's colour by a cubemap sampled along the light-to-fragment
  vector. Godot's `OmniLight3D` has no projector at all; only `SpotLight3D` does, and it takes a flat 2-D
  texture. So a light carrying a cubemap becomes a **spot** aimed by its own angles with the cubemap's forward
  face as the projector. A gobo authored to throw light six ways throws it one way. Lights with no cubemap are
  untouched and stay omni.
* **F2 corona occlusion.** DP fades a flare by the fraction of its pixels passing the depth test, via a GPU
  occlusion query. Godot exposes no per-object occlusion query to scripts, so this traces eye→light and
  smooths the result over a few frames. DP fades *partially* when a flare is half behind a pillar; a single
  ray is all-or-nothing per frame, and the smoothing is what turns that back into a fade rather than a blink.
* **F5 fake shadows.** DP projects the model's real silhouette. This projects a soft blob. That is a
  deliberate downgrade — the silhouette version needs a render pass per caster, and the entire point of the
  feature is to be the cheapest possible way to stop models looking like they hover.

---

## 5. Two findings worth keeping

**Xonotic disables the map-entity light import itself.** `r_shadow_realtime_world_importlightentitiesfrommap`
defaults to 1 in DarkPlaces, but `xonotic-client.cfg:304` sets it to **0**, with the reason in its own
comment: *"Whether build process uses keepLights is nontransparent and may change, so better make keepLights
not matter."* So on stock content that import never runs — even though the maps still carry the entities
(stormkeep has 119 `light` entities in its lump, and setting the cvar to 1 imports all 119). This is
reproduced rather than second-guessed; `rtlights_import` opts in explicitly.

**The light-grid direction divisor was wrong, by a little, for a long time.** `LightGridData.Sample` decoded
the baked light direction with `2π/255`. Both ioquake3 and DarkPlaces index a **256**-entry sine table with
the raw byte, so a full turn is 256 steps. The error was up to ~1.4° per sample — invisible on its own, and
fatal the moment a second implementation (the GPU packer) had to agree with it bit-for-bit.

---

## 6. Measured cost

`[verified — in-game profiler, DEBUG build, stormkeep and glowplant, RTX 3080 box]`. Debug numbers; treat the
*ratios* as meaningful and the absolutes as pessimistic.

| change | draws | note |
|---|---|---|
| baseline (stormkeep, bots 0) | 250 | |
| `r_shadow_world_casts 1` | 801 | every world cell becomes a shadow caster — this is why it is a separate opt-in |
| `r_fakeshadows 2` | +≤24 | one alpha quad per visible caster, capped |
| `r_volumetricfog 1` | 250 | no extra draws; cost is the froxel pass, not geometry |

The GPU light grid is **413–501 KB** on the maps checked (glowplant 56×45×42, stormkeep 54×61×39), i.e. a
rounding error against the ~474 MB of resident textures those maps already carry.

One perf bug was found and fixed during the work rather than shipped: giving each fake-shadow pool member its
own material duplicate cost a **14.6 ms first-frame hitch** (24 `Resource.Duplicate` calls), which the frame
profiler's watchdog caught as a `fakeshadows` sample. Opacity now rides an instance uniform on one shared
material.

---

## 7. What is still open

* **F6** — light-style animation of world *lightmaps* (deferred). Styled dynlights and styled world lights
  both animate; a map authoring a flickering corridor through its lightmap still renders steady. The residual
  is documented at `DynamicLightRenderer.cs`.
* **F8** — eye-trace light culling with hysteresis (deferred). Worth building when light count is actually the
  bottleneck; the budget's rank-and-cap covers the same ground more bluntly for now.
* **`r_shadow_realtime_world_shadows`** — world lights currently draw their shadow grants from the same budget
  as dynamic lights rather than having their own switch.
* **N8 rim light has not been visually confirmed** — the shader term compiles and the uniforms push, but no
  captured frame isolated the rim on a moving bot. It is off by default.
* **Release-export perf numbers.** Everything above is Debug. `./vx perf-smoke` before merging anything that
  turns a shadow path on by default.

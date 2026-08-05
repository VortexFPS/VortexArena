# Real-time lighting and light grids — DarkPlaces reference, Vortex gap analysis, and options

**Date:** 2026-08-05
**Scope:** How lighting works in Base Xonotic/DarkPlaces (`../Base/darkplaces`, `../Base/data/xonotic-data.pk3dir`),
what Vortex Arena renders today, where the two diverge, and what we could build — both to match Base and to go past it.
**Method:** Read the DarkPlaces C sources and embedded GLSL directly; read every Vortex lighting consumer.
Claims about Base and about Vortex code are `[verified — read the source]` unless tagged otherwise. No runtime
capture was taken for this document, so all *performance* numbers below are `[assumed]` and named as such.

---

## 0. The headline, before the detail

**The premise needs one correction and one confirmation.**

- **Correction — Vortex *does* have real-time dynamic lighting, and has had it for a while.** Six independent
  systems spawn `OmniLight3D` nodes into the live scene, and the world's lightmap shader has a `light()` function
  that accepts them. What Vortex has *no* form of is **shadows** — every dynamic light in the game is created with
  `ShadowEnabled = false`, the world geometry is created with `CastShadow = Off`, the world shader declares
  `shadows_disabled`, and the sun's shadow map was deliberately switched off on 2026-08-02. So the accurate
  statement is "we have unshadowed real-time lights, and nothing in a match casts a shadow."

- **Confirmed — the light grid is parsed and almost entirely unused.** `VortexArena.Formats.Bsp.LightGridData`
  is a complete, DP-faithful reader and trilinear sampler for BSP lump 15, and the player-skin shader has a full
  grid-lit branch. But exactly **one** thing in the game calls it: the first-person weapon viewmodel. Player
  models, item pickups, gibs, flags, projectiles and every other model are lit by a single hardcoded
  `DirectionalLight3D` plus a flat sky ambient — which is precisely the "everything looks dull and uniform"
  failure mode the grid exists to fix.

- **The single highest-value item in this document** is F1-B: port DarkPlaces' *3-D-texture* light grid so every
  model in the world samples the map's baked light per-pixel. It is one 3-D texture, one sampler, roughly the
  same shader math the viewmodel branch already runs, and it is what stock DarkPlaces does **by default**
  (`mod_q3bsp_lightgrid_texture` defaults to `1`).

---

## Part 1 — What Vortex Arena renders today

### 1.1 World geometry: baked lightmaps, plus additive dynamic light

`game/loaders/LightmapShader.cs` is the shader every IBSP world surface uses.

- The baked term (`albedo × lightmap × lightmap_scale`, plus `_glow`, plus deluxemap specular) is written to
  **`EMISSION`**, which Godot's lighting never touches. So the static look is exact and untouchable.
- `ALBEDO` carries plain linear diffuse purely so the `light()` function has something to modulate.
- `light()` adds a Lambert term for **omni lights only** — `if (!LIGHT_IS_DIRECTIONAL)` — scaled by a global
  shader parameter `world_dlight`. Directional light is deliberately discarded so the scene's sun does not add a
  second constant term on top of an already-fully-baked surface.
- `render_mode ... ambient_light_disabled, shadows_disabled`. The world neither receives shadows nor pays for
  the PCF taps.
- **Deluxemaps are supported** (`use_deluxemap`), reproducing DP's `MODE_LIGHTDIRECTIONMAP_MODELSPACE`: decode
  `deluxe*2-1`, rotate into the surface tangent frame, undo the angle attenuation with
  `1/max(0.25, lightnormal.z)`, then re-apply the directional diffuse. `MapLoader` generates per-vertex tangents
  for deluxemapped surfaces to feed it.
- Vertex-lit faces (negative lightmap index, q3map2 `-3`) modulate by mesh `COLOR` instead — DP's
  `MODE_VERTEXCOLOR`.
- `_norm` normal maps and `_gloss` specular companions are wired.
- Colour space is explicit (`srgb_color` uniform) so both of Xonotic's modes (`vid_sRGB` /
  `mod_q3bsp_sRGBlightmaps`) can be reproduced.

**Verdict:** the *static* world lighting is at or near parity with DarkPlaces' default path. This part is solid.

### 1.2 Dynamic lights: six sources, zero shadows

| Source | File | What it lights | Shadows |
|---|---|---|---|
| `dynlight` map entities | `game/client/DynamicLightRenderer.cs` | mapper-placed lights, path-travelling / FOLLOW / tag-attached | off (`:147`) |
| effectinfo flashes | `game/client/EffectSystem.cs:2109-2190` | explosions, impacts — any block with `lightradius > 0` | off |
| projectiles | `game/client/ProjectileRenderer.cs:696-721` | rockets, plasma, fireballs | off (`:712`, `:721`) |
| laser / beam endpoint | `game/client/LaserRenderer.cs:126` | the `adddynamiclight` in `laser.qc:338` | off (`:131`) |
| CSQC model effects | `game/client/CsqcModelEffects.cs:294-335` | `EF_*` player auras (`csqcmodel_hooks.qc:557-593`) | off |
| viewmodel | `game/client/ViewModel.cs:231-252` | muzzle flash + a constant fill light | off (`:237`, `:252`) |

Quality-of-implementation notes that are genuinely good and worth preserving:

- `EffectSystem` pools its flashes (`MaxFxLights = 24`, oldest-steals-on-saturation) rather than churning nodes,
  mirroring DP's fixed dlight slot array.
- `DynamicLightRenderer` PVS-gates its lights (`r_pvs_cull_dynlights`) so an out-of-view light does not enter
  Godot's clustered light grid.
- Both split a `color` whose components exceed 1 into a normalised hue plus `LightEnergy`, so DP's
  "nuclear blast" colours (`lightcolor 8 4 1`) brighten instead of clipping.
- `DynamicLightRenderer` samples the worldspawn light-style table so a styled dynlight flickers.

### 1.3 Model lighting: the grid exists, and one thing uses it

- `src/VortexArena.Formats/Bsp/LightGridData.cs` — parses lump 15, derives grid dims from world-model bounds
  exactly as `Mod_Q3BSP_LoadLightGrid` does, validates the lump length, trilinearly blends the 8 surrounding
  cells, and decodes the longitude/latitude direction bytes with ioquake3's `R_SetupEntityLightingGrid` formula.
  It also precomputes `AverageIntensity` over non-black cells so callers can normalise per-map.
- `game/loaders/PlayerSkinShader.cs:208-250` — a complete grid-lit branch reproducing DP's
  `MODE_LIGHTDIRECTION` combine: `tex×ambient + tex×diffuse×max(0,N·L) + gloss.rgb×diffuse×pow(N·H, 1+32·gloss.a) + glow`,
  with an optional gamma-space variant behind `r_model_light_gamma`. It outputs to `EMISSION` with `ALBEDO`
  zeroed so scene lights cannot double-light it.
- `game/client/ModelTint.cs:77` `ApplyGridLight` — pushes `grid_lit` / `grid_ambient` / `grid_diffuse` / `grid_dir`
  as instance uniforms.

**And the only caller is `ViewModel.cs:621`**, fed from `NetGame.UpdateViewModelLightgrid()` (`:3087`), which
samples the grid at the *camera*. Nothing else in the game calls `ApplyGridLight`. Player models, item pickups,
gibs, dropped weapons, flags and projectiles all take the `grid_lit == 0` PBR branch and are lit by:

- one `DirectionalLight3D` named `Sun` at a **hardcoded** `(-50°, -30°, 0°)` (`NetGame.cs:12915`), shadows gated
  on `r_sun_shadow` which **defaults to `0`** (`ClientSettings.cs:216`), and
- `AmbientLightSource = Sky`, `AmbientLightEnergy = 0.6`.

That is a single global key light with no relationship to the map. A rocket launcher in a pitch-black basement
and the same launcher in a floodlit yard render identically.

### 1.4 What has no implementation at all

`.rtlights` loading, realtime world lighting, coronas / lens flares, `gl_flashblend`, bounce-grid GI, cubemap
light filters (gobos), light-style animation of *world lightmaps*, fake model shadows (`r_shadows`), and any
shadow at all in a live match.

The `r_shadow_*` and `r_coronas` cvars **do** appear in `data/core.pk3dir/effects-*.cfg` — inherited verbatim
from Base — but `docs/reference/CVARS.md` marks them *unregistered*, meaning nothing reads them. The
2026-06-14 graphics audit (`planning/graphics-settings-audit-2026-06-14.md`, rows 97-106 and Table 3) already
flagged this, and item 12 in its findings table calls it a menu-honesty problem.

### 1.5 The platform

Godot **4.6**, **Forward+** (`project.godot:23`). Forward+ gives clustered lighting, a shadow atlas, MSAA 4×
fixed, and access to `SDFGI`, `VoxelGI`, `LightmapGI`, `ReflectionProbe`, volumetric fog, and screen-space
shadows/AO/reflections. None of the GI or probe features are currently enabled.

---

## Part 2 — How DarkPlaces and Xonotic actually do it

### 2.1 Three lighting modes, and which one Xonotic ships

DarkPlaces has three ways to light the world, selected by cvar:

1. **Lightmap mode (the default).** Baked lightmaps from q3map2, optionally with deluxemaps for direction.
   `r_shadow_realtime_world 0`.
2. **Realtime dlight mode.** `r_shadow_realtime_dlight 1` (default). Transient lights — explosions, rocket
   trails, muzzle flashes, CSQC `adddynamiclight` — are added *on top of* the lightmaps.
3. **Realtime world mode.** `r_shadow_realtime_world 1`. Static light sources loaded from a `.rtlights` file
   replace the lightmaps entirely (the lightmaps are re-admitted at
   `r_shadow_realtime_world_lightmaps` brightness, default `0`), with real shadows.

**What Xonotic actually ships matters enormously here.** From `../Base/data/xonotic-data.pk3dir/effects-*.cfg`:

| preset | `realtime_dlight` | `dlight_shadows` | `realtime_world` | `world_shadows` | `coronas` | `deluxemapping` | `gloss` |
|---|---|---|---|---|---|---|---|
| low | 0 | 0 | 0 | 0 | 1 | 0 | 0 |
| med | 1 | 0 | 0 | 0 | 1 | 0 | 0 |
| normal | 1 | 0 | 0 | 0 | 1 | 1 | 1 |
| high | 1 | 0 | 0 | 0 | 1 | 1 | 1 |
| ultra | 1 | 1 | **1** | 0 | 1 | 1 | 1 |
| ultimate | 1 | 1 | **1** | **1** | 1 | 1 | 1 |
| omg | 0 | 0 | 0 | 0 | 1 | 0 | 0 |

Realtime world lighting is **off in every preset a normal player uses**. And only **six** stock maps ship a
`.rtlights` file at all — `bromine`, `fuse`, `glowplant`, `implosion`, `runningman`, `techassault`
(verified by enumerating `xonotic-20230620-maps.pk3`; 6 of 4844 files).

**Therefore: Xonotic's shipping look is baked lightmaps + unshadowed realtime dlights + coronas + light-grid
model lighting.** Vortex already has three of those four. The missing one is the light grid on models, and the
missing garnish is coronas.

### 2.2 The light data model

`rtlight_t` (`client.h:104-230`) and its authoring wrapper `dlight_t` (`client.h:232-303`):

| field | meaning |
|---|---|
| `origin`, `angles` | position and orientation (orientation matters only for cubemap filters) |
| `radius` | reach; DP notes it is "brightness, not really radius anymore" |
| `color[3]` | typically `1 1 1`, may be dim or overbright |
| `style` | light-style index to modulate brightness (`currentcolor = color × d_lightstylevalue`) |
| `shadow` | whether this light casts |
| `corona`, `coronasizescale` | flare intensity and size (size default `0.25` of light radius) |
| `ambientscale`, `diffusescale`, `specularscale` | per-light weighting of the three shading terms |
| `cubemapname[64]` | a cubemap light *filter* — gobo / stained-glass projection |
| `flags` | `LIGHTFLAG_NORMALMODE` / `LIGHTFLAG_REALTIMEMODE` — which world mode the light participates in |

`dlight_t` adds the transient fields: `die`, `decay`, `intensity`, `initialradius`, `initialcolor`, and an owning
entity.

### 2.3 The `.rtlights` file format, exactly

Written by `R_Shadow_SaveWorldLights` (`r_shadow.c:4932-4984`), read by `R_Shadow_LoadWorldLights` (`:4829`).
Plain text, one light per line, three progressively-shorter forms chosen by what differs from the defaults:

```
[!]origin_x origin_y origin_z radius r g b style "cubemapname" corona angles_x angles_y angles_z coronasizescale ambientscale diffusescale specularscale flags
[!]origin_x origin_y origin_z radius r g b style "cubemapname" corona angles_x angles_y angles_z
[!]origin_x origin_y origin_z radius r g b style
```

A leading `!` means **this light casts no shadow**. The short form is emitted when
`coronasizescale == 0.25 && ambientscale == 0 && diffusescale == 1 && specularscale == 1 && flags == LIGHTFLAG_REALTIMEMODE`
and there is no cubemap, corona or rotation.

Fallback chain when no `.rtlights` exists: a `.lights` file (hlight format — `R_Shadow_LoadLightsFile`), then a
`.ent` file, then the map's own entity lump (`R_Shadow_LoadWorldLightsFromMap_LightArghliteTyrlite`, gated by
`r_shadow_realtime_world_importlightentitiesfrommap`, default `1`).

### 2.4 `r_editlights` — the in-engine light editor

DarkPlaces ships a full interactive light editor (`r_shadow.c`, `R_Shadow_EditLights_*`). `r_editlights 1` draws
sprite markers for every light, with distinct sprites for shadow/noshadow and cubemap/no-cubemap. Commands:
`_spawn`, `_edit <property> <value>`, `_editall`, `_remove`, `_clear`, `_save`, `_reload`, `_toggleshadow`,
`_togglecorona`, `_copyinfo`, `_pasteinfo`, `_lock`, `_importlightentitiesfrommap`, `_importlightsfile`, `_help`.
Cursor behaviour is tunable (`_cursordistance`, `_cursorpushback`, `_cursorpushoff`, `_cursorgrid` snap), and the
selected light's properties are mirrored into live cvars (`r_editlights_current_*`) so they can be scripted.

**This is worth noting for Vortex specifically** because `game/vmap/EditorLighting.cs` (1206 lines) and
`game/vmap/EditorLightBake.cs` (1266 lines) already exist — Vortex has an in-editor lighting system that
DarkPlaces' editor is the direct ancestor of.

### 2.5 Shadows

**Shadow mapping** (`r_shadow_shadowmapping`, default `1`) is the modern path:

- A single **atlas**, `r_shadow_shadowmapping_texturesize` default **8192**, into which every light's shadow map
  is packed at frame start.
- Per-light side size is chosen by `r_shadow_shadowmapping_precision` (default `1`) — "maximum resolution of this
  number of pixels per light source radius unit", clamped to `[minsize=32, maxsize=512]`. So shadow resolution
  scales with light size automatically; this is a real LOD system, not a fixed cost.
- Omni lights store **6 faces in a 2×3 grid**, or **12 faces in a 4×3 grid** when `EF_NOSELFSHADOW` entities are
  present (a second set of faces excluding self-shadowing casters).
- **VSDCT** (virtual shadow depth cube texture, `r_shadow_shadowmapping_vsdct`, default `1`) — an indirection
  cube map that turns cube-face selection into a texture lookup.
- Filtering: `filterquality` `-1` auto-selects by GPU vendor; `0` none, `1` bilinear, `2` 2×2 PCF, `3` 3×3, `4` 4×4.
  `useshadowsampler` prefers hardware `sampler2DShadow`.
- Bias: `bias` (0.03, scaled by `nearclip × 1024 / lodsize`), `polygonfactor` (2, slope-dependent),
  `polygonoffset` (0), `bordersize` (5 texels of filter margin), `nearclip` (1).
- `r_shadow_deferred` — an optional deferred/image-based path (depth + normal prepass, lights accumulated into
  separate diffuse and specular buffers).

**Fake model shadows** (`r_shadows`, `gl_rmain.c:123-131`) are a separate, much cheaper feature: models cast a
projected shadow onto the world but rtlights are unaffected. `r_shadows 1` throws the shadow along the model's
own lighting direction; `r_shadows 2` uses a fixed `r_shadows_throwdirection` (default `0 0 -1`, i.e. straight
down). Tunables: `r_shadows_darken` (0.5), `r_shadows_throwdistance` (500), `r_shadows_focus`,
`r_shadows_shadowmapscale` (0.25), `r_shadows_shadowmapbias`, `r_shadows_castfrombmodels`,
`r_shadows_drawafterrtlighting`. **This is the cheap way to give players a grounding shadow without a full
realtime-world solve**, and Xonotic leaves it at `0` but it is one cvar away.

### 2.6 Light culling — the part that makes many lights affordable

DarkPlaces spends real effort here, and it is directly relevant to any Vortex plan:

- `r_shadow_culllights_pvs` (default 1) — does the light's volume overlap any *visible* BSP leaf?
- `r_shadow_culllights_trace` (default 1) — fire `samples` (default 16) rays from the eye to random points in the
  light's bounds; if none connect, the light is invisible. Tunables: `_eyejitter` (16), `_enlarge`, `_expand` (8),
  `_pad` (8), `_tempsamples` (16, for CSQC-created lights with no inter-frame caching), `_delay` (1 second of
  hysteresis after any successful trace, to stop flicker).
- `r_shadow_usebihculling` (default 1) — BIH instead of BSP for finding lit surfaces.
- `r_shadow_scissor` (default 1) — restrict rasterisation to the light's screen-space bounds.
- Compile-time: `r_shadow_realtime_world_compile` precomputes per-light surface lists, leaf PVS, and per-triangle
  shadow/light bit vectors; `compilesvbsp` (exact, slow) or `compileportalculling` (faster, overrides svbsp).

Vortex has an analogue of the first item only (`r_pvs_cull_dynlights` in `DynamicLightRenderer`).

### 2.7 The light grid — and the part Vortex is missing

The Q3 BSP light grid is **lump 15**: a uniform 3-D array of probes over the world model's bounds, 8 bytes per
cell — `ambient RGB`, `directed RGB`, `direction yaw byte`, `direction pitch byte`. Default cell size
64 × 64 × 128. `Mod_Q3BSP_LoadLightGrid` (`model_brush.c:6443`) derives the dims from the world model bounds
with `imins = ceil(mins/size)`, `imaxs = floor(maxs/size)`, `isize = imaxs - imins + 1`, and applies the sRGB
conversion dictated by `mod_q3bsp_sRGBlightmaps` / `vid_sRGB`.

**DarkPlaces has two consumption paths, and the default is the one Vortex does not have.**

**Path A — CPU point sample (`Mod_Q3BSP_LightPoint`).** Trilinearly sample the grid at the entity's origin,
yielding one ambient colour, one directed colour and one direction for the *whole model*. This is what Vortex's
`LightGridData.Sample` implements, and what the viewmodel uses.

**Path B — GPU 3-D texture (`mod_q3bsp_lightgrid_texture`, default `1`).** At load, DP packs the whole grid into
a **single 3-D texture** and samples it **per-fragment**:

- Texture dims are `[nx, ny, (nz + 2) × 3]` — three stacked z-layers of the grid, plus 2 rows of padding.
- Layer 0 (z ∈ [0, ⅓)) = ambient RGB. Layer 1 (z ∈ [⅓, ⅔)) = directed RGB. Layer 2 (z ∈ [⅔, 1]) = the
  bent-normal light direction, encoded as a signed unit vector in `[0,1]` (`×127 + 127`), reconstructed from the
  yaw/pitch bytes via DP's `mod_md3_sin` table.
- The direction layer gets a neutral `(127,127,127,255)` padding row above and below it, so clamped sampling at
  the layer boundary degrades to "no direction" instead of bleeding ambient colour into the normal.
- A `lightgridworldtotexturematrix` maps world position → normalised texture coordinate; the vertex shader emits
  `LightGridTC = LightGridMatrix * Attrib_Position` (`shader_glsl.h:1344`).
- The fragment shader (`MODE_LIGHTGRID`, `shader_glsl.h:1567-1590`) does:

```glsl
vec3 LGTC = vec3(LightGridTC.xy, min(LightGridTC.z, 0.333333));   // clamp into layer 0
ambientcolor          = texture(Texture_LightGrid, LGTC).rgb;
lightcolor            = texture(Texture_LightGrid, LGTC + vec3(0,0,0.333333)).rgb;
lightnormal_worldspace= texture(Texture_LightGrid, LGTC + vec3(0,0,0.6666667)).rgb * 2.0 - 1.0;
lightnormal_modelspace= lightnormal_worldspace * LightGridNormalMatrix;
// rotate into tangent space against VectorS/T/R, normalize, then:
color.rgb  = diffusetex * (Color_Ambient + Color_Diffuse * (ambientcolor + diffuse * lightcolor));
color.rgb += glosstex.rgb * (specular * Color_Specular * lightcolor);   // USESPECULAR
```

The `min(LightGridTC.z, 0.333333)` clamp is a deliberate fix, commented in the source: light grid bounds are set
by the level designer and usually cover the playable area only, not the surrounding scenery, so an unclamped
sample would repeat-artifact outside it.

**The practical difference between A and B:** with Path A a player model standing half in a shadow and half in a
sunbeam is uniformly lit by whatever the grid says at its origin. With Path B the legs are dark and the torso is
bright, and a large model crossing a light boundary transitions smoothly instead of popping. Path B also lights
*every* fragment of *every* model from the same source, so the whole scene is coherent.

Two debug/experimental cvars extend it further: `mod_q3bsp_lightgrid_world_surfaces` and
`mod_q3bsp_lightgrid_bsp_surfaces` (both default 0) light the **world BSP geometry itself** from the grid instead
of from lightmaps.

**Which path an entity takes** is decided in `CL_UpdateEntityShading_Entity` (`cl_main.c:2680-2775`), in priority
order: `EF_FULLBRIGHT` / `r_fullbright` → CSQC `RENDER_CUSTOMIZEDMODELLIGHT` overrides → sprites (always
`R_CompleteLightPoint` with all three flags) → **light-grid texture** if available → `R_CompleteLightPoint` with
`LP_LIGHTMAP` → `r_fullbright_directed` fallback.

### 2.8 `R_CompleteLightPoint` — the unified light probe

`r_shadow.c:6014-6142`. This is DP's "what light is at this point" query, used by sprites, particles, CSQC
`getlight`, and any entity not on the grid-texture path. It accumulates **first-order spherical harmonics**
(`sa`, `sx`, `sy`, `sz`, `sd`) from up to three sources selected by flags:

- `LP_LIGHTMAP` — the world model's `LightPoint` (the grid on Q3BSP, the surface lightmap on Q1BSP), weighted by
  `r_refdef.scene.lightmapintensity`. On an unlit map it returns flat fullbright.
- `LP_RTWORLD` — every static rtlight within radius, attenuated by
  `min(1, (1-dist) × linearscale / (dividebias + dist²)) × lightintensityscale`, and **shadow-tested with an
  actual `CL_TraceLine`** if the light casts.
- `LP_DYNLIGHT` — the same for every scene dlight.

It then extracts a weighted-average light direction (the "bent normal") from `sd`, projects the diffuse colour
along it, and computes `ambient = sa - 0.333 × diffuse + ambientintensity`. There is a `FIXME: sample bouncegrid
too!` in the source — the bounce grid is not sampled here.

### 2.9 Bounce grid — DarkPlaces' global illumination

`r_shadow_bouncegrid` (default `0`). A photon-tracing radiosity solve accumulated into a 3-D texture:

- **Static mode** (`_static 1`, default): quality 16, up to 250 000 photons, 5 bounces, 64-unit spacing —
  computed once per map, high quality.
- **Dynamic mode**: quality 1, 25 000 photons, `_updateinterval` seconds, `_culllightpaths`, and
  `_dlightparticlemultiplier` to let explosions contribute bounce light.
- `_directionalshading` (default 1) stores 8× the data so the bounce is directional rather than flat ambient.
- `_floatcolors` (RGBA16F, or 32F at 2), `_blur`, `_subsamples`, `_threaded` (uses the task queue),
  `_rng_type` / `_rng_seed` (`-1` = time-seeded, for "disco-like craziness"), `_lightpathsize` (64),
  `_particlebounceintensity` (4), `_intensity` (4), `_includedirectlighting`.

Xonotic never enables it in any preset.

### 2.10 Coronas

`r_coronas` (default `0` in DP, but **`1` in every Xonotic preset**) — a bright flare sprite drawn at the light
position. Per-light `corona` intensity and `coronasizescale` (default 0.25 × radius). `r_coronas_occlusionquery`
uses a GPU occlusion query to fade the flare by the proportion of visible pixels
(`r_coronas_occlusionsizescale` 0.1); enabled at high/ultra/ultimate. `gl_flashblend` is the ancient
"draw coronas *instead of* real lighting" mode — fast and ugly.

### 2.11 Light styles

`d_lightstylevalue` drives both animated *lightmaps* (the classic Quake flicker/pulse/candle/strobe strings) and
any rtlight with a non-zero `style` (`currentcolor = color × d_lightstylevalue`). Vortex implements the rtlight
half (`LightStyles.Sample` in `DynamicLightRenderer`) but not the lightmap half — this is already documented as a
known residual in `DynamicLightRenderer.cs:27-30`.

### 2.12 Cubemap light filters

A light with a `cubemapname` projects that cubemap as a *filter* — the light's colour is multiplied by the
cubemap sampled along the light-to-fragment vector (`shader_glsl.h:1560`,
`color.rgb *= textureCube(Texture_Cube, CubeVector)`). This is how gobos, stained-glass windows, caustics and
shaped spotlights are authored. The light's `angles` orient the cube.

### 2.13 Where dynamic lights come from in Xonotic QC

`adddynamiclight(org, radius, colour)` / `adddynamiclight2(..., style, cubemapname, pflags)` — builtin #305.
Call sites in `../Base/data/xonotic-data.pk3dir/qcsrc`:

- `client/csqcmodel_hooks.qc:571-633` — player effect auras: blue/red flags, `EF_FULLBRIGHT`-ish glows, the
  burning/flame light (`PFLAGS_FULLDYNAMIC`, explicitly *without* `PFLAGS_CORONA` — "it looks bad"), the
  freeze/cold light, and per-foot speed-powerup lights.
- `common/mapobjects/misc/laser.qc:338` — the laser's impact light.
- `common/weapons/weapon/arc.qc:1075,1094` — the Arc beam.
- `common/gametypes/gametype/ctf/sv_ctf.qc:1444` — `g_ctf_dynamiclights`.

Plus **153 `lightradius` blocks in `effectinfo.txt`** — every explosion, impact and muzzle flash carries its own
light spec (`lightradius`, `lightcolor`, `lightradiusfade`).

Vortex ports the effectinfo half and the laser/CSQC halves already.

---

## Part 3 — The gap, at a glance

| # | Feature | Base / DP | Vortex today | Gap |
|---|---|---|---|---|
| 1 | Baked lightmaps on world | ✅ | ✅ | none |
| 2 | Deluxemaps (directional lightmaps) | ✅ | ✅ | none |
| 3 | Vertex-lit surfaces | ✅ | ✅ | none |
| 4 | `_glow` / `_norm` / `_gloss` companions | ✅ | ✅ | none |
| 5 | Realtime dlights over lightmaps | ✅ (on at med+) | ✅ (always on, ungated) | cvar gating only |
| 6 | effectinfo `lightradius` flashes | ✅ | ✅ (pooled, cap 24) | none |
| 7 | Light-style animation of *rtlights* | ✅ | ✅ | none |
| 8 | **Light grid → model lighting** | ✅ **per-pixel by default** | ⚠️ **viewmodel only, per-entity** | **large** |
| 9 | **Coronas / lens flares** | ✅ (on in every preset) | ❌ | **medium** |
| 10 | Light-style animation of *lightmaps* | ✅ | ❌ | small |
| 11 | Dynamic-light shadows | ✅ (ultra+) | ❌ | medium |
| 12 | `.rtlights` load + realtime world | ✅ (ultra+, 6 maps) | ❌ | medium |
| 13 | `r_shadows` fake model shadows | ✅ (off by default) | ❌ | small |
| 14 | Cubemap light filters (gobos) | ✅ | ❌ | small |
| 15 | Bounce-grid GI | ✅ (never enabled) | ❌ | nil (Base never ships it) |
| 16 | Light culling: PVS | ✅ | ✅ | none |
| 17 | Light culling: eye-trace + hysteresis | ✅ | ❌ | small |
| 18 | In-engine light editor | ✅ `r_editlights` | ⚠️ vmap editor exists, no `.rtlights` I/O | small |
| 19 | Effects-preset cvars actually wired | ✅ | ❌ (unregistered stubs) | medium |

---

## Part 4 — Options

Two groups. **F-series = feature matching with Base.** **N-series = new features Base does not have.**
Both are **additive lists** — items stack, so per house style each *item* carries its own recommendation and
impact line rather than one recommendation across the whole list.

A standing constraint shapes every recommendation below: the project's performance target is DarkPlaces-class,
2 ms/frame on the RTX 3080 dev box. Anything that adds shadow-map renders is measured against that, and every
timing estimate here is **[assumed]** until `./vx perf-smoke` says otherwise.

### F-series — matching Base

* **F1 — Light-grid model lighting for everything, not just the viewmodel.** The BSP's baked light probes
  (lump 15) should light every model in the world — players, item pickups, dropped weapons, gibs, flags,
  projectiles — the way DarkPlaces does. This is the single largest visual-fidelity gap and it directly answers
  "items on the map aren't lit by the grid".
  * **A:** Extend the existing per-entity CPU path — call `LightGridData.Sample` at each entity's origin and push
    the four instance uniforms through `ModelTint.ApplyGridLight`, exactly as the viewmodel already does.
    *Impact:* smallest possible change; the shader branch, the sampler and the uniform plumbing all already
    exist, so this is wiring plus a per-entity sample cadence (re-sample on move, not every frame). Cost is one
    trilinear sample per moving entity per update — negligible. Limitation: the whole model gets one light value,
    so a player straddling a light boundary is lit uniformly and pops as they cross cells.
  * **B (recommended):** Port DarkPlaces' 3-D-texture path — build the `[nx, ny, (nz+2)×3]` RGBA8 3-D texture at
    map load, bind it as a **global** shader parameter with a world→texture matrix, and add a `MODE_LIGHTGRID`
    branch to `PlayerSkinShader` that samples it per fragment.
    *Impact:* per-pixel model lighting for every model at the cost of **one 3-D texture fetch per fragment** and
    one texture upload per map — this is what stock DP does by default, so it is a proven budget. A typical
    Xonotic grid is a few hundred KB to low single-digit MB of VRAM **[assumed — not measured; derive from
    `nx·ny·(nz+2)·3·4` bytes on a real map before committing]**. Removes the popping in A entirely, and makes the
    scene coherent because the world and the models finally agree about where the light is. Larger change than A:
    new texture build, new uniform binding, new shader branch, plus the `min(z, 1/3)` clamp and the neutral
    padding rows, both of which must be reproduced or the edges artifact.
  * **C (skip):** Keep the hardcoded sun as the only model light.
    *Impact:* free, and it is the current state — but it is the reason weapons and pickups read flat and
    map-independent, and it is a visible divergence from Base on every single map.
  * **Note:** A and B are not mutually exclusive in the long run — B is the fast path when the map has a grid, A
    (or `R_CompleteLightPoint`-style probing) remains the fallback for models outside the grid bounds and for
    maps with no lump 15. Doing A first as a stepping stone is defensible; doing only A is the thing to avoid.

* **F2 — Coronas / lens flares (recommended).** `r_coronas` is `1` in **every** Xonotic preset including `low`,
  so its absence is a divergence from the *default* Base look, not from a high-end option. A corona is a
  camera-facing additive sprite at a light's position, sized `coronasizescale × radius`, faded by visibility.
  *Impact:* cheap — a handful of additive quads per frame, and Godot's `Environment` already runs a glow pass
  that they will feed. The occlusion-query fade (`r_coronas_occlusionquery`) has no direct Godot equivalent; a
  depth-buffer sample or a short raycast from the camera to the light is the practical substitute, and either is
  a per-light-per-frame cost that a light budget already bounds. Biggest single "it looks like Xonotic now" win
  per unit of work after F1.

* **F3 — Shadows for dynamic lights.** Every `OmniLight3D` in Vortex is `ShadowEnabled = false`. Godot Forward+
  can shadow them.
  * **A (recommended):** Enable shadows on a small, ranked subset — the N brightest/nearest lights, N driven by a
    quality cvar, defaulting to `0` at low/med and something like 2-4 at high/ultra. Requires re-enabling
    `CastShadow` on world geometry and dropping `shadows_disabled` from `LightmapShader`.
    *Impact:* an omni shadow is **six** shadow-map renders of every caster in range; this is the single most
    expensive item in this document and it must be budget-capped, not enabled globally. Matches Base, which
    turns `r_shadow_realtime_dlight_shadows` on only at ultimate. Note the coupling the 2026-08-02 change
    documented: `LightmapShader`'s `shadows_disabled` and `r_sun_shadow 0` were retired *as a pair*, so both come
    back together or neither does.
  * **B:** Screen-space contact shadows only (`Light3D.shadow_...`/SSS-style), no shadow maps.
    *Impact:* a fraction of the cost of A and it grounds objects convincingly at short range, but it cannot cast
    a shadow across a room, so it is a different effect wearing the same name. Good as the low/med tier under A.
  * **C (skip):** Enable shadows on all dynamic lights.
    *Impact:* correctness at the price of the frame budget — a firefight can have 24 pooled flash lights live at
    once, and six cube faces each is 144 shadow renders. This is the option that quietly destroys the 500 fps
    target.

* **F4 — `.rtlights` loading and realtime world lighting.** Parse the format in §2.3, build Godot lights from
  it, and dim or replace the lightmaps per `r_shadow_realtime_world_lightmaps`.
  *Impact:* meaningful work — file parsing (easy), the `.ent`/entity-lump import fallback (moderate), the mode
  switch that swaps the world between baked and realtime (invasive, touches `LightmapShader`'s core assumption
  that the baked term is authoritative), plus F3 for it to look like anything. And the payoff is bounded: **six
  stock maps ship the file**, and **no shipping Xonotic preset below `ultra` turns the mode on**. Worth doing for
  completeness and for custom maps, but it is a fidelity flourish, not a parity necessity — rank it below F1/F2.

* **F5 — Fake model shadows (`r_shadows`) (recommended).** DP's cheap grounding shadow: project each model's
  silhouette onto the world along its light direction (`r_shadows 1`) or straight down (`r_shadows 2`), darkened
  by `r_shadows_darken`, out to `r_shadows_throwdistance`.
  *Impact:* far cheaper than F3 — one directional shadow render for the model set, or even a projected blob
  decal, rather than six cube faces per light. Gives players and items the visual grounding they currently lack
  without touching the world's realtime light budget. Base ships it off by default, so enabling it is a *choice*
  rather than parity; but the machinery is parity, and it is the best shadow-per-millisecond in the document.
  Pairs naturally with F1 because the grid supplies the throw direction.

* **F6 — Light-style animation of world lightmaps.** Already documented as a known residual in
  `DynamicLightRenderer.cs:27-30`: styled *dynlights* animate, styled *world brush* lightmaps do not, so a map
  authored with a flickering corridor renders steady.
  *Impact:* needs per-style lightmap pages or a per-surface style index plus a small uniform array the shader
  multiplies by — a self-contained change to `LightmapShader` and `MapLoader`. Low risk, visible only on maps
  that use named styles. Fixes a stated, dated gap.

* **F7 — Cubemap light filters (gobos).** A `cubemapname` on a light multiplies its colour by a cubemap sample.
  *Impact:* Godot spot lights take a projector texture natively; omni lights do not take a cubemap projector, so
  faithful support means a custom light shader or approximating with spots. Small payoff — this is used by a
  handful of authored lights in a handful of maps — but it is cheap where a spot can stand in.

* **F8 — Light culling parity: eye-trace with hysteresis.** Add DP's `r_shadow_culllights_trace` on top of the
  PVS gate Vortex already has: sample rays from the eye into the light's bounds, keep the light alive for
  `_delay` seconds after any hit.
  *Impact:* pure win where light count is the bottleneck, and it is the prerequisite that makes F3/F4 affordable
  — DP does not render many shadowed lights cheaply, it renders *few* lights because it culls hard. Costs a
  handful of raycasts per light per frame, bounded by the light count. Only worth building once something is
  actually pushing the light budget.

* **F9 — Wire the effects presets, or stop pretending (recommended).** `data/core.pk3dir/effects-*.cfg` carries
  `r_shadow_realtime_dlight`, `r_shadow_realtime_world`, `r_coronas`, `r_shadow_shadowmapping`,
  `r_shadow_usenormalmap`, `r_shadow_gloss` — all inherited from Base, all *unregistered*, all inert. The
  2026-06-14 graphics audit already flagged this as finding 12.
  *Impact:* two honest choices, and either beats the status quo. Register and wire the ones whose features now
  exist (`r_coronas` after F2, `r_shadow_realtime_dlight` as a real gate on the six light sources,
  `r_shadow_realtime_dlight_shadows` after F3); and hide or grey the ones with no render path so the menu stops
  advertising controls that do nothing. Cheap, and it is the difference between a settings screen that describes
  the renderer and one that describes DarkPlaces' renderer.

### N-series — new features Base does not have

* **N1 — A better light grid than Q3's (recommended).** Q3's grid stores *one* ambient colour, *one* directed
  colour and *one* direction per cell — it cannot represent two lights from different sides, which is why models
  in Xonotic often read flat even when grid-lit. Bake an **L1 spherical-harmonic** (4 coefficients per channel)
  or **ambient-cube** (6 directional samples) grid at map-import time instead.
  *Impact:* strictly better model shading from the same probe positions — a model lit red from the left and blue
  from the right actually shows both. Requires an import-time bake step (Vortex already has
  `game/vmap/EditorLightBake.cs`, 1266 lines, so the machinery is not foreign) and a side-car file per map, and
  it is a *divergence* from Base's data, so it needs the Q3 grid as a fallback for maps without a bake. This is
  the "additional new feature" with the highest visual return, and it composes with F1-B rather than competing
  with it — same texture-fetch shape, more coefficients.

* **N2 — Dynamic light injection into the grid (recommended).** DP's light grid is entirely static: an explosion
  lights the *world* (via dlights) but never updates what the grid says, so a player standing in a blast is lit
  by the pre-baked probe. Inject bright transient lights into a small dynamic overlay grid, or blend the N
  nearest dynamic lights into the model's grid sample.
  *Impact:* explosions and muzzle flashes finally illuminate *players* coherently rather than only the walls
  behind them — a genuinely new look Base does not have, and one that reads as a gameplay cue (you can see
  someone lit up by their own rocket). Cheap in the "blend N nearest lights into the per-entity sample" form
  (this is essentially `R_CompleteLightPoint`'s `LP_DYNLIGHT` term, which DP computes but only for non-grid
  entities); moderately expensive in the "second 3-D texture updated per frame" form.

* **N3 — Volumetric fog and light shafts.** Godot 4.6 Forward+ has volumetric fog that light volumes feed
  directly (`Light3D.light_volumetric_fog_energy`).
  *Impact:* god rays through windows, glowing volumes around rockets and explosions, atmosphere in dark maps —
  visually the biggest "this is not a 2003 engine" upgrade available, and it composes with the map-declared fog
  `MapLoader.ApplyFog` already reads. Costs a froxel volume pass per frame; needs a quality cvar and a
  competitive-visibility review, because fog that hides players is a gameplay change, not a graphics one.
  Tag it explicitly as an option competitive players can turn off.

* **N4 — Reflection probes / screen-space reflections.** The player-skin shader already has a `dpreflectcube`
  path (`has_reflect_cube`) that falls back to the world environment when no explicit cubemap is bound.
  *Impact:* `ReflectionProbe` nodes placed from the light grid's bright cells would give weapons and armour real
  local reflections instead of a generic sky. Moderate cost (probe renders, amortised across frames), and it
  makes the existing `_reflect` mask machinery pay off properly.

* **N5 — Emissive-surface auto-lights.** Vortex already knows which surfaces carry a `_glow` page, and Q3
  shaders declare `q3map_surfacelight`. Auto-place a small unshadowed light at bright emissive clusters at map
  load.
  *Impact:* lava, light fixtures and glowing signage would actually cast light on nearby models — closing the
  gap on maps that have no `.rtlights` file (i.e. almost all of them) without asking anyone to author lights.
  Needs a clustering pass at load and a hard cap on generated lights. This is the pragmatic 80 % of F4 at a
  fraction of the cost, and it is arguably the most interesting item in the N-series.

* **N6 — A light budget manager with a real millisecond target (recommended).** Today each light source system
  caps itself independently (`EffectSystem`'s 24, and nothing anywhere else). A single arbiter should rank every
  candidate light by screen-space importance — brightness × solid angle × proximity — and grant shadow /
  no-shadow / cull per frame against a budget in ms.
  *Impact:* this is what makes F3, F5, N2 and N5 safe to enable at all; without it, each is an unbounded cost
  that a busy firefight can multiply. It is also the natural place to implement F8's trace-culling and the
  existing PVS gate as *policies* rather than per-system special cases. Note `game/vmap/EditorLighting.cs`
  already does exactly this shape for the editor (`:966-998` ranks lights and grants `ShadowEnabled` by budget),
  so there is a working precedent in-repo to lift.

* **N7 — `.rtlights` import/export in the vmap editor.** Vortex has `EditorLighting.cs` and `EditorLightBake.cs`;
  DarkPlaces has `r_editlights` and a text format every Xonotic mapper already knows.
  *Impact:* lets Vortex maps carry authored realtime lights that Base can also read, and lets the six stock
  `.rtlights` maps round-trip. Small, well-bounded work, and it turns the existing editor into a tool that
  produces something Base-compatible. Depends on F4 for the runtime side to mean anything.

* **N8 — Gameplay-legible lighting.** Team-coloured rim light on players, an intensity pulse on powerup carriers,
  a distinct grade on low-health opponents.
  *Impact:* pure gameplay value — target identification at a glance in a fast arena shooter, using the lighting
  system rather than HUD clutter. Entirely a Vortex-original feature; needs balance review because it is a
  competitive-information change, not a cosmetic one. Cheap to implement (per-instance uniforms on the existing
  skin shader) and the kind of thing a fork can do that upstream cannot.

* **N9 — SDFGI / VoxelGI for real-time global illumination.** Godot 4.6 ships both.
  *Impact:* SDFGI needs no bake and handles large open maps; VoxelGI is higher quality on bounded interiors but
  needs a bake. Either is a step past DarkPlaces' bounce grid (which Xonotic never enables anyway). Cost is high
  and the target is 2 ms/frame, so this is an ultra-preset item at best — but it is the only option here that
  produces indirect bounce light on *dynamic* geometry. Listed for completeness; do not start here.

---

## Part 5 — Suggested ordering

If the goal is "biggest visual gap closed per unit of risk", the order is:

1. **F1-B** — per-pixel light grid on all models. The gap the question started from, the largest single fidelity
   win, and a proven-cheap technique because it is DarkPlaces' own default.
2. **F2** — coronas. On in every Base preset; small, self-contained, visible everywhere.
3. **F9** — wire or retire the dead lighting cvars, now that two of them mean something.
4. **F5** — fake model shadows. Best shadow-per-millisecond; grounds players and items.
5. **N6** — the light budget arbiter, before anything that can multiply light cost.
6. **N5** — emissive-surface auto-lights. Gets `.rtlights`-grade atmosphere onto maps that have no `.rtlights`.
7. **N2** — dynamic light injection into the grid. Makes explosions light players, not just walls.
8. **F3-A / F3-B** — real dynamic shadows, budget-capped, high/ultra only.
9. Everything else, as appetite allows. **F4** (realtime world) and **N9** (GI) last: highest cost, and Base
   itself ships both switched off.

Steps 1-4 together would put Vortex's *default* lighting at or slightly ahead of Xonotic's *default* lighting,
which is the feature-matching goal. Steps 5-8 are where the fork gets to be better than the thing it forked.

---

## Appendix — Primary sources

| Topic | Location |
|---|---|
| rtlight/dlight structs | `../Base/darkplaces/client.h:104-303` |
| all `r_shadow_*` cvars | `../Base/darkplaces/r_shadow.c:141-268` |
| `.rtlights` write / read | `../Base/darkplaces/r_shadow.c:4932-4984` / `:4829` |
| `.lights` (hlight) import | `../Base/darkplaces/r_shadow.c:4986+` |
| `r_editlights` command set | `../Base/darkplaces/r_shadow.c:6684-6727` |
| `R_CompleteLightPoint` | `../Base/darkplaces/r_shadow.c:6014-6142` |
| light-grid load + 3-D texture build | `../Base/darkplaces/model_brush.c:6443-6604` |
| `mod_q3bsp_lightgrid_*` cvars | `../Base/darkplaces/model_brush.c:48-50` |
| `MODE_LIGHTGRID` GLSL | `../Base/darkplaces/shader_glsl.h:1344`, `:1451-1452`, `:1567-1590` |
| deluxemap GLSL | `../Base/darkplaces/shader_glsl.h:1324-1345` |
| cubemap light filter GLSL | `../Base/darkplaces/shader_glsl.h:1560` |
| per-entity shading path selection | `../Base/darkplaces/cl_main.c:2680-2775` |
| `r_shadows` fake shadows | `../Base/darkplaces/gl_rmain.c:123-131`, `r_shadow.c:4212-4290` |
| Xonotic effect presets | `../Base/data/xonotic-data.pk3dir/effects-*.cfg:17-34` |
| QC `adddynamiclight` call sites | `../Base/data/xonotic-data.pk3dir/qcsrc/client/csqcmodel_hooks.qc:571-633` |
| Vortex light-grid reader | `src/VortexArena.Formats/Bsp/LightGridData.cs` |
| Vortex grid-lit shader branch | `game/loaders/PlayerSkinShader.cs:208-250` |
| Vortex world shader | `game/loaders/LightmapShader.cs` |
| Vortex dynlight renderer | `game/client/DynamicLightRenderer.cs` |
| Vortex sun + environment | `game/net/NetGame.cs:12908-12956` |
| prior audit | `planning/graphics-settings-audit-2026-06-14.md` rows 97-106, Table 3, findings 7/11/12 |

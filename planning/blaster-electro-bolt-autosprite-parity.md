# Blaster + Electro primary bolt "flattened" — parity analysis & fix plan

**Date:** 2026-07-06 · **Branch:** `parity/blaster-electro-bolt-autosprite`
**Status:** ✅ **IMPLEMENTED 2026-07-10 (needs playtest).** All three phases below are built as designed
(2963 tests green, +10 new in `AutospriteBoltTests`); the manual verification checklist in Phase 3 is the
remaining gate. Where the code lives: `Formats/Md3/AutospriteQuads.cs` (bake),
`Formats/Materials/AutospriteShaderGen.cs` + `Q3StageGlsl.cs` (generated shader / shared stage emitters),
`ShaderCompiler.CompileAutosprite` + `AssetSystem.ResolveAutospriteMaterial` (game side),
`Md3Builder.ResolveSurfaces/ApplyFrame` (CUSTOM0/1), `ProjectileRenderer.BuildWarmupInstances` (PSO warm).

**Scope:** in-flight primary projectile visuals — PROJECTILE_BLASTER (`models/laser.mdl`) and
PROJECTILE_ELECTRO_BEAM (`models/elaser.mdl`). HLAC (`models/hlac_bullet.md3`) shares the exact bug and is
fixed by the same change.

---

## TL;DR

The bolts are *supposed* to be flat geometry. Base's bolt "models" (`laser.mdl`, `elaser.mdl` — MD3 content
despite the `.mdl` extension) are just **two zero-thickness quads each**; all of the 3-D illusion comes from
their Q3 shaders (`scripts/projectiles.shader`): `deformVertexes autosprite` re-aims the round core quad at
the camera every frame, and `deformVertexes autosprite2` rolls the long streak quad around its long axis
(the flight axis) to face the viewer. DarkPlaces implements both deforms on the CPU per frame
(`gl_rmain.c:8142` / `:8186`).

The port loads the same models and shaders but approximates **both** deforms with Godot's full
`BillboardMode.Enabled` (`ShaderCompiler.ApplyDeformBillboard`). After the Quake→Godot axis swap
(`godot = (q.X, q.Z, −q.Y)`) these quads lie in the **local XZ plane** (zero extent on local Y). Godot's
billboard maps local X→screen-right, local Y→screen-up, local Z→toward-viewer — so the quads' whole area
lands on *screen-right × view-depth*: they render **permanently edge-on**, a squashed horizontal sliver.
That is the "flattened" look, for both weapons.

This is the documented residual of playtest bug #38: commit `32ac05c` fixed the *shading* half; the
*geometry* half was explicitly deferred ("axial billboard is a follow-up").

Also found: electro's `tcmod page 4 1 0.1` crackle flipbook is parsed but a no-op in both compile paths — the
bolt is frozen on animation page 0.

### Correction to the #38 write-up (this branch supersedes it)

The #38 fix set **every** additive stage to `Unshaded` and called the resulting glow-in-the-dark look
"faithful." **That is not accurate.** DP's bolt stages are `blendfunc add` + **`rgbGen lightingDiffuse`**
with a fullbright **`_glow` companion** added on top (the `*_glow.tga` files ship next to every bolt
texture). DP's model is:

```
color.rgb  = texture(base) * lightingDiffuse   // the base IS lit by model lighting
color.rgb += texture(glow)                      // the _glow companion is added FULLBRIGHT
// then blended GL_ONE GL_ONE (additive) onto the framebuffer
```

So the always-visible part of the bolt in a dark room is the **`_glow` companion**, not an unshaded base.
Unshaded is wrong in both directions (too hot in the dark, no lighting response in the light). **The new
autosprite bolt path will match DP: lit base + fullbright `_glow` emission, additive** — it will not be
unshaded. The general `ApplyBlend`/`ApplyBaseBlend` unshaded treatment for other map-FX additive surfaces is
a separate, broader question and is left untouched by this branch (scope discipline).

---

## How Base renders these bolts

**Gameplay/CSQC** (`Base/data/xonotic-data.pk3dir/qcsrc/`):
- `common/weapons/weapon/blaster.qc:49` → `CSQCProjectile(PROJECTILE_BLASTER)`;
  `electro.qc:358` → `CSQCProjectile(PROJECTILE_ELECTRO_BEAM)`.
- `client/weapons/projectile.qc`: `setmodel` per type (laser.mdl / elaser.mdl); `this.scale = 1` for both
  (line 341 default; neither type overrides); **`this.angles = vectoangles(this.velocity)` every frame**
  (line 107); no per-type spin for either. Blaster trail = `EFFECT_Null`; electro beam trail =
  `EFFECT_TR_NEXUIZPLASMA`.

**Models** (parsed from the shipped files):

| model | surface | geometry (Quake units) | shader |
|---|---|---|---|
| laser.mdl | Plane01 | one quad ~25.5×25.5, centered on origin, **all verts at Z=0** | `laser_projectile_core` |
| laser.mdl | Plane02 | one quad 37.6×17.4, center **−10.8 on X** (trails behind) | `laser_projectile_long` |
| elaser.mdl | Plane01 | one quad ~64×64, centered | `electro_projectile_core` |
| elaser.mdl | Plane02 | one quad 74×22, center −10.4 on X | `electro_projectile_long` |

elaser UVs span x∈[0, 0.25] — one page of a 4-page horizontal flipbook atlas.

**Shaders** (`scripts/projectiles.shader`): `*_core` = `deformVertexes autosprite`; `*_long` =
`deformVertexes autosprite2`; all four `blendfunc add` + `rgbGen lightingDiffuse`; the two electro entries
add `tcmod page 4 1 0.1` (cycle 4 pages, 0.1 s each → 10 fps crackle). Every bolt texture ships a
`_glow.tga` companion.

**Engine** (`Base/darkplaces/gl_rmain.c`):
- `Q3DEFORM_AUTOSPRITE` (line 8142): per 4-vertex quad — compute the quad's **own center**, express each
  corner offset in the quad's original tangent frame, rebuild the corner on the **view's right/up axes**.
  A full screen-plane billboard pivoting at the quad center.
- `Q3DEFORM_AUTOSPRITE2` (line 8186): per quad — find the two shortest edges; the long axis runs between
  their midpoints. Each vertex **keeps its offset along that axis**; only the width component is re-aimed at
  the viewer (`newright = up × (center→camera)`). An axial/cylindrical billboard: the streak stays stretched
  along the flight line and only rolls about it.
- `tcmod page w h delay` (line 6494): UV translate by `((idx % w)/w, (idx/w)/h)` where
  `idx = floor(fract(time/(delay·w·h)) · w·h)`.

Net effect in Base: a round additive glow that reads identically from any angle + a tracer streak that
foreshortens when the bolt flies toward/away from you and shows full length side-on; the base lit by model
lighting with the `_glow` companion keeping it bright; the electro core crackles at 10 fps.

## How the port renders them today

- `ProjectileCatalog` (game/client/ProjectileCatalog.cs:163-168, 136-140): Blaster → `laser.mdl`, trailless,
  no light; ElectroBeam → `elaser.mdl` + TR_NEXUIZPLASMA trail + dlight. Classification verified
  (`blasterbolt`/`blaster` → Blaster; `electro_bolt` → ElectroBeam). **Identity parity is correct.**
- `ProjectileRenderer.OrientToVelocity` aims root +X down velocity — matches `vectoangles(velocity)`. ✓
- `Md3Morph.ApplyFrame` (game/loaders/models/Md3Builder.cs:369-436) emits the raw quad geometry with
  per-vertex `Coords.ToGodot` — the quads land in the **local XZ plane** (local Y ≈ 0 everywhere).
- Materials resolve through the real shader defs (`AssetSystem.ResolveMaterial` → `ShaderCompiler.Compile`):
  additive + **Unshaded** (the #38 fix — to be replaced for bolts, see correction above), and both deform
  types collapse to `BillboardMode.Enabled + KeepScale` (`ShaderCompiler.cs:793-803`).
- `tcmod page`: parsed (`TcModType.Page`) but `NeedsAnimatedShader` doesn't include it and `EmitTcMod`
  default-ignores it — and the static `StandardMaterial3D` path drops it entirely.

## Parity issues (ranked)

1. **[Root cause] Both deforms approximated by full billboard on geometry with zero local-Y extent** → bolts
   render edge-on: a thin additive smear that widens/narrows with view angle and collapses to a dot when
   fired straight away from the camera. This alone produces the reported "flattened" look for **both**
   weapons.
2. **`autosprite2` semantics lost** (even if #1 were patched by rotating the quads into the XY plane): the
   streak must stay aligned to the flight direction and pivot at the quad center ~10.5 qu behind the bolt.
   A full billboard pins it horizontal on screen and swings the offset around the node origin.
3. **`_glow` companions ignored / base wrongly unshaded** (the corrected #38 point): the bolts should be lit
   base + fullbright `_glow` emission, additive — matching DP. Today they are unshaded with no glow.
4. **`tcmod page` dead** → electro core+long frozen on page 0; no 10 fps crackle. (Blaster has no page anim.)
5. **Same-family collateral (pre-existing):** HLAC's `hlac_bullet.md3` is the identical two-quad autosprite
   pattern — flattened the same way today; fixed by this change. Crylink's Base model (`plasmatrail.mdl`,
   autosprite pattern) and Arc's (`ebomb.mdl`) aren't wired in the catalog at all (procedural glow sprites)
   — separate small gap the fix makes worth wiring later.
6. **Unaffected:** the electro SECONDARY orb (`ebomb.mdl`) is true 3-D geometry — which is why only the
   primaries look wrong.

## Recommended fix (planned)

**Implement the two deforms faithfully as a generated vertex shader, with per-quad center/axis baked into
custom vertex attributes by the MD3 builder.** GPU-side, zero per-frame C# work, and it fixes Blaster,
Electro primary, and HLAC in one change. The generated shader is also where the DP-faithful lit-base +
fullbright-`_glow` shading lives.

### Phase 1 — autosprite / autosprite2 geometry (the "flattened" bug)

**A. Bake quad frames in `Md3Morph.ApplyFrame`** when the surface's shader def carries an autosprite deform
(query `AssetSystem.GetShader(shaderName)?.Deforms`; the builder already receives `AssetSystem`):
- Process vertices in groups of 4 (these surfaces are exactly one quad; guard `vcount % 4 == 0`, else fall
  back to today's path).
- Per quad (converted Godot local space): `center` = mean of the 4 corners. For autosprite2, derive the long
  axis DP's way — two shortest edges (with DP's height tie-bias), axis = normalize(mid₁ − mid₀),
  right = normalize(shortest edge). For autosprite: right = normalize(v1−v0), up = normalize(cross(normal,
  right)).
- Per vertex: `s = dot(corner − center, right)`, `t = dot(corner − center, up/axis)`.
- Emit `CUSTOM0 = (center.xyz, s)` and `CUSTOM1 = (axis.xyz, t)` as RGBA-float custom arrays.

The pure math lives in a Godot-free helper (`Formats/Md3/AutospriteQuads.cs`) so it is unit-testable against
the real laser/elaser corner values.

**B. Compile a deform `ShaderMaterial` in `ShaderCompiler`** for defs with Autosprite/Autosprite2 (a new
opt-in `CompileAutosprite` + `AssetSystem.ResolveAutospriteMaterial` with its own cache, so the ordinary BSP
material path is untouched):

```glsl
shader_type spatial;
render_mode skip_vertex_transform, cull_disabled, blend_add;   // NOTE: not unshaded — DP lights the base

void vertex() {
    vec3 c = (MODELVIEW_MATRIX * vec4(CUSTOM0.xyz, 1.0)).xyz;   // quad center, view space
    float ms = length(MODEL_MATRIX[0].xyz);                     // uniform node scale (ModelScale)
    // -- autosprite (view-plane aligned): corner = center + s*view_right + t*view_up
    VERTEX = c + vec3(CUSTOM0.w, CUSTOM1.w, 0.0) * ms;
    // -- autosprite2 (axial): keep t along the flight axis, re-aim s at the camera
    // vec3 ax = normalize(mat3(MODELVIEW_MATRIX) * CUSTOM1.xyz);
    // vec3 fw = normalize(-c);
    // vec3 rt = cross(ax, fw); rt = length(rt) > 1e-5 ? normalize(rt) : vec3(1,0,0);
    // VERTEX = c + ax * (CUSTOM1.w * ms) + rt * (CUSTOM0.w * ms);
    NORMAL = vec3(0.0, 0.0, 1.0);   // camera-facing, for the lit base (view space)
}

void fragment() {
    vec2 uv = UV;              // ... tcMod stack emitted here (incl. page) ...
    vec4 c = texture(albedo_tex, uv);
    ALBEDO = c.rgb;            // LIT (rgbGen lightingDiffuse analogue — Godot lights ALBEDO)
    ALPHA  = c.a;
    EMISSION = texture(glow_tex, uv).rgb;   // _glow companion, fullbright add (DP color.rgb += glow)
}
```

Fragment reuses the existing tcMod/rgbGen emitters (so animation + rgbGen keep working). This matches DP's
math: axis components preserved per vertex, width component re-aimed, pivot at the quad center → the streak
keeps trailing behind the bolt; base lit + glow fullbright → correct brightness in both lit and dark rooms.

**C. No `ProjectileRenderer` changes** — root orientation already matches Base; the deform runs off
`MODELVIEW_MATRIX`. The procedural `GlowSprite` fallback stays for asset-less runs.

Rejected alternatives: CPU per-frame quad re-aim (per-bolt ArrayMesh rebuild every frame — alloc churn the
CPU campaign just removed); special-casing bolts as `Sprite3D`s in `ProjectileRenderer` (loses the streak's
axial behavior, duplicates material logic, fixes neither HLAC nor future autosprite content); rotating the
quads into the local XY plane and keeping `BillboardMode.Enabled` (fixes the core, still wrong for the
streak's axis-pinned semantics and off-center pivot).

### Phase 2 — `tcmod page` (electro crackle)

Add `TcModType.Page` to `NeedsAnimatedShader`'s animated list and emit DP's math (`gl_rmain.c:6494`):

```glsl
// tcMod page w h delay
float pf  = fract(TIME / (delay * w * h));
float idx = floor(pf * w * h);
uv += vec2(mod(idx, w) / w, floor(idx / w) / h);
```

Phase 1 already routes these defs through the generated-shader path, so electro core/long get flipbook +
deform in the same material.

### Phase 3 — verification + perf hygiene

- **Tests:** `AutospriteQuads` — bake laser.mdl / elaser.mdl-shaped corner data, assert center/s/t/axis.
  Shader-gen — autosprite def compiles to a ShaderMaterial using `skip_vertex_transform`, `CUSTOM0/1`,
  `blend_add` **without** `unshaded`, EMISSION from `glow_tex`; page tcmod emits the cycle math. Parser —
  `tcmod page` round-trips to `TcModType.Page`.
- **Manual:** `--host <map>`, fire both weapons — side-on = full streak along the flight line; firing away =
  core dominant, streak foreshortened; firing straight down = streak vertical on screen; base dims in dark
  rooms but the glow stays bright; electro core crackles. If the streak's bright end faces backward, flip the
  baked axis sign.
- **PSO warm:** the generated deform shaders are new pipelines. Ensure the model warm pass covers
  `laser.mdl` / `elaser.mdl` / `hlac_bullet.md3` so the first blaster/electro shot doesn't eat the compile
  hitch (`ProjectileRenderer.BuildWarmupInstances` currently warms only procedural bodies).
- Presentation-only change, but run `tools/perf-smoke.ps1` before merging per house rules.

---

## Implementation task breakdown (status: ALL DONE 2026-07-10; playtest pending)

1. ✅ Shared GLSL stage emitters (tcMod stack, rgbGen, waveforms, float fmt, alpha cutoff) moved into
   `Formats/Materials/Q3StageGlsl.cs`; `tcMod page` case added; `TcModType.Page` added to
   `NeedsAnimatedShader`. (ShaderCompiler delegates via thin aliases.)
2. ✅ `Formats/Md3/AutospriteQuads.cs` — pure per-quad center/axis/s/t math (autosprite UV-tangent frame
   with edge-based fallback; autosprite2 DP shortest-edge frame incl. the 1/1024 height tie-bias),
   Godot-free. One deviation from the sketch: the autosprite2 width axis is `cross(normal, axis)` rather
   than DP's raw shortest-edge direction — same line, sign pinned to the quad's front face so it agrees
   with the shader's per-frame `cross(axis, toQuad)`.
3. ✅ `AutospriteShaderGen` (Formats) + `ShaderCompiler.CompileAutosprite` (game) +
   `AssetSystem.ResolveAutospriteMaterial` (opt-in, own cache; null-caches non-autosprite names): deform
   vertex shader + lit-base/fullbright-`_glow` fragment (`ALPHA = 1.0` — GL_ONE GL_ONE under Godot's
   srcAlpha·ONE blend_add); albedo + `_glow` (1×1 black `AssetSystem.BlackTexture()` when missing) +
   fragment-stage instance colormod/glowmod.
4. ✅ `Md3Morph.ResolveSurfaces` detects the deform (quad-shaped surfaces only, `vcount % 4`) and swaps in
   the deform material; `ApplyFrame` bakes `CUSTOM0/1` (RgbaFloat) from the QUAKE-space morphed verts via
   `AutospriteQuads`, converting center/axis at the boundary.
5. ✅ `ProjectileRenderer.BuildWarmupInstances` now also builds the REAL model body per catalog type
   (deduped by path; null-safe without ModelFactory) — laser/elaser/hlac's new autosprite pipelines
   compile in the offscreen warm pass instead of on the first shot.
6. ✅ `AutospriteBoltTests` (10 tests: bake against laser core/streak-shaped corners, square-quad upright
   bias, multi-quad surfaces, non-quad rejection, shader-gen assertions, page math, parser round-trip);
   build + full suite green (2963); playtest-bugs #38 residual updated to point here.

# fx-effectinfo — parity spec

**Base refs:** `common/effects/effectinfo.qc` · `common/effects/effect.qh` · `common/effects/all.qc` · `common/effects/all.qh` · `common/effects/effectinfo.inc` (+ `effectinfo_*.inc`) · the shipped `data/xonotic-data.pk3dir/effectinfo.txt` · DarkPlaces engine `darkplaces/cl_particles.c` (`baselineparticleeffectinfo`, `CL_Particles_ParseEffectInfo`)
**Port refs:** `game/client/EffectInfo.cs` · `game/client/EffectInfoParticle.cs` · `src/VortexArena.Common/Gameplay/Effects/{Effect.cs,EffectsList.cs,EffectEmitter.cs}` · `game/client/EffectSystem.cs` · `game/client/particles/FaithfulParticleBackend.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview

`effectinfo` is Xonotic's **particle-effect registry / parameter catalog**: the named bank of
~800 layered emitter definitions (explosions, muzzleflashes, impacts, trails, blood, gibs,
sparks, teleport/item/CTF fx, vehicle/turret/monster fx, ambient fields) that every weapon,
item, gametype, monster, turret and vehicle references *by name* when it asks the engine to
play an effect. It has two halves that must not be confused:

1. **The named `EFFECT_*` registry** (`common/effects/all.qh` + `all.inc`) — a stable, hashed,
   ordered list mapping `EFFECT_ROCKET_EXPLODE` → effectinfo name `"rocket_explode"` + a
   trail flag. Server (`Send_Effect`) and client (`net_effect`) network effects *by registry
   id*, so the CL/SV content hash must agree. This is **shared/authority-adjacent**.
2. **The effectinfo.txt particle parameters** — the rich per-effect `type/color/size/alpha/
   time/velocityjitter/bounce/gravity/staintex/lightradius/...` blocks. These are parsed and
   consumed **client-side only** (presentation). In stock Xonotic they are *not* compiled into
   QC at runtime: the QC `effectinfo.qc`/`effectinfo.inc` (gated by `ENABLE_EFFECTINFO`) is only
   the `dumpeffectinfo` round-trip tool used to *regenerate* `effectinfo.txt`. The authoritative
   runtime parse lives in the **DarkPlaces engine** (`cl_particles.c CL_Particles_ParseEffectInfo`),
   which reads `effectinfo.txt` directly.

This unit covers (1) the registry + name→id mapping + the wire protocol, and (2) the
effectinfo.txt catalog: the file, its parser, the parsed data model and its defaults. It does
**not** re-audit the particle *simulation* (spawn/integration/collision math — that is the
`ParticleSim`/dual-particle work tracked in `planning/particles-dual-system.md` and
`ParticleParityTests`), nor the per-spawn *consumers* (damageeffects/gibs/casings selecting
which effect name to play, gentle-mode selection) which belong to their own units.

## Base algorithm (authoritative)

### Named effect registry  (`base_refs: common/effects/all.qh:EFFECT`, `all.qc`)
- **Definition:** `EFFECT(istrail, NAME, "effectinfo_realname")` enrolls into `REGISTRY(Effects, BITS(8))`
  (max 256). `EFFECT(0, Null, string_null)` is id 0. The list is sorted deterministically at boot
  and content-hashed (`REGISTRY_CHECK(Effects)`) so client and server agree.
- **Trail flag:** point effects spawn `count` particles at one origin (`pointparticles`); trail
  effects sweep particles between two points and ignore count (`trailparticles`).
- **Team variants:** CTF flag fx are per-team registry entries (`redflag_touch`, `red_pass`,
  `red_cap`, …) resolved by team number in the consumers.
- **Commented-out entries:** legacy per-team vaporizer/spawn/rocketminsta variants are commented
  out in `all.inc` (slated for removal post-0.9.0); only the neutral variants are registered.

### Effect emission + wire protocol  (`base_refs: common/effects/all.qc:Send_Effect`, `Net_Write_Effect`, `NET_HANDLE(net_effect)`)
- **Server:** `Send_Effect(eff, org, vel, cnt)` → `Send_Effect_Except` builds a transient
  `net_effect` entity and writes it to each real client. Guards: null effect → drop; point
  effect with `cnt==0` → drop.
- **Wire body** (`Net_Write_Effect`): `WriteRegistered(Effects)` id, then `WriteVector(location)`,
  an `extraflags` byte (`EFF_NET_VELOCITY=1`, `EFF_NET_COLOR_MIN=2`, `EFF_NET_COLOR_MAX=4`,
  `EFF_NET_COLOR_SAME=8`), then conditional velocity vector, colour-min triple
  (`rint(bound(0, 16*c, 255))` per component), colour-max triple, and finally (point effects
  only) the count byte. `EFF_NET_COLOR_SAME` is the min==max optimisation (send one colour).
- **Client read** (`net_effect` NET_HANDLE): reads it back, reconstructs colour (`/16`), then
  `WarpZone_TrailParticles_WithMultiplier` (trail) or `boxparticles` (point) with the resolved
  particle number.
- **`Send_Effect_`** (by string name): linear-scans the registry for the effectinfo name; if not
  found, falls back to engine `__pointparticles(_particleeffectnum(name), …)`.

### effectinfo.txt file + parse  (`base_refs: darkplaces/cl_particles.c:CL_Particles_ParseEffectInfo`; QC mirror common/effects/effectinfo.qc:effectinfo_read)
- **File:** auto-generated text bank. ~800 `effect <name>` blocks; multiple same-named blocks
  **layer** into one logical effect (e.g. `rocket_explode` = 8 blocks: fireball + smoke + sparks +
  dynamic light + decal …). Tokenised whitespace/newline; `//` line comments; quoted tokens.
- **Per-block keywords** (the full `EFFECTINFO_PARSER` set, `effectinfo.qc:4-140`):
  `type, blend, orientation, color, tex, size, sizeincrease, alpha, time, count, countabsolute,
  trailspacing, gravity, bounce, airfriction, liquidfriction, stretchfactor, velocitymultiplier,
  originoffset, relativeoriginoffset, velocityoffset, relativevelocityoffset, originjitter,
  velocityjitter, rotate, lightradius, lightradiusfade, lighttime, lightcolor, staincolor,
  staintex, stainsize, underwater, notunderwater` — plus engine-only keywords not in the QC
  parser but present in DP: `stainalpha, stainless, lightshadow, lightcubemapnum, lightcorona,
  forcenearest, blend`.
- **Baseline defaults** (`baselineparticleeffectinfo`, `cl_particles.c:201-277`) — every block
  starts from these, only the keywords present override:
  `type=pt_alphastatic, blend=PBLEND_ALPHA, orientation=PARTICLE_BILLBOARD,
  color={0xFFFFFF,0xFFFFFF}, tex={63,63}, size={1,1,0}, alpha={0,256,256}, time={16777216,16777216},
  gravity=0, bounce=0, airfriction=0, liquidfriction=0, stretchfactor=1, velocitymultiplier=0,
  all offsets/jitters=0, lightradiusstart=0, lightradiusfade=0, lighttime=16777216,
  lightcolor={1,1,1}, lightshadow=true, lightcorona={1,0.25},
  staincolor={0xFFFFFFFF,0xFFFFFFFF} (modding factor; 0x808080=neutral), staintex={-1,-1},
  stainalpha={1,1}, stainsize={2,2}, rotate={0,360,0,0}`.
- **Type → default blend/orientation** (DP `particletype[]` table): alphastatic→alpha/billboard,
  static→add/billboard, spark→add/spark, beam→add/beam, rain→add/spark,
  raindecal→add/oriented-doublesided, snow→add/billboard, bubble→add/billboard,
  blood→invmod/billboard (and **gravity forced to 1**), smoke→add/billboard, decal→invmod/oriented.
- **Gentle mode:** there is no separate gentle `effectinfo.txt`; the gentle blocks
  (`effectinfo_gentle*.inc`) define *additional* named effects (e.g. happy-face death fx), and the
  **consumers** (`damageeffects.qc`, `gibs.qc`, gated by `cl_gentle`/`cl_gentle_gibs`/`sv_gentle`)
  choose which named effect to play. Selection is out of this unit's scope; the catalog just
  needs to contain those names.
- **Color/tex hex:** `color` & `staincolor` are `0xRRGGBB` strings; `tex 0 8` means atlas cells
  0..7 (second index exclusive). The atlas is `particles/particlefont.tga`.

### Reload  (`base_refs: cl_particles.c`)
- `cl_particles_reloadeffects [file]` re-parses at runtime. `dumpeffectinfo` (QC) writes the file
  back from the QC registry.

## Port mapping

| Base feature | Port symbol | Notes |
|---|---|---|
| `EFFECT(...)` registry (all.qh/all.inc) | `EffectsList.RegisterAll` + `Effects`/`Effect` (`Common/Gameplay/Effects/`) | Complete ordered port of `all.inc`; same names, trail flags, team variants, commented-out entries omitted. Content-hashed (`Effects.Hash`). |
| `Send_Effect` / `Send_Effect_Except` | `EffectEmitter.Emit` (+ overloads) | Same null/count-0 guards. Server queues `EffectRequest`s into a swappable `IEffectSink`. |
| `Net_Write_Effect` wire body | `EffectNetProtocol.Encode` + `ExtraFlags`/`QuantizeColor` | Byte-exact: same EFF_NET_* bits, `rint(bound(0,16c,255))` quantise, count byte for point effects. |
| `Send_Effect_` (by name) | `EffectEmitter.EmitByEffectInfoName` | Same registry scan then engine-fallback (records request with null Effect). |
| `te_*` builtins | `EffectEmitter.Te*` helpers | Convenience wrappers mapping to registry names. |
| `effectinfo.txt` file | `Base/data/xonotic-data.pk3dir/effectinfo.txt` (9043 lines, 800 blocks) | Same shipped (violence/non-gentle) build. |
| `CL_Particles_ParseEffectInfo` | `EffectInfo.Parse` / `EffectInfo.Load` (`game/client/EffectInfo.cs`) | Faithful tokeniser; layers same-named blocks; case-insensitive lookup. |
| `particleeffectinfo_t` + baseline | `EffectInfoEmitter` (`game/client/EffectInfoParticle.cs`) | Field-for-field model; DP baseline defaults reproduced (see gaps for 2 stain-default mismatches). |
| keyword parse | `EffectInfo.ApplyKeyword` | All gameplay keywords handled; engine-only keywords (stainless/lightshadow/lightcubemapnum/lightcorona/forcenearest) accepted-but-ignored; unknown keywords silently ignored (DP warns). |
| type→blend/orientation table | `EffectInfo.DefaultsFor` | Matches DP `particletype[]`; blood gravity forced to 1. |
| catalog consumption | `EffectSystem.Spawn` → `LookupInfo` → `FaithfulParticleBackend` (mode 0 default) / `ModernParticleBackend` (mode 2) / heuristic `BuildFromInfo` fallback | The faithful backend consumes the full parsed field set. |

**Layer split:** the `Effect`/`EffectsList`/`EffectEmitter`/`EffectNetProtocol` half lives in
`VortexArena.Common` (shared/authority — server emits, wire protocol). The `EffectInfo`
parser + `EffectInfoEmitter` model + `EffectSystem` live in `game/client/` (presentation).

## Parity assessment

### Liveness (the live caller chain — verified)
- `ClientWorld` instantiates `EffectSystem` (`game/client/ClientWorld.cs:313`) and wires its
  `TextureLoader`/`VfsTextLoader` from the asset system (`:215-216`).
- `NetGame` wires `Effects.ModelLoader` and calls `_render.Effects.Warmup()` at map load
  (`game/net/NetGame.cs:941,948`), which calls `EnsureInfoLoaded` → `Info.Load()` → parses the
  mounted `effectinfo.txt`. The print `[EffectSystem] effectinfo: N effects` confirms the parse.
- Server gameplay (damage/ctf/items/weapons) emits via `EffectEmitter`; the net layer turns each
  request into `NetGame.OnEffect` / `OnEffectByName` → `Effects.Spawn(request)`
  (`NetGame.cs:375,379`), which calls `LookupInfo` → routes to the faithful backend.
- **Conclusion:** both halves are **live**. The catalog is parsed once per map load and consumed
  on every networked effect. `EffectsList.RegisterAll` is called from GameInit.

### Gaps (concrete)
1. **`staincolor` baseline default + semantics.** DP baseline `staincolor = {0xFFFFFFFF,0xFFFFFFFF}`
   and treats it as a **modding factor** on the particle's own colour (`0x808080` = neutral). The
   port's `EffectInfoEmitter.StainColor0/1` default to `0xFFFFFF` and are consumed as an **absolute**
   tint (`StainMidColor`). For blocks that omit `staincolor` and rely on the modding-factor
   default, the splat decal tint diverges. Minor (most stain-emitting blocks set staincolor
   explicitly); affects blood/scorch decal colour.
2. **`stainsize` baseline default.** DP baseline `stainsize = {2,2}`; port defaults
   `StainSizeMin/Max = 1`. A block that declares `staintex` but omits `stainsize` gets a
   half-size splat decal vs DP. **Not minor:** the shipped `effectinfo.txt` has 30 `staintex`
   blocks but only 8 `stainsize` lines, so ~22 stain-emitting blocks inherit the baseline and
   render at half size.
3. **`stainalpha` not parsed in the QC `EFFECTINFO_PARSER`** — it IS in DP's parser and the port
   handles it. The port is correct vs the engine; the QC mirror is simply incomplete (it can't
   round-trip stainalpha). Not a port defect — noted for completeness.
4. **`Lifetime()` render clamp (intended).** `EffectInfoEmitter.Lifetime()` clamps the derived
   per-particle life to `[0.05, 6]s` and uses the alpha-fade-vs-time *midpoint*, for the legacy
   heuristic `BuildFromInfo`/`QueryTrailSprite` path only. The default faithful backend computes
   per-particle life exactly (alpha/alphafade or time, whichever first). This is a deliberate
   approximation in the *fallback* path, not the live default path.
5. **Engine-only render keywords unmodelled** (intended divergence): `lightshadow`,
   `lightcubemapnum`, `lightcorona`, `forcenearest` are parsed-and-ignored — Godot has no analogue
   for particle coronas / per-light cubemaps / nearest-filtering. Cosmetic.
7. **`stainless` mis-handled (latent logic bug, NOT just cosmetic).** The port treats `stainless`
   as a no-op `break`, but in DP `stainless` actively **disables** the stain: it sets
   `staintex[0] = -2` and resets staincolor/stainalpha/stainsize. A block that uses `stainless` to
   suppress a stain inherited from the baseline would still stain in the port. Harmless today —
   the shipped `effectinfo.txt` contains **0** `stainless` lines — so the port's keyword `logic`
   is downgraded to `partial` rather than the file producing a visible defect.
6. **No unit test for the parser.** `ParticleParityTests`/`ParticleAnalyticTrace` cover the
   `ParticleSim` integration math, not `EffectInfo.Parse` / the keyword table / the baseline
   defaults. Parser fidelity is verified by reading only (confidence medium).

### Intended divergences
- `cl_particles_modern` dual-backend routing (port-specific architecture): the same parsed
  catalog feeds a faithful CPU backend (mode 0, default — the parity target) or a modern GPU
  backend (mode 2). The catalog/parse layer is mode-agnostic; routing is per `planning/particles-dual-system.md`.
- Unknown/engine-only keywords silently ignored rather than warned.
- Stain decals routed to the `Decals` sprite system (DP uses lightmap stainmaps + `R_Stain`).

## Verification
- **Registry/wire:** code read — `EffectsList.cs` is a complete ordered port of `all.inc`;
  `EffectNetProtocol.Encode` matches `Net_Write_Effect` byte-for-byte (EFF_NET_* bits, colour
  quantise, count byte). **The wire protocol is fully live and bidirectional:**
  `ServerNet.WriteEffect` calls `EffectNetProtocol.Encode` (live `EffectNetSink`, with per-recipient
  `Except` exclusion in `FlushEffects`), and `ClientNet.DecodeEffect` is the exact inverse
  (the `net_effect` NET_HANDLE) → `EffectReceived` → `NetGame.OnEffectReceived` → `_render.OnEffect`.
  Only a dedicated encode/decode round-trip *test* is missing.
- **File present:** `Base/data/xonotic-data.pk3dir/effectinfo.txt` = 9043 lines / 800 `effect`
  blocks; types histogram (smoke 242, spark 181, static 133, alphastatic 77, bubble 52, decal 51,
  blood 24, beam 22, snow 12) confirms it is the standard violence build. All key weapon/item/CTF
  effect names present.
- **Baseline defaults:** diffed `EffectInfoEmitter` field defaults against
  `baselineparticleeffectinfo` (`cl_particles.c:201-277`) — match except `staincolor` default
  (0xFFFFFFFF modding-factor vs 0xFFFFFF) and `stainsize` default ({2,2} vs {1,1}). `rotate`
  default `{0,360,0,0}` confirmed matching.
- **Liveness:** caller chain traced ClientWorld→NetGame.Warmup→EnsureInfoLoaded→Info.Load and
  EffectEmitter→NetGame.OnEffect→Effects.Spawn→LookupInfo→FaithfulParticleBackend.
- **Keyword coverage:** `ApplyKeyword` switch vs `EFFECTINFO_PARSER` + DP parser — all gameplay
  keywords present; field consumption confirmed in `FaithfulParticleBackend`.

## Open questions
- Does the host ever mount a port-specific `effectinfo_xg.txt` overlay (style registry) that
  could shadow base parameters? `EffectStyleRegistry`/`EffectInfoOverlay` exist but the overlay
  affects *routing style*, not the parsed parameter values — confirmed not to alter base params,
  but not runtime-verified.
- The two stain-default mismatches (staincolor/stainsize) — do any shipped blocks actually rely
  on the omitted defaults, or do all stain-emitting blocks set them explicitly? Worth a grep over
  effectinfo.txt for `staintex` blocks lacking `staincolor`/`stainsize` to size the impact.
- Wire protocol has no round-trip unit test; a behavioral encode/decode test would upgrade
  confidence from read-only.

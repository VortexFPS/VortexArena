# Particle golden-trace reference

`particles_ref.c` is an **independent** C reference for the Darkplaces/Xonotic client CPU particle
simulation, used to generate the golden-trace corpus that
`tests/VortexArena.Tests/ParticleParityTests.cs` checks the C# port (`ParticleSim`) against
(planning/particles-dual-system.md §C, §C.5).

## Why a separate C reference?

A golden corpus is only meaningful if it comes from an implementation **independent** of the code it
validates. This file is transcribed line-for-line from the engine source the client actually runs:

```
Base/darkplaces/cl_particles.c
  CL_NewParticle                 (668-849)
  CL_NewParticlesFromEffectinfo  (1569-1788)   — the box/point (non-trail-only) path
  R_DrawParticles update loop     (2907-3132)
Base/darkplaces/mathlib.h
  lhrandom (48), VectorRandom (119)
Base/darkplaces/mathlib.c
  AnglesFromVectors / AngleVectors
```

It is deliberately **not** derived from the C# port. The particle math is single-precision (`vec3_t` is
float in DP); `lhrandom` is computed in double exactly as the macro does, so the two agree to within
last-ULP transcendental noise — and the chosen scenarios are arranged so the spawn basis evaluates to
exact integers (zero relative-offset / relative-velocity), making the traces effectively bit-identical.

### RNG is recorded, not reproduced

Darkplaces' particle math is driven entirely by libc `rand()` via `lhrandom`/`VectorRandom`. To make a
divergence *pure math* rather than RNG drift, the reference compiles against glibc, seeds `srand(1)` (the
default), and **records every `rand()` return value in call order** into each scenario's `"rng": [...]`
array. The C# test replays that exact integer stream through `RecordedParticleRng`, reducing it through the
identical `lhrandom`/`VectorRandom` formulas (`IParticleRng.cs`). `rec_rand()` is the recording wrapper;
`lhrandom` calls it, so spawn **and** update draws (e.g. the snow flutter timer) are all captured in order.

> Note on `ptype_t`: DP numbers `pt_dead == 0` as the free-slot sentinel, so `pt_alphastatic == 1`. The
> C reference keeps DP's numbering (liveness == `typeindex != 0`); the C# `ParticleType` enum has no `Dead`
> member (`AlphaStatic == 0`) and tracks liveness with an explicit `Active` flag. When the reference emits a
> block's `"type"` field it writes the DP value **minus 1** so it maps straight onto the C# enum.

### Collision is shared by construction

The analytic world is a handful of axis-aligned half-space brushes (a solid floor / a solid wall / a water
box over a solid floor). The swept-**point** trace (`clip_point_to_brush` / `world_trace` /
`world_pointcontents`) is reproduced **verbatim** in C# (`ParticleAnalyticWorld` in the test project) — a
point trace has no hull expansion, so plane distances are used as-is. Collision is therefore identical on
both sides and the test isolates the particle maths.

## Regenerating the corpus

Requires the WSL toolchain (gcc; see the repo's `wsl-*.sh`). From the repo root:

```bash
wsl -e bash -lc 'cd /mnt/c/Users/Bryan/Projects/Xonotic/VortexArena/.claude/worktrees/trusting-borg-0c888d/tools/particles-ref \
  && gcc -O2 -std=c11 -o particles_ref particles_ref.c -lm \
  && ./particles_ref ../../tests/VortexArena.Tests/golden/particles'
```

(adjust the worktree path as needed). Then run `dotnet test --filter ParticleParityTests`. The JSON
fixtures in `tests/VortexArena.Tests/golden/particles/` are committed, so the tests run without the C
toolchain; regenerate only when the reference or scenarios change.

## Scenarios

| name | exercises |
|------|-----------|
| `jittered_burst` | point burst, origin+velocity ball jitter, air friction, no gravity/collision (must-pass) |
| `gravity_spark_bounce_plane` | sparks with gravity bouncing off a floor plane, spark slow-kill |
| `liquid_pool` | bubbles inside a water box: liquid friction + buoyancy, bubble-leaves-water kill |
| `trail_sweep` | smoke trail along a segment (trailspacing, relative origin offset) (must-pass) |
| `snow` | snow with the flutter timer (`rand()&3` + the DP `vel.x`-into-`vel.y` bug) (must-pass) |
| `blood_vs_wall` | blood thrown +x into a solid wall, killed on solid impact |

The must-pass set (`jittered_burst`, `snow`, `trail_sweep`) plus the distribution / accumulator-drain / perf
sanity tests in `ParticleParityTests` cover the no-collision archetypes; the collision archetypes are the
remaining three.

## Scope / fidelity notes

* The reference models the **box/point** spawn path of `CL_NewParticlesFromEffectinfo` (the
  `else` branch, 1694-1782). The `pt_decal` and `PARTICLE_HBEAM` early-paths and the dlight allocation are
  out of scope (they spawn no simulated particle / consume no extra `rand()` on this path).
* Stains, decals, dlights, and the rain-splash *visuals* are renderer concerns; the core sim raises them via
  the `ParticleSim.OnStain` hook and they do not affect org/vel/size/alpha. The rain `pt_rain → pt_spark`
  conversion and its delayed splash **sub-particles** are reproduced (they consume `rand()`), so the stream
  stays aligned, but rain is not in the committed collision scenarios.
* `sRGB3D` color conversion is off (matches a default client), so the recorded colors are the raw byte-lerp.

# Xonotic ↔ VortexArena Parity System

This directory is the **single source of truth** for how faithfully the Godot/C# port
reproduces the original Xonotic gameplay and presentation, and where it falls short.

It exists so that "find what's missing or wrong vs. the original" is a **repeatable,
machine-checkable process**, not a one-off audit. Each prior ad-hoc audit
(`HUD_PARITY_CONTRACT.md`, `weapon-effects-parity.md`, `warpzone-base-vs-port-audit-*.md`,
`graphics-settings-audit-*.md`) folds into this structure.

## Ground truth

- **Reference (the spec):** original Xonotic QuakeC at `../Base/data/xonotic-data.pk3dir/qcsrc`
- **Pinned reference revision:** `v0.8.6-1779-g863cd3e84` (from `csprogs-xonotic-v0.8.6-1779-g863cd3e84.pk3`)
  — this is the **data submodule's** checkout; the outer `../Base` umbrella repo describes as
  `xonotic-v0.8.6-151-*`, which is a *different repo's* numbering. Never derive the pin from `git describe`.
- **Subject under test:** this repo — gameplay in `src/VortexArena.Common/Gameplay/**` and
  `src/VortexArena.Server/**`; presentation/client in `game/**` and `src/VortexArena.Engine/**`.

The QuakeC is authoritative. When the port disagrees with Base, the port is wrong **unless**
the row is explicitly marked `intended_divergence: true` with a rationale.

**The spec is wider than qcsrc** (amended 2026-07-02 — see
[STRATEGY-REVIEW-2026-07-02.md](STRATEGY-REVIEW-2026-07-02.md)): original-Xonotic behavior also
comes from the shipped **data layer** (~170 cfg files, effectinfo, assets) and the **DarkPlaces
engine**. The registry units cover the QC; the data layer is checked *mechanically* by the
coverage/differ tools below (`COVERAGE.md`, `CVAR-DIFF.md`, `ASSET-CHECK.md`), and engine-behavior
divergences are recorded where consumed (unit rows + `intended_divergence`). Real shipped bugs have
come from each of these layers — none of them is out of scope.

## Layout

| Path | What |
|---|---|
| `SCHEMA.md` | The exact YAML row schema + status/dimension vocabulary. **Read before editing any registry file.** |
| `SPEC-TEMPLATE.md` | Template for a per-unit algorithm spec doc. |
| `registry/<unit>.yaml` | Machine-readable parity rows for one subsystem unit (the source of truth). |
| `specs/<unit>.md` | Human-readable algorithm/behavior spec for that unit (the "how it works"). |
| `INDEX.md` | Generated: full feature list + per-dimension status, one line per feature. |
| `PARITY-GAPS.md` | Generated: gaps ranked by gameplay impact. The actionable backlog. |
| `NEEDS-INGAME-CHECK.md` | Generated: rows whose presentation/liveness code-reading couldn't confirm — the manual in-game verify queue. |
| `PORTING-PLAN.md` | Generated: the **wave-based execution plan** for closing the gaps (how to do the work, in parallel). See below. |
| `REMAINING-WAVES.md` | Live wave tracker: which waves are done and the remaining final waves (16–17 + verification). |
| `UNPORTABLE-ANALYSIS.md` | Reclassifies the 357 `unportable`-flagged gaps: truly-unportable (~0) vs decision vs difficult vs verify-only, with options + a recommendation per cluster. |
| `EXECUTION-STRATEGY.md` | The reusable **plan→apply harness + model-tiering rule** every porting wave runs. Read before authoring a wave. |
| `STRATEGY-REVIEW-2026-07-02.md` | The four structural blind spots of the audit strategy + the improvement plan and its status. Read before changing the parity *process*. |
| `COVERAGE.md` | Generated (`tools/parity-coverage.py`): how much of Base qcsrc the registry actually cites — cited/excluded/deferred/**UNMAPPED** per directory + stale-citation lint. The completeness metric. |
| `coverage-scope.yaml` | Scope declarations for the coverage ledger: `exclude` (out of scope, rationale required) and `defer` (audit scheduled). |
| `CVAR-DIFF.md` | Generated (`tools/parity-cvar-diff.py`): Base cfg chain vs port cfg chain vs port code-literal defaults — value diffs, code-default mismatches, never-read cvars. |
| `cvar-diff-known.yaml` | Intended cvar divergences (fnmatch patterns) suppressed from CVAR-DIFF — the differ's `intended_divergence`. |
| `ASSET-CHECK.md` | Generated (`tools/parity-asset-check.py`): every literal asset path in port code resolved against the VFS mounts, with model-magic sniffing (catches missing files and unsupported `.mdl`). |
| `asset-check-known.yaml` | Accepted asset-ref suppressions (doc placeholders, virtual shader names). |

## The two workflows

1. **Build/refresh the map** (heavy, occasional) — fan out over Base subsystems, extract the
   exhaustive feature inventory + algorithms + constants, locate port counterparts, classify
   parity per dimension, adversarially verify "faithful"/"live" claims, write `registry/` + `specs/`.
2. **Continuous parity-diff** (`.claude/workflows/parity-diff`, the reusable one) — consumes the
   registry as the baseline, re-locates port code, re-checks liveness, diffs cvar/constant values
   vs Base, runs/observes flagged features, and reports **drift** (status regressions, new gaps,
   newly-fixed rows). Emits a delta report and updates the registry.

## Closing the gaps: the porting plan

[PORTING-PLAN.md](PORTING-PLAN.md) is the execution plan that turns `PARITY-GAPS.md` into actual
work, organized into **4 dependency-ordered waves** sized for maximum parallelism:

- **Wave 1 — shared seams.** The handful of cross-cutting capabilities (round-handler tick, projectile
  networking, shared weapon-fire FX/audio, input/impulse routing, the mod-icon feed, …) that live in
  *hot files* the rest of the work depends on. This is the only real bottleneck; the rest fans out from it.
- **Wave 2 — gameplay leaves.** Every gameplay unit's own-file gaps. One agent per file (~160), mutually
  independent once Wave 1 lands.
- **Wave 3 — presentation.** Client/HUD/FX/audio that renders the server state produced in Wave 2.
- **Wave 4 — verification.** Build, in-game checks, and a `parity-diff` re-baseline proving the gaps closed.

**How to follow it:**
1. Do the waves **in order**; inside a wave, run the rows **in parallel**.
2. **One agent owns one file** — never let two agents edit the same file in the same wave. The hot files
   are reserved to Wave 1 precisely so Waves 2–3 are conflict-free.
3. Each row names the **file** to edit and the **shard(s)** (`registry/<shard>.yaml` + `specs/<shard>.md`)
   that hold its exact gaps, Base symbols, and constants — the shard is the work order.
4. After each wave, run the wave's **exit criteria** (build + tests + `parity-diff`) before starting the next.
5. After a `parity-diff --update` shrinks the gap set, **regenerate the plan** so it always reflects what's left.

The plan is generated, not hand-maintained: `tools/parity-plan-deps.*` analysis feeds
`tools/parity-plan-gen.py`, which writes `PORTING-PLAN.md` (and persists `_plan-analysis.json`).

## Running it

```sh
# Regenerate INDEX.md, PARITY-GAPS.md, NEEDS-INGAME-CHECK.md from the registry shards:
python tools/parity-assemble.py

# Regenerate COVERAGE.md (+ _coverage.json): Base-file citation coverage + stale-citation lint.
# Run alongside parity-assemble after any registry change; drive UNMAPPED to 0 via audits or
# explicit coverage-scope.yaml entries:
python tools/parity-coverage.py

# Regenerate CVAR-DIFF.md: cfg-chain + code-default + never-read cvar diffs vs Base.
# Rerun after touching cfgs, Cvars tables, or Register defaults; triage findings into fixes or
# cvar-diff-known.yaml entries:
python tools/parity-cvar-diff.py

# Regenerate ASSET-CHECK.md: literal asset refs resolved against the VFS (missing / bad-format).
# Rerun after adding assets or asset-path code:
python tools/parity-asset-check.py

# Regenerate the wave-based PORTING-PLAN.md from the persisted dependency analysis:
python tools/parity-plan-gen.py

# Re-check parity and report drift (regressions/fixes/new gaps/unmapped Base features).
# Report only (does not touch the registry):
Workflow { name: "parity-diff", args: { scope: "all", mode: "diff", dateLabel: "2026-06-22" } }
# Re-audit one category or specific units, and rewrite the registry in place:
Workflow { name: "parity-diff", args: { scope: "weapon", mode: "update" } }
Workflow { name: "parity-diff", args: { scope: ["ctf", "freezetag"], mode: "diff" } }
```

`scope` = `"all"` | a `category` (e.g. `weapon`, `gametype`) | an array of unit ids. `mode` = `diff`
(report only) | `update` (rewrite the registry with current truth). Writes `DRIFT-<dateLabel>.md`.

## Why dimensions, not a single status

A feature can be logic-perfect yet visually broken (the CTF flag that doesn't rotate), or
present in the code yet **never called on the live path** (gibs that never spawn, a dead-wired
`ModelLoader`). A single done/not-done flag hides exactly the bugs we care about, so every
feature is scored independently on: **logic, values, timing, presentation, audio, liveness**.
See `SCHEMA.md`.

## Nine stale `port_refs` (recorded 2026-07-30, not fixed)

Found while repointing the registry after the Tier-1 rename. These paths do not resolve, and **did not
resolve before the rename either** — the rename only changed the prefix, not whether the file is there.
Six moved within `src/`; three are not under `src/` at all:

| cited as | actually |
| --- | --- |
| `VortexArena.Common/Gameplay/MinigameSessionManager.cs` | `…/Gameplay/Minigames/MinigameSessionManager.cs` |
| `VortexArena.Common/Gameplay/PlayerPhysics.cs` | `…/Physics/PlayerPhysics.cs` |
| `VortexArena.Common/Gameplay/RoundHandler.cs` | `…/Gameplay/GameTypes/RoundHandler.cs` |
| `VortexArena.Engine/Entity.cs` | `VortexArena.Common/Framework/Entity.cs` |
| `VortexArena.Server/GameScores.cs` | `VortexArena.Common/Gameplay/Scoring/GameScores.cs` |
| `VortexArena.Server/Mutators/SuperSpecMutator.cs` | `VortexArena.Common/Gameplay/Mutators/SuperSpecMutator.cs` |
| `VortexArena.Common/Gameplay/Damage/Combat.cs` | not in `src/` |
| `VortexArena.Common/Gameplay/Effects/HeroMaterials.cs` | not in `src/` |
| `VortexArena.Common/Gameplay/Waypoints/WaypointSprites.cs` | not in `src/` |

Left as-is deliberately: correcting a `port_refs` pointer means re-verifying that the row's recorded parity
status still holds against the code at the *new* location, and a pointer silently redirected without that
re-audit is worse than one that visibly fails to resolve. The three not under `src/` are the interesting
ones — they may have moved to `game/`, been renamed, or been folded into another file, and each answer
implies something different about the row above it.

Detect with:

```bash
git ls-files 'planning/parity/**' | xargs grep -ho 'src/VortexArena\.[A-Za-z]*/[A-Za-z0-9_/.-]*\.cs' \
  | sort -u | while read -r r; do [ -f "$r" ] || echo "$r"; done
```

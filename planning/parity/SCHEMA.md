# Parity Registry Schema

Every `registry/<unit>.yaml` file follows this schema **exactly**. The continuous parity-diff
workflow parses these files programmatically, so field names and the controlled vocabularies
below are a hard contract. When in doubt, copy the worked example at the bottom.

## File structure

```yaml
unit: ctf                              # kebab-case unit id; matches the file name
category: gametype                     # see CATEGORIES below
base_ref_rev: "v0.8.6-1779-g863cd3e84" # pinned Base reference revision
last_audited: "2026-06-22"             # ISO date this file was last (re)written
summary:                               # one-line-per-dimension rollup for the whole unit
  feature_count: 12
  worst_gaps: ["flag model never rotates/bobs at base", "..."]
features:
  - <feature row>                      # see FEATURE ROW below
```

## CATEGORIES (controlled)

`gametype` · `weapon` · `mutator` · `monster` · `turret` · `vehicle` · `item` ·
`physics` · `mapobject` · `server` · `client` · `effect` · `sound` · `notification` ·
`scoring` · `damage` · `bot` · `net` · `menu` · `data` · `misc`

- `menu` (added 2026-07-02): the menu system audited at **behavior level** — which dialogs/
  settings/screens exist and whether their bound cvars take effect. The QC widget toolkit
  internals (`menu/item/*`) are an `intended_divergence` (Godot Control replaces them), but
  the files are still cited so the coverage ledger counts them.
- `data` (added 2026-07-02): cross-cutting data-layer contracts (cfg default fidelity, asset
  reference resolution) surfaced by `tools/parity-cvar-diff.py` / `tools/parity-asset-check.py`.
  Confirmed per-feature mismatches should live in the owning gameplay unit; `data` units hold
  the contracts that belong to no single unit.

## FEATURE ROW

```yaml
- id: ctf.flag.idle_animation          # stable dotted id: <unit>.<area>.<feature>. NEVER reused/renamed.
  name: "Flag waving + rotation while at base"   # human label
  layer: [presentation]                # subset of: authority | shared | presentation
                                        #   authority   = server-side rules/state (Base sv_*.qc)
                                        #   shared       = code run identically client+server (Base shared .qc)
                                        #   presentation = client-only view/model/anim/effect (Base cl_*.qc / client/)
  base_refs:                            # WHERE in Base this lives. file path relative to qcsrc + symbol.
    - "common/gametypes/gametype/ctf/cl_ctf.qc:ctf_FlagSetup"
  port_refs:                            # WHERE in the port. [] (empty) means NOT IMPLEMENTED.
    - "src/VortexArena.Common/Gameplay/GameTypes/Ctf.cs:UpdateFlagVisual"
  constants:                            # exact Base values that must match. [] if none relevant.
    - { name: "rotate_speed_deg_per_s", base: "wave anim via frame", port: "none", match: false }
  status:                               # per-dimension. Use the STATUS vocab below. Use `na` when N/A.
    logic: faithful
    values: faithful
    timing: unknown
    presentation: missing
    audio: na
    liveness: live
  intended_divergence: false           # true ONLY for deliberate port-specific changes
  divergence_rationale: null           # required string when intended_divergence: true
  gaps:                                 # concrete, specific defects. [] when fully faithful.
    - "Flag entity is static at base: no waving frame animation, no yaw rotation."
  verification:                         # how the status was established. [] if unverified.
    - { kind: visual, ref: "spawn ctf map, observe flag at base", result: fail }
  confidence: high                      # high | medium | low  — how sure the auditor is
  notes: "Base drives the wave via the .md3 flag model's frame group; port loads model but never animates frames."
```

## STATUS vocabulary (per dimension)

Applied independently to `logic`, `values`, `timing`, `presentation`, `audio`:

| Value | Meaning |
|---|---|
| `faithful` | Matches Base behavior/values for this dimension. |
| `partial` | Implemented but diverges from Base in ways that are **not** intended. |
| `stub` | A placeholder/no-op exists (compiles, does nothing meaningful). |
| `missing` | Not implemented at all on this dimension. |
| `na` | Dimension does not apply (e.g. `audio: na` for a pure rules feature). |
| `unknown` | Not yet determined — needs deeper read or a behavioral check. |

`liveness` uses its own vocabulary:

| Value | Meaning |
|---|---|
| `live` | Invoked on the real gameplay path in a normal match. |
| `dead` | Code exists but **has no live caller** (the recurring port failure mode). |
| `partial` | Wired on some paths/modes but not others. |
| `na` | No code to be live (e.g. feature `missing`). |
| `unknown` | Reachability not yet traced. |

## Dimension definitions

- **logic** — the rules / state machine / decision flow (who scores, when a flag returns, hit
  detection branching). The "what happens".
- **values** — numeric constants and cvars: damage, cooldowns, speeds, radii, counts, defaults.
  A logic-faithful feature with wrong numbers is `logic: faithful, values: partial`.
- **timing** — tick cadence, durations, animation/respawn timers, dt handling. Frame-rate
  dependence vs Base. (See `planning/specs/determinism-and-physics.md`.)
- **presentation** — visible/model/animation/particle/UI fidelity. The CTF-flag-rotation class.
- **audio** — whether the correct sound API is actually called with the right cue.
- **liveness** — reachability on the live path. Existence ≠ wired.

## Rules

1. **`id` is permanent.** Renaming breaks drift tracking. Add a new id; never recycle an old one.
2. **`base_refs` is mandatory** for every non-`missing` feature. The Base symbol is the spec.
3. **Empty `port_refs` ⟺ the feature is `missing`/`stub`** on the implemented dimensions.
4. **`intended_divergence: true` requires a `divergence_rationale`.** This is how deliberate
   port changes (e.g. sound attenuation radius=2400, `cl_movement_errorcompensation=1`, particle
   clamps) avoid being re-flagged as bugs forever.
5. **Don't claim `faithful` or `live` you didn't verify.** Use `unknown` + `confidence: low`
   instead. The verify stage exists to upgrade these.

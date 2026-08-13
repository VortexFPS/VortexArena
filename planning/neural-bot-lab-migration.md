# VortexFPS/NeuralBotLab repository boundary

`NeuralBotLab` owns the reproducible training and evaluation control plane. `VortexArena` owns everything
that ships in the game or map editor. The observation/action/weight contract is versioned across both.

**Status: the split is done.** The control plane lives in
[VortexFPS/NeuralBotLab](https://github.com/VortexFPS/NeuralBotLab) (private), extracted with its history —
73 commits carried by `git-filter-repo` — and removed from this repository. Step 5 remains deliberately
open; the cross-contract CI test is not built yet. Both are recorded below.

## Remains in VortexArena

- Neural runtime integration: observation construction, action decoding, locomotion, policy loading and the
  live `NeuralBotService`.
- `NavField`, `NavFieldBaker`, `NavFieldIo`, distance fields, map features, cached navigation data, and all
  editor UI/actions that bake, inspect, validate, import or export that data. Navigation baking is a map
  authoring/export feature, not a trainer implementation detail.
- Runtime/schema/export compatibility tests, including the C# half of the layout contract.
- The C# headless host (`tools/neural/VortexArena.NeuralHost/`) and `TrainingEnv`, while they still
  reference internal Server/Formats APIs.

## Moved to NeuralBotLab

- Python policy, PPO trainer, environment client, experiment profiles and manifests.
- `vxstat` and the future `vxtrain run/status/stop/resume/bench` command surface.
- Benchmark orchestration (`worker.sh`), result schemas, analysis scripts and small metrics artifacts.
- Training documentation.

## Migration sequence

1. **Done.** `feature/neural-bot-lab` served as the history anchor; its work is merged to `main`.
2. **Done.** Protocol, weight-file and layout versions are explicit and enforced. Protocol version 2 adds a
   layout **descriptor** to `HELLO_ACK`: a canonical string of every section's name and width in vector
   order, derived independently on both sides (`NeuralLayoutDescriptor.Build()` and `layout.descriptor()`)
   and compared at handshake, with `verify()` naming the first structural difference.

   The descriptor exists because comparing sizes could not see the skew worth fearing: swap two equal-width
   sections and both ends still agree on 302 floats while disagreeing about what they mean. The same
   200-byte literal is asserted from both repositories by tests that never read the other's source, which
   is what makes the two notice each other now that neither builds the other.
3. **Done.** Extracted with history into `VortexFPS/NeuralBotLab` and deleted here. `vxstat` moved whole;
   no compatibility alias was retained, because nothing in this repository invoked it.
4. **Done, and it was the real work.** Nothing in the control plane may assume where it lives. Four
   fixed-depth paths had to go first — each of the form "count N directories up and you are at the repo
   root", each failing the same way: not by raising, but by returning a confidently wrong path.

   | Where | What it resolved |
   |---|---|
   | `va_neural/env.py` | the env host binary |
   | `train.py` (x3 call sites) | the map content root |
   | `worker.sh` | the host, the content root, and the fleet's own log directory |
   | `vxstat` | the runs directory |

   All four now resolve by explicit argument, then environment variable (`VX_NEURAL_HOST`, `VX_DATA_ROOT`,
   `VX_RUNS`), then a marker-file search that works at any depth and self-disables once the file leaves a
   VortexArena checkout. `vxstat` instead takes the nearest `runs/` that exists.

   Only the first was foreseen. The other three were found by performing the extraction into a scratch
   repository and reading what broke, which is worth remembering the next time something is split out.
5. **Open, deliberately.** The C# training host stays here until VortexArena exposes stable
   package/artifact APIs; `VortexArena.NeuralHost.csproj` still has `ProjectReference`s to
   `VortexArena.Server` and `VortexArena.Formats`. Nav-field baking and editor integration stay permanently.
6. **Done.** Checkpoints, exported policies and map packs are kept out of Git by `.gitignore` in both
   repositories rather than by habit — `runs/`, `*.pt`, `*.ckpt`, `*.vxpw`, plus `neural-host.json`, which
   holds a machine-specific path. Weights are published through releases/object storage with checksums.

## Still to build

**The cross-contract CI test.** VortexArena should export a known policy/schema fixture that NeuralBotLab
loads and drives against a pinned host. Until it exists, the guard is two independent assertions of the same
descriptor literal: a skew is still caught, but by whichever suite runs next rather than by one integrated
check, so it can be discovered later than it should be.

The licence remains GPL-3.0 with existing notices; `COPYING` and `GPL-3` were carried over unchanged.

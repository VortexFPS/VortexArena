# VortexFPS/NeuralBotLab repository boundary

`NeuralBotLab` owns the reproducible training and evaluation control plane. `VortexArena` owns everything
that ships in the game or map editor. The observation/action/weight contract is versioned across both.

## Remains in VortexArena

- Neural runtime integration: observation construction, action decoding, locomotion, policy loading and the
  live `NeuralBotService`.
- `NavField`, `NavFieldBaker`, `NavFieldIo`, distance fields, map features, cached navigation data, and all
  editor UI/actions that bake, inspect, validate, import or export that data. Navigation baking is a map
  authoring/export feature, not a trainer implementation detail.
- Runtime/schema/export compatibility tests.
- Initially, the C# headless host and `TrainingEnv`, while they still reference internal Server/Formats APIs.

## Moves to NeuralBotLab

- Python policy, PPO trainer, environment client, experiment profiles and manifests.
- `vxstat` and the future `vxtrain run/status/stop/resume/bench` command surface.
- Benchmark orchestration, sealed split manifests, result schemas, analysis notebooks/scripts and small
  metrics artifacts.
- Training documentation and CI smoke experiments.

## Migration sequence

1. Keep `feature/neural-bot-lab` as the history anchor for the current detached-worktree commits.
2. Make observation/action/protocol/weight schema versions explicit and test them in both languages.
3. Extract the Python/control-plane paths with history into `VortexFPS/NeuralBotLab`; retain `vxstat` as a
   compatibility alias.
4. Consume a pinned VortexArena headless-host build (or pinned VortexArena SHA) from NeuralBotLab. Do not
   replace the current project references with fragile sibling-directory references.
5. Move the C# training-only host/environment later, after VortexArena exposes stable package/artifact APIs.
   Nav-field baking and editor integration stay in VortexArena permanently.
6. Keep checkpoints and map packs out of Git. Store experiment manifests, hashes and compact metrics in Git;
   publish weights through releases/object storage with checksums.

Both repositories should run a cross-contract CI test: VortexArena exports a known policy/schema fixture and
NeuralBotLab loads/drives it against the pinned host. The likely license remains GPL-3.0 with existing notices.

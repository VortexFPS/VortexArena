# Neural bots — training

The learned locomotion policy for Vortex Arena bots. Design and rationale:
[`planning/neural-bots-2026-08-07.md`](../../planning/neural-bots-2026-08-07.md).

Nothing here ships. The game loads a weight file and evaluates it with its own 200-line MLP
([`PolicyNetwork.cs`](../../src/VortexArena.Server/Bot/Neural/PolicyNetwork.cs)); this directory is the
other half, and it needs Python only when someone is training.

## What is where

| Path | What it is |
|---|---|
| `VortexArena.NeuralHost/` | The environment host: a headless `GameWorld` full of bots, served over a localhost socket. Also the throughput bench and the weight verifier. |
| `va_neural/layout.py` | The observation and action layout, mirrored from C#. Checked against the host at handshake. |
| `va_neural/env.py` | Launches hosts, talks the protocol, vectorises across processes. |
| `va_neural/model.py` | The policy network, its distribution heads, and the exporter that writes the C# weight format. |
| `train.py` | PPO, the curriculum, checkpointing. |

## Setup

```bash
dotnet build tools/neural/VortexArena.NeuralHost -c Release
pip install torch numpy
```

## Measure before you train

The environment is the real game simulation, so throughput is a property of the machine and worth knowing
before committing to a run:

```bash
dotnet tools/neural/VortexArena.NeuralHost/bin/Release/net8.0/va-neural-host.dll --bench 4000 --agents 8
```

On the RTX 3080 dev box, stage 1: **70,589 agent-steps/s in one process, 490x real time** — that is 490
seconds of game world per wall second, with 8 players in it.

The full loop gets a fraction of that, and it is worth knowing which fraction before optimising:

| | game seconds per wall second | agent-steps/s |
|---|---|---|
| One host, no trainer | 490 | 70,589 |
| 8 hosts through the socket, no forward pass | 384 | 55,381 |
| 6 hosts + torch on CPU + the PPO update | 41 | ~6,600 |

## Where the time actually goes, and how to go faster

The trainer prints a phase breakdown per iteration: `env` waiting on the hosts, `pol` the rollout forward
pass, `upd` the PPO update. Two settings dominate everything else, and both were counter-intuitive.

### Cap torch's threads. This is the biggest single lever.

Torch defaults to one intra-op thread per core and spends them on tensors far too small to parallelise,
while competing with the env hosts for the same cores. Measured at 6 hosts x 64 agents on a 28-thread VM:

| torch threads | agent-steps/s | env / pol / upd |
|---|---|---|
| 28 (default) | 17,153 | 0.69 / 1.04 / 0.45 |
| **8** | **49,222** | 0.21 / 0.17 / 0.45 |

**2.9x from one setting**, and note the ENV phase improves most: the trainer had been starving the
simulation. 1 thread is too few, 4 and 14 both measure worse than 8.

An earlier pass on a different box compared only 1 thread against the default, found 1 slightly worse, and
concluded "leave it alone." The answer was in the middle the whole time. Two-point comparisons find the
wrong answer when the curve is not monotonic.

### Fat hosts, not many thin ones

Every step is a request/response round trip per host, and each is a scheduler wake-up. More agents per
host means more work per trip. Same 28-thread VM, torch=8 throughout:

| hosts x agents | total agents | agent-steps/s |
|---|---|---|
| 16 x 8 | 128 | 9,981 |
| 8 x 32 | 256 | 15,036 |
| 4 x 64 | 256 | 42,603 |
| 8 x 48 | 384 | 47,297 |
| **6 x 64** | **384** | **49,222** |

Note 4 x 64 and 8 x 32 are the same 256 agents and the same tensor shapes, 2.2x apart. That is contention,
not compute.

**`--hosts` is also the diversity knob.** Every agent in a host runs the same course, so 6 hosts means 6
distinct courses per batch. Do not trade it far down for throughput.

### Which box

The dev workstation returned anywhere from 17,688 to 70,589 agent-steps/s for identical work depending on
what else was running. The dedicated VM repeats within 2.5% (23,859 / 23,436 / 23,279). Tune on the quiet
box; treat numbers from a desktop as indicative only.

### If you run this in a VM

**Set the CPU type to `host` (or anything exposing AVX2).** Proxmox defaults to `kvm64`, which does not,
and torch silently falls back to unvectorised kernels. Nothing errors; the trainer is just severalfold
slower for no visible reason.

Sizing: RAM is not the constraint -- 6 x 64 agents on stage 1 sat at under 1 GiB, and stage 6 with all 29
maps cached per host reached 3 of 15 GiB. Disk wants room for the repo, the .NET SDK and the map packs; 40 G
is ample. No Godot and no display: the training stack is Godot-free by construction (ADR-0008).

**PPO minibatches: 4, not 8.** 8 gives 5,475 agent-steps/s; 4 gives 6,905 and stage 1 still solves (98.6%
sampled arrivals against 97.8%); 2 gives 8,191 but arrivals fall to 90.1%, because sixteen gradient steps
per update is too few.

## Train

```bash
# one stage
python tools/neural/train.py --stage 1 --steps 6000000 --hosts 6

# the whole curriculum, advancing on arrival rate
python tools/neural/train.py --curriculum --steps 8000000 --hosts 12

# resume
python tools/neural/train.py --resume runs/20260807-1200/checkpoint.pt --stage 4

# resume an existing policy into the speed-oriented, long-horizon curriculum
python tools/neural/train.py --training-profile speed-v2 \
  --resume runs/v26/checkpoint.pt --curriculum --steps 60000000 --name v27
```

`--hosts` is roughly "cores to spend"; each host is one process running one world. Every checkpoint writes
`policy.vxpw` beside it, ready to load.

Every run mirrors its output to `<run_dir>/train.log`, line-buffered. Read that to check on a run in
progress: piping the trainer's stdout through `grep` or `tee` buffers it until the process exits, so a
healthy long run looks identical to a hung one.

### Checkpoint compatibility and the speed-v2 profile

Version-1 checkpoints (including v26) remain loadable and are never edited in place. Resume into a new run
directory. For a legacy rolling checkpoint, the trainer recovers its stage steps, update, and best arrival
rate from that run's log; v26 therefore resumes at 44,151,422 stage steps rather than repeating its first
44 million. New checkpoints are atomic, keep `checkpoint.prev.pt`, and include optimiser/reward-scaler/RNG
state plus a schema-versioned progress record. SIGINT/SIGTERM asks the trainer to finish the current update,
write a checkpoint, and mark the run paused.

`--training-profile compatible` retains the historic learning horizon, fixed eval bank, single gate reading,
and arrival-only checkpoint selection. `--training-profile speed-v2` is weight-compatible and opts into:

- `gamma=0.999`, `gae_lambda=0.98`, so a fast terminal arrival can credit actions near the start of a long route;
- speed-aware checkpoint selection: first protect completion rate, then prefer a policy at least 3% faster
  within the conservative two-point arrival noise band;
- rotating evaluation route seeds and two consecutive passing evals before stage advancement;
- minimum curriculum stage budgets that are now enforced rather than merely documented;
- the learning-rate tournament and perturb-and-select disabled until they have a sealed validation bank
  that they cannot repeatedly optimise.

Every evaluation reports both completion rate and mean time among completed routes. Arrival remains the
first constraint—a bot cannot look fast by finishing only easy routes—but time now decides between policies
whose completion rates are statistically indistinguishable.

### The curriculum

Ordered because each stage's reward is only learnable once the previous one is. A stage that will not
converge usually means the stage before it did not really finish; reordering to get past it does not work.

| Stage | Course | What it teaches |
|---|---|---|
| 1 | Shipped-map short routes | Run and turn on real geometry |
| 2 | Shipped-map longer routes | Build and hold speed: bunnyhop, strafe-jump |
| 3 | Shipped-map complex routes | Jump timing, stairwells, doorways and landings |
| 4 | Shipped-map medium routes | Sustain movement through map furniture |
| 5 | Gaps wider than a jump, ledges higher than one | Weapon jumps |
| 6 | Full shipped-map distribution, minus a held-out split | Long-route retention and generalisation |

Stage 5 uses generated weapon-gap geometry because real maps cannot guarantee that lesson. The other stages
currently draw many origin/target pairs from shipped maps; rotating seeds prevent the selection loop from
turning one fixed route bank into training data. Stage 6 expands to the full non-held-out distribution.

Measured on a policy that
cleared stages 1 to 3: **97% arrivals on the corridor stage, 71% on terrain** against 22% and 3.5% for the
scripted runner, and **12.5% on real maps** — 3 routes of 8 on stormkeep where the classic steer finishes 7.

```bash
python tools/neural/train.py --stage 6 --steps 20000000 --hosts 8 --resume runs/latest/checkpoint.pt
```

`--maps A,B,C` narrows the pool; empty means every installed map. **The held-out set is removed either way**,
whatever the list says — `MapCourseSource.HeldOut` is `catharsis`, `fuse`, `afterslime`, and
`NeuralBotTests.MapCourseSource_RefusesHeldOutMapsEvenWhenAskedForThemByName` is the guard. Change that set
deliberately and record why; silently widening it is how a generalisation claim becomes untrue.

## Check the export

The weight format has two implementations in two languages that only meet at a binary file. A transposed
matrix produces a network that loads, runs, and is wrong. Run this after every export:

```bash
dotnet tools/neural/VortexArena.NeuralHost/bin/Release/net8.0/va-neural-host.dll --verify-weights runs/latest/policy.vxpw
```

It reports the shape, times a forward pass, and decodes a sample action. Measured for the shipping
architecture: **45,975 parameters, 21 microseconds per forward pass, 0.67% of one core for 16 bots at the
20 Hz think rate.**

## Does it actually beat the old bot

The only question that matters, and the answer has to come from maps the policy never trained on:

```bash
VA_NEURAL_WEIGHTS=runs/latest/policy.vxpw VA_TRIAL_MAPS="stormkeep,catharsis,fuse" \
  dotnet test tests/VortexArena.Tests --filter NeuralTimeTrialBench -l "console;verbosity=detailed"
```

Both arms run identical (map, origin, target) triples with identical seeds and the goal-rating layer
silenced, so the comparison is locomotion against locomotion.

Baseline to beat, stormkeep, 6 routes x 2 seeds: **classic steer finishes 7/8 at a 7.86 s median.**

## Scoring a checkpoint

Two different questions, two different tools.

**On the curriculum's own courses** — "did stage 4 teach it anything":

```bash
dotnet tools/neural/VortexArena.NeuralHost/bin/Release/net8.0/va-neural-host.dll   --bench 6000 --agents 8 --stage 3 --seed 101 --policy runs/latest/policy.vxpw
```

Compare against the same command with `--scripted` and with neither, on the same `--stage` and `--seed`.
A policy that cleared stages 1 to 3, measured that way:

| stage | policy | scripted forward | random |
|---|---|---|---|
| 1 flat | 97.2% | 98.7% | 12.5% |
| 2 corridor | **97.0%** | 22.0% | 0.0% |
| 3 terrain | **71.0%** | 3.5% | 0.0% |
| 6 real maps | 12.5% | — | — |

Stage 1 is where a straight line is already optimal, so matching the scripted arm is the ceiling. Stages 2
and 3 are where the policy is doing something the scripted arm cannot. Stage 6 is why stage 6 exists.

**On real maps against the classic steer** — the shipping question: the time trial, above.

## Three ways to measure, and only one of them counts

The trainer prints two arrival rates and a third exists. They disagree by up to 6x, so know which is which.

| what | how it decides | typical, stage 3, same weights |
|---|---|---|
| `sampled` | rollout actions, exploration noise alive | **11.9%** |
| external path, argmax | the trainer's socket path, argmax | **8.8%** |
| `shipped` | the game's own locomotor evaluating the weight file | **34.7%** |

`shipped` is what a live server does and the only one the curriculum gate reads. It is measured by shelling
out to `va-neural-host --bench --policy`, not in-process, because the trainer's external-action path is
measurably worse than the locomotor evaluating the network itself and gating on it would gate on a number
the shipped bot never experiences.

Two causes of that gap were found and fixed: the decision rate was skill-scaled rather than fixed (a policy
deciding at 34 Hz is a different and better policy than the same weights at 18 Hz — `bot_neural_hz` now
pins it, and the trainer sets it to its own step rate), and the observation was built inside the think
rather than at the step boundary. A residual gap remains and is not fully explained; bunnyhopping is
chaotic enough that small timing differences compound over a 50-second episode.

**`bot_neural_hz` must match the rate the policy trained at.** Changing it without retraining changes what
the network is.

## Sampled versus deterministic, which is a trap

The trainer prints two arrival rates and they disagree by a lot:

```
[s3 u 977] steps 6,002,688  reward +0.0710  sampled 11.9%  det 71.0%  ent 7.297
```

`sampled` is the rollout, taken with exploration noise alive (sigma 0.7 on the view deltas plus sampling
from six categorical heads). `det` is the argmax policy, which is **what gets exported and what runs in the
game**. The curriculum gate reads `det`; the first version read `sampled` and stalled on a stage the
deployable policy had already cleared, and no amount of further training would have moved it, because the
entropy bonus keeps the sampled policy noisy on purpose.

If you add your own gate or early-stop, gate on the deterministic number.

## Is it learning

The healthy signature, from a stage that converged:

```
[s2 u   1] steps      6,144  reward -0.0211  arrivals  0.0%  pi -0.0062  ent 7.777  kl 0.0026
[s2 u  20] steps    122,880  reward +0.2779  arrivals 67.9%  pi -0.0097  ent 7.351  kl 0.0061
[s2 u  40] steps    245,760  reward +0.2542  arrivals 95.3%  pi -0.0085  ent 7.216  kl 0.0066
```

Policy loss slightly negative, KL between 0.003 and 0.007, entropy falling steadily from 8.2, mean reward
crossing zero as arrivals climb. Three ways it goes wrong and what each looks like:

* **Entropy flat near 8.2** — the entropy coefficient is drowning the policy gradient.
* **KL negative** — impossible for a real KL; the stored action and its log-prob disagree.
* **Mean reward pinned at exactly -0.02 with 0% arrivals** — that is the time penalty and nothing else, so
  no action is having any effect. Check the environment before touching a hyperparameter:
  `--bench 4000 --scripted` should reach 90%+ arrivals on stage 1 at about +0.30/step.

## Play against it

```bash
./vx run -- --host stormkeep --bots 4 --cvar bot_neural 1 --cvar bot_neural_weights runs/latest/policy.vxpw
```

`bot_neural_status` in the console reports what loaded, what baked, and why the bots are on the classic
steer if they are.

## Things that will bite

**Layout skew.** Change `NeuralObservation.cs` and `layout.py` must follow. `layout.verify()` catches it at
handshake and `NeuralBotTests.ObservationLayoutMatchesPythonMirror` catches it at build. Both exist because
skew does not crash: the network keeps producing plausible actions from misread columns and the only
symptom is a policy that stops improving.

**Old weight files.** A weight file records its observation size; `NeuralBotService` refuses one that does
not match and says so. Retrain after a layout change, do not force it.

**Entropy coefficient.** The action space is six categorical heads plus two Gaussians, so summed entropy
starts near 8.2 nats where a single-head space would be near 2. At the usual 0.01 the entropy bonus
outweighed the policy gradient by 13x and the policy never left uniform. It is 0.002 here; scale it with
the number of heads if you add one.

**Anything the host writes to stdout.** The client reads exactly one line from that pipe (the port) and
then drains it on a daemon thread, because the GAME prints too: one `[bots] waypoints for ...` line per map
load, and a map load is every episode. Before the drain existed, the 64 KB pipe filled after about 24
episodes and the host blocked forever inside a write it could not complete. It presented as a hard crash at
a perfectly reproducible step number with no exception, no stderr and flat memory. If you add a new stdout
line to the host, the drain covers you; if you add a new pipe, remember this.

**Shaping.** The progress term is the plain difference `d - d'`, not the textbook discounted
`gamma*phi(s') - phi(s)`. The discounted form pays a stationary agent `d*(1-gamma)` per step, which at
1000 qu out is five times the time penalty, and the best available policy becomes standing still far from
the target. Measured: random actions scored +0.057/step under the discounted form and -0.023 under this one.

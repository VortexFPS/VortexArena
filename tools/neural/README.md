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

On the RTX 3080 dev box, stage 1: **34,000 agent-steps/s in one process, 235x real time.** Six host
processes plus the PPO update measure **8,200 agent-steps/s end to end**, so a 6M-step stage is about
twelve minutes.

## Train

```bash
# one stage
python tools/neural/train.py --stage 1 --steps 6000000 --hosts 6

# the whole curriculum, advancing on arrival rate
python tools/neural/train.py --curriculum --steps 8000000 --hosts 12

# resume
python tools/neural/train.py --resume runs/20260807-1200/checkpoint.pt --stage 4
```

`--hosts` is roughly "cores to spend"; each host is one process running one world. Every checkpoint writes
`policy.vxpw` beside it, ready to load.

### The curriculum

Ordered because each stage's reward is only learnable once the previous one is. A stage that will not
converge usually means the stage before it did not really finish; reordering to get past it does not work.

| Stage | Course | What it teaches |
|---|---|---|
| 1 | Flat room | Run and turn |
| 2 | Long bending corridor | Build and hold speed: bunnyhop, strafe-jump |
| 3 | Platforms, gaps, ramps | Jump timing and landing |
| 4 | Stage 3 plus jump pads, teleporters, hurt volumes | Route through map furniture |
| 5 | Gaps wider than a jump, ledges higher than one | Weapon jumps |

Stages 1 to 5 run on **generated** geometry, different every episode. The 32 shipped maps are held back for
stage 6 and the eval split, because a policy trained on maps we own has no pressure to generalise and a
training curve cannot tell you it failed.

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

**Shaping.** The progress term is the plain difference `d - d'`, not the textbook discounted
`gamma*phi(s') - phi(s)`. The discounted form pays a stationary agent `d*(1-gamma)` per step, which at
1000 qu out is five times the time penalty, and the best available policy becomes standing still far from
the target. Measured: random actions scored +0.057/step under the discounted form and -0.023 under this one.

# Neural bots — the environment host

What remains here is `VortexArena.NeuralHost/`: a console process that owns a headless `GameWorld` full of
bots and serves observation, action and reward over a localhost socket. It is also the throughput bench and
the weight verifier.

**The trainer lives in its own repository now:
[VortexFPS/NeuralBotLab](https://github.com/VortexFPS/NeuralBotLab).** The PPO trainer, the policy network,
the environment client, `vxstat` and the worker fleet script moved there with their history. Training
documentation moved with them; this file is a signpost, not a copy.

## Why the split falls here

| Stays in VortexArena | Moved to NeuralBotLab |
|---|---|
| Neural runtime: observation construction, action decoding, locomotion, policy loading, `NeuralBotService` | The PPO trainer, the policy, the environment client |
| `NavField` and its baking, distance fields, map features, and every editor action that bakes or inspects them | `vxstat`, `worker.sh`, benchmark orchestration |
| The C# environment host, here | Training documentation and CI smoke experiments |
| Runtime, schema and export compatibility tests | The Python half of the layout contract |

Navigation baking is a map authoring and export feature rather than a trainer implementation detail, so it
stays permanently. The host stays for now because it references the `Server` and `Formats` projects
directly; it moves only once those are consumable as packages
([`planning/neural-bot-lab-migration.md`](../../planning/neural-bot-lab-migration.md), step 5).

## The contract across the boundary

The observation and action layout is described by a canonical string that both sides derive from their own
constants and compare at the handshake — see
[`NeuralLayoutDescriptor.cs`](../../src/VortexArena.Server/Bot/Neural/NeuralLayoutDescriptor.cs).

The same literal is asserted from both repositories, by tests that never read the other's source:
`NeuralBotTests.LayoutDescriptorMatchesTheCrossLanguageContract` here, `tests/test_layout_descriptor.py`
there. **A layout change means updating both.** That is the mechanism, not an inconvenience — it is what
makes two repositories notice each other.

Sizes alone would not be enough: swap two equal-width sections and both ends still agree on 302 floats
while disagreeing about what the 302 numbers mean, with no crash and no symptom beyond a policy that stops
improving. The wire protocol and the weight file carry their own version numbers and refuse a mismatch
outright.

## Build and use

```bash
dotnet build tools/neural/VortexArena.NeuralHost -c Release
```

Measure the machine before committing to a run — the environment is the real game simulation, so throughput
is a property of the box:

```bash
dotnet tools/neural/VortexArena.NeuralHost/bin/Release/net8.0/va-neural-host.dll --bench 4000 --agents 8
```

The weight format has two implementations in two languages that only meet at a binary file, and a
transposed matrix produces a network that loads, runs, and is wrong. After every export:

```bash
dotnet tools/neural/VortexArena.NeuralHost/bin/Release/net8.0/va-neural-host.dll --verify-weights policy.vxpw
```

Point the trainer at this build with `VX_NEURAL_HOST`, or a `neural-host.json`; see the NeuralBotLab README.

## Playing against a trained policy

```bash
./vx run -- --host stormkeep --bots 4 --cvar bot_neural 1 --cvar bot_neural_weights /path/to/policy.vxpw
```

`bot_neural_status` reports what loaded, what baked, and why the bots are on the classic steer if they are.
In a local match, **F11** opens the Bot Movement Lab to rank and live-switch policies; **P** places or
retargets the HERE waypoint for a directed bot.

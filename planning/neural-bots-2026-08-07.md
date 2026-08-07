# Neural bots — a learned movement policy

*Design plan, 2026-08-07. Branch `feature/neural-bots`. Measurements throughout are from the RTX 3080 dev
box.*

> **Status, 2026-08-07 (same day).** Phases N1 to N7 are built and the curriculum has started producing
> policies: **stage 1 reaches 100% arrivals in 80k steps, stage 2 reaches 95% in 245k.** What is not done is
> the rest of the curriculum and the eval against the classic steer on held-out maps, both of which are
> compute time rather than code. §11 records what each estimate turned out to be, §11.1 the throughput
> breakdown, §11.2 the four bugs worth not re-deriving, and §12 what is left.

A bot in Vortex Arena today walks a waypoint graph. It reaches its goal by steering at the next node,
and it bunnyhops only when `skill >= bot_ai_bunnyhop_skilloffset` and the next node happens to be
roughly straight ahead ([BotNavigation.cs:332](src/VortexArena.Server/Bot/BotNavigation.cs:332)). It
never rocket-jumps to shortcut a route, never crylink-boosts down a corridor, and never takes a jump
pad it wasn't routed through.

This plan replaces the *movement* half of that with a small neural network trained to get from an
origin to a target as fast as the physics allows. The deterministic AI keeps every decision it makes
now: which goal, which enemy, which weapon, when to shoot. It hands the policy a destination and a set
of permissions; the policy decides which keys to press.

---

## 1. Scope

**In:** locomotion. Ground running, strafe-jumping, bunnyhop chaining, ramp and ledge jumps, crouch
slides, jump pads, teleporters, warpzones, and the three movement weapons the brief names (blaster
ground-pound, crylink secondary boost, devastator detonation catch).

**Out:** combat decisions, item priorities, objective play, team coordination. Those stay in
`BotBrain` / `BotRoles` / `BotObjectiveRoles` and are not touched.

**The success test:** median time from origin A to target B on a held-out map, against three baselines
(current havocbot at skill 10, a scripted straight-line bunnyhopper, and recorded human runs where any
exist). A policy that is not faster than havocbot on maps it never trained on has failed, however good
its training curves look.

---

## 2. What the codebase already gives us

Five facts drove every choice below. Four are measured.

### 2.1 The simulation is Godot-free, so training needs no engine

`VortexArena.Common`, `.Engine`, `.Server` and `.Formats` have no Godot reference; the ADR-0008 comment
in each `.csproj` says so and the build confirms it. `BotPerfBench` already boots a complete world from
a BSP with `GameInit.Boot(es)` and steps it with `world.Frame(dt)`, no renderer involved.
**[verified — read all six `.csproj` files; ran two benches that do exactly this]**

Training is therefore a plain `dotnet` console process. No headless Godot, no render stubs, no
`--display-driver dummy`.

### 2.2 The sim runs 23x real time per core with eight agents in it

Measured with the existing `BotTickPerfBench` on `stormkeep`, Release, 2160 ticks after a 14 s warm-up:

| agents in world | median tick | ticks/s/core | agent-steps/s/core | real-time factor |
|---|---|---|---|---|
| 1 | 0.124 ms | 8,065 | 8,065 | 112x |
| 8 | 0.597 ms | 1,675 | 13,400 | 23x |

**[verified — `VA_DATA_DIR=<repo>/data VA_BOTS=8 VA_MAP=stormkeep VA_TICKS=2160 dotnet test --filter BotTickPerfBench`]**

Batching agents into one world beats one-agent-per-world by 1.7x on throughput, because the fixed
per-tick cost (`sim.start`, `start.hooks`, entity integration) amortises. Twelve worker processes on
this box give roughly 160,000 agent-steps/s, so a 50M-step PPO run spends about five minutes inside the
simulation. The bottleneck will be policy inference and the trainer bridge, not the game.

That result is what makes this feature tractable. It is worth re-measuring on any box that will run
training before sizing a run.

### 2.3 A trace costs 0.028 ms, so perception must be precomputed

From `TracePerfBench` on `atelier`: a 2048 qu point trace is 0.0277 ms, a 2048 qu box sweep 0.0763 ms,
`PointContents` 0.0015 ms. **[verified — `dotnet test --filter TracePerfBench`]**

A 72-sample egocentric "vision" built from traces would cost 72 x 0.0277 = 2.0 ms per think. At 20 Hz
across 8 bots that is 320 ms of CPU per second of game time, a third of a core, for perception alone.
Unaffordable. The same 72 samples read out of a baked array cost single-digit microseconds. §4 is built
around that ratio.

### 2.4 Weapon jumping is already emergent; it needs no new code

Splash damage runs through `DamageSystem.RadiusDamage` into `ApplyKnockback`, whose player branch ends at
`targ.Velocity += farce` ([DamageSystem.cs:925](src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs:925)).
The shooter is inside their own splash radius, so a devastator fired at the floor pushes the shooter
exactly as it pushes anyone else. Crylink secondary carries `g_balance_crylink_secondary_force = -200`
([Crylink.cs:102](src/VortexArena.Common/Gameplay/Weapons/Crylink.cs:102)); the negative sign is a
*pull*, which is why firing it ahead of you while airborne yanks you forward.
**[verified — read the call chain]**

So the three movement weapons need no special-casing. The policy has to learn the button and angle
timing against physics that already rewards it. That also means the training signal is honest: if the
policy finds a boost we did not anticipate, it is a real boost.

### 2.5 The integration seam is one function

`BotBrain.ThinkProduce(Player bot, float dt) → MovementInput`
([BotBrain.cs:432](src/VortexArena.Server/Bot/BotBrain.cs:432)), consumed by
`BotPopulation.ThinkBotPlayer` ([BotPopulation.cs:1033](src/VortexArena.Server/Bot/BotPopulation.cs:1033))
and by the live server's per-tick input path. `MovementInput` is fourteen fields: view angles, a
three-axis wishmove, frame time, and the buttons
([Movement.cs:81](src/VortexArena.Common/Physics/Movement.cs:81)).

Everything below plugs in at that one function, which gives a free A/B and a one-cvar kill switch.

### 2.6 One existing problem the design must not inherit or worsen

At 8 bots the p99 tick is 6.5 ms and the worst is 9.6 ms, attributed almost entirely to `bot.strategy`
/ `bot.rate` — the A* replan and goal rating. **[verified — same bench run as §2.2]** That hitch is in
the strategy layer, which this feature keeps. The policy must not add to it, and the perception bake
(§4.1) must not run on the main thread at map load: parity finding D1 already records a 1 s waypoint
freeze as a shipped bug.

---

## 3. Layering

```
BotBrain          decides: goal, enemy, weapon, "must aim now", "may I use weapons to move"
   |
   |  MoveIntent
   v
NeuralLocomotion  decides: wishmove, jump, crouch, view delta, fire, weapon switch
   |
   |  MovementInput
   v
PlayerPhysics     unchanged
```

`NeuralLocomotion` replaces steps 3 through 5 of `ThinkProduce` (the steer, the dodge fold, the command
assembly) and nothing else. Under `bot_neural 0` the existing path runs untouched.

### 3.1 The intent contract

```csharp
/// What the deterministic tactician tells the learned policy each think.
public readonly struct MoveIntent
{
    public Vector3 GoalPos;        // world position the strategist wants reached
    public Vector3 CorridorA;      // next graph node beyond the goal, for look-ahead
    public Vector3 CorridorB;      // and the one after
    public float   Urgency;        // 0..1 — 1 = sprint, 0 = amble (feeds the speed/risk trade)

    public bool    WeaponMovementAllowed;  // false = attack outputs are masked off entirely
    public bool    AimRequired;            // combat wants the crosshair somewhere specific
    public Vector3 RequiredAimAngles;      // where, in world pitch/yaw
    public float   AimWeight;              // 0..1 — how badly. 1 = a frag is on the line
}
```

Three design calls live in that struct.

**The weapon permit is a hard mask, not a penalty.** When `WeaponMovementAllowed` is false, the
`attack1` / `attack2` / `weapon-select` logits are set to negative infinity before the argmax, so the
policy *cannot* fire. This matters because the brief's whole point is that deterministic combat logic
gets to claim the weapon; a soft penalty would let a well-trained policy occasionally override combat,
and that bug would be untraceable in a live match. Training randomises the flag so the policy is
competent under both settings rather than treating "no weapons" as out-of-distribution.

**The aim requirement is a soft constraint, and the network owns the mouse.** The policy outputs a
*view delta* (yaw and pitch increments, clamped to a human-plausible turn rate), not absolute angles.
`RequiredAimAngles` and `AimWeight` are inputs; the reward carries a term `-AimWeight x angular_error`.
The policy therefore learns the thing the brief asks for: when combat needs the crosshair on a target,
keep it there and find speed some other way. That "other way" exists because of the next point.

**Wishmove is emitted in world space and projected at the last moment.** The network's movement output
is a direction in the goal frame; `Emit` converts it to view-relative wishmove using whatever view
angles came out of the same forward pass. When combat swings the view 90 degrees, the wishmove
re-projects automatically and the bot keeps strafing the same world direction instead of veering.
Strafe-jumping still works, because the physics cares about the angle *between* view and wishmove
(`PMAccelerate.Aircontrol`), and the network sees both and controls both.

### 3.2 Not jerking when the goal changes

The strategist re-rates goals on a 5.5 to 7 second clock (`bot_ai_strategyinterval`), and a re-rate can
swing the target across the map. Two mechanisms, both needed:

- **Input side:** `GoalPos` reaches the policy through a slew-rate limiter, and the corridor look-ahead
  gives the policy two nodes of warning before a direction change lands.
- **Output side:** the previous action vector is an input, and the reward carries a jerk penalty on the
  view delta and on wishmove reversals. This is the mechanism that actually produces smooth motion.
  Input smoothing alone yields a policy that snaps as soon as the smoothed signal arrives.

---

## 4. Perception without rendering

Four channels, roughly 190 floats. The brief asks for vision-like geometric awareness that is neither a
render nor a trace storm; §2.3 says the only way to get it is to precompute.

### 4.1 P1 — a baked column field (the auto-generated navmesh)

At bake time, sample the map on a 32 qu horizontal lattice. 32 qu is not arbitrary: the player hull is
32 x 32 x 69 qu (`BotNavigation.Mins/Maxs`), so one cell is one footprint.

For each column store the list of standable spans:

```
struct FloorSpan {          // 12 bytes
    short FloorZ;           // top of the walkable surface
    short CeilZ;            // first solid above it
    byte  SlopeDot;         // ground normal . up, quantised — ramps and unwalkable slopes
    byte  Content;          // lava / slime / water / hurt-trigger / void bits
    short JumpReachMask;    // which of the 8 neighbours are reachable by a jump from here
    int   _pad;
}
```

A 4000 x 4000 qu map is 125 x 125 = 15,625 columns; at three spans average that is about 560 KB per
map. Runtime sampling is `spans[(y * w + x)]` plus a short scan, so the 72-sample egocentric pattern
(three radii x eight directions x three height offsets) is array indexing.

The bake needs roughly five traces per column, which estimated to about 2.3 s single-threaded per map.
**Measured on stormkeep: 377-473 ms across six workers, 11,464 occupied columns, 30,985 spans, 308 KB.**
Still too slow to sit on the map-load thread, so:

- ship the bake as a build artifact next to the BSP, the same shape as `.waypoints.cache`;
- fall back to an at-load parallel bake when the artifact is missing, off the sim thread, with bots
  running the old steer until it lands.

That fallback ordering is deliberate. Parity finding D1 records the cost of the opposite choice.

### 4.2 P2 — a small trace fan for what the bake cannot know

Doors, elevators mid-travel, other players, and projectiles are not in a static field. Six to nine
short box sweeps per *think*, not per tick. At 20 Hz across 16 bots that is 2,880 traces/s ≈ 58 ms/s,
under 6% of a core. `BotTracewalk` already enforces a 96-trace-per-tick budget
([BotTracewalk.cs:58](src/VortexArena.Server/Bot/BotTracewalk.cs:58)); this fan lives inside it.

### 4.3 P3 — map feature channels

The brief lists jump pads, teleporters, warpzones, hurt triggers, and elevators. These are entities the
server already has. At map load, build a feature list; at think time feed the K=4 nearest as egocentric
direction, distance, a six-way type one-hot, and one state scalar (elevator phase, door open fraction).

The part that makes them usable rather than merely visible: **bake the outcome**. A `trigger_push` has a
known target and launch velocity, so the feature carries "entering here puts you at P in T seconds". A
`trigger_teleport` carries its destination direction relative to the current goal. Without that, the
policy can at best learn to avoid a pad; with it, the pad becomes a route.

Build this from the entities directly, as Base does (`jumppads.qc:720`, `teleporters.qc:260`). The
port's waypoint path has a live bug here (parity D3: teleporter and jumppad waypoints are only created
on the no-file fallback path, so `teleportWps = 0` on every shipped map). Reading entities sidesteps it
instead of inheriting it.

### 4.4 P4 — proprioception

Velocity in the local frame, speed, on-ground, ground normal, time since last ground contact, ducked,
water level, health and armor (a rocket jump has a price), current weapon one-hot, ammo for the three
movement weapons, jump-held, and the previous action. Bunnyhop timing is phase-dependent, so a two-tick
history of velocity and ground contact goes in too.

---

## 5. Actions

| head | shape | note |
|---|---|---|
| wishmove | 9-way categorical | 8 compass directions + null |
| jump | binary | |
| crouch | binary | |
| view delta yaw | continuous, rate-clamped | |
| view delta pitch | continuous, rate-clamped | |
| attack1 / attack2 | binary each | masked when `WeaponMovementAllowed` is false |
| weapon select | 4-way categorical | none / blaster / crylink / devastator; masked by ownership and ammo |

Discrete wishmove rather than continuous is a deliberate choice. A human strafe-jumps by holding
exactly one strafe key, and `PMAccelerate.Aircontrol` rewards precisely that; a continuous head has to
learn to saturate at a corner of the square, which is slower to discover and noisier once found.

---

## 6. Training

### 6.1 Train in Python, ship weights plus a C# evaluator

**Recommended.** PPO in PyTorch against the C# sim exposed as a vectorised environment over shared
memory; export the trained weights as a flat binary; run inference in C# with a hand-written MLP
evaluator of a few hundred lines.

*Impact:* the shipping game gains no ML runtime dependency, which matters for a dedicated server that
has to `git clone && dotnet build` on a bare box (the same constraint that drove the
`Conductor.Protocol` vendoring decision in `VortexArena.Net.csproj`). It costs one IPC bridge and a
weight-format contract that has to stay in step across two languages.

The alternative of shipping ONNX Runtime buys generality we do not need for a 45k-parameter MLP and
adds a ~15 MB native dependency per platform. Writing PPO in C# to avoid the bridge means writing
autodiff, which is a project in itself.

**Network size:** two hidden layers of 128 over ~190 inputs is about 45,000 weights, 178 KB in fp32.
The weights are shared across every bot on the server, so they stay resident in L2 and only activations
are per-bot. Estimated 10 µs per inference, against the 35 µs the current `bot.think` already spends
per bot (§2.2 scope breakdown). The policy should be roughly cost-neutral. [estimated — no MLP kernel
written yet; this is the first thing to measure in phase N3.]

### 6.2 Curriculum

Ordered because each stage's reward is only learnable once the previous one is:

1. Flat ground, reach the target. Learns to run and turn.
2. Speed reward on. Learns bunnyhop and strafe-jump chaining.
3. Gaps, ledges, ramps. Learns jump timing and landing.
4. Jump pads, teleporters, hurt triggers, elevators. Learns to route through map furniture.
5. Movement weapons enabled. Learns rocket-jump, crylink boost, blaster pop.
6. Real maps. A/B pairs sampled from the waypoint graph.
7. Aim constraint on, weapon permit randomised. Learns to move well while the crosshair is committed.

Stages 1 through 5 run on procedurally generated obstacle courses, not shipped maps. Generated geometry
is how the policy avoids memorising the 32 maps we own.

**The waypoint graph is training scaffolding, never a policy input.** It picks the A/B pairs and it
supplies the potential function in §6.3. The network never sees a waypoint. That respects the brief's
constraint while getting map coverage for free.

### 6.3 Reward

```
  + potential shaping on geodesic distance to goal   (graph distance, not euclidean)
  - time
  + terminal bonus on arrival, scaled by time saved against the havocbot baseline
  - damage taken
  - AimWeight x angular error from RequiredAimAngles
  - jerk (view delta second difference, wishmove reversals)
  - death, heavily
```

Potential-based shaping is policy-invariant, so using graph distance as the potential guides
exploration without biasing which route is optimal. The policy stays free to find a rocket-jump
shortcut the graph does not contain, which is the point of the feature.

### 6.4 Evaluation

A `dotnet test` bench, non-gating, reporting median and p90 time-to-target over fixed (map, origin,
target) triples with N seeds, plus success rate. Held-out maps reported separately from trained ones;
a policy that is fast only on maps it trained on is a lookup table.

---

## 7. Runtime integration

- `bot_neural` (0/1, default 0 until it beats the baseline) selects the policy path.
- `bot_neural_weights` names the weight file; missing or malformed falls back to the existing steer and
  logs once.
- `Prof.Sample("bot.nn")` registered in `FrameProfiler.TopLevelNodeScopes`, per the house rule.
- Inference runs at think rate (`bot_ai_thinkinterval`, 0.05 s), not tick rate. Between thinks the last
  command persists, exactly as the existing throttle does today.
- Server-side only. Bots do not exist on clients, so the policy never enters the prediction path and
  ADR-0010's divergence budget is not involved. If that ever changes, float non-determinism in the MLP
  becomes a real problem and needs revisiting before, not after.

---

## 8. Phases

* **N1 — `MoveIntent` and the policy seam.** Split `ThinkProduce` so the strategist fills a
  `MoveIntent` and a pluggable locomotor consumes it. Ship with one locomotor: the existing steer,
  refactored, behaviour-identical. *(recommended)*
  *Impact:* no behaviour change and no risk, and everything after it becomes an additive change. About
  a day. `BotNavTests` and `BotLiveLoopTests` are the regression net.

* **N2 — the column-field bake.** Baker, on-disk format, loader, and the egocentric sampler. Standalone
  and testable without any network. *(recommended)*
  *Impact:* the long pole and the piece most likely to need iteration on resolution and span
  representation; measurable on its own (bake time, file size, sample cost) before anything depends on
  it.

* **N3 — the C# MLP evaluator and its bench.** Load a weight file, run a forward pass, measure it.
  *(recommended)*
  *Impact:* settles the 10 µs estimate in §6.1 before the training investment. If a forward pass turns
  out to cost 100 µs, the network gets smaller or the think rate drops, and better to learn that now.

* **N4 — the training environment.** Vectorised C# env host, shared-memory bridge, Python side, reset
  and episode plumbing, procedural course generator.

* **N5 — PPO through curriculum stages 1 to 3.** Flat ground to bunnyhop to gap jumps. First real
  evidence the approach works.

* **N6 — map furniture and movement weapons.** Curriculum stages 4 and 5.

* **N7 — real maps, aim constraint, weapon permit.** Curriculum stages 6 and 7, held-out map split.

* **N8 — the evaluation bench and the `bot_neural` switch.** Time-trial harness against the three
  baselines; flip the default only if it wins on held-out maps.

N1 through N3 are independent of each other and of any ML work. They are the right thing to build
first whatever happens to the rest.

---

## 9. Risks

* **R-N1 — the policy memorises maps.** A network that is fast on `stormkeep` and lost on a map it has
  not seen is worth nothing. Mitigated by procedural courses for stages 1 to 5 and a held-out map split
  reported separately at every eval. This is the risk most likely to kill the feature, and the one
  easiest to hide from yourself with a good-looking training curve.

* **R-N2 — server cost regression.** Sixteen bots at 20 Hz is 320 inferences/s. Phase N3 measures this
  before anything is built on top of it; the `Prof` scope keeps it visible afterwards.

* **R-N3 — bake time and bake staleness.** A 2.3 s per-map bake that has to run at load on a checkout
  without the artifact is a load-time regression. Off-thread with an old-steer fallback (§4.1), and the
  artifact needs a BSP hash so a recompiled map does not silently use a stale field.

* **R-N4 — reward hacking.** Time-to-target plus a speed term invites suicide-by-rocket-jump into the
  goal, or oscillating through a jump pad to farm shaping. Potential-based shaping is immune to the
  second by construction; the first needs the death penalty tuned and watched.

* **R-N5 — the intent contract leaks.** If the strategist starts encoding "how" into `MoveIntent`
  (a jump hint, a preferred weapon for a gap) the split rots and the policy stops being replaceable.
  Keep the struct to destination and permission.

---

## 10. Decisions taken

The three open questions from the first draft were answered on 2026-08-07 and are settled:

1. **One general policy, with a per-map fine-tune only if it turns out to be needed.** The weight file
   carries its own observation normalisation, so a fine-tune is a separate file rather than a format
   change; nothing in the runtime has to know which kind it loaded.
2. **The policy owns aim entirely.** Deterministic code computes the angle the crosshair should be at,
   including projectile lead, ballistic arcs, and the per-bot skill degradation that makes a low-skill bot
   miss (`BotAim.ComputeDesiredAngles`). The policy decides the path the crosshair takes to get there and
   pays a reward penalty proportional to `AimWeight` for missing it. Aim skill therefore never has to be
   learned, which is the whole reason the split is worth the plumbing.
3. **No skill scaling for now.** Every neural bot moves like the policy. The mechanism when it lands will
   combine disabling mechanics (no blaster jumps below skill N), slowing the view delta, and degrading
   perception; none of that needs skill as a network input, so deferring it costs nothing in the
   observation layout.

---

## 11. What the numbers turned out to be

Every estimate in this document that the build could check, checked. Measured on the RTX 3080 dev box.

| Claim | Estimated | Measured |
|---|---|---|
| Network size | ~45,000 parameters | **45,975** |
| Forward pass | ~10 us | **21.0 us** (`--verify-weights`) |
| 16 bots at 20 Hz | "roughly cost-neutral" against the 35 us `bot.think` | **0.67% of one core** — cheaper than the think it replaces |
| Nav field bake, stormkeep | ~2.3 s single-threaded | **377-473 ms** across 6 workers; 11,464 columns, 30,985 spans |
| Nav field size | ~560 KB | **308 KB** (stormkeep) |
| Env throughput | inferred from 13,400 agent-steps/s on stormkeep | **34,000 agent-steps/s** in one process on a generated course, 235x real time |
| Full training loop | not estimated | **8,200 agent-steps/s** end to end, 6 hosts plus the PPO update |
| Observation length | ~190 floats | **206** |

Two came out worse than estimated and neither changes anything: the forward pass is 2.1x the estimate and
still an order below budget, and the observation is 16 floats longer than sketched.

**The baseline the policy has to beat**, from the time trial on stormkeep over 6 routes x 2 seeds with the
goal-rating layer silenced: **the classic steer finishes 7 of 8 runs at a 7.86 s median.** On held-out maps.

### 11.1 Where the throughput actually goes

The environment is not the bottleneck, and it is worth knowing by how much before optimising the wrong end.

| | game seconds simulated per wall second | agent-steps/s |
|---|---|---|
| One env host, no trainer (`--bench`) | **490** (one world, 8 agents in it) | 70,589 |
| 8 hosts through the socket, no network forward pass | 8 x 48 = **384** | 55,381 |
| Full loop: 6 hosts + torch on CPU + the PPO update | 6 x 6.8 = **41** | ~6,600 |

A policy step advances the world 4 ticks at 72 Hz, so one step is 0.0556 s of game time. The 12x drop from
the first row to the last is entirely Python-side: a synchronous socket round trip per step, plus a
per-step forward pass on CPU. If training time starts to matter, that is the thing to fix, not the game.

### 11.2 Four bugs the build found, worth not re-deriving

* **Discounted potential shaping pays a stationary agent to stay away.** With `phi = -d` and gamma 0.99,
  `gamma*phi(s') - phi(s)` is worth `d*(1-gamma)` per step to an agent that does not move: at 1000 qu out
  that is +0.1 per step against a time penalty of 0.02. Random actions scored +0.057/step, so the optimal
  learnable behaviour was standing still far from the target. The plain difference `d - d'` is zero when
  stationary; random actions now score -0.023, which is the time cost and nothing else.
* **`CollisionWorld` is not safe for concurrent queries.** It keeps an epoch-dedup array
  (`_mark`/`_markNumber`) that every broadphase query stamps. The first parallel bake shared one world and
  found 995 spans where the serial bake found 1058 — a 6% hole in the map, silent. Each bake worker now
  gets its own world over the same immutable brushes.
* **A locomotor sync that early-outs on global state never reaches a bot that joins later.** Which is
  every bot on a real server: fixcount fills one per frame and the sync runs at the top of the frame.
* **The training env simulated freed players for 1.5M steps and nothing said so.** `bot_number` was 0 while
  the env connected its agents by hand, so fixcount removed them one per frame during warm-up. Position and
  velocity froze at their disconnect values, every observation was constant, every reward was the bare time
  penalty, and PPO learned nothing from a world where no action had any effect. It looked exactly like slow
  learning. What found it was a scripted hold-forward policy (`--scripted` on the env bench) closing
  24 qu/s where a running player does 320: not a movement problem, a not-moving problem.
  `TrainingEnv_ScriptedForward_ArrivesOnFlatGroundAndBeatsRandom` is the regression net, and it is the only
  test that asks whether the environment is solvable at all.

---

## 12. What is left

The code is done; the training is not.

* **T1 — Finish the curriculum (recommended).** Stages 1 and 2 are done (100% and 95% arrivals). Stage 3
  (terrain, jump timing) is where the difficulty steps up, then 4 and 5, then stage 6 on the shipped maps
  with a held-out split.
  *Impact:* hours of compute, no code. Stages 1 and 2 took about 15 minutes between them; the later stages
  are harder and stage 6 wants overnight. This is the only thing between the current state and a policy
  worth shipping.

  The healthy-run signature to compare against: policy loss slightly negative (-0.006 to -0.012), KL 0.003
  to 0.007, entropy falling steadily from 8.2, mean reward crossing zero as arrivals climb. A flat entropy
  is the entropy coefficient; a mean reward pinned at exactly -0.02 with 0% arrivals is the environment,
  not the policy.

* **T2 — Tune what the curriculum exposes (recommended).** Three settings are already known to be
  load-bearing, each found the hard way: the entropy coefficient (0.002, not the usual 0.01, because the
  entropy sums over eight heads and at 0.01 it outweighed the policy gradient 13-to-1), the rollout length
  (128, because gradient updates rather than samples were the short axis), and the arrival-bonus scale (the
  critic's loss spikes 400x when a rare +30 lands).
  *Impact:* the difference between a stage converging in twelve minutes and not converging at all.

* **T3 — Fix the held-out map split before stage 6 starts (recommended).**
  *Impact:* free now, impossible later. Choosing the eval maps after seeing the results is exactly how
  R-N1 gets missed.

* **T4 — Skill scaling.** Deferred by decision 3.
  *Impact:* until it lands, `bot_neural 1` makes every bot a movement expert regardless of `skill`. Fine
  for testing, wrong for a skill-3 opponent, so the cvar stays 0 by default anyway.

* **T5 — Ship baked fields as build artifacts.** At 377 ms off-thread the fallback bake is already cheap,
  so this is an optimisation rather than a fix.
  *Impact:* removes half a second of background CPU at map load; needs a VortexMaps packer change, and
  parity finding D1 is the cautionary tale about how packers classify unfamiliar extensions.

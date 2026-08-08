#!/usr/bin/env python3
"""PPO trainer for the Vortex Arena neural bots.

    python tools/neural/train.py --stage 1 --steps 2000000 --hosts 8
    python tools/neural/train.py --curriculum --steps 40000000 --hosts 12
    python tools/neural/train.py --resume runs/latest/checkpoint.pt --stage 4

Requires torch and numpy; the game side requires neither. Build the env host first:

    dotnet build tools/neural/VortexArena.NeuralHost -c Release

Design notes live in planning/neural-bots-2026-08-07.md. The two that matter when reading this file:

  * The environment runs the REAL game simulation, so a sample costs about 30 microseconds of CPU rather
    than the nanoseconds a toy env costs. Batch sizes are chosen accordingly: large rollouts, few epochs.
  * The curriculum is ordered because each stage's reward is only learnable once the previous one is. Do
    not reorder it to get past a stage that is not converging; a stage that will not converge means the
    stage before it did not really finish.
"""

from __future__ import annotations

import argparse
import copy
import json
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass, asdict
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    import torch
    import torch.nn as nn
except ImportError:
    sys.exit("torch is not installed:  pip install torch numpy")

from va_neural import layout
from va_neural.env import EnvConfig, VectorEnv
from va_neural.model import Policy, RunningNorm, export_weights


@dataclass
class Hyper:
    """PPO settings. Defaults are tuned for an expensive environment and a small network."""

    # 128 rather than a more usual 2048. The environment is the real game simulation, so a sample costs
    # about 30 microseconds and a run is bounded by wall clock, not by sample count. At 256 x 48 agents a
    # full 1.2M-step pass bought only 101 gradient updates and stage 1 was still at 3% arrivals; halving
    # the rollout doubles the updates for the same samples, which is the axis that was short.
    rollout: int = 128          # steps per host per update
    epochs: int = 4             # passes over each rollout; low, because samples are cheap to replace
    # 4, not the more usual 8 or 32. Measured on stage 1 with 6 hosts: 8 minibatches gives 5,475
    # agent-steps/s with the PPO update taking 0.42 s of a 1.06 s iteration; 4 gives 6,905 with the update
    # at 0.28 s, and stage 1 still solves (98.6% sampled arrivals against 97.8%). 2 is faster again at
    # 8,191 but the arrival rate drops to 90.1% -- sixteen gradient steps per update is too few.
    #
    # The network is 45,000 parameters, so on CPU each gradient step is dispatch-bound rather than
    # compute-bound and bigger batches are close to free.
    minibatches: int = 4
    lr: float = 3e-4
    # 0.0556 s per step, so 1/(1-gamma) = 200 steps is an ~11 s horizon -- against episodes of MaxSteps=900,
    # which are 50 s. The arrival bonus at the end of a long route is discounted by 0.995^900 = 0.011, so a
    # policy starting a 3000 qu course cannot see it at all and correctly optimises local progress instead.
    # This is the shape of the stage-6 plateau: arrivals fall off with route length (60.9% under 1000 qu,
    # 2.7% over 4000) exactly as a horizon shorter than the task predicts. See --gamma.
    gamma: float = 0.995
    gae_lambda: float = 0.95
    clip: float = 0.2
    value_coef: float = 0.5
    # 0.002, not the usual 0.01. The action space is SIX categorical heads plus two Gaussians, and the
    # entropy term sums over all of them, so it starts around 8.2 nats where a single-head space would be
    # near 2. At 0.01 the bonus contributed 0.082 to the loss against a policy loss of 0.006: measured over
    # 1.2M steps on stage 1, entropy never moved off uniform and arrivals crawled to 3%. Scale the
    # coefficient with the number of heads, not with habit.
    #
    # This value was tuned on stage 1 and does NOT survive the trip to stage 6. Stage 6 draws routes of
    # 700 qu to most of the width of an arena, so arrivals are rarer and the policy gradient is smaller,
    # while the entropy bonus is unchanged. Measured over 3.3M stage-6 steps: |policy loss| fell to 0.0076
    # against a bonus of 0.002 x 7.63 = 0.0153, entropy ROSE from 6.91 to 7.63 out of a 8.19 maximum, and
    # the eval sat between 3% and 9%. The policy was being pushed back toward uniform faster than the
    # reward pulled it off. See --entropy-coef, and watch the ent/pi ratio the update line now prints:
    # above about 1.0 the regulariser is steering and the reward is a passenger.
    #
    # Do not re-run the obvious experiment: it has been run. Three arms from one stage-6 checkpoint, same
    # seed, 2.5M steps each, coefficient spanning two orders of magnitude:
    #
    #     coef       e/p    entropy        sampled   shipped
    #     0.002      4.63   7.50 -> 7.85     2.1%      8.3%
    #     0.0003     0.70   7.49 -> 7.17     2.2%      8.3%
    #     0.00005    0.23   7.34             1.8%      5.6%
    #
    # The knob does exactly what it says -- entropy reverses direction -- and arrivals do not move. Rising
    # entropy was a real defect and worth fixing, but it was NOT what held stage 6 down. Keep the low
    # coefficient because a regulariser that overpowers the gradient is wrong on its own terms, and look
    # elsewhere for the plateau. kl running at 0.001-0.005 against target_kl 0.03 is the live lead: the
    # updates are an order of magnitude smaller than the trust region allows.
    entropy_coef: float = 0.002
    max_grad_norm: float = 0.5
    target_kl: float = 0.03     # stop the epoch loop early rather than let one update wreck the policy


# Stage, minimum steps, and the arrival rate that says it is done.
#
# Every gate is set against that stage's OWN measured baseline: a scripted straight-line runner on the same
# maps and the same route band, 192 agent-episodes each. The standard is roughly "double the dumb
# baseline", which is a claim about the policy rather than about whether a run happens to be passing.
#
#     stage   band                          scripted   gate
#       1     full pool,    700-1200 qu       50.9%     80%
#       2     full pool,   1200-2500          28.4%     60%
#       3     full pool,    700-1500          29.7%     60%
#       4     full pool,   1500-3000          22.9%     50%
#       5     GENERATED weapon gaps              --     45%
#       6     full pool,   uncapped           37.0%     55%
#
# Stages 1-2 re-measured 2026-08-08 after their four-simple-maps restriction was removed (it capped the
# distinct geometries per batch at 4 and held the policy below the baseline for 20M steps). The bands do
# not make each stage strictly harder than the last, deliberately: each stage ramps ONE axis.
CURRICULUM = [
    (1, 1_000_000, 0.80),   # full pool, short routes: run and turn on real geometry
    (2, 2_000_000, 0.60),   # full pool, longer routes: build and hold speed
    (3, 3_000_000, 0.60),   # full pool, short-but-complex: stairwells, doorways, railings
    (4, 4_000_000, 0.50),   # full pool, medium routes
    (5, 6_000_000, 0.45),   # generated weapon gaps: the one skill real maps cannot guarantee
    (6, 20_000_000, 0.55),  # full pool, uncapped: the real distribution
]


class _Tee:
    """Write to two streams at once, flushing each line. Used for the run log."""

    def __init__(self, a, b):
        self._a, self._b = a, b

    def write(self, text: str) -> int:
        self._a.write(text)
        self._a.flush()
        self._b.write(text)
        self._b.flush()
        return len(text)

    def flush(self) -> None:
        self._a.flush()
        self._b.flush()

    def isatty(self) -> bool:
        return False


def _tee(path: Path) -> None:
    handle = open(path, "a", encoding="utf-8", buffering=1)
    sys.stdout = _Tee(sys.stdout, handle)
    sys.stderr = _Tee(sys.stderr, handle)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--stage", type=int, default=1, help="curriculum stage 1-5")
    ap.add_argument("--curriculum", action="store_true", help="run every stage in order, advancing on arrival rate")
    ap.add_argument("--steps", type=int, default=2_000_000, help="total agent-steps (per stage with --curriculum)")
    # This help text was already correct and was ignored. Going 8 hosts -> 6 and 32 -> 64 agents for
    # throughput cut the distinct courses in a rollout from 8 to 6 while the agent count went up, and the
    # cost was measured only much later. Stage 1, constant 384 agents:
    #
    #     6 hosts x 64 agents    6 courses    sampled 27.1%   shipped 46.3%    9,623 steps/s
    #    12 hosts x 32 agents   12 courses    sampled 51.8%   shipped 66.2%   10,896 steps/s
    #    24 hosts x 16 agents   24 courses    sampled 96.2%   shipped 95.0%   22,260 steps/s
    #
    # Monotone, and 24 hosts clears the 90% gate that 6 hosts could not get halfway to. The eval draws
    # courses the policy never trained on, so a batch spanning few layouts overfits to them and the shipped
    # number is what pays for it.
    #
    # 24 hosts is also the FASTEST arm, which contradicts a throughput measurement taken earlier in the
    # same session that put it at 6,174 agent-steps/s. That measurement was made on an untrained policy,
    # where every episode runs to the 900-step cap; once agents actually arrive the episodes are short and
    # real throughput more than triples. Benchmarking env throughput against a policy that cannot finish an
    # episode measures the cap, not the environment.
    ap.add_argument("--hosts", type=int, default=24,
                    help="env host processes. Note each one is a distinct COURSE per batch, so this is the "
                         "diversity knob as well as the parallelism one -- do not drop it far for speed.")
    ap.add_argument("--agents", type=int, default=16,
                    help="agents per host. This trades against --hosts, and THROUGHPUT IS THE WRONG THING "
                         "TO TUNE IT ON: agents are parallelism, hosts are course diversity. At a constant "
                         "384 agents, 24x16 beat 6x64 96.2%% to 27.1%% sampled -- and was more than twice "
                         "as fast, because a policy that arrives ends its episodes early. Push --hosts up "
                         "first and raise this only once cores run out.")
    ap.add_argument("--ticks", type=int, default=4, help="sim ticks per policy step")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--out", type=Path, default=Path("runs"))
    ap.add_argument("--name", type=str, default=None)
    ap.add_argument("--resume", type=Path, default=None)
    ap.add_argument("--width", type=int, default=128)
    ap.add_argument("--torch-threads", type=int, default=8,
                    help="intra-op threads for torch. 8 is not a guess: torch defaults to one thread per "
                         "core and spends them starving the env hosts. At 6 hosts x 64 agents on a "
                         "28-thread box, the default (28) gave 17,153 agent-steps/s and 8 gave 49,222 -- "
                         "the env phase alone fell from 0.69s to 0.21s. 1 is too few, 4 and 14 are both "
                         "worse than 8. 0 leaves torch alone.")
    ap.add_argument("--entropy-coef", type=float, default=None,
                    help="override Hyper.entropy_coef. The default 0.002 was tuned on stage 1 and is too "
                         "strong once arrivals get rare: on stage 6 it made the entropy bonus twice the "
                         "policy gradient, entropy rose 6.91 -> 7.63 of a 8.19 maximum, and the eval never "
                         "left 3-9%%. Watch the e/p column -- keep it below about 1.0.")
    ap.add_argument("--lr", type=float, default=None,
                    help="override Hyper.lr. The default 3e-4 leaves the trust region almost unused: kl "
                         "runs 0.003-0.006 against target_kl 0.03, and entropy sits at 8.2 of a 8.19 "
                         "maximum, meaning the actor head has barely moved off its deliberately tiny "
                         "orthogonal(gain=0.01) initialisation. target_kl is the safety net -- raise this "
                         "until kl approaches it.")
    ap.add_argument("--log-std-max", type=float, default=None,
                    help="override Policy.LOG_STD_MAX, the cap on view-delta exploration noise. -1.386 is "
                         "sigma 0.25 of range (5.5 deg/step of yaw); the network initialises at -0.5, "
                         "which is 13.4 deg/step, so the default cap sits well below the starting point.")
    ap.add_argument("--gamma", type=float, default=None,
                    help="override Hyper.gamma. The default 0.995 is an 11 s horizon against 50 s episodes, "
                         "which discounts the arrival bonus to 0.011 at step 900. 0.999 makes the horizon "
                         "1000 steps, about the episode length.")
    ap.add_argument("--no-evolve", action="store_true",
                    help="disable the plateau perturb-and-select loop (G2): when the shipped eval makes no "
                         "new best for 4 evals, 6 noise-perturbed copies of the weights are scored through "
                         "the bench and the best is adopted if it beats the parent")
    ap.add_argument("--no-tournament", action="store_true",
                    help="disable the stage-entry lr tournament (G4): 3 branches at lr x0.5/x1/x2 run a "
                         "short probe budget from the same snapshot; the winner and ITS lr continue")
    ap.add_argument("--device", type=str, default="cpu",
                    help="cpu is usually right: the net is 45k parameters and the bottleneck is the env")
    ap.add_argument("--verbose-hosts", action="store_true", help="let the env hosts write to stderr")
    ap.add_argument("--eval-every", type=int, default=10,
                    help="updates between deterministic evals; the curriculum gate reads these, "
                         "not the sampled rollout rate")
    ap.add_argument("--eval-steps", type=int, default=400,
                    help="step cap per eval pass; the episode count is the real bound")
    ap.add_argument("--eval-episodes", type=int, default=40,
                    help="episodes per eval pass. Every eval scores the same courses, so this is what "
                         "makes two evals comparable")
    ap.add_argument("--data", type=Path, default=None,
                    help="content root for stage 6 (default: <repo>/data)")
    ap.add_argument("--maps", type=str, default="",
                    help="stage 6 map list, comma separated; empty means every installed map. "
                         "The held-out eval split is excluded either way.")
    args = ap.parse_args()

    run_name = args.name or time.strftime("%Y%m%d-%H%M%S")
    run_dir = args.out / run_name
    run_dir.mkdir(parents=True, exist_ok=True)

    # Mirror everything to <run_dir>/train.log, line-buffered.
    #
    # A run is hours long and the only way to see how it is going is to read its output. Piping stdout
    # through grep or tee buffers it until the process exits, so a run in progress looks like a run that
    # has printed nothing -- which is indistinguishable from a run that has hung. The log file always has
    # the current state.
    _tee(run_dir / "train.log")
    print(f"[train] run {run_dir}")

    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    device = torch.device(args.device)

    # Cap torch's intra-op threads. This is the single largest throughput lever measured on this project.
    #
    # Torch defaults to one thread per core and spends them on tensors far too small to parallelise, while
    # competing with the env host processes for the same cores -- and the hosts are the thing actually
    # producing samples. Measured at 6 hosts x 64 agents on a 28-thread box:
    #
    #     torch threads   agent-steps/s   env / pol / upd
    #     28 (default)          17,153     0.69 / 1.04 / 0.45
    #      8                    49,222     0.21 / 0.17 / 0.45
    #
    # A 2.9x difference from one setting, and note it is the ENV phase that improves most -- the trainer
    # was starving the simulation. An earlier test on a different box compared only 1 thread against the
    # default, found 1 slightly worse, and concluded "leave it alone". The answer was in the middle the
    # whole time; 4 and 14 both measure worse than 8 here.
    if args.torch_threads > 0:
        torch.set_num_threads(args.torch_threads)

    # Distribution argument validation runs on every construction, and the rollout builds seven
    # distributions per step. Nothing here feeds torch a malformed parameter; the checks are pure overhead.
    torch.distributions.Distribution.set_default_validate_args(False)

    policy = Policy(obs_size=layout.OBS_SIZE, width=args.width).to(device)
    norm = RunningNorm(layout.OBS_SIZE)
    hyper = Hyper()
    if args.entropy_coef is not None:
        hyper.entropy_coef = args.entropy_coef
    if args.gamma is not None:
        hyper.gamma = args.gamma
    if args.log_std_max is not None:
        Policy.LOG_STD_MAX = args.log_std_max
    if args.lr is not None:
        hyper.lr = args.lr
    optimizer = torch.optim.Adam(policy.parameters(), lr=hyper.lr, eps=1e-5)

    start_stage = args.stage
    if args.resume:
        ckpt = torch.load(args.resume, map_location=device)
        policy.load_state_dict(ckpt["policy"])
        optimizer.load_state_dict(ckpt["optimizer"])
        # load_state_dict restores the checkpoint's param_groups, INCLUDING its learning rate, which
        # silently overrides anything set on the command line. An --lr A/B run across a --resume would
        # otherwise compare three identical configurations and read as a cleanly rejected hypothesis.
        for group in optimizer.param_groups:
            group["lr"] = hyper.lr
        norm.load(ckpt["norm"])
        start_stage = ckpt.get("stage", args.stage)
        print(f"[train] resumed {args.resume} at stage {start_stage}, lr {hyper.lr:g}")

    plan = [(s, args.steps, thresh) for s, _, thresh in CURRICULUM if s >= start_stage] \
        if args.curriculum else [(args.stage, args.steps, 0.0)]

    (run_dir / "config.json").write_text(json.dumps({
        "args": {k: str(v) for k, v in vars(args).items()},
        "hyper": asdict(hyper),
        "obs_size": layout.OBS_SIZE,
        "action_logits": layout.ACTION_LOGITS,
    }, indent=2))

    for stage, budget, threshold in plan:
        print(f"\n[train] === stage {stage}: {budget:,} agent-steps, advance at {threshold:.0%} arrivals ===")

        # G4: a short lr tournament at stage entry. Each stage's reward scale differs (the return-scale
        # normaliser resets, the arrival rate changes by an order of magnitude), so the lr that served the
        # previous stage is a guess for this one. Three branches probe from the SAME snapshot; the winner
        # continues -- with its lr, which is the point. Bounded: ~500k steps per branch, and a branch is
        # scored by the same shipped-path bench the gate uses, not by its own rollout numbers.
        if args.curriculum and threshold > 0 and not args.no_tournament:
            snap_p = copy.deepcopy(policy.state_dict())
            snap_o = copy.deepcopy(optimizer.state_dict())
            snap_n = norm.state()
            probe_budget = 500_000
            results = []
            for b, mult in enumerate([0.5, 1.0, 2.0]):
                policy.load_state_dict(copy.deepcopy(snap_p))
                optimizer.load_state_dict(copy.deepcopy(snap_o))
                norm.load(copy.deepcopy(snap_n))
                for g in optimizer.param_groups:
                    g["lr"] = hyper.lr * mult
                torch.manual_seed(args.seed + stage * 613 + b)
                print(f"[tournament s{stage}] branch lr x{mult:g}: probing {probe_budget:,} steps")
                train_stage(policy, norm, optimizer, hyper, args, stage, probe_budget, 0.0, run_dir, device)
                r = evaluate(policy, norm, run_dir, stage, args.eval_steps, args)
                print(f"[tournament s{stage}] branch lr x{mult:g}: shipped {r:.1%}")
                results.append((r if r == r else -1.0, mult,
                                copy.deepcopy(policy.state_dict()),
                                copy.deepcopy(optimizer.state_dict()),
                                copy.deepcopy(norm.state())))
            r, mult, ps, po, pn = max(results, key=lambda x: x[0])
            policy.load_state_dict(ps)
            optimizer.load_state_dict(po)
            norm.load(pn)
            for g in optimizer.param_groups:
                g["lr"] = hyper.lr * mult
            print(f"[tournament s{stage}] winner lr x{mult:g} at {r:.1%}; continuing the stage with it")

        ok = train_stage(policy, norm, optimizer, hyper, args, stage, budget, threshold, run_dir, device)
        if not ok:
            print(f"[train] stage {stage} did not reach {threshold:.0%}; stopping rather than "
                  f"advancing onto a foundation that is not there")
            return 1

    print("[train] done")
    return 0


def train_stage(policy, norm, optimizer, hyper: Hyper, args, stage: int, budget: int,
                threshold: float, run_dir: Path, device) -> bool:
    cfg = EnvConfig(
        agents=args.agents,
        ticks_per_step=args.ticks,
        stage=stage,
        seed=args.seed + stage * 101,
        # Stage 1 and 2 are about locomotion; handing them weapons only adds an action dimension with no
        # reward attached to it. From stage 4 the permit starts flipping, which is what the live game does.
        weapon_chance=0.0 if stage <= 2 else 1.0,
        permit_flip_chance=0.0 if stage <= 3 else 0.35,
        aim_constraint_chance=0.0 if stage <= 2 else 0.4,
        data_root=str(args.data) if args.data else str(Path(__file__).resolve().parents[2] / "data"),
        map_list=args.maps,
    )
    env = VectorEnv(cfg, num_hosts=args.hosts, quiet=not args.verbose_hosts)
    try:
        return _run(policy, norm, optimizer, hyper, env, stage, budget, threshold, run_dir, device,
                    args.eval_every, args.eval_steps, args)
    finally:
        env.close()


def _run(policy, norm, optimizer, hyper: Hyper, env: VectorEnv, stage: int, budget: int,
         threshold: float, run_dir: Path, device, eval_every: int, eval_steps: int, eval_args) -> bool:
    n_agents = env.num_agents
    obs_size = env.obs_size
    rollout = hyper.rollout

    obs_buf = np.zeros((rollout, n_agents, obs_size), dtype=np.float32)
    act_buf = np.zeros((rollout, n_agents, layout.WIRE_ACTION_SIZE), dtype=np.float32)
    logp_buf = np.zeros((rollout, n_agents), dtype=np.float32)
    rew_buf = np.zeros((rollout, n_agents), dtype=np.float32)
    done_buf = np.zeros((rollout, n_agents), dtype=np.float32)
    # True where the agent was still running when the step began; see the note by `alive` below.
    alive_buf = np.zeros((rollout, n_agents), dtype=bool)
    val_buf = np.zeros((rollout, n_agents), dtype=np.float32)

    obs = env.reset()
    total_steps = 0
    update = 0
    t0 = time.time()
    arrival_history: list[float] = []
    det_rate = 0.0
    best_rate = 0.0
    recent_det: list[float] = []
    # G2 state: how many evals since the shipped rate last improved, and how many perturbation rounds this
    # stage has spent. See the block after the gate check.
    evals_since_best = 0
    perturb_rounds = 0
    baseline_logged = False
    ret_scale = ReturnScale()

    # Which agents are still running. TrainingEnv keeps reporting an agent that has finished -- reward 0,
    # done 1, and an observation slice Clear()ed to all zeros -- every step until EVERY agent finishes and
    # Reset() is called. Those are not transitions, and three things must not see them:
    #
    #   - RunningNorm, whose mean and variance are BAKED INTO the exported weight file. Feeding it all-zero
    #     vectors biases the mean toward zero and inflates the variance, so the shipped policy normalises
    #     real observations with statistics computed from padding. RunningNorm.count accumulates across the
    #     whole curriculum, so this does not wash out between stages.
    #   - The PPO losses, which would otherwise average over dead samples and shrink the real gradient.
    #   - GAE, which reads done=1 on every padded step and chops its accumulation there.
    #
    # Measured share of agent-steps that are padding, holding forward: 83.5% on stage 1, 19.6% on stage 6.
    # Stage 1 agents arrive in a fraction of the 900-step cap and then idle; stage 6 agents mostly run to
    # the cap. So this is worst on the stage that trains BEST, and it is not the stage-6 plateau -- it is a
    # correctness bug in its own right.
    alive = np.ones(n_agents, dtype=bool)

    while total_steps < budget:
        update += 1
        t_env = t_pol = 0.0
        for t in range(rollout):
            _p0 = time.perf_counter()
            alive_buf[t] = alive
            if alive.any():
                norm.update(obs[alive])
            nobs = norm.normalize(obs)
            obs_buf[t] = nobs

            with torch.inference_mode():
                tobs = torch.as_tensor(nobs, device=device)
                wire, logp, _, value = policy.act(tobs)
            # Copied out to numpy immediately: inference-mode tensors cannot participate in the autograd
            # graph the PPO update builds, and these values are only ever read as data.
            act_buf[t] = wire.cpu().numpy()
            logp_buf[t] = logp.cpu().numpy()
            val_buf[t] = value.cpu().numpy()
            _p1 = time.perf_counter(); t_pol += _p1 - _p0

            obs, reward, done, trunc = env.step(act_buf[t])
            t_env += time.perf_counter() - _p1
            rew_buf[t] = reward
            # Truncation is NOT termination: the value function must still bootstrap through a step that
            # ended only because the clock ran out. Conflating them teaches the policy that time limits are
            # a form of death and makes it hurry into walls near the cap.
            done_buf[t] = done.astype(np.float32)
            # This step counted only for agents that were still running when it began. An agent that was
            # already finished contributes padding, not experience.
            total_steps += int(alive.sum())
            # Finished means done OR truncated, and the distinction matters here in a way it does not in
            # done_buf. A timeout reports done=0, truncated=1, and TrainingEnv still marks that agent
            # finished and pads it from then on -- so masking on done alone would leave the majority of
            # stage 6 unmasked, where most agents reach the step cap rather than dying or arriving.
            finished = done.astype(bool) | trunc.astype(bool)
            alive &= ~finished
            # VectorEnv resets a host the moment all of its agents are finished, inside this same step call,
            # so that host's observations are already fresh and its agents are live again.
            per_host = env.agents_per_host
            for lo in range(0, n_agents, per_host):
                if finished[lo:lo + per_host].all():
                    alive[lo:lo + per_host] = True

        with torch.inference_mode():
            last_value = policy(torch.as_tensor(norm.normalize(obs), device=device))[1].cpu().numpy()

        # Scale the rewards by a running estimate of return magnitude before GAE.
        #
        # The reward mixes a per-step progress term around 0.2 with a one-off arrival bonus of 10 to 30 and
        # a death penalty of -5. The critic has to fit both, and it does not: the value loss sat around 8.9
        # while the policy loss was 0.02, so the advantages were dominated by value error and the policy
        # oscillated between updates rather than converging. Measured on stage 3, consecutive shipped-path
        # evals 25 updates apart swung between 20% and 59% -- and the eval is deterministic for a fixed
        # policy, so that is the policy genuinely thrashing, not the measurement.
        #
        # Dividing by a running std leaves the reward's SHAPE untouched (no mean shift, so the relative
        # value of arriving versus progressing is unchanged) and gives the critic a unit-scale target.
        ret_scale.update(rew_buf, hyper.gamma)
        scaled_rew = rew_buf / ret_scale.std()
        adv, ret = gae(scaled_rew, val_buf, done_buf, last_value, hyper.gamma, hyper.gae_lambda)

        _u0 = time.perf_counter()
        stats = ppo_update(policy, optimizer, hyper, obs_buf, act_buf, logp_buf, adv, ret, device,
                           alive_buf=alive_buf)
        t_upd = time.perf_counter() - _u0

        # Rollout arrivals, from SAMPLED actions. A progress signal, not a gate: see the eval below.
        ep = env.episode_stats()
        if ep:
            rate = float(np.mean([a / max(1, env.agents_per_host) for a, _, _ in ep]))
            arrival_history.append(rate)
            arrival_history[:] = arrival_history[-20:]
        sampled = float(np.mean(arrival_history)) if arrival_history else 0.0

        sps = total_steps / max(1e-6, time.time() - t0)
        print(f"[s{stage} u{update:4d}] steps {total_steps:>10,}  {sps:6.0f}/s  "
              f"reward {rew_buf.mean():+.4f}  sampled {sampled:5.1%}  shipped {det_rate:5.1%}  "
              f"pi {stats['policy_loss']:+.4f}  v {stats['value_loss']:.4f}  rs {ret_scale.std():.2f}  "
              f"ent {stats['entropy']:.3f}  kl {stats['kl']:.4f}  "
              # Entropy bonus over policy gradient. Above ~1.0 the regulariser is the larger term in the
              # loss and the policy drifts toward uniform no matter what the reward says -- the failure
              # that held stage 6 at 3-9% for 8.8M steps while entropy climbed.
              f"e/p {hyper.entropy_coef * stats['entropy'] / max(1e-9, abs(stats['policy_loss'])):5.2f}  "
              f"[env {t_env:.2f}s pol {t_pol:.2f}s upd {t_upd:.2f}s]")

        if update % 20 == 0:
            save(policy, norm, optimizer, stage, run_dir)

        # ---- the advancement gate, measured DETERMINISTICALLY ----
        #
        # What ships is the argmax policy; what the rollout measures is the sampled one, and the gap is
        # enormous while exploration noise is alive. Measured on the same stage-3 checkpoint: 11.9%
        # arrivals sampled, 71% deterministic. Gating on the rollout number stalled the curriculum on a
        # stage the deployable policy had already cleared, and more training would not have moved it --
        # the entropy bonus keeps the sampled policy noisy on purpose.
        # Runs whether or not there is a gate: a single-stage run still wants to see the number that
        # matters, and printing `det 0.0%` because the eval was skipped is worse than not printing it.
        if update % eval_every == 0:
            det_rate = evaluate(policy, norm, run_dir, stage, eval_steps, eval_args)
            gate = f"gate {threshold:.0%}" if threshold > 0 else "no gate"

            # Keep the best policy this stage has produced, separately from the rolling checkpoint.
            #
            # A stage can make the policy WORSE, and the rolling checkpoint happily records that. It
            # happened: stage 4's course generator was broken, twelve million steps drove the arrival rate
            # from 10% down to 5%, and the 71.6% policy that had just cleared stage 3 was overwritten a
            # hundred times over. Recovering it meant retraining a stage. One extra file per stage is a
            # cheap insurance premium against that.
            if det_rate > best_rate:
                best_rate = det_rate
                evals_since_best = 0
                save(policy, norm, optimizer, stage, run_dir, tag=f"stage{stage}-best")
            else:
                evals_since_best += 1
                recent_det.append(det_rate)
                recent_det[:] = recent_det[-4:]
                # Four consecutive readings well under the best, not one. A single low eval on a noisy
                # measurement is noise, and a warning that cries wolf is a warning nobody reads.
                if len(recent_det) == 4 and best_rate - max(recent_det) > 0.12:
                    print(f"[s{stage}] WARNING: the last 4 evals peaked at {max(recent_det):.1%} against "
                          f"this stage's best of {best_rate:.1%}. A stage that goes backwards is usually the "
                          f"course, not the policy — check the scripted baseline with --scripted on the "
                          f"same stage before spending more compute.")
                    recent_det.clear()

            print(f"[s{stage}] shipped-path eval: {det_rate:.1%} arrivals ({gate}, best {best_rate:.1%})")
            if threshold > 0 and det_rate >= threshold:
                save(policy, norm, optimizer, stage, run_dir)
                save(policy, norm, optimizer, stage + 1, run_dir, tag=f"stage{stage}-done")
                return True
            # The eval runs out-of-process now, so the rollout state is untouched and there is nothing
            # to reset.

            # G2: plateau perturb-and-select. A single GA generation -- mutation and selection, no
            # crossover -- stacked on PPO, firing only when gradient descent has stopped paying: no new
            # best for 4 consecutive evals. Candidates are scored through the same shipped-path bench as
            # the gate, so "better" means better on the number that matters.
            #
            # The history that shapes this block: every plateau so far was an objective or data defect, not
            # an optimiser trap. So the FIRST thing a firing does is re-measure the scripted baseline on
            # this stage as it is right now -- if that has moved, the task is mis-specified and no amount
            # of weight noise will fix it -- and the whole mechanism is capped at 3 rounds per stage.
            if (not eval_args.no_evolve) and evals_since_best >= 4 and perturb_rounds < 3:
                perturb_rounds += 1
                evals_since_best = 0
                if not baseline_logged:
                    base = evaluate_scripted(stage, eval_steps, eval_args)
                    print(f"[evolve s{stage}] scripted baseline right now: {base:.1%} "
                          f"(gate {threshold:.0%}) — if these disagree with the gate's assumptions, "
                          f"fix the task before blaming the policy")
                    baseline_logged = True
                print(f"[evolve s{stage}] round {perturb_rounds}: no new best in 4 evals; "
                      f"perturb-and-select from {det_rate:.1%}")
                parent = copy.deepcopy(policy.state_dict())
                best_c_rate, best_c_state = max(det_rate, best_rate), None
                for k, sigma in enumerate([0.02, 0.02, 0.05, 0.05, 0.1, 0.1]):
                    with torch.no_grad():
                        for _, prm in policy.named_parameters():
                            prm.add_(torch.randn_like(prm) * sigma * (prm.std() + 1e-8))
                    r = evaluate(policy, norm, run_dir, stage, eval_steps, eval_args)
                    print(f"[evolve s{stage}]   candidate {k} sigma {sigma:g}: "
                          f"{r if r == r else float('nan'):.1%}")
                    if r == r and r > best_c_rate + 0.005:
                        best_c_rate = r
                        best_c_state = copy.deepcopy(policy.state_dict())
                    policy.load_state_dict(parent)
                if best_c_state is not None:
                    # Adam's moments now describe the parent, not the child; they are close enough that the
                    # next few updates re-estimate them, and resetting them entirely would forget more.
                    policy.load_state_dict(best_c_state)
                    best_rate = best_c_rate
                    save(policy, norm, optimizer, stage, run_dir, tag=f"stage{stage}-best")
                    print(f"[evolve s{stage}] adopted a candidate at {best_c_rate:.1%}")
                else:
                    print(f"[evolve s{stage}] no candidate beat the parent; PPO continues unchanged")

    save(policy, norm, optimizer, stage, run_dir)
    if threshold <= 0:
        return True
    final = evaluate(policy, norm, run_dir, stage, eval_steps, eval_args)
    print(f"[s{stage}] final shipped-path eval: {final:.1%} arrivals "
          f"(gate {threshold:.0%}, best this stage {best_rate:.1%})")
    if final < best_rate:
        print(f"[s{stage}] the best policy this stage produced is in "
              f"{run_dir / f'stage{stage}-best.pt'}, NOT the rolling checkpoint — resume from that one.")
    return final >= threshold


def evaluate(policy, norm, run_dir: Path, stage: int, steps: int, args) -> float:
    """Arrival rate of the exported policy, measured through the path that actually SHIPS.

    Exports the weights and hands them to ``va-neural-host --bench --policy``, which runs the network
    inside the game's own locomotor: the same code a live server executes, deciding at the same fixed rate.

    Not measured in-process, and the reason is worth keeping. The trainer drives the environment through
    an external-action path — observation out over a socket, action back in — and that path is measurably
    worse than the locomotor evaluating the network itself. Same weights, same seed, stage 3:
    **34.7% arrivals in-locomotor against 8.8% through the external path.** Two causes were found and fixed
    (the decision rate was skill-scaled rather than fixed, and the observation was built inside the think
    rather than at the step boundary); a residual gap remains, and bunnyhopping is chaotic enough that
    small timing differences compound over a 50-second episode.

    Whatever the residual is, gating on it would be gating on a number the shipped bot never experiences.
    """
    weights = run_dir / "eval.vxpw"
    export_weights(policy, norm, weights, label=f"{run_dir.name}-s{stage}-eval")

    dll = Path(__file__).resolve().parent / "VortexArena.NeuralHost" / "bin" / "Release" / "net8.0" / "va-neural-host.dll"
    if not dll.exists():
        return float("nan")

    # Episode-bounded, with the step count as the safety cap. Every eval then scores the same courses;
    # a step budget alone scores a fast policy on a different slice than a slow one, which turned the
    # gate into a lottery (stage 3 swung between 20% and 59% on consecutive evals).
    cmd = [shutil.which("dotnet") or "dotnet", str(dll),
           "--bench", str(steps * 4), "--bench-episodes", str(args.eval_episodes),
           "--agents", str(args.agents), "--ticks", str(args.ticks),
           "--stage", str(stage), "--seed", str(args.seed + 9001),
           "--policy", str(weights)]
    # Every stage but 5 now draws from shipped maps, so the data root goes on unconditionally. Passing it
    # only for stage 6 would make the eval for stages 1-4 fail to find any map and score zero.
    cmd += ["--data", str(args.data) if args.data else str(Path(__file__).resolve().parents[2] / "data")]
    if args.maps:
        cmd += ["--maps", args.maps]

    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=900).stdout
    except (subprocess.TimeoutExpired, OSError):
        return float("nan")

    for line in out.splitlines():
        if line.startswith("arrival rate"):
            # "arrival rate   34.7 % (161/464 agent-episodes)"
            try:
                return float(line.split()[2]) / 100.0
            except (IndexError, ValueError):
                return float("nan")
    # The bench ran but printed no arrival line -- a load failure or a crash. NaN, explicitly: it
    # loses every comparison, so a broken eval can never pass a gate or win a tournament.
    return float("nan")
    return float("nan")


class ReturnScale:
    """A running standard deviation of the discounted return, for scaling rewards.

    Tracks the std of the discounted return accumulator rather than of the raw rewards, which is what the
    critic actually has to predict. Std only, never the mean: subtracting a mean would change the relative
    value of arriving versus making progress, and that ratio is the reward design.
    """

    def __init__(self):
        self._ret = None
        self._mean = 0.0
        self._var = 1.0
        self._count = 1e-4

    def update(self, rewards: np.ndarray, gamma: float) -> None:
        n_agents = rewards.shape[1]
        if self._ret is None or self._ret.shape[0] != n_agents:
            self._ret = np.zeros(n_agents, dtype=np.float64)
        for t in range(rewards.shape[0]):
            self._ret = self._ret * gamma + rewards[t]
            batch_mean = float(self._ret.mean())
            batch_var = float(self._ret.var())
            delta = batch_mean - self._mean
            total = self._count + n_agents
            self._mean += delta * n_agents / total
            m_a = self._var * self._count
            m_b = batch_var * n_agents
            self._var = (m_a + m_b + delta**2 * self._count * n_agents / total) / total
            self._count = total

    def std(self) -> float:
        # Floored so an early, near-constant reward stream cannot divide by nothing and blow the gradients.
        return max(float(np.sqrt(self._var)), 1e-3)


def evaluate_scripted(stage: int, steps: int, args) -> float:
    """Arrival rate of the scripted hold-forward baseline on this stage, right now.

    The number a gate should be calibrated against. G2 logs it once per stage before spending any
    perturbation budget, because a plateau against a moved baseline is a task problem, not a policy one.
    """
    dll = Path(__file__).resolve().parent / "VortexArena.NeuralHost" / "bin" / "Release" / "net8.0" / "va-neural-host.dll"
    cmd = [shutil.which("dotnet") or "dotnet", str(dll),
           "--bench", str(steps * 4), "--bench-episodes", str(args.eval_episodes),
           "--agents", str(args.agents), "--ticks", str(args.ticks),
           "--stage", str(stage), "--seed", str(args.seed + 9001), "--scripted"]
    cmd += ["--data", str(args.data) if args.data else str(Path(__file__).resolve().parents[2] / "data")]
    if args.maps:
        cmd += ["--maps", args.maps]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=900).stdout
    except (subprocess.TimeoutExpired, OSError):
        return float("nan")
    for line in out.splitlines():
        if line.startswith("arrival rate"):
            return float(line.split()[2]) / 100.0
    return float("nan")


def gae(rewards, values, dones, last_value, gamma: float, lam: float):
    """Generalised advantage estimation over the rollout."""
    T, N = rewards.shape
    adv = np.zeros((T, N), dtype=np.float32)
    gae_run = np.zeros(N, dtype=np.float32)
    for t in reversed(range(T)):
        next_value = last_value if t == T - 1 else values[t + 1]
        next_nonterminal = 1.0 - dones[t]
        delta = rewards[t] + gamma * next_value * next_nonterminal - values[t]
        gae_run = delta + gamma * lam * next_nonterminal * gae_run
        adv[t] = gae_run
    return adv, adv + values


def ppo_update(policy, optimizer, hyper: Hyper, obs_buf, act_buf, logp_buf, adv, ret, device,
               alive_buf=None) -> dict:
    T, N = adv.shape
    # Drop padding outright rather than weighting it to zero. An agent that has already finished keeps
    # being reported every step -- zero reward, done 1, all-zero observation -- until the whole host
    # resets, and those rows are not transitions. Selecting them out here also normalises the advantages
    # over real experience only, which weighting after the fact would not do.
    keep = (torch.as_tensor(alive_buf.reshape(T * N), device=device)
            if alive_buf is not None else torch.ones(T * N, dtype=torch.bool, device=device))
    b_obs = torch.as_tensor(obs_buf.reshape(T * N, -1), device=device)[keep]
    b_act = torch.as_tensor(act_buf.reshape(T * N, -1), device=device)[keep]
    b_logp = torch.as_tensor(logp_buf.reshape(T * N), device=device)[keep]
    b_adv = torch.as_tensor(adv.reshape(T * N), device=device)[keep]
    b_ret = torch.as_tensor(ret.reshape(T * N), device=device)[keep]
    b_adv = (b_adv - b_adv.mean()) / (b_adv.std() + 1e-8)

    # The masked row count, NOT T*N: selecting padding out above shortened every buffer, and indexing the
    # short tensors with the full range walks off the end.
    total = int(b_obs.shape[0])
    batch = max(1, total // hyper.minibatches)
    idx = np.arange(total)

    out = {"policy_loss": 0.0, "value_loss": 0.0, "entropy": 0.0, "kl": 0.0}
    n = 0
    stop = False
    for _ in range(hyper.epochs):
        if stop:
            break
        np.random.shuffle(idx)
        for start in range(0, total, batch):
            mb = torch.as_tensor(idx[start : start + batch], device=device, dtype=torch.long)
            logp, entropy, value = policy.evaluate(b_obs[mb], b_act[mb])

            ratio = (logp - b_logp[mb]).exp()
            surr1 = ratio * b_adv[mb]
            surr2 = torch.clamp(ratio, 1 - hyper.clip, 1 + hyper.clip) * b_adv[mb]
            policy_loss = -torch.min(surr1, surr2).mean()
            value_loss = 0.5 * (value - b_ret[mb]).pow(2).mean()
            ent = entropy.mean()

            loss = policy_loss + hyper.value_coef * value_loss - hyper.entropy_coef * ent

            optimizer.zero_grad()
            loss.backward()
            nn.utils.clip_grad_norm_(policy.parameters(), hyper.max_grad_norm)
            optimizer.step()

            with torch.no_grad():
                # Schulman's k3 estimator: (r - 1) - log r. Non-negative by construction and far lower
                # variance than the naive (old_logp - logp).mean(), which was the first thing here and
                # reported NEGATIVE values every update — so the early-stop below never fired and the
                # target_kl setting was decorative.
                log_ratio = logp - b_logp[mb]
                kl = ((log_ratio.exp() - 1.0) - log_ratio).mean().item()
            out["policy_loss"] += policy_loss.item()
            out["value_loss"] += value_loss.item()
            out["entropy"] += ent.item()
            out["kl"] += kl
            n += 1

            # An update that has already moved the policy this far will not be improved by three more
            # epochs of the same data; it will be wrecked by them.
            if kl > hyper.target_kl:
                stop = True
                break

    for k in out:
        out[k] /= max(1, n)
    return out


def save(policy, norm, optimizer, stage: int, run_dir: Path, tag: str | None = None) -> None:
    """Write a checkpoint plus its game-loadable weights.

    ``tag`` names a keeper (``stage3-best``, ``stage3-done``); without it this is the rolling checkpoint,
    which the next save overwrites.
    """
    name = tag or "checkpoint"
    torch.save({
        "policy": policy.state_dict(),
        "optimizer": optimizer.state_dict(),
        "norm": norm.state(),
        "stage": stage,
    }, run_dir / f"{name}.pt")
    # Export the game-loadable weights alongside every checkpoint, so any run can be dropped straight into
    # a server without a separate step that someone will forget.
    export_weights(policy, norm, run_dir / f"{'policy' if tag is None else tag}.vxpw",
                   label=f"{run_dir.name}-s{stage}{'' if tag is None else '-' + tag}")


if __name__ == "__main__":
    raise SystemExit(main())

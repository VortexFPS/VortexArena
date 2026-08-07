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
    minibatches: int = 8
    lr: float = 3e-4
    gamma: float = 0.995        # 0.055 s per step, so this is a ~9 s horizon
    gae_lambda: float = 0.95
    clip: float = 0.2
    value_coef: float = 0.5
    # 0.002, not the usual 0.01. The action space is SIX categorical heads plus two Gaussians, and the
    # entropy term sums over all of them, so it starts around 8.2 nats where a single-head space would be
    # near 2. At 0.01 the bonus contributed 0.082 to the loss against a policy loss of 0.006: measured over
    # 1.2M steps on stage 1, entropy never moved off uniform and arrivals crawled to 3%. Scale the
    # coefficient with the number of heads, not with habit.
    entropy_coef: float = 0.002
    max_grad_norm: float = 0.5
    target_kl: float = 0.03     # stop the epoch loop early rather than let one update wreck the policy


# Stage, minimum steps, and the arrival rate that says it is done. The rates are deliberately not 100%:
# a course generator that produces the occasional near-impossible layout is doing its job, and waiting for
# perfection on stage 3 means never reaching stage 5.
CURRICULUM = [
    (1, 2_000_000, 0.90),   # flat: run and turn
    (2, 4_000_000, 0.85),   # corridor: build and hold speed
    (3, 8_000_000, 0.70),   # terrain: jump timing
    (4, 8_000_000, 0.65),   # furniture: pads, teleporters, hazards
    (5, 10_000_000, 0.45),  # weapon gaps: rocket and blaster jumps
    # Real maps, held-out split excluded. The threshold is low because a shipped arena route is a harder
    # problem than any generated course: measured 12.5% arrivals for a policy that scores 71% on stage 3.
    (6, 20_000_000, 0.55),
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
    ap.add_argument("--hosts", type=int, default=4, help="env host processes; one core each")
    ap.add_argument("--agents", type=int, default=8, help="agents per host")
    ap.add_argument("--ticks", type=int, default=4, help="sim ticks per policy step")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--out", type=Path, default=Path("runs"))
    ap.add_argument("--name", type=str, default=None)
    ap.add_argument("--resume", type=Path, default=None)
    ap.add_argument("--width", type=int, default=128)
    ap.add_argument("--device", type=str, default="cpu",
                    help="cpu is usually right: the net is 45k parameters and the bottleneck is the env")
    ap.add_argument("--verbose-hosts", action="store_true", help="let the env hosts write to stderr")
    ap.add_argument("--eval-every", type=int, default=10,
                    help="updates between deterministic evals; the curriculum gate reads these, "
                         "not the sampled rollout rate")
    ap.add_argument("--eval-steps", type=int, default=400, help="steps per deterministic eval pass")
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

    policy = Policy(obs_size=layout.OBS_SIZE, width=args.width).to(device)
    norm = RunningNorm(layout.OBS_SIZE)
    hyper = Hyper()
    optimizer = torch.optim.Adam(policy.parameters(), lr=hyper.lr, eps=1e-5)

    start_stage = args.stage
    if args.resume:
        ckpt = torch.load(args.resume, map_location=device)
        policy.load_state_dict(ckpt["policy"])
        optimizer.load_state_dict(ckpt["optimizer"])
        norm.load(ckpt["norm"])
        start_stage = ckpt.get("stage", args.stage)
        print(f"[train] resumed {args.resume} at stage {start_stage}")

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
    val_buf = np.zeros((rollout, n_agents), dtype=np.float32)

    obs = env.reset()
    total_steps = 0
    update = 0
    t0 = time.time()
    arrival_history: list[float] = []
    det_rate = 0.0
    best_rate = 0.0

    while total_steps < budget:
        update += 1
        for t in range(rollout):
            norm.update(obs)
            nobs = norm.normalize(obs)
            obs_buf[t] = nobs

            with torch.no_grad():
                tobs = torch.as_tensor(nobs, device=device)
                wire, logp, _, value = policy.act(tobs)
            act_buf[t] = wire.cpu().numpy()
            logp_buf[t] = logp.cpu().numpy()
            val_buf[t] = value.cpu().numpy()

            obs, reward, done, trunc = env.step(act_buf[t])
            rew_buf[t] = reward
            # Truncation is NOT termination: the value function must still bootstrap through a step that
            # ended only because the clock ran out. Conflating them teaches the policy that time limits are
            # a form of death and makes it hurry into walls near the cap.
            done_buf[t] = done.astype(np.float32)
            total_steps += n_agents

        with torch.no_grad():
            last_value = policy(torch.as_tensor(norm.normalize(obs), device=device))[1].cpu().numpy()

        adv, ret = gae(rew_buf, val_buf, done_buf, last_value, hyper.gamma, hyper.gae_lambda)

        stats = ppo_update(policy, optimizer, hyper, obs_buf, act_buf, logp_buf, adv, ret, device)

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
              f"pi {stats['policy_loss']:+.4f}  v {stats['value_loss']:.4f}  "
              f"ent {stats['entropy']:.3f}  kl {stats['kl']:.4f}")

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
                save(policy, norm, optimizer, stage, run_dir, tag=f"stage{stage}-best")
            elif best_rate - det_rate > 0.15:
                print(f"[s{stage}] WARNING: {det_rate:.1%} is well below this stage's best of {best_rate:.1%}. "
                      f"A stage that goes backwards is usually the course, not the policy — check the "
                      f"scripted baseline with --scripted on the same stage before spending more compute.")

            print(f"[s{stage}] shipped-path eval: {det_rate:.1%} arrivals ({gate}, best {best_rate:.1%})")
            if threshold > 0 and det_rate >= threshold:
                save(policy, norm, optimizer, stage, run_dir)
                save(policy, norm, optimizer, stage + 1, run_dir, tag=f"stage{stage}-done")
                return True
            # The eval runs out-of-process now, so the rollout state is untouched and there is nothing
            # to reset.

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

    cmd = [shutil.which("dotnet") or "dotnet", str(dll), "--bench", str(steps),
           "--agents", str(args.agents), "--ticks", str(args.ticks),
           "--stage", str(stage), "--seed", str(args.seed + 9001),
           "--policy", str(weights)]
    if stage == 6:
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


def ppo_update(policy, optimizer, hyper: Hyper, obs_buf, act_buf, logp_buf, adv, ret, device) -> dict:
    T, N = adv.shape
    b_obs = torch.as_tensor(obs_buf.reshape(T * N, -1), device=device)
    b_act = torch.as_tensor(act_buf.reshape(T * N, -1), device=device)
    b_logp = torch.as_tensor(logp_buf.reshape(T * N), device=device)
    b_adv = torch.as_tensor(adv.reshape(T * N), device=device)
    b_ret = torch.as_tensor(ret.reshape(T * N), device=device)
    b_adv = (b_adv - b_adv.mean()) / (b_adv.std() + 1e-8)

    total = T * N
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

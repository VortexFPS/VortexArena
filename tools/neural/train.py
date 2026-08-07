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
    ap.add_argument("--data", type=Path, default=None,
                    help="content root for stage 6 (default: <repo>/data)")
    ap.add_argument("--maps", type=str, default="",
                    help="stage 6 map list, comma separated; empty means every installed map. "
                         "The held-out eval split is excluded either way.")
    args = ap.parse_args()

    run_name = args.name or time.strftime("%Y%m%d-%H%M%S")
    run_dir = args.out / run_name
    run_dir.mkdir(parents=True, exist_ok=True)
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
        return _run(policy, norm, optimizer, hyper, env, stage, budget, threshold, run_dir, device)
    finally:
        env.close()


def _run(policy, norm, optimizer, hyper: Hyper, env: VectorEnv, stage: int, budget: int,
         threshold: float, run_dir: Path, device) -> bool:
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

        ep = env.episode_stats()
        if ep:
            rate = float(np.mean([a / max(1, env.agents_per_host) for a, _, _ in ep]))
            arrival_history.append(rate)
            arrival_history[:] = arrival_history[-20:]

        recent = float(np.mean(arrival_history)) if arrival_history else 0.0
        sps = total_steps / max(1e-6, time.time() - t0)
        print(f"[s{stage} u{update:4d}] steps {total_steps:>10,}  {sps:6.0f}/s  "
              f"reward {rew_buf.mean():+.4f}  arrivals {recent:5.1%}  "
              f"pi {stats['policy_loss']:+.4f}  v {stats['value_loss']:.4f}  "
              f"ent {stats['entropy']:.3f}  kl {stats['kl']:.4f}")

        if update % 20 == 0:
            save(policy, norm, optimizer, stage, run_dir)

        # Advance only on a sustained rate, not on one lucky update.
        if threshold > 0 and len(arrival_history) >= 10 and recent >= threshold:
            print(f"[s{stage}] arrival rate {recent:.1%} over the last {len(arrival_history)} updates")
            save(policy, norm, optimizer, stage, run_dir)
            return True

    save(policy, norm, optimizer, stage, run_dir)
    recent = float(np.mean(arrival_history)) if arrival_history else 0.0
    return threshold <= 0 or recent >= threshold


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


def save(policy, norm, optimizer, stage: int, run_dir: Path) -> None:
    torch.save({
        "policy": policy.state_dict(),
        "optimizer": optimizer.state_dict(),
        "norm": norm.state(),
        "stage": stage,
    }, run_dir / "checkpoint.pt")
    # Export the game-loadable weights alongside every checkpoint, so any run can be dropped straight into
    # a server without a separate step that someone will forget.
    export_weights(policy, norm, run_dir / "policy.vxpw", label=f"{run_dir.name}-s{stage}")


if __name__ == "__main__":
    raise SystemExit(main())

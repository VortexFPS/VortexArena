"""Small regression tests for trainer math that must survive checkpoint resumes."""

import sys
import threading
from pathlib import Path

import numpy as np
import torch

NEURAL = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(NEURAL))

from train import (ReturnScale, _collect_eval_processes, _legacy_resume_state,
                   _shard_episode_counts, gae, save)  # noqa: E402
from va_neural.model import Policy, RunningNorm  # noqa: E402


def test_log_std_cap_keeps_a_gradient_for_legacy_checkpoints():
    policy = Policy(obs_size=302)
    policy.log_std.data.fill_(-0.5)  # value stored by checkpoints created before the cap
    policy.view_log_std.sum().backward()
    assert torch.allclose(policy.view_log_std, torch.full_like(policy.log_std, Policy.LOG_STD_MAX))
    assert torch.all(policy.log_std.grad == 1)


def test_gae_bootstraps_timeout_but_stops_trace_at_episode_boundary():
    rewards = np.array([[1.0], [2.0]], dtype=np.float32)
    values = np.array([[10.0], [20.0]], dtype=np.float32)
    next_values = np.array([[20.0], [30.0]], dtype=np.float32)
    terminated = np.zeros((2, 1), dtype=np.float32)
    truncated = np.array([[0.0], [1.0]], dtype=np.float32)
    adv, _ = gae(rewards, values, terminated, truncated, next_values, gamma=0.9, lam=0.95)
    # Timeout delta bootstraps from its final observation: 2 + .9*30 - 20 = 9.
    assert np.isclose(adv[1, 0], 9.0)
    # The timeout advantage must not leak backward from a different/reset episode.
    assert np.isclose(adv[0, 0], 1 + 0.9 * 20 - 10 + 0.9 * 0.95 * 9)


def test_return_scale_ignores_padding_and_resets_finished_agents():
    rs = ReturnScale()
    rewards = np.array([[1.0, 1000.0], [2.0, 1000.0]], dtype=np.float32)
    alive = np.array([[True, False], [True, False]])
    finished = np.array([[False, False], [True, False]])
    rs.update(rewards, gamma=0.9, alive=alive, finished=finished)
    assert rs._count < 3.0
    assert rs._ret[0] == 0.0
    state = rs.state()
    restored = ReturnScale()
    restored.load(state)
    assert np.isclose(restored.std(), rs.std())


def test_legacy_rolling_checkpoint_progress_is_recovered_from_its_log(tmp_path):
    (tmp_path / "train.log").write_text(
        "[s3 u2239] steps 44,130,000  2400/s sampled 70.0% shipped nan% ent 0.5 kl 0.01\n"
        "[s3] shipped-path eval: 61.0% arrivals (gate 65%, best 61.0%)\n"
        "[s3 u2240] steps 44,151,422  2428/s sampled 71.9% shipped nan% ent 0.5 kl 0.01\n",
        encoding="utf-8",
    )
    state = _legacy_resume_state(tmp_path, 3)
    assert state["stage_steps"] == 44_151_422
    assert state["update"] == 2240
    assert state["best_rate"] == 0.61


def test_atomic_checkpoint_keeps_the_previous_complete_copy(tmp_path):
    policy = Policy(obs_size=302)
    norm = RunningNorm(302)
    optimizer = torch.optim.Adam(policy.parameters(), lr=3e-4)
    scale = ReturnScale()
    save(policy, norm, optimizer, 3, tmp_path,
         training_state={"stage": 3, "stage_steps": 123}, return_scale=scale)
    first = (tmp_path / "checkpoint.pt").read_bytes()
    with torch.no_grad():
        policy.actor_head.bias.add_(0.01)
    save(policy, norm, optimizer, 3, tmp_path,
         training_state={"stage": 3, "stage_steps": 456}, return_scale=scale)
    assert (tmp_path / "checkpoint.prev.pt").read_bytes() == first
    loaded = torch.load(tmp_path / "checkpoint.pt", weights_only=False)
    assert loaded["checkpoint_version"] == 2
    assert loaded["training_state"]["stage_steps"] == 456
    assert loaded["return_scale"] is not None


def test_eval_episode_split_preserves_every_episode():
    assert _shard_episode_counts(10, 3) == [4, 3, 3]
    assert sum(_shard_episode_counts(120, 4)) == 120
    assert _shard_episode_counts(2, 8) == [1, 1]


def test_eval_shard_pipes_are_drained_concurrently():
    barrier = threading.Barrier(2)

    class FakeProcess:
        returncode = 0

        def communicate(self, timeout):
            barrier.wait(timeout=0.5)
            return "arrival rate 1 (1/1)\n", ""

    outputs, errors = _collect_eval_processes([FakeProcess(), FakeProcess()], timeout=1.0)
    assert len(outputs) == 2
    assert errors == []

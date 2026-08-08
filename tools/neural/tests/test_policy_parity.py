"""The hand-rolled distribution math must match torch.distributions exactly.

``Policy.act`` and ``Policy.evaluate`` compute log-probs and entropy with raw tensor ops rather than
``Categorical``/``Normal`` objects, because those objects were 69% of the rollout's cost. That is only a
safe trade if the numbers are identical, and two failures in particular are silent:

* **act and evaluate disagreeing.** PPO's importance ratio must be exactly 1 on the first epoch. When it
  is not, the symptom is a negative KL estimate and a policy that never learns -- which is what happened
  once already, from storing a clamped action against an unclamped log-prob.
* **entropy drifting.** The entropy bonus is a loss term; a wrong constant silently retunes exploration.

Run:  python -m pytest tools/neural/tests -q
  or: python tools/neural/tests/test_policy_parity.py
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import torch

from va_neural import layout
from va_neural.model import Policy


def _fixture(seed: int = 7, batch: int = 64):
    torch.manual_seed(seed)
    policy = Policy(obs_size=layout.OBS_SIZE, width=64).eval()
    # A non-trivial log_std, so a bug that assumes sigma == 1 shows up.
    with torch.no_grad():
        policy.log_std.copy_(torch.tensor([-0.31, 0.22]))
    obs = torch.randn(batch, layout.OBS_SIZE)
    return policy, obs


def test_evaluate_matches_torch_distributions():
    """Log-prob and entropy from the raw-tensor path equal the torch.distributions path."""
    policy, obs = _fixture()
    with torch.no_grad():
        wire, _, _, _ = policy.act(obs)
        logp_fast, entropy_fast, _ = policy.evaluate(obs, wire)

        logits, _ = policy(obs)
        cats, normal = policy.distributions(logits)
        logp_ref = sum(c.log_prob(wire[..., i].long()) for i, c in enumerate(cats))
        continuous = wire[..., len(layout.CATEGORICAL_HEADS) :]
        logp_ref = logp_ref + normal.log_prob(continuous).sum(-1)
        entropy_ref = sum(c.entropy() for c in cats) + normal.entropy().sum(-1)

    assert torch.allclose(logp_fast, logp_ref, atol=1e-5), \
        f"log-prob drift: max {(logp_fast - logp_ref).abs().max().item():.2e}"
    assert torch.allclose(entropy_fast, entropy_ref, atol=1e-5), \
        f"entropy drift: max {(entropy_fast - entropy_ref).abs().max().item():.2e}"


def test_act_and_evaluate_agree_on_the_same_action():
    """The PPO ratio is exp(evaluate - act). On the sampled action it must be exactly 1."""
    policy, obs = _fixture(seed=11)
    with torch.no_grad():
        wire, logp_act, _, _ = policy.act(obs)
        logp_eval, _, _ = policy.evaluate(obs, wire)
        ratio = (logp_eval - logp_act).exp()

    assert torch.allclose(logp_act, logp_eval, atol=1e-5), \
        f"act/evaluate disagree by up to {(logp_act - logp_eval).abs().max().item():.2e}"
    assert torch.allclose(ratio, torch.ones_like(ratio), atol=1e-4), \
        f"first-epoch importance ratio is not 1: range [{ratio.min():.4f}, {ratio.max():.4f}]"


def test_deterministic_act_takes_the_argmax():
    """Deterministic mode is what ships; it must pick the mode of every head."""
    policy, obs = _fixture(seed=3)
    with torch.no_grad():
        wire, _, _, _ = policy.act(obs, deterministic=True)
        logits, _ = policy(obs)
        off = 0
        for i, (_, size) in enumerate(layout.CATEGORICAL_HEADS):
            expected = logits[..., off : off + size].argmax(-1)
            assert torch.equal(wire[..., i].long(), expected), f"head {i} is not the argmax"
            off += size
        expected_mean = torch.tanh(logits[..., off : off + layout.N_CONTINUOUS])
        assert torch.allclose(wire[..., len(layout.CATEGORICAL_HEADS) :], expected_mean, atol=1e-6)


def test_sampling_is_unbiased():
    """Gumbel-max must reproduce the categorical it replaced, not merely something plausible."""
    torch.manual_seed(5)
    policy = Policy(obs_size=layout.OBS_SIZE, width=64).eval()
    obs = torch.randn(1, layout.OBS_SIZE).repeat(20000, 1)

    with torch.no_grad():
        logits, _ = policy(obs[:1])
        move_size = layout.CATEGORICAL_HEADS[0][1]
        expected = torch.softmax(logits[0, :move_size], dim=-1)
        wire, _, _, _ = policy.act(obs)
        counts = torch.bincount(wire[:, 0].long(), minlength=move_size).float()
        observed = counts / counts.sum()

    # 20k draws: three standard errors on the largest bin is well under 0.02.
    assert torch.allclose(observed, expected, atol=0.02), \
        f"sampling is biased\n  expected {expected.tolist()}\n  observed {observed.tolist()}"


if __name__ == "__main__":
    failures = 0
    for name, fn in sorted(globals().items()):
        if not name.startswith("test_") or not callable(fn):
            continue
        try:
            fn()
            print(f"PASS  {name}")
        except AssertionError as e:
            failures += 1
            print(f"FAIL  {name}\n      {e}")
    raise SystemExit(1 if failures else 0)

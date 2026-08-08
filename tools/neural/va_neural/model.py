"""The policy network, its distribution heads, and the exporter that writes the C# weight format.

Architecture is fixed by what the game can afford, not by what trains best: two hidden layers of 128 over a
206-float observation is about 45,000 parameters, 178 KB in fp32, which fits in L2 and is shared by every bot
on the server. See ``planning/neural-bots-2026-08-07.md`` section 6.1.
"""

from __future__ import annotations

import struct
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
from torch.distributions import Categorical, Normal

from . import layout


class Policy(nn.Module):
    """Actor-critic. The actor is what gets exported; the critic exists only during training.

    The actor's trunk and head are exported as one flat MLP because that is what
    ``PolicyNetwork.Evaluate`` runs: input, two tanh layers, one linear layer to
    ``layout.ACTION_LOGITS``. Nothing in the C# evaluator knows about heads, so the concatenated head layout
    must match ``ActionSpace``'s offsets exactly.
    """

    def __init__(self, obs_size: int = layout.OBS_SIZE, width: int = 128, hidden_layers: int = 2):
        super().__init__()
        self.obs_size = obs_size
        self.width = width
        self.hidden_layers = hidden_layers

        trunk: list[nn.Module] = []
        prev = obs_size
        for _ in range(hidden_layers):
            trunk += [nn.Linear(prev, width), nn.Tanh()]
            prev = width
        self.actor_trunk = nn.Sequential(*trunk)
        self.actor_head = nn.Linear(prev, layout.ACTION_LOGITS)

        # A separate critic trunk, not a shared one. Sharing saves parameters we are not short of and
        # couples the value loss to the policy gradient, which is the usual cause of a run where the value
        # function converges and the policy stops moving.
        critic: list[nn.Module] = []
        prev = obs_size
        for _ in range(hidden_layers):
            critic += [nn.Linear(prev, width), nn.Tanh()]
            prev = width
        self.critic = nn.Sequential(*critic, nn.Linear(prev, 1))

        # Log-std for the two continuous view deltas, a free parameter rather than a network output: the
        # policy should be able to sharpen its aim globally without having to learn to predict its own
        # uncertainty from the observation.
        self.log_std = nn.Parameter(torch.full((layout.N_CONTINUOUS,), -0.5))

        self.apply(self._init)
        # A small final layer keeps the opening policy close to uniform, so the first rollouts explore
        # rather than committing to whatever the initialisation happened to prefer.
        nn.init.orthogonal_(self.actor_head.weight, gain=0.01)
        nn.init.zeros_(self.actor_head.bias)

    @staticmethod
    def _init(m: nn.Module) -> None:
        if isinstance(m, nn.Linear):
            nn.init.orthogonal_(m.weight, gain=np.sqrt(2))
            nn.init.zeros_(m.bias)

    def forward(self, obs: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        return self.actor_head(self.actor_trunk(obs)), self.critic(obs).squeeze(-1)

    # -- distributions --
    #
    # Hand-rolled rather than torch.distributions, and the reason is measured. Profiling one rollout step
    # (48 agents, 206 inputs) split 2.79 ms/call as:
    #
    #     forward (trunk+head+critic)   0.306 ms   11%
    #     Categorical/Normal construct  0.647 ms   23%
    #     .sample()  x7                 0.503 ms   18%
    #     .log_prob() x7                0.584 ms   21%
    #     .entropy() x7                 0.186 ms    7%   <- and act()'s caller discards it
    #
    # Sixty-nine per cent of the cost was distribution-object machinery around a network that is eleven per
    # cent. The heads are six categoricals of size 9/2/2/2/2/4 and two Gaussians; the objects spend their
    # time on argument validation, lazy properties and broadcasting checks that none of these need.
    #
    # act() and evaluate() MUST agree on log-prob to the last bit, or PPO's importance ratio is wrong on the
    # first epoch when it should be exactly 1 -- the same class of bug as storing a clamped action against
    # an unclamped log-prob. They share _heads() for exactly that reason, and
    # NeuralPolicyParityTests keeps them matching torch.distributions.

    _LOG_2PI = float(np.log(2.0 * np.pi))

    def _heads(self, logits: torch.Tensor):
        """Per-head log-softmax and the Gaussian parameters. The one place the head layout is decoded."""
        logp_segments = []
        off = 0
        for _, size in layout.CATEGORICAL_HEADS:
            seg = logits[..., off : off + size]
            logp_segments.append(seg - seg.logsumexp(-1, keepdim=True))
            off += size
        mean = torch.tanh(logits[..., off : off + layout.N_CONTINUOUS])
        return logp_segments, mean

    def distributions(self, logits: torch.Tensor) -> tuple[list[Categorical], Normal]:
        """torch.distributions view of the same heads. Used only by the parity test, never on a hot path."""
        cats, off = [], 0
        for _, size in layout.CATEGORICAL_HEADS:
            cats.append(Categorical(logits=logits[..., off : off + size]))
            off += size
        mean = torch.tanh(logits[..., off : off + layout.N_CONTINUOUS])
        std = self.log_std.exp().expand_as(mean)
        return cats, Normal(mean, std)

    def act(self, obs: torch.Tensor, deterministic: bool = False):
        """Sample an action. Returns (wire action, log-prob, entropy, value).

        Entropy is returned as None: every caller of act() discards it, and computing it cost 7% of the
        rollout. evaluate() computes it properly, which is where the entropy bonus actually reads it.
        """
        logits, value = self(obs)
        logp_segments, mean = self._heads(logits)

        logp = None
        discrete = []
        for logp_seg in logp_segments:
            if deterministic:
                idx = logp_seg.argmax(dim=-1)
            else:
                # Gumbel-max: argmax(logp + Gumbel(0,1)) is an exact categorical sample, and costs one
                # uniform draw plus an argmax instead of a distribution object.
                u = torch.rand_like(logp_seg).clamp_(min=1e-20)
                idx = (logp_seg - torch.log(-torch.log(u))).argmax(dim=-1)
            picked = logp_seg.gather(-1, idx.unsqueeze(-1)).squeeze(-1)
            logp = picked if logp is None else logp + picked
            discrete.append(idx)

        std = self.log_std.exp()
        continuous = mean if deterministic else mean + std * torch.randn_like(mean)
        # log N(x; mu, sigma), summed over the two view-delta dimensions.
        z = (continuous - mean) / std
        logp = logp + (-0.5 * z * z - self.log_std - 0.5 * self._LOG_2PI).sum(-1)

        # ActionEncoding.Size: six indices then the two continuous values, UNCLAMPED.
        #
        # Clamping here was a real bug and an instructive one. The log-prob above is computed on the raw
        # Gaussian sample; if the stored action is the clamped one, PPO's evaluate() later scores a
        # different action than the one act() priced, so the importance ratio is wrong on the very first
        # epoch when it should be exactly 1. The symptom was a negative KL estimate (impossible for a real
        # KL), a policy loss pinned around +0.2, and no learning at all over 400k steps.
        #
        # ActionEncoding.Decode clamps on the C# side, so the environment still only ever sees [-1,1].
        wire = torch.stack([d.float() for d in discrete] + [continuous[..., 0], continuous[..., 1]], dim=-1)
        return wire, logp, None, value

    def evaluate(self, obs: torch.Tensor, wire: torch.Tensor):
        """Log-prob, entropy and value of actions already taken — the PPO ratio's denominator."""
        logits, value = self(obs)
        logp_segments, mean = self._heads(logits)

        logp = None
        entropy = None
        for i, logp_seg in enumerate(logp_segments):
            idx = wire[..., i].long()
            picked = logp_seg.gather(-1, idx.unsqueeze(-1)).squeeze(-1)
            logp = picked if logp is None else logp + picked
            head_entropy = -(logp_seg.exp() * logp_seg).sum(-1)
            entropy = head_entropy if entropy is None else entropy + head_entropy

        std = self.log_std.exp()
        continuous = wire[..., len(layout.CATEGORICAL_HEADS) :]
        z = (continuous - mean) / std
        logp = logp + (-0.5 * z * z - self.log_std - 0.5 * self._LOG_2PI).sum(-1)
        # Differential entropy of a Gaussian: 0.5*log(2*pi*e*sigma^2), summed over dimensions.
        entropy = entropy + (self.log_std + 0.5 * (self._LOG_2PI + 1.0)).sum()
        return logp, entropy, value


class RunningNorm:
    """Streaming mean and variance of the observation, exported into the weight file.

    The C# evaluator normalises with these, so the shipped policy sees exactly the distribution it trained
    on without the runtime having to keep its own statistics.
    """

    def __init__(self, size: int):
        self.mean = np.zeros(size, dtype=np.float64)
        self.var = np.ones(size, dtype=np.float64)
        self.count = 1e-4

    def update(self, x: np.ndarray) -> None:
        batch_mean = x.mean(axis=0)
        batch_var = x.var(axis=0)
        batch_count = x.shape[0]

        delta = batch_mean - self.mean
        total = self.count + batch_count
        self.mean = self.mean + delta * batch_count / total
        m_a = self.var * self.count
        m_b = batch_var * batch_count
        self.var = (m_a + m_b + delta**2 * self.count * batch_count / total) / total
        self.count = total

    def normalize(self, x: np.ndarray) -> np.ndarray:
        return np.clip((x - self.mean) / np.sqrt(self.var + 1e-8), -10.0, 10.0).astype(np.float32)

    def state(self) -> dict:
        return {"mean": self.mean.tolist(), "var": self.var.tolist(), "count": self.count}

    def load(self, s: dict) -> None:
        self.mean = np.array(s["mean"], dtype=np.float64)
        self.var = np.array(s["var"], dtype=np.float64)
        self.count = s["count"]


def export_weights(policy: Policy, norm: RunningNorm, path: Path, label: str) -> None:
    """Write the actor in the format ``PolicyNetwork.Read`` expects.

    Layout (all little-endian, matching PolicyNetwork.cs):

        u32 magic, i32 version, string label, i32 inputSize, i32 layerCount,
        f32[inputSize] mean, f32[inputSize] std,
        per layer: i32 outSize, u8 activation, f32[out*in] weights (row-major), f32[out] biases

    The string is .NET ``BinaryWriter.Write(string)``: a 7-bit-encoded length prefix then UTF-8.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    linear_layers: list[nn.Linear] = [m for m in policy.actor_trunk if isinstance(m, nn.Linear)]
    linear_layers.append(policy.actor_head)

    with path.open("wb") as f:
        f.write(struct.pack("<I", layout.WEIGHTS_MAGIC))
        f.write(struct.pack("<i", layout.WEIGHTS_VERSION))
        _write_dotnet_string(f, label)
        f.write(struct.pack("<i", policy.obs_size))
        f.write(struct.pack("<i", len(linear_layers)))

        f.write(norm.mean.astype(np.float32).tobytes())
        f.write(np.sqrt(norm.var + 1e-8).astype(np.float32).tobytes())

        for i, lin in enumerate(linear_layers):
            act = layout.ACT_TANH if i < len(linear_layers) - 1 else layout.ACT_NONE
            f.write(struct.pack("<i", lin.out_features))
            f.write(struct.pack("<B", act))
            # torch stores Linear.weight as [out, in], which is already the row-major [out*in] the C#
            # evaluator walks. No transpose: getting this wrong produces a network that runs and is wrong.
            f.write(lin.weight.detach().cpu().numpy().astype(np.float32).ravel(order="C").tobytes())
            f.write(lin.bias.detach().cpu().numpy().astype(np.float32).tobytes())


def _write_dotnet_string(f, s: str) -> None:
    data = s.encode("utf-8")
    n = len(data)
    while n >= 0x80:
        f.write(bytes([(n & 0x7F) | 0x80]))
        n >>= 7
    f.write(bytes([n]))
    f.write(data)

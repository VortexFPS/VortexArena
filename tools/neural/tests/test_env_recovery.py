import sys
from pathlib import Path

import numpy as np

NEURAL = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(NEURAL))

import va_neural.env as env_module  # noqa: E402
from va_neural.env import EnvConfig, VectorEnv  # noqa: E402


def test_vector_env_recovery_rebuilds_and_resets_the_entire_fleet(monkeypatch):
    class OldEnv:
        def __init__(self, seed, port):
            self.cfg = EnvConfig(agents=2, seed=seed)
            self.remote = ("worker", port)
            self.closed = False

        def close(self):
            self.closed = True

    old = [OldEnv(11, 5001), OldEnv(22, 5002)]
    created = []

    class NewEnv:
        def __init__(self, cfg, host_dll=None, quiet=True, host_args=None, remote=None):
            self.cfg = cfg
            self.remote = remote
            self.agents = cfg.agents
            self.obs_size = 3
            self.closed = False
            created.append(self)

        def send_reset(self):
            pass

        def recv_reset(self):
            return np.full((self.agents, self.obs_size), self.cfg.seed, dtype=np.float32)

        def close(self):
            self.closed = True

    monkeypatch.setattr(env_module, "HostEnv", NewEnv)
    fleet = VectorEnv.__new__(VectorEnv)
    fleet.envs = old
    fleet._host_dll = None
    fleet._quiet = True
    fleet._host_args = ["--no-warps"]

    obs = fleet.recover(retry_seconds=0)

    assert all(env.closed for env in old)
    assert [env.remote for env in created] == [("worker", 5001), ("worker", 5002)]
    assert [env.cfg.seed for env in created] == [11, 22]
    assert obs.shape == (4, 3)
    assert fleet.num_agents == 4
    assert np.all(obs[:2] == 11)
    assert np.all(obs[2:] == 22)

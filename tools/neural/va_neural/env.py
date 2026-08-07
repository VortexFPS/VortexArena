"""Client for the C# environment host: launches it, talks the wire protocol, and vectorises across processes.

One host process owns one world, because ``Api.Services`` in the game is process-ambient. Scaling across
cores therefore means launching several hosts, which :class:`VectorEnv` does, and stepping them in lockstep.
"""

from __future__ import annotations

import atexit
import os
import shutil
import socket
import struct
import subprocess
import sys
import threading
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from . import layout

# Protocol.cs opcodes.
OP_HELLO = 1
OP_HELLO_ACK = 2
OP_RESET = 3
OP_OBSERVATION = 4
OP_STEP = 5
OP_STEP_RESULT = 6
OP_SET_STAGE = 7
OP_EPISODE_STATS = 8
OP_CLOSE = 9
OP_ERROR = 10

_HEADER = struct.Struct("<BI")


def _pack_str(s: str) -> bytes:
    data = s.encode("utf-8")
    if len(data) > 0xFFFF:
        raise ValueError("string too long for the HELLO frame")
    return struct.pack("<H", len(data)) + data


@dataclass
class EnvConfig:
    """Mirrors TrainingEnv.Config; sent verbatim in the HELLO frame."""

    agents: int = 8
    ticks_per_step: int = 4
    max_steps: int = 900
    stage: int = 1
    seed: int = 1
    weapon_chance: float = 1.0
    permit_flip_chance: float = 0.35
    aim_constraint_chance: float = 0.4
    trace_fan: bool = True
    # Stage 6 only: where the maps are, and which of them to train on (empty = every installed map).
    data_root: str = ""
    map_list: str = ""

    # Field order and types must match Program.ReadHello exactly: six i32, three f32, one i32, then two
    # u16-length-prefixed UTF-8 strings.
    _WIRE = struct.Struct("<6i3fi")

    def pack(self) -> bytes:
        head = self._WIRE.pack(
            layout.PROTOCOL_VERSION,
            self.agents,
            self.ticks_per_step,
            self.max_steps,
            self.stage,
            self.seed,
            self.weapon_chance,
            self.permit_flip_chance,
            self.aim_constraint_chance,
            1 if self.trace_fan else 0,
        )
        return head + _pack_str(self.data_root) + _pack_str(self.map_list)


def _default_host_binary() -> Path:
    """Where ``dotnet build -c Release`` puts the host."""
    root = Path(__file__).resolve().parents[3]
    return root / "tools" / "neural" / "VortexArena.NeuralHost" / "bin" / "Release" / "net8.0" / "va-neural-host.dll"


class HostEnv:
    """One host process and the socket to it."""

    def __init__(self, cfg: EnvConfig, host_dll: Path | None = None, quiet: bool = True,
                 host_args: list[str] | None = None):
        self.cfg = cfg
        dll = Path(host_dll) if host_dll else _default_host_binary()
        if not dll.exists():
            raise FileNotFoundError(
                f"env host not built: {dll}\n"
                f"  dotnet build tools/neural/VortexArena.NeuralHost -c Release"
            )
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise FileNotFoundError("dotnet is not on PATH")

        # --port 0 lets the OS pick, which is what makes launching a dozen of these safe. The host prints
        # the chosen port to stdout as its first line and everything else to stderr.
        self.proc = subprocess.Popen(
            [dotnet, str(dll), "--port", "0", *(host_args or [])],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL if quiet else None,
            text=True,
            bufsize=1,
        )
        atexit.register(self.close)

        line = self.proc.stdout.readline().strip() if self.proc.stdout else ""
        if not line.startswith("PORT "):
            raise RuntimeError(f"env host did not announce a port (said {line!r})")
        port = int(line.split()[1])

        # Keep draining stdout for the life of the host.
        #
        # The host only writes PORT itself, but the GAME writes to stdout too — one "[bots] waypoints for
        # ..." line per map load, and a map load is every episode. Reading the port and then never touching
        # the pipe again means those lines accumulate in a 64 KB OS buffer until it fills, at which point
        # the host blocks forever inside a write it cannot complete. The failure looked like a hard crash at
        # a perfectly reproducible step number, which is exactly what a fixed-size buffer filling at a fixed
        # rate produces.
        self._drain = threading.Thread(target=self._drain_stdout, daemon=True)
        self._drain.start()

        self.sock = socket.create_connection(("127.0.0.1", port))
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self._file = self.sock.makefile("rwb")

        self._send(OP_HELLO, cfg.pack())
        op, body = self._recv()
        if op == OP_ERROR:
            raise RuntimeError(f"env host refused the handshake: {body.decode('utf-8', 'replace')}")
        if op != OP_HELLO_ACK:
            raise RuntimeError(f"expected HELLO_ACK, got opcode {op}")

        self.obs_size, self.action_size, self.agents, self.ticks_per_step = struct.unpack("<iiii", body[:16])
        layout.verify(self.obs_size, self.action_size)

        self._obs = np.zeros((self.agents, self.obs_size), dtype=np.float32)
        self.last_episode_stats: tuple[int, float, float] | None = None

    def _drain_stdout(self) -> None:
        try:
            for _ in self.proc.stdout:   # type: ignore[union-attr]
                pass
        except (ValueError, OSError):
            pass   # the pipe closed with the host

    # -- protocol --

    def _send(self, op: int, payload: bytes = b"") -> None:
        self._file.write(_HEADER.pack(op, len(payload)))
        if payload:
            self._file.write(payload)
        self._file.flush()

    def _recv(self) -> tuple[int, bytes]:
        header = self._file.read(5)
        if not header or len(header) < 5:
            raise ConnectionError("env host closed the connection")
        op, length = _HEADER.unpack(header)
        body = self._file.read(length) if length else b""
        if length and (body is None or len(body) < length):
            raise ConnectionError("env host closed mid-frame")
        return op, body

    # -- gym-ish surface --

    def reset(self) -> np.ndarray:
        self._send(OP_RESET)
        op, body = self._recv()
        if op == OP_ERROR:
            raise RuntimeError(body.decode("utf-8", "replace"))
        if op != OP_OBSERVATION:
            raise RuntimeError(f"expected OBSERVATION, got {op}")
        self._obs = np.frombuffer(body, dtype=np.float32).reshape(self.agents, self.obs_size).copy()
        return self._obs

    def step(self, actions: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
        """``actions`` is (agents, WIRE_ACTION_SIZE) float32. Returns obs, reward, done, truncated."""
        self.send_step(actions)
        return self.recv_step()

    def send_step(self, actions: np.ndarray) -> None:
        """Write the STEP frame without waiting for the reply.

        Split from :meth:`recv_step` so :class:`VectorEnv` can put every host to work before it blocks on
        any of them. Stepping hosts one at a time serialises a round trip per host, and the round trip is
        the expensive part: in-process the env does 4,200 steps/s, over the socket a synchronous client
        gets 570.
        """
        assert actions.shape == (self.agents, layout.WIRE_ACTION_SIZE), actions.shape
        self._send(OP_STEP, np.ascontiguousarray(actions, dtype=np.float32).tobytes())

    def recv_step(self) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
        """Block for the reply to a :meth:`send_step`."""
        op, body = self._recv()
        if op == OP_ERROR:
            raise RuntimeError(body.decode("utf-8", "replace"))
        if op != OP_STEP_RESULT:
            raise RuntimeError(f"expected STEP_RESULT, got {op}")

        n, k = self.agents, self.obs_size
        off = 0
        obs = np.frombuffer(body, dtype=np.float32, count=n * k, offset=off).reshape(n, k).copy()
        off += n * k * 4
        rew = np.frombuffer(body, dtype=np.float32, count=n, offset=off).copy()
        off += n * 4
        done = np.frombuffer(body, dtype=np.uint8, count=n, offset=off).copy()
        off += n
        trunc = np.frombuffer(body, dtype=np.uint8, count=n, offset=off).copy()

        # Episode stats are part of every step result: a flag byte then arrived / mean-time / mean-remaining.
        off += n
        episode_over = body[off] != 0
        if episode_over:
            arrived, mean_time, mean_remaining = struct.unpack_from("<iff", body, off + 1)
            self.last_episode_stats = (arrived, mean_time, mean_remaining)

        self._obs = obs
        return obs, rew, done.astype(bool), trunc.astype(bool)

    def set_stage(self, stage: int) -> None:
        """Takes effect at the next reset; a mid-episode change would score a policy on a course it did not start."""
        self.cfg.stage = stage
        self._send(OP_SET_STAGE, struct.pack("<i", stage))

    def close(self) -> None:
        proc = getattr(self, "proc", None)
        if proc is None or proc.poll() is not None:
            return
        try:
            self._send(OP_CLOSE)
        except OSError:
            pass
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.kill()


class VectorEnv:
    """Several :class:`HostEnv` processes stepped in lockstep, presented as one batch of agents.

    Batch layout is ``(num_hosts * agents_per_host, ...)`` with host 0's agents first. Auto-resets: a host
    whose agents have all finished is reset on the next step and its observations replaced, which is what
    keeps every host contributing to every batch instead of the batch shrinking as episodes end.
    """

    def __init__(self, cfg: EnvConfig, num_hosts: int = 4, host_dll: Path | None = None, quiet: bool = True):
        self.envs: list[HostEnv] = []
        for i in range(num_hosts):
            # A distinct seed per host, or every host generates the identical course sequence and the batch
            # is num_hosts copies of one experience.
            sub = EnvConfig(**{**cfg.__dict__, "seed": cfg.seed + i * 1_000_003})
            self.envs.append(HostEnv(sub, host_dll=host_dll, quiet=quiet))
        self.agents_per_host = self.envs[0].agents
        self.obs_size = self.envs[0].obs_size
        self.num_agents = self.agents_per_host * len(self.envs)
        self._done_hosts = [False] * len(self.envs)

    def reset(self) -> np.ndarray:
        self._done_hosts = [False] * len(self.envs)
        return np.concatenate([e.reset() for e in self.envs], axis=0)

    def step(self, actions: np.ndarray):
        # Two phases. Every host is handed its actions before any reply is awaited, so N hosts compute
        # concurrently and the batch costs one round trip instead of N.
        for i, env in enumerate(self.envs):
            lo = i * self.agents_per_host
            env.send_step(actions[lo : lo + self.agents_per_host])

        obs_parts, rew_parts, done_parts, trunc_parts = [], [], [], []
        for env in self.envs:
            o, r, d, t = env.recv_step()
            # An all-finished host is restarted immediately so it keeps producing experience. The done flags
            # for this step are still reported, so the trainer bootstraps the value function correctly.
            if bool(np.all(d | t)):
                o = env.reset()
            obs_parts.append(o)
            rew_parts.append(r)
            done_parts.append(d)
            trunc_parts.append(t)
        return (
            np.concatenate(obs_parts, axis=0),
            np.concatenate(rew_parts, axis=0),
            np.concatenate(done_parts, axis=0),
            np.concatenate(trunc_parts, axis=0),
        )

    def set_stage(self, stage: int) -> None:
        for e in self.envs:
            e.set_stage(stage)

    def episode_stats(self) -> list[tuple[int, float, float]]:
        return [e.last_episode_stats for e in self.envs if e.last_episode_stats is not None]

    def close(self) -> None:
        for e in self.envs:
            e.close()

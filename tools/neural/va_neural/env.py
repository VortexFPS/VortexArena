"""Client for the C# environment host: launches it, talks the wire protocol, and vectorises across processes.

One host process owns one world, because ``Api.Services`` in the game is process-ambient. Scaling across
cores therefore means launching several hosts, which :class:`VectorEnv` does, and stepping them in lockstep.
"""

from __future__ import annotations

import atexit
import json
import os
import shutil
import select
import socket
import struct
import subprocess
import sys
import threading
import time
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


def _unpack_str(body: bytes, offset: int) -> str:
    """The read half of :func:`_pack_str`: a u16 length then that many UTF-8 bytes."""
    if offset + 2 > len(body):
        raise RuntimeError(
            f"truncated frame: expected a length-prefixed string at byte {offset}, got {len(body)} bytes total"
        )
    (length,) = struct.unpack_from("<H", body, offset)
    start = offset + 2
    if start + length > len(body):
        raise RuntimeError(
            f"truncated frame: a {length}-byte string at {start} runs past the {len(body)}-byte frame"
        )
    return body[start:start + length].decode("utf-8")


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


# --- locating the environment host ---------------------------------------------------------------------
#
# The trainer and the host are separate deliverables. They do not have to share a directory, a repository or
# a machine, and after the control plane moves to its own repo they normally will not
# (planning/neural-bot-lab-migration.md, step 4).
#
# This used to be `Path(__file__).resolve().parents[3]` — "count three directories up and you are at the
# VortexArena root". A fixed depth is not a lookup, it is an assumption about where this file lives, and it
# does not fail when it stops holding: it returns a confidently wrong path.

HOST_ENV_VAR = "VX_NEURAL_HOST"
HOST_CONFIG_NAME = "neural-host.json"
_HOST_BUILD_OUTPUT = Path("tools/neural/VortexArena.NeuralHost/bin/Release/net8.0/va-neural-host.dll")


def _host_from_env() -> Path | None:
    value = os.environ.get(HOST_ENV_VAR, "").strip()
    return Path(value).expanduser() if value else None


def _host_config_paths() -> list[Path]:
    """Where a pinned-host config may live, nearest first."""
    return [
        Path.cwd() / HOST_CONFIG_NAME,                                # per-run override
        Path(__file__).resolve().parent.parent / HOST_CONFIG_NAME,    # beside the trainer; moves with it
    ]


def _host_from_config() -> Path | None:
    for path in _host_config_paths():
        try:
            raw = path.read_text(encoding="utf-8")
        except OSError:
            continue
        try:
            value = str(json.loads(raw).get("host_dll", "")).strip()
        except ValueError as exc:
            raise RuntimeError(f"{path} is not valid JSON: {exc}") from exc
        if value:
            # Relative paths resolve against the config file, never the working directory, so a config that
            # sits next to a pinned build keeps working whatever directory the trainer is launched from.
            return (path.parent / Path(value).expanduser()).resolve()
    return None


CHECKOUT_MARKERS = ("VortexArena.sln", "VortexArena.csproj")


def find_enclosing_checkout(start: Path | None = None) -> Path | None:
    """The root of a VortexArena checkout above ``start``, or None if there is not one.

    Identified by a marker file rather than by counting parents. That difference is the point: a wrong
    guess returns None and lets the caller say what it actually needs, instead of handing back a path that
    does not exist and blaming the build. It stops finding anything once the trainer lives in its own
    repository, which is what makes this safe to keep through the extraction.

    ``start`` exists so the search can be tested at several depths; it defaults to this file.
    """
    origin = (start or Path(__file__)).resolve()
    for parent in origin.parents:
        if any((parent / marker).exists() for marker in CHECKOUT_MARKERS):
            return parent
    return None


def _host_from_enclosing_checkout(start: Path | None = None) -> Path | None:
    """The host build output inside an enclosing checkout, if this file still sits in one."""
    root = find_enclosing_checkout(start)
    return root / _HOST_BUILD_OUTPUT if root is not None else None


def _default_host_binary() -> Path:
    """Resolve the env host: environment, then config, then an enclosing checkout."""
    for candidate in (_host_from_env(), _host_from_config(), _host_from_enclosing_checkout()):
        if candidate is not None:
            return candidate
    raise FileNotFoundError(
        "cannot locate the neural env host. Provide it in one of these ways:\n"
        "  - HostEnv(..., host_dll=PATH), or remote=(address, port) to use a host already running\n"
        f"  - the {HOST_ENV_VAR} environment variable\n"
        f'  - a {HOST_CONFIG_NAME} containing {{"host_dll": "..."}} in the working directory,\n'
        "    or beside va_neural/\n"
        "  - run from inside a VortexArena checkout built with:\n"
        "      dotnet build tools/neural/VortexArena.NeuralHost -c Release"
    )


class HostEnv:
    """One host process and the socket to it."""

    def __init__(self, cfg: EnvConfig, host_dll: Path | None = None, quiet: bool = True,
                 host_args: list[str] | None = None, remote: tuple[str, int] | None = None):
        """``remote`` attaches to a host already listening at (address, port) instead of spawning one.

        The protocol was always a socket; only the launching was local. A worker machine runs
        tools/neural/worker.sh to bring up N hosts on consecutive ports, and the trainer dials them. Nothing
        about the wire format changes, so a remote host and a local one are the same object from here on.

        The trainer stays single: one policy, one optimiser, one batch. What moves across the network is
        simulation, which is the part that actually costs -- at 16 agents an observation frame is about 19 KB
        and a step is a single round trip, so a LAN carries this comfortably. A WAN would not: the round trip
        is synchronous and per rollout step, so latency lands directly on throughput.
        """
        self.cfg = cfg
        self.proc = None
        self.remote = remote
        if remote is not None:
            self._connect(remote[0], remote[1], cfg)
            return
        dll = Path(host_dll) if host_dll else _default_host_binary()
        if not dll.exists():
            raise FileNotFoundError(
                f"env host not found at: {dll}\n"
                f"  build it:    dotnet build tools/neural/VortexArena.NeuralHost -c Release\n"
                f"  or point at an existing build with {HOST_ENV_VAR}, a {HOST_CONFIG_NAME}, or host_dll="
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

        self._connect("127.0.0.1", port, cfg)

    def _connect(self, address: str, port: int, cfg: EnvConfig) -> None:
        self.sock = socket.create_connection((address, port), timeout=30)
        self.sock.settimeout(None)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        # Room for a whole step result without the writer blocking.
        #
        # At 64 agents an observation frame is 64 x 206 x 4 = 53 KB, which overruns the default socket
        # buffer. The host then blocks part-way through its write until someone reads, and VectorEnv reads
        # its hosts strictly in order -- so hosts 1..N-1 sat stalled mid-write while host 0 was drained.
        # Measured before this: six hosts at ~10% CPU each on a 28-vCPU box that was 96% idle, with the env
        # phase at 0.98 s per iteration for about 0.8 ms of actual host work per step.
        for opt in (socket.SO_RCVBUF, socket.SO_SNDBUF):
            try:
                self.sock.setsockopt(socket.SOL_SOCKET, opt, 4 << 20)
            except OSError:
                pass   # the OS may clamp this; the larger buffer is an optimisation, not a requirement
        self._file = self.sock.makefile("rwb", buffering=1 << 20)

        self._send(OP_HELLO, cfg.pack())
        op, body = self._recv()
        if op == OP_ERROR:
            raise RuntimeError(f"env host refused the handshake: {body.decode('utf-8', 'replace')}")
        if op != OP_HELLO_ACK:
            raise RuntimeError(f"expected HELLO_ACK, got opcode {op}")

        self.obs_size, self.action_size, self.agents, self.ticks_per_step = struct.unpack("<iiii", body[:16])
        # Protocol 2: the descriptor rides after the four fixed i32s. It is what catches a size-preserving
        # layout change, which the sizes alone cannot see.
        self.layout_descriptor = _unpack_str(body, 16)
        layout.verify(self.obs_size, self.action_size, self.layout_descriptor)

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

    def send_reset(self) -> None:
        """Ask for a reset without waiting for it.

        Split from recv_reset so a fleet of hosts can rebuild concurrently. A reset is a whole GameWorld
        teardown and rebuild plus a nav flood -- 280 to 650 ms measured -- and doing them one at a time
        leaves every other host idle for the duration.
        """
        self._send(OP_RESET)

    def recv_reset(self) -> np.ndarray:
        op, body = self._recv()
        if op == OP_ERROR:
            raise RuntimeError(body.decode("utf-8", "replace"))
        if op != OP_OBSERVATION:
            raise RuntimeError(f"expected OBSERVATION, got {op}")
        self._obs = np.frombuffer(body, dtype=np.float32).reshape(self.agents, self.obs_size).copy()
        return self._obs

    def reset(self) -> np.ndarray:
        self.send_reset()
        return self.recv_reset()

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
        # Idempotent, and it has to be. recover() closes the whole fleet, then atexit closes it again on the
        # way out; a partially-constructed env from a failed __init__ gets closed too. A second pass must be
        # silent rather than raise out of a recovery path whose entire job is surviving a failure.
        #
        # getattr rather than an __init__ flag on purpose: __init__ can raise before any attribute is set,
        # and those objects still reach this method.
        if getattr(self, "_closed", False):
            return
        self._closed = True
        # A remote host has no process here to reap, but it still needs telling: without OP_CLOSE it sits on
        # a half-open socket and the worker cannot reuse the port until someone notices.
        #
        # ValueError joins the caught set because a write to an already-closed buffered file raises that, not
        # OSError -- which is how the second close() used to escape as a traceback.
        try:
            self._send(OP_CLOSE)
        except (OSError, AttributeError, ValueError):
            pass
        # makefile() owns a buffered wrapper around the socket.  Closing only the process (the old local-
        # host behaviour) leaves a remote connection half-open until GC, which delays the worker's respawn
        # loop and can make an immediate trainer recovery race the still-occupied port.
        try:
            self._file.close()
        except (OSError, AttributeError, ValueError):
            pass
        try:
            self.sock.close()
        except (OSError, AttributeError, ValueError):
            pass
        proc = getattr(self, "proc", None)
        if proc is None or proc.poll() is not None:
            return
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

    @staticmethod
    def parse_remotes(specs: list[str] | None) -> list[tuple[str, int]]:
        """``host:port`` or ``host:port:count`` (consecutive ports) into a flat endpoint list."""
        out: list[tuple[str, int]] = []
        for spec in specs or []:
            parts = spec.split(":")
            if len(parts) not in (2, 3):
                raise ValueError(f"--remote wants host:port or host:port:count, got {spec!r}")
            addr, port = parts[0], int(parts[1])
            count = int(parts[2]) if len(parts) == 3 else 1
            out.extend((addr, port + i) for i in range(count))
        return out

    def __init__(self, cfg: EnvConfig, num_hosts: int = 4, host_dll: Path | None = None, quiet: bool = True,
                 host_args: list[str] | None = None, remotes: list[tuple[str, int]] | None = None):
        """host_args goes verbatim to every host process.

        Router and observation switches live on the host, not in the trainer, so without this a run cannot
        hold them fixed. That matters when RESUMING: a policy is only meaningful against the observations it
        was trained on, and turning a router change on under a resumed policy silently mixes a distribution
        shift into whatever the run was supposed to be measuring.
        """
        self.envs: list[HostEnv] = []
        self._host_dll = host_dll
        self._quiet = quiet
        self._host_args = list(host_args or [])
        remotes = remotes or []
        # Remote hosts first, then local ones, and the seed stride runs across BOTH -- a remote host that
        # reused a local host's seed would generate the identical course sequence and the batch would hold
        # two copies of one experience, which is the failure this stride exists to prevent.
        for i, (addr, port) in enumerate(remotes):
            sub = EnvConfig(**{**cfg.__dict__, "seed": cfg.seed + i * 1_000_003})
            self.envs.append(HostEnv(sub, remote=(addr, port)))
        for j in range(num_hosts):
            i = len(remotes) + j
            sub = EnvConfig(**{**cfg.__dict__, "seed": cfg.seed + i * 1_000_003})
            self.envs.append(HostEnv(sub, host_dll=host_dll, quiet=quiet, host_args=host_args))
        if not self.envs:
            raise ValueError("no hosts: --hosts is 0 and no --remote endpoints were given")
        self.agents_per_host = self.envs[0].agents
        self.obs_size = self.envs[0].obs_size
        self.num_agents = self.agents_per_host * len(self.envs)
        self._done_hosts = [False] * len(self.envs)
        self.last_transition_obs = np.zeros((self.num_agents, self.obs_size), dtype=np.float32)

        # Per-host reply timing, off unless VX_HOST_TIMING is set. See _record_host_times.
        self._timing = os.environ.get("VX_HOST_TIMING", "") not in ("", "0")
        self._t_max: list[float] = []
        self._t_mean: list[float] = []

    def recover(self, retry_seconds: float = 45.0) -> np.ndarray:
        """Rebuild every connection and reset the fleet after a transport failure.

        A STEP has already been sent to every host when one receive fails, so reconnecting only the failed
        slot would leave the surviving streams at different protocol positions.  Close and rebuild the
        whole fleet instead, then reset every world to one clean boundary.  The trainer discards the
        incomplete rollout; no fabricated rewards or terminal flags enter PPO.

        Remote workers run each port in a respawn loop and normally listen again after one second.  Retry
        connection-refused errors within a bounded window so that normal respawn delay is recovery, while a
        genuinely dead worker still fails closed and leaves the trainer's checkpoint safe.
        """
        specs = [(EnvConfig(**env.cfg.__dict__), env.remote) for env in self.envs]
        for env in self.envs:
            env.close()

        rebuilt: list[HostEnv] = []
        try:
            for cfg, remote in specs:
                deadline = time.monotonic() + retry_seconds
                while True:
                    try:
                        rebuilt.append(HostEnv(
                            cfg,
                            host_dll=self._host_dll,
                            quiet=self._quiet,
                            host_args=self._host_args,
                            remote=remote,
                        ))
                        break
                    except (ConnectionError, OSError):
                        if remote is None or time.monotonic() >= deadline:
                            raise
                        time.sleep(0.5)
        except Exception:
            for env in rebuilt:
                env.close()
            self.envs = rebuilt
            raise

        self.envs = rebuilt
        self.agents_per_host = self.envs[0].agents
        self.obs_size = self.envs[0].obs_size
        self.num_agents = self.agents_per_host * len(self.envs)
        self._done_hosts = [False] * len(self.envs)
        self.last_transition_obs = np.zeros((self.num_agents, self.obs_size), dtype=np.float32)
        return self.reset()

    def _record_host_times(self) -> None:
        """When each host's reply becomes readable, relative to the moment they were all sent.

        This is the measurement that decides whether more machines would help. The env steps in LOCKSTEP:
        every host must answer before the batch advances, so a step costs max(host times), not mean. Hosts
        draw maps ranging from 13k to 55k spans, so the spread may be wide -- and if it is, utilisation is
        capped by the slowest host and ADDING hosts makes the max worse, not better. A host-count sweep
        already found CPU pinned at 58-62% from 32 to 80 hosts with throughput flattening; this says whether
        stragglers are the reason.

        Timing readiness rather than the receive call is the whole point: receives happen in host order, so
        timing them measures queueing behind host 0 rather than how long each host actually took.
        """
        pending = {e.sock.fileno(): e for e in self.envs}
        t0 = time.perf_counter()
        seen: list[float] = []
        while pending:
            ready, _, _ = select.select(list(pending), [], [], 30.0)
            if not ready:
                break            # a host died or wedged; the receive below will fail with a clearer message
            now = time.perf_counter() - t0
            for fd in ready:
                pending.pop(fd, None)
                seen.append(now)
        if seen:
            self._t_max.append(max(seen))
            self._t_mean.append(sum(seen) / len(seen))

    def timing_report(self) -> str:
        if not self._t_max:
            return "host timing: no samples (set VX_HOST_TIMING=1)"
        n = len(self._t_max)
        mx = sum(self._t_max) / n
        mn = sum(self._t_mean) / n
        idle = (1.0 - mn / mx) * 100.0 if mx > 0 else 0.0
        return (f"host timing over {n} steps: slowest {mx * 1000:.1f} ms, average {mn * 1000:.1f} ms, "
                f"so {idle:.0f}% of each step is hosts idle waiting on the straggler")

    def collect(self, T, obs, act_fn, store):
        """Fill a T-step rollout with every host free-running; returns the final observation batch.

        ``act_fn(rows)`` maps observation rows to (wire_actions, aux) where aux is whatever the trainer
        wants stored alongside (log-probs, values). ``store(t, lo, hi, obs, next_obs, act, aux, rew, done,
        trunc)`` writes one host's transition at its own step index t. Rows for host i live at
        [i*A, (i+1)*A).

        The select loop batches act_fn across every host whose reply arrived in the same wakeup, so the
        policy forward keeps most of its batching even though hosts no longer march in step.
        """
        import select as _select

        A = self.agents_per_host
        n = len(self.envs)
        cur = [obs[i * A:(i + 1) * A].copy() for i in range(n)]
        t_i = [0] * n
        phase = ["step"] * n          # step: owes a step reply; reset: owes a reset reply; done: window over
        fd_to_i = {e.sock.fileno(): i for i, e in enumerate(self.envs)}
        last = [None] * n

        # Prime: one action per host, batched in a single forward.
        acts, aux = act_fn(np.concatenate(cur, axis=0), list(range(n)))
        for i, e in enumerate(self.envs):
            e.send_step(acts[i * A:(i + 1) * A])
        pending_aux = [
            (acts[i * A:(i + 1) * A].copy(),
             tuple(x[i * A:(i + 1) * A].copy() for x in aux)) for i in range(n)
        ]

        while any(ph != "done" for ph in phase):
            waiting = [fd for fd, i in fd_to_i.items() if phase[i] != "done"]
            ready, _, _ = _select.select(waiting, [], [], 60.0)
            if not ready:
                slow = [self.envs[i].cfg.seed for i in range(n) if phase[i] != "done"]
                raise RuntimeError(f"no host replied for 60s; {len(slow)} still owed a reply")

            stepped = []      # hosts that just delivered a transition and need a NEXT action
            for fd in ready:
                i = fd_to_i[fd]
                e = self.envs[i]
                if phase[i] == "reset":
                    cur[i] = e.recv_reset()
                    phase[i] = "step"
                    stepped.append(i)     # a reset reply is followed by an action on the fresh obs
                    continue

                o, r, d, tr = e.recv_step()
                act_i, aux_i = pending_aux[i]
                store(t_i[i], i * A, (i + 1) * A, cur[i], o, act_i, aux_i, r, d, tr)
                t_i[i] += 1
                cur[i] = o

                if t_i[i] >= T:
                    phase[i] = "done"
                    last[i] = o
                elif bool(np.all(d | tr)):
                    # Whole host finished its episode: rebuild before the next transition, exactly as the
                    # synchronous path did -- the reset observation replaces the terminal one.
                    e.send_reset()
                    phase[i] = "reset"
                else:
                    stepped.append(i)

            if stepped:
                rows = np.concatenate([cur[i] for i in stepped], axis=0)
                acts, aux = act_fn(rows, stepped)
                for k, i in enumerate(stepped):
                    a_rows = acts[k * A:(k + 1) * A]
                    pending_aux[i] = (a_rows.copy(),
                                      tuple(x[k * A:(k + 1) * A].copy() for x in aux))
                    self.envs[i].send_step(a_rows)

        return np.concatenate(last, axis=0)

    def reset(self) -> np.ndarray:
        self._done_hosts = [False] * len(self.envs)
        # Every host rebuilds at once rather than in turn: N resets cost one reset's wall time, not N.
        for e in self.envs:
            e.send_reset()
        return np.concatenate([e.recv_reset() for e in self.envs], axis=0)

    def step(self, actions: np.ndarray):
        # Two phases. Every host is handed its actions before any reply is awaited, so N hosts compute
        # concurrently and the batch costs one round trip instead of N.
        for i, env in enumerate(self.envs):
            lo = i * self.agents_per_host
            env.send_step(actions[lo : lo + self.agents_per_host])

        if self._timing:
            self._record_host_times()

        obs_parts, rew_parts, done_parts, trunc_parts = [], [], [], []
        needs_reset = []
        for i, env in enumerate(self.envs):
            o, r, d, t = env.recv_step()
            # An all-finished host is restarted so it keeps producing experience. The done flags for this
            # step are still reported, so the trainer bootstraps the value function correctly.
            #
            # The restart is DEFERRED rather than done here. A reset blocks for a world teardown, rebuild and
            # nav flood -- 280 to 650 ms -- and doing it inside this loop stalls the whole fleet, because
            # every other host has already replied and is sitting idle waiting for the next action batch.
            # Measured before this: 34% CPU across 28 cores with 33 of 37 processes asleep and no I/O wait.
            if bool(np.all(d | t)):
                needs_reset.append(i)
            obs_parts.append(o)
            rew_parts.append(r)
            done_parts.append(d)
            trunc_parts.append(t)

        # Preserve the post-step observations before an all-done host is replaced by its reset. The PPO
        # timeout bootstrap needs this exact state; using the reset observation crosses episode boundaries.
        self.last_transition_obs = np.concatenate(obs_parts, axis=0).copy()

        # Same two-phase shape as the step itself: ask everyone, then collect.
        if needs_reset:
            for i in needs_reset:
                self.envs[i].send_reset()
            for i in needs_reset:
                obs_parts[i] = self.envs[i].recv_reset()
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

    def clear_episode_stats(self) -> None:
        """Forget the last episode summary on every host.

        The deterministic eval counts episodes as they finish, so a stale summary left over from the
        previous poll would be counted twice and inflate the arrival rate the curriculum gate reads.
        """
        for e in self.envs:
            e.last_episode_stats = None

    def close(self) -> None:
        for e in self.envs:
            e.close()

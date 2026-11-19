"""Measure normal client connection attempts to a silent localhost UDP socket."""
import argparse
import hashlib
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[4]
PIN = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
STOCK_HASHES = {
    "xonotic-linux64-sdl": "79e38b239b8dc5d5ee1ac8ada8245a51381ce27277dc165645fea0e69fed4312",
    "compiled/progs.dat": "c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7",
    "compiled/csprogs.dat": "cbc9fd9a4e2c7fc8f02f4aab6e5883e549790eb24e71be2bc0b8a0b0fea89999",
    "compiled/menu.dat": "4d502424af24f90c36c8a17a034ded536571a0d353a4192d8b332e8cc8b2d771",
}


def check_stock_attempts(relative):
    groups = []
    for stamp in relative:
        if not groups or stamp - groups[-1][-1] > 0.4:
            groups.append([])
        groups[-1].append(stamp)
    if len(groups) != 12 or any(len(group) != 2 for group in groups):
        raise ValueError("expected twelve release-client attempts of two datagrams each")
    if any(not 0.8 <= later[0] - earlier[0] <= 1.5 for earlier, later in zip(groups, groups[1:])):
        raise ValueError("stock retry cadence fell outside the fixture's measurement window")
    return [group[0] for group in groups]


def check_native_wait(elapsed, setting):
    # This bounds a repeat of the low-load fixture. A slow or descheduled
    # process needs a new measurement before it can support the comparison.
    if not setting - 0.4 <= elapsed <= setting + 1.0:
        raise ValueError(f"observed {elapsed:.2f}s for a {setting}s native timeout")


def stop_client_or_server(proc):
    if proc.poll() is not None:
        return
    try:
        os.killpg(proc.pid, signal.SIGCONT)
        os.killpg(proc.pid, signal.SIGTERM)
        proc.wait(timeout=8)
    except ProcessLookupError:
        pass
    except subprocess.TimeoutExpired:
        os.killpg(proc.pid, signal.SIGKILL)
        proc.wait()


def trial(engine, timeout, stage, args):
    profile = stage / f"{engine}-{timeout}"
    profile.mkdir()
    env = dict(os.environ, VORTEX_USERDIR=str(profile), VORTEX_NO_MASTER_QUERY="1",
               XDG_DATA_HOME=str(profile / "xdg"), XDG_CACHE_HOME=str(profile / "cache"),
               XDG_CONFIG_HOME=str(profile / "config"), LIBGL_ALWAYS_SOFTWARE="1", LP_NUM_THREADS="1")
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sink:
        sink.bind(("127.0.0.1", 0))
        sink.settimeout(0.2)
        address = f"127.0.0.1:{sink.getsockname()[1]}"
        if engine == "stock":
            env["LD_LIBRARY_PATH"] = str(args.stock / "sdl/usr/lib/x86_64-linux-gnu")
            cmd = ["xvfb-run", "-a", args.stock / "xonotic-linux64-sdl", "-xonotic",
                   "-basedir", stage, "-userdir", profile, "-nosound", "-nojoy", "-window",
                   "-width", "480", "-height", "320", "+net_connecttimeout", str(timeout),
                   "+cl_allow_uidtracking", "0", "+connect", address]
        else:
            cmd = [args.godot, "--headless", "--path", ROOT, "--connect", address, "--no-save-config",
                   "--cvar", "net_connecttimeout", str(timeout), "--cvar", "cl_allow_uidtracking", "0",
                   "--cvar", "gl_texturecompression", "0"]
        log = profile / "client.log"
        with log.open("wb") as output:
            proc = subprocess.Popen(cmd, stdout=output, stderr=subprocess.STDOUT, env=env,
                                    stdin=subprocess.DEVNULL, start_new_session=True)
        packets = []
        start = time.monotonic()
        ready_at = None
        try:
            while time.monotonic() - start < 90:
                try:
                    sink.recvfrom(65535)
                    packets.append(time.monotonic())
                except socket.timeout:
                    pass
                now = time.monotonic()
                if engine == "stock" and packets and now - packets[-1] > 5:
                    break
                if engine == "native":
                    text = log.read_text(errors="replace")
                    if ready_at is None and "[LoadTimeline] connect " in text and "load begin → ready" in text:
                        ready_at = now
                    if "[NetGame] connection failed:" in text:
                        break
                if proc.poll() is not None:
                    raise RuntimeError(f"client exited before the observation completed: {log}")
            else:
                raise RuntimeError(f"observation timed out: {log}")
            if not packets:
                raise RuntimeError(f"client sent no datagrams: {log}")
            # A stock attempt sends two datagrams together. Group nearby sends
            # only for reporting, preserving the distinct original timestamps.
            relative = [stamp - packets[0] for stamp in packets]
            groups = check_stock_attempts(relative) if engine == "stock" else relative
            print(f"{engine} cvar={timeout}, datagrams={len(packets)}, send groups={[round(t, 2) for t in groups]}, "
                  f"last send={relative[-1]:.2f}s, observation ended={now-packets[0]:.2f}s", flush=True)
            if engine == "native":
                if ready_at is None:
                    raise RuntimeError(f"no load-ready marker before client failure: {log}")
                print(f"load-ready to failure={now - ready_at:.2f}s", flush=True)
                reason = next(line for line in text.splitlines() if "[NetGame] connection failed:" in line)
                print(reason, flush=True)
                if "Couldn't connect to the server." not in reason:
                    raise RuntimeError(f"client left by a different failure path: {log}")
                check_native_wait(now - ready_at, timeout)
        finally:
            stop_client_or_server(proc)


def connected_legacy_trial(stage, args):
    """Pause the fixture's stock server after a normal legacy client reaches signon stage four."""
    data = stage / "data"
    for name in ("stormkeep.pk3", "shared.pk3"):
        (data / name).symlink_to((args.stock / "demo-data" / name).resolve(strict=True))
    client_data = stage / "client-data"
    client_data.mkdir()
    for item in data.iterdir():
        (client_data / item.name).symlink_to(item.resolve())
    (client_data / "default.cfg").write_bytes((args.base / "default.cfg").read_bytes() +
        b"\nnet_connecttimeout 20\nnet_messagetimeout 18\ncl_allow_uidtracking 0\ncl_allow_uid2name 0\ncl_allow_uidranking 0\n")
    env = dict(os.environ, VORTEX_USERDIR=str(stage / "profile"), VORTEX_NO_MASTER_QUERY="1",
               XDG_CONFIG_HOME=str(stage / "config"), XDG_DATA_HOME=str(stage / "xdg"),
               XDG_CACHE_HOME=str(stage / "cache"), LIBGL_ALWAYS_SOFTWARE="1", LP_NUM_THREADS="1")
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]

    def launch(log, cmd, environment):
        with log.open("wb") as output:
            return subprocess.Popen(cmd, stdout=output, stderr=subprocess.STDOUT, env=environment,
                                    stdin=subprocess.DEVNULL, start_new_session=True)

    client_log = stage / "legacy-client.log"
    def wait_for(client, marker, seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if marker in client_log.read_text(errors="replace"):
                return
            if client.poll() is not None:
                break
            time.sleep(0.05)
        raise RuntimeError(f"client did not reach {marker!r}: {client_log}")

    server = launch(stage / "stock-server.log", [
        args.stock / "xonotic-linux64-sdl", "-xonotic", "-basedir", stage, "-userdir", stage / "server",
        "-dedicated", "-nosound", "-nojoy", "-ip", "127.0.0.1", "-ip6", "::1", "-port", str(port),
        "+sv_public", "0", "+g_start_delay", "0", "+g_warmup", "0", "+sv_autopause", "0",
        "+sv_jumpspeedcap_min", "-1", "+sv_jumpspeedcap_max", "100000",
        "+g_playerstats_gamereporting_uri", "", "+g_playerstats_playerbasic_uri", "", "+map", "stormkeep"
    ], dict(env, LD_LIBRARY_PATH=str(args.stock / "sdl/usr/lib/x86_64-linux-gnu")))
    try:
        client = launch(client_log, [args.godot, "--headless", "--path", ROOT, "--legacy-data", client_data,
                                     "--legacy-connect", f"127.0.0.1:{port}", "--no-save-config"], env)
        try:
            wait_for(client, "signon stage 4", 100)
            time.sleep(1)
            start = time.monotonic()
            os.kill(server.pid, signal.SIGSTOP)
            wait_for(client, "TimedOut (server stopped responding)", 26)
            elapsed = time.monotonic() - start
            print(f"Legacy client: signon stage 4, server paused, timeout after {elapsed:.2f}s", flush=True)
            if not 17 <= elapsed <= 20:
                raise ValueError("elapsed time fell outside the 18-second message-timeout fixture")
        finally:
            stop_client_or_server(client)
    finally:
        stop_client_or_server(server)


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    cli.add_argument("--stock", type=Path, default=Path("/tmp/va-reviewed-stock"))
    cli.add_argument("--godot", required=True, type=Path)
    cli.add_argument("--legacy-loss", action="store_true", help="join and pause the stock server on Stormkeep instead of trying a silent port")
    args = cli.parse_args()
    if subprocess.check_output(["git", "-C", args.base, "rev-parse", "HEAD"], text=True).strip() != PIN:
        cli.error("expected the pinned Xonotic data checkout")
    for name, expected in STOCK_HASHES.items():
        if hashlib.sha256((args.stock / name).read_bytes()).hexdigest() != expected:
            cli.error(f"stock input differs from the recorded release fixture: {name}")
    stage = Path(tempfile.mkdtemp(prefix="va-connection-timing-"))
    data = stage / "data"
    data.mkdir()
    (data / "base.pk3dir").symlink_to(args.base.resolve())
    (data / "zz-code.pk3dir").symlink_to((args.stock / "compiled").resolve())
    print(f"Client logs in {stage}", flush=True)
    if args.legacy_loss:
        connected_legacy_trial(stage, args)
        return
    for timeout in (12, 22):
        for engine in ("stock", "native"):
            trial(engine, timeout, stage, args)


if __name__ == "__main__":
    main()

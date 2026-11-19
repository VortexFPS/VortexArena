#!/usr/bin/env python3
"""Run the same bot script on two real dedicated servers and inspect received sound messages."""
import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import struct
import subprocess
import threading
import time
import wave
from xml.sax.saxutils import escape

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
BASE = Path("/tmp/vortex-parity-base")
PIN = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
SAMPLES = ("botprobe/default.wav", "botprobe/explicit.wav")
ANSI = re.compile(r"\x1b\[[0-9;]*m")
MARK = re.compile(r"(?::chat:(\d+):|^(\[BOT\][^:]+): cc say )(BOTPROBE_\w+)")


def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def stage_cues(path):
    (path / "sound/botprobe").mkdir(parents=True)
    shutil.copyfile(HERE / "cues.bot", path / "cues.bot")
    for frequency, sample in zip((480, 800), SAMPLES):
        with wave.open(str(path / "sound" / sample), "wb") as cue:
            cue.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            # 125 ms square wave, real sound data with a distinct pitch for each cue.
            cycle = 16000 // frequency
            cue.writeframes(b"".join(struct.pack("<h", 2000 if i % cycle < cycle // 2 else -2000)
                                     for i in range(2000)))


def stage_original(folder, code):
    data = folder / "data"
    stage_cues(data)
    (data / "zz-pinned.pk3dir").symlink_to(BASE)
    (data / "zzz-compiled.pk3dir").symlink_to(code)
    for path in [ROOT / "data/maps/darkzone.pk3", ROOT / "data/maps/shared.pk3",
                 *(ROOT / "data").glob("font-*.pk3dir")]:
        (data / path.name).symlink_to(path)
    assets = data / "00-assets.pk3dir"
    assets.mkdir()
    for name in ("models", "sound", "gfx", "particles", "scripts", "textures", "cubemaps"):
        (assets / name).symlink_to(ROOT / "data/core.pk3dir" / name)
    (data / "offline.cfg").write_text("\n".join(
        f'set {key} "{value}"' for key, value in {
            "sv_public": 0, "sv_autopause": 0, "cl_curl_enabled": 0, "bot_join_empty": 1,
            "net_address_ipv6": "::1",
            "bot_nofire": 1, "sv_eventlog": 1, "sv_eventlog_console": 1,
            "g_warmup": 0, "g_start_delay": 0, "g_dm": 1, "sys_ticrate": "0.033333",
            "g_debug_bot_commands": 1, "g_chat_flood_spl": 0, "g_chat_flood_burst": 99,
            "g_playerstats_gamereport_uri": "", "g_playerstats_playerbasic_uri": "",
            "g_playerstats_playerdetail_uri": "",
            **{f"sv_master{i}": "" for i in range(1, 5)},
            "sv_masterextra1": "", "sv_masterextra2": "",
        }.items()) + "\n")
    return data


def profile(args, path, port):
    path.mkdir(parents=True, exist_ok=True)
    return os.environ | {
        "XDG_DATA_HOME": str(path / "xdg-data"), "XDG_CONFIG_HOME": str(path / "xdg-config"),
        "XDG_CACHE_HOME": str(path / "xdg-cache"), "VORTEX_USERDIR": str(path / "user"),
        "DOTNET_ROLL_FORWARD": "Major", "LD_PRELOAD": str(args.loopback_shim),
        "FIXTURE_PORT": str(port),
    }


class Log:
    def __init__(self, stack, argv, env, folder, name, now):
        self.path = folder / f"{name}.log"
        self.lines = []
        self.proc = subprocess.Popen(list(map(str, argv)), cwd=ROOT, stdout=subprocess.PIPE,
                                     stderr=subprocess.STDOUT, stdin=subprocess.PIPE, env=env)
        stack.callback(self.close)
        self.worker = threading.Thread(target=self.read, args=(now,), daemon=True)
        self.worker.start()

    def read(self, now):
        with self.path.open("w") as out:
            for raw in self.proc.stdout:
                line = ANSI.sub("", raw.decode(errors="replace")).strip()
                timestamp = now()
                self.lines.append((timestamp, line))
                out.write(f"{timestamp:7.3f} {line}\n")
                out.flush()

    def expect(self, marker, timeout=45):
        self.wait_until(lambda: any(marker in line for _, line in self.lines), marker, timeout)

    def wait_until(self, condition, marker, timeout):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if condition():
                return
            if self.proc.poll() is not None:
                break
            time.sleep(0.05)
        raise RuntimeError(f"{self.path}: missing {marker!r}; exit={self.proc.poll()}")

    def send(self, line):
        self.proc.stdin.write((line + "\n").encode())
        self.proc.stdin.flush()

    def close(self):
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=4)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait()
        self.worker.join()


def original_observer(folder, assembly):
    project = folder / "observer-build"
    project.mkdir()
    shutil.copyfile(HERE / "ObserveOriginal.cs", project / "Program.cs")
    refs = "".join(f'<Reference Include="{path.stem}"><HintPath>{escape(str(path))}</HintPath></Reference>'
                   for path in sorted(assembly.glob("VortexArena.*.dll")))
    (project / "Observer.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<Nullable>enable</Nullable></PropertyGroup><ItemGroup>'
        + refs + '</ItemGroup></Project>')
    subprocess.run(["dotnet", "build", project / "Observer.csproj", "--source", str(project), "-p:NuGetAudit=false", "-v:q"], check=True)
    return project / "bin/Debug/net10.0/Observer.dll"


class NativeWire:
    """Forward a loopback ENet session and extract the SoundWire records for the fixture."""
    def __init__(self, stack, server, now):
        self.socket = stack.enter_context(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
        self.socket.bind(("127.0.0.1", 0))
        self.socket.settimeout(0.1)
        self.port = self.socket.getsockname()[1]
        self.cues = []
        self.done = threading.Event()
        self.worker = threading.Thread(target=self.relay, args=(server, now), daemon=True)
        self.worker.start()
        stack.callback(self.close)

    def close(self):
        self.done.set()
        self.worker.join()

    def relay(self, server, now):
        client = None
        while not self.done.is_set():
            try:
                packet, sender = self.socket.recvfrom(65536)
            except TimeoutError:
                continue
            if sender == server and client is not None:
                self.record(packet, now())
                self.socket.sendto(packet, client)
            else:
                client = sender
                self.socket.sendto(packet, server)

    def record(self, packet, elapsed):
        # A cue is always the first sound in this isolated bundle. A same-tick
        # release can put both records into the bundle instead of two datagrams.
        for sample in SAMPLES:
            encoded = sample.encode()
            prefix = struct.pack("<H", len(encoded)) + encoded
            offset = packet.find(prefix)
            if offset < 3 or packet[offset - 3] != 14 or packet[offset - 2] not in (1, 2):
                continue
            for _ in range(packet[offset - 2]):
                length, = struct.unpack_from("<H", packet, offset)
                start = offset + 2 + length
                name = packet[offset + 2:start].decode("ascii", errors="replace")
                _, _, _, volume, attenuation, channel, entity, flags = struct.unpack_from("<5fbHB", packet, start)
                if name in SAMPLES and flags == 0:
                    self.cues.append((elapsed, f"CUE {name} entity={entity} channel={channel} "
                                               f"volume={volume:.6g} attenuation={attenuation:.6g}"))
                offset = start + 24 + bool(flags & 4)
            return


def extract(engine, load_time, server, cues, file):
    markers = {}
    evidence = []
    for time_seen, line in server.lines:
        if time_seen < load_time:
            continue
        match = MARK.search(line)
        if match:
            numeric, name, label = match.groups()
            markers.setdefault(label, []).append((time_seen, numeric or name))
            evidence.append((time_seen, f"{numeric or name} {label}"))
        elif "commands read" in line or "players:" in line:
            evidence.append((time_seen, line))
    evidence.extend(cues)
    file.write_text("".join(f"{when - load_time:+.3f} {line}\n" for when, line in sorted(evidence)))
    assert "BOTPROBE_unexpected_branch" not in markers, engine
    assert len(markers["BOTPROBE_relative_wait_done"]) == 1, engine
    for phase in ("start", "barrier_one", "absolute_wait_done", "cue_release", "end"):
        simultaneous = markers[f"BOTPROBE_{phase}"]
        assert len(simultaneous) == 2 and simultaneous[0][1] != simultaneous[1][1], (engine, phase)
        assert 0 <= simultaneous[1][0] - simultaneous[0][0] < 0.25, (engine, phase)
    first = min(when for when, _ in markers["BOTPROBE_start"])
    late = markers["BOTPROBE_relative_wait_done"][0][0]
    barrier = min(when for when, _ in markers["BOTPROBE_barrier_one"])
    absolute = min(when for when, _ in markers["BOTPROBE_absolute_wait_done"])
    release = min(when for when, _ in markers["BOTPROBE_cue_release"])
    assert 0.58 <= late - first < 1.15 and 0 <= barrier - late < 0.18, engine
    assert 0.28 <= absolute - barrier < 0.6 and 0.34 <= release - absolute < 0.8, engine
    assert len(cues) == 2 and all(abs(when - release) < 0.3 for when, _ in cues), engine
    expected_volume = ("178/255", "127/255") if engine == "original" else ("0.7", "0.5")
    for sample, channel, volume, attenuation in zip(SAMPLES, (-1, 4), expected_volume, ("0.015625", "2")):
        assert any(f"CUE {sample}" in line and
                   line.endswith(f"channel={channel} volume={volume} attenuation={attenuation}")
                   for _, line in cues), (engine, cues)


def run(args, engine, manifest, port):
    folder = args.output / engine
    folder.mkdir()
    started = time.monotonic()

    def now():
        return time.monotonic() - started

    with contextlib.ExitStack() as stack:
        env = profile(args, folder / "server-profile", port)
        if engine == "original":
            data = stage_original(folder / "basedir", args.code)
            server_args = [args.darkplaces, "-dedicated", "-xonotic", "-basedir", data.parent,
                           "-userdir", env["VORTEX_USERDIR"], "-ip", "127.0.0.1", "-port", port,
                           "+exec", "offline.cfg", "+map", "darkzone"]
            server = Log(stack, server_args, env, folder, "server", now)
            server.expect("execing post-config.cfg")
            dll = original_observer(folder, args.assemblies)
            client_env = profile(args, folder / "client-profile", port)
            client = Log(stack, ["dotnet", dll, port, data, client_env["VORTEX_USERDIR"]],
                         client_env, folder, "observer", now)
            client.expect("OBSERVER_READY")
        else:
            stage_cues(Path(env["VORTEX_USERDIR"]) / "data/botprobe.pk3dir")
            base = [args.godot, "--headless", "--path", ROOT, "--audio-driver", "Dummy", "--no-save-config"]
            server_args = base + ["--dedicated", "darkzone", "--gametype", "dm", "--port", port, "--bots", 0]
            for key, value in {
                "bot_join_empty": 1, "bot_nofire": 1, "sv_public": 0, "g_warmup": 0, "g_start_delay": 0,
                "sv_eventlog": 1, "sv_eventlog_console": 1, "g_chat_flood_spl": 0, "g_chat_flood_burst": 99,
                "sys_ticrate": "0.033333", "cl_frameprofiler": 0, "bot_neural_bake": 0,
            }.items():
                server_args.extend(("--cvar", key, value))
            wire = NativeWire(stack, ("127.0.0.1", port), now)
            server = Log(stack, server_args, env, folder, "server", now)
            server.expect("reading commands from stdin")
            client_env = profile(args, folder / "client-profile", port)
            stage_cues(Path(client_env["VORTEX_USERDIR"]) / "data/botprobe.pk3dir")
            client = Log(stack, base + ["--connect", f"127.0.0.1:{wire.port}",
                                        "--cvar", "_cl_name", "CueObserver"],
                         client_env, folder, "observer", now)
            client.expect("client loaded map 'darkzone'")
        time.sleep(0.75)
        loaded = now()
        server.send("sv_cmd bot_cmd load cues.bot")
        server.expect("25 commands read", timeout=5)
        def finished():
            return sum(1 for _, line in server.lines
                       if (marker := MARK.search(line)) and marker[3] == "BOTPROBE_end") == 2

        server.wait_until(finished, "both end markers", timeout=8)
        time.sleep(0.45)
        server.send("status")
        time.sleep(0.3)
        # Stop the observer first so its shutdown does not masquerade as a failed run.
        client.close()
        server.send("quit")
        server.proc.wait(timeout=6)
        server.worker.join()
        cues = ([(when, line) for when, line in client.lines if line.startswith("CUE ")]
                if engine == "original" else wire.cues)
        extract(engine, loaded, server, cues, folder / "evidence.txt")
        manifest[engine + "_argv"] = list(map(str, server_args))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--engine", choices=("original", "native", "both"), default="both")
    parser.add_argument("--port", type=int, default=29431)
    parser.add_argument("--darkplaces", type=Path, default=Path("/tmp/vortex-race-stock-runtime/darkplaces"))
    parser.add_argument("--code", type=Path, default=Path("/tmp/vortex-race-stock-runtime/compiled"))
    parser.add_argument("--godot", type=Path, default=next(Path("/tmp/vortex-parity-godot").glob("*/*.x86_64"), None))
    parser.add_argument("--loopback-shim", type=Path, default=Path("/tmp/va-earned.B3V3h7/loopback.so"))
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.assemblies = ROOT / ".godot/mono/temp/bin/Debug"
    if not args.loopback_shim.is_file():
        parser.error("--loopback-shim must point to the local loopback preload library")
    args.output.mkdir()
    revision = subprocess.check_output(["git", "-C", BASE, "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", BASE, "status", "--porcelain"], text=True).strip()
    if revision != PIN or dirty:
        raise RuntimeError(f"Expected a clean Xonotic checkout at {PIN}, got {revision}\n{dirty}")
    manifest = {
        "original_data_git": revision,
        "files_sha256": {path.name: sha(path) for path in (
            ROOT / "data/maps/darkzone.pk3", HERE / "cues.bot", args.darkplaces, args.godot,
            args.code / "progs.dat", args.code / "csprogs.dat", args.loopback_shim,
            args.assemblies / "VortexArena.dll", args.assemblies / "VortexArena.Server.dll",
        )},
    }
    engines = ("original", "native") if args.engine == "both" else (args.engine,)
    for i, engine in enumerate(engines):
        run(args, engine, manifest, args.port + i * 2)
    (args.output / "inputs.json").write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    main()

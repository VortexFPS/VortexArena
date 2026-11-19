#!/usr/bin/env python3
"""Record two real loopback clients against each engine. See README.md."""
import argparse
import contextlib
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import threading
import time
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
spec = importlib.util.spec_from_file_location("paired", ROOT / "tools/parity-paired.py")
paired = importlib.util.module_from_spec(spec)
spec.loader.exec_module(paired)
MAP = "runningmanctf"
BASE_REV = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
CAMERA = (-288, -208, 112, 0, 11)
# Destination fields are player origin and camera yaw/pitch in Quake coordinates.
DESTINATIONS = {
    "cryfire": (-221, -249, 48, 0, -4),
    "point": (-178, -208, 42, 0, 0),
    "point_drop": (-80, -208, 150, 0, 22),
    "redview": (-222, -156, 42, -20.1, -18),
    "blueview": (-222, -208, 42, 0, -19),
    "bluegen": (-215, -350, 42, 0, 8),
}
CVAR_COMMON = {
    "sv_public": 0,
    "sv_cheats": 1,
    "sv_eventlog": 1,
    "g_start_delay": 0,
    "g_warmup": 0,
    "g_spawnshieldtime": 0,
    "g_balance_teams_prevent_imbalance": 0,
    "bot_number": 0,
}
CVAR_CRY = {
    "g_weaponarena": "crylink",
    "g_balance_crylink_primary_speed": 100,
    "g_balance_crylink_primary_spread": 0.08,
    "g_balance_crylink_primary_refire": 4,
    "g_balance_crylink_primary_middle_lifetime": 0.35,
    "g_balance_crylink_primary_other_lifetime": 0.35,
    "g_balance_crylink_primary_middle_fadetime": 1.1,
    "g_balance_crylink_primary_other_fadetime": 1.1,
}
CVAR_ONS = {
    "g_weaponarena": "vortex",
    "g_balance_vortex_charge": 0,
    "g_balance_vortex_primary_damage": 62,
    "g_balance_vortex_primary_refire": 1.6,
    "g_balance_vortex_primary_force": 0,
    "g_onslaught_warmup": 0,
    "g_onslaught_round_timelimit": 1800,
    "g_onslaught_cp_buildtime": 4,
    "g_onslaught_cp_health": 200,
    "g_onslaught_cp_buildhealth": 80,
    "g_onslaught_cp_regen": 90,
    "g_onslaught_gen_health": 180,
}


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def entity(**fields):
    return "{\n" + "".join(f'"{key}" "{value}"\n' for key, value in fields.items()) + "}\n"


def make_map(stock):
    """Append a replacement entity lump. Leave geometry, PVS, and brush models byte-identical."""
    offset, length = struct.unpack_from("<II", stock, 8)
    assert stock[:4] == b"IBSP"
    original = stock[offset:offset + length].decode().rstrip("\0")
    entities = re.findall(r"\{[^{}]*\}", original)
    retained = [entry for entry in entities if not re.search(r'"classname"\s+"onslaught_', entry)]
    lump = "\n".join(retained) + "\n"
    lump += entity(classname="onslaught_controlpoint", targetname="probe_cp",
                   message="Big Blue room", origin="-80 -208 0", spawnflags=1)
    lump += entity(classname="onslaught_generator", targetname="probe_red",
                   team=5, origin="-696 2448 296", spawnflags=1)
    lump += entity(classname="onslaught_generator", targetname="probe_blue",
                   team=14, origin="-94 -350 0", spawnflags=1)
    for generator in ("probe_red", "probe_blue"):
        lump += entity(classname="onslaught_link", target=generator, target2="probe_cp", origin="-80 -208 0")
    for name, (x, y, z, yaw, pitch) in DESTINATIONS.items():
        lump += entity(classname="misc_teleporter_dest", targetname=f"probe_{name}",
                       origin=f"{x} {y} {z}", angles=f"{pitch} {yaw} 0")
    data = (lump + "\0").encode()
    bsp = bytearray(stock)
    bsp.extend(b"\0" * (-len(bsp) % 4))
    struct.pack_into("<II", bsp, 8, len(bsp), len(data))
    return bytes(bsp) + data


def stage(args):
    revision = subprocess.check_output(["git", "-C", args.base, "rev-parse", "HEAD"], text=True).strip()
    if revision != BASE_REV:
        raise RuntimeError(f"Expected pinned Xonotic data {BASE_REV}, found {revision}")
    original = args.output / "original"
    native = args.output / "native-data"
    (original / "data").mkdir(parents=True)
    (native / "maps").mkdir(parents=True)
    for filename in ("shared.pk3", "_init.pk3"):
        (original / "data" / filename).symlink_to(args.maps / filename)
    (original / "data/xonotic-data.pk3dir").symlink_to(args.base)
    (original / "data/zz-pinned.pk3dir").symlink_to(args.code)
    for entry in (ROOT / "data").iterdir():
        if entry.name != "maps" and entry.name.endswith((".pk3", ".pk3dir")):
            (native / entry.name).symlink_to(entry)
    (native / "maps/shared.pk3").symlink_to(ROOT / "data/maps/shared.pk3")
    stock_path = args.maps / f"{MAP}.pk3"
    bsp_name = f"maps/{MAP}.bsp"
    with zipfile.ZipFile(stock_path) as stock:
        source_bsp = stock.read(bsp_name)
        fixture_bsp = make_map(source_bsp)
        original_members = {name: stock.read(name) for name in stock.namelist() if name != bsp_name}
    native_path = ROOT / f"data/maps/{MAP}.pk3"
    lightmap = re.compile(rf"maps/{MAP}/lm_[0-9]+\.[^/]+")
    native_members = {}
    with zipfile.ZipFile(native_path) as assets:
        for name in assets.namelist():
            if name != bsp_name and not lightmap.fullmatch(name):
                native_members[name] = assets.read(name)
    native_members.update({name: content for name, content in original_members.items() if lightmap.fullmatch(name)})
    mapinfo = f"maps/{MAP}.mapinfo"
    original_members[mapinfo] += b"\ngametype dm\n"
    native_members[mapinfo] = original_members[mapinfo]
    for path, members in ((original / "data", original_members), (native / "maps", native_members)):
        with zipfile.ZipFile(path / f"{MAP}.pk3", "w", zipfile.ZIP_DEFLATED) as package:
            for name, content in members.items():
                package.writestr(name, content)
            package.writestr(bsp_name, fixture_bsp)
    manifest = {
        "original_data_git": revision,
        "stock_bsp_sha256": hashlib.sha256(source_bsp).hexdigest(),
        "fixture_bsp_sha256": hashlib.sha256(fixture_bsp).hexdigest(),
        "external_lightmaps_sha256": {
            name: hashlib.sha256(content).hexdigest()
            for name, content in original_members.items() if lightmap.fullmatch(name)
        },
        "original_code_sha256": {name: sha(args.code / name) for name in ("progs.dat", "csprogs.dat", "menu.dat")},
        "executables_sha256": {str(path): sha(path) for path in (args.godot, args.dp / "darkplaces-sdl",
                                                                args.dp / "darkplaces-dedicated")},
        "debug_assemblies_sha256": {
            path.name: sha(path) for path in sorted((args.native_project / ".godot/mono/temp/bin/Debug").glob("VortexArena*.dll"))
        },
    }
    (args.output / "inputs.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return original, native


class Session:
    screen_size = (480, 360)

    def __init__(self, args, folder, data):
        self.args = args
        self.folder = folder
        self.data = data
        self.native = args.engine == "native"
        self.ons = args.scenario == "ons"
        self.env = os.environ | {"LIBGL_ALWAYS_SOFTWARE": "1", "LP_NUM_THREADS": "2", "MAGICK_THREAD_LIMIT": "1"}
        self.clients = {}
        self.argv = {}
        self.origin = time.monotonic()
        self.lock = threading.Lock()

    def emit(self, **event):
        with self.lock, (self.folder / "timeline.jsonl").open("a") as log:
            log.write(json.dumps({"seconds": round(time.monotonic() - self.origin, 3), **event}) + "\n")

    def launch(self, stack, name, display=None, extra=()):
        user = self.folder / f"{name}-user"
        user.mkdir()
        env = self.env | {"VORTEX_USERDIR": str(user)}
        if display:
            env["DISPLAY"] = f":{display}"
        if self.native:
            env["DOTNET_ROOT"] = str(self.args.dotnet_root)
            cmd = [str(self.args.godot), "--path", str(self.args.native_project), "--audio-driver", "Dummy",
                   "--data", str(self.data), "--no-save-config"]
        else:
            if self.args.sdl_libs:
                env["LD_LIBRARY_PATH"] = str(self.args.sdl_libs)
            binary = "darkplaces-dedicated" if name == "server" else "darkplaces-sdl"
            cmd = [str(self.args.dp / binary), "-xonotic", "-basedir", str(self.data), "-userdir", str(user)]
        cmd.extend(map(str, extra))
        self.argv[name] = {"argv": cmd, "env_overrides": {k: env[k] for k in
                           ("DISPLAY", "VORTEX_USERDIR", "DOTNET_ROOT", "LD_LIBRARY_PATH",
                            "LIBGL_ALWAYS_SOFTWARE", "LP_NUM_THREADS", "MAGICK_THREAD_LIMIT") if k in env}}
        (self.folder / "commands.json").write_text(json.dumps(self.argv, indent=2) + "\n")
        log = self.folder / f"{name}.log"
        return paired.start(stack, cmd, log, env), log

    def boot(self, stack):
        cvars = CVAR_COMMON | (CVAR_ONS if self.ons else CVAR_CRY)
        port = self.args.port
        if self.native:
            extra = ["--headless", "--dedicated", MAP, "--port", port, "--gametype",
                     "ons" if self.ons else "dm", "--bots", 0]
            for k, v in (cvars | {"cl_frameprofiler": 0, "bot_neural_bake": 0}).items():
                extra.extend(("--cvar", k, v))
        else:
            extra = ["-dedicated", "-ip", "127.0.0.1", "-port", port]
            for k, v in (cvars | {"g_onslaught": int(self.ons), "g_dm": int(not self.ons)}).items():
                extra.extend((f"+{k}", v))
            extra.extend(("+map", MAP))
        self.server, self.server_log = self.launch(stack, "server", extra=extra)
        paired.wait_log(self.server, self.server_log, rf"(?:server on |Server listening on address )127\.0\.0\.1:{port}\b")
        starting = []
        width, height = self.screen_size
        for i, name in enumerate(("RedProbe", "BlueProbe")):
            number = self.args.display + i
            display = f":{number}"
            with (self.folder / f"xvfb-{number}.log").open("wb") as log:
                xvfb = subprocess.Popen(["Xvfb", display, "-screen", "0", f"{width}x{height}x24",
                                         "-nolisten", "tcp"], stdout=log, stderr=subprocess.STDOUT)
            stack.callback(paired.stop, xvfb)
            for _ in range(50):
                if xvfb.poll() is not None:
                    raise RuntimeError(f"Xvfb {display} exited")
                if Path(f"/tmp/.X11-unix/X{number}").exists():
                    break
                time.sleep(0.1)
            env = self.env | {"DISPLAY": display}
            address = f"127.0.0.1:{port}"
            observer = not self.ons and i == 0
            if self.native:
                extra = ["--rendering-method", "gl_compatibility", "--render-thread", "safe",
                         "--resolution", f"{width}x{height}", "--position", "0,0", "--connect", address,
                         "--cvar", "cl_autopause", 0, "--cvar", "cl_maxfps", 40,
                         "--cvar", "cl_frameprofiler", "0.0", "--cvar", "_cl_name", name,
                         "--cvar", "cl_portal_render", 0, "--cvar", "r_warpzone", 0,
                         "--cvar", "r_water", 0, "--cvar", "vid_vsync", 0, "--cvar", "cl_warm_at_boot", 0,
                         "--cvar", "cl_precache_all_weapons", 0,
                         "--cvar", "gl_texturecompression", 0]
                if observer:
                    extra.extend(("--observe", " ".join(map(str, CAMERA))))
                expected = rf"client loaded map '{MAP}'"
            else:
                extra = ["-window", "-width", width, "-height", height, "-nosound", "+cl_maxfps", 40,
                         "+name", name, "+cl_allow_uidtracking", 0, "+cl_allow_uid2name", 0,
                         "+cl_welcome", 0, "+cl_autodemo", 1, "+cl_movement", 0, "+connect", address]
                expected = rf"{name}.*connected"
            proc, log = self.launch(stack, name, number, extra)
            starting.append((name, proc, log, expected, env))
            if not self.native:
                paired.wait_log(proc, log, expected, timeout=180)
        for name, proc, log, expected, env in starting:
            if self.native:
                paired.wait_log(proc, log, expected, timeout=180)
            window = subprocess.check_output(["xdotool", "search", "--sync", "--onlyvisible",
                                              "--name", "Xonotic|Vortex"], env=env, timeout=15, text=True)
            subprocess.run(["xdotool", "windowfocus", window.splitlines()[0]], env=env, check=True)
            self.clients[name] = (proc, env)
        self.origin = time.monotonic()
        self.emit(event="both graphical clients connected", port=port)
        time.sleep(2)

    def input(self, who, *args):
        proc, env = self.clients[who]
        if proc.poll() is not None or self.server.poll() is not None:
            raise RuntimeError(f"{who} or server exited during run")
        subprocess.run(["xdotool", *args], env=env, check=True, timeout=4)

    def send(self, who, command):
        self.emit(client=who, command=command)
        self.input(who, "key", "grave")
        time.sleep(0.38)
        self.input(who, "type", "--clearmodifiers", "--delay", "1", command)
        self.input(who, "key", "Return")
        time.sleep(0.24)
        self.input(who, "key", "grave")
        time.sleep(0.65 if self.native else 0.30)

    def server_cmd(self, *commands):
        if not self.native:
            # fgets may read ahead, leaving select() on the pipe unaware of subsequent lines.
            commands = ("; ".join(commands),)
        for cmd in commands:
            self.emit(server=cmd)
            self.server.stdin.write(f"{cmd}\n".encode())
        self.server.stdin.flush()

    def teleport(self, who, target):
        self.send(who, "cmd teleporttotarget probe_" + target)
        if not self.native:
            number = 1 if who == "RedProbe" else 2
            self.server_cmd(f"echo {who} at probe_{target}", f"prvm_edictget server {number} origin",
                            f"prvm_edictget server {number} v_angle")

    def touch_point(self):
        if self.args.generator_only:
            self.teleport("RedProbe", "point_drop")
            return
        self.teleport("RedProbe", "point")
        self.emit(client="RedProbe", event="walk straight into pad")
        self.input("RedProbe", "keydown", "w")
        time.sleep(1.15 if self.native else 0.55)
        self.input("RedProbe", "keyup", "w")
        time.sleep(0.45)

    def shots(self, who, n=1):
        for _ in range(n):
            self.emit(client=who, event="attack down")
            self.input(who, "mousedown", "1")
            time.sleep(1.05 if self.native else 0.22)
            self.input(who, "mouseup", "1")
            self.emit(client=who, event="attack up")
            if n > 1:
                time.sleep(0.8 if self.native else 1.45)

    def wait_score(self, name, minimum, fallback):
        if not self.native:
            time.sleep(fallback)
            return
        offset = self.server_log.stat().st_size
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            self.server_cmd("status")
            time.sleep(0.9)
            with self.server_log.open("rb") as log:
                log.seek(offset)
                output = log.read().decode(errors="replace")
            scores = re.findall(rf"\b{name}\s+score\s+(\d+)", output)
            if any(int(score) >= minimum for score in scores):
                self.emit(client=name, verified_score=minimum)
                return
        raise RuntimeError(f"{name}: never saw score {minimum} in the server log")

    def start_frames(self, stack, who, interval):
        frames = self.folder / who
        frames.mkdir()
        done = threading.Event()
        _, env = self.clients[who]

        def record():
            index = 0
            while not done.is_set():
                then = time.monotonic()
                frame = f"{index:04}.webp"
                try:
                    subprocess.run(["import", "-window", "root", "-quality", "83", str(frames / frame)],
                                   env=env, check=True, timeout=8)
                except (subprocess.TimeoutExpired, subprocess.CalledProcessError) as error:
                    self.emit(client=who, sample_error=str(error))
                else:
                    self.emit(frame=f"{who}/{frame}", sample=round(then - self.origin, 3))
                index += 1
                done.wait(max(0, then + interval - time.monotonic()))

        worker = threading.Thread(target=record, daemon=True)
        worker.start()
        stack.callback(self.stop_frames, done, worker)

    @staticmethod
    def stop_frames(done, worker):
        done.set()
        worker.join(timeout=6)

    def run_crylink(self, stack):
        self.send("BlueProbe", "cmd join")
        if self.native:
            paired.wait_log(self.clients["BlueProbe"][0], self.folder / "BlueProbe.log",
                            r"joined the game", timeout=35)
            time.sleep(2)
        if not self.native:
            self.send("RedProbe", "cmd spectate")
            x, y, z, yaw, pitch = CAMERA
            self.server_cmd(f'prvm_edictset server 1 origin "{x} {y} {z}"; '
                            f'prvm_edictset server 1 angles "{pitch} {yaw} 0"; '
                            "prvm_edictset server 1 fixangle 1")
        self.teleport("BlueProbe", "cryfire")
        self.server_cmd("status", *CVAR_CRY)
        time.sleep(2)
        self.start_frames(stack, "RedProbe", 0.065)
        self.start_frames(stack, "BlueProbe", 0.13)
        time.sleep(0.55)
        self.shots("BlueProbe")
        time.sleep(4.6)
        self.shots("BlueProbe")
        time.sleep(2.1)

    def run_ons(self, stack):
        for actor, team in (("RedProbe", "red"), ("BlueProbe", "blue")):
            self.send(actor, f"cmd selectteam {team}")
            self.send(actor, "cmd join")
            proc, _ = self.clients[actor]
            expected = r"joined the game" if self.native else rf"{actor}.*is now playing on the .*{team.upper()} team"
            paired.wait_log(proc, self.folder / f"{actor}.log", expected, timeout=25)
        self.server_cmd("status", *CVAR_ONS)
        time.sleep(2)
        self.teleport("BlueProbe", "blueview")
        self.teleport("RedProbe", "bluegen")
        self.start_frames(stack, "RedProbe", 0.18)
        self.start_frames(stack, "BlueProbe", 0.18)
        time.sleep(1)
        self.emit(phase="shielded generator shot")
        self.shots("RedProbe")
        time.sleep(0.8)
        self.emit(phase="initial build touch")
        self.touch_point()
        if not self.args.generator_only:
            self.teleport("RedProbe", "redview")
            self.wait_score("RedProbe", 10, 5.5)
            self.emit(phase="captured point damage")
            self.shots("BlueProbe", 2)
            self.emit(phase="wait for automatic repair")
            time.sleep(9)
            self.emit(phase="destroy point and reset")
            self.shots("BlueProbe", 4)
            self.wait_score("BlueProbe", 10, 1.5)
            self.emit(phase="second build touch")
            self.touch_point()
            self.wait_score("RedProbe", 20, 6.0)
        else:
            self.wait_score("RedProbe", 10, 6.0)
        self.teleport("RedProbe", "bluegen")
        time.sleep(2.2)
        self.emit(phase="exposed generator damage")
        self.shots("RedProbe")
        time.sleep(1.45)
        self.shots("RedProbe")
        time.sleep(1.45)
        self.emit(phase="destroy generator")
        self.shots("RedProbe")
        if self.native:
            self.emit(phase="repeat exposed shot if the round is still active")
            time.sleep(1.65)
            self.shots("RedProbe", 4)
        time.sleep(self.args.generator_tail)
        self.server_cmd("status")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--scenario", required=True, choices=("crylink", "ons"))
    p.add_argument("--engine", required=True, choices=("original", "native"))
    p.add_argument("--port", required=True, type=int, choices=range(26800, 26820), metavar="26800-26819")
    p.add_argument("--display", required=True, type=int, choices=range(161, 164), metavar="161-163")
    p.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    p.add_argument("--maps", type=Path, default=Path("/tmp/va-original-maps"))
    p.add_argument("--code", type=Path, default=Path("/tmp/va-paired-run/compiled.pk3dir"))
    p.add_argument("--dp", type=Path, default=Path("/tmp/va-paired-20261119/upstream-engine"))
    p.add_argument("--sdl-libs", type=Path, default=Path("/tmp/va-paired-20261119/dependencies/usr/lib/x86_64-linux-gnu"))
    p.add_argument("--godot", type=Path, default=next(Path("/tmp/vortex-parity-godot").glob("*/*.x86_64"), None))
    p.add_argument("--generator-only", action="store_true", help="skip the point reset cycle in Onslaught")
    p.add_argument("--generator-tail", type=float, default=6, help="seconds to keep recording after the generator volley")
    p.add_argument("--native-project", type=Path, default=ROOT)
    p.add_argument("--dotnet-root", type=Path, default=Path("/tmp/va-dotnet8"))
    args = p.parse_args()
    args.native_project = args.native_project.resolve()
    args.output = args.output.resolve()
    args.output.mkdir()
    original, native = stage(args)
    with contextlib.ExitStack() as stack:
        session = Session(args, args.output, native if args.engine == "native" else original)
        session.boot(stack)
        if args.scenario == "ons":
            session.run_ons(stack)
        else:
            session.run_crylink(stack)


if __name__ == "__main__":
    main()

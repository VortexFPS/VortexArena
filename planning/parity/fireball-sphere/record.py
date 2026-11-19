#!/usr/bin/env python3
"""Record the primary Fireball from two real clients on the row 14 Stormkeep geometry."""
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
import time
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
spec = importlib.util.spec_from_file_location("objectives", HERE.parent / "scripted-objectives/record.py")
recorder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recorder)
recorder.MAP = "stormkeep"
recorder.CAMERA = (1216, 145, 112, 90, 21)
recorder.CVAR_CRY = {
    "g_weaponarena": "fireball",
    "g_balance_fireball_primary_bfgradius": 96,
    "g_balance_fireball_primary_animtime": 0.15,
    "g_balance_fireball_primary_radius": 32,
    "g_balance_fireball_primary_refire": 0.7,
    "g_balance_fireball_primary_laserradius": 0,
    "g_balance_fireball_primary_force": 0,
}


class Session(recorder.Session):
    def launch(self, stack, name, display=None, extra=()):
        if self.native:
            if name != "server":
                extra = [*extra, "--cvar", "cl_particles", 0, "--cvar", "cl_maxfps", 60,
                         "--cvar", "cl_welcome", 0, "--cvar", "r_shadow_realtime_dlight", 0,
                         "--cvar", "fov", 100, "--cvar", "slowmo", self.args.slowmo]
            return super().launch(stack, name, display, extra)

        folder = self.folder / f"{name}-user"
        folder.mkdir()
        env = self.env | {"LD_LIBRARY_PATH": str(self.args.stock / "sdl/usr/lib/x86_64-linux-gnu")}
        if display:
            env["DISPLAY"] = f":{display}"
            (folder / "data").mkdir()
            statement = function_statement(self.args.stock / "compiled/csprogs.dat", b"ModelEffect_Draw")
            (folder / "data/fireball-probe.cfg").write_text(
                f'bind F8 +fire\nbind F9 "prvm_breakpoint client {statement}"\n')
            extra = [*extra, "+exec", "fireball-probe.cfg", "+cl_particles", 0, "+cl_maxfps", 60, "+r_dynamic", 0,
                     "+r_shadow_realtime_dlight", 0, "+fov", 100]
        if name == "server":
            extra = [*extra, "+sv_jumpspeedcap_min", -1, "+sv_jumpspeedcap_max", 100000, "+w_prop_interval", 0.1]
        command = [self.args.stock / "xonotic-linux64-sdl", "-xonotic", "-basedir", self.data,
                   "-userdir", folder, *extra]
        command = list(map(str, command))
        self.argv[name] = {"argv": command, "display": env.get("DISPLAY")}
        (self.folder / "commands.json").write_text(json.dumps(self.argv, indent=2) + "\n")
        log = self.folder / f"{name}.log"
        return recorder.paired.start(stack, command, log, env), log

    def shot(self, name, lifetime, blast_radius=32, speed=1200):
        self.server_cmd(f"echo FIREBALL_TRIAL {name}", f"g_balance_fireball_primary_lifetime {lifetime}",
                        f"g_balance_fireball_primary_radius {blast_radius}",
                        f"g_balance_fireball_primary_speed {speed}",
                        "g_balance_fireball_primary_lifetime", "g_balance_fireball_primary_radius",
                        "g_balance_fireball_primary_speed")
        self.teleport("BlueProbe", "timeout" if name == "timeout" else "fire")
        if not self.native:
            self.server_cmd("prvm_edictget server 2 health", "prvm_edictget server 2 armorvalue")
        time.sleep(0.4)
        observer_log = self.folder / "RedProbe.log"
        log_offset = observer_log.stat().st_size
        self.emit(trial=name, phase="capture start")
        with contextlib.ExitStack() as sampling:
            for viewer in ("RedProbe", "BlueProbe"):
                directory = f"{name}-{viewer}"
                self.clients[directory] = self.clients[viewer]
                self.start_frames(sampling, directory, 0.04)
            time.sleep(0.45)
            self.emit(client="BlueProbe", event="attack down", key="F8")
            self.input("BlueProbe", "keydown", "F8")
            time.sleep(0.8)
            self.input("BlueProbe", "keyup", "F8")
            self.emit(client="BlueProbe", event="attack up", key="F8")
            time.sleep(6.5)
        if not self.native:
            self.server_cmd("prvm_edictget server 2 health", "prvm_edictget server 2 armorvalue")
        with observer_log.open("rb") as stream:
            stream.seek(log_offset)
            trial_log = stream.read().decode(errors="replace")
        self.emit(trial=name, phase="capture end",
                  stock_draw_calls=trial_log.count("ModelEffect_Draw : statement") if not self.native else None)

    def run(self):
        if self.native:
            proc, _ = self.clients["BlueProbe"]
            log = self.folder / "BlueProbe.log"
            for attempt in range(5):
                self.send("BlueProbe", "cmd join")
                try:
                    recorder.paired.wait_log(proc, log, r"joined the game", timeout=5)
                    break
                except RuntimeError:
                    if attempt == 4:
                        raise
            time.sleep(3)
        else:
            self.send("BlueProbe", "cmd join")
            x, y, z, yaw, pitch = recorder.CAMERA
            self.server_cmd(f'prvm_edictset server 1 origin "{x} {y} {z}"',
                            f'prvm_edictset server 1 angles "{pitch} {yaw} 0"',
                            "prvm_edictset server 1 fixangle 1")
            self.input("RedProbe", "keydown", "F9")
            time.sleep(0.25)
            self.input("RedProbe", "keyup", "F9")
        if self.native:
            self.send("BlueProbe", "bind F8 +fire")
        self.server_cmd("status", *recorder.CVAR_CRY)
        if not self.native:
            statement = function_statement(self.args.stock / "compiled/progs.dat", b"W_Fireball_Think")
            self.server_cmd(f"prvm_breakpoint server {statement + 3}")
        self.shot("impact", 15)
        self.shot("timeout", 0.001, speed=1)
        self.shot("self-damage", 15, 250)
        self.server_cmd("status")
        time.sleep(0.5)


def function_statement(program, symbol):
    data = program.read_bytes()
    header = struct.unpack_from("<15i", data)
    for i in range(header[9]):
        function = struct.unpack_from("<7i8B", data, header[8] + 36 * i)
        name = data[header[10] + function[4]:].split(b"\0", 1)[0]
        if name == symbol:
            return function[0]
    raise RuntimeError(f"{symbol!r} is missing from {program}")


def stage(args):
    pin = subprocess.check_output(["git", "-C", args.base, "rev-parse", "HEAD"], text=True).strip()
    if pin != recorder.BASE_REV:
        raise RuntimeError(f"Unexpected Xonotic data revision {pin}")
    original = args.output / "original"
    native = args.output / "native-data"
    (original / "data").mkdir(parents=True)
    (native / "maps").mkdir(parents=True)
    (original / "data/xonotic-data.pk3dir").symlink_to(args.base)
    (original / "data/zz-pinned-code.pk3dir").symlink_to(args.stock / "compiled")
    for entry in (ROOT / "data").glob("*.pk3dir"):
        (native / entry.name).symlink_to(entry)
        if entry.name.startswith("font-"):
            (original / "data" / entry.name).symlink_to(entry)
    for folder in (original / "data", native / "maps"):
        (folder / "shared.pk3").symlink_to(ROOT / "data/maps/shared.pk3")

    bsp_path = "maps/stormkeep.bsp"
    with zipfile.ZipFile(ROOT / "data/maps/stormkeep.pk3") as package:
        members = {name: package.read(name) for name in package.namelist()}
    bsp = bytearray(members[bsp_path])
    offset, length = struct.unpack_from("<II", bsp, 8)
    world = re.search(rb"\{[^{}]*\}", bsp[offset:offset + length])[0].decode("latin1")
    lump = world + "\n" + recorder.entity(classname="info_player_deathmatch", origin="1216 248 16")
    lump += recorder.entity(classname="info_player_deathmatch", origin="1216 448 16")
    lump += recorder.entity(classname="misc_teleporter_dest", targetname="probe_fire",
                            origin="1216 248 16", angles="22 90 0")
    lump += recorder.entity(classname="misc_teleporter_dest", targetname="probe_timeout",
                            origin="1216 248 16", angles="-10 90 0")
    lump = (lump + "\0").encode("latin1")
    bsp.extend(b"\0" * (-len(bsp) % 4))
    struct.pack_into("<II", bsp, 8, len(bsp), len(lump))
    members[bsp_path] = bsp + lump
    for folder in (original / "data", native / "maps"):
        with zipfile.ZipFile(folder / "stormkeep.pk3", "w", zipfile.ZIP_DEFLATED) as package:
            for name, content in members.items():
                package.writestr(name, content)
    tracked = [original / "data/stormkeep.pk3", native / "maps/stormkeep.pk3",
               args.base / "models/sphere/sphere.md3", ROOT / "data/core.pk3dir/models/sphere/sphere.md3",
               args.stock / "compiled/progs.dat", args.stock / "compiled/csprogs.dat",
               args.stock / "xonotic-linux64-sdl", args.godot]
    tracked += list((args.native_project / ".godot/mono/temp/bin/Debug").glob("VortexArena*.dll"))
    (args.output / "inputs.json").write_text(json.dumps({
        "original_data_git": pin,
        "fixture_bsp_sha256": hashlib.sha256(members[bsp_path]).hexdigest(),
        "sha256": {str(path): recorder.sha(path) for path in tracked}
    }, indent=2) + "\n")
    return original, native


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--engine", required=True, choices=("original", "native"))
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--display", required=True, type=int, help="first of two private X display numbers")
    parser.add_argument("--base", default=Path("/tmp/vortex-parity-base"), type=Path)
    parser.add_argument("--slowmo", type=float, default=0.25, help="server game-time scale (1 for normal speed)")
    parser.add_argument("--stock", default=Path("/tmp/va-reviewed-stock"), type=Path)
    parser.add_argument("--native-project", default=ROOT, type=Path)
    parser.add_argument("--godot", type=Path, default=next(Path("/tmp/vortex-parity-godot").glob("*/*.x86_64"), None))
    parser.add_argument("--dotnet-root", default=Path("/opt/dotnet"), type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir()
    args.scenario = "fireball"
    recorder.CVAR_CRY["slowmo"] = args.slowmo
    os.environ["DOTNET_ROLL_FORWARD"] = "Major"
    os.environ["XDG_DATA_HOME"] = str(args.output / "xdg-data")
    os.environ["XDG_CONFIG_HOME"] = str(args.output / "xdg-config")
    os.environ["XDG_CACHE_HOME"] = str(args.output / "xdg-cache")
    original, native = stage(args)
    with contextlib.ExitStack() as stack:
        session = Session(args, args.output, native if args.engine == "native" else original)
        session.boot(stack)
        session.run()


if __name__ == "__main__":
    main()

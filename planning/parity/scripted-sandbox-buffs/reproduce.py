#!/usr/bin/env python3
"""Script two independent clients against each real Stormkeep game server."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import importlib.util
import json
import os
import re
from pathlib import Path
import shlex
import shutil
import struct
import subprocess
import sys
import time
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("paired", ROOT / "tools/parity-paired.py")
paired = importlib.util.module_from_spec(spec)
spec.loader.exec_module(paired)

CVARS = paired.COMMON | {
    "g_start_delay": "0",
    "g_buffs": "1",
    "g_buffs_replace_powerups": "0",
    "g_buffs_spawn_count": "0",
    "g_sandbox": "1",
    "g_sandbox_info": "2",
    "g_sandbox_editor_free": "1",
    "g_sandbox_editor_distance_spawn": "88",
    "g_sandbox_storage_autoload": "0",
    "g_sandbox_storage_autosave": "0.2",
    "g_grab_range": "200",
    "timelimit": "0",
    "g_buffs_cooldown_activate": "1",
}
MODEL = "models/items/g_h100.md3"
COMMAND_KEYS = {
    "cmd join": "F1",
    "cmd teleporttotarget parity_owner": "F2",
    "cmd teleporttotarget parity_guest": "F3",
    "cmd teleporttotarget parity_buff_start": "F4",
    "cmd teleporttotarget parity_buff_view": "F5",
    "cmd teleporttotarget parity_buff_touch": "F6",
    f"cmd g_sandbox object_spawn {MODEL}": "F7",
    "cmd g_sandbox object_info object": "F8",
    "cmd g_sandbox object_edit skin 0": "F9",
    "quit": "F10",
}


def add_fixture(bsp, fixture):
    """Relocate only the entity lump. All other original BSP bytes stay intact."""
    offset, length = struct.unpack_from("<II", bsp, 8)
    entities = bsp[offset:offset + length].rstrip(b"\0") + b"\n" + fixture + b"\0"
    padded = bsp + bytes(-len(bsp) % 4)
    return padded[:8] + struct.pack("<II", len(padded), len(entities)) + padded[16:] + entities


def stage(args):
    original, native, evidence = paired.stage_data(args)
    original_map = original / "data/stormkeep.pk3"
    source = original_map.resolve()
    original_map.unlink()
    fixture = (HERE / "fixture.ent").read_bytes()
    with zipfile.ZipFile(source) as stock:
        bsp = add_fixture(stock.read(paired.BSP), fixture)
    for archive_path, source_path in ((original_map, source), (native / "maps/stormkeep.pk3", None)):
        old = source_path or archive_path.with_suffix(".upstream")
        if source_path is None:
            archive_path.rename(old)
        with zipfile.ZipFile(old) as archive, zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as out:
            for entry in archive.namelist():
                if entry == paired.BSP:
                    out.writestr(entry, bsp)
                elif entry != "maps/stormkeep.ent":
                    out.writestr(entry, archive.read(entry))
        if source_path is None:
            old.unlink()
        with zipfile.ZipFile(archive_path) as archive:
            assert archive.read(paired.BSP) == bsp
    if args.original_public_key:
        shutil.copyfile(args.original_public_key, original / "key_0.d0pk")
    evidence["fixture_sha256"] = paired.digest(fixture)
    evidence["fixture_bsp_sha256"] = paired.digest(bsp)
    evidence["xonotic_data_revision"] = subprocess.check_output(
        ["git", "-C", args.original_base, "rev-parse", "HEAD"], text=True).strip()
    evidence["runtime_sha256"] = {name: paired.digest(getattr(args, name).read_bytes())
                                  for name in ("godot", "xonotic_client", "xonotic_server")}
    evidence["native_build_sha256"] = {
        name: paired.digest((args.native_project / ".godot/mono/temp/bin/Debug" / name).read_bytes())
        for name in ("VortexArena.dll", "VortexArena.Common.dll", "VortexArena.Server.dll")
    }
    (args.output / "inputs.json").write_text(json.dumps(evidence, indent=2) + "\n")
    return original, native


def run(args, engine, data, port, first_display):
    folder = args.output / f"session-{engine}"
    folder.mkdir()
    native = engine == "native"
    cvars = CVARS | ({"sv_cheats": "2", "sv_spectate": "0"} if native else {})
    server_cmd, first_cmd = paired.commands(args, engine, "sandbox", port, folder, data, cvars, 0)
    env = os.environ | {"LIBGL_ALWAYS_SOFTWARE": "1", "LP_NUM_THREADS": "2", "DOTNET_ROOT": str(args.dotnet_root),
                        "SDL_MOUSE_RELATIVE_MODE_WARP": "1"}
    if native:
        server_cmd[server_cmd.index("--host")] = "--dedicated"
        server_cmd[2] = first_cmd[2] = str(args.native_project)
    clients = {}
    timeline = (folder / "timeline.jsonl").open("w", buffering=1)
    start = time.monotonic()

    def emit(**event):
        timeline.write(json.dumps({"seconds": round(time.monotonic() - start, 3), **event}) + "\n")

    def server(*commands):
        for command in commands:
            emit(server=command)
        if not native:
            commands = ("; ".join(commands),)
        for command in commands:
            if native and command.split()[0] in cvars | {"g_sandbox_readonly": ""}:
                command = f"set {command}"
            host.stdin.write(f"{command}\n".encode())
        host.stdin.flush()
        time.sleep(0.18)

    def x(role, *args):
        subprocess.run(["xdotool", *map(str, args)], env=clients[role][2], check=True, timeout=20)

    def send(role, command):
        if native and command.startswith("cmd teleporttotarget parity_buff_"):
            proc, log, _, _ = clients[role]
            snaps = log.read_text(errors="replace").count("authoritative snap")
            for attempt in range(1, 4):
                emit(client=role, command=command, input="console", attempt=attempt)
                x(role, "key", "grave")
                time.sleep(1.0)
                x(role, "type", "--clearmodifiers", "--delay", "15", command)
                x(role, "key", "Return")
                time.sleep(1.0)
                x(role, "key", "grave")
                deadline = time.monotonic() + 6
                while time.monotonic() < deadline and proc.poll() is None:
                    if log.read_text(errors="replace").count("authoritative snap") > snaps:
                        emit(client=role, camera_ack=command)
                        return
                    time.sleep(0.2)
            raise RuntimeError(f"{role} did not acknowledge {command}")
        key = COMMAND_KEYS[command]
        emit(client=role, command=command, key=key)
        x(role, "keydown", key)
        time.sleep(0.55 if native else 0.15)
        # Quit can tear down the window immediately on key-down.
        if command != "quit":
            x(role, "keyup", key)
        time.sleep(0.6)

    def pair_commands(owner, guest):
        with ThreadPoolExecutor(max_workers=2) as pool:
            jobs = (pool.submit(send, "owner", owner), pool.submit(send, "guest", guest))
            for job in jobs:
                job.result()

    def pose(owner, guest, disable_cheats=True):
        server(f"sv_cheats {cvars['sv_cheats']}")
        time.sleep(0.3)
        pair_commands(f"cmd teleporttotarget parity_{owner}", f"cmd teleporttotarget parity_{guest}")
        time.sleep(2.0)
        if disable_cheats:
            server("sv_cheats 0")
            time.sleep(0.25)

    def aim(role, x_pixels, y_pixels):
        emit(client=role, mouse_relative=[x_pixels, y_pixels])
        x(role, "mousemove_relative", "--", x_pixels, y_pixels)

    def snapshot(phase, save=True):
        for role, (_, _, xenv, _) in clients.items():
            name = f"{phase}-{role}.webp"
            try:
                frame = subprocess.check_output(["xwd", "-silent", "-root"], env=xenv, timeout=12)
                subprocess.run(["convert", "xwd:-", "-quality", "82", str(folder / name)],
                               input=frame, check=True, timeout=12)
                emit(client=role, frame=name)
            except (subprocess.SubprocessError, OSError) as error:
                emit(client=role, frame_error=name, error=str(error))
        if save:
            paths = list((folder / "server-user").rglob("storage_*_stormkeep.txt"))
            if paths:
                records = []
                for line in paths[0].read_text().splitlines():
                    if not line or line.startswith("//"):
                        continue
                    fields = shlex.split(line)
                    if len(fields) >= 17:
                        records.append({"origin": fields[0], "angles": fields[1], "model": fields[2],
                                        "movetype": int(fields[10]), "has_owner_id": bool(fields[13]),
                                        "owner": fields[14]})
                emit(phase=phase, objects=records)

    def drag(role, phase, readonly_transition=False, return_aim=False):
        emit(client=role, key="v", action="down")
        x(role, "keydown", "v")
        time.sleep(0.45)
        aim(role, 155, 0)
        time.sleep(1.4)
        snapshot(f"{phase}-held")
        if readonly_transition:
            server("g_sandbox_readonly 1")
            time.sleep(0.4)
            aim(role, -155, 0)
            time.sleep(1.4)
            snapshot(f"{phase}-readonly-held")
        if not readonly_transition and return_aim:
            aim(role, -155, 0)
            time.sleep(1.4)
        emit(client=role, key="v", action="up")
        x(role, "keyup", "v")
        time.sleep(1.5)
        snapshot(f"{phase}-released")

    with timeline, ExitStack() as stack:
        host_log = folder / "server.log"
        if not native and args.original_key_users:
            userdir = folder / "server-user"
            userdir.mkdir()
            shutil.copyfile(args.original_key_users / "server-user/key_0.d0si", userdir / "key_0.d0si")
        host = paired.start(stack, server_cmd, host_log, env | {"VORTEX_USERDIR": str(folder / "server-user"),
                                                               "XDG_DATA_HOME": str(folder / "server-xdg")})
        paired.wait_log(host, host_log, r"listen server on 127\.0\.0\.1|Server listening on address 127\.0\.0\.1")
        all_commands = {"server": server_cmd, "cvars": cvars, "input_binds": COMMAND_KEYS | {"+button8": "v"}}
        for index, role in enumerate(("owner", "guest")):
            display = f":{first_display + index}"
            display_proc = paired.start(stack, ["Xvfb", display, "-screen", "0", "640x480x24", "-nolisten", "tcp"],
                                        folder / f"{role}-xvfb.log", env)
            xenv = env | {"DISPLAY": display, "VORTEX_USERDIR": str(folder / f"{role}-user"),
                          "XDG_DATA_HOME": str(folder / f"{role}-xdg")}
            lock = Path(f"/tmp/.X{first_display + index}-lock")
            for _ in range(40):
                if display_proc.poll() is not None:
                    raise RuntimeError(f"Xvfb {display} exited")
                if lock.is_file() and lock.read_text().strip() == str(display_proc.pid):
                    if subprocess.run(["xdotool", "getdisplaygeometry"], env=xenv, capture_output=True).returncode == 0:
                        break
                time.sleep(0.1)
            else:
                raise RuntimeError(f"Xvfb {display} did not acquire its display")
            name = {"owner": "ParityOwner", "guest": "ParityGuest"}[role]
            cmd = [name if part == "ParityProbe" else part for part in first_cmd]
            bind_config = "\n".join(f'bind {key} "{command}"' for command, key in COMMAND_KEYS.items())
            bind_config += '\nbind v "+button8"\n'
            userdir = folder / f"{role}-user"
            if native:
                userdir.mkdir()
                (userdir / "config.cfg").write_text(bind_config)
                cmd[cmd.index("--resolution") + 1] = "640x480"
                cmd.extend(("--cvar", "sensitivity", "4", "--cvar", "showposition", "1",
                            "--cvar", "cl_frameprofiler", "0", "--cvar", "sv_warpzone_trace", "1",
                            "--cvar", "cl_portal_render", "0", "--cvar", "cl_warm_at_boot", "0",
                            "--cvar", "cl_precache_all_weapons", "0", "--cvar", "cl_idle_warmup", "0",
                            "--cvar", "gl_picmip", "2", "--cvar", "r_bloom", "0",
                            "--cvar", "r_shadow_realtime_dlight", "0"))
            else:
                cmd[cmd.index("-ip") + 1] = f"127.0.0.{index + 2}"
                cmd[cmd.index("-width") + 1] = "640"
                cmd[cmd.index("-height") + 1] = "480"
                cmd[cmd.index("-userdir") + 1] = str(folder / f"{role}-user")
                cmd += ["+sensitivity", "4", "+showposition", "1", "+in_mouse", "1",
                        "+r_bloom", "0", "+r_shadow_realtime_dlight", "0", "+cl_movement", "0",
                        "+developer", "1"]
                if args.original_key_users:
                    userdir = folder / f"{role}-user"
                    userdir.mkdir()
                    shutil.copyfile(args.original_key_users / f"{role}-user/key_0.d0si", userdir / "key_0.d0si")
                elif args.original_public_key:
                    # Generate a distinct unsigned ID locally. A loopback sink prevents a remote signing request.
                    key_cmd = cmd[:cmd.index("+connect")] + ["+crypto_keygen", "0", "http://127.0.0.1:1/", "+quit"]
                    key_log = folder / f"{role}-keygen.log"
                    key_proc = paired.start(stack, key_cmd, key_log, xenv)
                    paired.wait_log(key_proc, key_log, "Saved unsigned key", 25)
                    paired.stop(key_proc)
                config_dir = userdir / "data"
                config_dir.mkdir(parents=True, exist_ok=True)
                (config_dir / "parity-capture.cfg").write_text(bind_config)
                cmd[cmd.index("+connect"):cmd.index("+connect")] = ["+exec", "parity-capture.cfg"]
            all_commands[role] = {"argv": cmd, "display": display}
            log = folder / f"{role}.log"
            proc = paired.start(stack, cmd, log, xenv)
            clients[role] = (proc, log, xenv, display_proc)
            (folder / "commands.json").write_text(json.dumps(all_commands, indent=2) + "\n")
            windows = subprocess.check_output(["xdotool", "search", "--sync", "--onlyvisible", "--name", "Xonotic|Vortex"],
                                              env=xenv, timeout=50, text=True)
            x(role, "windowfocus", windows.splitlines()[0])
            ready = r"client loaded map 'stormkeep'" if native else r"client: program loaded"
            paired.wait_log(proc, log, ready, timeout=80)
        time.sleep(2)
        if not native:
            for name in ("ParityOwner", "ParityGuest"):
                paired.wait_log(host, host_log, rf":join:[^\r\n]*:{name}", timeout=80)
        if not native:
            pair_commands("cmd join", "cmd join")
        time.sleep(2)
        server("status")
        if args.phase != "buff":
            pose("owner", "guest")
            send("owner", f"cmd g_sandbox object_spawn {MODEL}")
            time.sleep(0.5)
            snapshot("01-spawn")
            pair_commands("cmd g_sandbox object_info object", "cmd g_sandbox object_info object")
            send("guest", "cmd g_sandbox object_edit skin 0")
            drag("guest", "02-nonowner", return_aim=True)
            if not native:
                # Use the opposite camera. Move the guest clear before occupying its position.
                server("sv_cheats 1")
                send("guest", "cmd teleporttotarget parity_buff_view")
                send("owner", "cmd teleporttotarget parity_guest")
                send("guest", "cmd teleporttotarget parity_owner")
                server("sv_cheats 0")
                send("owner", "cmd g_sandbox object_info object")
            drag("owner", "03-owner", return_aim=True)
            server("g_sandbox_editor_free 2")
            pose("owner", "guest")
            aim("guest", -60, 10)
            drag("guest", "04-free", readonly_transition=True)
            send("owner", "cmd g_sandbox object_edit skin 0")
            drag("owner", "05-readonly", return_aim=True)
        if args.phase != "sandbox":
            pose("buff_start", "buff_view", disable_cheats=False)
            if native:
                for role, angle in (("owner", "90"), ("guest", "270")):
                    proc, log, _, _ = clients[role]
                    paired.wait_log(proc, log, rf"authoritative snap -> <12, {angle}, 0>", timeout=10)
            snapshot("06-buff-before", save=False)
            send("owner", "cmd teleporttotarget parity_buff_touch")
            time.sleep(2 if native else 0.6)
            snapshot("07-buff-after", save=False)
            server("sv_cheats 0")
        server("status")
        for role in clients:
            send(role, "quit")
        server("quit")
        host.wait(timeout=20)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("output", "godot", "xonotic-client", "xonotic-server", "original-base", "original-maps", "original-code"):
        parser.add_argument(f"--{name}", required=True, type=lambda path: Path(path).resolve())
    parser.add_argument("--native-project", default=ROOT, type=lambda path: Path(path).resolve())
    parser.add_argument("--original-public-key", type=lambda path: Path(path).resolve())
    parser.add_argument("--original-key-users", type=lambda path: Path(path).resolve(),
                        help="optional fixture userdirs containing local keys from a previous run")
    parser.add_argument("--dotnet-root", default=Path("/opt/dotnet"), type=Path)
    parser.add_argument("--engine", choices=("original", "native", "both"), default="both")
    parser.add_argument("--phase", choices=("sandbox", "buff", "both"), default="both")
    parser.add_argument("--port", default=26700, type=int)
    args = parser.parse_args()
    if not 26700 <= args.port <= 26714:
        parser.error("game and discovery ports must fit in 26700–26719")
    args.output.mkdir(parents=True)
    original, native = stage(args)
    for offset, (engine, data) in enumerate((("original", original), ("native", native))):
        if args.engine in (engine, "both"):
            run(args, engine, data, args.port + 4 * offset, 151 + 2 * offset)


if __name__ == "__main__":
    main()

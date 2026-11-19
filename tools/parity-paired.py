#!/usr/bin/env python3
"""Capture private Xonotic and native Stormkeep multiplayer sessions on Linux."""

import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import zipfile


ROOT = Path(__file__).resolve().parents[1]
BSP = "maps/stormkeep.bsp"
COMMON = {
    "sv_public": "0",
    "sv_eventlog": "1",
    "g_warmup": "0",
    "skill": "5",
    "sv_cheats": "1",
    "g_spawnshieldtime": "0",
    "g_buffs_random_location": "0",
    "g_buffs_randomize": "0",
}
FREEZE = {
    "g_buffs": "1",
    "g_buffs_replace_powerups": "1",
    "g_freezetag_warmup": "5",
    "g_freezetag_frozen_maxtime": "12",
    "g_freezetag_revive_auto": "1",
    "g_freezetag_revive_auto_progress": "1",
    "g_freezetag_weaponarena": "vortex",
    "g_weaponarena": "vortex",
}
CRYLINK = {
    "g_buffs": "0",
    "bot_nofire": "1",
    "g_weaponarena": "crylink",
    "g_balance_crylink_primary_speed": "100",
    "g_balance_crylink_primary_refire": "3",
    "g_balance_crylink_primary_spread": "0.12",
    "g_balance_crylink_primary_middle_lifetime": "2",
    "g_balance_crylink_primary_other_lifetime": "2",
    "g_balance_crylink_primary_middle_fadetime": "1.5",
    "g_balance_crylink_primary_other_fadetime": "1.5",
}


def digest(content):
    return hashlib.sha256(content).hexdigest()


def stage_data(args):
    original = args.output / "original"
    data = original / "data"
    data.mkdir(parents=True)
    for filename in ("shared.pk3", "stormkeep.pk3", "_init.pk3"):
        (data / filename).symlink_to(args.original_maps / filename)
    (data / "xonotic-data.pk3dir").symlink_to(args.original_base)
    (data / "parity-code.pk3dir").symlink_to(args.original_code)
    native = args.output / "native-data"
    native.mkdir()
    for entry in (ROOT / "data").iterdir():
        if entry.name != "maps" and entry.name.endswith((".pk3", ".pk3dir")):
            (native / entry.name).symlink_to(entry)
    maps = native / "maps"
    maps.mkdir()
    (maps / "shared.pk3").symlink_to(ROOT / "data/maps/shared.pk3")
    stock_map = args.original_maps / "stormkeep.pk3"
    with zipfile.ZipFile(stock_map) as stock:
        map_members = {
            name: stock.read(name) for name in stock.namelist()
            if name == BSP or re.fullmatch(r"maps/stormkeep/lm_[0-9]+\.jpg", name)
        }
    fixture_paths = {Path(name).with_suffix("") for name in map_members}
    with zipfile.ZipFile(ROOT / "data/maps/stormkeep.pk3") as assets:
        with zipfile.ZipFile(maps / "stormkeep.pk3", "w", zipfile.ZIP_DEFLATED) as staged:
            for entry in assets.infolist():
                if Path(entry.filename).with_suffix("") not in fixture_paths:
                    staged.writestr(entry.filename, assets.read(entry))
            for name, content in map_members.items():
                staged.writestr(name, content)
    with zipfile.ZipFile(maps / "stormkeep.pk3") as staged:
        for name, content in map_members.items():
            if staged.read(name) != content:
                raise RuntimeError(f"native capture map member differs: {name}")
    return original, native, {
        "original_archive_sha256": digest(stock_map.read_bytes()),
        "matching_map_members_sha256": {
            name: digest(content) for name, content in map_members.items()
        },
        "original_code_sha256": {
            name: digest((args.original_code / name).read_bytes())
            for name in ("progs.dat", "csprogs.dat", "menu.dat")
        },
    }


def start(stack, cmd, file, env=None):
    with file.open("wb") as log:
        proc = subprocess.Popen(cmd, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                                stdin=subprocess.PIPE)
    stack.callback(stop, proc)
    return proc


def stop(proc):
    if proc.poll() is None:
        proc.terminate()
        try:
            proc.wait(timeout=4)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()


def wait_log(proc, file, expected, timeout=45):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if re.search(expected, file.read_text(errors="replace"), re.IGNORECASE):
            return
        if proc.poll() is not None:
            break
        time.sleep(0.2)
    raise RuntimeError(f"{file}: never saw {expected!r} (exit code {proc.poll()})")


def commands(args, engine, scenario, port, folder, data, cvars, bots):
    native = engine == "native"
    if native:
        base = [str(args.godot), "--path", str(ROOT), "--audio-driver", "Dummy",
                "--data", str(data), "--no-save-config"]
        server = base + ["--headless", "--host", "stormkeep", "--port", str(port),
                         "--gametype", "freezetag" if scenario == "freeze" else "dm",
                         "--bots", str(bots)]
        for name, value in cvars.items():
            server.extend(("--cvar", name, value))
        client = base + ["--rendering-method", "gl_compatibility", "--render-thread", "safe", "--resolution", "800x600",
                         "--position", "0,0", "--connect", f"127.0.0.1:{port}",
                         "--cvar", "cl_autopause", "0", "--cvar", "cl_maxfps", "40",
                         "--cvar", "_cl_name", "ParityProbe", "--cvar", "gl_texturecompression", "0"]
        return server, client

    base = ["-xonotic", "-basedir", str(data)]
    server = [str(args.xonotic_server), *base, "-dedicated", "-userdir", str(folder / "server-user"),
              "-ip", "127.0.0.1", "-port", str(port)]
    for name, value in cvars.items():
        server.extend((f"+{name}", value))
    server.extend(("+bot_number", str(bots), "+g_freezetag", "1" if scenario == "freeze" else "0",
                   "+map", "stormkeep"))
    client = [str(args.xonotic_client), *base, "-userdir", str(folder / "client-user"),
              "-ip", "127.0.0.1", "-nosound", "-window", "-width", "800", "-height", "600",
              "+cl_maxfps", "40", "+name", "ParityProbe", "+cl_autodemo", "1",
              "+cl_allow_uidtracking", "0", "+cl_allow_uid2name", "0", "+cl_welcome", "0",
              "+connect", f"127.0.0.1:{port}"]
    return server, client


def run_arm(args, engine, scenario, port, data):
    folder = args.output / f"{scenario}-{engine}"
    folder.mkdir()
    frames = folder / "frames"
    frames.mkdir()
    bots = 3 if scenario == "freeze" else 1
    cvars = COMMON | (FREEZE if scenario == "freeze" else CRYLINK)
    server_cmd, client_cmd = commands(args, engine, scenario, port, folder, data, cvars, bots)
    (folder / "commands.json").write_text(json.dumps({
        "server": server_cmd, "client": client_cmd, "requested_server_cvars": cvars, "bots": bots,
    }, indent=2) + "\n")
    server_log = folder / "server.log"
    client_log = folder / "client.log"
    env = os.environ.copy() | {"LIBGL_ALWAYS_SOFTWARE": "1", "LP_NUM_THREADS": "2"}
    if args.dotnet_root:
        env["DOTNET_ROOT"] = str(args.dotnet_root)
    with contextlib.ExitStack() as stack, (folder / "timeline.jsonl").open("w", buffering=1) as timeline:
        with (folder / "xvfb.log").open("wb") as log:
            display = subprocess.Popen(["Xvfb", "-displayfd", "1", "-screen", "0", "800x600x24",
                                        "-nolisten", "tcp"], stdout=subprocess.PIPE, stderr=log, text=True)
        stack.callback(stop, display)
        display_number = display.stdout.readline().strip()
        if not display_number:
            raise RuntimeError("Xvfb did not allocate a display")
        env["DISPLAY"] = f":{display_number}"
        server = start(stack, server_cmd, server_log, env | {"VORTEX_USERDIR": str(folder / "server-user")})
        wait_log(server, server_log, r"listen server on 127\.0\.0\.1|Server listening on address 127\.0\.0\.1")
        client = start(stack, client_cmd, client_log, env | {"VORTEX_USERDIR": str(folder / "client-user")})
        ready = r"client loaded map 'stormkeep'" if engine == "native" else r"ParityProbe.*connected"
        wait_log(client, client_log, ready, timeout=100)
        window = subprocess.check_output(["xdotool", "search", "--sync", "--onlyvisible",
                                          "--name", "Xonotic|Vortex"], env=env, timeout=15, text=True)
        origin = time.monotonic()

        def emit(**event):
            timeline.write(json.dumps({"elapsed": round(time.monotonic() - origin, 3), **event}) + "\n")

        def input_event(*input_args):
            subprocess.run(["xdotool", *map(str, input_args)], env=env, check=True)

        def send(command):
            emit(command=command)
            input_event("key", "grave")
            time.sleep(0.60)
            input_event("type", command)
            input_event("key", "Return")
            time.sleep(0.35)
            input_event("key", "grave")

        def status():
            for command in ("status", *sorted(cvars)):
                server.stdin.write(f"{command}\n".encode())
            server.stdin.flush()

        input_event("windowfocus", window.splitlines()[0])
        # The first images show the connected client's initial view. Join is sent
        # only after a load grace period to keep it out of the connecting screen.
        if scenario == "freeze":
            actions = [(3, "join"), (13, "status"), (20, "kill"), (26, "status"), (45, "status")]
            end = 51
            interval = 0.35
        else:
            actions = [(3, "join"), (12, "status"), (18, "teleport"),
                       (23, "press"), (24, "release"), (29, "press"), (30, "release"),
                       (35, "press"), (36, "release"), (40, "status")]
            end = 42
            interval = 0.12
        last_frame = 0.0
        frame_index = 0
        while time.monotonic() - origin < end:
            if client.poll() is not None or server.poll() is not None:
                raise RuntimeError(f"{folder}: game exited during capture")
            now = time.monotonic() - origin
            if actions and now >= actions[0][0]:
                _, action = actions.pop(0)
                if action == "join":
                    send("cmd join")
                elif action == "teleport":
                    send("cmd teleporttotarget misc_teleporter_dest1")
                elif action == "status":
                    status()
                    emit(server_query="status and scenario cvars")
                elif action == "kill":
                    send("cmd kill")
                elif action == "press":
                    input_event("mousedown", 1)
                    emit(button="attack down")
                elif action == "release":
                    input_event("mouseup", 1)
                    emit(button="attack up")
            if now - last_frame >= interval:
                frame_index += 1
                name = f"{frame_index:04}.webp"
                capture_time = round(time.monotonic() - origin, 3)
                try:
                    subprocess.run(["import", "-window", "root", "-quality", "85", str(frames / name)],
                                   env=env, check=True, timeout=8)
                except (subprocess.TimeoutExpired, subprocess.CalledProcessError) as error:
                    (frames / name).unlink(missing_ok=True)
                    emit(frame_error=str(error))
                else:
                    emit(frame=name, captured_at=capture_time)
                last_frame = now
            time.sleep(0.015)
        send("quit")
        try:
            client.wait(timeout=4)
        except subprocess.TimeoutExpired:
            stop(client)
        server.stdin.write(b"quit\n")
        server.stdin.flush()
        try:
            server.wait(timeout=4)
        except subprocess.TimeoutExpired:
            stop(server)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("output", "godot", "xonotic-client", "xonotic-server", "original-base",
                 "original-maps", "original-code"):
        parser.add_argument(f"--{name}", required=True, type=lambda p: Path(p).resolve())
    parser.add_argument("--dotnet-root", type=lambda p: Path(p).resolve())
    parser.add_argument("--port", type=int, default=27840, help="first of four adjacent game ports")
    parser.add_argument("--scenario", choices=("freeze", "crylink", "all"), default="all")
    parser.add_argument("--engine", choices=("original", "native", "both"), default="both")
    args = parser.parse_args()
    if not 1024 <= args.port <= 65524:
        parser.error("--port must leave room for the game and discovery ports")
    args.output.mkdir(parents=True)
    original, native, evidence = stage_data(args)
    evidence["runtime_sha256"] = {
        "xonotic_client": digest(args.xonotic_client.read_bytes()),
        "xonotic_server": digest(args.xonotic_server.read_bytes()),
        "godot": digest(args.godot.read_bytes()),
    }
    evidence["native_build_sha256"] = {
        name: digest((ROOT / ".godot/mono/temp/bin/Debug" / name).read_bytes())
        for name in ("VortexArena.dll", "VortexArena.Common.dll", "VortexArena.Server.dll")
    }
    (args.output / "inputs.json").write_text(json.dumps(evidence, indent=2) + "\n")
    for index, scenario in enumerate(("freeze", "crylink")):
        if args.scenario not in ("all", scenario):
            continue
        for offset, (engine, data) in enumerate((("original", original), ("native", native))):
            if args.engine in ("both", engine):
                print(f"Capturing {scenario}/{engine}", flush=True)
                run_arm(args, engine, scenario, args.port + 4 * index + 2 * offset, data)


if __name__ == "__main__":
    main()

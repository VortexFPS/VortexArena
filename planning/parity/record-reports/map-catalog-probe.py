"""Compare dedicated host map-list commands using fresh loopback profiles."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
import zipfile


ROOT = Path(__file__).resolve().parents[3]
PIN = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
ROTATION = "implosion darkzone parity-missing runningmanctf"


def wait_for(process, log, marker):
    for _ in range(240):
        if marker in log.read_text(errors="replace"):
            return
        if process.poll() is not None:
            break
        time.sleep(.25)
    raise RuntimeError(f"Missing {marker} in {log}")


def probe(args, root, stock, feature_check=False):
    label = "original" if stock else "native"
    if feature_check:
        label += "-feature"
    map_name = "darkzone" if feature_check else "implosion"
    mode = "dm" if feature_check else "ctf"
    profile = root / label
    profile.mkdir()
    log = root / f"{label}.log"
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        port = str(sock.getsockname()[1])
    env = dict(os.environ, FIXTURE_PORT=port, LD_PRELOAD=str(root / "loopback.so"),
               VORTEX_USERDIR=str(profile), XDG_DATA_HOME=str(profile / "xdg"), DOTNET_ROLL_FORWARD="Major")
    if stock:
        data = profile / "data"
        assets = data / "00-assets.pk3dir"
        assets.mkdir(parents=True)
        for name in ("models", "sound", "gfx", "particles", "scripts", "textures", "cubemaps"):
            (assets / name).symlink_to(ROOT / "data/core.pk3dir" / name)
        (data / "zz-xonotic-data.pk3dir").symlink_to(args.source)
        (data / "zzz-compiled.pk3dir").symlink_to(args.bytecode)
        for name in ("implosion", "runningmanctf", "darkzone", "shared"):
            (data / f"{name}.pk3").symlink_to(ROOT / f"data/maps/{name}.pk3")
        cmd = [args.original, "-dedicated", "-xonotic", "-basedir", profile, "-userdir", profile / "user",
               "-ip", "127.0.0.1", "-port", port, "+sv_public", "0", "+sv_autopause", "0",
               "+g_" + mode, "1", "+g_maplist", ROTATION]
        ready = "Server listening on address 127.0.0.1"
    else:
        cmd = [args.godot, "--headless", "--path", ROOT, "--dedicated", map_name, "--gametype", mode,
               "--port", port, "--no-save-config", "--cvar", "sv_public", "0"]
        if not feature_check:
            (profile / "server.cfg").write_text(
                'g_maplist "darkzone parity-missing runningmanctf"\nmaplist add "implosion"\n')
        ready = "reading commands"
    if feature_check:
        directory = profile / "data/maps"
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "darkzone.mapinfo").write_text("title Darkzone feature test\ngametype dm\n")
        for name, value in (("g_melee_only", "1"), ("g_pickup_items", "1"), ("g_weaponarena", "0")):
            cmd.extend(["+" + name, value] if stock else ["--cvar", name, value])
    if stock:
        cmd.extend(["+map", map_name])
    with log.open("w") as output, subprocess.Popen(cmd, env=env, cwd=ROOT, stdin=subprocess.PIPE,
                                                  stdout=output, stderr=subprocess.STDOUT, text=True) as process:
        try:
            wait_for(process, log, ready)
            if feature_check:
                wait_for(process, log, "lacks features required by current settings")
                print(label, next(line.strip() for line in log.read_text(errors="replace").splitlines()
                                  if "lacks features required by current settings" in line))
                return
            commands = ["lsmaps", "printmaplist", "maplist add parity-missing", "maplist cleanup", "g_maplist",
                        'maplist add "runningmanctf"', "g_maplist", "printmaplist"]
            for index, command in enumerate(commands):
                prefix = "sv_cmd " if command != "g_maplist" and (stock or index >= 5) else ""
                process.stdin.write(prefix + command + "\n")
                process.stdin.flush()
                time.sleep(.25)
            wait_for(process, log, "does not exist!")
            wait_for(process, log, '"g_maplist" is "implosion runningmanctf"')
            wait_for(process, log, '"g_maplist" is "runningmanctf implosion runningmanctf"')
        finally:
            if process.poll() is None:
                process.stdin.write("quit\n")
                process.stdin.flush()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.terminate()
                    process.wait(timeout=10)
    text = log.read_text(errors="replace")
    text = re.sub(r"\x1b\[[0-9;]*m|\^[0-9]", "", text)
    listed = next(line for line in text.splitlines() if "Maps available (" in line)
    if not all(part in listed for part in ("New maps have asterisks marked in blue", "*implosion", "*runningmanctf")) or "*darkzone" in listed:
        raise ValueError(f"{label} returned a different CTF catalog: {listed}")
    rotations = [line for line in text.splitlines() if "Maps in list (" in line]
    initial = "Maps in list (2): implosion runningmanctf"
    updated = "Maps in list (3): runningmanctf implosion runningmanctf"
    if rotations != [initial, initial if stock else updated]:
        raise ValueError(f"{label} returned unexpected rotations: {rotations}")
    for line in text.splitlines():
        if line in rotations or line == listed or "parity-missing does not exist!" in line or (
                "g_maplist" in line and "implosion runningmanctf" in line):
            print(label, line)


def native_vote_probe(args, root):
    profile = root / "native-vote"
    profile.mkdir()
    (profile / "server.cfg").write_text(
        'g_maplist darkzone\n'
        'g_maplist_votable 0\n'
        'sv_mapchange_delay 0\n'
        'sv_vote_gametype 1\n'
        'sv_vote_gametype_options parityctf\n'
        'sv_vote_gametype_parityctf_type ctf\n'
        'sv_vote_gametype_parityctf_maplist runningmanctf\n'
        'alias sv_vote_gametype_hook_parityctf "set catalog_hook_result ran"\n')
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        port = str(sock.getsockname()[1])
    env = dict(os.environ, FIXTURE_PORT=port, LD_PRELOAD=str(root / "loopback.so"),
               VORTEX_USERDIR=str(profile), XDG_DATA_HOME=str(profile / "xdg"), DOTNET_ROLL_FORWARD="Major")
    cmd = [args.godot, "--headless", "--path", ROOT, "--host", "darkzone", "--gametype", "dm",
           "--port", port, "--no-save-config", "--cvar", "sv_public", "0"]
    log = root / "native-vote.log"
    with log.open("w") as output, subprocess.Popen(cmd, env=env, cwd=ROOT, stdin=subprocess.PIPE,
                                                  stdout=output, stderr=subprocess.STDOUT, text=True) as process:
        def send(*lines):
            process.stdin.write("\n".join(lines) + "\n")
            process.stdin.flush()

        try:
            wait_for(process, log, "handshake accepted")
            send("endmatch")
            wait_for(process, log, "gametype=ctf map=runningmanctf")
            wait_for(process, log, "host runningmanctf")
            send("catalog_hook_result", "lsmaps")
            wait_for(process, log, '"catalog_hook_result" is "ran"')
            wait_for(process, log, "Maps available (")
        finally:
            if process.poll() is None:
                send("quit")
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.terminate()
                    process.wait(timeout=10)
    reply = next(line for line in log.read_text(errors="replace").splitlines() if "Maps available (" in line)
    if "*runningmanctf" not in reply or "darkzone" in reply:
        raise ValueError(f"Unexpected vote destination catalog in {log}: {reply}")
    print("native vote: Darkzone DM -> Running Man CTF; startup alias ran; CTF catalog listed")


def offline_probe(args, root):
    profile = root / "offline"
    maps = profile / "data/maps"
    maps.mkdir(parents=True)
    with zipfile.ZipFile(ROOT / "data/maps/darkzone.pk3") as pack:
        bsp = pack.read("maps/darkzone.bsp")
    for name, features in (("parity-armed", "has weapons\n"), ("parity-unarmed", "")):
        (maps / f"{name}.bsp").write_bytes(bsp)
        (maps / f"{name}.mapinfo").write_text(f"gametype dm\n{features}")
    (maps / "prefabs").mkdir()
    (maps / "prefabs/parity-nested.bsp").write_bytes(bsp)
    log = root / "offline.log"
    env = dict(os.environ, VORTEX_USERDIR=str(profile), XDG_DATA_HOME=str(profile / "xdg"),
               DOTNET_ROLL_FORWARD="Major", LP_NUM_THREADS="2")
    cmd = [args.godot, "--path", ROOT, "--rendering-method", "gl_compatibility", "--render-thread", "safe",
           "--audio-driver", "Dummy", "--no-save-config", "--cvar", "vid_fullscreen", "0",
           "--cvar", "g_melee_only", "1", "--cvar", "g_pickup_items", "1", "--cvar", "g_weaponarena", "0"]
    display_path = root / "xvfb-display"
    with display_path.open("wb") as display:
        xvfb = subprocess.Popen(["Xvfb", "-displayfd", str(display.fileno()), "-screen", "0", "1200x800x24",
                                 "-nolisten", "tcp"], pass_fds=[display.fileno()])
        try:
            for _ in range(100):
                if number := display_path.read_text().strip():
                    break
                time.sleep(.1)
            else:
                raise RuntimeError("Xvfb did not start")
            env["DISPLAY"] = f":{number}"
            with log.open("w") as output, subprocess.Popen(cmd, env=env, cwd=ROOT,
                                                           stdout=output, stderr=subprocess.STDOUT) as process:
                try:
                    wait_for(process, log, "user gamedir mounted")
                    window = subprocess.check_output(["xdotool", "search", "--sync", "--onlyvisible",
                                                      "--pid", str(process.pid)], env=env, text=True,
                                                     timeout=20).splitlines()[0]
                    time.sleep(3)
                    subprocess.run(["xdotool", "windowfocus", window, "key", "grave"], env=env, check=True)
                    time.sleep(.5)
                    for line in ("lsmaps dm", "g_melee_only 0", "lsmaps dm"):
                        subprocess.run(["xdotool", "type", "--clearmodifiers", line], env=env, check=True)
                        subprocess.run(["xdotool", "key", "Return"], env=env, check=True)
                        time.sleep(.5)
                    wait_for(process, log, "parity-unarmed")
                finally:
                    if process.poll() is None:
                        process.terminate()
                        process.wait(timeout=10)
        finally:
            xvfb.terminate()
            xvfb.wait(timeout=10)
    listings = [line.split("Maps available", 1)[1] for line in log.read_text(errors="replace").splitlines()
                if "Maps available" in line]
    if (len(listings) != 2 or "parity-armed" not in listings[0]
            or "parity-unarmed" in listings[0] or "parity-unarmed" not in listings[1]
            or any("parity-nested" in line or "asterisks" in line for line in listings)):
        raise ValueError(f"Unexpected offline listings in {log}: {listings}")
    print("offline melee: armed BSP listed, unarmed BSP excluded, nested BSP omitted, no record markers")
    print("offline melee disabled: unarmed BSP listed immediately")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("source", "bytecode", "original", "godot"):
        parser.add_argument("--" + key, type=lambda path: Path(path).resolve(strict=True), required=True)
    parser.add_argument("--offline", action="store_true", help="also drive the offline Godot console under Xvfb")
    args = parser.parse_args()
    pin = subprocess.check_output(["git", "-C", args.source, "rev-parse", "HEAD"], text=True).strip()
    if pin != PIN:
        parser.error(f"Expected {PIN}, got {pin}")
    root = Path(tempfile.mkdtemp(prefix="vortex-map-command-"))
    print("Run directory", root, flush=True)
    for path in (args.bytecode / "progs.dat", ROOT / "data/maps/implosion.pk3"):
        print(path.name, hashlib.sha256(path.read_bytes()).hexdigest())
    subprocess.run(["cc", "-shared", "-fPIC", "-o", root / "loopback.so",
                    ROOT / "planning/parity/record-lifecycle/earned/loopback.c", "-ldl"], check=True)
    probe(args, root, True)
    probe(args, root, False)
    probe(args, root, True, feature_check=True)
    probe(args, root, False, feature_check=True)
    native_vote_probe(args, root)
    if args.offline:
        offline_probe(args, root)

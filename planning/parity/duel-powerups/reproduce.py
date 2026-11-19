"""Exercise Duel's map-item filter through the two dedicated consoles, using private profiles."""

import argparse
import hashlib
import os
from pathlib import Path
import re
import struct
import subprocess
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[3]
PIN = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
ITEMS = ("item_strength", "item_shield", "item_speed", "item_invisibility", "item_jetpack", "item_fuel_regen")
ALIASES = ("item_invincible", "item_haste")
CONTROL = "item_health_small"


def make_overlay(path, pickups):
    with zipfile.ZipFile(ROOT / "data/maps/stormkeep.pk3") as pack:
        bsp = pack.read("maps/stormkeep.bsp")
    offset, size = struct.unpack_from("<II", bsp, 8)
    world = re.search(rb"\{[^{}]*\}", bsp[offset:offset + size])[0].decode("latin1")
    entities = [world]
    for i, name in enumerate(("info_player_deathmatch", "info_player_deathmatch", *pickups, CONTROL)):
        entities.append(f'{{\n"classname" "{name}"\n"origin" "1216 {184 + i * 28} 16"\n}}')
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(entities) + "\n")


def wait(log, token, start, proc):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline and proc.poll() is None:
        data = log.read_text(errors="replace")[start:]
        if token in data:
            return data
        time.sleep(0.05)
    raise RuntimeError(f"{log}: missing {token!r}")


def run(engine, command, env, out, overlay):
    log = out / f"{engine}.log"
    ready = "Loaded maps/stormkeep.ent" if engine == "stock" else "host stormkeep — load begin → ready"
    with log.open("w") as stream, subprocess.Popen(command, stdin=subprocess.PIPE, stdout=stream,
            stderr=subprocess.STDOUT, env=env, text=True, cwd=ROOT) as proc:
        def send(command):
            proc.stdin.write(command + "\n")
            proc.stdin.flush()
        try:
            boot = wait(log, ready, 0, proc)
            for index, enabled in enumerate((0, 1, 0, 1)):
                aliases_only = index == 3
                if aliases_only:
                    make_overlay(overlay, ALIASES)
                if index:
                    cursor = len(log.read_text(errors="replace"))
                    send(f"g_duel_with_powerups {enabled}")
                    send("map stormkeep")
                    boot = wait(log, ready, cursor, proc)
                if engine == "native" and ("gametype=duel map=stormkeep" not in boot or "DEDICATED: no local client" not in boot):
                    raise RuntimeError(f"{log}: did not boot a dedicated Duel match")
                time.sleep(0.7)
                cursor = len(log.read_text(errors="replace"))
                if engine == "stock":
                    send("g_duel")
                send("g_duel_with_powerups")
                for name in (*ITEMS, CONTROL, *ALIASES):
                    send(f"sv_cmd findent {name}")
                token = f"DUEL_ITEMS_END_{index}" if engine == "stock" else '"g_powerups_jetpack" is "1"'
                send(f"echo {token}" if engine == "stock" else "g_powerups_jetpack")
                data = wait(log, token, cursor, proc)
                if not re.search(rf'"g_duel_with_powerups" is "{enabled}"', data):
                    raise RuntimeError(f"{log}: missing cvar readback for phase {index}")
                if engine == "stock" and '"g_duel" is "1"' not in data:
                    raise RuntimeError(f"{log}: stock Duel was inactive")
                counts = [len(re.findall(rf"^\s*\d+ \({name}\)", data, re.M)) for name in (*ITEMS, CONTROL, *ALIASES)]
                print(engine, "aliases" if aliases_only else "all", enabled, counts, flush=True)
                expected = [enabled, 2 * enabled, 2 * enabled, enabled, enabled, enabled, 1, 0, 0]
                if aliases_only:
                    expected = [0, 1, 1, 0, 0, 0, 1, 0, 0]
                if counts != expected:
                    raise RuntimeError(f"{log}: expected {expected}, observed {counts}\n{data[-2400:]}")
            send("quit")
            if proc.wait(timeout=12) != 0:
                raise RuntimeError(f"{engine} returned {proc.returncode} on shutdown")
        finally:
            if proc.poll() is None:
                proc.terminate()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True, type=Path)
    parser.add_argument("--stock", type=Path, default=Path("/tmp/va-reviewed-stock"))
    parser.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--port", type=int, default=30497)
    args = parser.parse_args()
    if subprocess.check_output(["git", "-C", args.base, "rev-parse", "HEAD"], text=True).strip() != PIN:
        raise RuntimeError(f"{args.base} must be at Xonotic data commit {PIN}")
    program = args.stock / "compiled/progs.dat"
    expected_hash = "c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7"
    if hashlib.sha256(program.read_bytes()).hexdigest() != expected_hash:
        raise RuntimeError(f"{program} differs from the recorded server QC build")
    out = Path(tempfile.mkdtemp(prefix="va-duel-items."))
    print(out, flush=True)
    env = os.environ | {
        "VORTEX_USERDIR": str(out / "native"), "VORTEX_NO_MASTER_QUERY": "1",
        "XDG_DATA_HOME": str(out / "xdg-data"), "XDG_CONFIG_HOME": str(out / "xdg-config"),
        "XDG_CACHE_HOME": str(out / "xdg-cache"), "DOTNET_ROLL_FORWARD": "Major",
    }
    base = out / "stock"
    stock_ent = base / "data/maps/stormkeep.ent"
    make_overlay(stock_ent, (*ITEMS, *ALIASES))
    native_ent = out / "native/data/maps/stormkeep.ent"
    native_ent.parent.mkdir(parents=True)
    native_ent.write_bytes(stock_ent.read_bytes())
    mounts = {
        "xonotic-data.pk3dir": args.base, "zz-pinned-code.pk3dir": args.stock / "compiled",
        "shared.pk3": ROOT / "data/maps/shared.pk3", "stormkeep.pk3": ROOT / "data/maps/stormkeep.pk3",
    }
    for name, source in mounts.items():
        (base / "data" / name).symlink_to(source.resolve())
    settings = {
        "sv_public": "-2", "g_playerstats_gamereport_uri": "", "g_playerstats_playerbasic_uri": "",
        "bot_number": "0", "g_powerups": "1", "g_powerups_jetpack": "1", "g_powerups_fuelregen": "1",
        "g_powerups_invisibility": "1", "g_powerups_speed": "1", "g_powerups_strength": "1", "g_powerups_shield": "1",
    }
    stock = [str(args.stock / "xonotic-linux64-sdl"), "-xonotic", "-dedicated", "-ip", "127.0.0.1",
             "+net_address_ipv6", "::1", "-port", str(args.port), "-basedir", str(base), "-userdir", str(out / "stock-user")]
    stock_env = env | {"LD_LIBRARY_PATH": str(args.stock / "sdl/usr/lib/x86_64-linux-gnu")
                                         + ":" + env.get("LD_LIBRARY_PATH", "")}
    for key, value in settings.items():
        stock.extend(["+" + key, value])
    run("stock", stock + ["+g_duel", "1", "+map", "stormkeep"], stock_env, out, stock_ent)
    native = [str(args.godot.resolve()), "--headless", "--path", str(ROOT), "--no-save-config",
              "--dedicated", "stormkeep", "--gametype", "duel", "--port", str(args.port)]
    for key, value in settings.items():
        native.extend(["--cvar", key, value])
    run("native", native, env, out, native_ent)


if __name__ == "__main__":
    main()

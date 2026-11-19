"""Compare generated KeyHunt map eligibility and authored match settings on dedicated hosts."""

import argparse
import hashlib
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import zipfile


ROOT = Path(__file__).resolve().parents[3]
PIN = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
QC = "c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7"
BARE = {"khfewstarts": (11, 5121), "khexactdiam": (12, 5120), "khlargeenough": (12, 5121)}
AUTHORED = {
    "khmodern": "gametype kh pointlimit=650 timelimit=18 teams=4 leadlimit=75",
    "khlegacy": "type kh 700 17 4 85",
    "khdefault": "gametype kh",
}
READBACK = ("fraglimit", "timelimit", "g_keyhunt_teams", "leadlimit", "g_keyhunt_teams_override")


def entity(name, origin):
    return f'{{\n"classname" "{name}"\n"origin" "{origin}"\n}}\n'


def make_pack(path):
    with zipfile.ZipFile(ROOT / "data/maps/stormkeep.pk3") as stock:
        bsp = stock.read("maps/stormkeep.bsp")
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as pack:
        for name in (*BARE, *AUTHORED):
            starts, diameter = BARE.get(name, (12, 5121))
            entities = '{\n"classname" "worldspawn"\n"message" "KeyHunt fixture"\n}\n'
            for i in range(starts):
                entities += entity("info_player_deathmatch", f"{diameter if i == 0 else i} 0 0")
            entities += entity("weapon_shotgun", "0 0 0")
            pack.writestr(f"maps/{name}.bsp", bsp)
            pack.writestr(f"maps/{name}.ent", entities)
            if name in AUTHORED:
                pack.writestr(f"maps/{name}.mapinfo", f"title {name}\nhas weapons\n{AUTHORED[name]}\n")


def run(engine, command, env, out):
    log = out / f"{engine}.log"

    def wait_for(marker, cursor):
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline and proc.poll() is None:
            text = log.read_text(errors="replace")[cursor:]
            if marker in text:
                return text
            time.sleep(.05)
        raise RuntimeError(f"{log}: missing {marker}")

    def send(*commands):
        proc.stdin.write("\n".join(commands) + "\n")
        proc.stdin.flush()

    phases = [
        ("khmodern", (), (650, 18, 4, 75, 0)),
        ("khmodern", ("g_keyhunt_point_limit 400", "g_keyhunt_point_leadlimit 0",
                      "g_keyhunt_teams_override 2", "fraglimit_override 5"), (400, 18, 4, 0, 2)),
        ("khlegacy", ("g_keyhunt_point_limit -1", "g_keyhunt_point_leadlimit -1",
                      "g_keyhunt_teams_override 0", "fraglimit_override -1"), (700, 17, 4, 85, 0)),
        ("khdefault", (), (1000, 20, 3, 0, 0)),
        ("khdefault", ("g_keyhunt_point_limit 0", "fraglimit_override 5"), (0, 20, 3, 0, 0)),
    ]
    with log.open("w") as stream, subprocess.Popen(command, stdin=subprocess.PIPE, stdout=stream,
            stderr=subprocess.STDOUT, text=True, env=env, cwd=ROOT) as proc:
        try:
            if engine == "stock":
                # Stock can stop the boot catalog pass after generating one missing sidecar.
                # Warm every bare map in the private profile before sampling its cached reply.
                cursor = 0
                for _ in BARE:
                    wait_for("Loaded maps/khmodern.ent", cursor)
                    time.sleep(1.2)
                    cursor = len(log.read_text(errors="replace"))
                    send("map khmodern")
                wait_for("Loaded maps/khmodern.ent", cursor)
            for i, (name, settings, expected) in enumerate(phases):
                cursor = len(log.read_text(errors="replace")) if i else 0
                if i:
                    send(*settings, "map " + name)
                marker = f"Loaded maps/{name}.ent" if engine == "stock" else f"host {name} — load begin → ready"
                wait_for(marker, cursor)
                time.sleep(.8)
                cursor = len(log.read_text(errors="replace"))
                send(*READBACK, "sv_cmd lsmaps" if engine == "stock" else "lsmaps", "g_keyhunt_point_limit")
                text = wait_for('"g_keyhunt_point_limit" is "', cursor)
                values = [float(re.search(rf'^"{key}" is "([^"]+)"', text, re.M)[1]) for key in READBACK]
                plain = re.sub(r"\x1b\[[\d;]*m|\^\d", "", text)
                catalog = re.search(r"^Maps available \(\d+\): ([^\n]*)", plain, re.M)
                if catalog is None:
                    raise RuntimeError(f"Missing hosted map catalog. See {log}")
                listed = catalog[1].split()
                visible = [map_name for map_name in BARE if map_name in listed]
                print(engine, name, values, visible, flush=True)
                if values != list(expected) or visible != ["khlargeenough"]:
                    raise RuntimeError(f"Unexpected settings or bare map catalog. See {log}\n{text[-4000:]}")
            send("quit")
            if proc.wait(timeout=12):
                raise RuntimeError(f"Unclean exit. See {log}")
        finally:
            if proc.poll() is None:
                proc.terminate()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", type=Path, required=True)
    parser.add_argument("--stock", type=Path, default=Path("/tmp/va-reviewed-stock"))
    parser.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--port", type=int, default=30520)
    args = parser.parse_args()
    head = subprocess.check_output(["git", "-C", args.base, "rev-parse", "HEAD"], text=True).strip()
    if head != PIN or hashlib.sha256((args.stock / "compiled/progs.dat").read_bytes()).hexdigest() != QC:
        raise RuntimeError("The Xonotic source or compiled server program differs from the recorded input")
    out = Path(tempfile.mkdtemp(prefix="va-keyhunt-map."))
    print(out, flush=True)
    pack = out / "zz-keyhunt.pk3"
    make_pack(pack)
    for engine in ("stock", "native"):
        root = out / engine / "data"
        root.mkdir(parents=True)
        (root / pack.name).symlink_to(pack)
    root = out / "stock/data"
    for name, path in [("xonotic-data.pk3dir", args.base), ("zz-pinned-code.pk3dir", args.stock / "compiled"),
                       ("shared.pk3", ROOT / "data/maps/shared.pk3")]:
        (root / name).symlink_to(path.resolve())
    env = os.environ | {"VORTEX_USERDIR": str(out / "native"), "VORTEX_NO_MASTER_QUERY": "1",
        "XDG_DATA_HOME": str(out / "xdg-data"), "XDG_CACHE_HOME": str(out / "xdg-cache"),
        "XDG_CONFIG_HOME": str(out / "xdg-config"), "DOTNET_ROLL_FORWARD": "Major"}
    stock_env = env | {"LD_LIBRARY_PATH": str(args.stock / "sdl/usr/lib/x86_64-linux-gnu")
                                         + ":" + env.get("LD_LIBRARY_PATH", "")}
    run("stock", [str(args.stock / "xonotic-linux64-sdl"), "-xonotic", "-dedicated", "-ip", "127.0.0.1",
        "+net_address_ipv6", "::1", "-port", str(args.port), "-basedir", str(root.parent), "-userdir",
        str(out / "stock-user"), "+sv_public", "-2", "+g_playerstats_gamereport_uri", "",
        "+g_playerstats_playerbasic_uri", "", "+g_keyhunt", "1", "+map", "khmodern"], stock_env, out)
    run("native", [str(args.godot.resolve()), "--headless", "--path", str(ROOT), "--no-save-config",
        "--dedicated", "khmodern", "--gametype", "kh", "--port", str(args.port),
        "--cvar", "sv_public", "-2"], env, out)


if __name__ == "__main__":
    main()

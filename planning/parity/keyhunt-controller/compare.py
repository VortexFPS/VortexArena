#!/usr/bin/env python3
"""Probe KeyHunt on the same mounted Stormkeep archive in two local servers."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[3]
SETTINGS = {
    "sv_public": -2, "sv_autopause": 0, "sys_ticrate": 0.015625,
    "g_keyhunt_teams_override": 2, "g_start_delay": 4, "g_warmup_limit": -1,
    "g_balance_keyhunt_delay_round": 1.25, "g_balance_keyhunt_delay_tracking": 1.25,
    "bot_join_empty": 1, "bot_nofire": 1, "g_pickup_items": 0,
    "bot_ai_strategyinterval_movingtarget": 0.5, "sv_eventlog": 1,
    "g_playerstats_gamereport_uri": '""', "g_playerstats_playerbasic_uri": '""',
    "g_playerstats_playerdetail_uri": '""',
}


def observe_native(stage, env):
    inputs = (
        "Main.cs", "Main.cs.uid", "Main.tscn", "project.godot", "VortexArena.csproj",
        "Directory.Build.props", "game", "src", ".godot",
    )
    subprocess.run(["rsync", "-a", *(ROOT / name for name in inputs), stage], check=True)
    (stage / "data").symlink_to(ROOT / "data")
    target = stage / "src/VortexArena.Server/GameWorld.cs"
    source = target.read_text()
    at = "            case KeyHunt kh: kh.Tick(KeyHuntGameStartTime, GameStopped); break;"
    assert source.count(at) == 1
    target.write_text(source.replace(at, """            case KeyHunt kh:
                kh.Tick(KeyHuntGameStartTime, GameStopped);
                var keys = string.Join(",", kh.WorldKeys.Select(k => $"{k.HomeTeam}:{k.Carrier?.Team}"));
                var goals = string.Join(",", Bots.Brains.Select(b =>
                    b.Nav.GoalEntity is Player p ? $"player:{p.Team}" : b.Nav.GoalEntity?.ClassName ?? "-"));
                var state = $"warmup={Warmup.WarmupStage} phase={kh.Phase} tracking={kh.TrackingEnabled} keys={keys} goals={goals}";
                if (state != _keyHuntProbeLast)
                {
                    _keyHuntProbeLast = state;
                    System.Console.WriteLine(FormattableString.Invariant($"KH_PROBE time={Time:F4} start={KeyHuntGameStartTime:F4} {state}"));
                }
                break;""").replace("    private void DriveGametypeFrame()", """    private string? _keyHuntProbeLast;

    private void DriveGametypeFrame()"""))
    bind = stage / "game/net/NetTransport.cs"
    source = bind.read_text()
    at = "            Error err = s.Peer.CreateServer(port, maxClients, NetProtocol.ChannelCount);"
    assert source.count(at) == 1
    bind.write_text(source.replace(at, '            s.Peer.SetBindIP("127.0.0.1");\n' + at))
    with (stage / "build.log").open("wb") as output:
        subprocess.run(["dotnet", "build", stage / "VortexArena.csproj", "--no-restore", "--nologo"],
                       env=env, stdout=output, stderr=subprocess.STDOUT, check=True)


def run(folder, argv, env, stock):
    folder.mkdir()
    env = env | {name: str(folder / "profile") for name in (
        "VORTEX_USERDIR", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_CONFIG_HOME")}
    log = folder / "server.log"
    traced = False
    with log.open("wb") as output:
        process = subprocess.Popen(argv, env=env, stdin=subprocess.PIPE, stdout=output, stderr=subprocess.STDOUT)
        try:
            limit = time.monotonic() + 25
            while time.monotonic() < limit and process.poll() is None:
                text = log.read_text(errors="replace")
                if stock and "Server listening on address" in text:
                    if not traced:
                        process.stdin.write(b"prvm_printfunction server kh_waypointsprite_visible_for_bot; prvm_breakpoint server kh_waypointsprite_visible_for_bot\n")
                        traced = True
                    controllers = re.findall(r"kh_controller: entity (\d+)", text)
                    controller = int(controllers[-1]) if controllers else 0
                    head = re.findall(r"kh_worldkeylist: entity (\d+)", text)
                    head = int(head[-1]) if head else 0
                    fields = "; ".join(f"prvm_global server {name}" for name in (
                        "time", "game_starttime", "warmup_stage", "kh_tracking_enabled", "kh_worldkeylist",
                        "kh_controller", "kh_Controller_Thinkfunc"))
                    if controller:
                        fields += (f"; echo KH_CONTROLLER; prvm_edictget server {controller} cnt"
                                   f"; prvm_edictget server {controller} nextthink")
                    extra = ""
                    if head:
                        extra = "; echo KH_EDICTS; " + "; ".join(
                            f"prvm_edictget server {edict} {field}"
                            for edict, field in ((head, "count"), (head, "owner"), (head, "kh_worldkeynext"),
                                                 (1, "team"), (1, "goalstack01"), (1, "waypointsprite_attachedforcarrier"),
                                                 (2, "team"), (2, "goalstack01"), (2, "waypointsprite_attachedforcarrier")))
                    process.stdin.write(f"echo KH_SAMPLE; {fields}{extra}\n".encode())
                    process.stdin.flush()
                    if "kh_tracking_enabled: 1" in text:
                        break
                elif not stock and re.search(r"KH_PROBE .* tracking=True keys=\S+", text):
                    break
                time.sleep(0.22)
            else:
                raise RuntimeError(f"Tracking-enable observation missing in {log}")
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=6)


def fingerprint(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", type=Path, required=True)
    parser.add_argument("--pin", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--stock", type=Path, default=Path("/tmp/va-reviewed-stock"))
    parser.add_argument("--port", type=int, default=28439)
    parser.add_argument("--variant", choices=("start", "warmup"), default="start")
    parser.add_argument("--engine", choices=("both", "stock", "native"), default="both")
    args = parser.parse_args()
    pin = subprocess.check_output(["git", "-C", args.pin, "rev-parse", "HEAD"], text=True).strip()
    assert pin == "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
    root = Path(tempfile.mkdtemp(prefix="va-keyhunt-pair."))
    print(root, flush=True)
    env = os.environ | {"DOTNET_ROLL_FORWARD": "Major"}
    settings = SETTINGS | {"g_warmup": int(args.variant == "warmup")}
    mounted = ROOT / "data/maps"
    hashes = [mounted / "stormkeep.pk3", args.stock / "compiled/progs.dat"]
    (root / "inputs.txt").write_text("\n".join(f"{fingerprint(path)}  {path}" for path in hashes) + "\n")

    if args.engine in ("both", "stock"):
        data = root / "stock-overlay/data"
        data.mkdir(parents=True)
        for target, name in ((args.pin, "xonotic-data.pk3dir"), (args.stock / "compiled", "zz-pinned-code.pk3dir"),
                             (mounted / "stormkeep.pk3", "stormkeep.pk3"), (mounted / "shared.pk3", "shared.pk3")):
            (data / name).symlink_to(target)
        profile = root / "stock/profile"
        argv = [args.stock / "xonotic-linux64-sdl", "-xonotic", "-dedicated", "-ip", "127.0.0.1",
                "-port", str(args.port), "-basedir", data.parent, "-userdir", profile]
        for name, value in (settings | {"bot_number": 2, "g_keyhunt": 1}).items():
            argv.extend((f"+{name}", str(value)))
        run(root / "stock", [*argv, "+map", "stormkeep"],
            env | {"LD_LIBRARY_PATH": str(args.stock / "sdl/usr/lib/x86_64-linux-gnu")}, stock=True)

    if args.engine in ("both", "native"):
        stage = root / "native-project"
        stage.mkdir()
        observe_native(stage, env)
        argv = [args.godot, "--headless", "--path", stage, "--dedicated", "stormkeep",
                "--gametype", "kh", "--bots", "2", "--port", str(args.port), "--no-save-config"]
        for name, value in (settings | {"bot_neural_bake": 0}).items():
            argv.extend(("--cvar", name, str(value).strip('"')))
        run(root / "native", argv, env, stock=False)


if __name__ == "__main__":
    main()

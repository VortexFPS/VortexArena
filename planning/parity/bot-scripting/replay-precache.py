#!/usr/bin/env python3
"""Verify native enqueue-time sound loading on listen and dedicated hosts."""
import argparse
from contextlib import ExitStack
import os
from pathlib import Path
import sys
import time

sys.dont_write_bytecode = True
from replay import ROOT, Log, stage_cues

RELEASE = ":BOTPRECACHE_RELEASE"
DEFAULT = "precache 'botprobe/default.wav' mounted=True mixer=True"
EXPLICIT = "precache 'botprobe/explicit.wav' mounted=True mixer=True"


def profile(folder, role):
    path = folder / role
    stage_cues(path / "user/data/probe.pk3dir")
    (path / "user/data/probe.pk3dir/warm.bot").write_text(
        "sv_cmd bot_cmd 1 wait 2\n"
        "sv_cmd bot_cmd 1 sound 4 0.5 2 botprobe/explicit.wav\n"
        "sv_cmd bot_cmd 1 cc say BOTPRECACHE_RELEASE\n"
    )
    return os.environ | {
        "VORTEX_USERDIR": str(path / "user"),
        "XDG_DATA_HOME": str(path / "xdg-data"),
        "XDG_CONFIG_HOME": str(path / "xdg-config"),
        "XDG_CACHE_HOME": str(path / "xdg-cache"),
        "DOTNET_ROLL_FORWARD": "Major",
        "VORTEX_AUDIO_TRACE": "1",
    }


def run(args, mode, port):
    folder = args.output / mode
    folder.mkdir()
    start = time.monotonic()

    def now():
        return time.monotonic() - start

    listen = mode == "listen"
    base = [args.godot, "--headless", "--path", ROOT, "--audio-driver", "Dummy", "--no-save-config",
            "--cvar", "sv_public", 0, "--cvar", "snd_darkplaces", 1, "--cvar", "cl_frameprofiler", 0]
    with ExitStack() as stack:
        server = Log(stack, base + ["--host" if listen else "--dedicated", "darkzone",
                                   "--port", port, "--gametype", "dm", "--bots", 0,
                                   "--cvar", "bot_join_empty", 1, "--cvar", "bot_nofire", 1,
                                   "--cvar", "g_warmup", 0, "--cvar", "g_start_delay", 0,
                                   "--cvar", "sv_dedicated_slim", int(not listen),
                                   "--cvar", "sv_eventlog", 1, "--cvar", "sv_eventlog_console", 1],
                     profile(folder, "server-profile"), folder, "server", now)
        server.expect("reading commands from stdin")
        server.send("sv_cmd bot_cmd setbots 1")
        server.expect(":join:")
        if listen:
            client = server
            server.expect("handshake accepted")
        else:
            # Register a cue before a client exists, then remove it from the queue. Its map cache entry
            # still has to reach a late joiner and must not run the dedicated server's audio loader.
            server.send("sv_cmd bot_cmd 1 wait 4")
            server.expect("sent to bot")
            server.send("sv_cmd bot_cmd 1 sound botprobe/default.wav")
            server.expect("Command 'sound botprobe/default.wav' sent to bot")
            server.send("sv_cmd bot_cmd reset")
            client = Log(stack, base + ["--connect", f"127.0.0.1:{port}",
                                        "--cvar", "_cl_name", "PrecacheProbe"],
                         profile(folder, "client-profile"), folder, "observer", now)
            client.expect(DEFAULT, timeout=65)
            client.expect("client loaded map 'darkzone'", timeout=65)
        load = now()
        server.send("sv_cmd bot_cmd load warm.bot")
        server.expect("3 commands read")
        client.expect(EXPLICIT, timeout=5)
        server.expect(RELEASE, timeout=8)
        warm = next(t for t, line in client.lines if EXPLICIT in line)
        release = next(t for t, line in server.lines if RELEASE in line)
        assert load <= warm < release and release - warm >= 1, (mode, warm, release)
        assert listen or not any("precache 'botprobe/" in line for _, line in server.lines)
        events = [(load, "server: load warm.bot"), (warm, EXPLICIT), (release, "server: " + RELEASE[1:])]
        if not listen:
            events.append((next(t for t, line in client.lines if DEFAULT in line), DEFAULT + " (late join)"))
        (folder / "evidence.txt").write_text("".join(f"{t-load:+.3f} {line}\n" for t, line in sorted(events)))
        if not listen:
            client.close()
        server.send("quit")
        server.proc.wait(timeout=8)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--port", type=int, default=29481)
    parser.add_argument("--godot", type=Path, default=next(Path("/tmp/vortex-parity-godot").glob("*/*.x86_64"), None))
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir()
    run(args, "listen", args.port)
    run(args, "dedicated", args.port + 2)


if __name__ == "__main__":
    main()

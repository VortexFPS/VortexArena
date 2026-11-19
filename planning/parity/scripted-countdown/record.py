#!/usr/bin/env python3
"""Record clone and non-silent kill countdown from a second graphical client."""
import argparse
import contextlib
import importlib.util
from pathlib import Path
import sys
import time

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("objectives", HERE.parent / "scripted-objectives" / "record.py")
objectives = importlib.util.module_from_spec(SPEC)
sys.dont_write_bytecode = True
SPEC.loader.exec_module(objectives)
objectives.CVAR_CRY = {
    "g_balance_kill_delay": 4,
    "g_balance_kill_antispam": 0,
    "sv_clones": 2,
}
objectives.DESTINATIONS |= {
    "clone": (-157, -230, 45, 180, 0),
    "player": (-157, -185, 45, 180, 0),
}


class CountdownSession(objectives.Session):
    def __init__(self, args, folder, data):
        super().__init__(args, folder, data)
        self.screen_size = (480, 360) if self.native else (640, 480)

    def launch(self, stack, name, display=None, extra=()):
        extra = list(extra)
        if display:
            if self.native:
                extra[extra.index("cl_maxfps") + 1] = 24
                extra[extra.index("gl_texturecompression") + 1] = 1
                extra.extend(("--cvar", "cl_idle_warmup", 0, "--cvar", "r_glsl_deluxemapping", 0,
                              "--cvar", "gl_picmip", 2, "--cvar", "showfps", 0,
                              "--cvar", "r_water", 0, "--cvar", "r_bloom", 0,
                              "--cvar", "r_pvs_cull_entities", 0))
                if name == "RedProbe":
                    extra.extend(("--cvar", "hud_shownames", 0))
            else:
                extra[extra.index("+cl_maxfps") + 1] = 24
                extra.extend(("+r_shadow_realtime_world", 0,
                              "+r_shadow_realtime_dlight", 0, "+r_glsl_deluxemapping", 0))
        if name == "server":
            if self.native:
                extra.extend(("--cvar", "sys_ticrate", "0.033333"))
            else:
                extra[0:0] = ["+sys_ticrate", "0.033333",
                              "+sv_cullentities_pvs", 0, "+sv_cullentities_trace", 0]
        proc, log = super().launch(stack, name, display, extra)
        if name == "server" and not self.native:
            objectives.paired.wait_log(proc, log, r"Server spawned\.")
        # The dedicated console records every join. Client consoles can omit their own join message.
        return proc, self.server_log if display and not self.native else log

    def run(self, stack):
        self.send("BlueProbe", "cmd join")
        if self.native:
            proc, _ = self.clients["BlueProbe"]
            objectives.paired.wait_log(proc, self.folder / "BlueProbe.log", r"joined the game", timeout=30)
        if not self.native:
            self.send("RedProbe", "cmd spectate")
            x, y, z, yaw, pitch = objectives.CAMERA
            self.server_cmd(f'prvm_edictset server 1 origin "{x} {y} {z}"',
                            f'prvm_edictset server 1 angles "{pitch} {yaw} 0"',
                            "prvm_edictset server 1 fixangle 1")
        self.server_cmd("status", *objectives.CVAR_CRY)
        settle = 8 if self.native else 0
        self.teleport("BlueProbe", "clone")
        time.sleep(settle)
        self.emit(phase="spawn standing body")
        self.send("BlueProbe", "cmd impulse 142" if self.native else "impulse 142")
        time.sleep(settle)
        self.teleport("BlueProbe", "player")
        self.send("BlueProbe", 'bind F8 "kill"')
        time.sleep(settle + 3)
        self.start_frames(stack, "RedProbe", 0.125)
        self.start_frames(stack, "BlueProbe", 0.25)
        time.sleep(1)
        self.emit(phase="press kill")
        self.input("BlueProbe", "keydown", "F8")
        time.sleep(0.15)
        self.input("BlueProbe", "keyup", "F8")
        objectives.paired.wait_log(self.server, self.server_log, r":kill:suicide:", timeout=30)
        self.emit(phase="server recorded suicide")
        time.sleep(settle + 2)
        self.emit(phase="after countdown")
        self.server_cmd("status")
        time.sleep(0.2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--engine", required=True, choices=("original", "native"))
    parser.add_argument("--port", required=True, type=int, choices=range(26900, 26920), metavar="26900-26919")
    parser.add_argument("--display", required=True, type=int, choices=range(171, 174), metavar="171-173",
                        help="first Xvfb display (the second client uses the next display, up to 174)")
    parser.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--maps", type=Path, default=Path("/tmp/va-original-maps"))
    parser.add_argument("--code", type=Path, default=Path("/tmp/va-paired-run/compiled.pk3dir"))
    parser.add_argument("--dp", type=Path, default=Path("/tmp/va-paired-20261119/upstream-engine"))
    parser.add_argument("--sdl-libs", type=Path, default=Path("/tmp/va-paired-20261119/dependencies/usr/lib/x86_64-linux-gnu"))
    parser.add_argument("--godot", type=Path, default=next(Path("/tmp/vortex-parity-godot").glob("*/*.x86_64"), None))
    parser.add_argument("--native-project", type=Path, default=objectives.ROOT)
    parser.add_argument("--dotnet-root", type=Path, default=Path("/tmp/va-dotnet8"))
    args = parser.parse_args()
    if args.engine == "native" and args.port == 26919:
        parser.error("native needs the next reserved UDP port for LAN discovery")
    args.native_project = args.native_project.resolve()
    args.scenario = "crylink"
    args.output = args.output.resolve()
    args.output.mkdir()
    original, native = objectives.stage(args)
    with contextlib.ExitStack() as stack:
        session = CountdownSession(args, args.output, native if args.engine == "native" else original)
        session.boot(stack)
        session.run(stack)


if __name__ == "__main__":
    main()

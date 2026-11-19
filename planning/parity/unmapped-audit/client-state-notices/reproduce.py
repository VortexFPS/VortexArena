#!/usr/bin/env python3
"""Build a disposable join-notice probe and check both delayed-presentation cases."""

import argparse
import hashlib
import math
import os
from pathlib import Path
import re
import shlex
import shutil
import signal
import socket
import subprocess
import tempfile
import time


PIN = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"
REPO = Path(__file__).resolve().parents[4]
NOTICE = "^1Delayed join|^2Second line"
DURATION = 3
CASES = {"partial": 1.5, "expired": 4.0}
NO_STATS = {
    "sv_public": "-2",
    "g_playerstats_gamereport_uri": "",
    "g_playerstats_playerbasic_uri": "",
    "g_playerstats_playerdetail_uri": "",
}


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f"expected one instrumentation site for {old!r}")
    return text.replace(old, new, 1)


def instrument_native(project):
    path = project / "game/net/NetGame.cs"
    text = path.read_text()
    site = "    private TimedJoinNotice? _pendingJoinNotice;"
    text = replace_once(text, site, site + '''
    private float? _noticeProbeReleaseAt;
    private readonly float _noticeProbeDelay = float.TryParse(
        System.Environment.GetEnvironmentVariable("VORTEX_NOTICE_PROBE_DELAY"),
        System.Globalization.CultureInfo.InvariantCulture, out float delay) ? delay : 0f;
''')
    text = replace_once(text,
        "            _client.JoinNoticeReceived += notice => _pendingJoinNotice = new TimedJoinNotice(notice).WithClock(client.LatestServerTime);",
        '''            _client.JoinNoticeReceived += notice =>
            {
                float now = client.LatestServerTime;
                _pendingJoinNotice = new TimedJoinNotice(notice).WithClock(now);
                _noticeProbeReleaseAt = now + _noticeProbeDelay;
                GD.Print(FormattableString.Invariant($"[notice-probe] receipt {now:F6} duration {notice.Seconds} loading {LoadingScreen is not null || _fallbackOverlay is not null}"));
            };''')
    text = replace_once(text,
        "        if (LoadingScreen is not null || _fallbackOverlay is not null)\n        {\n            HandshakeStage stage =",
        '''        if ((LoadingScreen is not null || _fallbackOverlay is not null) && _noticeProbeReleaseAt.HasValue && (clientSpawned || welcomeReady))
            GD.Print(FormattableString.Invariant($"[notice-clock] hold {_client.LatestServerTime:F6}"));
        if ((LoadingScreen is not null || _fallbackOverlay is not null)
            && (_noticeProbeDelay == 0f || _noticeProbeReleaseAt is { } releaseAt && _client.LatestServerTime >= releaseAt))
        {
            HandshakeStage stage =''')
    site = "                    case HandshakeStage.Spawned:\n                        LoadingScreen?.UpdateProgress(1f, \"\");"
    text = replace_once(text, site, '''                    case HandshakeStage.Spawned:
                        GD.Print(FormattableString.Invariant($"[notice-probe] release {_client.LatestServerTime:F6}"));
                        LoadingScreen?.UpdateProgress(1f, "");''')
    text = replace_once(text,
        "            if (pending.RemainingSeconds(_client.LatestServerTime) == 0)\n                _pendingJoinNotice = null;",
        '''            GD.Print(FormattableString.Invariant($"[notice-clock] pending {_client.LatestServerTime:F6}"));
            if (pending.RemainingSeconds(_client.LatestServerTime) == 0)
            {
                GD.Print(FormattableString.Invariant($"[notice-probe] discard {_client.LatestServerTime:F6} deadline {pending.ExpiresAt:F6} loading {LoadingScreen is not null || _fallbackOverlay is not null}"));
                _pendingJoinNotice = null;
            }''')
    site = "                _joinNoticeOverlay?.ShowNotice(pending);"
    text = replace_once(text, site, site + '''
                GD.Print(FormattableString.Invariant($"[notice-probe] show {_client.LatestServerTime:F6} deadline {pending.ExpiresAt:F6} remaining {pending.RemainingSeconds(_client.LatestServerTime)}"));''')
    for site in ("        server.EnableLanDiscovery(_port);", "        server.EnableMasterAnnounce(_port);"):
        text = replace_once(text, site, "        // The notice fixture uses a direct loopback connection.")
    path.write_text(text)

    path = project / "game/net/NetTransport.cs"
    site = "            Error err = s.Peer.CreateServer(port, maxClients, NetProtocol.ChannelCount);"
    path.write_text(replace_once(path.read_text(), site, '            s.Peer.SetBindIP("127.0.0.1");\n' + site))

    path = project / "game/hud/JoinNoticeOverlay.cs"
    text = replace_once(path.read_text(), "        if (seconds == 0)\n            ClearNotice();", '''        GD.Print(FormattableString.Invariant($"[notice-clock] visible {Now:F6}"));
        if (seconds == 0)
        {
            GD.Print(FormattableString.Invariant($"[notice-probe] hide {Now:F6} deadline {_notice.ExpiresAt:F6}"));
            ClearNotice();
        }''')
    path.write_text(text)


def instrument_stock(source):
    text = source.read_text()
    text = replace_once(text, "entity cl_notices;\nvoid cl_notice_read()", '''entity cl_notices;
float notice_probe_release_at;
float notice_probe_deadline;
float notice_probe_released;
float notice_probe_shown;
void cl_notice_read()''')
    site = "    _notice.alpha = ReadLong() + time;\n    _notice.skin = ReadByte();"
    text = replace_once(text, site, site + r'''
    print(sprintf("[notice-probe] line %.6f deadline %.6f\n", time, _notice.alpha));
    if (!notice_probe_deadline)
    {
        notice_probe_deadline = _notice.alpha;
        notice_probe_release_at = time + cvar("notice_probe_delay");
        print(sprintf("[notice-probe] receipt %.6f duration %.0f loading True\n", time, _notice.alpha - time));
    }''')
    text = replace_once(text, "void cl_notice_run()\n{\n if (!cl_notices)", r'''void cl_notice_run()
{
 if (notice_probe_deadline && (!notice_probe_released || cl_notices))
     print(sprintf("[notice-clock] frame %.6f\n", time));
 if (notice_probe_deadline && !notice_probe_released && time >= notice_probe_release_at)
 {
     notice_probe_released = true;
     print(sprintf("[notice-probe] release %.6f\n", time));
 }
 if (!cl_notices)''')
    start = text.index("void cl_notice_run()\n{")
    end = text.index("    const float M1 = 30;", start)
    body = replace_once(text[start:end], " if (!flag)\n {", r''' if (!flag)
 {
  if (notice_probe_shown)
      print(sprintf("[notice-probe] hide %.6f deadline %.6f\n", time, notice_probe_deadline));
  else
      print(sprintf("[notice-probe] discard %.6f deadline %.6f loading %s\n", time, notice_probe_deadline, notice_probe_released ? "False" : "True"));''')
    return text[:start] + body + r'''
    if (!notice_probe_released)
        return;
    if (!notice_probe_shown)
    {
        notice_probe_shown = true;
        print(sprintf("[notice-probe] show %.6f deadline %.6f remaining %.0f\n", time, notice_probe_deadline, ceil(notice_probe_deadline - time)));
    }
''' + text[end:]


def run_logged(command, cwd, log):
    with log.open("wb") as target:
        subprocess.run(command, cwd=cwd, stdout=target, stderr=subprocess.STDOUT, check=True)


def spawn(command, cwd, log, env):
    print(f"{log.stem}: {shlex.join(map(str, command))}", flush=True)
    with log.open("wb") as target:
        return subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                stdout=target, stderr=subprocess.STDOUT, start_new_session=True)


def stop(process):
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait()


def wait_log(process, log, marker, timeout=90):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if marker in log.read_text(errors="replace"):
            return
        if process.poll() is not None:
            raise RuntimeError(f"{log.name} exited with {process.returncode} before {marker!r}")
        time.sleep(0.1)
    raise TimeoutError(f"{log.name} did not reach {marker!r}")


def profile(root):
    env = os.environ.copy()
    for name, directory in {"XDG_DATA_HOME": "xdg", "XDG_CONFIG_HOME": "config",
                            "XDG_CACHE_HOME": "cache", "VORTEX_USERDIR": "user"}.items():
        path = root / directory
        path.mkdir(parents=True)
        env[name] = str(path)
    env.update(VORTEX_NO_MASTER_QUERY="1", LIBGL_ALWAYS_SOFTWARE="1", LP_NUM_THREADS="2")
    env.setdefault("DOTNET_ROLL_FORWARD", "Major")
    return env


def loopback_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as game:
        game.bind(("127.0.0.1", 0))
        return game.getsockname()[1]


def check(log, delay, stock=False):
    data = log.read_text(errors="replace")
    markers = re.findall(r"\[notice-probe\] ([^\r\n]+)", data)
    samples = {}
    for clock, stamp in re.findall(r"\[notice-clock\] (\w+) ([\d.]+)", data):
        samples.setdefault(clock, []).append(float(stamp))
    events = {}
    lines = []
    for marker in markers:
        name, *fields = marker.split()
        if name == "line":
            lines.append(fields)
        elif name in events:
            raise RuntimeError(f"{log.name}: repeated {name!r}: {markers}")
        else:
            events[name] = fields

    def require(condition, message):
        if not condition:
            raise RuntimeError(f"{log.name}: {message}: {markers}")

    expired = delay >= DURATION
    expected = {"receipt", "release", "discard"} if expired else {"receipt", "release", "show", "hide"}
    require(set(events) == expected, "unexpected receipt/presentation events")
    received, _, duration, _, loading = events["receipt"]
    received, duration = float(received), int(duration)
    deadline = received + duration
    release = float(events["release"][0])
    require(duration == DURATION and loading == "True", "receipt did not occur under the hold")
    def first_eligible(clock, threshold, observed):
        values = samples.get("frame" if stock else clock, [])
        require(any(value < threshold - 0.000002 for value in values), f"no {clock} sample before threshold")
        first = next((value for value in values if value >= threshold - 0.000002), None)
        require(first is not None and abs(observed - first) <= 0.000002,
                f"{clock} event at {observed} missed the first eligible sample {first}")

    first_eligible("hold", received + delay, release)
    if stock:
        require(len(lines) == 2 and all(float(line[0]) == received and
                abs(float(line[2]) - deadline) <= 0.000002 for line in lines),
                "stock did not receive two lines on the same frame with the same deadline")
    expiry_event = events["discard" if expired else "hide"]
    actual = float(expiry_event[0])
    require(abs(float(expiry_event[2]) - deadline) < 0.002, "incorrect expiry deadline")
    first_eligible("pending" if expired else "visible", deadline, actual)
    if expired:
        require(expiry_event[4] == "True" and actual < release, "expiry happened after the hold")
    else:
        show = events["show"]
        shown, remaining = float(show[0]), int(show[4])
        first_eligible("pending", release, shown)
        require(release <= shown < deadline, "show must follow release and precede expiry")
        require(abs(float(show[2]) - deadline) < 0.002 and remaining == math.ceil(deadline - shown) < duration,
                "presentation restarted the countdown")
    print(f"PASS {log.name}", flush=True)
    for marker in markers:
        print(f"  {marker}")


def cvars(values):
    return [part for name, value in values.items() for part in ("--cvar", name, value)]


def run_native(godot, out):
    project = out / "project"
    project.mkdir()
    inputs = ["game", "src", ".godot", "VortexArena.csproj", "Directory.Build.props", "global.json",
              "nuget.config", "project.godot", "Main.cs", "Main.cs.uid", "Main.tscn"]
    subprocess.run(["cp", "-a", "--reflink=auto", *inputs, project], cwd=REPO, check=True)
    (project / "data").symlink_to(REPO / "data", target_is_directory=True)
    instrument_native(project)
    run_logged(["dotnet", "build", "VortexArena.csproj", "--no-restore"], project, out / "native-build.log")
    port = loopback_port()
    common = [godot, "--headless", "--path", project, "--audio-driver", "Dummy", "--no-save-config"]
    command = common + ["--dedicated", "stormkeep", "--port", str(port), "--gametype", "dm", "--bots", "0",
                        "--no-console"] + cvars(NO_STATS | {"sv_join_notices": NOTICE, "sv_join_notices_time": str(DURATION)})
    server_log = out / "native-server.log"
    server = spawn(command, project, server_log, profile(out / "native-server"))
    try:
        wait_log(server, server_log, "listen server on")
        for role, delay in CASES.items():
            env = profile(out / f"native-{role}")
            env["VORTEX_NOTICE_PROBE_DELAY"] = str(delay)
            log = out / f"native-{role}.log"
            command = common + ["--connect", f"127.0.0.1:{port}"] + cvars({
                "_cl_name": role, "cl_autopause": "0", "cl_welcome": "0", "cl_warm_at_boot": "0",
                "cl_precache_all_weapons": "0", "cl_portal_render": "0", "r_warpzone": "0", "r_water": "0",
                "gl_texturecompression": "0", "cl_frameprofiler": "-1",
            })
            client = spawn(command, project, log, env)
            try:
                wait_log(client, log, "[notice-probe] hide" if role == "partial" else "[notice-probe] release")
                time.sleep(0.25)  # allow a show on the release frame to reach the log
                check(log, delay)
            finally:
                stop(client)
    finally:
        stop(server)


def verify_hash(path, expected):
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != expected:
        raise RuntimeError(f"unexpected SHA-256 for {path}: {actual}")


def run_stock(base, stock, out):
    revision = subprocess.check_output(["git", "-C", base, "rev-parse", "HEAD"], text=True).strip()
    if revision != PIN:
        raise RuntimeError(f"expected stock data {PIN}, got {revision}")
    binary = stock / "xonotic-linux64-sdl"
    client_source = stock / "qc-pinned/client.qc"
    progs = stock / "compiled/progs.dat"
    verify_hash(binary, "79e38b239b8dc5d5ee1ac8ada8245a51381ce27277dc165645fea0e69fed4312")
    verify_hash(client_source, "40ce38d40bc77b129316456256ce7297e7b12d7ea1627e8de89b9ce861063032")
    verify_hash(progs, "c4a5a9e85598333568183779c06446b2d03f056d2da71636b6f86150ed1202f7")
    data = out / "stock-game/data"
    code = data / "zz-probe.pk3dir"
    code.mkdir(parents=True)
    (data / "base.pk3dir").symlink_to(base, target_is_directory=True)
    for name in ("stormkeep.pk3", "shared.pk3"):
        (data / name).symlink_to(REPO / "data/maps" / name)
    (code / "progs.dat").symlink_to(progs)
    (code / "menu.dat").symlink_to(stock / "compiled/menu.dat")
    qc = out / "stock-client.qc"
    qc.write_text(instrument_stock(client_source))
    run_logged([stock / "gmqcc/gmqcc", "-std=gmqcc", "-O3", "-Werror", "-Wall", "-Wno-field-redeclared",
                "-futf8", "-freturn-assignments", "-frelaxed-switch", "-Ooverlap-locals", "-flno",
                "-fno-bail-on-werror", "-o", code / "csprogs.dat", qc], out, out / "stock-build.log")
    port = loopback_port()
    common = [binary, "-xonotic", "-basedir", data.parent, "-nosound", "-nojoy"]

    def stock_profile(role, cfg):
        env = profile(out / role)
        env["LD_LIBRARY_PATH"] = str(stock / "sdl/usr/lib/x86_64-linux-gnu")
        user = Path(env["VORTEX_USERDIR"])
        (user / "data").mkdir()
        (user / "data/autoexec.cfg").write_text("".join(f'set {name} "{value}"\n' for name, value in cfg.items()))
        return env, user

    env, user = stock_profile("stock-server", NO_STATS | {"sv_public": "0", "net_address_ipv6": "::1",
        "sv_autopause": "0", "g_dm": "1",
        "g_start_delay": "0", "sv_join_notices": NOTICE, "sv_join_notices_time": str(DURATION)})
    server_log = out / "stock-server.log"
    server = spawn(common + ["-userdir", user, "-dedicated", "-ip", "127.0.0.1", "-port", str(port),
                            "+map", "stormkeep"], out, server_log, env)
    try:
        wait_log(server, server_log, "execing post-config.cfg")
        for role, delay in CASES.items():
            env, user = stock_profile(f"stock-{role}", {"notice_probe_delay": str(delay), "_cl_name": role,
                "cl_maxfps": "45", "cl_allow_uidtracking": "0", "cl_allow_uidranking": "0", "cl_allow_uid2name": "0"})
            log = out / f"stock-{role}.log"
            client = spawn(["xvfb-run", "-a", "-s", "-screen 0 640x480x24 -nolisten tcp", *common,
                            "-userdir", user, "-window", "-width", "640", "-height", "480",
                            "+cl_startup_disclaimer", "0", "+cl_welcome", "0", "+connect",
                            f"127.0.0.1:{port}"], out, log, env)
            try:
                wait_log(client, log, "[notice-probe] hide" if role == "partial" else "[notice-probe] release")
                time.sleep(0.25)
                check(log, delay, stock=True)
            finally:
                stop(client)
    finally:
        stop(server)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", choices=("native", "stock", "both"), default="native")
    parser.add_argument("--check-saved", type=Path, metavar="DIRECTORY",
                        help="verify four archived clock and event traces without launching either game")
    parser.add_argument("--godot", type=Path)
    parser.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--stock", type=Path, default=Path("/tmp/va-reviewed-stock"))
    parser.add_argument("--output", type=Path, help="fresh output directory, or a private temporary directory by default")
    args = parser.parse_args()
    if args.check_saved:
        for engine in ("native", "stock"):
            for role, delay in CASES.items():
                check(args.check_saved / f"{engine}-{role}.txt", delay, stock=engine == "stock")
        return
    if args.engine != "stock" and args.godot is None:
        parser.error("--godot is required for native runs")
    if args.engine != "native" and shutil.which("xvfb-run") is None:
        parser.error("stock runs require xvfb-run")
    out = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix="va-notice-probe-"))
    if args.output:
        out.mkdir(mode=0o700)
    print(f"Logs and disposable profiles: {out}", flush=True)
    if args.engine != "stock":
        run_native(args.godot.resolve(), out)
    if args.engine != "native":
        run_stock(args.base.resolve(), args.stock.resolve(), out)


if __name__ == "__main__":
    main()

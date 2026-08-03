#!/usr/bin/env bash
# perf-run — one-command perf capture + report (bash twin of perf-run.ps1; see docs/PERF-DEBUGGING.md).
# Usage: perf-run.sh <label> <secs> [extra --cvar flags...]
#   perf-run.sh baseline 35
#   perf-run.sh pvs_off  35 --cvar r_pvs_cull 0
# Env: PERF_MAP (default catharsis), PERF_BOTS (default 6), PERF_DEBUG=1 (Godot console binary
# on the project instead of the release export — NOT release-representative),
# PERF_USERDIR (capture profile dir; default _scratch/perf-userdir, "real" = the daily ~/XonData),
# PERF_BASELINE (path to a perf_*.json to --diff against; the twin of perf-run.ps1's -Baseline).
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
. "$ROOT/tools/lib/run-timeout.sh"   # portable `timeout` (absent on macOS)
LABEL="${1:-run}"; SECS="${2:-35}"; shift 2 || true
MAP="${PERF_MAP:-catharsis}"; BOTS="${PERF_BOTS:-6}"

# Isolated capture profile (VORTEX_USERDIR, honored by UserPaths.cs) — captures used to mutate the
# real ~/XonData/config.cfg and inherit whatever the last playtest left configured.
USERDIR="${PERF_USERDIR:-$ROOT/_scratch/perf-userdir}"
if [ "$USERDIR" = "real" ]; then
    unset VORTEX_USERDIR
    LOGDIR="$HOME/XonData/logs"
else
    mkdir -p "$USERDIR"
    export VORTEX_USERDIR="$(cd "$USERDIR" && pwd -W 2>/dev/null || pwd)"
    LOGDIR="$USERDIR/logs"
fi

if [ "${PERF_DEBUG:-0}" = "1" ]; then
    # Debug capture runs the project through the editor binary rather than an export.
    . "$ROOT/tools/lib/find-godot.sh"
    EXE="$(find_godot "$ROOT")" || { godot_not_found "$ROOT"; exit 1; }
    EXTRA_ARGS=(--path "$ROOT")
else
    # The export for THIS platform. Hardcoding the Windows path made every non-Windows capture impossible;
    # the presets and their output names are export_presets.cfg's, mirrored in tools/package.sh.
    case "$(uname -s)" in
        Darwin) PRESET="macos-client"; EXE="$ROOT/dist/macos-client/VortexArena.app/Contents/MacOS/VortexArena" ;;
        Linux)  PRESET="linux-client"; EXE="$ROOT/dist/linux-client/VortexArena.x86_64" ;;
        *)      PRESET="windows-client"; EXE="$ROOT/dist/windows-client/VortexArena.exe" ;;
    esac
    EXTRA_ARGS=()
    [ -x "$EXE" ] || { echo "!!! release export missing at $EXE — run: ./vx export --preset $PRESET   (or PERF_DEBUG=1)"; exit 1; }

    # Reproduce the PACKAGED content layout. `./vx export` now does this itself (Wrappers.PlaceContent), so
    # this block is a belt for a dist/ produced some other way — kept because the failure is silent: the
    # export excludes data/* from the pck, so an exported build resolves it through DataPaths.ResolveExported,
    # which probes exe-relative FIRST and only then the CWD. Without this the binary launches, mounts NOTHING,
    # self-quits and writes a session log full of flattering numbers — which is precisely how the first
    # capture of the menu-warm investigation came back clean. macOS keeps it inside the bundle at
    # Contents/Resources/data (tools/package.sh); the other platforms put it beside the binary.
    case "$(uname -s)" in
        Darwin) DATA_DEST="$ROOT/dist/macos-client/VortexArena.app/Contents/Resources/data" ;;
        *)      DATA_DEST="$(dirname "$EXE")/data" ;;
    esac
    if [ ! -e "$DATA_DEST" ]; then
        mkdir -p "$(dirname "$DATA_DEST")"
        ln -s "$ROOT/data" "$DATA_DEST" 2>/dev/null || cp -R "$ROOT/data" "$DATA_DEST"
        echo ">>> placed data/ for the exported build at $DATA_DEST"
    fi
fi

powershell -NoProfile -Command "Get-Process Godot*,VortexArena* -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null
BEFORE=$(ls -t "$LOGDIR"/*.log 2>/dev/null | head -1)
echo ">>> [$LABEL] $MAP + $BOTS bots, ${SECS}s  extra: $*"
# Pinned capture profile (later --cvar wins, so caller flags override the pins — see perf-run.ps1
# for the rationale per pin; cl_maxfps 0 = truly uncapped since 2026-07-06, captures measure peak;
# cl_frameprofiler_rendertime 1 buys the rcpu/gpu split that the game defaults OFF, and both arms of
# an A/B pay it equally — never diff a rendertime=1 capture against a rendertime=0 one).
# PERF_SCENARIO=idle opts out of the demo (spectated-bot gameplay) scenario.
SCENARIO_ARGS=()
if [ "${PERF_SCENARIO:-demo}" = "demo" ]; then
    SCENARIO_ARGS=(--cvar cl_bench_spectate 1
                   --cvar g_weaponarena "blaster shotgun vortex mortar devastator crylink electro hagar"
                   --cvar g_forced_respawn 1
                   --cvar bot_ai_weapon_rotate 8)
fi
# ${arr[@]+"${arr[@]}"} rather than "${arr[@]}": under `set -u`, macOS's bash 3.2 treats expanding an EMPTY
# array as an unbound variable and aborts. bash 4.4+ does not, which is why this only ever failed off the dev
# box. EXTRA_ARGS is empty on every non-PERF_DEBUG run, i.e. every release capture.
run_with_timeout $((SECS+60)) "$EXE" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} --host "$MAP" --gametype dm --bots "$BOTS" \
    --cvar cl_frameprofiler 2 --cvar cl_frameprofiler_hitchms 8 \
    --cvar cl_frameprofiler_rendertime 1 \
    --cvar cl_autopause 0 --cvar cl_portal_render 0 --cvar vid_vsync 0 --cvar cl_maxfps 0 \
    ${SCENARIO_ARGS[@]+"${SCENARIO_ARGS[@]}"} \
    "$@" --quit-after-seconds "$SECS" > "$ROOT/_scratch/perf_${LABEL}.out" 2>&1
sleep 2   # session-log writer flush
NEW=$(ls -t "$LOGDIR"/*.log 2>/dev/null | head -1)
if [ "$NEW" = "${BEFORE:-}" ] || [ -z "$NEW" ]; then
    echo "!!! no new session log (boot failed?) — see _scratch/perf_${LABEL}.out"; tail -20 "$ROOT/_scratch/perf_${LABEL}.out"; exit 1
fi
echo ">>> [$LABEL] session: $(basename "$NEW")"
. "$ROOT/tools/lib/find-python.sh"
PYTHON="$(find_python)" || { python_not_found; exit 1; }
# PERF_BASELINE is the .sh counterpart of perf-run.ps1's -Baseline. Its absence was drift, not design: the
# two scripts are documented as twins, and perf-smoke's --live gate has nothing to compare against without it.
REPORT_ARGS=("$NEW" --json "$ROOT/_scratch/perf_${LABEL}.json")
if [ -n "${PERF_BASELINE:-}" ]; then
    if [ -f "$PERF_BASELINE" ]; then
        REPORT_ARGS+=(--diff "$PERF_BASELINE")
    else
        echo ">>> baseline '$PERF_BASELINE' not found — reporting without a diff" >&2
    fi
fi
"$PYTHON" "$ROOT/tools/perf-report.py" "${REPORT_ARGS[@]}"

#!/usr/bin/env bash
# perf-run — one-command perf capture + report (bash twin of perf-run.ps1; see docs/PERF-DEBUGGING.md).
# Usage: perf-run.sh <label> <secs> [extra --cvar flags...]
#   perf-run.sh baseline 35
#   perf-run.sh pvs_off  35 --cvar r_pvs_cull 0
# Env: PERF_MAP (default catharsis), PERF_BOTS (default 6), PERF_DEBUG=1 (Godot console binary
# on the project instead of the release export — NOT release-representative),
# PERF_USERDIR (capture profile dir; default _scratch/perf-userdir, "real" = the daily ~/XonData).
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="${1:-run}"; SECS="${2:-35}"; shift 2 || true
MAP="${PERF_MAP:-catharsis}"; BOTS="${PERF_BOTS:-6}"

# Isolated capture profile (XONOTIC_USERDIR, honored by UserPaths.cs) — captures used to mutate the
# real ~/XonData/config.cfg and inherit whatever the last playtest left configured.
USERDIR="${PERF_USERDIR:-$ROOT/_scratch/perf-userdir}"
if [ "$USERDIR" = "real" ]; then
    unset XONOTIC_USERDIR
    LOGDIR="$HOME/XonData/logs"
else
    mkdir -p "$USERDIR"
    export XONOTIC_USERDIR="$(cd "$USERDIR" && pwd -W 2>/dev/null || pwd)"
    LOGDIR="$USERDIR/logs"
fi

if [ "${PERF_DEBUG:-0}" = "1" ]; then
    EXE="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
    EXTRA_ARGS=(--path "$ROOT")
else
    EXE="$ROOT/dist/windows-client/XonoticGodot.exe"
    EXTRA_ARGS=()
    [ -x "$EXE" ] || { echo "!!! release export missing at $EXE — export windows-client first (or PERF_DEBUG=1)"; exit 1; }
fi

powershell -NoProfile -Command "Get-Process Godot*,XonoticGodot* -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null
BEFORE=$(ls -t "$LOGDIR"/*.log 2>/dev/null | head -1)
echo ">>> [$LABEL] $MAP + $BOTS bots, ${SECS}s  extra: $*"
# Pinned capture profile (later --cvar wins, so caller flags override the pins — see perf-run.ps1
# for the rationale per pin; cl_maxfps 0 = truly uncapped since 2026-07-06, captures measure peak).
# PERF_SCENARIO=idle opts out of the demo (spectated-bot gameplay) scenario.
SCENARIO_ARGS=()
if [ "${PERF_SCENARIO:-demo}" = "demo" ]; then
    SCENARIO_ARGS=(--cvar cl_bench_spectate 1
                   --cvar g_weaponarena "blaster shotgun vortex mortar devastator crylink electro hagar"
                   --cvar g_forced_respawn 1
                   --cvar bot_ai_weapon_rotate 8)
fi
timeout $((SECS+60)) "$EXE" "${EXTRA_ARGS[@]}" --host "$MAP" --gametype dm --bots "$BOTS" \
    --cvar cl_frameprofiler 2 --cvar cl_frameprofiler_hitchms 8 \
    --cvar cl_autopause 0 --cvar cl_portal_render 0 --cvar vid_vsync 0 --cvar cl_maxfps 0 \
    "${SCENARIO_ARGS[@]}" \
    "$@" --quit-after-seconds "$SECS" > "$ROOT/_scratch/perf_${LABEL}.out" 2>&1
sleep 2   # session-log writer flush
NEW=$(ls -t "$LOGDIR"/*.log 2>/dev/null | head -1)
if [ "$NEW" = "${BEFORE:-}" ] || [ -z "$NEW" ]; then
    echo "!!! no new session log (boot failed?) — see _scratch/perf_${LABEL}.out"; tail -20 "$ROOT/_scratch/perf_${LABEL}.out"; exit 1
fi
echo ">>> [$LABEL] session: $(basename "$NEW")"
python "$ROOT/tools/perf-report.py" "$NEW" --json "$ROOT/_scratch/perf_${LABEL}.json"

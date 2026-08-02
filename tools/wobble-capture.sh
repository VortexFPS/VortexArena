#!/usr/bin/env bash
# wobble-capture — record a motion trace (+ a display-side present trace where one exists) and score it
# with wobble-report.py. Bash twin of wobble-capture.ps1.
#
# The GAME side is identical everywhere: run with `cl_motion_trace 1` (console, or
# `--cvar cl_motion_trace 1`). The v2 trace lands in ~/XonData/motion_trace_YYYYMMDD_HHMMSS.csv.
# This script owns the DISPLAY side and the join.
#
# Usage (game already running, or about to be launched by you):
#   tools/wobble-capture.sh --seconds 90
#   tools/wobble-capture.sh --process VortexArena --seconds 60 --skip-report
# Then move/strafe continuously during the window — the report needs sustained motion.
#
# WHAT THIS PORT CANNOT DO, AND WHY THAT IS NOT A GAP TO FIX LATER. The .ps1 captures the present queue with
# PresentMon, which is an ETW consumer: ETW is a Windows kernel facility with no macOS or Linux equivalent,
# so there is nothing to port it TO. macOS's nearest relatives (Instruments' Core Animation track, Metal
# System Trace) are GUI-driven and produce a different, non-interchangeable schema.
#
# So off Windows this runs the MOTION-TRACE half only: it holds the capture window open so the trace covers
# a known span, then reports on the trace alone. That is exactly what the .ps1 already does when PresentMon
# is absent, so it is a documented mode rather than a new degradation — wobble-report.py accepts a trace
# with no --presentmon and simply omits the display-side columns. The motion half is what catches
# camera/interp wobble; the display half is what separates "the frame was late" from "the camera moved
# wrong", and that distinction stays Windows-only.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROCESS=""
SECONDS_ARG=90
OUTDIR="_scratch/wobble"
SKIP_REPORT=0

while [ $# -gt 0 ]; do
    case "$1" in
        --process)     PROCESS="${2:?--process needs a name}"; shift ;;
        --seconds)     SECONDS_ARG="${2:?--seconds needs a number}"; shift ;;
        --out)         OUTDIR="${2:?--out needs a path}"; shift ;;
        --skip-report) SKIP_REPORT=1 ;;
        --help|-h)     grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "wobble-capture.sh: unknown option '$1' (try --help)" >&2; exit 1 ;;
    esac
    shift
done

OUT_FULL="$ROOT/$OUTDIR"
mkdir -p "$OUT_FULL"

# --- the game process. Only used to name the capture and to fail early when nothing is running; without a
#     present-side capture there is no process to attach to.
if [ -z "$PROCESS" ]; then
    PROCESS="$(ps -eo comm= 2>/dev/null | grep -iE 'vortex|xonotic|godot' | head -1 | xargs -r basename 2>/dev/null || true)"
fi
if [ -z "$PROCESS" ]; then
    echo "No running game process found (vortex/xonotic/godot)." >&2
    echo "Launch the game — a RELEASE export, for feel-representative capture — enable 'cl_motion_trace 1'," >&2
    echo "then re-run. ./vx export --preset <p> builds one." >&2
    exit 1
fi

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
        echo "wobble-capture.sh: on Windows, use tools/wobble-capture.ps1 — it can drive PresentMon" >&2
        echo "                   for the display-side capture, which this script cannot." >&2
        exit 1 ;;
esac

echo "Capturing '$PROCESS' for ${SECONDS_ARG}s (motion trace only — PresentMon is Windows-only)."
echo "Move and strafe continuously; the report needs sustained motion."
sleep "$SECONDS_ARG"

[ "$SKIP_REPORT" -eq 1 ] && exit 0

# --- newest motion trace, honouring the VORTEX_USERDIR override the capture profile sets.
USERDIR="${VORTEX_USERDIR:-$HOME/XonData}"
TRACE="$(ls -t "$USERDIR"/motion_trace_*.csv 2>/dev/null | head -1 || true)"
if [ -z "$TRACE" ]; then
    echo "No motion_trace_*.csv in $USERDIR — was 'cl_motion_trace 1' set (v2 build)?" >&2
    exit 1
fi
echo "Trace: $TRACE"

STAMP="$(basename "$TRACE" .csv | sed 's/^motion_trace_//')"
. "$ROOT/tools/lib/find-python.sh"
PYTHON="$(find_python)" || { python_not_found; exit 1; }
"$PYTHON" "$ROOT/tools/wobble-report.py" "$TRACE" --json "$OUT_FULL/wobble_$STAMP.json"

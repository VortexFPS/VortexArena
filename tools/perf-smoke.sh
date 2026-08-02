#!/usr/bin/env bash
# perf-smoke — the pre-merge perf regression check (bash twin of perf-smoke.ps1; docs/PERF-DEBUGGING.md).
#
#   tools/perf-smoke.sh              # headless benches only (~1 min, no window)
#   tools/perf-smoke.sh --live       # + a 30s release-export capture diffed vs the checked-in baseline
#
# 1) Runs the budget-asserting headless benches (ServerTickPerfBench fails on a >4-5x tick regression).
# 2) With --live: a 30s catharsis+bots capture via perf-run.sh, diffed against
#    tools/perf-baselines/catharsis-release.json when that baseline exists.
#
# WHY THIS EXISTS. CLAUDE.md makes running perf-smoke a house rule for perf-relevant changes, and until now
# the only implementation was PowerShell — so on macOS or Linux the rule was impossible to follow, which is
# the kind of rule that teaches people to ignore rules. The headless half is the portable half: the bench is
# an ordinary test and asserts the same budgets everywhere.
#
# THE --live HALF IS PLATFORM-SENSITIVE, and deliberately loud about it. tools/perf-baselines/ was captured
# on the Windows/RTX 3080 dev box, so a diff against it from another machine compares two different
# computers and means nothing. Off Windows this runs the capture and reports it WITHOUT the baseline diff
# unless PERF_ALLOW_CROSS_PLATFORM_BASELINE=1 says otherwise.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIVE=0
for arg in "$@"; do
    case "$arg" in
        --live)    LIVE=1 ;;
        --help|-h) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "perf-smoke.sh: unknown option '$arg' (try --help)" >&2; exit 1 ;;
    esac
done

echo "=== perf-smoke: headless benches (budget-asserting) ==="
if ! dotnet test "$ROOT/tests/VortexArena.Tests/VortexArena.Tests.csproj" \
        --filter "ServerTickPerfBench" -l "console;verbosity=detailed" --nologo; then
    echo "perf bench budgets FAILED — a server-tick regression landed" >&2
    exit 1
fi

if [ "$LIVE" -eq 1 ]; then
    echo "=== perf-smoke: live release capture ==="
    baseline="$ROOT/tools/perf-baselines/catharsis-release.json"

    is_windows=false
    case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*|Windows_NT) is_windows=true ;; esac

    if [ ! -f "$baseline" ]; then
        echo "(no baseline at ${baseline#"$ROOT/"} — copy _scratch/perf_smoke.json there on a known-good build to enable diffs)"
    elif $is_windows || [ "${PERF_ALLOW_CROSS_PLATFORM_BASELINE:-0}" = "1" ]; then
        export PERF_BASELINE="$baseline"
    else
        echo "(a baseline exists, but it is the Windows/RTX 3080 dev box's — NOT diffing against it from"
        echo " $(uname -s). A cross-machine diff compares two computers, not two builds. For a real"
        echo " before/after here, capture both arms locally with tools/perf-run.sh."
        echo " Override with PERF_ALLOW_CROSS_PLATFORM_BASELINE=1 if you know the machines match.)"
    fi

    PERF_MAP="${PERF_MAP:-catharsis}" bash "$ROOT/tools/perf-run.sh" smoke 30
fi

echo "=== perf-smoke: done ==="

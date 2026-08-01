#!/usr/bin/env bash
# Export + launch a TRUE RELEASE build of VortexArena — optimized C# (csharp=Release) AND
# godot-context=release, with NO editor/debugger overhead. This is the only way to measure real-world
# performance: running from the Godot editor or a Rider "Player" config ALWAYS loads the Debug assembly and
# reports godot-context=debug, regardless of the Rider build configuration.
#
# ONE-TIME PREREQUISITE: install the export templates (the export_templates dir is currently empty):
#   Godot editor  →  Editor menu  →  Manage Export Templates…  →  Download and Install  (4.6.3 .NET/Mono)
#
# Then just run:  ./run-release.sh            (export + launch)
#                 ./run-release.sh --host atelier --gametype dm   (extra args forwarded to the game)
set -euo pipefail

PROJ="$(cd "$(dirname "$0")" && pwd)"
# Python spelling differs by platform (`python` is gone on macOS 12.3+/most Linux; `python3` does not
# exist under the python.org Windows install), so resolve it for the hints below rather than guessing.
. "$PROJ/tools/lib/find-python.sh"
VX_PY="$(find_python 2>/dev/null || echo python3)"

# Pick the desktop-client export preset + output binary for THIS OS (export_presets.cfg). The engine itself
# is resolved by tools/lib/find-godot.sh ($GODOT → .godot-bin/ → PATH → platform install location).
. "$PROJ/tools/lib/find-godot.sh"
GODOT="$(find_godot "$PROJ")" || { godot_not_found "$PROJ"; exit 1; }

is_windows=false
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
        is_windows=true
        PRESET="windows-client"; OUT="$PROJ/dist/windows-client/VortexArena.exe" ;;   # preset.0
    Linux)
        PRESET="linux-client";   OUT="$PROJ/dist/linux-client/VortexArena.x86_64" ;;  # preset.2
    Darwin)
        echo "[run-release] macOS export is CI-only / best-effort (ADR-0014) — use the release workflow." >&2
        exit 1 ;;
    *)  echo "[run-release] unsupported OS '$(uname -s)'" >&2; exit 1 ;;
esac

# (Removed 2026-08-01, bootstrap Phase 0.) A block here stripped the Windows-only 'godot-editor' package
# source from nuget.config before the export's C# publish read it, backing the file up and restoring it on
# exit. nuget.config is now nuget.org-only, so there is nothing to strip.

mkdir -p "$(dirname "$OUT")"
echo "[run-release] exporting '$PRESET' (release, optimized C#) → $OUT"

# Godot's headless --export-release is doubly untrustworthy on Windows: it frequently exits
# NON-ZERO on a fully successful export (benign import/shader/.NET warnings), AND it frequently
# HANGS after a successful export — it prints '[ DONE ] savepack' but the process never exits
# (a lingering render/.NET thread), so the script would stall here forever and never launch.
# So we don't wait on godot to terminate or trust its exit code: run it in the background,
# mirror its output live, and the moment the final 'savepack' stage reports DONE give it a beat
# to flush the .exe/.pck to disk, then kill it ourselves. Real success is gated on the binary.
log="$(mktemp)"
set +e
"$GODOT" --headless --path "$PROJ" --export-release "$PRESET" "$OUT" >"$log" 2>&1 &
gpid=$!
tail -n +1 -f --pid="$gpid" "$log" &
tailpid=$!
# NOTE: match 'DONE.*savepack' (not a literal '] savepack') — Godot colorizes the marker, so there
# are ANSI escape codes between ']' and 'savepack'; '.*' bridges them. A too-strict pattern here just
# silently polls until the 10-min cap, which looks like "stalls forever after savepack".
reason="timeout (10-min cap)"
i=0
for i in $(seq 1 1200); do                              # ~10 min safety cap (0.5s/iter)
    if ! kill -0 "$gpid" 2>/dev/null; then reason="godot exited on its own"; break; fi
    if grep -qi 'DONE.*savepack' "$log" 2>/dev/null; then
        reason="savepack DONE detected"
        sleep 2                                         # let godot finish flushing to disk
        break
    fi
    sleep 0.5
done
echo "[run-release][debug] export loop ended after ~$((i/2))s — ${reason}; terminating godot (pid $gpid)…"
kill -9 "$gpid" 2>/dev/null                             # no-op if it already exited
wait "$gpid" 2>/dev/null; gexit=$?
kill "$tailpid" 2>/dev/null; wait "$tailpid" 2>/dev/null
rm -f "$log"
set -e
echo "[run-release][debug] godot reaped (wait status $gexit)"

if [ ! -e "$OUT" ]; then
    echo "[run-release] export FAILED — '$OUT' was not produced (see godot output above)" >&2
    exit 1
fi
echo "[run-release][debug] export OK — binary present: $OUT ($(wc -c <"$OUT" 2>/dev/null | tr -d ' ') bytes)"

# Reproduce the PACKAGED layout: data/ beside the binary (tools/package.sh does the same with a real copy).
# The export deliberately excludes data/* from the pck, so an exported build resolves it through
# DataPaths.ResolveExported, which probes exe-relative FIRST and only then the CWD. Relying on the CWD probe
# — which is what this script used to do — means the build only finds content when launched from the repo
# root, and silently loads NOTHING otherwise: no menu asset warm, no models, an empty world, and a run whose
# perf numbers look great because the game never loaded anything. A symlink costs nothing, needs no copy,
# and stays live as the content tree changes.
if [ ! -d "$PROJ/data" ]; then
    echo "[run-release] ERROR: no content tree at $PROJ/data (fetch maps: $VX_PY tools/data/fetch-maps.py)" >&2
    exit 1
fi
if [ ! -e "$(dirname "$OUT")/data" ]; then
    ln -s "$PROJ/data" "$(dirname "$OUT")/data" 2>/dev/null \
        || cp -r "$PROJ/data" "$(dirname "$OUT")/data"      # Windows without symlink rights: fall back to a copy
    echo "[run-release] placed data/ beside the binary"
fi

# Launch from the install dir, exactly as a player would — the exe-relative probe above is what finds data/,
# so this no longer depends on the caller's working directory.
cd "$(dirname "$OUT")"
echo "[run-release] launching: $OUT $*"
[ -x "$OUT" ] || echo "[run-release][debug] WARNING: '$OUT' is not marked executable — trying anyway" >&2
# Run as a CHILD (not exec) so we can report the exit code. A release build that vanishes instantly is
# almost always a startup crash or a missing asset/data path — its own console output appears above.
set +e
"$OUT" "$@"
rc=$?
set -e
if [ "$rc" -ne 0 ]; then
    echo "[run-release] game exited NON-ZERO ($rc) — failed to start or crashed; see its output above" >&2
    exit "$rc"
fi
echo "[run-release] game exited cleanly (0)"

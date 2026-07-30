#!/usr/bin/env bash
# Export + launch a TRUE RELEASE build of XonoticGodot — optimized C# (csharp=Release) AND
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

# Pick the desktop-client export preset + output binary for THIS OS (export_presets.cfg). GODOT may be
# overridden via the environment so non-Windows devs point at their own Godot 4.6.3 mono console build.
is_windows=false
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
        is_windows=true
        GODOT="${GODOT:-/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe}"
        PRESET="windows-client"; OUT="$PROJ/dist/windows-client/XonoticGodot.exe" ;;   # preset.0
    Linux)
        GODOT="${GODOT:-godot}"
        PRESET="linux-client";   OUT="$PROJ/dist/linux-client/XonoticGodot.x86_64" ;;  # preset.2
    Darwin)
        echo "[run-release] macOS export is CI-only / best-effort (ADR-0014) — use the release workflow." >&2
        exit 1 ;;
    *)  echo "[run-release] unsupported OS '$(uname -s)'" >&2; exit 1 ;;
esac

# On non-Windows the export's C# publish reads nuget.config; drop the Windows-only 'godot-editor' local
# source (a C:\ path absent here) or NuGet/SDK resolution hard-fails. Backed up + restored on exit.
if ! $is_windows && grep -q godot-editor "$PROJ/nuget.config" 2>/dev/null; then
    _nuget_bak="$(mktemp)"; cp "$PROJ/nuget.config" "$_nuget_bak"
    trap 'cp -f "$_nuget_bak" "$PROJ/nuget.config"; rm -f "$_nuget_bak"' EXIT
    dotnet nuget remove source godot-editor --configfile "$PROJ/nuget.config" >/dev/null
fi

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

# Launch from the project root so the exported build's content resolver finds the in-tree data/
# (DataPaths.Resolve probes <exe-dir>/data then a CWD-relative 'data' in an exported build; a packaged
# zip instead carries data/ beside the binary). Without this a release run boots into an empty world.
cd "$PROJ"
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

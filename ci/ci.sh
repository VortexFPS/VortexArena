#!/usr/bin/env bash
# Local CI mirror for XonoticGodot (T33 — ADR-0014). Runs the same gate as
# .github/workflows/ci.yml PLUS the asset-dependent steps GitHub can't run:
# with data/ mounted, the ~18 real-data test classes actually execute
# (in CI they self-skip), and the headless boot smoke exercises real asset
# loading — so THIS script, not the green Actions badge, is the authoritative
# pre-push gate.
#
# Usage:
#   ci/ci.sh                 # build libs+tests, run the suite, build the Godot host, headless smoke
#   ci/ci.sh --no-smoke      # skip the Godot headless boot (no Godot install needed)
#   ci/ci.sh --export        # additionally run both export presets (needs export templates installed)
#
# Env:
#   GODOT  — path to the Godot 4.6.3 mono CONSOLE executable. Defaults to the known dev-machine path.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT="${GODOT:-/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe}"

do_smoke=true
do_export=false
for arg in "$@"; do
    case "$arg" in
        --no-smoke) do_smoke=false ;;
        --export)   do_export=true ;;
        --help|-h)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $arg (try --help)"; exit 1 ;;
    esac
done

step()  { printf '\n\033[1;34m== %s ==\033[0m\n' "$*"; }
fail()  { printf '\033[1;31mFAIL:\033[0m %s\n' "$*" >&2; exit 1; }

# ── 0. on non-Windows, drop the Windows-only 'godot-editor' NuGet source ──────
# nuget.config adds the dev machine's local Godot editor nupkgs folder as a package source (key
# "godot-editor", a C:\ path). That path exists only on the Windows dev box; on Linux/macOS NuGet AND
# the Godot.NET.Sdk MSBuild SDK resolver hard-fail on the missing local source — so the host build below
# can't even resolve its SDK. Mirror what .github/workflows/ci.yml does: remove the source first. We back
# nuget.config up (to $TMPDIR) and restore it on exit, so the working tree is left byte-identical.
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT) ;;   # real Windows dev box: the local source exists — keep it
    *)
        if grep -q godot-editor "$ROOT/nuget.config" 2>/dev/null; then
            step "drop the Windows-only 'godot-editor' NuGet source (non-Windows host)"
            _nuget_bak="$(mktemp)"
            cp "$ROOT/nuget.config" "$_nuget_bak"
            trap 'cp -f "$_nuget_bak" "$ROOT/nuget.config"; rm -f "$_nuget_bak"' EXIT
            dotnet nuget remove source godot-editor --configfile "$ROOT/nuget.config" >/dev/null
        fi
        ;;
esac

# ── 0. engine patch provenance (cheap, and fails before anything slow) ────────
# Only the --patches half here: --binary needs an export, which this gate does not do (release.yml
# runs that half straight after its export). This catches a patch edited or line-ending-mangled in
# place, which would otherwise surface as a template rebuilt from something nobody reviewed.
step "engine patch provenance (engine.lock.json)"
python "$ROOT/tools/verify-engine-template.py" --patches

# ── 1. libraries + tests build (plain .NET SDK, no Godot) ─────────────────────
step "build libraries + tests"
dotnet build "$ROOT/tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj" -c Debug --nologo

# ── 2. the full test suite (assets present → real-data tests run too) ─────────
step "dotnet test (baseline: 3931 passed / 0 failed; only MAP-dependent cases can skip)"
dotnet test "$ROOT/tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj" -c Debug --no-build --nologo
# Core content is COMMITTED (item 21), so it cannot legitimately be absent — only compiled maps can,
# and those are a fetch away. Fail loudly on a broken checkout instead of printing a note and carrying on
# with the real-data classes quietly skipped.
if [ ! -d "$ROOT/data" ]; then
    fail "data/ is missing. Core content is committed — this checkout is broken (not a download away)."
fi
if [ ! -d "$ROOT/data/maps" ] || [ -z "$(ls -A "$ROOT/data/maps" 2>/dev/null)" ]; then
    echo "NOTE: no compiled maps — the map-dependent cases self-skipped. For full coverage:"
    echo "      python tools/data/fetch-maps.py"
fi

# ── 3. the Godot host project (restores Godot.NET.Sdk via nuget.config) ───────
step "build the Godot host (XonoticGodot.csproj)"
dotnet build "$ROOT/XonoticGodot.csproj" -c Debug --nologo

# ── 4. headless boot smoke (docs/RUNNING.md 'Run headless') ────────────────────────
if $do_smoke; then
    if [ -x "$GODOT" ] || [ -f "$GODOT" ]; then
        step "headless smoke (--quit-after 200)"
        log="$(mktemp)"
        timeout 180 "$GODOT" --headless --path "$ROOT" --quit-after 200 > "$log" 2>&1 || true
        hard_errors=$(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' "$log" || true)
        echo "hard errors: $hard_errors | warnings: $(grep -c 'WARNING:' "$log" || true)"
        grep -iE "XonoticGodot boot|MenuState\]|NetGame\]|loaded .* shaders|collision brushes|spawned" "$log" || true
        [ "${hard_errors:-1}" -eq 0 ] || { echo "--- $log ---"; tail -40 "$log"; fail "headless smoke had $hard_errors hard error(s)"; }
        rm -f "$log"

        # Dedicated-server smoke (docs/RUNNING.md 'Dedicated server'): the headless listen server must load the
        # map, fill bots (waypoints load on the first frame with bots), and accept the self-connect — this
        # exact path regressed silently once (a FramePostDraw await that never fires headless). Needs assets.
        # Needs the stormkeep MAP, which is fetched build output rather than committed content. Fetch it
        # instead of skipping: a smoke test that silently opts out on the machine where content is missing
        # is a smoke test that never runs where it matters (item 22).
        if [ ! -f "$ROOT/data/maps/stormkeep.pk3" ] && [ ! -d "$ROOT/data/maps/stormkeep.pk3dir" ]; then
            step "fetching stormkeep for the host smoke"
            # Non-fatal here: the presence check below decides, so an offline run gets one clear
            # message from there rather than two from different layers.
            python "$ROOT/tools/data/fetch-maps.py" --only stormkeep || true
        fi
        if [ -f "$ROOT/data/maps/stormkeep.pk3" ] || [ -d "$ROOT/data/maps/stormkeep.pk3dir" ]; then
            step "headless host smoke (--host stormkeep --bots 2, 20s)"
            log="$(mktemp)"
            timeout 240 "$GODOT" --headless --path "$ROOT" --host stormkeep --gametype dm --bots 2 \
                --quit-after-seconds 20 > "$log" 2>&1 || true
            # Belt-and-braces: Windows `timeout` can't kill the Godot child; a hung host would hold UDP 26000.
            command -v powershell >/dev/null 2>&1 && \
                powershell -Command "Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force" >/dev/null 2>&1 || true
            hard_errors=$(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' "$log" || true)
            echo "hard errors: $hard_errors | warnings: $(grep -c 'WARNING:' "$log" || true)"
            grep -aE "MapLoader|waypoints for|handshake accepted" "$log" || true
            grep -aq "MapLoader"          "$log" || { tail -40 "$log"; fail "host smoke: map never loaded ([MapLoader] missing)"; }
            grep -aq "waypoints for"      "$log" || { tail -40 "$log"; fail "host smoke: bots never filled ([bots] waypoints missing)"; }
            grep -aq "handshake accepted" "$log" || { tail -40 "$log"; fail "host smoke: client never connected (handshake missing)"; }
            [ "${hard_errors:-1}" -eq 0 ] || { echo "--- $log ---"; tail -40 "$log"; fail "host smoke had $hard_errors hard error(s)"; }
            rm -f "$log"
        else
            fail "stormkeep is not present and could not be fetched — the headless host smoke cannot run.
      This smoke covers the listen-server path that regressed silently once (a FramePostDraw await
      that never fires headless), so skipping it is not an acceptable outcome. Fetch manually:
        python tools/data/fetch-maps.py --only stormkeep"
        fi
    else
        echo "NOTE: Godot not found at '$GODOT' — skipping the headless smoke (set GODOT= or pass --no-smoke to silence)."
    fi
fi

# ── 5. Visual QA (headless assertions only) ───────────────────────────────────
# T5 (Wave A5). Godot's headless renderer (dummy_video) renders NOTHING, so NO rendered-frame / pixel
# correctness can run in CI — see tools/visual-qa.sh + docs/RUNNING.md "Visual QA" for the WINDOWED manual half.
# What CI *can* assert is structural: every stock map parses with renderable+collidable geometry, every model
# loads with a valid bone parent-chain; IQM models are additionally validated for a non-singular bind pose (unit
# bind quat + non-zero scales), while DPM and MD3 deliberately PERMIT singular/non-unit-scale content per the
# shipped DP baselines (DPM ships zero-scale helper bones; MD3 tag axes carry non-unit scale). Every .shader
# script compiles (parses) with no hard failure. VisualQaTests already ran inside step 2's full suite; this re-runs JUST that filter for a
# focused, greppable per-asset summary (map-dependent theories self-skip without data/maps, like the other real-data
# tests). It needs no Godot — pure xUnit over the parsed asset structures.
step "Visual QA (headless assertions only): VisualQa map/model/shader sweep"
vqa_log="$(mktemp)"
dotnet test "$ROOT/tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj" -c Debug --no-build --nologo \
    --filter "FullyQualifiedName~VisualQa" > "$vqa_log" 2>&1 || { cat "$vqa_log"; rm -f "$vqa_log"; fail "Visual QA headless assertions failed"; }
grep -E "Passed!|Failed!|Passed:|Failed:|Skipped:|Total tests" "$vqa_log" || true
if [ -d "$ROOT/data/maps" ] && [ -n "$(ls -A "$ROOT/data/maps" 2>/dev/null)" ]; then
    echo "Visual QA (headless): asserted load + structure for every stock map/model/shader; pixel correctness is the WINDOWED tools/visual-qa.sh checklist (docs/RUNNING.md)."
else
    echo "NOTE: no compiled maps — the MAP theories self-skipped (models/shaders from core content still ran)."
    echo "      python tools/data/fetch-maps.py"
fi
rm -f "$vqa_log"

# ── 6. optional: the two export presets (untested path — see ADR-0014) ────────
if $do_export; then
    [ -f "$GODOT" ] || fail "--export needs Godot (set GODOT=)"
    step "export windows-client + linux-client + linux-dedicated (macos-client is CI-only — needs a Mac)"
    mkdir -p "$ROOT/dist/windows-client" "$ROOT/dist/linux-client" "$ROOT/dist/linux-dedicated"
    "$GODOT" --headless --path "$ROOT" --export-release "windows-client"  "$ROOT/dist/windows-client/XonoticGodot.exe"
    "$GODOT" --headless --path "$ROOT" --export-release "linux-client"    "$ROOT/dist/linux-client/XonoticGodot.x86_64"
    "$GODOT" --headless --path "$ROOT" --export-release "linux-dedicated" "$ROOT/dist/linux-dedicated/xonoticgodot-dedicated.x86_64"
    echo "exports in $ROOT/dist/ — run tools/package.sh to bundle assets + zip"
fi

printf '\n\033[1;32mci.sh: all steps passed.\033[0m\n'

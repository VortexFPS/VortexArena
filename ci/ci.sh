#!/usr/bin/env bash
# Local CI mirror for VortexArena (T33 — ADR-0014). Runs the same gate as
# .github/workflows/ci.yml PLUS the asset-dependent steps GitHub can't run:
# with data/ mounted, the ~18 real-data test classes actually execute
# (in CI they self-skip), and the headless boot smoke exercises real asset
# loading — so THIS script, not the green Actions badge, is the authoritative
# pre-push gate.
#
# Usage:
#   ci/ci.sh                 # build libs+tests, run the suite, build the Godot host, headless smoke
#   ci/ci.sh --no-smoke      # skip the Godot headless boot (no Godot install needed)
#   ci/ci.sh --export        # additionally run the three local export presets. Fetches the pinned
#                            # engine templates first and verifies the exported binaries after, the
#                            # same gates release.yml runs — an export that silently ships a stock
#                            # engine is the bug ADR-0017 exists to prevent, on CI and locally alike.
#
# Env:
#   GODOT  — path to the Godot 4.6.3 mono CONSOLE executable. Optional: when unset, tools/lib/find-godot.sh
#            probes .godot-bin/, PATH and the platform's install location. Set it to override.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Resolved, not hardcoded: $GODOT → .godot-bin/ → PATH → the platform's install location. Empty when
# nothing is found, which the --no-smoke path below treats as "skip", exactly as before.
. "$ROOT/tools/lib/find-godot.sh"
GODOT="$(find_godot "$ROOT")" || GODOT=""
# Python is NOT optional here — the provenance checks below run before anything else and are the point of
# the gate. Resolved rather than hardcoded to either spelling: `python` does not exist on macOS 12.3+ or
# most current Linux, and `python3` does not exist under the python.org Windows install.
. "$ROOT/tools/lib/find-python.sh"
PYTHON="$(find_python)" || { python_not_found; exit 1; }

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

# (Removed 2026-08-01, bootstrap Phase 0.) A block here used to back up nuget.config, strip the
# Windows-only 'godot-editor' package source on non-Windows hosts, and restore the file on exit — because
# NuGet and the Godot.NET.Sdk MSBuild resolver both hard-fail on a missing local source. nuget.config is
# now nuget.org-only, so there is nothing to strip and no need to mutate a tracked file mid-run.

# ── 0. engine patch provenance (cheap, and fails before anything slow) ────────
# --patches catches a patch edited or line-ending-mangled in place, which would otherwise surface as a
# template rebuilt from something nobody reviewed. --audit-presets catches the bookkeeping failure the
# per-preset gates structurally cannot: they are invoked BY NAME below and in release.yml, so a preset
# added to export_presets.cfg later would be gated by no step at all. Both read committed text only —
# no template on disk, no download — so they run on every ci.sh, not just --export.
# Not --binary: that needs an export, which this gate does not do (the --export path runs it below).
step "engine patch provenance + preset accounting (engine.lock.json)"
"$PYTHON" "$ROOT/tools/verify-engine-template.py" --patches --audit-presets

# The parity registry's port_refs are pointers the differ and the parity workflows follow. A dangling one
# does not look broken - it looks like coverage - so it needs a gate rather than a periodic audit. The
# Tier-1 rename broke all 360 in a single commit and nothing noticed until someone went looking.
step "parity registry pointers resolve"
"$PYTHON" "$ROOT/tools/check-parity-refs.py"

# ── 1. libraries + tests build (plain .NET SDK, no Godot) ─────────────────────
step "build libraries + tests"
dotnet build "$ROOT/tests/VortexArena.Tests/VortexArena.Tests.csproj" -c Debug --nologo

# ── 2. the full test suite (assets present → real-data tests run too) ─────────
step "dotnet test (baseline: 3931 passed / 0 failed; only MAP-dependent cases can skip)"
dotnet test "$ROOT/tests/VortexArena.Tests/VortexArena.Tests.csproj" -c Debug --no-build --nologo
# Core content is COMMITTED (item 21), so it cannot legitimately be absent — only compiled maps can,
# and those are a fetch away. Fail loudly on a broken checkout instead of printing a note and carrying on
# with the real-data classes quietly skipped.
if [ ! -d "$ROOT/data" ]; then
    fail "data/ is missing. Core content is committed — this checkout is broken (not a download away)."
fi
if [ ! -d "$ROOT/data/maps" ] || [ -z "$(ls -A "$ROOT/data/maps" 2>/dev/null)" ]; then
    echo "NOTE: no compiled maps — the map-dependent cases self-skipped. For full coverage:"
    echo "      $PYTHON tools/data/fetch-maps.py"
fi

# ── 3. the Godot host project (restores Godot.NET.Sdk via nuget.config) ───────
step "build the Godot host (VortexArena.csproj)"
dotnet build "$ROOT/VortexArena.csproj" -c Debug --nologo

# ── 4. headless boot smoke (docs/RUNNING.md 'Run headless') ────────────────────────
if $do_smoke; then
    if [ -x "$GODOT" ] || [ -f "$GODOT" ]; then
        step "headless smoke (--quit-after 200)"
        log="$(mktemp)"
        timeout 180 "$GODOT" --headless --path "$ROOT" --quit-after 200 > "$log" 2>&1 || true
        hard_errors=$(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' "$log" || true)
        echo "hard errors: $hard_errors | warnings: $(grep -c 'WARNING:' "$log" || true)"
        grep -iE "VortexArena boot|MenuState\]|NetGame\]|loaded .* shaders|collision brushes|spawned" "$log" || true
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
            "$PYTHON" "$ROOT/tools/data/fetch-maps.py" --only stormkeep || true
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
            grep -aE "MapLoader|waypoints for|handshake accepted|dedicated slim" "$log" || true
            grep -aq "MapLoader"          "$log" || { tail -40 "$log"; fail "host smoke: map never loaded ([MapLoader] missing)"; }
            grep -aq "waypoints for"      "$log" || { tail -40 "$log"; fail "host smoke: bots never filled ([bots] waypoints missing)"; }
            grep -aq "handshake accepted" "$log" || { tail -40 "$log"; fail "host smoke: client never connected (handshake missing)"; }
            # Dedicated-slim (docs/RUNNING.md "Dedicated server"): a headless host must NOT pay the client asset
            # pipeline (measured 4.9 GB -> 0.58 GB peak WS). If this line vanishes, the slim gate regressed silently.
            grep -aq "dedicated slim"     "$log" || { tail -40 "$log"; fail "host smoke: dedicated-slim gate did not engage"; }
            [ "${hard_errors:-1}" -eq 0 ] || { echo "--- $log ---"; tail -40 "$log"; fail "host smoke had $hard_errors hard error(s)"; }
            rm -f "$log"

            # ── DS-1/DS-2: the CLIENT-LESS dedicated host, driven through the stdin console ──────────
            # The point of --dedicated is that NO local client exists: no loopback peer, no burned player
            # slot, and a truthful browser player count. Assert that by A/B against the host smoke above
            # (which requires "handshake accepted") and by reading the console's own `status` block.
            # bot_join_empty is required because bot fill is gated on realPlayers>0 || bot_join_empty
            # (QC bot.qc:644-660) — the v1 host only filled an empty map via its phantom self-client.
            step "dedicated smoke (--dedicated stormkeep, stdin console, 20s)"
            dlog="$(mktemp)"
            { sleep 12; echo status; echo quit; } | timeout 240 "$GODOT" --headless --path "$ROOT" \
                --dedicated stormkeep --gametype dm --bots 2 --port 26099 \
                --cvar bot_join_empty 1 --quit-after-seconds 30 > "$dlog" 2>&1 || true
            command -v powershell >/dev/null 2>&1 && \
                powershell -Command "Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force" >/dev/null 2>&1 || true
            d_errors=$(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' "$dlog" || true)
            echo "hard errors: $d_errors | warnings: $(grep -c 'WARNING:' "$dlog" || true)"
            grep -aE "DEDICATED:|players:|waypoints for" "$dlog" || true
            grep -aq "DEDICATED: no local client" "$dlog" || { tail -40 "$dlog"; fail "dedicated smoke: client-less mode did not engage"; }
            # The load-bearing assertion: a dedicated host must NOT connect a loopback client.
            grep -aq "handshake accepted" "$dlog" && { tail -40 "$dlog"; fail "dedicated smoke: a local client connected (DS-1 regressed — the phantom slot is back)"; }
            grep -aq "waypoints for" "$dlog" || { tail -40 "$dlog"; fail "dedicated smoke: bots never filled (bot_join_empty gate?)"; }
            # `status` must report the 2 bots and NO phantom player.
            grep -aq "players: 2 (2 bots)" "$dlog" || { tail -40 "$dlog"; fail "dedicated smoke: expected 'players: 2 (2 bots)' from the console status"; }
            [ "${d_errors:-1}" -eq 0 ] || { echo "--- $dlog ---"; tail -40 "$dlog"; fail "dedicated smoke had $d_errors hard error(s)"; }
            rm -f "$dlog"
        else
            fail "stormkeep is not present and could not be fetched — the headless host smoke cannot run.
      This smoke covers the listen-server path that regressed silently once (a FramePostDraw await
      that never fires headless), so skipping it is not an acceptable outcome. Fetch manually:
        $PYTHON tools/data/fetch-maps.py --only stormkeep"
        fi
    else
        echo "NOTE: Godot not found — skipping the headless smoke (pass --no-smoke to silence)."
        godot_not_found "$ROOT"
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
dotnet test "$ROOT/tests/VortexArena.Tests/VortexArena.Tests.csproj" -c Debug --no-build --nologo \
    --filter "FullyQualifiedName~VisualQa" > "$vqa_log" 2>&1 || { cat "$vqa_log"; rm -f "$vqa_log"; fail "Visual QA headless assertions failed"; }
grep -E "Passed!|Failed!|Passed:|Failed:|Skipped:|Total tests" "$vqa_log" || true
if [ -d "$ROOT/data/maps" ] && [ -n "$(ls -A "$ROOT/data/maps" 2>/dev/null)" ]; then
    echo "Visual QA (headless): asserted load + structure for every stock map/model/shader; pixel correctness is the WINDOWED tools/visual-qa.sh checklist (docs/RUNNING.md)."
else
    echo "NOTE: no compiled maps — the MAP theories self-skipped (models/shaders from core content still ran)."
    echo "      $PYTHON tools/data/fetch-maps.py"
fi
rm -f "$vqa_log"

# ── 6. optional: the three local export presets (untested path — see ADR-0014) ─
if $do_export; then
    if [ -z "$GODOT" ] || { [ ! -f "$GODOT" ] && [ ! -x "$GODOT" ]; }; then
        godot_not_found "$ROOT"
        fail "--export needs Godot"
    fi

    # Fetch and gate the engine templates BEFORE exporting, mirroring release.yml. Without this the
    # local path was the hole the release workflow already spent effort closing: an export here used
    # whatever happened to be sitting in the gitignored tools/engine-templates/, or silently fell back
    # to the STOCK template on an empty custom_template/release and produced a launchable binary with
    # none of the backports (G10). macos is skipped — this script cannot export it, and the macOS
    # template is a 149 MB download nobody here would use.
    step "fetch the pinned engine templates (windows + linux)"
    "$PYTHON" "$ROOT/tools/data/fetch-engine-template.py" --only windows --only linux

    # Cheap and BEFORE the slow part: catches an emptied or re-pointed custom_template/release in a
    # second rather than after three full exports, and asserts each template is in a form that
    # platform's exporter can actually open (a sha256 cannot see that). This is the only G10 gate the
    # Linux presets have, because no binary marker can discriminate a patched Linux template from a
    # stock one — the patch set touches platform/windows/ exclusively. macos-client is absent because
    # it is not exported here AND is not pinned at all yet; see engine.lock.json's unpinned_presets,
    # and note that step 0's --audit-presets is what keeps that omission from being silent.
    step "engine template configured + hashes + form match (pre-export gate)"
    "$PYTHON" "$ROOT/tools/verify-engine-template.py" \
        --preset-config windows-client --preset-config linux-client --preset-config linux-dedicated

    step "export windows-client + linux-client + linux-dedicated (macos-client is CI-only — needs a Mac)"
    mkdir -p "$ROOT/dist/windows-client" "$ROOT/dist/linux-client" "$ROOT/dist/linux-dedicated"
    "$GODOT" --headless --path "$ROOT" --export-release "windows-client"  "$ROOT/dist/windows-client/VortexArena.exe"
    "$GODOT" --headless --path "$ROOT" --export-release "linux-client"    "$ROOT/dist/linux-client/VortexArena.x86_64"
    "$GODOT" --headless --path "$ROOT" --export-release "linux-dedicated" "$ROOT/dist/linux-dedicated/vortexarena-dedicated.x86_64"

    # Assert on the SHIPPED BYTES, which is the only thing that speaks to what a player would run.
    # Windows is the real content check (the backport's marker is present or the build is stock). The
    # two Linux invocations assert only the contamination canary and SAY SO — they print
    # "NOT CONTENT-VERIFIED" and the summary line refuses to claim otherwise. Running them is still
    # worth it: exclude_filter is per-preset and can regress on any one of them.
    step "windows: verify the engine template that was used; linux: contamination canary only"
    "$PYTHON" "$ROOT/tools/verify-engine-template.py" \
        --binary "$ROOT/dist/windows-client/VortexArena.exe" --preset windows-client
    "$PYTHON" "$ROOT/tools/verify-engine-template.py" \
        --binary "$ROOT/dist/linux-client/VortexArena.x86_64" --preset linux-client
    "$PYTHON" "$ROOT/tools/verify-engine-template.py" \
        --binary "$ROOT/dist/linux-dedicated/vortexarena-dedicated.x86_64" --preset linux-dedicated

    echo "exports in $ROOT/dist/ — run tools/package.sh to bundle assets + zip"
fi

printf '\n\033[1;32mci.sh: all steps passed.\033[0m\n'

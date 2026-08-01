# shellcheck shell=sh
# ---------------------------------------------------------------------------------------------------
# Godot resolver — the single place that answers "where is the engine on this machine".
#
# Sourced, not executed:
#
#     . "$ROOT/tools/lib/find-godot.sh"
#     GODOT="$(find_godot "$ROOT")" || { godot_not_found "$ROOT"; exit 1; }
#
# WHY THIS EXISTS. Every Godot-dependent script used to carry its own copy of
#   GODOT="${GODOT:-/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe}"
# — one developer's Windows install path, as the DEFAULT, in ci/ci.sh, run-release.sh, tools/perf-run.sh
# and tools/visual-qa.sh. On any other machine every one of those silently degraded (ci.sh skips the
# headless smoke and still prints a pass) or failed somewhere far from the cause. Fixing that in one place
# is Phase 0 of planning/bootstrap-and-task-runner-2026-08-01.md; `vx setup` will later install into
# .godot-bin/, which is why that is probed before PATH.
#
# POSIX sh ONLY — no arrays, no [[ ]], no ${var,,}. macOS ships bash 3.2.57 (2007) and this is also
# sourced from Git Bash on Windows; both must work, and this file must never need a feature newer than
# either. It deliberately depends on nothing but `command`, `uname` and test.
# ---------------------------------------------------------------------------------------------------

# The engine version this tree expects. Kept in step with tools/engine-patches/engine.lock.json
# (engine.version) and docs/RUNNING.md.
VORTEX_GODOT_VERSION="4.6.3"

# Print the resolved Godot executable on stdout and return 0; print nothing and return 1 when not found.
# $1 (optional) repo root, used to probe the repo-local .godot-bin/.
find_godot() {
    _fg_root="${1:-}"
    _fg_found=""

    # 1. $GODOT wins outright. An explicit choice must never be second-guessed — including when it points
    #    at a build this script would not have picked, which is exactly how you test a new engine.
    if [ -n "${GODOT:-}" ]; then
        if [ -x "$GODOT" ] || [ -f "$GODOT" ]; then
            printf '%s\n' "$GODOT"
            return 0
        fi
        # Set but wrong is a mistake worth reporting, not something to silently paper over by falling
        # through to a different engine than the one that was asked for.
        return 1
    fi

    # 2. The repo-local install (`vx setup` writes here). Probed before PATH so a clone can pin its own
    #    engine without touching the machine, and so two clones can disagree about the version.
    if [ -n "$_fg_root" ]; then
        for _fg_c in \
            "$_fg_root/.godot-bin/godot_console.exe" \
            "$_fg_root/.godot-bin/godot.exe" \
            "$_fg_root/.godot-bin/Godot.app/Contents/MacOS/Godot" \
            "$_fg_root/.godot-bin/godot"
        do
            if [ -f "$_fg_c" ]; then printf '%s\n' "$_fg_c"; return 0; fi
        done
    fi

    # 3. PATH. `command -v` rather than `which` (POSIX, and `which` is not on every image).
    for _fg_n in godot4 godot Godot godot-mono; do
        _fg_found="$(command -v "$_fg_n" 2>/dev/null)" || _fg_found=""
        if [ -n "$_fg_found" ]; then printf '%s\n' "$_fg_found"; return 0; fi
    done

    # 4. Per-platform install locations.
    case "$(uname -s 2>/dev/null || echo unknown)" in
        Darwin)
            # The binary lives inside the bundle; a .app path itself is not executable.
            for _fg_c in \
                "/Applications/Godot_mono.app/Contents/MacOS/Godot" \
                "/Applications/Godot.app/Contents/MacOS/Godot" \
                "$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot" \
                "$HOME/Applications/Godot.app/Contents/MacOS/Godot"
            do
                if [ -f "$_fg_c" ]; then printf '%s\n' "$_fg_c"; return 0; fi
            done
            ;;
        MINGW*|MSYS*|CYGWIN*)
            # Prefer the CONSOLE build: the plain .exe detaches from the terminal on Windows, so GD.Print
            # and errors never reach a captured stdout. Every headless/CI use here depends on that.
            for _fg_c in \
                "/c/Program Files/Godot/Godot_v${VORTEX_GODOT_VERSION}-stable_mono_win64_console.exe" \
                "/c/Program Files/Godot/Godot_v${VORTEX_GODOT_VERSION}-stable_mono_win64.exe" \
                "/c/Program Files/Godot/godot_console.exe" \
                "/c/Program Files/Godot/godot.exe"
            do
                if [ -f "$_fg_c" ]; then printf '%s\n' "$_fg_c"; return 0; fi
            done
            ;;
        Linux)
            for _fg_c in \
                "/usr/local/bin/godot" \
                "/usr/bin/godot" \
                "$HOME/.local/bin/godot" \
                "/var/lib/flatpak/exports/bin/org.godotengine.Godot"
            do
                if [ -f "$_fg_c" ]; then printf '%s\n' "$_fg_c"; return 0; fi
            done
            ;;
    esac

    return 1
}

# True when $1 is a .NET/mono build. The plain build cannot run C# at all, and its failure mode is a wall
# of script errors rather than anything naming the cause — so it is worth one process spawn to catch.
godot_is_mono() {
    [ -n "${1:-}" ] || return 1
    "$1" --version 2>/dev/null | grep -qi mono
}

# Explain the failure and what to do about it. Written to stderr by callers that cannot continue.
# $1 (optional) repo root.
godot_not_found() {
    _gn_root="${1:-.}"
    {
        echo ""
        if [ -n "${GODOT:-}" ]; then
            echo "Godot not found: \$GODOT is set to '$GODOT', which does not exist."
            echo "Fix or unset \$GODOT — when it is set, it is used verbatim and nothing else is probed."
        else
            echo "Godot ${VORTEX_GODOT_VERSION} (.NET/mono build) not found."
            echo ""
            echo "Looked in, in order:"
            echo "  1. \$GODOT                     (not set)"
            echo "  2. $_gn_root/.godot-bin/"
            echo "  3. PATH                        (godot4, godot, Godot, godot-mono)"
            echo "  4. the platform's usual install location"
        fi
        echo ""
        echo "Any one of these fixes it:"
        echo "  export GODOT=/path/to/godot            # console build on Windows — see docs/RUNNING.md"
        echo "  ./vx setup                             # once the task runner lands, installs to .godot-bin/"
        echo ""
    } >&2
}

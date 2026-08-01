# shellcheck shell=sh
# ---------------------------------------------------------------------------------------------------
# Python resolver — the companion to find-godot.sh, for the same reason.
#
#     . "$ROOT/tools/lib/find-python.sh"
#     PYTHON="$(find_python)" || { python_not_found; exit 1; }
#     "$PYTHON" "$ROOT/tools/data/fetch-maps.py"
#
# WHY NOT JUST WRITE python3. Because that breaks the Windows dev box, which is the one machine where all
# of this currently works. Apple removed /usr/bin/python in macOS 12.3 and most current Linux distros ship
# only python3, so a bare `python` fails there — but the python.org Windows installer creates python.exe
# and the `py` launcher and NOT python3.exe (only the Microsoft Store build adds python3.exe), and Git Bash
# ships no Python at all. So neither spelling is portable on its own and the name has to be resolved.
#
# tools/package.sh:213 already reached this conclusion independently
# (`command -v python3 || command -v python`); this is that pattern, shared, with a version check added.
#
# POSIX sh ONLY — see the note in find-godot.sh. macOS bash is 3.2.57 and Git Bash must work too.
# ---------------------------------------------------------------------------------------------------

# Oldest interpreter the tools/ scripts are known to run on. macOS ships 3.9.6 via the Xcode command line
# tools, which is the realistic floor for a fresh clone on a Mac.
VORTEX_PYTHON_MIN="3.8"

# Print a usable Python 3 command on stdout and return 0; print nothing and return 1 when there is none.
find_python() {
    # $PYTHON wins outright, like $GODOT: an explicit choice is never second-guessed, and set-but-broken
    # is a mistake to report rather than paper over by silently running a different interpreter.
    if [ -n "${PYTHON:-}" ]; then
        if _fp_is_python3 "$PYTHON"; then printf '%s\n' "$PYTHON"; return 0; fi
        return 1
    fi
    # python3 first: on macOS and modern Linux it is the only spelling that exists. `python` second: on
    # Windows it is the only spelling that exists. Checking the version rather than trusting the name is
    # what stops a lingering Python 2 `python` on an old Linux box being picked.
    for _fp_c in python3 python; do
        if command -v "$_fp_c" >/dev/null 2>&1 && _fp_is_python3 "$_fp_c"; then
            printf '%s\n' "$_fp_c"
            return 0
        fi
    done
    return 1
}

# True when $1 is a Python >= VORTEX_PYTHON_MIN. Runs the interpreter rather than parsing `--version`,
# because the version string has moved between stdout and stderr across releases.
_fp_is_python3() {
    "$1" -c "import sys; sys.exit(0 if sys.version_info[:2] >= tuple(int(p) for p in '${VORTEX_PYTHON_MIN}'.split('.')) else 1)" >/dev/null 2>&1
}

python_not_found() {
    {
        echo ""
        if [ -n "${PYTHON:-}" ]; then
            echo "\$PYTHON is set to '$PYTHON', which is not a working Python >= ${VORTEX_PYTHON_MIN}."
            echo "Fix or unset it — when set, it is used verbatim and nothing else is probed."
        else
            echo "Python >= ${VORTEX_PYTHON_MIN} not found (tried: python3, python)."
            echo ""
            echo "The repo tooling needs it — fetching maps and engine templates, and the provenance checks."
            echo ""
            echo "  macOS    xcode-select --install      (ships 3.9)"
            echo "  Debian   sudo apt install python3"
            echo "  Fedora   sudo dnf install python3"
            echo "  Arch     sudo pacman -S python"
            echo "  Windows  https://www.python.org/downloads/  (tick 'Add python.exe to PATH')"
            echo ""
            echo "Or point at one explicitly:  export PYTHON=/path/to/python3"
        fi
        echo ""
    } >&2
}

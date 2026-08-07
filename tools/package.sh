#!/usr/bin/env bash
# Package VortexArena distributions (T33 — ADR-0014; extended 2026-06 for the full release matrix).
# Takes the export-preset outputs under dist/<target>/ (produced by `./vx export`, `ci/ci.sh --export`,
# or the release workflow), lays the game assets beside each binary, adds the launcher + licenses + a
# README, and zips each into a versioned "complete" archive (binary + Godot runtime + all Xonotic data
# — one download, unzip and play). Mirrors the upstream layout: one install dir = binary + data +
# launch script.
#
# Targets (each = one export preset → one zip):
#   windows-client    dist/windows-client/VortexArena.exe            → VortexArena-<ver>-windows-x86_64.zip
#   linux-client      dist/linux-client/VortexArena.x86_64           → VortexArena-<ver>-linux-x86_64.zip
#   linux-dedicated   dist/linux-dedicated/vortexarena-dedicated.*   → VortexArena-<ver>-linux-dedicated-x86_64.zip
#   macos-client      dist/macos-client/VortexArena.app              → VortexArena-<ver>-macos-universal.zip
#       (macOS keeps its data INSIDE the bundle at Contents/Resources/data — DataPaths.Resolve
#        probes ../Resources relative to the executable, so a double-clicked .app finds it.)
#
# Usage:
#   tools/package.sh                          # package every target whose export output exists
#   tools/package.sh windows-client           # only the named target(s)
#   tools/package.sh --version 0.1.0          # stamp the zip names (default: `git describe` or "dev")
#   tools/package.sh --no-zip                 # lay out the dist dirs but skip archiving
#
# Content is committed to the repo (maps via tools/data/fetch-maps.py). Zipping prefers `zip`,
# then `7z`, then python3's zipfile — so it works on the Windows runner (Git Bash has no `zip`) too.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Python spelling differs by platform (`python` is gone on macOS 12.3+/most Linux; `python3` does not
# exist under the python.org Windows install), so resolve it for the hints below rather than guessing.
. "$ROOT/tools/lib/find-python.sh"
VX_PY="$(find_python 2>/dev/null || echo python3)"
DIST="$ROOT/dist"
# The committed port content. Was $ROOT/assets/data, which on a dev box is a JUNCTION to the
# pristine upstream reference — packaging from it shipped upstream content with no vortex-*.cfg
# layer and no core.pk3dir. Must stay in step with DataPaths.Resolve's default (res://data): the
# packaged probe is derived from that default, so a mismatch here ships a build that finds nothing.
ASSETS_SRC="$ROOT/data"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$*"; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

# ── args ──────────────────────────────────────────────────────────────────────
do_zip=true
version=""
requested=()
while [ $# -gt 0 ]; do
    case "$1" in
        --no-zip)   do_zip=false ;;
        --version)  shift; version="${1:-}" ;;
        --version=*) version="${1#--version=}" ;;
        --help|-h)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        --*) echo "Unknown option: $1 (try --help)"; exit 1 ;;
        *) requested+=("$1") ;;
    esac
    shift
done

if [ -z "$version" ]; then
    version="$(git -C "$ROOT" describe --tags --always --dirty 2>/dev/null || echo dev)"
fi
version="${version#v}"   # a "v0.1.0" tag → "0.1.0" in the file name
info "version: $version"

# target → (export-output marker, friendly zip suffix)
marker_for()  { case "$1" in
    windows-client)    echo "windows-client/VortexArena.exe" ;;
    windows-dedicated) echo "windows-dedicated/vortexarena-dedicated.exe" ;;
    linux-client)      echo "linux-client/VortexArena.x86_64" ;;
    linux-dedicated)   echo "linux-dedicated/vortexarena-dedicated.x86_64" ;;
    macos-client)      echo "macos-client/VortexArena.app" ;;
esac; }
suffix_for()  { case "$1" in
    windows-client)    echo "windows-x86_64" ;;
    windows-dedicated) echo "windows-dedicated-x86_64" ;;
    linux-client)      echo "linux-x86_64" ;;
    linux-dedicated)   echo "linux-dedicated-x86_64" ;;
    macos-client)      echo "macos-universal" ;;
esac; }

# There is deliberately no macos-dedicated. A dedicated server is a thing operators run on a host they
# rent, and nobody rents macOS hosts; the macos-client target is already best-effort here (it exports
# from the STOCK template, see engine.lock.json unpinned_presets) and adding a second macOS target
# would double that unpinned surface for no operator who exists.
ALL_TARGETS=(windows-client windows-dedicated linux-client linux-dedicated macos-client)
[ ${#requested[@]} -gt 0 ] || requested=("${ALL_TARGETS[@]}")

# ── 1. find which requested targets actually have an export output ────────────
targets=()
for t in "${requested[@]}"; do
    marker="$DIST/$(marker_for "$t")"
    if [ -e "$marker" ]; then
        targets+=("$t")
    else
        warn "$t: no export output at $marker — skipping (run the export preset first)"
    fi
done
if [ ${#targets[@]} -eq 0 ]; then
    error "no export outputs found under $DIST/."
    error "run 'ci/ci.sh --export' or the release workflow (needs the Godot 4.6.3 mono export templates) first."
    exit 1
fi
info "packaging: ${targets[*]}"

# ── 2. content ────────────────────────────────────────────────────────────────
# No download branch any more (item 18): core content, music and fonts arrive with the clone, and
# compiled maps come from tools/data/fetch-maps.py per data/maps.lock.json. So a missing or empty
# source tree is a hard error rather than something to paper over by fetching — packaging silently
# from an incomplete tree is how a shippable-looking zip ends up with no content in it.
if [ ! -d "$ASSETS_SRC" ] || [ -z "$(ls -A "$ASSETS_SRC" 2>/dev/null)" ]; then
    error "content tree missing or empty: $ASSETS_SRC"
    error "  core content is committed — if it is absent this checkout is broken."
    error "  compiled maps: $VX_PY tools/data/fetch-maps.py"
    exit 1
fi
if [ ! -d "$ASSETS_SRC/maps" ] || [ -z "$(ls -A "$ASSETS_SRC/maps" 2>/dev/null)" ]; then
    warn "no compiled maps in $ASSETS_SRC/maps — this build will ship without playable maps."
    warn "  run: $VX_PY tools/data/fetch-maps.py"
fi

copy_assets() {  # copy_assets <dest-data-dir>
    local dest="$1"
    info "  content → ${dest#$DIST/}"

    # UNLINK FIRST. `dest` is dist/<target>/data, which is exactly where `./vx export` leaves a LINK to the
    # repo's data/ so an exported build can find content without a 0.9 GB copy (Wrappers.PlaceContent).
    # Packaging must replace that with a REAL directory, and writing through it instead is bad twice over:
    #   rsync -a --delete  → source and destination resolve to the SAME tree, and --delete is pointed at
    #                        the committed content tree rather than at a staging copy.
    #   cp fallback        → `rm -rf "$dest"` one resolved link away from deleting data/ outright.
    # Removing the link (never its target) makes both paths operate on a fresh directory, which is what
    # every later step already assumes. -L catches Unix symlinks and, under Git Bash/MSYS, the Windows
    # junction `vx export` prefers there.
    if [ -L "$dest" ]; then
        info "  (replacing the dev link at ${dest#$DIST/} with a real copy)"
        rm -f "$dest"
    fi

    mkdir -p "$dest"
    # The .git exclusion is gone with the pk3dir clones it existed for: content is committed to this
    # repo now, so there are no nested checkouts under it to strip.
    if command -v rsync &>/dev/null; then
        rsync -a --delete "$ASSETS_SRC/" "$dest/"
    else
        rm -rf "$dest"; mkdir -p "$dest"
        cp -r "$ASSETS_SRC/." "$dest/"
    fi
}

write_readme() {  # write_readme <dir> <target>
    local dir="$1" t="$2"
    cat > "$dir/README.txt" <<EOF
Vortex Arena — $t ($version)
A fork of Xonotic, reborn on Godot + C#.  https://github.com/VortexFPS/VortexArena

This is a "complete" build: the game binary, the Godot runtime, the .NET runtime, and all Xonotic
game data are bundled together. You do not need .NET installed on your system. Keep the files
together — the game loads data/ from beside the binary.

Source code and licensing
-------------------------
Vortex Arena is free software under the GNU General Public License version 3 or later.
The complete corresponding source for this build, and the licence texts, are available at:

    game code + content   https://github.com/VortexFPS/VortexArena
    map sources           https://github.com/VortexFPS/VortexMaps

Licence texts for the bundled game content are in data/licenses/ beside this file.
No charge, no registration, same place as the download — see GPLv3 section 6(d).
EOF
    case "$t" in
        windows-client)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  double-click VortexArena.exe  (or VortexArena.console.exe for a debug console window).
EOF
            ;;
        linux-client)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  ./run-client.sh        (or run ./VortexArena.x86_64 directly)
EOF
            ;;
        windows-dedicated)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  run-dedicated.cmd [map]       (dedicated server, e.g. run-dedicated.cmd stormkeep)

Use the .cmd rather than the .exe directly: the build finds data\ relative to the working
directory, and only the script guarantees that is this folder.

Server output goes to the window the script was started from. If you launch it by
double-clicking, a console window opens and closes with the server.
EOF
            ;;
        linux-dedicated)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  ./run-dedicated.sh [map]      (dedicated server, e.g. ./run-dedicated.sh stormkeep)
EOF
            ;;
        macos-client)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  double-click VortexArena.app
This build is UNSIGNED. The first launch macOS will refuse it ("can't be opened"). Clear the
quarantine flag once, from Terminal in this folder:
    xattr -dr com.apple.quarantine VortexArena.app
then double-click it (or right-click → Open).
EOF
            ;;
    esac
}

# ── 3. lay out each target ────────────────────────────────────────────────────
for t in "${targets[@]}"; do
    info "$t:"
    tdir="$DIST/$t"
    if [ "$t" = macos-client ]; then
        # macOS: data lives INSIDE the bundle so a double-clicked .app finds it (exe-relative ../Resources).
        copy_assets "$tdir/VortexArena.app/Contents/Resources/data"
    else
        copy_assets "$tdir/data"
    fi

    for lic in COPYING GPL-3; do
        [ -f "$ROOT/$lic" ] && cp "$ROOT/$lic" "$tdir/"
    done
    write_readme "$tdir" "$t"

    case "$t" in
        linux-client)
            cp "$ROOT/tools/run-client.sh" "$tdir/"
            chmod +x "$tdir/run-client.sh" "$tdir/VortexArena.x86_64" 2>/dev/null || true ;;
        linux-dedicated)
            cp "$ROOT/tools/run-dedicated.sh" "$tdir/"
            chmod +x "$tdir/run-dedicated.sh" "$tdir/vortexarena-dedicated.x86_64" 2>/dev/null || true ;;
        windows-dedicated)
            # No chmod: the zip is built on the Windows runner and unpacked on Windows, where the
            # executable bit does not exist. Copy only.
            cp "$ROOT/tools/run-dedicated.cmd" "$tdir/" ;;
    esac
done

# ── 4. zip (zip → 7z → python3 fallback, so the Windows runner works too) ─────
zip_dir() {  # zip_dir <out.zip> <dist-relative-dir>
    local out="$1" d="$2"
    rm -f "$out"
    if command -v zip &>/dev/null; then
        # -y: store symlinks AS symlinks (matters for the macOS .app's embedded-framework links).
        ( cd "$DIST" && zip -qry -y "$out" "$d" )
    elif command -v 7z &>/dev/null; then
        ( cd "$DIST" && 7z a -tzip -bso0 -bsp0 "$out" "$d" >/dev/null )
    elif command -v python3 &>/dev/null || command -v python &>/dev/null; then
        local py; py="$(command -v python3 || command -v python)"
        ( cd "$DIST" && "$py" - "$out" "$d" <<'PY'
import sys, os, zipfile
out, root = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as z:
    for dp, _, fs in os.walk(root):
        for f in fs:
            full = os.path.join(dp, f)
            z.write(full, full)
PY
        )
    else
        error "no zip / 7z / python available — '$d' laid out but not archived"; return 1
    fi
}

if $do_zip; then
    : > "$DIST/SHA256SUMS-$version.txt"
    for t in "${targets[@]}"; do
        out="$DIST/VortexArena-$version-$(suffix_for "$t").zip"
        info "zipping $(basename "$out")"
        zip_dir "$out" "$t"
        # checksum (sha256sum on Linux, shasum on macOS)
        if command -v sha256sum &>/dev/null; then
            ( cd "$DIST" && sha256sum "$(basename "$out")" >> "SHA256SUMS-$version.txt" )
        elif command -v shasum &>/dev/null; then
            ( cd "$DIST" && shasum -a 256 "$(basename "$out")" >> "SHA256SUMS-$version.txt" )
        fi
    done
fi

info "Done. Distributions in $DIST/"
# An `if` rather than `$do_zip && info ...`: as the script's last command that AND-list makes the
# whole script exit 1 under --no-zip even though packaging succeeded (set -e never fires, since the
# test sits in a condition position), so callers reading the exit code saw a bogus failure.
if $do_zip; then
    info "Zips: $(cd "$DIST" && ls VortexArena-"$version"-*.zip 2>/dev/null | tr '\n' ' ')"
fi

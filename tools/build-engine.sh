#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------------------
# Build the Godot engine from source — the editor, the export template, or both.
#
# WHY THIS EXISTS. Until now this tree could not build its own engine on a developer's machine. The
# two halves came from somewhere else:
#
#   the EDITOR    was downloaded, prebuilt, from godotengine's releases (tools/godot.lock.json,
#                 installed by `vx setup` into .godot-bin/).
#   the TEMPLATE  was built from source, but ONLY by .github/workflows/build-engine-template.yml on a
#                 GitHub runner, plus a prose recipe in tools/engine-patches/README.md that one person
#                 had run by hand.
#
# That is fine while every target architecture has an upstream binary and a GitHub runner. It stops
# being fine the moment one does not — 64-bit little-endian PowerPC (ppc64le) has NEITHER: Godot
# publishes no POWER builds, and GitHub hosts no POWER runners. On such a machine the prose recipe is
# the only path, and a recipe that lives in a README drifts from the workflow that is actually
# maintained.
#
# So this script is the ONE executable copy of the sequence, and the workflow's steps are its source of
# truth: same scons flags, same order, same verification. Run it on the machine you want a binary for.
#
# ── The sequence, and why it is more than one scons call ──────────────────────────────────────────
# A .NET-enabled engine cannot be built in a single pass. The C# bindings compile from generated glue
# (modules/mono/glue/*.gen.cpp), the glue is produced by RUNNING a Godot editor binary, and so the
# editor must be built first — even when the editor is not what you wanted. That is Godot's own
# sequence, not an inefficiency to optimise away:
#
#   1. verify the patch set matches engine.lock.json      (seconds; fails before anything expensive)
#   2. check out godotengine/godot at the pinned tag
#   3. apply tools/engine-patches/*.patch                 (idempotent — a re-run does not double-apply)
#   4. preflight the scons argument line                  (`--help` configures and exits; ~1 minute)
#   5. scons target=editor                                 \
#   6. <editor> --headless --generate-mono-glue             > required for ANY .NET build
#   7. build_assemblies.py                                 /
#   8. scons target=template_release                       (only for --target template|both)
#
# Usage:
#   tools/build-engine.sh                            # host platform + host arch, editor and template
#   tools/build-engine.sh --target editor            # just the editor (steps 1-7)
#   tools/build-engine.sh --arch ppc64 --install     # POWER, and put the results where the tree looks
#   tools/build-engine.sh --src ../godot-4.6.3-src   # reuse an existing clone instead of making one
#   tools/build-engine.sh --dry-run                  # print every command, run none of them
#
# Options:
#   --platform P   linuxbsd | windows | macos          (default: detected from uname)
#   --arch A       x86_64 | arm64 | ppc64 | rv64 | ...  (default: detected from uname -m)
#   --target T     editor | template | both             (default: both)
#   --src DIR      where the Godot source lives/goes    (default: ../godot-<version>-vortex,
#                                                        or $GODOT_SRC)
#   -j N           parallel jobs                        (default: detected core count)
#   --install      copy the results into .godot-bin/ (editor) and tools/engine-templates/ (template)
#   --no-patches   build STOCK — no Vortex patches. For A/B measurement only; never for a release.
#   --dry-run      print the plan and the exact commands, change nothing
#
# Prerequisites this script checks for rather than assumes: git, python3, scons, a C++ toolchain, and
# the .NET SDK. It does NOT install them — `vx doctor` reports them and your package manager owns them.
# Godot's own per-platform dependency list is the authority:
#   https://docs.godotengine.org/en/stable/contributing/development/compiling/
#
# Expect tens of minutes to hours, depending entirely on the machine. Two full engine compiles for
# --target both; the second is much cheaper than the first because scons reuses objects. MEASURED: 36
# minutes for both on the ppc64le box that first ran this, at -j10 (2026-08-20) - so "hours" was
# pessimistic there, and a laptop with four cores will still be slower than that.
#
# It says HOW MANY hours before it starts. The "sizing the job" step reports the CPU, the core count it
# will use, the source-file count of the tree it is about to compile and the free space it has, then
# estimates each target. The first build on a machine estimates from a calibration compile (a floor: it
# cannot see the C++ the build generates, and excludes linking); every build after that estimates from
# what actually happened here, recorded in _scratch/build-engine-history.json. Both say which they are,
# because an estimate whose basis is hidden cannot be argued with when it is wrong.
# ---------------------------------------------------------------------------------------------------
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOCKFILE="$ROOT/tools/engine-patches/engine.lock.json"
PATCH_DIR="$ROOT/tools/engine-patches"

# Python spelling differs by platform (no `python` on macOS 12.3+, no `python3` under the python.org
# Windows install), so resolve it the way every other script in this tree does rather than guessing.
. "$ROOT/tools/lib/find-python.sh"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
step()  { printf '\n\033[1;34m== %s ==\033[0m\n' "$*"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$*"; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }
die()   { error "$@"; exit 1; }

# ── host detection ───────────────────────────────────────────────────────────────────────────────
# Godot's own names, not the OS's. platform_methods.py owns the arch spellings and SConstruct owns the
# aliases; this table is that mapping applied to `uname -m`, so a machine reports the same word the
# build system will use in the binary's FILENAME. Getting it wrong here means the build succeeds and
# every later path lookup misses.
detect_platform() {
    case "$(uname -s 2>/dev/null || echo unknown)" in
        Linux)                  echo "linuxbsd" ;;
        Darwin)                 echo "macos" ;;
        MINGW*|MSYS*|CYGWIN*)   echo "windows" ;;
        FreeBSD|OpenBSD|NetBSD) echo "linuxbsd" ;;   # Godot's "linuxbsd" really is both
        *)                      echo "" ;;
    esac
}

detect_arch() {
    case "$(uname -m 2>/dev/null || echo unknown)" in
        x86_64|amd64)   echo "x86_64" ;;
        i686|i386)      echo "x86_32" ;;
        aarch64|arm64)  echo "arm64" ;;
        armv7l|armv7)   echo "arm32" ;;
        riscv64)        echo "rv64" ;;
        loongarch64)    echo "loongarch64" ;;
        # ppc64le is the ONLY PowerPC spelling that leads anywhere. Godot calls it "ppc64" (SConstruct
        # aliases ppc64le to it) and is little-endian only, and .NET is published for linux-ppc64le and
        # not for big-endian ppc64 — so a big-endian host is refused by name below rather than allowed
        # to discover the same wall three hours into a compile.
        ppc64le)        echo "ppc64" ;;
        ppc64)          echo "ppc64-BIGENDIAN" ;;
        *)              echo "" ;;
    esac
}

# ── args ─────────────────────────────────────────────────────────────────────────────────────────
platform=""
arch=""
target="both"
src="${GODOT_SRC:-}"
jobs=""
install=false
apply_patches=true
dry_run=false

while [ $# -gt 0 ]; do
    case "$1" in
        --platform)   shift; platform="${1:-}" ;;
        --platform=*) platform="${1#--platform=}" ;;
        --arch)       shift; arch="${1:-}" ;;
        --arch=*)     arch="${1#--arch=}" ;;
        --target)     shift; target="${1:-}" ;;
        --target=*)   target="${1#--target=}" ;;
        --src)        shift; src="${1:-}" ;;
        --src=*)      src="${1#--src=}" ;;
        -j)           shift; jobs="${1:-}" ;;
        -j*)          jobs="${1#-j}" ;;
        --jobs)       shift; jobs="${1:-}" ;;
        --jobs=*)     jobs="${1#--jobs=}" ;;
        --install)    install=true ;;
        --no-patches) apply_patches=false ;;
        --dry-run)    dry_run=true ;;
        --help|-h)    grep '^#' "$0" | grep -v '^#!' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)            die "unknown option: $1 (try --help)" ;;
    esac
    shift
done

case "$target" in
    editor|template|both) ;;
    *) die "--target must be editor, template or both (got '$target')" ;;
esac

[ -n "$platform" ] || platform="$(detect_platform)"
[ -n "$arch" ] || arch="$(detect_arch)"

if [ "$arch" = "ppc64-BIGENDIAN" ]; then
    cat >&2 <<'EOF'
ERROR: this is a BIG-ENDIAN ppc64 host, and the port stops here rather than three hours in.

  * Godot 4 is little-endian only.
  * .NET is published for linux-ppc64le and for no big-endian PowerPC target, so even a
    successfully built engine could not load the game's C# assemblies.

The supported PowerPC target is ppc64le (POWER8 and newer, little-endian). See
planning/ppc64le-port-2026-08-19.md.
EOF
    exit 1
fi

[ -n "$platform" ] || die "could not detect the platform from '$(uname -s)'. Pass --platform."
[ -n "$arch" ] || die "could not detect the architecture from '$(uname -m)'. Pass --arch."

# Resolve -j ONCE and assert it is a positive integer before anything expensive starts — the same
# guard the workflow carries, and for the same reason: a malformed -j kills the job minutes in, having
# already cost the configure.
if [ -z "$jobs" ]; then
    case "$platform" in
        windows) jobs="${NUMBER_OF_PROCESSORS:-4}" ;;
        macos)   jobs="$(sysctl -n hw.ncpu 2>/dev/null || echo 4)" ;;
        *)       jobs="$(nproc 2>/dev/null || echo 4)" ;;
    esac
fi
printf '%s' "$jobs" | grep -qE '^[1-9][0-9]*$' \
    || die "job count is '$jobs', not a positive integer. Refusing to start a multi-hour build on it."

# ── the pinned engine version, read from the lockfile rather than typed here ──────────────────────
VX_PY="$(find_python)" || die "no Python 3 on PATH (see tools/lib/find-python.sh; \$PYTHON overrides)"
[ -f "$LOCKFILE" ] || die "missing $LOCKFILE — this script builds what that file pins, so it cannot proceed."

read_lock() { "$VX_PY" -c "
import json, sys
with open(sys.argv[1], encoding='utf-8') as fh:
    lock = json.load(fh)
node = lock
for key in sys.argv[2].split('.'):
    node = node[key]
print(node)
" "$LOCKFILE" "$1"; }

ENGINE_TAG="$(read_lock engine.upstream_tag)"
ENGINE_VERSION="$(read_lock engine.version)"

[ -n "$src" ] || src="$ROOT/../godot-${ENGINE_VERSION}-vortex"

# Windows binaries carry .exe; every path below is built from these two.
exe=""
if [ "$platform" = "windows" ]; then exe=".exe"; fi
EDITOR_BIN="bin/godot.${platform}.editor.${arch}.mono${exe}"
TEMPLATE_BIN="bin/godot.${platform}.template_release.${arch}.mono${exe}"

# Matches build-engine-template.yml's SCONS_COMMON exactly. Do not drift: the whole point of pinning a
# template is that the CI artifact and a locally built one are the same CONFIGURATION, not merely both
# "patched". cvtt_export_templates=yes builds the BPTC/BC7 encoder into templates (upstream excludes
# the cvtt module from template builds), which together with the etcpak patch is what makes
# gl_texturecompression real in a release build rather than a silent no-op.
SCONS_COMMON=(module_mono_enabled=yes cvtt_export_templates=yes)

# Per-platform extras, also from the workflow's matrix. d3d12=no matches export_presets.cfg's
# application/export_d3d12=0 — the project runs Vulkan, and the D3D12 driver would pull an extra SDK
# for nothing.
SCONS_EXTRA=()
if [ "$platform" = "windows" ]; then SCONS_EXTRA+=(d3d12=no); fi

# NOTE for ppc64: no accesskit=no is needed, and adding one would be a silent configuration drift from
# CI. Checked against 4.6.3-stable's platform/linuxbsd/detect.py: the per-arch LIBPATH table (arm64,
# arm32, rv64, x86_64, x86_32 — no ppc64) is only consulted when accesskit_sdk_path is NON-EMPTY. It is
# empty by default and empty in CI, which takes the else branch to ACCESSKIT_DYNAMIC and needs no SDK.

# ─ how long is this going to take ──────────────────────────────────
# Everything here MEASURES rather than assumes, and prints what it measured. An estimate whose inputs are
# invisible is worse than none: nobody can tell a wrong answer from a wrong machine, so nobody trusts the
# right one either. The first build on a machine has no history to go on and calibrates against that
# machine's own compiler; every build after that uses what actually happened here last time.
HISTORY="$ROOT/_scratch/build-engine-history.json"

fmt_duration() {
    _d=${1%.*}
    if [ "$_d" -lt 90 ]; then printf '%ds' "$_d"
    elif [ "$_d" -lt 5400 ]; then printf '%dm' "$((_d / 60))"
    else printf '%dh %dm' "$((_d / 3600))" "$(((_d % 3600) / 60))"
    fi
}

# CPU model and logical core count. Unknown is printed as unknown rather than guessed at.
cpu_summary() {
    _model=""
    case "$platform" in
        linuxbsd)
            if [ -r /proc/cpuinfo ]; then
                _model="$(grep -m1 -E '^model name|^Model|^cpu[[:space:]]+:' /proc/cpuinfo 2>/dev/null | cut -d: -f2- | sed 's/^ *//')"
            fi
            ;;
        macos)   _model="$(sysctl -n machdep.cpu.brand_string 2>/dev/null || true)" ;;
        windows) _model="$(wmic cpu get name 2>/dev/null | sed -n 2p | tr -d '\r' | sed 's/ *$//' || true)" ;;
    esac
    [ -n "$_model" ] || _model="unknown CPU"
    printf '%s (%s logical cores, building with -j%s)' "$_model" "$(nproc 2>/dev/null || echo '?')" "$jobs"
}

# Translation units in the tree about to be compiled. A real count of real files rather than a remembered
# constant: it moves with the engine version and with which modules are enabled.
#
# It UNDERCOUNTS, knowably. Godot generates a large amount of C++ during the build — the mono glue, shader
# and doc data, *.gen.cpp — none of which exists to be counted before the build creates it. So an estimate
# derived from this number is a floor, and the wording downstream says so. The recorded time from a real
# build has no such problem, which is the other reason it replaces this at the first opportunity.
count_tus() {
    _n=0
    for _ext in cpp cc c mm; do
        _c="$(find "$src" -type f -name "*.$_ext" -not -path '*/.git/*' 2>/dev/null | wc -l | tr -d ' ')"
        _n=$((_n + _c))
    done
    printf '%s' "$_n"
}

# Seconds to compile ONE representative C++ translation unit here, as a float, or nothing when there is no
# usable compiler to ask.
#
# "Representative" is load-bearing. A bare `int main(){}` measures process startup and would underestimate a
# Godot TU by an order of magnitude, so this pulls in the standard headers Godot leans on and instantiates
# templates over them at -O2, which is the shape of the real work. It is still a proxy, which is why the
# estimate says so out loud and why one real build replaces it permanently.
calibrate_cc() {
    _cxx="${CXX:-}"
    if [ -z "$_cxx" ]; then
        _cxx="$(command -v g++ 2>/dev/null || command -v clang++ 2>/dev/null || true)"
    fi
    [ -n "$_cxx" ] || return 1

    _tmp="$(mktemp -d 2>/dev/null)" || return 1
    cat > "$_tmp/cal.cpp" <<'CALIBRATION'
#include <algorithm>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>
template <typename T> struct Holder {
    std::vector<T> v;
    std::map<std::string, T> m;
    std::unordered_map<std::string, std::vector<T>> u;
    void fill(int n) {
        for (int i = 0; i < n; ++i) {
            v.push_back(T(i));
            m[std::to_string(i)] = T(i);
            u[std::to_string(i)].push_back(T(i));
        }
    }
    T total() const {
        T t{};
        for (const auto &x : v) { t += x; }
        std::for_each(v.begin(), v.end(), [&](const T &x) { t += x; });
        return t;
    }
};
template struct Holder<int>;
template struct Holder<double>;
template struct Holder<long long>;
int main() { Holder<int> a; a.fill(8); return int(a.total()); }
CALIBRATION

    _t0="$(date +%s%N 2>/dev/null)" || { rm -rf "$_tmp"; return 1; }
    if ! "$_cxx" -std=c++17 -O2 -c "$_tmp/cal.cpp" -o "$_tmp/cal.o" >/dev/null 2>&1; then
        rm -rf "$_tmp"
        return 1
    fi
    _t1="$(date +%s%N)"
    rm -rf "$_tmp"
    awk -v a="$_t0" -v b="$_t1" 'BEGIN { printf "%.2f", (b - a) / 1000000000 }'
}

history_key() { printf '%s-%s-%s' "$platform" "$arch" "$1"; }

# Prints "<seconds> <jobs> <tu>" for a build of this shape recorded here before, or fails.
history_get() {
    [ -f "$HISTORY" ] || return 1
    "$VX_PY" -c "
import json, sys
try:
    with open(sys.argv[1], encoding='utf-8') as fh:
        h = json.load(fh)
except Exception:
    sys.exit(1)
e = h.get(sys.argv[2])
if not e:
    sys.exit(1)
print(int(e['seconds']), e.get('jobs', 0), e.get('tu', 0))
" "$HISTORY" "$1" 2>/dev/null
}

history_put() {
    mkdir -p "$(dirname "$HISTORY")"
    "$VX_PY" -c "
import json, os, sys
path, key, seconds, jobs, tu = sys.argv[1:6]
h = {}
if os.path.exists(path):
    try:
        with open(path, encoding='utf-8') as fh:
            h = json.load(fh)
    except Exception:
        h = {}
h[key] = {'seconds': int(seconds), 'jobs': int(jobs), 'tu': int(tu)}
with open(path, 'w', encoding='utf-8') as fh:
    json.dump(h, fh, indent=2, sort_keys=True)
" "$HISTORY" "$1" "$2" "$3" "$4" 2>/dev/null || true
}

# The estimate for one scons target: from history when there is any, from calibration when there is not.
# Both paths name their basis, because both can be wrong and the reader needs to know which one to distrust.
estimate_for() {
    _target="$1" _tu="$2" _percore="$3"
    if _prev="$(history_get "$(history_key "$_target")")"; then
        set -- $_prev
        _secs="$1" _pjobs="$2"
        if [ "${_pjobs:-0}" -gt 0 ] && [ "$_pjobs" -ne "$jobs" ]; then
            # Scaled for a changed job count. Compile time is near-linear in cores until memory bandwidth
            # caps it, so this is right for a small change and optimistic for a large one ─ which is why the
            # previous run's -j is printed rather than hidden inside the number.
            _scaled="$(awk -v s="$_secs" -v p="$_pjobs" -v j="$jobs" 'BEGIN { printf "%d", s * p / j }')"
            printf '~%s  (took %s here at -j%s, scaled to -j%s)' \
                "$(fmt_duration "$_scaled")" "$(fmt_duration "$_secs")" "$_pjobs" "$jobs"
        else
            printf '~%s  (what it took here last time, same -j%s)' "$(fmt_duration "$_secs")" "$jobs"
        fi
        return 0
    fi

    if [ -z "$_percore" ]; then
        printf 'unknown  (nothing recorded here yet, and no compiler to calibrate against)'
        return 0
    fi
    _secs="$(awk -v tu="$_tu" -v pc="$_percore" -v j="$jobs" 'BEGIN { printf "%d", tu * pc / j }')"
    printf '~%s  (>=%s files x %ss measured here / %s jobs; a FLOOR: excludes linking and the C++ the build generates)' \
        "$(fmt_duration "$_secs")" "$_tu" "$_percore" "$jobs"
}

run() {
    if $dry_run; then
        printf '\033[2m$ %s\033[0m\n' "$*"
    else
        "$@"
    fi
}

# run(), but from another directory. A plain `( cd "$src" && run ... )` cannot be used for this:
# under --dry-run the source tree may not exist yet, and the cd would fail before printing the
# command the dry run exists to show.
run_in() {
    _dir="$1"; shift
    if $dry_run; then
        printf '\033[2m$ (cd %s && %s)\033[0m\n' "$_dir" "$*"
    else
        ( cd "$_dir" && "$@" )
    fi
}

# ── plan ─────────────────────────────────────────────────────────────────────────────────────────
cat <<EOF

  engine      Godot ${ENGINE_VERSION} (${ENGINE_TAG})
  platform    ${platform}
  arch        ${arch}
  target      ${target}
  source      ${src}
  jobs        -j${jobs}
  patches     $($apply_patches && echo "tools/engine-patches/*.patch" || echo "NONE (--no-patches: stock build, not shippable)")
  install     $($install && echo "yes — .godot-bin/ and tools/engine-templates/" || echo "no (pass --install)")
EOF

if $dry_run; then info "dry run: nothing below is executed"; fi

# ── 0. tools ─────────────────────────────────────────────────────────────────────────────────────
step "checking the toolchain"
command -v git >/dev/null 2>&1 || die "git is not on PATH."
if command -v scons >/dev/null 2>&1; then
    SCONS=(scons)
elif "$VX_PY" -c "import SCons" >/dev/null 2>&1; then
    SCONS=("$VX_PY" -m SCons)
else
    die "scons is not installed. Install it with:  $VX_PY -m pip install scons"
fi
command -v dotnet >/dev/null 2>&1 \
    || die ".NET SDK is not on PATH. A module_mono_enabled build needs it to compile the C# assemblies."
info "git $(git --version | awk '{print $3}')  |  scons ${SCONS[*]}  |  dotnet $(dotnet --version)"

# ── 1. the patch set matches what the lockfile pins ───────────────────────────────────────────────
# Seconds, and it fails before the multi-hour part. A silent edit to a patch file would otherwise
# produce a binary nobody can account for.
if $apply_patches; then
    step "verifying the patch set against engine.lock.json"
    run "$VX_PY" "$ROOT/tools/verify-engine-template.py" --patches
else
    warn "--no-patches: building STOCK. Do not ship this, and do not pin it in engine.lock.json."
fi

# ── 2. the source tree ───────────────────────────────────────────────────────────────────────────
step "godot source at $ENGINE_TAG"
if [ -d "$src/.git" ]; then
    info "reusing the existing clone at $src"
    have="$(git -C "$src" describe --tags --always 2>/dev/null || echo unknown)"
    # A clone at the WRONG tag is the failure that wastes the most time: it builds fine and produces a
    # binary of a different engine version, which the export then rejects at runtime with an error that
    # says nothing about tags. Refuse rather than guess, because checking out over someone's working
    # tree is not this script's call to make.
    if ! git -C "$src" merge-base --is-ancestor "$ENGINE_TAG" HEAD 2>/dev/null \
       && [ "$have" != "$ENGINE_TAG" ]; then
        warn "the clone reports '$have' but the lockfile pins '$ENGINE_TAG'."
        warn "Check it out yourself, or pass --src elsewhere:"
        warn "    git -C '$src' fetch --depth 1 origin tag $ENGINE_TAG && git -C '$src' checkout $ENGINE_TAG"
        die "refusing to build an engine version the lockfile does not pin"
    fi
elif [ -e "$src" ]; then
    die "$src exists but is not a git clone. Move it aside, or pass --src elsewhere."
else
    info "cloning godotengine/godot @ $ENGINE_TAG into $src (shallow, ~200 MB)"
    run git clone --depth 1 --branch "$ENGINE_TAG" https://github.com/godotengine/godot.git "$src"
fi

# ── 3. patches, idempotently ─────────────────────────────────────────────────────────────────────
# `git apply --reverse --check` succeeding means the patch is ALREADY in the tree, so a re-run is a
# no-op instead of a confusing "patch does not apply". That matters because this script is expected to
# be re-run — a template build after an editor build, a resumed build after a failure.
if $apply_patches; then
    step "applying the Vortex patches"
    shopt -s nullglob
    patches=("$PATCH_DIR"/*.patch)
    shopt -u nullglob
    [ ${#patches[@]} -gt 0 ] || die "no patches in $PATCH_DIR — a stock engine needs no custom build at all."
    for p in "${patches[@]}"; do
        name="$(basename "$p")"
        if $dry_run; then
            printf '\033[2m$ git -C %s apply %s\033[0m\n' "$src" "$p"
            continue
        fi
        if git -C "$src" apply --reverse --check "$p" >/dev/null 2>&1; then
            info "$name — already applied, skipping"
        elif git -C "$src" apply --check "$p" >/dev/null 2>&1; then
            git -C "$src" apply "$p"
            info "$name — applied"
        else
            error "$name does not apply to $ENGINE_TAG, and is not already applied."
            error "If the engine tag moved, the patch needs rebasing onto it first."
            error "    cd '$src' && git apply --verbose '$p'      # to see which hunk fails"
            exit 1
        fi
    done
    $dry_run || git -C "$src" --no-pager diff --stat | tail -5
fi

# ── 4. preflight ─────────────────────────────────────────────────────────────────────────────────
# `--help` makes Godot's SConstruct configure fully, print its options and exit 0; a malformed argument
# line exits non-zero with the reason. One minute here beats discovering a typo fifty minutes in.
step "sizing the job"
info "$(cpu_summary)"

TU=0
PERCORE=""
if [ -d "$src" ]; then
    TU="$(count_tus)"
    info "$TU source files to compile in $src"

    # Free space on the volume the build writes into. scons builds IN TREE, so the objects land beside the
    # sources and this is the number that matters. No threshold is asserted here: what a build of this
    # engine actually consumes has never been measured on this project, and inventing a limit would either
    # block a machine that would have worked or wave through one that will not.
    if command -v df >/dev/null 2>&1; then
        info "disk: $(df -h "$src" 2>/dev/null | awk 'NR==2 {print $4 " free on " $NF}')  (objects go into the source tree)"
    fi

    if ! PERCORE="$(calibrate_cc)"; then
        PERCORE=""
        warn "no g++/clang++ found to calibrate against; set \$CXX if your compiler is elsewhere"
    fi

    printf '\n'
    printf '  editor    %s\n' "$(estimate_for editor "$TU" "$PERCORE")"
    if [ "$target" = "template" ] || [ "$target" = "both" ]; then
        printf '  template  %s\n' "$(estimate_for template_release "$TU" "$PERCORE")"
    fi
    printf '\n'
    info "estimates. The REAL time is recorded in _scratch/build-engine-history.json when this finishes, and"
    info "every later build on this machine is estimated from that instead of from a calibration."
else
    info "no source tree yet, so there is nothing to size (dry run)"
fi

step "preflighting the scons argument line"
run "${SCONS[@]}" --directory="$src" "platform=$platform" target=editor "arch=$arch" \
    "${SCONS_COMMON[@]}" ${SCONS_EXTRA[@]+"${SCONS_EXTRA[@]}"} "-j$jobs" --help >/dev/null
info "scons accepts the argument line"

# ── 5-7. editor, glue, assemblies ────────────────────────────────────────────────────────────────
# The editor is built even when only a template was asked for, because the glue cannot be generated
# without running one. It is not shipped.
step "building the editor (required to generate the C# glue)"
_editor_t0="$(date +%s)"
run "${SCONS[@]}" --directory="$src" "platform=$platform" target=editor "arch=$arch" \
    "${SCONS_COMMON[@]}" ${SCONS_EXTRA[@]+"${SCONS_EXTRA[@]}"} "-j$jobs"

if ! $dry_run; then
    _editor_secs=$(( $(date +%s) - _editor_t0 ))
    info "editor built in $(fmt_duration "$_editor_secs")"
    history_put "$(history_key editor)" "$_editor_secs" "$jobs" "$TU"
fi

if ! $dry_run && [ ! -f "$src/$EDITOR_BIN" ]; then
    error "the editor build reported success but $EDITOR_BIN is not there."
    error "Most likely the arch name in the filename differs from what was passed (--arch $arch)."
    error "Look in $src/bin/ and pass the arch that appears there."
    exit 1
fi

# Both run with the SOURCE ROOT as the working directory, matching the workflow's
# `working-directory: godot`. build_assemblies.py resolves its output paths relative to the cwd, so
# running it from elsewhere writes the assemblies into the wrong tree — silently — and the template
# build then embeds nothing.
step "generating the C# glue"
run_in "$src" "./$EDITOR_BIN" --headless --generate-mono-glue ./modules/mono/glue

step "building the .NET assemblies"
run_in "$src" "$VX_PY" ./modules/mono/build_scripts/build_assemblies.py \
    --godot-output-dir=./bin --godot-platform="$platform"

# ── 8. the export template ───────────────────────────────────────────────────────────────────────
if [ "$target" = "template" ] || [ "$target" = "both" ]; then
    step "building the release export template"
    _tpl_t0="$(date +%s)"
    run "${SCONS[@]}" --directory="$src" "platform=$platform" target=template_release "arch=$arch" \
        "${SCONS_COMMON[@]}" ${SCONS_EXTRA[@]+"${SCONS_EXTRA[@]}"} "-j$jobs"
    if ! $dry_run; then
        _tpl_secs=$(( $(date +%s) - _tpl_t0 ))
        info "template built in $(fmt_duration "$_tpl_secs")"
        history_put "$(history_key template_release)" "$_tpl_secs" "$jobs" "$TU"
    fi
fi

if $dry_run; then
    info "dry run complete — nothing was built"
    exit 0
fi

# ── results ──────────────────────────────────────────────────────────────────────────────────────
step "results"
hash_of() { "$VX_PY" -c "import hashlib,sys;print(hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest())" "$1"; }
size_of() { "$VX_PY" -c "import os,sys;print(os.path.getsize(sys.argv[1]))" "$1"; }

built_editor="$src/$EDITOR_BIN"
built_template="$src/$TEMPLATE_BIN"

if [ -f "$built_editor" ]; then
    printf '  editor    %s\n            %s bytes\n' "$built_editor" "$(size_of "$built_editor")"
fi

if [ -f "$built_template" ]; then
    t_sha="$(hash_of "$built_template")"
    t_size="$(size_of "$built_template")"
    printf '  template  %s\n            %s bytes  sha256 %s\n' "$built_template" "$t_size" "$t_sha"

    # The same snippet the workflow writes to its run summary. A template is only usable by this tree
    # once it is PINNED, and a pin needs these three values; printing them here is what stops the last
    # step being "now go and work out the hash yourself".
    cat <<EOF

  To pin this template, add to engine.lock.json → template.platforms:

    "${platform}-${arch}": {
      "filename": "$(basename "$built_template")",
      "url": "<publish it, then put the download URL here>",
      "sha256": "$t_sha",
      "bytes": $t_size,
      "template_form": "$([ "$platform" = windows ] && echo pe || echo elf)",
      "presets": [],
      "patched": $($apply_patches && echo true || echo false)
    }

  A LOCAL build has no url, and engine.lock.json's fetcher treats a null url as a hard error rather
  than guessing one. Until it is published, point the preset's custom_template/release straight at
  the installed copy under tools/engine-templates/ and declare the preset in unpinned_presets with
  that as the reason — see tools/engine-patches/README.md.
EOF
fi

# ── install ──────────────────────────────────────────────────────────────────────────────────────
if $install; then
    step "installing"
    if [ -f "$built_editor" ]; then
        # The names find-godot.sh probes, so a clone can pin its own engine without touching the machine.
        mkdir -p "$ROOT/.godot-bin"
        if [ "$platform" = "windows" ]; then
            cp -f "$built_editor" "$ROOT/.godot-bin/godot.exe"
            # find-godot.sh PREFERS the console build: the plain .exe detaches from the terminal, so
            # GD.Print and errors never reach a captured stdout, which every headless use here needs.
            if [ -f "${built_editor%.exe}.console.exe" ]; then
                cp -f "${built_editor%.exe}.console.exe" "$ROOT/.godot-bin/godot_console.exe"
            fi
            info "editor → .godot-bin/godot.exe"
        else
            cp -f "$built_editor" "$ROOT/.godot-bin/godot"
            chmod +x "$ROOT/.godot-bin/godot"
            info "editor → .godot-bin/godot"
        fi

        # THE BINARY ALONE IS NOT AN EDITOR. Godot resolves its C# API and GodotTools assemblies from
        # <exe dir>/GodotSharp/, which build_assemblies.py wrote into the source tree's bin/ back in step 7.
        # Without them the editor starts, opens the project, and dies at the first thing that needs C#:
        #
        #     ERROR: .NET: Assemblies not found   at: initialize (modules/mono/mono_gd/gd_mono.cpp:647)
        #     handle_crash: Program crashed with signal 11
        #
        # Reported from ppc64le on 2026-08-20, where this install step had put a perfectly good editor next
        # to nothing at all and the export it was asked to run aborted. The official archives ship this
        # directory beside the executable; an install that omits it is simply incomplete.
        if [ -d "$src/bin/GodotSharp" ]; then
            rm -rf "$ROOT/.godot-bin/GodotSharp"
            cp -R "$src/bin/GodotSharp" "$ROOT/.godot-bin/GodotSharp"
            info "assemblies → .godot-bin/GodotSharp/"
        else
            error "$src/bin/GodotSharp is missing, so the installed editor cannot run C#."
            error "It is produced by build_assemblies.py (step 7). Re-run without --target template,"
            error "or run that step by hand, before using this editor."
        fi
    fi
    if [ -f "$built_template" ]; then
        mkdir -p "$ROOT/tools/engine-templates"
        cp -f "$built_template" "$ROOT/tools/engine-templates/$(basename "$built_template")"
        info "template → tools/engine-templates/$(basename "$built_template")"
    fi
fi

step "done"
cat <<EOF
  Next:  ./vx doctor        confirm the tree now sees an engine
         ./vx build         build the game's C#
         ./vx test          run the suite
EOF

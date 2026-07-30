#!/usr/bin/env bash
# Migrate a pre-restructure branch onto the VortexArena structure (restructure item 28e, G8).
#
# Applies T1-T5 from docs/BRANCH-MIGRATION.md mechanically, so that a subsequent `git merge main` has
# only the branch's SEMANTIC changes left to resolve. Read that document first; this script is the
# executable half of it, not a substitute.
#
#   git checkout my-branch
#   git checkout -b my-branch-migrated     # keep the original until the merge is done
#   bash tools/migrate-branch.sh
#   git commit -am "chore: mechanical migration to the VortexArena structure"
#   git merge main
#
# Deliberately dumb and re-runnable. Running it twice is a no-op, so it is safe to re-run after a
# partial failure rather than needing a clean checkout.
#
# WHY TRANSFORM BEFORE MERGING, rather than rebasing: a rebase replays each commit onto the moved tree
# one at a time, so every commit touching a renamed file conflicts on the rename — a 25-commit branch
# means resolving the same path conflict 25 times. And merging WITHOUT the transform makes git infer the
# rename, which is heuristic, capped, and degrades exactly where the branch also edited the file. The
# transform turns "infer a rename and merge the edits" into "merge the edits".
#
# It does NOT touch data/. Content arrives from main in the merge.

set -euo pipefail

# Two different roots, on purpose.
#
# ROOT is the git toplevel of the CURRENT DIRECTORY — the tree being migrated. HELPERS is where this
# script lives. They differ in the normal case: a pre-restructure branch does not contain this script
# (it postdates the branch), so you run main's copy against a worktree checked out at the branch:
#
#   git worktree add ../wt-mybranch mybranch
#   cd ../wt-mybranch && git checkout -b mybranch-migrated
#   bash /path/to/main/tools/migrate-branch.sh
#
# Deriving ROOT from the script's location instead would have silently migrated MAIN.
HELPERS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(git rev-parse --show-toplevel 2>/dev/null || true)"
if [ -z "$ROOT" ]; then
    printf 'ERROR: not inside a git work tree. cd to the checkout you want to migrate first.
' >&2
    exit 1
fi
cd "$ROOT"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$*"; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

check_only=false
[ "${1:-}" = "--check" ] && check_only=true

# ── guards ────────────────────────────────────────────────────────────────────
branch="$(git rev-parse --abbrev-ref HEAD)"
if [ "$branch" = "main" ] && ! $check_only; then
    error "refusing to run on main — main IS the target structure."
    error "  Check out the branch you want to migrate first (and a -migrated copy of it)."
    exit 1
fi
if ! $check_only && ! git diff --quiet; then
    error "working tree is dirty. Commit or stash first, so the migration is one reviewable diff."
    exit 1
fi

# ── the file set ──────────────────────────────────────────────────────────────
# The content rewrite is restricted to files THE BRANCH ACTUALLY CHANGED, and that restriction is the
# difference between this script helping and hurting.
#
# Measured on fix/warpzone-view-smoothing: rewriting the whole tree produced 67 merge conflicts, of which
# only 12 were the branch's real overlapping edits — the other 55 were in files the branch never touched.
# The transform CREATED them. Wherever main did more than a mechanical rename (replacing a hardcoded
# assets/data literal with TestPaths.Data, say), a blanket sed produces a third version that agrees with
# neither side, and git has to ask.
#
# For a file the branch did not touch, doing nothing is strictly better: the branch side is unmodified
# relative to the merge base, so main's version wins outright with no conflict at all. The T2 directory
# move still applies to the WHOLE tree — that is what stops git having to infer renames — but the
# per-file content rewrite does not.
#
# Also excluded regardless:
#   data/                     content arrives from main in the merge; rewriting it would conflict
#                             thousands of binaries.
#   docs/BRANCH-MIGRATION.md  its whole job is recording OLD -> NEW mappings. A rename sweep turns
#                             `XonoticGodot.* -> VortexArena.*` into `VortexArena.* -> VortexArena.*`,
#                             mapping a name to itself. Not hypothetical: the stage 5 sweep on main did
#                             exactly this and it had to be recovered from git history.
#   planning/                 dated records of what was true when written; rewriting them falsifies them.
TARGET_BRANCH="${MIGRATE_ONTO:-main}"

branch_touched_files() {
    local base
    base="$(git merge-base HEAD "$TARGET_BRANCH" 2>/dev/null || true)"
    if [ -z "$base" ]; then
        error "no merge base with '$TARGET_BRANCH'. Set MIGRATE_ONTO to the branch you will merge."
        return 1
    fi
    # Paths come from the diff and are mapped through the T2 rename, because this runs AFTER the move.
    # The trailing `|| true` is load-bearing: without it the final `[ -f ]` test can leave a non-zero
    # status, `set -e` sees it through the `$( )` in the caller, and the script exits SILENTLY after the
    # directory moves — having renamed everything and rewritten nothing. It did exactly that once, and
    # the resulting low conflict count looked like success.
    git diff --name-only "$base" HEAD       | sed 's|^src/XonoticGodot\.|src/VortexArena.|; s|^tests/XonoticGodot\.Tests|tests/VortexArena.Tests|'       | grep -vE '^(data/|docs/BRANCH-MIGRATION\.md$|planning/)'       | { while IFS= read -r f; do [ -f "$f" ] && printf '%s\0' "$f"; done; true; }
}

# Every migratable file, for the leftovers report — which should look at the whole tree, not just what
# the branch touched, so an unclassified spelling anywhere is still surfaced.
migratable_files() {
    git ls-files -z       ':!data/**'       ':!docs/BRANCH-MIGRATION.md'       ':!planning/**'
}

# ── T2: move the project directories and solution files ───────────────────────
move_projects() {
    local moved=0
    for p in Common Engine Formats Net Server SourceGen; do
        if [ -d "src/XonoticGodot.$p" ]; then
            $check_only || git mv "src/XonoticGodot.$p" "src/VortexArena.$p"
            moved=$((moved + 1))
        fi
        if [ -f "src/VortexArena.$p/XonoticGodot.$p.csproj" ]; then
            $check_only || git mv "src/VortexArena.$p/XonoticGodot.$p.csproj" "src/VortexArena.$p/VortexArena.$p.csproj"
            moved=$((moved + 1))
        fi
    done
    if [ -d tests/XonoticGodot.Tests ]; then
        $check_only || git mv tests/XonoticGodot.Tests tests/VortexArena.Tests
        moved=$((moved + 1))
    fi
    if [ -f tests/VortexArena.Tests/XonoticGodot.Tests.csproj ]; then
        $check_only || git mv tests/VortexArena.Tests/XonoticGodot.Tests.csproj tests/VortexArena.Tests/VortexArena.Tests.csproj
        moved=$((moved + 1))
    fi
    for f in XonoticGodot.sln XonoticGodot.csproj; do
        if [ -f "$f" ]; then
            $check_only || git mv "$f" "VortexArena.${f#XonoticGodot.}"
            moved=$((moved + 1))
        fi
    done
    echo "$moved"
}

# ── T1/T3/T4/T5: the content rewrites ─────────────────────────────────────────
# Order matters in exactly one place, flagged below.
rewrite_contents() {
    # The rules live in tools/migrate_branch_rewrite.py — they need a per-rule regex-vs-literal flag,
    # and that is unreadable when it is also fighting two levels of shell quoting.
    branch_touched_files | python3 "$HELPERS/migrate_branch_rewrite.py"
}

# ── report anything the rewrite could not classify ────────────────────────────
# Scoped to the files that were IN SCOPE, not the whole tree. Under the restricted design the rest of
# the tree is EXPECTED to still carry old spellings — those files take main's version in the merge, and
# reporting them would make this warning fire on every single run. A warning that always fires is one
# people learn to scroll past, which costs more than it saves.
report_leftovers() {
    local left
    left=$(branch_touched_files | xargs -0 grep -IlE 'XonoticGodot|xonoticgodot|XG_[A-Z_]+|Xg[A-Z]|assets/data|XONOTIC_USERDIR|bryankruman' 2>/dev/null || true)
    if [ -n "$left" ]; then
        warn "these branch-touched files still carry pre-restructure spellings:"
        printf '%s
' "$left" | sed 's/^/    /'
        warn "Classify them by hand. The script prints rather than guesses on purpose — a wrong"
        warn "automatic rewrite is harder to notice than one that was left alone."
        return 1
    fi
    return 0
}

# ── run ───────────────────────────────────────────────────────────────────────
if $check_only; then
    info "--check: reporting only, changing nothing (branch: $branch)"
    report_leftovers && info "nothing left to migrate" || true
    exit 0
fi

info "migrating branch '$branch' to the VortexArena structure"
moved=$(move_projects)
info "T2: moved $moved path(s)"
candidates=$(branch_touched_files | python3 -c "import sys;print(sum(1 for x in sys.stdin.buffer.read().split(b'\0') if x))")
info "branch-touched files in scope: $candidates"
if [ "$candidates" -eq 0 ]; then
    error "no files in scope. A migration that renames directories and rewrites NOTHING looks like it"
    error "worked - the merge conflict count even goes DOWN - so this is a hard failure, not a warning."
    exit 1
fi
touched=$(rewrite_contents)
info "T1/T3/T4/T5: rewrote $touched file(s)"

if report_leftovers; then
    info "no unclassified leftovers"
fi

cat <<'NEXT'

Next:
  git commit -am "chore: mechanical migration to the VortexArena structure"
  git merge main

Then read the "After the merge" section of docs/BRANCH-MIGRATION.md — the merge is where the branch's
real changes surface, and a few of them need judgement rather than a script.
NEXT

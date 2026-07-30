#!/usr/bin/env bash
# Resolve the merge conflicts that are artefacts of the restructure rather than real disagreements.
#
# Run INSIDE a conflicted merge, after `tools/migrate-branch.sh` + `git merge main`:
#
#   bash tools/resolve-mechanical-conflicts.sh
#   git diff --name-only --diff-filter=U      # what is left is genuinely yours to resolve
#
# ── The rule, and why it is safe ──────────────────────────────────────────────
# For any conflicted file THE BRANCH NEVER EDITED, take main's version.
#
# That is not a heuristic. If the branch made no change to a file between the merge base and its tip,
# then it has no opinion about that file's contents, and main's version is by definition the correct
# one. The conflict exists only because the restructure moved the file.
#
# ── Why those conflicts happen at all, which is not obvious ───────────────────
# The Tier-1 rename changed the namespace on nearly every line of many files. Git's rename detection is
# similarity-based, so for a file like GameInit.cs — where `XonoticGodot.` appears on most lines — the
# pre- and post-rename versions fall BELOW the similarity threshold and git cannot pair them. It then
# sees main deleting the old path and adding a new one, while the branch's own `git mv` also adds that
# path, and reports add/add (`AA`).
#
# Raising merge.renameLimit does not help; that governs how many candidates git will consider, not how
# similar they must be. Measured: 999999 changed nothing.
#
# ── What this deliberately does NOT touch ─────────────────────────────────────
# Any file the branch DID edit. Those conflicts are the branch's real overlap with main and need someone
# who knows what the branch was for. Silently taking one side there would discard work.

set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

if ! git rev-parse -q --verify MERGE_HEAD >/dev/null; then
    error "no merge in progress. Run this between \`git merge main\` and the merge commit."
    exit 1
fi

TARGET_BRANCH="${MIGRATE_ONTO:-main}"

# The branch's own commits: merge base -> the commit before the mechanical migration commit, which is
# HEAD^ of the pre-merge tip. Using the migration commit's parent keeps the script's own rewrites out of
# "what the branch edited" — otherwise every migrated file would look branch-owned and nothing would
# resolve.
migration_commit="$(git rev-parse HEAD)"
branch_tip="$(git rev-parse "${migration_commit}^" 2>/dev/null || true)"
if [ -z "$branch_tip" ]; then
    error "cannot find the commit before the migration commit."
    exit 1
fi
base="$(git merge-base "$branch_tip" "$TARGET_BRANCH")"

branch_edited="$(git diff --name-only "$base" "$branch_tip" \
    | sed 's|^src/XonoticGodot\.|src/VortexArena.|; s|^tests/XonoticGodot\.Tests|tests/VortexArena.Tests|' \
    | sort -u)"

resolved=0
kept=0
while IFS= read -r f; do
    [ -n "$f" ] || continue
    if printf '%s\n' "$branch_edited" | grep -qxF "$f"; then
        kept=$((kept + 1))
        continue
    fi
    # main's side. `--theirs` during a merge means MERGE_HEAD, i.e. main.
    if git checkout --theirs -- "$f" 2>/dev/null; then
        git add -- "$f"
    else
        # main deleted it (UD): honour the deletion.
        git rm -q --force -- "$f" 2>/dev/null || true
    fi
    resolved=$((resolved + 1))
done < <(git diff --name-only --diff-filter=U)

info "resolved $resolved mechanical conflict(s) by taking $TARGET_BRANCH's version"
info "left $kept conflict(s) that the branch actually edited — those are yours"

if [ "$kept" -gt 0 ]; then
    git diff --name-only --diff-filter=U | sed 's/^/    /'
fi

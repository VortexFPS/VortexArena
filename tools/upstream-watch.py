#!/usr/bin/env python3
"""Upstream contribution harvester for Vortex Arena (XonoticGodot).

Surfaces new upstream Xonotic work worth triaging: every commit that has landed
on the `master` branch of the two source repositories since a cutoff date, plus
every branch that is *ahead of* master (a proposed / in-flight contribution). It
fetches the repos, enumerates candidates, cross-references what has already been
triaged in planning/upstream-watch/LEDGER.yaml, and writes a dated worklist of only
the UN-triaged items for the weekly analysis pass to consume.

It makes NO judgement calls. It is the mechanical half of the process documented
in planning/upstream-watch/README.md -- the analysis (portability, quality,
roadmap fit, the port/reject decision) is done in the workflow/agent pass that
reads the worklist this script emits.

Sources (see the README for the pinned-reference model):
  data = Base/data/xonotic-data.pk3dir  (gitlab.com/xonotic/xonotic-data.pk3dir) -- QC gameplay + data
  dp   = Base/darkplaces                 (gitlab.com/xonotic/darkplaces)          -- the reference engine

Open merge requests are pulled from the GitLab REST API when reachable (the best
"open contribution" signal: title, author, MR url). When offline the harvester
falls back to plain ahead-of-master branch enumeration and says so in the output.

Nothing is dropped silently: high-noise commits (Transifex/translation autosync,
pure CI/build chores) are FLAGGED low-priority in the worklist, not filtered out.

Usage:
  python tools/upstream-watch.py                     # fetch both repos, write worklist
  python tools/upstream-watch.py --since 2026-06-01  # override cutoff (default below)
  python tools/upstream-watch.py --no-fetch          # use local refs as-is (offline)
  python tools/upstream-watch.py --repo data         # one repo only (data|dp|all)
  python tools/upstream-watch.py --no-mr             # skip the GitLab MR API call
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import date, datetime, timedelta
from pathlib import Path
from urllib.parse import quote

DEFAULT_SINCE = "2026-06-01"
# Branches older than this (and without an open MR) are counted but not emitted as candidates:
# the repos carry 400+ branches going back a decade, almost all abandoned experiments. An open
# MR or recent activity is the real "open contribution" signal. Override with --branch-since / --all-branches.
DEFAULT_BRANCH_LOOKBACK_DAYS = 365

# tools/ lives in the port repo; Base is a sibling of the port repo root.
PORT_ROOT = Path(__file__).resolve().parents[1]
BASE_DIR = Path(os.environ.get("XG_BASE_DIR", PORT_ROOT.parent / "Base"))

WATCH_DIR = PORT_ROOT / "planning" / "upstream-watch"
LEDGER = WATCH_DIR / "LEDGER.yaml"
INBOX = WATCH_DIR / "_inbox"

REPOS = {
    "data": {
        "path": BASE_DIR / "data" / "xonotic-data.pk3dir",
        "gl_project": "xonotic/xonotic-data.pk3dir",
        "branch": "master",
    },
    "dp": {
        "path": BASE_DIR / "darkplaces",
        "gl_project": "xonotic/darkplaces",
        "branch": "master",
    },
}

# Translation/i18n churn: dropped entirely (never emitted to the worklist) — pure noise.
TRANSLATION_RE = re.compile(
    r"(transifex|autosync|update translations?|\bl10n\b|\bi18n\b)", re.IGNORECASE,
)
# Other low-value chores: recorded but flagged low-priority (not dropped).
NOISE_RE = re.compile(
    r"(bump version|update credits|\bci:|\.gitlab-ci|update changelog)", re.IGNORECASE,
)
# Subjects/paths that should always be surfaced even if the engine is otherwise n/a.
SECURITY_RE = re.compile(
    r"(overflow|out.of.bounds|\boob\b|buffer|bounds check|sanitiz|\bcve\b|"
    r"use.after.free|heap|memory (leak|corrupt)|null deref|integer overflow)",
    re.IGNORECASE,
)


def git(repo: Path, *args: str, check: bool = True) -> str:
    """Run a git command in `repo` and return stdout (stripped)."""
    res = subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if check and res.returncode != 0:
        raise RuntimeError(
            f"git {' '.join(args)} failed in {repo}:\n{res.stderr.strip()}"
        )
    return res.stdout.strip()


def branch_refspec_is_narrow(repo: Path) -> str | None:
    """Return the configured fetch refspec if it CANNOT see branches other than master.

    A clone made with --single-branch (or a submodule init) carries
    `+refs/heads/master:refs/remotes/origin/master`. With that refspec the whole
    branch/MR half of the harvest silently reports "0 branches, 0 hidden" — which
    reads identically to "upstream has no open contributions". Never silent: detect
    it and tell the operator how to widen it.
    """
    res = subprocess.run(
        ["git", "-C", str(repo), "config", "--get-all", "remote.origin.fetch"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    specs = [s.strip() for s in res.stdout.splitlines() if s.strip()]
    if not specs:
        return None  # no remote configured; harvest_repo reports that separately
    if any("refs/heads/*" in s for s in specs):
        return None
    return "; ".join(specs)


def load_ledger_keys() -> set[str]:
    """Every dedup token already present in the ledger (commit shas + branch tips)."""
    if not LEDGER.exists():
        return set()
    text = LEDGER.read_text(encoding="utf-8", errors="replace")
    # Grab anything that looks like a sha (>=7 hex) or a repo:branch@tip token.
    return set(re.findall(r"[0-9a-f]{7,40}", text))


def already_seen(ledger_keys: set[str], sha: str) -> bool:
    """True if this sha (by any >=7-char prefix present in the ledger) was triaged."""
    return any(sha.startswith(k) or k.startswith(sha[:12]) for k in ledger_keys)


def fetch_open_mrs(project: str, timeout: int = 20) -> dict[str, dict]:
    """source_branch -> {title, author, url, updated, draft} for open MRs. {} on failure."""
    enc = quote(project, safe="")
    url = (
        f"https://gitlab.com/api/v4/projects/{enc}/merge_requests"
        "?state=opened&per_page=100&order_by=updated_at&sort=desc"
    )
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "upstream-watch"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, ValueError, OSError) as e:
        print(f"  ! GitLab MR API unreachable ({e}); using branch enumeration only.",
              file=sys.stderr)
        return {}
    out = {}
    for mr in data:
        out[mr.get("source_branch", "")] = {
            "iid": mr.get("iid"),
            "title": (mr.get("title") or "").strip(),
            "author": (mr.get("author") or {}).get("username", "?"),
            "url": mr.get("web_url", ""),
            "updated": (mr.get("updated_at") or "")[:10],
            "draft": bool(mr.get("draft") or mr.get("work_in_progress")),
        }
    return out


def harvest_repo(key: str, cfg: dict, since: str, branch_since: str, ledger_keys: set[str],
                 do_fetch: bool, do_mr: bool) -> dict:
    repo = cfg["path"]
    branch = cfg["branch"]
    if not (repo / ".git").exists():
        print(f"  ! {key}: repo not found at {repo}; skipping.", file=sys.stderr)
        return {"repo": key, "error": f"not found at {repo}",
                "master_commits": [], "branches": []}

    narrow = branch_refspec_is_narrow(repo)
    if narrow:
        print(f"  ! {key}: fetch refspec is branch-blind ({narrow}) — the branch/MR stream can only "
              f"see '{branch}'. Widen it with:\n"
              f"      git -C {repo} config --unset-all remote.origin.fetch\n"
              f"      git -C {repo} config --add remote.origin.fetch "
              f"'+refs/heads/*:refs/remotes/origin/*'\n"
              f"      git -C {repo} fetch --prune origin",
              file=sys.stderr)

    if do_fetch:
        print(f"  fetching {key} ({cfg['gl_project']}) …")
        git(repo, "fetch", "--prune", "--tags", "origin")

    ref = f"origin/{branch}"
    # Verify the remote ref exists (fall back to local branch if --no-fetch on a fresh clone).
    if git(repo, "rev-parse", "--verify", "--quiet", ref, check=False) == "":
        ref = branch

    # --- master commits since cutoff (first-parent = the merge stream, not every squashed leaf) ---
    fmt = "%H%x1f%an%x1f%cI%x1f%s"
    raw = git(repo, "log", f"--since={since}", "--first-parent", "--date=iso-strict",
              f"--pretty=format:{fmt}", ref)
    master = []
    translations_dropped = 0
    for line in filter(None, raw.splitlines()):
        sha, author, cdate, subject = (line.split("\x1f") + ["", "", "", ""])[:4]
        if already_seen(ledger_keys, sha):
            continue
        if TRANSLATION_RE.search(subject):      # translation/i18n churn — not worth triaging
            translations_dropped += 1
            continue
        paths = git(repo, "show", "--name-only", "--pretty=format:", sha, check=False)
        master.append({
            "source": f"{key}@{sha[:12]}",
            "sha": sha,
            "author": author,
            "date": cdate[:10],
            "subject": subject,
            "noise": bool(NOISE_RE.search(subject)),
            "security": bool(SECURITY_RE.search(subject) or SECURITY_RE.search(paths)),
            "files_touched": len([p for p in paths.splitlines() if p.strip()]),
        })

    # --- branches ahead of master (proposed / in-flight contributions) ---
    mrs = fetch_open_mrs(cfg["gl_project"]) if do_mr else {}
    branches = []
    stale_skipped = 0  # ahead-of-master but too old and no open MR — counted, not emitted
    ref_lines = git(
        repo, "for-each-ref", "--sort=-committerdate",
        "--format=%(refname:short)%09%(objectname)%09%(committerdate:short)%09%(authorname)",
        "refs/remotes/origin",
    )
    for line in filter(None, ref_lines.splitlines()):
        name, tip, cdate, author = (line.split("\t") + ["", "", "", ""])[:4]
        short = name[len("origin/"):] if name.startswith("origin/") else name
        if short in (branch, "HEAD") or short.endswith("/HEAD"):
            continue
        ahead = git(repo, "rev-list", "--count", f"{ref}..{name}", check=False)
        try:
            ahead_n = int(ahead)
        except ValueError:
            ahead_n = 0
        if ahead_n == 0:
            continue  # merged / not ahead of master
        src = f"{key}:{short}@{tip[:12]}"
        if already_seen(ledger_keys, tip):
            continue
        # Was this branch triaged at an OLDER tip? (name present, tip absent -> re-review.)
        name_token = f"{key}:{short}"
        updated = (LEDGER.exists()
                   and name_token in LEDGER.read_text(encoding="utf-8", errors="replace"))
        mr = mrs.get(short)
        # Recency gate: skip abandoned branches (old + no open MR + not a re-review).
        if not mr and not updated and cdate < branch_since:
            stale_skipped += 1
            continue
        branches.append({
            "source": src,
            "branch": short,
            "tip": tip[:12],
            "ahead": ahead_n,
            "last_commit": cdate,
            "author": author,
            "open_mr": bool(mr),
            "mr_iid": mr["iid"] if mr else None,
            "mr_title": mr["title"] if mr else "",
            "mr_url": mr["url"] if mr else "",
            "mr_draft": mr["draft"] if mr else None,
            "status": "updated" if updated else "new",
        })

    # Open MRs whose source branch isn't a local origin ref (fork MRs) — record so nothing's missed.
    local_branches = {b["branch"] for b in branches}
    for sbranch, mr in mrs.items():
        if sbranch and sbranch not in local_branches:
            token = f"{key}:{sbranch}"
            if LEDGER.exists() and token in LEDGER.read_text(encoding="utf-8", errors="replace"):
                continue
            branches.append({
                "source": f"{key}:{sbranch}@fork",
                "branch": sbranch,
                "tip": "fork",
                "ahead": None,
                "last_commit": mr["updated"],
                "author": mr["author"],
                "open_mr": True,
                "mr_iid": mr["iid"],
                "mr_title": mr["title"],
                "mr_url": mr["url"],
                "mr_draft": mr["draft"],
                "status": "fork (not fetched locally — fetch the MR ref to diff)",
            })

    return {"repo": key, "gl_project": cfg["gl_project"],
            "master_commits": master, "branches": branches, "mr_api": bool(mrs),
            "stale_skipped": stale_skipped, "branch_since": branch_since,
            "translations_dropped": translations_dropped, "narrow_refspec": narrow}


def write_worklist(results: list[dict], since: str, run_date: str) -> tuple[Path, Path]:
    INBOX.mkdir(parents=True, exist_ok=True)
    payload = {"generated": run_date, "since": since, "repos": results}
    json_path = INBOX / f"worklist-{run_date}.json"
    json_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    n_commits = sum(len(r["master_commits"]) for r in results)
    n_branches = sum(len(r["branches"]) for r in results)
    lines: list[str] = []
    lines.append(f"# Upstream Watch — worklist {run_date}")
    lines.append("")
    lines.append(f"Cutoff `--since {since}`. **{n_commits} new master commit(s)**, "
                 f"**{n_branches} branch/MR candidate(s)** not yet in the ledger.")
    lines.append("")
    lines.append("Next: run the analysis pass (`Workflow { name: \"upstream-watch\", "
                 f"args: {{ worklist: \"{json_path.as_posix().split('XonoticGodot/')[-1]}\" }} }}`) "
                 "or hand this file to Claude. See ../README.md §6.")
    lines.append("")

    for r in results:
        lines.append(f"## `{r['repo']}` — {r.get('gl_project', '')}")
        if r.get("error"):
            lines.append(f"\n> ⚠ {r['error']}\n")
            continue
        if not r.get("mr_api", True):
            lines.append("\n> ⚠ GitLab MR API not reached — open-MR signal missing; "
                         "branch list is ahead-of-master only.\n")
        if r.get("narrow_refspec"):
            lines.append(f"\n> ⚠ **Branch stream is blind** — this clone's fetch refspec is "
                         f"`{r['narrow_refspec']}`, so only `master` is mirrored locally and the "
                         "branches-ahead-of-master list below is NOT trustworthy (only fork MRs "
                         "surfaced via the GitLab API appear). Widen with "
                         "`git config --add remote.origin.fetch "
                         "'+refs/heads/*:refs/remotes/origin/*'` then re-fetch.\n")

        mc = r["master_commits"]
        td = r.get("translations_dropped", 0)
        drop_note = f" — {td} translation/i18n commit(s) dropped" if td else ""
        lines.append(f"\n### Master commits since {since} ({len(mc)}){drop_note}\n")
        if mc:
            lines.append("| source | date | author | files | flags | subject |")
            lines.append("|---|---|---|---|---|---|")
            for c in mc:
                flags = " ".join(f for f, on in
                                 [("🔒sec", c["security"]), ("noise", c["noise"])] if on) or "—"
                subj = c["subject"].replace("|", "\\|")[:90]
                lines.append(f"| `{c['source']}` | {c['date']} | {c['author']} "
                             f"| {c['files_touched']} | {flags} | {subj} |")
        else:
            lines.append("_none new_")

        br = r["branches"]
        skipped = r.get("stale_skipped", 0)
        suffix = (f" — plus {skipped} older branch(es) ahead of master hidden "
                  f"(last commit < {r.get('branch_since', '?')}, no open MR; use --all-branches)") if skipped else ""
        lines.append(f"\n### Branches ahead of master / open MRs ({len(br)}){suffix}\n")
        if br:
            lines.append("| source | ahead | last | author | MR | title/status |")
            lines.append("|---|---|---|---|---|---|")
            for b in br:
                mr = f"!{b['mr_iid']}" + (" (draft)" if b.get("mr_draft") else "") if b["open_mr"] else "—"
                title = (b["mr_title"] or b["status"]).replace("|", "\\|")[:80]
                ahead = b["ahead"] if b["ahead"] is not None else "?"
                lines.append(f"| `{b['source']}` | {ahead} | {b['last_commit']} "
                             f"| {b['author']} | {mr} | {title} |")
        else:
            lines.append("_none new_")
        lines.append("")

    md_path = INBOX / f"worklist-{run_date}.md"
    md_path.write_text("\n".join(lines), encoding="utf-8")
    return md_path, json_path


def main() -> int:
    # Windows consoles default to cp1252, which cannot encode the status glyphs below — and the
    # one that crashed was on the "nothing new" path, i.e. the quiet-week path we hit most often.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass  # already-wrapped or non-reconfigurable stream: fall through harmlessly

    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--since", default=DEFAULT_SINCE, help=f"master-commit cutoff (default {DEFAULT_SINCE})")
    ap.add_argument("--branch-since", default=None,
                    help=f"hide branches whose last commit predates this (default: {DEFAULT_BRANCH_LOOKBACK_DAYS}d ago). "
                         "Open MRs are always shown regardless.")
    ap.add_argument("--all-branches", action="store_true",
                    help="emit every branch ahead of master, however old (overrides --branch-since)")
    ap.add_argument("--repo", choices=["data", "dp", "all"], default="all")
    ap.add_argument("--no-fetch", action="store_true", help="don't fetch; use local refs")
    ap.add_argument("--no-mr", action="store_true", help="skip the GitLab MR API call")
    args = ap.parse_args()

    try:
        datetime.strptime(args.since, "%Y-%m-%d")
    except ValueError:
        print(f"--since must be YYYY-MM-DD, got {args.since!r}", file=sys.stderr)
        return 2

    if args.all_branches:
        branch_since = "0000-00-00"
    elif args.branch_since:
        branch_since = args.branch_since
    else:
        branch_since = (date.today() - timedelta(days=DEFAULT_BRANCH_LOOKBACK_DAYS)).isoformat()

    run_date = date.today().isoformat()
    ledger_keys = load_ledger_keys()
    keys = ["data", "dp"] if args.repo == "all" else [args.repo]

    print(f"upstream-watch: master since {args.since}, branches since {branch_since}, repos={keys}, "
          f"fetch={not args.no_fetch}, ledger has {len(ledger_keys)} known key(s)")
    results = []
    for k in keys:
        results.append(harvest_repo(k, REPOS[k], args.since, branch_since, ledger_keys,
                                    do_fetch=not args.no_fetch, do_mr=not args.no_mr))

    md_path, json_path = write_worklist(results, args.since, run_date)
    n_commits = sum(len(r["master_commits"]) for r in results)
    n_branches = sum(len(r["branches"]) for r in results)
    print(f"\nWrote {md_path.relative_to(PORT_ROOT)} "
          f"({n_commits} commit(s), {n_branches} branch/MR candidate(s)).")
    print(f"Wrote {json_path.relative_to(PORT_ROOT)} (for the analysis workflow).")
    if n_commits + n_branches == 0:
        print("Nothing new since the last triage. 🎉")
    return 0


if __name__ == "__main__":
    sys.exit(main())

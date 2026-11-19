#!/usr/bin/env python3
"""Base->registry coverage ledger for the parity system.

Answers the question the per-unit audits can't: **how much of Base does the registry even
look at?** Every drift pass has found "newly-unmapped Base features" because unit feature
enumeration was top-down; this tool makes the blind spot measurable and drives it to zero.

It enumerates every .qc/.qh under the Base qcsrc tree, extracts every `base_refs` file path
cited anywhere in planning/parity/registry/*.yaml, applies the scope declarations in
planning/parity/coverage-scope.yaml, and buckets every Base file as:

  cited     — at least one registry row cites it (the unit audit is the coverage claim)
  excluded  — deliberately out of parity scope, with a written rationale (coverage-scope.yaml)
  deferred  — known-uncovered, an audit is scheduled/pending (coverage-scope.yaml)
  UNMAPPED  — no registry citation or scope entry. Separate pinned source reviews can cover these paths.

Also lints STALE citations (a base_refs path that matches no Base file — typo or moved file).

Outputs:
  planning/parity/COVERAGE.md    human report (summary, per-dir table, largest unmapped files)
  planning/parity/_coverage.json machine artifact (unmapped/stale lists for audit-wave tooling)

Run after any registry change (parity-diff --update, new units) alongside parity-assemble.py:
    python tools/parity-coverage.py
    python tools/parity-coverage.py --base <path-to-qcsrc>   # override the Base tree location
"""
from __future__ import annotations
import argparse, csv, datetime, fnmatch, json, pathlib, re, sys
from parity_reference import PIN, QC_EXTENSIONS, verify_qc_inputs
try:
    import yaml
except ImportError:
    sys.exit("PyYAML required: pip install pyyaml")

ROOT = pathlib.Path(__file__).resolve().parent.parent
REG = ROOT / "planning" / "parity" / "registry"
OUT = ROOT / "planning" / "parity"
SCOPE_FILE = OUT / "coverage-scope.yaml"
DEFAULT_BASE = ROOT.parent / "Base" / "data" / "xonotic-data.pk3dir" / "qcsrc"


PATH_RE = re.compile(r"[\w][\w./-]*\.(?:qc|qh|inc)\b")


def enumerate_base(base: pathlib.Path) -> dict[str, int]:
    """relative-posix-path -> line count, for every QC source file under qcsrc."""
    files = {}
    for p in base.rglob("*"):
        if p.suffix.lower() in QC_EXTENSIONS and p.is_file():
            rel = p.relative_to(base).as_posix()
            with p.open("r", encoding="utf-8", errors="replace") as source:
                files[rel] = sum(1 for _ in source)
    return files


def extract_paths(ref: str) -> list[str]:
    """Pull every QC source path out of a base_refs string. Auditors write refs in several
    shapes — 'path:symbol', 'path (annotation)', 'pathA + pathB', 'path / other' — so extract
    all path-shaped tokens rather than assuming one clean 'path:symbol'."""
    if not isinstance(ref, str):
        return []
    out = []
    for m in PATH_RE.finditer(ref.replace("\\", "/")):
        path = re.sub(r"^\.?/?(qcsrc/)?", "", m.group(0))
        if "/" in path:
            out.append(path)
    return out


def collect_citations() -> tuple[dict[str, set[str]], list[str]]:
    """Match explicit path:symbol anchors. Free-text descriptions still count as file citations."""
    cited: dict[str, set[str]] = {}
    warnings = []
    for shard in sorted(REG.glob("*.yaml")):
        try:
            doc = yaml.safe_load(shard.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            warnings.append(f"{shard.name}: YAML parse error: {e}")
            continue
        for f in (doc or {}).get("features") or []:
            for ref in f.get("base_refs") or []:
                for match in PATH_RE.finditer(ref.replace("\\", "/")):
                    for path in extract_paths(match[0]):
                        anchors = cited.setdefault(path, set())
                        tail = ref[match.end():]
                        if symbol := re.match(r":\s*([A-Za-z_]\w*)", tail):
                            anchors.add(symbol[1])
                        if pair := re.match(r":\s*(METHOD|MUTATOR_HOOKFUNCTION)\s*\(\s*(\w+)\s*,\s*(\w+)\b", tail):
                            anchors.add(f"{pair[1]}({pair[2]}, {pair[3]})")
                        if reg := re.match(r":\s*(REGISTER_[A-Z_0-9]+)\s*\(\s*(\w+)\s*[,)]", tail):
                            anchors.add(f"{reg[1]}({reg[2]})")
    return cited, warnings


def load_scope() -> dict:
    if not SCOPE_FILE.exists():
        return {"exclude": [], "defer": []}
    doc = yaml.safe_load(SCOPE_FILE.read_text(encoding="utf-8")) or {}
    return {"exclude": doc.get("exclude") or [], "defer": doc.get("defer") or []}


def match_scope(path: str, entries: list[dict]) -> dict | None:
    """First scope entry whose pattern matches path. `dir/**` matches the subtree."""
    for e in entries:
        pat = (e.get("pattern") or "").replace("\\", "/")
        if not pat:
            continue
        if pat.endswith("/**"):
            if path.startswith(pat[:-2]):  # keep the trailing '/'
                return e
        elif fnmatch.fnmatch(path, pat) or path == pat:
            return e
    return None


# Deliberately conservative: ordinary typed function bodies only. Macros, methods, hooks and
# registrations require the per-unit inventory. A row may cover a helper implicitly.
DEFINITION = re.compile(
    r"^\s*(?:static\s+)?(?:void|bool|int|float|vector|string|entity)\s+"
    r"([A-Za-z_]\w*)\s*\([^(){};]*\)\s*\{", re.MULTILINE)
QC_TEXT = re.compile(r'//[^\n]*|/\*.*?\*/|"(?:\\.|[^"\\])*"', re.DOTALL)


NAMED_METHOD = re.compile(r"^\s*(METHOD|MUTATOR_HOOKFUNCTION)\s*\(\s*(\w+)\s*,\s*(\w+)\s*[,)]", re.MULTILINE)
REGISTRATION = re.compile(r"^\s*(REGISTER_[A-Z_0-9]+)\s*\(\s*(\w+)\s*[,)]", re.MULTILINE)


def uncited_symbols(base, cited, cited_paths):
    """Find local symbol names without matching tokens in this file's registry citations."""
    result = {}
    macros = {}
    tokens = {}
    for path, names in cited.items():
        tokens.setdefault(path.lower(), set()).update(names)
    for path in sorted(cited_paths):
        source = (base / path).read_text(encoding="utf-8", errors="replace")
        code = QC_TEXT.sub(" ", source)
        definitions = set(DEFINITION.findall(code))
        missing = definitions - tokens[path.lower()]
        if missing:
            result[path] = sorted(missing)
        names = tokens[path.lower()]
        declarations = {f"{kind}({owner}, {name})" for kind, owner, name in NAMED_METHOD.findall(code)}
        declarations.update(f"{kind}({name})" for kind, name in REGISTRATION.findall(code))
        unreferenced = declarations - names
        if unreferenced:
            macros[path] = sorted(unreferenced)
    return result, macros


def main():
    ap = argparse.ArgumentParser(description="Base->registry coverage ledger")
    ap.add_argument("--base", type=pathlib.Path, default=DEFAULT_BASE,
                    help=f"Base qcsrc tree (default {DEFAULT_BASE})")
    ap.add_argument("--verify-pin", action="store_true", help="require a complete, clean QC tree at the registry pin")
    ap.add_argument("--top", type=int, default=50, help="how many largest unmapped files to list")
    args = ap.parse_args()
    if not args.base.is_dir():
        sys.exit(f"Base qcsrc tree not found: {args.base} (pass --base)")

    reference = "unverified source tree"
    if args.verify_pin:
        if args.base.name != "qcsrc":
            sys.exit("Expected qcsrc/ in the pinned data repository.")
        verify_qc_inputs(args.base.parent)
        reference = PIN
    base_files = enumerate_base(args.base)
    if not base_files:
        sys.exit("No QuakeC source files found in the supplied tree.")
    cited, warnings = collect_citations()
    if warnings or not cited:
        sys.exit("\n".join(warnings) or "No registry citations found.")

    scope = load_scope()

    # case-insensitive resolution: Base is the canonical case
    canon = {p.lower(): p for p in base_files}
    buckets: dict[str, list[str]] = {"cited": [], "excluded": [], "deferred": [], "unmapped": []}
    defer_satisfied, cited_paths = [], set()
    for ref_path in cited:
        hit = canon.get(ref_path.lower())
        if hit:
            cited_paths.add(hit)
    stale = sorted(p for p in cited if p.lower() not in canon)

    for path in sorted(base_files):
        if path in cited_paths:
            buckets["cited"].append(path)
            if match_scope(path, scope["defer"]):
                defer_satisfied.append(path)
        elif (e := match_scope(path, scope["exclude"])) is not None:
            buckets["excluded"].append(path)
        elif (e := match_scope(path, scope["defer"])) is not None:
            buckets["deferred"].append(path)
        else:
            buckets["unmapped"].append(path)

    lines_of = lambda paths: sum(base_files[p] for p in paths)
    total_files, total_lines = len(base_files), sum(base_files.values())

    # per top-level dir rollup
    def topdir(p): return p.split("/", 1)[0] if "/" in p else "(root)"
    dirs = sorted({topdir(p) for p in base_files})
    per_dir = {}
    for d in dirs:
        row = {}
        for b, paths in buckets.items():
            sub = [p for p in paths if topdir(p) == d]
            row[b] = (len(sub), lines_of(sub))
        dfiles = [p for p in base_files if topdir(p) == d]
        row["total"] = (len(dfiles), lines_of(dfiles))
        per_dir[d] = row

    unmapped_sorted = sorted(buckets["unmapped"], key=lambda p: -base_files[p])
    today = datetime.date.today().isoformat()

    md = [
        "# Base coverage ledger",
        "",
        f"_Generated {today} by `tools/parity-coverage.py` from {len(list(REG.glob('*.yaml')))} registry shards"
        f" against `{reference}`._",
        "",
        "A Base file is **cited** when any registry row's `base_refs` names it, **excluded** when",
        "[coverage-scope.yaml](coverage-scope.yaml) declares it out of parity scope (with rationale),",
        "**deferred** when an audit is scheduled and **UNMAPPED** when the registry has no",
        "citation or scope entry. These counts measure traceability. Checking behavior requires",
        "following the original and native callers or exercising them.",
        "",
        "## Summary",
        "",
        "| bucket | files | lines | % of lines |",
        "|---|---:|---:|---:|",
    ]
    for b in ("cited", "excluded", "deferred", "unmapped"):
        n, ln = len(buckets[b]), lines_of(buckets[b])
        label = b.upper() if b == "unmapped" else b
        md.append(f"| {label} | {n} | {ln} | {ln / max(1, total_lines) * 100:.1f}% |")
    md += [f"| **total** | {total_files} | {total_lines} | 100% |", ""]

    audit_coverage = None
    if reference == PIN:
        ledger = json.loads((OUT / "unmapped-audit/dispositions.json").read_text(encoding="utf-8"))
        if ledger["reference"] != PIN:
            sys.exit("Source review and coverage report use different upstream pins.")
        reviewed = {row["path"] for row in ledger["files"]}
        uncited = set(buckets["unmapped"])
        with (OUT / "deferred-audit/cohort.tsv").open(encoding="utf-8", newline="") as cohort:
            deferred_review = {row["path"] for row in csv.DictReader(cohort, delimiter="\t")}
        scheduled = set(buckets["deferred"])
        audit_coverage = {
            "source_review_files": len(reviewed),
            "unmapped_with_source_review": len(uncited & reviewed),
            "unmapped_without_source_review": sorted(uncited - reviewed),
            "deferred_review_files": len(deferred_review),
            "deferred_with_source_review": len(scheduled & deferred_review),
            "deferred_without_source_review": sorted(scheduled - deferred_review),
        }
        md += ["## Separate source review", "",
               f"The [directory audit](unmapped-audit/README.md) assessed {len(reviewed)} files at this pin. "
               f"It includes {len(uncited & reviewed)} of the {len(uncited)} files currently marked UNMAPPED. "
               f"{len(uncited - reviewed)} uncited files fall outside that review. "
               "The [ordered follow-up](unmapped-audit/S1-REVIEW.md) records behavioral comparisons within "
               "that inventory.", "",
               f"The [deferred menu audit](deferred-audit/README.md) assessed {len(deferred_review)} additional files. "
               f"It covers {len(scheduled & deferred_review)} of the {len(scheduled)} current deferred entries. "
               "A per-file source assessment establishes the scope of review. Runtime checks are listed separately.", ""]

    md += ["## Per-directory", "",
           "| dir | total files | cited | excluded | deferred | UNMAPPED files | UNMAPPED lines |",
           "|---|---:|---:|---:|---:|---:|---:|"]
    for d in dirs:
        r = per_dir[d]
        md.append(f"| `{d}/` | {r['total'][0]} | {r['cited'][0]} | {r['excluded'][0]} | "
                  f"{r['deferred'][0]} | {r['unmapped'][0]} | {r['unmapped'][1]} |")
    md.append("")

    if unmapped_sorted:
        md += [f"## Largest UNMAPPED files (top {args.top})", "",
               "| file | lines |", "|---|---:|"]
        for p in unmapped_sorted[:args.top]:
            md.append(f"| `{p}` | {base_files[p]} |")
        md.append("")

    # Generated include lists are common among uncited files. Preserve each path in the ledger,
    # but distinguish a file consisting only of includes/pragma once/comments from a behavior lead.
    include_only = []
    for path in buckets["unmapped"] + buckets["deferred"]:
        source = (args.base / path).read_text(encoding="utf-8", errors="replace")
        code = QC_TEXT.sub(" ", source)
        if all(re.fullmatch(r"\s*(?:#\s*(?:include\b.*|pragma\s+once))?\s*", line)
               for line in code.splitlines()):
            include_only.append(path)
    md += [f"Of the {len(buckets['unmapped']) + len(buckets['deferred'])} unmapped or deferred files, "
           f"{len(include_only)} contain only comments, whitespace, include directives or pragma once. "
           "The JSON lists those paths separately. Keep the files they include in scope.", ""]

    candidates, macro_candidates = uncited_symbols(args.base, cited, cited_paths)
    md += ["## Functions inside cited files", "",
           f"{sum(map(len, candidates.values()))} ordinary function names in {len(candidates)} cited files "
           "lack a path:symbol anchor naming the function immediately after the colon. "
           "A prose note, line reference or caller may already cover the behavior. "
           "The full per-file list is in `_coverage.json` under `uncited_functions`.", "",
           "| file | names to inspect |", "|---|---:|"]
    for path in sorted(candidates, key=lambda p: -len(candidates[p]))[:args.top]:
        md.append(f"| `{path}` | {len(candidates[path])} |")
    md.append("")

    md += ["## Macro declarations inside cited files", "",
           f"{sum(map(len, macro_candidates.values()))} distinct declarations in {len(macro_candidates)} "
           "cited files lack an explicit same-file macro anchor. This scan recognizes "
           "line-leading METHOD, MUTATOR_HOOKFUNCTION and REGISTER_* calls with literal identifiers. "
           "For a method or hook it checks both the owner and member names. Conditionals, other macro "
           "families and token-pasted names require inspection. The JSON lists the declarations "
           "under `uncited_macro_declarations`.", ""]

    if buckets["deferred"]:
        md += ["## Deferred (audit scheduled)", "",
               "| file | lines | note |", "|---|---:|---|"]
        for p in sorted(buckets["deferred"], key=lambda p: -base_files[p]):
            e = match_scope(p, scope["defer"]) or {}
            md.append(f"| `{p}` | {base_files[p]} | {e.get('note', '')} |")
        md.append("")

    if scope["exclude"]:
        md += ["## Exclusions (deliberately out of scope)", "",
               "| pattern | files | lines | rationale |", "|---|---:|---:|---|"]
        for e in scope["exclude"]:
            sub = [p for p in buckets["excluded"] if match_scope(p, [e]) is e]
            md.append(f"| `{e.get('pattern')}` | {len(sub)} | {lines_of(sub)} | {e.get('rationale', '')} |")
        md.append("")

    if defer_satisfied:
        md += ["## Defer entries now satisfied (remove from coverage-scope.yaml)", ""]
        md += [f"- `{p}` is now cited" for p in sorted(defer_satisfied)]
        md.append("")

    if stale:
        md += ["## STALE citations (base_refs path matches no Base file — fix the row)", ""]
        md += [f"- `{p}`" for p in stale]
        md.append("")

    (OUT / "COVERAGE.md").write_text("\n".join(md), encoding="utf-8")
    (OUT / "_coverage.json").write_text(json.dumps({
        "generated": today, "reference": reference, "source_review": audit_coverage,
        "summary": {b: {"files": len(v), "lines": lines_of(v)} for b, v in buckets.items()},
        "unmapped": [{"path": p, "lines": base_files[p]} for p in unmapped_sorted],
        "deferred": sorted(buckets["deferred"]),
        "uncited_functions": candidates,
        "uncited_macro_declarations": macro_candidates,
        "include_only_among_unmapped_or_deferred": sorted(include_only),
        "stale_citations": stale,
    }, indent=1), encoding="utf-8")

    print(f"base files={total_files} lines={total_lines} | cited={len(buckets['cited'])} "
          f"excluded={len(buckets['excluded'])} deferred={len(buckets['deferred'])} "
          f"UNMAPPED={len(buckets['unmapped'])} ({lines_of(buckets['unmapped'])} lines, "
          f"{lines_of(buckets['unmapped']) / max(1, total_lines) * 100:.1f}%) | stale={len(stale)}")
    for w in warnings:
        print("  WARN", w)


if __name__ == "__main__":
    main()

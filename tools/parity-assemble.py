#!/usr/bin/env python3
"""Assemble the Base<->port parity registry into human-readable views.

Reads every planning/parity/registry/*.yaml shard, validates it against the
schema in planning/parity/SCHEMA.md, and (re)generates:

  planning/parity/INDEX.md              every feature, one row, per-dimension status
  planning/parity/PARITY-GAPS.md        gaps ranked by gameplay impact (the backlog)
  planning/parity/NEEDS-INGAME-CHECK.md  rows whose observable status code-reading can't confirm

Run after the build/diff workflows have written the shards:
    python tools/parity-assemble.py
"""
from __future__ import annotations
import sys, pathlib, datetime, re
try:
    import yaml
except ImportError:
    sys.exit("PyYAML required: pip install pyyaml")

ROOT = pathlib.Path(__file__).resolve().parent.parent
REG = ROOT / "planning" / "parity" / "registry"
OUT = ROOT / "planning" / "parity"

DIMS = ["logic", "values", "timing", "presentation", "audio"]
STATUS = {"faithful", "partial", "stub", "missing", "na", "unknown"}
LIVENESS = {"live", "dead", "partial", "na", "unknown"}

# --- gameplay-impact ranking -------------------------------------------------
# dimension weights: logic & liveness dominate, presentation/audio lower.
DIM_WEIGHT = {"logic": 5, "values": 3, "timing": 3, "presentation": 2, "audio": 1, "liveness": 5}
# how bad each status is (per dimension)
PENALTY = {"missing": 1.0, "stub": 0.8, "partial": 0.5, "unknown": 0.4,
           "faithful": 0.0, "na": 0.0, "dead": 1.0, "live": 0.0}
# category weighting: core gameplay first, niche entities last
CAT_MULT = {
    "gametype": 1.5, "weapon": 1.5, "damage": 1.5, "physics": 1.5, "item": 1.4, "scoring": 1.4,
    "server": 1.2, "net": 1.1, "data": 1.1, "mapobject": 0.9, "mutator": 1.0,
    "client": 0.8, "effect": 0.8, "sound": 0.8, "notification": 0.8, "bot": 0.7,
    "monster": 0.6, "turret": 0.6, "vehicle": 0.6, "misc": 0.7, "menu": 0.5,
}
GLYPH = {"faithful": "OK", "partial": "~", "stub": "stub", "missing": "MISS",
         "na": "-", "unknown": "?", "live": "live", "dead": "DEAD"}


def scored_dimensions(f):
    if f.get("intended_divergence"):
        return f.get("open_dimensions", [])
    return DIMS + ["liveness"]


def feature_score(cat: str, f: dict) -> float:
    status = f.get("status") or {}
    score = sum(DIM_WEIGHT[d] * PENALTY.get(status.get(d, "unknown"), 0.4)
                for d in scored_dimensions(f) if d in DIM_WEIGHT)
    return round(CAT_MULT.get(cat, 1.0) * score, 2)


def load():
    units, warnings = [], []
    ids = set()
    for p in sorted(REG.glob("*.yaml")):
        try:
            doc = yaml.safe_load(p.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            warnings.append(f"{p.name}: YAML parse error: {e}")
            continue
        if not isinstance(doc, dict) or "features" not in doc:
            warnings.append(f"{p.name}: missing top-level 'features'")
            continue
        cat = doc.get("category", "misc")
        for f in doc.get("features") or []:
            ident = f.get("id")
            if not isinstance(ident, str) or not ident.strip() or ident in ids:
                warnings.append(f"{p.name}: missing or duplicate feature id '{ident}'")
                continue
            ids.add(ident)
            st = f.get("status", {}) or {}
            for d in DIMS:
                if st.get(d) not in STATUS:
                    warnings.append(f"{p.name}:{f.get('id')}: bad {d}='{st.get(d)}'")
            if st.get("liveness") not in LIVENESS:
                warnings.append(f"{p.name}:{f.get('id')}: bad liveness='{st.get('liveness')}'")
            if f.get("intended_divergence") and not f.get("divergence_rationale"):
                warnings.append(f"{p.name}:{f.get('id')}: intended_divergence without rationale")
            if "open_dimensions" in f:
                dims = f["open_dimensions"]
                if (not f.get("intended_divergence") or not isinstance(dims, list)
                        or not dims or any(not isinstance(d, str) or d not in DIM_WEIGHT for d in dims)
                        or len(set(dims)) != len(dims)
                        or any(not PENALTY.get(st.get(d), 0.0) for d in dims)):
                    warnings.append(f"{p.name}:{ident}: invalid open_dimensions for a mixed design/implementation row")
                    continue
            f["_unit"], f["_cat"] = doc.get("unit", p.stem), cat
            f["_score"] = feature_score(cat, f)
        units.append(doc)
    return units, warnings


def stamp():
    # date passed implicitly; avoid Date.now-style nondeterminism complaints in CI by reading mtime-free
    return datetime.date.today().isoformat()


def write_index(units):
    total = sum(len(u.get("features") or []) for u in units)
    # per-dimension histogram
    hist = {d: {} for d in DIMS + ["liveness"]}
    for u in units:
        for f in u.get("features") or []:
            st = f.get("status", {}) or {}
            for d in DIMS + ["liveness"]:
                v = st.get(d, "unknown")
                hist[d][v] = hist[d].get(v, 0) + 1
    lines = [f"# Parity Index", "",
             f"_Generated {stamp()} from {len(units)} units, {total} features._", "",
             "Status: `OK`=faithful `~`=partial `stub` `MISS`=missing `-`=n/a `?`=unknown; "
             "liveness `live`/`DEAD`/`~`/`?`.", "",
             "## Dimension rollup", "", "| dim | " + " | ".join(sorted(STATUS | LIVENESS)) + " |",
             "|" + "---|" * (len(sorted(STATUS | LIVENESS)) + 1)]
    cols = sorted(STATUS | LIVENESS)
    for d in DIMS + ["liveness"]:
        lines.append("| " + d + " | " + " | ".join(str(hist[d].get(c, 0)) for c in cols) + " |")
    lines += ["", "## Features by unit", ""]
    for u in sorted(units, key=lambda x: (x.get("category", ""), x.get("unit", ""))):
        feats = u.get("features") or []
        lines += [f"### `{u.get('unit')}` ({u.get('category')}) — {len(feats)} features", "",
                  "| id | name | L | V | T | P | A | live | conf |",
                  "|---|---|---|---|---|---|---|---|---|"]
        for f in feats:
            st = f.get("status", {}) or {}
            g = lambda d: GLYPH.get(st.get(d, "unknown"), "?")
            div = " *(intended)*" if f.get("intended_divergence") else ""
            if f.get("open_dimensions"):
                div = " *(design difference; open " + ", ".join(f["open_dimensions"]) + ")*"
            lines.append(f"| `{f.get('id')}` | {f.get('name','')}{div} | {g('logic')} | {g('values')} | "
                         f"{g('timing')} | {g('presentation')} | {g('audio')} | {g('liveness')} | {f.get('confidence','?')} |")
        lines.append("")
    (OUT / "INDEX.md").write_text("\n".join(lines), encoding="utf-8")


def absence_witnesses(units, root):
    """Find declaration-shaped lines for methods explicitly claimed absent. Never infer liveness."""
    claims = {}
    negative = re.compile(r"\b(?i:no|missing|lacks)\s+(`?[A-Z]\w*`?(?:\s*/\s*`?[A-Z]\w*`?)*)")
    for unit in units:
        for feature in unit.get("features") or []:
            evidence = list(feature.get("gaps") or [])
            evidence.extend(v.get("ref", "") for v in feature.get("verification") or [] if v.get("result") == "fail")
            for line in evidence:
                for group in negative.finditer(line):
                    names = re.split(r"\s*/\s*", group[1].replace("`", ""))
                    suffix = line[group.end():]
                    if re.match(r"\s*[.(]", suffix):
                        names.pop()  # A missing call or qualified member is a different claim.
                    elif re.match(r"(?:-equivalent\b|\s+(?:call(?:er|s|ers)?|hooks?)\b)", suffix, re.I):
                        continue  # A method can exist while its callers or a mode-specific hook are missing.
                    for name in names:
                        if any(c.islower() for c in name) and any(c.isupper() for c in name[1:]):
                            context = " ".join([line, *(feature.get("port_refs") or [])])
                            claims.setdefault(name, []).append((feature["id"], context))
    if not claims:
        return []

    names = "|".join(re.escape(n) for n in sorted(claims))
    declaration = re.compile(r"^[ \t]*(?:public|private|internal|protected|static)[ \t]+"
                             r"[\w.<>,?\[\] \t]+[ \t]+(" + names + r")(?:<[^()\n]+>)?[ \t]*\(", re.M)
    files = [p.relative_to(root) for folder in ("game", "src") for p in (root / folder).rglob("*.cs")
             if not {"bin", "obj"}.intersection(p.relative_to(root).parts)]
    stems = {}
    for path in files:
        stems.setdefault(path.stem, []).append(path)
    witnesses = set()
    for path in files:
        scoped = {name: {feature for feature, context in records if path.as_posix() in context
                         or (len(stems[path.stem]) == 1 and re.search(rf"\b{re.escape(path.stem)}\b", context))}
                  for name, records in claims.items()}
        if not any(scoped.values()):
            continue
        source = (root / path).read_text(encoding="utf-8-sig")
        # Keep newlines for source links, ignore common C# comments and literals. This is a lexical check.
        source = re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|"""[\s\S]*?"""|@"(?:""|[^"])*"|"(?:\\.|[^"\\\n])*"|\'(?:\\.|[^\'\\\n])*\'',
                        lambda m: "\n" * m[0].count("\n") or " ", source)
        for match in declaration.finditer(source):
            line = source.count("\n", 0, match.start()) + 1
            witnesses.update((feature, match[1], path.as_posix(), line) for feature in scoped[match[1]])
    return sorted(witnesses)


def write_gaps(units):
    rows = []
    for u in units:
        for f in u.get("features") or []:
            if f["_score"] > 0:
                rows.append(f)
    rows.sort(key=lambda f: f["_score"], reverse=True)
    lines = ["# Parity Gaps — ranked by gameplay impact", "",
             f"_Generated {stamp()}. {len(rows)} registry leads with an open or unknown dimension, highest score first._", "",
             "Score weights logic & liveness highest, then values/timing, then presentation/audio, "
             "scaled by category. These are recorded assessments. Recheck the current caller before treating a lead as a defect. For a mixed row, only the explicitly open dimensions contribute to its score. Design decisions and unscored caveats follow the table.", "",
             "| # | score | unit | feature | worst | gaps |", "|---|---|---|---|---|---|"]
    for i, f in enumerate(rows, 1):
        st = f.get("status", {}) or {}
        worst = max(scored_dimensions(f),
                    key=lambda d: DIM_WEIGHT[d] * PENALTY.get(st.get(d, "unknown"), 0.4))
        gap = "; ".join(f.get("gaps") or []) or "(no gap text)"
        gap = gap.replace("|", "\\|")
        lines.append(f"| {i} | {f['_score']} | `{f['_unit']}` | `{f.get('id')}` | {worst}:{st.get(worst)} | {gap} |")
    witnesses = absence_witnesses(units, ROOT)
    if witnesses:
        lines += ["", "## Recheck assertions of missing methods", "",
                  "These negative claims name a method and a native source file or unique file stem. A declaration-shaped line in that file matches the method name. Inspect the linked line, declaring type and callers before assigning a fix. This lexical check leaves the recorded scores intact.", "",
                  "| Feature | Method | Native source |", "|---|---|---|"]
        lines.extend(f"| `{feature}` | `{name}` | [{path}:{line}](../../{path}#L{line}) |" for feature, name, path, line in witnesses)
    lines += ["", "## Evidence to reconcile", "",
              "These rows record design decisions, mismatched constants, or caveats outside the ranked table. "
              "Check each claim against the pinned source, the current caller, and the decision rationale before assigning work.", ""]
    for u in units:
        for f in u.get("features") or []:
            # Ranked rows already print their gap text. Their design rationale and constants
            # are still separate evidence and remain relevant even with an open dimension.
            evidence = list(f.get("gaps") or []) if f["_score"] == 0 else []
            evidence.extend(f"Constant {c.get('name')} ({c.get('base')} / {c.get('port')})"
                            for c in f.get("constants") or [] if c.get("match") is False)
            if f.get("intended_divergence"):
                evidence.insert(0, f"Recorded divergence — {f.get('divergence_rationale')}")
            if evidence:
                lines.append(f"- `{f['id']}` — " + " ".join(evidence))
    (OUT / "PARITY-GAPS.md").write_text("\n".join(lines), encoding="utf-8")
    return rows


def write_ingame(units):
    rows = []
    for u in units:
        for f in u.get("features") or []:
            st = f.get("status", {}) or {}
            needs = (any(st.get(d) == "unknown" for d in DIMS + ["liveness"])
                     or (f.get("confidence") == "low" and st.get("presentation") not in ("na", "faithful")))
            if needs:
                rows.append(f)
    lines = ["# Needs in-game check", "",
             f"_Generated {stamp()}. {len(rows)} features with a dimension that "
             "code-reading could not confirm. Verify by running the app and observing._", "",
             "| unit | feature | uncertain | what to look for |", "|---|---|---|---|"]
    for f in rows:
        st = f.get("status", {}) or {}
        unc = ",".join(d for d in DIMS + ["liveness"] if st.get(d) == "unknown") or "low-conf"
        note = (f.get("notes") or (f.get("gaps") or [""])[0] or "").replace("|", "\\|")
        lines.append(f"| `{f['_unit']}` | `{f.get('id')}` | {unc} | {note} |")
    (OUT / "NEEDS-INGAME-CHECK.md").write_text("\n".join(lines), encoding="utf-8")
    return rows


def main():
    units, warnings = load()
    if not units:
        sys.exit("No registry shards found in " + str(REG))
    if warnings:
        sys.exit("\n".join(warnings))
    write_index(units)
    gaps = write_gaps(units)
    ingame = write_ingame(units)
    total = sum(len(u.get("features") or []) for u in units)
    print(f"units={len(units)} features={total} gaps={len(gaps)} needs_ingame={len(ingame)} warnings={len(warnings)}")


if __name__ == "__main__":
    main()

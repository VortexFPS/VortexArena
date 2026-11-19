#!/usr/bin/env python3
"""Generate or verify the per-file audit of the pinned QC unmapped inventory."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict, deque
from dataclasses import dataclass
from fnmatch import fnmatchcase
from hashlib import sha256
import json
from pathlib import Path, PurePosixPath
from posixpath import normpath
import re
from parity_reference import PIN, QC_EXTENSIONS, verify_qc_inputs

ROOT = Path(__file__).resolve().parent.parent
AUDIT = ROOT / "planning/parity/unmapped-audit"
TARGETS = {
    "CSQC": "client/progs.inc",
    "MENUQC": "menu/progs.inc",
    "SVQC": "server/progs.inc",
}
COMMENTS_AND_LITERALS = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'')
STRUCTURAL = re.compile(r"#\s*(?:include|pragma|if|ifdef|ifndef|elif|elifdef|elifndef|else|endif)\b")
DIRECTIVE = re.compile(r"#\s*(\w+)\b\s*(.*)")
INCLUDE = re.compile(r'([<"])([^>"]+)[>"]')
MACRO = re.compile(r"^\s*#\s*define\s+(\w+)")
MACRO_INVOCATION = re.compile(
    r"^\s*((?:REGISTER|SPAWNFUNC|STATIC_INIT|ACCUMULATE)\w*|"
    r"CLASS|ENDCLASS|ATTRIB|METHOD|MUTATOR_HOOK\w*|NET_HANDLE|TEST|MODEL|SOUND|"
    r"\w+_COMMAND|spawnfunc|spawnfunc_declare|COMPONENT|EVENT|SYSTEM(?:_UPDATE)?|"
    r"REGISTRY\w*|PRECACHE|SHUTDOWN|OFFHAND|FIELDS|ERASEABLE|_REGISTER\w*)\s*\(([^\n]*)"
)
TYPED_FUNCTION = re.compile(
    r"^[ \t]*(?:(?:static|noref|inline|var|\[\[[^\n]*?\]\])[ \t]+)*"
    r"(?:void|bool|int|float|vector|string|entity|[A-Z]\w*)[ \t]+(\w+)[ \t]*"
    r"\([^;{}]*\)\s*([;{=])",
    re.MULTILINE,
)
QC_FUNCTION = re.compile(
    r"^[ \t]*(?:static[ \t]+)?(?:void|bool|int|float|vector|string|entity)[ \t]*"
    r"\([^;{}]*\)[ \t]+(\w+)\s*([;=])",
    re.MULTILINE,
)
TOKEN = re.compile(r"\b[A-Za-z_]\w*\b")
DECLARATION = re.compile(
    r"^(?:(?:extern|noref|const|var|static|typedef)\s+)*"
    r"\.?[A-Za-z_]\w*\s+\.?(?:\w+\s*,\s*)*\w+\s*(?:\([^{}]*\))?\s*;$"
)
KNOWN_ZERO = {"ENABLE_DEBUGDRAW", "ENABLE_DEBUGTRACE", "DEBUGPATHING"}
KNOWN_UNDEFINED = {"BUILD_MOD", "EWHEEL_FANCYPATH", "WALKER_FANCYPATHING"}
REGISTRATION_PREFIXES = {
    "REGISTER_GAMETYPE": "MAPINFO_TYPE",
    "REGISTER_ITEM": "ITEM",
    "REGISTER_MONSTER": "MON",
    "REGISTER_MUTATOR": "MUTATOR",
    "REGISTER_NET_C2S": "NET",
    "REGISTER_NET_LINKED": "NET",
    "REGISTER_NET_TEMP": "NET",
    "REGISTER_STATUSEFFECT": "STATUSEFFECT",
    "REGISTER_WEAPON": "WEP",
    "MODEL": "MDL",
    "SOUND": "SND",
    "NET_HANDLE": "Net_Handle",
    "spawnfunc": "spawnfunc",
    "SPAWNFUNC_ITEM": "spawnfunc",
}


def mask_tokens(source: str, comments_only: bool = False) -> str:
    def replace(match):
        value = match.group()
        if comments_only and value[0] != "/":
            return value
        return re.sub(r"[^\n]", " ", value)

    return COMMENTS_AND_LITERALS.sub(replace, source)


@dataclass
class Source:
    content: bytes

    def __post_init__(self):
        self.text = self.content.decode("utf-8", errors="replace")
        self.code = mask_tokens(self.text)
        self.lines = self.text.splitlines()
        self.code_lines = self.code.splitlines()
        self.directive_lines = mask_tokens(self.text, comments_only=True).splitlines()


def tri_not(value):
    return None if value is None else not value


def tri_all(values):
    return False if False in values else (None if None in values else True)


def tri_any(values):
    return True if True in values else (None if None in values else False)


def condition(expr, target):
    """Resolve stock program selectors and audited default-off flags. Leave other expressions open."""
    expr = expr.strip()
    if "||" in expr:
        return tri_any([condition(part, target) for part in expr.split("||")])
    if "&&" in expr:
        return tri_all([condition(part, target) for part in expr.split("&&")])
    if expr.startswith("!"):
        return tri_not(condition(expr[1:], target))
    if expr.isdigit():
        return bool(int(expr))
    match = re.fullmatch(r"defined\s*(?:\(\s*(\w+)\s*\)|(\w+))", expr)
    if match:
        name = match[1] or match[2]
        defined = True
    else:
        name = expr
        defined = False
    if name in TARGETS:
        return name == target
    if name == "GAMEQC":
        return target != "MENUQC"
    if name == "XONOTIC":
        return True
    if name in KNOWN_UNDEFINED:
        return False
    if name in KNOWN_ZERO and not defined:
        return False
    return None


def line_context(source):
    """Return per-line possible targets and literal branch labels."""
    stack = []
    contexts = []
    for line in source.directive_lines:
        match = DIRECTIVE.match(line.lstrip())
        if match:
            op, value = match.groups()
            if op in {"ifdef", "ifndef", "elifdef", "elifndef"}:
                value = f"{'!' if op.endswith('ndef') else ''}defined({value.strip()})"
            if op in {"if", "ifdef", "ifndef"}:
                stack.append([value])
            if op in {"elif", "elifdef", "elifndef", "else"}:
                stack[-1].append("1" if op == "else" else value)
            if op == "endif":
                stack.pop()
        possible = []
        for target in TARGETS:
            branches = [
                tri_all([condition(choices[-1], target)] +
                        [tri_not(condition(v, target)) for v in choices[:-1]])
                for choices in stack
            ]
            if tri_all(branches) is not False:
                possible.append(target)
        guards = [
            " && ".join([f"!({v})" for v in choices[:-1]] + [choices[-1]])
            for choices in stack
        ]
        contexts.append((possible, guards))
    if stack:
        raise ValueError("unclosed QC preprocessor conditional")
    return contexts


def include_edges(path, source, sources):
    for number, line in enumerate(source.directive_lines, 1):
        directive = DIRECTIVE.match(line.lstrip())
        if directive is None or directive[1] != "include":
            continue
        match = INCLUDE.match(directive[2])
        if not match:
            continue
        literal = match[2]
        candidates = [literal]
        if match[1] == '"':
            candidates.insert(0, normpath(PurePosixPath(path).parent / literal))
        yield number, literal, next((item for item in candidates if item in sources), None)


def build_paths(includes, contexts):
    paths = defaultdict(dict)
    for target, root in TARGETS.items():
        pending = deque([(root, [])])
        while pending:
            path, chain = pending.popleft()
            if target in paths[path]:
                continue
            paths[path][target] = chain
            for line, _, child in includes[path]:
                if child is not None and target in contexts[path][line - 1][0]:
                    pending.append((child, chain + [f"{path}:{line}"]))
    return paths


def source_shape(source):
    code_lines = []
    continuation = False
    for line in source.directive_lines:
        line = line.strip()
        if not line:
            continue
        structural = not continuation and STRUCTURAL.match(line)
        if not structural:
            code_lines.append(line)
        continuation = line.endswith("\\")
    if not code_lines:
        return "include-routing" if any(line.lstrip().startswith("#") for line in source.directive_lines) else "empty-or-comments"
    if all(DECLARATION.fullmatch(line) for line in code_lines):
        return "declarations"
    return "semantic"


def find_symbols(source):
    symbols = []
    starts = set()
    for pattern, kind in ((TYPED_FUNCTION, "function"), (QC_FUNCTION, "function")):
        for match in pattern.finditer(source.code):
            if match.start(1) in starts:
                continue
            starts.add(match.start(1))
            number = source.code.count("\n", 0, match.start(1)) + 1
            label = "prototype" if match[2] == ";" else kind
            symbols.append((number, label, match[1]))
    for number, line in enumerate(source.code_lines, 1):
        if match := MACRO.match(line):
            symbols.append((number, "macro-definition", match[1]))
        if match := MACRO_INVOCATION.match(line):
            macro, arguments = match.groups()
            count = 2 if macro in {"METHOD", "ATTRIB", "CLASS", "MUTATOR_HOOKFUNCTION"} else 1
            identifiers = re.match(r"\s*(\w+)(?:\s*,\s*(\w+))?", arguments)
            if identifiers:
                args = ", ".join(arg for arg in identifiers.groups()[:count] if arg)
                symbols.append((number, "macro-invocation", f"{macro}({args})"))
    return sorted(set(symbols))


def reference_names(kind, name):
    if kind != "macro-invocation":
        return [name]
    macro, _, argument = name.partition("(")
    prefix = REGISTRATION_PREFIXES.get(macro)
    if prefix:
        return [f"{prefix}_{argument[:-1]}"]
    return []


def reference_index(symbols_by_path, sources, contexts, paths):
    indexed = defaultdict(list)
    names = {
        indexed_name for symbols in symbols_by_path.values() for _, kind, name in symbols
        for indexed_name in reference_names(kind, name)
    }
    definitions = {
        (path, line, name)
        for path, symbols in symbols_by_path.items() for line, _, name in symbols
    }
    for path, source in sources.items():
        macro_body = False
        for line, code in enumerate(source.code_lines, 1):
            macro_body = macro_body or bool(MACRO.match(code))
            for token in TOKEN.finditer(code):
                name = token.group()
                if name not in names or (path, line, name) in definitions:
                    continue
                possible, guards = contexts[path][line - 1]
                targets = [target for target in possible if target in paths[path]]
                tail = code[token.end():].lstrip()
                mode = "call-shaped" if tail.startswith("(") else "token-reference"
                if macro_body:
                    mode = "macro-body-reference"
                elif code.lstrip().startswith("#"):
                    mode = "preprocessor-reference"
                indexed[name].append((path, line, mode, targets, guards))
            macro_body = macro_body and source.directive_lines[line - 1].rstrip().endswith("\\")
    return indexed


def read_anchor(anchor, root):
    file, needle = anchor
    lines = (root / file).read_text(encoding="utf-8", errors="replace").splitlines()
    matches = [number for number, line in enumerate(lines, 1) if needle in line]
    if not matches:
        raise ValueError(f"Missing audit anchor {file} {needle!r}")
    return {"path": file, "lines": matches, "contains": needle}


def load_rules(base):
    rules = json.loads((AUDIT / "comparisons.json").read_text(encoding="utf-8"))
    for rule in rules:
        for key, root in (("native", ROOT), ("qc", base), ("base_config", base.parent)):
            if key in rule:
                rule[key] = [read_anchor(anchor, root) for anchor in rule[key]]
    return rules


def disposition(shape, rule):
    if shape == "empty-or-comments":
        return "empty"
    if shape == "include-routing":
        return "build-routing"
    if rule is not None and rule.get("disposition"):
        if shape != "declarations" or rule["disposition"] in {
            "stock-inactive", "compiled-no-stock-caller-found", "unresolved",
        }:
            return rule["disposition"]
    if shape == "declarations":
        return "declarations"
    return "unresolved"


def create_audit(base, manifest):
    paths = sorted(
        (row["path"] for row in manifest["unmapped"]),
        key=lambda path: (str(PurePosixPath(path).parent), PurePosixPath(path).name),
    )
    inventory = {row["path"]: row["lines"] for row in manifest["unmapped"]}
    if len(paths) != 631 or len(inventory) != 631:
        raise ValueError("Expected exactly 631 distinct inventory files")
    sources = {
        path.relative_to(base).as_posix(): Source(path.read_bytes())
        for path in sorted(base.rglob("*"))
        if path.suffix.lower() in QC_EXTENSIONS and path.is_file()
    }
    contexts = {path: line_context(source) for path, source in sources.items()}
    includes = {path: list(include_edges(path, source, sources)) for path, source in sources.items()}
    reached = build_paths(includes, contexts)
    symbols = {path: find_symbols(source) for path, source in sources.items()}
    refs = reference_index(symbols, sources, contexts, reached)
    rules = load_rules(base)
    used_rules = set()
    rows = []
    for path in paths:
        source = sources[path]
        if len(source.lines) != inventory[path]:
            raise ValueError(f"Line count differs from the coverage inventory for {path}")
        matching = [rule for rule in rules if fnmatchcase(path, rule["pattern"])]
        comparison_ids = [rule["id"] for rule in matching]
        used_rules.update(comparison_ids)
        shape = source_shape(source)
        semantic_lines = [
            line for line, content in enumerate(source.directive_lines, 1)
            if content.strip() and not STRUCTURAL.match(content.lstrip())
        ]
        targets = [
            target for target in TARGETS if target in reached[path]
            and any(target in contexts[path][line - 1][0] for line in semantic_lines)
        ]
        declarations = []
        for line, kind, name in symbols[path]:
            possible, guards = contexts[path][line - 1]
            live_targets = [target for target in possible if target in reached[path]]
            declaration = {"line": line, "kind": kind, "name": name, "possible_targets": live_targets}
            if guards:
                declaration["guards"] = guards
            names = reference_names(kind, name)
            if kind == "macro-invocation" and names:
                declaration["generated_names"] = names
            if names:
                available = sorted({
                    (ref[0], ref[1], ref[2], tuple(ref[3]), tuple(ref[4]))
                    for indexed_name in names for ref in refs[indexed_name]
                    if (ref[0], ref[1]) != (path, line)
                })
                declaration["reference_counts"] = {
                    "possible_build": sum(bool(ref[3]) for ref in available),
                    "outside_stock_build_or_disabled": sum(not ref[3] for ref in available),
                    "external_possible_calls": sum(
                        bool(ref[3]) and ref[0] != path and ref[2] == "call-shaped"
                        for ref in available
                    ),
                }
                # Put external, build-possible call syntax first, then preserve a sample of disabled references.
                possible_refs = sorted(
                    (ref for ref in available if ref[3]),
                    key=lambda ref: (ref[0] == path, ref[2] != "call-shaped", ref[0], ref[1]),
                )
                disabled_refs = [ref for ref in available if not ref[3]]
                chosen = possible_refs[:2] + disabled_refs[:1]
                if chosen:
                    declaration["reference_examples"] = [
                        {"path": r[0], "line": r[1], "kind": r[2], "possible_targets": r[3], "guards": r[4]}
                        for r in chosen
                    ]
            declarations.append(declaration)
        rows.append({
            "path": path,
            "lines": len(source.lines),
            "sha256": sha256(source.content).hexdigest(),
            "source_shape": shape,
            "disposition": disposition(shape, matching[0] if matching else None),
            "stock_include_paths": reached[path],
            "possible_semantic_targets": targets,
            "include_directives": [
                {"line": line, "include": literal, "resolved_path": child,
                 "guards": contexts[path][line - 1][1]}
                for line, literal, child in includes[path]
            ],
            "semantic_line_anchors": semantic_lines[:8],
            "symbols": declarations,
            "comparisons": comparison_ids,
        })
    missing_rules = {rule["id"] for rule in rules} - used_rules
    if missing_rules:
        raise ValueError(f"Unused comparison rules: {sorted(missing_rules)}")
    unreviewed = [row["path"] for row in rows if row["source_shape"] == "semantic" and not row["comparisons"]]
    if unreviewed:
        raise ValueError(f"Semantic files lacking a native or scope comparison: {unreviewed}")
    return {
        "schema_version": 1,
        "reference": manifest["reference"],
        "inventory_path": "planning/parity/unmapped-audit/cohort.tsv",
        "stock_build_anchors": [
            read_anchor(("Makefile", "progs.inc"), base),
            read_anchor(("tools/qcc.sh", 'XONOTIC="${XONOTIC:-1}"'), base),
            read_anchor(("tools/qcc.sh", 'DEFS=("-D'), base),
            read_anchor(("tools/qcc.sh", 'ENABLE_DEBUG'), base),
        ],
        "summary": dict(sorted(Counter(row["disposition"] for row in rows).items())),
        "dispatch_anchors": {
            "registry": read_anchor(("lib/registry.qh", "#define REGISTER_5"), base),
            "method": read_anchor(("lib/oo.qh", "#define METHOD(cname"), base),
            "spawn_function": read_anchor(("lib/spawnfunc.qh", "#define spawnfunc("), base),
            "network": read_anchor(("lib/net.qh", "this.m_read = Net_Handle_"), base),
            "initialization": read_anchor(("lib/static.qh", "#define STATIC_INIT("), base),
            "commands": read_anchor(("common/command/reg.qh", "#define GENERIC_COMMAND("), base),
        },
        "native_routing_anchors": [
            read_anchor(("planning/decisions/ADR-0001-rewrite-strategy.md", "## Decision"), ROOT),
            read_anchor(("planning/decisions/ADR-0003-source-generators.md", "## Decision"), ROOT),
            read_anchor(("src/VortexArena.Common/Gameplay/Registries.cs", "public static void Bootstrap"), ROOT),
        ],
        "comparisons": {rule["id"]: {key: val for key, val in rule.items() if key != "id"} for rule in rules},
        "files": rows,
    }


def directory_table(files):
    groups = (
        ("Routing or empty", ("build-routing", "empty")),
        ("Declarations", ("declarations",)),
        ("Architecture replaced", ("architecture-replaced",)),
        ("Native counterpart", ("native-counterpart",)),
        ("Documented gap", ("documented-gap",)),
        ("Inactive or no stock caller found", ("stock-inactive", "compiled-no-stock-caller-found")),
        ("Unresolved", ("unresolved",)),
    )
    by_directory = defaultdict(Counter)
    total = Counter()
    for row in files:
        counts = by_directory[row["path"].split("/", 1)[0]]
        for bucket in (counts, total):
            bucket["files"] += 1
            bucket["lines"] += row["lines"]
            bucket[row["disposition"]] += 1

    headings = ["QC directory", "Files", "Lines", *(name for name, _ in groups)]
    lines = ["| " + " | ".join(headings) + " |", "|---" + "|---:" * (len(headings) - 1) + "|"]
    for directory, counts in [*sorted(by_directory.items()), (None, total)]:
        amounts = [sum(counts[key] for key in keys) for _, keys in groups]
        if sum(amounts) != counts["files"]:
            raise ValueError("A file disposition is missing from the directory table")
        cells = [f"{n:,}" for n in [counts["files"], counts["lines"], *amounts]]
        if directory is None:
            cells = [f"**{cell}**" for cell in ["Total", *cells]]
        else:
            cells.insert(0, f"`{directory}`")
        lines.append("| " + " | ".join(cells) + " |")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--check", action="store_true", help="verify the checked-in audit without writing")
    args = parser.parse_args()
    base = args.base.resolve()
    if (base / "qcsrc").is_dir():
        base /= "qcsrc"
    cohort = (AUDIT / "cohort.tsv").read_text(encoding="utf-8").splitlines()
    pin = cohort[0].removeprefix("# Xonotic data ")
    records = (line.split("\t") for line in cohort if line and not line.startswith("#"))
    manifest = {"reference": pin, "unmapped": [{"path": path, "lines": int(lines)} for path, lines in records]}
    if PIN != manifest["reference"] or base.name != "qcsrc":
        raise SystemExit(f"Expected the QC cohort and qcsrc checkout at {PIN}")
    verify_qc_inputs(base.parent)
    result = create_audit(base, manifest)
    output = AUDIT / "dispositions.json"
    header = {key: val for key, val in result.items() if key != "files"}
    # One physical line per QC file keeps the directory-ordered inventory easy to diff and grep.
    rendered = json.dumps(header, indent=2)[:-2] + ',\n  "files": [\n'
    rendered += ",\n".join("    " + json.dumps(row, separators=(",", ":")) for row in result["files"])
    rendered += "\n  ]\n}\n"
    readme = AUDIT / "README.md"
    previous = readme.read_text(encoding="utf-8")
    report, replacements = re.subn(
        r"^\| QC directory \|[^\n]*\n(?:\|[^\n]*\n)*?\| \*\*Total\*\* \|[^\n]*",
        lambda _: directory_table(result["files"]), previous, flags=re.MULTILINE)
    if replacements != 1:
        raise SystemExit("Expected one directory table in the audit README")
    for path, contents in ((output, rendered), (readme, report)):
        if args.check:
            if path.read_text(encoding="utf-8") != contents:
                raise SystemExit(f"Audit is stale: {path.relative_to(ROOT)}")
        else:
            path.write_text(contents, encoding="utf-8")
    print(f"Verified {len(result['files'])} QC files at {PIN}: {result['summary']}")


if __name__ == "__main__":
    main()

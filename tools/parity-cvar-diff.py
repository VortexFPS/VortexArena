#!/usr/bin/env python3
"""Cvar-default differ: Base cfg chain vs the port's cfg chain vs port code literals.

The recurring parity bug class this mechanizes: a value the LIVE path actually uses diverges
from Base — because the port's cfg tree was edited, because a code fallback default disagrees
with the shipped cfg (the `hud_panel_centerprint_fade_in 0.15 vs _hud_common.cfg 0` class), or
because the port never reads the cvar at all (the dead-graphics-settings class). Each was
previously found one at a time by audit agents; this finds them wholesale.

Method: a Python port of the ConfigInterpreter grammar subset (set/seta/set_temp/seta_temp/setp,
bare `name value` behind the same NonCvarCommands denylist, exec recursion, // and /* */ comments,
quotes+escapes, `;` separators, aliases with $-expansion) is run over BOTH trees with the SAME
entry files the port actually loads (ConfigLoader/MenuState). Because both sides go through the
same simulation, simulation imperfections cancel out of the Base-vs-port comparison.

Sections produced:
  1. chain divergence  — cfg files exec'd on one side only, or with differing text
  2. value diffs       — same cvar, different effective value between the trees
  3. one-sided cvars   — set by only one side's chain (grouped by prefix)
  4. code-default mismatches — port code literal (Register/new/fallback-read) != Base effective
  5. never-read        — Base-effective cvars with no occurrence anywhere in port source

Known/intended divergences are suppressed via planning/parity/cvar-diff-known.yaml.

Outputs planning/parity/CVAR-DIFF.md (+ _cvar-diff.json full lists).
Run: python tools/parity-cvar-diff.py
"""
from __future__ import annotations
import datetime, json, os, pathlib, re, sys
try:
    import yaml
except ImportError:
    sys.exit("PyYAML required: pip install pyyaml")

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "planning" / "parity"
# Env overrides so the tool runs from a git worktree (whose ROOT has no assets/ junction and whose
# parent is .claude/worktrees, not the Xonotic checkout): XON_PORT_DATA / XON_BASE_DATA.
PORT_DATA = pathlib.Path(os.environ.get("XON_PORT_DATA", ROOT / "assets" / "data" / "xonotic-data.pk3dir"))
BASE_DATA = pathlib.Path(os.environ.get("XON_BASE_DATA", ROOT.parent / "Base" / "data" / "xonotic-data.pk3dir"))
KNOWN_FILE = OUT / "cvar-diff-known.yaml"

# The port's real entry files (ConfigLoader.ServerEntry/NotificationsEntry + MenuState client boot).
ENTRIES = ["xonotic-client.cfg", "xonotic-server.cfg", "notifications.cfg"]

# Mirror of ConfigInterpreter.NonCvarCommands — keep in sync with ConfigInterpreter.cs.
NON_CVAR_COMMANDS = {
    "bind", "unbind", "unbindall", "in_bind", "in_bindmap", "in_releaseall", "bindlist",
    "sv_cmd", "cl_cmd", "menu_cmd", "cmd", "rcon", "rcon_secure",
    "map", "changelevel", "gotomap", "gametype", "kill", "give", "god", "noclip",
    "say", "say_team", "tell", "name", "color", "playermodel", "playerskin",
    "kick", "ban", "banlist", "status", "quit", "disconnect", "connect", "reconnect",
    "defer", "wait", "echo", "toggle", "cycle", "inc", "dec", "play", "play2", "playall",
    "cd", "sv_startdownload", "prvm_language", "fpredict", "fdisconnect", "togglemenu",
    "alias", "unalias", "exec", "set", "seta",
}


def split_commands(text: str) -> list[str]:
    """ConfigInterpreter.SplitIntoCommands: strip //, /* */ outside quotes; split on ; and newlines."""
    commands, sb, in_q, i, n = [], [], False, 0, len(text)

    def flush():
        s = "".join(sb).strip()
        if s:
            commands.append(s)
        sb.clear()

    while i < n:
        c = text[i]
        if in_q:
            if c == "\\" and i + 1 < n:
                sb.append(c); sb.append(text[i + 1]); i += 2; continue
            if c == '"':
                in_q = False
            sb.append(c); i += 1; continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i += 2
            continue
        if c == '"':
            in_q = True; sb.append(c); i += 1; continue
        if c in ("\n", ";"):
            flush(); i += 1; continue
        sb.append(c); i += 1
    flush()
    return commands


ESCAPES = {"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\"}


def tokenize(line: str) -> list[str]:
    """ConfigInterpreter.Tokenize: quoted tokens with escapes; empty quoted token preserved."""
    tokens, sb, started, in_q = [], [], False, False
    i, n = 0, len(line)
    while i < n:
        c = line[i]
        if in_q:
            if c == "\\" and i + 1 < n:
                sb.append(ESCAPES.get(line[i + 1], line[i + 1])); i += 2; continue
            if c == '"':
                in_q = False; i += 1; continue
            sb.append(c); i += 1; continue
        if c == '"':
            in_q = True; started = True; i += 1; continue
        if c in " \t\r\n":
            if started:
                tokens.append("".join(sb)); sb.clear(); started = False
            i += 1; continue
        started = True; sb.append(c); i += 1
    if started:
        tokens.append("".join(sb))
    return tokens


class CfgSim:
    """Python mirror of ConfigInterpreter over one data tree. values[name] = (value, source_file)."""

    def __init__(self, root: pathlib.Path):
        self.root = root
        self.values: dict[str, tuple[str, str]] = {}
        self.aliases: dict[str, str] = {"if_client": "${* asis}", "if_dedicated": "${* asis}"}
        self.executed: list[str] = []
        self.missing: list[str] = []
        self.exec_stack: set[str] = set()
        self.alias_depth = 0
        # case-insensitive cvar names (DP cvars are case-insensitive)
        self._canon: dict[str, str] = {}

    def read(self, path: str) -> str | None:
        p = self.root / path
        try:
            return p.read_text(encoding="utf-8", errors="replace") if p.is_file() else None
        except OSError:
            return None

    def set_cvar(self, name: str, value: str, src: str):
        key = self._canon.setdefault(name.lower(), name)
        self.values[key] = (value, src)

    def get_cvar(self, name: str) -> str:
        key = self._canon.get(name.lower())
        return self.values[key][0] if key else ""

    # -- $-expansion (ConfigInterpreter.Expand subset) --
    def expand(self, s: str, args: list[str] | None) -> str:
        if "$" not in s:
            return s
        out, i, n = [], 0, len(s)
        while i < n:
            c = s[i]
            if c != "$":
                out.append(c); i += 1; continue
            if i + 1 < n and s[i + 1] == "$":
                out.append("$"); i += 2; continue
            if i + 1 >= n:
                out.append("$"); break
            if s[i + 1] == "{":
                close = s.find("}", i + 2)
                if close < 0:
                    out.append(s[i:]); break
                out.append(self._resolve(s[i + 2:close].strip(), args))
                i = close + 1
            else:
                j = i + 1
                if j < n and s[j] == "*":
                    j += 1
                else:
                    while j < n and (s[j].isalnum() or s[j] == "_"):
                        j += 1
                if j == i + 1:
                    out.append("$"); i += 1; continue
                out.append(self._resolve(s[i + 1:j], args))
                i = j
        return "".join(out)

    def _resolve(self, inner: str, args: list[str] | None) -> str:
        sel = inner.split(" ", 1)[0]
        if not sel:
            return ""
        if sel == "*":
            return self._join(args, 1)
        if len(sel) >= 2 and sel[-1] == "-" and sel[:-1].isdigit():
            return self._join(args, int(sel[:-1]))
        if sel.lstrip("-").isdigit():
            idx = int(sel)
            return args[idx] if args and 0 <= idx < len(args) else ""
        return self.get_cvar(sel)

    @staticmethod
    def _join(args: list[str] | None, frm: int) -> str:
        if not args or frm >= len(args):
            return ""
        parts = []
        for a in args[frm:]:
            parts.append(f'"{a}"' if (not a or " " in a or "\t" in a) else a)
        return " ".join(parts)

    # -- dispatch --
    def exec_file(self, path: str):
        if len(self.exec_stack) >= 48 or path.lower() in self.exec_stack:
            return
        text = self.read(path)
        if text is None:
            self.missing.append(path)
            return
        self.exec_stack.add(path.lower())
        self.executed.append(path)
        try:
            for cmd in split_commands(text):
                self.run_command(cmd, None, path)
        finally:
            self.exec_stack.discard(path.lower())

    def run_command(self, command: str, args: list[str] | None, src: str):
        raw = tokenize(command)
        if not raw:
            return
        if raw[0].lower() == "alias":
            if len(raw) >= 2:
                name = self.expand(raw[1], args) if "$" in raw[1] else raw[1]
                body = raw[2] if len(raw) == 3 else " ".join(raw[2:]) if len(raw) > 3 else ""
                self.aliases[name.lower()] = body.replace("$$", "$")
            return
        expanded = self.expand(command, args)
        argv = tokenize(expanded)
        if not argv:
            return
        cmd = argv[0].lower()
        if cmd in ("set", "seta", "set_temp", "seta_temp", "setp"):
            if len(argv) >= 2:
                self.set_cvar(argv[1], argv[2] if len(argv) >= 3 else "", src)
            return
        if cmd == "unalias":
            if len(argv) >= 2:
                self.aliases.pop(argv[1].lower(), None)
            return
        if cmd == "exec":
            if len(argv) >= 2:
                self.exec_file(argv[1])
            return
        if cmd in ("unset", "cvar_reset", "reset"):
            if len(argv) >= 2:
                self.set_cvar(argv[1], "", src)
            return
        if cmd in self.aliases and cmd not in NON_CVAR_COMMANDS:
            if self.alias_depth < 64:
                self.alias_depth += 1
                try:
                    for sub in split_commands(self.aliases[cmd]):
                        self.run_command(sub, argv, src)
                finally:
                    self.alias_depth -= 1
            return
        if len(argv) >= 2 and cmd not in NON_CVAR_COMMANDS:
            self.set_cvar(argv[0], argv[1], src)  # bare `name value`


def norm(v: str) -> str:
    """Value normalization for comparison: numeric-equal strings compare equal ('0.60'=='0.6')."""
    s = v.strip()
    try:
        f = float(s)
        return repr(f)
    except ValueError:
        return s


def scan_code_literals() -> tuple[dict[str, list[tuple[str, str, str]]], set[str], list[re.Pattern]]:
    """Scan src/+game .cs for (a) name->[(default, file:line, kind)] code-default shapes,
    (b) the set of ALL string literals, and (c) dynamic-name patterns from interpolated strings
    ($"g_balance_{NetName}_reload_ammo" -> ^g_balance_.+_reload_ammo$) — both used by never-read."""
    reg_re = re.compile(r'\bRegister\(\s*"([A-Za-z_][\w+.]*)"\s*,\s*"([^"]*)"')
    new_re = re.compile(r'\bnew\(\s*"([A-Za-z_][\w+.]*)"\s*,\s*"([^"]*)"')
    fb_re = re.compile(r'\b(?:GetFloat|GetInt|Float|Int|CvarOr|Cvar)\(\s*"([A-Za-z_][\w+.]*)"\s*,\s*(-?\d+(?:\.\d+)?)f?\s*[),]')
    lit_re = re.compile(r'"([A-Za-z_][\w+.]*)"')
    interp_re = re.compile(r'\$"([A-Za-z_][\w+.{}]*\{[^"]*)"')
    defaults: dict[str, list[tuple[str, str, str]]] = {}
    literals: set[str] = set()
    dynamic_srcs: set[str] = set()
    for top in ("src", "game"):
        for p in (ROOT / top).rglob("*.cs"):
            if any(part in ("obj", "bin", ".godot") for part in p.parts):
                continue
            try:
                text = p.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            for m in lit_re.finditer(text):
                literals.add(m.group(1).lower())
            for m in interp_re.finditer(text):
                dynamic_srcs.add(m.group(1))
            rel = p.relative_to(ROOT).as_posix()
            for regex, kind in ((reg_re, "Register"), (new_re, "table"), (fb_re, "fallback")):
                for m in regex.finditer(text):
                    line = text.count("\n", 0, m.start()) + 1
                    defaults.setdefault(m.group(1).lower(), []).append((m.group(2), f"{rel}:{line}", kind))
    dynamic = []
    for tmpl in dynamic_srcs:
        parts = [re.escape(seg) for seg in re.split(r"\{[^{}]*\}", tmpl)]
        if len(parts) >= 2 and any(parts):  # at least one hole and one literal segment
            dynamic.append(re.compile("^" + ".+".join(parts) + "$", re.IGNORECASE))
    return defaults, literals, dynamic


def main():
    if not BASE_DATA.is_dir():
        sys.exit(f"Base data tree not found: {BASE_DATA}")
    known_patterns: list[tuple[str, str]] = []
    if KNOWN_FILE.exists():
        for e in (yaml.safe_load(KNOWN_FILE.read_text(encoding="utf-8")) or {}).get("known") or []:
            known_patterns.append(((e.get("name") or "").lower(), e.get("note", "")))

    def is_known(name: str) -> bool:
        import fnmatch as _fn
        low = name.lower()
        return any(_fn.fnmatch(low, pat) for pat, _ in known_patterns)

    base, port = CfgSim(BASE_DATA), CfgSim(PORT_DATA)
    for entry in ENTRIES:
        base.exec_file(entry)
        port.exec_file(entry)

    # 1. chain divergence
    bset, pset = {f.lower() for f in base.executed}, {f.lower() for f in port.executed}
    only_base, only_port = sorted(bset - pset), sorted(pset - bset)
    differing_text = []
    for f in sorted(bset & pset):
        if (base.read(f) or "") != (port.read(f) or ""):
            differing_text.append(f)

    # 2 + 3. effective value diffs / one-sided
    value_diffs, base_only, port_only = [], [], []
    for name in sorted(set(base.values) | set(port.values), key=str.lower):
        b, p = base.values.get(name), port.values.get(name)
        if b and p:
            if norm(b[0]) != norm(p[0]) and not is_known(name):
                value_diffs.append((name, b[0], p[0], p[1]))
        elif b:
            if not is_known(name):
                base_only.append(name)
        else:
            if not is_known(name):
                port_only.append(name)

    # 4. code-default mismatches (port code literal vs Base effective)
    defaults, literals, dynamic = scan_code_literals()
    code_mismatch = []
    for name, (bval, bsrc) in base.values.items():
        for dval, where, kind in defaults.get(name.lower(), []):
            if norm(dval) != norm(bval) and not is_known(name):
                code_mismatch.append((name, bval, dval, where, kind))
    code_mismatch.sort(key=lambda r: (r[3],))

    # 5. never-read: Base-effective gameplay cvars with no literal occurrence in port source and
    # no interpolated-pattern match ($"g_balance_{X}_damage" covers the per-weapon reads).
    def referenced(n: str) -> bool:
        low = n.lower()
        return low in literals or any(rx.match(n) for rx in dynamic)
    never = [n for n in sorted(base.values, key=str.lower)
             if not referenced(n) and not is_known(n)
             and not n.lower().startswith(("notification_", "g_physics_"))]

    today = datetime.date.today().isoformat()
    prefix_of = lambda n: (n.split("_", 1)[0] + "_") if "_" in n else "(bare)"
    md = [
        "# Cvar default diff — Base vs port",
        "",
        f"_Generated {today} by `tools/parity-cvar-diff.py`. Entries simulated on both trees:"
        f" {', '.join(f'`{e}`' for e in ENTRIES)} (the port's real boot chain — ConfigLoader/MenuState)._",
        "",
        f"Base tree: `{BASE_DATA}` — {len(base.values)} effective cvars from {len(base.executed)} files.",
        f"Port tree: `{PORT_DATA}` — {len(port.values)} effective cvars from {len(port.executed)} files.",
        "",
        "Findings are LEADS for triage, not verdicts: confirm each on the live path, then either fix it",
        "or record it in [cvar-diff-known.yaml](cvar-diff-known.yaml) (intended divergences) so it stops",
        "re-flagging — the same discipline as `intended_divergence` in the registry.",
        "",
        "## 1. Chain divergence (files exec'd on one side only, or with differing text)",
        "",
    ]
    md += [f"- exec'd by Base only: `{f}`" for f in only_base] or []
    md += [f"- exec'd by port only: `{f}`" for f in only_port] or []
    md += [f"- text differs: `{f}`" for f in differing_text] or []
    if not (only_base or only_port or differing_text):
        md.append("- none — the trees exec identical files with identical text")
    if base.missing or port.missing:
        md.append(f"- missing exec targets: Base {sorted(set(base.missing))} / port {sorted(set(port.missing))}")

    md += ["", f"## 2. Effective value diffs ({len(value_diffs)})", ""]
    if value_diffs:
        md += ["| cvar | Base | port | set by |", "|---|---|---|---|"]
        md += [f"| `{n}` | `{b}` | `{p}` | {src} |" for n, b, p, src in value_diffs]
    else:
        md.append("none.")

    md += ["", f"## 3. One-sided cvars (Base-only: {len(base_only)}, port-only: {len(port_only)})", "",
           "Base-only = set by Base's chain but not the port's (unconsumed defaults);",
           "port-only = port additions. Grouped by prefix; full lists in `_cvar-diff.json`.", ""]
    for label, lst in (("Base-only", base_only), ("port-only", port_only)):
        groups: dict[str, int] = {}
        for n in lst:
            groups[prefix_of(n)] = groups.get(prefix_of(n), 0) + 1
        top = sorted(groups.items(), key=lambda kv: -kv[1])[:15]
        md.append(f"- **{label}**: " + (", ".join(f"`{k}`×{v}" for k, v in top) if top else "none"))

    md += ["", f"## 4. Code-default mismatches vs Base effective ({len(code_mismatch)})", "",
           "A C# literal default (Register table / Cvars.Defaults / numeric fallback read) that disagrees",
           "with the Base effective value. Bites on any path that reads before/without the cfg chain",
           "(tests, headless boots, unset cvars) — the `hud_panel_centerprint_fade_in` class.", ""]
    if code_mismatch:
        md += ["| cvar | Base effective | code literal | where | kind |", "|---|---|---|---|---|"]
        md += [f"| `{n}` | `{b}` | `{d}` | {w} | {k} |" for n, b, d, w, k in code_mismatch[:200]]
        if len(code_mismatch) > 200:
            md.append(f"| … | | | +{len(code_mismatch) - 200} more (see `_cvar-diff.json`) | |")
    else:
        md.append("none.")

    md += ["", f"## 5. Base-effective cvars never referenced in port source ({len(never)})", "",
           "No string-literal occurrence anywhere under src/ or game/ — dead-setting candidates (the",
           "graphics-stub class) or subsystems the port genuinely lacks. Interpolated reads",
           "($\"g_balance_{X}_damage\") ARE pattern-matched; `+`-concatenated names are NOT (a lead here",
           "may be read via string concat — check before filing). `g_physics_<set>_*` and",
           "`notification_*` are excluded wholesale. Grouped by prefix; full list in `_cvar-diff.json`.", ""]
    groups: dict[str, list[str]] = {}
    for n in never:
        groups.setdefault(prefix_of(n), []).append(n)
    for k in sorted(groups, key=lambda k: -len(groups[k])):
        sample = ", ".join(f"`{x}`" for x in groups[k][:8]) + (" …" if len(groups[k]) > 8 else "")
        md.append(f"- `{k}` ×{len(groups[k])}: {sample}")

    (OUT / "CVAR-DIFF.md").write_text("\n".join(md), encoding="utf-8")
    (OUT / "_cvar-diff.json").write_text(json.dumps({
        "generated": today,
        "value_diffs": [{"name": n, "base": b, "port": p, "src": s} for n, b, p, s in value_diffs],
        "base_only": base_only, "port_only": port_only,
        "code_mismatch": [{"name": n, "base": b, "code": d, "where": w, "kind": k}
                          for n, b, d, w, k in code_mismatch],
        "never_read": never,
    }, indent=1), encoding="utf-8")
    print(f"base={len(base.values)} port={len(port.values)} | value_diffs={len(value_diffs)} "
          f"base_only={len(base_only)} port_only={len(port_only)} code_mismatch={len(code_mismatch)} "
          f"never_read={len(never)} | chain: base_only_files={len(only_base)} "
          f"port_only_files={len(only_port)} differing={len(differing_text)}")


if __name__ == "__main__":
    main()

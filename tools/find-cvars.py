#!/usr/bin/env python3
"""Console-variable (cvar) inventory scanner for the VortexArena port.

Walks the C# source tree (src/, game/) and extracts every console variable the
port declares or reads, then groups them by prefix and prints an inventory. It
is the tool behind docs/CVARS.md — regenerate that doc's "Full inventory"
section by pasting this script's --markdown output.

WHY A HEURISTIC: cvars have no single registry. They are reached through many
shapes — Api.Cvars.GetFloat("x"), Cvars.Float("x"), c.Register("x", ...),
the server CvarDef table (new("x", ...)), const-string name constants
(const string FooCvar = "x"), and bare string-array tables (MoveVarsBlock,
the sentcvar allowlist). Several of those shapes are shared with NON-cvars
(entity classnames like func_door, effect names like TR_ROCKET, which also use
.Register(...)). So extraction is context + prefix driven:

  * TRUSTED context (a receiver whose name contains "cvar", or the new(...)
    table in Cvars.cs) -> the name is a cvar even with no prefix. This is how
    bare cvars (timelimit, teamplay, developer) are found.
  * PREFIXED literal anywhere -> a string starting with a known cvar prefix
    (cl_ sv_ g_ hud_ crosshair_ r_ snd_ vid_ ...) is taken as a cvar. This is
    how the bulk (and the bare string-array tables) are found.
  * A curated BARE_ALLOWLIST backstops no-prefix cvars that appear only in
    ambiguous contexts (showfps, volume, fov, ...).
  * ENTITY_PREFIXES / NAME_DENYLIST drop the known non-cvar lookalikes.

Run with --show-rejects to audit: it prints names captured in a cvar context
but filtered out, so a genuinely-missed bare cvar can be folded into
BARE_ALLOWLIST. Nothing here changes program behaviour; it only reads source.

Some cvars are DYNAMICALLY named and cannot be enumerated statically; they are
reported separately:
  * snd_channel{N}volume  (built in a 0..9 loop; expanded here for convenience)
  * g_physics_<set>_<var> (preset overrides, e.g. g_physics_cpma_maxspeed)
  * notification_<CHOICE>  (one per kill-message choice)

Usage:
    python tools/find-cvars.py                 # grouped text report
    python tools/find-cvars.py --markdown      # markdown for docs/CVARS.md
    python tools/find-cvars.py --json          # machine-readable
    python tools/find-cvars.py --show-rejects  # audit filtered-out names
    python tools/find-cvars.py --include-tests # also scan tests/

The script lives in tools/ and assumes the port root is its parent directory.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field

# ---------------------------------------------------------------------------
# Classification tables
# ---------------------------------------------------------------------------

# Known cvar prefixes (ordered longest-first so hud_panel_ wins over hud_ when
# we attribute a name to a group). Every prefix here is assumed to NOT collide
# with an entity classname or effect name (see ENTITY_PREFIXES).
CVAR_PREFIXES = [
    "crosshair_", "hud_panel_", "hud_", "cl_", "sv_", "g_", "r_", "snd_",
    "vid_", "menu_", "con_", "net_", "bot_", "sys_", "music_", "prvm_",
    "gl_", "scr_", "in_", "joy_", "m_",
]

# Leading-underscore (private/internal) cvar prefixes we trust. A bare leading
# underscore is NOT enough — _norm / _gloss / _skybox are texture/material
# suffixes, not cvars — so we only accept these specific internal namespaces
# plus the explicit names in BARE_ALLOWLIST.
UNDERSCORE_PREFIXES = ["_cl_", "_hud_", "_campaign_", "_menu_"]

# Structural (non-cvar) prefixes that share the .Register(...) / string-literal
# shape: entity classnames and the like. Anything starting with one of these is
# never a cvar.
ENTITY_PREFIXES = [
    "func_", "trigger_", "target_", "monster_", "turret_", "vehicle_",
    "item_", "weapon_", "nexball_", "ball_", "info_", "misc_", "path_",
    "dom_", "onslaught_", "invasion_", "relay_", "team_", "trigger",
]

# No-prefix cvars that only ever appear in ambiguous contexts. Curated; audit
# with --show-rejects and extend as needed. Stems ending in "_" match by
# startswith (e.g. "timelimit_" covers timelimit_overtime, _min, _max, ...).
BARE_ALLOWLIST = {
    # match flow / rules (server-authoritative, g_-in-spirit but historically bare)
    "timelimit", "timelimit_", "fraglimit", "leadlimit", "leadlimit_and_fraglimit",
    "teamplay", "teamplay_mode", "skill", "skill_auto", "samelevel", "lastlevel",
    "minplayers", "minplayers_per_team", "maxplayers", "pausable", "deathmatch",
    "hostname", "mapname", "modname", "nextmap",
    # client view / input / engine
    "fov", "sensitivity", "developer", "name",
    "showfps", "showping", "showposition", "showdate", "showtime", "prvm_language",
    # audio (the rest of the family is snd_*)
    "volume", "bgmvolume", "mastervolume",
}

# Names that pass the prefix test but are console COMMANDS, prefix/category
# labels (used by cvarlist-style grouping), or other non-cvars. Note: any name
# ENDING in "_" is dropped automatically (it is a bare prefix, never a real
# cvar) — see add(); this set is only for the no-trailing-underscore lookalikes.
NAME_DENYLIST = {
    "cl_cmd", "sv_cmd", "menu_cmd", "g_cmd", "common_cmd",
    "g_balance",  # category label (cvar reset/grouping), not a cvar
}

# Directory -> conceptual scope, for the cross-boundary report. Longest path
# wins. Anything unmatched under game/ is the Godot "host" layer (boot, asset
# loading) which legitimately touches both sides and is excluded from the
# cross-boundary signal.
SCOPE_BY_DIR = [
    ("game/client", "client"),
    ("game/hud", "client"),
    ("game/menu", "client"),
    ("game/console", "client"),
    ("src/VortexArena.Server", "server"),
    ("src/VortexArena.Common", "shared"),
    ("src/VortexArena.Engine", "shared"),
    ("game/net", "net"),
    ("src/VortexArena.Net", "net"),
    ("src/VortexArena.Formats", "shared"),
]

PRUNE_DIRS = {".git", ".claude", ".godot", ".idea", ".vs", ".vscode", "obj",
              "bin", "node_modules", ".tmp", "build-obj", "movement-ref",
              "particles-ref"}

# ---------------------------------------------------------------------------
# Regexes
# ---------------------------------------------------------------------------

# A cvar-name-shaped literal: lowercase start (optionally one leading _), then
# lowercase/digit/underscore. Permits a single {..} interpolation hole so we can
# detect dynamically-built names (snd_channel{ch}volume).
_NAME = r'[_a-z][a-z0-9_]*(?:\{[^}"]*\}[a-z0-9_]*)?'

# TRUSTED: receiver identifier contains "cvar"/"cvars" (Cvars., Api.Cvars.,
# world.Services.Cvars., cvars., MenuState.Cvars., CvarsImpl.), calling a cvar
# method. Captured names are accepted even without a prefix.
RE_TRUSTED_RECEIVER = re.compile(
    r'\b\w*[Cc]vars?(?:Impl)?\.'
    r'(?:GetFloat|GetString|GetDefault|Set|Register|Has|'
    r'Float|Bool|Int|String|FloatOr|Bound)\(\s*"(' + _NAME + r')"')

# TRUSTED: the CvarDef table in Cvars.cs is `new("name", ...)`.
RE_NEW_CTOR = re.compile(r'\bnew\(\s*"(' + _NAME + r')"')

# A registration call (any receiver) — used both to capture prefixed names and
# to mark a name as "registered".
RE_REGISTER = re.compile(r'\bRegister\(\s*"(' + _NAME + r')"')

# const string FooCvar = "name";  (and any const string = "<prefixed>")
RE_CONST_STRING = re.compile(r'\bconst\s+string\s+\w+\s*=\s*"(' + _NAME + r')"')

# Any string literal (the broad prefixed-literal scan + array tables).
RE_ANY_LITERAL = re.compile(r'"(' + _NAME + r')"')


def has_cvar_prefix(name: str) -> bool:
    if any(name.startswith(p) for p in ENTITY_PREFIXES):
        return False
    if any(name.startswith(p) for p in CVAR_PREFIXES):
        return True
    if any(name.startswith(p) for p in UNDERSCORE_PREFIXES):
        return True
    return False


def in_bare_allowlist(name: str) -> bool:
    if name in BARE_ALLOWLIST:
        return True
    return any(s.endswith("_") and name.startswith(s) for s in BARE_ALLOWLIST)


def group_for(name: str) -> str:
    """The prefix bucket a cvar is reported under."""
    for p in sorted(CVAR_PREFIXES, key=len, reverse=True):
        if name.startswith(p):
            return p.rstrip("_") if p != "g_" else "g_"
    for p in UNDERSCORE_PREFIXES:
        if name.startswith(p):
            return "_ (private/internal)"
    if name.startswith("_"):
        return "_ (private/internal)"
    return "(bare / no prefix)"


def scope_for(relpath: str) -> str:
    rp = relpath.replace("\\", "/")
    for prefix, scope in SCOPE_BY_DIR:
        if rp.startswith(prefix):
            return scope
        # The src/ project directories carry the old codename and the Tier-1 rename relocates them
        # (src/VortexArena.* -> src/VortexArena.*). Match either, because the alternative is the
        # fallthrough below quietly reclassifying every server/shared/net cvar as "host" - and since
        # docs/reference/CVARS.md treats the prefix as AUTHORITY, that is a wrong document rather than a
        # missing one. validate_scope_dirs() below turns the same drift into a loud failure.
        if prefix.startswith("src/VortexArena.") and rp.startswith(
            prefix.replace("src/VortexArena.", "src/VortexArena.", 1)
        ):
            return scope
    return "host"


def validate_scope_dirs(root) -> None:
    """Fail if a configured scope prefix matches no directory on disk.

    Without this, renaming a project silently degrades every cvar in it to the "host" scope instead of
    breaking - the classic fail-open. Accepts either codename, so it stays quiet across the rename and
    speaks up if a prefix becomes wrong for any other reason.
    """
    import pathlib

    root = pathlib.Path(root)
    missing = []
    for prefix, _ in SCOPE_BY_DIR:
        alt = prefix.replace("src/VortexArena.", "src/VortexArena.", 1)
        if not (root / prefix).is_dir() and not (root / alt).is_dir():
            missing.append(prefix)
    if missing:
        raise SystemExit(
            "error: these scope prefixes match no directory, so their cvars would silently be "
            "classified as 'host':\n  " + "\n  ".join(missing) +
            "\n  Update SCOPE_BY_DIR - a renamed project must not degrade the scope column."
        )


# ---------------------------------------------------------------------------
# Scan
# ---------------------------------------------------------------------------

@dataclass
class Cvar:
    name: str
    files: set = field(default_factory=set)       # "relpath:line"
    scopes: set = field(default_factory=set)       # client/server/shared/net/host
    registered: bool = False
    read: bool = False
    dynamic: bool = False                          # contains {..} interpolation

    def first_loc(self) -> str:
        return sorted(self.files)[0] if self.files else "?"


def scan(root: str, scan_dirs: list[str]):
    cvars: dict[str, Cvar] = {}
    rejects: dict[str, str] = {}   # name -> first "relpath:line" (audit aid)

    def add(name: str, relpath: str, lineno: int, *, registered=False,
            read=False):
        # A name ending in "_" is a bare prefix/stem (a cvarlist grouping label,
        # the FromCvars `prefix` arg, the hud_panel_<id> builder), never a real
        # cvar; the denylist catches the no-trailing-underscore lookalikes.
        if not name or name.endswith("_") or name in NAME_DENYLIST:
            return
        cv = cvars.get(name)
        if cv is None:
            cv = cvars[name] = Cvar(name)
        cv.files.add(f"{relpath}:{lineno}")
        cv.scopes.add(scope_for(relpath))
        cv.registered = cv.registered or registered
        cv.read = cv.read or read
        if "{" in name:
            cv.dynamic = True

    for scan_dir in scan_dirs:
        base = os.path.join(root, scan_dir)
        if not os.path.isdir(base):
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in PRUNE_DIRS]
            for fn in filenames:
                if not fn.endswith(".cs"):
                    continue
                full = os.path.join(dirpath, fn)
                relpath = os.path.relpath(full, root).replace("\\", "/")
                is_cvars_table = fn == "Cvars.cs"
                try:
                    with open(full, encoding="utf-8", errors="replace") as fh:
                        lines = fh.readlines()
                except OSError:
                    continue
                for lineno, line in enumerate(lines, 1):
                    # 1) TRUSTED receiver -> accept name as-is (catches bare).
                    for m in RE_TRUSTED_RECEIVER.finditer(line):
                        name = m.group(1)
                        if name in NAME_DENYLIST:
                            continue
                        is_reg = ".Register(" in line[:m.start() + 40] or \
                                 "Register(" in m.group(0)
                        add(name, relpath, lineno,
                            registered="Register" in m.group(0), read=True)
                    # 2) new("x",...) inside Cvars.cs is the CvarDef table.
                    if is_cvars_table:
                        for m in RE_NEW_CTOR.finditer(line):
                            name = m.group(1)
                            if name in NAME_DENYLIST:
                                continue
                            add(name, relpath, lineno, registered=True)
                    # 3) Register(...) (any receiver): prefix/bare gated.
                    for m in RE_REGISTER.finditer(line):
                        name = m.group(1)
                        if name in NAME_DENYLIST:
                            continue
                        if has_cvar_prefix(name) or in_bare_allowlist(name):
                            add(name, relpath, lineno, registered=True)
                        else:
                            rejects.setdefault(name, f"{relpath}:{lineno}")
                    # 4) const string FooCvar = "x"
                    for m in RE_CONST_STRING.finditer(line):
                        name = m.group(1)
                        if name in NAME_DENYLIST:
                            continue
                        if has_cvar_prefix(name) or in_bare_allowlist(name):
                            add(name, relpath, lineno, registered=is_cvars_table,
                                read=True)
                    # 5) broad prefixed-literal scan (array tables, reads, ...).
                    for m in RE_ANY_LITERAL.finditer(line):
                        name = m.group(1)
                        if name in NAME_DENYLIST:
                            continue
                        if has_cvar_prefix(name):
                            add(name, relpath, lineno, read=True)

    return cvars, rejects


# ---------------------------------------------------------------------------
# Dynamic-family expansion (names that can't be found as plain literals)
# ---------------------------------------------------------------------------

def expand_dynamic(cvars: dict[str, Cvar]) -> dict[str, Cvar]:
    """Turn the one captured template literal into the concrete family where the
    expansion is finite and known (snd_channel{N}volume, N=0..9)."""
    out = dict(cvars)
    tmpl = out.pop("snd_channel{ch}volume", None)
    if tmpl is not None:
        for n in range(10):
            name = f"snd_channel{n}volume"
            cv = out.get(name) or Cvar(name)
            cv.files |= tmpl.files
            cv.scopes |= tmpl.scopes
            cv.registered = True
            out[name] = cv
    # Drop any leftover template names from the report's concrete counts.
    for k in [k for k in out if "{" in k]:
        out.pop(k)
    return out


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

def grouped(cvars: dict[str, Cvar]) -> dict[str, list[Cvar]]:
    groups: dict[str, list[Cvar]] = defaultdict(list)
    for cv in cvars.values():
        groups[group_for(cv.name)].append(cv)
    for g in groups.values():
        g.sort(key=lambda c: c.name)
    return groups


# Group display order.
ORDER = ["g_", "sv_", "cl_", "hud_panel", "hud", "crosshair", "bot_", "snd",
         "r", "menu", "vid", "con", "net", "m", "sys", "music", "prvm", "gl",
         "scr", "in", "joy", "_ (private/internal)", "(bare / no prefix)"]


def order_key(g: str) -> int:
    return ORDER.index(g) if g in ORDER else len(ORDER)


def report_text(cvars, rejects, show_rejects):
    groups = grouped(cvars)
    total = len(cvars)
    print(f"VortexArena cvar inventory - {total} distinct cvars\n")
    for g in sorted(groups, key=order_key):
        items = groups[g]
        print(f"== {g}  ({len(items)}) " + "=" * max(0, 60 - len(g)))
        for cv in items:
            tags = []
            if not cv.registered:
                tags.append("UNREGISTERED")
            scopes = cv.scopes - {"host"}
            cross = ("client" in scopes) and bool(scopes & {"server", "shared"})
            if cross:
                tags.append("CROSS-BOUNDARY")
            tag = ("  [" + ", ".join(tags) + "]") if tags else ""
            print(f"   {cv.name}{tag}")
        print()
    cross = [c for c in cvars.values()
             if "client" in c.scopes and c.scopes & {"server", "shared"}]
    unreg = [c for c in cvars.values() if not c.registered]
    print(f"-- cross-boundary (client AND server/shared): {len(cross)} --")
    for cv in sorted(cross, key=lambda c: c.name):
        print(f"   {cv.name:40} {sorted(cv.scopes)}  {cv.first_loc()}")
    print(f"\n-- read but never registered: {len(unreg)} --")
    for cv in sorted(unreg, key=lambda c: c.name):
        print(f"   {cv.name:40} {cv.first_loc()}")
    if show_rejects:
        print(f"\n-- rejected from a cvar context ({len(rejects)}) "
              "[audit for missed bare cvars] --")
        for name in sorted(rejects):
            print(f"   {name:40} {rejects[name]}")


def report_markdown(cvars):
    groups = grouped(cvars)
    total = len(cvars)
    print(f"_Generated by `python tools/find-cvars.py` - {total} distinct "
          "cvars._\n")
    for g in sorted(groups, key=order_key):
        items = groups[g]
        print(f"### `{g}` ({len(items)})\n")
        for cv in items:
            notes = []
            if not cv.registered:
                notes.append("unregistered")
            if "client" in cv.scopes and cv.scopes & {"server", "shared"}:
                notes.append("cross-boundary")
            suffix = f"  _( {', '.join(notes)} )_" if notes else ""
            print(f"- `{cv.name}`{suffix}")
        print()


def report_json(cvars):
    out = {}
    for name, cv in sorted(cvars.items()):
        out[name] = {
            "group": group_for(name),
            "registered": cv.registered,
            "scopes": sorted(cv.scopes),
            "cross_boundary": "client" in cv.scopes
            and bool(cv.scopes & {"server", "shared"}),
            "first_loc": cv.first_loc(),
            "locations": sorted(cv.files),
        }
    print(json.dumps(out, indent=2))


def main(argv=None):
    ap = argparse.ArgumentParser(description="Scan the port for console variables.")
    ap.add_argument("--markdown", action="store_true", help="emit markdown")
    ap.add_argument("--json", action="store_true", help="emit JSON")
    ap.add_argument("--show-rejects", action="store_true",
                    help="list names filtered out of a cvar context (audit)")
    ap.add_argument("--include-tests", action="store_true",
                    help="also scan tests/")
    args = ap.parse_args(argv)

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    scan_dirs = ["src", "game"]
    if args.include_tests:
        scan_dirs.append("tests")

    validate_scope_dirs(root)
    cvars, rejects = scan(root, scan_dirs)
    cvars = expand_dynamic(cvars)

    if args.json:
        report_json(cvars)
    elif args.markdown:
        report_markdown(cvars)
    else:
        report_text(cvars, rejects, args.show_rejects)
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Render or check the linked manifest for the manually reviewed S1 files."""
from __future__ import annotations

import argparse
from collections import Counter
from functools import cache
from hashlib import sha256
import json
from pathlib import Path
import re
from parity_reference import PIN, verify_qc_inputs

ROOT = Path(__file__).resolve().parent.parent
AUDIT = ROOT / "planning/parity/unmapped-audit"
UPSTREAM = f"https://github.com/xonotic/xonotic-data.pk3dir/blob/{PIN}/qcsrc/"
# The first pass selected 65 unresolved and 113 native-counterpart files. Their
# dispositions can change while the ordered set used for repeat comparisons stays fixed.
COHORT_SHA256 = "9949b3035373ed66f3709467330fb3fac16325196fa0fc50768f39a9979ac9fd"
OUTCOMES = {
    "counterpart", "counterpart-with-delta", "architecture-replaced",
    "gap", "stock-disabled", "no-stock-caller-evidenced",
}


@cache
def read_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8").splitlines()


def anchor(root: Path, witness: str) -> tuple[str, int]:
    name, separator, excerpt = witness.partition("@")
    path = root / name
    if not separator or not excerpt or not path.resolve().is_relative_to(root):
        raise ValueError(f"Invalid source witness {witness!r}")
    for number, line in enumerate(read_lines(path), 1):
        if excerpt in line:
            return name, number
    raise ValueError(f"No matching source witness {witness!r}")


def qc_link(path: str, line: int | None = None) -> str:
    return f"[{path}{':' + str(line) if line else ''}]({UPSTREAM}{path}{'#L' + str(line) if line else ''})"


def native_link(path: str, line: int) -> str:
    return f"[{Path(path).name}:{line}](../../../{path}#L{line})"


def check_evidence_link(directory: Path, link: str) -> None:
    """S1 links cite files or simple, named ATX sections in the evidence reports."""
    name, _, fragment = link.partition("#")
    report = directory / name
    if not report.is_file():
        raise ValueError(f"Missing evidence report: {link}")
    if not fragment:
        return
    fence = ""
    for line in read_lines(report):
        if fence:
            closing = line.strip()
            if closing.startswith(fence) and not closing.strip(fence[0]):
                fence = ""
            continue
        delimiter = re.match(r" {0,3}(`{3,}|~{3,})", line)
        if delimiter:
            fence = delimiter[1]
            continue
        heading = re.fullmatch(r" {0,3}#{1,6}\s+([\w -]+?)\s*", line)
        if heading and heading[1].lower().replace(" ", "-") == fragment:
            return
    raise ValueError(f"Missing evidence section: {link}")


def evidence_for(path: str) -> tuple[str, str] | None:
    """Link only the branch exercised by each fixture or test."""
    stem = path.rsplit(".", 1)[0]
    reports = {
        "client/shownames": ("Paired label occlusion, overlap and model-alpha trials", "../shownames/README.md"),
        "client/teamradar": ("Paired radar pulse, projection and click traces", "../quickmenu-radar/TEAMRADAR.md"),
        "client/hud/crosshair": ("Paired remote crosshair gates and chase captures", "../crosshair/README.md"),
        "client/hud/panel/scoreboard": ("Paired tiebreaks, ignored chat and modal input", "../scoreboard-blocks/README.md"),
        "common/_all": ("Release-client retries and connection deadlines", "connection-timeout/README.md"),
        "common/constants": ("Corpse hit traces, species, ballots and door initialization", "shared-constants/README.md"),
        "common/mapobjects/trigger/hurt": ("Spawnflag and touch regressions", "S1-VERIFICATION.md#q3-trigger_hurt"),
        "common/net_notice": ("Receipt, delayed presentation and expiry", "client-state-notices/README.md#notice-delivery-and-expiry"),
        "common/state": ("Native connection and player-state lifetimes", "client-state-notices/README.md"),
        "common/debug": ("Paired local entity and waypoint labels", "../debug-labels/README.md"),
        "common/viewloc": ("Paired remote cursor, brush and combined camera-snap trials", "../view-location/FREEAIM.md"),
        "common/mapobjects/trigger/viewloc": ("Rail entry and exit on server-only replacement entities", "../view-location/README.md"),
        "common/effects/qc/casings": ("Shell and brass impact audio and final-second fade", "../rubble-limits/casing-impact/README.md"),
        "common/effects/qc/modeleffects": ("Paired Fireball blast, timeout and self-damage trials", "../fireball-sphere/README.md"),
        "common/effects/qc/gibs": ("Paired lethal hits under seven client settings", "../rubble-limits/gib-options-live/README.md"),
        "common/effects/qc/rubble.qh": ("Paired lethal shotgun and native same-frame trials", "../rubble-limits/network-order.md#row-18"),
        "common/effects/qc/rubble.qc": ("Electro cap, moving carrier, death and disconnect trials", "../rubble-limits/electro-limit/README.md"),
        "common/gametypes/gametype/duel/sv_duel": ("Paired Duel powerup switches and alias-only map load", "../duel-powerups/README.md"),
        "common/gametypes/gametype/keyhunt/keyhunt.qh": ("KeyHunt catalog boundaries and authored match settings", "../keyhunt-map/README.md"),
        "common/gametypes/gametype/keyhunt/sv_keyhunt.qh": ("Round controller and bot target order", "../keyhunt-controller/README.md"),
        "common/vehicles/cl_vehicles": ("Native reticle fixture and its input limits", "../vehicle-reticles/README.md"),
        "common/mutators/mutator/status_effects/status_effect/superweapons":
            ("Paired picker and peer recordings", "../review/superweapons-poweroff-verification.md#live-capture"),
        "server/weapons/weaponstats": ("Native per-map export and focused tests", "../registry/sv-weaponstats.yaml"),
        "server/command/sv_cmd": ("Three program exports and stock comparison", "../local-commands/README.md"),
        "lib/matrix/command": ("Dedicated operator console and loopback checks", "../matrix/VERIFICATION.md"),
        "lib/matrix/matrix": ("Dedicated Matrix transport and room lifecycle", "../matrix/VERIFICATION.md"),
        "server/matrix": ("Bidirectional loopback chat", "../matrix/VERIFICATION.md"),
    }
    for mode in ("assault", "ctf", "cts", "domination", "duel", "freezetag", "invasion", "keepaway", "keyhunt"):
        reports[f"common/gametypes/gametype/{mode}/{mode}.qc"] = (
            "Paired Simplified Chinese Guide selections", "../guide-gametypes/README.md")
    return reports.get(path) or reports.get(stem)


def review(fields: list[str], inventory: dict) -> tuple[str, str, str, str, str, list[str]]:
    outcome, *data = fields
    if outcome == "turret":
        unit, type_name, impulse, details = data
        caller = f"common/turrets/turret/{unit}_weapon.qc@wr_think"
        native = f"src/VortexArena.Common/Gameplay/Turrets/{type_name}Turret.cs@class {type_name}Turret"
        comparison = (
            f"QC registers turret_{unit} as a hidden special attack with impulse {impulse}, "
            "using a PortoLaunch subclass. Native routes the map turret's combat through its "
            "typed controller. The native firing counterpart alone does not establish parity "
            "for player invocation of the QC special attack."
        )
        return "counterpart", caller, native, comparison, details, ["0003"]
    caller, native, comparison, unresolved, *decisions = data
    if outcome not in OUTCOMES or not comparison or not unresolved or len(decisions) > 1:
        raise ValueError(f"Invalid review for {inventory['path']}")
    return outcome, caller, native, comparison, unresolved, decisions[0].split(",") if decisions else []


def build_manifest(base: Path) -> str:
    ledger = json.loads((AUDIT / "dispositions.json").read_text(encoding="utf-8"))
    if ledger["reference"] != PIN:
        raise ValueError("QC checkout and input ledger must match the S1 pin")
    verify_qc_inputs(base)
    qc = base / "qcsrc"
    notes = read_lines(AUDIT / "s1-review.txt")
    paths = [note.partition("|")[0] for note in notes]
    if sha256(("\n".join(paths) + "\n").encode()).hexdigest() != COHORT_SHA256:
        raise ValueError("S1 source paths or directory order differ from the frozen 178-file cohort")
    by_path = {entry["path"]: entry for entry in ledger["files"]}

    totals = Counter()
    sections = []
    for index, note in enumerate(notes, 1):
        path, *fields = note.split("|")
        if path not in by_path:
            raise ValueError(f"Missing source inventory entry for row {index}: {path}")
        entry = by_path[path]
        content = (qc / path).read_bytes()
        if sha256(content).hexdigest() != entry["sha256"] or len(content.splitlines()) != entry["lines"]:
            raise ValueError(f"QC source does not match the input ledger: {path}")

        try:
            outcome, caller, native, comparison, unresolved, decisions = review(fields, entry)
            qc_witness = qc_link(*anchor(qc, caller))
            native_witness = native_link(*anchor(ROOT, native))
        except (OSError, ValueError) as error:
            raise ValueError(f"Row {index} ({path}): {error}") from error
        totals[outcome] += 1

        includes = []
        for target, chain in sorted(entry["stock_include_paths"].items()):
            links = []
            for edge in chain:
                source, number = edge.rsplit(":", 1)
                line_number = int(number)
                if "#include" not in read_lines(qc / source)[line_number - 1]:
                    raise ValueError(f"Invalid include edge {edge}")
                links.append(qc_link(source, line_number))
            includes.append(f"{target} ({' → '.join(links) or 'program root'})")

        quality = "Static source comparison."
        if outcome == "gap":
            quality = "Static source trace and native absence finding."
        elif outcome == "no-stock-caller-evidenced":
            quality = "Negative caller search and static source reading."
        elif outcome == "stock-disabled":
            quality = "Static build/configuration gate."
        if evidence := evidence_for(path):
            description, report = evidence
            check_evidence_link(AUDIT, report)
            quality += f" [{description}]({report})"

        section = [
            f"### {index}. `{path}`",
            "",
            f"**Disposition** · {outcome} · inventory `{entry['disposition']}`.",
            f"**Evidence quality** · {quality}",
            "",
            comparison,
            "",
            f"**Open** · {unresolved}",
            "",
            f"**Sources** · {qc_link(path)} · {qc_witness} · {native_witness}",
            f"**Include routes** · {' · '.join(includes) or 'No stock route recorded.'}",
        ]
        if decisions:
            references = []
            for decision in decisions:
                (adr,) = sorted((ROOT / "planning/decisions").glob(f"ADR-{decision}-*.md"))
                references.append(f"[ADR-{decision}](../../decisions/{adr.name})")
            section.append(f"**Applicable decisions** · {' · '.join(references)}")
        sections.append("\n".join(section))

    counts = ", ".join(f"{count} {outcome}" for outcome, count in sorted(totals.items()))
    return (
        "# S1 source review\n\n"
        f"Xonotic data pin `{PIN}`. The first pass selected 65 unresolved and 113 native-counterpart entries. "
        "This review retains those 178 paths in directory order as their [inventory dispositions](dispositions.json) change. "
        "The [authored notes](s1-review.txt) supply each disposition, comparison, source witness and open dimension.\n\n"
        f"**Outcomes** · {counts}.\n\n"
        "Counterpart identifies a native responsibility with some static evidence. "
        "Gap records an observed difference or a missing consumer after scoped search. "
        "Architecture decisions explain implementation boundaries. Behavioral equality requires "
        "paired runtime evidence for the relevant inputs. Include routes are possible stock build routes "
        "from the original ledger. The cited witness can be a caller, registration, implementation, or diagnostic anchor.\n\n"
        + "\n\n".join(sections) + "\n"
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, default=Path("/tmp/vortex-parity-base"))
    parser.add_argument("--check", action="store_true", help="compare the manifest without writing")
    args = parser.parse_args()
    try:
        manifest = build_manifest(args.base.resolve())
    except (OSError, ValueError) as error:
        raise SystemExit(str(error)) from error
    output = AUDIT / "S1-REVIEW.md"
    if args.check:
        if not output.is_file() or output.read_text(encoding="utf-8") != manifest:
            raise SystemExit(f"Stale manifest: {output.relative_to(ROOT)}")
    else:
        output.write_text(manifest, encoding="utf-8")
    print(f"Verified 178 ordered S1 reviews at {PIN}")


if __name__ == "__main__":
    main()

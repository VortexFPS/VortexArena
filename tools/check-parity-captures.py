#!/usr/bin/env python3
"""Check the published two-client comparisons and, optionally, their raw recordings."""

from __future__ import annotations

import argparse
from collections import Counter
from functools import lru_cache
from hashlib import file_digest, sha256
from html.parser import HTMLParser
import json
import math
from pathlib import Path
import re
import subprocess
import sys
from zipfile import BadZipFile, ZipFile

from parity_reference import PIN

PARITY = Path(__file__).resolve().parents[1] / "planning/parity"
ARCHIVE_ROOT = Path("/tmp")
SCENARIOS = {
    "sandbox-owner", "sandbox-readonly", "vampire", "crylink", "onslaught-point",
    "onslaught-generator", "countdown",
}
ENGINES = ("original", "native")
SHEET_PHASES = {
    "sandbox-owner": ("03-owner-held", "03-owner-released"),
    "sandbox-readonly": ("04-free-held", "04-free-readonly-held", "04-free-released", "05-readonly-held"),
    "vampire": ("06-buff-before", "07-buff-after"),
}
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
DIGEST = re.compile(r"[0-9a-f]{64}")


class Comparisons(HTMLParser):
    def __init__(self):
        super().__init__()
        self.pairs = {}
        self.scenario = None
        self.current = None

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if tag == "section":
            self.scenario = values.get("id")
            if self.current is not None or self.scenario not in SCENARIOS or self.scenario in self.pairs:
                raise ValueError(f"Invalid or duplicate comparison section: {self.scenario!r}")
            self.current = []
        elif tag == "img" and self.current is not None:
            path = (PARITY / values["src"]).resolve()
            if not path.is_relative_to(PARITY):
                raise ValueError(f"Nonlocal image: {path}")
            self.current.append(path)
        elif tag == "a" and "href" in values:
            path = PARITY / values["href"]
            if not path.resolve().is_relative_to(PARITY) or not path.is_file():
                raise ValueError(f"Missing or nonlocal report link: {path}")

    def handle_endtag(self, tag):
        if tag == "section":
            if self.current is None or len(self.current) != 2:
                raise ValueError("Each comparison needs one original frame and one native frame")
            self.pairs[self.scenario] = tuple(self.current)
            self.current = None


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def timeline(folder):
    json_path = folder / "timeline.json"
    if json_path.is_file():
        return read(json_path)
    return [
        json.loads(line) for line in (folder / "timeline.jsonl").read_text().splitlines() if line.strip()
    ]


def timestamp(value):
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


def arguments(commands, role):
    command = commands[role]
    return command["argv"] if isinstance(command, dict) else command


def option(argv, flag):
    if flag not in argv or argv.index(flag) + 1 >= len(argv):
        raise ValueError(f"Missing argument for {flag}")
    return argv[argv.index(flag) + 1]


def logfile(folder, role):
    for suffix in (".log", ".txt"):
        path = (folder / role).with_suffix(suffix)
        if path.is_file() and path.stat().st_size:
            return ANSI.sub("", path.read_text(errors="replace"))
    raise ValueError(f"Missing {role} log in {folder}")


def check_connections(logs, argv, original):
    server = logs["server"]
    slots = set()
    for role in argv.keys() - {"server"}:
        expected_name = {"owner": "ParityOwner", "guest": "ParityGuest"}.get(role, role)
        name = option(argv[role], "+name" if original else "_cl_name")
        if name != expected_name:
            raise ValueError(f"Expected client {expected_name}, got {name}")
        escaped = re.escape(name)
        joins = set(re.findall(rf"^:join:(\d+):-?\d+:.*:{escaped}\s*$", server, re.MULTILINE))
        if len(joins) != 1 or joins & slots:
            raise ValueError(f"Expected a distinct server join for {name}")
        slots.update(joins)
        if original:
            connected = re.search(rf"^{escaped} connected\s*$", logs[role], re.MULTILINE)
        else:
            slot = next(iter(joins))
            connected = re.search(rf"^\[ClientNet\] handshake accepted by '[^']+': netId {slot},", logs[role],
                                  re.MULTILINE)
        if not connected:
            raise ValueError(f"Missing client connection record for {name}")


def evidence(image, engine, scenario, entry):
    run = image.parent
    original = engine == "original"
    inputs = read(run / "inputs.json")
    pin = inputs.get("original_data_git", inputs.get("xonotic_data_revision"))
    if pin != PIN:
        raise ValueError(f"{run} refers to {pin!r}")
    commands = read(run / "commands.json")
    roles = ("owner", "guest") if scenario in SHEET_PHASES else ("RedProbe", "BlueProbe")
    argv = {role: arguments(commands, role) for role in ("server", *roles)}
    expected = "darkplaces" if original else "godot"
    for role, command in argv.items():
        if not command or expected not in Path(command[0]).name.lower():
            raise ValueError(f"Expected {expected} executable for {role} in {run}")
    logs = {role: logfile(run, role) for role in argv}
    check_connections(logs, argv, original)
    events = timeline(run)
    if not events or not all(timestamp(event.get("seconds")) for event in events):
        raise ValueError(f"Empty or untimed action timeline in {run}")

    payload = image.read_bytes()
    digest = sha256(payload).hexdigest()
    if entry["image"] != image.relative_to(PARITY).as_posix() or entry["sha256"] != digest:
        raise ValueError(f"Published image path or digest mismatch for {scenario}/{engine}: {image}")

    if image.suffix == ".webp":
        if payload[:4] != b"RIFF" or payload[8:12] != b"WEBP":
            raise ValueError(f"Invalid WebP container for {image}")
        manifest = read(run / "frames.json")
        if isinstance(manifest, list):
            frame = next((row for row in manifest if row["label"] == image.stem), {})
            selected = {"frame": frame.get("frame"), "sample": frame.get("sample"), "seconds": frame.get("seconds")}
        else:
            frame = manifest["frames"][image.name]
            selected = {"frame": frame["source"], "sample": frame["sample"]}
        recorded = entry["sample"]
        if not timestamp(selected["sample"]) or any(recorded.get(key) != value for key, value in selected.items()):
            raise ValueError(f"Missing or mismatched sampled frame entry for {image}")
        # The objective exporters retain sampling rows separately in frames.json.
        if scenario == "countdown":
            check_sample(events, recorded)
    elif image.suffix == ".jpg" and payload.startswith(b"\xff\xd8") and payload.endswith(b"\xff\xd9"):
        samples = entry["samples"]
        sample_frames = {sample["frame"] for sample in samples}
        if len(samples) != len(sample_frames):
            raise ValueError(f"Duplicate sheet samples for {image}")
        expected_frames = {f"{phase}-{role}.webp" for phase in SHEET_PHASES[scenario] for role in roles}
        if not expected_frames <= sample_frames:
            raise ValueError(f"Missing phase samples for {image}")
        for sample in samples:
            check_sample(events, sample)
            if not DIGEST.fullmatch(sample["sha256"]):
                raise ValueError(f"Invalid raw frame digest for {sample['frame']}")
    else:
        raise ValueError(f"Invalid contact sheet for {image}")

    bsp = inputs["fixture_bsp_sha256"]
    members = inputs.get("external_lightmaps_sha256", inputs.get("matching_map_members_sha256", {}))
    lights = {key: value for key, value in members.items() if "/lm_" in key}
    qc = inputs["original_code_sha256"]
    if not lights or qc.keys() != {"progs.dat", "csprogs.dat", "menu.dat"}:
        raise ValueError(f"Missing map lightmaps or QC hashes in {run}")
    if not all(DIGEST.fullmatch(value) for value in (bsp, *lights.values(), *qc.values())):
        raise ValueError(f"Invalid digest in {run}")
    return (bsp, lights, qc), inputs, argv, logs, events, digest


def check_sample(events, sample):
    frame = sample["frame"]
    path = Path(frame)
    if path.is_absolute() or ".." in path.parts or path.suffix != ".webp":
        raise ValueError(f"Invalid raw frame path: {frame}")
    matches = [event for event in events if event.get("frame") == frame]
    fields = ("seconds", "client") if "client" in sample else ("seconds", "sample")
    if len(matches) != 1 or any(matches[0].get(field) != sample[field] for field in fields):
        raise ValueError(f"Raw frame timeline mismatch for {frame}")
    if not timestamp(sample["seconds"]) or ("sample" in sample and not timestamp(sample["sample"])):
        raise ValueError(f"Invalid frame timestamp for {frame}")


@lru_cache
def digest_file(path):
    with path.open("rb") as stream:
        return file_digest(stream, "sha256").hexdigest()


def check_digest(actual, expected, description):
    if actual != expected:
        raise ValueError(f"SHA-256 mismatch for {description}: expected {expected}, found {actual}")


class RawArchive:
    def __init__(self, root, base):
        self.root = root.resolve(strict=True)
        self.base = base.resolve(strict=True)
        self.counts = Counter()
        git = ["git", "--no-optional-locks", "-C", str(self.base)]
        top = subprocess.check_output([*git, "rev-parse", "--show-toplevel"], text=True).strip()
        if Path(top).resolve() != self.base:
            raise ValueError(f"Expected the pinned repository root at {self.base}")
        pin = subprocess.check_output([*git, "rev-parse", "--verify", "HEAD^{commit}"], text=True).strip()
        if pin != PIN:
            raise ValueError(f"Expected Xonotic data {PIN}, got {pin} at {self.base}")
        changes = subprocess.check_output(
            [*git, "status", "--porcelain", "--untracked-files=no"], text=True
        )
        if changes:
            raise ValueError(f"Pinned Xonotic data has tracked changes at {self.base}")

    def path(self, recorded):
        path = Path(recorded)
        if not path.is_relative_to(ARCHIVE_ROOT) or ".." in path.parts:
            raise ValueError(f"Invalid archived /tmp path: {recorded}")
        return self.root / path.relative_to(ARCHIVE_ROOT)

    def mount(self, link):
        target = link.readlink()
        if target.is_absolute() and target.is_relative_to(ARCHIVE_ROOT):
            return self.path(target).resolve(strict=True)
        return (link.parent / target).resolve(strict=True)

    def check(self, engine, scenario, entry, record):
        fixture, inputs, argv, logs, events, digest = record
        original = engine == "original"
        sandbox = scenario in SHEET_PHASES
        server = argv["server"]
        flag = "-basedir" if original else "--data"
        data = self.path(option(server, flag))
        stage = data.parent
        raw = stage / f"session-{engine}" if sandbox else stage
        if read(stage / "inputs.json") != inputs:
            raise ValueError(f"Captured input record differs from the saved record: {stage}")
        raw_commands = read(raw / "commands.json")
        if any(arguments(raw_commands, role) != command for role, command in argv.items()):
            raise ValueError(f"Captured commands differ from the saved commands: {raw}")
        if any(option(command, flag) != option(server, flag) for command in argv.values()):
            raise ValueError(f"Recorded clients and server mount different data: {raw}")
        raw_events = timeline(raw)
        saved_events = raw_events
        if scenario not in SHEET_PHASES and scenario != "countdown":
            saved_events = [event for event in raw_events if "frame" not in event]
        if saved_events != events:
            raise ValueError(f"Captured timeline differs from the saved timeline: {raw}")
        raw_logs = {role: logfile(raw, role) for role in argv}
        check_connections(raw_logs, argv, original)
        if raw_logs != logs:
            raise ValueError(f"Captured logs differ from the saved logs: {raw}")
        self.counts["client logs"] += len(argv) - 1

        stock_mount = stage / "original/data"
        if self.mount(stock_mount / "xonotic-data.pk3dir") != self.base:
            raise ValueError(f"Captured Xonotic data mount does not match --base: {stock_mount}")
        code_mount = self.mount(stock_mount / ("parity-code.pk3dir" if sandbox else "zz-pinned.pk3dir"))
        bsp, lights, qc = fixture
        for name, expected in qc.items():
            check_digest(digest_file(code_mount / name), expected, code_mount / name)
            self.counts["compiled QC records"] += 1
        map_name = option(server, "+map" if original else "--dedicated")
        bsp_name = f"maps/{map_name}.bsp"
        package = data / ("data" if original else "maps") / f"{map_name}.pk3"
        with ZipFile(package) as archive:
            actual_lights = [name for name in archive.namelist()
                             if re.fullmatch(rf"maps/{re.escape(map_name)}/lm_[0-9]+\.[^/]+", name)]
            if sorted(actual_lights) != sorted(lights) or archive.namelist().count(bsp_name) != 1:
                raise ValueError(f"Unexpected BSP or lightmap members in {package}")
            for name, expected in {bsp_name: bsp, **lights}.items():
                check_digest(sha256(archive.read(name)).hexdigest(), expected, f"{package}:{name}")
        self.counts["mounted fixture packages"] += 1
        self.check_executables(inputs, argv, original, scenario)
        self.check_images(entry, digest, raw, raw_events)

    def check_executables(self, inputs, argv, original, scenario):
        recorded = inputs.get("executables_sha256")
        if recorded is None:
            role_keys = {"server": "xonotic_server"} if original else {}
            recorded = {command[0]: inputs["runtime_sha256"][role_keys.get(
                role, "xonotic_client" if original else "godot"
            )] for role, command in argv.items()}
        for filename in {command[0] for command in argv.values()}:
            exe = self.path(filename)
            check_digest(digest_file(exe), recorded[filename], exe)
            self.counts["engine binary records"] += 1

        if original:
            return
        assemblies = inputs.get("debug_assemblies_sha256", inputs.get("native_build_sha256", {}))
        project = Path(option(argv["server"], "--path"))
        directory = (self.path(project) / ".godot/mono/temp/bin/Debug"
                     if project.is_relative_to(ARCHIVE_ROOT) else None)
        matching = sum(1 for name, expected in assemblies.items()
                       if directory is not None and (file := directory / name).is_file()
                       and digest_file(file) == expected)
        if matching != len(assemblies):
            print(f"{scenario}/native: {matching}/{len(assemblies)} historical DLL hashes match "
                  f"available build bytes; the remaining records are unverified ({project}).")
        self.counts["matching historical DLLs"] += matching
        self.counts["recorded historical DLLs"] += len(assemblies)

    def check_images(self, entry, digest, raw, events):
        if "sample" in entry:
            sample = entry["sample"]
            check_sample(events, sample)
            path = raw / sample["frame"]
            check_digest(digest_file(path), digest, path)
            self.counts["selected WebPs"] += 1
            return

        tiles = []
        for sample in entry["samples"]:
            check_sample(events, sample)
            path = raw / sample["frame"]
            check_digest(digest_file(path), sample["sha256"], path)
            if sample["tile"]:
                tiles.append(str(path))
            self.counts["raw sheet samples"] += 1
        recipe = entry["montage"]
        labels = []
        if "font" in recipe:
            labels = ["-font", recipe["font"], "-pointsize", str(recipe["pointsize"]), "-label", "%f"]
        jpg = subprocess.check_output([
            "montage", *labels, *tiles, "-tile", "2x", "-geometry", recipe["geometry"],
            "-quality", str(recipe["quality"]), "jpg:-",
        ])
        check_digest(sha256(jpg).hexdigest(), digest, f"reconstructed sheet for {raw}")
        self.counts["reproduced sheets"] += 1
        if "raw_sheet" in entry:
            sheet = self.path(entry["raw_sheet"])
            check_digest(digest_file(sheet), digest, sheet)
            self.counts["matching archived JPEG copies"] += 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--raw", type=Path, help="root containing the captured /tmp runs and binaries")
    parser.add_argument("--base", type=Path, help="pinned Xonotic data Git checkout mounted by the runs")
    args = parser.parse_args()
    if (args.raw is None) != (args.base is None):
        parser.error("--raw and --base must be supplied together")
    raw = RawArchive(args.raw, args.base) if args.raw else None
    commitments = read(PARITY / "scripted-capture-evidence.json")
    page = Comparisons()
    page.feed((PARITY / "scripted-comparisons.html").read_text(encoding="utf-8"))
    if page.current is not None or page.pairs.keys() != SCENARIOS or commitments.keys() != SCENARIOS:
        raise ValueError("Expected the seven published comparisons in the page and evidence index")
    seen = set()
    for scenario, images in page.pairs.items():
        entries = commitments[scenario]
        if entries.keys() != set(ENGINES):
            raise ValueError(f"Expected both engines in the evidence index for {scenario}")
        fixtures = []
        for engine, image in zip(ENGINES, images, strict=True):
            entry = entries[engine]
            record = evidence(image, engine, scenario, entry)
            fixture, *_, digest = record
            if digest in seen:
                raise ValueError(f"Reused image bytes in {scenario}")
            seen.add(digest)
            fixtures.append(fixture)
            if raw:
                raw.check(engine, scenario, entry, record)
        if fixtures[0] != fixtures[1]:
            raise ValueError(f"Input mismatch between {images[0].parent} and {images[1].parent}")
    print(f"{len(page.pairs)} published pairs match their saved image checksums, client logs and fixture records.")
    if raw:
        print(f"Raw archive at pin {PIN}: " + ", ".join(f"{count} {label}" for label, count in raw.counts.items()) + ".")


if __name__ == "__main__":
    try:
        main()
    except (BadZipFile, KeyError, OSError, ValueError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))

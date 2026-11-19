"""Regression fixtures for pinned source, asset resolution and mixed parity assessments."""
import importlib.util
import hashlib
import io
from contextlib import redirect_stdout
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

import yaml

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import parity_reference


def load_tool(filename):
    spec = importlib.util.spec_from_file_location(filename, TOOLS / (filename + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ParityEvidenceTests(unittest.TestCase):
    def test_cues_after_done_and_short_peer_monitors_fail(self):
        script = TOOLS.parent / "planning/parity/review/superweapons-poweroff/check-run.py"
        spec = importlib.util.spec_from_file_location("check_cues", script)
        checker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(checker)
        sample = self.root / "reference"
        sample.write_bytes(b"sample for this fixture")
        self.write("timeline.log", "0 scenario native timed\n1 audio recording\n1.25 picker cmd teleporttotarget probe_shooter\n2 done\n")
        for role in ("picker", "peer"):
            (self.root / f"{role}.raw").write_bytes(bytes(194000))  # just over one second at 48 kHz
        args = [str(script), "native", "timed", str(self.root), "--matcher", "matcher", "--sample", str(sample)]
        with patch.object(sys, "argv", args), patch.object(checker, "SAMPLE_SHA256", hashlib.sha256(sample.read_bytes()).hexdigest()), redirect_stdout(io.StringIO()):
            with patch.object(subprocess, "check_output", return_value="1.005 s  corr 1.0\n" * 6):
                with self.assertRaisesRegex(SystemExit, "Cue count mismatch"):
                    checker.main()
            (self.root / "peer.raw").write_bytes(bytes(144000))
            with patch.object(subprocess, "check_output", return_value="0.500 s  corr 1.0\n" * 6):
                with self.assertRaisesRegex(ValueError, "peer.raw duration differs"):
                    checker.main()

    def test_timeout_trial_requires_pairs_and_a_bounded_native_wait(self):
        probe = runpy.run_path(str(TOOLS.parent / "planning/parity/unmapped-audit/connection-timeout/reproduce.py"))
        pairs = [second + index / 100 for second in range(12) for index in range(2)]
        self.assertEqual(probe["check_stock_attempts"](pairs), list(range(12)))
        with self.assertRaisesRegex(ValueError, "two datagrams each"):
            probe["check_stock_attempts"](sorted(pairs[:1] + [1.02] + pairs[2:]))
        probe["check_native_wait"](22.14, 22)
        for elapsed in (10, 36):
            with self.subTest(elapsed=elapsed), self.assertRaises(ValueError):
                probe["check_native_wait"](elapsed, 22)

    def test_comparison_evidence_links_resolve_to_the_exercised_section(self):
        s1 = load_tool("parity-s1-review")
        self.assertNotEqual(s1.evidence_for("common/effects/qc/rubble.qc"),
                            s1.evidence_for("common/effects/qc/rubble.qh"))
        self.write("paired.md", "## Row 18\n\n```text\n## Row 13\n```\n")
        s1.check_evidence_link(self.root, "paired.md#row-18")
        with self.assertRaisesRegex(ValueError, "Missing evidence section"):
            s1.check_evidence_link(self.root, "paired.md#row-13")

    def test_directory_summary_reflects_reclassified_files(self):
        table = runpy.run_path(str(TOOLS / "parity-unmapped-audit.py"))["directory_table"]
        files = [
            {"path": "common/viewloc.qh", "lines": 14, "disposition": "declarations"},
            {"path": "common/viewloc.qc", "lines": 206, "disposition": "native-counterpart"},
        ]
        self.assertIn("| `common` | 2 | 220 | 0 | 1 | 0 | 1 | 0 | 0 | 0 |", table(files))
        files[0]["disposition"] = "unknown disposition"
        with self.assertRaises(ValueError):
            table(files)

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)

    def write(self, name, content):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def test_explicit_absence_claim_surfaces_native_methods_for_review(self):
        tool = load_tool("parity-assemble")
        units = [{"features": [{"id": "itemstats", "gaps": ["No DrawItemStats / SetItemStats in Hud.",
                                "No `OtherMethod`(target) in Hud.", "No UniqueMethod anywhere.",
                                "No OtherMethod hook in Hud.", "No OtherMethod caller in Hud.",
                                "No OtherMethod-equivalent in Hud."],
                                "verification": [{"result": "pass", "ref": "No OtherMethod"}]}]}]
        self.write("game/Hud.cs", "private float DrawItemStats(float y) => y;\npublic void SetItemStats() {}\n"
                   "public void OtherMethod() {}\n/*\npublic void SetItemStats() {}\n*/\n"
                   "public void UniqueMethod() {}\npublic void SetItemStats(int n) {}\n")
        self.write("src/Server/obj/Copy.cs", "public void SetItemStats() {}")
        self.write("game/Unrelated.cs", "public void DrawItemStats() {}")
        self.assertEqual(tool.absence_witnesses(units, self.root), [
            ("itemstats", "DrawItemStats", "game/Hud.cs", 1), ("itemstats", "SetItemStats", "game/Hud.cs", 2),
            ("itemstats", "SetItemStats", "game/Hud.cs", 8)])

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args], text=True).strip()

    def test_pinned_bytes_ignore_git_index_visibility(self):
        self.git("init", "-q")
        for name in ("qcsrc/Makefile", "qcsrc/tools/qcc.sh", "xonotic-server.cfg"):
            self.write(name, "fixture\n")
        source = self.write("qcsrc/example.qc", "test\n")
        self.git("add", ".")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@localhost",
                 "-c", "commit.gpgSign=false", "commit", "-qm", "fixture")
        with patch.object(parity_reference, "PIN", self.git("rev-parse", "HEAD")):
            parity_reference.verify_qc_inputs(self.root)
            self.git("update-index", "--assume-unchanged", "qcsrc/example.qc")
            source.write_text("TEST\n")
            self.assertEqual(self.git("status", "--porcelain"), "")
            with self.assertRaisesRegex(SystemExit, "bytes differ"):
                parity_reference.verify_qc_inputs(self.root)
            source.unlink()
            with self.assertRaisesRegex(SystemExit, "Missing"):
                parity_reference.verify_qc_inputs(self.root)
            source.write_text("test\n")
            self.write(".git/info/exclude", "*.local.qh\n")
            self.write("qcsrc/extra.local.qh", "ignored")
            with self.assertRaisesRegex(SystemExit, "Untracked QC"):
                parity_reference.verify_qc_inputs(self.root)

    def test_pack_priority_and_required_cubemap_faces(self):
        assets = load_tool("parity-asset-check")
        for pack in ("maps/z.pk3dir", "a.pk3dir", "z.pk3dir"):
            self.write(pack + "/gfx/value.png", pack)
        with ZipFile(self.root / "zz.pk3", "w") as archive:
            archive.writestr("gfx/value.png", "last pack")
        self.assertEqual(assets.Vfs(self.root).read_head("gfx/value.png"), b"last pack")
        self.write("gfx/value.png", "loose")
        self.assertEqual(assets.Vfs(self.root).read_head("gfx/value.png"), b"loose")
        for suffix in ("px", "nx", "py", "ny", "pz"):
            self.write(f"a.pk3dir/cubemaps/default/sky{suffix}.png", "")
        self.assertEqual(assets.resolve(assets.Vfs(self.root), "cubemaps/default/sky")[0], "missing")
        self.write("a.pk3dir/cubemaps/default/skynz.png", "")
        self.assertEqual(assets.resolve(assets.Vfs(self.root), "cubemaps/default/sky")[0], "ok")
        for suffix in ("rt", "lf", "bk", "ft", "up"):
            self.write(f"a.pk3dir/env/test_{suffix}.png", "")
        self.write("a.pk3dir/env/test_nz.png", "")
        self.assertEqual(assets.resolve(assets.Vfs(self.root), "env/test")[0], "missing")
        self.write("a.pk3dir/env/testdn.png", "")
        self.assertEqual(assets.resolve(assets.Vfs(self.root), "env/test")[0], "ok")

    def test_ranked_design_difference_preserves_rationale_and_constants(self):
        assembler = load_tool("parity-assemble")
        assembler.REG = assembler.OUT = self.root
        statuses = dict.fromkeys(assembler.DIMS, "faithful")
        statuses.update(logic="partial", liveness="live")
        feature = dict(id="fixture", status=statuses, intended_divergence=True,
                       divergence_rationale="native design", open_dimensions=["logic"],
                       constants=[dict(name="radius", base=24, port=48, match=False)])

        def parse(row):
            self.write("unit.yaml", yaml.safe_dump({"features": [row]}))
            return assembler.load()

        units, diagnostics = parse(feature)
        self.assertEqual(diagnostics, [])
        self.assertGreater(units[0]["features"][0]["_score"], 0)
        assembler.write_gaps(units)
        report = (self.root / "PARITY-GAPS.md").read_text()
        for expected in ("native design", "24 / 48", "logic:partial"):
            self.assertIn(expected, report)
        for invalid in (dict(id=[]), dict(id=" "), dict(open_dimensions=["timing"]),
                        dict(open_dimensions=["logic", "logic"]), dict(status={"logic": "partial"})):
            with self.subTest(invalid=invalid):
                self.assertTrue(parse(feature | invalid)[1])


if __name__ == "__main__":
    unittest.main()

import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("bundle_decisions", Path(__file__).resolve().parents[1] / "parity-bundle-decisions.py")
bundler = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bundler)


class BundleInputsTests(unittest.TestCase):
    def test_inputs_and_their_aliases_survive_output_selection(self):
        with tempfile.TemporaryDirectory() as directory:
            parity = Path(directory)
            page = parity / "index.html"
            page.write_text("const media = path => path; media('sample.webp')")
            image = parity / "sample.webp"
            image.write_bytes(b"image fixture")
            report = parity / "ORDERED-FOLLOWUP.md"
            report.write_text("report fixture")
            link = parity / "linked.html"
            link.symlink_to(image)
            hardlink = parity / "hardlink.html"
            os.link(report, hardlink)
            with patch.object(bundler, "PAGE", page), patch.object(bundler, "PARITY", parity):
                for path in (page, image, report, link, hardlink):
                    with self.subTest(path=path):
                        before = path.read_bytes()
                        with self.assertRaises(ValueError):
                            bundler.bundle(path)
                        self.assertEqual(path.read_bytes(), before)
                output = parity / "separate.html"
                bundler.bundle(output)
                self.assertIn("aW1hZ2UgZml4dHVyZQ==", output.read_text())

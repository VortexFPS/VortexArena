#!/usr/bin/env python3
"""Bundle the local parity decision page, its real screenshots and its report in one HTML file."""

import argparse
import base64
import json
from pathlib import Path
import re


PARITY = Path(__file__).resolve().parents[1] / "planning/parity"
PAGE = PARITY / "review/index.html"


def bundle(destination):
    source = PAGE.read_text(encoding="utf-8")
    marker = "const media = path => path;"
    if source.count(marker) != 1:
        raise ValueError("Expected one media resolver in the decision page")
    inputs = {PAGE, PARITY / "ORDERED-FOLLOWUP.md"}
    data = {}
    for _, name in sorted(set(re.findall(r"""(["'])([.\w/-]+\.webp)\1""", source))):
        path = (PAGE.parent / name).resolve()
        path.relative_to(PARITY)
        inputs.add(path)
        data[name] = base64.b64encode(path.read_bytes()).decode("ascii")
    if not data:
        raise ValueError("The decision page references no screenshots")
    report = base64.b64encode((PARITY / "ORDERED-FOLLOWUP.md").read_bytes()).decode("ascii")
    resolver = """
function localAsset(type, data) {
    const bytes = Uint8Array.from(atob(data), char => char.charCodeAt(0));
    return URL.createObjectURL(new Blob([bytes], {type}));
}
const urls = new Map(Object.entries(ASSET_DATA).map(([name, data]) =>
    [name, localAsset("image/webp", data)]));
const media = path => {
    if (!urls.has(path)) throw new Error("Missing bundled screenshot: " + path);
    return urls.get(path);
};
const reportLink = document.querySelector("#results");
reportLink.href = localAsset("text/markdown", REPORT_DATA);
reportLink.download = "ORDERED-FOLLOWUP.md";
reportLink.textContent = "Download the implementation report";
""".replace("ASSET_DATA", json.dumps(data)).replace("REPORT_DATA", json.dumps(report))
    if destination.suffix.lower() not in {".html", ".htm"}:
        raise ValueError("Choose an HTML file for the bundle")
    if destination.exists() and any(destination.samefile(path) for path in inputs):
        raise ValueError("The bundle destination points to its source page, report or screenshots")
    destination.write_text(source.replace(marker, resolver), encoding="utf-8")
    print(f"Bundled {len(data)} distinct screenshots and the report in {destination}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="Path for the standalone HTML file")
    bundle(parser.parse_args().output)

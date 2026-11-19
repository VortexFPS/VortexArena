"""Pinned xonotic-data.pk3dir used by the native implementation registry."""
from hashlib import sha1
from pathlib import Path
import subprocess

PIN = "863cd3e8456e18f577f7fe5fef857ff55ae62cc0"


def git(root: Path, *args: str) -> str:
    proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True)
    if proc.returncode:
        raise SystemExit(proc.stderr.strip() or f"Git could not read {root}")
    return proc.stdout.strip()


def require_reference(root: Path) -> None:
    if Path(git(root, "rev-parse", "--show-toplevel")).resolve() != root.resolve() or git(root, "rev-parse", "HEAD") != PIN:
        raise SystemExit(f"Expected the Xonotic data repository root at {PIN}.")


QC_EXTENSIONS = {".qc", ".qh", ".inc"}


def verify_qc_inputs(root: Path) -> None:
    """Verify the actual bytes used by the coverage, caller, and ordered audits."""
    require_reference(root)
    witnesses = {"qcsrc/Makefile", "qcsrc/tools/qcc.sh", "xonotic-server.cfg"}
    tree = subprocess.check_output(["git", "-C", str(root), "ls-tree", "-rz", PIN,
                                    "--", "qcsrc", "xonotic-server.cfg"])
    expected_sources = set()
    checked_witnesses = set()
    for record in tree.split(b"\0"):
        if not record:
            continue
        header, name = record.split(b"\t", 1)
        relative = name.decode("utf-8")
        path = root / relative
        if relative in witnesses:
            checked_witnesses.add(relative)
        elif path.suffix.lower() in QC_EXTENSIONS:
            expected_sources.add(path)
        else:
            continue
        if not path.is_file() or path.is_symlink():
            raise SystemExit(f"Missing or substituted pinned QC input: {relative}")
        content = path.read_bytes()
        digest = sha1(b"blob " + str(len(content)).encode("ascii") + b"\0" + content)
        if digest.hexdigest() != header.split()[2].decode("ascii"):
            raise SystemExit(f"QC input bytes differ from {PIN}: {relative}")
    if checked_witnesses != witnesses or not expected_sources:
        raise SystemExit(f"Incomplete QC reference tree at {PIN}")
    scanned = {path for path in (root / "qcsrc").rglob("*")
               if path.suffix.lower() in QC_EXTENSIONS and path.is_file()}
    if extra := sorted(scanned - expected_sources):
        raise SystemExit(f"Untracked QC input outside the pinned tree: {extra[0].relative_to(root)}")

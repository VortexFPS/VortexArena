#!/usr/bin/env python3
"""Asset-reference checker: every asset path the port's code names must resolve in the VFS.

The bug class this mechanizes: code referencing an asset that isn't there (the hitsound loading
non-existent `misc/hitconfirm` instead of `misc/hit.wav`) or that exists in a format the port
can't load (`models/casing_shell.mdl` — IDPO magic). Both were previously found by luck (one in
an audit, one in a boot log); this finds them wholesale, before a runtime path ever fires.

Method:
  1. Collect literal asset-path strings from src/**/*.cs + game/**/*.cs (prefixes: models/ sound/
     sounds/ gfx/ textures/ particles/ maps/ env/ cubemaps/ scripts/). Interpolated/concatenated
     paths can't be checked statically and are skipped (counted).
  2. Build the VFS view the game actually mounts: every data/*.pk3dir tree and data/*.pk3 zip, PLUS
     data/maps/*.pk3 — the per-map packs, which sit one level down and were being missed entirely.
  3. Resolve each reference with DarkPlaces' fallback rules: images try .tga/.png/.jpg[/.dds],
     sounds try .wav<->.ogg, env/ skyboxes try the 6 _ft.._dn side suffixes.
  4. For model hits, sniff the magic: IDP3 (md3) / IQM / DPM / Quake MDL v6 ok; other
     magics are flagged present-but-unsupported.

Findings are leads: a "missing" ref may be dead code or a write-path; check before filing.
Known/accepted refs live in planning/parity/asset-check-known.yaml (fnmatch patterns).

Outputs planning/parity/ASSET-CHECK.md (+ _asset-check.json).
Run: python tools/parity-asset-check.py
"""
from __future__ import annotations
import datetime, fnmatch, json, pathlib, re, sys, zipfile
try:
    import yaml
except ImportError:
    sys.exit("PyYAML required: pip install pyyaml")

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "planning" / "parity"
# The COMMITTED port content. Was ROOT/"assets"/"data", which on a dev box is a junction to the
# pristine upstream reference — so this checker validated UPSTREAM content while its docstring claimed
# to model "the VFS view the game actually mounts". Same root cause as the cvar differ's self-compare.
DATA = ROOT / "data"
KNOWN_FILE = OUT / "asset-check-known.yaml"

PREFIXES = ("models/", "sound/", "sounds/", "gfx/", "textures/", "particles/", "maps/",
            "env/", "cubemaps/", "scripts/")
# Anchored on the prefix's slash and a non-slash tail: bare words (sounds, modelscale) and pure
# directory-join prefixes ("models/", "sound/cdtracks/") are not checkable references.
LIT_RE = re.compile(r'(?<!\$)"((?:%s)[A-Za-z0-9_\-./]*[A-Za-z0-9_\-.])"' % "|".join(re.escape(p) for p in PREFIXES))
IMG_EXTS = (".tga", ".png", ".jpg", ".jpeg", ".dds")
SND_EXTS = (".wav", ".ogg")
MODEL_EXTS = (".md3", ".iqm", ".dpm", ".mdl", ".md2", ".obj", ".zym", ".psk")
SKY_FACE_SETS = (
    ("px", "nx", "py", "ny", "pz", "nz"),
    ("posx", "negx", "posy", "negy", "posz", "negz"),
    ("rt", "lf", "bk", "ft", "up", "dn"),
)


class Vfs:
    """Union view over the packs the game mounts, case-insensitive.

    Mirrors VirtualFileSystem.MountContentRoot: the per-map packs under data/maps/ AND the packs at the
    data root. Missing the maps/ level was why this tool reported "mounts=6" and a pile of phantom
    missing refs on a fully-populated tree — the six .pk3dir at the root were all it could see, so every
    map-carried texture, shader and model looked absent.

    Precedence: setdefault means FIRST writer wins, and the root packs are indexed first, so core content
    outranks a map pack that happens to carry the same path. That matches MountGameDir's prepend
    semantics (the game mounts maps/ first, then the root, and later mounts win), so a map cannot shadow
    core content in either place.
    """

    def __init__(self, data: pathlib.Path):
        self.index: dict[str, tuple[str, object]] = {}  # lower-path -> (mount label, dir-Path or (zip, member))
        self.mounts: list[str] = []

        def add_dir(d: pathlib.Path) -> None:
            self.mounts.append(d.name)
            for p in d.rglob("*"):
                if p.is_file():
                    self.index.setdefault(p.relative_to(d).as_posix().lower(), (d.name, p))

        def add_zip(z: pathlib.Path) -> None:
            try:
                zf = zipfile.ZipFile(z)
            except (OSError, zipfile.BadZipFile):
                return
            self.mounts.append(z.name)
            for member in zf.namelist():
                if not member.endswith("/"):
                    self.index.setdefault(member.lower(), (z.name, (zf, member)))

        # First writer wins here. The native VFS prepends each mount, giving loose
        # files first priority, then packs in reverse name order within each root.
        for directory in (data, data / "maps"):
            if not directory.is_dir():
                continue
            add_dir(directory)
            for entry in sorted(directory.iterdir(), key=lambda p: p.name.lower(), reverse=True):
                suffix = entry.suffix.lower()
                if entry.is_dir() and suffix in {".pk3dir", ".dpkdir"}:
                    add_dir(entry)
                elif entry.is_file() and suffix in {".pk3", ".dpk", ".obb", ".pak"}:
                    add_zip(entry)

    def exists(self, path: str) -> bool:
        return path.lower() in self.index

    def read_head(self, path: str, n: int = 32) -> bytes:
        hit = self.index.get(path.lower())
        if hit is None:
            return b""
        _, src = hit
        try:
            if isinstance(src, pathlib.Path):
                with src.open("rb") as f:
                    return f.read(n)
            zf, member = src
            with zf.open(member) as f:
                return f.read(n)
        except OSError:
            return b""


def resolve(vfs: Vfs, ref: str) -> tuple[str, str | None]:
    """-> (status, resolved_path). status: ok | missing | unsupported-format."""
    stem, ext = (ref.rsplit(".", 1)[0], "." + ref.rsplit(".", 1)[1].lower()) if "." in ref.rsplit("/", 1)[-1] else (ref, "")

    def first_hit(cands):
        for c in cands:
            if vfs.exists(c):
                return c
        return None

    if ext in MODEL_EXTS or (not ext and ref.startswith("models/")):
        cands = [ref] if ext else [stem + e for e in MODEL_EXTS]
        hit = first_hit(cands)
        if hit is None:
            return "missing", None
        head = vfs.read_head(hit)
        if head[:4] == b"IDP3" or head[:8] == b"IDPO\x06\x00\x00\x00" or head.startswith(b"INTERQUAKEMODEL") or head.startswith(b"DARKPLACESMODEL"):
            return "ok", hit
        if hit.lower().endswith(".obj"):
            return "ok", hit
        return "unsupported-format", hit  # IDP2 or unknown magic

    if ref.startswith(("sound/", "sounds/")) or ext in SND_EXTS:
        cands = [stem + e for e in SND_EXTS] if ext in SND_EXTS or not ext else [ref]
        hit = first_hit(cands if cands else [ref])
        return ("ok", hit) if hit else ("missing", None)

    if ref.startswith("cubemaps/") and ext == "":
        # AssetSystem.DefaultReflectCubemap requires all six DP box faces.
        faces = [first_hit(ref + face + e for e in IMG_EXTS) for face in SKY_FACE_SETS[0]]
        return ("ok", faces[0]) if all(faces) else ("missing", None)

    if ref.startswith("env/") and ext == "":
        # SkyboxLoader accepts all six faces in the same naming convention.
        def find_face(side):
            names = (ref + "_" + side, ref + side, "env/" + ref + side, "gfx/env/" + ref + side)
            return first_hit(name + e for name in names for e in IMG_EXTS)

        for convention in SKY_FACE_SETS:
            faces = [find_face(side) for side in convention]
            if all(faces):
                return "ok", faces[0]
        return "missing", None

    if ext in IMG_EXTS or ext == "":
        cands = ([ref] if ext else []) + [stem + e for e in IMG_EXTS]
        hit = first_hit(cands)
        return ("ok", hit) if hit else ("missing", None)

    return ("ok", ref) if vfs.exists(ref) else ("missing", None)


def collect_refs() -> tuple[dict[str, list[str]], int]:
    """literal ref -> [file:line, ...]; plus count of interpolated (unchecked) asset-ish strings."""
    refs: dict[str, list[str]] = {}
    interpolated = 0
    interp_re = re.compile(r'\$"(?:%s)[^"]*\{' % "|".join(p.rstrip("/") for p in PREFIXES))
    for top in ("src", "game"):
        for p in sorted((ROOT / top).rglob("*.cs")):
            if any(part in ("obj", "bin", ".godot") for part in p.parts):
                continue
            try:
                text = p.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            rel = p.relative_to(ROOT).as_posix()
            for number, line in enumerate(text.splitlines(), 1):
                if line.lstrip().startswith("//"):
                    continue
                interpolated += len(interp_re.findall(line))
                for m in LIT_RE.finditer(line):
                    refs.setdefault(m.group(1), []).append(f"{rel}:{number}")
    return refs, interpolated


def main():
    if not DATA.is_dir():
        sys.exit(f"content tree not found: {DATA}\n"
                 f"       core content is committed; for maps run tools/data/fetch-maps.py")
    known: list[tuple[str, str]] = []
    if KNOWN_FILE.exists():
        for e in (yaml.safe_load(KNOWN_FILE.read_text(encoding="utf-8")) or {}).get("known") or []:
            known.append(((e.get("path") or "").lower(), e.get("note", "")))

    vfs = Vfs(DATA)
    refs, interpolated = collect_refs()
    missing, unsupported, ok = [], [], 0
    exceptions = []
    for ref in sorted(refs):
        if exception := next(((pat, note) for pat, note in known if fnmatch.fnmatch(ref.lower(), pat)), None):
            pattern, note = exception
            exceptions.append({"ref": ref, "pattern": pattern, "reason": note, "where": refs[ref]})
            continue
        status, hit = resolve(vfs, ref)
        if status == "ok":
            ok += 1
        elif status == "missing":
            missing.append((ref, refs[ref]))
        else:
            unsupported.append((ref, hit, refs[ref]))

    today = datetime.date.today().isoformat()
    md = [
        "# Asset reference check",
        "",
        f"_Generated {today} by `tools/parity-asset-check.py`. {len(refs)} literal asset refs from"
        f" src/+game resolved against {len(vfs.mounts)} mounts ({len(vfs.index)} files);"
        f" {interpolated} interpolated refs skipped (not statically checkable)._",
        "",
        "Findings are LEADS: a missing ref may be dead code or a write-path. Confirm, then fix the",
        "path/asset — or record it in [asset-check-known.yaml](asset-check-known.yaml) if accepted.",
        "",
        f"## Missing ({len(missing)}) — referenced by code, no file resolves",
        "",
    ]
    if missing:
        md += ["| ref | referenced at |", "|---|---|"]
        md += [f"| `{r}` | {'; '.join(w[:3])}{' …' if len(w) > 3 else ''} |" for r, w in missing]
    else:
        md.append("none.")
    md += ["", f"## Present but unsupported format ({len(unsupported)}) — magic sniff failed", ""]
    if unsupported:
        md += ["| ref | resolved file | referenced at |", "|---|---|---|"]
        md += [f"| `{r}` | `{h}` | {'; '.join(w[:2])} |" for r, h, w in unsupported]
    else:
        md.append("none.")
    if exceptions:
        md += ["", "## Documented exceptions", "",
               "| Detected reference | Reason |", "|---|---|"]
        md.extend(f"| `{entry['ref']}` | {entry['reason']} |" for entry in exceptions)
    md += ["", f"_{ok} references resolved and passed the extension or header check. Loading and rendering "
           "require separate tests. Full data in `_asset-check.json`._", ""]

    (OUT / "ASSET-CHECK.md").write_text("\n".join(md), encoding="utf-8")
    (OUT / "_asset-check.json").write_text(json.dumps({
        "generated": today,
        "missing": [{"ref": r, "where": w} for r, w in missing],
        "exceptions": exceptions,
        "unsupported": [{"ref": r, "resolved": h, "where": w} for r, h, w in unsupported],
        "ok": ok, "interpolated_skipped": interpolated,
    }, indent=1), encoding="utf-8")
    print(f"refs={len(refs)} ok={ok} missing={len(missing)} unsupported={len(unsupported)} "
          f"interpolated_skipped={interpolated} mounts={len(vfs.mounts)}")


if __name__ == "__main__":
    main()

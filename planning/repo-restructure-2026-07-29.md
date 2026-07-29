# Repo restructure, data consolidation, and the Tier-1 rename

**Date:** 2026-07-29 · **Status:** plan, not yet executed · **Revised:** 2026-07-29 (readiness pass)

> **Readiness pass, 2026-07-29.** Every count and path below was re-verified against the working tree.
> Four things had drifted since the first draft and are corrected in place: `feature/map-editor` has
> **landed** (so Stage 0 item 1 is done), the repo **moved on disk** from
> `Projects\Xonotic\XonoticGodot` to `Projects\Vortex\VortexArena` (so several source paths in Stage 0
> and Stage 1 were dead), the touch-list counts had grown, and G13 turned out to be a **prerequisite**
> rather than a stage-3 item — it now has its own Stage −1. One decision was added: **D8**, the Vortex
> config layer (§11), which replaces the config half of the old item 36.

Vortex Arena currently builds its content tree by cloning four upstream Xonotic repositories at
build time. This plan stops that, moves the runtime content into the game repo, splits map sources
into a `VortexMaps` submodule, re-encodes 5,264 TGA files as PNG, and folds in the
`XonoticGodot.* → VortexArena.*` rename that `docs/REBRANDING.md` Decision 3 already settled.

---

## 1. What the existing docs already cover

Five documents bear on this. Four are on `main`; ADR-0015 is only on `feature/launcher-updater`.

**[`docs/REBRANDING.md`](../docs/REBRANDING.md)** is the closest thing to a structure plan we have.
Its Decision 3 settles the internal-ID rename as a clean-break big-bang covering Tier 0
(campaign id, gamename, env var, config filenames, bundle id) and Tier 1 (namespaces, `.sln`,
`.csproj`, assemblies, artifact filenames). Its Phase 4 is the namespace sweep. Decision 2 walks
the asset/trademark spectrum and lands on Option B, curated divergence, with one line about the
end state: "under C you'd host your own asset repo." That line is the entire prior treatment of
data ownership, and it is the hole this plan fills.

**[ADR-0006](decisions/ADR-0006-asset-pipeline.md)** specified an offline-first asset pipeline:
convert the shipped asset set to optimized resources ahead of time, keep a runtime loader as the
fallback. The runtime loader shipped. The offline conversion never did. The TGA to PNG pass below
is the first piece of that offline half to actually get built.

**[ADR-0008](decisions/ADR-0008-solution-structure.md)** has drifted from reality. It specifies
`XonoticGodot.Client` and `XonoticGodot.Menu` as separate projects mirroring `csprogs.dat` and
`menu.dat`. Neither exists. Both live in the root Godot host project under `game/client/` and
`game/menu/`. See §10 for the recommendation.

**[ADR-0014](decisions/ADR-0014-ci-packaging-distribution.md)** established the packaging shape
this plan has to keep working: fat per-platform zips, data laid beside the binary, and
`DataPaths.Resolve` probing executable-relative paths. It also documents `download-assets.sh` as
the single source of asset truth, keyed in CI by `hashFiles('download-assets.sh')`.

**ADR-0015** (`git show feature/launcher-updater:planning/decisions/ADR-0015-launcher-updater.md`)
designed the launcher: Avalonia 11, Velopack self-update, and a split payload where a
content-addressed `assets-<hash12>.zip` is uploaded once and reused across releases. That
content-addressing scheme survives this plan unchanged; only the input to the hash changes.

The 2026-07-09 docs reorg that split `docs/` (operational how-to) from `planning/` (design and
trackers) is done and stays as-is.

---

## 2. Decisions taken 2026-07-29

| # | Decision |
|---|---|
| D1 | Stop cloning `gitlab.com/xonotic` at build time. Vortex Arena hosts its own content. |
| D2 | Runtime content moves into the game repo at `data/`, committed as ordinary git objects. |
| D3 | Map sources move to a new **`VortexMaps`** repo, consumed by the game repo as a git submodule. |
| D4 | All 5,264 `.tga` files are re-encoded as `.png`. `.dds` and `.jpg` are left alone (§4.3). |
| D5 | The Tier-1 rename from REBRANDING.md Decision 3 lands in the same motion. |
| D6 | **No Git LFS.** Reasoning in §3. |
| D7 | Compiled maps are **not committed** anywhere. `VortexMaps` CI publishes them as GitHub Release assets; the game repo pins them in a lockfile and fetches them (§5.3.1). The line: hand-edited content is committed, compiled output is fetched. |
| D8 | **The Xonotic config files are never edited.** Vortex divergence lives in an additive `vortex-*.cfg` layer exec'd after them and before `LockDefaults()`. This replaces the config-rename half of the old item 36, which is dropped rather than deferred. Reasoning and wiring in **§11**. |

---

## 3. Git LFS on a public repo

Being open source does not help here, and in one respect makes it worse.

GitHub Free and Pro accounts include 10 GiB of LFS storage and 10 GiB of bandwidth per month, the
same allowance whether the repository is public or private. Bandwidth is billed to the repository
owner, not the person cloning, and pulls against a fork count toward the **parent** repository's
bandwidth. Every anonymous clone and every fork's fetch spends the quota on the owner's account,
with no mechanism to rate-limit strangers. GitHub replaced the old pre-paid data packs with metered
billing, so exceeding the allowance produces a bill rather than a hard stop.

At the sizes involved, roughly 2 GB of content in the main repo, 10 GiB of monthly bandwidth is
about five full clones. A public arena shooter that attracts any attention exhausts that in a day,
and the failure mode is a charge, not an error.

Plain git objects have no bandwidth meter. The constraint that survives is GitHub's hard **100 MB
per-file limit**, which rejects the push outright. Both compiled map packs violate it today:
`xonotic-20230620-maps.pk3` is 597 MB and `xonotic-20230620-nexcompat.pk3` is 120 MB. §5.3 handles
that.

The limit is enforced, not advisory. The default LFS budget on a non-Enterprise account is $0, and
at $0 the account is not billed for overage; LFS is **blocked for the rest of the calendar month**,
with clients reporting "This repository is over its data quota."

### 3.1 None of this touches how players download the game

LFS quotas govern `git clone` and `git lfs pull` against the repository. They have nothing to do
with **GitHub Releases**, which is what ADR-0014 and ADR-0015 already use for distribution: release
assets have **no bandwidth limit and no metering**, allow 2 GiB per file, and up to 1,000 assets per
release.

That is how every large project ships large downloads, and it is why the fat per-platform zips and
the launcher's content-addressed asset pack are unaffected by anything in this section. Players
downloading through the launcher, and anyone grabbing a zip from the releases page, cost nothing and
are never throttled. The only population that would ever have touched LFS bandwidth is developers
and forks cloning the source repository.

Sources: [Git LFS billing](https://docs.github.com/en/billing/concepts/product-billing/git-lfs),
[about releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases),
[community discussion 68492](https://github.com/orgs/community/discussions/68492).

---

## 4. What the content tree actually holds

### 4.1 Current inventory

Measured 2026-07-29 against `assets/data/`. The "on disk" column excludes the `.git` directories of
the four upstream clones, which add a further 2,772 MB of pack files that exist only because the
content arrives as git repositories.

| Tree | Purpose | On disk | Of which TGA |
|---|---|---:|---:|
| `xonotic-data.pk3dir` | runtime content: textures, models, gfx, sound | 2,543 MB | 2,310 MB (2,233 files) |
| `xonotic-maps.pk3dir` | map **sources**: 180 `.map`, source textures, `.ase` models | 3,281 MB | 2,941 MB (3,031 files) |
| `xonotic-20230620-maps.pk3` | compiled maps: 31 BSPs + DDS textures | 597 MB | none (zip) |
| `xonotic-20230620-nexcompat.pk3` | Nexuiz compatibility textures | 120 MB | none (zip) |
| `xonotic-music.pk3dir` | 44 ogg tracks | 106 MB | none |
| four `font-*.pk3dir` | DejaVu, Nimbus Sans L, Unifont, Xolonium | 13 MB | none |
| **Total** | | **6,660 MB** | **5,251 MB** |

`xonotic-maps.pk3dir` holds exactly one `.bsp`. The 31 BSPs the game actually loads live inside
`xonotic-20230620-maps.pk3`. The 3.3 GB source tree is a build input, which is why it belongs in
`VortexMaps` rather than in every player's and contributor's checkout.

`xonotic-data.pk3dir` also carries content the port cannot execute: `progs.dat`, `csprogs.dat`,
`menu.dat` and their `.lno` sidecars total 21 MB of compiled QuakeC bytecode, plus `qcsrc/` at
7.5 MB, `.tmp/` at 37 MB, and `demos/` at 15 MB. `download-assets.sh` prunes some of these after
each clone, but `git pull` restores them on the next run, so the prune fights the clone forever.
Owning the tree ends that.

### 4.2 TGA to PNG: measured, and smaller than it first looks

Measured on an 80-file stratified sample (48.2 MB) drawn across the full size distribution, encoded
with `ffmpeg -compression_level 9` and no `-pix_fmt` override so source bit depth is preserved.
Zero failures. Sample header mix: 51 RGBA 32-bit, 25 RGB 24-bit, 4 grayscale/paletted 8-bit; 28 of
them RLE-compressed.

| Form | Sample | Share of raw TGA | Projected over 5.13 GB |
|---|---:|---:|---:|
| TGA on disk | 48.2 MB | 100% | 5.13 GB |
| TGA as git stores it (zlib-6) | 20.7 MB | 43.1% | 2.21 GB |
| PNG on disk | 18.6 MB | 38.6% | **1.98 GB** |
| PNG as git stores it | 18.6 MB | 38.6% | **1.98 GB** |

The working-tree saving is 61.4%. The git-pack saving is 10.5%.

Both numbers are real and they measure different things. Git already deflates the TGAs, so the
conversion buys little in repo size or clone time. What it buys is 3.15 GB off every checkout,
every extracted release, and every player's installed footprint, plus 61% fewer bytes read from
disk at map load. If the goal is a smaller repository, this pass is worth 10%. If the goal is a
smaller install and faster texture I/O, it is worth 61%. The second is the one to quote.

Cost on the other side: PNG decode is zlib inflate plus per-scanline unfiltering, where TGA decode
is close to a memcpy through the hand-written `TgaDecoder`. Map-load CPU goes up while map-load I/O
goes down. Which wins is not predictable from first principles, so §6 gates the conversion on a
`tools/perf-smoke.ps1` run rather than assuming.

### 4.3 Do not convert the DDS files

The two compiled map packs contain 6,964 `.dds` files totalling 943 MB uncompressed. These are
S3TC/DXT block-compressed textures that `AssetSystem.DecodeDds` passes to the GPU still compressed.
Re-encoding them as PNG would enlarge them on disk, and would multiply their VRAM footprint by four
to eight by forcing an uncompressed upload. The 749 `.jpg` files are already lossy; re-encoding them
as PNG would inflate them with no quality gain. TGA is the only format in the tree that is both
uncompressed and losslessly convertible, which is why it is the only one this pass touches.

---

## 5. Planned structure

Three repositories, all under the **`VortexFPS`** organization (renamed from `VortexArena` to free
that name for the repo). `VortexArena` transfers in from `bryankruman/`; `VortexMaps` and
`VortexLauncher` are created there from the start. See §5.0 for what the transfer costs.

### 5.0 The `VortexFPS` organization

`bryankruman/VortexArena` moves to **`VortexFPS/VortexArena`**; the new repos are created under the
same org. Do the transfer **before stage 0**, so every URL this migration touches gets written once
instead of twice.

> **The transfer has happened — done 2026-07-29.** `gh repo view` reports `VortexFPS/VortexArena` as
> the canonical name. It surfaced the way §5.0 predicted it would: a `git push` against the stale
> remote succeeded and printed *"This repository moved. Please use the new location"*, which is the
> silent-redirect trap this section warns about — the push worked, so nothing forced the issue. The org
> currently holds `VortexArena` and the superseded `VortexData`; **`VortexMaps` and `VortexLauncher`
> do not exist yet** and stage 1 is blocked on `VortexMaps`.

What the move actually changes:

- **Six tracked files carry the old owner** and need rewriting: `README.md` (two CI badges),
  `docs/RELEASING.md` (the releases URL), `tools/package.sh` (the bundled per-zip README),
  `tools/upstream-ledger-html.py` (`GITHUB_BLOB`, a constant — regenerate `LEDGER.html` rather than
  hand-editing its ~60 generated links), and `planning/upstream-watch/README.md`.
  `planning/wave-a3/briefs/T33.json` names the pre-rename remote and stays frozen.
- **The GitHub Pages URL changes** from `bryankruman.github.io/VortexArena` to
  `vortexfps.github.io/VortexArena`. Check `.github/workflows/pages.yml` and the upstream-watch
  ledger link together.
- **`git remote set-url origin git@github.com:VortexFPS/VortexArena.git`**, in the main checkout and
  in every surviving worktree. The `.git` directory is shared across worktrees, so one change covers
  them all.
- **Check the org's default Actions permissions.** `release.yml` declares
  `permissions: contents: write` for the release upload. An organization can set a read-only default
  that a workflow-level grant cannot raise, in which case releases fail at the upload step with a 403
  and nothing earlier in the job complains.
- **Org rulesets are better than repo rules for G12.** Tag protection for `VortexMaps` can be an
  organization ruleset covering every repo, which also covers community map repos later without
  configuring each one.

**The transfer is a third continuity break, and it belongs with the other two.** The launcher reads
`https://github.com/<repo>/releases/latest/download/latest.json`, so transferring changes the feed
host, and stage 5 separately changes every artifact filename. GitHub redirects transferred repos, so
an old install keeps working through the redirect rather than breaking outright, but there is no
reason to spend two cutovers: **the org transfer, the artifact rename, and the first
`VortexArena`-named release should be one deliberate event**, documented once in `docs/RELEASING.md`
and the launcher repo.

Not a factor: LFS quotas. A free organization gets the same 10 GiB as a personal account, so the move
neither helps nor hurts, and D6 means we are not using LFS anyway.

#### `VortexFPS/VortexData` is superseded and out of scope

A prior repo, **`VortexFPS/VortexData`** (public, ~1.15 GB, last pushed 2026-07-09): *"Vortex Arena
art assets — textures, models, sounds, UI images, particles, shaders. Trimmed fork of Xonotic's
`xonotic-data.pk3dir` (art only; no code/config/maps)."* It was an earlier answer to the same question
D2 answers, and D2 supersedes it: runtime content is committed to `data/core.pk3dir/` in the game repo.

**Nothing in this plan or this repository depends on it.** `VortexData` appears in zero tracked files
and in zero commits on any branch (`git log --all -S VortexData` is empty). It can be deleted whenever
its owner chooses, independently of this migration, and nothing here needs to change first.

Two things to know before that happens:

- **It is not a drop-in for `data/core.pk3dir/`, so do not shortcut stage 1 item 8 with it.** The game
  reads *config* from the data pack — `bal-wep-*.cfg` (the weapon balance sets, pinned by
  `WeaponBalanceTests`), `_hud_common.cfg`, `autoexec.cfg` — plus `scripts/*.shader`. `VortexData`
  excludes config by construction. The source for `data/core.pk3dir/` stays `xonotic-data.pk3dir`.
- **Its file list is still useful as a cross-check.** Somebody already did the work of separating art
  from non-art there. Stage 1 item 8 re-derives that split from scratch with a different prune list, so
  diffing the two before deleting is a cheap way to catch anything one of them drops that the other
  keeps.

Incidental confirmation while looking it up: `VortexArena/VortexData` and `VortexFPS/VortexData` both
resolve to the same repository, so GitHub's org-rename redirect is working as this section assumes the
repo-transfer redirect will.

### 5.1 `VortexArena` — game code and runtime content

```
VortexArena/
├── VortexArena.sln                  (was XonoticGodot.sln)
├── VortexArena.csproj               Godot host: client + headless dedicated server
├── project.godot                    config/name = "Vortex Arena"
├── Main.cs                          --data override, launch flags
│
├── src/
│   ├── VortexArena.Common/          gameplay, physics, protocol defs   (no Godot)
│   ├── VortexArena.Engine/          sim core, collision, trace, VFS    (no Godot)
│   ├── VortexArena.Net/             wire format, prediction, recon     (no Godot)
│   ├── VortexArena.Formats/         IBSP, MD3, IQM, DPM, Q3 shader     (no Godot)
│   ├── VortexArena.Server/          dedicated server logic             (no Godot)
│   └── VortexArena.SourceGen/       Roslyn generators
│
├── game/                            Godot-side: client/, menu/, hud/, net/, console/, loaders/
├── tests/VortexArena.Tests/
│
├── data/                            ← COMMITTED. was assets/data, was gitignored
│   ├── .gdignore                    keeps the Godot editor out of 17k files
│   ├── .gitattributes               `* -text` (G2)
│   ├── core.pk3dir/                 runtime content (ex-xonotic-data.pk3dir, pruned)
│   │   ├── textures/ models/ gfx/ sound/ particles/ scripts/
│   │   ├── *-xonotic.cfg  physicsX.cfg  …   upstream, never edited (D8, §11)
│   │   └── vortex-*.cfg             our divergence layer, exec'd after them (D8, §11)
│   ├── music.pk3dir/                44 ogg tracks
│   ├── font-{dejavu,nimbussansl,unifont,xolonium}.pk3dir/
│   ├── maps.lock.json               ← TRACKED. pins the VortexMaps release + per-map sha256
│   └── maps/<map>.pk3dir/           ← NOT tracked. fetched per the lockfile (§5.3.1, §9.3)
│
├── maps-src/                        ← GIT SUBMODULE → VortexMaps
│
├── ci/  tools/  docs/  planning/
└── tools/data/                      new: convert-tga.py, build-maps.py, verify-data.py
```

`data/` replaces `assets/data/`. The `assets/*` ignore rule comes out of `.gitignore`; the
`.gdignore` marker moves with the tree and stays tracked.

> **The `.pk3dir` suffixes are load-bearing, not cosmetic.** An earlier draft wrote plain `core/`,
> `music/` and `fonts/`. That does not resolve. `MountGameDir` mounts a subdirectory as a *pack* only
> if its name ends in `.pk3dir`/`.dpkdir`; anything else is just a subdirectory of the plain mount, so
> a request for `textures/foo.png` would look for `data/textures/foo.png` and miss
> `data/core/textures/foo.png` entirely. Keeping the `.pk3dir` suffix is what makes the type-rooted
> tree resolve, and it is the same convention the current `assets/data/xonotic-data.pk3dir` already
> relies on. See §9.3 for the same rule applied to maps.

`maps-src/` is a submodule, so a plain `git clone` of the game repo does not fetch it. Contributors
who need map sources run `git submodule update --init maps-src`.

### 5.2 `VortexMaps` — map sources and the map build

New repo. Holds what `xonotic-maps.pk3dir` holds today, after the TGA pass: 180 `.map` files,
source textures, `.ase`/`.md3` prefab models, skybox `env/`, and the shader scripts. Roughly
1,475 MB after conversion, down from 3,281 MB.

Layout splits by role, so a map's source, its q3map2 output, and its future `vmap` output sit side
by side rather than competing for one directory:

```
VortexMaps/
├── sources/                     Radiant/editor inputs — never shipped to players
│   ├── <mapname>/
│   │   ├── <mapname>.map        + any prefab sub-.maps it includes
│   │   ├── textures/            source textures (PNG after conversion)
│   │   ├── models/              .ase / .obj / .md3 prefabs
│   │   └── scripts/<mapname>.shader
│   └── shared/                  env/ skyboxes, common texture sets, shared prefabs
│
├── builds/
│   ├── q3map2/<mapname>/        .bsp + .ent + .mapinfo + .waypoints + shipped textures
│   └── bake/<mapname>/          vmap bake cache, keyed by truth hash (NOT "builds/vmap" — §9.4)
│
├── build/
│   ├── q3map2.toolchain         pinned compiler version + flags
│   ├── build-map.py             sources/<map> → builds/q3map2/<map>
│   └── publish.py               builds/<backend>/ → per-map release archives + lockfile
└── .github/workflows/           tag → build all maps → publish per-map archives as release assets
```

**Built output is committed to the game repo, not read from the submodule.** `maps-src/` is not
fetched by a default clone, so the game cannot depend on it at runtime. Instead, `VortexMaps` CI
packages `builds/<backend>/<mapname>/` into one archive per map and publishes them as **GitHub
Release assets**; the game repo pins and fetches them (§5.3.1). Sources exist in exactly one place,
builds exist as versioned artifacts, and nothing compiled is committed to either repo.

This layout is refined in §9.3 once the `.vmap` editor lands: `builds/vmap/` turns out to be the
wrong shape, because a `.vmap`'s truth sections are themselves the shipping form and only the bake
cache is a build output.

This is the repo the map-editor track writes into. The editor branch's own conclusion, recorded in
memory as `editor-branch-review-findings`, was that the pipeline is the blocker and the answer is to
write `.map` and let q3map2 do the rest. `VortexMaps` is where that pipeline lives.

### 5.3 Compiled maps: extract the packs, split by role

`xonotic-20230620-maps.pk3` bundles 31 maps into a single 597 MB file, which GitHub's 100 MB
per-file limit rejects. Extract it to loose files, one directory per map.

An earlier draft of this plan argued for repackaging as 31 per-map `.pk3`s instead, on the grounds
that extraction costs working-tree space for no gain. Reading the pack's actual contents overturns
that:

| Extension | Files | Raw | Zipped | Role |
|---|---:|---:|---:|---|
| `.dds` | 3,207 | 794.0 MB | 394.4 MB | runtime, GPU-compressed |
| `.bsp` | 31 | 192.0 MB | 51.1 MB | runtime |
| `.jpg` | 287 | 129.7 MB | 123.5 MB | runtime |
| `.map` | 150 | 65.0 MB | 6.6 MB | **source** |
| `.ase` | 183 | 30.9 MB | 2.6 MB | **source** |
| `.ogg` / `.mp3` | 116 | 16.6 MB | 16.2 MB | runtime |
| `.obj` / `.cache` / `.hardwired` / `.options` / `.sh` | 133 | 2.7 MB | 0.5 MB | **build residue** |
| everything else | 294 | 8.3 MB | 1.4 MB | runtime |

97 MB of Radiant sources and q3map2 build residue ship inside the runtime pack. Extraction lets
those move to `VortexMaps/sources/` and out of every player's install. The runtime remainder is
roughly 1,140 MB unpacked, 586 MB compressed.

Only 31 of the 150 `.map` basenames match a BSP; the other 119 are prefab sub-maps pulled in by the
main sources. All 150 belong in `VortexMaps/sources/`.

The pack contains **zero `.tga`** (lightmaps are internal to the BSPs), so the §4.2 conversion pass
never touches compiled map content.

### 5.3.1 Compiled maps are release assets, not commits

Committing the extracted builds into `data/maps/` would work, but it is committing build outputs,
and binary build outputs in git history are permanent. Every q3map2 rerun writes another ~586 MB
that no later commit can reclaim. That is tolerable while the map set is frozen at Xonotic 0.8.6 and
untenable once the `feature/map-editor` track starts producing map revisions.

So `VortexMaps` CI publishes per-map archives as **GitHub Release assets**, and the game repo tracks
a lockfile instead of the bytes:

```json
// data/maps.lock.json
{ "schema": 1, "source": "VortexFPS/VortexMaps", "release": "maps-2026.07", "backend": "q3map2",
  "maps": [ { "name": "stormkeep", "size": 24117248, "sha256": "…",
              "url": "https://github.com/VortexFPS/VortexMaps/releases/download/maps-2026.07/stormkeep-q3map2.zip" } ] }
```

`tools/data/fetch-maps.py` reads the lockfile, downloads what is missing or hash-mismatched, and
extracts to `data/maps/<map>.pk3dir/` (§9.3), which is gitignored. `ci/ci.sh` and `tools/package.sh` call it
automatically; a developer runs it once after cloning.

Why this is the right shape:

- **Release asset bandwidth is unmetered** (§3.1), so fetching costs nothing no matter how many
  people clone. This is the same reason the fat zips are safe.
- **The game repo stops growing when maps change.** A map revision is a one-line lockfile diff.
- **Per-map archives mean a map fix re-downloads ~19 MB**, not 600 MB.
- **It is ADR-0015's mechanism, not a new one.** The launcher already pins a content-addressed
  `assets-<hash12>.zip` by absolute URL in `latest.json` and dedupes uploads across releases. Maps
  become a second artifact class in that same scheme.
- **It generalizes to community maps.** A third-party map repo publishing the same archive shape is
  installable through the identical code path, which is the thing an arena shooter eventually wants.

The line this draws: **hand-edited content is committed, compiled output is fetched.**
`data/core.pk3dir/`, `data/music.pk3dir/` and the `font-*.pk3dir/` set are authored assets that
nobody generates, they change only when we
deliberately replace them, and committing them is what makes `git clone` yield a working game.
Maps are the one part of the tree with a compiler in front of it.

Cost of the split, stated plainly: a fresh clone no longer runs without one network fetch. That was
the thing D1 set out to remove. The difference is that the fetch is now a pinned, hash-verified
artifact from a repo we control, rather than four `git clone`s of moving upstream branches.

`nexcompat` at 120 MB is 3,757 DDS plus 462 JPG serving the Nexuiz-compat maps, so it rides along as
another release asset rather than a commit. Still worth checking whether anything references it.

### 5.4 `VortexLauncher` — extracted from the feature branch

The launcher currently lives under `launcher/` on `feature/launcher-updater` and is an Avalonia app
with its own release cadence, its own Velopack packaging, and no dependency on the game's build.
Give it its own repo. ADR-0015 moves with it; leave a stub in `planning/decisions/` pointing at the
new home so the ADR sequence stays readable.

The launcher's content-addressed asset scheme survives intact. Today `<hash12>` is
`hashFiles('download-assets.sh')`. After this plan it becomes a hash over the committed `data/`
tree, which is a better key: it changes when the content changes rather than when the download
script changes.

---

## 6. Migration

### 6.0 The ordering constraint that governs everything

**Convert before committing. Never commit a `.tga` into the game repo.**

An earlier draft had Stage 1 import the content and Stage 2 convert it. That order is wrong and the
mistake is unrecoverable: git keeps every blob ever committed, so importing 2,233 TGAs and then
converting them leaves **2.21 GB of dead TGA blobs in history forever**, on top of the 892 MB of PNG.
The repo would be permanently three times its necessary size, fixable only by a history rewrite that
invalidates every clone.

The same rule covers the map packs, the `.import` sidecars, and the dev directories: **stage the
tree, clean it, verify it, and only then make the first commit.** Everything below is ordered around
that.

Stages 0 through 2 all land in one squashed commit per repo, so a mistake is one `git reset` away
rather than a `filter-repo` run.

### 6.1 Gotcha register

Every trap this plan knows about, labelled so the stage items can point at them. G1 through G7 are
one-shot migration hazards; G8 through G15 are standing hazards, or hazards that outlive the
migration. G13 and G14 came out of the completeness pass on 2026-07-29 and are the reason §6.2
exists; G15 came out of the readiness pass the same day, with D8.

---

**G1 — Committing before converting bloats history permanently.** *(governs stage order, §6.0)*

- **Status:** **Accepted 2026-07-29** — stage order below is the agreed one.

- **Breaks:** the repo carries 2.21 GB of TGA blobs plus 892 MB of PNG, forever, for a tree whose
  useful content is 892 MB.
- **Why:** git never discards a blob that was ever committed. Deleting the `.tga` in a later commit
  removes it from the working tree, not from the pack.
- **Detect:** `git count-objects -vH` after the import commit. If `size-pack` is materially above the
  working-tree size of `data/`, dead blobs are already in.
- **Do:** stage the tree on disk, convert, verify, then commit once. Keep stages 0 to 2 as a single
  squashed commit per repo so a mistake is `git reset --hard`, not `git filter-repo`.
- **Cost of getting it wrong:** a history rewrite that invalidates every existing clone and fork.

---

**G2 — Line-ending normalization makes release zips differ per build host.**

- **Status:** **Accepted 2026-07-29** — `data/.gitattributes` with `* -text`.

- **Breaks:** `package.sh` on Windows and on a Linux runner produce byte-different zips from the same
  commit, so checksums do not reproduce and the launcher's asset-pack identity wobbles.
- **Why:** `.gitattributes` currently declares EOL rules only for `.sh`/`.ps1`/`.cmd`/`.bat`.
  Everything else falls to git's auto-detection. Binary formats (PNG, DDS, BSP, OGG) carry NUL bytes
  early and are detected correctly, but `data/` also holds real text: `.shader`, `.cfg`, `.skin`,
  `.mapinfo`, `.map`. Those get CRLF on a Windows checkout and LF on Linux.
- **Detect:** check out the same commit on both platforms and compare `sha256sum` of a `.shader`.
- **Do:** add a `data/.gitattributes` containing `* -text` **before** the first data commit. Placing
  it inside the tree beats a root-level `data/** -text` pattern because it travels with the directory
  and cannot be defeated by pattern-precedence surprises.
- **Note:** committed blob hashes are unaffected either way, so `git rev-parse HEAD:data` (§8.6)
  stays a valid content key regardless. This is purely about what lands on disk.

---

**G3 — 6,558 stale `.import` sidecars are sitting in the tree right now.**

- **Breaks:** 5.9 MB of Godot import metadata for a tree Godot is not supposed to import, committed
  permanently, and split across both new repos.
- **Why:** they predate the `.gdignore` marker. Measured today: 3,064 under `xonotic-data.pk3dir`,
  3,472 under `xonotic-maps.pk3dir`, 22 under `xonotic-music.pk3dir`.
- **Detect:** `find data -name '*.import' | wc -l` on the staged tree; expect 0.
- **Do:** exclude `*.import` in the staging copy (stage 1, items 8 and 9). **Accepted 2026-07-29.** Note that 3,472 of them
  would otherwise land in `VortexMaps`, not the game repo, so the exclusion has to be in both paths.

---

**G4 — The `.gdignore` marker has to move, and has to be there first.**

- **Status:** **Accepted 2026-07-29.**

- **Breaks:** the Godot editor walks `data/` and generates import sidecars for all 17,580 files,
  which is both a long stall and G3 all over again.
- **Why:** today the only marker is at `assets/.gdignore`, one level *above* `assets/data`. The new
  content root is `data/`, so the marker belongs at `data/.gdignore`.
- **Detect:** open the project in the editor after staging and watch for an import progress bar.
- **Do:** write `data/.gdignore` as part of the staging copy, before the editor next opens the
  project, and keep it tracked so a fresh checkout is protected before anything else exists.

---

**G5 — Case-only filename collisions. Checked: clean today, keep the guard.**

- **Status:** **Accepted 2026-07-29** — keep the guard, run it against source listings.

- **Breaks:** on a case-insensitive filesystem the second file silently overwrites the first during
  extraction, so the repo gets one file where the source had two, and the loss is invisible.
- **Why:** the dev box is NTFS, CI runners are ext4. Xonotic asset paths are mixed-case and the VFS
  folds case deliberately (`AssetSystem.cs:894`).
- **Measured:** zero collisions. `xonotic-20230620-maps.pk3` 0 of 4,391 entries,
  `nexcompat.pk3` 0 of 4,299, and the full extracted tree is 16,770 files with 16,770 distinct
  lowercased paths. **This is a guard, not a known problem.**
- **Do:** keep the check in the migration anyway, and run it against the *source listing* rather than
  the extracted tree. An extraction onto NTFS has already collapsed any collision, so scanning the
  result cannot find one. For the pk3s read the zip index; for the pk3dir clones read
  `git ls-files` inside the clone.
- **Why keep it:** future content additions, and the fact that `VortexMaps` will accept third-party
  map contributions authored on case-sensitive systems.

---

**G6 — A conversion that "succeeded" can still be wrong.**

- **Status:** **Accepted 2026-07-29** — per-file raw-decode comparison, not a spot check.

- **Breaks:** a texture that loads without error and renders wrong. Dropped alpha turns a decal
  opaque; a quantized 16-bit source bands a gradient. Nothing logs.
- **Why:** exit code 0 from the encoder only means it wrote a file. The sample here was 51 RGBA
  32-bit, 25 RGB 24-bit, and 4 grayscale/paletted 8-bit, with 28 RLE-compressed, so there are several
  distinct decode paths and only one of them is the common case.
- **Detect:** decode both forms to raw and compare, rather than trusting the encoder:
  `ffmpeg -i x.tga -f rawvideo -pix_fmt rgba -` against the same for the PNG, hashed.
- **Do:** make that comparison a per-file assertion inside `convert-tga.py`, not a spot check. 5,264
  files is small enough to verify exhaustively, and this is the failure least likely to be caught by
  any other gate.

---

**G7 — After the TGA delete there is no rollback source.**

- **Status:** **Accepted 2026-07-29** — archive outside git until the gate passes.

- **Breaks:** if the §8 perf gate fails and PNG decode proves too expensive, the original data exists
  only in a git history that G1 deliberately kept it out of.
- **Why:** the two decisions are in tension on purpose. Keeping TGAs in history for safety is exactly
  the 2.21 GB G1 exists to avoid.
- **Do:** archive the pre-conversion tree outside git until the gate passes, then discard it. Cheap
  insurance for a one-week window.
- **Note:** the likely outcome is not a revert. Map-load I/O drops 61% while decode CPU rises, and
  the mitigation if it regresses is the existing persistent asset cache
  (`cl_persist_asset_cache`), not the format.

---

**G8 — Seven open branches will conflict on nearly every file.**

- **Breaks:** every open feature branch, simultaneously. This restructure moves every asset path,
  relocates the solution file, and renames every namespace in `src/`.
- **Why:** the conflicts are not semantic, they are path-level, so git's rename detection helps less
  than usual and there is no merge strategy that resolves them cheaply.
- **Status:** **Decided 2026-07-29; the landing half is done.** Seven branches migrate afterwards, one
  at a time, through `docs/BRANCH-MIGRATION.md`.
- **Landed already (2026-07-29):** `feature/map-editor` — 46 commits, 26 `Vmap*` files, and the branch
  this whole §9 discussion is about. `claude/tool-selection-usage-design-8159cd` landed with it, which
  is why §9's `.vmap` single-text-file format is described as current rather than proposed. The vmap
  code is on `main`, so §9's layout applies to real files.
- **Merged, so delete rather than migrate — eight refs.** Each is `ahead=0` of `main` and carries no
  unique commits: `feature/map-editor`, `claude/tool-selection-usage-design-8159cd`,
  `claude/map-editor-backlog-continue-6865a0`, `claude/viberadiant-review-vortex-204d70`,
  `claude/vortex-arena-anti-cheat-b51f29`, `claude/vortex-startup-disclaimer-949f5e`,
  `fix/editor-grid-stringname-alloc`, `parity/port-recent-fixes`. Running
  `docs/BRANCH-MIGRATION.md` against any of them is wasted work; prune them in Stage −1 so the
  migration queue is unambiguous.
- **Migrates after — the real seven:** `feature/launcher-updater` (3 commits; becomes the stage 6
  extraction rather than a merge), `feature/demo-merge` (13), `feature/dedicated-server-v2` (12),
  `feature/anim-smoothness-ragdolls` (3), `feature/player-soft-collision` (2),
  `feature/playermodel-lean` (4), `fix/warpzone-view-smoothing` (3).
- **Verify the split before trusting it**, since a merged branch looks identical to an open one in
  `git branch`:

  ```bash
  git for-each-ref --format='%(refname:short)' refs/heads | while read b; do
    printf '%-50s ahead=%s\n' "$b" "$(git rev-list --count main..$b)"; done
  ```
- **What this changes:** `docs/BRANCH-MIGRATION.md` stops being a fallback and becomes the **primary
  path for all seven surviving branches**. That makes `tools/migrate-branch.sh` a deliverable of the
  restructure itself, not a thing to improvise later — it has to exist and be proven on the first
  branch before the others queue up behind it. Added as stage 3 item 28e.
- **Consequence worth pricing in:** each of those seven branches accrues additional drift for as long
  as it stays unmigrated, and their authors cannot merge `main` in the meantime without doing the
  migration anyway. Migrate them in a batch soon after the restructure rather than on demand.
- **Why it is listed as a gotcha:** it is the largest non-technical cost in the plan and the one most
  likely to be underestimated, because nothing about it shows up in a build or a test run.

---

**G9 — Godot rewrites `project.godot` and `export_presets.cfg`, and drops comments.**

- **Status:** **Accepted 2026-07-29** — hand-edit both files, add the pinning test.

- **Breaks:** the wobble fix, silently. `run/delta_smooth=false`,
  `common/physics_jitter_fix=0.0` and `common/physics_ticks_per_second=10` are load-bearing and
  non-obvious, and lines 41 to 50 of `project.godot` are the comment block explaining why.
- **Why:** Godot's config writer does not preserve comments, and the migration has to edit both files
  by hand anyway (`config/name`, `assembly_name`, the `assets/*` → `data/*` export filter).
- **Detect:** nothing catches it today. The symptom is a feel regression that only shows up in
  playtest, which is how it was found the first time.
- **Do:** edit both files by hand, never via the editor, and add a test pinning the three timing
  values so a future editor save fails the suite instead of shipping the regression.
- **Related:** the same file carries `custom_template/release`, which is G10.

---

**G10 — An empty `custom_template/release` ships a stock build silently.** *(§7.2)*

- **Status:** **Accepted 2026-07-29** — fetch + pre-export sha256 assertion.

- **Breaks:** the Windows release loses the mouse-input backport, and the frame-cadence stutter
  returns with nothing in the export output saying so.
- **Why:** Godot hard-aborts on a *wrong* template path (`ERR_FILE_NOT_FOUND`, names the path) but
  falls back to the stock template on an *empty* one. A wrong path fails loudly; an empty one fails
  invisibly.
- **Current state:** the path is absolute to one dev box, so CI cannot export Windows at all today,
  and the obvious "fix" is to blank the field, which is the dangerous value.
- **Do:** fetch the template per `engine.lock.json` and assert, before every export, that the field
  is non-empty and the file's sha256 matches the lockfile (§7.4).

---

**G11 — Per-map packs under `data/maps/` are not mounted without a second call.**

- **Breaks:** every map fails to load, with no error more specific than a missing BSP.
- **Why:** `VirtualFileSystem.MountGameDir` enumerates packs **directly inside** the directory it is
  handed, and `MenuState.cs:120` calls it once on the data root. Packs one level down at
  `data/maps/*.pk3dir` are invisible to it.
- **Status:** **Accepted 2026-07-29** — `data/maps/`, mounted **before** the root call.
- **Do:** call `MountGameDir(<data>/maps)` **first**, then the existing `MountGameDir(<data>)`.
- **Why before, not after:** mount order is priority order, lowest first. Today the data root sorts
  `font-*` < `xonotic-20230620-maps.pk3` < `xonotic-20230620-nexcompat.pk3` < `xonotic-data.pk3dir` <
  `xonotic-maps.pk3dir` < `xonotic-music.pk3dir`, so **core data already outranks the compiled map
  packs**. Mounting `data/maps/` after the root call would invert that and let any map's
  `textures/foo` shadow core's. Mounting it first preserves the existing precedence exactly.
- **Rejected alternative:** putting all 35+ packs flat at the `data/` root needs no code change, but
  precedence then falls out of alphabetical order (`core.pk3dir` < `stormkeep.pk3dir`), which is the
  same inversion arrived at less visibly.
- **Downside accepted:** the mount list grows from 9 entries to about 41, and `VirtualFileSystem`
  walks `_mounts` linearly on a lookup miss. `ResolveImage` caches misses so image resolution is
  amortized, but non-image lookups pay it every time. Measure with `tools/perf-smoke.ps1` alongside
  the G7 conversion gate rather than assuming it is free.

---

**G12 — Deleting or retagging a `VortexMaps` release breaks pinned checkouts.** *(§8.2)*

- **Breaks:** every `VortexArena` commit whose lockfile points at that release, retroactively.
- **How it happens:** deleting a prerelease during cleanup, force-pushing a tag, or renaming or
  transferring the repo. That last one is not hypothetical here; this project already renamed
  `XonoticGodot` to `VortexArena` once.
- **What already helps:** the per-file sha256 makes a *substituted* artifact fail loudly rather than
  silently shipping different maps. It does not help with a *deleted* one.
- **Status:** **Accepted 2026-07-29** — two measures, no external mirror.
- **Do (1):** a GitHub tag-protection ruleset on `VortexMaps` blocking tag deletion and update, plus
  a never-delete policy for map releases. Platform-enforced and free; closes the accidental-cleanup
  and force-push paths.
- **Do (2):** `fetch-maps.py --rebuild`, which compiles from the pinned `maps-src` submodule when a
  fetch fails. This makes the **git sources the real backup** — distributed, and every clone is a
  copy — rather than depending on one host staying up. §8.3 already requires pinning the q3map2
  version and flags, so reproducibility is a stated requirement and this is mostly wiring. It is the
  same escape hatch as `--rebuild-bake` for the vmap era (§9.4).
- **Not doing:** a mirror URL on a second host. The `urls` array in the lockfile stays an array so
  one can be added without a schema change, but nothing is set up today. Revisit if we ever self-host
  a CDN.

---

### 6.2 The complete touch list

Re-counted against the tracked tree on 2026-07-29 (readiness pass), excluding `planning/legacy/` and
`planning/wave-a*` (frozen snapshots, deliberately left with their old paths). **126 tracked files**
reference `assets/data`, `download-assets`, or `assets/*`. A separate, partly overlapping **71 tracked
files** hardcode an absolute dev-box path, 42 of them under `tests/` (G13).

> **These counts go stale every time a branch lands.** The first draft said 80 in prose while its own
> table summed to 117, and the real figure was already 126 — the gap being almost entirely the
> `feature/map-editor` merge, which added test files carrying both patterns. That is G8's drift
> argument turned on this document. **Regenerate rather than trust:**
>
> ```bash
> git ls-files | xargs grep -l 'assets/data\|download-assets' \
>   | grep -v 'planning/legacy/\|planning/wave-a' | wc -l
> git ls-files | xargs grep -l 'Users.Bryan.Projects' | wc -l
> ```

| Group | Files | Notes |
|---|---:|---|
| **Parity registry & specs** | 49 | `planning/parity/registry/*.yaml`, `specs/*.md`, `_wave13-units.json`, two `*.workflow.js`. Read by `tools/parity-asset-check.py`, so this is machine-checked data, not prose |
| **Tests** | 43 | G13 (42 of them also carry the dead absolute path). Plus `VisualQaTests`, `ViewModelDepthHackTests` which carry `assets/data` strings |
| **Root** | 9 | `.gitignore` (2), `COPYING`, `Main.cs`, `README.md` (6), `XonoticGodot.sln`, `download-assets.sh`, `export_presets.cfg`, `run-release.sh`, `run-release.ps1` |
| **Planning prose** | 7 | ADR-0014 (9 hits), `HUD_PARITY_CONTRACT.md`, `playtest-bugs.md`, two `handoff-*.md`, this file |
| **Tools** | 6 | `tools/run-dedicated.sh` (5), `tools/run-client.sh` (5), `tools/parity-asset-check.py` (4), `tools/visual-qa.sh` (2), `tools/camera-ref/README.md` |
| **Runtime code** | 5 | `game/DataPaths.cs` (7 hits), `game/Shell.cs`, `game/menu/dialogs/DialogWinner.cs`, `game/client/EffectInfo.cs`, `game/client/particles/EffectInfoOverlay.cs` (+ `Main.cs`, counted under Root) |
| **Docs** | 4 | `docs/RUNNING.md` (7), `docs/REBRANDING.md` (7), `docs/RELEASING.md` (6) (+ `COPYING`, counted under Root) |
| **CI** | 3 | `.github/workflows/release.yml` (6), `ci.yml`, `ci/ci.sh` (8) |

Two groups were missing from the original change list and are the reason this pass was worth doing:

- **The 49 parity files are data, not documentation.** `tools/parity-asset-check.py` reads the
  registry YAMLs and resolves asset paths out of them. Leaving them pointing at `assets/data` breaks
  the parity gate rather than merely reading stale.
- **The 42 hardcoded test paths** are G13, now Stage −1.

And one item in the original list is **wrong and should be dropped**: `.run/*.run.xml` do not name the
executable. All four are `ShConfigurationType` configs invoking `$PROJECT_DIR$/run-release.sh`, so they
are path-agnostic and need no change. `Directory.Build.props` is real, but for the rename rather than
the path move: it carries the `XonoticGodotTfm` property, `XgBotPlayer`, and `XonoticGodot.csproj` in a
comment.

### 6.3 New ADRs this plan owes

The plan takes seven decisions (D1 to D7) that currently live only here. `planning/decisions/` is the
project's record of that kind of choice, so:

- **Write ADR-0016 — "Vortex Arena owns its content"** covering D1, D2, D3, D7: no upstream cloning,
  content committed at `data/`, map sources in a submodule, compiled maps as pinned release assets.
- **Write ADR-0017 — "Engine patches are fetched, not committed"** covering §7.3, since it
  establishes a mechanism (lockfile + prebuilt template + pre-export assertion) that outlives this
  migration.
- **Write ADR-0018 — "Vortex config is a layer, not a fork of the Xonotic config"** covering D8 and
  §11. It is the decision most likely to be quietly violated by a future contributor who just edits
  `balance-xonotic.cfg`, so it needs a record with the reasoning attached.
- **Amend ADR-0006** (asset pipeline): the offline-conversion half it specified is now partly built,
  as TGA to PNG.
- **Amend ADR-0014** (CI/packaging): it names `download-assets.sh` as the single source of asset
  truth and keys CI on `hashFiles('download-assets.sh')`. Both statements stop being true.
- **Amend or supersede ADR-0008** (solution structure): the `Client`/`Menu` project drift, §10.

---
**G13 — 42 test files hardcode an absolute dev-box path, and that path is already dead.**

- **Status:** **Promoted to a prerequisite, 2026-07-29 — this is now Stage −1, not a stage-3 item.**
- **Breaks:** the asset-dependent half of the suite, **on every machine including this one**, and
  every occurrence moves again when `assets/data` becomes `data/`.
- **Why it is worse than the first draft said:** the pattern is `private const string DataDir =
  @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";` — and the repo has since moved to
  `C:\Users\Bryan\Projects\Vortex\VortexArena`, with the upstream reference tree moving from
  `Projects\Xonotic\Base` to `Projects\Vortex\Base`. **Both prefixes now resolve to nothing.**
  Measured: 325 occurrences of the old repo prefix and 3 of the old `Base` prefix, spread over
  **42 tracked files under `tests/`** and **71 tracked files repo-wide** — the extra 29 being
  `.claude/workflows/{parity-diff,upstream-watch}.js`, `export_presets.cfg`, four `tools/*-ref/`
  docs, and the parity prose. The first draft's "34" predated the `feature/map-editor` merge, which
  added `VmapTextFormatTests`, `Perf/VmapVsBspLoadBench` and `Perf/EditorOcclusionBench` to the set.
- **This is G10 again in a different file type.** An absolute path to one workstation, load-bearing,
  invisible until someone else runs the thing. The `Perf/*Bench` files already do it correctly with
  `?? @"C:\..."` behind an env-var lookup; the rest do not.
- **Detect:** `git ls-files "tests/**" | xargs grep -l "Users.Bryan.Projects"` — expect zero. Scoped to
  `tests/` deliberately: `export_presets.cfg` holds a legitimate absolute path until stage 4, and the
  `tools/*-ref/` and `planning/` prose are stage-3 work.
- **Do:** one shared `TestPaths` helper resolving `VA_DATA_DIR`, else a repo-relative walk up from
  `AppContext.BaseDirectory`, else skip. Replace all 42. Keep the `Base/data` lookups separate and
  env-driven too, since that tree stays outside the repo by design — note `XG_BASE_DIR` **already
  exists** (one call site), so that is a rename to `VA_BASE_DIR`, not a new mechanism.
- **Why it moved to the front:** Stage 5 item 37 makes "clean build plus the full ~2,950-test suite
  green" the proof for the largest mechanical change in the repo's history. That gate is
  **unverifiable today** — the asset-dependent tests cannot pass anywhere, so "green" would mean
  "green with a silent skip of exactly the tests that read the tree we just moved." Fixing the paths
  first is what makes the restructure's own proof mean something. It is also the cheapest possible
  ordering: the paths are broken already, so the fix is pure gain and carries no restructure risk.

---

**G14 — Nine git worktrees are registered, and all nine are already stale.**

- **Breaks:** every worktree under `.claude/worktrees/`, in the same way and at the same time as the
  branches in G8, but silently — a worktree is not something `git branch` reminds you about.
- **Scale:** nine, not seven. The first draft missed `merge-branch-to-main-cb8172` and
  `zz-merge-trial`; the full set is `dedicated-slim`, `happy-moore-abb8f0`,
  `map-editor-backlog-continue-6865a0`, `merge-branch-to-main-cb8172`,
  `viberadiant-review-vortex-204d70`, `vortex-arena-anti-cheat-b51f29`,
  `vortex-startup-disclaimer-949f5e`, `xonotic-upstream-analysis-bc54bb`, `zz-merge-trial`.
- **All nine are `prunable` already.** They are registered at the pre-move path
  `C:/Users/Bryan/Projects/Xonotic/XonoticGodot/.claude/worktrees/…`, which no longer exists, so
  `git worktree list` reports every one as prunable. Seven of the nine also point at branches that
  are now fully merged, so there is nothing in them to migrate.
- **Why it is worse than a branch:** worktrees share one `.git` but have independent working trees
  and their own `assets/` state. The project's own convention is to build and run *inside* the
  worktree, so a surviving one needs its own content tree or `--data` pointer after the move.
- **Nothing recoverable is inside them — checked 2026-07-29 before pruning.** Every worktree's
  `assets/` holds exactly one file, the 246-byte `.gdignore`; none has a content tree, and a search for
  `physicsBryan*` across all nine (and across `Projects/Vortex/` and the user data dir) found nothing.
  Seven of the nine also point at now-merged branches. So the prune is safe in both senses: no unique
  commits and no unique files.
- **Do:** `git worktree prune` first — it is a metadata-only operation and, with every entry already
  prunable, it is risk-free. Then check `.claude/worktrees/` on disk: the directory survived the move
  and has accumulated loose `*.log` files at its top level alongside the worktree copies. Delete the
  copies whose branches are merged; only a worktree on one of G8's seven surviving branches needs
  `docs/BRANCH-MIGRATION.md`.

---

**G15 — A `vortex-*.cfg` exec'd after `LockDefaults()` becomes a user value, not a default.** *(§11)*

- **Breaks:** every cvar the Vortex layer sets gets written into the player's `config.cfg` and then
  outranks anything we ship later, so a balance or physics revision silently fails to reach anyone who
  has already launched the game once.
- **Why:** `MenuState.Boot` runs the config chain, then `_cvars.LockDefaults()` (`MenuState.cs:208`),
  then `LoadUserConfig()` (`:227`). `LockDefaults` is DP's `Cvar_LockDefaults` — it freezes each
  cvar's *current* value as its default. A cvar assigned before the lock becomes the shipped default
  and is persisted only if the player moves it; a cvar assigned after the lock is indistinguishable
  from something the player typed. The existing video overrides at `MenuState.cs:190-200` carry a
  comment block explaining exactly this, so the precedent is there to follow rather than rediscover.
- **Detect:** boot once, change nothing, quit, and read the generated `config.cfg`. Any
  `vortex-*.cfg` cvar appearing in it means the layer ran too late.
- **Do:** exec the whole Vortex layer inside the same `ConfigLoader.Load` call as the Xonotic entries,
  appended after them, so the ordering is structural rather than a separate call someone can later
  move. §11.2 wires it that way for this reason.
- **Related, same hazard one level down:** `ConfigLoader.cs:44-47` records that the shipped cfgs, not
  the C# registration tables, are the authority on which cvars are archiveable. A `seta` in the Vortex
  layer widens `config.cfg` beyond what upstream archives, so the layer uses plain `set` unless it is
  deliberately introducing a new archiveable cvar.

---

### Stage −1 — prerequisites (no restructure risk, do these first)

These three are independently correct, already-broken-today fixes. None depends on any decision in
this plan, and each one shrinks the migration's surface or makes its proof gate meaningful.

**Status: all three done 2026-07-29** (`b7e1700`). Notes below record what the execution taught.

-3. ~~**Replace the 42 hardcoded test paths with a shared `TestPaths` helper**~~ — **G13. Done.**
   `tests/XonoticGodot.Tests/TestPaths.cs` resolves `VA_DATA_DIR`, else walks up from
   `AppContext.BaseDirectory` to the repo root and probes `data/` **then** `assets/data/` — both, so
   the stage-3 path move needs no test edits. `VA_BASE_DIR` (renamed from `XG_BASE_DIR`, which nothing
   set externally) covers the parity tree, falling back to the `<parent>/Base/data` sibling convention.
   `CorePk3Dir` probes `core.pk3dir` before `xonotic-data.pk3dir` for the same reason.
   - **The gate needed rewording.** "`grep -l Users.Bryan.Projects` returns nothing" is wrong
     repo-wide: `export_presets.cfg` legitimately holds an absolute path until stage 4, and the
     `tools/*-ref/` and `planning/` docs are stage-3 prose. The real gate is **zero under `tests/`**,
     which now holds.
   - **A green suite was never proof and still isn't, on its own.** These tests self-skip via
     `if (!Directory.Exists(DataDir)) return;`, so a resolution bug reads as "no checkout here". Added
     `TestPathsTests`, which asserts the *conditional*: if a tree is discoverable from the repo root,
     `TestPaths` must have found it, naming the path it expected when it hasn't. Trivially true on an
     assetless checkout, so CI stays portable. Confirmed it has teeth — a bogus `VA_DATA_DIR` fails it.
   - **The dev box had no content tree at all.** `assets/data` was a symlink to the pre-move
     `Projects/Xonotic/Base/data`; re-pointed it as a junction to `Projects/Vortex/Base/data` so the
     gate measures something. Untracked and gitignored, and stage 1 replaces it.
   - Two declarations needed hand fixing: `ViewModelRenderBucketTests` (declaration split across two
     lines, so the `const` modifier survived the rewrite) and `EntityDefsTests` (a `const` built by
     concatenating `DataDir`, which stops being a compile-time constant). The latter reads
     `xonotic-maps.pk3dir/scripts/entities.ent` — **map-source content, so it starts self-skipping
     after stage 1** unless the submodule is checked out. Commented in place.
   - Result: **3,916 tests pass, 0 skipped, 0 failed.**
-2. ~~**Prune the stale git state**~~ — **G8**, **G14. Done.** `git worktree prune` removed all nine
   (each "gitdir file points to non-existent location"), and the eight merged branch refs are deleted
   with `-d`, leaving exactly G8's seven open branches plus `main`. **The 1.3 GB of orphaned worktree
   directories and 65 loose `*.log` files are still on disk** — unregistered from git now, so they are
   inert, but reclaiming the space is a separate call. Checked before pruning: none held a content tree,
   `dedicated-slim` sat on its branch tip exactly (no unpushed commits), and `zz-merge-trial`'s
   `2ab490d` is unreachable from any ref.
-1. ~~**Repoint `custom_template/release` at the engine build that exists**~~ — **G10**, §7.2. **Done.**
   Now `C:/Users/Bryan/Projects/Vortex/godot-4.6.3-inputfix/bin/…`; verified the 67 MB template
   resolves. Stage 4 still replaces this absolute path with the lockfile mechanism.

### Stage 0 — prepare, before anything is committed

0. **Transfer `bryankruman/VortexArena` → `VortexFPS/VortexArena`** and create `VortexFPS/VortexMaps`
   + `VortexFPS/VortexLauncher` (§5.0). First, so every URL below is written once. Status:
   - ~~transfer~~ — **done** (§5.0). Discovered via the push redirect, not deliberately.
   - ~~sweep the tracked files naming the old owner~~ — **done 2026-07-29.** Five files, not six:
     `README.md` (2 CI badges), `docs/RELEASING.md`, `tools/package.sh`,
     `tools/upstream-ledger-html.py` (`GITHUB_BLOB`), and `planning/upstream-watch/README.md` (the
     Pages host, `bryankruman.github.io` → `vortexfps.github.io`). `LEDGER.html` regenerated from the
     tool — 60 links repointed, 0 stale. `.github/workflows/pages.yml` needed nothing: it uses
     `steps.deployment.outputs.page_url`. Deliberately left: the old name in `docs/BRANCH-MIGRATION.md`
     and here (both *describe* the rename) and `planning/wave-a3/briefs/T33.json` (frozen).
   - **`git remote set-url origin git@github.com:VortexFPS/VortexArena.git`** — still to do; the local
     remote is stale and working only through GitHub's redirect.
   - **Create `VortexFPS/VortexMaps` and `VortexFPS/VortexLauncher`** — still to do. **Stage 1 is
     blocked on `VortexMaps`.**
   - **Check the org's default Actions permissions** — still to do, before relying on `release.yml`.
1. ~~**Land `feature/map-editor`**~~ — **G8. Done 2026-07-29** (46 commits, merged; the vmap code and
   the `989cd59` single-text-file `.vmap` format are on `main`). The seven surviving branches migrate
   afterwards via `docs/BRANCH-MIGRATION.md`.
1b. ~~**Prune the `.claude/worktrees/` copies**~~ — **G14. Moved to Stage −1 item −2.**
2. **Write `data/.gitattributes` containing `* -text`** — **G2**. Must exist in the same commit that
   first introduces content, not after.
3. **Write `data/.gdignore`** — **G4**, before the Godot editor next opens the project.
4. **Run the case-collision check against the source listings** — **G5**. Verified clean today; this
   is a guard for `VortexMaps` contributions later.
5. **Archive the pre-conversion tree outside git** — **G7**, until the Stage 2 perf gate (item 17)
   passes.

### Stage 1 — build the content trees on disk (still nothing committed)

> **The source tree is `C:\Users\Bryan\Projects\Vortex\Base\data\`, not `assets/data`.** The repo's
> `assets/data` is a **symlink** to `Projects/Xonotic/Base/data`, a path that no longer exists — so
> `assets/data` currently resolves to nothing and every "seed from the current tree" instruction below
> has to name `../Base/data/` explicitly. Two consequences worth stating:
>
> - **This box never used `download-assets.sh` output.** It symlinked straight to the upstream
>   reference checkout, which is why §4.1's inventory and its "four upstream clones" are really
>   describing `Base`'s clones. The measurements hold; the path in them does not.
> - **`Base` is also the parity baseline** (`VA_BASE_DIR`, `tools/parity-asset-check.py`,
>   `tools/parity-cvar-diff.py`). Staging `data/core.pk3dir/` from it means the migration source and
>   the parity reference are the same tree, which is fine — but it means **G7's "archive the
>   pre-conversion tree" cannot be satisfied by "Base still has it."** Base is a live upstream
>   checkout that gets updated; the archive has to be a separate frozen copy.

6. Create `VortexMaps`. Seed from `../Base/data/xonotic-maps.pk3dir` with its upstream `.git`
   discarded, so history starts clean rather than importing a 1.3 GB pack.
7. Extract both compiled map packs. Route the 97 MB of `.map`, `.ase`, `.obj` and q3map2 residue to
   `VortexMaps/sources/<map>/`; the runtime remainder becomes
   `VortexMaps/builds/q3map2/<map>.pk3dir/` in the type-rooted layout of §9.3.
7b. **Decide where `scripts/shaderlist.txt` goes — it is the one real content collision in the
   split.** Both trees ship one and they differ: `xonotic-data.pk3dir`'s lists 21 entries (gameplay
   effect shaders), `xonotic-maps.pk3dir`'s lists 71 (every `map_*` and `skies_*`). Today the maps
   copy wins, because `xonotic-maps.pk3dir` sorts above `xonotic-data.pk3dir` and mounts higher. The
   port reads the file **nowhere** — zero hits across tracked sources — so there is no runtime
   consequence either way, but **q3map2 does read it**, so the 71-entry copy has to land in
   `VortexMaps/` for the map build, and the 21-entry copy is what `data/core.pk3dir/` keeps. Choose it
   deliberately here; letting the copy step pick is how one of them silently disappears.
8. Stage `../Base/data/xonotic-data.pk3dir` → `data/core.pk3dir/`, dropping `qcsrc/`, `.tmp/`,
   `demos/`, `.tx/`, `cmake/`, every `*.import` (**G3**), and the six `progs`/`csprogs`/`menu`
   `.dat`/`.lno` files. Removes 81 MB the port cannot execute plus 5.9 MB of stale sidecars.
   **Keep every `*-xonotic.cfg` byte-identical to upstream** — **D8**, §11.
9. Stage music → `data/music.pk3dir/`, fonts → `data/font-*.pk3dir/` (keep the `.pk3dir` suffix — §5.1).
9b. **Verify the map-source tree really is only a build input, before deleting it from the mount set.**
   The plan asserts this; it is now measured, and the check belongs in the migration so it stays true.
   4,190 files exist only in `xonotic-maps.pk3dir` — but normalising the `dds/` prefix away and
   dropping extensions (`ResolveImage` probes `.tga`/`.png`/`.jpg`, and `DdsDecoder` handles the
   `dds/` mirror), only **13** image stems are unreachable from the rest of the runtime mount, all
   `textures/map_*/{*_alpha,*_bump}`, and **none of them is referenced by any of the 135 shipped
   `.shader` files**. Skyboxes were the worry and they are safe: `env/calm_sea/calm_sea_bk.jpg` and
   the other eight sets ship inside `xonotic-20230620-maps.pk3`, not in the source tree. Re-run this
   as a gate rather than trusting the number, since a future map addition can break it.

### Stage 2 — convert, verify, then commit

10. Write `tools/data/convert-tga.py`: walk a staged tree, re-encode `.tga` to `.png` at
    `-compression_level 9` **without** a `-pix_fmt` override so source bit depth is preserved, then
    delete the `.tga`. Idempotent, resumable, and it must refuse to touch `.dds` or `.jpg`.
11. **Verify losslessness by pixels, not by exit code** — **G6**. For every converted file, decode both forms
    and assert identical dimensions and identical pixel data. A conversion that silently drops an
    alpha channel or quantizes a 16-bit TGA produces a file that loads fine and renders wrong, which
    is the failure mode least likely to be noticed before release.
12. Run it over both staged trees: 2,233 files / 2,310 MB in `data/`, 3,031 files / 2,941 MB in
    `VortexMaps`.
13. **Now** make the first commits — **G1**, the point the ordering exists for. `VortexMaps`: commit, push, tag `v0`. `VortexArena`: remove
    `assets/*` from `.gitignore`, add `data/maps/`, commit the staged `data/` tree as one commit.
14. Add `.github/workflows/build-maps.yml` to `VortexMaps`: on tag, package each
    `builds/<backend>/<map>.pk3dir/` as one archive, publish the set as release assets, and emit the
    manifest that becomes `data/maps.lock.json`. Tag `maps-2026.07` as the first pinned set. In
    `VortexArena`: add `maps-src` as a submodule pinned to `v0` and commit `data/maps.lock.json`.
15. Fix the one hard-coded path that bypasses the VFS: `game/menu/dialogs/DialogWinner.cs:22-24`
    lists `res://assets/data/gfx/winner.png` before `…winner.tga`, so it already survives the
    conversion, but the `res://assets/data` prefix must become `res://data`.
16. Leave every other `.tga` string alone. `VirtualFileSystem.ResolveImage` strips a known image
    extension before probing and searches `.tga`, `.png`, `.jpg` in that order, so the 1,666
    `.shader` lines and 46 `.qc` lines that name `.tga` explicitly resolve to the PNG unchanged.
    Same for `LaserRenderer.cs:145`. The `.tga` literals in `Q3ShaderParserDirectiveTests` and
    `AutospriteBoltTests` are synthetic parser input and touch no real file.
17. **Gate (G7):** run `tools/perf-smoke.ps1` before and after. PNG decode costs more CPU than TGA decode
    while reading 61% fewer bytes. If map-load time regresses, the mitigation is the existing
    persistent asset cache (`cl_persist_asset_cache`), not reverting the format. Only after this
    passes may the Stage 0 item 5 archive be discarded.

### Stage 3 — rewrite the build and CI scripts

18. Delete `download-assets.sh`. Replace with two much smaller scripts: `tools/data/fetch-maps.py`
    (reads `data/maps.lock.json`, downloads missing or hash-mismatched archives, extracts to
    `data/maps/`) and a thin `git submodule update --init maps-src` wrapper for map authors.
    Core content, music and fonts now arrive with the clone and need no script at all.
19. `tools/package.sh`: `ASSETS_SRC` becomes `$ROOT/data`; drop the "download if missing" branch and
    the `--no-music` / `--no-maps` flags, which only made sense for a network fetch. Drop the
    `--exclude .git` logic for the pk3dir clones, which no longer exist.
20. `.github/workflows/release.yml`: delete the `assets` job and the tar fan-out to the four build
    jobs. Core content arrives with `actions/checkout`; maps arrive from `fetch-maps.py`. Re-key the
    `actions/cache` step from `hashFiles('download-assets.sh')` to `hashFiles('data/maps.lock.json')`,
    which is a strictly better key: it changes when the maps change, not when the script does. Set
    `submodules: false` on the game jobs so the 1.5 GB map source tree is not fetched to build a
    client.
21. `.github/workflows/ci.yml`: the real-data tests stop self-skipping for everything except maps,
    which now depend on the fetch step. Run `fetch-maps.py` in the test job behind the lockfile
    cache. That is a coverage gain and a runtime cost; measure the new job duration and consider
    `sparse-checkout` for jobs that need only code.
22. `ci/ci.sh`: drop the four `[ -d "$ROOT/assets/data" ]` guards; replace the headless host smoke's
    stormkeep guard with a `fetch-maps.py` call, so the smoke stops silently skipping.
23. `export_presets.cfg`: the exclude filter changes from `assets/*` to `data/*`. **Do not
    regenerate this file from the editor** — see item 26.
24. `game/DataPaths.cs`, `game/Shell.cs:35`: default path `res://assets/data` → `res://data`.
    `DataPaths.Resolve`'s executable-relative probe becomes `<exe-dir>/data` and
    `<exe-dir>/../Resources/data` on macOS. Update `docs/RELEASING.md` to match.
25. `MenuState.cs:120`: add `MountGameDir(<data>/maps)` **before** the existing root call, so
    per-map `.pk3dir` packages mount below core data and cannot shadow it — **G11**, §9.3.
26. **G9 — Godot rewrites `project.godot` and `export_presets.cfg` and drops comments.** Both files
    carry load-bearing, non-obvious settings that a regenerate would silently discard:
    `run/delta_smooth=false`, `common/physics_jitter_fix=0.0` and `common/physics_ticks_per_second=10`
    in `project.godot` (the wobble fix, §7.1), plus their 10-line explanatory comment block, and
    `custom_template/release` in `export_presets.cfg` (§7.2). Edit both by hand, and add a test that
    pins the three timing values so a future editor save that drops them fails the suite instead of
    shipping a regression nobody can feel until playtest.
27. Rename the env vars and MSBuild properties, alongside the Tier-0 `XONOTIC_USERDIR` →
    `VORTEX_USERDIR` rename. **There are twelve, not one** — earlier drafts named only `XG_DATA_DIR`:
    `XG_DATA_DIR` (26 hits), `XG_BENCH` (14), `XG_BOTPLAYER` (11), `XgBotPlayer` (8, MSBuild),
    `XG_BOTS` (9), `XG_MAPS` (6), `XG_TICKS` (5), `XG_PERF_ASSERT` (5), `XG_MAP` (5),
    `XgDebugUnoptimized` (3), `XG_PROBE_BSP` (2), `XG_BASE_DIR` (1). Sweep with
    `git ls-files | xargs grep -ohE '\bXG_[A-Z_]+|\bXg[A-Z][A-Za-z]+' | sort -u` and expect an empty
    result afterwards. Note `XG_DATA_DIR` and `XG_BASE_DIR` are already renamed by Stage −1, so this
    item covers the remaining ten.
28. `README.md`: replace the Assets section. Post-clone is now one command, `tools/data/fetch-maps.py`,
    and `git clone --filter=blob:none` becomes the documented default.
28a. ~~**The hardcoded test paths**~~ — **G13. Moved to Stage −1 item −3**, since Stage 5's proof gate
    depends on it. What remains here is only the second rewrite: the `TestPaths` helper's repo-relative
    fallback and its `VA_DATA_DIR` default change from `assets/data` to `data` along with everything
    else. One file, not 42.
28b. **The 49 parity registry and spec files** (§6.2) plus `tools/parity-asset-check.py`. This is
    machine-checked data feeding the parity gate, not prose, so it moves with the code. Re-run
    `python tools/parity-asset-check.py` as the proof.
28f. **Wire the Vortex config layer** — **D8**, **G15**, §11.2. Five call-site edits
    (`ConfigLoader.cs:25-31,69`, `MenuState.cs:162`, `MenuState.cs:184`, `MenuState.cs:262`) plus the
    seven `vortex-*.cfg` files in `data/core.pk3dir/`. Do **not** touch any `*-xonotic.cfg`, and do not
    rename the ~100 provenance comments that cite them. Gate: the two checks at the end of §11.5.
28c. **The remaining tools and scripts**: `tools/run-client.sh`, `tools/run-dedicated.sh`,
    `tools/visual-qa.sh`, `run-release.sh`, `run-release.ps1`, `tools/camera-ref/README.md`.
28d. **Docs**: `docs/RUNNING.md`, `docs/RELEASING.md`, `docs/REBRANDING.md`, `COPYING`, and the four
    prose files in `planning/` that name the old paths. `planning/legacy/` and `planning/wave-a*` stay
    frozen with their old paths, per the 2026-07-09 reorg convention.
28e. **Write and prove `tools/migrate-branch.sh`** — **G8**. Seven branches depend on it, so it ships
    with the restructure rather than after. Prove it by migrating one real branch (`fix/warpzone-view-smoothing`
    is the smallest) and getting a green build before the batch.

### Stage 4 — the engine template (§7)

29. Add `tools/engine-patches/engine.lock.json` pinning engine version, patch-set hash, and the
    URL + sha256 of the prebuilt template; add `tools/data/fetch-engine-template.py`; repoint
    `custom_template/release` off the absolute dev-box path.
30. Add the manual-dispatch `build-engine-template.yml` workflow, and the pre-export hash assertion
    that closes the silent-stock-fallback hole — **G10**.

### Stage 5 — the Tier-1 rename (REBRANDING.md Decision 3, Phase 4)

Land after Stage 3 so the mechanical sweep sits alone in history and stays bisectable.

31. `XonoticGodot.* → VortexArena.*` across the six `src/` projects and every `using`.
32. `XonoticGodot.sln` → `VortexArena.sln`; every `.csproj` filename, `RootNamespace`, and
    `AssemblyName`; `project/assembly_name` in `project.godot`.
33. **Trap:** the Roslyn generators in `VortexArena.SourceGen` emit the namespace as string
    literals. Rename those in lockstep and rebuild from clean, or the build breaks in generated
    code that no `using` statement points at.
34. Artifact filenames: `XonoticGodot.exe` → `VortexArena.exe`,
    `xonoticgodot-dedicated.x86_64` → `vortexarena-dedicated.x86_64`, in `export_presets.cfg`
    `export_path`s, `tools/package.sh`, and `release.yml`.
35. **Trap:** the launcher's `latest.json` loses update continuity across the artifact rename. The
    first `VortexArena`-named release is a deliberate cutover; document it in the launcher repo and
    in `docs/RELEASING.md` before tagging.
35a. `Directory.Build.props` (the `XonoticGodotTfm` property, `XgBotPlayer`, and `XonoticGodot.csproj`
    in a comment). **`.run/*.run.xml` are not in scope** — they invoke `$PROJECT_DIR$/run-release.sh`
    and name no executable, so the original §6.2 entry for them was wrong.
35b. **Regenerate the Godot UID cache after the `src/` moves.** 451 of the repo's 914 committed
    `*.cs.uid` sidecars live under `src/`, which item 31 relocates wholesale. The sidecars travel with
    their files, so nothing is lost — but `.godot/uid_cache.bin` maps uid → *path* and is stale the
    moment the directories move. Open the editor once (or delete the cache and let it rebuild) and
    check that no `.cs.uid` was orphaned: every `*.cs.uid` should still sit beside a `*.cs`.
36. ~~Tier-0 config filenames~~ — **split, 2026-07-29.**
    - **Dropped, not deferred:** renaming `xonotic-client.cfg` / `xonotic-server.cfg` /
      `binds-xonotic.cfg`. **D8** replaces it with the additive `vortex-*.cfg` layer in §11, which
      gets the same divergence without touching upstream files.
    - **Deferred out of this migration:** the remaining REBRANDING.md Tier-0 items — campaign id
      `xonoticbeta` → `vortexbeta`, `hostname` defaults, macOS bundle id. They are user-visible
      identity changes with their own continuity questions, they share nothing mechanically with the
      path move or the namespace sweep, and Stage 5 is already the largest mechanical change in the
      repo's history. Track them in `docs/REBRANDING.md`, not here.
37. **Proof:** clean `dotnet build` plus the full ~3,920-test suite green, then `ci/ci.sh`. This gate
    is only meaningful because **Stage −1 item −3** made the asset-dependent tests runnable; before
    that fix, "green" meant "green with those tests silently skipped."

### Stage 6 — extract the launcher

38. Create `VortexLauncher` from `feature/launcher-updater:launcher/`, preserving history with
    `git subtree split` or `git filter-repo`.
39. Move ADR-0015 to the new repo; leave a pointer stub at
    `planning/decisions/ADR-0015-launcher-updater.md`.
40. Repoint the launcher's `<hash12>` content key from `hashFiles('download-assets.sh')` to
    `git rev-parse HEAD:data` (§8.6 — the exact key, not an approximation).

### Stage 7 — record the decisions

41. Write **ADR-0016** (Vortex Arena owns its content), **ADR-0017** (engine patches are fetched) and
    **ADR-0018** (Vortex config is a layer, not a fork), and amend **ADR-0006**, **ADR-0014**,
    **ADR-0008**. Details in §6.3. Last, not first: the ADRs should record what was built, and stages 1
    through 6 will have changed some of it.
42. Add all three to `planning/decisions/README.md`, which currently indexes 13 ADRs. Note ADR-0015
    never existed on `main` — it lives only on `feature/launcher-updater` — so after the stage 6
    extraction the on-`main` sequence reads 0014 → (0015 pointer stub) → 0016.

---

## 7. Engine settings and engine patches in the new build system

Two separate things get conflated here, and only one of them is a patch.

### 7.1 The frame-delta fix is a setting, not a patch

The rubberband/wobble fix required no engine build. It is three values in `project.godot` plus a
runtime override:

| Where | Value |
|---|---|
| `project.godot:22` | `run/delta_smooth=false` |
| `project.godot:56` | `common/physics_ticks_per_second=10` |
| `project.godot:58` | `common/physics_jitter_fix=0.0` |
| `ClientSettings.cs:441` | `cl_engine_jitterfix` → `Engine.PhysicsJitterFix`, for a live mid-match A/B |

`physics_jitter_fix = 0` degenerates Godot's second delta clamp to `clamp(measured, measured)`, which
stops `MainTimerSync::advance_checked` rewriting the `_Process` delta onto a `physics_step/N` grid
with a ±50 ms ledger. Lines 41 through 50 of `project.godot` are a comment block explaining exactly
that. **Godot's config writer drops comments on save**, so any editor-driven regeneration of
`project.godot` silently deletes both the rationale and, potentially, the values. Item 26 covers it;
the pinning test is what actually protects it.

Nothing about the restructure threatens these values except carelessness while editing
`project.godot` for `config/name` and `assembly_name`. Flagged, not a design problem.

### 7.2 There *is* an engine patch, and the release pipeline cannot currently build it

`tools/engine-patches/godot-4.6.3-pr109639-mouse-input-backport.patch` (18 KB, committed) backports
[godot#109639](https://github.com/godotengine/godot/pull/109639), which batch-drains raw mouse input
instead of pumping `WM_INPUT` per message. Measured effect: +1.38 ms median frame cost while turning
on stock, +0.01 ms patched. It ships upstream in Godot 4.8; we are on 4.6.3.

Applying it requires a **custom-built Windows export template**, and here is the problem:

- `export_presets.cfg:49` → `custom_template/release="C:/Users/Bryan/Projects/Xonotic/godot-4.6.3-inputfix/bin/godot.windows.template_release.x86_64.mono.exe"`, an absolute path to one dev box —
  **and as of the repo move that path is dead on the dev box too.** The binary exists, at
  `C:/Users/Bryan/Projects/Vortex/godot-4.6.3-inputfix/bin/`. So this is not "CI cannot export
  Windows" any more, it is **nobody can**, which is why Stage −1 item −1 repoints it immediately
  rather than waiting for the Stage 4 lockfile.
- `release.yml:100` provisions Godot on `windows-latest` via `chickensoft-games/setup-godot@v2` with
  stock `include-templates: true`. That path does not exist there.
- Godot aborts an export whose custom template is missing (`ERR_FILE_NOT_FOUND`), producing no binary.
- `release.yml:110` runs the export under `|| true`, then `test -f dist/windows-client/XonoticGodot.exe`.

So the Windows job fails at the `test -f`, loudly, which is the intended design. The consequence is
that **since the patch landed (48c1071, 2026-07-27) the Windows release job cannot succeed on CI.**
No release has been attempted since; the last `release.yml` run was 2026-06-14 and it failed for an
unrelated reason. Linux and macOS are unaffected, since their three presets leave
`custom_template/release` empty.

The `tools/engine-patches/README.md` already documents the sharp edge that makes this dangerous to
"fix" casually: a *wrong* path fails loudly, but an *empty* path makes Godot fall back to the stock
template **silently**, shipping a release without the backport and saying nothing.

### 7.3 The fix, which is the same shape as maps

The plan already has a rule for this: **hand-edited content is committed, compiled output is
fetched.** A patch file is hand-edited and is already committed. A built template is compiled output,
so it gets fetched, exactly like maps and exactly like the launcher's asset pack.

```json
// tools/engine-patches/engine.lock.json
{ "schema": 1, "engine": "4.6.3-stable", "dropAtEngine": "4.8",
  "patches": [ { "file": "godot-4.6.3-pr109639-mouse-input-backport.patch", "sha256": "…" } ],
  "templates": { "windows-x86_64-release": {
      "sha256": "…", "size": …,
      "urls": [ "https://github.com/VortexFPS/VortexArena/releases/download/engine-4.6.3-pr109639/godot.windows.template_release.x86_64.mono.exe" ] } } }
```

- `tools/data/fetch-engine-template.py` downloads into a gitignored `.godot-templates/`, verifying
  sha256. Same retry, resume, atomic-rename and fail-closed rules as `fetch-maps.py` (§8.1).
- `custom_template/release` becomes a **repo-relative** path into `.godot-templates/`, which also
  retires the README's "anyone exporting elsewhere must re-point it at their own build" caveat.
  Verify that Godot's `FileAccess::exists` check accepts a `res://` path here before relying on it;
  if it insists on an absolute path, the fetch script writes the absolute path into a local
  `export_presets.cfg` override rather than the tracked file.
- A new `build-engine-template.yml`, manual dispatch only, clones the pinned engine tag, applies the
  pinned patch set, runs the scons line from the README, uploads the binary as a release asset, and
  prints the lockfile entry. Building Godot takes tens of minutes, so this runs on a patch change,
  not per release.
- `dropAtEngine` records when the patch becomes unnecessary. The README warns that a stale custom
  template from an older engine crashes the export at runtime; a version field the upgrade checklist
  reads is cheaper than remembering.

### 7.4 The assertion that closes the silent hole

Fetching is not enough on its own, because the dangerous state is an empty `custom_template/release`,
not a missing file. Add a pre-export step to `release.yml` and `ci/ci.sh`:

> For every preset that `engine.lock.json` declares a template for, assert `custom_template/release`
> is non-empty, that the file it names exists, and that its sha256 matches the lockfile. Fail the job
> otherwise.

That converts the one silent failure mode into a loud one, and it is the only check that can tell a
correct release build from a stock-template build after the fact. Worth writing before the first
`VortexArena`-named release, since that release is already the launcher cutover (item 35) and is a
bad time to also discover the mouse fix went missing.

---

## 8. Making the fetch path reliable

Pinning to release assets adds a network dependency to a previously self-contained checkout. These
are the failure modes it introduces, ordered by how badly each one bites.

### 8.1 Integrity

Every artifact is hash-verified, extracted atomically, and resumable.

- The lockfile carries sha256 per map. `fetch-maps.py` verifies before extracting and writes a
  `.stamp` beside the extracted directory, so a re-run costs a stat rather than a re-download.
- Extract to `data/maps/.staging/<map>/`, then rename into place. Never extract over a live
  directory: a interrupted in-place extract leaves a half-map that loads and renders wrong instead
  of failing, which is far worse than a clean error.
- A hash mismatch is a hard failure. There is no "download anyway" path.
- `download-assets.sh` already implements retry with exponential backoff, `curl -C -` resume across
  attempts, and a post-download archive integrity check. Port those helpers into `fetch-maps.py`
  before deleting the script; they are proven against real flaky transfers and are the main thing
  worth keeping from it.

### 8.2 Availability

The pin must not rot.

- Map release tags are immutable, enforced by a GitHub tag-protection ruleset on `VortexMaps`; never
  force-push a map tag, never delete a release that any `VortexArena` commit pins. (G12, accepted.)
- `fetch-maps.py --rebuild` compiles from the pinned `maps-src` submodule when a fetch fails, making
  the distributed git sources the durable backup rather than any single host. (G12, accepted.)
- The lockfile holds a `urls` array rather than a single `url`, so a mirror can be added later
  without a schema change. Nothing is mirrored today; that was considered and deferred.

### 8.3 Reproducibility

The same sources must produce the same maps.

Pin the q3map2 version and its exact flags in `build/q3map2.toolchain`, and record the toolchain
hash in the release manifest. Without that, a rebuild produces different lightmaps and a map change
cannot be diffed against its baseline. This matters most for the `vmap` track: comparing
`builds/vmap/` against `builds/q3map2/` is only meaningful if the q3map2 baseline is reproducible.

### 8.4 Drift detection

`tools/data/verify-data.py` walks `data/maps/`, checks each stamp against `maps.lock.json`, and
reports missing, extra, or mismatched maps. Wire it into `ci/ci.sh` as a gate.

Two more one-line checks belong in the same gate, because they catch the two mistakes most likely to
reach a push: no `.tga` survives anywhere under `data/` after Stage 2, and no file staged for commit
exceeds 100 MB.

### 8.5 Failure UX

Booting with an empty `data/maps/` must print the exact command that fixes it, not "map not found."
This is the most likely first-contact failure for a new contributor. `ci/ci.sh` and
`tools/package.sh` call the fetch themselves, so the only person who can hit it is someone running
the game directly after a fresh clone.

### 8.6 The bug this design ships if nobody catches it

ADR-0015 keys the launcher's shared asset store on `<hash12>` = `hashFiles('download-assets.sh')`.
Stage 3 deletes that file. If the replacement key covers only `data/maps.lock.json`, then a release
that changes `data/core.pk3dir/` without changing the maps produces the **same `<hash12>` as its
predecessor**, the launcher decides it already has that asset pack, and players silently keep stale
textures. Nothing errors.

Use the git tree object of `data/` instead:

```bash
git rev-parse HEAD:data     # 40-hex tree SHA; take the first 12
```

Verified against this repo: the tree SHA is byte-stable across commits that do not touch the
directory. It changes if and only if a committed file under `data/` changes, and
`data/maps.lock.json` lives under `data/`, so the map pin is covered by the same hash. `data/maps/`
is gitignored and does not perturb it. Exact rather than approximate, one git command, no file walk.

### 8.7 What the launcher sees: nothing changes

The launcher never builds from source. ADR-0015 scopes it to check, download, verify, install,
launch, consuming three artifact classes off `VortexArena` releases: fat zips, `-core` zips, and
`assets-<hash12>.zip`. Maps reach players inside those, because `release.yml` runs `fetch-maps.py`
before `package.sh` assembles them.

```
VortexMaps tag → per-map release assets
                      ↓  (release.yml, per data/maps.lock.json)
              VortexArena release.yml → package.sh → fat / core / assets zips
                      ↓
                  launcher → player
```

The launcher never contacts `VortexMaps`. Exactly three consumers run `fetch-maps.py`: developers
after a clone, CI, and the release packaging job. None of them is a player.

---

## 9. Transition to the `.vmap` editor

Checked against `feature/map-editor` (26 `Vmap*` source files, `planning/procedural-map-decoration.html`
§11.2, `planning/editor-lighting-q3map2-gaps.md`) and against `989cd59` on
`claude/tool-selection-usage-design-8159cd`, which replaced the `.vmap` container with a single text
file after this section was first written. The short version: the split this plan draws
and the split the editor design draws are the same split, arrived at independently. Four adjustments
make them line up exactly.

### 9.1 The design doc already drew this line

Design doc *goal* G6 (its own numbering, unrelated to the §6.1 gotcha labels): "a native map format
where the editable representation is the shipping
representation, with a regenerable bake cache for runtime efficiency (the `.map` vs `.bsp` split
collapsed into one package)."

The design keeps a strict split between **truth** (edited, versioned, small) and **bake** (derived,
regenerable, big, content-addressed by hash of the truth). That is §5.3.1's "hand-edited content is
committed, compiled output is fetched," in the editor's vocabulary.

> **Format change, 2026-07-29 (`989cd59`, `claude/tool-selection-usage-design-8159cd`).** The
> container is gone. **A `.vmap` is now one UTF-8 text file** in `maps/`, line-oriented and
> prefix-coded, filling the slot the `.bsp` fills. `VmapText` owns it; `VmapPackage` keeps the JSON
> section readers as read-only legacy (`CurrentFormatVersion` 3; 1 and 2 are the old forms) and the
> writers are deleted. The reader sniffs by **content, not extension**, so all three historical forms
> still load.
>
> This supersedes the directory-of-JSON-sections layout an earlier draft of this section described,
> and it retires the "extension on a directory name" question entirely. The commit's own reasoning is
> the same conclusion §9.3 reaches from the mount side: *"a `.pk3dir` IS the loose-files layout and a
> `.pk3` IS the zipped one, so a map ended up at `stormkeep.pk3dir/maps/stormkeep.vmap/geometry.json`:
> two containers, one purpose. The pk3 is the container now."*

Sizing, now measured rather than extrapolated (`BspToVmap.Import` over the shipped maps):

| map | faces | JSON sections | one text file |
|---|---:|---:|---:|
| fuse | 19,348 | 9.76 MB | **1.41 MB** |
| stormkeep | 48,762 | 22.13 MB | **2.66 MB** |
| afterslime | 30,326 | 17.36 MB | **3.01 MB** |
| catharsis | 1,085,397 | 476.07 MB | **~56 MB** |

The conclusion this section drew from a `.map`-size extrapolation survives, with better evidence:
`.vmap` truth is small enough to commit. Two details the numbers add. **The old JSON form was not
committable at all** — catharsis at 476 MB is past GitHub's single-file limit, so the format's own
goal of "deterministic bytes so `.vmap`s merge in git" was unreachable for the section that dominated
it. And **catharsis is still the outlier at ~56 MB**, under the limit but large enough that it, not
the median map, sets the ceiling; worth re-checking if face counts grow.

Packed binary measured *larger* than the text (2.76 vs 2.36 MB on stormkeep geometry), because most
values are short and binary spends four bytes on each regardless. Do not reach for binary here.

### 9.2 The q3map2 path is permanent, so §5.3.1 is not scaffolding

The design doc lists "replacing q3map2 for classic-map compatibility" as an explicit **non-goal**:
"decorated maps are a deliberate fork-forward feature; the Q3-stage material path stays intact for
parity." The 31 stock BSPs keep their pipeline indefinitely, and nobody can regenerate them without
the full q3map2 toolchain.

So the lockfile-and-release-assets mechanism is permanent infrastructure, not a bridge. Both
pipelines run side by side forever.

### 9.3 The unit is a `.pk3dir`, both eras

Earlier drafts of this plan wrote `data/maps/<mapname>/`. That is wrong, and reading the real pack
layout is what shows it. Xonotic content is **type-rooted, not per-map**: the pk3's top level is
`dds/ models/ maps/ sound/ scripts/ env/ gfx/ textures/ cubemaps/`, and a map is a set of files
scattered across those directories, unified only by a `<name>` / `map_<name>` naming convention.
Everything stormkeep owns:

```
maps/stormkeep.bsp  .mapinfo  .waypoints{,.cache,.hardwired}  .race.waypoints*  .jpg
maps/stormkeep/lm_0000.jpg              external lightmap pages
dds/textures/map_stormkeep/*.dds        brickfloor + _norm + _gloss …
scripts/map_stormkeep.shader
gfx/stormkeep_mini.jpg                  minimap
```

A flat `data/maps/stormkeep/` directory would break resolution outright, because the game asks the
VFS for `maps/stormkeep.bsp`, not `stormkeep/stormkeep.bsp`. The unit that *does* work is the one
Xonotic already uses for community maps: **one `.pk3dir` per map**, holding the type-rooted tree.

`VirtualFileSystem.MountGameDir` already implements this. It enumerates every `.pk3dir`/`.dpkdir`
directory and `.pk3`/`.pak`/`.dpk` file **directly inside** the directory it is given, mounts them in
case-insensitive name order (later name wins), then mounts the plain directory on top so loose files
take priority. That is `FS_AddGameDirectory` parity and it needs no loader change.

One wiring detail: `MountGameDir` scans only the directory it is handed, and today
`MenuState.cs:120` calls it once on the data root. Per-map packs living under `data/maps/` therefore
need a second `MountGameDir(<data>/maps)` call. One line, and it beats dumping 31 pack directories
into `data/` alongside `core.pk3dir/` and `music.pk3dir/`.

**Classic map** (q3map2 output, shipped form):

```
data/maps/stormkeep.pk3dir/
├── maps/stormkeep.bsp  .mapinfo  .waypoints*  .jpg
├── maps/stormkeep/lm_0000.jpg
├── dds/textures/map_stormkeep/*.dds
├── scripts/map_stormkeep.shader
└── gfx/stormkeep_mini.jpg
```

Its sources stay behind in `VortexMaps`: `stormkeep.map`, `stormkeep.map.options`, and the `.ase`
prefabs, none of which a player needs.

**vmap map** (editor output, shipped form):

```
data/maps/vortex1.pk3dir/
├── maps/vortex1.vmap                ← ONE text file, in the .bsp's slot
├── maps/vortex1.mapinfo  .waypoints
├── textures/map_vortex1/*.png
├── scripts/map_vortex1.shader
└── gfx/vortex1_mini.png
```

The two layouts differ in exactly one line. A `.vmap` does **not** replace the pk3dir; it is one file
in `maps/`, occupying the slot the `.bsp` occupies today, with the textures, shaders, minimap and
waypoints around it unchanged. `989cd59` made this true by construction: the old directory-or-zip
container was removed precisely because it duplicated the pk3.

There is no longer an extension-on-a-directory question to answer. `.pk3dir` remains the one place the
convention appears, exactly where Xonotic already uses it:

| | loose (editing) | packed (shipping) |
|---|---|---|
| map package | `stormkeep.pk3dir/` | `stormkeep.pk3` |

### 9.4 Three further adjustments

**1. Drop `builds/vmap/`. It is a category error.** There is no vmap "build" parallel to a q3map2
build, because the truth *is* the shipping form. What gets built is the bake. Corrected layout:

```
VortexMaps/
├── sources/<map>/
│   ├── <map>.map                classic Radiant source → q3map2
│   ├── <map>.map.options        compile flags
│   ├── <map>.vmap                vmap truth, ONE text file → git-diffable
│   └── textures/  models/       source art, pre-DDS
└── builds/
    ├── q3map2/<map>.pk3dir/     .bsp + DDS  (stock + classic maps, permanent)
    └── bake/<map>/              vmap bake cache, keyed by truth hash
```

Both source forms sit in one per-map directory, so a map converted from `.map` to `.vmap` keeps its
textures and history in place rather than moving between trees.

**2. Add `truthHash` to the lockfile schema now.** The bake is content-addressed by hash of the truth
sections, so its identity is derived, not tag-derived. Recording `truthHash` per map from day one
means the vmap era adds an artifact class rather than migrating a schema. Keep `urls` as an array and
the `schema` integer for the same reason: §14 wants bake distribution to eventually go "delta by tile
hash," which is finer granularity than one archive per map.

**3. `fetch-maps.py` needs `--rebuild-bake`.** `EditorLightBake` runs a full map bake in 9 to 14
seconds on this branch, down from 95. A missing or stale bake should degrade to "regenerate locally,"
not "fail." That is a materially better reliability posture than BSPs, which are not locally
reproducible, and it means bake distribution is an optimization rather than a hard dependency. §14
flags bake-cache distribution as an open risk ("tens of MB per map ... needs care on the wire"); a
working regenerate path is what keeps that risk from being release-blocking.

**4. The editor has no publish path, and this structure would swallow its output.** `VmapService`
writes to `user://vmaps/<name>.vmap`, which `UserPaths` redirects to `~/XonData/vmaps/`. That is
outside the repo. Under this plan `data/maps/` is gitignored, so a mapper who copies a finished
`.vmap` there loses it silently on the next clean checkout. The editor needs a `vmap_publish <name>`
verb that writes into `maps-src/sources/`, which is the tracked submodule. This is the one concrete
seam where the restructure and the editor branch actually collide, and it is cheap to add now.

### 9.5 End state

```
data/maps/<map>.pk3dir/                     ← the unit, both eras
  ├── maps/<map>.bsp                        fetched   q3map2 output, permanent
  ├── maps/<map>.vmap                       committed truth, one text file, 1.4-56 MB, diffable
  ├── maps/<map>/bake/                      fetched   derived, keyed by truthHash, regenerable
  └── dds|textures|scripts|gfx/…            mixed     shipped art travels with its map
```

New vmap maps ship their truth committed and fetch only the bake. Stock maps keep fetching
everything. One mount rule, one lockfile, one directory, and a map can hold a `.bsp` today and a
`.vmap` tomorrow without moving.

---

## 10. Open items

**ADR-0008 has drifted.** It specifies `XonoticGodot.Client` and `XonoticGodot.Menu` as separate
projects; both live in the root Godot host under `game/client/` and `game/menu/`. The rename in
**Stage 5** forces a choice: amend the ADR to record what was built, or split the projects out. The
recommendation is to amend. The split buys separation the codebase has not asked for, and Stage 5
is already the largest mechanical change in the repo's history.

**Upstream content updates.** After D1 there is no path for an upstream Xonotic asset fix to reach
Vortex Arena. That is the intent of a fork, but the parity tooling in `planning/parity/` and
`tools/parity-asset-check.py` compares against `Base/data/`, which stays an upstream reference
checkout outside the repo. Confirm those tools still resolve after `assets/data` moves to `data/` —
and note that `assets/data` is a **dead symlink** into the pre-move path today, so the fix is to point
them at `../Base/data` explicitly rather than at anything inside the repo (Stage 1 preamble).
**D8 narrows this problem for config specifically**: because no `*-xonotic.cfg` is ever edited, an
upstream config refresh is a file replacement rather than a merge (§11.5). The equivalent guarantee for
art and models does not exist and is not attempted.

**License and attribution.** The content stops being downloaded from upstream and starts being
redistributed by us. `COPYING` currently describes the code lineage only. Redistributing GPLv2+ and
CC-BY-SA content directly requires per-file attribution to travel with it. Check that each
`xonotic-data.pk3dir` license and credits file lands in `data/core.pk3dir/` rather than being pruned
as "not runtime content."

**Repo size after all stages.**

| Repo | Packed | Who clones it |
|---|---:|---|
| `VortexArena` | **~1.16 GB** (892 MB PNG, 152 MB core non-TGA, 106 MB ogg, 13 MB fonts, code) | everyone |
| `VortexMaps` | ~1.5 GB sources + builds | map authors only |
| map release assets | ~704 MB per tagged set | fetched, unmetered, cached |

The game repo carries no compiled map content, so it stops growing when maps change. Still large
enough that `git clone --filter=blob:none` belongs in `README.md` as the documented default.

**Release durability — settled, see G12.** A tag-protection ruleset on `VortexMaps` blocking tag
deletion and update, plus `fetch-maps.py --rebuild` compiling from the pinned `maps-src` submodule
when a fetch fails, so the distributed git sources are the real backup. No external mirror for now.

---

## 11. The Vortex config layer (D8)

Decided 2026-07-29, replacing the config half of the old item 36. **The Xonotic config files are
never edited. Vortex divergence is an additive `vortex-*.cfg` layer exec'd after them.**

### 11.1 The evidence for doing it this way

There is already one Vortex config divergence, and it has already been lost once.

`ConfigLoader.cs:16` documents the shipped default physics as `physicsBryan.cfg` — "stock Xonotic +
`sv_step_upspeed_max 1`" — and `planning/parity/cvar-diff-known.yaml:10-11` records how it was
applied: *"Shipped as physicsBryan.cfg; xonotic-server.cfg execs it instead of physicsX.cfg."* So the
mechanism was **edit upstream's `xonotic-server.cfg`, and drop a new file beside it.**

Checked against the tree today:

| Claim | Reality |
|---|---|
| `physicsBryan.cfg` exists | **No.** 33 `physics*.cfg` files in `xonotic-data.pk3dir`; that is not one of them |
| `xonotic-server.cfg` execs it | **No.** Line 675 reads `exec physicsX.cfg`, unmodified upstream |
| the tree carries local edits | **No.** `git status --porcelain -- '*.cfg'` in the clone is empty |

The divergence is gone. It did not survive `assets/data` being re-pointed at a clean upstream
checkout, and nothing failed loudly when it vanished — the game just quietly runs stock physics while
two files in the repo describe it as running ours. That is the failure mode a hand-edit against a
re-cloneable tree always has, and D2 alone does not fix it: committing the tree makes the edit
*durable*, but it also makes every upstream refresh a merge conflict against files we have no reason
to own.

The second reason is bigger and shows up in the touch list. Roughly a hundred tracked files name
`bal-wep-xonotic.cfg`, `balance-xonotic.cfg` and friends — but **almost all of them are provenance
comments**, of the form `// balance from bal-wep-xonotic.cfg (g_balance_arc_*)` on the weapon classes
in `src/VortexArena.Common/Gameplay/Weapons/`. Those citations are load-bearing documentation: they
are how a reader checks a ported value against its upstream source, and they are what
`tools/parity-cvar-diff.py` and the `planning/parity/` registry are written against. Renaming the
files would invalidate every one of them for no gain. Keeping the names pristine and layering on top
costs five call-site edits.

### 11.2 The wiring

**The override mechanism already exists.** `ConfigLoader.Load` takes `params string[] entryFiles` and
its own doc comment states the semantics: *"Later files override earlier ones (DP `set` semantics) —
pass a balance variant after the server entry to mod it."* D8 is a policy plus five edits, not new
machinery.

The load-bearing config sites are exactly these — everything else that names a `*-xonotic.cfg` is a
comment:

| Site | Today | Change |
|---|---|---|
| `ConfigLoader.cs:25` | `ServerEntry = "xonotic-server.cfg"` | unchanged; add `VortexCommonEntry = "vortex-common.cfg"` beside it |
| `ConfigLoader.cs:28` | `CommonEntry = "xonotic-common.cfg"` | unchanged |
| `ConfigLoader.cs:31` | `NotificationsEntry = "notifications.cfg"` | unchanged |
| `ConfigLoader.cs:69` | `LoadServerConfig` → `ServerEntry, NotificationsEntry` | append `VortexCommonEntry` |
| `MenuState.cs:162` | `"xonotic-client.cfg", "xonotic-server.cfg", "notifications.cfg"` | append `"vortex-common.cfg"` |
| `MenuState.cs:184`, `:262` | `ExecuteFile("binds-xonotic.cfg")` | follow each with `ExecuteFile("vortex-binds.cfg")` |

Three constraints govern the ordering, and all three are already documented in the code:

- **Before `LockDefaults()` — G15.** `MenuState.Boot` runs the chain, then `LockDefaults()` at
  `:208`, then `LoadUserConfig()` at `:227`. The layer must run inside the first step so its values
  become shipped defaults rather than indistinguishable-from-user-typed values.
- **Binds are a special case and need both call sites.** `binds-xonotic.cfg` is exec'd twice on
  purpose: `xonotic-client.cfg:603` pulls it in before any `bind` sink is registered, so those binds
  are dropped, and `MenuState.cs:184` re-execs it after `BindInput.RegisterBindCommands` to actually
  fill `BindTable`. `MenuState.cs:262` does the same for the settings scratch interpreter.
  `vortex-binds.cfg` has to follow it at **both** sites or the Vortex binds land in one code path and
  not the other.
- **Plain `set`, not `seta`.** Per `ConfigLoader.cs:44-47` the shipped cfgs are the authority on which
  cvars are archiveable. `seta` in the Vortex layer widens the player's `config.cfg` beyond upstream's
  set; use it only when deliberately introducing a new archiveable cvar.

A missing layer file is a no-op, not a crash: `ExecuteFile` returns false and increments
`FilesMissing` (pinned by `ConfigTests.cs:108`). So the layer can ship one file at a time.

### 11.3 The files

Committed to `data/core.pk3dir/`, beside the untouched Xonotic set:

```
data/core.pk3dir/
├── xonotic-{client,server,common}.cfg   ← upstream, byte-identical, never edited
├── balance-xonotic.cfg  bal-wep-xonotic.cfg  binds-xonotic.cfg  physicsX.cfg  …  ← same
│
├── vortex-common.cfg      the layer's only entry point; execs the five below
├── vortex-physics.cfg     replaces the lost physicsBryan.cfg (sv_step_upspeed_max 1, …)
├── vortex-balance.cfg     g_balance_* divergence
├── vortex-bal-wep.cfg     per-weapon divergence
├── vortex-server.cfg      server-side rules, gametype and mutator divergence
├── vortex-client.cfg      client/video/HUD divergence
└── vortex-binds.cfg       bind divergence — exec'd separately, see §11.2
```

One entry point matters: `vortex-common.cfg` execs the rest, so the C# side names a single file and
adding a sixth layer file later is a content change with no code change.

### 11.4 What moves into the layer

Two things exist today that the layer gives a home to.

**The physics preset — recovered in full, and it is three lines.** Traced 2026-07-29; no archaeology
needed, because only the config half was ever lost. The C# half is intact and tested.

`physicsBryan` was **`physicsX.cfg` (stock Xonotic) plus one port-added knob**, not a fork of another
preset. Both `ConfigLoader.cs:17` and `cvar-diff-known.yaml:9` say "stock Xonotic values +
`sv_step_upspeed_max 1`", and `physicsX.cfg` is the file headed `g_mod_physics Xonotic` /
"current Xonotic physics". `sv_step_upspeed_max` appears **nowhere in `Base/data/`** — it is a port
extension, default `-1` = disabled, and `planning/parity/registry/physics-player.yaml:405` records that
at stock defaults it is "a strict no-op … byte-identical to Base."

So the whole divergence is:

```
// data/core.pk3dir/vortex-physics.cfg
set sv_step_upspeed_max 1                       // the step-up launch cap (port extension, off by default)
set g_physics_bryan_step_upspeed_max 1          // same knob for the client-selectable "bryan" set
set g_physics_clientselect_options "xonotic nexuiz vecxis quake quake2 quake3 cpma bones xdf warsow bryan"
```

Upstream's list is the same string without `warsow bryan` (`physics.cfg:10`), so the third line is an
append rather than a rewrite — worth writing out in full anyway, since a cvar assignment cannot append.

**This is the strongest argument for D8 in the whole plan.** The old mechanism was: copy a 60-line
upstream preset, hand-edit it, and hand-edit `xonotic-server.cfg` to exec the copy — three touched
files, two of them upstream, and the result evaporated on the next re-clone. The same divergence as a
layer is three lines in a file nobody upstream owns, with `xonotic-server.cfg:675` left execing
`physicsX.cfg` exactly as shipped.

> **Not `physicsSamual.cfg`.** Worth recording because the two are easy to conflate: `physicsSamual.cfg`
> is `g_mod_physics Samual`, a materially different movement model — `sv_maxspeed` 420/235 against
> physicsX's 360/360, `sv_accelerate` 6 against 15, `sv_airaccelerate` 6 against 2, `sv_jumpvelocity`
> 300 against 260, `sv_stepheight` 34 against 31, and air-strafe and air-control zeroed out entirely.
> Shipping it would change how the game plays. The name overlaps for two innocent reasons: `physicsX.cfg`
> carries a Samual-derived `sv_stepheight 31` with the comment "Samual: 31 (just below 32, keeping things
> smooth without allowing 32qu steps)", so Samual is cited *inside* stock; and upstream defines no
> `g_physics_samual_*` family at all, so `bryan` was added to the selectable list alongside `warsow`
> rather than replacing a samual entry.

The preset framework itself never broke: `PhysicsPreset.Resolve` handles `g_physics_<set>_*` with a
fallback to the global cvar (`PhysicsPreset.cs:73-76`), and `PhysicsPresetTests.cs:238-252` already pins
`g_physics_bryan_step_upspeed_max = 1` end to end. Only the `.cfg` wiring that fed it went missing,
which is exactly the half D8 makes durable.

**The C#-side video overrides.** `MenuState.cs:190-200` sets `vid_fullscreen 2` and `vid_vsync 0` in
code, with a comment explaining that these "override Xonotic's SHIPPED cfg values" and must run before
`LockDefaults`. That is precisely what `vortex-client.cfg` is for, and config is the better home:
visible to the player, greppable, changeable without a rebuild, and covered by the same
`FilesExecuted`/`CvarsAssigned` accounting as everything else. Move them, keep the comment's reasoning
with them, and leave the C# path only for values that genuinely cannot be expressed as a cvar
assignment.

### 11.5 What this does to the parity tooling

It improves the signal, and one known-difference entry has to be rewritten.

`planning/parity/cvar-diff-known.yaml` currently encodes the hand-edit as expected chain divergence:
`physicsX.cfg` Base-only, `physicsBryan.cfg` port-only. Under D8 the Xonotic chain stops diverging at
all — `xonotic-server.cfg` execs `physicsX.cfg` exactly as upstream does — so those entries are
replaced by a single, stronger invariant:

> The `*-xonotic.cfg` chain matches `Base/data/` byte-for-byte. Every port-side cvar difference is
> attributable to a `vortex-*.cfg` file.

That is a cheaper check than diffing an edited chain and a better one: it turns "which of these
hundreds of cvar differences did we mean?" into "the diff is the layer, and the layer is six files
you can read." It also makes an upstream content refresh a file replacement rather than a merge, which
is the thing §10's "Upstream content updates" open item was worried about.

Add both as gates in `ci/ci.sh` alongside the §8.4 checks:

- no `*-xonotic.cfg` under `data/core.pk3dir/` differs from the pinned upstream reference;
- booting once with a clean profile writes no `vortex-*.cfg` cvar into `config.cfg` (**G15**).

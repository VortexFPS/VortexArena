# Asset repository — licensing, harvesting, in-game browsing, and sound

**Date:** 2026-08-12 · **Status:** research + proposal, nothing built

Four questions, answered in order:

1. Which licenses may Vortex Arena ship assets under, given it is GPLv3-**or-later**?
2. How do we find and collect those assets into a repository?
3. How does a mapper browse them from inside the game?
4. What are the options for sound effects, which we do not need to bundle?

Names used throughout: **Vortex Arena** is this project, a Godot/C# port of Xonotic. **q3map2** is the Quake 3
map compiler. **PBR** (physically-based rendering) is the albedo/normal/roughness texture convention modern
asset sites publish in; Quake 3 and Xonotic use an older diffuse + `_norm`/`_gloss`/`_glow` convention instead.
**SPDX** is the standard registry of short license identifiers (`CC0-1.0`, `GPL-3.0-or-later`). **REUSE** is the
FSF-Europe specification for recording per-file licensing in a machine-readable way.

---

## 1. The licence answer

### 1.1 What constrains us

Vortex Arena's own grant is the starting point, and it is stricter than it looks:

- **Source code:** GPL version 3 **or any later version** (`COPYING`, repo root).
- **Bundled content under `data/`:** also GPLv3-or-later, inherited from Team Xonotic's licensing document
  (`data/licenses/COPYING.xonotic`), which grants "GPL version 3 or any later version, at your choice".
- The DarkPlaces GPLv2 constraint does **not** apply here — this port does not include DarkPlaces
  (`COPYING`, "DarkPlaces engine" section). So unlike Xonotic itself, we are not pinned to GPLv2-compatibility.

That "or any later version" is the detail that decides several rows in the table below.

### 1.2 Tier A — safe to bundle in the default game

Everything here is a free licence **and** GPL-compatible per the FSF's own license list, so it can sit inside a
shipped `.pk3` with no argument.

| Licence | SPDX | FSF verdict | Notes for us |
|---|---|---|---|
| **CC0 1.0** (public domain dedication) | `CC0-1.0` | Free; "compatible with the GNU GPL"; FSF *recommends* it for non-software works | **Best case.** No attribution obligation, no source-file obligation. Prefer this above all. |
| **Public domain / expired copyright** | `LicenseRef-PublicDomain` | n/a | Needs provenance evidence, not just a claim. |
| **CC BY 4.0** | `CC-BY-4.0` | Free; "**compatible with all versions of the GNU GPL**" | Attribution required and must be carried into the game's credits. Fine. |
| **OGA-BY 4.0 / 3.0** (OpenGameArt's own) | `LicenseRef-OGA-BY-4.0` | Not FSF-listed | CC BY with the anti-DRM clause removed, i.e. strictly *more* permissive than CC BY. Compatible by inference, **not** by FSF ruling — tag it and review per asset. |
| **Expat / MIT** | `MIT` | Free, GPL-compatible | Common on models and UI art. |
| **BSD 2-/3-clause, Apache 2.0** | `BSD-2-Clause`, `BSD-3-Clause`, `Apache-2.0` | Free, GPL-compatible (Apache 2.0 with GPLv3 only) | Apache 2.0 is GPLv3-compatible but **not** GPLv2 — irrelevant to us since we are v3+. |
| **Artistic License 2.0** | `Artistic-2.0` | Free, GPL-compatible via §4(c)(ii) | Listed because Xonotic names it explicitly. |
| **WTFPL v2** | `WTFPL` | Free, GPL-compatible | FSF does not recommend it, but it is usable. |
| **GPLv2-or-later** | `GPL-2.0-or-later` | — | Upgradeable to v3, so it fits. **This is what OpenArena's whole asset set is**, which makes OpenArena the single best source of art already in the Quake 3 idiom. |
| **GPLv3-or-later** | `GPL-3.0-or-later` | — | Our own grant. What Xonotic's existing content is. |

**One obligation rides along with the GPL rows and not with the CC rows:** the GPL's "preferred form of the work
for making modifications" means a GPL-licensed texture must ship with its *source* — the `.xcf`, the `.blend`,
the high-poly bake. Both Xonotic and OpenArena enforce this explicitly. CC0 and CC BY carry no such duty. That
alone is a reason to bias the repository toward CC0/CC BY and treat GPL art as the exception.

### 1.3 Tier B — usable, but not in the default bundle

| Licence | SPDX | Why it is not Tier A |
|---|---|---|
| **CC BY-SA 4.0** | `CC-BY-SA-4.0` | Shippable **unmodified** with attribution only (see 1.5). It lands in Tier B because *our conversion pipeline modifies it*, and because a share-alike pack cannot be folded into `data/`'s uniform GPLv3+ grant. |
| **GPLv2-only** | `GPL-2.0-only` | FSF: "GPLv2 is, by itself, not compatible with GPLv3." Cannot be combined with our v3+ tree. |
| **CC BY 3.0 / CC BY-SA 3.0 and earlier** | `CC-BY-3.0`, `CC-BY-SA-3.0` | The FSF's GPL-compatibility ruling covers **4.0 only**. Pre-4.0 SA has no compatibility mechanism at all. |
| **Free Art License** | `LAL-1.3` | FSF: free and copyleft for art, but "**incompatible with the GNU GPL**". |
| **ODbL** | `ODbL-1.0` | FSF: "incompatible with the GNU GPL." |

Tier B assets can still be *offered* — as separately distributed packs a user opts into, relying on mere
aggregation (1.6) — but they must never be swept into the default install.

### 1.4 Tier C — never

| Licence class | Why |
|---|---|
| **CC BY-NC, any version** | FSF: "does not qualify as free, because there are restrictions on charging money for copies." GPLv3 §4 explicitly permits selling copies, so NC directly contradicts our own licence. |
| **CC BY-ND, any version** | FSF: "does not qualify as free, because there are restrictions on distributing modified versions." A texture nobody may modify is useless in a map editor. |
| **Freesound "Sampling+"** (legacy) | Restricts standalone commercial redistribution. Non-free. |
| **"Royalty-free" commercial bundles** (Sonniss GDC, Zapsplat, most marketplace packs) | Permit *use in your game*; forbid redistribution *as sounds*. An asset repository redistributes as sounds, so this is exactly the forbidden case. See §4.3. |
| **id Software Quake 3 assets** | The **engine** is GPLv2; `pak0.pk3` is **not**. Textures, models and maps from retail Q3 remain proprietary. Any texture set derived from them inherits that. This is the trap most Quake-lineage projects hit. |
| **Anything with unresolvable provenance** | "Found on a texture site, says free" is not a licence. Reject at harvest time. |

### 1.5 The "or later" problem — narrower than it first appears

**Corrected 2026-08-12.** An earlier draft of this document treated CC BY-SA 4.0 as broadly blocked for us. It is
not. The block is real but it applies only to a specific act, and our pipeline is what would commit that act.

Start from what CC BY-SA 4.0 actually requires. Section 3 splits into two conditions:

- **§3(a) Attribution** — applies "If You Share the Licensed Material (**including in modified form**)": keep the
  creator identification, the copyright notice, a link to the licence, and mark any changes.
- **§3(b) ShareAlike** — applies only "**if You Share Adapted Material You produce**", and then requires that
  "The Adapter's License You apply must be a Creative Commons license with the same License Elements, this
  version or later, or a BY-SA Compatible License."

"Adapted Material" is defined as material "translated, altered, arranged, transformed, or otherwise modified in a
manner requiring permission." So:

- **Shipping an unmodified CC BY-SA 4.0 texture in a `.pk3` next to GPLv3+ code triggers attribution and nothing
  else.** ShareAlike never fires, no relicensing is contemplated, and the GPL's own aggregate rule (1.6) covers
  the packaging. This is legal today, with no proxy and no negotiation.
- **The moment we adapt it, §3(b) fires.** Our own conversion stage (§2.3) downscales to 512–1024, inverts
  roughness into `_gloss`, and repacks — that is unambiguously adaptation.

Even then, GPL relicensing is *optional*. §3(b) offers three destinations for the Adapter's License, and the
obvious one is **CC BY-SA 4.0 itself**. Keeping our converted textures under CC BY-SA 4.0 satisfies the licence
completely and never touches the GPL question.

The GPL route is only needed if we want the adapted result to *be* GPL — and that is where the "or later" catch
bites, in the FSF's words:

> "Because Creative Commons lists only version 3 of the GNU GPL on its compatible licenses list, it means that
> you can not license your adapted CC BY-SA works under the terms of 'GNU GPL version 3, or (at your option) any
> later version.'"

**So the practical position for evillair's e-texture sets (e6, e7, e8, eX256, ecel, eq2, dsi — by a wide margin
the most used third-party texture library in Quake 3 mapping, and CC BY-SA 4.0 per the author's own GitHub
repository):** we may redistribute them unmodified today, and we may redistribute converted versions today
provided the converted pack stays CC BY-SA 4.0. What we may *not* do is quietly absorb them into `data/`'s
uniform "GPLv3 or later" grant.

Four ways to handle that, cheapest first:

- **Keep the adaptation under CC BY-SA 4.0, in its own pack.** Costs nothing legally; costs the uniform-licence
  story for `data/`, which REUSE metadata (§2.3) is designed to solve anyway.
- **Ship unmodified originals** and do the conversion on the user's machine at install time. Avoids producing
  Adapted Material for distribution at all. Costs install-time CPU and a second code path.
- **Ask the author.** One reachable person, already on GitHub. A CC BY 4.0 or CC0 re-grant erases the question.
- **Use the GPLv3 §14 proxy** — name Creative Commons as the proxy who decides which later GPL versions apply,
  which the FSF describes as the intended fix. Only worth it if we specifically want these assets under GPL,
  which nothing so far requires.

### 1.6 Mere aggregation — why bundling is not automatically "combining"

The GPL's copyleft reaches derivative works, not everything on the same disk. GPLv2 §2 and GPLv3 §5 both carve
out "mere aggregation" of a separate work on a distribution medium. A texture the engine loads at runtime, that
could be swapped for another with no code change, is a strong candidate for aggregation rather than derivation.

The GNU GPL FAQ is unusually direct about this, and it is worth quoting because it decides the packaging
question:

> "The GPL permits you to create and distribute an aggregate, even when the licenses of the other software are
> nonfree or GPL-incompatible. The only condition is that you cannot release the aggregate under a license that
> prohibits users from exercising rights that each program's individual license would grant them."

and, on when compatibility is needed at all:

> "If you just want to install two separate programs in the same system, it is not necessary that their licenses
> be compatible, because this does not combine them into a larger work."

The FAQ's test for "combined" turns on the mechanism and semantics of communication — shared address space,
linked modules, complex internal data structures passed back and forth. A `.tga` read off disk through the
virtual filesystem is at the far opposite end of that scale.

That reading is defensible — but note that **Xonotic and OpenArena both declined to rely on it**, requiring every
shipped byte to be GPL-compatible in its own right. Xonotic's legal page rejects ShareAlike and NonCommercial
outright for artwork and audio, and requires editable source files for what it does accept.

**Recommendation:** keep the conservative default (Tier A only in the base install), and use aggregation
deliberately and visibly for the Tier B opt-in packs — a separate download, a separate `.pk3`, a separate
licence file, never merged into `data/`. Aggregation is much easier to defend when the packaging visibly matches
the claim.

### 1.7 We are in the same position as Xonotic, not a worse one

**Corrected 2026-08-12.** An earlier draft claimed "Xonotic is pinned to GPLv2-compatibility by DarkPlaces in a
way we are not," implying we have less room on ShareAlike than they do. That is wrong, and the direction of the
asymmetry was backwards.

The facts:

- **Xonotic's content grant is GPLv3-or-later** — `data/licenses/COPYING.xonotic` says "All source files … in
  scope of this document are under the GPL version 3 or any later version, at your choice." That is *identical*
  to ours, because ours is inherited from it.
- **The GPLv2 constraint attaches to DarkPlaces, the engine, not to content.** The same document says the engine
  "is licensed under the GPL version 2 or any later version." Xonotic's wiki requirement that *code* be
  "GPLv2 or any later version" is about contributions to that engine.
- So on CC BY-SA art there is **no asymmetry at all**. Xonotic could ship unmodified CC BY-SA 4.0 assets under
  exactly the conditions in 1.5, and so can we. They chose not to, as policy.

Where we *do* have more room than Xonotic is code that is GPLv3-compatible but not GPLv2-compatible — Apache-2.0
being the everyday example — because we run on Godot (MIT) and ship no DarkPlaces. That has nothing to do with
ShareAlike.

Our position should therefore be:

- Same rejection of NC and ND — those are non-free, full stop.
- Same requirement of editable source for GPL-licensed art.
- **Different** on ShareAlike: not rejected, but confined to Tier B, kept under its own licence in its own pack
  rather than absorbed into `data/`.

### 1.8 Should we be GPLv2+ instead of GPLv3+?

**No — GPLv2 is not available to us, and the question that is actually live is whether to keep "or later".**

GPLv3 §14, in the copy at `GPL-3` in this repo:

> "If the Program specifies that a certain numbered version of the GNU General Public License 'or any later
> version' applies to it, you have the option of following the terms and conditions either of that numbered
> version **or of any later version**."

The option runs forward only. We derive from Xonotic's `qcsrc/` game source, which is GPLv3-or-later, so the
versions on offer are 3, or 4 when it exists — never 2. Xonotic's own GPLv2 figure applies to DarkPlaces, which
we do not ship (§1.7). There is no route to GPLv2+ and no benefit to looking for one: GPLv2-compatibility would
only matter if we wanted to combine with GPLv2-**only** code, and the engine that would have forced that is
gone.

The genuine option §14 does open is **GPLv3-only** — dropping "or later". That would make the CC BY-SA→GPLv3
compatibility route work directly, with no proxy. It is not worth it:

- §1.5 shows we do not need the GPL route for CC BY-SA at all, so it buys almost nothing.
- It complicates upstreaming to Xonotic, whose contribution terms require GPLv3-or-later. Our own commits we
  could dual-license at will, since we hold the copyright — but a third-party contribution made to a
  GPLv3-only Vortex Arena could not be sent upstream without going back to its author.
- It permanently forecloses GPLv4 and makes us incompatible with future GPLv4-only code.

**Recommendation: stay GPLv3-or-later for code and for `data/`, and let Tier B packs carry their own licences
beside it rather than bending our grant to fit them.**

---

## 2. Building the repository

### 2.1 The shape, and why it is mostly already built

The repo already runs a lockfile-pinned content pipeline for maps, and the asset repository should be the same
machine pointed at a second corpus:

- `data/maps.lock.json` — schema, release tag, provenance block, and per-pack `{size, sha256, urls}` (31 packs).
- [`tools/data/fetch-maps.py`](tools/data/fetch-maps.py) — hash-verified fetch with exponential backoff and HTTP
  Range resume; installs `.pk3` **as-is** rather than extracting, which removes staging, zip-path validation and
  stamp sidecars.
- [`VirtualFileSystem.Mount`](src/VortexArena.Formats/Vfs/VirtualFileSystem.cs:100) /
  [`MountGameDir`](src/VortexArena.Formats/Vfs/VirtualFileSystem.cs:129) — mounts `.pk3`/`.pk3dir` natively,
  with a `RescanResult` path for re-scanning without a restart.

So the deliverable is `data/assets.lock.json` + `tools/data/fetch-assets.py`, both near-copies, plus a separate
harvester that *produces* those packs. Reusing the proven fetcher is worth more than any new design here.

### 2.2 Sources, ranked by cost to harvest

| Source | Licence | Access | Volume | Verdict |
|---|---|---|---|---|
| **ambientCG** | CC0 | Official API v2, `/full_json`, no key, documented | ~2,000 PBR materials | **Start here.** Best licence, best API. |
| **Poly Haven** | CC0 | Official API, no key, full metadata + per-resolution download links | ~800 textures/HDRIs/models | **Start here.** They ask only that the source is credited visibly. |
| **OpenArena** | GPLv2-or-later | Git/release archives | Full Q3-idiom set | **High value, low competition.** Already the right art style, right shader conventions, and upgradeable to v3. Source-file duty applies. |
| **Kenney** | CC0 | Stable download URLs, no API | 40,000+ assets | Easy. More UI/prop than architectural, but the audio matters (§4). |
| **Xonotic / VortexMaps** | GPLv3+ | Local, already ours | Existing set | Becomes the seed catalog — index what we already ship. |
| **OpenGameArt** | Mixed: CC0, CC BY, CC BY-SA, GPL, OGA-BY | **No official API.** Drupal HTML; must scrape | Large | **Highest legal risk, highest care.** Per-item licence varies and multi-licensing is common. Rate-limit hard; the site operators warn that heavy scraping gets IPs blocked. |
| **evillair eTextures** | CC BY-SA 4.0 | GitHub repo, 8 zips | The canonical Q3 set | Tier B, or email the author (1.5). |

### 2.3 The harvester, in three stages

**Stage 1 — Fetch.** One adapter per source behind a common interface. API sources are trivial. For
OpenGameArt: obey `robots.txt`, serialise requests with a courtesy delay, cache aggressively so a re-run costs
nothing, and identify the crawler with a contact URL in the User-Agent. The goal is a slow, polite, resumable
crawl, not throughput.

**Stage 2 — Adjudicate the licence.** This is the stage that earns its keep, and it should be able to *refuse*:

- Resolve the stated licence to an **SPDX identifier**. No identifier, no asset.
- Assign **Tier A / B / C** by the table in §1. Tier C is dropped, with the reason logged.
- Snapshot the evidence: the licence text or page, the author name, the canonical URL, and the fetch date.
  Sites change; the snapshot is what we can show later.
- **Multi-licensed assets take the best compatible option** (OpenGameArt items are frequently CC0 *and* CC BY
  *and* GPL) and record that a choice was made.
- Anything ambiguous goes to a **quarantine list for human review**, never to a default.

**Stage 3 — Convert and package.** The gap between what these sites publish and what this engine loads:

- **PBR → Q3 channel conventions.** Sources publish albedo/normal/roughness/AO/displacement. The port's shader
  compiler expects diffuse plus `_norm`, `_gloss`, `_glow`, `_reflect` suffixes
  (`planning/specs/asset-pipeline.md`). So: albedo → base, normal → `_norm` (**check green-channel handedness —
  OpenGL vs DirectX normal maps are inverted and this is the single easiest thing to get silently wrong**),
  roughness → invert → `_gloss`. Drop AO/displacement or fold AO into the diffuse.
- **Resolution.** Sources ship 2K–8K. Downscale to 512–1024 to match the existing set; keep one higher tier as
  an optional pack.
- **Generate a `.shader` per set** with `qer_editorimage` and sane `surfaceparm` defaults, so the material shows
  up in the editor browser exactly like the stock sets do.
- **Emit REUSE-compliant metadata**: a `LICENSES/` directory holding each licence text named by SPDX
  identifier, and a `REUSE.toml` covering the asset tree (per-file comment headers are impossible in a `.tga`).
- **Pack as `.pk3`**, publish as GitHub Release assets, pin `{size, sha256, urls}` in `assets.lock.json` — the
  same shape and the same free unmetered bandwidth the map packs already use.

### 2.4 Attribution has to be automatic or it will not happen

CC BY and GPL both require credit. With thousands of assets this cannot be hand-maintained. The repo already has
the pattern: `tools/gen-credits.py` generates `data/licenses/CREDITS` from the credits screen. Extend it so
the catalog's attribution data generates a credits section, and — more importantly — so that **a map exported
from the editor carries an attribution file listing only the assets it actually used** (§3, V5). A mapper should
not have to know they incurred an obligation.

---

## 3. The in-game viewer

### 3.1 Most of this exists

Backlog item T6 ("texture browser thumbnails") is **done**, and the pieces are the right ones to build on:

- [`EditorDialogPanel`](game/hud/EditorDialogPanel.cs:24) with `DialogKind.Gallery` — a thumbnail grid with a
  2-D cursor, type-to-search, grouping, and a detail pane.
- [`EditorThumbnailCache`](game/hud/EditorThumbnailCache.cs:23) — bounded LRU keyed on last *drawn* frame,
  off-thread decode with only the GPU upload on the main thread, in-flight cap so a flick-scroll cannot queue
  2,000 loads, and explicit `Dispose` on eviction.
- [`NetGame.OpenShaderBrowser`](game/net/NetGame.cs:10613) — populates rows from `AssetSystem.ShaderNames()`,
  filters tool materials, groups by first path segment.
- `AssetSystem.LoadThumbnailImage(material, size)` — the decode side.

So this is **not** a new browser. It is a catalog behind the existing browser, plus the ability to browse things
that are not installed yet.

### 3.2 Work items

Prefix **A** (asset browser), continuing the map-editor backlog's convention.

| ID | Item | Depends on |
|---|---|---|
| **A1** | **A catalog model.** Today a row is a shader name and a path segment. Introduce an asset record — id, set, tags, licence SPDX id, author, source URL, install state — loaded from a `catalog.json` shipped in each pack, and fall back to the current name-only behaviour when a pack has none. | — |
| **A2** | **Facets and filtering.** Filter by set, tag, and **licence tier**. A mapper making a map for the default game needs to be able to say "Tier A only" and have the browser stop showing anything that would burden them. Reuses the existing filter input. | A1 |
| **A3** | **Detail pane shows the obligation.** Author, licence, source link, and one plain sentence — "credit required", "no obligation", "share-alike: not in the default bundle". The information exists; the browser just does not show it. | A1 |
| **A4** | **Remote catalog + install on demand.** Browse packs that are not installed, greyed with a download affordance; fetch via the `fetch-assets.py` path; mount with `VirtualFileSystem.Mount` and rescan — **no restart**. The VFS already supports the rescan; this is mostly UI and a progress model. | A1, §2 |
| **A5** | **Per-map attribution export.** The editor knows every material a map references. On save/export, write an attribution file listing exactly those, with author and licence. This is the item that makes the whole licensing effort actually work in practice. | A1 |
| **A6** | **Thumbnail source for remote assets.** `EditorThumbnailCache` decodes from the VFS; a not-yet-installed asset has no VFS entry. Ship a small thumbnail atlas inside the catalog so remote entries have pictures before download. | A1, A4 |

**Order: A1 → A3 → A2 → A5 → A4 → A6.** A1 is the gate. A3 and A2 are cheap once the data is there and make
the browser immediately more useful with only local packs. A5 before A4 deliberately — get the obligation
plumbing correct while the corpus is small and known, rather than after thousands of remote assets can enter.

### 3.3 Things that will bite

- **`EditorThumbnailCache.Capacity` is 512 entries at 96 px.** A catalog an order of magnitude larger than the
  ~2,000 stock shaders will scroll fine (that is the point of the LRU) but the *row list* is built eagerly in
  `OpenShaderBrowser` and sorted every open. Virtualise the row build before the catalog gets large.
- **Licence filtering must default to safe.** If the default view mixes tiers, mappers will pick Tier B assets
  without noticing and the maps will not be shippable in the default game.
- **Download is untrusted input.** The map fetcher deliberately installs `.pk3` as-is precisely so nothing is
  ever unpacked; keep that property. Verify sha256 before mounting, not after.

---

## 4. Sound

The brief is different here: not necessarily bundled, but developers must be able to find sounds and know the
licence is compatible. That splits cleanly into two lanes.

### 4.1 Lane A — a redistributable CC0/CC BY sound pack (can live in the repo)

Same machinery as §2, smaller corpus:

- **Freesound, CC0 subset only.** The API supports exact licence filtering
  (`filter=license:"Creative Commons 0"`). CC0 sounds carry no attribution duty and may be redistributed freely.
- **Kenney audio** — CC0, clean and game-ready, no sign-up.
- **OpenGameArt audio** — CC0 and CC BY items, same adjudication as textures.

Volume will be lower and quality more variable than a commercial library, but every byte is redistributable and
Tier A.

### 4.2 Lane B — an in-editor Freesound search client (discovery only, nothing mirrored)

Freesound is the largest useful corpus (700,000+ sounds), but its **API terms**, separate from the sounds'
licences, constrain what we may build:

- **"You can use the Freesound API for free only for non-commercial purposes"**; commercial terms are negotiated
  case-by-case with UPF (Universitat Pompeu Fabra, who run it).
- You may not "distribute, publish, or allow access or linking to the Freesound API or Content from any location
  or source other than your Application" — **so a Vortex-hosted mirror of Freesound is off the table.**
- Intermediate copies are permitted only as needed and "should be deleted when they are no longer required."

Two consequences worth stating plainly:

1. **A mirror is not allowed; a client is.** The compliant design is an editor panel that searches Freesound and
   downloads to the *user's own machine*. Nothing transits or rests on our infrastructure.
2. **The API key cannot ship.** Vortex Arena is GPLv3 — the source is published, so an embedded key is a
   published key. And GPLv3 §4 explicitly permits anyone to sell copies, which sits awkwardly against a
   "non-commercial use only" API grant. **Have the user supply their own Freesound API key.** That single
   decision resolves both the key-distribution problem and the non-commercial question, because the API use is
   then the user's, under their own account and their own terms.

Sounds pulled this way are still subject to their own licence: CC0 (free), CC BY 4.0 (credit, Tier A), CC BY-NC
(Tier C — must be filtered out of the UI, not merely warned about), and legacy Sampling+ (Tier C).

### 4.3 What to avoid, and why it is tempting

- **Sonniss #GameAudioGDC bundle** — 200+ GB of professional SFX, free, royalty-free, no attribution, and
  explicitly usable in commercial games. It is the obvious answer and it is the wrong one for a *repository*:
  the licence says the licensee "may not sell any of the sound effects as they come" and forbids sublicensing.
  Incorporating a sound into a game is fine; redistributing it as a sound is exactly what an asset repository
  does. It also prohibits use for AI training. **Usable by an individual sound designer producing Vortex Arena's
  own sounds; not usable as repository content.**
- **BBC Sound Effects** — the RemArc licence is personal/educational/research only. Not free, not commercial.
- **Zapsplat, soundbible-style aggregators** — proprietary attribution licences with redistribution bans, and
  frequently unclear provenance on individual files.
- **Aggregator sites that mix public-domain and royalty-free material** (gamesounds.xyz and similar) — the mix
  is the problem; per-file provenance is usually unrecoverable.

### 4.4 Recommendation

Build Lane A as a small `vortex-sounds` pack on the same lockfile machinery, and build Lane B as an opt-in
editor panel gated on a user-supplied API key. Do not attempt to unify them: one is content we redistribute, the
other is a search tool over content we never touch. Keeping them visibly separate is what keeps both defensible.

---

## 5. Sources

Licensing:
- [FSF, Various Licenses and Comments about Them](https://www.gnu.org/licenses/license-list.en.html) — the entries for CC0, CC BY 4.0, CC BY-SA 4.0, CC BY-NC, CC BY-ND, Free Art License, ODbL, WTFPL, GPLv2 were read directly from this page
- [FSF: CC BY-SA 4.0 declared one-way compatible with GPLv3](https://www.fsf.org/blogs/licensing/creative-commons-by-sa-4-0-declared-one-way-compatible-with-gnu-gpl-version-3)
- [Creative Commons: CC BY-SA 4.0 now one-way compatible with GPLv3](https://creativecommons.org/2015/10/08/cc-by-sa-4-0-now-one-way-compatible-with-gplv3/)
- [Creative Commons: Compatible Licenses](https://creativecommons.org/share-your-work/licensing-considerations/compatible-licenses/)
- [CC BY-SA 4.0 legal code](https://creativecommons.org/licenses/by-sa/4.0/legalcode.en) — §1 "Adapted Material", §3(a) Attribution, §3(b) ShareAlike were read directly
- [GNU GPL FAQ](https://www.gnu.org/licenses/gpl-faq.html) — `#MereAggregation`, `#WhatIsCompatible`, `#v2v3Compatibility`, `#GPLOtherThanSoftware`
- GPLv3 §14 "Revised Versions of this License" — read from [`GPL-3`](GPL-3) in this repo
- [Xonotic wiki: Legal](https://github.com/xonotic/xonotic/wiki/Legal)
- [OpenArena wiki: GPL](https://openarena.fandom.com/wiki/GPL) and [Appendix F: GPL Compliance](https://openarena.fandom.com/wiki/Mapping_manual/Appendix_F:_GPL_Compliance)
- [REUSE Specification 3.3](https://reuse.software/spec-3.3/) · [reuse-tool](https://github.com/fsfe/reuse-tool)

Asset sources:
- [ambientCG API v2 docs](https://docs.ambientcg.com/api/v2/) · [`/full_json`](https://docs.ambientcg.com/api/v2/full_json/)
- [Poly Haven API](https://polyhaven.com/our-api) · [Poly Haven license](https://polyhaven.com/license)
- [OpenGameArt FAQ](https://opengameart.org/content/faq) · [OGA API forum thread](https://opengameart.org/forumtopic/opengameart-api)
- [evillair/eTextures](https://github.com/evillair/eTextures) — CC BY-SA 4.0
- [Quake3World: an open pak0.pk3?](https://quake3world.com/forum/viewtopic.php?t=14745) — retail Q3 assets are not GPL

Sound:
- [Freesound API terms of use](https://freesound.org/help/tos_api/) · [developer help](https://freesound.org/help/developers/)
- [An Introduction to Freesound (Creative Commons)](https://opensource.creativecommons.org/blog/entries/freesound-intro/)
- [Sonniss #GameAudioGDC bundle licence](https://sonniss.com/gdc-bundle-license/)

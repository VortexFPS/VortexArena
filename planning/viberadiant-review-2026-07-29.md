# VibeRadiant vs NetRadiant — what it added, what actually works, what we should take

**Date:** 2026-07-29 · **Subject:** [themuffinator/VibeRadiant](https://github.com/themuffinator/VibeRadiant) @ `aedcd472`
· **Purpose:** decide what belongs in the Vortex in-game map editor

Everything below is read out of the fork's source and git history, not its README. Where a claim in
`CHANGES_FROM_NRC.md` and the code disagree, the code is quoted.

---

## 1. The baseline

The README says the fork is based on "NetRadiant-Custom". The merge-base against
`Garux/netradiant-custom` is `cc036b6b` (2025-12-11); everything after is the fork.

| | |
|---|---|
| Fork point | `cc036b6b`, 2025-12-11 |
| Fork commits | 28, of which 25 are substantive; several are single 5k–15k-line commits |
| Diff vs base | 353 files, +47,686 / −13,277. Code only (`radiant/ libs/ plugins/ include/ tools/`): 182 files, +36,599 / −3,725 |
| Upstream drift | 18 NRC commits missing, including a terrain-generator plugin, a 10× faster BSP→map decompiler, a GCC 16.1 build fix, and `12972515 fix group entities with origin broken` |
| Last commit | 2026-04-24, which deleted all four GitHub Actions workflows |
| Tests | none of its own (only vendored assimp/draco) |
| Releases and tags | zero of each. `VERSION` reads `1.6.0`; no matching tag exists |

The new code lives in 23 new files, all present in the `Makefile`, so none of it is orphaned at link
time. The largest are `genai.cpp` (3,756), `previewlighting.cpp` (2,954), `entitybrowser.cpp` (1,732),
`soundbrowser.cpp` (1,143), `update.cpp` (1,063) and `linkedgroups.cpp` (1,004).

---

## 2. Feature inventory

### 2.1 Finished, and worth reading

**Camera preview lighting** — `radiant/previewlighting.cpp`. A real CPU lightmapper running against the
live document. Per-face luxel grid at 24 world units, resolution clamped 4–64 px per axis; a BVH over
scene triangles for shadow rays; `q3map_surfacelight`, `q3map_skyLight` and `q3map_sunExt` parsed out of
shader text; sky sampling jittered by radical-inverse Van der Corput so `_deviance`/`_samples` behave;
content hashing that includes patch tessellation, so a curve edit invalidates correctly. It drains a
dirty deque under a **6 ms per frame** budget (`kWorkBudgetMs`) and refreshes same-size textures with
`glTexSubImage2D` rather than reallocating. Drawn as a modulate overlay (`GL_ZERO, GL_SRC_COLOR`) over a
fullbright base pass, so it never touches the main render path.

Two limits: luxels clamp to 8-bit LDR at write time (`previewlighting.cpp:1760`), and the alternate
`Fast Interaction` model recomputes `compute_lighting` per winding vertex inside the draw loop with no
cache at all (`render_overlay_fast_interaction`), so it does not scale with brush count.

**Texture and entity find/replace** — `radiant/select.h`, `radiant/findtexturedialog.cpp`. The best-built
thing in the fork, and the changelog barely mentions it. Match modes exact / contains / starts / ends /
wildcard / regex with `$1..$9` backreferences; separate replace modes (whole value vs matched span);
include and exclude path filters; surface- and content-flag require/exclude masks; scope of all,
selected, or selected faces. The same machinery covers entity keys and values. Registered as
`FindReplace` on Ctrl+H.

**Linked duplicates** — `radiant/linkedgroups.cpp`. TrenchBroom-compatible on disk: `_tb_linked_group_id`
and `_tb_transformation`. Copies are related by an affine delta (`targetTransform × sourceInverse`), so a
mirrored or rotated copy stays correct. Propagation has one choke point: `onCommandStart` opens a dirty
set, `onCommandFinish` flushes it once, and a `g_updating` guard stops the propagation from re-entering
itself. Create, select-linked and separate all work.

The cost is in `update_linked_groups_from_source`, which clones every child of the source and replaces
the entire contents of each linked group on any change. That is O(copies × children) per edit, and it
destroys and recreates the nodes in the copies each time.

**Selection undo/redo** — `radiant/selection.cpp:7004`. A `SelectionUndoTracker` attached to the global
undo system; snapshot taken on `begin()`, committed on the first selection change inside the command.
Restoration walks the live scene graph and uses the stored `scene::Node*` set only as a membership test,
so nothing dead is dereferenced. Two gaps: node identity is a raw pointer, so a delete-then-undo that
reuses an address selects the wrong node; and component restore calls
`setSelectedComponents(true, mode)` for the whole node, so vertex selection comes back as *all* vertices
of the nodes that had any, not the ones that were selected.

**Issue browser** — `radiant/issuebrowser.cpp`. Complete for its scope, which is three checks: missing
`classname`, duplicate `targetname`, `target` with no matching `targetname`. Select-affected and
undoable batch fixes for each. It ignores `target2`, `target3`, `target4` and `killtarget`, which this
same fork's `docs/Additional_map_editor_features.htm` advertises as supported targeting keys. The
missing-classname auto-fix assigns `info_null`, which is a decision the mapper should probably make.

**Z-bar** — `radiant/zwindow.cpp`, wired into every layout in `mainframe.cpp`. Done.

Smaller and correct: **Drop Entities to Floor** (`EntityDropToFloor`, registered and undoable),
**idTech2 flag-aware filters**, **`model2` secondary model rendering**, and a **Windows crash reporter**
writing timestamped logs plus minidumps, honestly labelled as Windows-only.

### 2.2 Built, but with nothing behind it

**Quake 3 multi-stage shader rendering.** The first bullet of the README. `plugins/shaders/shaders.cpp:78`:

```cpp
bool g_enableQ3ShaderStages = false;
```

There is no preference, cvar, command-line switch or menu item anywhere in the tree that changes it. The
changelog records the sequence: enabled by default, then *"temporarily disabled Quake 3 shader stage
rendering by default to avoid material browser crashes while the root cause is investigated."* The root
cause was not found. Multi-stage preview, hover animation, the live shader-editor preview and the 3D
animate toggle are all gated behind that constant and unreachable in a built binary.

**Auto-updater.** `radiant/update.cpp` is a finished client: GitHub Releases API, stable and prerelease
channels, an `update.json` manifest asset, install and relaunch for Windows zip, Linux AppImage and macOS
tar.gz. The workflows that produced those artifacts (`release.yml`, `nightly.yml`) were deleted on
2026-04-24, and `api.github.com/repos/themuffinator/VibeRadiant/releases` returns `[]`. Every check
resolves to `"No matching release with update.json was found."`

**Language packs.** 20 locale files in `setup/data/tools/i18n/`, **28 keys each**, against an editor with
thousands of user-facing strings. Coverage is the menu bar plus a handful of dialog titles. Quality is
machine-grade: `de.json` renders `"Brush"` as `"Pinsel"`, a painter's brush, which is the wrong word for
a convex solid.

**Smart Tags.** `TextureBrowser_loadSmartTagRules` reads `<gametools>/smarttags.txt` and a user file. No
`smarttags.txt` exists anywhere in the repository, so the feature ships empty.

### 2.3 Named for something they are not

**UV View panel** — `radiant/uvview.cpp`, 234 lines, displays no UVs. It is a column of `QPushButton`s
that call commands which already have keybinds (`FitTexture`, `TexShiftLeft`, `MouseUV`, and so on) plus
a status label refreshed by a 250 ms polling `QTimer`. A Radiant user reading "UV View" will expect a
texture-space canvas.

**Macro recording** — `radiant/commands.cpp:120`. Records command *names* only: no arguments, no mouse
input, no selection context, no persistence across sessions. `GlobalCommands_insert` wraps its callback
in the recorder; `GlobalToggles_insert` at line 247 does not. Mode switches, filters, grid toggles and
mouse-tool changes are therefore invisible to it, which is most of what a macro would want to capture.

### 2.4 Working, but approximate where the changelog claims precision

**Asset drag-and-drop** — `radiant/assetdrop.cpp`. Entities, models, sounds and textures drag from the
browser into the 2D and 3D views. Two defects:

- `findBrushAtPoint` and `findEntityAtPoint` pick the candidate whose **AABB centre** is nearest the drop
  point, after inflating every bounds by `max(8, gridSize)`. There is no surface trace. So "flush on top
  of the hit surface" is bbox-approximate, and a large brush can beat the small one under the cursor.
- `AssetDrop_handleSoundPath`, `_handleTexture` and `_handleModelPath` each open an `UndoableCommand`.
  `AssetDrop_handleEntityClass` does not, and neither call site wraps it (`xywindow.cpp:629`,
  `camwindow.cpp:1781`). The one path that creates a brush, converts it to an entity and reshaders it is
  the one that is not a single undo step.

**GenAI Prompt-to-Blockout** — `radiant/genai.cpp`, the largest single addition. More real than the name
suggests: OpenAI Responses API planning with a deterministic heuristic fallback when unconfigured; hollow
room and corridor shells with wall openings aligned to the link that crosses them; mitred corridor side-
wall joins; typed traversal links (`door`, `stairs`, `ramp`, `func_plat`, `jumppad`, `teleporter`);
BFS-derived main progression path; playstyle inference from the prompt that changes the graph shape;
per-surface shader controls; an idTech3 caulk option; and the whole build wrapped in one
`UndoableCommand`.

Against that: `GenAI_generateOpenAIBlockoutPlan` runs a **nested blocking `QEventLoop`** over a
stack-allocated `QNetworkAccessManager` (`genai.cpp:3132`), so the editor freezes for up to the 60-second
timeout, and re-entrant event pumping is the exact class of bug this fork already had to fix once during
startup. And `docs/ai-level-design-tools.md` specifies seven tools across four phases; one exists.

### 2.5 Product surface

Startup journey (splash, update check, setup, onboarding, loading screen, welcome dialog), a tokenized
theme engine with density and accent controls, a collapsible console drawer with severity chips, command
palette, workspace presets, focus mode, preferences split-navigation with search, VSCode-style build and
launch tasks, a game install manager, the gamepack refactor (`VibePack` as canonical source, `.game` keys
migrated to camelCase, `.def` converted to `.fgd`), idTech2/idTech4/DarkMod support, and version-aware
BSP import shelling out to `bsputil` or `mbspc`.

This is where most of the 36,599 added lines went, and the cost is visible in the changelog itself. Of
273 bullets, **36 (13%) are the fork repairing crashes and build breaks it introduced**: 13 begin
`Build fixes`, 23 begin `Startup stability`, `Runtime stability`, `UI stability` or `Startup robustness`.
Four rollback and crash-isolation switches survive in shipped code — `-startup-legacy-flow`,
`-startup-no-welcome`, `-startup-debug-skip-to-loading`, `-startup-debug-mainwindow-only`.

---

## 3. Three shapes that repeat

**A finished client with no producer.** The updater, Smart Tags and the language packs are each a
well-written consumer of data that was never generated. In every case the consumer shipped and the
producer did not.

**A flag that ends at `false`.** Build the feature, hit a crash, switch it off "temporarily", ship the
switch. `g_enableQ3ShaderStages` is the clean example, and the four `-startup-*` flags are the same
instinct applied to a subsystem too large to switch off in one constant.

**A name that outruns the code.** "UV View" with no UV canvas; "macro recording" that cannot record a
mode change. `AGENTS.md` instructs *"Update `CHANGES_FROM_NRC.md` whenever a significant change is
implemented"*, and the entries read as written from the intent of the commit rather than from the
behaviour of the result.

Upstream drift compounds all three. Seven months behind, the fork is missing a fix for group entities
with an origin brush and a GCC 16.1 build fix it will hit, while having independently written a second
BSP-import path rather than inheriting the one NRC made ten times faster in January.

---

## 4. What to take

None of this is code we would copy. It is C++/Qt/fixed-function OpenGL against Radiant's scene graph; we
are C#/Godot against `VmapDocument`. What ports is the design.

### 4.1 First — T8, find and replace

Not previously on the backlog, and the highest-value idea in the fork. On a 2,666-brush map, retexturing
by hand is a job; with wildcard and regex find/replace scoped to selection or faces, it is a command.

It fits our architecture with no new concepts: a selection predicate plus a `SetObjectsOp`, which means
it replicates through the existing `VmapEditSession.Applied` choke point without any wire work. Take the
whole feature matrix — match modes, backreferences, include/exclude filters, flag masks, scope. Filed as
**T8**.

### 4.2 Second — design input for F8, not a spec

Linked groups are the right primitive for a symmetric CTF map: build one base, get the other. The parts
worth keeping are the data model (a link id on the group, a per-instance affine transform, copies related
by delta) and the propagation discipline (one dirty set opened and flushed per command, with a re-entry
guard).

**F8 stays blocked on a design pass, and this does not unblock it.** Grouping, layers and linked
instances are three separate ideas that Radiant conflates, and the UI question — what a group looks like
when selected, how you enter and leave one, whether a layer is a group or an orthogonal axis, what
happens when a linked copy is edited directly — is not answerable from reading someone else's C++. That
wants interactive prototyping before anything is written. Recorded against F8 as prior art with a named
flaw to avoid: their clone-and-replace-all-children sync is the wrong shape for us, because we would be
re-emitting whole-object ops over the wire on every keystroke. Ours diffs and emits only changed ids.

### 4.3 Third — the relight scheduler for P3/T2

We already have the physics: `EditorLightBake` is q3map2's light model running against the document. What
we do not have is interactivity. Their scheduling is directly transplantable and is the whole trick:
content-hash each brush and patch including tessellation, keep a dirty deque, spend a fixed millisecond
budget per frame draining it, and preserve caches when geometry is hidden or the mode is off so
re-enabling rebuilds only what changed. That turns a batch bake into something a mapper tunes lights
against.

Do not inherit their 8-bit clamp. We already know that trap from the editor bake work; keep HDR through
to the atlas.

### 4.4 Also worth filing

- **T9 — selection undo/redo.** Cheaper and safer for us than for them: our objects have stable ids, not
  raw pointers, so both their ABA hazard and their lossy component restore disappear. A snapshot is a set
  of ids plus a mode.
- **T10 — map diagnostics panel.** Serves V1 and V2 directly by catching the entity-wiring mistakes those
  passes will produce. Widen past their three checks: `target2..4`, `killtarget`, and Vortex-specific
  ones such as unreachable spawns.
- **F4 — the drop interaction.** Drag-from-palette-to-create is the interaction F4 wants. Take it, and
  fix the two flaws on the way in: pick with `VmapPicking` against a real surface hit rather than a bbox
  centre, and wrap create-brush-then-make-entity in one op.
- **T6 — the filter bar.** Their layout is a reasonable reference for name search plus surface/content
  flag filters plus in-use/unused. Skip Smart Tags; the tag-file format duplicates work our material
  metadata already does.

### 4.5 Read, do not port — the blockout planner

The layout-synthesis half of `genai.cpp` is worth reading against the procgen track
(`planning/procedural-map-decoration.html`), even though the plumbing is not. Playstyle inference driving
different graph shapes (deathmatch toward loops and contested control space, campaign toward readable
progression with paced detours), BFS main-path derivation, hub/arena/connector/secret room roles,
choke-point narrowing, spaced major items against budgeted minor pickups. That is a coherent position on
what makes an arena layout good, expressed as operations on a graph.

Two things we would do differently. Never block the game loop on an HTTP call; ours would be async and
cancellable. And we hold the half they lack: we can playtest the generated layout with bots in the same
process that generated it. Their generator can only assert that a layout plays well. Ours can measure it.

---

## 5. What not to take

- **The preview-lighting renderer.** Godot gives us real-time lighting natively; their CPU-lightmap
  overlay exists to work around fixed-function OpenGL. Take the scheduling, discard the rasterization.
- **The UV View panel.** A button rack over commands our binds already reach.
- **Macro recording as designed.** Command-name replay that cannot capture a mode switch is not worth the
  surface area. If we want macros, they should record ops, which are already serializable — a better
  foundation than theirs.
- **Updater, theme engine, startup journey, gamepack manager.** Desktop-application product surface with
  no analogue in an in-game editor.
- **Their asset-drop picking, their linked-group sync loop, their pointer-identity selection snapshots.**
  Each carries a specific defect named in §2 and §4.

---

## 6. Verdict

About a third of the fork is engineering worth learning from: the find/replace subsystem, the
preview-lighting scheduler, linked groups, selection undo. The rest is product scaffolding at varying
distances from finished, plus a headline feature disabled by a constant and an updater with nothing to
update from. The 13% of the changelog spent repairing its own regressions, the four surviving rollback
flags and the deletion of CI on the final commit are the accurate signal about its state.

Our shortlist, in order: **T8 find/replace**, then the **relight scheduler into P3/T2**, then
**T9** and **T10**. Linked duplicates informs **F8** and waits on F8's design pass.

---

## Appendix — citations

| Claim | Location |
|---|---|
| Q3 shader stages off, no way to enable | `plugins/shaders/shaders.cpp:78` |
| Preview lighting frame budget | `previewlighting.cpp:1026` (`kWorkBudgetMs = 6.0`) |
| Preview lighting LDR clamp | `previewlighting.cpp:1760` |
| Uncached per-frame fast model | `previewlighting.cpp:2620` |
| Find/replace option surface | `radiant/select.h:25`, `findtexturedialog.cpp:578` |
| Linked group keys and sync | `include/linkedgroups.h:80`, `linkedgroups.cpp:537` |
| Selection undo tracker | `selection.cpp:7004`, restore at `:7294` |
| Macro records names only; toggles unwrapped | `commands.cpp:120`, `commands.cpp:247` |
| Asset drop missing undo | `assetdrop.cpp:200`, call sites `xywindow.cpp:629`, `camwindow.cpp:1781` |
| Asset drop bbox-centre picking | `assetdrop.cpp:81` |
| GenAI blocking event loop | `genai.cpp:3132` |
| Updater expects `update.json` release asset | `update.cpp:102`, `update.cpp:280` |
| Translation coverage | `setup/data/tools/i18n/*.json`, 28 keys per file |

# Map editor backlog

Everything between the editor as it stands (E0–E8 built, reviewed 2026-07-29) and an editor a mapper can make
a Vortex Arena map with. IDs are stable — cite them in commits and branches.

Design doc: `planning/procedural-map-decoration.html` (§11 the editor, §13.1 the completeness audit).

| Prefix | Area |
|---|---|
| **P** | Pipeline — getting a map out of the editor |
| **F** | Format & core editing operations |
| **T** | Tools & interaction |
| **B** | Bugs |
| **C** | Co-editing |
| **V** | Verification |

Status: **open** · **partial** (some of it landed) · **done**.

---

## P · Pipeline

The gap that makes the rest secondary. The importers run one way — `.bsp` and `.map` go in via `BspToVmap`
and `MapSourceReader`, and nothing comes out — so nothing authored in the editor can leave the editor
gametype on the machine holding the package.

The question this section had to answer first was whether `.vmap` should stay uncompiled. It should:
measurement says the cost of not compiling is load time only, and the fix for load time is a cache, not a
format. See P6.

**Order: P6 → P5 → P3/P4 → P1/P2.** The measurement (`VmapVsBspLoadBench`, commit `30f3a73`) says a compiled
format buys nothing at frame time in this engine — Godot owns culling and draw submission, and the document
path already produces *fewer* draw batches than the BSP path (43 vs 50 on stormkeep, 93 vs 103 on fuse, 70 vs
105 on catharsis), because a BSP splits batches by lightmap page while the document batches purely by
material. What a compile actually buys is **not recomputing deterministic derivations of the document**. That
is a cache, not a format.

| ID | Item | Status |
|---|---|---|
| **P6** | **Build cache, keyed on a document hash.** `.vmap` stays the single truth; a sidecar holds whatever was derived from it. A hash mismatch makes staleness impossible rather than merely unlikely, and deleting the cache costs speed, never correctness. **Do this first** — it is the delivery mechanism P3 and P4 need, not merely a speedup. Two tiers, and the distinction is the whole design (see below). | open |
| **P5** | **Boot a `.vmap` outside the editor gametype.** Collision already builds from a document (`VmapCollisionBuilder`); the map-load path only looks for a `.bsp`. Closes the loop with no compile step, and needs only P6's cheap tier to be worth playing. | open |
| **P3** | **Lightmap output from the existing bake.** `EditorLightBake` is already q3map2's light model (`EMIT_*`, `photons/d²`, area cosine, 8-bounce) running against the document. What it lacks is atlas output rather than per-vertex — a packing problem, not a physics one. Ships in P6's baked tier. | open |
| **P4** | **Visibility for `.vmap`.** **Settled** (see below): bake Godot occluders for rendering, no PVS; a coarse cell bitset for gameplay *only if* a bigger match profiles it as needed; no portal-flood vis compiler. Ships in P6's baked tier. | open, **decided** |
| **P1** | **`.map` writer.** Brushes are already plane sets and patches already control grids, so both are near-direct writes into a format q3map2 and NetRadiant already read. This is the **interop** route — not the engine's route — so it ranks after the engine can stand on its own. | open |
| **P2** | **Layer flattening on export.** `.map` has one shader per face, so a P1 export flattens a layer stack to its base and says what it dropped. Lossy on purpose; silence is the thing to avoid. | open |

### P6 — the two tiers

The tiers differ by what a cache MISS costs, and that is what decides how each behaves:

| | **Derived** (cheap) | **Baked** (expensive) |
|---|---|---|
| What | Triangulated surfaces, collision brushes | PVS, lightmaps |
| Cost to compute | 0.1–0.7 s on a normal map (measured) | seconds to minutes |
| On a miss | Rebuild silently. Always correct. | **Cannot** rebuild at load — run degraded and say so |
| Scope | Local, disposable | Ships with the map, versioned |

The baked tier is the interesting half: it is not an optimization but the only practical way to deliver
something you cannot afford to compute at load. Which makes it exactly what a `.bsp` is — the difference being
that here it is a sidecar next to a source you can still edit, rather than the only artifact that survives.

**Degrading well is already the behaviour, not a new thing to build.** `CheckPvs` returns *true* on an unvised
map, so every gameplay caller (`SpawnSystem`, `MonsterAI`, `TurretAI`, `SpawnNearTeammateMutator`) treats it
as a conservative pre-filter before an exact trace — without PVS you do more traces and get the same answers.
And the editor already renders *fuse* with no PVS at all. So a baked-tier miss means slower and unlit, never
wrong, which is what makes shipping the cache separately from the source safe.

### P4 — settled 2026-07-29: bake occluders, bake a coarse bitset, write no vis compiler

A BSP supplied rendering and gameplay visibility from one structure, which is why they read as one problem.
They are two, and measuring them separately (`VisibilityValueBench`) decided both.

**Rendering — no PVS, bake Godot occluders.** The world is one `MeshInstance3D` per (1024-unit cell,
material), and that is a small number:

| map | instances | cells | materials | tris | tris/instance |
|---|---|---|---|---|---|
| stormkeep | 366 | 32 | 43 | 104,223 | 284 |
| fuse | 735 | 20 | 93 | 216,667 | 294 |
| catharsis | 1,030 | 63 | 70 | 111,916 | 108 |

Godot frustum-culls those every frame already, and occlusion culling is enabled
(`occlusion_culling/use_occlusion_culling=true`, gated per viewport by `r_occlusion_cull`). PVS would be a
third filter over a list of ~1000 items. Bake `OccluderInstance3D` geometry into P6's baked tier and stop.

**Gameplay — a coarse cell bitset, not a portal flood.** PVS rejects a real share of the queries gameplay
asks, sampled over the map's own spawn/item/weapon positions:

| map | points | pairs | rejected | rate |
|---|---|---|---|---|
| stormkeep | 112 | 6,216 | 3,625 | **58.3%** |
| fuse | 46 | 1,035 | 591 | **57.1%** |
| catharsis | 146 | 10,585 | 8,258 | **78.0%** |
| afterslime | 64 | 2,016 | 1,020 | **50.6%** |
| solarium | 72 | 2,556 | 698 | **27.3%** |

So it earns its keep as a filter — over half of `CheckPvs` queries never become traces. But **rejections at
that rate are gross separation** (different rooms, opposite ends of a map), which is exactly what a coarse
cell grid captures. Portal-flood precision buys only the marginal cases: the sliver-of-a-doorway pairs.

**And it is not urgent.** Running the same 8-bot stormkeep tick with the PVS wired and unwired put the
difference inside run-to-run variance — the sign of the delta flipped across three repeats (−2.6%, +4.8%,
−22.1% median). On a 0.6 ms tick against a 13.9 ms budget, the traces PVS saves are too cheap to measure at
this scale. It would matter more with more entities (16-player CTF, monsters, turrets), which is the case to
re-measure before spending anything on it.

**Decision.** Ship P5 with no visibility at all — already correct, since `CheckPvs` returns true unvised and
the editor renders *fuse* that way today. Then bake occluders for rendering. Only build the coarse gameplay
bitset if a bigger match shows it on a profile. No portal-flood vis compiler, at any point.

> **Ordering note.** P1 before F1 would be a mistake: once an exporter exists it quietly becomes the spec and
> argues against every feature that does not survive the round trip. Extend the format first, export second.

---

## F · Format & core editing operations

| ID | Item | Status |
|---|---|---|
| **F1** | **Face layer stacks.** A face is a stack of layers, each with its own material, projection and blend, instead of one material. Persisted, replicated, rendered as a `next_pass` chain, undo-safe. Single-layer faces write the bytes they always did. | **done** (`82c8b27`) |
| **F2** | **Blend maps — paintable layer weights.** *Redesigned 2026-07-29, see below.* A weight TEXTURE with its own planar projection, sampled per-texel, not a per-vertex weight. | open |
| **F3** | **The paint tool.** What makes F2 usable: a brush that paints into the blend map, sized and softened, one stroke = one op. | open |
| **F4** | **Brush entities can be created.** Nothing turns a selection into a `func_door` — they import and their keys edit, but the editor authors only static geometry and point entities, and every dynamic element in a Xonotic map is a brush entity. An op assigning selected brush/patch ids to a new entity; the ownership plumbing exists everywhere else already. | open |
| **F5** | **CSG: subtract.** Radiant's carving workflow. No workaround today. | open |
| **F6** | **CSG: merge and hollow/room.** Lower value than F5; hollow is a convenience over six clipped brushes. | open |
| **F7** | **Texture lock.** *(done — `f50c2a9`)*  `TranslateBrushesOp` moves planes and leaves the projection alone, so a moved brush slides its texture and alignment work is lost on every move. `PasteOp` already offsets the projection with the geometry, so the behaviour is inconsistent as well as wrong. Wants a cvar; default ON. | open |
| **F8** | **Grouping and layers.** Named sets of objects, hidden/shown and selected together. | open |
| **F10** | **`.map` export must flatten a layer stack** — tracked as P2. | see P2 |
| **F9** | **Region / hide / isolate.** Narrow what you are working on — and what gets rebuilt — on a 2666-brush map. The one of F8/F9 that matters more. | open |

### F2 — why blend weights are a texture, not a vertex attribute

The first design put the weight on the mesh VERTEX, with `VmapFaceLayer.WeightChannel` indexing R/G/B/A of a
custom vertex channel. That is how terrain systems do it, and it is wrong here for a reason that only shows up
when you try to use it: **a brush face is a convex polygon with as few as three vertices.** A flat wall would
offer four control points to "paint" with. Terrain meshes get away with it because they are already a dense
grid; brush faces are not, and subdividing them to gain paint resolution would mean inventing a second,
denser geometry representation purely so the painting had somewhere to live.

So the weights live in a **blend map**: an RGBA texture (up to four layer weights) with its own planar
projection over the face — the same trick `VmapTexProjection` already uses for diffuse, so brushes need no UV
unwrap. Painted at texel resolution, sampled in the shader, resolution set by an author-controlled
texels-per-unit the way a lightmap sample size is.

Consequences worth writing down before building it:

- **It is SOURCE data, not derived.** The mapper painted it, so it belongs in the `.vmap` package, not the P6
  build cache. That means the package gains binary image data for the first time.
- **Undo cannot snapshot it the way it snapshots a brush.** A journal entry holding a full texture per stroke
  would be enormous. Strokes want to be the undo unit, replayed or inverted, rather than the bitmap.
- **Replication travels strokes, not bitmaps.** Same reasoning: an op carrying a whole blend map would not fit
  the wire (and the wire already caps a line).
- **`WeightChannel` survives the redesign** — it still selects which of the four channels a layer reads. Only
  where the weight comes FROM changes.

---

## T · Tools & interaction

| ID | Item | Status |
|---|---|---|
| **T1** | **Depth-checked entity boxes.** Entity bboxes draw through walls, so a big map shows every entity at once. Wants an occlusion test per box and a cvar to switch it off (seeing through walls is sometimes the point). | open |
| **T2** | **Lights get their own tool.** Out of the entity palette, into `EditorTool.Light` with its own dialog — intensity, radius, colour, spot cone, the q3map2 keys the bake actually reads. Lights are the thing you tune most and the least like other entities. | open |
| **T3** | **Split visual grid from alignment grid.** One grid size currently drives both what you see and what you snap to. Two sizes, two controls; the drawn grid is a reference, the snap grid is a constraint, and they are not the same decision. | open |
| **T4** | **Scroll wheel drives camera speed.** Free-fly speed is what you adjust constantly; grid size is not. Move grid size behind a held **G** — tap G to toggle the grid, hold G and scroll to change the alignment grid (T3's, not the visual one). | open |
| **T5** | **Snap to adjacent brush / plane.** Toggleable, with a snap distance you can raise and lower. Snap candidates: nearby face planes, edges and vertices of neighbouring brushes, so things line up without hand-typing coordinates. The measure tool's picking already finds nearby geometry and is the obvious starting point. | open |
| **T6** | **Texture browser thumbnails.** It is a name list grouped by path segment. Nobody picks a wall texture from strings. | open |
| **T7** | **Entity scaling.** *(done — `348c16d`)*  `ScaleSelectionOp` takes brush and patch ids only. A brush entity should scale the geometry it owns (like `MoveEntitiesOp` resolves its brushes); a point entity should write a `scale`/`modelscale` key. See B4. | open |

---

## B · Bugs

| ID | Item | Status |
|---|---|---|
| **B1** | **Entities would not move.** `SelectedBrushIds()` returned a phantom brush id 0 for entity and patch selections, so the entity-move gate (`SelectedBrushIds().Count == 0`) never opened; the drag fell through to a switch with no `Entity` case and returned false silently. | **fixed** (`d48a6c8`) |
| **B2** | **Scale said "that would break the brush" for an entity.** Same phantom 0 — `ScaleSelectionOp` got brush id 0, `FindBrush(0)` failed, and the op reported invalid geometry for a brush that was never selected. The nonsense message is gone. | **fixed** (`d48a6c8`) |
| **B3** | **Items sit wrong on patches (Stormkeep mega health).** Root-caused: patch **collision** tessellates at 3 subdivisions (`BspCollisionBuilder.PatchCollisionSubdivisions`) while **render** uses 8 (`BezierPatch.Subdivisions`). On a curve the coarse hull deviates from the drawn surface, so a dropped item rests at the wrong height. Measured on the 15 curved patches under the mega health at (1696, −256, −32) in `stormkeep.map`: **worst deviation 6.49 units**. DP builds collision from the same subdivision it renders, which is why Base looks right. Fix wants to be curvature-adaptive and walkable-biased rather than a flat bump to 8 — flat patches are exact at any level, and uniform 8 is ~7× the slab count (~12k → ~85k on stormkeep). | open, diagnosed |
| **B4** | **Scaling an entity still does nothing.** *(fixed — `348c16d`)*  B2 removed the wrong message; the capability was never there. Tracked as T7. | open |
| **B5** | **No texture lock** — see F7. *(fixed — `f50c2a9`)* Filed as a feature, but a mapper will report it as a bug. | open |

*Fixed during the 2026-07-29 review and listed for the record: a remote OOM in the op codec, packets corrupted
by any sizeable paste, a guest that could not paste, shift-multiselect deselecting instead of adding, every
PLAYTEST toggle orphaning derived entities, an ungated main-thread broadcast into `ServerNet`, a non-atomic
save, A\* indexing at −1 on a deleted waypoint, the editor never reopening a saved `.vmap`, and patches having
no delete op.*

---

## C · Co-editing

| ID | Item | Status |
|---|---|---|
| **C1** | **Op replication.** Server-authoritative ops, id-carrying create handshake, paste as `AddObjectsOp`, undo as `SetObjectsOp`, per-brush locks. Protocol v20. | **done** (`d16453c`) |
| **C2** | **Document handshake on join.** A guest imports the map itself rather than receiving the host's document, so a host editing a saved `.vmap` and a guest importing the `.bsp` would number brushes differently and every replicated op would land on the wrong solid. It says so on join instead of diverging silently. | open |
| **C3** | **Guest-local undo.** Undo is the host's journal; a guest submits and watches. Fine for one authoritative mapper plus helpers, wrong for two peers. | open |

---

## V · Verification

| ID | Item | Status |
|---|---|---|
| **V1** | **Drive the editor by hand.** Every op and both replication directions are under test; the interaction layer — menus, two-phase handle grabbing, drag feel, the readouts — has been built and never once operated by a human. The cheapest remaining source of surprises. | open |
| **V2** | **A playtest pass on a map built from nothing.** Greybox a room with the editor alone, playtest it with bots, and see what the workflow is actually missing. Will reorder half this list. | open |

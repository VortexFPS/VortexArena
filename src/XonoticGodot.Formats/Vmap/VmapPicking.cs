using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>What a camera ray hit, and where.</summary>
public readonly struct VmapPickResult
{
    public bool Hit { get; init; }
    public VmapSelection Selection { get; init; }

    /// <summary>World position of the hit point on the surface.</summary>
    public Vector3 Point { get; init; }

    /// <summary>Distance along the ray to <see cref="Point"/>.</summary>
    public float Distance { get; init; }

    /// <summary>Outward normal of the face that was hit (useful for the drag axis of a face push).</summary>
    public Vector3 Normal { get; init; }

    public static VmapPickResult Miss => new() { Hit = false, Selection = VmapSelection.None };
}

/// <summary>
/// A cached, broadphase-accelerated view of a document's geometry for picking and snapping.
///
/// Evaluating brush windings is the expensive part of every spatial query, and a real map is thousands of
/// brushes — solving a crosshair pick from scratch on stormkeep's 5400 cost ~43 ms, which is fine standing
/// still and unusable while flying. So windings are computed ONCE per geometry version and kept, together
/// with each brush's bounds; a query then rejects almost every brush with a ray/AABB slab test and only looks
/// at polygons for the handful the ray actually crosses.
///
/// The cache is keyed on <see cref="Version"/>: bump it after an edit and the next query rebuilds. Holding
/// the windings is also what lets the orthographic wireframe be rebuilt without recomputing them a second
/// time — the same data serves picking, snapping and drawing.
/// </summary>
public sealed class VmapPickIndex
{
    /// <summary>One brush's cached geometry.</summary>
    public sealed class Entry
    {
        public required VmapBrush Brush { get; init; }
        public required Vector3 Mins { get; init; }
        public required Vector3 Maxs { get; init; }

        /// <summary>Per-face polygons, parallel to <see cref="VmapBrush.Faces"/>; empty for a bevel plane.</summary>
        public required Vector3[][] Windings { get; init; }
    }

    /// <summary>A cached patch: its tessellated triangles and bounds, for object-level picking.</summary>
    public sealed class PatchEntry
    {
        public required VmapPatch Patch { get; init; }
        public required Vector3 Mins { get; init; }
        public required Vector3 Maxs { get; init; }

        /// <summary>Triangle soup, three vertices per triangle.</summary>
        public required Vector3[] Triangles { get; init; }
    }

    /// <summary>
    /// A cached POINT entity: its descriptor box in world space. Brush entities are not here — they are picked
    /// through the geometry they own, which is already in the brush index.
    /// </summary>
    public sealed class EntityEntry
    {
        public required VmapEntity Entity { get; init; }
        public required Vector3 Mins { get; init; }
        public required Vector3 Maxs { get; init; }
    }

    private readonly List<Entry> _entries = new();
    private readonly List<PatchEntry> _patches = new();
    private readonly List<EntityEntry> _entities = new();

    // Brush and patch bounds again, as flat floats (six per item, parallel to Entries/Patches): min x/y/z then
    // max x/y/z. The same numbers the Entry objects hold, laid out contiguously.
    //
    // One crosshair pick a frame can afford to walk a List of heap objects; the entity-occlusion sweep
    // (backlog T1) fires many rays a frame, and there the cost is the pointer chase rather than the slab
    // arithmetic. Flattening keeps the reject pass in cache and costs one array per rebuild.
    private float[] _brushBounds = Array.Empty<float>();
    private float[] _patchBounds = Array.Empty<float>();

    /// <summary>Flat brush bounds, six floats per <see cref="Entries"/> item.</summary>
    internal float[] BrushBounds => _brushBounds;

    /// <summary>Flat patch bounds, six floats per <see cref="Patches"/> item.</summary>
    internal float[] PatchBounds => _patchBounds;

    // Segment broadphase, built LAZILY: only the occlusion sweep needs it, and an editing session that never
    // opens the entity tool should not pay to bucket 75,000 brushes after every edit.
    private readonly VmapCellGrid _brushGrid = new();
    private readonly VmapCellGrid _patchGrid = new();
    private readonly List<int> _brushCandidates = new();
    private readonly List<int> _patchCandidates = new();

    /// <summary>
    /// Indices into <see cref="Entries"/> that a segment could cross. The returned list is REUSED between
    /// calls — copy it if you need it to outlive the next query.
    /// </summary>
    internal List<int> BrushesAlong(Vector3 from, Vector3 to)
    {
        if (!_brushGrid.Built)
            _brushGrid.Build(_brushBounds, _entries.Count);
        _brushGrid.Segment(from, to, _brushCandidates);
        return _brushCandidates;
    }

    /// <summary>Indices into <see cref="Patches"/> that a segment could cross. Same reuse caveat.</summary>
    internal List<int> PatchesAlong(Vector3 from, Vector3 to)
    {
        if (!_patchGrid.Built)
            _patchGrid.Build(_patchBounds, _patches.Count);
        _patchGrid.Segment(from, to, _patchCandidates);
        return _patchCandidates;
    }

    /// <summary>Cell size the brush broadphase settled on, in world units. Diagnostics only.</summary>
    public float BrushGridCellSize => _brushGrid.CellSize;

    /// <summary>Brushes too large to bucket, so tested on every segment query. Diagnostics only.</summary>
    public int BrushGridOversized => _brushGrid.OversizedCount;

    /// <summary>Cached point entities, for picking and for drawing their boxes.</summary>
    public IReadOnlyList<EntityEntry> Entities => _entities;

    /// <summary>
    /// Class descriptors used to size entity boxes. Assign before the first build; null falls back to a small
    /// cube per entity, which still draws and still picks.
    /// </summary>
    public EntityDefs? Defs { get; set; }

    /// <summary>Cached patches, for picking and for drawing their outlines.</summary>
    public IReadOnlyList<PatchEntry> Patches => _patches;
    private VmapDocument? _doc;
    private bool _includedTools;
    private int _hiddenStamp;

    /// <summary>
    /// Inline model indices hidden by the gametype filter. Assign, then call <see cref="Invalidate"/>.
    /// </summary>
    public HashSet<int> HiddenSubmodels { get; } = new();

    /// <summary>Geometry version this cache was built for; -1 when never built.</summary>
    public int Version { get; private set; } = -1;

    /// <summary>The cached brushes, for consumers that want the windings directly (e.g. wireframe drawing).</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Rebuild if the document or version changed. Cheap no-op when already current.</summary>
    /// <param name="includeToolBrushes">
    /// Include q3map2 tool brushes (hint/skip/clip/trigger/caulk). Off by default: they are scaffolding rather
    /// than architecture, and they sit in front of the geometry you actually want to grab.
    /// </param>
    public void EnsureBuilt(VmapDocument doc, int version, bool includeToolBrushes = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (Version == version && ReferenceEquals(_doc, doc) && _includedTools == includeToolBrushes
            && _hiddenStamp == HiddenSubmodels.Count)
            return;
        _includedTools = includeToolBrushes;
        _hiddenStamp = HiddenSubmodels.Count;

        _entries.Clear();
        _patches.Clear();
        _entities.Clear();
        _doc = doc;
        Version = version;

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (brush.IsToolBrush && !includeToolBrushes)
                continue;
            // Geometry belonging to an inline model the current gametype filter hides (e.g. a CTF-only
            // func_wall while editing for deathmatch). Hidden, never discarded.
            if (brush.SubmodelIndex != 0 && HiddenSubmodels.Contains(brush.SubmodelIndex))
                continue;

            Vector3[][] windings = VmapWinding.BuildBrushWindings(brush);

            var mins = new Vector3(float.MaxValue);
            var maxs = new Vector3(float.MinValue);
            bool any = false;
            foreach (Vector3[] w in windings)
            {
                foreach (Vector3 p in w)
                {
                    mins = Vector3.Min(mins, p);
                    maxs = Vector3.Max(maxs, p);
                    any = true;
                }
            }
            if (!any)
                continue;   // bounds-less brush: nothing to hit or snap to

            _entries.Add(new Entry { Brush = brush, Mins = mins, Maxs = maxs, Windings = windings });
        }

        // Patches are tessellated once here for the same reason brush windings are: a curved surface has no
        // plane set to intersect, so picking it means testing real triangles, and rebuilding them per frame
        // would cost what the broadphase was added to avoid.
        foreach (VmapPatch patch in doc.Patches)
        {
            if (!patch.IsValid)
                continue;

            var single = new VmapDocument();
            single.Patches.Add(patch);
            IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(single, includeSky: true);

            var tris = new List<Vector3>(256);
            var pmins = new Vector3(float.MaxValue);
            var pmaxs = new Vector3(float.MinValue);
            foreach (VmapSurface surf in surfaces)
            {
                foreach (int idx in surf.Indices)
                {
                    Vector3 v = surf.Positions[idx];
                    tris.Add(v);
                    pmins = Vector3.Min(pmins, v);
                    pmaxs = Vector3.Max(pmaxs, v);
                }
            }
            if (tris.Count < 3)
                continue;

            _patches.Add(new PatchEntry
            {
                Patch = patch, Mins = pmins, Maxs = pmaxs, Triangles = tris.ToArray(),
            });
        }

        // Point entities, boxed from their class descriptor. A brush entity is deliberately skipped: it has no
        // origin, and its geometry is already pickable through the brush index — giving it a second, invisible
        // box would mean clicking a door sometimes grabbed the door and sometimes a phantom volume around it.
        foreach (VmapEntity ent in doc.Entities)
        {
            if (ent.IsBrushEntity)
                continue;
            if (string.Equals(ent.ClassName, "worldspawn", StringComparison.OrdinalIgnoreCase))
                continue;   // worldspawn is the map itself, not something you click

            EntityClassDef def = Defs?.GetOrPlaceholder(ent.ClassName)
                ?? new EntityClassDef { Name = ent.ClassName };
            Vector3 origin = ent.Origin();
            _entities.Add(new EntityEntry
            {
                Entity = ent,
                Mins = origin + def.DrawMins,
                Maxs = origin + def.DrawMaxs,
            });
        }

        _brushBounds = FlattenBounds(_entries.Count, i => (_entries[i].Mins, _entries[i].Maxs));
        _patchBounds = FlattenBounds(_patches.Count, i => (_patches[i].Mins, _patches[i].Maxs));
        _brushGrid.Reset();
        _patchGrid.Reset();
    }

    private static float[] FlattenBounds(int count, Func<int, (Vector3 Mins, Vector3 Maxs)> at)
    {
        var flat = new float[count * 6];
        for (int i = 0; i < count; i++)
        {
            (Vector3 mins, Vector3 maxs) = at(i);
            int b = i * 6;
            flat[b] = mins.X;
            flat[b + 1] = mins.Y;
            flat[b + 2] = mins.Z;
            flat[b + 3] = maxs.X;
            flat[b + 4] = maxs.Y;
            flat[b + 5] = maxs.Z;
        }
        return flat;
    }

    /// <summary>Whether this index was built including tool brushes (drives the per-face pick filter too).</summary>
    public bool IncludesToolBrushes => _includedTools;

    /// <summary>Force a rebuild on the next <see cref="EnsureBuilt"/> call.</summary>
    public void Invalidate() => Version = -1;

    /// <summary>
    /// Slab test: does the ray reach this box within <paramref name="maxDistance"/>? Uses the reciprocal
    /// direction so the per-brush cost is a handful of multiplies — this is what makes rejecting 5400 brushes
    /// cheap enough to do every frame.
    /// </summary>
    internal static bool RayHitsBox(Vector3 origin, Vector3 invDir, Vector3 mins, Vector3 maxs, float maxDistance)
    {
        float t0 = (mins.X - origin.X) * invDir.X;
        float t1 = (maxs.X - origin.X) * invDir.X;
        float tmin = MathF.Min(t0, t1);
        float tmax = MathF.Max(t0, t1);

        t0 = (mins.Y - origin.Y) * invDir.Y;
        t1 = (maxs.Y - origin.Y) * invDir.Y;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1));
        tmax = MathF.Min(tmax, MathF.Max(t0, t1));

        t0 = (mins.Z - origin.Z) * invDir.Z;
        t1 = (maxs.Z - origin.Z) * invDir.Z;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1));
        tmax = MathF.Min(tmax, MathF.Max(t0, t1));

        return tmax >= MathF.Max(tmin, 0f) && tmin <= maxDistance;
    }

    /// <summary>
    /// The same slab test against <see cref="BrushBounds"/>-style flat storage: six floats from
    /// <paramref name="at"/>. Identical maths to <see cref="RayHitsBox"/>, reading contiguous memory.
    /// </summary>
    internal static bool RayHitsFlatBox(Vector3 origin, Vector3 invDir, float[] b, int at, float maxDistance)
    {
        float t0 = (b[at] - origin.X) * invDir.X;
        float t1 = (b[at + 3] - origin.X) * invDir.X;
        float tmin = MathF.Min(t0, t1);
        float tmax = MathF.Max(t0, t1);

        t0 = (b[at + 1] - origin.Y) * invDir.Y;
        t1 = (b[at + 4] - origin.Y) * invDir.Y;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1));
        tmax = MathF.Min(tmax, MathF.Max(t0, t1));

        t0 = (b[at + 2] - origin.Z) * invDir.Z;
        t1 = (b[at + 5] - origin.Z) * invDir.Z;
        tmin = MathF.Max(tmin, MathF.Min(t0, t1));
        tmax = MathF.Min(tmax, MathF.Max(t0, t1));

        return tmax >= MathF.Max(tmin, 0f) && tmin <= maxDistance;
    }

    /// <summary>True when a sphere of <paramref name="radius"/> about <paramref name="p"/> overlaps the box.</summary>
    internal static bool SphereHitsBox(Vector3 p, float radius, Vector3 mins, Vector3 maxs)
    {
        Vector3 closest = Vector3.Clamp(p, mins, maxs);
        return (closest - p).LengthSquared() <= radius * radius;
    }
}

/// <summary>
/// Ray picking against truth geometry, and geometry-to-geometry snapping (design doc §11.4).
///
/// Picking runs against the brush planes, NOT the render mesh: the render mesh is derived and may be
/// spatially re-chunked, merged or (later) replaced by amplified decoration, so picking it would select
/// something that has no stable identity to edit. Going straight to the truth also means a pick can resolve
/// sub-objects — the face you hit, or the edge/vertex near where you hit it — which is what the vertex and
/// edge drags need.
/// </summary>
public static class VmapPicking
{
    /// <summary>
    /// Pick the nearest brush along a ray, resolving to a vertex, an edge or a face.
    ///
    /// Resolution is by SCREEN-SPACE proximity, approximated here as a world-space radius that the caller
    /// scales with distance (<paramref name="grabRadius"/>): a vertex within the radius of the hit point wins,
    /// then an edge, then the face itself. Without that, distant vertices would be unclickably small while
    /// nearby ones would swallow the whole face.
    /// </summary>
    /// <param name="doc">Document to pick against.</param>
    /// <param name="origin">Ray origin (world/Quake space).</param>
    /// <param name="direction">Ray direction; need not be normalized.</param>
    /// <param name="mode">Which sub-object kinds may be returned.</param>
    /// <param name="grabRadius">World-space radius within which a vertex/edge beats the face.</param>
    /// <param name="maxDistance">Ignore hits beyond this range.</param>
    public static VmapPickResult Pick(
        VmapDocument doc,
        Vector3 origin,
        Vector3 direction,
        VmapSelectionKind mode = VmapSelectionKind.Face,
        float grabRadius = 8f,
        float maxDistance = 8192f)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var scratch = new VmapPickIndex();
        scratch.EnsureBuilt(doc, 0);
        return Pick(scratch, origin, direction, mode, grabRadius, maxDistance);
    }

    /// <summary>
    /// The accelerated pick: same result as the document overload, but resolved against a prebuilt
    /// <see cref="VmapPickIndex"/> so a per-frame crosshair query does not rebuild every brush's geometry.
    /// </summary>
    /// <param name="entityFilter">
    /// Which point entities this pick may return. Null accepts them all. The caller supplies the rule rather
    /// than the pick inventing one, because the two rules that exist are the caller's business: the Light tool
    /// takes only lights (backlog T2), and no tool should return an entity whose box is hidden behind a wall
    /// (backlog T1) — clicking something you cannot see is the same bug as drawing it.
    /// </param>
    public static VmapPickResult Pick(
        VmapPickIndex index,
        Vector3 origin,
        Vector3 direction,
        VmapSelectionKind mode = VmapSelectionKind.Face,
        float grabRadius = 8f,
        float maxDistance = 8192f,
        Func<VmapEntity, bool>? entityFilter = null)
    {
        ArgumentNullException.ThrowIfNull(index);

        float dirLen = direction.Length();
        if (dirLen < 1e-6f)
            return VmapPickResult.Miss;
        Vector3 dir = direction / dirLen;

        // A zero component would divide by zero; the large reciprocal makes that axis' slab effectively
        // infinite, which is the correct answer for a ray parallel to it.
        Vector3 invDir = new(
            MathF.Abs(dir.X) > 1e-8f ? 1f / dir.X : 1e30f,
            MathF.Abs(dir.Y) > 1e-8f ? 1f / dir.Y : 1e30f,
            MathF.Abs(dir.Z) > 1e-8f ? 1f / dir.Z : 1e30f);

        VmapPickResult best = VmapPickResult.Miss;
        float bestDistance = maxDistance;

        foreach (VmapPickIndex.Entry entry in index.Entries)
        {
            // Broadphase: skip the brush entirely unless the ray reaches its box within the current best hit.
            if (!VmapPickIndex.RayHitsBox(origin, invDir, entry.Mins, entry.Maxs, bestDistance))
                continue;

            VmapBrush brush = entry.Brush;
            Vector3[][] windings = entry.Windings;
            for (int f = 0; f < windings.Length; f++)
            {
                Vector3[] w = windings[f];
                if (w.Length < 3)
                    continue;

                VmapFace pf = brush.Faces[f];

                // Skip faces that draw NOTHING. The brush-level tool filter is not enough on its own: an
                // ordinary wall brush still carries caulk/noshader/nodraw sides, and those invisible planes sit
                // in front of the visible surface, so the crosshair grabs a face that is not there. Only offer
                // what the mapper can actually see (unless tool geometry was explicitly asked for).
                if (!index.IncludesToolBrushes && !IsPickableFace(pf))
                    continue;

                VmapPlane plane = pf.Plane;

                // Only front faces: a ray leaving the camera should hit the outside of a solid, and skipping
                // back faces stops a click from selecting the far wall of the room you are standing in.
                float denom = Vector3.Dot(plane.Normal, dir);
                if (denom >= -1e-6f)
                    continue;

                float t = (plane.Dist - Vector3.Dot(plane.Normal, origin)) / denom;
                if (t < 0f || t >= bestDistance)
                    continue;

                Vector3 point = origin + dir * t;
                if (!PointInPolygon(w, plane.Normal, point))
                    continue;

                bestDistance = t;
                best = new VmapPickResult
                {
                    Hit = true,
                    Point = point,
                    Distance = t,
                    Normal = plane.Normal,
                    Selection = Resolve(brush, f, w, point, mode, grabRadius),
                };
            }
        }

        // --- patches: no plane set to intersect, so test the cached tessellation. Always picked as WHOLE
        // objects regardless of the sub-object tool: a curved surface has no faces or corners to grab.
        foreach (VmapPickIndex.PatchEntry pe in index.Patches)
        {
            if (!VmapPickIndex.RayHitsBox(origin, invDir, pe.Mins, pe.Maxs, bestDistance))
                continue;

            for (int i = 0; i + 2 < pe.Triangles.Length; i += 3)
            {
                if (!RayTriangle(origin, dir, pe.Triangles[i], pe.Triangles[i + 1], pe.Triangles[i + 2],
                        out float t, out Vector3 n) || t < 0f || t >= bestDistance)
                    continue;

                bestDistance = t;
                best = new VmapPickResult
                {
                    Hit = true,
                    Point = origin + dir * t,
                    Distance = t,
                    Normal = n,
                    Selection = VmapSelection.OfPatch(pe.Patch.Id),
                };
            }
        }

        // --- point entities: their descriptor box, and ONLY when the entity tool asked for them.
        //
        // Gated on the mode rather than always tested, because an entity box is a volume floating in the air
        // around a pickup: with it always live, aiming at the floor under a health pack would grab the pack
        // instead of the floor, and the geometry tools would become unusable anywhere a map is furnished.
        if (mode == VmapSelectionKind.Entity)
        {
            foreach (VmapPickIndex.EntityEntry ee in index.Entities)
            {
                if (entityFilter is not null && !entityFilter(ee.Entity))
                    continue;
                if (!RayBoxEntry(origin, dir, invDir, ee.Mins, ee.Maxs, bestDistance, out float t, out Vector3 n))
                    continue;

                bestDistance = t;
                best = new VmapPickResult
                {
                    Hit = true,
                    Point = origin + dir * t,
                    Distance = t,
                    Normal = n,
                    Selection = VmapSelection.OfEntity(ee.Entity.Id),
                };
            }
        }

        return best;
    }

    /// <summary>
    /// Is there solid, VISIBLE geometry between two points? (backlog T1.)
    ///
    /// A BOOLEAN test, not a pick: the first blocker ends it, nothing is sorted, and nothing is allocated. That
    /// is what makes it affordable to run many times a frame, which is what the entity overlay needs — a level
    /// holds hundreds of entities and each one's box has to answer "am I behind a wall" independently.
    ///
    /// "Visible" means the same thing it means to <see cref="Pick"/>: a face that draws nothing (caulk, nodraw,
    /// the common/* shader families) does not block. Without that, every entity inside a caulk shell — which is
    /// most of them, since a Xonotic map's architecture is wrapped in one — would be hidden behind a wall the
    /// editor does not draw.
    ///
    /// Entities are deliberately not tested. One entity's box must never hide another's, or a rack of pickups
    /// would show only its nearest item.
    /// </summary>
    /// <param name="index">Prebuilt geometry cache to test against.</param>
    /// <param name="from">Eye position (world/Quake space).</param>
    /// <param name="to">The point being tested for visibility.</param>
    /// <param name="bias">
    /// Shorten the ray by this much, so a surface the target is sitting ON does not occlude it. An entity box
    /// resting against a floor is the common case, not the exception.
    /// </param>
    public static bool IsOccluded(VmapPickIndex index, Vector3 from, Vector3 to, float bias = 1f)
    {
        ArgumentNullException.ThrowIfNull(index);

        Vector3 seg = to - from;
        float length = seg.Length();
        float reach = length - MathF.Max(0f, bias);
        if (reach <= 0f)
            return false;      // the target is at (or inside) the eye: nothing can be between them

        Vector3 dir = seg / length;
        Vector3 invDir = new(
            MathF.Abs(dir.X) > 1e-8f ? 1f / dir.X : 1e30f,
            MathF.Abs(dir.Y) > 1e-8f ? 1f / dir.Y : 1e30f,
            MathF.Abs(dir.Z) > 1e-8f ? 1f / dir.Z : 1e30f);

        // Narrow to the solids the segment could reach before touching a single winding. A flat slab test over
        // every brush measured 850 µs a ray on catharsis (75,537 of them); the cell walk is what makes a sweep
        // of one ray per entity a per-frame cost rather than a stall.
        float[] brushBounds = index.BrushBounds;
        IReadOnlyList<VmapPickIndex.Entry> entries = index.Entries;
        List<int> brushCandidates = index.BrushesAlong(from, from + dir * reach);
        for (int c = 0; c < brushCandidates.Count; c++)
        {
            int e = brushCandidates[c];
            if (!VmapPickIndex.RayHitsFlatBox(from, invDir, brushBounds, e * 6, reach))
                continue;

            VmapPickIndex.Entry entry = entries[e];
            VmapBrush brush = entry.Brush;
            Vector3[][] windings = entry.Windings;
            for (int f = 0; f < windings.Length; f++)
            {
                Vector3[] w = windings[f];
                if (w.Length < 3)
                    continue;

                VmapFace pf = brush.Faces[f];
                if (!index.IncludesToolBrushes && !IsPickableFace(pf))
                    continue;

                VmapPlane plane = pf.Plane;

                // Front faces only, exactly as Pick does: a ray leaving the eye meets the outside of a solid
                // first, and testing back faces too would make a room's far wall occlude everything in it.
                float denom = Vector3.Dot(plane.Normal, dir);
                if (denom >= -1e-6f)
                    continue;

                float t = (plane.Dist - Vector3.Dot(plane.Normal, from)) / denom;
                if (t < 0f || t >= reach)
                    continue;

                if (PointInPolygon(w, plane.Normal, from + dir * t))
                    return true;
            }
        }

        float[] patchBounds = index.PatchBounds;
        IReadOnlyList<VmapPickIndex.PatchEntry> patches = index.Patches;
        List<int> patchCandidates = index.PatchesAlong(from, from + dir * reach);
        for (int c = 0; c < patchCandidates.Count; c++)
        {
            int p = patchCandidates[c];
            if (!VmapPickIndex.RayHitsFlatBox(from, invDir, patchBounds, p * 6, reach))
                continue;

            Vector3[] tris = patches[p].Triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
                if (RayTriangle(from, dir, tris[i], tris[i + 1], tris[i + 2], out float t, out _)
                    && t >= 0f && t < reach)
                    return true;
        }

        return false;
    }

    /// <summary>
    /// Ray against an axis-aligned box, returning the ENTRY distance and the face normal that was crossed.
    ///
    /// The slab test used by the broadphase only answers yes/no within a budget; picking needs the distance to
    /// sort against other candidates and the normal so the hover highlight faces the right way. An eye already
    /// inside the box counts as a hit at zero, which is what lets you grab an entity you are standing in.
    /// </summary>
    internal static bool RayBoxEntry(
        Vector3 origin, Vector3 dir, Vector3 invDir, Vector3 mins, Vector3 maxs, float maxDistance,
        out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.UnitZ;

        float tmin = 0f, tmax = maxDistance;
        int axis = 2;
        float sign = 1f;

        for (int i = 0; i < 3; i++)
        {
            float o = i == 0 ? origin.X : i == 1 ? origin.Y : origin.Z;
            float inv = i == 0 ? invDir.X : i == 1 ? invDir.Y : invDir.Z;
            float lo = i == 0 ? mins.X : i == 1 ? mins.Y : mins.Z;
            float hi = i == 0 ? maxs.X : i == 1 ? maxs.Y : maxs.Z;

            float t1 = (lo - o) * inv;
            float t2 = (hi - o) * inv;
            float near = MathF.Min(t1, t2);
            float far = MathF.Max(t1, t2);

            if (near > tmin)
            {
                tmin = near;
                axis = i;
                sign = t1 > t2 ? 1f : -1f;   // which slab face was crossed
            }
            tmax = MathF.Min(tmax, far);
            if (tmin > tmax)
                return false;
        }

        distance = tmin;
        normal = axis switch
        {
            0 => new Vector3(sign, 0f, 0f),
            1 => new Vector3(0f, sign, 0f),
            _ => new Vector3(0f, 0f, sign),
        };
        return true;
    }

    /// <summary>
    /// Möller–Trumbore ray/triangle intersection. Two-sided: a patch is a surface, not a solid, and a mapper
    /// may well be looking at its back.
    /// </summary>
    public static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c,
        out float t, out Vector3 normal)
    {
        t = 0f;
        normal = Vector3.Zero;

        Vector3 e1 = b - a, e2 = c - a;
        Vector3 h = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-8f)
            return false;   // parallel

        float inv = 1f / det;
        Vector3 s = origin - a;
        float u = Vector3.Dot(s, h) * inv;
        if (u < 0f || u > 1f)
            return false;

        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(dir, q) * inv;
        if (v < 0f || u + v > 1f)
            return false;

        t = Vector3.Dot(e2, q) * inv;
        if (t <= 0f)
            return false;

        Vector3 nn = Vector3.Cross(e1, e2);
        float len = nn.Length();
        if (len < 1e-8f)
            return false;
        nn /= len;
        // Face the normal back toward the ray so a face push on a patch reads sensibly from either side.
        normal = Vector3.Dot(nn, dir) > 0f ? -nn : nn;
        return true;
    }

    /// <summary>Choose the sub-object the hit point is closest to, honouring the requested mode.</summary>
    private static VmapSelection Resolve(
        VmapBrush brush, int faceIndex, Vector3[] winding, Vector3 point, VmapSelectionKind mode, float grabRadius)
    {
        if (mode == VmapSelectionKind.Brush)
            return VmapSelection.OfBrush(brush.Id);

        if (mode is VmapSelectionKind.Vertex or VmapSelectionKind.Edge)
        {
            // Vertices beat edges: a corner is harder to hit than an edge, so it gets first refusal.
            if (mode == VmapSelectionKind.Vertex)
            {
                Vector3 nearestVertex = winding[0];
                float bestSq = float.MaxValue;
                foreach (Vector3 v in winding)
                {
                    float d = (v - point).LengthSquared();
                    if (d < bestSq)
                    {
                        bestSq = d;
                        nearestVertex = v;
                    }
                }
                if (bestSq <= grabRadius * grabRadius)
                    return VmapSelection.OfVertex(brush.Id, nearestVertex);
            }
            else
            {
                Vector3 ea = Vector3.Zero, eb = Vector3.Zero;
                float bestDist = float.MaxValue;
                for (int i = 0; i < winding.Length; i++)
                {
                    Vector3 a = winding[i], b = winding[(i + 1) % winding.Length];
                    float d = DistancePointSegment(point, a, b);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        ea = a;
                        eb = b;
                    }
                }
                if (bestDist <= grabRadius)
                    return VmapSelection.OfEdge(brush.Id, ea, eb);
            }
        }

        return VmapSelection.OfFace(brush.Id, faceIndex);
    }

    /// <summary>Q3SURFACEFLAG_NODRAW — the face exists for collision/vis only.</summary>
    private const int SurfaceNoDraw = 0x0080;

    /// <summary>
    /// A face is pickable when it actually renders: not NODRAW, and not one of the invisible shader families
    /// (<c>common/*</c>, <c>noshader</c>, empty). This is what keeps the crosshair on the wall you can see
    /// instead of the caulk plane in front of it.
    /// </summary>
    private static bool IsPickableFace(VmapFace f)
        => (f.SurfaceFlags & SurfaceNoDraw) == 0 && !VmapBrush.IsToolMaterial(f.Material);

    /// <summary>Is a coplanar point inside a convex polygon? (Consistent sign of the edge cross products.)</summary>
    internal static bool PointInPolygon(Vector3[] w, Vector3 normal, Vector3 p)
    {
        for (int i = 0; i < w.Length; i++)
        {
            Vector3 a = w[i], b = w[(i + 1) % w.Length];
            // Winding is counter-clockwise seen from outside, so an interior point stays left of every edge.
            if (Vector3.Dot(Vector3.Cross(b - a, p - a), normal) < -VmapWinding.OnEpsilon)
                return false;
        }
        return true;
    }

    /// <summary>Shortest distance from a point to a line segment.</summary>
    public static float DistancePointSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-12f)
            return (p - a).Length();
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / lenSq, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }

    // =============================================================================================
    //  Geometry-to-geometry snapping (E4)
    // =============================================================================================

    /// <summary>A resolved snap: where the dragged point should land, and what it snapped to (for the HUD).</summary>
    public readonly struct SnapResult
    {
        public bool Snapped { get; init; }
        public Vector3 Position { get; init; }
        public VmapSelectionKind TargetKind { get; init; }
        public int TargetBrushId { get; init; }

        /// <summary>The snapped-to feature's endpoints (one point for a vertex, two for an edge) — for drawing the hint.</summary>
        public IReadOnlyList<Vector3> TargetPoints { get; init; }
    }

    /// <summary>
    /// Pull a dragged position onto nearby geometry: vertex first, then edge, then face plane.
    ///
    /// This is what makes brushes actually meet instead of nearly meeting — a hairline gap between two walls
    /// leaks light and shows a seam, and it is invisible at editing zoom. Geometry snapping wins inside its
    /// radius; outside it, the caller falls back to the grid, so the two never fight.
    /// </summary>
    /// <param name="doc">Document to snap against.</param>
    /// <param name="position">The dragged position.</param>
    /// <param name="radius">Snap threshold in world units.</param>
    /// <param name="excludeBrushIds">Brushes being dragged — never snap geometry to itself.</param>
    public static SnapResult SnapToGeometry(
        VmapDocument doc, Vector3 position, float radius, IReadOnlyList<int>? excludeBrushIds = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var scratch = new VmapPickIndex();
        scratch.EnsureBuilt(doc, 0);
        return SnapToGeometry(scratch, position, radius, excludeBrushIds);
    }

    /// <summary>Accelerated snap against a prebuilt index (see the document overload for the policy).</summary>
    public static SnapResult SnapToGeometry(
        VmapPickIndex index, Vector3 position, float radius, IReadOnlyList<int>? excludeBrushIds = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (radius <= 0f)
            return default;

        float radiusSq = radius * radius;
        SnapResult best = default;
        float bestDist = float.MaxValue;

        foreach (VmapPickIndex.Entry entry in index.Entries)
        {
            if (excludeBrushIds is not null && excludeBrushIds.Contains(entry.Brush.Id))
                continue;
            if (!VmapPickIndex.SphereHitsBox(position, radius, entry.Mins, entry.Maxs))
                continue;

            foreach (Vector3[] w in entry.Windings)
            {
                if (w.Length < 3)
                    continue;

                // --- vertex ---
                foreach (Vector3 v in w)
                {
                    float d = (v - position).LengthSquared();
                    if (d <= radiusSq && d < bestDist)
                    {
                        bestDist = d;
                        best = new SnapResult
                        {
                            Snapped = true,
                            Position = v,
                            TargetKind = VmapSelectionKind.Vertex,
                            TargetBrushId = entry.Brush.Id,
                            TargetPoints = new[] { v },
                        };
                    }
                }
            }
        }

        // A vertex snap always wins — it is the most specific target, and settling for the edge through it
        // would leave the dragged point sliding along that edge instead of landing on the corner.
        if (best.Snapped)
            return best;

        foreach (VmapPickIndex.Entry entry in index.Entries)
        {
            if (excludeBrushIds is not null && excludeBrushIds.Contains(entry.Brush.Id))
                continue;
            if (!VmapPickIndex.SphereHitsBox(position, radius, entry.Mins, entry.Maxs))
                continue;

            foreach (Vector3[] w in entry.Windings)
            {
                if (w.Length < 3)
                    continue;

                for (int i = 0; i < w.Length; i++)
                {
                    Vector3 a = w[i], b = w[(i + 1) % w.Length];
                    float d = DistancePointSegment(position, a, b);
                    if (d > radius || d * d >= bestDist)
                        continue;

                    Vector3 ab = b - a;
                    float t = Math.Clamp(Vector3.Dot(position - a, ab) / MathF.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
                    bestDist = d * d;
                    best = new SnapResult
                    {
                        Snapped = true,
                        Position = a + ab * t,
                        TargetKind = VmapSelectionKind.Edge,
                        TargetBrushId = entry.Brush.Id,
                        TargetPoints = new[] { a, b },
                    };
                }
            }
        }

        // Patch control points and their spans, at the same two tiers. A patch is geometry a mapper aligns to
        // exactly as much as a brush is — a wall meeting the lip of a curved platform is the same job — and
        // leaving them out meant the snap silently stopped working near the parts of a map most likely to
        // need it.
        foreach (VmapPickIndex.PatchEntry patch in index.Patches)
        {
            if (!VmapPickIndex.SphereHitsBox(position, radius, patch.Mins, patch.Maxs))
                continue;

            foreach (Vector3 v in patch.Patch.Controls)
            {
                float d = (v - position).LengthSquared();
                if (d > radiusSq || d >= bestDist)
                    continue;
                bestDist = d;
                best = new SnapResult
                {
                    Snapped = true,
                    Position = v,
                    TargetKind = VmapSelectionKind.Vertex,
                    TargetPoints = new[] { v },
                };
            }
        }

        if (best.Snapped)
            return best;

        // --- face plane ---
        //
        // Last, and deliberately so: it is the least specific target, and it is what makes a brush land FLUSH
        // against a wall it is nowhere near a corner of. Only inside the face's winding — an infinite plane
        // would drag things onto surfaces they are not over, from across the map.
        foreach (VmapPickIndex.Entry entry in index.Entries)
        {
            if (excludeBrushIds is not null && excludeBrushIds.Contains(entry.Brush.Id))
                continue;
            if (!VmapPickIndex.SphereHitsBox(position, radius, entry.Mins, entry.Maxs))
                continue;

            for (int fi = 0; fi < entry.Windings.Length && fi < entry.Brush.Faces.Count; fi++)
            {
                Vector3[] w = entry.Windings[fi];
                if (w.Length < 3)
                    continue;

                VmapPlane plane = entry.Brush.Faces[fi].Plane;
                float signed = Vector3.Dot(plane.Normal, position) - plane.Dist;
                float d = MathF.Abs(signed);
                if (d > radius || d * d >= bestDist)
                    continue;

                Vector3 onPlane = position - plane.Normal * signed;
                if (!PointInPolygon(w, plane.Normal, onPlane))
                    continue;

                bestDist = d * d;
                best = new SnapResult
                {
                    Snapped = true,
                    Position = onPlane,
                    TargetKind = VmapSelectionKind.Face,
                    TargetBrushId = entry.Brush.Id,
                    TargetPoints = w,
                };
            }
        }

        return best;
    }

    /// <summary>
    /// Resolve a dragged position through the full snapping policy: geometry snap if anything is in range,
    /// otherwise the grid. This is the single place the precedence rule lives, so the 3D view, the ortho views
    /// and any scripted edit all behave identically.
    /// </summary>
    public static Vector3 ResolveDragPosition(
        VmapDocument doc,
        Vector3 position,
        float gridSize,
        float snapRadius,
        IReadOnlyList<int>? excludeBrushIds,
        out SnapResult snap)
    {
        snap = snapRadius > 0f
            ? SnapToGeometry(doc, position, snapRadius, excludeBrushIds)
            : default;

        return snap.Snapped ? snap.Position : VmapEdit.SnapToGrid(position, gridSize);
    }

    /// <summary>Accelerated drag resolution against a prebuilt index (same precedence rule).</summary>
    public static Vector3 ResolveDragPosition(
        VmapPickIndex index,
        Vector3 position,
        float gridSize,
        float snapRadius,
        IReadOnlyList<int>? excludeBrushIds,
        out SnapResult snap)
    {
        snap = snapRadius > 0f
            ? SnapToGeometry(index, position, snapRadius, excludeBrushIds)
            : default;

        return snap.Snapped ? snap.Position : VmapEdit.SnapToGrid(position, gridSize);
    }
}

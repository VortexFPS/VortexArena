using System.Numerics;

namespace XonoticGodot.Formats.Vmap;

/// <summary>
/// A uniform cell grid over a set of world-space boxes, answering "which of these could a SEGMENT cross".
///
/// Built because measurement said the obvious thing was not enough. The entity-occlusion sweep (backlog T1) is
/// the editor's first query that fires many rays per frame rather than one, and a flat slab test over every
/// solid measured 79 µs a ray on stormkeep and 850 µs on catharsis (75,537 brushes) — 1.9 and 20.4 ms of frame
/// at a budget of 24 rays. A crosshair pick can afford a linear pass because it happens once when the camera
/// moves; a sweep cannot. With the grid the same rays cost 6.5 and 8.3 µs, i.e. 0.16 and 0.20 ms a frame, and
/// the figure stops tracking the map's brush count (<c>EditorOcclusionBench</c>, Debug build).
///
/// The layout is CSR: one offset per cell into one shared item array, built in two passes, so a query is index
/// arithmetic over two int arrays and nothing is allocated or dereferenced until a candidate survives. Boxes
/// that span an unreasonable number of cells go into an <c>oversized</c> list tested on every query instead —
/// bucketing a brush that covers half the map would fill the grid with it and defeat the point.
///
/// Cell size is chosen, not configured: start fine and coarsen until both the cell count and the total number
/// of insertions fit a budget. A dense arena and a sprawling outdoor map therefore get different grids without
/// either being tuned by hand.
///
/// Deliberately NOT wired into <see cref="VmapPicking.Pick"/> or <c>SnapToGeometry</c>. Both keep a running
/// best and compare with a strict <c>&lt;</c>, so which candidate they see FIRST decides ties; visiting in
/// cell order rather than document order would quietly change results those queries have pinned, to speed up
/// something that already runs at most once per camera movement. A boolean occlusion test has no such
/// ordering, which is what makes it safe to accelerate.
/// </summary>
internal sealed class VmapCellGrid
{
    /// <summary>Ceiling on cells, so a sprawling map coarsens rather than allocating an offset per square metre.</summary>
    private const int MaxCells = 1 << 18;

    /// <summary>A box spanning more cells than this is tested on every query instead of being bucketed.</summary>
    private const int MaxCellsPerItem = 128;

    /// <summary>Finest cell tried. Below this the offset array dominates on any real map.</summary>
    private const float FinestCell = 64f;

    private float _cell = FinestCell;
    private Vector3 _origin;
    private int _nx, _ny, _nz;

    // CSR: _start has CellCount + 1 entries; cell c owns _items[_start[c].._start[c + 1]).
    private int[] _start = Array.Empty<int>();
    private int[] _items = Array.Empty<int>();
    private int[] _oversized = Array.Empty<int>();

    // Per-item visit stamp, so a box bucketed into six cells along the segment is only reported once.
    private int[] _stamp = Array.Empty<int>();
    private int _tick;

    /// <summary>True once <see cref="Build"/> has run for the current geometry.</summary>
    public bool Built { get; private set; }

    /// <summary>Cell edge length actually chosen, in world units (for diagnostics).</summary>
    public float CellSize => _cell;

    /// <summary>How many boxes were too large to bucket (for diagnostics).</summary>
    public int OversizedCount => _oversized.Length;

    /// <summary>Discard the structure; the next query rebuilds it.</summary>
    public void Reset() => Built = false;

    /// <summary>
    /// (Re)build over <paramref name="count"/> boxes held as six floats each: min x/y/z then max x/y/z.
    /// </summary>
    public void Build(float[] bounds, int count)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        Built = true;
        _nx = _ny = _nz = 0;
        _start = Array.Empty<int>();
        _items = Array.Empty<int>();
        _oversized = Array.Empty<int>();
        if (count <= 0)
            return;

        if (_stamp.Length < count)
        {
            _stamp = new int[count];
            _tick = 0;
        }

        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            int b = i * 6;
            lo = Vector3.Min(lo, new Vector3(bounds[b], bounds[b + 1], bounds[b + 2]));
            hi = Vector3.Max(hi, new Vector3(bounds[b + 3], bounds[b + 4], bounds[b + 5]));
        }
        if (!IsFinite(lo) || !IsFinite(hi))
        {
            // Degenerate bounds (a NaN plane somewhere upstream). Fall back to "everything is a candidate":
            // slower, never wrong, and it keeps a broken brush from taking the editor's picking with it.
            _oversized = AllIndices(count);
            return;
        }

        _origin = lo;
        Vector3 span = Vector3.Max(hi - lo, Vector3.One);

        // Total insertions matter as much as the cell count: a fine grid over large boxes copies each of them
        // into hundreds of cells, which costs more memory than the linear scan it replaces.
        long insertionBudget = 4L * count + 65536L;

        _cell = FinestCell;
        bool settled = false;
        for (int attempt = 0; attempt < 40 && !settled; attempt++)
        {
            long nx = (long)(span.X / _cell) + 1;
            long ny = (long)(span.Y / _cell) + 1;
            long nz = (long)(span.Z / _cell) + 1;
            if (nx * ny * nz > MaxCells)
            {
                _cell *= 2f;
                continue;
            }

            _nx = (int)nx;
            _ny = (int)ny;
            _nz = (int)nz;

            long insertions = 0;
            for (int i = 0; i < count && insertions <= insertionBudget; i++)
            {
                long cells = CellSpan(bounds, i);
                if (cells <= MaxCellsPerItem)
                    insertions += cells;
            }
            if (insertions > insertionBudget)
            {
                _nx = _ny = _nz = 0;
                _cell *= 2f;
                continue;
            }
            settled = true;
        }

        if (!settled)
        {
            _nx = _ny = _nz = 0;
            _oversized = AllIndices(count);
            return;
        }

        int cellCount = _nx * _ny * _nz;
        _start = new int[cellCount + 1];
        var oversized = new List<int>();

        // Pass one: count per cell (offset by one, so the prefix sum lands directly in _start).
        for (int i = 0; i < count; i++)
        {
            if (CellSpan(bounds, i) > MaxCellsPerItem)
            {
                oversized.Add(i);
                continue;
            }
            Range(bounds, i, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        _start[CellOf(x, y, z) + 1]++;
        }

        for (int c = 0; c < cellCount; c++)
            _start[c + 1] += _start[c];

        // Pass two: fill, walking a cursor copy so _start stays the offset table.
        _items = new int[_start[cellCount]];
        int[] cursor = (int[])_start.Clone();
        for (int i = 0; i < count; i++)
        {
            if (CellSpan(bounds, i) > MaxCellsPerItem)
                continue;
            Range(bounds, i, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        _items[cursor[CellOf(x, y, z)]++] = i;
        }

        _oversized = oversized.ToArray();
    }

    /// <summary>
    /// Fill <paramref name="into"/> with every box the segment could cross, each exactly once. A superset: the
    /// caller still runs its own exact test, this only removes the boxes that cannot possibly matter.
    /// </summary>
    public void Segment(Vector3 from, Vector3 to, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        // Advance the stamp first, so the oversized items below are deduplicated against the walk as well.
        if (++_tick == int.MaxValue)
        {
            Array.Clear(_stamp);
            _tick = 1;
        }

        foreach (int i in _oversized)
        {
            _stamp[i] = _tick;
            into.Add(i);
        }

        if (_nx == 0)
            return;

        Vector3 d = to - from;
        float length = d.Length();
        if (length < 1e-6f)
        {
            AddCell(CellClamp(from.X, _origin.X, _nx), CellClamp(from.Y, _origin.Y, _ny),
                CellClamp(from.Z, _origin.Z, _nz), into);
            return;
        }
        Vector3 dir = d / length;

        // Clip to the grid's own box first: the walk has to start inside, and a segment that misses the grid
        // entirely has no candidates at all.
        float t0 = 0f, t1 = length;
        Vector3 gridMax = _origin + new Vector3(_nx * _cell, _ny * _cell, _nz * _cell);
        for (int axis = 0; axis < 3; axis++)
        {
            float o = Component(from, axis), dd = Component(dir, axis);
            float lo = Component(_origin, axis), hi = Component(gridMax, axis);
            if (MathF.Abs(dd) < 1e-9f)
            {
                if (o < lo || o > hi)
                    return;
                continue;
            }
            float ta = (lo - o) / dd, tb = (hi - o) / dd;
            if (ta > tb)
                (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta);
            t1 = MathF.Min(t1, tb);
            if (t0 > t1)
                return;
        }

        Vector3 entry = from + dir * t0;
        int cx = CellClamp(entry.X, _origin.X, _nx);
        int cy = CellClamp(entry.Y, _origin.Y, _ny);
        int cz = CellClamp(entry.Z, _origin.Z, _nz);

        // Amanatides & Woo: step to whichever axis' next cell boundary is nearest, repeatedly.
        int sx = StepOf(dir.X), sy = StepOf(dir.Y), sz = StepOf(dir.Z);
        float tMaxX = NextBoundary(entry.X, dir.X, _origin.X, cx, t0);
        float tMaxY = NextBoundary(entry.Y, dir.Y, _origin.Y, cy, t0);
        float tMaxZ = NextBoundary(entry.Z, dir.Z, _origin.Z, cz, t0);
        float tDeltaX = sx == 0 ? float.MaxValue : _cell / MathF.Abs(dir.X);
        float tDeltaY = sy == 0 ? float.MaxValue : _cell / MathF.Abs(dir.Y);
        float tDeltaZ = sz == 0 ? float.MaxValue : _cell / MathF.Abs(dir.Z);

        int guard = 2 * (_nx + _ny + _nz) + 8;
        while (guard-- > 0)
        {
            AddCell(cx, cy, cz, into);

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                if (tMaxX > t1) return;
                cx += sx;
                if (sx == 0 || cx < 0 || cx >= _nx) return;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                if (tMaxY > t1) return;
                cy += sy;
                if (sy == 0 || cy < 0 || cy >= _ny) return;
                tMaxY += tDeltaY;
            }
            else
            {
                if (tMaxZ > t1) return;
                cz += sz;
                if (sz == 0 || cz < 0 || cz >= _nz) return;
                tMaxZ += tDeltaZ;
            }
        }
    }

    private void AddCell(int x, int y, int z, List<int> into)
    {
        int c = CellOf(x, y, z);
        int end = _start[c + 1];
        for (int k = _start[c]; k < end; k++)
        {
            int item = _items[k];
            if (_stamp[item] == _tick)
                continue;
            _stamp[item] = _tick;
            into.Add(item);
        }
    }

    private int CellOf(int x, int y, int z) => (z * _ny + y) * _nx + x;

    private int CellClamp(float world, float origin, int n)
        => Math.Clamp((int)MathF.Floor((world - origin) / _cell), 0, n - 1);

    private static int StepOf(float d) => d > 1e-9f ? 1 : d < -1e-9f ? -1 : 0;

    /// <summary>Distance along the ray at which it leaves cell <paramref name="cell"/> on this axis.</summary>
    private float NextBoundary(float p, float d, float origin, int cell, float t0)
    {
        if (MathF.Abs(d) < 1e-9f)
            return float.MaxValue;
        float boundary = origin + (cell + (d > 0f ? 1 : 0)) * _cell;
        // Float slop at a cell edge can put the boundary marginally behind the entry point; clamping forward
        // costs one redundant cell at worst and keeps the walk from stalling.
        return MathF.Max(t0, t0 + (boundary - p) / d);
    }

    private long CellSpan(float[] b, int i)
    {
        Range(b, i, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
        return (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
    }

    private void Range(float[] b, int i,
        out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
    {
        int at = i * 6;
        x0 = CellClamp(b[at], _origin.X, _nx);
        y0 = CellClamp(b[at + 1], _origin.Y, _ny);
        z0 = CellClamp(b[at + 2], _origin.Z, _nz);
        x1 = CellClamp(b[at + 3], _origin.X, _nx);
        y1 = CellClamp(b[at + 4], _origin.Y, _ny);
        z1 = CellClamp(b[at + 5], _origin.Z, _nz);
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static int[] AllIndices(int count)
    {
        var all = new int[count];
        for (int i = 0; i < count; i++)
            all[i] = i;
        return all;
    }
}

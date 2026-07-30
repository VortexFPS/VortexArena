using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Engine.Collision;

/// <summary>
/// The DYNAMIC-ENTITY broadphase — the C# successor to Darkplaces' <c>SV_AreaGrid</c>
/// (Base/darkplaces/world.c <c>World_LinkEdict_AreaGrid</c> / <c>World_EntitiesInBox</c>). The static-brush
/// broadphase already has its own grid (<see cref="CollisionWorld.Query"/>); this is the missing half for the
/// MOVING entities (players, projectiles, items, triggers) that traces, splash-radius queries, and the
/// trigger-touch pass would otherwise scan as one flat O(n) list every call (planning/PERFORMANCE_REPORT.md §4/D1).
///
/// <para><b>Representation.</b> A uniform XY hash-grid: each entity is linked into every <see cref="CellSize"/>-unit
/// cell its world AABB (XY) overlaps (Z is not partitioned — the caller's precise box test handles it, exactly
/// as DP grids only X+Y). An entity whose footprint spans more than <see cref="MaxCellsPerLink"/> cells (a
/// map-spanning trigger volume) goes into the <c>_oversized</c> list that every query always includes — DP's
/// <c>areagrid_outside</c>. A hash-grid (fixed cell SIZE, cells created on demand) rather than DP's fixed-COUNT
/// grid over precomputed world bounds means no world-size plumbing and it copes with any coordinate.</para>
///
/// <para><b>Correctness.</b> This is a CONSERVATIVE broadphase: a query returns every entity whose AABB could
/// overlap the box (a superset), and the callers keep their existing precise per-entity test — so the result is
/// byte-identical to the old linear scan (validated by <c>EntityAreaGridDifferentialTests</c>), only with far
/// fewer candidates. Entities are binned from their LIVE <c>Origin+Mins/Maxs</c> at link time, so correctness
/// rests on the same contract DP relies on: <see cref="EntityService.LinkEdict"/> is called after every move
/// (the physics does, and every <c>setorigin/setsize/setmodel</c> builtin does).</para>
///
/// <para><b>Thread-safety.</b> Single-threaded per world, like the rest of the sim. Queries are re-entrant-safe
/// (a trace fired from inside a trigger-touch callback): each <see cref="EntitiesInBox"/> gather is atomic and
/// completes before any callback runs, and dedup uses a per-query id stamped into a slot-indexed tag array, so a
/// nested query bumping the id can't corrupt an outer gather that has already finished filling its list.</para>
/// </summary>
public sealed class EntityAreaGrid
{
    /// <summary>Cell edge in world units. ~256 gives ≈32 cells across an 8000-unit map — DP's areagrid scale.</summary>
    public const float CellSize = 256f;
    private const float InvCellSize = 1f / CellSize;

    /// <summary>Footprint cap (in cells) before an entity is treated as <c>areagrid_outside</c> (always queried).
    /// Bounds the per-link work: a 2048×2048-unit volume (8×8 cells) still grids; larger goes oversized.</summary>
    private const int MaxCellsPerLink = 64;

    private readonly Dictionary<long, List<Entity>> _cells = new();   // packed (cx,cy) -> entities in that cell
    private readonly List<Entity> _oversized = new();                 // huge/world-spanning entities (always candidates)
    private readonly Dictionary<Entity, Span> _links = new();         // each entity's current linked footprint (for unlink)

    // Dedup state: a per-slot "last query id" stamped during a gather. Indexed by Slot(Entity.Index) (dense),
    // grown on link. _queryId increments per EntitiesInBox call; an entity already stamped with the current id
    // is a dup.
    private int[] _queryTag = new int[64];
    private int _queryId;

    /// <summary>Tag-array slot for an entity index. Client edicts live in ClientManager's NEGATIVE index space
    /// (so they never collide with engine-table slots); indexing the tag array directly by Entity.Index made the
    /// bounds check skip them, so a player spanning N grid cells was returned N times per query — every splash
    /// blast then damaged/pushed the player once per overlapped cell (2-4x). Interleave the two index spaces:
    /// index &gt;= 0 → even slots, index &lt; 0 → odd slots, so both dedup.</summary>
    private static int Slot(int index) => index >= 0 ? index << 1 : (~index << 1) | 1;

    /// <summary>One entity's linked footprint: the inclusive cell range, or the oversized flag.</summary>
    private readonly record struct Span(int X0, int Y0, int X1, int Y1, bool Oversized);

    private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;
    private static int Coord(float v) => (int)MathF.Floor(v * InvCellSize);

    /// <summary>Link (or re-link) an entity from its current <c>Origin+Mins/Maxs</c> — DP <c>SV_LinkEdict</c>'s
    /// areagrid half. Idempotent + cheap when the footprint is unchanged (the common case for a static or
    /// slow-moving entity): only a footprint that crosses a cell boundary touches the cell lists.</summary>
    public void Link(Entity e)
    {
        if (e.IsFreed)
        {
            Unlink(e);
            return;
        }
        EnsureTag(Slot(e.Index));

        Vector3 lo = e.Origin + e.Mins;
        Vector3 hi = e.Origin + e.Maxs;
        int x0 = Coord(lo.X), y0 = Coord(lo.Y);
        int x1 = Coord(hi.X), y1 = Coord(hi.Y);
        bool oversized = (long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerLink;
        var span = new Span(x0, y0, x1, y1, oversized);

        if (_links.TryGetValue(e, out Span old))
        {
            if (old.Equals(span))
                return;                  // footprint unchanged — the cells already hold this entity
            RemoveFromCells(e, old);
        }
        AddToCells(e, span);
        _links[e] = span;
    }

    /// <summary>Remove an entity from the grid (DP unlink on free / solid-off).</summary>
    public void Unlink(Entity e)
    {
        if (_links.TryGetValue(e, out Span s))
        {
            RemoveFromCells(e, s);
            _links.Remove(e);
        }
    }

    /// <summary>
    /// Gather every entity whose footprint overlaps [<paramref name="mins"/>,<paramref name="maxs"/>] (XY) into
    /// <paramref name="results"/> (cleared first), de-duplicated — DP <c>World_EntitiesInBox</c>. A conservative
    /// superset; the caller applies the precise box/distance test. <paramref name="results"/> must not alias a
    /// list currently being iterated by an outer query on the same grid.
    /// </summary>
    public void EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
    {
        results.Clear();
        int qid = NextQueryId();

        // areagrid_outside: oversized entities are candidates for every query.
        for (int i = 0; i < _oversized.Count; i++)
            TryAdd(_oversized[i], qid, results);

        int x0 = Coord(mins.X), y0 = Coord(mins.Y);
        int x1 = Coord(maxs.X), y1 = Coord(maxs.Y);
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out List<Entity>? list))
                    continue;
                for (int i = 0; i < list.Count; i++)
                    TryAdd(list[i], qid, results);
            }
    }

    /// <summary>Drop every link (a fresh map / world reset). The <see cref="EntityService"/> is normally rebuilt
    /// per map so this is rarely needed, but it keeps the grid reusable.</summary>
    public void Clear()
    {
        _cells.Clear();
        _oversized.Clear();
        _links.Clear();
        Array.Clear(_queryTag, 0, _queryTag.Length);
        _queryId = 0;
    }

    // -----------------------------------------------------------------------------------------------

    private void TryAdd(Entity e, int qid, List<Entity> results)
    {
        if (e.IsFreed)
            return;
        int slot = Slot(e.Index);
        if ((uint)slot < (uint)_queryTag.Length)
        {
            if (_queryTag[slot] == qid)
                return;                  // already gathered this query
            _queryTag[slot] = qid;
        }
        results.Add(e);
    }

    private void AddToCells(Entity e, Span s)
    {
        if (s.Oversized)
        {
            _oversized.Add(e);
            return;
        }
        for (int cx = s.X0; cx <= s.X1; cx++)
            for (int cy = s.Y0; cy <= s.Y1; cy++)
            {
                long k = Key(cx, cy);
                if (!_cells.TryGetValue(k, out List<Entity>? list))
                    _cells[k] = list = new List<Entity>();
                list.Add(e);
            }
    }

    private void RemoveFromCells(Entity e, Span s)
    {
        if (s.Oversized)
        {
            _oversized.Remove(e);
            return;
        }
        for (int cx = s.X0; cx <= s.X1; cx++)
            for (int cy = s.Y0; cy <= s.Y1; cy++)
                if (_cells.TryGetValue(Key(cx, cy), out List<Entity>? list))
                    list.Remove(e);      // O(cell occupancy) — small in practice
    }

    private int NextQueryId()
    {
        if (++_queryId == int.MaxValue)
        {
            // Wrapped (after 2^31 queries): reset tags so a stale id can't masquerade as the new one.
            Array.Clear(_queryTag, 0, _queryTag.Length);
            _queryId = 1;
        }
        return _queryId;
    }

    private void EnsureTag(int index)
    {
        if (index < _queryTag.Length)
            return;
        int n = _queryTag.Length;
        while (n <= index)
            n *= 2;
        Array.Resize(ref _queryTag, n);
    }
}

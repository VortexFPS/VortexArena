using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Builds a <see cref="NavField"/> by probing a <see cref="CollisionWorld"/> on a 32 qu lattice.
///
/// <para><b>Cost.</b> Each column costs two point traces and two PointContents per span found, so roughly
/// 0.06 ms per span at the rates in <c>TracePerfBench</c>. A 125 x 125 column map averaging three spans is
/// around 2.8 s single-threaded. That is too slow to sit on the map-load thread — parity finding D1 records
/// a 1 s waypoint freeze as a shipped bug — so <see cref="BakeParallel"/> fans out across cores and the
/// caller is expected to run it off the sim thread with the classic steer live until it lands.</para>
///
/// <para><b>Thread safety.</b> Neither <see cref="TraceService"/> nor <see cref="CollisionWorld"/> is safe
/// to share across threads. The trace service keeps per-instance scratch (<c>_candidates</c>,
/// <c>_boxCache</c>); the collision world keeps an epoch-dedup array (<c>_mark</c>/<c>_markNumber</c>) that
/// every broadphase query stamps. Sharing the world silently loses candidate brushes rather than crashing:
/// the first version of this bake did share it and found 995 spans where the serial bake found 1058, a
/// 6% hole in the map that nothing would have reported.
///
/// <para>So each worker gets both its own trace service AND its own world, built over the SAME
/// <see cref="Brush"/> objects — brushes are immutable during a trace, so only the grid and the mark array
/// need duplicating. That costs one grid build per worker (milliseconds) against a bake measured in
/// seconds, and it needs no change to the engine's hot path.</para>
/// </summary>
public static class NavFieldBaker
{
    /// <summary>Progress callback: (columns done, columns total). Called from the bake threads, so keep it cheap.</summary>
    public delegate void Progress(int done, int total);

    /// <summary>
    /// Bake on the calling thread. Deterministic, and the reference implementation
    /// <see cref="BakeParallel"/> is checked against.
    /// </summary>
    public static NavField Bake(CollisionWorld world, string mapName, ulong geometryHash,
        IReadOnlyList<Entity>? mapEntities = null, Progress? progress = null)
        => BakeInternal(world, mapName, geometryHash, mapEntities, progress, maxDegreeOfParallelism: 1);

    /// <summary>
    /// Bake across <paramref name="threads"/> workers (default: all but two cores, matching the repo's other
    /// background work). Produces the same field as <see cref="Bake"/>; columns are independent.
    /// </summary>
    public static NavField BakeParallel(CollisionWorld world, string mapName, ulong geometryHash,
        IReadOnlyList<Entity>? mapEntities = null, Progress? progress = null, int threads = 0)
    {
        if (threads <= 0) threads = Math.Max(1, Environment.ProcessorCount - 2);
        return BakeInternal(world, mapName, geometryHash, mapEntities, progress, threads);
    }

    private static NavField BakeInternal(CollisionWorld world, string mapName, ulong geometryHash,
        IReadOnlyList<Entity>? mapEntities, Progress? progress, int maxDegreeOfParallelism)
    {
        Vector3 lo = world.WorldMins, hi = world.WorldMaxs;

        // Pad by one cell so a surface flush with the world bounds still gets a column centred on it.
        int cell = NavField.CellSize;
        var origin = new Vector3(
            MathF.Floor(lo.X / cell) * cell - cell,
            MathF.Floor(lo.Y / cell) * cell - cell,
            0f);
        int width = (int)MathF.Ceiling((hi.X - origin.X) / cell) + 2;
        int height = (int)MathF.Ceiling((hi.Y - origin.Y) / cell) + 2;

        // A degenerate world (an empty CollisionWorld, or one brush) still has to produce a usable object
        // rather than throwing: the headless tests build exactly that, and a server that fails to boot
        // because a map has no geometry is worse than a server whose bots fall back to the classic steer.
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            return Empty(mapName, geometryHash, origin, Math.Clamp(width, 1, 4096), Math.Clamp(height, 1, 4096));

        float zTop = hi.Z + 64f;
        float zBottom = lo.Z - 64f;

        // Hazard volumes (trigger_hurt and friends) are entities, not brush contents, so the content bits
        // for them come from an AABB list gathered once rather than from a trace per probe.
        HazardVolume[] hazards = GatherHazards(mapEntities);
        MoverVolume[] movers = GatherMovers(mapEntities);

        var columns = new FloorSpan[width * height][];
        int done = 0;

        void BakeRange(int yStart, int yEnd, TraceService trace)
        {
            Span<FloorSpan> scratch = stackalloc FloorSpan[NavField.MaxSpansPerColumn];
            for (int y = yStart; y < yEnd; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float wx = origin.X + x * cell;
                    float wy = origin.Y + y * cell;
                    int n = BakeColumn(trace, wx, wy, zTop, zBottom, hazards, movers, scratch);
                    if (n > 0)
                    {
                        var arr = new FloorSpan[n];
                        for (int i = 0; i < n; i++) arr[i] = scratch[i];
                        columns[y * width + x] = arr;
                    }
                }
                if (progress is not null)
                {
                    int d = System.Threading.Interlocked.Increment(ref done);
                    if ((d & 15) == 0) progress(d * width, width * height);
                }
            }
        }

        if (maxDegreeOfParallelism <= 1)
        {
            BakeRange(0, height, new TraceService(world));
        }
        else
        {
            int workers = Math.Min(maxDegreeOfParallelism, height);
            int rowsPer = (height + workers - 1) / workers;
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
            {
                int y0 = w * rowsPer;
                int y1 = Math.Min(height, y0 + rowsPer);
                if (y0 >= y1) return;
                // A private world AND a private service per worker: see the thread-safety note above.
                BakeRange(y0, y1, new TraceService(PrivateView(world)));
            });
        }

        // Flatten. Doing this after the fan-out keeps the workers writing to disjoint slots with no shared
        // counter, at the cost of one jagged array that dies immediately after.
        var columnStart = new int[width * height];
        var columnCount = new byte[width * height];
        int total = 0;
        for (int c = 0; c < columns.Length; c++)
            if (columns[c] is { } a) total += a.Length;

        var spans = new FloorSpan[total];
        int write = 0;
        for (int c = 0; c < columns.Length; c++)
        {
            columnStart[c] = write;
            FloorSpan[]? a = columns[c];
            if (a is null) { columnCount[c] = 0; continue; }
            Array.Copy(a, 0, spans, write, a.Length);
            columnCount[c] = (byte)a.Length;
            write += a.Length;
        }

        var field = new NavField(mapName, geometryHash, origin, width, height, columnStart, columnCount, spans);
        LinkNeighbours(field, spans, columnStart, columnCount, width, height);
        return field;
    }

    /// <summary>
    /// A collision world one worker can query alone: the same brush objects, its own broadphase grid and
    /// its own dedup marks. Brushes are read-only during a trace, so this shares the geometry and
    /// duplicates only the mutable query state.
    /// </summary>
    private static CollisionWorld PrivateView(CollisionWorld shared)
    {
        var view = new CollisionWorld();
        view.AddBrushes(shared.Brushes);
        view.BuildGrid();
        return view;
    }

    private static NavField Empty(string mapName, ulong hash, Vector3 origin, int w, int h)
        => new(mapName, hash, origin, w, h, new int[w * h], new byte[w * h], Array.Empty<FloorSpan>());

    /// <summary>
    /// Probe one column top-down, recording every standable surface. Returns the count written to
    /// <paramref name="dest"/>, highest floor first.
    /// </summary>
    private static int BakeColumn(TraceService trace, float wx, float wy, float zTop, float zBottom,
        HazardVolume[] hazards, MoverVolume[] movers, Span<FloorSpan> dest)
    {
        int n = 0;
        float z = zTop;
        var zero = Vector3.Zero;

        while (n < dest.Length && z > zBottom)
        {
            var from = new Vector3(wx, wy, z);
            var to = new Vector3(wx, wy, zBottom);
            TraceResult down = trace.Trace(from, zero, zero, to, MoveFilter.WorldOnly, null);

            // Fraction 1 means the probe fell all the way through: nothing below, stop.
            if (down.Fraction >= 1f) break;

            float floorZ = down.EndPos.Z;
            // A start-solid probe means we began inside a brush; skip past it rather than recording a floor
            // at the point we happened to start.
            if (down.StartSolid)
            {
                z = DescendThroughSolid(trace, wx, wy, z, zBottom);
                continue;
            }

            // Ceiling: how much headroom above this floor.
            var upFrom = new Vector3(wx, wy, floorZ + 2f);
            var upTo = new Vector3(wx, wy, floorZ + 2f + NavField.MaxClearance);
            TraceResult up = trace.Trace(upFrom, zero, zero, upTo, MoveFilter.WorldOnly, null);
            float ceilZ = up.Fraction >= 1f ? floorZ + NavField.MaxClearance : up.EndPos.Z;

            float slope = down.PlaneNormal.Z;
            int clearance = (int)(ceilZ - floorZ);

            var content = NavContent.None;

            // Sky underfoot is the void: falling here is a death, not a drop.
            if ((down.DpHitContents & SuperContents.Sky) != 0)
                content |= NavContent.Void;

            // Liquid a hands-breadth above the floor decides the medium. One PointContents, not a trace.
            int pc = trace.PointContents(new Vector3(wx, wy, floorZ + 8f));
            if ((pc & SuperContents.Lava) != 0) content |= NavContent.Lethal;
            else if ((pc & SuperContents.Slime) != 0) content |= NavContent.Harmful;
            else if ((pc & SuperContents.Water) != 0) content |= NavContent.Water;

            // Entity hazards: trigger_hurt volumes overlapping the standing space.
            for (int i = 0; i < hazards.Length; i++)
            {
                if (!hazards[i].Contains(wx, wy, floorZ + 8f)) continue;
                content |= hazards[i].Lethal ? NavContent.Lethal : NavContent.Harmful;
            }

            for (int i = 0; i < movers.Length; i++)
            {
                if (!movers[i].Contains(wx, wy, floorZ)) continue;
                content |= NavContent.Mover;
                break;
            }

            bool standable = clearance >= NavField.MinStandClearance
                             && slope >= NavField.MinWalkableSlope
                             && (content & (NavContent.Lethal | NavContent.Void)) == 0;
            if (standable) content |= NavContent.Standable;

            // Record even a non-standable surface. "There is a lava floor 200 qu ahead" is exactly the kind
            // of thing the policy has to see, and dropping it would make the hazard invisible.
            dest[n++] = new FloorSpan
            {
                FloorZ = ClampShort(floorZ),
                CeilZ = ClampShort(ceilZ),
                SlopeDot = (byte)Math.Clamp((int)(slope * 255f), 0, 255),
                Content = (byte)content,
                JumpReachMask = 0,
            };

            // Descend below this surface to look for another one under it (a walkway over a pit, a lower
            // corridor). Step through the solid rather than tracing from just under the floor, which would
            // start inside the brush and return start-solid forever.
            z = DescendThroughSolid(trace, wx, wy, floorZ - 1f, zBottom);
        }

        return n;
    }

    /// <summary>
    /// Walk downward from <paramref name="z"/> until the point is out of solid, so the next downward trace
    /// starts in open space. Steps in 16 qu so a thick floor costs a handful of PointContents (0.0015 ms
    /// each) rather than a trace.
    /// </summary>
    private static float DescendThroughSolid(TraceService trace, float wx, float wy, float z, float zBottom)
    {
        const float step = 16f;
        int guard = 0;
        while (z > zBottom && guard++ < 4096)
        {
            int pc = trace.PointContents(new Vector3(wx, wy, z));
            if ((pc & SuperContents.Solid) == 0) return z;
            z -= step;
        }
        return zBottom - 1f;
    }

    private static short ClampShort(float v)
        => (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);

    /// <summary>
    /// Fill each span's <see cref="FloorSpan.JumpReachMask"/>: for each of the eight neighbours, is the
    /// neighbour's nearest span reachable by a walk (within step height) or a single jump (within jump
    /// apex)? Pure array work over the finished field, no traces.
    ///
    /// <para>This is what the training-time geodesic potential walks, and what stops the course generator
    /// handing the policy an A/B pair with no route between them.</para>
    /// </summary>
    private static void LinkNeighbours(NavField field, FloorSpan[] spans, int[] columnStart, byte[] columnCount,
        int width, int height)
    {
        // (dx,dy) in the same order NavField's probe ring uses: +X first, counter-clockwise.
        ReadOnlySpan<int> dxs = stackalloc int[] { 1, 1, 0, -1, -1, -1, 0, 1 };
        ReadOnlySpan<int> dys = stackalloc int[] { 0, 1, 1, 1, 0, -1, -1, -1 };

        float stepUp = BotNavigation.StepHeight;
        float jumpUp = BotNavigation.JumpStepHeight;
        const float maxDrop = 400f; // a fall a bot survives at stock gravity and health

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int c = y * width + x;
                int start = columnStart[c], count = columnCount[c];
                for (int s = 0; s < count; s++)
                {
                    ref FloorSpan span = ref spans[start + s];
                    if (((NavContent)span.Content & NavContent.Standable) == 0) continue;

                    int mask = 0;
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = x + dxs[d], ny = y + dys[d];
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int nc = ny * width + nx;
                        int nStart = columnStart[nc], nCount = columnCount[nc];
                        for (int t = 0; t < nCount; t++)
                        {
                            FloorSpan other = spans[nStart + t];
                            if (((NavContent)other.Content & NavContent.Standable) == 0) continue;
                            float dz = other.FloorZ - span.FloorZ;
                            bool reachable = dz <= jumpUp && dz >= -maxDrop
                                             && (dz <= stepUp || other.Clearance >= NavField.MinStandClearance);
                            if (reachable) { mask |= 1 << d; break; }
                        }
                    }
                    span.JumpReachMask = (byte)mask;
                }
            }
        }
        _ = field;
    }

    // ---- entity-derived hazard / mover volumes ----

    private readonly struct HazardVolume
    {
        public readonly Vector3 Mins, Maxs;
        public readonly bool Lethal;
        public HazardVolume(Vector3 mins, Vector3 maxs, bool lethal) { Mins = mins; Maxs = maxs; Lethal = lethal; }
        public bool Contains(float x, float y, float z)
            => x >= Mins.X && x <= Maxs.X && y >= Mins.Y && y <= Maxs.Y && z >= Mins.Z && z <= Maxs.Z;
    }

    private readonly struct MoverVolume
    {
        public readonly Vector3 Mins, Maxs;
        public MoverVolume(Vector3 mins, Vector3 maxs) { Mins = mins; Maxs = maxs; }
        public bool Contains(float x, float y, float z)
            => x >= Mins.X && x <= Maxs.X && y >= Mins.Y && y <= Maxs.Y && z >= Mins.Z - 64f && z <= Maxs.Z + 64f;
    }

    private static HazardVolume[] GatherHazards(IReadOnlyList<Entity>? entities)
    {
        if (entities is null) return Array.Empty<HazardVolume>();
        var list = new List<HazardVolume>();
        for (int i = 0; i < entities.Count; i++)
        {
            Entity e = entities[i];
            if (e.IsFreed) continue;
            string cn = e.ClassName;
            if (!cn.StartsWith("trigger_hurt", StringComparison.Ordinal)) continue;
            Vector3 mins = e.AbsMin, maxs = e.AbsMax;
            if (mins == maxs) { mins = e.Origin + e.Mins; maxs = e.Origin + e.Maxs; }
            if (mins == maxs) continue;
            // QC: dmg >= 1000 (or the -1 "instant death" convention) is the pit-killer configuration.
            bool lethal = e.Dmg >= 1000f || e.Dmg < 0f;
            list.Add(new HazardVolume(mins, maxs, lethal));
        }
        return list.ToArray();
    }

    private static MoverVolume[] GatherMovers(IReadOnlyList<Entity>? entities)
    {
        if (entities is null) return Array.Empty<MoverVolume>();
        var list = new List<MoverVolume>();
        for (int i = 0; i < entities.Count; i++)
        {
            Entity e = entities[i];
            if (e.IsFreed) continue;
            string cn = e.ClassName;
            if (!(cn.StartsWith("func_plat", StringComparison.Ordinal)
                  || cn.StartsWith("func_door", StringComparison.Ordinal)
                  || cn.StartsWith("func_train", StringComparison.Ordinal)
                  || cn.StartsWith("func_bobbing", StringComparison.Ordinal)))
                continue;
            Vector3 mins = e.AbsMin, maxs = e.AbsMax;
            if (mins == maxs) { mins = e.Origin + e.Mins; maxs = e.Origin + e.Maxs; }
            if (mins == maxs) continue;
            list.Add(new MoverVolume(mins, maxs));
        }
        return list.ToArray();
    }
}

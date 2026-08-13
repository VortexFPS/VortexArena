using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Gameplay;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// Geodesic distance from every standable span of a <see cref="NavField"/> to a goal, computed by Dijkstra
/// over the field's own <see cref="FloorSpan.JumpReachMask"/> adjacency.
///
/// <para><b>What it is for.</b> The training reward shapes on progress toward the goal, and straight-line
/// distance is the wrong potential: it rewards a bot for pressing into the wall between it and the target.
/// Graph distance rewards actual progress along a route that exists.</para>
///
/// <para><b>Why shaping on it does not bias the answer.</b> The reward uses it as a potential
/// (<c>gamma * phi(s') - phi(s)</c>), and potential-based shaping is policy-invariant: it changes how fast
/// the policy learns, not which policy is optimal. So the network stays free to find a rocket-jump shortcut
/// this graph does not contain, which is the entire point of the feature. Handing the distance to the policy
/// as an OBSERVATION would be the biasing move, and is why the network never sees it.</para>
///
/// <para>Training-time only, plus the eval harness. Never built on a live server.</para>
/// </summary>
public sealed class NavDistanceField
{
    private readonly NavField _field;

    /// <summary>Distance in Quake units per span, indexed the same way the field's flat span array is.</summary>
    private readonly float[] _dist;

    /// <summary>Flat index of the first span of each column, and how many.</summary>
    private readonly int[] _start;
    private readonly byte[] _count;

    /// <summary>Unreachable spans carry this. Finite so arithmetic on it stays well-behaved.</summary>
    public const float Unreachable = 1e9f;

    /// <summary>
    /// How far a link may reach, in lattice cells. Ten cells is 320 qu, about what a running jump clears at
    /// <c>sv_maxspeed</c>. Past this the router should be finding a way around, not a way across.
    /// </summary>
    public const int MaxJumpCells = 10;

    /// <summary>Spans that Dijkstra actually reached. Low coverage means the goal is walled off.</summary>
    public int ReachedSpans { get; private set; }

    private NavDistanceField(NavField field, float[] dist, int[] start, byte[] count)
    {
        _field = field;
        _dist = dist;
        _start = start;
        _count = count;
    }

    /// <summary>The distance buffer, so a probing caller can hand it back to the next <see cref="Build"/>.</summary>
    public float[] Buffer => _dist;

    /// <summary>
    /// A copy that owns its distance buffer, so the shared probe buffer can keep being reused.
    ///
    /// <para>The span index is shared deliberately -- it is per-field and immutable, and sharing it is the
    /// point of caching it.</para>
    /// </summary>
    public NavDistanceField DetachCopy()
    {
        var copy = new NavDistanceField(_field, (float[])_dist.Clone(), _start, _count)
        {
            ReachedSpans = ReachedSpans,
        };
        return copy;
    }

    /// <summary>
    /// Build the distance field for one goal position. Cost is O(spans log spans); on a 15,000-column map
    /// that is a few milliseconds, so the training env rebuilds it per episode.
    /// </summary>
    /// <summary>
    /// The span index for a field: where each column's spans start, and how many. Goal-independent, so it
    /// is built once per field and shared by every distance field over it.
    ///
    /// <para>This used to be rebuilt inside every <see cref="Build"/>, which meant an int[cells] and a
    /// byte[cells] allocation plus a full sweep of the lattice per call. Both arrays are large enough to
    /// land on the Large Object Heap, which .NET does not compact by default, and Build is called far more
    /// often than it looks: <see cref="MapCourseSource.NextRouteOn"/> retries up to eight times per agent
    /// when a drawn target turns out unreachable, and routes are now per agent. At 20 agents that is up to
    /// 160 calls per episode. Measured: a 120-episode bench peaked at 5.6 GB resident, about 46 MB per
    /// episode, and every training run this session ended in an out-of-memory kill because of it.</para>
    /// </summary>
    private sealed class SpanIndex
    {
        public readonly int[] Start;
        public readonly byte[] Count;
        public readonly int Total;

        public SpanIndex(NavField field)
        {
            int w = field.Width, h = field.Height;
            Start = new int[w * h];
            Count = new byte[w * h];
            int total = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int c = y * w + x;
                    Start[c] = total;
                    int n = field.Column(x, y).Length;
                    Count[c] = (byte)n;
                    total += n;
                }
            }
            Total = total;
        }
    }

    // Keyed weakly so a field that falls out of the map cache does not pin its index here.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NavField, SpanIndex> _indexCache = new();

    /// <summary>
    /// One-way connections the 32 qu lattice cannot express: jump pads, teleporters and warpzones.
    /// </summary>
    /// <remarks>
    /// The lattice links neighbours by walking and jumping, which covers everything a bot can do under its
    /// own power and nothing a map does for it. So a pit whose only exit is a launch, or a room reached only
    /// through a teleporter, was simply absent from the graph -- the flood reported the goal unreachable and
    /// the bot was standing somewhere it could in fact leave.
    ///
    /// <para>Measured before this existed: of agents that read "goal unreachable" while standing on the
    /// ground, 86.6% recovered within the same episode and 35.7% went on to ARRIVE. Reachability was not a
    /// fact about the map, it was a fact about what the graph happened to model.</para>
    ///
    /// <para>Stored per field rather than baked into it, because the links come from entities and the field
    /// is disk-cached: putting them in the field would mean a format version and a cache invalidation for
    /// data that is free to rebuild.</para>
    /// </remarks>
    private sealed class WarpLinks
    {
        /// <summary>Destination span -> the spans that can reach it by entering a pad or teleporter, and at what cost.</summary>
        public readonly Dictionary<int, List<(int Source, float Cost)>> IntoDest = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NavField, WarpLinks> _warpCache = new();

    /// <summary>
    /// What entering a pad or teleporter costs the router, over and above its flight time.
    /// </summary>
    /// <remarks>
    /// Not free. A pad has to be walked onto and committed to, and a router that treats one as a zero-cost
    /// edge will thread every route through the nearest one. 128 qu is four cells: cheap against the
    /// hundreds or thousands of qu a launch actually covers, dear enough that a short walk still wins.
    /// </remarks>
    private const float WarpBaseCost = 128f;

    /// <summary>Flight time converted to route cost at roughly running speed, so a slow arc reads as further.</summary>
    private const float WarpSpeedEquivalent = 400f;

    /// <summary>Vertical slack when deciding which spans sit inside a trigger volume, in qu.</summary>
    private const float WarpVolumeSlack = 64f;

    /// <summary>
    /// Teach every distance field over <paramref name="field"/> about the map's pads, teleporters and warpzones.
    /// </summary>
    /// <remarks>
    /// Idempotent and cheap; call it once per prepared map, after <see cref="MapFeatures.Build"/>. A field
    /// with no registration behaves exactly as before, which is what keeps generated courses and the tests
    /// unaffected.
    /// </remarks>
    /// <summary>Set false to build no warp links at all, for A/B against the pre-warp router.</summary>
    public static bool WarpsEnabled = true;

    /// <returns>How many destination spans and how many riding spans were linked. Zero means the map has no
    /// usable pads or teleporters -- worth logging, because a feature that silently never fires looks
    /// exactly like one that works.</returns>
    public static (int Dests, int Sources) RegisterWarps(NavField field, MapFeatures features)
    {
        // Once per field. The links come from the map's entities, which do not change while it is resident,
        // and the training env resets on the same field every episode.
        if (_warpCache.TryGetValue(field, out WarpLinks? existing))
            return (existing.IntoDest.Count, existing.IntoDest.Sum(kv => kv.Value.Count));
        if (!WarpsEnabled)
        {
            _warpCache.Add(field, new WarpLinks());
            return (0, 0);
        }

        SpanIndex index = _indexCache.GetValue(field, static f => new SpanIndex(f));
        var links = new WarpLinks();

        foreach (MapFeature f in features.All)
        {
            if (f.Kind is not (MapFeatureKind.JumpPad or MapFeatureKind.Teleporter or MapFeatureKind.Warpzone)) continue;
            // An unresolved exit means the map names a destination that is not in the entity table. Skipping
            // is right: inventing an edge to nowhere is worse than the missing edge it replaces.
            if (f.Exit == Vector3.Zero) continue;
            if (!field.TryCell(f.Exit, out int ex, out int ey)) continue;

            int dest = SpanUnderStatic(field, index, ex, ey, f.Exit.Z);
            if (dest < 0) continue;

            float cost = WarpBaseCost + f.TransitTime * WarpSpeedEquivalent;
            if (!links.IntoDest.TryGetValue(dest, out var sources))
                links.IntoDest[dest] = sources = new List<(int, float)>();

            // Every standable span inside the trigger volume can take this ride.
            for (float wy = f.Mins.Y; wy <= f.Maxs.Y + NavField.CellSize; wy += NavField.CellSize)
            {
                for (float wx = f.Mins.X; wx <= f.Maxs.X + NavField.CellSize; wx += NavField.CellSize)
                {
                    if (!field.TryCell(new Vector3(wx, wy, f.Centre.Z), out int cx, out int cy)) continue;
                    ReadOnlySpan<FloorSpan> col = field.Column(cx, cy);
                    for (int t = 0; t < col.Length; t++)
                    {
                        if (((NavContent)col[t].Content & NavContent.Standable) == 0) continue;
                        if (col[t].FloorZ < f.Mins.Z - WarpVolumeSlack) continue;
                        if (col[t].FloorZ > f.Maxs.Z + WarpVolumeSlack) continue;
                        int src = index.Start[cy * field.Width + cx] + t;
                        if (src != dest) sources.Add((src, cost));
                    }
                }
            }
            if (sources.Count == 0) links.IntoDest.Remove(dest);
        }

        // Stored even when empty, so the early-out above works and a map with no pads is not rescanned
        // every episode.
        _warpCache.Add(field, links);
        return (links.IntoDest.Count, links.IntoDest.Sum(kv => kv.Value.Count));
    }

    /// <summary>Mirrors <see cref="SpanIndexUnder"/> for callers that have the index but no built field yet.</summary>
    private static int SpanUnderStatic(NavField field, SpanIndex index, int cx, int cy, float z)
    {
        ReadOnlySpan<FloorSpan> col = field.Column(cx, cy);
        float feet = FeetResolution ? z - OriginAboveFloor : z;
        float probe = feet + BotNavigation.StepHeight;
        for (int i = 0; i < col.Length; i++)
            if (col[i].FloorZ <= probe) return index.Start[cy * field.Width + cx] + i;
        return -1;
    }

    /// <param name="reuse">
    /// A distance buffer to write into instead of allocating one, or null.
    ///
    /// <para>For callers that flood repeatedly and keep only some of the results. MapCourseSource retries
    /// up to eight times per agent when a drawn target turns out unreachable, and every rejected attempt
    /// used to allocate and discard a float[spans] on the Large Object Heap. The accepted field must not
    /// share a buffer with the next probe, so a caller that keeps one calls <see cref="DetachCopy"/>.</para>
    /// </param>
    public static NavDistanceField Build(NavField field, Vector3 goal, float[]? reuse = null)
    {
        int w = field.Width, h = field.Height;
        SpanIndex index = _indexCache.GetValue(field, static f => new SpanIndex(f));
        int[] start = index.Start;
        byte[] count = index.Count;
        int total = index.Total;

        // The one genuinely per-goal allocation left. Still large, but one array per call rather than
        // three, and no lattice sweep -- and a caller probing for a reachable target can hand the same
        // buffer back on every attempt so only the accepted route allocates.
        float[] dist = reuse is not null && reuse.Length >= total ? reuse : new float[total];
        for (int i = 0; i < total; i++) dist[i] = Unreachable;

        var result = new NavDistanceField(field, dist, start, count);
        if (total == 0) return result;

        // Seed: the span under the goal.
        if (!field.TryCell(goal, out int gx, out int gy)) return result;
        int seed = result.SpanIndexUnder(gx, gy, goal.Z);
        if (seed < 0) return result;

        dist[seed] = 0f;

        // A binary heap keyed on distance. The span count is in the tens of thousands, so a bucket queue
        // would also work; the heap keeps the code honest about non-uniform edge costs (a jump up costs more
        // than a step across, and that is what stops the policy being shaped toward impossible shortcuts).
        var heap = new PriorityQueue<int, float>();
        heap.Enqueue(seed, 0f);

        ReadOnlySpan<int> dxs = stackalloc int[] { 1, 1, 0, -1, -1, -1, 0, 1 };
        ReadOnlySpan<int> dys = stackalloc int[] { 0, 1, 1, 1, 0, -1, -1, -1 };

        _warpCache.TryGetValue(field, out WarpLinks? warps);
        // Null it out when the map has none, so the inner loop skips a dictionary lookup per popped span.
        if (warps is not null && warps.IntoDest.Count == 0) warps = null;

        int reached = 0;
        while (heap.TryDequeue(out int cur, out float d))
        {
            if (d > dist[cur]) continue;   // a stale heap entry
            reached++;

            // Pads and teleporters, relaxed in the direction the flood actually runs.
            //
            // This flood is seeded at the GOAL and spreads outward, so dist[i] is the cost from i to the
            // goal. A pad is one-way: riding it takes you from its trigger volume to its exit, never back.
            // So the edge to relax is the reverse of the ride -- popping the EXIT span at cost d makes every
            // span inside the trigger volume cost d + ride. Relaxing it the other way round would tell the
            // router it can reach the pad by standing on the landing pad, which is exactly backwards.
            if (warps is not null && warps.IntoDest.TryGetValue(cur, out var riders))
            {
                foreach ((int src, float cost) in riders)
                {
                    float nd = d + cost;
                    if (nd < dist[src])
                    {
                        dist[src] = nd;
                        heap.Enqueue(src, nd);
                    }
                }
            }

            result.Locate(cur, out int cx, out int cy, out int slot);
            FloorSpan span = field.Column(cx, cy)[slot];

            for (int dir = 0; dir < 8; dir++)
            {
                // Walk OUTWARD along this direction until a standable span turns up, out to a running
                // jump's reach. Whatever is skipped on the way is a gap, and crossing a gap is what a jump
                // is for.
                //
                // The first version looked only at the ADJACENT cell, and gated on FloorSpan.JumpReachMask,
                // which is built the same way. A 32 qu lattice linked at 32 qu cannot represent a jump:
                // stage 3 puts its platforms 96 to 296 qu apart, so the field saw every course as a chain
                // of islands and HALF of them had no route from spawn to target at all. The reward's
                // geodesic term silently fell back to a padded straight line on those, and stage 6 rejected
                // good A/B pairs on real maps for the same reason.
                for (int step = 1; step <= MaxJumpCells; step++)
                {
                    int nx = cx + dxs[dir] * step, ny = cy + dys[dir] * step;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) break;

                    ReadOnlySpan<FloorSpan> col = field.Column(nx, ny);
                    int hit = -1;
                    float hitDz = 0f;
                    for (int t = 0; t < col.Length; t++)
                    {
                        if (((NavContent)col[t].Content & NavContent.Standable) == 0) continue;
                        float dz = col[t].FloorZ - span.FloorZ;
                        // A jump gains height only up to its apex, and the further it has to carry the
                        // flatter it is; a drop is bounded by what a fall survives.
                        float rise = step == 1
                            ? BotNavigation.JumpStepHeight
                            : BotNavigation.JumpStepHeight * (1f - (step - 1) / (float)MaxJumpCells);
                        if (dz > rise || dz < -400f) continue;
                        hit = t;
                        hitDz = dz;
                        break;
                    }
                    if (hit < 0) continue;   // nothing standable here; keep looking further out

                    float diag = (dir % 2 == 1) ? 1.41421f : 1f;
                    float horiz = NavField.CellSize * diag * step;
                    float climb = hitDz > BotNavigation.StepHeight ? hitDz * 2.5f : 0f;
                    // A gap costs more than the ground it spans: the jump has to be set up and landing
                    // short is a death. Without this the router prefers a chain of leaps to a walkway.
                    float gap = step > 1 ? (step - 1) * NavField.CellSize * 1.5f : 0f;
                    // Hazards are expensive, not forbidden: a bot with health to spare should be free to
                    // clip a slime corner if it saves a second, and making them impassable would hide
                    // routes the policy could legitimately take.
                    float hazard = ((NavContent)col[hit].Content & NavContent.Harmful) != 0 ? 600f : 0f;

                    float nd = d + horiz + climb + gap + hazard;
                    int ni = result._start[ny * w + nx] + hit;
                    if (nd < dist[ni])
                    {
                        dist[ni] = nd;
                        heap.Enqueue(ni, nd);
                    }
                    break;   // the nearest standable span in this direction is the one we cross to
                }
            }
        }

        result.ReachedSpans = reached;
        return result;
    }

    /// <summary>
    /// Geodesic distance from <paramref name="world"/> to the goal, or <see cref="Unreachable"/>.
    /// </summary>
    public float DistanceAt(Vector3 world)
    {
        if (!_field.TryCell(world, out int cx, out int cy)) return Unreachable;
        int idx = SpanIndexUnder(cx, cy, world.Z);
        return idx < 0 ? Unreachable : _dist[idx];
    }

    /// <summary>
    /// Whether a route exists from <paramref name="from"/> to the goal this field was built for. The course
    /// generator uses it to reject A/B pairs with no path, which is the difference between a hard episode
    /// and an impossible one.
    /// </summary>
    public bool IsReachable(Vector3 from)
    {
        if (DistanceAt(from) < Unreachable) return true;
        if (!PocketTolerantReach) return false;

        // Tolerate the lattice's own gaps before calling a position hopeless.
        //
        // A 32 qu discretisation does not reach every standable span: doorway thresholds, stair nosings,
        // slope edges and cell centres whose only standable span the adjacency could not link all end up
        // outside the flood. A bot crossing one reads "unreachable" correctly as a statement about that CELL,
        // and then walks out of it a step later.
        //
        // Measured at the instant such a spell begins, over 426 agents: 75.6% had a NEIGHBOURING column that
        // routed fine, and the single commonest case was a one-span column -- so there was no wrong surface
        // to pick and no missing pad edge to add. Two fixes aimed at those theories (jump-pad/teleporter
        // edges, and resolving onto the feet rather than the origin) each moved this metric by under a point.
        // The pockets are the cause, and a query that means "is this position hopeless" has to look past one
        // cell to answer it honestly.
        if (!_field.TryCell(from, out int cx, out int cy)) return false;

        // Any other span in the bot's own column: it may simply be standing on the one the flood missed.
        foreach (FloorSpan s in _field.Column(cx, cy))
            if (DistanceAt(new Vector3(from.X, from.Y, s.FloorZ + OriginAboveFloor)) < Unreachable)
                return true;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                float px = from.X + dx * NavField.CellSize, py = from.Y + dy * NavField.CellSize;
                if (!_field.TryCell(new Vector3(px, py, from.Z), out int nx, out int ny)) continue;
                foreach (FloorSpan s in _field.Column(nx, ny))
                {
                    // Only surfaces the bot could actually step onto from here; a routable span forty feet
                    // below is not an escape, it is a fall.
                    if (MathF.Abs(s.FloorZ + OriginAboveFloor - from.Z) > BotNavigation.JumpStepHeight) continue;
                    if (DistanceAt(new Vector3(px, py, s.FloorZ + OriginAboveFloor)) < Unreachable) return true;
                }
            }
        }
        return false;
    }

    /// <summary>Set false to restore the single-cell reachability test, for A/B.</summary>
    public static bool PocketTolerantReach = true;

    /// <summary>
    /// The flat span index of the surface under (<paramref name="cx"/>,<paramref name="cy"/>) at height
    /// <paramref name="z"/>, or -1. Mirrors <see cref="NavField.TrySampleBelow"/>'s choice so the reward and
    /// the observation always agree about which surface the bot is on.
    /// </summary>
    /// <summary>
    /// How far a body's origin sits above the surface it rests on: 24 qu, from the player hull.
    /// </summary>
    /// <remarks>
    /// Every Z handed to this class is an ORIGIN, not a pair of feet. Player positions are origins by
    /// definition, entity anchors are entity origins, and <see cref="PointAlongRoute"/> deliberately emits
    /// <c>FloorZ + 24</c> so its output can be fed straight back in. The convention is consistent; what was
    /// missing was accounting for it in the one place that resolves a Z back onto a surface.
    /// </remarks>
    public static readonly float OriginAboveFloor = -SpawnSystem.PlayerMins.Z;

    /// <summary>Set false to restore the pre-S6 resolution, for A/B.</summary>
    public static bool FeetResolution = true;

    /// <summary>
    /// The span index a body whose ORIGIN is at <paramref name="z"/> is standing on, or -1.
    /// </summary>
    /// <remarks>
    /// The tolerance is a step height above the FEET, not above the origin. It used to be measured from the
    /// origin, which made it 42 qu rather than 18 -- so a crate top, a stair tread or a low ledge up to 42 qu
    /// above the bot could win the resolution and the bot would be told it was standing on a surface it was
    /// actually standing beside.
    ///
    /// <para>That is what made reachability jitter. Measured: of agents reading "goal unreachable" while on
    /// the ground, 86.6% recovered within the same episode and 35.7% still arrived -- the field was not
    /// describing the map, it was describing which of several stacked spans the query happened to land on.
    /// Jump-pad edges were tried first on the theory that the graph was missing connections; they made no
    /// difference, which is what pointed here.</para>
    /// </remarks>
    private int SpanIndexUnder(int cx, int cy, float z)
    {
        int c = cy * _field.Width + cx;
        ReadOnlySpan<FloorSpan> col = _field.Column(cx, cy);
        float feet = FeetResolution ? z - OriginAboveFloor : z;
        float probe = feet + BotNavigation.StepHeight;
        for (int i = 0; i < col.Length; i++)
            if (col[i].FloorZ <= probe) return _start[c] + i;
        return -1;
    }

    /// <summary>Flat span index back to (column x, column y, slot). Binary search over the column offsets.</summary>
    private void Locate(int flat, out int cx, out int cy, out int slot)
    {
        int lo = 0, hi = _start.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_start[mid] <= flat) lo = mid; else hi = mid - 1;
        }
        // Empty columns share their successor's offset, so walk back to the one that actually owns the span.
        while (lo > 0 && _count[lo] == 0) lo--;
        cx = lo % _field.Width;
        cy = lo / _field.Width;
        slot = flat - _start[lo];
    }

    /// <summary>
    /// Walk downhill from <paramref name="from"/> and return the point roughly <paramref name="lookahead"/>
    /// Quake units along the route to the goal.
    ///
    /// <para><b>Why the training environment needs this.</b> The observation carries two corridor
    /// look-ahead vectors, which at runtime come from the waypoint route
    /// (<c>BotBrainNeural</c> reads <see cref="BotNavigation.RouteNode"/>) and give the policy two nodes of
    /// warning before a direction change. Training used to set both equal to the goal, so six of the 206
    /// observation floats were constant during learning and meaningful in the game: the policy learns to
    /// ignore an input that then starts carrying information. Walking the distance field produces the same
    /// quantity from the same geometry, and it works on generated courses too, where there is no waypoint
    /// graph to read.</para>
    ///
    /// <para>Returns the goal itself when the route is shorter than the look-ahead, and
    /// <paramref name="from"/> when the position is off-graph.</para>
    /// </summary>
    public Vector3 PointAlongRoute(Vector3 from, float lookahead)
    {
        if (!_field.TryCell(from, out int cx, out int cy)) return from;
        int idx = SpanIndexUnder(cx, cy, from.Z);
        if (idx < 0 || _dist[idx] >= Unreachable) return from;

        ReadOnlySpan<int> dxs = stackalloc int[] { 1, 1, 0, -1, -1, -1, 0, 1 };
        ReadOnlySpan<int> dys = stackalloc int[] { 0, 1, 1, 1, 0, -1, -1, -1 };

        int w = _field.Width, h = _field.Height;
        float travelled = 0f;
        Vector3 cur = from;
        int steps = 0;

        // Greedy descent over the same adjacency Dijkstra used, so the path exists by construction. Bounded
        // because a numerically flat region could otherwise oscillate between two equal-cost neighbours.
        while (travelled < lookahead && steps++ < 96)
        {
            float best = _dist[SpanIndexAt(cx, cy, cur.Z, out int curSlot)];
            if (curSlot < 0) break;

            int bx = -1, by = -1;
            float bz = cur.Z;
            for (int d = 0; d < 8; d++)
            {
                // The same outward reach the Dijkstra used, or this walk stalls at the near lip of every
                // gap the route crosses.
                for (int step = 1; step <= MaxJumpCells; step++)
                {
                    int nx = cx + dxs[d] * step, ny = cy + dys[d] * step;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) break;
                    int nc = ny * w + nx;
                    ReadOnlySpan<FloorSpan> col = _field.Column(nx, ny);
                    if (col.Length == 0) continue;

                    bool routable = false;
                    for (int t = 0; t < col.Length; t++)
                    {
                        float nd = _dist[_start[nc] + t];
                        if (nd >= Unreachable) continue;
                        routable = true;
                        if (nd >= best) continue;
                        best = nd;
                        // Emit an ORIGIN height, not a floor height, so the result can be fed straight back
                        // into DistanceAt / SpanIndexUnder without a caller having to know the convention.
                        bx = nx; by = ny; bz = col[t].FloorZ + OriginAboveFloor;
                    }
                    if (routable) break;   // the nearest routable column in this direction
                }
            }
            if (bx < 0) break;   // a local minimum: this is the goal, or as close as the graph gets

            Vector3 next = _field.CellCentre(bx, by);
            next.Z = bz;
            travelled += (next - cur).Length();
            cur = next;
            cx = bx; cy = by;
        }
        return cur;
    }

    /// <summary>The flat span index under a cell at height <paramref name="z"/>, with its slot.</summary>
    private int SpanIndexAt(int cx, int cy, float z, out int slot)
    {
        int c = cy * _field.Width + cx;
        int flat = SpanIndexUnder(cx, cy, z);
        if (flat >= 0)
        {
            slot = flat - _start[c];
            return flat;
        }
        slot = -1;
        return 0;
    }

    /// <summary>
    /// Every standable span whose distance from the goal falls in [<paramref name="min"/>,
    /// <paramref name="max"/>], as world positions. The course generator draws episode start points from
    /// this, so difficulty is set by route length rather than by straight-line distance.
    /// </summary>
    public List<Vector3> SpansInRange(float min, float max, int limit = 4096)
    {
        var result = new List<Vector3>();
        for (int y = 0; y < _field.Height && result.Count < limit; y++)
        {
            for (int x = 0; x < _field.Width && result.Count < limit; x++)
            {
                int c = y * _field.Width + x;
                ReadOnlySpan<FloorSpan> col = _field.Column(x, y);
                for (int i = 0; i < col.Length; i++)
                {
                    float d = _dist[_start[c] + i];
                    if (d < min || d > max) continue;
                    if (((NavContent)col[i].Content & NavContent.Standable) == 0) continue;
                    Vector3 centre = _field.CellCentre(x, y);
                    result.Add(new Vector3(centre.X, centre.Y, col[i].FloorZ + 26f));
                    break;
                }
            }
        }
        return result;
    }
}

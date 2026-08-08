using System;
using System.Collections.Generic;
using System.Numerics;

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

    public static NavDistanceField Build(NavField field, Vector3 goal)
    {
        int w = field.Width, h = field.Height;
        SpanIndex index = _indexCache.GetValue(field, static f => new SpanIndex(f));
        int[] start = index.Start;
        byte[] count = index.Count;
        int total = index.Total;

        // The one genuinely per-goal allocation left. Still large, but one array per call rather than
        // three, and no lattice sweep.
        var dist = new float[total];
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

        int reached = 0;
        while (heap.TryDequeue(out int cur, out float d))
        {
            if (d > dist[cur]) continue;   // a stale heap entry
            reached++;

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
    public bool IsReachable(Vector3 from) => DistanceAt(from) < Unreachable;

    /// <summary>
    /// The flat span index of the surface under (<paramref name="cx"/>,<paramref name="cy"/>) at height
    /// <paramref name="z"/>, or -1. Mirrors <see cref="NavField.TrySampleBelow"/>'s choice so the reward and
    /// the observation always agree about which surface the bot is on.
    /// </summary>
    private int SpanIndexUnder(int cx, int cy, float z)
    {
        int c = cy * _field.Width + cx;
        ReadOnlySpan<FloorSpan> col = _field.Column(cx, cy);
        float probe = z + BotNavigation.StepHeight;
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
                        bx = nx; by = ny; bz = col[t].FloorZ + 24f;
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
        ReadOnlySpan<FloorSpan> col = _field.Column(cx, cy);
        float probe = z + BotNavigation.StepHeight;
        for (int i = 0; i < col.Length; i++)
        {
            if (col[i].FloorZ > probe) continue;
            slot = i;
            return _start[c] + i;
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

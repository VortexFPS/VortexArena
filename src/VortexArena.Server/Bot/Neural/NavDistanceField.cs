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
    public static NavDistanceField Build(NavField field, Vector3 goal)
    {
        int w = field.Width, h = field.Height;
        int cells = w * h;
        var start = new int[cells];
        var count = new byte[cells];

        int total = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int c = y * w + x;
                start[c] = total;
                int n = field.Column(x, y).Length;
                count[c] = (byte)n;
                total += n;
            }
        }

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
            int mask = span.JumpReachMask;
            if (mask == 0) continue;

            for (int dir = 0; dir < 8; dir++)
            {
                if ((mask & (1 << dir)) == 0) continue;
                int nx = cx + dxs[dir], ny = cy + dys[dir];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;

                ReadOnlySpan<FloorSpan> col = field.Column(nx, ny);
                for (int t = 0; t < col.Length; t++)
                {
                    if (((NavContent)col[t].Content & NavContent.Standable) == 0) continue;
                    float dz = col[t].FloorZ - span.FloorZ;
                    // Only cross to a neighbour the mask actually permits reaching; the mask is per-direction,
                    // not per-span, so re-apply the height test to pick WHICH span in that column.
                    if (dz > BotNavigation.JumpStepHeight || dz < -400f) continue;

                    // Horizontal cost is the lattice step (diagonals cost sqrt(2)). Climbing costs extra
                    // because a jump takes time the flat step does not; dropping is free, which is correct —
                    // falling is fast.
                    float horiz = (dir % 2 == 1) ? NavField.CellSize * 1.41421f : NavField.CellSize;
                    float climb = dz > BotNavigation.StepHeight ? dz * 2.5f : 0f;
                    // Hazards are not forbidden, just expensive: a bot with health to spare should be free to
                    // clip a slime corner if it saves a second, and making them impassable would hide routes
                    // the policy could legitimately take.
                    float hazard = ((NavContent)col[t].Content & NavContent.Harmful) != 0 ? 600f : 0f;

                    float nd = d + horiz + climb + hazard;
                    int ni = result._start[ny * w + nx] + t;
                    if (nd >= dist[ni]) continue;
                    dist[ni] = nd;
                    heap.Enqueue(ni, nd);
                    break;   // the first standable span in range is the one we cross to
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

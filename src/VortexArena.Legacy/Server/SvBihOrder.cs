// Port of Base/darkplaces/bih.c BIH_Build / BIH_BuildNode (how the leaves are partitioned) and the
// order model_brush.c Mod_CollisionBIH_TraceBrush visits them in (at a split the back child is pushed
// first and the front child second, so the front half is popped and tested first; the leaves of an
// unordered node in the order they were stored).
using System.Numerics;
using VortexArena.Engine.Collision;

namespace VortexArena.Legacy.Server;

/// <summary>
/// The order in which DarkPlaces tests a map's collision leaves (brushes and patch triangles) against
/// a box. A sweep's result does not depend on that order; one thing does: when a box starts inside
/// several leaves equally deep, the leaf tested first is the one whose way out is taken
/// ("if (trace->startdepth > startdepth)" is strict), and that decides which way the program's
/// nudge-out-of-solid pass moves an item that sits in a symmetric hollow. The tree itself is not
/// kept - the port's own broadphase finds the candidates - only each leaf's place in the visiting
/// order.
/// </summary>
internal static class SvBihOrder
{
    private const int MaxUnorderedChildren = 8;   // BIH_MAXUNORDEREDCHILDREN

    /// <summary>
    /// The rank of every leaf: 0 is tested first. <paramref name="leaves"/> are in the order
    /// Mod_MakeCollisionBIH adds them - the model's brushes, then its collision triangles.
    /// </summary>
    public static Dictionary<Brush, int> Rank(IReadOnlyList<Brush> leaves, Func<Brush, bool> boundsAlreadyGrown)
    {
        int n = leaves.Count;
        Dictionary<Brush, int> rank = new(n, ReferenceEqualityComparer.Instance);
        if (n == 0) return rank;
        Vector3[] mins = new Vector3[n], maxs = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            // A leaf's box is the brush's bounds grown by one unit (Collision_NewBrushFromPlanes'
            // "brush->mins[0] -= 1", Mod_MakeCollisionBIH's "- 1" / "+ 1" for a triangle).
            Vector3 grow = boundsAlreadyGrown(leaves[i]) ? Vector3.Zero : Vector3.One;
            mins[i] = leaves[i].Mins - grow;
            maxs[i] = leaves[i].Maxs + grow;
        }
        int[] list = new int[n], scratch = new int[n];
        for (int i = 0; i < n; i++) list[i] = i;
        int next = 0;
        int[] order = new int[n];
        BuildNode(list, 0, n, scratch, mins, maxs, order, ref next, 0);
        for (int i = 0; i < n; i++) rank[leaves[order[i]]] = i;
        return rank;
    }

    private static void BuildNode(int[] list, int first, int count, int[] scratch, Vector3[] mins, Vector3[] maxs, int[] order, ref int next, int depth)
    {
        // "check if there are few enough children to store an unordered node". (The depth guard is
        // not DarkPlaces': a tree this deep would have overflowed its 1024-entry traversal stack.)
        if (count <= MaxUnorderedChildren || depth > 512)
        {
            for (int i = 0; i < count; i++) order[next++] = list[first + i];
            return;
        }
        // calculate bounds of children
        Vector3 lo = mins[list[first]], hi = maxs[list[first]];
        for (int i = 1; i < count; i++)
        {
            lo = Vector3.Min(lo, mins[list[first + i]]);
            hi = Vector3.Max(hi, maxs[list[first + i]]);
        }
        Vector3 size = hi - lo;
        // pick longest axis
        int longest = 0;
        if (size.X < size.Y) longest = 1;
        if (Axis(size, longest) < size.Z) longest = 2;
        // "iterate possible split axis choices, starting with the longest axis, if all fail it means
        // all children have the same bounds and we simply split the list in half"
        int front = 0, back = 0, j;
        for (j = 0; j < 3; j++)
        {
            int axis = (longest + j) % 3;
            float splitDist = (Axis(lo, axis) + Axis(hi, axis)) * 0.5f;
            front = back = 0;
            for (int i = 0; i < count; i++)
            {
                int leaf = list[first + i];
                float d = (Axis(mins[leaf], axis) + Axis(maxs[leaf], axis)) * 0.5f;
                if (d < splitDist) scratch[back++] = leaf;
                else list[first + front++] = leaf;
            }
            // now copy the back ones into the space made in the leaflist for them
            if (back != 0) Array.Copy(scratch, 0, list, first + front, back);
            // if both sides have some children, it's good enough for us.
            if (front != 0 && back != 0) break;
        }
        if (j == 3)
        {
            // somewhat common case: no good choice, divide children arbitrarily
            back = count >> 1;
            front = count - back;
        }
        // The front child is visited before the back child.
        BuildNode(list, first, front, scratch, mins, maxs, order, ref next, depth + 1);
        BuildNode(list, first + front, back, scratch, mins, maxs, order, ref next, depth + 1);
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}

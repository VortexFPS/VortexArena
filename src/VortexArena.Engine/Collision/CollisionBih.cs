// Port of Base/darkplaces/bih.c BIH_Build / BIH_BuildNode (how the leaves are partitioned) and of
// the tree walks in model_brush.c Mod_CollisionBIH_TraceBrush and Mod_CollisionBIH_TraceLineShared
// (which leaves a moving box or a line is tested against, and in which order: at a split the back
// child is pushed first and the front child second, so the front half is popped and tested first;
// the leaves of an unordered node in the order they were stored).
using System.Numerics;

namespace VortexArena.Engine.Collision;

/// <summary>
/// DarkPlaces' bounding interval hierarchy over a model's collision leaves (brushes and triangles):
/// a binary tree that splits the leaves at the middle of their longest axis until eight or fewer are
/// left, each split remembering how far its two halves reach toward each other. A move is walked
/// through it as a line - the path of the box's centre - that is cut down at every split to the part
/// that can touch each half, so a long line visits only the leaves along it.
///
/// Two things come out of it that the result of a trace can depend on. Which leaves are tested at
/// all (DarkPlaces' culling is this walk, pad and all). And in which order: a sweep's impact does
/// not depend on that, but when a box starts inside several leaves equally deep the leaf tested
/// first is the one whose way out is taken ("if (trace->startdepth > startdepth)" is strict), and
/// of two impacts at exactly the same fraction the first one tested is the one reported.
///
/// Immutable once built, so one tree may be walked from several threads; each walker brings its own
/// <see cref="Walker"/> for the traversal stack.
/// </summary>
public sealed class CollisionBih
{
    public const int MaxUnorderedChildren = 8;   // BIH_MAXUNORDEREDCHILDREN
    private const int StackSize = 1024;          // "int nodestack[1024]"
    private const int MaxDepth = 512;
    private const int Unordered = 3;             // BIH_UNORDERED; 0..2 are BIH_SPLITX..BIH_SPLITZ

    private struct Node
    {
        public int Type;
        public Vector3 Mins, Maxs;
        // a split: node indexes of the children and the interval each reaches along the split axis
        public int Front, Back;
        public float FrontMin, BackMax;
        // an unordered node: its leaves are _children[FirstChild .. FirstChild + ChildCount)
        public int FirstChild, ChildCount;
    }

    /// <summary>The traversal stack of one walker ("nodestack" and "nodestackline"). Not shared between threads.</summary>
    public sealed class Walker
    {
        internal readonly int[] Nodes = new int[StackSize];
        internal readonly Vector3[] Starts = new Vector3[StackSize], Ends = new Vector3[StackSize];
    }

    private readonly Node[] _nodes;
    private readonly int _nodeCount;
    private readonly int[] _children;
    private readonly Vector3[] _leafMins, _leafMaxs;

    /// <summary>How many leaves the tree was built over.</summary>
    public int LeafCount => _leafMins.Length;
    public int NodeCount => _nodeCount;
    /// <summary>bih->mins / maxs: the bounds of every leaf.</summary>
    public Vector3 Mins { get; }
    public Vector3 Maxs { get; }

    /// <summary>The leaves in the order a walk that visits everything meets them: element 0 is tested first.</summary>
    public ReadOnlySpan<int> LeafOrder => _children;

    private CollisionBih(Node[] nodes, int nodeCount, int[] children, Vector3[] leafMins, Vector3[] leafMaxs)
    {
        _nodes = nodes;
        _nodeCount = nodeCount;
        _children = children;
        _leafMins = leafMins;
        _leafMaxs = leafMaxs;
        Mins = nodeCount > 0 ? nodes[0].Mins : default;
        Maxs = nodeCount > 0 ? nodes[0].Maxs : default;
    }

    /// <summary>
    /// BIH_Build. <paramref name="leafMins"/> / <paramref name="leafMaxs"/> are the leaf boxes as
    /// Mod_MakeCollisionBIH makes them - a brush's or a triangle's bounds grown by one unit - in the
    /// order it adds them. The arrays are kept.
    /// </summary>
    public static CollisionBih Build(Vector3[] leafMins, Vector3[] leafMaxs)
    {
        ArgumentNullException.ThrowIfNull(leafMins);
        ArgumentNullException.ThrowIfNull(leafMaxs);
        if (leafMins.Length != leafMaxs.Length) throw new ArgumentException("one box per leaf");
        int n = leafMins.Length;
        if (n == 0) return new CollisionBih(Array.Empty<Node>(), 0, Array.Empty<int>(), leafMins, leafMaxs);
        // "bihmaxnodes = bihnumleafs + 1"
        Builder b = new() { Nodes = new Node[n + 1], Children = new int[n], List = new int[n], Scratch = new int[n], Mins = leafMins, Maxs = leafMaxs };
        for (int i = 0; i < n; i++) b.List[i] = i;
        b.BuildNode(0, n, 0, out _, out _);
        Node[] nodes = b.Nodes;
        if (b.NodeCount < nodes.Length) Array.Resize(ref nodes, b.NodeCount);
        return new CollisionBih(nodes, b.NodeCount, b.Children, leafMins, leafMaxs);
    }

    private sealed class Builder
    {
        public Node[] Nodes = Array.Empty<Node>();
        public int NodeCount, ChildCount;
        public int[] Children = Array.Empty<int>(), List = Array.Empty<int>(), Scratch = Array.Empty<int>();
        public Vector3[] Mins = Array.Empty<Vector3>(), Maxs = Array.Empty<Vector3>();

        // BIH_BuildNode
        public int BuildNode(int first, int count, int depth, out Vector3 totalMins, out Vector3 totalMaxs)
        {
            // calculate bounds of children
            Vector3 lo = Mins[List[first]], hi = Maxs[List[first]];
            for (int i = 1; i < count; i++)
            {
                // "if (mins[0] > child->mins[0]) mins[0] = child->mins[0]": written out because
                // Vector3.Min is not that comparison when a bound is NaN
                Vector3 cl = Mins[List[first + i]], ch = Maxs[List[first + i]];
                if (lo.X > cl.X) lo.X = cl.X;
                if (lo.Y > cl.Y) lo.Y = cl.Y;
                if (lo.Z > cl.Z) lo.Z = cl.Z;
                if (hi.X < ch.X) hi.X = ch.X;
                if (hi.Y < ch.Y) hi.Y = ch.Y;
                if (hi.Z < ch.Z) hi.Z = ch.Z;
            }
            Vector3 size = hi - lo;
            // provide bounds to caller
            totalMins = lo;
            totalMaxs = hi;
            int nodeNum = NodeCount++;
            ref Node node = ref Nodes[nodeNum];
            node.Mins = lo;
            node.Maxs = hi;
            // "check if there are few enough children to store an unordered node". (The depth guard is
            // not DarkPlaces': a tree this deep would have overflowed its 1024-entry traversal stack.
            // Such a node simply lists more than eight leaves.)
            if (count <= MaxUnorderedChildren || depth > MaxDepth)
            {
                node.Type = Unordered;
                node.FirstChild = ChildCount;
                node.ChildCount = count;
                for (int j = 0; j < count; j++) Children[ChildCount++] = List[first + j];
                return nodeNum;
            }
            // pick longest axis
            int longest = 0;
            if (size.X < size.Y) longest = 1;
            if (Axis(size, longest) < size.Z) longest = 2;
            // "iterate possible split axis choices, starting with the longest axis, if all fail it means
            // all children have the same bounds and we simply split the list in half because each node
            // can only have two children"
            int axis = 0, front = 0, back = 0, tries;
            for (tries = 0; tries < 3; tries++)
            {
                // pick an axis
                axis = (longest + tries) % 3;
                // sort children into front and back lists
                float splitDist = (Axis(lo, axis) + Axis(hi, axis)) * 0.5f;
                front = back = 0;
                for (int i = 0; i < count; i++)
                {
                    int leaf = List[first + i];
                    float d = (Axis(Mins[leaf], axis) + Axis(Maxs[leaf], axis)) * 0.5f;
                    if (d < splitDist) Scratch[back++] = leaf;
                    else List[first + front++] = leaf;
                }
                // now copy the back ones into the space made in the leaflist for them
                if (back != 0) Array.Copy(Scratch, 0, List, first + front, back);
                // if both sides have some children, it's good enough for us.
                if (front != 0 && back != 0) break;
            }
            if (tries == 3)
            {
                // somewhat common case: no good choice, divide children arbitrarily
                axis = 0;
                back = count >> 1;
                front = count - back;
            }
            // we now have front and back children divided in leaflist...
            int frontNode = BuildNode(first, front, depth + 1, out Vector3 frontMins, out _);
            int backNode = BuildNode(first + front, back, depth + 1, out _, out Vector3 backMaxs);
            // (the array may not be touched through "node" across the recursion in C either: the
            // nodes are preallocated, as here)
            node = ref Nodes[nodeNum];
            node.Type = axis;
            node.Front = frontNode;
            node.FrontMin = Axis(frontMins, axis);
            node.Back = backNode;
            node.BackMax = Axis(backMaxs, axis);
            return nodeNum;
        }
    }

    /// <summary>
    /// The walk of Mod_CollisionBIH_TraceBrush: the leaves a box moving from one place to another is
    /// tested against, appended to <paramref name="leaves"/> in the order DarkPlaces tests them.
    /// <paramref name="start"/> and <paramref name="end"/> are the centre of the box's bounds at the
    /// two ends of the move and <paramref name="mins"/> / <paramref name="maxs"/> its extent about
    /// that centre ("calculate tracebox-like parameters for efficient culling"). With both zero this
    /// is the walk of Mod_CollisionBIH_TraceLineShared, which is the same code without the box.
    /// </summary>
    public void QuerySwept(Walker walker, Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, List<int> leaves)
    {
        if (_nodeCount == 0) return;
        int[] stack = walker.Nodes;
        Vector3[] starts = walker.Starts, ends = walker.Ends;
        int pos = 0;
        // push first node
        starts[pos] = start;
        ends[pos] = end;
        stack[pos++] = 0;
        Vector3 one = Vector3.One;
        while (pos != 0)
        {
            int nodeNum = stack[--pos];
            ref readonly Node node = ref _nodes[nodeNum];
            Vector3 nodeStart = starts[pos], nodeEnd = ends[pos];
            Vector3 sweepMins = Vector3.Min(nodeStart, nodeEnd) + mins - one, sweepMaxs = Vector3.Max(nodeStart, nodeEnd) + maxs + one;
            if (!CollisionWorld.BoxesOverlap(sweepMins, sweepMaxs, node.Mins, node.Maxs)) continue;
            if (node.Type != Unordered)
            {
                // "Out of stack"
                if (pos > StackSize - 2) continue;
                // recurse children of the split
                int axis = node.Type;
                float s = Axis(nodeStart, axis), e = Axis(nodeEnd, axis);
                float d1 = node.BackMax - Axis(mins, axis) - s;
                float d2 = node.BackMax - Axis(mins, axis) - e;
                float d3 = s - (node.FrontMin - Axis(maxs, axis));
                float d4 = e - (node.FrontMin - Axis(maxs, axis));
                float f = 1.0f / (e - s);
                // The sixteen cases of the C's switch, as the two questions they answer. The back
                // child gets the part of the line on the back side of backmax, the front child the
                // part on the front side of frontmin; a child the line does not reach is not pushed.
                if (!(d1 < 0 && d2 < 0))
                {
                    starts[pos] = d1 < 0 ? Lerp(nodeStart, d1 * f, nodeEnd) : nodeStart;
                    ends[pos] = d1 >= 0 && d2 < 0 ? Lerp(nodeStart, d1 * f, nodeEnd) : nodeEnd;
                    stack[pos++] = node.Back;
                }
                if (!(d3 < 0 && d4 < 0))
                {
                    starts[pos] = d3 < 0 ? Lerp(nodeStart, -d3 * f, nodeEnd) : nodeStart;
                    ends[pos] = d3 >= 0 && d4 < 0 ? Lerp(nodeStart, -d3 * f, nodeEnd) : nodeEnd;
                    stack[pos++] = node.Front;
                }
                continue;
            }
            // "copy node bounds into local variables and expand to get Minkowski Sum of the two
            // shapes", then "clip line to this node bounds"
            Vector3 bigMins = node.Mins - maxs, bigMaxs = node.Maxs - mins;
            if (!ClipLine(ref nodeStart, ref nodeEnd, bigMins.X, bigMaxs.X, 0)
                || !ClipLine(ref nodeStart, ref nodeEnd, bigMins.Y, bigMaxs.Y, 1)
                || !ClipLine(ref nodeStart, ref nodeEnd, bigMins.Z, bigMaxs.Z, 2))
                continue;
            // some of the line intersected the enlarged node box: calculate sweep bounds for this node
            sweepMins = Vector3.Min(nodeStart, nodeEnd) + mins - one;
            sweepMaxs = Vector3.Max(nodeStart, nodeEnd) + maxs + one;
            int last = node.FirstChild + node.ChildCount;
            for (int i = node.FirstChild; i < last; i++)
            {
                int leaf = _children[i];
                if (CollisionWorld.BoxesOverlap(sweepMins, sweepMaxs, _leafMins[leaf], _leafMaxs[leaf])) leaves.Add(leaf);
            }
        }
    }

    // One axis of "clip line to this node bounds": false if the line lies wholly outside the slab.
    private static bool ClipLine(ref Vector3 start, ref Vector3 end, float lo, float hi, int axis)
    {
        float d1 = Axis(start, axis) - lo, d2 = Axis(end, axis) - lo;
        if (d1 < 0)
        {
            if (d2 < 0) return false;
            start = Lerp(start, d1 / (d1 - d2), end);
        }
        else if (d2 < 0) end = Lerp(start, d1 / (d1 - d2), end);
        d1 = hi - Axis(start, axis);
        d2 = hi - Axis(end, axis);
        if (d1 < 0)
        {
            if (d2 < 0) return false;
            start = Lerp(start, d1 / (d1 - d2), end);
        }
        else if (d2 < 0) end = Lerp(start, d1 / (d1 - d2), end);
        return true;
    }

    // VectorLerp(v1, lerp, v2, out)
    private static Vector3 Lerp(Vector3 a, float t, Vector3 b) => new(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z));

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}

using System.Numerics;

namespace XonoticGodot.Formats.Bsp;

/// <summary>
/// Potentially-Visible-Set queries over a parsed <see cref="BspData"/> — the C# successor to DP's
/// <c>Mod_Q3BSP_PointSuperContents</c>/<c>CM_PointLeafnum</c> + cluster-PVS path, backing the QC
/// <c>checkpvs(vector, entity)</c> builtin. A point is mapped to a BSP leaf (and thus a visibility cluster) by
/// descending the node tree; two points are "potentially visible" when their clusters' PVS bit is set.
///
/// <para>PVS is a conservative <em>superset</em> of true visibility: it never hides something that is actually
/// visible, so it is safe to use as a cheap pre-filter before an exact <c>traceline</c> line-of-sight test
/// (the QC pattern), or to cull entity networking / sound to a client's visible set. On an unvised map (no
/// <see cref="BspData.Vis"/> data) every query returns visible — exactly DP's degradation.</para>
///
/// Godot-free and immutable after construction, so it is safe to share across the sim and build in tests.
/// </summary>
public sealed class BspPvs
{
    private readonly BspNode[] _nodes;
    private readonly BspLeaf[] _leafs;
    private readonly BspPlane[] _planes;
    private readonly BspVis _vis;
    private readonly int[] _leafFaces;
    private readonly int _faceCount;

    public BspPvs(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        _nodes = bsp.Nodes;
        _leafs = bsp.Leafs;
        _planes = bsp.Planes;
        _vis = bsp.Vis;
        _leafFaces = bsp.LeafFaces;
        _faceCount = bsp.Faces.Length;
    }

    /// <summary>True when the map carries real visibility data (otherwise every <see cref="IsInPvs"/> is visible).</summary>
    public bool HasVis => _vis.ClusterCount > 0 && _vis.Data is { Length: > 0 } && _leafs.Length > 0;

    /// <summary>
    /// Descend the BSP node tree to the leaf containing <paramref name="point"/> (DP <c>CM_PointLeafnum</c>):
    /// at each node take the front child when the point is on the positive side of the splitting plane, else the
    /// back child. Returns a leaf index, or −1 when there is no node tree to descend.
    /// </summary>
    public int FindLeaf(Vector3 point)
    {
        if (_nodes.Length == 0)
            return _leafs.Length > 0 ? 0 : -1; // single-leaf model (no splits)

        int num = 0;
        int guard = 0;
        while (num >= 0)
        {
            if (num >= _nodes.Length || ++guard > _nodes.Length + 1)
                return -1; // malformed tree — bail to "no leaf" (caller treats as visible)
            BspNode node = _nodes[num];
            if (node.PlaneIndex < 0 || node.PlaneIndex >= _planes.Length)
                return -1;
            BspPlane plane = _planes[node.PlaneIndex];
            float d = Vector3.Dot(plane.Normal, point) - plane.Distance;
            num = d >= 0f ? node.Child0 : node.Child1;
        }
        return -num - 1; // leaf index = -(child)-1
    }

    /// <summary>The visibility cluster of a leaf (−1 if the index is invalid or the leaf has no cluster).</summary>
    public int LeafCluster(int leafIndex)
        => leafIndex >= 0 && leafIndex < _leafs.Length ? _leafs[leafIndex].Cluster : -1;

    /// <summary>
    /// DP-faithful per-face visibility labels: <c>result[f]</c> is the set of clusters of EVERY leaf that
    /// references face <c>f</c> (via the lump-5 leaf→face slices). A face bounds one-or-more leaves; DP draws it
    /// when ANY referencing leaf is in the camera's PVS, so this union is exactly the set of clusters the face is
    /// potentially visible from — no sampling, no boundary/solid mislabeling. Faces referenced only by solid
    /// leaves (cluster −1), or by none, get an empty array (the §12.8 culler treats a cell with no clusters as
    /// always-visible, so such geometry is never wrongly hidden). Built once at map load.
    /// </summary>
    public int[][] BuildFaceClusterSets()
    {
        var sets = new HashSet<int>?[_faceCount];
        foreach (BspLeaf leaf in _leafs)
        {
            if (leaf.Cluster < 0)
                continue; // solid / clusterless leaf contributes nothing
            int start = leaf.FirstLeafFace;
            int end = start + leaf.LeafFaceCount;
            for (int i = start; i < end; i++)
            {
                if (i < 0 || i >= _leafFaces.Length)
                    continue;
                int face = _leafFaces[i];
                if (face < 0 || face >= _faceCount)
                    continue;
                (sets[face] ??= new HashSet<int>()).Add(leaf.Cluster);
            }
        }
        var result = new int[_faceCount][];
        for (int f = 0; f < _faceCount; f++)
            result[f] = sets[f] is { } s ? System.Linq.Enumerable.ToArray(s) : Array.Empty<int>();
        return result;
    }

    /// <summary>True if cluster <paramref name="to"/> is potentially visible from cluster <paramref name="from"/>.</summary>
    public bool ClustersVisible(int from, int to) => _vis.ClusterVisible(from, to);

    /// <summary>
    /// DP <c>Mod_Q3BSP_BoxTouchingPVS</c>: is ANY leaf the world-space box [<paramref name="mins"/>,
    /// <paramref name="maxs"/>] overlaps potentially visible from <paramref name="viewerCluster"/>? Used to
    /// PVS-cull entity RENDERING by the entity's BOUNDS rather than a single point — a tall model peeking past
    /// a cluster boundary must not vanish, so we test the whole box and show it if any touched cluster is in
    /// the PVS. Conservative by construction: an unvised map, a viewer in solid (cluster &lt; 0), or a
    /// malformed tree all return true (never hide a possibly-visible entity); solid leaves the box clips
    /// through contribute nothing (their −1 cluster can't make it visible, but other touched leaves can).
    /// </summary>
    public bool BoxAnyClusterVisibleFrom(int viewerCluster, Vector3 mins, Vector3 maxs)
    {
        if (!HasVis || viewerCluster < 0 || _nodes.Length == 0)
            return true;
        return BoxVis(0, viewerCluster, mins, maxs);
    }

    private bool BoxVis(int num, int viewerCluster, Vector3 mins, Vector3 maxs)
    {
        while (num >= 0)
        {
            if (num >= _nodes.Length)
                return true; // malformed tree → conservative
            BspNode node = _nodes[num];
            if (node.PlaneIndex < 0 || node.PlaneIndex >= _planes.Length)
                return true;
            BspPlane plane = _planes[node.PlaneIndex];
            Vector3 n = plane.Normal;
            // Project the box's two extreme corners onto the plane normal (the near/far corner along n).
            float distMin = (n.X >= 0f ? mins.X : maxs.X) * n.X
                          + (n.Y >= 0f ? mins.Y : maxs.Y) * n.Y
                          + (n.Z >= 0f ? mins.Z : maxs.Z) * n.Z - plane.Distance;
            if (distMin >= 0f) { num = node.Child0; continue; }      // box wholly on the front side
            float distMax = (n.X >= 0f ? maxs.X : mins.X) * n.X
                          + (n.Y >= 0f ? maxs.Y : mins.Y) * n.Y
                          + (n.Z >= 0f ? maxs.Z : mins.Z) * n.Z - plane.Distance;
            if (distMax < 0f) { num = node.Child1; continue; }       // box wholly on the back side
            // Straddles the plane — explore the front subtree, then fall through to the back.
            if (BoxVis(node.Child0, viewerCluster, mins, maxs))
                return true;
            num = node.Child1;
        }
        int cluster = LeafCluster(-num - 1);
        if (cluster < 0)
            return false; // solid / clusterless leaf — this overlap contributes nothing
        return ClustersVisible(viewerCluster, cluster);
    }

    /// <summary>
    /// QC <c>checkpvs(from, to)</c>: is <paramref name="to"/> potentially visible from <paramref name="from"/>?
    /// Returns true (conservatively visible) on an unvised map or when either point falls outside the leaf/cluster
    /// data — never a false negative, so it is safe as a traceline pre-filter.
    /// </summary>
    public bool IsInPvs(Vector3 from, Vector3 to)
    {
        if (!HasVis)
            return true;
        int clusterFrom = LeafCluster(FindLeaf(from));
        int clusterTo = LeafCluster(FindLeaf(to));
        if (clusterFrom < 0 || clusterTo < 0)
            return true; // a point in solid / no cluster info → conservatively visible
        return _vis.ClusterVisible(clusterFrom, clusterTo);
    }
}

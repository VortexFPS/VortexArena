namespace VortexArena.Formats.Bsp;

/// <summary>
/// The parts of a Quake 1 format map that have the same meaning in a Quake 3 one - the node tree, the
/// leaves' visibility clusters, the visibility rows, the models' bounds and the entity text - as a
/// <see cref="BspData"/>, so that code written against the Quake 3 type for exactly those parts
/// (<see cref="BspPvs"/>, a server's fat visibility set and its box test: DarkPlaces' <c>Mod_BSP_*</c>
/// functions, which are shared by both formats there too) runs on either.
///
/// It is NOT the map: it has no faces, vertices, textures, brushes, lightmaps or light grid. Whoever
/// draws or collides goes to <see cref="Q1BspData"/>.
/// </summary>
public static class Q1BspTreeView
{
    /// <summary>The value of <see cref="BspData.Version"/> that marks a view made here (29, Quake's own number).</summary>
    public const int Version = 29;

    public static BspData Create(Q1BspData q1)
    {
        ArgumentNullException.ThrowIfNull(q1);
        var planes = new BspPlane[q1.Planes.Length];
        for (int i = 0; i < planes.Length; i++) planes[i] = new BspPlane(q1.Planes[i].Normal, q1.Planes[i].Dist);
        var nodes = new BspNode[q1.Nodes.Length];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = new BspNode(q1.Nodes[i].PlaneIndex, q1.Nodes[i].Child0, q1.Nodes[i].Child1);
        var leafs = new BspLeaf[q1.Leafs.Length];
        for (int i = 0; i < leafs.Length; i++) leafs[i] = new BspLeaf(q1.Leafs[i].Cluster, 0, 0, 0, 0, 0);
        var models = new BspModel[q1.Models.Length];
        for (int i = 0; i < models.Length; i++) models[i] = new BspModel(q1.Models[i].Mins, q1.Models[i].Maxs, 0, 0, 0, 0);
        return new BspData
        {
            Version = Version,
            EntitiesText = q1.EntitiesText,
            Entities = q1.Entities,
            Planes = planes,
            Nodes = nodes,
            Leafs = leafs,
            Models = models,
            Vis = q1.PvsClusterCount > 0 ? new BspVis(q1.PvsClusterCount, q1.PvsClusterBytes, q1.PvsClusters) : default,
        };
    }
}

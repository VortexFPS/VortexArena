using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the BSP Potentially-Visible-Set: parsing the Nodes/Leafs/Vis lumps, the point→leaf tree descent, the
/// cluster visibility bitset, and the <c>checkpvs</c> facade. PVS is a conservative superset of true visibility
/// (never a false negative), so it is safe as a traceline pre-filter; an unvised map reads as all-visible.
/// </summary>
public class BspPvsTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    /// <summary>
    /// A minimal vised BSP: one splitting plane at X=0 with two leaves/clusters, where each cluster sees ONLY
    /// itself. Front (x≥0) = leaf 0 / cluster 0; back (x&lt;0) = leaf 1 / cluster 1.
    /// </summary>
    private static BspData TwoClusterBsp() => new()
    {
        Planes = new[] { new BspPlane(new Vector3(1, 0, 0), 0f) },
        Nodes = new[] { new BspNode(0, -1, -2) },           // child0 → leaf0 (-1), child1 → leaf1 (-2)
        Leafs = new[]
        {
            new BspLeaf(0, 0, 0, 0, 0, 0),                  // leaf 0 → cluster 0
            new BspLeaf(1, 0, 0, 0, 0, 0),                  // leaf 1 → cluster 1
        },
        Vis = new BspVis(2, 1, new byte[] { 0b0000_0001, 0b0000_0010 }), // 0 sees 0; 1 sees 1
    };

    private static readonly Vector3 Front = new(10, 0, 0);   // cluster 0
    private static readonly Vector3 Back = new(-10, 0, 0);   // cluster 1

    [Fact]
    public void FindLeaf_Descends_To_The_Correct_Side()
    {
        var pvs = new BspPvs(TwoClusterBsp());
        Assert.True(pvs.HasVis);
        Assert.Equal(0, pvs.FindLeaf(Front));
        Assert.Equal(1, pvs.FindLeaf(Back));
        Assert.Equal(0, pvs.LeafCluster(pvs.FindLeaf(Front)));
        Assert.Equal(1, pvs.LeafCluster(pvs.FindLeaf(Back)));
    }

    /// <summary>
    /// Regression for the §12.8 world-cell PVS culler: <see cref="BspPvs.FindLeaf"/> descends the tree in
    /// QUAKE space (Z-up), so a point expressed in Godot axes (Y-up) must be converted back first. The map
    /// loader recorded each cell's clusters from its mesh centroids — which live in Godot space — and was
    /// feeding them to FindLeaf RAW, so the Y/Z swap mislabeled every cell and the culler hid geometry that
    /// was actually visible (world surfaces vanishing as the camera crossed clusters). This locks the
    /// contract: the Godot-axis form lands in the WRONG leaf; converting to Quake first lands in the right one.
    /// </summary>
    [Fact]
    public void FindLeaf_Needs_Quake_Axes_Not_Godot_Axes()
    {
        // A Y-split tree so the Quake↔Godot Y/Z swap actually flips the side: front (Quake y≥0) = cluster 0.
        var bsp = new BspData
        {
            Planes = new[] { new BspPlane(new Vector3(0, 1, 0), 0f) },
            Nodes = new[] { new BspNode(0, -1, -2) },
            Leafs = new[] { new BspLeaf(0, 0, 0, 0, 0, 0), new BspLeaf(1, 0, 0, 0, 0, 0) },
            Vis = new BspVis(2, 1, new byte[] { 0b0000_0001, 0b0000_0010 }),
        };
        var pvs = new BspPvs(bsp);

        // A surface centroid in QUAKE space, genuinely in front (y=10 ≥ 0 → cluster 0).
        var quake = new Vector3(0, 10, -5);
        // Coords.ToGodot mirror (game-side, Godot-coupled, so replicated here): godot = (q.X, q.Z, -q.Y).
        var godot = new Vector3(quake.X, quake.Z, -quake.Y);
        // Coords.ToQuake mirror: quake = (g.X, -g.Z, g.Y) — the exact inverse, what the fix applies.
        var roundTripped = new Vector3(godot.X, -godot.Z, godot.Y);

        Assert.Equal(0, pvs.LeafCluster(pvs.FindLeaf(quake)));            // truth: cluster 0
        Assert.Equal(1, pvs.LeafCluster(pvs.FindLeaf(godot)));           // BUG: raw Godot axes → wrong cluster
        Assert.Equal(0, pvs.LeafCluster(pvs.FindLeaf(roundTripped)));    // FIX: convert back to Quake → correct
    }

    /// <summary>
    /// Locks the §12.8 culler's DP-faithful surface→cluster labeling (<see cref="BspPvs.BuildFaceClusterSets"/>),
    /// which replaced the fragile point-sampling that produced two "skybox-through-map" bugs (axis swap, then
    /// centroid-on-boundary landing in solid). DP knows each surface's clusters EXACTLY from the lump-5 leaf→face
    /// references: a face's cluster set is the union over every leaf that lists it. A face bounding two clusters
    /// gets both; a face referenced only by a solid (cluster −1) leaf gets none (the culler then treats its cell
    /// as always-visible — never wrongly hidden). No sampling, so no face is ever mislabeled into solid.
    /// </summary>
    [Fact]
    public void BuildFaceClusterSets_Unions_Every_Referencing_Leafs_Cluster()
    {
        var face = new BspFace(0, 0, BspFaceType.Flat, 0, 0, 0, 0, -1, 0, 0);
        var bsp = new BspData
        {
            // Three faces (0,1,2); geometry is irrelevant — only the leaf→face references matter here.
            Faces = new[] { face, face, face },
            // leaf0 (cluster 0) lists faces {0,1}; leaf1 (cluster 1) lists {1}; leaf2 (SOLID, −1) lists {2}.
            Leafs = new[]
            {
                new BspLeaf(0, 0, FirstLeafFace: 0, LeafFaceCount: 2, 0, 0),
                new BspLeaf(1, 0, FirstLeafFace: 2, LeafFaceCount: 1, 0, 0),
                new BspLeaf(-1, 0, FirstLeafFace: 3, LeafFaceCount: 1, 0, 0),
            },
            LeafFaces = new[] { 0, 1, 1, 2 },
        };

        int[][] sets = new BspPvs(bsp).BuildFaceClusterSets();

        Assert.Equal(new[] { 0 }, sets[0]);                 // face 0: only leaf0 → {0}
        Assert.Equal(new[] { 0, 1 }, sets[1].OrderBy(c => c));   // face 1: leaf0 ∪ leaf1 → {0,1}
        Assert.Empty(sets[2]);                              // face 2: only the solid leaf (−1) → none
    }

    /// <summary>
    /// (§12.8) <see cref="BspPvs.BoxAnyClusterVisibleFrom"/> backs DP-faithful entity render culling: an entity
    /// is shown when ANY leaf its bounds overlap is in the camera's PVS (tested by bounds, not a point, so a
    /// model straddling a cluster boundary can't wink out). A box wholly in a cluster the viewer can't see is
    /// culled; a box straddling the split touches both leaves and stays visible from either side.
    /// </summary>
    [Fact]
    public void BoxAnyClusterVisibleFrom_Tests_The_Whole_Box_Against_The_Pvs()
    {
        var pvs = new BspPvs(TwoClusterBsp()); // plane X=0: front=cluster0, back=cluster1; each sees only itself

        var frontBox = (min: new Vector3(5, -5, -5), max: new Vector3(10, 5, 5));    // wholly in cluster 0
        var backBox = (min: new Vector3(-10, -5, -5), max: new Vector3(-5, 5, 5));   // wholly in cluster 1
        var straddleBox = (min: new Vector3(-5, -5, -5), max: new Vector3(5, 5, 5)); // overlaps BOTH clusters

        // Viewer in cluster 0 sees the front box and the straddling box, but not the back-room box.
        Assert.True(pvs.BoxAnyClusterVisibleFrom(0, frontBox.min, frontBox.max));
        Assert.False(pvs.BoxAnyClusterVisibleFrom(0, backBox.min, backBox.max));
        Assert.True(pvs.BoxAnyClusterVisibleFrom(0, straddleBox.min, straddleBox.max));
        // ...and the mirror from cluster 1.
        Assert.False(pvs.BoxAnyClusterVisibleFrom(1, frontBox.min, frontBox.max));
        Assert.True(pvs.BoxAnyClusterVisibleFrom(1, backBox.min, backBox.max));
        Assert.True(pvs.BoxAnyClusterVisibleFrom(1, straddleBox.min, straddleBox.max));

        // Conservative fallbacks: a viewer in solid (cluster −1) and an unvised map never hide anything.
        Assert.True(pvs.BoxAnyClusterVisibleFrom(-1, backBox.min, backBox.max));
        BspData src = TwoClusterBsp();
        var unvised = new BspPvs(new BspData { Planes = src.Planes, Nodes = src.Nodes, Leafs = src.Leafs });
        Assert.True(unvised.BoxAnyClusterVisibleFrom(0, backBox.min, backBox.max));
    }

    [Fact]
    public void IsInPvs_Honors_The_Cluster_Bitset_Both_Directions()
    {
        var pvs = new BspPvs(TwoClusterBsp());
        Assert.True(pvs.IsInPvs(Front, Front));   // cluster 0 sees itself
        Assert.True(pvs.IsInPvs(Back, Back));     // cluster 1 sees itself
        Assert.False(pvs.IsInPvs(Front, Back));   // cluster 0 does NOT see cluster 1
        Assert.False(pvs.IsInPvs(Back, Front));   // and the reverse
    }

    [Fact]
    public void Unvised_Map_Is_Conservatively_All_Visible()
    {
        BspData src = TwoClusterBsp();
        var bsp = new BspData { Planes = src.Planes, Nodes = src.Nodes, Leafs = src.Leafs }; // no Vis
        var pvs = new BspPvs(bsp);
        Assert.False(pvs.HasVis);
        Assert.True(pvs.IsInPvs(Front, Back)); // no PVS → never cull
    }

    [Fact]
    public void Cluster_Bitset_Out_Of_Range_Reads_Visible()
    {
        var vis = new BspVis(2, 1, new byte[] { 0b0000_0001, 0b0000_0010 });
        Assert.True(vis.ClusterVisible(0, 0));
        Assert.False(vis.ClusterVisible(0, 1));
        Assert.True(vis.ClusterVisible(-1, 0));   // solid-leaf cluster → conservatively visible
        Assert.True(vis.ClusterVisible(0, 99));   // out of range → conservatively visible
    }

    [Fact]
    public void CheckPvs_Facade_Delegates_To_The_Wired_Pvs()
    {
        var world = new CollisionWorld();
        world.BuildGrid();
        var services = new EngineServices(world);

        // No PVS wired → conservatively visible (a non-BSP/test world).
        Assert.True(services.Trace.CheckPvs(Front, Back));

        services.Pvs = new BspPvs(TwoClusterBsp());
        Assert.True(services.Trace.CheckPvs(Front, Front));
        Assert.False(services.Trace.CheckPvs(Front, Back));
    }

    [Fact]
    public void Real_Bsp_Vis_Lumps_Parse_And_SelfVisibility_Holds()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? bspPath = vfs.Find("maps/", "bsp").FirstOrDefault();
        if (bspPath is null) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        // _init.bsp ships a real BSP tree (nodes + leaves) but is unvised (empty PVS lump), so this exercises
        // the real tree descent while the cluster-bitset culling is covered by the synthetic tests above.
        Assert.True(bsp.Nodes.Length >= 1, "a compiled BSP has a node tree");
        Assert.True(bsp.Leafs.Length >= 1, "a compiled BSP has leaves");

        var pvs = new BspPvs(bsp);
        // Descent on real data must land in a valid leaf, and self-visibility always holds.
        int leaf = pvs.FindLeaf(new Vector3(0, 0, 0));
        Assert.InRange(leaf, 0, bsp.Leafs.Length - 1);
        Assert.True(pvs.IsInPvs(Vector3.Zero, Vector3.Zero));
    }

    /// <summary>
    /// Real-data regression for the reported finalrage bug: standing at the quake (213,−161,216) spawn and
    /// looking down the +X corridor toward the (952,108,152) spawn, a slab of world past quake X=1024 winked out
    /// to the skybox. Root cause was the §12.8 culler's point-sampled cluster labels; this re-derives the cell's
    /// labels with the DP-faithful leaffaces path (<see cref="BspPvs.BuildFaceClusterSets"/>) and asserts the
    /// corridor cell is visible from the camera. Skips when the map assets aren't present.
    /// </summary>
    [Fact]
    public void Finalrage_Corridor_Cell_Is_Visible_From_The_Reported_Spawn()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? bspPath = vfs.Find("maps/", "bsp").FirstOrDefault(p => p.Contains("finalrage"));
        if (bspPath is null) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        var pvs = new BspPvs(bsp);
        Assert.True(pvs.HasVis, "finalrage is a vised map");
        Assert.NotEmpty(bsp.LeafFaces);   // lump 5 parsed

        // The two named spawns are in Quake space (HUD readout / info_player_deathmatch origins).
        var camera = new Vector3(213, -161, 216);
        var target = new Vector3(952, 108, 152);
        int camCluster = pvs.LeafCluster(pvs.FindLeaf(camera));
        int targetCluster = pvs.LeafCluster(pvs.FindLeaf(target));
        Assert.True(camCluster >= 0 && targetCluster >= 0, "both spawns are in real clusters");
        Assert.True(pvs.ClustersVisible(camCluster, targetCluster), "PVS itself proves the corridor is in view");

        // Re-derive the world cell that holds the +X corridor continuation exactly as MapLoader does: bin each
        // Flat/Mesh triangle by the Godot-space centroid of its 1024-unit cell, unioning the DP-faithful
        // per-face cluster set. (Godot = (qx, qz, −qy); cell = floor(godot / 1024).)
        int[][] faceClusters = pvs.BuildFaceClusterSets();
        const float cellSize = 1024f;
        static (int X, int Y, int Z) CellOf(Vector3 quakeCentroid)
        {
            var g = new Vector3(quakeCentroid.X, quakeCentroid.Z, -quakeCentroid.Y);
            return ((int)MathF.Floor(g.X / cellSize), (int)MathF.Floor(g.Y / cellSize), (int)MathF.Floor(g.Z / cellSize));
        }
        // The corridor the player looks down lives past quake X=1024 → Godot cell X=1.
        (int, int, int) corridorCell = CellOf(new Vector3(1300, 108, 152));

        var clustersInCorridorCell = new HashSet<int>();
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Flat && face.Type != BspFaceType.Mesh)
                continue;
            int end = face.FirstIndex + face.IndexCount;
            for (int e = face.FirstIndex; e + 2 < end; e += 3)
            {
                Vector3 va = bsp.Vertices[face.FirstVertex + bsp.Triangles[e]].Position;
                Vector3 vb = bsp.Vertices[face.FirstVertex + bsp.Triangles[e + 1]].Position;
                Vector3 vc = bsp.Vertices[face.FirstVertex + bsp.Triangles[e + 2]].Position;
                if (CellOf((va + vb + vc) / 3f) == corridorCell)
                    foreach (int cl in faceClusters[fi])
                        clustersInCorridorCell.Add(cl);
            }
        }

        Assert.NotEmpty(clustersInCorridorCell);   // the corridor cell has real geometry + labels
        // The fix: at least one of the cell's clusters is visible from the camera → the culler shows it (no skybox).
        Assert.Contains(clustersInCorridorCell, c => pvs.ClustersVisible(camCluster, c));
    }
}

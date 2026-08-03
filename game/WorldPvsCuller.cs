using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Bsp;

namespace VortexArena.Game;

/// <summary>
/// (§12.8) PVS-driven visibility for the spatially-split world mesh (§12.5 R5a): each frame the camera's
/// position descends the BSP tree to its visibility cluster, and every world cell is shown iff ANY of the
/// clusters its geometry occupies is potentially visible from there — the map compiler's own precomputed
/// occlusion (q3map2 vis), exactly the data DP culls with. This hides whole cells BEHIND WALLS, which
/// frustum culling cannot, at near-zero per-frame cost (one ~15-plane tree descent + a few dozen bitset
/// probes, and cell visibility is only re-applied when the camera CHANGES cluster).
///
/// <para><b>Losslessness:</b> PVS is a conservative superset of true visibility, and a cell's cluster set is
/// the UNION over its triangles — strictly more conservative than DP's per-face culling. A camera outside
/// the tree / in solid (cluster −1), a cell with no recorded clusters, or <c>r_pvs_cull 0</c> all degrade to
/// "everything visible" (today's behavior). Nothing DP would draw is ever hidden.</para>
/// </summary>
public sealed partial class WorldPvsCuller : Node
{
    private readonly BspPvs _pvs;
    private readonly (MeshInstance3D Node, int[] Clusters)[] _cells;
    private readonly List<int> _viewClusters = new();      // this frame's viewpoint clusters (main + portals)
    private readonly List<int> _lastClusters = new();      // last applied set (sorted); empty = first frame
    private bool _disabled;

    public WorldPvsCuller(BspPvs pvs, List<(MeshInstance3D Node, int[] Clusters)> cells)
    {
        Name = "WorldPvsCuller";
        _pvs = pvs;
        _cells = cells.ToArray();
    }

    // (draws 2026-08-02) Point-visibility service for the other render-side cullers (map ambient emitters,
    // map dynlights): they cache the cluster of their fixed position and ask "does any current viewpoint see
    // it?" per frame — the same conservative PVS contract the world cells use, so a culled emitter/light can
    // never be one a legal viewpoint could see. Instance-scoped like FrameProfiler: one live culler per match.
    public static WorldPvsCuller? Instance { get; private set; }

    /// <summary>True when this frame ran the cull with a usable main-camera cluster — consumers must treat
    /// false as "show everything" (cull disabled, camera in solid, or no probe yet).</summary>
    public bool CullActive { get; private set; }

    /// <summary>The visibility cluster of a Quake-space position (−1 = solid/outside).</summary>
    public int ClusterAt(System.Numerics.Vector3 quakePos) => _pvs.LeafCluster(_pvs.FindLeaf(quakePos));

    /// <summary>Any of this frame's viewpoints (main camera + active portal exits) sees <paramref name="cluster"/>.
    /// Conservative: true when the cull is inactive or the cluster is unknown.</summary>
    public bool ClusterVisibleFromView(int cluster)
    {
        if (!CullActive || cluster < 0)
            return true;
        for (int v = 0; v < _viewClusters.Count; v++)
            if (_viewClusters[v] >= 0 && _pvs.ClustersVisible(_viewClusters[v], cluster))
                return true;
        return false;
    }

    public override void _Ready()
    {
        // Escape hatch for A/B + safety. Deliberately NOT archived (no CvarFlags.Save): a `set r_pvs_cull 0`
        // debug pin must not silently persist into the player's config.cfg.
        VortexArena.Game.Menu.MenuState.Cvars.Register("r_pvs_cull", "1");
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Process(double delta)
    {
        using var _prof = VortexArena.Game.Client.FrameProfiler.Scope("world.pvscull");
        bool enabled = VortexArena.Game.Menu.MenuState.Cvars.GetFloat("r_pvs_cull") != 0f;
        if (!enabled)
        {
            if (!_disabled)
            {
                // Turned off: restore everything once, then idle until re-enabled.
                foreach ((MeshInstance3D node, _) in _cells)
                    if (GodotObject.IsInstanceValid(node))
                        node.Visible = true;
                _disabled = true;
                _lastClusters.Clear();
            }
            CullActive = false;
            return;
        }
        _disabled = false;

        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null)
        {
            CullActive = false;
            return;
        }

        // Viewpoint set: the main camera + every ACTIVE portal exit view (see PortalRenderer.ActiveExitViewsQuake
        // — Visible=false applies to EVERY viewport sharing the World3D, so the exit room's cells must be kept
        // visible whenever a portal renders it, or the portal image is black). A portal exit view whose point sits
        // in solid (cluster < 0) is SKIPPED, not show-all — only the MAIN camera degrades to show-everything.
        System.Numerics.Vector3 quakeEye = Coords.ToQuake(cam.GlobalPosition);
        int cluster = _pvs.LeafCluster(_pvs.FindLeaf(quakeEye));
        // The point-visibility service (emitters/dynlights) mirrors the cells' contract: active only with a
        // usable MAIN cluster — in-solid degrades every consumer to show-everything, same as the cells below.
        CullActive = cluster >= 0;
        _viewClusters.Clear();
        _viewClusters.Add(cluster);
        var portalViews = VortexArena.Game.Client.PortalRenderer.ActiveExitViewsQuake;
        for (int i = 0; i < portalViews.Count; i++)
        {
            int pc = _pvs.LeafCluster(_pvs.FindLeaf(portalViews[i]));
            if (pc >= 0 && !_viewClusters.Contains(pc))
                _viewClusters.Add(pc);
        }
        _viewClusters.Sort();
        if (_viewClusters.Count == _lastClusters.Count)
        {
            bool same = true;
            for (int i = 0; i < _viewClusters.Count; i++)
                if (_viewClusters[i] != _lastClusters[i]) { same = false; break; }
            if (same)
                return;   // same viewpoint-cluster set → same visible set; nothing to re-apply
        }
        _lastClusters.Clear();
        _lastClusters.AddRange(_viewClusters);

        bool showAll = cluster < 0;   // MAIN camera in solid / outside the tree → conservative: show everything
        foreach ((MeshInstance3D node, int[] clusters) in _cells)
        {
            bool visible = showAll || clusters.Length == 0;
            if (!visible)
            {
                foreach (int c in clusters)
                {
                    for (int v = 0; v < _viewClusters.Count && !visible; v++)
                        if (_viewClusters[v] >= 0 && _pvs.ClustersVisible(_viewClusters[v], c))
                            visible = true;
                    if (visible) break;
                }
            }
            if (GodotObject.IsInstanceValid(node))
                node.Visible = visible;
        }
    }
}

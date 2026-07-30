using System.Numerics;

namespace VortexArena.Server.Bot;

/// <summary>
/// Undo/redo for waypoint editing (design doc §11.9).
///
/// Separate from the geometry journal on purpose. <see cref="VortexArena.Formats.Vmap.VmapEditSession"/>
/// journals a <c>VmapDocument</c>, and the waypoint graph is not in one — it lives on the server, is what
/// bots path against, and is saved to its own Base-compatible files. Forcing it into the document journal
/// would mean either moving waypoints into the .vmap (a format change, and a compatibility break with
/// upstream tooling) or teaching the geometry ops about a graph they otherwise never touch.
///
/// Snapshot-based, like the geometry journal and for the same reason: <see cref="WaypointNetwork.AutoLink"/>
/// re-derives every link through a tracewalk, so "undo the relink" has no inverse to apply — only a previous
/// state to restore. Graphs are a few hundred nodes, so a snapshot is cheap.
/// </summary>
public sealed class WaypointJournal
{
    /// <summary>One captured graph state and what produced it.</summary>
    private readonly record struct Entry(string Label, List<Snapshot> Nodes);

    /// <summary>
    /// A node, flattened. Links are stored as INDICES rather than references because the references point at
    /// objects the undo is about to replace — restoring by reference would rebuild a graph wired to the nodes
    /// that were thrown away.
    /// </summary>
    private readonly record struct Snapshot(
        Vector3 Origin, Vector3 Mins, Vector3 Maxs, WaypointFlags Flags, float Danger,
        List<(int To, float Cost)> Links);

    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();

    /// <summary>Retained steps. Lower than the geometry limit: a graph snapshot is bigger than a brush.</summary>
    public int UndoLimit { get; set; } = 64;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Label of the next undo step, for the HUD.</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>True when the graph has edits not yet written to disk.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Forget everything — a new map, or a fresh load.</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        IsDirty = false;
    }

    /// <summary>
    /// Run <paramref name="edit"/> against <paramref name="net"/>, capturing the state BEFORE it so the change
    /// can be rolled back. Returns whatever the edit returned; a false result journals nothing, so a refused
    /// edit does not leave an empty step in the list.
    /// </summary>
    public bool Apply(WaypointNetwork net, string label, Func<bool> edit)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(edit);

        List<Snapshot> before = Capture(net);
        if (!edit())
            return false;

        _undo.Add(new Entry(label, before));
        if (_undo.Count > UndoLimit)
            _undo.RemoveAt(0);
        _redo.Clear();
        IsDirty = true;
        return true;
    }

    /// <summary>Roll back the most recent waypoint edit.</summary>
    public bool Undo(WaypointNetwork net)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (_undo.Count == 0)
            return false;

        Entry e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new Entry(e.Label, Capture(net)));
        Restore(net, e.Nodes);
        IsDirty = true;
        return true;
    }

    /// <summary>Re-apply the most recently undone waypoint edit.</summary>
    public bool Redo(WaypointNetwork net)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (_redo.Count == 0)
            return false;

        Entry e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new Entry(e.Label, Capture(net)));
        Restore(net, e.Nodes);
        IsDirty = true;
        return true;
    }

    /// <summary>Mark the graph as written to disk.</summary>
    public void MarkSaved() => IsDirty = false;

    private static List<Snapshot> Capture(WaypointNetwork net)
    {
        // Index by identity first, so link targets can be flattened to positions in this same list.
        var indexOf = new Dictionary<Waypoint, int>(net.Nodes.Count);
        for (int i = 0; i < net.Nodes.Count; i++)
            indexOf[net.Nodes[i]] = i;

        var shot = new List<Snapshot>(net.Nodes.Count);
        foreach (Waypoint wp in net.Nodes)
        {
            var links = new List<(int, float)>(wp.Links.Count);
            foreach (WaypointLink link in wp.Links)
                if (indexOf.TryGetValue(link.To, out int to))
                    links.Add((to, link.Cost));
            shot.Add(new Snapshot(wp.Origin, wp.Mins, wp.Maxs, wp.Flags, wp.Danger, links));
        }
        return shot;
    }

    private static void Restore(WaypointNetwork net, List<Snapshot> shot)
    {
        net.ReplaceAll(shot.Count,
            (i, wp) =>
            {
                Snapshot s = shot[i];
                wp.Origin = s.Origin;
                wp.Mins = s.Mins;
                wp.Maxs = s.Maxs;
                wp.Flags = s.Flags;
                wp.Danger = s.Danger;
            },
            (i, wp, nodes) =>
            {
                foreach ((int to, float cost) in shot[i].Links)
                    if (to >= 0 && to < nodes.Count)
                        wp.Links.Add(new WaypointLink(nodes[to], cost));
            });
    }
}

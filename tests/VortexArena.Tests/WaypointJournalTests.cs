using System.Numerics;
using VortexArena.Server.Bot;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers <see cref="WaypointJournal"/> — undo/redo for the waypoint graph.
///
/// The graph is not in the document, so it cannot ride the geometry journal; this is its own snapshot journal.
/// Snapshot rather than inverse-op for the same reason the geometry one is: <see cref="WaypointNetwork.AutoLink"/>
/// re-derives every link through a tracewalk, so a relink has no inverse to apply — only a previous state.
///
/// The link-restoration tests are the ones with teeth. Links hold REFERENCES, and a restore rebuilds the node
/// objects, so anything that captured references instead of indices would come back wired to nodes that were
/// thrown away — a graph that looks right and paths into freed objects.
/// </summary>
public class WaypointJournalTests
{
    private static WaypointNetwork Net(params Vector3[] points)
    {
        var net = new WaypointNetwork();
        foreach (Vector3 p in points)
            net.Add(p);
        return net;
    }

    private static readonly Vector3 A = new(0, 0, 0);
    private static readonly Vector3 B = new(128, 0, 0);
    private static readonly Vector3 C = new(256, 0, 0);

    // ---------------------------------------------------------------- basics

    [Fact]
    public void AFreshJournalHasNothingToUndo()
    {
        var j = new WaypointJournal();
        Assert.False(j.CanUndo);
        Assert.False(j.CanRedo);
        Assert.False(j.IsDirty);
        Assert.False(j.Undo(Net()));
    }

    [Fact]
    public void UndoingAPlaceRemovesIt()
    {
        WaypointNetwork net = Net(A);
        var j = new WaypointJournal();

        Assert.True(j.Apply(net, "place", () => { WaypointEditor.Place(net, B); return true; }));
        Assert.Equal(2, net.Nodes.Count);

        Assert.True(j.Undo(net));
        Assert.Single(net.Nodes);
        Assert.Equal(A, net.Nodes[0].Origin);
    }

    [Fact]
    public void UndoingARemoveBringsItBack()
    {
        WaypointNetwork net = Net(A, B);
        var j = new WaypointJournal();

        Assert.True(j.Apply(net, "remove", () => WaypointEditor.Remove(net, net.Nodes[1])));
        Assert.Single(net.Nodes);

        Assert.True(j.Undo(net));
        Assert.Equal(2, net.Nodes.Count);
        Assert.Equal(B, net.Nodes[1].Origin);
    }

    [Fact]
    public void RedoReappliesTheEdit()
    {
        WaypointNetwork net = Net(A);
        var j = new WaypointJournal();

        j.Apply(net, "place", () => { WaypointEditor.Place(net, B); return true; });
        j.Undo(net);
        Assert.Single(net.Nodes);

        Assert.True(j.Redo(net));
        Assert.Equal(2, net.Nodes.Count);
    }

    [Fact]
    public void ARefusedEditJournalsNothing()
    {
        WaypointNetwork net = Net(A);
        var j = new WaypointJournal();

        Assert.False(j.Apply(net, "nope", () => false));
        Assert.False(j.CanUndo);
    }

    [Fact]
    public void EditingAfterUndoingClearsTheRedoStack()
    {
        WaypointNetwork net = Net(A);
        var j = new WaypointJournal();

        j.Apply(net, "one", () => { WaypointEditor.Place(net, B); return true; });
        j.Undo(net);
        Assert.True(j.CanRedo);

        j.Apply(net, "two", () => { WaypointEditor.Place(net, C); return true; });
        Assert.False(j.CanRedo);
    }

    // ---------------------------------------------------------------- links

    /// <summary>
    /// Links hold references and a restore rebuilds the nodes, so a snapshot that captured references would
    /// come back pointing at objects that are no longer in the graph.
    /// </summary>
    [Fact]
    public void RestoredLinksPointAtTheRestoredNodes()
    {
        WaypointNetwork net = Net(A, B, C);
        net.Link(net.Nodes[0], net.Nodes[1]);
        net.Link(net.Nodes[1], net.Nodes[2]);

        var j = new WaypointJournal();
        j.Apply(net, "remove", () => WaypointEditor.Remove(net, net.Nodes[1]));
        Assert.True(j.Undo(net));

        Assert.Equal(3, net.Nodes.Count);
        foreach (Waypoint wp in net.Nodes)
            foreach (WaypointLink link in wp.Links)
                Assert.Contains(link.To, net.Nodes);      // identity, not just position
    }

    [Fact]
    public void RestoredLinkTopologyMatchesWhatWasCaptured()
    {
        WaypointNetwork net = Net(A, B, C);
        net.Link(net.Nodes[0], net.Nodes[1]);
        net.Link(net.Nodes[1], net.Nodes[2]);
        net.Link(net.Nodes[2], net.Nodes[0]);

        var j = new WaypointJournal();
        j.Apply(net, "wipe", () =>
        {
            foreach (Waypoint wp in net.Nodes)
                wp.Links.Clear();
            return true;
        });
        Assert.Empty(net.Nodes[0].Links);

        Assert.True(j.Undo(net));
        Assert.Single(net.Nodes[0].Links);
        Assert.Same(net.Nodes[1], net.Nodes[0].Links[0].To);
        Assert.Same(net.Nodes[2], net.Nodes[1].Links[0].To);
        Assert.Same(net.Nodes[0], net.Nodes[2].Links[0].To);
    }

    [Fact]
    public void RestoredNodesAreDenselyIndexed()
    {
        WaypointNetwork net = Net(A, B, C);
        var j = new WaypointJournal();

        j.Apply(net, "remove", () => WaypointEditor.Remove(net, net.Nodes[0]));
        j.Undo(net);

        for (int i = 0; i < net.Nodes.Count; i++)
            Assert.Equal(i, net.Nodes[i].Index);
    }

    /// <summary>Pathfinding must work after an undo, which is what the dense index is for.</summary>
    [Fact]
    public void PathfindingWorksAfterAnUndo()
    {
        WaypointNetwork net = Net(A, B, C);
        for (int i = 0; i < 2; i++)
            net.Link(net.Nodes[i], net.Nodes[i + 1], bidirectional: true);

        var j = new WaypointJournal();
        j.Apply(net, "remove", () => WaypointEditor.Remove(net, net.Nodes[2]));
        Assert.True(j.Undo(net));

        List<Waypoint>? path = net.FindPath(net.Nodes[0], net.Nodes[2]);
        Assert.NotNull(path);
        Assert.Same(net.Nodes[2], path![^1]);
    }

    [Fact]
    public void FlagsAndBoxExtentsSurviveARoundTrip()
    {
        var net = new WaypointNetwork();
        net.Add(A, new Vector3(-32, -32, 0), new Vector3(32, 32, 64), WaypointFlags.Teleport);

        var j = new WaypointJournal();
        j.Apply(net, "remove", () => WaypointEditor.Remove(net, net.Nodes[0]));
        Assert.True(j.Undo(net));

        Waypoint wp = Assert.Single(net.Nodes);
        Assert.True(wp.HasFlag(WaypointFlags.Teleport));
        Assert.True(wp.IsBox);
        Assert.Equal(new Vector3(32, 32, 64), wp.Maxs);
    }

    // ---------------------------------------------------------------- dirty tracking

    [Fact]
    public void EditingMarksDirty_AndSavingClearsIt()
    {
        WaypointNetwork net = Net(A);
        var j = new WaypointJournal();

        j.Apply(net, "place", () => { WaypointEditor.Place(net, B); return true; });
        Assert.True(j.IsDirty);

        j.MarkSaved();
        Assert.False(j.IsDirty);

        j.Undo(net);
        Assert.True(j.IsDirty);   // undoing back past a save is still a change against what is on disk
    }

    [Fact]
    public void TheUndoLabelNamesTheStep()
    {
        WaypointNetwork net = Net(A);
        var j = new WaypointJournal();
        j.Apply(net, "Place waypoint", () => { WaypointEditor.Place(net, B); return true; });
        Assert.Equal("Place waypoint", j.UndoLabel);
    }

    [Fact]
    public void OldStepsFallOffTheBottom()
    {
        WaypointNetwork net = Net(A);
        var j = new WaypointJournal { UndoLimit = 4 };

        for (int i = 0; i < 10; i++)
            j.Apply(net, $"step {i}", () => { WaypointEditor.Place(net, B); return true; });

        int undone = 0;
        while (j.Undo(net))
            undone++;
        Assert.Equal(4, undone);
    }
}

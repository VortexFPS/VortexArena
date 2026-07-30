using System.Numerics;
using VortexArena.Server.Bot;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers <see cref="WaypointEditor"/> (phase E8) — the editing half of Base's <c>wpeditor</c>, over the
/// already-ported waypoint runtime.
///
/// The removal tests carry the weight. A link is a reference held by the SOURCE node, and
/// <see cref="Waypoint.Index"/> is a dense index the A* uses to size its score arrays, so a delete that only
/// drops the node from the list leaves neighbours routing into an object that is no longer in the graph and
/// leaves every later node's index pointing past the end. Neither shows up until a bot paths through it.
/// </summary>
public class WaypointEditorTests
{
    private static WaypointNetwork Net(params Vector3[] points)
    {
        var net = new WaypointNetwork();
        foreach (Vector3 p in points)
            net.Add(p);
        return net;
    }

    // ---------------------------------------------------------------- picking

    [Fact]
    public void PickFindsTheNearestWaypointInRange()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(128, 0, 0));
        Waypoint? hit = WaypointEditor.Pick(net, new Vector3(10, 0, 0));

        Assert.NotNull(hit);
        Assert.Equal(0, hit!.Index);
    }

    [Fact]
    public void PickMissesWhenNothingIsClose()
        => Assert.Null(WaypointEditor.Pick(Net(Vector3.Zero), new Vector3(4096, 0, 0)));

    [Fact]
    public void PickPrefersTheCloserOfTwo()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(40, 0, 0));
        Assert.Equal(1, WaypointEditor.Pick(net, new Vector3(38, 0, 0))!.Index);
    }

    // ---------------------------------------------------------------- placing

    [Fact]
    public void PlaceAddsANodeAtThePoint()
    {
        var net = new WaypointNetwork();
        Waypoint wp = WaypointEditor.Place(net, new Vector3(64, 128, 32));

        Assert.Single(net.Nodes);
        Assert.Equal(new Vector3(64, 128, 32), wp.Origin);
        Assert.Equal(0, wp.Index);
    }

    [Theory]
    [InlineData(WaypointFlags.Jump, "jump")]
    [InlineData(WaypointFlags.Crouch, "crouch")]
    [InlineData(WaypointFlags.Support, "support")]
    public void PlaceCarriesTheKind(WaypointFlags flags, string label)
    {
        var net = new WaypointNetwork();
        Waypoint wp = WaypointEditor.Place(net, Vector3.Zero, flags);

        Assert.True(wp.HasFlag(flags));
        Assert.Contains(label, WaypointEditor.Describe(wp));
    }

    // ---------------------------------------------------------------- removal

    [Fact]
    public void RemoveDropsTheNode()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(128, 0, 0));
        Assert.True(WaypointEditor.Remove(net, net.Nodes[0]));
        Assert.Single(net.Nodes);
    }

    /// <summary>
    /// The one that matters: a neighbour must not be left holding a link to a waypoint that is gone.
    /// </summary>
    [Fact]
    public void RemoveUnhooksIncomingLinks()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(128, 0, 0), new Vector3(256, 0, 0));
        net.Link(net.Nodes[0], net.Nodes[1]);
        net.Link(net.Nodes[2], net.Nodes[1]);
        Assert.Single(net.Nodes[0].Links);

        Waypoint doomed = net.Nodes[1];
        Assert.True(WaypointEditor.Remove(net, doomed));

        foreach (Waypoint wp in net.Nodes)
            Assert.DoesNotContain(wp.Links, l => ReferenceEquals(l.To, doomed));
    }

    /// <summary>
    /// Index is a DENSE index into Nodes that the A* uses to size its score arrays, so a removal that does not
    /// reindex leaves later nodes addressing past the end of those arrays.
    /// </summary>
    [Fact]
    public void RemoveReindexesTheRemainingNodes()
    {
        WaypointNetwork net = Net(
            new Vector3(0, 0, 0), new Vector3(128, 0, 0), new Vector3(256, 0, 0), new Vector3(384, 0, 0));

        Assert.True(WaypointEditor.Remove(net, net.Nodes[1]));

        for (int i = 0; i < net.Nodes.Count; i++)
            Assert.Equal(i, net.Nodes[i].Index);
        Assert.Equal(3, net.Nodes.Count);
    }

    [Fact]
    public void RemovingSomethingNotInTheGraph_IsRefused()
    {
        WaypointNetwork net = Net(Vector3.Zero);
        var stranger = new Waypoint { Origin = Vector3.Zero };
        Assert.False(WaypointEditor.Remove(net, stranger));
        Assert.Single(net.Nodes);
    }

    /// <summary>Pathfinding must still work after an edit — the point of reindexing.</summary>
    [Fact]
    public void PathfindingSurvivesARemoval()
    {
        WaypointNetwork net = Net(
            new Vector3(0, 0, 0), new Vector3(128, 0, 0), new Vector3(256, 0, 0), new Vector3(384, 0, 0));
        for (int i = 0; i < 3; i++)
            net.Link(net.Nodes[i], net.Nodes[i + 1], bidirectional: true);

        // Remove a leaf, then path across what is left.
        Assert.True(WaypointEditor.Remove(net, net.Nodes[3]));

        List<Waypoint>? path = net.FindPath(net.Nodes[0], net.Nodes[2]);
        Assert.NotNull(path);
        Assert.Equal(net.Nodes[2], path![^1]);
    }

    // ---------------------------------------------------------------- links

    [Fact]
    public void LinkPendingConnectsTheTwo()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(128, 0, 0));
        Assert.True(WaypointEditor.LinkPending(net, net.Nodes[0], net.Nodes[1]));
        Assert.Contains(net.Nodes[0].Links, l => ReferenceEquals(l.To, net.Nodes[1]));
    }

    [Fact]
    public void LinkPendingRefusesDegeneratePairs()
    {
        WaypointNetwork net = Net(Vector3.Zero);
        Assert.False(WaypointEditor.LinkPending(net, net.Nodes[0], net.Nodes[0]));
        Assert.False(WaypointEditor.LinkPending(net, net.Nodes[0], null!));
    }

    /// <summary>
    /// A SUPPORT waypoint exists to force traffic through a chosen route, which it does by stripping the
    /// destination's OTHER incoming links. Without that it would be an ordinary link and change nothing.
    /// </summary>
    [Fact]
    public void ASupportLinkStripsTheDestinationsOtherIncomingLinks()
    {
        var net = new WaypointNetwork();
        Waypoint support = WaypointEditor.Place(net, new Vector3(0, 0, 0), WaypointFlags.Support);
        Waypoint dest = WaypointEditor.Place(net, new Vector3(128, 0, 0));
        Waypoint other = WaypointEditor.Place(net, new Vector3(256, 0, 0));

        net.Link(other, dest);
        Assert.Contains(other.Links, l => ReferenceEquals(l.To, dest));

        Assert.True(WaypointEditor.LinkPending(net, support, dest));

        Assert.Contains(support.Links, l => ReferenceEquals(l.To, dest));
        Assert.DoesNotContain(other.Links, l => ReferenceEquals(l.To, dest));
    }

    [Fact]
    public void AnOrdinaryLinkLeavesOtherIncomingLinksAlone()
    {
        var net = new WaypointNetwork();
        Waypoint from = WaypointEditor.Place(net, new Vector3(0, 0, 0));
        Waypoint dest = WaypointEditor.Place(net, new Vector3(128, 0, 0));
        Waypoint other = WaypointEditor.Place(net, new Vector3(256, 0, 0));
        net.Link(other, dest);

        Assert.True(WaypointEditor.LinkPending(net, from, dest));
        Assert.Contains(other.Links, l => ReferenceEquals(l.To, dest));
    }

    /// <summary>
    /// The hardwired marker has to go on BEFORE the link, because the save writers key the hardwired file off
    /// it — flagged after the fact, the link is written into the ordinary link cache and lost on the next
    /// relink, which is exactly the work a mapper hardwired it to protect.
    /// </summary>
    [Fact]
    public void HardwireMarksTheSourceAndLinks()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(512, 0, 0));
        WaypointEditor.Hardwire(net, net.Nodes[0], net.Nodes[1]);

        Assert.True(net.Nodes[0].HasFlag(WaypointFlags.CustomJp));
        Assert.Contains(net.Nodes[0].Links, l => ReferenceEquals(l.To, net.Nodes[1]));
        Assert.Contains("hardwired", WaypointEditor.Describe(net.Nodes[0]));
    }

    [Fact]
    public void HardwiredLinksAppearInTheHardwiredFile()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(512, 0, 0));
        WaypointEditor.Hardwire(net, net.Nodes[0], net.Nodes[1]);

        Assert.False(string.IsNullOrWhiteSpace(net.SaveHardwiredLinksToText()));
    }

    // ---------------------------------------------------------------- diagnostics

    [Fact]
    public void UnreachableFindsNodesWithNoWayIn()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(128, 0, 0), new Vector3(256, 0, 0));
        net.Link(net.Nodes[0], net.Nodes[1], bidirectional: true);
        // Node 2 has neither incoming nor outgoing links.

        List<Waypoint> bad = WaypointEditor.Unreachable(net);
        Assert.Contains(net.Nodes[2], bad);
    }

    [Fact]
    public void UnreachableFindsDeadEnds()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(128, 0, 0));
        net.Link(net.Nodes[0], net.Nodes[1]);   // one-way in, nothing out

        List<Waypoint> bad = WaypointEditor.Unreachable(net);
        Assert.Contains(net.Nodes[1], bad);     // no outgoing link
        Assert.Contains(net.Nodes[0], bad);     // no incoming link
    }

    [Fact]
    public void AFullyConnectedPairIsClean()
    {
        WaypointNetwork net = Net(new Vector3(0, 0, 0), new Vector3(128, 0, 0));
        net.Link(net.Nodes[0], net.Nodes[1], bidirectional: true);
        Assert.Empty(WaypointEditor.Unreachable(net));
    }

    // ---------------------------------------------------------------- saving

    [Fact]
    public void EditsAppearInTheSavedNodeFile()
    {
        var net = new WaypointNetwork();
        WaypointEditor.Place(net, new Vector3(64, 128, 32));
        WaypointEditor.Place(net, new Vector3(-64, 0, 16), WaypointFlags.Crouch);

        string text = net.SaveToText(time: "0");
        Assert.Contains("64", text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    /// <summary>A placed-then-removed waypoint must not survive into the file.</summary>
    [Fact]
    public void ARemovedWaypointIsNotSaved()
    {
        var net = new WaypointNetwork();
        WaypointEditor.Place(net, new Vector3(4242, 0, 0));
        Waypoint doomed = WaypointEditor.Place(net, new Vector3(9999, 0, 0));
        WaypointEditor.Remove(net, doomed);

        Assert.DoesNotContain("9999", net.SaveToText(time: "0"));
    }
}

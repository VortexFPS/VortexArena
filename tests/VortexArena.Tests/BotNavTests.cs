using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server.Bot;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.14: waypoint auto-generation from map entities (items / spawns / teleporters → a navigable
/// graph) and the per-gametype bot objective-role dispatch (now incl. Nexball + Assault).
/// </summary>
[Collection("GlobalState")]
public class BotNavTests
{
    public BotNavTests() => Api.Services = new EngineServices(new CollisionWorld());

    [Fact]
    public void AutoGenerate_MakesWaypointsForItemsSpawnsAndTeleporters()
    {
        var es = (EngineServices)Api.Services!;

        // an item, a spawn point, and a teleporter trigger → destination.
        Entity item = es.EntityTable.Spawn(); item.ClassName = "item_health_medium"; item.Flags = EntFlags.Item; item.Origin = new Vector3(100, 0, 0);
        Entity spawn = es.EntityTable.Spawn(); spawn.ClassName = "info_player_deathmatch"; spawn.Origin = new Vector3(0, 200, 0);
        Entity dst = es.EntityTable.Spawn(); dst.ClassName = "info_teleport_destination"; dst.TargetName = "tdest"; dst.Origin = new Vector3(500, 500, 0);
        Entity tele = es.EntityTable.Spawn(); tele.ClassName = "trigger_teleport"; tele.Target = "tdest"; tele.Origin = new Vector3(-100, 0, 0);
        tele.Mins = new Vector3(-32, -32, -32); tele.Maxs = new Vector3(32, 32, 32);

        var net = new WaypointNetwork();
        int n = net.GenerateFromEntities(es.EntityTable.All, autoLink: false);

        Assert.True(n >= 4); // item + spawn + teleporter box + teleport destination
        // the teleporter box waypoint carries the Teleport flag and a one-way link to its destination.
        Waypoint? teleWp = null;
        foreach (var w in net.Nodes) if (w.HasFlag(WaypointFlags.Teleport)) { teleWp = w; break; }
        Assert.NotNull(teleWp);
        Assert.NotEmpty(teleWp!.Links);
    }

    [Fact]
    public void LoadFromText_ParsesQuotedVtosVectors_IncludingLeadingSpaceComponents()
    {
        // The shipped .waypoints/.waypoints.cache files single-quote each vector (DP vtos), and vtos
        // right-aligns components so a small value carries a leading space inside the quotes
        // (' 46.8 ...'). Both forms must parse — the regression that broke this dropped the whole graph to
        // zero nodes and forced the expensive AutoLink tracewalk fallback.
        const string wp =
            "//WAYPOINT_VERSION 1.04\n" +
            "//WAYPOINT_SYMMETRY 0\n" +
            "'-507.0 1112.6 409.0'\n'-507.0 1112.6 409.0'\n0\n" +     // normal negative component
            "' 46.8 -380.6 536.0'\n' 46.8 -380.6 536.0'\n0\n" +       // leading-space (right-aligned) component
            "'1298.4 -59.8 217.0'\n'1298.4 -59.8 217.0'\n16384\n";    // a jump waypoint (flag 1<<14)

        var net = WaypointNetwork.LoadFromText(wp);
        Assert.Equal(3, net.Count);
        Assert.Equal(new Vector3(46.8f, -380.6f, 536.0f), net.Nodes[1].Origin);
        Assert.True(net.Nodes[2].HasFlag(WaypointFlags.Jump));

        // the .cache link form is 'from'*'to' with the same quoting; loading it must add the edge.
        net.LoadLinks("//WAYPOINT_VERSION 1.04\n'-507.0 1112.6 409.0'*' 46.8 -380.6 536.0'\n");
        Assert.Single(net.Nodes[0].Links);
        Assert.Same(net.Nodes[1], net.Nodes[0].Links[0].To);
    }

    [Fact]
    public void LoadLinks_ResolvesEveryEndpoint_AcrossSpatialHashCells()
    {
        // FindAt (the cache→node match) is now an O(1) spatial-hash probe instead of an O(nodes) scan — the fix
        // for the first-bot-frame waypoint-load stutter (loading catharsis's 76 KB cache was ~2M distance checks
        // in one frame). This pins that the optimized lookup still resolves EVERY endpoint, including nodes that
        // land in different grid cells and ones sitting right on a cell boundary (the 27-cell probe must cover
        // the neighbour cells). A miss would silently drop links → broken bot routing.
        var sb = new System.Text.StringBuilder("//WAYPOINT_VERSION 1.04\n//WAYPOINT_SYMMETRY 0\n");
        var pts = new System.Collections.Generic.List<Vector3>();
        // a 6×6 grid at 7u spacing (> the 5u hash cell, so neighbours span multiple cells) plus a few points
        // deliberately near cell edges (multiples of 5 ± a hair) to exercise the boundary probe.
        for (int gx = 0; gx < 6; gx++)
            for (int gy = 0; gy < 6; gy++)
                pts.Add(new Vector3(gx * 7f, gy * 7f, 0f));
        pts.Add(new Vector3(4.9f, 5.1f, 0f));   // straddles the x=5 / y=5 cell boundary
        pts.Add(new Vector3(10.0f, 0.0f, 9.99f)); // straddles z=10
        foreach (var p in pts)
            sb.Append('\'').Append(Q(p)).Append("'\n'").Append(Q(p)).Append("'\n0\n");

        var net = WaypointNetwork.LoadFromText(sb.ToString());
        Assert.Equal(pts.Count, net.Count);

        // Link a chain through every node (i→i+1); a dropped endpoint match would lose that node's edge.
        var cache = new System.Text.StringBuilder("//WAYPOINT_VERSION 1.04\n");
        for (int i = 0; i + 1 < pts.Count; i++)
            cache.Append('\'').Append(Q(pts[i])).Append("'*'").Append(Q(pts[i + 1])).Append("'\n");
        net.LoadLinks(cache.ToString());

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Assert.Single(net.Nodes[i].Links);
            Assert.Same(net.Nodes[i + 1], net.Nodes[i].Links[0].To);
        }
        Assert.Empty(net.Nodes[pts.Count - 1].Links); // last node is only a target, never a source

        // CROSS-CELL probe: a node sitting at a cell edge (x=5.0 ⇒ cell 1) linked from an endpoint 0.4u away on
        // the other side of the boundary (x=4.6 ⇒ cell 0). Distance 0.4 < the 1u match radius, but the two are in
        // different grid cells — only the 3×3×3 neighbour probe finds it. The hand-authored .hardwired files
        // (matched within 5u) rely on exactly this. A 1-cell-only lookup would silently drop the link.
        var net2 = WaypointNetwork.LoadFromText(
            "//WAYPOINT_VERSION 1.04\n'0.0 0.0 0.0'\n'0.0 0.0 0.0'\n0\n'5.0 0.0 0.0'\n'5.0 0.0 0.0'\n0\n");
        net2.LoadLinks("//WAYPOINT_VERSION 1.04\n'0.0 0.0 0.0'*'4.6 0.0 0.0'\n"); // 4.6 must resolve to the 5.0 node
        Assert.Single(net2.Nodes[0].Links);
        Assert.Same(net2.Nodes[1], net2.Nodes[0].Links[0].To);

        // local helper: format a vector the way the shipped files do (vtos, 1 decimal place).
        static string Q(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.0} {1:0.0} {2:0.0}", v.X, v.Y, v.Z);
    }

    [Fact]
    public void RoleDispatch_CoversNexballAndAssault()
    {
        Assert.Equal("RoleNexball", BotRoles.ChooseRole("nexball").Method.Name);
        Assert.Equal("RoleNexball", BotRoles.ChooseRole("nb").Method.Name);
        Assert.Equal("RoleAssault", BotRoles.ChooseRole("assault").Method.Name);
        Assert.Equal("RoleAssault", BotRoles.ChooseRole("as").Method.Name);
        // existing modes still dispatch
        Assert.Equal("RoleCtf", BotRoles.ChooseRole("ctf").Method.Name);
    }
}

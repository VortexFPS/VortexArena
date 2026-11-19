using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using VortexArena.Server.Bot;
using Xunit;

namespace VortexArena.Tests;

public sealed class LocalDebugLabelTests
{
    [Fact]
    public void EntityAim_IncludesOnlyModeledTriggersAndRespectsWorldOcclusion()
    {
        var collision = new CollisionWorld();
        collision.AddBrush(Brush.FromBox(new Vector3(70, -20, -20), new Vector3(74, 20, 20), SuperContents.Solid));
        var services = new EngineServices(collision);
        Entity invisible = AddTrigger(20, "");
        Entity item = AddTrigger(50, "models/item.md3");
        Entity hidden = AddTrigger(90, "models/hidden.md3");

        Assert.Same(item, Trace(MoveFilter.NormalWithVisibleTriggers));
        Assert.NotSame(item, Trace(MoveFilter.Normal));
        Assert.Equal(Solid.Trigger, item.Solid);
        services.Entities.Remove(item);
        Entity? afterRemove = Trace(MoveFilter.NormalWithVisibleTriggers);
        Assert.NotSame(invisible, afterRemove);
        Assert.NotSame(hidden, afterRemove);

        Entity AddTrigger(int x, string model)
        {
            Entity entity = services.Entities.Spawn();
            entity.Solid = Solid.Trigger;
            entity.Model = model;
            services.Entities.SetSize(entity, new Vector3(-5), new Vector3(5));
            services.Entities.SetOrigin(entity, new Vector3(x, 0, 0));
            return entity;
        }

        Entity? Trace(MoveFilter filter) => services.Trace.Trace(Vector3.Zero, Vector3.Zero, Vector3.Zero,
            new Vector3(128, 0, 0), filter, null).Ent;
    }

    [Fact]
    public void EntityLabel_UsesBoundsCenterAndExpiresWithoutRefreshingOnSameTarget()
    {
        var entity = new Entity
        {
            Index = 12,
            NetName = "Big health",
            ClassName = "item_health_big",
            Origin = new Vector3(10, 0, 2),
            AbsMin = new Vector3(0, -10, 2),
            AbsMax = new Vector3(20, 10, 16),
        };
        var labels = new LocalDebugLabels();

        labels.AimAtEntity(entity, 2);
        labels.AimAtEntity(entity, 4);
        var label = Assert.Single(labels.Labels);
        Assert.Equal(new Vector3(10, 0, 9), label.Position);
        Assert.Equal("^7ent #12\n^8 netname: ^3Big health\n^8 classname: ^5item_health_big\n^8 origin: ^2'10.0 0.0 2.0'",
            label.Text);
        Assert.Equal(0.5f, label.Alpha(5.5));
        labels.Expire(9);
        labels.AimAtEntity(entity, 9.1);
        Assert.Empty(labels.Labels);
        labels.AimAtEntity(null, 9.2);
        labels.AimAtEntity(entity, 9.3);
        Assert.Single(labels.Labels);
    }

    [Fact]
    public void WaypointAim_StopsAtObstructionAndUsesPointAndBoxBounds()
    {
        var net = new WaypointNetwork();
        net.Add(new Vector3(20, 17, 0)); // Just outside the stock point-waypoint cube.
        Waypoint box = net.Add(new Vector3(50, 14, 0), new Vector3(-3, -16, -5), new Vector3(3, 16, 5));
        Waypoint point = net.Add(new Vector3(36, 0, 0));
        Assert.Null(WaypointEditor.PickAimed(net, Vector3.Zero, Vector3.UnitX, 19));
        Assert.Same(point, WaypointEditor.PickAimed(net, Vector3.Zero, Vector3.UnitX, 80));
        net.RemoveNode(point);
        Assert.Null(WaypointEditor.PickAimed(net, Vector3.Zero, Vector3.UnitX, 46));
        Assert.Same(box, WaypointEditor.PickAimed(net, Vector3.Zero, Vector3.UnitX, 55));
    }

    [Fact]
    public void WaypointLabel_ReportsTypeFlagsOriginAndBoxBounds()
    {
        var network = new WaypointNetwork();
        Waypoint box = network.Add(new Vector3(70, 0, 2), new Vector3(-5), new Vector3(5), WaypointFlags.Teleport);
        var jumppad = new Entity
        {
            ClassName = "trigger_push",
            AbsMin = new Vector3(67, -3, 0),
            AbsMax = new Vector3(76, 3, 4),
        };
        var labels = new LocalDebugLabels();

        labels.AimAtWaypoint(box, new[] { jumppad }, 0);
        var label = Assert.Single(labels.Labels);
        Assert.Equal(box.Origin, label.Position);
        Assert.Equal("Entity 0: ^3Jumppad waypoint^7\nflags: 2097152\norigin: '70.0 0.0 2.0'\nabsmin: '65.0 -5.0 -3.0'\nabsmax: '75.0 5.0 7.0'",
            label.Text);
    }
}

using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class ViewLocationBrushTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ViewLocationUsesBrushPlanesUnlessMapAuthorsSupplyBounds(bool authoredBounds, bool quake3Compat)
    {
        var previous = Api.Services;
        var previousCompat = CompatRemaps.Q3CompatProvider;
        var world = new CollisionWorld();
        var services = new EngineServices(world);
        services.TraceImpl.DarkPlacesArithmetic = true;
        var simulation = new SimulationLoop(services, world);
        Api.Services = services;
        CompatRemaps.Q3CompatProvider = () => quake3Compat;
        try
        {
            // Triangular prism x + y <= 64. The far corner of its model bounds is empty.
            var planes = new BrushPlane[]
            {
                new(-Vector3.UnitX, 0), new(Vector3.UnitX, 64),
                new(-Vector3.UnitY, 0), new(Vector3.UnitY, 64),
                new(-Vector3.UnitZ, 0), new(Vector3.UnitZ, 64),
                new(Vector3.Normalize(new Vector3(1, 1, 0)), 64 / MathF.Sqrt(2)),
            };
            var points = new Vector3[]
            {
                Vector3.Zero, new(64, 0, 0), new(0, 64, 0),
                new(0, 0, 64), new(64, 0, 64), new(0, 64, 64),
            };
            var edges = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(new Vector3(1, -1, 0)) };
            var brush = new Brush(planes, points, edges, SuperContents.Solid);
            BspCollisionBuilder.RegisterSubmodels(
                new[] { new BspCollisionBuilder.Submodel("*1", Vector3.Zero, new Vector3(64), new[] { brush }) },
                services.ModelsImpl);

            Entity start = Api.Entities.Spawn();
            start.TargetName = "brush_test_camera";
            ViewLocation.StartSetup(start);
            Entity trigger = Api.Entities.Spawn();
            trigger.Model = "*1";
            trigger.Target = start.TargetName;
            if (authoredBounds)
                Api.Entities.SetSize(trigger, Vector3.Zero, new Vector3(70));
            ViewLocation.TriggerViewLocationSetup(trigger);
            ViewLocation.RunDeferredInit();

            Entity first = AddPlayer(new Vector3(16, 16, 16));
            Entity second = AddPlayer(new Vector3(32, 16, 16));
            simulation.Tick();
            Assert.Same(trigger, first.ViewLoc);
            Assert.Same(trigger, second.ViewLoc);

            // The unexpanded hull misses the sloping face by one unit in x + y.
            Api.Entities.SetOrigin(first, new Vector3(34.5f, 34.5f, 16));
            simulation.Tick();
            Assert.Same(authoredBounds || !quake3Compat ? trigger : null, first.ViewLoc);

            Api.Entities.SetOrigin(first, new Vector3(56, 56, 16));
            simulation.Tick();
            Assert.Same(authoredBounds ? trigger : null, first.ViewLoc);
            Assert.Same(trigger, second.ViewLoc);

            // Only the explicit 70-unit box, followed by QC's one-unit toucher padding, reaches this point.
            Api.Entities.SetOrigin(first, new Vector3(73, 48, 16));
            simulation.Tick();
            Assert.Same(authoredBounds && !quake3Compat ? trigger : null, first.ViewLoc);

            Api.Entities.SetOrigin(second, new Vector3(90, 90, 16));
            Api.Entities.SetOrigin(first, new Vector3(16, 16, 16));
            simulation.Tick();
            Assert.Null(second.ActiveViewLocation);
            Assert.Same(trigger, first.ViewLoc);
        }
        finally
        {
            Api.Services = previous;
            CompatRemaps.Q3CompatProvider = previousCompat;
        }
    }

    private static Entity AddPlayer(Vector3 origin)
    {
        Entity player = Api.Entities.Spawn();
        player.ClassName = "player";
        player.Flags = EntFlags.Client;
        player.DpHitContentsMask = SuperContents.Solid;
        Api.Entities.SetSize(player, new Vector3(-2), new Vector3(2));
        Api.Entities.SetOrigin(player, origin);
        return player;
    }
}

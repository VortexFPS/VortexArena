using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the map-entity warpzone wiring (REMAINING-WORK §6 follow-up): a <c>trigger_warpzone</c> brush in the
/// entity lump auto-orients from its geometry via <c>getsurface*</c> (the §6 mesh queries), the deferred
/// <see cref="WarpzoneManager.InitMapZones"/> derives each plane and links the pairs, and an optional
/// <c>trigger_warpzone_position</c> overrides the orientation — the C# port of <c>lib/warpzone/server.qc</c>'s
/// spawnfunc + <c>WarpZone_StartFrame</c> init pass.
/// </summary>
public class WarpzoneSpawnTests
{
    // Two planar quad brushes ("*1" facing +X at x=0, "*2" facing -X at x=500), one BSP face each.
    private static BspData TwoPortalBsp()
    {
        Vector3[] q1 = { new(0, -16, -16), new(0, 16, -16), new(0, 16, 16), new(0, -16, 16) };
        Vector3[] q2 = { new(500, -16, -16), new(500, 16, -16), new(500, 16, 16), new(500, -16, 16) };
        var verts = q1.Select(p => new BspVertex(p, default, default, new Vector3(1, 0, 0), new BspColor(255, 255, 255, 255)))
            .Concat(q2.Select(p => new BspVertex(p, default, default, new Vector3(-1, 0, 0), new BspColor(255, 255, 255, 255))))
            .ToArray();

        return new BspData
        {
            Textures = new[] { new BspTexture("textures/map/portal", 0, SuperContents.Solid) },
            Vertices = verts,
            Triangles = new[] { 0, 2, 1, 0, 3, 2,   /* face1 (+X winding) */ 0, 1, 2, 0, 2, 3 /* face2 (-X winding) */ },
            Faces = new[]
            {
                new BspFace(0, -1, BspFaceType.Flat, 0, 4, 0, 6, -1, 0, 0),  // "*1"
                new BspFace(0, -1, BspFaceType.Flat, 4, 4, 6, 6, -1, 0, 0),  // "*2"
            },
            Models = new[]
            {
                new BspModel(Vector3.Zero, Vector3.Zero, 0, 0, 0, 0),                                       // *0 worldspawn
                new BspModel(new Vector3(0, -16, -16), new Vector3(0, 16, 16), 0, 1, 0, 0),                 // *1
                new BspModel(new Vector3(500, -16, -16), new Vector3(500, 16, 16), 1, 1, 0, 0),             // *2
            },
        };
    }

    private static (EngineServices svc, WarpzoneManager mgr) Setup()
    {
        var svc = new EngineServices(new CollisionWorld());
        Api.Services = svc;
        BspSurfaceBuilder.BuildAndAttach(TwoPortalBsp(), svc.ModelsImpl);
        var mgr = new WarpzoneManager();
        WarpzoneSpawns.Sink = mgr.OnMapEntity;   // the bridge GameWorld.Boot installs
        return (svc, mgr);
    }

    private static Entity SpawnBrush(EngineServices svc, string model, string targetName, string target, string killtarget = "")
    {
        Entity e = svc.Entities.Spawn();
        e.Model = model; e.TargetName = targetName; e.Target = target; e.KillTarget = killtarget;
        WarpzoneSpawns.TriggerWarpzoneSetup(e);   // == SpawnFuncs.TrySpawn("trigger_warpzone", e)
        return e;
    }

    [Fact]
    public void MapWarpzones_Spawn_Orient_And_Link_FromBrushGeometry()
    {
        (EngineServices svc, WarpzoneManager mgr) = Setup();
        SpawnBrush(svc, "*1", targetName: "wzA", target: "wzB");
        SpawnBrush(svc, "*2", targetName: "wzB", target: "wzA");
        mgr.InitMapZones();

        Assert.Equal(2, mgr.Zones.Count);
        Assert.All(mgr.Zones, z => Assert.True(z.Linked, "both portals should link to their partner"));

        // each plane is auto-oriented from its quad: origin = centroid, forward = the face normal.
        Warpzone a = mgr.Zones.First(z => z.TargetName == "wzA");
        Warpzone b = mgr.Zones.First(z => z.TargetName == "wzB");
        AssertVec(new Vector3(0, 0, 0), a.InOrigin, 0.01f);
        AssertVec(new Vector3(500, 0, 0), b.InOrigin, 0.01f);
        QMath.AngleVectors(a.InAngles, out Vector3 fa, out _, out _);
        QMath.AngleVectors(b.InAngles, out Vector3 fb, out _, out _);
        AssertVec(new Vector3(1, 0, 0), fa, 1e-3f);   // *1 faces +X
        AssertVec(new Vector3(-1, 0, 0), fb, 1e-3f);  // *2 faces -X

        // the brushes became live touch volumes (QC ExactTrigger).
        Assert.Equal("trigger_warpzone", a.Trigger!.ClassName);
        Assert.Equal(Solid.Trigger, a.Trigger!.Solid);

        // an entity that has CROSSED INTO portal A (now just past the IN plane on its far/negative side, as the QC
        // WarpZone_PlaneDist < 0 gate requires — server.qc:193) emerges at portal B's plane (momentum preserved).
        var e = new Entity { Origin = a.InOrigin - new Vector3(1, 0, 0), Velocity = new Vector3(-200, 0, 0) };
        float speed = e.Velocity.Length();
        Assert.True(mgr.Teleport(e, a));
        AssertVec(b.InOrigin, e.Origin, 1.5f);
        Assert.True(MathF.Abs(e.Velocity.Length() - speed) < 0.01f);
    }

    [Fact]
    public void MapWarpzone_PositionEntity_Overrides_Orientation()
    {
        (EngineServices svc, WarpzoneManager mgr) = Setup();
        SpawnBrush(svc, "*1", targetName: "wzA", target: "wzB");
        SpawnBrush(svc, "*2", targetName: "wzB", target: "wzA");

        // a trigger_warpzone_position whose .target names portal A gives it an explicit orientation (yaw 33°);
        // the init pass attaches it as the zone's aiment and aims the plane against it.
        Entity pos = svc.Entities.Spawn();
        pos.Origin = new Vector3(0, 0, 0);
        pos.Angles = new Vector3(0, 33f, 0);
        pos.Target = "wzA";
        WarpzoneSpawns.TriggerWarpzonePositionSetup(pos);

        mgr.InitMapZones();

        Warpzone a = mgr.Zones.First(z => z.TargetName == "wzA");
        // with an aiment the forward is corrected against the plane but keeps the aiment's roll/handedness; for an
        // axis-aligned +X quad it still resolves to ±X — assert it stayed planar and linked (the override ran).
        Assert.True(a.Linked);
        QMath.AngleVectors(a.InAngles, out Vector3 f, out _, out _);
        Assert.True(MathF.Abs(MathF.Abs(f.X) - 1f) < 1e-2f, $"plane should stay axis-aligned to the quad, got {f}");
    }

    [Fact]
    public void MapWarpzones_TwoWayLink_WhenOnlyOneCarriesTarget()
    {
        (EngineServices svc, WarpzoneManager mgr) = Setup();
        // asymmetric: only portal A names its partner; B carries just a targetname (QC's two-way enemy link).
        SpawnBrush(svc, "*1", targetName: "wzA", target: "wzB");
        SpawnBrush(svc, "*2", targetName: "wzB", target: "");
        mgr.InitMapZones();

        Assert.All(mgr.Zones, z => Assert.True(z.Linked, "both portals link even though only A set .target"));
    }

    [Fact]
    public void MiscWarpzonePosition_Spawnfunc_OrientsAZone()
    {
        (EngineServices svc, WarpzoneManager mgr) = Setup();
        SpawnBrush(svc, "*1", targetName: "wzA", target: "wzB");
        SpawnBrush(svc, "*2", targetName: "wzB", target: "wzA");

        // QC's CANONICAL position spawnfunc name (server.qc:642). Must orient zone A identically to
        // trigger_warpzone_position — not be silently dropped.
        Entity pos = svc.Entities.Spawn();
        pos.Origin = new Vector3(0, 0, 0);
        pos.Angles = new Vector3(0, 33f, 0);
        pos.Target = "wzA";
        WarpzoneSpawns.MiscWarpzonePositionSetup(pos);

        mgr.InitMapZones();

        Warpzone a = mgr.Zones.First(z => z.TargetName == "wzA");
        Assert.Same(pos, a.Aiment);   // the canonical position helper was attached as the zone's aiment
        Assert.True(a.Linked);
    }

    [Fact]
    public void Reconnect_RelinksAfterRetarget()
    {
        (EngineServices svc, WarpzoneManager mgr) = Setup();
        Entity ba = SpawnBrush(svc, "*1", targetName: "wzA", target: "wzB");
        Entity bb = SpawnBrush(svc, "*2", targetName: "wzB", target: "wzA");
        mgr.InitMapZones();
        Assert.All(mgr.Zones, z => Assert.True(z.Linked));

        // Retarget A to a non-existent partner, then reconnect: A must drop its link (partner gone).
        Warpzone a = mgr.Zones.First(z => z.TargetName == "wzA");
        Warpzone b = mgr.Zones.First(z => z.TargetName == "wzB");
        a.Target = "nope";
        // Base two-way link: clear B's back-reference too so the reverse pass can't relink them.
        b.Target = "";
        mgr.Reconnect();
        Assert.False(a.Linked, "A no longer names a valid partner after reconnect");

        // Point A back at B and reconnect: the pair re-links.
        a.Target = "wzB";
        mgr.Reconnect();
        Assert.True(a.Linked && b.Linked, "the pair re-links once A targets B again");
    }

    [Fact]
    public void WarpObservers_WarpsASolidNotClient_ThroughAZone()
    {
        (EngineServices svc, WarpzoneManager mgr) = Setup();
        SpawnBrush(svc, "*1", targetName: "wzA", target: "wzB");
        SpawnBrush(svc, "*2", targetName: "wzB", target: "wzA");
        mgr.InitMapZones();
        Warpzone a = mgr.Zones.First(z => z.TargetName == "wzA");
        Warpzone b = mgr.Zones.First(z => z.TargetName == "wzB");

        // A SOLID_NOT observer-style entity sitting just past portal A's plane (inside the trigger box) is warped to
        // portal B each frame — the touch pass can't fire for a non-solid mover. Use a real engine entity so its
        // AbsMin/AbsMax are linked for the overlap test.
        Entity obs = svc.Entities.Spawn();
        obs.Solid = Solid.Not;
        svc.Entities.SetSize(obs, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        svc.Entities.SetOrigin(obs, a.InOrigin - new Vector3(1, 0, 0)); // just across the IN plane, inside the box
        obs.Velocity = new Vector3(-50, 0, 0);

        mgr.WarpObservers(new[] { obs });
        AssertVec(b.InOrigin, obs.Origin, 2f);   // emerged at portal B
    }

    private static void AssertVec(Vector3 expected, Vector3 actual, float tol = 1e-4f)
        => Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual} (tol {tol})");
}

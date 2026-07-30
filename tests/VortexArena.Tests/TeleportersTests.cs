using System.Linq;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Teleporter OUT-DIRECTION + velocity fidelity (qcsrc/common/mapobjects/teleporters.qc Simple_TeleportPlayer /
/// TeleportPlayer + the DarkPlaces ED_ParseEdict "anglehack"). Guards two regressions:
///  1. a teleported player must FACE the destination's mangle and have its speed REPROJECTED along that facing
///     (not keep its entry direction) — including with the KEEP_SPEED spawnflag, which per entities.ent only
///     skips the g_teleport_maxspeed clamp, never the reprojection;
///  2. a destination orientated with the single-float `angle "X"` key (the QuakeEd/real-map convention) must
///     resolve to yaw X, not the default yaw 0 (facing +X). Without the anglehack every stock teleporter fired
///     the player due-east regardless of where the mapper aimed the exit.
///
/// Mutates Api.Services / the MapMover index, so it runs in the serialized GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public sealed class TeleportersTests
{
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new();
        public TestFacade() { Inner = new EngineServices(new CollisionWorld()); }
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    private static TestFacade Boot()
    {
        var f = new TestFacade();
        GameInit.Boot(f);          // Api.Services = f; Movement.System + all map-object spawnfuncs registered
        MapMover.ClearIndex();
        MapObjectsState.Reset();
        f.GameClock.Time = 1f;
        f.GameClock.FrameTime = 1f / 60f;
        return f;
    }

    private static Entity Destination(Vector3 mangle, string targetName)
    {
        Entity dest = Api.Entities.Spawn();
        dest.Angles = mangle;          // info_teleport_destination stores facing in .mangle (set from .angles)
        dest.TargetName = targetName;
        Assert.True(SpawnFuncs.TrySpawn("info_teleport_destination", dest));
        Assert.Equal(mangle, dest.MAngle);
        return dest;
    }

    private static Entity Player(Vector3 velocity)
    {
        Entity p = Api.Entities.Spawn();
        p.ClassName = "player";
        p.Flags = EntFlags.Client;
        p.MoveType = MoveType.Walk;
        p.Mins = new Vector3(-16, -16, -24);
        p.Maxs = new Vector3(16, 16, 45);
        p.Health = 100f;
        Api.Entities.SetOrigin(p, new Vector3(0, 0, 24));
        p.Velocity = velocity;
        return p;
    }

    // =====================================================================================
    //  Simple_TeleportPlayer — face the dest mangle, reproject speed along its forward
    // =====================================================================================

    [Fact]
    public void Teleport_FacesDestinationAndReprojectsVelocityAlongForward()
    {
        Boot();
        var mangle = new Vector3(0, 90, 0);       // yaw 90 -> forward +Y in Quake convention
        Entity dest = Destination(mangle, "td");

        Entity tele = Api.Entities.Spawn();
        tele.ClassName = "trigger_teleport";
        tele.Enemy = dest;                         // single cached destination

        Entity p = Player(new Vector3(300, 0, 0)); // entering at 300 u/s due +X

        Teleporters.SimpleTeleportPlayer(tele, p);

        // Faces the destination.
        Assert.Equal(mangle, p.Angles);

        // Velocity is the entry SPEED reprojected along the destination forward — NOT the entry direction.
        QMath.AngleVectors(mangle, out Vector3 fwd, out _, out _);
        Vector3 expected = fwd * 300f;
        Assert.Equal(expected.X, p.Velocity.X, 2);
        Assert.Equal(expected.Y, p.Velocity.Y, 2);
        Assert.Equal(expected.Z, p.Velocity.Z, 2);
        Assert.Equal(300f, p.Velocity.Length(), 2);   // speed preserved
        Assert.True(p.Velocity.X < 1f, "must no longer be travelling in the entry (+X) direction");
    }

    [Fact]
    public void Teleport_KeepSpeedStillReprojectsDirection()
    {
        Boot();
        var mangle = new Vector3(0, 90, 0);
        Entity dest = Destination(mangle, "td");

        Entity tele = Api.Entities.Spawn();
        tele.ClassName = "trigger_teleport";
        tele.SpawnFlags = Teleporters.KeepSpeed;   // KEEP_SPEED: skips the maxspeed clamp ONLY, not the reprojection
        tele.Enemy = dest;

        Entity p = Player(new Vector3(300, 0, 0));

        Teleporters.SimpleTeleportPlayer(tele, p);

        QMath.AngleVectors(mangle, out Vector3 fwd, out _, out _);
        Vector3 expected = fwd * 300f;
        Assert.Equal(expected.X, p.Velocity.X, 2);
        Assert.Equal(expected.Y, p.Velocity.Y, 2);
        Assert.True(p.Velocity.X < 1f, "KEEP_SPEED must NOT preserve the entry (+X) direction");
    }

    // =====================================================================================
    //  DarkPlaces anglehack — `angle "X"` resolves to yaw X on the spawned edict
    // =====================================================================================

    [Fact]
    public void AngleHack_SingleFloatAngleKeyBecomesYaw()
    {
        var ed = new EntityDict("info_teleport_destination");
        ed.Fields["targetname"] = "td_angle";
        ed.Fields["angle"] = "90";                 // the QuakeEd single-float yaw convention

        var world = new GameWorld(new CollisionWorld(), new[] { ed });
        world.Boot("dm");

        Entity dest = MapMover.FindByTargetName("td_angle").First();
        Assert.Equal(new Vector3(0, 90, 0), dest.MAngle);
    }

    [Fact]
    public void AngleHack_ExplicitAnglesVectorWinsOverAngleKey()
    {
        var ed = new EntityDict("info_teleport_destination", angles: new Vector3(0, 45, 0));
        ed.Fields["targetname"] = "td_vec";
        ed.Fields["angle"] = "90";                 // both present: the explicit vector must win

        var world = new GameWorld(new CollisionWorld(), new[] { ed });
        world.Boot("dm");

        Entity dest = MapMover.FindByTargetName("td_vec").First();
        Assert.Equal(new Vector3(0, 45, 0), dest.MAngle);
    }

    // =====================================================================================
    //  Client prediction — TriggerTouch.PredictTeleportsAmbient relocates + stamps .fixangle
    // =====================================================================================

    /// <summary>Build a single-destination trigger_teleport box at the origin (Enemy cached, Active).</summary>
    private static Entity TeleporterBox(Entity dest)
    {
        Entity tele = Api.Entities.Spawn();
        tele.ClassName = "trigger_teleport";
        tele.Solid = Solid.Trigger;
        tele.Active = MapMover.ActiveActive;
        tele.Enemy = dest;
        tele.Mins = new Vector3(-32, -32, -32);
        tele.Maxs = new Vector3(32, 32, 32);
        tele.Size = tele.Maxs - tele.Mins;
        Api.Entities.SetOrigin(tele, Vector3.Zero); // relink AbsMin/AbsMax to cover the box
        return tele;
    }

    /// <summary>A SOLID_NOT prediction-carrier player overlapping the origin, moving +X.</summary>
    private static Entity Carrier()
    {
        Entity c = Api.Entities.Spawn();
        c.ClassName = "player";
        c.Flags = EntFlags.Client | EntFlags.OnGround;
        c.Solid = Solid.Not;             // the prediction ghost is deliberately non-solid
        c.Mins = new Vector3(-16, -16, -24);
        c.Maxs = new Vector3(16, 16, 45);
        c.Size = c.Maxs - c.Mins;
        Api.Entities.SetOrigin(c, Vector3.Zero);
        c.Velocity = new Vector3(300, 0, 0);
        return c;
    }

    [Fact]
    public void PredictTeleport_RelocatesReprojectsAndStampsFixAngle_NoSideEffects()
    {
        Boot();
        var mangle = new Vector3(0, 90, 0);
        Entity dest = Destination(mangle, "td");
        dest.Origin = new Vector3(1000, 2000, 50);
        Entity tele = TeleporterBox(dest);
        float padLTimeBefore = tele.PushLTime;

        Entity carrier = Carrier();
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictTeleportsAmbient(carrier);

        // Relocated to the destination (with the QC floor-clear nudge 1 - mins.z - 24 = 1).
        Vector3 expectedOrigin = dest.Origin + new Vector3(0, 0, 1f - carrier.Mins.Z - 24f);
        Assert.Equal(expectedOrigin.X, carrier.Origin.X, 2);
        Assert.Equal(expectedOrigin.Y, carrier.Origin.Y, 2);
        Assert.Equal(expectedOrigin.Z, carrier.Origin.Z, 2);

        // Velocity reprojected along the dest forward; ground cleared.
        QMath.AngleVectors(mangle, out Vector3 fwd, out _, out _);
        Vector3 expectedVel = fwd * 300f;
        Assert.Equal(expectedVel.X, carrier.Velocity.X, 2);
        Assert.Equal(expectedVel.Y, carrier.Velocity.Y, 2);
        Assert.False(carrier.OnGround);

        // The view-snap signal (QC .fixangle) is stamped with the destination facing.
        Assert.True(carrier.FixAngle);
        Assert.Equal(mangle, carrier.FixAngleAngles);

        // No server-only side effects in predicted mode: the sound/effect debounce never armed.
        Assert.Equal(padLTimeBefore, tele.PushLTime);
    }

    [Fact]
    public void PredictTeleport_SkipsMultiDestinationTeleporter()
    {
        Boot();
        Entity dest = Destination(new Vector3(0, 90, 0), "td");
        Entity tele = TeleporterBox(dest);
        tele.Enemy = null; // multi-destination: random exit, not predictable (CSQC skips it)

        Entity carrier = Carrier();
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictTeleportsAmbient(carrier);

        // Untouched: still at the origin, no view-snap stamped.
        Assert.Equal(Vector3.Zero, carrier.Origin);
        Assert.False(carrier.FixAngle);
    }
}

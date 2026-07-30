using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T22 — conveyor / ladder volume PRODUCER tests (qcsrc/common/mapobjects/func/{conveyor,ladder}.qc). The
/// CONSUMER side (PlayerPhysics reading Entity.ConveyorEntity/ConveyorMoveDir + Entity.LadderEntity) was
/// already ported; these assert the per-frame producer think tags overlapping entities and releases them
/// when they leave the volume (the g_conveyed/g_ladderents lifecycle).
///
/// Runs in the GlobalState collection (mutates Api.Services, the MapMover index, and the MapVolumes
/// per-producer tracking).
/// </summary>
[Collection("GlobalState")]
public sealed class MapVolumesTests
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

    private TestFacade _f = null!;

    private TestFacade Boot()
    {
        _f = new TestFacade();
        Api.Services = _f;
        MapMover.ClearIndex();
        MapObjectsState.Reset(); // also drops MapVolumes per-producer tracking
        _f.GameClock.Time = 1f;
        _f.GameClock.FrameTime = 1f / 60f;
        return _f;
    }

    private static Entity Volume(string className, Vector3 mins, Vector3 maxs, System.Action<Entity>? pre = null)
    {
        Entity e = Api.Entities.Spawn();
        e.Mins = mins;
        e.Maxs = maxs;
        e.Size = maxs - mins;
        e.Origin = Vector3.Zero;
        pre?.Invoke(e);
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should be registered");
        Api.Entities.SetOrigin(e, e.Origin); // relink AbsMin/AbsMax to cover the box
        return e;
    }

    private static Entity Player(Vector3 origin)
    {
        Entity p = Api.Entities.Spawn();
        p.ClassName = "player";
        p.Flags = EntFlags.Client;
        p.MoveType = MoveType.Walk;
        p.Mins = new Vector3(-16, -16, -24);
        p.Maxs = new Vector3(16, 16, 45);
        p.Health = 100f;
        Api.Entities.SetOrigin(p, origin);
        return p;
    }

    // =====================================================================================
    //  trigger_conveyor — tag overlapping pushables, release when they leave
    // =====================================================================================

    [Fact]
    public void Conveyor_TagsOverlappingPlayerWithMoveDir()
    {
        Boot();
        // a conveyor box pushing +X (angle yaw 0 -> movedir +X), speed 200.
        Entity conv = Volume("trigger_conveyor", new Vector3(-64, -64, -16), new Vector3(64, 64, 16),
            e => { e.Angles = Vector3.Zero; e.Speed = 200f; });

        Assert.Equal(MapMover.ActiveActive, conv.Active);
        // movedir was baked to speed (movedir *= speed): +X * 200.
        Assert.Equal(200f, conv.MoveDir.X, 1);

        Entity p = Player(new Vector3(0, 0, 24)); // standing in the box footprint

        conv.Think!(conv);

        Assert.Same(conv, p.ConveyorEntity);
        Assert.Equal(conv.MoveDir, p.ConveyorMoveDir);
    }

    [Fact]
    public void Conveyor_ReleasesPlayerThatLeavesTheVolume()
    {
        Boot();
        Entity conv = Volume("trigger_conveyor", new Vector3(-64, -64, -16), new Vector3(64, 64, 16),
            e => e.Speed = 200f);
        Entity p = Player(new Vector3(0, 0, 24));

        conv.Think!(conv);
        Assert.Same(conv, p.ConveyorEntity);

        // walk far away; the next think must release us (not leave ConveyorEntity stuck).
        Api.Entities.SetOrigin(p, new Vector3(10000f, 0f, 24f));
        conv.Think!(conv);

        Assert.Null(p.ConveyorEntity);
        Assert.Equal(Vector3.Zero, p.ConveyorMoveDir);
    }

    [Fact]
    public void Conveyor_InactiveReleasesAndDoesNotTag()
    {
        Boot();
        Entity conv = Volume("trigger_conveyor", new Vector3(-64, -64, -16), new Vector3(64, 64, 16),
            e => e.Speed = 200f);
        Entity p = Player(new Vector3(0, 0, 24));

        conv.Think!(conv);
        Assert.Same(conv, p.ConveyorEntity);

        // deactivate: the release pass still runs, but no new tag happens.
        conv.Active = MapMover.ActiveNot;
        conv.Think!(conv);
        Assert.Null(p.ConveyorEntity);
    }

    // =====================================================================================
    //  func_ladder / func_water — tag overlapping players
    // =====================================================================================

    [Fact]
    public void Ladder_TagsOverlappingPlayer()
    {
        Boot();
        Entity lad = Volume("func_ladder", new Vector3(-32, -32, -64), new Vector3(32, 32, 64));
        Entity p = Player(new Vector3(0, 0, 0));

        lad.Think!(lad);
        Assert.Same(lad, p.LadderEntity);
    }

    [Fact]
    public void Ladder_IgnoresNoclipAndDeadPlayers()
    {
        Boot();
        Entity lad = Volume("func_ladder", new Vector3(-32, -32, -64), new Vector3(32, 32, 64));

        Entity noclip = Player(new Vector3(0, 0, 0));
        noclip.MoveType = MoveType.Noclip;
        Entity dead = Player(new Vector3(0, 0, 0));
        dead.DeadState = DeadFlag.Dead;

        lad.Think!(lad);
        Assert.Null(noclip.LadderEntity);
        Assert.Null(dead.LadderEntity);
    }

    [Fact]
    public void Ladder_ReleasesPlayerThatLeaves()
    {
        Boot();
        Entity lad = Volume("func_ladder", new Vector3(-32, -32, -64), new Vector3(32, 32, 64));
        Entity p = Player(new Vector3(0, 0, 0));

        lad.Think!(lad);
        Assert.Same(lad, p.LadderEntity);

        Api.Entities.SetOrigin(p, new Vector3(10000f, 0f, 0f));
        lad.Think!(lad);
        Assert.Null(p.LadderEntity);
    }

    [Fact]
    public void FuncWater_UsesTheLadderScanWithWaterClassname()
    {
        Boot();
        Entity water = Volume("func_water", new Vector3(-32, -32, -64), new Vector3(32, 32, 64));
        Assert.Equal("func_water", water.ClassName); // PlayerPhysics special-cases this for waterlevel

        Entity p = Player(new Vector3(0, 0, 0));
        water.Think!(water);
        Assert.Same(water, p.LadderEntity);
    }
}

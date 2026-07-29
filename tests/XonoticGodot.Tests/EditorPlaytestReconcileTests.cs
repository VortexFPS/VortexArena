using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// The EDIT to PLAYTEST reconciliation (design doc §11.9): replacing the map's entity set with the edited
/// document's and respawning it.
///
/// The property under test is that it is REPEATABLE. A mapper toggles between editing and playtesting
/// constantly, so a reconciliation that leaves anything behind compounds: the second toggle spawns a second
/// set on top of the first, and the map fills with duplicate triggers nobody can see.
/// </summary>
[Collection("GlobalState")]
public class EditorPlaytestReconcileTests
{
    private static GameWorld EditorWorld()
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("editor");
        return world;
    }

    private static int LiveCount(GameWorld world)
    {
        int n = 0;
        if (world.Services.EntityTable.All is { } all)
            foreach (Entity e in all)
                if (e is not null && !e.IsFreed)
                    n++;
        return n;
    }

    /// <summary>
    /// A class whose spawnfunc builds a second entity, which is what the real ones do: a door spawns its touch
    /// field, a platform its trigger, a mover its controller, an item its replacement. Registered here rather
    /// than leaning on <c>func_door</c> because a real door needs a resolvable brush model to get as far as
    /// spawning its field, and a test that quietly never reaches the interesting path proves nothing.
    /// </summary>
    private const string ClassWithChild = "__test_spawns_a_child";

    static EditorPlaytestReconcileTests()
        => XonoticGodot.Common.Gameplay.SpawnFuncs.Register(ClassWithChild, parent =>
        {
            Entity child = XonoticGodot.Common.Services.Api.Entities.Spawn();
            child.ClassName = "__test_child";
            child.Owner = parent;
        });

    private static List<EntityDict> Dicts() => new()
    {
        new(ClassWithChild, new Vector3(0, 0, 0)),
        new(ClassWithChild, new Vector3(64, 0, 0)),
        new("info_player_deathmatch", new Vector3(128, 0, 24)),
    };

    [Fact]
    public void ReconcilingRepeatedly_DoesNotAccumulateEntities()
    {
        GameWorld world = EditorWorld();

        world.RespawnMapEntities(Dicts());
        int afterFirst = LiveCount(world);

        for (int i = 0; i < 4; i++)
            world.RespawnMapEntities(Dicts());

        // Every derived entity a spawnfunc created has to come out with the edict that created it. Tracking
        // only the top-level dicts leaves the triggers behind, and each pass adds another set.
        Assert.Equal(afterFirst, LiveCount(world));
    }

    [Fact]
    public void ReconcilingWithAnEmptyDocument_RemovesEverythingItSpawned()
    {
        GameWorld world = EditorWorld();
        int baseline = LiveCount(world);

        world.RespawnMapEntities(Dicts());
        Assert.True(LiveCount(world) > baseline, "the map spawn should have produced entities");

        world.RespawnMapEntities(new List<EntityDict>());
        Assert.Equal(baseline, LiveCount(world));
    }

    [Fact]
    public void Reconciling_LeavesEntitiesItDidNotSpawnAlone()
    {
        // Projectiles, gibs and anything a gametype owns must survive a playtest toggle — dropping into
        // playtest should not reset the session around you.
        GameWorld world = EditorWorld();
        world.RespawnMapEntities(Dicts());

        Entity bystander = world.Services.Entities.Spawn();
        bystander.ClassName = "not_a_map_entity";

        world.RespawnMapEntities(Dicts());

        Assert.False(bystander.IsFreed);
    }
}

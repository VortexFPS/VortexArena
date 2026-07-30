using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Dropped weapons must collide with the world instead of sinking through it.
///
/// A loot item's bbox is <c>ITEM_D_MINS</c>/<c>ITEM_D_MAXS</c> (±30 horizontally) but the player hull that drops
/// it is only ±16, so a weapon thrown — or death-dropped — anywhere near a wall, pillar or step spawns with its
/// box ALREADY EMBEDDED in world geometry. Measured on fuse, roughly half of all bot death-drops spawned
/// start-solid. A start-solid MOVETYPE_TOSS body is not obstructed by the brush it is already inside, so it
/// sailed straight through the wall rather than resting against it.
///
/// Base handles exactly this: <c>StartItem</c>'s loot branch calls <c>nudgeoutofsolid_OrFallback(this)</c>
/// (server/items/items.qc:1089, commented "most loot items have a bigger horizontal size than a player") — DP's
/// <c>SV_NudgeOutOfSolid</c> builtin. The port's <c>SetupLoot</c> had no equivalent.
///
/// Mutates Api.Services, so it runs in the serialized GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public sealed class DroppedWeaponCollisionTests
{
    private readonly ITestOutputHelper _out;
    public DroppedWeaponCollisionTests(ITestOutputHelper output) => _out = output;

    /// <summary>A floor plus a wall occupying x &gt;= 0 — so a drop centred just short of x=0 has its ±30 item
    /// box poking into the wall while a ±16 player hull at the same spot would be clear.</summary>
    private static CollisionWorld FloorAndWall()
    {
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-1024f, -1024f, -64f), new Vector3(1024f, 1024f, 0f), SuperContents.Solid));
        w.AddBrush(Brush.FromBox(new Vector3(0f, -1024f, 0f), new Vector3(1024f, 1024f, 512f), SuperContents.Solid));
        w.BuildGrid();
        return w;
    }

    private static GameWorld BootWorld()
    {
        var world = new GameWorld(FloorAndWall(), new List<EntityDict>
        {
            new("worldspawn"),
            new("info_player_deathmatch", new Vector3(-500f, 0f, 32f)),
        });
        world.Boot("dm");
        return world;
    }

    private static bool StuckAt(Entity e, Vector3 pos)
        => Api.Trace.Trace(pos, e.Mins, e.Maxs, pos, MoveFilter.NoMonsters, e).StartSolid;

    private static Entity DropLootAt(Vector3 origin)
    {
        Weapon? wep = Weapons.ByName("devastator");
        Assert.NotNull(wep);
        Entity item = Api.Entities.Spawn();
        Api.Entities.SetOrigin(item, origin);
        item.Origin = origin;
        Assert.NotNull(StartItem.SpawnLoot(item, ItemSpawnFuncs.PickupFor(wep!)));
        return item;
    }

    /// <summary>
    /// Drop a weapon at x = -20: a ±16 player hull is clear of the wall there, but the item's ±30 box overlaps
    /// it by 10 units. After <c>SpawnLoot</c> the item must NOT be embedded in the wall.
    /// </summary>
    [Fact]
    public void LootDroppedAgainstAWallIsNudgedOutOfSolid()
    {
        BootWorld();

        var dropOrigin = new Vector3(-20f, 0f, 1f);
        Entity item = DropLootAt(dropOrigin);

        // Precondition: the drop point really is one the ±30 item box cannot occupy (the bug's geometry).
        Assert.Equal(new Vector3(-30f, -30f, 0f), item.Mins);
        Assert.True(StuckAt(item, dropOrigin), "precondition: the raw drop origin must be start-solid");

        _out.WriteLine($"drop {dropOrigin} -> {item.Origin}");
        Assert.False(StuckAt(item, item.Origin), $"item still embedded in the wall at {item.Origin}");
    }

    /// <summary>
    /// The whole point of the nudge: a freed item is then obstructed by the wall like any other TOSS body. Hurl
    /// the drop straight at the wall and tick the real server frame — it must not end up inside or beyond it.
    /// </summary>
    [Fact]
    public void NudgedLootThenCollidesWithTheWallInsteadOfPassingThrough()
    {
        GameWorld world = BootWorld();

        Entity item = DropLootAt(new Vector3(-20f, 0f, 1f));
        item.Velocity = new Vector3(900f, 0f, 0f); // hurled straight at the wall

        for (int t = 0; t < 72 && !item.IsFreed; t++)
            world.Frame(SimulationLoop.TicRate);

        _out.WriteLine($"after 1s: origin={item.Origin} velocity={item.Velocity} freed={item.IsFreed}");
        Assert.False(item.IsFreed);
        // The wall's face is at x = 0 and the item's box extends +30, so a body stopped by it rests at x <= -30.
        // Anything past that has tunnelled into (or through) the brush.
        Assert.True(item.Origin.X <= -29.5f, $"item passed into the wall: x={item.Origin.X}");
        Assert.False(StuckAt(item, item.Origin), "item came to rest inside solid");
    }

    /// <summary>A drop in open air must be left exactly where it was thrown — the nudge is a no-op when free.</summary>
    [Fact]
    public void LootDroppedInOpenAirIsNotMoved()
    {
        BootWorld();

        var dropOrigin = new Vector3(-400f, 0f, 64f);
        Entity item = DropLootAt(dropOrigin);
        Assert.Equal(dropOrigin, item.Origin);
    }
}

using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server.Bot;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests for the QC-faithful bot item valuation extensions (2026-07-11 follow-up): per-weapon
/// bot_pickupbasevalue + arsenal discount (QC weapon_pickupevalfunc, items.qc:887-907), the ammo need
/// eval (ammo_pickupevalfunc, items.qc:909-955), the underwater shore-waypoint push (havocbot_ai:68-100),
/// and the skill&gt;3 Strength/Shield aggression nudges (roles.qc:203-210).
/// </summary>
[Collection("GlobalState")]
public class BotItemValueTests
{
    private static EngineServices Facade()
    {
        var es = new EngineServices(new CollisionWorld());
        Api.Services = es;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap(); // discovers the [Weapon] registry (per-weapon BotPickupBaseValue)
        return es;
    }

    private static Player NewPlayer(int team, Vector3 origin = default)
        => new Player
        {
            Team = team, Flags = EntFlags.Client, Origin = origin,
            Health = 100f, MaxHealth = 100f, TakeDamage = DamageMode.Aim,
        };

    private static Entity SpawnWeaponItem(EngineServices es, string netName, Vector3 origin)
    {
        Entity item = es.EntityTable.Spawn();
        item.ClassName = "weapon_" + netName;
        item.NetName = netName;
        item.Flags = EntFlags.Item;
        item.Solid = Solid.Trigger;
        item.Origin = origin;
        return item;
    }

    private static float RateItems(BotBrain brain)
    {
        var rater = new GoalRater();
        rater.Start();
        BotRoles.GoalrateItems(brain, rater, brain.Bot.Origin, 10000f);
        return rater.HasGoal ? rater.Best.Rating : 0f;
    }

    [Fact]
    public void UnownedWeapon_RatesItsQcBaseValue()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var brain = new BotBrain(bot) { PlayerProvider = () => new List<Player> { bot } };
        SpawnWeaponItem(es, "vortex", new Vector3(100, 0, 0));

        // QC vortex bot_pickupbasevalue = 8000; empty arsenal → no discount (c = 1). Distance-discounted
        // slightly by rangebias 2000 at 100qu, so assert "in the vortex band, well above MID weapons".
        float rating = RateItems(brain);
        Assert.True(rating > 6500f && rating <= 8000f, $"unowned vortex should rate ~8000, got {rating}");
    }

    [Fact]
    public void StackedArsenal_DiscountsUnownedWeapon()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var brain = new BotBrain(bot) { PlayerProvider = () => new List<Player> { bot } };
        SpawnWeaponItem(es, "vortex", new Vector3(100, 0, 0));

        float empty = RateItems(brain);

        // Give a ≥20000-value arsenal (devastator 8000 + machinegun 7000 + mortar 7000) → full 50% discount.
        Inventory.GiveWeapon(bot, Weapons.ByName("devastator")!);
        Inventory.GiveWeapon(bot, Weapons.ByName("machinegun")!);
        Inventory.GiveWeapon(bot, Weapons.ByName("mortar")!);
        float stacked = RateItems(brain);

        Assert.True(stacked < empty * 0.55f && stacked > empty * 0.45f,
            $"a ≥20000 arsenal should halve the unowned-weapon value (QC c = 0.5): {empty} -> {stacked}");
    }

    [Fact]
    public void AmmoBox_ForUnownedWeapon_RatesZero()
    {
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var brain = new BotBrain(bot) { PlayerProvider = () => new List<Player> { bot } };

        Entity cells = es.EntityTable.Spawn();
        cells.ClassName = "item_cells";
        cells.Flags = EntFlags.Item;
        cells.Solid = Solid.Trigger;
        cells.Origin = new Vector3(100, 0, 0);
        cells.SetResourceExplicit(ResourceType.Cells, 25f);

        // QC ammo_pickupevalfunc: no owned weapon feeds on cells → item_resource stays NULL → rating 0.
        Assert.Equal(0f, RateItems(brain));

        // Owning a cells weapon (vortex) makes the same box worth its m_botvalue (1500) × need.
        Inventory.GiveWeapon(bot, Weapons.ByName("vortex")!);
        float rating = RateItems(brain);
        Assert.True(rating > 500f, $"cells box should rate once a cells weapon is owned, got {rating}");
    }

    [Fact]
    public void StrengthPowerup_RaisesOwnAggression_LowersTowardPoweredEnemy()
    {
        Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var enemy = NewPlayer(Teams.None, new Vector3(500, 0, 0));
        var brain = new BotBrain(bot, skill: 6f) { PlayerProvider = () => new List<Player> { bot, enemy } };

        float Rate()
        {
            var rater = new GoalRater();
            rater.Start();
            BotRoles.GoalrateEnemyPlayers(brain, rater, bot.Origin, 10000f);
            return rater.HasGoal ? rater.Best.Rating : 0f;
        }

        float baseline = Rate();
        Assert.True(baseline > 0f);

        // QC roles.qc:203-210 (skill>3): OUR Strength (>1s left) → t += 0.5 (press the advantage).
        var strength = StatusEffectsCatalog.ByName("strength")!;
        StatusEffectsCatalog.Apply(bot, strength, duration: 30f);
        float pressing = Rate();
        Assert.True(pressing > baseline, $"own Strength should raise aggression: {baseline} -> {pressing}");

        // THEIR Strength too → the two nudges cancel back to baseline; theirs alone would drop below.
        StatusEffectsCatalog.Apply(enemy, strength, duration: 30f);
        float contested = Rate();
        Assert.True(contested < pressing, $"enemy Strength should temper it: {pressing} -> {contested}");
    }

    [Fact]
    public void SwimmingBotWithNoGoal_PushesShoreWaypoint()
    {
        Facade();
        var bot = NewPlayer(Teams.None, new Vector3(0, 0, 0));
        bot.WaterLevel = 2; // QC WATERLEVEL_SWIMMING
        bot.ViewOfs = new Vector3(0, 0, 35);

        var net = new WaypointNetwork();
        net.Add(new Vector3(200, 0, 40));   // shore: above the bot, within eye+100
        net.Add(new Vector3(100, 0, -50));  // below the bot — not a way OUT
        var brain = new BotBrain(bot, net) { PlayerProvider = () => new List<Player> { bot } };

        // The push logic itself (QC havocbot_ai:68-100): must pick the up-and-out waypoint, never the one
        // below. (The empty collision world traces open and PointContents empty — the happy path.)
        Assert.True(brain.TryPushShoreWaypoint(bot), "swimming goal-less bot should push a shore waypoint");
        Assert.Equal(new Vector3(200, 0, 40), brain.Nav.Current!.Value);
    }

    /// <summary>
    /// The whole per-weapon table, netname → QC <c>bot_pickupbasevalue</c> (the "rating" ATTRIB in
    /// common/weapons/weapon/*.qh and the overkill mutator's). This is transcribed data, so a typo in ONE
    /// entry is the likely defect class and no amount of "does a weapon rate something" testing catches it.
    /// An unowned weapon rates exactly its base value on an empty arsenal (the discount c is 1.0 at 0 owned).
    /// </summary>
    [Theory]
    [InlineData("arc", 8000f)]
    [InlineData("devastator", 8000f)]
    [InlineData("oknex", 8000f)]
    [InlineData("vortex", 8000f)]
    [InlineData("crylink", 6000f)]
    [InlineData("hagar", 6000f)]
    [InlineData("okshotgun", 6000f)]
    [InlineData("shotgun", 6000f)]
    [InlineData("electro", 5000f)]
    [InlineData("fireball", 5000f)]
    [InlineData("seeker", 5000f)]
    [InlineData("hlac", 4000f)]
    [InlineData("machinegun", 7000f)]
    [InlineData("minelayer", 7000f)]
    [InlineData("mortar", 7000f)]
    [InlineData("okmachinegun", 7000f)]
    [InlineData("rifle", 7000f)]
    [InlineData("okhmg", 10000f)]
    [InlineData("okrpc", 10000f)]
    [InlineData("vaporizer", 10000f)]
    [InlineData("tuba", 2000f)]
    [InlineData("blaster", 0f)]
    [InlineData("hook", 0f)]
    [InlineData("porto", 0f)]
    public void EveryWeapon_CarriesItsQcBotPickupBaseValue(string netName, float expected)
    {
        Facade();
        Weapon? w = Weapons.ByName(netName);
        Assert.NotNull(w);
        Assert.Equal(expected, w!.BotPickupBaseValue);
    }

    [Fact]
    public void LiveProjectiles_AreNotRatedAsWeaponPickups()
    {
        // EntFlags.Item doubles as the port's FL_PROJECTILE marker and projectiles carry their weapon's
        // NetName, so an in-flight rocket matched the weapon branch and rated ~8000 — bots pathed INTO
        // incoming rockets and treated a laid mine as a permanent attractor. Real pickups are explicitly
        // ownerless; a projectile always carries its firer.
        var es = Facade();
        var bot = NewPlayer(Teams.None, Vector3.Zero);
        var shooter = NewPlayer(Teams.None, new Vector3(500, 0, 0));

        Entity rocket = es.EntityTable.Spawn();
        rocket.ClassName = "rocket";
        rocket.NetName = "devastator";     // as Devastator.cs stamps it
        rocket.Flags = EntFlags.Item;      // "QC FL_PROJECTILE"
        rocket.Solid = Solid.Corpse;
        rocket.Origin = new Vector3(300, 0, 0);
        rocket.Owner = shooter;

        var brain = new BotBrain(bot) { PlayerProvider = () => new List<Player> { bot, shooter } };
        Assert.Equal(0f, RateItems(brain));

        // Sanity: the SAME entity as a real ownerless pickup does rate — proving the test isn't vacuous.
        rocket.Owner = null;
        rocket.Solid = Solid.Trigger;
        Assert.True(RateItems(brain) > 1000f);
    }
}

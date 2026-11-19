using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// T66 — the FilterItem item-spawn hook (port of qcsrc/server/items/items.qc:1031:
/// <c>if(MUTATOR_CALLHOOK(FilterItem, this)) { delete(this); return; }</c>). StartItem fires the
/// <see cref="MutatorHooks.FilterItemDefinition"/> chain after the items/weapon/flags seeding and BEFORE the
/// have-pickup gate; a subscriber returning true deletes the edict and reports the spawn failed.
///
/// These tests boot the real registries + spawnfuncs, flip the mutator cvar, run
/// <see cref="MutatorActivation.Apply"/> to subscribe the live mutators, then drive a real end-to-end spawn
/// through <c>SpawnFuncs.TrySpawn → StartItem.Spawn</c> and assert the filter is enforced (item removed /
/// kept) exactly as the cited QC subscribers (nix / melee_only) dictate.
///
/// Mirrors the ItemSpawnTouchTests + MutatorBatchT51Tests harness (GlobalState collection; mutators torn down
/// in Dispose so the global hook chains don't leak handlers into the next test).
/// </summary>
[Collection("GlobalState")]
public class FilterItemHookTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new() { FrameTime = 1f / 60f };
        public TestFacade() { Inner = new EngineServices(new CollisionWorld()); }
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    // Boot a clean world (services + registries + status effects + spawnfuncs), then seed the pickup cvars the
    // spawn path reads + apply the requested mutator cvars, and converge the mutator subscriptions.
    private static TestFacade Boot(params (string name, string value)[] cvars)
    {
        var facade = new TestFacade();
        Api.Services = facade;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();          // discovers the [Item] pickups + the Weapon/Mutator registries
        Combat.System = new DamageSystem();
        Movement.System = new PlayerPhysics();
        ItemSpawnFuncs.Register();            // install item_*/weapon_* spawnfuncs (needs the registries above)
        SeedCvars(facade);
        foreach (var (n, v) in cvars) facade.Cvars.Set(n, v);
        MutatorActivation.Apply();            // subscribe the now-enabled mutators' hooks (incl. FilterItem)
        return facade;
    }

    // The slice of the pickup/powerup cvars StartItem + ItemInit read so items actually spawn (mirrors Cvars.cs).
    private static void SeedCvars(TestFacade f)
    {
        void S(string n, string v) => f.Cvars.Set(n, v);
        S("g_pickup_items", "1");
        S("g_powerups", "1");
        S("g_powerups_strength", "1"); S("g_powerups_shield", "1"); S("g_powerups_speed", "1");
        S("g_powerups_invisibility", "1"); S("g_powerups_jetpack", "1"); S("g_powerups_fuelregen", "1");
        S("g_powerups_stack", "0");
        S("g_weapon_stay", "0");
        S("g_balance_powerup_strength_time", "30");
        S("g_pickup_healthsmall", "5"); S("g_pickup_healthsmall_max", "200");
        S("g_pickup_healthbig", "25"); S("g_pickup_healthbig_max", "200");
        S("g_pickup_healthmega", "100"); S("g_pickup_healthmega_max", "200");
        S("g_pickup_armorsmall", "5"); S("g_pickup_armorsmall_max", "200");
        S("g_pickup_armorbig", "25"); S("g_pickup_armorbig_max", "200");
        S("g_balance_health_limit", "200"); S("g_balance_armor_limit", "200");
    }

    /// <summary>Spawn a map item by classname through the real StartItem pipeline; return the world edict.</summary>
    private static Entity Spawn(string className)
    {
        var e = Api.Services!.Entities.Spawn();
        e.Origin = System.Numerics.Vector3.Zero;
        SpawnFuncs.TrySpawn(className, e); // true even on a filtered spawn (a spawnfunc ran; the edict may be freed)
        return e;
    }

    [Fact]
    public void BuffAutoSeed_UsesTheCompletedMapItemSet()
    {
        var facade = Boot(("g_buffs", "1"), ("g_buffs_spawn_count", "2"), ("g_buffs_resistance", "1"),
            ("g_buffs_pickup_delay", "4"));
        Assert.Empty(Api.Entities.FindByClass("item_buff"));

        var mapBuff = Spawn("item_buff_resistance");
        BuffsMutator.SpawnInitialBuffs();
        Assert.Same(mapBuff, Assert.Single(Api.Entities.FindByClass("item_buff")));

        Api.Entities.Remove(mapBuff);
        BuffsMutator.SpawnInitialBuffs();
        var seeds = Api.Entities.FindByClass("item_buff").ToArray();
        Assert.Equal(2, seeds.Length);

        var seed = seeds[0];
        facade.GameClock.Time = seed.NextThink;
        seed.Think!(seed);
        var player = Api.Entities.Spawn();
        player.Flags = EntFlags.Client;
        StatusEffectsCatalog.Apply(player, StatusEffectsCatalog.ByName("buff_medic")!, 30f);
        seed.Touch!(seed, player);
        Assert.True(StatusEffectsCatalog.Has(player, StatusEffectsCatalog.ByName("buff_resistance")!));
        Assert.Equal(facade.GameClock.Time + 4f, player.BuffShield);
    }

    [Theory]
    [InlineData(0f, 12, 12f)]
    [InlineData(7.5f, 12, 7.5f)]
    public void RandomTeamBuff_UsesSpawnFlagsAndAuthoredPickupDuration(float duration, int count, float expected)
    {
        var facade = Boot(("g_buffs", "1"), ("g_buffs_resistance", "1"));
        var buff = Api.Entities.Spawn();
        buff.SpawnFlags = 1;
        buff.Count = count;
        buff.BuffsFinished = duration;
        bool wasTeamplay = GameScores.Teamplay;
        try
        {
            GameScores.Teamplay = true;
            Assert.True(SpawnFuncs.TrySpawn("item_buff_random_team1", buff));
            Assert.Equal(Teams.Red, buff.TeamForced);
            Assert.Equal(MoveType.None, buff.MoveType);
            Assert.Equal(0.3f, buff.Alpha);

            facade.GameClock.Time = buff.NextThink;
            buff.Think!(buff);
            Assert.Equal(1f, buff.Alpha);
            Assert.NotEqual(0, (int)(buff.Effects & EffectFlags.FullBright));
            var toucher = Api.Entities.Spawn();
            toucher.Flags = EntFlags.Client;
            toucher.Team = Teams.Blue;
            buff.Touch!(buff, toucher);
            Assert.Empty(toucher.StatusEffects);
            toucher.Team = Teams.Red;
            buff.Touch!(buff, toucher);
            var held = Assert.Single(toucher.StatusEffects);
            Assert.Equal(facade.GameClock.Time + expected, held.ExpireTime);
            Assert.Equal(0.3f, buff.Alpha);
            Assert.Equal(0, (int)(buff.Effects & EffectFlags.FullBright));
        }
        finally { GameScores.Teamplay = wasTeamplay; }
    }

    [Fact]
    public void Disability_ScalesTheEntireProjectileLaunchVector()
    {
        Boot(("g_buffs", "1"), ("g_weaponspeedfactor", "2"), ("g_buffs_disability_weaponspeed", "0.5"));
        var actor = Api.Entities.Spawn();
        var stunned = StatusEffectsCatalog.Stunned!;
        var normal = WeaponFiring.ProjectileVelocity(actor, Vector3.UnitX, Vector3.UnitY, 100f, 30f, 40f);
        Assert.True(Vector3.Distance(new Vector3(200f, 60f, 80f), normal) < 0.001f);

        StatusEffectsCatalog.Apply(actor, stunned, 3f);
        Assert.Equal(normal * 0.5f, WeaponFiring.ProjectileVelocity(actor, Vector3.UnitX, Vector3.UnitY, 100f, 30f, 40f));
    }

    [Theory]
    [InlineData("80")]
    [InlineData("120")]
    public void Swapper_PicksClosestEnemyThroughPortal(string range)
    {
        Boot(("g_buffs", "1"), ("g_buffs_swapper_range", range));
        var manager = new WarpzoneManager();
        Vector3 half = new(8, 64, 64);
        manager.Spawn(Vector3.Zero, Vector3.Zero, "in", "out", -half, half);
        manager.Spawn(new Vector3(100, 0, 0), new Vector3(0, 180, 0), "out", "in", -half, half);
        manager.Link();
        Entity PlayerAt(float x, int team)
        {
            var entity = Api.Entities.Spawn();
            entity.ClassName = "player";
            entity.Flags = EntFlags.Client;
            entity.Team = team;
            Api.Entities.SetOrigin(entity, new Vector3(x, 0f, 0f));
            return entity;
        }
        var carrier = PlayerAt(50, Teams.Red);
        var direct = PlayerAt(70, Teams.Blue);
        var across = PlayerAt(155, Teams.Blue);
        var power = StatusEffectsCatalog.ByName("buff_swapper")!;
        StatusEffectsCatalog.Apply(carrier, power, 30f);
        var previous = WarpzoneTrace.AmbientManager;
        try
        {
            WarpzoneTrace.AmbientManager = manager;
            var args = new MutatorHooks.ForbidThrowCurrentWeaponArgs(carrier);
            Assert.True(MutatorHooks.ForbidThrowCurrentWeapon.Call(ref args));
            Assert.Equal(155f, carrier.Origin.X);
            Assert.Equal(50f, across.Origin.X);
            Assert.Equal(70f, direct.Origin.X);
        }
        finally { WarpzoneTrace.AmbientManager = previous; }
    }

    [Fact]
    public void Swapper_AdmitsAnElongatedPortalFromBehindItsPlane()
    {
        Boot(("g_buffs", "1"), ("g_buffs_swapper_range", "80"));
        var manager = SwapperPortalPair(new Vector3(8f, 256f, 64f));
        var carrier = SwapperPlayerAt(new Vector3(-50f, 200f, 0f), Teams.Red);
        var direct = SwapperPlayerAt(new Vector3(-30f, 200f, 0f), Teams.Blue);
        var through = SwapperPlayerAt(new Vector3(445f, 200f, 0f), Teams.Blue);

        UseSwapper(carrier, manager);

        Assert.Equal(new Vector3(445f, 200f, 0f), carrier.Origin);
        Assert.Equal(new Vector3(-50f, 200f, 0f), through.Origin);
        Assert.Equal(new Vector3(-30f, 200f, 0f), direct.Origin);
    }

    [Theory]
    [InlineData(false, 555f)]
    [InlineData(true, 540f)]
    public void Swapper_ClipsExitSearchButRanksByNominalOrigin(bool blocked, float expectedX)
    {
        var facade = Boot(("g_buffs", "1"), ("g_buffs_swapper_range", "60"));
        if (blocked)
        {
            var collision = new CollisionWorld();
            collision.AddBrush(Brush.FromBox(new Vector3(520f, -64f, -64f),
                new Vector3(524f, 64f, 64f), SuperContents.Solid));
            collision.BuildGrid();
            facade.Inner.SetCollisionWorld(collision);
        }

        var manager = SwapperPortalPair(new Vector3(8f, 64f, 64f));
        var carrier = SwapperPlayerAt(new Vector3(50f, 0f, 0f), Teams.Red);
        var direct = SwapperPlayerAt(new Vector3(65f, 0f, 0f), Teams.Blue);
        SwapperPlayerAt(new Vector3(540f, 0f, 0f), Teams.Blue);
        SwapperPlayerAt(new Vector3(555f, 0f, 0f), Teams.Blue);

        // The nominal origin is x=550. The wall clips the search center to x≈520 and removes ≈30 units
        // of radius. x=555 drops out, but x=540 keeps its nominal distance of 10 and beats the direct target.
        UseSwapper(carrier, manager);

        Assert.Equal(expectedX, carrier.Origin.X);
        Assert.Equal(65f, direct.Origin.X);
    }

    private static WarpzoneManager SwapperPortalPair(Vector3 entryHalfSize)
    {
        var manager = new WarpzoneManager();
        var exitHalfSize = new Vector3(8f, 64f, 64f);
        manager.Spawn(Vector3.Zero, Vector3.Zero, "in", "out", -entryHalfSize, entryHalfSize);
        manager.Spawn(new Vector3(500f, 0f, 0f), new Vector3(0f, 180f, 0f),
            "out", "in", -exitHalfSize, exitHalfSize);
        manager.Link();
        return manager;
    }

    private static Entity SwapperPlayerAt(Vector3 origin, int team)
    {
        var entity = Api.Entities.Spawn();
        entity.ClassName = "player";
        entity.Flags = EntFlags.Client;
        entity.Team = team;
        Api.Entities.SetOrigin(entity, origin);
        return entity;
    }

    private static void UseSwapper(Entity carrier, WarpzoneManager manager)
    {
        StatusEffectsCatalog.Apply(carrier, StatusEffectsCatalog.ByName("buff_swapper")!, 30f);
        var previous = WarpzoneTrace.AmbientManager;
        try
        {
            WarpzoneTrace.AmbientManager = manager;
            var args = new MutatorHooks.ForbidThrowCurrentWeaponArgs(carrier);
            Assert.True(MutatorHooks.ForbidThrowCurrentWeapon.Call(ref args));
        }
        finally { WarpzoneTrace.AmbientManager = previous; }
    }

    [Fact]
    public void Swapper_ExchangesMotionAndViewAndCreditsTheCarrier()
    {
        var facade = Boot(("g_buffs", "1"), ("g_maxpushtime", "6"));
        var carrier = Api.Entities.Spawn();
        var target = Api.Entities.Spawn();
        carrier.Flags = target.Flags = EntFlags.Client;
        carrier.ClassName = target.ClassName = "player";
        carrier.Team = target.Team = Teams.Red;
        facade.GameClock.Time = 10f;
        Api.Entities.SetOrigin(carrier, new Vector3(1f, 2f, 3f));
        Api.Entities.SetOrigin(target, new Vector3(101f, 102f, 103f));
        carrier.Velocity = Vector3.UnitX;
        target.Velocity = Vector3.UnitY;
        carrier.Angles = new Vector3(10f, 20f, 30f);
        target.Angles = new Vector3(40f, 50f, 60f);
        target.ButtonChat = true;
        var carrierInitial = (carrier.Origin, carrier.Velocity, carrier.Angles);
        var targetInitial = (target.Origin, target.Velocity, target.Angles);
        var swapper = StatusEffectsCatalog.ByName("buff_swapper")!;
        StatusEffectsCatalog.Apply(carrier, swapper, 30f);

        var args = new MutatorHooks.ForbidThrowCurrentWeaponArgs(carrier);
        bool wasTeamplay = GameScores.Teamplay;
        try
        {
            GameScores.Teamplay = false;
            facade.Cvars.Set("g_buffs_swapper_range", "0");
            Assert.False(MutatorHooks.ForbidThrowCurrentWeapon.Call(ref args));
            Assert.True(StatusEffectsCatalog.Has(carrier, swapper));
            facade.Cvars.Set("g_buffs_swapper_range", "1500");
            Assert.True(MutatorHooks.ForbidThrowCurrentWeapon.Call(ref args));
        }
        finally { GameScores.Teamplay = wasTeamplay; }
        Assert.Equal(targetInitial, (carrier.Origin, carrier.Velocity, carrier.Angles));
        Assert.Equal(carrierInitial, (target.Origin, target.Velocity, target.Angles));
        foreach (var player in new[] { carrier, target })
        {
            Assert.True(player.FixAngle);
            Assert.True(player.AntilagNeedsClear);
            Assert.Equal(player.Angles, player.FixAngleAngles);
            Assert.Equal(player.Angles, player.ViewAngles);
            Assert.Equal(player.Origin, player.OldOrigin);
            Assert.Equal(player.Velocity, player.OldVelocity);
        }
        Assert.Same(carrier, target.Pusher);
        Assert.Equal(16f, target.PushLTime);
        Assert.True(target.IsTypeFrag);
        Assert.False(StatusEffectsCatalog.Has(carrier, swapper));
    }

    // =====================================================================================
    //  Hook fires at all (baseline: no filter mutator → items spawn normally)
    // =====================================================================================

    [Theory]
    [InlineData("vortex", false, true)]
    [InlineData("crylink vortex", false, true)]
    [InlineData("none", false, true)]
    [InlineData("off", false, false)]
    [InlineData("vortex", true, false)]
    public void WeaponArena_MapPickupsFollowTheResolvedArenaAndForceSpawnSetting(
        string arena, bool forceSpawn, bool weaponsFiltered)
    {
        Boot(("g_weaponarena", arena));
        string defaultPickups = VortexArena.Server.Cvars.Defaults.Single(cvar => cvar.Name == "g_pickup_items").DefaultValue;
        Api.Cvars.Set("g_pickup_items", forceSpawn ? "1" : defaultPickups);

        Assert.Equal(weaponsFiltered, Spawn("weapon_crylink").IsFreed);
        Assert.Equal(weaponsFiltered, Spawn("item_cells").IsFreed);
        Assert.False(Spawn("item_health_small").IsFreed);
    }

    [Theory]
    [InlineData("off", "vortex", true)]
    [InlineData("vortex", "off", false)]
    public void WeaponArena_MapPickupsHonorGametypeArenaOverride(string arena, string arenaOverride, bool filtered)
    {
        Boot(("g_weaponarena", arena), ("g_pickup_items", "-1"));
        bool OverrideArena(ref MutatorHooks.SetWeaponArenaArgs args)
        {
            args.Arena = arenaOverride;
            return false;
        }

        MutatorHooks.SetWeaponArena.Add(OverrideArena);
        try
        {
            Assert.Equal(filtered, Spawn("weapon_crylink").IsFreed);
        }
        finally
        {
            MutatorHooks.SetWeaponArena.Remove(OverrideArena);
        }
    }

    [Fact]
    public void NoFilterMutator_ItemsSpawnNormally()
    {
        Boot(); // no nix / melee_only enabled → the FilterItem chain has no "delete" subscribers
        var health = Spawn("item_health_small");
        Assert.False(health.IsFreed);
        Assert.True(health.ItemAvailable);
        Assert.False(StartItem.LastSpawnFailed);
    }

    // Both canonical and compatibility spawn functions pass their resolved pickup to the Duel hook.
    [Theory]
    [InlineData("item_strength")]
    [InlineData("item_shield")]
    [InlineData("item_invincible")]
    [InlineData("item_speed")]
    [InlineData("item_invisibility")]
    [InlineData("item_jetpack")]
    [InlineData("item_fuel_regen")]
    [InlineData("item_haste")]
    public void DuelActive_FiltersResolvedPowerupsOnEachSpawn(string className)
    {
        var facade = Boot();
        var duel = new Duel();
        duel.Activate(); // subscribes OnFilterItemDefinition into MutatorHooks.FilterItemDefinition
        try
        {
            Assert.True(Spawn(className).IsFreed);
            Assert.True(StartItem.LastSpawnFailed);
            facade.Cvars.Set("g_duel_with_powerups", "1");
            Assert.False(Spawn(className).IsFreed);
            facade.Cvars.Set("g_duel_with_powerups", "0");
            Assert.True(Spawn(className).IsFreed);
            Assert.False(Spawn("item_health_small").IsFreed);
        }
        finally { duel.Deactivate(); }
    }

    // =====================================================================================
    //  NIX (common/mutators/mutator/nix/sv_nix.qc FilterItem) — delete everything but optionally health/armor.
    // =====================================================================================

    [Fact]
    public void Nix_Active_DeletesHealthItem()
    {
        Boot(("g_nix", "1")); // g_nix_with_healtharmor unset → false → health/armor filtered out
        Assert.True(Mutators.ByName("nix")!.IsEnabled);

        var health = Spawn("item_health_small");
        // QC: MUTATOR_CALLHOOK(FilterItem) returns true → delete(this) + startitem_failed = true.
        Assert.True(health.IsFreed);
        Assert.True(StartItem.LastSpawnFailed);
    }

    [Fact]
    public void Nix_WithHealthArmor_KeepsHealthItem()
    {
        Boot(("g_nix", "1"), ("g_nix_with_healtharmor", "1")); // keep health/armor pickups
        Assert.True(Mutators.ByName("nix")!.IsEnabled);

        var health = Spawn("item_health_small");
        Assert.False(health.IsFreed);
        Assert.True(health.ItemAvailable);
        Assert.False(StartItem.LastSpawnFailed);
    }

    [Fact]
    public void Nix_Active_DeletesAmmoItem()
    {
        Boot(("g_nix", "1"), ("g_nix_with_healtharmor", "1")); // even keeping health, ammo is still filtered
        var ammo = Spawn("item_shells");
        // NIX deletes ALL items except (optionally) health/armor/powerups; ammo is never kept.
        Assert.True(ammo.IsFreed);
        Assert.True(StartItem.LastSpawnFailed);
    }

    // =====================================================================================
    //  melee_only (common/mutators/mutator/melee_only/sv_melee_only.qc FilterItem) — strip small health/armor.
    // =====================================================================================

    [Fact]
    public void MeleeOnly_Active_DeletesSmallHealth()
    {
        Boot(("g_melee_only", "1"));
        Assert.True(Mutators.ByName("melee_only")!.IsEnabled);

        var small = Spawn("item_health_small");
        // QC switches on ITEM_HealthSmall / ITEM_ArmorSmall → return true → deleted.
        Assert.True(small.IsFreed);
        Assert.True(StartItem.LastSpawnFailed);
    }

    [Fact]
    public void MeleeOnly_Active_KeepsBigHealth()
    {
        Boot(("g_melee_only", "1"));
        var big = Spawn("item_health_big");
        // melee_only only strips the SMALL health/armor; the big health is left to spawn.
        Assert.False(big.IsFreed);
        Assert.True(big.ItemAvailable);
        Assert.False(StartItem.LastSpawnFailed);
    }

    [Fact]
    public void MeleeOnly_Active_DeletesSmallArmor()
    {
        Boot(("g_melee_only", "1"));
        var smallArmor = Spawn("item_armor_small");
        Assert.True(smallArmor.IsFreed);
        Assert.True(StartItem.LastSpawnFailed);
    }

    // =====================================================================================
    //  NetName must be live at hook time (the port deviation: subscribers read args.Definition.NetName).
    // =====================================================================================

    // MeleeOnly's filter ALSO matches on the def's NetName ("health_small"/"armor_small") — which the port must
    // assign to the edict BEFORE the hook fires (StartItem hoists item.NetName ahead of the FilterItem call). A
    // direct hook call with only the NetName tag set (no ClassName) proves that path is honored.
    [Fact]
    public void FilterHook_ReadsNetName_NotJustClassName()
    {
        Boot(("g_melee_only", "1"));

        var probe = new Entity { NetName = "health_small" }; // ClassName intentionally left empty
        var args = new MutatorHooks.FilterItemDefinitionArgs(probe);
        bool filtered = MutatorHooks.FilterItemDefinition.Call(ref args);
        Assert.True(filtered, "melee_only matches the def NetName tag — so NetName must be live when the hook fires");
    }

    // The end-to-end counterpart: after a real spawn the edict carries the def NetName (the value the hook read).
    [Fact]
    public void StartItem_AssignsNetName_FromDef()
    {
        Boot(); // no filter; just verify NetName is populated by the spawn driver
        var health = Spawn("item_health_small");
        Assert.Equal("health_small", health.NetName);
    }
}

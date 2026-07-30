using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

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

    // =====================================================================================
    //  Hook fires at all (baseline: no filter mutator → items spawn normally)
    // =====================================================================================

    [Fact]
    public void NoFilterMutator_ItemsSpawnNormally()
    {
        Boot(); // no nix / melee_only enabled → the FilterItem chain has no "delete" subscribers
        var health = Spawn("item_health_small");
        Assert.False(health.IsFreed);
        Assert.True(health.ItemAvailable);
        Assert.False(StartItem.LastSpawnFailed);
    }

    // Duel.OnFilterItemDefinition (Duel.cs:93) IS now subscribed into the FilterItem hook chain on
    // Activate() (QC MUTATOR_HOOKFUNCTION(duel, FilterItemDefinition)): with g_duel_with_powerups unset (0),
    // a powerup item must be FILTERED OUT (deleted + startitem_failed), exactly as Base. We activate the Duel
    // gametype directly (matching MayhemScoringTests) so the live subscription registers.
    [Fact]
    public void DuelActive_FiltersPowerup_WithoutDuelPowerups()
    {
        Boot(); // g_duel_with_powerups unset → powerups blocked in duel
        var duel = new Duel();
        duel.Activate(); // subscribes OnFilterItemDefinition into MutatorHooks.FilterItemDefinition
        try
        {
            var strength = Spawn("item_strength");
            // QC: instanceOfPowerup → return !autocvar_g_duel_with_powerups → true → delete(this) + failed.
            Assert.True(strength.IsFreed);
            Assert.True(StartItem.LastSpawnFailed);
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

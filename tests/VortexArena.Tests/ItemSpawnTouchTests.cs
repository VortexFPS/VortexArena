using System.Collections.Generic;
using System.Numerics;
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
/// Tests for T35 — the world-item spawn + touch pipeline (port of qcsrc/server/items/items.qc + spawning.qc):
/// the item_*/weapon_* spawnfuncs (StartItem), the Item_Touch gate + give + respawn tail, the powerup items
/// (not-spawn-at-start + mutator-block), the loot lifecycle, and the shared op-aware GiveItems grammar.
///
/// Runs in the GlobalState collection because it mutates the process-global registries + Api.Services.
/// </summary>
[Collection("GlobalState")]
public class ItemSpawnTouchTests
{
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

    // Boot a clean world: services + registries + status effects, then install the item spawnfuncs and seed the
    // pickup/powerup cvar defaults the gameplay reads (the same values Cvars.cs registers on the server).
    private static TestFacade Boot()
    {
        var facade = new TestFacade();
        Api.Services = facade;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();          // discovers the [Item] pickups + the Weapon registry
        Combat.System = new DamageSystem();
        Movement.System = new PlayerPhysics();
        ItemSpawnFuncs.Register();            // install item_*/weapon_* spawnfuncs (needs the registries above)
        SeedCvars(facade);
        return facade;
    }

    private static void SeedCvars(TestFacade f)
    {
        // mirror the T35 slice of Cvars.cs so a bare unit test gets authentic amounts + items actually spawn.
        void S(string n, string v) => f.Cvars.Set(n, v);
        S("g_pickup_items", "1");
        S("g_powerups", "1");
        S("g_powerups_strength", "1"); S("g_powerups_shield", "1"); S("g_powerups_speed", "1");
        S("g_powerups_invisibility", "1"); S("g_powerups_jetpack", "1"); S("g_powerups_fuelregen", "1");
        S("g_powerups_stack", "0");
        S("g_weapon_stay", "0");
        S("g_items_dropped_lifetime", "20");
        S("g_balance_powerup_strength_time", "30");
        S("g_pickup_healthsmall", "5"); S("g_pickup_healthsmall_max", "200");
        S("g_pickup_healthmega", "100"); S("g_pickup_healthmega_max", "200");
        S("g_pickup_armorsmall", "5"); S("g_pickup_armorsmall_max", "200");
        S("g_pickup_cells", "30"); S("g_pickup_cells_max", "180");
        S("g_pickup_shells", "15"); S("g_pickup_shells_max", "60");
        S("g_balance_health_limit", "200"); S("g_balance_armor_limit", "200");
    }

    private static Player NewPlayer(float health = 100f) => new()
    {
        Flags = EntFlags.Client,
        CanPickupItems = true,
        DeadState = DeadFlag.No,
        Health = health,
        Origin = Vector3.Zero,
        Mins = new Vector3(-16, -16, -24),
        Maxs = new Vector3(16, 16, 45),
    };

    // =====================================================================================
    //  Spawnfunc registration
    // =====================================================================================

    [Theory]
    [InlineData("item_health_small")]
    [InlineData("item_health_medium")]
    [InlineData("item_health_big")]
    [InlineData("item_health_mega")]
    [InlineData("item_armor_small")]
    [InlineData("item_armor_medium")]
    [InlineData("item_armor_big")]
    [InlineData("item_armor_mega")]
    [InlineData("item_shells")]
    [InlineData("item_bullets")]
    [InlineData("item_rockets")]
    [InlineData("item_cells")]
    [InlineData("item_fuel")]
    [InlineData("item_strength")]
    [InlineData("item_shield")]
    [InlineData("item_invincible")]
    [InlineData("item_speed")]
    [InlineData("item_jetpack")]
    // compat aliases (server/items/spawning.qc)
    [InlineData("item_armor1")]
    [InlineData("item_armor25")]
    [InlineData("item_health1")]
    [InlineData("item_health25")]
    [InlineData("item_health100")]
    [InlineData("item_armor_large")]
    [InlineData("item_health_large")]
    public void SpawnFunc_RegistersAllItemClassnames(string className)
    {
        Boot();
        var e = Api.Services!.Entities.Spawn();
        e.Origin = new Vector3(0, 0, 0);
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should be registered + spawn");
    }

    [Fact]
    public void SpawnFunc_RegistersWeaponClassnames()
    {
        Boot();
        // weapon_<netname> for every weapon in the registry (QC weapon_defaultspawnfunc).
        var w = Weapons.All[0];
        var e = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("weapon_" + w.NetName, e), $"weapon_{w.NetName} should be registered");
    }

    // The item defs carry the BARE model name (the QC Item_Model/W_Model macro argument); StartItem must build
    // the full VFS path or the client asset loader fails ("not found in any mount: item_armor_large.md3").
    [Theory]
    [InlineData("item_armor_mega", "models/items/item_armor_large.md3")]
    [InlineData("item_armor_small", "models/items/item_armor_small.md3")]
    [InlineData("item_health_mega", "models/items/g_h100.md3")]
    [InlineData("item_shells", "models/items/a_shells.md3")]
    [InlineData("item_strength", "models/items/g_strength.md3")]
    public void SpawnFunc_ItemModelGetsItemsDirPrefix(string className, string expectedModel)
    {
        Boot();
        var e = Api.Services!.Entities.Spawn();
        e.Origin = Vector3.Zero;
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should spawn");
        Assert.Equal(expectedModel, e.Model);
    }

    [Fact]
    public void SpawnFunc_WeaponPickupModelGetsWeaponsDirPrefix()
    {
        Boot();
        var w = Weapons.All[0];
        var e = Api.Services!.Entities.Spawn();
        e.Origin = Vector3.Zero;
        Assert.True(SpawnFuncs.TrySpawn("weapon_" + w.NetName, e));
        // QC W_Model: a world weapon pickup uses models/weapons/<v_model> (NOT the items dir).
        Assert.StartsWith("models/weapons/", e.Model);
    }

    // =====================================================================================
    //  Health pickup spawn + touch
    // =====================================================================================

    [Fact]
    public void HealthSmall_SpawnAndTouch_GivesHealth()
    {
        Boot();
        var item = Api.Services!.Entities.Spawn();
        item.Origin = new Vector3(0, 0, 0);
        Assert.True(SpawnFuncs.TrySpawn("item_health_small", item));

        // QC StartItem: a permanent item is SOLID_TRIGGER, available, with the Item_Touch handler + the live marker.
        Assert.Equal(Solid.Trigger, item.Solid);
        Assert.True(item.ItemAvailable);
        Assert.NotNull(item.Touch);
        Assert.NotEqual(0f, item.SpawnShieldExpire); // live marker (non-zero)
        Assert.Equal(5f, item.GetResource(ResourceType.Health), 3); // ItemInit seeded the give amount

        var p = NewPlayer(health: 1f);
        item.Touch!(item, p);

        Assert.Equal(6f, p.GetResource(ResourceType.Health), 3); // +5
        Assert.False(item.ItemAvailable);                        // consumed -> hidden/scheduled
        Assert.Equal(Solid.Not, item.Solid);
    }

    [Fact]
    public void HealthMega_LargeBox_NoGlow_AndCapBehavior()
    {
        Boot();
        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("item_health_mega", item));

        // QC: HealthMega uses ITEM_L_MAXS (large box) + the MEGAHEALTH pickup sound. It does NOT glow in QC
        // (no m_glow ATTRIB — only powerups glow; the old port wrongly set Glow=true).
        Assert.Equal(ItemBoxes.LargeMaxs, item.Maxs);
        Assert.Equal("MEGAHEALTH", item.Pickup!.ItemDef.PickupSound);
        Assert.False(item.Pickup!.ItemDef.Glow);

        // a 100hp player gains 100 (to the 200 cap); a 200hp player (at cap) gains nothing (pickup_anyway off).
        var p100 = NewPlayer(health: 100f);
        item.Touch!(item, p100);
        Assert.Equal(200f, p100.GetResource(ResourceType.Health), 3);
    }

    [Fact]
    public void HealthMega_AtCap_NotTaken()
    {
        Boot();
        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("item_health_mega", item));

        var p = NewPlayer(health: 200f); // already at the mega cap (200)
        item.Touch!(item, p);
        Assert.Equal(200f, p.GetResource(ResourceType.Health), 3); // unchanged
        Assert.True(item.ItemAvailable);                            // not consumed
    }

    // =====================================================================================
    //  Weapon pickup + weapon-stay
    // =====================================================================================

    [Fact]
    public void WeaponPickup_GivesWeaponInBothReps()
    {
        Boot();
        // pick a non-superweapon so Item_Reset shows it (superweapons schedule-initial-respawn, hidden at start).
        Weapon w = Weapons.All[0];
        foreach (var cand in Weapons.All) if (!cand.IsSuperWeapon) { w = cand; break; }

        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("weapon_" + w.NetName, item));
        Assert.True(item.OwnedWeaponSet.Has(w)); // the world item carries the weapon set (QC STAT(WEAPONS,item))
        Assert.Equal(Solid.Trigger, item.Solid); // a normal weapon spawns available

        var p = NewPlayer();
        item.Touch!(item, p);

        Assert.True(p.OwnedWeaponSet.Has(w));            // Entity.OwnedWeaponSet (WepSet)
        Assert.Contains(w.NetName, p.OwnedWeapons);      // Player.OwnedWeapons (NetName) — dual-rep
    }

    [Fact]
    public void WeaponStay_GivesOnlyWeaponNoAmmo_AndNoRespawn()
    {
        var f = Boot();
        f.Cvars.Set("g_weapon_stay", "1");

        // find a weapon that consumes ammo (so we can prove the stay-no-ammo rule).
        Weapon? w = null;
        foreach (var cand in Weapons.All)
            if (cand.AmmoType != ResourceType.None && !cand.IsSuperWeapon) { w = cand; break; }
        Assert.NotNull(w);

        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("weapon_" + w!.NetName, item));

        // QC Item_Show(item, 0): with g_weapon_stay set, a weapon becomes a translucent STILL-pickable ghost with
        // the live marker cleared (spawnshieldtime == 0) and ITS_STAYWEP set.
        ItemPickupRules.Show(item, 0);
        Assert.Equal(Solid.Trigger, item.Solid);
        Assert.Equal(0f, item.SpawnShieldExpire);  // 0 = stay marker
        Assert.True(item.ItemStayWeapon);

        var p = NewPlayer();
        float ammoBefore = p.GetResource(w.AmmoType);
        item.Touch!(item, p);

        Assert.True(p.OwnedWeaponSet.Has(w));                       // got the weapon
        Assert.Equal(ammoBefore, p.GetResource(w.AmmoType), 3);     // but NO ammo (stay marker, g_weapon_stay==1)
        Assert.Equal(0f, item.SpawnShieldExpire);                   // still a stay weapon (no respawn schedule)
        Assert.True(item.ItemAvailable);                            // a stay weapon stays available (no respawn)
    }

    // =====================================================================================
    //  Loot lifecycle
    // =====================================================================================

    [Fact]
    public void Loot_TossLifecycle_NoInstantPick_ThenRemovedOnPickup()
    {
        var f = Boot();
        var def = Items.ByName("health_medium")!;
        var item = Api.Services!.Entities.Spawn();
        item.Origin = new Vector3(0, 0, 0);
        f.GameClock.Time = 100f;

        var spawned = StartItem.SpawnLoot(item, def);
        Assert.NotNull(spawned);

        // QC loot branch: MOVETYPE_TOSS, gravity 1, anti-instant-pick spawnshield = time + 0.5.
        Assert.Equal(MoveType.Toss, item.MoveType);
        Assert.Equal(1f, item.Gravity, 3);
        Assert.Equal(100.5f, item.ItemSpawnShieldExpire, 3);
        Assert.True(item.ItemIsLoot);

        // touch during the spawnshield window -> NO pickup.
        var p = NewPlayer(health: 1f);
        item.Touch!(item, p);
        Assert.Equal(1f, p.GetResource(ResourceType.Health), 3); // unchanged

        // past the spawnshield -> picked up, and the loot is REMOVED (not respawned).
        f.GameClock.Time = 101f;
        item.Touch!(item, p);
        Assert.Equal(26f, p.GetResource(ResourceType.Health), 3); // +25 (medium)
        Assert.True(item.IsFreed);                                // RemoveItem
    }

    [Fact]
    public void Loot_DespawnsAfterLifetime()
    {
        var f = Boot();
        var def = Items.ByName("shells")!;
        var item = Api.Services!.Entities.Spawn();
        f.GameClock.Time = 0f;
        StartItem.SpawnLoot(item, def, lifetime: 20f);
        Assert.Equal(20f, item.ItemWait, 3);

        // before the lifetime -> Item_Think keeps it alive.
        f.GameClock.Time = 10f;
        item.Think!(item);
        Assert.False(item.IsFreed);

        // past the lifetime -> Item_Think removes it.
        f.GameClock.Time = 21f;
        item.Think!(item);
        Assert.True(item.IsFreed);
    }

    // =====================================================================================
    //  Powerups: don't spawn at start, mutator-block when disabled
    // =====================================================================================

    [Fact]
    public void Powerup_DoesNotSpawnAtMatchStart()
    {
        Boot(); // g_powerups = 1
        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("item_strength", item));

        // QC: Item_Reset routes a powerup to Item_ScheduleInitialRespawn -> Item_Show(0) (hidden initially).
        Assert.False(item.ItemAvailable);
        Assert.False(item.IsFreed);                 // it exists, just hidden until its first scheduled spawn
        // and the world item carries the seeded strength duration (ItemInit).
        Assert.Equal(30f, item.StrengthFinished, 3);
    }

    [Fact]
    public void Powerup_Blocked_WhenDisabled_IsDeleted()
    {
        var f = Boot();
        f.Cvars.Set("g_powerups_strength", "0"); // disable strength only

        var item = Api.Services!.Entities.Spawn();
        bool spawned = SpawnFuncs.TrySpawn("item_strength", item);
        // QC: powerup_strength_init flags the def MUTATORBLOCKED, have_pickup_item returns false, StartItem deletes it.
        Assert.True(item.IsFreed);
        // (TrySpawn returns true because a spawnfunc ran; the item entity is removed by StartItem.)
        Assert.True(StartItem.LastSpawnFailed);
    }

    [Fact]
    public void Powerup_Touch_AppliesStatusEffect()
    {
        Boot();
        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("item_strength", item));

        // manually show it (it starts hidden) so we can touch it.
        ItemPickupRules.Show(item, 1);
        var p = NewPlayer();
        item.Touch!(item, p);

        var def = StatusEffectsCatalog.ByName("strength");
        Assert.NotNull(def);
        Assert.True(StatusEffectsCatalog.Has(p, def!), "strength should be applied on pickup");
    }

    [Fact]
    public void Jetpack_Touch_GivesBitAndFuel()
    {
        Boot();
        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("item_jetpack", item));
        ItemPickupRules.Show(item, 1);

        var p = NewPlayer();
        p.SetResourceExplicit(ResourceType.Fuel, 0f);
        item.Touch!(item, p);

        Assert.True((p.Items & (int)ItemFlag.Jetpack) != 0, "IT_JETPACK should transfer (IT_PICKUPMASK)");
        Assert.True(p.GetResource(ResourceType.Fuel) > 0f, "the jetpack refills fuel");
    }

    // =====================================================================================
    //  GiveItems operator grammar
    // =====================================================================================

    [Fact]
    public void GiveItems_OperatorGrammar_ResourceOps()
    {
        Boot();
        var p = NewPlayer(health: 100f);

        // max 50 health -> min(100, 50) = 50.
        GiveItems.Apply(p, new[] { "max", "50", "health" });
        Assert.Equal(50f, p.GetResource(ResourceType.Health), 3);

        // min 80 health on 50 -> max(50, 80) = 80.
        GiveItems.Apply(p, new[] { "min", "80", "health" });
        Assert.Equal(80f, p.GetResource(ResourceType.Health), 3);

        // plus 25 health -> 105.
        GiveItems.Apply(p, new[] { "plus", "25", "health" });
        Assert.Equal(105f, p.GetResource(ResourceType.Health), 3);

        // minus 30 cells on 50 -> 20.
        p.SetResourceExplicit(ResourceType.Cells, 50f);
        GiveItems.Apply(p, new[] { "minus", "30", "cells" });
        Assert.Equal(20f, p.GetResource(ResourceType.Cells), 3);
    }

    [Fact]
    public void GiveItems_NumericResetsAfterNameToken()
    {
        Boot();
        var p = NewPlayer(health: 100f);
        p.SetResourceExplicit(ResourceType.Armor, 0f);

        // "100 health armor": val=100 applies to health (->100), then RESETS to 999 before armor (->999).
        GiveItems.Apply(p, new[] { "100", "health", "armor" });
        Assert.Equal(100f, p.GetResource(ResourceType.Health), 3);
        Assert.Equal(999f, p.GetResource(ResourceType.Armor), 3);
    }

    [Fact]
    public void GiveItems_Aggregate_All_CascadesToWeaponsAndAmmo()
    {
        Boot();
        var p = NewPlayer(health: 1f);
        p.SetResourceExplicit(ResourceType.Cells, 0f);

        int got = GiveItems.Apply(p, new[] { "all" });
        Assert.True(got > 0);

        // QC FALLTHROUGH: `all` includes allweapons + allammo. Default val 999 -> health/armor/ammo = 999.
        Assert.Equal(999f, p.GetResource(ResourceType.Health), 3);
        Assert.Equal(999f, p.GetResource(ResourceType.Armor), 3);
        Assert.Equal(999f, p.GetResource(ResourceType.Cells), 3);
        Assert.True((p.Items & (int)ItemFlag.Jetpack) != 0, "all sets the jetpack bit");
        // and at least one (non-hidden) weapon was granted in BOTH reps.
        Assert.True(p.OwnedWeaponSet.CountSet > 0);
        Assert.True(p.OwnedWeapons.Count > 0);
    }

    [Fact]
    public void GiveItems_AllAmmo_OnlyAmmo()
    {
        Boot();
        var p = NewPlayer(health: 50f);
        int weaponsBefore = p.OwnedWeaponSet.CountSet;

        GiveItems.Apply(p, new[] { "allammo" });

        Assert.Equal(999f, p.GetResource(ResourceType.Cells), 3);
        Assert.Equal(999f, p.GetResource(ResourceType.Shells), 3);
        Assert.Equal(50f, p.GetResource(ResourceType.Health), 3);          // untouched by allammo
        Assert.Equal(weaponsBefore, p.OwnedWeaponSet.CountSet);            // no weapons from allammo
    }

    [Fact]
    public void GiveItems_NoToken_ClearsBit()
    {
        Boot();
        var p = NewPlayer();
        p.Items |= (int)ItemFlag.Jetpack;

        // "no jetpack" -> OP_MAX, val 0 -> clears the bit.
        GiveItems.Apply(p, new[] { "no", "jetpack" });
        Assert.True((p.Items & (int)ItemFlag.Jetpack) == 0);
    }

    [Fact]
    public void GiveItems_WeaponToken_DualRep()
    {
        Boot();
        var p = NewPlayer();
        var w = Weapons.All[0];

        GiveItems.Apply(p, new[] { w.NetName });
        Assert.True(p.OwnedWeaponSet.Has(w));
        Assert.Contains(w.NetName, p.OwnedWeapons);

        // "no <weapon>" removes it from both reps.
        GiveItems.Apply(p, new[] { "no", w.NetName });
        Assert.False(p.OwnedWeaponSet.Has(w));
        Assert.DoesNotContain(w.NetName, p.OwnedWeapons);
    }

    // =====================================================================================
    //  Gates
    // =====================================================================================

    [Fact]
    public void ItemTouch_Gate_RejectsNonPickerAndDeadAndOwner()
    {
        Boot();
        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("item_health_small", item));

        // an entity without CanPickupItems (e.g. a projectile / observer) does not pick up.
        var notPicker = NewPlayer(health: 1f);
        notPicker.CanPickupItems = false;
        item.Touch!(item, notPicker);
        Assert.Equal(1f, notPicker.GetResource(ResourceType.Health), 3);
        Assert.True(item.ItemAvailable);

        // a dead picker does not pick up.
        var dead = NewPlayer(health: 1f);
        dead.DeadState = DeadFlag.Dead;
        item.Touch!(item, dead);
        Assert.Equal(1f, dead.GetResource(ResourceType.Health), 3);

        // the item's own owner does not pick it up (loot anti-pickup-by-thrower).
        var owner = NewPlayer(health: 1f);
        item.Owner = owner;
        item.Touch!(item, owner);
        Assert.Equal(1f, owner.GetResource(ResourceType.Health), 3);
    }

    // =====================================================================================
    //  Pickup/respawn particle bursts (QC client/items/items.qc EFFECT_ITEM_PICKUP/_RESPAWN).
    //  The port emits them server-side over the Send_Effect channel on the authoritative event;
    //  here we read them off the EffectEmitter recording sink. Effects register in GameInit, not the
    //  bare item Boot(), so each test calls Effects.RegisterAll() (idempotent by name) first.
    // =====================================================================================

    [Fact]
    public void ItemTouch_TakenItem_EmitsPickupEffectAtBboxCentre()
    {
        Boot();
        Effects.RegisterAll();
        EffectEmitter.Sink = EffectEmitter.Recorder;

        var item = Api.Services!.Entities.Spawn();
        item.Origin = new Vector3(64, -32, 16);
        Assert.True(SpawnFuncs.TrySpawn("item_health_small", item));

        // QC client/items/items.qc:271 fires the burst at (absmin + absmax) * 0.5; the port computes the
        // equivalent origin + (mins + maxs) * 0.5 (stable headless, where the engine link skips AbsMin/Max).
        var expectedCentre = item.Origin + (item.Mins + item.Maxs) * 0.5f;

        EffectEmitter.Recorder.Clear();
        item.Touch!(item, NewPlayer(health: 1f)); // a 1hp player takes the +5 -> the give succeeds

        Assert.Equal(1, EffectEmitter.Recorder.Count);
        var fx = EffectEmitter.Recorder.Last;
        Assert.Equal("ITEM_PICKUP", fx.Effect?.Name);
        Assert.Equal(expectedCentre, fx.Origin);
    }

    [Fact]
    public void ItemTouch_NotTaken_EmitsNoPickupEffect()
    {
        Boot();
        Effects.RegisterAll();
        EffectEmitter.Sink = EffectEmitter.Recorder;

        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn("item_health_mega", item));

        // QC: the burst plays only when the give actually happens (Item_Touch returns early if !gave). A
        // capped player takes nothing, so the item stays available and no pickup effect is emitted.
        EffectEmitter.Recorder.Clear();
        item.Touch!(item, NewPlayer(health: 200f)); // already at the mega cap -> nothing taken

        Assert.Equal(0, EffectEmitter.Recorder.Count);
        Assert.True(item.ItemAvailable);
    }

    [Fact]
    public void ItemRespawn_EmitsRespawnEffectAndReshows()
    {
        Boot();
        Effects.RegisterAll();
        EffectEmitter.Sink = EffectEmitter.Recorder;

        var item = Api.Services!.Entities.Spawn();
        item.Origin = new Vector3(0, 96, 8);
        Assert.True(SpawnFuncs.TrySpawn("item_health_small", item));
        ItemPickupRules.Show(item, -1); // hide it first, as a post-pickup item would be
        Assert.False(item.ItemAvailable);

        var expectedCentre = item.Origin + (item.Mins + item.Maxs) * 0.5f;

        EffectEmitter.Recorder.Clear();
        ItemPickupRules.Respawn(item); // QC Item_Respawn: re-show + the respawn sparkle (items.qc:258)

        Assert.Equal(1, EffectEmitter.Recorder.Count);
        var fx = EffectEmitter.Recorder.Last;
        Assert.Equal("ITEM_RESPAWN", fx.Effect?.Name);
        Assert.Equal(expectedCentre, fx.Origin);
        Assert.True(item.ItemAvailable); // respawn makes it available again
    }

    // =====================================================================================
    //  Loot despawn FX (EFFECT_ITEM_DESPAWN) — the CONTINUOUS client-side animation QC runs while
    //  ITS_EXPIRING is set (client/items/items.qc:191-210), NOT a single server emit. Two halves:
    //   • SERVER: Item_Think raises Entity.ItemExpiringFx (networked as NetEntityFlags.ItemExpiring) during
    //     the last IT_DESPAWNFX_TIME seconds of a loot item's life, then hands off to RemoveItem at the wait.
    //   • CLIENT: the pure ItemDespawnFx timer fades alpha + emits puffs on the accelerating 0.25→0.0625
    //     cadence, honoring cl_items_animate bits 2 (fade) and 4 (particles).
    // =====================================================================================

    [Fact]
    public void Loot_ItemThink_FlagsExpiring_InDespawnWindow_ThenRemovesAtWait()
    {
        var f = Boot();
        var def = Items.ByName("shells")!;
        var item = Api.Services!.Entities.Spawn();
        f.GameClock.Time = 0f;
        StartItem.SpawnLoot(item, def, lifetime: 20f); // QC wait = time + lifetime = 20
        Assert.Equal(20f, item.ItemWait, 3);
        Assert.False(item.ItemExpiringFx);

        // OUTSIDE the despawn-fx window (more than IT_DESPAWNFX_TIME=1.5 before wait): not flagged, just re-ticks
        // — and the reschedule never overshoots the window start (QC "ensuring full time for effects").
        f.GameClock.Time = 18f; // 20 - 1.5 = 18.5, so 18 < 18.5
        item.Think!(item);
        Assert.False(item.ItemExpiringFx);
        Assert.False(item.IsFreed);
        Assert.Equal(18f + ItemPickupRules.ItemUpdateInterval, item.NextThink, 3);

        f.GameClock.Time = 18.45f; // next tick would pass 18.5 → clamp to the window start
        item.Think!(item);
        Assert.False(item.ItemExpiringFx);
        Assert.Equal(18.5f, item.NextThink, 3); // min(18.45+0.0625, 18.5) = 18.5

        // INSIDE the window (time >= wait - IT_DESPAWNFX_TIME): raise the expiring flag (networked to the client)
        // and keep ticking at IT_UPDATE_INTERVAL.
        f.GameClock.Time = 18.6f;
        item.Think!(item);
        Assert.True(item.ItemExpiringFx);
        Assert.False(item.IsFreed); // still animating the fx window
        Assert.Equal(18.6f + ItemPickupRules.ItemUpdateInterval, item.NextThink, 3);

        // At the wait time the item hands off to RemoveItem (QC setthink(RemoveItem); nextthink = wait).
        f.GameClock.Time = 20f;
        item.Think!(item);
        Assert.True(item.IsFreed);
    }

    // =====================================================================================
    //  Bob+spin animation class (QC ITS_ANIMATE1/2, server/items/items.qc:1198-1213 + client ItemDraw).
    //  StartItem tags each item with its animation class; the client (EntityNode) floats + spins it and the
    //  bob's base offset lifts the model clear of the floor (fixes a tall item like megahealth rendering sunk).
    // =====================================================================================

    [Theory]
    [InlineData("health_mega", (byte)2)]  // health → ANIMATE2 (-90°/s, low bob)
    [InlineData("armor_mega", (byte)2)]   // armor  → ANIMATE2
    [InlineData("health_small", (byte)2)] // small health/armor animate too (QC keys on the resource, not size)
    [InlineData("strength", (byte)1)]     // powerup → ANIMATE1 (+180°/s, high bob)
    [InlineData("shells", (byte)0)]       // ammo → static (no bob/spin)
    public void StartItem_TagsAnimationClass(string netName, byte expected)
    {
        var f = Boot();
        var def = Items.ByName(netName)!;
        var item = Api.Services!.Entities.Spawn();
        f.GameClock.Time = 0f;
        StartItem.SpawnLoot(item, def);
        Assert.Equal(expected, item.ItemAnimate);
    }

    [Fact]
    public void StartItem_Spawnflag1024_SuppressesAnimation()
    {
        var f = Boot();
        var def = Items.ByName("health_mega")!;
        var item = Api.Services!.Entities.Spawn();
        item.SpawnFlags = 1024; // QC: the no-animate spawnflag
        f.GameClock.Time = 0f;
        StartItem.SpawnLoot(item, def);
        Assert.Equal((byte)0, item.ItemAnimate);
    }

    [Fact]
    public void ItemBobAnim_MatchesQcWaveforms()
    {
        // QC client/items/items.qc: ANIMATE1 = (10 + 8·sin(2t), 180·t); ANIMATE2 = (8 + 4·sin(3t), -90·t).
        // At t=0 the bob sits at its base offset (the lift that keeps the model out of the floor) and no spin.
        var (bob1, yaw1) = ItemBobAnim.Sample(ItemBobAnim.Animate1, 0f);
        Assert.Equal(10f, bob1, 4);
        Assert.Equal(0f, yaw1, 4);

        var (bob2, yaw2) = ItemBobAnim.Sample(ItemBobAnim.Animate2, 0f);
        Assert.Equal(8f, bob2, 4);
        Assert.Equal(0f, yaw2, 4);

        // The base offset is always positive (lift never sinks below the resting origin): min bob = base − amp.
        for (float t = 0f; t < 7f; t += 0.13f)
        {
            Assert.True(ItemBobAnim.Sample(ItemBobAnim.Animate1, t).bobHeight >= 2f - 1e-4f); // 10 − 8
            Assert.True(ItemBobAnim.Sample(ItemBobAnim.Animate2, t).bobHeight >= 4f - 1e-4f); // 8 − 4
        }

        // Spin direction + rate at t=1s: ANIMATE1 +180°, ANIMATE2 −90°.
        Assert.Equal(180f, ItemBobAnim.Sample(ItemBobAnim.Animate1, 1f).yawDeg, 4);
        Assert.Equal(-90f, ItemBobAnim.Sample(ItemBobAnim.Animate2, 1f).yawDeg, 4);

        // Static class: no movement.
        Assert.Equal((0f, 0f), ItemBobAnim.Sample(0, 3.7f));
    }

    [Fact]
    public void ItemDespawnFx_FirstTick_SeedsWindow_FullAlpha_EmitsImmediately()
    {
        var fx = new ItemDespawnFx();
        Assert.False(fx.Started);

        fx.Tick(time: 100f, animateFlags: 7, out float alpha, out bool emit);

        Assert.True(fx.Started);
        Assert.Equal(100f + ItemPickupRules.DespawnFxTime, fx.DespawnTime, 3); // QC: wait = time + IT_DESPAWNFX_TIME
        Assert.Equal(1f, alpha, 3);   // full alpha at the window start
        Assert.True(emit);            // QC: pointtime starts at 0 → the first puff fires immediately
    }

    [Fact]
    public void ItemDespawnFx_AlphaFadesLinearlyToZero_OverWindow()
    {
        var fx = new ItemDespawnFx();
        fx.Tick(100f, 7, out float a0, out _);       // window: 100 .. 101.5
        Assert.Equal(1f, a0, 3);
        fx.Tick(100.75f, 7, out float aMid, out _);  // halfway through the 1.5s window
        Assert.Equal(0.5f, aMid, 2);
        fx.Tick(101.5f, 7, out float aEnd, out _);   // at the despawn time
        Assert.Equal(0f, aEnd, 3);
    }

    [Fact]
    public void ItemDespawnFx_PuffCadence_AcceleratesFromQuarterToSixteenth()
    {
        var fx = new ItemDespawnFx();
        var emits = new List<float>();
        const float start = 100f;
        // Fine, drift-free sampling (t = start + ms*0.001 rather than accumulating) over the 1.5s window.
        for (int ms = 0; ms <= 1500; ms++)
        {
            float t = start + ms * 0.001f;
            fx.Tick(t, 7, out _, out bool emit);
            if (emit) emits.Add(t);
        }

        Assert.True(emits.Count >= 18, $"expected many puffs over the window, got {emits.Count}");
        Assert.Equal(start, emits[0], 2); // first puff immediate

        var gaps = new List<float>();
        for (int i = 1; i < emits.Count; i++)
            gaps.Add(emits[i] - emits[i - 1]);

        // QC: delay 0.25 halves to 0.125 BEFORE the first interval is applied, so the first gap is ~0.125,
        // then it halves once more and settles at the 0.0625 floor.
        Assert.InRange(gaps[0], 0.115f, 0.135f);
        Assert.InRange(gaps[gaps.Count - 1], 0.058f, 0.068f);
        // Monotonic non-increasing (accelerating), within the 1ms sampling tolerance.
        for (int i = 1; i < gaps.Count; i++)
            Assert.True(gaps[i] <= gaps[i - 1] + 0.0015f, $"gap[{i}]={gaps[i]} > gap[{i - 1}]={gaps[i - 1]}");
        // The interval converged to the 0.0625 floor.
        Assert.Equal(0.0625f, fx.CurrentDelay, 4);
    }

    [Fact]
    public void ItemDespawnFx_HonorsClItemsAnimateBits()
    {
        // bit 4 only (particles, no fade): emits puffs; alpha stays 1.
        var particlesOnly = new ItemDespawnFx();
        particlesOnly.Tick(100f, ItemDespawnFx.AnimateParticlesBit, out float aP, out bool eP);
        Assert.Equal(1f, aP, 3);
        Assert.True(eP);

        // bit 2 only (fade, no particles): fades alpha; never emits.
        var fadeOnly = new ItemDespawnFx();
        fadeOnly.Tick(100f, ItemDespawnFx.AnimateAlphaBit, out float aA, out bool eA);
        Assert.Equal(1f, aA, 3);
        Assert.False(eA);
        fadeOnly.Tick(100.75f, ItemDespawnFx.AnimateAlphaBit, out float aA2, out bool eA2);
        Assert.Equal(0.5f, aA2, 2);
        Assert.False(eA2);

        // neither bit: window still seeded (QC seeds .wait outside the cvar gates), no fade, no particles.
        var off = new ItemDespawnFx();
        off.Tick(100f, 0, out float aOff, out bool eOff);
        Assert.True(off.Started);
        Assert.Equal(1f, aOff, 3);
        Assert.False(eOff);
    }

    // =====================================================================================
    //  Pickup-sound wiring (regression: a pickup whose PickupSound name isn't in the Sounds
    //  catalog resolves to no sample and plays SILENTLY — the bug that made every health/armor
    //  except mega health soundless. PlayPickupSound passes ItemDef.PickupSound to the client's
    //  OnSound, which does Sounds.ByName(name); an unregistered name => LoadStream(name) => null.)
    // =====================================================================================

    [Fact]
    public void EveryPickupSound_ResolvesToARegisteredSample()
    {
        Boot();
        Sounds.RegisterAll(); // the sound catalog (GameInit wires this at boot; idempotent)

        var unresolved = new List<string>();
        foreach (Pickup p in Items.All)
        {
            string name = p.ItemDef.PickupSound;
            // empty/null is allowed (no sound); a non-empty name MUST resolve to a registered GameSound, else
            // the pickup is silent on the client.
            if (!string.IsNullOrEmpty(name) && Sounds.ByName(name) is null)
                unresolved.Add($"{p.NetName} -> \"{name}\"");
        }

        Assert.True(unresolved.Count == 0,
            "These pickups reference an unregistered pickup sound (silent on pickup): "
            + string.Join(", ", unresolved));
    }

    [Theory]
    [InlineData("item_health_small", "HealthSmall")]
    [InlineData("item_health_medium", "HealthMedium")]
    [InlineData("item_health_big", "HealthBig")]
    [InlineData("item_health_mega", "MEGAHEALTH")]
    [InlineData("item_armor_small", "ArmorSmall")]
    [InlineData("item_armor_medium", "ArmorMedium")]
    [InlineData("item_armor_big", "ArmorBig")]
    [InlineData("item_armor_mega", "ArmorMega")]
    public void HealthArmorPickupSounds_AreRegistered(string spawnFunc, string expectedSound)
    {
        Boot();
        Sounds.RegisterAll();

        var item = Api.Services!.Entities.Spawn();
        Assert.True(SpawnFuncs.TrySpawn(spawnFunc, item));
        Assert.Equal(expectedSound, item.Pickup!.ItemDef.PickupSound);

        // the name must resolve to a real (non-empty) sample path under sound/.
        GameSound? snd = Sounds.ByName(expectedSound);
        Assert.NotNull(snd);
        Assert.StartsWith("misc/", snd!.Sample);
    }
}

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
/// Tests for T52 — the ADD side of the Q3/QL/CPMA/Q1/Q2/WoP/Q3DF compatibility remaps
/// (port of qcsrc/server/compat/{quake3,quake,quake2,wop}.qc + quake3.qh). Covers the weapon/item/ammo
/// classname remaps installed by ItemSpawnFuncs.Register, the Q3 ammo .count scaling
/// (CompatRemaps.GetAmmoConsumption / ApplyAmmoRemap), and the four Q3DF target_* entities.
///
/// Runs in the GlobalState collection because it mutates the process-global registries + Api.Services.
/// </summary>
[Collection("GlobalState")]
public class CompatRemapsTests
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

    private static TestFacade Boot()
    {
        var facade = new TestFacade();
        Api.Services = facade;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();          // discovers the [Item] pickups + the Weapon registry
        Combat.System = new DamageSystem();
        Movement.System = new PlayerPhysics();
        ItemSpawnFuncs.Register();            // installs item_*/weapon_*/ammo_* + the compat remaps
        CompatRemaps.Register();              // installs the Q3DF target_* spawnfuncs
        CompatRemaps.IsCtsActive = null;      // default: not CTS (re-set per test as needed)
        MapMover.ClearIndex();                // drop any targetname links left by a prior test
        SeedCvars(facade);
        return facade;
    }

    private static void SeedCvars(TestFacade f)
    {
        void S(string n, string v) => f.Cvars.Set(n, v);
        S("g_pickup_items", "1");
        S("g_powerups", "1");
        S("g_powerups_strength", "1"); S("g_powerups_shield", "1"); S("g_powerups_speed", "1");
        S("g_powerups_invisibility", "1"); S("g_powerups_jetpack", "1"); S("g_powerups_fuelregen", "1");
        S("g_weapon_stay", "0");
        // ammo pickup amounts + caps (so the ammo items can spawn + cap correctly).
        S("g_pickup_shells", "15"); S("g_pickup_shells_max", "999");
        S("g_pickup_nails", "80"); S("g_pickup_nails_max", "999");
        S("g_pickup_rockets", "40"); S("g_pickup_rockets_max", "999");
        S("g_pickup_cells", "30"); S("g_pickup_cells_max", "999");
        S("g_pickup_fuel", "50"); S("g_pickup_fuel_max", "999");
    }

    private static Entity Spawn() => Api.Services!.Entities.Spawn();

    private static Player NewPlayer(float health = 100f) => new()
    {
        Flags = EntFlags.Client,
        DeadState = DeadFlag.No,
        Health = health,
        Origin = Vector3.Zero,
        Mins = new Vector3(-16, -16, -24),
        Maxs = new Vector3(16, 16, 45),
    };

    // =====================================================================================
    //  Weapon classname remaps (quake3.qc / quake.qc / wop.qc)
    // =====================================================================================

    [Theory]
    [InlineData("weapon_railgun", "vortex")]            // Rail Gun -> Vortex
    [InlineData("weapon_rocketlauncher", "devastator")] // Rocket Launcher -> Devastator
    [InlineData("weapon_grenadelauncher", "mortar")]    // Grenade Launcher -> Mortar
    [InlineData("weapon_lightning", "electro")]         // Lightning Gun -> Electro
    [InlineData("weapon_prox_launcher", "minelayer")]   // Proximity Launcher -> Mine Layer
    [InlineData("weapon_chaingun", "hagar")]            // Chain Gun -> Hagar
    [InlineData("weapon_hmg", "hagar")]                 // Heavy MachineGun -> Hagar
    [InlineData("weapon_grapplinghook", "hook")]        // Grappling Hook -> Hook
    [InlineData("weapon_gauntlet", "tuba")]             // Gauntlet -> Tuba
    [InlineData("weapon_shotgun", "shotgun")]           // non-arena: Shotgun -> Shotgun
    [InlineData("weapon_machinegun", "machinegun")]     // non-arena: MachineGun -> MachineGun
    // Q1
    [InlineData("weapon_supernailgun", "hagar")]
    [InlineData("weapon_supershotgun", "machinegun")]
    // WoP
    [InlineData("weapon_punchy", "arc")]
    [InlineData("weapon_splasher", "vortex")]
    [InlineData("weapon_betty", "devastator")]
    public void WeaponRemap_ResolvesToExpectedWeapon(string className, string expectedNetName)
    {
        Boot();
        var e = Spawn();
        e.Origin = Vector3.Zero;
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should be registered");
        // the world item carries the remapped weapon (QC STAT(WEAPONS, item)).
        var w = Weapons.ByName(expectedNetName)!;
        Assert.True(e.OwnedWeaponSet.Has(w), $"{className} -> {expectedNetName}");
    }

    [Fact]
    public void NailgunRemap_BranchesOnMapformat()
    {
        // QC: weapon_nailgun -> autocvar_sv_mapformat_is_quake3 ? WEP_CRYLINK : WEP_ELECTRO.
        var f = Boot();
        f.Cvars.Set("sv_mapformat_is_quake3", "1");
        Assert.Equal("crylink", CompatRemaps.WeaponForClassname("weapon_nailgun")!.NetName);

        f.Cvars.Set("sv_mapformat_is_quake3", "0");
        Assert.Equal("electro", CompatRemaps.WeaponForClassname("weapon_nailgun")!.NetName);
    }

    [Fact]
    public void PlasmagunAndBfg_BranchOnXdfBalance()
    {
        var f = Boot();
        f.Cvars.Set("g_mod_balance", "XDF");
        Assert.Equal("hagar", CompatRemaps.WeaponForClassname("weapon_plasmagun")!.NetName);
        Assert.Equal("crylink", CompatRemaps.WeaponForClassname("weapon_bfg")!.NetName);

        f.Cvars.Set("g_mod_balance", "");
        Assert.Equal("hlac", CompatRemaps.WeaponForClassname("weapon_plasmagun")!.NetName);
        Assert.Equal("fireball", CompatRemaps.WeaponForClassname("weapon_bfg")!.NetName);
    }

    // =====================================================================================
    //  Item classname remaps (quake3.qc / quake2.qc / quake.qc / wop.qc)
    // =====================================================================================

    [Theory]
    // quake3.qc armor + powerups + medkit
    [InlineData("item_armor_body", "armor_mega")]
    [InlineData("item_armor_combat", "armor_big")]
    [InlineData("item_armor_shard", "armor_small")]
    [InlineData("item_armor_green", "armor_medium")]
    [InlineData("item_quad", "strength")]
    [InlineData("item_enviro", "invincible")]
    [InlineData("item_haste", "speed")]
    [InlineData("item_invis", "invisibility")]
    [InlineData("holdable_medkit", "armor_big")]
    // quake2.qc
    [InlineData("item_armor_jacket", "armor_medium")]
    [InlineData("item_invulnerability", "invincible")]
    // quake.qc
    [InlineData("item_spikes", "bullets")]
    [InlineData("item_armor2", "armor_mega")]
    [InlineData("item_armorInv", "armor_mega")]
    // wop.qc
    [InlineData("item_padpower", "strength")]
    [InlineData("item_climber", "invincible")]
    [InlineData("item_armor_padshield", "armor_mega")]
    [InlineData("holdable_floater", "jetpack")]
    [InlineData("ammo_pumper", "shells")]
    [InlineData("ammo_nipper", "bullets")]
    [InlineData("ammo_balloony", "rockets")]
    public void ItemRemap_SpawnsExpectedDef(string className, string expectedNetName)
    {
        Boot();
        var e = Spawn();
        e.Origin = Vector3.Zero;
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should be registered");
        Assert.NotNull(e.Pickup);
        Assert.Equal(expectedNetName, e.Pickup!.NetName);
    }

    [Fact]
    public void Q1Health_BranchesOnSpawnflag2()
    {
        // QC: item_health -> this.spawnflags & 2 ? ITEM_HealthMega : ITEM_HealthMedium.
        Boot();
        var mega = Spawn();
        mega.SpawnFlags = 2;
        Assert.True(SpawnFuncs.TrySpawn("item_health", mega));
        Assert.Equal("health_mega", mega.Pickup!.NetName);

        var medium = Spawn();
        medium.SpawnFlags = 0;
        Assert.True(SpawnFuncs.TrySpawn("item_health", medium));
        Assert.Equal("health_medium", medium.Pickup!.NetName);
    }

    // =====================================================================================
    //  GetAmmoConsumption (sv_resources.qc:231) — the per-shot ammo table
    // =====================================================================================

    [Theory]
    [InlineData("electro", 4f)]       // default WEP_CVAR_PRI(electro, ammo)
    [InlineData("vortex", 6f)]        // default WEP_CVAR_PRI(vortex, ammo)
    [InlineData("shotgun", 1f)]       // default WEP_CVAR_PRI(shotgun, ammo)
    [InlineData("mortar", 2f)]        // default WEP_CVAR_PRI(mortar, ammo)
    [InlineData("crylink", 3f)]       // default WEP_CVAR_PRI(crylink, ammo)
    [InlineData("hagar", 1f)]         // default WEP_CVAR_PRI(hagar, ammo)
    [InlineData("hlac", 1f)]          // default WEP_CVAR_PRI(hlac, ammo)
    [InlineData("devastator", 4f)]    // special: WEP_CVAR(devastator, ammo)
    [InlineData("machinegun", 1f)]    // special: WEP_CVAR(machinegun, sustained_ammo)
    [InlineData("minelayer", 4f)]     // special: WEP_CVAR(mine_layer, ammo)
    public void GetAmmoConsumption_MatchesQcDefaults(string netName, float expected)
    {
        Boot();
        var w = Weapons.ByName(netName)!;
        Assert.Equal(expected, CompatRemaps.GetAmmoConsumption(w), 3);
    }

    [Fact]
    public void GetAmmoConsumption_AmmolessWeapon_IsZero()
    {
        // QC: if (wpn.ammo_type == RES_NONE) return 0 — FIREBALL/grapplinghook/Tuba have no ammo type.
        Boot();
        Assert.Equal(0f, CompatRemaps.GetAmmoConsumption(Weapons.ByName("fireball")!), 3);
        Assert.Equal(0f, CompatRemaps.GetAmmoConsumption(Weapons.ByName("tuba")!), 3);
    }

    // =====================================================================================
    //  Q3 ammo .count scaling (the SPAWNFUNC_Q3AMMO half)
    // =====================================================================================

    [Fact]
    public void AmmoLightning_Count100_Scales125x_ThenConsumption_To50Cells()
    {
        // QC: ammo_lightning -> WEP_ELECTRO, 0.125x; rint(100 * 0.125 * GetAmmoConsumption(ELECTRO=4)) = 50.
        Boot();
        var e = Spawn();
        e.Origin = Vector3.Zero;
        e.Count = 100; // the map's `count` key
        Assert.True(SpawnFuncs.TrySpawn("ammo_lightning", e));
        Assert.Equal("cells", e.Pickup!.NetName);            // ELECTRO ammo_type -> cells
        Assert.Equal(50f, e.GetResource(ResourceType.Cells), 3);
    }

    [Fact]
    public void AmmoSlugs_Count10_VortexConsumption_To60Cells()
    {
        // QC: ammo_slugs -> WEP_VORTEX (no multiplier); rint(10 * GetAmmoConsumption(VORTEX=6)) = 60.
        Boot();
        var e = Spawn();
        e.Count = 10;
        Assert.True(SpawnFuncs.TrySpawn("ammo_slugs", e));
        Assert.Equal("cells", e.Pickup!.NetName);
        Assert.Equal(60f, e.GetResource(ResourceType.Cells), 3);
    }

    [Fact]
    public void AmmoShells_NonArena_MapsToShotgunShells()
    {
        // QC (non-arena): ammo_shells -> WEP_SHOTGUN (1x); rint(10 * GetAmmoConsumption(SHOTGUN=1)) = 10 shells.
        Boot();
        var e = Spawn();
        e.Count = 10;
        Assert.True(SpawnFuncs.TrySpawn("ammo_shells", e));
        Assert.Equal("shells", e.Pickup!.NetName);
        Assert.Equal(10f, e.GetResource(ResourceType.Shells), 3);
    }

    [Fact]
    public void AmmoBullets_NonArena_MapsToMachinegunBullets()
    {
        // QC (non-arena): ammo_bullets -> WEP_MACHINEGUN (1x); rint(50 * sustained_ammo=1) = 50 bullets.
        Boot();
        var e = Spawn();
        e.Count = 50;
        Assert.True(SpawnFuncs.TrySpawn("ammo_bullets", e));
        Assert.Equal("bullets", e.Pickup!.NetName);
        Assert.Equal(50f, e.GetResource(ResourceType.Bullets), 3);
    }

    [Fact]
    public void AmmoBox_NoCountKey_UsesItemDefaultAmount()
    {
        // QC: `if(this.count && ...)` — a 0 .count leaves the resource at the ammo item's ItemInit default.
        Boot();
        var e = Spawn();
        e.Count = 0;
        Assert.True(SpawnFuncs.TrySpawn("ammo_rockets", e)); // DEVASTATOR ammo_type -> rockets
        Assert.Equal("rockets", e.Pickup!.NetName);
        Assert.Equal(40f, e.GetResource(ResourceType.Rockets), 3); // g_pickup_rockets default
    }

    [Fact]
    public void AmmoBfg_NonXdf_FireballHasNoAmmoType_EntityDeleted()
    {
        // QC: bfg -> FIREBALL (non-XDF); FIREBALL has no ammo_type, so GetAmmoItem(RES_NONE) is null and
        // SPAWNFUNC_BODY deletes the entity.
        Boot();
        var e = Spawn();
        e.Count = 10;
        Assert.True(SpawnFuncs.TrySpawn("ammo_bfg", e)); // a spawnfunc DID run...
        Assert.True(e.IsFreed);                          // ...but the entity was deleted (no ammo item)
    }

    // =====================================================================================
    //  Q3DF target_score (quake3.qc:190-203) — CTS-only accumulator
    // =====================================================================================

    [Fact]
    public void TargetScore_OutsideCts_DeletesItself()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => false; // QC: if(!g_cts) { delete(this); return; }
        var e = Spawn();
        Assert.True(SpawnFuncs.TrySpawn("target_score", e));
        Assert.True(e.IsFreed);
        Assert.Null(e.Use); // no .use wired (it was deleted before that)
    }

    [Fact]
    public void TargetScore_InsideCts_DefaultsCountAndAccumulates()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => true;

        // count unset -> defaults to 1 (QC: if(!this.count) this.count = 1).
        var e = Spawn();
        Assert.True(SpawnFuncs.TrySpawn("target_score", e));
        Assert.False(e.IsFreed);
        Assert.Equal(1, e.Count);
        Assert.NotNull(e.Use);

        var p = NewPlayer();
        e.Use!(e, p);
        Assert.Equal(1, p.FragsFilterCnt);
        e.Use!(e, p);
        Assert.Equal(2, p.FragsFilterCnt);

        // a non-player activator is ignored (QC: if(!IS_PLAYER(actor)) return).
        var notPlayer = Spawn();
        e.Use!(e, notPlayer);
        Assert.Equal(0, notPlayer.FragsFilterCnt);
    }

    [Fact]
    public void TargetScore_HonorsExplicitCount()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => true;
        var e = Spawn();
        e.Count = 5;
        Assert.True(SpawnFuncs.TrySpawn("target_score", e));
        Assert.Equal(5, e.Count);

        var p = NewPlayer();
        e.Use!(e, p);
        Assert.Equal(5, p.FragsFilterCnt);
    }

    // =====================================================================================
    //  Q3DF target_fragsFilter (quake3.qc:205-236) — CTS-only gate
    // =====================================================================================

    [Fact]
    public void TargetFragsFilter_OutsideCts_DeletesItself()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => false;
        var e = Spawn();
        Assert.True(SpawnFuncs.TrySpawn("target_fragsFilter", e));
        Assert.True(e.IsFreed);
    }

    [Fact]
    public void TargetFragsFilter_BelowThreshold_DoesNotFireTargets()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => true;
        var e = Spawn();
        e.Frags = 3f;                 // need 3 (the map's `frags` key, set directly here)
        e.Target = "downstream";
        Assert.True(SpawnFuncs.TrySpawn("target_fragsFilter", e));

        var flag = WireTarget("downstream");
        var p = NewPlayer();
        p.FragsFilterCnt = 2;         // below threshold
        e.Use!(e, p);
        Assert.False(flag.Fired, "below the threshold -> targets must NOT fire");
        Assert.Equal(2, p.FragsFilterCnt); // unchanged
    }

    [Fact]
    public void TargetFragsFilter_MetThreshold_FiresTargets_AndRemoverSubtracts()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => true;
        var e = Spawn();
        e.Frags = 3f;
        e.SpawnFlags = 1; // FRAGSFILTER_REMOVER
        e.Target = "downstream";
        Assert.True(SpawnFuncs.TrySpawn("target_fragsFilter", e));

        var flag = WireTarget("downstream");
        var p = NewPlayer();
        p.FragsFilterCnt = 5;        // >= 3
        e.Use!(e, p);
        Assert.True(flag.Fired, "at/above the threshold -> targets fire");
        Assert.Equal(2, p.FragsFilterCnt); // REMOVER: 5 - 3 = 2
    }

    [Fact]
    public void TargetFragsFilter_ResetFlag_ZeroesCounter()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => true;
        var e = Spawn();
        e.Frags = 3f;
        e.SpawnFlags = 8; // FRAGSFILTER_RESET (takes precedence over REMOVER)
        Assert.True(SpawnFuncs.TrySpawn("target_fragsFilter", e));

        var p = NewPlayer();
        p.FragsFilterCnt = 7;
        e.Use!(e, p);
        Assert.Equal(0, p.FragsFilterCnt); // RESET -> 0
    }

    [Fact]
    public void TargetFragsFilter_DefaultsThresholdToOne()
    {
        Boot();
        CompatRemaps.IsCtsActive = () => true;
        var e = Spawn();
        Assert.True(SpawnFuncs.TrySpawn("target_fragsFilter", e));
        Assert.Equal(1f, e.Frags, 3); // QC: if(!this.frags) this.frags = 1
    }

    // =====================================================================================
    //  Q3DF target_print (quake3.qc:238-295)
    // =====================================================================================

    [Fact]
    public void TargetPrint_WiresUse_AndIgnoresNonPlayers()
    {
        Boot();
        var e = Spawn();
        e.Message = "hello";
        Assert.True(SpawnFuncs.TrySpawn("target_print", e));
        Assert.NotNull(e.Use);

        // a non-player activator is ignored (QC: if(!IS_PLAYER(actor)) return) — no throw.
        var notPlayer = Spawn();
        e.Use!(e, notPlayer);

        // a player activator runs the centerprint path (the audible half) without error.
        var p = NewPlayer();
        e.Use!(e, p);
    }

    [Fact]
    public void TargetSmallprint_AliasesTargetPrint()
    {
        Boot();
        var e = Spawn();
        e.Message = "hi";
        Assert.True(SpawnFuncs.TrySpawn("target_smallprint", e)); // QC: spawnfunc_target_print(this)
        Assert.Equal("target_print", e.ClassName);
        Assert.NotNull(e.Use);
    }

    // =====================================================================================
    //  Q3DF target_init (quake3.qc:119-187) — the DeFRaG reset entity
    // =====================================================================================

    [Fact]
    public void TargetInit_NoFlags_ResetsHealthArmorAmmoWeapons()
    {
        var f = Boot();
        f.Cvars.Set("g_start_ammo_shells", "20");
        f.Cvars.Set("g_balance_health_start", "100");
        f.Cvars.Set("g_balance_armor_start", "0");

        var e = Spawn();
        e.SpawnFlags = 0; // reset everything
        Assert.True(SpawnFuncs.TrySpawn("target_init", e));
        Assert.NotNull(e.Use);

        var p = NewPlayer(health: 33f);
        p.SetResourceExplicit(ResourceType.Armor, 99f);
        p.SetResourceExplicit(ResourceType.Cells, 50f);

        e.Use!(e, p);

        // health/armor reset to the start loadout; cells (an extra ammo) reset to start (0 by default).
        Assert.Equal(100f, p.GetResource(ResourceType.Health), 3);
        Assert.Equal(0f, p.GetResource(ResourceType.Armor), 3);
        Assert.Equal(0f, p.GetResource(ResourceType.Cells), 3);
    }

    [Fact]
    public void TargetInit_KeepHealthFlag_LeavesHealthUntouched()
    {
        Boot();
        var e = Spawn();
        e.SpawnFlags = 2; // bit 1 — DON'T reset health
        Assert.True(SpawnFuncs.TrySpawn("target_init", e));

        var p = NewPlayer(health: 33f);
        e.Use!(e, p);
        Assert.Equal(33f, p.GetResource(ResourceType.Health), 3); // preserved
    }

    [Fact]
    public void TargetInit_MeleeOnlyFlag_ZeroesAmmoAndGivesShotgunOnly()
    {
        Boot();
        var e = Spawn();
        e.SpawnFlags = 32; // bit 5 — melee only (zero ammo, SHOTGUN only)
        Assert.True(SpawnFuncs.TrySpawn("target_init", e));

        var p = NewPlayer();
        p.SetResourceExplicit(ResourceType.Cells, 80f);
        p.OwnedWeapons.Add("vortex");
        p.OwnedWeaponSet.Add(Weapons.ByName("vortex")!);

        e.Use!(e, p);

        Assert.Equal(0f, p.GetResource(ResourceType.Cells), 3);
        Assert.True(p.OwnedWeaponSet.Has(Weapons.ByName("shotgun")!), "shotgun granted");
        Assert.False(p.OwnedWeaponSet.Has(Weapons.ByName("vortex")!), "other weapons stripped");
        Assert.Contains("shotgun", p.OwnedWeapons);
        Assert.DoesNotContain("vortex", p.OwnedWeapons);
    }

    [Fact]
    public void TargetInit_StripsPowerups_UnlessKeepFlag()
    {
        Boot();
        var strength = StatusEffectsCatalog.ByName("strength")!;

        // default flags -> powerups stripped.
        var e = Spawn();
        Assert.True(SpawnFuncs.TrySpawn("target_init", e));
        var p = NewPlayer();
        StatusEffectsCatalog.Apply(p, strength, 30f);
        Assert.True(StatusEffectsCatalog.Has(p, strength));
        e.Use!(e, p);
        Assert.False(StatusEffectsCatalog.Has(p, strength), "powerup stripped");

        // bit 3 (8) set -> powerups preserved.
        var e2 = Spawn();
        e2.SpawnFlags = 8;
        Assert.True(SpawnFuncs.TrySpawn("target_init", e2));
        var p2 = NewPlayer();
        StatusEffectsCatalog.Apply(p2, strength, 30f);
        e2.Use!(e2, p2);
        Assert.True(StatusEffectsCatalog.Has(p2, strength), "powerup preserved with the keep flag");
    }

    /// <summary>A mutable flag a downstream target's <c>.use</c> flips, so a SUB_UseTargets fire is observable.</summary>
    private sealed class FireFlag { public bool Fired; }

    // Wire a downstream target (by targetname) whose .use sets the returned flag, registered in the targetname
    // index so MapMover.UseTargets finds it.
    private static FireFlag WireTarget(string targetName)
    {
        var flag = new FireFlag();
        var t = Spawn();
        t.TargetName = targetName;
        t.Use = (_, _) => flag.Fired = true;
        MapMover.IndexRegister(t);
        return flag;
    }
}

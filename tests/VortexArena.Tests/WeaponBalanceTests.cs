using System;
using System.IO;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Serializes test classes that mutate the process-global registry (<see cref="GameRegistries"/>) or ambient
/// <see cref="Api.Services"/>, so they don't race xUnit's per-class parallelism (e.g. another class calling
/// <c>GameRegistries.Reset()</c> mid-assertion).
/// </summary>
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public class GlobalStateCollection { }

/// <summary>
/// Tests that weapon balance is actually wired: <see cref="Weapon.Configure"/> reads the <c>g_balance_*</c>
/// cvars (seeded by the config interpreter) with the stock value as a fallback, and runs at registration so
/// weapons aren't left with a zero-default balance block.
/// </summary>
[Collection("GlobalState")]
public class WeaponBalanceTests
{
    [Fact]
    public void Configure_Uses_Stock_Fallback_When_Cvars_Unset()
    {
        Api.Services = new EngineServices(new CollisionWorld()); // empty cvar store
        var blaster = new Blaster();
        blaster.Configure();
        // Stock bal-wep-xonotic.cfg values, kept as the fallback so a bare server still plays Xonotic balance.
        Assert.Equal(20f, blaster.Primary.Damage);
        Assert.Equal(6000f, blaster.Primary.Speed);
        Assert.Equal(0.7f, blaster.Primary.Refire);
    }

    [Fact]
    public void Configure_Reads_Loaded_Balance_Cvar_Override()
    {
        var facade = new EngineServices(new CollisionWorld());
        Api.Services = facade;
        // Simulate an alternate balance config (e.g. an overkill/XPM set) having loaded this cvar.
        facade.Cvars.Set("g_balance_blaster_primary_damage", "99");

        var blaster = new Blaster();
        blaster.Configure();
        Assert.Equal(99f, blaster.Primary.Damage);       // override honored
        Assert.Equal(6000f, blaster.Primary.Speed);      // untouched fields keep the stock fallback
    }

    [Fact]
    public void Bootstrap_Runs_Configure_So_Weapons_Are_Not_Zero_Balance()
    {
        // The registry bootstrap must call Configure() — otherwise the balance struct stays at its zero
        // default and weapons fire with no damage. (Regression guard for that latent bug.)
        GameRegistries.Bootstrap();
        var blaster = Weapons.ByName("blaster") as Blaster;
        Assert.NotNull(blaster);
        Assert.True(blaster!.Primary.Damage > 0f,
            "Configure() must run at registration so the blaster has real (non-zero) damage");
        Assert.True(blaster.Primary.Speed > 0f, "blaster projectile speed must be non-zero");
    }

    [Fact]
    public void Vortex_Secondary_Is_Zoom_With_Stock_Balance()
    {
        // QC vortex.qc wr_zoomdir/wr_zoom + vortex.qh w_reticle: with g_balance_vortex_secondary 0 (the stock
        // default — secondary is NOT a separate fire mode) holding ATTACK2 zooms, and the scope is gfx/reticle_nex.
        Api.Services = new EngineServices(new CollisionWorld()); // empty store → stock fallbacks
        var vortex = new Vortex();
        vortex.Configure();
        Assert.False(vortex.Cvars.Secondary, "stock g_balance_vortex_secondary is 0");
        Assert.True(vortex.ZoomOnSecondary, "ATTACK2 must zoom while secondary is not a fire mode");
        Assert.Equal("gfx/reticle_nex", vortex.Reticle);
    }

    [Fact]
    public void Vortex_Secondary_Disables_Zoom_When_It_Is_A_Fire_Mode()
    {
        // g_balance_vortex_secondary 1 turns ATTACK2 into a real (weaker) fire mode, so the zoom is off
        // (wr_zoomdir → false), exactly as in base.
        var facade = new EngineServices(new CollisionWorld());
        Api.Services = facade;
        facade.Cvars.Set("g_balance_vortex_secondary", "1");

        var vortex = new Vortex();
        vortex.Configure();
        Assert.True(vortex.Cvars.Secondary);
        Assert.False(vortex.ZoomOnSecondary, "with secondary as a fire mode, ATTACK2 must NOT zoom");
    }

    [Fact]
    public void Default_Weapon_Has_No_Zoom_Or_Reticle()
    {
        // The base Weapon contract: ordinary weapons don't zoom on secondary and have no scope (QC: only the
        // Vortex/Vaporizer define wr_zoom/w_reticle). Guards against a stray override leaking the scope onto every gun.
        Api.Services = new EngineServices(new CollisionWorld());
        var blaster = new Blaster();
        blaster.Configure();
        Assert.False(blaster.ZoomOnSecondary);
        Assert.Null(blaster.Reticle);
    }

    [Fact]
    public void Real_Balance_Config_Flows_Into_Weapon_Configure()
    {
        const string pk3 = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";
        if (!File.Exists(Path.Combine(pk3, "bal-wep-xonotic.cfg"))) return; // CI-portable: no checkout, no-op

        var facade = new EngineServices(new CollisionWorld());
        Api.Services = facade;
        // balance-xonotic.cfg execs bal-wep-xonotic.cfg — one entry loads the whole default weapon balance.
        ConfigLoader.Load(facade.Cvars,
            p => { string f = Path.Combine(pk3, p); return File.Exists(f) ? File.ReadAllText(f) : null; },
            "balance-xonotic.cfg");

        // The strongest proof: a field whose stock config value DIFFERS from the port's hardcoded fallback.
        // The machinegun's spread_decay fallback is 0 (Nexuiz-style counter spread); stock Xonotic is 0.048.
        var mg = new Machinegun();
        mg.Configure();
        Assert.True(Math.Abs(mg.Cvars.SpreadDecay - 0.048f) < 1e-4f,
            $"loaded config should set machinegun spread_decay to 0.048, got {mg.Cvars.SpreadDecay}");

        // And a few authentic values across weapons (these match the fallback too, but now come from the cfg).
        var vortex = new Vortex();
        vortex.Configure();
        Assert.Equal(80f, vortex.Cvars.Damage);

        var mortar = new Mortar();
        mortar.Configure();
        Assert.Equal(55f, mortar.Primary.Damage);
    }
}

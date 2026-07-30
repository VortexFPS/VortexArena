using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// End-to-end splash-damage regression guard ("damage/force felt ~double", 2026-06): a live GameWorld where
/// one player fires a single Blaster bolt / Devastator rocket at a stationary victim, asserting the victim
/// takes ONE application of the blast — the Xonotic-balance damage and knockback, not a multiple.
///
/// The original bug: players (negative-index client edicts) were returned MULTIPLE times by FindInRadius —
/// once per overlapped 256-unit area-grid cell (the grid's dedup tag array skipped negative indices) PLUS
/// once more from ServerEntityService's player merge — so RadiusDamage applied every blast 2-5x. A direct
/// rocket dealt ~237 damage instead of ~80 and the calcpush knockback chained 750→1200→1470 u/s.
///
/// The victim deliberately stands at Y=0 (its 32-unit-wide hull straddles a cell line, linking it into two
/// cells) — the exact geometry that multiplied the blast before the fix.
/// </summary>
[Collection("GlobalState")]
public class SplashDamageSingleApplicationTests
{
    private readonly ITestOutputHelper _out;
    public SplashDamageSingleApplicationTests(ITestOutputHelper output)
    {
        _out = output;
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
    }

    private sealed class ProbeInput : IMovementInput
    {
        public Vector3 ViewAngles { get; set; }
        public Vector3 MoveValues { get; set; }
        public float FrameTime { get; set; } = SimulationLoop.TicRate;
        public bool ButtonJump => false;
        public bool ButtonCrouch => false;
        public bool ButtonUse => false;
        public bool ButtonAttack1 { get; set; }
        public bool ButtonAttack2 => false;
    }

    private static CollisionWorld FlatFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    private static List<EntityDict> SpawnDicts(params Vector3[] spots)
    {
        var dicts = new List<EntityDict> { new("worldspawn") };
        foreach (Vector3 s in spots)
            dicts.Add(new EntityDict("info_player_deathmatch", s));
        return dicts;
    }

    /// <summary>
    /// Expected single-application numbers (code-fallback balance, no cfg tree):
    ///   blaster: core 20 dmg; knockback = 375 force x 2 g_player_damageforcescale = 750 u/s.
    ///   devastator: core ~80 dmg; knockback ~790 u/s. One rocket must NOT kill a 200hp victim.
    /// Upper bounds sit far below the 2x-application level (the first doubled push lands at ~1200 u/s).
    /// </summary>
    [Theory]
    [InlineData("blaster", 18f, 25f, 700f, 800f)]
    [InlineData("devastator", 70f, 90f, 700f, 900f)]
    public void OneBlast_AppliesDamageAndKnockbackExactlyOnce(
        string weaponName, float dmgMin, float dmgMax, float pushMin, float pushMax)
    {
        var world = new GameWorld(FlatFloor(), SpawnDicts(
            new Vector3(-1000f, 0f, 32f), new Vector3(1000f, 0f, 32f)));
        world.Boot("dm");
        world.Services.Cvars.Set("sv_spectate", "0");

        ClientManager.ClientInfo atkInfo = world.Clients.ClientConnect(isBot: false, netName: "atk");
        ClientManager.ClientInfo vicInfo = world.Clients.ClientConnect(isBot: false, netName: "vic");
        Player atk = atkInfo.Player;
        Player vic = vicInfo.Player;
        world.Clients.Join(atk);
        world.Clients.Join(vic);

        var atkInput = new ProbeInput();
        var vicInput = new ProbeInput();
        world.InputProvider = p => ReferenceEquals(p, atk) ? atkInput : (IMovementInput)vicInput;

        // settle: spawn, land, spawn shield elapse
        for (int t = 0; t < 72 * 3; t++) world.Frame(SimulationLoop.TicRate);

        // face-off along +X; victim at Y=0 so its hull straddles a grid cell line (the bug's geometry)
        world.Services.Entities.SetOrigin(atk, new Vector3(0f, 0f, 25f));
        world.Services.Entities.SetOrigin(vic, new Vector3(400f, 0f, 25f));
        atk.Velocity = Vector3.Zero;
        vic.Velocity = Vector3.Zero;
        atk.SpawnShieldExpire = 0f;
        vic.SpawnShieldExpire = 0f;
        atk.UnlimitedAmmo = true;

        Weapon? wep = Weapons.ByName(weaponName);
        Assert.NotNull(wep);
        Inventory.GiveWeapon(atk, wep!);
        Inventory.SwitchWeapon(atk, wep!);

        vic.SetResourceExplicit(ResourceType.Health, 200f);
        vic.SetResourceExplicit(ResourceType.Armor, 0f);

        // let the weapon raise to READY
        for (int t = 0; t < 36; t++) world.Frame(SimulationLoop.TicRate);

        float hp0 = vic.GetResource(ResourceType.Health);
        float ar0 = vic.GetResource(ResourceType.Armor);
        float peakSpeed = 0f;
        float maxOneTickDrop = 0f;
        float prevHp = hp0;

        // hold +attack well under the refire window (one shot), then run 2 s total so the projectile lands
        int holdTicks = (int)(0.3f * 72f);
        for (int t = 0; t < 72 * 2; t++)
        {
            atkInput.ButtonAttack1 = t < holdTicks;
            world.Frame(SimulationLoop.TicRate);
            peakSpeed = MathF.Max(peakSpeed, vic.Velocity.Length());
            float hpNow = vic.GetResource(ResourceType.Health);
            maxOneTickDrop = MathF.Max(maxOneTickDrop, prevHp - hpNow);
            prevHp = hpNow;
        }

        float dmg = (hp0 - vic.GetResource(ResourceType.Health)) + (ar0 - vic.GetResource(ResourceType.Armor));
        _out.WriteLine($"weapon={weaponName}: dmg={dmg:0.##} oneTickDrop={maxOneTickDrop:0.##} peak={peakSpeed:0.#} u/s");

        // The blast must land exactly once: damage and knockback in the single-application band.
        // (dmg uses the total over the window; health rot adds < ~2hp of drift, covered by the band.)
        Assert.InRange(maxOneTickDrop, dmgMin, dmgMax);
        Assert.InRange(peakSpeed, pushMin, pushMax);
        Assert.True(vic.GetResource(ResourceType.Health) > 0f, "a single blast must not kill a 200hp victim");
    }

    /// <summary>
    /// The blaster JUMP itself: fire straight down at the floor and measure the SELF-knockback. The shooter is
    /// NOT the blast's direct-hit entity (the floor is), so this exercises the through-floor LOS path — QC
    /// blends force by the visible fraction of the player's box (damage.qc:838-905), so an exposed player on
    /// open ground gets ~full force every time. The old single-ray binary check could flip a jump to
    /// 0.7-0.75x force when its one ray clipped the floor, making jump heights inconsistent. Self-damage must
    /// be ~20 x 0.65 selfdamagepercent = 13, applied once.
    /// </summary>
    [Fact]
    public void SelfBlasterJump_GetsFullKnockback_OnOpenGround()
    {
        var world = new GameWorld(FlatFloor(), SpawnDicts(
            new Vector3(-1000f, 0f, 32f), new Vector3(1000f, 0f, 32f)));
        world.Boot("dm");
        world.Services.Cvars.Set("sv_spectate", "0");

        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: "jumper");
        Player p = info.Player;
        world.Clients.Join(p);

        var input = new ProbeInput { ViewAngles = new Vector3(90f, 0f, 0f) }; // look straight down
        world.InputProvider = _ => input;

        for (int t = 0; t < 72 * 3; t++) world.Frame(SimulationLoop.TicRate); // settle + shield elapse
        world.Services.Entities.SetOrigin(p, new Vector3(0f, 0f, 25f));
        p.Velocity = Vector3.Zero;
        p.SpawnShieldExpire = 0f;
        p.UnlimitedAmmo = true;

        Weapon? blaster = Weapons.ByName("blaster");
        Assert.NotNull(blaster);
        Inventory.GiveWeapon(p, blaster!);
        Inventory.SwitchWeapon(p, blaster!);
        p.SetResourceExplicit(ResourceType.Health, 100f);
        p.SetResourceExplicit(ResourceType.Armor, 0f);
        for (int t = 0; t < 36; t++) world.Frame(SimulationLoop.TicRate); // raise to READY

        float hp0 = p.GetResource(ResourceType.Health);
        float peakUp = 0f;
        int holdTicks = (int)(0.2f * 72f);
        for (int t = 0; t < 72; t++)
        {
            input.ButtonAttack1 = t < holdTicks;
            world.Frame(SimulationLoop.TicRate);
            peakUp = MathF.Max(peakUp, p.Velocity.Z);
        }
        float selfDmg = hp0 - p.GetResource(ResourceType.Health);
        _out.WriteLine($"self blaster jump: peak vz={peakUp:0.#} u/s, self damage={selfDmg:0.##}");

        // Full-force band: |force| = 375 x 2 playerscale = 750, mostly vertical (blast under the eye). The
        // floor is 600+: a 0.7-0.75x through-floor misfire would land ~500-560, and a duplicated application
        // would exceed 800. Self-damage: 20 x 0.65 = 13 (one application).
        Assert.InRange(peakUp, 600f, 800f);
        Assert.InRange(selfDmg, 11f, 16f);
    }
}

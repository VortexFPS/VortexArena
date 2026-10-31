using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Playtest #84 ("shotgun secondary doesn't work"): the melee slap end to end on the server — the press plays
/// SND_SHOTGUN_MELEE, commits WFRAME_FIRE2 (the viewmodel's 23-frame swing clip), and after melee_delay the
/// swing traces damage a player standing inside melee_range in front of the attacker (shotgun.qc
/// W_Shotgun_Attack2 / W_Shotgun_Melee_Think, g_balance_shotgun_secondary 1).
/// </summary>
[Collection("GlobalState")]
public class ShotgunMeleeTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    private static (EngineServices f, SimulationLoop sim, List<string> sounds) Boot()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-2048f, -2048f, -64f), new Vector3(2048f, 2048f, -24f), SuperContents.Solid));
        world.BuildGrid();
        var f = new EngineServices(world);
        Api.Services = f;
        VehicleCommon.GameStopped = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();
        var sounds = new List<string>();
        f.SoundImpl.Broadcast += e => { if (!e.Stop) sounds.Add(e.Sample); };
        var sim = new SimulationLoop(f, world);
        return (f, sim, sounds);
    }

    private static Entity NewPlayer(EngineServices f, Vector3 origin)
    {
        Entity e = f.Entities.Spawn();
        e.ClassName = "player";
        e.Flags = EntFlags.Client | EntFlags.OnGround;
        e.Solid = Solid.SlideBox;
        e.Angles = Vector3.Zero; // facing +X
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.ViewOfs = new Vector3(0, 0, 35);
        e.Health = 100; e.MaxHealth = 100;
        e.TakeDamage = DamageMode.Aim;
        f.Entities.SetOrigin(e, origin);
        return e;
    }

    [Fact]
    public void Melee_Plays_Its_Sound_Commits_Fire2_And_Damages_A_Player_In_Range()
    {
        var (f, sim, sounds) = Boot();
        var shotgun = (Shotgun)Weapons.ByName("shotgun")!;
        Assert.Equal(1, shotgun.Secondary.Secondary); // the stock balance: secondary = melee

        Entity attacker = NewPlayer(f, Vector3.Zero);
        Entity victim = NewPlayer(f, new Vector3(80f, 0f, 0f)); // inside melee_range 120
        var slot = new WeaponSlot(0);
        attacker.ActiveWeaponId = shotgun.RegistryId;
        WeaponSlotState st = attacker.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.ButtonAttack2 = true;

        shotgun.WrThink(attacker, slot, FireMode.Secondary);

        Assert.Contains("weapons/shotgun_melee.wav", sounds);
        Assert.Equal(Weapon.WFrameFire2, st.FireAnimFrame);
        Assert.Equal(100f, victim.Health); // nothing yet: the swing starts after melee_delay (0.25 s)

        // Run the meleetemp think through melee_delay + melee_time.
        for (int i = 0; i < 64 && victim.Health >= 100f; i++)
            sim.Tick();

        Assert.True(victim.Health < 100f, $"the slap should have hurt the player in front (health {victim.Health})");
        Assert.Equal(100f, attacker.Health);
    }

    [Fact]
    public void Melee_Misses_A_Player_Beyond_Range()
    {
        var (f, sim, _) = Boot();
        var shotgun = (Shotgun)Weapons.ByName("shotgun")!;
        Entity attacker = NewPlayer(f, Vector3.Zero);
        Entity victim = NewPlayer(f, new Vector3(300f, 0f, 0f));
        var slot = new WeaponSlot(0);
        attacker.ActiveWeaponId = shotgun.RegistryId;
        WeaponSlotState st = attacker.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.ButtonAttack2 = true;

        shotgun.WrThink(attacker, slot, FireMode.Secondary);
        for (int i = 0; i < 64; i++)
            sim.Tick();

        Assert.Equal(100f, victim.Health);
    }

    [Fact]
    public void Primary_Commits_Fire1()
    {
        var (f, _, _) = Boot();
        var shotgun = (Shotgun)Weapons.ByName("shotgun")!;
        Entity attacker = NewPlayer(f, Vector3.Zero);
        attacker.SetResourceExplicit(ResourceType.Shells, 50f);
        var slot = new WeaponSlot(0);
        attacker.ActiveWeaponId = shotgun.RegistryId;
        WeaponSlotState st = attacker.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.ButtonAttack = true;

        shotgun.WrThink(attacker, slot, FireMode.Primary);

        Assert.Equal(Weapon.WFrameFire1, st.FireAnimFrame);
    }
}

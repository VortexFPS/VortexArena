using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// The Vortex's knockback, and the charge that scales it ("the vortex shot isn't doing any force", 2026-08).
///
/// <para>Force flows <c>g_balance_vortex_primary_force</c> (200) → <see cref="Vortex"/>'s charge scaling →
/// <c>FireRailgunBullet</c> → <c>DamageSystem.ApplyKnockback</c>, where QC's
/// <c>damage_explosion_calcpush(damageforcescale·force, victim velocity, speedfactor)</c> lands it. Against a
/// stationary victim that is <c>200 × g_player_damageforcescale 2 = 400 u/s</c> exactly — the number Base
/// produces, and the anchor the first test pins.</para>
///
/// <para>The bug this guards: <c>Vortex.WrSetup</c> re-seeded <c>vortex_charge = charge_start</c> (0.5) on
/// every switch-in. Base's <c>wr_setup</c> (vortex.qc:277-280) only clears <c>vortex_lasthit</c>; the charge is
/// seeded in <c>wr_resetplayer</c> (vortex.qc:299-311), which runs on RESPAWN. So in Base a weapon swap
/// preserves your charge, while the port knocked it down to a 0.75 charge factor — 60 dmg / 150 force / 300 u/s
/// instead of 80 / 200 / 400, recovering only after <c>(1 - 0.5) / charge_rate 0.6 = 0.83 s</c>.</para>
///
/// <para>Moving the seed to <c>WrResetPlayer</c> only works because the respawn dispatch now runs
/// <c>wr_resetplayer</c> for EVERY registered weapon (QC server/client.qc:802) rather than the held one — you
/// respawn holding the Blaster, so a held-only dispatch would never seed the Vortex at all. The last test pins
/// that, since it is the half of the fix with no local symptom.</para>
/// </summary>
[Collection("GlobalState")]
public class VortexChargeAndKnockbackTests
{
    private readonly ITestOutputHelper _out;
    public VortexChargeAndKnockbackTests(ITestOutputHelper output)
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
        public bool ButtonJump { get; set; }
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

    /// <summary>A shooter facing a stationary victim 400 units down +X, both settled and shield-free.</summary>
    private sealed class Duel
    {
        public GameWorld World = null!;
        public Player Attacker = null!;
        public Player Victim = null!;
        public ProbeInput AttackerInput = null!;

        public static Duel Setup()
        {
            var d = new Duel();
            d.World = new GameWorld(FlatFloor(), SpawnDicts(
                new Vector3(-1000f, 0f, 32f), new Vector3(1000f, 0f, 32f)));
            d.World.Boot("dm");
            d.World.Services.Cvars.Set("sv_spectate", "0");

            d.Attacker = d.World.Clients.ClientConnect(isBot: false, netName: "atk").Player;
            d.Victim = d.World.Clients.ClientConnect(isBot: false, netName: "vic").Player;
            d.World.Clients.Join(d.Attacker);
            d.World.Clients.Join(d.Victim);

            d.AttackerInput = new ProbeInput();
            var vicInput = new ProbeInput();
            d.World.InputProvider = p => ReferenceEquals(p, d.Attacker) ? d.AttackerInput : (IMovementInput)vicInput;

            for (int t = 0; t < 72 * 3; t++) d.World.Frame(SimulationLoop.TicRate); // spawn, land, shield elapse

            d.World.Services.Entities.SetOrigin(d.Attacker, new Vector3(0f, 0f, 25f));
            d.World.Services.Entities.SetOrigin(d.Victim, new Vector3(400f, 0f, 25f));
            d.Attacker.Velocity = Vector3.Zero;
            d.Victim.Velocity = Vector3.Zero;
            d.Attacker.SpawnShieldExpire = 0f;
            d.Victim.SpawnShieldExpire = 0f;
            d.Attacker.UnlimitedAmmo = true;
            d.Attacker.SetResourceExplicit(ResourceType.Cells, 999f);
            d.Victim.SetResourceExplicit(ResourceType.Health, 500f);
            d.Victim.SetResourceExplicit(ResourceType.Armor, 0f);
            return d;
        }

        public void Run(int ticks)
        {
            for (int t = 0; t < ticks; t++) World.Frame(SimulationLoop.TicRate);
        }

        /// <summary>
        /// Drive the attacker back to life the way a real client does. The DEAD_* machine (GameWorld.cs:3880)
        /// walks on button EDGES in both directions — released to reach DEAD_DEAD, pressed for RESPAWNABLE,
        /// released again for RESPAWNING — so pulse jump rather than holding it. Returns false if it never
        /// came back.
        /// </summary>
        public bool RunUntilRespawned(int maxTicks)
        {
            for (int t = 0; t < maxTicks; t++)
            {
                AttackerInput.ButtonJump = (t / 4) % 2 == 1; // ~18 Hz press/release pulse
                World.Frame(SimulationLoop.TicRate);
                if (Attacker.DeadState == DeadFlag.No)
                {
                    AttackerInput.ButtonJump = false;
                    return true;
                }
            }
            AttackerInput.ButtonJump = false;
            return false;
        }

        /// <summary>Tap primary once (the 1.5 s refire makes a double-shot impossible) and report the victim's
        /// single-tick health drop and peak speed.</summary>
        public (float damage, float push) FireOnce()
        {
            Victim.Velocity = Vector3.Zero;
            float prevHp = Victim.GetResource(ResourceType.Health);
            float damage = 0f, push = 0f;
            for (int t = 0; t < 72; t++)
            {
                AttackerInput.ButtonAttack1 = t < 8;
                World.Frame(SimulationLoop.TicRate);
                push = MathF.Max(push, Victim.Velocity.Length());
                float hp = Victim.GetResource(ResourceType.Health);
                damage = MathF.Max(damage, prevHp - hp);
                prevHp = hp;
            }
            AttackerInput.ButtonAttack1 = false;
            return (damage, push);
        }
    }

    /// <summary>
    /// The anchor: a fully charged Vortex hit pushes a stationary victim at Base's exact number. force 200 ×
    /// damageforcescale 2 = 400 u/s (calcpush's multiplier is 1 against a still target), for 80 damage.
    /// A regression that silently drops force to zero — or halves it via the charge — fails here.
    /// </summary>
    [Fact]
    public void FullyChargedVortex_Pushes400UnitsPerSecond()
    {
        Duel d = Duel.Setup();
        Weapon vortex = Weapons.ByName("vortex")!;
        Inventory.GiveWeapon(d.Attacker, vortex);
        Inventory.SwitchWeapon(d.Attacker, vortex);
        d.Run(144); // raise, then charge_start 0.5 -> charge_limit 1.0 at charge_rate 0.6/s

        (float damage, float push) = d.FireOnce();
        _out.WriteLine($"full charge: damage={damage:0.##} push={push:0.#} u/s");

        Assert.InRange(damage, 79f, 81f); // g_balance_vortex_primary_damage 80 at charge factor 1.0
        Assert.InRange(push, 395f, 410f); // 200 force x 2 g_player_damageforcescale
    }

    /// <summary>
    /// The fix: switching away from the Vortex and back must NOT re-seed the charge. Base only touches
    /// vortex_charge in wr_resetplayer, so a fully charged Vortex is still fully charged after a swap. Before
    /// the fix this landed 72 dmg / 361 u/s (the charge had re-seeded to 0.5 and partially regenerated during
    /// the raise); a fast swap-and-snap bottomed out at 60 / 300.
    /// </summary>
    [Fact]
    public void SwitchingAwayAndBack_PreservesVortexCharge()
    {
        Duel d = Duel.Setup();
        Weapon vortex = Weapons.ByName("vortex")!;
        Weapon blaster = Weapons.ByName("blaster")!;
        Inventory.GiveWeapon(d.Attacker, blaster);
        Inventory.GiveWeapon(d.Attacker, vortex);
        Inventory.SwitchWeapon(d.Attacker, vortex);
        d.Run(144); // charge to the limit

        Inventory.SwitchWeapon(d.Attacker, blaster);
        d.Run(36);
        Inventory.SwitchWeapon(d.Attacker, vortex);
        d.Run(36); // raise back to READY

        (float damage, float push) = d.FireOnce();
        _out.WriteLine($"after a swap out and back: damage={damage:0.##} push={push:0.#} u/s");

        // Identical to the never-switched shot: the swap costs nothing.
        Assert.InRange(damage, 79f, 81f);
        Assert.InRange(push, 395f, 410f);
    }

    /// <summary>
    /// Respawn DOES re-seed the charge (QC wr_resetplayer), and it reaches the Vortex even though the player
    /// respawns holding something else — which is what the every-weapon respawn dispatch buys. A fresh life's
    /// first Vortex shot therefore starts from charge_start 0.5, i.e. a charge factor of
    /// <c>mindmg/dmg + (1 - mindmg/dmg)·0.5 = 0.75</c>: 60 damage and 150 force (300 u/s).
    ///
    /// <para>Without the dispatch fix the seed never runs for an unheld Vortex, so this shot would inherit
    /// whatever charge the PREVIOUS life ended on — a full-charge 80/400 here.</para>
    /// </summary>
    [Fact]
    public void Respawn_ReseedsVortexCharge_EvenWhenHoldingAnotherWeapon()
    {
        Duel d = Duel.Setup();
        Weapon vortex = Weapons.ByName("vortex")!;
        Inventory.GiveWeapon(d.Attacker, vortex);
        Inventory.SwitchWeapon(d.Attacker, vortex);
        d.Run(144); // build the charge to the limit during this life

        // Kill the attacker and let them respawn (they come back holding the spawn loadout, not the Vortex).
        d.Attacker.SetResourceExplicit(ResourceType.Health, 1f);
        Combat.Damage(d.Attacker, d.Attacker, d.Attacker, 500f, DeathTypes.Generic, d.Attacker.Origin, Vector3.Zero);
        Assert.True(d.RunUntilRespawned(72 * 15), "attacker never respawned");

        // Re-establish the duel geometry for the new life, then take the Vortex back out.
        d.World.Services.Entities.SetOrigin(d.Attacker, new Vector3(0f, 0f, 25f));
        d.World.Services.Entities.SetOrigin(d.Victim, new Vector3(400f, 0f, 25f));
        d.Attacker.Velocity = Vector3.Zero;
        d.Attacker.SpawnShieldExpire = 0f;
        d.Victim.SpawnShieldExpire = 0f;
        d.Attacker.UnlimitedAmmo = true;
        d.Attacker.SetResourceExplicit(ResourceType.Cells, 999f);
        d.Victim.SetResourceExplicit(ResourceType.Health, 500f);

        Inventory.GiveWeapon(d.Attacker, vortex);
        Inventory.SwitchWeapon(d.Attacker, vortex);
        d.Run(36); // raise only — do NOT give the charge time to climb back

        (float damage, float push) = d.FireOnce();
        _out.WriteLine($"first shot of a new life: damage={damage:0.##} push={push:0.#} u/s");

        // charge_start 0.5 seeded by the respawn, plus the 0.5 s raise regenerating 0.6*0.5 = 0.3 toward the
        // limit: charge 0.8 -> factor 0.9 -> 72 dmg / 180 force. The band excludes BOTH a stale full charge
        // (80 / 400, the dispatch never ran) and an unseeded zero charge (40 / 200).
        Assert.InRange(damage, 65f, 76f);
        Assert.InRange(push, 320f, 380f);
    }
}

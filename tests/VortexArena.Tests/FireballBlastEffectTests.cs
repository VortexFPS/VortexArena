using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Net;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public class FireballBlastEffectTests
{
    [Theory]
    [InlineData("hit", 700f, true)]
    [InlineData("use", 1000f, true)]
    [InlineData("timeout", 1000f, false)]
    [InlineData("shot down", 1000f, false)]
    [InlineData("self damage", 1000f, false)]
    [InlineData("hit", 0f, false)]
    public void PrimaryDetonationEmitsSphereOnlyWhenBfgSweepIsEligible(string trigger, float radius, bool emits)
    {
        var world = new CollisionWorld();
        var services = new EngineServices(world);
        var simulation = new SimulationLoop(services, world);
        var recorder = new EffectEmitter.RecordingSink();
        var oldSink = EffectEmitter.Sink;
        var oldServices = Api.Services;
        var oldCombat = Combat.System;
        try
        {
            Api.Services = services;
            Combat.System = new DamageSystem();
            EffectEmitter.Sink = recorder;
            services.Cvars.Set("g_balance_fireball_primary_animtime", "0");
            services.Cvars.Set("g_balance_fireball_primary_health", "10");
            services.Cvars.Set("g_projectiles_damage", "2");
            services.Cvars.Set("g_balance_fireball_primary_lifetime", "0.01");
            services.Cvars.Set("g_balance_fireball_primary_bfgradius",
                radius.ToString(System.Globalization.CultureInfo.InvariantCulture));
            GameRegistries.Reset();
            GameRegistries.Bootstrap();

            var weapon = Assert.IsType<Fireball>(Weapons.ByName("fireball"));
            var actor = services.Entities.Spawn();
            actor.ClassName = "player";
            actor.Flags = EntFlags.Client;
            actor.Health = actor.MaxHealth = 100f;
            actor.TakeDamage = DamageMode.Aim;
            actor.ActiveWeaponId = weapon.RegistryId;
            var slot = actor.WeaponState(new WeaponSlot(0));
            slot.State = WeaponFireState.Ready;
            slot.RlRelease = true;

            var fire = new MovementInput { ButtonAttack1 = true };
            simulation.Tick();
            simulation.Tick(); // The clock published by the first tick is zero, which means no pending think.
            WeaponFireDriver.Frame(actor, fire);
            for (int i = 0; i < 4; i++)
                WeaponFireDriver.Frame(actor, null);
            var projectile = Assert.Single(services.Entities.FindByClass("plasma_prim"));
            var origin = trigger == "self damage" ? actor.Origin : new Vector3(5250f, -13f, 88f);
            services.Entities.SetOrigin(projectile, origin);

            if (trigger == "timeout")
                simulation.Tick();
            else if (trigger == "shot down")
            {
                projectile.GtEventDamage!(projectile, actor, actor, "shotgun", 20f, origin, Vector3.Zero);
                Assert.True(projectile.Health <= 0f);
                simulation.Tick();
            }
            else if (trigger == "use")
                projectile.Use!(projectile, new Entity());
            else
                projectile.Touch!(projectile, new Entity());

            Assert.True(projectile.IsFreed);
            if (emits)
                Assert.Equal(new FireballBlastEffect(origin, radius), Assert.Single(recorder.FireballBlasts));
            else
                Assert.Empty(recorder.FireballBlasts);
        }
        finally
        {
            EffectEmitter.Sink = oldSink;
            Api.Services = oldServices;
            Combat.System = oldCombat;
        }
    }

    [Fact]
    public void StockSphereGrowsAcrossHoldAndFadeAndExpiresAtVisibilityThreshold()
    {
        var blast = new FireballBlastEffect(Vector3.Zero, 1000f);
        // Stock model has 64 Quake units of maximum absolute axis extent.
        Assert.Equal(15.625f, blast.FinalScale(64f));
        Assert.Equal(700f, new FireballBlastEffect(Vector3.Zero, 700f).FinalScale(64f) * 64f);

        Assert.True(FireballBlastEffect.Sample(0f, out float scale, out float alpha));
        Assert.Equal(0f, scale);
        Assert.Equal(0.2f, alpha);

        Assert.True(FireballBlastEffect.Sample(0.05f, out scale, out alpha));
        Assert.Equal(1f / 6f, scale, 5);
        Assert.Equal(0.2f, alpha);

        Assert.True(FireballBlastEffect.Sample(0.175f, out scale, out alpha));
        Assert.Equal(7f / 12f, scale, 5);
        Assert.Equal(0.1f, alpha, 5);
        Assert.True(FireballBlastEffect.Sample(0.296f, out _, out _));
        Assert.False(FireballBlastEffect.Sample(0.297f, out _, out _));
    }

    [Fact]
    public void WirePreservesLargeOriginsAndRejectsIncompleteOrInvalidEvents()
    {
        var blast = new FireballBlastEffect(new Vector3(5250.5f, -6400.25f, 88.125f), 700.25f);
        var writer = new BitWriter();
        FireballBlastWire.Write(writer, blast);
        var reader = new BitReader(writer.WrittenSpan);
        Assert.Equal(blast, FireballBlastWire.Read(ref reader));

        reader = new BitReader(writer.WrittenSpan[..^1]);
        Assert.Null(FireballBlastWire.Read(ref reader));

        writer.Reset();
        FireballBlastWire.Write(writer, blast with { Radius = float.NaN });
        reader = new BitReader(writer.WrittenSpan);
        Assert.Null(FireballBlastWire.Read(ref reader));
    }
}

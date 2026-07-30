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
/// Runtime tests for the Phase-2 gameplay systems: deterministic player movement (the fidelity
/// contract, ADR-0010) and the damage pipeline (armor/health split + the death hook).
/// </summary>
public class GameplaySystemsTests
{
    private static EngineServices NewFacade() => new EngineServices(new CollisionWorld());

    private static Entity NewPlayer() => new Entity
    {
        ClassName = "player",
        Origin = new Vector3(0, 0, 100),
        Mins = new Vector3(-16, -16, -24),
        Maxs = new Vector3(16, 16, 45),
        Health = 100,
        Gravity = 1f,
    };

    [Fact]
    public void Movement_IsDeterministic_AndResponds()
    {
        Api.Services = NewFacade();          // empty world; MovementParameters falls back to Xonotic defaults
        Movement.System = new PlayerPhysics();

        static MovementInput ForwardWish() => new MovementInput
        {
            ViewAngles = Vector3.Zero,            // facing +X
            MoveValues = new Vector3(400, 0, 0),  // full forward
            FrameTime = 1f / 72f,                 // the 72 Hz tick
        };

        var a = NewPlayer();
        var b = NewPlayer();
        for (int i = 0; i < 24; i++)
        {
            Movement.Move(a, ForwardWish());
            Movement.Move(b, ForwardWish());
        }

        // Determinism: two identical runs must match bit-for-bit (prediction/reconciliation depends on this).
        Assert.Equal(a.Origin, b.Origin);
        Assert.Equal(a.Velocity, b.Velocity);

        // Responds to input + gravity: forward air-accel gives +X, gravity gives -Z.
        Assert.True(a.Velocity.X > 0f, $"expected forward velocity, got {a.Velocity}");
        Assert.True(a.Velocity.Z < 0f, $"expected gravity to pull down, got {a.Velocity}");
    }

    [Fact]
    public void Damage_SplitsBetweenArmorAndHealth()
    {
        Api.Services = NewFacade();
        Api.Cvars.Set("g_balance_armor_blockpercent", "0.7");
        Combat.System = new DamageSystem();

        var target = new Entity { ClassName = "player", TakeDamage = DamageMode.Yes, Health = 100f };
        target.ArmorValue = 100f;
        var attacker = new Entity { ClassName = "player" };

        Combat.Damage(target, inflictor: null, attacker: attacker, amount: 50f,
            deathType: "weapon/test", hitLocation: target.Origin, force: Vector3.Zero);

        // 70% of 50 -> armor (save 35), remainder -> health (take 15)
        Assert.Equal(65f, target.GetResource(ResourceType.Armor), 1);
        Assert.Equal(85f, target.Health, 1);
        Assert.NotEqual(DeadFlag.Dead, target.DeadState);
    }

    [Fact]
    public void LethalDamage_Kills_AndFiresDeathHook()
    {
        Api.Services = NewFacade();
        Combat.System = new DamageSystem();

        bool fired = false;
        Entity? victim = null;
        HookHandler<DeathEvent> handler = (ref DeathEvent e) => { fired = true; victim = e.Victim; return false; };
        Combat.Death.Add(handler);
        try
        {
            var target = new Entity { ClassName = "player", TakeDamage = DamageMode.Yes, Health = 50f };
            var attacker = new Entity { ClassName = "player" };

            Combat.Damage(target, null, attacker, 100f, "weapon/test", target.Origin, Vector3.Zero);

            Assert.True(fired, "Combat.Death should fire on a kill");
            Assert.Same(target, victim);
            // The faithful DamageSystem sets DYING on the kill frame, then walks DYING->DEAD over later frames (QC behavior).
            Assert.True(target.DeadState is DeadFlag.Dying or DeadFlag.Dead, $"expected dying/dead, got {target.DeadState}");
        }
        finally
        {
            Combat.Death.Remove(handler);  // keep static hook chain clean for other tests
        }
    }
}

using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the Devastator's <c>g_balance_devastator_force_xyscale</c> knockback shaping to the QC ground truth.
/// The stock balance value is 1 (a no-op), so without these tests the wiring can silently rot — and did: the
/// port passed the value as a Z scale on the REMOTE blast, while QC applies it on X/Y on the CONTACT blast
/// (and not at all on the remote one).
///
/// Mirrors:
///   devastator.qc:29-31  W_Devastator_Explode — force_xyzscale.x/.y = force_xyscale, z stays 1
///   devastator.qc:116-126 W_Devastator_DoRemoteExplode — plain RadiusDamage, forcexyzscale '1 1 1'
///   damage.qc:831-836    per-axis apply, each axis only scaled when its component is non-zero
///
/// Runs in the GlobalState collection (mutates the process-global registries + Api.Services).
/// </summary>
[Collection("GlobalState")]
public class DevastatorForceXyScaleTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    /// <summary>A settable-clock facade so the lifetime/detonate-gate timers are steerable.</summary>
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new();
        public TestFacade() { Inner = new EngineServices(new CollisionWorld()); }
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    private static TestFacade Boot(params (string name, string value)[] cvars)
    {
        var facade = new TestFacade();
        Api.Services = facade;
        VehicleCommon.GameStopped = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        foreach (var (n, v) in cvars) facade.Cvars.Set(n, v);
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();
        return facade;
    }

    private static Entity NewPlayer(TestFacade f, Vector3 origin)
    {
        Entity e = f.Entities.Spawn();
        e.ClassName = "player";
        e.Flags = EntFlags.Client;
        e.Origin = origin;
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.Health = 100; e.MaxHealth = 100;
        e.TakeDamage = DamageMode.Yes;
        e.DamageForceScale = 1f;
        f.Entities.SetOrigin(e, origin);
        return e;
    }

    private static void ArmPrimary(Entity actor, Weapon w, WeaponSlot slot)
    {
        actor.ActiveWeaponId = w.RegistryId;
        actor.SetResourceExplicit(ResourceType.Rockets, 999f);
        var st = actor.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.AttackFinished = 0f;
        st.ButtonAttack = true;
        st.RlRelease = true;
    }

    // Fire a real rocket, park its blast diagonally off the victim's bbox center (all of X/Y/Z in play),
    // detonate it through the requested path, and return the still-stationary victim's resulting knockback.
    // For a victim at rest the calc-push multiplier is exactly 1 and DamageForceScale is 1, so the returned
    // velocity IS the shaped force vector — per-axis ratios between runs read off the scaling directly.
    private static Vector3 VictimKnockback(bool remote, params (string name, string value)[] cvars)
    {
        var f = Boot(cvars);
        f.GameClock.Time = 10f;

        var dev = (Devastator)Weapons.ByName("devastator")!;
        var actor = NewPlayer(f, new Vector3(-500, 0, 0)); // outside both blast radii (110)
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, dev, slot);
        dev.WrThink(actor, slot, FireMode.Primary);

        Entity rocket = f.Entities.FindByClass("rocket").First(e => !e.IsFreed);
        var victim = NewPlayer(f, Vector3.Zero);
        victim.Velocity = Vector3.Zero;

        // Blast point: diagonal offset from the victim's bbox center so the knockback has X, Y and Z parts.
        Vector3 bboxCenter = victim.Origin + (victim.Mins + victim.Maxs) * 0.5f;
        f.Entities.SetOrigin(rocket, bboxCenter + new Vector3(-60, -30, -45));

        if (remote)
        {
            // W_Devastator_DoRemoteExplode: flag rl_detonate_later, step past the detonatedelay gate (0.02).
            rocket.DeadState = DeadFlag.Dying;
            f.GameClock.Time = 10f + dev.Cvars.DetonateDelay + 0.01f;
        }
        else
        {
            // W_Devastator_Explode via end-of-lifetime (same blast as a contact hit, directHit null).
            f.GameClock.Time = 10f + dev.Cvars.Lifetime + 1f;
        }
        rocket.Think!(rocket);

        Assert.True(rocket.IsFreed, "the rocket must have detonated");
        return victim.Velocity;
    }

    [Fact]
    public void ContactExplode_ForceXyScale_ScalesXY_NotZ()
    {
        Vector3 baseline = VictimKnockback(remote: false);
        Vector3 scaled = VictimKnockback(remote: false, ("g_balance_devastator_force_xyscale", "2"));

        Assert.NotEqual(0f, baseline.X); // diagonal placement: every axis carries force
        Assert.NotEqual(0f, baseline.Y);
        Assert.NotEqual(0f, baseline.Z);
        // QC W_Devastator_Explode: force_xyzscale = (xyscale, xyscale, 1).
        Assert.Equal(baseline.X * 2f, scaled.X, 2);
        Assert.Equal(baseline.Y * 2f, scaled.Y, 2);
        Assert.Equal(baseline.Z, scaled.Z, 2); // Z must NOT scale (the original port scaled exactly this axis)
    }

    [Fact]
    public void RemoteExplode_IgnoresForceXyScale()
    {
        Vector3 baseline = VictimKnockback(remote: true);
        Vector3 scaled = VictimKnockback(remote: true, ("g_balance_devastator_force_xyscale", "2"));

        Assert.NotEqual(Vector3.Zero, baseline);
        // QC W_Devastator_DoRemoteExplode goes through the plain RadiusDamage wrapper ('1 1 1'):
        // force_xyscale must not shape the remote blast on ANY axis.
        Assert.Equal(baseline.X, scaled.X, 2);
        Assert.Equal(baseline.Y, scaled.Y, 2);
        Assert.Equal(baseline.Z, scaled.Z, 2);
    }

    [Fact]
    public void ContactExplode_ZeroXyScale_LeavesForceUnscaled()
    {
        // damage.qc:831-836 skips an axis whose scale component is 0 — so xyscale 0 behaves like 1, it does
        // NOT null the horizontal knockback.
        Vector3 baseline = VictimKnockback(remote: false);
        Vector3 zeroed = VictimKnockback(remote: false, ("g_balance_devastator_force_xyscale", "0"));

        Assert.Equal(baseline.X, zeroed.X, 2);
        Assert.Equal(baseline.Y, zeroed.Y, 2);
        Assert.Equal(baseline.Z, zeroed.Z, 2);
    }
}

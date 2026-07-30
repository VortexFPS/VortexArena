using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Gameplay.Scoring;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Regression pins for two upstream fixes ported on 2026-07-27 (upstream-watch UW-0122 / UW-0123).
///
/// <para><b>UW-0122</b> — upstream <c>a7664932</c> (MR !1626, "Make damage accounting simpler and more
/// robust"): <c>healtharmor_applydamage()</c> gained a health parameter so <c>take</c> clamps to the
/// victim's CURRENT HEALTH instead of to the incoming damage. Before the fix a 300-damage hit on a
/// 40-health player booked 300 damage taken, which over-credited every consumer of take/save (the damage
/// log, accuracy "real" credit, and the per-frame damage score columns).</para>
///
/// <para><b>UW-0123</b> — upstream <c>916d46a6</c>: <c>StatusEffects_gettime()</c> used to clamp its result
/// up to the current time, so an effect whose timer had lapsed but whose tick had not yet removed it read
/// as "ends now"; and every per-effect <c>m_tick</c> was reordered to run the base tick (which performs the
/// timeout removal) FIRST and then bail when the effect is no longer active, so an expiring effect no
/// longer gets one extra body run on the frame it lapses.</para>
///
/// Runs in the GlobalState collection because it mutates the process-global registries + Api.Services.
/// </summary>
[Collection("GlobalState")]
public class UpstreamDamageAndStatusTimerTests
{
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new() { Time = 10f, FrameTime = 1f / 64f };

        public TestFacade() { Inner = new EngineServices(new CollisionWorld()); }

        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    private static Entity NewPlayer() => new Entity
    {
        ClassName = "player",
        Flags = EntFlags.Client,
        Origin = Vector3.Zero,
        Mins = new Vector3(-16, -16, -24),
        Maxs = new Vector3(16, 16, 45),
        Health = 100,
        Gravity = 1f,
        Alpha = 1f,
    };

    private static TestFacade Boot()
    {
        var facade = new TestFacade();
        Api.Services = facade;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        Movement.System = new PlayerPhysics();
        MutatorActivation.Apply();
        return facade;
    }

    private static StatusEffectDef Effect(string name)
    {
        var d = StatusEffectsCatalog.ByName(name);
        Assert.NotNull(d);
        return d!;
    }

    // =====================================================================================
    //  UW-0122 — take is bounded by current health, so overkill is never counted
    // =====================================================================================

    [Fact]
    public void SplitHealthArmorHook_SeesTakeAlreadyBoundedByCurrentHealth()
    {
        // This is what the health bound actually buys. The port already re-clamped take to current health
        // AFTER the hook, so the victim's end-state resources were never wrong — but every
        // PlayerDamage_SplitHealthArmor handler (ClanArena damage2score, Mayhem accrual, vampire) read the
        // UNBOUNDED value and had to know to clamp it itself. Upstream's comment on the fix is explicit:
        // "healtharmor_applydamage() bounds take and save to current health and armour so hooks need not
        // duplicate that."
        Boot();

        float seenTake = float.NaN;
        float seenSave = float.NaN;
        bool Capture(ref GameHooks.PlayerDamageArgs a)
        {
            seenTake = a.DamageTake;
            seenSave = a.DamageSave;
            return false;
        }
        GameHooks.PlayerDamageSplitHealthArmor.Add(Capture);
        try
        {
            var attacker = NewPlayer();
            var target = NewPlayer();
            target.TakeDamage = DamageMode.Yes;
            target.SetResourceExplicit(ResourceType.Health, 40f);
            target.SetResourceExplicit(ResourceType.Armor, 0f);

            Combat.Damage(target, null, attacker, 300f, "weapon/test", target.Origin, Vector3.Zero);

            Assert.Equal(40f, seenTake, 3);   // was 300 before the port of upstream a7664932
            Assert.Equal(0f, seenSave, 3);
        }
        finally
        {
            GameHooks.PlayerDamageSplitHealthArmor.Remove(Capture);
        }
    }

    [Fact]
    public void VirtualFriendlyFire_ReportsOnlyWhatTheVictimCouldHaveLost()
    {
        // The g_friendlyfire_virtual / g_mirrordamage_virtual paths add their split straight onto
        // dmg_take/dmg_save with NO later clamp, so before the health bound they showed the victim a HUD
        // number far larger than the damage they would actually have taken.
        var f = Boot();
        f.Inner.Cvars.Set("teamplay_mode", "4");
        f.Inner.Cvars.Set("g_friendlyfire", "1");
        f.Inner.Cvars.Set("g_friendlyfire_virtual", "1");
        f.Inner.Cvars.Set("g_teamdamage_threshold", "0");
        GameScores.Teamplay = true;

        var attacker = NewPlayer();
        var target = NewPlayer();
        attacker.Team = 5;
        target.Team = 5;                       // same team -> the virtual friendly-fire branch
        target.TakeDamage = DamageMode.Yes;
        target.SetResourceExplicit(ResourceType.Health, 30f);
        target.SetResourceExplicit(ResourceType.Armor, 0f);
        target.DmgTake = 0f;
        target.DmgSave = 0f;

        Combat.Damage(target, null, attacker, 400f, "weapon/test", target.Origin, Vector3.Zero);

        // Virtual: no health is actually lost, and the reported figure is capped at what WOULD have been.
        Assert.Equal(30f, target.GetResource(ResourceType.Health), 3);
        Assert.True(target.DmgTake <= 30f + 1e-3f,
            $"virtual friendly fire reported {target.DmgTake} taken against only 30 health");
    }

    [Fact]
    public void SurvivableDamage_SplitIsUnchanged()
    {
        Boot();

        var attacker = NewPlayer();
        var target = NewPlayer();
        target.TakeDamage = DamageMode.Yes;
        target.SetResourceExplicit(ResourceType.Health, 100f);
        target.SetResourceExplicit(ResourceType.Armor, 0f);
        target.DmgTake = 0f;
        target.DmgSave = 0f;

        // A hit the victim can absorb is unaffected by the health bound — this is the guard against
        // "fixing" the overkill case by clamping something that also touches ordinary damage.
        Combat.Damage(target, null, attacker, 35f, "weapon/test", target.Origin, Vector3.Zero);

        Assert.Equal(35f, target.DmgTake, 3);
        Assert.Equal(65f, target.GetResource(ResourceType.Health), 3);
    }

    // =====================================================================================
    //  UW-0123 — GetTime contract + tick ordering
    // =====================================================================================

    [Fact]
    public void GetTime_ReturnsZero_WhenTheEffectIsAbsent()
    {
        var f = Boot();
        var p = NewPlayer();

        Assert.Equal(0f, StatusEffectsCatalog.GetTime(p, Effect("strength"), f.GameClock.Time));
    }

    [Fact]
    public void GetTime_DoesNotClampAnExpiredTimerUpToNow()
    {
        var f = Boot();
        var p = NewPlayer();
        var strength = Effect("strength");

        StatusEffectsCatalog.Apply(p, strength, 5f);          // expires at t=15
        float expireTime = StatusEffectsCatalog.GetTime(p, strength, f.GameClock.Time);
        Assert.Equal(15f, expireTime, 3);

        // Advance past the expiry WITHOUT ticking: the effect is still present but lapsed. Before the
        // upstream fix this reported `now`, which made `gettime() + duration` start a window in the past.
        f.GameClock.Time = 20f;
        Assert.Equal(15f, StatusEffectsCatalog.GetTime(p, strength, f.GameClock.Time), 3);
    }

    [Fact]
    public void PowerupPickup_AfterTheTimerLapsed_ArmsTheFullNewDuration()
    {
        // The concrete upstream bug: with a lapsed timer still present, the pickup computed its new window
        // from a stale value and the powerup could be lost the instant it was taken.
        var f = Boot();
        var player = NewPlayer();
        var strength = Effect("strength");

        StatusEffectsCatalog.Apply(player, strength, 5f);      // expires at t=15
        f.GameClock.Time = 20f;                                // lapsed, not yet ticked away

        var item = new Entity { ClassName = "item", Origin = Vector3.Zero };
        item.StrengthFinished = 30f;
        ItemPickupRules.ItemGiveTo(item, player);

        // A full 30s window from NOW — not 15 + 30 measured from a stale base, and never already-expired.
        Assert.Equal(50f, StatusEffectsCatalog.GetTime(player, strength, f.GameClock.Time), 3);
    }

    [Fact]
    public void ExpiredEffect_IsRemovedAndDealsNoFurtherDamage()
    {
        // Guard for the tick reorder (timeout removal now runs BEFORE the per-effect body, matching
        // upstream's SUPER-first-then-bail-if-inactive shape). NOTE this passes on both sides of the
        // reorder: FireApplyDamage already self-guards on the remaining burn time, so the extra body run
        // the old order allowed was a no-op for burning. The reorder is a fidelity fix, not a visible bug
        // fix — this test exists to pin that an expiring burn stays harmless if either side is touched.
        var f = Boot();
        var target = NewPlayer();
        target.TakeDamage = DamageMode.Yes;
        target.SetResourceExplicit(ResourceType.Health, 100f);

        var burning = Effect("burning");
        StatusEffectsCatalog.Apply(target, burning, 2f, strength: 50f);   // 50 dps, expires at t=12
        target.FireDamagePerSec = 50f;

        f.GameClock.Time = 11f;                     // still burning
        StatusEffectsCatalog.Tick(target, f.GameClock.Time);
        float healthWhileBurning = target.GetResource(ResourceType.Health);
        Assert.True(healthWhileBurning < 100f, "a live burn should still tick damage");

        f.GameClock.Time = 12.5f;                   // lapsed
        StatusEffectsCatalog.Tick(target, f.GameClock.Time);

        Assert.False(StatusEffectsCatalog.Has(target, burning), "an expired burn should be removed");
        Assert.Equal(healthWhileBurning, target.GetResource(ResourceType.Health), 3);
    }

    [Fact]
    public void PersistentEffect_StillTicksAndDoesNotTimeOut()
    {
        // The persistent path must survive the tick reorder: a persistent effect never times out, so its
        // body keeps running (QC: SUPER returns early without removing, m_active stays true).
        var f = Boot();
        var p = NewPlayer();
        var frozen = Effect("frozen");

        StatusEffectsCatalog.Apply(p, frozen, 0f);   // port convention: duration <= 0 == permanent

        f.GameClock.Time = 999f;
        StatusEffectsCatalog.Tick(p, f.GameClock.Time);

        Assert.True(StatusEffectsCatalog.Has(p, frozen), "a permanent effect must not be timed out");
        // and it must still read as running rather than as "inactive" (which a raw stored 0 would imply).
        Assert.Equal(999f, StatusEffectsCatalog.GetTime(p, frozen, f.GameClock.Time), 3);
    }
}

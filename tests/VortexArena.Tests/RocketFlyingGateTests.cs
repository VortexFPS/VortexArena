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
/// T19c — the rocket/mine remote-detonate GATE field (<see cref="Entity.ProjectileDetonateTime"/>, the role QC
/// overloaded onto <c>.spawnshieldtime</c> for projectiles) and the Rocket Flying mutator's clear of it.
///
/// Before this task the Devastator stored its gate in a closure local and the Minelayer on <c>LTime</c>, while
/// the mutator wrote the unrelated player-spawn-shield field — so <c>g_rocket_flying</c> was INERT (the existing
/// MutatorBatchT19 test asserted the dead write and passed without proving any gate cleared). These tests prove
/// the mutator's write now reaches the gate both weapons read, and that the field defaults to the SAFE proximity
/// branch (-1) so a pooled/reused entity is never spuriously detonatable.
///
/// Mirrors:
///   sv_rocketflying.qc:7-16  MUTATOR_HOOKFUNCTION(rocketflying, EditProjectile) — proj.spawnshieldtime = time
///   devastator.qc:295-298    W_Devastator_Attack — missile.spawnshieldtime = (detonatedelay>=0 ? time+delay : -1)
///   devastator.qc:140-152    W_Devastator_RemoteExplode — timer vs proximity gate
///   minelayer.qc:316-319     W_MineLayer_Attack — mine.spawnshieldtime seed (detonatedelay default -1)
///
/// Runs in the GlobalState collection (mutates the process-global registries + Api.Services + hook chains;
/// xUnit parallelism is disabled assembly-wide — see the TestParallelization memo).
/// </summary>
[Collection("GlobalState")]
public class RocketFlyingGateTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    /// <summary>A settable-clock facade so the time-gated detonate logic is testable.</summary>
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
        GameRegistries.Bootstrap();      // discovers [Weapon] + [Mutator] classes and runs Configure()
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();       // subscribe the enabled mutators' hooks
        return facade;
    }

    private static Entity NewPlayer(TestFacade f, Vector3 origin = default)
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

    // Arm an actor to fire weapon `w` primary THIS tick through the real WrThink path (the QC W_WeaponFrame
    // fire branch needs: the weapon equipped/active, the slot READY, the refire timer clear, the primary
    // button held, the rl_release latch set, and ammo).
    private static void ArmPrimary(Entity actor, Weapon w, WeaponSlot slot, float rockets = 999f)
    {
        actor.ActiveWeaponId = w.RegistryId;        // QC actor.(weaponentity).m_weapon == this
        actor.SetResourceExplicit(ResourceType.Rockets, rockets);
        var st = actor.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.AttackFinished = 0f;
        st.ButtonAttack = true;                     // PHYS_INPUT_BUTTON_ATCK held
        st.RlRelease = true;                        // Devastator: fresh press allowed
    }

    // ---------------------------------------------------------------------------------------------
    //  the field itself
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ProjectileDetonateTime_DefaultsToProximityBranch()
    {
        // A fresh (pooled-reuse) entity MUST default to -1 (the proximity branch), NOT 0 — a stale 0 would sit
        // in the timer branch with time>=0 always true, allowing instant detonation even without the mutator.
        Assert.Equal(-1f, new Entity().ProjectileDetonateTime);
    }

    // ---------------------------------------------------------------------------------------------
    //  rocketflying mutator clears the gate (relocated from MutatorBatchT19's dead SpawnShieldExpire assert)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RocketFlying_ClearsDetonateDelay_OnRocketProjectile()
    {
        var f = Boot(("g_rocket_flying", "1"));
        f.GameClock.Time = 7.5f;

        var owner = NewPlayer(f);
        var rocket = new Entity { ClassName = "rocket", ProjectileDetonateTime = 999f };
        var ep = new MutatorHooks.EditProjectileArgs(owner, rocket);
        MutatorHooks.EditProjectile.Call(ref ep);

        Assert.Equal(7.5f, rocket.ProjectileDetonateTime); // QC: proj.spawnshieldtime = time
        Assert.Equal(0f, rocket.SpawnShieldExpire);        // and the player-spawn-shield field is untouched
    }

    [Fact]
    public void RocketFlying_ClearsDetonateDelay_OnMineProjectile()
    {
        // QC clears "rocket" OR "mine" — the Minelayer is covered by the same hook.
        var f = Boot(("g_rocket_flying", "1"));
        f.GameClock.Time = 3f;

        var owner = NewPlayer(f);
        var mine = new Entity { ClassName = "mine", ProjectileDetonateTime = 999f };
        var ep = new MutatorHooks.EditProjectileArgs(owner, mine);
        MutatorHooks.EditProjectile.Call(ref ep);

        Assert.Equal(3f, mine.ProjectileDetonateTime);
    }

    [Fact]
    public void RocketFlying_IgnoresNonRocketProjectile()
    {
        var f = Boot(("g_rocket_flying", "1"));
        f.GameClock.Time = 7.5f;

        var owner = NewPlayer(f);
        var other = new Entity { ClassName = "grapplinghook", ProjectileDetonateTime = 999f };
        var ep = new MutatorHooks.EditProjectileArgs(owner, other);
        MutatorHooks.EditProjectile.Call(ref ep);

        Assert.Equal(999f, other.ProjectileDetonateTime); // untouched: not a rocket/mine
    }

    [Fact]
    public void RocketFlying_Disabled_LeavesGate()
    {
        // g_rocket_flying unset -> the mutator's IsEnabled is false, so it never subscribes EditProjectile.
        var f = Boot();
        f.GameClock.Time = 7.5f;

        var owner = NewPlayer(f);
        var rocket = new Entity { ClassName = "rocket", ProjectileDetonateTime = 999f };
        var ep = new MutatorHooks.EditProjectileArgs(owner, rocket);
        MutatorHooks.EditProjectile.Call(ref ep);

        Assert.Equal(999f, rocket.ProjectileDetonateTime); // unchanged: mutator inert
    }

    [Fact]
    public void RocketFlying_DisableDelaysOff_LeavesGate()
    {
        // Mutator enabled but g_rocket_flying_disabledelays 0 -> the inner guard is false, gate left alone.
        var f = Boot(("g_rocket_flying", "1"), ("g_rocket_flying_disabledelays", "0"));
        f.GameClock.Time = 7.5f;

        var owner = NewPlayer(f);
        var rocket = new Entity { ClassName = "rocket", ProjectileDetonateTime = 999f };
        var ep = new MutatorHooks.EditProjectileArgs(owner, rocket);
        MutatorHooks.EditProjectile.Call(ref ep);

        Assert.Equal(999f, rocket.ProjectileDetonateTime); // unchanged: disabledelays off
    }

    // ---------------------------------------------------------------------------------------------
    //  Devastator seeds the gate from detonatedelay (timer vs proximity), no mutator
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Devastator_GateField_SeededFromDetonateDelay()
    {
        var f = Boot(); // stock balance: g_balance_devastator_detonatedelay 0.02
        f.GameClock.Time = 5f;

        var dev = (Devastator)Weapons.ByName("devastator")!;
        Assert.True(dev.Cvars.DetonateDelay >= 0f, "stock devastator detonatedelay (0.02) must be >= 0");

        var actor = NewPlayer(f, new Vector3(0, 0, 0));
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, dev, slot);

        dev.WrThink(actor, slot, FireMode.Primary); // launches a rocket through W_Devastator_Attack

        Entity rocket = f.Entities.FindByClass("rocket").First(e => !e.IsFreed);
        // QC: detonatedelay >= 0 -> spawnshieldtime = time + detonatedelay (the timer branch).
        Assert.Equal(5f + dev.Cvars.DetonateDelay, rocket.ProjectileDetonateTime, 3);
    }

    [Fact]
    public void Devastator_Rocket_IsSolidCorpse_TransparentToFirer()
    {
        // QC PROJECTILE_MAKETRIGGER (server/weapons/common.qh:33): the rocket is SOLID_CORPSE with
        // dphitcontentsmask SOLID|BODY|CORPSE, so it is transparent to a player's movement (which masks
        // SOLID|BODY|PLAYERCLIP, no CORPSE) — the fix for "the rocket hits the firer while walking forward".
        var f = Boot();
        f.GameClock.Time = 5f;

        var dev = (Devastator)Weapons.ByName("devastator")!;
        var actor = NewPlayer(f, Vector3.Zero);
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, dev, slot);

        dev.WrThink(actor, slot, FireMode.Primary);

        Entity rocket = f.Entities.FindByClass("rocket").First(e => !e.IsFreed);
        Assert.Equal(Solid.Corpse, rocket.Solid);
        Assert.NotEqual(0, rocket.DpHitContentsMask & XonoticGodot.Engine.Collision.SuperContents.Corpse);
        Assert.NotEqual(0, rocket.DpHitContentsMask & XonoticGodot.Engine.Collision.SuperContents.Body);
        Assert.NotEqual(0, rocket.DpHitContentsMask & XonoticGodot.Engine.Collision.SuperContents.Solid);
    }

    [Fact]
    public void Devastator_GateField_NegativeDetonateDelay_IsProximity()
    {
        // detonatedelay < 0 -> spawnshieldtime = -1 (proximity-safety branch; QC the rocket-jump path).
        var f = Boot(("g_balance_devastator_detonatedelay", "-1"));
        f.GameClock.Time = 5f;

        var dev = (Devastator)Weapons.ByName("devastator")!;
        var actor = NewPlayer(f, Vector3.Zero);
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, dev, slot);

        dev.WrThink(actor, slot, FireMode.Primary);

        Entity rocket = f.Entities.FindByClass("rocket").First(e => !e.IsFreed);
        Assert.Equal(-1f, rocket.ProjectileDetonateTime);
    }

    // ---------------------------------------------------------------------------------------------
    //  Minelayer seeds the gate; default detonatedelay -1 -> proximity branch (LTime is no longer the gate)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Minelayer_GateField_DefaultProximity()
    {
        var f = Boot(); // stock balance: g_balance_minelayer_detonatedelay -1
        f.GameClock.Time = 5f;

        var ml = (Minelayer)Weapons.ByName("minelayer")!;
        Assert.True(ml.Cvars.DetonateDelay < 0f, "stock minelayer detonatedelay (-1) must be < 0 (proximity)");

        var actor = NewPlayer(f, Vector3.Zero);
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, ml, slot);

        ml.WrThink(actor, slot, FireMode.Primary); // lays a mine through W_MineLayer_Attack

        Entity mine = f.Entities.FindByClass("mine").First(e => !e.IsFreed);
        // Default detonatedelay -1 -> the gate sits in the proximity branch.
        Assert.Equal(-1f, mine.ProjectileDetonateTime);
        // The gate is no longer stored on LTime (mines have no pushltime; LTime is now free, left at default 0).
        Assert.Equal(0f, mine.LTime);
    }

    [Fact]
    public void Minelayer_GateField_SeededTimer_WhenDetonateDelayPositive()
    {
        var f = Boot(("g_balance_minelayer_detonatedelay", "0.5"));
        f.GameClock.Time = 5f;

        var ml = (Minelayer)Weapons.ByName("minelayer")!;
        var actor = NewPlayer(f, Vector3.Zero);
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, ml, slot);

        ml.WrThink(actor, slot, FireMode.Primary);

        Entity mine = f.Entities.FindByClass("mine").First(e => !e.IsFreed);
        Assert.Equal(5f + 0.5f, mine.ProjectileDetonateTime, 3);
    }

    // ---------------------------------------------------------------------------------------------
    //  end-to-end: the mutator's clear actually opens remote detonation (the regression guard)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Devastator_RocketFlying_DetonatesImmediately()
    {
        // With the mutator on, EditProjectile (fired inside W_Devastator_Attack) clears the gate to `time`, so
        // the timer branch (time >= gate) is immediately open -> a flagged rocket detonates on its next think.
        var f = Boot(("g_rocket_flying", "1"));
        f.GameClock.Time = 10f;

        var dev = (Devastator)Weapons.ByName("devastator")!;
        var actor = NewPlayer(f, Vector3.Zero);
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, dev, slot);

        dev.WrThink(actor, slot, FireMode.Primary);
        Entity rocket = f.Entities.FindByClass("rocket").First(e => !e.IsFreed);
        // The mutator's EditProjectile write reached the gate:
        Assert.Equal(10f, rocket.ProjectileDetonateTime);

        // Flag it for remote detonation (QC rl_detonate_later, set by the secondary press) and run its think:
        // the gate is open (time 10 >= 10) so it detonates and is removed.
        rocket.DeadState = DeadFlag.Dying;
        rocket.Think!(rocket);
        Assert.True(rocket.IsFreed, "rocket-flying gate is open at fire time -> the rocket detonates immediately");
    }

    [Fact]
    public void Devastator_NoMutator_HeldByDetonateDelayTimer()
    {
        // Contrast: WITHOUT the mutator, the gate is time + 0.02; a flagged rocket whose timer hasn't elapsed is
        // NOT yet detonatable (proving the gate genuinely controls detonation, so the mutator's clear matters).
        var f = Boot(); // no g_rocket_flying
        f.GameClock.Time = 10f;

        var dev = (Devastator)Weapons.ByName("devastator")!;
        var actor = NewPlayer(f, Vector3.Zero);
        var slot = new WeaponSlot(0);
        ArmPrimary(actor, dev, slot);

        dev.WrThink(actor, slot, FireMode.Primary);
        Entity rocket = f.Entities.FindByClass("rocket").First(e => !e.IsFreed);
        Assert.Equal(10f + dev.Cvars.DetonateDelay, rocket.ProjectileDetonateTime, 3); // timer not yet elapsed

        // Flag + think at the SAME instant: time (10) < gate (10.02) -> remote detonation is held.
        rocket.DeadState = DeadFlag.Dying;
        rocket.Think!(rocket);
        Assert.False(rocket.IsFreed, "the 0.02s arm window must hold the rocket until its detonate timer elapses");

        // Advance past the timer: now the same think detonates it.
        f.GameClock.Time = 10f + dev.Cvars.DetonateDelay + 0.01f;
        rocket.Think!(rocket);
        Assert.True(rocket.IsFreed, "once the detonate timer elapses the flagged rocket detonates");
    }

    // ---------------------------------------------------------------------------------------------
    //  BuildMutatorsString / BuildMutatorsPrettyString — advertise active mutator to server-log and
    //  client serverinfo (QC sv_rocketflying.qc:23-31)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RocketFlying_BuildMutatorsString_AppendsToken_WhenActive()
    {
        // QC: MUTATOR_HOOKFUNCTION(rocketflying, BuildMutatorsString) → M_ARGV(0,string) = strcat(s, ":RocketFlying")
        // The chain runner (MutatorActivation.BuildMutatorsString) calls each Added mutator's override.
        Boot(("g_rocket_flying", "1"));
        string result = MutatorActivation.BuildMutatorsString("");
        Assert.Contains(":RocketFlying", result);
    }

    [Fact]
    public void RocketFlying_BuildMutatorsPrettyString_AppendsToken_WhenActive()
    {
        // QC: MUTATOR_HOOKFUNCTION(rocketflying, BuildMutatorsPrettyString) → strcat(s, ", Rocket Flying")
        // Used by PutClientInServer (server/client.qc:1107) and the server-browser modifications field.
        Boot(("g_rocket_flying", "1"));
        string raw = MutatorActivation.BuildMutatorsPrettyString("");
        // QC strips the leading ", " with substring(s, 2, ...) at the call site; the raw result retains it.
        Assert.Contains(", Rocket Flying", raw);
        // Stripping the leader gives the display-ready string.
        string stripped = raw.StartsWith(", ", System.StringComparison.Ordinal) ? raw.Substring(2) : raw;
        Assert.Contains("Rocket Flying", stripped);
    }

    [Fact]
    public void RocketFlying_BuildMutatorsString_EmitsNoToken_WhenInactive()
    {
        // When the mutator is off (g_rocket_flying unset) it is not Added → its override is never called.
        Boot(); // no g_rocket_flying
        string result = MutatorActivation.BuildMutatorsString("");
        Assert.DoesNotContain("RocketFlying", result);
    }

    [Fact]
    public void RocketFlying_BuildMutatorsPrettyString_EmitsNoToken_WhenInactive()
    {
        Boot(); // no g_rocket_flying
        string result = MutatorActivation.BuildMutatorsPrettyString("");
        Assert.DoesNotContain("Rocket Flying", result);
    }
}

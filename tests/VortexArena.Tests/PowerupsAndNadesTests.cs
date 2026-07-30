using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Nades;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for T11 (part A): the powerup CONSUMER effects (strength/shield damage+force, speed highspeed +
/// weapon-rate, invisibility alpha) and the Nades CORE (the nade registry/type-selection, the held-nade
/// charge + throw, the bonus economy, and the boom-dispatch seam). The per-type boom files are part B; this
/// suite exercises the seam (dispatch routing) with a stub boom so it's verifiable without them.
///
/// Runs in the GlobalState collection because it mutates the process-global registries + Api.Services.
/// </summary>
[Collection("GlobalState")]
public class PowerupsAndNadesTests
{
    /// <summary>
    /// A test facade that reuses the real <see cref="EngineServices"/> (entity table / trace / cvar store /
    /// sound) but swaps in a settable <see cref="MutableClock"/> — the engine's GameClock has an internal
    /// setter, so the nade timing tests need a clock they can advance.
    /// </summary>
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

    private static Entity NewPlayer() => new Entity
    {
        ClassName = "player",
        Flags = EntFlags.Client,
        Origin = new Vector3(0, 0, 0),
        Mins = new Vector3(-16, -16, -24),
        Maxs = new Vector3(16, 16, 45),
        Health = 100,
        Gravity = 1f,
        Alpha = 1f,
    };

    // Build a clean world: services + registries + status effects + nade catalog, mutators activated.
    private static TestFacade Boot()
    {
        var facade = new TestFacade();
        Api.Services = facade;
        GameRegistries.Reset();          // drop any mutator hooks from a prior test
        StatusEffectsCatalog.RegisterAll();
        NadeRegistry.RegisterAll();
        GameRegistries.Bootstrap();      // discovers [Mutator] PowerupsMutator + NadesMutator
        Combat.System = new DamageSystem();
        Movement.System = new PlayerPhysics();
        MutatorActivation.Apply();       // subscribe the enabled mutators' hooks
        return facade;
    }

    private static StatusEffectDef Effect(string name)
    {
        var d = StatusEffectsCatalog.ByName(name);
        Assert.NotNull(d);
        return d!;
    }

    // =====================================================================================
    //  Powerups — consumers
    // =====================================================================================

    [Fact]
    public void Strength_Powerup_TriplesOutgoingDamage()
    {
        Boot(); // PowerupsMutator is always-enabled

        var attacker = NewPlayer();
        var target = NewPlayer();
        target.TakeDamage = DamageMode.Yes;

        // baseline: no powerup, 50 raw damage (no armor) -> ~50 health taken.
        float before = target.Health;
        Combat.Damage(target, null, attacker, 50f, "weapon/test", target.Origin, Vector3.Zero);
        float baselineTaken = before - target.Health;

        // with strength: g_balance_powerup_strength_damage (3x) is applied in Damage_Calculate.
        target.Health = 100f;
        StatusEffectsCatalog.Apply(attacker, Effect("strength"), 30f);
        before = target.Health;
        Combat.Damage(target, null, attacker, 50f, "weapon/test", target.Origin, Vector3.Zero);
        float strengthTaken = before - target.Health;

        Assert.True(strengthTaken > baselineTaken * 2.5f,
            $"strength should ~triple damage: baseline {baselineTaken}, strength {strengthTaken}");
    }

    [Fact]
    public void Shield_Powerup_ReducesIncomingDamage()
    {
        Boot();

        var attacker = NewPlayer();
        var target = NewPlayer();
        target.TakeDamage = DamageMode.Yes;

        // shield: target takes 0.33x damage (g_balance_powerup_invincible_takedamage).
        StatusEffectsCatalog.Apply(target, Effect("shield"), 30f);
        float before = target.Health;
        Combat.Damage(target, null, attacker, 60f, "weapon/test", target.Origin, Vector3.Zero);
        float taken = before - target.Health;

        Assert.True(taken < 30f, $"shield should cut damage to ~1/3 (got {taken} from 60)");
        Assert.True(taken > 10f, $"shield should not nullify damage entirely (got {taken})");
    }

    [Fact]
    public void Speed_Powerup_RaisesTopSpeed_ViaSpeedMultiplier()
    {
        Boot();

        // The PlayerPhysics hook writes SpeedMultiplier; the integrator applies it. Assert the multiplier was
        // set (the inert-highspeed bug is "SpeedMultiplier never read"; here we prove the hook writes it).
        var player = NewPlayer();
        player.Flags |= EntFlags.OnGround;
        StatusEffectsCatalog.Apply(player, Effect("speed"), 30f);

        var input = new MovementInput
        {
            ViewAngles = Vector3.Zero,
            MoveValues = new Vector3(400, 0, 0),
            FrameTime = 1f / 72f,
        };
        Movement.Move(player, input);

        // After Move, SpeedMultiplier reflects the powerup factor (1.5) — it's reset+written inside the hook.
        Assert.Equal(1.5f, player.SpeedMultiplier, 3);
    }

    [Fact]
    public void Speed_Powerup_SpeedMultiplier_ResetsWhenEffectGone()
    {
        Boot();

        var player = NewPlayer();
        player.Flags |= EntFlags.OnGround;
        StatusEffectsCatalog.Apply(player, Effect("speed"), 30f);
        Movement.Move(player, new MovementInput { FrameTime = 1f / 72f });
        Assert.Equal(1.5f, player.SpeedMultiplier, 3);

        // remove the effect -> next frame the central reset returns the multiplier to 1.
        StatusEffectsCatalog.Remove(player, Effect("speed"));
        Movement.Move(player, new MovementInput { FrameTime = 1f / 72f });
        Assert.Equal(1f, player.SpeedMultiplier, 3);
    }

    [Fact]
    public void Speed_Powerup_SpeedsUpWeaponRate()
    {
        Boot();

        var player = NewPlayer();
        // The WeaponRateFactor hook multiplies the factor by 0.8 when Speed is active. Fire the chain directly
        // (the value the shared fire gate consumes).
        var argsNoPow = new MutatorHooks.WeaponRateFactorArgs(1f, player);
        MutatorHooks.WeaponRateFactor.Call(ref argsNoPow);
        Assert.Equal(1f, argsNoPow.Factor, 3);

        StatusEffectsCatalog.Apply(player, Effect("speed"), 30f);
        var argsPow = new MutatorHooks.WeaponRateFactorArgs(1f, player);
        MutatorHooks.WeaponRateFactor.Call(ref argsPow);
        Assert.Equal(0.8f, argsPow.Factor, 3);
    }

    [Fact]
    public void Invisibility_Powerup_LowersAlpha_AndRestores()
    {
        Boot();

        var player = NewPlayer();
        Assert.Equal(1f, player.Alpha, 3);

        StatusEffectsCatalog.Apply(player, Effect("invisibility"), 30f);
        var pre = new MutatorHooks.PlayerPreThinkArgs(player);
        MutatorHooks.PlayerPreThink.Call(ref pre);
        Assert.Equal(0.15f, player.Alpha, 3); // g_balance_powerup_invisibility_alpha

        StatusEffectsCatalog.Remove(player, Effect("invisibility"));
        MutatorHooks.PlayerPreThink.Call(ref pre);
        Assert.Equal(1f, player.Alpha, 3); // restored to default_player_alpha
    }

    // =====================================================================================
    //  Nades — registry / type selection
    // =====================================================================================

    [Fact]
    public void NadeRegistry_HasTheTwelveTypes_WithStableIds()
    {
        NadeRegistry.RegisterAll();
        Assert.Equal(0, NadeRegistry.Null.Id);
        Assert.Equal("normal", NadeRegistry.Normal!.NetName);
        Assert.Equal(1, NadeRegistry.Normal.Id);
        Assert.Equal("napalm", NadeRegistry.Napalm!.NetName);
        Assert.Equal("pokenade", NadeRegistry.Monster!.NetName); // monster nade netname is "pokenade"
        Assert.Equal(0.45f, NadeRegistry.Veil!.Alpha, 3);        // veil nade is semi-transparent
        Assert.Equal(12, NadeRegistry.All.Count);                // Null + 11 types
    }

    [Fact]
    public void NadeRegistry_FromString_ByNameAndImpulse()
    {
        NadeRegistry.RegisterAll();
        Assert.Equal("napalm", NadeRegistry.FromString("napalm").NetName);
        Assert.Equal("napalm", NadeRegistry.FromString("2").NetName); // impulse 2 == napalm
        Assert.Equal(0, NadeRegistry.FromString("nonsense").Id);      // unknown -> Null sentinel
    }

    [Fact]
    public void NadeRegistry_CheckType_GatesOnCvar_FallsBackToNormal()
    {
        var facade = Boot();

        // napalm is OFF by default (g_nades_napalm 0) -> CheckType falls back to NORMAL.
        Assert.Equal("normal", NadeRegistry.CheckType(NadeRegistry.Napalm).NetName);

        // enable it -> CheckType returns napalm.
        facade.Cvars.Set("g_nades_napalm", "1");
        Assert.Equal("napalm", NadeRegistry.CheckType(NadeRegistry.Napalm).NetName);

        // ice gate: explicit-enable path (the cvar store is empty here, so the gate reads 0 until set).
        facade.Cvars.Set("g_nades_ice", "1");
        Assert.Equal("ice", NadeRegistry.CheckType(NadeRegistry.Ice).NetName);

        // Null sentinel passes through (random).
        Assert.Equal(0, NadeRegistry.CheckType(NadeRegistry.Null).Id);
    }

    // =====================================================================================
    //  Nades — held-nade prime + throw (the offhand path)
    // =====================================================================================

    [Fact]
    public void Nade_Prime_ThenThrow_SpawnsAProjectile()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades", "1");
        facade.GameClock.FrameTime = 1f / 72f;
        facade.GameClock.Time = 100f;

        var player = NewPlayer();
        player.OffhandWeapon = "nade";
        player.NadeRefire = 0f; // allow prime now

        // press: prime a nade.
        NadeThrow.OffhandThink(player, keyPressed: true);
        Assert.NotNull(player.Nade);
        Assert.Equal("normal", (NadeRegistry.ById(player.Nade!.NadeBonusType) ?? NadeRegistry.Null).NetName);

        // hold >1s, then release: the nade is tossed (becomes a projectile, no longer held).
        facade.GameClock.Time = 102f;
        NadeThrow.OffhandThink(player, keyPressed: false);
        Assert.Null(player.Nade);

        // a thrown nade entity exists, is shootable, and bounces.
        Entity? thrown = null;
        foreach (Entity e in facade.Inner.EntityTable.All)
            if (!e.IsFreed && e.ClassName == "nade") { thrown = e; break; }
        Assert.NotNull(thrown);
        Assert.Equal(MoveType.Bounce, thrown!.MoveType);
        Assert.Equal(DamageMode.Aim, thrown.TakeDamage);
        Assert.Equal(25f, thrown.MaxHealth, 1); // g_nades_nade_health
    }

    [Fact]
    public void Nade_Throw_ForceRampsWithChargeTime()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades", "1");
        facade.Cvars.Set("g_nades_spread", "0"); // deterministic direction for the magnitude check
        facade.GameClock.FrameTime = 1f / 72f;

        // Short charge (exactly the 1s minimum) -> near minforce.
        facade.GameClock.Time = 10f;
        var p1 = NewPlayer(); p1.OffhandWeapon = "nade"; p1.NadeRefire = 0f;
        NadeThrow.OffhandThink(p1, true);
        facade.GameClock.Time = 11f; // held 1s
        NadeThrow.OffhandThink(p1, false);
        float speed1 = FindThrown(facade).Velocity.Length();

        // Long charge (3s) -> much higher force.
        facade.GameClock.Time = 20f;
        var p2 = NewPlayer(); p2.OffhandWeapon = "nade"; p2.NadeRefire = 0f;
        NadeThrow.OffhandThink(p2, true);
        facade.GameClock.Time = 23f; // held 3s
        NadeThrow.OffhandThink(p2, false);
        float speed2 = FindThrown(facade, skip: 1).Velocity.Length();

        Assert.True(speed2 > speed1 * 1.5f, $"longer charge => more force: {speed1} -> {speed2}");
    }

    [Fact]
    public void Nade_CannotPrime_BeforeRefire()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades", "1");
        facade.GameClock.Time = 5f;

        var player = NewPlayer();
        player.OffhandWeapon = "nade";
        player.NadeRefire = 100f; // refire in the future -> can't prime yet

        NadeThrow.OffhandThink(player, keyPressed: true);
        Assert.Null(player.Nade);
    }

    // =====================================================================================
    //  Nades — bonus economy
    // =====================================================================================

    [Fact]
    public void NadeBonus_AccruesAndBanksABonusNade()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades", "1");
        facade.Cvars.Set("g_nades_bonus", "1");
        facade.Cvars.Set("g_nades_bonus_score_max", "100");
        facade.Cvars.Set("g_nades_bonus_max", "3");

        var player = NewPlayer();
        Assert.Equal(0, player.NadeBonus);

        // accrue a full score_max worth -> one bonus nade banked.
        NadeBonus.GiveBonus(player, 100f);
        Assert.Equal(1, player.NadeBonus);

        // RemoveBonus wipes it.
        NadeBonus.RemoveBonus(player);
        Assert.Equal(0, player.NadeBonus);
        Assert.Equal(0f, player.NadeBonusScore, 3);
    }

    [Fact]
    public void NadeBonus_TeamkillWipesAttackerBonus()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades", "1");
        facade.Cvars.Set("g_nades_bonus", "1");

        var attacker = NewPlayer(); attacker.Team = Teams.Red;
        var victim = NewPlayer(); victim.Team = Teams.Red; // same team
        attacker.NadeBonus = 2;

        NadeBonus.OnPlayerDies(attacker, victim);
        Assert.Equal(0, attacker.NadeBonus); // teamkill wipes the spree bonus
    }

    // =====================================================================================
    //  Nades — boom dispatch seam (the part-B interface)
    // =====================================================================================

    [Fact]
    public void BoomDispatch_RoutesToTheRegisteredHandler_AndDeletesTheNade()
    {
        var facade = Boot();

        // Register a stub boom for "normal". Part B's real NadeNormalBoom is now reflection-discovered, so
        // force the scan FIRST and then overwrite "normal" with the stub (explicit Register-after-scan wins) —
        // this keeps the seam test asserting dispatch routing without depending on the real boom's effect.
        NadeBoomRegistry.Reset();
        NadeBoomRegistry.EnsureScanned();
        bool boomed = false;
        NadeBoomRegistry.Register(new StubBoom("normal", _ => boomed = true));

        var nade = facade.Inner.EntityTable.Spawn();
        nade.ClassName = "nade";
        nade.NadeBonusType = NadeRegistry.Normal!.Id;
        nade.TakeDamage = DamageMode.Aim;

        NadeBoom.Detonate(nade);

        Assert.True(boomed, "the registered normal boom handler should run");
        Assert.True(nade.IsFreed, "the dispatcher deletes the nade after the boom");

        NadeBoomRegistry.Reset(); // don't leak the stub into other tests
    }

    [Fact]
    public void BoomDispatch_DestroyedNade_FallsBackToNormal()
    {
        var facade = Boot();

        NadeBoomRegistry.Reset();
        NadeBoomRegistry.EnsureScanned(); // discover the real booms, then overwrite with stubs (explicit wins)
        string? whichBoomed = null;
        NadeBoomRegistry.Register(new StubBoom("normal", _ => whichBoomed = "normal"));
        NadeBoomRegistry.Register(new StubBoom("napalm", _ => whichBoomed = "napalm"));

        // A napalm nade that was DESTROYED (takedamage cleared) detonates as NORMAL (QC nade_boom:121-126).
        var nade = facade.Inner.EntityTable.Spawn();
        nade.ClassName = "nade";
        nade.NadeBonusType = NadeRegistry.Napalm!.Id;
        nade.TakeDamage = DamageMode.No; // destroyed

        NadeBoom.Detonate(nade);
        Assert.Equal("normal", whichBoomed);

        NadeBoomRegistry.Reset();
    }

    // =====================================================================================
    //  Nades — the 11 per-type boom files (part B)
    // =====================================================================================

    [Fact]
    public void Booms_AllElevenTypesRegister()
    {
        Boot();
        NadeBoomRegistry.Reset();
        NadeBoomRegistry.EnsureScanned();

        // Every non-Null nade type has a boom handler discovered by reflection.
        foreach (var def in NadeRegistry.All)
        {
            if (def.Id == 0) continue; // Null sentinel has no boom
            Assert.True(NadeBoomRegistry.Get(def.NetName) is not null,
                $"nade type '{def.NetName}' should have a registered INadeBoom");
        }
        Assert.Equal(11, NadeBoomRegistry.Count);
    }

    [Fact]
    public void NormalBoom_DealsRadiusDamage()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades_nade_damage", "200");
        facade.Cvars.Set("g_nades_nade_edgedamage", "100");
        facade.Cvars.Set("g_nades_nade_radius", "300");
        facade.Cvars.Set("g_nades_nade_force", "0");

        var thrower = NewPlayer();
        var victim = WorldPlayer(facade, new Vector3(50, 0, 0)); // in the table so FindInRadius sees it
        victim.TakeDamage = DamageMode.Yes;

        var nade = SpawnNadeAt(facade, NadeRegistry.Normal!, thrower, new Vector3(0, 0, 0));
        float before = victim.Health;
        NadeBoom.Detonate(nade); // routes to NadeNormalBoom

        Assert.True(victim.Health < before, "a nearby player should take the normal-boom radius damage");
        Assert.True(nade.IsFreed, "the dispatcher deletes the nade after the boom");
    }

    [Fact]
    public void IceBoom_FreezesNearbyEnemy()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades_ice_freeze_time", "3");
        facade.Cvars.Set("g_nades_ice_radius", "300");
        facade.Cvars.Set("g_nades_ice_teamcheck", "0"); // freeze everyone (incl. simplest case)
        facade.GameClock.Time = 50f;

        var thrower = NewPlayer();
        var enemy = WorldPlayer(facade, new Vector3(40, 0, 0)); // in the table so the field's FindInRadius sees it
        enemy.TakeDamage = DamageMode.Yes;

        var nade = SpawnNadeAt(facade, NadeRegistry.Ice!, thrower, Vector3.Zero);
        NadeBoom.Detonate(nade); // spawns the freeze-field fountain

        // Drive the fountain think once (it freezes in-radius targets on each tick).
        Entity fountain = FindByClass(facade, "nade_ice_fountain");
        facade.GameClock.Time = 50.1f;
        fountain.Think!(fountain);

        var frozen = StatusEffectsCatalog.Frozen!;
        Assert.True(StatusEffectsCatalog.Has(enemy, frozen), "the ice field should freeze a nearby enemy");
    }

    [Fact]
    public void HealBoom_HealsTeammate_AndHarmsFoe()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades_heal_rate", "100");
        facade.Cvars.Set("g_nades_heal_radius", "300");
        facade.Cvars.Set("g_nades_heal_friend", "1");
        facade.Cvars.Set("g_nades_heal_foe", "-4");
        facade.GameClock.FrameTime = 0.1f;
        GameScores.Teamplay = true; // so distinct players can share a team

        try
        {
            var owner = NewPlayer(); owner.Team = Teams.Red;
            var ally = NewPlayer(); ally.Team = Teams.Red; ally.TakeDamage = DamageMode.Yes; ally.Health = 50f;
            var foe = NewPlayer(); foe.Team = Teams.Blue; foe.TakeDamage = DamageMode.Yes; foe.Health = 100f;
            ally.Origin = new Vector3(10, 0, 0);
            foe.Origin = new Vector3(10, 0, 0);

            var nade = SpawnNadeAt(facade, NadeRegistry.Heal!, owner, Vector3.Zero);
            NadeBoom.Detonate(nade);

            Entity orb = FindByClass(facade, "nade_orb");
            float allyBefore = ally.Health, foeBefore = foe.Health;
            orb.Touch!(orb, ally);
            orb.Touch!(orb, foe);

            Assert.True(ally.Health > allyBefore, "a teammate inside the heal orb gains health");
            Assert.True(foe.Health < foeBefore, "a foe inside the heal orb is harmed");
        }
        finally { GameScores.Teamplay = false; }
    }

    [Fact]
    public void AmmoBoom_GivesAmmoToFriend()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades_ammo_rate", "100");
        facade.Cvars.Set("g_nades_ammo_radius", "300");
        facade.Cvars.Set("g_pickup_rockets_max", "160");
        facade.GameClock.FrameTime = 0.1f;

        var owner = NewPlayer();
        owner.AmmoRockets = 0f;
        var nade = SpawnNadeAt(facade, NadeRegistry.Ammo!, owner, Vector3.Zero);
        NadeBoom.Detonate(nade);

        Entity orb = FindByClass(facade, "nade_orb");
        orb.Touch!(orb, owner); // the thrower is a "friend" (self) -> gains ammo

        Assert.True(owner.AmmoRockets > 0f, "the thrower inside the ammo orb gains ammo");
    }

    [Fact]
    public void EntrapBoom_SlowsEnemyVelocity_AndFlagsEntrapTime()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades", "1");
        facade.Cvars.Set("g_nades_entrap_strength", "0.01");
        facade.Cvars.Set("g_nades_entrap_speed", "0.5");
        facade.Cvars.Set("g_nades_entrap_radius", "500");
        facade.GameClock.Time = 10f;

        var owner = NewPlayer();
        var enemy = NewPlayer(); // FFA: distinct entity => DIFF_TEAM
        enemy.Velocity = new Vector3(1000, 0, 0);
        enemy.LastPushTime = 9.95f; // 0.05s since last push -> a real (non-zero, <=0.15) delta

        var nade = SpawnNadeAt(facade, NadeRegistry.Entrap!, owner, Vector3.Zero);
        NadeBoom.Detonate(nade);

        Entity orb = FindByClass(facade, "nade_orb");
        float speedBefore = enemy.Velocity.Length();
        orb.Touch!(orb, enemy);

        Assert.True(enemy.Velocity.Length() < speedBefore, "an enemy in the entrap orb is slowed");
        Assert.True(enemy.NadeEntrapTime > 10f, "the enemy is flagged with an entrap deadline");
    }

    [Fact]
    public void EntrapSpeed_PlayerPhysics_SlowsHighspeed()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades", "1");
        facade.Cvars.Set("g_nades_entrap_speed", "0.5");
        MutatorActivation.Apply(); // activate NadeEntrapSpeedMutator now that g_nades is on
        facade.GameClock.Time = 5f;
        facade.GameClock.FrameTime = 1f / 72f;

        var player = NewPlayer();
        player.Flags |= EntFlags.OnGround;
        player.NadeEntrapTime = 6f; // entrapped (deadline in the future)

        Movement.Move(player, new MovementInput { FrameTime = 1f / 72f });
        Assert.Equal(0.5f, player.SpeedMultiplier, 3); // highspeed halved by the entrap PlayerPhysics handler
    }

    [Fact]
    public void VeilBoom_HidesTeammate()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades_veil_time", "8");
        facade.Cvars.Set("g_nades_veil_radius", "250");
        facade.GameClock.Time = 20f;
        GameScores.Teamplay = true;

        try
        {
            var owner = NewPlayer(); owner.Team = Teams.Red;
            var ally = NewPlayer(); ally.Team = Teams.Red; ally.Alpha = 1f;

            var nade = SpawnNadeAt(facade, NadeRegistry.Veil!, owner, Vector3.Zero);
            NadeBoom.Detonate(nade);

            Entity orb = FindByClass(facade, "nade_orb");
            orb.Touch!(orb, ally);

            Assert.Equal(-1f, ally.Alpha, 3);            // teammate hidden
            Assert.Equal(1f, ally.NadeVeilPrevAlpha, 3); // previous alpha saved for restore
            Assert.True(ally.NadeVeilTime > 20f);        // veil deadline set
        }
        finally { GameScores.Teamplay = false; }
    }

    [Fact]
    public void DarknessBoom_BlindsNearbyEnemy()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades_darkness_time", "4");
        facade.Cvars.Set("g_nades_darkness_radius", "300");
        facade.Cvars.Set("g_nades_darkness_teamcheck", "0");
        facade.GameClock.Time = 30f;

        var owner = NewPlayer();
        var enemy = WorldPlayer(facade, new Vector3(30, 0, 0)); // in the table so the field's FindInRadius sees it
        enemy.TakeDamage = DamageMode.Yes;

        var nade = SpawnNadeAt(facade, NadeRegistry.Darkness!, owner, Vector3.Zero);
        NadeBoom.Detonate(nade);

        Entity fountain = FindByClass(facade, "nade_darkness_fountain");
        facade.GameClock.Time = 30.1f;
        fountain.Think!(fountain);

        Assert.True(enemy.NadeDarknessTime > 30f, "a nearby enemy gets a darkness (blind) deadline");
    }

    [Fact]
    public void SpawnBoom_SetsSpawnLoc_AndDestroyDamageHurtsOwner()
    {
        var facade = Boot();
        facade.Cvars.Set("g_nades_spawn_count", "3");
        facade.Cvars.Set("g_nades_spawn_destroy_damage", "25");

        var owner = NewPlayer();
        owner.TakeDamage = DamageMode.Yes;
        var nade = SpawnNadeAt(facade, NadeRegistry.Spawn!, owner, new Vector3(100, 0, 0));

        // The boom plants a spawn-loc marker on the owner.
        var spawnBoom = (INadeBoom)NadeBoomRegistry.Get("spawn")!;
        spawnBoom.Boom(nade);
        Assert.NotNull(owner.NadeSpawnLoc);
        Assert.Equal(3, owner.NadeSpawnLoc!.NadeSpawnCount);
        Assert.Equal(new Vector3(100, 0, 0), owner.NadeSpawnLoc.Origin);

        // DestroyDamage (shot down) hurts the owner and returns false (normal boom still runs).
        var dd = (INadeDestroyDamage)spawnBoom;
        float before = owner.Health;
        var attacker = NewPlayer();
        bool consumed = dd.DestroyDamage(nade, attacker);
        Assert.False(consumed);
        Assert.True(owner.Health < before, "shooting down a spawn nade hurts its owner");
    }

    [Fact]
    public void TranslocateBoom_TeleportsOwnerToDetonation()
    {
        var facade = Boot();
        facade.GameClock.Time = 5f;

        var owner = NewPlayer();
        owner.Origin = new Vector3(0, 0, 0);
        owner.Angles = Vector3.Zero;

        var detonatePos = new Vector3(500, 0, 32);
        var nade = SpawnNadeAt(facade, NadeRegistry.Translocate!, owner, detonatePos);
        NadeBoom.Detonate(nade); // routes to NadeTranslocateBoom

        // The owner is relocated near the detonation point (X/Y match; Z adjusted by the floor trace).
        Assert.True(MathF.Abs(owner.Origin.X - 500f) < 1f, $"owner X should be the detonation X (got {owner.Origin.X})");
        Assert.True(MathF.Abs(owner.Origin.Y - 0f) < 1f, $"owner Y should be the detonation Y (got {owner.Origin.Y})");
    }

    [Fact]
    public void MonsterBoom_NoOpsWhenMonstersDisabled()
    {
        var facade = Boot();
        facade.Cvars.Set("g_monsters", "0"); // monsters off -> the boom returns without spawning

        var owner = NewPlayer();
        var nade = SpawnNadeAt(facade, NadeRegistry.Monster!, owner, Vector3.Zero);
        nade.PokenadeType = "zombie";

        var monsterBoom = (INadeBoom)NadeBoomRegistry.Get("pokenade")!;
        int before = CountByClassPrefix(facade, "monster");
        monsterBoom.Boom(nade);
        int after = CountByClassPrefix(facade, "monster");

        Assert.Equal(before, after); // no monster spawned while g_monsters is 0
    }

    // ---- helpers ----

    /// <summary>
    /// Spawn a PLAYER entity registered IN the engine entity table (so <c>FindInRadius</c> finds it — a bare
    /// <c>new Entity</c> from <see cref="NewPlayer"/> is not in the table). Used by the radius-effect boom tests.
    /// </summary>
    private static Entity WorldPlayer(TestFacade facade, Vector3 origin, float team = 0f)
    {
        Entity e = facade.Inner.Entities.Spawn();
        e.ClassName = "player";
        e.Flags = EntFlags.Client;
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.Health = 100f;
        e.Alpha = 1f;
        e.Team = team;
        facade.Inner.Entities.SetOrigin(e, origin);
        return e;
    }

    /// <summary>Spawn a thrown-nade entity of <paramref name="def"/> at <paramref name="at"/>, owned by <paramref name="owner"/>.</summary>
    private static Entity SpawnNadeAt(TestFacade facade, NadeDef def, Entity owner, Vector3 at)
    {
        NadeBoomRegistry.EnsureScanned();
        Entity nade = facade.Inner.EntityTable.Spawn();
        nade.ClassName = "nade";
        nade.Owner = owner;
        nade.Team = owner.Team;
        nade.NadeBonusType = def.Id;
        nade.TakeDamage = DamageMode.Aim;
        facade.Inner.Entities.SetOrigin(nade, at);
        return nade;
    }

    private static Entity FindByClass(TestFacade facade, string className)
    {
        foreach (Entity e in facade.Inner.EntityTable.All)
            if (!e.IsFreed && e.ClassName == className) return e;
        Assert.Fail($"no live entity with classname '{className}' found");
        return null!;
    }

    private static int CountByClassPrefix(TestFacade facade, string prefix)
    {
        int n = 0;
        foreach (Entity e in facade.Inner.EntityTable.All)
            if (!e.IsFreed && e.ClassName.StartsWith(prefix, System.StringComparison.Ordinal)) n++;
        return n;
    }

    private static Entity FindThrown(TestFacade facade, int skip = 0)
    {
        int seen = 0;
        foreach (Entity e in facade.Inner.EntityTable.All)
        {
            if (e.IsFreed || e.ClassName != "nade") continue;
            if (seen++ == skip) return e;
        }
        Assert.Fail("no thrown nade found");
        return null!; // unreachable
    }

    private sealed class StubBoom : INadeBoom
    {
        private readonly System.Action<Entity> _boom;
        public StubBoom(string netName, System.Action<Entity> boom) { NadeNetName = netName; _boom = boom; }
        public string NadeNetName { get; }
        public void Boom(Entity nade) => _boom(nade);
    }
}

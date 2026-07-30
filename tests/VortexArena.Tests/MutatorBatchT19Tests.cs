using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T19 (stage 1): the six shared-file-touching mutators (rocketminsta, rocketflying, stale_move_negation,
/// random_gravity, globalforces, vampirehook) and the hook SEAMS they need. Each mutator self-registers via
/// <c>[Mutator]</c> and subscribes to a hook chain when its <c>g_*</c> cvar is set; these tests boot the
/// registries, flip the cvar, run <see cref="MutatorActivation.Apply"/>, and exercise the ported behavior
/// through the real damage / spawn / projectile pipelines.
///
/// Runs in the GlobalState collection — it mutates the process-global registries + Api.Services + hook chains
/// (xUnit parallelism is disabled assembly-wide; see the TestParallelization memo).
/// </summary>
[Collection("GlobalState")]
public class MutatorBatchT19Tests : IDisposable
{
    // Unsubscribe every mutator hook after each test so an enabled mutator (e.g. rocketminsta's
    // Damage_Calculate handler) can't perturb a later test that runs the real damage pipeline. Mirrors QC
    // tearing down the hook chains on a progs reload (GameRegistries.Reset does the same at the next Boot).
    public void Dispose() => MutatorActivation.DeactivateAll();

    /// <summary>A settable-clock facade so the time-gated mutators (random_gravity, vampirehook) are testable.</summary>
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

    // Boot a clean world: fresh registries + damage system + activated mutators. Cvars are set BEFORE Apply so
    // each mutator's IsEnabled + Hook() see them.
    private static TestFacade Boot(params (string name, string value)[] cvars)
    {
        var facade = new TestFacade();
        Api.Services = facade;
        VehicleCommon.GameStopped = false;      // the match is live (a prior test may have left this set)
        // Reset the host-wired mutator seams a prior full-GameWorld test may have left set (these are process
        // statics): RandomGravity reads RoundHandler.RoundNotStartedProvider (its pre-round gate — must be inert
        // here = no round mode) and MutatorActivation.SettempCvarHandler (its ONADD sv_gravity settemp).
        // VampireHook reads StartItem.GameStartTimeProvider for its time<game_starttime pre-match gate — null
        // here = game_starttime 0, so the drain isn't blocked at the test clock.
        RoundHandler.RoundNotStartedProvider = null;
        MutatorActivation.SettempCvarHandler = null;
        StartItem.GameStartTimeProvider = null;
        GameRegistries.Reset();                 // drops any mutator hooks from a prior test
        StatusEffectsCatalog.RegisterAll();
        foreach (var (n, v) in cvars) facade.Cvars.Set(n, v);
        GameRegistries.Bootstrap();             // discovers the [Mutator] classes
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();              // subscribe the enabled mutators' hooks
        return facade;
    }

    // A standalone player (not in the entity table) — fine for tests that pass the entity directly into the
    // damage pipeline (rocketminsta, stale_move_negation).
    private static Entity NewPlayer(Vector3 origin = default) => Configure(new Entity(), origin);

    // A player spawned THROUGH the entity table, so FindByClass("player") (the FOREACH_CLIENT idiom used by
    // globalforces / vampirehook) returns it.
    private static Entity SpawnPlayer(TestFacade f, Vector3 origin = default)
    {
        Entity e = f.Entities.Spawn();
        Configure(e, origin);
        f.Entities.SetOrigin(e, origin); // link bounds into the table
        return e;
    }

    private static Entity Configure(Entity e, Vector3 origin)
    {
        e.ClassName = "player";
        e.Flags = EntFlags.Client;
        e.Origin = origin;
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.Health = 100;
        e.MaxHealth = 100;
        e.TakeDamage = DamageMode.Yes;
        e.DamageForceScale = 1f; // players normally have damageforcescale 1 (a fresh Entity defaults to 0)
        return e;
    }

    // ----------------------------------------------------------------------------------------------
    //  Registration — all six self-register and bump the count
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void AllSix_Register_ByName()
    {
        Boot();
        Assert.NotNull(Mutators.ByName("rocketminsta"));
        Assert.NotNull(Mutators.ByName("rocketflying"));
        Assert.NotNull(Mutators.ByName("stale_move_negation"));
        Assert.NotNull(Mutators.ByName("random_gravity"));
        Assert.NotNull(Mutators.ByName("globalforces"));
        Assert.NotNull(Mutators.ByName("vampirehook"));
        Assert.True(Mutators.Count >= 6, $"expected >=6 mutators, got {Mutators.Count}");
    }

    // ----------------------------------------------------------------------------------------------
    //  rocketminsta
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void RocketMinsta_ZeroesSelfDevastatorDamage()
    {
        Boot(("g_instagib", "1"), ("g_rm", "1"));

        var p = NewPlayer();
        float before = p.Health;
        // self-Devastator hit: rm zeroes the damage in Damage_Calculate so rocket-jumping never hurts.
        Combat.Damage(p, p, p, 50f, DeathTypes.FromWeapon("devastator"), p.Origin, Vector3.Zero);
        Assert.Equal(before, p.Health); // no self damage
    }

    [Fact]
    public void RocketMinsta_Inert_WhenRmSubmodeOff()
    {
        // instagib on but g_rm off → the rm hooks early-out, self-Devastator damage applies normally.
        Boot(("g_instagib", "1"), ("g_rm", "0"));

        var p = NewPlayer();
        float before = p.Health;
        Combat.Damage(p, p, p, 50f, DeathTypes.FromWeapon("devastator"), p.Origin, Vector3.Zero);
        Assert.True(p.Health < before, "with g_rm 0, self-Devastator damage should apply (rm inert)");
    }

    [Fact]
    public void RocketMinsta_ForcesGib_OnDevastatorKill()
    {
        Boot(("g_instagib", "1"), ("g_rm", "1"));

        // PlayerDies bumps the kill damage to 1000 so a vaporizer death always gibs. Verify via the hook directly
        // (the damage value the chain reads back is what the corpse-gib branch uses for `excess`).
        var attacker = NewPlayer(new Vector3(100, 0, 0));
        var victim = NewPlayer();
        var args = new MutatorHooks.PlayerDiesArgs(null, attacker, victim, DeathTypes.FromWeapon("devastator"), 40f);
        MutatorHooks.PlayerDies.Call(ref args);
        Assert.Equal(1000f, args.Damage);
    }

    // ----------------------------------------------------------------------------------------------
    //  stale_move_negation
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void StaleMoveNegation_MultiplierDecreasesWithWeight()
    {
        Boot(("g_smneg", "1"));
        var m = (StaleMoveNegationMutator)Mutators.ByName("stale_move_negation")!;

        // smneg_multiplier is monotonically decreasing in the accumulated weight (more repeated use → weaker).
        float f0 = m.Multiplier(0f);
        float f50 = m.Multiplier(50f);
        float f200 = m.Multiplier(200f);
        Assert.True(f0 > f50, $"multiplier should drop as weight rises: f0 {f0}, f50 {f50}");
        Assert.True(f50 > f200, $"multiplier should keep dropping: f50 {f50}, f200 {f200}");
    }

    [Fact]
    public void StaleMoveNegation_RepeatedSameWeapon_ScalesDamageDown()
    {
        Boot(("g_smneg", "1"));

        var attacker = NewPlayer(new Vector3(100, 0, 0));
        var target = NewPlayer();
        string dt = DeathTypes.FromWeapon("vortex");

        // First hit and a later hit with the SAME weapon: the weapon's weight accrues, so the second hit deals
        // strictly less than the first (the stale-move penalty).
        target.Health = 1000f;
        float h0 = target.Health;
        Combat.Damage(target, null, attacker, 80f, dt, target.Origin, Vector3.Zero);
        float firstHit = h0 - target.Health;

        // Several more hits to accumulate weight, then measure one more.
        for (int i = 0; i < 5; i++)
            Combat.Damage(target, null, attacker, 80f, dt, target.Origin, Vector3.Zero);
        float beforeLast = target.Health;
        Combat.Damage(target, null, attacker, 80f, dt, target.Origin, Vector3.Zero);
        float lastHit = beforeLast - target.Health;

        Assert.True(lastHit < firstHit, $"repeated same-weapon damage should decay: first {firstHit}, last {lastHit}");
    }

    // ----------------------------------------------------------------------------------------------
    //  random_gravity
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void RandomGravity_SetsSvGravityWithinBounds()
    {
        var f = Boot(
            ("g_random_gravity", "1"),
            ("g_random_gravity_min", "100"),
            ("g_random_gravity_max", "800"),
            ("g_random_gravity_positive", "800"),
            ("g_random_gravity_negative", "800"),
            ("g_random_gravity_negative_chance", "0.5"),
            ("g_random_gravity_delay", "5"));

        // Roll several frames (advancing past the delay each time) and assert every rolled gravity is in bounds.
        for (int i = 0; i < 8; i++)
        {
            f.GameClock.Time = i * 6f; // > delay (5s) apart so each frame re-rolls
            MutatorHooks.FireStartFrame(f.GameClock.Time);
            float g = f.Cvars.GetFloat("sv_gravity");
            Assert.InRange(g, 100f, 800f);
        }
    }

    [Fact]
    public void RandomGravity_RespectsDelay()
    {
        var f = Boot(
            ("g_random_gravity", "1"),
            ("g_random_gravity_min", "100"),
            ("g_random_gravity_max", "800"),
            ("g_random_gravity_positive", "800"),
            // negative_chance 0 + negative unset → the first-branch formula degenerates to bound(100, random(), 800)
            // = 100 (random() < 1), a deterministic in-bounds first roll; we only care that a roll happened.
            ("g_random_gravity_negative_chance", "0"),
            ("g_random_gravity_delay", "5"));

        f.GameClock.Time = 10f;
        MutatorHooks.FireStartFrame(10f);
        float first = f.Cvars.GetFloat("sv_gravity");

        // Within the delay window: no re-roll (gravity unchanged).
        f.Cvars.Set("sv_gravity", "12345");
        f.GameClock.Time = 12f; // < first roll (10) + delay (5)
        MutatorHooks.FireStartFrame(12f);
        Assert.Equal(12345f, f.Cvars.GetFloat("sv_gravity"));
        _ = first;
    }

    // ----------------------------------------------------------------------------------------------
    //  globalforces
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void GlobalForces_PushesNearbyPlayer_OnDamage()
    {
        var f = Boot(("g_globalforces", "1"), ("g_globalforces_range", "1000"));

        var attacker = SpawnPlayer(f, new Vector3(500, 0, 0));
        var target = SpawnPlayer(f, new Vector3(0, 0, 0));
        var bystander = SpawnPlayer(f, new Vector3(100, 0, 0)); // within range of the target

        Assert.Equal(Vector3.Zero, bystander.Velocity);
        // a hit on the target with a non-zero force should shove the bystander too.
        Combat.Damage(target, null, attacker, 30f, DeathTypes.FromWeapon("devastator"), target.Origin, new Vector3(0, 0, 600));
        Assert.NotEqual(Vector3.Zero, bystander.Velocity);
    }

    [Fact]
    public void GlobalForces_SkipsOutOfRangePlayer()
    {
        var f = Boot(("g_globalforces", "1"), ("g_globalforces_range", "200"));

        var attacker = SpawnPlayer(f, new Vector3(500, 0, 0));
        var target = SpawnPlayer(f, new Vector3(0, 0, 0));
        var faraway = SpawnPlayer(f, new Vector3(5000, 0, 0)); // way outside the 200u range

        Combat.Damage(target, null, attacker, 30f, DeathTypes.FromWeapon("devastator"), target.Origin, new Vector3(0, 0, 600));
        Assert.Equal(Vector3.Zero, faraway.Velocity); // untouched: out of range
    }

    // ----------------------------------------------------------------------------------------------
    //  rocketflying
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void RocketFlying_ClearsDetonateDelay_OnRocketProjectile()
    {
        var f = Boot(("g_rocket_flying", "1"));
        f.GameClock.Time = 7.5f;

        var owner = NewPlayer();
        var rocket = new Entity { ClassName = "rocket", ProjectileDetonateTime = 999f };
        // EditProjectile is the seam rocketflying subscribes to (the same one Devastator/Hook fire at launch).
        var ep = new MutatorHooks.EditProjectileArgs(owner, rocket);
        MutatorHooks.EditProjectile.Call(ref ep);

        Assert.Equal(7.5f, rocket.ProjectileDetonateTime); // QC: proj.spawnshieldtime = time
    }

    [Fact]
    public void RocketFlying_IgnoresNonRocketProjectile()
    {
        var f = Boot(("g_rocket_flying", "1"));
        f.GameClock.Time = 7.5f;

        var owner = NewPlayer();
        var other = new Entity { ClassName = "grapplinghook", ProjectileDetonateTime = 999f };
        var ep = new MutatorHooks.EditProjectileArgs(owner, other);
        MutatorHooks.EditProjectile.Call(ref ep);

        Assert.Equal(999f, other.ProjectileDetonateTime); // untouched: not a rocket/mine
    }

    // ----------------------------------------------------------------------------------------------
    //  vampirehook
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void VampireHook_Inert_WhenNoHookedPlayer()
    {
        // The port's grapple latches geometry (no .aiment), so the drain is a documented partial: with no hooked
        // player the handler is a no-op. Drive GrappleHookThink with a hook that has no aiment and assert nothing
        // changes — proving the guards hold (the seam is wired, the body is inert until the tarzan mechanic lands).
        var f = Boot(("g_vampirehook", "1"), ("g_vampirehook_damage", "10"), ("g_vampirehook_health_steal", "10"));
        f.GameClock.Time = 5f;

        var owner = NewPlayer(new Vector3(0, 0, 0));
        var hook = new Entity { ClassName = "grapplinghook", Owner = owner };
        float ownerHealthBefore = owner.Health;

        var gh = new MutatorHooks.GrappleHookThinkArgs(hook);
        MutatorHooks.GrappleHookThink.Call(ref gh);

        Assert.Equal(ownerHealthBefore, owner.Health); // no aiment → no drain/heal
    }

    [Fact]
    public void VampireHook_DrainsEnemy_WhenHookedPlayerPresent()
    {
        // When a hooked PLAYER is present (manually wired here to model the tarzan variant the port doesn't yet
        // spawn), the enemy is drained and the owner heals — the faithful ported body.
        var f = Boot(
            ("g_vampirehook", "1"),
            ("g_vampirehook_damage", "10"),
            ("g_vampirehook_damagerate", "0.1"),
            ("g_vampirehook_health_steal", "7"),
            ("g_pickup_healthsmall_max", "5")); // QC Heal(...) caps at the small-health max
        f.GameClock.Time = 5f;

        var owner = SpawnPlayer(f, new Vector3(0, 0, 0));
        owner.Health = 1f; // below the small-health cap, so the steal heals (QC: Heal caps the post-give total)
        var victim = SpawnPlayer(f, new Vector3(100, 0, 0));
        owner.Team = Teams.Red; victim.Team = Teams.Blue; // enemies (DIFF_TEAM)

        var hook = new Entity { ClassName = "grapplinghook", Owner = owner, Aiment = victim, Origin = victim.Origin };
        float victimHpBefore = victim.Health;
        float ownerHpBefore = owner.Health;

        var gh = new MutatorHooks.GrappleHookThinkArgs(hook);
        MutatorHooks.GrappleHookThink.Call(ref gh);

        Assert.True(victim.Health < victimHpBefore, $"enemy should be drained: {victimHpBefore} -> {victim.Health}");
        // QC: Heal(owner, ..., 7, healthsmall_max=5) → owner heals from 1 up to the 5 cap (not 1+7).
        Assert.True(owner.Health > ownerHpBefore, $"owner should heal: {ownerHpBefore} -> {owner.Health}");
        Assert.True(owner.Health <= 5f, $"heal must respect the small-health cap (5), got {owner.Health}");
    }
}

/// <summary>
/// T19 (stage 2): the six new-file-only mutators (weaponarena_random, new_toys, spawn_unique,
/// spawn_near_teammate, random_items, physical_items) registering against the stage-1 hook SEAMS. Each mutator
/// self-registers via <c>[Mutator]</c> and subscribes to a hook chain when its <c>g_*</c> cvar is set; these
/// tests boot the registries, flip the cvar, run <see cref="MutatorActivation.Apply"/>, and exercise the ported
/// behavior through the real spawn-score / SetStartItems / GiveFragsForKill / PlayerDies pipelines.
///
/// Runs in the GlobalState collection — it mutates the process-global registries + Api.Services + hook chains
/// (xUnit parallelism is disabled assembly-wide; see the TestParallelization memo).
/// </summary>
[Collection("GlobalState")]
public class MutatorBatchT19StageTwoTests : IDisposable
{
    public void Dispose()
    {
        MutatorActivation.DeactivateAll();
        GameScores.Teamplay = false; // a teamplay-flipping test must not leak into the next
    }

    /// <summary>A settable-clock facade so the time-gated mutators are testable.</summary>
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
        GameScores.Teamplay = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        foreach (var (n, v) in cvars) facade.Cvars.Set(n, v);
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();
        return facade;
    }

    private static Entity SpawnPlayer(TestFacade f, Vector3 origin = default, int team = 0)
    {
        Entity e = f.Entities.Spawn();
        e.ClassName = "player";
        e.Flags = EntFlags.Client;
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.Health = 100; e.MaxHealth = 100;
        e.TakeDamage = DamageMode.Yes;
        e.Team = team;
        e.Origin = origin;
        f.Entities.SetOrigin(e, origin);
        return e;
    }

    private static Entity SpawnSpot(TestFacade f, Vector3 origin, int team = 0)
    {
        Entity e = f.Entities.Spawn();
        e.ClassName = "info_player_deathmatch";
        e.Team = team;
        e.Origin = origin;
        f.Entities.SetOrigin(e, origin);
        return e;
    }

    // ----------------------------------------------------------------------------------------------
    //  Registration
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void AllSix_Register_ByName()
    {
        Boot();
        Assert.NotNull(Mutators.ByName("weaponarena_random"));
        Assert.NotNull(Mutators.ByName("new_toys"));
        Assert.NotNull(Mutators.ByName("spawn_unique"));
        Assert.NotNull(Mutators.ByName("spawn_near_teammate"));
        Assert.NotNull(Mutators.ByName("random_items"));
        Assert.NotNull(Mutators.ByName("physical_items"));
    }

    // ----------------------------------------------------------------------------------------------
    //  new_toys
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void NewToys_SetStartItems_ReplacesCoreWithNewToy()
    {
        // autoreplace ALWAYS: vortex→rifle, hagar→seeker, machinegun→hlac, devastator→minelayer.
        Boot(("g_new_toys", "1"), ("g_new_toys_autoreplace", "1"));

        var loadout = new StartLoadout();
        loadout.SetWeapons("vortex", "shotgun");
        var args = new MutatorHooks.SetStartItemsArgs(loadout);
        MutatorHooks.SetStartItems.Call(ref args);

        Assert.DoesNotContain("vortex", args.Loadout.Weapons);  // replaced
        Assert.Contains("rifle", args.Loadout.Weapons);          // by its new-toy variant
        Assert.Contains("shotgun", args.Loadout.Weapons);        // untouched (no replacement)
    }

    [Fact]
    public void NewToys_AutoReplaceNever_KeepsCoreWeapon()
    {
        Boot(("g_new_toys", "1"), ("g_new_toys_autoreplace", "0")); // NEVER

        var loadout = new StartLoadout();
        loadout.SetWeapons("vortex");
        var args = new MutatorHooks.SetStartItemsArgs(loadout);
        MutatorHooks.SetStartItems.Call(ref args);

        Assert.Contains("vortex", args.Loadout.Weapons); // unchanged
        Assert.DoesNotContain("rifle", args.Loadout.Weapons);
    }

    [Fact]
    public void NewToys_Disabled_WhenInstagib()
    {
        // QC: !MUTATOR_IS_ENABLED(instagib) — new_toys must not activate alongside instagib.
        Boot(("g_new_toys", "1"), ("g_instagib", "1"));
        var nt = Mutators.ByName("new_toys")!;
        Assert.False(nt.IsEnabled);
    }

    [Fact]
    public void NewToys_GetReplacement_MatchesQcMapping()
    {
        Boot();
        Assert.Equal("seeker", NewToysMutator.GetReplacement("hagar", NewToysMutator.AutoReplaceAlways));
        Assert.Equal("minelayer", NewToysMutator.GetReplacement("devastator", NewToysMutator.AutoReplaceAlways));
        Assert.Equal("hlac", NewToysMutator.GetReplacement("machinegun", NewToysMutator.AutoReplaceAlways));
        Assert.Equal("rifle", NewToysMutator.GetReplacement("vortex", NewToysMutator.AutoReplaceAlways));
        // No mapping → the weapon itself; NEVER → the weapon itself even when a mapping exists.
        Assert.Equal("shotgun", NewToysMutator.GetReplacement("shotgun", NewToysMutator.AutoReplaceAlways));
        Assert.Equal("vortex", NewToysMutator.GetReplacement("vortex", NewToysMutator.AutoReplaceNever));
        Assert.True(NewToysMutator.IsNewToy("seeker"));
        Assert.False(NewToysMutator.IsNewToy("vortex"));
    }

    // ----------------------------------------------------------------------------------------------
    //  spawn_unique
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void SpawnUnique_DropsScore_OnLastSpawnPoint()
    {
        var f = Boot(("g_spawn_unique", "1"));

        var player = SpawnPlayer(f, Vector3.Zero);
        var spotA = SpawnSpot(f, new Vector3(100, 0, 0));

        // Record spotA as the player's last spawn point (PlayerSpawn hook).
        var ps = new MutatorHooks.PlayerSpawnArgs(player, spotA);
        MutatorHooks.PlayerSpawn.Call(ref ps);

        // Scoring spotA again should demote its priority to 0.1; a different spot is untouched.
        var ssA = new MutatorHooks.SpawnScoreArgs(player, spotA, priority: 10f, weight: 500f);
        MutatorHooks.SpawnScore.Call(ref ssA);
        Assert.Equal(0.1f, ssA.Priority);

        var spotB = SpawnSpot(f, new Vector3(200, 0, 0));
        var ssB = new MutatorHooks.SpawnScoreArgs(player, spotB, priority: 10f, weight: 500f);
        MutatorHooks.SpawnScore.Call(ref ssB);
        Assert.Equal(10f, ssB.Priority); // not the last point → unchanged
    }

    // ----------------------------------------------------------------------------------------------
    //  spawn_near_teammate
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void SpawnNearTeammate_RaisesPriority_NearLiveTeammate()
    {
        var f = Boot(
            ("g_spawn_near_teammate", "1"),
            ("g_spawn_near_teammate_distance", "1000"),
            ("g_spawn_near_teammate_ignore_spawnpoint", "0"));
        GameScores.Teamplay = true; // QC: the hook early-outs unless teamplay

        var player = SpawnPlayer(f, Vector3.Zero, team: Teams.Red);
        // A live teammate near the spot but not on top of it (QC requires 48u < dist < distance(1000u)):
        // spot at (300,0,0), teammate at (500,0,0) → 200u away.
        SpawnPlayer(f, new Vector3(500, 0, 0), team: Teams.Red);
        var spot = SpawnSpot(f, new Vector3(300, 0, 0), team: Teams.Red);

        var ss = new MutatorHooks.SpawnScoreArgs(player, spot, priority: 10f, weight: 500f);
        MutatorHooks.SpawnScore.Call(ref ss);

        // SPAWN_PRIO_NEAR_TEAMMATE_FOUND (200) is added on top of the base 10.
        Assert.Equal(10f + SpawnNearTeammateMutator.PrioNearTeammateFound, ss.Priority);
    }

    [Fact]
    public void SpawnNearTeammate_Inert_WhenNotTeamplay()
    {
        var f = Boot(("g_spawn_near_teammate", "1"), ("g_spawn_near_teammate_distance", "1000"));
        GameScores.Teamplay = false; // FFA → the hook is a no-op

        var player = SpawnPlayer(f, Vector3.Zero, team: Teams.Red);
        SpawnPlayer(f, new Vector3(300, 0, 0), team: Teams.Red);
        var spot = SpawnSpot(f, new Vector3(300, 0, 0), team: Teams.Red);

        var ss = new MutatorHooks.SpawnScoreArgs(player, spot, priority: 10f, weight: 500f);
        MutatorHooks.SpawnScore.Call(ref ss);
        Assert.Equal(10f, ss.Priority); // unchanged
    }

    // ----------------------------------------------------------------------------------------------
    //  weaponarena_random
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void WeaponArenaRandom_PlayerSpawn_GivesRandomSubset()
    {
        var f = Boot(("g_weaponarena", "1"), ("g_weaponarena_random", "3"));

        var player = SpawnPlayer(f, Vector3.Zero);
        // Seed a wide owned set (the arena set the random pick draws from).
        foreach (string n in new[] { "shotgun", "machinegun", "vortex", "crylink", "hagar", "electro", "mortar" })
            if (Weapons.ByName(n) is { } w) player.OwnedWeaponSet.Add(w);
        int before = player.OwnedWeaponSet.CountSet;
        Assert.True(before >= 3);

        var ps = new MutatorHooks.PlayerSpawnArgs(player, null);
        MutatorHooks.PlayerSpawn.Call(ref ps);

        // After the spawn hook the player should hold exactly g_weaponarena_random (3) weapons, all from the pool.
        Assert.Equal(3, player.OwnedWeaponSet.CountSet);
    }

    [Fact]
    public void WeaponArenaRandom_WithBlaster_AlwaysKeepsBlaster()
    {
        var f = Boot(("g_weaponarena", "1"), ("g_weaponarena_random", "2"), ("g_weaponarena_random_with_blaster", "1"));

        var player = SpawnPlayer(f, Vector3.Zero);
        foreach (string n in new[] { "shotgun", "machinegun", "vortex", "crylink", "hagar" })
            if (Weapons.ByName(n) is { } w) player.OwnedWeaponSet.Add(w);

        var ps = new MutatorHooks.PlayerSpawnArgs(player, null);
        MutatorHooks.PlayerSpawn.Call(ref ps);

        Weapon? blaster = Weapons.ByName("blaster");
        Assert.NotNull(blaster);
        Assert.True(player.OwnedWeaponSet.Has(blaster!), "with_blaster must keep the blaster on top of the random set");
        // 2 random + the blaster = 3.
        Assert.Equal(3, player.OwnedWeaponSet.CountSet);
    }

    [Fact]
    public void WeaponArenaRandom_GiveFragsForKill_SwapsCulpritWeapon()
    {
        var f = Boot(("g_weaponarena", "1"), ("g_weaponarena_random", "2"));

        var attacker = SpawnPlayer(f, new Vector3(100, 0, 0));
        var victim = SpawnPlayer(f, Vector3.Zero);

        // Attacker holds {vortex, shotgun}; the culprit (deathtype) is the vortex. After the frag, the culprit is
        // swapped out (dropped), then the attacker is switched to their best remaining weapon.
        Weapon? vortex = Weapons.ByName("vortex");
        Weapon? shotgun = Weapons.ByName("shotgun");
        Assert.NotNull(vortex); Assert.NotNull(shotgun);
        attacker.OwnedWeaponSet.Add(vortex!);
        attacker.OwnedWeaponSet.Add(shotgun!);

        var gfk = new MutatorHooks.GiveFragsForKillArgs(attacker, victim, fragScore: 1f,
            DeathTypes.FromWeapon("vortex"), weaponEntity: null);
        MutatorHooks.GiveFragsForKill.Call(ref gfk);

        Assert.False(attacker.OwnedWeaponSet.Has(vortex!), "the culprit weapon (vortex) should be swapped out");
    }

    [Fact]
    public void WeaponArenaRandom_RandomWeapons_PicksRequestedCount_WithoutReplacement()
    {
        Boot(("g_weaponarena", "1"), ("g_weaponarena_random", "3"));

        var pool = new WepSet();
        foreach (string n in new[] { "shotgun", "machinegun", "vortex", "crylink", "hagar" })
            if (Weapons.ByName(n) is { } w) pool.Add(w);

        WepSet picked = WeaponArenaRandomMutator.RandomWeapons(pool, 3);
        Assert.Equal(3, picked.CountSet); // exactly 3 distinct weapons drawn from the pool

        // Asking for more than the pool size returns the whole pool (without replacement → can't exceed it).
        var smallPool = new WepSet();
        if (Weapons.ByName("shotgun") is { } sg) smallPool.Add(sg);
        WepSet all = WeaponArenaRandomMutator.RandomWeapons(smallPool, 5);
        Assert.Equal(1, all.CountSet);
    }

    // ----------------------------------------------------------------------------------------------
    //  random_items
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void RandomItems_PlayerDies_DropsLoot()
    {
        var f = Boot(
            ("g_random_loot", "1"),
            ("g_random_loot_min", "2"),
            ("g_random_loot_max", "0"),     // floor(2 + random()*0) = exactly 2 loot items
            ("g_random_loot_time", "20"),
            ("g_random_loot_spread", "100"),
            ("g_random_loot_weapon_probability", "1"),     // type pick → weapon
            ("g_random_loot_weapon_shotgun_probability", "1"));   // a concrete weapon to resolve (QC m_canonical_spawnfunc = "weapon_shotgun")

        var victim = SpawnPlayer(f, new Vector3(0, 0, 0));
        int before = CountItems(f);

        var pd = new MutatorHooks.PlayerDiesArgs(null, null, victim, DeathTypes.FromWeapon("blaster"), 100f);
        MutatorHooks.PlayerDies.Call(ref pd);

        int after = CountItems(f);
        Assert.Equal(before + 2, after); // exactly two loot items spawned
    }

    [Fact]
    public void RandomItems_LootDisabled_DropsNothing()
    {
        var f = Boot(("g_random_items", "1")); // items on, loot OFF

        var victim = SpawnPlayer(f, Vector3.Zero);
        int before = CountItems(f);
        var pd = new MutatorHooks.PlayerDiesArgs(null, null, victim, DeathTypes.FromWeapon("blaster"), 100f);
        MutatorHooks.PlayerDies.Call(ref pd);
        Assert.Equal(before, CountItems(f)); // no loot without g_random_loot
    }

    private static int CountItems(TestFacade f)
    {
        // No "enumerate all entities" API; FindInRadius over a world-sized sphere about the corpse catches the
        // loot (spawned near the victim origin), then filter to item entities.
        int n = 0;
        foreach (Entity e in f.Entities.FindInRadius(Vector3.Zero, 1_000_000f))
            if (!e.IsFreed && (e.Flags & EntFlags.Item) != 0) n++;
        return n;
    }

    // ----------------------------------------------------------------------------------------------
    //  physical_items
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void PhysicalItems_Disabled_NoPhysicsEngine()
    {
        // DOUBLE-BLOCKED: no ODE rigid-body engine wired → the mutator self-disables (QC's "revert to old items").
        Boot(("g_physical_items", "1"));
        var pi = Mutators.ByName("physical_items")!;
        Assert.False(pi.IsEnabled); // never activates without a physics engine
        Assert.False(pi.Added);
    }
}

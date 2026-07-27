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
/// T51: the wave-2 remaining mutators (doublejump, hook, bugrigs, campcheck, damagetext, itemstime + the P3
/// stubs breakablehook/kick_teamkiller/dynamic_handicap) and the 5 Overkill weapons (ok{machinegun,shotgun,
/// nex,hmg,rpc}). Each self-registers via <c>[Mutator]</c>/<c>[Weapon]</c> and subscribes to a hook chain when
/// its cvar is set; these tests boot the registries, flip the cvar, run <see cref="MutatorActivation.Apply"/>,
/// and exercise the ported behavior through the real damage / physics / hook pipelines.
///
/// Mirrors the MutatorBatchT19Tests harness (GlobalState collection, parallelism disabled assembly-wide).
/// </summary>
[Collection("GlobalState")]
public class MutatorBatchT51Tests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    /// <summary>A settable-clock facade; optionally over a pre-built CollisionWorld (for the trace-driven mutators).</summary>
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new();
        public TestFacade(CollisionWorld? world = null) { Inner = new EngineServices(world ?? new CollisionWorld()); }
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    private static TestFacade Boot(CollisionWorld? world, params (string name, string value)[] cvars)
    {
        var facade = new TestFacade(world);
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

    private static TestFacade Boot(params (string name, string value)[] cvars) => Boot(null, cvars);

    private static Entity NewPlayer(Vector3 origin = default) => Configure(new Entity(), origin);

    private static Entity SpawnPlayer(TestFacade f, Vector3 origin = default)
    {
        Entity e = f.Entities.Spawn();
        Configure(e, origin);
        f.Entities.SetOrigin(e, origin);
        return e;
    }

    private static Entity Configure(Entity e, Vector3 origin)
    {
        e.ClassName = "player";
        e.Flags = EntFlags.Client;
        e.Origin = origin;
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.Health = 100; e.MaxHealth = 100;
        e.TakeDamage = DamageMode.Yes;
        e.DamageForceScale = 1f;
        return e;
    }

    private static CollisionWorld FloorWorld()
    {
        var world = new CollisionWorld();
        // A floor slab with its top at Z=0 (Quake Z up).
        world.AddBrush(Brush.FromBox(new Vector3(-512, -512, -64), new Vector3(512, 512, 0), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    // ----------------------------------------------------------------------------------------------
    //  Registration
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void AllNewMutators_Register_ByName()
    {
        Boot();
        Assert.NotNull(Mutators.ByName("doublejump"));
        Assert.NotNull(Mutators.ByName("grappling_hook"));
        Assert.NotNull(Mutators.ByName("bugrigs"));
        Assert.NotNull(Mutators.ByName("campcheck"));
        Assert.NotNull(Mutators.ByName("damagetext"));
        Assert.NotNull(Mutators.ByName("itemstime"));
        Assert.NotNull(Mutators.ByName("breakablehook"));
        Assert.NotNull(Mutators.ByName("kick_teamkiller"));
        Assert.NotNull(Mutators.ByName("dynamic_handicap"));
    }

    [Fact]
    public void AllOverkillWeapons_Register_ByName()
    {
        Boot();
        foreach (string n in new[] { "okmachinegun", "okshotgun", "oknex", "okhmg", "okrpc" })
            Assert.NotNull(Weapons.ByName(n));
        // The OverkillMutator's loadout names now resolve (they were NULL before T51).
        Assert.IsType<OkMachinegun>(Weapons.ByName("okmachinegun"));
        Assert.IsType<OkNex>(Weapons.ByName("oknex"));
    }

    [Fact]
    public void OverkillWeapons_HaveExpectedIdentity()
    {
        Boot();
        var mg = (OkMachinegun)Weapons.ByName("okmachinegun")!;
        Assert.Equal(ResourceType.Bullets, mg.AmmoType);
        Assert.Equal(3, mg.Impulse);
        Assert.True((mg.SpawnFlags & WeaponFlags.MutatorBlocked) != 0);
        Assert.True((mg.SpawnFlags & WeaponFlags.Hidden) != 0);

        var hmg = (OkHmg)Weapons.ByName("okhmg")!;
        Assert.True(hmg.IsSuperWeapon, "okhmg is WEP_FLAG_SUPERWEAPON");
        var rpc = (OkRpc)Weapons.ByName("okrpc")!;
        Assert.True(rpc.IsSuperWeapon, "okrpc is WEP_FLAG_SUPERWEAPON");
    }

    // ----------------------------------------------------------------------------------------------
    //  doublejump (the headline correctness fix)
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Doublejump_Disabled_Inert()
    {
        Boot(("sv_doublejump", "0"));
        var dj = Mutators.ByName("doublejump")!;
        Assert.False(dj.IsEnabled);

        // With sv_doublejump 0 the mutator doesn't subscribe; a PlayerJump call leaves Multijump false.
        var p = NewPlayer();
        var args = new MutatorHooks.PlayerJumpArgs(p, 270f, multijump: false);
        MutatorHooks.PlayerJump.Call(ref args);
        Assert.False(args.Multijump);
    }

    [Fact]
    public void Doublejump_GrantsAndClips_OnWalkableSurface()
    {
        // Stand the player on the floor (hull bottom at z = origin.z - 24). origin.z = 24 → bottom at 0 (on floor).
        var f = Boot(FloorWorld(), ("sv_doublejump", "1"));
        var dj = Mutators.ByName("doublejump")!;
        Assert.True(dj.IsEnabled);

        var p = NewPlayer(new Vector3(0, 0, 24f));
        p.Velocity = new Vector3(0, 0, -100f); // sliding down into the plane (negative dot with the up normal)

        var args = new MutatorHooks.PlayerJumpArgs(p, 270f, multijump: false);
        MutatorHooks.PlayerJump.Call(ref args);

        Assert.True(args.Multijump, "on a walkable surface the doublejump mutator grants the air-jump");
        // QC clips the into-plane (negative dot) component: a downward velocity into the floor normal is removed.
        Assert.True(p.Velocity.Z >= -0.001f, $"into-plane velocity should be clipped, got z={p.Velocity.Z}");
    }

    [Fact]
    public void Doublejump_NoGrant_InMidair()
    {
        // High above the floor: the tracebox 0.01u down hits nothing → no grant, velocity untouched.
        var f = Boot(FloorWorld(), ("sv_doublejump", "1"));
        var p = NewPlayer(new Vector3(0, 0, 500f));
        p.Velocity = new Vector3(0, 0, -100f);

        var args = new MutatorHooks.PlayerJumpArgs(p, 270f, multijump: false);
        MutatorHooks.PlayerJump.Call(ref args);

        Assert.False(args.Multijump, "midair (no surface below) must NOT grant a doublejump");
        Assert.Equal(-100f, p.Velocity.Z, 3); // velocity unchanged
    }

    // ----------------------------------------------------------------------------------------------
    //  bugrigs
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Bugrigs_PMPhysics_ReplacesMove_OnlyWhenEnabled()
    {
        var f = Boot(FloorWorld(), ("g_bugrigs", "1"));
        Assert.True(Mutators.ByName("bugrigs")!.IsEnabled);

        var p = NewPlayer(new Vector3(0, 0, 24f));
        var args = new MutatorHooks.PMPhysicsArgs(p, 1f, 1f / 60f);
        bool replaced = MutatorHooks.PMPhysics.Call(ref args);
        Assert.True(replaced, "with g_bugrigs 1 the PM_Physics hook fully replaces the move (returns true)");
        // The rig switches movetype away from the default walk (NOCLIP in planar mode / FLY otherwise).
        Assert.True(p.MoveType is MoveType.Noclip or MoveType.Fly, $"bugrigs sets a rig movetype, got {p.MoveType}");
    }

    [Fact]
    public void Bugrigs_Disabled_DoesNotReplaceMove()
    {
        var f = Boot(FloorWorld(), ("g_bugrigs", "0"));
        Assert.False(Mutators.ByName("bugrigs")!.IsEnabled);

        var p = NewPlayer(new Vector3(0, 0, 24f));
        var args = new MutatorHooks.PMPhysicsArgs(p, 1f, 1f / 60f);
        bool replaced = MutatorHooks.PMPhysics.Call(ref args);
        Assert.False(replaced, "with g_bugrigs 0 the hook is not subscribed and the move is not replaced");
    }

    [Fact]
    public void Bugrigs_PlayerPhysics_StashesPrevAngles()
    {
        var f = Boot(FloorWorld(), ("g_bugrigs", "1"));
        var p = NewPlayer(new Vector3(0, 0, 24f));
        p.Angles = new Vector3(5f, 90f, 0f);

        var args = new MutatorHooks.PlayerPhysicsArgs(p, 1f / 60f);
        MutatorHooks.PlayerPhysics.Call(ref args);
        Assert.Equal(new Vector3(5f, 90f, 0f), p.BugrigsPrevAngles);
    }

    // ----------------------------------------------------------------------------------------------
    //  campcheck
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Campcheck_DamagesCampingPlayer_AfterInterval()
    {
        var f = Boot(
            ("g_campcheck", "1"),
            ("g_campcheck_damage", "100"),
            ("g_campcheck_distance", "1800"),
            ("g_campcheck_interval", "10"));
        Assert.True(Mutators.ByName("campcheck")!.IsEnabled);

        var p = SpawnPlayer(f, new Vector3(0, 0, 24f));
        // Spawn-init the timer (PlayerSpawn sets nextcheck = time + interval*2).
        var ps = new MutatorHooks.PlayerSpawnArgs(p, null);
        MutatorHooks.PlayerSpawn.Call(ref ps);

        // Tick a few frames WITHOUT moving, advancing the clock past the next check.
        float before = p.Health;
        for (int i = 0; i < 5; i++)
        {
            f.GameClock.Time = 5f + i * 10f; // > nextcheck (= 20) on later iterations
            var pt = new MutatorHooks.PlayerPreThinkArgs(p);
            MutatorHooks.PlayerPreThink.Call(ref pt);
        }
        Assert.True(p.Health < before, $"a camping player should take camp damage: {before} -> {p.Health}");
    }

    [Fact]
    public void Campcheck_NoDamage_WhenMoving()
    {
        var f = Boot(
            ("g_campcheck", "1"),
            ("g_campcheck_damage", "100"),
            ("g_campcheck_distance", "100"),  // small threshold; the player moves far past it
            ("g_campcheck_interval", "10"));

        var p = SpawnPlayer(f, new Vector3(0, 0, 24f));
        var ps = new MutatorHooks.PlayerSpawnArgs(p, null);
        MutatorHooks.PlayerSpawn.Call(ref ps);

        float before = p.Health;
        for (int i = 0; i < 5; i++)
        {
            f.GameClock.Time = 5f + i * 10f;
            p.Origin = new Vector3(i * 500f, 0, 24f); // move 500u each frame (well past the 100u threshold)
            f.Entities.SetOrigin(p, p.Origin);
            var pt = new MutatorHooks.PlayerPreThinkArgs(p);
            MutatorHooks.PlayerPreThink.Call(ref pt);
        }
        Assert.Equal(before, p.Health); // moving → no camp damage
    }

    // ----------------------------------------------------------------------------------------------
    //  damagetext
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Damagetext_QueuesEvent_OnPlayerDamaged()
    {
        Boot(("sv_damagetext", "2"));
        var dt = (DamagetextMutator)Mutators.ByName("damagetext")!;
        Assert.True(dt.IsEnabled);

        var attacker = NewPlayer(new Vector3(100, 0, 0));
        var target = NewPlayer();
        // health removed 40, no armor, potential 40 (a clean hit).
        var args = new MutatorHooks.PlayerDamagedArgs(attacker, target, health: 40f, armor: 0f,
            hitLocation: target.Origin, deathType: DeathTypes.FromWeapon("vortex"), potentialDamage: 40f);
        MutatorHooks.PlayerDamaged.Call(ref args);

        var pending = dt.DrainPending();
        Assert.Single(pending);
        Assert.Equal(40f, pending[0].Health);
        Assert.True((pending[0].Flags & DamageTextWire.FlagNoArmor) != 0, "armor 0 → DTFLAG_NO_ARMOR");
        Assert.Equal(target, pending[0].Target);
    }

    [Fact]
    public void Damagetext_AccumulatesSameFrameHits()
    {
        var f = Boot(("sv_damagetext", "2"));
        var dt = (DamagetextMutator)Mutators.ByName("damagetext")!;
        f.GameClock.Time = 5f;

        var attacker = NewPlayer(new Vector3(100, 0, 0));
        var target = NewPlayer();
        string deathType = DeathTypes.FromWeapon("shotgun");

        // Two pellets from the same shotgun blast THIS frame accumulate onto one number.
        for (int i = 0; i < 2; i++)
        {
            var a = new MutatorHooks.PlayerDamagedArgs(attacker, target, 10f, 0f, target.Origin, deathType, 10f);
            MutatorHooks.PlayerDamaged.Call(ref a);
        }

        var pending = dt.DrainPending();
        Assert.Single(pending);                 // accumulated into one event
        Assert.Equal(20f, pending[0].Health);   // 10 + 10
    }

    [Fact]
    public void Damagetext_Disabled_QueuesNothing()
    {
        Boot(("sv_damagetext", "0"));
        var dt = Mutators.ByName("damagetext")!;
        Assert.False(dt.IsEnabled); // sv_damagetext 0 → not subscribed
    }

    // ----------------------------------------------------------------------------------------------
    //  DamageTextFormat (pure helper)
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void DamageTextFormat_DefaultFormat_ShowsNegativeTotal()
    {
        // Default "-{total}" with 40 health + 0 armor (×128 precision): total = 40.
        int mul = DamageTextWire.PrecisionMultiplier;
        string s = DamageTextFormat.Build("-{total}", verbose: false, hideRedundant: false,
            health: 40f * mul, armor: 0f, potential: 40f * mul);
        Assert.Equal("-40", s);
    }

    [Fact]
    public void DamageTextFormat_HideRedundantArmor()
    {
        int mul = DamageTextWire.PrecisionMultiplier;
        // "{health}+{armor}" with 0 armor and hide_redundant → the armor token vanishes.
        string s = DamageTextFormat.Build("{health}+{armor}", verbose: false, hideRedundant: true,
            health: 30f * mul, armor: 0f, potential: 30f * mul);
        Assert.Equal("30+", s); // armor hidden (QC strips the value, leaving the literal '+')
    }

    [Fact]
    public void DamageTextFormat_StripsUnknownTokens()
    {
        int mul = DamageTextWire.PrecisionMultiplier;
        string s = DamageTextFormat.Build("{total}{future_token}", verbose: false, hideRedundant: false,
            health: 10f * mul, armor: 0f, potential: 10f * mul);
        Assert.Equal("10", s); // the unknown {future_token} is stripped
    }

    [Fact]
    public void DamageTextFormat_MapSize_ClampsAndInterpolates()
    {
        // map_bound_ranges(potential, 25, 140, 10, 16): small dmg → ~size_min, large dmg → size_max, clamped.
        Assert.Equal(10f, DamageTextFormat.MapSize(0f, 25f, 140f, 10f, 16f), 3);   // below min → min
        Assert.Equal(16f, DamageTextFormat.MapSize(200f, 25f, 140f, 10f, 16f), 3); // above max → max
        float mid = DamageTextFormat.MapSize(82.5f, 25f, 140f, 10f, 16f);          // halfway → halfway
        Assert.True(mid is > 12f and < 14f, $"midpoint size should be ~13, got {mid}");
    }

    // ----------------------------------------------------------------------------------------------
    //  per-viewer visibility (QC write_damagetext, sv_damagetext.qc:32-37)
    // ----------------------------------------------------------------------------------------------

    [Theory]
    // tier 0/negative: producer already off, but the gate must agree (nothing shows).
    [InlineData(0f, true, false, false)]
    // tier 1 (spectators/observers only): the ATTACKER themselves sees nothing…
    [InlineData(1f, true, false, false)]
    // …but a free-fly observer sees everything.
    [InlineData(1f, false, true, true)]
    // tier 2 (shipped default): the attacker sees their own hits…
    [InlineData(2f, true, false, true)]
    // …and NOT anyone else's (the bot-vs-bot center-screen number pileup this gate exists to stop).
    [InlineData(2f, false, false, false)]
    // tier 2 keeps the tier-1 observer grant.
    [InlineData(2f, false, true, true)]
    // tier 3: everyone sees every hit.
    [InlineData(3f, false, false, true)]
    public void Damagetext_Visibility_Follows_The_SvDamagetext_Tiers(
        float tier, bool isAttacker, bool isObserver, bool expected)
        => Assert.Equal(expected,
            DamagetextMutator.ShouldShowTo(tier, isAttacker, isObserver));

    [Fact]
    public void Damagetext_Visibility_Spectator_Of_The_Attacker_Sees_Their_Hits_At_Tier_1()
        => Assert.True(DamagetextMutator.ShouldShowTo(1f,
            viewerIsAttacker: false, viewerIsObserver: false, viewerSpectatesAttacker: true));

    // ----------------------------------------------------------------------------------------------
    //  itemstime
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Itemstime_PublishesRespawnTime_ForTimedItem()
    {
        var f = Boot(("sv_itemstime", "1"));
        var it = (ItemstimeMutator)Mutators.ByName("itemstime")!;
        Assert.True(it.IsEnabled);
        f.GameClock.Time = 5f;

        // A mega-health on cooldown (scheduled to respawn at t=20).
        Entity mega = f.Entities.Spawn();
        mega.ClassName = "item_health_mega";
        mega.Flags |= EntFlags.Item;
        mega.ScheduledRespawnTime = 20f;
        f.Entities.SetOrigin(mega, Vector3.Zero);

        it.Recompute();
        Assert.True(it.CurrentTimes.TryGetValue("health_mega", out float t));
        Assert.Equal(20f, t, 3); // absolute respawn time (positive = on cooldown)
    }

    [Fact]
    public void Itemstime_NegativeEncoding_WhenAnotherCopyAvailable()
    {
        var f = Boot(("sv_itemstime", "1"));
        var it = (ItemstimeMutator)Mutators.ByName("itemstime")!;
        f.GameClock.Time = 5f;

        // Two armor-mega: one up now (scheduled <= time), one on cooldown (t=30).
        Entity up = f.Entities.Spawn();
        up.ClassName = "item_armor_mega"; up.Flags |= EntFlags.Item; up.ScheduledRespawnTime = 0f;
        f.Entities.SetOrigin(up, Vector3.Zero);
        Entity cd = f.Entities.Spawn();
        cd.ClassName = "item_armor_mega"; cd.Flags |= EntFlags.Item; cd.ScheduledRespawnTime = 30f;
        f.Entities.SetOrigin(cd, new Vector3(100, 0, 0));

        it.Recompute();
        Assert.True(it.CurrentTimes.TryGetValue("armor_mega", out float t));
        // QC Item_ItemsTime_UpdateTime: a copy is available → NEGATIVE encoding of the min cooldown time.
        Assert.True(t < -1f, $"expected the 'available now' negative encoding, got {t}");
        Assert.Equal(-30f, t, 3);
    }

    [Fact]
    public void Itemstime_Disabled_NoTimes()
    {
        Boot(("sv_itemstime", "0"));
        var it = Mutators.ByName("itemstime")!;
        Assert.False(it.IsEnabled);
    }

    // ----------------------------------------------------------------------------------------------
    //  hook mutator
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void HookMutator_SetsOffhandHook_OnSpawn()
    {
        Boot(("g_grappling_hook", "1"));
        Assert.True(Mutators.ByName("grappling_hook")!.IsEnabled);

        var p = NewPlayer();
        var ps = new MutatorHooks.PlayerSpawnArgs(p, null);
        MutatorHooks.PlayerSpawn.Call(ref ps);
        Assert.Equal("hook", p.OffhandWeapon);
    }

    [Fact]
    public void HookMutator_SuppressesHookPickup_ViaFilterItem()
    {
        Boot(("g_grappling_hook", "1"));

        Entity def = new() { ClassName = "weapon_hook", NetName = "hook" };
        var args = new MutatorHooks.FilterItemDefinitionArgs(def);
        bool suppressed = MutatorHooks.FilterItemDefinition.Call(ref args);
        Assert.True(suppressed, "the offhand-hook mutator suppresses the WEP_HOOK world pickup");
    }

    [Fact]
    public void HookMutator_GrantsFuel_WhenUseAmmo()
    {
        Boot(("g_grappling_hook", "1"), ("g_grappling_hook_useammo", "1"), ("g_balance_fuel_rotstable", "50"));

        var loadout = new StartLoadout();
        var args = new MutatorHooks.SetStartItemsArgs(loadout);
        MutatorHooks.SetStartItems.Call(ref args);
        Assert.True(args.Loadout.AmmoFuel >= 50f, "useammo on → start fuel granted");
        Assert.Contains("FUEL_REGEN", args.Loadout.ItemFlags);
    }

    [Fact]
    public void HookMutator_NoFuelGrant_WhenUseAmmoOff()
    {
        Boot(("g_grappling_hook", "1"), ("g_grappling_hook_useammo", "0"), ("g_balance_fuel_rotstable", "50"));

        var loadout = new StartLoadout();
        var args = new MutatorHooks.SetStartItemsArgs(loadout);
        MutatorHooks.SetStartItems.Call(ref args);
        Assert.Equal(0f, args.Loadout.AmmoFuel); // useammo off → no fuel grant
        Assert.DoesNotContain("FUEL_REGEN", args.Loadout.ItemFlags);
    }

    // ----------------------------------------------------------------------------------------------
    //  dynamic_handicap (P3 — fully portable)
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void DynamicHandicap_HandicapsAboveMeanPlayer()
    {
        var f = Boot(
            ("g_dynamic_handicap", "1"),
            ("g_dynamic_handicap_scale", "0.1"),
            ("g_dynamic_handicap_exponent", "1"),
            ("g_dynamic_handicap_min", "0"),
            ("g_dynamic_handicap_max", "0"));
        var dh = (DynamicHandicapMutator)Mutators.ByName("dynamic_handicap")!;
        Assert.True(dh.IsEnabled);

        var strong = SpawnPlayer(f, new Vector3(0, 0, 0));
        var weak = SpawnPlayer(f, new Vector3(100, 0, 0));
        XonoticGodot.Common.Gameplay.Scoring.GameScores.SetPlayer(strong, XonoticGodot.Common.Gameplay.Scoring.GameScores.Score, 100);
        XonoticGodot.Common.Gameplay.Scoring.GameScores.SetPlayer(weak, XonoticGodot.Common.Gameplay.Scoring.GameScores.Score, 0);

        dh.UpdateHandicap();
        // The above-mean player gets a handicap > 1 (deals less / takes more); the below-mean player < 1.
        Assert.True(strong.HandicapGive > 1f, $"strong player should be handicapped > 1, got {strong.HandicapGive}");
        Assert.True(weak.HandicapGive < 1f, $"weak player should be buffed < 1, got {weak.HandicapGive}");
    }

    // ----------------------------------------------------------------------------------------------
    //  breakablehook (P3 — substrate now exists: shootable grapplinghook)
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Breakablehook_ZeroesDamage_OnOwnHook_WhenOwnerBreakOff()
    {
        Boot(("g_breakablehook", "1"), ("g_breakablehook_owner", "0"));
        Assert.True(Mutators.ByName("breakablehook")!.IsEnabled);

        var owner = NewPlayer();
        var hook = new Entity { ClassName = "grapplinghook", Owner = owner };
        var args = new MutatorHooks.DamageCalculateArgs(hook, owner, hook, DeathTypes.FromWeapon("vortex"),
            damage: 50f, mirrorDamage: 0f, force: Vector3.Zero, weaponEntity: null);
        MutatorHooks.DamageCalculate.Call(ref args);
        Assert.Equal(0f, args.Damage); // can't break your own hook with owner-break off
    }

    // ----------------------------------------------------------------------------------------------
    //  Overkill weapon ammo / superweapon / clip wiring (Wave A2 fixes F5 / F6 / F18)
    // ----------------------------------------------------------------------------------------------

    private const int ItemUnlimitedSuperweapons = 1 << 1; // ItemFlags.UnlimitedSuperweapons

    /// <summary>Configure a player to "hold primary, ready to fire, full clip" for the weapon in slot 0.</summary>
    private static (WeaponSlot slot, WeaponSlotState st) ArmPrimary(Entity actor, float clip)
    {
        var slot = new WeaponSlot(0);
        var st = actor.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.AttackFinished = 0f;
        st.ButtonAttack = true;
        st.ClipLoad = (int)clip;
        return (slot, st);
    }

    // F6: the 5 Overkill weapons were missing from WeaponAmmo.Check, so wr_checkammo fell through to the
    // default `true` (it never ran). After the fix, the primary ammo check is honored.
    [Theory]
    [InlineData("okmachinegun", ResourceType.Bullets)]
    [InlineData("okshotgun", ResourceType.Shells)]
    [InlineData("oknex", ResourceType.Cells)]
    [InlineData("okhmg", ResourceType.Bullets)]
    [InlineData("okrpc", ResourceType.Rockets)]
    public void OverkillWeapons_WrCheckAmmo_HonorsPrimaryAmmo(string netName, ResourceType ammo)
    {
        Boot();
        var w = Weapons.ByName(netName)!;
        var slot = new WeaponSlot(0);

        var withAmmo = NewPlayer();
        withAmmo.SetResource(ammo, 999f);
        Assert.True(w.WrCheckAmmo(withAmmo, slot, secondary: false),
            $"{netName} with full ammo should report wr_checkammo1 true");

        var dry = NewPlayer();
        dry.SetResource(ammo, 0f);
        Assert.False(w.WrCheckAmmo(dry, slot, secondary: false),
            $"{netName} with no ammo must report wr_checkammo1 FALSE (was true via the missing-case default before F6)");
    }

    // F18: the Overkill weapons are WEP_FLAG_RELOADABLE; W_DecreaseAmmo must drain the CLIP (not the player's
    // ammo resource) so the wr_think forced-reload branch engages. With UNLIMITED_AMMO (granted by Overkill),
    // the clip still drains (and the resource is untouched) — exactly as QC's W_DecreaseAmmo does.
    [Theory]
    [InlineData("okmachinegun", ResourceType.Bullets)]
    [InlineData("okshotgun", ResourceType.Shells)]
    [InlineData("oknex", ResourceType.Cells)]
    [InlineData("okrpc", ResourceType.Rockets)]
    public void OverkillWeapons_Fire_DrainsClip_NotResource(string netName, ResourceType ammo)
    {
        var f = Boot(FloorWorld());
        var w = Weapons.ByName(netName)!;
        var actor = SpawnPlayer(f, new Vector3(0, 0, 64f));
        actor.UnlimitedAmmo = true;                 // Overkill grants IT_UNLIMITED_AMMO
        actor.SetResource(ammo, 500f);
        float perShot = PrimaryAmmoCost(w);

        var (slot, st) = ArmPrimary(actor, clip: 50f);
        int clipBefore = st.ClipLoad;
        float resBefore = actor.GetResource(ammo);

        w.WrThink(actor, slot, FireMode.Primary);

        Assert.True(st.ClipLoad < clipBefore,
            $"{netName} should drain its clip via W_DecreaseAmmo, clip {clipBefore} -> {st.ClipLoad}");
        Assert.Equal(clipBefore - (int)perShot, st.ClipLoad);
        // reloadable + unlimited ammo: the resource pool is NOT touched (the old TakeResource bug drained it).
        Assert.Equal(resBefore, actor.GetResource(ammo), 3);
    }

    // F5: the okhmg superweapon gate belongs in the ATTACK path, not in wr_checkammo1. Without the Superweapon
    // status (and without IT_UNLIMITED_SUPERWEAPONS) the HMG fires NOTHING and switches away (okhmg.qc:22-25);
    // with the status (or the unlimited-superweapons item) it fires. Overkill's IT_UNLIMITED_AMMO would
    // short-circuit the shared ammo gate, so this must NOT live in the (now ammo-only) wr_checkammo1.
    [Fact]
    public void OkHmg_WithoutSuperweapon_FiresNothing_AndSwitchesAway()
    {
        var f = Boot(FloorWorld());
        var hmg = (OkHmg)Weapons.ByName("okhmg")!;
        var blaster = Weapons.ByName("blaster")!;

        var actor = SpawnPlayer(f, new Vector3(0, 0, 64f));
        actor.UnlimitedAmmo = true;                 // Overkill loadout
        actor.SetResource(ResourceType.Bullets, 500f);
        Inventory.GiveWeapon(actor, hmg);
        Inventory.GiveWeapon(actor, blaster);
        Inventory.SwitchWeapon(actor, hmg);         // currently holding the HMG
        actor.Items = 0;                            // no IT_UNLIMITED_SUPERWEAPONS
        // no Superweapon status applied

        var (slot, st) = ArmPrimary(actor, clip: 50f);
        int clipBefore = st.ClipLoad;

        hmg.WrThink(actor, slot, FireMode.Primary);

        Assert.Equal(clipBefore, st.ClipLoad);                 // no shot fired (gate halted it before W_DecreaseAmmo)
        Assert.NotEqual(hmg.RegistryId, actor.ActiveWeaponId); // W_SwitchWeapon_Force switched off the HMG
    }

    [Fact]
    public void OkHmg_WithSuperweaponStatus_Fires()
    {
        var f = Boot(FloorWorld());
        var hmg = (OkHmg)Weapons.ByName("okhmg")!;
        var actor = SpawnPlayer(f, new Vector3(0, 0, 64f));
        actor.UnlimitedAmmo = true;
        actor.SetResource(ResourceType.Bullets, 500f);
        StatusEffectsCatalog.Apply(actor, StatusEffectsCatalog.Superweapon!, duration: 30f);

        var (slot, st) = ArmPrimary(actor, clip: 50f);
        int clipBefore = st.ClipLoad;

        hmg.WrThink(actor, slot, FireMode.Primary);

        Assert.True(st.ClipLoad < clipBefore,
            $"with the Superweapon status active the HMG fires (clip {clipBefore} -> {st.ClipLoad})");
    }

    [Fact]
    public void OkHmg_WithUnlimitedSuperweaponsItem_Fires()
    {
        var f = Boot(FloorWorld());
        var hmg = (OkHmg)Weapons.ByName("okhmg")!;
        var actor = SpawnPlayer(f, new Vector3(0, 0, 64f));
        actor.UnlimitedAmmo = true;
        actor.SetResource(ResourceType.Bullets, 500f);
        actor.Items = ItemUnlimitedSuperweapons;    // IT_UNLIMITED_SUPERWEAPONS satisfies the gate
        // no Superweapon status

        var (slot, st) = ArmPrimary(actor, clip: 50f);
        int clipBefore = st.ClipLoad;

        hmg.WrThink(actor, slot, FireMode.Primary);

        Assert.True(st.ClipLoad < clipBefore,
            $"IT_UNLIMITED_SUPERWEAPONS should satisfy the gate (clip {clipBefore} -> {st.ClipLoad})");
    }

    // F5 corollary: the superweapon gate is OUT of wr_checkammo1 (it is ammo-only now). With bullets present but
    // no superweapon, wr_checkammo1 must still report true (the old bundling returned false here).
    [Fact]
    public void OkHmg_CheckAmmoPrimary_IsAmmoOnly_NoSuperweaponRequired()
    {
        Boot();
        var hmg = (OkHmg)Weapons.ByName("okhmg")!;
        var actor = NewPlayer();
        actor.SetResource(ResourceType.Bullets, 500f);
        actor.Items = 0; // no superweapon item, no status
        Assert.True(hmg.CheckAmmoPrimary(actor),
            "wr_checkammo1 is ammo-only (okhmg.qc:116-122): ammo present → true even without the superweapon status");
    }

    /// <summary>The per-primary-shot ammo cost for an Overkill weapon (its Cvars.Ammo).</summary>
    private static float PrimaryAmmoCost(Weapon w) => w switch
    {
        OkMachinegun m => m.Cvars.Ammo,
        OkShotgun s => s.Cvars.Ammo,
        OkNex n => n.Cvars.Ammo,
        OkHmg h => h.Cvars.Ammo,
        OkRpc r => r.Cvars.Ammo,
        _ => 1f,
    };
}

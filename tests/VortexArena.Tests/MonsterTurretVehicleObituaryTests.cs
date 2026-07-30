using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression coverage for the monster / turret / vehicle attack DEATHTYPES (qcsrc/common/deathtypes/all.inc
/// REGISTER_DEATHTYPE rows that carry .message "monster"/"turret"/"vehicle"). The attack call sites used to tag
/// their damage with <c>DeathTypes.FromWeapon(NetName)</c> — a WEAPON-style tag — so the categorized predicates
/// (<see cref="DeathTypes.IsMonster"/>/<see cref="DeathTypes.IsTurret"/>/<see cref="DeathTypes.IsVehicle"/>)
/// never fired on a real kill and the obituary dropped to a generic weapon line. These drive the REAL attacks
/// (and the projectile-blast helpers each call site funnels through) against a damage-recording victim, then
/// assert the deathtype is the categorized special one and that the obituary selector
/// (<see cref="DeathMessages.SelectSpecial"/>, what <c>Scores.EmitObituary</c> calls) picks the
/// monster / turret / vehicle line — never a weapon line. A final test drives the full obituary emission over
/// the <see cref="Combat.Death"/> bus and asserts the DEATH_MURDER_MONSTER / _CHEAT / _VH_* kill-feed lines.
///
/// Capture mechanism: a non-player victim carries a <see cref="Entity.GtEventDamage"/> spy. The real damage
/// pipeline (<c>DamageSystem.Apply</c>) routes a non-player edict's damage straight to that hook with the
/// deathtype string, so the spy records exactly the tag the attack dealt.
/// </summary>
[Collection("GlobalState")]
public class MonsterTurretVehicleObituaryTests : IDisposable
{
    private readonly IEngineServices _prevServices;
    private readonly INotificationSink _prevSink;
    private readonly NotificationSystem.RecordingSink _rec = new();
    private readonly List<Scores> _subscribed = new();

    public MonsterTurretVehicleObituaryTests()
    {
        var world = new CollisionWorld();
        // Big flat floor (Quake Z up) so spawns rest on it and the LOS/melee traces have something to clip.
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();

        _prevServices = Api.Services;
        GameInit.Boot(new EngineServices(world)); // registries (monsters/turrets/vehicles/notifications) + DamageSystem
        MonsterAI.ResetCounters();
        DeathMessages.ResetChoiceArgCounts();
        Prandom.Seed(0xBEEF);

        _prevSink = NotificationSystem.Sink;
        NotificationSystem.Sink = _rec;
        NotificationSystem.WarmupStage = false;
        NotificationSystem.DefaultChoiceValue = 1;
        NotificationSystem.ChoiceValues.Clear();
    }

    public void Dispose()
    {
        foreach (var s in _subscribed) s.UnsubscribeFromDeaths();
        NotificationSystem.Sink = _prevSink;
        Api.Services = _prevServices;
    }

    // ------------------------------------------------------------------------------------------------
    //  harness
    // ------------------------------------------------------------------------------------------------

    /// <summary>Records the deathtype string the damage pipeline delivers to this victim (its GtEventDamage hook).</summary>
    private sealed class Capture { public Entity Victim = null!; public string? DeathType; }

    /// <summary>
    /// A non-player, damageable target linked into the world (so traces + FindInRadius see it) whose
    /// <see cref="Entity.GtEventDamage"/> records the deathtype tag of any hit. It never dies (huge health) —
    /// the test only needs the tag the attack carried.
    /// </summary>
    private static Capture MakeVictim(Vector3 origin, Vector3 mins, Vector3 maxs)
    {
        var cap = new Capture();
        Entity v = Api.Entities.Spawn();
        v.ClassName = "obituary_target"; // NOT a client: DamageSystem routes non-players to GtEventDamage
        v.TakeDamage = DamageMode.Aim;
        v.Health = 100_000f;
        v.Solid = Solid.BBox;
        v.GtEventDamage = (self, inflictor, attacker, deathType, damage, hitLoc, force) => cap.DeathType = deathType;
        Api.Entities.SetSize(v, mins, maxs);
        Api.Entities.SetOrigin(v, origin); // links AbsMin/AbsMax into the area grid
        cap.Victim = v;
        return cap;
    }

    private static Entity SpawnMonster(string netName, Vector3 origin)
    {
        Monster? def = Monsters.ByName(netName);
        Assert.NotNull(def);
        Entity e = Api.Entities.Spawn();
        e.Origin = origin;
        Api.Entities.SetOrigin(e, origin);
        Assert.True(MonsterAI.SpawnFromMap(def!, e, checkAppear: false));
        return e;
    }

    // ================================================================================================
    //  MONSTERS (DEATH_MONSTER_* — .message "monster", shared DEATH_MURDER_MONSTER + per-monster self line)
    // ================================================================================================

    [Fact]
    public void SpiderBite_RealAttack_TagsMonsterSpider_ObituaryPicksMonsterLine()
    {
        Monster spider = Monsters.ByName("spider")!;
        Entity e = SpawnMonster("spider", new Vector3(0, 0, 26));
        e.Angles = Vector3.Zero; // face +X (the melee trace goes along facing)

        var cap = MakeVictim(new Vector3(60, 0, 26), new Vector3(-24, -24, -40), new Vector3(24, 24, 60));

        // Real call site: Spider.Attack (melee branch) -> MonsterAI.MeleeAttack(DeathTypes.MonsterSpider).
        spider.Attack(e, cap.Victim);

        Assert.Equal(DeathTypes.MonsterSpider, cap.DeathType);
        Assert.True(DeathTypes.IsMonster(cap.DeathType));
        Assert.False(DeathTypes.IsWeapon(cap.DeathType)); // regression: was FromWeapon("spider") -> "weapon/spider"
        Assert.Equal("DEATH_MURDER_MONSTER", DeathMessages.SelectSpecial(cap.DeathType, murder: true));
        Assert.Equal("DEATH_SELF_MON_SPIDER", DeathMessages.SelectSpecial(cap.DeathType, murder: false));
    }

    [Fact]
    public void MonsterProjectileBlast_CarriesMonsterDeathtype_ObituaryPicksMonsterLine()
    {
        // The shared monster projectile spawner (mage spike / wyvern fireball / spider web / golem zap) now
        // carries its string deathtype through the blast (its deathType param was previously dropped, so every
        // monster projectile blast dealt Generic-typed damage). Drive it with the wyvern's fireball deathtype.
        Entity wyvern = SpawnMonster("wyvern", new Vector3(0, 0, 40));
        var st = MonsterAI.StateOf(wyvern)!;

        Entity proj = MonsterAI.SpawnProjectile(wyvern, st, new Vector3(1, 0, 0), speed: 0f,
            damage: 50f, edgeDamage: 20f, radius: 120f, force: 50f, deathType: DeathTypes.MonsterWyvern, lifetime: 5f);
        var cap = MakeVictim(proj.Origin, new Vector3(-16, -16, -16), new Vector3(16, 16, 16));

        proj.Touch!(proj, cap.Victim); // contact detonation -> RadiusDamage(deathTag: MonsterWyvern)

        Assert.Equal(DeathTypes.MonsterWyvern, cap.DeathType);
        Assert.True(DeathTypes.IsMonster(cap.DeathType));
        Assert.False(DeathTypes.IsWeapon(cap.DeathType));
        Assert.Equal("DEATH_MURDER_MONSTER", DeathMessages.SelectSpecial(cap.DeathType, murder: true));
        Assert.Equal("DEATH_SELF_MON_WYVERN", DeathMessages.SelectSpecial(cap.DeathType, murder: false));
    }

    // ================================================================================================
    //  TURRETS (DEATH_TURRET[_*] — .message "turret", per-turret self line + shared DEATH_MURDER_CHEAT)
    // ================================================================================================

    [Fact]
    public void TurretDie_DealsNoDeathBlast_MatchingBase()
    {
        // Base turret_die has its ammo-scaled RadiusDamage COMMENTED OUT (sv_turrets.qc:182): a dying turret
        // deals NO area damage. The port previously did a port-added blast; TurretAI.Die must not damage anyone.
        Entity turret = Api.Entities.Spawn();
        turret.Origin = new Vector3(0, 0, 8);
        Api.Entities.SetOrigin(turret, turret.Origin);
        TurretSpawnFuncs.EWheel(turret);
        Assert.False(turret.IsFreed);

        var cap = MakeVictim(new Vector3(48, 0, 20), new Vector3(-24, -24, -24), new Vector3(24, 24, 24));

        TurretAI.Die(turret);

        Assert.Null(cap.DeathType); // no blast tagged the nearby victim

        // The DEATH_TURRET obituary mapping itself stays correct (still used by per-turret weapon deathtypes).
        Assert.True(DeathTypes.IsTurret(DeathTypes.Turret));
        Assert.False(DeathTypes.IsWeapon(DeathTypes.Turret));
        Assert.Equal("DEATH_MURDER_CHEAT", DeathMessages.SelectSpecial(DeathTypes.Turret, murder: true));
        Assert.Equal("DEATH_SELF_TURRET", DeathMessages.SelectSpecial(DeathTypes.Turret, murder: false));
    }

    [Fact]
    public void TurretMachinegunBullet_CarriesPerTurretDeathtype_ObituaryPicksTurretLine()
    {
        // The shared turret hitscan helper the machinegun + walker-gun call sites use; it previously ignored its
        // int deathtype and tagged FromWeapon(turret.NetName). It now takes the per-turret string tag.
        Entity turret = Api.Entities.Spawn();
        turret.ClassName = "turret_machinegun";
        turret.NetName = "machinegun";
        turret.Origin = new Vector3(0, 0, 20);
        Api.Entities.SetOrigin(turret, turret.Origin);

        var cap = MakeVictim(new Vector3(120, 0, 20), new Vector3(-32, -32, -32), new Vector3(32, 32, 32));

        TurretCombat.FireBullet(turret, turret.Origin, new Vector3(1, 0, 0), spread: 0f,
            damage: 20f, force: 0f, DeathTypes.TurretMachinegun);

        Assert.Equal(DeathTypes.TurretMachinegun, cap.DeathType);
        Assert.True(DeathTypes.IsTurret(cap.DeathType));
        Assert.False(DeathTypes.IsWeapon(cap.DeathType));
        Assert.Equal("DEATH_SELF_TURRET_MACHINEGUN", DeathMessages.SelectSpecial(cap.DeathType, murder: false));
        Assert.Equal("DEATH_MURDER_CHEAT", DeathMessages.SelectSpecial(cap.DeathType, murder: true));
    }

    [Fact]
    public void TurretProjectileBlast_CarriesPerTurretDeathtype_ObituaryPicksTurretLine()
    {
        // The generic turret projectile (MLRS/plasma/ewheel/flac) — its int deathtype was lost in the blast;
        // it now carries the per-turret string tag through RadiusDamage.
        Entity turret = Api.Entities.Spawn();
        turret.Origin = Vector3.Zero;
        Api.Entities.SetOrigin(turret, turret.Origin);
        var cap = MakeVictim(new Vector3(100, 0, 20), new Vector3(-16, -16, -16), new Vector3(16, 16, 16));

        Entity proj = TurretSpawn.Projectile(turret, new Vector3(100, 0, 20), new Vector3(0, 0, 1), speed: 100f,
            size: 4f, health: 0f, damage: 80f, edgeDamage: 0f, radius: 120f, force: 0f,
            DeathTypes.TurretMlrs, spread: 0f);
        proj.Touch!(proj, cap.Victim); // contact detonation -> RadiusDamage(deathTag: TurretMlrs)

        Assert.Equal(DeathTypes.TurretMlrs, cap.DeathType);
        Assert.True(DeathTypes.IsTurret(cap.DeathType));
        Assert.Equal("DEATH_SELF_TURRET_MLRS", DeathMessages.SelectSpecial(cap.DeathType, murder: false));
    }

    // ================================================================================================
    //  VEHICLES (DEATH_VH_* — .message "vehicle", per-vehicle self/murder lines)
    // ================================================================================================

    [Fact]
    public void SpiderbotMinigun_RealFire_TagsVhSpidMinigun_ObituaryPicksVehicleLine()
    {
        var bot = new Spiderbot();
        Entity vehicle = Api.Entities.Spawn();
        vehicle.ClassName = "vehicle_spiderbot";
        vehicle.NetName = "spiderbot";
        vehicle.Origin = new Vector3(0, 0, 30);
        vehicle.Angles = Vector3.Zero; // barrels point +X (no model tag -> facing fallback)
        Api.Entities.SetOrigin(vehicle, vehicle.Origin);
        Entity pilot = Api.Entities.Spawn();

        var cap = MakeVictim(new Vector3(220, 0, 30), new Vector3(-48, -48, -48), new Vector3(48, 48, 48));

        // Real call site: Spiderbot.FireMinigun -> fireBullet -> Combat.Damage(DeathTypes.VhSpidMinigun).
        bot.FireMinigun(vehicle, pilot);

        Assert.Equal(DeathTypes.VhSpidMinigun, cap.DeathType);
        Assert.True(DeathTypes.IsVehicle(cap.DeathType));
        Assert.False(DeathTypes.IsWeapon(cap.DeathType)); // regression: was FromWeapon -> a weapon kill-feed line
        Assert.Equal("DEATH_MURDER_VH_SPID_MINIGUN", DeathMessages.SelectSpecial(cap.DeathType, murder: true));
    }

    [Fact]
    public void VehicleProjectileBlast_CarriesPerVehicleDeathtype_ObituaryPicksVehicleLine()
    {
        // The shared vehicle projectile spawner (racer/raptor/bumblebee cannon, spiderbot rocket) — its string
        // deathtype param was dropped (the int registryId drove the blast -> Generic). It now carries the tag.
        Entity owner = Api.Entities.Spawn();
        owner.Origin = Vector3.Zero;
        Api.Entities.SetOrigin(owner, owner.Origin);
        Entity pilot = Api.Entities.Spawn();
        var cap = MakeVictim(new Vector3(100, 0, 20), new Vector3(-16, -16, -16), new Vector3(16, 16, 16));

        Entity proj = VehicleCommon.SpawnProjectile(owner, pilot, new Vector3(100, 0, 20), new Vector3(0, 0, 100),
            damage: 90f, radius: 125f, force: 0f, size: 1f, DeathTypes.VhWakiGun, health: 0f, lifetime: 0f);
        proj.Touch!(proj, cap.Victim); // contact detonation -> RadiusDamage(deathTag: VhWakiGun)

        Assert.Equal(DeathTypes.VhWakiGun, cap.DeathType);
        Assert.True(DeathTypes.IsVehicle(cap.DeathType));
        Assert.Equal("DEATH_MURDER_VH_WAKI_GUN", DeathMessages.SelectSpecial(cap.DeathType, murder: true));
        // all.inc: VH_WAKI_GUN registers murder-only (death_msgself == NULL) -> generic self fallback.
        Assert.Equal("DEATH_SELF_GENERIC", DeathMessages.SelectSpecial(cap.DeathType, murder: false));
    }

    // ================================================================================================
    //  end-to-end: the kill feed (Scores.Obituary over Combat.Death) picks the categorized lines
    // ================================================================================================

    [Fact]
    public void Obituary_PicksMonster_Turret_Vehicle_KillFeedLines_NotAWeaponLine()
    {
        Scores s = SubscribedScores(teamGame: false);
        Player killer = NewPlayer("Killer");
        s.Register(killer);

        // A representative MURDER of each category, tagged with the deathtype the corresponding attack now deals.
        FireMurder(s, killer, "MonVictim", DeathTypes.MonsterSpider);
        FireMurder(s, killer, "TurVictim", DeathTypes.Turret);
        FireMurder(s, killer, "VehVictim", DeathTypes.VhWakiRocket);

        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "INFO_DEATH_MURDER_MONSTER");      // monster
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "INFO_DEATH_MURDER_CHEAT");        // turret
        Assert.Contains(_rec.Log, d => d.Notification.RegistryName == "INFO_DEATH_MURDER_VH_WAKI_ROCKET"); // vehicle
        // None of the categorized kills fell back to a generic weapon kill-feed line.
        Assert.DoesNotContain(_rec.Log, d => d.Notification.RegistryName.StartsWith("INFO_WEAPON_"));
    }

    // ---- Scores obituary harness (mirrors ObituaryEmissionTests) ------------------------------------

    private Scores SubscribedScores(bool teamGame)
    {
        var s = new Scores();
        s.SubscribeToDeaths(teamGame, ownsScore: false);
        _subscribed.Add(s);
        return s;
    }

    private static Player NewPlayer(string name, int team = 0)
    {
        var p = new Player { NetName = name, Flags = EntFlags.Client };
        p.Team = team;
        p.SetResource(ResourceType.Health, 100f);
        p.SetResource(ResourceType.Armor, 25f);
        return p;
    }

    private void FireMurder(Scores s, Player attacker, string victimName, string deathType)
    {
        var victim = NewPlayer(victimName);
        s.Register(victim);
        var ev = new DeathEvent { Victim = victim, Attacker = attacker, DeathType = deathType };
        Combat.Death.Call(ref ev);
    }
}

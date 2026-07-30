using System;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first coverage for the turret target-selection pipeline (<see cref="TurretAI.ValidTarget"/> /
/// <see cref="TurretAI.ScoreTarget"/> / <see cref="TurretAI.SelectTarget"/>, ports of
/// <c>turret_validate_target</c> / <c>turret_targetscore_generic</c> / <c>turret_select_target</c> in
/// common/turrets/sv_turrets.qc). NOTE: the turret subsystem has no live engine caller yet — these
/// tests are its first consumer. Harness mirrors MonsterDamageDeathTests: an EngineServices on a flat
/// floor with GameInit.Boot.
/// </summary>
[Collection("GlobalState")]
public class TurretTargetingTests
{
    private static EngineServices Boot()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        var services = new EngineServices(world);
        GameInit.Boot(services);
        Prandom.Seed(0xC0FFEE);
        return services;
    }

    private static Entity SpawnTurret(Vector3 origin, float team = 0f)
    {
        Entity t = Api.Entities.Spawn();
        t.ClassName = "turret_test";
        t.Origin = origin;
        Api.Entities.SetOrigin(t, origin);
        // QC turret_initialize default: a nonzero team so SAME_TEAM gating works without teamplay
        t.Team = team == 0f ? float.MaxValue : team;
        TurretAI.State(t).ShotOrg = origin;
        return t;
    }

    private static Entity SpawnPlayerTarget(Vector3 origin, float team = 0f, float health = 100f)
    {
        Entity p = Api.Entities.Spawn();
        p.ClassName = "player";
        p.Flags |= EntFlags.Client;       // TurretAI.IsPlayer
        p.Health = health;
        p.TakeDamage = DamageMode.Aim;
        p.Team = team;
        p.Origin = origin;
        Api.Entities.SetOrigin(p, origin);
        return p;
    }

    private const int PlayersInRange = TurretAI.SelectPlayers | TurretAI.SelectRangeLimits;

    // ---------------------------------------------------------------- ValidTarget gates

    [Fact]
    public void ValidTarget_RejectsNullSelfAndOwnProjectiles()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));

        Assert.False(TurretAI.ValidTarget(turret, null, PlayersInRange, 0f, 5000f));
        Assert.False(TurretAI.ValidTarget(turret, turret, PlayersInRange, 0f, 5000f));

        Entity ownMissile = Api.Entities.Spawn();
        ownMissile.Owner = turret;
        ownMissile.TakeDamage = DamageMode.Yes;
        ownMissile.Health = 1f;
        Assert.False(TurretAI.ValidTarget(turret, ownMissile, TurretAI.SelectMissiles, 0f, 5000f));
    }

    [Fact]
    public void ValidTarget_RejectsDead_Undamageable_AndNoTarget()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));

        Entity dead = SpawnPlayerTarget(new Vector3(200, 0, 50), health: 0f);
        Assert.False(TurretAI.ValidTarget(turret, dead, PlayersInRange, 0f, 5000f));

        Entity noDamage = SpawnPlayerTarget(new Vector3(200, 0, 50));
        noDamage.TakeDamage = DamageMode.No;
        Assert.False(TurretAI.ValidTarget(turret, noDamage, PlayersInRange, 0f, 5000f));

        Entity notarget = SpawnPlayerTarget(new Vector3(200, 0, 50));
        notarget.Flags |= EntFlags.NoTarget;
        Assert.False(TurretAI.ValidTarget(turret, notarget, PlayersInRange, 0f, 5000f));

        Entity ok = SpawnPlayerTarget(new Vector3(200, 0, 50));
        Assert.True(TurretAI.ValidTarget(turret, ok, PlayersInRange, 0f, 5000f));
    }

    [Fact]
    public void ValidTarget_PlayersNeedThePlayersFlag_MissilesTheMissileFlags()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));

        Entity player = SpawnPlayerTarget(new Vector3(200, 0, 50));
        Assert.False(TurretAI.ValidTarget(turret, player, TurretAI.SelectRangeLimits, 0f, 5000f));
        Assert.True(TurretAI.ValidTarget(turret, player, PlayersInRange, 0f, 5000f));

        // a missile = FL_PROJECTILE stand-in (Item flag) + an owner
        Entity enemyOwner = SpawnPlayerTarget(new Vector3(900, 900, 50));
        Entity missile = Api.Entities.Spawn();
        missile.Flags |= EntFlags.Item;
        missile.Owner = enemyOwner;
        missile.TakeDamage = DamageMode.Yes;
        missile.Health = 1f;
        missile.Origin = new Vector3(150, 0, 50);
        Api.Entities.SetOrigin(missile, missile.Origin);

        Assert.False(TurretAI.ValidTarget(turret, missile, PlayersInRange, 0f, 5000f));          // not a player
        Assert.True(TurretAI.ValidTarget(turret, missile, TurretAI.SelectMissiles, 0f, 5000f));
        // SelectMissilesOnly rejects non-missiles even when players are also allowed
        Assert.False(TurretAI.ValidTarget(turret, player,
            TurretAI.SelectPlayers | TurretAI.SelectMissilesOnly, 0f, 5000f));
        Assert.True(TurretAI.ValidTarget(turret, missile, TurretAI.SelectMissilesOnly, 0f, 5000f));
    }

    [Fact]
    public void ValidTarget_TeamCheck_RejectsSameTeam_OwnTeamRequiresIt()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50), team: 5f);

        Entity friend = SpawnPlayerTarget(new Vector3(200, 0, 50), team: 5f);
        Entity foe = SpawnPlayerTarget(new Vector3(200, 0, 50), team: 14f);

        int combat = PlayersInRange | TurretAI.SelectTeamCheck;
        Assert.False(TurretAI.ValidTarget(turret, friend, combat, 0f, 5000f));
        Assert.True(TurretAI.ValidTarget(turret, foe, combat, 0f, 5000f));

        int support = combat | TurretAI.SelectOwnTeam;
        Assert.True(TurretAI.ValidTarget(turret, friend, support, 0f, 5000f));
        Assert.False(TurretAI.ValidTarget(turret, foe, support, 0f, 5000f));
    }

    [Fact]
    public void ValidTarget_RangeLimits_GateMinAndMax()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));

        Entity tooClose = SpawnPlayerTarget(new Vector3(50, 0, 50));
        Entity inRange = SpawnPlayerTarget(new Vector3(500, 0, 50));
        Entity tooFar = SpawnPlayerTarget(new Vector3(3000, 0, 50));

        Assert.False(TurretAI.ValidTarget(turret, tooClose, PlayersInRange, 100f, 2000f));
        Assert.True(TurretAI.ValidTarget(turret, inRange, PlayersInRange, 100f, 2000f));
        Assert.False(TurretAI.ValidTarget(turret, tooFar, PlayersInRange, 100f, 2000f));
        // without SelectRangeLimits the same distances all pass
        Assert.True(TurretAI.ValidTarget(turret, tooClose, TurretAI.SelectPlayers, 100f, 2000f));
        Assert.True(TurretAI.ValidTarget(turret, tooFar, TurretAI.SelectPlayers, 100f, 2000f));
    }

    [Fact]
    public void ValidTarget_LineOfSight_BlockedByWorld()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));

        int flags = PlayersInRange | TurretAI.SelectLos;
        Entity visible = SpawnPlayerTarget(new Vector3(400, 0, 50));
        Assert.True(TurretAI.ValidTarget(turret, visible, flags, 0f, 5000f));

        // the floor slab (z -64..0) blocks the eye trace to a target buried beneath it
        Entity hidden = SpawnPlayerTarget(new Vector3(400, 0, -200));
        Assert.False(TurretAI.ValidTarget(turret, hidden, flags, 0f, 5000f));
        // the same buried target passes once LOS gating is off (proving LOS was the rejector)
        Assert.True(TurretAI.ValidTarget(turret, hidden, PlayersInRange, 0f, 5000f));
    }

    [Fact]
    public void ValidTarget_GrapplinghookIsNeverATarget()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        Entity hook = SpawnPlayerTarget(new Vector3(300, 0, 50));
        hook.ClassName = "grapplinghook";
        Assert.False(TurretAI.ValidTarget(turret, hook, PlayersInRange, 0f, 5000f));
    }

    // ---------------------------------------------------------------- ScoreTarget formula

    private static TurretParams Params(
        float rangeMin = 0.1f, float rangeMax = 5000f, float rangeOptimal = 1000f,
        float rangeBias = 1f, float sameBias = 1f, float angleBias = 1f,
        float missileBias = 1f, float playerBias = 1f, float aimMaxRot = 90f)
        => new(PlayersInRange | TurretAI.SelectMissiles, rangeMin, rangeMax, shotDamage: 10f, refire: 1f,
            aimSpeed: 36f, fireToleranceDist: 50f, lead: false,
            rangeOptimal: rangeOptimal, aimMaxRot: aimMaxRot,
            rangeBias: rangeBias, sameBias: sameBias, angleBias: angleBias,
            missileBias: missileBias, playerBias: playerBias);

    [Fact]
    public void ScoreTarget_MatchesTheQcBiasFormula()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        // aim the body straight at the target so the angular score term is exactly 1
        Entity target = SpawnPlayerTarget(new Vector3(1000, 0, 50));   // dist == rangeOptimal
        turret.Angles = QMath.VecToAngles(QMath.Normalize(target.Origin - turret.Origin));

        TurretParams p = Params(rangeBias: 0.25f, angleBias: 0.5f, playerBias: 1f, missileBias: 0f);
        float score = TurretAI.ScoreTarget(turret, target, in p);

        // QC: d_score = min(ikr,d)/max(ikr,d) = 1; a_score = 1 - thadf/aim_maxrot = 1;
        //     m_score = 0 (missilebias 0); p_score = 1.
        // score = 1*0.25 + 1*0.5 + 0*0 + 1*1 = 1.75
        Assert.Equal(1.75f, score, 3);
    }

    [Fact]
    public void ScoreTarget_DistanceScore_PeaksAtOptimalRange()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        TurretParams p = Params(rangeBias: 1f, angleBias: 0f, playerBias: 0f, missileBias: 0f);

        Entity atOptimal = SpawnPlayerTarget(new Vector3(1000, 0, 50));
        Entity atDouble = SpawnPlayerTarget(new Vector3(2000, 0, 50));
        Entity atHalf = SpawnPlayerTarget(new Vector3(500, 0, 50));
        turret.Angles = Vector3.Zero;

        float sOpt = TurretAI.ScoreTarget(turret, atOptimal, in p);
        float sDouble = TurretAI.ScoreTarget(turret, atDouble, in p);
        float sHalf = TurretAI.ScoreTarget(turret, atHalf, in p);

        Assert.Equal(1f, sOpt, 2);       // min/max of equal distances = 1
        Assert.Equal(0.5f, sDouble, 2);  // min(1000,2000)/max(1000,2000)
        Assert.Equal(0.5f, sHalf, 2);    // symmetric: closer than optimal also scores down
    }

    [Fact]
    public void ScoreTarget_BeyondRangeMax_CollapsesByAFactor1000()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        TurretParams p = Params(rangeMax: 800f, rangeOptimal: 400f, angleBias: 0f, playerBias: 0f, missileBias: 0f);

        Entity beyond = SpawnPlayerTarget(new Vector3(1600, 0, 50));
        float score = TurretAI.ScoreTarget(turret, beyond, in p);
        // d_score = 400/1600 = 0.25, then *0.001 because vlen(shotorg - target) > target_range
        Assert.Equal(0.25f * 0.001f, score, 6);
    }

    // ---------------------------------------------------------------- SelectTarget

    [Fact]
    public void SelectTarget_PicksTheBestScoringValidTarget()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        turret.Angles = Vector3.Zero;
        TurretParams p = Params(rangeOptimal: 500f, angleBias: 0f);

        Entity nearOptimal = SpawnPlayerTarget(new Vector3(500, 0, 50));
        Entity farAway = SpawnPlayerTarget(new Vector3(2500, 0, 50));

        Entity? picked = TurretAI.SelectTarget(turret, p.SelectFlags, in p);
        Assert.Same(nearOptimal, picked);
        _ = farAway;
    }

    [Fact]
    public void SelectTarget_ReturnsNullWhenNothingValid()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        TurretParams p = Params();
        Assert.Null(TurretAI.SelectTarget(turret, p.SelectFlags, in p));

        // a dead body in range still yields nothing
        SpawnPlayerTarget(new Vector3(400, 0, 50), health: 0f);
        Assert.Null(TurretAI.SelectTarget(turret, p.SelectFlags, in p));
    }

    [Fact]
    public void SelectTarget_CurrentEnemyIsStickyViaSameBias()
    {
        Boot();
        Entity turret = SpawnTurret(new Vector3(0, 0, 50));
        turret.Angles = Vector3.Zero;

        // two equally-scored targets (same distance, angle term disabled); the current enemy's score is
        // seeded * samebias, so a marginally-better newcomer cannot displace it.
        TurretParams p = Params(rangeOptimal: 600f, angleBias: 0f, sameBias: 2f);
        Entity current = SpawnPlayerTarget(new Vector3(600, 0, 50));
        Entity rival = SpawnPlayerTarget(new Vector3(0, 600, 50));
        turret.Enemy = current;

        Entity? picked = TurretAI.SelectTarget(turret, p.SelectFlags, in p);
        Assert.Same(current, picked);
        _ = rival;
    }
}

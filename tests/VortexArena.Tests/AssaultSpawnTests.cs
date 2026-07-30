// Tests for T36: the Assault objective-chain map spawnfuncs (target_objective / target_objective_decrease /
// func_assault_destructible / target_assault_roundend / roundstart, + info_player_attacker/defender) wiring the
// BSP entity lump through GametypeObjectiveSpawns.Sink → GameWorld.WireObjectiveSpawns (Assault arm) → the
// Assault POJO chain, with the destructible→decreaser→objective links resolved by the post-spawn pass
// (GameWorld.Boot → Assault.ResolveObjectiveGraph, QC INITPRIO_FINDTARGET).
//
// Port of: common/gametypes/gametype/assault/sv_assault.qc spawnfuncs (lines 287-394).

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

[Collection("GlobalState")]
public sealed class AssaultSpawnTests
{
    private static EntityDict Dict(string cls, Vector3 origin = default, params (string k, string v)[] fields)
    {
        var d = new EntityDict { ClassName = cls, Origin = origin };
        foreach (var (k, v) in fields) d.Fields[k] = v;
        return d;
    }

    /// <summary>
    /// A small two-objective Assault map (the canonical chain): each objective has its own destructible+decreaser.
    /// Destroying "core1" (the head) activates "core2" (the final); destroying core2 fires the roundend. Entities
    /// are listed in a DELIBERATELY shuffled order (destructibles + decreasers before their objectives) to prove
    /// the deferred INITPRIO_FINDTARGET resolution is spawn-order-independent.
    /// </summary>
    private static GameWorld BootAssaultMap()
    {
        var ents = new List<EntityDict>
        {
            // destructibles BEFORE their decreasers/objectives (arbitrary spawn order):
            Dict("func_assault_destructible", new Vector3(10, 0, 0), ("target", "dec1"), ("health", "100")),
            Dict("func_assault_destructible", new Vector3(20, 0, 0), ("target", "dec2"), ("health", "100")),
            // decreasers BEFORE their objectives:
            Dict("target_objective_decrease", default, ("targetname", "dec1"), ("target", "core1"), ("dmg", "150")),
            Dict("target_objective_decrease", default, ("targetname", "dec2"), ("target", "core2"), ("dmg", "150")),
            // objectives (core1 head → core2 final):
            Dict("target_objective", default, ("targetname", "core1"), ("target", "core2")),
            Dict("target_objective", default, ("targetname", "core2")),
            Dict("target_assault_roundend", default, ("targetname", "roundend")),
            Dict("target_assault_roundstart"),
        };
        var world = new GameWorld(new CollisionWorld(), ents);
        world.Boot("as"); // Assault
        return world;
    }

    [Fact]
    public void SpawnfuncsBuildTheObjectiveChain()
    {
        GameWorld world = BootAssaultMap();
        var aslt = Assert.IsType<Assault>(world.GameType);

        Assert.Equal(2, aslt.Objectives.Count);
        Assert.Equal(2, aslt.Decreasers.Count);
        Assert.Equal(2, aslt.Destructibles.Count);
        Assert.True(aslt.HasRoundEnd);

        // QC default attacker = NUM_TEAM_1 (red).
        Assert.Equal(Teams.Red, aslt.AttackerTeam);
        Assert.Equal(Teams.Blue, aslt.DefenderTeam);
    }

    [Fact]
    public void DeferredResolution_LinksDestructibleToDecreaserToObjective_RegardlessOfSpawnOrder()
    {
        GameWorld world = BootAssaultMap();
        var aslt = (Assault)world.GameType!;

        // The dec1 destructible (spawned FIRST) resolved its decreaser by name; the decreaser resolved its objective.
        Assault.Decreaser dec1 = aslt.Decreasers.First(d => d.Name == "dec1");
        Assault.Destructible wall1 = aslt.Destructibles.First(w => w.Target == "dec1");
        Assert.Same(dec1, wall1.DecreaserRef);
        Assert.NotNull(dec1.ObjectiveRef);
        Assert.Equal("core1", dec1.ObjectiveRef!.Name);

        // The mapper's custom .health / .dmg were plumbed through ApplyDictFields (not the QC defaults).
        Assert.Equal(100f, wall1.MaxHealth);
        Assert.Equal(150f, dec1.Dmg);
    }

    [Fact]
    public void DecreaserDmg_DefaultsTo101_WhenUnset()
    {
        // No .dmg key → QC `if(!this.dmg) this.dmg = 101;`.
        var ents = new List<EntityDict>
        {
            Dict("target_objective", default, ("targetname", "core1")),
            Dict("target_objective_decrease", default, ("targetname", "dec1"), ("target", "core1")),
            Dict("target_assault_roundend"),
        };
        var world = new GameWorld(new CollisionWorld(), ents);
        world.Boot("as");
        var aslt = (Assault)world.GameType!;
        Assert.Equal(101f, Assert.Single(aslt.Decreasers).Dmg);
    }

    [Fact]
    public void PostSpawn_ArmsTheFirstObjective()
    {
        GameWorld world = BootAssaultMap();
        var aslt = (Assault)world.GameType!;

        // ResolveObjectiveGraph ends with ResetObjectives (QC roundstart): the chain HEAD (core1, which nothing
        // else targets) is active; core2 stays inactive until core1 falls.
        Assault.Objective core1 = aslt.Objectives.First(o => o.Name == "core1");
        Assault.Objective core2 = aslt.Objectives.First(o => o.Name == "core2");
        Assert.True(core1.Active, "the head objective should be armed at round start");
        Assert.False(core2.Active);

        // The head's destructible (dec1, aimed at core1) is shootable; the final objective's destructible (dec2,
        // aimed at the still-inactive core2) is NOT yet armed.
        Assault.Destructible wall1 = aslt.Destructibles.First(w => w.Target == "dec1");
        Assault.Destructible wall2 = aslt.Destructibles.First(w => w.Target == "dec2");
        Assert.True(wall1.Active, "the head objective's destructible should be shootable");
        Assert.False(wall2.Active, "the final objective's destructible stays inert until its objective activates");
    }

    [Fact]
    public void WinPath_DestroyingTheChain_EndsWithAttackersWinning()
    {
        GameWorld world = BootAssaultMap();
        var aslt = (Assault)world.GameType!;

        Assault.Destructible wall1 = aslt.Destructibles.First(w => w.Target == "dec1");
        Assault.Destructible wall2 = aslt.Destructibles.First(w => w.Target == "dec2");
        Assault.Objective core1 = aslt.Objectives.First(o => o.Name == "core1");
        Assault.Objective core2 = aslt.Objectives.First(o => o.Name == "core2");

        // Shoot the head destructible to 0 as the attacking (red) team → fires dec1 → core1 (100hp, dmg 150) is
        // destroyed → activates core2 (which arms wall2).
        Assert.True(aslt.DamageDestructible(wall1, Teams.Red, 100f));
        Assert.True(core1.Destroyed);
        Assert.True(core2.Active, "destroying the head objective activates the next");
        Assert.True(wall2.Active, "activating core2 re-arms its destructible");

        // Shoot the final destructible → fires dec2 → core2 destroyed → terminal target is the round-end.
        Assert.True(aslt.DamageDestructible(wall2, Teams.Red, 100f));
        Assert.True(core2.Destroyed);

        // The chain's terminal target is the round-end → the attackers (red) win the round (round 1 of 2).
        Assert.Equal(Teams.Red, aslt.WinningTeam);
    }

    [Fact]
    public void AttackerWin_SchedulesSecondRound_ThenSwapsRolesAfterTheDelay()
    {
        // QC WinningCondition_Assault: a round-1 attacker core-destruction (non-campaign) does NOT end the match —
        // it freezes for AS_ROUND_DELAY (5s) then assault_new_round swaps roles for round 2 (DriveFrame fires it).
        GameWorld world = BootAssaultMap();
        var aslt = (Assault)world.GameType!;

        Assault.Destructible wall1 = aslt.Destructibles.First(w => w.Target == "dec1");
        Assault.Destructible wall2 = aslt.Destructibles.First(w => w.Target == "dec2");

        // Destroy the whole chain as red → round 1 won by the attackers (red).
        Assert.True(aslt.DamageDestructible(wall1, Teams.Red, 100f));
        Assert.True(aslt.DamageDestructible(wall2, Teams.Red, 100f));
        Assert.Equal(Teams.Red, aslt.WinningTeam);

        // Round 1 win → a second round is PENDING (the 5s freeze), the match has NOT ended, roles not yet swapped.
        Assert.True(aslt.SecondRoundPending, "an attacker round-1 win schedules round 2 (the as_round freeze)");
        Assert.False(aslt.MatchEnded);
        Assert.Equal(Teams.Red, aslt.AttackerTeam);

        // A host restart callback stands in for ReadyRestart_force(true).
        bool restarted = false;
        aslt.OnSecondRoundRestart = () => restarted = true;

        // Drive frames during the freeze: nothing flips until the delay elapses.
        aslt.GameStartTime = 0f;
        aslt.DriveFrame(1f);
        Assert.True(aslt.SecondRoundPending);
        Assert.Equal(Teams.Red, aslt.AttackerTeam);

        // Once AS_ROUND_DELAY has passed (the as_round nextthink), DriveFrame runs assault_new_round: roles swap
        // (blue now attacks), the win latch clears, the objective chain re-arms, and the host restart fires.
        // (A fresh booted world's clock is at t=0, so the due time is RoundDelay; drive well past it.)
        aslt.DriveFrame(Assault.RoundDelay + 100f);
        Assert.False(aslt.SecondRoundPending);
        Assert.Equal(1, aslt.State.Round);
        Assert.Equal(Teams.Blue, aslt.AttackerTeam);
        Assert.Equal(0, aslt.WinningTeam);
        Assert.True(restarted, "round 2 runs the host's ReadyRestart_force(true) restart");
        // The chain head is armed again for the new attackers.
        Assert.True(aslt.Objectives.First(o => o.Name == "core1").Active);
        Assert.True(aslt.Destructibles.First(w => w.Target == "dec1").Active);
    }

    [Fact]
    public void FuncAssaultWall_HidesWhenItsObjectiveIsDestroyed_AndIsSolidWhileItLives()
    {
        // QC func_assault_wall + assault_wall_think: the wall is SOLID_BSP/visible while its objective lives and
        // hides (model="" + SOLID_NOT) once the objective is destroyed (RES_HEALTH < 0). The wall watches "core1".
        var ents = new List<EntityDict>
        {
            Dict("func_assault_wall", new Vector3(50, 0, 0), ("target", "core1"), ("model", "*3")),
            Dict("func_assault_destructible", new Vector3(10, 0, 0), ("target", "dec1"), ("health", "100")),
            Dict("target_objective_decrease", default, ("targetname", "dec1"), ("target", "core1"), ("dmg", "150")),
            Dict("target_objective", default, ("targetname", "core1")),
            Dict("target_assault_roundend", default, ("targetname", "roundend")),
            Dict("target_assault_roundstart"),
        };
        var world = new GameWorld(new CollisionWorld(), ents);
        world.Boot("as");
        var aslt = (Assault)world.GameType!;

        // The wall edict was kept in the world (not retired) and linked to its objective.
        Assault.Wall wall = Assert.Single(aslt.Walls);
        Assert.Equal("core1", wall.ObjectiveRef!.Name);
        Entity edict = aslt.Walls[0].WorldEntity!;
        Assert.False(edict.IsFreed);

        // While the objective lives (inactive or active, health >= 0) the wall is solid + visible.
        aslt.DriveWalls();
        Assert.Equal(Solid.Bsp, edict.Solid);

        // Shoot the destructible → core1 destroyed (health -1) → DriveWalls hides the wall (non-solid, model cleared).
        Assault.Destructible wall1 = aslt.Destructibles.First(w => w.Target == "dec1");
        Assert.True(aslt.DamageDestructible(wall1, Teams.Red, 100f));
        Assert.True(aslt.Objectives.First(o => o.Name == "core1").Destroyed);
        aslt.DriveWalls();
        Assert.Equal(Solid.Not, edict.Solid);
        Assert.Equal("", edict.Model);
    }

    [Fact]
    public void Destructible_StaysInWorld_AsAShootableEntity_LinkedToItsPojo()
    {
        GameWorld world = BootAssaultMap();
        var aslt = (Assault)world.GameType!;

        // Unlike flags/checkpoints, a func_assault_destructible KEEPS its world edict (the bot Assault role finds it
        // by classname and the live damage bridge maps it back to the Destructible POJO via DestructibleFor).
        var walls = Api.Entities.FindByClass("func_assault_destructible").ToList();
        Assert.Equal(2, walls.Count);
        Assert.All(walls, w =>
        {
            Assert.Equal(Solid.Bsp, w.Solid);             // shootable
            Assert.Equal(DamageMode.Aim, w.TakeDamage);   // damageable
            Assert.NotNull(aslt.DestructibleFor(w));      // edict → POJO mapping for the damage bridge
        });
    }

    [Fact]
    public void LiveDamage_ShootingTheWall_DrivesTheChainThroughTheDamagePipeline()
    {
        // The full live path: GameWorld.Boot installs the real DamageSystem, and the func_assault_destructible
        // edict carries a GtEventDamage callback. An attacking (red) player shelling the wall through Combat.Damage
        // → DamageSystem.EventDamage → GtEventDamage → DamageDestructible whittles the objective and fires the chain.
        GameWorld world = BootAssaultMap();
        var aslt = (Assault)world.GameType!;

        XonoticGodot.Common.Framework.Entity wall1 = Api.Entities.FindByClass("func_assault_destructible")
            .First(w => aslt.DestructibleFor(w) is { } d && d.Target == "dec1");
        Assault.Objective core1 = aslt.Objectives.First(o => o.Name == "core1");

        var red = new Player { NetName = "red", Team = Teams.Red, Health = 100f };

        // Shell the wall (100hp) to 0 through the shared damage pipeline.
        XonoticGodot.Common.Gameplay.Damage.Combat.Damage(wall1, null, red, 100f,
            XonoticGodot.Common.Gameplay.Damage.DeathTypes.Generic, wall1.Origin, System.Numerics.Vector3.Zero);

        // The wall broke → its decreaser fired → core1 (dmg 150 > 100hp) destroyed → core2 activates.
        Assert.True(aslt.DestructibleFor(wall1)!.Destroyed);
        Assert.True(core1.Destroyed);
        Assert.True(aslt.Objectives.First(o => o.Name == "core2").Active);
        // The world edict reads as broken (non-solid) once destroyed.
        Assert.Equal(Solid.Not, wall1.Solid);
    }

    [Fact]
    public void WrongTeam_CannotDamageObjectives()
    {
        GameWorld world = BootAssaultMap();
        var aslt = (Assault)world.GameType!;
        Assault.Destructible wall = aslt.Destructibles[0];

        // The DEFENDING team (blue) shooting the destructible does nothing (QC actor.team != attacker_team).
        Assert.False(aslt.DamageDestructible(wall, Teams.Blue, 1000f));
        Assert.False(wall.Destroyed);
    }

    [Fact]
    public void InfoPlayerAttackerDefender_BecomeTeamSpawnPoints()
    {
        var ents = new List<EntityDict>
        {
            Dict("info_player_attacker", new Vector3(100, 0, 24)),
            Dict("info_player_defender", new Vector3(-100, 0, 24)),
            Dict("target_objective", default, ("targetname", "core1")),
            Dict("target_assault_roundend"),
        };
        var world = new GameWorld(new CollisionWorld(), ents);
        world.Boot("as");

        // QC info_player_attacker/defender set this.team = NUM_TEAM_1/2 then chain to info_player_deathmatch.
        // The port retags them to info_player_deathmatch with the team stamped, so SpawnSystem finds them as
        // team spawn points. Confirm the edicts survived (kept) with the right classname + team.
        var dmSpots = Api.Entities.FindByClass("info_player_deathmatch").ToList();
        Assert.Contains(dmSpots, e => (int)e.Team == Teams.Red && e.Origin.X > 0);
        Assert.Contains(dmSpots, e => (int)e.Team == Teams.Blue && e.Origin.X < 0);
    }

    [Fact]
    public void NonAssaultGametype_DropsObjectiveEntities()
    {
        // QC each spawnfunc is gated `if(!g_assault){delete;return;}`. In a DM match the Assault sink isn't wired
        // (the switch default is null), so the objective edicts are simply ignored (no chain).
        var ents = new List<EntityDict>
        {
            Dict("target_objective", default, ("targetname", "core1")),
            Dict("func_assault_destructible", default, ("target", "dec1")),
        };
        var world = new GameWorld(new CollisionWorld(), ents);
        world.Boot("dm");
        // No Assault gametype, so nothing to assert on a chain — just that boot didn't throw and the mode is FFA.
        Assert.IsNotType<Assault>(world.GameType);
    }
}

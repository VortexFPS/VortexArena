using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests for T18 Onslaught DEPTH: the control-point capture-by-build combat (QC ons_ControlPoint_Touch →
/// Icon_Spawn → BuildThink → capture / Icon_Damage destroy) and the generator damage pipeline routed through
/// the shared <see cref="DamageSystem"/> via <see cref="Entity.GtEventDamage"/> (QC ons_GeneratorDamage:
/// shield-block + destruction + the round-win credit).
/// </summary>
[Collection("GlobalState")]
public class OnslaughtCombatTests
{
    private static EngineServices Facade()
    {
        var es = new EngineServices(new CollisionWorld());
        Api.Services = es;
        Combat.System = new DamageSystem(); // route Combat.Damage through the real pipeline (GtEventDamage)
        return es;
    }

    private static Player NewPlayer(int team) =>
        new Player { Team = team, Flags = EntFlags.Client };

    [Fact]
    public void RadarUseRespectsAnEarlierMutatorThatConsumedThePress()
    {
        Facade();
        var ons = new Onslaught();
        ons.Activate();
        ons.CpCombat.SpawnGenerator(Teams.Red, Vector3.Zero);
        Player player = NewPlayer(Teams.Red);
        player.GtTeleportAntispam = -1f;
        int openings = 0;
        ons.RadarRequested += _ => openings++;
        HookHandler<MutatorHooks.PlayerUseKeyArgs> consume = (ref MutatorHooks.PlayerUseKeyArgs _) => true;

        try
        {
            MutatorHooks.PlayerUseKey.Add(consume, HookOrder.First);
            Assert.True(MutatorHooks.FirePlayerUseKey(player));
            Assert.Equal(0, openings);

            MutatorHooks.PlayerUseKey.Remove(consume);
            Assert.True(MutatorHooks.FirePlayerUseKey(player));
            Assert.Equal(1, openings);
        }
        finally
        {
            MutatorHooks.PlayerUseKey.Remove(consume);
            ons.Deactivate();
        }
    }

    [Fact]
    public void MapGeneratorPaletteIndicesPowerNativePlayers()
    {
        static EntityDict Spawn(string classname, Vector3 origin, params (string Key, string Value)[] fields)
        {
            var entity = new EntityDict { ClassName = classname, Origin = origin };
            foreach (var (key, value) in fields)
                entity.Fields[key] = value;
            return entity;
        }

        var world = new GameWorld(new CollisionWorld(),
        [
            Spawn("onslaught_generator", Vector3.Zero, ("team", "5"), ("targetname", "red")),
            Spawn("onslaught_generator", new Vector3(900, 0, 0), ("team", "14"), ("targetname", "blue")),
            Spawn("onslaught_controlpoint", new Vector3(400, 0, 0), ("targetname", "middle")),
            Spawn("onslaught_link", Vector3.Zero, ("target", "red"), ("target2", "middle")),
            Spawn("onslaught_link", Vector3.Zero, ("target", "blue"), ("target2", "middle"))
        ]);
        world.Boot("ons");

        var onslaught = Assert.IsType<Onslaught>(world.GameType);
        Assert.Equal(Teams.Red, onslaught.NodeByName("red")!.Team);
        Assert.Equal(Teams.Blue, onslaught.NodeByName("blue")!.Team);
        Entity pad = Assert.Single(world.Services.Entities.FindByClass("onslaught_controlpoint"));
        var combat = onslaught.CpCombat;
        var shields = world.Services.Entities.FindByClass(OnslaughtControlPoint.ShieldClassName);
        Entity blueShield = Assert.Single(shields, s => s.GtShieldFlag == combat.GeneratorEntity(Teams.Blue));
        Entity pointShield = Assert.Single(shields, s => s.GtShieldFlag == pad);
        Assert.True(combat.CaptureShieldVisibleTo(blueShield, Teams.Red));
        Assert.False(combat.CaptureShieldVisibleTo(blueShield, Teams.Blue));
        Assert.False(combat.CaptureShieldVisibleTo(pointShield, Teams.Red));
        Assert.True(combat.CaptureShieldVisibleTo(pointShield, Teams.None));

        combat.ControlPointTouch(pad, NewPlayer(Teams.Red));
        Assert.Equal(Teams.Red, combat.IconFor(pad.GtPointId)!.GtHomeTeam);
        onslaught.CaptureControlPoint(pad.GtPointId, Teams.Red);
        Assert.False(combat.CaptureShieldVisibleTo(blueShield, Teams.Red));
    }

    /// <summary>
    /// A red player touching an enemy-attackable control point spawns a build icon that, once it finishes
    /// building (BuildThink ticks), flips the point to red and credits the capture (ONS_CAPS + SCORE +10).
    /// </summary>
    [Fact]
    public void ControlPoint_BuildToCompletion_CapturesAndCredits()
    {
        Facade();
        var ons = new Onslaught();
        // Graph: red generator — control point. The CP neighbors the red generator so red can build it.
        Onslaught.OnsNode gen = ons.AddGenerator(Teams.Red);
        Onslaught.OnsNode cp = ons.AddControlPoint(1);
        ons.Link(gen, cp);
        ons.UpdateLinks();
        ons.Activate();

        // The CP must be unshielded/attackable by red (powered red generator neighbor exposed it).
        Assert.False(cp.Shielded);
        Assert.True(ons.IsAttackable(cp, Teams.Red));

        var red = NewPlayer(Teams.Red);
        Entity? cpEnt = ons.CpCombat.SpawnControlPoint(1, new Vector3(100, 0, 0));
        Assert.NotNull(cpEnt);

        // Touch starts the build icon (QC ons_ControlPoint_Touch → Icon_Spawn). The point is NOT captured yet.
        ons.CpCombat.ControlPointTouch(cpEnt!, red);
        Entity? icon = ons.CpCombat.IconFor(1);
        Assert.NotNull(icon);
        Assert.False(icon!.GtIconBuilt);
        Assert.False(cp.Captured);

        const string intact = "models/onslaught/controlpoint_icon.md3";
        var blue = NewPlayer(Teams.Blue);
        Combat.Damage(icon, null, blue, 1f, DeathTypes.Generic, icon.Origin, Vector3.Zero);
        Assert.Equal(intact, icon.Model); // Only opacity represents the icon's health while building.

        ons.CpCombat.IconBuildThink(icon);
        Assert.False(icon.GtIconBuilt);
        Assert.Equal("models/onslaught/controlpoint_pad2.md3", cpEnt.Model);
        Assert.True(cpEnt.Maxs.Z < icon.Origin.Z + icon.Mins.Z - cpEnt.Origin.Z);

        // Drive the build think enough times to finish.
        for (int i = 0; i < 200 && !icon.GtIconBuilt; i++)
            ons.CpCombat.IconBuildThink(icon);

        Assert.True(icon.GtIconBuilt);          // QC: iscaptured = true once health reaches max
        Assert.True(cp.Captured);               // the point flipped
        Assert.Equal(Teams.Red, cp.Team);
        Assert.Equal(Teams.Red, ons.ControlPointOwner(1));

        // Captured icons swap only below each damage threshold and heal back to the intact mesh.
        cp.Shielded = false;
        float quarterHealth = icon.GtObjMaxHealth * 0.25f;
        Combat.Damage(icon, null, blue, quarterHealth, DeathTypes.Generic, icon.Origin, Vector3.Zero);
        Assert.Equal(intact, icon.Model);
        Combat.Damage(icon, null, blue, 1f, DeathTypes.Generic, icon.Origin, Vector3.Zero);
        Assert.Equal("models/onslaught/controlpoint_icon_dmg1.md3", icon.Model);
        Assert.True(ons.CpCombat.IconHeal(icon, 1f));
        Assert.Equal(intact, icon.Model);
        Combat.Damage(icon, null, blue, icon.GtObjHealth, DeathTypes.Generic, icon.Origin, Vector3.Zero);
        Assert.Equal("models/onslaught/controlpoint_pad.md3", cpEnt.Model);
        Assert.Null(ons.CpCombat.IconFor(1));
    }

    /// <summary>
    /// A build icon destroyed mid-build (its event_damage drops it to 0) aborts the capture: the point reverts
    /// to neutral and never flips (QC ons_ControlPoint_Icon_Damage at health &lt;= 0).
    /// </summary>
    [Fact]
    public void ControlPointIcon_DestroyedMidBuild_AbortsCapture()
    {
        Facade();
        var ons = new Onslaught();
        Onslaught.OnsNode gen = ons.AddGenerator(Teams.Red);
        Onslaught.OnsNode cp = ons.AddControlPoint(1);
        ons.Link(gen, cp);
        ons.UpdateLinks();
        ons.Activate();

        var red = NewPlayer(Teams.Red);
        Entity? cpEnt = ons.CpCombat.SpawnControlPoint(1, new Vector3(100, 0, 0));
        ons.CpCombat.ControlPointTouch(cpEnt!, red);
        Entity? icon = ons.CpCombat.IconFor(1);
        Assert.NotNull(icon);

        // An enemy blue player shells the icon down through the shared pipeline (Combat.Damage → GtEventDamage).
        var blue = NewPlayer(Teams.Blue);
        Combat.Damage(icon!, null, blue, 99999f, DeathTypes.Generic, icon!.Origin, Vector3.Zero);

        Assert.Null(ons.CpCombat.IconFor(1)); // icon was destroyed
        Assert.False(cp.Captured);            // the point reverted to neutral — capture aborted
        Assert.Equal(Teams.None, cp.Team);
    }

    /// <summary>
    /// A SHIELDED generator ignores damage entirely (QC ons_GeneratorDamage: isshielded → return), but an
    /// UNshielded one takes damage through the pipeline and, at 0 health, is destroyed — ending the round with
    /// the surviving team credited ST_ONS_GENS +1 and the attacker SCORE +100.
    /// </summary>
    [Fact]
    public void Generator_ShieldBlocksThenDestroyThroughPipeline()
    {
        Facade();
        var ons = new Onslaught();
        // Two generators, no CP yet → each generator is shielded (no enemy controls a linked CP).
        Onslaught.OnsNode redGen = ons.AddGenerator(Teams.Red);
        Onslaught.OnsNode blueGen = ons.AddGenerator(Teams.Blue);
        ons.UpdateLinks();
        ons.Activate();
        ons.Handler!.CanRoundStart = () => true;
        // Start the round so ons_GeneratorDamage's round-started gate passes (tick to InProgress).
        ons.Handler.Init(0f, 0f, 0f);
        ons.Handler.Tick(); // → Countdown
        ons.Handler.Tick(); // → InProgress
        Assert.True(ons.Handler.IsRoundStarted);

        Entity? blueGenEnt = ons.CpCombat.SpawnGenerator(Teams.Blue, new Vector3(-500, 0, 0));
        Assert.NotNull(blueGenEnt);
        Assert.True(blueGen.Shielded);
        Assert.Equal(Solid.BBox, blueGenEnt!.Solid);

        var red = NewPlayer(Teams.Red);
        Vector3 aim = blueGenEnt.Origin + new Vector3(0, 0, 59);
        Vector3 start = aim - Vector3.UnitX * 121;
        Vector3 end = aim + Vector3.UnitX * 200;
        // A real Vortex-style rail hits the solid generator, whose shield prevents health loss.
        float before = blueGen.Gen!.Health;
        Assert.Same(blueGenEnt, WeaponFiring.FireRailgunBullet(red, start, end, 500f, 0));
        Assert.Equal(before, blueGen.Gen.Health);

        // Once exposed, the same trace damages the generator and changes its model.
        blueGen.Shielded = false;
        float damage = before * 0.35f;
        Assert.Same(blueGenEnt, WeaponFiring.FireRailgunBullet(red, start, end, damage, 0));
        Assert.Equal(before - damage, blueGen.Gen.Health);
        Assert.Equal("models/onslaught/generator_dmg3.md3", blueGenEnt.Model);

        Assert.Same(blueGenEnt, WeaponFiring.FireRailgunBullet(red, start, end, 99999f, 0));
        Assert.Equal("models/onslaught/generator_dead.md3", blueGenEnt.Model);

        Assert.True(blueGen.Gen.Destroyed);
        Assert.True(ons.MatchEnded);
        Assert.Equal(Teams.Red, ons.WinningTeam);
        Assert.Equal(1, ons.GetTeamGenerators(Teams.Red)); // ST_ONS_GENS +1 for the round win
        Assert.Equal(100, red.ScoreFrags);                 // QC: attacker SCORE +100 on a generator kill
        Assert.Equal(blueGenEnt.Origin, ons.CpCombat.GeneratorDestruction!.Value.Origin);
        ons.CpCombat.ClearGeneratorDestruction();
        Assert.Null(ons.CpCombat.GeneratorDestruction);
    }
}

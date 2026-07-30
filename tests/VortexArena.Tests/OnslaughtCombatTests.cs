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

        // Drive the build think enough times to finish (buildhealth 100 → 200 at the per-tick rate).
        for (int i = 0; i < 200 && !icon.GtIconBuilt; i++)
            ons.CpCombat.IconBuildThink(icon);

        Assert.True(icon.GtIconBuilt);          // QC: iscaptured = true once health reaches max
        Assert.True(cp.Captured);               // the point flipped
        Assert.Equal(Teams.Red, cp.Team);
        Assert.Equal(Teams.Red, ons.ControlPointOwner(1));
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

        var red = NewPlayer(Teams.Red);
        // Shielded → the hit is ignored (no health lost).
        float before = blueGen.Gen!.Health;
        Combat.Damage(blueGenEnt!, null, red, 500f, DeathTypes.Generic, blueGenEnt!.Origin, Vector3.Zero);
        Assert.Equal(before, blueGen.Gen.Health);

        // Now unshield the blue generator (red controls a CP linked to it) and destroy it.
        blueGen.Shielded = false;
        Combat.Damage(blueGenEnt, null, red, 99999f, DeathTypes.Generic, blueGenEnt.Origin, Vector3.Zero);

        Assert.True(blueGen.Gen.Destroyed);
        Assert.True(ons.MatchEnded);
        Assert.Equal(Teams.Red, ons.WinningTeam);
        Assert.Equal(1, ons.GetTeamGenerators(Teams.Red)); // ST_ONS_GENS +1 for the round win
        Assert.Equal(100, red.ScoreFrags);                 // QC: attacker SCORE +100 on a generator kill
    }
}

using System.Numerics;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.6: CTF one-flag mode (a shared neutral flag, captured at a team base; team flags not pickable)
/// and the 3/4-team variants (capture credits the right team via the team-keyed flag/cap tables).
/// </summary>
[Collection("GlobalState")]
public class CtfVariantsTests
{
    private static void Facade() => Api.Services = new EngineServices(new CollisionWorld());

    private static Player NewPlayer(int team) => new Player { Team = team, Flags = XonoticGodot.Common.Framework.EntFlags.Client };

    [Fact]
    public void OneFlag_CaptureNeutralAtOwnBase()
    {
        Facade();
        var ctf = new Ctf();
        var neutral = ctf.SpawnNeutralFlag(new Vector3(0, 0, 0));
        var redFlag = ctf.SpawnFlag(Teams.Red, new Vector3(100, 0, 0));
        ctf.SpawnFlag(Teams.Blue, new Vector3(-100, 0, 0));
        ctf.Activate();
        Assert.True(ctf.OneFlag);

        var red = NewPlayer(Teams.Red);

        // grab the neutral flag at its base
        ctf.FlagTouch(neutral.Entity!, red);
        Assert.Same(neutral, ctf.CarriedBy(red));

        // bring it to the red base flag → capture for red
        ctf.FlagTouch(redFlag.Entity!, red);
        Assert.Equal(1, ctf.GetTeamCaps(Teams.Red));
        Assert.Equal(FlagStatus.AtBase, neutral.Status); // neutral flag returned to base
        Assert.Null(ctf.CarriedBy(red));
    }

    [Fact]
    public void OneFlag_TeamFlagsAreNotPickable()
    {
        Facade();
        var ctf = new Ctf();
        ctf.SpawnNeutralFlag(new Vector3(0, 0, 0));
        ctf.SpawnFlag(Teams.Red, new Vector3(100, 0, 0));
        var blueFlag = ctf.SpawnFlag(Teams.Blue, new Vector3(-100, 0, 0));
        ctf.Activate();

        var red = NewPlayer(Teams.Red);
        // touching the enemy (blue) team base flag must NOT pick it up in one-flag mode
        ctf.FlagTouch(blueFlag.Entity!, red);
        Assert.Null(ctf.CarriedBy(red));
        Assert.Equal(FlagStatus.AtBase, blueFlag.Status);
    }

    [Fact]
    public void ThreeTeam_CaptureCreditsCorrectTeam()
    {
        Facade();
        var ctf = new Ctf();
        var redFlag = ctf.SpawnFlag(Teams.Red, new Vector3(100, 0, 0));
        var blueFlag = ctf.SpawnFlag(Teams.Blue, new Vector3(-100, 0, 0));
        var yellowFlag = ctf.SpawnFlag(Teams.Yellow, new Vector3(0, 100, 0));
        ctf.DelayedInit();
        ctf.Activate();
        Assert.False(ctf.OneFlag);
        Assert.Equal(3, ctf.TeamCount);

        // red player steals blue's flag and caps it at red base
        var red = NewPlayer(Teams.Red);
        ctf.FlagTouch(blueFlag.Entity!, red);
        Assert.Same(blueFlag, ctf.CarriedBy(red));
        ctf.FlagTouch(redFlag.Entity!, red);
        Assert.Equal(1, ctf.GetTeamCaps(Teams.Red));

        // yellow player steals red's flag and caps it at yellow base
        var yellow = NewPlayer(Teams.Yellow);
        ctf.FlagTouch(redFlag.Entity!, yellow);
        Assert.Same(redFlag, ctf.CarriedBy(yellow));
        ctf.FlagTouch(yellowFlag.Entity!, yellow);
        Assert.Equal(1, ctf.GetTeamCaps(Teams.Yellow));
        Assert.Equal(0, ctf.GetTeamCaps(Teams.Blue));
    }
}

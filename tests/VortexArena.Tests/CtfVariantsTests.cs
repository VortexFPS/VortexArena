using System.Numerics;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests for §4.6: CTF one-flag mode (a shared neutral flag, captured at a team base; team flags not pickable)
/// and the 3/4-team variants (capture credits the right team via the team-keyed flag/cap tables).
/// </summary>
[Collection("GlobalState")]
public class CtfVariantsTests
{
    private static void Facade() => Api.Services = new EngineServices(new CollisionWorld());

    private static Player NewPlayer(int team) => new Player { Team = team, Flags = VortexArena.Common.Framework.EntFlags.Client };

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

    [Theory]
    [InlineData(true, false, true, 1f, "ranked", 1f)]
    [InlineData(true, false, false, 1f, "ranked", 0f)]
    [InlineData(false, false, true, 1f, "ranked", 0f)]
    [InlineData(true, true, true, 1f, "ranked", 0f)]
    [InlineData(true, false, true, 0.005f, "ranked", 0f)]
    [InlineData(true, false, true, 1f, "", 0f)]
    public void CaptureLeaderboard_RecordsOnlyEnabledValidIdentifiedTwoFlagCaptures(
        bool enabled, bool oneFlag, bool consent, float duration, string identity, float expected)
    {
        var services = new EngineServices(new CollisionWorld());
        Api.Services = services;
        string map = $"ctf-leaderboard-test-{enabled}-{oneFlag}-{consent}-{duration}-{identity}";
        Api.Cvars.Set("mapname", map);
        Api.Cvars.Set("g_ctf_leaderboard", enabled ? "1" : "0");
        var ctf = new Ctf { UidConsentProvider = _ => consent };
        var redFlag = ctf.SpawnFlag(Teams.Red, new Vector3(100, 0, 0));
        var blueFlag = ctf.SpawnFlag(Teams.Blue, new Vector3(-100, 0, 0));
        var pickup = oneFlag ? ctf.SpawnNeutralFlag(Vector3.Zero) : blueFlag;
        ctf.DelayedInit();
        ctf.Activate();
        var player = NewPlayer(Teams.Red);
        player.PersistentId = identity;
        player.NetName = "Runner";

        var clock = typeof(GameClock).GetProperty(nameof(GameClock.Time))!;
        clock.SetValue(services.ClockImpl, 10f);
        ctf.FlagTouch(pickup.Entity!, player);
        clock.SetValue(services.ClockImpl, 10f + duration);
        ctf.FlagTouch(redFlag.Entity!, player);

        Assert.Equal(1, ctf.GetTeamCaps(Teams.Red));
        Assert.Equal(expected, RaceRecords.ReadTime(map, RaceRecords.CtfRecord, 1));
        Assert.Equal(expected > 0f ? "Runner" : "", RaceRecords.ReadName(map, RaceRecords.CtfRecord, 1));
        Assert.Equal(0f, RaceRecords.ReadTime(map, RaceRecords.RaceRecord, 1));
        Assert.Equal(!oneFlag && duration > 0.01f ? duration : 0f, RaceRecords.ReadCaptureBest(map).Time);
        if (expected > 0f) RaceRecords.DeleteTime(map, RaceRecords.CtfRecord, 1);
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

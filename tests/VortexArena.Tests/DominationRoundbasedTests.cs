using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for T18 Domination DEPTH: the round-based variant (g_domination_roundbased, QC Domination_CheckWinner
/// + Team_GetWinnerTeam_WithOwnedItems). A team owning ALL control points wins the round (ST_DOM_CAPS +1); the
/// round-win path runs INSTEAD of the per-tick scoring (PointThink early-returns when round-based).
/// </summary>
[Collection("GlobalState")]
public class DominationRoundbasedTests
{
    private static EngineServices Facade()
    {
        var es = new EngineServices(new CollisionWorld());
        Api.Services = es;
        // g_domination_roundbased is read at Activate; set it on the cvar store.
        Api.Cvars.Set("g_domination_roundbased", "1");
        return es;
    }

    private static Player NewPlayer(int team) => new Player { Team = team, Flags = EntFlags.Client };

    /// <summary>A team owning every control point wins the round and is credited ST_DOM_CAPS +1 (QC).</summary>
    [Fact]
    public void RoundBased_TeamOwningAllPoints_WinsRound()
    {
        Facade();
        var dom = new Domination();
        ControlPoint a = dom.AddControlPoint(new Vector3(100, 0, 0));
        ControlPoint b = dom.AddControlPoint(new Vector3(-100, 0, 0));
        dom.Activate();
        Assert.True(dom.RoundBased);

        var red = NewPlayer(Teams.Red);
        dom.RoundStart(); // QC Domination_RoundStart: clear the per-round winner latch

        // Red captures one point — not all yet → the round is NOT decided.
        dom.CapturePoint(red, a);
        Assert.False(dom.CheckRoundWinner());
        Assert.Equal(0, dom.GetTeamCaps(Teams.Red));

        // Red captures the second point — now owns ALL → round decided, ST_DOM_CAPS +1.
        dom.CapturePoint(red, b);
        Assert.True(dom.CheckRoundWinner());
        Assert.Equal(1, dom.GetTeamCaps(Teams.Red));
        _ = a; _ = b;
    }

    /// <summary>
    /// While points are split between teams, no team owns all → the round stays contested (no winner, no caps).
    /// </summary>
    [Fact]
    public void RoundBased_SplitPoints_NoWinner()
    {
        Facade();
        var dom = new Domination();
        ControlPoint a = dom.AddControlPoint(new Vector3(100, 0, 0));
        ControlPoint b = dom.AddControlPoint(new Vector3(-100, 0, 0));
        dom.Activate();
        dom.RoundStart();

        var red = NewPlayer(Teams.Red);
        var blue = NewPlayer(Teams.Blue);
        dom.CapturePoint(red, a);
        dom.CapturePoint(blue, b);

        Assert.False(dom.CheckRoundWinner()); // split → contested
        Assert.Equal(0, dom.GetTeamCaps(Teams.Red));
        Assert.Equal(0, dom.GetTeamCaps(Teams.Blue));
    }

    /// <summary>
    /// In the round-based variant the per-tick scoring is suppressed (QC dompointthink `if(!domination_roundbased)`):
    /// PointThink grants nothing even for an owned point.
    /// </summary>
    [Fact]
    public void RoundBased_PerTickScoringSuppressed()
    {
        Facade();
        var dom = new Domination();
        ControlPoint a = dom.AddControlPoint(new Vector3(100, 0, 0));
        dom.Activate();
        dom.RoundStart();

        var red = NewPlayer(Teams.Red);
        dom.CapturePoint(red, a);

        // Drive the tick repeatedly — no per-tick points accrue in the round-based variant.
        for (int i = 0; i < 10; i++)
            dom.Tick();
        Assert.Equal(0, dom.GetTeamScore(Teams.Red)); // ST_SCORE (ticks) untouched
        Assert.Equal(0, red.ScoreFrags);
    }

    /// <summary>
    /// QC dompointtouch: while a round is active but not yet started (round_handler_IsActive &amp;&amp;
    /// !round_handler_IsRoundStarted), captures are blocked. The port gates on
    /// <see cref="Domination.RoundStartedSource"/> returning false.
    /// </summary>
    [Fact]
    public void RoundBased_CaptureBlockedWhileRoundNotStarted()
    {
        Facade();
        var dom = new Domination();
        ControlPoint cp = dom.AddControlPoint(new Vector3(0, 0, 0));
        dom.Activate();
        dom.RoundStart();

        // Wire the round-not-started source (simulates EnableRounds wiring in GameWorld).
        bool roundStarted = false;
        dom.RoundStartedSource = () => roundStarted;

        var red = NewPlayer(Teams.Red);

        // While round is not started: capture attempt must be blocked.
        dom.CapturePoint(red, cp);
        Assert.Equal(Teams.None, cp.OwnerTeam); // still neutral
        Assert.Equal(0, dom.GetTeamCaps(Teams.Red));

        // Once the round starts: capture is allowed.
        roundStarted = true;
        dom.CapturePoint(red, cp);
        Assert.Equal(Teams.Red, cp.OwnerTeam);
    }

    /// <summary>
    /// QC Domination_RoundStart: clears LastRoundWinner so <see cref="Domination.CheckRoundWinner"/> can
    /// credit a new winner in the next round. Without this clear, a second round win would not bank caps.
    /// </summary>
    [Fact]
    public void RoundBased_RoundStart_ClearsLastRoundWinner()
    {
        Facade();
        var dom = new Domination();
        ControlPoint a = dom.AddControlPoint(new Vector3(100, 0, 0));
        dom.Activate();
        dom.RoundStart();

        // Red wins the first round.
        var red = NewPlayer(Teams.Red);
        dom.CapturePoint(red, a);
        Assert.True(dom.CheckRoundWinner());
        Assert.Equal(1, dom.GetTeamCaps(Teams.Red));

        // A new round begins: RoundStart resets the winner latch.
        a.OwnerTeam = Teams.None;
        dom.RoundStart();

        // Red wins again — caps should now be 2, not stuck at 1.
        dom.CapturePoint(red, a);
        Assert.True(dom.CheckRoundWinner());
        Assert.Equal(2, dom.GetTeamCaps(Teams.Red));
    }
}

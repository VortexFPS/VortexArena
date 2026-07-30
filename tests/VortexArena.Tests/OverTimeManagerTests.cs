using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// [T42] Tests for the global overtime / sudden-death win layer — the port of <c>CheckRules_World</c>'s
/// overtime cascade (server/world.qc InitiateSuddenDeath / InitiateOvertime / GetWinningCode /
/// WinningCondition_Scores). Covers the <see cref="OverTimeManager"/> decision logic and the per-gametype
/// equality (tie) report (<see cref="GameType.ReportsTie"/>): a tied timed DM/TDM/CTF enters overtime instead
/// of drawing.
/// </summary>
[Collection("GlobalState")]
public class OverTimeManagerTests
{
    private EngineServices _f = null!;

    public OverTimeManagerTests()
    {
        Api.Services = _f = new EngineServices(new CollisionWorld());
        GameScores.RegisterAll();
        GameScores.ResetTeams();
        // overtime cvars (xonotic-server.cfg shipped defaults). Set explicitly so a test does not depend on
        // Cvars.RegisterDefaults having run.
        _f.Cvars.Set("timelimit_overtime", "2");
        _f.Cvars.Set("timelimit_overtimes", "0");
        _f.Cvars.Set("timelimit_suddendeath", "5");
        _f.Cvars.Set("g_campaign", "0");
    }

    private static Player NewPlayer(int team = 0) => new Player { Team = team, Flags = EntFlags.Client };

    // ---- OverTimeManager state + decision functions --------------------------------------------------

    [Fact]
    public void Reset_ClearsAllCheckrulesState()
    {
        var ot = new OverTimeManager();
        _f.Cvars.Set("timelimit_overtimes", "3");
        ot.InitiateSuddenDeath(0f);   // adds-overtime path leaves state untouched, but bump via overtime below
        ot.InitiateOvertime(20f);     // ++OvertimesAdded
        Assert.Equal(1, ot.OvertimesAdded);

        ot.Reset();
        Assert.Equal(0, ot.OvertimesAdded);
        Assert.Equal(0f, ot.SuddenDeathEnd);
        Assert.False(ot.SuddenDeathWarning);
        Assert.Equal(0, ot.Overtimes);
        Assert.False(ot.InSuddenDeath);
    }

    [Fact]
    public void InitiateSuddenDeath_ReturnsTrue_WhenAnOvertimeIsStillAvailable()
    {
        _f.Cvars.Set("timelimit_overtimes", "1"); // one overtime allowed
        var ot = new OverTimeManager();

        // overtimesadded (0) < overtimes (1) and timelimit_overtime != 0 → can add a normal overtime.
        Assert.True(ot.InitiateSuddenDeath(0f));
        Assert.False(ot.InSuddenDeath); // sudden death NOT armed when an overtime is available
    }

    [Fact]
    public void InitiateSuddenDeath_ArmsSuddenDeath_WhenNoOvertimesLeft()
    {
        _f.Cvars.Set("timelimit_overtimes", "0"); // no normal overtimes
        _f.Cvars.Set("timelimit_suddendeath", "5");
        var ot = new OverTimeManager();

        Assert.False(ot.InitiateSuddenDeath(100f)); // must arm sudden death
        Assert.True(ot.InSuddenDeath);
        Assert.Equal(100f + 60f * 5f, ot.SuddenDeathEnd); // time + 60 * timelimit_suddendeath
        Assert.Equal(OverTimeManager.OvertimeSuddenDeath, ot.Overtimes);
    }

    [Fact]
    public void InitiateSuddenDeath_Campaign_EndsSuddenDeathImmediately()
    {
        _f.Cvars.Set("g_campaign", "1");
        var ot = new OverTimeManager();

        Assert.False(ot.InitiateSuddenDeath(42f)); // campaign never gets an overtime
        Assert.True(ot.InSuddenDeath);
        Assert.Equal(42f, ot.SuddenDeathEnd); // no suddendeath in campaign → ends at `time`
        Assert.NotEqual(OverTimeManager.OvertimeSuddenDeath, ot.Overtimes);
    }

    [Fact]
    public void InitiateOvertime_IncrementsCount_AndExtendsTimelimitCvar()
    {
        _f.Cvars.Set("timelimit_overtime", "2");
        float? extendedTo = null;
        var ot = new OverTimeManager { SetTimeLimitCvar = v => extendedTo = v };

        ot.InitiateOvertime(20f); // current timelimit 20 min
        Assert.Equal(1, ot.OvertimesAdded);
        Assert.Equal(1, ot.Overtimes);          // overtimes == overtimesadded (not the suddendeath sentinel)
        Assert.Equal(22f, extendedTo);          // 20 + timelimit_overtime (2)

        ot.InitiateOvertime(22f);
        Assert.Equal(2, ot.OvertimesAdded);
        Assert.Equal(24f, extendedTo);
    }

    [Fact]
    public void GetWinningCode_TieAtLimit_StartsSuddenDeathOvertime()
    {
        Assert.Equal(WinningCode.StartSuddenDeathOvertime, OverTimeManager.GetWinningCode(limitReached: true, equality: true));
        Assert.Equal(WinningCode.Never, OverTimeManager.GetWinningCode(limitReached: false, equality: true));
        Assert.Equal(WinningCode.Yes, OverTimeManager.GetWinningCode(limitReached: true, equality: false));
        Assert.Equal(WinningCode.No, OverTimeManager.GetWinningCode(limitReached: false, equality: false));
    }

    [Fact]
    public void GetWinningCode_Campaign_NeverEntersOvertime()
    {
        _f.Cvars.Set("g_campaign", "1");
        // campaign collapses to a plain limit check — a tie at the limit is a plain win, never overtime.
        Assert.Equal(WinningCode.Yes, OverTimeManager.GetWinningCode(limitReached: true, equality: true));
        Assert.Equal(WinningCode.No, OverTimeManager.GetWinningCode(limitReached: false, equality: true));
    }

    [Fact]
    public void RevertSuddenDeathIfJustBegun_UndoesAFreshSuddenDeathLatch()
    {
        var ot = new OverTimeManager();
        _f.Cvars.Set("timelimit_overtimes", "0");
        ot.InitiateSuddenDeath(0f); // arms sudden death; overtimes := OVERTIME_SUDDENDEATH
        Assert.True(ot.InSuddenDeath);

        ot.RevertSuddenDeathIfJustBegun(overtimesBefore: 0); // it just began this tick → revert
        Assert.False(ot.InSuddenDeath);
        Assert.Equal(0, ot.Overtimes);
    }

    // ---- per-gametype equality (tie) report ----------------------------------------------------------

    [Fact]
    public void Deathmatch_ReportsTie_WhenTopTwoPlayersAreEqual()
    {
        var dm = new Deathmatch();
        var a = NewPlayer();
        var b = NewPlayer();
        a.ScoreFrags = 7;
        b.ScoreFrags = 7; // tied at the top
        var roster = new List<Player> { a, b };

        Assert.True(dm.ReportsTie(roster)); // QC FFA equality: top two players tied → enter overtime
    }

    [Fact]
    public void Deathmatch_DoesNotReportTie_WhenOnePlayerLeads()
    {
        var dm = new Deathmatch();
        var a = NewPlayer();
        var b = NewPlayer();
        a.ScoreFrags = 9;
        b.ScoreFrags = 7; // a leads → decisive
        var roster = new List<Player> { a, b };

        Assert.False(dm.ReportsTie(roster));
    }

    [Fact]
    public void Deathmatch_SoleLeader_IsNotATie()
    {
        var dm = new Deathmatch();
        var a = NewPlayer();
        a.ScoreFrags = 3;
        Assert.False(dm.ReportsTie(new List<Player> { a })); // fewer than two scorers → not a tie
    }

    [Fact]
    public void Tdm_ReportsTie_OnEqualTeamPoints_NotWhenLeading()
    {
        var tdm = new Tdm();
        tdm.Activate(); // sets the TDM team rules (ST_SCORE is the team primary) + zeroes the slots
        try
        {
            tdm.AddTeamScore(Teams.Red, 5);
            tdm.AddTeamScore(Teams.Blue, 5); // tied on team points
            Assert.True(tdm.ReportsTie(new List<Player>()));

            tdm.AddTeamScore(Teams.Red, 1);  // red pulls ahead 6:5
            Assert.False(tdm.ReportsTie(new List<Player>()));
        }
        finally
        {
            tdm.Deactivate();
        }
    }

    [Fact]
    public void Ctf_ReportsTie_OnEqualCaptures()
    {
        var ctf = new Ctf();
        ctf.Activate(); // sets the CTF team rules (ST_CTF_CAPS is the team primary)
        try
        {
            ctf.AddTeamCaps(Teams.Red, 2);
            ctf.AddTeamCaps(Teams.Blue, 2); // tied on captures
            Assert.True(ctf.ReportsTie(new List<Player>()));

            ctf.AddTeamCaps(Teams.Blue, 1); // blue pulls ahead 3:2 captures
            Assert.False(ctf.ReportsTie(new List<Player>()));
        }
        finally
        {
            ctf.Deactivate();
        }
    }

    [Fact]
    public void ObjectiveLatchedMode_NeverReportsTie()
    {
        // Onslaught latches a single winning team (no WinningCondition_Scores tie) → base default false.
        var ons = new Onslaught();
        Assert.False(ons.ReportsTie(new List<Player>()));
    }

    // ---- integration: a tied timed match continues into overtime, then concludes ---------------------

    [Fact]
    public void TiedTimedMatch_AddsOneOvertime_ThenConcludes()
    {
        _f.Cvars.Set("timelimit_overtimes", "1"); // allow exactly one normal overtime
        _f.Cvars.Set("timelimit_overtime", "2");
        float timelimit = 20f;
        float? extendedTo = null;
        var ot = new OverTimeManager { SetTimeLimitCvar = v => { extendedTo = v; timelimit = v; } };

        // --- tick 1: time limit elapses on a tied match (equality = true, no fraglimit winner) ---
        bool wantOvertime = ot.InitiateSuddenDeath(now: 100f); // overtime available → true, no sudden death
        Assert.True(wantOvertime);
        Assert.False(ot.InSuddenDeath);

        WinningCode status = OverTimeManager.ResolveWinningCode(limitReached: false, equality: true);
        Assert.Equal(WinningCode.Never, status);

        // wantOvertime && status==Never → InitiateOvertime extends the timelimit (the match continues).
        ot.InitiateOvertime(timelimit);
        Assert.Equal(22f, extendedTo); // 20 + 2 → clients/commands see the extension
        Assert.Equal(1, ot.OvertimesAdded);

        // --- tick 2: the overtime expires still tied; no overtimes remain → sudden death arms ---
        wantOvertime = ot.InitiateSuddenDeath(now: 220f);
        Assert.False(wantOvertime);   // overtimesadded (1) is not < overtimes (1) → no more normal overtimes
        Assert.True(ot.InSuddenDeath);

        // --- tick 3: someone finally scores in sudden death → the match is decided ---
        // The leader has no fraglimit win (limitReached=false) but is no longer tied; the score check returns No,
        // and CheckRules_World's suddendeath clause (status != Never while in sudden death) folds it to Yes.
        status = OverTimeManager.ResolveWinningCode(limitReached: false, equality: false);
        Assert.Equal(WinningCode.No, status);
        bool endsInSuddenDeath = ot.InSuddenDeath && status != WinningCode.Never; // the GameWorld fold
        Assert.True(endsInSuddenDeath);
    }
}

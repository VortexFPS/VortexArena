using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.11: bot autobalance (move the lowest-scoring bot from the largest team to the smallest) and the
/// inverse-variance skill-weighted team assignment (a joiner goes to the lower-total-skill team).
/// </summary>
[Collection("GlobalState")]
public class TeamBalanceWiringTests
{
    private EngineServices _f = null!;
    public TeamBalanceWiringTests() => Api.Services = _f = new EngineServices(new CollisionWorld());

    private static Player Bot(int team, int score, float skill = 5f)
    {
        var p = new Player { Team = team, IsBot = true, BotSkill = skill, Flags = EntFlags.Client };
        p.ScoreFrags = score;
        return p;
    }

    [Fact]
    public void AutoBalance_MovesLowestScoringBotFromLargestTeam()
    {
        var tp = new Teamplay(isTeamGame: true, teamCount: 2);
        var weak = Bot(Teams.Red, 1);
        var roster = new List<Player>
        {
            weak, Bot(Teams.Red, 5), Bot(Teams.Red, 9), // 3 on red
            Bot(Teams.Blue, 4),                          // 1 on blue → uneven by 2
        };

        Assert.True(tp.TeamsAreUneven(roster));
        Player? moved = tp.AutoBalanceBots(roster);
        Assert.Same(weak, moved);                  // the lowest scorer moves
        Assert.Equal(Teams.Blue, (int)weak.Team);  // now on the smallest team
    }

    [Fact]
    public void AutoBalance_NoMoveWhenEven()
    {
        var tp = new Teamplay(isTeamGame: true, teamCount: 2);
        var roster = new List<Player> { Bot(Teams.Red, 3), Bot(Teams.Blue, 4) };
        Assert.False(tp.TeamsAreUneven(roster));
        Assert.Null(tp.AutoBalanceBots(roster));
    }

    [Fact]
    public void WarmupSkillJoin_SendsJoinerToTheTeamFurthestInSkill()
    {
        // QC TeamBalance_FindBestTeams warmup branch (teamplay.qc:1036): in warmup, with sv_teamnagger AND
        // g_balance_teams_skill, the joiner goes to the team whose weighted-mean skill differs MOST from theirs
        // (z-score) — even if that unbalances sizes — to avoid clumping similar-skill players by join order.
        _f.Cvars.Set("g_balance_teams_skill", "1");
        _f.Cvars.Set("sv_teamnagger", "2");
        _f.Cvars.Set("g_balance_teams_skill_significance_threshold", "1.645");
        var tp = new Teamplay(isTeamGame: true, teamCount: 2) { IsWarmup = () => true };
        tp.SkillProvider = p => p.BotSkill;

        // Red holds a skill-2 bot, Blue a skill-2 bot too; the joiner is skill 9 → it should land on the team it
        // differs from most. With both teams equal skill+size the branch ties → reservoir randomizes, so instead
        // make the teams differ: Red ~2, Blue ~9. A skill-9 joiner is closest to Blue, furthest from Red → joins Red.
        var roster = new List<Player>
        {
            Bot(Teams.Red, 0, skill: 2f),
            Bot(Teams.Blue, 0, skill: 9f),
        };
        var joiner = new Player { IsBot = true, BotSkill = 9f, Flags = EntFlags.Client };

        int team = tp.AssignBestTeam(joiner, roster);
        Assert.Equal(Teams.Red, team); // furthest-in-skill team (the warmup spread rule), not the smaller one
    }

    [Fact]
    public void MoveToTeam_MovesAcrossLockedTeamsAndRestoresLock()
    {
        // QC MoveToTeam (teamplay.qc:330): an admin move backs up the team lock, disables it for the move, then
        // restores it — so the move goes through even while teams are locked and the lock state is unchanged after.
        var tp = new Teamplay(isTeamGame: true, teamCount: 2);
        bool locked = true;
        tp.LockTeamsGet = () => locked;
        tp.LockTeamsSet = v => locked = v;

        var p = new Player { Team = Teams.Red, Flags = EntFlags.Client };
        bool ok = tp.MoveToTeam(p, Teams.Blue);

        Assert.True(ok);
        Assert.Equal(Teams.Blue, (int)p.Team); // moved despite the lock
        Assert.True(locked);                    // lock restored to its prior value
    }

    [Fact]
    public void SkillWeighting_SendsJoinerToLowerSkillTeam()
    {
        _f.Cvars.Set("g_balance_teams_skill", "1"); // enable inverse-variance skill weighting
        var tp = new Teamplay(isTeamGame: true, teamCount: 2);
        tp.SkillProvider = p => p.BotSkill;

        var roster = new List<Player>
        {
            Bot(Teams.Red, 0, skill: 9f),  // red has the strong bot
            Bot(Teams.Blue, 0, skill: 2f), // blue has the weak bot
        };
        var joiner = new Player { IsBot = true, BotSkill = 5f, Flags = EntFlags.Client };

        int team = tp.AssignBestTeam(joiner, roster);
        Assert.Equal(Teams.Blue, team); // balances total skill → joins the weaker (lower total skill) team
    }
}

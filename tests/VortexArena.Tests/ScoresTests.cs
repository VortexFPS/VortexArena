using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the real per-column score table (§4.3): the SP_* field registry, the SP_SCORE/<c>.frags</c>-status
/// split (frags reset on respawn no longer wipes score), column add/clear, winning-condition sort, and the
/// server→client scoreboard net codec round-trip.
/// </summary>
public class ScoresTests
{
    public ScoresTests() => GameScores.RegisterAll(); // idempotent; ensures the registry is present

    private static Player NewPlayer(int team = 0) => new Player { Team = team, Flags = EntFlags.Client };

    [Fact]
    public void Registry_HasCommonFields_AndScoreIsPrimary()
    {
        Assert.NotNull(GameScores.Field("SCORE"));
        Assert.NotNull(GameScores.Field("CTF_CAPS"));
        Assert.NotNull(GameScores.Field("RACE_LAPS"));
        Assert.NotNull(GameScores.Field("DEATHS"));
        // SP_SCORE is the default primary key; DEATHS is lower-is-better.
        Assert.Same(GameScores.Field("SCORE"), GameScores.Primary);
        Assert.True((GameScores.Deaths.Flags & ScoreFlags.LowerIsBetter) != 0);
    }

    [Fact]
    public void ScoreFrags_BacksOntoScoreColumn_NotEngineFrags()
    {
        Player p = NewPlayer();
        p.ScoreFrags = 7;
        Assert.Equal(7, GameScores.Get(p, GameScores.Score));
        Assert.Equal(7, p.ScoreFrags);

        // The engine .frags field (the STATUS sentinel) is independent of the match score.
        p.FragsStatus = Player.FragsSpectator;
        Assert.Equal(Player.FragsSpectator, (int)p.Frags);
        Assert.Equal(7, p.ScoreFrags); // unchanged — status != score
    }

    [Fact]
    public void RespawnFragsReset_DoesNotWipeScore()
    {
        Player p = NewPlayer();
        p.ScoreFrags = 12;

        // QC PutPlayerInServer resets .frags = FRAGS_PLAYER on every (re)spawn (status reset).
        p.FragsStatus = Player.FragsPlayer;

        Assert.Equal(Player.FragsPlayer, (int)p.Frags);
        Assert.Equal(12, p.ScoreFrags); // the running match score survived the respawn
    }

    [Fact]
    public void AddToPlayer_AccumulatesAndMarksDirty()
    {
        Player p = NewPlayer();
        int before = GameScores.Version;
        GameScores.AddToPlayer(p, GameScores.Kills, 1);
        GameScores.AddToPlayer(p, GameScores.Kills, 2);
        Assert.Equal(3, GameScores.Get(p, GameScores.Kills));
        Assert.True(p.ScoreDirty != 0);
        Assert.True(GameScores.Version > before); // a scored change bumps the net version
    }

    [Fact]
    public void ClearPlayer_ZeroesColumns()
    {
        Player p = NewPlayer();
        p.ScoreFrags = 5;
        GameScores.AddToPlayer(p, GameScores.Deaths, 3);
        GameScores.ClearPlayer(p);
        Assert.Equal(0, p.ScoreFrags);
        Assert.Equal(0, GameScores.Get(p, GameScores.Deaths));
    }

    [Fact]
    public void WinningSort_RanksByPrimaryThenLowerDeaths()
    {
        Player a = NewPlayer(), b = NewPlayer(), c = NewPlayer();
        a.ScoreFrags = 5; GameScores.AddToPlayer(a, GameScores.Deaths, 4);
        b.ScoreFrags = 5; GameScores.AddToPlayer(b, GameScores.Deaths, 1); // ties score, fewer deaths => ahead
        c.ScoreFrags = 9;

        var sorted = GameScores.SortPlayers(new Entity[] { a, b, c });
        Assert.Same(c, sorted[0]);              // highest score first
        Assert.Same(b, sorted[1]);              // tie broken by fewer deaths (lower-is-better)
        Assert.Same(a, sorted[2]);
        Assert.Same(c, GameScores.Leader(new Entity[] { a, b, c }));
    }

    [Fact]
    public void TeamScores_AddAndLead()
    {
        GameScores.ResetTeams();
        GameScores.AddToTeam(Teams.Red, 3);
        GameScores.AddToTeam(Teams.Blue, 5);
        Assert.Equal(3, GameScores.TeamScore(Teams.Red));
        Assert.Equal(5, GameScores.TeamScore(Teams.Blue));
        Assert.Equal(Teams.Blue, GameScores.LeaderTeam());
    }

    [Fact]
    public void ScoreboardCodec_RoundTrips()
    {
        Player a = NewPlayer(Teams.Red), b = NewPlayer(Teams.Blue);
        a.ScoreFrags = 11; GameScores.AddToPlayer(a, GameScores.Kills, 11); GameScores.AddToPlayer(a, GameScores.Deaths, 4);
        b.ScoreFrags = 7;  GameScores.AddToPlayer(b, GameScores.Kills, 7);

        var rows = ScoreboardBlock.CaptureRows(new (int, Entity)[] { (101, a), (102, b) });
        var teams = new System.Collections.Generic.List<(int, int)> { (Teams.Red, 11), (Teams.Blue, 7) };

        var w = new BitWriter(512);
        ScoreboardBlock.Serialize(w, rows, teams);

        var r = new BitReader(w.WrittenSpan.ToArray());
        ScoreboardWire? sb = ScoreboardBlock.Deserialize(ref r);
        Assert.NotNull(sb);
        Assert.Equal(2, sb!.Rows.Count);
        Assert.Equal(2, sb.Teams.Count);

        // The decoded columns line up positionally with NetworkedFields; verify SP_SCORE/KILLS for player a.
        int scoreIdx = -1, killsIdx = -1;
        var fields = GameScores.NetworkedFields;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name == "SCORE") scoreIdx = i;
            if (fields[i].Name == "KILLS") killsIdx = i;
        }
        ScoreRowWire rowA = sb.Rows.Find(x => x.NetId == 101);
        Assert.Equal(11, rowA.Columns[scoreIdx]);
        Assert.Equal(11, rowA.Columns[killsIdx]);
        Assert.Contains(sb.Teams, t => t.team == Teams.Red && t.score == 11);
    }

    [Fact]
    public void StatusEffects_StunnedSpawnshieldInferno_AreDefined()
    {
        StatusEffectsCatalog.RegisterAll();
        Assert.NotNull(StatusEffectsCatalog.ByName("stunned"));
        Assert.NotNull(StatusEffectsCatalog.ByName("spawnshield"));
        Assert.NotNull(StatusEffectsCatalog.ByName("buff_inferno"));
        Assert.True(StatusEffectsCatalog.ByName("buff_inferno")!.IsBuff);
        // the powerups now resolve too (ItemPickupRules powerup timers)
        Assert.NotNull(StatusEffectsCatalog.ByName("strength"));
        Assert.NotNull(StatusEffectsCatalog.ByName("superweapon"));
    }
}

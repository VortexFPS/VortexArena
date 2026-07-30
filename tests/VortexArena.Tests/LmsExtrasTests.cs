using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.9: LMS extra-life pickups (grant g_lms_extra_lives lives) and the leader computation
/// (lms_UpdateLeaders — max-lives players become leaders only with a big enough lead and a small enough share).
/// </summary>
[Collection("GlobalState")]
public class LmsExtrasTests
{
    private EngineServices _f = null!;
    private Player NewPlayer() => new Player { Flags = EntFlags.Client };

    public LmsExtrasTests()
    {
        _f = new EngineServices(new CollisionWorld());
        Api.Services = _f;
        // QC: LMS lives are derived from fraglimit (mapinfo lives= / g_lms_lives_override), default 5 when unset.
        _f.Cvars.Set("fraglimit", "5");
        _f.Cvars.Set("g_lms_extra_lives", "3");
    }

    [Fact]
    public void ExtraLife_GrantsConfiguredLives()
    {
        var lms = new LastManStanding();
        var p = NewPlayer();
        lms.AddPlayer(p);
        Assert.Equal(5, lms.LivesOf(p));

        int added = lms.GiveExtraLife(p);
        Assert.Equal(3, added);
        Assert.Equal(8, lms.LivesOf(p));
    }

    [Fact]
    public void ExtraLife_NoGrantOnceEliminated()
    {
        var lms = new LastManStanding();
        var p = NewPlayer();
        lms.GetState(p).Lives = 0; // eliminated
        Assert.Equal(0, lms.GiveExtraLife(p));
        Assert.Equal(0, lms.LivesOf(p));
    }

    [Fact]
    public void Leaders_RequireBigLeadAndSmallShare()
    {
        _f.Cvars.Set("g_lms_leader_lives_diff", "2");
        _f.Cvars.Set("g_lms_leader_minpercent", "0.5");

        var lms = new LastManStanding();
        var leader = NewPlayer(); var mid = NewPlayer(); var low = NewPlayer();
        lms.GetState(leader).Lives = 9;
        lms.GetState(mid).Lives = 5;
        lms.GetState(low).Lives = 4;

        lms.UpdateLeaders();
        // leader has +4 over second-best (5), is 1/3 of the field (<= 0.5) → a leader
        Assert.Contains(leader, lms.Leaders);
        Assert.DoesNotContain(mid, lms.Leaders);
        Assert.Equal(4, lms.LeadersLivesDiff);
    }

    [Fact]
    public void Leaders_NoneWhenPackIsTight()
    {
        _f.Cvars.Set("g_lms_leader_lives_diff", "2");
        _f.Cvars.Set("g_lms_leader_minpercent", "0.5");

        var lms = new LastManStanding();
        var a = NewPlayer(); var b = NewPlayer();
        lms.GetState(a).Lives = 5;
        lms.GetState(b).Lives = 4; // only +1 lead → below the diff threshold

        lms.UpdateLeaders();
        Assert.Empty(lms.Leaders);
    }

    // ---- lms.win.last_standing: ReportsTie / TimeLimitCancelled → WINNING_NEVER ----------------------

    /// <summary>
    /// QC WinningCondition_LMS: when ≥2 living players have equal top-two lives, LMS cancels the time limit
    /// (WINNING_NEVER). <see cref="LastManStanding.ReportsTie"/> surfaces this to the CheckRules_World overtime
    /// cascade so a timelimit expiry while tied enters overtime instead of ending the match.
    /// </summary>
    [Fact]
    public void ReportsTie_TrueWhenTopTwoLivesEqual()
    {
        var lms = new LastManStanding();
        // Simulate match running (game_starttime = 0; Api.Clock.Time defaults 0 which equals GameStartTime).
        // Set GameStartTime slightly in the past so PreMatch is false.
        lms.GameStartTime = -1f;

        var a = NewPlayer(); var b = NewPlayer(); var c = NewPlayer();
        lms.GetState(a).Lives = 5;
        lms.GetState(b).Lives = 5; // top two tied
        lms.GetState(c).Lives = 3;

        lms.CheckWinningCondition();
        Assert.True(lms.TimeLimitCancelled);
        Assert.True(lms.ReportsTie(System.Array.Empty<Player>())); // QC WINNING_NEVER → overtime
    }

    [Fact]
    public void ReportsTie_FalseWhenLeaderIsDecisive()
    {
        var lms = new LastManStanding();
        lms.GameStartTime = -1f;

        var a = NewPlayer(); var b = NewPlayer();
        lms.GetState(a).Lives = 5;
        lms.GetState(b).Lives = 3; // a leads decisively

        lms.CheckWinningCondition();
        Assert.False(lms.TimeLimitCancelled);
        Assert.False(lms.ReportsTie(System.Array.Empty<Player>()));
    }

    [Fact]
    public void ReportsTie_FalseDuringPreMatch()
    {
        var lms = new LastManStanding();
        lms.InWarmup = true; // PreMatch == true during warmup

        var a = NewPlayer(); var b = NewPlayer();
        lms.GetState(a).Lives = 5;
        lms.GetState(b).Lives = 5; // would be a tie, but warmup blocks it

        lms.CheckWinningCondition();
        // CheckWinningCondition returns early on PreMatch, leaving TimeLimitCancelled=false
        Assert.False(lms.TimeLimitCancelled);
        Assert.False(lms.ReportsTie(System.Array.Empty<Player>()));
    }

    // ---- lms.forfeit.remove_player: MakePlayerObserver → RemovePlayer(voluntary:true) -----------------

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(lms, MakePlayerObserver) (sv_lms.qc:413): Base always sets lms_spectate=true in
    /// this hook (for both forced and voluntary paths), routing lms_RemovePlayer into the voluntary-cleanup branch.
    /// The demoted player's state is removed; out-of-game players' ranks are decremented (gap closing).
    /// </summary>
    [Fact]
    public void MakePlayerObserver_RemovesPlayerState_AfterGameStart()
    {
        var lms = new LastManStanding();
        lms.Activate();
        try
        {
            lms.GameStartTime = -1f; // match is running (PreMatch=false)

            var a = NewPlayer(); var leaving = NewPlayer();
            lms.GetState(a).Lives = 4;
            lms.GetState(leaving).Lives = 2; // still in-game (not yet eliminated)

            // Give 'a' an out-of-game rank so we can observe the decrement path
            lms.GetState(a).Rank = 2; // pretend 'a' was already eliminated/ranked
            lms.GetState(a).Lives = 0; // mark out-of-game

            // Simulate MakePlayerObserver hook being fired for 'leaving'
            var hookArgs = new MutatorHooks.MakePlayerObserverArgs(leaving);
            MutatorHooks.MakePlayerObserver.Call(ref hookArgs);

            // voluntary path: 'leaving' state is removed from the dict; 'a' rank decremented from 2 → 1.
            // After removal, GetState(leaving) lazily recreates with StartingLives and Rank=0 — not ranked out.
            Assert.Equal(1, lms.GetState(a).Rank);  // out-of-game rank decremented (gap closed)
        }
        finally
        {
            lms.Deactivate();
        }
    }

    [Fact]
    public void MakePlayerObserver_NoRankAssigned_DuringWarmup()
    {
        var lms = new LastManStanding();
        lms.Activate();
        try
        {
            lms.InWarmup = true; // PreMatch = true → state cleared, no rank assigned

            var a = NewPlayer();
            lms.GetState(a).Lives = 5;
            int startingLives = lms.StartingLives;

            var hookArgs = new MutatorHooks.MakePlayerObserverArgs(a);
            MutatorHooks.MakePlayerObserver.Call(ref hookArgs);

            // During warmup RemovePlayer removes the state entry (no rank). GetState re-creates a fresh one.
            var freshState = lms.GetState(a);
            Assert.Equal(0, freshState.Rank);           // no rank assigned during warmup
            Assert.Equal(startingLives, freshState.Lives); // freshly seeded (no stale state)
        }
        finally
        {
            lms.Deactivate();
        }
    }
}

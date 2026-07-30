using System;
using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the T53 per-mode round/objective HUD status block (<see cref="GametypeStatusBlock"/>) and the
/// gametype capture APIs feeding it: the CA/FT alive counts (QC CA_count_alive_players /
/// freezetag_count_alive_players → STAT(REDALIVE..PINKALIVE)), the eliminated set (QC eliminatedPlayers),
/// the KeyHunt key pack (QC kh_update_state → STAT(OBJECTIVE_STATUS), incl. the per-recipient "31 = self"
/// personalization), the Survival role + hunter-disclosure rules (QC SurvivalStatuses_SendEntity — the
/// anti-cheat invariant that prey never see hunter ids mid-round), codec round-trip + stream alignment,
/// and the per-peer hash gating. Standalone of the ServerNet/ClientNet splice (T54).
/// </summary>
[Collection("GlobalState")]
public class GametypeStatusTests
{
    public GametypeStatusTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameScores.RegisterAll(); // idempotent; ScoreFrags / gametype score rules need the registry
    }

    // ------------------------------------------------------------------------------------------------
    //  helpers
    // ------------------------------------------------------------------------------------------------

    private static Player P(int team) => new Player { Team = team, Flags = EntFlags.Client };

    /// <summary>Stable test net ids: roster index + 1 (0 is never a player id on the real wire either).</summary>
    private static Func<Player, int> Ids(IReadOnlyList<Player> roster)
    {
        var map = new Dictionary<Player, int>();
        for (int i = 0; i < roster.Count; i++) map[roster[i]] = i + 1;
        return p => map[p];
    }

    private static byte[] CaptureBytes(object gametype, Player viewer, IReadOnlyList<Player> roster,
        Func<Player, int> ids)
    {
        var w = new BitWriter();
        Assert.True(GametypeStatusBlock.Capture(w, gametype, viewer, roster, ids, roundStarted: true));
        return w.ToArray();
    }

    /// <summary>Capture between two sentinels and assert the trailing sentinel survives the decode — the
    /// stream-alignment guard (a length mismatch would corrupt every later read of a real snapshot).</summary>
    private static GametypeStatusBlock.Decoded DecodeWithSentinels(object gametype, Player viewer,
        IReadOnlyList<Player> roster, Func<Player, int> ids)
    {
        var w = new BitWriter();
        w.WriteUShort(0xBEEF);
        Assert.True(GametypeStatusBlock.Capture(w, gametype, viewer, roster, ids, roundStarted: true));
        w.WriteUShort(0xD00D);

        var r = new BitReader(w.WrittenSpan);
        Assert.Equal(0xBEEF, r.ReadUShort());
        GametypeStatusBlock.Decoded? d = GametypeStatusBlock.Deserialize(ref r);
        Assert.NotNull(d);
        Assert.Equal(0xD00D, r.ReadUShort());
        Assert.False(r.BadRead);
        return d!;
    }

    // ------------------------------------------------------------------------------------------------
    //  shared plumbing
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void TeamIndexOf_MapsColorCodesToIndices()
    {
        // The #1 footgun (brief risk list): the wire carries 1..4 INDICES, not the 4/13/12/9 color codes.
        Assert.Equal(1, GametypeStatusBlock.TeamIndexOf(Teams.Red));
        Assert.Equal(2, GametypeStatusBlock.TeamIndexOf(Teams.Blue));
        Assert.Equal(3, GametypeStatusBlock.TeamIndexOf(Teams.Yellow));
        Assert.Equal(4, GametypeStatusBlock.TeamIndexOf(Teams.Pink));
        Assert.Equal(0, GametypeStatusBlock.TeamIndexOf(Teams.None));
    }

    [Fact]
    public void UntrackedGametype_WritesNothing()
    {
        var w = new BitWriter();
        var p = P(Teams.None);
        Assert.False(GametypeStatusBlock.Capture(w, null, p, new[] { p }, _ => 1, roundStarted: true));
        Assert.Equal(0, w.Length); // the caller sends only its false bool gate
    }

    // ------------------------------------------------------------------------------------------------
    //  Clan Arena (QC CA_count_alive_players + ca_isEliminated)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Ca_AliveCounts_And_Eliminated_RoundTrip()
    {
        var ca = new ClanArena();
        Player red1 = P(Teams.Red), red2 = P(Teams.Red), blue1 = P(Teams.Blue), blue2 = P(Teams.Blue);
        red2.DeadState = DeadFlag.Dead; // CA alive test = !IS_DEAD
        var roster = new[] { red1, red2, blue1, blue2 };

        ca.SetRoster(roster); ca.CheckWinner(); // the LIVE per-frame recount (round_handler canRoundEnd -> CheckWinner)
        Assert.Equal(1, ca.AliveCount(Teams.Red));
        Assert.Equal(2, ca.AliveCount(Teams.Blue));
        Assert.Equal(0, ca.AliveCount(Teams.Yellow)); // inactive team reads 0
        Assert.True(ca.IsEliminatedPlayer(red2));
        Assert.False(ca.IsEliminatedPlayer(red1));

        Func<Player, int> ids = Ids(roster);
        GametypeStatusBlock.Decoded d = DecodeWithSentinels(ca, red1, roster, ids);
        Assert.Equal(GametypeStatusBlock.Kind.ClanArena, d.Mode);
        Assert.Equal(1, d.MyTeamIndex);                  // red viewer → index 1, NOT color code 4
        Assert.Equal(2, d.TeamCount);                    // CA default teams
        Assert.Equal(new[] { 1, 2, 0, 0 }, d.Alive);     // fixed red,blue,yellow,pink order
        Assert.Equal(new HashSet<int> { ids(red2) }, d.EliminatedNetIds);
    }

    // ------------------------------------------------------------------------------------------------
    //  Freeze Tag (QC freezetag_count_alive_players: alive = health >= 1 && !FROZEN)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Ft_FrozenCountsAsEliminated_NotDead()
    {
        var ft = new FreezeTag();
        Player red1 = P(Teams.Red), red2 = P(Teams.Red), blue1 = P(Teams.Blue);
        var roster = new[] { red1, red2, blue1 };

        ft.Freeze(red2, blue1); // frozen, NOT dead — still eliminated (QC freezetag_isEliminated)
        Assert.False(red2.IsDead);
        Assert.True(ft.IsEliminated(red2));

        ft.SetRoster(roster); ft.CheckWinner();
        Assert.Equal(1, ft.AliveCount(Teams.Red));
        Assert.Equal(1, ft.AliveCount(Teams.Blue));

        Func<Player, int> ids = Ids(roster);
        GametypeStatusBlock.Decoded d = DecodeWithSentinels(ft, blue1, roster, ids);
        Assert.Equal(GametypeStatusBlock.Kind.FreezeTag, d.Mode);
        Assert.Equal(2, d.MyTeamIndex); // blue viewer
        Assert.Equal(new[] { 1, 1, 0, 0 }, d.Alive);
        Assert.Equal(new HashSet<int> { ids(red2) }, d.EliminatedNetIds); // the scoreboard grey-out set
    }

    // ------------------------------------------------------------------------------------------------
    //  KeyHunt (QC kh_update_state) — the pack must match the EXISTING panel decode expression
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Kh_PackKeyState_MatchesPanelDecode_AndPersonalizes()
    {
        var kh = new KeyHunt(); // default 3 teams (red/blue/yellow)
        Player redV = P(Teams.Red), blue = P(Teams.Blue), yellow = P(Teams.Yellow);
        var roster = new[] { redV, blue, yellow };
        kh.SetRoster(roster);

        // Between rounds every key is removed → QC state 0.
        Assert.Equal(0u, kh.PackKeyState(redV));

        kh.StartRound(); // spawns one key per team onto a random live teammate
        Assert.Equal(KeyHunt.RoundPhase.InProgress, kh.Phase);

        // Arrange the brief's scenario deterministically (KeyState.Carrier is the QC key.owner):
        kh.Keys[Teams.Red].Carrier = blue;    // red key carried by a BLUE player
        kh.Keys[Teams.Blue].Carrier = redV;   // blue key carried by THE VIEWER
        kh.Keys[Teams.Yellow].Carrier = null; // yellow key dropped (entity stays in the world)

        uint s = kh.PackKeyState(redV);
        // Raw slots: [carrier team index+1, 31 self, 30 dropped, 0 no pink key].
        Assert.Equal(3u, s & 31);
        Assert.Equal(31u, (s >> 5) & 31);
        Assert.Equal(30u, (s >> 10) & 31);
        Assert.Equal(0u, (s >> 15) & 31);

        // The EXISTING ModIconsPanel.DrawKeyhunt decode (QC cl_keyhunt.qc:25, ((state >> i*5) & 31) - 1):
        // expected per-key results: 2 = blue team, 30 = carrying (self), 29 = dropped, -1 = no key.
        var decoded = new int[4];
        for (int i = 0; i < 4; i++) decoded[i] = (int)((s >> (i * 5)) & 31) - 1;
        Assert.Equal(new[] { 2, 30, 29, -1 }, decoded);

        // PERSONALIZATION (QC's per-recipient |= 31): the same state packs differently for the blue carrier.
        uint sBlue = kh.PackKeyState(blue);
        Assert.Equal(31u, sBlue & 31);        // blue carries the red key → its own slot reads "self"
        Assert.Equal(2u, (sBlue >> 5) & 31);  // the viewer's (red, index 1) carry is a plain team slot

        // Codec round-trip + alignment.
        Func<Player, int> ids = Ids(roster);
        GametypeStatusBlock.Decoded d = DecodeWithSentinels(kh, redV, roster, ids);
        Assert.Equal(GametypeStatusBlock.Kind.KeyHunt, d.Mode);
        Assert.Equal(3, d.TeamCount);
        Assert.Equal(s, d.KeyState);
    }

    // ------------------------------------------------------------------------------------------------
    //  Survival (QC SurvivalStatuses_SendEntity) — the hunter-visibility anti-cheat invariant
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Surv_HunterIdsNeverLeakToPreyMidRound_DisclosedAtRoundEnd()
    {
        var surv = new Survival();
        surv.Activate();
        try
        {
            Player p0 = P(Teams.None), p1 = P(Teams.None), p2 = P(Teams.None), p3 = P(Teams.None);
            var roster = new[] { p0, p1, p2, p3 };
            surv.SetRoster(roster);
            Func<Player, int> ids = Ids(roster);

            // Pre-round: roles not live → myStatus 0 (the client hides the panel), nothing disclosed.
            Assert.False(surv.RoleAssigned);
            GametypeStatusBlock.Decoded pre = DecodeWithSentinels(surv, p1, roster, ids);
            Assert.Equal(GametypeStatusBlock.Kind.Survival, pre.Mode);
            Assert.Equal(0, pre.TeamCount); // roleplay-FFA
            Assert.Equal(0, pre.MyStatus);
            Assert.Empty(pre.HunterNetIds);
            Assert.Empty(pre.EliminatedNetIds);

            // Start the round: zero countdown, two ticks (Waiting→Countdown→InProgress→AssignRoles).
            surv.Handler!.Init(0f, 0f, 0f);
            surv.Tick();
            surv.Tick();
            Assert.True(surv.RoleAssigned);
            // g_survival_hunter_count default 0.25 → floor(4 * 0.25) = 1 hunter, picked RANDOMLY among the
            // live roster (QC Surv_RoundStart FOREACH_CLIENT_RANDOM) — resolve WHO it is rather than assuming
            // a fixed slot (the old port marked roster[0]; the random pick is the Base-faithful behavior).
            Player hunterP = roster.Single(p => surv.StatusOf(p) == Survival.SurvStatus.Hunter);
            Player[] preyP = roster.Where(p => surv.StatusOf(p) == Survival.SurvStatus.Prey).ToArray();
            Assert.Equal(3, preyP.Length);

            // Mid-round PREY viewer: own role only — hunter ids MUST NOT ride the wire (anti-cheat).
            GametypeStatusBlock.Decoded prey = DecodeWithSentinels(surv, preyP[0], roster, ids);
            Assert.Equal(1, prey.MyStatus);
            Assert.Empty(prey.HunterNetIds);

            // Mid-round HUNTER viewer: hunters know all hunters (QC sendflags |= STATUS_SEND_HUNTERS).
            GametypeStatusBlock.Decoded hunter = DecodeWithSentinels(surv, hunterP, roster, ids);
            Assert.Equal(2, hunter.MyStatus);
            Assert.Equal(new HashSet<int> { ids(hunterP) }, hunter.HunterNetIds);

            // Wipe the prey → round over → EVERYONE receives the hunter set (the round-end scoreboard out)
            // and the eliminated set carries the dead prey.
            foreach (Player pr in preyP)
                surv.GetState(pr).Alive = false;
            surv.CheckWinningCondition();
            Assert.True(surv.RoundOver);
            Assert.True(surv.DisclosesHuntersTo(preyP[0]));

            GametypeStatusBlock.Decoded over = DecodeWithSentinels(surv, preyP[0], roster, ids);
            Assert.Equal(new HashSet<int> { ids(hunterP) }, over.HunterNetIds);
            Assert.Equal(preyP.Select(ids).ToHashSet(), over.EliminatedNetIds);
        }
        finally
        {
            surv.Deactivate(); // unhook Combat.Death + the score-anonymize hook (global state hygiene)
        }
    }

    // ------------------------------------------------------------------------------------------------
    //  Last Man Standing (QC sv_lms.qc recycled STAT(REDALIVE/BLUEALIVE/OBJECTIVE_STATUS) leader stats)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Lms_LeaderStats_RoundTrip()
    {
        Api.Services.Cvars.Set("fraglimit", "9");
        Api.Services.Cvars.Set("g_lms_leader_lives_diff", "2");
        Api.Services.Cvars.Set("g_lms_leader_minpercent", "0.5");

        var lms = new LastManStanding();
        Player leader = P(Teams.None), mid = P(Teams.None), low = P(Teams.None);
        lms.GetState(leader).Lives = 9;
        lms.GetState(mid).Lives = 5;
        lms.GetState(low).Lives = 4;
        var roster = new[] { leader, mid, low };

        // QC lms_UpdateLeaders + SV_StartFrame: the leader is +4 over the next-best, 1/3 of the field → a leader.
        lms.UpdateLeaders();
        lms.DriveLeaderVisibility();
        Assert.Equal(1, lms.LeaderCount);
        Assert.Equal(4, lms.LeadersLivesDiff);

        Func<Player, int> ids = Ids(roster);
        GametypeStatusBlock.Decoded d = DecodeWithSentinels(lms, leader, roster, ids);
        Assert.Equal(GametypeStatusBlock.Kind.Lms, d.Mode);
        Assert.Equal(0, d.TeamCount); // FFA: no visible teams
        Assert.Equal(1, d.LmsLeaderCount);   // QC STAT(REDALIVE) = lms_leaders
        Assert.Equal(4, d.LmsLivesDiff);     // QC STAT(BLUEALIVE) = lms_leaders_lives_diff
        // The first frame schedules the show-window (lms_visible_leaders=true → false), so leaders aren't visible yet.
        Assert.False(d.LmsLeadersVisible);   // QC STAT(OBJECTIVE_STATUS) = lms_visible_leaders
    }

    // ------------------------------------------------------------------------------------------------
    //  Hash gating (the per-peer resend gate in the ServerNet splice)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Hash_StableWhenUnchanged_ChangesOnKill_NeverZero()
    {
        var ca = new ClanArena();
        Player red1 = P(Teams.Red), blue1 = P(Teams.Blue), blue2 = P(Teams.Blue);
        var roster = new[] { red1, blue1, blue2 };
        Func<Player, int> ids = Ids(roster);

        ca.SetRoster(roster); ca.CheckWinner();
        uint h1 = GametypeStatusBlock.Hash(CaptureBytes(ca, red1, roster, ids));
        uint h2 = GametypeStatusBlock.Hash(CaptureBytes(ca, red1, roster, ids));
        Assert.Equal(h1, h2);   // unchanged state → no resend
        Assert.NotEqual(0u, h1); // 0 is the "never sent" sentinel

        blue2.DeadState = DeadFlag.Dead;
        ca.SetRoster(roster); ca.CheckWinner();
        uint h3 = GametypeStatusBlock.Hash(CaptureBytes(ca, red1, roster, ids));
        Assert.NotEqual(h1, h3); // a kill changes the alive count + eliminated set → resend

        Assert.NotEqual(0u, GametypeStatusBlock.Hash(ReadOnlySpan<byte>.Empty)); // fold-to-nonzero
    }
}

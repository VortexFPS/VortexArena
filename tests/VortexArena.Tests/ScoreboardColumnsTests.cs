using System.Collections.Generic;
using XonoticGodot.Common.Framework;          // Entity, EntFlags
using XonoticGodot.Common.Gameplay;           // Player, Teams
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Net;
using Xunit;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for T9 — the score-LAYOUT networking that closes the dropped-scoreboard gap, plus the column depth.
///
/// The core bug: <see cref="GameScores.NetworkedFields"/> (and thus <see cref="ScoreboardBlock"/>'s layout
/// hash) is LABEL-derived. A SERVER runs a mode's ScoreRules (blank → declare its columns); a remote client
/// only ran <see cref="GameScores.RegisterAll"/> (default labels). The two networked sets differ, the hash
/// disagrees, and <see cref="ScoreboardBlock.Deserialize"/> drops the whole block. The <see cref="ScoreInfoBlock"/>
/// (QC ENT_CLIENT_SCORES_INFO) carries the active label/flag set so the client matches the server.
///
/// GameScores is a static singleton; these tests model the "server state" (a mode's ScoreRules applied) vs the
/// "client default state" (RegisterAll) by transitioning the SAME singleton and asserting hash equality across
/// the transition — which is exactly what <see cref="ScoreInfoBlock.Apply"/> does on a remote client.
/// </summary>
public class ScoreboardColumnsTests
{
    public ScoreboardColumnsTests()
    {
        GS.RegisterAll();      // ensure the registry exists
        GS.ResetToDefaults();  // start every test from the canonical default state (client-after-RegisterAll)
        GS.ResetTeams();
        GS.Gametype = "dm";
        GS.Teamplay = false;
    }

    /// <summary>Mirror Ctf.cs's ScoreRules so the test's "server" has CTF's mode-specific networked column set.</summary>
    private static void ApplyCtfRulesAsServer()
    {
        GS.Gametype = "ctf";
        GS.Teamplay = true;
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: ScoreFlags.None);
        GS.SetTeamLabel(GS.TeamSlotSecondary, "caps", ScoreFlags.SortPrioPrimary);
        GS.DeclareColumn("CTF_CAPS", ScoreFlags.None, "caps");
        GS.DeclareColumn("CTF_CAPTIME", ScoreFlags.LowerIsBetter | ScoreFlags.Time, "captime");
        GS.DeclareColumn("CTF_PICKUPS", ScoreFlags.None, "pickups");
        GS.DeclareColumn("CTF_FCKILLS", ScoreFlags.None, "fckills");
        GS.DeclareColumn("CTF_RETURNS", ScoreFlags.None, "returns");
        GS.DeclareColumn("CTF_DROPS", ScoreFlags.LowerIsBetter, "drops");
        GS.SetSortKeys(GS.Score, GS.Field("CTF_CAPS"));
    }

    // =====================================================================================
    //  THE fix: layout agreement
    // =====================================================================================

    [Fact]
    public void DefaultClientAndCtfServer_LayoutHashesDiffer()
    {
        // client-after-RegisterAll layout (ctor left us here)
        uint clientHash = ScoreboardBlock.LayoutHash();

        ApplyCtfRulesAsServer();
        uint serverHash = ScoreboardBlock.LayoutHash();

        // The whole point: the two sets differ, which is why a remote client drops the block (the bug).
        Assert.NotEqual(clientHash, serverHash);
        // CTF's set must include CTF_CAPS; the default set must not (its label was blank).
        Assert.Contains(GS.NetworkedFields, f => f.Name == "CTF_CAPS");
    }

    [Fact]
    public void ScoreInfoApply_MakesClientLayoutHashMatchServer()
    {
        // 1) SERVER: apply CTF rules and serialize the ScoreInfo block + record the server layout hash.
        ApplyCtfRulesAsServer();
        uint serverHash = ScoreboardBlock.LayoutHash();
        var w = new BitWriter(1024);
        ScoreInfoBlock.Serialize(w);

        // 2) CLIENT divergence: a remote client only ran RegisterAll — reset the shared singleton to that state.
        GS.ResetToDefaults();
        GS.Gametype = "dm";
        GS.Teamplay = false;
        Assert.NotEqual(serverHash, ScoreboardBlock.LayoutHash()); // diverged

        // 3) CLIENT applies the received ScoreInfo → its layout must now equal the server's.
        var r = new BitReader(w.WrittenSpan.ToArray());
        ScoreInfoBlock.Decoded? d = ScoreInfoBlock.Deserialize(ref r);
        Assert.NotNull(d);
        ScoreInfoBlock.Apply(d!);

        Assert.Equal(serverHash, ScoreboardBlock.LayoutHash());
        Assert.Equal("ctf", GS.Gametype);
        Assert.True(GS.Teamplay);
    }

    [Fact]
    public void ScoreInfo_RoundTripsLabelsFlagsGametypeAndTeamplay()
    {
        ApplyCtfRulesAsServer();
        ScoreFlags capsFlags = GS.Field("CTF_CAPS")!.Flags;
        ScoreFlags captimeFlags = GS.Field("CTF_CAPTIME")!.Flags;
        string teamSlot1Label = GS.TeamLabel(GS.TeamSlotSecondary);

        var w = new BitWriter(1024);
        ScoreInfoBlock.Serialize(w);
        var r = new BitReader(w.WrittenSpan.ToArray());
        ScoreInfoBlock.Decoded? d = ScoreInfoBlock.Deserialize(ref r);

        Assert.NotNull(d);
        Assert.Equal("ctf", d!.Gametype);
        Assert.True(d.Teamplay);
        // per-field labels/flags survive, in registry order.
        int capsIdx = GS.Field("CTF_CAPS")!.RegistryId;
        int captimeIdx = GS.Field("CTF_CAPTIME")!.RegistryId;
        Assert.Equal("caps", d.Labels[capsIdx]);
        Assert.Equal(capsFlags, d.Flags[capsIdx]);
        Assert.Equal(captimeFlags, d.Flags[captimeIdx]);
        // team slot 1 carried its label.
        Assert.Equal(teamSlot1Label, d.TeamLabels[GS.TeamSlotSecondary]);
    }

    [Fact]
    public void ScoreInfoApply_IsIdempotent_ListenServerPath()
    {
        // On a listen server the co-located server already set the labels on the SHARED singleton; applying the
        // received block must be a no-op (same values), not a desync.
        ApplyCtfRulesAsServer();
        uint hashBefore = ScoreboardBlock.LayoutHash();
        ScoreField? caps = GS.Field("CTF_CAPS");

        var w = new BitWriter(1024);
        ScoreInfoBlock.Serialize(w);
        var r = new BitReader(w.WrittenSpan.ToArray());
        ScoreInfoBlock.Decoded? d = ScoreInfoBlock.Deserialize(ref r);
        ScoreInfoBlock.Apply(d!); // apply onto the already-CTF state

        Assert.Equal(hashBefore, ScoreboardBlock.LayoutHash());
        Assert.Equal("caps", caps!.Label);
        // CTF calls SetSortKeys(SCORE, CTF_CAPS): SCORE primary, CTF_CAPS secondary.
        Assert.Same(GS.Field("SCORE"), GS.Primary);
        Assert.Same(GS.Field("CTF_CAPS"), GS.Secondary);
    }

    [Fact]
    public void ScoreInfoApply_ResolvesPrimaryAndSecondaryFromFlags()
    {
        // CTF calls SetSortKeys(SCORE, CTF_CAPS) → SCORE flagged PRIMARY, CTF_CAPS flagged SECONDARY. Verify the
        // client's ResolveSortKeys (inside Apply) derives the SAME keys purely from the networked flags, even
        // though the client never ran the gametype's ScoreRules.
        ApplyCtfRulesAsServer();
        var w = new BitWriter(1024);
        ScoreInfoBlock.Serialize(w);

        GS.ResetToDefaults();
        var r = new BitReader(w.WrittenSpan.ToArray());
        ScoreInfoBlock.Apply(ScoreInfoBlock.Deserialize(ref r)!);

        Assert.Same(GS.Field("SCORE"), GS.Primary);
        Assert.Same(GS.Field("CTF_CAPS"), GS.Secondary);
    }

    // =====================================================================================
    //  Full snapshot ordering: ScoreInfo then ScoreboardBlock, end-to-end across a divergence
    // =====================================================================================

    [Fact]
    public void EndToEnd_RemoteClient_ScoreInfoThenScoreboard_RendersColumns()
    {
        // SERVER: CTF rules + two players with CTF_CAPS values.
        ApplyCtfRulesAsServer();
        var a = new Player { Team = Teams.Red, NetName = "alice", Flags = EntFlags.Client };
        var b = new Player { Team = Teams.Blue, NetName = "bob", Flags = EntFlags.Client };
        a.ScoreFrags = 3; GS.AddToPlayer(a, GS.Field("CTF_CAPS")!, 2);
        b.ScoreFrags = 1; GS.AddToPlayer(b, GS.Field("CTF_CAPS")!, 1);

        var rows = ScoreboardBlock.CaptureRows(new (int, XonoticGodot.Common.Framework.Entity)[] { (1, a), (2, b) });
        var teams = new List<(int, int)> { (Teams.Red, 2), (Teams.Blue, 1) };

        var w = new BitWriter(2048);
        ScoreInfoBlock.Serialize(w);                 // ScoreInfo first (server order)
        ScoreboardBlock.Serialize(w, rows, teams);   // then the per-player columns

        // CLIENT divergence: only RegisterAll.
        GS.ResetToDefaults();
        GS.Gametype = "dm";
        GS.Teamplay = false;

        var r = new BitReader(w.WrittenSpan.ToArray());
        ScoreInfoBlock.Apply(ScoreInfoBlock.Deserialize(ref r)!); // apply layout FIRST (as ClientNet does)
        ScoreboardWire? sb = ScoreboardBlock.Deserialize(ref r);   // now the hash matches → not dropped

        Assert.NotNull(sb); // the bug was this returning null on a remote client
        Assert.Equal(2, sb!.Rows.Count);

        // the entcs name/team slice round-tripped.
        ScoreRowWire ra = sb.Rows.Find(x => x.NetId == 1);
        Assert.Equal("alice", ra.Name);
        Assert.Equal(Teams.Red, ra.Team);

        // CTF_CAPS lands in the right column after the layout matched.
        int capsIdx = -1;
        IReadOnlyList<ScoreField> fields = GS.NetworkedFields;
        for (int i = 0; i < fields.Count; i++) if (fields[i].Name == "CTF_CAPS") capsIdx = i;
        Assert.True(capsIdx >= 0);
        Assert.Equal(2, ra.Columns[capsIdx]);
    }

    [Fact]
    public void ScoreboardBlock_NameAndTeamSlice_RoundTrips()
    {
        // The ScoreboardBlock row gained the entcs name/team slice; verify it survives independently of ScoreInfo.
        var p = new Player { Team = Teams.Yellow, NetName = "^3carol", Flags = EntFlags.Client };
        p.ScoreFrags = 5;
        var rows = ScoreboardBlock.CaptureRows(new (int, XonoticGodot.Common.Framework.Entity)[] { (7, p) });

        var w = new BitWriter(512);
        ScoreboardBlock.Serialize(w, rows, new List<(int, int)>());
        var r = new BitReader(w.WrittenSpan.ToArray());
        ScoreboardWire? sb = ScoreboardBlock.Deserialize(ref r);

        Assert.NotNull(sb);
        ScoreRowWire row = sb!.Rows[0];
        Assert.Equal(7, row.NetId);
        Assert.Equal("^3carol", row.Name);
        Assert.Equal(Teams.Yellow, row.Team);
    }

    // =====================================================================================
    //  Change-gating (steady-state cost)
    // =====================================================================================

    [Fact]
    public void ScoreInfoHash_ChangesOnModeSwitch_StableOtherwise()
    {
        uint dmHash = ScoreInfoBlock.Hash();
        uint dmHash2 = ScoreInfoBlock.Hash();
        Assert.Equal(dmHash, dmHash2); // stable when nothing changed (steady-state = one bool)

        ApplyCtfRulesAsServer();       // a mode switch re-labels columns → the layout generation bumps
        Assert.NotEqual(dmHash, ScoreInfoBlock.Hash());
    }

    // =====================================================================================
    //  Value formatting (QC ScoreString / count_ordinal / TIME_ENCODED_TOSTRING)
    // =====================================================================================

    [Fact]
    public void ScoreString_PlainInteger()
        => Assert.Equal("7", GS.ScoreString(ScoreFlags.None, 7));

    [Fact]
    public void ScoreString_HideZero_BlanksZero()
    {
        Assert.Equal("", GS.ScoreString(ScoreFlags.HideZero, 0));
        Assert.Equal("3", GS.ScoreString(ScoreFlags.HideZero, 3));
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    public void CountOrdinal_MatchesEnglishRules(int n, string expected)
        => Assert.Equal(expected, GS.CountOrdinal(n));

    [Fact]
    public void ScoreString_Rank_UsesOrdinal()
        => Assert.Equal("3rd", GS.ScoreString(ScoreFlags.Rank, 3));

    [Fact]
    public void ScoreString_Time_FormatsMmSsHh()
    {
        // 1:23.45 → TIME_ENCODE = (60+23.45)*100 = 8345 hundredths.
        int encoded = GS.TimeEncode(83.45f);
        Assert.Equal("1:23.45", GS.ScoreString(ScoreFlags.Time, encoded));
    }

    [Fact]
    public void ScoreString_Time_CompactUnderAMinute()
    {
        int encoded = GS.TimeEncode(9.50f); // 9.50 seconds → compact "9.50"
        Assert.Equal("9.50", GS.ScoreString(ScoreFlags.Time, encoded));
    }

    [Fact]
    public void ScoreString_Time_ZeroIsBlank()
        => Assert.Equal("", GS.ScoreString(ScoreFlags.Time, 0)); // SFL_TIME hides a zero (never-set time)

    [Fact]
    public void TimeEncodeDecode_RoundTrips()
    {
        int encoded = GS.TimeEncode(42.37f);
        Assert.Equal(42.37f, GS.TimeDecode(encoded), 2);
    }
}

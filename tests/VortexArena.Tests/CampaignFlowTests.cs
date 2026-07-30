// Port of qcsrc/server/campaign.qc + qcsrc/common/campaign_file.qc + qcsrc/common/campaign_setup.qc
//
// End-to-end verification of the single-player campaign server core (T49). The campaign was implemented
// (src/XonoticGodot.Server/Campaign.cs) but never validated as a flow: Campaign.cs historically had zero
// callers. These tests trace the SERVER half of the flow the live path runs inside GameWorld:
//   Boot -> Campaign.PreInit (gametype/bots/skill/mutators) -> Campaign.PostInit (frag/time limits)
//   ... match ... -> Campaign.PreIntermission (win/lose + progress save) -> Campaign.PostIntermission
//   -> Campaign.Setup (next/replay level -> OnLevelTransition -> host changelevel).
//
// They mirror the QC branch order EXACTLY (campaign.qc CampaignPreInit/PostInit/PreIntermission/
// PostIntermission/CampaignSetup, campaign_file.qc CampaignFile_Load), so a parity drift fails here rather
// than only surfacing in a playthrough. The catalog/menu parse half is covered by CampaignCatalogTests.

using System.Collections.Generic;
using System.Linq;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the campaign server core (<see cref="Campaign"/>) — the C# successor to server/campaign.qc.
/// They touch the ambient cvar store (PreInit reads <c>_campaign_index</c>/<c>_campaign_name</c>, the win
/// logic reads <c>timelimit</c>/<c>fraglimit</c>, the settemps write live cvars), so they run in the
/// serialized GlobalState collection like the other Api-dependent server tests.
/// </summary>
[Collection("GlobalState")]
public class CampaignFlowTests
{
    private EngineServices _f = null!;

    // A faithful two-level slice of maps/campaignxonoticbeta.txt: header, comment lines, then two data rows.
    // Level 0 (boil): dm, 5 bots, skill 2, fraglimit 10, default timelimit, the g_nix mutator.
    // Level 1 (stormkeep): tdm, 5 bots, skill 2, default fraglimit, timelimit 5, no mutator.
    // (Server-side parsing drops the two description columns — the QC #ifdef SVQC slice — but they may be
    // present in the file; the parser tolerates them.)
    private const string Sample =
        "//campaign:Xonotic Campaign\n" +
        "//\"game\",\"mapname\",\"bots\",\"skill\",\"fraglimit\",\"timelimit\",\"mutators\"\n" +
        "\"dm\",\"boil\",\"5\",\"2\",\"10\",,\"g_nix 1\",\"Deathmatch: Boil\",\"Welcome!\"\n" +
        "\"tdm\",\"stormkeep\",\"5\",\"2\",,\"5\",,\"Team Deathmatch: Stormkeep\",\"3v3.\"\n";

    public CampaignFlowTests()
    {
        Api.Services = _f = new EngineServices(new CollisionWorld());
        SettempCvars.Clear();
        // The cvars the campaign core reads/writes. Set explicitly so a test does not depend on
        // Cvars.RegisterDefaults having run (QC the autocvar defaults seeded as the cfgs are exec'd).
        _f.Cvars.Set("g_campaign", "0");
        _f.Cvars.Set("g_campaign_skill", "0");
        _f.Cvars.Set("_campaign_testrun", "0");
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "");
        _f.Cvars.Set("sv_cheats", "0");
        _f.Cvars.Set("sv_public", "1");
        _f.Cvars.Set("pausable", "0");
        _f.Cvars.Set("timelimit", "0");
        _f.Cvars.Set("fraglimit", "0");
        _f.Cvars.Set("leadlimit", "0");
        _f.Cvars.Set("skill", "8");
        _f.Cvars.Set("bot_number", "0");
        _f.Cvars.Set("g_dm", "1");
        _f.Cvars.Set("bot_vs_human", "1");
    }

    private Campaign NewCampaign(string text = Sample) => new() { FileReader = _ => text };

    private static Player NewPlayer(bool winning, bool bot = false)
        => new() { Winning = winning, IsBot = bot };

    // =============================================================================================
    //  Load (QC CampaignFile_Load) — the working buffer holds current + next (CAMPAIGN_MAX_ENTRIES = 2)
    // =============================================================================================

    [Fact]
    public void Load_ParsesGameplayColumns()
    {
        var c = NewCampaign();
        Assert.Equal(2, c.Load(0, Campaign.MaxEntries));

        Assert.Equal("dm", c.Entries[0].Gametype);
        Assert.Equal("boil", c.Entries[0].MapName);
        Assert.Equal(5f, c.Entries[0].Bots);
        Assert.Equal(2f, c.Entries[0].BotSkill);
        Assert.Equal("10", c.Entries[0].FragLimit);
        Assert.Equal("g_nix 1", c.Entries[0].Mutators);

        Assert.Equal("tdm", c.Entries[1].Gametype);
        Assert.Equal("stormkeep", c.Entries[1].MapName);
    }

    [Fact]
    public void Load_AtOffsetIsCurrentLevelPlusNext()
    {
        // QC CampaignPreInit: CampaignFile_Load(campaign_level, 2) — entry[0] is the CURRENT level.
        var c = NewCampaign();
        Assert.Equal(1, c.Load(1, Campaign.MaxEntries)); // only stormkeep remains from offset 1
        Assert.Equal("stormkeep", c.CurrentMap);
        Assert.Equal(1, c.Offset);
    }

    [Fact]
    public void Load_MissingFileLoadsNothing()
    {
        var c = new Campaign { FileReader = _ => null }; // QC fopen < 0 -> 0 entries
        Assert.Equal(0, c.Load(0, Campaign.MaxEntries));
        Assert.Empty(c.Entries);
    }

    // =============================================================================================
    //  PreInit (QC CampaignPreInit) — gametype + bot/skill/mutator settemps, abort paths
    // =============================================================================================

    [Fact]
    public void PreInit_ResolvesLevelAndSetsSettemps()
    {
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");

        var c = NewCampaign();
        Assert.True(c.PreInit());
        Assert.False(c.Aborted);

        // current level (entry[0]) drives the boot gametype/map (QC MapInfo_SwitchGameType(campaign_gametype[0])).
        Assert.Equal("dm", c.CurrentGametype);
        Assert.Equal("boil", c.CurrentMap);

        // QC cvar_settemp: skill = max(0, g_campaign_skill + campaign_botskill[0]); bots/g_campaign/g_dm/bot_vs_human.
        Assert.Equal(2f, _f.Cvars.GetFloat("skill"));      // 0 + 2
        Assert.Equal(5f, _f.Cvars.GetFloat("bot_number"));
        Assert.Equal(1f, _f.Cvars.GetFloat("g_campaign"));
        Assert.Equal(0f, _f.Cvars.GetFloat("g_dm"));
        Assert.Equal(0f, _f.Cvars.GetFloat("bot_vs_human"));

        // QC cvar_set (permanent, NOT settemp): sv_public 0, pausable 1.
        Assert.Equal(0f, _f.Cvars.GetFloat("sv_public"));
        Assert.Equal(1f, _f.Cvars.GetFloat("pausable"));

        // QC the mutator settemp loop over campaign_mutators[0] ("g_nix 1").
        Assert.Equal("1", _f.Cvars.GetString("g_nix"));
    }

    [Fact]
    public void PreInit_AppliesGlobalSkillOffset()
    {
        // QC baseskill = max(0, autocvar_g_campaign_skill + campaign_botskill[0]).
        _f.Cvars.Set("g_campaign_skill", "2");
        var c = NewCampaign();
        Assert.True(c.PreInit());
        Assert.Equal(4f, _f.Cvars.GetFloat("skill")); // 2 + 2

        SettempCvars.Clear();
        _f.Cvars.Set("g_campaign_skill", "-10"); // negative offset clamped at 0 (QC max(0, ...))
        var c2 = NewCampaign();
        Assert.True(c2.PreInit());
        Assert.Equal(0f, _f.Cvars.GetFloat("skill"));
    }

    [Fact]
    public void PreInit_UnknownMapAborts()
    {
        var c = new Campaign { FileReader = _ => null }; // QC campaign_entries < 1 -> CampaignBailout
        Assert.False(c.PreInit());
        Assert.True(c.Aborted);
        Assert.Equal(0f, _f.Cvars.GetFloat("g_campaign")); // QC CampaignBailout: cvar_set("g_campaign", "0")
    }

    [Fact]
    public void PreInit_CheatsAbort()
    {
        // QC: if(autocvar_sv_cheats) { ... CampaignBailout("JOLLY CHEATS"); }
        _f.Cvars.Set("sv_cheats", "1");
        var c = NewCampaign();
        Assert.False(c.PreInit());
        Assert.True(c.Aborted);
        Assert.Equal(0f, _f.Cvars.GetFloat("g_campaign"));
    }

    // =============================================================================================
    //  PostInit (QC CampaignPostInit) — frag/time limits: default vs empty vs value
    // =============================================================================================

    [Fact]
    public void PostInit_AppliesLevelLimits()
    {
        var c = NewCampaign();
        Assert.True(c.PreInit()); // level 0 (boil): fraglimit "10", timelimit "" (default-on-load slot)
        c.PostInit();

        // boil's fraglimit column is "10" (single value, no "+lead"): fraglimit set, leadlimit untouched.
        Assert.Equal(10f, _f.Cvars.GetFloat("fraglimit"));
        // boil's timelimit column is empty ("" != "default") so QC sets the cvar to "" -> 0 (no limit).
        Assert.Equal(0f, _f.Cvars.GetFloat("timelimit"));
    }

    [Fact]
    public void PostInit_DefaultKeywordLeavesCvarUntouched()
    {
        // A "default" column means "use the implicit value" (QC: skip the cvar_set entirely).
        const string text =
            "//campaign:T\n" +
            "\"dm\",\"boil\",\"4\",\"3\",\"default\",\"default\",\n";
        _f.Cvars.Set("fraglimit", "33");
        _f.Cvars.Set("timelimit", "44");

        var c = NewCampaign(text);
        Assert.True(c.PreInit());
        c.PostInit();

        Assert.Equal(33f, _f.Cvars.GetFloat("fraglimit")); // unchanged
        Assert.Equal(44f, _f.Cvars.GetFloat("timelimit")); // unchanged
    }

    [Fact]
    public void PostInit_FragPlusLeadSplit()
    {
        // The fraglimit column is "score+lead" (QC tokenizebyseparator(campaign_fraglimit[0], "+")).
        const string text =
            "//campaign:T\n" +
            "\"dm\",\"boil\",\"4\",\"3\",\"20+5\",\"default\",\n";
        var c = NewCampaign(text);
        Assert.True(c.PreInit());
        c.PostInit();

        Assert.Equal(20f, _f.Cvars.GetFloat("fraglimit"));
        Assert.Equal(5f, _f.Cvars.GetFloat("leadlimit"));
    }

    // =============================================================================================
    //  PreIntermission (QC CampaignPreIntermission) — sole-human-winner win/lose + progress save
    // =============================================================================================

    [Fact]
    public void PreIntermission_SoloWinAdvancesFrontier()
    {
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "0"); // the saved frontier == the current level
        var c = NewCampaign();
        Assert.True(c.PreInit());

        var saved = new Dictionary<string, float>();
        c.OnProgressSaved = (name, value) => saved[name] = value;
        c.CfgReader = () => "";          // isolate from the real campaign.cfg on disk
        c.CfgWriter = _ => { };

        // One real human, the sole winner: QC won==1 && lost==0 -> WON.
        int result = c.PreIntermission(
            new[] { NewPlayer(winning: true) }, p => p.Winning,
            checkrulesEquality: false, cheatCount: 0, timeNow: 0f);

        Assert.Equal(1, result);
        Assert.Equal(1, c.Won);
        // QC: at the frontier level, save campaign_index_var = campaign_level + 1 (advance).
        Assert.True(saved.ContainsKey("g_campaignxonoticbeta_index"));
        Assert.Equal(1f, saved["g_campaignxonoticbeta_index"]);
    }

    [Fact]
    public void PreIntermission_LossDoesNotAdvance()
    {
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "0");
        var c = NewCampaign();
        Assert.True(c.PreInit());

        var saved = new Dictionary<string, float>();
        c.OnProgressSaved = (name, value) => saved[name] = value;
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        // The human did NOT win (lost==1) -> QC LOST, no progress save.
        int result = c.PreIntermission(
            new[] { NewPlayer(winning: false) }, p => p.Winning,
            checkrulesEquality: false, cheatCount: 0, timeNow: 0f);

        Assert.Equal(0, result);
        Assert.Empty(saved); // replay: the frontier must not move
    }

    [Fact]
    public void PreIntermission_CheatsBlockTheSave()
    {
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "0");
        var c = NewCampaign();
        Assert.True(c.PreInit());

        var saved = new Dictionary<string, float>();
        c.OnProgressSaved = (name, value) => saved[name] = value;
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        // QC: progress saves only when cheatcount_total == 0. A win WITH cheats decides Won but saves nothing.
        int result = c.PreIntermission(
            new[] { NewPlayer(winning: true) }, p => p.Winning,
            checkrulesEquality: false, cheatCount: 3, timeNow: 0f);

        Assert.Equal(1, result);
        Assert.Empty(saved);
    }

    [Fact]
    public void PreIntermission_ReplayingBelowFrontierDoesNotRegressIt()
    {
        // Replaying level 0 while the frontier is already at level 1 (Level != g_campaign<id>_index) must not save.
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "1"); // frontier ahead of the replayed level
        var c = NewCampaign();
        Assert.True(c.PreInit());

        var saved = new Dictionary<string, float>();
        c.OnProgressSaved = (name, value) => saved[name] = value;
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        int result = c.PreIntermission(
            new[] { NewPlayer(winning: true) }, p => p.Winning,
            checkrulesEquality: false, cheatCount: 0, timeNow: 0f);

        Assert.Equal(1, result);                 // still a win
        Assert.Empty(saved);                      // QC: campaign_level == cvar(index_var) gate fails -> no save
    }

    [Fact]
    public void PreIntermission_ForceWinAlwaysWins()
    {
        // QC: campaign_forcewin (a level-end trigger) forces a win regardless of the winner tally.
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "0");
        var c = NewCampaign();
        Assert.True(c.PreInit());
        c.ForceWin = true;
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        int result = c.PreIntermission(
            new[] { NewPlayer(winning: false) }, p => p.Winning, // even though the human "lost"
            checkrulesEquality: false, cheatCount: 0, timeNow: 0f);

        Assert.Equal(1, result);
    }

    [Fact]
    public void PreIntermission_SoleWinnerButTimeUpIsALoss()
    {
        // QC: inside the won==1 && lost==0 branch, if timelimit AND fraglimit are both set and time has
        // expired, the level is LOST ("Time's up!") — the player must beat the clock, not just outlast.
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "0");
        _f.Cvars.Set("timelimit", "10"); // minutes
        _f.Cvars.Set("fraglimit", "20");
        var c = NewCampaign();
        Assert.True(c.PreInit());
        // PostInit would re-author these from the file; here we test PreIntermission's branch directly, so set
        // the live limits AFTER PreInit (which doesn't touch timelimit/fraglimit).
        _f.Cvars.Set("timelimit", "10");
        _f.Cvars.Set("fraglimit", "20");
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        var saved = new Dictionary<string, float>();
        c.OnProgressSaved = (name, value) => saved[name] = value;

        // time = 11 minutes > timelimit (10 min) -> over the limit despite the sole winner.
        int result = c.PreIntermission(
            new[] { NewPlayer(winning: true) }, p => p.Winning,
            checkrulesEquality: false, cheatCount: 0, timeNow: 11f * 60f);

        Assert.Equal(0, result); // LOST on the clock
        Assert.Empty(saved);     // no advance
    }

    [Fact]
    public void PreIntermission_BotsAreNotCountedAsWinnersOrLosers()
    {
        // QC FOREACH_CLIENT(IS_PLAYER(it) && IS_REAL_CLIENT(it)) — bots never count. The live path passes
        // Clients.Players.Where(p => !p.IsBot), so only the human decides won/lost. A lone human winner wins
        // even with bot players present.
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "0");
        var c = NewCampaign();
        Assert.True(c.PreInit());
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        // The live filter (!IsBot) — mirror it here so the test exercises the same input the server feeds.
        var clients = new[] { NewPlayer(winning: true), NewPlayer(winning: false, bot: true) };
        int result = c.PreIntermission(
            clients.Where(p => !p.IsBot), p => p.Winning,
            checkrulesEquality: false, cheatCount: 0, timeNow: 0f);

        Assert.Equal(1, result); // the bot is filtered out -> won==1 && lost==0
    }

    // =============================================================================================
    //  PostIntermission (QC CampaignPostIntermission) — last level vs next level
    // =============================================================================================

    [Fact]
    public void PostIntermission_NextLevelSetsUpAndTransitions()
    {
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        var c = NewCampaign();
        Assert.True(c.PreInit()); // loads boil + stormkeep (entries == 2)

        (string name, int index, string map)? transition = null;
        c.OnLevelTransition = (name, index, map) => transition = (name, index, map);

        c.CfgReader = () => "";
        c.CfgWriter = _ => { };
        c.PreIntermission(new[] { NewPlayer(winning: true) }, p => p.Winning, false, 0, 0f); // Won = 1

        Assert.True(c.PostIntermission()); // not the last level -> continues
        Assert.NotNull(transition);
        // QC CampaignSetup(campaign_won): _campaign_index = offset + won = 0 + 1, map = campaign_mapname[1].
        Assert.Equal("xonoticbeta", transition!.Value.name);
        Assert.Equal(1, transition!.Value.index);
        Assert.Equal("stormkeep", transition!.Value.map);
    }

    [Fact]
    public void PostIntermission_LossReplaysSameLevel()
    {
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        var c = NewCampaign();
        Assert.True(c.PreInit());

        (string name, int index, string map)? transition = null;
        c.OnLevelTransition = (name, index, map) => transition = (name, index, map);

        c.CfgReader = () => "";
        c.CfgWriter = _ => { };
        c.PreIntermission(new[] { NewPlayer(winning: false) }, p => p.Winning, false, 0, 0f); // Won = 0

        Assert.True(c.PostIntermission());
        // QC CampaignSetup(0): replay -> _campaign_index = offset + 0 = 0, map = campaign_mapname[0] (boil).
        Assert.Equal(0, transition!.Value.index);
        Assert.Equal("boil", transition!.Value.map);
    }

    [Fact]
    public void PostIntermission_WinningTheLastLevelEndsTheCampaign()
    {
        // Boot at the LAST level (offset 1 -> only stormkeep loads -> entries == 1).
        _f.Cvars.Set("_campaign_index", "1");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        _f.Cvars.Set("g_campaignxonoticbeta_index", "1");
        var c = NewCampaign();
        Assert.True(c.PreInit());
        Assert.Single(c.Entries);

        bool transitioned = false;
        c.OnLevelTransition = (_, _, _) => transitioned = true;
        var saved = new Dictionary<string, float>();
        c.OnProgressSaved = (name, value) => saved[name] = value;
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        c.PreIntermission(new[] { NewPlayer(winning: true) }, p => p.Winning, false, 0, 0f); // Won = 1

        // QC: at the LAST level, the win saves BOTH _won and the advanced _index (campaign_entries < 2 branch).
        Assert.True(saved.ContainsKey("g_campaignxonoticbeta_won"));
        Assert.Equal(1f, saved["g_campaignxonoticbeta_won"]);
        Assert.Equal(2f, saved["g_campaignxonoticbeta_index"]);

        // QC CampaignPostIntermission: campaign_won && campaign_entries < 2 -> last map won, NO further Setup.
        Assert.False(c.PostIntermission());
        Assert.False(transitioned);
    }

    [Fact]
    public void PostIntermission_LosingTheLastLevelReplaysIt()
    {
        _f.Cvars.Set("_campaign_index", "1");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        var c = NewCampaign();
        Assert.True(c.PreInit());
        Assert.Single(c.Entries);

        (string name, int index, string map)? transition = null;
        c.OnLevelTransition = (name, index, map) => transition = (name, index, map);
        c.CfgReader = () => "";
        c.CfgWriter = _ => { };

        c.PreIntermission(new[] { NewPlayer(winning: false) }, p => p.Winning, false, 0, 0f); // Won = 0

        // Won == 0 so the "last map won" early-out does NOT trigger; Setup(0) replays stormkeep.
        Assert.True(c.PostIntermission());
        Assert.Equal("stormkeep", transition!.Value.map);
        Assert.Equal(1, transition!.Value.index);
    }

    // =============================================================================================
    //  Setup (QC CampaignSetup) — sets the version/progress cvars before the world boots
    // =============================================================================================

    [Fact]
    public void Setup_SetsCampaignCvarsAndFiresTransition()
    {
        _f.Cvars.Set("_campaign_index", "0");
        _f.Cvars.Set("_campaign_name", "xonoticbeta");
        var c = NewCampaign();
        Assert.True(c.PreInit());

        (string name, int index, string map)? transition = null;
        c.OnLevelTransition = (name, index, map) => transition = (name, index, map);

        c.Setup(1); // advance to offset+1

        // QC CampaignSetup: set g_campaign 1; set _campaign_name <name>; set _campaign_index <offset+n>.
        Assert.Equal(1f, _f.Cvars.GetFloat("g_campaign"));
        Assert.Equal("xonoticbeta", _f.Cvars.GetString("_campaign_name"));
        Assert.Equal(1f, _f.Cvars.GetFloat("_campaign_index"));

        Assert.NotNull(transition);
        Assert.Equal("stormkeep", transition!.Value.map);
    }

    [Fact]
    public void SaveCvar_RewritesCampaignCfgPreservingOtherKeys()
    {
        // QC CampaignSaveCvar: read campaign.cfg, drop any prior line for this cvar, keep the rest, append the
        // new value, write back; also set the live cvar.
        var c = NewCampaign();
        string? written = null;
        c.CfgReader = () => "set g_campaignother_index 4\nset g_campaignxonoticbeta_index 2\n";
        c.CfgWriter = s => written = s;

        c.SaveCvar("g_campaignxonoticbeta_index", 3);

        Assert.Equal(3f, _f.Cvars.GetFloat("g_campaignxonoticbeta_index")); // live cvar updated
        Assert.NotNull(written);
        Assert.Contains("set g_campaignother_index 4", written!);            // unrelated key preserved
        Assert.Contains("set g_campaignxonoticbeta_index 3", written!);      // our key rewritten to 3
        Assert.DoesNotContain("g_campaignxonoticbeta_index 2", written!);    // the stale value is gone
    }
}

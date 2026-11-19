using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using VortexArena.Server.Bot.Neural;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests for the T56 server-bus commands on <see cref="Commands"/> — the port of server/command/cmd.qc
/// (voice/suggestmap/autoswitch/physics/clientversion), server/command/common.qc (records/rankings/lsmaps/
/// printmaplist/ladder/cvar_changes/cvar_purechanges/editmob) and the engine <c>defer</c> command, plus the
/// defer↔restart-vote regression guard and caller gating.
/// </summary>
[Collection("GlobalState")]
public class ServerClientCommandsTests
{
    private static GameWorld NewWorld(string gametype = "dm")
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot(gametype);
        return world;
    }

    private static Player NewCaller(string name = "p")
        => new() { NetName = name, Flags = EntFlags.Client, PlayerId = 1 };

    [Fact]
    public void HereCrosshair_PublishesVisibleMarkerAndDirectedBotGoal()
    {
        var world = NewWorld();
        Player p = NewCaller("controller");
        p.Origin = new Vector3(32f, 64f, 96f);
        p.ViewAngles = new Vector3(0f, 90f, 0f);

        var brain = world.Bots.AddDirectedBot(p, "directed-test");
        Assert.NotNull(brain);

        CommandContext result = world.Commands.Execute(
            "waypoint_here_crosshair", isServerConsole: false, caller: p);

        Assert.Contains("HERE spawned", result.Output);
        var marker = Assert.Single(VortexArena.Common.Gameplay.Waypoints.WaypointSprites.Active);
        Assert.Equal("Here", marker.SpriteName);
        Assert.Same(p, marker.DeployedBy);
        Vector3 directed = brain!.DirectedGoalProvider!.Invoke()!.Value;
        Assert.Equal(marker.Origin.X, directed.X);
        Assert.Equal(marker.Origin.Y, directed.Y);
        Assert.Equal(marker.Origin.Z + NavDistanceField.OriginAboveFloor, directed.Z);

        Vector3 first = marker.Origin;
        p.Origin = new Vector3(-400f, 240f, 32f);
        CommandContext retarget = world.Commands.Execute(
            "waypoint_here_here", isServerConsole: false, caller: p);

        Assert.Contains("HERE spawned", retarget.Output);
        var replacement = Assert.Single(VortexArena.Common.Gameplay.Waypoints.WaypointSprites.Active);
        Assert.NotEqual(first, replacement.Origin);
        Assert.Equal(p.Origin, replacement.Origin);
        Assert.Equal(replacement.Origin, brain.DirectedGoalProvider.Invoke());
    }

    [Fact]
    public void HereCrosshair_ExplainsWhyObserverCannotPlaceIt()
    {
        var world = NewWorld();
        Player p = NewCaller();
        p.IsObserver = true;

        CommandContext result = world.Commands.Execute(
            "waypoint_here_crosshair", isServerConsole: false, caller: p);

        Assert.Contains("join the match", result.Output);
        Assert.Empty(VortexArena.Common.Gameplay.Waypoints.WaypointSprites.Active);
    }

    [Fact]
    public void Vote_ExplicitServerCommandDoesNotUseTheListenPlayer()
    {
        var world = NewWorld();
        Player host = world.Clients.ClientConnect(isBot: false, netName: "host").Player;
        world.Services.Cvars.Set("sv_vote_gamestart", "1");
        world.Services.Cvars.Set("sv_vote_nospectators", "0");

        world.Commands.Execute("sv_cmd vote call fraglimit 83", isServerConsole: true, caller: host);

        Assert.True(world.Voting.Active);
        Assert.Null(world.Voting.Caller);
        Assert.Equal(0, world.Voting.YesCount);
        Assert.Contains("Only clients can vote", world.Commands.Execute("sv_cmd vote yes", true, host).Output);
        world.Commands.Execute("vote yes", isServerConsole: false, caller: host);
        Assert.False(world.Voting.Active);
        Assert.Equal("83", world.Services.Cvars.GetString("fraglimit"));
    }

    // ============================================================================== engine defer

    [Fact]
    public void Defer_Enqueue_DoesNotRunUntilPumped()
    {
        var world = NewWorld();
        bool ran = false;
        world.Commands.Register("__t_fire", "test", _ => { ran = true; return true; });

        world.Commands.Execute("defer 1 __t_fire", isServerConsole: true);
        Assert.False(ran); // queued, not run

        // pump at +0.5s — not yet; at +1s — fires (mirrors GameWorld.OnStartFrame's pump line).
        world.Commands.Deferred.Pump(world.Time + 0.5f, cmd => world.Commands.Execute(cmd, isServerConsole: true));
        Assert.False(ran);
        world.Commands.Deferred.Pump(world.Time + 1f, cmd => world.Commands.Execute(cmd, isServerConsole: true));
        Assert.True(ran);
    }

    [Fact]
    public void Defer_Clear_DropsPending()
    {
        var world = NewWorld();
        bool ran = false;
        world.Commands.Register("__t_fire", "test", _ => { ran = true; return true; });
        world.Commands.Execute("defer 5 __t_fire", isServerConsole: true);
        world.Commands.Execute("defer clear", isServerConsole: true);
        world.Commands.Deferred.Pump(world.Time + 100f, cmd => world.Commands.Execute(cmd, isServerConsole: true));
        Assert.False(ran);
    }

    [Fact]
    public void Defer_NoArgs_ListsPendingOrNone()
    {
        var world = NewWorld();
        CommandContext empty = world.Commands.Execute("defer", isServerConsole: true);
        Assert.Contains("No commands are pending.", empty.Output);

        world.Commands.Execute("defer 3 restart", isServerConsole: true);
        CommandContext listed = world.Commands.Execute("defer", isServerConsole: true);
        Assert.Contains("restart", listed.Output);
    }

    [Fact]
    public void Defer_RestartVote_ActuallyRestarts_AfterPump()
    {
        // The regression guard: a passed `restart` vote enqueues `defer 1 restart`; once the queue is pumped
        // past 1 s the match must actually restart (Intermission cleared). Before T56 `defer` didn't exist and
        // the command silently no-op'd.
        var world = NewWorld();
        // put the match into intermission so a restart has something observable to clear.
        world.Clients.ClientConnect(isBot: true, netName: "bot1");
        world.EndMatch();
        Assert.True(world.Intermission.Running);

        // simulate VoteController's passed-vote callback running its parsed command through the bus.
        world.Commands.Execute("defer 1 restart", isServerConsole: true);
        Assert.True(world.Intermission.Running); // still in intermission — the restart is deferred

        // pump past the 1 s delay → restart runs → intermission reset.
        world.Commands.Deferred.Pump(world.Time + 1.01f, cmd => world.Commands.Execute(cmd, isServerConsole: true));
        Assert.False(world.Intermission.Running);
    }

    [Fact]
    public void Nextframe_EnqueuesForNextTick()
    {
        var world = NewWorld();
        bool ran = false;
        world.Commands.Register("__t_nf", "test", _ => { ran = true; return true; });
        world.Commands.Execute("nextframe __t_nf", isServerConsole: true);
        Assert.False(ran);
        world.Commands.Deferred.Pump(world.Time, cmd => world.Commands.Execute(cmd, isServerConsole: true)); // delay 0
        Assert.True(ran);
    }

    // ============================================================================== generic family on the bus

    [Fact]
    public void Rpn_OnServerBus_WritesCvar()
    {
        var world = NewWorld();
        world.Commands.Execute("rpn /g_testval 6 7 mul def", isServerConsole: true);
        Assert.Equal("42", Cvars.String("g_testval"));
    }

    [Fact]
    public void Maplist_Add_OnBus_Prepends()
    {
        var world = NewWorld();
        Cvars.Set("g_maplist", "dance");
        world.Commands.Execute("maplist add boil", isServerConsole: true);
        Assert.Equal("boil dance", Cvars.String("g_maplist"));
    }

    [Fact]
    public void Rpn_Time_OnServerBus_PushesSimClock()
    {
        // F22: the rpn `time` op (QC rpn.qc:547-548 rpn_pushf(time)) pushes the VM clock — i.e. QC's `time`
        // global — NOT the empty cvar_string("time") the old port pushed. On the live bus Api.Services is wired,
        // so it reads Api.Clock.Time (the QC `time` global). Advance a few frames so the clock is non-zero.
        var world = NewWorld();
        for (int i = 0; i < 8; i++) world.Frame(0.1f);
        // NB: the oracle is Api.Clock.Time (the QC `time` global the op reads), which is the last-tick clock and
        // is NOT identical to world.Time (== Simulation accumulated time) between frame boundaries.
        float clock = Api.Clock.Time;
        Assert.True(clock > 0f, "precondition: the sim clock should have advanced");

        world.Commands.Execute("rpn /g_rpn_time time def", isServerConsole: true);
        // The result is the clock formatted exactly as QC's sprintf("%.9g", time).
        Assert.Equal(Rpn.Format9g(clock), Cvars.String("g_rpn_time"));
        Assert.NotEqual("", Cvars.String("g_rpn_time")); // the old bug pushed the empty cvar value
    }

    // ============================================================================== autoswitch

    [Fact]
    public void Autoswitch_TogglesFlag_AndReports()
    {
        var world = NewWorld();
        Player p = NewCaller();
        CommandContext on = world.Commands.Execute("autoswitch on", isServerConsole: false, caller: p);
        Assert.True(world.Commands.GetAutoswitch(p));
        Assert.Contains("on", on.Output);

        CommandContext off = world.Commands.Execute("autoswitch off", isServerConsole: false, caller: p);
        Assert.False(world.Commands.GetAutoswitch(p));
        Assert.Contains("off", off.Output);
    }

    [Fact]
    public void Autoswitch_RejectsNullCaller()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("autoswitch 1", isServerConsole: true);
        Assert.Contains("client command", ctx.Output);
    }

    // ============================================================================== physics

    [Fact]
    public void Physics_Disabled_ByDefault()
    {
        var world = NewWorld();
        Cvars.Set("g_physics_clientselect", "0"); // shipped default
        CommandContext ctx = world.Commands.Execute("physics quake", isServerConsole: false, caller: NewCaller());
        Assert.Contains("disabled", ctx.Output);
    }

    [Fact]
    public void Physics_List_ShowsOptions_WhenEnabled()
    {
        var world = NewWorld();
        Cvars.Set("g_physics_clientselect", "1");
        Cvars.Set("g_physics_clientselect_options", "xonotic quake cpma");
        CommandContext ctx = world.Commands.Execute("physics list", isServerConsole: false, caller: NewCaller());
        Assert.Contains("xonotic quake cpma", ctx.Output);
        Assert.Contains("default", ctx.Output);
    }

    [Fact]
    public void Physics_ValidSet_Changes_WhenEnabled()
    {
        var world = NewWorld();
        Cvars.Set("g_physics_clientselect", "1");
        Cvars.Set("g_physics_clientselect_options", "xonotic quake");
        CommandContext ctx = world.Commands.Execute("physics quake", isServerConsole: false, caller: NewCaller());
        Assert.Contains("successfully changed", ctx.Output);
    }

    // ============================================================================== clientversion

    [Fact]
    public void ClientVersion_RecordsTheVersion_NoCrash()
    {
        var world = NewWorld();
        Player p = NewCaller();
        world.Commands.Execute("clientversion 806", isServerConsole: false, caller: p);
        Assert.Equal(806f, world.Commands.GetClientVersion(p));
    }

    [Fact]
    public void ClientVersion_GameversionMacro_IsOne()
    {
        var world = NewWorld();
        Player p = NewCaller();
        world.Commands.Execute("clientversion $gameversion", isServerConsole: false, caller: p);
        Assert.Equal(1f, world.Commands.GetClientVersion(p));
    }

    // ============================================================================== sentcvar (T54 — full suite in CvarReplicationTests)

    [Fact]
    public void Sentcvar_IsRegistered_AndCallerGated()
    {
        var world = NewWorld();
        Assert.True(world.Commands.Has("sentcvar"));
        // server console (no caller) → rejected, like the other ClientCommand_* verbs in this family.
        CommandContext ctx = world.Commands.Execute("sentcvar cl_autoswitch 1", isServerConsole: true);
        Assert.Contains("client command", ctx.Output);
        // a real caller lands in the per-client store and bridges into the T56 autoswitch flag.
        Player p = NewCaller();
        world.Commands.Execute("sentcvar cl_autoswitch 1", isServerConsole: false, caller: p);
        Assert.True(world.Commands.GetAutoswitch(p));
        Assert.Equal("1", world.Commands.GetClientCvar(p, "cl_autoswitch"));
    }

    // ============================================================================== voice

    [Fact]
    public void Voice_InvalidType_Warns()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("voice notarealtaunt", isServerConsole: false, caller: NewCaller());
        Assert.Contains("Invalid voice", ctx.Output);
    }

    [Fact]
    public void Voice_Dead_IsSilent()
    {
        var world = NewWorld();
        Player p = NewCaller();
        p.DeadState = DeadFlag.Dead; // IS_DEAD
        CommandContext ctx = world.Commands.Execute("voice taunt", isServerConsole: false, caller: p);
        Assert.Equal("", ctx.Output.Trim()); // valid type + dead → silent (no warning, no usage)
    }

    [Fact]
    public void Voice_Spectator_IsSilent()
    {
        var world = NewWorld();
        Player p = NewCaller();
        p.FragsStatus = Player.FragsSpectator;
        CommandContext ctx = world.Commands.Execute("voice taunt", isServerConsole: false, caller: p);
        Assert.Equal("", ctx.Output.Trim());
    }

    [Fact]
    public void Voice_LivePlayer_EmitsSound()
    {
        // [A5 #5] CmdVoice now routes through SoundSystem.GlobalSound -> _GlobalSound (QC VoiceMessage), so the
        // taunt reaches the connected-client ROSTER (FOREACH_CLIENT) on CH_VOICE (= SoundChannel.VoiceAuto), not
        // just the emitter on the wrong channel. Connect+join a live player so they're in Clients.Players, open
        // the taunt gate (sv_taunt 1, sv_gentle 0), then voice a taunt and assert it broadcasts on VoiceAuto.
        var world = NewWorld();
        world.Services.Cvars.Set("sv_taunt", "1");
        world.Services.Cvars.Set("sv_gentle", "0");
        // Connect a client so it lands in Clients.Players (the FOREACH_CLIENT recipient roster), then mark it live
        // directly — Join()/Spawn() needs a map spawnpoint the headless test harness doesn't load, but the voice
        // route only needs a non-observer roster member to broadcast to.
        ClientManager.ClientInfo info = world.Clients.ClientConnect(isBot: false, netName: "p");
        Player p = info.Player;
        p.IsObserver = false;
        p.FragsStatus = Player.FragsPlayer;
        Api.Entities.SetOrigin(p, new Vector3(0, 0, 0));

        var sounds = new List<SoundEvent>();
        world.Services.SoundImpl.Broadcast += sounds.Add;
        world.Commands.Execute("voice taunt", isServerConsole: false, caller: p);
        // QC CH_VOICE is the stacking voice channel (-2 = VoiceAuto); the taunt reaches the live roster player.
        Assert.Contains(sounds, s => s.Channel == SoundChannel.VoiceAuto);
    }

    // ============================================================================== suggestmap

    [Fact]
    public void SuggestMap_RecordsSuggestion_AndDedupes()
    {
        var world = NewWorld();
        Player p = NewCaller();
        CommandContext first = world.Commands.Execute("suggestmap dance", isServerConsole: false, caller: p);
        Assert.Contains("accepted", first.Output);
        Assert.Contains(world.Commands.MapSuggestions, s => s.Map == "dance");

        CommandContext dup = world.Commands.Execute("suggestmap dance", isServerConsole: false, caller: p);
        Assert.Contains("already suggested", dup.Output);
        Assert.Single(world.Commands.MapSuggestions);
    }

    [Fact]
    public void SuggestMap_DisableSwitch_RejectsAndDoesNotRecord()
    {
        // F8: QC MapVote_Suggest (mapvoting.qc:134) — g_maplist_votable_suggestions 0 disables suggestions.
        // Ships 2; an admin can turn it off → "Suggestions are not accepted on this server." (and no record).
        var world = NewWorld();
        Cvars.Set("g_maplist_votable_suggestions", "0");
        try
        {
            CommandContext ctx = world.Commands.Execute("suggestmap dance", isServerConsole: false, caller: NewCaller());
            Assert.Contains("Suggestions are not accepted on this server.", ctx.Output);
            Assert.Empty(world.Commands.MapSuggestions);
        }
        finally
        {
            Cvars.Set("g_maplist_votable_suggestions", "2"); // restore the shipped default (shared global state)
        }
    }

    [Fact]
    public void SuggestMap_NonexistentMap_Rejected()
    {
        // F8: QC MapVote_Suggest (mapvoting.qc:138-140) — GameTypeVote_MapInfo_FixName returns null for a map
        // not on the server → "The map you suggested is not available on this server." The port mirrors that with
        // Rotation.MapExists (the same catalog check the console maplist side uses).
        var world = NewWorld();
        world.Rotation.MapExists = m => m == "boil"; // only "boil" exists on this server
        CommandContext ctx = world.Commands.Execute("suggestmap not_a_real_map", isServerConsole: false, caller: NewCaller());
        Assert.Contains("is not available on this server.", ctx.Output);
        Assert.Empty(world.Commands.MapSuggestions);

        // a map that DOES exist is accepted (proving the gate is the existence check, not a blanket reject).
        CommandContext ok = world.Commands.Execute("suggestmap boil", isServerConsole: false, caller: NewCaller());
        Assert.Contains("Suggestion of boil accepted.", ok.Output);
        Assert.Contains(world.Commands.MapSuggestions, s => s.Map == "boil");
    }

    // ============================================================================== reply commands

    [Fact]
    public void PrintMapList_ShowsTheRotation()
    {
        var world = NewWorld();
        Cvars.Set("g_maplist", "boil dance");
        CommandContext ctx = world.Commands.Execute("printmaplist", isServerConsole: true);
        Assert.Contains("Maps in list (2)", ctx.Output);
        Assert.Contains("boil", ctx.Output);
        Assert.Contains("dance", ctx.Output);
    }

    [Fact]
    public void PrintMapList_EmptyRotation()
    {
        var world = NewWorld();
        Cvars.Set("g_maplist", "");
        CommandContext ctx = world.Commands.Execute("printmaplist", isServerConsole: true);
        Assert.Contains("Map list is empty", ctx.Output);
    }

    [Fact]
    public void PrintMapList_EmptyMaplist_ListsTheFullCatalog()
    {
        // An empty g_maplist is not an empty rotation — it means "every map that supports the current
        // gametype" — so the reply lists what the rotation would actually cycle (xonotic-data#3002).
        var world = NewWorld();
        Cvars.Set("g_maplist", "");
        world.Rotation.AllowedMaps = () => new[] { "boil", "dance", "stormkeep" };
        CommandContext ctx = world.Commands.Execute("printmaplist", isServerConsole: true);
        Assert.Contains("Maps in list (3)", ctx.Output);
        Assert.Contains("stormkeep", ctx.Output);
    }

    [Fact]
    public void PrintMapList_LongCatalog_IsPagedNotTruncated()
    {
        // QC maplist_reply[] paging: a list too long for one reply line is split over several, so every map
        // is still shown. A count cap would silently hide maps, which is the shape of the #3002 bug.
        var world = NewWorld();
        Cvars.Set("g_maplist", "");
        string[] catalog = Enumerable.Range(0, 3000).Select(i => $"map{i}").ToArray();
        world.Rotation.AllowedMaps = () => catalog;
        CommandContext ctx = world.Commands.Execute("printmaplist", isServerConsole: true);

        Assert.Contains($"Maps in list ({catalog.Length})", ctx.Output);
        Assert.DoesNotContain("not listed", ctx.Output);      // 3000 maps fit inside the page budget
        Assert.Contains("map0 ", ctx.Output);                 // first
        Assert.Contains($"map{catalog.Length - 1}", ctx.Output); // and last — nothing was dropped
        foreach (string line in ctx.Output.Split('\n'))
            Assert.True(line.Length <= CommandReplies.MaplistPageMaxLen + 64,
                $"a reply line exceeded the page budget: {line.Length}");
    }

    [Fact]
    public void PrintMapList_BeyondEveryPage_CountsWhatItCannotShow()
    {
        // Only when the list outruns every page does the reply report a remainder — and the total stays honest.
        var world = NewWorld();
        Cvars.Set("g_maplist", "");
        int perPage = CommandReplies.MaplistPageMaxLen / 12;                   // ~"^3mapNNNNN " per entry
        string[] catalog = Enumerable.Range(0, perPage * (CommandReplies.MaplistReplyPages + 4))
            .Select(i => $"map{i:D6}").ToArray();
        world.Rotation.AllowedMaps = () => catalog;
        CommandContext ctx = world.Commands.Execute("printmaplist", isServerConsole: true);

        Assert.Contains($"Maps in list ({catalog.Length})", ctx.Output);
        Assert.Contains("not listed", ctx.Output);
        Assert.Equal(CommandReplies.MaplistReplyPages, ctx.Output.TrimEnd('\n').Split('\n').Length);
    }

    [Fact]
    public void Rankings_NonRaceMode_HonestEmpty()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("rankings", isServerConsole: true);
        Assert.Contains("No records are available", ctx.Output);
    }

    [Fact]
    public void Ladder_NoStore_HonestEmpty()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("ladder", isServerConsole: true);
        Assert.Contains("No ladder", ctx.Output);
    }

    [Fact]
    public void Records_NoStore_HonestEmpty()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("records", isServerConsole: true);
        Assert.Contains("No records", ctx.Output);
    }

    [Fact]
    public void Records_CtfPagesUseMapPositionsAndSeparateFastestCapture()
    {
        var world = NewWorld("ctf");
        string[] maps = Enumerable.Range(0, 2001).Select(i => $"report-map-{i:0000}").ToArray();
        world.Commands.Replies.RecordMapCatalog = mode =>
        {
            Assert.Equal("ctf", mode);
            return maps;
        };
        RaceRecords.Clear();
        try
        {
            RaceRecords.WriteCaptureBest(maps[199], 9.25f, "Page one");
            RaceRecords.WriteCaptureBest(maps[200], 12.5f, "Page two");
            RaceRecords.WriteCaptureBest(maps[1999], 21f, "Page ten");
            RaceRecords.WriteCaptureBest(maps[2000], 22f, "Outside page limit");
            RaceRecords.WriteCaptureBest("unmounted", 1f, "Outside catalog");
            RaceRecords.SetTime(maps[200], RaceRecords.CtfRecord, 10f, "other", "Ranked but separate");

            string first = world.Commands.Execute("records 1", isServerConsole: true).Output;
            string second = world.Commands.Execute("records 2", caller: NewCaller()).Output;
            Assert.Equal(maps[199].PadLeft(32) + " 9.25   Page one\n", first);
            Assert.Equal(maps[200].PadLeft(32) + " 12.50  Page two\n", second);
            string tenth = world.Commands.Execute("records 10", isServerConsole: true).Output;
            Assert.Contains("Page ten", tenth);
            string all = world.Commands.Execute("records", isServerConsole: true).Output;
            Assert.Equal(first + second + tenth, all);
            foreach (string arg in new[] { "3", "11", "-1", "invalid" })
                Assert.Equal(all, world.Commands.Execute("records " + arg, isServerConsole: true).Output);
        }
        finally { RaceRecords.Clear(); }
    }

    [Theory]
    [InlineData("rc", RaceRecords.RaceRecord, RaceRecords.CtsRecord)]
    [InlineData("cts", RaceRecords.CtsRecord, RaceRecords.RaceRecord)]
    public void Records_RaceModesReturnScopedRankOneInClockFormat(string mode, string activeType, string otherType)
    {
        var world = NewWorld(mode);
        world.Commands.Replies.RecordMapCatalog = requested =>
        {
            Assert.Equal(mode, requested);
            return new[] { "boil", "record-other" };
        };
        RaceRecords.Clear();
        try
        {
            RaceRecords.SetTime("boil", activeType, 61.25f, "record-first", "First");
            RaceRecords.SetTime("boil", activeType, 70f, "record-second", "Second");
            RaceRecords.SetTime("boil", otherType, 1f, "record-other", "Other mode");
            RaceRecords.SetTime("record-other", activeType, 4.5f, "record-other", "Other map");
            string report = world.Commands.Execute("records", caller: NewCaller()).Output;
            Assert.Equal("boil".PadLeft(32) + " 1:01.25  First\n"
                        + "record-other".PadLeft(32) + " 0:04.50  Other map\n", report);
            string rankings = world.Commands.Execute("rankings", caller: NewCaller()).Output;
            Assert.Contains("Second", rankings);
            Assert.DoesNotContain("Other", rankings);
        }
        finally { RaceRecords.Clear(); }
    }

    [Fact]
    public void Ladder_CombinesCatalogPositionsAndCtsSpeedByIdentity()
    {
        var world = NewWorld("cts");
        world.Commands.Replies.RecordMapCatalog = _ => new[] { "boil", "ladder-other" };
        RaceRecords.Clear();
        try
        {
            RaceRecords.SetTime("boil", RaceRecords.CtsRecord, 10f, "first", "First");
            RaceRecords.SetTime("boil", RaceRecords.CtsRecord, 15f, "second", "Same name");
            RaceRecords.SetTime("ladder-other", RaceRecords.CtsRecord, 11f, "second", "Same name");
            RaceRecords.SetTime("ladder-other", RaceRecords.CtsRecord, 17f, "first", "First");
            RaceRecords.SetTime("unmounted", RaceRecords.CtsRecord, 1f, "excluded", "Excluded");
            RaceRecords.WriteSpeedAwardBest("ladder-other", RaceRecords.CtsRecord, 700f, "second");
            RaceRecords.WriteSpeedAwardBest("boil", RaceRecords.RaceRecord, 800f, "first");
            string report = world.Commands.Execute("ladder", caller: NewCaller()).Output;
            Assert.Contains("1st^3| ^7    160^3|", report);
            Assert.Contains("2nd^3| ^7    150^3|", report);
            Assert.True(report.IndexOf("Same name", StringComparison.Ordinal) < report.IndexOf("First", StringComparison.Ordinal));
            Assert.DoesNotContain("Excluded", report);
        }
        finally { RaceRecords.Clear(); }
    }

    [Fact]
    public void Ladder_KeepsCatalogTieOrderAndCapsRanksAndTable()
    {
        var world = NewWorld("rc");
        string[] maps = Enumerable.Range(0, 31).Select(i => $"ladder-{30 - i:00}").ToArray();
        world.Commands.Replies.RecordMapCatalog = _ => maps;
        RaceRecords.Clear();
        try
        {
            for (int i = 0; i < maps.Length; i++)
                RaceRecords.SetTime(maps[i], RaceRecords.RaceRecord, 10f, maps[i], $"Runner {i:00}");
            string report = world.Commands.Execute("ladder", isServerConsole: true).Output;
            string[] rows = report.Split('\n').Where(line => line.Contains("Runner ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(30, rows.Length);
            for (int i = 0; i < rows.Length; i++)
                Assert.EndsWith($" ^7Runner {i:00}", rows[i]);
            Assert.Contains("   0 ^3| ^7", rows[0]);
            world.Commands.Replies.RecordMapCatalog = _ => new[] { maps[0] };
            for (int rank = 2; rank <= 11; rank++)
                RaceRecords.SetTime(maps[0], RaceRecords.RaceRecord, 10f + rank, $"place-{rank}", $"Place {rank}");
            string ranked = world.Commands.Execute("ladder", isServerConsole: true).Output;
            Assert.Contains("Place 10", ranked);
            Assert.DoesNotContain("Place 11", ranked);
        }
        finally { RaceRecords.Clear(); }
    }

    [Fact]
    public void Lsmaps_ListsCatalog()
    {
        var world = NewWorld();
        Cvars.Set("g_maplist", "boil");
        CommandContext ctx = world.Commands.Execute("lsmaps", isServerConsole: true);
        Assert.Contains("Maps available", ctx.Output);
    }

    [Theory]
    [InlineData("ctf")]
    [InlineData("cts")]
    [InlineData("rc")]
    public void Lsmaps_MarksUnplayedMapsUsingTheActiveRecordTable(string mode)
    {
        var world = NewWorld(mode);
        world.Commands.Replies.RecordMapCatalog = _ => new[] { "played", "other-mode", "empty" };
        RaceRecords.Clear();
        try
        {
            if (mode == "ctf")
            {
                RaceRecords.WriteCaptureBest("played", 12.5f, "Capture holder");
                RaceRecords.SetTime("other-mode", RaceRecords.CtfRecord, 9f, "other", "Rank only");
            }
            else
            {
                string active = mode == "rc" ? RaceRecords.RaceRecord : RaceRecords.CtsRecord;
                string other = mode == "rc" ? RaceRecords.CtsRecord : RaceRecords.RaceRecord;
                RaceRecords.SetTime("played", active, 15f, "a", "A");
                RaceRecords.SetTime("other-mode", other, 9f, "b", "B");
            }
            Assert.Equal("^7Maps available (3) (New maps have asterisks marked in blue): ^3played ^4*other-mode ^5*empty\n",
                world.Commands.Execute("lsmaps", caller: NewCaller()).Output);
            Assert.Equal("^7Maps available (1): ^3played", CommandReplies.FormatMapCatalog(new[] { "played" }, mode));
        }
        finally { RaceRecords.Clear(); }
    }

    [Fact]
    public void MapReplies_ReadOnlyTheirOwnCatalogAndRefreshAfterARecord()
    {
        var world = NewWorld("ctf");
        var commands = world.Commands;
        var maps = new[] { "catalog-test" };
        commands.Replies.RecordMapCatalog = _ => maps;
        world.Rotation.AllowedMaps = () => throw new InvalidOperationException("Rotation was not requested.");
        Cvars.Set("g_maplist", "");
        RaceRecords.Clear();
        try
        {
            Assert.Contains("^5*catalog-test", commands.Execute("lsmaps", isServerConsole: true).Output);
            RaceRecords.WriteCaptureBest("catalog-test", 5f, "New holder");
            Assert.Contains("^3catalog-test", commands.Execute("lsmaps", isServerConsole: true).Output);

            Cvars.Set("g_maplist", "first");
            commands.Replies.RecordMapCatalog = _ => throw new InvalidOperationException("Catalog was not requested.");
            Assert.Contains("^3first", commands.Execute("printmaplist", isServerConsole: true).Output);
            commands.Execute("maplist add second", isServerConsole: true);
            Assert.Contains("^3second ^2first", commands.Execute("printmaplist", isServerConsole: true).Output);
        }
        finally { RaceRecords.Clear(); }
    }

    [Fact]
    public void Lsmaps_TruncatesAt250AndCountsOmittedMaps()
    {
        string[] maps = Enumerable.Range(1, 252).Select(i => $"map-{i}").ToArray();
        string reply = CommandReplies.FormatMapCatalog(maps, "dm");
        Assert.StartsWith("^7Maps available (252): ^3map-1 ^2map-2 ", reply);
        Assert.EndsWith("^2map-250 ^7(2 not listed)", reply);
        Assert.DoesNotContain("map-251", reply);
    }

    [Fact]
    public void Maplist_AddChecksTheBspAndCleanupRetainsSupportedDuplicatesInOrder()
    {
        var world = NewWorld();
        world.Rotation.MapExists = map => map is "one" or "two" or "wrong-mode";
        world.Rotation.MapSupportsGametype = map => map != "wrong-mode";
        Cvars.Set("g_maplist", "one missing two one");
        Assert.Contains("does not exist", world.Commands.Execute("maplist add missing", isServerConsole: true).Output);
        string quotedError = world.Commands.Execute("sv_cmd maplist add \"missing map\"", isServerConsole: true).Output;
        Assert.Contains("missing map does not exist", quotedError);
        Assert.Contains("Usage: maplist", quotedError);
        world.Commands.Execute("sv_cmd maplist add \"wrong-mode\"", isServerConsole: true);
        Assert.Equal("wrong-mode one missing two one", Cvars.String("g_maplist"));
        Assert.Contains("Maps in list (3): ^2one ^2two ^3one", world.Commands.Execute("sv_cmd printmaplist", isServerConsole: true).Output);
        world.Commands.Execute("sv_cmd maplist cleanup", isServerConsole: true);
        Assert.Equal("one two one", Cvars.String("g_maplist"));
    }

    // ============================================================================== cvar_changes / purechanges

    [Fact]
    public void CvarChanges_DefaultServer_PrintsDefaultLine()
    {
        // A fresh boot with no overrides → the "default server settings" line (QC the empty case).
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("cvar_changes", isServerConsole: true);
        Assert.Contains("default server settings", ctx.Output);
    }

    [Fact]
    public void CvarChanges_ListsAChangedGameplayCvar()
    {
        var world = NewWorld();
        Cvars.Set("g_balance_health_regen", "0.5"); // a gameplay cvar changed from its default
        CommandContext all = world.Commands.Execute("cvar_changes", isServerConsole: true);
        Assert.Contains("g_balance_health_regen", all.Output);

        CommandContext pure = world.Commands.Execute("cvar_purechanges", isServerConsole: true);
        Assert.Contains("g_balance_health_regen", pure.Output); // gameplay-relevant → in the pure log too
    }

    [Fact]
    public void CvarChanges_ExcludesClientCvars()
    {
        var world = NewWorld();
        Cvars.Set("cl_forwardspeed", "999"); // a client cvar — excluded from the server log
        CommandContext ctx = world.Commands.Execute("cvar_changes", isServerConsole: true);
        Assert.DoesNotContain("cl_forwardspeed", ctx.Output);
    }

    // ============================================================================== editmob

    [Fact]
    public void EditMob_Butcher_ServerOnly_RemovesAllMonsters()
    {
        var world = NewWorld();
        Cvars.Set("g_monsters", "1");
        // spawn two monsters directly via the public API (the spawnmob path; butcher must remove them).
        Entity m1 = MonsterAI.SpawnMonster(Api.Entities.Spawn(), "zombie", null, null, null,
            new Vector3(100, 0, 0), respawn: false, removeIfInvalid: false, moveFlags: 0)!;
        Entity m2 = MonsterAI.SpawnMonster(Api.Entities.Spawn(), "zombie", null, null, null,
            new Vector3(200, 0, 0), respawn: false, removeIfInvalid: false, moveFlags: 0)!;
        Assert.NotNull(m1);
        Assert.NotNull(m2);

        CommandContext ctx = world.Commands.Execute("editmob butcher", isServerConsole: true); // caller null
        Assert.Contains("Killed", ctx.Output);
        Assert.Empty(Api.Entities.FindByClass("monster").Where(e => MonsterAI.StateOf(e) is not null));
    }

    [Fact]
    public void EditMob_Butcher_RejectedForPlayers()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("editmob butcher", isServerConsole: false, caller: NewCaller());
        Assert.Contains("not available to players", ctx.Output);
    }

    [Fact]
    public void EditMob_SpawnList_ShowsMonsterRegistry()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("editmob spawn list", isServerConsole: false, caller: NewCaller());
        Assert.Contains("Monsters available", ctx.Output);
    }

    [Fact]
    public void EditMob_DisabledInCampaign()
    {
        var world = NewWorld();
        Cvars.Set("g_campaign", "1");
        CommandContext ctx = world.Commands.Execute("editmob butcher", isServerConsole: true);
        Assert.Contains("disabled in singleplayer", ctx.Output);
        Cvars.Set("g_campaign", "0");
    }

    [Fact]
    public void EditMob_Spawn_DisabledByDefault_PerPlayerZero()
    {
        // Shipped g_monsters_max_perplayer is 0 → editmob spawn prints "Monster spawning is disabled".
        var world = NewWorld();
        Cvars.Set("g_monsters", "1");
        Cvars.Set("g_monsters_max", "20");
        Cvars.Set("g_monsters_max_perplayer", "0");
        Player p = NewCaller();
        CommandContext ctx = world.Commands.Execute("editmob spawn zombie", isServerConsole: false, caller: p);
        Assert.Contains("disabled", ctx.Output);
    }

    [Fact]
    public void SpawnMob_Alias_RoutesToEditmobSpawn()
    {
        // The cfg alias `spawnmob <type>` must hit the same path as `editmob spawn <type>` (here: the disabled
        // branch with the shipped per-player 0, proving the alias rewires argv correctly).
        var world = NewWorld();
        Cvars.Set("g_monsters", "1");
        Cvars.Set("g_monsters_max_perplayer", "0");
        CommandContext ctx = world.Commands.Execute("spawnmob zombie", isServerConsole: false, caller: NewCaller());
        Assert.Contains("disabled", ctx.Output);
    }

    [Fact]
    public void EditMob_Spawn_SpawnsAMonster_WhenEnabledAndLooking()
    {
        var world = NewWorld();
        Cvars.Set("g_monsters", "1");
        Cvars.Set("g_monsters_max", "20");
        Cvars.Set("g_monsters_max_perplayer", "5");
        Cvars.Set("g_campaign", "0");
        Player p = NewCaller();
        p.Mins = new Vector3(-16, -16, -24);
        p.Maxs = new Vector3(16, 16, 45);
        p.ViewOfs = new Vector3(0, 0, 35);
        Api.Entities.SetOrigin(p, new Vector3(0, 0, 0));

        int before = Api.Entities.FindByClass("monster").Count(e => MonsterAI.StateOf(e) is not null);
        CommandContext ctx = world.Commands.Execute("editmob spawn zombie", isServerConsole: false, caller: p);
        int after = Api.Entities.FindByClass("monster").Count(e => MonsterAI.StateOf(e) is not null);
        Assert.True(after > before, $"expected a monster to spawn; output: {ctx.Output}");
    }

    [Fact]
    public void EditMob_Spawn_RejectedWhileDrivingVehicle()
    {
        // F21: QC common.qc:370 — a seated pilot can't spawn monsters ("You can't spawn monsters while driving
        // a vehicle"). Same fully-enabled setup as the spawn-success test, but caller.Vehicle is non-null → the
        // gate fires and NO monster spawns.
        var world = NewWorld();
        Cvars.Set("g_monsters", "1");
        Cvars.Set("g_monsters_max", "20");
        Cvars.Set("g_monsters_max_perplayer", "5");
        Cvars.Set("g_campaign", "0");
        Player p = NewCaller();
        p.Mins = new Vector3(-16, -16, -24);
        p.Maxs = new Vector3(16, 16, 45);
        p.ViewOfs = new Vector3(0, 0, 35);
        Api.Entities.SetOrigin(p, new Vector3(0, 0, 0));
        p.Vehicle = Api.Entities.Spawn(); // QC: caller.vehicle set → seated in a vehicle

        int before = Api.Entities.FindByClass("monster").Count(e => MonsterAI.StateOf(e) is not null);
        CommandContext ctx = world.Commands.Execute("editmob spawn zombie", isServerConsole: false, caller: p);
        int after = Api.Entities.FindByClass("monster").Count(e => MonsterAI.StateOf(e) is not null);
        Assert.Contains("You can't spawn monsters while driving a vehicle", ctx.Output);
        Assert.Equal(before, after); // gated → nothing spawned
    }
}

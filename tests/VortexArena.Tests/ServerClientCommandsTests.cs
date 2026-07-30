using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

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
    public void Lsmaps_ListsCatalog()
    {
        var world = NewWorld();
        Cvars.Set("g_maplist", "boil");
        CommandContext ctx = world.Commands.Execute("lsmaps", isServerConsole: true);
        Assert.Contains("Maps available", ctx.Output);
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

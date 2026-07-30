using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using XonoticGodot.Server.Bot;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T39 — the HavocBot LIVE-LOOP wiring (QC bot_serverframe / bot_fixcount / sys_phys_ai): the gap these pin
/// is that BotBrain/BotController were complete but had ZERO live callers, so <c>--bots N</c> spawned inert
/// standing bots. Covered here:
///   - the pure bot_fixcount target math (bot_number / minplayers fill, bot_vs_human, caps, empty-server);
///   - the GameWorld integration: bot_number fills one bot per frame after the 2.5s sentinel, the bots spawn
///     with a model, MOVE, acquire each other as enemies and fire through the human weapon path, and a
///     bot_number drop trims them again;
///   - a dead bot presses jump while DEAD_DEAD (QC bot_think:144-147) and respawns through the DEAD_* machine;
///   - the waypoint load-once path through GameWorld.ConfigReader (.waypoints + .cache, no AutoLink);
///   - BotDanger.CheckDanger classification (clear / lava / trigger_hurt).
/// </summary>
[Collection("GlobalState")]
public class BotLiveLoopTests
{
    public BotLiveLoopTests()
    {
        // reset ambient global state per test (the suite runs single-threaded; see TestParallelization).
        Api.Services = new EngineServices(new CollisionWorld());
        Cvars.RegisterDefaults();
    }

    // =============================================================================================
    // harness
    // =============================================================================================

    /// <summary>A big flat floor (Quake Z-up, top at Z=0) like the VehicleRuntimeTests harness.</summary>
    private static CollisionWorld FlatFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    private static List<EntityDict> SpawnDicts(params Vector3[] spots)
    {
        var dicts = new List<EntityDict> { new("worldspawn") };
        foreach (Vector3 s in spots)
            dicts.Add(new EntityDict("info_player_deathmatch", s));
        return dicts;
    }

    /// <summary>Advance the world tick-by-tick until its sim time reaches <paramref name="until"/>.</summary>
    private static void RunTo(GameWorld world, float until, Action? perFrame = null)
    {
        while (world.Time < until)
        {
            world.Frame(SimulationLoop.TicRate);
            perFrame?.Invoke();
        }
    }

    // =============================================================================================
    // bot_fixcount target math (QC bot.qc:623-664)
    // =============================================================================================

    private static int Target(float botNumber = 0, float minPlayers = 0, float minPerTeam = 0, int activeHumans = 0,
        int humans = 0, bool teamplay = false, int teams = 0, float botVsHuman = 0, int playerLimit = 0,
        int maxClients = 16, int currentBots = 0, float time = 10f, bool joinEmpty = false)
        => BotPopulation.TargetBotCount(botVsHuman, teams, activeHumans, humans, teamplay,
            minPlayers, minPerTeam, botNumber, playerLimit, maxClients, currentBots, time, joinEmpty);

    [Fact]
    public void FixCount_BotNumber_FillsWithHumansPresent()
        => Assert.Equal(3, Target(botNumber: 3, activeHumans: 1, humans: 1));

    [Fact]
    public void FixCount_MinPlayers_FillsToMinusActiveHumans()
        => Assert.Equal(3, Target(minPlayers: 4, activeHumans: 1, humans: 1));

    [Fact]
    public void FixCount_BotNumberWins_WhenLargerThanMinplayersFill()
        => Assert.Equal(5, Target(botNumber: 5, minPlayers: 4, activeHumans: 2, humans: 2));

    [Fact]
    public void FixCount_TeamplayMinPerTeam_ScalesByTeams()
        => Assert.Equal(3, Target(minPerTeam: 2, teamplay: true, teams: 2, activeHumans: 1, humans: 1));

    [Fact]
    public void FixCount_NoHumans_NoJoinEmpty_TearsDown()
        => Assert.Equal(0, Target(botNumber: 4, currentBots: 4, time: 10f));

    [Fact]
    public void FixCount_NoHumans_LevelChangeGrace_KeepsBotsBefore5s()
        => Assert.Equal(4, Target(botNumber: 4, currentBots: 4, time: 4f));

    [Fact]
    public void FixCount_JoinEmpty_KeepsBotsWithNoHumans()
        => Assert.Equal(4, Target(botNumber: 4, joinEmpty: true));

    [Fact]
    public void FixCount_MaxClients_CapsTheFill()
        => Assert.Equal(6, Target(botNumber: 9, activeHumans: 2, humans: 2, maxClients: 8));

    [Fact]
    public void FixCount_PlayerLimit_CapsTheFill()
        => Assert.Equal(4, Target(botNumber: 9, activeHumans: 2, humans: 2, playerLimit: 6));

    [Fact]
    public void FixCount_BotVsHuman_RatioOfActiveHumans()
        => Assert.Equal(2, Target(botVsHuman: 1f, teams: 2, activeHumans: 2, humans: 2, teamplay: true));

    // =============================================================================================
    // GameWorld integration: fill → spawn → move → fight → trim (the live loop)
    // =============================================================================================

    [Fact]
    public void LiveLoop_BotsFill_Move_Fight_AndTrim()
    {
        var world = new GameWorld(FlatFloor(), SpawnDicts(
            new Vector3(-256f, 0f, 32f), new Vector3(256f, 0f, 32f), new Vector3(0f, 320f, 32f)));
        world.Boot("dm");
        // QC: with zero real clients bots only stay when bot_join_empty is set (bot.qc:644).
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("bot_number", "2");
        Cvars.Set("skill", "8");

        // (c) the time<2.5 sentinel: nothing fills early (QC bot.qc:712-716).
        RunTo(world, 2.0f);
        Assert.Equal(0, world.Clients.BotCount);

        // fill: one bot per frame from time 2.5 (QC bot_fixcount one-add-per-frame).
        RunTo(world, 4.0f);
        Assert.Equal(2, world.Clients.BotCount);
        Assert.Equal(2, world.Bots.Brains.Count);
        var spawnOrigins = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            Player bot = world.Bots.Brains[i].Bot;
            Assert.False(bot.IsObserver);                       // auto-joined + spawned
            Assert.False(string.IsNullOrEmpty(bot.Model));      // has a player model
            Assert.False(string.IsNullOrEmpty(bot.NetName));
            spawnOrigins[i] = bot.Origin;
        }
        // the waypoint graph loaded once bots appeared (entity auto-generation on this bare floor).
        Assert.NotNull(world.Bots.Network);

        // run the match: the bots must move under their own input and at least one must pull the trigger
        // (ButtonAttack1 through the human WeaponFireDriver path — enemies have clear LOS on a flat floor).
        bool anyAttack = false, anyEnemy = false;
        RunTo(world, 12f, () =>
        {
            foreach (BotBrain b in world.Bots.Brains)
            {
                anyAttack |= b.LastInput.ButtonAttack1;
                anyEnemy |= b.Enemy is not null;
            }
        });
        for (int i = 0; i < world.Bots.Brains.Count; i++)
        {
            Player bot = world.Bots.Brains[i].Bot;
            if (bot.IsDead) continue; // a mid-fight corpse is parked; the living must have moved
            Assert.True((bot.Origin - spawnOrigins[System.Math.Min(i, 1)]).Length() > 64f,
                $"bot {i} did not move (at {bot.Origin})");
        }
        Assert.True(anyEnemy, "no bot ever acquired an enemy");
        Assert.True(anyAttack, "no bot ever pressed attack");

        // trim: dropping bot_number removes the excess (QC bot_removenewest while currentbots > target).
        Cvars.Set("bot_number", "0");
        Cvars.Set("bot_join_empty", "0");
        RunTo(world, world.Time + 1f);
        Assert.Equal(0, world.Clients.BotCount);
        Assert.Empty(world.Bots.Brains);
    }

    [Fact]
    public void LiveLoop_DeadBot_PressesJumpWhenDeadDead_AndRespawns()
    {
        var world = new GameWorld(FlatFloor(), SpawnDicts(
            new Vector3(-128f, 0f, 32f), new Vector3(128f, 0f, 32f)));
        world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("bot_number", "1");
        RunTo(world, 3.5f);
        Assert.Equal(1, world.Clients.BotCount);
        BotBrain brain = world.Bots.Brains[0];
        Player bot = brain.Bot;

        // kill it (the death edge: DeadPlayerThink computes the respawn timing on its own).
        bot.DeadState = DeadFlag.Dying;
        bot.SetResourceExplicit(ResourceType.Health, 0f);

        // QC bot_think:144-147: the bot RELEASES jump in DEAD_DYING (so the keydown EDGE registers) then PRESSES
        // it in DEAD_DEAD to ask for respawn. That jump press is exactly what advances the button-gated DEAD_*
        // machine DEAD→RESPAWNABLE — so the brain produces it WHILE it sees DEAD_DEAD, and the SAME tick's
        // DeadPlayerThink consumes it and moves the state on to RESPAWNABLE (atomic, like QC's bot_think +
        // PlayerThink in one frame). So observe the jump the brain produced for the dead state at PRODUCTION time
        // (prevState was DEAD_DEAD), not the post-frame state which has already advanced.
        bool jumpWhileDeadDead = false, jumpWhileDying = false;
        DeadFlag prevState = bot.DeadState;
        RunTo(world, world.Time + 6f, () =>
        {
            // the brain pressed jump and the state advanced DEAD_DEAD -> RESPAWNABLE this frame: that jump WAS the
            // dead-dead respawn press (the brain only presses jump while .deadflag==DEAD_DEAD).
            if (prevState == DeadFlag.Dead && bot.DeadState == DeadFlag.Respawnable)
                jumpWhileDeadDead |= brain.LastInput.ButtonJump;
            // a frame that stays in DEAD_DEAD: the brain is holding jump (or between throttled thinks).
            if (prevState == DeadFlag.Dead && bot.DeadState == DeadFlag.Dead)
                jumpWhileDeadDead |= brain.LastInput.ButtonJump;
            // DEAD_DYING must never show a pressed jump (the released-frame keydown-edge rule).
            if (prevState == DeadFlag.Dying && bot.DeadState == DeadFlag.Dying)
                jumpWhileDying |= brain.LastInput.ButtonJump;
            prevState = bot.DeadState;
        });

        Assert.True(jumpWhileDeadDead, "bot never pressed jump while DEAD_DEAD (QC bot_think:147)");
        Assert.False(jumpWhileDying, "jump must stay RELEASED during DEAD_DYING so the keydown edge registers");
        Assert.False(bot.IsDead); // respawned through the DEAD_* machine
    }

    [Fact]
    public void LiveLoop_StrategyToken_KeepsRotating_WhenAHolderDies()
    {
        // QC havocbot_ai:103 sets bot_strategytoken_taken = true UNCONDITIONALLY before the dead/frozen return
        // at :113, so a dead token-holder STILL releases the token and bot_serverframe (:786-813) passes it on.
        // The port's regression was: ThinkProduce returned early for a dead bot BEFORE consuming the token, so
        // RotateStrategyToken (gated on _tokenTaken) froze it on the corpse and the WHOLE population stopped
        // re-rating goals until that bot respawned. This pins the fix: with a holder kept dead, the token must
        // still leave it and keep cycling among the living bots.
        var world = new GameWorld(FlatFloor(), SpawnDicts(
            new Vector3(-256f, 0f, 32f), new Vector3(256f, 0f, 32f), new Vector3(0f, 320f, 32f)));
        world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("bot_number", "3");
        Cvars.Set("skill", "5");
        RunTo(world, 4.0f);
        Assert.Equal(3, world.Clients.BotCount);

        // settle a couple of frames so exactly one brain holds the token, then pick that holder as the victim.
        RunTo(world, world.Time + 0.2f);
        int Holder()
        {
            for (int i = 0; i < world.Bots.Brains.Count; i++)
                if (world.Bots.Brains[i].StrategyTokenHeld) return i;
            return -1;
        }
        int victimIdx = Holder();
        Assert.InRange(victimIdx, 0, 2);                  // some bot holds the token
        Player victim = world.Bots.Brains[victimIdx].Bot;

        // keep the victim permanently dead (re-stamp each frame so it never respawns through the DEAD_* machine).
        // Observe: the token must move OFF the dead holder, and a LIVING bot must hold it on multiple frames
        // (proving rotation never froze on the corpse).
        bool tokenLeftCorpse = false;
        var livingHolders = new HashSet<int>();
        int framesWhereCorpseHeld = 0, frames = 0;
        RunTo(world, world.Time + 3f, () =>
        {
            victim.DeadState = DeadFlag.Dying;
            victim.SetResourceExplicit(ResourceType.Health, 0f);

            frames++;
            int h = Holder();
            if (h < 0) return;
            if (world.Bots.Brains[h].Bot == victim) framesWhereCorpseHeld++;
            else { tokenLeftCorpse = true; livingHolders.Add(h); }
        });

        Assert.True(tokenLeftCorpse, "token never left the dead holder (deadlock)");
        Assert.True(livingHolders.Count >= 1, "no living bot ever held the token while a holder was dead");
        // the corpse may legitimately hold it for the one handoff frame, but must not monopolize it.
        Assert.True(framesWhereCorpseHeld < frames,
            $"token stuck on the corpse for all {frames} frames (deadlock)");
    }

    // =============================================================================================
    // waypoint load-once via the ConfigReader (QC waypoint_loadall + waypoint_load_links)
    // =============================================================================================

    [Fact]
    public void Waypoints_LoadOnce_FromConfigReader_WithCache()
    {
        // two waypoints + a precompiled 2-link cache (the vtos single-quote vector form the real files use).
        const string wpText = "'-64 0 32'\n'-64 0 32'\n0\n'192 0 32'\n'192 0 32'\n0\n";
        const string cacheText = "'-64 0 32'*'192 0 32'\n'192 0 32'*'-64 0 32'\n";
        var reads = new List<string>();
        var world = new GameWorld(FlatFloor(), SpawnDicts(new Vector3(0f, 0f, 32f))) { MapName = "testmap" };
        world.ConfigReader = path =>
        {
            reads.Add(path);
            return path switch
            {
                "maps/testmap.waypoints" => wpText,
                "maps/testmap.waypoints.cache" => cacheText,
                _ => null,
            };
        };
        world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("bot_number", "1");
        RunTo(world, 3.5f);

        Assert.NotNull(world.Bots.Network);
        Assert.Equal(2, world.Bots.Network!.Count);                       // the file's nodes, not an auto-graph
        Assert.Contains("maps/testmap.waypoints", reads);
        Assert.Contains("maps/testmap.waypoints.cache", reads);
        Assert.Equal(1, reads.FindAll(r => r == "maps/testmap.waypoints").Count); // loaded ONCE
    }

    [Fact]
    public void Waypoints_NoFile_AutoGeneratesFromEntities()
    {
        var world = new GameWorld(FlatFloor(), SpawnDicts(
            new Vector3(-128f, 0f, 32f), new Vector3(128f, 0f, 32f)));
        world.Boot("dm");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("bot_number", "1");
        RunTo(world, 3.5f);
        Assert.NotNull(world.Bots.Network);
        Assert.True(world.Bots.Network!.Count > 0, "entity auto-graph is empty");
    }

    [Fact]
    public void CandidateModelPaths_ReturnsDistinctRosterModels_FromBotConfig()
    {
        // bots.txt is tab-separated (name, model, …); the client precaches exactly this distinct model set
        // under the loading screen (NetGame perf §9.4) so the bots are texture-cache-hot at first render.
        // Duplicate models collapse to one entry, empty-model rows are skipped, comments are ignored, and the
        // bare model name becomes models/player/<model>.iqm (matching PickNameAndModel's .iqm append).
        const string botsTxt =
            "// roster\n" +
            "Hellfire\tnyx\t0\n" +
            "Toxic\tgakmasked\t0\n" +
            "Scorcher\tnyx\t0\n" +   // duplicate model -> single entry
            "Nameless\t\t0\n";       // empty model -> skipped
        var world = new GameWorld(FlatFloor(), SpawnDicts(new Vector3(0f, 0f, 32f))) { MapName = "testmap" };
        world.ConfigReader = path => path == "bots.txt" ? botsTxt : null;
        world.Boot("dm");

        Assert.Equal(
            new[] { "models/player/nyx.iqm", "models/player/gakmasked.iqm" },
            world.Bots.CandidateModelPaths());
    }

    // =============================================================================================
    // BotDanger (QC havocbot_checkdanger)
    // =============================================================================================

    /// <summary>An approach floor for x&lt;0 (top Z=0) and a pit for x&gt;=0 whose hazard the test chooses.</summary>
    private static CollisionWorld PitWorld(Action<CollisionWorld> addHazard)
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-1024f, -1024f, -64f), new Vector3(0f, 1024f, 0f), SuperContents.Solid));
        addHazard(world);
        world.BuildGrid();
        return world;
    }

    private static Player StandingBot(Vector3 origin)
        => new()
        {
            Origin = origin,
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            ViewOfs = new Vector3(0f, 0f, 35f),
        };

    [Fact]
    public void Danger_ClearFloorAhead_IsSafe()
    {
        Api.Services = new EngineServices(PitWorld(w =>
            w.AddBrush(Brush.FromBox(new Vector3(0f, -1024f, -64f), new Vector3(1024f, 1024f, 0f), SuperContents.Solid))));
        Player bot = StandingBot(new Vector3(-32f, 0f, 24f));
        Vector3 eye = bot.Origin + bot.ViewOfs;
        int r = BotDanger.CheckDanger(bot, eye, eye + new Vector3(64f, 0f, 0f), goalZ: 0f,
            bot.Mins, bot.Maxs, onGround: true, jumpHeld: false, moving: true, committed: false);
        Assert.Equal(0, r);
    }

    [Fact]
    public void Danger_LavaPitAhead_Returns3()
    {
        // pit: solid bottom under a lava pool (the down-trace passes THROUGH the non-solid lava and stops on
        // the bottom; pointcontents 1qu above the endpoint reads LAVA — exactly QC's branch order).
        Api.Services = new EngineServices(PitWorld(w =>
        {
            w.AddBrush(Brush.FromBox(new Vector3(0f, -1024f, -120f), new Vector3(1024f, 1024f, -90f), SuperContents.Solid));
            w.AddBrush(Brush.FromBox(new Vector3(0f, -1024f, -90f), new Vector3(1024f, 1024f, -20f), SuperContents.Lava));
        }));
        Player bot = StandingBot(new Vector3(-32f, 0f, 24f));
        Vector3 eye = bot.Origin + bot.ViewOfs;
        int r = BotDanger.CheckDanger(bot, eye, eye + new Vector3(64f, 0f, 0f), goalZ: 0f,
            bot.Mins, bot.Maxs, onGround: true, jumpHeld: false, moving: true, committed: false);
        Assert.Equal(3, r);
    }

    [Fact]
    public void Danger_TriggerHurtInPit_Returns4()
    {
        // shallow solid pit (only ~66qu below the feet, so neither the cliff nor the lava branch trips) with a
        // trigger_hurt volume hanging in the fall column.
        Api.Services = new EngineServices(PitWorld(w =>
            w.AddBrush(Brush.FromBox(new Vector3(0f, -1024f, -90f), new Vector3(1024f, 1024f, -66f), SuperContents.Solid))));
        Entity hurt = Api.Entities.Spawn();
        hurt.ClassName = "trigger_hurt";
        hurt.AbsMin = new Vector3(0f, -64f, -66f);
        hurt.AbsMax = new Vector3(512f, 64f, -30f);

        Player bot = StandingBot(new Vector3(-32f, 0f, 24f));
        Vector3 eye = bot.Origin + bot.ViewOfs;
        int r = BotDanger.CheckDanger(bot, eye, eye + new Vector3(64f, 0f, 0f), goalZ: 0f,
            bot.Mins, bot.Maxs, onGround: true, jumpHeld: false, moving: true, committed: false);
        Assert.Equal(4, r);
    }

    [Fact]
    public void Danger_DeepCliffAhead_Returns2()
    {
        Api.Services = new EngineServices(PitWorld(w =>
            w.AddBrush(Brush.FromBox(new Vector3(0f, -1024f, -400f), new Vector3(1024f, 1024f, -360f), SuperContents.Solid))));
        Player bot = StandingBot(new Vector3(-32f, 0f, 24f));
        Vector3 eye = bot.Origin + bot.ViewOfs;
        int r = BotDanger.CheckDanger(bot, eye, eye + new Vector3(64f, 0f, 0f), goalZ: 0f,
            bot.Mins, bot.Maxs, onGround: true, jumpHeld: false, moving: true, committed: false);
        Assert.Equal(2, r);
    }
}

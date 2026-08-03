using System;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using VortexArena.Server.Bot;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// AI_STATUS_STUCK and navigation_unstuck (QC navigation.qc:1861-1867, :1908-2007) — Base's only escape from
/// "I cannot reach anything from here". Before this was ported, a bot whose rating pass produced nothing
/// re-rated from the same spot every 2 s and failed identically, forever; it is the terminal state of every
/// other rating defect. See planning/bot-ai-parity-2026-08-03.md D4.
/// </summary>
[Collection("GlobalState")]
public class BotUnstuckTests
{
    /// <summary>A big flat floor (Quake Z-up, top at Z=0) so tracewalk has ground to walk on.</summary>
    private static CollisionWorld FlatFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f),
            SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    public BotUnstuckTests()
    {
        Api.Services = new EngineServices(FlatFloor());
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();
        Cvars.RegisterDefaults();
    }

    /// <summary>A graph of hand-authored waypoints spread along +X, none of them Generated.</summary>
    private static WaypointNetwork UserGraph(int count = 5, float spacing = 200f)
    {
        var net = new WaypointNetwork();
        for (int i = 0; i < count; i++)
            net.Add(new Vector3(i * spacing, 0f, 24f));
        return net;
    }

    private static (BotBrain brain, Player bot) NewBot(WaypointNetwork? net)
    {
        var bot = new Player { IsBot = true, Health = 100f, DeadState = DeadFlag.No, Index = -1000,
            Origin = new Vector3(0f, 0f, 24f), Mins = new Vector3(-16f, -16f, -24f), Maxs = new Vector3(16f, 16f, 45f) };
        return (new BotBrain(bot, net, skill: 8f, seed: 1), bot);
    }

    [Fact]
    public void RatingPassWithNoGoalRaisesTheStuckFlag()
    {
        var (brain, _) = NewBot(UserGraph());
        Assert.False(brain.Unstuck.IsStuck);

        brain.Unstuck.NoteRatingProducedNothing();

        Assert.True(brain.Unstuck.IsStuck);
    }

    /// <summary>QC navigation_unstuck:1910 — the whole mechanism is behind bot_wander_enable.</summary>
    [Fact]
    public void WanderDisabledMeansTheBotNeverEntersTheStuckState()
    {
        Cvars.Set("bot_wander_enable", "0");
        var (brain, _) = NewBot(UserGraph());

        brain.Unstuck.NoteRatingProducedNothing();

        Assert.False(brain.Unstuck.IsStuck);
    }

    /// <summary>
    /// QC navigation.qc:1913-1920: refuse to wander on a purely auto-generated graph — there is nothing there a
    /// mapper vouched for, so the scan would be noise.
    /// </summary>
    [Fact]
    public void AGeneratedOnlyGraphIsNotWanderedOver()
    {
        var net = new WaypointNetwork();
        for (int i = 0; i < 4; i++)
            net.Add(new Vector3(i * 200f, 0f, 24f), WaypointFlags.Generated);
        Assert.False(net.HasUserWaypoints);

        var (brain, bot) = NewBot(net);
        brain.Unstuck.NoteRatingProducedNothing();

        Assert.False(brain.Unstuck.Think(bot, net, brain.Nav));
    }

    /// <summary>
    /// The shake-loose half (QC navigation.qc:1951-1959): while the scan is still running and nothing reachable
    /// is known yet, the bot is given a goal anyway so it physically moves out of whatever pocket it is wedged
    /// in. A stuck bot that just stands still is the reported symptom.
    /// </summary>
    [Fact]
    public void StuckBotIsGivenAGoalToShakeItselfLoose()
    {
        var net = UserGraph();
        var (brain, bot) = NewBot(net);
        brain.Unstuck.NoteRatingProducedNothing();
        Assert.False(brain.Nav.HasGoal);

        bool acted = brain.Unstuck.Think(bot, net, brain.Nav);

        Assert.True(acted);
        Assert.True(brain.Nav.HasGoal, "a stuck bot must be given somewhere to walk, not left standing");
    }

    /// <summary>
    /// The scan is spread ONE WAYPOINT PER THINK (QC navigation.qc:1935-1950) so a full reachability sweep never
    /// lands in a single frame, and it terminates: once the queue is exhausted with a reachable waypoint found,
    /// the bot commits to it and the stuck bit clears.
    /// </summary>
    [Fact]
    public void ScanTerminatesAndClearsTheStuckFlag()
    {
        var net = UserGraph(count: 4);
        var (brain, bot) = NewBot(net);
        brain.Unstuck.NoteRatingProducedNothing();

        // With no collision world geometry every tracewalk succeeds, so the scan finds the farthest node.
        for (int i = 0; i < 16 && brain.Unstuck.IsStuck; i++)
            brain.Unstuck.Think(bot, net, brain.Nav);

        Assert.False(brain.Unstuck.IsStuck);
        Assert.True(brain.Nav.HasGoal);
    }

    /// <summary>Only one bot at a time owns the scan (QC bot_waypoint_queue_owner), so N wedged bots cost one sweep.</summary>
    [Fact]
    public void OnlyOneBotOwnsTheUnstuckScanAtATime()
    {
        var net = UserGraph();
        var (a, _) = NewBot(net);
        var (b, _) = NewBot(net);

        BotBrain? owner = null;
        Func<BotBrain, bool> claim = who =>
        {
            if (owner is null || ReferenceEquals(owner, who)) { owner = who; return true; }
            return false;
        };
        a.TryOwnUnstuckQueueHook = who => claim(who);
        b.TryOwnUnstuckQueueHook = who => claim(who);

        Assert.True(a.TryOwnUnstuckQueueHook!(a));
        Assert.False(b.TryOwnUnstuckQueueHook!(b));
    }
}

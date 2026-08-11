using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Server;
using VortexArena.Server.Bot;
using VortexArena.Server.Bot.Neural;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// The time trial: how long does a bot take to get from A to B, classic steer versus learned policy?
///
/// <para><b>This is the test the whole feature answers to.</b> A policy that is not faster than the
/// existing havocbot on maps it has never trained on has failed, however good its training curves looked.
/// So the bench reports per-map medians against the classic steer on the same (map, origin, target) triples
/// with the same seeds, and it reports HELD-OUT maps separately: a policy fast only where it trained is a
/// lookup table (risk R-N1 in planning/neural-bots-2026-08-07.md).</para>
///
/// <para>Informational, not a CI gate. It no-ops without compiled map content, and it no-ops the neural arm
/// without a weight file, so a checkout with neither still runs green.</para>
///
/// <code>
/// VA_NEURAL_WEIGHTS=runs/latest/policy.vxpw VA_TRIAL_MAPS="stormkeep,catharsis" \
///   dotnet test tests/VortexArena.Tests --filter NeuralTimeTrialBench -l "console;verbosity=detailed"
/// </code>
/// </summary>
[Collection("GlobalState")]
public class NeuralTimeTrialBench
{
    private static readonly string DataDir = TestPaths.Data;

    private static string[] Maps =>
        (Environment.GetEnvironmentVariable("VA_TRIAL_MAPS") ?? "stormkeep,catharsis,fuse")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? WeightsPath => Environment.GetEnvironmentVariable("VA_NEURAL_WEIGHTS");

    /// <summary>Routes per map. Each is one origin/target pair drawn from the waypoint graph.</summary>
    private static int Routes =>
        int.TryParse(Environment.GetEnvironmentVariable("VA_TRIAL_ROUTES"), out int n) ? n : 8;

    /// <summary>Seeds per route, so the medians are over bot-to-bot variance as well as route variance.</summary>
    private static int Seeds =>
        int.TryParse(Environment.GetEnvironmentVariable("VA_TRIAL_SEEDS"), out int n) ? n : 3;

    /// <summary>Give up on a route after this many seconds of sim time and record it as a failure.</summary>
    private const float TimeoutSeconds = 45f;

    /// <summary>How close counts as arrived. Matches the training env's <see cref="TrainingEnv.ArriveRadius"/>.</summary>
    private const float ArriveRadius = 96f;

    private readonly ITestOutputHelper _out;
    public NeuralTimeTrialBench(ITestOutputHelper output) => _out = output;

    private sealed record Result(int Finished, int Attempted, float MedianSeconds, float P90Seconds);

    [Fact]
    public void Benchmark_TimeToTarget_ClassicVersusNeural()
    {
        if (!Directory.Exists(DataDir)) { _out.WriteLine($"content dir missing — skipped ({TestPaths.NoMapsReason})"); return; }

        PolicyNetwork? policy = null;
        if (WeightsPath is { Length: > 0 } wp)
        {
            policy = PolicyNetwork.Load(wp, out string? err);
            if (policy is null) _out.WriteLine($"neural arm skipped: {err}");
            else _out.WriteLine($"neural arm: policy '{policy.Label}' ({policy.ParameterCount:N0} parameters) from {wp}");
        }
        else
        {
            _out.WriteLine("neural arm skipped: set VA_NEURAL_WEIGHTS to a policy file to compare arms");
        }

        _out.WriteLine($"=== time trial: {Routes} routes x {Seeds} seeds, {TimeoutSeconds:F0}s cap ===");
        _out.WriteLine("map            arm      finished    median     p90");

        foreach (string map in Maps)
        {
            using var vfs = new VirtualFileSystem();
            if (!vfs.MountContentRoot(DataDir)) continue;
            string bspPath = $"maps/{map}.bsp";
            if (!vfs.Exists(bspPath)) { _out.WriteLine($"{map,-14} (not installed)"); continue; }

            BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
            BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp);

            // Routes come from the waypoint graph, which is training SCAFFOLDING, not a policy input: it
            // picks where to run, never how. Using it here keeps both arms on identical routes, which is
            // the only way the medians mean anything.
            List<(Vector3 From, Vector3 To)> routes = PickRoutes(vfs, map, built.World);
            if (routes.Count == 0) { _out.WriteLine($"{map,-14} (no waypoint graph — no routes)"); continue; }

            var sw = Stopwatch.StartNew();
            NavField field = NavFieldBaker.BakeParallel(built.World, map, NavFieldIo.GeometryHash(built.World));
            sw.Stop();
            _out.WriteLine($"{map,-14} field {field.OccupiedColumns} cols / {field.SpanCount} spans / " +
                           $"{field.ApproxBytes / 1024} KB, baked in {sw.Elapsed.TotalMilliseconds:F0} ms");

            Result classic = RunArm(map, bsp, built, vfs, routes, field, policy: null);
            Report(map, "classic", classic);

            if (policy is not null)
            {
                Result neural = RunArm(map, bsp, built, vfs, routes, field, policy);
                Report(map, "neural", neural);

                if (classic.Finished > 0 && neural.Finished > 0)
                {
                    float delta = (neural.MedianSeconds - classic.MedianSeconds) / classic.MedianSeconds;
                    _out.WriteLine($"{map,-14} neural is {(delta < 0 ? "FASTER" : "slower")} by " +
                                   $"{MathF.Abs(delta):P1} on the median");
                }
            }
        }

        _out.WriteLine("(informational. The number that decides the feature is the median on maps the policy " +
                       "did NOT train on; report those separately from the training set.)");
        Assert.True(true);
    }

    private void Report(string map, string arm, Result r)
        => _out.WriteLine($"{map,-14} {arm,-8} {r.Finished,3}/{r.Attempted,-3}   " +
                          $"{r.MedianSeconds,7:F2}s {r.P90Seconds,7:F2}s");

    /// <summary>
    /// Run every (route, seed) pair for one arm and return the timing distribution. A route the bot never
    /// finishes contributes the timeout to the distribution rather than being dropped, because dropping it
    /// would let an arm look fast by only completing the easy routes.
    /// </summary>
    private Result RunArm(string map, BspData bsp, BspCollisionBuilder.Result built, VirtualFileSystem vfs,
        List<(Vector3 From, Vector3 To)> routes, NavField field, PolicyNetwork? policy)
    {
        var times = new List<float>();
        int finished = 0;

        foreach ((Vector3 from, Vector3 to) in routes)
        {
            for (int seed = 0; seed < Seeds; seed++)
            {
                float t = RunOne(map, bsp, built, vfs, from, to, field, policy, seed);
                if (t > 0f) { finished++; times.Add(t); }
                else times.Add(TimeoutSeconds);
            }
        }

        times.Sort();
        float median = times.Count > 0 ? times[times.Count / 2] : 0f;
        float p90 = times.Count > 0 ? times[Math.Min(times.Count - 1, (int)(times.Count * 0.9))] : 0f;
        return new Result(finished, times.Count, median, p90);
    }

    /// <summary>Sim seconds to reach the target, or -1 on a timeout.</summary>
    private static float RunOne(string map, BspData bsp, BspCollisionBuilder.Result built, VirtualFileSystem vfs,
        Vector3 from, Vector3 to, NavField field, PolicyNetwork? policy, int seed)
    {
        var world = new GameWorld(built.World, BuildEntityDicts(bsp, from)) { MapName = map };
        world.BrushModels = built.Submodels;
        world.MapBsp = bsp;
        world.Pvs = new BspPvs(bsp);
        world.ConfigReader = path => vfs.Exists(path) ? vfs.ReadText(path) : null;
        SpawnSystem.Reseed(unchecked(seed * 7919 + 17));
        world.Boot("dm");
        world.Bots.SetBotSeed(unchecked(seed + 1));

        Cvars.Set("sv_spectate", "0");
        Cvars.Set("bot_join_empty", "1");
        Cvars.Set("skill", "10");
        // No fighting: this measures locomotion, and a bot that stops to shoot is measuring something else.
        Cvars.Set("bot_nofire", "1");
        Cvars.Set("bot_neural", policy is null ? "0" : "1");
        Cvars.Set("bot_number", "1");

        if (policy is not null)
        {
            var features = new MapFeatures();
            features.Build(world.Services.EntityTable.All);
            world.Bots.Neural = NeuralBotService.ForPreparedMap(policy, field, features, map);
        }

        const float dt = SimulationLoop.TicRate;
        // Let fixcount fill and the bot spawn.
        for (int t = 0; t < 72 * 4 && world.Bots.Brains.Count == 0; t++) world.Frame(dt);
        if (world.Bots.Brains.Count == 0) return -1f;

        BotBrain brain = world.Bots.Brains[0];
        Player bot = brain.Bot;

        // Silence the goal-rating layer for the duration of the trial.
        //
        // Without this, both arms score 0/3 on stormkeep and the bench looks broken: the role re-rates on
        // its 5.5-to-7 second clock, captures whatever item it fancies, and SetGoal replaces the route to
        // the trial's target. The bot then runs a perfectly good route to somewhere else until the clock
        // runs out. A no-op role leaves the pushed goal standing, which is what makes the two arms
        // comparable — this measures locomotion, not goal selection.
        brain.Role = static (_, _) => { };
        brain.Nav.SetGoal(bot.Origin, to, world.Bots.EnsureWaypointNetwork(), null, bot.OnGround);

        // The neural arm reads its destination from the intent rather than the route, so give it the same
        // target directly. Corridor look-ahead points at the route's next node when there is one.
        if (policy is not null)
        {
            brain.IntentOverride = intent =>
            {
                intent.GoalPos = to;
                intent.CorridorA = brain.Nav.RouteNode(1, to);
                intent.CorridorB = brain.Nav.RouteNode(2, to);
                intent.Urgency = 1f;
                return intent;
            };
        }

        float start = world.Time;
        while (world.Time - start < TimeoutSeconds)
        {
            world.Frame(dt);
            if ((bot.Origin - to).Length() <= ArriveRadius) return world.Time - start;

            // Re-push the goal when the route empties, so a bot that arrives at an intermediate node keeps
            // going to the real target rather than idling out the clock.
            if (!brain.Nav.HasGoal)
                brain.Nav.SetGoal(bot.Origin, to, world.Bots.Network, null, bot.OnGround);

            if (bot.IsDead) return -1f;
        }
        return -1f;
    }

    /// <summary>
    /// Origin/target pairs from the map's waypoint graph, spread across the map: node i paired with the
    /// node roughly half the graph away, which on a stock arena is a cross-map run rather than a stroll.
    /// </summary>
    private static List<(Vector3, Vector3)> PickRoutes(VirtualFileSystem vfs, string map, CollisionWorld world)
    {
        var routes = new List<(Vector3, Vector3)>();
        string wpPath = $"maps/{map}.waypoints";
        if (!vfs.Exists(wpPath)) return routes;

        WaypointNetwork net = WaypointNetwork.LoadFromText(vfs.ReadText(wpPath));
        string cachePath = $"maps/{map}.waypoints.cache";
        if (vfs.Exists(cachePath)) net.LoadLinks(vfs.ReadText(cachePath));
        if (net.Count < 4) return routes;

        int stride = Math.Max(1, net.Count / Math.Max(1, Routes));
        for (int i = 0; i < net.Count && routes.Count < Routes; i += stride)
        {
            Waypoint a = net.Nodes[i];
            Waypoint b = net.Nodes[(i + net.Count / 2) % net.Count];
            if ((a.Center - b.Center).Length() < 512f) continue;   // too short to distinguish the arms
            routes.Add((a.Center + new Vector3(0f, 0f, 24f), b.Center));
        }
        _ = world;
        return routes;
    }

    private static List<EntityDict> BuildEntityDicts(BspData bsp, Vector3 spawn)
    {
        var dicts = new List<EntityDict>();
        foreach (Dictionary<string, string> e in bsp.Entities)
        {
            if (!e.TryGetValue("classname", out string? cn) || cn is null) continue;
            var d = new EntityDict(cn);
            if (e.TryGetValue("origin", out string? os) && TryVec(os, out Vector3 origin)) d.Origin = origin;
            foreach (KeyValuePair<string, string> kv in e)
                if (!kv.Key.Equals("classname", StringComparison.OrdinalIgnoreCase)) d.Fields[kv.Key] = kv.Value;
            dicts.Add(d);
        }
        // A spawn point exactly where the trial starts, so both arms begin the run at the same place.
        dicts.Add(new EntityDict("info_player_deathmatch", spawn));
        return dicts;
    }

    private static bool TryVec(string s, out Vector3 v)
    {
        v = default;
        string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }
}

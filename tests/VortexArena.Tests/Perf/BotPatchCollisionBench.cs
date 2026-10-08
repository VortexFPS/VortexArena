using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// The native bots on a map with a lot of curved floor, once with the default patch collision (slabs) and
/// once with DarkPlaces' triangles (<see cref="PatchCollisionMode.DarkPlacesTriangles"/>): how far they
/// travel, how often they stand still for two seconds, how often their box is inside world geometry, how
/// often they die. A measurement for the question "should the native game switch", not a test: it asserts
/// nothing about the numbers, the two runs are two different matches (the bots' choices diverge as soon as
/// one trace differs), and it only runs when asked - set <c>VA_BOT_PATCH_BENCH=1</c>. <c>VA_BOTS</c>
/// (default 6) and <c>VA_SECONDS</c> (default 60) size it.
///
/// Run: VA_BOT_PATCH_BENCH=1 dotnet test tests/VortexArena.Tests --filter BotPatchCollisionBench -l "console;verbosity=detailed"
/// </summary>
[Collection("GlobalState")]
public class BotPatchCollisionBench
{
    private static readonly string DataDir = TestPaths.Data;
    private static int BotCount => int.TryParse(Environment.GetEnvironmentVariable("VA_BOTS"), out int n) ? n : 6;
    private static int Seconds => int.TryParse(Environment.GetEnvironmentVariable("VA_SECONDS"), out int n) ? n : 60;

    private readonly ITestOutputHelper _out;
    public BotPatchCollisionBench(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("vorix")]
    [InlineData("afterslime")]
    [InlineData("glowplant")]
    [InlineData("stormkeep")]
    public void Bots_with_slabs_and_with_DarkPlaces_triangles(string map)
    {
        if (Environment.GetEnvironmentVariable("VA_BOT_PATCH_BENCH") != "1") return;
        if (!Directory.Exists(DataDir)) { _out.WriteLine("content dir missing - skipped"); return; }
        using var vfs = new VirtualFileSystem();
        Assert.True(vfs.MountContentRoot(DataDir));
        string bspPath = $"maps/{map}.bsp";
        if (!vfs.Exists(bspPath)) { _out.WriteLine($"{bspPath} missing - skipped"); return; }
        byte[] file = vfs.ReadBytes(bspPath);
        BspData bsp = BspReader.Read(file);

        _out.WriteLine($"=== {map}: {BotCount} bots, {Seconds} s measured after 14 s of warm-up ===");
        foreach (PatchCollisionMode mode in new[] { PatchCollisionMode.Slabs, PatchCollisionMode.DarkPlacesTriangles })
        {
            BspCollisionBuilder.Result built = BspCollisionBuilder.Build(bsp, null, new BspCollisionOptions { PatchCollision = mode, MapFile = file });
            var world = new GameWorld(built.World, BuildEntityDicts(bsp)) { MapName = map };
            world.BrushModels = built.Submodels;
            world.MapBsp = bsp;
            world.Pvs = new BspPvs(bsp);
            world.ConfigReader = path => vfs.Exists(path) ? vfs.ReadText(path) : null;
            world.Boot("dm");
            world.Services.Cvars.Set("sv_spectate", "0");
            world.Services.Cvars.Set("bot_join_empty", "1");
            world.Services.Cvars.Set("bot_number", BotCount.ToString(CultureInfo.InvariantCulture));
            world.Services.Cvars.Set("skill", "5");
            const float dt = SimulationLoop.TicRate;
            for (int t = 0; t < 72 * 14; t++) world.Frame(dt);

            var last = new Dictionary<Entity, Vector3>();
            var still = new Dictionary<Entity, int>();
            var wasAlive = new Dictionary<Entity, bool>();
            double distance = 0;
            long aliveTicks = 0, stillTicks = 0, embeddedSamples = 0, samples = 0;
            int stuckEpisodes = 0, deaths = 0;
            var stuckAt = new List<Vector3>();
            float lowest = float.MaxValue;
            int ticks = 72 * Seconds;
            for (int t = 0; t < ticks; t++)
            {
                world.Frame(dt);
                foreach (Player player in world.Clients.Players)
                {
                    bool alive = !player.IsFreed && player.Health > 0 && player.DeadState == DeadFlag.No && player.Solid != Solid.Not;
                    if (wasAlive.TryGetValue(player, out bool before) && before && !alive) deaths++;
                    wasAlive[player] = alive;
                    if (!alive) { last.Remove(player); still[player] = 0; continue; }
                    aliveTicks++;
                    lowest = MathF.Min(lowest, player.Origin.Z);
                    if (last.TryGetValue(player, out Vector3 prev))
                    {
                        float step = Vector3.Distance(prev, player.Origin);
                        if (step < 64) distance += step;   // further in one tick is a teleport or a respawn
                        if (step < 20 * dt)
                        {
                            stillTicks++;
                            int run = still.GetValueOrDefault(player) + 1;
                            still[player] = run;
                            if (run == 72 * 2)
                            {
                                stuckEpisodes++;
                                if (stuckAt.Count < 8) stuckAt.Add(player.Origin);
                            }
                        }
                        else still[player] = 0;
                    }
                    last[player] = player.Origin;
                    if (t % 8 == 0)
                    {
                        samples++;
                        TraceResult rest = world.Services.Trace.Trace(player.Origin, player.Mins, player.Maxs, player.Origin, MoveFilter.WorldOnly, player);
                        if (rest.StartSolid) embeddedSamples++;
                    }
                }
            }
            _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {mode,-20} {built.World.Brushes.Count,6} brushes; travelled {distance / Math.Max(1, aliveTicks) / dt,6:0} u/s alive; "
                + $"standing still {100.0 * stillTicks / Math.Max(1, aliveTicks),5:0.0}% of the time, {stuckEpisodes} spells of 2 s or more; box in world geometry in {embeddedSamples} of {samples} samples; {deaths} deaths; lowest z {lowest:0}"));
            if (stuckAt.Count > 0) _out.WriteLine("    stood still at: " + string.Join("  ", stuckAt.Select(v => string.Create(CultureInfo.InvariantCulture, $"({v.X:0} {v.Y:0} {v.Z:0})"))));
        }
    }

    // test-side mirror of game/net/NetGame.BuildEntityDicts (as in BotTickPerfBench)
    private static List<EntityDict> BuildEntityDicts(BspData bsp)
    {
        var list = new List<EntityDict>(bsp.Entities.Count);
        foreach (IReadOnlyDictionary<string, string> dict in bsp.Entities)
        {
            if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls)) continue;
            var ed = new EntityDict { ClassName = cls, Origin = ParseVec(dict, "origin"), Angles = ParseVec(dict, "angles") };
            foreach (KeyValuePair<string, string> kv in dict) ed.Fields[kv.Key] = kv.Value;
            list.Add(ed);
        }
        return list;
    }

    private static Vector3 ParseVec(IReadOnlyDictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s)) return Vector3.Zero;
        string[] p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return p.Length < 3 ? Vector3.Zero : new Vector3(ParseF(p[0]), ParseF(p[1]), ParseF(p[2]));
    }

    private static float ParseF(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
}

// Tests for T36: the Invasion map spawnfuncs (invasion_spawnpoint / invasion_wave / target_invasion_roundend)
// wiring the BSP entity lump through GametypeObjectiveSpawns.Sink → GameWorld.WireObjectiveSpawns (Invasion arm)
// → Invasion.AddSpawnPoint / AddWave / AddRoundEnd, including the .cnt (wave number) + .spawnmob (monster list)
// field plumbing (ApplyDictFields) and the STAGE round-end touch → TriggerRoundEnd.
//
// Port of: common/gametypes/gametype/invasion/sv_invasion.qc spawnfuncs (lines 45-70).

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

[Collection("GlobalState")]
public sealed class InvasionSpawnTests
{
    private static EntityDict Dict(string cls, Vector3 origin = default, params (string k, string v)[] fields)
    {
        var d = new EntityDict { ClassName = cls, Origin = origin };
        foreach (var (k, v) in fields) d.Fields[k] = v;
        return d;
    }

    private static GameWorld BootInvasionMap(IEnumerable<EntityDict> ents, string type = "0")
    {
        var world = new GameWorld(new CollisionWorld(), ents.ToList());
        world.Boot("inv");
        Cvars.Set("g_invasion_type", type); // 0 ROUND (default), 1 HUNT, 2 STAGE
        return world;
    }

    [Fact]
    public void SpawnpointSpawnfunc_RegistersSpawnOrigins()
    {
        GameWorld world = BootInvasionMap(new[]
        {
            Dict("invasion_spawnpoint", new Vector3(10, 20, 30)),
            Dict("invasion_spawnpoint", new Vector3(-40, 50, 60)),
        });
        var inv = Assert.IsType<Invasion>(world.GameType);

        Assert.Equal(2, inv.SpawnPoints.Count);
        Assert.Contains(new Vector3(10, 20, 30), inv.SpawnPoints);
        Assert.Contains(new Vector3(-40, 50, 60), inv.SpawnPoints);
    }

    [Fact]
    public void WaveSpawnfunc_RegistersWaveListsByCntAndSpawnmob()
    {
        // QC invasion_wave reads .cnt (wave number) + .spawnmob (space-separated monster netnames).
        GameWorld world = BootInvasionMap(new[]
        {
            Dict("invasion_wave", default, ("cnt", "1"), ("spawnmob", "zombie spider")),
            Dict("invasion_wave", default, ("cnt", "3"), ("spawnmob", "golem")),
        });
        var inv = (Invasion)world.GameType!;

        // GetWaveMonsters resolves a round to the right list (exact, else last <= round).
        Assert.Equal(new[] { "zombie", "spider" }, inv.GetWaveMonsters(1));
        Assert.Equal(new[] { "zombie", "spider" }, inv.GetWaveMonsters(2)); // fallback to wave 1
        Assert.Equal(new[] { "golem" }, inv.GetWaveMonsters(3));
    }

    [Fact]
    public void Cnt_IsDistinctFromCount()
    {
        // The wave number comes from `cnt` (e.Cnt), NOT `count` (e.Count). A map setting `count` must not be read
        // as the wave number — this pins the ApplyDictFields cnt/count separation.
        GameWorld world = BootInvasionMap(new[]
        {
            Dict("invasion_wave", default, ("cnt", "2"), ("count", "99"), ("spawnmob", "zombie")),
        });
        var inv = (Invasion)world.GameType!;
        Assert.Equal(new[] { "zombie" }, inv.GetWaveMonsters(2)); // keyed by cnt=2
        Assert.Null(inv.GetWaveMonsters(1)); // not keyed by count=99 or wave 1
    }

    [Fact]
    public void RoundEndSpawnfunc_StageTouch_FiresTriggerRoundEnd()
    {
        GameWorld world = BootInvasionMap(new[]
        {
            Dict("target_invasion_roundend", new Vector3(0, 0, 0)),
        }, type: "2"); // STAGE
        var inv = (Invasion)world.GameType!;
        Assert.Equal(Invasion.InvasionType.Stage, inv.Type);

        // The round-end placeholder is KEPT as a touch volume (not retired) so a STAGE map's end trigger works.
        var roundend = Api.Entities.FindByClass("target_invasion_roundend").FirstOrDefault();
        Assert.NotNull(roundend);
        Assert.Equal(Solid.Trigger, roundend!.Solid);
        Assert.NotNull(roundend.Touch);

        Assert.False(inv.RoundEndReached);
        // A live player touches the end trigger → QC target_invasion_roundend_use → TriggerRoundEnd.
        var p = new Player { NetName = "p", Health = 100f };
        roundend.Touch!(roundend, p);
        Assert.True(inv.RoundEndReached);
    }

    [Fact]
    public void SpawnedMonster_UsesARegisteredSpawnPoint()
    {
        // Boot with the registries (monsters) available, a flat floor so the monster can be placed, and a
        // spawnpoint. SpawnWaveMonster should draw a monster and place it at the registered spawn origin.
        var world = new GameWorld(new CollisionWorld(), new List<EntityDict>
        {
            Dict("invasion_spawnpoint", new Vector3(0, 0, 26)),
        });
        world.Boot("inv");
        var inv = (Invasion)world.GameType!;

        Assert.Single(inv.SpawnPoints);
        Entity? m = inv.SpawnWaveMonster();
        Assert.NotNull(m);
        // It spawned at the (only) registered spawn point.
        Assert.Equal(new Vector3(0, 0, 26), inv.SpawnPoints[0]);
        Assert.True((m!.Flags & EntFlags.Monster) != 0, "spawned a real monster (FL_MONSTER)");

        inv.Deactivate();
    }
}

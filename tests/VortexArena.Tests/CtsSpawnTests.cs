// Tests for T36: the CTS start/stop-timer map spawnfuncs (target_startTimer / target_stopTimer +
// target_checkpoint) wiring the BSP entity lump through GametypeObjectiveSpawns.Sink →
// GameWorld.WireObjectiveSpawns (Cts arm) → Cts.SpawnStartTimer / SpawnStopTimer, and the end-to-end
// start-touch → stop-touch → fastest-time path.
//
// Port of: server/race.qc target_checkpoint_setup + spawnfunc(target_startTimer/target_stopTimer) (lines
// 1142-1201, gated `if(!g_race && !g_cts) delete`). The port's CTS models a single start→stop course, so
// target_checkpoint (defrag intermediate) is consumed as a no-op.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

[Collection("GlobalState")]
public sealed class CtsSpawnTests
{
    private static EntityDict Dict(string cls, Vector3 origin = default)
        => new() { ClassName = cls, Origin = origin };

    // ---- A facade with a settable clock (for deterministic run timing) ----
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; } = new(new CollisionWorld());
        public MutableClock GameClock { get; } = new();
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    // =========================================================================== spawn registration (GameWorld)

    [Fact]
    public void StartStopTimerSpawnfuncs_RegisterTimers()
    {
        var world = new GameWorld(new CollisionWorld(), new List<EntityDict>
        {
            Dict("target_startTimer", new Vector3(0, 0, 0)),
            Dict("target_stopTimer", new Vector3(500, 0, 0)),
        });
        world.Boot("cts");
        var cts = Assert.IsType<Cts>(world.GameType);

        Assert.Equal(2, cts.Timers.Count);
        Assert.Contains(cts.Timers, t => t.GtIsStartTimer);
        Assert.Contains(cts.Timers, t => t.GtIsStopTimer);
        // each timer is a live touch volume (QC EXACTTRIGGER_INIT → SOLID_TRIGGER with a touch handler).
        Assert.All(cts.Timers, t => Assert.Equal(Solid.Trigger, t.Solid));
        Assert.All(cts.Timers, t => Assert.NotNull(t.Touch));
    }

    [Fact]
    public void TargetCheckpoint_IsConsumedAsNoOp()
    {
        // The port's CTS has no defrag intermediate checkpoints; target_checkpoint must be consumed (no timer
        // registered, no "unhandled class" — it has a registered spawnfunc).
        var world = new GameWorld(new CollisionWorld(), new List<EntityDict>
        {
            Dict("target_startTimer", new Vector3(0, 0, 0)),
            Dict("target_checkpoint", new Vector3(250, 0, 0)),
            Dict("target_stopTimer", new Vector3(500, 0, 0)),
        });
        world.Boot("cts");
        var cts = (Cts)world.GameType!;

        Assert.Equal(2, cts.Timers.Count); // only start + stop, not the intermediate checkpoint
        Assert.DoesNotContain("target_checkpoint", world.UnhandledClasses);
    }

    // ============================================== Q3DF target_score / target_fragsFilter CTS gate (T52)

    [Fact]
    public void TargetScoreAndFragsFilter_SurviveOnCtsMap_WithPromotedFragsThreshold()
    {
        // A target_fragsFilter with a mapper-set custom threshold (frags "5"), plus a target_score, on a CTS map:
        // the CTS gate (CompatRemaps.IsCtsActive, wired in GameWorld.Boot) must keep both alive, and the 'frags'
        // key must be promoted by ApplyDictFields so the filter gates on 5, not the hardcoded default 1.
        var filterDict = Dict("target_fragsFilter", new Vector3(0, 0, 0));
        filterDict.Fields["frags"] = "5";
        var world = new GameWorld(new CollisionWorld(), new List<EntityDict>
        {
            Dict("target_score", new Vector3(100, 0, 0)),
            filterDict,
        });
        world.Boot("cts");
        Assert.IsType<Cts>(world.GameType);

        Entity score = Assert.Single(Api.Entities.FindByClass("target_score"));
        Assert.NotNull(score.Use);                 // .use wired → it survived the !g_cts gate
        Assert.Equal(1, score.Count);              // QC: count unset → 1

        Entity filter = Assert.Single(Api.Entities.FindByClass("target_fragsFilter"));
        Assert.NotNull(filter.Use);                // survived the gate
        Assert.Equal(5f, filter.Frags);            // the mapper's frags "5" was promoted (not defaulted to 1)
    }

    [Fact]
    public void TargetScoreAndFragsFilter_DeletedOffCtsMap()
    {
        // The same entities on a non-CTS (DM) map must self-delete (QC: if(!g_cts) { delete(this); return; }).
        var world = new GameWorld(new CollisionWorld(), new List<EntityDict>
        {
            Dict("target_score", new Vector3(100, 0, 0)),
            Dict("target_fragsFilter", new Vector3(0, 0, 0)),
        });
        world.Boot("dm");

        Assert.Empty(Api.Entities.FindByClass("target_score"));
        Assert.Empty(Api.Entities.FindByClass("target_fragsFilter"));
    }

    // =========================================================================== end-to-end timing (touch path)

    [Fact]
    public void EndToEnd_CrossStartThenStop_RecordsRunTime()
    {
        var f = new TestFacade();
        Api.Services = f;
        GameInit.Boot(f); // registers the spawnfuncs (and Movement/registries); Api.Services = f
        RaceRecords.Clear();
        f.GameClock.FrameTime = 1f / 60f;

        var cts = new Cts();
        cts.Activate();
        // Wire the objective sink to this CTS exactly as GameWorld.WireObjectiveSpawns' Cts arm does.
        GametypeObjectiveSpawns.Sink = e =>
        {
            switch (e.ClassName)
            {
                case "target_startTimer": cts.SpawnStartTimer(e.Origin); break;
                case "target_stopTimer": cts.SpawnStopTimer(e.Origin); break;
            }
            Api.Entities.Remove(e); // RetirePlaceholder
        };

        // Spawn the start + stop timers through the registered spawnfuncs (the BSP-lump path).
        Entity startPlaceholder = Api.Entities.Spawn(); startPlaceholder.Origin = new Vector3(0, 0, 0);
        Assert.True(SpawnFuncs.TrySpawn("target_startTimer", startPlaceholder));
        Entity stopPlaceholder = Api.Entities.Spawn(); stopPlaceholder.Origin = new Vector3(500, 0, 0);
        Assert.True(SpawnFuncs.TrySpawn("target_stopTimer", stopPlaceholder));

        Entity startTimer = cts.Timers.First(t => t.GtIsStartTimer);
        Entity stopTimer = cts.Timers.First(t => t.GtIsStopTimer);

        var runner = new Player { NetName = "Speedy", PersistentId = "uid-speedy", Health = 100f };

        // Cross the start line at t=10.
        f.GameClock.Time = 10f;
        startTimer.Touch!(startTimer, runner);

        // Cross the finish 7.5s later.
        f.GameClock.Time = 17.5f;
        float runTime = cts.FinishStage(runner);

        Assert.True(System.MathF.Abs(runTime - 7.5f) < 1e-3f, $"run time should be ~7.5s, got {runTime}");
        Assert.True(System.MathF.Abs(cts.FastestTimeOf(runner) - 7.5f) < 1e-3f);

        cts.Deactivate();
        GametypeObjectiveSpawns.Sink = null;
    }

    [Fact]
    public void EndToEnd_StopTouch_FoldsFasterRun_AndFilesRecord()
    {
        var f = new TestFacade();
        Api.Services = f;
        GameInit.Boot(f);
        RaceRecords.Clear();

        var cts = new Cts();
        cts.Activate();

        var runner = new Player { NetName = "Ace", PersistentId = "uid-ace", Health = 100f };

        // Run 1: 12s. (Start at a NON-zero time: RunStartTime==0 reads as "not running" in QC/the port.)
        f.GameClock.Time = 1f; cts.StartTimer(runner);
        f.GameClock.Time = 13f; cts.FinishStage(runner);
        Assert.Equal(12f, cts.FastestTimeOf(runner), 3);

        // Run 2: 9s (faster) → folds into fastest.
        f.GameClock.Time = 20f; cts.StartTimer(runner);
        f.GameClock.Time = 29f; cts.FinishStage(runner);
        Assert.Equal(9f, cts.FastestTimeOf(runner), 3);

        // The persistent CTS record (rank 1) reflects the best run.
        Assert.True(System.MathF.Abs(cts.ServerRecord - 9f) < 1e-3f, $"server record should be 9s, got {cts.ServerRecord}");

        cts.Deactivate();
    }

    [Fact]
    public void FinishWithoutStart_RecordsNoTime()
    {
        var f = new TestFacade();
        Api.Services = f;
        GameInit.Boot(f);

        var cts = new Cts();
        cts.Activate();
        var runner = new Player { NetName = "p", PersistentId = "uid-p", Health = 100f };

        f.GameClock.Time = 5f;
        // QC: a runner who spawned past the start line (no run started) records nothing.
        Assert.Equal(0f, cts.FinishStage(runner));
        Assert.Equal(0f, cts.FastestTimeOf(runner));

        cts.Deactivate();
    }
}

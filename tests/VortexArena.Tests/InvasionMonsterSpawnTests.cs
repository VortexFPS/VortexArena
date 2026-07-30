using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression tests for the Invasion wave-monster spawn wiring (<see cref="Invasion.SpawnMonsterDef"/>).
///
/// The bug: SpawnMonsterDef spawned a wave monster via <c>MonsterAI.Setup</c> + <c>def.Spawn</c> but never
/// wired the per-frame think (<c>e.Think</c>/<c>e.NextThink</c>), so the simulation loop's SV_RunThink had
/// nothing to fire — the monster's brain (Monster_Think → chase/attack) never ran and the monster sat inert.
/// It also called Setup twice (directly, then again inside <c>def.Spawn</c>), creating a second MonsterState
/// that overwrote the first and ORPHANED the <c>inv_monsterskill</c> stamp.
///
/// The fix routes the spawn through <see cref="MonsterAI.SpawnMonster"/> — the port of QC <c>spawnmonster()</c>
/// that <c>invasion_SpawnChosenMonster</c> actually calls: it runs <c>def.Spawn</c> once, stamps
/// SPAWNED|NORESPAWN, and wires use+think. These tests pin (1) the wiring + flags + skill stamp, and (2) the
/// end-to-end behaviour: a spawned wave monster ticked in a real <see cref="SimulationLoop"/> acquires a
/// nearby player and chases it.
/// </summary>
[Collection("GlobalState")]
public class InvasionMonsterSpawnTests
{
    private const float Gravity = 800f;

    private sealed class Harness
    {
        public EngineServices Services = null!;
        public SimulationLoop Sim = null!;
        public Invasion Inv = null!;
    }

    /// <summary>Engine facade + sim loop on a big flat floor, with the registries booted (monsters registered).</summary>
    private static Harness Build()
    {
        var world = new CollisionWorld();
        // Big floor slab (Quake Z up): top at Z=0, so a monster/player hull rests on it.
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();

        var services = new EngineServices(world);
        GameInit.Boot(services); // Api.Services = services; gameplay systems + registries (monsters) built
        Api.Cvars.Set("sv_gravity", Gravity.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var sim = new SimulationLoop(services, world) { Gravity = Gravity };
        // round 1, 10 players => skill = 1 + (int)max(1, 10*0.3) = 1 + 3 = 4 (distinct from the engine default of 1).
        var inv = new Invasion { PlayerCount = 10 };
        inv.Activate();
        return new Harness { Services = services, Sim = sim, Inv = inv };
    }

    /// <summary>A damageable client standing on the floor — a valid monster target. Huge health so the monster
    /// can't kill it within the test window (a dead enemy would be dropped, defeating the chase assertion).</summary>
    private static Entity SpawnPlayer(Vector3 origin)
    {
        Entity p = Api.Entities.Spawn();
        p.ClassName = "player";
        p.Flags |= EntFlags.Client;
        p.Solid = Solid.SlideBox;
        p.MoveType = MoveType.Walk;
        p.Mins = new Vector3(-16f, -16f, -24f);
        p.Maxs = new Vector3(16f, 16f, 45f);
        p.ViewOfs = new Vector3(0f, 0f, 35f);
        p.Health = 1_000_000f;
        p.TakeDamage = DamageMode.Aim;
        p.Origin = origin;
        Api.Entities.SetOrigin(p, origin); // link AbsMin/AbsMax so FindInRadius + the LOS trace see it
        return p;
    }

    /// <summary>
    /// Advance the published sim clock past zero. <c>SpawnFromMap</c> sets <c>e.NextThink = time</c>, and
    /// SV_RunThink treats <c>nextthink ≤ 0</c> as "never scheduled" — so a monster must be spawned at
    /// <c>time &gt; 0</c> to ever think. The clock published to QC-facing code lags one tick, so two warm-up
    /// ticks leave <see cref="IGameClock.Time"/> &gt; 0.
    /// </summary>
    private static void WarmUpClock(SimulationLoop sim)
    {
        sim.Tick();
        sim.Tick();
        Assert.True(Api.Clock.Time > 0f, "warm-up should advance the published clock past zero");
    }

    [Fact]
    public void SpawnMonsterDef_WiresThinkAndStampsFlagsAndSkill()
    {
        var h = Build();
        WarmUpClock(h.Sim);

        Monster? zombie = Monsters.ByName("zombie");
        Assert.NotNull(zombie); // registry booted

        Entity? m = h.Inv.SpawnMonsterDef(zombie!, new Vector3(0f, 0f, 26f));
        Assert.NotNull(m);

        // THE FIX: the per-frame think is wired and armed, so SV_RunThink will drive the brain each tick.
        Assert.NotNull(m!.Think);
        Assert.True(m.NextThink > 0f, $"think must be armed (nextthink>0), got {m.NextThink}");

        // Exactly one live MonsterState, carrying the invasion wave skill (the orphaned-skill regression):
        // under the old double-Setup, StateOf(m).Skill stayed at the engine default (1), not inv_monsterskill.
        MonsterAI.MonsterState? st = MonsterAI.StateOf(m);
        Assert.NotNull(st);
        int expectedSkill = h.Inv.ComputeMonsterSkill(h.Inv.Wave.Round);
        Assert.Equal(expectedSkill, st!.Skill);
        Assert.NotEqual(1, st.Skill); // distinct from the engine default → proves the stamp landed on the live state

        // QC spawnmonster flags: SPAWNED + NORESPAWN (invasion monsters don't respawn — a kill advances the wave).
        Assert.True((m.SpawnFlags & MonsterAI.MonsterFlag_Spawned) != 0, "MONSTERFLAG_SPAWNED should be set");
        Assert.True((m.SpawnFlags & MonsterAI.MonsterFlag_NoRespawn) != 0, "MONSTERFLAG_NORESPAWN should be set");
        Assert.True((m.Flags & EntFlags.Monster) != 0, "FL_MONSTER should be set");

        h.Inv.Deactivate();
    }

    [Fact]
    public void SpawnedWaveMonster_TicksItsBrain_AcquiresAndChasesPlayer()
    {
        var h = Build();

        // A player 256 units away on +X — the monster's intended target.
        Entity player = SpawnPlayer(new Vector3(256f, 0f, 24f));

        WarmUpClock(h.Sim);

        Monster? zombie = Monsters.ByName("zombie");
        Assert.NotNull(zombie);
        Vector3 spawnOrigin = new(0f, 0f, 26f);
        Entity? m = h.Inv.SpawnMonsterDef(zombie!, spawnOrigin);
        Assert.NotNull(m);
        Assert.Null(m!.Enemy); // nothing acquired yet

        static float DistXY(Entity a, Entity b)
            => new Vector2(a.Origin.X - b.Origin.X, a.Origin.Y - b.Origin.Y).Length();
        float startDist = DistXY(m, player);

        // Tick ~4s (300 ticks @ 72 Hz): past the spawn-anim gate (≈2s from zombie.dpm group 30: 61 frames @ 30 fps)
        // + the enemy re-check stagger (≤1s), with time left to chase.
        for (int i = 0; i < 300; i++)
            h.Sim.Tick();

        // The brain ran: it acquired the player and closed distance (chased). This is exactly what the missing
        // think-wiring broke — without the fix m.Enemy stays null and the monster never moves from its spawn.
        Assert.Same(player, m.Enemy);
        float endDist = DistXY(m, player);
        Assert.True(endDist < startDist - 16f,
            $"monster should have chased the player: startDist={startDist:F1}, endDist={endDist:F1}");

        h.Inv.Deactivate();
    }
}

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T22 — logic-gate trigger tests (qcsrc/common/mapobjects/trigger/{flipflop,monoflop,multivibrator,
/// disablerelay,relay_if,relay_teamcheck,relay_activators,gamestart}.qc). Each gate fires SUB_UseTargets
/// (MapMover.UseTargets) — the port of which is already trusted — so these assert the GATE LOGIC: which
/// events pass, when, and how the active/latch state evolves. A probe target with a counting <c>.use</c>
/// records every fire.
///
/// Uses a settable clock (<see cref="MutableClock"/>) over a real <see cref="EngineServices"/> so the
/// timed gates (monoflop/multivibrator/gamestart) can be advanced. Runs in the GlobalState collection
/// because it mutates the process-global <c>Api.Services</c>, the MapMover targetname index, and the
/// LogicGates magicear/gamestart statics.
/// </summary>
[Collection("GlobalState")]
public sealed class LogicGatesTests
{
    /// <summary>A test facade reusing the real entity table/cvars but with a clock the test can advance.</summary>
    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; }
        public MutableClock GameClock { get; } = new();
        public TestFacade() { Inner = new EngineServices(new CollisionWorld()); }
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    private TestFacade _f = null!;

    private TestFacade Boot()
    {
        _f = new TestFacade();
        Api.Services = _f;
        MapMover.ClearIndex();             // the targetname index is a process-global static
        LogicGates.MagicEars = null;       // reset the magicear linked list across tests
        LogicGates.GameStartTime = 0f;     // reset the gamestart clock across tests
        _f.GameClock.Time = 10f;           // a non-zero clock so "fire at game start" deferred thinks are sane
        _f.GameClock.FrameTime = 1f / 60f;
        return _f;
    }

    private void SetTime(float t) => _f.GameClock.Time = t;

    /// <summary>Spawn a probe target named <paramref name="name"/> whose <c>.use</c> counts how often it fires.</summary>
    private static Entity Probe(string name, int[] counter)
    {
        Entity e = Api.Entities.Spawn();
        e.ClassName = "info_notnull";
        e.TargetName = name;
        e.Use = (self, actor) => counter[0]++;
        MapMover.IndexRegister(e);
        return e;
    }

    /// <summary>Spawn a gate of <paramref name="className"/> targeting <paramref name="target"/> via a spawnfunc.</summary>
    private static Entity Gate(string className, string target, System.Action<Entity>? pre = null)
    {
        Entity e = Api.Entities.Spawn();
        e.Target = target;
        pre?.Invoke(e);
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should be registered");
        return e;
    }

    // =====================================================================================
    //  trigger_flipflop — passes only every 2nd event
    // =====================================================================================

    [Fact]
    public void Flipflop_PassesEverySecondEvent()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_flipflop", "t");

        // Starts OFF (no START_ENABLED): 1st use flips ON -> fires; 2nd flips OFF -> no fire; 3rd fires; ...
        g.Use!(g, g); Assert.Equal(1, hits[0]);
        g.Use!(g, g); Assert.Equal(1, hits[0]);
        g.Use!(g, g); Assert.Equal(2, hits[0]);
        g.Use!(g, g); Assert.Equal(2, hits[0]);
    }

    [Fact]
    public void Flipflop_StartEnabled_FlipsToOffFirst()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        // START_ENABLED (BIT0): state starts 1, so the FIRST use flips it to 0 -> no fire; the 2nd fires.
        Entity g = Gate("trigger_flipflop", "t", e => e.SpawnFlags = MapMover.SpawnStartEnabled);

        g.Use!(g, g); Assert.Equal(0, hits[0]);
        g.Use!(g, g); Assert.Equal(1, hits[0]);
    }

    [Fact]
    public void Flipflop_InactiveGate_DoesNotFire()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_flipflop", "t");
        g.Active = MapMover.ActiveNot;       // QC: if(this.active != ACTIVE_ACTIVE) return;
        g.Use!(g, g);
        Assert.Equal(0, hits[0]);
    }

    // =====================================================================================
    //  trigger_monoflop — one event -> on, then off after .wait
    // =====================================================================================

    [Fact]
    public void Monoflop_OneEvent_FiresOnThenOffAfterWait()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_monoflop", "t", e => e.Wait = 2f);

        // rising edge: fire ON now.
        SetTime(10f);
        g.Use!(g, g);
        Assert.Equal(1, hits[0]);
        Assert.Equal(1, g.GateState);
        Assert.Equal(12f, g.NextThink, 3);   // time + wait

        // before the timer: a re-trigger does NOT fire again, but DOES extend the off-timer (default variant).
        SetTime(11f);
        g.Use!(g, g);
        Assert.Equal(1, hits[0]);
        Assert.Equal(13f, g.NextThink, 3);   // re-armed to 11 + 2

        // the off-timer elapses: think fires the OFF event and clears the latch.
        g.Think!(g);
        Assert.Equal(2, hits[0]);
        Assert.Equal(0, g.GateState);
    }

    [Fact]
    public void Monoflop_Fixed_IgnoresRetriggerOffTime()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        // MONOFLOP_FIXED (BIT0): the off-time is fixed at the first trigger; re-triggers are ignored entirely.
        Entity g = Gate("trigger_monoflop", "t", e => { e.Wait = 2f; e.SpawnFlags = LogicGates.MonoflopFixed; });

        SetTime(10f);
        g.Use!(g, g);
        Assert.Equal(1, hits[0]);
        Assert.Equal(12f, g.NextThink, 3);

        // re-trigger while on: ignored, off-timer NOT extended (the "fixed" difference vs the default variant).
        SetTime(11f);
        g.Use!(g, g);
        Assert.Equal(1, hits[0]);
        Assert.Equal(12f, g.NextThink, 3);   // still 10 + 2, not re-armed
    }

    [Fact]
    public void Monoflop_DefaultWaitIsOne()
    {
        Boot();
        Entity g = Gate("trigger_monoflop", "t");   // no wait set
        Assert.Equal(1f, g.Wait, 3);
    }

    // =====================================================================================
    //  trigger_multivibrator — free-running on/off oscillator
    // =====================================================================================

    [Fact]
    public void Multivibrator_OscillatesAndFiresOnStateChange()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        // wait=1 (on), respawntime=1 (off): a 2s period. phase 0.
        SetTime(0f);
        Entity g = Gate("trigger_multivibrator", "t", e => { e.Wait = 1f; e.RespawnTimeMover = 1f; });

        // The spawn set nextthink = max(1, time) = 1 and state 0; the first send (think) at t in [0,1) -> on.
        SetTime(0f);
        g.Think!(g);
        Assert.Equal(1, g.GateState);          // now ON
        Assert.Equal(1, hits[0]);              // state changed 0->1 -> fired
        Assert.Equal(1.01f, g.NextThink, 2);   // next boundary = cyclestart(0) + wait(1) + 0.01

        // at t in [1,2): off.
        SetTime(1.01f);
        g.Think!(g);
        Assert.Equal(0, g.GateState);          // now OFF
        Assert.Equal(2, hits[0]);              // state changed 1->0 -> fired again
        Assert.Equal(2.01f, g.NextThink, 2);   // cyclestart(0) + wait(1) + respawntime(1) + 0.01
    }

    [Fact]
    public void Multivibrator_DefaultsRespawnTimeToWait()
    {
        Boot();
        Entity g = Gate("trigger_multivibrator", "t", e => e.Wait = 3f);
        Assert.Equal(3f, g.RespawnTimeMover, 3);   // QC: if(!respawntime) respawntime = wait
    }

    [Fact]
    public void Multivibrator_Toggle_StopsWhenRunning()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        SetTime(0f);
        Entity g = Gate("trigger_multivibrator", "t", e => { e.Wait = 1f; e.RespawnTimeMover = 1f; });

        // drive it ON via a think, then toggle: it should stop (nextthink 0) and fire the OFF if it was on.
        g.Think!(g);
        Assert.Equal(1, g.GateState);
        int before = hits[0];

        g.Use!(g, g);                          // multivibrator_toggle while running + on
        Assert.Equal(0f, g.NextThink, 3);      // stopped
        Assert.Equal(0, g.GateState);          // forced off
        Assert.Equal(before + 1, hits[0]);     // fired the OFF event
    }

    // =====================================================================================
    //  trigger_disablerelay — flips ACTIVE<->NOT on all named targets
    // =====================================================================================

    [Fact]
    public void DisableRelay_TogglesTargetsActiveState()
    {
        Boot();
        // two relays sharing the targetname "grp", both ACTIVE -> the valid "all on -> all off" toggle.
        Entity a = Api.Entities.Spawn(); a.TargetName = "grp"; a.Active = MapMover.ActiveActive; MapMover.IndexRegister(a);
        Entity b = Api.Entities.Spawn(); b.TargetName = "grp"; b.Active = MapMover.ActiveActive; MapMover.IndexRegister(b);

        Entity g = Gate("trigger_disablerelay", "grp");
        g.Use!(g, g);
        Assert.Equal(MapMover.ActiveNot, a.Active);   // ACTIVE -> NOT
        Assert.Equal(MapMover.ActiveNot, b.Active);   // ACTIVE -> NOT

        // a second use flips them back (NOT -> ACTIVE).
        g.Use!(g, g);
        Assert.Equal(MapMover.ActiveActive, a.Active);
        Assert.Equal(MapMover.ActiveActive, b.Active);
    }

    // =====================================================================================
    //  trigger_relay_if — cvar-compare gate
    // =====================================================================================

    [Fact]
    public void RelayIf_FiresWhenCvarsMatch()
    {
        Boot();
        Api.Cvars.Set("cv_a", "1");
        Api.Cvars.Set("cv_b", "1");
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_relay_if", "t", e => { e.NetName = "cv_a"; e.Message = "cv_b"; });

        g.Use!(g, g);
        Assert.Equal(1, hits[0]);

        // now make them differ -> no fire.
        Api.Cvars.Set("cv_b", "2");
        g.Use!(g, g);
        Assert.Equal(1, hits[0]);
    }

    [Fact]
    public void RelayIf_Negate_InvertsTheTest()
    {
        Boot();
        Api.Cvars.Set("cv_a", "1");
        Api.Cvars.Set("cv_b", "2"); // differ
        var hits = new int[1];
        Probe("t", hits);
        // RELAYIF_NEGATE: fires when they DON'T match.
        Entity g = Gate("trigger_relay_if", "t", e =>
        {
            e.NetName = "cv_a"; e.Message = "cv_b"; e.SpawnFlags = LogicGates.RelayIfNegate;
        });

        g.Use!(g, g);
        Assert.Equal(1, hits[0]);
    }

    // =====================================================================================
    //  trigger_relay_teamcheck — team gate
    // =====================================================================================

    [Fact]
    public void RelayTeamCheck_SameTeamFires_DiffTeamDoesNot()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_relay_teamcheck", "t", e => e.Team = Teams.Red);

        Entity red = new Entity { Flags = EntFlags.Client, Team = Teams.Red };
        Entity blue = new Entity { Flags = EntFlags.Client, Team = Teams.Blue };

        g.Use!(g, red);  Assert.Equal(1, hits[0]);   // same team -> fire
        g.Use!(g, blue); Assert.Equal(1, hits[0]);   // different team -> no fire
    }

    [Fact]
    public void RelayTeamCheck_Invert_FiresOnDifferentTeam()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_relay_teamcheck", "t", e =>
        {
            e.Team = Teams.Red; e.SpawnFlags = LogicGates.RelayTeamCheckInvert;
        });

        Entity blue = new Entity { Flags = EntFlags.Client, Team = Teams.Blue };
        Entity red = new Entity { Flags = EntFlags.Client, Team = Teams.Red };

        g.Use!(g, blue); Assert.Equal(1, hits[0]);   // INVERT: different team -> fire
        g.Use!(g, red);  Assert.Equal(1, hits[0]);   // same team -> no fire
    }

    [Fact]
    public void RelayTeamCheck_NoTeamActor_FiresOnlyWithNoTeamFlag()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_relay_teamcheck", "t", e =>
        {
            e.Team = Teams.Red; e.SpawnFlags = LogicGates.RelayTeamCheckNoTeam;
        });

        Entity noTeam = new Entity { Flags = EntFlags.Client, Team = Teams.None };
        g.Use!(g, noTeam);
        Assert.Equal(1, hits[0]);   // RELAYTEAMCHECK_NOTEAM lets a teamless actor through
    }

    // =====================================================================================
    //  relay_activate / relay_deactivate / relay_activatetoggle (+ generic_setactive fallback)
    // =====================================================================================

    [Fact]
    public void RelayActivators_SetTargetsActiveStateViaGenericFallback()
    {
        Boot();
        // a target with NO .setactive pointer -> the generic_setactive fallback is used.
        Entity targ = Api.Entities.Spawn(); targ.TargetName = "x"; targ.Active = MapMover.ActiveActive; MapMover.IndexRegister(targ);

        Entity deact = Gate("relay_deactivate", "x");
        deact.Use!(deact, deact);
        Assert.Equal(MapMover.ActiveNot, targ.Active);

        Entity act = Gate("relay_activate", "x");
        act.Use!(act, act);
        Assert.Equal(MapMover.ActiveActive, targ.Active);

        Entity toggle = Gate("relay_activatetoggle", "x");
        toggle.Use!(toggle, toggle);
        Assert.Equal(MapMover.ActiveNot, targ.Active);   // toggled ACTIVE -> NOT
        toggle.Use!(toggle, toggle);
        Assert.Equal(MapMover.ActiveActive, targ.Active); // toggled NOT -> ACTIVE
    }

    [Fact]
    public void RelayActivators_DispatchesToTargetSetActivePointer()
    {
        Boot();
        // a target WITH a .setactive pointer -> relay_activators calls it (not the generic fallback).
        int captured = -99;
        Entity targ = Api.Entities.Spawn();
        targ.TargetName = "y";
        targ.SetActive = (self, astate) => captured = astate;
        MapMover.IndexRegister(targ);

        Entity act = Gate("relay_activate", "y");
        act.Use!(act, act);
        Assert.Equal(MapMover.ActiveActive, captured);   // received .cnt = ACTIVE_ACTIVE
    }

    [Fact]
    public void GenericSetActive_ToggleFlips_ElseSets()
    {
        var e = new Entity { Active = MapMover.ActiveActive };
        LogicGates.GenericSetActive(e, MapMover.ActiveToggle);
        Assert.Equal(MapMover.ActiveNot, e.Active);
        LogicGates.GenericSetActive(e, MapMover.ActiveToggle);
        Assert.Equal(MapMover.ActiveActive, e.Active);
        LogicGates.GenericSetActive(e, MapMover.ActiveNot);
        Assert.Equal(MapMover.ActiveNot, e.Active);
    }

    // =====================================================================================
    //  trigger_gamestart — fire targets at game start (or after .wait), then delete
    // =====================================================================================

    [Fact]
    public void Gamestart_NoWait_FiresOnceViaDeferredThinkThenRemovesItself()
    {
        Boot();
        var hits = new int[1];
        Probe("t", hits);
        Entity g = Gate("trigger_gamestart", "t");   // no wait -> deferred think scheduled at Now()

        Assert.NotNull(g.Think);
        g.Think!(g);                                  // adaptor_think2use -> gamestart_use
        Assert.Equal(1, hits[0]);
        Assert.True(g.IsFreed, "gamestart_use should delete the entity after firing");
    }

    [Fact]
    public void Gamestart_Wait_SchedulesThinkAtGameStartPlusWait()
    {
        Boot();
        LogicGates.GameStartTime = 5f;
        Probe("t", new int[1]);
        Entity g = Gate("trigger_gamestart", "t", e => e.Wait = 3f);
        Assert.Equal(8f, g.NextThink, 3);             // game_starttime(5) + wait(3)
    }
}

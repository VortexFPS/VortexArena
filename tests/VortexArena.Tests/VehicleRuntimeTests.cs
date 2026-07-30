using System.Globalization;
using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Runtime-seam tests for the vehicle boarding/drive/impulse wiring (T37 — <see cref="VehicleBoarding"/>).
///
/// The gap these pin: the Racer/Raptor/Spiderbot/Bumblebee descriptors and the shared
/// <see cref="VehicleCommon"/> core are fully ported, but nothing BOARDED a vehicle (no PlayerUseKey / radius
/// search), nothing WROTE the seated pilot's input to <see cref="Entity.VehInput"/> (so every <c>*_frame</c>
/// read a zeroed input and the vehicle sat dead), and no impulse routed to the per-vehicle mode switch. These
/// tests assert the four seams at the helper level (headless, deterministic — no Godot, no GameWorld):
///   1. spawn + self-think (regression guard on the already-working Think auto-tick);
///   2. board via the +use path (<see cref="VehicleBoarding.UseKey"/>) + every QC guard;
///   3. drive — the seated input reaches the descriptor Frame (energy drains / velocity builds / a projectile
///      spawns), which is impossible without the VehInput write;
///   4. impulse routing (Raptor/Spiderbot mode set + cycle; a non-seated caller falls through);
///   5. exit; 6. death -> eject -> respawn; 7. Bumblebee multi-seat gunner; 8. the VehicleEnter/Exit hooks.
///
/// Harness mirrors <c>InvasionMonsterSpawnTests</c>: an <see cref="EngineServices"/> on a flat floor with the
/// registries booted (vehicles registered), driven through a real <see cref="SimulationLoop"/>.
/// </summary>
[Collection("GlobalState")]
public class VehicleRuntimeTests
{
    private const float Gravity = 800f;

    private sealed class Harness
    {
        public EngineServices Services = null!;
        public SimulationLoop Sim = null!;
    }

    /// <summary>Engine facade + sim loop on a big flat floor (Quake Z up: top at Z=0), registries booted.</summary>
    private static Harness Build()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-8192f, -8192f, -64f), new Vector3(8192f, 8192f, 0f), SuperContents.Solid));
        world.BuildGrid();

        var services = new EngineServices(world);
        GameInit.Boot(services); // Api.Services = services; registries (vehicles) built; gameplay systems installed
        Api.Cvars.Set("sv_gravity", Gravity.ToString(CultureInfo.InvariantCulture));

        var sim = new SimulationLoop(services, world) { Gravity = Gravity };

        // Default config: use-key board (g_vehicles_enter 1), radius 250, bots not allowed. These are the
        // SHIPPED defaults VehicleBoarding falls back to when unset, but set them explicitly so a stray cvar
        // store from another test can't perturb the boarding path.
        Api.Cvars.Set("g_vehicles", "1");
        Api.Cvars.Set("g_vehicles_enter", "1");
        Api.Cvars.Set("g_vehicles_enter_radius", "250");
        Api.Cvars.Set("g_vehicles_allow_bots", "0");
        Api.Cvars.Set("g_vehicles_steal", "0");
        // These seams test immediate boarding/drive/impulse, not the first-think stagger. g_vehicles_delayspawn
        // defaults ON (cfg default 1), which parks a map-placed vehicle inert+hidden until time+respawntime+jitter
        // — un-boardable for the first ~35s — so opt out of the stagger and keep the ACTIVE_ACTIVE immediate spawn.
        Api.Cvars.Set("g_vehicles_delayspawn", "0");

        // Determinism: the vehicle code draws spread/exit-attempt/death-timing samples from the seeded PRNG.
        Prandom.Seed(0xC0FFEE);

        // Clear the vehicle hooks so a prior test's subscribers don't leak into this one (the chains are static).
        MutatorHooks.VehicleEnter.Clear();
        MutatorHooks.VehicleExit.Clear();
        MutatorHooks.VehicleTouch.Clear();
        MutatorHooks.VehicleInit.Clear();

        VehicleCommon.GameStopped = false;

        return new Harness { Services = services, Sim = sim };
    }

    /// <summary>
    /// Advance the published sim clock past zero. A vehicle's Spawn sets <c>NextThink = time</c>, and SV_RunThink
    /// treats <c>nextthink &lt;= 0</c> as "never scheduled" — so the vehicle must be spawned at <c>time &gt; 0</c>
    /// to ever think. The QC-facing clock lags one tick, so two warm-up ticks leave Time &gt; 0.
    /// </summary>
    private static void WarmUpClock(SimulationLoop sim)
    {
        sim.Tick();
        sim.Tick();
        Assert.True(Api.Clock.Time > 0f, "warm-up should advance the published clock past zero");
    }

    /// <summary>Spawn a vehicle of the given map class (e.g. "vehicle_racer") at <paramref name="origin"/>.</summary>
    private static Entity SpawnVehicle(string mapClass, Vector3 origin, float team = 0f)
    {
        Entity v = Api.Entities.Spawn();
        v.ClassName = mapClass;
        v.Origin = origin;
        v.Team = team;
        Api.Entities.SetOrigin(v, origin);
        // QC vehicle_initialize records pos1/pos2 from origin/angles; the spawnfunc does this then runs vr_spawn.
        Assert.True(SpawnFuncs.TrySpawn(mapClass, v), $"{mapClass} spawnfunc should be registered");
        return v;
    }

    /// <summary>A live, alive player on the floor near the origin — a valid vehicle boarder.</summary>
    private static Player SpawnPlayer(Vector3 origin, float team = 0f, bool bot = false)
    {
        var p = new Player
        {
            IsBot = bot,
            Team = team,
            Solid = Solid.SlideBox,
            MoveType = MoveType.Walk,
            TakeDamage = DamageMode.Aim,
            DeadState = DeadFlag.No,
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            ViewOfs = new Vector3(0f, 0f, 35f),
            Health = 100f,
        };
        p.Flags |= EntFlags.Client;
        p.Origin = origin;
        Api.Entities.SetOrigin(p, origin); // link AbsMin/AbsMax so FindInRadius sees the player's origin
        return p;
    }

    // =====================================================================================
    // 1) spawn + self-think
    // =====================================================================================

    [Fact]
    public void RacerSpawn_RegistersDescriptor_ArmsThink_AndIdlesWithoutNaN()
    {
        var h = Build();
        WarmUpClock(h.Sim);

        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));

        Assert.IsType<Racer>(racer.VehicleDef);
        Assert.NotNull(racer.Think);
        Assert.True(racer.NextThink > 0f, $"think must be armed, got {racer.NextThink}");
        Assert.Equal(200f, racer.GetResource(ResourceType.Health));
        Assert.True((racer.VehicleFlags & VehicleFlags.IsVehicle) != 0);
        Assert.Null(racer.Owner);

        // Tick a few seconds: the empty craft idle-hovers (no pilot) and never produces a NaN origin.
        for (int i = 0; i < 60; i++) h.Sim.Tick();
        Assert.False(float.IsNaN(racer.Origin.X) || float.IsNaN(racer.Origin.Y) || float.IsNaN(racer.Origin.Z),
            "idle hover must not NaN the origin");
    }

    // =====================================================================================
    // 2) board (use-key) + guards
    // =====================================================================================

    [Fact]
    public void UseKey_BoardsNearbyRacer_LinksAndFreezesPilot()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(100f, 0f, 64f)); // within radius 250

        VehicleBoarding.UseKey(pl);

        Assert.Same(racer, pl.Vehicle);
        Assert.Same(pl, racer.Owner);
        Assert.Equal(MoveType.None, pl.MoveType);
        Assert.Equal(Solid.Not, pl.Solid);
        Assert.Equal(DamageMode.No, pl.TakeDamage);
        Assert.True(racer.NextThink > 0f, "the boarded vehicle re-arms its think (Frame runs from Think)");
    }

    /// <summary>
    /// Dismounting must restore the on-foot STANDING eye + hull (QC vehicles_exit: view_ofs = STAT(PL_VIEW_OFS);
    /// setsize(PL_MIN,PL_MAX)). EnterVehicle ZEROES view_ofs while seated, so without the restore the dismounted
    /// player fires from its origin — W_SetupShot eye = origin + view_ofs → shot ~35u LOW — until a crouch cycle
    /// re-sets the eye (the same eye-not-seeded class of bug as the spawn path).
    /// </summary>
    [Fact]
    public void ExitVehicle_RestoresStandingEyeAndHull_SoDismountedShotsAreNotLow()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(100f, 0f, 64f));

        VehicleBoarding.UseKey(pl);
        Assert.Equal(Vector3.Zero, pl.ViewOfs); // EnterVehicle zeroed the eye while seated

        VehicleCommon.ExitVehicle(racer, pl, VehicleExitFlag.Normal);

        Assert.Equal(new Vector3(0f, 0f, 35f), pl.ViewOfs); // STANDING eye restored (was 0 → shot fired low)
        Assert.False(pl.IsDucked);
        Assert.Equal(new Vector3(16f, 16f, 45f), pl.Maxs);  // standing hull restored (PL_MAX)
    }

    [Fact]
    public void UseKey_OutOfRadius_DoesNotBoard()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(400f, 0f, 64f)); // beyond radius 250

        VehicleBoarding.UseKey(pl);
        Assert.Null(pl.Vehicle);
    }

    [Fact]
    public void UseKey_BotWithoutAllowBots_DoesNotBoard()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player bot = SpawnPlayer(new Vector3(80f, 0f, 64f), bot: true);

        VehicleBoarding.UseKey(bot);
        Assert.Null(bot.Vehicle);
        Assert.Null(racer.Owner);
    }

    [Fact]
    public void UseKey_DeadPlayer_DoesNotBoard()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        pl.DeadState = DeadFlag.Dying;

        VehicleBoarding.UseKey(pl);
        Assert.Null(pl.Vehicle);
    }

    [Fact]
    public void UseKey_EnterDelayInFuture_DoesNotBoard()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        pl.VehicleEnterDelay = Api.Clock.Time + 5f; // just exited something

        VehicleBoarding.UseKey(pl);
        Assert.Null(pl.Vehicle);
    }

    [Fact]
    public void UseKey_GameStopped_DoesNotBoard()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleCommon.GameStopped = true;

        VehicleBoarding.UseKey(pl);
        Assert.Null(pl.Vehicle);
    }

    // =====================================================================================
    // 3) drive — the seated input reaches the descriptor Frame (THE load-bearing seam test)
    // =====================================================================================

    [Fact]
    public void Drive_AfterburnInput_DrainsEnergy()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);
        Assert.Same(racer, pl.Vehicle);

        // QC vehicles_enter zeroes the energy pool on board (VehicleCommon.EnterVehicle: vehicle_energy = 0); it
        // regens back up over the first moments. Let it refill (idle, no input) before testing the afterburn drain.
        for (int i = 0; i < 80; i++)
        {
            racer.VehInput = default; // no input -> energy regens (90/s)
            h.Sim.Tick();
        }
        float energyBefore = racer.VehicleEnergy;
        Assert.True(energyBefore > 50f, $"energy should have regenerated, got {energyBefore:F1}");

        // Now hold jump (afterburn), re-stashing the input each tick the way the GameWorld seated-gate does.
        for (int i = 0; i < 10; i++)
        {
            racer.VehInput = new MovementInput { ButtonJump = true, FrameTime = Api.Clock.FrameTime };
            h.Sim.Tick();
        }

        // Without the VehInput wiring the afterburn never engages and energy would only REGEN (climb). The drain
        // (AfterburnCost 130/s) outpaces the regen (90/s, and is paused while boosting), so a held afterburn must
        // NET-DECREASE the energy.
        Assert.True(racer.VehicleEnergy < energyBefore,
            $"afterburn must drain energy: before={energyBefore:F1}, after={racer.VehicleEnergy:F1}");
    }

    [Fact]
    public void Drive_ForwardInput_AcceleratesAlongFacing()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 200f)); // up high so it can move freely
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 200f));
        VehicleBoarding.UseKey(pl);

        // Forward wish-move (sign-only is what the frame reads). View straight ahead.
        for (int i = 0; i < 10; i++)
        {
            racer.VehInput = new MovementInput
            {
                MoveValues = new Vector3(1f, 0f, 0f),
                ViewAngles = Vector3.Zero,
                FrameTime = Api.Clock.FrameTime,
            };
            h.Sim.Tick();
        }

        // The racer thrusts along its forward; assert it built forward speed (velocity·forward > 0).
        QMath.AngleVectors(racer.Angles, out Vector3 fwd, out _, out _);
        float along = QMath.Dot(racer.Velocity, fwd);
        Assert.True(along > 0f, $"forward input should accelerate the racer forward, got along={along:F1}");
    }

    [Fact]
    public void Drive_PrimaryFire_SpawnsAVehicleProjectile()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 200f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 200f));
        VehicleBoarding.UseKey(pl);

        // Let the on-board-zeroed energy regen (QC EnterVehicle: vehicle_energy = 0) so the cannon (cost 1.5) can fire.
        for (int i = 0; i < 40; i++) { racer.VehInput = default; h.Sim.Tick(); }

        int before = Api.Entities.FindByClass("vehicles_projectile").Count();

        // Hold primary fire (energy laser). The cannon is refire-gated (0.05s) + energy-gated; a few ticks fire.
        for (int i = 0; i < 10; i++)
        {
            racer.VehInput = new MovementInput { ButtonAttack1 = true, FrameTime = Api.Clock.FrameTime };
            h.Sim.Tick();
        }

        int after = Api.Entities.FindByClass("vehicles_projectile").Count();
        Assert.True(after > before, $"primary fire must spawn at least one vehicle projectile: {before} -> {after}");
    }

    // =====================================================================================
    // 4) impulse routing
    // =====================================================================================

    [Fact]
    public void Impulse_Spiderbot_SetsAndCyclesRocketMode()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity spider = SpawnVehicle("vehicle_spiderbot", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);
        Assert.Same(spider, pl.Vehicle);
        Assert.Equal((int)SpiderbotRocketMode.Guided, spider.VehW2Mode); // vr_enter default SBRM_GUIDE

        Assert.True(VehicleBoarding.Impulse(pl, 1));
        Assert.Equal((int)SpiderbotRocketMode.Volley, spider.VehW2Mode);

        Assert.True(VehicleBoarding.Impulse(pl, 3));
        Assert.Equal((int)SpiderbotRocketMode.Artillery, spider.VehW2Mode);

        // weapon_next (10) cycles ARTILLERY(3) -> wraps to VOLLY(1).
        Assert.True(VehicleBoarding.Impulse(pl, 10));
        Assert.Equal((int)SpiderbotRocketMode.Volley, spider.VehW2Mode);

        // weapon_prev (12) cycles VOLLY(1) -> wraps to ARTILLERY(3).
        Assert.True(VehicleBoarding.Impulse(pl, 12));
        Assert.Equal((int)SpiderbotRocketMode.Artillery, spider.VehW2Mode);
    }

    [Fact]
    public void Impulse_Raptor_SetsBombAndFlareModes()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity raptor = SpawnVehicle("vehicle_raptor", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);
        Assert.Same(raptor, pl.Vehicle);
        Assert.Equal((int)RaptorMode.Bomb, raptor.VehW2Mode); // vr_enter default RSM_BOMB

        Assert.True(VehicleBoarding.Impulse(pl, 2));
        Assert.Equal((int)RaptorMode.Flare, raptor.VehW2Mode);

        Assert.True(VehicleBoarding.Impulse(pl, 1));
        Assert.Equal((int)RaptorMode.Bomb, raptor.VehW2Mode);

        // next (10): BOMB(1) -> FLARE(2).
        Assert.True(VehicleBoarding.Impulse(pl, 10));
        Assert.Equal((int)RaptorMode.Flare, raptor.VehW2Mode);
    }

    [Fact]
    public void Raptor_BombBurst_EmitsShellfragSpreadEffect()
    {
        // raptor.cb_shellfrag_gib: the cluster-bomb burst fires the cosmetic shell-fragment FX. QC
        // raptor_bomb_burst calls Damage_DamageInfo(DEATH_VH_RAPT_FRAGMENT) which dispatches the client
        // RaptorCBShellfragToss + the RAPTOR_BOMB_SPREAD particle puff. The port emits RAPTOR_BOMB_SPREAD at
        // burst (the client toss + puff hook). Assert the burst path emits it.
        var h = Build();
        WarmUpClock(h.Sim);
        // The chassis drops to the floor on spawn (droptofloor); board it at the settled floor height.
        Entity raptor = SpawnVehicle("vehicle_raptor", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);
        var def = (Raptor)raptor.VehicleDef!;

        EffectEmitter.Recorder.Clear();
        EffectEmitter.Sink = EffectEmitter.Recorder;

        // Drop the bombs, then drive their think until they burst. Each bomb thinks every tick; while inside the
        // clear-fall window (cnt = time + 10s) it keeps deferring (it falls straight down, staying within
        // bomblet_radius of the stationary raptor), then bursts unconditionally once the window lapses. The sim
        // fixed-step is ~1/72s, so the 10s window is ~720 ticks — run 900 for margin so the burst is guaranteed.
        def.DropBombs(raptor, pl);
        for (int i = 0; i < 900; i++) h.Sim.Tick();

        bool emitted = false;
        foreach (var r in EffectEmitter.Recorder.Log)
            if (r.EffectName == "raptor_bomb_spread" || r.Effect?.Name == "RAPTOR_BOMB_SPREAD") { emitted = true; break; }
        Assert.True(emitted, "cluster-bomb burst must emit the RAPTOR_BOMB_SPREAD shell-fragment FX");
    }

    [Fact]
    public void Raptor_Reload2_MirrorsBombReloadProgress()
    {
        // raptor.hud_crosshair (reload2 feed): the bomb dropmark only predicts while reload2 == 100. QC
        // raptor_frame mirrors vehicle_reload2 = bound(0, bombAlpha*100, 100) onto the pilot, ramping 0->100 as
        // the secondary reloads. Assert the mirror climbs over the bombs_refire window after boarding.
        var h = Build();
        WarmUpClock(h.Sim);
        // A map-placed vehicle drops to the floor on spawn (QC vehicle_initialize droptofloor → the raptor settles
        // onto the Z=0 floor regardless of the authored Z). Spawn the boarder at the SAME settled floor height so
        // it stays inside the g_vehicles_enter_radius (250) — a high authored Z would leave the pilot stranded in
        // the air after the chassis dropped.
        Entity raptor = SpawnVehicle("vehicle_raptor", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);
        Assert.Same(raptor, pl.Vehicle); // boarded (player at the settled floor height)

        // vr_enter seeds delay = time + bombs_refire (5s), lip = time → reload2 starts at 0 and climbs.
        h.Sim.Tick();
        float early = pl.VehicleReload2;

        // Run well past bombs_refire (5s). The sim fixed-step is ~1/72s, so 5s ≈ 360 ticks — use 500 for margin
        // so the bomb reload definitely completes and reload2 saturates at 100.
        for (int i = 0; i < 500; i++) h.Sim.Tick();
        float late = pl.VehicleReload2;

        Assert.True(late > early, $"reload2 must climb as the bombs reload: {early:F1} -> {late:F1}");
        Assert.True(late >= 100f - 0.01f, $"reload2 must reach 100 once bombs are ready, got {late:F1}");
    }

    [Fact]
    public void Impulse_NotSeated_ReturnsFalse()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Player pl = SpawnPlayer(new Vector3(0f, 0f, 64f));
        Assert.False(VehicleBoarding.Impulse(pl, 1), "a non-seated player's impulse falls through to weapons");
    }

    [Fact]
    public void Impulse_ChaseToggle17_ConsumedWhenSeated()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);

        // Racer has no per-vehicle mode, but the shared chase toggle (imp 17) is consumed for a seated pilot so
        // it doesn't fall through to weapon_drop.
        Assert.True(VehicleBoarding.Impulse(pl, 17));
    }

    // =====================================================================================
    // 5) exit
    // =====================================================================================

    [Fact]
    public void UseKey_WhileSeated_Exits_RestoresPlayerAndSetsReEntryDelay()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);
        Assert.Same(racer, pl.Vehicle);

        float now = Api.Clock.Time;
        VehicleBoarding.UseKey(pl); // second press: exit

        Assert.Null(pl.Vehicle);
        Assert.Null(racer.Owner);
        Assert.Equal(MoveType.Walk, pl.MoveType);
        Assert.Equal(Solid.SlideBox, pl.Solid);
        Assert.Equal(DamageMode.Aim, pl.TakeDamage);
        Assert.True(pl.VehicleEnterDelay > now, "exit sets a re-entry delay so +use doesn't instantly re-board");
    }

    // =====================================================================================
    // 6) death -> eject -> respawn (regression guard on the descriptor death path)
    // =====================================================================================

    [Fact]
    public void Death_EjectsPilot_ThenRespawnsAtSpawnPoint()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Vector3 spawn = new(0f, 0f, 64f);
        Entity racer = SpawnVehicle("vehicle_racer", spawn);
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));
        VehicleBoarding.UseKey(pl);
        Assert.Same(racer, pl.Vehicle);

        // Drop the shield so health takes the hit, then deal lethal damage (vehicles_damage path).
        racer.VehicleShield = 0f;
        VehicleCommon.DamageVehicle(racer, null, pl, 10_000f, DeathTypes.Generic, racer.Origin, Vector3.Zero);

        // The pilot was ejected (vehicles_damage -> descriptor Exit before vr_death).
        Assert.Null(pl.Vehicle);
        Assert.NotEqual(DeadFlag.No, racer.DeadState);

        // Tick past the racer death tumble (up to ~5s) + the 35s respawn time so it returns to its spawn
        // point, alive. Use a generous 60s budget (the tumble delay is randomized; 35+5 < 60 with margin).
        for (int i = 0; i < (int)(60f / Api.Clock.FrameTime); i++) h.Sim.Tick();

        Assert.Equal(DeadFlag.No, racer.DeadState);
        Assert.Equal(200f, racer.GetResource(ResourceType.Health));
        // Returned to its spawn XY (QC vehicles_spawn setorigin(this, pos1)). The Z legitimately drifts upward
        // afterwards as the now-empty craft idle-hovers, so assert the horizontal return, not the full 3D point.
        float xyDist = new Vector2(racer.Origin.X - spawn.X, racer.Origin.Y - spawn.Y).Length();
        Assert.True(xyDist < 8f, $"respawned vehicle should return to its spawn XY, got {racer.Origin}");
    }

    // =====================================================================================
    // 7) Bumblebee multi-seat — a same-team boarder takes a gunner seat
    // =====================================================================================

    [Fact]
    public void Bumblebee_SecondSameTeamBoarder_TakesAGunnerSeat()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity bee = SpawnVehicle("vehicle_bumblebee", new Vector3(0f, 0f, 200f), team: 1f);
        Assert.True((bee.VehicleFlags & VehicleFlags.MultiSlot) != 0, "bumblebee is MULTISLOT");

        Player pilot = SpawnPlayer(new Vector3(60f, 0f, 200f), team: 1f);
        VehicleBoarding.UseKey(pilot);
        Assert.Same(bee, pilot.Vehicle); // pilot in the body

        Player gunner = SpawnPlayer(new Vector3(0f, 80f, 200f), team: 1f);
        VehicleBoarding.UseKey(gunner);

        // The gunner is seated in a gun SLOT (not the body) — its "vehicle" is a VHF_PLAYERSLOT entity.
        Assert.NotNull(gunner.Vehicle);
        Assert.NotSame(bee, gunner.Vehicle);
        Assert.True((gunner.Vehicle!.VehicleFlags & VehicleFlags.PlayerSlot) != 0, "gunner sits in a player slot");
        Assert.True(gunner.Vehicle == bee.VehGun1 || gunner.Vehicle == bee.VehGun2, "slot is one of the two guns");
        Assert.True(bee.VehGunner1 == gunner || bee.VehGunner2 == gunner, "the body records the gunner");
        Assert.Equal(MoveType.None, gunner.MoveType);
    }

    [Fact]
    public void Bumblebee_GunnerFire_SpawnsAProjectile()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity bee = SpawnVehicle("vehicle_bumblebee", new Vector3(0f, 0f, 300f), team: 1f);
        Player pilot = SpawnPlayer(new Vector3(60f, 0f, 300f), team: 1f);
        VehicleBoarding.UseKey(pilot);
        Player gunner = SpawnPlayer(new Vector3(0f, 80f, 300f), team: 1f);
        VehicleBoarding.UseKey(gunner);
        Assert.NotNull(gunner.Vehicle);

        int before = Api.Entities.FindByClass("vehicles_projectile").Count();

        // Hold the gunner's primary fire. GunnerFrame (driven from the body's Think) reads gunner.VehInput, so
        // the input MUST be stashed on the gunner (the player), the way the GameWorld seated-gate writes it.
        for (int i = 0; i < 8; i++)
        {
            gunner.VehInput = new MovementInput { ButtonAttack1 = true, ViewAngles = Vector3.Zero, FrameTime = Api.Clock.FrameTime };
            h.Sim.Tick();
        }

        int after = Api.Entities.FindByClass("vehicles_projectile").Count();
        Assert.True(after > before, $"gunner fire must spawn a projectile: {before} -> {after}");
    }

    // =====================================================================================
    // 8) hooks fire on board / exit
    // =====================================================================================

    [Fact]
    public void EnterAndExitHooks_FireOnBoardAndExit()
    {
        var h = Build();
        WarmUpClock(h.Sim);
        Entity racer = SpawnVehicle("vehicle_racer", new Vector3(0f, 0f, 64f));
        Player pl = SpawnPlayer(new Vector3(80f, 0f, 64f));

        int entered = 0, exited = 0;
        Entity? enteredVeh = null, exitedVeh = null;
        bool EnterHandler(ref MutatorHooks.VehicleEnterArgs a) { entered++; enteredVeh = a.Vehicle; return false; }
        bool ExitHandler(ref MutatorHooks.VehicleExitArgs a) { exited++; exitedVeh = a.Vehicle; return false; }
        MutatorHooks.VehicleEnter.Add(EnterHandler);
        MutatorHooks.VehicleExit.Add(ExitHandler);

        VehicleBoarding.UseKey(pl); // board -> VehicleEnter
        Assert.Equal(1, entered);
        Assert.Same(racer, enteredVeh);

        VehicleBoarding.UseKey(pl); // exit -> VehicleExit
        Assert.Equal(1, exited);
        Assert.Same(racer, exitedVeh);
    }

    // =====================================================================================
    // ToInput round-trips every field
    // =====================================================================================

    [Fact]
    public void ToInput_CopiesEveryField()
    {
        var src = new MovementInput
        {
            ViewAngles = new Vector3(10f, 20f, 30f),
            MoveValues = new Vector3(400f, -350f, 0f),
            FrameTime = 0.0123f,
            ButtonJump = true,
            ButtonCrouch = true,
            ButtonUse = true,
            ButtonAttack1 = true,
            ButtonAttack2 = true,
            ButtonJetpack = true,
            Typing = true,
            Impulse = 7,
        };

        MovementInput dst = VehicleBoarding.ToInput(src);

        Assert.Equal(src.ViewAngles, dst.ViewAngles);
        Assert.Equal(src.MoveValues, dst.MoveValues);
        Assert.Equal(src.FrameTime, dst.FrameTime);
        Assert.True(dst.ButtonJump && dst.ButtonCrouch && dst.ButtonUse && dst.ButtonAttack1 && dst.ButtonAttack2);
        Assert.True(dst.ButtonJetpack && dst.Typing);
        Assert.Equal(7, dst.Impulse);
    }
}

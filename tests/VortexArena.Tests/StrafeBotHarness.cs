using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Net;
using VortexArena.Tests.Camera;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// STRAFE-BOT HARNESS — drives the movement sim with the optimal-strafe controllers a human is
/// approximating when they air-strafe, so the acceleration path is exercised at its ceiling rather than at
/// whatever angle a hand-picked constant lead happens to hit.
///
/// <para>Three of the four controllers are CLOSED-LOOP (they read the current speed/heading each tick, like
/// a player watching the StrafeHUD) and are for characterising the port against the QC reference. The fourth,
/// <see cref="Controller.ConstantYaw"/>, is OPEN-LOOP: yaw is a function of elapsed time only, so a stock
/// Xonotic client can reproduce it exactly with <c>cl_yawspeed</c> + <c>+left</c> and no code changes. That
/// one is the cross-engine fingerprint — see docs/MOVEMENT-PHYSICS.md §10 for the capture protocol.</para>
///
/// <para>The angle theory (Base's own, from client/hud/panel/strafehud.qc:189-192,412-428, documented at
/// https://gitlab.com/otta8634/xonotic-physics):</para>
/// <list type="bullet">
///   <item><c>a = accel · dt · movespeed</c> — the tick's accel step, in velocity units.</item>
///   <item><c>bestspeed = max(movespeed - a, 0)</c> — the velocity-onto-wishdir projection that still
///         receives the FULL step; aim so <c>v·wishdir</c> lands exactly here.</item>
///   <item><c>δ_opt = acos(bestspeed / speed)</c> — the optimal angle between velocity and wishdir.</item>
///   <item><c>δ_min = acos(movespeed / speed)</c> — below this the Quake term is saturated (no gain).</item>
///   <item><c>δ_max = acos(-(airstopaccel · a / 2) / speed)</c> — beyond this the tick LOSES speed.</item>
///   <item>W-turn (air control) optimum tends to <c>acos(sqrt(p/(p+1)))</c> = 35.26° at <c>aircontrol_power 2</c>.</item>
/// </list>
/// For pure A/D strafe at stock cvars <c>movespeed</c> is the strafe-clamped 100 and <c>accel</c> the
/// strafe-blended 18, NOT sv_maxairspeed 360 / sv_airaccelerate 2 — see MOVEMENT-PHYSICS.md §5.
///
/// <para>Writes CSVs to <c>_scratch/strafe/</c> (gitignored) plus the matching Xonotic + Vortex console
/// scripts, so both legs of a cross-engine run are generated from ONE sweep definition and cannot drift.</para>
/// </summary>
public class StrafeBotHarness
{
    private readonly ITestOutputHelper _out;
    public StrafeBotHarness(ITestOutputHelper o) => _out = o;

    // ==================================================================================================
    //  stock Xonotic air-branch constants (physicsX.cfg) — see MOVEMENT-PHYSICS.md §6
    // ==================================================================================================
    private const float MaxAirSpeed = 360f;          // sv_maxairspeed
    private const float AirAccelerate = 2f;          // sv_airaccelerate
    private const float AirAccelQW = -0.8f;          // sv_airaccel_qw
    private const float AirStrafeAccelQW = -0.95f;   // sv_airstrafeaccel_qw
    private const float StretchFactor = 2f;          // sv_airaccel_qw_stretchfactor
    private const float AirStopAccelerate = 3f;      // sv_airstopaccelerate
    private const float AirStrafeAccelerate = 18f;   // sv_airstrafeaccelerate
    private const float MaxAirStrafeSpeed = 100f;    // sv_maxairstrafespeed
    private const float SpeedLimitNonQW = 900f;      // sv_airspeedlimit_nonqw
    private const float SideMove = 360f;             // sys_phys_fixspeed stuffcmd value
    private const float AirControl = 100f;           // sv_aircontrol
    private const float AirControlPower = 2f;        // sv_aircontrol_power

    private const float Rad2Deg = 180f / MathF.PI;
    private const float Deg2Rad = MathF.PI / 180f;

    /// <summary>The turn rates swept by the cross-engine fingerprint, in deg/s. Chosen to bracket the
    /// optimal-strafe band at the speeds a bunnyhop actually reaches (δ_opt at 400 u/s is ~79°, and the
    /// heading turn rate that holds it is well under 100 deg/s once speed climbs).</summary>
    private static readonly float[] YawSweep =
        { 20f, 30f, 40f, 50f, 60f, 70f, 80f, 90f, 100f, 120f, 140f, 170f, 200f, 250f, 300f };

    /// <summary>The LIVE server tick, not <c>SimulationLoop.TicRate</c>. Xonotic ships
    /// <c>sys_ticrate 0.015625</c> = 1/64 (xonotic-common.cfg:101) with <c>cl_netfps 64</c>, and Vortex Arena
    /// ships the same (vortex-server.cfg:70, ClientSettings.cs:348). The 1/72 in
    /// <c>SimulationLoop.TicRate</c> is the DarkPlaces ENGINE default, kept only as a compile-time fallback
    /// for paths that never read the cvar — see MOVEMENT-PHYSICS.md §7.2.</summary>
    private const float LiveTicRate = 1f / 64f;

    private const float StartSpeed = 400f;
    private const float AirWindow = 2.0f;   // seconds of uninterrupted air time
    private const float BhopWindow = 6.0f;  // seconds of held-jump bunnyhopping

    // ==================================================================================================
    //  controllers ("strafe bots")
    // ==================================================================================================

    public enum Controller
    {
        /// <summary>Open-loop constant yaw rate. The ONLY controller a stock Xonotic client can reproduce
        /// (cl_yawspeed + +left), so it is what the cross-engine comparison uses.</summary>
        ConstantYaw,

        /// <summary>Closed-loop: hold wishdir at Base's own δ_opt = acos(bestspeed/speed) off the velocity
        /// heading. What a perfect player following the StrafeHUD achieves.</summary>
        HudOptimal,

        /// <summary>Closed-loop: each tick, search the wish angle that maximises the NEXT tick's speed under
        /// the real accel implementation. The true per-tick-greedy ceiling; beats HudOptimal wherever the QW
        /// accelqw split and the stretchfactor clamp make the classic Quake formula an approximation.</summary>
        NumericOptimal,

        /// <summary>Closed-loop, W held instead of A/D: hold wishdir at the air-control asymptote
        /// acos(sqrt(p/(p+1))) = 35.26° off velocity. Exercises CPM_PM_Aircontrol, which pure strafe never
        /// reaches (movity 0 → k ≤ 0 → early return).</summary>
        WTurn,
    }

    /// <summary>Which key the bot holds. Decides how a desired WISH HEADING maps to a view yaw: with Side=+1
    /// the wish direction is the view's RIGHT vector (heading = yaw - 90); with Forward=+1 it is the view's
    /// FORWARD vector (heading = yaw).</summary>
    private static float YawForWishHeading(float wishHeadingRad, bool sideKey)
        => wishHeadingRad * Rad2Deg + (sideKey ? 90f : 0f);

    /// <summary>Base's δ_opt for the pure-strafe air branch (strafehud.qc:191-192, 415-417).</summary>
    public static float BestAngleDeg(float speed, float dt)
    {
        float movespeed = MathF.Min(SideMove, MaxAirSpeed);
        movespeed = MathF.Min(movespeed, GeomLerp(MaxAirSpeed, 1f, MaxAirStrafeSpeed)); // strafity 1 → 100
        float accel = GeomLerp(AirAccelerate, 1f, AirStrafeAccelerate);                 // strafity 1 → 18
        float a = accel * dt * movespeed;
        float bestspeed = MathF.Max(movespeed - a, 0f);
        return speed > bestspeed ? MathF.Acos(bestspeed / speed) * Rad2Deg : 0f;
    }

    /// <summary>Base's δ_max — the angle past which the tick loses speed (strafehud.qc:423-427).</summary>
    public static float OverturnAngleDeg(float speed, float dt)
    {
        float movespeed = MathF.Min(GeomLerp(MaxAirSpeed, 1f, MaxAirStrafeSpeed), MaxAirSpeed);
        float a = GeomLerp(AirAccelerate, 1f, AirStrafeAccelerate) * dt * movespeed;
        // Side-strafing suppresses airstopaccelerate (strafehud.qc:334-336 treats it as 1 while turning).
        return speed > a * 0.5f ? MathF.Acos(-(a * 0.5f) / speed) * Rad2Deg : 180f;
    }

    /// <summary>The air-control asymptote: acos(sqrt(p/(p+1))), 35.26° at aircontrol_power 2
    /// (strafehud.qc:441-478).</summary>
    public static float WTurnAngleDeg()
        => MathF.Acos(MathF.Sqrt(AirControlPower / (AirControlPower + 1f))) * Rad2Deg;

    // ==================================================================================================
    //  tests / entry points
    // ==================================================================================================

    [Fact]
    public void StrafeBots_AirWindow_PortVsReference()
    {
        _out.WriteLine($"pure-air {AirWindow}s window, start {StartSpeed} u/s, dt 1/64 (live sys_ticrate), stock Xonotic cvars");
        _out.WriteLine($"δ_opt at 400 u/s = {BestAngleDeg(400f, LiveTicRate):F2}°, "
                     + $"δ_max = {OverturnAngleDeg(400f, LiveTicRate):F2}°, W-turn asymptote = {WTurnAngleDeg():F2}°");
        _out.WriteLine("");
        _out.WriteLine($"{"controller",-16} {"port end",10} {"ref end",10} {"delta",8}");
        _out.WriteLine(new string('-', 48));

        const float dt = LiveTicRate;
        float worst = 0f;
        foreach (Controller c in new[] { Controller.HudOptimal, Controller.NumericOptimal, Controller.WTurn })
        {
            float port = RunPortAir(dt, AirWindow, StartSpeed, c, 0f, out _);
            float reference = RunRefAir(dt, AirWindow, StartSpeed, c, 0f, out _);
            float d = MathF.Abs(port - reference);
            worst = MathF.Max(worst, d);
            _out.WriteLine($"{c,-16} {port,10:F1} {reference,10:F1} {d,8:F2}");
        }

        // The reference is an independent transcription of the QC air branch, so agreement under the
        // MAXIMISING controllers is a far stronger parity statement than agreement at one fixed lead angle:
        // any divergence in the clamp order, the GeomLerp blends or the accelqw stretch would show up as a
        // different ceiling. WTurn additionally covers CPM_PM_Aircontrol, which pure strafe never enters.
        Assert.True(worst < 2.0f,
            $"port must match the QC reference under every strafe-bot controller (worst |delta| {worst:F2} u/s)");
    }

    [Fact]
    public void StrafeBots_ConstantYawSweep_WritesCrossEngineKit()
    {
        const float dt = LiveTicRate;
        var rows = new List<string> { "yaw_deg_per_s,air_end_speed,bhop_end_speed,bhop_landings" };

        _out.WriteLine($"{"yaw deg/s",10} {"air end",10} {"bhop end",10} {"landings",9}");
        _out.WriteLine(new string('-', 42));
        foreach (float omega in YawSweep)
        {
            float air = RunPortAir(dt, AirWindow, StartSpeed, Controller.ConstantYaw, omega, out _);
            float bhop = RunPortBhop(dt, BhopWindow, StartSpeed, omega, out List<float> landings);
            rows.Add(string.Create(CultureInfo.InvariantCulture,
                $"{omega:F0},{air:F2},{bhop:F2},{landings.Count}"));
            _out.WriteLine($"{omega,10:F0} {air,10:F1} {bhop,10:F1} {landings.Count,9}");
        }

        string dir = ScratchDir();
        File.WriteAllLines(Path.Combine(dir, "vortex-yawsweep.csv"), rows);
        File.WriteAllText(Path.Combine(dir, "xonotic-strafe-capture.cfg"), BuildXonoticCfg());
        File.WriteAllText(Path.Combine(dir, "vortex-strafe-capture.cfg"), BuildVortexCfg());
        _out.WriteLine("");
        _out.WriteLine($"wrote {Path.Combine(dir, "vortex-yawsweep.csv")}");
        _out.WriteLine($"wrote {Path.Combine(dir, "xonotic-strafe-capture.cfg")}  (exec in Xonotic)");
        _out.WriteLine($"wrote {Path.Combine(dir, "vortex-strafe-capture.cfg")}   (exec in Vortex Arena)");
        _out.WriteLine("then: python tools/strafe-compare.py --help");
    }

    [Fact]
    public void StrafeBots_PerTickTrace_WritesCsv()
    {
        const float dt = LiveTicRate;
        var sb = new StringBuilder("controller,t,speed,lead_deg,best_angle_deg,overturn_deg,onground\n");
        foreach (Controller c in Enum.GetValues<Controller>())
        {
            float omega = c == Controller.ConstantYaw ? 60f : 0f;
            RunPortAir(dt, AirWindow, StartSpeed, c, omega, out List<(float t, float speed, float lead, bool ground)> tr);
            foreach ((float t, float speed, float lead, bool ground) in tr)
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"{c},{t:F4},{speed:F3},{lead:F3},{BestAngleDeg(speed, dt):F3},{OverturnAngleDeg(speed, dt):F3},{(ground ? 1 : 0)}\n"));
        }
        string path = Path.Combine(ScratchDir(), "vortex-strafe-trace.csv");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine($"wrote {path}");
    }

    // ==================================================================================================
    //  the port's real sim
    // ==================================================================================================

    private static float RunPortAir(float dt, float seconds, float speed0, Controller c, float omega,
        out List<(float t, float speed, float lead, bool ground)> trace)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        VortexArena.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        VortexArena.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        VortexArena.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        // High enough that the whole window is airborne (gravity 800 over 2 s falls ~1600 qu).
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 20000f), Velocity = new Vector3(speed0, 0f, 0f), OnGround = false };

        trace = new List<(float, float, float, bool)>();
        bool sideKey = c != Controller.WTurn;
        float now = 0f, yaw0 = 0f;
        while (now < seconds)
        {
            now += dt;
            clock.Time = now;
            float speed = Speed2D(s.Velocity);
            float heading = MathF.Atan2(s.Velocity.Y, s.Velocity.X);
            float lead = LeadFor(c, speed, dt, omega, now, heading, ref yaw0);
            float yaw = c == Controller.ConstantYaw
                ? yaw0 + omega * now
                : YawForWishHeading(heading + lead * Deg2Rad, sideKey);

            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, yaw, 0f),
                Side = sideKey ? 1f : 0f,
                Forward = sideKey ? 0f : 1f,
                DeltaTime = dt,
            };
            step.Step(ref s, in cmd, default);
            trace.Add((now, Speed2D(s.Velocity), lead, s.OnGround));
        }
        return Speed2D(s.Velocity);
    }

    private static float RunPortBhop(float dt, float seconds, float speed0, float omega, out List<float> landings)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        VortexArena.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        VortexArena.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        VortexArena.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        // Floor top is z=64 in FlatWorld; hull mins.z = -24 → stand at 88.
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 88.03f), Velocity = new Vector3(speed0, 0f, 0f), OnGround = true };

        landings = new List<float>();
        float now = 0f;
        while (now < seconds)
        {
            now += dt;
            clock.Time = now;
            bool wasAir = !s.OnGround;
            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, omega * now, 0f),
                Side = 1f,
                DeltaTime = dt,
                Buttons = (int)InputButtons.Jump, // hold space: autohop, the way a player bunnyhops
            };
            step.Step(ref s, in cmd, default);
            // Base logs landing speed from the phys_land event (sv_physics.qc sys_phys_land, `speedmeter`);
            // sample at the same edge so the two engines' series are directly comparable.
            if (wasAir && s.OnGround)
                landings.Add(Speed2D(s.Velocity));
        }
        return Speed2D(s.Velocity);
    }

    // ==================================================================================================
    //  independent QC reference (2D air branch), transcribed from Base qcsrc
    // ==================================================================================================

    private static float RunRefAir(float dt, float seconds, float speed0, Controller c, float omega,
        out List<float> trace)
    {
        float vx = speed0, vy = 0f;
        trace = new List<float>();
        float now = 0f, yaw0 = 0f;
        while (now < seconds)
        {
            now += dt;
            float speed = MathF.Sqrt(vx * vx + vy * vy);
            float heading = MathF.Atan2(vy, vx);
            float lead = LeadFor(c, speed, dt, omega, now, heading, ref yaw0);
            float w = heading + lead * Deg2Rad;
            RefAirTick(ref vx, ref vy, dt, MathF.Cos(w), MathF.Sin(w), sideStrafe: c != Controller.WTurn);
            trace.Add(MathF.Sqrt(vx * vx + vy * vy));
        }
        return MathF.Sqrt(vx * vx + vy * vy);
    }

    /// <summary>One air-branch tick: physics.qc:309-371 + player.qc PM_Accelerate / CPM_PM_Aircontrol.
    /// <paramref name="sideStrafe"/> selects strafity 1 (pure A/D: strafe blends on, air control skipped)
    /// vs strafity 0 with forward input (W: no strafe blends, air control ACTIVE).</summary>
    private static void RefAirTick(ref float vx, ref float vy, float dt, float wdx, float wdy, bool sideStrafe)
    {
        float wishspeed = MathF.Min(SideMove, MaxAirSpeed);   // :303/:307
        float wishspeed0 = wishspeed;                          // :313
        float airaccel = AirAccelerate;                        // :319
        float airaccelqw = AirAccelQW;                         // :312
        float wishspeed2 = wishspeed;                          // :322 (air control uses the pre-clamp value)

        float speed = MathF.Sqrt(vx * vx + vy * vy);
        if (AirStopAccelerate != 0f && speed > 0f)              // :325-335
        {
            float dot = (vx / speed) * wdx + (vy / speed) * wdy;
            if (dot < 0f)
                airaccel += (airaccel - AirStopAccelerate) * dot;
        }

        float strafity = sideStrafe ? 1f : 0f;                  // :344
        if (MaxAirStrafeSpeed != 0f)                            // :345-348
            wishspeed = MathF.Min(wishspeed, GeomLerp(MaxAirSpeed, strafity, MaxAirStrafeSpeed));
        if (AirStrafeAccelerate != 0f)                          // :350-351
            airaccel = GeomLerp(airaccel, strafity, AirStrafeAccelerate);
        if (AirStrafeAccelQW != 0f)                             // :353-357
            airaccelqw = ((strafity > 0.5f ? AirStrafeAccelQW : AirAccelQW) >= 0f ? +1f : -1f)
                * (1f - GeomLerp(1f - MathF.Abs(AirAccelQW), strafity, 1f - MathF.Abs(AirStrafeAccelQW)));

        RefAccelerate(ref vx, ref vy, dt, wdx, wdy, wishspeed, wishspeed0, airaccel, airaccelqw,
            StretchFactor, SpeedLimitNonQW);                    // :364-366

        // :369-370 — CPM_PM_Aircontrol. Pure A/D gives movity 0 → k = -1 ≤ 0 → early return (player.qc:245-247),
        // so it only runs on the W-held path.
        if (!sideStrafe && AirControl != 0f)
            RefAircontrol(ref vx, ref vy, dt, wdx, wdy, wishspeed2);
    }

    /// <summary>player.qc:280-341 PM_Accelerate, 2D (vel_z is untouched by it). sidefric is 0 at stock
    /// (sv_airaccel_sideways_friction 0), so vel_perpend passes through unchanged.</summary>
    private static void RefAccelerate(ref float vx, ref float vy, float dt, float wdx, float wdy,
        float wishspeed, float wishspeed0, float accel, float accelqw, float stretchfactor, float speedlimit)
    {
        float speedclamp = stretchfactor > 0f ? stretchfactor : accelqw < 0f ? 1f : -1f;
        accelqw = MathF.Abs(accelqw);
        wishspeed0 = wishspeed; // GAMEPLAYFIX_Q2AIRACCELERATE, ON in Base (:288-289) — see §7.3

        float vel_straight = vx * wdx + vy * wdy;
        float perpx = vx - vel_straight * wdx, perpy = vy - vel_straight * wdy;
        float step = accel * dt * wishspeed0;
        float vel_xy_current = MathF.Sqrt(vx * vx + vy * vy);

        if (speedlimit != 0f)
            accelqw = AdjustAirAccelQW(accelqw,
                (speedlimit - Bound(wishspeed, vel_xy_current, speedlimit)) / MathF.Max(1f, speedlimit - wishspeed));

        float vel_xy_forward = vel_xy_current + Bound(0f, wishspeed - vel_xy_current, step) * accelqw + step * (1f - accelqw);
        vel_straight = vel_straight + Bound(0f, wishspeed - vel_straight, step) * accelqw + step * (1f - accelqw);

        vx = vel_straight * wdx + perpx;
        vy = vel_straight * wdy + perpy;

        if (speedclamp >= 0f)
        {
            float pre = MathF.Sqrt(vx * vx + vy * vy);
            if (pre > 0f)
            {
                vel_xy_current += (vel_xy_forward - vel_xy_current) * speedclamp;
                if (vel_xy_current < pre)
                {
                    float f = vel_xy_current / pre;
                    vx *= f; vy *= f;
                }
            }
        }
    }

    /// <summary>player.qc:234-268 CPM_PM_Aircontrol, 2D. Called only with W held (movity 1 → k = 1), and with
    /// aircontrol_flags 0 / aircontrol_penalty 0 as physicsX.cfg ships.</summary>
    private static void RefAircontrol(ref float vx, ref float vy, float dt, float wdx, float wdy, float wishspeed)
    {
        float k = 1f;                                            // 2 * movity - 1 with movity 1 (W dead ahead)
        k *= Bound(0f, wishspeed / MaxAirSpeed, 1f);             // :248-249 (crouch impact; flags bit2 clear)

        float xyspeed = MathF.Sqrt(vx * vx + vy * vy);
        if (xyspeed == 0f)
            return;
        float nx = vx / xyspeed, ny = vy / xyspeed;
        float dot = nx * wdx + ny * wdy;

        if (dot > 0f)                                            // :258-264
        {
            k *= MathF.Pow(dot, AirControlPower) * dt;
            // aircontrol_penalty is 0 at stock, so xyspeed is unchanged here.
            k *= 32f * MathF.Abs(AirControl);
            float mx = nx * xyspeed + wdx * k, my = ny * xyspeed + wdy * k;
            float len = MathF.Sqrt(mx * mx + my * my);
            if (len > 0f) { nx = mx / len; ny = my / len; }
        }
        vx = nx * xyspeed;
        vy = ny * xyspeed;
    }

    // ==================================================================================================
    //  controller lead-angle selection
    // ==================================================================================================

    /// <summary>The signed lead angle (degrees) to hold the wish direction at, measured CCW off the current
    /// velocity heading. ConstantYaw ignores it (the caller drives yaw open-loop from <paramref name="now"/>).</summary>
    private static float LeadFor(Controller c, float speed, float dt, float omega, float now, float heading,
        ref float yaw0)
    {
        switch (c)
        {
            case Controller.ConstantYaw:
                // Seed the open-loop ramp so tick 1's wishdir starts at δ_opt off the initial heading;
                // without this the first ticks are spent sweeping through the dead zone.
                if (yaw0 == 0f)
                    yaw0 = YawForWishHeading(heading + BestAngleDeg(speed, dt) * Deg2Rad, sideKey: true) - omega * now;
                return 0f;

            case Controller.HudOptimal:
                return BestAngleDeg(speed, dt);

            case Controller.NumericOptimal:
                return SearchBestLead(speed, dt, sideStrafe: true);

            case Controller.WTurn:
                return WTurnAngleDeg();

            default:
                return 0f;
        }
    }

    /// <summary>Brute-force + bisect the lead angle that maximises the next tick's speed under the real accel
    /// implementation. Coarse scan over the whole gaining band (0 to δ_max) then a golden-section refine, run
    /// on a velocity of the given speed pointing along +X (the problem is rotationally symmetric).</summary>
    private static float SearchBestLead(float speed, float dt, bool sideStrafe)
    {
        float hi = MathF.Min(179f, OverturnAngleDeg(speed, dt));
        float best = 0f, bestVal = float.NegativeInfinity;
        for (float d = 0f; d <= hi; d += 1f)
        {
            float v = TrySpeedAfter(speed, d, dt, sideStrafe);
            if (v > bestVal) { bestVal = v; best = d; }
        }
        float lo = MathF.Max(0f, best - 1f), up = MathF.Min(hi, best + 1f);
        for (int i = 0; i < 40 && up - lo > 1e-4f; i++)
        {
            float m1 = lo + (up - lo) / 3f, m2 = up - (up - lo) / 3f;
            if (TrySpeedAfter(speed, m1, dt, sideStrafe) < TrySpeedAfter(speed, m2, dt, sideStrafe)) lo = m1;
            else up = m2;
        }
        return (lo + up) * 0.5f;
    }

    private static float TrySpeedAfter(float speed, float leadDeg, float dt, bool sideStrafe)
    {
        float vx = speed, vy = 0f;
        float w = leadDeg * Deg2Rad;
        RefAirTick(ref vx, ref vy, dt, MathF.Cos(w), MathF.Sin(w), sideStrafe);
        return MathF.Sqrt(vx * vx + vy * vy);
    }

    // ==================================================================================================
    //  cross-engine console scripts (generated from the SAME sweep the CSV uses)
    // ==================================================================================================

    /// <summary>
    /// The Xonotic-side capture script. Uses only stock console features, so it runs on an unmodified client:
    /// <c>cl_yawspeed</c> + <c>+left</c> for the open-loop constant turn, <c>+moveright</c> for the strafe key,
    /// <c>+jump</c> for autohop, and <c>speedmeter 1</c> + <c>developer 1</c> so the server prints the landing
    /// speed of every hop (sv_physics.qc <c>sys_phys_land</c>). Launch Xonotic with <c>-condebug</c> and the
    /// lines land in <c>qconsole.log</c> for tools/strafe-compare.py.
    /// </summary>
    private static string BuildXonoticCfg()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Xonotic side of the Vortex Arena cross-engine strafe capture.");
        sb.AppendLine("// GENERATED by StrafeBotHarness.StrafeBots_ConstantYawSweep_WritesCrossEngineKit — do not hand-edit.");
        sb.AppendLine("// Launch Xonotic with -condebug so console output is written to qconsole.log.");
        sb.AppendLine("//");
        sb.AppendLine("//   1. start a local game on a flat map, stand in the open");
        sb.AppendLine("//   2. exec this file");
        sb.AppendLine("//   3. run   strafe_all   (or a single leg: strafe_60)");
        sb.AppendLine("//   4. quit, then feed qconsole.log to tools/strafe-compare.py");
        sb.AppendLine();
        sb.AppendLine("speedmeter 1");
        sb.AppendLine("developer 1");
        sb.AppendLine("cl_forwardspeed 360; cl_backspeed 360; cl_sidespeed 360");
        sb.AppendLine("m_filter 0; m_accelerate 1");
        sb.AppendLine();
        sb.AppendLine("alias strafe_stop \"-left; -moveright; -jump; echo ^2STRAFE-END\"");
        sb.AppendLine();
        foreach (float omega in YawSweep)
        {
            string tag = ((int)omega).ToString(CultureInfo.InvariantCulture);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"alias strafe_{tag} \"echo ^3STRAFE-BEGIN {tag}; cl_yawspeed {omega:F0}; +moveright; +left; +jump; defer {BhopWindow:F1} strafe_stop\""));
        }
        sb.AppendLine();
        // Chain the legs with `defer` so one command runs the whole sweep hands-off; each leg needs its own
        // window plus a settling gap for the player to come to rest before the next one starts.
        float t = 0f;
        var chain = new StringBuilder("alias strafe_all \"");
        foreach (float omega in YawSweep)
        {
            string tag = ((int)omega).ToString(CultureInfo.InvariantCulture);
            chain.Append(string.Create(CultureInfo.InvariantCulture, $"defer {t:F1} strafe_{tag}; "));
            t += BhopWindow + 3f;
        }
        chain.Append('"');
        sb.AppendLine(chain.ToString());
        return sb.ToString();
    }

    /// <summary>The Vortex Arena side of the same capture. Identical command vocabulary — which is only true
    /// because +left/+right now TURN (BindTable/NetGame.ApplyKeyboardTurn) instead of aliasing to strafe.</summary>
    private static string BuildVortexCfg()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Vortex Arena side of the cross-engine strafe capture.");
        sb.AppendLine("// GENERATED by StrafeBotHarness.StrafeBots_ConstantYawSweep_WritesCrossEngineKit — do not hand-edit.");
        sb.AppendLine("// Same command vocabulary as the Xonotic leg, so the two runs are the same experiment.");
        sb.AppendLine();
        sb.AppendLine("speedmeter 1");
        sb.AppendLine("developer 1");
        sb.AppendLine("m_filter 0; m_accelerate 1");
        sb.AppendLine();
        sb.AppendLine("alias strafe_stop \"-left; -moveright; -jump; echo ^2STRAFE-END\"");
        sb.AppendLine();
        foreach (float omega in YawSweep)
        {
            string tag = ((int)omega).ToString(CultureInfo.InvariantCulture);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"alias strafe_{tag} \"echo ^3STRAFE-BEGIN {tag}; cl_yawspeed {omega:F0}; +moveright; +left; +jump; defer {BhopWindow:F1} strafe_stop\""));
        }
        sb.AppendLine();
        float t = 0f;
        var chain = new StringBuilder("alias strafe_all \"");
        foreach (float omega in YawSweep)
        {
            string tag = ((int)omega).ToString(CultureInfo.InvariantCulture);
            chain.Append(string.Create(CultureInfo.InvariantCulture, $"defer {t:F1} strafe_{tag}; "));
            t += BhopWindow + 3f;
        }
        chain.Append('"');
        sb.AppendLine(chain.ToString());
        return sb.ToString();
    }

    // ==================================================================================================
    //  helpers
    // ==================================================================================================

    private static float Speed2D(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static float Bound(float lo, float v, float hi) => MathF.Min(MathF.Max(lo, v), hi);

    private static float GeomLerp(float a, float lerp, float b)                  // player.qc:163-168
        => a == 0f ? (lerp < 1f ? 0f : b)
         : b == 0f ? (lerp > 0f ? 0f : a)
         : a * MathF.Pow(MathF.Abs(b / a), lerp);

    private static float AdjustAirAccelQW(float accelqw, float factor)           // player.qc:270-273
        => MathF.CopySign(Bound(0.000001f, 1f - (1f - MathF.Abs(accelqw)) * factor, 1f), accelqw);

    /// <summary>`_scratch/strafe/` at the repo root (gitignored per CLAUDE.md), found by walking up from the
    /// test binary until the solution file appears.</summary>
    private static string ScratchDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "VortexArena.sln")))
            d = d.Parent;
        string root = d?.FullName ?? AppContext.BaseDirectory;
        string dir = Path.Combine(root, "_scratch", "strafe");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (AnalyticWorld world, MutableClock clock) FlatWorld()
    {
        var brushes = new List<(int, float[])>
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                0, 0, 1, 0, 0, 0, -1, 64, 1, 0, 0, 8192, -1, 0, 0, 8192, 0, 1, 0, 8192, 0, -1, 0, 8192,
            }),
        };
        return (AnalyticWorld.FromPlanes(brushes), new MutableClock());
    }
}

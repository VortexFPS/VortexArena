using System;
using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Net;
using VortexArena.Tests.Camera;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// AIR-CONTROL parity (QC <c>CPM_PM_Aircontrol</c>, common/physics/player.qc:234-268) against an independent
/// re-transcription of the QC, across the parameter space the existing suites leave open.
///
/// <para>The other movement suites drive either PURE strafe (<c>strafity</c> 1, where air control returns
/// immediately) or PURE forward (<c>movity</c> 1, one point of the ramp). Real play lives in between: players
/// hold W plus a fraction of A/D and turn the mouse, which lands on the <c>movity</c> ramp, and a bunnyhop
/// alternates air-control ticks with ground contact and re-jumps. Both regimes are covered here.</para>
///
/// <para>What makes the middle interesting, at the stock <c>sv_aircontrol_flags 0</c>:</para>
/// <list type="bullet">
///   <item><c>movity = IsMoveInDirection(movement, 0)</c> falls linearly from 1 (dead ahead) to 0 at 45°.</item>
///   <item><c>k = 2·movity - 1</c>, and <c>k &lt;= 0</c> returns early — so air control switches OFF at
///         <b>22.5°</b> of wish-move offset, not at 45°.</item>
///   <item><c>strafity</c> stays 0 until the wish-move passes 45°, so between 22.5° and 45° the player gets
///         NEITHER air control NOR the strafe blends. A real dead band, and exactly where a W+A player who
///         leans on the strafe key ends up.</item>
/// </list>
///
/// <para>The reference below transcribes the QC afresh (including QuakeC's own <c>remainder</c>/<c>rint</c>
/// semantics from lib/warpzone/mathlib.qc, which differ from <see cref="MathF.IEEERemainder"/> at exact
/// half-integers) so a shared transcription bug in the port cannot hide. See docs/MOVEMENT-PHYSICS.md §4.6
/// and §7.4.</para>
/// </summary>
public class AirControlParityTests
{
    private readonly ITestOutputHelper _out;
    public AirControlParityTests(ITestOutputHelper o) => _out = o;

    // Live server tick — sys_ticrate 0.015625 (see MOVEMENT-PHYSICS.md §7.2), NOT SimulationLoop.TicRate.
    private const float Dt = 1f / 64f;
    private const float Rad2Deg = 180f / MathF.PI;
    private const float Deg2Rad = MathF.PI / 180f;

    /// <summary>physicsX.cfg air-branch values, with the air-control knobs left variable per test.</summary>
    private struct P
    {
        public float MaxAirSpeed, AirAccelerate, AirAccelQW, AirStrafeAccelQW, StretchFactor;
        public float AirStopAccelerate, AirStrafeAccelerate, MaxAirStrafeSpeed, SpeedLimitNonQW;
        public float AirControl, AirControlPower, AirControlPenalty;
        public int AirControlFlags;
        public bool AirStopAccelerateFull;

        public static P Stock() => new()
        {
            MaxAirSpeed = 360f, AirAccelerate = 2f, AirAccelQW = -0.8f, AirStrafeAccelQW = -0.95f,
            StretchFactor = 2f, AirStopAccelerate = 3f, AirStrafeAccelerate = 18f, MaxAirStrafeSpeed = 100f,
            SpeedLimitNonQW = 900f, AirControl = 100f, AirControlPower = 2f, AirControlPenalty = 0f,
            AirControlFlags = 0, AirStopAccelerateFull = false,
        };
    }

    private const float MoveScale = 360f; // sys_phys_fixspeed: max(sv_maxspeed, sv_maxairspeed)

    // ==================================================================================================
    //  1. the movity ramp — the regime real players occupy
    // ==================================================================================================

    [Fact]
    public void MovityRamp_WPlusPartialStrafe_PortMatchesReference()
    {
        // Sweep the wish-move from dead ahead (pure W) round to pure side (pure D) in 2.5° steps. This
        // crosses BOTH gates: air control dies at 22.5°, the strafe blends wake up past 45°.
        _out.WriteLine($"{"wish°",6} {"movity",7} {"k",7} {"strafity",9} {"port",9} {"ref",9} {"delta",8}");
        _out.WriteLine(new string('-', 62));

        float worst = 0f;
        for (float wishDeg = 0f; wishDeg <= 90f; wishDeg += 2.5f)
        {
            // move = (forward, side) as a unit direction at wishDeg off forward, scaled to wish-velocity units.
            float mf = MathF.Cos(wishDeg * Deg2Rad) * MoveScale;
            float ms = MathF.Sin(wishDeg * Deg2Rad) * MoveScale;

            float port = RunPort(P.Stock(), mf, ms, turnRate: 90f, seconds: 1.0f, ducked: false);
            float reference = RunRef(P.Stock(), mf, ms, turnRate: 90f, seconds: 1.0f, ducked: false);
            float d = MathF.Abs(port - reference);
            worst = MathF.Max(worst, d);

            float movity = IsMoveInDirection(mf, ms, 0f);
            float strafity = IsMoveInDirection(mf, ms, -90f) + IsMoveInDirection(mf, ms, +90f);
            _out.WriteLine($"{wishDeg,6:F1} {movity,7:F3} {2f * movity - 1f,7:F3} {strafity,9:F3} "
                         + $"{port,9:F2} {reference,9:F2} {d,8:F3}");
        }

        Assert.True(worst < 0.5f,
            $"air control must match the QC reference across the whole movity ramp (worst |delta| {worst:F3} u/s)");
    }

    [Fact]
    public void AirControl_SwitchesOffAt22Point5Degrees_NotAt45()
    {
        // k = 2*movity - 1 with movity = 1 - wish/45  ->  k = 1 - 2*wish/45, zero at wish = 22.5°.
        // Guard the cliff itself: just inside it air control turns the velocity, just outside it does nothing.
        var p = P.Stock();
        p.AirStrafeAccelerate = 0f;   // isolate air control from the strafe-accel blend
        p.MaxAirStrafeSpeed = 0f;
        p.AirStrafeAccelQW = 0f;

        // Measure air control's OWN contribution: the same run with sv_aircontrol 100 minus sv_aircontrol 0.
        // An absolute turn figure is not usable here, because PM_Accelerate redirects the velocity on its own
        // and does so MORE as the wish-move swings toward perpendicular — at 40° that alone out-turns the
        // air-controlled 25° case. The difference isolates the mechanism under test.
        _out.WriteLine($"{"wish°",6} {"ac=100",8} {"ac=0",8} {"ac only",8}");
        var results = new Dictionary<float, float>();
        foreach (float wish in new[] { 0f, 10f, 20f, 22f, 23f, 25f, 30f, 40f })
        {
            var off = p; off.AirControl = 0f;
            float on = TurnedDegrees(p, wish, turnRate: 120f, seconds: 0.5f, ducked: false);
            float without = TurnedDegrees(off, wish, turnRate: 120f, seconds: 0.5f, ducked: false);
            results[wish] = on - without;
            _out.WriteLine($"{wish,6:F0} {on,8:F2} {without,8:F2} {results[wish],8:F2}");
        }

        Assert.True(results[0f] > 5f, $"dead ahead, air control must dominate the turn (got {results[0f]:F2}°)");
        Assert.True(results[20f] > 0.5f, $"at 20° (inside 22.5°) air control must still act (got {results[20f]:F2}°)");
        Assert.True(results[22f] > results[23f], "the contribution must be falling as k -> 0 across 22.5°");
        foreach (float wish in new[] { 23f, 25f, 30f, 40f })
            Assert.True(MathF.Abs(results[wish]) < 0.01f,
                $"past 22.5° air control must contribute NOTHING (wish {wish}° gave {results[wish]:F4}°)");
    }

    [Fact]
    public void DeadBand_Between22Point5And45_HasNeitherAirControlNorStrafeBlends()
    {
        // The documented gap: air control needs < 22.5°, the strafe blends need > 45°. A wish-move at 33°
        // gets the plain air branch (sv_airaccelerate 2, wishspeed 360) and nothing else.
        const float wish = 33f;
        float mf = MathF.Cos(wish * Deg2Rad) * MoveScale, ms = MathF.Sin(wish * Deg2Rad) * MoveScale;

        Assert.True(2f * IsMoveInDirection(mf, ms, 0f) - 1f <= 0f, "k must be <= 0 at 33° (air control off)");
        Assert.Equal(0f, IsMoveInDirection(mf, ms, -90f) + IsMoveInDirection(mf, ms, +90f), 5);

        // With air control off and strafity 0, disabling aircontrol entirely must change nothing at all.
        var withAc = P.Stock();
        var noAc = P.Stock();
        noAc.AirControl = 0f;
        float a = RunPort(withAc, mf, ms, turnRate: 90f, seconds: 1.0f, ducked: false);
        float b = RunPort(noAc, mf, ms, turnRate: 90f, seconds: 1.0f, ducked: false);
        Assert.Equal(a, b, 3);
    }

    // ==================================================================================================
    //  2. the air-control knobs
    // ==================================================================================================

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void AirControlFlags_EveryCombination_PortMatchesReference(int flags)
    {
        // bit0 = also count backward input, bit1 = also count sideward, bit2 = crouching has no impact.
        // Stock Xonotic is 0; CPMA and bones ship 5 (backward + no-crouch-impact).
        var p = P.Stock();
        p.AirControlFlags = flags;

        float worst = 0f;
        foreach (float wishDeg in new[] { 0f, 15f, 30f, 60f, 90f, 135f, 180f })
        {
            float mf = MathF.Cos(wishDeg * Deg2Rad) * MoveScale;
            float ms = MathF.Sin(wishDeg * Deg2Rad) * MoveScale;
            float port = RunPort(p, mf, ms, turnRate: 90f, seconds: 0.8f, ducked: false);
            float reference = RunRef(p, mf, ms, turnRate: 90f, seconds: 0.8f, ducked: false);
            worst = MathF.Max(worst, MathF.Abs(port - reference));
        }
        Assert.True(worst < 0.5f, $"sv_aircontrol_flags {flags}: worst |delta| {worst:F3} u/s");
    }

    [Theory]
    [InlineData(1f, 0f)] [InlineData(2f, 0f)] [InlineData(3f, 0f)]
    [InlineData(2f, 180f)]   // the overkill preset: aircontrol_penalty 180
    public void AirControlPowerAndPenalty_PortMatchesReference(float power, float penalty)
    {
        var p = P.Stock();
        p.AirControlPower = power;
        p.AirControlPenalty = penalty;

        float worst = 0f;
        foreach (float wishDeg in new[] { 0f, 5f, 10f, 15f, 20f })
        {
            float mf = MathF.Cos(wishDeg * Deg2Rad) * MoveScale;
            float ms = MathF.Sin(wishDeg * Deg2Rad) * MoveScale;
            float port = RunPort(p, mf, ms, turnRate: 100f, seconds: 0.8f, ducked: false);
            float reference = RunRef(p, mf, ms, turnRate: 100f, seconds: 0.8f, ducked: false);
            worst = MathF.Max(worst, MathF.Abs(port - reference));
        }
        Assert.True(worst < 0.5f, $"power {power} penalty {penalty}: worst |delta| {worst:F3} u/s");
    }

    [Fact]
    public void AirControlPenalty_TradesSpeedForTurnRate()
    {
        // player.qc:261 — xyspeed -= penalty * sqrt(1 - dot²) * k. Non-zero penalty must COST speed while
        // turning; that is the whole point of the knob (overkill ships 180).
        var free = P.Stock();
        var taxed = P.Stock();
        taxed.AirControlPenalty = 180f;

        float speedFree = RunPort(free, MoveScale, 0f, turnRate: 100f, seconds: 1.0f, ducked: false);
        float speedTaxed = RunPort(taxed, MoveScale, 0f, turnRate: 100f, seconds: 1.0f, ducked: false);
        _out.WriteLine($"penalty 0 -> {speedFree:F2} u/s, penalty 180 -> {speedTaxed:F2} u/s");
        Assert.True(speedTaxed < speedFree,
            $"aircontrol_penalty must cost speed while turning ({speedTaxed:F2} vs {speedFree:F2})");
    }

    [Fact]
    public void Crouching_HalvesAirControl_UnlessFlagBit2Set()
    {
        // player.qc:248-249 — k *= bound(0, wishspeed/PHYS_MAXAIRSPEED, 1), and wishspeed here is wishspeed2,
        // which the duck branch already halved (physics.qc:316). So crouching halves k, and flag bit 2 opts out.
        float standing = TurnedDegrees(P.Stock(), 0f, turnRate: 100f, seconds: 0.6f, ducked: false);
        float ducked = TurnedDegrees(P.Stock(), 0f, turnRate: 100f, seconds: 0.6f, ducked: true);

        var noImpact = P.Stock();
        noImpact.AirControlFlags = 4; // bit2: crouching has no impact
        float duckedExempt = TurnedDegrees(noImpact, 0f, turnRate: 100f, seconds: 0.6f, ducked: true);

        _out.WriteLine($"turned: standing {standing:F2}°, ducked {ducked:F2}°, ducked+flag4 {duckedExempt:F2}°");
        Assert.True(ducked < standing, $"crouching must weaken air control ({ducked:F2}° vs {standing:F2}°)");
        Assert.True(duckedExempt > ducked, $"flag bit 2 must exempt crouch ({duckedExempt:F2}° vs {ducked:F2}°)");
    }

    [Fact]
    public void BrakingGate_NoAirControlWhenWishOpposesVelocity()
    {
        // player.qc:258 — `if (dot > 0)`, "we can't change direction while slowing down". Aim the wish-move
        // BEHIND the velocity: air control must not rotate the velocity at all.
        var p = P.Stock();
        p.AirStrafeAccelerate = 0f; p.MaxAirStrafeSpeed = 0f; p.AirStrafeAccelQW = 0f;

        // Keep the wish direction pinned 180° from the CURRENT velocity every tick, so dot stays at -1 for the
        // whole run. (A fixed view angle will NOT do: PM_Accelerate brakes and then drives the player
        // backwards, the velocity heading swings past the wish direction, dot turns positive and air control
        // legitimately engages — which is what an earlier version of this test mistook for a gate failure.)
        // With dot <= 0 throughout, sv_aircontrol must make no difference at all.
        var off = p; off.AirControl = 0f;
        float speedOn = RunBraking(p), speedOff = RunBraking(off);

        _out.WriteLine($"braking (wish pinned opposite velocity): ac=100 -> {speedOn:F4} u/s, ac=0 -> {speedOff:F4} u/s");
        Assert.Equal(speedOff, speedOn, 4);
    }

    /// <summary>Air run whose wish direction is re-aimed 180° from the live velocity each tick, so
    /// <c>CPM_PM_Aircontrol</c>'s <c>dot &gt; 0</c> gate is never satisfied.</summary>
    private static float RunBraking(P p)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock) { Cvars = CvarsFor(p) };
        ClearHooks();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 20000f), Velocity = new Vector3(400f, 0f, 0f), OnGround = false };

        float now = 0f;
        for (int i = 0; i < 32; i++) // 0.5 s — stop before the velocity reverses through zero
        {
            now += Dt; clock.Time = now;
            // Forward=1 → wishdir is the view's forward vector, so yaw = (velocity heading + 180°).
            float yaw = MathF.Atan2(s.Velocity.Y, s.Velocity.X) * Rad2Deg + 180f;
            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, yaw, 0f),
                Forward = 1f,
                DeltaTime = Dt,
            };
            step.Step(ref s, in cmd, default);
        }
        return Speed2D(s.Velocity);
    }

    // ==================================================================================================
    //  3. bunnyhopping with air control — the real gameplay loop
    // ==================================================================================================

    [Fact]
    public void Bunnyhop_EveryAirTick_MatchesReference_InSitu()
    {
        // The strongest form of the check: run a REAL bunnyhop through the port's full sim (gravity,
        // collision, landings, autohop re-jumps) and, for every tick that was airborne at entry AND at exit
        // (so no landing clipped the velocity and no jump added to it), feed that tick's ACTUAL entry state
        // and input into the QC reference and compare the horizontal result. Any divergence in the air
        // branch shows up in situ, at the states bunnyhopping actually visits, rather than at a synthetic
        // start state.
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        ClearHooks();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 88.03f), Velocity = new Vector3(400f, 0f, 0f), OnGround = true };

        var p = P.Stock();
        float now = 0f, worst = 0f;
        int compared = 0, airTicks = 0, groundTicks = 0;
        // W + a third of a strafe key = ~18° wish offset: inside the air-control cliff, so this hop is
        // genuinely exercising CPM_PM_Aircontrol on every air tick.
        const float wishDeg = 18f;
        float mf = MathF.Cos(wishDeg * Deg2Rad), ms = MathF.Sin(wishDeg * Deg2Rad);

        for (int i = 0; i < 400; i++) // ~6.25 s at 64 Hz
        {
            now += Dt; clock.Time = now;
            float yaw = 100f * now; // constant mouse sweep

            Vector3 entryVel = s.Velocity;
            bool entryGround = s.OnGround;

            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, yaw, 0f),
                Forward = mf, Side = ms,
                DeltaTime = Dt,
                Buttons = (int)InputButtons.Jump,
            };
            step.Step(ref s, in cmd, default);

            if (entryGround) { groundTicks++; continue; }
            airTicks++;
            if (s.OnGround) continue; // landed during this tick: WalkMove clipped the velocity

            float vx = entryVel.X, vy = entryVel.Y;
            RefAirTick(ref vx, ref vy, p, mf * MoveScale, ms * MoveScale, yaw, ducked: false);
            float d = MathF.Sqrt((vx - s.Velocity.X) * (vx - s.Velocity.X) + (vy - s.Velocity.Y) * (vy - s.Velocity.Y));
            worst = MathF.Max(worst, d);
            compared++;
        }

        _out.WriteLine($"bhop ticks: {groundTicks} ground-entry, {airTicks} air, {compared} compared; "
                     + $"worst per-tick |delta v_xy| = {worst:F5} u/s; end speed {Speed2D(s.Velocity):F1}");

        Assert.True(compared > 250, $"the run must actually spend its time airborne (compared {compared} ticks)");
        Assert.True(worst < 0.05f, $"every air tick of a real bunnyhop must match the QC reference "
                                 + $"(worst per-tick |delta| {worst:F5} u/s)");
    }

    [Fact]
    public void Bunnyhop_WithAirControl_TurnsAndKeepsSpeed()
    {
        // Behavioural companion to the differential test above: holding W + partial strafe + jump while
        // sweeping the mouse must actually carry the player around a curve without bleeding speed — the
        // thing a player means by "air control". Guards against air control silently becoming a no-op.
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock);
        ClearHooks();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 88.03f), Velocity = new Vector3(400f, 0f, 0f), OnGround = true };

        const float wishDeg = 15f;
        float mf = MathF.Cos(wishDeg * Deg2Rad), ms = MathF.Sin(wishDeg * Deg2Rad);
        float now = 0f, turned = 0f, prev = MathF.Atan2(s.Velocity.Y, s.Velocity.X);

        for (int i = 0; i < 256; i++) // 4 s
        {
            now += Dt; clock.Time = now;
            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, 80f * now, 0f),
                Forward = mf, Side = ms,
                DeltaTime = Dt,
                Buttons = (int)InputButtons.Jump,
            };
            step.Step(ref s, in cmd, default);
            float h = MathF.Atan2(s.Velocity.Y, s.Velocity.X);
            turned += WrapPi(h - prev);
            prev = h;
        }

        float end = Speed2D(s.Velocity);
        _out.WriteLine($"bhop + air control: turned {MathF.Abs(turned) * Rad2Deg:F1}° over 4 s, end speed {end:F1} u/s");
        Assert.True(MathF.Abs(turned) * Rad2Deg > 90f,
            $"air control must carry the velocity around a real curve (turned {MathF.Abs(turned) * Rad2Deg:F1}°)");
        Assert.True(end > 350f, $"a controlled bunnyhop must not bleed speed (end {end:F1} u/s)");
    }

    [Fact]
    public void Bunnyhop_AirControlOff_TurnsMarkedlyLess()
    {
        // Differential guard on the same loop: sv_aircontrol 0 must visibly reduce how far the velocity can be
        // steered. If this ever stops holding, air control is not reaching the bunnyhop path at all.
        // Sweep the wish-move offset inside a REAL bunnyhop and measure air control's own contribution
        // (sv_aircontrol 100 minus sv_aircontrol 0). This is the movity ramp observed through the full loop:
        // strongest with W dead ahead, weakening as the strafe key leans in, gone past 22.5°.
        _out.WriteLine($"{"wish°",6} {"ac=100",9} {"ac=0",9} {"ac only",9}");
        var contribution = new Dictionary<float, float>();
        foreach (float wish in new[] { 0f, 5f, 15f, 30f })
        {
            var off = P.Stock(); off.AirControl = 0f;
            float on = BhopTurnDegrees(P.Stock(), wish);
            float without = BhopTurnDegrees(off, wish);
            contribution[wish] = on - without;
            _out.WriteLine($"{wish,6:F0} {on,9:F1} {without,9:F1} {contribution[wish],9:F1}");
        }

        Assert.True(contribution[0f] > 20f,
            $"with W dead ahead, air control must dominate the bunnyhop turn (added {contribution[0f]:F1}°)");
        Assert.True(contribution[0f] > contribution[5f] && contribution[5f] > contribution[15f],
            "air control's share of the bunnyhop turn must fall along the movity ramp");
        Assert.True(contribution[15f] > 5f, $"at 15° it must still matter (added {contribution[15f]:F1}°)");
        Assert.True(MathF.Abs(contribution[30f]) < 0.01f,
            $"past 22.5° air control must be inert even mid-bunnyhop (added {contribution[30f]:F4}°)");
    }

    // ==================================================================================================
    //  sv_landing_yaw_advance prototype (docs/MOVEMENT-PHYSICS.md §11.6)
    // ==================================================================================================

    [Fact]
    public void LandingYawAdvance_ShiftsTheStrafeCycleTowardXonotic()
    {
        // Real Xonotic applies each autohop rejump tick with a view yaw 2-3 input frames ahead of the
        // contiguous stream (measured: +2 ticks at 80 deg/s, fit residual 0.0000 over 13 hops), which holds
        // its strafe cycle at a ~80 deg lead / 673 u/s after 10 s. The port's strict 1:1 model reaches
        // ~86 deg / 801 u/s from the same input. sv_landing_yaw_advance N replays the advance; this guards
        // that (a) OFF is the unchanged baseline and (b) ON pulls the cycle toward Xonotic's.
        float off = RunFromRestStrafe(advance: 0f);
        float on2 = RunFromRestStrafe(advance: 2f);

        _out.WriteLine($"from-rest A-strafe, 80 deg/s sweep, 10 s: advance 0 -> {off:F1} u/s, advance 2 -> {on2:F1} u/s");
        _out.WriteLine("real Xonotic (fx_80 runs): ~673 u/s; the port baseline measured ~801 u/s");

        // HONEST RESULT (2026-08-07): the prototype FIRES but does NOT close the cross-engine gap —
        // advance 2 moves the 10 s end speed by only ~-4 u/s of the ~-128 needed to land on Xonotic's
        // cycle. One kicked tick per hop is too little; whatever real Xonotic's input pipeline does, it is
        // not equivalent to a single-tick yaw advance at the rejump. Kept as a guard on both facts:
        // the baseline is unchanged with the cvar off, and the hook demonstrably reaches the physics.
        Assert.True(off > 760f, $"baseline (advance 0) must stay the fast/wide cycle (~801), got {off:F1}");
        Assert.True(MathF.Abs(on2 - off) > 0.5f,
            $"advance 2 must reach the physics (measured effect ~-4 u/s), got {on2:F1} vs {off:F1}");
        Assert.True(on2 > 700f,
            "if this ever drops near Xonotic's 673, the prototype started actually closing the gap — "
            + "revisit docs/MOVEMENT-PHYSICS.md §11.6 before trusting it");
    }

    /// <summary>From-rest bunnyhop strafe on the flat floor: hold A, sweep the view left at 80 deg/s, jump
    /// held, 10 s at the live 64 Hz tick. Returns the final horizontal speed.</summary>
    private static float RunFromRestStrafe(float advance)
    {
        var (world, clock) = FlatWorld();
        var p = P.Stock();
        var cvars = (DictCvars)CvarsFor(p);
        cvars.Set("sv_landing_yaw_advance", advance.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Api.Services = new MovementTestServices(world, clock) { Cvars = cvars };
        ClearHooks();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 88.03f), Velocity = Vector3.Zero, OnGround = true };

        float now = 0f;
        int ticks = (int)MathF.Round(10f / Dt);
        for (int i = 0; i < ticks; i++)
        {
            now += Dt; clock.Time = now;
            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, 80f * now, 0f),
                Side = -1f,
                DeltaTime = Dt,
                Buttons = (int)InputButtons.Jump,
            };
            step.Step(ref s, in cmd, default);
        }
        return MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
    }

    // ==================================================================================================
    //  port drivers
    // ==================================================================================================

    /// <summary>Run the port's real sim in free air and return the final horizontal speed.</summary>
    private static float RunPort(P p, float moveFwd, float moveSide, float turnRate, float seconds, bool ducked,
        float viewOffsetDeg = 0f)
        => RunPortInternal(p, moveFwd, moveSide, turnRate, seconds, ducked, viewOffsetDeg).speed;

    /// <summary>Run the port's real sim in free air and return how far the VELOCITY HEADING rotated (degrees).</summary>
    private static float TurnedDegrees(P p, float wishDeg, float turnRate, float seconds, bool ducked,
        float viewOffsetDeg = 0f)
    {
        float mf = MathF.Cos(wishDeg * Deg2Rad) * MoveScale, ms = MathF.Sin(wishDeg * Deg2Rad) * MoveScale;
        return RunPortInternal(p, mf, ms, turnRate, seconds, ducked, viewOffsetDeg).turnedDeg;
    }

    private static (float speed, float turnedDeg) RunPortInternal(P p, float moveFwd, float moveSide,
        float turnRate, float seconds, bool ducked, float viewOffsetDeg)
    {
        var (world, clock) = FlatWorld();
        var services = new MovementTestServices(world, clock) { Cvars = CvarsFor(p) };
        Api.Services = services;
        ClearHooks();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 20000f), Velocity = new Vector3(400f, 0f, 0f), OnGround = false };

        float mag = MathF.Sqrt(moveFwd * moveFwd + moveSide * moveSide);
        float f = mag > 0f ? moveFwd / mag : 0f, sd = mag > 0f ? moveSide / mag : 0f;

        float now = 0f, turned = 0f, prev = MathF.Atan2(s.Velocity.Y, s.Velocity.X);
        int ticks = (int)MathF.Round(seconds / Dt);
        for (int i = 0; i < ticks; i++)
        {
            now += Dt; clock.Time = now;
            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, turnRate * now + viewOffsetDeg, 0f),
                Forward = f, Side = sd,
                DeltaTime = Dt,
                Buttons = ducked ? (int)InputButtons.Crouch : 0,
            };
            step.Step(ref s, in cmd, default);
            float h = MathF.Atan2(s.Velocity.Y, s.Velocity.X);
            turned += WrapPi(h - prev);
            prev = h;
        }
        return (Speed2D(s.Velocity), MathF.Abs(turned) * Rad2Deg);
    }

    private static float BhopTurnDegrees(P p, float wishDeg = 15f)
    {
        var (world, clock) = FlatWorld();
        Api.Services = new MovementTestServices(world, clock) { Cvars = CvarsFor(p) };
        ClearHooks();

        var step = new PlayerPhysicsStep(new Vector3(-16f, -16f, -24f), new Vector3(16f, 16f, 45f));
        var s = new PredictedState { Origin = new Vector3(0f, 0f, 88.03f), Velocity = new Vector3(400f, 0f, 0f), OnGround = true };

        float mf = MathF.Cos(wishDeg * Deg2Rad), ms = MathF.Sin(wishDeg * Deg2Rad);
        float now = 0f, turned = 0f, prev = MathF.Atan2(s.Velocity.Y, s.Velocity.X);
        for (int i = 0; i < 256; i++)
        {
            now += Dt; clock.Time = now;
            var cmd = new InputCommand
            {
                ViewAngles = new Vector3(0f, 80f * now, 0f),
                Forward = mf, Side = ms,
                DeltaTime = Dt,
                Buttons = (int)InputButtons.Jump,
            };
            step.Step(ref s, in cmd, default);
            float h = MathF.Atan2(s.Velocity.Y, s.Velocity.X);
            turned += WrapPi(h - prev);
            prev = h;
        }
        return MathF.Abs(turned) * Rad2Deg;
    }

    /// <summary>Free-air reference run: the QC air branch only, no gravity/collision (the port runs are high
    /// enough that neither happens either, and gravity touches only z).</summary>
    private static float RunRef(P p, float moveFwd, float moveSide, float turnRate, float seconds, bool ducked)
    {
        float mag = MathF.Sqrt(moveFwd * moveFwd + moveSide * moveSide);
        float f = mag > 0f ? moveFwd / mag * MoveScale : 0f;
        float sd = mag > 0f ? moveSide / mag * MoveScale : 0f;

        float vx = 400f, vy = 0f, now = 0f;
        int ticks = (int)MathF.Round(seconds / Dt);
        for (int i = 0; i < ticks; i++)
        {
            now += Dt;
            RefAirTick(ref vx, ref vy, p, f, sd, turnRate * now, ducked);
        }
        return MathF.Sqrt(vx * vx + vy * vy);
    }

    // ==================================================================================================
    //  independent QC reference — ecs/systems/physics.qc:249-371 + common/physics/player.qc:154-341
    // ==================================================================================================

    /// <summary>One airborne tick for an arbitrary wish-move. <paramref name="moveFwd"/>/<paramref name="moveSide"/>
    /// are the raw usercmd movement values (velocity units); <paramref name="yawDeg"/> the view yaw.</summary>
    private static void RefAirTick(ref float vx, ref float vy, P p, float moveFwd, float moveSide, float yawDeg,
        bool ducked)
    {
        // physics.qc:249-255 — yaw-only basis, wishvel = forward*move.x + right*move.y.
        float cy = MathF.Cos(yawDeg * Deg2Rad), sy = MathF.Sin(yawDeg * Deg2Rad);
        float wvx = cy * moveFwd + sy * moveSide;      // forward=(cy,sy), right=(sy,-cy)
        float wvy = sy * moveFwd - cy * moveSide;

        float wishspeed = MathF.Sqrt(wvx * wvx + wvy * wvy);          // :303
        float wdx = wishspeed != 0f ? wvx / wishspeed : 0f;           // :306
        float wdy = wishspeed != 0f ? wvy / wishspeed : 0f;
        wishspeed = MathF.Min(wishspeed, p.MaxAirSpeed);              // :307 (com_phys_vel_max)

        float airaccelqw = p.AirAccelQW;                              // :312
        float wishspeed0 = wishspeed;                                 // :313
        if (ducked) wishspeed *= 0.5f;                                // :316-317
        float airaccel = p.AirAccelerate;                             // :319
        float wishspeed2 = wishspeed;                                 // :322

        float speed = MathF.Sqrt(vx * vx + vy * vy);
        if (p.AirStopAccelerate != 0f && speed > 0f)                  // :325-335
        {
            float dot = (vx / speed) * wdx + (vy / speed) * wdy;
            if (dot < 0f)
                airaccel = p.AirStopAccelerateFull
                    ? p.AirStopAccelerate
                    : airaccel + (airaccel - p.AirStopAccelerate) * dot;
        }

        float strafity = IsMoveInDirection(moveFwd, moveSide, -90f)   // :344
                       + IsMoveInDirection(moveFwd, moveSide, +90f);
        if (p.MaxAirStrafeSpeed != 0f)                                // :345-348
            wishspeed = MathF.Min(wishspeed, GeomLerp(p.MaxAirSpeed, strafity, p.MaxAirStrafeSpeed));
        if (p.AirStrafeAccelerate != 0f)                              // :350-351
            airaccel = GeomLerp(airaccel, strafity, p.AirStrafeAccelerate);
        if (p.AirStrafeAccelQW != 0f)                                 // :353-357
            airaccelqw = ((strafity > 0.5f ? p.AirStrafeAccelQW : p.AirAccelQW) >= 0f ? +1f : -1f)
                * (1f - GeomLerp(1f - MathF.Abs(p.AirAccelQW), strafity, 1f - MathF.Abs(p.AirStrafeAccelQW)));

        RefAccelerate(ref vx, ref vy, wdx, wdy, wishspeed, wishspeed0, airaccel, airaccelqw,
            p.StretchFactor, p.SpeedLimitNonQW);                      // :364-366

        if (p.AirControl != 0f)                                       // :369-370
            RefAircontrol(ref vx, ref vy, p, moveFwd, moveSide, wdx, wdy, wishspeed2);
    }

    /// <summary>player.qc:280-341 PM_Accelerate (2D; sidefric 0 at stock so vel_perpend is untouched).</summary>
    private static void RefAccelerate(ref float vx, ref float vy, float wdx, float wdy,
        float wishspeed, float wishspeed0, float accel, float accelqw, float stretchfactor, float speedlimit)
    {
        float speedclamp = stretchfactor > 0f ? stretchfactor : accelqw < 0f ? 1f : -1f;
        accelqw = MathF.Abs(accelqw);
        wishspeed0 = wishspeed; // GAMEPLAYFIX_Q2AIRACCELERATE — ON in Base (:288-289)

        float vel_straight = vx * wdx + vy * wdy;
        float perpx = vx - vel_straight * wdx, perpy = vy - vel_straight * wdy;
        float step = accel * Dt * wishspeed0;
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
                if (vel_xy_current < pre) { float k = vel_xy_current / pre; vx *= k; vy *= k; }
            }
        }
    }

    /// <summary>player.qc:234-268 CPM_PM_Aircontrol (2D — the QC saves/restores velocity_z around it).</summary>
    private static void RefAircontrol(ref float vx, ref float vy, P p, float moveFwd, float moveSide,
        float wdx, float wdy, float wishspeed)
    {
        float movity = IsMoveInDirection(moveFwd, moveSide, 0f);                    // :236
        if ((p.AirControlFlags & 1) != 0)                                           // :237-238 backward
            movity += IsMoveInDirection(moveFwd, moveSide, 180f);
        if ((p.AirControlFlags & 2) != 0)                                           // :239-243 sideward
        {
            movity += IsMoveInDirection(moveFwd, moveSide, 90f);
            movity += IsMoveInDirection(moveFwd, moveSide, -90f);
        }

        float k = 2f * movity - 1f;                                                 // :245
        if (k <= 0f) return;                                                        // :246-247
        if ((p.AirControlFlags & 4) == 0)                                           // :248-249 crouch impact
            k *= Bound(0f, wishspeed / p.MaxAirSpeed, 1f);

        float xyspeed = MathF.Sqrt(vx * vx + vy * vy);                              // :253
        if (xyspeed == 0f) return;
        float nx = vx / xyspeed, ny = vy / xyspeed;                                 // :254
        float dot = nx * wdx + ny * wdy;                                            // :256

        if (dot > 0f)                                                               // :258
        {
            k *= MathF.Pow(dot, p.AirControlPower) * Dt;                            // :260
            xyspeed = MathF.Max(0f, xyspeed - p.AirControlPenalty                    // :261
                * MathF.Sqrt(MathF.Max(0f, 1f - dot * dot)) * k);
            k *= 32f * MathF.Abs(p.AirControl);                                     // :262
            float mx = nx * xyspeed + wdx * k, my = ny * xyspeed + wdy * k;         // :263
            float len = MathF.Sqrt(mx * mx + my * my);
            if (len > 0f) { nx = mx / len; ny = my / len; }
        }
        vx = nx * xyspeed;                                                          // :266
        vy = ny * xyspeed;
    }

    // --- QuakeC math, transcribed from lib/warpzone/mathlib.qc + the DP rint builtin ---------------

    /// <summary>DP <c>VM_rint</c> (prvm_cmds.c:1508-1517): round half AWAY FROM ZERO, which is NOT what
    /// <see cref="MathF.IEEERemainder"/> (half-to-even) does. The two disagree only at exact half-integers,
    /// i.e. <c>remainder(ang, 360)</c> at ang = ±180 — where both results saturate <see cref="IsMoveInDirection"/>
    /// to 0. Transcribed faithfully so this reference would EXPOSE the difference rather than share it.</summary>
    private static float QcRint(float f) => f > 0f ? MathF.Floor(f + 0.5f) : MathF.Ceiling(f - 0.5f);

    /// <summary>QC <c>remainder(e, f) = e - f * rint(e / f)</c> (mathlib.qc:207-210).</summary>
    private static float QcRemainder(float e, float f) => e - f * QcRint(e / f);

    /// <summary>player.qc:154-161 IsMoveInDirection, on the raw (forward, side) move values.</summary>
    private static float IsMoveInDirection(float mvx, float mvy, float ang)
    {
        if (mvx == 0f && mvy == 0f) return 0f;
        ang -= Rad2Deg * MathF.Atan2(mvy, mvx);
        ang = QcRemainder(ang, 360f) / 45f;
        return ang > 1f ? 0f : ang < -1f ? 0f : 1f - MathF.Abs(ang);
    }

    private static float GeomLerp(float a, float lerp, float b)                     // player.qc:163-168
        => a == 0f ? (lerp < 1f ? 0f : b)
         : b == 0f ? (lerp > 0f ? 0f : a)
         : a * MathF.Pow(MathF.Abs(b / a), lerp);

    private static float AdjustAirAccelQW(float accelqw, float factor)              // player.qc:270-273
        => MathF.CopySign(Bound(0.000001f, 1f - (1f - MathF.Abs(accelqw)) * factor, 1f), accelqw);

    private static float Bound(float lo, float v, float hi) => MathF.Min(MathF.Max(lo, v), hi);

    // ==================================================================================================
    //  harness plumbing
    // ==================================================================================================

    /// <summary>A cvar store holding the air-control knobs, so <c>MovementParameters.FromCvars</c> picks them
    /// up. Everything not listed reads "" / 0 and falls back to the compiled-in stock value.</summary>
    private static ICvarService CvarsFor(P p)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sv_aircontrol"] = Str(p.AirControl),
            ["sv_aircontrol_power"] = Str(p.AirControlPower),
            ["sv_aircontrol_penalty"] = Str(p.AirControlPenalty),
            ["sv_aircontrol_flags"] = Str(p.AirControlFlags),
            ["sv_airstrafeaccelerate"] = Str(p.AirStrafeAccelerate),
            ["sv_maxairstrafespeed"] = Str(p.MaxAirStrafeSpeed),
            ["sv_airstrafeaccel_qw"] = Str(p.AirStrafeAccelQW),
            ["sv_airstopaccelerate"] = Str(p.AirStopAccelerate),
        };
        return new DictCvars(d);
    }

    private static string Str(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Minimal settable cvar store. <c>MovementParameters</c> treats a cvar as ABSENT when
    /// <c>GetString</c> is empty, so unlisted names fall through to the stock defaults.</summary>
    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v;
        public DictCvars(Dictionary<string, string> v) => _v = v;
        public float GetFloat(string name) =>
            _v.TryGetValue(name, out string? s) && float.TryParse(s,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out string? s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None)
        {
            if (!_v.ContainsKey(name)) _v[name] = defaultValue;
        }
    }

    private static void ClearHooks()
    {
        VortexArena.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        VortexArena.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        VortexArena.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();
    }

    private static float Speed2D(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
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

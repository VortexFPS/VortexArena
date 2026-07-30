using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the PORT-EXTENSION step-up vertical-velocity limiter (<c>sv_step_upspeed_scale</c> /
/// <c>sv_step_upspeed_max</c>, applied by <c>PlayerPhysics.ApplyStepUpSpeedClamp</c>).
///
/// Stock Xonotic step-up is purely POSITIONAL: the slide-move lifts the hull up to <c>sv_stepheight</c> in one
/// tick and preserves velocity (the <c>stair_step_up</c> golden trace keeps <c>velocity.z == 0</c> when WALKING,
/// so these knobs never touch it). A player who JUMPS into a stair/bump keeps full upward velocity AND gets the
/// positional lift, so they "launch" to step+jump height. These tests drive a jump into the same 24u step world
/// the golden trace uses and assert the knobs scale / cap the carried upward velocity while leaving the
/// positional lift (stair traversal) intact — and that the stock defaults are a strict no-op.
/// </summary>
[Collection("GlobalState")]
public class StepUpSpeedTests
{
    public StepUpSpeedTests()
    {
        // hermetic vs leaked net-session statics / other tests' mutator chains
        MovementParameters.PredictionOverride = null;
        MovementParameters.PresetProvider = null;
    }

    // The stair_step_up golden world: a low floor (top z=0, x<=64) and a high step (top z=24, x>=64). The vertical
    // step face is at x=64; a player moving +x climbs the 24u step there.
    private static readonly (int contents, float[] planes)[] StepWorld =
    {
        (AnalyticWorld.ContSolid, new float[] { 1,0,0,64,  -1,0,0,4096, 0,1,0,4096, 0,-1,0,4096, 0,0,1,0,  0,0,-1,256 }),
        (AnalyticWorld.ContSolid, new float[] { 1,0,0,4096, -1,0,0,-64, 0,1,0,4096, 0,-1,0,4096, 0,0,1,24, 0,0,-1,256 }),
    };

    private readonly record struct StepRun(float PeakZ, Vector3 FinalOrigin, float VelZAtStep, bool Stepped);

    /// <summary>
    /// Launch the player in +x with upward velocity so the front edge meets the x=64 step while still rising,
    /// run 20 ticks, and report: the highest origin.z reached (the launch apex), the final origin, the upward
    /// velocity retained on the step-up tick, and whether a step-up was detected (a &gt;10u one-tick z jump).
    /// </summary>
    private static StepRun RunStepJump(string? scale, string? max)
    {
        IEngineServices? saved = Api.Services;
        try
        {
            MutatorHooks.PlayerJump.Clear();
            MutatorHooks.PlayerCanCrouch.Clear();
            MutatorHooks.PlayerPhysics.Clear();
            MovementParameters.PredictionOverride = null;
            MovementParameters.PresetProvider = null;

            var world = AnalyticWorld.FromPlanes(StepWorld);
            var clock = new MutableClock();
            var services = new StepTestServices(world, clock);
            if (scale is not null) services.Cvars.Set("sv_step_upspeed_scale", scale);
            if (max is not null) services.Cvars.Set("sv_step_upspeed_max", max);
            Api.Services = services;

            var physics = new PlayerPhysics();
            var player = new Entity
            {
                Origin = new Vector3(46f, 0f, 30f),     // bottom z=6 (cleanly airborne above the z=0 floor), front edge x=62 (< 64)
                Velocity = new Vector3(300f, 0f, 200f),  // running +x, rising fast — reaches the step near the top of the arc
                Angles = Vector3.Zero,
                Mins = new Vector3(-16f, -16f, -24f),
                Maxs = new Vector3(16f, 16f, 45f),
                Gravity = 1f,
                Flags = EntFlags.JumpReleased,           // airborne, jump already released (no re-jump this run)
            };
            var input = new MovementInput
            {
                ViewAngles = Vector3.Zero,
                MoveValues = new Vector3(300f, 0f, 0f),
                FrameTime = 1f / 32f,
            };

            float peakZ = player.Origin.Z;
            float prevZ = player.Origin.Z;
            float velZAtStep = 0f;
            bool stepped = false;
            for (int t = 0; t < 20; t++)
            {
                clock.Time += input.FrameTime;
                physics.Move(player, input);

                float dz = player.Origin.Z - prevZ;
                if (!stepped && dz > 10f) // the one-tick positional lift over the step face
                {
                    stepped = true;
                    velZAtStep = player.Velocity.Z;
                }
                prevZ = player.Origin.Z;
                if (player.Origin.Z > peakZ) peakZ = player.Origin.Z;
            }
            return new StepRun(peakZ, player.Origin, velZAtStep, stepped);
        }
        finally
        {
            Api.Services = saved!;
            MovementParameters.PredictionOverride = null;
            MovementParameters.PresetProvider = null;
        }
    }

    [Fact]
    public void Vanilla_RetainsUpwardVelocity_AndLaunchesAboveTheStep()
    {
        // Stock defaults (cvars unset → scale 1, max -1): the step-up is a no-op for the limiter, so the player
        // keeps full upward velocity through the positional lift and launches well above the z=48 step rest height.
        StepRun v = RunStepJump(scale: null, max: null);
        Assert.True(v.Stepped, "the jump should have triggered a step-up over the 24u face");
        Assert.True(v.VelZAtStep > 120f, $"vanilla should keep most upward velocity through the step (was {v.VelZAtStep:F1})");
        Assert.True(v.PeakZ > 55f, $"vanilla should launch above the step (origin.z on the step is ~48; peak was {v.PeakZ:F1})");
    }

    [Fact]
    public void Scale_Zero_StepsUpWithoutLaunching()
    {
        StepRun vanilla = RunStepJump(scale: null, max: null);
        StepRun killed = RunStepJump(scale: "0", max: null);

        // The positional step-up STILL happens (traversal preserved) — the player ends up on the high surface...
        Assert.True(killed.Stepped, "scale 0 must still let the player step up (only the launch velocity is removed)");
        Assert.True(killed.FinalOrigin.X > 64f, $"the player should have climbed onto the high step (final x {killed.FinalOrigin.X:F1})");
        // ...but the carried upward velocity is zeroed, so there is no launch above the ~48 rest height.
        Assert.True(killed.VelZAtStep <= 0.001f, $"scale 0 should zero the upward step velocity (was {killed.VelZAtStep:F3})");
        Assert.True(killed.PeakZ < vanilla.PeakZ - 8f,
            $"scale 0 should tame the launch (clamped peak {killed.PeakZ:F1} vs vanilla {vanilla.PeakZ:F1})");
        Assert.True(killed.PeakZ <= 49f, $"clamped player should settle on the step (~48), not launch (peak {killed.PeakZ:F1})");
    }

    [Fact]
    public void Max_CapsTheCarriedUpwardVelocity()
    {
        StepRun vanilla = RunStepJump(scale: null, max: null);
        StepRun capped = RunStepJump(scale: null, max: "50");

        Assert.True(capped.Stepped, "the capped run should still step up");
        Assert.InRange(capped.VelZAtStep, 49f, 51f);   // upward velocity hard-capped to ~50 u/s
        Assert.True(capped.VelZAtStep < vanilla.VelZAtStep, "the cap must reduce the retained upward velocity");
        Assert.True(capped.PeakZ < vanilla.PeakZ, $"the cap must lower the launch apex (capped {capped.PeakZ:F1} vs vanilla {vanilla.PeakZ:F1})");
    }

    [Fact]
    public void Cvars_ReadAndReplicateRoundTrip()
    {
        // FromCvars reads the new knobs with EXISTS-gated semantics: unset → (1, -1), a configured 0 honored,
        // and Capture()/FromValues() (the wire path) reproduce the same values for remote-client prediction.
        var world = new AnalyticWorld();
        var clock = new MutableClock();
        var services = new StepTestServices(world, clock);
        IEngineServices? saved = Api.Services;
        try
        {
            Api.Services = services;

            MovementParameters unset = MovementParameters.FromCvars();
            Assert.Equal(1f, unset.StepUpSpeedScale);
            Assert.Equal(-1f, unset.StepUpSpeedMax);

            services.Cvars.Set("sv_step_upspeed_scale", "0");   // a real configured 0 must survive (not snap to 1)
            services.Cvars.Set("sv_step_upspeed_max", "150");
            MovementParameters set = MovementParameters.FromCvars();
            Assert.Equal(0f, set.StepUpSpeedScale);
            Assert.Equal(150f, set.StepUpSpeedMax);

            // wire round-trip: server Capture -> client FromValues agrees with the live FromCvars read.
            MovementParameters wire = MovementParameters.FromValues(
                XonoticGodot.Net.MoveVarsBlock.Capture(services.Cvars));
            Assert.Equal(set.StepUpSpeedScale, wire.StepUpSpeedScale);
            Assert.Equal(set.StepUpSpeedMax, wire.StepUpSpeedMax);
        }
        finally
        {
            Api.Services = saved!;
        }
    }

    /// <summary>The analytic movement harness, but with a REAL settable cvar store (so the step-up knobs apply).</summary>
    private sealed class StepTestServices : IEngineServices
    {
        private readonly MovementTestServices _inner;
        public StepTestServices(AnalyticWorld world, MutableClock clock) => _inner = new MovementTestServices(world, clock);
        public ITraceService Trace => _inner.Trace;
        public IEntityService Entities => _inner.Entities;
        public ICvarService Cvars { get; } = new CvarService();
        public ISoundService Sound => _inner.Sound;
        public IModelService Models => _inner.Models;
        public IGameClock Clock => _inner.Clock;
    }
}

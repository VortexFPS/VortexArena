using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression pin for the WalkMove on-ground rule (the bunnyhop "crippled jump" bug, 2026-07-05).
///
/// Base (walk.qc:30-59 / DP SV_WalkMove) sets FL_ONGROUND exclusively at a genuine floor collision inside
/// FlyMove — which simultaneously clips velocity.z into the plane — or via the stepdown==2 path. The
/// DOWNTRACEONGROUND probe only PREVENTS CLEARING an already-set flag (clip |= 1); it never grants it.
/// That preserves the invariant "OnGround ⇒ velocity.z was ground-clipped", which Xonotic's ADDITIVE
/// PlayerJump (velocity_z += jumpvelocity, player.qc:481) depends on.
///
/// The port used to grant the flag from the downtrace: a falling player whose tick ended within 1u above
/// the floor became "on ground" at velocity.z ≈ -271, and the held-jump bunnyhop jump fired
/// 260 - 271 ≈ dead — eaten/stunted hops that self-healed after ~1s (the next proper collision landing),
/// felt worst when hopping up onto platforms (the stair-step settle lands exactly in the probe window).
/// </summary>
public class JumpGrantReproTests
{
    private readonly ITestOutputHelper _out;
    public JumpGrantReproTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 72f;

    private static AnalyticWorld FloorWorld()
    {
        // one solid slab, top surface at z=0, huge extents
        return AnalyticWorld.FromPlanes(new[]
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                 0f, 0f,  1f,     0f,   // top:    z <= 0
                 0f, 0f, -1f,    64f,   // bottom: z >= -64
                 1f, 0f,  0f,  4096f,
                -1f, 0f,  0f,  4096f,
                 0f, 1f,  0f,  4096f,
                 0f,-1f,  0f,  4096f,
            }),
        });
    }

    private static (PlayerPhysics physics, Entity player, MutableClock clock) Setup(float startHeightAboveFloor, float vz)
    {
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var clock = new MutableClock();
        Api.Services = new MovementTestServices(FloorWorld(), clock);

        var player = new Entity
        {
            // feet = origin.z - 24, so origin.z = 24 means standing on the floor top (z=0)
            Origin = new Vector3(0f, 0f, 24f + startHeightAboveFloor),
            Velocity = new Vector3(400f, 0f, vz), // bhop-like horizontal speed, falling
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            Gravity = 1f,
            Flags = EntFlags.None, // airborne, jump HELD (JumpReleased not set)
        };
        return (new PlayerPhysics(), player, clock);
    }

    private static MovementInput HoldJump() => new()
    {
        ViewAngles = Vector3.Zero,
        MoveValues = Vector3.Zero,
        FrameTime = Dt,
        ButtonJump = true, // bunnyhop: jump held through the landing
    };

    [Fact]
    public void WindowLanding_StaysAirborne_UntilRealCollision_ThenFullJump()
    {
        // Fall per tick at vz=-260: (260 + g*dt/2) * dt = 3.688u. Start 4.2u above the floor
        // -> tick 1 ends 0.512u above it: no collision, but inside the downtrace's 1u probe window.
        var (physics, player, clock) = Setup(startHeightAboveFloor: 4.2f, vz: -260f);

        clock.Time += Dt;
        physics.Move(player, HoldJump());
        _out.WriteLine($"tick1 (window):    onground={player.OnGround} vel.z={player.Velocity.Z:F2} z-above-floor={player.Origin.Z - 24f:F3}");

        // Base-faithful: the downtrace must NOT grant on-ground to a falling player — still airborne,
        // velocity untouched. (The buggy grant reported onground=True at vel.z ~ -271 here.)
        Assert.False(player.OnGround, "downtrace must not grant OnGround to an airborne player");
        Assert.True(player.Velocity.Z < -250f, $"still falling, un-clipped: {player.Velocity.Z:F2}");

        clock.Time += Dt;
        physics.Move(player, HoldJump());
        _out.WriteLine($"tick2 (collision): onground={player.OnGround} vel.z={player.Velocity.Z:F2}");

        // The real landing: FlyMove's floor collision sets on-ground AND clips velocity.z to the plane.
        Assert.True(player.OnGround, "the genuine collision landing sets OnGround");
        Assert.True(MathF.Abs(player.Velocity.Z) < 1f, $"landing clips vel.z: {player.Velocity.Z:F2}");

        clock.Time += Dt;
        physics.Move(player, HoldJump());
        _out.WriteLine($"tick3 (jump):      onground={player.OnGround} vel.z={player.Velocity.Z:F2}");

        // Held-jump bunnyhop fires from a clipped vel.z=0 -> full-strength hop (260 - one tick of gravity).
        Assert.True(player.Velocity.Z > 240f, $"full jump expected: vel.z={player.Velocity.Z:F2}");
    }

    [Fact]
    public void ProperCollisionLanding_ControlCase_FullJump()
    {
        // Start 2.0u above the floor: the tick's 3.688u fall CROSSES the surface -> genuine floor
        // collision inside FlyMove -> velocity clipped to the plane (vel.z = 0) + OnGround set.
        var (physics, player, clock) = Setup(startHeightAboveFloor: 2.0f, vz: -260f);

        clock.Time += Dt;
        physics.Move(player, HoldJump());
        _out.WriteLine($"tick1 (landing): onground={player.OnGround} vel.z={player.Velocity.Z:F2}");
        Assert.True(player.OnGround);
        Assert.True(MathF.Abs(player.Velocity.Z) < 1f, $"landing collision clips vel.z: {player.Velocity.Z}");

        clock.Time += Dt;
        physics.Move(player, HoldJump());
        _out.WriteLine($"tick2 (jump):    onground={player.OnGround} vel.z={player.Velocity.Z:F2}");

        // Full-strength hop: 260 - one tick of gravity ~= +248.9
        Assert.True(player.Velocity.Z > 240f, $"full jump expected: vel.z={player.Velocity.Z:F2}");
    }
}

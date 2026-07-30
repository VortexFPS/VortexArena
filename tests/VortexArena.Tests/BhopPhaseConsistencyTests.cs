// r16 rubberband hunt, probe 4: is bunnyhop speed retention TICK-PHASE dependent?
//
// Bryan reports "movement speed and jumping feel inconsistent" (rubberbandy) in live play, surviving the
// interp-window fix, the clock rate-slew, and a locked frame rate — so the suspect moved into the movement
// sim itself. The classic bug shape: when a held-jump landing's position within the fixed 1/72 tick varies
// (it always does — fall distances are continuous), some alignments spend MORE GROUND TICKS than others
// before the auto-hop fires, and every extra ground tick applies FRICTION — speed pumps down irregularly,
// hop rhythm wobbles. DP/Base fire the held jump in the same move that detects the landing (zero friction
// ticks, every hop identical).
//
// The probe: drop a fast-moving player (400 u/s horizontal, jump held) onto a flat slab from a sweep of
// start heights spanning two full ticks of fall distance (so the landing hits every sub-tick phase), run a
// fixed number of ticks, and measure (a) horizontal speed retention and (b) hop count. If retention/hops
// vary with the start-height PHASE, the landing path leaks friction ticks on some alignments — the
// deterministic conviction of the felt inconsistency.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

public class BhopPhaseConsistencyTests
{
    private readonly ITestOutputHelper _out;
    public BhopPhaseConsistencyTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 72f;

    private static AnalyticWorld FloorWorld()
    {
        return AnalyticWorld.FromPlanes(new[]
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                 0f, 0f,  1f,     0f,
                 0f, 0f, -1f,    64f,
                 1f, 0f,  0f,  8192f,
                -1f, 0f,  0f,  8192f,
                 0f, 1f,  0f,  8192f,
                 0f,-1f,  0f,  8192f,
            }),
        });
    }

    private static (PlayerPhysics physics, Entity player) Setup(float startHeightAboveFloor)
    {
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        Api.Services = new MovementTestServices(FloorWorld(), new MutableClock());

        var player = new Entity
        {
            Origin = new Vector3(0f, 0f, 24f + startHeightAboveFloor),
            Velocity = new Vector3(400f, 0f, -200f), // fast bhop carry, already falling
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            Gravity = 1f,
            Flags = EntFlags.None, // airborne, jump held (JumpReleased not set)
        };
        return (new PlayerPhysics(), player);
    }

    private static MovementInput HoldJump() => new()
    {
        ViewAngles = Vector3.Zero,
        MoveValues = Vector3.Zero, // no wish-move: horizontal speed should be CONSERVED except friction ticks
        FrameTime = Dt,
        ButtonJump = true,
    };

    [Fact]
    public void HeldJumpBhop_SpeedRetentionAndHopCount_AreTickPhaseIndependent()
    {
        // Two ticks of fall at ~200-300 u/s ≈ 5.5-8.3u — sweep 0..8u of extra height in 24 steps so the
        // landing instant covers every sub-tick phase at least twice.
        const int phases = 24;
        const int ticks = 288; // 4 seconds — ~10+ hops
        var results = new List<(float h, float speed, int hops)>();

        for (int p = 0; p < phases; p++)
        {
            float h = 0.5f + 8f * p / phases;
            (PlayerPhysics physics, Entity player) = Setup(h);

            int hops = 0;
            float prevVz = player.Velocity.Z;
            for (int t = 0; t < ticks; t++)
            {
                physics.Move(player, HoldJump());
                // count upward launches (vz transitions from <=0 to strongly positive = a hop fired)
                if (prevVz <= 0f && player.Velocity.Z > 100f) hops++;
                prevVz = player.Velocity.Z;
            }

            float hspeed = new Vector2(player.Velocity.X, player.Velocity.Y).Length();
            results.Add((h, hspeed, hops));
            _out.WriteLine($"phase {p,2}  h={h:F2}u  end hspeed={hspeed,7:F2}  hops={hops}");
        }

        float min = results.Min(r => r.speed);
        float max = results.Max(r => r.speed);
        int minHops = results.Min(r => r.hops);
        int maxHops = results.Max(r => r.hops);
        _out.WriteLine($"speed spread: {min:F2}..{max:F2} ({max - min:F2})  hops: {minHops}..{maxHops}");

        // A phase-independent bhop keeps horizontal speed essentially identical across every landing
        // alignment. Allow a small numeric tolerance; a friction-tick leak shows up as tens of units of
        // spread (one ground tick at 400 u/s ≈ 400*friction*dt ≈ 22+ u lost, compounding per hop).
        Assert.True(max - min < 5f,
            $"bhop speed retention is tick-phase DEPENDENT: spread {max - min:F2} u/s across landing phases " +
            $"({min:F2}..{max:F2}) — friction ticks are leaking on some landing alignments");
        Assert.True(maxHops - minHops <= 1,
            $"hop count varies with landing phase: {minHops}..{maxHops}");
    }
}

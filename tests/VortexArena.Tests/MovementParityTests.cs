using System.Numerics;
using System.Text.Json;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// MOVEMENT-PARITY / GOLDEN-TRACE tests (REMAINING-WORK §6: "Golden-trace corpus from Darkplaces" +
/// "Movement-parity tests"). Each fixture in <c>tests/XonoticGodot.Tests/golden/*.json</c> was produced by
/// <c>tools/movement-ref/movement_ref.c</c> — an INDEPENDENT C reference transcribed line-for-line from the
/// preprocessed Xonotic QuakeC the Darkplaces engine runs (<c>.tmp/server.txt</c>). We replay each fixture's
/// exact per-tick input sequence through the ported <see cref="PlayerPhysics"/> and assert the trajectory
/// matches the reference to within transcendental-ULP tolerance.
///
/// Because the analytic collision world is shared bit-for-bit (the trace in <see cref="AnalyticWorld"/> is a
/// twin of the C trace), a divergence here is a pure physics-math difference between the port and the QC —
/// the movement-fidelity guard the task asked for ("never A/B'd against actual Darkplaces traces").
/// </summary>
public class MovementParityTests
{
    private readonly ITestOutputHelper _out;
    public MovementParityTests(ITestOutputHelper o) => _out = o;

    // Per-tick acceptance tolerances. The two implementations differ only in the last-ULP behaviour of
    // libm vs .NET MathF transcendentals (sin/cos/pow/log/atan2), which over a scenario accumulates to a
    // small bounded drift; a real port bug diverges by whole units immediately. Kept tight so it stays a
    // real guard.
    private const float PosTol = 0.20f;   // quake units
    private const float VelTol = 0.40f;   // quake units / second

    public static IEnumerable<object[]> Scenarios() => new[]
    {
        new object[] { "ground_accel_forward" },
        new object[] { "ground_friction_stop" },
        new object[] { "forward_jump_arc" },
        new object[] { "strafe_jump_air" },
        new object[] { "bunnyhop_chain" },
        new object[] { "air_control_turn" },
        new object[] { "free_fall" },
        new object[] { "ramp_run_up" },
        new object[] { "stair_step_up" },
        new object[] { "swim_forward" },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Port_Matches_DarkplacesReference(string name)
    {
        GoldenTrace g = LoadGolden(name);

        // The C reference runs the bare physics with no mutators. Other tests in the suite may have left
        // mutator handlers (multijump/bloodloss/…) on these global chains, which the jump/crouch paths call;
        // clear them so the parity sim is hermetic regardless of test order.
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        var world = AnalyticWorld.FromPlanes(g.World);
        var clock = new MutableClock();
        Api.Services = new MovementTestServices(world, clock);
        var physics = new PlayerPhysics();

        var player = new Entity
        {
            Origin = g.Start.Origin,
            Velocity = g.Start.Velocity,
            Angles = new Vector3(0f, g.Start.VAngle.Y, 0f),
            Mins = g.Hull.Mins,
            Maxs = g.Hull.Maxs,
            Gravity = 1f,                       // ent gravity factor 1 == reference's (gravity?gravity:1)
            Flags = DecodeFlags(g.Start.Flags),
        };

        float worstPos = 0f, worstVel = 0f;
        int worstTick = -1;
        for (int t = 0; t < g.Ticks.Count; t++)
        {
            GoldenTick tick = g.Ticks[t];
            clock.Time += tick.In.Dt;           // engine advances `time` before player physics

            var input = new MovementInput
            {
                ViewAngles = tick.In.Ang,
                MoveValues = tick.In.Move,
                FrameTime = tick.In.Dt,
                ButtonJump = tick.In.Jump != 0,
                ButtonCrouch = tick.In.Crouch != 0,
            };

            physics.Move(player, input);

            float posErr = (player.Origin - tick.Out.Origin).Length();
            float velErr = (player.Velocity - tick.Out.Velocity).Length();
            if (posErr > worstPos) { worstPos = posErr; worstTick = t; }
            if (velErr > worstVel) worstVel = velErr;

            int ong = player.OnGround ? 1 : 0;
            Assert.True(ong == tick.Out.OnGround,
                $"{name} tick {t}: onground {ong} != reference {tick.Out.OnGround} " +
                $"(origin {player.Origin}, vel {player.Velocity})");
            Assert.True(player.WaterLevel == tick.Out.WaterLevel,
                $"{name} tick {t}: waterlevel {player.WaterLevel} != reference {tick.Out.WaterLevel}");
        }

        _out.WriteLine($"{name}: {g.Ticks.Count} ticks, worst pos err {worstPos:F4} qu (tick {worstTick}), worst vel err {worstVel:F4} qu/s");
        Assert.True(worstPos <= PosTol, $"{name}: worst position error {worstPos:F4} qu at tick {worstTick} exceeds {PosTol} qu");
        Assert.True(worstVel <= VelTol, $"{name}: worst velocity error {worstVel:F4} qu/s exceeds {VelTol} qu/s");
    }

    private static EntFlags DecodeFlags(int cflags)
    {
        // C reference bitfield: ONGROUND=1, JUMPRELEASED=2, DUCKED=4, ONSLICK=8, WATERJUMP=16
        EntFlags f = EntFlags.None;
        if ((cflags & 1) != 0) f |= EntFlags.OnGround;
        if ((cflags & 2) != 0) f |= EntFlags.JumpReleased;
        if ((cflags & 16) != 0) f |= EntFlags.WaterJump;
        return f;
    }

    // ---- golden fixture loading ----

    private static GoldenTrace LoadGolden(string name)
    {
        string path = System.IO.Path.Combine(GoldenDir(), name + ".json");
        Assert.True(System.IO.File.Exists(path), $"golden fixture missing: {path} (run tools/movement-ref/movement_ref)");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        JsonElement r = doc.RootElement;

        var world = new List<(int, float[])>();
        foreach (JsonElement b in r.GetProperty("world").EnumerateArray())
        {
            int contents = b.GetProperty("contents").GetInt32();
            var flat = new List<float>();
            foreach (JsonElement pl in b.GetProperty("planes").EnumerateArray())
                foreach (JsonElement v in pl.EnumerateArray())
                    flat.Add(v.GetSingle());
            world.Add((contents, flat.ToArray()));
        }

        var hull = r.GetProperty("hull");
        var start = r.GetProperty("start");
        var ticks = new List<GoldenTick>();
        foreach (JsonElement tk in r.GetProperty("ticks").EnumerateArray())
        {
            JsonElement i = tk.GetProperty("in"), o = tk.GetProperty("out");
            ticks.Add(new GoldenTick(
                new GoldenInput(Vec3(i, "ang"), Vec3(i, "move"), i.GetProperty("jump").GetInt32(),
                    i.GetProperty("crouch").GetInt32(), i.GetProperty("dt").GetSingle()),
                new GoldenOutput(Vec3(o, "origin"), Vec3(o, "velocity"),
                    o.GetProperty("onground").GetInt32(), o.GetProperty("waterlevel").GetInt32())));
        }

        return new GoldenTrace(world,
            new GoldenHull(Vec3(hull, "mins"), Vec3(hull, "maxs")),
            new GoldenStart(Vec3(start, "origin"), Vec3(start, "velocity"), Vec3(start, "vangle"),
                start.GetProperty("flags").GetInt32()),
            ticks);
    }

    private static Vector3 Vec3(JsonElement e, string prop)
    {
        JsonElement a = e.GetProperty(prop);
        return new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "golden");

    private readonly record struct GoldenInput(Vector3 Ang, Vector3 Move, int Jump, int Crouch, float Dt);
    private readonly record struct GoldenOutput(Vector3 Origin, Vector3 Velocity, int OnGround, int WaterLevel);
    private readonly record struct GoldenTick(GoldenInput In, GoldenOutput Out);
    private readonly record struct GoldenHull(Vector3 Mins, Vector3 Maxs);
    private readonly record struct GoldenStart(Vector3 Origin, Vector3 Velocity, Vector3 VAngle, int Flags);
    private sealed record GoldenTrace(List<(int, float[])> World, GoldenHull Hull, GoldenStart Start, List<GoldenTick> Ticks);
}

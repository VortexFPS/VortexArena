using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// Cross-architecture / cross-run DETERMINISM validation (REMAINING-WORK §6 — "Cross-architecture determinism
/// validation"; ADR-0010). The deterministic simulation (movement, the deterministic PRNG, the Quake math) must
/// reproduce bit-identical results from identical inputs on a given architecture; across architectures only the
/// last ULP of the transcendentals may differ, and ADR-0010's prediction-error smoothing must absorb that.
///
/// These tests:
///  * pin same-run reproducibility (the sim is deterministic — no wall-clock / unordered / Random leaks),
///  * pin the canonical trace checksum + key float results against the recorded per-platform reference values
///    (a regression guard that tolerates documented libm last-ULP divergence — see <c>MovementTraceReferences</c>),
///  * prove a ULP-scale perturbation stays within the prediction-error envelope (the cross-play guarantee),
///  * forbid non-deterministic APIs in the simulation source.
/// See <c>planning/process/determinism.md</c>.
/// </summary>
public class DeterminismTests
{
    private readonly ITestOutputHelper _out;
    public DeterminismTests(ITestOutputHelper o) => _out = o;

    // ---- canonical deterministic movement trace -------------------------------------------------

    private static AnalyticWorld FlatWorld()
        => AnalyticWorld.FromPlanes(new (int, float[])[]
        {
            (AnalyticWorld.ContSolid, new float[]
            {
                1,0,0, 8192,  -1,0,0, 8192,  0,1,0, 8192,  0,-1,0, 8192,  0,0,1, 0,  0,0,-1, 256,
            }),
        });

    /// <summary>Run a fixed input sequence through the ported physics and fold the per-tick state into a hash.
    /// <paramref name="perturbVelX"/> nudges the initial x-velocity by N ULP (the cross-arch divergence proxy);
    /// the returned list is the per-tick origin for the envelope test.</summary>
    private static (ulong hash, List<Vector3> origins) RunCanonical(int perturbVelX = 0)
    {
        var world = FlatWorld();
        var clock = new MutableClock();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        float vx0 = 200f;
        for (int i = 0; i < perturbVelX; i++) vx0 = MathF.BitIncrement(vx0);

        var physics = new PlayerPhysics();
        var player = new Entity
        {
            Origin = new Vector3(0, 0, 24.125f),
            Velocity = new Vector3(vx0, 0, 0),
            Mins = new Vector3(-16, -16, -24),
            Maxs = new Vector3(16, 16, 45),
            Gravity = 1f,
            Flags = EntFlags.OnGround | EntFlags.JumpReleased,
        };

        const float dt = 1f / 32f;
        var hash = DeterminismHash.New();
        var origins = new List<Vector3>(64);
        for (int t = 0; t < 48; t++)
        {
            clock.Time += dt;
            float yaw = 0.5f * t; // a slow turn that exercises makevectors' sin/cos every tick
            var input = new MovementInput
            {
                ViewAngles = new Vector3(0, yaw, 0),
                MoveValues = new Vector3(360, 160, 0),
                FrameTime = dt,
                ButtonJump = t == 8, // one jump mid-run
            };
            physics.Move(player, input);
            hash.Add(player.Origin);
            hash.Add(player.Velocity);
            hash.Add(player.OnGround);
            origins.Add(player.Origin);
        }
        return (hash.Value, origins);
    }

    /// <summary>An AIRBORNE strafe run (no ground friction to damp divergence — the worst case for cross-arch
    /// drift), used by the envelope test. Returns the per-tick origins.</summary>
    private static List<Vector3> RunAirborneStrafe(int perturbVelX)
    {
        var world = AnalyticWorld.FromPlanes(System.Array.Empty<(int, float[])>()); // open air, no geometry
        var clock = new MutableClock();
        Api.Services = new MovementTestServices(world, clock);
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();

        float vx0 = 320f;
        for (int i = 0; i < perturbVelX; i++) vx0 = MathF.BitIncrement(vx0);

        var physics = new PlayerPhysics();
        var player = new Entity
        {
            Origin = new Vector3(0, 0, 1024),
            Velocity = new Vector3(vx0, 0, 0),
            Mins = new Vector3(-16, -16, -24),
            Maxs = new Vector3(16, 16, 45),
            Gravity = 1f,
            Flags = EntFlags.JumpReleased, // airborne
        };

        const float dt = 1f / 32f;
        var origins = new List<Vector3>(48);
        for (int t = 0; t < 32; t++)
        {
            clock.Time += dt;
            var input = new MovementInput
            {
                ViewAngles = new Vector3(0, 18f + 0.3f * t, 0),
                MoveValues = new Vector3(0, 400, 0), // pure strafe (the air-accel speed-gain path)
                FrameTime = dt,
            };
            physics.Move(player, input);
            origins.Add(player.Origin);
        }
        return origins;
    }

    [Fact]
    public void Movement_Trace_Is_Reproducible_Run_To_Run()
    {
        // Same inputs -> bit-identical trace. Catches any non-determinism that touches the movement path.
        ulong a = RunCanonical().hash;
        ulong b = RunCanonical().hash;
        Assert.Equal(a, b);
    }

    // The canonical checksums below fold raw transcendental (sin/cos) result bits from makevectors. Those bits
    // differ in the last ULP between platform libm implementations (Windows CRT vs Linux glibc vs Apple libm)
    // even on the same x64 architecture — the documented, error-compensation-absorbed cross-platform divergence
    // (ADR-0010; see the class summary and planning/process/determinism.md). So each pinned check accepts the
    // reference recorded for ANY known platform: a genuine numeric REGRESSION shifts the result on EVERY
    // platform at once (matching none of these → fails everywhere), while a mere libm last-ULP difference still
    // matches its own platform's pin. When a new platform/runtime first runs this, append its observed value
    // (printed by the test) — but ONLY after confirming via the envelope test + determinism.md that it is a
    // transcendental last-ULP difference, NOT a logic change. Do not blindly bump.
    private static readonly ulong[] MovementTraceReferences =
    {
        0xA169345D42F53C7DUL, // x64 / Windows CRT  (original dev-machine pin)
        0xDF2C2D60CEC26301UL, // x64 / Linux glibc  (GitHub ubuntu-latest)
    };
    private static readonly ulong[] QuakeMathReferences =
    {
        0x30D85D31A8B2C3EFUL, // x64 / Windows CRT  (original dev-machine pin)
        0xB23337E9D523E7E6UL, // x64 / Linux glibc  (GitHub ubuntu-latest)
    };

    [Fact]
    public void Movement_Trace_Checksum_Matches_X64_Reference()
    {
        // A mismatch against EVERY known-platform reference means EITHER an intended numeric change (re-pin)
        // OR a brand-new platform's cross-arch float divergence (evaluate against the envelope test below and
        // planning/process/determinism.md, then add the value to MovementTraceReferences — do NOT just bump).
        ulong actual = RunCanonical().hash;
        _out.WriteLine($"canonical movement checksum = 0x{actual:X16}");
        Assert.Contains(actual, MovementTraceReferences);
    }

    [Fact]
    public void Prandom_Seeded_Sequence_Is_Pinned()
    {
        // The deterministic PRNG (ADR-0010: server seeds it, the predicting client must reproduce it).
        Prandom.Seed(0x1234u);
        var hash = DeterminismHash.New();
        float first = 0f;
        for (int i = 0; i < 256; i++)
        {
            float f = Prandom.Float();
            if (i == 0) first = f;
            hash.Add(f);
        }
        _out.WriteLine($"prandom first=0x{BitConverter.SingleToUInt32Bits(first):X8} seqhash=0x{hash.Value:X16}");
        // Prandom is pure integer + a power-of-two scale, so these bits are exact on EVERY architecture/runtime.
        Assert.Equal(0x3F37947Eu, BitConverter.SingleToUInt32Bits(first)); // first value, exact bits
        Assert.Equal(0xC11A458C0DB9BF45UL, hash.Value);

        // Re-seeding reproduces the identical stream.
        Prandom.Seed(0x1234u);
        Assert.Equal(first, Prandom.Float());
    }

    [Fact]
    public void QuakeMath_Canonical_Results_Are_Pinned()
    {
        // makevectors / vectoangles over a sweep — the transcendental-bearing core. The exact bits differ by
        // platform libm (see QuakeMathReferences + the note above); a match against any known platform passes.
        var hash = DeterminismHash.New();
        for (int i = 0; i < 360; i += 7)
        {
            QMath.AngleVectors(new Vector3(i * 0.5f - 90f, i, i * 0.25f), out Vector3 f, out Vector3 r, out Vector3 u);
            hash.Add(f); hash.Add(r); hash.Add(u);
            hash.Add(QMath.VecToAngles(f));
        }
        _out.WriteLine($"quakemath checksum = 0x{hash.Value:X16}");
        Assert.Contains(hash.Value, QuakeMathReferences);
    }

    [Fact]
    public void UlpPerturbation_Stays_Within_PredictionEnvelope()
    {
        // Simulate a cross-architecture last-ULP difference by nudging the initial velocity by a few ULP, then
        // measure how far the trajectory drifts over a realistic client prediction window. ADR-0010 relies on
        // the netcode's error compensation (cl_movement_errorcompensation) to absorb residual divergence; a
        // jump/teleport is only inferred for deltas far larger than this. The drift over the window must stay
        // well under that — proving cross-play tolerates ULP-scale float divergence without a visible snap.
        const int window = 16;          // ~0.5s at this ticrate, longer than a typical client prediction window
        const float envelope = 2.0f;    // quake units; the smoothing/teleport threshold is far larger (>100u)

        // Ground movement (friction is a contraction map) — divergence is damped toward zero.
        List<Vector3> g0 = RunCanonical(perturbVelX: 0).origins;
        List<Vector3> g1 = RunCanonical(perturbVelX: 4).origins;
        float groundDrift = MaxDrift(g0, g1, window);

        // Airborne strafing (no friction — the worst case) — divergence can grow but stays bounded over the window.
        List<Vector3> a0 = RunAirborneStrafe(perturbVelX: 0);
        List<Vector3> a1 = RunAirborneStrafe(perturbVelX: 4);
        float airDrift = MaxDrift(a0, a1, window);

        _out.WriteLine($"max prediction-window drift from a 4-ULP velocity perturbation: ground={groundDrift:F6} qu, air={airDrift:F6} qu over {window} ticks");
        Assert.True(groundDrift < envelope, $"ground drift {groundDrift:F4} qu exceeds the {envelope} qu envelope");
        Assert.True(airDrift < envelope, $"airborne drift {airDrift:F4} qu exceeds the {envelope} qu envelope");
    }

    private static float MaxDrift(List<Vector3> a, List<Vector3> b, int window)
    {
        float m = 0f;
        for (int t = 0; t < window && t < a.Count && t < b.Count; t++)
            m = MathF.Max(m, (a[t] - b[t]).Length());
        return m;
    }

    [Fact]
    public void Simulation_Source_Has_No_NonDeterministic_Apis()
    {
        // Static guard: the deterministic sim must not read wall-clock time, use System.Random, or otherwise
        // pull in non-reproducible state. (Run-to-run reproducibility above catches anything on the exercised
        // path; this also catches it on cold paths.)
        string[] forbidden =
        {
            "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
            "Environment.TickCount", "Stopwatch", "Guid.NewGuid", "new Random(", "Random.Shared",
            "Math.FusedMultiplyAdd", "MathF.FusedMultiplyAdd",
        };
        string[] dirs =
        {
            RepoPath("src", "XonoticGodot.Common", "Physics"),
            RepoPath("src", "XonoticGodot.Common", "Math"),
            RepoPath("src", "XonoticGodot.Engine", "Simulation"),
        };
        var offenders = new List<string>();
        foreach (string dir in dirs)
        {
            if (!System.IO.Directory.Exists(dir)) continue;
            foreach (string file in System.IO.Directory.EnumerateFiles(dir, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                if (file.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar)) continue;
                string text = System.IO.File.ReadAllText(file);
                foreach (string bad in forbidden)
                    if (text.Contains(bad))
                        offenders.Add($"{System.IO.Path.GetFileName(file)}: {bad}");
            }
        }
        Assert.True(offenders.Count == 0, "non-deterministic API in the simulation source: " + string.Join(", ", offenders));
    }

    private static string RepoPath(params string[] parts)
    {
        // tests/XonoticGodot.Tests/<thisdir> -> repo root is two levels up.
        string testsDir = SourceDir();
        string root = System.IO.Directory.GetParent(System.IO.Directory.GetParent(testsDir)!.FullName)!.FullName;
        return System.IO.Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string f = "")
        => System.IO.Path.GetDirectoryName(f)!;
}

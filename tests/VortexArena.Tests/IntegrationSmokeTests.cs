using System.Numerics;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Md3;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Cross-project integration smoke tests: prove the foundation + the five ported libraries
/// (Common gameplay, Engine sim/collision, Net serialization, Assets parsers) wire together and
/// run, not just compile. These are the seed of the permanent test suite
/// (planning/process/testing-strategy.md).
/// </summary>
public class IntegrationSmokeTests
{
    // --- Framework + Gameplay: the registry discovers the ported content ---

    [Fact]
    public void Registries_Discover_PortedContent()
    {
        GameRegistries.Reset();
        GameRegistries.Bootstrap();

        Assert.NotNull(Weapons.ByName("blaster"));
        Assert.NotNull(Weapons.ByName("vortex"));
        Assert.NotNull(Weapons.ByName("machinegun"));
        // the Phase-2 weapon fan-out
        Assert.NotNull(Weapons.ByName("shotgun"));
        Assert.NotNull(Weapons.ByName("devastator"));
        Assert.NotNull(Weapons.ByName("mortar"));
        Assert.True(Weapons.Count >= 9, $"expected >=9 weapons, got {Weapons.Count}");
        Assert.True(Items.Count >= 3, $"expected several items, got {Items.Count}");
        Assert.NotNull(Mutators.ByName("vampire"));
        Assert.NotNull(GameTypes.ByName("dm"));   // Deathmatch

        // deterministic ordering: ids are sequential and follow ordinal name order
        for (int i = 0; i < Weapons.All.Count; i++)
            Assert.Equal(i, Weapons.All[i].RegistryId);
        for (int i = 1; i < Weapons.All.Count; i++)
            Assert.True(string.CompareOrdinal(Weapons.All[i - 1].RegistryName, Weapons.All[i].RegistryName) < 0);

        // content hash is stable across a re-bootstrap (client/server agreement guarantee)
        uint h1 = Weapons.Hash;
        GameRegistries.Reset();
        GameRegistries.Bootstrap();
        Assert.Equal(h1, Weapons.Hash);
    }

    [Fact]
    public void FullContent_Registers_AcrossAllSubsystems()
    {
        GameRegistries.Reset();
        GameRegistries.Bootstrap();      // attribute-based: weapons/items/mutators/gametypes/monsters/turrets/vehicles
        Effects.RegisterAll();           // self-registering catalogs
        Notifications.RegisterAll();
        Sounds.RegisterAll();
        Minigames.RegisterAll();

        Assert.True(Weapons.Count >= 19, $"weapons={Weapons.Count}");
        Assert.True(GameTypes.Count >= 18, $"gametypes={GameTypes.Count}");
        Assert.True(Mutators.Count >= 15, $"mutators={Mutators.Count}");
        Assert.True(Monsters.Count >= 5, $"monsters={Monsters.Count}");
        Assert.True(Turrets.Count >= 10, $"turrets={Turrets.Count}");
        Assert.True(Vehicles.Count >= 4, $"vehicles={Vehicles.Count}");
        Assert.True(Effects.Count >= 40, $"effects={Effects.Count}");
        Assert.True(Notifications.Count >= 50, $"notifications={Notifications.Count}");
        Assert.True(Sounds.Count >= 50, $"sounds={Sounds.Count}");
        Assert.True(Minigames.Count >= 4, $"minigames={Minigames.Count}");
    }

    // --- Math: AngleVectors is the makevectors fidelity port ---

    [Fact]
    public void QMath_AngleVectors_ZeroAngles_AreCanonicalBasis()
    {
        QMath.AngleVectors(Vector3.Zero, out var fwd, out var right, out var up);
        AssertVec(new Vector3(1, 0, 0), fwd);
        AssertVec(new Vector3(0, -1, 0), right);   // Quake's right is -Y at yaw 0
        AssertVec(new Vector3(0, 0, 1), up);

        // yaw 90° turns forward toward +Y
        QMath.AngleVectors(new Vector3(0, 90, 0), out var fwd90, out _, out _);
        AssertVec(new Vector3(0, 1, 0), fwd90, 1e-3f);
    }

    [Fact]
    public void QMath_VecToAngles_PitchIsInverseOfAngleVectors_ByDesign()
    {
        // vectoangles returns "up-positive" pitch (atan2(z, horiz)); makevectors uses "down-positive"
        // (forward.Z = -sin pitch). They are deliberately inverse on pitch — faithful to DarkPlaces, NOT a bug.
        // This pins the contract so a well-meaning "fix" that negates VecToAngles can't slip in unnoticed.
        QMath.AngleVectors(new Vector3(20f, 0f, 0f), out var fwd, out _, out _);
        Assert.True(fwd.Z < 0f, $"pitch +20 must aim forward down (-Z) per makevectors; got {fwd.Z}");

        Vector3 back = QMath.VecToAngles(fwd);
        Assert.True(System.MathF.Abs(back.X - 340f) < 0.01f, $"expected round-trip pitch 340 (=-20), got {back.X}");
        Assert.True(System.MathF.Abs(back.Y) < 0.01f, $"yaw should round-trip to 0, got {back.Y}");

        // FixedVecToAngles IS the makevectors-consistent inverse: re-vectoring its result reproduces the direction.
        // It negates the wrapped vectoangles pitch WITHOUT re-wrapping (faithful to Xonotic's fixedvectoangles macro),
        // so the raw value is -340, geometrically +20. The makevectors round-trip below is the real correctness check.
        Vector3 fixedAng = QMath.FixedVecToAngles(fwd);
        float pitchMod = ((fixedAng.X % 360f) + 360f) % 360f;   // -340 -> 20
        Assert.True(System.MathF.Abs(pitchMod - 20f) < 0.01f, $"fixedvectoangles pitch should be +20 (mod 360), got {fixedAng.X}");
        QMath.AngleVectors(fixedAng, out var fwd2, out _, out _);
        AssertVec(fwd, fwd2, 1e-3f);
    }

    // --- Net: quantized wire round-trips (com_msg.c parity) ---

    [Fact]
    public void Net_Vector_And_Angles_RoundTrip_WithinQuantization()
    {
        var w = new BitWriter();
        var pos = new Vector3(10.5f, -20.25f, 30.125f);   // multiples of 1/8 -> exact at Low (coord13i)
        var ang = new Vector3(0f, 90f, 45f);
        w.WriteVector(pos);
        w.WriteAngles(ang);

        var r = new BitReader(w.WrittenSpan);
        var pos2 = r.ReadVector();
        var ang2 = r.ReadAngles();

        AssertVec(pos, pos2, 0.13f);                       // 1/8-unit coord resolution
        Assert.True(System.Math.Abs(ang.Y - ang2.Y) < 1.5f); // 360/256 angle resolution
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-1000)]
    [InlineData(8388607)]    // +2^23 - 1
    [InlineData(-8388608)]   // -2^23
    public void Net_Int24_RoundTrips_Exactly(int value)
    {
        var w = new BitWriter();
        w.WriteInt24(value);
        var r = new BitReader(w.WrittenSpan);
        Assert.Equal(value, r.ReadInt24());
    }

    // --- Engine: the collision trace hits a brush (the fidelity-critical core) ---

    [Fact]
    public void Engine_Trace_Hits_SolidBrush()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-16, -16, -16), new Vector3(16, 16, 16), SuperContents.Solid));
        world.BuildGrid();
        var trace = new TraceService(world);

        var res = trace.Trace(
            start: new Vector3(-100, 0, 0), mins: Vector3.Zero, maxs: Vector3.Zero,
            end: new Vector3(100, 0, 0), filter: MoveFilter.Normal, ignore: null);

        Assert.True(res.Fraction < 1f, "expected the trace to hit the brush");
        Assert.InRange(res.EndPos.X, -16.2f, -15.8f);     // contact at the -X face
        Assert.True(res.PlaneNormal.X < -0.9f, "expected a -X surface normal");

        // a trace that misses the brush entirely returns Fraction == 1
        var miss = trace.Trace(new Vector3(-100, 100, 0), Vector3.Zero, Vector3.Zero,
                               new Vector3(100, 100, 0), MoveFilter.Normal, null);
        Assert.Equal(1f, miss.Fraction, 3);
    }

    // --- Assets: parsers validate magic and read a minimal header ---

    [Fact]
    public void Bsp_RejectsBadMagic_AndReadsMinimalHeader()
    {
        Assert.Throws<AssetParseException>(() => BspReader.Read(new byte[200])); // all-zero magic

        var bsp = BspReader.Read(MinimalIbspHeader());
        Assert.NotNull(bsp); // empty-but-valid map parses without throwing
    }

    [Fact]
    public void Md3_RejectsBadMagic()
    {
        Assert.Throws<AssetParseException>(() => Md3Reader.Read(new byte[200]));
    }

    // --- helpers ---

    private static byte[] MinimalIbspHeader()
    {
        // IBSP, version 46, 17 lumps each (offset=0, length=0). 4 + 4 + 17*8 = 144 bytes.
        var buf = new byte[144];
        buf[0] = (byte)'I'; buf[1] = (byte)'B'; buf[2] = (byte)'S'; buf[3] = (byte)'P';
        BitConverter.GetBytes(46).CopyTo(buf, 4);
        return buf;
    }

    private static void AssertVec(Vector3 expected, Vector3 actual, float tol = 1e-4f)
    {
        Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual} (tol {tol})");
    }
}

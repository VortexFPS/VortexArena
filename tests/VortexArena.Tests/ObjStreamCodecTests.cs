using System.Numerics;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// [objstream] Round-trips the two new entity-delta wire groups through <see cref="EntityStateCodec"/> — the
/// turret-head aim/spin block (<see cref="EntityField.TurretHead"/> = 1&lt;&lt;20) and the objective build/health
/// block (<see cref="EntityField.ObjState"/> = 1&lt;&lt;21). The wire is the contract this track must not break:
/// both groups are appended AFTER the existing wepent block (any new field MUST follow GunAlign or every later
/// field desyncs), so these tests are the desync guard for the append order.
///
/// <para>Both fields are zero/default on non-turret/non-objective entities — a plain player/item state diffs to a
/// mask WITHOUT either bit, so a normal entity costs nothing on the wire for these groups.</para>
/// </summary>
public class ObjStreamCodecTests
{
    /// <summary>A plain player baseline, defaulted exactly like the producer + the <see cref="NetEntityState.Empty"/>
    /// baseline so the wepent group (switch ids at the -1 sentinel) and the new turret/obj groups all stay clear.</summary>
    private static NetEntityState Player(int id, Vector3 origin, int health) => new()
    {
        EntNum = id, Kind = NetEntityKind.Player, ModelIndex = 7, Origin = origin, Health = health,
        SwitchWeapon = -1, SwitchingWeapon = -1,
    };

    /// <summary>8-bit angle quantization step (1.40625°). Pitch/yaw must survive within ~1.5° (one step).</summary>
    private const float AngleTolerance = 1.5f;

    [Fact]
    public void TurretHead_RoundTrips()
    {
        // A turret-class Generic entity whose head aim/active changed vs baseline. Head angles ride WriteAngles(Low)
        // (8-bit per axis, roll always 0); AVelYaw is a raw float; TurFlags is a byte (bit0 Active, bit1 Dead).
        var baseline = new NetEntityState { EntNum = 30, Kind = NetEntityKind.Generic, SwitchWeapon = -1, SwitchingWeapon = -1 };
        var cur = baseline;
        cur.TurHeadPitch = 12.5f;
        cur.TurHeadYaw = -47f;
        cur.TurHeadAVelYaw = 50f;
        cur.TurFlags = 1; // Active

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        Assert.Equal(EntityField.TurretHead, mask & EntityField.TurretHead);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);

        // Head pitch/yaw survive 8-bit angle quantization within ~1.5°.
        Assert.InRange(got.TurHeadPitch, 12.5f - AngleTolerance, 12.5f + AngleTolerance);
        Assert.InRange(got.TurHeadYaw, -47f - AngleTolerance, -47f + AngleTolerance);
        // AVelYaw is a raw float — exact.
        Assert.Equal(50f, got.TurHeadAVelYaw);
        // TurFlags is a byte — exact.
        Assert.Equal((byte)1, got.TurFlags);
    }

    [Fact]
    public void ObjState_RoundTrips()
    {
        // An objective entity (generator/CP-pad/build-icon) whose obj-health fraction or build state changed.
        var baseline = new NetEntityState { EntNum = 31, Kind = NetEntityKind.Generic, SwitchWeapon = -1, SwitchingWeapon = -1 };
        var cur = baseline;
        cur.ObjHealthByte = 128; // half health
        cur.ObjState = 1;        // building

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        Assert.Equal(EntityField.ObjState, mask & EntityField.ObjState);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);

        // Both bytes round-trip byte-exact.
        Assert.Equal((byte)128, got.ObjHealthByte);
        Assert.Equal((byte)1, got.ObjState);
    }

    [Fact]
    public void Idle_CostsNothing()
    {
        // A plain player/item state (turret/obj fields default) diffs to a mask WITHOUT TurretHead/ObjState — they
        // cost nothing on the wire until a producer sets them. Proven against the implicit spawn baseline.
        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, NetEntityState.Empty(10), Player(10, Vector3.Zero, 100));
        Assert.Equal(EntityField.None, mask & (EntityField.TurretHead | EntityField.ObjState));
    }

    [Fact]
    public void AppendOrder_AllSet()
    {
        // A single state changing wepent AND turret AND obj fields simultaneously must round-trip every field — this
        // guards the append order (turret + obj are written AFTER the wepent block; a misordered read desyncs).
        var baseline = new NetEntityState { EntNum = 32, Kind = NetEntityKind.Generic, SwitchWeapon = -1, SwitchingWeapon = -1 };
        var cur = baseline;
        // wepent block
        cur.SwitchWeapon = 5;
        cur.SwitchingWeapon = 4;
        cur.WepPhase = 2;
        cur.ViewmodelSkin = 1;
        cur.WepAlpha = 128;
        cur.GunAlign = 3;
        // turret block
        cur.TurHeadPitch = 12.5f;
        cur.TurHeadYaw = -47f;
        cur.TurHeadAVelYaw = 50f;
        cur.TurFlags = 3; // Active + Dead
        // obj block
        cur.ObjHealthByte = 200;
        cur.ObjState = 2; // built/captured

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, cur);
        Assert.Equal(EntityField.Wepent | EntityField.TurretHead | EntityField.ObjState,
            mask & (EntityField.Wepent | EntityField.TurretHead | EntityField.ObjState));

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);

        // wepent survives
        Assert.Equal(5, got.SwitchWeapon);
        Assert.Equal(4, got.SwitchingWeapon);
        Assert.Equal((byte)2, got.WepPhase);
        Assert.Equal((byte)1, got.ViewmodelSkin);
        Assert.Equal((byte)128, got.WepAlpha);
        Assert.Equal((byte)3, got.GunAlign);
        // turret survives (angles quantized, the rest exact)
        Assert.InRange(got.TurHeadPitch, 12.5f - AngleTolerance, 12.5f + AngleTolerance);
        Assert.InRange(got.TurHeadYaw, -47f - AngleTolerance, -47f + AngleTolerance);
        Assert.Equal(50f, got.TurHeadAVelYaw);
        Assert.Equal((byte)3, got.TurFlags);
        // obj survives
        Assert.Equal((byte)200, got.ObjHealthByte);
        Assert.Equal((byte)2, got.ObjState);
    }
}

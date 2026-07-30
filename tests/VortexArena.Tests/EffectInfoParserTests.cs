using System;
using System.Numerics;
using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Unit tests for the effect wire-protocol encoder (<see cref="EffectNetProtocol"/>) — the C# port of
/// <c>Net_Write_Effect</c> / the <c>net_effect</c> handler in <c>common/effects/all.qc</c>. Closes the
/// open <c>fx-effectinfo.net.wire_protocol</c> parity gap ("no encode/decode round-trip unit test").
///
/// SCOPE NOTE: the partner gap <c>fx-effectinfo.parser.tokenize</c> (the effectinfo.txt parser / keyword
/// table / baseline defaults) is intentionally NOT covered here. The parser (<c>EffectInfo</c> /
/// <c>EffectInfoEmitter</c> / <c>EiType</c>…) lives in the Godot client project (namespace
/// <c>XonoticGodot.Game.Client</c>, game/client/EffectInfo.cs), which this test assembly does not
/// reference (it references only the Godot-free src/ libraries — see XonoticGodot.Tests.csproj). Those
/// types are unreachable from here, so the original multi-fact proposal could not compile; this file is
/// scoped to the wire protocol, which lives in the referenced <c>XonoticGodot.Common</c> assembly.
///
/// Reference for the wire layout: <c>Net_Write_Effect</c> (origin, an extraflags byte, then conditional
/// velocity / colour-min / colour-max triples and — point effects only — the count byte). Colour is
/// quantised <c>rint(bound(0, 16*c, 255))</c> per component.
/// </summary>
public class EffectInfoParserTests
{
    // ========================================================================================
    //  Helper — build a request against a registered effect.
    // ========================================================================================

    /// <summary>Build an <see cref="EffectRequest"/> using the registered effect of the given EFFECT_* name.</summary>
    private static EffectRequest Req(string effectName, Vector3 origin, Vector3 velocity, int count,
        Vector3 colorMin = default, Vector3 colorMax = default)
    {
        Effects.RegisterAll(); // idempotent by name
        Effect? eff = Effects.ByName(effectName);
        Assert.NotNull(eff); // the named effect must exist in the registry for the test to be meaningful
        return new EffectRequest(eff, eff!.NetName, origin, velocity, count, colorMin, colorMax, except: null);
    }

    // ========================================================================================
    //  Point effect round-trip: [origin:12b][flags:1b][velocity:12b][count:1b]
    // ========================================================================================

    [Fact]
    public void Wire_PointEffect_RoundTrip_MatchesQcLayout()
    {
        // A simple point effect with velocity and no color override.
        var origin = new Vector3(100f, 200f, 300f);
        var velocity = new Vector3(10f, 0f, -5f);
        var req = Req("TE_EXPLOSION", origin, velocity, count: 3);

        byte[]? body = EffectNetProtocol.Encode(in req);
        Assert.NotNull(body);

        // Decode manually, mirroring the receiver's read order.
        int idx = 0;
        float ox = BitConverter.ToSingle(body!, idx); idx += 4;
        float oy = BitConverter.ToSingle(body, idx); idx += 4;
        float oz = BitConverter.ToSingle(body, idx); idx += 4;
        int flags = body[idx++];
        float vx = BitConverter.ToSingle(body, idx); idx += 4;
        float vy = BitConverter.ToSingle(body, idx); idx += 4;
        float vz = BitConverter.ToSingle(body, idx); idx += 4;
        int countByte = body[idx];

        Assert.Equal(EffectNetProtocol.NetVelocity, flags & EffectNetProtocol.NetVelocity);
        Assert.Equal(0, flags & EffectNetProtocol.NetColorMin); // no color override
        Assert.Equal(origin.X, ox, precision: 3);
        Assert.Equal(origin.Y, oy, precision: 3);
        Assert.Equal(origin.Z, oz, precision: 3);
        Assert.Equal(velocity.X, vx, precision: 3);
        Assert.Equal(velocity.Y, vy, precision: 3);
        Assert.Equal(velocity.Z, vz, precision: 3);
        Assert.Equal(3, countByte);

        // Full byte length: origin(12) + flags(1) + velocity(12) + count(1) = 26.
        Assert.Equal(26, body.Length);
    }

    [Fact]
    public void Wire_PointEffect_NoVelocity_OmitsVelocityBytes()
    {
        // With zero velocity the EFF_NET_VELOCITY flag is clear and no velocity triple is written:
        // origin(12) + flags(1) + count(1) = 14 bytes.
        var req = Req("TE_EXPLOSION", new Vector3(1f, 2f, 3f), Vector3.Zero, count: 5);
        byte[]? body = EffectNetProtocol.Encode(in req);
        Assert.NotNull(body);

        int flags = body![12];
        Assert.Equal(0, flags & EffectNetProtocol.NetVelocity);
        Assert.Equal(14, body.Length);
        Assert.Equal(5, body[13]); // count byte directly after the flags byte
    }

    // ========================================================================================
    //  Trail effect: NO count byte (Net_Write_Effect writes count only for point effects).
    // ========================================================================================

    [Fact]
    public void Wire_TrailEffect_HasNoCountByte()
    {
        // ARC_BEAM is registered isTrail: true. A trail emission carries the swept end-point in velocity
        // and omits the count byte entirely.
        Effects.RegisterAll();
        Effect? trailEff = Effects.ByName("ARC_BEAM");
        Assert.NotNull(trailEff);
        Assert.True(trailEff!.IsTrail);

        var req = new EffectRequest(trailEff, trailEff.NetName,
            new Vector3(0f, 0f, 0f), new Vector3(100f, 0f, 0f), count: 0,
            Vector3.Zero, Vector3.Zero, except: null);

        byte[]? body = EffectNetProtocol.Encode(in req);
        Assert.NotNull(body);

        // origin(12) + flags(1) + velocity(12) = 25 bytes; no trailing count byte.
        Assert.Equal(25, body!.Length);
        int flags = body[12];
        Assert.Equal(EffectNetProtocol.NetVelocity, flags & EffectNetProtocol.NetVelocity);
    }

    // ========================================================================================
    //  Colour optimisation: min == max sets EFF_NET_COLOR_SAME, not COLOR_MAX.
    // ========================================================================================

    [Fact]
    public void Wire_ColorSameOptimisation_SetsSameFlag()
    {
        // When colorMin == colorMax (both non-zero) the EFF_NET_COLOR_SAME flag is set and COLOR_MAX is NOT,
        // matching the QC min==max optimisation in Net_Write_Effect.
        var color = new Vector3(1f, 0.5f, 0f);
        var req = Req("TE_EXPLOSION", Vector3.Zero, Vector3.Zero, count: 1, color, color);
        int flags = EffectNetProtocol.ExtraFlags(in req);

        Assert.NotEqual(0, flags & EffectNetProtocol.NetColorMin);
        Assert.NotEqual(0, flags & EffectNetProtocol.NetColorSame);
        Assert.Equal(0, flags & EffectNetProtocol.NetColorMax); // same => max not written separately
    }

    [Fact]
    public void Wire_ColorDistinct_SetsColorMaxNotSame()
    {
        // Distinct min/max colours: COLOR_MIN and COLOR_MAX are set; COLOR_SAME is not.
        var min = new Vector3(1f, 0f, 0f);
        var max = new Vector3(0f, 0f, 1f);
        var req = Req("TE_EXPLOSION", Vector3.Zero, Vector3.Zero, count: 1, min, max);
        int flags = EffectNetProtocol.ExtraFlags(in req);

        Assert.NotEqual(0, flags & EffectNetProtocol.NetColorMin);
        Assert.NotEqual(0, flags & EffectNetProtocol.NetColorMax);
        Assert.Equal(0, flags & EffectNetProtocol.NetColorSame);
    }

    // ========================================================================================
    //  Colour quantisation: rint(bound(0, 16*c, 255)) per component.
    // ========================================================================================

    [Fact]
    public void Wire_ColorQuantize_MatchesQcFormula()
    {
        // DP: rint(bound(0, 16*c, 255)). So 1.0 => 16, 0.0 => 0, 0.5 => 8, and a component large enough
        // to exceed 255 (>= 15.9375) clamps to 255.
        Assert.Equal((byte)16,  EffectNetProtocol.QuantizeColor(1f));    // 16*1 = 16
        Assert.Equal((byte)0,   EffectNetProtocol.QuantizeColor(0f));
        Assert.Equal((byte)8,   EffectNetProtocol.QuantizeColor(0.5f));  // rint(16*0.5) = 8
        Assert.Equal((byte)255, EffectNetProtocol.QuantizeColor(20f));   // 16*20 = 320 -> clamped to 255
        Assert.Equal((byte)6,   EffectNetProtocol.QuantizeColor(0.375f)); // rint(16*0.375) = 6
    }

    [Fact]
    public void Wire_ColorMin_QuantizedBytesInBody()
    {
        // A colour-min override writes three quantised bytes after the flags (no velocity here, so they
        // sit right after the flags byte): origin(12) + flags(1) + colorMin(3) + count(1) = 17 bytes.
        var min = new Vector3(1f, 0.5f, 0f);
        var req = Req("TE_EXPLOSION", Vector3.Zero, Vector3.Zero, count: 1, min, min);
        byte[]? body = EffectNetProtocol.Encode(in req);
        Assert.NotNull(body);

        // min==max => only the COLOR_SAME min triple is written, no separate max triple.
        Assert.Equal(17, body!.Length);
        Assert.Equal((byte)16, body[13]); // 1.0  -> 16
        Assert.Equal((byte)8,  body[14]); // 0.5  -> 8
        Assert.Equal((byte)0,  body[15]); // 0.0  -> 0
        Assert.Equal(1, body[16]);        // count byte (point effect)
    }

    // ========================================================================================
    //  Guards: a point effect with count 0 is dropped; a null-Effect (engine-fallback) is dropped.
    // ========================================================================================

    [Fact]
    public void Wire_PointEffect_CountZero_ReturnsNull()
    {
        // A point effect with count 0 must return null (the same guard Send_Effect_Except applies).
        Effects.RegisterAll();
        Effect? eff = Effects.ByName("TE_EXPLOSION");
        Assert.NotNull(eff);
        var req = new EffectRequest(eff, eff!.NetName, Vector3.Zero, Vector3.Zero, count: 0,
            Vector3.Zero, Vector3.Zero, except: null);
        Assert.Null(EffectNetProtocol.Encode(in req));
    }

    [Fact]
    public void Wire_NullEffect_RegisteredEncode_ReturnsNull()
    {
        // Encode() is the registered-id path; an engine-fallback (by-name, null Effect) request is not
        // networkable through it and must return null.
        var req = new EffectRequest(null, "some_effectinfo_name", Vector3.Zero, Vector3.Zero, count: 1,
            Vector3.Zero, Vector3.Zero, except: null);
        Assert.Null(EffectNetProtocol.Encode(in req));
    }

    // ========================================================================================
    //  By-name (engine-fallback) path: EncodeByName carries a POINT payload, drops count-0 / registered.
    // ========================================================================================

    [Fact]
    public void Wire_ByName_EncodesPointPayload()
    {
        // The by-name path (no registered Effect) encodes a point-effect body: origin + flags
        // (+ velocity/colour as set) + count byte. Here: origin(12) + flags(1) + count(1) = 14.
        var req = new EffectRequest(null, "impact_metal", new Vector3(5f, 6f, 7f), Vector3.Zero, count: 4,
            Vector3.Zero, Vector3.Zero, except: null);
        byte[]? body = EffectNetProtocol.EncodeByName(in req);
        Assert.NotNull(body);
        Assert.Equal(14, body!.Length);
        Assert.Equal(4, body[13]); // count byte present (point effect)
    }

    [Fact]
    public void Wire_ByName_DropsCountZeroAndRegistered()
    {
        // A count-0 by-name point request is dropped (Send_Effect_Except guard).
        var zero = new EffectRequest(null, "impact_metal", Vector3.Zero, Vector3.Zero, count: 0,
            Vector3.Zero, Vector3.Zero, except: null);
        Assert.Null(EffectNetProtocol.EncodeByName(in zero));

        // A request that DOES carry a registered Effect is not the by-name path and is rejected by EncodeByName.
        Effects.RegisterAll();
        Effect? eff = Effects.ByName("TE_EXPLOSION");
        Assert.NotNull(eff);
        var registered = new EffectRequest(eff, eff!.NetName, Vector3.Zero, Vector3.Zero, count: 1,
            Vector3.Zero, Vector3.Zero, except: null);
        Assert.Null(EffectNetProtocol.EncodeByName(in registered));
    }
}

// playtest-bugs #80 (CTF flags drew at the wrong size) / #4 (the capture-shield scale): QC .scale reaches the
// client through the protocol-v23 EntityField.Scale byte, DarkPlaces' scale*16 encoding, and CTF sets Base's
// FLAG_SCALE on the flag.

using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Gameplay;
using VortexArena.Engine.Collision;
using VortexArena.Net;
using VortexArena.Server;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public sealed class EntityScaleWireTests
{
    [Theory]
    [InlineData(0f, 0)]        // unset → default, not sent
    [InlineData(1f, 0)]        // DP's 16 = the default
    [InlineData(0.5625f, 9)]   // Base FLAG_SCALE = 9/16
    [InlineData(0.6f, 9)]      // DP truncates: the old 0.6 flag scale was really 9/16 (sv_ctf.qh:49)
    [InlineData(0.5f, 8)]      // capture shield
    [InlineData(2f, 32)]
    [InlineData(100f, 255)]    // bounded to a byte
    [InlineData(0.01f, 1)]     // nonzero but under 1/16: the smallest drawable step
    public void EncodeScale_IsDarkPlacesByte(float scale, int expected)
        => Assert.Equal(expected, NetEntityState.EncodeScale(scale));

    [Fact]
    public void Scale_SurvivesDeltaRoundTrip_AndUnscaledCostsNothing()
    {
        var baseline = NetEntityState.Empty(40);
        var cur = baseline;
        cur.Kind = NetEntityKind.Generic;
        cur.Scale = NetEntityState.EncodeScale(Ctf.FlagScale);

        Assert.Equal(EntityField.Scale, NetEntityState.Diff(baseline, cur) & EntityField.Scale);
        var w = new BitWriter();
        EntityStateCodec.WriteDelta(w, baseline, cur);
        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.False(r.BadRead);
        Assert.Equal(0.5625f, NetEntityState.DecodeScale(got.Scale));

        // Back to scale 1 (a flag boarding a vehicle → VEHICLE_FLAG_SCALE): the bit is sent and decodes to unset.
        var back = got;
        back.Scale = NetEntityState.EncodeScale(1f);
        Assert.NotEqual(EntityField.None, NetEntityState.Diff(got, back) & EntityField.Scale);
        Assert.Equal(0f, NetEntityState.DecodeScale(back.Scale));

        // An unscaled entity never sets the bit.
        var plain = baseline;
        plain.Frame = 3;
        Assert.Equal(EntityField.None, NetEntityState.Diff(baseline, plain) & EntityField.Scale);
    }

    [Fact]
    public void CtfFlags_SpawnAtBaseFlagScale()
    {
        var ents = new List<EntityDict>
        {
            new("item_flag_team1", new Vector3(-736, -864, 128)),
            new("item_flag_team2", new Vector3(5152, -5024, 128)),
        };
        var world = new GameWorld(new CollisionWorld(), ents);
        world.Boot("ctf");
        var ctf = Assert.IsType<Ctf>(world.GameType);
        Assert.Equal(2, ctf.Flags.Count);
        foreach (var flag in ctf.Flags.Values)
        {
            Assert.NotNull(flag.Entity);
            Assert.Equal(0.5625f, flag.Entity!.ScaleFactor);
            Assert.Equal(9, NetEntityState.EncodeScale(flag.Entity.ScaleFactor)); // what ServerNet sends
        }
    }
}

using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards the owner-block weapon-HUD ring scalars (QC the networked wepent.* fields: vortex charge/chargepool,
/// clip load/size, hagar load/max, mine count/limit, arc heat). These ride the END of
/// <c>ServerNet.WriteOwnerState</c> and are read by <c>ClientNet.HandleSnapshot</c>; a single
/// <see cref="OwnerWeaponRings"/> Write/Read pair is the shared layout so the two halves can't drift.
///
/// <para>The regression these lock down: Wave 16 added the server write but NOT the client read, so the nine
/// unread floats desynced every block AFTER the owner block (movevars/scores/entities) for a real remote
/// client. The stream-alignment test below fails loudly if Write and Read ever disagree on the byte count.</para>
/// </summary>
public class OwnerWeaponRingsTests
{
    private static OwnerWeaponRings Sample => new()
    {
        VortexCharge = 0.25f,
        VortexChargePool = 0.5f,
        ClipLoad = 7f,
        ClipSize = 12f,
        HagarLoad = 3f,
        HagarLoadMax = 4f,
        MineCount = 2f,
        MineLimit = 3f,
        ArcHeat = 0.8f,
    };

    [Fact]
    public void RoundTrips_AllNineScalars()
    {
        var w = new BitWriter();
        Sample.Write(w);

        var r = new BitReader(w.WrittenSpan);
        OwnerWeaponRings got = OwnerWeaponRings.Read(ref r);

        Assert.False(r.BadRead);
        Assert.Equal(0.25f, got.VortexCharge);
        Assert.Equal(0.5f, got.VortexChargePool);
        Assert.Equal(7f, got.ClipLoad);
        Assert.Equal(12f, got.ClipSize);
        Assert.Equal(3f, got.HagarLoad);
        Assert.Equal(4f, got.HagarLoadMax);
        Assert.Equal(2f, got.MineCount);
        Assert.Equal(3f, got.MineLimit);
        Assert.Equal(0.8f, got.ArcHeat);
    }

    /// <summary>The desync guard: write a sentinel, the rings, then a second sentinel, and confirm the reader
    /// lands EXACTLY on the second sentinel after reading the rings — i.e. Read consumes precisely the bytes
    /// Write produced. If the two halves ever drift (a field added on one side only), the trailing sentinel
    /// mismatches and this fails, exactly as the live owner block would desync.</summary>
    [Fact]
    public void Read_ConsumesExactlyWhatWriteProduced_KeepingTheStreamAligned()
    {
        var w = new BitWriter();
        w.WriteUShort(0xBEEF);
        Sample.Write(w);
        w.WriteUShort(0xD00D);

        var r = new BitReader(w.WrittenSpan);
        Assert.Equal(0xBEEF, r.ReadUShort());
        OwnerWeaponRings got = OwnerWeaponRings.Read(ref r);
        Assert.Equal(0xD00D, r.ReadUShort()); // misaligned by even one float and this is garbage
        Assert.False(r.BadRead);
        Assert.Equal(Sample.ClipSize, got.ClipSize);
    }

    [Fact]
    public void None_MatchesCrosshairPanelSentinels()
    {
        OwnerWeaponRings none = OwnerWeaponRings.None;
        Assert.Equal(-1f, none.VortexCharge);
        Assert.Equal(-1f, none.VortexChargePool);
        Assert.Equal(-1f, none.ClipLoad);
        Assert.Equal(0f, none.ClipSize);   // QC weapon_clipsize gate: ring only drawn when > 0
        Assert.Equal(-1f, none.HagarLoad);
        Assert.Equal(4f, none.HagarLoadMax);
        Assert.Equal(-1f, none.MineCount);
        Assert.Equal(3f, none.MineLimit);
        Assert.Equal(-1f, none.ArcHeat);
    }
}

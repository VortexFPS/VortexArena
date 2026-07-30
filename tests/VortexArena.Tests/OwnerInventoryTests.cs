using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards the owner-block inventory tail (QC the ammo RES_* STATs + STAT_WEAPONS + IT_UNLIMITED_AMMO + the
/// STAT_ITEMS flag bits). It rides the END of <c>ServerNet.WriteOwnerState</c> and is read by
/// <c>ClientNet.HandleSnapshot</c>; a single <see cref="OwnerInventory"/> Write/Read pair is the shared layout so
/// the two halves can't drift.
///
/// <para>The regression this locks down is the same class as <see cref="OwnerWeaponRingsTests"/>: hand-appended
/// owner-block fields (the write and read maintained by hand in two files) desync every block AFTER the owner
/// block for a real remote client. The stream-alignment test below fails loudly if Write and Read ever disagree
/// on the byte count — and the 64-bit WepSet lo/hi split is exercised with a value that uses both halves.</para>
/// </summary>
public class OwnerInventoryTests
{
    private static OwnerInventory Sample => new()
    {
        Shells = 25f,
        Bullets = 180f,
        Rockets = 15f,
        Cells = 40f,
        Fuel = 33.5f,
        WeaponBits = 0xDEAD_BEEF_F00D_1234UL, // both 32-bit halves non-zero — exercises the lo/hi split
        UnlimitedAmmo = true,
        ItemFlags = 0x2A,
    };

    [Fact]
    public void RoundTrips_AllFields()
    {
        var w = new BitWriter();
        Sample.Write(w);

        var r = new BitReader(w.WrittenSpan);
        OwnerInventory got = OwnerInventory.Read(ref r);

        Assert.False(r.BadRead);
        Assert.Equal(25f, got.Shells);
        Assert.Equal(180f, got.Bullets);
        Assert.Equal(15f, got.Rockets);
        Assert.Equal(40f, got.Cells);
        Assert.Equal(33.5f, got.Fuel);
        Assert.Equal(0xDEAD_BEEF_F00D_1234UL, got.WeaponBits); // the 64-bit bitset survives the lo/hi round-trip
        Assert.True(got.UnlimitedAmmo);
        Assert.Equal(0x2A, got.ItemFlags);
    }

    /// <summary>The desync guard: write a sentinel, the inventory, then a second sentinel, and confirm the reader
    /// lands EXACTLY on the second sentinel — i.e. Read consumes precisely the bytes Write produced. If the two
    /// halves ever drift (a field added on one side only), the trailing sentinel mismatches and this fails,
    /// exactly as the live owner block would desync.</summary>
    [Fact]
    public void Read_ConsumesExactlyWhatWriteProduced_KeepingTheStreamAligned()
    {
        var w = new BitWriter();
        w.WriteUShort(0xBEEF);
        Sample.Write(w);
        w.WriteUShort(0xD00D);

        var r = new BitReader(w.WrittenSpan);
        Assert.Equal(0xBEEF, r.ReadUShort());
        OwnerInventory got = OwnerInventory.Read(ref r);
        Assert.Equal(0xD00D, r.ReadUShort()); // misaligned by even one field and this is garbage
        Assert.False(r.BadRead);
        Assert.Equal(Sample.WeaponBits, got.WeaponBits);
    }

    [Fact]
    public void None_IsAllZero()
    {
        OwnerInventory none = OwnerInventory.None;
        Assert.Equal(0f, none.Shells);
        Assert.Equal(0UL, none.WeaponBits);
        Assert.False(none.UnlimitedAmmo);
        Assert.Equal(0, none.ItemFlags);
    }
}

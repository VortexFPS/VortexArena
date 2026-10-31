using VortexArena.Common.Menu;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <see cref="InstalledMaps"/>: which installed files the map lists offer (DarkPlaces' <c>maps/*.bsp</c> plus
/// QC <c>MapInfo_ForbiddenFlags</c>).
/// </summary>
public class InstalledMapsTests
{
    [Theory]
    [InlineData("maps/stormkeep.bsp", "stormkeep")]
    [InlineData("MAPS/Stormkeep.BSP", "Stormkeep")]
    [InlineData("maps/space-elevator.bsp", "space-elevator")]
    [InlineData("maps/_hudsetup.bsp", "_hudsetup")]
    public void BspName_Takes_A_Bsp_Directly_Under_Maps(string vpath, string expected) =>
        Assert.Equal(expected, InstalledMaps.BspName(vpath));

    [Theory]
    // The old DarkPlaces boot stub: DP's wildcard does not cross '/', and `map _init` could not load it.
    [InlineData("maps/_init/_init.bsp")]
    [InlineData("maps/sub/foo.bsp")]
    [InlineData("maps/b_box.bsp")]        // compiler prefab box, not a level
    [InlineData("maps/stormkeep.mapinfo")]
    [InlineData("stormkeep.bsp")]
    [InlineData("maps/.bsp")]
    [InlineData("")]
    public void BspName_Rejects_What_Is_Not_A_Loadable_Map(string vpath) =>
        Assert.Null(InstalledMaps.BspName(vpath));

    [Fact]
    public void Forbidden_Is_Never_Offered()
    {
        var f = new InstalledMaps.Flags(Forbidden: true, Hidden: false, Frustrating: false);
        Assert.False(InstalledMaps.IsOffered(f, allowHidden: true, allowFrustrating: 1));
    }

    [Fact]
    public void Hidden_Is_Offered_Only_When_Allowed()
    {
        var f = new InstalledMaps.Flags(Forbidden: false, Hidden: true, Frustrating: false);
        Assert.False(InstalledMaps.IsOffered(f, allowHidden: false, allowFrustrating: 0));
        Assert.True(InstalledMaps.IsOffered(f, allowHidden: true, allowFrustrating: 0));
    }

    [Fact]
    public void Frustrating_Follows_G_Maplist_Allow_Frustrating()
    {
        var frustrating = new InstalledMaps.Flags(false, false, Frustrating: true);
        var plain = new InstalledMaps.Flags(false, false, false);

        Assert.False(InstalledMaps.IsOffered(frustrating, false, allowFrustrating: 0));
        Assert.True(InstalledMaps.IsOffered(frustrating, false, allowFrustrating: 1));
        Assert.True(InstalledMaps.IsOffered(plain, false, allowFrustrating: 1));
        // 2 = ONLY the frustrating ones (QC MapInfo_RequiredFlags).
        Assert.True(InstalledMaps.IsOffered(frustrating, false, allowFrustrating: 2));
        Assert.False(InstalledMaps.IsOffered(plain, false, allowFrustrating: 2));
    }

    [Fact]
    public void A_Map_With_No_Flags_Is_Offered() =>
        Assert.True(InstalledMaps.IsOffered(default, allowHidden: false, allowFrustrating: 0));
}

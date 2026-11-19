using System;
using VortexArena.Common.Menu;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <see cref="InstalledMaps"/>: which installed files the map lists offer (DarkPlaces' <c>maps/*.bsp</c> plus
/// QC <c>MapInfo_ForbiddenFlags</c>), and the <c>editor &lt;map&gt;</c> existence check.
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

    [Theory]
    [InlineData("stormkeep", "stormkeep")]
    [InlineData("stormkeep.bsp", "stormkeep")]
    [InlineData("maps/stormkeep.bsp", "stormkeep")]
    [InlineData(" maps\\stormkeep.bsp ", "stormkeep")]
    public void Stem_Strips_The_Maps_Prefix_And_Bsp_Extension(string typed, string expected) =>
        Assert.Equal(expected, InstalledMaps.Stem(typed));

    // ---- editor <map> ----

    private static readonly Func<string, bool> None = _ => false;

    [Fact]
    public void Editor_Opens_An_Installed_Map()
    {
        string? asked = null;
        Assert.Null(InstalledMaps.EditorOpenError("maps/stormkeep.bsp", s => { asked = s; return s == "stormkeep"; }, None));
        Assert.Equal("stormkeep", asked);
    }

    [Fact]
    public void Editor_Opens_A_Saved_Map_That_Has_No_Bsp()
    {
        // `editor_save mycopy` writes mycopy.vmap with no compiled map behind it; that is a real map to the editor.
        Assert.Null(InstalledMaps.EditorOpenError("mycopy", None, s => s == "mycopy"));
    }

    [Fact]
    public void Editor_Refuses_A_Name_That_Is_Neither_Installed_Nor_Saved()
    {
        string? error = InstalledMaps.EditorOpenError("stromkeep", None, None);
        Assert.NotNull(error);
        Assert.Contains("\"stromkeep\"", error);
        Assert.Contains("maps/stromkeep.bsp", error);
        Assert.Contains("lsmaps", error);
    }

    [Fact]
    public void Editor_Refuses_A_Nested_Path_Even_If_Something_Is_There()
    {
        // maps/_init/_init.bsp exists in an old install, but `_init/_init` is not a map name the host can open.
        string? error = InstalledMaps.EditorOpenError("_init/_init", _ => true, _ => true);
        Assert.NotNull(error);
        Assert.Contains("not a map name", error);
    }

    [Fact]
    public void Editor_Refuses_An_Empty_Name() =>
        Assert.NotNull(InstalledMaps.EditorOpenError("  ", _ => true, _ => true));
}

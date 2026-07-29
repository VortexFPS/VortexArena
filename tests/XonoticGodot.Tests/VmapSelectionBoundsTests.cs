using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers <see cref="VmapEdit.TryGetSelectionBounds"/> — the extent of a whole selection, brushes, patches
/// and entities together.
///
/// It exists because the editor's uniform-scale drag divides pointer travel by it. Reading only the brushes
/// was fine while brushes were the only scalable thing; once entity scaling landed, an entity-only selection
/// fell through to a 16-unit floor and the same gesture scaled it tens of times harder than a brush.
/// </summary>
public class VmapSelectionBoundsTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id)
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = "textures/test/wall",
            Projection = VmapTexProjection.AxialFor(n),
        });
        Face(new Vector3(1, 0, 0), maxs.X);
        Face(new Vector3(-1, 0, 0), -mins.X);
        Face(new Vector3(0, 1, 0), maxs.Y);
        Face(new Vector3(0, -1, 0), -mins.Y);
        Face(new Vector3(0, 0, 1), maxs.Z);
        Face(new Vector3(0, 0, -1), -mins.Z);
        return b;
    }

    private static VmapEntity Point(int id, string className, Vector3 origin)
    {
        var e = new VmapEntity { Id = id, ClassName = className };
        e.Fields["classname"] = className;
        e.SetOrigin(origin);
        return e;
    }

    [Fact]
    public void NothingSelectedHasNoBounds()
    {
        Assert.False(VmapEdit.TryGetSelectionBounds(
            new VmapDocument(), null, null, null, null, out _, out _));
    }

    [Fact]
    public void BrushesUnionRatherThanTakingTheLargest()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(16, 16, 16), 1));
        doc.Brushes.Add(Box(new Vector3(1000, 0, 0), new Vector3(1016, 16, 16), 2));

        Assert.True(VmapEdit.TryGetSelectionBounds(
            doc, new[] { 1, 2 }, null, null, null, out Vector3 mins, out Vector3 maxs));

        Assert.Equal(Vector3.Zero, mins);
        Assert.Equal(new Vector3(1016, 16, 16), maxs);
    }

    /// <summary>
    /// The regression: a point entity has no brush, so a brush-only reading returned nothing at all and the
    /// caller fell back to its floor.
    /// </summary>
    [Fact]
    public void APointEntityContributesItsDescriptorBox()
    {
        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "info_player_deathmatch", new Vector3(0, 0, 0)));
        doc.Entities.Add(Point(2, "info_player_deathmatch", new Vector3(512, 0, 0)));

        Assert.True(VmapEdit.TryGetSelectionBounds(
            doc, null, null, new[] { 1, 2 }, null, out Vector3 mins, out Vector3 maxs));

        // No EntityDefs supplied, so each falls back to the placeholder cube the pick index also uses.
        Assert.Equal(EntityClassDef.DefaultMins, mins);
        Assert.Equal(new Vector3(512, 0, 0) + EntityClassDef.DefaultMaxs, maxs);
        Assert.True((maxs - mins).Length() * 0.5f > 250f, "two spawns 512 apart is not a 16-unit selection");
    }

    /// <summary>The class descriptor is used when one is available, rather than the placeholder.</summary>
    [Fact]
    public void ADeclaredBoxBeatsThePlaceholder()
    {
        const string xml = """
            <?xml version="1.0"?>
            <classes>
            <point name="weapon_devastator" color="1 0 .5" box="-30 -30 0 30 30 48">a launcher</point>
            </classes>
            """;
        EntityDefs defs = EntityDefs.Parse(xml);

        var doc = new VmapDocument();
        doc.Entities.Add(Point(1, "weapon_devastator", Vector3.Zero));

        Assert.True(VmapEdit.TryGetSelectionBounds(
            doc, null, null, new[] { 1 }, defs, out Vector3 mins, out Vector3 maxs));

        Assert.Equal(new Vector3(-30, -30, 0), mins);
        Assert.Equal(new Vector3(30, 30, 48), maxs);
    }

    /// <summary>A brush entity has no origin — its extent is the geometry it owns.</summary>
    [Fact]
    public void ABrushEntityContributesTheGeometryItOwns()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-64, -64, 0), new Vector3(64, 64, 128), 1));
        var door = new VmapEntity { Id = 1, ClassName = "func_door" };
        door.Fields["classname"] = "func_door";
        door.BrushIds.Add(1);
        doc.Entities.Add(door);

        Assert.True(VmapEdit.TryGetSelectionBounds(
            doc, null, null, new[] { 1 }, null, out Vector3 mins, out Vector3 maxs));

        Assert.Equal(new Vector3(-64, -64, 0), mins);
        Assert.Equal(new Vector3(64, 64, 128), maxs);
    }

    [Fact]
    public void PatchesContributeTheirControlPoints()
    {
        var doc = new VmapDocument();
        var p = new VmapPatch { Id = 1, Width = 3, Height = 3, Material = "textures/test/curve" };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                p.Controls.Add(new Vector3((col - 1) * 128f, (row - 1) * 128f, 0f));
                p.ControlUvs.Add(new Vector2(col * 0.5f, row * 0.5f));
            }
        doc.Patches.Add(p);

        Assert.True(VmapEdit.TryGetSelectionBounds(
            doc, null, new[] { 1 }, null, null, out Vector3 mins, out Vector3 maxs));

        Assert.Equal(new Vector3(-128, -128, 0), mins);
        Assert.Equal(new Vector3(128, 128, 0), maxs);
    }

    [Fact]
    public void MixedSelectionsCoverEveryKind()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(16, 16, 16), 1));
        doc.Entities.Add(Point(1, "info_null", new Vector3(0, 0, 400)));

        Assert.True(VmapEdit.TryGetSelectionBounds(
            doc, new[] { 1 }, null, new[] { 1 }, null, out Vector3 mins, out Vector3 maxs));

        Assert.Equal(EntityClassDef.DefaultMins.X, mins.X);
        Assert.Equal(400f + EntityClassDef.DefaultMaxs.Z, maxs.Z);
    }

    [Fact]
    public void IdsThatResolveToNothingAreSkipped()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(Vector3.Zero, new Vector3(16, 16, 16), 1));

        Assert.True(VmapEdit.TryGetSelectionBounds(
            doc, new[] { 1, 999 }, new[] { 999 }, new[] { 999 }, null, out Vector3 mins, out Vector3 maxs));

        Assert.Equal(Vector3.Zero, mins);
        Assert.Equal(new Vector3(16, 16, 16), maxs);
    }
}

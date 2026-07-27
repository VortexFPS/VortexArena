using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers <see cref="VmapFaceCulling"/> — the rule that keeps the editor's regenerated world looking like the
/// compiled one instead of like the mapper's pile of overlapping solids.
///
/// The two failure directions matter equally and pull against each other. Culling too little leaves buried
/// masonry painted over the rooms (the bug this was written for). Culling too much opens holes in the level:
/// the dangerous case is brushes that merely TOUCH, because a shared seam looks a lot like containment to a
/// sloppy epsilon, and eroding it would crack every floor in the map along its brush boundaries.
/// </summary>
public class VmapFaceCullingTests
{
    private static int _nextId = 1;

    /// <summary>An axis-aligned box brush, all six sides drawable with the given material.</summary>
    private static VmapBrush Box(Vector3 min, Vector3 max, string material = "textures/test/wall", int contents = 1)
    {
        var brush = new VmapBrush { Id = _nextId++, ContentFlags = contents };
        void Side(Vector3 n, float d) => brush.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = material,
            Projection = VmapTexProjection.AxialFor(n),
        });

        Side(new Vector3(1, 0, 0), max.X);
        Side(new Vector3(-1, 0, 0), -min.X);
        Side(new Vector3(0, 1, 0), max.Y);
        Side(new Vector3(0, -1, 0), -min.Y);
        Side(new Vector3(0, 0, 1), max.Z);
        Side(new Vector3(0, 0, -1), -min.Z);
        return brush;
    }

    /// <summary>Total drawn area of one material across the document, which is what "is it visible" reduces to.</summary>
    private static double DrawnArea(VmapDocument doc, bool cull)
    {
        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(
            doc, new VmapSurfaceOptions { CullOccludedFaces = cull });

        double area = 0;
        foreach (VmapSurface s in surfaces)
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
            {
                Vector3 p0 = s.Positions[s.Indices[i]];
                Vector3 p1 = s.Positions[s.Indices[i + 1]];
                Vector3 p2 = s.Positions[s.Indices[i + 2]];
                area += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).Length();
            }
        return area;
    }

    [Fact]
    public void BrushInsideAnotherBrushDrawsNothing()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-128, -128, -128), new Vector3(128, 128, 128)));
        VmapBrush inner = Box(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        doc.Brushes.Add(inner);

        // The outer box's own six faces are 6 * 256^2; the swallowed inner box must contribute nothing.
        Assert.Equal(6 * 256.0 * 256.0, DrawnArea(doc, cull: true), 1);
    }

    [Fact]
    public void TouchingBrushesKeepTheirFullSharedSurface()
    {
        // Two floor slabs meeting at x = 0. Their top faces are coplanar and their side faces are flush: this
        // is the case that must NOT erode, or every multi-brush floor in every map develops cracks.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-128, -128, -16), new Vector3(0, 128, 0)));
        doc.Brushes.Add(Box(new Vector3(0, -128, -16), new Vector3(128, 128, 0)));

        double uncelled = DrawnArea(doc, cull: false);
        double culled = DrawnArea(doc, cull: true);

        // Exactly the two flush inner side faces (256 x 16 each) are hidden; nothing else may be lost.
        Assert.Equal(uncelled - 2 * 256.0 * 16.0, culled, 1);
    }

    [Fact]
    public void WallStandingOnFloorHidesTheContactPatchBothWays()
    {
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-128, -128, -16), new Vector3(128, 128, 0)));   // floor
        doc.Brushes.Add(Box(new Vector3(-32, -32, 0), new Vector3(32, 32, 64)));        // wall block on top

        double culled = DrawnArea(doc, cull: true);

        // The block's underside (64x64) is gone, and so is the 64x64 patch of floor it stands on.
        double floor = 2 * 256.0 * 256.0 + 4 * 256.0 * 16.0 - 64.0 * 64.0;
        double block = 2 * 64.0 * 64.0 + 4 * 64.0 * 64.0 - 64.0 * 64.0;
        Assert.Equal(floor + block, culled, 1);
    }

    [Fact]
    public void PartiallyOverlappedFaceKeepsItsExposedPart()
    {
        // A block covering the left half of a floor: the floor's top face must lose exactly that half.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-128, -128, -16), new Vector3(128, 128, 0)));
        doc.Brushes.Add(Box(new Vector3(-128, -128, 0), new Vector3(0, 128, 64)));

        double culled = DrawnArea(doc, cull: true);

        double floor = 2 * 256.0 * 256.0 + 4 * 256.0 * 16.0 - 128.0 * 256.0;
        double block = 2 * 128.0 * 256.0 + 2 * 128.0 * 64.0 + 2 * 256.0 * 64.0 - 128.0 * 256.0;
        Assert.Equal(floor + block, culled, 1);
    }

    [Fact]
    public void TranslucentBrushDoesNotHideWhatIsBehindIt()
    {
        const int Translucent = 0x2000_0000;
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-128, -128, -16), new Vector3(128, 128, 0)));
        doc.Brushes.Add(Box(new Vector3(-128, -128, 0), new Vector3(128, 128, 64),
            "textures/test/glass", contents: 1 | Translucent));

        // You can see through glass, so the floor it covers must still be drawn in full. The pane's own
        // underside is the one thing that goes: it is flush against opaque floor, so nothing can see it (and
        // drawing it would only z-fight with the floor).
        Assert.Equal(DrawnArea(doc, cull: false) - 256.0 * 256.0, DrawnArea(doc, cull: true), 1);
    }

    [Fact]
    public void HiddenSubmodelNeitherDrawsNorOccludes()
    {
        // A gametype-conditional func_wall sitting on the floor. Filtered out of the view, it must take its own
        // geometry with it AND give the floor underneath it back — a hole with no visible cause is worse than
        // the slab was.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-128, -128, -16), new Vector3(128, 128, 0)));
        VmapBrush conditional = Box(new Vector3(-32, -32, 0), new Vector3(32, 32, 64));
        conditional.SubmodelIndex = 3;
        doc.Brushes.Add(conditional);

        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc, new VmapSurfaceOptions
        {
            CullOccludedFaces = true,
            HiddenSubmodels = new HashSet<int> { 3 },
        });

        double area = 0;
        foreach (VmapSurface s in surfaces)
            for (int i = 0; i + 2 < s.Indices.Count; i += 3)
                area += 0.5 * Vector3.Cross(
                    s.Positions[s.Indices[i + 1]] - s.Positions[s.Indices[i]],
                    s.Positions[s.Indices[i + 2]] - s.Positions[s.Indices[i]]).Length();

        Assert.Equal(2 * 256.0 * 256.0 + 4 * 256.0 * 16.0, area, 1);
    }

    [Fact]
    public void CullingNeverInventsSurface()
    {
        // Whatever the arrangement, subtraction can only remove area. A regression that grew geometry would
        // mean the fragment decomposition had started overlapping itself.
        var doc = new VmapDocument();
        doc.Brushes.Add(Box(new Vector3(-128, -128, -16), new Vector3(128, 128, 0)));
        doc.Brushes.Add(Box(new Vector3(-64, -64, -8), new Vector3(64, 64, 32)));
        doc.Brushes.Add(Box(new Vector3(0, 0, -32), new Vector3(192, 192, 8)));

        Assert.True(DrawnArea(doc, cull: true) <= DrawnArea(doc, cull: false) + 1e-3);
    }
}

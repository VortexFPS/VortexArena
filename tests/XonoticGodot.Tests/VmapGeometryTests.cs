using System.Numerics;
using XonoticGodot.Formats.Vmap;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the brush CSG kernel (<see cref="VmapWinding"/>) and the surface generator
/// (<see cref="VmapGeometryBuilder"/>) — the foundation of the whole editable-geometry pipeline. A <c>.map</c>
/// import and every editor geometry edit carry ONLY planes, so if plane-set → polygon is wrong, nothing
/// downstream (render, collision, texturing) can be right.
/// </summary>
public class VmapGeometryTests
{
    /// <summary>An axis-aligned box brush spanning [mins, maxs], with outward face normals.</summary>
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, string material = "textures/test/wall", int id = 1)
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = material,
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

    [Fact]
    public void BoxBrush_EachFaceIsAQuadWithTheRightArea()
    {
        VmapBrush box = Box(new Vector3(-32, -32, -16), new Vector3(32, 32, 16));
        Vector3[][] windings = VmapWinding.BuildBrushWindings(box);

        Assert.Equal(6, windings.Length);
        foreach (Vector3[] w in windings)
            Assert.Equal(4, w.Length); // a box face is a quad, not a fan of slivers

        // +X face spans 64 (y) x 32 (z) = 2048 square units.
        Assert.Equal(2048f, PolygonArea(windings[0]), 1f);
        // +Z face spans 64 x 64 = 4096.
        Assert.Equal(4096f, PolygonArea(windings[4]), 1f);
    }

    [Fact]
    public void BoxBrush_WindingsAreCounterClockwiseSeenFromOutside()
    {
        VmapBrush box = Box(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        Vector3[][] windings = VmapWinding.BuildBrushWindings(box);

        // The surface contract: vertices run counter-clockwise seen from outside, so the polygon's own
        // right-hand normal must agree with its face plane normal. A flipped winding renders inside-out.
        for (int i = 0; i < windings.Length; i++)
        {
            Vector3[] w = windings[i];
            Vector3 geometric = Vector3.Cross(w[1] - w[0], w[2] - w[0]);
            geometric /= geometric.Length();
            Assert.True(Vector3.Dot(geometric, box.Faces[i].Plane.Normal) > 0.99f,
                $"face {i} winding is flipped relative to its plane normal");
        }
    }

    [Fact]
    public void BoxBrush_PointsAreTheEightCorners()
    {
        VmapBrush box = Box(new Vector3(-8, -8, -8), new Vector3(8, 8, 8));
        Vector3[] pts = VmapWinding.BrushPoints(box);
        Assert.Equal(8, pts.Length);

        Assert.True(VmapWinding.TryGetBounds(box, out Vector3 mins, out Vector3 maxs));
        Assert.Equal(new Vector3(-8, -8, -8), mins);
        Assert.Equal(new Vector3(8, 8, 8), maxs);
    }

    [Fact]
    public void CutCornerBrush_ProducesTheDiagonalFace()
    {
        // A box with one corner sliced off by a 45-degree plane: the classic non-axial brush.
        VmapBrush b = Box(new Vector3(0, 0, 0), new Vector3(64, 64, 64));
        Vector3 n = Vector3.Normalize(new Vector3(1, 1, 0));
        b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, Vector3.Dot(new Vector3(64, 32, 0), n)),
            Material = "textures/test/wall",
            Projection = VmapTexProjection.AxialFor(n),
        });

        Vector3[][] windings = VmapWinding.BuildBrushWindings(b);
        Vector3[] diagonal = windings[6];

        Assert.Equal(4, diagonal.Length);                       // the cut is a rectangle up the Z axis
        Assert.True(VmapWinding.IsClosedConvex(b));

        // Every vertex of the cut must lie ON the cutting plane.
        foreach (Vector3 p in diagonal)
            Assert.Equal(0f, b.Faces[6].Plane.Distance(p), 2);
    }

    [Fact]
    public void RedundantPlane_ContributesNoSurface_ButKeepsFaceIndexAlignment()
    {
        // A plane far outside the solid clips nothing and is clipped away entirely: it must yield an EMPTY
        // winding at its own index rather than shifting the other faces' indices.
        VmapBrush b = Box(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(new Vector3(1, 0, 0), 4096f),
            Material = "textures/test/unused",
            Projection = VmapTexProjection.AxialFor(new Vector3(1, 0, 0)),
        });

        Vector3[][] windings = VmapWinding.BuildBrushWindings(b);
        Assert.Equal(7, windings.Length);
        Assert.Empty(windings[6]);
        for (int i = 0; i < 6; i++)
            Assert.Equal(4, windings[i].Length);
    }

    [Fact]
    public void OpenPlaneSet_IsNotClosedConvex()
    {
        // Three planes cannot bound a volume, and the validity gate an editor drag must pass has to say so.
        var b = new VmapBrush { Id = 1 };
        b.Faces.Add(new VmapFace { Plane = new VmapPlane(new Vector3(1, 0, 0), 0f) });
        b.Faces.Add(new VmapFace { Plane = new VmapPlane(new Vector3(0, 1, 0), 0f) });
        b.Faces.Add(new VmapFace { Plane = new VmapPlane(new Vector3(0, 0, 1), 0f) });
        Assert.False(VmapWinding.IsClosedConvex(b));

        // An "inside out" box (mins/maxs swapped) bounds nothing either.
        VmapBrush inverted = Box(new Vector3(16, 16, 16), new Vector3(-16, -16, -16));
        Assert.False(VmapWinding.IsClosedConvex(inverted));
    }

    [Fact]
    public void UnboundedPlaneSet_IsRejectedEvenThoughEveryFaceYieldsAPolygon()
    {
        // The subtle case an editor drag can actually produce: five faces, EVERY one of them contributing a
        // real polygon, but the solid never closes because nothing bounds +X. Counting contributing faces
        // says "valid"; the giveaway is that those polygons run out to base-quad scale. Committing this would
        // hand the collision builder a brush with a 128k-unit AABB.
        VmapBrush b = Box(new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        b.Faces.RemoveAt(0);   // drop the +X plane, leaving the box open on that side

        Vector3[][] windings = VmapWinding.BuildBrushWindings(b);
        Assert.Equal(5, windings.Length);
        Assert.All(windings, w => Assert.True(w.Length >= 3));  // every face still looks fine on its own
        Assert.False(VmapWinding.IsClosedConvex(b));            // but the volume is open
    }

    [Fact]
    public void BuildSurfaces_GroupsByMaterial_AndTriangulates()
    {
        var doc = new VmapDocument();
        VmapBrush b = Box(new Vector3(-32, -32, -32), new Vector3(32, 32, 32));
        b.Faces[4].Material = "textures/test/floor";   // give one face its own material
        doc.Brushes.Add(b);

        IReadOnlyList<VmapSurface> surfaces = VmapGeometryBuilder.BuildSurfaces(doc);

        Assert.Equal(2, surfaces.Count);
        VmapSurface floor = surfaces.Single(s => s.Material == "textures/test/floor");
        VmapSurface wall = surfaces.Single(s => s.Material == "textures/test/wall");

        Assert.Equal(2, floor.TriangleCount);        // one quad
        Assert.Equal(10, wall.TriangleCount);        // five quads
        Assert.Equal(4, floor.VertexCount);
        Assert.All(floor.Normals, n => Assert.Equal(new Vector3(0, 0, 1), n));
    }

    [Fact]
    public void BuildSurfaces_SkipsNodrawAndSky()
    {
        var doc = new VmapDocument();
        VmapBrush b = Box(new Vector3(-32, -32, -32), new Vector3(32, 32, 32));
        b.Faces[0].SurfaceFlags = VmapGeometryBuilder.SurfaceNoDraw;
        b.Faces[1].SurfaceFlags = VmapGeometryBuilder.SurfaceSky;
        doc.Brushes.Add(b);

        // Default: nodraw and sky both dropped -> 4 quads remain.
        Assert.Equal(8, VmapGeometryBuilder.BuildSurfaces(doc).Sum(s => s.TriangleCount));

        // The editor's Base/wireframe modes ask for sky back (but never for nodraw) -> 5 quads.
        Assert.Equal(10, VmapGeometryBuilder.BuildSurfaces(doc, includeSky: true).Sum(s => s.TriangleCount));
    }

    [Fact]
    public void TextureProjection_IsStableUnderTranslationOfThePoint()
    {
        // The canonical form is u = dot(p, AxisU) + OffsetU, so moving a point one full repeat along the U axis
        // must advance u by exactly 1 — the property the BSP fit and the .map importer both have to satisfy.
        var proj = new VmapTexProjection(new Vector3(1f / 64f, 0, 0), new Vector3(0, 0, -1f / 64f), 0.25f, 0f);
        Vector2 a = proj.Evaluate(new Vector3(0, 0, 0));
        Vector2 b = proj.Evaluate(new Vector3(64, 0, 0));
        Assert.Equal(0.25f, a.X, 4);
        Assert.Equal(1.25f, b.X, 4);
        Assert.Equal(0f, b.Y, 4);
    }

    [Fact]
    public void AxialProjection_PicksTheDominantAxisFrame()
    {
        // A floor maps in XY; a wall facing +X maps in YZ. Both must produce a non-degenerate frame whose
        // axes lie IN the face plane (a component along the normal would skew the texture across the surface).
        foreach (Vector3 n in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            VmapTexProjection p = VmapTexProjection.AxialFor(n);
            Assert.True(p.IsValid);
            Assert.Equal(0f, Vector3.Dot(Vector3.Normalize(p.AxisU), n), 4);
            Assert.Equal(0f, Vector3.Dot(Vector3.Normalize(p.AxisV), n), 4);
        }
    }

    /// <summary>Area of a planar convex polygon (fan sum of triangle cross products).</summary>
    private static float PolygonArea(Vector3[] w)
    {
        float area = 0f;
        for (int i = 1; i + 1 < w.Length; i++)
            area += Vector3.Cross(w[i] - w[0], w[i + 1] - w[0]).Length() * 0.5f;
        return area;
    }
}

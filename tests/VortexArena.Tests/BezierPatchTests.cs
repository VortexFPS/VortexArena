using System;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — direct coverage for <see cref="BezierPatch"/> (the Q3 biquadratic patch tessellator —
/// previously only exercised indirectly through BspPatchCollisionTests). Pins: exact corner
/// interpolation, analytic quadratic-bezier midpoint evaluation, flat-patch coplanarity, vertex/index
/// counts vs subdivision level, multi-group (5-wide) grids, NaN-free degenerate handling, and the
/// malformed-grid rejections (even/undersized dimensions, vertex range out of bounds). Pure math —
/// always runs, no assets.
/// </summary>
public class BezierPatchTests
{
    private static BspVertex V(float x, float y, float z, float s = 0f, float t = 0f)
        => new(new Vector3(x, y, z), new Vector2(s, t), new Vector2(s * 0.5f, t * 0.5f), new Vector3(0, 0, 1), new BspColor(255, 255, 255, 255));

    private static BspFace PatchFace(int w, int h, int firstVertex = 0, int vertexCount = -1)
        => new(0, -1, BspFaceType.Patch, firstVertex, vertexCount < 0 ? w * h : vertexCount, 0, 0, -1, w, h);

    /// <summary>A flat 3x3 grid on z=0 spanning [0,2]x[0,2] (row-major, row = height direction).</summary>
    private static BspVertex[] FlatGrid3x3()
    {
        var v = new BspVertex[9];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            v[r * 3 + c] = V(c, r, 0, s: c / 2f, t: r / 2f);
        return v;
    }

    [Fact]
    public void FlatPatch_IsCoplanar_AndCornersInterpolateExactly()
    {
        var tess = BezierPatch.Tessellate(PatchFace(3, 3), FlatGrid3x3());

        Assert.NotNull(tess);
        Assert.False(tess!.IsEmpty);
        // every output vertex stays on the z=0 plane
        Assert.All(tess.Vertices, p => Assert.Equal(0f, p.Position.Z, 5));
        // corner control points are reproduced exactly at the parametric corners
        Vector3[] positions = tess.Vertices.Select(p => p.Position).ToArray();
        Assert.Contains(positions, p => (p - new Vector3(0, 0, 0)).Length() < 1e-5f);
        Assert.Contains(positions, p => (p - new Vector3(2, 0, 0)).Length() < 1e-5f);
        Assert.Contains(positions, p => (p - new Vector3(0, 2, 0)).Length() < 1e-5f);
        Assert.Contains(positions, p => (p - new Vector3(2, 2, 0)).Length() < 1e-5f);
    }

    [Fact]
    public void VertexAndTriangleCounts_MatchSubdivisionLevel()
    {
        const int steps = 4;
        var tess = BezierPatch.Tessellate(PatchFace(3, 3), FlatGrid3x3(), steps);

        Assert.NotNull(tess);
        Assert.Equal((steps + 1) * (steps + 1), tess!.Vertices.Count);   // one 3x3 group
        Assert.Equal(steps * steps * 2 * 3, tess.Indices.Count);         // 2 tris per cell
        Assert.All(tess.Indices, i => Assert.InRange(i, 0, tess.Vertices.Count - 1));
    }

    [Fact]
    public void MoreSubdivisions_ProduceMoreTriangles()
    {
        var coarse = BezierPatch.Tessellate(PatchFace(3, 3), FlatGrid3x3(), 4)!;
        var fine = BezierPatch.Tessellate(PatchFace(3, 3), FlatGrid3x3(), 8)!;
        Assert.True(fine.Indices.Count > coarse.Indices.Count);
    }

    [Fact]
    public void QuadraticArc_MidpointMatchesAnalyticBezier()
    {
        // Rows identical so V doesn't matter; along U the control polygon is P0=(0,0,0), P1=(1,0,1),
        // P2=(2,0,0). The quadratic Bernstein midpoint is 0.25*P0 + 0.5*P1 + 0.25*P2 = (1, 0, 0.5).
        var v = new BspVertex[9];
        for (int r = 0; r < 3; r++)
        {
            v[r * 3 + 0] = V(0, r, 0);
            v[r * 3 + 1] = V(1, r, 1);
            v[r * 3 + 2] = V(2, r, 0);
        }

        const int steps = 8;
        var tess = BezierPatch.Tessellate(PatchFace(3, 3), v, steps)!;

        // s = j/steps = 0.5 at j = steps/2; row i = 0 vertex index = 0 * (steps+1) + steps/2
        BezierPatch.PatchVertex mid = tess.Vertices[steps / 2];
        Assert.Equal(1f, mid.Position.X, 4);
        Assert.Equal(0.5f, mid.Position.Z, 4);
        // the arc apex never exceeds the analytic max height of 0.5
        Assert.All(tess.Vertices, p => Assert.True(p.Position.Z <= 0.5f + 1e-4f));
    }

    [Fact]
    public void TexCoords_AndLightmapCoords_InterpolateWithSameWeights()
    {
        const int steps = 8;
        var tess = BezierPatch.Tessellate(PatchFace(3, 3), FlatGrid3x3(), steps)!;

        // the grid's s/t run 0..1 linearly across control points, so the patch center has uv (0.5, 0.5)
        BezierPatch.PatchVertex center = tess.Vertices[(steps / 2) * (steps + 1) + steps / 2];
        Assert.Equal(0.5f, center.TexCoord.X, 4);
        Assert.Equal(0.5f, center.TexCoord.Y, 4);
        // lightmap uv was authored at half the texcoord — interpolation must keep the relationship
        Assert.Equal(0.25f, center.LightmapCoord.X, 4);
        Assert.Equal(0.25f, center.LightmapCoord.Y, 4);
    }

    [Fact]
    public void FiveWideGrid_TessellatesTwoSharedEdgeGroups()
    {
        // 5x3 control grid = two overlapping 3x3 groups sharing the middle column.
        var v = new BspVertex[15];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 5; c++)
            v[r * 5 + c] = V(c, r, 0);

        const int steps = 4;
        var tess = BezierPatch.Tessellate(PatchFace(5, 3), v, steps)!;

        Assert.Equal(2 * (steps + 1) * (steps + 1), tess.Vertices.Count);
        Assert.Equal(2 * steps * steps * 2 * 3, tess.Indices.Count);
        // both groups' far corners exist: x spans 0..4
        Assert.Contains(tess.Vertices, p => MathF.Abs(p.Position.X - 4f) < 1e-4f);
    }

    [Fact]
    public void DegenerateGrid_AllPointsCoincident_ProducesNoNaNs()
    {
        var v = Enumerable.Repeat(V(5, 5, 5), 9).ToArray();
        var tess = BezierPatch.Tessellate(PatchFace(3, 3), v);

        Assert.NotNull(tess);
        foreach (BezierPatch.PatchVertex p in tess!.Vertices)
        {
            Assert.False(float.IsNaN(p.Position.X) || float.IsNaN(p.Position.Y) || float.IsNaN(p.Position.Z));
            Assert.False(float.IsNaN(p.Normal.X) || float.IsNaN(p.Normal.Y) || float.IsNaN(p.Normal.Z));
            // the zero-normal fallback is the +Z unit vector
            Assert.Equal(1f, p.Normal.Length(), 3);
        }
    }

    [Fact]
    public void Normals_AreUnitLength()
    {
        var tess = BezierPatch.Tessellate(PatchFace(3, 3), FlatGrid3x3())!;
        Assert.All(tess.Vertices, p => Assert.Equal(1f, p.Normal.Length(), 3));
    }

    // ---------------------------------------------------------------- malformed grids -> null

    [Fact]
    public void EvenOrUndersizedDimensions_ReturnNull()
    {
        Assert.Null(BezierPatch.Tessellate(PatchFace(2, 3), FlatGrid3x3()));   // even width
        Assert.Null(BezierPatch.Tessellate(PatchFace(3, 4), FlatGrid3x3()));   // even height
        Assert.Null(BezierPatch.Tessellate(PatchFace(1, 3), FlatGrid3x3()));   // < 3
        Assert.Null(BezierPatch.Tessellate(PatchFace(3, 1), FlatGrid3x3()));
    }

    [Fact]
    public void VertexRangeOutOfBounds_ReturnsNull()
    {
        // declared 3x3 grid but the face's vertex range is short / outside the array
        Assert.Null(BezierPatch.Tessellate(PatchFace(3, 3, firstVertex: 0, vertexCount: 8), FlatGrid3x3()));
        Assert.Null(BezierPatch.Tessellate(PatchFace(3, 3, firstVertex: 5), FlatGrid3x3()));
        Assert.Null(BezierPatch.Tessellate(PatchFace(3, 3, firstVertex: -1), FlatGrid3x3()));
    }

    [Fact]
    public void SubdivisionsBelowOne_ClampToOneStep()
    {
        var tess = BezierPatch.Tessellate(PatchFace(3, 3), FlatGrid3x3(), 0);
        Assert.NotNull(tess);
        Assert.Equal(4, tess!.Vertices.Count);   // (1+1)^2
        Assert.Equal(6, tess.Indices.Count);     // 1 cell * 2 tris
    }
}

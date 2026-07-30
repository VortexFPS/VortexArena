using System.Numerics;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers <see cref="BspToVmap.TryFitProjection"/> — recovering a brush side's texture alignment from the
/// compiled surface it produced.
///
/// The case that matters is the MERGED draw surface. q3map2's meta pass welds coplanar surfaces sharing a
/// shader into one face, so a single compiled face routinely carries several texdefs — a trim strip turning a
/// corner is two alignments rotated 90° apart in one face. Fitting one affine map across the whole thing has
/// no solution, and accepting the least-squares answer anyway re-textures the wall with something that is
/// plausible, stable and wrong.
/// </summary>
public class BspProjectionRecoveryTests
{
    /// <summary>Build a BSP whose single face welds two quads with DIFFERENT alignments, on the plane x = 0.</summary>
    private static BspData TwoTexdefFace()
    {
        // Island A (y 0..64): u runs along Y, v along Z.   Island B (y 64..128): u runs along Z, v along Y.
        var verts = new List<BspVertex>();
        void V(float y, float z, float u, float v) => verts.Add(new BspVertex(
            new Vector3(0f, y, z), new Vector2(u, v), Vector2.Zero, new Vector3(-1f, 0f, 0f),
            new BspColor(255, 255, 255, 255)));

        V(0, 0, 0, 0);        V(64, 0, 2, 0);        V(64, 64, 2, -1);        V(0, 64, 0, -1);
        V(64, 0, 0, 0.5f);    V(128, 0, 0, 1.5f);    V(128, 64, 2, 1.5f);     V(64, 64, 2, 0.5f);

        var tris = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };

        return new BspData
        {
            Vertices = verts.ToArray(),
            Triangles = tris,
            Textures = new[] { new BspTexture("textures/test/trim", 0, 1) },
            Faces = new[]
            {
                new BspFace(0, -1, BspFaceType.Flat, FirstVertex: 0, VertexCount: 8,
                    FirstIndex: 0, IndexCount: 12, LightmapIndex: -1, PatchWidth: 0, PatchHeight: 0),
            },
        };
    }

    [Fact]
    public void MergedFaceWithTwoTexdefsIsNotFittedAsOne()
    {
        BspData bsp = TwoTexdefFace();

        // No winding: nothing says which half is being asked about, and no single map fits both — so the
        // honest answer is "cannot recover", not a blended average of the two alignments.
        Assert.False(BspToVmap.TryFitProjection(bsp, bsp.Faces[0], new Vector3(-1f, 0f, 0f), out _));
    }

    [Fact]
    public void FitRecoversTheAlignmentOfTheIslandTheSideCovers()
    {
        BspData bsp = TwoTexdefFace();
        var normal = new Vector3(-1f, 0f, 0f);

        // A brush side covering island A only.
        Vector3[] windingA =
        {
            new(0, 4, 4), new(0, 60, 4), new(0, 60, 60), new(0, 4, 60),
        };
        Assert.True(BspToVmap.TryFitProjection(bsp, bsp.Faces[0], normal, windingA, out VmapTexProjection a));

        // Island A maps u along Y at 2 texture widths per 64 units, v along Z at -1 per 64.
        Assert.Equal(2f / 64f, a.AxisU.Y, 4);
        Assert.Equal(0f, a.AxisU.Z, 4);
        Assert.Equal(-1f / 64f, a.AxisV.Z, 4);

        // A brush side covering island B only — the SAME compiled face, rotated alignment.
        Vector3[] windingB =
        {
            new(0, 68, 4), new(0, 124, 4), new(0, 124, 60), new(0, 68, 60),
        };
        Assert.True(BspToVmap.TryFitProjection(bsp, bsp.Faces[0], normal, windingB, out VmapTexProjection b));

        Assert.Equal(0f, b.AxisU.Y, 4);
        Assert.Equal(2f / 64f, b.AxisU.Z, 4);
        Assert.Equal(1f / 64f, b.AxisV.Y, 4);
    }

    [Fact]
    public void RecoveredProjectionReproducesTheCompiledUvs()
    {
        BspData bsp = TwoTexdefFace();
        Vector3[] winding = { new(0, 4, 4), new(0, 60, 4), new(0, 60, 60), new(0, 4, 60) };
        Assert.True(BspToVmap.TryFitProjection(
            bsp, bsp.Faces[0], new Vector3(-1f, 0f, 0f), winding, out VmapTexProjection proj));

        // Every vertex of the island it was fitted from must come back exactly — that is what "recovered"
        // means, and it is the check that distinguishes a real alignment from a convincing wrong one.
        for (int i = 0; i < 4; i++)
        {
            BspVertex v = bsp.Vertices[i];
            Assert.Equal(v.TexCoord.X, proj.Evaluate(v.Position).X, 3);
            Assert.Equal(v.TexCoord.Y, proj.Evaluate(v.Position).Y, 3);
        }
    }
}

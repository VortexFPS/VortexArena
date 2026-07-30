using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers <see cref="VmapTexAlign"/> and the surface ops (phase E8) — the Surface Inspector's maths.
///
/// A face's texture projection is an affine WORLD-to-UV map, not a texture-space nudge, and every operation
/// here is a transform of it. The recurring trap the tests below pin is the anchor: scaling or rotating a
/// texture without re-anchoring through a fixed world point also SLIDES it, so a mapper who scales a wall's
/// texture finds it has walked along the wall as well as resized.
/// </summary>
public class VmapTexAlignTests
{
    /// <summary>A 64x64 face on the +Z plane at z=0, with a default one-per-64-units projection.</summary>
    private static readonly Vector3[] Quad =
    {
        new(0, 0, 0), new(64, 0, 0), new(64, 64, 0), new(0, 64, 0),
    };

    private static VmapTexProjection Axial() => VmapTexProjection.AxialFor(new Vector3(0, 0, 1));

    // ---------------------------------------------------------------- shift

    [Fact]
    public void ShiftMovesTheImage_InTheDirectionAsked()
    {
        VmapTexProjection p = Axial();
        Vector2 before = p.Evaluate(Quad[0]);

        VmapTexProjection shifted = VmapTexAlign.Shift(p, 0.25f, 0f);
        Vector2 after = shifted.Evaluate(Quad[0]);

        // Shift is expressed in the direction the IMAGE moves, so the uv coordinate of a fixed world point
        // goes the other way. Getting this backwards makes every nudge in the inspector feel inverted.
        Assert.Equal(before.X - 0.25f, after.X, 4);
        Assert.Equal(before.Y, after.Y, 4);
    }

    [Fact]
    public void ShiftDoesNotChangeTheScale()
    {
        VmapTexProjection p = Axial();
        VmapTexProjection shifted = VmapTexAlign.Shift(p, 3f, -2f);
        Assert.Equal(p.AxisU, shifted.AxisU);
        Assert.Equal(p.AxisV, shifted.AxisV);
    }

    // ---------------------------------------------------------------- scale

    /// <summary>A bigger image means fewer repeats per world unit, so the axes shrink.</summary>
    [Fact]
    public void ScalingUpMakesTheTextureBigger()
    {
        VmapTexProjection p = Axial();
        VmapTexProjection scaled = VmapTexAlign.Scale(p, 2f, 2f, Vector3.Zero);

        Assert.Equal(p.AxisU.Length() / 2f, scaled.AxisU.Length(), 6);
        Assert.Equal(2f * 64f, 1f / scaled.AxisU.Length(), 3);   // one repeat now spans 128 units
    }

    /// <summary>The anchor stays put: scaling resizes the texture without walking it along the surface.</summary>
    [Fact]
    public void ScalingKeepsTheAnchorPointFixed()
    {
        VmapTexProjection p = VmapTexAlign.Shift(Axial(), 0.3f, -0.7f);
        var anchor = new Vector3(32, 32, 0);
        Vector2 before = p.Evaluate(anchor);

        Vector2 after = VmapTexAlign.Scale(p, 3f, 0.5f, anchor).Evaluate(anchor);

        Assert.Equal(before.X, after.X, 4);
        Assert.Equal(before.Y, after.Y, 4);
    }

    [Fact]
    public void ADegenerateScaleIsIgnored()
    {
        VmapTexProjection p = Axial();
        Assert.Equal(p.AxisU, VmapTexAlign.Scale(p, 0f, 1f, Vector3.Zero).AxisU);
    }

    // ---------------------------------------------------------------- rotate

    [Fact]
    public void RotatingSwapsTheAxesAtNinetyDegrees()
    {
        VmapTexProjection p = Axial();
        var normal = new Vector3(0, 0, 1);

        VmapTexProjection r = VmapTexAlign.Rotate(p, normal, 90f, Vector3.Zero);

        // The U axis turns into (what was) the V direction; lengths are preserved, so the scale is unchanged.
        Assert.Equal(p.AxisU.Length(), r.AxisU.Length(), 6);
        Assert.Equal(p.AxisV.Length(), r.AxisV.Length(), 6);
        Assert.True(MathF.Abs(Vector3.Dot(Vector3.Normalize(r.AxisU), Vector3.Normalize(p.AxisU))) < 1e-3f);
    }

    /// <summary>
    /// The axes must stay in the face's plane. Rotating about anything else shears the projection and the
    /// texture stops being a valid surface mapping.
    /// </summary>
    [Fact]
    public void RotatingKeepsTheAxesInTheFacePlane()
    {
        var normal = new Vector3(0, 0, 1);
        VmapTexProjection r = VmapTexAlign.Rotate(Axial(), normal, 37f, Vector3.Zero);

        Assert.Equal(0f, Vector3.Dot(r.AxisU, normal), 5);
        Assert.Equal(0f, Vector3.Dot(r.AxisV, normal), 5);
    }

    [Fact]
    public void RotatingKeepsTheAnchorPointFixed()
    {
        VmapTexProjection p = VmapTexAlign.Shift(Axial(), 1.1f, 2.2f);
        var anchor = new Vector3(16, 48, 0);
        Vector2 before = p.Evaluate(anchor);

        Vector2 after = VmapTexAlign.Rotate(p, new Vector3(0, 0, 1), 45f, anchor).Evaluate(anchor);

        Assert.Equal(before.X, after.X, 4);
        Assert.Equal(before.Y, after.Y, 4);
    }

    // ---------------------------------------------------------------- fit

    [Fact]
    public void FitPutsExactlyOneTileAcrossTheFace()
    {
        VmapTexProjection fitted = VmapTexAlign.Fit(Axial(), Quad);

        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (Vector3 v in Quad)
        {
            Vector2 uv = fitted.Evaluate(v);
            minU = MathF.Min(minU, uv.X);
            maxU = MathF.Max(maxU, uv.X);
            minV = MathF.Min(minV, uv.Y);
            maxV = MathF.Max(maxV, uv.Y);
        }

        Assert.Equal(0f, minU, 4);
        Assert.Equal(0f, minV, 4);
        Assert.Equal(1f, maxU, 4);
        Assert.Equal(1f, maxV, 4);
    }

    [Fact]
    public void FitHonoursARepeatCount()
    {
        VmapTexProjection fitted = VmapTexAlign.Fit(Axial(), Quad, repeatsU: 3f, repeatsV: 2f);

        float maxU = float.MinValue, maxV = float.MinValue;
        foreach (Vector3 v in Quad)
        {
            Vector2 uv = fitted.Evaluate(v);
            maxU = MathF.Max(maxU, uv.X);
            maxV = MathF.Max(maxV, uv.Y);
        }
        Assert.Equal(3f, maxU, 4);
        Assert.Equal(2f, maxV, 4);
    }

    /// <summary>Fit's value is that the result does not depend on where the face sits in the world.</summary>
    [Fact]
    public void FitIsIndependentOfWhereTheFaceIs()
    {
        var moved = new Vector3[Quad.Length];
        var offset = new Vector3(1234.5f, -6789f, 42f);
        for (int i = 0; i < Quad.Length; i++)
            moved[i] = Quad[i] + offset;

        VmapTexProjection a = VmapTexAlign.Fit(Axial(), Quad);
        VmapTexProjection b = VmapTexAlign.Fit(Axial(), moved);

        Assert.Equal(a.Evaluate(Quad[2]).X, b.Evaluate(moved[2]).X, 3);
        Assert.Equal(a.Evaluate(Quad[2]).Y, b.Evaluate(moved[2]).Y, 3);
    }

    [Fact]
    public void FitOnADegenerateWindingIsIgnored()
    {
        VmapTexProjection p = Axial();
        Assert.Equal(p.AxisU, VmapTexAlign.Fit(p, new[] { Vector3.Zero, Vector3.Zero, Vector3.Zero }).AxisU);
        Assert.Equal(p.AxisU, VmapTexAlign.Fit(p, System.Array.Empty<Vector3>()).AxisU);
    }

    // ---------------------------------------------------------------- natural / axial

    [Fact]
    public void NaturalResetsTheScaleButKeepsTheRotation()
    {
        VmapTexProjection rotated = VmapTexAlign.Rotate(Axial(), new Vector3(0, 0, 1), 30f, Vector3.Zero);
        VmapTexProjection stretched = VmapTexAlign.Scale(rotated, 7f, 0.2f, Vector3.Zero);

        VmapTexProjection natural = VmapTexAlign.Natural(stretched, unitsPerRepeat: 64f);

        // Scale is back to one repeat per 64 units on both axes...
        Assert.Equal(64f, 1f / natural.AxisU.Length(), 3);
        Assert.Equal(64f, 1f / natural.AxisV.Length(), 3);

        // ...and the direction the mapper rotated it to is preserved.
        Assert.True(Vector3.Dot(Vector3.Normalize(natural.AxisU), Vector3.Normalize(stretched.AxisU)) > 0.999f);
    }

    [Fact]
    public void AxialIsTheWorldAlignedReset()
    {
        VmapTexProjection a = VmapTexAlign.Axial(new Vector3(0, 0, 1));
        Assert.Equal(0f, a.OffsetU, 6);
        Assert.Equal(0f, a.OffsetV, 6);
        Assert.Equal(64f, 1f / a.AxisU.Length(), 3);
    }

    [Fact]
    public void ScaleReadoutIsWorldUnitsPerRepeat()
    {
        Vector2 s = VmapTexAlign.ScaleOf(Axial());
        Assert.Equal(64f, s.X, 3);
        Assert.Equal(64f, s.Y, 3);
    }

    // ---------------------------------------------------------------- the ops

    private static VmapDocument DocWithBox()
    {
        var b = new VmapBrush { Id = 1, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = "textures/test/wall",
            Projection = VmapTexProjection.AxialFor(n),
        });
        Face(new Vector3(1, 0, 0), 16);
        Face(new Vector3(-1, 0, 0), 16);
        Face(new Vector3(0, 1, 0), 16);
        Face(new Vector3(0, -1, 0), 16);
        Face(new Vector3(0, 0, 1), 16);
        Face(new Vector3(0, 0, -1), 16);

        var doc = new VmapDocument();
        doc.Brushes.Add(b);
        return doc;
    }

    [Fact]
    public void SetProjectionWritesIt_AndUndoRestoresTheOldAlignment()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        VmapTexProjection original = doc.Brushes[0].Faces[4].Projection;

        VmapTexProjection want = VmapTexAlign.Shift(original, 0.5f, 0.25f);
        Assert.True(session.Apply(new SetFaceProjectionOp(1, 4, want)));
        Assert.Equal(want.OffsetU, doc.Brushes[0].Faces[4].Projection.OffsetU, 5);

        Assert.True(session.Undo());
        Assert.Equal(original.OffsetU, doc.Brushes[0].Faces[4].Projection.OffsetU, 5);
    }

    [Fact]
    public void ADegenerateProjectionIsRefused()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(
            new SetFaceProjectionOp(1, 0, new VmapTexProjection(Vector3.Zero, Vector3.Zero, 0f, 0f))));
    }

    [Fact]
    public void AnOutOfRangeFaceIsRefused()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new SetFaceProjectionOp(1, 99, VmapTexProjection.AxialFor(Vector3.UnitZ))));
        Assert.False(session.Apply(new SetFaceProjectionOp(99, 0, VmapTexProjection.AxialFor(Vector3.UnitZ))));
    }

    [Fact]
    public void SettingFlagsWritesThem_AndUndoRestores()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SetFaceFlagsOp(1, 0, surfaceFlags: 0x0080, contentFlags: 0x10000)));
        Assert.Equal(0x0080, doc.Brushes[0].Faces[0].SurfaceFlags);

        Assert.True(session.Undo());
        Assert.Equal(0, doc.Brushes[0].Faces[0].SurfaceFlags);
    }

    [Fact]
    public void SettingTheSameFlagsAgain_JournalsNothing()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);
        Assert.False(session.Apply(new SetFaceFlagsOp(1, 0, 0, 0)));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void SetMaterialStillWorks_AndIsUndoable()
    {
        VmapDocument doc = DocWithBox();
        var session = new VmapEditSession(doc);

        Assert.True(session.Apply(new SetFaceMaterialOp(1, 2, "textures/exx/floor01")));
        Assert.Equal("textures/exx/floor01", doc.Brushes[0].Faces[2].Material);

        Assert.True(session.Undo());
        Assert.Equal("textures/test/wall", doc.Brushes[0].Faces[2].Material);
    }
}

using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Texture lock (backlog F7): the texture stays where the mapper put it when the geometry moves.
///
/// The invariant every test here checks is the same one, and it is worth stating once: for a point p on the
/// face and a geometric transform T, the texture coordinate of the MOVED point under the NEW projection must
/// equal the coordinate of the original point under the OLD one — <c>u'(T(p)) == u(p)</c>. Assert that and
/// the arithmetic cannot be quietly wrong in a way that only shows up as a wall whose texture crawls.
/// </summary>
public class VmapTextureLockTests
{
    private static VmapBrush Box(Vector3 mins, Vector3 maxs, int id = 1)
    {
        var b = new VmapBrush { Id = id, ContentFlags = 1 };
        void Face(Vector3 n, float d) => b.Faces.Add(new VmapFace
        {
            Plane = new VmapPlane(n, d),
            Material = "textures/test/wall",
            // Deliberately NOT axial and NOT origin-anchored: an offset of zero would hide a missing
            // pivot-correction term, and an axial projection would hide an axis that failed to rotate.
            Projection = new VmapTexProjection(
                new Vector3(0.02f, 0.005f, 0f), new Vector3(0f, -0.015625f, 0.003f), 3.25f, -7.5f),
        });
        Face(new Vector3(1, 0, 0), maxs.X);
        Face(new Vector3(-1, 0, 0), -mins.X);
        Face(new Vector3(0, 1, 0), maxs.Y);
        Face(new Vector3(0, -1, 0), -mins.Y);
        Face(new Vector3(0, 0, 1), maxs.Z);
        Face(new Vector3(0, 0, -1), -mins.Z);
        return b;
    }

    /// <summary>A handful of points on the brush, so the check is about the whole surface not one corner.</summary>
    private static Vector3[] Probes(Vector3 mins, Vector3 maxs) => new[]
    {
        mins,
        maxs,
        (mins + maxs) * 0.5f,
        new Vector3(mins.X, maxs.Y, (mins.Z + maxs.Z) * 0.5f),
        new Vector3(maxs.X, (mins.Y + maxs.Y) * 0.5f, mins.Z),
    };

    private static void AssertTextureStuck(
        VmapTexProjection before, VmapTexProjection after, Func<Vector3, Vector3> transform,
        Vector3 mins, Vector3 maxs)
    {
        foreach (Vector3 p in Probes(mins, maxs))
        {
            Vector2 was = before.Evaluate(p);
            Vector2 now = after.Evaluate(transform(p));
            Assert.True(MathF.Abs(was.X - now.X) < 1e-3f,
                $"U slid at {p}: {was.X} -> {now.X}");
            Assert.True(MathF.Abs(was.Y - now.Y) < 1e-3f,
                $"V slid at {p}: {was.Y} -> {now.Y}");
        }
    }

    // ---------------------------------------------------------------- the three transforms

    [Fact]
    public void Translate_KeepsTheTextureOnTheSurface()
    {
        Vector3 mins = Vector3.Zero, maxs = new(64, 64, 64);
        var delta = new Vector3(37.5f, -12f, 200f);

        VmapTexProjection before = Box(mins, maxs).Faces[0].Projection;
        VmapTexProjection after = VmapTexLock.Translate(before, delta);

        AssertTextureStuck(before, after, p => p + delta, mins, maxs);
    }

    [Fact]
    public void Rotate_KeepsTheTextureOnTheSurface()
    {
        Vector3 mins = Vector3.Zero, maxs = new(64, 64, 64);
        var pivot = new Vector3(32, 32, 0);            // NOT the origin — that is where the correction bites
        Quaternion q = Quaternion.CreateFromAxisAngle(
            Vector3.Normalize(new Vector3(0.2f, 0.3f, 1f)), 37f * MathF.PI / 180f);

        VmapTexProjection before = Box(mins, maxs).Faces[0].Projection;
        VmapTexProjection after = VmapTexLock.Rotate(before, q, pivot);

        AssertTextureStuck(before, after, p => pivot + Vector3.Transform(p - pivot, q), mins, maxs);
    }

    [Fact]
    public void Scale_KeepsTheTextureOnTheSurface()
    {
        Vector3 mins = Vector3.Zero, maxs = new(64, 64, 64);
        var pivot = new Vector3(16, 48, 8);
        var scale = new Vector3(2f, 0.5f, 1.25f);      // non-uniform: a uniform scale hides an axis mistake

        VmapTexProjection before = Box(mins, maxs).Faces[0].Projection;
        VmapTexProjection after = VmapTexLock.Scale(before, scale, pivot);

        AssertTextureStuck(before, after, p => pivot + (p - pivot) * scale, mins, maxs);
    }

    // ---------------------------------------------------------------- through the ops

    [Fact]
    public void MovingABrushWithLockOn_KeepsItsTextureAndWithLockOffSlidesIt()
    {
        Vector3 mins = Vector3.Zero, maxs = new(64, 64, 64);
        var delta = new Vector3(0f, 0f, 128f);

        var locked = new VmapDocument();
        locked.Brushes.Add(Box(mins, maxs));
        VmapTexProjection before = locked.Brushes[0].Faces[0].Projection;
        Assert.True(new TranslateBrushesOp(new[] { 1 }, delta, textureLock: true).Apply(locked));
        AssertTextureStuck(before, locked.Brushes[0].Faces[0].Projection, p => p + delta, mins, maxs);

        // And with it off the projection is untouched — the behaviour that predates the flag, kept so the
        // cvar genuinely switches something.
        var free = new VmapDocument();
        free.Brushes.Add(Box(mins, maxs));
        Assert.True(new TranslateBrushesOp(new[] { 1 }, delta).Apply(free));
        Assert.Equal(before.OffsetU, free.Brushes[0].Faces[0].Projection.OffsetU, 4);
        Assert.Equal(before.AxisU, free.Brushes[0].Faces[0].Projection.AxisU);
    }

    [Fact]
    public void RotatingABrushWithLockOn_KeepsItsTexture()
    {
        Vector3 mins = Vector3.Zero, maxs = new(64, 64, 64);
        var pivot = new Vector3(32, 32, 32);
        var axis = new Vector3(0, 0, 1);
        const float degrees = 90f;

        var doc = new VmapDocument();
        doc.Brushes.Add(Box(mins, maxs));
        VmapTexProjection before = doc.Brushes[0].Faces[0].Projection;

        Assert.True(new RotateBrushesOp(new[] { 1 }, pivot, axis, degrees, textureLock: true).Apply(doc));

        Quaternion q = Quaternion.CreateFromAxisAngle(
            Vector3.Normalize(axis), degrees * MathF.PI / 180f);
        AssertTextureStuck(before, doc.Brushes[0].Faces[0].Projection,
            p => pivot + Vector3.Transform(p - pivot, q), mins, maxs);
    }

    [Fact]
    public void ScalingABrushWithLockOn_KeepsItsTexture()
    {
        // Scale commits through CopyPlanesInto, which carries planes only — so a projection written onto the
        // validation clone would be silently discarded. This is the test that catches that.
        Vector3 mins = Vector3.Zero, maxs = new(64, 64, 64);
        var pivot = new Vector3(32, 32, 32);
        var scale = new Vector3(2f, 1f, 0.5f);

        var doc = new VmapDocument();
        doc.Brushes.Add(Box(mins, maxs));
        VmapTexProjection before = doc.Brushes[0].Faces[0].Projection;

        Assert.True(new ScaleSelectionOp(
            new[] { 1 }, Array.Empty<int>(), pivot, scale, textureLock: true).Apply(doc));

        AssertTextureStuck(before, doc.Brushes[0].Faces[0].Projection,
            p => pivot + (p - pivot) * scale, mins, maxs);
    }

    [Fact]
    public void TextureLockAppliesToEveryLayer_NotJustTheBase()
    {
        // A layered wall whose base stayed put while the blend slid out from under it would be worse than
        // no lock at all.
        Vector3 mins = Vector3.Zero, maxs = new(64, 64, 64);
        var delta = new Vector3(100f, 0f, 0f);

        var doc = new VmapDocument();
        VmapBrush brush = Box(mins, maxs);
        brush.Faces[0].Layers.Add(new VmapFaceLayer
        {
            Material = "textures/exx/rust",
            Projection = new VmapTexProjection(
                new Vector3(0.031f, 0f, 0f), new Vector3(0f, -0.031f, 0f), -4f, 9f),
            Blend = VmapBlend.Alpha,
        });
        VmapTexProjection layerBefore = brush.Faces[0].Layers[1].Projection;
        doc.Brushes.Add(brush);

        Assert.True(new TranslateBrushesOp(new[] { 1 }, delta, textureLock: true).Apply(doc));

        AssertTextureStuck(layerBefore, doc.Brushes[0].Faces[0].Layers[1].Projection,
            p => p + delta, mins, maxs);
    }

    // ---------------------------------------------------------------- replication

    [Fact]
    public void TheLockFlagTravelsOnTheWireAndIsOptional()
    {
        // It rides as a trailing token, so a line written before the flag existed still decodes — to OFF,
        // which is what those lines meant.
        foreach (bool locked in new[] { true, false })
        {
            var move = new TranslateBrushesOp(new[] { 1, 2 }, new Vector3(8, 0, 0), locked);
            string line = VmapOpWire.Serialize(move)!;
            var back = (TranslateBrushesOp)VmapOpWire.Deserialize(line)!;
            Assert.Equal(locked, back.TextureLock);
            Assert.Equal(line, VmapOpWire.Serialize(back));

            var rot = new RotateSelectionOp(
                new[] { 1 }, Array.Empty<int>(), Vector3.Zero, Vector3.UnitZ, 45f, locked);
            var rotBack = (RotateSelectionOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(rot)!)!;
            Assert.Equal(locked, rotBack.TextureLock);

            var scale = new ScaleSelectionOp(
                new[] { 1 }, new[] { 3 }, Vector3.Zero, new Vector3(2f, 2f, 2f), locked);
            var scaleBack = (ScaleSelectionOp)VmapOpWire.Deserialize(VmapOpWire.Serialize(scale)!)!;
            Assert.Equal(locked, scaleBack.TextureLock);
        }

        // A pre-flag line: no trailing token at all.
        var legacy = (TranslateBrushesOp)VmapOpWire.Deserialize("move 1 1 8 0 0")!;
        Assert.False(legacy.TextureLock);
    }
}

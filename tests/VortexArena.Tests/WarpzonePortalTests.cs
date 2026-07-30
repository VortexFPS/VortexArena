using System.Numerics;
using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Guards the <see cref="WarpzoneTransform"/> math the warpzone PORTAL render relies on (PortalRenderer places its
/// SubViewport camera at the warp-transformed main-camera pose). These are the parts verifiable without a running
/// engine — the rendered result (handedness through the Quake↔Godot conversion, near-clip) still needs an
/// in-game eyeball, but if these invariants break, the portal camera is provably wrong.
/// </summary>
public class WarpzonePortalTests
{
    private const float Eps = 1e-3f;

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length() < Eps, $"expected {expected} got {actual}");
    }

    // A straight-through portal: IN plane at origin facing +X, OUT plane 256 along +X facing -X (you walk in one
    // side and continue out the other) — the canonical corridor warpzone.
    private static WarpzoneTransform StraightThrough() =>
        new(new Vector3(0, 0, 0), new Vector3(0, 0, 0), new Vector3(256, 0, 0), new Vector3(0, 180, 0));

    // A 90°-turn portal with the OUT plane offset and yawed, to exercise real rotation.
    private static WarpzoneTransform Turned() =>
        new(new Vector3(100, 50, 0), new Vector3(0, 0, 0), new Vector3(-300, 600, 0), new Vector3(0, 90, 0));

    [Fact]
    public void TransformOrigin_MapsInPlaneOriginToOutPlaneOrigin()
    {
        foreach (WarpzoneTransform t in new[] { StraightThrough(), Turned() })
            AssertVec(t.OutOrigin, t.TransformOrigin(t.InOrigin));
    }

    [Fact]
    public void Rotate_MapsInForwardToNegatedOutForward_ThePortalHandedness()
    {
        // The 180°-flipped basis (inFwd → -outFwd) is what makes a portal camera look "out" of the exit. If this
        // sign flips, the portal renders the back of the exit instead of the view through it.
        foreach (WarpzoneTransform t in new[] { StraightThrough(), Turned() })
            AssertVec(-t.OutForward, t.Rotate(t.InForward));
    }

    [Fact]
    public void Rotate_IsLengthPreserving()
    {
        WarpzoneTransform t = Turned();
        var v = new Vector3(12f, -34f, 56f);
        Assert.True(System.MathF.Abs(t.Rotate(v).Length() - v.Length()) < Eps);
    }

    [Fact]
    public void Inverse_UndoesTheTransform()
    {
        // OUT→IN should round-trip a point back (no roll in these planes, so the inverse is exact).
        WarpzoneTransform t = StraightThrough();
        WarpzoneTransform inv = t.Inverse();
        var p = new Vector3(-40f, 17f, 9f);
        AssertVec(p, inv.TransformOrigin(t.TransformOrigin(p)));
    }

    [Fact]
    public void PortalCamera_InFrontOfIn_LandsBehindOut_LookingThrough()
    {
        // A camera 64 units in FRONT of the IN plane (along +inForward, the approach side) maps to 64 units along
        // -outForward from the OUT origin — i.e. behind the exit plane, facing back through it. This is exactly the
        // portal-camera placement PortalRenderer computes each frame.
        WarpzoneTransform t = StraightThrough();
        Vector3 camPos = t.InOrigin + t.InForward * 64f;
        Vector3 portalPos = t.TransformOrigin(camPos);
        AssertVec(t.OutOrigin - t.OutForward * 64f, portalPos);
    }
}

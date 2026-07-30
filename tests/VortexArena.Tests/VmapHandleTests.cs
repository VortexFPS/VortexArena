using System.Numerics;
using VortexArena.Formats.Vmap;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Covers the two-phase manipulator handles (phase E7, design doc §11.9).
///
/// The invariant these are all circling: aiming at an axis arrow must resolve to THAT axis and nothing else.
/// The whole reason handles became click targets is that grabbing the object and dragging moves it on two
/// screen axes at once, so any test that lets a grab return an unconstrained delta is testing the bug.
/// </summary>
public class VmapHandleTests
{
    private static readonly Vector3 X = new(1, 0, 0);
    private static readonly Vector3 Y = new(0, 1, 0);
    private static readonly Vector3 Z = new(0, 0, 1);

    private static List<EditorHandle> Build(HandleSet set, Vector3 centre, float size = 64f)
    {
        var list = new List<EditorHandle>();
        VmapHandles.Build(list, set, centre, size);
        return list;
    }

    // ---------------------------------------------------------------- construction

    [Fact]
    public void MoveSet_HasThreeArrowsAndThreePads()
    {
        List<EditorHandle> h = Build(HandleSet.Move, Vector3.Zero);
        Assert.Equal(3, h.Count(x => x.Kind == HandleKind.MoveAxis));
        Assert.Equal(3, h.Count(x => x.Kind == HandleKind.MovePlane));
    }

    [Fact]
    public void ScaleSet_HasSixBoxesAndAUniformCentre()
    {
        List<EditorHandle> h = Build(HandleSet.Scale, Vector3.Zero);
        Assert.Equal(6, h.Count(x => x.Kind == HandleKind.ScaleAxis));
        Assert.Single(h, x => x.Kind == HandleKind.ScaleUniform);

        // Both signs on every axis, so "+X face out" and "-X face out" are distinct grabs.
        Assert.Equal(3, h.Count(x => x.Kind == HandleKind.ScaleAxis && x.Sign > 0f));
        Assert.Equal(3, h.Count(x => x.Kind == HandleKind.ScaleAxis && x.Sign < 0f));
    }

    [Fact]
    public void RotateSet_HasThreeRings()
        => Assert.Equal(3, Build(HandleSet.Rotate, Vector3.Zero).Count(x => x.Kind == HandleKind.RotateRing));

    [Fact]
    public void NoneSet_BuildsNothing()
        => Assert.Empty(Build(HandleSet.None, Vector3.Zero));

    [Fact]
    public void Build_ClearsWhatWasThereBefore()
    {
        var list = new List<EditorHandle>();
        VmapHandles.Build(list, HandleSet.Scale, Vector3.Zero, 64f);
        VmapHandles.Build(list, HandleSet.Rotate, Vector3.Zero, 64f);
        Assert.Equal(3, list.Count);
    }

    // ---------------------------------------------------------------- picking

    [Fact]
    public void AimingDownAnArrow_PicksThatAxis()
    {
        List<EditorHandle> h = Build(HandleSet.Move, Vector3.Zero);

        // Stand off on -Y looking at the +X arrow's midpoint.
        var eye = new Vector3(40, -400, 0);
        Vector3 target = X * 40f;
        Assert.True(VmapHandles.TryPick(h, eye, Vector3.Normalize(target - eye), out EditorHandle hit, out _));

        Assert.Equal(HandleKind.MoveAxis, hit.Kind);
        Assert.Equal(X, hit.Axis);
    }

    [Fact]
    public void EachArrowResolvesToItsOwnAxis()
    {
        List<EditorHandle> h = Build(HandleSet.Move, Vector3.Zero);

        // Aim at each arrow from far enough back that perspective cannot confuse them, along a direction
        // that is not parallel to any of the three.
        foreach ((Vector3 axis, Vector3 eye) in new[]
        {
            (X, new Vector3(40, -500, 0)),
            (Y, new Vector3(-500, 40, 0)),
            (Z, new Vector3(0, -500, 40)),
        })
        {
            Vector3 target = axis * 40f;
            Assert.True(VmapHandles.TryPick(h, eye, Vector3.Normalize(target - eye), out EditorHandle hit, out _));
            Assert.Equal(HandleKind.MoveAxis, hit.Kind);
            Assert.Equal(axis, hit.Axis);
        }
    }

    [Fact]
    public void AimingAtEmptySpace_MissesEverything()
    {
        List<EditorHandle> h = Build(HandleSet.Move, Vector3.Zero);
        var eye = new Vector3(0, -500, 0);
        Assert.False(VmapHandles.TryPick(h, eye, Vector3.Normalize(new Vector3(900, 500, 900)), out _, out _));
    }

    [Fact]
    public void HandlesBehindTheEye_AreNeverPicked()
    {
        List<EditorHandle> h = Build(HandleSet.Move, Vector3.Zero);

        // Sitting past the handles looking further away: everything is behind the ray.
        var eye = new Vector3(0, -500, 0);
        Assert.False(VmapHandles.TryPick(h, eye, Vector3.Normalize(new Vector3(0, -1, 0)), out _, out _));
    }

    [Fact]
    public void NearestHandleWins_WhenTwoLineUp()
    {
        List<EditorHandle> near = Build(HandleSet.Move, new Vector3(0, 0, 0), size: 64f);
        List<EditorHandle> far = Build(HandleSet.Move, new Vector3(0, 0, 512), size: 64f);
        var all = new List<EditorHandle>();
        all.AddRange(far);
        all.AddRange(near);      // deliberately added second, so "nearest" cannot come from list order

        // Look along +Z from below: the near set's Z arrow is hit first.
        var eye = new Vector3(0, 0, -400);
        Assert.True(VmapHandles.TryPick(all, eye, Z, out EditorHandle hit, out float dist));
        Assert.Equal(HandleKind.MoveAxis, hit.Kind);
        Assert.True(dist < 512f, $"picked the far set (t={dist})");
    }

    [Fact]
    public void TheUniformCentreDoesNotSwallowTheAxisBoxes()
    {
        List<EditorHandle> h = Build(HandleSet.Scale, Vector3.Zero);

        // Aim at the +X box, which sits a shaft-length out from the centre handle.
        var eye = new Vector3(0, -500, 0);
        Vector3 target = X * 64f;
        Assert.True(VmapHandles.TryPick(h, eye, Vector3.Normalize(target - eye), out EditorHandle hit, out _));

        Assert.Equal(HandleKind.ScaleAxis, hit.Kind);
        Assert.Equal(1f, hit.Sign);
    }

    [Fact]
    public void RingIsPickedOnItsRim_NotItsInterior()
    {
        List<EditorHandle> h = Build(HandleSet.Rotate, Vector3.Zero, size: 64f);

        // The Z ring lies in the XY plane with radius 0.95*64 ~= 61. Aim straight down at its rim.
        var eye = new Vector3(61, 0, 400);
        Assert.True(VmapHandles.TryPick(h, eye, -Z, out EditorHandle rim, out _));
        Assert.Equal(HandleKind.RotateRing, rim.Kind);

        // Straight down through the middle must miss: a disc test would wrongly grab here, and that is what
        // makes rotate rings unusable (the ring you can see through becomes a solid click target).
        Assert.False(VmapHandles.TryPick(h, new Vector3(0, 0, 400), -Z, out _, out _));
    }

    // ---------------------------------------------------------------- drag constraint

    [Fact]
    public void AxisHandle_ProjectsADiagonalDragOntoOneAxis()
    {
        var handle = new EditorHandle(HandleKind.MoveAxis, Z, Vector3.Zero, Z * 64f, 6f) { Sign = 1f };
        Vector3 outcome = VmapHandles.ConstrainDrag(handle, new Vector3(37f, -19f, 12f));

        Assert.Equal(new Vector3(0f, 0f, 12f), outcome);
    }

    [Fact]
    public void PlanePad_KeepsTwoAxesAndDropsTheThird()
    {
        var pad = new EditorHandle(HandleKind.MovePlane, X, Vector3.Zero, Vector3.Zero, 8f)
        { Axis2 = Y, Sign = 1f };
        Vector3 outcome = VmapHandles.ConstrainDrag(pad, new Vector3(5f, 7f, 99f));

        Assert.Equal(new Vector3(5f, 7f, 0f), outcome);
    }

    // ---------------------------------------------------------------- scale factors

    [Fact]
    public void PullingAPositiveScaleHandleOutward_Grows()
    {
        var h = new EditorHandle(HandleKind.ScaleAxis, X, Vector3.Zero, X * 64f, 7f) { Sign = 1f };
        Vector3 f = VmapHandles.ScaleFactors(h, alongAxis: 32f, reach: 64f, minFactor: 0.01f);

        Assert.Equal(1.5f, f.X, 4);
        Assert.Equal(1f, f.Y, 4);
        Assert.Equal(1f, f.Z, 4);
    }

    /// <summary>
    /// Dragging the -X handle in -X must GROW the solid, not shrink it. Taking the factor from the raw
    /// displacement instead of folding in the handle's sign is the bug this pins: the far handle would then
    /// behave backwards, which reads as the gizmo being broken.
    /// </summary>
    [Fact]
    public void PullingANegativeScaleHandleOutward_AlsoGrows()
    {
        var h = new EditorHandle(HandleKind.ScaleAxis, X, Vector3.Zero, -X * 64f, 7f) { Sign = -1f };
        Vector3 f = VmapHandles.ScaleFactors(h, alongAxis: -32f, reach: 64f, minFactor: 0.01f);

        Assert.Equal(1.5f, f.X, 4);
    }

    [Fact]
    public void UniformHandle_ScalesAllThreeAxes()
    {
        var h = new EditorHandle(HandleKind.ScaleUniform, Vector3.One, Vector3.Zero, Vector3.Zero, 8f) { Sign = 1f };
        Vector3 f = VmapHandles.ScaleFactors(h, alongAxis: 64f, reach: 64f, minFactor: 0.01f);

        Assert.Equal(2f, f.X, 4);
        Assert.Equal(2f, f.Y, 4);
        Assert.Equal(2f, f.Z, 4);
    }

    [Fact]
    public void ScaleIsClampedAtTheFloor_SoADragCannotInvertTheSolid()
    {
        var h = new EditorHandle(HandleKind.ScaleAxis, X, Vector3.Zero, X * 64f, 7f) { Sign = 1f };

        // Dragging far enough inward would give a negative factor, which ScaleSelectionOp refuses outright;
        // clamping here means the drag stops at "very thin" instead of being rejected at release.
        Vector3 f = VmapHandles.ScaleFactors(h, alongAxis: -500f, reach: 64f, minFactor: 0.01f);
        Assert.Equal(0.01f, f.X, 4);
    }

    [Fact]
    public void ZeroReach_LeavesTheScaleAlone()
    {
        var h = new EditorHandle(HandleKind.ScaleAxis, X, Vector3.Zero, Vector3.Zero, 7f) { Sign = 1f };
        Assert.Equal(Vector3.One, VmapHandles.ScaleFactors(h, alongAxis: 10f, reach: 0f, minFactor: 0.01f));
    }

    // ---------------------------------------------------------------- screen-constant sizing

    [Fact]
    public void HandleSize_GrowsWithDistance_SoItStaysGrabbable()
    {
        float tan = MathF.Tan(MathF.PI / 8f);      // 45-degree vertical FOV
        float near = VmapHandles.ScreenScale(100f, tan);
        float far = VmapHandles.ScreenScale(4000f, tan);

        Assert.True(far > near * 30f, $"near={near} far={far}");
        // Proportional, so the handle occupies the same screen fraction at both distances.
        Assert.Equal(40f, far / near, 3);
    }

    [Fact]
    public void HandleSize_DoesNotCollapseAtTheCamera()
        => Assert.True(VmapHandles.ScreenScale(0f, MathF.Tan(MathF.PI / 8f)) > 0f);
}

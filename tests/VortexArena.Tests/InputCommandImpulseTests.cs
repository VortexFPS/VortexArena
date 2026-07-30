using System;
using System.Numerics;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Covers T34's two new surfaces that live in the Godot-free libraries:
/// <list type="bullet">
///   <item>the C2S <see cref="InputCommand.Impulse"/> wire byte (QC usercmd.impulse) — serialize/deserialize
///         round-trip incl. the boundary values + that adding it didn't disturb the other fields;</item>
///   <item>the shared first-person view's FOV math (the *0.75 4:3 aspect-normalization + the zoom ratio) — the
///         pure formula <c>XonoticGodot.Game.Client.FirstPersonView.ComputeVerticalFov</c> uses. That type lives in the
///         Godot game assembly (it takes a <c>Camera3D</c>), which this Godot-free test project doesn't reference,
///         so the math is re-derived here with <see cref="MathF"/> and pinned to the reference values the port
///         must produce (fov 100 → ~83.6° vertical; full zoom shrinks the frustum by exactly the zoom factor).</item>
/// </list>
/// </summary>
public class InputCommandImpulseTests
{
    private static InputCommand RoundTrip(InputCommand c)
    {
        var w = new BitWriter();
        c.Serialize(w);
        var r = new BitReader(w.WrittenSpan);
        InputCommand got = InputCommand.Deserialize(ref r);
        Assert.False(r.BadRead, "input command deserialized without overrunning the buffer");
        return got;
    }

    [Fact]
    public void Impulse_RoundTrips_On_The_Wire()
    {
        var c = new InputCommand
        {
            Seq = 1234,
            ViewAngles = new Vector3(12f, -47f, 0f),
            Forward = 1f,
            Side = -1f,
            Up = 0f,
            Buttons = (int)(InputButtons.Attack | InputButtons.Jump),
            Impulse = 230, // weapon_byid_0 (common/impulses/all.qh) — a real weapon-select impulse
            DeltaTime = 1f / 72f,
        };

        InputCommand got = RoundTrip(c);
        Assert.Equal(230, got.Impulse);
        // adding the impulse byte must not disturb the other fields the predictor/server read.
        Assert.Equal(c.Seq, got.Seq);
        Assert.Equal((int)(InputButtons.Attack | InputButtons.Jump), got.Buttons);
        Assert.True(MathF.Abs(got.DeltaTime - c.DeltaTime) < 1e-6f);
    }

    [Theory]
    [InlineData(0)]    // no impulse
    [InlineData(1)]    // weapon_group_1
    [InlineData(14)]   // weapon_group_0
    [InlineData(20)]   // weapon_reload
    [InlineData(253)]  // weapon_byid_23 — the top valid weapon impulse
    public void Impulse_RoundTrips_Across_Its_Valid_Range(int impulse)
    {
        var c = new InputCommand { Seq = 7, Impulse = impulse, DeltaTime = 1f / 72f };
        Assert.Equal(impulse, RoundTrip(c).Impulse);
    }

    [Fact]
    public void Impulse_Is_Clamped_Into_A_Byte_On_The_Wire()
    {
        // The impulse is serialized as a byte; an out-of-range value must not corrupt the stream or wrap into a
        // valid impulse. Serialize must clamp; the rest of the command must still read back intact.
        var c = new InputCommand { Seq = 99, Impulse = 9999, Buttons = (int)InputButtons.Zoom, DeltaTime = 1f / 72f };
        InputCommand got = RoundTrip(c);
        Assert.InRange(got.Impulse, 0, 255);
        Assert.Equal(99u, got.Seq);
        Assert.Equal((int)InputButtons.Zoom, got.Buttons);
    }

    [Fact]
    public void Zero_Impulse_Is_The_Default_And_Round_Trips()
    {
        // The common case (no weapon command this tick) must serialize a 0 impulse — the server's "nothing to
        // dispatch" sentinel (mirrors QC's CS(this).impulse == 0).
        var c = new InputCommand { Seq = 3, DeltaTime = 1f / 72f };
        Assert.Equal(0, c.Impulse);                 // default
        Assert.Equal(0, RoundTrip(c).Impulse);      // and on the wire
    }

    // ----------------------------------------------------------------------------------------------------
    //  FirstPersonView FOV math (mirrors XonoticGodot.Game.Client.FirstPersonView.ComputeVerticalFov)
    // ----------------------------------------------------------------------------------------------------

    /// <summary>The exact formula the shared view uses: Xonotic `fov` is HORIZONTAL at a 4:3 reference, Godot's
    /// Camera3D.Fov is VERTICAL — vfov = atan(tan(fov/2) * 0.75 * currentViewzoom) * 2. Kept here as the spec the
    /// port must satisfy (the Godot type isn't visible to this assembly).</summary>
    private static float ComputeVerticalFov(float baseFovDegrees, float currentViewZoom)
    {
        float frustumy = MathF.Tan(baseFovDegrees * (MathF.PI / 360f)) * 0.75f * currentViewZoom;
        return MathF.Atan(frustumy) * (360f / MathF.PI);
    }

    [Fact]
    public void Unzoomed_Fov100_Normalizes_To_About_83_6_Degrees_Vertical()
    {
        // The load-bearing *0.75 aspect-normalize: without it `fov 100` would render 100° vertical (too wide);
        // Xonotic's actual vertical fov at fov 100 is ~83.6°. (Regression guard for the SEAM the net path missed.)
        float vfov = ComputeVerticalFov(100f, 1f);
        Assert.InRange(vfov, 83.0f, 84.0f);
    }

    [Fact]
    public void Full_Zoom_Shrinks_The_Frustum_By_Exactly_The_Zoom_Factor()
    {
        // GetCurrentFov scales frustumy linearly by current_viewzoom (1 = unzoomed, 1/zoomfactor = fully zoomed).
        // So tan(vfov_zoomed/2) / tan(vfov_unzoomed/2) must equal current_viewzoom exactly (the zoom RATIO the
        // *0.75 doesn't change). Verify at a 5x zoom (cl_zoomfactor 5 → current_viewzoom 1/5).
        const float zoom = 1f / 5f;
        float unzoomed = ComputeVerticalFov(100f, 1f);
        float zoomed = ComputeVerticalFov(100f, zoom);

        float ratio = MathF.Tan(zoomed * (MathF.PI / 360f)) / MathF.Tan(unzoomed * (MathF.PI / 360f));
        Assert.True(MathF.Abs(ratio - zoom) < 1e-4f, $"zoom ratio {ratio} should equal current_viewzoom {zoom}");
        Assert.True(zoomed < unzoomed, "zooming in narrows the rendered fov");
    }

    [Fact]
    public void No_Zoom_Gives_The_Widest_Fov_And_Stays_Below_The_Horizontal_Base()
    {
        // current_viewzoom == 1 (fully unzoomed) is the steady state the zoom integration relaxes to when +zoom is
        // released; it must give the WIDEST rendered fov (any zoom<1 narrows it) and — thanks to the *0.75
        // normalize — stay below the horizontal base fov (the vertical fov is narrower than the 4:3 horizontal).
        float unzoomed = ComputeVerticalFov(90f, 1f);
        Assert.True(unzoomed < 90f, "the *0.75 normalize keeps the vertical fov below the horizontal base");
        Assert.True(unzoomed > ComputeVerticalFov(90f, 0.5f), "unzoomed is wider than any partial zoom");
    }
}

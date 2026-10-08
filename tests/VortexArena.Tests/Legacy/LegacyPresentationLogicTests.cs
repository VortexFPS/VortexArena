using System;
using System.Collections.Generic;
using System.Text;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The parts of the Godot bridge (game/legacy/) that are arithmetic and bookkeeping rather than
/// rendering, kept in the Godot-free library so they can be checked here: the view-property store and
/// its two projections, the field-of-view conversion, the recorded 2D draw list and its colour codes,
/// the input command's buttons and mouse look, the mark-and-sweep ledger behind the proxy nodes, the
/// virtual 2D resolution, text layout, font slots and the engine's first-person view.
/// Expected values are worked out by hand from the DarkPlaces formulas each class cites.
/// </summary>
public class LegacyPresentationLogicTests
{
    private static void Near(float expected, float actual, float tolerance = 1e-3f) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void Near(QcVector expected, QcVector actual, float tolerance = 1e-2f)
    {
        Near(expected.X, actual.X, tolerance);
        Near(expected.Y, actual.Y, tolerance);
        Near(expected.Z, actual.Z, tolerance);
    }

    private static LegacyViewState View(float width = 800, float height = 600, float fov = 90)
    {
        LegacyViewState view = new() { ConWidth = 800, ConHeight = 600 };
        view.SetEngineView(width, height, default, default, fov, 1);
        view.Reset();
        return view;
    }

    // ---- field of view ---------------------------------------------------------------------------------

    [Fact]
    public void Fov_IsHorizontalAtFourByThree()
    {
        // fov 90: frustum_y = tan(45) * 0.75 = 0.75, so the vertical angle is 2 * atan(0.75) = 73.74 degrees.
        Near(73.7398f, LegacyViewState.VerticalFov(90));
        // fov 100: tan(50) * 0.75 = 0.893815; 2 * atan = 83.58 degrees - the figure NetGame documents.
        Near(83.5814f, LegacyViewState.VerticalFov(100), 1e-2f);
        // Zooming in by 2 halves the tangent, not the angle.
        Near(2 * MathF.Atan(0.375f) * 180 / MathF.PI, LegacyViewState.VerticalFov(90, 0.5f));
    }

    [Fact]
    public void EngineFrustum_WidensWithTheWindowNotTheHeight()
    {
        LegacyViewState.EngineFrustum(90, 1, 1920, 1080, out float x, out float y);
        Near(0.75f, y);
        Near(0.75f * 1920 / 1080, x);
        // At 4:3 the horizontal angle is the fov cvar itself.
        LegacyViewState.EngineFrustum(90, 1, 1024, 768, out x, out _);
        Near(1f, x);
    }

    [Fact]
    public void ClearScene_RestoresTheEngineView()
    {
        LegacyViewState view = new() { ConWidth = 800, ConHeight = 600 };
        view.SetEngineView(1600, 900, new QcVector(10, 20, 30), new QcVector(5, 90, 0), 100, 1);
        view.Reset();
        Assert.Equal(1600, view.Width);
        Assert.Equal(900, view.Height);
        Near(new QcVector(10, 20, 30), view.Origin);
        Near(new QcVector(5, 90, 0), view.Angles);
        Assert.True(view.DrawWorld);
        Assert.False(view.DrawCrosshair);
        Assert.True(view.UsePerspective);

        view.Set(LegacyViewState.VfOrigin, new QcVector(1, 2, 3), default);
        view.Set(LegacyViewState.VfDrawWorld, default, default);
        view.Set(LegacyViewState.VfFov, new QcVector(60, 40, 0), default);
        view.Reset();
        Near(new QcVector(10, 20, 30), view.Origin);
        Assert.True(view.DrawWorld);
        Near(0.893815f, view.FrustumY, 1e-4f);
    }

    [Fact]
    public void Properties_RoundTripAndRejectUnknownKeys()
    {
        LegacyViewState view = View();
        Assert.True(view.Set(LegacyViewState.VfViewport, new QcVector(10, 20, 0), new QcVector(300, 200, 0)));
        Assert.True(view.Get(LegacyViewState.VfMin, out QcVector min));
        Near(new QcVector(10, 20, 0), min);
        Assert.True(view.Get(LegacyViewState.VfSizeY, out QcVector height));
        Near(200, height.X);

        // VF_FOV keeps the angles for getproperty and the tangents for the projection.
        Assert.True(view.Set(LegacyViewState.VfFov, new QcVector(90, 60, 0), default));
        Near(1f, view.FrustumX);
        Near(MathF.Tan(30 * MathF.PI / 180), view.FrustumY);
        Assert.True(view.Get(LegacyViewState.VfFov, out QcVector fov));
        Near(new QcVector(90, 60, 0), fov);
        Near(60f, view.VerticalFovDegrees);

        // One component at a time.
        view.Set(LegacyViewState.VfOrigin, new QcVector(1, 2, 3), default);
        view.Set(LegacyViewState.VfOriginY, new QcVector(50, 0, 0), default);
        Near(new QcVector(1, 50, 3), view.Origin);
        view.Set(LegacyViewState.VfAnglesX, new QcVector(-10, 0, 0), default);
        Assert.True(view.Get(LegacyViewState.VfAngles, out QcVector angles));
        Near(-10, angles.X);

        // VF_CLEARSCREEN is stored inverted (isoverlay) and read back as stored, as in the C.
        view.Set(LegacyViewState.VfClearScreen, new QcVector(0, 0, 0), default);
        Assert.True(view.IsOverlay);
        view.Set(LegacyViewState.VfMainView, new QcVector(1, 0, 0), default);
        Assert.True(view.Get(LegacyViewState.VfMainView, out QcVector main));
        Near(1, main.X);

        Assert.False(view.FogTouched);
        view.Set(LegacyViewState.VfFogColorG, new QcVector(0.5f, 0, 0), default);
        Assert.True(view.FogTouched);
        Near(0.5f, view.FogColor.Y);

        // cl.viewangles (33..36) is input state and belongs to the host, not to the view; 22 is nothing.
        Assert.False(view.Set(33, default, default));
        Assert.False(view.Get(22, out _));
        Assert.False(view.Set(9999, default, default));
    }

    [Fact]
    public void SetProperty_SurvivesHostileNumbers()
    {
        LegacyViewState view = View();
        view.Set(LegacyViewState.VfSize, new QcVector(float.NaN, float.PositiveInfinity, 0), default);
        Assert.Equal(0, view.Width);
        Assert.Equal(0, view.Height);   // not a number, not a size
        view.SetEngineView(float.NaN, -5, default, default, float.PositiveInfinity, float.NaN);
        view.Reset();
        Assert.Equal(1024, view.Width);
        Assert.Equal(1, view.Height);
        Assert.True(float.IsFinite(view.FrustumY));
    }

    // ---- cs_project / cs_unproject ---------------------------------------------------------------------

    [Fact]
    public void Project_MatchesHandComputedPixels()
    {
        // Eye at the origin looking along +X, 800x600 virtual pixels, frustum_x 1 and frustum_y 0.75 (fov 90 at 4:3).
        LegacyViewState view = View();
        // Straight ahead: the middle of the screen, depth 100.
        Near(new QcVector(400, 300, 100), view.Project(new QcVector(100, 0, 0)));
        // 50 units to the RIGHT is -Y in Quake. v1 = -50: x = 800 * 0.5 * (1 + (-50 / 100) / -1) = 600.
        Near(new QcVector(600, 300, 100), view.Project(new QcVector(100, -50, 0)));
        // 25 units up. v2 = 25: y = 600 * 0.5 * (1 + (25 / 100) / -0.75) = 200 (up the screen is smaller y).
        Near(new QcVector(400, 200, 100), view.Project(new QcVector(100, 0, 25)));
        // The edge of the view: tan(45) to the left is x = 0.
        Near(new QcVector(0, 300, 100), view.Project(new QcVector(100, 100, 0)));
        // Behind the eye: negative depth, which is what the program tests before drawing a label.
        Assert.True(view.Project(new QcVector(-100, 0, 0)).Z < 0);
    }

    [Fact]
    public void Project_FollowsTheViewAngles()
    {
        LegacyViewState view = View();
        view.Set(LegacyViewState.VfOrigin, new QcVector(10, 20, 30), default);
        // Yaw 90 looks along +Y; a point 200 units along +Y is dead centre.
        view.Set(LegacyViewState.VfAngles, new QcVector(0, 90, 0), default);
        Near(new QcVector(400, 300, 200), view.Project(new QcVector(10, 220, 30)));
        // Quake pitch is positive DOWN: pitched 45 down, a point straight ahead and level is in the upper half,
        // at v2 / v0 = 1: y = 300 * (1 - 1 / 0.75) = -100.
        view.Set(LegacyViewState.VfAngles, new QcVector(45, 0, 0), default);
        Near(new QcVector(400, -100, 100 * MathF.Sqrt(0.5f)), view.Project(new QcVector(110, 20, 30)));
    }

    [Fact]
    public void Unproject_InvertsProject()
    {
        LegacyViewState view = View(1920, 1080, 100);
        view.ConWidth = 1067;
        view.ConHeight = 600;
        view.Set(LegacyViewState.VfOrigin, new QcVector(-340, 512, 96), default);
        view.Set(LegacyViewState.VfAngles, new QcVector(12, -137, 4), default);
        foreach (QcVector world in new[] { new QcVector(-600, 300, 120), new QcVector(-700, 100, -40), new QcVector(-400, 420, 300) })
        {
            QcVector screen = view.Project(world);
            Assert.True(screen.Z > 0);
            Near(world, view.Unproject(screen), 0.05f);
        }
        // And by hand: the centre of the screen at depth 64 is 64 units along the view direction.
        LegacyViewState flat = View();
        Near(new QcVector(64, 0, 0), flat.Unproject(new QcVector(400, 300, 64)));
        // The right edge at depth 64: 64 * frustum_x to the right, which is -Y.
        Near(new QcVector(64, -64, 0), flat.Unproject(new QcVector(800, 300, 64)));
    }

    // ---- the virtual 2D resolution -----------------------------------------------------------------------

    [Theory]
    [InlineData(1024, 768, 800, 600)]
    [InlineData(1920, 1080, 1067, 600)]   // 800 wide would be 450 high: the height is held at 600 instead
    [InlineData(640, 480, 640, 480)]      // never finer than the window
    [InlineData(1280, 1024, 800, 640)]
    public void ConsoleSize_FollowsXonoticsMenu(int width, int height, int conWidth, int conHeight) =>
        Assert.Equal((conWidth, conHeight), LegacyConsoleSize.For(width, height));

    [Fact]
    public void ConsoleSize_ScalesAndSurvivesNonsense()
    {
        // menu_vid_scale -1: one unit per pixel.
        Assert.Equal((1920, 1080), LegacyConsoleSize.For(1920, 1080, 1, -1));
        // menu_vid_scale 1 on a 4:3 window: down to 640 wide.
        Assert.Equal((640, 480), LegacyConsoleSize.For(1024, 768, 1, 1));
        Assert.Equal((800, 600), LegacyConsoleSize.For(0, 0));
        Assert.Equal((800, 600), LegacyConsoleSize.For(float.NaN, 100));
    }

    // ---- colour codes ----------------------------------------------------------------------------------

    private static readonly LegacyColor White = new(1, 1, 1, 1);

    [Fact]
    public void ColorCodes_SplitIntoRuns()
    {
        List<LegacyTextRun> runs = new();
        LegacyColor last = LegacyTextColors.Walk("ab^1cd^x0F0e", false, White, runs);
        Assert.Equal(3, runs.Count);
        Assert.Equal(("ab", White), (runs[0].Text, runs[0].Color));
        Assert.Equal(("cd", new LegacyColor(1, 0, 0, 1)), (runs[1].Text, runs[1].Color));
        Assert.Equal("e", runs[2].Text);
        Assert.Equal(new LegacyColor(0, 1, 0, 1), runs[2].Color);
        Assert.Equal(new LegacyColor(0, 1, 0, 1), last);
    }

    [Fact]
    public void ColorCodes_KeepWhatIsNotACode()
    {
        StringBuilder visible = new();
        // "^^" is one caret; "^z" and a trailing caret are literal; "^xZZZ" is not a colour.
        LegacyTextColors.Walk("a^^b^zc^xZZZd^", false, White, null, visible);
        Assert.Equal("a^b^zc^xZZZd^", visible.ToString());

        // With colour codes ignored (drawstring) every character is drawn.
        List<LegacyTextRun> runs = new();
        LegacyTextColors.Walk("^1red", true, White, runs);
        Assert.Single(runs);
        Assert.Equal("^1red", runs[0].Text);
    }

    [Fact]
    public void ColorCodes_MultiplyTheCallersColour()
    {
        // The table colour times the base: half-bright red text asked for at alpha 0.5.
        LegacyColor baseColor = new(0.5f, 0.5f, 0.5f, 0.5f);
        LegacyColor last = LegacyTextColors.Walk("^1x", false, baseColor);
        Assert.Equal(new LegacyColor(0.5f, 0, 0, 0.5f), last);
        // ^8 is half transparent, ^9 half bright, ^4 the readable blue.
        Assert.Equal(new LegacyColor(1, 1, 1, 0.5f), LegacyTextColors.Walk("^8", false, White));
        Assert.Equal(new LegacyColor(0.5f, 0.5f, 0.5f, 1), LegacyTextColors.Walk("^9", false, White));
        Assert.Equal(new LegacyColor(0.05f, 0.15f, 1, 1), LegacyTextColors.Walk("^4", false, White));
        // No code at all: the default colour (7, white) times the base.
        Assert.Equal(baseColor, LegacyTextColors.Walk("plain", false, baseColor));
        // RGBstring_to_colorindex: 0x1RGBF.
        Assert.Equal(0x1A5FF, LegacyTextColors.RgbIndex("^xa5F", 2));
        Assert.Equal(0, LegacyTextColors.RgbIndex("^xa5", 2));
    }

    // ---- the draw list ---------------------------------------------------------------------------------

    [Fact]
    public void DrawList_RecordsInOrder()
    {
        LegacyDrawList list = new();
        list.SetClip(10, 20, 100, 50);
        list.Fill(new QcVector(1, 2, 0), new QcVector(3, 4, 0), new QcVector(1, 0.5f, 0.25f), 0.75f, 1);
        list.Picture(new LegacyPicture { Name = "gfx/hud/default/ammo_shells", Position = new QcVector(5, 6, 0), Size = new QcVector(32, 16, 0), Color = new QcVector(1, 1, 1), Alpha = 1, SourceSize = new QcVector(1, 1, 0) });
        QcVector last = list.Text(new LegacyText { Text = "^2go", Position = new QcVector(7, 8, 0), Scale = new QcVector(12, 12, 0), Color = new QcVector(1, 1, 1), Alpha = 1, Font = 9, FontScale = new QcVector(1, 1, 0) });
        list.ResetClip();

        Assert.Equal(5, list.Count);
        Assert.Equal(new[] { LegacyDrawKind.SetClip, LegacyDrawKind.Fill, LegacyDrawKind.Picture, LegacyDrawKind.Text, LegacyDrawKind.ResetClip },
            new[] { list.Commands[0].Kind, list.Commands[1].Kind, list.Commands[2].Kind, list.Commands[3].Kind, list.Commands[4].Kind });
        LegacyDrawCommand fill = list.Commands[1];
        Assert.Equal((1f, 2f, 3f, 4f, 1), (fill.X, fill.Y, fill.Width, fill.Height, fill.Flags));
        Assert.Equal(new LegacyColor(1, 0.5f, 0.25f, 0.75f), fill.Color);
        Assert.False(list.Commands[2].Rotated);
        Assert.Equal(9, list.Commands[3].Font);
        // The colour at the end of "^2go" is green: what drawcolorcodedstring returns.
        Near(new QcVector(0, 1, 0), last);

        list.Clear();
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void DrawList_IsBounded()
    {
        LegacyDrawList list = new();
        for (int i = 0; i < LegacyDrawList.MaxCommands + 100; i++)
            list.Fill(default, new QcVector(1, 1, 0), new QcVector(1, 1, 1), 1, 0);
        Assert.Equal(LegacyDrawList.MaxCommands, list.Count);
        Assert.Equal(100, list.Dropped);

        list.Clear();
        Assert.Equal(0, list.Dropped);
        Assert.Equal(100, list.TotalDropped);

        // Text is bounded per string and per frame.
        string big = new('x', LegacyDrawList.MaxTextLength * 2);
        for (int i = 0; i < 200; i++)
            list.Text(new LegacyText { Text = big, Scale = new QcVector(8, 8, 0), Color = new QcVector(1, 1, 1), Alpha = 1 });
        Assert.Equal(LegacyDrawList.MaxTextCharacters / LegacyDrawList.MaxTextLength, list.Count);
        Assert.Equal(LegacyDrawList.MaxTextLength, list.Commands[0].Text!.Length);

        // A polygon needs three vertices, and the frame's vertices are bounded too.
        list.Clear();
        LegacyPolygonVertex[] triangle = new LegacyPolygonVertex[3];
        Assert.False(list.Polygon("", 0, triangle.AsSpan(0, 2)));
        Assert.True(list.Polygon("", 0, triangle));
        Assert.Equal(3, list.PolygonVertices.Count);
        Assert.False(list.Polygon("", 0, new LegacyPolygonVertex[LegacyDrawList.MaxPolygonVertices]));
    }

    [Fact]
    public void DrawList_NeutralisesHostileNumbersAndNames()
    {
        LegacyDrawList list = new();
        list.Fill(new QcVector(float.NaN, float.PositiveInfinity, 0), new QcVector(1e30f, -1e30f, 0), new QcVector(float.NaN, 2, 3), float.NegativeInfinity, 0);
        LegacyDrawCommand fill = list.Commands[0];
        Assert.Equal((0f, 0f, 1e6f, -1e6f), (fill.X, fill.Y, fill.Width, fill.Height));
        Assert.Equal(new LegacyColor(0, 2, 3, 0), fill.Color);
        // A picture with no name, or one too long to be a path, is not recorded.
        Assert.False(list.Picture(new LegacyPicture { Name = "" }));
        Assert.False(list.Picture(new LegacyPicture { Name = new string('a', 400) }));
        Assert.Equal(1, list.Count);
        // A rotated picture is marked as such.
        list.Picture(new LegacyPicture { Name = "gfx/x", Angle = 30, RotationOrigin = new QcVector(4, 4, 0) });
        Assert.True(list.Commands[1].Rotated);
    }

    // ---- text layout and font slots --------------------------------------------------------------------

    [Fact]
    public void TextLayout_RasterisesAtTheCellHeight()
    {
        // A 12x12 cell at font scale 1: glyphs at 12 px, not stretched, baseline three quarters down.
        LegacyTextLayout square = LegacyTextLayout.For(12, 12, 1, 1);
        Assert.Equal((12, 1f, 1f, 9f), (square.PixelSize, square.ScaleX, square.ScaleY, square.Baseline));
        Near(60, square.Width(60));
        // An 8-wide, 16-high cell squeezes the glyphs to half width.
        LegacyTextLayout narrow = LegacyTextLayout.For(8, 16, 1, 1);
        Assert.Equal(16, narrow.PixelSize);
        Near(0.5f, narrow.ScaleX);
        Near(30, narrow.Width(60));
        // The drawfontscale multiplies the cell; a zero scale means "unset".
        Assert.Equal(24, LegacyTextLayout.For(12, 12, 0, 2).PixelSize);
        // Huge text is rasterised at the cap and scaled up; degenerate cells draw nothing.
        LegacyTextLayout huge = LegacyTextLayout.For(1000, 1000, 1, 1);
        Assert.Equal(LegacyTextLayout.MaxPixelSize, huge.PixelSize);
        Near(1000f / LegacyTextLayout.MaxPixelSize, huge.ScaleY);
        Assert.Equal(0f, LegacyTextLayout.For(float.NaN, 12, 1, 1).ScaleX);
    }

    [Fact]
    public void FontSlots_StartWithTheEnginesNames()
    {
        LegacyFontSlots fonts = new();
        Assert.Equal(LegacyFontSlots.BuiltIn, fonts.Count);
        Assert.Equal(0, fonts.Find("default"));
        Assert.Equal(1, fonts.Find("console"));
        Assert.Equal(LegacyFontSlots.FontUser + 1, fonts.Find("user1"));
        Assert.Equal(-1, fonts.Find("hud_bigfont"));

        // loadfont user1 fonts/xolonium-regular,gfx/vera-sans: the face and its fallbacks.
        Assert.Equal(9, fonts.Load("user1", "fonts/xolonium-regular:0,gfx/vera-sans", -1, 1, 0));
        Assert.Equal(new[] { "fonts/xolonium-regular", "gfx/vera-sans" }, fonts[9].Files);
        int version = fonts[9].Version;
        fonts.Load("user1", "fonts/other", -1, 0.9f, 0.1f);
        Assert.True(fonts[9].Version > version);
        Near(0.9f, fonts[9].Scale);

        // A new title is appended; an out-of-range slot reads as the default font.
        int added = fonts.Load("hud_bigfont", "fonts/xolonium-bold", -1, 1, 0);
        Assert.Equal(LegacyFontSlots.BuiltIn, added);
        Assert.Equal(added, fonts.Find("hud_bigfont"));
        Assert.Same(fonts[0], fonts[9999]);
        Assert.Equal(-1, fonts.Load("", "x", -1, 1, 0));
        Assert.Equal(-1, fonts.Load("x", "y", 100000, 1, 0));
    }

    // ---- input -----------------------------------------------------------------------------------------

    [Fact]
    public void Buttons_UseDarkPlacesBitLayout()
    {
        Assert.Equal(0, LegacyInputMath.Buttons(default, true));
        Assert.Equal(512, LegacyInputMath.Buttons(default, false));   // the chat bubble: the game has no keyboard
        Assert.Equal(1, LegacyInputMath.Buttons(new LegacyHeldButtons { Attack = true }, true));
        Assert.Equal(2, LegacyInputMath.Buttons(new LegacyHeldButtons { Jump = true }, true));
        Assert.Equal(4, LegacyInputMath.Buttons(new LegacyHeldButtons { Button3 = true }, true));    // +fire2
        Assert.Equal(8, LegacyInputMath.Buttons(new LegacyHeldButtons { Button4 = true }, true));    // +zoom
        Assert.Equal(16, LegacyInputMath.Buttons(new LegacyHeldButtons { Button5 = true }, true));   // +crouch
        Assert.Equal(32, LegacyInputMath.Buttons(new LegacyHeldButtons { Button6 = true }, true));   // +hook
        Assert.Equal(64, LegacyInputMath.Buttons(new LegacyHeldButtons { Button7 = true }, true));
        Assert.Equal(128, LegacyInputMath.Buttons(new LegacyHeldButtons { Button8 = true }, true));
        Assert.Equal(256, LegacyInputMath.Buttons(new LegacyHeldButtons { Use = true }, true));
        // Bit 1024 is skipped: button9 is 2048, button10 (+jetpack) 4096, button16 262144.
        Assert.Equal(2048, LegacyInputMath.Buttons(new LegacyHeldButtons { Button9 = true }, true));
        Assert.Equal(4096, LegacyInputMath.Buttons(new LegacyHeldButtons { Button10 = true }, true));
        Assert.Equal(262144, LegacyInputMath.Buttons(new LegacyHeldButtons { Button16 = true }, true));
        Assert.Equal(1 | 2 | 16, LegacyInputMath.Buttons(new LegacyHeldButtons { Attack = true, Jump = true, Button5 = true }, true));
    }

    [Fact]
    public void Move_ScalesKeysBySpeedCvars()
    {
        LegacyMoveSpeeds speeds = LegacyMoveSpeeds.Default;
        LegacyInputMath.Move(new LegacyHeldButtons { Forward = true, MoveLeft = true }, speeds, out float forward, out float side, out float up);
        Assert.Equal((400f, -350f, 0f), (forward, side, up));
        LegacyInputMath.Move(new LegacyHeldButtons { Back = true, MoveRight = true, MoveUp = true }, speeds, out forward, out side, out up);
        Assert.Equal((-400f, 350f, 400f), (forward, side, up));
        // Opposite keys cancel; +speed multiplies.
        LegacyInputMath.Move(new LegacyHeldButtons { Forward = true, Back = true, MoveRight = true, Speed = true }, speeds, out forward, out side, out _);
        Assert.Equal((0f, 700f), (forward, side));
    }

    [Fact]
    public void MouseLook_TurnsAgainstXAndClampsPitch()
    {
        // 100 counts right at sensitivity 3, m_yaw 0.022: yaw falls by 6.6 degrees. 50 counts down: pitch rises by 3.3.
        QcVector angles = LegacyInputMath.MouseLook(new QcVector(0, 90, 0), 100, 50, 3, 1, 1);
        Near(new QcVector(3.3f, 83.4f, 0), angles);
        // The program's sensitivity scale and the zoom both multiply.
        angles = LegacyInputMath.MouseLook(default, 100, 0, 3, 0.5f, 0.5f);
        Near(-1.65f, angles.Y);
        // An inverted mouse is a negative m_pitch.
        angles = LegacyInputMath.MouseLook(default, 0, 50, 3, 1, 1, 0.022f, -0.022f);
        Near(-3.3f, angles.X);
        // in_pitch_min / in_pitch_max.
        Near(90, LegacyInputMath.MouseLook(new QcVector(80, 0, 0), 0, 10000, 3, 1, 1).X);
        Near(-90, LegacyInputMath.MouseLook(new QcVector(-80, 0, 0), 0, -10000, 3, 1, 1).X);
        // A broken device delta changes nothing.
        Near(new QcVector(1, 2, 3), LegacyInputMath.MouseLook(new QcVector(1, 2, 3), float.NaN, 5, 3, 1, 1));
    }

    // ---- mark and sweep --------------------------------------------------------------------------------

    [Fact]
    public void Ledger_HidesWhatWasNotSubmittedAndReleasesLater()
    {
        LegacySceneLedger ledger = new() { ReleaseAfterFrames = 2 };
        List<int> hide = new(), release = new();

        ledger.BeginFrame();
        Assert.Equal(LegacySceneLedger.TouchResult.Create, ledger.Touch(5, "models/a.md3", 0));
        Assert.Equal(LegacySceneLedger.TouchResult.Create, ledger.Touch(6, "models/b.iqm", 0));
        // The same entity twice in a frame is one proxy.
        Assert.Equal(LegacySceneLedger.TouchResult.Update, ledger.Touch(5, "models/a.md3", 0));
        Assert.Equal(2, ledger.SubmittedThisFrame);
        ledger.Sweep(hide, release);
        Assert.Empty(hide);
        Assert.Empty(release);

        // Frame 2: only 5 is submitted. 6 is hidden, not yet released.
        ledger.BeginFrame();
        Assert.Equal(LegacySceneLedger.TouchResult.Update, ledger.Touch(5, "models/a.md3", 0));
        Assert.True(ledger.IsCurrent(5));
        Assert.False(ledger.IsCurrent(6));
        ledger.Sweep(hide, release);
        Assert.Equal(new[] { 6 }, hide);
        Assert.Empty(release);

        // Frame 3: still unsubmitted - it is hidden once, not again.
        ledger.BeginFrame();
        ledger.Touch(5, "models/a.md3", 0);
        ledger.Sweep(hide, release);
        Assert.Empty(hide);
        Assert.Empty(release);

        // Frame 4: three frames unsubmitted, past ReleaseAfterFrames.
        ledger.BeginFrame();
        ledger.Touch(5, "models/a.md3", 0);
        ledger.Sweep(hide, release);
        Assert.Equal(new[] { 6 }, release);
        Assert.Equal(1, ledger.Count);

        // It comes back as a new proxy.
        ledger.BeginFrame();
        Assert.Equal(LegacySceneLedger.TouchResult.Create, ledger.Touch(6, "models/b.iqm", 0));
    }

    [Fact]
    public void Ledger_RebuildsOnModelOrSkinChangeAndRefusesWhenFull()
    {
        LegacySceneLedger ledger = new() { Capacity = 2 };
        List<int> hide = new(), release = new();
        ledger.BeginFrame();
        ledger.Touch(1, "models/a.md3", 0);
        // The edict was freed and reused for something else, or the entity changed its model.
        Assert.Equal(LegacySceneLedger.TouchResult.Rebuild, ledger.Touch(1, "models/b.md3", 0));
        Assert.Equal(LegacySceneLedger.TouchResult.Rebuild, ledger.Touch(1, "models/b.md3", 2));
        Assert.Equal(LegacySceneLedger.TouchResult.Update, ledger.Touch(1, "models/b.md3", 2));
        ledger.Touch(2, "models/c.md3", 0);
        // R_AddEntity: "the scene is full".
        Assert.Equal(LegacySceneLedger.TouchResult.Refused, ledger.Touch(3, "models/d.md3", 0));
        Assert.Equal(1, ledger.Refused);
        Assert.Equal(2, ledger.Count);

        // A hidden proxy that is submitted again is shown again, and hidden again when it stops.
        ledger.Sweep(hide, release);
        ledger.BeginFrame();
        ledger.Sweep(hide, release);
        Assert.Equal(2, hide.Count);
        ledger.BeginFrame();
        ledger.Touch(1, "models/b.md3", 2);
        ledger.Sweep(hide, release);
        Assert.Empty(hide);
        ledger.BeginFrame();
        ledger.Sweep(hide, release);
        Assert.Equal(new[] { 1 }, hide);

        Assert.True(ledger.Release(1));
        Assert.False(ledger.Release(1));
        ledger.Clear(release);
        Assert.Equal(new[] { 2 }, release);
        Assert.Equal(0, ledger.Count);
    }

    // ---- V_CalcRefdef ----------------------------------------------------------------------------------

    private static LegacyRefdefSettings Settings => LegacyRefdefSettings.Default with { SmoothViewHeight = 0 };

    [Fact]
    public void Refdef_RaisesTheEyeByTheViewHeight()
    {
        LegacyRefdef refdef = new();
        LegacyRefdefInput input = new() { Origin = new QcVector(100, 200, 24), ViewHeight = 35 };
        refdef.Calculate(input, new QcVector(10, 20, 0), 1.0, 0.99, Settings, out QcVector origin, out QcVector angles);
        Near(new QcVector(100, 200, 59), origin);
        Near(new QcVector(10, 20, 0), angles);
    }

    [Fact]
    public void Refdef_SmoothsStairsOnTheGroundOnly()
    {
        LegacyRefdef refdef = new();
        LegacyRefdefSettings settings = Settings;
        LegacyRefdefInput input = new() { Origin = new QcVector(0, 0, 0), OnGround = true };
        refdef.Calculate(input, default, 1.0, 0.99, settings, out QcVector origin, out _);
        Near(0, origin.Z);

        // A 16-unit step up, 0.05 s later: the eye may climb 160 * 0.05 = 8 units.
        input.Origin = new QcVector(0, 0, 16);
        refdef.Calculate(input, default, 1.05, 1.0, settings, out origin, out _);
        Near(8, origin.Z);
        // The next 0.05 s finishes it.
        refdef.Calculate(input, default, 1.10, 1.05, settings, out origin, out _);
        Near(16, origin.Z);

        // In the air there is no smoothing: the eye is where the entity is.
        input.OnGround = false;
        input.Origin = new QcVector(0, 0, 80);
        refdef.Calculate(input, default, 1.15, 1.10, settings, out origin, out _);
        Near(80, origin.Z);

        // A teleport snaps even on the ground.
        input.OnGround = true;
        input.Teleported = true;
        input.Origin = new QcVector(0, 0, 500);
        refdef.Calculate(input, default, 1.20, 1.15, settings, out origin, out _);
        Near(500, origin.Z);

        // A step taller than the step height is not smoothed past it: the eye stays within StepHeight of the entity.
        input.Teleported = false;
        input.Origin = new QcVector(0, 0, 600);
        refdef.Calculate(input, default, 1.21, 1.20, settings, out origin, out _);
        Near(600 - settings.StepHeight, origin.Z);
    }

    [Fact]
    public void Refdef_AppliesPunchOncePerFrameAndTiltsTheDead()
    {
        LegacyRefdef refdef = new();
        LegacyRefdefSettings settings = Settings with { PunchAngle = new QcVector(-2, 0, 0), PunchVector = new QcVector(0, 0, 1) };
        LegacyRefdefInput input = new() { Origin = new QcVector(0, 0, 0) };
        refdef.BeginFrame();
        refdef.Calculate(input, new QcVector(10, 0, 0), 1.0, 0.99, settings, out QcVector origin, out QcVector angles);
        Near(8, angles.X);
        Near(1, origin.Z);
        // A second view in the same frame: "don't apply punchangle twice".
        refdef.Calculate(input, new QcVector(10, 0, 0), 1.0, 0.99, settings, out _, out angles);
        Near(10, angles.X);
        refdef.BeginFrame();
        refdef.Calculate(input, new QcVector(10, 0, 0), 1.02, 1.0, settings, out _, out angles);
        Near(8, angles.X);

        // Dead: rolled over by v_deathtiltangle, no punch.
        input.Dead = true;
        refdef.BeginFrame();
        refdef.Calculate(input, new QcVector(10, 0, 0), 1.04, 1.02, settings, out _, out angles);
        Near(new QcVector(10, 0, 80), angles);

        // Intermission: a fixed camera - the entity's own angles, raised by the view height, nothing else.
        LegacyRefdefInput camera = new() { Origin = new QcVector(5, 6, 7), Angles = new QcVector(30, 40, 0), ViewHeight = 10, Intermission = true };
        refdef.Calculate(camera, new QcVector(1, 2, 3), 1.06, 1.04, settings, out origin, out angles);
        Near(new QcVector(5, 6, 17), origin);
        Near(new QcVector(30, 40, 0), angles);
    }

    [Fact]
    public void Refdef_BlendsACrouchOverTime()
    {
        LegacyRefdef refdef = new();
        LegacyRefdefSettings settings = LegacyRefdefSettings.Default with { SmoothViewHeight = 0.1f, StairSmoothSpeed = 0 };
        LegacyRefdefInput input = new() { ViewHeight = 35 };
        // A whole second since the previous frame: the blend factor is 1 and the height is taken whole.
        refdef.Calculate(input, default, 10.0, 9.0, settings, out QcVector origin, out _);
        Near(35, origin.Z);
        // Crouch to 20; 0.05 s later half of the 0.1 s blend has passed: 35 * 0.5 + 20 * 0.5.
        input.ViewHeight = 20;
        refdef.Calculate(input, default, 10.05, 10.0, settings, out origin, out _);
        Near(27.5f, origin.Z);
    }
}

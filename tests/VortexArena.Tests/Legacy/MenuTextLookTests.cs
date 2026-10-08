using System;
using System.Linq;
using VortexArena.Legacy.Presentation;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// What makes DarkPlaces text look as it does, apart from its size: the outline and blur baked into each
/// glyph (ft2.c Font_Postprocess), the contrast and brightness applied to every text colour and the
/// optional shadow pass (gl_draw.c DrawQ_GetTextColor), and text starting on whole pixels
/// (snap_to_pixel_x / _y). Regression tests for "text has no halo", "text is greyer than DarkPlaces'"
/// and "glyph faces are smeared over two pixels".
/// </summary>
public class MenuTextLookTests
{
    private static byte Alpha(byte[] image, int width, int x, int y) => image[(x + y * width) * 4 + 3];
    private static byte Face(byte[] image, int width, int x, int y) => image[(x + y * width) * 4];

    [Fact]
    public void XonoticsSettings_GiveEveryGlyphADarkHaloTwoPixelsWide()
    {
        // r_font_postprocess_outline 1, r_font_postprocess_blur 1: one opaque pixel.
        LegacyGlyphPostprocess xonotic = new(1, 1);
        Assert.False(xonotic.Identity);
        Assert.Equal((2, 2, 2, 2), (xonotic.PadLeft, xonotic.PadRight, xonotic.PadTop, xonotic.PadBottom));
        byte[] image = xonotic.Apply(new byte[] { 255 }, 1, 1, out int width, out int height);
        Assert.Equal((5, 5), (width, height));

        // The glyph's own pixel: still opaque, still white.
        Assert.Equal(255, Alpha(image, width, 2, 2));
        Assert.Equal(255, Face(image, width, 2, 2));
        // Beside it: outline, black (the face colour is scaled by "old alpha / new alpha", and there was none).
        Assert.InRange(Alpha(image, width, 3, 2), 150, 254);
        Assert.Equal(0, Face(image, width, 3, 2));
        // The halo thins outwards, reaches the edge of the padding, and is the same on every side.
        Assert.True(Alpha(image, width, 3, 2) > Alpha(image, width, 3, 3));
        Assert.True(Alpha(image, width, 3, 3) > Alpha(image, width, 4, 2));
        Assert.True(Alpha(image, width, 4, 2) > Alpha(image, width, 4, 4));
        Assert.Equal(Alpha(image, width, 3, 2), Alpha(image, width, 1, 2));
        Assert.Equal(Alpha(image, width, 3, 2), Alpha(image, width, 2, 1));
        Assert.Equal(Alpha(image, width, 3, 2), Alpha(image, width, 2, 3));
        Assert.Equal(Alpha(image, width, 4, 4), Alpha(image, width, 0, 0));

        // Pinned: what this port produced when the screen matched DarkPlaces pixel for pixel (the top row
        // of the halo, and the pixel beside the glyph).
        Assert.Equal(new byte[] { 10, 37, 53, 37, 10 }, Enumerable.Range(0, 5).Select(x => Alpha(image, width, x, 0)).ToArray());
        Assert.Equal(158, Alpha(image, width, 3, 2));
    }

    [Fact]
    public void APartlyCoveredPixel_KeepsItsCoverageOfWhite_OverTheOutline()
    {
        LegacyGlyphPostprocess xonotic = new(1, 1);
        byte[] image = xonotic.Apply(new byte[] { 255, 128 }, 2, 1, out int width, out _);
        int alpha = Alpha(image, width, 3, 2), face = Face(image, width, 3, 2);
        Assert.True(alpha > 128);                                                 // "this is >= oldalpha"
        // c' = c * a1 / a': drawn over a background the pixel shows the same amount of white as before.
        Assert.InRange(face * alpha / 255, 126, 129);
    }

    [Fact]
    public void WithNothingSet_TheGlyphIsLeftAsItIs_AndTheShadowSettingsShiftTheHalo()
    {
        LegacyGlyphPostprocess none = new(0, 0);
        Assert.True(none.Identity);
        byte[] plain = none.Apply(new byte[] { 10, 200, 0, 255 }, 2, 2, out int width, out int height);
        Assert.Equal((2, 2), (width, height));
        Assert.Equal(new byte[] { 255, 255, 255, 10, 255, 255, 255, 200, 255, 255, 255, 0, 255, 255, 255, 255 }, plain);

        // r_font_postprocess_shadow_x: the disc is displaced, so the halo is heavier on one side.
        LegacyGlyphPostprocess shifted = new(1, 0, shadowX: 1);
        Assert.Equal((0, 2), (shifted.PadLeft, shifted.PadRight));
        byte[] image = shifted.Apply(new byte[] { 255 }, 1, 1, out width, out height);
        Assert.Equal((3, 3), (width, height));
        Assert.Equal(255, Alpha(image, width, 1, 1));
        Assert.Equal(255, Alpha(image, width, 2, 1));
        Assert.Equal(0, Face(image, width, 2, 1));
    }

    [Fact]
    public void Postprocess_IsBounded_WhateverTheCvarsHold()
    {
        LegacyGlyphPostprocess wild = new(1e9f, float.NaN, float.NegativeInfinity, 500, -500);
        Assert.InRange(wild.PadLeft, 0, 2 * LegacyGlyphPostprocess.MaxRadius);
        Assert.InRange(wild.PadBottom, 0, 2 * LegacyGlyphPostprocess.MaxRadius);
        byte[] image = wild.Apply(new byte[9], 3, 3, out int width, out int height);
        Assert.Equal(width * height * 4, image.Length);
        Assert.Equal(new byte[0], new LegacyGlyphPostprocess(0, 0).Apply(ReadOnlySpan<byte>.Empty, 0, 0, out _, out _));
        new LegacyGlyphPostprocess(1, 1).Apply(ReadOnlySpan<byte>.Empty, 0, 0, out width, out height);
        Assert.Equal((4, 4), (width, height));
        Assert.Throws<ArgumentException>(() => new LegacyGlyphPostprocess(1, 1).Apply(new byte[3], 2, 2, out _, out _));
        Assert.Throws<ArgumentException>(() => new LegacyGlyphPostprocess(1, 1).Apply(new byte[3], -1, 2, out _, out _));
        // The same instance serves glyphs of any size, larger after smaller and back.
        LegacyGlyphPostprocess reused = new(1, 1);
        reused.Apply(new byte[400], 20, 20, out _, out _);
        byte[] small = reused.Apply(new byte[] { 255 }, 1, 1, out width, out _);
        Assert.Equal(158, Alpha(small, width, 3, 2));
    }

    [Fact]
    public void TextColour_IsContrastTimesColourPlusBrightness()
    {
        LegacyTextLook xonotic = new(0.8f, 0.2f, 0);                              // xonotic-common.cfg
        Assert.Equal(new LegacyColor(1, 1, 1, 0.5f), Round(xonotic.Color(new LegacyColor(1, 1, 1, 0.5f))));
        Assert.Equal(new LegacyColor(0.2f, 0.2f, 0.2f, 1), Round(xonotic.Color(new LegacyColor(0, 0, 0, 1))));   // black text is a fifth grey
        Assert.Equal(new LegacyColor(0.6f, 0.2f, 1, 1), Round(xonotic.Color(new LegacyColor(0.5f, 0, 1, 1))));
        Assert.Equal(new LegacyColor(0.3f, 0.6f, 0.9f, 0.25f), LegacyTextLook.Plain.Color(new LegacyColor(0.3f, 0.6f, 0.9f, 0.25f)));
        Assert.False(xonotic.HasShadow(1));                                       // r_textshadow 0: one pass

        // r_textshadow 1: a black copy first, fainter under dark text.
        LegacyTextLook shadowed = new(1, 0, 1);
        Assert.True(shadowed.HasShadow(1));
        Assert.False(shadowed.HasShadow(0));
        Assert.Equal(new LegacyColor(0, 0, 0, 0.5f), shadowed.ShadowColor(new LegacyColor(1, 1, 1, 0.5f)));     // (1+1+1)*0.8, bounded to 1
        Assert.Equal(0.4f, shadowed.ShadowColor(new LegacyColor(0.5f, 0, 0, 1)).A, 5);
        Assert.Equal(0f, shadowed.ShadowColor(new LegacyColor(0, 0, 0, 1)).A);
        Assert.False(new LegacyTextLook(1, 0, float.NaN).HasShadow(1));

        static LegacyColor Round(LegacyColor c) => new(MathF.Round(c.R, 4), MathF.Round(c.G, 4), MathF.Round(c.B, 4), c.A);
    }

    [Fact]
    public void TextStartsOnAWholePixel_AndSoDoesItsBaseline()
    {
        // snap_to_pixel_x(x, 0.4): up from four tenths. snap_to_pixel_y: up from MORE than the threshold.
        Assert.Equal(10, LegacyTextLayout.SnapX(10.39f));
        Assert.Equal(11, LegacyTextLayout.SnapX(10.41f));
        Assert.Equal(11, LegacyTextLayout.SnapX(10.99f));
        Assert.Equal(10, LegacyTextLayout.SnapY(10.4f, 0.4f));
        Assert.Equal(11, LegacyTextLayout.SnapY(10.41f, 0.4f));
        Assert.Equal(10, LegacyTextLayout.SnapY(10.29f));
        Assert.Equal(11, LegacyTextLayout.SnapY(10.5f));                          // the baseline of a 14-pixel map
        Assert.Equal(9, LegacyTextLayout.SnapY(9f));                              // and of a 12-pixel one
        Assert.Equal(0, LegacyTextLayout.SnapX(float.NaN));
        Assert.Equal(0, LegacyTextLayout.SnapY(float.PositiveInfinity));

        // A call that is drawn larger than its map: the baseline is a whole REAL pixel, not a whole map pixel.
        LegacyFontSizing sizing = new() { Sizes = new float[] { 12 }, PixelsX = 1.2f, PixelsY = 1.2f, Snapping = 4, Metrics = new LegacyFontMetrics(1000, 1200), Scale = 1 };
        LegacyTextLayout title = LegacyTextLayout.For(24, 24, 1, 1, sizing, out _);
        float realBaseline = title.Baseline * title.ScaleY * 1.2f;                // map pixels -> virtual units -> real pixels
        Assert.Equal(22, realBaseline, 3);                                        // 28.8 * 0.75 = 21.6
    }
}

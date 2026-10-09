using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VortexArena.Formats.Images;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// <see cref="QuakePalette"/> against DarkPlaces' <c>palette.c</c> (<c>Palette_Load</c>,
/// <c>Palette_SetupSpecialPalettes</c>) and <c>gl_rmain.c</c> (<c>R_SkinFrame_LoadInternalQuake</c>,
/// <c>R_SkinFrame_GenerateTexturesFromQPixels</c>). Everything is synthetic except the one comparison with the
/// table in <c>palette.c</c>, which does nothing without the reference checkout.
/// </summary>
public class QuakePaletteTests
{
    private static byte[] Px(byte[] rgba, int i) => rgba.AsSpan(i * 4, 4).ToArray();

    [Fact]
    public void Default_HasQuakeColoursAndNoFullbrights()
    {
        QuakePalette p = QuakePalette.Default;
        Assert.Equal(768, p.Rgb.Length);
        Assert.Equal(new byte[] { 0, 0, 0 }, p.Rgb.Slice(0, 3).ToArray());
        Assert.Equal(new byte[] { 235, 235, 235 }, p.Rgb.Slice(15 * 3, 3).ToArray());
        Assert.Equal(new byte[] { 159, 91, 83 }, p.Rgb.Slice(255 * 3, 3).ToArray());
        Assert.Equal(new byte[] { 255, 243, 27 }, p.Rgb.Slice(111 * 3, 3).ToArray());

        // No gfx/colormap.lmp: fullbright_start = 256, so nothing is a glow index.
        Assert.Equal(256, p.FullbrightStart);
        Assert.Equal(QuakePaletteFeatures.Standard, p.FeatureOf(254));
        Assert.Null(p.Convert(new byte[] { 1, 240, 254 }).Glow);
    }

    [Fact]
    public void Features_FollowDarkPlacesOrderOfOverrides()
    {
        QuakePalette p = QuakePalette.Quake;
        Assert.Equal(224, p.FullbrightStart);
        Assert.Equal(QuakePaletteFeatures.Zero, p.FeatureOf(0));
        Assert.Equal(QuakePaletteFeatures.Standard, p.FeatureOf(1));
        Assert.Equal(QuakePaletteFeatures.Shirt, p.FeatureOf(16));
        Assert.Equal(QuakePaletteFeatures.Shirt, p.FeatureOf(31));
        Assert.Equal(QuakePaletteFeatures.Standard, p.FeatureOf(32));
        Assert.Equal(QuakePaletteFeatures.Pants, p.FeatureOf(96));
        Assert.Equal(QuakePaletteFeatures.Pants, p.FeatureOf(111));
        Assert.Equal(QuakePaletteFeatures.Standard, p.FeatureOf(112));
        Assert.Equal(QuakePaletteFeatures.Reversed, p.FeatureOf(128));
        Assert.Equal(QuakePaletteFeatures.Reversed, p.FeatureOf(223));
        Assert.Equal(QuakePaletteFeatures.Glow, p.FeatureOf(224));
        Assert.Equal(QuakePaletteFeatures.Glow, p.FeatureOf(254));
        Assert.Equal(QuakePaletteFeatures.Transparent, p.FeatureOf(255));   // not Glow
        Assert.Equal(QuakePaletteFeatures.Zero | QuakePaletteFeatures.Pants | QuakePaletteFeatures.Glow,
            p.FeaturesOf(new byte[] { 0, 100, 230 }));
    }

    [Fact]
    public void Load_TakesColoursFromPaletteLmpAndFullbrightCountFromColormapLmp()
    {
        byte[] pal = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            pal[i * 3] = (byte)i;
            pal[i * 3 + 1] = (byte)(255 - i);
            pal[i * 3 + 2] = 7;
        }
        byte[] colormap = new byte[16385];
        colormap[16384] = 32;

        QuakePalette p = QuakePalette.Load(pal, colormap);
        Assert.Equal(224, p.FullbrightStart);
        Assert.Equal(new byte[] { 200, 55, 7, 255 }, Px(p.ToRgba(new byte[] { 200 }), 0));

        // Too short a palette falls back on the built-in one; too short a colormap gives no fullbrights.
        QuakePalette q = QuakePalette.Load(new byte[767], new byte[16384]);
        Assert.Equal(256, q.FullbrightStart);
        Assert.True(q.Rgb.SequenceEqual(QuakePalette.DefaultRgb));

        // A colormap saying "8 fullbrights" starts them at 248.
        colormap[16384] = 8;
        Assert.Equal(248, QuakePalette.Load(null, colormap).FullbrightStart);
    }

    [Fact]
    public void Convert_OpaquePictureWithoutFullbrights_IsTheCompletePalette()
    {
        QuakeTexture t = QuakePalette.Quake.Convert(new byte[] { 0, 15, 255, 1 });
        Assert.Null(t.Glow);            // 255 alone is "transparent", not "glow"
        Assert.False(t.HasAlpha);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Px(t.Base, 0));
        Assert.Equal(new byte[] { 235, 235, 235, 255 }, Px(t.Base, 1));
        Assert.Equal(new byte[] { 159, 91, 83, 255 }, Px(t.Base, 2));
        Assert.Equal(new byte[] { 15, 15, 15, 255 }, Px(t.Base, 3));
    }

    [Fact]
    public void Convert_SplitsFullbrightsIntoGlowAndBlacksThemInBase()
    {
        // 251 = (255,0,0); 15 = light grey; 255 = the transparent index.
        QuakeTexture t = QuakePalette.Quake.Convert(new byte[] { 15, 251, 255 });
        Assert.NotNull(t.Glow);
        Assert.Equal(new byte[] { 235, 235, 235, 255 }, Px(t.Base, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Px(t.Base, 1));          // palette_bgra_nofullbrights: index 0's colour
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Px(t.Base, 2));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(t.Glow!, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(t.Glow!, 1));
        Assert.Equal(new byte[] { 159, 91, 83, 255 }, Px(t.Glow!, 2));     // opaque picture: 255 glows too

        // r_fullbrights 0: no split.
        QuakeTexture whole = QuakePalette.Quake.Convert(new byte[] { 15, 251 }, splitFullbrights: false);
        Assert.Null(whole.Glow);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(whole.Base, 1));
    }

    [Fact]
    public void Convert_Transparent255_MakesAHoleInBaseAndGlow()
    {
        QuakeTexture t = QuakePalette.Quake.Convert(new byte[] { 15, 251, 255 }, transparent255: true);
        Assert.True(t.HasAlpha);
        Assert.Equal(new byte[] { 235, 235, 235, 255 }, Px(t.Base, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Px(t.Base, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(t.Base, 2));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(t.Glow!, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(t.Glow!, 2));

        QuakeTexture noGlow = QuakePalette.Default.Convert(new byte[] { 15, 251, 255 }, transparent255: true);
        Assert.Null(noGlow.Glow);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(noGlow.Base, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(noGlow.Base, 2));
    }

    [Fact]
    public void ConvertColormapped_SplitsPantsAndShirtAsGreyRamps()
    {
        QuakePalette p = QuakePalette.Quake;
        Assert.Null(p.ConvertColormapped(new byte[] { 1, 2, 240 }));       // no pants or shirt texel

        // 16 = first shirt index, 31 = last; 96 = first pants, 111 = last; 5 ordinary; 251 fullbright.
        QuakeColormappedTexture c = p.ConvertColormapped(new byte[] { 16, 31, 96, 111, 5, 251 })!;
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(c.Base, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(c.Base, 2));
        Assert.Equal(new byte[] { 75, 75, 75, 255 }, Px(c.Base, 4));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(c.Base, 5));            // nocolormapnofullbrights
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Px(c.Shirt, 0));         // grey ramp entry 0
        Assert.Equal(new byte[] { 235, 235, 235, 255 }, Px(c.Shirt, 1));   // grey ramp entry 15
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(c.Shirt, 2));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Px(c.Pants, 2));
        Assert.Equal(new byte[] { 235, 235, 235, 255 }, Px(c.Pants, 3));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(c.Pants, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(c.Glow!, 5));

        // Without a fullbright range the base keeps 251 and there is no glow.
        QuakeColormappedTexture d = QuakePalette.Default.ConvertColormapped(new byte[] { 16, 251 })!;
        Assert.Null(d.Glow);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Px(d.Base, 1));
    }

    [Fact]
    public void Tables_AlphaAndFont()
    {
        QuakePalette p = QuakePalette.Quake;
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Px(p.ToRgba(new byte[] { 3 }, QuakePaletteTable.Alpha), 0));
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, Px(p.ToRgba(new byte[] { 255 }, QuakePaletteTable.Alpha), 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Px(p.ToRgba(new byte[] { 0 }, QuakePaletteTable.Font), 0));
        Assert.Equal(new byte[] { 159, 91, 83, 255 }, Px(p.ToRgba(new byte[] { 255 }, QuakePaletteTable.Font), 0));
        Assert.Equal(1024, p.Table(QuakePaletteTable.Complete).Length);
    }

    [Fact]
    public void ColormapColor_IsTheRowsBrightEntry()
    {
        QuakePalette p = QuakePalette.Default;
        // Row 0: index 0x0C = (187,187,187). Row 8 (reversed): index 0x84 = (127,91,111).
        Assert.Equal(((byte)187, (byte)187, (byte)187), p.ColormapColor(0));
        Assert.Equal(((byte)127, (byte)91, (byte)111), p.ColormapColor(8));
        Assert.Equal(p.ColormapColor(4), p.ColormapColor(4 + 16));         // only the low nibble counts
    }

    /// <summary>The embedded palette is byte for byte the <c>host_quakepal</c> table of the reference checkout.</summary>
    [Fact]
    public void DefaultRgb_MatchesDarkPlacesSource()
    {
        if (TestPaths.BaseData == TestPaths.Unresolved)
            return;
        string file = Path.Combine(Path.GetDirectoryName(TestPaths.BaseData)!, "darkplaces", "palette.c");
        if (!File.Exists(file))
            return;
        string src = File.ReadAllText(file);
        int a = src.IndexOf("unsigned char host_quakepal[768]", StringComparison.Ordinal);
        Assert.True(a >= 0);
        int open = src.IndexOf('{', a), close = src.IndexOf("};", open, StringComparison.Ordinal);
        string body = Regex.Replace(src.Substring(open + 1, close - open - 1), "//.*", "");
        byte[] expected = Regex.Matches(body, @"\d+").Select(m => byte.Parse(m.Value)).ToArray();
        Assert.Equal(768, expected.Length);
        Assert.True(QuakePalette.DefaultRgb.SequenceEqual(expected));
    }
}

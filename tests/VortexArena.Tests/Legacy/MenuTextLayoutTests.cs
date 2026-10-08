using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using VortexArena.Legacy.Presentation;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// How DarkPlaces sizes text drawn with an outline font (ft2.c Font_SearchSize, Font_IndexForSize,
/// Font_VirtualToRealSize; gl_draw.c DrawQ_String_Scale) - the regression tests for "the menu's text is
/// too large and too wide": the port used to rasterise glyphs as tall as the character cell, where
/// DarkPlaces fits the whole LINE in the cell and snaps the cell to the nearest font map.
/// </summary>
public class MenuTextLayoutTests
{
    /// <summary>A font whose line is 1.2 em, as Xolonium's is.</summary>
    private static readonly LegacyFontMetrics Line12 = new(1000, 1200);

    // Xonotic at 1280x720: vid_conheight 600, vid_conwidth 1066 (vid_conwidthauto), r_font_size_snapping 4.
    private static LegacyFontSizing Sizing(params float[] sizes) => new()
    {
        Sizes = sizes, PixelsX = 1280f / 1066f, PixelsY = 720f / 600f, Snapping = 4, Metrics = Line12, Scale = 1,
    };

    [Fact]
    public void SearchSize_FitsTheLineInTheMap_NotTheEm()
    {
        Assert.Equal(12, Line12.SearchSize(14));                                  // 12 * 1.2 = 14.4 -> 14
        Assert.Equal(10, Line12.SearchSize(12));
        Assert.Equal(13, Line12.SearchSize(16));                                  // 13 * 1.2 = 15.6 -> 16
        Assert.Equal(16, new LegacyFontMetrics(2048, 2048).SearchSize(16));       // a font whose line is its em
        Assert.Equal(-1, new LegacyFontMetrics(1000, 8000).SearchSize(4));        // nothing fits
        Assert.Equal(-1, default(LegacyFontMetrics).SearchSize(14));
    }

    [Fact]
    public void MapSize_IsTheVirtualSizeInWholeRealPixels()
    {
        Assert.Equal(14, LegacyTextLayout.MapSize(12, 1.2f));                     // 14.4
        Assert.Equal(13, LegacyTextLayout.MapSize(10, 1.25f));                    // 12.5 rounds up
        Assert.Equal(16, LegacyTextLayout.MapSize(0, 1.2f));                      // "0 means 16"
        Assert.Equal(16, LegacyTextLayout.MapSize(float.NaN, 1.2f));
        Assert.Equal(16, LegacyTextLayout.MapSize(5000, 1.2f));
    }

    [Fact]
    public void ACallNearAMap_SnapsToIt_AndIsRasterisedAtTheSizeWhoseLineFits()
    {
        // The menu font has one map (12 virtual = 14 real pixels). Menu text asked for at 12 units:
        LegacyTextLayout layout = LegacyTextLayout.For(12, 12, 1, 1, Sizing(12), out float yShift);
        Assert.Equal(0, yShift);
        Assert.Equal(14, layout.Cell);
        Assert.Equal(12, layout.PixelSize);                                       // not 14: the bug this replaced
        Assert.Equal(11, layout.Baseline);                                        // 0.75 * 14 = 10.5, half goes down
        // One rasterised pixel is one real pixel: 1/1.2 virtual units, exactly, both ways.
        Assert.Equal(600f / 720f, layout.ScaleY, 5);
        Assert.Equal(1066f / 1280f, layout.ScaleX, 5);

        // Asked for at 10 or at 14 units it is still within 4 real pixels of the map: the same glyphs.
        foreach (float size in new[] { 10f, 11f, 13f, 14f })
        {
            LegacyTextLayout near = LegacyTextLayout.For(size, size, 1, 1, Sizing(12), out _);
            Assert.Equal((14, 12), (near.Cell, near.PixelSize));
            Assert.Equal(600f / 720f, near.ScaleY, 5);
        }

        // A title at 24 units is too far to snap: the same map, stretched to the size asked for.
        LegacyTextLayout title = LegacyTextLayout.For(24, 24, 1, 1, Sizing(12), out _);
        Assert.Equal((14, 12), (title.Cell, title.PixelSize));
        Assert.Equal(24f / 14f, title.ScaleY, 5);
        Assert.Equal(24f / 14f, title.ScaleX, 5);
    }

    [Fact]
    public void TheNearestMapIsUsed_TheBiggerOfTwoEquallyNear()
    {
        LegacyFontSizing sizing = new() { Sizes = new float[] { 8, 12, 16, 24 }, PixelsX = 1, PixelsY = 1, Snapping = 0, Metrics = Line12, Scale = 1 };
        Assert.Equal(16, LegacyTextLayout.For(17, 17, 1, 1, sizing, out _).Cell);
        Assert.Equal(24, LegacyTextLayout.For(40, 40, 1, 1, sizing, out _).Cell);
        Assert.Equal(8, LegacyTextLayout.For(3, 3, 1, 1, sizing, out _).Cell);
        Assert.Equal(16, LegacyTextLayout.For(14, 14, 1, 1, sizing, out _).Cell); // 12 and 16 are both 2 away
        // No snapping: the size asked for is kept, drawn from the 16 map.
        LegacyTextLayout kept = LegacyTextLayout.For(14, 14, 1, 1, sizing, out _);
        Assert.Equal(14f / 16f, kept.ScaleY, 5);
        Assert.Equal(13, kept.PixelSize);
        // No sizes on the loadfont line: one map of 16 real pixels.
        Assert.Equal(16, LegacyTextLayout.For(10, 10, 1, 1, sizing with { Sizes = null }, out _).Cell);
    }

    [Fact]
    public void FontScaleAndOffset_MoveTheTextOnItsLine_AndDrawFontScaleStretchesIt()
    {
        LegacyFontSizing sizing = new() { Sizes = new float[] { 16 }, PixelsX = 1, PixelsY = 1, Snapping = 0, Metrics = Line12, Scale = 1.5f, VerticalOffset = 0.25f };
        LegacyTextLayout layout = LegacyTextLayout.For(16, 16, 1, 1, sizing, out float yShift);
        Assert.Equal(-((1.5f - 1) * 16 * 0.5f - 0.25f * 16), yShift, 5);          // "center & offset": -4 + 4
        Assert.Equal(1.5f, layout.ScaleY, 5);                                     // 24 units from the 16 map

        LegacyTextLayout squeezed = LegacyTextLayout.For(16, 16, 0.5f, 2, sizing with { Scale = 1, VerticalOffset = 0 }, out _);
        Assert.Equal(0.5f, squeezed.ScaleX, 5);
        Assert.Equal(2f, squeezed.ScaleY, 5);
        Assert.Equal(8f, squeezed.Width(16), 5);
    }

    [Fact]
    public void WithoutAnOutlineFont_OrWithNonsense_TheSimpleLayoutIsUsed()
    {
        LegacyTextLayout bitmap = LegacyTextLayout.For(8, 12, 1, 1, new LegacyFontSizing { PixelsX = 1.2f, PixelsY = 1.2f, Snapping = 4, Scale = 1 }, out _);
        Assert.Equal(LegacyTextLayout.For(8, 12, 1, 1), bitmap);
        Assert.Equal(12, bitmap.Cell);
        Assert.Equal(0, LegacyTextLayout.For(float.NaN, 12, 1, 1, Sizing(12), out _).ScaleX);
        Assert.Equal(0, LegacyTextLayout.For(12, 0, 1, 1, Sizing(12), out _).ScaleY);
        Assert.Equal(0, LegacyTextLayout.For(12, 12, float.PositiveInfinity, 1, Sizing(12), out _).ScaleX);
        Assert.True(LegacyTextLayout.For(1e6f, 1e6f, 1, 1, Sizing(900), out _).PixelSize <= LegacyTextLayout.MaxPixelSize);
    }

    // ---- the font file ----------------------------------------------------------------------------

    private static byte[] Sfnt(int unitsPerEm, short ascender, short descender, short lineGap, (bool UseTypo, short Ascender, short Descender, short LineGap)? os2 = null)
    {
        int tables = os2 is null ? 2 : 3;
        int offset = 12 + tables * 16;
        byte[] file = new byte[offset + 54 + 36 + 96];
        BinaryPrimitives.WriteUInt32BigEndian(file, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)tables);
        void Record(int index, string tag, int at, int length)
        {
            Span<byte> record = file.AsSpan(12 + index * 16);
            for (int i = 0; i < 4; i++) record[i] = (byte)tag[i];
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)length);
        }
        Record(0, "head", offset, 54);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(offset + 18), (ushort)unitsPerEm);
        Record(1, "hhea", offset + 54, 36);
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(offset + 54 + 4), ascender);
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(offset + 54 + 6), descender);
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(offset + 54 + 8), lineGap);
        if (os2 is { } typo)
        {
            int at = offset + 54 + 36;
            Record(2, "OS/2", at, 96);
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(at + 62), (ushort)(typo.UseTypo ? 128 : 0));
            BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(at + 68), typo.Ascender);
            BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(at + 70), typo.Descender);
            BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(at + 72), typo.LineGap);
        }
        return file;
    }

    [Fact]
    public void FontMetrics_AreReadFromTheFilesOwnTables()
    {
        Assert.True(LegacyFontMetrics.TryRead(Sfnt(1000, 950, -250, 0), out LegacyFontMetrics hhea));
        Assert.Equal(new LegacyFontMetrics(1000, 1200), hhea);
        // USE_TYPO_METRICS: the OS/2 values win.
        Assert.True(LegacyFontMetrics.TryRead(Sfnt(2048, 1900, -500, 0, (true, 1600, -448, 100)), out LegacyFontMetrics typo));
        Assert.Equal(new LegacyFontMetrics(2048, 2148), typo);
        // Without the bit they are only a fallback for an empty hhea.
        Assert.True(LegacyFontMetrics.TryRead(Sfnt(2048, 1900, -500, 0, (false, 1600, -448, 100)), out LegacyFontMetrics kept));
        Assert.Equal(2400, kept.Height);
        Assert.True(LegacyFontMetrics.TryRead(Sfnt(2048, 0, 0, 0, (false, 1600, -448, 100)), out LegacyFontMetrics fallback));
        Assert.Equal(2148, fallback.Height);

        // A file that is not a font, is cut short, or lies about its tables is refused, not read past.
        Assert.False(LegacyFontMetrics.TryRead(ReadOnlySpan<byte>.Empty, out _));
        Assert.False(LegacyFontMetrics.TryRead(new byte[11], out _));
        Assert.False(LegacyFontMetrics.TryRead(Sfnt(1000, 950, -250, 0).AsSpan(0, 30), out _));
        Assert.False(LegacyFontMetrics.TryRead(Sfnt(0, 950, -250, 0), out _));
        Assert.False(LegacyFontMetrics.TryRead(Sfnt(1000, 0, 0, 0), out _));    // no line at all
        byte[] lying = Sfnt(1000, 950, -250, 0);
        BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(12 + 8), 0xFFFFFF00);  // head "at" four gigabytes
        Assert.False(LegacyFontMetrics.TryRead(lying, out _));
        byte[] collection = new byte[16];
        "ttcf"u8.CopyTo(collection);
        BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(12), 0x7FFFFFFF);
        Assert.False(LegacyFontMetrics.TryRead(collection, out _));
        Random random = new(7);
        byte[] noise = new byte[4096];
        for (int round = 0; round < 200; round++)
        {
            random.NextBytes(noise);
            noise[4] = 0;
            noise[5] = (byte)random.Next(1, 40);
            LegacyFontMetrics.TryRead(noise.AsSpan(0, random.Next(12, noise.Length)), out _);   // must not throw
        }
    }

    [Fact]
    public void Xolonium_TheFontXonoticShips_GetsTheSizesDarkPlacesGivesIt()
    {
        if (TestPaths.BaseData == TestPaths.Unresolved) return;
        string path = Path.Combine(TestPaths.BaseData, "font-xolonium.pk3dir", "fonts", "xolonium-regular.otf");
        if (!File.Exists(path)) return;
        Assert.True(LegacyFontMetrics.TryRead(File.ReadAllBytes(path), out LegacyFontMetrics metrics));
        Assert.Equal(12, metrics.SearchSize(14));                                 // menu text at 1280x720
        Assert.Equal(10, metrics.SearchSize(12));
    }

    // ---- the old character set ---------------------------------------------------------------------

    [Fact]
    public void BitmapFontWidths_AreReadAsLoadFontReadsThem()
    {
        LegacyBitmapFontWidths widths = LegacyBitmapFontWidths.Parse("0.5 0.25\nextraspacing 0.1 0.5\tscale 2 somethingelse 9 1");
        Assert.Equal(0.5f, widths.Widths[0]);
        Assert.Equal(0.25f, widths.Widths[1]);
        Assert.Equal(0.6f, widths.Widths[2], 5);
        Assert.Equal(1.1f, widths.Widths[3], 5);                                  // the unknown word took its value with it
        Assert.Equal(1f, widths.Widths[4]);                                       // unsaid: a whole cell
        Assert.Equal(2f, widths.Scale);
        Assert.Equal(7, widths.Advance(0, 14));                                   // whole pixels of the map
        Assert.Equal(4, widths.Advance(1, 14));
        Assert.Equal(7, widths.Advance(0xE000, 14));                              // only the low byte names the glyph
        Assert.Equal(0, widths.Advance(0, 0));

        Assert.All(LegacyBitmapFontWidths.Parse(null).Widths, w => Assert.Equal(1f, w));
        Assert.Null(LegacyBitmapFontWidths.Parse("").Scale);
        // More numbers than glyphs, a word with no value, numbers that are not: none of it throws or spills.
        LegacyBitmapFontWidths many = LegacyBitmapFontWidths.Parse(string.Join(' ', Enumerable.Repeat("0.5", 400)) + " scale");
        Assert.Equal(256, many.Widths.Length);
        Assert.Equal(0f, LegacyBitmapFontWidths.Parse("1e999 -").Widths[0]);
    }

    [Fact]
    public void LoadFont_KeepsTheSizesItWasGiven_WithinDarkPlacesLimits()
    {
        LegacyFontSlots fonts = new();
        int slot = fonts.Load("menu", "fonts/xolonium-regular,fonts/unifont", -1, 1, 0, new float[] { 12, 12, 0, -3, 5000, 16, float.NaN });
        Assert.Equal(fonts.Find("menu"), slot);
        Assert.Equal(new float[] { 12, 16 }, fonts[slot].Sizes);                  // no "crap sizes", none twice
        Assert.Equal(new[] { "fonts/xolonium-regular", "fonts/unifont" }, fonts[slot].Files);
        fonts.Load("menu", "fonts/x", -1, 1, 0, Enumerable.Range(1, 40).Select(i => (float)i).ToArray());
        Assert.Equal(LegacyFontSlots.MaxFontSizes, fonts[slot].Sizes.Length);
        fonts.Load("menu", "fonts/x", -1, 1, 0);
        Assert.Empty(fonts[slot].Sizes);
    }
}

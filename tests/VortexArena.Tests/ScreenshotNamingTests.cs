using System;
using System.Collections.Generic;
using XonoticGodot.Engine.Console;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the Godot-free screenshot file-naming core (<see cref="ScreenshotNamer"/>) — the port of DarkPlaces'
/// <c>SCR_ScreenShot_f</c> naming logic (cl_screen.c): format precedence, explicit-filename extension parsing, and
/// the two auto-naming modes (timestamped + sequential) with their cross-extension "first free slot" scan. The
/// Godot capture/encode glue (<c>ScreenshotService</c>) is verified manually (windowed).
/// </summary>
public class ScreenshotNamingTests
{
    /// <summary>An existence probe for a dir with nothing in it yet.</summary>
    private static readonly Func<string, bool> None = _ => false;

    /// <summary>An existence probe over a fixed set of already-taken write-root-relative paths.</summary>
    private static Func<string, bool> Taken(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.Ordinal);
        return p => set.Contains(p);
    }

    [Theory]
    [InlineData(true, true, ScreenshotFormat.Jpeg)]    // jpeg wins over png (DP cl_screen.c:927-928)
    [InlineData(true, false, ScreenshotFormat.Jpeg)]
    [InlineData(false, true, ScreenshotFormat.Png)]
    [InlineData(false, false, ScreenshotFormat.Tga)]   // neither set → Targa
    public void ResolveFormat_FollowsDpPrecedence(bool jpeg, bool png, ScreenshotFormat expected)
        => Assert.Equal(expected, ScreenshotNamer.ResolveFormat(jpeg, png));

    [Theory]
    [InlineData(ScreenshotFormat.Jpeg, "jpg")]
    [InlineData(ScreenshotFormat.Png, "png")]
    [InlineData(ScreenshotFormat.Tga, "tga")]
    public void Extension_MapsFormat(ScreenshotFormat fmt, string ext)
        => Assert.Equal(ext, ScreenshotNamer.Extension(fmt));

    [Theory]
    [InlineData("shot.jpg", true, ScreenshotFormat.Jpeg)]
    [InlineData("shot.JPG", true, ScreenshotFormat.Jpeg)] // case-insensitive
    [InlineData("shot.png", true, ScreenshotFormat.Png)]
    [InlineData("shot.tga", true, ScreenshotFormat.Tga)]
    [InlineData("a.b.png", true, ScreenshotFormat.Png)]   // last dot wins
    [InlineData("shot.bmp", false, ScreenshotFormat.Tga)] // unsupported extension → rejected (DP error)
    [InlineData("noext", false, ScreenshotFormat.Tga)]
    [InlineData("", false, ScreenshotFormat.Tga)]
    public void TryFormatFromExtension_OnlyAcceptsThreeExtensions(string file, bool ok, ScreenshotFormat fmt)
    {
        Assert.Equal(ok, ScreenshotNamer.TryFormatFromExtension(file, out ScreenshotFormat got));
        if (ok)
            Assert.Equal(fmt, got);
    }

    [Fact]
    public void NextSequential_EmptyDir_StartsAtZeroPadded()
    {
        var n = new ScreenshotNamer();
        Assert.Equal("screenshots/xonotic000000.jpg", n.NextSequential("xonotic", ScreenshotFormat.Jpeg, None));
    }

    [Fact]
    public void NextSequential_AdvancesAcrossCalls()
    {
        var n = new ScreenshotNamer();
        Assert.Equal("screenshots/xonotic000000.jpg", n.NextSequential("xonotic", ScreenshotFormat.Jpeg, None));
        Assert.Equal("screenshots/xonotic000001.jpg", n.NextSequential("xonotic", ScreenshotFormat.Jpeg, None));
    }

    [Fact]
    public void NextSequential_SkipsSlotsTakenByAnyExtension()
    {
        var n = new ScreenshotNamer();
        // 000000 already exists as a PNG, 000001 as a TGA → the first free number is 000002 (DP's triple-check).
        Func<string, bool> exists = Taken("screenshots/xonotic000000.png", "screenshots/xonotic000001.tga");
        Assert.Equal("screenshots/xonotic000002.jpg", n.NextSequential("xonotic", ScreenshotFormat.Jpeg, exists));
    }

    [Fact]
    public void NextSequential_ResetsCounterWhenPrefixChanges()
    {
        var n = new ScreenshotNamer();
        Assert.Equal("screenshots/a000000.tga", n.NextSequential("a", ScreenshotFormat.Tga, None));
        Assert.Equal("screenshots/a000001.tga", n.NextSequential("a", ScreenshotFormat.Tga, None));
        // A different prefix rescans from 0 (DP's old_prefix_name reset).
        Assert.Equal("screenshots/b000000.tga", n.NextSequential("b", ScreenshotFormat.Tga, None));
    }

    [Fact]
    public void NextTimestamped_EmptyDir_UsesTwoDigitSuffix()
    {
        Assert.Equal("screenshots/xonotic20240101120000-00.jpg",
            ScreenshotNamer.NextTimestamped("xonotic20240101120000", ScreenshotFormat.Jpeg, None));
    }

    [Fact]
    public void NextTimestamped_SkipsTakenSuffixes()
    {
        Func<string, bool> exists = Taken("screenshots/p-00.jpg", "screenshots/p-01.png");
        Assert.Equal("screenshots/p-02.tga", ScreenshotNamer.NextTimestamped("p", ScreenshotFormat.Tga, exists));
    }

    [Fact]
    public void NextTimestamped_AllHundredTaken_ReturnsNull()
    {
        // Every base already has a .jpg this second → no free 00..99 slot (DP "already 100 shots taken this second").
        Func<string, bool> exists = p => p.EndsWith(".jpg", StringComparison.Ordinal);
        Assert.Null(ScreenshotNamer.NextTimestamped("p", ScreenshotFormat.Jpeg, exists));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Formats.Lighting;
using VortexArena.Formats.Vfs;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// The <c>.rtlights</c> reader/writer (F4). The format has three progressively shorter line forms and a
/// leading <c>!</c> that means the opposite of what a bang usually means (no shadow, not "important"), so the
/// cases below pin each form, the marker, and the round trip.
/// </summary>
public class RtLightsFileTests
{
    private static readonly string DataDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../data"));

    [Fact]
    public void Short_Form_Parses_With_Dp_Defaults()
    {
        RtLightsFile.Light? l = RtLightsFile.ParseLine(
            "-128.000000 1072.000000 576.000000 320.000000 1.000000 0.700000 0.500000 0");
        Assert.NotNull(l);
        Assert.Equal(new Vector3(-128f, 1072f, 576f), l!.Origin);
        Assert.Equal(320f, l.Radius);
        Assert.Equal(new Vector3(1f, 0.7f, 0.5f), l.Color);
        Assert.Equal(0, l.Style);

        // Everything the short form omits must come back as DarkPlaces' default, not as zero.
        Assert.True(l.Shadow);
        Assert.Equal(string.Empty, l.CubemapName);
        Assert.Equal(0f, l.Corona);
        Assert.Equal(0.25f, l.CoronaSizeScale);
        Assert.Equal(0f, l.AmbientScale);
        Assert.Equal(1f, l.DiffuseScale);
        Assert.Equal(1f, l.SpecularScale);
        Assert.Equal(RtLightsFile.FlagRealtimeMode, l.Flags);
    }

    [Fact]
    public void Long_Form_Parses_Every_Field()
    {
        RtLightsFile.Light? l = RtLightsFile.ParseLine(
            "418.000000 550.000000 550.000000 320.000000 1.000000 0.700000 0.500000 4 \"cubemaps/gobo\" " +
            "2.500000 10.000000 20.000000 30.000000 0.750000 0.100000 0.900000 0.800000 3");
        Assert.NotNull(l);
        Assert.Equal(4, l!.Style);
        Assert.Equal("cubemaps/gobo", l.CubemapName);
        Assert.Equal(2.5f, l.Corona);
        Assert.Equal(new Vector3(10f, 20f, 30f), l.Angles);
        Assert.Equal(0.75f, l.CoronaSizeScale);
        Assert.Equal(0.1f, l.AmbientScale);
        Assert.Equal(0.9f, l.DiffuseScale);
        Assert.Equal(0.8f, l.SpecularScale);
        Assert.Equal(3, l.Flags);
    }

    /// <summary>The leading <c>!</c> is DarkPlaces' "this light casts NO shadow" marker.</summary>
    [Fact]
    public void Bang_Prefix_Means_No_Shadow()
    {
        RtLightsFile.Light? on = RtLightsFile.ParseLine("10 20 30 100 1 1 1 0");
        RtLightsFile.Light? off = RtLightsFile.ParseLine("!10 20 30 100 1 1 1 0");
        Assert.True(on!.Shadow);
        Assert.False(off!.Shadow);
        // The ! must not eat the coordinate it prefixes.
        Assert.Equal(on.Origin, off.Origin);
    }

    /// <summary>A quoted cubemap name containing a space must stay one field.</summary>
    [Fact]
    public void Quoted_Cubemap_With_Space_Stays_One_Field()
    {
        RtLightsFile.Light? l = RtLightsFile.ParseLine(
            "0 0 0 100 1 1 1 0 \"my cube map\" 1.0 0 0 0");
        Assert.NotNull(l);
        Assert.Equal("my cube map", l!.CubemapName);
        Assert.Equal(1.0f, l.Corona);
    }

    [Fact]
    public void Bad_Lines_Are_Skipped_Not_Fatal()
    {
        List<RtLightsFile.Light> lights = RtLightsFile.Parse(
            "10 20 30 100 1 1 1 0\nnot a light at all\n\n40 50 60 200 1 1 1 0\n", out int skipped);
        Assert.Equal(2, lights.Count);
        Assert.Equal(1, skipped);
    }

    /// <summary>
    /// Round trip: parse → write → parse must be stable, and the writer must pick the same shortest-adequate
    /// form DP picks (so a file this port writes stays loadable by DarkPlaces).
    /// </summary>
    [Fact]
    public void Round_Trip_Is_Stable_And_Picks_The_Shortest_Form()
    {
        var plain = new RtLightsFile.Light
        { Origin = new Vector3(1f, 2f, 3f), Radius = 100f, Color = Vector3.One, Style = 0 };
        var withCorona = new RtLightsFile.Light
        { Origin = new Vector3(4f, 5f, 6f), Radius = 200f, Color = Vector3.One, Corona = 1f };
        var withScales = new RtLightsFile.Light
        { Origin = new Vector3(7f, 8f, 9f), Radius = 300f, Color = Vector3.One, AmbientScale = 0.5f, Shadow = false };

        string text = RtLightsFile.Write(new[] { plain, withCorona, withScales });
        string[] lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(8, lines[0].Split(' ').Length);     // all-default -> short form
        Assert.Equal(13, lines[1].Split(' ').Length);    // corona -> medium form
        Assert.Equal(18, lines[2].Split(' ').Length);    // non-default scale -> long form
        Assert.StartsWith("!", lines[2]);                // no-shadow marker survives the write

        List<RtLightsFile.Light> back = RtLightsFile.Parse(text, out int skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(3, back.Count);
        Assert.Equal(plain.Origin, back[0].Origin);
        Assert.Equal(1f, back[1].Corona);
        Assert.Equal(0.5f, back[2].AmbientScale);
        Assert.False(back[2].Shadow);
    }

    /// <summary>
    /// The six shipped Xonotic maps that carry a <c>.rtlights</c> file must parse without skipping a line.
    /// This is the case that catches a tokeniser that is right about the synthetic cases and wrong about the
    /// mixed short/long lines a real DarkPlaces writer emits.
    /// </summary>
    [Fact]
    public void Shipped_Xonotic_Rtlights_Files_Parse_Clean()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountContentRoot(DataDir)) return;

        string[] known = { "bromine", "fuse", "glowplant", "implosion", "runningman", "techassault" };
        int filesSeen = 0, lightsSeen = 0;
        foreach (string map in known)
        {
            string path = $"maps/{map}.rtlights";
            string? text;
            try { text = vfs.ReadText(path); }
            catch { continue; }
            if (string.IsNullOrEmpty(text)) continue;

            filesSeen++;
            List<RtLightsFile.Light> lights = RtLightsFile.Parse(text, out int skipped);
            Assert.Equal(0, skipped);
            Assert.NotEmpty(lights);
            lightsSeen += lights.Count;

            // Sanity on the parsed values: a radius must be positive, and a colour must not be all-black
            // (either would mean the fields shifted).
            foreach (RtLightsFile.Light l in lights)
            {
                Assert.True(l.Radius > 0f, $"{map}: non-positive radius {l.Radius}");
                Assert.True(l.Color.X + l.Color.Y + l.Color.Z > 0f, $"{map}: black light colour");
            }
        }

        // Non-vacuity: if the map pk3s are mounted at all, ALL SIX must have been found and parsed. Without
        // this the test passes silently in an environment where the VFS quietly resolved nothing - which is
        // exactly the environment where a tokeniser regression would slip through unnoticed.
        bool mapsMounted = vfs.Find("maps/", "bsp").Any(p => p.Contains("bromine"));
        if (!mapsMounted) return;
        Assert.Equal(known.Length, filesSeen);
        Assert.True(lightsSeen > 10, $"only {lightsSeen} lights across {filesSeen} files - suspiciously few");
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using XonoticGodot.Formats.Sidecars;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first coverage for the <see cref="FrameGroups"/> sidecar parser (port of Darkplaces
/// <c>Mod_FrameGroupify_ParseGroups</c>, model_shared.c). Pins the line grammar
/// <c>firstframe framecount [fps] [loop] [name] // comment</c>: the first two tokens are REQUIRED
/// (an incomplete line is skipped, like DP's warn-and-continue), fps defaults to 20 with a lower bound
/// of 1, loop defaults to TRUE, the optional 5th token is the clip name, and <c>//</c> comments are
/// the only comment style. Includes a real shipped .framegroups when assets are present.
/// </summary>
public class FrameGroupsTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    [Fact]
    public void FourTokenLine_ParsesAllFields()
    {
        List<FrameGroup> groups = FrameGroups.Parse("0 50 5 1");
        FrameGroup g = Assert.Single(groups);
        Assert.Equal(0, g.FirstFrame);
        Assert.Equal(50, g.FrameCount);
        Assert.Equal(5f, g.Fps);
        Assert.True(g.Loop);
        Assert.Equal(string.Empty, g.Name);
    }

    [Fact]
    public void FpsDefaultsTo20_LoopDefaultsToTrue()
    {
        FrameGroup g = Assert.Single(FrameGroups.Parse("10 25"));
        Assert.Equal(10, g.FirstFrame);
        Assert.Equal(25, g.FrameCount);
        Assert.Equal(20f, g.Fps);     // DP default
        Assert.True(g.Loop);          // DP default
    }

    [Fact]
    public void LoopZero_ParsesAsFalse()
    {
        FrameGroup g = Assert.Single(FrameGroups.Parse("0 10 15 0"));
        Assert.False(g.Loop);
    }

    [Fact]
    public void FifthToken_IsTheClipName_FurtherTokensIgnored()
    {
        FrameGroup g = Assert.Single(FrameGroups.Parse("0 10 15 1 idle extra tokens eaten"));
        Assert.Equal("idle", g.Name);
    }

    [Fact]
    public void Comments_RunToEndOfLine()
    {
        // the Xonotic convention: numbers then a human comment
        List<FrameGroup> groups = FrameGroups.Parse(
            "// dpm model animations\n" +
            "1115 121 10 1 // zombie idle\n" +
            "0 50 5 1 // dieback\n");
        Assert.Equal(2, groups.Count);
        Assert.Equal(1115, groups[0].FirstFrame);
        Assert.Equal(121, groups[0].FrameCount);
        Assert.Equal(10f, groups[0].Fps);
        Assert.Equal(string.Empty, groups[0].Name); // the comment is NOT a name
    }

    [Fact]
    public void IncompleteLine_IsSkipped()
    {
        // a lone first number is incomplete — DP warns and continues
        List<FrameGroup> groups = FrameGroups.Parse("42\n0 10\n");
        FrameGroup g = Assert.Single(groups);
        Assert.Equal(0, g.FirstFrame);
    }

    [Fact]
    public void FrameCountAndFps_AreLowerBounded()
    {
        // DP bound()s framecount to >= 1 and fps to >= 1
        FrameGroup zeroCount = Assert.Single(FrameGroups.Parse("5 0"));
        Assert.Equal(1, zeroCount.FrameCount);

        FrameGroup zeroFps = Assert.Single(FrameGroups.Parse("5 10 0.25"));
        Assert.Equal(1f, zeroFps.Fps);

        FrameGroup negFirst = Assert.Single(FrameGroups.Parse("-3 10"));
        Assert.Equal(0, negFirst.FirstFrame);
    }

    [Fact]
    public void NonNumericFirstToken_SkipsLine_LaterGarbageStillAtoisToZero()
    {
        // r11 correction (supersedes the old garbage->0 pin for the FIRST token): a PROSE line must not mint a
        // phantom group — the DP-generated DPM sidecars carry a prose header, and "Used by DarkPlaces to
        // simulate frame groups…" once became a clip literally named "simulate", shifting the real weapon
        // slots off their indexes.
        Assert.Empty(FrameGroups.Parse("abc 10"));
        // C atoi semantics are KEPT for later tokens: garbage -> 0 (framecount then clamps to 1), never a throw.
        FrameGroup g = Assert.Single(FrameGroups.Parse("7 abc"));
        Assert.Equal(7, g.FirstFrame);
        Assert.Equal(1, g.FrameCount);
    }

    [Fact]
    public void BlankLines_AndWhitespace_AreIgnored()
    {
        List<FrameGroup> groups = FrameGroups.Parse("\n   \n0 10\n\t\n5 20\n");
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void OrderIsPreserved()
    {
        List<FrameGroup> groups = FrameGroups.Parse("0 10\n10 20\n30 5\n");
        Assert.Equal(new[] { 0, 10, 30 }, groups.Select(g => g.FirstFrame).ToArray());
    }

    [Fact]
    public void EmptyInput_ParsesToNothing()
    {
        Assert.Empty(FrameGroups.Parse(""));
        Assert.Empty(FrameGroups.Parse("// nothing but a comment"));
    }

    [Fact]
    public void DpmGeneratedHeader_BlockCommentAndProse_AreSkipped()
    {
        // The DP-generated DPM weapon sidecars (h_rl/h_gl/h_electro/…) open with a /* */ prose header. The
        // old parser read those prose lines as data — two phantom groups whose 5th words ("h_rl"/"simulate")
        // became clip NAMES, shifting the real fire/fire2/idle/reload slots to indexes 2..5 and defeating the
        // nameless 4-slot weapon contract: no DPM viewmodel ever animated (playtest r11).
        const string text = "/*\n" +
            "Generated framegroups file for h_rl\n" +
            "Used by DarkPlaces to simulate frame groups in DPM models.\n" +
            "*/\n" +
            "\n" +
            "1 31 30 0 // h_rl fire\n" +
            "32 31 30 0 // h_rl fire\n" +
            "63 101 3 1 // h_rl idle\n" +
            "164 101 3 1 // h_rl idle\n";
        List<FrameGroup> groups = FrameGroups.Parse(text);
        Assert.Equal(4, groups.Count);
        Assert.All(groups, g => Assert.Equal(string.Empty, g.Name)); // nameless → the weapon slot contract applies
        Assert.Equal(1, groups[0].FirstFrame);
        Assert.Equal(31, groups[0].FrameCount);
        Assert.False(groups[0].Loop);
        Assert.Equal(164, groups[3].FirstFrame);
        Assert.True(groups[3].Loop);
    }

    [Fact]
    public void ProseLineOutsideBlockComment_IsSkippedNotZeroParsed()
    {
        // A stray non-numeric line must not atoi to a phantom 0/1 group.
        List<FrameGroup> groups = FrameGroups.Parse("stray prose line here\n5 10 20 1\n");
        FrameGroup g = Assert.Single(groups);
        Assert.Equal(5, g.FirstFrame);
    }

    [Fact]
    public void RealAsset_ShippedFramegroupsFile_ParsesToValidRanges()
    {
        if (!Directory.Exists(DataDir)) return; // skip-if-missing
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        string? path = vfs.Find("models/", "framegroups").FirstOrDefault();
        if (path is null) return;

        List<FrameGroup> groups = FrameGroups.Parse(vfs.ReadText(path));
        Assert.True(groups.Count >= 1, $"{path}: expected at least one frame group");
        foreach (FrameGroup g in groups)
        {
            Assert.True(g.FirstFrame >= 0);
            Assert.True(g.FrameCount >= 1);
            Assert.True(g.Fps >= 1f);
        }
    }
}
